using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// K5：問題名稱只有一個出口（<see cref="IssueNames"/>）。Windows、Linux（有無規則）、PRTG 各一例；
/// 對同一個 Windows 事件與 <see cref="LogIssueSignature.SourceEventLabel"/> 逐字相同。
/// </summary>
public class IssueNamesTests
{
    private static readonly IReadOnlyList<KnownIssueRule> NoRules = Array.Empty<KnownIssueRule>();

    [Fact]
    public void Windows事件與SourceEventLabel逐字相同()
    {
        var sig = new LogIssueSignature { LogName = "System", Source = "Service Control Manager", EventId = 7000 };
        Assert.Equal(sig.SourceEventLabel, IssueNames.For("Service Control Manager", 7000));
        Assert.Equal("Service Control Manager EventId 7000", IssueNames.For("Service Control Manager", 7000));
    }

    [Fact]
    public void Linux事件編號為0時不顯示0_規則名稱取得到才附上()
    {
        Assert.Equal("sshd", IssueNames.For("sshd", 0));
        Assert.Equal("sshd", IssueNames.For("sshd", 0, "  "));
        Assert.Equal("sshd（SSH 暴力破解）", IssueNames.For("sshd", 0, "SSH 暴力破解"));
        Assert.DoesNotContain("0", IssueNames.For("sshd", 0));
        // 邊界另一側：事件編號大於 0 時規則名稱不附（Windows 名稱不受規則影響）
        Assert.Equal("disk EventId 153", IssueNames.For("disk", 153, "任何規則"));
    }

    [Fact]
    public void Linux有事件鍵的完整簽章仍走SourceEventLabel_不變()
    {
        var withKey = new LogIssueSignature { LogName = "syslog", Source = "sshd", EventId = 0, EventKey = "builtin-linux-ssh-bruteforce" };
        Assert.Equal("sshd（builtin-linux-ssh-bruteforce）", withKey.SourceEventLabel);
        Assert.Equal(withKey.SourceEventLabel, IssueNames.For("sshd", 0, "builtin-linux-ssh-bruteforce"));
    }

    [Fact]
    public void PRTG聚合層顯示規則而不是事件編號0()
    {
        Assert.Equal("PRTG 持續故障", IssueNames.For("PRTG:down", 0));
        Assert.Equal("PRTG 持續故障", IssueNames.For("PRTG:down", 0, "很長的規則說明句"));   // PRTG 不附規則說明
        Assert.Equal("PRTG custom-x", IssueNames.For("PRTG:custom-x", 0));
        Assert.DoesNotContain(" 0", IssueNames.For("PRTG:down", 0));
    }

    [Fact]
    public void 既有單的名稱顯示時重新產生_來源空白或PRTG才用存的字串()
    {
        Assert.Equal("sshd", IssueNames.Display("sshd", 0, "sshd 0", NoRules));
        Assert.Equal("disk EventId 153", IssueNames.Display("disk", 153, "disk 153", NoRules));
        Assert.Equal("多問題單", IssueNames.Display(null, 0, "多問題單", NoRules));
        Assert.Equal("多問題單", IssueNames.Display("  ", 0, "多問題單", NoRules));
        // PRTG 舊單：單上沒有感測器編號，重新產生會丟資訊，沿用存的可讀字串
        const string stored = "PRTG:down（prtg:down:5:7:x）";
        Assert.Equal(PrtgFindingMapper.DisplayStoredLabel(stored), IssueNames.Display("PRTG:down", 0, stored, NoRules));
        // PRTG 人工建立存的舊寫法讀不出感測器：重新產生
        Assert.Equal("PRTG 持續故障", IssueNames.Display("PRTG:down", 0, "PRTG:down 0", NoRules));
    }

    [Fact]
    public void WorkOrder的顯示名稱用單上的來源與事件_不回寫存的字串()
    {
        var order = new WorkOrder { SourceName = "sshd", EventId = 0, IssueLabel = "sshd 0" };
        Assert.Equal("sshd", order.DisplayLabel(NoRules));
        Assert.Equal("sshd 0", order.IssueLabel);
        var multi = new WorkOrder { SourceName = null, EventId = null, IssueLabel = "3 個問題" };
        Assert.Equal("3 個問題", multi.DisplayLabel(NoRules));
    }
}
