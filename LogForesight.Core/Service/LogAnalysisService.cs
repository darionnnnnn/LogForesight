using System.Diagnostics;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 每日分析流程：取多來源 log → 聚合 → 規則分類 → 帶入近期歷史 → 呼叫 AI 白話翻譯 → 寫回歷史。
/// 設計原則見 docs/archive/HISTORY.md：規則/趨勢/關聯三層負責偵測與風險判定（確定性、AI 判斷只能
/// 把風險往上拉不能往下壓），AI 負責把這些結論翻譯成白話——低風險日（三層皆無訊號）不呼叫 AI。
/// </summary>
public class LogAnalysisService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 低風險日仍執行前置掃描的未分類事件種類門檻。低於此值的低風險日維持零 AI 呼叫；
    /// 達到此值代表當日有異常大量的未分類事件，規則層依定義沒看過它們，值得付出一次掃描成本。
    /// </summary>
    private const int MinTailForLowRiskScreening = 20;

    /// <summary>敘事欄位（story/trend_story/action）的合理長度上限。這些是一到兩句話的白話敘述，
    /// 不該超過這個長度，超過視為模型異常重複輸出（JSON 語法可能仍合法，但內容不合理），觸發 JSON 重試</summary>
    private const int MaxSummaryChars = 600;

    /// <summary>標題欄位（headline）的長度上限——比敘事欄位更短，一句話而非一段話</summary>
    private const int MaxHeadlineChars = 60;

    private readonly EventLogService _eventLogService;
    private readonly IAiService _aiService;
    private readonly IAnalysisRecordStore _historyService;
    private readonly ISuppressionStore _suppressionStore;
    private readonly RiskReportService? _reportService;
    private readonly string _serverDescription;
    private readonly string _host;
    private readonly long _hostId;
    private readonly AnalysisPromptBuilder _promptBuilder;

    /// <param name="suppressionStore">主機級告警抑制設定（見 docs/RULES-SPEC.md）：只影響「要不要吵」
    /// （通知、風險升級），偵測與紀錄照常——事件照樣聚合、命中規則、寫入歷史，只是不進告警清單、不拉高風險</param>
    /// <param name="serverDescription">伺服器角色描述（如「AD 網域控制站」），會帶入 prompt 讓 AI 依環境判讀；空字串則略過</param>
    /// <param name="reportService">提供時，風險「中」以上的日期會輸出 export/{日期}.txt 風險報告</param>
    /// <param name="host">寫入紀錄的主機名稱；null/空字串時預設為 Environment.MachineName（本機情境的自然值）</param>
    /// <param name="hostId">寫入紀錄的主機 PK（主機清單登記後取得）。**紀錄與主機的關聯鍵**；
    /// 0＝取不到主機列時的降級，查詢端會退回以主機名稱比對，分析本身不受影響</param>
    public LogAnalysisService(EventLogService eventLogService, IAiService aiService, IAnalysisRecordStore historyService,
        ISuppressionStore suppressionStore, string serverDescription = "", RiskReportService? reportService = null,
        string? host = null, long hostId = 0)
    {
        _eventLogService = eventLogService;
        _aiService = aiService;
        _historyService = historyService;
        _suppressionStore = suppressionStore;
        _serverDescription = serverDescription;
        _reportService = reportService;
        _host = string.IsNullOrEmpty(host) ? Environment.MachineName : host;
        _hostId = hostId;
        _promptBuilder = new AnalysisPromptBuilder(aiService);
    }

    /// <summary>
    /// 分析已抓取好的當日 log（回補多天時用：log 由呼叫端一次掃描、預先分桶，
    /// 分析迴圈不需等待任何 Event Log I/O，只等 AI 推論）。
    ///
    /// 組合呼叫：<see cref="BuildStatisticalRecordAsync"/> 算統計段，需要 AI 時立刻接著跑
    /// <see cref="CompleteAiAsync"/>，行為與拆分前完全相同——本機分析路徑目前仍走這個組合呼叫，
    /// 兩階段真正脫鉤（統計先寫入、AI 由背景消費者延後補上）只在 NetIQ pipeline 生效
    /// （docs/FEEDBACK-12-PLAN.md §3.3/§3.4）。
    /// </summary>
    /// <param name="useAi">false = 統計模式：聚合、規則分類、趨勢比對照常執行，但不呼叫 AI</param>
    /// <param name="dataIncomplete">true = 本日事件來源不完整（如 Event Log 回補時已被覆蓋），寫入紀錄供趨勢基準排除</param>
    /// <param name="securityLogAvailable">本次執行 Security log 是否成功讀取；false 時停用相關規則層偵測、
    /// 相關關聯模式改標記「未檢查」，並在趨勢基準計算時排除本日的 Security 簽章</param>
    /// <param name="channels">本次各頻道的讀取三態（成功/被拒/不存在）；null = 舊呼叫端（單日情境），
    /// 退回三頻道假設。寫入 <see cref="DailyAnalysisRecord.ChannelsRead"/> 供暖身/趨勢基準判斷，
    /// 並讓 UncoveredChecks 申報被拒的 Defender/RDP 頻道</param>
    public async Task<DailyAnalysisRecord> AnalyzeDayAsync(DateTime targetDate, List<EventLogEntryData> logs, bool useAi = true,
        int historyDays = 14, bool dataIncomplete = false, bool? securityLogAvailable = true, ChannelAvailability? channels = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var (record, workItem) = await BuildStatisticalRecordAsync(
            targetDate, logs, useAi, historyDays, dataIncomplete, securityLogAvailable, channels, ct);

        if (workItem != null)
        {
            var outcome = await CompleteAiAsync(workItem, ct);
            ApplyOutcome(record, outcome);
        }

        _historyService.Append(record);

        Log.Info("完成分析 {Date:yyyy-MM-dd}：風險={Risk}, 錯誤={Errors}, 警告={Warnings}, 稽核={Audit}, " +
                 "aiAnalyzed={AiAnalyzed}, 耗時={ElapsedMs}ms, 報告檔={ReportFile}",
            targetDate, record.RiskLevel, record.ErrorCount, record.WarningCount, record.AuditEventCount,
            record.AiAnalyzed, sw.ElapsedMilliseconds, record.ReportFile ?? "(無)");

        return record;
    }

    /// <summary>
    /// 統計段（docs/FEEDBACK-12-PLAN.md §3.3）：聚合、規則分類、趨勢／慢速趨勢／關聯比對——
    /// 全部確定性計算，不呼叫 AI。回傳的 <see cref="DailyAnalysisRecord"/> 在「不需要 AI」時
    /// 已經是定案內容（含報告檔，若風險可行動）；「需要 AI」時則是暫代內容（Headline/Summary
    /// 顯示排隊中字樣），同時回傳非 null 的 <see cref="AiWorkItem"/> 供 <see cref="CompleteAiAsync"/>
    /// 之後補完——呼叫端（NetIQ pipeline 的兩階段消費者）據此決定要不要先把這筆統計結果寫入，
    /// 讓搜尋主線不被 AI 拖住。
    ///
    /// 「需要 AI」的判準：<c>useAi &amp;&amp; (!lowRisk || tailIssues.Count >= MinTailForLowRiskScreening)</c>——
    /// 與拆分前的 <c>shouldScreen</c>／<c>skipAiForLowRisk</c> 語意等價，只是決策時間點提前到
    /// AI 呼叫本身發生之前：lowRisk 且尾巴事件夠多的日子，統計段還答不出「篩選後到底要不要用 AI
    /// 內容」，這個問題留給 AI 段自己跑完前置掃描後決定（見 <see cref="CompleteAiAsync"/> 內的
    /// <c>skipForLowRisk</c>）。
    /// </summary>
    internal async Task<(DailyAnalysisRecord Record, AiWorkItem? WorkItem)> BuildStatisticalRecordAsync(
        DateTime targetDate, List<EventLogEntryData> logs, bool useAi = true, int historyDays = 14,
        bool dataIncomplete = false, bool? securityLogAvailable = true, ChannelAvailability? channels = null,
        CancellationToken ct = default)
    {
        Log.Info("開始分析 {Date:yyyy-MM-dd}：log 筆數={LogCount}, useAi={UseAi}", targetDate, logs.Count, useAi);

        var issues = LogAggregator.Aggregate(logs);

        // 主機級告警抑制（見 docs/RULES-SPEC.md）：只標記「這個簽章命中的規則被本機抑制」，
        // 不影響聚合、分類或後續寫入歷史——偵測與紀錄照常，只是後面判定風險/組告警文字時要跳過它。
        // 保留完整的 activeSuppressions（含 Reason）供風險報告的「已抑制的告警」區塊顯示。
        var activeSuppressions = SuppressionFilter.ActiveForHost(_suppressionStore.LoadAll(), _host, DateTime.Now);
        if (activeSuppressions.Count > 0)
        {
            var suppressedRuleIds = SuppressionFilter.ToRuleIdSet(activeSuppressions);
            foreach (var issue in issues)
            {
                if (issue.RuleId != null && suppressedRuleIds.Contains(issue.RuleId))
                {
                    issue.Suppressed = true;
                }
            }
        }

        // EntryType 0 是 classic API 讀到的 Critical 等級事件（如 Kernel-Power 41），計入錯誤
        var errorCount = logs.Count(l => l.EntryType == EventLogEntryType.Error || (int)l.EntryType == 0);
        var warningCount = logs.Count(l => l.EntryType == EventLogEntryType.Warning);
        var auditCount = logs.Count(l => l.EntryType is EventLogEntryType.FailureAudit or EventLogEntryType.SuccessAudit);

        // 錨定在被分析的那一天：回補中間缺漏日時，檔案裡已經有該日之後的紀錄，
        // 而 TrendAnalyzer 不自行過濾日期——不錨定就等於拿後來發生的事去判斷這一天
        var history = _historyService.ReadRecent(targetDate, historyDays);

        // 程式端確定性頻率比對：當日 vs 前一日 vs 歷史平均，頻率上升會就地升級該事件的嚴重度
        var trendAlerts = TrendAnalyzer.Apply(issues, history, targetDate, errorCount, auditCount);

        // 慢速趨勢偵測（2026-07-20，見 docs/archive/HISTORY.md）：近 7 天 vs 前 7 天總量比較，
        // 每日、全主機、確定性執行，捕捉躲在 TrendAnalyzer 單日門檻下的緩慢惡化訊號——
        // 取代原本「週六全量體檢」找慢速斜線的職責，偵測延遲從最壞 7 天縮到 1 天。
        // 併入既有 trendAlerts 清單：同屬程式比對出的頻率異常，prompt/報告/console 沿用同一套呈現與風險下限判定
        trendAlerts.AddRange(SlowTrendAnalyzer.Apply(issues, history, targetDate, out bool slowTrendEvaluated));

        issues = issues
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.Count)
            .ToList();

        // 條件式撈取 4624（成功登入）：只有當日 4625 達暴力破解門檻才額外查一次，
        // 平時不收（SuccessAudit 量極大），比對是否與失敗記錄同一組帳號/IP——
        // 這是暴力破解「得手」最直接的證據，比只看見帳號建立/提權更早、更確定
        SuccessfulLogonMatch? successfulLogonMatch = null;
        if (securityLogAvailable != false)
        {
            var bruteForceSignature = issues.FirstOrDefault(i =>
                i.LogName.Equals("Security", StringComparison.OrdinalIgnoreCase) &&
                i.Source.Contains("Security-Auditing", StringComparison.OrdinalIgnoreCase) &&
                i.EventId == 4625 && i.Count >= 10);

            if (bruteForceSignature != null)
            {
                successfulLogonMatch = await DetectSuccessfulLogonAfterBruteForceAsync(targetDate, logs);
            }
        }

        // 跨 log 關聯比對：多個獨立訊號的已知攻擊鏈/故障鏈組合（含跨日比對）。
        // 單一事件各自不嚴重、組合起來卻是明確故事——小模型最容易漏掉的判讀，由程式確定性比對
        var correlations = CorrelationAnalyzer.Detect(issues, history, targetDate, successfulLogonMatch);

        // 這幾個清單都是程式自己產生的短結構化字串（不是原始 log 內容），數量也有上限，記錄完整內容沒問題
        if (trendAlerts.Count > 0)
        {
            Log.Info("頻率異常 {Count} 項：{Alerts}", trendAlerts.Count, string.Join(" | ", trendAlerts));
        }
        if (correlations.Count > 0)
        {
            Log.Info("關聯訊號 {Count} 項：{Alerts}", correlations.Count, string.Join(" | ", correlations.Select(c => c.Description)));
        }

        // 程式判定的風險下限：規則或關聯鏈命中「重大」旗標 → 高；有 High 問題/頻率異常/關聯訊號 → 中
        var ruleRisk = ComputeRuleBasedRisk(issues, trendAlerts, correlations);
        bool lowRisk = ruleRisk == RiskLevels.Low;

        // 判定依據（docs/archive/HISTORY.md #11）：純顯示用途，說明「為什麼是這個風險等級」，
        // 不影響任何判定邏輯本身。AI 若把風險往上拉，CompleteAiAsync 會覆寫為 "ai_raise"
        var riskBasis = DescribeRiskBasis(issues, correlations, trendAlerts, ruleRisk);

        // 前置掃描與主分析都移到 CompleteAiAsync；這裡只需要「日後要不要進 AI 段」的判準，
        // 見本方法 XML 文件的說明
        var tailIssues = AnalysisPromptBuilder.GetTailIssues(issues);
        bool needsAi = useAi && (!lowRisk || tailIssues.Count >= MinTailForLowRiskScreening);

        var uncoveredChecks = BuildUncoveredChecks(securityLogAvailable, channels);

        // 慢速趨勢層若因前期歷史不足而完全沒有比對，要明講——「沒告警」不等於「沒問題」。
        // 歷史本來就不足（部署未滿兩週）屬預期，記 Info；歷史夠長卻仍無法比對，代表前期窗口內
        // 有 DataIncomplete 的日子把可靠天數吃掉了，那是需要留意的靜默失效，記 WARN 並列入申報。
        if (!slowTrendEvaluated)
        {
            if (history.Count >= 2 * SlowTrendAnalyzer.WindowDays)
            {
                Log.Warn("{Date:yyyy-MM-dd} 慢速趨勢比對未執行：前期窗口可靠歷史不足 {Window} 天" +
                         "（歷史共 {HistoryDays} 天，可能含 DataIncomplete 的日子）", targetDate, SlowTrendAnalyzer.WindowDays, history.Count);
                uncoveredChecks.Add($"慢速趨勢比對未執行（前期窗口可靠歷史不足 {SlowTrendAnalyzer.WindowDays} 天，緩慢惡化訊號本日未檢查）");
            }
            else
            {
                Log.Info("{Date:yyyy-MM-dd} 慢速趨勢比對未執行：歷史累積未滿兩期（共 {HistoryDays} 天），屬預期",
                    targetDate, history.Count);
            }
        }

        var record = new DailyAnalysisRecord
        {
            Date = targetDate.Date,
            HostId = _hostId,
            Host = _host,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            AuditEventCount = auditCount,
            TopIssues = issues,
            TrendAlerts = trendAlerts,
            CorrelationAlerts = correlations.Select(c => c.Description).ToList(),
            RiskLevel = ruleRisk,
            RiskBasis = riskBasis,
            AiAnalyzed = false,
            DataIncomplete = dataIncomplete,
            SecurityLogAvailable = securityLogAvailable,
            UncoveredChecks = uncoveredChecks,
            ChannelsRead = channels?.Read
        };

        if (needsAi)
        {
            record.Headline = "（統計已完成，AI 分析排隊中）";
            record.Summary = "（統計已完成，AI 分析排隊中）";
            record.AiPending = true;

            var workItem = new AiWorkItem(targetDate, issues, trendAlerts, correlations, ruleRisk, riskBasis,
                uncoveredChecks, dataIncomplete, errorCount, warningCount, auditCount, historyDays,
                logs, activeSuppressions);
            return (record, workItem);
        }

        // 不需要 AI：lowRisk 且尾巴事件不夠多（既有 skipAiForLowRisk 語意），或 useAi 全域關閉——
        // 兩者都在這裡直接定案，不進 AI 段
        bool skipAiForLowRisk = useAi && lowRisk;
        record.Headline = skipAiForLowRisk ? "今日狀況正常，無需處理" : "（統計模式紀錄，未呼叫 AI 分析）";
        record.Summary = skipAiForLowRisk
            ? "今日無異常訊號，規則/趨勢/慢速趨勢/關聯四層檢查全數通過。"
            : "（統計模式紀錄，未呼叫 AI 分析）";

        // record 此時已是完整定案內容（Headline/Summary/RiskLevel/TrendAlerts/CorrelationAlerts/
        // UncoveredChecks/DataIncomplete 皆已設好），直接傳給報告產生器，GenerateAsync 會就地
        // 把 DeepDives 寫進同一個物件，不需要另外合併
        record.ReportFile = await GenerateReportIfActionableAsync(record, logs, activeSuppressions, ct);

        return (record, null);
    }

    /// <summary>
    /// AI 段（docs/FEEDBACK-12-PLAN.md §3.3）：對 <see cref="BuildStatisticalRecordAsync"/>
    /// 判定需要 AI 的主機日執行前置掃描＋主分析＋深析報告，回傳定案結果供呼叫端合併進已寫入的
    /// 統計紀錄——<see cref="AnalyzeDayAsync"/> 的組合呼叫直接套用；NetIQ pipeline 的兩階段
    /// 消費者則透過類似 <c>AttachWeeklyCheckup</c> 的讀-改-寫回樣板套用（見 <c>AttachAiResult</c>）。
    ///
    /// 歷史在這裡才重讀，不是統計段算好傳進來：讓 <see cref="AiFollowupQueue{T}"/> 的 FIFO
    /// 保序保證的「前一天已定案」語意在讀取當下自然成立，隔日 prompt 引用前一天 AI 摘要的
    /// 既有語意不因兩階段化而降級。
    /// </summary>
    internal async Task<AiOutcome> CompleteAiAsync(AiWorkItem item, CancellationToken ct = default)
    {
        var history = _historyService.ReadRecent(item.TargetDate, item.HistoryDays);
        var tailIssues = AnalysisPromptBuilder.GetTailIssues(item.Issues);
        bool lowRisk = item.RuleRisk == RiskLevels.Low;

        // 前置掃描：Other 類事件種類超過主 prompt 呈現上限時，超出的項目先分批給獨立的
        // AI 呼叫逐項篩選（這些項目彼此不需要一起看，適合拆分），值得注意的帶著掃描意見
        // 回流主分析——主呼叫維持全局判讀，不因折疊漏看、也不因塞滿明細稀釋注意力。
        bool shouldScreen = tailIssues.Count > 0 && (!lowRisk || tailIssues.Count >= MinTailForLowRiskScreening);
        AnalysisPromptBuilder.ScreeningOutcome? screening = null;
        if (shouldScreen)
        {
            Console.WriteLine($"  事件種類較多，前置掃描 {tailIssues.Count} 項未分類項目...");
            screening = await _promptBuilder.ScreenTailAsync(item.TargetDate, tailIssues, ct);
            Log.Info("前置掃描完成：共 {Total} 項，值得注意 {Notable} 項，一般雜訊 {Clean} 項，掃描失敗 {Failed} 項",
                tailIssues.Count, screening.Notable.Count, screening.CleanCount, screening.FailedCount);
        }

        // 低風險日（四層皆無訊號）不呼叫主分析：沒有訊號就沒有故事可講，白話翻譯的價值趨近於零，
        // 2026-07-20 AI 角色轉換——2000 台規模下這是 AI 時間預算能否成立的關鍵之一。
        // 但前置掃描若在未分類事件裡找到值得注意的項目，仍要跑主分析——掃描結果必須能拉高當日
        // 風險等級（MoreSevere），否則掃描發現的異常只會躺在 ScreeningNotes 裡不影響任何判定。
        bool skipForLowRisk = lowRisk && (screening?.Notable.Count ?? 0) == 0;

        string riskLevel = item.RuleRisk;
        string? riskBasis = item.RiskBasis;
        string headline;
        string summary;
        string trendAssessment = string.Empty;
        string action = string.Empty;
        bool aiAnalyzed;
        int screenedTailCount = screening != null ? tailIssues.Count : 0;
        List<string> screeningNotes = screening?.Notable
            .Select(n => $"{n.Issue.LogName}/{n.Issue.Source} EventId {n.Issue.EventId} x{n.Issue.Count}：{n.Reason}")
            .ToList() ?? new List<string>();

        if (skipForLowRisk)
        {
            aiAnalyzed = false;
            headline = "今日狀況正常，無需處理";
            summary = "今日無異常訊號，規則/趨勢/慢速趨勢/關聯四層檢查全數通過。";
        }
        else
        {
            var prompt = AnalysisPromptBuilder.BuildPrompt(item.TargetDate, item.Issues, item.ErrorCount, item.WarningCount,
                item.AuditCount, history, item.TrendAlerts, item.Correlations, screening, item.DataIncomplete,
                item.UncoveredChecks, _serverDescription);

            // response_format=json_object 只保證「合法 JSON」，不保證是我們要的物件形狀
            // （模型可能回傳陣列、或欄位塞入異常冗長的重複文字）；驗證失敗會自動重新請求
            var result = await _aiService.ChatJsonAsync<AiAnalysisResult>(prompt, AnalysisPromptBuilder.SystemPrompt,
                validate: r => r.RiskLevel.Length > 0 && r.Headline.Length > 0 && r.Story.Length > 0
                               && r.Headline.Length <= MaxHeadlineChars && r.Story.Length <= MaxSummaryChars
                               && r.TrendStory.Length <= MaxSummaryChars && r.Action.Length <= MaxSummaryChars,
                label: $"daily-{item.TargetDate:yyyyMMdd}", ct: ct);

            if (result.Success)
            {
                aiAnalyzed = true;
                headline = result.Value!.Headline;
                summary = result.Value.Story;
                trendAssessment = result.Value.TrendStory;
                action = result.Value.Action;
                // AI 判斷與程式判斷取較嚴重者：即使模型輕忽了，規則與趨勢比對的結論也會強制拉高風險等級
                riskLevel = RiskLevels.MoreSevere(RiskLevels.Normalize(result.Value.RiskLevel), item.RuleRisk);
                if (riskLevel != item.RuleRisk) riskBasis = "ai_raise";
            }
            else if (result.RawContent.Length > 0)
            {
                // 網路正常但重試 JsonRetryCount 次後仍不合格：保留原文（截斷避免報告膨脹），不當機、不遺失資訊；
                // 仍算完成 AI 分析，只是白話翻譯品質降級
                aiAnalyzed = true;
                headline = "AI 回覆格式異常，以下為原始內容";
                summary = $"（AI 回覆經 {result.Attempts} 次嘗試仍未通過 JSON 檢查，保留原文供參考）{TextTruncation.Truncate(result.RawContent, MaxSummaryChars)}";
                riskLevel = RiskLevels.MoreSevere(RiskLevels.Normalize(result.RawContent), item.RuleRisk);
                if (riskLevel != item.RuleRisk) riskBasis = "ai_raise";
                Log.Warn("{Date:yyyy-MM-dd} 主分析降級為原文保留（{Attempts} 次嘗試仍未通過 JSON 檢查）", item.TargetDate, result.Attempts);
            }
            else
            {
                // 重試耗盡仍完全失敗（如 llama.cpp 未啟動、網路不通）時降級為統計模式紀錄。
                // 偵測（規則/趨勢/關聯）與規則命中問題的處置建議（靜態知識庫）完全不受影響，
                // 只是少了白話摘要——降級語意刻意用正面表述，AI 已不是偵測的必要環節
                aiAnalyzed = false;
                headline = "今日分析摘要暫缺（AI 服務未回應）";
                summary = $"偵測與處置建議仍完整，僅白話摘要因 AI 服務未回應而從缺（{result.Error}）。";
                Log.Error("{Date:yyyy-MM-dd} 主分析完全失敗，降級為統計模式：{Error}", item.TargetDate, result.Error);
            }
        }

        // CompleteAiAsync 沒有自己的持久化紀錄，組一個形狀跟原本單階段版本完全一樣的暫用物件
        // 餵給報告產生器——少任何一個 BuildReport 會讀的欄位，報告內容就會缺一塊
        // （這裡曾經漏掉，只給 TopIssues/RiskLevel/AiAnalyzed 幾個欄位，體檢時抓到）
        var scratch = new DailyAnalysisRecord
        {
            Date = item.TargetDate,
            HostId = _hostId,
            Host = _host,
            ErrorCount = item.ErrorCount,
            WarningCount = item.WarningCount,
            AuditEventCount = item.AuditCount,
            TopIssues = item.Issues,
            TrendAlerts = item.TrendAlerts,
            CorrelationAlerts = item.Correlations.Select(c => c.Description).ToList(),
            RiskLevel = riskLevel,
            RiskBasis = riskBasis,
            Headline = headline,
            Summary = summary,
            TrendAssessment = trendAssessment,
            Action = action,
            AiAnalyzed = aiAnalyzed,
            ScreenedTailCount = screenedTailCount,
            ScreeningNotes = screeningNotes,
            DataIncomplete = item.DataIncomplete,
            UncoveredChecks = item.UncoveredChecks
        };
        var reportFile = await GenerateReportIfActionableAsync(scratch, item.Logs, item.ActiveSuppressions, ct);

        return new AiOutcome(headline, summary, trendAssessment, action, riskLevel, riskBasis,
            aiAnalyzed, screenedTailCount, screeningNotes, reportFile, scratch.DeepDives);
    }

    /// <summary>把 AI 段的定案結果套用到統計段已建立的紀錄——只有 <see cref="AnalyzeDayAsync"/>
    /// 的組合呼叫用得到（兩段緊接著跑完才寫入一次）；NetIQ pipeline 的兩階段消費者統計段已經
    /// 先寫入一次，AI 段完成後改走 <c>AttachAiResult</c> 的讀-改-寫回，不能重複使用這個方法。</summary>
    private static void ApplyOutcome(DailyAnalysisRecord record, AiOutcome outcome)
    {
        record.Headline = outcome.Headline;
        record.Summary = outcome.Summary;
        record.TrendAssessment = outcome.TrendAssessment;
        record.Action = outcome.Action;
        record.RiskLevel = outcome.RiskLevel;
        record.RiskBasis = outcome.RiskBasis;
        record.AiAnalyzed = outcome.AiAnalyzed;
        record.AiPending = false;
        record.ScreenedTailCount = outcome.ScreenedTailCount;
        record.ScreeningNotes = outcome.ScreeningNotes;
        record.ReportFile = outcome.ReportFile;
        record.DeepDives.AddRange(outcome.DeepDives);
    }

    /// <summary>
    /// 風險「中」以上輸出報告檔（含第二階段 AI 深入分析與原始 log）。統計段（不需要 AI）與
    /// AI 段（<see cref="CompleteAiAsync"/>）都會呼叫——判準只看 <paramref name="record"/> 最終的
    /// <see cref="DailyAnalysisRecord.RiskLevel"/>，不管風險是規則判定還是 AI 拉高的，這與拆分前
    /// 「report 生成不看 useAi、只看最終風險等級」的既有行為一致（統計模式下規則本身判定
    /// 中/高風險一樣會出報告，只是報告內容不含 AI 深析）。
    ///
    /// <paramref name="record"/> 必須已填好 <see cref="RiskReportService.GenerateAsync"/> 會讀取的
    /// 全部欄位（Headline/Summary/TrendAssessment/Action/TopIssues/TrendAlerts/CorrelationAlerts/
    /// UncoveredChecks/DataIncomplete/ScreeningNotes/AiAnalyzed）——呼叫端若沒有現成的完整紀錄
    /// （<see cref="CompleteAiAsync"/> 沒有自己的持久化紀錄），要組一個形狀完全對齊的暫用物件，
    /// 少任何一個欄位，報告內容就會缺一塊。<see cref="DailyAnalysisRecord.DeepDives"/> 由
    /// <see cref="RiskReportService.GenerateAsync"/> 就地寫入同一個 <paramref name="record"/>。
    /// </summary>
    private async Task<string?> GenerateReportIfActionableAsync(
        DailyAnalysisRecord record, List<EventLogEntryData> logs, List<RuleSuppression> activeSuppressions, CancellationToken ct)
    {
        if (_reportService == null || !RiskLevels.IsActionable(record.RiskLevel))
        {
            return null;
        }

        try
        {
            return await _reportService.GenerateAsync(record, logs, _serverDescription, activeSuppressions, ct: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"風險報告輸出失敗：{ex.Message}");
            Log.Error(ex, "風險報告輸出失敗：{Date:yyyy-MM-dd}", record.Date);
            return null;
        }
    }

    /// <summary>
    /// 4625 達暴力破解門檻時，條件式撈取當日 4624（成功登入），比對是否與失敗記錄同一組帳號/IP。
    /// 平時不收 4624（SuccessAudit 量極大），只在已有暴力破解訊號時才多查一次，兼顧偵測面與效能。
    /// </summary>
    private async Task<SuccessfulLogonMatch?> DetectSuccessfulLogonAfterBruteForceAsync(DateTime targetDate, List<EventLogEntryData> logs)
    {
        var failedMessages = logs
            .Where(l => l.LogName.Equals("Security", StringComparison.OrdinalIgnoreCase) && l.EventId == 4625)
            .Select(l => l.Message);
        var (failedAccounts, failedIps) = LogAggregator.ExtractAccountsAndIps(failedMessages);

        if (failedAccounts.Count == 0 && failedIps.Count == 0)
        {
            return null;
        }

        var scan = await Task.Run(() =>
            _eventLogService.ScanRange(targetDate.Date, targetDate.Date.AddDays(1), "Security", securityExtraEventIds: new[] { 4624 }));

        var logonSuccessMessages = scan.Entries.Where(l => l.EventId == 4624).Select(l => l.Message).ToList();

        // RDP 成功登入（LSM 21/25、RCM 1149）已在主掃描收進 logs（Operational 頻道 watchlist），
        // 一併納入成功登入面：暴力破解未必走 4624，也可能直接以 RDP 工作階段得手
        var rdpSuccessMessages = logs.Where(IsRdpSuccessLogon).Select(l => l.Message).ToList();

        var allSuccessMessages = logonSuccessMessages.Concat(rdpSuccessMessages).ToList();
        if (allSuccessMessages.Count == 0)
        {
            return null;
        }

        var (successAccounts, successIps) = LogAggregator.ExtractAccountsAndIps(allSuccessMessages);
        var matchedAccounts = successAccounts.Intersect(failedAccounts, StringComparer.OrdinalIgnoreCase).ToList();
        var matchedIps = successIps.Intersect(failedIps).ToList();

        if (matchedAccounts.Count == 0 && matchedIps.Count == 0)
        {
            return null;
        }

        // 判斷「得手途徑是否含 RDP」：僅當交集帳號/IP 確實出現在 RDP 成功面時才標註，避免 4624 得手也誤稱 RDP
        var (rdpAccounts, rdpIps) = LogAggregator.ExtractAccountsAndIps(rdpSuccessMessages);
        bool includesRdp = matchedAccounts.Any(a => rdpAccounts.Contains(a)) || matchedIps.Any(rdpIps.Contains);

        Log.Warn("{Date:yyyy-MM-dd} 偵測到破解得手跡象：大量登入失敗後同一組帳號/IP 出現成功登入（帳號={Accounts}，IP={Ips}，含RDP={Rdp}）",
            targetDate, string.Join(",", matchedAccounts), string.Join(",", matchedIps), includesRdp);

        return new SuccessfulLogonMatch { MatchedAccounts = matchedAccounts, MatchedIps = matchedIps, IncludesRdp = includesRdp };
    }

    /// <summary>RDP 成功登入事件：LocalSessionManager 21（登入）/25（重連），RemoteConnectionManager 1149（驗證成功）。</summary>
    private static bool IsRdpSuccessLogon(EventLogEntryData l) =>
        (l.LogName.Equals(ChannelCatalog.RdpLsmChannel, StringComparison.OrdinalIgnoreCase) && (l.EventId is 21 or 25)) ||
        (l.LogName.Equals(ChannelCatalog.RdpRcmChannel, StringComparison.OrdinalIgnoreCase) && l.EventId == 1149);

    /// <summary>
    /// 逐條列出因權限或來源限制而停用的偵測項目——覆蓋率誠實申報，而不是一句「讀取失敗」帶過。
    /// 只申報「被拒」（存在卻讀不到＝偵測盲區）；「頻道不存在」（該偵測本來就不適用於這台主機）
    /// 由 Program.cs 印到 console，不塞進這裡逐日重複，以免無 Defender/RDP 角色的主機每天噪音。
    /// </summary>
    private static List<string> BuildUncoveredChecks(bool? securityLogAvailable, ChannelAvailability? channels)
    {
        var checks = new List<string>();

        if (securityLogAvailable == false)
        {
            checks.Add("入侵跡象規則表（Security-Auditing 相關：登入失敗/帳戶鎖定/帳號建立/權限與角色異動等）未檢查");
            checks.Add("跨 log 關聯模式【入侵鏈】【持久化】【滅跡】【提權→植入】【跨日入侵鏈】【破解得手】【暴力破解→RDP 得手】未檢查（皆需要 Security log）");
            checks.Add("安全稽核事件總量趨勢比對未檢查");
        }

        if (channels != null)
        {
            if (channels.WasDenied(ChannelCatalog.DefenderChannel))
            {
                checks.Add("防毒（Microsoft Defender）頻道存取被拒：惡意程式偵測／防護遭關閉規則與【防護遭關閉→惡意程式】【惡意程式→持久化】關聯未檢查");
            }
            if (channels.WasDenied(ChannelCatalog.RdpLsmChannel) || channels.WasDenied(ChannelCatalog.RdpRcmChannel))
            {
                checks.Add("遠端桌面（RDP TerminalServices）頻道存取被拒：RDP 工作階段收集與【暴力破解→RDP 得手】關聯未檢查");
            }
        }

        return checks;
    }

    /// <summary>
    /// 程式判定的風險下限。被抑制的簽章不參與風險判定的旗標/High 門檻——抑制關的是
    /// 「要不要吵」，這裡正是「吵不吵」的判定點；關聯層（correlations）完全不受抑制影響，
    /// 單事件被關掉不代表組合出來的攻擊鏈/故障鏈訊號也該被關掉（見 docs/RULES-SPEC.md 語意邊界）。
    /// docs/archive/HISTORY.md #1（B1 三級化）：原本看 Severity==Critical 判定高風險日，
    /// 三級化後嚴重度封頂 High，改看 ElevatesDayRisk 旗標——判定行為完全不變。
    /// </summary>
    internal static string ComputeRuleBasedRisk(List<LogIssueSignature> issues, List<string> trendAlerts,
        List<CorrelationFinding> correlations)
    {
        if (issues.Any(i => !i.Suppressed && i.ElevatesDayRisk) ||
            correlations.Any(c => c.ElevatesDayRisk))
        {
            return RiskLevels.High;
        }

        if (trendAlerts.Count > 0 || correlations.Count > 0 || issues.Any(i => !i.Suppressed && i.Severity == IssueSeverity.High))
        {
            return RiskLevels.Medium;
        }

        return RiskLevels.Low;
    }

    /// <summary>
    /// 程式判定依據的代碼（docs/archive/HISTORY.md #11）：純顯示用途，說明「為什麼是這個
    /// 風險等級」，不影響任何判定邏輯。與 ComputeRuleBasedRisk 判斷同一組條件，只是額外指名
    /// 是哪一條規則/哪一種訊號觸發的。呼叫端在 AI 把風險往上拉時會覆寫成 "ai_raise"。
    /// </summary>
    private static string? DescribeRiskBasis(
        List<LogIssueSignature> issues, List<CorrelationFinding> correlations, List<string> trendAlerts, string ruleRisk)
    {
        if (ruleRisk == RiskLevels.High)
        {
            var flagged = issues.FirstOrDefault(i => !i.Suppressed && i.ElevatesDayRisk);
            if (flagged != null) return $"rule:{flagged.Source} EventId {flagged.EventId}";
            return correlations.Any(c => c.ElevatesDayRisk) ? "correlation" : "rule";
        }

        if (ruleRisk == RiskLevels.Medium)
        {
            if (trendAlerts.Count > 0) return "trend";
            if (correlations.Count > 0) return "correlation";
            var high = issues.FirstOrDefault(i => !i.Suppressed && i.Severity == IssueSeverity.High);
            return high != null ? $"high_issue:{high.Source} EventId {high.EventId}" : "medium";
        }

        return null;   // 低風險：沒有明確依據可講
    }
}
