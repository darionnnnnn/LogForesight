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

    private static volatile string? _keyLocationWarning;

    /// <summary>
    /// 啟動時算出的金鑰檔位置風險提示（金鑰檔與部署目錄在一起、或退回內嵌金鑰）；null＝無風險。
    /// 僅供健康頁顯示，不影響整體狀態。
    /// </summary>
    public static string? KeyLocationWarning
    {
        get => _keyLocationWarning;
        internal set => _keyLocationWarning = value;
    }

    /// <summary>
    /// 依金鑰來源與路徑判斷位置風險（純函式）。env 無金鑰檔；embedded 代表建檔失敗；
    /// file 且金鑰檔位於部署目錄之內時，重新部署清掉執行目錄會連金鑰檔一起清掉。
    /// （與靜態屬性 <see cref="KeyLocationWarning"/> 同名會衝突，故函式叫 EvaluateKeyLocationWarning。）
    /// </summary>
    internal static string? EvaluateKeyLocationWarning(string keySource, string keyFilePath, string baseDirectory, bool caseInsensitive)
    {
        switch (keySource)
        {
            case "embedded":
                return "金鑰檔無法建立，目前以內嵌金鑰運作；請檢查資料目錄的寫入權限。";
            case "file":
                var sep = Path.DirectorySeparatorChar;
                var key = Path.GetFullPath(keyFilePath);
                var prefix = Path.GetFullPath(baseDirectory);
                if (!prefix.EndsWith(sep)) prefix += sep;
                var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return key.StartsWith(prefix, comparison)
                    ? "金鑰檔與部署目錄在一起，重新部署會清掉它並讓所有已存憑證失效；請把 DataRoot 設到部署目錄之外，或把 keys 資料夾排除在部署清除範圍（已排除者可忽略）。"
                    : null;
            default:
                return null;
        }
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
        var probed = false;
        var acquired = new NamedMutexGate(MutexName).RunExclusive(
            () => probed = RunCore(backend, path, settingsStore, sentinelStore), mutexTimeout,
            runActionWhenNotAcquired: false);
        if (acquired)
        {
            // 金鑰來源已在 RunCore 內確定；只算一次，供健康頁顯示
            var warning = EvaluateKeyLocationWarning(
                CryptoHelper.KeySource, path, AppContext.BaseDirectory, OperatingSystem.IsWindows());
            KeyLocationWarning = warning;
            if (warning != null) Log.Warn("[Crypto] {0}", warning);
        }

        if (!acquired)
            throw new InvalidOperationException(
                $"無法在 {mutexTimeout.TotalSeconds:0.###} 秒內取得金鑰準備互斥；未執行任何金鑰或資料庫寫入，啟動已停止。" +
                "請確認另一個 LogForesight 行程的啟動工作已完成後再重試。");

        Log.Info("密文金鑰來源：{0}；金鑰檔：{1}", CryptoHelper.KeySource, path);

        // 指紋不符的路徑 RunCore 已在鎖內探測過（探測結果決定能不能覆寫指紋），不重複探測、不重複噴 Warn；
        // 指紋相符的路徑在 rewrap 之後探測一次。兩條路徑都恰好一次。
        if (!probed)
            ProbeSecrets(settingsStore, sentinelStore);
    }

    /// <summary>
    /// 啟動探測：對 <see cref="SecretInventory"/> 每一筆非空密文試解一次。<see cref="CryptoHelper.TryDecrypt"/>
    /// 失敗時會設 <see cref="CryptoHelper.DecryptFailureSeen"/>，健康頁因此在第一個夜間取數之前就顯示降級，
    /// 而不是等到某個功能剛好用到那組憑證。**不改任何資料**；探測失敗（讀不到設定等）只記 Warn，不得影響啟動。
    /// </summary>
    /// <returns>解不開的項目顯示名稱（不含任何值）</returns>
    internal static IReadOnlyList<string> ProbeSecrets(ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
        => ProbeSecretsCore(settingsStore, sentinelStore, out _);

    /// <param name="completed">探測是否完整跑完；讀不到設定等例外時為 false（此時回傳空清單不代表「全部解得開」）</param>
    private static IReadOnlyList<string> ProbeSecretsCore(
        ISystemSettingsStore settingsStore, ISentinelStore sentinelStore, out bool completed)
    {
        completed = false;
        try
        {
            var bad = new List<string>();
            foreach (var entry in SecretInventory.Build(settingsStore.Get(), sentinelStore.GetAll()))
            {
                if (entry.Retired || string.IsNullOrEmpty(entry.Ciphertext)) continue;
                if (!CryptoHelper.TryDecrypt(entry.Ciphertext, out _)) bad.Add(entry.DisplayName);
            }

            if (bad.Count > 0)
                Log.Warn("[Crypto] 啟動探測：下列憑證用目前金鑰解不開：{0}。請還原正確的金鑰檔，或到對應設定頁重新輸入。", string.Join("、", bad));
            completed = true;
            return bad;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Crypto] 啟動探測失敗（不影響啟動）：{0}", ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <returns>是否已在鎖內做過啟動探測（指紋不符路徑為 true；相符路徑由呼叫端在 rewrap 之後探測）</returns>
    private static bool RunCore(StorageBackend backend, string path, ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        var envSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LF_CRYPTO_KEY"));
        var blob = backend.Blob(FingerprintBlobKey);

        // 金鑰檔不存在而指紋 blob 已有內容＝金鑰檔遺失後被新產生（不是首次啟動）。
        // 必須在 TryCreateKeyFile 之前讀；用環境變數金鑰時不適用（本來就不產生金鑰檔）。
        var keyFileLost = false;
        if (!envSet && !File.Exists(path))
        {
            keyFileLost = !string.IsNullOrWhiteSpace(ReadBlobSafely(blob));
            TryCreateKeyFile(path);
        }

        CryptoHelper.UseKeyFile(path);

        var fingerprint = CryptoHelper.KeyFingerprint;
        var fingerprintCheck = ClaimOrValidateFingerprint(blob, fingerprint);
        var recorded = fingerprintCheck.Recorded;
        if (recorded != null)
        {
            // 指紋不符：先用目前金鑰探測全部密文。每一筆非空密文都解得開（含 inventory 全空）代表所有憑證
            // 都已用目前金鑰重新輸入，舊指紋只是過期記錄，覆寫它；有任何一筆解不開（含停用的 Sentinel、SMTP）
            // 就維持不符——多站台共用資料庫、各自金鑰的情境不得被另一個站台覆寫。
            var bad = ProbeSecretsCore(settingsStore, sentinelStore, out var completed);
            if (completed && bad.Count == 0 && OverwriteFingerprint(blob, recorded, fingerprint))
            {
                Log.Warn("[Crypto] 金鑰指紋已更新為目前金鑰檔（所有憑證已用新金鑰重新輸入；原指紋 {0}，目前 {1}）。", recorded, fingerprint);
                KeyMismatch = false;
                RewrapAll(settingsStore, sentinelStore);
                return true;
            }

            KeyMismatch = true;
            if (keyFileLost)
                Log.Error("[Crypto] 金鑰檔遺失：{0} 不存在，但資料庫已有金鑰指紋 {1}，系統已新產生金鑰（指紋 {2}），" +
                          "原本加密的所有密碼欄位都將無法解密。常見原因：重新部署清掉了執行目錄，或 DataRoot 指到部署目錄。" +
                          "請從備份還原原本的金鑰檔；沒有備份時到設定頁重新輸入全部憑證（停用的 Sentinel 也要重新輸入或刪除）後重新啟動站台。" +
                          "建議把 DataRoot 放在部署目錄之外，或把 keys 資料夾排除在部署清除範圍。",
                    path, recorded, fingerprint);
            else
                Log.Error("金鑰檔與資料庫不相符（資料庫記錄的指紋 {0}，目前金鑰 {1}）：可能是還原資料庫時沒有一併還原金鑰檔，" +
                          "或多個站台共用同一個資料庫（共用資料庫的站台必須共用同一把金鑰）。所有密碼欄位將無法解密，" +
                          "請還原正確的金鑰檔，或到設定頁重新輸入全部憑證（停用的 Sentinel 也要重新輸入或刪除）後重新啟動站台。",
                    recorded, fingerprint);
            return true;
        }

        KeyMismatch = false;
        RewrapAll(settingsStore, sentinelStore);
        return false;
    }

    private static void RewrapAll(ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        var rewrapped = RewrapSettings(settingsStore) + RewrapSentinels(sentinelStore);
        if (rewrapped > 0)
            Log.Info("[Crypto] 已將 {0} 個舊格式（enc:v1）密碼欄位重新加密為 enc:v2。", rewrapped);
    }

    private static string? ReadBlobSafely(EfJsonBlobStore blob)
    {
        try { return blob.Read(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Crypto] 讀取金鑰指紋記錄失敗，無法判斷金鑰檔是否遺失：{0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 在同一個 blob 原子操作內，確認目前記錄仍是 <paramref name="expectedRecorded"/> 才覆寫成目前金鑰指紋，
    /// 並留下 UpdatedAt／ReplacedFingerprint；記錄已被其他行程改動則不寫、回 false。
    /// </summary>
    internal static bool OverwriteFingerprint(EfJsonBlobStore blob, string expectedRecorded, string fingerprint)
    {
        return blob.Mutate<bool>(current =>
        {
            var now = ReadFingerprint(current);
            if (now == null || !string.Equals(now, expectedRecorded, StringComparison.OrdinalIgnoreCase))
                return (current ?? "", false);

            DateTime createdAt;
            try { createdAt = JsonSerializer.Deserialize<FingerprintRecord>(current!)?.CreatedAt ?? DateTime.Now; }
            catch (JsonException) { createdAt = DateTime.Now; }
            var content = JsonSerializer.Serialize(new FingerprintRecord
            {
                Fingerprint = fingerprint,
                CreatedAt = createdAt,
                UpdatedAt = DateTime.Now,
                ReplacedFingerprint = expectedRecorded
            });
            return (content, true);
        });
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

        /// <summary>指紋被覆寫的時間（自動修復才有）</summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>被覆寫掉的原指紋（自動修復才有）</summary>
        public string? ReplacedFingerprint { get; set; }
    }

    private sealed class FingerprintMismatchException : Exception
    {
        public FingerprintMismatchException(string recorded) : base("金鑰指紋不符") => Recorded = recorded;

        public string Recorded { get; }
    }
}
