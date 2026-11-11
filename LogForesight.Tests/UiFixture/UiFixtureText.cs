namespace LogForesight.Tests;

/// <summary>
/// UI 驗收資料集用的長字串與邊界字串。刻意集中一處：每一條都對應矩陣裡的一個格子，
/// 測試用同一份常數回頭比對，產生器與斷言不會各寫一份而漂移。
/// </summary>
internal static class UiFixtureText
{
    /// <summary>重複某段文字直到長度至少 <paramref name="minLength"/>（結果長度不一定剛好）。</summary>
    public static string RepeatTo(string unit, int minLength)
    {
        var sb = new System.Text.StringBuilder(minLength + unit.Length);
        while (sb.Length < minLength) sb.Append(unit);
        return sb.ToString();
    }

    /// <summary>中英混排的長句單元（含空白，可自然斷行）。</summary>
    public const string MixedUnit =
        "當 Windows Server 2019 的 Print Spooler service 在 driver isolation 啟用後仍反覆 crash，" +
        "請先檢查 C:\\Windows\\System32\\spool\\drivers 底下的 third-party driver 版本與 event log 的 faulting module，";

    /// <summary>無空白連續字串（2,000 字元）：測試容器是否會被撐破、有沒有換行斷點。</summary>
    public static readonly string Contiguous2000 = RepeatTo("ABCDEFGHIJ0123456789", 2000)[..2000];

    /// <summary>1,000 字元無空白連續字串：規則白話說明的產品長度上限（<c>RuleSchemaLimits.PlainExplanationMaxLength</c>）。</summary>
    public static readonly string Contiguous1000 = Contiguous2000[..1000];

    /// <summary>255 字元、無空白的 Source（簽章 Source 欄位的長度上限）。</summary>
    public static readonly string Source255 = ("UiFixture-LongSource-" + RepeatTo("x", 300))[..255];

    /// <summary>含 HTML／引號的字串：一律應被當純文字顯示。</summary>
    public const string Hostile = "<script>alert(1)</script> & \"雙引號\" 'single' <img src=x onerror=alert(2)>";

    public const string Legacy1 =
        "TP-BKSVR02$ Account Domain: BRK Logon ID: 0x3E7 Group: Security ID: S-1-5-32-544 Group Name: Administrators Group Domain: Builtin Changed Attributes: SAM Account Name: - SID History: - Additional Information: Privileges: -";

    public const string Legacy2 =
        "TP-BKSVR02$ Account Domain: BRK Logon ID: 0x3E7 Audit Policy Change: Category: Privilege Use Subcategory: Other Privilege Use Events Subcategory GUID: {0cce922a-69ae-11d9-bed3-505054503030} Changes: Success removed, Failure removed";

    public const string Legacy3 =
        "- Account Domain: - Logon ID: 0x0 Logon Type: 3 Account For Which Logon Failed: Security ID: S-1-0-0 Account Name: srvadm18.brk Account Domain: brk Failure Information: Failure Reason: Unknown user name or bad password. Status: 0xC000006D Sub Status: 0xC0000064";

    public const string Legacy3Source = "VM-SPRINGBOARD7 Source Network Address: 10.216.8.188 Source Port: 0";

    /// <summary>Markdown：表格、清單、LaTeX 箭頭、貨幣符號。</summary>
    public const string Markdown =
        "## 今日重點\n\n" +
        "| 項目 | 數量 | 嚴重度 |\n|---|---|---|\n| 磁碟錯誤 | 12 | 高 |\n| 服務停止 | 3 | 中 |\n\n" +
        "- 第一項：檢查 RAID 狀態\n- 第二項：備份後更換磁碟\n\n" +
        "1. 先確認備份\n2. 再排程更換\n\n" +
        "狀態從 異常 $\\rightarrow$ 恢復，預估費用約 $100 元（另有 $200 與 $300 兩種方案）。\n\n" +
        "**粗體** 與 `inline code` 與 > 引言";

    public const string OneLineExplanation = "這個事件代表服務被意外停止。";

    /// <summary>超過 300 字的多段說明。</summary>
    public static readonly string LongExplanation =
        RepeatTo("這是一段超過三百字的白話說明，用來檢查說明區塊在內容很長時是否仍能正常換行、捲動或收合，" +
                 "同時確認行高與段落間距不會讓版面失衡，也不會把旁邊的欄位擠出畫面之外。", 420);

    /// <summary>產生 n 個帳號名稱（含大小寫、網域、長帳號）。</summary>
    public static List<string> Accounts(int n, string prefix = "user") =>
        Enumerable.Range(1, n).Select(i => i % 7 == 0 ? $@"CORP\{prefix}.with.a.very.long.account.name.{i:D3}" : $"{prefix}{i:D3}").ToList();

    /// <summary>仿 <c>LogAggregator.ExtractSecurityDetails</c> 的 KeyDetails 顯示字串。</summary>
    public static string? KeyDetails(IReadOnlyList<string> accounts, IReadOnlyList<string> ips)
    {
        var parts = new List<string>();
        if (accounts.Count > 0)
            parts.Add($"相關帳號({accounts.Count}個): {string.Join(", ", accounts.Take(5))}{(accounts.Count > 5 ? "…" : "")}");
        if (ips.Count > 0)
            parts.Add($"來源IP({ips.Count}個): {string.Join(", ", ips.Take(5))}{(ips.Count > 5 ? "…" : "")}");
        return parts.Count > 0 ? string.Join("；", parts) : null;
    }
}
