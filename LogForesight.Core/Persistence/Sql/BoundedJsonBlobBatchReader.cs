using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>Batch metadata lookup with complete bounded text, preserving the caller's transaction.</summary>
internal static class BoundedJsonBlobBatchReader
{
    internal static Dictionary<string,(string? Content,long Version,int Length,DateTime UpdatedAt)> Read(
        LfDbContext context,IEnumerable<string> keys,int maximumCharacters)
    {
        if (maximumCharacters is < 1 or > int.MaxValue-1)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var requested=keys.Distinct(StringComparer.Ordinal).ToArray();
        var output=new Dictionary<string,(string? Content,long Version,int Length,DateTime UpdatedAt)>(StringComparer.Ordinal);
        if (requested.Length==0) return output;
        var connection=context.Database.GetDbConnection();
        var opened=connection.State==ConnectionState.Closed;
        if (opened) connection.Open();
        try
        {
            var sqlite=connection as SqliteConnection;
            using var ownedTransaction=sqlite is not null && context.Database.CurrentTransaction is null
                ? sqlite.BeginTransaction(deferred:true) : null;
            var transaction=context.Database.CurrentTransaction?.GetDbTransaction() ?? ownedTransaction;
            Encoding? encoding=null;
            string? encodingName=null;
            if (sqlite is not null)
            {
                using var pragma=sqlite.CreateCommand();
                pragma.Transaction=(SqliteTransaction?)transaction;
                pragma.CommandText="PRAGMA encoding;";
                encodingName=(string)pragma.ExecuteScalar()!;
                encoding=encodingName switch
                {
                    "UTF-8" => new UTF8Encoding(false,true),
                    "UTF-16le" => new UnicodeEncoding(false,false,true),
                    "UTF-16be" => new UnicodeEncoding(true,false,true),
                    _ => throw new InvalidDataException("SQLite text encoding is unsupported.")
                };
            }
            // Stay below both providers' parameter limits without one SQL query per resource.
            foreach(var page in requested.Chunk(200))
            {
                using var command=connection.CreateCommand();
                command.Transaction=transaction;
                command.CommandTimeout=context.Database.GetCommandTimeout() ?? command.CommandTimeout;
                var parameters=new List<string>(page.Length);
                for(var i=0;i<page.Length;i++)
                {
                    var parameter=command.CreateParameter();
                    parameter.ParameterName="@k"+i;
                    parameter.Value=page[i];
                    command.Parameters.Add(parameter);
                    parameters.Add(parameter.ParameterName);
                }
                var selection=sqlite is not null ? "blob_key,rowid,version,typeof(content),updated_at"
                    : "blob_key,CASE WHEN DATALENGTH(content)>@bytes THEN NULL ELSE content END,version,DATALENGTH(content),updated_at";
                if(sqlite is null)
                {
                    var bytes=command.CreateParameter();
                    bytes.ParameterName="@bytes";bytes.Value=(long)maximumCharacters*2;
                    command.Parameters.Add(bytes);
                }
                command.CommandText=$"SELECT {selection} FROM lf_blobs WHERE blob_key IN ({string.Join(',',parameters)});";
                var metadata=new List<(string Key,long Rowid,long Version,bool Text,DateTime UpdatedAt)>();
                using(var reader=command.ExecuteReader())
                {
                    while(reader.Read())
                    {
                        var key=reader.GetString(0);
                        var version=Convert.ToInt64(reader.GetValue(2));
                        if(sqlite is not null)
                            metadata.Add((key,reader.GetInt64(1),version,reader.GetString(3)=="text",reader.GetDateTime(4)));
                        else
                        {
                            var content=reader.IsDBNull(1)?null:reader.GetString(1);
                            var valid=content is not null && content.Length<=maximumCharacters && !content.Contains('\0') &&
                                Convert.ToInt64(reader.GetValue(3))==(long)content.Length*2;
                            output[key]=valid ? (content,version,content!.Length,reader.GetDateTime(4)):(null,version,maximumCharacters+1,reader.GetDateTime(4));
                        }
                    }
                }
                foreach(var row in metadata)
                {
                    var text=row.Text ? EfJsonBlobStore.ReadSqlitePhysicalText(sqlite!,row.Rowid,row.Version,
                        maximumCharacters,encoding!,encodingName!) : (null,row.Version,maximumCharacters+1);
                    output[row.Key]=(text.Item1,text.Item2,text.Item3,row.UpdatedAt);
                }
            }
            return output;
        }
        finally { if(opened) connection.Close(); }
    }
}
