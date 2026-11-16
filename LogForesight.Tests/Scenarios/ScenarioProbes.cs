using System.Text.Json;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 當天結束時的「讀取探針」：以某帳號的身分對站台打一支 GET，把狀態碼與回應本文記進當天的結果，
/// 供預期斷言讀取畫面看得到的東西（清單、徽章、看板、待派、頁面殼）。只走 HTTP，不直接讀資料表。
/// 探針在當天人為動作之後執行。
/// </summary>
internal sealed class ScenarioProbe
{
    public ScenarioProbe(string name, string actor, Func<ScenarioActionContext, string> url)
    {
        Name = name;
        Actor = actor;
        Url = url;
    }

    public string Name { get; }

    public string Actor { get; }

    public Func<ScenarioActionContext, string> Url { get; }

    internal async Task<ProbeView> RunAsync(ScenarioActionContext ctx)
    {
        string url;
        try
        {
            url = Url(ctx);
        }
        catch (ScenarioUnresolvedException ex)
        {
            return new ProbeView(Name, Actor, "", 0, ex.Message);
        }

        var result = await ctx.Site.SendAsync(Actor, HttpMethod.Get, url);
        return new ProbeView(Name, Actor, url, result.StatusCode, result.Body);
    }
}

/// <summary>一個探針的讀取結果。<see cref="StatusCode"/> 0＝沒有送出（網址解析不到，原因放在本文）</summary>
public sealed record ProbeView(string Name, string Actor, string Url, int StatusCode, string? Body)
{
    public bool Ok => StatusCode is >= 200 and < 300;

    /// <summary>ApiResponse 的 data；非 JSON 或沒有 data 時回 null</summary>
    public JsonElement? Data
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(Body);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data)
                    ? data.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>data 底下的陣列（清單的 items、看板的 rows 等）；取不到回空</summary>
    public IReadOnlyList<JsonElement> Items(string property)
    {
        if (Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.EnumerateArray().ToList();
        return Array.Empty<JsonElement>();
    }

    /// <summary>data 底下的整數欄位；取不到回 -1</summary>
    public int Int(string property) =>
        Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : -1;

    /// <summary>data 底下的布林欄位；取不到回 null</summary>
    public bool? Bool(string property) =>
        Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty(property, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

public static class ProbeJson
{
    public static string Str(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static long Long(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;

    public static int Int(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

    public static bool Flag(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
}
