﻿﻿using System.Globalization;
using System.Reflection;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// RecordsController 的「query string → 傳進服務的查詢條件」綁定守門。
///
/// 綁定走 ASP.NET Core 自己的 <see cref="ParameterBinder"/>（與正式請求同一套 model binding，
/// 不手寫對照），再以反射呼叫 action；服務層用真的 <see cref="RecordListQueryService"/>，
/// 但底下的 <see cref="IRecordRepository"/> 與 <see cref="IIssueAggregateQuery"/> 換成記錄呼叫的替身，
/// 從替身收到的參數反推控制器組出了什麼查詢條件。
/// 服務層不是 virtual，不能直接攔 <see cref="RecordSearchRequest"/>，所以這裡看的是它往下游傳的結果。
/// </summary>
public class RecordsControllerBindingTests
{
    // ── 替身：記錄呼叫，不回任何資料 ────────────────────────────────────────

    private sealed class RepoSpy : IRecordRepository
    {
        public List<string> Calls { get; } = new();
        public RecordQueryFilter? Filter { get; private set; }
        public string? SortKey { get; private set; }
        public bool Ascending { get; private set; }
        public int Page { get; private set; }
        public int PageSize { get; private set; }

        public List<DailyAnalysisRecord> Query(RecordQueryFilter filter, bool applyDayRiskVisibility = true)
        {
            Calls.Add("Query");
            Filter = filter;
            return new List<DailyAnalysisRecord>();
        }

        public List<DailyAnalysisRecord> QueryLightweight(RecordQueryFilter filter)
        {
            Calls.Add("QueryLightweight");
            Filter = filter;
            return new List<DailyAnalysisRecord>();
        }

        public PagedResult<DailyAnalysisRecord> QueryPage(
            RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false)
        {
            Calls.Add("QueryPage");
            Filter = filter;
            Page = page;
            PageSize = pageSize;
            SortKey = sortKey;
            Ascending = ascending;
            return new PagedResult<DailyAnalysisRecord>();
        }

        public DailyAnalysisRecord? GetOne(long hostId, DateTime date) => null;

        public List<HostKey> ResolveHostKeys(long hostId) =>
            new() { new HostKey { HostId = hostId, HostName = $"H{hostId}" } };

        public WebHost? ResolveHost(string hostName) => null;
    }

    /// <summary>IIssueAggregateQuery 的記錄替身：每個方法回空集合／預設值，只留下參數</summary>
    public class AggregateSpy : DispatchProxy
    {
        public List<(string Name, Dictionary<string, object?> Args)> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            var named = new Dictionary<string, object?>();
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++) named[parameters[i].Name!] = args![i];
            Calls.Add((method.Name, named));

            var returnType = method.ReturnType;
            if (returnType == typeof(void)) return null;
            if (returnType.IsValueType) return Activator.CreateInstance(returnType);
            return returnType.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(returnType) : null;
        }
    }

    // ── 組裝 ─────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public RepoSpy Repo { get; } = new();
        public AggregateSpy Aggregates { get; private set; } = null!;
        public RecordsController Controller { get; private set; } = null!;
        public long[] HostIds { get; private set; } = Array.Empty<long>();

        public static Rig Create()
        {
            var rig = new Rig();
            var hosts = new FakeHostStore();
            var created = new[] { "HOST-A", "HOST-B", "HOST-C" }.Select(n => TestData.AddHost(hosts, n)).ToArray();
            rig.HostIds = created.Select(h => h.HostId).ToArray();

            var visibility = new AlwaysVisibleService(hosts);
            var users = new FakeUserStore();
            var handlings = new FakeHandlingStore();
            var issueHandlings = new FakeIssueHandlingStore();
            var cases = new FakeIssueCaseStore();
            var settingsStore = new FakeSystemSettingsStore();
            var settings = new FakeSystemSettingsService();

            var proxy = DispatchProxy.Create<IIssueAggregateQuery, AggregateSpy>();
            rig.Aggregates = (AggregateSpy)(object)proxy;

            var list = new RecordListQueryService(
                rig.Repo, hosts, users, handlings, issueHandlings, cases, settingsStore, settings, visibility,
                proxy,
                new OccurrenceStatusResolver(hosts, issueHandlings, cases, settingsStore),
                new UserDisplayNameService(settingsStore),
                new NextUnhandledSequenceCache(new DataVersionStamp()),
                new FixedIssueExclusionSource(IssueExclusion.None));
            // 這四個端點不碰單日詳情服務
            rig.Controller = new RecordsController(list, null!);
            return rig;
        }
    }

    private static readonly IReadOnlyList<ControllerActionDescriptor> Actions = LoadActions();

    private static IReadOnlyList<ControllerActionDescriptor> LoadActions()
    {
        using var provider = BuildProvider();
        return provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Where(a => a.ControllerTypeInfo == typeof(RecordsController))
            .ToList();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddControllersWithViews().AddApplicationPart(typeof(RecordsController).Assembly);
        return services.BuildServiceProvider();
    }

    /// <summary>"a=1&amp;b=x,y" → 每個值先跳脫再組成 query string（測試案例直接寫人看得懂的字）</summary>
    private static QueryString ToQueryString(string query)
    {
        if (string.IsNullOrEmpty(query)) return QueryString.Empty;
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var index = pair.IndexOf('=');
            return $"{Uri.EscapeDataString(pair[..index])}={Uri.EscapeDataString(pair[(index + 1)..])}";
        });
        return new QueryString("?" + string.Join("&", parts));
    }

    /// <summary>用 MVC 自己的 ParameterBinder 綁每個參數，再呼叫 action</summary>
    private static object? Invoke(Rig rig, string endpoint, string query)
    {
        var action = Actions.Single(a => a.MethodInfo.Name == endpoint);
        using var provider = BuildProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.QueryString = ToQueryString(query);
        var actionContext = new ActionContext(http, new RouteData(), action);

        var factory = provider.GetRequiredService<IModelBinderFactory>();
        var metadataProvider = (ModelMetadataProvider)provider.GetRequiredService<IModelMetadataProvider>();
        var binder = provider.GetRequiredService<ParameterBinder>();
        var valueProvider = new QueryStringValueProvider(BindingSource.Query, http.Request.Query, CultureInfo.InvariantCulture);

        var args = new List<object?>();
        foreach (var parameter in action.MethodInfo.GetParameters())
        {
            var descriptor = action.Parameters.Single(p => p.Name == parameter.Name);
            var metadata = metadataProvider.GetMetadataForParameter(parameter);
            var modelBinder = factory.CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                BindingInfo = descriptor.BindingInfo,
                CacheToken = descriptor
            });
            var result = binder.BindModelAsync(actionContext, modelBinder, valueProvider, descriptor, metadata, null)
                .GetAwaiter().GetResult();
            if (result.IsModelSet) args.Add(result.Model);
            else if (parameter.HasDefaultValue) args.Add(parameter.DefaultValue);
            else args.Add(parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null);
        }

        try
        {
            return action.MethodInfo.Invoke(rig.Controller, args.ToArray());
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // ── 摘要：把替身收到的參數壓成一行字串 ─────────────────────────────────────

    private static string D(DateTime? value) => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "-";

    private static string Csv<T>(IEnumerable<T>? values) =>
        values == null ? "-" : string.Join(",", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).OrderBy(s => s, StringComparer.Ordinal));

    private static string Summarize(Rig rig, string endpoint)
    {
        if (endpoint == nameof(RecordsController.Search))
        {
            var f = rig.Repo.Filter!;
            var path = rig.Repo.Calls.Single(c => c is "QueryPage" or "QueryLightweight");
            var head = $"{path}|hosts={Csv(f.Hosts?.Select(h => h.HostId))}|from={D(f.From)}|to={D(f.To)}" +
                       $"|risk={Csv(f.RiskLevels)}|cat={Csv(f.Categories)}|sev={f.MinSeverity?.ToString() ?? "-"}" +
                       $"|event={f.EventId?.ToString() ?? "-"}|src={f.Source ?? "-"}";
            return path == "QueryPage"
                ? $"{head}|sort={rig.Repo.SortKey ?? "-"}|asc={rig.Repo.Ascending}|page={rig.Repo.Page}|size={rig.Repo.PageSize}"
                : head;
        }

        var call = rig.Aggregates.Calls.First(c => c.Name is "AggregateByHost" or "AggregateByDate" or "Aggregate");
        var a = call.Args;
        var summary = $"{call.Name}|hosts={Csv((IEnumerable<long>?)a["hostIds"])}|from={D((DateTime)a["from"]!)}|to={D((DateTime)a["to"]!)}";
        if (call.Name == "Aggregate") return summary;
        return summary +
               $"|risk={Csv((IEnumerable<string>?)a["riskLevels"])}|cat={Csv((IEnumerable<IssueCategory>?)a["categories"])}" +
               $"|event={a["eventId"] ?? "-"}|src={a["source"] ?? "-"}|sev={a["minSeverity"] ?? "-"}";
    }

    private static string Expand(string template, Rig rig, string lookbackStart) =>
        template
            .Replace("{h1}", rig.HostIds[0].ToString(CultureInfo.InvariantCulture))
            .Replace("{h2}", rig.HostIds[1].ToString(CultureInfo.InvariantCulture))
            .Replace("{h3}", rig.HostIds[2].ToString(CultureInfo.InvariantCulture))
            .Replace("{all}", Csv(rig.HostIds))
            .Replace("{lookback}", lookbackStart);

    private static string LookbackStart() =>
        DateTime.Today.AddDays(-1).AddDays(-RecordListQueryService.DefaultLookbackDays).ToString("yyyy-MM-dd");

    // ── 表驅動案例 ───────────────────────────────────────────────────────────

    private const string Full =
        "hostIds={h1},{h2}&from=2026-10-01&to=2026-10-05&riskLevels=高,中&categories=Hardware,Storage" +
        "&severity=High&eventId=7&source=Disk&sort=date&dir=asc&page=2&pageSize=25";

    public static IEnumerable<object[]> Cases() => new[]
    {
        // 明細（Search）
        new object[] { "Search", Full,
            "QueryPage|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05|risk=中,高|cat=Hardware,Storage|sev=High|event=7|src=Disk|sort=date|asc=True|page=2|size=25" },
        new object[] { "Search", "hostIds={h1},{h2}&from=2026-10-01&to=2026-10-05",
            "QueryPage|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },
        new object[] { "Search", "from=not-a-date&to=2026-10-05",
            "QueryPage|hosts=-|from={lookback}|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },
        new object[] { "Search", "from=2026-10-09&to=2026-10-01",
            "QueryPage|hosts=-|from=2026-10-01|to=2026-10-09|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&dir=ASC",
            "QueryPage|hosts=-|from=2026-10-01|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&dir=desc",
            "QueryPage|hosts=-|from=2026-10-01|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&dir=asc",
            "QueryPage|hosts=-|from=2026-10-01|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=True|page=1|size=50" },
        // 處理狀態類條件走整批撈回的慢路徑（不分頁下推）
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&overdue=true",
            "QueryLightweight|hosts=-|from=2026-10-01|to=2026-10-05|risk=中,高|cat=-|sev=-|event=-|src=-" },
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&unassigned=true",
            "QueryLightweight|hosts=-|from=2026-10-01|to=2026-10-05|risk=中,高|cat=-|sev=-|event=-|src=-" },
        new object[] { "Search", "from=2026-10-01&to=2026-10-05&statuses=open,in_progress",
            "QueryLightweight|hosts=-|from=2026-10-01|to=2026-10-05|risk=中,高|cat=-|sev=-|event=-|src=-" },
        new object[] { "Search", "hostIds=&from=2026-10-01&to=2026-10-05",
            "QueryPage|hosts=-|from=2026-10-01|to=2026-10-05|risk=-|cat=-|sev=-|event=-|src=-|sort=-|asc=False|page=1|size=50" },

        // 依主機（ByHost）
        new object[] { "ByHost", Full,
            "AggregateByHost|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05|risk=中,高|cat=Hardware,Storage|event=7|src=Disk|sev=High" },
        new object[] { "ByHost", "hostIds={h1},{h2}&from=2026-10-01&to=2026-10-05",
            "AggregateByHost|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },
        new object[] { "ByHost", "from=not-a-date&to=2026-10-05",
            "AggregateByHost|hosts={all}|from=0001-01-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },
        new object[] { "ByHost", "from=2026-10-09&to=2026-10-01",
            "AggregateByHost|hosts={all}|from=2026-10-01|to=2026-10-09|risk=-|cat=-|event=-|src=-|sev=-" },
        // 依主機不支援 overdue／statuses／unassigned：帶了也被忽略，與不帶時送給聚合查詢的參數完全相同
        new object[] { "ByHost", "from=2026-10-01&to=2026-10-05&overdue=true&statuses=open&unassigned=true",
            "AggregateByHost|hosts={all}|from=2026-10-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },
        new object[] { "ByHost", "hostIds=&from=2026-10-01&to=2026-10-05",
            "AggregateByHost|hosts={all}|from=2026-10-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },

        // 依日期（ByDate）
        new object[] { "ByDate", Full,
            "AggregateByDate|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05|risk=中,高|cat=Hardware,Storage|event=7|src=Disk|sev=High" },
        new object[] { "ByDate", "from=2026-10-01&to=2026-10-05&overdue=true&unassigned=true",
            "AggregateByDate|hosts={all}|from=2026-10-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },
        new object[] { "ByDate", "from=not-a-date&to=2026-10-05",
            "AggregateByDate|hosts={all}|from=0001-01-01|to=2026-10-05|risk=-|cat=-|event=-|src=-|sev=-" },

        // 依問題（ByIssue）：聚合查詢只看得到主機與期間（其餘條件在服務層後段篩選）
        new object[] { "ByIssue", Full + "&statuses=open&unassigned=true",
            "Aggregate|hosts={h1},{h2}|from=2026-10-01|to=2026-10-05" },
        new object[] { "ByIssue", "from=2026-10-01&to=2026-10-05&overdue=true",
            "Aggregate|hosts={all}|from=2026-10-01|to=2026-10-05" },
        new object[] { "ByIssue", "from=2026-10-09&to=2026-10-01",
            "Aggregate|hosts={all}|from=2026-10-01|to=2026-10-09" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 查詢參數綁定後傳進服務的條件(string endpoint, string query, string expected)
    {
        var rig = Rig.Create();

        Invoke(rig, endpoint, Expand(query, rig, LookbackStart()));

        Assert.Equal(Expand(expected, rig, LookbackStart()), Summarize(rig, endpoint));
    }

    [Theory]
    [InlineData("Search", "hostIds=abc")]
    [InlineData("ByHost", "hostIds=abc")]
    [InlineData("ByDate", "groupIds=x,y")]
    [InlineData("ByIssue", "hostIds=,,abc")]
    public void 主機或群組參數全是非數字_回驗證錯誤(string endpoint, string query)
    {
        var rig = Rig.Create();

        var ex = Assert.Throws<DomainException>(() => Invoke(rig, endpoint, query));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("主機／群組參數格式不正確", ex.Message);
        Assert.Empty(rig.Repo.Calls);
        Assert.Empty(rig.Aggregates.Calls);
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("ByHost")]
    [InlineData("ByDate")]
    [InlineData("ByIssue")]
    public void 主機與群組參數為空字串_視為未指定(string endpoint)
    {
        var rig = Rig.Create();

        Invoke(rig, endpoint, "hostIds=&groupIds=&from=2026-10-01&to=2026-10-05");

        var summary = Summarize(rig, endpoint);
        Assert.Contains(endpoint == "Search" ? "hosts=-" : "hosts=" + Csv(rig.HostIds), summary);
    }

    // ── 支援矩陣：RecordQuerySupport 是單一出處，BuildRequest 只帶支援的條件 ─────────

    [Theory]
    [InlineData(RecordView.Detail, true, true, true)]
    [InlineData(RecordView.ByIssue, true, false, true)]
    [InlineData(RecordView.ByHost, false, false, false)]
    [InlineData(RecordView.ByDate, false, false, false)]
    public void 篩選支援矩陣_BuildRequest只帶該視角支援的條件(
        RecordView view, bool statuses, bool overdue, bool unassigned)
    {
        var query = new RecordQuery { Statuses = "open,in_progress", Overdue = true, Unassigned = true };

        var request = RecordsController.BuildRequest(query, view);

        Assert.Equal(statuses, request.Statuses is { Count: 2 });
        Assert.Equal(overdue, request.Overdue == true);
        Assert.Equal(unassigned, request.Unassigned);
        Assert.Equal(statuses, RecordQuerySupport.Supports(view, "statuses"));
        Assert.Equal(overdue, RecordQuerySupport.Supports(view, "overdue"));
        Assert.Equal(unassigned, RecordQuerySupport.Supports(view, "unassigned"));
    }
}
