using System.Net.Http.Headers;
using System.Text;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Services;

/// <summary>Sentinel 目錄回報的一台主機（探索結果的最小單位）</summary>
public record NetiqDiscoveredHost(string HostName, string IpAddress);

/// <summary>探索連線/認證失敗——訊息可直接顯示給管理員（不含機敏內容）</summary>
public class NetiqDiscoveryException : Exception
{
    public NetiqDiscoveryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 向 Sentinel 主動查詢其管理的主機清單（docs/SCALE-2000-PLAN.md §1.2）。
///
/// 真實 API 的認證方式與回應格式屬環境細節（不同版本 Sentinel 不同），因此隔離在介面後：
/// <see cref="SentinelRestDirectoryClient"/> 真連線、<see cref="StubNetiqDirectoryClient"/> 給
/// 開發與測試用固定資料，整條匯入 UI 流程不必有真 Sentinel 就能開發與驗收。
/// </summary>
public interface INetiqDirectoryClient
{
    Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer server, CancellationToken ct);
}

/// <summary>
/// 開發／測試用替身：回固定的三網段示範資料，其中刻意含與既有主機重疊的 IP，
/// 讓「已登錄」「重疊復活」等分類在 demo 資料下也走得到。Development 環境注入。
/// </summary>
public class StubNetiqDirectoryClient : INetiqDirectoryClient
{
    public Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer server, CancellationToken ct)
    {
        // 依 Sentinel 名稱回不同網段，模擬「一台 Sentinel 管一批主機」
        var seed = server.Name.GetHashCode() & 0x7fffffff;
        var thirdOctet = seed % 200 + 1;

        var hosts = new List<NetiqDiscoveredHost>();
        // 網段 A（含既有 demo 主機的 IP，觸發「已登錄」）
        hosts.Add(new NetiqDiscoveredHost("SRV-OO-WEB01", "10.1.2.11"));
        hosts.Add(new NetiqDiscoveredHost("SRV-OO-DB01", "10.1.2.12"));
        for (var i = 20; i < 55; i++)
            hosts.Add(new NetiqDiscoveredHost($"SRV-{server.Name}-{i:D3}", $"10.1.2.{i}"));
        // 網段 B（全新）
        for (var i = 5; i < 28; i++)
            hosts.Add(new NetiqDiscoveredHost($"AP-{server.Name}-{i:D3}", $"10.{thirdOctet}.9.{i}"));

        return Task.FromResult(hosts);
    }
}

/// <summary>
/// Sentinel REST API 真連線。**認證方式與端點路徑待真實環境驗證前不定案**
/// （docs/SCALE-2000-PLAN.md §1.2）：此處以基本驗證＋可設定端點的骨架實作，
/// 回應解析待接上真 Sentinel 後補齊；失敗一律轉成可顯示的 NetiqDiscoveryException。
/// </summary>
public class SentinelRestDirectoryClient : INetiqDirectoryClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SentinelRestDirectoryClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new NetiqDiscoveryException($"Sentinel「{server.Name}」未設定 BaseUrl。");

        var client = _httpClientFactory.CreateClient("sentinel");
        client.Timeout = TimeSpan.FromSeconds(30);   // 掃描是互動操作，逾時要短

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.Username}:{server.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        try
        {
            // TODO(env)：端點路徑與回應 schema 待真 Sentinel 環境確認後補齊解析
            var response = await client.GetAsync($"{server.BaseUrl.TrimEnd('/')}/SecurityManager/rest/hosts", ct);
            response.EnsureSuccessStatusCode();

            throw new NetiqDiscoveryException(
                "Sentinel REST 探索尚未接上真實環境（回應解析待實作）。開發/測試請以 Stub 模式操作。");
        }
        catch (NetiqDiscoveryException) { throw; }
        catch (TaskCanceledException)
        {
            throw new NetiqDiscoveryException($"連線 Sentinel「{server.Name}」逾時（30 秒）。");
        }
        catch (Exception ex)
        {
            throw new NetiqDiscoveryException($"連線 Sentinel「{server.Name}」失敗：{ex.Message}", ex);
        }
    }
}
