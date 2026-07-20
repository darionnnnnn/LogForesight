using System.Text.Json.Serialization;

namespace LogForesight;

public class DailyAnalysisRecord
{
    public DateTime Date { get; set; }

    /// <summary>
    /// 產生本筆紀錄的主機（本機直讀＝Environment.MachineName；未來 NetIQ 主機＝Sentinel 回報的主機識別）。
    /// 現階段單機情境下這個欄位本身不影響任何邏輯，是為 DB 匯入與多主機階段預先準備——
    /// 屆時匯入器直接讀這個欄位即知道每筆紀錄屬於哪台主機，不用從檔名或設定檔反推。
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }
    public List<LogIssueSignature> TopIssues { get; set; } = new();

    /// <summary>程式比對歷史後偵測到的頻率異常（首次出現、頻率上升、整體錯誤量突增）</summary>
    public List<string> TrendAlerts { get; set; } = new();

    /// <summary>跨 log 關聯訊號：多個獨立事件的已知攻擊鏈/故障鏈組合（CorrelationAnalyzer 確定性比對）</summary>
    public List<string> CorrelationAlerts { get; set; } = new();

    public string RiskLevel { get; set; } = string.Empty;

    // AI 回傳的白話翻譯結果（JSON 契約解析後的欄位，2026-07-20 AI 角色轉換）。
    // 偵測與風險判定完全由規則/趨勢/關聯三層負責（見 KnownIssueCatalog／TrendAnalyzer／CorrelationAnalyzer），
    // 這裡是「把結論講成人話」的產出，AI 服務不可用時從缺不影響風險等級或處置建議
    // （規則命中問題的處置建議來自 KnownIssueCatalog 的靜態知識庫，見 RiskReportService）。

    /// <summary>一句話標題，讓不懂 Event Log 的人一眼看懂今天的狀況</summary>
    public string Headline { get; set; } = string.Empty;

    /// <summary>今天發生什麼的白話敘述（沿用既有欄位名，序列化格式不變）</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>這是新問題、正在惡化、還是延續中的已知問題——接續前幾天脈絡講</summary>
    public string TrendAssessment { get; set; } = string.Empty;

    /// <summary>現在該做什麼、多急迫（取代原本的多項 Recommendations 清單）</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>false = 統計模式紀錄（AI 未呼叫或呼叫失敗時的降級紀錄）</summary>
    public bool AiAnalyzed { get; set; } = true;

    /// <summary>前置掃描檢視的低嚴重度項目數（0 = 當日事件種類未超過主 prompt 上限，未觸發掃描）</summary>
    public int ScreenedTailCount { get; set; }

    /// <summary>前置掃描判定值得注意的項目（事件 + AI 掃描意見），已回流主分析一併判讀</summary>
    public List<string> ScreeningNotes { get; set; } = new();

    /// <summary>風險「中」以上時輸出的報告檔參照（今日為 export/{日期}_*.txt 路徑，未來 DB 後端可能是資料表 id），無風險為 null</summary>
    public string? ReportFile { get; set; }

    /// <summary>
    /// true = 本日事件來源不完整（例如回補時 Event Log 已被系統覆蓋、只能取得部分時段），
    /// 趨勢基準（TrendAnalyzer 的近期平均）計算時應排除這一天，避免用不完整的天墊低/墊高平均值。
    /// </summary>
    public bool DataIncomplete { get; set; }

    /// <summary>
    /// 本次執行是否成功讀取 Security log；null = 本次未嘗試（理論上不會發生，保留給未來來源擴充時的預設值）。
    /// false 時，Security 相關簽章不計入 TrendAnalyzer 的歷史基準，且 UncoveredChecks 會列出因此停用的偵測項目。
    /// </summary>
    public bool? SecurityLogAvailable { get; set; }

    /// <summary>因權限或來源限制而未能執行的偵測項目說明（如「無 Security 權限，入侵跡象規則表與相關關聯模式未檢查」）</summary>
    public List<string> UncoveredChecks { get; set; } = new();

    /// <summary>本次執行若剛好做了體檢（due-date 到期，見 WeeklyCheckupService），結果附掛於當天紀錄；平常日為 null</summary>
    public WeeklyCheckupResult? WeeklyCheckup { get; set; }

    /// <summary>
    /// 各類別的 AI 深入分析結構化結果（風險「中」以上才有）。與報告全文（ReportFile）並存但目的不同：
    /// 報告全文是給人看的完整排版，這裡是給未來 DB／查詢/問答用的結構化資料，兩者由同一次深析呼叫產生，
    /// 不需要事後從文字反解析。低風險日恆為空清單（該日從不觸發深析）。
    /// </summary>
    public List<CategoryDeepDive> DeepDives { get; set; } = new();
}

/// <summary>單一類別（儲存裝置/硬體/安全…）的深入分析結果</summary>
public class CategoryDeepDive
{
    // 與 LogIssueSignature 的列舉欄位一致，存字串（"Storage"）而非整數——
    // 這是 docs/DB-PLAN.md 一致性機制 #5：兩後端逐字一致，未來 DB 匯入直接對應字串類別，不用反查數字
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueCategory Category { get; set; }
    public List<DeepDiveFinding> Findings { get; set; } = new();
}

/// <summary>單一問題的深入分析——欄位對應 RiskReportService 深析呼叫的 JSON 契約，但這是儲存端的獨立模型
/// （不隨 AI 回應的 JsonPropertyName 命名走），AI 契約要調整時不會牽動這裡</summary>
public class DeepDiveFinding
{
    public string Problem { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public string Impact { get; set; } = string.Empty;
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>
/// 體檢（週期性回顧，補「慢速趨勢躲在每日 2 倍門檻下」的盲點）的結論。
/// 2026-07-20 重設計：「發現」職責已移交每日確定性的 SlowTrendAnalyzer，體檢只負責「講故事」——
/// 詳見 docs/PLAN.md「核心設計決策 B」與 docs/AI-ROLE-PLAN.md。
/// </summary>
public class WeeklyCheckupResult
{
    public DateTime CheckupDate { get; set; }

    /// <summary>
    /// false = 窗口內三層皆無訊號（確定性閘門判定，不呼叫 AI），不輸出獨立報告檔。
    /// true 時必定是閘門已判定窗口內有值得回顧的訊號，AI 只負責把它講成一段回顧文字。
    /// </summary>
    public bool HasFindings { get; set; }

    public string Conclusion { get; set; } = string.Empty;

    /// <summary>有發現時輸出的週檢報告檔參照，無發現為 null</summary>
    public string? ReportFile { get; set; }

    /// <summary>
    /// AI 呼叫是否成功完成（不論有無發現）。false = 本次體檢因 AI 失敗未真正執行，
    /// 呼叫端不應寫入歷史——否則會消耗掉這一週的體檢額度，讓「排程失敗時自動補跑」失效。
    /// 不序列化：能被寫進歷史的體檢，依定義就是已完成的，讀回時預設 true 即正確。
    /// </summary>
    [JsonIgnore]
    public bool Completed { get; set; } = true;
}
