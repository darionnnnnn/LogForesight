using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 寫入測試的「前後快照」：一律經站台自己的 store 讀（與請求同一份資料），序列化成 JSON 字串逐字比對。
/// 兩種粒度：<see cref="Global"/> 供角色閘門（被擋的請求不得在任何地方寫入）；
/// 其餘供寫入矩陣（指向某個主機日、某張交辦單、某個變更編號、某個使用者的偏好）。
/// </summary>
public sealed class AuthorizationDataProbe
{
    private readonly AuthorizationHttpFixture _fx;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public AuthorizationDataProbe(AuthorizationHttpFixture fx) => _fx = fx;

    private static string Ser(object? value) => JsonSerializer.Serialize(value, Json);

    private T Get<T>() where T : notnull
    {
        using var scope = _fx.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private string[] HostNames => _fx.Hosts.Select(h => h.Name).ToArray();

    // ── 全域指紋（角色閘門用）────────────────────────────────────────────────

    /// <summary>主機、人員、群組、授權、系統設定、問題負責人、抑制、處理狀態、案件、交辦單與事件、權限異動確認、個人偏好</summary>
    public string Global()
    {
        var parts = new List<object?>
        {
            Get<IHostStore>().GetAll(),
            Get<IUserStore>().GetAll(),
            Get<IUserGroupStore>().GetAll(),
            Get<IHostGroupStore>().GetAll(),
            Get<IGroupAccessStore>().GetAll(),
            Get<ISystemSettingsStore>().Get(),
            Get<IIssueOwnerStore>().GetAll(),
            Get<ISuppressionStore>().LoadAll(),
            Get<IRecordHandlingStore>().GetMany(HostNames, AuthorizationHttpFixture.Day.AddDays(-30), AuthorizationHttpFixture.Day.AddDays(30)),
            Get<IIssueHandlingStore>().GetMany(HostNames, AuthorizationHttpFixture.Day.AddDays(-30), AuthorizationHttpFixture.Day.AddDays(30)),
            Get<IIssueCaseStore>().GetMany(HostNames),
            Get<PermissionChangeStore>().GetConfirmations(Enumerable.Range(1, 4).Select(n => $"authz-change-{n}")),
            _fx.Identities.Where(i => i.UserId > 0).Select(i => Get<UserPreferenceStore>().Get(i.UserId))
        };

        var orders = Get<IWorkOrderStore>();
        foreach (var o in new[] { _fx.WorkOrderA, _fx.WorkOrderC })
        {
            parts.Add(orders.Get(o.Id));
            parts.Add(orders.ListEvents(o.Id));
        }

        parts.Add(orders.GetAllActive().Select(o => o.WorkOrderId));
        return Ser(parts);
    }

    // ── 日層級與問題層級處理狀態 ────────────────────────────────────────────

    public RecordHandling? DayHandling(HostFx h) => Get<IRecordHandlingStore>().Get(h.Name, AuthorizationHttpFixture.Day);

    public string DaySnapshot(HostFx h) => Ser(new
    {
        Handling = DayHandling(h),
        Logs = Get<IRecordHandlingStore>().GetLogs(h.Name, AuthorizationHttpFixture.Day)
    });

    public string IssueSnapshot(HostFx h) => Ser(new
    {
        Issues = Get<IIssueHandlingStore>().GetForDay(h.Name, AuthorizationHttpFixture.Day),
        OpenCases = Get<IIssueCaseStore>().GetOpenForHost(h.Name)
    });

    /// <summary>一台主機日的完整處理資料（日層級＋問題層級＋案件）</summary>
    public string HostDaySnapshot(HostFx h) => DaySnapshot(h) + IssueSnapshot(h);

    /// <summary>寫入前的原始狀態，供正向對照結束後復原（讀取矩陣與其他列依賴它們）</summary>
    public sealed record HostDayState(RecordHandling? Day, List<IssueHandling> Issues, List<IssueCase> Cases);

    public HostDayState CaptureHostDay(HostFx h) => new(
        DayHandling(h),
        Get<IIssueHandlingStore>().GetForDay(h.Name, AuthorizationHttpFixture.Day),
        Get<IIssueCaseStore>().GetOpenForHost(h.Name));

    public void RestoreHostDay(HostFx h, HostDayState state)
    {
        var day = AuthorizationHttpFixture.Day;
        var issues = Get<IIssueHandlingStore>();
        foreach (var now in issues.GetForDay(h.Name, day))
            if (!state.Issues.Any(i => i.IssueKey == now.IssueKey)) issues.Clear(h.Name, day, now.IssueKey);
        if (state.Issues.Count > 0) issues.SaveMany(state.Issues);

        if (state.Day != null) Get<IRecordHandlingStore>().Save(state.Day);

        var cases = Get<IIssueCaseStore>();
        foreach (var c in state.Cases) cases.Save(c);
    }

    // ── 權限異動確認 ────────────────────────────────────────────────────────

    public string PermConfirmation(string changeId) =>
        Ser(Get<PermissionChangeStore>().GetConfirmations(new[] { changeId }));

    public void AddPermChange(HostFx h, string changeId) =>
        Get<PermissionChangeStore>().AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = changeId,
                HostName = h.Name,
                DetectedAt = AuthorizationHttpFixture.Day.AddHours(4),
                Target = Sentinels.Of(h.N, SentinelKind.Perm),
                ChangeType = "成員新增",
                Before = "-",
                After = "-",
                AlertText = Sentinels.Of(h.N, SentinelKind.Perm) + " 告警"
            }
        });

    // ── 交辦單 ──────────────────────────────────────────────────────────────

    public string OrderSnapshot(long orderId)
    {
        var orders = Get<IWorkOrderStore>();
        return Ser(new
        {
            Order = orders.Get(orderId),
            Events = orders.ListEvents(orderId),
            Cases = Get<IIssueCaseStore>().GetByWorkOrder(orderId, 0, 500)
        });
    }

    /// <summary>
    /// 對「夾具原有的交辦單」做寫入前先擷取，回傳復原動作：還原單本身、單底下的案件，以及成員所在主機當天的處理狀態。
    /// 事件只能附加不能刪，復原後多出的回覆事件會留著（不影響其他列：它們只比對各自的前後快照）。
    /// </summary>
    public Action CaptureFixtureOrderRestore(long orderId, HostFx? memberHost)
    {
        var orders = Get<IWorkOrderStore>();
        var order = orders.Get(orderId);
        var cases = Get<IIssueCaseStore>().GetByWorkOrder(orderId, 0, 500);
        var day = memberHost != null ? CaptureHostDay(memberHost) : null;
        return () =>
        {
            if (order != null)
            {
                // Save 以 UpdatedAt 當併發權杖：先換成資料庫目前的值才寫得回去（回覆已把它推進了）
                order.UpdatedAt = orders.Get(orderId)!.UpdatedAt;
                orders.Save(order);
            }
            var store = Get<IIssueCaseStore>();
            foreach (var c in cases) store.Save(c);
            if (memberHost != null && day != null) RestoreHostDay(memberHost, day);
        };
    }

    /// <summary>為處理人建一張進行中的交辦單（正向對照用，每列各建一張避免互相干擾）；
    /// <paramref name="withMember"/> 時附一個該主機上的獨立問題案件（問題是這張單專屬的，不碰夾具的問題 A／B）</summary>
    public long NewOrder(IdentityFx handler, HostFx host, string tag, bool withMember)
    {
        var orders = Get<IWorkOrderStore>();
        var creator = _fx.Admin;
        var issue = new LogIssueSignature
        {
            LogName = "System",
            Source = Sentinels.Prefix(host.N) + "WO-" + tag,
            EventId = 2000 + Math.Abs(tag.GetHashCode() % 900),
            EntryType = LogEntryType.Error,
            Severity = IssueSeverity.High
        };
        var id = orders.Insert(new WorkOrder
        {
            HandlerId = handler.UserId,
            IssueLabel = issue.SourceEventLabel,
            SourceName = issue.Source,
            EventId = issue.EventId,
            Note = $"SENTINEL-WO-W-{tag}-NOTE",
            CreatedById = creator.UserId,
            CreatedByAccount = creator.Account,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });
        orders.AppendEvent(new WorkOrderEvent
        {
            WorkOrderId = id,
            Action = WorkOrderEventActions.Created,
            ActorId = creator.UserId,
            ActorAccount = creator.Account,
            Note = $"SENTINEL-WO-W-{tag}-EVENT",
            CreatedAt = DateTime.Now
        });
        if (withMember)
        {
            Get<IIssueCaseStore>().Save(new IssueCase
            {
                CaseId = Guid.NewGuid().ToString("N"),
                HostName = host.Name,
                IssueKey = IssueSignatureKey.For(issue),
                IssueLabel = issue.SourceEventLabel,
                Status = IssueHandlingStatuses.InProgress,
                HandlerId = handler.UserId,
                FirstLinkedDate = AuthorizationHttpFixture.Day,
                LastLinkedDate = AuthorizationHttpFixture.Day,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                CreatedByAccount = creator.Account,
                WorkOrderId = id
            });
        }

        return id;
    }

    // ── 個人偏好 ────────────────────────────────────────────────────────────

    public string Preferences(IdentityFx who) => Ser(Get<UserPreferenceStore>().Get(who.UserId));

    /// <summary>除了本人以外，所有人的偏好（驗證「只寫到自己」）</summary>
    public string OthersPreferences(IdentityFx me) =>
        Ser(_fx.Identities.Where(i => i.UserId > 0 && i.UserId != me.UserId).Select(i => Get<UserPreferenceStore>().Get(i.UserId)));

    public void ClearNotePhrases(IdentityFx who) => Get<UserPreferenceStore>().SetNotePhrases(who.UserId, new List<string>());

    public void SetSetupGuideHidden(IdentityFx who, bool hidden) => Get<UserPreferenceStore>().SetSetupGuideHidden(who.UserId, hidden);
}
