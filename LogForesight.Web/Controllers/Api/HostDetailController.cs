using LogForesight.Core.Analysis;
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

/// <summary>主機詳情／時間軸（§9.4）</summary>
[ApiController]
[Route("api/host-detail")]
public class HostDetailController : ControllerBase
{
    private readonly RecordDetailQueryService _service;
    private readonly EfPrtgStore _prtgStore;
    private readonly IVisibilityService _visibility;
    private readonly PrtgValueAssessment _valueAssessment;
    private readonly IKnownIssueRuleStore _rules;

    public HostDetailController(
        RecordDetailQueryService service,
        EfPrtgStore prtgStore,
        IVisibilityService visibility,
        PrtgValueAssessment valueAssessment,
        IKnownIssueRuleStore rules)
    {
        _service = service;
        _prtgStore = prtgStore;
        _visibility = visibility;
        _valueAssessment = valueAssessment;
        _rules = rules;
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
        if (_visibility.IsCaseGrantOnly(hostId))
            throw DomainException.Forbidden("交辦授與不包含主機層級的 PRTG 對應資訊。");

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
        var allSensorsWithDevice = new List<(long DeviceObjid, HostPrtgSensorDto Dto)>();

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

            foreach (var s in sensors)
            {
                allSensorsWithDevice.Add((r.DeviceObjid, s));
            }

            devices.Add(new HostPrtgDeviceDto
            {
                DeviceObjid = r.DeviceObjid,
                Name = deviceNames.TryGetValue(r.DeviceObjid, out var deviceName) ? deviceName : null,
                Ip = r.Ip,
                MapStatus = r.MapStatus,
                Note = r.Note,
                Sensors = sensors,
                SensorCount = sensors.Count
            });
        }

        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        // 讀規則庫而非 KnownIssueCatalog.Rules：Web 行程不呼叫 Initialize（Program.cs），後者在夜間執行前是 seed、
        // 之後是上一趟的快照，管理者改過的窗口／最少有效日會對不上規則頁與下一趟管線
        var allRules = KnownIssueCatalog.ResolveRules(_rules);

        foreach (var code in PrtgRuleCatalog.ValueTrendCodes)
        {
            var category = PrtgRuleCatalog.TrendSensorCategory(code)!;
            var matching = allSensorsWithDevice
                .Where(x => string.Equals(x.Dto.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0) continue;

            int windowDays;
            int requiredDays;

            var rule = allRules.FirstOrDefault(r => string.Equals(r.PrtgRuleCode, code, StringComparison.OrdinalIgnoreCase));
            if (code == PrtgRuleEvaluator.RuleDiskFreeTrend)
            {
                var thresholds = rule?.PrtgDiskTrendThresholds ?? PrtgDiskTrendThresholds.Provisional;
                windowDays = thresholds.RecentWindowDays;
                requiredDays = thresholds.MinimumValidDays;
            }
            else
            {
                var thresholds = rule?.PrtgTrendThresholds ?? PrtgTrendThresholds.ProvisionalFor(code);
                windowDays = thresholds.WindowDays;
                requiredDays = thresholds.MinimumValidDays;
            }

            var dtosByObjid = matching.ToDictionary(x => x.Dto.Objid, x => x.Dto);
            var valueSensors = matching
                .Select(x => new PrtgValueSensor(x.Dto.Objid, x.DeviceObjid, hostId, x.Dto.Name, x.Dto.SensorType, x.Dto.Category))
                .ToList();

            foreach (var chunk in valueSensors.Chunk(PrtgValueAssessment.PageSize))
            {
                var rows = _valueAssessment.AssessPage(chunk, yesterday, windowDays, requiredDays);
                foreach (var row in rows)
                {
                    if (dtosByObjid.TryGetValue(row.Sensor.SensorObjid, out var dto))
                    {
                        dto.ValueReasonCode = row.Reason.ToString();
                        dto.ValueReason = PrtgValueReasonText.Label(row.Reason);
                        dto.ValidDays = row.ValidDays;
                        dto.RequiredDays = row.RequiredDays;
                    }
                }
            }
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
