using System.Net;
using LogForesight.Core.Models;

namespace LogForesight.Tests.Authorization;

/// <summary>寫入請求的期望結果</summary>
public enum WriteExpect
{
    /// <summary>被拒（403／404），前後快照逐字相同</summary>
    Reject,

    /// <summary>成功（2xx），且被檢視的資料真的變了——證明判準比得到東西</summary>
    Write,

    /// <summary>成功（2xx），但這個端點對該身分本來就不寫資料（例如「已讀」只有管理者才寫）</summary>
    OkNoWrite
}

/// <summary>一個測試案例的計畫：要送的請求、要比對的快照、期望結果與復原動作</summary>
public sealed class WritePlan
{
    public required HttpMethod Method { get; init; }
    public required string Url { get; init; }
    public object? Body { get; init; }
    public required WriteExpect Expect { get; init; }

    /// <summary>這一列「相關資料」的快照（例如該主機日的處理狀態、該張交辦單、該筆權限異動確認）</summary>
    public required Func<string> Snapshot { get; init; }

    /// <summary>被拒時允許的狀態碼（預設 403／404）</summary>
    public HttpStatusCode[] RejectStatuses { get; init; } = { HttpStatusCode.Forbidden, HttpStatusCode.NotFound };

    /// <summary>批次端點對不可見的項目是「200＋略過」而不是整筆 4xx：200 時回應必須含這段文字才算被拒</summary>
    public string? SoftRejectText { get; init; }

    /// <summary>正向對照寫入後的復原（讀取矩陣與其他列依賴夾具的原始資料）</summary>
    public Action? Restore { get; init; }

    /// <summary>被拒時，回應全文不得出現的主機哨兵編號</summary>
    public int[] ForbiddenHosts { get; init; } = Array.Empty<int>();
}

public sealed class WriteCtx
{
    public required AuthorizationHttpFixture Fx { get; init; }
    public required AuthorizationDataProbe Probe { get; init; }
    public required IdentityFx Identity { get; init; }

    /// <summary>案例標籤：H1～H4＝主機；H3:A／H3:B＝主機 3 的問題 A／B；WO-C／WO-A／own＝交辦單；self／-</summary>
    public required string Label { get; init; }

    public int HostN => int.Parse(Label[1..2]);
    public HostFx Host => Fx.Host(HostN);
    public string Day => AuthorizationHttpFixture.DayText;
}

/// <summary>
/// 寫入矩陣的一列＝一個「受限者能進」的寫入端點。<see cref="Key"/> 與路由快照基準檔逐字相同（涵蓋守門靠它對帳）。
/// 每列的案例＝（身分, 標籤）；<see cref="Plan"/> 依案例組出請求與期望。
/// </summary>
public sealed record WriteRow
{
    public required string Key { get; init; }

    /// <summary>（身分名稱, 案例標籤）</summary>
    public required (string Identity, string Label)[] Cases { get; init; }

    public required Func<WriteCtx, WritePlan> Plan { get; init; }

    public override string ToString() => Key;
}

public static class WriteMatrix
{
    private static readonly (string, string)[] AOnHosts =
        { ("A受限", "H1"), ("A受限", "H2"), ("A受限", "H3"), ("A受限", "H4") };

    private static readonly (string, string)[] AOnOrders =
        { ("A受限", "WO-C"), ("A受限", "own"), ("C案件授與", "WO-A") };

    private static string IssueKeyOf(HostFx h, string which) => which == "A" ? h.IssueKeyA : IssueSignatureKey.For(h.IssueB);

    /// <summary>非授權主機＝這個身分完整可見以外的主機</summary>
    private static int[] Unauthorized(IdentityFx id) => new[] { 1, 2, 3, 4 }.Where(n => id.VisOf(n) != Visibility.Full).ToArray();

    // ── 日層級／問題層級處理狀態 ──────────────────────────────────────────────

    private static WritePlan DayPlan(WriteCtx c)
    {
        var h = c.Host;
        var own = c.Identity.VisOf(h.N) == Visibility.Full;
        var before = c.Probe.CaptureHostDay(h);
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = $"/api/records/{h.Id}/{c.Day}/handling",
            Body = new { status = HandlingStatuses.Resolved, note = Sentinels.Of(h.N, SentinelKind.Note) },
            Expect = own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.HostDaySnapshot(h),
            ForbiddenHosts = Unauthorized(c.Identity),
            Restore = () => c.Probe.RestoreHostDay(h, before)
        };
    }

    private static WritePlan IssuePlan(WriteCtx c)
    {
        var h = c.Host;
        var caseGrantIssue = c.Label.EndsWith(":A", StringComparison.Ordinal) || c.Label.EndsWith(":B", StringComparison.Ordinal);
        // 受限者對自己的主機標記問題 B；對未授權主機標問題 A；案件授與者依標籤標 A（被交辦）或 B（未被交辦）
        var which = caseGrantIssue ? c.Label[^1..] : c.Identity.VisOf(h.N) == Visibility.Full ? "B" : "A";
        var expectWrite = c.Identity.VisOf(h.N) == Visibility.Full || (caseGrantIssue && which == "A");
        var before = c.Probe.CaptureHostDay(h);
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = $"/api/records/{h.Id}/{c.Day}/handling/issues",
            Body = new
            {
                issueKey = IssueKeyOf(h, which),
                // 案件授與者的問題 A 有進行中案件，維持進行中（不結案、不影響他的可見範圍）
                status = caseGrantIssue ? IssueHandlingStatuses.InProgress : IssueHandlingStatuses.Resolved,
                note = "寫入矩陣"
            },
            Expect = expectWrite ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.HostDaySnapshot(h),
            ForbiddenHosts = Unauthorized(c.Identity),
            Restore = () => c.Probe.RestoreHostDay(h, before)
        };
    }

    private static WritePlan IssueBatchPlan(WriteCtx c)
    {
        var h = c.Host;
        var caseGrantIssue = c.Label.EndsWith(":A", StringComparison.Ordinal) || c.Label.EndsWith(":B", StringComparison.Ordinal);
        // 批次對照單筆用另一個問題（A），避免兩列互相干擾；案件授與者依標籤
        var which = caseGrantIssue ? c.Label[^1..] : "A";
        var expectWrite = c.Identity.VisOf(h.N) == Visibility.Full || (caseGrantIssue && which == "A");
        var before = c.Probe.CaptureHostDay(h);
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = $"/api/records/{h.Id}/{c.Day}/handling/issues/batch",
            Body = new
            {
                issueKeys = new[] { IssueKeyOf(h, which) },
                status = caseGrantIssue ? IssueHandlingStatuses.InProgress : IssueHandlingStatuses.Resolved,
                note = "寫入矩陣批次"
            },
            Expect = expectWrite ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.HostDaySnapshot(h),
            ForbiddenHosts = Unauthorized(c.Identity),
            Restore = () => c.Probe.RestoreHostDay(h, before)
        };
    }

    // ── 權限異動確認 ────────────────────────────────────────────────────────

    private static string ConfirmId(WriteCtx c, string kind) => $"authz-write-{kind}-h{c.HostN}-{Guid.NewGuid():N}";

    private static WritePlan ConfirmPlan(WriteCtx c)
    {
        var h = c.Host;
        var own = c.Identity.VisOf(h.N) == Visibility.Full;
        // 未授權主機用夾具既有的變更（authz-change-n）；自己的主機另建一筆，不動夾具的資料
        var id = own ? ConfirmId(c, "single") : $"authz-change-{h.N}";
        if (own) c.Probe.AddPermChange(h, id);
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = $"/api/permission-changes/{id}/confirm",
            Body = new { status = PermissionConfirmStatuses.Authorized, note = "寫入矩陣" },
            Expect = own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.PermConfirmation(id),
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    private static WritePlan ConfirmBatchPlan(WriteCtx c)
    {
        var h = c.Host;
        var own = c.Identity.VisOf(h.N) == Visibility.Full;
        string[] ids;
        if (own)
        {
            ids = new[] { ConfirmId(c, "batch1"), ConfirmId(c, "batch2") };
            foreach (var id in ids) c.Probe.AddPermChange(h, id);
        }
        else
        {
            ids = new[] { $"authz-change-{h.N}" };
        }

        return new WritePlan
        {
            Method = HttpMethod.Post,
            Url = "/api/permission-changes/confirm/batch",
            Body = new { changeIds = ids, status = PermissionConfirmStatuses.Authorized, note = "寫入矩陣批次" },
            Expect = own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => string.Join("|", ids.Select(c.Probe.PermConfirmation)),
            // 批次對看不到的項目回 200 並列入 skipped、updatedCount=0，而不是整筆 4xx
            SoftRejectText = "\"updatedCount\":0",
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    // ── 交辦單：回覆、到期日、已讀 ──────────────────────────────────────────

    private sealed record OrderTarget(long Id, bool Own, bool Fixture = false);

    /// <summary>"WO-C"／"WO-A"＝夾具的單（不是該身分本人的）；"own"＝為該身分另建的一張，成員在主機 1</summary>
    private static OrderTarget ResolveOrder(WriteCtx c, bool withMember)
    {
        return c.Label switch
        {
            // 夾具的單：處理人本人（例如 C 回覆 WO-C）才算自己的；其餘身分碰別人的單必須被擋
            "WO-C" => new OrderTarget(c.Fx.WorkOrderC.Id, c.Identity.UserId == c.Fx.WorkOrderC.HandlerId, Fixture: true),
            "WO-A" => new OrderTarget(c.Fx.WorkOrderA.Id, c.Identity.UserId == c.Fx.WorkOrderA.HandlerId, Fixture: true),
            _ => new OrderTarget(c.Probe.NewOrder(c.Identity, c.Fx.Host(1), "own" + Guid.NewGuid().ToString("N")[..6], withMember), true)
        };
    }

    private static WritePlan ReplyPlan(WriteCtx c)
    {
        var o = ResolveOrder(c, withMember: true);
        // 案件授與者 C 回覆自己名下的夾具單 WO-C：維持進行中（結案會讓他失去主機 3 的案件授與可見度，干擾其他列），
        // 寫完把單、案件與主機 3 當天處理狀態復原
        var fixtureOwn = o is { Fixture: true, Own: true };
        var restore = fixtureOwn ? c.Probe.CaptureFixtureOrderRestore(o.Id, c.Fx.Host(3)) : null;
        return new WritePlan
        {
            Method = HttpMethod.Post,
            Url = $"/api/work-orders/{o.Id}/reply",
            Body = new { status = fixtureOwn ? IssueHandlingStatuses.InProgress : IssueHandlingStatuses.Resolved, note = "寫入矩陣回覆" },
            Expect = o.Own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.OrderSnapshot(o.Id),
            ForbiddenHosts = Unauthorized(c.Identity),
            Restore = restore
        };
    }

    private static WritePlan ReplyManyPlan(WriteCtx c)
    {
        var o = ResolveOrder(c, withMember: true);
        return new WritePlan
        {
            Method = HttpMethod.Post,
            Url = "/api/work-orders/reply-many",
            Body = new { workOrderIds = new[] { o.Id }, status = IssueHandlingStatuses.Resolved, note = "寫入矩陣批次回覆" },
            Expect = o.Own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.OrderSnapshot(o.Id),
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    private static WritePlan DueDatePlan(WriteCtx c)
    {
        var o = ResolveOrder(c, withMember: false);
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = $"/api/work-orders/{o.Id}/due-date",
            Body = new { dueDate = DateTime.Today.AddDays(7).ToString("yyyy-MM-dd") },
            Expect = o.Own ? WriteExpect.Write : WriteExpect.Reject,
            Snapshot = () => c.Probe.OrderSnapshot(o.Id),
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    private static WritePlan SeenPlan(WriteCtx c)
    {
        var o = ResolveOrder(c, withMember: false);
        return new WritePlan
        {
            Method = HttpMethod.Post,
            Url = $"/api/work-orders/{o.Id}/seen",
            // 「已讀」只有具 Assign 的管理者才會寫入（WorkOrderQueryService.MarkSeen）；處理人本人呼叫是 200 但不寫
            Expect = o.Own ? WriteExpect.OkNoWrite : WriteExpect.Reject,
            Snapshot = () => c.Probe.OrderSnapshot(o.Id),
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    // ── 沒有主機維度的寫入類端點 ────────────────────────────────────────────

    private static WritePlan TidyNotePlan(WriteCtx c) => new()
    {
        Method = HttpMethod.Post,
        Url = "/api/ai/tidy-note",
        // 太短：服務內直接回提示，不呼叫 AI、不扣節流額度
        Body = new { text = "短", issueLabel = (string?)null, hostCount = 1 },
        Expect = WriteExpect.OkNoWrite,
        Snapshot = c.Probe.Global,
        ForbiddenHosts = new[] { 1, 2, 3, 4 }
    };

    private static WritePlan ChatPlan(WriteCtx c)
    {
        var h = c.Host;
        var own = c.Identity.VisOf(h.N) == Visibility.Full;
        return new WritePlan
        {
            Method = HttpMethod.Post,
            Url = "/api/ai/chat",
            Body = new
            {
                hostId = h.Id,
                date = c.Day,
                issueKey = "",
                messages = new[] { new { role = "user", content = "請說明這一天的狀況" } }
            },
            // AI 指向一個接受連線即關閉的埠：自己的主機不要求寫入、也不檢查狀態碼（失敗時回 200＋data:null 或錯誤信封都可）
            Expect = own ? WriteExpect.OkNoWrite : WriteExpect.Reject,
            Snapshot = c.Probe.Global,
            ForbiddenHosts = Unauthorized(c.Identity)
        };
    }

    // ── 使用者自己的設定（只寫到自己）──────────────────────────────────────

    private static WritePlan SetupGuidePlan(WriteCtx c)
    {
        var me = c.Identity;
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = "/api/me/setup-guide",
            Body = new { hidden = true },
            Expect = WriteExpect.Write,
            Snapshot = () => c.Probe.Preferences(me),
            Restore = () => c.Probe.SetSetupGuideHidden(me, false)
        };
    }

    private static WritePlan NotePhrasesPlan(WriteCtx c)
    {
        var me = c.Identity;
        return new WritePlan
        {
            Method = HttpMethod.Put,
            Url = "/api/me/note-phrases",
            Body = new { phrases = new[] { "寫入矩陣常用語" } },
            Expect = WriteExpect.Write,
            Snapshot = () => c.Probe.Preferences(me),
            Restore = () => c.Probe.ClearNotePhrases(me)
        };
    }

    private static readonly (string, string)[] CIssues = { ("C案件授與", "H3:A"), ("C案件授與", "H3:B") };

    public static readonly IReadOnlyList<WriteRow> Rows = new List<WriteRow>
    {
        new() { Key = "PUT /api/records/{hostId:long}/{date}/handling", Cases = AOnHosts.Append(("C案件授與", "H3")).ToArray(), Plan = DayPlan },
        new() { Key = "PUT /api/records/{hostId:long}/{date}/handling/issues", Cases = AOnHosts.Concat(CIssues).ToArray(), Plan = IssuePlan },
        new() { Key = "PUT /api/records/{hostId:long}/{date}/handling/issues/batch", Cases = AOnHosts.Concat(CIssues).ToArray(), Plan = IssueBatchPlan },
        new() { Key = "PUT /api/permission-changes/{changeId}/confirm", Cases = AOnHosts.Append(("C案件授與", "H3")).ToArray(), Plan = ConfirmPlan },
        new() { Key = "POST /api/permission-changes/confirm/batch", Cases = AOnHosts.Append(("C案件授與", "H3")).ToArray(), Plan = ConfirmBatchPlan },
        // 成對：A／C 打別人的單被擋；C 回覆自己名下的 WO-C 成功（正向，證明判準比得到東西）
        new() { Key = "POST /api/work-orders/{id:long}/reply", Cases = AOnOrders.Append(("C案件授與", "WO-C")).ToArray(), Plan = ReplyPlan },
        new() { Key = "POST /api/work-orders/reply-many", Cases = AOnOrders, Plan = ReplyManyPlan },
        new() { Key = "PUT /api/work-orders/{id:long}/due-date", Cases = AOnOrders, Plan = DueDatePlan },
        new() { Key = "POST /api/work-orders/{id:long}/seen", Cases = AOnOrders.Take(2).ToArray(), Plan = SeenPlan },
        new() { Key = "POST /api/ai/tidy-note", Cases = new[] { ("A受限", "-") }, Plan = TidyNotePlan },
        new() { Key = "POST /api/ai/chat", Cases = AOnHosts, Plan = ChatPlan },
        new() { Key = "PUT /api/me/setup-guide", Cases = new[] { ("A受限", "self") }, Plan = SetupGuidePlan },
        new() { Key = "PUT /api/me/note-phrases", Cases = new[] { ("A受限", "self") }, Plan = NotePhrasesPlan },
    };

    public static IReadOnlySet<string> Keys { get; } = Rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
}
