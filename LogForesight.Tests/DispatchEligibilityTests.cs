using System.Text.RegularExpressions;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次 D：派工資格單一判定的守門與續掛停止標示的儲存行為。
/// 情境層（S4／S5／S6、D1～D4）在 <c>HandoffScenarioTests</c>。
/// </summary>
public class DispatchEligibilityTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    private static string CodeOf(string relativePath) =>
        string.Join("\n", File.ReadAllLines(Path.Combine(RepoRoot(), relativePath))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>D1-f：判斷停用／暫停／可見主機的程式在派工決策內只出現一處（<c>IneligibleReason</c>）</summary>
    [Fact]
    public void 派工決策內判斷暫停與可見主機的程式只在一個函式()
    {
        var dispatcher = CodeOf("LogForesight.Core/Persistence/Dispatch/WorkOrderDispatcher.cs");

        Assert.Single(Regex.Matches(dispatcher, @"\.Paused\b"));
        Assert.Single(Regex.Matches(dispatcher, @"\.VisibleHostIds\b"));
        Assert.Single(Regex.Matches(dispatcher, @"\.InPool\b"));
        Assert.Single(Regex.Matches(dispatcher, @"Pool\.ByUserId\.TryGetValue"));

        var body = Regex.Match(dispatcher, @"internal static string\? IneligibleReason\(.*?\n    \}", RegexOptions.Singleline).Value;
        Assert.Contains(".Paused", body);
        Assert.Contains(".VisibleHostIds", body);
        Assert.Contains("TryGetValue", body);

        // 同一資料夾的其他派工檔不得再自己判斷
        foreach (var file in new[] { "DispatchContext.cs", "NightlyDispatch.cs", "DispatchCandidate.cs" })
        {
            var code = CodeOf("LogForesight.Core/Persistence/Dispatch/" + file);
            Assert.DoesNotMatch(@"\.Paused\b|\.VisibleHostIds\b", code);
        }
    }

    private WorkOrder NewOrder(long handler = 1, int eventId = 153) => new()
    {
        SourceName = "Disk", EventId = eventId, IssueLabel = $"Disk {eventId}", HandlerId = handler,
        Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.All, AutoAttach = true,
        CreatedByAccount = "a", CreatedAt = DateTime.Now
    };

    [Fact]
    public void 續掛停止標示寫入與清除_清除時停止時間一併清掉()
    {
        var store = new EfWorkOrderStore(_fx.NewContext);
        var id = store.Insert(NewOrder());
        var at = new DateTime(2026, 11, 10, 3, 0, 0);

        store.SetContinuationStops(new[] { new ContinuationStopUpdate(id, ContinuationStopReasons.HandlerPaused, at) });
        var stopped = store.Get(id)!;
        Assert.Equal(ContinuationStopReasons.HandlerPaused, stopped.ContinuationStoppedReason);
        Assert.Equal(at, stopped.ContinuationStoppedAt);

        store.SetContinuationStops(new[] { new ContinuationStopUpdate(id, null, at.AddDays(1)) });
        var cleared = store.Get(id)!;
        Assert.Null(cleared.ContinuationStoppedReason);
        Assert.Null(cleared.ContinuationStoppedAt);
    }

    /// <summary>標示由夜間派工單句 UPDATE 寫入：不動併發權杖，手上拿著舊複本的整列 Save 也不能把它蓋回去</summary>
    [Fact]
    public void 整列Save不會蓋掉續掛停止標示_也不推進併發權杖()
    {
        var store = new EfWorkOrderStore(_fx.NewContext);
        var id = store.Insert(NewOrder());
        var stale = store.Get(id)!;
        var version = stale.UpdatedAt;

        store.SetContinuationStops(new[] { new ContinuationStopUpdate(id, ContinuationStopReasons.HostNotVisible, DateTime.Now) });
        Assert.Equal(version, store.Get(id)!.UpdatedAt);

        stale.Note = "管理者改了備註";
        store.Save(stale);

        var after = store.Get(id)!;
        Assert.Equal("管理者改了備註", after.Note);
        Assert.Equal(ContinuationStopReasons.HostNotVisible, after.ContinuationStoppedReason);
        Assert.NotNull(after.ContinuationStoppedAt);
    }

    [Fact]
    public void 續掛停止標示不存在的單略過_空清單不寫()
    {
        var store = new EfWorkOrderStore(_fx.NewContext);

        store.SetContinuationStops(Array.Empty<ContinuationStopUpdate>());
        var ex = Record.Exception(() =>
            store.SetContinuationStops(new[] { new ContinuationStopUpdate(9999, ContinuationStopReasons.HandlerInactive, DateTime.Now) }));

        Assert.Null(ex);
    }

    [Fact]
    public void 原因代碼都有顯示名稱_未知代碼回null()
    {
        foreach (var code in ContinuationStopReasons.All) Assert.False(string.IsNullOrWhiteSpace(ContinuationStopReasons.LabelOf(code)));
        Assert.Null(ContinuationStopReasons.LabelOf(null));
        Assert.Null(ContinuationStopReasons.LabelOf("unknown"));
        Assert.Equal(3, ContinuationStopReasons.All.Length);
    }

    /// <summary>JSON 依宣告型別序列化：交辦單詳情與總覽端點的宣告型別必須含續掛停止欄位（見 CLAUDE.md 控制器回傳型別）</summary>
    [Fact]
    public void 交辦單列與詳情DTO含續掛停止欄位且端點宣告實際型別()
    {
        var row = typeof(WorkOrderRowDto);
        foreach (var name in new[] { "ContinuationStopped", "ContinuationStoppedReason", "ContinuationStoppedLabel", "ContinuationStoppedAt" })
            Assert.NotNull(row.GetProperty(name));
        Assert.True(typeof(WorkOrderDetailDto).IsSubclassOf(row));

        var detailAction = typeof(LogForesight.Web.Controllers.Api.WorkOrderViewController).GetMethod("Get")!;
        Assert.Equal(typeof(ApiResponse<WorkOrderDetailDto>), detailAction.ReturnType);
        var listAction = typeof(LogForesight.Web.Controllers.Api.WorkOrderListController).GetMethod("List")!;
        Assert.Equal(typeof(ApiResponse<WorkOrderListDto>), listAction.ReturnType);
    }
}
