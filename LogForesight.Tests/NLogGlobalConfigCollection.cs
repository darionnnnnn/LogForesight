using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 會替換或附掛 NLog 全域設定（<c>LogManager.Configuration</c>）的測試類別都標 <c>[Collection("NLogGlobalConfig")]</c>，
/// 並關閉與其他集合的並行：全域設定同一時間只能有一個測試在讀改寫，
/// 否則附掛的 target 會被並行測試的 ReconfigExistingLoggers 抹掉，造成偶發失敗。
/// 正式碼的 BatchRunRecorder 另以自己的互斥鎖保護，那把鎖是 private，測試改用集合隔離。
/// </summary>
[CollectionDefinition("NLogGlobalConfig", DisableParallelization = true)]
public sealed class NLogGlobalConfigCollection;
