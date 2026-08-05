using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P0-3：<see cref="SystemSettingsService"/> 新增的 RunLogRetentionDays／AuditRetentionDays 欄位
/// 走完整條 Update → Get 路徑（實測曾手改多處 wiring，這類機械式串接最容易漏一處）。
/// </summary>
public class SystemSettingsServiceTests
{
    private readonly FakeSystemSettingsStore _store = new();

    private SystemSettingsService Create() =>
        new(_store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore());

    private static UpdateSystemSettingsRequest ValidRequest(
        int runLogRetentionDays = 90, int auditRetentionDays = 730, int riskyEventRetentionDays = 14) => new()
    {
        UnhandledSeverities = new List<string> { "High" },
        SeverityDisplayMode = "DefaultHidden",
        VisibleDayRiskLevels = new List<string> { "高", "中", "低" },
        AiBaseUrl = "",
        InitialHistoryDays = 120,
        RetentionDays = 120,
        RunLogRetentionDays = runLogRetentionDays,
        AuditRetentionDays = auditRetentionDays,
        RiskyEventRetentionDays = riskyEventRetentionDays,
        // §12：自 appsettings 遷入的參數（各欄位的 [Range] 由 API 邊界的模型驗證負責，
        // 服務層只做跨欄位規則；這裡帶出廠值，讓既有測試不會存進一堆 0）
        AiTimeoutSeconds = 600,
        AiRetryCount = 3,
        AiRetryDelaySeconds = 10,
        AiJsonRetryCount = 2,
        AiMaxTokens = 1536,
        AiDeepDiveMaxTokens = 8192,
        AiFrequencyPenalty = 0.8,
        AiPresencePenalty = 0.8,
        AiExtraRequestFieldsJson = """{"rep_pen":1.3}""",
        CheckupIntervalDays = 7,
        ImportMaxFileSizeKb = 2048,
        ImportMaxRows = 5000
    };

    // ── §12：自 appsettings 遷入設定頁的參數 ────────────────────────────────

    [Fact]
    public void Update後_AI進階參數與分析參數持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.AiTimeoutSeconds = 300;
        request.AiMaxTokens = 2048;
        request.AiFrequencyPenalty = 1.2;
        request.ServerDescription = "  公司機房的 AD 網域控制站  ";
        request.CheckupIntervalDays = 14;
        request.WatchedFolders = new List<string> { " C:\\inetpub\\wwwroot ", "", "C:\\inetpub\\wwwroot" };
        request.AnalysisChannels = new List<string> { "System", "Application" };
        request.ImportMaxFileSizeKb = 4096;
        request.ImportMaxRows = 10000;

        var saved = service.Update(request);

        Assert.Equal(300, saved.AiTimeoutSeconds);
        Assert.Equal(2048, saved.AiMaxTokens);
        Assert.Equal(1.2, saved.AiFrequencyPenalty);
        Assert.Equal("公司機房的 AD 網域控制站", saved.ServerDescription);   // trim
        Assert.Equal(14, saved.CheckupIntervalDays);
        Assert.Equal(new[] { "C:\\inetpub\\wwwroot" }, saved.WatchedFolders);   // trim＋去空行＋去重
        Assert.Equal(new[] { "System", "Application" }, saved.AnalysisChannels);
        Assert.Equal(4096, saved.ImportMaxFileSizeKb);
        Assert.Equal(10000, saved.ImportMaxRows);

        // 重讀確認落地（不是只有回傳值對）
        var reread = service.Get();
        Assert.Equal(300, reread.AiTimeoutSeconds);
        Assert.Equal(14, reread.CheckupIntervalDays);
    }

    [Fact]
    public void Update_額外請求欄位非JSON物件時拒絕()
    {
        var service = Create();

        var notJson = ValidRequest();
        notJson.AiExtraRequestFieldsJson = "{ 這不是 JSON";
        Assert.Throws<DomainException>(() => service.Update(notJson));

        // 合法 JSON 但不是物件（無法合併進請求本體）也要擋
        var notObject = ValidRequest();
        notObject.AiExtraRequestFieldsJson = "[1, 2, 3]";
        Assert.Throws<DomainException>(() => service.Update(notObject));
    }

    [Fact]
    public void Update_額外請求欄位可留空表示不附加()
    {
        var service = Create();
        var request = ValidRequest();
        request.AiExtraRequestFieldsJson = "   ";

        Assert.Equal("", service.Update(request).AiExtraRequestFieldsJson);
    }

    [Fact]
    public void Update後_RunLog與Audit保留天數持久化()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(runLogRetentionDays: 45, auditRetentionDays: 365));

        Assert.Equal(45, saved.RunLogRetentionDays);
        Assert.Equal(365, saved.AuditRetentionDays);
        Assert.Equal(45, service.Get().RunLogRetentionDays);
        Assert.Equal(365, service.Get().AuditRetentionDays);
    }

    // ── docs/archive/WEB-SCHEDULER-PLAN.md §2：RiskyEventRetentionDays ──────────────────────

    [Fact]
    public void Update後_風險log暫存保留天數持久化()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(riskyEventRetentionDays: 7));

        Assert.Equal(7, saved.RiskyEventRetentionDays);
        Assert.Equal(7, service.Get().RiskyEventRetentionDays);
    }

    [Fact]
    public void 風險log暫存保留天數大於歷史資料保留天數時拒絕()
    {
        var service = Create();
        var request = ValidRequest(riskyEventRetentionDays: 200); // RetentionDays 固定為 120（ValidRequest）

        Assert.Throws<DomainException>(() => service.Update(request));
    }

    [Fact]
    public void 風險log暫存保留天數等於歷史資料保留天數時允許()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(riskyEventRetentionDays: 120));

        Assert.Equal(120, saved.RiskyEventRetentionDays);
    }

    [Fact]
    public void 預設值符合SystemSettings的內建預設()
    {
        var settings = new SystemSettings();

        Assert.Equal(90, settings.RunLogRetentionDays);
        Assert.Equal(730, settings.AuditRetentionDays);
        Assert.Equal(14, settings.RiskyEventRetentionDays);
    }

    /// <summary>
    /// docs/archive/HISTORY.md S10：ValidSeverities 是手寫陣列（承載畫面勾選順序，
    /// 由重到輕，無法直接用 Enum.GetNames 因為那是宣告順序），IssueSeverity 加值時編譯器
    /// 不會提醒這裡要跟著加——用測試斷言集合一致，enum 漏改時這裡紅燈。
    /// docs/archive/HISTORY.md #1（B1 三級化）：IssueSeverity 列舉刻意保留 Critical
    /// （舊資料反序列化相容用），但 ValidSeverities 是「使用者可勾選的層級」，三級化後
    /// 不再包含它——兩者從此不再逐一對應，是設計選擇而非漏同步。
    /// </summary>
    [Fact]
    public void ValidSeverities集合排除Critical但涵蓋其餘列舉值()
    {
        var enumNames = Enum.GetNames<IssueSeverity>().ToHashSet();
        var validSeverities = SystemSettingsService.ValidSeverities.ToHashSet();

        Assert.DoesNotContain("Critical", validSeverities);
        Assert.Equal(enumNames.Where(n => n != "Critical").ToHashSet(), validSeverities);
    }

    // ── P0-5：AI 金鑰加密路徑（動到 CryptoHelper 金鑰來源時一併補測） ─────────────────

    [Fact]
    public void 設定AI金鑰_以CryptoHelper加密存放不留明碼()
    {
        var service = Create();
        var request = ValidRequest();
        request.AiApiKey = "sk-my-real-api-key";

        service.Update(request);

        var stored = _store.Get().AiApiKeyEnc;
        Assert.True(CryptoHelper.IsEncrypted(stored));
        Assert.NotEqual("sk-my-real-api-key", stored);
        Assert.Equal("sk-my-real-api-key", CryptoHelper.Decrypt(stored));
    }

    /// <summary>write-only：Get() 回傳的 DTO 只能看得出「有沒有設」，看不到明碼或密文本身</summary>
    [Fact]
    public void 設定AI金鑰後_DTO只回報已設定不回傳金鑰內容()
    {
        var service = Create();
        var request = ValidRequest();
        request.AiApiKey = "sk-my-real-api-key";

        var saved = service.Update(request);

        Assert.True(saved.AiHasApiKey);
    }

    /// <summary>AiApiKey 留空＝沿用既有金鑰（設定頁的既定語意），不能被一次「不小心留空」的儲存清掉</summary>
    [Fact]
    public void 更新時AI金鑰留空_沿用既有金鑰不被清除()
    {
        var service = Create();
        var withKey = ValidRequest();
        withKey.AiApiKey = "sk-original-key";
        service.Update(withKey);
        var encryptedBefore = _store.Get().AiApiKeyEnc;

        var withoutKey = ValidRequest();
        withoutKey.AiApiKey = null;
        service.Update(withoutKey);

        Assert.Equal(encryptedBefore, _store.Get().AiApiKeyEnc);
        Assert.Equal("sk-original-key", CryptoHelper.Decrypt(_store.Get().AiApiKeyEnc));
    }

    [Fact]
    public void ClearAiApiKey為true時_清除金鑰()
    {
        var service = Create();
        var withKey = ValidRequest();
        withKey.AiApiKey = "sk-to-be-cleared";
        service.Update(withKey);

        var clearRequest = ValidRequest();
        clearRequest.ClearAiApiKey = true;
        var saved = service.Update(clearRequest);

        Assert.Equal("", _store.Get().AiApiKeyEnc);
        Assert.False(saved.AiHasApiKey);
    }

    // ── docs/archive/HISTORY.md #5：三模式簡化為兩個（DefaultHidden／SiteHidden）──────

    [Theory]
    [InlineData("Locked")]
    [InlineData("GlobalFilter")]
    public void Update拒絕舊的三模式值(string legacyMode)
    {
        var service = Create();
        var request = ValidRequest();
        request.SeverityDisplayMode = legacyMode;

        Assert.Throws<DomainException>(() => service.Update(request));
    }

    [Fact]
    public void Update接受新的SiteHidden值()
    {
        var service = Create();
        var request = ValidRequest();
        request.SeverityDisplayMode = "SiteHidden";

        var saved = service.Update(request);

        Assert.Equal("SiteHidden", saved.SeverityDisplayMode);
    }

    [Theory]
    [InlineData("Locked")]
    [InlineData("GlobalFilter")]
    public void Get_既存blob裡的舊值正規化為SiteHidden(string legacyMode)
    {
        _store.Update(s => s.SeverityDisplayMode = legacyMode);
        var service = Create();

        Assert.Equal("SiteHidden", service.Get().SeverityDisplayMode);
    }

    [Theory]
    [InlineData("Locked")]
    [InlineData("GlobalFilter")]
    [InlineData("SiteHidden")]
    public void GetVisibleSeverities_舊值與新值皆視同SiteHidden回傳集合(string mode)
    {
        _store.Update(s =>
        {
            s.SeverityDisplayMode = mode;
            s.UnhandledSeverities = new List<string> { "Critical", "High" };
        });
        var service = Create();

        var visible = service.GetVisibleSeverities();

        // docs/archive/HISTORY.md #1（B1 三級化）：舊設定殘留的 "Critical" 正規化為 "High"，
        // 與既有的 "High" 合併成同一個值——三級化前的兩個不同層級現在是同一層級
        Assert.NotNull(visible);
        Assert.Equal(new HashSet<string> { "High" }, visible);
    }

    [Fact]
    public void GetVisibleSeverities_DefaultHidden回傳null不過濾()
    {
        _store.Update(s => s.SeverityDisplayMode = "DefaultHidden");
        var service = Create();

        Assert.Null(service.GetVisibleSeverities());
    }

    // ── docs/archive/FEEDBACK-3-PLAN.md #8：日風險等級顯示 ──────────────────────

    [Fact]
    public void Update_日風險等級顯示未含高_丟例外()
    {
        var service = Create();
        var request = ValidRequest();
        request.VisibleDayRiskLevels = new List<string> { "中", "低" };

        Assert.Throws<DomainException>(() => service.Update(request));
    }

    [Fact]
    public void Update_日風險等級顯示非法值被剔除且去重()
    {
        var service = Create();
        var request = ValidRequest();
        request.VisibleDayRiskLevels = new List<string> { "高", "不存在的等級", "高" };

        var saved = service.Update(request);

        Assert.Equal(new[] { "高" }, saved.VisibleDayRiskLevels);
    }

    /// <summary>SystemSettings 模型的內建預設值已是全三級（見類別定義）——舊部署升級後
    /// 這個欄位在資料庫尚未有值時，讀到的就是這個預設，不會突然全部隱藏</summary>
    [Fact]
    public void Get_未曾設定過時預設全顯示()
    {
        var service = Create();

        var visible = service.Get().VisibleDayRiskLevels;

        Assert.Equal(new HashSet<string> { "高", "中", "低" }, visible.ToHashSet());
    }

    [Fact]
    public void GetVisibleDayRiskLevels_全勾時回null不過濾()
    {
        var service = Create();
        service.Update(ValidRequest());   // ValidRequest 預設高中低全勾

        Assert.Null(service.GetVisibleDayRiskLevels());
    }

    [Fact]
    public void GetVisibleDayRiskLevels_只勾高時回傳單一集合()
    {
        var service = Create();
        var request = ValidRequest();
        request.VisibleDayRiskLevels = new List<string> { "高" };
        service.Update(request);

        Assert.Equal(new HashSet<string> { "高" }, service.GetVisibleDayRiskLevels());
    }

    // ── docs/archive/HISTORY.md #9：AD 驗證設定 ──────────────────────────────

    [Fact]
    public void Update_AD啟用但伺服器清單為空_丟例外()
    {
        var service = Create();
        var request = ValidRequest();
        request.AdAuthEnabled = true;
        request.AdServers = new List<string>();

        Assert.Throws<DomainException>(() => service.Update(request));
    }

    [Fact]
    public void Update_AD啟用且有伺服器_成功並持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.AdAuthEnabled = true;
        request.AdServers = new List<string> { "dc1.corp.local", "dc2.corp.local" };
        request.AdSearchBase = "DC=corp,DC=local";

        var saved = service.Update(request);

        Assert.True(saved.AdAuthEnabled);
        Assert.Equal(new[] { "dc1.corp.local", "dc2.corp.local" }, saved.AdServers);
        Assert.Equal("DC=corp,DC=local", saved.AdSearchBase);
        Assert.True(service.Get().AdAuthEnabled);
    }

    [Fact]
    public void Update_AD停用時_伺服器清單可以是空的()
    {
        var service = Create();
        var request = ValidRequest();
        request.AdAuthEnabled = false;
        request.AdServers = new List<string>();

        var saved = service.Update(request);

        Assert.False(saved.AdAuthEnabled);
    }

    [Fact]
    public void Update_伺服器清單去除空白與重複()
    {
        var service = Create();
        var request = ValidRequest();
        request.AdAuthEnabled = true;
        request.AdServers = new List<string> { "  dc1  ", "", "dc1", "DC1", "dc2" };

        var saved = service.Update(request);

        Assert.Equal(new[] { "dc1", "dc2" }, saved.AdServers);
    }

    [Fact]
    public void Update_SearchFilter留空時回退預設值()
    {
        var service = Create();
        var request = ValidRequest();
        request.AdAuthEnabled = true;
        request.AdServers = new List<string> { "dc1" };
        request.AdSearchFilter = "";

        var saved = service.Update(request);

        Assert.Equal("(sAMAccountName={0})", saved.AdSearchFilter);
    }

    [Fact]
    public void TestAdConnection_伺服器清單為空_丟例外_不觸發任何連線嘗試()
    {
        var service = Create();

        Assert.Throws<DomainException>(() => service.TestAdConnection(new TestAdConnectionRequest
        {
            Servers = new List<string>(),
            Account = "user",
            Password = "pass"
        }));
    }
}
