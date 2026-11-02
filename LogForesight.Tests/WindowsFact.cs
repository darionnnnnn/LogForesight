using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 平台判定與略過原因的唯一點。非 Windows 時，真的呼叫 Windows 專屬 API（ACL、Event Log、AD…）的測試略過，
/// 其餘（只是 new 物件、走純邏輯或注入 isWindows 的分支）照常執行。
/// </summary>
public static class WindowsPlatform
{
    public const string SkipText = "需要 Windows 平台（此測試會呼叫 Windows 專屬 API）";

    /// <summary>非 Windows 回傳略過原因，Windows 回傳 null；<paramref name="isWindows"/> 可注入以便測試。</summary>
    internal static string? SkipReason(bool isWindows) => isWindows ? null : SkipText;
}

/// <summary>只在 Windows 執行的 <see cref="FactAttribute"/>，非 Windows 時 <c>Skip</c> 附原因。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        Skip = WindowsPlatform.SkipReason(OperatingSystem.IsWindows());
    }
}

/// <summary>只在 Windows 執行的 <see cref="TheoryAttribute"/>，非 Windows 時 <c>Skip</c> 附原因。</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        Skip = WindowsPlatform.SkipReason(OperatingSystem.IsWindows());
    }
}
