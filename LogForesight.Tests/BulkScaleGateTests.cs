using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次操作的規模閘門（docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 D／體檢 M10、S7、X3）。
///
/// 這些上限存在的理由不是「怕慢」，而是**畫面誠實**與**請求可完成**：
///   - 預覽逐台清單無上限時，6000 台環境按一次統一標記就把 6000 列塞進 modal；
///   - 但總數不能跟著被截斷——「我到底影響了多少」是這個操作最需要準確的數字；
///   - 單次寫入無上限時必然超過瀏覽器逾時，而逾時後使用者不知道寫進去多少。
/// </summary>
public class BulkScaleGateTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly IssueHandlingCommandService _service;

    public BulkScaleGateTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());

        _service = NewService(new FakeUserGroupStore());
    }

    /// <summary>groups 影響「被指派者動不動得了」的判定（H1），多數測試給空的即可</summary>
    private IssueHandlingCommandService NewService(FakeUserGroupStore groups)
    {
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());
        var currentUser = FakeCurrentUser.WithCapabilities(LogForesight.Web.Auth.Capability.Assign, LogForesight.Web.Auth.Capability.Handle);
        var displayNames = new UserDisplayNameService(_settingsStore);
        var issueOwnerAdmin = new IssueOwnerAdminService(
            new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(), _users, new RecordingAuditService(), currentUser, displayNames);

        return new IssueHandlingCommandService(
            _handlingStore, _issueHandlingStore, _caseStore,
            new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore()),
            new FakeNoiseMarkStore(), repository, _hosts, _users, visibility,
            currentUser,
            new RecordingAuditService(), new HandlingProgressCalculator(_issueHandlingStore, _handlingStore, _caseStore, _settingsStore),
            new LogForesight.Web.Auth.UserCapabilityResolver(groups, _hosts), issueOwnerAdmin, displayNames);
    }

    public void Dispose() => _fixture.Dispose();

    private static LogIssueSignature Issue() => new()
    {
        LogName = "System", Source = "DistributedCOM", EventId = 10016,
        Category = IssueCategory.Config, Severity = IssueSeverity.Low, Count = 3
    };

    /// <summary>造 N 台主機，每台在 days 天內都有同一個問題</summary>
    private void SeedHosts(int hostCount, int days)
    {
        for (var i = 0; i < hostCount; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"SRV-{i:D4}", Active = true });
            for (var d = 0; d < days; d++)
            {
                _recordStore.Append(new DailyAnalysisRecord
                {
                    HostId = host.HostId,
                    Host = host.HostName,
                    Date = DateTime.Today.AddDays(-d),
                    RiskLevel = RiskLevels.Medium,
                    TopIssues = new List<LogIssueSignature> { Issue() }
                });
            }
        }
    }

    [Fact]
    public void 統一標記預覽_逐台清單有上限_但總數誠實回報()
    {
        var hostCount = IssueHandlingCommandService.PreviewHostLimit + 15;
        SeedHosts(hostCount, days: 2);

        var preview = _service.PreviewBulkClose("DistributedCOM", 10016, null, null);

        Assert.Equal(IssueHandlingCommandService.PreviewHostLimit, preview.Hosts.Count);
        Assert.True(preview.Truncated);
        // 總數與總日數不得跟著被截斷——那是使用者判斷「要不要按下去」的唯一依據
        Assert.Equal(hostCount, preview.TotalHostCount);
        Assert.Equal(hostCount * 2, preview.TotalDayCount);
    }

    [Fact]
    public void 統一標記預覽_未超過上限時不標示截斷()
    {
        SeedHosts(3, days: 2);

        var preview = _service.PreviewBulkClose("DistributedCOM", 10016, null, null);

        Assert.Equal(3, preview.Hosts.Count);
        Assert.False(preview.Truncated);
        Assert.Equal(3, preview.TotalHostCount);
        Assert.Equal(6, preview.TotalDayCount);
    }

    /// <summary>
    /// 截斷時優先留下「會被寫入的」主機：略過的主機對操作決策的價值較低，
    /// 而略過數另外用 SkippedHostCount 誠實申報。
    /// </summary>
    [Fact]
    public void 統一標記預覽_略過的主機另外計數()
    {
        SeedHosts(4, days: 1);
        // 其中一台已有他人的進行中案件 → 整台略過（定案 6-1）
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\other", DisplayName = "他人", Active = true });
        _caseStore.Save(new IssueCase
        {
            CaseId = "c1", HostName = "SRV-0000", IssueKey = IssueSignatureKey.For(Issue()),
            IssueLabel = "DistributedCOM 10016", HandlerId = handler.UserId,
            Status = IssueHandlingStatuses.InProgress, CreatedAt = DateTime.Now, CreatedByAccount = "admin"
        });

        var preview = _service.PreviewBulkClose("DistributedCOM", 10016, null, null);

        Assert.Equal(4, preview.TotalHostCount);
        Assert.Equal(1, preview.SkippedHostCount);
        Assert.Equal(3, preview.TotalDayCount);   // 略過那台的天數不算進去
    }

    /// <summary>
    /// 單次寫入的硬上限（M10／S7）：統一標記是同步 HTTP，寫入量一大必然超過瀏覽器逾時，
    /// 而逾時之後使用者不知道到底寫進去多少、重按一次又是一輪。上限要**擋在寫入之前**，
    /// 而且錯誤訊息要說得出「怎麼縮小範圍」。
    /// </summary>
    [Fact]
    public void 統一標記_超過單次寫入上限時擋下並說明怎麼分批()
    {
        // 每台 1 天，台數超過上限即可觸發（不必真的造五千筆多天資料）
        SeedHosts(IssueHandlingCommandService.MaxBulkCloseDayWrites + 1, days: 1);

        var ex = Assert.Throws<DomainException>(() => _service.BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "DistributedCOM",
            EventId = 10016,
            Status = IssueHandlingStatuses.KnownNoise,
            Note = "壓測"
        }));

        Assert.Contains("超過單次上限", ex.Message);
        Assert.Contains("分次執行", ex.Message);
        // 擋下就是完全沒寫——半套結果比擋下來難收拾得多
        Assert.Empty(_issueHandlingStore.GetMany(
            new[] { "SRV-0000", "SRV-0001" }, DateTime.Today.AddDays(-1), DateTime.Today));
    }

    [Fact]
    public void 統一標記_結果帶回追溯用的問題與期間()
    {
        SeedHosts(2, days: 1);
        var from = DateTime.Today.AddDays(-7);
        var to = DateTime.Today;

        var result = _service.BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "DistributedCOM", EventId = 10016, From = from, To = to,
            Status = IssueHandlingStatuses.KnownNoise, Note = "已確認為背景雜訊"
        });

        Assert.Equal(2, result.UpdatedHostCount);
        // 追溯連結的素材（體檢 X6）：批次寫入沒有復原機制，至少要查得到影響了哪些
        Assert.Equal("DistributedCOM", result.Source);
        Assert.Equal(10016, result.EventId);
        Assert.Equal(from.ToString("yyyy-MM-dd"), result.From);
        Assert.Equal(to.ToString("yyyy-MM-dd"), result.To);
    }

    /// <summary>
    /// 體檢 H1：指派前的檢查過去只做了「他看得到這台主機嗎」，沒做「他動得了嗎」。
    /// 交辦出去的工作進了對方清單、對方按下「回覆處理狀態」必定 403，而指派的人不知情。
    /// **不擋、只提示**——把工作知會給主管是合理用法。
    /// </summary>
    [Fact]
    public void 批次指派_對方沒有處理能力時要回報但不擋()
    {
        SeedHosts(2, days: 1);
        // manager 群組只有 ViewAll，沒有 Handle
        var groups = new FakeUserGroupStore();
        var managerGroup = groups.Upsert(new UserGroup { GroupName = "manager", Role = UserRole.Manager, Active = true });
        var manager = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\mgr", DisplayName = "王主管", Active = true,
            GroupIds = new List<long> { managerGroup.GroupId }
        });

        var service = NewService(groups);
        var result = service.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "DistributedCOM",
            EventId = 10016,
            HandlerId = manager.UserId,
            HostIds = _hosts.GetAll().Select(h => h.HostId).ToList()
        });

        // 指派本身仍然成立（不擋）
        Assert.Equal(2, result.Created);
        // 但要講：一個處理人只回報一次，帶上影響的主機數
        var warning = Assert.Single(result.AssigneeCannotHandle);
        Assert.Contains("王主管", warning.HandlerName);
        Assert.Equal(2, warning.HostCount);
    }

    /// <summary>有 Handle 的人不該被誤報——負責人隱含能力（§2b）也算數</summary>
    [Fact]
    public void 批次指派_對方有處理能力時不回報()
    {
        SeedHosts(1, days: 1);
        var groups = new FakeUserGroupStore();
        var userGroup = groups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User, Active = true });
        var handler = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\chen", DisplayName = "陳工程師", Active = true,
            GroupIds = new List<long> { userGroup.GroupId }
        });

        var result = NewService(groups).BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "DistributedCOM",
            EventId = 10016,
            HandlerId = handler.UserId,
            HostIds = _hosts.GetAll().Select(h => h.HostId).ToList()
        });

        Assert.Equal(1, result.Created);
        Assert.Empty(result.AssigneeCannotHandle);
    }

    [Fact]
    public void 批次指派預覽_逐台清單有上限_但總數誠實回報()
    {
        var hostCount = IssueHandlingCommandService.PreviewHostLimit + 8;
        SeedHosts(hostCount, days: 1);

        var preview = _service.PreviewIssueCaseAssign("DistributedCOM", 10016, null, null);

        Assert.Equal(IssueHandlingCommandService.PreviewHostLimit, preview.Hosts.Count);
        Assert.True(preview.Truncated);
        Assert.Equal(hostCount, preview.TotalHostCount);
    }
}
