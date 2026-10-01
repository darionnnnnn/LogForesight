using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>正式狀態判定只採可信區間；恢復前的故障仍計入當日，缺口不算連續。</summary>
public static class PrtgCoveredRuleEvaluator
{
    public static PrtgEvaluationResult Evaluate(DateTime day, IReadOnlyList<PrtgSensorStatusInput> sensors,
        IReadOnlyDictionary<long, PrtgSensorTimelineEvidence> evidence, IReadOnlyList<KnownIssueRule> rules)
    {
        var findings = new List<PrtgFinding>();
        var start = new DateTimeOffset(day.Date); var end = start.AddDays(1);
        foreach (var sensor in sensors)
        {
            if (!evidence.TryGetValue(sensor.Objid, out var proof)) continue;
            var periods = new List<PrtgCoveredStatePeriod>();
            foreach (var period in proof.Periods(start, end))
            {
                if (periods.Count > 0 && periods[^1].Through == period.From &&
                    (string.Equals(periods[^1].Status, period.Status, StringComparison.OrdinalIgnoreCase) ||
                     PrtgSensorStatuses.IsDown(periods[^1].Status) && PrtgSensorStatuses.IsDown(period.Status)))
                    periods[^1] = periods[^1] with { Through = period.Through, Status = period.Status };
                else periods.Add(period);
            }
            KnownIssueRule? Rule(string code) => rules.Where(r => r.PrtgRuleCode == code &&
                PrtgFormalEligibility.RuleCategoryMatches(r, sensor.Category))
                .OrderByDescending(r => r.PrtgSensorCategory != null).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            void Add(string code, int magnitude, DateTimeOffset entered, bool ack = false)
            {
                var rule = Rule(code);
                if (rule == null || magnitude < rule.PrtgThreshold) return;
                findings.Add(new(sensor.DeviceObjid, sensor.Objid, code,
                    $"sensor {sensor.Objid}：可信 {code} 區間 {magnitude} {(code == "flapping" ? "次" : "分鐘")}；起點 {entered:yyyy-MM-dd HH:mm:ss zzz}，來源與資源世代已確認。" +
                        (code == "down" ? " 當日故障區間：" + string.Join("；", periods.Where(p => PrtgSensorStatuses.IsDown(p.Status)).Select(p => $"{p.EnteredAt:yyyy-MM-dd HH:mm:ss zzz} 至 {p.Through:yyyy-MM-dd HH:mm:ss zzz}")) : ""),
                    magnitude, rule, ack)
                { SensorCategory = sensor.Category, SourceGeneration = proof.SourceGeneration,
                    ResourceGeneration = proof.ResourceGeneration, IncidentStartedAt = entered });
            }
            var down = periods.Where(p => PrtgSensorStatuses.IsDown(p.Status))
                .Where(p => (p.Through - p.From).TotalMinutes >= (Rule("down")?.PrtgThreshold ?? int.MaxValue))
                .OrderByDescending(p => p.EnteredAt).FirstOrDefault();
            if (down != null) Add("down", (int)(down.Through - down.From).TotalMinutes, down.EnteredAt,
                PrtgSensorStatuses.IsAcknowledged(down.Status));
            var warning = periods.Where(p => PrtgSensorStatuses.IsWarning(p.Status)).ToArray();
            if (warning.Length > 0) Add("warning", (int)warning.Sum(p => (p.Through - p.From).TotalMinutes), warning[^1].EnteredAt);
            var flaps = 0;
            var flapStart = start;
            for (var i = 1; i < periods.Count; i++)
                if (periods[i - 1].Through == periods[i].From && PrtgSensorStatuses.IsDown(periods[i - 1].Status) &&
                    PrtgSensorStatuses.IsUp(periods[i].Status)) { flaps++; flapStart = periods[i - 1].EnteredAt; }
            if (flaps > 0) Add("flapping", flaps, flapStart);
        }
        var warnings = rules.Where(r => !string.IsNullOrWhiteSpace(r.PrtgRuleCode))
            .GroupBy(r => (Code: r.PrtgRuleCode!.ToLowerInvariant(), Category: r.PrtgSensorCategory?.ToLowerInvariant()))
            .Where(g => g.Count() > 1).Select(g =>
                $"規則代碼 {g.Key.Code}{(g.Key.Category == null ? "" : $"（分類 {g.Key.Category}）")} 有多條啟用規則，採用 {g.OrderBy(r => r.Id, StringComparer.Ordinal).First().Id}").ToList();
        return new(findings, warnings, 0);
    }
}
