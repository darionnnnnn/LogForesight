using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪批次A：憑證狀態單一出口（Sentinel 側）、密文清單與取數前置檢查。
/// 凡是用「解不開的密文」呼叫 <see cref="CryptoHelper.TryDecrypt"/> 的類別都會設全域的解密失敗旗標，
/// 一律放 CryptoKeyState 集合序列化，並在建構／釋放時重設旗標。
/// </summary>
internal static class CredentialTestData
{
    /// <summary>格式合法但任何金鑰都解不開的 v2 密文</summary>
    public const string Undecryptable = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
}

/// <summary>Sentinel 密碼三態：Ok／NotConfigured／Undecryptable 由工廠單一判定。</summary>
[Collection("CryptoKeyState")]
public class SentinelConnectionFactoryTests : IDisposable
{
    public SentinelConnectionFactoryTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static Sentinel Make(string? username, string? passwordEnc) =>
        new() { SentinelId = 7, Name = "S1", BaseUrl = "https://s1", Username = username ?? "", PasswordEnc = passwordEnc ?? "" };

    [Fact]
    public void Ok_密文解得開_帶出明碼()
    {
        var s = Make("svc", CryptoHelper.Encrypt("hunter2"));

        var server = SentinelConnectionFactory.ToConnectable(s);

        Assert.Equal(CredentialState.Ok, server.CredentialState);
        Assert.Equal("hunter2", server.Password);
        Assert.True(server.CanDiscover);
        Assert.Equal(CredentialState.Ok, SentinelConnectionFactory.CredentialStateOf(s));
        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Ok_舊資料的明文密碼原樣可用()
    {
        var server = SentinelConnectionFactory.ToConnectable(Make("svc", "plain-old-pass"));

        Assert.Equal(CredentialState.Ok, server.CredentialState);
        Assert.Equal("plain-old-pass", server.Password);
    }

    [Theory]
    [InlineData("svc", "")]
    [InlineData("svc", "   ")]
    [InlineData("", "plain")]
    [InlineData("  ", "plain")]
    public void NotConfigured_密碼或帳號欄位空白(string username, string passwordEnc)
    {
        var s = Make(username, passwordEnc);

        Assert.Equal(CredentialState.NotConfigured, SentinelConnectionFactory.CredentialStateOf(s));
        Assert.Equal(CredentialState.NotConfigured, SentinelConnectionFactory.ToConnectable(s).CredentialState);
        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void Undecryptable_密文解不開_密碼為空且與未設定分開()
    {
        var s = Make("svc", CredentialTestData.Undecryptable);

        var server = SentinelConnectionFactory.ToConnectable(s);

        Assert.Equal(CredentialState.Undecryptable, server.CredentialState);
        Assert.Equal("", server.Password);
        Assert.False(server.CanDiscover);
        Assert.True(CryptoHelper.DecryptFailureSeen);
        // POCO 屬性只看欄位非空，解不開時仍是 true——這正是行為判定不能再用它的原因
        Assert.True(s.CanDiscover);
    }

    [Fact]
    public void 狀態字串_對外三值()
    {
        Assert.Equal("ok", CredentialState.Ok.ToWire());
        Assert.Equal("not_configured", CredentialState.NotConfigured.ToWire());
        Assert.Equal("undecryptable", CredentialState.Undecryptable.ToWire());
    }
}

/// <summary>密文清單：名稱與順序固定、「這趟用不用得到」的判定。</summary>
public class SecretInventoryTests
{
    private static Sentinel MakeSentinel(string name, bool active = true) =>
        new() { SentinelId = 1, Name = name, Username = "u", PasswordEnc = "enc:v2:x", Active = active };

    private static SystemSettings Settings(Action<SystemSettings>? edit = null)
    {
        var s = new SystemSettings
        {
            AiApiKeyEnc = "ai", SmtpPasswordEnc = "smtp", PrtgApiTokenEnc = "tok", PrtgPasswordEnc = "pw", PrtgPasshashEnc = "ph"
        };
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public void 名稱與順序固定_沿用匯出工廠的中文名()
    {
        var list = SecretInventory.Build(Settings(), new[] { MakeSentinel("A"), MakeSentinel("B") });

        Assert.Equal(
            new[] { "AI 金鑰", "SMTP 密碼", "PRTG API token", "PRTG 密碼", "PRTG passhash", "Sentinel 密碼", "Sentinel 密碼" },
            list.Select(e => e.Name));
        Assert.Equal(new[] { "ai", "smtp", "tok", "pw", "ph", "enc:v2:x", "enc:v2:x" }, list.Select(e => e.Ciphertext));
        Assert.Equal("Sentinel 密碼「A」", list[5].DisplayName);
        Assert.Equal("AI 金鑰", list[0].DisplayName);
    }

    [Fact]
    public void AI未設定_金鑰這趟用不到()
    {
        var unconfigured = SecretInventory.Build(Settings(s => { s.AiProvider = "local"; s.AiBaseUrl = ""; }), Array.Empty<Sentinel>());
        var configured = SecretInventory.Build(Settings(s => { s.AiProvider = "local"; s.AiBaseUrl = "http://ai.local"; }), Array.Empty<Sentinel>());

        Assert.False(unconfigured.Single(e => e.Name == SecretInventory.AiKeyName).NeededThisRun);
        Assert.True(configured.Single(e => e.Name == SecretInventory.AiKeyName).NeededThisRun);
    }

    [Fact]
    public void PRTG未啟用_三組都用不到_已啟用只算目前認證模式()
    {
        var off = SecretInventory.Build(Settings(s => s.PrtgEnabled = false), Array.Empty<Sentinel>());
        Assert.All(off.Where(e => e.Name.StartsWith("PRTG")), e => Assert.False(e.NeededThisRun));

        foreach (var (mode, expected) in new[]
        {
            (PrtgAuthModes.Token, SecretInventory.PrtgTokenName),
            (PrtgAuthModes.Password, SecretInventory.PrtgPasswordName),
            (PrtgAuthModes.Passhash, SecretInventory.PrtgPasshashName)
        })
        {
            var on = SecretInventory.Build(Settings(s => { s.PrtgEnabled = true; s.PrtgAuthMode = mode; }), Array.Empty<Sentinel>());
            Assert.Equal(new[] { expected }, on.Where(e => e.Name.StartsWith("PRTG") && e.NeededThisRun).Select(e => e.Name));
        }
    }

    [Fact]
    public void SMTP密碼永遠不屬於取數趟()
    {
        var list = SecretInventory.Build(Settings(s => s.PrtgEnabled = true), Array.Empty<Sentinel>());
        Assert.False(list.Single(e => e.Name == SecretInventory.SmtpPasswordName).NeededThisRun);
    }

    [Fact]
    public void Sentinel_停用或沒有主機的用不到()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "with-hosts" };
        var list = SecretInventory.Build(Settings(),
            new[] { MakeSentinel("with-hosts"), MakeSentinel("no-hosts"), MakeSentinel("inactive", active: false) }, names);

        var needed = list.Where(e => e.Name == SecretInventory.SentinelPasswordName && e.NeededThisRun).Select(e => e.Subject);
        Assert.Equal(new[] { "with-hosts" }, needed);

        // 不給名稱集合：啟用中的都算
        var all = SecretInventory.Build(Settings(), new[] { MakeSentinel("a"), MakeSentinel("b", active: false) });
        Assert.Equal(new[] { "a" }, all.Where(e => e.Name == SecretInventory.SentinelPasswordName && e.NeededThisRun).Select(e => e.Subject));
    }
}

/// <summary>
/// 取數前憑證前置檢查：單元（CredentialPreflight）與整合（真的 AnalysisOrchestrator＋真的 BatchRunRecorder）。
/// 整合案先預熱解不開的密文：CryptoHelper 對每筆壞密文在整個行程只記一次 Error，正式站台由啟動探測先消耗掉；
/// 不預熱的話第一次解密剛好落在這趟的 recorder 內，那一則 Error 不是前置檢查寫的。
/// </summary>
[Collection("CryptoKeyState")]
public sealed class CredentialPreflightTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-credpre-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _app;
    private readonly StorageBackend _backend;
    private readonly List<string> _console = new();

    public CredentialPreflightTests()
    {
        CryptoHelper.ResetForTests();
        Directory.CreateDirectory(_dir);
        _app = new AppSettings
        {
            Ai = new AiSettings { BaseUrl = "" },
            Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "p.db")}" }
        };
        _backend = new StorageBackend(_app.Storage, _dir, 1);
    }

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private sealed class EmptyCandidateSource : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
    }

    private sealed class ListConsole(List<string> lines) : IRunConsole
    {
        public void WriteLine(string message = "") => lines.Add(message);
    }

    private void SeedSettings(Action<SystemSettings> edit) => new SystemSettingsStore(_backend.Blob("system_settings")).Update(edit);

    private (OrchestratorResult Result, BatchRun Run, List<BatchRunLog> Logs) Run(
        Func<SystemSettings, IEnumerable<Sentinel>, IReadOnlySet<string>?, IReadOnlyList<SecretEntry>>? inventory = null)
    {
        // 模擬啟動探測：先把壞密文的「第一次失敗」消耗掉（見類別說明）
        CryptoHelper.TryDecrypt(CredentialTestData.Undecryptable, out _);

        var orchestrator = new AnalysisOrchestrator(
            new EmptyCandidateSource(), NullAiRequestGate.Instance, secretInventory: inventory);
        var result = orchestrator.RunAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false },
            _app, _dir, new RetentionOptions(), new ListConsole(_console), CancellationToken.None).GetAwaiter().GetResult();

        var store = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"));
        var run = store.GetRun(result.BatchRunId!.Value)!;
        return (result, run, store.GetLogs(run.RunId));
    }

    private static List<BatchRunLog> PreflightLogs(List<BatchRunLog> logs) =>
        logs.Where(l => l.Logger == CredentialPreflight.LoggerName).ToList();

    // ── 整合：真 recorder ──

    [Fact]
    public void AI已設定且金鑰解不開_寫一則Warn_不增加ErrorCount_趟次不因此失敗()
    {
        SeedSettings(s => { s.AiProvider = "local"; s.AiBaseUrl = "http://ai.local"; s.AiApiKeyEnc = CredentialTestData.Undecryptable; });

        var (result, run, logs) = Run();

        Assert.True(result.Success, result.FailureMessage);
        var warn = Assert.Single(PreflightLogs(logs));
        Assert.Equal("Warn", warn.Level);
        Assert.Contains("AI 金鑰", warn.Message);
        Assert.DoesNotContain("AAAAAAAA", warn.Message);   // 只列名稱不列值
        Assert.Equal(0, run.ErrorCount);
        Assert.Equal(1, run.WarnCount);
        Assert.NotEqual(BatchRunStatus.Failed, BatchRunStatus.Compute(run, DateTime.Now, TimeSpan.FromHours(1)));
        Assert.Contains(_console, l => l.Contains("下列憑證無法解密，本趟略過") && l.Contains("AI 金鑰") && l.Contains("請到對應設定頁重新輸入"));
    }

    [Fact]
    public void AI未設定_不檢查AI金鑰_PRTG未啟用_不檢查PRTG()
    {
        SeedSettings(s =>
        {
            s.AiProvider = "local"; s.AiBaseUrl = ""; s.AiApiKeyEnc = CredentialTestData.Undecryptable;
            s.PrtgEnabled = false; s.PrtgApiTokenEnc = CredentialTestData.Undecryptable;
            s.PrtgPasswordEnc = CredentialTestData.Undecryptable; s.PrtgPasshashEnc = CredentialTestData.Undecryptable;
        });

        var (result, run, logs) = Run();

        Assert.True(result.Success, result.FailureMessage);
        Assert.Empty(PreflightLogs(logs));
        Assert.Equal(0, run.WarnCount);
        Assert.Equal(0, run.ErrorCount);
        Assert.DoesNotContain(_console, l => l.Contains("無法解密"));
    }

    [Fact]
    public void PRTG已啟用_只檢查目前認證模式的憑證()
    {
        SeedSettings(s =>
        {
            s.PrtgEnabled = true; s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("good-token");
            s.PrtgPasswordEnc = CredentialTestData.Undecryptable;   // 殘留的舊模式密文，不是目前用的
        });
        Assert.Empty(PreflightLogs(Run().Logs));
    }

    [Fact]
    public void PRTG已啟用且目前模式的憑證解不開_Warn列出名稱()
    {
        SeedSettings(s =>
        {
            s.PrtgEnabled = true; s.PrtgAuthMode = PrtgAuthModes.Passhash; s.PrtgUsername = "admin";
            s.PrtgPasshashEnc = CredentialTestData.Undecryptable;
        });

        var (_, run, logs) = Run();

        var warn = Assert.Single(PreflightLogs(logs));
        Assert.Contains("PRTG passhash", warn.Message);
        Assert.Equal(0, run.ErrorCount);
    }

    [Fact]
    public void Sentinel啟用且有主機_解不開時列名_沒有主機的不檢查()
    {
        var sentinels = new SentinelStore(_backend.Blob("sentinels"));
        var withHosts = sentinels.Upsert(new Sentinel
        {
            Name = "SENT-A", BaseUrl = "https://a.invalid", Username = "u", PasswordEnc = CredentialTestData.Undecryptable, Active = true
        });
        sentinels.Upsert(new Sentinel
        {
            Name = "SENT-B", BaseUrl = "https://b.invalid", Username = "u", PasswordEnc = CredentialTestData.Undecryptable, Active = true
        });
        new HostStore(_backend.Blob("hosts")).Upsert(new WebHost
        {
            HostName = "HOST-1", IpAddress = "10.0.0.1", Os = WebHost.OsWindows,
            SentinelId = withHosts.SentinelId, NetiqServer = "SENT-A", Source = "netiq", Active = true
        });

        var (_, run, logs) = Run();

        var warn = Assert.Single(PreflightLogs(logs));
        Assert.Contains("SENT-A", warn.Message);
        Assert.DoesNotContain("SENT-B", warn.Message);
        Assert.Equal(0, run.ErrorCount);
        // 管線端：不送請求、計失敗主機日（由 NetiqPipelineBaselineTests 逐項斷言），這裡只確認整趟照常走完並留下輸出
        Assert.Contains(_console, l => l.Contains("Sentinel「SENT-A」密碼無法解密"));
    }

    [Fact]
    public void 清單來源擲例外_分析照常完成_只記Warn()
    {
        SeedSettings(s => { s.AiProvider = "local"; s.AiBaseUrl = "http://ai.local"; });

        var (result, run, logs) = Run((_, _, _) => throw new InvalidOperationException("清單壞了"));

        Assert.True(result.Success, result.FailureMessage);
        var warn = Assert.Single(PreflightLogs(logs));
        Assert.Equal("Warn", warn.Level);
        Assert.Equal(0, run.ErrorCount);
        Assert.NotEqual(BatchRunStatus.Failed, BatchRunStatus.Compute(run, DateTime.Now, TimeSpan.FromHours(1)));
    }

    // ── 單元 ──

    [Fact]
    public void FindUndecryptable_空密文與用不到的項目跳過()
    {
        var list = new[]
        {
            new SecretEntry("AI 金鑰", "", true),
            new SecretEntry("SMTP 密碼", CredentialTestData.Undecryptable, false),
            new SecretEntry("PRTG 密碼", CryptoHelper.Encrypt("ok"), true),
            new SecretEntry("Sentinel 密碼", CredentialTestData.Undecryptable, true, "S9")
        };

        Assert.Equal(new[] { "Sentinel 密碼「S9」" }, CredentialPreflight.FindUndecryptable(list));
    }
}

/// <summary>NetIQ 診斷 probe：密碼解不開就不建 client、不送請求。</summary>
[Collection("CryptoKeyState")]
public class NetiqProbeRunnerCredentialTests : IDisposable
{
    public NetiqProbeRunnerCredentialTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static async Task<(bool Ok, List<string> Lines)> Probe(params Sentinel[] sentinels)
    {
        var lines = new List<string>();
        var ok = await NetiqProbeRunner.RunAsync(sentinels, new NetiqOptions(), null, null, new RecordingRunConsole(lines));
        return (ok, lines);
    }

    [Fact]
    public async Task 密碼解不開_印無法解密_不呼叫client_整體不算成功()
    {
        var (ok, lines) = await Probe(new Sentinel
        {
            Name = "S1", BaseUrl = "http://127.0.0.1:1", Username = "u", PasswordEnc = CredentialTestData.Undecryptable
        });

        Assert.False(ok);
        Assert.Contains(lines, l => l.Contains("S1") && l.Contains("密碼無法解密"));
        // ProbeOneAsync 一開始就印這行標題；沒印＝沒進到建 client 的路徑
        Assert.DoesNotContain(lines, l => l.StartsWith("── Sentinel「", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("帳密未設定"));
    }

    [Fact]
    public async Task 密碼未填_維持帳密未設定的既有訊息()
    {
        var (ok, lines) = await Probe(new Sentinel { Name = "S2", BaseUrl = "http://127.0.0.1:1", Username = "u", PasswordEnc = "" });

        Assert.True(ok);
        Assert.Contains(lines, l => l.Contains("S2") && l.Contains("帳密未設定"));
        Assert.DoesNotContain(lines, l => l.Contains("密碼無法解密"));
    }
}

/// <summary>探索精靈：解不開的密碼要看到「無法解密」，不是「尚未設定」。</summary>
[Collection("CryptoKeyState")]
public class NetiqDiscoveryCredentialTests : IDisposable
{
    public NetiqDiscoveryCredentialTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static (NetiqDiscoveryService Svc, int[] Calls) Create(Sentinel sentinel)
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(sentinel);
        var calls = new int[1];
        var client = new FakeClient((_, _, _, _, _, _) =>
        {
            calls[0]++;
            return Task.FromResult(new NetiqDiscoveryResult());
        });
        var svc = new NetiqDiscoveryService(
            new NetiqServerCatalog(sentinels), client, new FakeHostStore(), new FakeHostGroupStore(), sentinels,
            new FakeImportLogStore(), new FakeCurrentUser(), new RecordingAuditService(), TestPermissionStamps.Shared,
            new NetiqDiscoveryService.ScanRegistry());
        return (svc, calls);
    }

    [Fact]
    public async Task 掃描_密碼解不開_訊息講無法解密且不呼叫client()
    {
        var (svc, calls) = Create(new Sentinel
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc", PasswordEnc = CredentialTestData.Undecryptable
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ScanAsync("S1", "10.1.2", default));

        Assert.Contains("無法解密", ex.Message);
        Assert.DoesNotContain("尚未設定", ex.Message);
        Assert.Equal(0, calls[0]);
    }

    [Fact]
    public void 背景掃描啟動_密碼解不開_同樣講無法解密()
    {
        var (svc, calls) = Create(new Sentinel
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc", PasswordEnc = CredentialTestData.Undecryptable
        });

        var ex = Assert.Throws<DomainException>(() => svc.StartScan("S1", "10.1.2"));

        Assert.Contains("無法解密", ex.Message);
        Assert.Equal(0, calls[0]);
    }

    [Fact]
    public async Task 掃描_密碼未填_維持尚未設定的既有訊息()
    {
        var (svc, _) = Create(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "svc", PasswordEnc = "" });

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ScanAsync("S1", "10.1.2", default));

        Assert.Contains("尚未設定探索帳密", ex.Message);
    }
}

/// <summary>管理頁清單 DTO 的 credentialState 三值。</summary>
[Collection("CryptoKeyState")]
public class SentinelAdminServiceCredentialStateTests : IDisposable
{
    private readonly FakeSentinelStore _sentinels = new();

    public SentinelAdminServiceCredentialStateTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private SentinelAdminService Create() =>
        new(_sentinels, new FakeHostStore(), new RecordingAuditService(), TestPermissionStamps.Shared);

    [Fact]
    public void DTO_credentialState三值()
    {
        _sentinels.Upsert(new Sentinel { Name = "OK", BaseUrl = "https://a", Username = "u", PasswordEnc = CryptoHelper.Encrypt("pw") });
        _sentinels.Upsert(new Sentinel { Name = "EMPTY", BaseUrl = "https://b", Username = "u", PasswordEnc = "" });
        _sentinels.Upsert(new Sentinel { Name = "BAD", BaseUrl = "https://c", Username = "u", PasswordEnc = CredentialTestData.Undecryptable });

        var dtos = Create().GetSentinels().ToDictionary(d => d.Name);

        Assert.Equal("ok", dtos["OK"].CredentialState);
        Assert.Equal("not_configured", dtos["EMPTY"].CredentialState);
        Assert.Equal("undecryptable", dtos["BAD"].CredentialState);
        // 既有欄位語意不變：CanDiscover 仍只看欄位非空，HasPassword 照舊
        Assert.True(dtos["BAD"].CanDiscover);
        Assert.True(dtos["BAD"].HasPassword);
        Assert.False(dtos["EMPTY"].CanDiscover);
    }

    [Fact]
    public void DTO_不洩漏密碼或密文()
    {
        _sentinels.Upsert(new Sentinel { Name = "BAD", BaseUrl = "https://c", Username = "u", PasswordEnc = CredentialTestData.Undecryptable });

        var json = System.Text.Json.JsonSerializer.Serialize(Create().GetSentinels());

        Assert.DoesNotContain("AAAAAAAA", json);
        Assert.DoesNotContain("enc:v2", json);
    }
}

/// <summary>PRTG 憑證三模式 × 三狀態；<c>HasUsableCredentials</c> 與狀態一致（既有 PrtgClientFactoryTests 案維持不變）。</summary>
[Collection("CryptoKeyState")]
public class PrtgClientFactoryCredentialStateTests : IDisposable
{
    public PrtgClientFactoryCredentialStateTests() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        GC.SuppressFinalize(this);
    }

    /// <summary>依模式把「該模式用的密文欄」設成指定值，其他欄位留空</summary>
    private static SystemSettings Make(string mode, string? username, string? secret)
    {
        var s = new SystemSettings { PrtgAuthMode = mode, PrtgUsername = username ?? "" };
        switch (mode)
        {
            case PrtgAuthModes.Password: s.PrtgPasswordEnc = secret ?? ""; break;
            case PrtgAuthModes.Passhash: s.PrtgPasshashEnc = secret ?? ""; break;
            default: s.PrtgApiTokenEnc = secret ?? ""; break;
        }
        return s;
    }

    [Theory]
    [InlineData(PrtgAuthModes.Token)]
    [InlineData(PrtgAuthModes.Password)]
    [InlineData(PrtgAuthModes.Passhash)]
    public void Ok_三模式(string mode)
    {
        var s = Make(mode, "admin", CryptoHelper.Encrypt("good"));

        Assert.Equal(CredentialState.Ok, PrtgClientFactory.CredentialState(s));
        Assert.True(PrtgClientFactory.HasUsableCredentials(s));
        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Theory]
    [InlineData(PrtgAuthModes.Token, "admin", "")]
    [InlineData(PrtgAuthModes.Token, "", "  ")]
    [InlineData(PrtgAuthModes.Password, "admin", "")]
    [InlineData(PrtgAuthModes.Password, "", "plain-pw")]   // 缺帳號
    [InlineData(PrtgAuthModes.Passhash, "admin", "  ")]
    [InlineData(PrtgAuthModes.Passhash, " ", "123456")]    // 缺帳號
    public void NotConfigured_三模式(string mode, string username, string secret)
    {
        var s = Make(mode, username, secret);

        Assert.Equal(CredentialState.NotConfigured, PrtgClientFactory.CredentialState(s));
        Assert.False(PrtgClientFactory.HasUsableCredentials(s));
        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Theory]
    [InlineData(PrtgAuthModes.Token)]
    [InlineData(PrtgAuthModes.Password)]
    [InlineData(PrtgAuthModes.Passhash)]
    public void Undecryptable_三模式_與未設定分開_HasUsable為false(string mode)
    {
        var s = Make(mode, "admin", CredentialTestData.Undecryptable);

        Assert.Equal(CredentialState.Undecryptable, PrtgClientFactory.CredentialState(s));
        Assert.False(PrtgClientFactory.HasUsableCredentials(s));
        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void 只看目前認證模式_其他模式殘留的壞密文不影響()
    {
        var s = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("good"),
            PrtgPasswordEnc = CredentialTestData.Undecryptable,
            PrtgPasshashEnc = CredentialTestData.Undecryptable
        };

        Assert.Equal(CredentialState.Ok, PrtgClientFactory.CredentialState(s));
    }

    [Fact]
    public void 未知模式字串_視同token模式_null設定視為未設定()
    {
        var s = new SystemSettings { PrtgAuthMode = "weird", PrtgApiTokenEnc = CredentialTestData.Undecryptable };

        Assert.Equal(CredentialState.Undecryptable, PrtgClientFactory.CredentialState(s));
        Assert.Equal(CredentialState.NotConfigured, PrtgClientFactory.CredentialState(null!));
    }
}
