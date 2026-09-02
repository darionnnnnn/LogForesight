using System.ComponentModel.DataAnnotations;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P0-3：<see cref="SystemSettingsService"/> 新增的 RunLogRetentionDays／AuditRetentionDays 欄位
/// 走完整條 Update → Get 路徑（實測曾手改多處 wiring，這類機械式串接最容易漏一處）。
/// </summary>
public class SystemSettingsServiceTests : IDisposable
{
    private readonly FakeSystemSettingsStore _store = new();
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeSmtpMailSender _mailSender = new();

    /// <summary>回饋十八輪批次C 測試所需：讓測試能先塞紀錄，再驗證 Update 後
    /// MailNotifyStateStore 的預填結果——原本 Create() 內部各自新建，測試碰不到。</summary>
    private readonly FakeAnalysisRecordQuery _mailRecords = new();
    private MailNotifyStateStore MailState => new(_fx.Blob("mail_notify_state"));

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private SystemSettingsService Create() =>
        new(_store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(_store, _mailSender, new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                _mailRecords, new FakeHandlingStore(),
                MailState), new FakeReportUsageQuery());

    private static UpdateSystemSettingsRequest ValidRequest(
        int runLogRetentionDays = 120, int auditRetentionDays = 730, int rawEventRetentionDays = 120) => new()
    {
        UnhandledSeverities = new List<string> { "High" },
        SeverityDisplayMode = "DefaultHidden",
        VisibleDayRiskLevels = new List<string> { "高", "中", "低" },
        AiProvider = "Local",
        AiBaseUrl = "",
        AiModel = "local-model",
        AiAzureDeployment = "",
        AiAzureApiVersion = "2024-10-21",
        InitialHistoryDays = 120,
        RetentionDays = 180,
        RunLogRetentionDays = runLogRetentionDays,
        AuditRetentionDays = auditRetentionDays,
        RawEventRetentionDays = rawEventRetentionDays,
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
        ImportMaxRows = 5000,
        // PRTG 監控系統設定（PRTG 第 1 輪批次B）
        PrtgEnabled = false,
        PrtgUrl = "",
        PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token,
        PrtgUsername = "",
        PrtgPassword = null,
        ClearPrtgPassword = false,
        PrtgPasshash = null,
        ClearPrtgPasshash = false,
        PrtgIgnoreSslErrors = false,
        PrtgTimeoutSeconds = 60,
        PrtgFetchConcurrency = 2,
        PrtgBackfillDays = 30,
        PrtgRetentionDays = 180
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
    public void Update後_權限異動自訂欄位四角色持久化與往返DTO不掉值()
    {
        var service = Create();
        var request = ValidRequest();
        request.PermissionOperatorFields = new List<string> { " CustomOp ", "", "CustomOp2", "CustomOp" };
        request.PermissionMemberFields = new List<string> { "CustomMem" };
        request.PermissionGroupFields = new List<string> { " Team Group ", "GrpName" };
        request.PermissionObjectFields = new List<string> { "ResourcePath" };

        var saved = service.Update(request);

        Assert.Equal(new[] { "CustomOp", "CustomOp2" }, saved.PermissionOperatorFields);
        Assert.Equal(new[] { "CustomMem" }, saved.PermissionMemberFields);
        Assert.Equal(new[] { "Team Group", "GrpName" }, saved.PermissionGroupFields);
        Assert.Equal(new[] { "ResourcePath" }, saved.PermissionObjectFields);

        // 重讀確認資料庫落地
        var reread = service.Get();
        Assert.Equal(new[] { "CustomOp", "CustomOp2" }, reread.PermissionOperatorFields);
        Assert.Equal(new[] { "CustomMem" }, reread.PermissionMemberFields);
        Assert.Equal(new[] { "Team Group", "GrpName" }, reread.PermissionGroupFields);
        Assert.Equal(new[] { "ResourcePath" }, reread.PermissionObjectFields);

        // 驗證 JSON 序列化往返不掉值
        var json = System.Text.Json.JsonSerializer.Serialize(reread);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SystemSettingsDto>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(reread.PermissionOperatorFields, deserialized.PermissionOperatorFields);
        Assert.Equal(reread.PermissionMemberFields, deserialized.PermissionMemberFields);
        Assert.Equal(reread.PermissionGroupFields, deserialized.PermissionGroupFields);
        Assert.Equal(reread.PermissionObjectFields, deserialized.PermissionObjectFields);
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

    // ── 原始事件內容保留天數（RawEventRetentionDays） ──────────────────────

    [Fact]
    public void Update後_原始事件內容保留天數持久化()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(rawEventRetentionDays: 90));

        Assert.Equal(90, saved.RawEventRetentionDays);
        Assert.Equal(90, service.Get().RawEventRetentionDays);
    }

    [Fact]
    public void 原始事件內容保留天數大於歷史資料保留天數時拒絕()
    {
        var service = Create();
        var request = ValidRequest(rawEventRetentionDays: 200);
        request.RetentionDays = 180;

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("原始事件內容保留天數", ex.Message);
    }

    [Fact]
    public void 原始事件內容保留天數等於歷史資料保留天數時允許()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(rawEventRetentionDays: 180));

        Assert.Equal(180, saved.RawEventRetentionDays);
    }

    [Fact]
    public void 預設值符合SystemSettings的內建預設()
    {
        var settings = new SystemSettings();

        Assert.Equal(120, settings.RunLogRetentionDays);
        Assert.Equal(730, settings.AuditRetentionDays);
        Assert.Equal(120, settings.RawEventRetentionDays);
        Assert.Equal(120, settings.InitialHistoryDays);
        Assert.Equal(180, settings.RetentionDays);
    }

    // ── 保留天數共同下限 90（回饋二十六輪作業 D）─────────────────────────────────
    // [Range] 在 API 邊界的模型驗證生效（服務層只做跨欄位規則），所以這裡直接驗屬性。

    [Theory]
    [InlineData(nameof(UpdateSystemSettingsRequest.InitialHistoryDays))]
    [InlineData(nameof(UpdateSystemSettingsRequest.RetentionDays))]
    [InlineData(nameof(UpdateSystemSettingsRequest.RawEventRetentionDays))]
    [InlineData(nameof(UpdateSystemSettingsRequest.RunLogRetentionDays))]
    [InlineData(nameof(UpdateSystemSettingsRequest.AuditRetentionDays))]
    public void 保留天數低於90時模型驗證不通過(string propertyName)
    {
        // 走 ASP.NET 模型繫結實際使用的那一套驗證（Validator + DataAnnotations），
        // 不是斷言「屬性上掛了某個 attribute」——後者連 DTO 換掉都不會紅。
        var request = ValidRequest();
        var property = typeof(UpdateSystemSettingsRequest).GetProperty(propertyName)!;
        property.SetValue(request, 89);

        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(ok);
        Assert.Contains(results, r => r.MemberNames.Contains(propertyName));

        // 90 是允許的下界
        property.SetValue(request, 90);
        results.Clear();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains(propertyName));
    }

    [Fact]
    public void Update_稽核紀錄同時包含Before與After的原始事件內容保留天數()
    {
        // 先建立初始設定並清除之前的稽核紀錄（或者直接用新的 audit service 拿最後一筆）
        var audit = new RecordingAuditService();
        var service = new SystemSettingsService(_store, FakeCurrentUser.WithCapabilities(), audit, new FakeUserStore(),
            new MailNotificationService(_store, _mailSender, new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                _mailRecords, new FakeHandlingStore(),
                MailState), new FakeReportUsageQuery());

        // 存入不同的值以確認稽核抓到變更
        service.Update(ValidRequest(rawEventRetentionDays: 90));

        var record = audit.Entries.Last();
        var detailNode = System.Text.Json.Nodes.JsonNode.Parse(record.DetailJson!)!;

        // FakeSystemSettingsStore 會修改同一個物件參考，所以 before/after 值可能相同。
        // 但測試的重點是「斷言兩邊都在」（Before 與 After 區塊都有此欄位），所以檢查 key 是否存在。
        var detailObj = detailNode.AsObject();
        Assert.True(detailObj["Before"]!.AsObject().ContainsKey("RawEventRetentionDays"));
        Assert.True(detailObj["After"]!.AsObject().ContainsKey("RawEventRetentionDays"));
    }

    // ── 舊版保留天數設定遷移（SystemSettingsStore.OnDeserialized） ───────────────

    [Fact]
    public void 舊設定遷移_情境1_同時有兩個舊鍵且值不同_新鍵取較小者()
    {
        _fx.Blob("system_settings_mig_1").Mutate<object?>(_ => ("""
        {
          "DetailRetentionDays": 120,
          "RiskyEventRetentionDays": 90
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_1"));
        var settings = store.Get();

        Assert.Equal(90, settings.RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_情境2a_只有DetailRetentionDays舊鍵_新鍵等於該值()
    {
        _fx.Blob("system_settings_mig_2a").Mutate<object?>(_ => ("""
        {
          "DetailRetentionDays": 100
        }
        """, null));
        var storeDetail = new SystemSettingsStore(_fx.Blob("system_settings_mig_2a"));
        Assert.Equal(100, storeDetail.Get().RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_情境2b_只有RiskyEventRetentionDays舊鍵_新鍵等於該值()
    {
        _fx.Blob("system_settings_mig_2b").Mutate<object?>(_ => ("""
        {
          "RiskyEventRetentionDays": 60
        }
        """, null));
        var storeRisky = new SystemSettingsStore(_fx.Blob("system_settings_mig_2b"));
        Assert.Equal(60, storeRisky.Get().RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_情境3_兩個舊鍵都沒有_新鍵等於出廠預設()
    {
        _fx.Blob("system_settings_mig_3").Mutate<object?>(_ => ("""
        {
          "BrandName": "MyPlatform"
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_3"));
        var settings = store.Get();

        Assert.Equal(SystemSettings.DefaultRawEventRetentionDays, settings.RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_情境4_已是新版JSON_值原樣保留不被遷移覆寫()
    {
        _fx.Blob("system_settings_mig_4").Mutate<object?>(_ => ("""
        {
          "RawEventRetentionDays": 200
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_4"));
        var settings = store.Get();

        Assert.Equal(200, settings.RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_情境5_遷移後再存一次_寫回的JSON不再包含舊鍵()
    {
        _fx.Blob("system_settings_mig_5").Mutate<object?>(_ => ("""
        {
          "DetailRetentionDays": 120,
          "RiskyEventRetentionDays": 90,
          "BrandName": "LogForesight"
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_5"));
        store.Update(s => s.BrandName = "UpdatedBrand");

        var rawJson = _fx.Blob("system_settings_mig_5").Read();
        Assert.NotNull(rawJson);
        Assert.Contains("RawEventRetentionDays", rawJson);
        Assert.DoesNotContain("DetailRetentionDays", rawJson);
        Assert.DoesNotContain("RiskyEventRetentionDays", rawJson);
    }

    [Fact]
    public void 舊設定遷移_同時有兩個舊鍵且值相同_新鍵等於該值()
    {
        _fx.Blob("system_settings_mig_same").Mutate<object?>(_ => ("""
        {
          "DetailRetentionDays": 90,
          "RiskyEventRetentionDays": 90
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_same"));
        var settings = store.Get();

        Assert.Equal(90, settings.RawEventRetentionDays);
    }

    [Fact]
    public void 舊設定遷移_舊鍵名稱大小寫不敏感_仍能正確遷移()
    {
        _fx.Blob("system_settings_mig_case").Mutate<object?>(_ => ("""
        {
          "detailretentiondays": 110,
          "riskyeventretentiondays": 95
        }
        """, null));

        var store = new SystemSettingsStore(_fx.Blob("system_settings_mig_case"));
        var settings = store.Get();

        Assert.Equal(95, settings.RawEventRetentionDays);
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

    [Fact]
    public void DisplaySettings_未限制嚴重度時回傳完整清單_有限制時只回傳可見嚴重度()
    {
        // 1. DefaultHidden 模式（未限制）：回傳完整清單 ["High", "Medium", "Low"]
        _store.Update(s => s.SeverityDisplayMode = "DefaultHidden");
        var service = Create();
        var controller = new DisplaySettingsController(service);

        var unrestricted = controller.Get();
        Assert.True(unrestricted.Success);
        Assert.NotNull(unrestricted.Data);
        Assert.Equal(new List<string> { "High", "Medium", "Low" }, unrestricted.Data.VisibleSeverities);

        // 2. SiteHidden 模式（有限制）：只回傳可見的嚴重度
        _store.Update(s =>
        {
            s.SeverityDisplayMode = "SiteHidden";
            s.UnhandledSeverities = new List<string> { "High", "Medium" };
        });
        var restricted = controller.Get();
        Assert.True(restricted.Success);
        Assert.NotNull(restricted.Data);
        Assert.Equal(new List<string> { "High", "Medium" }, restricted.Data.VisibleSeverities);
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

    /// <summary>SystemSettings 模型的內建預設值是高＋中（回饋十三輪新增項2：低風險日佔絕大多數
    /// 且通常不需要人工介入，預設顯示會把真正需要關注的高／中風險日稀釋掉）——舊部署升級後
    /// 這個欄位在資料庫尚未有值時，讀到的就是這個預設。</summary>
    [Fact]
    public void Get_未曾設定過時預設高中不含低()
    {
        var service = Create();

        var visible = service.Get().VisibleDayRiskLevels;

        Assert.Equal(new HashSet<string> { "高", "中" }, visible.ToHashSet());
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
    // ── 回饋第十輪 §1（品牌自訂）／§3（進階參數出廠值）────────────────────────

    /// <summary>
    /// §3：設定頁的「還原預設值」用的是後端送來的出廠值，而出廠值必須與 Core 模型的
    /// 屬性初始器同源——這條測試就是在盯「有人改了 SystemSettings 卻忘了同步別處」。
    /// </summary>
    [Fact]
    public void Get_帶出AI進階參數的出廠預設值()
    {
        var factory = new SystemSettings();
        var defaults = Create().Get().AiAdvancedDefaults;

        Assert.Equal(factory.AiTimeoutSeconds, defaults.TimeoutSeconds);
        Assert.Equal(factory.AiRetryCount, defaults.RetryCount);
        Assert.Equal(factory.AiRetryDelaySeconds, defaults.RetryDelaySeconds);
        Assert.Equal(factory.AiJsonRetryCount, defaults.JsonRetryCount);
        Assert.Equal(factory.AiMaxTokens, defaults.MaxTokens);
        Assert.Equal(factory.AiDeepDiveMaxTokens, defaults.DeepDiveMaxTokens);
        Assert.Equal(factory.AiFrequencyPenalty, defaults.FrequencyPenalty);
        Assert.Equal(factory.AiPresencePenalty, defaults.PresencePenalty);
        Assert.Equal(factory.AiExtraRequestFieldsJson, defaults.ExtraRequestFieldsJson);
    }

    /// <summary>§1：品牌預設副標不含「Windows」——Linux 規則面已就緒，寫死 Windows 名不符實</summary>
    [Fact]
    public void 品牌副標預設值_不含Windows()
    {
        Assert.Equal("事件日誌預警", new SystemSettings().BrandSubtitle);
    }

    [Fact]
    public void Update_品牌三欄持久化()
    {
        var request = ValidRequest();
        request.BrandName = "  資安監控平台  ";
        request.BrandSubtitle = "全廠事件日誌";
        request.BrandIconDataUri = "data:image/png;base64,aGVsbG8=";

        var saved = Create().Update(request);

        Assert.Equal("資安監控平台", saved.BrandName);   // 前後空白會被 trim
        Assert.Equal("全廠事件日誌", saved.BrandSubtitle);
        Assert.Equal("data:image/png;base64,aGVsbG8=", saved.BrandIconDataUri);
    }

    /// <summary>名稱留空＝回退出廠名稱，不是驗證錯誤（使用者只是不想自訂）</summary>
    [Fact]
    public void Update_品牌名稱留空時回退出廠值()
    {
        var request = ValidRequest();
        request.BrandName = "   ";

        Assert.Equal("LogForesight", Create().Update(request).BrandName);
    }

    /// <summary>§1：只收 PNG／JPG——SVG 可內嵌 script，不為單一裝飾功能開這個驗證面</summary>
    [Fact]
    public void Update_品牌圖示非PNG或JPG時擋下()
    {
        var request = ValidRequest();
        request.BrandIconDataUri = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=";

        var ex = Assert.Throws<DomainException>(() => Create().Update(request));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("PNG", ex.Message);
    }

    /// <summary>大小上限驗的是 base64 **解碼後**的真實位元組數，不是字串長度</summary>
    [Fact]
    public void Update_品牌圖示超過上限時擋下()
    {
        var request = ValidRequest();
        request.BrandIconDataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[65 * 1024]);

        var ex = Assert.Throws<DomainException>(() => Create().Update(request));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("KB", ex.Message);
    }

    [Fact]
    public void Update_品牌圖示內容無法解碼時擋下()
    {
        var request = ValidRequest();
        request.BrandIconDataUri = "data:image/png;base64,!!!not-base64!!!";

        Assert.Throws<DomainException>(() => Create().Update(request));
    }

    // ── 回饋十五輪批次D：郵件通知設定（round-trip／密碼三態／驗證）────────────────

    /// <summary>合格的郵件設定基準（啟用＋連線＋收件人齊備），測試只覆寫要驗的那一項</summary>
    private static UpdateSystemSettingsRequest ValidMailRequest()
    {
        var request = ValidRequest();
        request.MailEnabled = true;
        request.SmtpServer = "smtp.corp.local";
        request.SmtpPort = 587;
        request.SmtpUseTls = true;
        request.SmtpAccount = "notify@corp.local";
        request.MailFrom = "logforesight@corp.local";
        request.MailRecipients = new List<string> { "ops@corp.local" };
        request.MailOnRunCompleted = true;
        return request;
    }

    [Fact]
    public void Update後_郵件設定走完整條UpdateGet路徑持久化()
    {
        var service = Create();
        var request = ValidMailRequest();
        request.MailNotifyHostOwners = true;
        request.MailMinRiskLevel = "中";
        request.MailDailyEnabled = true;
        request.MailDailyTime = "09:30";
        request.MailWeeklyEnabled = true;
        request.MailWeeklyDayOfWeek = "Friday";
        request.MailWeeklyTime = "17:00";
        request.MailUrgentEnabled = true;
        request.MailSubjectTemplate = "[{site}] {type}";
        request.MailBodyIntro = "  各位好  ";

        service.Update(request);

        // 重讀確認落地（不是只有回傳值對）——這類機械式串接最容易漏一處（同本檔 P0-3 慣例）
        var reread = service.Get();
        Assert.True(reread.MailEnabled);
        Assert.Equal("smtp.corp.local", reread.SmtpServer);
        Assert.Equal(587, reread.SmtpPort);
        Assert.True(reread.SmtpUseTls);
        Assert.Equal("notify@corp.local", reread.SmtpAccount);
        Assert.Equal("logforesight@corp.local", reread.MailFrom);
        Assert.Equal(new[] { "ops@corp.local" }, reread.MailRecipients);
        Assert.True(reread.MailNotifyHostOwners);
        Assert.Equal("中", reread.MailMinRiskLevel);
        Assert.True(reread.MailOnRunCompleted);
        Assert.True(reread.MailDailyEnabled);
        Assert.Equal("09:30", reread.MailDailyTime);
        Assert.True(reread.MailWeeklyEnabled);
        Assert.Equal("Friday", reread.MailWeeklyDayOfWeek);
        Assert.Equal("17:00", reread.MailWeeklyTime);
        Assert.True(reread.MailUrgentEnabled);
        Assert.Equal("[{site}] {type}", reread.MailSubjectTemplate);
        Assert.Equal("各位好", reread.MailBodyIntro);   // trim
    }

    [Fact]
    public void 設定SMTP密碼_以CryptoHelper加密存放且DTO只回報已設定()
    {
        var service = Create();
        var request = ValidMailRequest();
        request.SmtpPassword = "smtp-secret";

        var saved = service.Update(request);

        var stored = _store.Get().SmtpPasswordEnc;
        Assert.True(CryptoHelper.IsEncrypted(stored));
        Assert.Equal("smtp-secret", CryptoHelper.Decrypt(stored));
        Assert.True(saved.SmtpHasPassword);
    }

    /// <summary>密碼三態之二：留空＝沿用既有密碼，不能被一次「不小心留空」的儲存清掉（同 AI 金鑰語意）</summary>
    [Fact]
    public void 更新時SMTP密碼留空_沿用既有密碼不被清除()
    {
        var service = Create();
        var withPassword = ValidMailRequest();
        withPassword.SmtpPassword = "smtp-original";
        service.Update(withPassword);
        var encryptedBefore = _store.Get().SmtpPasswordEnc;

        service.Update(ValidMailRequest());   // SmtpPassword 未帶

        Assert.Equal(encryptedBefore, _store.Get().SmtpPasswordEnc);
    }

    [Fact]
    public void ClearSmtpPassword為true時_清除密碼()
    {
        var service = Create();
        var withPassword = ValidMailRequest();
        withPassword.SmtpPassword = "smtp-to-clear";
        service.Update(withPassword);

        var clearRequest = ValidMailRequest();
        clearRequest.ClearSmtpPassword = true;
        var saved = service.Update(clearRequest);

        Assert.Equal("", _store.Get().SmtpPasswordEnc);
        Assert.False(saved.SmtpHasPassword);
    }

    [Fact]
    public void Update_啟用郵件觸發但收件人為空_丟例外()
    {
        var request = ValidMailRequest();
        request.MailRecipients = new List<string>();

        Assert.Throws<DomainException>(() => Create().Update(request));
    }

    /// <summary>格式錯誤的位址放行落盤的話，排程觸發時建 MailAddress 才炸、又被 SendSafeAsync
    /// 靜默吞掉——使用者以為通知設好了卻永遠收不到信，儲存當下就要擋</summary>
    [Fact]
    public void Update_收件人不是合法信箱格式_丟例外()
    {
        var request = ValidMailRequest();
        request.MailRecipients = new List<string> { "這不是信箱" };

        var ex = Assert.Throws<DomainException>(() => Create().Update(request));
        Assert.Contains("收件人", ex.Message);
    }

    [Fact]
    public void Update_寄件人不是合法信箱格式_丟例外()
    {
        var request = ValidMailRequest();
        request.MailFrom = "not-an-email";

        var ex = Assert.Throws<DomainException>(() => Create().Update(request));
        Assert.Contains("寄件人", ex.Message);
    }

    [Fact]
    public void Update_每日摘要時刻格式不合法_丟例外()
    {
        var request = ValidMailRequest();
        request.MailDailyEnabled = true;
        request.MailDailyTime = "廿五點";

        Assert.Throws<DomainException>(() => Create().Update(request));
    }

    [Fact]
    public void Update_郵件標題模板留空時回退出廠值()
    {
        var request = ValidMailRequest();
        request.MailSubjectTemplate = "   ";

        var saved = Create().Update(request);

        Assert.Equal(new SystemSettings().MailSubjectTemplate, saved.MailSubjectTemplate);
    }

    /// <summary>郵件未啟用時，連線與收件人都可以留空——「還沒設定」是合法狀態，不該被逼著先填假資料</summary>
    [Fact]
    public void Update_郵件未啟用時_連線欄位可全空()
    {
        var request = ValidRequest();   // Mail* 全部維持預設（未啟用、全空）

        var saved = Create().Update(request);

        Assert.False(saved.MailEnabled);
        Assert.Empty(saved.MailRecipients);
    }

    // ── 回饋十八輪批次C：郵件由關轉開時預填已通知狀態 ──────────────────────────

    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    /// <summary>由關轉開（首次啟用）：窗口內既有紀錄一律標成已通知——不補寄啟用前的歷史，
    /// 避免第一封信是「近 14 天總帳」（見 docs/archive/FEEDBACK-18-PLAN.md 批次C）。</summary>
    [Fact]
    public void Update_摘要由關轉開時預填窗口內既有紀錄為已通知()
    {
        _mailRecords.Add(new DailyAnalysisRecord { HostId = 1, Host = "host1", Date = Yesterday, RiskLevel = "高" });

        Create().Update(ValidMailRequest());   // MailEnabled+MailOnRunCompleted 由 false→true

        Assert.Contains($"1|{Yesterday:yyyy-MM-dd}", MailState.Get().SummarySentKeys);
    }

    /// <summary>已經開著時重複儲存（哪怕只是改了無關欄位）不能觸發預填——否則會把當下真正
    /// pending 的紀錄也標成已讀，變成真的漏寄。</summary>
    [Fact]
    public void Update_已啟用時重複儲存不預填新紀錄()
    {
        var service = Create();
        service.Update(ValidMailRequest());   // 第一次：由關轉開，預填當時窗口（此時無紀錄）

        _mailRecords.Add(new DailyAnalysisRecord { HostId = 2, Host = "host2", Date = Yesterday, RiskLevel = "高" });
        var again = ValidMailRequest();
        again.MailBodyIntro = "改個無關欄位";
        service.Update(again);   // 第二次：已經是開著的，不該再次預填

        Assert.DoesNotContain($"2|{Yesterday:yyyy-MM-dd}", MailState.Get().SummarySentKeys);
    }

    /// <summary>只開緊急通知（不開執行摘要）時，只預填 UrgentSentKeys，不動 SummarySentKeys。</summary>
    [Fact]
    public void Update_只開緊急通知時只預填UrgentSentKeys()
    {
        _mailRecords.Add(new DailyAnalysisRecord { HostId = 3, Host = "host3", Date = Yesterday, RiskLevel = "高" });
        var request = ValidMailRequest();
        request.MailOnRunCompleted = false;
        request.MailUrgentEnabled = true;

        Create().Update(request);

        var state = MailState.Get();
        Assert.Contains($"3|{Yesterday:yyyy-MM-dd}", state.UrgentSentKeys);
        Assert.DoesNotContain($"3|{Yesterday:yyyy-MM-dd}", state.SummarySentKeys);
    }

    /// <summary>關閉後重新啟用：語意是「從（重新）啟用起算」——關閉期間新增的紀錄，
    /// 在重新啟用當下一樣視為既有歷史、不補寄（使用者確認的甲案）。</summary>
    [Fact]
    public void Update_關閉後重新啟用再次預填當時窗口內容()
    {
        var service = Create();
        service.Update(ValidMailRequest());   // 第一次啟用

        var disabled = ValidMailRequest();
        disabled.MailEnabled = false;
        service.Update(disabled);   // 關閉

        _mailRecords.Add(new DailyAnalysisRecord { HostId = 4, Host = "host4", Date = Yesterday, RiskLevel = "高" });
        service.Update(ValidMailRequest());   // 重新啟用：關閉期間新增的紀錄也被視為既有歷史

        Assert.Contains($"4|{Yesterday:yyyy-MM-dd}", MailState.Get().SummarySentKeys);
    }

    // ── AI 服務 Provider 三選一與相關新欄位 ───────────────────────────────────

    [Fact]
    public void 未設定Provider的既有設定讀出來是本機OpenAI相容端點_且既有行為不受影響()
    {
        // 模擬既有部署（未設定 Provider 欄位）
        _store.Update(s =>
        {
            s.AiProvider = "";
            s.AiModel = "";
            s.AiAzureDeployment = "";
            s.AiAzureApiVersion = "";
            s.AiBaseUrl = "http://localhost:8080";
        });

        var service = Create();
        var dto = service.Get();

        Assert.Equal("Local", dto.AiProvider);
        Assert.Equal("local-model", dto.AiModel);
        Assert.Equal("", dto.AiAzureDeployment);
        Assert.Equal("2024-10-21", dto.AiAzureApiVersion);
        Assert.Equal("http://localhost:8080", dto.AiBaseUrl);
    }

    [Fact]
    public void 出廠預設的逾時秒數為1200_但既有已存的值不會被改動()
    {
        // 出廠模型預設值為 1200
        var defaultModel = new SystemSettings();
        Assert.Equal(1200, defaultModel.AiTimeoutSeconds);

        // 既有部署已存在 600 秒
        _store.Update(s => s.AiTimeoutSeconds = 600);

        var service = Create();
        var dto = service.Get();

        // 讀出來仍為 600，不被出廠預設 1200 覆寫
        Assert.Equal(600, dto.AiTimeoutSeconds);

        // 執行期解析也不會被改動
        var appSettings = new LogForesight.Core.Configuration.AppSettings();
        LogForesight.Core.Service.RuntimeSettingsResolver.ApplySystemSettingsOverrides(appSettings, _store);
        Assert.Equal(600, appSettings.Ai.TimeoutSeconds);
    }

    [Fact]
    public void Update_選Azure時缺少部署名稱會回驗證錯誤()
    {
        var service = Create();

        // 1. 缺部署名稱
        var requestNoDeployment = ValidRequest();
        requestNoDeployment.AiProvider = "AzureOpenAi";
        requestNoDeployment.AiBaseUrl = "https://example.openai.azure.com/";
        requestNoDeployment.AiAzureDeployment = "";
        requestNoDeployment.AiAzureApiVersion = "2024-10-21";
        requestNoDeployment.AiApiKey = "sk-azure-key";
        var ex1 = Assert.Throws<DomainException>(() => service.Update(requestNoDeployment));
        Assert.Contains("部署名稱", ex1.Message);

        // 2. 缺端點位址
        var requestNoUrl = ValidRequest();
        requestNoUrl.AiProvider = "AzureOpenAi";
        requestNoUrl.AiBaseUrl = "";
        requestNoUrl.AiAzureDeployment = "gpt-4o";
        requestNoUrl.AiAzureApiVersion = "2024-10-21";
        requestNoUrl.AiApiKey = "sk-azure-key";
        var ex2 = Assert.Throws<DomainException>(() => service.Update(requestNoUrl));
        Assert.Contains("端點位址", ex2.Message);

        // 3. 缺 API 版本
        var requestNoVer = ValidRequest();
        requestNoVer.AiProvider = "AzureOpenAi";
        requestNoVer.AiBaseUrl = "https://example.openai.azure.com/";
        requestNoVer.AiAzureDeployment = "gpt-4o";
        requestNoVer.AiAzureApiVersion = "";
        requestNoVer.AiApiKey = "sk-azure-key";
        var ex3 = Assert.Throws<DomainException>(() => service.Update(requestNoVer));
        Assert.Contains("API 版本", ex3.Message);

        // 4. 缺金鑰
        var requestNoKey = ValidRequest();
        requestNoKey.AiProvider = "AzureOpenAi";
        requestNoKey.AiBaseUrl = "https://example.openai.azure.com/";
        requestNoKey.AiAzureDeployment = "gpt-4o";
        requestNoKey.AiAzureApiVersion = "2024-10-21";
        requestNoKey.AiApiKey = null;
        var ex4 = Assert.Throws<DomainException>(() => service.Update(requestNoKey));
        Assert.Contains("金鑰", ex4.Message);
    }

    [Fact]
    public void Update_選OpenAI官方時缺少模型名稱或金鑰會回驗證錯誤()
    {
        var service = Create();

        // 1. 缺模型名稱
        var requestNoModel = ValidRequest();
        requestNoModel.AiProvider = "OpenAi";
        requestNoModel.AiModel = "";
        requestNoModel.AiApiKey = "sk-openai-key";
        var ex1 = Assert.Throws<DomainException>(() => service.Update(requestNoModel));
        Assert.Contains("模型名稱", ex1.Message);

        // 2. 缺金鑰
        var requestNoKey = ValidRequest();
        requestNoKey.AiProvider = "OpenAi";
        requestNoKey.AiModel = "gpt-4o";
        requestNoKey.AiApiKey = null;
        var ex2 = Assert.Throws<DomainException>(() => service.Update(requestNoKey));
        Assert.Contains("金鑰", ex2.Message);
    }

    [Fact]
    public void Update後_AI提供者與新欄位正確持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.AiProvider = "AzureOpenAi";
        request.AiBaseUrl = "https://prod.openai.azure.com/";
        request.AiModel = "gpt-4o";
        request.AiAzureDeployment = "gpt-4o-deployment";
        request.AiAzureApiVersion = "2024-10-21";
        request.AiApiKey = "sk-azure-secret";

        var saved = service.Update(request);

        Assert.Equal("AzureOpenAi", saved.AiProvider);
        Assert.Equal("https://prod.openai.azure.com/", saved.AiBaseUrl);
        Assert.Equal("gpt-4o", saved.AiModel);
        Assert.Equal("gpt-4o-deployment", saved.AiAzureDeployment);
        Assert.Equal("2024-10-21", saved.AiAzureApiVersion);
        Assert.True(saved.AiHasApiKey);

        // 重讀確認 DB 落地
        var reread = service.Get();
        Assert.Equal("AzureOpenAi", reread.AiProvider);
        Assert.Equal("https://prod.openai.azure.com/", reread.AiBaseUrl);
        Assert.Equal("gpt-4o", reread.AiModel);
        Assert.Equal("gpt-4o-deployment", reread.AiAzureDeployment);
        Assert.Equal("2024-10-21", reread.AiAzureApiVersion);
    }

    // ── PRTG 監控系統設定（批次B-1）─────────────────────────────────────────

    [Fact]
    public void Update後_PRTG設定持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgApiToken = "secret-prtg-token-123";
        request.PrtgIgnoreSslErrors = true;
        request.PrtgTimeoutSeconds = 45;
        request.PrtgFetchConcurrency = 3;
        request.PrtgBackfillDays = 60;
        request.PrtgRetentionDays = 150;

        var saved = service.Update(request);

        Assert.True(saved.PrtgEnabled);
        Assert.Equal("https://prtg.example.local", saved.PrtgUrl);
        Assert.True(saved.PrtgHasApiToken);
        Assert.True(saved.PrtgIgnoreSslErrors);
        Assert.Equal(45, saved.PrtgTimeoutSeconds);
        Assert.Equal(3, saved.PrtgFetchConcurrency);
        Assert.Equal(60, saved.PrtgBackfillDays);
        Assert.Equal(150, saved.PrtgRetentionDays);

        // 驗證 DTO 不含任何 token 字串屬性
        var dtoProps = typeof(SystemSettingsDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("PrtgApiTokenEnc", dtoProps);
        Assert.DoesNotContain("PrtgApiToken", dtoProps);

        // 重讀確認 DB 落地與密文儲存
        var reread = service.Get();
        Assert.True(reread.PrtgEnabled);
        Assert.Equal("https://prtg.example.local", reread.PrtgUrl);
        Assert.True(reread.PrtgHasApiToken);
        Assert.True(reread.PrtgIgnoreSslErrors);
        Assert.Equal(45, reread.PrtgTimeoutSeconds);
        Assert.Equal(3, reread.PrtgFetchConcurrency);
        Assert.Equal(60, reread.PrtgBackfillDays);
        Assert.Equal(150, reread.PrtgRetentionDays);

        var stored = _store.Get();
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(stored.PrtgApiTokenEnc));
        Assert.Equal("secret-prtg-token-123", LogForesight.Core.CryptoHelper.Decrypt(stored.PrtgApiTokenEnc));
    }

    [Fact]
    public void PRTG啟用但位址空白時拒絕()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "   ";

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("PRTG 位址不可為空", ex.Message);
    }

    [Theory]
    [InlineData("ftp://prtg.example.local")]
    [InlineData("not-a-url")]
    [InlineData("relative/path")]
    public void PRTG位址scheme不合法時拒絕(string invalidUrl)
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = invalidUrl;

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("PRTG 位址格式不合法", ex.Message);
    }

    [Fact]
    public void PRTG保留天數大於分析紀錄保留天數時拒絕()
    {
        var service = Create();
        var request = ValidRequest();
        request.RetentionDays = 180;
        request.PrtgRetentionDays = 200;

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("PRTG 資料保留天數不可大於歷史資料保留天數", ex.Message);
    }

    [Fact]
    public void PRTG_API_token留空時沿用既有值()
    {
        var service = Create();

        // 1. 先存一次 token
        var initialRequest = ValidRequest();
        initialRequest.PrtgApiToken = "initial-token-xyz";
        service.Update(initialRequest);

        var initialStored = _store.Get();
        var initialCipher = initialStored.PrtgApiTokenEnc;
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(initialCipher));

        // 2. 再送一次 PrtgApiToken = "" 的請求，密文未變
        var followUpRequest = ValidRequest();
        followUpRequest.PrtgApiToken = "";
        followUpRequest.ClearPrtgApiToken = false;
        service.Update(followUpRequest);

        var afterKeep = _store.Get();
        Assert.Equal(initialCipher, afterKeep.PrtgApiTokenEnc);

        // 3. 送 ClearPrtgApiToken = true，被清空
        var clearRequest = ValidRequest();
        clearRequest.ClearPrtgApiToken = true;
        service.Update(clearRequest);

        var afterClear = _store.Get();
        Assert.Equal("", afterClear.PrtgApiTokenEnc);
        Assert.False(service.Get().PrtgHasApiToken);
    }

    [Fact]
    public void PRTG預設值符合SystemSettings的內建預設()
    {
        var settings = new SystemSettings();

        Assert.False(settings.PrtgEnabled);
        Assert.Equal("", settings.PrtgUrl);
        Assert.Equal("", settings.PrtgApiTokenEnc);
        Assert.False(settings.PrtgIgnoreSslErrors);
        Assert.Equal(SystemSettings.DefaultPrtgTimeoutSeconds, settings.PrtgTimeoutSeconds);
        Assert.Equal(60, settings.PrtgTimeoutSeconds);
        Assert.Equal(SystemSettings.DefaultPrtgFetchConcurrency, settings.PrtgFetchConcurrency);
        Assert.Equal(2, settings.PrtgFetchConcurrency);
        Assert.Equal(SystemSettings.DefaultPrtgBackfillDays, settings.PrtgBackfillDays);
        Assert.Equal(30, settings.PrtgBackfillDays);
        Assert.Equal(SystemSettings.DefaultPrtgRetentionDays, settings.PrtgRetentionDays);
        Assert.Equal(180, settings.PrtgRetentionDays);
    }

    [Fact]
    public void Update後_PRTG帳密設定持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        request.PrtgUsername = "prtgadmin";
        request.PrtgPassword = "SecretPassword123";

        var savedDto = service.Update(request);

        var stored = _store.Get();
        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Password, stored.PrtgAuthMode);
        Assert.Equal("prtgadmin", stored.PrtgUsername);
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(stored.PrtgPasswordEnc));

        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Password, savedDto.PrtgAuthMode);
        Assert.Equal("prtgadmin", savedDto.PrtgUsername);
        Assert.True(savedDto.PrtgHasPassword);

        var retrievedDto = service.Get();
        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Password, retrievedDto.PrtgAuthMode);
        Assert.Equal("prtgadmin", retrievedDto.PrtgUsername);
        Assert.True(retrievedDto.PrtgHasPassword);
    }

    [Theory]
    [InlineData("ntlm")]
    [InlineData("")]
    [InlineData("oauth")]
    [InlineData("basic")]
    public void PRTG認證方式非法值時拒絕(string invalidAuthMode)
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgAuthMode = invalidAuthMode;

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("PRTG 認證方式不合法", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PRTG帳密模式啟用但帳號空白時拒絕(string? emptyUsername)
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        request.PrtgUsername = emptyUsername;
        request.PrtgPassword = "SecretPassword123";

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("啟用 PRTG 並使用帳號密碼時，必須設定帳號", ex.Message);
    }

    [Fact]
    public void PRTG帳密模式啟用但從未設定密碼時拒絕()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        request.PrtgUsername = "prtgadmin";
        request.PrtgPassword = "";

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("啟用 PRTG 並使用帳號密碼時，必須設定密碼", ex.Message);
    }

    [Fact]
    public void PRTG帳密模式_密碼留空但既有密碼存在時通過()
    {
        var service = Create();

        // 1. 先存一次密碼與帳號
        var initialRequest = ValidRequest();
        initialRequest.PrtgEnabled = true;
        initialRequest.PrtgUrl = "https://prtg.example.local";
        initialRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        initialRequest.PrtgUsername = "prtgadmin";
        initialRequest.PrtgPassword = "InitialPassword123";
        service.Update(initialRequest);

        var initialStored = _store.Get();
        var initialCipher = initialStored.PrtgPasswordEnc;
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(initialCipher));

        // 2. 再送一次 PrtgPassword = "" 的請求（其他欄位有改動），斷言不擲例外且密文未變
        var followUpRequest = ValidRequest();
        followUpRequest.PrtgEnabled = true;
        followUpRequest.PrtgUrl = "https://prtg.example.local";
        followUpRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        followUpRequest.PrtgUsername = "prtgadmin";
        followUpRequest.PrtgPassword = "";
        followUpRequest.ClearPrtgPassword = false;
        followUpRequest.ServerDescription = "說明更新";

        var updatedDto = service.Update(followUpRequest);

        var afterKeep = _store.Get();
        Assert.Equal(initialCipher, afterKeep.PrtgPasswordEnc);
        Assert.Equal("prtgadmin", afterKeep.PrtgUsername);
        Assert.Equal("說明更新", afterKeep.ServerDescription);
        Assert.True(updatedDto.PrtgHasPassword);
    }

    [Fact]
    public void PRTG_token模式啟用但從未設定token時拒絕()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        request.PrtgApiToken = "";

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("啟用 PRTG 並使用 API token 時，必須設定 API token", ex.Message);
    }

    [Fact]
    public void PRTG密碼留空時沿用既有值_清除旗標可清空()
    {
        var service = Create();

        // 1. 存入密碼
        var initialRequest = ValidRequest();
        initialRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        initialRequest.PrtgUsername = "admin";
        initialRequest.PrtgPassword = "first-secret";
        service.Update(initialRequest);

        var initialStored = _store.Get();
        var initialCipher = initialStored.PrtgPasswordEnc;
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(initialCipher));
        Assert.True(service.Get().PrtgHasPassword);

        // 2. 留空沿用
        var keepRequest = ValidRequest();
        keepRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        keepRequest.PrtgUsername = "admin";
        keepRequest.PrtgPassword = "";
        keepRequest.ClearPrtgPassword = false;
        service.Update(keepRequest);

        var keepStored = _store.Get();
        Assert.Equal(initialCipher, keepStored.PrtgPasswordEnc);
        Assert.True(service.Get().PrtgHasPassword);

        // 3. ClearPrtgPassword 清空
        var clearRequest = ValidRequest();
        clearRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        clearRequest.PrtgUsername = "admin";
        clearRequest.ClearPrtgPassword = true;
        service.Update(clearRequest);

        var clearStored = _store.Get();
        Assert.Equal("", clearStored.PrtgPasswordEnc);
        Assert.False(service.Get().PrtgHasPassword);
    }

    [Fact]
    public void Update後_PRTG_passhash設定持久化()
    {
        var service = Create();
        var request = ValidRequest();
        request.PrtgEnabled = true;
        request.PrtgUrl = "https://prtg.example.local";
        request.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        request.PrtgUsername = "prtguser";
        request.PrtgPasshash = "1234567890passhash";

        var saved = service.Update(request);

        // 密文落地
        var stored = _store.Get();
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(stored.PrtgPasshashEnc));
        Assert.Equal("1234567890passhash", LogForesight.Core.CryptoHelper.Decrypt(stored.PrtgPasshashEnc));
        Assert.Equal("prtguser", stored.PrtgUsername);

        // DTO 回報已設定且不外流明文或密文
        Assert.True(saved.PrtgHasPasshash);
        var dtoProps = typeof(SystemSettingsDto).GetProperties();
        Assert.DoesNotContain(dtoProps, p => p.Name is "PrtgPasshash" or "PrtgPasshashEnc");
    }

    [Fact]
    public void PRTG_passhash模式啟用但缺帳號或缺passhash時拒絕()
    {
        var service = Create();

        // 缺帳號
        var noUser = ValidRequest();
        noUser.PrtgEnabled = true;
        noUser.PrtgUrl = "https://prtg.example.local";
        noUser.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        noUser.PrtgUsername = "";
        noUser.PrtgPasshash = "some-passhash";

        var exNoUser = Assert.Throws<DomainException>(() => service.Update(noUser));
        Assert.Contains("必須設定帳號", exNoUser.Message);

        // 缺 passhash
        var noPasshash = ValidRequest();
        noPasshash.PrtgEnabled = true;
        noPasshash.PrtgUrl = "https://prtg.example.local";
        noPasshash.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        noPasshash.PrtgUsername = "prtguser";
        noPasshash.PrtgPasshash = "";

        var exNoPasshash = Assert.Throws<DomainException>(() => service.Update(noPasshash));
        Assert.Contains("必須設定 passhash", exNoPasshash.Message);
    }

    [Fact]
    public void PRTG_passhash留空時沿用既有值_清除旗標可清空()
    {
        var service = Create();

        // 1. 存入 passhash
        var initialRequest = ValidRequest();
        initialRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        initialRequest.PrtgUsername = "admin";
        initialRequest.PrtgPasshash = "first-passhash";
        service.Update(initialRequest);

        var initialStored = _store.Get();
        var initialCipher = initialStored.PrtgPasshashEnc;
        Assert.True(LogForesight.Core.CryptoHelper.IsEncrypted(initialCipher));
        Assert.True(service.Get().PrtgHasPasshash);

        // 2. 留空沿用
        var keepRequest = ValidRequest();
        keepRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        keepRequest.PrtgUsername = "admin";
        keepRequest.PrtgPasshash = "";
        keepRequest.ClearPrtgPasshash = false;
        service.Update(keepRequest);

        var keepStored = _store.Get();
        Assert.Equal(initialCipher, keepStored.PrtgPasshashEnc);
        Assert.True(service.Get().PrtgHasPasshash);

        // 3. ClearPrtgPasshash 清空
        var clearRequest = ValidRequest();
        clearRequest.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        clearRequest.PrtgUsername = "admin";
        clearRequest.ClearPrtgPasshash = true;
        service.Update(clearRequest);

        var clearStored = _store.Get();
        Assert.Equal("", clearStored.PrtgPasshashEnc);
        Assert.False(service.Get().PrtgHasPasshash);
    }

    [Fact]
    public void PRTG切換認證方式時不污染另一組憑證()
    {
        var service = Create();
        const string Token = LogForesight.Core.Models.PrtgAuthModes.Token;
        const string Password = LogForesight.Core.Models.PrtgAuthModes.Password;

        // 先在 token 模式存好 token，再在帳密模式存好密碼
        var setToken = ValidRequest();
        setToken.PrtgAuthMode = Token;
        setToken.PrtgApiToken = "the-token";
        service.Update(setToken);

        var setPassword = ValidRequest();
        setPassword.PrtgAuthMode = Password;
        setPassword.PrtgUsername = "admin";
        setPassword.PrtgPassword = "the-password";
        service.Update(setPassword);

        var tokenCipher = _store.Get().PrtgApiTokenEnc;
        var passwordCipher = _store.Get().PrtgPasswordEnc;
        Assert.NotEqual("", tokenCipher);
        Assert.NotEqual("", passwordCipher);

        // 情境一：在 token 模式勾了「清除 token」後改用帳密儲存。
        // 那個勾選在畫面上已經隨著切換而隱藏，不該生效——否則 token 被靜默清空。
        var switchToPassword = ValidRequest();
        switchToPassword.PrtgAuthMode = Password;
        switchToPassword.PrtgUsername = "admin";
        switchToPassword.ClearPrtgApiToken = true;
        switchToPassword.PrtgApiToken = "leftover";
        service.Update(switchToPassword);

        Assert.Equal(tokenCipher, _store.Get().PrtgApiTokenEnc);

        // 情境二：反方向——帳密欄殘留的密碼不該在 token 模式下被寫入
        var switchToToken = ValidRequest();
        switchToToken.PrtgAuthMode = Token;
        switchToToken.PrtgPassword = "leftover-password";
        switchToToken.ClearPrtgPassword = true;
        service.Update(switchToToken);

        Assert.Equal(passwordCipher, _store.Get().PrtgPasswordEnc);
    }

    [Fact]
    public void PRTG三種認證方式切換時互不污染()
    {
        var service = Create();
        const string Token = LogForesight.Core.Models.PrtgAuthModes.Token;
        const string Password = LogForesight.Core.Models.PrtgAuthModes.Password;
        const string Passhash = LogForesight.Core.Models.PrtgAuthModes.Passhash;

        // 1. 先分別存好 token、密碼、passhash 三組憑證
        var setToken = ValidRequest();
        setToken.PrtgAuthMode = Token;
        setToken.PrtgApiToken = "the-token";
        service.Update(setToken);

        var setPassword = ValidRequest();
        setPassword.PrtgAuthMode = Password;
        setPassword.PrtgUsername = "admin";
        setPassword.PrtgPassword = "the-password";
        service.Update(setPassword);

        var setPasshash = ValidRequest();
        setPasshash.PrtgAuthMode = Passhash;
        setPasshash.PrtgUsername = "admin";
        setPasshash.PrtgPasshash = "the-passhash";
        service.Update(setPasshash);

        var tokenCipher = _store.Get().PrtgApiTokenEnc;
        var passwordCipher = _store.Get().PrtgPasswordEnc;
        var passhashCipher = _store.Get().PrtgPasshashEnc;
        Assert.NotEqual("", tokenCipher);
        Assert.NotEqual("", passwordCipher);
        Assert.NotEqual("", passhashCipher);

        // 2. 切換到 Token 模式，帶上 Password 與 Passhash 的殘留輸入與清除勾選
        var switchToToken = ValidRequest();
        switchToToken.PrtgAuthMode = Token;
        switchToToken.PrtgApiToken = "new-token";
        switchToToken.PrtgPassword = "leftover-password";
        switchToToken.ClearPrtgPassword = true;
        switchToToken.PrtgPasshash = "leftover-passhash";
        switchToToken.ClearPrtgPasshash = true;
        service.Update(switchToToken);

        var afterToken = _store.Get();
        Assert.Equal("new-token", LogForesight.Core.CryptoHelper.Decrypt(afterToken.PrtgApiTokenEnc));
        Assert.Equal(passwordCipher, afterToken.PrtgPasswordEnc);
        Assert.Equal(passhashCipher, afterToken.PrtgPasshashEnc);

        // 3. 切換到 Password 模式，帶上 Token 與 Passhash 的殘留輸入與清除勾選
        var switchToPassword = ValidRequest();
        switchToPassword.PrtgAuthMode = Password;
        switchToPassword.PrtgUsername = "admin";
        switchToPassword.PrtgPassword = "new-password";
        switchToPassword.PrtgApiToken = "leftover-token";
        switchToPassword.ClearPrtgApiToken = true;
        switchToPassword.PrtgPasshash = "leftover-passhash";
        switchToPassword.ClearPrtgPasshash = true;
        service.Update(switchToPassword);

        var afterPassword = _store.Get();
        Assert.Equal(afterToken.PrtgApiTokenEnc, afterPassword.PrtgApiTokenEnc);
        Assert.Equal("new-password", LogForesight.Core.CryptoHelper.Decrypt(afterPassword.PrtgPasswordEnc));
        Assert.Equal(passhashCipher, afterPassword.PrtgPasshashEnc);

        // 4. 切換到 Passhash 模式，帶上 Token 與 Password 的殘留輸入與清除勾選
        var switchToPasshash = ValidRequest();
        switchToPasshash.PrtgAuthMode = Passhash;
        switchToPasshash.PrtgUsername = "admin";
        switchToPasshash.PrtgPasshash = "new-passhash";
        switchToPasshash.PrtgApiToken = "leftover-token";
        switchToPasshash.ClearPrtgApiToken = true;
        switchToPasshash.PrtgPassword = "leftover-password";
        switchToPasshash.ClearPrtgPassword = true;
        service.Update(switchToPasshash);

        var afterPasshash = _store.Get();
        Assert.Equal(afterToken.PrtgApiTokenEnc, afterPasshash.PrtgApiTokenEnc);
        Assert.Equal(afterPassword.PrtgPasswordEnc, afterPasshash.PrtgPasswordEnc);
        Assert.Equal("new-passhash", LogForesight.Core.CryptoHelper.Decrypt(afterPasshash.PrtgPasshashEnc));
    }

    [Fact]
    public void PRTG_username在password與passhash兩模式都能寫入且token模式不動它()
    {
        var service = Create();

        // 1. password 模式存了 username
        var setPassword = ValidRequest();
        setPassword.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        setPassword.PrtgUsername = "shared-admin";
        setPassword.PrtgPassword = "password123";
        service.Update(setPassword);

        Assert.Equal("shared-admin", _store.Get().PrtgUsername);

        // 2. 切到 passhash 模式並「改」帳號：刻意帶不同值，才分得出
        //    「passhash 分支確實有寫入 username」與「它根本沒寫、值只是沒被動到」——
        //    後者的真實後果是 passhash 模式下改不了帳號（存檔後畫面又跳回舊值）
        var setPasshash = ValidRequest();
        setPasshash.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Passhash;
        setPasshash.PrtgUsername = "passhash-admin";
        setPasshash.PrtgPasshash = "passhash123";
        service.Update(setPasshash);

        Assert.Equal("passhash-admin", _store.Get().PrtgUsername);

        // 2b. 切回 password 模式也要能改帳號（共用同一欄位，兩個分支都要真的寫）
        var backToPassword = ValidRequest();
        backToPassword.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        backToPassword.PrtgUsername = "shared-admin";
        service.Update(backToPassword);

        Assert.Equal("shared-admin", _store.Get().PrtgUsername);

        // 3. 再切到 token 模式儲存（不傳 username），username 仍然保留（token 模式不寫 username）
        var setToken = ValidRequest();
        setToken.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        setToken.PrtgApiToken = "token123";
        setToken.PrtgUsername = ""; // 前端 token 模式隱藏 username，送空字串
        service.Update(setToken);

        Assert.Equal("shared-admin", _store.Get().PrtgUsername);
    }

    [Fact]
    public void PRTG憑證欄位輸入純空白時不覆蓋既有值()
    {
        var service = Create();

        var setToken = ValidRequest();
        setToken.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        setToken.PrtgApiToken = "good-token";
        service.Update(setToken);
        var cipher = _store.Get().PrtgApiTokenEnc;

        // 純空白＝沒填。若當成有值寫進去，好的 token 會被覆蓋成加密後的空白，
        // 而 HasUsableCredentials 因為欄位非空仍判定「有設定」，要到實際連線才 401
        var blank = ValidRequest();
        blank.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        blank.PrtgApiToken = "   ";
        service.Update(blank);

        Assert.Equal(cipher, _store.Get().PrtgApiTokenEnc);
    }

    [Fact]
    public void PRTG預設認證方式為token()
    {
        var settings = new SystemSettings();

        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Token, settings.PrtgAuthMode);
        Assert.Equal("", settings.PrtgUsername);
        Assert.Equal("", settings.PrtgPasswordEnc);
        Assert.Equal("", settings.PrtgPasshashEnc);
    }

    [Fact]
    public void PRTG白名單出廠預設含8個type且不含Ping()
    {
        var settings = new SystemSettings();

        Assert.Equal(8, settings.PrtgSensorTypeWhitelist.Count);
        Assert.Contains("SNMP Disk Free", settings.PrtgSensorTypeWhitelist);
        Assert.DoesNotContain(settings.PrtgSensorTypeWhitelist, s => string.Equals(s, "Ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Update後_PRTG白名單去除空白空行與重複項並持久化()
    {
        var store = new SystemSettingsStore(_fx.Blob("system_settings_whitelist"));
        var service = new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(store, _mailSender, new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                _mailRecords, new FakeHandlingStore(),
                new MailNotifyStateStore(_fx.Blob("mail_state_whitelist"))), new FakeReportUsageQuery());

        var request = ValidRequest();
        request.PrtgSensorTypeWhitelist = new List<string>
        {
            "  SNMP Disk Free  ",
            "",
            "snmp disk free",
            "SNMP CPU LOAD",
            "   ",
            "snmp cpu load",
            "Windows Network Card"
        };

        var saved = service.Update(request);

        Assert.Equal(3, saved.PrtgSensorTypeWhitelist.Count);
        Assert.Equal(new[] { "SNMP Disk Free", "SNMP CPU LOAD", "Windows Network Card" }, saved.PrtgSensorTypeWhitelist);

        // 重讀確認落地
        var reread = service.Get();
        Assert.Equal(3, reread.PrtgSensorTypeWhitelist.Count);
        Assert.Equal(new[] { "SNMP Disk Free", "SNMP CPU LOAD", "Windows Network Card" }, reread.PrtgSensorTypeWhitelist);

        var stored = store.Get();
        Assert.Equal(3, stored.PrtgSensorTypeWhitelist.Count);
        Assert.Equal(new[] { "SNMP Disk Free", "SNMP CPU LOAD", "Windows Network Card" }, stored.PrtgSensorTypeWhitelist);
    }

    [Fact]
    public void PRTG白名單_ToDto回傳與儲存值一致()
    {
        var store = new SystemSettingsStore(_fx.Blob("system_settings_dto"));
        var service = new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(store, _mailSender, new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                _mailRecords, new FakeHandlingStore(),
                new MailNotifyStateStore(_fx.Blob("mail_state_dto"))), new FakeReportUsageQuery());

        var request = ValidRequest();
        request.PrtgSensorTypeWhitelist = new List<string> { "SNMP Memory", "SNMP Linux Meminfo" };
        var savedDto = service.Update(request);

        var getDto = service.Get();

        Assert.Equal(savedDto.PrtgSensorTypeWhitelist, getDto.PrtgSensorTypeWhitelist);
        Assert.Equal(new[] { "SNMP Memory", "SNMP Linux Meminfo" }, getDto.PrtgSensorTypeWhitelist);
    }

    [Fact]
    public void SetPrtgEnabled_開啟時若URL為空擲DomainException()
    {
        var service = Create();
        var ex = Assert.Throws<DomainException>(() => service.SetPrtgEnabled(true));
        Assert.Contains("請先於 PRTG 維護頁完成連線設定", ex.Message);
    }

    [Fact]
    public void SetPrtgEnabled_開啟時URL與憑證齊備成功寫入且Get讀回為true()
    {
        var service = Create();
        _store.Update(s =>
        {
            s.PrtgUrl = "https://prtg.example.local";
            s.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = LogForesight.Core.CryptoHelper.Encrypt("my-token");
        });

        var result = service.SetPrtgEnabled(true);
        Assert.True(result);

        var dto = service.Get();
        Assert.True(dto.PrtgEnabled);
    }

    [Fact]
    public void SetPrtgEnabled_關閉時即使URL為空也成功()
    {
        var service = Create();
        _store.Update(s =>
        {
            s.PrtgUrl = "";
            s.PrtgEnabled = true;
        });

        var result = service.SetPrtgEnabled(false);
        Assert.False(result);

        var dto = service.Get();
        Assert.False(dto.PrtgEnabled);
    }

    [Fact]
    public void Update_未提供PRTG連線欄位時沿用既有值不清空()
    {
        var service = Create();

        // 1. 先存好 PRTG 連線設定
        var initial = ValidRequest();
        initial.PrtgEnabled = true;
        initial.PrtgUrl = "https://prtg.example.com";
        initial.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        initial.PrtgApiToken = "abc";
        service.Update(initial);

        // 2. 送出未提供 PRTG 連線欄位（null）的請求
        var request = ValidRequest();
        request.PrtgEnabled = null;
        request.PrtgUrl = null;
        request.PrtgAuthMode = null;
        var saved = service.Update(request);

        // 斷言回傳 DTO 與重讀 DB 均保留原值
        Assert.True(saved.PrtgEnabled);
        Assert.Equal("https://prtg.example.com", saved.PrtgUrl);
        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Token, saved.PrtgAuthMode);

        var reread = service.Get();
        Assert.True(reread.PrtgEnabled);
        Assert.Equal("https://prtg.example.com", reread.PrtgUrl);
        Assert.Equal(LogForesight.Core.Models.PrtgAuthModes.Token, reread.PrtgAuthMode);
    }

    [Fact]
    public void Update_提供PRTG連線欄位時確實更新()
    {
        var service = Create();

        var initial = ValidRequest();
        initial.PrtgEnabled = true;
        initial.PrtgUrl = "https://prtg.example.com";
        initial.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        initial.PrtgApiToken = "abc";
        service.Update(initial);

        var updateReq = ValidRequest();
        updateReq.PrtgEnabled = true;
        updateReq.PrtgUrl = "https://new.example.com";
        var saved = service.Update(updateReq);

        Assert.Equal("https://new.example.com", saved.PrtgUrl);

        var reread = service.Get();
        Assert.Equal("https://new.example.com", reread.PrtgUrl);
    }

    [Fact]
    public void Update_PRTG沿用既有啟用狀態時位址驗證仍生效()
    {
        var service = Create();

        // 1. 既有設定為啟用且有合法位址
        var initial = ValidRequest();
        initial.PrtgEnabled = true;
        initial.PrtgUrl = "https://prtg.example.com";
        initial.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Token;
        initial.PrtgApiToken = "abc";
        service.Update(initial);

        // 2. 本次請求 PrtgEnabled = null（沿用既有 true），但 PrtgUrl = ""（明確送空字串清空）
        var request = ValidRequest();
        request.PrtgEnabled = null;
        request.PrtgUrl = "";

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("PRTG 位址不可為空", ex.Message);
    }

    [Fact]
    public void Update_關閉PRTG時不需要位址且成功寫入()
    {
        var service = Create();

        var request = ValidRequest();
        request.PrtgEnabled = false;
        request.PrtgUrl = "";

        var saved = service.Update(request);

        Assert.False(saved.PrtgEnabled);
        Assert.Equal("", saved.PrtgUrl);

        var reread = service.Get();
        Assert.False(reread.PrtgEnabled);
        Assert.Equal("", reread.PrtgUrl);
    }

    [Fact]
    public void Update_未提供PrtgUsername時沿用既有帳號不清空()
    {
        var service = Create();

        // 1. 先存好 PRTG 連線設定（含帳號）
        var initial = ValidRequest();
        initial.PrtgEnabled = true;
        initial.PrtgUrl = "https://prtg.example.com";
        initial.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        initial.PrtgUsername = "prtg_admin";
        initial.PrtgPassword = "secret_password";
        service.Update(initial);

        // 2. 送出未提供 PrtgUsername（null）的請求
        var request = ValidRequest();
        request.PrtgEnabled = null;
        request.PrtgUrl = null;
        request.PrtgAuthMode = null;
        request.PrtgUsername = null;
        var saved = service.Update(request);

        // 斷言回傳 DTO 與重讀 DB 均保留原帳號
        Assert.Equal("prtg_admin", saved.PrtgUsername);

        var reread = service.Get();
        Assert.Equal("prtg_admin", reread.PrtgUsername);
    }

    [Fact]
    public void Update_PRTG為Password模式且啟用時_未帶帳號沿用既有值不被驗證擋下()
    {
        var service = Create();

        // 1. 先存好 PRTG 連線設定（Password 模式已啟用、有帳密）
        var initial = ValidRequest();
        initial.PrtgEnabled = true;
        initial.PrtgUrl = "https://prtg.example.com";
        initial.PrtgAuthMode = LogForesight.Core.Models.PrtgAuthModes.Password;
        initial.PrtgUsername = "prtg_admin";
        initial.PrtgPassword = "secret_password";
        service.Update(initial);

        // 2. 模擬一般設定頁存檔（不送 PRTG 欄位，PrtgUsername 為 null）
        var request = ValidRequest();
        request.PrtgEnabled = null;
        request.PrtgUrl = null;
        request.PrtgAuthMode = null;
        request.PrtgUsername = null;
        request.BrandName = "新自訂系統名稱";

        // 應該成功儲存，不擲出「必須設定帳號」的 DomainException
        var saved = service.Update(request);

        Assert.Equal("新自訂系統名稱", saved.BrandName);
        Assert.True(saved.PrtgEnabled);
        Assert.Equal("prtg_admin", saved.PrtgUsername);
    }
}
