using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 設定檔載入的兩種缺席語意（2026-07-23 定案）：不存在→預設值（開箱即用）、
/// 存在但壞掉→擲例外中止（有設定意圖就不能靜默用預設值跑——Storage.Type 退回
/// 預設會把資料寫進分裂的儲存後端）。起因：Storage 段一個括號錯誤讓整份設定
/// 含 AI 位址被靜默丟棄，使用者只看到「AI 抓到預設值」卻找不到原因。
/// </summary>
public class AppSettingsLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lf-settings-test").FullName;

    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void 檔案不存在_使用預設值()
    {
        var settings = AppSettings.Load(PathOf("missing.json"));

        Assert.Equal("http://localhost:8080", settings.Ai.BaseUrl);
        Assert.Equal("Sqlite", settings.Storage.Type);
    }

    [Fact]
    public void 格式正確_讀取設定值()
    {
        var path = PathOf("valid.json");
        File.WriteAllText(path, """
            {
              // 註解與尾逗號都要支援（appsettings.json 檔頭承諾的格式）
              "Ai": { "BaseUrl": "http://ai-server:9999", "TimeoutSeconds": 30, },
              "Storage": { "Type": "Sqlite" },
            }
            """);

        var settings = AppSettings.Load(path);

        Assert.Equal("http://ai-server:9999", settings.Ai.BaseUrl);
        Assert.Equal(30, settings.Ai.TimeoutSeconds);
        Assert.Equal("Sqlite", settings.Storage.Type);
    }

    /// <summary>回歸釘樁：正是造成本次事故的形態——Storage 段物件括號漏掉，
    /// 後面所有內容變成非法 JSON。舊行為是靜默退回預設值（含 AI 段一起丟掉）。</summary>
    [Fact]
    public void 存在但格式錯誤_擲例外不得靜默用預設值()
    {
        var path = PathOf("broken.json");
        File.WriteAllText(path, """
            {
              "Ai": { "BaseUrl": "http://ai-server:9999" },
                "Type": "Sqlite",
                "DataRoot": ""
              },
              "NetIq": { "Servers": [] }
            }
            """);

        var ex = Assert.Throws<AppSettingsLoadException>(() => AppSettings.Load(path));

        // 訊息要能讓人自己修好：含檔案位置與修正指引
        Assert.Contains(path, ex.Message);
        Assert.Contains("修正 JSON 語法", ex.Message);
    }
}
