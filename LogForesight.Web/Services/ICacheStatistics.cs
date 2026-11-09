using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// 跨請求快取回報自身大小與命中狀況的介面（健康頁記憶體詳情用）。
/// 實作者在自己的鎖內維護計數，<see cref="GetStatistics"/> 回傳一份一致的快照。
/// </summary>
public interface ICacheStatistics
{
    CacheStatisticsSnapshot GetStatistics();
}
