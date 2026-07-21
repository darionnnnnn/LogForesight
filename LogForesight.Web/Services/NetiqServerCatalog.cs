using System.Text.Json;
using LogForesight.Web.Configuration;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// Sentinel 名單的唯讀來源（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 E）。
///
/// **單一事實來源是批次的 appsettings.json**：Web 的資料根目錄本來就指向批次執行檔目錄，
/// 直接唯讀解析同一份檔案即可。刻意不建 Sentinel 管理表、不做 Web 端 CRUD——
/// 加一台 Sentinel 本來就要改批次設定（BaseUrl 與查詢帳密只有批次用得到），
/// 兩處各存一份名單只會分歧，而分歧的後果是「畫面上選得到、批次卻查不到」。
/// </summary>
public interface INetiqServerCatalog
{
    /// <summary>已設定的 Sentinel 名稱（依名稱排序）。未設定 NetIq 區段時為空清單</summary>
    List<string> GetServerNames();

    /// <summary>名稱是否存在於設定中（不分大小寫）；用於登錄與匯入時的驗證</summary>
    bool IsKnownServer(string? name);
}

public class NetiqServerCatalog : INetiqServerCatalog
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _settingsPath;
    private readonly object _lock = new();

    private List<string>? _cached;
    private DateTime _cachedFileTime;

    public NetiqServerCatalog(WebAppSettings settings)
    {
        _settingsPath = Path.Combine(settings.Storage.ResolveDataRoot(), "appsettings.json");
    }

    public List<string> GetServerNames()
    {
        lock (_lock)
        {
            // 依檔案時間快取：設定改了不必重啟 Web，但也不用每次請求都讀檔解析
            var lastWrite = File.Exists(_settingsPath) ? File.GetLastWriteTimeUtc(_settingsPath) : DateTime.MinValue;

            if (_cached == null || lastWrite != _cachedFileTime)
            {
                _cached = LoadServerNames();
                _cachedFileTime = lastWrite;
            }

            return _cached;
        }
    }

    public bool IsKnownServer(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        GetServerNames().Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private List<string> LoadServerNames()
    {
        if (!File.Exists(_settingsPath))
        {
            Log.Info("批次設定檔不存在（{0}），Sentinel 名單為空", _settingsPath);
            return new List<string>();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), options);

            return (parsed?.NetIq.Servers ?? new List<SentinelServer>())
                .Select(s => s.Name?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 讀不到就是名單為空，不讓站台掛掉——但要留下紀錄，
            // 否則畫面上「Sentinel 下拉是空的」會查不出原因
            Log.Warn(ex, "解析批次設定檔的 NetIq.Servers 失敗（{0}）：{1}", _settingsPath, ex.Message);
            return new List<string>();
        }
    }
}
