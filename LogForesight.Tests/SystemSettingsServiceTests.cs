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
        new(_store, FakeCurrentUser.WithCapabilities(), new FakeAuditService());

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
}
