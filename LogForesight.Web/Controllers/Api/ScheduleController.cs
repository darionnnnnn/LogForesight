using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 排程設定與手動觸發（docs/WEB-SCHEDULER-PLAN.md §1.4.4，「排程作業」頁）。
///
/// 讀端（<see cref="GetOptions"/>／<see cref="GetStatus"/>）開放 DevMonitor 或 Maintain，寫端
/// （設定／預覽／觸發／停止）僅 Maintain——與側欄「排程作業」入口、頁面權限（PagesController.Runs）
/// 同一套：dev 看得到排程長什麼樣子與有沒有在跑，只有 admin/serverAdmin 能改。
/// </summary>
[ApiController]
[Route("api/admin/schedule")]
public class ScheduleController : ControllerBase
{
    private readonly IScheduleOptionsStore _optionsStore;
    private readonly SchedulerHostedService _scheduler;
    private readonly SchedulerRunState _runState;
    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;

    public ScheduleController(
        IScheduleOptionsStore optionsStore, SchedulerHostedService scheduler, SchedulerRunState runState,
        IHostStore hosts, ISentinelStore sentinels, IAuditService audit, ICurrentUser currentUser)
    {
        _optionsStore = optionsStore;
        _scheduler = scheduler;
        _runState = runState;
        _hosts = hosts;
        _sentinels = sentinels;
        _audit = audit;
        _currentUser = currentUser;
    }

    [HttpGet("options")]
    [Permission(Capability.DevMonitor, Capability.Maintain)]
    public ApiResponse<ScheduleOptionsDto> GetOptions() => ApiResponse<ScheduleOptionsDto>.Ok(ToDto(_optionsStore.Get()));

    [HttpPut("options")]
    [Permission(Capability.Maintain)]
    public ApiResponse<ScheduleOptionsDto> SaveOptions([FromBody] SaveScheduleOptionsRequest request)
    {
        var windows = request.Windows.Select(w => new ScheduleWindow { Start = w.Start, End = w.End }).ToList();
        var validation = ScheduleCalculator.Validate(windows);
        if (!validation.IsValid)
            throw DomainException.Validation(string.Join("；", validation.Errors));

        var saved = _optionsStore.Update(o =>
        {
            o.Enabled = request.Enabled;
            o.Windows = windows;
            o.DebugDump = request.DebugDump;
            o.UpdatedByAccount = _currentUser.Account;
        });

        _audit.Record(
            action: AuditActions.ScheduleOptionsUpdate,
            summary: $"更新排程設定：{(saved.Enabled ? "已啟用" : "未啟用")}、{saved.Windows.Count} 個執行窗口" +
                     (saved.DebugDump ? "，AI 診斷傾印開啟中" : ""),
            targetKind: "schedule",
            detail: new { saved.Enabled, saved.Windows, saved.DebugDump });

        return ApiResponse<ScheduleOptionsDto>.Ok(ToDto(saved));
    }

    [HttpGet("status")]
    [Permission(Capability.DevMonitor, Capability.Maintain)]
    public ApiResponse<ScheduleStatusDto> GetStatus()
    {
        var options = _optionsStore.Get();

        return ApiResponse<ScheduleStatusDto>.Ok(new ScheduleStatusDto
        {
            IsRunning = _runState.IsRunning,
            TriggerText = TriggerText(_runState.Trigger),
            StartedAt = _runState.StartedAt,
            LatestMessage = _runState.LatestMessage,
            CanStop = _runState.IsRunning,
            ScheduleEnabled = options.Enabled,
            NextTriggerTime = options.Enabled ? ScheduleCalculator.NextTriggerTime(DateTime.Now, options.Windows) : null
        });
    }

    /// <summary>執行前預覽：這個範圍實際會涵蓋幾台主機（docs/WEB-SCHEDULER-PLAN.md §1.4.4）</summary>
    [HttpGet("run-preview")]
    [Permission(Capability.Maintain)]
    public ApiResponse<RunPreviewDto> RunPreview([FromQuery] string scope, [FromQuery] string? segment, [FromQuery] long? hostId)
    {
        var (_, hostIds, includesLocal) = ResolveScope(scope, segment, hostId);
        return ApiResponse<RunPreviewDto>.Ok(new RunPreviewDto
        {
            HostCount = (hostIds?.Count ?? 0) + (includesLocal ? 1 : 0)
        });
    }

    [HttpPost("run")]
    [Permission(Capability.Maintain)]
    public async Task<ApiResponse<TriggerRunResultDto>> Run([FromBody] TriggerRunRequest request)
    {
        var (runScope, hostIds, _) = ResolveScope(request.Scope, request.Segment, request.HostId);
        if (runScope == RunScope.NetiqHosts && (hostIds == null || hostIds.Count == 0))
            throw DomainException.Validation("找不到符合條件的主機，請確認網段或主機是否正確。");

        var runRequest = new RunRequest
        {
            Scope = runScope,
            HostIds = hostIds,
            BackfillOverride = request.BackfillDays,
            Trigger = $"manual:{_currentUser.Account}"
        };

        _audit.Record(
            action: AuditActions.ScheduleManualRun,
            summary: $"手動觸發分析執行（範圍：{ScopeText(request.Scope)}" +
                     (request.Segment != null ? $"「{request.Segment}」" : "") +
                     (hostIds != null ? $"，涵蓋 {hostIds.Count} 台主機" : "") + "）",
            targetKind: "schedule",
            detail: new { request.Scope, request.Segment, request.HostId, request.BackfillDays });

        var started = await _scheduler.TriggerRunAsync(runRequest);
        return ApiResponse<TriggerRunResultDto>.Ok(new TriggerRunResultDto
        {
            Started = started,
            Message = started ? "已開始執行。" : "目前已有其他執行進行中，請稍後再試（不會排隊）。"
        });
    }

    [HttpPost("cancel")]
    [Permission(Capability.Maintain)]
    public ApiResponse Cancel()
    {
        if (!_runState.TryCancel())
            throw DomainException.Validation("目前沒有正在執行的分析。");

        _audit.Record(action: AuditActions.ScheduleManualCancel, summary: "手動停止進行中的執行（優雅停止，停在主機日邊界）", targetKind: "schedule");
        return ApiResponse.Ok();
    }

    /// <summary>
    /// scope=all/segment/host 解析成 orchestrator 看得懂的 RunScope＋主機清單。
    /// 網段解析與比對複用 <see cref="CidrMatcher"/>（與 NetIQ 匯入精靈同一套，不寫第二份）；
    /// 主機清單複用 <see cref="StoreHostListProvider"/>（與 --host-list／NetIQ 機房分析同一份
    /// 「實際會被查詢」語意，網段找不到符合的主機不會被靜默吞掉）。
    /// </summary>
    private (RunScope Scope, List<long>? HostIds, bool IncludesLocal) ResolveScope(string scope, string? segment, long? hostId)
    {
        switch (scope)
        {
            case "all":
                var allTargets = new StoreHostListProvider(_hosts, _sentinels).GetHostList();
                return (RunScope.Full, allTargets.ByServer.Values.SelectMany(v => v).Select(t => t.HostId).ToList(), true);

            case "segment":
                if (string.IsNullOrWhiteSpace(segment))
                    throw DomainException.Validation("請輸入要執行的網段。");

                // 輸入語法與 NetIQ 匯入精靈一致（前綴 10.1.2 或 CIDR 10.1.2.0/24），
                // 共用同一份 NormalizeSubnetPrefix；比對主機清單改用 CidrMatcher（同一套邏輯的
                // 另一個消費端——精靈用正規化後的前綴組 Sentinel 查詢字串，這裡組本地萬用字元比對）
                string normalizedPrefix;
                try
                {
                    normalizedPrefix = SentinelQueryBuilder.NormalizeSubnetPrefix(segment);
                }
                catch (ArgumentException ex)
                {
                    throw DomainException.Validation(ex.Message);
                }
                var range = CidrMatcher.Parse($"{normalizedPrefix}.*")!;

                var netiqList = new StoreHostListProvider(_hosts, _sentinels).GetHostList();
                var matched = netiqList.ByServer.Values
                    .SelectMany(v => v)
                    .Where(t => CidrMatcher.Matches(range, t.IpAddress))
                    .Select(t => t.HostId)
                    .ToList();
                return (RunScope.NetiqHosts, matched, false);

            case "host":
                if (hostId is not { } id) throw DomainException.Validation("請指定要更新的主機。");
                var host = _hosts.Get(id) ?? throw DomainException.NotFound("找不到這台主機。");
                if (host.Source == "local") return (RunScope.LocalOnly, null, true);

                // NetIQ 主機：確認目前真的在會被查詢的清單內（Pollable），不然「1 台」的預覽會是假象——
                // 停用／待歸屬／IP 衝突／所屬 Sentinel 停用的主機實際執行時會被 orchestrator 濾掉、
                // 靜默變成 0 台，跟「不靜默少幾台」原則相悖，這裡先擋下並給出明確理由
                var pollableIds = new StoreHostListProvider(_hosts, _sentinels).GetHostList()
                    .ByServer.Values.SelectMany(v => v).Select(t => t.HostId).ToHashSet();
                if (!pollableIds.Contains(id))
                    throw DomainException.Validation(
                        $"「{host.HostName}」目前不會被查詢（可能已停用、待歸屬 Sentinel、IP 衝突，" +
                        "或所屬 Sentinel 已停用），請至主機頁確認狀態後再試。");

                return (RunScope.NetiqHosts, new List<long> { id }, false);

            default:
                throw DomainException.Validation($"範圍「{scope}」不合法，僅接受 all/segment/host。");
        }
    }

    private static string ScopeText(string scope) => scope switch
    {
        "all" => "全部主機",
        "segment" => "網段",
        "host" => "單一主機",
        _ => scope
    };

    private static string TriggerText(string? trigger) => trigger switch
    {
        null => "閒置",
        "schedule" => "排程",
        _ when trigger.StartsWith("manual:", StringComparison.Ordinal) => $"手動（{trigger["manual:".Length..]}）",
        _ => trigger
    };

    private static ScheduleOptionsDto ToDto(ScheduleOptions options) => new()
    {
        Enabled = options.Enabled,
        Windows = options.Windows.Select(w => new ScheduleWindowDto { Start = w.Start, End = w.End }).ToList(),
        DebugDump = options.DebugDump,
        UpdatedAt = options.UpdatedAt,
        UpdatedByAccount = options.UpdatedByAccount,
        NextTriggerTime = options.Enabled ? ScheduleCalculator.NextTriggerTime(DateTime.Now, options.Windows) : null
    };
}
