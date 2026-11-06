using System.Text.Json;
using LogForesight.Web.Services;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="CryptoHelper"/> 的金鑰快取、金鑰檔路徑、解密失敗旗標與
/// <see cref="CryptoKeyBootstrapper.KeyMismatch"/> 都是行程層級的 static 狀態。
/// 會改動它們的測試類別都標 <c>[Collection("CryptoKeyState")]</c>；並關閉與其他集合的並行——
/// 否則切到金鑰檔的那段期間，別的類別用 <c>Encrypt</c> 寫、<c>Decrypt</c> 讀會拿到不同金鑰。
/// </summary>
[CollectionDefinition("CryptoKeyState", DisableParallelization = true)]
public sealed class CryptoKeyStateCollection
{
}

/// <summary>啟動時的金鑰檔產生、指紋比對與 v1→v2 重加密（SQLite 暫存庫＋暫存 DataRoot）。</summary>
[Collection("CryptoKeyState")]
public class CryptoKeyBootstrapperTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;
    private readonly SentinelStore _sentinels;

    public CryptoKeyBootstrapperTests()
    {
        CryptoHelper.ResetForTests();
        CryptoKeyBootstrapper.KeyMismatch = false;

        _dataRoot = Path.Combine(Path.GetTempPath(), "lf-cryptokey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dataRoot, "c.db")}" }, _dataRoot);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _sentinels = new SentinelStore(_backend.Blob("sentinels"));
    }

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        CryptoKeyBootstrapper.KeyMismatch = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        catch (UnauthorizedAccessException) { /* 同上（金鑰檔 ACL 限縮後的極端情況） */ }
    }

    private static string LegacyV1(string plain) => CryptoHelper.EncryptV1With(CryptoHelper.EmbeddedKeyForTests, plain);

    private void Run() => CryptoKeyBootstrapper.Run(_backend, _dataRoot, _settings, _sentinels);

    private string KeyPath => CryptoKeyBootstrapper.KeyFilePath(_dataRoot);

    [Fact]
    public void 無金鑰檔_產生金鑰檔並把v1重加密成v2_寫入指紋()
    {
        _settings.Update(s => s.SmtpPasswordEnc = LegacyV1("secret"));

        Run();

        Assert.True(File.Exists(KeyPath));
        Assert.Equal("file", CryptoHelper.KeySource);
        var stored = _settings.Get().SmtpPasswordEnc;
        Assert.StartsWith("enc:v2:", stored);
        Assert.False(CryptoHelper.NeedsRewrap(stored));
        Assert.True(CryptoHelper.TryDecrypt(stored, out var plain));
        Assert.Equal("secret", plain);
        Assert.NotNull(_backend.Blob("crypto_key_fingerprint").Read());
        Assert.Contains(CryptoHelper.KeyFingerprint, _backend.Blob("crypto_key_fingerprint").Read());
        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
    }

    [Fact]
    public void 再執行一次_金鑰檔與欄位值都不變()
    {
        _settings.Update(s => s.AiApiKeyEnc = LegacyV1("secret"));
        Run();
        var keyContent = File.ReadAllText(KeyPath);
        var value = _settings.Get().AiApiKeyEnc;
        var updatedAt = _settings.Get().UpdatedAt;

        // 模擬重啟：快取清掉、重新走一次
        CryptoHelper.ResetForTests();
        Run();

        Assert.Equal(keyContent, File.ReadAllText(KeyPath));
        Assert.Equal(value, _settings.Get().AiApiKeyEnc);
        Assert.Equal(updatedAt, _settings.Get().UpdatedAt);
        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        Assert.True(CryptoHelper.TryDecrypt(_settings.Get().AiApiKeyEnc, out var plain));
        Assert.Equal("secret", plain);
    }

    [Fact]
    public void 指紋不符_KeyMismatch且欄位不被改動()
    {
        Run();   // 先寫入指紋
        _backend.Blob("crypto_key_fingerprint").Mutate<bool>(_ => ("{\"Fingerprint\":\"0000000000000000\"}", true));
        // 有一筆用目前金鑰解不開的密文：不覆寫指紋、不 rewrap，所有欄位維持原樣
        var legacy = LegacyV1("secret");
        _settings.Update(s => s.PrtgApiTokenEnc = Undecryptable);
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = legacy });

        CryptoHelper.ResetForTests();
        Run();

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Equal(Undecryptable, _settings.Get().PrtgApiTokenEnc);
        Assert.Equal(legacy, _sentinels.FindByName("S1")!.PasswordEnc);
        Assert.Contains("0000000000000000", _backend.Blob("crypto_key_fingerprint").Read());
    }

    // ── 第 63-2 輪批次H-2：指紋不符時的自動修復與金鑰檔遺失辨識 ──

    private const string FakeFingerprint = "0000000000000000";

    private string? ReadFingerprintBlob() => _backend.Blob("crypto_key_fingerprint").Read();

    /// <summary>先跑一次建立金鑰檔與指紋（之後 CryptoHelper 仍用這把金鑰），再把 DB 指紋改成別把金鑰的</summary>
    private void PrepareMismatch()
    {
        Run();
        _backend.Blob("crypto_key_fingerprint").Mutate<bool>(_ => ("{\"Fingerprint\":\"" + FakeFingerprint + "\"}", true));
    }

    private static List<string> CaptureLogs(Action action)
    {
        var target = new MemoryTarget("lf-crypto-boot-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target, typeof(CryptoKeyBootstrapper).FullName);
        LogManager.ReconfigExistingLoggers();
        try
        {
            action();
            return target.Logs.ToList();
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }

    [Fact]
    public void 指紋不符_只剩PRTG其他模式的殘留解不開_仍覆寫指紋()
    {
        // 使用者站台的實際情況：改用 passhash 後舊密碼還在資料庫，畫面上沒有欄位能處理它
        PrepareMismatch();
        _settings.Update(s =>
        {
            s.PrtgAuthMode = PrtgAuthModes.Passhash;
            s.PrtgPasshashEnc = CryptoHelper.Encrypt("new-hash");
            s.PrtgPasswordEnc = Undecryptable;
            s.PrtgApiTokenEnc = Undecryptable;
        });
        CryptoHelper.ResetForTests();

        Run();

        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        Assert.False(CryptoHelper.DecryptFailureSeen);
        Assert.DoesNotContain(FakeFingerprint + "\",\"CreatedAt", ReadFingerprintBlob()!);
        Assert.Equal(Undecryptable, _settings.Get().PrtgPasswordEnc);   // 殘留不動，切回該模式時照實回報解不開
    }

    [Fact]
    public void 指紋不符_目前模式的憑證解不開_不覆寫()
    {
        PrepareMismatch();
        _settings.Update(s =>
        {
            s.PrtgAuthMode = PrtgAuthModes.Passhash;
            s.PrtgPasshashEnc = Undecryptable;
            s.PrtgPasswordEnc = CryptoHelper.Encrypt("old");
        });
        CryptoHelper.ResetForTests();

        Run();

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void 指紋不符_全部憑證解得開_覆寫指紋並留下痕跡_密文不變且rewrap有跑()
    {
        PrepareMismatch();
        var v2 = CryptoHelper.Encrypt("ai-key");                    // 用目前金鑰檔加密＝已重新輸入
        var sentinelV2 = CryptoHelper.Encrypt("pw");
        _settings.Update(s =>
        {
            s.AiApiKeyEnc = v2;
            s.SmtpPasswordEnc = LegacyV1("smtp");                  // 之後 rewrap 要跑到
        });
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = sentinelV2, Active = false });
        CryptoHelper.ResetForTests();

        var logs = CaptureLogs(Run);

        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        Assert.False(CryptoHelper.DecryptFailureSeen);
        using var doc = JsonDocument.Parse(ReadFingerprintBlob()!);
        Assert.Equal(CryptoHelper.KeyFingerprint, doc.RootElement.GetProperty("Fingerprint").GetString());
        Assert.Equal(FakeFingerprint, doc.RootElement.GetProperty("ReplacedFingerprint").GetString());
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("UpdatedAt").ValueKind);
        // 密文逐欄不變（v2 不重寫），v1 舊密文被 rewrap 成 v2
        Assert.Equal(v2, _settings.Get().AiApiKeyEnc);
        Assert.Equal(sentinelV2, _sentinels.FindByName("S1")!.PasswordEnc);
        Assert.StartsWith("enc:v2:", _settings.Get().SmtpPasswordEnc);
        Assert.True(CryptoHelper.TryDecrypt(_settings.Get().SmtpPasswordEnc, out var smtp));
        Assert.Equal("smtp", smtp);
        // Warn 帶原指紋、不帶任何值；沒有 Error
        var warn = Assert.Single(logs, l => l.Contains("金鑰指紋已更新"));
        Assert.StartsWith("Warn|", warn);
        Assert.Contains(FakeFingerprint, warn);
        Assert.DoesNotContain(logs, l => l.StartsWith("Error|"));
        Assert.DoesNotContain(logs, l => l.Contains("ai-key") || l.Contains("smtp"));
    }

    [Fact]
    public void 指紋不符_有一筆停用Sentinel解不開_不覆寫且KeyMismatch為真_Warn列名稱()
    {
        PrepareMismatch();
        _settings.Update(s => s.AiApiKeyEnc = CryptoHelper.Encrypt("ai-key"));
        _sentinels.Upsert(new Sentinel { Name = "S-OFF", BaseUrl = "https://s", Username = "u", PasswordEnc = Undecryptable, Active = false });
        var before = ReadFingerprintBlob();
        CryptoHelper.ResetForTests();

        var logs = CaptureLogs(Run);

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Equal(before, ReadFingerprintBlob());
        Assert.Contains(logs, l => l.StartsWith("Warn|") && l.Contains("S-OFF"));
        Assert.Contains(logs, l => l.StartsWith("Error|") && l.Contains(FakeFingerprint));
        Assert.DoesNotContain(logs, l => l.Contains("金鑰指紋已更新"));
    }

    [Fact]
    public void 指紋不符_SMTP密文解不開_不覆寫()
    {
        PrepareMismatch();
        _settings.Update(s => s.SmtpPasswordEnc = Undecryptable);
        var before = ReadFingerprintBlob();
        CryptoHelper.ResetForTests();

        Run();

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Equal(before, ReadFingerprintBlob());
    }

    [Fact]
    public void 指紋不符_inventory為空_覆寫指紋()
    {
        PrepareMismatch();
        CryptoHelper.ResetForTests();

        Run();

        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        using var doc = JsonDocument.Parse(ReadFingerprintBlob()!);
        Assert.Equal(CryptoHelper.KeyFingerprint, doc.RootElement.GetProperty("Fingerprint").GetString());
        Assert.Equal(FakeFingerprint, doc.RootElement.GetProperty("ReplacedFingerprint").GetString());
    }

    [Fact]
    public void 指紋不符_探測沒能完整跑完_不當成全部解得開而覆寫()
    {
        PrepareMismatch();
        var before = ReadFingerprintBlob();
        var throwing = System.Reflection.DispatchProxy.Create<ISystemSettingsStore, RunExportDeidentifierTestsSource.ThrowingProxy<ISystemSettingsStore>>();
        ((RunExportDeidentifierTestsSource.ThrowingProxy<ISystemSettingsStore>)(object)throwing).ThrowOn = "Get";
        CryptoHelper.ResetForTests();

        CryptoKeyBootstrapper.Run(_backend, _dataRoot, throwing, _sentinels);

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Equal(before, ReadFingerprintBlob());
    }

    [Fact]
    public void 覆寫指紋_記錄在比對後被別人改掉_不寫入()
    {
        PrepareMismatch();
        var blob = _backend.Blob("crypto_key_fingerprint");

        // 預期的原指紋與目前記錄不同（另一個行程剛改過）→ 不覆寫
        var ok = CryptoKeyBootstrapper.OverwriteFingerprint(blob, "ffffffffffffffff", CryptoHelper.KeyFingerprint);

        Assert.False(ok);
        Assert.Contains(FakeFingerprint, blob.Read());
    }

    [Fact]
    public void 指紋不符_兩條路徑探測各恰好一次()
    {
        // 不符且有解不開的：探測 Warn 只一行
        PrepareMismatch();
        _settings.Update(s => s.AiApiKeyEnc = Undecryptable);
        CryptoHelper.ResetForTests();
        var mismatchLogs = CaptureLogs(Run);
        Assert.Single(mismatchLogs, l => l.Contains("啟動探測"));

        // 相符且有解不開的：探測 Warn 同樣只一行
        CryptoHelper.ResetForTests();
        _backend.Blob("crypto_key_fingerprint").Mutate<bool>(_ => ("{\"Fingerprint\":\"" + CryptoKeyFingerprintOfFile() + "\"}", true));
        CryptoHelper.ResetForTests();
        var matchLogs = CaptureLogs(Run);
        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Single(matchLogs, l => l.Contains("啟動探測"));
    }

    private string CryptoKeyFingerprintOfFile()
    {
        CryptoHelper.UseKeyFile(KeyPath);
        return CryptoHelper.KeyFingerprint;
    }

    [Fact]
    public void 金鑰檔遺失而新產生_Error訊息寫明金鑰檔遺失與常見原因()
    {
        Run();                                                     // 建立金鑰檔與指紋
        _settings.Update(s => s.AiApiKeyEnc = CryptoHelper.Encrypt("ai-key"));
        File.Delete(KeyPath);                                      // 模擬重新部署清掉 keys
        CryptoHelper.ResetForTests();

        var logs = CaptureLogs(Run);

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        var error = Assert.Single(logs, l => l.StartsWith("Error|"));
        Assert.Contains("金鑰檔遺失", error);
        Assert.Contains("重新部署", error);
        Assert.Contains("DataRoot", error);
    }

    [Fact]
    public void 金鑰檔存在而指紋不符_Error訊息不含金鑰檔遺失()
    {
        PrepareMismatch();
        _settings.Update(s => s.AiApiKeyEnc = Undecryptable);
        CryptoHelper.ResetForTests();

        var logs = CaptureLogs(Run);

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        var error = Assert.Single(logs, l => l.StartsWith("Error|"));
        Assert.DoesNotContain("金鑰檔遺失", error);
    }

    [Fact]
    public void 首次啟動_blob為空_不是金鑰檔遺失()
    {
        var logs = CaptureLogs(Run);

        Assert.False(CryptoKeyBootstrapper.KeyMismatch);
        Assert.DoesNotContain(logs, l => l.Contains("金鑰檔遺失"));
    }

    // ── 第 63-2 輪批次A：啟動探測 ──

    private const string Undecryptable = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void 啟動探測_有解不開的密文_旗標為真且資料不變()
    {
        _settings.Update(s => s.PrtgPasswordEnc = Undecryptable);
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = Undecryptable });
        var updatedAt = _settings.Get().UpdatedAt;
        Assert.False(CryptoHelper.DecryptFailureSeen);

        Run();

        Assert.True(CryptoHelper.DecryptFailureSeen);
        Assert.Equal(Undecryptable, _settings.Get().PrtgPasswordEnc);
        Assert.Equal(Undecryptable, _sentinels.FindByName("S1")!.PasswordEnc);
        Assert.Equal(updatedAt, _settings.Get().UpdatedAt);
    }

    [Fact]
    public void 啟動探測_全部正常_旗標維持為假()
    {
        _settings.Update(s =>
        {
            s.SmtpPasswordEnc = LegacyV1("smtp");
            s.PrtgApiTokenEnc = "plain-old-token";
        });
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = LegacyV1("pw") });

        Run();

        Assert.False(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void 啟動探測_金鑰指紋不符提早返回時仍會探測()
    {
        Run();
        _backend.Blob("crypto_key_fingerprint").Mutate<bool>(_ => ("{\"Fingerprint\":\"0000000000000000\"}", true));
        _settings.Update(s => s.AiApiKeyEnc = Undecryptable);

        CryptoHelper.ResetForTests();
        Run();

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.True(CryptoHelper.DecryptFailureSeen);
    }

    [Fact]
    public void 啟動探測_讀設定失敗只記警告_不擲例外()
    {
        var throwing = System.Reflection.DispatchProxy.Create<ISystemSettingsStore, RunExportDeidentifierTestsSource.ThrowingProxy<ISystemSettingsStore>>();
        ((RunExportDeidentifierTestsSource.ThrowingProxy<ISystemSettingsStore>)(object)throwing).ThrowOn = "Get";

        var bad = CryptoKeyBootstrapper.ProbeSecrets(throwing, _sentinels);

        Assert.Empty(bad);
    }

    [Fact]
    public void Sentinel密碼同樣被重加密()
    {
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = LegacyV1("secret") });
        _sentinels.Upsert(new Sentinel { Name = "S2", BaseUrl = "https://s2", Username = "u", PasswordEnc = "" });

        Run();

        var s1 = _sentinels.FindByName("S1")!.PasswordEnc;
        Assert.StartsWith("enc:v2:", s1);
        Assert.True(CryptoHelper.TryDecrypt(s1, out var plain));
        Assert.Equal("secret", plain);
        Assert.Equal("", _sentinels.FindByName("S2")!.PasswordEnc);
    }

    [Fact]
    public void 固定CBC向量錯誤key解出非UTF8時_PRTG與Sentinel都以正確明碼重包()
    {
        const string legacy = "enc:v1:I3Mnvp+FKKtlA+EfrWepjAB1NRzTwMwVntC7faKySAU=";
        var wrongKey = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("lf-cbc-padding-repro-371"));
        var priorEnv = Environment.GetEnvironmentVariable("LF_CRYPTO_KEY");
        try
        {
            Environment.SetEnvironmentVariable("LF_CRYPTO_KEY", null);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, Convert.ToBase64String(wrongKey));
            _settings.Update(s => s.PrtgApiTokenEnc = legacy);
            _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = legacy });

            Run();

            var token = _settings.Get().PrtgApiTokenEnc;
            var sentinel = _sentinels.FindByName("S1")!.PasswordEnc;
            Assert.StartsWith("enc:v2:", token);
            Assert.StartsWith("enc:v2:", sentinel);
            Assert.Equal("file", CryptoHelper.KeySource);
            Assert.True(CryptoHelper.TryDecrypt(token, out var tokenPlain));
            Assert.True(CryptoHelper.TryDecrypt(sentinel, out var sentinelPlain));
            Assert.Equal("legacy-secret", tokenPlain);
            Assert.Equal("legacy-secret", sentinelPlain);
            Assert.False(CryptoHelper.DecryptFailureSeen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LF_CRYPTO_KEY", priorEnv);
        }
    }

    [Fact]
    public void 歧義CBC向量_保留PRTG與Sentinel原密文並設解密失敗旗標()
    {
        const string ambiguous = "enc:v1:1H3QwjLgWFCy+raRuJKj/tLS64ud2W73ZiH9k4D52XQ=";
        var wrongKey = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("lf-cbc-padding-repro-371"));
        var priorEnv = Environment.GetEnvironmentVariable("LF_CRYPTO_KEY");
        try
        {
            Environment.SetEnvironmentVariable("LF_CRYPTO_KEY", null);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, Convert.ToBase64String(wrongKey));
            _settings.Update(s => s.PrtgApiTokenEnc = ambiguous);
            _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = ambiguous });

            Run();

            Assert.Equal(ambiguous, _settings.Get().PrtgApiTokenEnc);
            Assert.Equal(ambiguous, _sentinels.FindByName("S1")!.PasswordEnc);
            Assert.True(CryptoHelper.DecryptFailureSeen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LF_CRYPTO_KEY", priorEnv);
        }
    }

    [Fact]
    public void 解不開的v1保持原值_其餘照樣重加密()
    {
        // 長度不是 16 的倍數：任何金鑰都必定解不開
        var broken = "enc:v1:" + Convert.ToBase64String(new byte[20]);
        _settings.Update(s =>
        {
            s.PrtgPasswordEnc = broken;
            s.PrtgPasshashEnc = LegacyV1("hash");
        });

        Run();

        Assert.Equal(broken, _settings.Get().PrtgPasswordEnc);
        Assert.True(CryptoHelper.TryDecrypt(_settings.Get().PrtgPasshashEnc, out var plain));
        Assert.Equal("hash", plain);
        Assert.StartsWith("enc:v2:", _settings.Get().PrtgPasshashEnc);
    }

    [Fact]
    public void 金鑰檔已存在_沿用不覆寫()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var existing = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        File.WriteAllText(KeyPath, existing);

        Run();

        Assert.Equal(existing, File.ReadAllText(KeyPath));
        Assert.Equal("file", CryptoHelper.KeySource);
        var cipher = CryptoHelper.Encrypt("x");
        Assert.Equal("x", CryptoHelper.DecryptWith(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), cipher));
    }

    [Fact]
    public async Task 競爭claim_只有一個指紋成功寫入且不被後續覆蓋()
    {
        var fingerprints = Enumerable.Range(0, 20).Select(i => $"fp-{i:00}").ToArray();
        var start = new ManualResetEventSlim(false);

        var tasks = fingerprints.Select(fingerprint => Task.Run(() =>
        {
            start.Wait();
            return CryptoKeyBootstrapper.ClaimFingerprintForTests(
                _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey), fingerprint);
        })).ToArray();

        start.Set();
        var claims = await Task.WhenAll(tasks);

        var stored = _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey).Read();
        Assert.NotNull(stored);
        using var document = JsonDocument.Parse(stored!);
        var claimed = document.RootElement.GetProperty("Fingerprint").GetString();
        Assert.Contains(claimed, fingerprints);
        Assert.Single(claims, value => value);
    }

    [Fact]
    public void 損毀指紋_不當成不存在且不覆寫()
    {
        const string corrupted = " ";
        _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey)
            .Mutate<bool>(_ => (corrupted, true));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CryptoKeyBootstrapper.ClaimFingerprintForTests(
                _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey), "new-fingerprint"));

        Assert.Contains("指紋已損毀", ex.Message);
        Assert.Equal(corrupted, _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey).Read());
    }

    [Fact]
    public void 金鑰檔格式錯誤_擲中文訊息()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllText(KeyPath, Convert.ToBase64String(new byte[16]));

        var ex = Assert.Throws<InvalidOperationException>(Run);
        Assert.Contains("32 bytes", ex.Message);
        Assert.Contains("金鑰檔", ex.Message);
    }

    [Fact]
    public async Task 互斥逾時_不建立金鑰且不寫入資料庫()
    {
        _settings.Update(s => s.SmtpPasswordEnc = LegacyV1("secret"));
        var settingsVersion = _backend.Blob("system_settings").ReadVersion();
        var fingerprintVersion = _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey).ReadVersion();

        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderTask = Task.Run(() =>
        {
            using var held = new Mutex(initiallyOwned: false, CryptoKeyBootstrapper.MutexNameForTests);
            Assert.True(held.WaitOne());
            holderEntered.SetResult();
            releaseHolder.Task.GetAwaiter().GetResult();
            held.ReleaseMutex();
        });

        await holderEntered.Task;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                CryptoKeyBootstrapper.RunForTests(
                    _backend, _dataRoot, _settings, _sentinels, TimeSpan.FromMilliseconds(50)));

            Assert.Contains("未執行任何金鑰或資料庫寫入", ex.Message);
            Assert.False(File.Exists(KeyPath));
            Assert.Equal(settingsVersion, _backend.Blob("system_settings").ReadVersion());
            Assert.Equal(fingerprintVersion, _backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey).ReadVersion());
            Assert.Null(_backend.Blob(CryptoKeyBootstrapper.FingerprintBlobKey).Read());
            Assert.Equal("secret", CryptoHelper.DecryptWith(CryptoHelper.EmbeddedKeyForTests, _settings.Get().SmtpPasswordEnc));
        }
        finally
        {
            releaseHolder.SetResult();
            await holderTask;
        }
    }

    // ── 金鑰檔位置提示（純函式）──────────────────────────────────────────

    [Fact]
    public void 位置提示_env與未知來源為null_embedded提示內嵌金鑰()
    {
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("env", "x", "y", true));
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("other", "x", "y", true));
        Assert.Contains("內嵌金鑰", CryptoKeyBootstrapper.EvaluateKeyLocationWarning("embedded", "x", "y", true));
    }

    [WindowsFact]
    public void 位置提示_Windows路徑_金鑰檔在部署目錄內命中()
    {
        var w = CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", @"C:\site\keys\lf-crypto.key", @"C:\site\", true);
        Assert.Contains("部署目錄", w);
    }

    [WindowsFact]
    public void 位置提示_Windows路徑_相鄰同前綴目錄不命中()
    {
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", @"C:\site-data\keys\lf-crypto.key", @"C:\site", true));
    }

    [WindowsFact]
    public void 位置提示_Windows路徑_大小寫旗標決定是否命中()
    {
        Assert.NotNull(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", @"c:\site\keys\x.key", @"C:\Site\", true));
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", @"c:\site\keys\x.key", @"C:\Site\", false));
    }

    [WindowsFact]
    public void 位置提示_Windows路徑_不同磁碟不命中()
    {
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", @"D:\data\keys\x.key", @"C:\site\", true));
    }

    [Fact]
    public void 位置提示_跨平台_在部署目錄內命中_相鄰同前綴不命中()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-loc-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(root, "site");
        var inside = Path.Combine(site, "keys", "lf-crypto.key");
        var sibling = Path.Combine(root, "site-data", "keys", "lf-crypto.key");

        Assert.Contains("部署目錄", CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", inside, site, false));
        Assert.Contains("部署目錄", CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", inside, site + Path.DirectorySeparatorChar, false));
        Assert.Null(CryptoKeyBootstrapper.EvaluateKeyLocationWarning("file", sibling, site, false));
    }
}
