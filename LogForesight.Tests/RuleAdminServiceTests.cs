using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規則維護（docs/WEB-SPEC.md §9.7）。
///
/// 釘住 2026-07-21 定案的四層保護：builtin 可停用、可修改、**不可刪除**、可回復預設；
/// custom 全權。以及「儲存前驗證擋壞資料」——把 --selftest 的檢查內建進儲存路徑。
/// </summary>
public class RuleAdminServiceTests
{
    private readonly FakeRuleStore _rules = new();
    private readonly FakeRuleSeedStore _seeds = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeAuditService _audit = new();

    private const string BuiltinId = "builtin-disk-153";

    public RuleAdminServiceTests()
    {
        var builtin = new KnownIssueRule
        {
            Id = BuiltinId,
            Origin = "builtin",
            Enabled = true,
            SourcePattern = "disk",
            EventIds = new[] { 153 },
            Category = IssueCategory.Storage,
            Severity = IssueSeverity.Critical,
            Description = "磁碟 I/O 錯誤",
            CountThreshold = 1,
            PlainExplanation = "硬碟可能即將故障",
            Impact = "資料遺失風險",
            LikelyCauses = new[] { "磁區損壞" },
            NextSteps = new[] { "更換硬碟" }
        };

        _rules.Content = new RuleFileContent { SeedVersion = 1, Rules = new List<KnownIssueRule> { builtin } };
        _seeds.Sync(new[] { builtin }, seedVersion: 1);
    }

    private RuleAdminService Create() =>
        new(_rules, _seeds, _suppressions, _users, FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);

    private static SaveRuleRequest ValidRequest(string id = "custom-test") => new()
    {
        Id = id,
        Enabled = true,
        SourcePattern = "MyApp",
        EventIds = new List<int> { 9001 },
        Category = "Service",
        Severity = "Medium",
        Description = "自訂應用程式錯誤",
        CountThreshold = 3,
        PlainExplanation = "應用程式發生錯誤",
        Impact = "服務可能中斷",
        LikelyCauses = new List<string> { "設定錯誤" },
        NextSteps = new List<string> { "檢查設定檔" }
    };

    // ── 四層保護 ─────────────────────────────────────────────────────────────

    [Fact]
    public void builtin規則_不可刪除()
    {
        var ex = Assert.Throws<DomainException>(() => Create().DeleteRule(BuiltinId));

        Assert.Contains("內建規則", ex.Message);
        Assert.Contains("停用", ex.Message);
        Assert.Single(_rules.Content.Rules);
    }

    [Fact]
    public void custom規則_可刪除()
    {
        var service = Create();
        service.SaveRule(ValidRequest());

        service.DeleteRule("custom-test");

        Assert.DoesNotContain(_rules.Content.Rules, r => r.Id == "custom-test");
    }

    [Fact]
    public void 刪除custom規則_連同抑制設定一併清除()
    {
        var service = Create();
        service.SaveRule(ValidRequest());
        service.AddSuppression("custom-test", new AddSuppressionRequest { Host = "SRV-01", Reason = "已知雜訊" });

        service.DeleteRule("custom-test");

        Assert.Empty(_suppressions.LoadAll());
    }

    [Fact]
    public void builtin規則_可停用且可再啟用()
    {
        var service = Create();

        service.SetEnabled(BuiltinId, false);
        Assert.False(_rules.Content.Rules.Single().Enabled);

        service.SetEnabled(BuiltinId, true);
        Assert.True(_rules.Content.Rules.Single().Enabled);
    }

    /// <summary>「已修改」徽章指內容被改過；只停用/啟用不該掛上它——
    /// 那會讓人誤以為程式改版時這條需要人工比對差異</summary>
    [Fact]
    public void 只停用builtin_不標記為已修改()
    {
        var service = Create();

        service.SetEnabled(BuiltinId, false);

        var rule = service.GetRules().Single(r => r.Id == BuiltinId);
        Assert.False(rule.IsModified);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void builtin規則_可修改並標記已修改()
    {
        var service = Create();
        var request = ValidRequest(BuiltinId);
        request.Description = "改過的說明";

        var result = service.SaveRule(request);

        Assert.True(result.IsModified);
        Assert.Equal("改過的說明", _rules.Content.Rules.Single().Description);
        // Origin 不可被修改——它決定這條規則會不會被 --import-rules 覆寫
        Assert.Equal("builtin", _rules.Content.Rules.Single().Origin);
    }

    [Fact]
    public void 回復預設_還原內容()
    {
        var service = Create();
        var request = ValidRequest(BuiltinId);
        request.Description = "改壞的說明";
        request.SourcePattern = "wrong";
        service.SaveRule(request);

        service.RestoreSeed(BuiltinId);

        var restored = _rules.Content.Rules.Single();
        Assert.Equal("磁碟 I/O 錯誤", restored.Description);
        Assert.Equal("disk", restored.SourcePattern);
        Assert.False(restored.ModifiedAt.HasValue);
    }

    /// <summary>回復內容不等於重新啟用——沿用 --overwrite-builtin 的既有語意，停用不會被悄悄打開</summary>
    [Fact]
    public void 回復預設_保留使用者的停用設定()
    {
        var service = Create();
        service.SetEnabled(BuiltinId, false);

        service.RestoreSeed(BuiltinId);

        Assert.False(_rules.Content.Rules.Single().Enabled);
    }

    [Fact]
    public void 回復預設_預覽列出欄位差異()
    {
        var service = Create();
        var request = ValidRequest(BuiltinId);
        request.Description = "改過的說明";
        service.SaveRule(request);

        var preview = service.PreviewRestore(BuiltinId);

        Assert.Contains(preview.Differences, d => d.Field == "說明" && d.Seed == "磁碟 I/O 錯誤");
    }

    [Fact]
    public void custom規則_沒有預設可回復()
    {
        var service = Create();
        service.SaveRule(ValidRequest());

        var ex = Assert.Throws<DomainException>(() => service.RestoreSeed("custom-test"));
        Assert.Contains("自訂規則", ex.Message);
    }

    // ── 儲存前驗證 ───────────────────────────────────────────────────────────

    [Fact]
    public void 新規則Id未以custom開頭_被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().SaveRule(ValidRequest("builtin-fake")));

        Assert.Contains("custom-", ex.Message);
    }

    [Fact]
    public void 缺少必要欄位_驗證不通過且不寫入()
    {
        var request = ValidRequest();
        request.SourcePattern = "";

        var validation = Create().ValidateRule(request);
        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.Errors);

        Assert.Throws<DomainException>(() => Create().SaveRule(request));
        Assert.DoesNotContain(_rules.Content.Rules, r => r.Id == "custom-test");
    }

    [Fact]
    public void 未知類別_被拒()
    {
        var request = ValidRequest();
        request.Category = "NotACategory";

        var ex = Assert.Throws<DomainException>(() => Create().SaveRule(request));
        Assert.Contains("類別", ex.Message);
    }

    [Fact]
    public void 合格規則_驗證通過()
    {
        var validation = Create().ValidateRule(ValidRequest());

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void 儲存合格規則_寫入並留下稽核()
    {
        Create().SaveRule(ValidRequest());

        Assert.Contains(_rules.Content.Rules, r => r.Id == "custom-test");
        Assert.Contains(_audit.Entries, e => e.Action == AuditActions.RuleCreate);
    }

    // ── 抑制 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 新增抑制_需填原因()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().AddSuppression(BuiltinId, new AddSuppressionRequest { Host = "SRV-01", Reason = "  " }));

        Assert.Contains("原因", ex.Message);
    }

    [Fact]
    public void 同規則同主機重複抑制_覆寫而非累積()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Host = "SRV-01", Reason = "第一次" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Host = "SRV-01", Reason = "第二次" });

        var all = _suppressions.LoadAll();
        Assert.Single(all);
        Assert.Equal("第二次", all[0].Reason);
    }

    [Fact]
    public void 抑制稽核_說明語意邊界()
    {
        Create().AddSuppression(BuiltinId, new AddSuppressionRequest
        { Host = "SRV-01", Reason = "MyApp 重啟屬正常", Days = 30 });

        var entry = _audit.Entries.Single(e => e.Action == AuditActions.SuppressAdd);
        Assert.Contains("只關掉通知", entry.Summary);
        Assert.Contains("照常聚合", entry.Summary);
    }

    [Fact]
    public void 解除不存在的抑制_回報找不到()
    {
        var ex = Assert.Throws<DomainException>(() => Create().RemoveSuppression(BuiltinId, "SRV-99"));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────

internal class FakeRuleStore : IKnownIssueRuleStore
{
    public RuleFileContent Content { get; set; } = new();

    public string Location => "(fake)";
    public bool Exists => true;

    public RuleLoadOutcome Load() => RuleLoadOutcome.Ok(Content);

    public void Save(RuleFileContent content) => Content = content;
}

internal class FakeRuleSeedStore : IRuleSeedStore
{
    private readonly List<RuleSeedSnapshot> _snapshots = new();

    public RuleSeedSnapshot? Get(string ruleId) =>
        _snapshots.FirstOrDefault(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

    public List<RuleSeedSnapshot> GetAll() => _snapshots.ToList();

    public void Sync(IEnumerable<KnownIssueRule> seedRules, int seedVersion)
    {
        _snapshots.Clear();
        foreach (var rule in seedRules)
        {
            _snapshots.Add(new RuleSeedSnapshot
            {
                RuleId = rule.Id,
                SeedVersion = seedVersion,
                ContentJson = System.Text.Json.JsonSerializer.Serialize(rule, new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                })
            });
        }
    }
}

internal class FakeSuppressionStore : ISuppressionStore
{
    private List<RuleSuppression> _suppressions = new();

    public string Location => "(fake)";

    public List<RuleSuppression> LoadAll() => _suppressions.ToList();

    public void SaveAll(List<RuleSuppression> suppressions) => _suppressions = suppressions.ToList();
}
