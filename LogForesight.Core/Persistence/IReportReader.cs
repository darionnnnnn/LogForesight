namespace LogForesight.Core.Persistence;

/// <summary>
/// 報告全文與它的分類欄位。<c>content</c> 以外的欄位供畫面標題列與下載檔名使用，
/// 不必為了顯示「這是哪一天的什麼報告」去反解析全文。
/// </summary>
public record ReportContent(
    long ReportId,
    string Kind,
    DateTime ReportDate,
    string? RiskLevel,
    string? Categories,
    string FileName,
    string Content);

/// <summary>
/// 報告全文的讀取（<see cref="IReportSink"/> 的對應讀取端）。
///
/// 為什麼要獨立一個介面而不是加在 IReportSink 上：批次只寫、Web 只讀，
/// 合成一個介面會讓批次被迫依賴它用不到的讀取方法（ISP）。
/// 儲存實作這一側兩者是同一個類別（<c>EfReportStore</c>）——切分在呼叫端，不在儲存端。
///
/// **以「主機×日期×種類」自然鍵讀取，而不是以紀錄裡存的參照**：三種報告只有風險報告
/// 有欄位存得下參照（<c>DailyAnalysisRecord.ReportFile</c>），週檢與權限異動報告從來沒有；
/// 而升級前的部署存下來的參照是檔案絕對路徑。統一走自然鍵，三種報告同一條路徑，
/// 舊紀錄也不需要回頭改寫參照。
/// </summary>
public interface IReportReader
{
    /// <summary>
    /// 讀某主機某日某種報告。找不到時回 null——報告可能已過保留期被清理，
    /// 那不是錯誤，畫面顯示「報告已不存在」即可。
    /// </summary>
    /// <param name="kind">種類字面值，見 <see cref="ReportKinds"/></param>
    ReportContent? Read(HostKey host, DateTime date, string kind);

    /// <summary>
    /// 這份報告存不存在。畫面要決定「顯不顯示查看報告的入口」時用這個而不是 <see cref="Read"/>——
    /// 只為了判斷有無而把整份全文（可能數十 KB）撈回來是白費。
    ///
    /// 也不能改用紀錄上的 <c>ReportFile</c> 旗標代替：報告有自己的保留期，可能已被清掉，
    /// 那個旗標卻永遠留著，會讓畫面給出一個點下去必定落空的入口。
    /// </summary>
    bool Exists(HostKey host, DateTime date, string kind);
}

/// <summary>
/// 報告全文的存量查詢（設定頁的空間告知）。
///
/// 與 <see cref="IReportReader"/> 分開：讀者關心的是「某一份報告的內容」，
/// 這裡關心的是「全部報告佔了多少」——後者是設定畫面才需要的維運視角，
/// 併進讀取介面會讓每個只想讀報告的呼叫端都被迫依賴它。
/// </summary>
public interface IReportUsageQuery
{
    /// <summary>目前的報告份數與內容總字元數</summary>
    (int Count, long TotalChars) Usage();
}
