using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// K5：同一個問題在各畫面與通知信輸出相同的名稱。Windows、Linux（恰好一條規則／多條規則）、PRTG 各一例。
/// 依問題聚合的畫面（待派、不再自動派工、問題查詢、排行、主機詳情、報表）都呼叫 <see cref="IssueNames.ForRules"/>；
/// 交辦單清單／單頭、管理員提醒、通知信、稽核都顯示 <see cref="WorkOrder.DisplayLabel"/>（舊單存的字串不影響）；
/// 管理員提醒的派不出去清單用 <see cref="IssueNames.Display"/>（結構化來源與事件編號）。
/// 這裡逐一比對這三個出口，另以真正的 <see cref="DismissedIssueService"/> 驗一個畫面。
/// </summary>
public class IssueNameParityTests
{
    private static readonly IReadOnlyList<KnownIssueRule> Rules = KnownIssueSeed.CreateRules();

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "disk", 153, "disk EventId 153" },                                   // Windows
        new object[] { "cron", 0, "cron（排程任務（cron）執行失敗）" },                      // Linux，恰好一條規則命中
        new object[] { "sshd", 0, "sshd" },                                                  // Linux，多條規則命中：不猜是哪一條
        new object[] { "unknown-daemon", 0, "unknown-daemon" },                              // Linux，沒有規則
        new object[] { "PRTG:down", 0, "PRTG 持續故障" },                                    // PRTG（聚合層沒有感測器編號）
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 聚合畫面_交辦單_提醒_通知信輸出相同(string source, int eventId, string expected)
    {
        var aggregate = IssueNames.ForRules(Rules, source, eventId);
        // 舊單存的是舊寫法（或另一條路徑存的完整簽章名稱），顯示時一律重新產生
        var order = new WorkOrder { SourceName = source, EventId = eventId, IssueLabel = $"{source} {eventId}" };
        var nightlyStored = new WorkOrder { SourceName = source, EventId = eventId, IssueLabel = "夜間派工存的另一種寫法" };
        var unassignable = IssueNames.Display(source, eventId, "舊標籤", Rules);

        Assert.Equal(expected, aggregate);
        Assert.Equal(expected, order.DisplayLabel(Rules));
        Assert.Equal(expected, unassignable);
        if (!source.StartsWith("PRTG:", StringComparison.Ordinal))
            Assert.Equal(expected, nightlyStored.DisplayLabel(Rules));
    }

    [Fact]
    public void 事件編號0不出現在任何名稱()
    {
        foreach (var source in new[] { "cron", "sshd", "unknown-daemon", "PRTG:down" })
            Assert.DoesNotMatch(@"\b0\b", IssueNames.ForRules(Rules, source, 0));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void 不再自動派工清單的問題名稱與共用出口相同(string source, int eventId, string expected)
    {
        var now = new DateTime(2026, 11, 10, 9, 0, 0);
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "HOST-A" });
        var cases = new FakeIssueCaseStore();
        cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = "HOST-A",
            IssueKey = IssueSignatureKey.For("System", source, eventId, LogEntryType.Error),
            IssueLabel = $"{source} {eventId}", Status = IssueHandlingStatuses.WontFix,
            CreatedAt = now.AddDays(-5), UpdatedAt = now.AddDays(-2), FirstLinkedDate = now.Date.AddDays(-6),
            LastLinkedDate = now.Date.AddDays(-3), ClosedAt = now.AddDays(-2), SourceName = source, EventId = eventId
        });
        var service = new DismissedIssueService(
            cases, new FakeHandlingStore(), hosts, new FakeUserStore(), new FakeRuleStore(),
            new FixedVisibilityService(hosts.GetAll().Select(h => h.HostId)), new RecordingAuditService(),
            new UserDisplayNameService(new FakeSystemSettingsStore()));

        var group = service.GetList(1).Groups.Single();

        Assert.Equal(expected, group.IssueLabel);
    }
}
