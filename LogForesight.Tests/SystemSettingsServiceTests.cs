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
        new(_store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService());

    private static UpdateSystemSettingsRequest ValidRequest(int runLogRetentionDays = 90, int auditRetentionDays = 730) => new()
    {
        UnhandledSeverities = new List<string> { "High" },
        SeverityDisplayMode = "DefaultHidden",
        AiBaseUrl = "",
        InitialHistoryDays = 120,
        RetentionDays = 120,
        RunLogRetentionDays = runLogRetentionDays,
        AuditRetentionDays = auditRetentionDays
    };

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

    [Fact]
    public void 預設值符合SystemSettings的內建預設()
    {
        var settings = new SystemSettings();

        Assert.Equal(90, settings.RunLogRetentionDays);
        Assert.Equal(730, settings.AuditRetentionDays);
    }

    /// <summary>
    /// docs/SHARED-STANDARDS-PLAN.md S10：ValidSeverities 是手寫陣列（承載畫面勾選順序，
    /// 由重到輕，無法直接用 Enum.GetNames 因為那是宣告順序），IssueSeverity 加值時編譯器
    /// 不會提醒這裡要跟著加——用測試斷言集合一致，enum 漏改時這裡紅燈。
    /// docs/WEB-FEEDBACK-2-PLAN.md #1（B1 三級化）：IssueSeverity 列舉刻意保留 Critical
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

    // ── docs/WEB-FEEDBACK-PLAN.md #5：三模式簡化為兩個（DefaultHidden／SiteHidden）──────

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

        // docs/WEB-FEEDBACK-2-PLAN.md #1（B1 三級化）：舊設定殘留的 "Critical" 正規化為 "High"，
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

    // ── docs/WEB-FEEDBACK-PLAN.md #9：AD 驗證設定 ──────────────────────────────

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
