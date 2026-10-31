using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 主機詳情的 PRTG 對應查詢（§9.4）：某台主機目前對應的 PRTG device 與 sensor，
/// 附 IP 排除狀態與各 sensor 的數值就緒度。控制器只呼叫這一個入口。
/// </summary>
public class HostPrtgDetailService
{
    private readonly EfPrtgStore _store;
    private readonly IVisibilityService _visibility;
    private readonly PrtgValueAssessment _valueAssessment;
    private readonly IKnownIssueRuleStore _rules;

    public HostPrtgDetailService(
        EfPrtgStore store,
        IVisibilityService visibility,
        PrtgValueAssessment valueAssessment,
        IKnownIssueRuleStore rules)
    {
        _store = store;
        _visibility = visibility;
        _valueAssessment = valueAssessment;
        _rules = rules;
    }

    public HostPrtgMappingDto GetForHost(long hostId)
    {
        // 授權過濾在查詢層強制（docs/DB-SPEC.md）：這支不經 RecordDetailQueryService，
        // 必須在讀任何 PRTG 資料之前自己檢查——否則任何登入者都能用任意 hostId
        // 列出別人主機的 PRTG device 與 sensor。順序是安全前提，不可挪到資料讀取之後。
        _visibility.EnsureVisible(hostId);
        if (_visibility.IsCaseGrantOnly(hostId))
            throw DomainException.Forbidden("交辦授與不包含主機層級的 PRTG 對應資訊。");

        var store = _store;
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

        // 只查這一台主機的對應列：MapDate 仍是「最近一次有對應資料的日期」，
        // 該日存在但這台沒有對應列時照樣回傳日期。
        var (mapDate, targetRows) = store.GetLatestHostMapForHost(hostId);
        if (mapDate == null)
        {
            return new HostPrtgMappingDto
            {
                IpExcluded = ipExcluded,
                ExcludedIp = excludedIp
            };
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

        AttachValueReadiness(hostId, allSensorsWithDevice);

        return new HostPrtgMappingDto
        {
            MapDate = mapDate,
            Devices = devices,
            IpExcluded = ipExcluded,
            ExcludedIp = excludedIp
        };
    }

    /// <summary>為趨勢類 sensor 補上數值就緒度（有效日／需求日／原因），其餘 sensor 維持空值</summary>
    private void AttachValueReadiness(long hostId, List<(long DeviceObjid, HostPrtgSensorDto Dto)> allSensorsWithDevice)
    {
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
    }
}
