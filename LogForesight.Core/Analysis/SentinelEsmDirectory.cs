using System.Text.Json;

namespace LogForesight.Core.Analysis;

/// <summary>ESM 事件來源目錄裡的一台主機（解析結果的最小單位）</summary>
public sealed record EsmEventSource(string Name, string IpAddress);

/// <summary>
/// ESM 事件來源目錄（<c>/SentinelRESTServices/objects/eventsource</c>）的回應解析
/// （docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §5.2）。
///
/// <para><b>這是一份「防禦性」解析器，不是已定案的欄位對應。</b>
/// 本專案其他的 Sentinel 欄位對應（<see cref="SentinelFieldMap"/>）全部有真實 probe
/// 樣本背書；這裡沒有——本環境的探索帳號對 ESM 端點被 401/403 拒絕，
/// 拿不到任何一筆真實回應。下面的候選欄位名是依公開 7.0 apidoc 與 Sentinel 物件慣例
/// 列舉的，**未經本環境實測**。</para>
///
/// <para>因此設計原則是「<b>寧可退路，不可猜錯</b>」：
/// 解析不出任何一台合法主機時，回傳的是「不可用」而**不是**「這台 Sentinel 沒有主機」——
/// 兩者的後果天差地遠，前者退回事件掃描、後者會讓管理員以為機房空了。
/// 呼叫端據此決定要不要退回事件掃描（見 <c>SentinelRestDirectoryClient</c>）。</para>
///
/// <para>拿到真實輸出之後要做的事：把樣本存成測試 fixture、依實際欄位收斂候選清單、
/// 更新 docs/NETIQ-API-REFERENCE.md——這份防禦版才算轉正。</para>
/// </summary>
public static class SentinelEsmDirectory
{
    /// <summary>
    /// IP 欄位的候選名稱（依可能性排序）。Sentinel 的物件模型在不同版本用過不同名稱，
    /// 而我們無法在本環境確認是哪一個——全部試，取第一個解析出合法 IPv4 的。
    /// </summary>
    private static readonly string[] IpFieldCandidates =
    {
        "ipAddress", "ip", "hostIp", "IPAddress", "IP", "address", "sourceIp", "networkAddress"
    };

    /// <summary>名稱欄位的候選（同上，未經實測）</summary>
    private static readonly string[] NameFieldCandidates =
    {
        "name", "Name", "hostname", "hostName", "displayName", "eventSourceName", "sourceName"
    };

    /// <summary>
    /// 解析 ESM 回應。
    /// </summary>
    /// <param name="json">端點回傳的原始 JSON（陣列，或含陣列的物件包裝——兩種都試）</param>
    /// <returns>
    /// <c>Sources</c>：解析出的主機；<c>RawEntryCount</c>：回應裡看到的條目數
    /// （供 CoverageNote 說「目錄共 N 條、可解析 M 台」）；
    /// <c>Usable</c>：**至少解析出一台合法主機**——false 代表格式與預期不符，
    /// 呼叫端應退回事件掃描並警告，**不可**當成「這台 Sentinel 沒有主機」。
    /// </returns>
    public static EsmParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EsmParseResult.Unusable(0);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 連 JSON 都不是（HTML 錯誤頁、純文字訊息等）——明確不可用
            return EsmParseResult.Unusable(0);
        }

        var entries = FindEntries(root);
        var sources = new List<EsmEventSource>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var ip = FirstMatching(entry, IpFieldCandidates, IsValidIpv4);
            if (ip == null) continue;   // 沒有可辨識的 IP 就不是我們認得的主機條目

            // 名稱可缺（退回 IP）——名稱只是顯示用，IP 才是身分
            var name = FirstMatching(entry, NameFieldCandidates, v => !string.IsNullOrWhiteSpace(v));
            sources.Add(new EsmEventSource(name ?? ip, ip));
        }

        var deduped = sources
            .GroupBy(s => s.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new EsmParseResult(deduped, entries.Count, Usable: deduped.Count > 0);
    }

    /// <summary>
    /// 從回應裡找出「條目陣列」。ESM 端點可能直接回陣列，也可能包在某個屬性底下
    /// （Sentinel 的其他端點兩種都見過）——兩種都試，取第一個**內含物件**的陣列。
    /// </summary>
    private static List<JsonElement> FindEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();

        if (root.ValueKind != JsonValueKind.Object) return new List<JsonElement>();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;

            var items = property.Value.EnumerateArray().ToList();
            if (items.Any(i => i.ValueKind == JsonValueKind.Object)) return items;
        }

        return new List<JsonElement>();
    }

    /// <summary>
    /// 依候選欄位名依序尋找第一個通過驗證的值。**大小寫不敏感**：候選清單已經是猜的，
    /// 再讓大小寫成為第二個猜錯的機會沒有意義。
    /// </summary>
    private static string? FirstMatching(JsonElement entry, string[] candidates, Func<string, bool> isValid)
    {
        foreach (var candidate in candidates)
        {
            foreach (var property in entry.EnumerateObject())
            {
                if (!string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var value = property.Value.GetString();
                if (value != null && isValid(value)) return value.Trim();
            }
        }
        return null;
    }

    /// <summary>完整四段 IPv4。嚴格驗證是刻意的——這是「這條目是不是一台主機」的唯一判準，
    /// 寬鬆一點就會把版本號、序號之類的字串當成 IP 收進來。</summary>
    private static bool IsValidIpv4(string value)
    {
        var octets = value.Trim().Split('.');
        return octets.Length == 4 &&
               octets.All(o => o.Length > 0 && int.TryParse(o, out var v) && v is >= 0 and <= 255);
    }
}

/// <param name="Sources">解析出的主機（依 IP 去重）</param>
/// <param name="RawEntryCount">回應裡的條目數，供誠實申報「目錄共 N 條、可解析 M 台」</param>
/// <param name="Usable">至少解析出一台——false 代表**格式不符**，不是「沒有主機」</param>
public sealed record EsmParseResult(IReadOnlyList<EsmEventSource> Sources, int RawEntryCount, bool Usable)
{
    public static EsmParseResult Unusable(int rawEntryCount) =>
        new(Array.Empty<EsmEventSource>(), rawEntryCount, Usable: false);
}
