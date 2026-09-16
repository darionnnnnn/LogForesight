using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 A3：全站「執行進行中」告示。
///
/// 告示從儀表板搬到共用版型（layout.js + _Layout.cshtml），所有已登入頁面共用同一個來源；
/// 觸發者文字由 <see cref="RunTriggerText"/> 單點決定，排程頁與 /api/run-activity 不得各講一套。
/// </summary>
public class RunActivityBannerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeUserStore _users = new();
    private readonly UserDisplayNameService _displayNames = new(new FakeSystemSettingsStore());

    public RunActivityBannerTests()
    {
        _users.Upsert(new WebUser { Account = "alice", DisplayName = "愛麗絲", Active = true });
    }

    public void Dispose() => _fx.Dispose();

    private RunActivityController CreateRunActivityController(SchedulerRunState runState, AiAnalysisRunState aiRunState) =>
        new(runState, aiRunState, _users, _displayNames);

    private ScheduleController CreateScheduleController(SchedulerRunState runState, AiAnalysisRunState aiRunState) =>
        new(
            new ScheduleOptionsStore(_fx.Blob("schedule_options")),
            null!,
            runState,
            new FakeHostStore(), new FakeSentinelStore(), new RecordingAuditService(), new FakeCurrentUser(), _users,
            records: null!, settingsStore: new FakeSystemSettingsStore(), userDisplayNames: _displayNames,
            aiScheduler: null!, aiRunState: aiRunState, webAi: new FakeWebAi { Available = true });

    // ── C1／驗收1：觸發者文字兩邊一致 ─────────────────────────────

    [Theory]
    [InlineData("manual:alice")]
    [InlineData("schedule")]
    [InlineData("fetch-followup")]
    public void 取數執行中時TriggerText與排程頁狀態算出的文字相同(string trigger)
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun(trigger, out _));

        var activity = CreateRunActivityController(runState, aiRunState).Get().Data!;
        var status = CreateScheduleController(runState, aiRunState).GetStatus().Data!;

        Assert.True(activity.IsRunning);
        Assert.NotNull(activity.TriggerText);
        // 同一個 helper 的兩個呼叫端：文字一字不差，使用者在兩個畫面看到的是同一件事
        Assert.Equal(status.TriggerText, activity.TriggerText);
    }

    [Fact]
    public void 手動觸發的文字帶出顯示名稱()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("manual:alice", out _));

        var activity = CreateRunActivityController(runState, new AiAnalysisRunState()).Get().Data!;

        Assert.Contains("手動", activity.TriggerText!);
        Assert.Contains("愛麗絲", activity.TriggerText!);
    }

    [Fact]
    public void 沒有任何執行時TriggerText為null()
    {
        var activity = CreateRunActivityController(new SchedulerRunState(), new AiAnalysisRunState()).Get().Data!;

        Assert.False(activity.IsRunning);
        Assert.Null(activity.TriggerText);
    }

    [Fact]
    public void 取數結束後AI未跑時TriggerText回到null()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("manual:alice", out _));
        runState.EndRun();

        var activity = CreateRunActivityController(runState, new AiAnalysisRunState()).Get().Data!;

        Assert.Null(activity.TriggerText);
    }

    // ── 驗收2：兩邊同時在跑時取「取數」那一邊 ─────────────────────

    [Fact]
    public void 取數與AI同時執行時TriggerText取取數那一邊()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun("manual:alice", out _));
        Assert.True(aiRunState.TryBeginRun("schedule", 10, out _));

        var activity = CreateRunActivityController(runState, aiRunState).Get().Data!;

        Assert.Contains("愛麗絲", activity.TriggerText!);   // 取數那一邊（AI 是「排程」）
        Assert.NotEqual("排程", activity.TriggerText);
    }

    [Fact]
    public void 只有AI在執行時TriggerText取AI那一邊()
    {
        var aiRunState = new AiAnalysisRunState();
        Assert.True(aiRunState.TryBeginRun("manual:alice", 10, out _));

        var activity = CreateRunActivityController(new SchedulerRunState(), aiRunState).Get().Data!;

        Assert.True(activity.IsRunning);
        Assert.Contains("愛麗絲", activity.TriggerText!);
    }

    /// <summary>
    /// 告示的 IsRunning 是「取數或 AI 任一在跑」的聯集，但互斥判斷不能用聯集：
    /// 主機更新只在取數執行中才會被後端擋下，AI 單獨在跑時那個動作是允許的。
    /// 少了這條守門，畫面會在 AI 分析期間停用「指定主機更新」並說「取數執行中」，兩件事都不成立。
    /// </summary>
    [Fact]
    public void 只有AI在執行時不算取數執行()
    {
        var aiRunState = new AiAnalysisRunState();
        Assert.True(aiRunState.TryBeginRun("schedule", 10, out _));

        var activity = CreateRunActivityController(new SchedulerRunState(), aiRunState).Get().Data!;

        Assert.True(activity.IsRunning);        // 告示照樣要出現（畫面確實會變慢）
        Assert.False(activity.IsFetchRun);      // 但主機更新不該被擋
    }

    [Fact]
    public void 取數執行中算取數執行()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("schedule", out _));

        var activity = CreateRunActivityController(runState, new AiAnalysisRunState()).Get().Data!;

        Assert.True(activity.IsRunning);
        Assert.True(activity.IsFetchRun);
    }

    [Fact]
    public void 兩者都閒置時不算取數執行()
    {
        var activity = CreateRunActivityController(new SchedulerRunState(), new AiAnalysisRunState()).Get().Data!;

        Assert.False(activity.IsRunning);
        Assert.False(activity.IsFetchRun);
    }
}

/// <summary>階段 A3 的前端結構斷言：告示的來源只剩共用版型一處。</summary>
public class RunActivityBannerUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string ReadWebFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot(), "LogForesight.Web" }.Concat(parts).ToArray()));

    /// <summary>擷取函式主體（從簽章到下一個頂層 function 宣告之前），擷不到就讓測試紅在這裡</summary>
    private static string FunctionBody(string source, string signature, string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {signature}");
        var end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到 {signature} 之後的 {nextSignature}");
        var body = source[start..end];
        Assert.False(string.IsNullOrWhiteSpace(body), "擷取到的函式主體是空的");
        return body;
    }

    // ── 驗收3 ────────────────────────────────────────────────────

    [Fact]
    public void 告示輪詢在執行中30秒閒置60秒且廣播CustomEvent()
    {
        var js = ReadWebFile("wwwroot", "js", "core", "layout.js");
        var body = FunctionBody(js, "async function refreshRunActivity()", "function renderRunActivity(");

        Assert.Contains("30000", body);
        Assert.Contains("60000", body);
        Assert.Contains("setTimeout(refreshRunActivity", body);
        Assert.Contains("new CustomEvent(", body);
        Assert.Contains("lf:run-activity", js);
    }

    /// <summary>
    /// 閒置與 API 失敗都不得停掉輪詢：停掉的話使用者停在這一頁時，永遠等不到下一次執行的告示。
    /// </summary>
    [Fact]
    public void 告示的錯誤處理不清除計時器()
    {
        var js = ReadWebFile("wwwroot", "js", "core", "layout.js");
        var body = FunctionBody(js, "async function refreshRunActivity()", "function renderRunActivity(");

        Assert.DoesNotContain("clearTimeout", body);
        Assert.DoesNotContain("clearInterval", body);
    }

    [Fact]
    public void 共用版型含告示容器且不佔位()
    {
        var layout = ReadWebFile("Views", "Shared", "_Layout.cshtml");
        var line = Array.Find(layout.Split('\n'), l => l.Contains("id=\"lf-run-activity-banner\""));

        Assert.NotNull(line);
        Assert.Contains("lf-no-print", line);
        // 閒置時零高度：容器本身不得帶任何 margin/padding class 撐出空白
        Assert.DoesNotContain(" mb-", line);
        Assert.DoesNotContain(" p-", line);
    }

    // ── 驗收4：儀表板不再自己來一套 ───────────────────────────────

    [Fact]
    public void 儀表板不再自行輪詢執行中告示()
    {
        var js = ReadWebFile("wwwroot", "js", "pages", "dashboard.js");
        var cshtml = ReadWebFile("Views", "Pages", "Dashboard.cshtml");

        Assert.DoesNotContain("run-activity", js);
        Assert.DoesNotContain("dashboard-run-activity", cshtml);
        Assert.DoesNotContain("runActivityTimer", js);
    }

    // ── 驗收5：主機詳情按鈕停用 ───────────────────────────────────

    [Fact]
    public void 主機詳情訂閱告示事件並以disabled加說明呈現()
    {
        var js = ReadWebFile("wwwroot", "js", "pages", "host-detail.js");

        Assert.Contains("lf:run-activity", js);
        Assert.Contains("window.addEventListener(RUN_ACTIVITY_EVENT", js);

        var body = FunctionBody(js, "function applyRunActivityState()", "function ensureRunActivityNotes(");

        Assert.Contains("disabled = schedulerRunning", body);
        Assert.Contains("host-update-submit", body);
        Assert.Contains("classList.toggle('d-none', !schedulerRunning)", body);
        // 用 disabled 加說明，不是把按鈕藏起來（藏起來會被當成權限被拿掉）
        Assert.DoesNotContain("style.display", body);

        // 說明文字抽成常數由兩處共用，函式內引用它。
        // 講的是「取數執行中」而不是籠統的「排程執行中」：AI 分析排程單獨在跑時
        // 這個動作是允許的，說成排程會讓使用者以為系統壞了。
        Assert.Contains("RUN_BUSY_NOTE_TEXT = '取數執行中", js);

        // 停用條件只看取數，不看「取數或 AI 任一在跑」的聯集
        Assert.Contains("isFetchRun === true", js);
        Assert.DoesNotContain("event.detail?.isRunning === true", js);

        var note = FunctionBody(js, "function ensureRunActivityNotes()", "window.addEventListener(RUN_ACTIVITY_EVENT");
        Assert.Contains("RUN_BUSY_NOTE_TEXT", note);

        // 說明必須有**按鈕旁**那一份：頁面上那顆按鈕被停用後 modal 就打不開，
        // 說明只放在 modal 裡等於看不到，使用者面對的仍是一顆沒有理由的灰按鈕。
        Assert.Contains("host-update-open", note);
        Assert.Contains("parentElement", note);
        // 以及 modal 內那一份（modal 已開著時排程才開始，灰掉的是送出鈕）
        Assert.Contains("modal-body", note);
    }
}
