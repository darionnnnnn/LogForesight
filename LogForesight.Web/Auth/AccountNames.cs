namespace LogForesight.Web.Auth;

/// <summary>登入帳號正規化的單一出口：Trim 並去掉第一個反斜線之前的網域前綴（DOMAIN\user → user）。</summary>
public static class AccountNames
{
    public static string Canonical(string? account)
    {
        var s = (account ?? string.Empty).Trim();
        var i = s.IndexOf('\\');
        return i >= 0 ? s[(i + 1)..].Trim() : s;
    }
}
