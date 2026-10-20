using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// D12：<see cref="RecordListQueryService.Search"/> 慢速路徑（處理狀態／逾期／未指派篩選）的
/// 預設風險等級與候選上限守門。替身 repository 只記錄收到的 filter 與呼叫次數，不碰資料庫。
/// </summary>
public class RecordListQueryServiceTests
{
    private readonly RecordingRecordRepository _repository = new();
    private readonly RecordListQueryService _service;

    public RecordListQueryServiceTests()
    {
        var hosts = new FakeHostStore();
        var settings = new FakeSystemSettingsStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        _service = new RecordListQueryService(
            _repository, hosts, new FakeUserStore(), new FakeHandlingStore(), issueHandlings, cases, settings,
            new FakeSystemSettingsService(), new AlwaysVisibleService(hosts), new FakeIssueAggregateQuery(),
            new OccurrenceStatusResolver(hosts, issueHandlings, cases, settings),
            new UserDisplayNameService(settings),
            new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None));
    }

    [Fact]
    public void Search_帶狀態篩選且未帶風險等級_只查高與中()
    {
        _service.Search(new RecordSearchRequest { Statuses = new List<string> { "open" } });

        Assert.Equal(new[] { RiskLevels.High, RiskLevels.Medium }, _repository.LastLightweightFilter!.RiskLevels);
    }

    [Fact]
    public void Search_明確帶三種風險等級_原樣傳給查詢()
    {
        _service.Search(new RecordSearchRequest
        {
            Statuses = new List<string> { "open" },
            RiskLevels = new List<string> { "高", "中", "低" }
        });

        Assert.Equal(new[] { "高", "中", "低" }, _repository.LastLightweightFilter!.RiskLevels);
    }

    [Fact]
    public void Search_候選超過上限擲驗證例外_剛好等於上限不擲()
    {
        _repository.ResultCount = RecordListQueryService.HandlingFilterCandidateLimit + 1;
        Assert.Throws<DomainException>(() =>
            _service.Search(new RecordSearchRequest { Statuses = new List<string> { "open" } }));

        _repository.ResultCount = RecordListQueryService.HandlingFilterCandidateLimit;
        var exception = Record.Exception(() =>
            _service.Search(new RecordSearchRequest { Statuses = new List<string> { "open" } }));
        Assert.Null(exception);
    }

    [Fact]
    public void Search_無狀態篩選走快速路徑_不呼叫QueryLightweight()
    {
        _service.Search(new RecordSearchRequest());

        Assert.Equal(0, _repository.QueryLightweightCount);
        Assert.Equal(1, _repository.QueryPageCount);
    }

    /// <summary>記錄收到的 filter 與呼叫次數；QueryLightweight 回傳指定筆數的高風險日。</summary>
    private sealed class RecordingRecordRepository : IRecordRepository
    {
        public int ResultCount { get; set; }
        public int QueryLightweightCount { get; private set; }
        public int QueryPageCount { get; private set; }
        public RecordQueryFilter? LastLightweightFilter { get; private set; }

        public List<DailyAnalysisRecord> QueryLightweight(RecordQueryFilter filter)
        {
            QueryLightweightCount++;
            LastLightweightFilter = filter;
            var start = new DateTime(2024, 1, 1);
            return Enumerable.Range(0, ResultCount)
                .Select(i => new DailyAnalysisRecord { HostId = 1, Host = "H", Date = start.AddDays(i), RiskLevel = "高" })
                .ToList();
        }

        public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false)
        {
            QueryPageCount++;
            return new PagedResult<DailyAnalysisRecord> { Items = new List<DailyAnalysisRecord>(), Page = page, PageSize = pageSize, Total = 0 };
        }

        public List<DailyAnalysisRecord> Query(RecordQueryFilter filter, bool applyDayRiskVisibility = true) => new();
        public DailyAnalysisRecord? GetOne(long hostId, DateTime date) => null;
        public List<HostKey> ResolveHostKeys(long hostId) => new();
        public WebHost? ResolveHost(string hostName) => null;
    }
}
