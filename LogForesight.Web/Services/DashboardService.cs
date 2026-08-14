using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>總覽儀表板（docs/WEB-SPEC.md §9.1）</summary>
public class DashboardService
{
    private readonly IRecordRepository _repository;
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

    public DashboardService(
        IRecordRepository repository,
        IVisibilityService visibility,
        AuditLogStore audit,
        ICurrentUser currentUser,
        HandlingHistoryQueryService handling,
        PermissionChangeService permissionChanges,
        IHostGroupStore hostGroups,
        IssueRankingBuilder issueRanking,
        ISystemSettingsStore settings,
        IIssueAggregateQuery aggregates,
        IssueTodoQuery issueTodo)
    {
        _repository = repository;
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
    }

    public DashboardDto GetSummary(int days)
    {
        // 期間錨點＝昨天，不是今天（回饋十九輪批次C）：分析永遠只產到昨天
        // （AnalysisOrchestrator 固定分析 yesterday），錨在真實今天會讓「今天」按鈕查詢
        // 一個必然是空的區間，還觸發「本期無風險訊號」綠橫幅——把「沒資料」講成「沒事」。
        var anchor = DateTime.Today.AddDays(-1);
        var from = anchor.AddDays(-days + 1);
        // 全面 SQL 化（回饋十九輪批次E4）：主機排行／群組風險／風險日計數／涵蓋缺口計數／
        // GetTodo 都只需要判定用欄位（風險等級、關聯訊號有無、TopIssues、Headline），
        // 不需要整份分析內容——QueryLightweight（批次E3 建的同一套機制）避免可見主機數 × 天數
        // 這個量級（兩千台×30天約 6 萬筆）逐一反序列化整份 ContentJson
        var records = _repository.QueryLightweight(new RecordQueryFilter { From = from });
        var visibleHosts = _visibility.GetVisibleHosts();

        var dto = new DashboardDto
        {
            Days = days,
            From = from.ToString("yyyy-MM-dd"),
            To = anchor.ToString("yyyy-MM-dd"),
            TotalHosts = visibleHosts.Count
        };

        var visibleHostIds = visibleHosts.Select(h => h.HostId).ToList();

        // 風險類型卡（回饋十九輪批次D）：SQL 端聚合，與報表共用同一個查詢方法，
        // 「大數字（去重）／小字（累計）」兩個口徑不可能在兩頁漂移成不同的數字
        dto.Categories = RecordStatsBuilder.BuildCategoryCards(
            _aggregates.AggregateByCategory(from, anchor, visibleHostIds, _settings.Get().ParseUnhandledSeverities()));

        dto.HostRanking = RecordStatsBuilder
            .BuildHostRanking(records, visibleHosts.ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase))
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
        var actionableResolved = _issueTodo.ResolveActionable(from, anchor, visibleHostIds);
        BuildGroupRisk(dto, records, visibleHosts, actionableResolved);

        dto.HighRiskDays = records.Count(r => r.RiskLevel == RiskLevels.High);
        dto.MediumRiskDays = records.Count(r => r.RiskLevel == RiskLevels.Medium);
        dto.CoverageGapDays = records.Count(r => r.HasCoverageGap);

        // 待辦母體（高＋中風險日）由 GetTodo 內部強制套用，呼叫端傳整批紀錄即可
        dto.Todo = _handling.GetTodo(records);
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
        DashboardDto dto, List<DailyAnalysisRecord> records, List<WebHost> visibleHosts,
        List<ResolvedOccurrence> actionableResolved)
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
                    HighRiskDays = memberRecords.Count(r => r.RiskLevel == RiskLevels.High),
                    MediumRiskDays = memberRecords.Count(r => r.RiskLevel == RiskLevels.Medium),
                    UnhandledCount = groupIssueTodo.OpenIssueCount + groupIssueTodo.InProgressIssueCount
                };
            })
            .Where(g => g.HostCount > 0)
            .OrderByDescending(g => g.HighRiskDays)
            .ThenByDescending(g => g.MediumRiskDays)
            .ToList();
    }
}
