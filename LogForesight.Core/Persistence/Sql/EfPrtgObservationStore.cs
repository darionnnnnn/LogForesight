using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>獨立觀察的影子寫入。正式讀取切換須另通過來源身分與遷移就緒檢核。</summary>
public sealed class EfPrtgObservationStore(Func<LfDbContext> contextFactory)
{
    public const int FormatVersion = 1;
    public const string PendingQuality = "source-resource-generation-and-coverage-unverified";
    public const string CoveredQuality = "covered-state-v1";

    /// <summary>
    /// 同主機日整批原子保存。重跑相同內容不增加列；修訂保留前版，空結果不代表恢復。
    /// settingsRevision 僅用來追溯設定，不能證明 PRTG Core 或資源身分延續。
    /// 原始 finding 保存規則快照；signature 是含抑制／跨日標註的當次判定，不是原始狀態事件。
    /// </summary>
    public int Capture(long hostId, DateTime day, string settingsRevision,
        IReadOnlyList<(PrtgFinding Finding, LogIssueSignature Signature)> findings,
        string? sourceUrl = null, long? runId = null)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        if (day != day.Date) throw new ArgumentException("判定日不可包含時間。", nameof(day));
        if (string.IsNullOrWhiteSpace(settingsRevision)) throw new ArgumentException("缺少設定版本。", nameof(settingsRevision));
        var sourceHint = SourceHintFor(sourceUrl);
        var rows = findings.Select(item =>
        {
            if (!PrtgFindingMapper.IsPrtg(item.Signature) || item.Finding.DeviceObjid <= 0 ||
                item.Finding.SensorObjid is <= 0 || string.IsNullOrWhiteSpace(item.Finding.RuleCode) ||
                item.Finding.RuleCode.Length > 100)
                throw new ArgumentException("觀察必須包含有效的 PRTG 資源與規則。", nameof(findings));
            // 不把 mapper 產生的 00:00／23:59 宣稱為來源時間；缺少來源區間的狀態明列於品質欄。
            var covered = item.Finding.SourceGeneration != null && item.Finding.ResourceGeneration != null && item.Finding.IncidentStartedAt != null;
            var quality = covered ? CoveredQuality : PendingQuality;
            var content = JsonSerializer.Serialize(new
            {
                FormatVersion = covered ? 2 : FormatVersion, HostId = hostId, Day = day, SettingsRevision = settingsRevision, SourceHint = sourceHint,
                QualityReason = quality, Finding = item.Finding, Decision = item.Signature
            });
            var resource = item.Finding.SensorObjid.HasValue
                ? $"sensor:{item.Finding.SensorObjid.Value}" : $"device:{item.Finding.DeviceObjid}";
            var decisionParts = $"{item.Finding.SourceGeneration ?? sourceHint}|{hostId}|{resource}|{item.Finding.ResourceGeneration}|{item.Finding.RuleCode.ToLowerInvariant()}|{day:yyyy-MM-dd}";
            var decisionKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decisionParts)));
            return new PrtgObservationRow
            {
                SnapshotId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                DecisionKey = decisionKey,
                RunId = runId, HostId = hostId, RecordDate = day, DeviceObjid = item.Finding.DeviceObjid,
                SensorObjid = item.Finding.SensorObjid, RuleCode = item.Finding.RuleCode,
                EventKey = item.Signature.EventKey, SourceName = item.Signature.Source,
                Category = item.Signature.Category.ToString(), SeverityRank = (int)item.Signature.Severity,
                ElevatesDayRisk = item.Signature.ElevatesDayRisk, Suppressed = item.Signature.Suppressed,
                SourceHint = sourceHint,
                SourceGeneration = item.Finding.SourceGeneration, ResourceGeneration = item.Finding.ResourceGeneration,
                SupplementStatus = covered ? "pending" : "shadow",
                FormatVersion = covered ? 2 : FormatVersion, QualityReason = quality, ContentJson = content,
                RecordedAtUtc = DateTime.UtcNow
            };
        }).DistinctBy(r => r.SnapshotId).ToArray();
        if (rows.Length == 0) return 0;
        if (rows.GroupBy(r => r.DecisionKey).Any(g => g.Count() > 1))
            throw new ArgumentException("同一主機日資源與規則有多個判定，須先合併證據。", nameof(findings));

        using var probe = contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            // SQL Server 的重試策略每次使用新 context；交易不包含任何外部副作用。
            using var ctx = contextFactory();
            using var tx = ctx.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var written = 0;
            foreach (var batch in rows.Chunk(300))
            {
                var keys = batch.Select(r => r.DecisionKey).ToArray();
                var current = ctx.PrtgObservations.Where(r => r.ActiveKey != null && keys.Contains(r.ActiveKey))
                    .ToDictionary(r => r.ActiveKey!, StringComparer.Ordinal);
                var changed = batch.Where(r => !current.TryGetValue(r.DecisionKey, out var old) ||
                    old.SnapshotId != r.SnapshotId).ToArray();
                if (changed.Length == 0) continue;
                foreach (var row in changed)
                {
                    if (current.TryGetValue(row.DecisionKey, out var old))
                    { old.ActiveKey = null; old.SupplementStatus = "superseded"; }
                }
                ctx.SaveChanges(); // 先釋出唯一 active_key，避免 EF 先 INSERT 再 UPDATE 的排序競爭。
                var ids = changed.Select(r => r.SnapshotId).ToArray();
                var previous = ctx.PrtgObservations.Where(r => ids.Contains(r.SnapshotId))
                    .ToDictionary(r => r.SnapshotId, StringComparer.Ordinal);
                var additions = changed.Where(r => !previous.ContainsKey(r.SnapshotId)).ToArray();
                foreach (var row in changed)
                {
                    if (previous.TryGetValue(row.SnapshotId, out var old))
                    { old.ActiveKey = row.DecisionKey; old.SupplementStatus = row.SupplementStatus; old.SupplementAttemptAtUtc = null; }
                    else row.ActiveKey = row.DecisionKey;
                }
                ctx.PrtgObservations.AddRange(additions);
                ctx.SaveChanges();
                written += changed.Length;
                ctx.ChangeTracker.Clear();
            }
            tx.Commit();
            return written;
        });
    }

    /// <summary>不保存 URL、query 或 userinfo；摘要只是換端點防混淆提示，不能當 Core 世代。</summary>
    public static string SourceHintFor(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return string.Empty;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("PRTG 位址格式錯誤。", nameof(sourceUrl));
        var location = $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location)));
    }

    /// <summary>僅供影子驗收／稽核。必填可見主機集合，授權與日期在 SQL 分頁前套用。</summary>
    public List<PrtgObservationRow> ReadPage(IReadOnlyCollection<long> visibleHostIds,
        DateTime from, DateTime to, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(visibleHostIds);
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (visibleHostIds.Count == 0) return [];
        var start = from.Date;
        var end = to.Date;
        using var ctx = contextFactory();
        return ctx.PrtgObservations.AsNoTracking()
            .Where(r => visibleHostIds.Contains(r.HostId) && r.RecordDate >= start && r.RecordDate <= end)
            .OrderByDescending(r => r.RecordDate).ThenBy(r => r.HostId).ThenBy(r => r.SnapshotId)
            .Skip(offset).Take(limit).ToList();
    }

    /// <summary>只統計影子與舊問題交集，供遷移預覽；不切換正式讀取。</summary>
    public PrtgObservationCutoverPreview Preview(DateTime from, DateTime to, IReadOnlyCollection<long> visibleHostIds)
    {
        ArgumentNullException.ThrowIfNull(visibleHostIds);
        var start = from.Date;
        var end = to.Date;
        if (end < start || (end - start).Days > 365)
            throw new ArgumentException("預覽日期範圍須在 366 天內且由早到晚。", nameof(to));
        if (visibleHostIds.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0);
        using var ctx = contextFactory();
        var active = ctx.PrtgObservations.AsNoTracking()
            .Where(r => r.ActiveKey != null && visibleHostIds.Contains(r.HostId) &&
                        r.RecordDate >= start && r.RecordDate <= end);
        var legacy = ctx.TopIssues.AsNoTracking()
            .Where(r => r.LogName == PrtgFindingMapper.PrtgLogName && visibleHostIds.Contains(r.HostId) &&
                        r.RecordDate >= start && r.RecordDate <= end);
        var activeCount = active.Count();
        var oldCount = legacy.Count();
        var overlaps = active.Count(r => legacy.Any(o => o.HostId == r.HostId &&
            o.RecordDate == r.RecordDate && o.EventKey == r.EventKey));
        var oldMissingSnapshot = legacy.Count(o => !active.Any(r => r.HostId == o.HostId &&
            r.RecordDate == o.RecordDate && r.EventKey == o.EventKey));
        var noSource = active.Count(r => r.SourceGeneration == null);
        var noResource = active.Count(r => r.ResourceGeneration == null);
        var missingLegacyIdentity = legacy.Count(r => r.EventKey == "" || r.SourceName == "");
        return new(activeCount, oldCount, overlaps, activeCount - overlaps, noSource, noResource,
            missingLegacyIdentity, oldMissingSnapshot);
    }

    /// <summary>影子 v1 沒有案件引用，依保存時間清理；未知新格式不套用此清理規則。</summary>
    public int PruneShadow(int retentionDays, DateTime utcNow)
    {
        if (retentionDays <= 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("清理基準必須是 UTC。", nameof(utcNow));
        var cutoff = utcNow.AddDays(-retentionDays);
        using var ctx = contextFactory();
        return ctx.PrtgObservations.Where(r => r.FormatVersion == 1 && r.RecordedAtUtc < cutoff).ExecuteDelete();
    }

    /// <summary>已完成的可信判定，僅在案件最小證據已保存後清理。未完成意圖不提前刪除。</summary>
    public int PruneCompleted(int retentionDays, DateTime utcNow)
    {
        if (retentionDays <= 0 || utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("保留天數與 UTC 基準無效。");
        var cutoff = utcNow.AddDays(-retentionDays);
        using var ctx = contextFactory();
        return ctx.PrtgObservations.Where(r => r.FormatVersion == 2 && r.RecordedAtUtc < cutoff &&
            (r.SupplementStatus == "applied" || (r.ActiveKey == null && r.SupplementStatus == "superseded")) && !ctx.IssueCases.Any(c => c.IssueKey.EndsWith(r.EventKey) && c.PrtgEvidenceJson == null))
            .ExecuteDelete();
    }
}

public sealed record PrtgObservationCutoverPreview(int ActiveSnapshots, int LegacyIssueRows,
    int LegacyOverlaps, int IndependentSnapshots, int UnknownSourceGenerations,
    int UnknownResourceGenerations, int LegacyMissingIdentityRows, int LegacyWithoutSnapshotRows)
{
    public bool ReadyForCutover => ActiveSnapshots + LegacyIssueRows > 0 &&
                                   UnknownSourceGenerations == 0 && UnknownResourceGenerations == 0 &&
                                   LegacyMissingIdentityRows == 0 && LegacyWithoutSnapshotRows == 0;
}
