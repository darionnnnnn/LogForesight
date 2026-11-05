using System.Reflection;

namespace LogForesight.Core.Service;

/// <summary>
/// 這次建置的版本識別。<c>Directory.Build.props</c> 的版本號不隨 commit 變，只看它分不出站台部署了哪一版；
/// .NET SDK 在有 git 的環境建置時會把 commit 寫進 InformationalVersion（<c>1.0.53.2+完整sha</c>），這裡是唯一的解析點。
/// 建置環境沒有 git（例如從壓縮檔建置）時沒有 sha，<see cref="ShaKnown"/> 為 false。
/// </summary>
public sealed record BuildVersion(string Number, string? Sha)
{
    private const int ShortShaLength = 7;

    public static BuildVersion Current { get; } = Parse(
        typeof(BuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildVersion).Assembly.GetName().Version?.ToString());

    public bool ShaKnown => Sha != null;

    /// <summary>給人看、也寫進執行紀錄的版本字串：<c>1.0.53.2+abc1234</c>；沒有 sha 時只有版本號。</summary>
    public string Label => Sha == null ? Number : Number + "+" + Sha;

    public static BuildVersion Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return new BuildVersion("unknown", null);
        var plus = informational.IndexOf('+');
        if (plus < 0) return new BuildVersion(informational.Trim(), null);
        var number = informational[..plus].Trim();
        var sha = informational[(plus + 1)..].Trim();
        if (number.Length == 0) number = "unknown";
        if (sha.Length == 0 || !sha.All(Uri.IsHexDigit)) return new BuildVersion(number, null);
        return new BuildVersion(number, sha.Length > ShortShaLength ? sha[..ShortShaLength] : sha);
    }
}
