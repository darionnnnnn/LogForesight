using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>郵件內容指紋（第 78 輪批次 0）：ForRecord 輸出固定、郵件指紋不含三個狀態旗標。</summary>
public class MailFingerprintTests
{
    internal static DailyAnalysisRecord Fixed() => new()
    {
        RecordId = 4001, HostId = 7, Host = "host", Date = new DateTime(2026, 11, 10),
        RiskLevel = RiskLevels.High, LogSource = AnalysisLogSource.Netiq,
        AiPending = true, AuditEventCount = 3, ErrorCount = 2, WarningCount = 1,
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "Disk", EventId = 7, EventKey = "Disk/7",
                Category = IssueCategory.Other, Severity = IssueSeverity.High, Count = 1 }
        }
    };

    [Fact]
    public void ForRecord輸出與改動前逐位元相同()
    {
        Assert.Equal("4883660CC3E11553949F4F8BB192CE8669E277E16E34A0BAAF32E93DE42B7708", HostDayWorkflowFingerprint.ForRecord(Fixed()));
    }

    [Fact]
    public void 郵件內容指紋不含三個狀態旗標_風險等級仍納入()
    {
        var a = Fixed();
        var b = Fixed();
        b.AiPending = false; b.AiAnalyzed = true; b.DetailPruned = true;
        Assert.NotEqual(HostDayWorkflowFingerprint.ForRecord(a), HostDayWorkflowFingerprint.ForRecord(b));
        Assert.Equal(HostDayWorkflowFingerprint.ForMailContent(a), HostDayWorkflowFingerprint.ForMailContent(b));
        b.RiskLevel = RiskLevels.Medium;
        Assert.NotEqual(HostDayWorkflowFingerprint.ForMailContent(a), HostDayWorkflowFingerprint.ForMailContent(b));
    }
}
