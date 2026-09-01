using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>PRTG 狀態變更規則的判定門檻</summary>
public sealed record PrtgRuleThresholds(
    int DownMinutes = 60,
    int FlapCount = 5,
    int WarningMinutes = 240);

/// <summary>一筆 PRTG finding（尚未映射成問題簽章，那是下一段的工作）</summary>
public sealed record PrtgFinding(
    long DeviceObjid,
    long? SensorObjid,
    string RuleCode,
    string Detail,
    int Magnitude);

/// <summary>
/// 依 PRTG 狀態變更與 sensor 現況判定四種 finding。純函式，不碰資料庫、不寫入結果。
/// </summary>
public static class PrtgRuleEvaluator
{
    public const string RuleDown = "down";
    public const string RuleFlapping = "flapping";
    public const string RuleWarning = "warning";
    public const string RuleSilent = "silent";

    public static List<PrtgFinding> Evaluate(
        DateTime day,
        IReadOnlyList<PrtgStateChangeRow> changes,
        IReadOnlyDictionary<long, long> sensorToDevice,
        IReadOnlyList<(long Objid, long DeviceObjid, string? Status)> sensorStatuses,
        PrtgRuleThresholds? thresholds = null)
    {
        thresholds ??= new PrtgRuleThresholds();
        var findings = new List<PrtgFinding>();

        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);

        // 1~3: 針對各 sensor 的狀態變更判定
        var sensorGroups = changes.GroupBy(c => c.SensorObjid);
        foreach (var group in sensorGroups)
        {
            var sensorObjid = group.Key;
            if (!sensorToDevice.TryGetValue(sensorObjid, out var deviceObjid))
            {
                continue;
            }

            var allSensorChanges = group.OrderBy(c => c.ChangedAt).ToList();

            // 1. 持續 Down（RuleDown）
            var relevantChanges = allSensorChanges.Where(c => c.ChangedAt < dayEnd).ToList();
            if (relevantChanges.Count > 0)
            {
                var lastChange = relevantChanges[relevantChanges.Count - 1];
                if (PrtgSensorStatuses.IsDown(lastChange.Status))
                {
                    var downStartIndex = relevantChanges.Count - 1;
                    while (downStartIndex > 0 && PrtgSensorStatuses.IsDown(relevantChanges[downStartIndex - 1].Status))
                    {
                        downStartIndex--;
                    }

                    var enteredDownAt = relevantChanges[downStartIndex].ChangedAt;
                    var effectiveStart = enteredDownAt < dayStart ? dayStart : enteredDownAt;
                    var durationMinutes = (int)(dayEnd - effectiveStart).TotalMinutes;

                    if (durationMinutes >= thresholds.DownMinutes)
                    {
                        findings.Add(new PrtgFinding(
                            deviceObjid,
                            sensorObjid,
                            RuleDown,
                            $"持續 Down 達 {durationMinutes} 分鐘（自 {enteredDownAt:yyyy-MM-dd HH:mm:ss} 起）",
                            durationMinutes));
                    }
                }
            }

            // 2 & 3: 當日變更判定（flapping 與持續 Warning）
            var dayChanges = allSensorChanges
                .Where(c => c.ChangedAt >= dayStart && c.ChangedAt < dayEnd)
                .ToList();

            // 2. flapping（RuleFlapping）
            var flapCount = 0;
            var inDown = false;
            foreach (var c in dayChanges)
            {
                if (PrtgSensorStatuses.IsDown(c.Status))
                {
                    inDown = true;
                }
                else if (PrtgSensorStatuses.IsUp(c.Status))
                {
                    if (inDown)
                    {
                        flapCount++;
                        inDown = false;
                    }
                }
                else
                {
                    inDown = false;
                }
            }

            if (flapCount >= thresholds.FlapCount)
            {
                findings.Add(new PrtgFinding(
                    deviceObjid,
                    sensorObjid,
                    RuleFlapping,
                    $"狀態頻繁震盪（flapping），當日 Down → Up 往返達 {flapCount} 次",
                    flapCount));
            }

            // 3. 持續 Warning（RuleWarning）
            var warningMinutes = 0;
            for (var i = 0; i < dayChanges.Count; i++)
            {
                if (PrtgSensorStatuses.IsWarning(dayChanges[i].Status))
                {
                    var start = dayChanges[i].ChangedAt;
                    var end = (i + 1 < dayChanges.Count) ? dayChanges[i + 1].ChangedAt : dayEnd;
                    if (end > dayEnd)
                    {
                        end = dayEnd;
                    }
                    if (end > start)
                    {
                        warningMinutes += (int)(end - start).TotalMinutes;
                    }
                }
            }

            if (warningMinutes >= thresholds.WarningMinutes)
            {
                findings.Add(new PrtgFinding(
                    deviceObjid,
                    sensorObjid,
                    RuleWarning,
                    $"持續 Warning 累計達 {warningMinutes} 分鐘",
                    warningMinutes));
            }
        }

        // 4. 沉默 device（RuleSilent）
        var deviceGroups = sensorStatuses.GroupBy(s => s.DeviceObjid);
        foreach (var group in deviceGroups)
        {
            var deviceObjid = group.Key;
            var sensors = group.ToList();
            if (sensors.Count == 0)
            {
                continue;
            }

            if (sensors.All(s => PrtgSensorStatuses.IsUnknownOrEmpty(s.Status)))
            {
                findings.Add(new PrtgFinding(
                    deviceObjid,
                    null,
                    RuleSilent,
                    $"Device 底下全部 {sensors.Count} 個未暫停 sensor 皆為 Unknown 或無狀態",
                    sensors.Count));
            }
        }

        return findings
            .OrderBy(f => f.DeviceObjid)
            .ThenBy(f => f.RuleCode)
            .ThenBy(f => f.SensorObjid ?? 0)
            .ToList();
    }
}
