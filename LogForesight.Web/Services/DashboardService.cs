using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>總覽儀表板（docs/WEB-SPEC.md §9.1）</summary>
public interface IDashboardService
{
    DashboardDto GetSummary(int days);
}

public class DashboardService : IDashboardService
{
    private readonly IRecordRepository _repository;
    private readonly IVisibilityService _visibility;
    private readonly IAuditLogStore _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IHandlingService _handling;
    private readonly IPermissionChangeService _permissionChanges;
    private readonly IHostGroupStore _hostGroups;

    public DashboardService(
        IRecordRepository repository,
        IVisibilityService visibility,
        IAuditLogStore audit,
        ICurrentUser currentUser,
        IHandlingService handling,
        IPermissionChangeService permissionChanges,
        IHostGroupStore hostGroups)
    {
        _repository = repository;
        _visibility = visibility;
        _audit = audit;
        _currentUser = currentUser;
        _handling = handling;
        _permissionChanges = permissionChanges;
        _hostGroups = hostGroups;
    }

    public DashboardDto GetSummary(int days)
    {
        var from = DateTime.Today.AddDays(-days + 1);
        var records = _repository.Query(new RecordQueryFilter { From = from });
        var visibleHosts = _visibility.GetVisibleHosts();

        var dto = new DashboardDto
        {
            Days = days,
            From = from.ToString("yyyy-MM-dd"),
            To = DateTime.Today.ToString("yyyy-MM-dd"),
            TotalHosts = visibleHosts.Count
        };

        BuildCategoryCards(dto, records);
        BuildHostRanking(dto, records, visibleHosts);
        BuildSilentHosts(dto, visibleHosts);
        BuildGroupRisk(dto, records, visibleHosts);

        dto.HighRiskDays = records.Count(r => r.RiskLevel == "高");
        dto.MediumRiskDays = records.Count(r => r.RiskLevel == "中");
        dto.CoverageGapDays = records.Count(r => r.DataIncomplete || r.SecurityLogAvailable == false);

        // 主管看到「有哪些風險」後的下一句話幾乎必然是「那有人在處理嗎？」
        // 待辦母體 = 本期的高＋中風險日：低風險日不進待辦（全塞進來待辦永遠爆量，等於沒有待辦）
        dto.Todo = _handling.GetTodo(records.Where(r => r.RiskLevel is "高" or "中").ToList());
        dto.PendingPermissionChanges = _permissionChanges.CountPending();

        // 登入失敗卡只給看得到稽核的人（admin）——一般使用者看到這個數字沒有意義，
        // 反而洩漏了系統遭受嘗試的程度
        if (_currentUser.Has(Capability.ViewAudit))
        {
            dto.RecentLoginFailures = _audit.Count(DateTime.Now.AddHours(-24), DateTime.Now, AuditActions.LoginFailed);
        }

        return dto;
    }

    private static void BuildCategoryCards(DashboardDto dto, List<DailyAnalysisRecord> records)
    {
        // 逐日彙總後合併：CategoryAggregator 是兩個儲存後端共用的同一份規則（§10.3），
        // 儀表板與明細頁因此不可能算出不同的數字
        var perDay = records.SelectMany(r => CategoryAggregator.Aggregate(r.TopIssues));
        var merged = CategoryAggregator.Merge(perDay);

        var hostsPerCategory = records
            .SelectMany(r => r.TopIssues.Select(i => new { i.Category, r.Host }))
            .GroupBy(x => x.Category)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        dto.Categories = merged.Select(c => new DashboardCategoryDto
        {
            Category = c.Category.ToString(),
            IssueCount = c.IssueCount,
            TotalEvents = c.TotalEvents,
            MaxSeverity = c.MaxSeverity.ToString(),
            CriticalCount = c.CriticalCount,
            HighCount = c.HighCount,
            MediumCount = c.MediumCount,
            LowCount = c.LowCount,
            AffectedHosts = hostsPerCategory.TryGetValue(c.Category, out var count) ? count : 0
        }).ToList();
    }

    private static void BuildHostRanking(
        DashboardDto dto, List<DailyAnalysisRecord> records, List<WebHost> visibleHosts)
    {
        var hostsByName = visibleHosts.ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        dto.HostRanking = records
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardHostDto
            {
                HostId = hostsByName.TryGetValue(group.Key, out var host) ? host.HostId : 0,
                HostName = group.Key,
                HighRiskDays = group.Count(r => r.RiskLevel == "高"),
                MediumRiskDays = group.Count(r => r.RiskLevel == "中"),
                CorrelationDays = group.Count(r => r.CorrelationAlerts.Count > 0),
                LatestRiskLevel = group.OrderByDescending(r => r.Date).First().RiskLevel,
                LatestHeadline = group.OrderByDescending(r => r.Date).First().Headline
            })
            .Where(h => h.HighRiskDays > 0 || h.MediumRiskDays > 0)
            // 緊急程度：高風險日數 → 有關聯訊號的日數 → 中風險日數（§DB-PLAN E 節）
            .OrderByDescending(h => h.HighRiskDays)
            .ThenByDescending(h => h.CorrelationDays)
            .ThenByDescending(h => h.MediumRiskDays)
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// 無回報主機：批次沒跑就不會有任何風險紀錄。
    /// 「這台很安靜」與「這台根本沒在看」在畫面上必須分得出來——
    /// 這是 README「沒告警 ≠ 沒問題」在主機層級的版本。
    ///
    /// §5.4 D-4：只算數量，不逐台列出——兩千台規模下這份清單本身可能就有數百筆，
    /// 逐台渲染會把儀表板撐爆。點計數卡改導向主機頁的「未回報」篩選（Hosts.SilentThreshold）。
    ///
    /// **新主機豁免**（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 9）：LastReportAt 為 null 的主機
    /// 只在建立超過 <see cref="HostAdminService.NewHostGracePeriod"/>（與該處保持一致，
    /// 兩邊數字才不會對不上）才算無回報——剛匯入的主機第一次批次還沒跑完，
    /// LastReportAt 必然是空的，不豁免的話整批匯入就會立刻觸發告警洪水。
    /// 已經回報過至少一次（LastReportAt 有值）的主機不受豁免影響，維持原本的 2 天判定。
    /// </summary>
    private static void BuildSilentHosts(DashboardDto dto, List<WebHost> visibleHosts)
    {
        var cutoff = DateTime.Now.AddDays(-2);
        var graceCutoff = DateTime.Now - HostAdminService.NewHostGracePeriod;

        dto.SilentHostsCount = visibleHosts
            .Count(h => h.Active &&
                        (h.LastReportAt == null ? h.CreatedAt < graceCutoff : h.LastReportAt < cutoff));
    }

    /// <summary>
    /// 依主機群組的風險概況（§5.4 D-4）：兩千台規模下「先看部門、再下鑽個別主機」是主要動線，
    /// 比一次攤開兩千台實際得多。只列出「至少有一台可見主機」的群組。
    /// </summary>
    private void BuildGroupRisk(DashboardDto dto, List<DailyAnalysisRecord> records, List<WebHost> visibleHosts)
    {
        var recordsByHost = records
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        dto.GroupRisk = _hostGroups.GetAll()
            .Where(g => g.Active)
            .Select(group =>
            {
                var memberHosts = visibleHosts.Where(h => h.Active && h.GroupIds.Contains(group.GroupId)).ToList();
                var memberRecords = memberHosts
                    .SelectMany(h => recordsByHost.TryGetValue(h.HostName, out var recs) ? recs : new List<DailyAnalysisRecord>())
                    .ToList();

                // 待辦母體與儀表板整體待辦同一套規則：只算高／中風險日，低風險日不進待辦
                var todo = _handling.GetTodo(memberRecords.Where(r => r.RiskLevel is "高" or "中").ToList());

                return new DashboardGroupRiskDto
                {
                    GroupId = group.GroupId,
                    GroupName = group.GroupName,
                    HostCount = memberHosts.Count,
                    HighRiskDays = memberRecords.Count(r => r.RiskLevel == "高"),
                    MediumRiskDays = memberRecords.Count(r => r.RiskLevel == "中"),
                    UnhandledCount = todo.OpenCount + todo.InProgressCount
                };
            })
            .Where(g => g.HostCount > 0)
            .OrderByDescending(g => g.HighRiskDays)
            .ThenByDescending(g => g.MediumRiskDays)
            .ToList();
    }
}
