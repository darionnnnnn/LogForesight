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
    public void focusKeys三個加訊息命中兩個新的_回四個_最新命中在前_較舊的延續焦點被擠掉()
    {
        var issues = Day();
        issues.Add(I("Disk", 11));
        issues.Add(I("Ntfs", 55));
        var keys = new[] { issues[3].IssueKey, issues[0].IssueKey, issues[2].IssueKey };
        var (focus, source) = ChatFocusResolver.Resolve(issues, keys, "4735 和 Ntfs 與 11");
        // 命中：4735（issues[1]）與 Ntfs（issues[5]）排最前；其後接 focusKeys 前兩個，第三個被上限擠掉
        Assert.Equal(new[] { issues[1].IssueKey, issues[5].IssueKey, keys[0], keys[1] }, focus.Select(f => f.IssueKey));
        Assert.Equal("manual", source);
    }

    [Fact]
    public void 焦點已滿時再問到新的問題_新問題進焦點而不是被靜默丟掉()
    {
        var issues = Day();
        issues.Add(I("Ntfs", 55));
        var full = issues.Take(4).Select(i => i.IssueKey).ToArray();
        var (focus, _) = ChatFocusResolver.Resolve(issues, full, "那 Ntfs 的錯誤呢");
        Assert.Equal(4, focus.Count);
        Assert.Equal("Ntfs", focus[0].Source);
    }

    [Fact]
    public void 命中的問題已在focusKeys裡_不重複且移到最前()
    {
        var issues = Day();
        var keys = new[] { issues[2].IssueKey, issues[0].IssueKey };
        var (focus, source) = ChatFocusResolver.Resolve(issues, keys, "4625 怎麼了");
        Assert.Equal(new[] { issues[0].IssueKey, issues[2].IssueKey }, focus.Select(f => f.IssueKey));
        Assert.Equal("manual", source);
    }

    [Fact]
    public void 請求帶超量focusKeys_只看前幾個_不擲例外()
    {
        var issues = Day();
        var keys = Enumerable.Repeat("不存在的鍵", 5000).Append(issues[0].IssueKey).ToArray();
        var (focus, source) = ChatFocusResolver.Resolve(issues, keys, "今天怎麼樣");
        Assert.Equal("default", source);
        Assert.Equal(3, focus.Count);
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

    [Theory]
    [InlineData("這個 issue 的 result 怎麼看")]
    [InlineData("sudo 的紀錄正常嗎")]
    [InlineData("consult 一下")]
    public void 短來源名不得被其他英文字的片段誤命中(string message)
    {
        var issues = new List<IssueDto> { I("su", 0, "High", label: "su（linux-su-fail）"), I("sshd", 0, "Medium"), I("crond", 0, "Low") };
        var (focus, source) = ChatFocusResolver.Resolve(issues, null, message);
        Assert.Equal("default", source);
    }

    [Fact]
    public void 來源名的後半段以連字號相連時仍命中()
    {
        var (focus, source) = ChatFocusResolver.Resolve(Day(), null, "Security-Auditing 的事件怎麼了");
        Assert.Equal("keyword", source);
        Assert.All(focus, f => Assert.Equal("Microsoft-Windows-Security-Auditing", f.Source));
        Assert.Equal(2, focus.Count);
    }

    [Theory]
    [InlineData("su 失敗是怎麼回事")]
    [InlineData("請說明su的問題")]
    [InlineData("SU?")]
    public void 短來源名整詞出現時命中(string message)
    {
        var issues = new List<IssueDto> { I("su", 0, "High"), I("sshd", 0, "Medium") };
        var (focus, source) = ChatFocusResolver.Resolve(issues, null, message);
        Assert.Equal("keyword", source);
        Assert.Equal("su", Assert.Single(focus).Source);
    }
}
