using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Services;

/// <summary>去識別化失敗的原因類別（不含任何原值）</summary>
public enum DeidentificationFailureKind
{
    /// <summary>字典來源（主機、使用者、Sentinel、PRTG 鏡像、設定、密鑰）讀取或解密失敗</summary>
    SourceUnavailable,

    /// <summary>序列化後的整份內容仍掃到字典原值、IPv4、Email 或密鑰</summary>
    ResidualFound
}

/// <summary>
/// 排程作業診斷匯出的去識別化失敗：字典來源失敗、或結果掃描有殘留。
/// 對應 HTTP 500 與錯誤碼 <c>export_deidentification_failed</c>（由 <c>ApiExceptionFilter</c> 轉譯）。
///
/// **訊息與屬性只放來源名稱、類別與次數，永遠不放原值**——這個例外會被寫進伺服器 log，
/// 若帶原值，去識別化本身就變成外洩路徑。
/// </summary>
public sealed class RunExportDeidentificationException : Exception
{
    public DeidentificationFailureKind Kind { get; }

    /// <summary>來源讀取失敗時的來源名稱（例如「主機清單」）；殘留類別為 null</summary>
    public string? SourceName { get; }

    /// <summary>殘留類別 → 次數；來源失敗時為空</summary>
    public IReadOnlyDictionary<string, int> Residual { get; }

    public RunExportDeidentificationException(string sourceName, Exception inner)
        : base($"無法讀取去識別化所需的「{sourceName}」，為避免外洩已中止匯出，未產生任何檔案。請稍後再試；若持續發生請聯繫系統管理員。", inner)
    {
        Kind = DeidentificationFailureKind.SourceUnavailable;
        SourceName = sourceName;
        Residual = new Dictionary<string, int>();
    }

    public RunExportDeidentificationException(IReadOnlyDictionary<string, int> residual)
        : base("匯出內容無法確認已完成去識別化，為避免外洩已中止匯出，未產生任何檔案。請聯繫系統管理員查看伺服器紀錄（僅記錄殘留類別與次數）。")
    {
        Kind = DeidentificationFailureKind.ResidualFound;
        Residual = residual;
    }
}

/// <summary>主機的去識別化來源欄位（只含會出現在文字裡的識別值）</summary>
public sealed record DeidentifierHost(
    long HostId, string? HostName, string? DisplayName, string? IpAddress,
    string? NetiqServer, string? OrphanedFromSentinel, string? RoleDesc);

/// <summary>使用者的去識別化來源欄位</summary>
public sealed record DeidentifierUser(long UserId, string? Account, string? DisplayName, string? Email);

/// <summary>Sentinel 的去識別化來源欄位（密碼走 <see cref="RunExportDeidentifierSource.Secrets"/>）</summary>
public sealed record DeidentifierSentinel(long SentinelId, string? Name, string? BaseUrl, string? Username);

/// <summary>PRTG 裝置鏡像的去識別化來源欄位</summary>
public sealed record DeidentifierPrtgDevice(long Objid, string? Name, string? Ip, string? GroupPath);

/// <summary>系統設定中會出現在文字裡的識別值，已依用途分類</summary>
public sealed record DeidentifierSettings
{
    /// <summary>站台名稱、伺服器描述等自由文字（遮成 domain 代號）</summary>
    public IReadOnlyList<string?> Names { get; init; } = Array.Empty<string?>();

    /// <summary>服務位址（遮成 url 代號，網址中的主機部分遮成 domain 代號）</summary>
    public IReadOnlyList<string?> Urls { get; init; } = Array.Empty<string?>();

    /// <summary>伺服器名稱或搜尋基底（SMTP、AD；遮成 domain 代號）</summary>
    public IReadOnlyList<string?> Domains { get; init; } = Array.Empty<string?>();

    /// <summary>服務帳號（SMTP、PRTG；遮成 user 代號）</summary>
    public IReadOnlyList<string?> Accounts { get; init; } = Array.Empty<string?>();

    /// <summary>寄件者與收件者信箱（遮成 email 代號）</summary>
    public IReadOnlyList<string?> Emails { get; init; } = Array.Empty<string?>();

    private const string DefaultBrandName = "LogForesight";
    private const string DefaultBrandSubtitle = "事件日誌預警";

    /// <summary>
    /// AD 搜尋基準的網域名：<c>DC=brk,DC=local</c> → <c>brk.local</c>、<c>brk</c>。事件訊息常只印 NetBIOS 短名，
    /// 設定裡沒有獨立欄位，這是唯一來源。最後一段是頂級網域（local、com）不單獨收，短於 3 字的標籤也不收，避免遮掉一般用語。
    /// </summary>
    private static IEnumerable<string?> AdDomainNames(string? searchBase)
    {
        if (string.IsNullOrWhiteSpace(searchBase)) yield break;

        var labels = searchBase.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part[3..].Trim())
            .Where(label => label.Length > 0)
            .ToList();
        if (labels.Count == 0) yield break;

        yield return string.Join('.', labels);
        foreach (var label in labels.Take(labels.Count - 1))
            if (label.Length >= 3) yield return label;
    }

    /// <summary>
    /// 由系統設定投影。預設的產品名稱與副標題不是站台識別，不放進字典
    /// （否則「LogForesight」這個詞在每一則訊息都被遮掉，分析價值歸零）；改過的才算。
    /// </summary>
    public static DeidentifierSettings From(SystemSettings s)
    {
        var names = new List<string?>();
        if (!string.Equals(s.BrandName?.Trim(), DefaultBrandName, StringComparison.OrdinalIgnoreCase)) names.Add(s.BrandName);
        if (!string.Equals(s.BrandSubtitle?.Trim(), DefaultBrandSubtitle, StringComparison.Ordinal)) names.Add(s.BrandSubtitle);
        names.Add(s.ServerDescription);

        var domains = new List<string?> { s.SmtpServer, s.AdSearchBase };
        domains.AddRange(s.AdServers ?? new List<string>());
        domains.AddRange(AdDomainNames(s.AdSearchBase));

        var emails = new List<string?> { s.MailFrom };
        emails.AddRange(s.MailRecipients ?? new List<string>());

        return new DeidentifierSettings
        {
            Names = names,
            Urls = new List<string?> { s.PrtgUrl, s.AiBaseUrl },
            Domains = domains,
            Accounts = new List<string?> { s.SmtpAccount, s.PrtgUsername, s.UpdatedByAccount },
            Emails = emails
        };
    }
}

/// <summary>
/// 去識別化字典的完整來源資料。測試可直接用記憶體資料建立，不需要資料庫；
/// 正式環境由 <see cref="RunExportDeidentifierFactory"/> 從各 store 讀出。
/// </summary>
public sealed record RunExportDeidentifierSource
{
    public IReadOnlyList<DeidentifierHost> Hosts { get; init; } = Array.Empty<DeidentifierHost>();

    public IReadOnlyList<string?> HostGroupNames { get; init; } = Array.Empty<string?>();

    public IReadOnlyList<DeidentifierUser> Users { get; init; } = Array.Empty<DeidentifierUser>();

    public IReadOnlyList<DeidentifierSentinel> Sentinels { get; init; } = Array.Empty<DeidentifierSentinel>();

    public IReadOnlyList<DeidentifierPrtgDevice> PrtgDevices { get; init; } = Array.Empty<DeidentifierPrtgDevice>();

    public DeidentifierSettings Settings { get; init; } = new();

    /// <summary>已設定的密鑰實際值（解密後的明碼）：AI 金鑰、SMTP、PRTG 密碼／token／passhash、Sentinel 密碼</summary>
    public IReadOnlyList<string> Secrets { get; init; } = Array.Empty<string>();

    /// <summary>用目前金鑰解不開、因此無法比對實際值的憑證名稱（例如「PRTG 密碼」），不含任何值</summary>
    public IReadOnlyList<string> UndecryptableSecrets { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 建立「本次匯出專用」的遮罩器：從主機、主機群組、使用者、Sentinel、PRTG 裝置鏡像與系統設定讀出字典來源。
/// 每次匯出呼叫一次 <see cref="Create"/>，代號只在該實例內穩定。
///
/// **任一來源讀取失敗就丟 <see cref="RunExportDeidentificationException"/>**，不以殘缺字典繼續；
/// 密鑰解不開不中止，改記進 <see cref="RunExportDeidentifierSource.UndecryptableSecrets"/>（理由見 <see cref="Secret"/>）。
/// PRTG 未啟用或鏡像為空屬正常（PRTG 未啟用時鏡像讀取失敗也視為沒有資料，已啟用才算失敗）。
/// 身分規則放在 Web，不帶進 Core。
/// </summary>
public sealed class RunExportDeidentifierFactory
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly ISentinelStore _sentinels;
    private readonly Func<IReadOnlyList<PrtgDeviceRow>> _prtgDevices;
    private readonly ISystemSettingsStore _settings;

    public RunExportDeidentifierFactory(
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IUserStore users,
        ISentinelStore sentinels,
        Func<IReadOnlyList<PrtgDeviceRow>> prtgDevices,
        ISystemSettingsStore settings)
    {
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _sentinels = sentinels;
        _prtgDevices = prtgDevices;
        _settings = settings;
    }

    /// <summary>讀出所有來源並建立本次匯出專用的遮罩器</summary>
    public RunExportDeidentifier Create() => RunExportDeidentifier.Create(ReadSource());

    /// <summary>只讀取來源資料（不建立遮罩器），供測試與批次C 檢查</summary>
    public RunExportDeidentifierSource ReadSource()
    {
        var settings = Read("系統設定", _settings.Get);
        var hosts = Read("主機清單", _hosts.GetAll);
        var groups = Read("主機群組", _hostGroups.GetAll);
        var users = Read("使用者清單", _users.GetAll);
        var sentinels = Read("Sentinel 設定", _sentinels.GetAll);

        IReadOnlyList<PrtgDeviceRow> devices;
        try
        {
            devices = _prtgDevices();
        }
        catch (Exception) when (!settings.PrtgEnabled)
        {
            // PRTG 沒啟用：鏡像沒有資料屬正常，讀不到不阻擋匯出（已啟用才視為來源失敗）
            devices = Array.Empty<PrtgDeviceRow>();
        }
        catch (Exception ex)
        {
            throw new RunExportDeidentificationException("PRTG 裝置鏡像", ex);
        }

        // 密文欄位清單只有 SecretInventory 一份（啟動探測與取數前置檢查共用）；這裡不看「這趟用不用得到」，全部都要比對
        var secrets = new List<string>();
        var undecryptable = new List<string>();
        foreach (var entry in SecretInventory.Build(settings, sentinels))
            Secret(secrets, undecryptable, entry.Name, entry.Ciphertext, entry.Retired);

        return new RunExportDeidentifierSource
        {
            Hosts = hosts.Select(h => new DeidentifierHost(
                    h.HostId, h.HostName, h.DisplayName, h.IpAddress, h.NetiqServer, h.OrphanedFromSentinel, h.RoleDesc))
                // PRTG 數值取數的額外主機是使用者手填的主機名或 IP，不在主機清單裡，同樣是識別資訊
                .Concat((settings.PrtgValueFetchExtraHosts ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => new DeidentifierHost(0, x, null, null, null, null, null)))
                .ToList(),
            HostGroupNames = groups.Select(g => (string?)g.GroupName).ToList(),
            Users = users.Select(u => new DeidentifierUser(u.UserId, u.Account, u.DisplayName, u.Email)).ToList(),
            Sentinels = sentinels.Select(s => new DeidentifierSentinel(s.SentinelId, s.Name, s.BaseUrl, s.Username)).ToList(),
            PrtgDevices = devices.Select(d => new DeidentifierPrtgDevice(d.Objid, d.Name, d.Ip, d.GroupPath)).ToList(),
            Settings = DeidentifierSettings.From(settings),
            Secrets = secrets,
            UndecryptableSecrets = undecryptable.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static T Read<T>(string sourceName, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (RunExportDeidentificationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RunExportDeidentificationException(sourceName, ex);
        }
    }

    /// <summary>
    /// 把密文欄位解成明碼放進密鑰清單；非密文格式的值（舊資料或測試）原樣當成密鑰。
    ///
    /// **解不開的密文不中止匯出**：密文用目前金鑰解不開，常見於搬站或換金鑰後留下的舊憑證（站台照常運作，
    /// 也可能根本不是目前使用的認證方式）。它阻擋匯出時，使用者只能重填憑證才拿得到檔案，與排查問題的用途相反。
    /// 目前的行程讀不出這個值，也無法用它連線，因此不會把它寫進之後的紀錄；只有換金鑰前寫入的舊紀錄有理論風險，
    /// 由 <c>password=</c>、<c>passhash=</c>、<c>Bearer</c> 等樣式比對接手，並把欄位名稱寫進檔案的 meta 交代。
    /// </summary>
    internal static void Secret(List<string> secrets, List<string> undecryptable, string name, string? stored, bool retired = false)
    {
        if (string.IsNullOrEmpty(stored)) return;

        var decrypted = retired ? CryptoHelper.TryDecryptQuietly(stored, out var plain) : CryptoHelper.TryDecrypt(stored, out plain);
        if (!decrypted)
        {
            undecryptable.Add(name);
            Log.Warn("診斷匯出：「{0}」用目前金鑰解不開，略過實際值比對；請重新設定該憑證。", name);
            return;
        }

        if (!string.IsNullOrEmpty(plain)) secrets.Add(plain);
    }
}
