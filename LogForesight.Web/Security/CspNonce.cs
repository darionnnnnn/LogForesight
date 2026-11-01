using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace LogForesight.Web.Security;

/// <summary>內容安全政策（CSP）的送出模式（<c>Server:Csp</c>）</summary>
public enum CspMode
{
    /// <summary>不送任何 CSP 標頭</summary>
    Off,
    /// <summary>送 <c>Content-Security-Policy-Report-Only</c>：只回報不攔截</summary>
    ReportOnly,
    /// <summary>送 <c>Content-Security-Policy</c>：違規即攔截</summary>
    Enforce
}

/// <summary>每請求一個的 CSP nonce：16 bytes 隨機、base64，存在 <see cref="HttpContext.Items"/>，同一請求多次讀取回同值</summary>
public static class CspNonce
{
    private const string ItemKey = "LogForesight.CspNonce";

    public static string GetCspNonce(this HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is string nonce) return nonce;
        nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[ItemKey] = nonce;
        return nonce;
    }
}
