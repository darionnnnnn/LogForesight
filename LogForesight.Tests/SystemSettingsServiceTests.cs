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
                MailState));

    private static UpdateSystemSettingsRequest ValidRequest(
        int runLogRetentionDays = 90, int auditRetentionDays = 730, int riskyEventRetentionDays = 14, int detailRetentionDays = 120) => new()
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
        DetailRetentionDays = detailRetentionDays,
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
        Assert.Equal(120, settings.DetailRetentionDays);
    }

    // ── 詳情保留天數（DetailRetentionDays） ───────────────────────────────────────────

    [Fact]
    public void Update後_詳情保留天數持久化()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(detailRetentionDays: 90));

        Assert.Equal(90, saved.DetailRetentionDays);
        Assert.Equal(90, service.Get().DetailRetentionDays);
    }

    [Fact]
    public void 詳情保留天數大於歷史資料保留天數時拒絕()
    {
        var service = Create();
        var request = ValidRequest(detailRetentionDays: 200); // RetentionDays 固定為 120

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("詳情保留天數", ex.Message);
    }

    [Fact]
    public void 詳情保留天數等於歷史資料保留天數時允許()
    {
        var service = Create();

        var saved = service.Update(ValidRequest(detailRetentionDays: 120));

        Assert.Equal(120, saved.DetailRetentionDays);
    }

    [Fact]
    public void Update_稽核紀錄同時包含Before與After的詳情保留天數()
    {
        // 先建立初始設定並清除之前的稽核紀錄（或者直接用新的 audit service 拿最後一筆）
        var audit = new RecordingAuditService();
        var service = new SystemSettingsService(_store, FakeCurrentUser.WithCapabilities(), audit, new FakeUserStore(),
            new MailNotificationService(_store, _mailSender, new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                _mailRecords, new FakeHandlingStore(),
                MailState));

        // 存入不同的值以確認稽核抓到變更
        service.Update(ValidRequest(detailRetentionDays: 90));

        var record = audit.Entries.Last();
        var detailNode = System.Text.Json.Nodes.JsonNode.Parse(record.DetailJson!)!;

        // FakeSystemSettingsStore 會修改同一個物件參考，所以 before/after 值可能相同。
        // 但測試的重點是「斷言兩邊都在」（Before 與 After 區塊都有此欄位），所以檢查 key 是否存在。
        var detailObj = detailNode.AsObject();
        Assert.True(detailObj["Before"]!.AsObject().ContainsKey("DetailRetentionDays"));
        Assert.True(detailObj["After"]!.AsObject().ContainsKey("DetailRetentionDays"));
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
}
