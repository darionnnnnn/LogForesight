using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 啟動時的密文金鑰準備：產生站台專屬金鑰檔（DataRoot\keys\lf-crypto.key）、比對 DB 記錄的金鑰指紋、
/// 把 v1 舊密文重寫成 v2。必須在任何可能解密的服務啟動前執行。
///
/// 金鑰檔與 DB 分開存放是這道防線的全部意義：只拿到 DB 備份解不開密碼欄位。
/// 反過來說，還原 DB 時沒一併還原金鑰檔，所有密碼欄位就解不開——指紋比對就是為了讓這件事
/// 在啟動 log 與健康頁當場看得到，而不是各功能靜默變成「未設定」。
/// </summary>
public static class CryptoKeyBootstrapper
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const string FingerprintBlobKey = "crypto_key_fingerprint";
    private const string MutexName = "LogForesight-CryptoKeyBootstrap";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(30);

    private static volatile bool _keyMismatch;

    /// <summary>啟動時金鑰指紋與 DB 記錄不符（供健康頁顯示）。</summary>
    public static bool KeyMismatch
    {
        get => _keyMismatch;
        internal set => _keyMismatch = value;
    }

    /// <summary>金鑰檔路徑（DataRoot\keys\lf-crypto.key）</summary>
    public static string KeyFilePath(string dataRoot) => Path.Combine(dataRoot, "keys", "lf-crypto.key");

    public static void Run(StorageBackend backend, string dataRoot, ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        var path = KeyFilePath(dataRoot);

        // 同步版互斥：整段同步、無 await，取得與釋放在同一條執行緒（見 NamedMutexGate.RunExclusive）。
        // 逾時仍會執行——金鑰檔以 CreateNew 建立、重加密只重寫 v1 欄位，兩者都冪等。
        var acquired = new NamedMutexGate(MutexName).RunExclusive(
            () => RunCore(backend, path, settingsStore, sentinelStore), MutexTimeout);
        if (!acquired)
            Log.Warn("[Crypto] 等待金鑰準備互斥逾時（{0} 秒），仍繼續執行。", MutexTimeout.TotalSeconds);

        Log.Info("密文金鑰來源：{0}；金鑰檔：{1}", CryptoHelper.KeySource, path);
    }

    private static void RunCore(StorageBackend backend, string path, ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        var envSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LF_CRYPTO_KEY"));
        if (!envSet && !File.Exists(path))
            TryCreateKeyFile(path);

        CryptoHelper.UseKeyFile(path);

        var fingerprint = CryptoHelper.KeyFingerprint;
        var blob = backend.Blob(FingerprintBlobKey);
        var recorded = ReadFingerprint(blob.Read());
        if (recorded == null)
        {
            var content = JsonSerializer.Serialize(new FingerprintRecord { Fingerprint = fingerprint, CreatedAt = DateTime.Now });
            blob.Mutate<bool>(_ => (content, true));
        }
        else if (!string.Equals(recorded, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            KeyMismatch = true;
            Log.Error("金鑰檔與資料庫不相符（資料庫記錄的指紋 {0}，目前金鑰 {1}）：可能是還原資料庫時沒有一併還原金鑰檔，" +
                      "或多個站台共用同一個資料庫。所有密碼欄位將無法解密，請還原正確的金鑰檔或到設定頁重新輸入密碼。",
                recorded, fingerprint);
            return;
        }

        KeyMismatch = false;
        var rewrapped = RewrapSettings(settingsStore) + RewrapSentinels(sentinelStore);
        if (rewrapped > 0)
            Log.Info("[Crypto] 已將 {0} 個舊格式（enc:v1）密碼欄位重新加密為 enc:v2。", rewrapped);
    }

    private static void TryCreateKeyFile(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(dir);
            var key = RandomNumberGenerator.GetBytes(32);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(Convert.ToBase64String(key));
            }
            Log.Info("[Crypto] 已產生站台專屬金鑰檔 {0}（請與資料庫備份分開、一併備份）。", path);
            RestrictAcl(path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // 另一個行程搶先建立：沿用它的檔案（後續 UseKeyFile 會讀它）
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Error(ex, "無法寫入金鑰檔，請確認站台執行帳號對 {0} 有寫入權限。本次沿用原本的金鑰來源運作。", dir);
        }
    }

    /// <summary>Windows 上把金鑰檔 ACL 限縮為「目前執行身分＋Administrators 完全控制、移除繼承」；失敗只 Warn。</summary>
    private static void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var current = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Crypto] 無法限縮金鑰檔 {0} 的存取權限（不影響運作，建議手動設定只允許站台帳號與系統管理員讀取）。", path);
        }
    }

    private static string? ReadFingerprint(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<FingerprintRecord>(content);
            return string.IsNullOrWhiteSpace(record?.Fingerprint) ? null : record.Fingerprint;
        }
        catch (JsonException)
        {
            // 內容損毀視為不存在，重寫目前指紋
            return null;
        }
    }

    private static int RewrapSettings(ISystemSettingsStore store)
    {
        // 沒有舊密文就不寫：Update 會蓋 UpdatedAt，而 UpdatedAt 從 null 變非 null 會改變
        // RuntimeSettingsResolver 的「是否存過設定」判斷
        var current = store.Get();
        if (!new[] { current.AiApiKeyEnc, current.SmtpPasswordEnc, current.PrtgPasshashEnc, current.PrtgPasswordEnc, current.PrtgApiTokenEnc }
                .Any(CryptoHelper.NeedsRewrap))
            return 0;

        var count = 0;
        store.Update(s =>
        {
            s.AiApiKeyEnc = Rewrap(s.AiApiKeyEnc, "AiApiKeyEnc", ref count);
            s.SmtpPasswordEnc = Rewrap(s.SmtpPasswordEnc, "SmtpPasswordEnc", ref count);
            s.PrtgPasshashEnc = Rewrap(s.PrtgPasshashEnc, "PrtgPasshashEnc", ref count);
            s.PrtgPasswordEnc = Rewrap(s.PrtgPasswordEnc, "PrtgPasswordEnc", ref count);
            s.PrtgApiTokenEnc = Rewrap(s.PrtgApiTokenEnc, "PrtgApiTokenEnc", ref count);
        });
        return count;
    }

    private static int RewrapSentinels(ISentinelStore store)
    {
        var count = 0;
        foreach (var sentinel in store.GetAll())
        {
            var before = count;
            var rewrapped = Rewrap(sentinel.PasswordEnc, $"Sentinel[{sentinel.Name}].PasswordEnc", ref count);
            if (count == before) continue;
            sentinel.PasswordEnc = rewrapped;
            store.Upsert(sentinel);
        }
        return count;
    }

    private static string Rewrap(string value, string field, ref int count)
    {
        if (!CryptoHelper.NeedsRewrap(value)) return value;
        if (!CryptoHelper.TryDecrypt(value, out var plain))
        {
            Log.Warn("[Crypto] {0} 的舊格式密文無法解密，保持原值。", field);
            return value;
        }
        count++;
        return CryptoHelper.Encrypt(plain);
    }

    private sealed class FingerprintRecord
    {
        public string Fingerprint { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
