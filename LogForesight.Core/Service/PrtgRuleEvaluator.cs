using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>一筆 PRTG finding（尚未映射成問題簽章，那是下一段的工作）</summary>
/// <remarks>
/// <c>Magnitude</c>（Down 持續分鐘數／flap 往返次數／Warning 累計分鐘數）刻意**不**寫進
/// 問題簽章的 <c>Count</c>：整日 Down 會是 1440，會讓 PRTG finding 在問題排行的「次數」
/// 維度壓過所有真實事件計數。它的用途是規則測試、Detail 文案，以及校準數值匯出時的門檻分佈統計。
/// </remarks>
public sealed record PrtgFinding(
    long DeviceObjid,
    long? SensorObjid,
    string RuleCode,
    string Detail,
    int Magnitude,
    KnownIssueRule Rule,
    bool Acknowledged = false);

/// <summary>PRTG 規則評估結果，附帶同代碼多條啟用規則之警告清單</summary>
public sealed class PrtgEvaluationResult : List<PrtgFinding>
{
    public IReadOnlyList<string> DuplicateRuleWarnings { get; }

    public PrtgEvaluationResult(IEnumerable<PrtgFinding> findings, IReadOnlyList<string> duplicateRuleWarnings)
        : base(findings)
    {
        DuplicateRuleWarnings = duplicateRuleWarnings;
    }
}

/// <summary>
/// 依 PRTG 狀態變更與 sensor 現況判定四種 finding。純函式，不碰資料庫、不寫入結果。
/// </summary>
public static class PrtgRuleEvaluator
{
    public const string RuleDown = "down";
    public const string RuleFlapping = "flapping";
    public const string RuleWarning = "warning";
    public const string RuleSilent = "silent";

    public static PrtgEvaluationResult Evaluate(
        DateTime day,
        IReadOnlyList<PrtgStateChangeRow> changes,
        IReadOnlyDictionary<long, long> sensorToDevice,
        IReadOnlyList<(long Objid, long DeviceObjid, string? Status, string SensorType)> sensorStatuses,
        IReadOnlyList<KnownIssueRule> rules,
        IReadOnlyDictionary<long, string> sensorNames,
        IReadOnlyDictionary<long, string> deviceNames,
        bool includeSilent = true)
    {
        var findings = new List<PrtgFinding>();
        var duplicateWarnings = new List<string>();
        var activeRules = new Dictionary<string, KnownIssueRule>(StringComparer.OrdinalIgnoreCase);

        var ruleGroups = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.PrtgRuleCode))
            .GroupBy(r => r.PrtgRuleCode!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in ruleGroups)
        {
            var sorted = group.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            var selected = sorted[0];
            activeRules[group.Key] = selected;

            if (sorted.Count > 1)
            {
                duplicateWarnings.Add($"規則代碼 {group.Key} 有多條啟用規則，採用 {selected.Id}");
            }
        }

        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);

        var sensorTypes = sensorStatuses
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().SensorType);

        string GetDeviceName(long devId) =>
            deviceNames.TryGetValue(devId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : devId.ToString();

        string GetSensorName(long sId) =>
            sensorNames.TryGetValue(sId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : sId.ToString();

        string GetSensorType(long sId) =>
            sensorTypes.TryGetValue(sId, out var type) && !string.IsNullOrWhiteSpace(type)
                ? type
                : sId.ToString();

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
            if (activeRules.TryGetValue(RuleDown, out var downRule))
            {
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

                        if (durationMinutes >= downRule.PrtgThreshold)
                        {
                            var ack = PrtgSensorStatuses.IsAcknowledged(lastChange.Status);
                            var ackSuffix = ack ? "，已於 PRTG 確認" : "";
                            var detail = $"[{GetDeviceName(deviceObjid)}] {GetSensorName(sensorObjid)}（{GetSensorType(sensorObjid)}）持續 Down 達 {durationMinutes} 分鐘（自 {enteredDownAt:yyyy-MM-dd HH:mm:ss} 起）{ackSuffix}";
                            findings.Add(new PrtgFinding(
                                deviceObjid,
                                sensorObjid,
                                RuleDown,
                                detail,
                                durationMinutes,
                                downRule,
                                ack));
                        }
                    }
                }
            }

            // 2 & 3: 當日變更判定（flapping 與持續 Warning）
            var dayChanges = allSensorChanges
                .Where(c => c.ChangedAt >= dayStart && c.ChangedAt < dayEnd)
                .ToList();

            // 2. flapping（RuleFlapping）
            if (activeRules.TryGetValue(RuleFlapping, out var flapRule))
            {
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

                if (flapCount >= flapRule.PrtgThreshold)
                {
                    var detail = $"[{GetDeviceName(deviceObjid)}] {GetSensorName(sensorObjid)}（{GetSensorType(sensorObjid)}）狀態頻繁震盪，當日 Down → Up 往返達 {flapCount} 次";
                    findings.Add(new PrtgFinding(
                        deviceObjid,
                        sensorObjid,
                        RuleFlapping,
                        detail,
                        flapCount,
                        flapRule,
                        false));
                }
            }

            // 3. 持續 Warning（RuleWarning）
            if (activeRules.TryGetValue(RuleWarning, out var warnRule))
            {
                var warningMinutes = 0;
                var priorChanges = allSensorChanges.Where(c => c.ChangedAt < dayStart).ToList();
                if (priorChanges.Count > 0)
                {
                    var lastPrior = priorChanges[priorChanges.Count - 1];
                    if (PrtgSensorStatuses.IsWarning(lastPrior.Status))
                    {
                        var firstChangeTime = dayChanges.Count > 0 ? dayChanges[0].ChangedAt : dayEnd;
                        if (firstChangeTime > dayEnd)
                        {
                            firstChangeTime = dayEnd;
                        }
                        if (firstChangeTime > dayStart)
                        {
                            warningMinutes += (int)(firstChangeTime - dayStart).TotalMinutes;
                        }
                    }
                }

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

                if (warningMinutes >= warnRule.PrtgThreshold)
                {
                    var detail = $"[{GetDeviceName(deviceObjid)}] {GetSensorName(sensorObjid)}（{GetSensorType(sensorObjid)}）持續 Warning 累計達 {warningMinutes} 分鐘";
                    findings.Add(new PrtgFinding(
                        deviceObjid,
                        sensorObjid,
                        RuleWarning,
                        detail,
                        warningMinutes,
                        warnRule,
                        false));
                }
            }
        }

        // 4. 沉默 device（RuleSilent）
        // 沉默的依據是 sensor 目前狀態，不是狀態變更；對過去日評估等於把今天的沉默套到過去，是假訊號。
        if (includeSilent && activeRules.TryGetValue(RuleSilent, out var silentRule))
        {
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
                    var detail = $"[{GetDeviceName(deviceObjid)}] 底下全部 {sensors.Count} 個未暫停 sensor 皆為 Unknown 或無狀態";
                    findings.Add(new PrtgFinding(
                        deviceObjid,
                        null,
                        RuleSilent,
                        detail,
                        sensors.Count,
                        silentRule,
                        false));
                }
            }
        }

        var orderedFindings = findings
            .OrderBy(f => f.DeviceObjid)
            .ThenBy(f => f.RuleCode)
            .ThenBy(f => f.SensorObjid ?? 0)
            .ToList();

        return new PrtgEvaluationResult(orderedFindings, duplicateWarnings);
    }
}
