using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Services;

/// <summary>Read-only effectiveness counts derived from existing dated findings and handling stores.</summary>
public sealed class PrtgEffectivenessService
{
    public const int DefaultWindowDays = 30;
    public const int MaxWindowDays = 366;
    private const int MaxPayloadRecords = 10_000;
    private readonly Func<LfDbContext> _contextFactory;
    private readonly IIssueOwnerStore _issueOwners;

    public PrtgEffectivenessService(Func<LfDbContext> contextFactory, IIssueOwnerStore issueOwners)
    { _contextFactory = contextFactory; _issueOwners = issueOwners; }

    public PrtgEffectivenessSummary Get(DateTime? from = null, DateTime? through = null)
    {
        var end = (through ?? DateTime.Today).Date;
        var start = (from ?? end.AddDays(-(DefaultWindowDays - 1))).Date;
        if (start > end) throw new ArgumentException("起始日期不可晚於結束日期。");
        var days = (end - start).Days + 1;
        if (days > MaxWindowDays) throw new ArgumentOutOfRangeException(nameof(from), $"日期範圍最多 {MaxWindowDays} 天。");
        var endExclusive = end.AddDays(1);

        using var ctx = _contextFactory();
        var findingQuery = ctx.TopIssues.AsNoTracking()
            .Where(i => i.RecordDate >= start && i.RecordDate < endExclusive && i.LogName == PrtgFindingMapper.PrtgLogName);
        var findingCount = findingQuery.Count();
        var lowCoverageSampledHours = ctx.PrtgValues.AsNoTracking()
            .Count(v => v.PeriodStart >= start && v.PeriodStart < endExclusive
                && v.Quality == PrtgDataQuality.Sampled
                && v.Coverage < PrtgValueUsability.SampledMinCoverage);
        var recordIds = findingQuery.Select(i => i.RecordId).Distinct().OrderBy(id => id)
            .Take(MaxPayloadRecords + 1).ToArray();

        // Suppression and corroboration flags are kept in the dated record JSON, not in the indexed issue rows.
        // Load only records already known to contain a PRTG finding, in bounded IN batches.
        int? suppressed = 0;
        var corroboratedDays = 0;
        var payloadScopeComplete = recordIds.Length <= MaxPayloadRecords;
        var payloadsReadable = payloadScopeComplete;
        if (!payloadScopeComplete) suppressed = null;
        foreach (var batch in recordIds.Take(MaxPayloadRecords).Chunk(400))
        {
            var rows = ctx.DailyRecords.AsNoTracking().Where(r => batch.Contains(r.RecordId))
                .Select(r => new { r.RecordId, r.ContentJson }).ToList();
            var byId = rows.ToDictionary(r => r.RecordId, r => r.ContentJson);
            foreach (var id in batch)
            {
                if (!byId.TryGetValue(id, out var json)) { payloadsReadable = false; continue; }
                try
                {
                    var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(json);
                    if (record == null) { payloadsReadable = false; continue; }
                    if (suppressed.HasValue)
                        suppressed += record.TopIssues.Count(i => PrtgFindingMapper.IsPrtg(i) && i.Suppressed);
                    if (record.CorrelationAlertRefs.Any(IsPrtgCorroboration)
                        || record.SuppressedCorrelationAlerts.Any(IsPrtgCorroboration)) corroboratedDays++;
                }
                catch (JsonException) { payloadsReadable = false; }
            }
        }
        if (!payloadsReadable) suppressed = null;

        var cases = ctx.IssueCases.AsNoTracking()
            .Count(c => c.CreatedAt >= start && c.CreatedAt < endExclusive
                && ((c.SourceKey != null && c.SourceKey.StartsWith("PRTG:"))
                    || (c.SourceKey == null && c.SourceName != null && c.SourceName.StartsWith("PRTG:"))));
        var workOrders = ctx.WorkOrders.AsNoTracking()
            .Where(w => w.CreatedAt >= start && w.CreatedAt < endExclusive
                && ((w.SourceKey != null && w.SourceKey.StartsWith("PRTG:"))
                    || (w.SourceKey == null && w.SourceName != null && w.SourceName.StartsWith("PRTG:"))));
        var workOrderCount = workOrders.Count();
        var replied = workOrders.Count(w => w.LastReplyAt != null);
        // Mutes live in issue profiles, not dated finding rows. Count distinct PRTG profiles whose
        // configured interval overlaps the requested dates; do not present this as muted findings.
        var mutedProfiles = _issueOwners.GetAll().Count(profile =>
            profile.SourceName.StartsWith("PRTG:", StringComparison.OrdinalIgnoreCase)
            && profile.Mutes.Any(mute => mute.From.Date <= end && mute.To.Date >= start));
        return new PrtgEffectivenessSummary(start, end, days, findingCount, lowCoverageSampledHours, cases, workOrderCount, replied,
            suppressed, mutedProfiles, payloadsReadable ? corroboratedDays : null,
            "Findings count indexed PRTG TopIssue rows per host-day/signature. LowCoverageSampledHours counts observed lf_prtg_values hourly rows in the selected period with quality=sampled and coverage below the usable threshold; it does not count all missing values, missing hourly periods, disk sensor-days, or findings. Cases and work orders count rows created within the selected dates and whose normalized source begins PRTG:. Replied counts those same work orders with LastReplyAt set, regardless of reply date. Suppressed counts PRTG signatures flagged Suppressed in the dated DailyAnalysisRecord payload. MutedPrtgProfiles counts distinct PRTG issue profiles with a mute interval overlapping the selected dates; it does not imply any finding occurred. Corroborated counts host-day records with a stored PRTG corroboration reference or suppressed corroboration text. These are separate denominators and are not a conversion funnel or resolution rate.",
            payloadsReadable
                ? "Corroboration is counted per host-day, not per finding. Current suppression configuration is not used to reconstruct historical state."
                : $"Suppression and corroboration are omitted because the payload record cap ({MaxPayloadRecords}) was exceeded or a dated analysis payload was missing/unreadable. Current suppression configuration is not used to reconstruct historical state.");
    }

    private static bool IsPrtgCorroboration(CorrelationAlertRef alert) => IsPrtgCorroboration(alert.PatternId);
    private static bool IsPrtgCorroboration(string text) => text.StartsWith("prtg-", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("【儲存故障雙重確認】", StringComparison.Ordinal)
        || text.StartsWith("【磁碟容量雙重確認】", StringComparison.Ordinal)
        || text.StartsWith("【失聯獲 PRTG 證實】", StringComparison.Ordinal);
}

public sealed record PrtgEffectivenessSummary(DateTime From, DateTime Through, int WindowDays,
    int PrtgFindings, int LowCoverageSampledHours, int CasesCreated, int WorkOrdersCreated, int WorkOrdersReplied,
    int? SuppressedFindings, int MutedPrtgProfiles, int? CorroboratedHostDays, string MetricSemantics, string Limitations);
