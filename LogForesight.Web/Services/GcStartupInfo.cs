using System.Globalization;

namespace LogForesight.Web.Services;

/// <summary>啟動 log 用：把 GC 設定的「實際生效值」寫成一行，確認 runtimeconfig／環境變數有沒有真的生效。</summary>
internal static class GcStartupInfo
{
    internal const string Unset = "未設定";

    internal static string DescribeGcStartup(IReadOnlyDictionary<string, object> gcConfig, long? hardLimitBytes)
    {
        var hardLimit = hardLimitBytes is { } bytes
            ? $"{(long)Math.Round(bytes / 1048576.0, MidpointRounding.AwayFromZero)} MB"
            : Unset;
        // hardLimitBytes 是 GC 可用的記憶體總量：沒設 HeapHardLimitPercent 時是實體記憶體，不能標成硬上限
        var limitLabel = gcConfig.ContainsKey("GCHeapHardLimitPercent") ? "GCHeapHardLimit" : "GC 可用記憶體（未設硬上限）";
        return $"GC 設定生效值：ServerGC={Read(gcConfig, "ServerGC")}、GCConserveMem={Read(gcConfig, "GCConserveMem")}、"
               + $"GCHeapHardLimitPercent={Read(gcConfig, "GCHeapHardLimitPercent")}、{limitLabel}={hardLimit}";
    }

    private static string Read(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var raw) || raw == null) return Unset;
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? Unset;
    }
}
