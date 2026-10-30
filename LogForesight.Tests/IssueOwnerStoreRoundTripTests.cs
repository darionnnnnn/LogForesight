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

    private static MuteInterval Mute(string reason) => new()
    {
        From = DateTime.Today, To = DateTime.Today.AddDays(2), Reason = reason, ByAccount = "a"
    };

    [Fact]
    public void Update_先加靜音再設結論_兩者並存()
    {
        Store().Update("disk", 153, p => p.Mutes = new List<MuteInterval> { Mute("搬機房") });
        Store().Update("DISK", 153, p =>
        {
            p.ConclusionStatus = "wont_fix";
            p.ConclusionNote = "已知雜訊";
        });

        var reread = Store().Get("disk", 153)!;
        Assert.Single(Store().GetAll());
        Assert.Equal("wont_fix", reread.ConclusionStatus);
        Assert.Equal("已知雜訊", reread.ConclusionNote);
        Assert.Equal("搬機房", Assert.Single(reread.Mutes).Reason);
    }

    [Fact]
    public void Update_問題不存在_建立一列並設UpdatedAt()
    {
        var before = DateTime.Now.AddSeconds(-1);

        var saved = Store().Update("Ntfs", 55, p => p.Note = "新建");

        Assert.Equal("Ntfs", saved.SourceName);
        Assert.Equal(55, saved.EventId);
        Assert.Equal("新建", saved.Note);
        Assert.True(saved.UpdatedAt >= before);
        var reread = Assert.Single(Store().GetAll());
        Assert.Equal("新建", reread.Note);
    }

    [Fact]
    public void Update_回傳複本_鎖外修改不影響store()
    {
        var saved = Store().Update("disk", 153, p => p.Note = "原值");
        saved.Note = "被呼叫端改掉";

        Assert.Equal("原值", Store().Get("disk", 153)!.Note);
    }

    /// <summary>決定性交錯：舊流程先讀（stale），別人寫入靜音後，Update 只改結論欄 → 靜音仍在；
    /// 對照案以舊寫法 stale 改結論後 Upsert → 靜音被洗掉（證明舊寫法的問題）。</summary>
    [Fact]
    public void 交錯_Update只改結論欄_靜音不被還原_舊寫法Upsert會洗掉()
    {
        Store().Upsert(new IssueProfile { SourceName = "disk", EventId = 153 });
        var stale = Store().Get("disk", 153)!;                         // 舊流程的讀
        Store().Update("disk", 153, p => p.Mutes = new List<MuteInterval> { Mute("先寫者") });

        Store().Update("disk", 153, p =>
        {
            p.ConclusionStatus = "false_positive";
            p.ConclusionNote = "後寫者";
        });
        var viaUpdate = Store().Get("disk", 153)!;
        Assert.Equal("先寫者", Assert.Single(viaUpdate.Mutes).Reason);
        Assert.Equal("false_positive", viaUpdate.ConclusionStatus);

        // 對照：舊寫法（用先前讀到的 stale 改結論再整列 Upsert）
        stale.ConclusionStatus = "wont_fix";
        stale.ConclusionNote = "舊寫法";
        Store().Upsert(stale);
        Assert.Empty(Store().Get("disk", 153)!.Mutes);
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
