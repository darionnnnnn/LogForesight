using NLog;
using NLog.Config;
using NLog.Targets;

namespace LogForesight.Tests;

/// <summary>
/// 測試用的記錄替身：掛一個 NLog 記憶體 target 抓指定型別的記錄（含例外原文），Dispose 時卸掉。
/// 用到的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>（全域設定同時只能一個測試改）。
/// </summary>
internal sealed class NLogCapture : IDisposable
{
    private readonly MemoryTarget _target;
    private readonly LoggingConfiguration _config;

    private NLogCapture(string loggerName)
    {
        _target = new MemoryTarget("lf-capture-" + Guid.NewGuid().ToString("N"))
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        _config = LogManager.Configuration ??= new LoggingConfiguration();
        _config.AddTarget(_target);
        _config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, _target, loggerName);
        LogManager.ReconfigExistingLoggers();
    }

    public static NLogCapture For(Type loggerOwner) => new(loggerOwner.FullName!);

    public IReadOnlyList<string> Logs => _target.Logs.ToList();

    public void Dispose()
    {
        _config.RemoveTarget(_target.Name);
        _config.LoggingRules.Where(r => r.Targets.Contains(_target)).ToList().ForEach(r => _config.LoggingRules.Remove(r));
        LogManager.ReconfigExistingLoggers();
    }
}
