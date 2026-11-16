using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 超過單頁上限的主機日改由恢復服務逐列讀完整內容恢復（第 75 輪 I-1／I-2）：不再永遠停在恢復等待
/// （等待會把主機日排除在通知與 PRTG 判定之外）；超過單列上限的才維持等待並列為需要人看。
/// 全部走真的 SQLite 紀錄層，內容長度以補字的方式精確控制在邊界兩側。
/// </summary>
public sealed class HostDayWorkflowOversizedRecoveryTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 10, 5);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-oversized-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostDayWorkflowService _workflow;
    private int _baseLength = -1;

    public HostDayWorkflowOversizedRecoveryTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        _workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task 單頁上限邊界兩側_都恢復而不留在等待()
    {
        var atLimit = AppendSized(101, WorkflowRecoveryPage.MaximumPayloadBytes);
        var overLimit = AppendSized(102, WorkflowRecoveryPage.MaximumPayloadBytes + 1);
        Prepare(atLimit);
        Prepare(overLimit);
        Assert.Contains(_backend.RecordStore().QueryWorkflowRecoveryPage(Day, Day, 0, 10).WaitingHostDays,
            row => row.RecordId == overLimit.RecordId); // 前提：多一個字元的那列在分頁讀取裡確實歸為等待

        var result = await Service().RunSliceAsync(CancellationToken.None, Day.AddDays(1));

        Assert.Equal(0, result.WaitingRows);
        foreach (var record in new[] { atLimit, overLimit })
        {
            var state = _workflow.Get(record.HostId, Day)!;
            Assert.False(state.IsRecoveryWaiting, $"主機 {record.HostId} 不應留在恢復等待");
            Assert.Equal(WorkflowLegState.Succeeded, state.Prtg);
            Assert.False(_workflow.IsRecoveryWaiting(record.HostId, Day, record.RecordId));
        }
    }

    [Fact]
    public async Task 單列上限邊界兩側_超過的維持等待並列為需要人看()
    {
        var atCap = AppendSized(201, WorkflowRecoveryPage.MaximumOversizedPayloadBytes);
        var overCap = AppendSized(202, WorkflowRecoveryPage.MaximumOversizedPayloadBytes + 1);
        Prepare(atCap);
        Prepare(overCap, prepareFromPage: false);
        var service = Service();

        await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));

        Assert.False(_workflow.Get(atCap.HostId, Day)!.IsRecoveryWaiting);
        Assert.True(_workflow.Get(overCap.HostId, Day)!.IsRecoveryWaiting);
        var summary = service.GetWaitingSummary();
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(1, summary.NeedsAttention);
    }

    [Fact]
    public async Task 既有的恢復等待_逐列恢復後解除並還原PRTG_不重設寄送()
    {
        var record = AppendSized(301, WorkflowRecoveryPage.MaximumPayloadBytes + 500);
        Prepare(record);
        var revision = Revision(record.RecordId);
        Assert.True(_workflow.MarkRecoveryWaiting(new WorkflowRecoveryWaitingHostDay(record.RecordId, record.HostId, Day,
            "recovery-payload-over-limit", WorkflowRecoveryPage.MaximumPayloadBytes + 500, revision, DateTime.UtcNow)));
        var before = _workflow.Get(record.HostId, Day)!;
        Assert.True(before.IsRecoveryWaiting);
        Assert.Equal(WorkflowLegState.Waiting, before.Prtg);

        await Service().RunSliceAsync(CancellationToken.None, Day.AddDays(1));

        var after = _workflow.Get(record.HostId, Day)!;
        Assert.False(after.IsRecoveryWaiting);
        Assert.Equal(WorkflowLegState.Succeeded, after.Prtg);
        // 指紋相同就不推進決策版本、不重設寄送：已寄過通知的主機日不會因恢復再寄一次
        Assert.Equal(before.DecisionVersion, after.DecisionVersion);
        Assert.Equal(before.DecisionFingerprint, after.DecisionFingerprint);
        Assert.Equal(before.MailPlanClosed, after.MailPlanClosed);
        Assert.Equal(before.MailIntents.Length, after.MailIntents.Length);
    }

    [Fact]
    public async Task 每頁最多逐列恢復四列_其餘下一輪處理_游標照常前進()
    {
        var records = Enumerable.Range(0, 6)
            .Select(index => AppendSized(400 + index, WorkflowRecoveryPage.MaximumPayloadBytes + 10 + index))
            .ToList();
        records.ForEach(record => Prepare(record));
        var service = Service();

        var first = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(6 - HostDayWorkflowRecoveryHostedService.MaxOversizedRecoveriesPerPage, first.WaitingRows);
        // 延後的列不標成恢復等待（標了會把它排除在通知之外），只排進下一輪優先處理
        Assert.Equal(0, records.Count(record => _workflow.Get(record.HostId, Day)!.IsRecoveryWaiting));
        Assert.Equal(2, service.GetWaitingSummary().Waiting);
        Assert.Equal(0, service.GetWaitingSummary().NeedsAttention); // 排隊等逐列恢復的不算需要人看

        var second = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(0, second.WaitingRows);
        Assert.Equal(0, service.GetWaitingSummary().Waiting);
        Assert.All(records, record => Assert.False(_workflow.Get(record.HostId, Day)!.IsRecoveryWaiting));

        // 第三輪：全部都已在同一個寫入版本恢復過，不再重讀也不再延後
        var third = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(0, third.WaitingRows);
    }

    [Fact]
    public async Task 內容無效的列_連跑三輪等待數維持一列_不重複累加()
    {
        var record = AppendSized(501, 2_000);
        Prepare(record);
        using (var context = _backend.CreateContext())
        {
            var row = context.DailyRecords.Single(value => value.RecordId == record.RecordId);
            row.ContentJson = "{\"not\": valid json";
            context.SaveChanges();
        }
        var service = Service();

        for (var round = 0; round < 3; round++)
        {
            var result = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
            Assert.Equal(1, result.CumulativeWaitingRows);
        }
        Assert.True(_workflow.Get(record.HostId, Day)!.IsRecoveryWaiting); // 真的壞資料仍等待
        Assert.Equal(1, service.GetWaitingSummary().NeedsAttention);
    }

    [Fact]
    public async Task 逐列讀不到的超大列_佔每頁額度_同版本不再重讀並列為需要檢查()
    {
        // 五列超過單頁上限但內容無效（逐列讀也讀不出來），最後一列正常：額度若只算成功，五列每輪都會被重讀
        var broken = Enumerable.Range(0, 5)
            .Select(index => AppendSized(600 + index, WorkflowRecoveryPage.MaximumPayloadBytes + 100 + index))
            .ToList();
        using (var context = _backend.CreateContext())
        {
            foreach (var record in broken)
            {
                var row = context.DailyRecords.Single(value => value.RecordId == record.RecordId);
                row.ContentJson = "{\"broken\": " + new string('x', WorkflowRecoveryPage.MaximumPayloadBytes + 100);
            }
            context.SaveChanges();
        }
        var valid = AppendSized(610, WorkflowRecoveryPage.MaximumPayloadBytes + 50);
        Prepare(valid);
        var service = Service();

        var first = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        // 四次嘗試都花在壞列上：第五個壞列與正常列延後
        Assert.Equal(6, first.WaitingRows);
        Assert.Equal(4, service.GetWaitingSummary().NeedsAttention);

        var second = await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        // 延後的優先：第五個壞列與正常列各試一次，正常列恢復；已確認讀不到的四列同版本不再重讀
        Assert.Equal(5, second.WaitingRows);
        Assert.Equal(5, service.GetWaitingSummary().NeedsAttention);
        Assert.False(_workflow.Get(valid.HostId, Day)!.IsRecoveryWaiting);
    }

    [Fact]
    public async Task 記憶體中的等待列_超過兩天且之後走完一整趟深層巡掃才清掉()
    {
        var overCap = AppendSized(701, WorkflowRecoveryPage.MaximumOversizedPayloadBytes + 1);
        Prepare(overCap, prepareFromPage: false);
        var service = Service();
        await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(1, service.GetWaitingSummary().Waiting);
        var now = DateTime.UtcNow;
        var past = now + HostDayWorkflowRecoveryHostedService.InMemoryRetention + TimeSpan.FromMinutes(1);

        // 列數多的站台一趟深層巡掃會超過兩天：還沒有整趟走完就不丟，否則「需要檢查」先歸零、巡到又出現
        service.PruneInMemory(past);
        Assert.Equal(1, service.GetWaitingSummary().Waiting);
        service.NoteDeepPassCompleted(now.AddHours(1));   // 這一趟（含看到它的那一頁）走完
        service.PruneInMemory(past);
        Assert.Equal(1, service.GetWaitingSummary().Waiting);
        service.NoteDeepPassCompleted(now.AddHours(2));   // 之後又整整走完一趟都沒再看到
        service.PruneInMemory(now.AddDays(1));
        Assert.Equal(1, service.GetWaitingSummary().Waiting);   // 兩天內仍保留
        service.PruneInMemory(past);
        Assert.Equal(0, service.GetWaitingSummary().Waiting);
    }

    [Fact]
    public async Task 已恢復的超大列_PRTG啟用狀態變了要重新對帳()
    {
        var record = AppendSized(801, WorkflowRecoveryPage.MaximumPayloadBytes + 500);
        Prepare(record);
        var settings = new FakeSystemSettingsStore();
        settings.Update(value => { value.RetentionDays = 10; value.PrtgEnabled = true; });
        var service = Service(settings);

        await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(WorkflowLegState.Succeeded, _workflow.Get(record.HostId, Day)!.Prtg);

        // 一般列每輪都以最新啟用狀態對帳；超大列同版本恢復過就跳過的話，PRTG 關閉後它會一直停在舊狀態
        settings.Update(value => value.PrtgEnabled = false);
        await service.RunSliceAsync(CancellationToken.None, Day.AddDays(1));
        Assert.Equal(WorkflowLegState.Disabled, _workflow.Get(record.HostId, Day)!.Prtg);
    }

    /// <summary>附加一筆主機日，內容 JSON 長度（字元）恰為 <paramref name="contentChars"/>（以標題補 ASCII 字元控制）。</summary>
    private DailyAnalysisRecord AppendSized(long hostId, int contentChars)
    {
        if (_baseLength < 0)
        {
            // 量基準長度：同一形狀、標題為空的紀錄寫在範圍外的日期，不影響被測頁面
            var probeDay = Day.AddYears(-1);
            _backend.RecordStore().Append(NewRecord(9_999, probeDay, string.Empty));
            using var context = _backend.CreateContext();
            _baseLength = context.DailyRecords.Where(row => row.RecordDate == probeDay).Select(row => row.ContentJson.Length).Single();
        }
        // 主機編號位數不同會改變長度：以實際寫入後的長度校正一次
        var padding = new string('x', Math.Max(0, contentChars - _baseLength));
        _backend.RecordStore().Append(NewRecord(hostId, Day, padding));
        using (var context = _backend.CreateContext())
        {
            var row = context.DailyRecords.Single(value => value.HostId == hostId && value.RecordDate == Day);
            var diff = contentChars - row.ContentJson.Length;
            if (diff != 0)
            {
                row.ContentJson = row.ContentJson.Replace(padding, new string('x', padding.Length + diff));
                context.SaveChanges();
            }
            Assert.Equal(contentChars, context.DailyRecords.Single(value => value.RecordId == row.RecordId).ContentJson.Length);
            return new DailyAnalysisRecord { RecordId = row.RecordId, HostId = hostId, Date = Day, Host = row.HostName };
        }
    }

    private static DailyAnalysisRecord NewRecord(long hostId, DateTime day, string headline) => new()
    {
        HostId = hostId, Host = $"oversized-{hostId}", Date = day, RiskLevel = RiskLevels.High,
        LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
        LatestNetiqAttemptAtUtc = new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc), AuditEventCount = 3,
        Headline = headline
    };

    /// <summary>
    /// 以恢復服務實際會看到的反序列化結果建立「父紀錄成功、PRTG 已完成」的工作流狀態。
    /// 超過單列上限的列讀不出來，改用原始寫入值建立（它本來就不會被恢復）。
    /// </summary>
    private void Prepare(DailyAnalysisRecord stub, bool prepareFromPage = true)
    {
        DailyAnalysisRecord authoritative;
        if (prepareFromPage)
        {
            var single = _backend.RecordStore().QueryWorkflowRecoveryRecord(stub.RecordId, WorkflowRecoveryPage.MaximumOversizedPayloadBytes);
            authoritative = Assert.Single(single.Records);
        }
        else
        {
            authoritative = NewRecord(stub.HostId, Day, string.Empty);
            authoritative.RecordId = stub.RecordId;
        }
        _workflow.ParentSucceeded(authoritative.HostId, authoritative.Host, Day, authoritative.LatestNetiqAttemptAtUtc?.ToString("O") ?? "parent",
            authoritative.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(authoritative), prtgEnabled: true, aiEnabled: false,
            parentRecordId: authoritative.RecordId);
        _workflow.SetPrtg(authoritative.HostId, Day, WorkflowLegState.Succeeded, evidenceReady: true);
    }

    private long Revision(long recordId) =>
        _backend.RecordStore().QueryWorkflowRecoveryRecord(recordId, WorkflowRecoveryPage.MaximumOversizedPayloadBytes)
            .CapturedWriteRevisions![recordId];

    private HostDayWorkflowRecoveryHostedService Service(FakeSystemSettingsStore? settings = null)
    {
        if (settings == null)
        {
            settings = new FakeSystemSettingsStore();
            settings.Update(value => { value.RetentionDays = 10; value.PrtgEnabled = true; });
        }
        return new HostDayWorkflowRecoveryHostedService((IAnalysisRecordQuery)_backend.RecordStore(), _backend, settings,
            new FakeWebAi(), _workflow,
            new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 3, 0, 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)),
            NullAiRequestGate.Instance);
    }
}
