using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 會替換或附掛 NLog 全域設定（<c>LogManager.Configuration</c>）的測試類別都標 <c>[Collection("NLogGlobalConfig")]</c>，
/// 並關閉與其他集合的並行：全域設定同一時間只能有一個測試在讀改寫，
/// 否則附掛的 target 會被並行測試的 ReconfigExistingLoggers 抹掉，造成偶發失敗。
/// 執行紀錄的常駐 target（RunLogCapture）不在此列：它只安裝一次、之後不隨執行增減，recorder 之間靠 scope 識別分派。
/// </summary>
[CollectionDefinition("NLogGlobalConfig", DisableParallelization = true)]
public sealed class NLogGlobalConfigCollection;
