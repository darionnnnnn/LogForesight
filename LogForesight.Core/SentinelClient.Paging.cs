using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogForesight.Core;

/// <summary>SentinelClient 的查詢結果分頁取回與 best-effort JSON 解析。</summary>
public sealed partial class SentinelClient
{
    private async Task<List<SentinelEvent>> FetchAllPagesAsync(JobStatus status, SentinelSearchRequest request, CancellationToken ct)
    {
        var events = new List<SentinelEvent>();
        if (status.ResultsHref == null || status.Avail == 0)
            return events;

        var pageSize = request.PageSize ?? _settings.PageSize;
        var targetCount = Math.Min(status.Found, request.MaxResults ?? _settings.MaxResultsPerJob);
        var page = 1;

        while (events.Count < targetCount)
        {
            await ThrottleAsync(ct);
            var href = SetPageQueryParam(status.ResultsHref, page);
            var pageEvents = await FetchPageAsync(href, ct);
            if (pageEvents.Count == 0) break; // 沒有更多資料，即使數字對不上也停止，避免無窮迴圈

            events.AddRange(pageEvents);
            if (request.PageObserver?.Invoke(pageEvents) == false) break;
            if (pageEvents.Count < pageSize) break; // 這頁不足一頁筆數，視為最後一頁

            page++;
        }

        return events;
    }

    /// <summary>把 results 連結的 <c>page=</c> 參數改成指定頁碼。避免依賴未文件化的「下一頁連結」
    /// 慣例（docs/BACKLOG.md 未決事項）——已知第一頁 href 帶 <c>page=1</c>，往後頁碼自己算。</summary>
    internal static string SetPageQueryParam(string href, int page)
    {
        if (Regex.IsMatch(href, @"([?&])page=\d+"))
            return Regex.Replace(href, @"([?&])page=\d+", $"$1page={page}");

        var separator = href.Contains('?') ? "&" : "?";
        return $"{href}{separator}page={page}";
    }

    private async Task<List<SentinelEvent>> FetchPageAsync(string href, CancellationToken ct)
    {
        var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, href), ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp);
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」讀取查詢結果失敗：HTTP {(int)resp.StatusCode}｜{Truncate(body)}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseEventsPage(json);
        }
    }

    private static readonly string[] CandidateArrayKeys =
        { "items", "event", "events", "results", "data", "records", "rows", "entry", "entries" };

    /// <summary>
    /// **best-effort 通用解析**：官方文件未提供結果頁的確切 JSON 結構範例
    /// （docs/BACKLOG.md 未決事項）。依序嘗試常見形狀：純陣列、或物件中常見鍵名的陣列屬性，
    /// 找不到時保底取第一個陣列型別的屬性。解析失敗回傳空清單而非擲例外——呼叫端
    /// （尤其 NetIQ 診斷分頁的 <see cref="NetiqProbeRunner"/>）應另外印出原始 body 供人工核對
    /// 真實結構，不能讓「格式猜錯」讓整條查詢鏈斷裂。真實環境的 probe 輸出定案欄位對應後，
    /// 這裡再依實測結果收斂。
    /// </summary>
    internal static List<SentinelEvent> ParseEventsPage(string json)
    {
        var events = new List<SentinelEvent>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return events;
        }

        using (doc)
        {
            var array = FindEventArray(doc.RootElement);
            if (array == null) return events;

            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.EnumerateObject())
                {
                    fields[prop.Name] = ElementToString(prop.Value);
                }
                events.Add(new SentinelEvent(fields));
            }
        }
        return events;
    }

    private static JsonElement? FindEventArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in CandidateArrayKeys)
        {
            if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Array)
                return val;
        }
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array) return prop.Value;
        }
        return null;
    }

    private static string ElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.GetRawText() // 物件／陣列型別的欄位值原樣保留 JSON 文字，不遺失資訊
    };
}
