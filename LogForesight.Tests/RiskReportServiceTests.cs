using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 2026-07-20 AI 角色轉換後的報告雙軌渲染：規則已命中的類別（Category ≠ Other）
/// 直接查靜態知識庫、零 AI 呼叫；只有 Other 類別才會嘗試呼叫 AI。
/// 這裡只涵蓋純規則命中的情境（不含 Other），因此不需要 mock/啟動真的 AI 服務——
/// AIService 的建構子本身不發任何網路請求，只要流程中不觸發 DeepDiveAsync 就不會用到它。
/// </summary>
// 這裡的測試透過 KnownIssueCatalog.FindRule 讀取共用的靜態規則表（"disk" 等規則命中查表），
// 需要跟會呼叫 KnownIssueCatalog.Initialize 的測試序列執行，避免平行執行時讀到暫時被改寫的狀態
// ——見 KnownIssueCatalogStateCollection 的說明。
[Collection("KnownIssueCatalogState")]
public class RiskReportServiceTests
{
    // FakeReportSink 已搬到 TestDoubles\ReportingFakes.cs（與 WeeklyCheckupServiceTests 共用）。

    private static RiskReportService MakeService(FakeReportSink sink)
    {
        // 不會被呼叫到（測試情境全是規則命中的類別），BaseUrl 隨意即可
        var aiService = new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 }, NullAiRequestGate.Instance);
        return new RiskReportService(aiService, sink);
    }

    private static LogIssueSignature MakeStorageIssue() => new()
    {
        LogName = "System",
        Source = "disk",
        EventId = 153,
        EntryType = EventLogEntryType.Error,
        Count = 47,
        FirstSeen = "03:12",
        LastSeen = "23:40",
        Category = IssueCategory.Storage,
        Severity = IssueSeverity.Critical,
        KnownIssue = "磁碟 I/O 錯誤或壞軌前兆，硬碟可能即將故障，應盡快備份並安排更換"
    };

    private static DailyAnalysisRecord MakeRecord(LogIssueSignature issue) => new()
    {
        Date = DateTime.Today,
        Host = "test-host",
        RiskLevel = "高",
        AiAnalyzed = true,
        TopIssues = new List<LogIssueSignature> { issue },
        Summary = "測試摘要"
    };

    [Fact]
    public async Task 殘留憑證判定依據要寫進風險報告()
    {
        // 殘留判定會把嚴重度調降，報告不寫出理由的話，讀者只會看到一個沒來由變成
        // Medium 的登入失敗（FEEDBACK-30 A3 契約：「畫面與報告標示判定依據」）。
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var issue = MakeStorageIssue();
        issue.ResidualCredentialBasis = "疑似殘留憑證重試：svc_backup 自 WKS01 重複失敗 40 次（網路登入，密碼錯誤），近 7 天已重複出現";
        var record = MakeRecord(issue);

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        Assert.Contains("疑似殘留憑證重試：svc_backup 自 WKS01", draft.Content);
        Assert.False(sink.Called);
    }

    [Fact]
    public async Task 規則命中類別直接渲染靜態知識庫不呼叫AI()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        Assert.Contains("處置參考（知識庫）", draft.Content);
        Assert.DoesNotContain("AI 深入分析（儲存裝置）", draft.Content);
        Assert.Contains("硬碟可能即將故障", draft.Content + record.TopIssues[0].KnownIssue);
    }

    [Fact]
    public async Task PRTG風險報告包含有界量值說明與明確來源可信度()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var issue = MakeStorageIssue();
        issue.LogName = "PRTG";
        issue.Source = "PRTG:disk_free_trend";
        issue.EventId = 0;
        issue.EventKey = "prtg:disk_free_trend:17";
        issue.SampleMessages = ["兩小時可用空間均值 4.2%，合格涵蓋 120 分鐘"];
        issue.SourceObservations =
        [
            new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.Prtg,
                ExactHostKey = "host-id:17",
                ResourceScope = SourceResourceScope.Unknown,
                WindowStartUtc = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero),
                WindowEndUtc = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero)
            }
        ];

        var draft = await service.PrepareAsync(MakeRecord(issue), new List<EventLogEntryData>());

        Assert.Contains("PRTG量值／期間說明（原始 finding；不代表跨來源確認）：兩小時可用空間均值 4.2%", draft.Content);
        Assert.Contains("UTC 時間：[2026-10-05T08:00:00.0000000+00:00,2026-10-05T10:00:00.0000000+00:00)", draft.Content);
        Assert.Contains("主機身分：已確認（host-id:17）", draft.Content);
        Assert.Contains("資源身分：未確認", draft.Content);
        Assert.Contains("原生來源引用：未確認", draft.Content);
        Assert.DoesNotContain("精確來源佐證", draft.Content);
    }

    [Fact]
    public async Task PRTG報告清理並限制不可信顯示字串且不輸出credential樣式內容()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var issue = MakeStorageIssue();
        issue.LogName = "PRTG";
        issue.Source = "PRTG:resource_cpu_sustained_pressure";
        issue.EventId = 0;
        issue.EventKey = "prtg:resource_cpu_sustained_pressure:17";
        issue.SampleMessages = ["<script>alert(1)</script>\r\n" + new string('x', 500)];
        issue.SourceObservations =
        [
            new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.Prtg,
                ExactHostKey = "host-id:17\n<script>",
                ExactResourceKey = "cpu<&>\r\n",
                ResourceScope = SourceResourceScope.Volume,
                SourceReference = "native</script>\nref",
                SourceReferenceQuality = SourceReferenceQuality.ExactNative,
                WindowStartUtc = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero),
                WindowEndUtc = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero)
            }
        ];

        var draft = await service.PrepareAsync(MakeRecord(issue), new List<EventLogEntryData>());

        Assert.DoesNotContain("<script>", draft.Content);
        Assert.DoesNotContain("</script>", draft.Content);
        Assert.Contains("‹script›", draft.Content);
        Assert.Contains("Volume（已確認（cpu‹＆›））", draft.Content);
        Assert.Contains("原生來源引用：已確認（native‹/script›ref）", draft.Content);
        Assert.DoesNotContain(new string('x', 321), draft.Content);

        issue.SampleMessages = ["password=do-not-print"];
        var credentialDraft = await MakeService(new FakeReportSink()).PrepareAsync(MakeRecord(issue), new List<EventLogEntryData>());
        Assert.DoesNotContain("password=do-not-print", credentialDraft.Content);
    }

    [Fact]
    public async Task PRTG舊紀錄空來源集合仍以未確認輸出()
    {
        var sink = new FakeReportSink();
        var issue = MakeStorageIssue();
        issue.LogName = "PRTG";
        issue.Source = "PRTG:disk_free_trend";
        issue.EventId = 0;
        issue.SampleMessages = null!;
        issue.SourceObservations = null!;

        var draft = await MakeService(sink).PrepareAsync(MakeRecord(issue), new List<EventLogEntryData>());

        Assert.Contains("PRTG來源佐證：UTC 時間未確認；主機身分未確認；資源身分未確認；原生來源引用未確認", draft.Content);
    }

    [Fact]
    public async Task 靜態知識庫內容寫入DeepDives供DB查詢()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        var dive = Assert.Single(record.DeepDives);
        Assert.Equal(IssueCategory.Storage, dive.Category);
        var finding = Assert.Single(dive.Findings);
        Assert.False(string.IsNullOrWhiteSpace(finding.Problem));
        Assert.False(string.IsNullOrWhiteSpace(finding.Impact));
        Assert.NotEmpty(finding.LikelyCauses);
        Assert.NotEmpty(finding.NextSteps);
    }

    [Fact]
    public async Task AI未分析時規則命中類別仍能渲染靜態知識庫()
    {
        // AiAnalyzed=false（AI 呼叫失敗降級的統計模式紀錄）——靜態知識庫不依賴 AI 是否可用，
        // 這正是 AI 角色轉換的核心收益：規則命中的處置建議不再從缺
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());
        record.AiAnalyzed = false;

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        Assert.Single(record.DeepDives);
        Assert.Contains("處置參考（知識庫）", draft.Content);
    }

    private static LogIssueSignature MakeLinuxIssue(string ruleId, int count = 12) => new()
    {
        LogName = "Linux",
        Source = "sshd",
        EventId = 0,
        EntryType = EventLogEntryType.Warning,
        EventKey = ruleId,
        RuleId = ruleId,
        Count = count,
        FirstSeen = "01:00",
        LastSeen = "02:00",
        Category = IssueCategory.Security,
        Severity = IssueSeverity.High
    };

    private static EventLogEntryData MakeLinuxLog(string message, int minute) => new()
    {
        LogName = "Linux",
        Source = "sshd",
        EventId = 0,
        EntryType = EventLogEntryType.Warning,
        Message = message,
        TimeGenerated = DateTime.Today.AddMinutes(minute)
    };

    [Fact]
    public async Task Linux規則命中簽章以RuleId反查處置參考()
    {
        var rules = KnownIssueSeed.CreateRules();
        var service = new RiskReportService(
            new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 }, NullAiRequestGate.Instance),
            new FakeReportSink(), ruleProvider: () => rules);
        var record = MakeRecord(MakeLinuxIssue("builtin-linux-ssh-bruteforce"));

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        var rule = rules.First(r => r.Id == "builtin-linux-ssh-bruteforce");
        Assert.Contains(rule.PlainExplanation, draft.Content);
        Assert.Single(record.DeepDives);
    }

    [Fact]
    public async Task 自訂規則只在注入清單中仍有處置參考()
    {
        var custom = new KnownIssueRule
        {
            Id = "custom-r59-c7-only", Origin = "custom", Enabled = true, Platform = "windows",
            SourcePattern = "r59c7-nowhere", Category = IssueCategory.Storage, Severity = IssueSeverity.High,
            PlainExplanation = "R59C7 自訂規則白話說明獨有字串", Impact = "影響", LikelyCauses = new[] { "原因" }, NextSteps = new[] { "步驟" }
        };
        var service = new RiskReportService(
            new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 }, NullAiRequestGate.Instance),
            new FakeReportSink(), ruleProvider: () => new List<KnownIssueRule> { custom });
        var issue = MakeStorageIssue();
        issue.Source = "r59c7-source";
        issue.EventId = 9;
        issue.RuleId = custom.Id;

        var draft = await service.PrepareAsync(MakeRecord(issue), new List<EventLogEntryData>());

        Assert.Contains("R59C7 自訂規則白話說明獨有字串", draft.Content);
    }

    [Fact]
    public async Task 已抑制的High簽章不進重點問題()
    {
        var service = MakeService(new FakeReportSink());
        var suppressed = MakeStorageIssue();
        suppressed.Suppressed = true;

        var onlySuppressed = await service.PrepareAsync(MakeRecord(suppressed), new List<EventLogEntryData>());
        Assert.Contains("（無重點類別）", onlySuppressed.Content);
        Assert.DoesNotContain("重點問題", onlySuppressed.Content);

        var other = MakeStorageIssue();
        other.Source = "Ntfs";
        other.EventId = 55;
        var record = MakeRecord(suppressed);
        record.TopIssues.Add(other);
        var mixed = await service.PrepareAsync(record, new List<EventLogEntryData>());
        Assert.Contains("重點問題 1 項", mixed.Content);
    }

    [Fact]
    public async Task 同program不同規則的原始log不互相撈取()
    {
        var service = MakeService(new FakeReportSink());
        var record = MakeRecord(MakeLinuxIssue("builtin-linux-ssh-bruteforce"));
        record.TopIssues.Add(MakeLinuxIssue("builtin-linux-ssh-accept", 5));
        var logs = new List<EventLogEntryData>
        {
            MakeLinuxLog("Failed password for root from 1.2.3.4 port 1 ssh2", 1),
            MakeLinuxLog("Accepted publickey for admin from 5.6.7.8 port 2 ssh2", 2)
        };

        var draft = await service.PrepareAsync(record, logs);

        Assert.Contains("相關原始 Log（2 筆", draft.Content);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(draft.Content, "Failed password for root"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(draft.Content, "Accepted publickey for admin"));
    }
}
