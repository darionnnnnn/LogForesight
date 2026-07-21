using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 每日分析流程：取多來源 log → 聚合 → 規則分類 → 帶入近期歷史 → 呼叫 AI 白話翻譯 → 寫回歷史。
/// 設計原則見 docs/AI-ROLE-PLAN.md：規則/趨勢/關聯三層負責偵測與風險判定（確定性、AI 判斷只能
/// 把風險往上拉不能往下壓），AI 負責把這些結論翻譯成白話——低風險日（三層皆無訊號）不呼叫 AI。
/// </summary>
public class LogAnalysisService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // ── prompt 呈現上限 ──────────────────────────────────────────
    // 歷史資料庫照存完整資訊，這裡只限制「進 prompt 的量」：異常大量的日子（如硬碟垂死、
    // 遭受攻擊）事件種類會暴增，不設上限的話 prompt 可膨脹到 25KB 以上，稀釋小模型注意力。
    // 列表皆已依嚴重度排序，被折疊的一定是相對不重要的項目，且統計數字不受影響。
    internal const int MaxFlaggedInPrompt = 12;  // 規則命中問題最多逐項列 12 個
    internal const int MaxOthersInPrompt = 10;   // 未命中規則事件最多逐項列 10 個
    private const int MaxTrendAlertsInPrompt = 15;

    /// <summary>前置掃描每批的項目數（每批一次獨立 AI 呼叫，prompt 約 5KB）</summary>
    private const int ScreeningChunkSize = 20;

    /// <summary>
    /// 低風險日仍執行前置掃描的未分類事件種類門檻。低於此值的低風險日維持零 AI 呼叫；
    /// 達到此值代表當日有異常大量的未分類事件，規則層依定義沒看過它們，值得付出一次掃描成本。
    /// </summary>
    private const int MinTailForLowRiskScreening = 20;
    private const int FlaggedSampleCount = 2;    // 重點問題每項附 2 則範例訊息
    private const int OtherSampleCount = 1;      // 其他事件每項附 1 則

    /// <summary>敘事欄位（story/trend_story/action）的合理長度上限。這些是一到兩句話的白話敘述，
    /// 不該超過這個長度，超過視為模型異常重複輸出（JSON 語法可能仍合法，但內容不合理），觸發 JSON 重試</summary>
    private const int MaxSummaryChars = 600;

    /// <summary>標題欄位（headline）的長度上限——比敘事欄位更短，一句話而非一段話</summary>
    private const int MaxHeadlineChars = 60;

    /// <summary>
    /// 2026-07-20 AI 角色轉換（見 docs/AI-ROLE-PLAN.md）：AI 不再是判斷風險或找根因的分析引擎，
    /// 那些已由規則/趨勢/關聯三層與 KnownIssueCatalog 的靜態知識庫負責。AI 唯一的職責是把
    /// 這些已經算好的結論翻譯成不懂 Event Log 的人也能看懂的白話——risk_level 仍要填，但只作為
    /// 安全網（只能把風險往上拉，不能往下壓，見 MoreSevere），不是重新判斷的依據。
    /// </summary>
    private const string SystemPrompt =
        "你是資深 Windows Server 維運與資安分析師，同時也是把技術判讀翻譯成白話的溝通者。" +
        "以下資料已由程式完成規則比對、趨勢分析與風險判定，你的工作分兩部分：" +
        "(1) 依專業判斷填寫 risk_level，但這只是輔助判斷、不會讓程式判定的風險等級降低；" +
        "(2) 把結論轉譯成不懂 Event Log 的管理者也能看懂的白話——不要引用 Event ID 或程式碼層級術語，" +
        "只根據使用者提供的資料撰寫，不要臆測資料中不存在的事件。全程使用繁體中文。" +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出一個符合使用者指定結構的 JSON 物件。";

    private readonly EventLogService _eventLogService;
    private readonly AIService _aiService;
    private readonly IAnalysisRecordStore _historyService;
    private readonly ISuppressionStore _suppressionStore;
    private readonly RiskReportService? _reportService;
    private readonly string _serverDescription;
    private readonly string _host;
    private readonly long _hostId;

    /// <param name="suppressionStore">主機級告警抑制設定（見 docs/RULES-PLAN.md）：只影響「要不要吵」
    /// （通知、風險升級），偵測與紀錄照常——事件照樣聚合、命中規則、寫入歷史，只是不進告警清單、不拉高風險</param>
    /// <param name="serverDescription">伺服器角色描述（如「AD 網域控制站」），會帶入 prompt 讓 AI 依環境判讀；空字串則略過</param>
    /// <param name="reportService">提供時，風險「中」以上的日期會輸出 export/{日期}.txt 風險報告</param>
    /// <param name="host">寫入紀錄的主機名稱；null/空字串時預設為 Environment.MachineName（本機情境的自然值）</param>
    /// <param name="hostId">寫入紀錄的主機 PK（主機清單登記後取得）。**紀錄與主機的關聯鍵**；
    /// 0＝取不到主機列時的降級，查詢端會退回以主機名稱比對，分析本身不受影響</param>
    public LogAnalysisService(EventLogService eventLogService, AIService aiService, IAnalysisRecordStore historyService,
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
    }

    /// <summary>自行抓取當日 log 後分析（單日情境用）</summary>
    public Task<DailyAnalysisRecord> AnalyzeDayAsync(DateTime targetDate, bool useAi = true, string[]? logNames = null, int historyDays = 14)
        => AnalyzeDayAsync(targetDate, _eventLogService.GetEventLogsFromAll(targetDate, logNames), useAi, historyDays);

    /// <summary>
    /// 分析已抓取好的當日 log（回補多天時用：log 由呼叫端一次掃描、預先分桶，
    /// 分析迴圈不需等待任何 Event Log I/O，只等 AI 推論）
    /// </summary>
    /// <param name="useAi">false = 統計模式：聚合、規則分類、趨勢比對照常執行，但不呼叫 AI</param>
    /// <param name="dataIncomplete">true = 本日事件來源不完整（如 Event Log 回補時已被覆蓋），寫入紀錄供趨勢基準排除</param>
    /// <param name="securityLogAvailable">本次執行 Security log 是否成功讀取；false 時停用相關規則層偵測、
    /// 相關關聯模式改標記「未檢查」，並在趨勢基準計算時排除本日的 Security 簽章</param>
    public async Task<DailyAnalysisRecord> AnalyzeDayAsync(DateTime targetDate, List<EventLogEntryData> logs, bool useAi = true,
        int historyDays = 14, bool dataIncomplete = false, bool? securityLogAvailable = true)
    {
        var sw = Stopwatch.StartNew();
        Log.Info("開始分析 {Date:yyyy-MM-dd}：log 筆數={LogCount}, useAi={UseAi}", targetDate, logs.Count, useAi);

        var issues = LogAggregator.Aggregate(logs);

        // 主機級告警抑制（見 docs/RULES-PLAN.md）：只標記「這個簽章命中的規則被本機抑制」，
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

        // 慢速趨勢偵測（2026-07-20，見 docs/AI-ROLE-PLAN.md）：近 7 天 vs 前 7 天總量比較，
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

        // 程式判定的風險下限：規則或關聯鏈命中 Critical → 高；有 High 問題/頻率異常/關聯訊號 → 中
        var ruleRisk = ComputeRuleBasedRisk(issues, trendAlerts, correlations);
        bool lowRisk = ruleRisk == "低";

        // 前置掃描：Other 類事件種類超過主 prompt 呈現上限時，超出的項目先分批給獨立的
        // AI 呼叫逐項篩選（這些項目彼此不需要一起看，適合拆分），值得注意的帶著掃描意見
        // 回流主分析——主呼叫維持全局判讀，不因折疊漏看、也不因塞滿明細稀釋注意力。
        //
        // 低風險日原則上完全不呼叫 AI（見下方 skipAiForLowRisk），但「三層皆無訊號、卻有大量
        // 未分類事件」的日子是唯一的例外：那些事件規則層依定義沒看過，若連掃描都不做就沒有任何
        // 一層檢視過它們。門檻 MinTailForLowRiskScreening 讓一般的低風險日仍維持零 AI 呼叫，
        // 只有未分類種類異常多時才付出掃描成本（2026-07-20 審查後補上，見 docs/AI-ROLE-PLAN.md）。
        ScreeningOutcome? screening = null;
        var tailIssues = GetTailIssues(issues);
        bool shouldScreen = tailIssues.Count > 0 &&
                            (!lowRisk || tailIssues.Count >= MinTailForLowRiskScreening);
        if (useAi && shouldScreen)
        {
            Console.WriteLine($"  事件種類較多，前置掃描 {tailIssues.Count} 項未分類項目...");
            screening = await ScreenTailAsync(targetDate, tailIssues);
            Log.Info("前置掃描完成：共 {Total} 項，值得注意 {Notable} 項，一般雜訊 {Clean} 項，掃描失敗 {Failed} 項",
                tailIssues.Count, screening.Notable.Count, screening.CleanCount, screening.FailedCount);
        }

        // 低風險日（四層皆無訊號）不呼叫 AI：沒有訊號就沒有故事可講，白話翻譯的價值趨近於零，
        // 2026-07-20 AI 角色轉換——2000 台規模下這是 AI 時間預算能否成立的關鍵之一。
        // 但前置掃描若在未分類事件裡找到值得注意的項目，仍要跑主分析——掃描結果必須能拉高當日
        // 風險等級（MoreSevere），否則掃描發現的異常只會躺在 ScreeningNotes 裡不影響任何判定。
        // 沿用既有 AiAnalyzed=false 的統計模式語意，只是原因從「AI 失敗」變成「本日不需要」。
        bool skipAiForLowRisk = useAi && lowRisk && (screening?.Notable.Count ?? 0) == 0;
        if (skipAiForLowRisk)
        {
            useAi = false;
        }

        string riskLevel = ruleRisk;
        string headline = skipAiForLowRisk ? "今日狀況正常，無需處理" : "（統計模式紀錄，未呼叫 AI 分析）";
        string summary = skipAiForLowRisk
            ? "今日無異常訊號，規則/趨勢/慢速趨勢/關聯四層檢查全數通過。"
            : "（統計模式紀錄，未呼叫 AI 分析）";
        string trendAssessment = string.Empty;
        string action = string.Empty;

        var uncoveredChecks = BuildUncoveredChecks(securityLogAvailable);

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

        if (useAi)
        {
            var prompt = BuildPrompt(targetDate, issues, errorCount, warningCount, auditCount, history, trendAlerts, correlations, screening,
                dataIncomplete, uncoveredChecks);

            // response_format=json_object 只保證「合法 JSON」，不保證是我們要的物件形狀
            // （模型可能回傳陣列、或欄位塞入異常冗長的重複文字）；驗證失敗會自動重新請求
            var result = await _aiService.ChatJsonAsync<AiAnalysisResult>(prompt, SystemPrompt,
                validate: r => r.RiskLevel.Length > 0 && r.Headline.Length > 0 && r.Story.Length > 0
                               && r.Headline.Length <= MaxHeadlineChars && r.Story.Length <= MaxSummaryChars
                               && r.TrendStory.Length <= MaxSummaryChars && r.Action.Length <= MaxSummaryChars,
                label: $"daily-{targetDate:yyyyMMdd}");

            if (result.Success)
            {
                headline = result.Value!.Headline;
                summary = result.Value.Story;
                trendAssessment = result.Value.TrendStory;
                action = result.Value.Action;
                // AI 判斷與程式判斷取較嚴重者：即使模型輕忽了，規則與趨勢比對的結論也會強制拉高風險等級
                riskLevel = MoreSevere(NormalizeRisk(result.Value.RiskLevel), ruleRisk);
            }
            else if (result.RawContent.Length > 0)
            {
                // 網路正常但重試 JsonRetryCount 次後仍不合格：保留原文（截斷避免報告膨脹），不當機、不遺失資訊；
                // 仍算完成 AI 分析（useAi 維持 true），只是白話翻譯品質降級
                headline = "AI 回覆格式異常，以下為原始內容";
                summary = $"（AI 回覆經 {result.Attempts} 次嘗試仍未通過 JSON 檢查，保留原文供參考）{Truncate(result.RawContent, MaxSummaryChars)}";
                riskLevel = MoreSevere(NormalizeRisk(result.RawContent), ruleRisk);
                Log.Warn("{Date:yyyy-MM-dd} 主分析降級為原文保留（{Attempts} 次嘗試仍未通過 JSON 檢查）", targetDate, result.Attempts);
            }
            else
            {
                // 重試耗盡仍完全失敗（如 llama.cpp 未啟動、網路不通）時降級為統計模式紀錄。
                // 偵測（規則/趨勢/關聯）與規則命中問題的處置建議（靜態知識庫）完全不受影響，
                // 只是少了白話摘要——降級語意刻意用正面表述，AI 已不是偵測的必要環節
                useAi = false;
                headline = "今日分析摘要暫缺（AI 服務未回應）";
                summary = $"偵測與處置建議仍完整，僅白話摘要因 AI 服務未回應而從缺（{result.Error}）。";
                Log.Error("{Date:yyyy-MM-dd} 主分析完全失敗，降級為統計模式：{Error}", targetDate, result.Error);
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
            RiskLevel = riskLevel,
            Headline = headline,
            Summary = summary,
            TrendAssessment = trendAssessment,
            Action = action,
            AiAnalyzed = useAi,
            ScreenedTailCount = screening != null ? tailIssues.Count : 0,
            ScreeningNotes = screening?.Notable
                .Select(n => $"{n.Issue.LogName}/{n.Issue.Source} EventId {n.Issue.EventId} x{n.Issue.Count}：{n.Reason}")
                .ToList() ?? new List<string>(),
            DataIncomplete = dataIncomplete,
            SecurityLogAvailable = securityLogAvailable,
            UncoveredChecks = uncoveredChecks
        };

        // 風險「中」以上輸出報告檔（含第二階段 AI 深入分析與原始 log），路徑一併寫入歷史
        if (_reportService != null && record.RiskLevel is "高" or "中")
        {
            try
            {
                record.ReportFile = await _reportService.GenerateAsync(record, logs, _serverDescription, activeSuppressions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"風險報告輸出失敗：{ex.Message}");
                Log.Error(ex, "風險報告輸出失敗：{Date:yyyy-MM-dd}", targetDate);
            }
        }

        _historyService.Append(record);

        Log.Info("完成分析 {Date:yyyy-MM-dd}：風險={Risk}, 錯誤={Errors}, 警告={Warnings}, 稽核={Audit}, " +
                 "aiAnalyzed={AiAnalyzed}, 耗時={ElapsedMs}ms, 報告檔={ReportFile}",
            targetDate, riskLevel, errorCount, warningCount, auditCount, useAi, sw.ElapsedMilliseconds, record.ReportFile ?? "(無)");

        return record;
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

        var successMessages = scan.Entries.Where(l => l.EventId == 4624).Select(l => l.Message).ToList();
        if (successMessages.Count == 0)
        {
            return null;
        }

        var (successAccounts, successIps) = LogAggregator.ExtractAccountsAndIps(successMessages);
        var matchedAccounts = successAccounts.Intersect(failedAccounts, StringComparer.OrdinalIgnoreCase).ToList();
        var matchedIps = successIps.Intersect(failedIps).ToList();

        if (matchedAccounts.Count == 0 && matchedIps.Count == 0)
        {
            return null;
        }

        Log.Warn("{Date:yyyy-MM-dd} 偵測到破解得手跡象：大量登入失敗後同一組帳號/IP 出現成功登入（帳號={Accounts}，IP={Ips}）",
            targetDate, string.Join(",", matchedAccounts), string.Join(",", matchedIps));

        return new SuccessfulLogonMatch { MatchedAccounts = matchedAccounts, MatchedIps = matchedIps };
    }

    /// <summary>Security 無權限時，逐條列出因此停用的偵測項目——覆蓋率誠實申報，而不是一句「讀取失敗」帶過</summary>
    private static List<string> BuildUncoveredChecks(bool? securityLogAvailable)
    {
        if (securityLogAvailable != false)
        {
            return new List<string>();
        }

        return new List<string>
        {
            "入侵跡象規則表（Security-Auditing 相關：登入失敗/帳戶鎖定/帳號建立/權限與角色異動等）未檢查",
            "跨 log 關聯模式【入侵鏈】【持久化】【滅跡】【提權→植入】【跨日入侵鏈】【破解得手】未檢查（皆需要 Security log）",
            "安全稽核事件總量趨勢比對未檢查"
        };
    }

    /// <summary>
    /// 超出主 prompt 呈現上限的 Other 類項目（前置掃描的對象；與 BuildPrompt 的分界一致）。
    /// 2026-07-20 AI 角色轉換後限縮：規則已命中的尾巴不再掃描——靜態知識庫已涵蓋處置建議，
    /// 不需要 AI 逐項篩選；只有 Other 類（未命中規則）才是 AI 唯一還需要判讀新型態問題的地方
    /// （見 docs/AI-ROLE-PLAN.md），與 RiskReportService 深析限縮到 Other 類的原則一致。
    /// </summary>
    private static List<LogIssueSignature> GetTailIssues(List<LogIssueSignature> issues)
    {
        return issues.Where(i => i.KnownIssue == null).Skip(MaxOthersInPrompt).ToList();
    }

    /// <summary>
    /// 前置掃描：分批請 AI 逐項篩選尾巴項目，只回報值得注意者。
    /// 批次之間彼此獨立（逐項判斷是否為雜訊不需要全局脈絡），所以可以安全拆分呼叫。
    /// </summary>
    private async Task<ScreeningOutcome> ScreenTailAsync(DateTime date, List<LogIssueSignature> tailIssues)
    {
        var outcome = new ScreeningOutcome();

        foreach (var chunk in tailIssues.Chunk(ScreeningChunkSize))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{date:yyyy-MM-dd} 的 Windows Server 事件種類較多，主分析前請先篩選以下較低嚴重度的事件。" +
                          "逐項判斷是否值得納入主分析（入侵跡象、故障前兆、不尋常的模式）；一般性雜訊不要列出。");
            sb.AppendLine();
            for (int i = 0; i < chunk.Length; i++)
            {
                var item = chunk[i];
                sb.AppendLine($"{i + 1}. [{item.Severity}] {item.LogName}/{item.Source} EventId {item.EventId} x{item.Count}" +
                              $"（{item.FirstSeen}~{item.LastSeen}）：" +
                              (item.KnownIssue != null ? $"{item.KnownIssue}；" : "") +
                              (item.SampleMessages.FirstOrDefault() ?? ""));
                if (item.KeyDetails != null)
                {
                    sb.AppendLine($"   {item.KeyDetails}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("請只回傳一個 JSON 物件（不要任何其他文字），no 為上列項目編號；全部屬一般雜訊時 notable 給空陣列：");
            sb.AppendLine("""{"notable": [{"no": 1, "reason": "為何值得注意"}]}""");

            var result = await _aiService.ChatJsonAsync<ScreeningResult>(sb.ToString(), SystemPrompt, label: $"screening-{date:yyyyMMdd}");
            var parsed = result.Value;

            if (parsed == null)
            {
                outcome.FailedCount += chunk.Length;
                continue;
            }

            int valid = 0;
            foreach (var n in parsed.Notable)
            {
                if (n.No >= 1 && n.No <= chunk.Length)
                {
                    outcome.Notable.Add((chunk[n.No - 1], n.Reason));
                    valid++;
                }
            }
            outcome.CleanCount += chunk.Length - valid;
        }

        return outcome;
    }

    private string BuildPrompt(DateTime date, List<LogIssueSignature> issues,
        int errorCount, int warningCount, int auditCount, List<DailyAnalysisRecord> history,
        List<string> trendAlerts, List<CorrelationFinding> correlations, ScreeningOutcome? screening,
        bool dataIncomplete, List<string> uncoveredChecks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"以下是 Windows Server 在 {date:yyyy-MM-dd}（{WeekdayZh(date)}）的事件日誌摘要" +
                      "（已聚合統計，且已由程式完成規則比對、趨勢分析與風險判定）。" +
                      "請依這些資料給出風險等級判斷，並把結論轉譯成白話讓不懂技術的人也能理解，" +
                      "特別注意硬體故障前兆與入侵跡象。");

        if (_serverDescription.Length > 0)
        {
            sb.AppendLine($"【伺服器環境】{_serverDescription}");
        }

        if (uncoveredChecks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【本次未能檢查的項目】（權限或來源限制，非「已檢查且無異常」，判讀時請留意這是偵測盲區）");
            foreach (var check in uncoveredChecks)
            {
                sb.AppendLine($"- {check}");
            }
        }

        if (dataIncomplete)
        {
            sb.AppendLine();
            sb.AppendLine("【資料完整性提醒】本日部分事件來源的保留歷史不足以涵蓋整天，統計數字可能偏低，非真實反映當日狀況。");
        }

        sb.AppendLine();
        sb.AppendLine($"【當日統計】錯誤 {errorCount} 筆、警告 {warningCount} 筆、安全稽核事件 {auditCount} 筆");

        if (trendAlerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【程式比對出的頻率異常】（程式已用歷史次數確定性比對，這些不是猜測，請務必納入評估）");
            foreach (var alert in trendAlerts.Take(MaxTrendAlertsInPrompt))
            {
                sb.AppendLine($"- {alert}");
            }
            if (trendAlerts.Count > MaxTrendAlertsInPrompt)
            {
                sb.AppendLine($"（另有 {trendAlerts.Count - MaxTrendAlertsInPrompt} 項頻率異常未逐項列出）");
            }
        }

        if (correlations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【程式比對出的關聯訊號】（多個獨立事件的已知攻擊鏈/故障鏈組合，由程式確定性比對，" +
                          "不是猜測——這些關聯是本次分析最重要的線索，風險評估與趨勢解讀必須以此為核心）");
            foreach (var c in correlations)
            {
                sb.AppendLine($"- [{c.Severity}] {c.Description}");
            }
        }

        var flagged = issues.Where(i => i.KnownIssue != null).ToList();
        var others = issues.Where(i => i.KnownIssue == null).ToList();

        if (flagged.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【規則已標記的重點問題】（程式依已知危險事件比對，請優先評估）");
            foreach (var i in flagged.Take(MaxFlaggedInPrompt))
            {
                AppendIssue(sb, i, history.Count, flagged: true);
            }
            // 規則命中的尾巴不再前置掃描（2026-07-20 限縮，見 GetTailIssues）——靜態知識庫已涵蓋
            // 處置建議，這裡固定顯示折疊統計行，不像 Other 類尾巴有掃描結果可以彙報
            if (flagged.Count > MaxFlaggedInPrompt)
            {
                var folded = flagged.Skip(MaxFlaggedInPrompt).ToList();
                sb.AppendLine($"（另有 {folded.Count} 個嚴重度較低的規則命中問題共 {folded.Sum(i => i.Count)} 筆，未逐項列出——處置建議見報告的「處置參考（知識庫）」區塊）");
            }
        }

        if (others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【其他事件】（未命中已知規則，請判斷是否有新型態問題）");
            foreach (var i in others.Take(MaxOthersInPrompt))
            {
                AppendIssue(sb, i, history.Count, flagged: false);
            }
            if (others.Count > MaxOthersInPrompt && screening == null)
            {
                var folded = others.Skip(MaxOthersInPrompt).ToList();
                sb.AppendLine($"（另有 {folded.Count} 種其他事件共 {folded.Sum(i => i.Count)} 筆，未逐項列出）");
            }
        }

        if (screening != null)
        {
            sb.AppendLine();
            sb.AppendLine("【前置掃描結果】（超出上方篇幅的較低嚴重度項目，已先由獨立 AI 呼叫逐項檢視）");
            foreach (var (issue, reason) in screening.Notable)
            {
                AppendIssue(sb, issue, history.Count, flagged: issue.KnownIssue != null);
                sb.AppendLine($"  掃描意見：{reason}");
            }
            if (screening.Notable.Count == 0 && screening.CleanCount > 0)
            {
                sb.AppendLine($"- 已檢視 {screening.CleanCount} 項，皆判定為一般雜訊。");
            }
            else if (screening.CleanCount > 0)
            {
                sb.AppendLine($"（另 {screening.CleanCount} 項經檢視判定為一般雜訊）");
            }
            if (screening.FailedCount > 0)
            {
                sb.AppendLine($"（{screening.FailedCount} 項掃描失敗未經檢視，僅計入當日統計）");
            }
        }

        if (issues.Count == 0)
        {
            sb.AppendLine("（當日無錯誤、警告或需注意的稽核事件）");
        }

        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【近期歷史】（每日總量變化參考；注意星期規律，例如每週固定維護重開機屬正常模式）");
            foreach (var h in history)
            {
                var topKeys = string.Join("、", h.TopIssues
                    .Where(i => i.Severity >= IssueSeverity.Medium)
                    .Take(3)
                    .Select(i => $"{i.Source}#{i.EventId}x{i.Count}"));

                sb.Append($"- {h.Date:MM-dd}({WeekdayZh(h.Date)})：錯誤{h.ErrorCount} 警告{h.WarningCount} 稽核{h.AuditEventCount} 風險{h.RiskLevel}");
                if (topKeys.Length > 0)
                {
                    sb.Append($" 重點:{topKeys}");
                }
                // 非低風險日附上當日 AI 結論，讓模型看得到先前判讀的語意脈絡（是否已知原因、是否已處理）
                if (h.RiskLevel is "高" or "中" && h.AiAnalyzed && h.Summary.Length > 0)
                {
                    sb.Append($" 當日結論:{Truncate(h.Summary, 80)}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("請只回傳一個 JSON 物件（不要 markdown 圍欄、不要任何其他文字），結構如下：");
        sb.AppendLine("""
{
  "risk_level": "低、中、高 擇一（輔助判斷，不會讓程式判定的風險等級降低）",
  "headline": "一句話標題，讓不懂 Event Log 的人一眼看懂今天的狀況",
  "story": "用白話講清楚今天發生了什麼，避免使用 Event ID 或程式碼層級的專有術語",
  "trend_story": "依據上方頻率比對結果，這是新問題、正在惡化、還是延續中的已知問題，用白話接續之前的脈絡講",
  "action": "現在該做什麼、多急迫，例如「今天就要處理」「本週內確認」「持續觀察即可」"
}
""");

        return sb.ToString();
    }

    /// <summary>
    /// 輸出單一事件的完整資訊：嚴重度、發生時段（集中爆發 vs 全天零星）、趨勢比對數字、
    /// 訊息多樣性（同一問題重複 vs 多個不同對象）、Security 事件的帳號/IP 彙總
    /// </summary>
    private static void AppendIssue(StringBuilder sb, LogIssueSignature i, int historyDays, bool flagged)
    {
        var head = flagged ? $"[{i.Severity}/{i.Category}]" : $"[{EntryTypeText(i)}]";
        var time = i.Count > 1 ? $"（{i.FirstSeen}~{i.LastSeen}）" : $"（{i.FirstSeen}）";
        var known = flagged ? $"：{i.KnownIssue}" : "";
        sb.AppendLine($"- {head} {i.LogName}/{i.Source} EventId {i.EventId} x{i.Count}{time}{TrendText(i, historyDays)}{known}");

        // 歷史存 3 則範例，prompt 只放部分控制長度（重點問題 2 則、其他 1 則）；完整範例在風險報告與歷史紀錄
        var sampleCount = flagged ? FlaggedSampleCount : OtherSampleCount;
        var variety = i.DistinctMessageCount > 1 ? $"（共 {i.DistinctMessageCount} 種不同內容）" : "";
        sb.AppendLine($"  範例訊息{variety}：{string.Join(" ｜ ", i.SampleMessages.Take(sampleCount))}");

        if (i.KeyDetails != null)
        {
            sb.AppendLine($"  {i.KeyDetails}");
        }
    }

    /// <summary>EntryType 0 是 classic API 讀到的 Critical 等級事件，顯示為 Critical 而非數字</summary>
    private static string EntryTypeText(LogIssueSignature i) =>
        (int)i.EntryType == 0 ? "Critical" : i.EntryType.ToString();

    private static string WeekdayZh(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "週一",
        DayOfWeek.Tuesday => "週二",
        DayOfWeek.Wednesday => "週三",
        DayOfWeek.Thursday => "週四",
        DayOfWeek.Friday => "週五",
        DayOfWeek.Saturday => "週六",
        _ => "週日"
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>把 TrendAnalyzer 算好的比對數字附註在事件行後面，模型只需解讀、不需自己算</summary>
    private static string TrendText(LogIssueSignature i, int historyDays)
    {
        return i.Trend switch
        {
            IssueTrend.New => "（首次出現，歷史中從未發生）",
            IssueTrend.Rising => $"（頻率上升：近{historyDays}日平均 x{i.HistoryDailyAverage}" +
                                 (i.PreviousDayCount != null ? $"、昨日 x{i.PreviousDayCount}" : "") + "）",
            IssueTrend.Recurring => $"（重複出現：近{historyDays}日中 {i.DaysSeenInHistory} 天有發生，平均 x{i.HistoryDailyAverage}）",
            IssueTrend.Declining => $"（頻率下降：近{historyDays}日平均 x{i.HistoryDailyAverage}）",
            _ => ""
        };
    }

    /// <summary>
    /// 程式判定的風險下限。被抑制的簽章不參與風險判定的 Critical/High 門檻——抑制關的是
    /// 「要不要吵」，這裡正是「吵不吵」的判定點；關聯層（correlations）完全不受抑制影響，
    /// 單事件被關掉不代表組合出來的攻擊鏈/故障鏈訊號也該被關掉（見 docs/RULES-PLAN.md 語意邊界）。
    /// </summary>
    internal static string ComputeRuleBasedRisk(List<LogIssueSignature> issues, List<string> trendAlerts,
        List<CorrelationFinding> correlations)
    {
        if (issues.Any(i => !i.Suppressed && i.Severity == IssueSeverity.Critical) ||
            correlations.Any(c => c.Severity == IssueSeverity.Critical))
        {
            return "高";
        }

        if (trendAlerts.Count > 0 || correlations.Count > 0 || issues.Any(i => !i.Suppressed && i.Severity == IssueSeverity.High))
        {
            return "中";
        }

        return "低";
    }

    private static string MoreSevere(string a, string b)
    {
        static int Rank(string level) => level switch { "高" => 3, "中" => 2, "低" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    /// <summary>從 AI 回傳的風險等級文字（或 JSON 解析失敗時的原文）歸一化為 高/中/低/未知</summary>
    private static string NormalizeRisk(string text)
    {
        foreach (var level in new[] { "高", "中", "低" })
        {
            if (text.Contains(level))
            {
                return level;
            }
        }

        return "未知";
    }

    /// <summary>前置掃描的彙總結果</summary>
    private class ScreeningOutcome
    {
        public List<(LogIssueSignature Issue, string Reason)> Notable { get; } = new();
        public int CleanCount { get; set; }
        public int FailedCount { get; set; }
    }

    /// <summary>前置掃描呼叫的 JSON 契約</summary>
    private class ScreeningResult
    {
        [JsonPropertyName("notable")]
        public List<ScreeningItem> Notable { get; set; } = new();
    }

    private class ScreeningItem
    {
        [JsonPropertyName("no")]
        public int No { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
