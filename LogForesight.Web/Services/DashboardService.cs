using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>總覽儀表板（docs/WEB-SPEC.md §9.1）</summary>
public class DashboardService
{
    private readonly IVisibilityService _visibility;
    private readonly AuditLogStore _audit;
    private readonly ICurrentUser _currentUser;
    private readonly HandlingHistoryQueryService _handling;
    private readonly PermissionChangeService _permissionChanges;
    private readonly IHostGroupStore _hostGroups;
    private readonly IssueRankingBuilder _issueRanking;
    private readonly ISystemSettingsStore _settings;
    private readonly IIssueAggregateQuery _aggregates;
    private readonly IssueTodoQuery _issueTodo;
    private readonly ISystemSettingsService _systemSettings;

    public DashboardService(
        IVisibilityService visibility,
        AuditLogStore audit,
        ICurrentUser currentUser,
        HandlingHistoryQueryService handling,
        PermissionChangeService permissionChanges,
        IHostGroupStore hostGroups,
        IssueRankingBuilder issueRanking,
        ISystemSettingsStore settings,
        IIssueAggregateQuery aggregates,
        IssueTodoQuery issueTodo,
        ISystemSettingsService systemSettings)
    {
        _visibility = visibility;
        _audit = audit;
        _currentUser = currentUser;
        _handling = handling;
        _permissionChanges = permissionChanges;
        _hostGroups = hostGroups;
        _issueRanking = issueRanking;
        _settings = settings;
        _aggregates = aggregates;
        _issueTodo = issueTodo;
        _systemSettings = systemSettings;
    }

    public DashboardDto GetSummary(int days)
    {
        // 期間錨點＝昨天，不是今天（回饋十九輪批次C）：分析永遠只產到昨天
        // （AnalysisOrchestrator 固定分析 yesterday），錨在真實今天會讓「今天」按鈕查詢
        // 一個必然是空的區間，還觸發「本期無風險訊號」綠橫幅——把「沒資料」講成「沒事」。
        var anchor = DateTime.Today.AddDays(-1);
        var from = anchor.AddDays(-days + 1);
        var visibleHosts = _visibility.GetVisibleHosts();
        var visibleHostIds = visibleHosts.Select(h => h.HostId).ToList();

        var dto = new DashboardDto
        {
            Days = days,
            From = from.ToString("yyyy-MM-dd"),
            To = anchor.ToString("yyyy-MM-dd"),
            TotalHosts = visibleHosts.Count
        };

        // 全部走 SQL 端聚合（SCALE-3000 S4），不再把整段期間的紀錄載入記憶體。
        // 繞過 RecordRepository 之後，它套的三層可見範圍要在這裡自己等價重現：
        // 可見主機以 hostIds 傳入；日風險等級顯示範圍由 ResolveDayRiskLevels 算出，
        // **空交集必須在這裡短路、不可下推**（底層對空集合的語意是「不限制」，會從零筆變全部）；
        // 可見嚴重度傳給有問題子列參與的聚合。
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();
        var riskLevels = RecordRepository.ResolveDayRiskLevels(_systemSettings.GetVisibleDayRiskLevels(), null);
        var visibleSeverities = RecordRepository.ParseVisibleSeverities(_systemSettings.GetVisibleSeverities());
        var nothingVisible = riskLevels != null && riskLevels.Count == 0;

        var hostRiskAgg = nothingVisible
            ? new List<HostRiskAggregate>()
            : _aggregates.AggregateByHost(from, anchor, visibleHostIds, riskLevels: riskLevels, visibleSeverities: visibleSeverities);

        // 風險類型卡（回饋十九輪批次D）：SQL 端聚合，與報表共用同一個查詢方法，
        // 「大數字（去重）／小字（累計）」兩個口徑不可能在兩頁漂移成不同的數字
        dto.Categories = RecordStatsBuilder.BuildCategoryCards(
            _aggregates.AggregateByCategory(from, anchor, visibleHostIds, unhandledSeverities));

        dto.HostRanking = RecordStatsBuilder
            .BuildHostRanking(hostRiskAgg, visibleHosts.ToDictionary(h => h.HostId))
            .Take(10)
            .ToList();
        // 重點問題（§8-1）：五筆足以回答「現在最該處理哪幾個問題」，再多就變成第二張清單頁。
        //
        // 自 P4 起走 SQL 端聚合（docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 C）而不是對 records
        // 在記憶體 GroupBy——6000 台 × 7 天約 4.2 萬筆紀錄、數十萬個問題物件，
        // 每次載入都重算一遍。順帶取得「時間形狀」五個訊號（§10.3），
        // 那是「今天有什麼不一樣」的唯一來源。
        var ranked = _issueRanking.Build(from, anchor, visibleHostIds, visibleHosts.Count);
        var (openIssues, concludedCount) = IssueRankingBuilder.ExcludeConcluded(ranked);
        dto.TopIssues = openIssues.Take(5).ToList();
        dto.ConcludedTopIssueCount = concludedCount;

        // 背景整理中時數字會偏低但看起來正常——必須說出來（G2）
        (dto.IssueStatsPending, dto.IssueStatsPendingHint) = _issueRanking.StatsPending();
        BuildSilentHosts(dto, visibleHosts);

        // 可行動快照對全站可見範圍**只解析一次**（回饋十九輪批次I 體檢修正）：KPI 卡與逐群組的
        // UnhandledCount 都從同一份解析結果各自彙總——原本逐群組各自呼叫 IssueTodoQuery.Build，
        // 每個群組都是一趟 SQL＋三次批次載入，群組數不設上限時就是 4×N 次查詢的 N+1
        var actionableResolved = nothingVisible
            ? new List<ResolvedOccurrence>()
            : _issueTodo.ResolveActionable(from, anchor, visibleHostIds, riskLevels);
        BuildGroupRisk(dto, hostRiskAgg, visibleHosts, actionableResolved);

        // 全站三個日數從同一個 KPI 聚合取（與報表同一支查詢）。刻意**不**用 hostRiskAgg 加總——
        // 讓「群組加總 == 全站總數」那條測試真的在比對兩支不同查詢，不是同一份資料自己加自己
        if (nothingVisible)
        {
            dto.HighRiskDays = 0;
            dto.MediumRiskDays = 0;
            dto.CoverageGapDays = 0;
            dto.Todo = new HandlingTodoDto();
        }
        else
        {
            var kpi = _aggregates.AggregateReportKpi(from, anchor, visibleHostIds, riskLevels, visibleSeverities);
            dto.HighRiskDays = kpi.HighRiskDays;
            dto.MediumRiskDays = kpi.MediumRiskDays;
            dto.CoverageGapDays = kpi.CoverageGapDays;

            // 待辦走 GetTodoByRange：它負責墓碑主機的兩段相加（SQL 經 host_name 橋接對合併主機
            // 必然落空，那些主機另行以記憶體精算），母體（高＋中風險日）也由它內部強制套用。
            // 不要直接呼叫 AggregateDayTodo——那會漏掉墓碑、也漏掉 TotalCount
            dto.Todo = _handling.GetTodoByRange(from, anchor, riskLevels);
        }
        // 待辦的問題口徑（回饋十九輪批次D2）：KPI 卡的主要數字，Todo 只供「未處理風險日 M」副標
        dto.IssueTodo = IssueTodoQuery.Aggregate(actionableResolved);
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
    /// **新主機豁免**（docs/archive/HISTORY.md 定案 9）：LastReportAt 為 null 的主機
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
    private void BuildGroupRisk(
        DashboardDto dto, IReadOnlyList<HostRiskAggregate> hostRiskAgg, List<WebHost> visibleHosts,
        List<ResolvedOccurrence> actionableResolved)
    {
        var aggByHost = hostRiskAgg.ToDictionary(h => h.HostId);

        dto.GroupRisk = _hostGroups.GetAll()
            .Where(g => g.Active)
            .Select(group =>
            {
                var memberHosts = visibleHosts.Where(h => h.Active && h.GroupIds.Contains(group.GroupId)).ToList();
                var memberAgg = memberHosts
                    .Select(h => aggByHost.TryGetValue(h.HostId, out var agg) ? agg : null)
                    .Where(a => a != null)
                    .Select(a => a!)
                    .ToList();

                // UnhandledCount 改問題口徑（回饋十九輪批次D2）：群組內未處理／處理中的問題數，
                // 不再是風險日數——與儀表板 KPI 卡同一套定義，只是把母體縮到這個群組的主機。
                // 從呼叫端已解析好的全站快照做記憶體子集彙總（回饋十九輪批次I 體檢修正），
                // 不逐群組重新查詢——群組成員 ⊆ 可見範圍，子集過濾與逐群組查詢結果等價
                var memberIds = memberHosts.Select(h => h.HostId).ToHashSet();
                var groupIssueTodo = IssueTodoQuery.Aggregate(
                    actionableResolved.Where(r => memberIds.Contains(r.Occurrence.HostId)));

                return new DashboardGroupRiskDto
                {
                    GroupId = group.GroupId,
                    GroupName = group.GroupName,
                    HostCount = memberHosts.Count,
                    HighRiskDays = memberAgg.Sum(a => a.HighRiskDays),
                    MediumRiskDays = memberAgg.Sum(a => a.MediumRiskDays),
                    UnhandledCount = groupIssueTodo.OpenIssueCount + groupIssueTodo.InProgressIssueCount
                };
            })
            .Where(g => g.HostCount > 0)
            .OrderByDescending(g => g.HighRiskDays)
            .ThenByDescending(g => g.MediumRiskDays)
            .ToList();
    }
}
