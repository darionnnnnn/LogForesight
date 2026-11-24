using LogForesight.Core.Models;
using LogForesight.Web.Models.Dto;
using Microsoft.Extensions.DependencyInjection;

namespace LogForesight.Tests.Scenarios;

/// <summary>動作執行時可用的環境（驅動器提供）</summary>
public sealed class ScenarioActionContext
{
    internal ScenarioActionContext(ScenarioSite site, int day, Func<int, DateTime> dataDateOf,
        IReadOnlyDictionary<string, long> hostIds, IReadOnlyDictionary<string, long> hostGroupIds,
        IReadOnlyDictionary<string, UserSpec> users)
    {
        Site = site;
        Day = day;
        DataDateOf = dataDateOf;
        HostIds = hostIds;
        HostGroupIds = hostGroupIds;
        Users = users;
    }

    public ScenarioSite Site { get; }

    /// <summary>目前是第幾天</summary>
    public int Day { get; }

    /// <summary>第 N 天分析的資料日（真實日期，只供 API 篩選條件使用，輸出不外洩）</summary>
    public Func<int, DateTime> DataDateOf { get; }

    internal IReadOnlyDictionary<string, long> HostIds { get; }
    internal IReadOnlyDictionary<string, long> HostGroupIds { get; }
    internal IReadOnlyDictionary<string, UserSpec> Users { get; }

    public long HostId(string name) =>
        HostIds.TryGetValue(name, out var id) ? id : throw new ScenarioUnresolvedException($"情境沒有主機「{name}」");

    public long HostGroupId(string name) =>
        HostGroupIds.TryGetValue(name, out var id) ? id : throw new ScenarioUnresolvedException($"情境沒有主機群組「{name}」");

    public long UserId(string account)
    {
        var user = Site.Get<IUserStore>().FindByAccount(account);
        return user?.UserId ?? throw new ScenarioUnresolvedException($"站台沒有使用者「{account}」");
    }

    public UserSpec UserSpec(string account) =>
        Users.TryGetValue(account, out var spec) ? spec : throw new ScenarioUnresolvedException($"情境沒有使用者「{account}」");
}

/// <summary>動作的參照對象解析不到（例如指定的單根本還沒建出來）；記成一筆「未送出」的結果，不中斷情境</summary>
public sealed class ScenarioUnresolvedException : Exception
{
    public ScenarioUnresolvedException(string message) : base(message) { }
}

/// <summary>
/// 以「條件」而不是寫死單號指到某張交辦單；在動作執行當下才解析（單號本身也是確定的，
/// 但用條件寫情境不會因為前面多建一張單而整串失準）。
/// </summary>
public abstract class OrderRef
{
    public abstract string Describe();

    internal abstract long Resolve(ScenarioActionContext ctx);

    /// <summary>指定單號</summary>
    public static OrderRef Id(long id) => new ById(id);

    /// <summary>目前單號最大的一張</summary>
    public static OrderRef Latest() => new LatestOrder();

    /// <summary>某處理人名下最新的進行中單</summary>
    public static OrderRef ForHandler(string account) => new ByHandler(account);

    /// <summary>某問題（Source＋EventId）最新的進行中單</summary>
    public static OrderRef ForIssue(string source, int eventId) => new ByIssue(source, eventId);

    private sealed class ById(long id) : OrderRef
    {
        public override string Describe() => $"單#{id}";
        internal override long Resolve(ScenarioActionContext ctx) =>
            ctx.Site.Get<IWorkOrderStore>().Get(id) != null ? id : throw new ScenarioUnresolvedException($"沒有單#{id}");
    }

    private sealed class LatestOrder : OrderRef
    {
        public override string Describe() => "最新一張單";
        internal override long Resolve(ScenarioActionContext ctx) =>
            AllOrders(ctx).Select(o => o.WorkOrderId).DefaultIfEmpty(0).Max() is var id and > 0
                ? id : throw new ScenarioUnresolvedException("目前沒有任何交辦單");
    }

    private sealed class ByHandler(string account) : OrderRef
    {
        public override string Describe() => $"{account} 名下最新進行中單";
        internal override long Resolve(ScenarioActionContext ctx)
        {
            var userId = ctx.UserId(account);
            var order = ctx.Site.Get<IWorkOrderStore>().GetActiveByHandler(userId).OrderByDescending(o => o.WorkOrderId).FirstOrDefault();
            return order?.WorkOrderId ?? throw new ScenarioUnresolvedException($"{account} 名下沒有進行中的單");
        }
    }

    private sealed class ByIssue(string source, int eventId) : OrderRef
    {
        public override string Describe() => $"{source} EventId {eventId} 最新進行中單";
        internal override long Resolve(ScenarioActionContext ctx)
        {
            var order = ctx.Site.Get<IWorkOrderStore>().GetActiveByIssue(source, eventId).OrderByDescending(o => o.WorkOrderId).FirstOrDefault();
            return order?.WorkOrderId ?? throw new ScenarioUnresolvedException($"{source} EventId {eventId} 沒有進行中的單");
        }
    }

    internal static List<WorkOrder> AllOrders(ScenarioActionContext ctx) => AllOrders(ctx.Site.Get<IWorkOrderStore>());

    internal static List<WorkOrder> AllOrders(IWorkOrderStore store)
    {
        var result = new List<WorkOrder>();
        for (var page = 1; ; page++)
        {
            var batch = store.QueryOrders(new WorkOrderQuery { Status = WorkOrderQueries.StatusAll, Page = page, PageSize = 100 });
            result.AddRange(batch.Items);
            if (batch.Items.Count < 100) break;
        }
        return result.OrderBy(o => o.WorkOrderId).ToList();
    }
}

/// <summary>一個人為動作：以某帳號的身分打真實 API。解析失敗與 HTTP 失敗都回結果，不擲例外。</summary>
public abstract class ScenarioAction
{
    protected ScenarioAction(string actor) => Actor = actor;

    /// <summary>送出請求的帳號</summary>
    public string Actor { get; }

    /// <summary>人讀的一句話（輸出表格用，不含真實日期或時間）</summary>
    public abstract string Describe();

    internal abstract Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx);

    internal async Task<ActionHttpResult> RunAsync(ScenarioActionContext ctx)
    {
        try
        {
            return await ExecuteAsync(ctx);
        }
        catch (ScenarioUnresolvedException ex)
        {
            // 0＝沒有送出（對象解析不到）；與真實 HTTP 狀態碼區隔
            return new ActionHttpResult(0, "unresolved", ex.Message);
        }
    }
}

/// <summary>情境動作的建構入口。第一個參數一律是「哪個帳號做的」。</summary>
public static class ScenarioActions
{
    /// <summary>處理人回覆交辦單（全部進行中成員）。<paramref name="status"/> 例：resolved／in_progress／wont_fix</summary>
    public static ScenarioAction Reply(string actor, OrderRef order, string status, string? note = null) =>
        new ReplyAction(actor, order, status, note);

    /// <summary>管理者依問題＋主機建立交辦（單一處理人，主機範圍）</summary>
    public static ScenarioAction CreateOrder(string actor, string source, int eventId, string handler, string[] hosts, string? note = null) =>
        new CreateOrderAction(actor, source, eventId, handler, hosts, note);

    /// <summary>管理者改派</summary>
    public static ScenarioAction Reassign(string actor, OrderRef order, string newHandler) =>
        new ReassignAction(actor, order, newHandler);

    /// <summary>管理者把「無法處理」的成員退回處理人（說明必填）</summary>
    public static ScenarioAction Return(string actor, OrderRef order, string note) =>
        new ReturnAction(actor, order, note);

    /// <summary>管理者代為結案；<paramref name="status"/> 例：resolved／wont_fix</summary>
    public static ScenarioAction AdminClose(string actor, OrderRef order, string status, string? reason = null, bool blockRedispatch = false) =>
        new AdminCloseAction(actor, order, status, reason, blockRedispatch);

    /// <summary>管理者取消交辦</summary>
    public static ScenarioAction Cancel(string actor, OrderRef order, string? reason = null) =>
        new CancelAction(actor, order, reason);

    /// <summary>待派頁「不再自動派工」：恢復單一（主機, 問題）。問題簽章鍵由站台目前被擋住的案件解析</summary>
    public static ScenarioAction RestoreDismissed(string actor, string host, string source, int eventId) =>
        new RestoreDismissedAction(actor, host, source, eventId);

    /// <summary>待派頁「不再自動派工」：恢復整個問題</summary>
    public static ScenarioAction RestoreDismissedIssue(string actor, string source, int eventId) =>
        new RestoreDismissedIssueAction(actor, source, eventId);

    /// <summary>設定問題靜音（Source＋EventId）。<paramref name="existingOrders"/>：pause（不動既有單）或 close</summary>
    public static ScenarioAction Mute(string actor, string source, int eventId, int days, string reason, string existingOrders = "pause") =>
        new MuteAction(actor, source, eventId, days, reason, existingOrders);

    /// <summary>解除問題靜音</summary>
    public static ScenarioAction Unmute(string actor, string source, int eventId) => new UnmuteAction(actor, source, eventId);

    /// <summary>環境變化：停用使用者（停用後該帳號的登入狀態立即失效）</summary>
    public static ScenarioAction DisableUser(string actor, string account) => new DisableUserAction(actor, account);

    /// <summary>環境變化：修改主機所屬群組（整組取代）</summary>
    public static ScenarioAction SetHostGroups(string actor, string host, params string[] groups) =>
        new SetHostGroupsAction(actor, host, groups);

    /// <summary>暫停／恢復某人的接單</summary>
    public static ScenarioAction SetDispatchPaused(string actor, string account, bool paused) =>
        new SetDispatchPausedAction(actor, account, paused);

    /// <summary>處理人回覆（帶預計完成日／觀察至；回報期限驗證用）</summary>
    public static ScenarioAction ReplyWithDue(string actor, OrderRef order, string status, DateTime dueDate, string? note = null) =>
        new ReplyAction(actor, order, status, note, dueDate);

    /// <summary>處理人只回覆單內指定主機的成員（其餘成員不動）</summary>
    public static ScenarioAction ReplyHosts(string actor, OrderRef order, string status, string[] hosts, string? note = null) =>
        new ReplyAction(actor, order, status, note, hosts: hosts);

    /// <summary>
    /// 管理者以範圍（全站或主機群組）建單；<paramref name="autoAttach"/> 開啟＝之後夜間新出現且在範圍內的主機會續掛進這張單。
    /// 範圍內受影響主機＝資料日區間內出現該問題的主機（第 1 天到目前這天）。
    /// </summary>
    public static ScenarioAction CreateOrderScoped(string actor, string source, int eventId, string handler, string scopeKind,
        string[] hostGroups, bool autoAttach, DateTime? dueDate = null, string? note = null) =>
        new CreateScopedOrderAction(actor, source, eventId, handler, scopeKind, hostGroups, autoAttach, dueDate, note);

    /// <summary>建單時帶預計完成日（日期可早於今天，用來驗證 API 如何處理）</summary>
    public static ScenarioAction CreateOrderDue(string actor, string source, int eventId, string handler, string[] hosts, DateTime dueDate) =>
        new CreateOrderAction(actor, source, eventId, handler, hosts, null, dueDate);

    /// <summary>環境變化：整組取代主機負責人</summary>
    public static ScenarioAction SetHostOwners(string actor, string host, params string[] owners) =>
        new SetHostOwnersAction(actor, host, owners);

    /// <summary>環境變化：停用／啟用主機</summary>
    public static ScenarioAction SetHostActive(string actor, string host, bool active) =>
        new SetHostActiveAction(actor, host, active);

    /// <summary>設定問題負責人（整組取代）</summary>
    public static ScenarioAction SetIssueOwners(string actor, string source, int eventId, params string[] owners) =>
        new SetIssueOwnersAction(actor, source, eventId, owners);

    /// <summary>環境變化：整組取代某使用者（其專屬使用者群組）可看的主機群組授權</summary>
    public static ScenarioAction SetUserHostAccess(string actor, string account, params string[] hostGroups) =>
        new SetUserHostAccessAction(actor, account, hostGroups);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private sealed class ReplyAction(string actor, OrderRef order, string status, string? note, DateTime? dueDate = null, string[]? hosts = null)
        : ScenarioAction(actor)
    {
        public override string Describe() => $"回覆 {order.Describe()}：{status}"
            + (hosts == null ? "" : $"（{string.Join("、", hosts)}）")
            + (string.IsNullOrEmpty(note) ? "" : $"（{note}）");

        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var orderId = order.Resolve(ctx);
            List<string>? caseIds = null;
            if (hosts != null)
            {
                var wanted = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                caseIds = ctx.Site.Get<IIssueCaseStore>().GetByWorkOrder(orderId, 0, 500)
                    .Where(c => c.ClosedAt == null && wanted.Contains(c.HostName)).Select(c => c.CaseId).ToList();
            }

            return ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{orderId}/reply",
                new WorkOrderReplyRequest { Status = status, Note = note, DueDate = dueDate, CaseIds = caseIds });
        }
    }

    private sealed class CreateOrderAction(string actor, string source, int eventId, string handler, string[] hosts, string? note, DateTime? dueDate = null)
        : ScenarioAction(actor)
    {
        public override string Describe() => $"建立交辦：{source} EventId {eventId}→{handler}（{string.Join("、", hosts)}）";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, "api/work-orders", new CreateWorkOrderRequest
            {
                Source = source,
                EventId = eventId,
                From = ctx.DataDateOf(1),
                To = ctx.DataDateOf(ctx.Day),
                HostIds = hosts.Select(ctx.HostId).ToList(),
                AssignMode = "single",
                HandlerId = ctx.UserId(handler),
                ScopeKind = WorkOrderScopes.Hosts,
                Note = note,
                DueDate = dueDate
            });
    }

    private sealed class CreateScopedOrderAction(string actor, string source, int eventId, string handler, string scopeKind,
        string[] hostGroups, bool autoAttach, DateTime? dueDate, string? note) : ScenarioAction(actor)
    {
        public override string Describe() => $"建立範圍交辦：{source} EventId {eventId}→{handler}（{scopeKind}"
            + (hostGroups.Length == 0 ? "" : "：" + string.Join("、", hostGroups)) + (autoAttach ? "，續掛" : "") + "）";

        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, "api/work-orders", new CreateWorkOrderRequest
            {
                Source = source,
                EventId = eventId,
                From = ctx.DataDateOf(1),
                To = ctx.DataDateOf(ctx.Day),
                GroupIds = hostGroups.Select(ctx.HostGroupId).ToList(),
                AssignMode = "single",
                HandlerId = ctx.UserId(handler),
                ScopeKind = scopeKind,
                AutoAttach = autoAttach,
                DueDate = dueDate,
                Note = note
            });
    }

    private sealed class SetHostOwnersAction(string actor, string host, string[] owners) : ScenarioAction(actor)
    {
        public override string Describe() => $"主機 {host} 負責人改為：{(owners.Length == 0 ? "（無）" : string.Join("、", owners))}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/hosts/{ctx.HostId(host)}/owners",
                new SetIdsRequest { Ids = owners.Select(ctx.UserId).ToList() });
    }

    private sealed class SetHostActiveAction(string actor, string host, bool active) : ScenarioAction(actor)
    {
        public override string Describe() => $"{(active ? "啟用" : "停用")}主機 {host}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/hosts/{ctx.HostId(host)}/active", new SetHostActiveRequest { Active = active });
    }

    private sealed class SetIssueOwnersAction(string actor, string source, int eventId, string[] owners) : ScenarioAction(actor)
    {
        public override string Describe() => $"問題 {source} EventId {eventId} 負責人：{string.Join("、", owners)}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, "api/admin/issue-owners",
                new SaveIssueOwnerRequest { SourceName = source, EventId = eventId, OwnerUserIds = owners.Select(ctx.UserId).ToList() });
    }

    private sealed class SetUserHostAccessAction(string actor, string account, string[] hostGroups) : ScenarioAction(actor)
    {
        public override string Describe() => $"{account} 的主機授權改為：{(hostGroups.Length == 0 ? "（無）" : string.Join("、", hostGroups))}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var group = ctx.Site.Get<IUserGroupStore>().GetAll().FirstOrDefault(g => g.GroupName == "ug-" + account)
                ?? throw new ScenarioUnresolvedException($"站台沒有使用者「{account}」的專屬群組");
            // 情境動作語意是「改為」：授權寫入只有增減，這裡以當下授權算出差異再送
            var wanted = hostGroups.Select(ctx.HostGroupId).ToList();
            var current = ctx.Site.Get<IGroupAccessStore>().GetAll()
                .Where(a => a.UserGroupId == group.GroupId).Select(a => a.HostGroupId).ToList();
            return ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/access/{group.GroupId}",
                new SetAccessRequest { Grant = wanted.Except(current).ToList(), Revoke = current.Except(wanted).ToList() });
        }
    }

    private sealed class ReassignAction(string actor, OrderRef order, string newHandler) : ScenarioAction(actor)
    {
        public override string Describe() => $"改派 {order.Describe()}→{newHandler}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{order.Resolve(ctx)}/reassign",
                new ReassignWorkOrderRequest { HandlerId = ctx.UserId(newHandler) });
    }

    private sealed class ReturnAction(string actor, OrderRef order, string note) : ScenarioAction(actor)
    {
        public override string Describe() => $"退回 {order.Describe()}（{note}）";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{order.Resolve(ctx)}/return", new ReturnWorkOrderRequest { Note = note });
    }

    private sealed class AdminCloseAction(string actor, OrderRef order, string status, string? reason, bool blockRedispatch) : ScenarioAction(actor)
    {
        public override string Describe() => $"代為結案 {order.Describe()}：{status}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{order.Resolve(ctx)}/admin-close",
                new AdminCloseWorkOrderRequest { Status = status, Reason = reason, BlockRedispatch = blockRedispatch });
    }

    private sealed class RestoreDismissedAction(string actor, string host, string source, int eventId) : ScenarioAction(actor)
    {
        public override string Describe() => $"恢復自動派工：{host} 的 {source} EventId {eventId}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var blocked = DispatchContext.DismissedCases(ctx.Site.Get<IIssueCaseStore>().GetMany(new[] { host }))
                .FirstOrDefault(c => string.Equals(c.SourceName, source, StringComparison.OrdinalIgnoreCase) && c.EventId == eventId)
                ?? throw new ScenarioUnresolvedException($"{host} 的 {source} EventId {eventId} 目前沒有被擋住");
            return ctx.Site.SendAsync(Actor, HttpMethod.Post, "api/work-orders/dismissed/restore",
                new RestoreDismissedRequest { HostName = host, IssueKey = blocked.IssueKey });
        }
    }

    private sealed class RestoreDismissedIssueAction(string actor, string source, int eventId) : ScenarioAction(actor)
    {
        public override string Describe() => $"恢復整個問題的自動派工：{source} EventId {eventId}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, "api/work-orders/dismissed/restore-issue",
                new RestoreDismissedIssueRequest { Source = source, EventId = eventId });
    }

    private sealed class CancelAction(string actor, OrderRef order, string? reason) : ScenarioAction(actor)
    {
        public override string Describe() => $"取消 {order.Describe()}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{order.Resolve(ctx)}/cancel", new CancelWorkOrderRequest { Reason = reason });
    }

    private sealed class MuteAction(string actor, string source, int eventId, int days, string reason, string existingOrders) : ScenarioAction(actor)
    {
        public override string Describe() => $"靜音 {source} EventId {eventId} {days} 天（既有單：{existingOrders}）";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/issue-owners/{Esc(source)}/{eventId}/mute",
                new SetIssueMuteRequest { Days = days, Reason = reason, ExistingOrders = existingOrders });
    }

    private sealed class UnmuteAction(string actor, string source, int eventId) : ScenarioAction(actor)
    {
        public override string Describe() => $"解除靜音 {source} EventId {eventId}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Delete, $"api/admin/issue-owners/{Esc(source)}/{eventId}/mute");
    }

    private sealed class DisableUserAction(string actor, string account) : ScenarioAction(actor)
    {
        public override string Describe() => $"停用使用者 {account}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var spec = ctx.UserSpec(account);
            return ctx.Site.SendAsync(Actor, HttpMethod.Post, "api/admin/users", new SaveUserRequest
            {
                Account = account,
                DisplayName = account,
                Email = spec.HasEmail ? $"{account}@scenario.test" : null,
                Active = false
            });
        }
    }

    private sealed class SetHostGroupsAction(string actor, string host, string[] groups) : ScenarioAction(actor)
    {
        public override string Describe() => $"主機 {host} 改屬群組：{(groups.Length == 0 ? "（無）" : string.Join("、", groups))}";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/hosts/{ctx.HostId(host)}/groups",
                new SetIdsRequest { Ids = groups.Select(ctx.HostGroupId).ToList() });
    }

    /// <summary>管理者在設定頁改「管理者提醒間隔天數」：讀目前設定、只改這一欄、整份存回（與設定頁同一支 API）</summary>
    public static ScenarioAction SetEscalationReminderDays(string actor, int days) => new SetEscalationReminderDaysAction(actor, days);

    private sealed class SetEscalationReminderDaysAction(string actor, int days) : ScenarioAction(actor)
    {
        public override string Describe() => $"設定頁把管理者提醒間隔改為 {days} 天";
        internal override async Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var got = await ctx.Site.SendAsync(Actor, HttpMethod.Get, "api/admin/settings");
            if (got.StatusCode != 200 || got.Body == null) return got;
            using var doc = System.Text.Json.JsonDocument.Parse(got.Body);
            var data = doc.RootElement.GetProperty("data").GetRawText();
            var request = System.Text.Json.JsonSerializer.Deserialize<UpdateSystemSettingsRequest>(
                data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            request.ExpectedRevision = doc.RootElement.GetProperty("data").GetProperty("revision").GetString();
            // 測試站台的設定存檔沒有帶網路層重試次數（出廠值 3 由設定頁表單補上），這裡照設定頁補成出廠值
            if (request.AiRetryCount < 1) request.AiRetryCount = new SystemSettings().AiRetryCount;
            request.EscalationReminderDays = days;
            return await ctx.Site.SendAsync(Actor, HttpMethod.Put, "api/admin/settings", request);
        }
    }

    private sealed class SetDispatchPausedAction(string actor, string account, bool paused) : ScenarioAction(actor)
    {
        public override string Describe() => $"{(paused ? "暫停" : "恢復")} {account} 接單";
        internal override Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx) =>
            ctx.Site.SendAsync(Actor, HttpMethod.Put, $"api/admin/users/{ctx.UserId(account)}/dispatch-paused",
                new SetUserDispatchPausedRequest { Paused = paused });
    }
}
