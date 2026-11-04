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

    internal const string MutexNameForTests = MutexName;

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
        => RunWithTimeout(backend, dataRoot, settingsStore, sentinelStore, MutexTimeout);

    internal static void RunForTests(
        StorageBackend backend,
        string dataRoot,
        ISystemSettingsStore settingsStore,
        ISentinelStore sentinelStore,
        TimeSpan mutexTimeout)
        => RunWithTimeout(backend, dataRoot, settingsStore, sentinelStore, mutexTimeout);

    private static void RunWithTimeout(
        StorageBackend backend,
        string dataRoot,
        ISystemSettingsStore settingsStore,
        ISentinelStore sentinelStore,
        TimeSpan mutexTimeout)
    {
        var path = KeyFilePath(dataRoot);

        // 同步版互斥：整段同步、無 await，取得與釋放在同一條執行緒（見 NamedMutexGate.RunExclusive）。
        // 金鑰檔產生、指紋 claim 與密文重加密都必須在鎖內；逾時不可讓任何一項寫入繼續。
        var acquired = new NamedMutexGate(MutexName).RunExclusive(
            () => RunCore(backend, path, settingsStore, sentinelStore), mutexTimeout,
            runActionWhenNotAcquired: false);
        if (!acquired)
            throw new InvalidOperationException(
                $"無法在 {mutexTimeout.TotalSeconds:0.###} 秒內取得金鑰準備互斥；未執行任何金鑰或資料庫寫入，啟動已停止。" +
                "請確認另一個 LogForesight 行程的啟動工作已完成後再重試。");

        Log.Info("密文金鑰來源：{0}；金鑰檔：{1}", CryptoHelper.KeySource, path);

        // 金鑰不符時 RunCore 提早返回，探測放在這裡才一定會跑到
        ProbeSecrets(settingsStore, sentinelStore);
    }

    /// <summary>
    /// 啟動探測：對 <see cref="SecretInventory"/> 每一筆非空密文試解一次。<see cref="CryptoHelper.TryDecrypt"/>
    /// 失敗時會設 <see cref="CryptoHelper.DecryptFailureSeen"/>，健康頁因此在第一個夜間取數之前就顯示降級，
    /// 而不是等到某個功能剛好用到那組憑證。**不改任何資料**；探測失敗（讀不到設定等）只記 Warn，不得影響啟動。
    /// </summary>
    /// <returns>解不開的項目顯示名稱（不含任何值）</returns>
    internal static IReadOnlyList<string> ProbeSecrets(ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        try
        {
            var bad = new List<string>();
            foreach (var entry in SecretInventory.Build(settingsStore.Get(), sentinelStore.GetAll()))
            {
                if (string.IsNullOrEmpty(entry.Ciphertext)) continue;
                if (!CryptoHelper.TryDecrypt(entry.Ciphertext, out _)) bad.Add(entry.DisplayName);
            }

            if (bad.Count > 0)
                Log.Warn("[Crypto] 啟動探測：下列憑證用目前金鑰解不開：{0}。請還原正確的金鑰檔，或到對應設定頁重新輸入。", string.Join("、", bad));
            return bad;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Crypto] 啟動探測失敗（不影響啟動）：{0}", ex.Message);
            return Array.Empty<string>();
        }
    }

    private static void RunCore(StorageBackend backend, string path, ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        var envSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LF_CRYPTO_KEY"));
        if (!envSet && !File.Exists(path))
            TryCreateKeyFile(path);

        CryptoHelper.UseKeyFile(path);

        var fingerprint = CryptoHelper.KeyFingerprint;
        var blob = backend.Blob(FingerprintBlobKey);
        var fingerprintCheck = ClaimOrValidateFingerprint(blob, fingerprint);
        var recorded = fingerprintCheck.Recorded;
        if (recorded != null)
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

    /// <summary>
    /// 在同一個 blob 原子操作內完成首次 claim 或既有指紋比較。
    /// 回傳非 null 代表已有不同指紋；空 blob 才能寫入 claim。非空但損毀的內容直接失敗，
    /// 不把它當成首次啟動覆寫。
    /// </summary>
    private static (string? Recorded, bool Claimed) ClaimOrValidateFingerprint(
        EfJsonBlobStore blob,
        string fingerprint)
    {
        try
        {
            var claimed = blob.Mutate<bool>(current =>
            {
                if (current is null || current.Length == 0)
                {
                    var content = JsonSerializer.Serialize(new FingerprintRecord
                    {
                        Fingerprint = fingerprint,
                        CreatedAt = DateTime.Now
                    });
                    return (content, true);
                }

                var recorded = ReadFingerprint(current);
                if (recorded == null)
                    throw new InvalidOperationException(
                        "資料庫中的金鑰指紋已損毀；啟動已停止，未覆寫原指紋。請還原正確資料庫備份或重新建立資料庫。");

                if (!string.Equals(recorded, fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new FingerprintMismatchException(recorded);

                return (current, false);
            });

            return (null, claimed);
        }
        catch (FingerprintMismatchException ex)
        {
            return (ex.Recorded, false);
        }
    }

    /// <summary>供定向競態測試呼叫同一個原子 claim 實作，不成為正式啟動 API。</summary>
    internal static bool ClaimFingerprintForTests(EfJsonBlobStore blob, string fingerprint) =>
        ClaimOrValidateFingerprint(blob, fingerprint).Claimed;

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

    /// <summary>
    /// Windows 上把金鑰檔 ACL 限縮為「目前執行身分＋Administrators 完全控制、移除繼承」；
    /// 非 Windows（macOS／Linux）改設檔案模式 600（僅擁有者可讀寫）。失敗只 Warn。
    /// </summary>
    private static void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[Crypto] 無法將金鑰檔 {0} 的檔案模式設為 600（不影響運作，建議手動 chmod 600）。", path);
            }
            return;
        }

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
            // 呼叫端會把非空的損毀內容視為錯誤，不得當成首次啟動覆寫
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

    private sealed class FingerprintMismatchException : Exception
    {
        public FingerprintMismatchException(string recorded) : base("金鑰指紋不符") => Recorded = recorded;

        public string Recorded { get; }
    }
}
