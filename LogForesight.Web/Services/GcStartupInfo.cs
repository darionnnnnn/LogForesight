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
        return $"GC 設定生效值：ServerGC={Read(gcConfig, "ServerGC")}、GCConserveMem={Read(gcConfig, "GCConserveMem")}、"
               + $"GCHeapHardLimitPercent={Read(gcConfig, "GCHeapHardLimitPercent")}、GCHeapHardLimit={hardLimit}";
    }

    private static string Read(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var raw) || raw == null) return Unset;
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? Unset;
    }
}
