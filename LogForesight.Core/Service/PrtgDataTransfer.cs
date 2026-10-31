namespace LogForesight.Core.Service;

/// <summary>PRTG 診斷包的格式版本與來源提示（匯出由 PrtgDiagnosticExportWriter 串流產生，舊單次匯入已移除）。</summary>
public static class PrtgDataTransfer
{
    public const int CurrentFormatVersion = 2;

    /// <summary>不保存 URL、query 或 userinfo；摘要只是換端點防混淆提示，不能當 Core 世代。</summary>
    public static string SourceHintFor(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return string.Empty;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("PRTG 位址格式錯誤。", nameof(sourceUrl));
        var location = $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(location)));
    }
}
