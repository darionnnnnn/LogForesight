using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 風險日詳情與主機詳情（docs/WEB-SPEC.md §9.3、§9.4）。清單視角見
/// <see cref="RecordListQueryService"/>。
/// </summary>
public class RecordDetailQueryService
{
    private readonly IRecordRepository _repository;
    private readonly IReportReader _reports;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IHostGroupStore _hostGroups;
    private readonly IVisibilityService _visibility;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IIssueCaseStore _cases;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly IKnownIssueRuleStore _rules;
    private readonly ICurrentUser _currentUser;
    private readonly ISystemSettingsStore _settings;

    public RecordDetailQueryService(
        IRecordRepository repository,
        IReportReader reports,
        IHostStore hosts,
        IUserStore users,
        IHostGroupStore hostGroups,
        IVisibilityService visibility,
        IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases,
        INoiseMarkStore noiseMarks,
        IKnownIssueRuleStore rules,
        ICurrentUser currentUser,
        ISystemSettingsStore settings)
    {
        _repository = repository;
        _reports = reports;
        _hosts = hosts;
        _users = users;
        _hostGroups = hostGroups;
        _visibility = visibility;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _noiseMarks = noiseMarks;
        _rules = rules;
        _currentUser = currentUser;
        _settings = settings;
    }

    public RecordDetailDto GetDetail(long hostId, DateTime date)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var host = _hosts.Get(hostId);

        // 規則命中問題的處置參考來自當前 rules.json（反映 Web 上的規則編輯），
        // 一次載入建索引，逐列查是記憶體查表。單日詳情低頻，一次檔案讀取可忽略
        var guidance = LoadGuidanceLookup();

        // 問題層級處理狀態（方案 B）：以現行主機名稱為鍵取當日已標記的問題。
        // 存整筆 IssueHandling（不只是 Status 字串）——批次套用改版後還需要 DueDate
        var hostName = host?.HostName ?? record.Host;
        var issueHandlingByKey = _issueHandlings
            .GetForDay(hostName, date)
            .GroupBy(h => h.IssueKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 已知雜訊記憶（跨日、以主機＋簽章為鍵，見 §5.1 D-1 #3）：未標記的問題若命中記憶，
        // 前端自動顯示「已知雜訊（自動）」，不必使用者每天重標
        var noiseMarks = _noiseMarks.GetForHost(hostName)
            .ToDictionary(m => m.IssueKey, StringComparer.Ordinal);

        // 問題案件（docs/archive/FEEDBACK-4-PLAN.md §2）：進行中案件涵蓋的問題顯示「○○○ 處理中
        // （1/10 起）」，解釋為什麼某些問題的狀態會被案件同步「自己動」
        var openCases = _cases.GetOpenForHost(hostName)
            .ToDictionary(
                c => c.IssueKey,
                c =>
                {
                    // 帳號與顯示名稱一起帶出（docs/archive/FEEDBACK-10-PLAN.md §6）：徽章要組成
                    // 「顯示名稱(帳號)」，同一次 Get 拿齊，不為了帳號再查一次
                    var handler = c.HandlerId.HasValue ? _users.Get(c.HandlerId.Value) : null;
                    return (HandlerId: c.HandlerId, HandlerName: handler?.DisplayName, HandlerAccount: handler?.Account,
                            c.Status, FirstLinkedDate: c.FirstLinkedDate.ToString("yyyy-MM-dd"));
                },
                StringComparer.Ordinal);

        // 先前處理過（docs/archive/FEEDBACK-5-PLAN.md §4）：本主機更早日期有結案類逐日標記，
        // 或有已結案案件，任一命中即視為「這個問題之前處理過」——只算旗標，內容留給
        // GetIssueHistory 端點按需查詢，避免詳情頁一次把全部問題的歷史都撈回來
        var priorClosedIssueKeys = _issueHandlings
            .GetMany(new[] { hostName }, DateTime.MinValue, date.Date.AddDays(-1))
            .Where(h => IssueHandlingStatuses.IsClosed(h.Status))
            .Select(h => h.IssueKey)
            .ToHashSet(StringComparer.Ordinal);
        priorClosedIssueKeys.UnionWith(
            _cases.GetMany(new[] { hostName })
                .Where(c => c.ClosedAt != null)
                .Select(c => c.IssueKey));

        var settings = _settings.Get();
        var unhandledSeverities = settings.ParseUnhandledSeverities();

        // 嚴重度可見性已由 RecordRepository.GetOne 強制套用（S1）：record.TopIssues 已是
        // SiteHidden 模式下的可見子集，這裡不必也不該重覆判斷 SeverityDisplayMode
        var visibleTopIssues = record.TopIssues;

        // 案件授與的裁剪（docs/archive/FEEDBACK-10-PLAN.md §7）：這台主機不在使用者的授權範圍內，
        // 只因為他是某些問題的案件處理人才進得來——那就只給他那些問題。
        // 整日敘事（白話總覽四段、關聯訊號、深入分析、報告全文）一律不給：那些是「整台主機
        // 這一天發生什麼事」的內容，交辦一個問題不等於交出整天的狀況。
        var grantedKeys = _visibility.GetIssueKeyRestriction(hostId);
        var caseGrantOnly = grantedKeys != null;
        var reportHost = new HostKey { HostId = record.HostId, HostName = record.Host };
        if (grantedKeys != null)
        {
            visibleTopIssues = visibleTopIssues
                .Where(i => grantedKeys.Contains(IssueSignatureKey.For(i)))
                .ToList();
        }

        return new RecordDetailDto
        {
            HostId = hostId,
            HostName = host?.HostName ?? record.Host,
            HostDisplayName = host?.DisplayName,
            HostIpAddress = host?.IpAddress,
            HostOs = host?.Os ?? "windows",
            HostRoleDesc = host?.RoleDesc ?? "",
            Date = record.Date.ToString("yyyy-MM-dd"),
            RiskLevel = record.RiskLevel,
            RiskBasisText = FormatRiskBasis(record.RiskBasis),
            HiddenIssueCount = record.HiddenIssueCount,
            // 以下整日敘事在案件授與模式下一律清空（§7）——見上方裁剪處的說明
            Headline = caseGrantOnly ? "" : record.Headline,
            Summary = caseGrantOnly ? "" : record.Summary,
            TrendAssessment = caseGrantOnly ? "" : record.TrendAssessment,
            Action = caseGrantOnly ? "" : record.Action,
            AiAnalyzed = !caseGrantOnly && record.AiAnalyzed,
            AiPending = !caseGrantOnly && record.AiPending,
            DetailPruned = record.DetailPruned,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditEventCount = record.AuditEventCount,
            TopIssues = visibleTopIssues.Select(i => ToIssueDto(i, guidance, issueHandlingByKey, noiseMarks, unhandledSeverities, openCases, priorClosedIssueKeys)).ToList(),
            Categories = CategoryAggregator.Aggregate(visibleTopIssues).Select(ToCategoryDto).ToList(),
            TrendAlerts = caseGrantOnly ? new List<string>() : record.TrendAlerts,
            CorrelationAlerts = caseGrantOnly ? new List<string>() : record.CorrelationAlerts,
            TrendAlertRefs = caseGrantOnly ? new List<TrendAlertRefDto>() : record.TrendAlertRefs
                .Select(r => new TrendAlertRefDto { Text = r.Text, IssueKey = r.IssueKey, Kind = r.Kind }).ToList(),
            CorrelationAlertRefs = caseGrantOnly ? new List<CorrelationAlertRefDto>() : record.CorrelationAlertRefs
                .Select(r => new CorrelationAlertRefDto { Text = r.Text, PatternId = r.PatternId }).ToList(),
            SuppressedTrendAlerts = caseGrantOnly ? new List<string>() : record.SuppressedTrendAlerts,
            SuppressedCorrelationAlerts = caseGrantOnly ? new List<string>() : record.SuppressedCorrelationAlerts,
            DeepDives = caseGrantOnly ? new List<DeepDiveDto>() : record.DeepDives.Select(d => new DeepDiveDto
            {
                Category = d.Category.ToString(),
                Findings = d.Findings.Select(f => new DeepDiveFindingDto
                {
                    Problem = f.Problem,
                    Impact = f.Impact,
                    LikelyCauses = f.LikelyCauses,
                    NextSteps = f.NextSteps
                }).ToList()
            }).ToList(),
            DataIncomplete = record.DataIncomplete,
            SecurityLogAvailable = record.SecurityLogAvailable,
            UncoveredChecks = record.UncoveredChecks,
            // 報告有自己的保留期，可能比紀錄先被清掉——實查有無，不看紀錄上那個永遠留著的旗標，
            // 否則畫面會給出一個點下去必定落空的入口
            HasReport = !caseGrantOnly && _reports.Exists(reportHost, date, ReportKinds.DailyRisk),
            CaseGrantOnly = caseGrantOnly,
            WeeklyCheckup = record.WeeklyCheckup == null ? null : new WeeklyCheckupDto
            {
                CheckupDate = record.WeeklyCheckup.CheckupDate.ToString("yyyy-MM-dd"),
                HasFindings = record.WeeklyCheckup.HasFindings,
                Conclusion = record.WeeklyCheckup.Conclusion,
                // 體檢報告掛在體檢基準日，不是這筆紀錄的日期
                HasReport = !caseGrantOnly &&
                            _reports.Exists(reportHost, record.WeeklyCheckup.CheckupDate, ReportKinds.WeeklyCheckup)
            },
            CanHandle = _currentUser.Has(Capability.Handle),
            UnhandledSeverities = settings.UnhandledSeverities,
            SeverityDisplayMode = settings.SeverityDisplayMode
        };
    }

    /// <summary>
    /// 當日報告全文（純文字），供「詢問 AI」把報告一併餵給模型。
    /// </summary>
    public string? GetReport(long hostId, DateTime date) =>
        GetReportView(hostId, date, ReportKinds.DailyRisk)?.Content;

    /// <summary>
    /// 報告全文與其顯示欄位。<paramref name="kind"/> 見 <see cref="ReportKinds"/>。
    ///
    /// 讀取以「主機×日期×種類」自然鍵反查（見 <see cref="IReportReader"/>），
    /// 紀錄上的 <c>ReportFile</c> 只當「這天有沒有風險報告」的旗標用。
    /// </summary>
    public ReportViewDto? GetReportView(long hostId, DateTime date, string kind)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        // 報告全文是整台主機當日的完整敘事，案件授與者不得取得（§7）。前端已不顯示入口，
        // 這裡是防直接打端點——授權判斷不能只長在畫面上。
        // **回 null 而不是拋例外**：「詢問 AI」會把報告一併餵給模型（AiController.Chat），
        // 拋例外會讓整個對話端點 404；回 null 是既有的「這天沒有報告」路徑，對話照常進行、
        // 只是不含報告內容
        if (_visibility.IsCaseGrantOnly(hostId)) return null;

        // 體檢報告掛在體檢基準日，不是這筆紀錄的日期——兩者可以不同天
        var reportDate = kind == ReportKinds.WeeklyCheckup ? record.WeeklyCheckup?.CheckupDate.Date : date.Date;
        if (reportDate == null) return null;

        var content = _reports.Read(new HostKey { HostId = record.HostId, HostName = record.Host }, reportDate.Value, kind);
        return content == null ? null : ReportViewDto.From(content);
    }

    /// <summary>
    /// 單一問題簽章在本主機的先前處理歷史（docs/archive/FEEDBACK-5-PLAN.md §4）：只含結案類——
    /// 「處理中」與「未處理」的歷史不列入，使用者要的是「上次怎麼解的」不是完整流水帳。
    /// 授權沿用 GetDetail／GetReport 同一套（<see cref="IRecordRepository.GetOne"/> 先驗證可見範圍）。
    /// </summary>
    public IssueHistoryDto GetIssueHistory(long hostId, DateTime date, string issueKey)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        // 案件授與者只能查被交辦問題的歷史（§7）——這支端點以 issueKey 為參數，
        // 不擋的話換個 key 就能讀到同一台主機其他問題的處理過程
        var allowed = _visibility.GetIssueKeyRestriction(hostId);
        if (allowed != null && !allowed.Contains(issueKey))
            throw DomainException.NotFound("找不到這個問題，或您沒有檢視權限。");

        var hostName = _hosts.Get(hostId)?.HostName ?? record.Host;

        var entries = _issueHandlings
            .GetMany(new[] { hostName }, DateTime.MinValue, date.Date.AddDays(-1))
            .Where(h => string.Equals(h.IssueKey, issueKey, StringComparison.Ordinal) && IssueHandlingStatuses.IsClosed(h.Status))
            .OrderByDescending(h => h.Date)
            .Select(h => new IssueHistoryEntryDto
            {
                Date = h.Date.ToString("yyyy-MM-dd"),
                Status = h.Status,
                StatusText = IssueStatusText(h.Status),
                Note = h.Note,
                ActorAccount = h.ActorAccount,
                ActorDisplayName = _users.FindByAccount(h.ActorAccount)?.DisplayName,
                FromCase = !string.IsNullOrEmpty(h.CaseId)
            })
            .ToList();

        var cases = _cases.GetMany(new[] { hostName })
            .Where(c => string.Equals(c.IssueKey, issueKey, StringComparison.Ordinal) && c.ClosedAt != null)
            .OrderByDescending(c => c.ClosedAt)
            .Select(c => new IssueHistoryCaseDto
            {
                HandlerName = c.HandlerId.HasValue ? _users.Get(c.HandlerId.Value)?.DisplayName : null,
                FirstLinkedDate = c.FirstLinkedDate.ToString("yyyy-MM-dd"),
                LastLinkedDate = c.LastLinkedDate.ToString("yyyy-MM-dd"),
                ClosedAt = c.ClosedAt?.ToString("yyyy-MM-dd"),
                Status = c.Status,
                StatusText = IssueStatusText(c.Status),
                Note = c.Note
            })
            .ToList();

        return new IssueHistoryDto { Cases = cases, Entries = entries };
    }

    public HostDetailDto GetHostDetail(long hostId, int days)
    {
        _visibility.EnsureVisible(hostId);

        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        // 錨點＝昨天，不是今天（回饋十九輪批次C）：分析永遠只產到昨天，時間軸錨在今天
        // 會固定多出一格「沒分析」——那不是涵蓋缺口，只是今晚批次還沒跑到而已，
        // 硬把它畫成跟真正的缺口同一種灰格，反而稀釋了缺口訊號的可信度
        var anchor = DateTime.Today.AddDays(-1);
        var from = anchor.AddDays(-days + 1);
        // 別名展開：這台主機若併入過其他主機，時間軸要涵蓋合併前的那段歷史，
        // 否則「風險時間軸」會在合併日之前整片空白，看起來像沒有分析過。
        // applyDayRiskVisibility=false（docs/archive/FEEDBACK-3-PLAN.md #8 豁免）：時間軸與下方的
        // 重點問題彙總都要看完整證據——被「日風險等級顯示」設定藏起來的日子，時間軸若顯示成
        // 「無分析紀錄」灰格就是說謊，這裡是全站唯二不受該設定影響的查詢路徑之一
        // （另一個是 GetOne，本來就不走 filter 路徑）。
        var records = _repository.Query(new RecordQueryFilter
        {
            Hosts = _repository.ResolveHostKeys(hostId),
            From = from
        }, applyDayRiskVisibility: false)
            // 一天可能有兩筆：合併當天存活主機與墓碑各分析過一次。時間軸一天一格，
            // 取存活主機那筆（沒有才退而取其一）——直接 ToDictionary 會因重複鍵整頁爆掉
            .GroupBy(r => r.Date.Date)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(r => r.HostId == hostId) ?? g.First());

        // 逐日填格：**沒有紀錄的日子也要有格子**——那代表「這天沒分析」，
        // 與「這天分析過、沒風險」是完全不同的意義，畫面上必須分得出來
        var timeline = new List<TimelineDayDto>();
        for (var date = from; date <= anchor; date = date.AddDays(1))
        {
            records.TryGetValue(date.Date, out var record);
            timeline.Add(new TimelineDayDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                HasRecord = record != null,
                RiskLevel = record?.RiskLevel,
                Headline = record?.Headline ?? "",
                HasCoverageGap = record != null && record.HasCoverageGap
            });
        }

        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        var latestCheckup = records.Values
            .Where(r => r.WeeklyCheckup != null)
            .OrderByDescending(r => r.Date)
            .Select(r => r.WeeklyCheckup!)
            .FirstOrDefault();

        return new HostDetailDto
        {
            HostId = host.HostId,
            HostName = host.HostName,
            DisplayName = host.DisplayName,
            RoleDesc = host.RoleDesc,
            IpAddress = host.IpAddress,
            Os = host.Os,
            Tier = host.Tier,
            TierText = HostDtoMapper.TierText(host.Tier),
            Source = host.Source,
            NetiqServer = host.NetiqServer,
            LastReportAt = host.LastReportAt,
            GroupNames = NameFormat.ResolveNames(host.GroupIds, groups, g => g.GroupName),
            OwnerNames = NameFormat.ResolveNames(host.OwnerUserIds, users, u => u.DisplayName),
            Timeline = timeline,
            LatestCheckup = latestCheckup == null ? null : new WeeklyCheckupDto
            {
                CheckupDate = latestCheckup.CheckupDate.ToString("yyyy-MM-dd"),
                HasFindings = latestCheckup.HasFindings,
                Conclusion = latestCheckup.Conclusion
            },
            TopSignatures = BuildIssueSummary(records.Values),
            MaxBackfillDays = NetiqOptions.GetEffectiveBackfillDaysLimit(_settings.Get().RetentionDays)
        };
    }

    /// <summary>
    /// 主機詳情頁「重點問題」某一列展開後的發生明細（docs/archive/FEEDBACK-4-PLAN.md §3）：點某個
    /// 問題（Source+EventId）看它在期間內逐日出現的頻率、間隔與各日處理狀態。與
    /// <see cref="GetHostDetail"/> 共用同一套別名展開＋<c>applyDayRiskVisibility:false</c> 豁免
    /// （時間軸與這裡都要看完整證據，不受「日風險等級顯示」設定影響）。
    /// </summary>
    public HostIssueOccurrenceDto GetHostIssueOccurrences(long hostId, string source, int eventId, int days)
    {
        _visibility.EnsureVisible(hostId);
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        // 錨點與 GetHostDetail 同一套（回饋十九輪批次C）：days 視窗的語意要一致，
        // 否則「重點問題」展開的逐日發生明細與上方時間軸看到的區間會對不上
        var from = DateTime.Today.AddDays(-1).AddDays(-days + 1);
        var records = _repository.Query(new RecordQueryFilter
        {
            Hosts = _repository.ResolveHostKeys(hostId),
            From = from,
            Source = source,
            EventId = eventId
        }, applyDayRiskVisibility: false)
            // 合併當天可能有存活主機與墓碑各一筆，取存活主機那筆（同 GetHostDetail 的既有處理）
            .GroupBy(r => r.Date.Date)
            .Select(g => g.FirstOrDefault(r => r.HostId == hostId) ?? g.First())
            .Where(r => r.TopIssues.Any(i => i.Source == source && i.EventId == eventId))
            .OrderBy(r => r.Date)
            .ToList();

        if (records.Count == 0) return new HostIssueOccurrenceDto();

        var hostName = host.HostName;
        // 一個 (Source,EventId) 可能對到多個完整 IssueKey（LogName/EntryType 不同）——
        // 逐日取當天實際命中的那個簽章，狀態也各自對應那個簽章的標記，不強行假設全期間同一把鍵
        var issueKeys = records
            .SelectMany(r => r.TopIssues.Where(i => i.Source == source && i.EventId == eventId))
            .Select(IssueSignatureKey.For)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet();

        var issueHandlings = _issueHandlings.GetMany(new[] { hostName }, records.Min(r => r.Date), records.Max(r => r.Date));
        var noiseMarks = _noiseMarks.GetForHost(hostName).ToDictionary(m => m.IssueKey, StringComparer.Ordinal);
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var occurrences = new List<HostIssueOccurrenceDayDto>();
        foreach (var record in records)
        {
            var issue = record.TopIssues.First(i => i.Source == source && i.EventId == eventId);
            var key = IssueSignatureKey.For(issue);
            var handling = issueHandlings.FirstOrDefault(h => h.Date.Date == record.Date.Date && string.Equals(h.IssueKey, key, StringComparison.Ordinal));
            var (status, isDefaultUnhandled, noiseMark) = ResolveIssueStatus(issue, handling, noiseMarks, unhandledSeverities);

            occurrences.Add(new HostIssueOccurrenceDayDto
            {
                Date = record.Date.ToString("yyyy-MM-dd"),
                Count = issue.Count,
                RiskLevel = record.RiskLevel,
                StatusText = status.Length > 0 ? IssueStatusText(status)
                    : isDefaultUnhandled ? "不處理（預設）"
                    : noiseMark != null ? "已知雜訊（自動）"
                    : "未處理",
                FromCase = handling?.CaseId != null
            });
        }

        // 案件行：有進行中或最近結案的案件時才顯示——「上次誰處理的、怎麼結的」，
        // 即使案件已結案也保留（不是只顯示進行中）
        var relevantCase = _cases.GetMany(new[] { hostName })
            .Where(c => issueKeys.Contains(c.IssueKey))
            .OrderByDescending(c => c.ClosedAt == null)
            .ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        var stats = BuildOccurrenceStats(records);
        stats.TotalCount = occurrences.Sum(o => o.Count);

        return new HostIssueOccurrenceDto
        {
            Stats = stats,
            Case = relevantCase == null ? null : new HostIssueOccurrenceCaseDto
            {
                HandlerId = relevantCase.HandlerId,
                HandlerName = relevantCase.HandlerId.HasValue ? _users.Get(relevantCase.HandlerId.Value)?.DisplayName : null,
                Status = relevantCase.Status,
                StatusText = IssueStatusText(relevantCase.Status),
                FirstLinkedDate = relevantCase.FirstLinkedDate.ToString("yyyy-MM-dd"),
                ClosedAt = relevantCase.ClosedAt
            },
            Occurrences = occurrences
        };
    }

    /// <summary>出現頻率統計：平均間隔（只出現一天時無意義，回 null）、最長連續出現天數
    /// （日曆日相鄰才算連續，缺一天就斷）</summary>
    private static HostIssueOccurrenceStatsDto BuildOccurrenceStats(List<DailyAnalysisRecord> records)
    {
        var dates = records.Select(r => r.Date.Date).Distinct().OrderBy(d => d).ToList();

        double? avgGapDays = null;
        if (dates.Count > 1)
        {
            var gaps = new List<double>();
            for (var i = 1; i < dates.Count; i++) gaps.Add((dates[i] - dates[i - 1]).TotalDays);
            avgGapDays = gaps.Average();
        }

        var longestStreak = dates.Count > 0 ? 1 : 0;
        var currentStreak = longestStreak;
        for (var i = 1; i < dates.Count; i++)
        {
            currentStreak = (dates[i] - dates[i - 1]).Days == 1 ? currentStreak + 1 : 1;
            longestStreak = Math.Max(longestStreak, currentStreak);
        }

        return new HostIssueOccurrenceStatsDto
        {
            DaysSeen = dates.Count,
            // TotalCount 由呼叫端另外從逐日結果加總覆寫（BuildOccurrenceStats 這裡沒有
            // Source/EventId 可篩選，records.TopIssues 是整日全部問題，不能直接加總）
            AvgGapDays = avgGapDays,
            LongestStreak = longestStreak,
            FirstSeen = dates.Count > 0 ? dates[0].ToString("yyyy-MM-dd") : string.Empty,
            LastSeen = dates.Count > 0 ? dates[^1].ToString("yyyy-MM-dd") : string.Empty
        };
    }

    /// <summary>
    /// 主機詳情頁「重點問題（期間彙總）」（docs/archive/FEEDBACK-3-PLAN.md #4）：依 Source+EventId
    /// 分組——與 <see cref="RecordQueryHelpers.GroupIssuesBySignature"/> 共用分組鍵定義。
    /// 這裡關心「出現幾天」而不是「幾台主機」，維度不同故各自 Select。
    /// 排序：最高嚴重度（重→輕）→ 總次數 desc。
    /// </summary>
    private static List<HostIssueSummaryDto> BuildIssueSummary(IEnumerable<DailyAnalysisRecord> records) =>
        RecordQueryHelpers.GroupIssuesBySignature(records)
            .Select(g =>
            {
                // 說明／分類取最近一天的：規則的已知問題文字可能事後被編輯過，
                // 各天的舊紀錄各自保留分析當下的快照，「最近一天最新」比任意挑一筆更有用
                var latest = g.OrderByDescending(x => x.Record.Date).First();
                return (MaxSeverity: g.Max(x => x.Issue.Severity), Dto: new HostIssueSummaryDto
                {
                    Source = g.Key.Source,
                    EventId = g.Key.EventId,
                    Category = latest.Issue.Category.ToString(),
                    TotalCount = g.Sum(x => x.Issue.Count),
                    DaysSeen = g.Select(x => x.Record.Date.Date).Distinct().Count(),
                    LastSeenDate = latest.Record.Date.ToString("yyyy-MM-dd"),
                    KnownIssue = latest.Issue.KnownIssue
                });
            })
            // 排序用未字串化的 enum（High=2 > Medium=1 > Low=0），字串化只在最後賦值，
            // 不繞「轉字串再解析回列舉」那種多此一舉的寫法
            .OrderByDescending(x => x.MaxSeverity)
            .ThenByDescending(x => x.Dto.TotalCount)
            .Select(x =>
            {
                x.Dto.MaxSeverity = x.MaxSeverity.ToString();
                return x.Dto;
            })
            .ToList();

    /// <summary>
    /// 判定依據代碼 → 白話文字（docs/archive/HISTORY.md #11）：與 LogAnalysisService
    /// 寫入的代碼格式對應（rule:.../correlation/trend/high_issue:.../medium/ai_raise）。
    /// null／未知代碼一律回 null，前端顯示通用說明，不強行湊一句解釋不出來的話。
    /// </summary>
    private static string? FormatRiskBasis(string? basis)
    {
        if (string.IsNullOrEmpty(basis)) return null;
        if (basis == "ai_raise") return "AI 判讀上調（程式判定較低）";
        if (basis == "trend") return "頻率異常規則命中";
        if (basis == "correlation") return "跨事件關聯訊號命中";
        if (basis == "medium") return "問題嚴重度判定為中風險";
        if (basis.StartsWith("rule:", StringComparison.Ordinal)) return $"重大規則命中：{basis[5..]}";
        if (basis.StartsWith("high_issue:", StringComparison.Ordinal)) return $"高嚴重度問題：{basis[11..]}";
        return null;
    }

    /// <summary>
    /// 規則命中問題的處置參考索引。用當前 rules.json（反映 Web 編輯）；載入失敗時退回
    /// 內建種子，詳情頁仍看得到處置參考、不會因規則檔一時讀不到就整片從缺。
    /// </summary>
    private Dictionary<string, KnownIssueRule> LoadGuidanceLookup()
    {
        var outcome = _rules.Load();
        var rules = outcome.Success && outcome.Content != null
            ? outcome.Content.Rules
            : KnownIssueSeed.CreateRules();

        // 同 Id 理論上唯一；防禦性地取第一筆，避免壞檔的重複 Id 讓 ToDictionary 整個炸掉
        return rules
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 問題層級狀態判定的單點邏輯（docs/archive/FEEDBACK-4-PLAN.md §3）：未標記時看已知雜訊記憶／
    /// 未處理計算等級推導出「不處理（預設）」「已知雜訊（自動）」；明確標記過（含 open）一律
    /// 尊重使用者的判斷，不再套自動推導。ToIssueDto（詳情頁）與主機詳情問題發生明細
    /// （GetHostIssueOccurrences）共用，避免兩處各自維護一份判定條件遲早漂移不一致。
    /// </summary>
    private static (string Status, bool IsDefaultUnhandled, NoiseMark? NoiseMark) ResolveIssueStatus(
        LogIssueSignature issue, IssueHandling? handling, Dictionary<string, NoiseMark>? noiseMarks,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var key = IssueSignatureKey.For(issue);
        var status = handling?.Status ?? string.Empty;

        // 未標記時才看得到自動判讀：明確標記過（含 open）一律尊重使用者的判斷，不再套自動推導
        var noiseMark = status.Length == 0 && noiseMarks != null && noiseMarks.TryGetValue(key, out var m) ? m : null;
        // 未列入「未處理計算」等級的問題（「系統管理 > 設定」頁維護），未標記過時預設視為不處理
        var isDefaultUnhandled = status.Length == 0 && noiseMark == null && !unhandledSeverities.Contains(issue.Severity);

        return (status, isDefaultUnhandled, noiseMark);
    }

    private static IssueDto ToIssueDto(
        LogIssueSignature issue,
        Dictionary<string, KnownIssueRule>? guidance,
        Dictionary<string, IssueHandling>? issueHandlingByKey,
        Dictionary<string, NoiseMark>? noiseMarks,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        Dictionary<string, (long? HandlerId, string? HandlerName, string? HandlerAccount, string Status, string FirstLinkedDate)>? openCases = null,
        HashSet<string>? priorClosedIssueKeys = null)
    {
        var key = IssueSignatureKey.For(issue);
        var handling = issueHandlingByKey != null && issueHandlingByKey.TryGetValue(key, out var h) ? h : null;
        var (status, isDefaultUnhandled, noiseMark) = ResolveIssueStatus(issue, handling, noiseMarks, unhandledSeverities);

        (long? HandlerId, string? HandlerName, string? HandlerAccount, string Status, string FirstLinkedDate)? openCase =
            openCases != null && openCases.TryGetValue(key, out var c) ? c : null;

        return new IssueDto
        {
            LogName = issue.LogName,
            Source = issue.Source,
            EventId = issue.EventId,
            SourceEventLabel = issue.SourceEventLabel,
            Count = issue.Count,
            Category = issue.Category.ToString(),
            Severity = issue.Severity.ToString(),
            ElevatesDayRisk = issue.ElevatesDayRisk,
            KnownIssue = issue.KnownIssue,
            FirstSeen = issue.FirstSeen,
            LastSeen = issue.LastSeen,
            DistinctMessageCount = issue.DistinctMessageCount,
            KeyDetails = issue.KeyDetails,
            LoginFailureDetails = issue.LoginFailureDetails?.Select(d => new LoginFailureDetailDto
            {
                Account = d.Account,
                IsComputerAccount = d.IsComputerAccount,
                Source = d.Source,
                LogonTypeText = d.LogonType.HasValue ? LoginFailureTextFormatter.FormatLogonType(d.LogonType.Value) : string.Empty,
                ReasonText = LoginFailureTextFormatter.FormatReason(d.ReasonCode),
                Count = d.Count
            }).ToList(),
            LoginFailureTotalCount = issue.LoginFailureTotalCount,
            LoginFailureDetailsTruncated = issue.LoginFailureDetailsTruncated,
            ResidualCredentialRetry = issue.ResidualCredentialRetry,
            ResidualCredentialBasis = issue.ResidualCredentialBasis,
            SampleMessages = issue.SampleMessages,
            Suppressed = issue.Suppressed,
            Trend = issue.Trend.ToString(),
            TrendText = BuildTrendText(issue),
            Guidance = BuildGuidance(issue, guidance),
            IssueKey = key,
            RuleId = issue.RuleId,
            HandlingStatus = status,
            HandlingStatusText = IssueStatusText(status),
            IsDefaultUnhandled = isDefaultUnhandled,
            IsAutoNoise = noiseMark != null,
            NoiseNote = noiseMark?.Note,
            DueDate = handling?.DueDate?.ToString("yyyy-MM-dd"),
            CaseHandlerId = openCase?.HandlerId,
            CaseHandlerName = openCase?.HandlerName,
            CaseHandlerAccount = openCase?.HandlerAccount,
            CaseStatus = openCase?.Status,
            CaseFirstLinkedDate = openCase?.FirstLinkedDate,
            HasPriorHandling = priorClosedIssueKeys?.Contains(key) ?? false
        };
    }

    private static string IssueStatusText(string status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        IssueHandlingStatuses.InProgress => "處理中",
        IssueHandlingStatuses.Observing => "觀察中",
        // 無法處理（回饋十八輪批次G，體檢輪修正）：與 HandlingTextHelpers.IssueStatusText 用字一致——
        // 這裡是獨立的一份（Open 刻意映射成空字串而非「未處理」，不與共用版合併），
        // 新增狀態時兩處都要記得加，見 HandlingTextHelpers 的對照。
        IssueHandlingStatuses.Escalated => "無法處理（待管理員決定）",
        IssueHandlingStatuses.Open => string.Empty,
        _ => string.Empty
    };

    /// <summary>
    /// 規則命中問題的處置參考（與 txt 報告「處置參考（知識庫）」同一份來源）。
    /// 以問題的 RuleId 反查規則——用命中當下記下的 Id 最精準（來源/EventId 反查可能因規則改動而漂移）。
    /// 無 RuleId（未命中規則的 Other）或規則無知識內容時回 null，前端就不掛展開面板。
    /// </summary>
    private static IssueGuidanceDto? BuildGuidance(LogIssueSignature issue, Dictionary<string, KnownIssueRule>? guidance)
    {
        if (guidance == null || string.IsNullOrEmpty(issue.RuleId)) return null;
        if (!guidance.TryGetValue(issue.RuleId, out var rule)) return null;

        var hasContent = !string.IsNullOrWhiteSpace(rule.PlainExplanation) ||
                         !string.IsNullOrWhiteSpace(rule.Impact) ||
                         rule.LikelyCauses.Length > 0 || rule.NextSteps.Length > 0;
        if (!hasContent) return null;

        return new IssueGuidanceDto
        {
            Explanation = rule.PlainExplanation,
            Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses.ToList(),
            NextSteps = rule.NextSteps.ToList()
        };
    }

    /// <summary>
    /// 趨勢的白話描述在後端組好（含比對數字），前端不再自行拼裝——
    /// 同一份規則若兩邊各寫一次，遲早出現「清單說頻率上升、詳情說重複發生」。
    /// </summary>
    private static string BuildTrendText(LogIssueSignature issue)
    {
        var parts = new List<string>();

        // 防衛已存的舊紀錄：TrendAnalyzer 修正前寫入的行可能標成 New 卻帶著昨日次數
        // （首次出現的判定曾誤用可靠歷史，把「只在不完整日出現過」當成首次）。history.txt
        // 不會回填，這裡遇到矛盾就改述為重複發生，不讓畫面自打嘴巴。
        var effectiveTrend = issue.Trend == IssueTrend.New && issue.PreviousDayCount > 0
            ? IssueTrend.Recurring
            : issue.Trend;

        switch (effectiveTrend)
        {
            case IssueTrend.New:
                parts.Add("首次出現");
                break;
            case IssueTrend.Rising:
                parts.Add("頻率上升");
                break;
            case IssueTrend.Recurring:
                parts.Add("重複發生");
                break;
            case IssueTrend.Declining:
                parts.Add("頻率下降");
                break;
        }

        // 首次出現時「前一日 N 次」是贅述（首次出現＝前一日必為 0），不輸出——
        // 這是 §5.1 D-1 #5：畫面已經說了「首次」，不需要再用一個必然是 0 的數字佐證
        if (effectiveTrend != IssueTrend.New && issue.PreviousDayCount.HasValue)
            parts.Add($"前一日 {issue.PreviousDayCount} 次");
        if (issue.HistoryDailyAverage.HasValue) parts.Add($"歷史基準 {issue.HistoryDailyAverage.Value:0.#} 次");
        if (issue.DaysSeenInHistory > 0) parts.Add($"近期出現 {issue.DaysSeenInHistory} 天");

        return string.Join("\n", parts);
    }

    private static CategorySummaryDto ToCategoryDto(CategorySummary summary) => new()
    {
        Category = summary.Category.ToString(),
        IssueCount = summary.IssueCount,
        TotalEvents = summary.TotalEvents,
        MaxSeverity = summary.MaxSeverity.ToString(),
        CriticalCount = summary.CriticalCount,
        HighCount = summary.HighCount,
        MediumCount = summary.MediumCount,
        LowCount = summary.LowCount,
        ElevatesCount = summary.ElevatesCount
    };
}
