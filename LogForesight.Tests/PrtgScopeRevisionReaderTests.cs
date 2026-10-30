using System.Security.Cryptography;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 範圍柵欄字串：<see cref="PrtgScopeRevisionReader.Read"/> 是郵件冪等鍵與快照佇列雜湊的持久鍵，格式不得變；
/// 結構／原始語意柵欄不含規則版本（範圍計算與回填都不讀規則）。
/// </summary>
public sealed class PrtgScopeRevisionReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-scope-revision-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;

    public PrtgScopeRevisionReaderTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "scope.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
    }

    private void BumpRules(int marker) =>
        _backend.Blob("rules").Mutate<object?>(_ => ("{\"marker\":" + marker + "}", null));

    private long BlobVersion(string key)
    {
        using var context = _backend.CreateContext();
        return context.Blobs.AsNoTracking().Where(row => row.BlobKey == key).Select(row => (long?)row.Version).FirstOrDefault() ?? 0;
    }

    [Fact]
    public void Read_輸出字串格式不變_含規則版本()
    {
        BumpRules(1);
        BumpRules(2);
        var rulesVersion = BlobVersion("rules");
        Assert.True(rulesVersion >= 2);

        // 改動前的格式：{範圍版本}:{政策版本}:{規則版本}:{sentinel 指紋}:{主機指紋}
        var hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("[]")));
        var expected = $"{BlobVersion(EfPrtgStore.ScopeRevisionBlobKey)}:{BlobVersion(PrtgMonitoringPolicyStore.BlobKey)}:{rulesVersion}:[]:{hostFingerprint}";

        Assert.Equal(expected, new PrtgScopeRevisionReader(_backend, _hosts).Read());
    }

    [Fact]
    public void 規則版本變動_Read改變而結構與原始語意柵欄不變()
    {
        var reader = new PrtgScopeRevisionReader(_backend, _hosts);
        BumpRules(1);
        var read = reader.Read();
        var structural = reader.ReadStructural();
        var raw = reader.ReadRawSemantic();

        BumpRules(2);

        Assert.NotEqual(read, reader.Read());
        Assert.Equal(structural, reader.ReadStructural());
        Assert.Equal(raw, reader.ReadRawSemantic());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
