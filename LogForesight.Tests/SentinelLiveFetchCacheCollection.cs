using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="LogForesight.Web.Services.SentinelEventFetchService"/> 的即時查詢快取與併發旗標
/// 都是行程層級的靜態欄位。所有會呼叫 FetchAsync 的測試類別都要標
/// <c>[Collection("SentinelLiveFetchCacheState")]</c>，xUnit 才會把它們序列化執行——
/// 否則跨類別並行時，一邊寫入的條目會把另一邊的條目數斷言變成偶發紅綠
/// （同 <see cref="CalibrationCacheStateCollection"/> 的理由）。
/// </summary>
[CollectionDefinition("SentinelLiveFetchCacheState")]
public sealed class SentinelLiveFetchCacheCollection
{
}
