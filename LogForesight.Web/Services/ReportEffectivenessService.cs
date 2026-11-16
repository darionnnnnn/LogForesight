using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core.Analysis;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 報表「處理成效」（WEB-SPEC §9.6）：處理有沒有跟上、處理完還會不會再來。
/// 母體是期間內高＋中風險日（與 KPI「未結案風險日」同一母體），套可見範圍、顯示設定與靜音排除，
/// **不套** handlingScope。風險日現在的三態一律走 <see cref="HandlingHistoryQueryService.ClassifyDays"/>，
/// 不另寫判定。
/// </summary>
public class ReportEffectivenessService
{
    /// <summary>暫定：已處理之後這麼多天內同主機同問題又出現，算「再發生」</summary>
    public const int RecurrenceWindowDays = 14;

    /// <summary>暫定：帳齡分段（天數下界，上界為下一段下界 − 1，最後一段無上限）</summary>
    private static readonly (string Label, int Min, int? Max)[] AgingBands =
    {
        ("0–3", 0, 3), ("4–7", 4, 7), ("8–14", 8, 14), ("15–30", 15, 30), ("31+", 31, null)
    };

    private const int TopRecurringLimit = 10;

    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly HandlingHistoryQueryService _handling;
    private readonly IIssueHandlingStore _issueStore;
    private readonly ISystemSettingsService _systemSettings;
    private readonly SummaryCache _cache;
    private readonly IIssueExclusionSource _exclusions;

    public ReportEffectivenessService(
        IRecordRepository repository, IHostStore hosts, IVisibilityService visibility,
        HandlingHistoryQueryService handling, IIssueHandlingStore issueStore, ISystemSettingsService systemSettings,
        SummaryCache cache, IIssueExclusionSource exclusions)
    {
        _repository = repository;
        _hosts = hosts;
        _visibility = visibility;
        _handling = handling;
        _issueStore = issueStore;
        _systemSettings = systemSettings;
        _cache = cache;
        _exclusions = exclusions;
    }

    public ReportEffectivenessDto Get(DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        var visibleHosts = _visibility.GetVisibleHosts();
        var exclusion = _exclusions.Current();
        return _cache.GetOrAdd(
            SummaryCache.KeyOf("report-effectiveness", $"{from:yyyyMMdd}|{to:yyyyMMdd}|{exclusion.CacheToken}",
                visibleHosts.Select(h => h.HostId).ToList()),
            () => Build(from.Date, to.Date, exclusion));
    }

    private ReportEffectivenessDto Build(DateTime from, DateTime to, IssueExclusion exclusion)
    {
        var buckets = ReportBucketing.Build(from, to);
        var (_, _, nothingVisible) = RecordRepository.ResolveVisibility(
            _systemSettings.GetVisibleDayRiskLevels(), _systemSettings.GetVisibleSeverities());

        var dto = new ReportEffectivenessDto
        {
            From = ReportBucketing.Fmt(from),
            To = ReportBucketing.Fmt(to),
            HandlingScopeApplied = false,
            AsOf = ReportBucketing.Fmt(to),
            Buckets = buckets.Select(b => new ReportEffectivenessBucketDto
            {
                From = ReportBucketing.Fmt(b.From), To = ReportBucketing.Fmt(b.To), Days = b.Days
            }).ToList(),
            Aging = AgingBands.Select(a => new ReportAgingBandDto { Label = a.Label, MinDays = a.Min, MaxDays = a.Max }).ToList(),
            Definitions = BuildDefinitions()
        };
        if (nothingVisible) return dto;

        // 一次查詢：期間起日 ～ 期間迄日＋觀察期（不超過昨天；期間本身已含今天時不縮短期間）
        var yesterday = DateTime.Today.AddDays(-1);
        var queryTo = to.AddDays(RecurrenceWindowDays);
        if (queryTo > yesterday) queryTo = yesterday;
        if (queryTo < to) queryTo = to;
        var loaded = _repository.QueryLightweight(new RecordQueryFilter { From = from, To = queryTo });
        if (loaded.Count == 0) return dto;

        var lookup = new HostLookup(_hosts.GetAll());
        string NameOf(DailyAnalysisRecord r) => lookup.For(r)?.HostName ?? r.Host;

        // 一次取回期間內的問題層級處理列：三態推導與再發生率共用同一份
        var hostNames = loaded.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var issueRows = _issueStore.GetMany(hostNames, from, to);

        var inPeriod = loaded.Where(r => r.Date.Date >= from && r.Date.Date <= to).ToList();
        FillHandling(dto, buckets, exclusion, inPeriod, issueRows,
            asOf: inPeriod.Count > 0 ? inPeriod.Max(r => r.Date.Date) : to);
        dto.Recurrence = BuildRecurrence(exclusion, loaded, issueRows, NameOf, from, to);
        return dto;
    }

    private void FillHandling(
        ReportEffectivenessDto dto, List<ReportBucketing.Bucket> buckets, IssueExclusion exclusion,
        List<DailyAnalysisRecord> inPeriod, List<IssueHandling> issueRows, DateTime asOf)
    {
        dto.AsOf = ReportBucketing.Fmt(asOf);
        foreach (var day in _handling.ClassifyDays(exclusion, inPeriod, issueRows))
        {
            var index = ReportBucketing.IndexOf(buckets, day.Record.Date);
            if (index < 0) continue;
            var bucket = dto.Buckets[index];
            bucket.Total++;
            switch (day.External)
            {
                case HandlingStatuses.Open: bucket.Open++; break;
                case HandlingStatuses.InProgress: bucket.InProgress++; break;
                default: bucket.Resolved++; break;
            }

            if (day.External == HandlingStatuses.Resolved) continue;
            dto.UnresolvedTotal++;
            var age = Math.Max(0, (asOf - day.Record.Date.Date).Days);
            var band = dto.Aging.First(a => age >= a.MinDays && (a.MaxDays == null || age <= a.MaxDays));
            band.Count++;
        }
    }

    private static ReportRecurrenceDto BuildRecurrence(
        IssueExclusion exclusion, List<DailyAnalysisRecord> loaded, List<IssueHandling> issueRows,
        Func<DailyAnalysisRecord, string> nameOf, DateTime from, DateTime to)
    {
        var result = new ReportRecurrenceDto();

        // 每個「主機 × 問題簽章」最後一次被標為已處理的紀錄日
        var lastResolved = new Dictionary<(string HostName, string IssueKey), DateTime>(HostIssueSignatureKeyComparer.Instance);
        foreach (var row in issueRows)
        {
            if (row.Status != IssueHandlingStatuses.Resolved) continue;
            var date = row.Date.Date;
            if (date < from || date > to) continue;
            var key = (row.HostName, row.IssueKey);
            if (!lastResolved.TryGetValue(key, out var existing) || date > existing) lastResolved[key] = date;
        }
        if (lastResolved.Count == 0) return result;

        // 只為「有已處理標記的組合」建出現日索引（靜音當天的出現不算）；一趟走完所有紀錄，與組合數無關
        var lastDataDay = loaded.Max(r => r.Date.Date);
        var appearances = new Dictionary<(string HostName, string IssueKey), List<DateTime>>(HostIssueSignatureKeyComparer.Instance);
        foreach (var record in loaded)
        {
            var host = nameOf(record);
            var date = record.Date.Date;
            foreach (var issue in record.TopIssues)
            {
                var key = (host, IssueSignatureKey.For(issue.LogName, issue.Source, issue.EventId, issue.EntryType, issue.EventKey));
                if (!lastResolved.ContainsKey(key)) continue;
                if (exclusion.IsMuted(issue.Source, issue.EventId, date)) continue;
                if (!appearances.TryGetValue(key, out var days)) appearances[key] = days = new List<DateTime>();
                days.Add(date);
            }
        }

        // 依問題彙總：期間內被標為已處理的主機數／其中已再發生的主機數
        var perIssue = new Dictionary<string, (string Source, int EventId, string Label, int Hosts, int Recurred)>(IssueSignatureKeyComparer.Instance);
        foreach (var (key, resolvedDay) in lastResolved)
        {
            var windowEnd = resolvedDay.AddDays(RecurrenceWindowDays);
            var recurred = appearances.TryGetValue(key, out var days)
                && days.Any(d => d > resolvedDay && d <= windowEnd);

            if (recurred) result.Recurred++;
            else if (windowEnd <= lastDataDay) result.NotRecurred++;
            else result.Observing++;

            var parsed = IssueSignatureKey.TryParseFull(key.IssueKey);
            if (parsed == null) continue;
            perIssue.TryGetValue(key.IssueKey, out var agg);
            if (agg.Label == null)
            {
                var label = new LogIssueSignature
                {
                    LogName = parsed.Value.LogName, Source = parsed.Value.Source,
                    EventId = parsed.Value.EventId, EventKey = parsed.Value.EventKey
                }.SourceEventLabel;
                agg = (parsed.Value.Source, parsed.Value.EventId, label, 0, 0);
            }
            perIssue[key.IssueKey] = (agg.Source, agg.EventId, agg.Label, agg.Hosts + 1, agg.Recurred + (recurred ? 1 : 0));
        }

        var judged = result.Recurred + result.NotRecurred;
        result.Rate = judged == 0 ? null : (double)result.Recurred / judged;
        result.TopRecurring = perIssue.Values
            .Where(v => v.Recurred > 0)
            .OrderByDescending(v => v.Recurred)
            .ThenByDescending(v => v.Hosts)
            .ThenBy(v => v.Source, StringComparer.Ordinal)
            .ThenBy(v => v.EventId)
            .Take(TopRecurringLimit)
            .Select(v => new ReportRecurringIssueDto
            {
                Source = v.Source, EventId = v.EventId, Label = v.Label, HostCount = v.Hosts, RecurredCount = v.Recurred
            })
            .ToList();
        return result;
    }

    private static ReportEffectivenessDefinitionsDto BuildDefinitions() => new()
    {
        Resolution = "期間內的高、中風險日，依「現在」的處理狀態分成已處理、處理中、未處理；以風險日的紀錄日歸入時間桶，不受上方顯示範圍影響。",
        Aging = "目前仍未結案（未處理或處理中）的風險日，距離資料最後一個紀錄日已經過了幾天。",
        Recurrence = $"已處理後 {RecurrenceWindowDays} 天內同一台主機同一個問題又出現的比例；觀察未滿 {RecurrenceWindowDays} 天且尚未再發生的不計入，不處理、誤報、已知雜訊也不算已處理。「又出現」只看系統設定中顯示的風險等級與嚴重度。"
    };
}
