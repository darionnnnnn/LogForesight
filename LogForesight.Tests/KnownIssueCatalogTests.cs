using System.Diagnostics;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 逐條規則自動驗證：規則層是換環境時最需要驗證的部分（Source 字串比對、EventId 門檻），
/// 用整張規則表自動產生案例，而不是手寫少數幾條——規則表增修時測試自動涵蓋新規則，不用手動補案例。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class KnownIssueCatalogTests : IDisposable
{
    // 這個測試類別裡只有「推導出的SecurityAuditWatchlist涵蓋原始寫死清單」會呼叫
    // KnownIssueCatalog.Initialize；用完整種子呼叫本身就等同預設狀態，但仍在每個測試後
    // 明確重置一次，避免任何一次呼叫方式的疏漏影響到同 collection 內其他測試類別
    // （見 KnownIssueCatalogStateCollection 的說明）。
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    // 改吃 KnownIssueSeed.CreateRules() 而非 KnownIssueCatalog.Rules（2026-07-21 規則外部化）：
    // KnownIssueCatalog.Rules 現在是可被 Initialize() 覆寫的可變靜態狀態，若其他測試呼叫過
    // Initialize（例如未來測規則載入/匯入流程），會讓這裡的 MemberData 隨測試執行順序漂移。
    // 種子本身才是這個測試檔案真正要驗證的對象，直接吃種子與可變全域狀態脫鉤。
    public static IEnumerable<object[]> AllRules() =>
        KnownIssueSeed.CreateRules().Select(r => new object[] { r });

    /// <summary>Windows 規則子集：這個檔案裡凡是走 LogAggregator/Classify（Event Log 語意）的
    /// 測試都只能吃這個子集——Linux 規則的 SourcePattern/EventIds 恆空，見 LinuxRules() 的獨立測試。</summary>
    public static IEnumerable<object[]> WindowsRules() =>
        KnownIssueSeed.CreateRules().Where(r => r.Platform == "windows").Select(r => new object[] { r });

    /// <summary>Linux 規則子集（docs/LINUX-RULES.md）：比對走 KnownIssueCatalog.FindLinuxRule。</summary>
    public static IEnumerable<object[]> LinuxRules() =>
        KnownIssueSeed.CreateRules().Where(r => r.Platform == "linux").Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(WindowsRules))]
    public void 達到門檻時分類與嚴重度符合規則表(KnownIssueRule rule)
    {
        var eventId = rule.EventIds.Length > 0 ? rule.EventIds[0] : 9999;
        var entryType = InferEntryType(rule.SourcePattern, eventId);

        var logs = MakeEntries(rule.CountThreshold, rule.SourcePattern, eventId, entryType);
        var signature = LogAggregator.Aggregate(logs).FirstOrDefault(s => s.Source == rule.SourcePattern && s.EventId == eventId);

        Assert.NotNull(signature);
        Assert.Equal(rule.Category, signature!.Category);
        Assert.Equal(rule.Severity, signature.Severity);
    }

    [Theory]
    [MemberData(nameof(WindowsRules))]
    public void 未達門檻時嚴重度降一級(KnownIssueRule rule)
    {
        if (rule.CountThreshold <= 1)
        {
            return; // 門檻為 1 的規則沒有「未達門檻」的情境
        }

        var eventId = rule.EventIds.Length > 0 ? rule.EventIds[0] : 9999;
        var entryType = InferEntryType(rule.SourcePattern, eventId);

        var logs = MakeEntries(1, rule.SourcePattern, eventId, entryType);
        var signature = LogAggregator.Aggregate(logs).FirstOrDefault(s => s.Source == rule.SourcePattern && s.EventId == eventId);

        var expected = rule.Severity == IssueSeverity.Low ? IssueSeverity.Low : rule.Severity - 1;
        Assert.NotNull(signature);
        Assert.Equal(expected, signature!.Severity);
    }

    [Fact]
    public void 未命中任何規則時歸類為Other且Low()
    {
        var logs = MakeEntries(1, "TotallyUnknownSource", 1, EventLogEntryType.Error);
        var signature = Assert.Single(LogAggregator.Aggregate(logs));

        Assert.Equal(IssueCategory.Other, signature.Category);
        Assert.Equal(IssueSeverity.Low, signature.Severity);
        Assert.Null(signature.KnownIssue);
    }

    /// <summary>
    /// 每條規則的靜態知識庫欄位（2026-07-20 AI 角色轉換新增）不可為空——
    /// 這些內容在規則命中時直接取代 AI 深入分析呼叫（見 RiskReportService.BuildStaticOutcome），
    /// 缺一個欄位就會讓報告的「處置參考」區塊靜默漏掉該問題。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRules))]
    public void 每條規則都有完整的白話知識庫內容(KnownIssueRule rule)
    {
        Assert.False(string.IsNullOrWhiteSpace(rule.PlainExplanation));
        Assert.False(string.IsNullOrWhiteSpace(rule.Impact));
        Assert.NotEmpty(rule.LikelyCauses);
        Assert.All(rule.LikelyCauses, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.NotEmpty(rule.NextSteps);
        Assert.All(rule.NextSteps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    [Fact]
    public void FindRule可依SourceEventId查回規則且與Classify比對邏輯一致()
    {
        var rule = KnownIssueCatalog.FindRule("disk", 153);

        Assert.NotNull(rule);
        Assert.Equal(IssueCategory.Storage, rule!.Category);
        // docs/archive/HISTORY.md #1（B1 三級化）：原 Critical 改為 High＋ElevatesDayRisk=true
        Assert.Equal(IssueSeverity.High, rule.Severity);
        Assert.True(rule.ElevatesDayRisk);
    }

    [Fact]
    public void FindRule對未命中規則的來源回傳null()
    {
        Assert.Null(KnownIssueCatalog.FindRule("TotallyUnknownSource", 1));
    }

    // ── Linux 規則（docs/LINUX-RULES.md）：比對走 FindLinuxRule，不經 LogAggregator ──

    [Theory]
    [MemberData(nameof(LinuxRules))]
    public void Linux規則各自宣告的比對路都能命中自己(KnownIssueRule rule)
    {
        if (!string.IsNullOrEmpty(rule.EventNamePattern))
        {
            var hit = KnownIssueCatalog.FindLinuxRule("unrelated-program", rule.EventNamePattern, "");
            Assert.NotNull(hit);
            Assert.Equal(rule.Id, hit!.Id);
        }

        if (!string.IsNullOrEmpty(rule.ProgramPattern))
        {
            var message = rule.MessagePatterns.Length > 0 ? rule.MessagePatterns[0] : "任意訊息內容";
            var hit = KnownIssueCatalog.FindLinuxRule(rule.ProgramPattern, null, message);
            Assert.NotNull(hit);
            Assert.Equal(rule.Id, hit!.Id);
        }
    }

    [Fact]
    public void FindLinuxRule對完全不相關的program與訊息回傳null()
    {
        Assert.Null(KnownIssueCatalog.FindLinuxRule("totally-unrelated-xyz", null, "totally unrelated message xyz"));
    }

    [Fact]
    public void FindRule明確排除Linux規則_即使SourcePattern為空字串也不會意外命中()
    {
        // Linux 規則的 SourcePattern 恆空；FindRule 若不顯式排除 Platform，"".Contains("") 恆真
        // 會是地雷（見 docs/LINUX-RULES.md §1.2）。用空字串來源直接戳這個邊界。
        Assert.Null(KnownIssueCatalog.FindRule("", 1));
    }

    // ── 規則外部化（2026-07-21）新增：種子本身的地基完整性 ─────────────────

    [Fact]
    public void 種子規則的Id全部唯一且以builtin開頭()
    {
        var rules = KnownIssueSeed.CreateRules();

        var ids = rules.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r => Assert.StartsWith("builtin-", r.Id));
        Assert.All(rules, r => Assert.Equal("builtin", r.Origin));
        Assert.All(rules, r => Assert.True(r.Enabled));
        Assert.All(rules, r => Assert.Equal("all", r.Scope));
        Assert.All(rules, r => Assert.Null(r.MatchFilter));
    }

    [Fact]
    public void 種子規則通過RuleValidator且零警告()
    {
        var outcome = RuleValidator.Validate(KnownIssueSeed.CreateRules());

        Assert.Empty(outcome.SkippedRules);
        Assert.Empty(outcome.ShadowWarnings);
    }

    /// <summary>
    /// 推導出的 watchlist（KnownIssueCatalog.SecurityAuditWatchlist，見 docs/RULES-SPEC.md 陷阱 1）
    /// 至少要涵蓋規則外部化前寫死維護的原始清單——這是確保「改用推導」沒有不小心漏掉的唯一保障，
    /// 與 <see cref="CorrelationAnalyzerRuleAlignmentTests"/> 用同一份基準集、同一種「涵蓋」語意
    /// （不要求逐項相等：推導改成以 EventId 為準、不再區分 FailureAudit/SuccessAudit 型別，
    /// 比原始清單多涵蓋 4625 屬無害的超集，不是漏算）。
    /// </summary>
    [Fact]
    public void 推導出的SecurityAuditWatchlist涵蓋原始寫死清單()
    {
        var legacyBaseline = new HashSet<int>
        {
            1102, 4719, 4720, 4722, 4724, 4728, 4732, 4756, 4729, 4733, 4757,
            4697, 4698, 4740, 4670, 4907, 4717, 4718, 4704, 4705, 4703, 4735, 4739, 4731, 4734
        };

        // 顯式用完整種子呼叫 Initialize（結果與預設狀態相同，所有規則皆 Enabled），
        // 讓這個測試不依賴「目前沒有其他測試呼叫過 Initialize」的執行順序假設
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

        Assert.All(legacyBaseline, id => Assert.Contains(id, KnownIssueCatalog.SecurityAuditWatchlist));
    }

    // ── C1（seed v5 補強）：規則涵蓋驗證 ───────────────────────────────────

    [Fact]
    public void 種子版本為6()
    {
        Assert.Equal(6, KnownIssueSeed.Version);
    }

    [Fact]
    public void 規則總數正確_Windows共64條_Linux共28條_PRTG共4條_總計96條()
    {
        var rules = KnownIssueSeed.CreateRules();
        var windows = rules.Where(r => r.Platform == "windows").ToList();
        var linux = rules.Where(r => r.Platform == "linux").ToList();
        var prtg = rules.Where(r => r.Platform == "prtg").ToList();

        Assert.Equal(64, windows.Count);
        Assert.Equal(28, linux.Count);
        Assert.Equal(4, prtg.Count);
        Assert.Equal(96, rules.Count);
    }

    [Fact]
    public void 新增的Security事件ID皆進入SecurityAuditWatchlist()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

        var expectedIds = new[]
        {
            4771, 4725, 4726, 4767, 4738, 4699, 4700, 4701, 4702,
            4946, 4947, 4948, 4741, 4743, 4727, 4730, 4754, 4758,
            4713, 4865, 4866, 4867, 4794
        };

        Assert.All(expectedIds, id => Assert.Contains(id, KnownIssueCatalog.SecurityAuditWatchlist));
    }

    [Fact]
    public void System日誌清除104可透過FindRule命中()
    {
        var rule = KnownIssueCatalog.FindRule("EventLog", 104);
        Assert.NotNull(rule);
        Assert.Equal("builtin-security-system-log-cleared-104", rule!.Id);
        Assert.True(rule.ElevatesDayRisk);
    }

    [Fact]
    public void 帳號解鎖4767為Medium嚴重度()
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", 4767);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.Medium, rule!.Severity);
    }

    [Theory]
    [InlineData(4700)]
    [InlineData(4701)]
    [InlineData(4702)]
    public void 排程更新4700至4702為Medium且門檻為5(int eventId)
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", eventId);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.Medium, rule!.Severity);
        Assert.Equal(5, rule.CountThreshold);
    }

    [Theory]
    [InlineData(4946)]
    [InlineData(4947)]
    [InlineData(4948)]
    public void 防火牆異動4946至4948為Medium且門檻為5(int eventId)
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", eventId);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.Medium, rule!.Severity);
        Assert.Equal(5, rule.CountThreshold);
    }

    [Fact]
    public void 帳號屬性變更4738門檻為10()
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", 4738);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.High, rule!.Severity);
        Assert.Equal(10, rule.CountThreshold);
    }

    [Fact]
    public void 日誌清除104帶重大旗標_ESENT損毀不帶重大旗標()
    {
        var rule104 = KnownIssueCatalog.FindRule("EventLog", 104);
        Assert.NotNull(rule104);
        Assert.True(rule104!.ElevatesDayRisk);

        var ruleEsent = KnownIssueCatalog.FindRule("ESENT", 467);
        Assert.NotNull(ruleEsent);
        Assert.False(ruleEsent!.ElevatesDayRisk);
    }

    [Fact]
    public void 電腦帳戶變更4742刻意不收錄於任何規則()
    {
        var rules = KnownIssueSeed.CreateRules();
        Assert.DoesNotContain(rules, r => r.EventIds.Contains(4742));
    }

    [Theory]
    [InlineData(4768)]
    [InlineData(4769)]
    [InlineData(4776)]
    public void 超大量或無EntryType的4768與4769與4776刻意不收錄於任何規則(int eventId)
    {
        var rules = KnownIssueSeed.CreateRules();
        Assert.DoesNotContain(rules, r => r.EventIds.Contains(eventId));
    }

    [Fact]
    public void Linux_kernel家族比對順序正確且回歸保護()
    {
        // 1. No space left on device 命中磁碟空間耗盡 (Resource)，不被 storage-io 或 hw-error 攔截
        var diskFullHit = KnownIssueCatalog.FindLinuxRule("kernel", null, "kernel: No space left on device on /var/log");
        Assert.NotNull(diskFullHit);
        Assert.Equal("builtin-linux-resource-disk-space", diskFullHit!.Id);
        Assert.Equal(IssueCategory.Resource, diskFullHit.Category);

        // 2. 唯讀重掛 (Storage, ElevatesDayRisk=true) 命中 remount-ro
        var remountHit = KnownIssueCatalog.FindLinuxRule("kernel", null, "EXT4-fs error (device sda1): ext4_remount: Remounting filesystem read-only");
        Assert.NotNull(remountHit);
        Assert.Equal("builtin-linux-storage-remount-ro", remountHit!.Id);
        Assert.True(remountHit.ElevatesDayRisk);

        // 3. 既有 storage-io 訊息仍命中原本的 builtin-linux-storage-io（回歸保護）
        var storageIoHit = KnownIssueCatalog.FindLinuxRule("kernel", null, "kernel: Buffer I/O error on dev sda1, logical block 1234, async page read");
        Assert.NotNull(storageIoHit);
        Assert.Equal("builtin-linux-storage-io", storageIoHit!.Id);
        Assert.Equal(IssueCategory.Storage, storageIoHit.Category);
    }

    [Fact]
    public void fail2ban封鎖規則為Low嚴重度()
    {
        var rule = KnownIssueCatalog.FindLinuxRule("fail2ban", null, "Ban 192.168.1.100");
        Assert.NotNull(rule);
        Assert.Equal("builtin-linux-security-fail2ban-ban", rule!.Id);
        Assert.Equal(IssueSeverity.Low, rule.Severity);
    }

    // ── C3（既有規則降噪調整）：比對收斂與門檻/重大調整驗證 ──────────────────

    [Theory]
    [InlineData("libuser")]
    [InlineData("gnome-user-share")]
    [InlineData("systemd-userdbd")]
    [InlineData("user@1000.service")]
    public void 帳號家族誤報反例_含user但非管理指令的daemon不命中帳號規則(string program)
    {
        var hit = KnownIssueCatalog.FindLinuxRule(program, null, "any message");
        var accountRuleIds = new[] { "builtin-linux-account-change", "builtin-linux-account-mod", "builtin-linux-account-del" };
        if (hit != null)
        {
            Assert.DoesNotContain(hit.Id, accountRuleIds);
        }
    }

    [Theory]
    [InlineData("useradd", "builtin-linux-account-change")]
    [InlineData("usermod", "builtin-linux-account-mod")]
    [InlineData("userdel", "builtin-linux-account-del")]
    public void 帳號家族真陽性維持_useradd與usermod與userdel命中各自規則(string program, string expectedRuleId)
    {
        var hit = KnownIssueCatalog.FindLinuxRule(program, null, "account action");
        Assert.NotNull(hit);
        Assert.Equal(expectedRuleId, hit!.Id);
        Assert.Equal(IssueSeverity.High, hit.Severity);
        Assert.Equal(IssueCategory.Security, hit.Category);
    }

    [Theory]
    [InlineData("groupmems-helper")]
    [InlineData("systemd-journal-group")]
    [InlineData("group-service")]
    public void 群組家族誤報反例_含group但非管理指令的daemon不命中群組規則(string program)
    {
        var hit = KnownIssueCatalog.FindLinuxRule(program, null, "any message");
        var groupRuleIds = new[] { "builtin-linux-group-mgmt", "builtin-linux-group-mod", "builtin-linux-group-del" };
        if (hit != null)
        {
            Assert.DoesNotContain(hit.Id, groupRuleIds);
        }
    }

    [Theory]
    [InlineData("groupadd", "builtin-linux-group-mgmt")]
    [InlineData("groupmod", "builtin-linux-group-mod")]
    [InlineData("groupdel", "builtin-linux-group-del")]
    public void 群組家族真陽性維持_groupadd與groupmod與groupdel命中各自規則(string program, string expectedRuleId)
    {
        var hit = KnownIssueCatalog.FindLinuxRule(program, null, "group action");
        Assert.NotNull(hit);
        Assert.Equal(expectedRuleId, hit!.Id);
        Assert.Equal(IssueSeverity.High, hit.Severity);
        Assert.Equal(IssueCategory.Security, hit.Category);
    }

    [Fact]
    public void Linux_gpasswd特權群組規則行為不變_回歸保護()
    {
        var hit = KnownIssueCatalog.FindLinuxRule("gpasswd", null, "gpasswd -a testadmin wheel");
        Assert.NotNull(hit);
        Assert.Equal("builtin-linux-priv-group-membership", hit!.Id);
        Assert.Equal(IssueSeverity.High, hit.Severity);
    }

    [Fact]
    public void Security_4703權杖權限調整門檻為10_達門檻為High_未達門檻降為Medium()
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", 4703);
        Assert.NotNull(rule);
        Assert.Equal(10, rule!.CountThreshold);
        Assert.Equal(IssueSeverity.High, rule.Severity);
        Assert.False(rule.ElevatesDayRisk);

        // 達門檻 (10 筆) 為 High
        var logs10 = MakeEntries(10, "Security-Auditing", 4703, EventLogEntryType.SuccessAudit);
        var sig10 = LogAggregator.Aggregate(logs10).FirstOrDefault(s => s.EventId == 4703);
        Assert.NotNull(sig10);
        Assert.Equal(IssueSeverity.High, sig10!.Severity);

        // 未達門檻 (1 筆) 降為 Medium
        var logs1 = MakeEntries(1, "Security-Auditing", 4703, EventLogEntryType.SuccessAudit);
        var sig1 = LogAggregator.Aggregate(logs1).FirstOrDefault(s => s.EventId == 4703);
        Assert.NotNull(sig1);
        Assert.Equal(IssueSeverity.Medium, sig1!.Severity);
    }

    [Fact]
    public void Security_4907物件SACL稽核設定變更不帶重大旗標且嚴重度為High()
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", 4907);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.High, rule!.Severity);
        Assert.False(rule.ElevatesDayRisk);
    }

    [Fact]
    public void Security_1102安全稽核日誌清除維持重大旗標_回歸保護()
    {
        var rule = KnownIssueCatalog.FindRule("Security-Auditing", 1102);
        Assert.NotNull(rule);
        Assert.Equal(IssueSeverity.High, rule!.Severity);
        Assert.True(rule.ElevatesDayRisk);
    }

    private static EventLogEntryType InferEntryType(string source, int eventId)
    {
        if (!source.Equals("Security-Auditing", StringComparison.OrdinalIgnoreCase))
        {
            return EventLogEntryType.Error;
        }
        return eventId is 4625 or 4740 or 4771 ? EventLogEntryType.FailureAudit : EventLogEntryType.SuccessAudit;
    }

    private static List<EventLogEntryData> MakeEntries(int count, string source, int eventId, EventLogEntryType entryType)
        => Enumerable.Range(0, count)
            .Select(i => new EventLogEntryData
            {
                TimeGenerated = DateTime.Today.AddMinutes(i),
                LogName = "System",
                Source = source,
                EventId = eventId,
                EntryType = entryType,
                Message = $"test event #{i}",
                InstanceId = eventId
            })
            .ToList();

    [Fact]
    public void 種子含四條PRTG規則且全部通過RuleValidator()
    {
        var rules = KnownIssueSeed.CreateRules();
        var prtgRules = rules.Where(r => r.Platform == "prtg").ToList();

        Assert.Equal(4, prtgRules.Count);
        Assert.Contains(prtgRules, r => r.Id == "builtin-prtg-down" && r.PrtgRuleCode == PrtgRuleEvaluator.RuleDown && r.PrtgThreshold == 60);
        Assert.Contains(prtgRules, r => r.Id == "builtin-prtg-flapping" && r.PrtgRuleCode == PrtgRuleEvaluator.RuleFlapping && r.PrtgThreshold == 5);
        Assert.Contains(prtgRules, r => r.Id == "builtin-prtg-warning" && r.PrtgRuleCode == PrtgRuleEvaluator.RuleWarning && r.PrtgThreshold == 240);
        Assert.Contains(prtgRules, r => r.Id == "builtin-prtg-silent" && r.PrtgRuleCode == PrtgRuleEvaluator.RuleSilent && r.PrtgThreshold == 0);

        var outcome = RuleValidator.Validate(prtgRules);
        Assert.Equal(4, outcome.ValidRules.Count);
        Assert.Empty(outcome.SkippedRules);
        Assert.Empty(outcome.ShadowWarnings);
    }

    [Fact]
    public void CloneForSeedOverwrite_保留PRTG欄位_突變防護()
    {
        var original = new KnownIssueRule
        {
            Id = "builtin-prtg-down",
            Origin = "builtin",
            Enabled = true,
            Scope = "all",
            Platform = "prtg",
            PrtgRuleCode = PrtgRuleEvaluator.RuleDown,
            PrtgThreshold = 120,
            Category = IssueCategory.Service,
            Severity = IssueSeverity.High,
            ElevatesDayRisk = true,
            Description = "持續 Down",
            CountThreshold = 1,
            PlainExplanation = "白話說明",
            Impact = "影響",
            LikelyCauses = new[] { "原因" },
            NextSteps = new[] { "步驟" }
        };

        var cloned = original.CloneForSeedOverwrite(enabled: false);

        Assert.Equal(original.Id, cloned.Id);
        Assert.False(cloned.Enabled);
        Assert.Equal("prtg", cloned.Platform);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, cloned.PrtgRuleCode);
        Assert.Equal(120, cloned.PrtgThreshold);
    }
}
