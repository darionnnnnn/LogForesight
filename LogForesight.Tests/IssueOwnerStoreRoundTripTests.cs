using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IssueOwnerStore"/> 的欄位往返（對**真實** store，不是替身，回饋十八輪批次F）。
///
/// 同 SentinelStoreRoundTripTests 的理由：Upsert 更新既有規則是逐欄複製，新增模型欄位
/// 忘了在那裡加一行，症狀是「新增存得進去、編輯被靜默還原」。比對整個物件，
/// 新增欄位時若沒改 Upsert，這組測試會直接失敗。
/// </summary>
public class IssueOwnerStoreRoundTripTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private IssueOwnerStore Store() => new(_fixture.Blob("issue_owners"));

    private static IssueProfile FullyPopulated() => new()
    {
        SourceName = "disk",
        EventId = 153,
        OwnerUserIds = new List<long> { 1, 2 },
        Note = "機房 A 硬體問題",
        UpdatedByAccount = "DOMAIN\\admin"
    };

    [Fact]
    public void 新增_全部欄位都存得進去()
    {
        Store().Upsert(FullyPopulated());

        var reread = Store().Get("disk", 153);
        Assert.NotNull(reread);
        AssertAllFieldsMatch(FullyPopulated(), reread!);
    }

    [Fact]
    public void Get比對Source不分大小寫()
    {
        Store().Upsert(FullyPopulated());

        Assert.NotNull(Store().Get("DISK", 153));
        Assert.NotNull(Store().Get("Disk", 153));
    }

    /// <summary>更新走逐欄複製的分支，漏抄一欄就會靜默還原成舊值。</summary>
    [Fact]
    public void 更新_全部欄位都跟著改_沒有欄位被靜默還原()
    {
        Store().Upsert(new IssueProfile
        {
            SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { 9 }, Note = "舊備註"
        });

        Store().Upsert(FullyPopulated());

        var reread = Store().Get("disk", 153);
        Assert.NotNull(reread);
        AssertAllFieldsMatch(FullyPopulated(), reread!);
    }

    [Fact]
    public void 更新_同SourceEventId視為同一筆而非新增()
    {
        Store().Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { 1 } });
        Store().Upsert(new IssueProfile { SourceName = "DISK", EventId = 153, OwnerUserIds = new List<long> { 2 } });

        Assert.Single(Store().GetAll());
        Assert.Equal(new List<long> { 2 }, Store().Get("disk", 153)!.OwnerUserIds);
    }

    [Fact]
    public void Delete_依SourceEventId不分大小寫移除()
    {
        Store().Upsert(FullyPopulated());

        Store().Delete("DISK", 153);

        Assert.Null(Store().Get("disk", 153));
        Assert.Empty(Store().GetAll());
    }

    /// <summary>逐欄比對。新增模型欄位時這裡也要加一行——加了才會逼出 Upsert 的漏抄。</summary>
    private static void AssertAllFieldsMatch(IssueProfile expected, IssueProfile actual)
    {
        Assert.Equal(expected.SourceName, actual.SourceName);
        Assert.Equal(expected.EventId, actual.EventId);
        Assert.Equal(expected.OwnerUserIds, actual.OwnerUserIds);
        Assert.Equal(expected.Note, actual.Note);
        Assert.Equal(expected.UpdatedByAccount, actual.UpdatedByAccount);
    }
}
