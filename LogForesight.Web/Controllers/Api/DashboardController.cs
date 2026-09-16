using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>儀表板（docs/WEB-SPEC.md §9.1）。一次回傳全部區塊，避免首頁發五個請求</summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    /// <summary>days 未指定時的預設期間（§12：原 appsettings 的 Ui:DashboardDefaultDays，
    /// 前端另有 localStorage 期間記憶，這個值只是 API 的 fallback，改為常數）</summary>
    public const int DefaultDays = 7;

    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet("summary")]
    public ApiResponse<DashboardDto> Summary([FromQuery] int? days) =>
        // Clamp 同 host-detail 慣例（1..90）：未夾住時任意整數（含負數／超大值）會直接進
        // DateTime.Today.AddDays(-days+1) 算出離譜的查詢區間
        ApiResponse<DashboardDto>.Ok(_dashboard.GetSummary(Math.Clamp(days ?? DefaultDays, 1, 90)));
}

/// <summary>
/// 執行中告示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）：**任何登入者**都讀得到，
/// 刻意不掛 <c>[Permission]</c>——分析與站台跑在同一個行程，變慢的是所有人的畫面，
/// 只讓維運看得到原因等於沒有配套。回傳只有「在不在跑、跑到哪」，不含排程設定與上次成敗
/// （那些在 <c>/api/admin/schedule/status</c>，維運視角）；**觸發者**（誰按的）只對具
/// DevMonitor 或 Maintain 的使用者填，一般使用者拿到 null——告示對他們仍照常出現，只是不講是誰。
///
/// 路由刻意不放在 <c>api/admin/</c> 底下：那個前綴在本專案是「需要管理權限」的訊號，
/// 一般使用者讀得到的東西擺進去，只會讓下一個人誤判這支的權限範圍。
/// </summary>
[ApiController]
[Route("api/run-activity")]
public class RunActivityController : ControllerBase
{
    private readonly SchedulerRunState _runState;
    private readonly AiAnalysisRunState _aiRunState;
    private readonly IUserStore _users;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly ICurrentUser _currentUser;

    public RunActivityController(
        SchedulerRunState runState,
        AiAnalysisRunState aiRunState,
        IUserStore users,
        IUserDisplayNameService userDisplayNames,
        ICurrentUser currentUser)
    {
        _runState = runState;
        _aiRunState = aiRunState;
        _users = users;
        _userDisplayNames = userDisplayNames;
        _currentUser = currentUser;
    }

    /// <summary>觸發者姓名屬維運資訊：與 <c>/api/admin/schedule/status</c> 同一道門檻，其餘角色為 null</summary>
    private bool CanSeeTrigger => _currentUser.Has(Capability.DevMonitor) || _currentUser.Has(Capability.Maintain);

    [HttpGet]
    public ApiResponse<RunActivityDto> Get()
    {
        // 單一告示只能顯示一條進度，主／本機軌的取捨由 SchedulerRunState.LatestActivity
        // 單點決定（回饋十四輪 UI-6 體檢）——這支與健康診斷共用同一個選擇邏輯，不各自記得。
        var (phase, done, total) = _runState.LatestActivity();

        if (_runState.IsRunning || phase != null)
        {
            return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
            {
                IsRunning = _runState.IsRunning,
                Done = done,
                Total = total,
                // 本機路徑是逐日回補（天），NetIQ 機房路徑是逐台主機（台）——「主機日」這個單位
                // 一般使用者沒有概念。
                UnitText = phase switch
                {
                    "local" => "天",
                    "netiq" => "台",
                    _ => null
                },
                // 誰觸發的：與排程頁共用 RunTriggerText，兩邊對同一個 Trigger 值算出相同文字。
                // 沒在跑時不講（閒置沒有「觸發者」可言）。
                TriggerText = _runState.IsRunning && CanSeeTrigger
                    ? RunTriggerText.Of(_runState.Trigger, _userDisplayNames, _users)
                    : null,
                // 取數分支：互斥判斷要分得出「取數在跑」與「只有 AI 在跑」（見 DTO 註解）
                IsFetchRun = _runState.IsRunning
            });
        }

        // 取數閒置時輪到 AI 排程（回饋三十五輪批次D：AI 補寫拆成獨立排程後，
        // 它單獨在跑時全站也要看得到——優先序 取數 > AI，兩者同時跑時取數較有代表性）
        var ai = _aiRunState.Snapshot();
        return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
        {
            IsRunning = ai.IsRunning,
            Done = ai.ProgressDone,
            Total = ai.ProgressTotal,
            UnitText = ai.IsRunning ? "件" : null,
            TriggerText = ai.IsRunning && CanSeeTrigger
                ? RunTriggerText.Of(ai.Trigger, _userDisplayNames, _users)
                : null,
            // AI 分支：取數此時必定閒置（上面的分支沒進來），主機更新不該被擋
            IsFetchRun = false
        });
    }
}

/// <summary>主機詳情／時間軸（§9.4）</summary>
[ApiController]
[Route("api/host-detail")]
public class HostDetailController : ControllerBase
{
    private readonly RecordDetailQueryService _service;
    private readonly EfPrtgStore _prtgStore;
    private readonly IVisibilityService _visibility;

    public HostDetailController(RecordDetailQueryService service, EfPrtgStore prtgStore, IVisibilityService visibility)
    {
        _service = service;
        _prtgStore = prtgStore;
        _visibility = visibility;
    }

    [HttpGet("{hostId:long}")]
    public ApiResponse<HostDetailDto> Get(long hostId, [FromQuery] int days = 30) =>
        ApiResponse<HostDetailDto>.Ok(_service.GetHostDetail(hostId, Math.Clamp(days, 7, 90)));

    /// <summary>某個問題（Source+EventId）的逐日發生明細（docs/archive/FEEDBACK-4-PLAN.md §3）：
    /// 重點問題彙總表某一列展開時查詢，days 與外層時間軸期間一致</summary>
    [HttpGet("{hostId:long}/issues")]
    public ApiResponse<HostIssueOccurrenceDto> Issues(
        long hostId, [FromQuery] string source, [FromQuery] int eventId, [FromQuery] int days = 30) =>
        ApiResponse<HostIssueOccurrenceDto>.Ok(_service.GetHostIssueOccurrences(hostId, source, eventId, Math.Clamp(days, 7, 90)));

    /// <summary>取得指定主機目前對應的 PRTG device 與 sensor 清單</summary>
    [HttpGet("{hostId:long}/prtg")]
    public ApiResponse<HostPrtgMappingDto> Prtg(long hostId)
    {
        // 授權過濾在查詢層強制（docs/DB-SPEC.md）：同 controller 的另外兩個端點都經
        // RecordDetailQueryService 內的 EnsureVisible，這支不走 service，必須自己檢查——
        // 否則任何登入者都能用任意 hostId 列出別人主機的 PRTG device 與 sensor
        _visibility.EnsureVisible(hostId);

        var store = _prtgStore;
        var host = _visibility.GetVisibleHosts().FirstOrDefault(h => h.HostId == hostId);
        var ipExcluded = false;
        string? excludedIp = null;
        if (!string.IsNullOrWhiteSpace(host?.IpAddress))
        {
            // 正規化回 null 代表「不是合法 IP」，兩邊都 null 時 `==` 會成立而誤判成命中
            // （主機 IP 欄填了 DNS 名稱 ＋ 排除清單裡有舊的非 IP 列，就會湊出這個組合）。
            var normHostIp = PrtgHostMapper.NormalizeIp(host.IpAddress);
            var match = normHostIp == null
                ? null
                : store.GetIpExcludes().FirstOrDefault(e => PrtgHostMapper.NormalizeIp(e.Ip) == normHostIp);
            if (match != null)
            {
                ipExcluded = true;
                excludedIp = match.Ip;
            }
        }

        // 只查這一台主機的對應列（回饋四十五輪 B5）：原本是把「最近一次對應」整張表
        // （數千列起跳）讀回記憶體再 Where 出一台主機的那幾列。語意不變——
        // MapDate 仍是「最近一次有對應資料的日期」，該日存在但這台沒有對應列時照樣回傳日期。
        var (mapDate, targetRows) = store.GetLatestHostMapForHost(hostId);
        if (mapDate == null)
        {
            return ApiResponse<HostPrtgMappingDto>.Ok(new HostPrtgMappingDto
            {
                IpExcluded = ipExcluded,
                ExcludedIp = excludedIp
            });
        }

        // device 名稱一次查回（不逐 device 查）
        var deviceNames = store.GetDeviceNamesByObjids(targetRows.Select(r => r.DeviceObjid).Distinct().ToList());

        var devices = new List<HostPrtgDeviceDto>();
        foreach (var r in targetRows)
        {
            var sensors = store.GetSensorsByDevice(r.DeviceObjid)
                .Select(s => new HostPrtgSensorDto
                {
                    Objid = s.Objid,
                    Name = s.Name,
                    SensorType = s.SensorType,
                    Category = s.Category,
                    Paused = s.Paused,
                    Status = s.Status
                }).ToList();

            devices.Add(new HostPrtgDeviceDto
            {
                DeviceObjid = r.DeviceObjid,
                Name = deviceNames.TryGetValue(r.DeviceObjid, out var deviceName) ? deviceName : null,
                Ip = r.Ip,
                MapStatus = r.MapStatus,
                Note = r.Note,
                Sensors = sensors
            });
        }

        return ApiResponse<HostPrtgMappingDto>.Ok(new HostPrtgMappingDto
        {
            MapDate = mapDate,
            Devices = devices,
            IpExcluded = ipExcluded,
            ExcludedIp = excludedIp
        });
    }
}

/// <summary>報表（§9.6）</summary>
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly ReportService _reports;

    public ReportsController(ReportService reports)
    {
        _reports = reports;
    }

    [HttpGet("summary")]
    public ApiResponse<ReportSummaryDto> Summary(
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? handlingScope, [FromQuery] string? compare = null)
    {
        // 未帶 to 時的預設終點錨在昨天（回饋十九輪批次C）：前端一律會帶明確日期
        // toDefault 為昨天；fromDefault 依實際解析出的 to 往前 29 天（報表 JS 的 setRange 語意）
        var toDefault = DateTime.Today.AddDays(-1);
        var (fromDate, toDate) = ParseDateRange(
            from, to,
            fromDefault: (ParseDate(to) ?? toDefault).AddDays(-29),
            toDefault: toDefault,
            throwOnReversed: true,
            maxDays: 366);

        return ApiResponse<ReportSummaryDto>.Ok(_reports.GetSummary(fromDate!.Value, toDate!.Value, handlingScope, compare));
    }
    // 跨主機同簽章查詢（原 GET signature）已於 §4 併入「問題查詢」（eventId＋source＋依問題視角
    // 為嚴格超集），端點與 ReportService.FindSignature／SignatureHitDto 一併移除。
}
