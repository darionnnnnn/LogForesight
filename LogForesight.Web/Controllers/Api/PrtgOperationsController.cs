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
    MailNotifyStateStore mailState) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        var settingsRevision = new SystemSettingsStore(backend.Blob("system_settings")).Get().Revision;
        // 先授權才取前 100 筆；不回傳收件人地址、原始判定或不可見主機統計。
        var state = mailState.Get();
        var intents = state.UrgentOutbox.Values.Where(i => visible.Contains(i.HostId))
            .OrderByDescending(i => i.UpdatedAtUtc).Take(100).Select(i => new
            {
                i.HostId, i.RecordDate, Status = MailNotificationService.EffectiveUrgentStatusForRead(i, state.UrgentSentKeys), i.UpdatedAtUtc,
                AdoptedSettingsRevision = i.SettingsRevision, ExpectedSettingsRevision = settingsRevision,
                Recipients = i.Recipients.Values.GroupBy(s => s).Select(g => new { Status = g.Key, Count = g.Count() })
            }).ToArray();
        return Ok(ApiResponse<object>.Ok(new { Notifications = intents,
            OperationVersions = PrtgOperationScope.ReadVersions(),
            Semantics = "SMTP 接受不代表信箱實收；PRTG 問題只進每日／每週摘要，不寄即時信。" }));
    }
}
