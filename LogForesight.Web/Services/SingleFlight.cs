using System.Collections.Concurrent;

namespace LogForesight.Web.Services;

/// <summary>
/// 同鍵並發只算一次：第一個進來的請求計算，同時進來的同鍵請求等它的結果（含例外）；算完就移除，
/// 下一次呼叫重新計算（結果通常已由呼叫端寫進快取）。
/// <para>為什麼需要：跨請求快取的計算函式在鎖外執行，未命中時每個並發請求各算一整份；
/// 結果大到進不了快取（超過元素上限）時更是每個請求都算——3000 台下儀表板一份可能數百 MB，
/// 幾個人同時開就疊加到主機記憶體吃滿。合併後同一時間同一個鍵最多一份在算。</para>
/// <para>每個快取持有自己的實例，鍵不跨快取共用；鍵仍由各快取組（含可見主機集合與版本戳），這裡不做任何授權判斷。</para>
/// </summary>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<object>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>目前正在計算中的鍵數（測試用）。</summary>
    internal int InFlightCount => _inFlight.Count;

    public T Run<T>(string key, Func<T> compute) where T : class
    {
        var flight = _inFlight.GetOrAdd(key,
            _ => new Lazy<object>(() => compute(), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return (T)flight.Value;
        }
        finally
        {
            // 只移除自己等的這一份：之後才進來的同鍵請求若已建了新的一份，不能被這裡刪掉
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<object>>(key, flight));
        }
    }
}
