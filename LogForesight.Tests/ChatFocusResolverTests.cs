using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>詢問 AI 的確定性焦點判定（不呼叫 AI）</summary>
public class ChatFocusResolverTests
{
    private static IssueDto I(string source, int eventId, string severity = "High", string? ruleId = null, string? label = null) => new()
    {
        Source = source, EventId = eventId, Severity = severity, RuleId = ruleId,
        IssueKey = $"System|{source}|{eventId}|1",
        SourceEventLabel = label ?? $"{source} EventId {eventId}"
    };

    private static List<IssueDto> Day() => new()
    {
        I("Microsoft-Windows-Security-Auditing", 4625),
        I("Microsoft-Windows-Security-Auditing", 4735),
        I("Service Control Manager", 7031),
        I("Microsoft-Windows-GroupPolicy", 1058)
    };

    [Fact]
    public void 訊息含兩個編號_焦點恰為那兩個_keyword()
    {
        var (focus, source) = ChatFocusResolver.Resolve(Day(), null, "4625 和 4735 有關聯嗎");
        Assert.Equal(new[] { 4625, 4735 }, focus.Select(f => f.EventId));
        Assert.Equal("keyword", source);
    }

    [Fact]
    public void Source最後一段命中()
    {
        var (focus, source) = ChatFocusResolver.Resolve(Day(), null, "GroupPolicy 的錯誤怎麼辦");
        var only = Assert.Single(focus);
        Assert.Equal("Microsoft-Windows-GroupPolicy", only.Source);
        Assert.Equal("keyword", source);
    }

    [Theory]
    [InlineData("發生了 0 次嗎")]
    [InlineData("共 12 次")]
    [InlineData("14625 是什麼")]
    public void 非編號數字不命中_退回預設(string message)
    {
        var (_, source) = ChatFocusResolver.Resolve(Day(), null, message);
        Assert.Equal("default", source);
    }

    [Fact]
    public void EventId為0的問題不以數字比對()
    {
        var issues = new List<IssueDto> { I("sshd", 0), I("kernel", 100) };
        var (focus, source) = ChatFocusResolver.Resolve(issues, null, "100 次");
        Assert.Equal("keyword", source);
        Assert.Equal("kernel", Assert.Single(focus).Source);
        var (_, s2) = ChatFocusResolver.Resolve(issues, null, "000 次");
        Assert.Equal("default", s2);
    }

    [Fact]
    public void Linux規則Id命中()
    {
        var issues = new List<IssueDto> { I("sshd", 0, ruleId: "ssh-brute-force", label: "sshd（ssh-brute-force）"), I("cron", 0) };
        var (focus, source) = ChatFocusResolver.Resolve(issues, null, "ssh-brute-force 嚴重嗎");
        Assert.Equal("sshd", Assert.Single(focus).Source);
        Assert.Equal("keyword", source);
    }

    [Fact]
    public void focusKeys含不存在的key_忽略不擲例外()
    {
        var (focus, source) = ChatFocusResolver.Resolve(Day(), new[] { "nope|x|1|1", "" }, "隨便問");
        Assert.Equal("default", source);
        Assert.True(focus.Count <= 3);
    }

    [Fact]
    public void focusKeys三個加訊息命中兩個新的_回四個_前三依序_manual()
    {
        var issues = Day();
        issues.Add(I("Disk", 11));
        issues.Add(I("Ntfs", 55));
        var keys = new[] { issues[3].IssueKey, issues[0].IssueKey, issues[2].IssueKey };
        var (focus, source) = ChatFocusResolver.Resolve(issues, keys, "4735 和 Ntfs 與 11");
        Assert.Equal(4, focus.Count);
        Assert.Equal(keys, focus.Take(3).Select(f => f.IssueKey));
        Assert.Equal("manual", source);
    }

    [Fact]
    public void 無指定無命中_取嚴重度前三_不足全取()
    {
        var five = new List<IssueDto>
        {
            I("a", 1, "Low"), I("b", 2, "Critical"), I("c", 3, "Medium"), I("d", 4, "High"), I("e", 5, "Critical")
        };
        var (focus, source) = ChatFocusResolver.Resolve(five, null, "整天怎麼樣");
        Assert.Equal(new[] { "b", "e", "d" }, focus.Select(f => f.Source));
        Assert.Equal("default", source);

        var (two, _) = ChatFocusResolver.Resolve(five.Take(2).ToList(), null, "整天怎麼樣");
        Assert.Equal(2, two.Count);
    }

    [Fact]
    public void 兩位數的事件編號不以數字比對_避免把次數當編號()
    {
        // Kernel-General 12 這類兩位數編號：提問裡的「12 次」不得被當成指向它
        var day = Day();
        day.Add(I("Microsoft-Windows-Kernel-General", 12, "Low"));
        var (focus, source) = ChatFocusResolver.Resolve(day, null, "共 12 次");
        Assert.Equal("default", source);
        Assert.DoesNotContain(focus, f => f.EventId == 12);
    }
}
