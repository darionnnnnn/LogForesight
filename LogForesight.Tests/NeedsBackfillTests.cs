using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 <see cref="HostDayPostProcessor.NeedsBackfill"/> 的核心語意，以及透過
/// <see cref="ScheduleController.RunPreview"/> 的整合驗證。
///
/// 新增測試涵蓋：
/// 1. 低風險日 AiAnalyzed=false AiPending=false → 不需補跑（合法終局狀態）
/// 2. useAi=false 時，非低風險且 AiAnalyzed=false → 不需補跑；缺日與 AiPending=true → 需補跑
/// 3. ScheduleController.RunPreview：低風險且 AiAnalyzed=false 的主機不計入預覽台數
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class NeedsBackfillTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 測試 1：低風險日合法終局狀態 ────────────────────────────────────────

    [Fact]
    public void NeedsBackfill_低風險日AiAnalyzed為false且AiPending為false_不需補跑()
    {
        // 低風險日「刻意不呼叫 AI」是合法終局狀態，不視為失敗
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today.AddDays(-1),
            HostId = 1,
            Host = "test-host",
            RiskLevel = RiskLevels.Low,
            AiAnalyzed = false,
            AiPending = false
        };

        Assert.False(HostDayPostProcessor.NeedsBackfill(record, useAi: true),
            "低風險日（AiAnalyzed=false、AiPending=false）應判定為不需補跑");
    }

    // ── 測試 2：useAi=false（統計模式）下的判定 ─────────────────────────────

    [Fact]
    public void NeedsBackfill_useAiIsFalse時非低風險且AiAnalyzed為false_不需補跑()
    {
        // AI 未設定時，AiAnalyzed 恆為 false 屬正常狀態，不應全部重跑
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today.AddDays(-1),
            HostId = 1,
            Host = "test-host",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = false
        };

        Assert.False(HostDayPostProcessor.NeedsBackfill(record, useAi: false),
            "useAi=false（統計模式）時，非低風險且 AiAnalyzed=false 仍應判定為不需補跑");
    }

    [Fact]
    public void NeedsBackfill_useAiIsFalse時缺日記錄為null_需補跑()
    {
        // 缺日（null）無論 useAi 如何，都需補跑
        Assert.True(HostDayPostProcessor.NeedsBackfill(null, useAi: false),
            "缺日（record 為 null）應判定為需補跑，與 useAi 無關");
    }

    [Fact]
    public void NeedsBackfill_AiPendingTrue_取數側不補跑()
    {
        // 體檢輪語意變更：AiPending=true 的待補由獨立 AI 分析排程消化，取數側不再視為要補
        // ——否則「只補跑失敗或未執行」會對整個 AI 積壓重打 Sentinel、重寫紀錄再標回待補，
        // 與 AI 排程互相打架、補跑歷史永不收斂。
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today.AddDays(-1),
            HostId = 1,
            Host = "test-host",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = true
        };

        Assert.False(HostDayPostProcessor.NeedsBackfill(record, useAi: true),
            "AiPending=true 應由 AI 排程消化，取數側不補跑");
        Assert.False(HostDayPostProcessor.NeedsBackfill(record, useAi: false),
            "統計模式下同樣不補");
    }

    // ── 測試 3：RunPreview 整合——低風險日不計入預覽台數 ─────────────────────

    [Fact]
    public void RunPreview_低風險且AiAnalyzed為false的主機不計入補跑預覽台數()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var yesterday = DateTime.Today.AddDays(-1);

        // Local host: 低風險日（AI 刻意不呼叫，合法終局）→ 不應計入
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 0,
            Host = Environment.MachineName,
            RiskLevel = RiskLevels.Low,
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 1: 低風險日（AI 刻意不呼叫，合法終局）→ 不應計入
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 1,
            Host = "host-low-risk",
            RiskLevel = RiskLevels.Low,
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 2: 高風險、AI 已定案失敗（AiPending=false、AiAnalyzed=false）→ 應計入
        // （AiPending=true 的待補改由 AI 排程消化、取數側不補——見 NeedsBackfill 體檢輪語意）
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 2,
            Host = "host-pending",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 3: 無紀錄（缺日）→ 應計入

        var hosts = new FakeHostStore();
        var sentinel = new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true };
        hosts.Upsert(new WebHost { HostId = 1, HostName = "host-low-risk", IpAddress = "10.0.0.1", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });
        hosts.Upsert(new WebHost { HostId = 2, HostName = "host-pending", IpAddress = "10.0.0.2", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });
        hosts.Upsert(new WebHost { HostId = 3, HostName = "host-missing", IpAddress = "10.0.0.3", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });

        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(sentinel);

        // AI 已設定（Available=true）→ useAi=true，低風險日不需補跑
        var fakeAi = new FakeWebAi { Available = true };
        var controller = new ScheduleController(
            new ScheduleOptionsStore(_fx.Blob("schedule_options")),
            null!,
            new SchedulerRunState(),
            hosts, sentinels, new RecordingAuditService(), new FakeCurrentUser(), new FakeUserStore(),
            records: store, settingsStore: new FakeSystemSettingsStore(), userDisplayNames: new UserDisplayNameService(new FakeSystemSettingsStore()),
            aiScheduler: null!, aiRunState: new AiAnalysisRunState(), webAi: fakeAi);

        var preview = controller.RunPreview("all", segment: null, hostId: null, onlyMissingOrFailed: true, backfillDays: 1);

        // Local host (低, AiAnalyzed=false, AiPending=false) 不需補跑，不計入
        // host-low-risk (低, AiAnalyzed=false, AiPending=false) 不需補跑，不計入
        // host-pending (高, AI 已定案失敗) 需補跑 → 計入
        // host-missing (無紀錄) 缺日 → 計入
        Assert.Equal(2, preview.Data!.HostCount);
        Assert.False(preview.Data.AiDisabled, "AI 已設定時 AiDisabled 應為 false");
    }

    [Fact]
    public void RunPreview_AI未設定時AiDisabled為true且統計模式下僅補缺日()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test2");
        var yesterday = DateTime.Today.AddDays(-1);

        // Local host: 高風險、AiAnalyzed=false、AiPending=false（統計模式正常完成）→ useAi=false 時不需補跑
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 0,
            Host = Environment.MachineName,
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 1: 高風險、AiAnalyzed=false、AiPending=false（統計模式正常完成）→ useAi=false 時不需補跑
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 1,
            Host = "host-done",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 2: 無紀錄（缺日）→ 即使 useAi=false 也需補跑

        var hosts = new FakeHostStore();
        var sentinel = new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true };
        hosts.Upsert(new WebHost { HostId = 1, HostName = "host-done", IpAddress = "10.0.0.1", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });
        hosts.Upsert(new WebHost { HostId = 2, HostName = "host-missing", IpAddress = "10.0.0.2", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });

        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(sentinel);

        // AI 未設定（Available=false）→ useAi=false，統計模式下只補缺日
        var fakeAi = new FakeWebAi { Available = false };
        var controller = new ScheduleController(
            new ScheduleOptionsStore(_fx.Blob("schedule_options")),
            null!,
            new SchedulerRunState(),
            hosts, sentinels, new RecordingAuditService(), new FakeCurrentUser(), new FakeUserStore(),
            records: store, settingsStore: new FakeSystemSettingsStore(), userDisplayNames: new UserDisplayNameService(new FakeSystemSettingsStore()),
            aiScheduler: null!, aiRunState: new AiAnalysisRunState(), webAi: fakeAi);

        var preview = controller.RunPreview("all", segment: null, hostId: null, onlyMissingOrFailed: true, backfillDays: 1);

        // Local host（高, AiAnalyzed=false, AiPending=false）useAi=false 時不需補跑 → 不計入
        // host-done（高, AiAnalyzed=false, AiPending=false）useAi=false 時不需補跑 → 不計入
        // host-missing（缺日）→ 計入
        Assert.Equal(1, preview.Data!.HostCount);
        Assert.True(preview.Data.AiDisabled, "AI 未設定時 AiDisabled 應為 true");
    }

    [Fact]
    public void NeedsBackfill_AI已成功分析_不需補跑()
    {
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today.AddDays(-1), HostId = 1, Host = "test-host",
            RiskLevel = RiskLevels.High, AiAnalyzed = true, AiPending = false
        };
        Assert.False(HostDayPostProcessor.NeedsBackfill(record, useAi: true));
    }
}
