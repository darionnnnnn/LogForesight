using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/operations"), Permission(Capability.Maintain)]
public sealed class PrtgOperationsController(StorageBackend backend, IVisibilityService visibility,
    MailNotifyStateStore mailState, MailNotificationService mail, PrtgSupplementReplay replay,
    BackgroundWorkGate gate, DataVersionStamp stamp, IAuditService audit, IHostStore hosts) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        var settingsRevision = new SystemSettingsStore(backend.Blob("system_settings")).Get().Revision;
        using var db = backend.CreateContext();
        var supplements = db.PrtgObservations.AsNoTracking().Where(r => r.ActiveKey != null && visible.Contains(r.HostId))
            .GroupBy(r => r.SupplementStatus).Select(g => new { Status = g.Key, Count = g.Count() }).ToArray();
        // 先授權才取前 100 筆；不回傳收件人地址、原始判定或不可見主機統計。
        var intents = mailState.Get().UrgentOutbox.Values.Where(i => visible.Contains(i.HostId))
            .OrderByDescending(i => i.UpdatedAtUtc).Take(100).Select(i => new
            {
                i.HostId, i.RecordDate, i.Status, i.UpdatedAtUtc,
                AdoptedSettingsRevision = i.SettingsRevision, ExpectedSettingsRevision = settingsRevision,
                Recipients = i.Recipients.Values.GroupBy(s => s).Select(g => new { Status = g.Key, Count = g.Count() })
            }).ToArray();
        return Ok(ApiResponse<object>.Ok(new { Supplements = supplements, Notifications = intents,
            OperationVersions = PrtgOperationScope.ReadVersions(),
            CanRetry = hosts.GetAll().All(h => visible.Contains(h.HostId)),
            Semantics = "SMTP 接受不代表信箱實收；部分成功重試可能重寄，案件與通知識別保持一致。過期補追加只進摘要。" }));
    }
    [HttpPost("retry")]
    public async Task<IActionResult> Retry(CancellationToken ct)
    {
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        // 全站後台工作含不可見範圍，不可由受限管理者啟動或重置別人的熔斷狀態。
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!policy.HostIds.All(visible.Contains) || hosts.GetAll().Any(h => !visible.Contains(h.HostId))) return Forbid();
        await gate.RunAsync("PRTG 補追加重試", () => Task.Run(() =>
        { if (replay.RunBatch(cancellationToken: ct) > 0) stamp.Bump(); }, ct), ct);
        // 不清除成功識別；只重新核對當前來源、路由與權限後處理尚未完成的意圖。
        mail.ResetRecipientFailureStreaks();
        await mail.NotifyAfterRunAsync(ct);
        audit.Record("prtg_operations_retry", "重試持久補追加與未完成郵件；保留成功去重識別。", "prtg", null);
        return Get();
    }
}
