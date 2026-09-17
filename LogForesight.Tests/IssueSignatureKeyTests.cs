using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary><see cref="IssueSignatureKey.TryParseFull"/> 的格式判定與「組回再組鍵逐字相同」不變式</summary>
public class IssueSignatureKeyTests
{
    [Fact]
    public void 四段_EventKey為空字串()
    {
        var parsed = IssueSignatureKey.TryParseFull("System|disk|153|1");

        Assert.Equal(("System", "disk", 153, EventLogEntryType.Error, string.Empty), parsed);
    }

    /// <summary>突變參考：TryParseFull 忽略第五段時這條轉紅</summary>
    [Fact]
    public void 五段_EventKey為第五段()
    {
        var parsed = IssueSignatureKey.TryParseFull("Linux|sshd|0|2|builtin-linux-ssh-bruteforce");

        Assert.Equal(("Linux", "sshd", 0, EventLogEntryType.Warning, "builtin-linux-ssh-bruteforce"), parsed);
    }

    [Fact]
    public void Source含特殊字元_原樣保留()
    {
        var parsed = IssueSignatureKey.TryParseFull("Microsoft-Windows-Kernel-Power/Operational|Microsoft-Windows-Kernel (電源) #1:x|41|1");

        Assert.NotNull(parsed);
        Assert.Equal("Microsoft-Windows-Kernel-Power/Operational", parsed!.Value.LogName);
        Assert.Equal("Microsoft-Windows-Kernel (電源) #1:x", parsed.Value.Source);
        Assert.Equal(41, parsed.Value.EventId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Linux|sshd|0")]
    [InlineData("a|b|1|2|e|f")]
    [InlineData("System|disk|not-a-number|1")]
    [InlineData("System|disk|153|Error")]
    public void 格式不符回null(string? key)
    {
        Assert.Null(IssueSignatureKey.TryParseFull(key));
    }

    [Fact]
    public void TryParseSignature行為不變_第四段非整數仍可解析來源與事件()
    {
        Assert.Equal(("disk", 153), IssueSignatureKey.TryParseSignature("System|disk|153|Error"));
        Assert.Equal(("sshd", 0), IssueSignatureKey.TryParseSignature("Linux|sshd|0|1|rule"));
        Assert.Null(IssueSignatureKey.TryParseSignature("System|disk|x|1"));
    }

    [Fact]
    public void 來回不變式_組回簽章再組鍵逐字相同()
    {
        var signatures = new[]
        {
            new LogIssueSignature { LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error },
            new LogIssueSignature { LogName = "Application", Source = "MSSQL$SQLEXPRESS", EventId = 17054, EntryType = EventLogEntryType.Information },
            new LogIssueSignature { LogName = "Security", Source = "Microsoft-Windows-Security-Auditing", EventId = 4625, EntryType = EventLogEntryType.FailureAudit },
            new LogIssueSignature { LogName = "Linux", Source = "sshd", EventId = 0, EntryType = EventLogEntryType.Warning, EventKey = "builtin-linux-ssh-bruteforce" },
            new LogIssueSignature { LogName = "PRTG", Source = "PRTG:down", EventId = 0, EntryType = EventLogEntryType.Error, EventKey = "探測 (中文)" },
            new LogIssueSignature { LogName = "System", Source = "Service Control Manager", EventId = -1, EntryType = EventLogEntryType.SuccessAudit }
        };

        foreach (var signature in signatures)
        {
            var key = IssueSignatureKey.For(signature);
            var p = IssueSignatureKey.TryParseFull(key)!.Value;

            var rebuilt = IssueSignatureKey.For(new LogIssueSignature
            {
                LogName = p.LogName, Source = p.Source, EventId = p.EventId, EntryType = p.EntryType, EventKey = p.EventKey
            });

            Assert.Equal(key, rebuilt);
        }
    }
}
