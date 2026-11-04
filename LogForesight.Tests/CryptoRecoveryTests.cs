using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪批次H-2：運行中重新輸入憑證後，儲存入口成功即重新評估解密失敗旗標
/// （<see cref="CryptoRecovery"/>）。旗標是行程層級 static，放進 CryptoKeyState 集合。
/// </summary>
[Collection("CryptoKeyState")]
public class CryptoRecoveryTests : IDisposable
{
    private const string Undecryptable = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly FakeSystemSettingsStore _store = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly EfSqliteFixture _fx = new();

    public CryptoRecoveryTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private SystemSettingsService CreateSettings() =>
        new(_store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(_store, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_fx.Blob("mail_notify_state")),
                new ScheduleFreshnessService(new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")), new ScheduleOptionsStore(_fx.Blob("schedule_options"))),
                new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)),
                new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore()),
            new FakeReportUsageQuery(), _sentinels);

    private SentinelAdminService CreateSentinelAdmin() =>
        new(_sentinels, new FakeHostStore(), new RecordingAuditService(), TestPermissionStamps.Shared, _store);

    /// <summary>製造「曾有密文解不開」的現場：旗標為真</summary>
    private static void RaiseFailureFlag()
    {
        Assert.False(CryptoHelper.TryDecrypt(Undecryptable, out _));
        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    // ── helper 本身 ──

    [Fact]
    public void Reevaluate_全部解得開_清旗標()
    {
        RaiseFailureFlag();
        _store.Update(s => s.AiApiKeyEnc = CryptoHelper.Encrypt("k"));
        _sentinels.Upsert(new Sentinel { Name = "S1", PasswordEnc = CryptoHelper.Encrypt("p"), Active = false });

        CryptoRecovery.Reevaluate(_store, _sentinels);

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Reevaluate_只剩PRTG其他模式的殘留解不開_清旗標()
    {
        RaiseFailureFlag();
        _store.Update(s =>
        {
            s.PrtgAuthMode = PrtgAuthModes.Passhash;
            s.PrtgPasshashEnc = CryptoHelper.Encrypt("h");
            s.PrtgPasswordEnc = Undecryptable;
        });

        CryptoRecovery.Reevaluate(_store, _sentinels);

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void 診斷匯出試解殘留_不舉旗標也還是列出解不開()
    {
        var secrets = new List<string>();
        var undecryptable = new List<string>();

        RunExportDeidentifierFactory.Secret(secrets, undecryptable, "PRTG 密碼", Undecryptable, retired: true);

        Assert.False(CryptoHelper.DecryptFailureSeen);
        Assert.Equal(new[] { "PRTG 密碼" }, undecryptable);

        RunExportDeidentifierFactory.Secret(secrets, undecryptable, "PRTG passhash", Undecryptable);
        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Reevaluate_仍有一筆停用Sentinel解不開_旗標維持()
    {
        RaiseFailureFlag();
        _sentinels.Upsert(new Sentinel { Name = "S-OFF", PasswordEnc = Undecryptable, Active = false });

        CryptoRecovery.Reevaluate(_store, _sentinels);

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Reevaluate_讀設定拋例外_不外拋且旗標不動()
    {
        RaiseFailureFlag();
        _store.ThrowOnGet = new InvalidOperationException("boom");

        CryptoRecovery.Reevaluate(_store, _sentinels);

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Reevaluate_缺其中一個store_不動作()
    {
        RaiseFailureFlag();

        CryptoRecovery.Reevaluate(_store, null);
        CryptoRecovery.Reevaluate(null, _sentinels);

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    // ── 儲存入口 ──

    [Fact]
    public void Update_重新輸入AI金鑰且其餘都解得開_旗標被清()
    {
        _store.Update(s => s.AiApiKeyEnc = Undecryptable);
        RaiseFailureFlag();
        var service = CreateSettings();
        var request = SystemSettingsServiceTests.ValidRequest();
        request.ExpectedRevision = _store.Get().Revision;
        request.AiApiKey = "new-key";

        service.Update(request);

        Assert.False(CryptoHelper.DecryptFailureSeen);
        Assert.True(CryptoHelper.TryDecrypt(_store.Get().AiApiKeyEnc, out var plain));
        Assert.Equal("new-key", plain);
    }

    [Fact]
    public void Update_還有別的憑證解不開_旗標維持()
    {
        _store.Update(s => s.SmtpPasswordEnc = Undecryptable);
        RaiseFailureFlag();
        var request = SystemSettingsServiceTests.ValidRequest();
        request.ExpectedRevision = _store.Get().Revision;
        request.AiApiKey = "new-key";

        CreateSettings().Update(request);

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Update_版本衝突儲存失敗_不清旗標()
    {
        _store.Update(s => s.AiApiKeyEnc = "");
        _sentinels.Upsert(new Sentinel { Name = "S1", PasswordEnc = "", Active = true });
        RaiseFailureFlag();   // 旗標為真，但目前所有密文其實都解得開——若誤呼叫會被清掉
        var request = SystemSettingsServiceTests.ValidRequest();
        request.ExpectedRevision = "stale-revision";

        Assert.ThrowsAny<Exception>(() => CreateSettings().Update(request));

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void UpdatePrtg_重新輸入token_旗標被清()
    {
        _store.Update(s => s.PrtgApiTokenEnc = Undecryptable);
        RaiseFailureFlag();

        CreateSettings().UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = _store.Get().Revision,
            PrtgApiToken = "new-token"
        });

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void UpdatePrtg_驗證失敗_不清旗標()
    {
        RaiseFailureFlag();   // 目前所有密文都解得開，誤呼叫會清掉

        Assert.ThrowsAny<Exception>(() => CreateSettings().UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = _store.Get().Revision,
            PrtgEnabled = true   // 啟用但沒有 URL → 驗證例外
        }));

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void SaveSentinel_重新輸入密碼_旗標被清()
    {
        var created = CreateSentinelAdmin().SaveSentinel(new SaveSentinelRequest { Name = "S1", Password = "old" });
        _sentinels.Upsert(new Sentinel
        {
            SentinelId = created.SentinelId, Name = "S1", PasswordEnc = Undecryptable, Active = true
        });
        RaiseFailureFlag();

        CreateSentinelAdmin().SaveSentinel(new SaveSentinelRequest
        {
            SentinelId = created.SentinelId, Name = "S1", Password = "new"
        });

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void SaveSentinel_驗證失敗_不清旗標()
    {
        RaiseFailureFlag();

        Assert.Throws<DomainException>(() => CreateSentinelAdmin().SaveSentinel(new SaveSentinelRequest { Name = "  " }));

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void DeleteSentinel_刪掉唯一解不開的密文_旗標被清()
    {
        var bad = _sentinels.Upsert(new Sentinel { Name = "S-BAD", PasswordEnc = Undecryptable, Active = true });
        RaiseFailureFlag();

        CreateSentinelAdmin().DeleteSentinel(bad.SentinelId);

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void DeleteSentinel_另有解不開的_旗標維持()
    {
        var bad = _sentinels.Upsert(new Sentinel { Name = "S-BAD", PasswordEnc = Undecryptable, Active = true });
        _sentinels.Upsert(new Sentinel { Name = "S-BAD2", PasswordEnc = Undecryptable, Active = false });
        RaiseFailureFlag();

        CreateSentinelAdmin().DeleteSentinel(bad.SentinelId);

        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    // ── 健康頁文案 ──

    [Fact]
    public void 健康頁文案_區分金鑰不符與部分憑證解不開()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "settings.js"));
        Assert.Contains("金鑰與資料庫不相符：重新輸入全部憑證後重新啟動站台即解除", js);
        Assert.Contains("部分憑證無法解密，請重新輸入", js);
        // 金鑰不符的分支在前，只有解密失敗才落到後者
        Assert.True(js.IndexOf("cryptoKeyMismatch", StringComparison.Ordinal) <
                    js.IndexOf("部分憑證無法解密", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")) &&
               !Directory.Exists(Path.Combine(dir.FullName, "LogForesight.Web", "wwwroot")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到專案根目錄");
    }
}
