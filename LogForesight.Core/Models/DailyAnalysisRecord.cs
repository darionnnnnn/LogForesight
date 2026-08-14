using System.Text.Json.Serialization;

namespace LogForesight.Core.Models;

public class DailyAnalysisRecord
{
    public DateTime Date { get; set; }

    /// <summary>
    /// 產生本筆紀錄的主機 PK（↔ lf_daily_records.host_id）。**這是紀錄與主機的關聯鍵**——
    /// 主機改名、換 IP、搬遷 Sentinel 都不影響歸戶，因為身分是這個號碼而不是任何一段文字。
    ///
    /// 0 = 本欄位問世前寫入的舊紀錄，或批次當下取不到主機列（hosts.json 寫入失敗）的降級；
    /// 這兩種情況查詢端一律退回以 <see cref="Host"/> 字串比對，見 <see cref="HostMatcher"/>。
    /// </summary>
    public long HostId { get; set; }

    /// <summary>
    /// 產生本筆紀錄的主機名稱（本機直讀＝Environment.MachineName；NetIQ 主機＝清單登錄的識別字串）。
    /// **寫入當下的顯示名快照**：關聯鍵是 <see cref="HostId"/>，這個欄位的用途是
    /// (1) 主機列遺失時人仍辨認得出這筆紀錄屬於誰、(2) 舊紀錄的 fallback 比對。
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }
    public List<LogIssueSignature> TopIssues { get; set; } = new();

    /// <summary>程式比對歷史後偵測到的頻率異常（首次出現、頻率上升、整體錯誤量突增）。
    /// 已排除本機抑制設定關掉的項目（回饋十五輪 A）——被抑制的仍照算趨勢欄位與嚴重度升級，
    /// 只是不出現在這份清單，語意邊界見 docs/RULES-SPEC.md。</summary>
    public List<string> TrendAlerts { get; set; } = new();

    /// <summary>跨 log 關聯訊號：多個獨立事件的已知攻擊鏈/故障鏈組合（CorrelationAnalyzer 確定性比對）。
    /// 已排除本機抑制設定關掉的模式（回饋十五輪 A）。</summary>
    public List<string> CorrelationAlerts { get; set; } = new();

    /// <summary>
    /// <see cref="TrendAlerts"/> 的結構化平行資料（回饋十五輪 A-5／C-1）：供詳情頁頁內導航
    /// （點擊捲到對應問題分節）。舊紀錄為空清單，前端據此降級回純文字顯示。
    /// </summary>
    public List<TrendAlertRef> TrendAlertRefs { get; set; } = new();

    /// <summary><see cref="CorrelationAlerts"/> 的結構化平行資料（回饋十五輪 A-5／C-1）：
    /// 供詳情頁的模式說明 popover 與「抑制此模式」出口。舊紀錄為空清單。</summary>
    public List<CorrelationAlertRef> CorrelationAlertRefs { get; set; } = new();

    /// <summary>
    /// 因抑制設定而未進入 <see cref="TrendAlerts"/> 的告警文字（回饋十五輪 A）：抑制關的是
    /// 「要不要吵」不是「要不要記」，這份清單讓詳情頁的「已抑制的告警」區塊誠實申報「暫時關掉
    /// 的東西其實還在發生」，而不是讓它們無聲消失。涵蓋簽章層（首次出現／頻率上升）與總量層
    /// （整體錯誤量／安全稽核事件量突增）兩種抑制。舊紀錄為空清單。
    /// </summary>
    public List<string> SuppressedTrendAlerts { get; set; } = new();

    /// <summary>因抑制設定而未進入 <see cref="CorrelationAlerts"/> 的告警文字（回饋十五輪 A），
    /// 語意與 <see cref="SuppressedTrendAlerts"/> 對稱。舊紀錄為空清單。</summary>
    public List<string> SuppressedCorrelationAlerts { get; set; } = new();

    public string RiskLevel { get; set; } = string.Empty;

    /// <summary>
    /// 日風險等級的判定依據代碼（docs/archive/HISTORY.md #11）：null＝本欄位問世前寫入的
    /// 舊紀錄（同 <see cref="HostId"/>=0 的降級慣例，前端顯示通用說明）。
    /// 值域：<c>rule:{Source} EventId {EventId}</c>（命中重大旗標規則）／<c>correlation</c>
    /// （關聯訊號帶旗標）／<c>trend</c>（頻率異常）／<c>high_issue:{Source} EventId {EventId}</c>
    /// （單純 High 嚴重度問題）／<c>medium</c>（中風險但無單一可指名依據）／<c>ai_raise</c>
    /// （AI 判讀把程式判定的風險往上拉，見 RiskLevels.MoreSevere）。純顯示用途，不影響任何判定邏輯。
    /// </summary>
    public string? RiskBasis { get; set; }

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

    /// <summary>
    /// true = 統計段已完成並寫入，AI 段還在排隊或執行中（docs/archive/FEEDBACK-12-PLAN.md §3.5）。
    /// 與 <see cref="AiAnalyzed"/> 是不同的兩件事：AiAnalyzed=false 且 AiPending=false 是
    /// 「AI 已定案不需要／已嘗試但失敗」的既有語意；AiPending=true 是新增的第三態，代表
    /// 「還沒定案，正在等」。執行完成後由 <c>AttachAiResult</c> 改回 false；若執行被取消，
    /// 這個旗標維持 true，下次執行會被當成待補跑的孤兒紀錄自動接手（§3.10）。
    /// </summary>
    public bool AiPending { get; set; }

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

    /// <summary>
    /// 涵蓋率缺口（docs/archive/HISTORY.md S6）：事件來源不完整，或 Security log 未能讀取。
    /// 不序列化——由 <see cref="DataIncomplete"/>／<see cref="SecurityLogAvailable"/> 算出，
    /// 避免另存一份可能與來源欄位漂移不同步的值。取代原本 Dashboard／Report 各自寫一次的判斷式。
    /// </summary>
    [JsonIgnore]
    public bool HasCoverageGap => DataIncomplete || SecurityLogAvailable == false;

    /// <summary>
    /// 因全站嚴重度顯示設定（SiteHidden）被過濾掉的問題數（docs/archive/HISTORY.md #11）。
    /// **不序列化、不持久化**——由 RecordRepository 在讀取當下計算填入，只有風險日詳情頁的
    /// 單筆讀取路徑會設定它（見 IRecordRepository.GetOne），其餘查詢路徑維持預設值 0。
    /// 風險等級判定不受顯示設定影響（見 SystemSettings.SeverityDisplayMode 文件），這個欄位
    /// 只用來在畫面上解釋「為什麼看到的問題比判定依據少」。
    /// </summary>
    [JsonIgnore]
    public int HiddenIssueCount { get; set; }

    /// <summary>
    /// 本日實際成功讀取的頻道全名清單。**null = 本欄位問世前寫入的舊紀錄**（同 <see cref="HostId"/>=0 的
    /// 降級慣例）；趨勢層/慢速趨勢層以 <see cref="ChannelCoverage.WasRead"/> 判斷是否納入某頻道簽章的基準，
    /// 舊紀錄自動 fallback 到「三個傳統日誌 + SecurityLogAvailable」的既有語意，新頻道則不算入基準。
    /// </summary>
    public List<string>? ChannelsRead { get; set; }

    /// <summary>本次執行若剛好做了體檢（due-date 到期，見 WeeklyCheckupService），結果附掛於當天紀錄；平常日為 null</summary>
    public WeeklyCheckupResult? WeeklyCheckup { get; set; }

    /// <summary>
    /// 各類別的 AI 深入分析結構化結果（風險「中」以上才有）。與報告全文（ReportFile）並存但目的不同：
    /// 報告全文是給人看的完整排版，這裡是給未來 DB／查詢/問答用的結構化資料，兩者由同一次深析呼叫產生，
    /// 不需要事後從文字反解析。低風險日恆為空清單（該日從不觸發深析）。
    /// </summary>
    public List<CategoryDeepDive> DeepDives { get; set; } = new();
}

/// <summary>
/// AI 段定案後的輸出（docs/archive/FEEDBACK-12-PLAN.md §3.3/§3.5）——覆寫統計段已寫入的暫代欄位，
/// 是 <c>IAnalysisRecordStore.AttachAiResult</c> 的輸入。放在 Models（而不是產生它的
/// LogAnalysisService 所在的 Service 命名空間）是因為 Persistence 層的
/// <c>IAnalysisRecordStore</c> 介面也需要引用這個型別——與 <see cref="WeeklyCheckupResult"/>
/// 同一個理由（<c>AttachWeeklyCheckup</c> 的輸入也放在這裡）。
/// <see cref="ReportFile"/>／<see cref="DeepDives"/> 也在這裡：深析報告需要「AI 是否成功、
/// 風險是否被拉高」的最終結果才能決定要不要產生，所以報告產出的時機隨 AI 段一起移動，
/// 不再是統計段的職責。
/// </summary>
/// <param name="UncoveredChecksAddendum">
/// 要追加進既有 <see cref="DailyAnalysisRecord.UncoveredChecks"/> 的項目（回饋十三輪 C，
/// 孤兒補跑產報告）；null／空＝不追加。統計段寫入的 UncoveredChecks 已經定案，
/// <c>AttachAiResult</c>（見 EfAnalysisRecordStore）只用這個欄位把 AI 段才知道的新缺口
/// （例如風險事件暫存已逾期，補跑報告從缺）併進去，不是整批覆寫——避免覆寫掉統計段
/// 已經寫好的既有申報項目。</param>
public sealed record AiOutcome(
    string Headline,
    string Summary,
    string TrendAssessment,
    string Action,
    string RiskLevel,
    string? RiskBasis,
    bool AiAnalyzed,
    int ScreenedTailCount,
    List<string> ScreeningNotes,
    string? ReportFile,
    List<CategoryDeepDive> DeepDives,
    List<string>? UncoveredChecksAddendum = null);

/// <summary>單一類別（儲存裝置/硬體/安全…）的深入分析結果</summary>
public class CategoryDeepDive
{
    // 與 LogIssueSignature 的列舉欄位一致，存字串（"Storage"）而非整數——
    // 這是 docs/DB-SPEC.md 一致性機制 #5：兩後端逐字一致，未來 DB 匯入直接對應字串類別，不用反查數字
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
/// 詳見 docs/archive/HISTORY.md「核心設計決策 B」。
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
