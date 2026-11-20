using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 頻道政策「資訊等級也收」明列清單（F2）與 1102 守門（F3）。決策點是
/// <see cref="EventLogService.ShouldInclude"/>（classic 與 EventLogReader 兩條掃描路徑共用），
/// 以假的事件（提供者名、等級、編號）直接餵它，不需要真的 Windows 事件記錄。
/// 讀 KnownIssueCatalog 共用靜態狀態，納入序列執行的 collection。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class ChannelInformationalAllowListTests : IDisposable
{
    public ChannelInformationalAllowListTests() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    private static bool Include(ChannelPolicy policy, LogEntryType type, string source, int eventId) =>
        EventLogService.ShouldInclude(policy, type, source, eventId, null);

    // ── F2：明列清單 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("System", "Service Control Manager", 7045, "builtin-security-new-service-7045")]
    [InlineData("Application", "Service Control Manager", 7045, "builtin-security-new-service-7045")]
    [InlineData("System", "EventLog", 104, "builtin-security-system-log-cleared-104")]
    [InlineData("System", "Microsoft-Windows-Eventlog", 104, "builtin-security-system-log-cleared-104")]
    public void 清單內的資訊等級事件被收錄並能觸發規則(string channel, string source, int eventId, string expectedRuleId)
    {
        var policy = ChannelCatalog.Resolve(channel);

        Assert.True(Include(policy, LogEntryType.Information, source, eventId));

        var rule = KnownIssueCatalog.FindRule(source, eventId);
        Assert.NotNull(rule);
        Assert.Equal(expectedRuleId, rule!.Id);
    }

    [Fact]
    public void 清單內資訊等級事件經聚合後帶規則分類()
    {
        var entry = new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(2),
            LogName = "System",
            Source = "Service Control Manager",
            EventId = 7045,
            EntryType = LogEntryType.Information,
            Message = "A service was installed in the system."
        };
        Assert.True(Include(ChannelCatalog.Resolve("System"), entry.EntryType, entry.Source, entry.EventId));

        var signature = Assert.Single(LogAggregator.Aggregate(new List<EventLogEntryData> { entry }));

        Assert.Equal("builtin-security-new-service-7045", signature.RuleId);
        Assert.Equal(IssueCategory.Security, signature.Category);
    }

    [Theory]
    [InlineData("Service Control Manager", 7036)]  // 同提供者、不同編號（服務狀態變更，量大的正常訊息）
    [InlineData("Ntfs", 98)]                        // 規則涵蓋的編號，但資訊等級版本是健康訊息，不得整個編號收
    [InlineData("Ntfs", 130)]
    [InlineData("Some Other Provider", 7045)]       // 同編號、不同提供者
    [InlineData("Some Other Provider", 104)]
    public void 清單外的資訊等級事件仍不收(string source, int eventId)
    {
        Assert.False(Include(ChannelCatalog.Resolve("System"), LogEntryType.Information, source, eventId));
        Assert.False(Include(ChannelCatalog.Resolve("Application"), LogEntryType.Information, source, eventId));
    }

    [Fact]
    public void 清單為空時行為與純政策完全相同()
    {
        var empty = new ChannelPolicy { ChannelName = "System", Kind = ChannelInclusionKind.ErrorWarningOnly };

        // 資訊等級一律不收（含 7045／104）
        Assert.False(Include(empty, LogEntryType.Information, "Service Control Manager", 7045));
        Assert.False(Include(empty, LogEntryType.Information, "EventLog", 104));
        // 錯誤、警告、Critical（等級值 0）照收
        Assert.True(Include(empty, LogEntryType.Error, "disk", 7));
        Assert.True(Include(empty, LogEntryType.Warning, "disk", 51));
        Assert.True(Include(empty, (LogEntryType)0, "disk", 1));
    }

    [Fact]
    public void 未知頻道的政策清單為空且資訊等級不收()
    {
        var unknown = ChannelCatalog.Resolve("Some-Custom-Channel");

        Assert.Empty(unknown.InformationalAllowList);
        Assert.False(Include(unknown, LogEntryType.Information, "Service Control Manager", 7045));
    }

    [Fact]
    public void 明列清單初始內容只有7045與104與1102且不是以編號整收()
    {
        Assert.Equal(
            new[] { ("Service Control Manager", 7045), ("EventLog", 104) },
            ChannelCatalog.LegacyInformationalAllowList.Select(e => (e.ProviderPattern, e.EventId)).ToArray());
        Assert.All(ChannelCatalog.SecurityInformationalAllowList, e => Assert.Equal(1102, e.EventId));
    }

    // ── F3：1102 守門 ─────────────────────────────────────────────────

    private static List<KnownIssueRule> SeedWithout1102(Func<KnownIssueRule, bool> remove)
    {
        var rules = KnownIssueSeed.CreateRules();
        var seeded1102 = rules.Where(r => r.Platform == "windows" && r.EventIds.Contains(1102)).ToList();
        // 守門前提：種子確實有兩條 1102 規則（若種子被改成一條，這個守門要跟著重審）
        Assert.Equal(2, seeded1102.Count);
        rules.RemoveAll(r => remove(r));
        return rules;
    }

    [Theory]
    [InlineData("builtin-security-audit-log-cleared-1102")]
    [InlineData("builtin-security-audit-log-cleared-1102-eventlog")]
    public void 刪除任一條1102規則後本機仍收得到1102(string removedRuleId)
    {
        KnownIssueCatalog.Initialize(SeedWithout1102(r => r.Id == removedRuleId));
        var policy = ChannelCatalog.Resolve("Security");

        Assert.True(Include(policy, LogEntryType.SuccessAudit, "Microsoft-Windows-Eventlog", 1102),
            $"刪除規則 {removedRuleId} 後本機不再收 1102——明列清單 SecurityInformationalAllowList 被改壞或移除。");
        Assert.True(Include(policy, LogEntryType.SuccessAudit, "Microsoft-Windows-Security-Auditing", 1102));
    }

    [Fact]
    public void 兩條1102規則都停用後本機仍收得到1102()
    {
        var rules = KnownIssueSeed.CreateRules()
            .Select(r => r.Platform == "windows" && r.EventIds.Contains(1102) ? r.CloneForSeedOverwrite(enabled: false) : r)
            .ToList();
        KnownIssueCatalog.Initialize(rules);

        Assert.DoesNotContain(1102, KnownIssueCatalog.SecurityAuditWatchlist);
        Assert.True(Include(ChannelCatalog.Resolve("Security"), LogEntryType.SuccessAudit, "Microsoft-Windows-Eventlog", 1102));
    }

    [Fact]
    public void 兩條1102規則都刪除後本機仍收得到1102()
    {
        KnownIssueCatalog.Initialize(SeedWithout1102(r => r.EventIds.Contains(1102)));

        Assert.True(Include(ChannelCatalog.Resolve("Security"), LogEntryType.SuccessAudit, "Microsoft-Windows-Eventlog", 1102));
    }

    [Fact]
    public void 其他提供者的1102與同提供者其他編號的成功稽核不被必收清單吃進來()
    {
        KnownIssueCatalog.Initialize(SeedWithout1102(r => r.EventIds.Contains(1102)));
        var policy = ChannelCatalog.Resolve("Security");

        Assert.False(Include(policy, LogEntryType.SuccessAudit, "Some Other Provider", 1102));
        Assert.False(Include(policy, LogEntryType.SuccessAudit, "Microsoft-Windows-Eventlog", 1103));
    }
}
