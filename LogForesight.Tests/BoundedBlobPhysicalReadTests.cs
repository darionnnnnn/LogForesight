using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class BoundedBlobPhysicalReadTests : IDisposable
{
    private readonly EfSqliteFixture fixture=new();
    public void Dispose() => fixture.Dispose();

    [Theory]
    [InlineData("abcdefghijklmnop",true)]
    [InlineData("abcdefghijklmno ",true)]
    [InlineData("中中中中中中中中中中中中中中中中",true)]
    [InlineData("😀😀😀😀😀😀😀😀",true)]
    [InlineData("abcdefghijklmnopq",false)]
    [InlineData("😀😀😀😀😀😀😀😀x",false)]
    [InlineData("{}\0foreign",false)]
    public void SmallUtf16BoundaryRejectsIncompleteOrNullContainingText(string raw,bool accepted)
    {
        var blob=fixture.Blob("bounded-physical");
        blob.Mutate(_=>(raw,true));
        var result=blob.ReadBoundedWithVersion(16);
        Assert.Equal(1,result.Version);
        if (accepted) { Assert.Equal(raw,result.Prefix); Assert.Equal(raw.Length,result.ReportedLength); }
        else { Assert.Null(result.Prefix); Assert.True(result.ReportedLength>16); }
        Assert.Equal(raw,blob.Read());
        using var context=fixture.NewContext();
        using var transaction=context.Database.BeginTransaction();
        Assert.Equal(result,EfJsonBlobStore.ReadBoundedInContext(context,"bounded-physical",16));
    }

    [Fact]
    public void PreReadFenceRunsBeforeContentAndSharesRollbackTransaction()
    {
        var blob=fixture.Blob("bounded-fenced");
        blob.Mutate(_ => ("old",true));
        var mutated=false;
        Assert.Throws<InvalidDataException>(() => blob.MutateWithContext((_,raw) =>
        {
            mutated=true;
            return ("new",true);
        },maxCurrentCharacters:16,beforeRead:ctx =>
        {
            Assert.NotNull(ctx.Database.CurrentTransaction);
            ctx.Database.ExecuteSqlRaw("UPDATE lf_blobs SET content = {0}, version = version + 1 WHERE blob_key = {1}",
                "abcdefghijklmnopq","bounded-fenced");
        }));
        Assert.False(mutated);
        Assert.Equal("old",blob.Read());
        Assert.Equal(1,blob.ReadBoundedWithVersion(16).Version);
    }
}
