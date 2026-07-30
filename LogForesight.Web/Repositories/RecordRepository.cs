using LogForesight.Web.Services;

namespace LogForesight.Web.Repositories;

/// <summary>
/// 分析紀錄的查詢組合（docs/WEB-SPEC.md §4.2 Repository 層）。
///
/// 這一層存在的核心理由：**一台主機可能有多個識別**——本身，加上所有已併入它的
/// 墓碑列（人工綁定新舊主機的結果）。查詢時必須把它們一起展開，否則合併之前的歷史
/// 會從畫面上消失。這個展開如果散落在各個 Service，遲早有人忘了做。
/// 集中在這裡，並且**強制**每次查詢都套用可見範圍——呼叫端拿不到「不過濾」的入口。
///
/// 同一個理由延伸到問題嚴重度可見性（docs/HISTORY.md S1）：
/// SiteHidden 模式下 TopIssues 的過濾也在這裡強制套用，所有呼叫端拿到的都是
/// 已過濾的結果，不必也不該各自重覆判斷。
/// </summary>
public interface IRecordRepository
{
    /// <summary>依條件查詢（已套用目前登入者的可見範圍）</summary>
    List<DailyAnalysisRecord> Query(RecordQueryFilter filter);

    /// <summary>
    /// 分頁查詢（已套用目前登入者的可見範圍）——docs/HISTORY.md P1-2。
    /// 可下推的條件盡量在 SQL 端完成排序與分頁，見 <see cref="IAnalysisRecordQuery.QueryPage"/>。
    /// </summary>
    PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false);

    /// <summary>單筆紀錄；不在可見範圍內回 null（不區分「不存在」與「沒權限」）</summary>
    DailyAnalysisRecord? GetOne(long hostId, DateTime date);

    /// <summary>
    /// 主機 ID → 該主機的全部識別（本身＋已併入它的墓碑列，本身排在最前）。
    /// 查無主機時回空清單——空集合在查詢語意上就是「查不到任何資料」，是安全的失敗方向。
    /// </summary>
    List<HostKey> ResolveHostKeys(long hostId);

    /// <summary>主機名稱 → 主機（查無回 null）</summary>
    WebHost? ResolveHost(string hostName);
}

public class RecordRepository : IRecordRepository
{
    private readonly IAnalysisRecordQuery _records;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly ISystemSettingsService _settings;

    public RecordRepository(
        IAnalysisRecordQuery records, IHostStore hosts, IVisibilityService visibility, ISystemSettingsService settings)
    {
        _records = records;
        _hosts = hosts;
        _visibility = visibility;
        _settings = settings;
    }

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        ApplyVisibility(filter);
        return ApplySeverityVisibility(_records.Query(filter));
    }

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false)
    {
        ApplyVisibility(filter);
        var paged = _records.QueryPage(filter, page, pageSize, sortKey, ascending);
        paged.Items = ApplySeverityVisibility(paged.Items);
        return paged;
    }

    /// <summary>
    /// 問題嚴重度可見性（docs/HISTORY.md S1）：SiteHidden 模式下，未勾選層級的
    /// 問題從 TopIssues 整批排除——這是全站唯一的過濾點。Dashboard／Report／RecordQueryService
    /// 的統計、下鑽、AI context（經 GetDetail／ClusterSignatures）全部繼承同一份結果，
    /// 不必（也不該）各自重覆判斷 SeverityDisplayMode——散落判斷正是先前查詢頁分組視圖／
    /// 簽章查詢漏掉這道過濾的原因。DefaultHidden 模式回 null，此處不過濾，交由前端顯示層決定。
    /// 同一個咽喉點也是舊資料嚴重度正規化的落點（見 NormalizeLegacySeverity）——正規化必須
    /// 先做，過濾條件（GetVisibleSeverities）比對的才是正規化後的層級名稱。
    /// </summary>
    private List<DailyAnalysisRecord> ApplySeverityVisibility(List<DailyAnalysisRecord> records)
    {
        foreach (var record in records) NormalizeLegacySeverity(record);

        var visible = _settings.GetVisibleSeverities();
        if (visible == null) return records;

        foreach (var record in records)
        {
            record.TopIssues = record.TopIssues.Where(i => visible.Contains(i.Severity.ToString())).ToList();
        }

        return records;
    }

    private DailyAnalysisRecord ApplySeverityVisibility(DailyAnalysisRecord record)
    {
        NormalizeLegacySeverity(record);

        var visible = _settings.GetVisibleSeverities();
        if (visible != null)
        {
            var before = record.TopIssues.Count;
            record.TopIssues = record.TopIssues.Where(i => visible.Contains(i.Severity.ToString())).ToList();
            // 只有單筆讀取路徑（GetOne，即風險日詳情頁）需要這個數字——docs/HISTORY.md #11
            record.HiddenIssueCount = before - record.TopIssues.Count;
        }

        return record;
    }

    /// <summary>
    /// 舊資料相容（docs/HISTORY.md #1，B1 三級化）：三級化之前寫入的歷史紀錄
    /// 若還是 Severity=Critical，讀取時一律正規化為 High＋ElevatesDayRisk=true——Critical
    /// 原本唯一的實際作用就是「命中即列為高風險日」，正規化後可解釋性不變（「重大」標註沿用）。
    /// 只在讀取時於記憶體正規化，不回寫資料庫——證據層是事後不可改寫的批次判定結果。
    /// 這裡是全站唯一的正規化點，下游 CategoryAggregator／RecordQueryService／ReportService／
    /// RecordStatsBuilder 全部只消費已經過 IRecordRepository 的紀錄，因此自動繼承正規化結果。
    /// </summary>
    private static void NormalizeLegacySeverity(DailyAnalysisRecord record)
    {
        foreach (var issue in record.TopIssues)
        {
            if (issue.Severity == IssueSeverity.Critical)
            {
                issue.Severity = IssueSeverity.High;
                issue.ElevatesDayRisk = true;
            }
        }
    }

    /// <summary>
    /// 呼叫端可以再縮小範圍（例如只看某幾台），但**不可能擴大**——交集永遠不超出可見範圍。
    /// 這是第 3 層防線實際生效的地方，Query 與 QueryPage 共用同一份邏輯。
    /// </summary>
    private void ApplyVisibility(RecordQueryFilter filter)
    {
        var visible = VisibleHostKeys();
        var visibleIds = visible.Select(k => k.HostId).ToHashSet();

        filter.Hosts = filter.Hosts == null
            ? visible
            : filter.Hosts.Where(k => visibleIds.Contains(k.HostId)).ToList();
    }

    public DailyAnalysisRecord? GetOne(long hostId, DateTime date)
    {
        if (!_visibility.GetVisibleHostIds().Contains(hostId)) return null;

        var record = _records.GetOne(ResolveHostKeys(hostId), date);
        return record == null ? null : ApplySeverityVisibility(record);
    }

    public List<HostKey> ResolveHostKeys(long hostId) =>
        HostIdentityResolver.Expand(_hosts.GetAll(), hostId);

    public WebHost? ResolveHost(string hostName) => _hosts.FindByName(hostName);

    /// <summary>
    /// 可見範圍的全部識別：可見主機本身，加上已併入它們的墓碑列。
    ///
    /// **墓碑列因此隨目標主機一起可見**，即使墓碑自己不在任何授權群組裡——這是刻意的：
    /// 併入代表管理員已確認「這兩列是同一台實體機器」，看得到這台機器的人就該看得到
    /// 它改名/重建之前的完整歷史，否則合併等於把歷史藏起來。
    /// </summary>
    private List<HostKey> VisibleHostKeys()
    {
        var visibleIds = _visibility.GetVisibleHostIds();
        var allHosts = _hosts.GetAll();

        return allHosts
            .Where(h => visibleIds.Contains(h.HostId))
            .SelectMany(h => HostIdentityResolver.Expand(allHosts, h.HostId))
            .DistinctBy(k => k.HostId)
            .ToList();
    }
}
