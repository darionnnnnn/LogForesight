using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 1（1-4）：沒有問題的主機日，案件意圖與結果合成一次工作流更新。
/// 寫入次數以工作流 blob 版本前後相減取得；狀態與「逐步呼叫 RecordCaseIntents＋RecordCaseResults」逐欄相同。
/// </summary>
public sealed class HostDayPostProcessorWorkflowWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-attach-wr-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostDayWorkflowService _workflow;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly DateTime _day = DateTime.Today.AddDays(-1);

    public HostDayPostProcessorWorkflowWriteTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);
        _workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        _orders = new FakeWorkOrderStore(_cases);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, _owners);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private NightlyDispatch Dispatch()
    {
        var pool = new DispatchCandidatePool { ByUserId = new Dictionary<long, DispatchCandidate>() };
        var ctx = DispatchContext.Build(pool, _owners, _orders, _cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now);
        var coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, _caseCoordinator, _handlingLog, _hosts);
        return new NightlyDispatch(coordinator, ctx, _hosts);
    }

    private static LogIssueSignature Issue() => new()
    {
        LogName = "System", Source = "Disk", EventId = 153, EntryType = LogEntryType.Error, Severity = IssueSeverity.High
    };

    private WebHost NewHost(string name)
    {
        var host = _hosts.Upsert(new WebHost { HostName = name });
        _workflow.ParentSucceeded(host.HostId, host.HostName, _day, "run", 1, "parent", false, false, parentRecordId: 123);
        return host;
    }

    private long Writes(WebHost host) => _backend.Blob($"workflow_{host.HostId}_{_day:yyyyMMdd}").ReadVersion();

    private static readonly Regex Timestamp = new("\"\\d{4}-\\d{2}-\\d{2}T[0-9:.+\\-Z]+\"", RegexOptions.Compiled);

    private string StateJson(WebHost host) => Timestamp.Replace(
        System.Text.Json.JsonSerializer.Serialize(_workflow.Get(host.HostId, _day), LfJsonOptions.Pretty), "\"T\"")
        .Replace($"\"HostId\": {host.HostId},", "\"HostId\": H,");

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    [Theory]
    [InlineData("")]
    [InlineData("[NetIQ] ")]
    [InlineData("[PRTG] ")]
    public void 空主機日_案件意圖與結果合成一次更新_狀態與逐步呼叫逐欄相同_寫入次數由2降為1(string logContext)
    {
        var viaAttach = NewHost("attach-" + logContext.Trim('[', ']', ' '));
        var viaLegacy = NewHost("legacy-" + logContext.Trim('[', ']', ' '));
        var attachBefore = Writes(viaAttach);
        var legacyBefore = Writes(viaLegacy);

        var ok = HostDayPostProcessor.AttachCase(_caseCoordinator, Dispatch(), viaAttach.HostName, _day, new List<LogIssueSignature>(),
            logContext, _workflow, viaAttach.HostId, 123, clock: TimeProvider.System);

        // 改動前的做法：兩次獨立更新
        var version = _workflow.CaptureDeliveryVersion(viaLegacy.HostId, _day, 123);
        _workflow.RecordCaseIntents(viaLegacy.HostId, _day, [], version);
        _workflow.RecordCaseResults(viaLegacy.HostId, _day, [], [], [], version);

        Assert.True(ok);
        Assert.Equal(StateJson(viaLegacy).Replace(viaLegacy.HostName, "N"), StateJson(viaAttach).Replace(viaAttach.HostName, "N"));
        Assert.True(_workflow.Get(viaAttach.HostId, _day)!.CasePlanClosed);
        Assert.Equal(WorkflowLegState.Succeeded, _workflow.Get(viaAttach.HostId, _day)!.CaseState);
        Assert.Equal(2, Writes(viaLegacy) - legacyBefore);
        Assert.Equal(1, Writes(viaAttach) - attachBefore);
    }

    /// <summary>有問題的主機日：工作流狀態與寫入次數與改動前完全相同（改動前於 HEAD 實測：狀態摘要與寫入次數見常數）。</summary>
    [Fact]
    public void 有問題的主機日_工作流狀態與寫入次數與改動前相同()
    {
        var host = NewHost("with-issue");
        var before = Writes(host);
        var ok = HostDayPostProcessor.AttachCase(_caseCoordinator, Dispatch(), host.HostName, _day, [Issue()],
            "", _workflow, host.HostId, 123, clock: TimeProvider.System);
        var writes = Writes(host) - before;
        var digest = Digest(StateJson(host).Replace(host.HostName, "N"));

        Assert.Equal(GoldenIssueDayDigest, digest);
        Assert.Equal(GoldenIssueDayWrites, writes);
        Assert.Equal(GoldenIssueDayReturn, ok);
    }

    private const string GoldenIssueDayDigest = "899627BBB6D3C603";
    private const long GoldenIssueDayWrites = 3;
    private const bool GoldenIssueDayReturn = true;

    [Fact]
    public void 有問題的主機日_工作流寫入失敗時AttachCase仍回傳false()
    {
        var host = NewHost("oversize");
        var key = $"workflow_{host.HostId}_{_day:yyyyMMdd}";
        // 讓既有狀態剛好吃滿大小上限（讀得進來）：任何讓內容變長的更新都會在寫入時擲 InvalidDataException
        _backend.Blob(key).Mutate(raw =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(raw!)!.AsObject();
            node["AiFailure"] = "";
            var baseLength = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Length;
            node["AiFailure"] = new string('x', 64 * 1024 - baseLength);
            var padded = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Assert.Equal(64 * 1024, padded.Length);
            return (padded, 0);
        });

        var ok = HostDayPostProcessor.AttachCase(_caseCoordinator, Dispatch(), host.HostName, _day, [Issue()],
            "", _workflow, host.HostId, 123, clock: TimeProvider.System);

        Assert.False(ok);
    }
}
