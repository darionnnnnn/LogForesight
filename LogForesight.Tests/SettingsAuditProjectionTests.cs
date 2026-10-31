using System.Reflection;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>系統設定稽核投影的守門：每個設定欄位都要出現在稽核輸出，機密與大圖不得以原文落盤。</summary>
public sealed class SettingsAuditProjectionTests
{
    // 不進稽核輸出、也不由投影欄位代表的屬性（機密改由呼叫端的 *Changed 布林表達；其餘為內部／版本欄位）
    private static readonly HashSet<string> Excluded = new()
    {
        nameof(SystemSettings.AiApiKeyEnc),
        nameof(SystemSettings.SmtpPasswordEnc),
        nameof(SystemSettings.Revision),
        nameof(SystemSettings.UpdatedAt),
        nameof(SystemSettings.UpdatedByAccount),
        nameof(SystemSettings.ExtensionData)
    };

    // 以「有無」或雜湊代表的屬性：原文不進稽核，輸出欄位名稱不同
    private static readonly Dictionary<string, string> Represented = new()
    {
        [nameof(SystemSettings.BrandIconDataUri)] = "BrandIconSha12",
        [nameof(SystemSettings.PrtgApiTokenEnc)] = "PrtgHasApiToken",
        [nameof(SystemSettings.PrtgPasswordEnc)] = "PrtgHasPassword",
        [nameof(SystemSettings.PrtgPasshashEnc)] = "PrtgHasPasshash"
    };

    [Fact]
    public void 每個設定欄位都在投影輸出或明列排除清單()
    {
        var projected = SettingsAuditProjection.From(new SystemSettings())
            .GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet();

        var missing = typeof(SystemSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !Excluded.Contains(name))
            .Where(name => !projected.Contains(Represented.TryGetValue(name, out var alias) ? alias : name))
            .ToList();

        Assert.True(missing.Count == 0,
            "稽核投影漏掉設定欄位（新增欄位請同步加進 SettingsAuditProjection.From，或在此明列排除理由）：" + string.Join("、", missing));
    }

    [Fact]
    public void 機密與圖檔原文不出現在投影的JSON()
    {
        var settings = new SystemSettings
        {
            AiApiKeyEnc = "SECRET-AI-KEY-ENC",
            SmtpPasswordEnc = "SECRET-SMTP-ENC",
            PrtgApiTokenEnc = "SECRET-PRTG-TOKEN-ENC",
            PrtgPasswordEnc = "SECRET-PRTG-PWD-ENC",
            PrtgPasshashEnc = "SECRET-PRTG-HASH-ENC",
            BrandIconDataUri = "data:image/png;base64,QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo="
        };

        var json = JsonSerializer.Serialize(SettingsAuditProjection.From(settings));

        Assert.DoesNotContain("SECRET-", json);
        Assert.DoesNotContain("QUJDREVGR0hJSktM", json);
        Assert.DoesNotContain("data:image", json);
        Assert.Contains("\"PrtgHasApiToken\":true", json);
    }

    [Fact]
    public void 品牌圖記雜湊前12碼_無圖記無()
    {
        var withIcon = JsonSerializer.Serialize(SettingsAuditProjection.From(new SystemSettings { BrandIconDataUri = "data:image/png;base64,AAAA" }));
        var without = JsonSerializer.Serialize(SettingsAuditProjection.From(new SystemSettings { BrandIconDataUri = "" }));

        var hash = JsonDocument.Parse(withIcon).RootElement.GetProperty("BrandIconSha12").GetString();
        Assert.Matches("^[0-9a-f]{12}$", hash);
        Assert.Equal("無", JsonDocument.Parse(without).RootElement.GetProperty("BrandIconSha12").GetString());
    }
}
