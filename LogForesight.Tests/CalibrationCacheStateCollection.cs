using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="LogForesight.Core.Service.CalibrationService"/> 的判定快取是行程層級的靜態欄位。
/// 所有會建立該服務的測試類別都要標 <c>[Collection("CalibrationCacheState")]</c>，
/// xUnit 才會把它們序列化執行——否則跨類別並行時會吃到別的類別建的假資料摘要，
/// 變成偶發紅綠且失敗訊息看不出原因。
/// </summary>
[CollectionDefinition("CalibrationCacheState")]
public sealed class CalibrationCacheStateCollection
{
}
