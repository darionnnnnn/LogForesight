using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>郵件內容指紋：ForRecord 輸出固定、郵件指紋不含三個狀態旗標與 NetIQ 嘗試欄位。</summary>
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

    /// <summary>
    /// 帳本在續跑每批開始與結束時就地回寫 NetIQ 嘗試狀態與時間，信件內容沒變：郵件指紋不得隨之改變。
    /// ForRecord 仍含這兩欄（AI 寫回的樂觀檢查與失敗路徑復原刻意使用），舊算法（ForRecordWithFlags）也仍含——
    /// 旗標全為 false 的舊算法就是上一版的郵件指紋，升級相容靠它認得舊鍵。
    /// </summary>
    [Fact]
    public void 郵件內容指紋不含NetIQ嘗試欄位_舊算法與ForRecord仍含()
    {
        var a = Fixed();
        a.LatestNetiqAttemptStatus = "partial";
        a.LatestNetiqAttemptAtUtc = new DateTime(2026, 11, 11, 1, 0, 0, DateTimeKind.Utc);
        var b = Fixed();
        b.LatestNetiqAttemptStatus = "running";
        b.LatestNetiqAttemptAtUtc = new DateTime(2026, 11, 11, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal(HostDayWorkflowFingerprint.ForMailContent(a), HostDayWorkflowFingerprint.ForMailContent(b));
        Assert.NotEqual(HostDayWorkflowFingerprint.ForRecord(a), HostDayWorkflowFingerprint.ForRecord(b));
        Assert.NotEqual(HostDayWorkflowFingerprint.ForRecordWithFlags(a, false, false, false),
            HostDayWorkflowFingerprint.ForRecordWithFlags(b, false, false, false));
    }

    [Fact]
    public void 沒有嘗試欄位時_新郵件指紋與上一版算法相同()
    {
        // 嘗試欄位為空的主機日（本機來源、或帳本從未回寫）升級前後鍵完全相同，不必經相容路徑
        var r = Fixed();
        Assert.Equal(HostDayWorkflowFingerprint.ForRecordWithFlags(r, false, false, false), HostDayWorkflowFingerprint.ForMailContent(r));
    }
}
