using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>總覽儀表板（docs/WEB-SPEC.md §9.1）</summary>
public class DashboardService
{
    private readonly IRecordRepository _repository;
    private readonly IVisibilityService _visibility;
    private readonly JsonAuditLogStore _audit;
    private readonly ICurrentUser _currentUser;
    private readonly HandlingService _handling;
    private readonly PermissionChangeService _permissionChanges;
    private readonly IHostGroupStore _hostGroups;

    public DashboardService(
        IRecordRepository repository,
        IVisibilityService visibility,
        JsonAuditLogStore audit,
        ICurrentUser currentUser,
        HandlingService handling,
        PermissionChangeService permissionChanges,
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

        dto.Categories = RecordStatsBuilder.BuildCategoryCards(records);
        dto.HostRanking = RecordStatsBuilder
            .BuildHostRanking(records, visibleHosts.ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        BuildSilentHosts(dto, visibleHosts);
        BuildGroupRisk(dto, records, visibleHosts);

        dto.HighRiskDays = records.Count(r => r.RiskLevel == RiskLevels.High);
        dto.MediumRiskDays = records.Count(r => r.RiskLevel == RiskLevels.Medium);
        dto.CoverageGapDays = records.Count(r => r.HasCoverageGap);

        // 待辦母體（高＋中風險日）由 GetTodo 內部強制套用，呼叫端傳整批紀錄即可
        dto.Todo = _handling.GetTodo(records);
        dto.PendingPermissionChanges = _permissionChanges.CountPending();

        // 登入失敗卡只給看得到稽核的人（admin）——一般使用者看到這個數字沒有意義，
        // 反而洩漏了系統遭受嘗試的程度
        if (_currentUser.Has(Capability.ViewAudit))
        {
            dto.RecentLoginFailures = _audit.Count(DateTime.Now.AddHours(-24), DateTime.Now, AuditActions.LoginFailed);
        }

        return dto;
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

                // 待辦母體（高／中風險日）由 GetTodo 內部強制套用
                var todo = _handling.GetTodo(memberRecords);

                return new DashboardGroupRiskDto
                {
                    GroupId = group.GroupId,
                    GroupName = group.GroupName,
                    HostCount = memberHosts.Count,
                    HighRiskDays = memberRecords.Count(r => r.RiskLevel == RiskLevels.High),
                    MediumRiskDays = memberRecords.Count(r => r.RiskLevel == RiskLevels.Medium),
                    UnhandledCount = todo.OpenCount + todo.InProgressCount
                };
            })
            .Where(g => g.HostCount > 0)
            .OrderByDescending(g => g.HighRiskDays)
            .ThenByDescending(g => g.MediumRiskDays)
            .ToList();
    }
}
