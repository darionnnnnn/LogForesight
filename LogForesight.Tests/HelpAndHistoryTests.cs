using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public class HelpAndHistoryTests
{
    [Fact]
    public void 靜音查詢限定問題動作與日期_不混入其他稽核()
    {
        using var db = new EfSqliteFixture();
        var audit = new AuditLogStore(db.LogStore("audit"));
        audit.Append(new AuditEntry { TargetKind = "issue_owner", TargetId = "disk/153", Action = AuditActions.IssueMute });
        audit.Append(new AuditEntry { TargetKind = "issue_owner", TargetId = "disk/153", Action = AuditActions.IssueUnmute });
        audit.Append(new AuditEntry { TargetKind = "issue_owner", TargetId = "other/153", Action = AuditActions.IssueMute });
        audit.Append(new AuditEntry { TargetKind = "issue_owner", TargetId = "disk/153", Action = AuditActions.Login });
        audit.Append(new AuditEntry { TargetKind = "issue_owner", TargetId = "disk/153", Action = AuditActions.IssueMute, OccurredAt = DateTime.Today.AddDays(-100) });
        var result = audit.Query(new AuditQuery
        {
            TargetKind = "issue_owner", TargetId = "disk/153",
            Actions = new() { AuditActions.IssueMute, AuditActions.IssueUnmute }
        });
        Assert.True(result.DefaultRangeApplied);
        Assert.Equal(2, result.Total);
        Assert.All(result.Items, entry => Assert.Equal("disk/153", entry.TargetId));
        Assert.Contains(result.Items, entry => entry.Action == AuditActions.IssueUnmute);
    }

    [Fact]
    public void 處理人取得公開章節且相關連結不洩漏管理章節()
    {
        var content = new HelpContentService();
        var manual = content.GetManual(new HashSet<Capability> { Capability.Handle });
        var ids = manual.Chapters.Select(c => c.Id).ToHashSet();
        Assert.Contains("my-work", ids);
        Assert.DoesNotContain("案件", manual.Chapters.Single(c => c.Id == "my-work").Content);
        foreach (var restricted in content.Chapters.Where(c => c.Requires != null))
            Assert.DoesNotContain(restricted.Id, ids);
        Assert.All(manual.Chapters, chapter => Assert.All(chapter.Related, id => Assert.Contains(id, ids)));
        Assert.Equal(content.Chapters.Count, content.GetManual(new HashSet<Capability> { Capability.Maintain }).Chapters.Count);
    }

    [Fact]
    public async Task 處理人問管理問題也不會將管理章節送給AI()
    {
        var content = new HelpContentService();
        var ai = new FakeWebAi { Available = true, Response = "請聯絡管理員" };
        var service = new HelpQaService(content, ai, FakeCurrentUser.WithCapabilities(Capability.Handle));
        var result = await service.AskAsync("SMTP 郵件通知與 AD 驗證設定如何管理？");
        Assert.NotNull(result);
        Assert.NotNull(ai.LastUserPrompt);
        foreach (var restricted in content.Chapters.Where(c => c.Requires != null))
        {
            Assert.DoesNotContain(restricted.Id, result!.CitedChapterIds);
            if (!string.IsNullOrWhiteSpace(restricted.ContentForAi))
                Assert.DoesNotContain(restricted.ContentForAi, ai.LastUserPrompt);
        }
    }
}
