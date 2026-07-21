using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機清單來源（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 D）。
/// **步驟 4 的驗收閘門是「txt 與 Web 兩模式輸出相同清單」**——換個主人不該換一批主機。
/// </summary>
public class HostListProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHostStore _hosts = new();

    private static readonly string[] KnownServers = { "SENTINEL-A", "SENTINEL-B" };

    public HostListProviderTests() => Directory.CreateDirectory(_dir);

    private void WriteList(string serverName, string content) =>
        File.WriteAllText(Path.Combine(_dir, $"{serverName}.txt"), content);

    private TxtHostListProvider TxtProvider() => new(_hosts, _dir, KnownServers);

    // ── 驗收閘門：兩模式輸出一致 ─────────────────────────────────────────────

    /// <summary>
    /// 交接 SOP 的核心保證：以 txt 同步一次之後切成 Web 模式，選出來的主機必須完全一樣
    /// （含 HostId——那是分析紀錄的關聯鍵，換一個就等於換了一台主機）。
    /// </summary>
    [Fact]
    public void 兩模式輸出相同清單()
    {
        WriteList("SENTINEL-A", "10.0.0.1,DB 主機\n10.0.0.2");
        WriteList("SENTINEL-B", "10.0.1.1,AP 主機");

        var fromTxt = TxtProvider().GetHostList();
        var fromStore = new StoreHostListProvider(_hosts).GetHostList();

        Assert.Equal(Flatten(fromTxt), Flatten(fromStore));
        Assert.Equal(3, fromTxt.TotalHosts);
    }

    private static List<string> Flatten(HostListResult result) =>
        result.ByServer
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => p.Value.Select(t => $"{p.Key}|{t.HostId}|{t.IpAddress}|{t.RoleDesc}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    // ── txt 同步 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Txt_檔名決定所屬Sentinel()
    {
        WriteList("SENTINEL-B", "10.0.1.1");

        var result = TxtProvider().GetHostList();

        Assert.Equal("SENTINEL-B", result.ByServer.Keys.Single());
    }

    [Fact]
    public void Txt_註解與空行忽略_不合法的行略過並警告()
    {
        WriteList("SENTINEL-A", "# 機房 A\n\n10.0.0.1\nSRV-BAD\n");

        var result = TxtProvider().GetHostList();

        Assert.Equal(1, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("SRV-BAD"));
    }

    /// <summary>檔名打錯的後果是整檔主機靜默地不會被查——必須警告而不是安靜略過</summary>
    [Fact]
    public void Txt_檔名不對應任何Sentinel_整檔略過並警告()
    {
        WriteList("SENTINEL-TYPO", "10.0.0.1");

        var result = TxtProvider().GetHostList();

        Assert.Equal(0, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("SENTINEL-TYPO"));
    }

    /// <summary>IP 全域唯一是主機識別的前提，出現在兩個清單裡就是設定錯誤</summary>
    [Fact]
    public void Txt_同一IP出現在兩個清單_只取第一個並警告()
    {
        WriteList("SENTINEL-A", "10.0.0.1");
        WriteList("SENTINEL-B", "10.0.0.1");

        var result = TxtProvider().GetHostList();

        Assert.Equal(1, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("已在其他清單中出現"));
    }

    // ── txt 是主人：移除即停止分析 ───────────────────────────────────────────

    [Fact]
    public void Txt_清單移除的主機_停用但保留主機列()
    {
        WriteList("SENTINEL-A", "10.0.0.1\n10.0.0.2");
        TxtProvider().GetHostList();

        WriteList("SENTINEL-A", "10.0.0.1");
        var result = TxtProvider().GetHostList();

        Assert.Equal(1, result.TotalHosts);
        Assert.False(_hosts.FindByName("10.0.0.2")!.Active);
        Assert.Equal(2, _hosts.GetAll().Count);   // 主機列保留，歷史才追溯得到
    }

    /// <summary>
    /// **這條是刻意的安全設計**：某台 Sentinel 的 txt 暫時不見（誤刪、檔案伺服器沒掛上）時，
    /// 不該把它轄下的主機整批停掉——那會讓一整個機房靜默地停止被監控。
    /// </summary>
    [Fact]
    public void Txt_某台Sentinel的清單檔消失_不影響其轄下主機()
    {
        WriteList("SENTINEL-A", "10.0.0.1");
        WriteList("SENTINEL-B", "10.0.1.1");
        TxtProvider().GetHostList();

        File.Delete(Path.Combine(_dir, "SENTINEL-B.txt"));
        var result = TxtProvider().GetHostList();

        Assert.True(_hosts.FindByName("10.0.1.1")!.Active);
        Assert.Equal(2, result.TotalHosts);
    }

    [Fact]
    public void Txt_Web維護欄位不被覆寫()
    {
        WriteList("SENTINEL-A", "10.0.0.1,txt 的描述");
        TxtProvider().GetHostList();

        var host = _hosts.FindByName("10.0.0.1")!;
        _hosts.SetGroups(host.HostId, new long[] { 7 });
        _hosts.SetOwners(host.HostId, new long[] { 3 });

        TxtProvider().GetHostList();

        var after = _hosts.FindByName("10.0.0.1")!;
        Assert.Equal(new long[] { 7 }, after.GroupIds);
        Assert.Equal(new long[] { 3 }, after.OwnerUserIds);
    }

    /// <summary>txt 沒寫描述不代表要清掉 Web 上填過的</summary>
    [Fact]
    public void Txt_該行未帶描述_保留既有角色描述()
    {
        WriteList("SENTINEL-A", "10.0.0.1,原本的描述");
        TxtProvider().GetHostList();

        WriteList("SENTINEL-A", "10.0.0.1");
        TxtProvider().GetHostList();

        Assert.Equal("原本的描述", _hosts.FindByName("10.0.0.1")!.RoleDesc);
    }

    // ── 來源不可用與排除警告 ─────────────────────────────────────────────────

    /// <summary>「目錄不存在」與「清單是空的」要分得開：前者是設定沒完成，不能當成沒主機要查</summary>
    [Fact]
    public void Txt_目錄不存在_標示來源不可用()
    {
        var provider = new TxtHostListProvider(_hosts, Path.Combine(_dir, "不存在"), KnownServers);

        var result = provider.GetHostList();

        Assert.False(result.SourceUsable);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Txt_目錄沒有任何txt_標示來源不可用()
    {
        Assert.False(TxtProvider().GetHostList().SourceUsable);
    }

    [Fact]
    public void 待歸屬主機_不查詢並列出原因()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.0.0.9", IpAddress = "10.0.0.9", Source = "netiq", Active = true
        });

        var result = new StoreHostListProvider(_hosts).GetHostList();

        Assert.Equal(0, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("尚未確定所屬 Sentinel"));
    }

    [Fact]
    public void IP衝突_只查最早建立的那台並列出被跳過的()
    {
        foreach (var name in new[] { "SRV-A", "SRV-B" })
        {
            _hosts.Upsert(new WebHost
            {
                HostName = name, IpAddress = "10.0.0.9",
                NetiqServer = "SENTINEL-A", Source = "netiq", Active = true
            });
        }

        var result = new StoreHostListProvider(_hosts).GetHostList();

        Assert.Equal(1, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("衝突"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
