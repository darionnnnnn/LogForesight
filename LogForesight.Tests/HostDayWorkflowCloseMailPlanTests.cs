using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 1（1-1）：<see cref="HostDayWorkflowService.CloseMailPlanIfChanged"/> 與逐步呼叫
/// <c>ReplaceMailPlan</c>＋<c>CloseMailPlan</c> 的最終狀態逐欄相同；已是目標狀態或版本不符時不寫入。
/// </summary>
public sealed class HostDayWorkflowCloseMailPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-close-plan-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostDayWorkflowService _service;
    private readonly DateTime _day = DateTime.Today.AddDays(-1);

    public HostDayWorkflowCloseMailPlanTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);
        _service = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private long Writes(long hostId) => _backend.Blob($"workflow_{hostId}_{_day:yyyyMMdd}").ReadVersion();

    private static readonly Regex Timestamp = new("\"\\d{4}-\\d{2}-\\d{2}T[0-9:.+\\-Z]+\"", RegexOptions.Compiled);

    private string StateJson(long hostId) => Timestamp.Replace(
        System.Text.Json.JsonSerializer.Serialize(_service.Get(hostId, _day), LfJsonOptions.Pretty), "\"T\"")
        .Replace($"\"HostId\": {hostId},", "\"HostId\": H,").Replace("\"h" + hostId + "\"", "\"N\"")
        .Replace($"\"ParentRecordId\": {700 + hostId},", "\"ParentRecordId\": P,");

    private HostDayWorkflowVersion NewDay(long hostId, bool withSummaryPlan)
    {
        _service.ParentSucceeded(hostId, "h" + hostId, _day, "run", 1, "parent", false, false, parentRecordId: 700 + hostId);
        var version = _service.CaptureDeliveryVersion(hostId, _day, 700 + hostId)!;
        if (withSummaryPlan)
            _service.ReplaceMailPlan(hostId, _day, "summary", new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["summary:x"] = new[] { "part-1" }
            }, version);
        return version;
    }

    [Theory]
    [InlineData(false, new string[0], "below-threshold-or-not-required")]
    [InlineData(true, new[] { "summary" }, "recipient-plan-evaluated")]
    [InlineData(true, new[] { "urgent" }, "recipient-plan-evaluated")]
    [InlineData(true, new[] { "summary", "urgent" }, "recipient-plan-evaluated")]
    [InlineData(true, new string[0], "disabled")]
    public void 合併方法與逐步呼叫的最終狀態逐欄相同_且第二次呼叫不寫入(bool withSummaryPlan, string[] lanes, string reason)
    {
        var merged = NewDay(1, withSummaryPlan);
        var legacy = NewDay(2, withSummaryPlan);

        _service.CloseMailPlanIfChanged(1, _day, merged, lanes, reason);
        var empty = new Dictionary<string, IReadOnlyCollection<string>>();
        if (!lanes.Contains("summary")) _service.ReplaceMailPlan(2, _day, "summary", empty, legacy);
        if (!lanes.Contains("urgent")) _service.ReplaceMailPlan(2, _day, "urgent", empty, legacy);
        _service.CloseMailPlan(2, _day, legacy, lanes, reason);

        Assert.Equal(StateJson(2), StateJson(1));

        var before = Writes(1);
        _service.CloseMailPlanIfChanged(1, _day, merged, lanes, reason);
        Assert.Equal(before, Writes(1));
        Assert.Equal(StateJson(2), StateJson(1));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("parent")]
    public void 版本不符_新方法不寫入也不改任何東西(string mismatch)
    {
        var version = NewDay(5, true);
        var before = Writes(5);
        var json = StateJson(5);
        var stale = mismatch == "decision"
            ? version with { DecisionVersion = version.DecisionVersion + 1 }
            : version with { ParentRecordId = version.ParentRecordId + 1 };

        _service.CloseMailPlanIfChanged(5, _day, stale, ["summary"], "recipient-plan-evaluated");

        Assert.Equal(before, Writes(5));
        Assert.Equal(json, StateJson(5));
    }
}
