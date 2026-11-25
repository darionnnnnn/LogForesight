using System.Net;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using static LogForesight.Tests.Authorization.SentinelKind;

namespace LogForesight.Tests.Authorization;

/// <summary>讀取矩陣的列種類：決定端點樣板中的哪些占位符要代入什麼</summary>
public enum RowScope
{
    /// <summary>無編號參數的清單／彙總端點：每個身分查一次，回應要對「全部四台主機」逐台驗哨兵</summary>
    Global,
    /// <summary>以主機編號（或主機名稱）直取：每個身分 × 四台主機</summary>
    Host,
    /// <summary>接受主機編號但對不可見主機回 200 空結果（不是 404）：判準只看回應不含哨兵</summary>
    HostSoft,
    /// <summary>路由帶處理人 userId：每個身分 × 四個目標使用者</summary>
    User,
    /// <summary>路由帶交辦單編號：每個身分 × 兩張交辦單</summary>
    WorkOrder
}

/// <summary>
/// 讀取矩陣的一列＝一個 GET 端點。
/// <see cref="Key"/> 必須與 <c>controller-route-snapshot.txt</c> 的「方法 路由樣板」逐字相同（涵蓋守門靠它對帳）。
/// </summary>
public sealed record ReadRow
{
    public required string Key { get; init; }
    public required RowScope Scope { get; init; }

    /// <summary>附加在路由後的查詢字串樣板；占位符：{hostId} {hostName} {date} {from} {to} {issueKey} {src} {eventId} {allHostIds}</summary>
    public string Query { get; init; } = "";

    /// <summary>完整可見的主機，回應至少要含其中一種哨兵（證明判準比得到東西）；空陣列＝這個端點的資料形狀不帶哨兵，不做正向斷言</summary>
    public SentinelKind[] Positive { get; init; } = Array.Empty<SentinelKind>();

    /// <summary>回應不帶哨兵時的正向斷言：完整可見的主機，回應必須含這段文字（例如該端點只回統計數字）</summary>
    public string? PositiveText { get; init; }

    /// <summary>Global 列的彙總型正向斷言：依身分算出回應必須含的文字（null＝不斷言）。用於只回彙總數字、不帶任何哨兵的端點</summary>
    public Func<IdentityFx, string?>? GlobalNeedle { get; init; }

    /// <summary>true＝這個端點對 ViewAll 的 Admin 本來就不依「主機是否啟用」過濾（權限異動清單依主機名單過濾、ViewAll 不下推名單）；
    /// 停用主機 4 的哨兵出現在 Admin 的回應中不視為洩漏。這是已知的行為，不是被測試放行的漏洞，見回報。</summary>
    public bool AdminSeesInactive { get; init; }

    /// <summary>端點要求的能力（任一即可）；身分都沒有就是 403</summary>
    public Capability[]? Gate { get; init; }

    /// <summary>Global／Host 完整可見時允許的狀態碼（預設 200）</summary>
    public HttpStatusCode[] OkStatuses { get; init; } = { HttpStatusCode.OK };

    /// <summary>Host 列對「只因案件授與可見」的主機允許的狀態碼（預設 200 或 404）</summary>
    public HttpStatusCode[] CaseStatuses { get; init; } = { HttpStatusCode.OK, HttpStatusCode.NotFound };

    /// <summary>案件授與者查到 200 時，回應至少要含的哨兵（主機 3 的問題 A）；null＝不做正向斷言</summary>
    public SentinelKind[]? CasePositive { get; init; }

    /// <summary>User 列：依「被查的目標使用者」取回應中必須出現的字串（null＝不斷言）；僅在查詢身分被允許（本人／Assign／ViewAll）時檢查</summary>
    public Func<IdentityFx, string?>? UserNeedle { get; init; }

    /// <summary>WorkOrder 列：依交辦單取回應中必須出現的字串（null＝不帶資料）</summary>
    public Func<WorkOrderFx, string?>? WorkOrderNeedle { get; init; }

    /// <summary>回應結構斷言（哨兵比不到的端點，例如只回 {hostId, date}）：給身分、被查的主機（Host／HostSoft 列才有）、全部主機與回應全文，回傳問題描述（null＝通過）。
    /// 只在狀態碼 200 且該身分通過閘門時檢查</summary>
    public Func<IdentityFx, HostFx?, IReadOnlyList<HostFx>, string, string?>? ResponseCheck { get; init; }

    public string Method => Key[..Key.IndexOf(' ')];
    public string Route => Key[(Key.IndexOf(' ') + 1)..];
    public override string ToString() => Key;
}

public static class ReadMatrix
{
    /// <summary>案件授與者對主機 3 可以看到的哨兵：主機名稱、IP、所屬主機群組名稱（host-detail 對案件授與者本來就回主機基本資料並帶 caseGrantOnly 旗標）與被交辦的問題 A（含它先前的結案說明）；其餘一律不得出現。</summary>
    public static readonly SentinelKind[] CaseAllowed = { Host, Ip, IssueA, IssueNote, SentinelKind.Group };

    public static IEnumerable<SentinelKind> CaseForbidden =>
        Enum.GetValues<SentinelKind>().Where(k => !CaseAllowed.Contains(k));

    private const string RecordRange = "from={from}&to={to}&pageSize=100";

    public static readonly IReadOnlyList<ReadRow> Rows = new List<ReadRow>
    {
        // ── 儀表板、報表、問題查詢（清單／彙總類）──────────────────────────────
        new() { Key = "GET /api/dashboard/summary", Scope = RowScope.Global, Positive = new[] { Host, Headline, IssueA } },
        new() { Key = "GET /api/reports/summary", Scope = RowScope.Global, Query = "from={from}&to={to}", Positive = new[] { Host, Headline, IssueA } },
        // 報表 CSV 匯出（F20-4）：與 summary 同一段查詢，主機排行表每台有風險日的可見主機一列
        new() { Key = "GET /api/reports/export/{table}", Scope = RowScope.Global, Query = "from={from}&to={to}", Positive = new[] { Host } },
        // 處理成效：再發生排行的問題標籤帶主機前綴的問題名（哨兵 IssueA）；類型趨勢只回數字，
        // 完整可見的主機數 × 兩個問題＝問題日總數（多算＝洩漏了不可見主機的資料）
        new() { Key = "GET /api/reports/effectiveness", Scope = RowScope.Global, Query = "from={from}&to={to}", Positive = new[] { IssueA } },
        new() { Key = "GET /api/reports/category-trend", Scope = RowScope.Global, Query = "from={from}&to={to}",
            GlobalNeedle = id => id.Vis.Values.Count(v => v == Visibility.Full) is var n and > 0 ? $"\"total\":{n * 2}" : null },
        new() { Key = "GET /api/records", Scope = RowScope.Global, Query = RecordRange, Positive = new[] { Host, Headline, IssueA } },
        new() { Key = "GET /api/records/by-host", Scope = RowScope.Global, Query = RecordRange, Positive = new[] { Host } },
        new() { Key = "GET /api/records/by-date", Scope = RowScope.Global, Query = RecordRange,
            // 依日期彙總只回數字：完整可見的主機數必須等於該身分看得到的高風險主機數（多算＝洩漏了不可見主機的存在）
            GlobalNeedle = id => id.Vis.Values.Count(v => v == Visibility.Full) is var n and > 0 ? $"\"highRiskHosts\":{n}" : null },
        new() { Key = "GET /api/records/by-issue", Scope = RowScope.Global, Query = RecordRange, Positive = new[] { IssueA } },
        // 依問題展開的逐主機列：以該主機獨有的問題查，看不到的主機回 200 空清單（不得帶出主機名）
        new() { Key = "GET /api/records/issue-hosts", Scope = RowScope.HostSoft, Query = "from={from}&to={to}&source={src}&eventId={eventId}",
            Positive = new[] { Host } },
        new() { Key = "GET /api/records/next-unhandled", Scope = RowScope.HostSoft, Query = "hostId={hostId}&date={date}",
            // 回應只有 {hostId, date}、沒有哨兵：回傳的 hostId 必須落在該身分完整可見的主機編號內（或 data 為 null）
            ResponseCheck = (id, _, hosts, body) =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
                if (!data.TryGetProperty("hostId", out var hid) || !hid.TryGetInt64(out var returned)) return "回應缺少 hostId";
                var visibleIds = hosts.Where(h => id.VisOf(h.N) == Visibility.Full).Select(h => h.Id).ToHashSet();
                return visibleIds.Contains(returned) ? null : $"回傳的 hostId={returned} 不在該身分可見的主機編號集合 [{string.Join(",", visibleIds)}] 內";
            } },
        // 沿用上次說明：以該主機的問題簽章查。受限者查未授權主機的簽章，回應不得含該主機的哨兵（hostKeys 篩選）；
        // 完整可見者要查得到該主機的結案說明（正向）。Admin 也只在啟用主機內找：查停用主機 4 的簽章不得回出主機 4 的說明
        new() { Key = "GET /api/handling/last-note", Scope = RowScope.HostSoft, Query = "issueKey={issueKey}", Gate = new[] { Capability.Handle },
            Positive = new[] { IssueNote } },

        // ── 風險日詳情與子資源（編號直取）───────────────────────────────────────
        new() { Key = "GET /api/records/{hostId:long}/{date}", Scope = RowScope.Host, Positive = new[] { Headline, IssueA }, CaseStatuses = new[] { HttpStatusCode.OK }, CasePositive = new[] { IssueA } },
        new() { Key = "GET /api/records/{hostId:long}/{date}/report", Scope = RowScope.Host, Positive = new[] { Report }, CaseStatuses = new[] { HttpStatusCode.OK } },
        new() { Key = "GET /api/records/{hostId:long}/{date}/handling", Scope = RowScope.Host, Positive = new[] { Note }, CaseStatuses = new[] { HttpStatusCode.OK },
            // 案件授與者讀到的是裁剪後的回應（只有識別與能力旗標）：必須帶 canHandle=true（C 有 Handle 能力），面板才會給批次表單
            ResponseCheck = (id, host, _, body) => id.VisOf(host!.N) == Visibility.CaseOnly && !body.Contains("\"canHandle\":true", StringComparison.Ordinal)
                ? "案件授與者的 handling 回應應含 \"canHandle\":true" : null },
        new() { Key = "GET /api/records/{hostId:long}/{date}/handling/logs", Scope = RowScope.Host, Positive = new[] { Note } },
        new() { Key = "GET /api/records/{hostId:long}/{date}/handling/issue-history", Scope = RowScope.Host, Query = "issueKey={issueKey}", Positive = new[] { IssueNote }, CasePositive = new[] { IssueNote } },

        // ── 主機詳情、主機與群組清單 ─────────────────────────────────────────────
        new() { Key = "GET /api/host-detail/{hostId:long}", Scope = RowScope.Host, Positive = new[] { Host } },
        new() { Key = "GET /api/host-detail/{hostId:long}/issues", Scope = RowScope.Host, Query = "source={src}&eventId={eventId}", PositiveText = "\"totalCount\":5" },
        new() { Key = "GET /api/host-detail/{hostId:long}/prtg", Scope = RowScope.Host, CaseStatuses = new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }, PositiveText = "\"devices\":[]" },
        new() { Key = "GET /api/hosts", Scope = RowScope.Global, Positive = new[] { Host } },
        new() { Key = "GET /api/hosts/groups", Scope = RowScope.Global, Positive = new[] { SentinelKind.Group } },

        // ── 我的交辦／交辦單 ───────────────────────────────────────────────────
        new() { Key = "GET /api/handlers/me/badge", Scope = RowScope.Global },
        new() { Key = "GET /api/handlers/{userId:long}/workload", Scope = RowScope.User },
        new() { Key = "GET /api/handlers/{userId:long}/work-orders", Scope = RowScope.User, UserNeedle = UserOrderNeedle },
        new() { Key = "GET /api/handlers/{userId:long}/work-orders/summary", Scope = RowScope.User },
        new() { Key = "GET /api/work-orders/{id:long}", Scope = RowScope.WorkOrder, WorkOrderNeedle = wo => wo.Needle("NOTE") },
        new() { Key = "GET /api/work-orders/{id:long}/members", Scope = RowScope.WorkOrder, WorkOrderNeedle = wo => wo.Tag == "C" ? Sentinels.Of(3, IssueA) : null },
        new() { Key = "GET /api/work-orders/{id:long}/timeline", Scope = RowScope.WorkOrder, WorkOrderNeedle = wo => wo.Needle("EVENT") },

        // ── 權限異動（[Permission(ConfirmPermission)]）──────────────────────────
        new() { Key = "GET /api/permission-changes", Scope = RowScope.Global, Gate = new[] { Capability.ConfirmPermission }, Positive = new[] { Perm }, AdminSeesInactive = true },
        new() { Key = "GET /api/permission-changes/ids", Scope = RowScope.Global, Gate = new[] { Capability.ConfirmPermission }, AdminSeesInactive = true,
            GlobalNeedle = id => id.Vis.Where(kv => kv.Value == Visibility.Full).Select(kv => (int?)kv.Key).Min() is { } n ? $"authz-change-{n}" : null },
        new() { Key = "GET /api/permission-changes/categories", Scope = RowScope.Global, Gate = new[] { Capability.ConfirmPermission } },
        new() { Key = "GET /api/permission-changes/report", Scope = RowScope.Host, Query = "host={hostName}&date={date}", Gate = new[] { Capability.ConfirmPermission }, Positive = new[] { PermReport }, AdminSeesInactive = true },

        // ── AI 讀取端點：AI 指向一個接受連線即關閉的本機埠，呼叫失敗時一律回 200＋data:null（不洩漏、也沒有哨兵可比）；
        //    interpret-issue 對不可見主機仍會先走 GetDetail 授權而回 404，所以編號直取的授權路徑有被驗到 ─
        new() { Key = "GET /api/ai/status", Scope = RowScope.Global },
        new() { Key = "GET /api/ai/today-focus", Scope = RowScope.Global },
        new() { Key = "GET /api/ai/query-summary", Scope = RowScope.Global, Query = "hostIds={allHostIds}&from={from}&to={to}" },
        new() { Key = "GET /api/ai/interpret-issue", Scope = RowScope.Host, Query = "hostId={hostId}&date={date}&issueKey={issueKey}" },

        // ── 執行活動（讀取類，登入即可）────────────────────────────────────────
        new() { Key = "GET /api/run-activity", Scope = RowScope.Global },
    };

    /// <summary>被查的目標使用者名下交辦單的哨兵：A 的單帶 SENTINEL-WO-A-*（無成員）；C 的單以主機 3 的問題 A 當問題標籤（清單不回單備註）</summary>
    private static string? UserOrderNeedle(IdentityFx target) => target.Name switch
    {
        "A受限" => "SENTINEL-WO-A-",
        "C案件授與" => Sentinels.Of(3, IssueA),
        _ => null
    };

    public static IReadOnlySet<string> Keys { get; } = Rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

    private static readonly Regex Placeholder = new(@"\{(\w+)(?::\w+)?\}", RegexOptions.Compiled);

    /// <summary>把路由樣板與查詢字串的占位符代入實際值</summary>
    public static string BuildUrl(ReadRow row, AuthorizationHttpFixture fx, HostFx? host, IdentityFx? targetUser, WorkOrderFx? order)
    {
        var from = AuthorizationHttpFixture.Day.AddDays(-14).ToString("yyyy-MM-dd");
        var to = AuthorizationHttpFixture.DayText;

        string Sub(string template) => Placeholder.Replace(template, m => m.Groups[1].Value switch
        {
            "hostId" => (host?.Id ?? 0).ToString(),
            "hostName" => Uri.EscapeDataString(host?.Name ?? ""),
            "date" => AuthorizationHttpFixture.DayText,
            "from" => from,
            "to" => to,
            "issueKey" => Uri.EscapeDataString(host?.IssueKeyA ?? ""),
            "src" => Uri.EscapeDataString(host?.IssueA.Source ?? ""),
            "eventId" => (host?.IssueA.EventId ?? 0).ToString(),
            "userId" => (targetUser?.UserId ?? 0).ToString(),
            "id" => (order?.Id ?? 0).ToString(),
            "allHostIds" => string.Join(",", fx.Hosts.Select(h => h.Id)),
            // 報表 CSV 匯出的表名：主機排行是每台主機一列，哨兵（主機名）一定出現在可見主機的列上
            "table" => "hosts",
            var other => throw new InvalidOperationException($"未知的占位符 {{{other}}}（{row.Key}）")
        });

        var url = Sub(row.Route);
        return row.Query.Length == 0 ? url : url + "?" + Sub(row.Query);
    }
}
