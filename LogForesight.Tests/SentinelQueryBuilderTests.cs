using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelQueryBuilder：規則表→Lucene filter 的純函數產生器（docs/NETIQ-API-REFERENCE.md §4）。
/// 多數案例用建構出來的規則清單測試，不碰 KnownIssueCatalog 共用靜態狀態；
/// 檔尾另有一組對真實種子規則表的結構性驗證（自已退場的 console selftest
/// RunSentinelQueryChecks 移植而來，docs/archive/WEB-SCHEDULER-PLAN.md §1.5）。
/// </summary>
public class SentinelQueryBuilderTests
{
    private static KnownIssueRule WindowsRule(string id, int[] eventIds, bool enabled = true, bool matchAll = false) => new()
    {
        Id = id,
        Platform = "windows",
        Enabled = enabled,
        MatchAllEventIds = matchAll,
        EventIds = eventIds,
        SourcePattern = "Test-Provider",
        Category = IssueCategory.Security
    };

    private static KnownIssueRule LinuxRule(string id) => new()
    {
        Id = id,
        Platform = "linux",
        Enabled = true,
        ProgramPattern = "sshd",
        Category = IssueCategory.Security
    };

    private static KnownIssueRule LinuxRule(
        string id, string program, string[]? messagePatterns = null, bool enabled = true) => new()
    {
        Id = id,
        Platform = "linux",
        Enabled = enabled,
        ProgramPattern = program,
        MessagePatterns = messagePatterns ?? Array.Empty<string>(),
        Category = IssueCategory.Security
    };

    [Fact]
    public void BuildWindowsFilter_含IP批次子句與AND連接內容子句()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(
            new[] { "10.1.2.11", "10.1.2.12" }, new[] { WindowsRule("r1", new[] { 4625 }) });

        Assert.StartsWith("(repip:10.1.2.11 OR repip:10.1.2.12)", filter);
        Assert.Contains(" AND ", filter);
    }

    [Fact]
    public void BuildWindowsFilter_空IP清單擲例外()
    {
        Assert.Throws<ArgumentException>(() =>
            SentinelQueryBuilder.BuildWindowsFilter(Array.Empty<string>(), Enumerable.Empty<KnownIssueRule>()));
    }

    [Fact]
    public void BuildWindowsFilter_無規則時只有generic分支()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(new[] { "10.1.2.11" }, Enumerable.Empty<KnownIssueRule>());

        Assert.DoesNotContain("rv40:(", filter);
        Assert.Contains("rv150:System OR rv150:Application", filter);
        Assert.Contains($"sev:[{SentinelQueryBuilder.GenericErrorSeverityMin} TO 5]", filter);
    }

    [Fact]
    public void BuildWindowsFilter_有規則時rv40子句與generic分支皆存在_以OR連接()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(
            new[] { "10.1.2.11" }, new[] { WindowsRule("r1", new[] { 4625, 4720 }) });

        Assert.Contains("rv40:(4625 OR 4720)", filter);
        Assert.Contains(" OR (", filter);   // rv40 子句與 generic 子句以 OR 連接
    }

    [Fact]
    public void WindowsRuleEventIds_排除停用規則()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("enabled", new[] { 100 }),
            WindowsRule("disabled", new[] { 200 }, enabled: false)
        });

        Assert.Contains(100, ids);
        Assert.DoesNotContain(200, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_排除MatchAllEventIds規則()
    {
        // WHEA-Logger／Resource-Exhaustion／VSS 這類規則沒有具體 EventId 可下推，
        // 就算欄位上填了 EventIds 也不該混進聯集（防禦性測試：現行種子這類規則 EventIds 恆空，
        // 但函數本身的契約不該依賴這個巧合）
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("matchall", new[] { 999 }, matchAll: true)
        });

        Assert.DoesNotContain(999, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_排除Linux規則()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new KnownIssueRule[]
        {
            WindowsRule("win", new[] { 100 }),
            LinuxRule("linux")
        });

        Assert.Single(ids);
        Assert.Contains(100, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_跨規則聯集去重且升冪排序()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("r1", new[] { 300, 100 }),
            WindowsRule("r2", new[] { 100, 200 })   // 100 與 r1 重複
        });

        Assert.Equal(new[] { 100, 200, 300 }, ids);
    }

    [Fact]
    public void BuildIpClause_單一IP不含多餘OR()
    {
        var clause = SentinelQueryBuilder.BuildIpClause(new[] { "10.1.2.11" });

        Assert.Equal("(repip:10.1.2.11)", clause);
    }

    // ── Linux（docs/archive/FEEDBACK-12-PLAN.md §4.4，四輪 probe 實證定案）───────────────

    [Fact]
    public void BuildLinuxFilter_含IP批次子句與AND連接內容子句()
    {
        var filter = SentinelQueryBuilder.BuildLinuxFilter(
            new[] { "10.1.2.11", "10.1.2.12" }, new[] { LinuxRule("r1", "sshd", new[] { "Failed password" }) });

        Assert.StartsWith("(repip:10.1.2.11 OR repip:10.1.2.12)", filter);
        Assert.Contains(" AND ", filter);
    }

    [Fact]
    public void BuildLinuxFilter_空IP清單擲例外()
    {
        Assert.Throws<ArgumentException>(() =>
            SentinelQueryBuilder.BuildLinuxFilter(Array.Empty<string>(), Enumerable.Empty<KnownIssueRule>()));
    }

    [Fact]
    public void BuildLinuxFilter_無規則時只有generic分支()
    {
        var filter = SentinelQueryBuilder.BuildLinuxFilter(new[] { "10.1.2.11" }, Enumerable.Empty<KnownIssueRule>());

        Assert.DoesNotContain($"{SentinelFieldMap.LinuxProgram}:", filter);
        Assert.Contains($"{SentinelFieldMap.Severity}:[{SentinelQueryBuilder.LinuxGenericSeverityMin} TO 5]", filter);
    }

    [Fact]
    public void LinuxRuleProgramClauses_靜program整拉不論有無MessagePatterns()
    {
        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[]
        {
            LinuxRule("quiet-no-msg", "user"),
            LinuxRule("quiet-with-msg", "chronyd", new[] { "Can't synchronise" })
        });

        Assert.Contains($"{SentinelFieldMap.LinuxProgram}:user*", clauses);
        Assert.Contains($"{SentinelFieldMap.LinuxProgram}:chronyd*", clauses);
        Assert.DoesNotContain(clauses, c => c.Contains("msg:"));
    }

    [Fact]
    public void LinuxRuleProgramClauses_吵program帶msg片語下推()
    {
        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[]
        {
            LinuxRule("ssh-bruteforce", "sshd", new[] { "Failed password", "Invalid user" })
        });

        var sshdClause = Assert.Single(clauses);
        Assert.Contains($"{SentinelFieldMap.LinuxProgram}:sshd*", sshdClause);
        Assert.Contains($"{SentinelFieldMap.Message}:(", sshdClause);
        Assert.Contains("\"Failed password\"", sshdClause);
        Assert.Contains("\"Invalid user\"", sshdClause);
        Assert.Contains(" AND ", sshdClause);
    }

    [Fact]
    public void LinuxRuleProgramClauses_同program多規則的MessagePatterns聯集去重()
    {
        // sshd 底下 ssh-bruteforce 與 ssh-accept 兩條規則各自的片語要合併成一個 sp:sshd* 子句，
        // 不是兩個獨立子句——否則 filter 會對同一個 program 查兩次
        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[]
        {
            LinuxRule("ssh-bruteforce", "sshd", new[] { "Failed password", "Invalid user" }),
            LinuxRule("ssh-accept", "sshd", new[] { "Accepted password" })
        });

        var sshdClause = Assert.Single(clauses);
        Assert.Contains("\"Failed password\"", sshdClause);
        Assert.Contains("\"Invalid user\"", sshdClause);
        Assert.Contains("\"Accepted password\"", sshdClause);
    }

    [Fact]
    public void LinuxRuleProgramClauses_排除停用規則與Windows規則()
    {
        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[]
        {
            LinuxRule("disabled", "sudo", new[] { "authentication failure" }, enabled: false),
            WindowsRule("win", new[] { 100 })
        });

        Assert.Empty(clauses);
    }

    [Fact]
    public void LinuxRuleProgramClauses_MessagePatterns含跳脫字元時包成合法Lucene片語()
    {
        // chronyd 是靜 program（不下推 msg），改用一個假想的吵 program 規則驗證跳脫邏輯本身，
        // 不依賴種子規則表當下是否剛好有帶特殊字元的吵 program 片語
        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[]
        {
            LinuxRule("kernel-test", "kernel", new[] { "mce: [Hardware Error]", "a \"quoted\" phrase" })
        });

        var kernelClause = Assert.Single(clauses);
        Assert.Contains("\"mce: [Hardware Error]\"", kernelClause);
        Assert.Contains("\\\"quoted\\\"", kernelClause); // 內嵌的雙引號被跳脫，不會提早結束片語
    }

    [Fact]
    public void BuildSubnetProbeFilter_Linux時退回sev子句而非頻道子句()
    {
        var filter = SentinelQueryBuilder.BuildSubnetProbeFilter("10.1.2.0/24", os: WebHost.OsLinux);

        Assert.Equal($"(repip:10.1.2.* AND {SentinelFieldMap.Severity}:[0 TO 5])", filter);
        Assert.DoesNotContain("rv150", filter);
    }

    [Fact]
    public void BuildSubnetProbeFilter_未指定os時維持既有Windows行為()
    {
        // 既有呼叫端（未改動前）不會傳 os，預設值必須讓輸出逐字不變——契約回歸
        var filter = SentinelQueryBuilder.BuildSubnetProbeFilter("10.1.2.0/24");

        Assert.Equal("(repip:10.1.2.* AND (rv150:System OR rv150:Application))", filter);
    }

    /// <summary>真實種子規則表建出的 Linux filter：吵/靜 program 分組符合輪 B 第 5 項的環境實測
    /// （docs/archive/FEEDBACK-12-PLAN.md §4.4）——這組是規則表演進時的護欄，不是重新驗證分組邏輯本身
    /// （那些在上面用合成規則測過了）。</summary>
    [Fact]
    public void 種子規則表建出的LinuxFilter吵program帶msg下推靜program整拉()
    {
        var seedRules = KnownIssueSeed.CreateRules();
        var filter = SentinelQueryBuilder.BuildLinuxFilter(new[] { "10.1.2.11" }, seedRules);

        // 吵 program：sshd/sudo/su/kernel/systemd 都應該帶 msg 下推
        foreach (var noisy in new[] { "sshd", "sudo", "su", "kernel", "systemd" })
        {
            Assert.Contains($"{SentinelFieldMap.LinuxProgram}:{noisy}* AND {SentinelFieldMap.Message}:(", filter);
        }

        // 靜 program：帳號異動類（無 MessagePatterns）應該只有整拉，不帶 msg 子句
        Assert.Contains($"{SentinelFieldMap.LinuxProgram}:user*", filter);
        Assert.DoesNotContain($"{SentinelFieldMap.LinuxProgram}:user* AND", filter);
    }

    // ── NormalizeSubnetPrefix／BuildSubnetDiscoveryFilter（網段範圍探索，docs/NETIQ-API-REFERENCE.md §3.4）──

    [Theory]
    [InlineData("10.1", "10.1")]
    [InlineData("10.1.2", "10.1.2")]
    [InlineData(" 10.1.2 ", "10.1.2")]         // 前後空白
    [InlineData("10.1.0/16", "10.1")]             // CIDR /16 → 2 段
    [InlineData("10.1.2.0/24", "10.1.2")]       // CIDR /24 → 3 段
    [InlineData("10.1.2.99/24", "10.1.2")]      // CIDR 多打的第四段被截斷
    public void NormalizeSubnetPrefix_合法輸入正確正規化(string input, string expected)
    {
        Assert.Equal(expected, SentinelQueryBuilder.NormalizeSubnetPrefix(input));
    }

    [Fact]
    public void BuildSubnetDiscoveryFilter_組出repip前綴萬用字元查詢()
    {
        var filter = SentinelQueryBuilder.BuildSubnetDiscoveryFilter("10.1.2.0/24");

        Assert.Equal("repip:10.1.2.*", filter);
    }

    // ── 探索窄化與排除（docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.1）──────────

    /// <summary>
    /// 主掃描窄化到低量頻道：探索要的是「每台主機至少一筆事件」，不是「所有事件」。
    /// probe 實測 Security 佔單台日量的 99.95%，排除它之後取回量從正比於事件量
    /// 變成正比於主機數——這正是不必再壓縮掃描窗口（＝不再靜默漏機）的前提。
    /// </summary>
    [Fact]
    public void BuildSubnetProbeFilter_網段前綴加上低量頻道限定()
    {
        var filter = SentinelQueryBuilder.BuildSubnetProbeFilter("10.1.2.0/24");

        Assert.Equal("(repip:10.1.2.* AND (rv150:System OR rv150:Application))", filter);
    }

    [Fact]
    public void BuildSubnetProbeFilter_帶排除清單時附加NOT子句()
    {
        var filter = SentinelQueryBuilder.BuildSubnetProbeFilter("10.1.2", new[] { "10.1.2.11", "10.1.2.12" });

        Assert.Equal(
            "(repip:10.1.2.* AND (rv150:System OR rv150:Application)) AND NOT (repip:10.1.2.11 OR repip:10.1.2.12)",
            filter);
    }

    [Fact]
    public void BuildSubnetDiscoveryFilter_帶排除清單時附加NOT子句()
    {
        var filter = SentinelQueryBuilder.BuildSubnetDiscoveryFilter("10.1.2", new[] { "10.1.2.11" });

        Assert.Equal("repip:10.1.2.* AND NOT (repip:10.1.2.11)", filter);
    }

    [Fact]
    public void BuildExclusionSuffix_空清單不產生子句()
    {
        Assert.Equal(string.Empty, SentinelQueryBuilder.BuildExclusionSuffix(null));
        Assert.Equal(string.Empty, SentinelQueryBuilder.BuildExclusionSuffix(Array.Empty<string>()));
    }

    /// <summary>
    /// 髒資料略過而不是擲例外：排除清單來自既有主機資料，一筆壞的不該讓整趟探索失敗。
    /// 漏排除的後果只是多取回一點事件（不影響正確性）；擲例外的後果是探索整個不能用。
    /// </summary>
    [Theory]
    [InlineData("10.1.2")]              // 段數不足
    [InlineData("10.1.2.300")]          // 超出 0~255
    [InlineData("10.1.2.*")]            // 萬用字元不是一台明確的主機
    [InlineData("host-a")]              // 根本不是 IP
    [InlineData("")]
    public void BuildExclusionSuffix_不合法IP直接略過(string ip)
    {
        Assert.Equal(string.Empty, SentinelQueryBuilder.BuildExclusionSuffix(new[] { ip }));
    }

    [Fact]
    public void BuildExclusionSuffix_混合清單只留合法的且去重()
    {
        var suffix = SentinelQueryBuilder.BuildExclusionSuffix(
            new[] { "10.1.2.11", "壞資料", "10.1.2.11", "10.1.2.12" });

        Assert.Equal(" AND NOT (repip:10.1.2.11 OR repip:10.1.2.12)", suffix);
    }

    [Fact]
    public void BuildSubnetProbeFilter_非法網段仍擲ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.BuildSubnetProbeFilter("10"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSubnetPrefix_空白輸入擲例外(string input)
    {
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix(input));
    }

    [Fact]
    public void NormalizeSubnetPrefix_單段前綴太籠統擲例外()
    {
        // 只有一段等同「查所有 10.x.x.x」，量級與全站同一數量級
        var ex = Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix("10"));
        Assert.Contains("太籠統", ex.Message);
    }

    [Fact]
    public void NormalizeSubnetPrefix_完整四段IP不帶CIDR時擲例外()
    {
        // 這是探索「網段」的功能，不是查單一台主機——repip:x.x.x.x.* 語法上也不合法
        var ex = Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix("10.1.2.5"));
        Assert.Contains("單一 IP", ex.Message);
    }

    [Theory]
    [InlineData("10.1.2.0/25")]   // 不對齊八位組邊界
    [InlineData("10.1.2.0/8")]    // 本函數不支援 /8（太籠統，交由段數檢查統一擋）
    [InlineData("10.1.2.0/abc")]  // 非數字
    public void NormalizeSubnetPrefix_不支援的CIDR遮罩擲例外(string input)
    {
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix(input));
    }

    [Theory]
    [InlineData("10.256.11")]        // 八位組超過 255
    [InlineData("10.-1.11")]         // 負數
    [InlineData("abc.def.gh")]       // 非數字
    [InlineData("10..11")]           // 空段
    [InlineData("10.1.2; DROP")]  // 注入嘗試——白名單天然免疫，這裡驗證確實被擋下而不是被靜默接受
    public void NormalizeSubnetPrefix_格式不正確擲例外(string input)
    {
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix(input));
    }

    [Fact]
    public void NormalizeSubnetPrefix_CIDR位元數大於實際段數時擲例外()
    {
        // /24 需要 3 段，只給 2 段
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.NormalizeSubnetPrefix("10.1/24"));
    }

    // ── 真實種子規則表的結構性驗證（自已退場的 console selftest RunSentinelQueryChecks
    //    移植而來，docs/archive/WEB-SCHEDULER-PLAN.md §1.5）：上面的案例用合成規則驗產生器邏輯，
    //    這組驗「目前這個版本的種子規則表」建出的 filter 結構正確——規則表演進時的護欄。──

    /// <summary>Security-Auditing 規則的原始寫死清單（規則外部化前的版本），與
    /// KnownIssueCatalogTests／CorrelationAnalyzerRuleAlignmentTests 用同一份基準集。</summary>
    private static readonly int[] LegacySecurityWatchlistBaseline =
    {
        1102, 4719, 4720, 4722, 4724, 4728, 4732, 4756, 4729, 4733, 4757,
        4697, 4698, 4740, 4670, 4907, 4717, 4718, 4704, 4705, 4703, 4735, 4739, 4731, 4734
    };

    [Fact]
    public void 種子規則表建出的filter含IP批次與generic收集子句()
    {
        var sampleIps = new[] { "192.168.0.11", "192.168.0.12" };
        var filter = SentinelQueryBuilder.BuildWindowsFilter(sampleIps, KnownIssueSeed.CreateRules());

        Assert.All(sampleIps, ip => Assert.Contains($"{SentinelFieldMap.HostIp}:{ip}", filter));
        Assert.Contains($"{SentinelFieldMap.LogName}:System", filter);
        Assert.Contains($"{SentinelFieldMap.LogName}:Application", filter);
    }

    [Fact]
    public void 種子規則表有Windows事件ID可下推Lucene()
    {
        // 空清單＝全部只能靠 generic 收集，規則來源下推整個失效
        var eventIds = SentinelQueryBuilder.WindowsRuleEventIds(KnownIssueSeed.CreateRules());

        Assert.NotEmpty(eventIds);
    }

    [Fact]
    public void 基準SecurityID存在於種子規則表者皆反映在filter的rv40子句()
    {
        var seedRules = KnownIssueSeed.CreateRules();
        var eventIds = SentinelQueryBuilder.WindowsRuleEventIds(seedRules);
        var filter = SentinelQueryBuilder.BuildWindowsFilter(new[] { "192.168.0.11" }, seedRules);

        var expectedInFilter = LegacySecurityWatchlistBaseline.Intersect(eventIds).ToList();
        Assert.NotEmpty(expectedInFilter); // 交集空了代表基準清單與規則表已嚴重脫節，本測試失去意義
        Assert.Contains($"{SentinelFieldMap.EventId}:(", filter);
        Assert.All(expectedInFilter, id => Assert.Contains(id.ToString(), filter));
    }

    [Fact]
    public void MatchAllEventIds規則的事件ID未混入下推聯集()
    {
        // MatchAllEventIds 規則（WHEA-Logger／Resource-Exhaustion／VSS）沒有具體 EventId 可下推，
        // 不該混進聯集——它們的事件靠 generic 分支撈、本地 Classify 精準比對
        var seedRules = KnownIssueSeed.CreateRules();
        var eventIds = SentinelQueryBuilder.WindowsRuleEventIds(seedRules);
        var matchAllOnlyIds = seedRules.Where(r => r.MatchAllEventIds).SelectMany(r => r.EventIds)
            .Where(id => !seedRules.Any(r => !r.MatchAllEventIds && r.EventIds.Contains(id)));

        Assert.All(matchAllOnlyIds, id => Assert.DoesNotContain(id, eventIds));
    }

    // ── ExpandToSlash24Prefixes（/24 網段展開，task-B2-step1）──────────────────

    [Fact]
    public void ExpandToSlash24Prefixes_兩段前綴展開成256個子網段()
    {
        var segments = SentinelQueryBuilder.ExpandToSlash24Prefixes("192.168");

        Assert.Equal(256, segments.Count);
        Assert.Equal("192.168.0", segments[0]);
        Assert.Equal("192.168.255", segments[255]);
        Assert.Equal("192.168.1", segments[1]);
    }

    [Fact]
    public void ExpandToSlash24Prefixes_三段前綴回傳單元素清單()
    {
        var segments = SentinelQueryBuilder.ExpandToSlash24Prefixes("192.168.1");

        var segment = Assert.Single(segments);
        Assert.Equal("192.168.1", segment);
    }

    [Theory]
    [InlineData("192.168.0.0/16", 256, "192.168.0", "192.168.255")]
    [InlineData("192.168.0.0/24", 1, "192.168.0", "192.168.0")]
    [InlineData("10.1.2.0/24", 1, "10.1.2", "10.1.2")]
    public void ExpandToSlash24Prefixes_CIDR輸入正確展開(string input, int expectedCount, string first, string last)
    {
        var segments = SentinelQueryBuilder.ExpandToSlash24Prefixes(input);

        Assert.Equal(expectedCount, segments.Count);
        Assert.Equal(first, segments.First());
        Assert.Equal(last, segments.Last());
    }

    [Theory]
    [InlineData("10")]               // 太籠統（只有一段）
    [InlineData("192.168.1.1")]      // 完整四段單一 IP
    [InlineData("192.168.0.0/25")]   // 不支援的 CIDR
    [InlineData("invalid-subnet")]   // 非法字串
    [InlineData("")]                 // 空字串
    public void ExpandToSlash24Prefixes_不合法輸入擲ArgumentException(string input)
    {
        Assert.Throws<ArgumentException>(() => SentinelQueryBuilder.ExpandToSlash24Prefixes(input));
    }

    // ── ExpandToSegments（粒度展開，task-B2b-step1）───────────────────────────

    [Fact]
    public void ExpandToSegments_Slash24產生單段且Ips為null()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168.7", ScanGranularity.Slash24);

        var seg = Assert.Single(segments);
        Assert.Equal("192.168.7", seg.Prefix);
        Assert.Equal("192.168.7", seg.Label);
        Assert.Null(seg.Ips);
    }

    [Fact]
    public void ExpandToSegments_Slash25產生2段首末位址正確()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168.7", ScanGranularity.Slash25);

        Assert.Equal(2, segments.Count);

        var seg0 = segments[0];
        Assert.Equal("192.168.7", seg0.Prefix);
        Assert.Equal("192.168.7.0/25", seg0.Label);
        Assert.NotNull(seg0.Ips);
        Assert.Equal(128, seg0.Ips.Count);
        Assert.Equal("192.168.7.0", seg0.Ips[0]);
        Assert.Equal("192.168.7.127", seg0.Ips[127]);

        var seg1 = segments[1];
        Assert.Equal("192.168.7", seg1.Prefix);
        Assert.Equal("192.168.7.128/25", seg1.Label);
        Assert.NotNull(seg1.Ips);
        Assert.Equal(128, seg1.Ips.Count);
        Assert.Equal("192.168.7.128", seg1.Ips[0]);
        Assert.Equal("192.168.7.255", seg1.Ips[127]);
    }

    [Fact]
    public void ExpandToSegments_Slash26產生4段首位址正確()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168.7", ScanGranularity.Slash26);

        Assert.Equal(4, segments.Count);

        var expectedLabels = new[] { "192.168.7.0/26", "192.168.7.64/26", "192.168.7.128/26", "192.168.7.192/26" };
        var expectedFirstIps = new[] { "192.168.7.0", "192.168.7.64", "192.168.7.128", "192.168.7.192" };
        var expectedLastIps = new[] { "192.168.7.63", "192.168.7.127", "192.168.7.191", "192.168.7.255" };

        for (var i = 0; i < 4; i++)
        {
            var seg = segments[i];
            Assert.Equal("192.168.7", seg.Prefix);
            Assert.Equal(expectedLabels[i], seg.Label);
            Assert.NotNull(seg.Ips);
            Assert.Equal(64, seg.Ips.Count);
            Assert.Equal(expectedFirstIps[i], seg.Ips[0]);
            Assert.Equal(expectedLastIps[i], seg.Ips[63]);
        }
    }

    [Fact]
    public void ExpandToSegments_兩段前綴配合Slash26展開為1024段()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168", ScanGranularity.Slash26);

        Assert.Equal(1024, segments.Count);
        Assert.Equal("192.168.0", segments[0].Prefix);
        Assert.Equal("192.168.0.0/26", segments[0].Label);
        Assert.Equal("192.168.255", segments[1023].Prefix);
        Assert.Equal("192.168.255.192/26", segments[1023].Label);
    }

    [Fact]
    public void BuildSubnetDiscoveryFilter與BuildSubnetProbeFilter_Slash26明列OR且不含萬用字元()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168.7", ScanGranularity.Slash26);
        var seg0 = segments[0];

        var discoveryFilter = SentinelQueryBuilder.BuildSubnetDiscoveryFilter(seg0);
        Assert.StartsWith("(repip:192.168.7.0 OR repip:192.168.7.1 OR ", discoveryFilter);
        Assert.DoesNotContain("192.168.7.*", discoveryFilter);

        var probeFilter = SentinelQueryBuilder.BuildSubnetProbeFilter(seg0);
        Assert.StartsWith("((repip:192.168.7.0 OR repip:192.168.7.1 OR ", probeFilter);
        Assert.Contains(" AND (rv150:System OR rv150:Application))", probeFilter);
        Assert.DoesNotContain("192.168.7.*", probeFilter);
    }

    [Fact]
    public void BuildSubnetDiscoveryFilter與BuildSubnetProbeFilter_Slash24仍使用萬用字元()
    {
        var segments = SentinelQueryBuilder.ExpandToSegments("192.168.7", ScanGranularity.Slash24);
        var seg = segments[0];

        var discoveryFilter = SentinelQueryBuilder.BuildSubnetDiscoveryFilter(seg);
        Assert.Equal("repip:192.168.7.*", discoveryFilter);

        var probeFilter = SentinelQueryBuilder.BuildSubnetProbeFilter(seg);
        Assert.Equal("(repip:192.168.7.* AND (rv150:System OR rv150:Application))", probeFilter);
    }

    [Theory]
    [InlineData("26", ScanGranularity.Slash26)]
    [InlineData("/26", ScanGranularity.Slash26)]
    [InlineData("Slash26", ScanGranularity.Slash26)]
    [InlineData("25", ScanGranularity.Slash25)]
    [InlineData("/25", ScanGranularity.Slash25)]
    [InlineData("Slash25", ScanGranularity.Slash25)]
    [InlineData("24", ScanGranularity.Slash24)]
    [InlineData("/24", ScanGranularity.Slash24)]
    [InlineData("Slash24", ScanGranularity.Slash24)]
    [InlineData(null, ScanGranularity.Slash24)]
    [InlineData("", ScanGranularity.Slash24)]
    [InlineData("   ", ScanGranularity.Slash24)]
    [InlineData("abc", ScanGranularity.Slash24)]
    [InlineData("/27", ScanGranularity.Slash24)]
    public void ParseGranularity_各種格式正確解析與容錯(string? input, ScanGranularity expected)
    {
        Assert.Equal(expected, SentinelQueryBuilder.ParseGranularity(input));
    }
}
