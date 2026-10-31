using System.Reflection;
using System.Text.Json;
using LogForesight.Web.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// appsettings.json 與強型別設定類別（<see cref="WebAppSettings"/>）的鍵對照守門：
/// 範例檔裡的每個鍵都必須對得到一個公開屬性（打錯字或改名後留下的鍵會被靜默忽略，
/// 部署者以為設定了其實沒生效），而強型別有的鍵範例檔也要列出，運維才找得到。
/// </summary>
public class AppSettingsKeyGuardTests
{
    /// <summary>不屬於 <see cref="WebAppSettings"/> 的頂層鍵（框架自己讀的）</summary>
    private static readonly HashSet<string> FrameworkTopLevelKeys = new(StringComparer.OrdinalIgnoreCase) { "AllowedHosts" };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static JsonElement LoadAppSettings()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "appsettings.json");
        var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return doc.RootElement.Clone();
    }

    /// <summary>公開、可讀寫的實例屬性；巢狀設定物件（自訂 class，非字串／集合）需要往下比對</summary>
    private static PropertyInfo[] SettingProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).ToArray();

    private static bool IsSectionType(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    private static void CollectUnknownKeys(JsonElement element, Type type, string path, List<string> unknown)
    {
        var props = SettingProperties(type).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var member in element.EnumerateObject())
        {
            var keyPath = path.Length == 0 ? member.Name : path + ":" + member.Name;
            if (path.Length == 0 && FrameworkTopLevelKeys.Contains(member.Name)) continue;
            if (!props.TryGetValue(member.Name, out var prop))
            {
                unknown.Add(keyPath);
                continue;
            }
            if (member.Value.ValueKind == JsonValueKind.Object && IsSectionType(prop.PropertyType))
                CollectUnknownKeys(member.Value, prop.PropertyType, keyPath, unknown);
        }
    }

    private static void CollectMissingKeys(JsonElement element, Type type, string path, List<string> missing)
    {
        var present = element.EnumerateObject().ToDictionary(m => m.Name, m => m.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in SettingProperties(type))
        {
            var keyPath = path.Length == 0 ? prop.Name : path + ":" + prop.Name;
            if (!present.TryGetValue(prop.Name, out var value))
            {
                missing.Add(keyPath);
                continue;
            }
            if (value.ValueKind == JsonValueKind.Object && IsSectionType(prop.PropertyType))
                CollectMissingKeys(value, prop.PropertyType, keyPath, missing);
        }
    }

    [Fact]
    public void appsettings每個鍵都對得到強型別設定類別的公開屬性()
    {
        var unknown = new List<string>();
        CollectUnknownKeys(LoadAppSettings(), typeof(WebAppSettings), "", unknown);

        Assert.True(unknown.Count == 0,
            "appsettings.json 含有強型別設定類別沒有的鍵（會被靜默忽略）：" + string.Join("、", unknown));
    }

    [Fact]
    public void 強型別設定類別的每個鍵_appsettings範例檔都有列出()
    {
        var missing = new List<string>();
        CollectMissingKeys(LoadAppSettings(), typeof(WebAppSettings), "", missing);

        Assert.True(missing.Count == 0,
            "強型別設定有、但 appsettings.json 範例檔沒列的鍵（運維無從發現）：" + string.Join("、", missing));
    }
}
