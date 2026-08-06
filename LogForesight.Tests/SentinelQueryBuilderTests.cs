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

    // ── 探索窄化與排除（docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.1）──────────

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
}
