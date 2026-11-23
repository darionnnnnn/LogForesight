using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 報表「類型趨勢」（WEB-SPEC §9.6）：類別 × 時間桶的問題日熱度表。
/// 問題日＝主機 × 問題簽章 × 紀錄日（同一主機同一天同一問題只算一次）。
/// 套可見範圍、顯示設定（嚴重度與風險等級，皆由 <see cref="IRecordRepository.QueryLightweight"/> 套用）與靜音排除，
/// 不套 handlingScope。依日期分段查詢、記憶體彙總。
/// </summary>
public class ReportCategoryTrendService
{
    /// <summary>分段載入的天數（見 <see cref="Build"/>）</summary>
    private const int SegmentDays = 7;

    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly ISystemSettingsService _systemSettings;
    private readonly SummaryCache _cache;
    private readonly IIssueExclusionSource _exclusions;

    public ReportCategoryTrendService(
        IRecordRepository repository, IHostStore hosts, IVisibilityService visibility,
        ISystemSettingsService systemSettings, SummaryCache cache, IIssueExclusionSource exclusions)
    {
        _repository = repository;
        _hosts = hosts;
        _visibility = visibility;
        _systemSettings = systemSettings;
        _cache = cache;
        _exclusions = exclusions;
    }

    public ReportCategoryTrendDto Get(DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        var visibleHosts = _visibility.GetVisibleHosts();
        var exclusion = _exclusions.Current();
        return _cache.GetOrAdd(
            SummaryCache.KeyOf("report-category-trend", $"{from:yyyyMMdd}|{to:yyyyMMdd}|{exclusion.CacheToken}",
                visibleHosts.Select(h => h.HostId).ToList()),
            () => Build(from.Date, to.Date, exclusion));
    }

    private ReportCategoryTrendDto Build(DateTime from, DateTime to, IssueExclusion exclusion)
    {
        var buckets = ReportBucketing.Build(from, to);
        var dto = new ReportCategoryTrendDto
        {
            From = ReportBucketing.Fmt(from),
            To = ReportBucketing.Fmt(to),
            Buckets = ReportBucketing.ToDtos(buckets)
        };

        var (_, _, nothingVisible) = RecordRepository.ResolveVisibility(
            _systemSettings.GetVisibleDayRiskLevels(), _systemSettings.GetVisibleSeverities());
        if (nothingVisible) return dto;

        var lookup = new HostLookup(_hosts.GetAll());
        var cells = new Dictionary<IssueCategory, int[]>();

        // 依日期分段載入（每段 SegmentDays 天、連續不重疊、以日期為界），逐段累計後丟棄該段紀錄，
        // 單次請求的記憶體尖峰由「整期（上限 366 天）所有主機日」降為「一段的量」。
        // 與整期一次載入等價：累計只依各筆紀錄本身與「（主機, 問題鍵, 日期）」去重——去重鍵含日期，
        // 不同日期段之間不會互相影響，所以每段各自一份 seen 即可；HostLookup、靜音判斷與分桶對每筆紀錄獨立。
        // 仍走同一個 QueryLightweight（可見範圍、日風險與嚴重度顯示規則不在這裡重寫），篩選條件除 From／To 外完全相同。
        for (var start = from; start <= to; start = start.AddDays(SegmentDays))
        {
            var end = start.AddDays(SegmentDays - 1);
            if (end > to) end = to;
            var records = _repository.QueryLightweight(new RecordQueryFilter { From = start, To = end });
            if (records.Count == 0) continue;

            // (主機, 問題, 日期) 去重集合；日期不跨段，所以逐段重建
            var seen = new HashSet<(string Host, string IssueKey, DateTime Date)>(new DayKeyComparer());
            foreach (var record in records)
            {
                var date = record.Date.Date;
                var bucket = ReportBucketing.IndexOf(buckets, date);
                if (bucket < 0) continue;
                var host = lookup.For(record)?.HostName ?? record.Host;

                foreach (var issue in record.TopIssues)
                {
                    if (exclusion.IsMuted(issue.Source, issue.EventId, date)) continue;
                    var issueKey = IssueSignatureKey.For(issue.LogName, issue.Source, issue.EventId, issue.EntryType, issue.EventKey);
                    if (!seen.Add((host, issueKey, date))) continue;

                    if (!cells.TryGetValue(issue.Category, out var counts))
                    {
                        counts = new int[buckets.Count];
                        cells[issue.Category] = counts;
                    }
                    counts[bucket]++;
                }
            }
        }

        dto.Rows = cells
            .Select(kv => new ReportCategoryTrendRowDto
            {
                Category = kv.Key.ToString(),
                Total = kv.Value.Sum(),
                Cells = kv.Value.ToList()
            })
            .Where(r => r.Total > 0)
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ToList();
        dto.CellMax = dto.Rows.Count == 0 ? 0 : dto.Rows.Max(r => r.Cells.Max());
        return dto;
    }

    /// <summary>主機名稱不分大小寫、問題鍵走簽章比較器</summary>
    private sealed class DayKeyComparer : IEqualityComparer<(string Host, string IssueKey, DateTime Date)>
    {
        public bool Equals((string Host, string IssueKey, DateTime Date) x, (string Host, string IssueKey, DateTime Date) y) =>
            x.Date == y.Date
            && StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host)
            && IssueSignatureKeyComparer.Instance.Equals(x.IssueKey, y.IssueKey);

        public int GetHashCode((string Host, string IssueKey, DateTime Date) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Host),
                IssueSignatureKeyComparer.Instance.GetHashCode(key.IssueKey), key.Date);
    }
}
