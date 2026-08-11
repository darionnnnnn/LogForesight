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
/// custom 全權。以及「儲存前驗證擋壞資料」——把規則驗證（RuleValidator）內建進儲存路徑。
/// </summary>
public class RuleAdminServiceTests
{
    private readonly FakeRuleStore _rules = new();
    private readonly FakeRuleSeedStore _seeds = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeUserStore _users = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueAggregateQuery _issueAggregateQuery = new();

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
        new(_rules, _seeds, _suppressions, _users, FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, _hostGroups,
            _hosts, _issueAggregateQuery);

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
        // Origin 不可被修改——它決定這條規則會不會被「內建規則升級」覆寫
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

    /// <summary>
    /// docs/archive/HISTORY.md #1（B1 三級化）：原廠鏡像是**批次啟動時**才同步的，站台升級後到
    /// 下一次批次執行之間，鏡像裡仍是三級化之前的 Severity=Critical 快照（本測試 fixture 正是這個狀態）。
    /// 回復預設必須把它正規化為 High＋ElevatesDayRisk——否則不只是嚴重度顯示不出中文名，
    /// **旗標消失會讓這條規則從此不再把當天判定為高風險日**，是靜默的行為降級。
    /// </summary>
    [Fact]
    public void 回復預設_舊版鏡像的Critical正規化為High加重大旗標()
    {
        var service = Create();
        var request = ValidRequest(BuiltinId);
        request.Description = "改壞的說明";
        service.SaveRule(request);

        service.RestoreSeed(BuiltinId);

        var restored = _rules.Content.Rules.Single();
        Assert.Equal(IssueSeverity.High, restored.Severity);
        Assert.True(restored.ElevatesDayRisk);
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
        var ex = Assert.Throws<DomainException>(() =>
            Create().RemoveSuppression(BuiltinId, SuppressionScopes.Host, "SRV-99", null));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    // ── 抑制範圍：Group／Site（回饋十三輪 F）───────────────────────────────────

    [Fact]
    public void 新增群組範圍抑制_成功且帶回群組名稱()
    {
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "IIS 前端" });
        var service = Create();

        service.AddSuppression(BuiltinId, new AddSuppressionRequest
        { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "整群組已知雜訊" });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionScopes.Group, dto.Scope);
        Assert.Equal(group.GroupId, dto.HostGroupId);
        Assert.Equal("IIS 前端", dto.HostGroupName);
        Assert.Equal(string.Empty, dto.Host);
    }

    [Fact]
    public void 新增群組範圍抑制_群組不存在時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(BuiltinId,
            new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = 999, Reason = "測試" }));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void 新增群組範圍抑制_未選群組時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(BuiltinId,
            new AddSuppressionRequest { Scope = SuppressionScopes.Group, Reason = "測試" }));

        Assert.Contains("主機群組", ex.Message);
    }

    [Fact]
    public void 新增全站範圍抑制_成功且不須額外目標()
    {
        var service = Create();

        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "全站已知雜訊" });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionScopes.Site, dto.Scope);
        Assert.Null(dto.HostGroupId);
        Assert.Equal(string.Empty, dto.Host);
    }

    [Fact]
    public void 同規則Host與Site範圍可並存_不互相覆寫()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "單台" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "全站" });

        Assert.Equal(2, service.GetSuppressions().Count);
    }

    [Fact]
    public void 同規則同群組重複抑制_覆寫而非累積()
    {
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "DB 伺服器" });
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "第一次" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "第二次" });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal("第二次", dto.Reason);
    }

    [Fact]
    public void 解除群組範圍抑制_成功()
    {
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "DB 伺服器" });
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "測試" });

        service.RemoveSuppression(BuiltinId, SuppressionScopes.Group, null, group.GroupId);

        Assert.Empty(service.GetSuppressions());
    }

    [Fact]
    public void 解除全站範圍抑制_成功()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "測試" });

        service.RemoveSuppression(BuiltinId, SuppressionScopes.Site, null, null);

        Assert.Empty(service.GetSuppressions());
    }

    // ── 抑制目標四型（回饋十五輪 A-6）：Signature／Correlation／Volume ──────────────

    [Fact]
    public void 既有Rule路徑建立的抑制TargetType為Rule()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Host = "SRV-01", Reason = "既有路徑" });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionTargetTypes.Rule, dto.TargetType);
        Assert.Equal(BuiltinId, dto.RuleId);
    }

    [Fact]
    public void Signature目標_成功建立且帶回標籤與平台()
    {
        var service = Create();
        var key = IssueSignatureKey.For("Application", "MyNoisyApp", 1000, System.Diagnostics.EventLogEntryType.Error);

        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Signature,
            SignatureKey = key,
            TargetLabel = "Application / MyNoisyApp EventId 1000",
            Scope = SuppressionScopes.Host,
            Host = "SRV-01",
            Reason = "已知雜訊，未命中規則"
        });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionTargetTypes.Signature, dto.TargetType);
        Assert.Equal(key, dto.SignatureKey);
        Assert.Equal("Application / MyNoisyApp EventId 1000", dto.TargetLabel);
        Assert.Equal(WebHost.OsWindows, dto.Platform);
        Assert.Equal(string.Empty, dto.RuleId);
    }

    [Fact]
    public void Signature目標_缺簽章鍵時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Signature, TargetLabel = "測試", Host = "SRV-01", Reason = "測試"
        }));

        Assert.Contains("問題簽章", ex.Message);
    }

    [Fact]
    public void Signature目標_缺顯示名稱時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Signature, SignatureKey = "a|b|1|1", Host = "SRV-01", Reason = "測試"
        }));

        Assert.Contains("顯示名稱", ex.Message);
    }

    [Fact]
    public void Correlation目標_成功建立且平台依模式Id前綴推導()
    {
        var service = Create();

        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Correlation,
            CorrelationPatternId = CorrelationPatternIds.XdayBruteRdp,
            TargetLabel = "暴力破解→RDP 得手",
            Scope = SuppressionScopes.Host,
            Host = "SRV-01",
            Reason = "已知的內部弱點掃描演練"
        });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionTargetTypes.Correlation, dto.TargetType);
        Assert.Equal(CorrelationPatternIds.XdayBruteRdp, dto.CorrelationPatternId);
        Assert.Equal(WebHost.OsWindows, dto.Platform);

        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Correlation,
            CorrelationPatternId = CorrelationPatternIds.LinuxSshBruteSuccess,
            TargetLabel = "SSH 破解得手",
            Scope = SuppressionScopes.Host,
            Host = "SRV-02",
            Reason = "測試"
        });
        var linuxDto = service.GetSuppressions().Single(s => s.CorrelationPatternId == CorrelationPatternIds.LinuxSshBruteSuccess);
        Assert.Equal(WebHost.OsLinux, linuxDto.Platform);
    }

    [Fact]
    public void Correlation目標_不合法的模式Id時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Correlation, CorrelationPatternId = "not-a-real-pattern",
            TargetLabel = "測試", Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試"
        }));

        Assert.Contains("關聯模式", ex.Message);
    }

    [Fact]
    public void Volume目標_成功建立_留空標籤時後端自動帶入固定文字()
    {
        var service = Create();

        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Volume, VolumeKind = VolumeKinds.Audit,
            Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "本機常態高稽核量"
        });

        var dto = Assert.Single(service.GetSuppressions());
        Assert.Equal(SuppressionTargetTypes.Volume, dto.TargetType);
        Assert.Equal(VolumeKinds.Audit, dto.VolumeKind);
        Assert.Equal("安全稽核事件量突增", dto.TargetLabel);
    }

    [Fact]
    public void Volume目標_不合法的總量類別時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Volume, VolumeKind = "not-a-real-kind",
            Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試"
        }));

        Assert.Contains("總量類別", ex.Message);
    }

    // ── 新三型 Scope 限制：一律僅支援 Host（回饋十六輪批次C-1）────────────────

    [Theory]
    [InlineData(SuppressionTargetTypes.Signature)]
    [InlineData(SuppressionTargetTypes.Correlation)]
    [InlineData(SuppressionTargetTypes.Volume)]
    public void 新三型_Group範圍被拒(string targetType)
    {
        var request = new AddSuppressionRequest { TargetType = targetType, Scope = SuppressionScopes.Group, HostGroupId = 1, Reason = "測試" };
        FillRequiredFields(request, targetType);

        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(request));

        Assert.Contains("僅支援單台主機範圍", ex.Message);
    }

    [Theory]
    [InlineData(SuppressionTargetTypes.Signature)]
    [InlineData(SuppressionTargetTypes.Correlation)]
    [InlineData(SuppressionTargetTypes.Volume)]
    public void 新三型_Site範圍被拒(string targetType)
    {
        var request = new AddSuppressionRequest { TargetType = targetType, Scope = SuppressionScopes.Site, Reason = "測試" };
        FillRequiredFields(request, targetType);

        var ex = Assert.Throws<DomainException>(() => Create().AddSuppression(request));

        Assert.Contains("僅支援單台主機範圍", ex.Message);
    }

    /// <summary>Rule 型不受新三型的 Scope 限制影響——Group／Site 仍走既有的
    /// PreviewSuppression 護欄，維持原行為。</summary>
    [Fact]
    public void Rule目標_不受新三型Scope限制影響_Site與Group仍可建立()
    {
        var service = Create();
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "測試群組" });

        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "全站" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "群組" });

        Assert.Equal(2, service.GetSuppressions().Count);
    }

    private static void FillRequiredFields(AddSuppressionRequest request, string targetType)
    {
        switch (targetType)
        {
            case SuppressionTargetTypes.Signature:
                request.SignatureKey = "a|b|1|1";
                request.TargetLabel = "測試";
                break;
            case SuppressionTargetTypes.Correlation:
                request.CorrelationPatternId = CorrelationPatternIds.StorageChain;
                request.TargetLabel = "測試";
                break;
            case SuppressionTargetTypes.Volume:
                request.VolumeKind = VolumeKinds.Error;
                break;
        }
    }

    [Fact]
    public void 四型各自獨立比對_不因巧合值互相覆寫()
    {
        // Signature 的 SignatureKey 恰好與某條規則的 Id 撞字串也不該互相覆寫——
        // upsert 去重鍵先比 TargetType，兩者分屬不同分區
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "Rule" });
        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Signature, SignatureKey = BuiltinId, TargetLabel = "撞字串測試",
            Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "Signature"
        });

        Assert.Equal(2, service.GetSuppressions().Count);
    }

    [Fact]
    public void 解除Signature目標抑制_成功()
    {
        var service = Create();
        var key = "System|disk|999|1";
        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Signature, SignatureKey = key, TargetLabel = "測試",
            Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試"
        });

        service.RemoveSuppression(SuppressionTargetTypes.Signature, null, key, null, null,
            SuppressionScopes.Host, "SRV-01", null);

        Assert.Empty(service.GetSuppressions());
    }

    [Fact]
    public void 解除Correlation目標抑制_成功()
    {
        var service = Create();
        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Correlation, CorrelationPatternId = CorrelationPatternIds.StorageChain,
            TargetLabel = "測試", Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試"
        });

        service.RemoveSuppression(SuppressionTargetTypes.Correlation, null, null, CorrelationPatternIds.StorageChain, null,
            SuppressionScopes.Host, "SRV-01", null);

        Assert.Empty(service.GetSuppressions());
    }

    [Fact]
    public void 解除Volume目標抑制_成功()
    {
        var service = Create();
        service.AddSuppression(new AddSuppressionRequest
        {
            TargetType = SuppressionTargetTypes.Volume, VolumeKind = VolumeKinds.Error,
            Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試"
        });

        service.RemoveSuppression(SuppressionTargetTypes.Volume, null, null, null, VolumeKinds.Error,
            SuppressionScopes.Host, "SRV-01", null);

        Assert.Empty(service.GetSuppressions());
    }

    // ── 抑制徽章代表筆：最寬範圍優先（回饋十五輪 R3）───────────────────────────

    [Fact]
    public void 規則同時有Host與Site抑制時代表筆取Site不是先建立的Host()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "先建的 Host" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Site, Reason = "後建的 Site" });

        var rule = service.GetRules().Single(r => r.Id == BuiltinId);

        Assert.Equal(SuppressionScopes.Site, rule.Suppression!.Scope);
        Assert.Equal(2, rule.SuppressionCount);
    }

    [Fact]
    public void 規則同時有Host與Group抑制時代表筆取Group()
    {
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "測試群組" });
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "Host" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group.GroupId, Reason = "Group" });

        var rule = service.GetRules().Single(r => r.Id == BuiltinId);

        Assert.Equal(SuppressionScopes.Group, rule.Suppression!.Scope);
    }

    [Fact]
    public void 只有一筆抑制時SuppressionCount為一且Preview只含這一筆()
    {
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "測試" });

        var rule = service.GetRules().Single(r => r.Id == BuiltinId);

        Assert.Equal(1, rule.SuppressionCount);
        Assert.Single(rule.SuppressionPreview);
    }

    [Fact]
    public void 超過三筆抑制時Preview只取前三筆但SuppressionCount反映全部()
    {
        var group1 = _hostGroups.Upsert(new HostGroup { GroupName = "群組一" });
        var group2 = _hostGroups.Upsert(new HostGroup { GroupName = "群組二" });
        var service = Create();
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-01", Reason = "1" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Host, Host = "SRV-02", Reason = "2" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group1.GroupId, Reason = "3" });
        service.AddSuppression(BuiltinId, new AddSuppressionRequest { Scope = SuppressionScopes.Group, HostGroupId = group2.GroupId, Reason = "4" });

        var rule = service.GetRules().Single(r => r.Id == BuiltinId);

        Assert.Equal(4, rule.SuppressionCount);
        Assert.Equal(3, rule.SuppressionPreview.Count);
    }

    [Fact]
    public void 沒有抑制時SuppressionCount為零且Preview為空()
    {
        var rule = Create().GetRules().Single(r => r.Id == BuiltinId);

        Assert.Equal(0, rule.SuppressionCount);
        Assert.Empty(rule.SuppressionPreview);
        Assert.Null(rule.Suppression);
    }

    // ── PreviewSuppression：抑制影響面預覽（回饋十四輪 C1）─────────────────────

    [Fact]
    public void PreviewSuppression_Host範圍不適用被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().PreviewSuppression(BuiltinId, SuppressionScopes.Host, null));

        Assert.Contains("Host", ex.Message);
    }

    [Fact]
    public void PreviewSuppression_Group未選群組時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().PreviewSuppression(BuiltinId, SuppressionScopes.Group, null));

        Assert.Contains("主機群組", ex.Message);
    }

    [Fact]
    public void PreviewSuppression_群組不存在時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().PreviewSuppression(BuiltinId, SuppressionScopes.Group, 999));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void PreviewSuppression_規則不存在時被拒()
    {
        var ex = Assert.Throws<DomainException>(() => Create().PreviewSuppression("no-such-rule", SuppressionScopes.Site, null));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void PreviewSuppression_Site範圍只計入啟用中且未合併的主機()
    {
        _hosts.Upsert(new WebHost { HostName = "H1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "H2", Active = true });
        _hosts.Upsert(new WebHost { HostName = "H3", Active = false }); // 停用，不計入

        var preview = Create().PreviewSuppression(BuiltinId, SuppressionScopes.Site, null);

        Assert.Equal(2, preview.AffectedHostCount);
        Assert.Equal(14, preview.WindowDays);
        Assert.False(preview.ApproximateForLinux);
    }

    [Fact]
    public void PreviewSuppression_Group範圍只統計該群組成員的主機Id()
    {
        var target = _hostGroups.Upsert(new HostGroup { GroupName = "DB 伺服器" });
        var other = _hostGroups.Upsert(new HostGroup { GroupName = "Web 伺服器" });
        var db1 = _hosts.Upsert(new WebHost { HostName = "DB1", Active = true, GroupIds = new List<long> { target.GroupId } });
        var db2 = _hosts.Upsert(new WebHost { HostName = "DB2", Active = true, GroupIds = new List<long> { target.GroupId } });
        _hosts.Upsert(new WebHost { HostName = "WEB1", Active = true, GroupIds = new List<long> { other.GroupId } });

        var preview = Create().PreviewSuppression(BuiltinId, SuppressionScopes.Group, target.GroupId);

        Assert.Equal(2, preview.AffectedHostCount);
        Assert.NotNull(_issueAggregateQuery.LastCall);
        var requestedHostIds = _issueAggregateQuery.LastCall!.Value.HostIds!.OrderBy(x => x).ToList();
        Assert.Equal(new[] { db1.HostId, db2.HostId }.OrderBy(x => x), requestedHostIds);
    }

    /// <summary>
    /// M 值精準對應 KnownIssueCatalog.FindRule 同一套比對邏輯（來源子字串＋EventId 精確符合）：
    /// Source 含子字串但 EventId 不符、或 Source 不含子字串的聚合列都不該被算進命中次數。
    /// </summary>
    [Fact]
    public void PreviewSuppression_Windows規則精準比對SourcePattern子字串與EventId()
    {
        _hosts.Upsert(new WebHost { HostName = "H1", Active = true }); // Site 範圍至少要有一台存活主機，查詢才不會被空集合短路
        _issueAggregateQuery.Result = new List<IssueAggregate>
        {
            new() { Source = "disk", EventId = 153, TotalCount = 42 },        // 精準命中
            new() { Source = "disk-controller", EventId = 153, TotalCount = 3 }, // Source 含子字串，命中
            new() { Source = "disk", EventId = 999, TotalCount = 100 },       // EventId 不符，不計入
            new() { Source = "network", EventId = 153, TotalCount = 50 }      // Source 不符，不計入
        };

        var preview = Create().PreviewSuppression(BuiltinId, SuppressionScopes.Site, null);

        Assert.Equal(45, preview.RecentHitCount);
    }

    /// <summary>
    /// Linux 規則沒有 EventKey 可用（lf_top_issues 沒存），只能以 ProgramPattern 對 Source
    /// 做子字串比對——涵蓋面因此寬於這條規則實際命中的次數，ApproximateForLinux 必須誠實標記。
    /// </summary>
    [Fact]
    public void PreviewSuppression_Linux規則以ProgramPattern子字串比對且標記為近似值()
    {
        var linuxRule = new KnownIssueRule
        {
            Id = "builtin-linux-ssh-bruteforce",
            Origin = "builtin",
            Enabled = true,
            Platform = "linux",
            ProgramPattern = "sshd",
            Category = IssueCategory.Security,
            Severity = IssueSeverity.High,
            Description = "SSH 暴力破解嘗試",
            CountThreshold = 10
        };
        _rules.Content.Rules.Add(linuxRule);
        _hosts.Upsert(new WebHost { HostName = "H1", Active = true }); // Site 範圍至少要有一台存活主機，查詢才不會被空集合短路
        _issueAggregateQuery.Result = new List<IssueAggregate>
        {
            new() { Source = "sshd", EventId = 0, TotalCount = 30 },   // 同 program 的另一條規則（如 ssh-accept）也會被算進來
            new() { Source = "httpd", EventId = 0, TotalCount = 99 }   // 不同 program，不計入
        };

        var preview = Create().PreviewSuppression(linuxRule.Id, SuppressionScopes.Site, null);

        Assert.Equal(30, preview.RecentHitCount);
        Assert.True(preview.ApproximateForLinux);
    }

    // ── MatchOrder：比對順序可見化（回饋十五輪 B-1）─────────────────────────────

    private static KnownIssueRule LinuxRuleFixture(string id, bool enabled = true) => new()
    {
        Id = id, Origin = "custom", Enabled = enabled, Platform = "linux", ProgramPattern = "sshd",
        Category = IssueCategory.Security, Severity = IssueSeverity.High, Description = "測試", CountThreshold = 1
    };

    [Fact]
    public void MatchOrder反映清單順序_同平台規則依序編號從一開始()
    {
        // 建構子已放入 BuiltinId（清單第一條）；再手動 append 兩條，模擬 FindRule 的實際比對序位
        _rules.Content.Rules.Add(new KnownIssueRule
        {
            Id = "custom-second", Origin = "custom", Enabled = true, Platform = "windows",
            SourcePattern = "app2", EventIds = new[] { 1 }, Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium, Description = "測試", CountThreshold = 1
        });
        _rules.Content.Rules.Add(new KnownIssueRule
        {
            Id = "custom-third", Origin = "custom", Enabled = true, Platform = "windows",
            SourcePattern = "app3", EventIds = new[] { 1 }, Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium, Description = "測試", CountThreshold = 1
        });

        var rules = Create().GetRules();

        Assert.Equal(1, rules.Single(r => r.Id == BuiltinId).MatchOrder);
        Assert.Equal(2, rules.Single(r => r.Id == "custom-second").MatchOrder);
        Assert.Equal(3, rules.Single(r => r.Id == "custom-third").MatchOrder);
    }

    /// <summary>FindRule／FindLinuxRule 是兩套獨立比對邏輯——Windows 規則中間插一條 Linux 規則，
    /// 不該打斷 Windows 側的順位編號，Linux 規則也從自己的 1 開始算，不看物理清單位置。</summary>
    [Fact]
    public void MatchOrder_Windows與Linux分開計數_不受清單中彼此交錯影響()
    {
        // 清單物理順序：[BuiltinId(win), linux-a, custom-second(win), linux-b]——刻意交錯
        _rules.Content.Rules.Add(LinuxRuleFixture("linux-a"));
        _rules.Content.Rules.Add(new KnownIssueRule
        {
            Id = "custom-second", Origin = "custom", Enabled = true, Platform = "windows",
            SourcePattern = "app2", EventIds = new[] { 1 }, Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium, Description = "測試", CountThreshold = 1
        });
        _rules.Content.Rules.Add(LinuxRuleFixture("linux-b"));

        var rules = Create().GetRules();

        Assert.Equal(1, rules.Single(r => r.Id == BuiltinId).MatchOrder);
        Assert.Equal(2, rules.Single(r => r.Id == "custom-second").MatchOrder);
        Assert.Equal(1, rules.Single(r => r.Id == "linux-a").MatchOrder);
        Assert.Equal(2, rules.Single(r => r.Id == "linux-b").MatchOrder);
    }

    /// <summary>停用規則依然佔一個順位——順序是清單事實，不是「目前有效比對序位」，
    /// 停用只是不參與比對，列上仍照實顯示（同 RuleValidator 的遮蔽偵測語意）。</summary>
    [Fact]
    public void MatchOrder_停用規則仍計入順序不被跳過()
    {
        _rules.Content.Rules.Add(new KnownIssueRule
        {
            Id = "custom-disabled", Origin = "custom", Enabled = false, Platform = "windows",
            SourcePattern = "app2", EventIds = new[] { 1 }, Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium, Description = "測試", CountThreshold = 1
        });
        _rules.Content.Rules.Add(new KnownIssueRule
        {
            Id = "custom-third", Origin = "custom", Enabled = true, Platform = "windows",
            SourcePattern = "app3", EventIds = new[] { 1 }, Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium, Description = "測試", CountThreshold = 1
        });

        var rules = Create().GetRules();

        Assert.Equal(2, rules.Single(r => r.Id == "custom-disabled").MatchOrder);
        Assert.Equal(3, rules.Single(r => r.Id == "custom-third").MatchOrder);
    }
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────
// FakeRuleStore／FakeRuleSeedStore／FakeSuppressionStore 已搬到 TestDoubles\RuleAdminFakes.cs
// （FakeRuleStore 已被其他測試檔共用）。
