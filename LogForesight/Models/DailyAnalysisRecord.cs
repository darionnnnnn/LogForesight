using System.Text.Json.Serialization;

namespace LogForesight;

public class DailyAnalysisRecord
{
    public DateTime Date { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }
    public List<LogIssueSignature> TopIssues { get; set; } = new();

    /// <summary>程式比對歷史後偵測到的頻率異常（首次出現、頻率上升、整體錯誤量突增）</summary>
    public List<string> TrendAlerts { get; set; } = new();

    /// <summary>跨 log 關聯訊號：多個獨立事件的已知攻擊鏈/故障鏈組合（CorrelationAnalyzer 確定性比對）</summary>
    public List<string> CorrelationAlerts { get; set; } = new();

    public string RiskLevel { get; set; } = string.Empty;

    // AI 回傳的結構化結果（JSON 契約解析後的欄位），後續接 mail/webhook 可直接取用
    public string Summary { get; set; } = string.Empty;
    public string TrendAssessment { get; set; } = string.Empty;
    public List<string> Recommendations { get; set; } = new();

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

    /// <summary>本次執行若剛好做了每週體檢，結果附掛於當天紀錄；平常日為 null</summary>
    public WeeklyCheckupResult? WeeklyCheckup { get; set; }
}

/// <summary>每週體檢（週對週回顧，補「慢速趨勢躲在每日 2 倍門檻下」的盲點）的結論</summary>
public class WeeklyCheckupResult
{
    public DateTime CheckupDate { get; set; }

    /// <summary>false = AI 判讀本週無值得額外提出的發現，不輸出獨立報告檔</summary>
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
