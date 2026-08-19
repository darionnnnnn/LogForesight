using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 排程設定與手動觸發（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4，「排程作業」頁）。
///
/// 讀端（<see cref="GetOptions"/>／<see cref="GetStatus"/>）開放 DevMonitor 或 Maintain，寫端
/// （設定／預覽／觸發／停止）僅 Maintain——與側欄「排程作業」入口、頁面權限（PagesController.Runs）
/// 同一套：dev 看得到排程長什麼樣子與有沒有在跑，只有 admin/serverAdmin 能改。
/// </summary>
[ApiController]
[Route("api/admin/schedule")]
public class ScheduleController : ControllerBase
{
    private readonly ScheduleOptionsStore _optionsStore;
    private readonly SchedulerHostedService _scheduler;
    private readonly SchedulerRunState _runState;
    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUserStore _users;
    private readonly IAnalysisRecordQuery _records;
    private readonly IWebAiService? _webAi;

    public ScheduleController(
        ScheduleOptionsStore optionsStore, SchedulerHostedService scheduler, SchedulerRunState runState,
        IHostStore hosts, ISentinelStore sentinels, IAuditService audit, ICurrentUser currentUser, IUserStore users,
        IAnalysisRecordQuery records, IWebAiService? webAi = null)
    {
        _optionsStore = optionsStore;
        _scheduler = scheduler;
        _runState = runState;
        _hosts = hosts;
        _sentinels = sentinels;
        _audit = audit;
        _currentUser = currentUser;
        _users = users;
        _records = records;
        _webAi = webAi;
    }

    [HttpGet("options")]
    [Permission(Capability.DevMonitor, Capability.Maintain)]
    public ApiResponse<ScheduleOptionsDto> GetOptions() => ApiResponse<ScheduleOptionsDto>.Ok(ToDto(_optionsStore.Get()));

    [HttpPut("options")]
    [Permission(Capability.Maintain)]
    public ApiResponse<ScheduleOptionsDto> SaveOptions([FromBody] SaveScheduleOptionsRequest request)
    {
        var validation = ScheduleCalculator.Validate(request.Windows);
        if (!validation.IsValid)
            throw DomainException.Validation(string.Join("；", validation.Errors));

        var saved = _optionsStore.Update(o =>
        {
            o.Enabled = request.Enabled;
            o.Windows = request.Windows;
            o.DebugDump = request.DebugDump;
            o.LocalAnalysisEnabled = request.LocalAnalysisEnabled;
            o.UpdatedByAccount = _currentUser.Account;
        });

        _audit.Record(
            action: AuditActions.ScheduleOptionsUpdate,
            summary: $"更新排程設定：{(saved.Enabled ? "已啟用" : "未啟用")}、{saved.Windows.Count} 個執行窗口" +
                     (saved.DebugDump ? "，AI 診斷傾印開啟中" : "") +
                     (saved.LocalAnalysisEnabled ? "" : "，本機分析已停用"),
            targetKind: "schedule",
            detail: new { saved.Enabled, saved.Windows, saved.DebugDump, saved.LocalAnalysisEnabled });

        return ApiResponse<ScheduleOptionsDto>.Ok(ToDto(saved));
    }

    [HttpGet("status")]
    [Permission(Capability.DevMonitor, Capability.Maintain)]
    public ApiResponse<ScheduleStatusDto> GetStatus()
    {
        var options = _optionsStore.Get();

        var lastOutcome = _runState.LastOutcome;

        return ApiResponse<ScheduleStatusDto>.Ok(new ScheduleStatusDto
        {
            IsRunning = _runState.IsRunning,
            TriggerText = TriggerText(_runState.Trigger),
            StartedAt = _runState.StartedAt,
            LatestMessage = _runState.LatestMessage,
            ProgressPhase = _runState.ProgressPhase,
            ProgressDone = _runState.ProgressDone,
            ProgressTotal = _runState.ProgressTotal,
            SubProgressPhase = _runState.SubProgressPhase,
            SubProgressDone = _runState.SubProgressDone,
            SubProgressTotal = _runState.SubProgressTotal,
            LocalProgressPhase = _runState.LocalProgressPhase,
            LocalProgressDone = _runState.LocalProgressDone,
            LocalProgressTotal = _runState.LocalProgressTotal,
            CanStop = _runState.IsRunning,
            ScheduleEnabled = options.Enabled,
            NextTriggerTime = options.Enabled ? ScheduleCalculator.NextTriggerTime(DateTime.Now, options.Windows) : null,
            LastRunSuccess = lastOutcome?.Success,
            LastRunMessage = lastOutcome?.Message,
            LastRunTriggerText = lastOutcome != null ? TriggerText(lastOutcome.Trigger) : null,
            LastRunEndedAt = lastOutcome?.EndedAt
        });
    }

    /// <summary>執行前預覽：這個範圍實際會涵蓋幾台主機（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）</summary>
    [HttpGet("run-preview")]
    [Permission(Capability.Maintain)]
    public ApiResponse<RunPreviewDto> RunPreview(
        [FromQuery] string scope, [FromQuery] string? segment, [FromQuery] long? hostId,
        [FromQuery] bool onlyMissingOrFailed = false, [FromQuery] int? backfillDays = null)
    {
        var (_, hostIds, includesLocal) = ResolveScope(scope, segment, hostId);
        var aiDisabled = !(_webAi?.Available ?? true);

        if (!onlyMissingOrFailed)
        {
            return ApiResponse<RunPreviewDto>.Ok(new RunPreviewDto
            {
                HostCount = (hostIds?.Count ?? 0) + (includesLocal ? 1 : 0),
                AiDisabled = aiDisabled
            });
        }

        var useAi = !aiDisabled;
        var lookback = Math.Clamp(backfillDays ?? 14, 1, NetiqOptions.MaxBackfillDaysLimit);
        var from = DateTime.Today.AddDays(-lookback);
        var to = DateTime.Today.AddDays(-1);
        // 走輕量查詢：這裡只需要 HostId／Date／AiAnalyzed／AiPending 四個欄位，
        // Query 會反序列化整份分析內容，三千台 × 14 天在預覽這種互動路徑上代價過高
        var recentRecords = _records.QueryLightweight(new RecordQueryFilter { From = from, To = to });

        var count = 0;

        if (hostIds is { Count: > 0 })
        {
            var recordsByHost = recentRecords.Where(r => r.HostId != 0).GroupBy(r => r.HostId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var id in hostIds)
            {
                // 「該主機在回望窗口內的紀錄筆數不足＝有缺日」語意保留
                if (!recordsByHost.TryGetValue(id, out var hostRecords) || hostRecords.Count < lookback ||
                    hostRecords.Any(r => HostDayPostProcessor.NeedsBackfill(r, useAi)))
                {
                    count++;
                }
            }
        }

        if (includesLocal)
        {
            var localHostName = Environment.MachineName;
            var localRecords = recentRecords.Where(r => string.Equals(r.Host, localHostName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (localRecords.Count < lookback || localRecords.Any(r => HostDayPostProcessor.NeedsBackfill(r, useAi)))
            {
                count++;
            }
        }

        return ApiResponse<RunPreviewDto>.Ok(new RunPreviewDto
        {
            HostCount = count,
            AiDisabled = aiDisabled
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
            OnlyMissingOrFailed = request.OnlyMissingOrFailed,
            Trigger = $"manual:{_currentUser.Account}"
        };

        _audit.Record(
            action: AuditActions.ScheduleManualRun,
            summary: $"手動觸發分析執行（範圍：{ScopeText(request.Scope)}" +
                     (request.Segment != null ? $"「{request.Segment}」" : "") +
                     (hostIds != null ? $"，涵蓋 {hostIds.Count} 台主機" : "") +
                     (request.OnlyMissingOrFailed ? "，僅補跑失敗或未執行" : "") + "）",
            targetKind: "schedule",
            detail: new { request.Scope, request.Segment, request.HostId, request.BackfillDays, request.OnlyMissingOrFailed });

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
    /// 主機清單複用 <see cref="HostListSelection"/>（與 NetIQ 機房分析同一份
    /// 「實際會被查詢」語意，網段找不到符合的主機不會被靜默吞掉；Windows／Linux 主機皆涵蓋在內，
    /// docs/archive/FEEDBACK-12-PLAN.md §4B——Linux 取數上線後這裡不再需要另外分開處理）。
    /// </summary>
    private (RunScope Scope, List<long>? HostIds, bool IncludesLocal) ResolveScope(string scope, string? segment, long? hostId)
    {
        switch (scope)
        {
            case "all":
                var allTargets = HostListSelection.FromStore(_hosts, _sentinels);
                var allFlat = allTargets.ByServer.Values.SelectMany(v => v).ToList();
                // 本機分析停用時（回饋十八輪批次D），全部主機的範圍不再涵蓋本機——預覽台數與
                // 執行監控要如實反映，不能讓使用者以為本機也在這次執行內
                return (RunScope.Full, allFlat.Select(t => t.HostId).ToList(), _optionsStore.Get().LocalAnalysisEnabled);

            case "segment":
                if (string.IsNullOrWhiteSpace(segment))
                    throw DomainException.Validation("請輸入要執行的網段。");

                // 輸入語法與 NetIQ 匯入精靈一致（前綴 192.168.0 或 CIDR 192.168.0.0/24），
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

                var netiqList = HostListSelection.FromStore(_hosts, _sentinels);
                var matchedTargets = netiqList.ByServer.Values
                    .SelectMany(v => v)
                    .Where(t => CidrMatcher.Matches(range, t.IpAddress))
                    .ToList();
                return (RunScope.NetiqHosts, matchedTargets.Select(t => t.HostId).ToList(), false);

            case "host":
                if (hostId is not { } id) throw DomainException.Validation("請指定要更新的主機。");
                var host = _hosts.Get(id) ?? throw DomainException.NotFound("找不到這台主機。");
                if (host.Source == "local")
                {
                    if (!_optionsStore.Get().LocalAnalysisEnabled)
                        throw DomainException.Validation("本機分析已停用，請先於排程作業頁啟用「分析本機主機」。");
                    return (RunScope.LocalOnly, null, true);
                }

                // 確認目前真的在會被查詢的清單內（Pollable，Windows／Linux 皆同一套判準），
                // 不然「1 台」的預覽會是假象——停用／待歸屬／IP 衝突／所屬 Sentinel 停用的主機
                // 實際執行時會被 orchestrator 濾掉、靜默變成 0 台，跟「不靜默少幾台」原則相悖，
                // 這裡先擋下並給出明確理由
                var pollableIds = HostListSelection.FromStore(_hosts, _sentinels)
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

    private string TriggerText(string? trigger) => trigger switch
    {
        null => "閒置",
        "schedule" => "排程",
        _ when trigger.StartsWith("manual:", StringComparison.Ordinal) =>
            $"手動（{NameFormat.FormatAccount(_users, trigger["manual:".Length..])}）",
        _ => trigger
    };

    private ScheduleOptionsDto ToDto(ScheduleOptions options) => new()
    {
        Enabled = options.Enabled,
        Windows = options.Windows,
        DebugDump = options.DebugDump,
        LocalAnalysisEnabled = options.LocalAnalysisEnabled,
        UpdatedAt = options.UpdatedAt,
        UpdatedByAccount = options.UpdatedByAccount,
        UpdatedByDisplayName = string.IsNullOrEmpty(options.UpdatedByAccount)
            ? null
            : _users.FindByAccount(options.UpdatedByAccount)?.DisplayName,
        NextTriggerTime = options.Enabled ? ScheduleCalculator.NextTriggerTime(DateTime.Now, options.Windows) : null
    };
}
