using System.Text.Json;
using LogForesight.Core.Analysis;
using System.Text.RegularExpressions;

namespace LogForesight.Core;

/// <summary>SentinelClient 的查詢結果分頁取回與 best-effort JSON 解析。</summary>
public sealed partial class SentinelClient
{
    /// <summary>
    /// 逐頁取回查詢結果。串流模式（<see cref="SentinelSearchRequest.StreamOnly"/>）下不累積事件，
    /// 只把每一頁交給 PageObserver 後隨即釋放——回傳的清單為空，取回筆數另外計數。
    /// </summary>
    private async Task<(List<SentinelEvent> Events, int Retrieved)> FetchAllPagesAsync(
        JobStatus status, SentinelSearchRequest request, CancellationToken ct)
    {
        if (request.StreamOnly && request.PageObserver == null)
        {
            throw new ArgumentException(
                "串流模式（StreamOnly）必須提供 PageObserver，否則取回的事件無處可去。", nameof(request));
        }

        var events = new List<SentinelEvent>();
        var retrieved = 0;
        if (status.ResultsHref == null || status.Avail == 0)
            return (events, retrieved);

        var pageSize = request.PageSize ?? _settings.PageSize;
        var targetCount = Math.Min(status.Found, request.MaxResults ?? _settings.MaxResultsPerJob);
        var page = 1;

        while (retrieved < targetCount)
        {
            await ThrottleAsync(ct);
            var href = SetPageQueryParam(status.ResultsHref, page);
            var pageEvents = await FetchPageAsync(href, request.RawFields, ct);
            if (pageEvents.Count == 0) break; // 沒有更多資料，即使數字對不上也停止，避免無窮迴圈

            retrieved += pageEvents.Count;
            if (!request.StreamOnly) events.AddRange(pageEvents);
            if (request.PageObserver?.Invoke(pageEvents) == false) break;
            if (pageEvents.Count < pageSize) break; // 這頁不足一頁筆數，視為最後一頁

            page++;
        }

        return (events, retrieved);
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

    private async Task<List<SentinelEvent>> FetchPageAsync(string href, bool rawFields, CancellationToken ct)
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
            // 只留分析與顯示會用到的欄位（批次E1）：回應可能帶回投影以外的屬性，
            // 全部入袋會讓單筆事件大小不受控。probe 診斷路徑（RawFields）不過濾，
            // 它要靠全欄位聯集發現未知欄位。
            return ParseEventsPage(json, rawFields ? null : SentinelFieldMap.ParseKeepFields);
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
    internal static List<SentinelEvent> ParseEventsPage(string json, IReadOnlySet<string>? keepFields)
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
                var fields = new Dictionary<string, string>(
                    keepFields?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.EnumerateObject())
                {
                    // keepFields 為 null＝不過濾（診斷用途要看真實回應的全部欄位）
                    if (keepFields != null && !keepFields.Contains(prop.Name)) continue;
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
