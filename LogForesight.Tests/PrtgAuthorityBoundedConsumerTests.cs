using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgAuthorityBoundedConsumerTests : IDisposable
{
    private readonly EfSqliteFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void EfPrtgBatchConsumerReadsExactUtf16BoundaryAndRetainsVersionMetadata()
    {
        var blob = fixture.Blob("authority-exact");
        blob.Mutate(_ => ("abcdefghijklmnop", true));
        fixture.Blob("authority-exact-unicode").Mutate(_ => ("😀😀😀😀", true));

        using var context = fixture.NewContext();
        using var transaction = context.Database.BeginTransaction();
        var rows = EfPrtgStore.ReadBoundedBlobs(context, ["authority-exact", "authority-exact-unicode"],
            maximumCharacters: 16, maximumUtf8Bytes: 16, reason: "test-cap");
        var row = rows["authority-exact"];

        Assert.Equal("abcdefghijklmnop", row.Content);
        Assert.Equal(1L, row.Version);
        Assert.NotEqual(default, row.UpdatedAt);
        Assert.Equal("😀😀😀😀", rows["authority-exact-unicode"].Content);
        Assert.Equal(8, rows["authority-exact-unicode"].Length);
        Assert.Equal(16, System.Text.Encoding.UTF8.GetByteCount(rows["authority-exact-unicode"].Content!));
        Assert.Same(transaction, context.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("abcdefghijklmnopq", 17, 17)]
    [InlineData("😀😀😀😀中", 9, 19)]
    [InlineData("safe\0foreign", 12, 12)]
    public void EfPrtgBatchConsumerRejectsOverLimitUtf8OrNulWithoutAcceptingPrefix(
        string content, int utf16Length, int utf8Length)
    {
        Assert.Equal(utf16Length, content.Length);
        Assert.Equal(utf8Length, System.Text.Encoding.UTF8.GetByteCount(content));
        fixture.Blob("authority-invalid").Mutate(_ => ("{}", true));
        var bytes = Encoding.UTF8.GetBytes(content);
        using (var seed = fixture.NewContext())
        {
            const string key = "authority-invalid";
            seed.Database.ExecuteSqlInterpolated($"UPDATE lf_blobs SET content=CAST({bytes} AS TEXT) WHERE blob_key={key}");
        }

        using var context = fixture.NewContext();
        using var transaction = context.Database.BeginTransaction();
        Assert.Throws<InvalidDataException>(() => EfPrtgStore.ReadBoundedBlobs(context,
            ["authority-invalid"], maximumCharacters: 16, maximumUtf8Bytes: 16, reason: "test-cap"));
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal(content, fixture.Blob("authority-invalid").Read());
    }
}
