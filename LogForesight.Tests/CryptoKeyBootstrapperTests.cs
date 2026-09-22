using System.Text.Json;
using LogForesight.Web.Services;
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
        var legacy = LegacyV1("secret");
        _settings.Update(s => s.PrtgApiTokenEnc = legacy);
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://s1", Username = "u", PasswordEnc = legacy });

        CryptoHelper.ResetForTests();
        Run();

        Assert.True(CryptoKeyBootstrapper.KeyMismatch);
        Assert.Equal(legacy, _settings.Get().PrtgApiTokenEnc);
        Assert.Equal(legacy, _sentinels.FindByName("S1")!.PasswordEnc);
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
}
