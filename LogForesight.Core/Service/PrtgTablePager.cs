using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>
/// 分頁未收斂：翻到頁數上限仍沒有任何停止條件成立。
/// 這是 PRTG 端行為異常（忽略位移、代理快取同一頁）的訊號，必須看得見——
/// 靜默截斷會讓鏡像少一大塊而畫面顯示同步成功。
/// </summary>
internal sealed class PrtgPagingNotConvergedException : Exception
{
    public PrtgPagingNotConvergedException(string content, int pages, int readRows, int duplicateRows)
        : base($"{content} 分頁未收斂：已翻 {pages} 頁、讀取 {readRows} 筆" +
               (duplicateRows > 0 ? $"（其中重複 {duplicateRows} 筆）" : "") + "仍未到結尾。" +
               "PRTG 端可能忽略了 start 位移，或前方代理反覆回傳同一頁。")
    {
        Content = content;
        Pages = pages;
        ReadRows = readRows;
        DuplicateRows = duplicateRows;
    }

    public string Content { get; }
    public int Pages { get; }
    public int ReadRows { get; }
    public int DuplicateRows { get; }
}

/// <summary>分頁結果統計。ReadRows 含重複列，Mapped 是實際交給 onBatch 的筆數。</summary>
internal sealed record PrtgPagerResult(int Mapped, int ReadRows, int DuplicateRows, int Pages);

/// <summary>
/// PRTG table.json 的唯一分頁實作。所有需要翻頁讀取 PRTG 表格的路徑都走這裡，
/// 避免同一套停止條件在多處各寫一份而只修到一半。
/// </summary>
internal static class PrtgTablePager
{
    internal const int DefaultPageSize = 500;

    /// <summary>
    /// treesize 未知時的頁數上限（＝20 萬筆）。treesize 已知時取「推算值與它的較大者」——
    /// treesize 在帶 filter 的查詢下是否為過濾後筆數並無保證，讓它只能放大上限、不能縮小，
    /// 否則會把合法的長同步誤判成未收斂。
    /// </summary>
    internal const int DefaultMaxPages = 400;

    /// <summary>每翻這麼多頁寫一行執行輸出：大型環境要翻數百頁，沒有輸出時「慢」與「卡死」看起來一樣。</summary>
    private const int ConsoleEveryPages = 50;

    /// <summary>
    /// 逐頁讀取並轉換，每累積滿一頁大小就呼叫 onBatch 寫出（絕不把整份資料堆在記憶體）。
    ///
    /// 停止條件三道，任一成立即停：
    /// (a) 回應不是預期的陣列，或空頁；
    /// (b) 本頁筆數少於頁大小（＝最後一頁）；
    /// (c) 本頁沒有任何沒見過的列（依 <paramref name="rowKey"/> 判定）。
    /// (c) 是真正的收斂條件：實機的 PRTG 在 start 超出範圍時會夾到最後一頁而非回空頁，
    /// 總筆數剛好是頁大小整數倍時只靠 (a)(b) 永遠停不下來。
    ///
    /// <paramref name="rowKey"/> 必須是該 content 真正的唯一鍵，預設是 objid。
    /// **messages 的 objid 是「發出訊息的 sensor」而不是訊息自己的 id**，同一天同一顆 sensor
    /// 會有很多列——拿 objid 當鍵會把第二筆以後全部丟掉，還會在整頁都是同一顆 sensor 時
    /// 誤判成結尾。那條路徑要傳「objid + 時間」，與 lf_prtg_state_changes 的去重鍵一致。
    /// 鍵取不到（null）的列一律當成沒見過：判不了重寧可重複寫入（寫入是冪等 upsert），
    /// 不能靜默丟資料。
    ///
    /// 查詢一律帶 sortby=objid：分頁的前提除了「遵守 start」還有「兩次查詢之間順序一致」，
    /// 順序不穩定時會靜默漏列（去重擋得住重複、擋不住漏列）。不支援的版本會忽略這個參數。
    /// </summary>
    /// <exception cref="PrtgPagingNotConvergedException">翻到頁數上限仍未收斂。</exception>
    internal static async Task<PrtgPagerResult> FetchAsync<T>(
        PrtgClient client,
        IRunConsole console,
        string content,
        string columns,
        string? extraQuery,
        Func<JsonElement, T?> mapper,
        Action<IReadOnlyList<T>> onBatch,
        CancellationToken ct,
        string? phase = null,
        Action<string, int, int>? progress = null,
        string? stageLabel = null,
        int pageSize = DefaultPageSize,
        int maxPagesWhenTreeSizeUnknown = DefaultMaxPages,
        Func<JsonElement, string?>? rowKey = null)
    {
        rowKey ??= DefaultRowKey;

        var offset = 0;
        var totalMapped = 0;
        var readRows = 0;
        var duplicateRows = 0;
        var pageIndex = 0;
        var treeSize = 0;
        var maxPages = maxPagesWhenTreeSizeUnknown;
        var seenKeys = new HashSet<string>();
        var buffer = new List<T>(pageSize);

        // 分母尚未知（要等第一次回應的 treesize），先送 0 讓進度軌顯示不定進度
        if (phase != null) progress?.Invoke(phase, 0, 0);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var query = $"api/table.json?content={content}&columns={columns}&start={offset}&count={pageSize}&sortby=objid";
            if (!string.IsNullOrEmpty(extraQuery)) query += $"&{extraQuery}";

            var json = await client.GetJsonAsync(query, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(content, out var arrayProp) ||
                arrayProp.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            // treesize＝PRTG 回報的該 content 總筆數，當進度分母。只在第一次取得時記錄
            // （分頁過程中的值可能隨資料異動而變，分母來回跳動比沒有分母更難讀）。
            // 沒有這個欄位時維持 0，進度軌顯示不定進度但仍有分子。
            if (treeSize == 0)
            {
                var parsedTreeSize = GetLongProperty(root, "treesize");
                if (parsedTreeSize is > 0 and <= int.MaxValue)
                {
                    treeSize = (int)parsedTreeSize.Value;
                    // 同步期間資料可能增加，推算值留兩頁餘裕
                    var estimated = (treeSize + pageSize - 1) / pageSize + 2;
                    maxPages = Math.Max(estimated, maxPagesWhenTreeSizeUnknown);
                }
            }

            var countInPage = 0;
            var newInPage = 0;
            foreach (var item in arrayProp.EnumerateArray())
            {
                countInPage++;
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                // 去重在轉換之前：夾到末頁時整頁都是讀過的資料，重複寫入雖被 upsert 吸收，
                // 但會讓寫入數虛報，也白跑一趟資料庫。
                var key = rowKey(item);
                if (key != null && !seenKeys.Add(key))
                {
                    duplicateRows++;
                    continue;
                }
                newInPage++;

                var mapped = mapper(item);
                if (mapped != null)
                {
                    buffer.Add(mapped);
                    if (buffer.Count >= pageSize)
                    {
                        totalMapped += buffer.Count;
                        onBatch(buffer);
                        buffer.Clear();
                    }
                }
            }

            pageIndex++;
            readRows += countInPage;

            // 分子用「已讀取的列數」而非已寫入數：寫入是每滿一頁才發生一次，
            // 用寫入數當分子會讓進度以頁為單位跳動、且最後一批寫入前看起來停滯。
            // 分子夾住分母：夾到末頁時會多讀一整頁，不夾的話進度會顯示 1500/1000
            var reported = treeSize > 0 ? Math.Min(readRows, treeSize) : readRows;
            if (phase != null) progress?.Invoke(phase, reported, treeSize);

            if (stageLabel != null && pageIndex % ConsoleEveryPages == 0)
            {
                var scope = treeSize > 0 ? $" / 約 {treeSize} 筆" : string.Empty;
                console.WriteLine($"  [{stageLabel}] 已翻 {pageIndex} 頁、累計讀取 {readRows} 筆{scope}...");
            }

            if (countInPage == 0) break;
            if (countInPage < pageSize) break;
            if (newInPage == 0) break;

            // 上限允許多讀一頁再判定：總筆數剛好是「上限 × 頁大小」時，最後一頁是滿頁，
            // 下一頁才是空頁——在這裡就擲例外會把合法的收尾判成未收斂。
            if (pageIndex > maxPages)
            {
                // 已寫出的資料留著（呼叫端的寫入是冪等 upsert），但這一階段必須報失敗。
                if (buffer.Count > 0)
                {
                    totalMapped += buffer.Count;
                    onBatch(buffer);
                    buffer.Clear();
                }
                throw new PrtgPagingNotConvergedException(content, pageIndex, readRows, duplicateRows);
            }

            offset += countInPage;
        }

        if (buffer.Count > 0)
        {
            totalMapped += buffer.Count;
            onBatch(buffer);
            buffer.Clear();
        }

        return new PrtgPagerResult(totalMapped, readRows, duplicateRows, pageIndex);
    }

    /// <summary>預設列鍵：objid。devices 與 sensors 的 objid 是主鍵，這對它們成立。</summary>
    private static string? DefaultRowKey(JsonElement el) => GetLongProperty(el, "objid")?.ToString();

    private static long? GetLongProperty(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }
}
