using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批 4A（docs/archive/FEEDBACK-12-PLAN.md §4.2/§4.3/§4.7）Linux 事件模型與簽章聚合的專屬測試：
/// EventKey 五元組分組鍵、IssueSignatureKey 向後相容、TrendAnalyzer/SlowTrendAnalyzer 的
/// SameIssue 五元組比對、ChannelCoverage.WasRead("Linux") 恆真、關聯層 Linux 短路與
/// UncoveredChecks 申報、FindLinuxRule 經 LogAggregator 整合命中、RiskyEventSelector
/// 不跨規則污染。與 KnownIssueCatalogTests 共用同一個 collection——本檔多處呼叫
/// LogAggregator.Aggregate（間接讀 KnownIssueCatalog.Rules 這份共用靜態狀態）。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class LinuxSignatureAggregationTests : IDisposable
{
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    private static EventLogEntryData MakeLinuxEntry(string program, string message, DateTime? time = null) => new()
    {
        TimeGenerated = time ?? DateTime.Today.AddHours(1),
        LogName = LogAggregator.LinuxLogName,
        Source = program,
        EventId = 0,
        EntryType = EventLogEntryType.Error,
        Message = message
    };

    // ── LogAggregator：EventKey 五元組聚合 ──────────────────────────────

    [Fact]
    public void 同program不同規則的訊息分成兩個簽章各自帶對應EventKey()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(Enumerable.Range(0, 6).Select(i =>
            MakeLinuxEntry("sshd", $"Failed password for invalid user root from 10.0.0.{i} port 22 ssh2")));
        logs.AddRange(Enumerable.Range(0, 3).Select(i =>
            MakeLinuxEntry("sshd", $"Accepted password for admin from 10.0.0.{i} port 22 ssh2")));

        var signatures = LogAggregator.Aggregate(logs);

        Assert.Equal(2, signatures.Count);

        var bruteforce = Assert.Single(signatures, s => s.EventKey == "builtin-linux-ssh-bruteforce");
        Assert.Equal(6, bruteforce.Count);
        Assert.Equal("sshd", bruteforce.Source);
        Assert.Equal(0, bruteforce.EventId);
        Assert.Equal("builtin-linux-ssh-bruteforce", bruteforce.RuleId);

        var accepted = Assert.Single(signatures, s => s.EventKey == "builtin-linux-ssh-accept");
        Assert.Equal(3, accepted.Count);
        Assert.Equal("builtin-linux-ssh-accept", accepted.RuleId);
    }

    [Fact]
    public void 未命中規則的Linux事件EventKey恆空且同program聚合成同一個Other簽章()
    {
        var logs = Enumerable.Range(0, 4)
            .Select(i => MakeLinuxEntry("totally-unrelated-daemon", $"routine message #{i}"))
            .ToList();

        var signature = Assert.Single(LogAggregator.Aggregate(logs));

        Assert.Equal(string.Empty, signature.EventKey);
        Assert.Equal(IssueCategory.Other, signature.Category);
        Assert.Equal(IssueSeverity.Low, signature.Severity);
        Assert.Null(signature.RuleId);
        Assert.Equal(4, signature.Count);
    }

    [Fact]
    public void Windows事件的EventKey恆空_四元組聚合行為零改變()
    {
        var logs = Enumerable.Range(0, 3).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(i),
            LogName = "System",
            Source = "disk",
            EventId = 153,
            EntryType = EventLogEntryType.Error,
            Message = $"磁碟發生 I/O 錯誤 #{i}"
        }).ToList();

        var signature = Assert.Single(LogAggregator.Aggregate(logs));

        Assert.Equal(string.Empty, signature.EventKey);
        Assert.Equal("disk EventId 153", signature.SourceEventLabel);
    }

    [Fact]
    public void SourceEventLabel_Linux命中規則顯示規則Id_未命中維持EventId0()
    {
        var hit = new LogIssueSignature { Source = "sshd", EventId = 0, EventKey = "builtin-linux-ssh-bruteforce" };
        var miss = new LogIssueSignature { Source = "totally-unrelated-daemon", EventId = 0, EventKey = "" };

        Assert.Equal("sshd（builtin-linux-ssh-bruteforce）", hit.SourceEventLabel);
        Assert.Equal("totally-unrelated-daemon EventId 0", miss.SourceEventLabel);
    }

    // ── IssueSignatureKey：向後相容釘 ────────────────────────────────────

    [Fact]
    public void Windows簽章的穩定鍵字串與既有四段格式逐字相同()
    {
        var sig = new LogIssueSignature
        {
            LogName = "System",
            Source = "disk",
            EventId = 153,
            EntryType = EventLogEntryType.Error,
            EventKey = string.Empty
        };

        Assert.Equal("System|disk|153|1", IssueSignatureKey.For(sig));
    }

    [Fact]
    public void Linux命中規則的簽章鍵附加第五段EventKey()
    {
        var sig = new LogIssueSignature
        {
            LogName = LogAggregator.LinuxLogName,
            Source = "sshd",
            EventId = 0,
            EntryType = EventLogEntryType.Error,
            EventKey = "builtin-linux-ssh-bruteforce"
        };

        Assert.Equal("Linux|sshd|0|1|builtin-linux-ssh-bruteforce", IssueSignatureKey.For(sig));
    }

    [Fact]
    public void TryParseSignature可解析四段與五段鍵並忽略多餘的EventKey尾段()
    {
        var fourSegment = IssueSignatureKey.TryParseSignature("System|disk|153|1");
        var fiveSegment = IssueSignatureKey.TryParseSignature("Linux|sshd|0|1|builtin-linux-ssh-bruteforce");

        Assert.Equal(("disk", 153), fourSegment);
        Assert.Equal(("sshd", 0), fiveSegment);
    }

    [Fact]
    public void TryParseSignature對段數不符或非數字EventId回傳null()
    {
        Assert.Null(IssueSignatureKey.TryParseSignature("Linux|sshd|0"));
        Assert.Null(IssueSignatureKey.TryParseSignature("a|b|c|d|e|f"));
        Assert.Null(IssueSignatureKey.TryParseSignature("System|disk|not-a-number|1"));
        Assert.Null(IssueSignatureKey.TryParseSignature(null));
    }

    // ── TrendAnalyzer / SlowTrendAnalyzer：SameIssue 五元組 ──────────────

    private static LogIssueSignature MakeSignature(string eventKey, int count) => new()
    {
        LogName = LogAggregator.LinuxLogName,
        Source = "sshd",
        EventId = 0,
        EntryType = EventLogEntryType.Error,
        EventKey = eventKey,
        Count = count,
        Severity = IssueSeverity.High
    };

    private static DailyAnalysisRecord MakeHistoryDay(DateTime date, string eventKey, int count) => new()
    {
        Date = date,
        TopIssues = new List<LogIssueSignature> { MakeSignature(eventKey, count) }
    };

    [Fact]
    public void TrendAnalyzer_不同EventKey不會被誤判成同一個問題的歷史()
    {
        var today = DateTime.Today;
        // 歷史 7 天只有 ssh-accept 出現過，今天換成 ssh-bruteforce——不能被誤判成
        // 「同一個問題延續」，SameIssue 若漏比對 EventKey 會把這裡誤標成 Recurring
        var history = Enumerable.Range(1, 7)
            .Select(d => MakeHistoryDay(today.AddDays(-d), "builtin-linux-ssh-accept", 5))
            .ToList();

        var todaySig = MakeSignature("builtin-linux-ssh-bruteforce", 6);
        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { todaySig }, history, today, todayErrorCount: 6, todayAuditCount: 0);

        Assert.Equal(IssueTrend.New, todaySig.Trend);
        Assert.Null(todaySig.HistoryDailyAverage);
        Assert.Contains(alerts, a => a.Contains("首次出現"));
    }

    [Fact]
    public void SlowTrendAnalyzer_不同EventKey不會併入同一個累計總量()
    {
        var today = DateTime.Today;
        // 前後兩期歷史（各 7 天）全部都是 ssh-accept（count=10/天），今天比對的是 ssh-bruteforce（count=50）：
        // 若 SameIssue 漏比對 EventKey，兩期都會被誤判成「有 ssh-bruteforce 歷史」（誤取 accept 的次數），
        // 算出 priorTotal=70、recentTotal=50+60=110，110>=70*1.5 命中，錯誤觸發「慢速惡化」告警；
        // 修正後 priorTotal 應為 0（兩個 EventKey 從未在歷史中相符），因 priorTotal>0 的守門而不觸發
        var history = new List<DailyAnalysisRecord>();
        for (int d = 1; d <= 13; d++)
        {
            history.Add(MakeHistoryDay(today.AddDays(-d), "builtin-linux-ssh-accept", 10));
        }

        var todaySig = MakeSignature("builtin-linux-ssh-bruteforce", 50);
        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { todaySig }, history, today);

        Assert.Empty(alerts);
    }

    // ── ChannelCoverage：Linux 恆已讀 ─────────────────────────────────────

    [Fact]
    public void ChannelCoverage_Linux恆視為已讀_不受ChannelsReadNull影響()
    {
        var record = new DailyAnalysisRecord { ChannelsRead = null };
        Assert.True(ChannelCoverage.WasRead(record, LogAggregator.LinuxLogName));

        var recordWithOtherChannels = new DailyAnalysisRecord { ChannelsRead = new List<string> { "System" } };
        Assert.True(ChannelCoverage.WasRead(recordWithOtherChannels, "Linux"));
    }

    // ── LogAnalysisService：關聯層 Linux 分路（LinuxCorrelationAnalyzer）與 UncoveredChecks 申報
    //    （docs/archive/FEEDBACK-12-PLAN.md §4.5，批 4C 落地——CorrelationAnalyzer 對 Linux 事件的
    //    「不執行」在 4A 已改為「走 LinuxCorrelationAnalyzer」，這裡驗證的是分路接對、
    //    UncoveredChecks 文案也同步更新，不是原本 §4.3 那個純短路的舊行為）───────

    [Fact]
    public async Task BuildStatisticalRecordAsync_Linux主機未達暴力破解門檻時關聯層無告警()
    {
        using var fx = new EfSqliteFixture();
        var history = new EfAnalysisRecordStore(fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore());

        var logs = Enumerable.Range(0, 6) // 未達 LinuxCorrelationAnalyzer 的 10 筆門檻
            .Select(i => MakeLinuxEntry("sshd", $"Failed password for invalid user root from 10.0.0.{i} port 22 ssh2"))
            .ToList();

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, logs, useAi: false, hostOs: WebHost.OsLinux);

        Assert.Empty(record.CorrelationAlerts);
        Assert.Contains(record.UncoveredChecks, c => c.Contains("關聯層") && c.Contains("不適用於 Linux"));
        // 回饋十三輪 A9：檢索面比 Windows 窄一階的申報，與關聯層申報同一力道、同時出現
        Assert.Contains(record.UncoveredChecks, c => c.Contains("sev 2-5") && c.Contains("Windows 主機無此限制"));
    }

    [Fact]
    public async Task BuildStatisticalRecordAsync_Linux主機SSH破解得手時關聯層產出告警()
    {
        using var fx = new EfSqliteFixture();
        var history = new EfAnalysisRecordStore(fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore());

        var logs = new List<EventLogEntryData>();
        logs.AddRange(Enumerable.Range(0, 10).Select(i =>
            MakeLinuxEntry("sshd", $"Failed password for invalid user attacker1 from 10.0.0.1 port {50000 + i} ssh2",
                DateTime.Today.AddHours(1).AddMinutes(i))));
        logs.Add(MakeLinuxEntry("sshd", "Accepted password for attacker1 from 10.0.0.9 port 22 ssh2",
            DateTime.Today.AddHours(2)));

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, logs, useAi: false, hostOs: WebHost.OsLinux);

        var alert = Assert.Single(record.CorrelationAlerts);
        Assert.Contains("破解得手", alert);
        Assert.Contains("attacker1", alert);
        // 命中關聯的問題日一律申報「僅涵蓋 SSH」，不會因為這次真的命中了就改口說「已完整涵蓋」
        Assert.Contains(record.UncoveredChecks, c => c.Contains("僅涵蓋 SSH 破解得手"));
    }

    [Fact]
    public async Task BuildStatisticalRecordAsync_Windows主機不受Linux短路影響()
    {
        using var fx = new EfSqliteFixture();
        var history = new EfAnalysisRecordStore(fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore());

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, new List<EventLogEntryData>(), useAi: false);

        Assert.DoesNotContain(record.UncoveredChecks, c => c.Contains("不適用於 Linux"));
        // 回饋十三輪 A9：檢索面限制是 Linux 專屬的申報，Windows 主機不該出現
        Assert.DoesNotContain(record.UncoveredChecks, c => c.Contains("sev 2-5"));
    }

    // ── FindLinuxRule 經 LogAggregator 整合命中：17 條種子逐條 ────────────

    public static IEnumerable<object[]> LinuxRules() =>
        KnownIssueSeed.CreateRules().Where(r => r.Platform == "linux").Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(LinuxRules))]
    public void 每條Linux規則經過完整聚合流程都能命中自己並帶正確EventKey(KnownIssueRule rule)
    {
        var program = rule.ProgramPattern.Length > 0 ? rule.ProgramPattern : "unrelated-program";
        var message = rule.MessagePatterns.Length > 0 ? rule.MessagePatterns[0] : "任意訊息內容";

        var logs = Enumerable.Range(0, rule.CountThreshold)
            .Select(i => MakeLinuxEntry(program, message, DateTime.Today.AddMinutes(i)))
            .ToList();

        var signature = Assert.Single(LogAggregator.Aggregate(logs));

        Assert.Equal(rule.Id, signature.EventKey);
        Assert.Equal(rule.Id, signature.RuleId);
        Assert.Equal(rule.Category, signature.Category);
        Assert.Equal(rule.Severity, signature.Severity);
    }

    // ── RiskyEventSelector：不跨規則污染 ──────────────────────────────────

    [Fact]
    public void RiskyEventSelector_同program不同規則的原始事件不互相污染()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(Enumerable.Range(0, 8).Select(i =>
            MakeLinuxEntry("sshd", $"Failed password for invalid user root from 10.0.0.{i} port 22 ssh2")));
        logs.AddRange(Enumerable.Range(0, 4).Select(i =>
            MakeLinuxEntry("sshd", $"Accepted password for admin from 10.0.0.{i} port 22 ssh2")));

        var issues = LogAggregator.Aggregate(logs);
        var riskyEvents = RiskyEventSelector.Select(issues, logs, hostId: 1, date: DateTime.Today);

        var bruteforceEvents = riskyEvents.Where(e => e.RuleId == "builtin-linux-ssh-bruteforce").ToList();
        var acceptEvents = riskyEvents.Where(e => e.RuleId == "builtin-linux-ssh-accept").ToList();

        Assert.Equal(8, bruteforceEvents.Count);
        Assert.Equal(4, acceptEvents.Count);
        Assert.All(bruteforceEvents, e => Assert.Contains("Failed password", e.Message));
        Assert.All(acceptEvents, e => Assert.Contains("Accepted password", e.Message));
    }

    // ── LinuxAuthParser / LogAggregator：Linux 登入失敗明細抽取（A2） ────────────

    [Fact]
    public void 解析sshd密碼錯誤訊息()
    {
        var msg = "Failed password for alice from 10.0.0.5 port 22 ssh2";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("alice", user);
        Assert.Equal("10.0.0.5", ip);
        Assert.Equal("bad_password", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sshd", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("alice", detail.Account);
        Assert.False(detail.IsComputerAccount);
        Assert.Equal("10.0.0.5", detail.Source);
        Assert.Null(detail.LogonType);
        Assert.Equal("bad_password", detail.ReasonCode);
        Assert.Equal(1, detail.Count);
    }

    [Fact]
    public void 解析sshd無效帳號密碼錯誤訊息()
    {
        var msg = "Failed password for invalid user 1838651 from 10.225.2.219 port 54500 ssh2";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("1838651", user);
        Assert.Equal("10.225.2.219", ip);
        Assert.Equal("unknown_user", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sshd", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("1838651", detail.Account);
        Assert.Equal("10.225.2.219", detail.Source);
        Assert.Equal("unknown_user", detail.ReasonCode);
    }

    [Fact]
    public void 解析sshd獨立InvalidUser行()
    {
        var msg = "Invalid user admin from 10.0.0.5 port 22";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("admin", user);
        Assert.Equal("10.0.0.5", ip);
        Assert.Equal("unknown_user", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sshd", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("admin", detail.Account);
        Assert.Equal("10.0.0.5", detail.Source);
        Assert.Equal("unknown_user", detail.ReasonCode);
    }

    [Fact]
    public void 解析pam_unix_sshd認證失敗訊息取user而非ruser()
    {
        var msg = "pam_unix(sshd:auth): authentication failure; logname= uid=0 euid=0 tty=ssh ruser= rhost=10.0.0.5 user=root";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("root", user);
        Assert.Equal("10.0.0.5", ip);
        Assert.Equal("bad_password", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sshd", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("root", detail.Account);
        Assert.Equal("10.0.0.5", detail.Source);
        Assert.Equal("bad_password", detail.ReasonCode);
    }

    [Fact]
    public void 解析pam_unix_sudo認證失敗訊息rhost為空時ip為空字串()
    {
        var msg = "pam_unix(sudo:auth): authentication failure; logname=alice uid=1000 euid=0 tty=/dev/pts/0 ruser=alice rhost= user=alice";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("alice", user);
        Assert.Equal(string.Empty, ip);
        Assert.Equal("bad_password", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sudo", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("alice", detail.Account);
        Assert.Equal(string.Empty, detail.Source);
        Assert.Equal("bad_password", detail.ReasonCode);
    }

    [Fact]
    public void 解析pam_unix_su認證失敗訊息取user而非ruser()
    {
        var msg = "pam_unix(su:auth): authentication failure; logname=alice uid=1000 euid=0 tty=/dev/pts/1 ruser=alice rhost= user=root";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("root", user);
        Assert.Equal(string.Empty, ip);
        Assert.Equal("bad_password", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("su", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("root", detail.Account);
        Assert.Equal(string.Empty, detail.Source);
        Assert.Equal("bad_password", detail.ReasonCode);
    }

    [Fact]
    public void 解析sudo_incorrect_password_attempt訊息()
    {
        var msg = "alice : 1 incorrect password attempt ; TTY=pts/0 ; PWD=/home/alice ; USER=root ; COMMAND=/bin/ls";
        var success = LinuxAuthParser.TryParseAuthFailure(msg, out var user, out var ip, out var reasonCode);

        Assert.True(success);
        Assert.Equal("alice", user);
        Assert.Equal(string.Empty, ip);
        Assert.Equal("bad_password", reasonCode);

        var logs = new List<EventLogEntryData> { MakeLinuxEntry("sudo", msg) };
        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.NotNull(sig.LoginFailureDetails);
        var detail = Assert.Single(sig.LoginFailureDetails);
        Assert.Equal("alice", detail.Account);
        Assert.Equal(string.Empty, detail.Source);
        Assert.Equal("bad_password", detail.ReasonCode);
    }

    [Fact]
    public void 完全不符合任何樣式的訊息不產生明細且簽章Count不受影響()
    {
        var unparseableMsg = "sshd: some unparseable authentication failure message";
        var success = LinuxAuthParser.TryParseAuthFailure(unparseableMsg, out var user, out var ip, out var reasonCode);

        Assert.False(success);
        Assert.Equal(string.Empty, user);
        Assert.Equal(string.Empty, ip);
        Assert.Equal(string.Empty, reasonCode);

        var logs = new List<EventLogEntryData>
        {
            MakeLinuxEntry("sshd", unparseableMsg),
            MakeLinuxEntry("sshd", unparseableMsg)
        };

        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.Equal("builtin-linux-ssh-bruteforce", sig.EventKey);
        Assert.Equal(2, sig.Count);
        Assert.NotNull(sig.LoginFailureDetails);
        Assert.Empty(sig.LoginFailureDetails);
    }

    [Fact]
    public void 同一帳號與IP多次出現合併為一筆且Count正確()
    {
        var logs = new List<EventLogEntryData>
        {
            MakeLinuxEntry("sshd", "Failed password for alice from 10.0.0.5 port 22 ssh2"),
            MakeLinuxEntry("sshd", "Failed password for ALICE from 10.0.0.5 port 23 ssh2"),
            MakeLinuxEntry("sshd", "Failed password for bob from 10.0.0.6 port 22 ssh2")
        };

        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.Equal(3, sig.Count);
        Assert.NotNull(sig.LoginFailureDetails);
        Assert.Equal(2, sig.LoginFailureDetails.Count);

        var alice = sig.LoginFailureDetails[0];
        Assert.Equal("alice", alice.Account);
        Assert.Equal("10.0.0.5", alice.Source);
        Assert.Equal(2, alice.Count);
        Assert.Equal("bad_password", alice.ReasonCode);

        var bob = sig.LoginFailureDetails[1];
        Assert.Equal("bob", bob.Account);
        Assert.Equal("10.0.0.6", bob.Source);
        Assert.Equal(1, bob.Count);
        Assert.Equal("bad_password", bob.ReasonCode);
    }

    [Fact]
    public void 非登入失敗的Linux簽章其LoginFailureDetails為null()
    {
        var logs = new List<EventLogEntryData>
        {
            MakeLinuxEntry("kernel", "EXT4-fs error (device sda1): ext4_lookup: deleted inode referenced: 12345")
        };

        var sig = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.Equal("builtin-linux-storage-io", sig.EventKey);
        Assert.Null(sig.LoginFailureDetails);
    }

    [Fact]
    public void Linux登入失敗明細超過50組時最多保留50筆且依Count降冪排序()
    {
        var msgs = new List<string>();
        for (int i = 1; i <= 60; i++)
        {
            int count = (i == 1) ? 5 : 1;
            for (int j = 0; j < count; j++)
            {
                msgs.Add($"Failed password for user{i} from 10.0.0.{i} port 22 ssh2");
            }
        }

        var details = LogAggregator.ExtractLinuxLoginFailureDetails(msgs);

        Assert.Equal(50, details.Count);
        Assert.Equal("user1", details[0].Account);
        Assert.Equal(5, details[0].Count);

        for (int i = 0; i < details.Count - 1; i++)
        {
            Assert.True(details[i].Count >= details[i + 1].Count);
        }
    }

    [Fact]
    public void LoginFailureRuleIds集合包含三個預期規則Id()
    {
        Assert.Contains("builtin-linux-ssh-bruteforce", LinuxAuthParser.LoginFailureRuleIds);
        Assert.Contains("builtin-linux-sudo-auth-failure", LinuxAuthParser.LoginFailureRuleIds);
        Assert.Contains("builtin-linux-su-auth-failure", LinuxAuthParser.LoginFailureRuleIds);
        Assert.Equal(3, LinuxAuthParser.LoginFailureRuleIds.Count);
    }
}
