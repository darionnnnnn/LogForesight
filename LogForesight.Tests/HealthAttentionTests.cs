using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 C-4：健康整體狀態的唯一判定（<see cref="HealthAttention"/>）。
/// 併入清單的每個條件各一例 → 整體需要注意且原因含該條；全部正常 → 沒有原因。
/// </summary>
public class HealthAttentionTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0);

    /// <summary>每個原因種類一例；新增種類沒補案例時 <see cref="每個原因種類都有案例"/> 轉紅。</summary>
    private static readonly Dictionary<string, (HealthAttentionInput Input, string TextPart)> Cases = new()
    {
        [HealthAttentionKeys.Storage] = (new() { StorageDown = true }, "資料庫無法連線"),
        [HealthAttentionKeys.SlowQueries] = (new() { SlowOperationsHigh = true }, "慢操作"),
        [HealthAttentionKeys.FirstSeen] = (new() { FirstSeenFailed = true }, "首見日"),
        [HealthAttentionKeys.Freshness] = (new() { ScheduleStaleAndNotAcked = true }, "48 小時"),
        [HealthAttentionKeys.PrtgSnapshot] = (new() { PrtgSnapshotWarning = true, PrtgSnapshotMessage = "快照停擺" }, "快照停擺"),
        [HealthAttentionKeys.Memory] = (new() { MemoryWarning = true }, "記憶體"),
        [HealthAttentionKeys.Crypto] = (new() { CryptoKeyMismatch = true }, "金鑰"),
        [HealthAttentionKeys.Migration] = (new()
        {
            Migrations = new[] { new MigrationHealth("處理狀態", "running", Now.AddMinutes(-45), null) }
        }, "處理狀態遷移超過 30 分鐘"),
        [HealthAttentionKeys.PrtgFreshness] = (new()
        {
            PrtgFreshnessSuspicious = new[] { ("歷史數值", 3) }
        }, "歷史數值連續 3 次取得 0 筆"),
        [HealthAttentionKeys.HostDayRecovery] = (new() { HostDayRecoveryNeedsAttention = 2 }, "2 筆需要檢查"),
        [HealthAttentionKeys.LastRun] = (new() { LastRunSucceeded = false }, "最近一趟分析執行失敗"),
        [HealthAttentionKeys.MailSuspended] = (new()
        {
            SuspendedMailRecipients = new[] { "bad@example.test" }
        }, "bad@example.test"),
        [HealthAttentionKeys.Retention] = (new()
        {
            RetentionFailure = new RetentionFailure(Now, new[] { "歷史紀錄", "報告全文" })
        }, "歷史紀錄、報告全文"),
        [HealthAttentionKeys.PrtgSensorsEmpty] = (new()
        {
            PrtgSyncWarnings = new[] { PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "感測器全部回空") }
        }, "感測器全部回空"),
        [HealthAttentionKeys.PrtgDevicesPurged] = (new()
        {
            PrtgSyncWarnings = new[] { PrtgSyncWarning.Create(PrtgSyncWarning.MappedDevicesPurged, "已對應主機的裝置被清除") }
        }, "已對應主機的裝置被清除"),
        [HealthAttentionKeys.CertificateTrust] = (new() { UnpinnedCertificateBypass = new[] { "Sentinel" } }, "Sentinel開了「忽略憑證錯誤」"),
        [HealthAttentionKeys.OutboxDropped] = (new() { OutboxRecentDropped = 3, OutboxLastDroppedAt = Now.AddDays(-1) }, "丟棄了 3 筆"),
        [HealthAttentionKeys.AiUnreachable] = (new() { AiUnreachable = true }, "AI 服務連線失敗"),
        [HealthAttentionKeys.DispatchReadiness] = (new()
        {
            DispatchReadinessFailures = new[] { "自動派工開關沒有開，夜間分析後不會自動派單。" }
        }, "派工就緒：自動派工開關沒有開"),
    };

    public static IEnumerable<object[]> AllKeys() => HealthAttentionKeys.All.Select(k => new object[] { k });

    [Fact]
    public void 每個原因種類都有案例()
    {
        Assert.Equal(HealthAttentionKeys.All.Order(), Cases.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void 併入清單的每個條件_整體需要注意且原因含該條(string key)
    {
        var (input, textPart) = Cases[key];

        var reasons = HealthAttention.Evaluate(input, Now);

        var reason = Assert.Single(reasons);
        Assert.Equal(key, reason.Key);
        Assert.Contains(textPart, reason.Text);
        Assert.EndsWith("。", reason.Text);   // 每個原因一句中文
    }

    [Fact]
    public void 交辦通知丟棄_滿七天前仍提示_超過七天消失_沒有丟棄不提示()
    {
        HealthAttentionInput Input(int dropped, DateTime? at) => new() { OutboxRecentDropped = dropped, OutboxLastDroppedAt = at };

        // 邊界：剛好七天仍在窗口內；多一秒就消失
        Assert.Single(HealthAttention.Evaluate(Input(2, Now - HealthAttention.OutboxDropRecentWindow), Now));
        Assert.Empty(HealthAttention.Evaluate(Input(2, Now - HealthAttention.OutboxDropRecentWindow - TimeSpan.FromSeconds(1)), Now));
        // 從未丟棄、或筆數為 0（即使時間在窗口內）都不提示
        Assert.Empty(HealthAttention.Evaluate(Input(0, null), Now));
        Assert.Empty(HealthAttention.Evaluate(Input(0, Now), Now));
    }

    [Fact]
    public void 全部正常_沒有原因()
    {
        Assert.Empty(HealthAttention.Evaluate(new HealthAttentionInput(), Now));
        // 邊界另一側：條件的「不成立」值也不產生原因
        Assert.Empty(HealthAttention.Evaluate(new HealthAttentionInput
        {
            LastRunSucceeded = true,
            Migrations = new[]
            {
                new MigrationHealth("處理狀態", "completed", Now.AddHours(-5), null),
                new MigrationHealth("權限異動", "unknown", null, null),
                new MigrationHealth("處理狀態", "running", Now.AddMinutes(-5), null)   // 剛開始、未滿 30 分鐘
            },
            PrtgSyncWarnings = new[] { new PrtgSyncWarning { Code = "unknown-code", Message = "x" } }
        }, Now));
    }

    [Fact]
    public void 遷移有失敗訊息即視為卡住_原因帶訊息()
    {
        var reasons = HealthAttention.Evaluate(new HealthAttentionInput
        {
            Migrations = new[] { new MigrationHealth("權限異動", "pending", null, "磁碟已滿") }
        }, Now);

        Assert.Contains("權限異動遷移卡住", Assert.Single(reasons).Text);
        Assert.Contains("磁碟已滿", reasons[0].Text);
    }

    [Fact]
    public void 多個條件同時成立_原因逐項列出()
    {
        var reasons = HealthAttention.Evaluate(new HealthAttentionInput
        {
            LastRunSucceeded = false,
            HostDayRecoveryNeedsAttention = 1,
            SuspendedMailRecipients = new[] { "a@x.test", "b@x.test", "c@x.test", "d@x.test" }
        }, Now);

        Assert.Equal(new[] { HealthAttentionKeys.HostDayRecovery, HealthAttentionKeys.LastRun, HealthAttentionKeys.MailSuspended },
            reasons.Select(r => r.Key).Order().ToArray());
        Assert.Contains("等 4 位", reasons.Single(r => r.Key == HealthAttentionKeys.MailSuspended).Text);
    }

    [Fact]
    public void 超長的PRTG警告文字與收件人在原因裡被截斷()
    {
        var longText = new string('字', 5000);
        var reasons = HealthAttention.Evaluate(new HealthAttentionInput
        {
            PrtgSyncWarnings = new[] { new PrtgSyncWarning { Code = PrtgSyncWarning.SensorsAllEmpty, Message = longText } },
            SuspendedMailRecipients = new[] { new string('m', 5000) }
        }, Now);

        Assert.All(reasons, r => Assert.True(r.Text.Length <= PrtgSyncWarning.MaxMessageLength + 20, $"{r.Key} 長度 {r.Text.Length}"));
    }
}

/// <summary>
/// 判定接進 <see cref="HealthService.GetDetail"/> 的接線：保留清除失敗記錄與 PRTG 同步警告都真的讓整體狀態變成需要注意，
/// 排除後恢復正常；<see cref="HealthService.GetAttention"/> 與詳情是同一份判定。
/// </summary>
[Collection("CryptoKeyState")]
public class HealthAttentionServiceWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-healthattn-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HealthAttentionServiceWiringTests()
    {
        CryptoHelper.ResetForTests();
        CryptoKeyBootstrapper.KeyMismatch = false;
        CryptoKeyBootstrapper.KeyLocationWarning = null;
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ScheduleFreshnessService NewFreshnessService() => new(
        new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
        new ScheduleOptionsStore(_backend.Blob("schedule_options")));

    private HealthService NewService(TimeProvider? clock = null, IAiProbeService? aiProbe = null)
    {
        var freshness = NewFreshnessService();
        var mail = new MailNotificationService(
            new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
            new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness, new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)),
            new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore());
        return new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mail, freshness, clock: clock, aiProbe: aiProbe);
    }

    private static WorkOrderOutboxItem OutboxItem(int i) => new()
    {
        Kind = WorkOrderOutboxKinds.Created, WorkOrderId = i, RecipientUserId = 1, ActorAccount = "admin"
    };

    /// <summary>F22-2：AI 已設定卻連不上才是故障；沒設定、還沒探測、探測正常都不算。邊界兩側各一例。</summary>
    [Theory]
    [InlineData(AiProbeStatus.Failed, true)]
    [InlineData(AiProbeStatus.Ready, false)]
    [InlineData(AiProbeStatus.NotConfigured, false)]
    [InlineData(AiProbeStatus.NotProbed, false)]
    public void AI探測結果只有Failed會進整體狀態原因(AiProbeStatus status, bool expectReason)
    {
        var probe = new FakeAiProbeService { LatestResult = new AiProbeResult(status == AiProbeStatus.Ready, status, "x", DateTime.UtcNow) };

        var detail = NewService(aiProbe: probe).GetDetail();

        Assert.Equal(expectReason, detail.StatusReasons.Any(r => r.Key == HealthAttentionKeys.AiUnreachable));
        Assert.Equal(status.ToString(), detail.AiProbeStatus);
        if (expectReason) Assert.Equal(HealthStatuses.Degraded, detail.Status);
    }

    [Fact]
    public void 交辦通知佇列剛好塞滿_沒有丟棄_整體狀態正常()
    {
        var store = new WorkOrderOutboxStore(
            _backend.Blob(WorkOrderOutboxStore.BlobKey), null, new WorkOrderOutboxDropStore(_backend.Blob(WorkOrderOutboxDropStore.BlobKey)));

        var dropped = store.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(OutboxItem).ToList());

        Assert.Equal(0, dropped);
        var detail = NewService().GetDetail();
        Assert.DoesNotContain(detail.StatusReasons, r => r.Key == HealthAttentionKeys.OutboxDropped);
        Assert.Equal(HealthStatuses.Ok, detail.Status);
    }

    [Fact]
    public void 交辦通知佇列塞滿再加一筆_丟棄最舊一筆並讓整體狀態需要注意_超過七天後恢復()
    {
        var drops = new WorkOrderOutboxDropStore(_backend.Blob(WorkOrderOutboxDropStore.BlobKey));
        var store = new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey), null, drops);
        store.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(OutboxItem).ToList());

        var dropped = store.Enqueue(OutboxItem(WorkOrderOutboxStore.MaxItems + 1));

        Assert.Equal(1, dropped);
        var detail = NewService().GetDetail();
        Assert.Equal(HealthStatuses.Degraded, detail.Status);
        var reason = Assert.Single(detail.StatusReasons, r => r.Key == HealthAttentionKeys.OutboxDropped);
        Assert.Contains("丟棄了 1 筆", reason.Text);

        // 把最近一次丟棄時間撥到八天前：條件消失，狀態恢復
        drops.Update(s => s.LastDroppedAt = DateTime.Now.AddDays(-8));
        var later = NewService().GetDetail();
        Assert.DoesNotContain(later.StatusReasons, r => r.Key == HealthAttentionKeys.OutboxDropped);
        Assert.Equal(HealthStatuses.Ok, later.Status);
    }

    [Fact]
    public void 全新站台_整體狀態正常且沒有原因()
    {
        var detail = NewService().GetDetail();

        Assert.Equal(HealthStatuses.Ok, detail.Status);
        Assert.Empty(detail.StatusReasons);
    }

    [Fact]
    public void PRTG同步警告_讓整體狀態需要注意_清掉警告後恢復()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgEnabled = true);
        var syncStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        syncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.Warnings = new List<PrtgSyncWarning>
            {
                PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "範圍內的感測器全部回空"),
                PrtgSyncWarning.Create(PrtgSyncWarning.MappedDevicesPurged, "已對應主機的裝置被清除")
            };
        });

        var detail = NewService().GetDetail();

        Assert.Equal(HealthStatuses.Degraded, detail.Status);
        Assert.Contains(detail.StatusReasons, r => r.Key == HealthAttentionKeys.PrtgSensorsEmpty);
        Assert.Contains(detail.StatusReasons, r => r.Key == HealthAttentionKeys.PrtgDevicesPurged);
        Assert.Equal(2, detail.PrtgSyncWarnings.Count);

        syncStore.Update(s => s.Warnings = new List<PrtgSyncWarning>());
        var cleared = NewService().GetDetail();
        Assert.Equal(HealthStatuses.Ok, cleared.Status);
        Assert.Empty(cleared.PrtgSyncWarnings);
    }

    [Fact]
    public void GetAttention_三十秒內第二次不重算_過期後重算()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgEnabled = true);
        var syncStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var service = NewService(clock);

        Assert.Equal(HealthStatuses.Ok, service.GetAttention().Status);

        // 診斷來源改變後，快取存活期內仍回前一份（沒有重算）
        syncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.Warnings = new List<PrtgSyncWarning> { PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "範圍內的感測器全部回空") };
        });
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(HealthStatuses.Ok, service.GetAttention().Status);

        // 過期後重算，看到新狀態
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HealthStatuses.Degraded, service.GetAttention().Status);
    }

    [Fact]
    public void 健康頁算出新結果後_告示列快取同步更新_不再掛著舊警示()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgEnabled = true);
        var syncStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        syncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.Warnings = new List<PrtgSyncWarning> { PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "範圍內的感測器全部回空") };
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var service = NewService(clock);
        Assert.Equal(HealthStatuses.Degraded, service.GetAttention().Status);

        // 問題排除後使用者開健康頁：健康頁已正常，告示列（同一個行程內的快取）不能再掛 30 秒
        syncStore.Update(s => s.Warnings = new List<PrtgSyncWarning>());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HealthStatuses.Ok, service.GetDetail().Status);

        var attention = service.GetAttention();
        Assert.Equal(HealthStatuses.Ok, attention.Status);
        Assert.Empty(attention.Reasons);
    }

    [Fact]
    public void PRTG未啟用_舊的同步警告不影響整體狀態()
    {
        new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)).Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Warnings = new List<PrtgSyncWarning> { PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "x") };
        });

        Assert.Equal(HealthStatuses.Ok, NewService().GetDetail().Status);
    }

    [Fact]
    public void 保留清除失敗_讓整體狀態需要注意_下一趟全部成功後恢復()
    {
        // 先讓 schema 建立，再拿掉第一段（歷史紀錄）用的表，使該段擲例外
        _backend.RecordStore().Prune(30);
        string createSql;
        using (var ctx = _backend.CreateContext())
        {
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='lf_daily_records'";
            createSql = (string)read.ExecuteScalar()!;
            using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE lf_daily_records";
            drop.ExecuteNonQuery();
        }

        var failed = RetentionPruner.Run(_backend, new RetentionOptions(), new NullRunConsole(), batchRunStore: null);
        Assert.Contains("歷史紀錄", failed.FailedSegments);

        var detail = NewService().GetDetail();
        Assert.Equal(HealthStatuses.Degraded, detail.Status);
        var reason = Assert.Single(detail.StatusReasons, r => r.Key == HealthAttentionKeys.Retention);
        Assert.Contains("歷史紀錄", reason.Text);
        Assert.Contains("歷史紀錄", detail.RetentionFailedSegments);

        // 導覽告示列的精簡結果是同一份判定
        var attention = NewService().GetAttention();
        Assert.Equal(detail.Status, attention.Status);
        Assert.Equal(detail.StatusReasons.Select(r => r.Key), attention.Reasons.Select(r => r.Key));

        // 還原表，下一趟全部成功 → 失敗記錄被清掉、整體狀態恢復
        using (var ctx = _backend.CreateContext())
        {
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }
        var ok = RetentionPruner.Run(_backend, new RetentionOptions(), new NullRunConsole(), batchRunStore: null);
        Assert.True(ok.Succeeded, ok.Note);
        Assert.Null(RetentionPruner.LastFailure(_backend));
        Assert.Equal(HealthStatuses.Ok, NewService().GetDetail().Status);
    }

    private sealed class NullRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}

/// <summary>C-長度：PRTG 警告文字含外部字串（裝置名稱），寫入前一律截斷，超長不擲例外。</summary>
public class PrtgSyncWarningLengthTests
{
    [Fact]
    public void 警告文字超過上限_被截斷且不擲例外()
    {
        var warning = PrtgSyncWarning.Create(PrtgSyncWarning.MappedDevicesPurged, new string('名', 100_000));

        Assert.Equal(PrtgSyncWarning.MaxMessageLength, warning.Message.Length);
        Assert.EndsWith("…", warning.Message);
    }

    [Fact]
    public void 未超過上限的文字原樣保留()
    {
        Assert.Equal("短文字", PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "短文字").Message);
        Assert.Equal(string.Empty, PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, null!).Message);
    }

    [Fact]
    public void 狀態複製時警告一併複製_且是獨立的一份()
    {
        var source = new PrtgStructureSyncStatus { Warnings = { PrtgSyncWarning.Create(PrtgSyncWarning.SensorsAllEmpty, "a") } };
        var target = new PrtgStructureSyncStatus();

        target.CopyFrom(source);
        source.Warnings[0].Message = "changed";

        Assert.Equal("a", Assert.Single(target.Warnings).Message);
    }
}
