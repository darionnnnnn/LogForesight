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
    bool Acknowledged = false)
{
    /// <summary>
    /// 觸發這筆 finding 的 sensor 語意分類（評估時已知，見 <see cref="PrtgSensorCategories"/>）。
    /// device 層（silent）與分類未知的 sensor 為 null。映射成簽章時寫進 <c>LogIssueSignature.PrtgSensorCategory</c>。
    /// </summary>
    public string? SensorCategory { get; init; }
}

/// <summary>規則評估用的 sensor 現況（未暫停 sensor）：objid、所屬 device、狀態、type、語意分類。</summary>
public sealed record PrtgSensorStatusInput(
    long Objid,
    long DeviceObjid,
    string? Status,
    string SensorType,
    string? Category);

/// <summary>PRTG 規則評估結果，附帶同代碼多條啟用規則之警告清單與同裝置折疊筆數</summary>
public sealed class PrtgEvaluationResult : List<PrtgFinding>
{
    public IReadOnlyList<string> DuplicateRuleWarnings { get; }

    /// <summary>同裝置折疊時被合併掉（不回傳）的 down／flapping finding 總筆數</summary>
    public int MergedCount { get; }

    public PrtgEvaluationResult(IEnumerable<PrtgFinding> findings, IReadOnlyList<string> duplicateRuleWarnings, int mergedCount)
        : base(findings)
    {
        DuplicateRuleWarnings = duplicateRuleWarnings;
        MergedCount = mergedCount;
    }
}

/// <summary>
/// 依 PRTG 狀態變更與 sensor 現況判定四種 finding。純函式，不碰資料庫、不寫入結果。
/// </summary>
public static class PrtgRuleEvaluator
{
    public const string RuleDiskFreeTrend = "disk_free_trend";
    public const string RuleDown = "down";
    public const string RuleFlapping = "flapping";
    public const string RuleWarning = "warning";
    public const string RuleSilent = "silent";

    public static PrtgEvaluationResult Evaluate(
        DateTime day,
        IReadOnlyList<PrtgStateChangeRow> changes,
        IReadOnlyDictionary<long, long> sensorToDevice,
        IReadOnlyList<PrtgSensorStatusInput> sensorStatuses,
        IReadOnlyList<KnownIssueRule> rules,
        IReadOnlyDictionary<long, string> sensorNames,
        IReadOnlyDictionary<long, string> deviceNames,
        bool includeSilent = true)
    {
        var findings = new List<PrtgFinding>();
        var duplicateWarnings = new List<string>();

        var sensorCategories = sensorStatuses
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().Category);

        // 同「代碼＋適用分類」有多條規則：挑選時一律取 Id 字典序最小，這裡每組警告一次。
        // 刻意在評估前對整份規則清單判定，而不是等某顆 sensor 用到才警告——規則庫設定有歧義這件事
        // 與當天有沒有 sensor 狀態變更無關，沒有變更的日子也要看得到。
        var duplicateGroups = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.PrtgRuleCode))
            .GroupBy(r => (Code: r.PrtgRuleCode!.ToLowerInvariant(), Category: r.PrtgSensorCategory == null ? null : r.PrtgSensorCategory.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Category, StringComparer.Ordinal);
        foreach (var group in duplicateGroups)
        {
            var selectedId = group.OrderBy(r => r.Id, StringComparer.Ordinal).First().Id;
            // 不限分類沿用既有文字（pipeline 與其測試依此比對），分類規則才標出分類
            var label = group.Key.Category == null ? "" : $"（分類 {group.Key.Category}）";
            duplicateWarnings.Add($"規則代碼 {group.Key.Code}{label} 有多條啟用規則，採用 {selectedId}");
        }

        // 依分類挑規則（規則挑選的唯一實作）：分類相符者（不分大小寫）優先，沒有相符者才用不限分類
        // （PrtgSensorCategory == null）的規則；候選多條取 Id 字典序最小；沒有候選回傳 null＝不評估。
        // sensor 分類為 null 時只會挑到不限分類的規則。結果依「代碼＋分類」快取，逐 sensor 呼叫不重掃規則清單。
        var selectionCache = new Dictionary<(string Code, string? Category), KnownIssueRule?>();
        KnownIssueRule? SelectRule(string code, string? sensorCategory)
        {
            var categoryKey = sensorCategory == null ? null : sensorCategory.ToLowerInvariant();
            if (selectionCache.TryGetValue((code, categoryKey), out var cached))
            {
                return cached;
            }

            var sameCode = rules
                .Where(r => string.Equals(r.PrtgRuleCode, code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var candidates = categoryKey == null
                ? new List<KnownIssueRule>()
                : sameCode.Where(r => string.Equals(r.PrtgSensorCategory, categoryKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
            {
                candidates = sameCode.Where(r => r.PrtgSensorCategory == null).ToList();
            }

            var selected = candidates.Count == 0
                ? null
                : candidates.OrderBy(r => r.Id, StringComparer.Ordinal).First();

            selectionCache[(code, categoryKey)] = selected;
            return selected;
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

        // type 查不到時省略括號（退回 objid 會被讀成 type）；名稱查不到退回 objid 則合理，位置本身就是識別
        string SensorLabel(long sId) =>
            sensorTypes.TryGetValue(sId, out var type) && !string.IsNullOrWhiteSpace(type)
                ? $"{GetSensorName(sId)}（{type}）"
                : GetSensorName(sId);

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
            var sensorCategory = sensorCategories.TryGetValue(sensorObjid, out var knownCategory) ? knownCategory : null;

            // 1. 持續 Down（RuleDown）
            if (SelectRule(RuleDown, sensorCategory) is { } downRule)
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
                            var detail = $"[{GetDeviceName(deviceObjid)}] {SensorLabel(sensorObjid)}持續 Down 達 {durationMinutes} 分鐘（自 {enteredDownAt:yyyy-MM-dd HH:mm:ss} 起）{ackSuffix}";
                            findings.Add(new PrtgFinding(
                                deviceObjid,
                                sensorObjid,
                                RuleDown,
                                detail,
                                durationMinutes,
                                downRule,
                                ack) { SensorCategory = sensorCategory });
                        }
                    }
                }
            }

            // 2 & 3: 當日變更判定（flapping 與持續 Warning）
            var dayChanges = allSensorChanges
                .Where(c => c.ChangedAt >= dayStart && c.ChangedAt < dayEnd)
                .ToList();

            // 2. flapping（RuleFlapping）
            if (SelectRule(RuleFlapping, sensorCategory) is { } flapRule)
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
                    var detail = $"[{GetDeviceName(deviceObjid)}] {SensorLabel(sensorObjid)}狀態頻繁震盪，當日 Down → Up 往返達 {flapCount} 次";
                    findings.Add(new PrtgFinding(
                        deviceObjid,
                        sensorObjid,
                        RuleFlapping,
                        detail,
                        flapCount,
                        flapRule,
                        false) { SensorCategory = sensorCategory });
                }
            }

            // 3. 持續 Warning（RuleWarning）
            if (SelectRule(RuleWarning, sensorCategory) is { } warnRule)
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
                    var detail = $"[{GetDeviceName(deviceObjid)}] {SensorLabel(sensorObjid)}持續 Warning 累計達 {warningMinutes} 分鐘";
                    findings.Add(new PrtgFinding(
                        deviceObjid,
                        sensorObjid,
                        RuleWarning,
                        detail,
                        warningMinutes,
                        warnRule,
                        false) { SensorCategory = sensorCategory });
                }
            }
        }

        // 4. 沉默 device（RuleSilent）
        // 沉默的依據是 sensor 目前狀態，不是狀態變更；對過去日評估等於把今天的沉默套到過去，是假訊號。
        // silent 以 device 為單位，只用不限分類的規則（傳入 null 分類即只會挑到 PrtgSensorCategory == null 者）
        if (includeSilent && SelectRule(RuleSilent, null) is { } silentRule)
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

        var mergedCount = FoldByDevice(findings, sensorCategories);

        var orderedFindings = findings
            .OrderBy(f => f.DeviceObjid)
            .ThenBy(f => f.RuleCode)
            .ThenBy(f => f.SensorObjid ?? 0)
            .ToList();

        return new PrtgEvaluationResult(orderedFindings, duplicateWarnings, mergedCount);
    }

    /// <summary>
    /// 同裝置折疊：device 當日有 availability 分類 sensor 的 down 時，主機失聯會讓同裝置其他 sensor
    /// 一起 Down 或震盪，這些 finding 併入 objid 最小的那筆 availability down 的 Detail，不另外回傳。
    /// warning、silent 與 availability 自己的 finding 不受影響；沒有 availability down 的 device 不折疊。
    /// 就地修改 <paramref name="findings"/>，回傳被合併掉的總筆數。
    /// </summary>
    private static int FoldByDevice(List<PrtgFinding> findings, IReadOnlyDictionary<long, string?> sensorCategories)
    {
        bool IsAvailability(long? sensorObjid) =>
            sensorObjid.HasValue
            && sensorCategories.TryGetValue(sensorObjid.Value, out var category)
            && string.Equals(category, PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase);

        var mergedTotal = 0;
        var deviceIds = findings.Select(f => f.DeviceObjid).Distinct().ToList();
        foreach (var deviceObjid in deviceIds)
        {
            var anchor = findings
                .Where(f => f.DeviceObjid == deviceObjid
                            && string.Equals(f.RuleCode, RuleDown, StringComparison.OrdinalIgnoreCase)
                            && IsAvailability(f.SensorObjid))
                .OrderBy(f => f.SensorObjid!.Value)
                .FirstOrDefault();
            if (anchor is null)
            {
                continue;
            }

            var removed = findings.RemoveAll(f => f.DeviceObjid == deviceObjid
                && (string.Equals(f.RuleCode, RuleDown, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.RuleCode, RuleFlapping, StringComparison.OrdinalIgnoreCase))
                && !IsAvailability(f.SensorObjid));
            if (removed > 0)
            {
                var index = findings.IndexOf(anchor);
                findings[index] = anchor with { Detail = $"{anchor.Detail}；同裝置另有 {removed} 顆 sensor 同時 Down 或震盪（已合併）" };
                mergedTotal += removed;
            }
        }

        return mergedTotal;
    }
}
