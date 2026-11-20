using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LogForesight.Core;

/// <summary>
/// PRTG 與 Sentinel 的伺服器憑證信任判定（第 79 輪批次 E1）。三種情況：
/// - 設了信任指紋（SHA-256，可有多個，任一相符即可——多台各自自簽的 Sentinel）：只接受指紋在清單內的憑證，
///   與憑證鏈、主機名稱無關——自簽憑證環境用這個取代「不驗證」；
/// - 沒設指紋但開了「忽略憑證錯誤」：一律接受（舊逃生門，健康頁會警示）；
/// - 兩者都沒有：照系統預設，憑證鏈與主機名稱都要正確。
///
/// 每次驗證都記下伺服器出示的指紋，供探測顯示（管理者核對後貼回設定）與失敗訊息指明原因。
/// 一個實例對應一個用戶端的連線處理常式，不跨用戶端共用。
/// </summary>
public sealed class ServerCertificateTrust
{
    private volatile string? _lastPresented;
    private volatile string? _lastRejection;

    public ServerCertificateTrust(string? pinnedSha256, bool ignoreErrors)
    {
        if (!TryNormalize(pinnedSha256, out var normalized))
            throw new ArgumentException("憑證指紋格式不正確，應為 64 個十六進位字元的 SHA-256。", nameof(pinnedSha256));
        PinnedSha256 = normalized;
        _pinned = normalized is null ? Array.Empty<string>() : normalized.Split(',');
        IgnoreErrors = ignoreErrors;
    }

    private readonly string[] _pinned;

    /// <summary>
    /// 正規化後的信任指紋（大寫十六進位、無分隔；多個以逗號分隔），沒設為 null。
    /// 呼叫端只用「有沒有設」判斷與存檔，比對一律走 <see cref="Validate"/>。
    /// </summary>
    public string? PinnedSha256 { get; }

    public bool IgnoreErrors { get; }

    /// <summary>最近一次連線時伺服器出示的憑證指紋（還沒連過為 null）</summary>
    public string? LastPresentedSha256 => _lastPresented;

    /// <summary>最近一次被拒的原因（中文、可直接顯示）；最近一次通過時為 null</summary>
    public string? LastRejection => _lastRejection;

    /// <summary>沒設指紋卻放行所有憑證：健康頁要列出的組合</summary>
    public static bool IsUnpinnedBypass(string? pinnedSha256, bool ignoreErrors)
        => ignoreErrors && string.IsNullOrWhiteSpace(pinnedSha256);

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var presented = certificate is null ? null : Sha256Of(certificate);
        _lastPresented = presented;

        string? rejection;
        if (PinnedSha256 is not null)
        {
            rejection = presented is not null && _pinned.Contains(presented, StringComparer.Ordinal)
                ? null
                : $"伺服器憑證的指紋與設定的信任指紋不符（伺服器出示 {presented ?? "（沒有憑證）"}）。" +
                  "憑證更新後指紋會改變，請核對後把新的指紋貼回設定。";
        }
        else if (IgnoreErrors)
        {
            rejection = null;
        }
        else
        {
            rejection = errors == SslPolicyErrors.None
                ? null
                : $"伺服器憑證不被信任（{Describe(errors)}；伺服器出示的指紋 {presented ?? "（沒有憑證）"}）。" +
                  "請把簽發的 CA 憑證安裝到這台主機，或在設定填入信任的憑證指紋。";
        }

        _lastRejection = rejection;
        return rejection is null;
    }

    /// <summary>憑證 DER 內容的 SHA-256，大寫十六進位</summary>
    public static string Sha256Of(X509Certificate certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>
    /// 接受常見的貼上格式：大小寫不拘，單一指紋內可含冒號、空白或連字號分隔（瀏覽器與 openssl 的顯示方式）。
    /// 多個指紋以逗號、分號或換行分隔，結果正規化為以逗號分隔的大寫十六進位（重複的只留一個）。
    /// 空白輸入＝沒設（回 true、結果 null）；任一項不是 64 個十六進位字元就整欄回 false，不靜默略過壞的那一項。
    /// </summary>
    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        var pins = new List<string>();
        foreach (var token in input.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var hex = new string(token.Where(c => c is not (':' or ' ' or '-' or '\t')).ToArray()).ToUpperInvariant();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return false;
            if (!pins.Contains(hex)) pins.Add(hex);
        }
        if (pins.Count == 0) return true;   // 只有分隔符號等同空白
        normalized = string.Join(',', pins);
        return true;
    }

    private static string Describe(SslPolicyErrors errors)
    {
        var parts = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) parts.Add("伺服器沒有出示憑證");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) parts.Add("憑證上的主機名稱與連線位址不符");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) parts.Add("憑證鏈無法驗證，可能是自簽或內部 CA");
        return string.Join("、", parts);
    }
}
