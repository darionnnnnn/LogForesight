using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>
/// 測試專用組裝門面：RecordQueryService 已依關注點拆成 RecordListQueryService（明細/依主機/
/// 依日期/依問題/共通問題）與 RecordDetailQueryService（單日詳情/報告/問題歷史/主機詳情/
/// 主機問題發生明細）。既有測試（RecordQueryServiceSearchTests 等）同一個 fixture 混用兩邊的
/// 方法，這裡組裝成單一門面、保留原本 RecordQueryService 的建構參數與方法簽章，讓既有測試
/// 呼叫端零改動。**僅供測試使用**，production 端一律直接注入拆分後的兩個服務。
/// </summary>
internal class RecordQueryServiceFacade
{
    private readonly RecordListQueryService _list;
    private readonly RecordDetailQueryService _detail;

    public RecordQueryServiceFacade(
        IRecordRepository repository,
        IReportReader reports,
        IHostStore hosts,
        IUserStore users,
        IHostGroupStore hostGroups,
        IVisibilityService visibility,
        IRecordHandlingStore handlings,
        IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases,
        INoiseMarkStore noiseMarks,
        IKnownIssueRuleStore rules,
        ICurrentUser currentUser,
        ISystemSettingsStore settings,
        IIssueAggregateQuery aggregates,
        OccurrenceStatusResolver statusResolver,
        IIssueOwnerStore? issueOwners = null,
        ISystemSettingsService? settingsService = null)
    {
        _list = new RecordListQueryService(
            repository, hosts, users, handlings, issueHandlings, cases, settings,
            settingsService ?? new FakeSystemSettingsService(), visibility, aggregates, statusResolver, issueOwners);
        _detail = new RecordDetailQueryService(
            repository, reports, hosts, users, hostGroups, visibility, issueHandlings, cases, noiseMarks, rules, currentUser, settings);
    }

    public PagedResult<RecordListItemDto> Search(RecordSearchRequest request) => _list.Search(request);
    public PagedResult<RecordHostGroupDto> SearchByHost(RecordSearchRequest request) => _list.SearchByHost(request);
    public PagedResult<RecordDateGroupDto> SearchByDate(RecordSearchRequest request) => _list.SearchByDate(request);
    public PagedResult<IssueGroupDto> SearchByIssue(RecordSearchRequest request) => _list.SearchByIssue(request);
    public List<IssueClusterDto> ClusterSignatures(RecordSearchRequest request) => _list.ClusterSignatures(request);

    public RecordDetailDto GetDetail(long hostId, DateTime date) => _detail.GetDetail(hostId, date);
    public string? GetReport(long hostId, DateTime date) => _detail.GetReport(hostId, date);
    public IssueHistoryDto GetIssueHistory(long hostId, DateTime date, string issueKey) => _detail.GetIssueHistory(hostId, date, issueKey);
    public HostDetailDto GetHostDetail(long hostId, int days) => _detail.GetHostDetail(hostId, days);
    public HostIssueOccurrenceDto GetHostIssueOccurrences(long hostId, string source, int eventId, int days) =>
        _detail.GetHostIssueOccurrences(hostId, source, eventId, days);
}
