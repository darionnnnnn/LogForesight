using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Sentinel store 為空時自批次 appsettings.json 種子匯入（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1、6）。
/// </summary>
public class SentinelSeederTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lf-sentinel-seed-test").FullName;
    private readonly FakeSentinelStore _sentinels = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void 批次設定檔含Sentinel_匯入並加密密碼()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            {
              "NetIq": {
                "Servers": [
                  { "Name": "S1", "BaseUrl": "https://s1", "Username": "svc", "Password": "hunter2" },
                  { "Name": "S2" }
                ]
              }
            }
            """);

        var count = SentinelSeeder.SeedIfEmpty(_sentinels, _dir);

        Assert.Equal(2, count);
        var s1 = _sentinels.FindByName("S1")!;
        Assert.Equal("https://s1", s1.BaseUrl);
        Assert.True(CryptoHelper.IsEncrypted(s1.PasswordEnc));
        Assert.Equal("hunter2", CryptoHelper.Decrypt(s1.PasswordEnc));
        Assert.True(s1.Active);

        var s2 = _sentinels.FindByName("S2")!;
        Assert.False(s2.CanDiscover);   // 沒有帳密
    }

    [Fact]
    public void store已非空_不重複匯入()
    {
        _sentinels.Upsert(new Sentinel { Name = "既有" });
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            { "NetIq": { "Servers": [ { "Name": "S1" } ] } }
            """);

        var count = SentinelSeeder.SeedIfEmpty(_sentinels, _dir);

        Assert.Equal(0, count);
        Assert.Single(_sentinels.GetAll());
    }

    [Fact]
    public void 找不到批次設定檔_回零不擲例外()
    {
        Assert.Equal(0, SentinelSeeder.SeedIfEmpty(_sentinels, _dir));
    }

    [Fact]
    public void 設定檔格式錯誤_回零不擲例外()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), "{ not valid json");

        Assert.Equal(0, SentinelSeeder.SeedIfEmpty(_sentinels, _dir));
    }
}
