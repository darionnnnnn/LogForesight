using System.Text.Json;

namespace LogForesight.Web.Services;

/// <summary>
/// Sentinel store 為空時，自批次 appsettings.json 的 <c>NetIq.Servers</c> 做一次性種子匯入
/// （docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1、6）。之後 Sentinel 一律由 Web 維護，
/// 這段只服務既有部署從舊設定升級的那一次；跑在 Web 啟動時，冪等（store 非空即略過）。
/// </summary>
public static class SentinelSeeder
{
    /// <summary>回傳實際匯入筆數（0＝store 已非空，或找不到/解析不了批次設定檔）</summary>
    public static int SeedIfEmpty(ISentinelStore sentinels, string dataRoot)
    {
        if (sentinels.GetAll().Count > 0) return 0;

        var settingsPath = Path.Combine(dataRoot, "appsettings.json");
        if (!File.Exists(settingsPath)) return 0;

        List<SentinelServer> seeds;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), options);
            seeds = parsed?.NetIq.Servers ?? new List<SentinelServer>();
        }
        catch (JsonException)
        {
            // 讀不到就不種——不該讓 Web 啟動失敗；admin 可從 Sentinel 管理頁手動新增
            return 0;
        }

        var count = 0;
        foreach (var seed in seeds.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            sentinels.Upsert(new Sentinel
            {
                Name = seed.Name.Trim(),
                BaseUrl = seed.BaseUrl,
                Username = seed.Username,
                PasswordEnc = CryptoHelper.Encrypt(seed.Password),
                Active = true
            });
            count++;
        }

        return count;
    }
}
