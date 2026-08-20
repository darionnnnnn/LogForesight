namespace LogForesight.Core.Service;

/// <summary>
/// 帳號顯示名稱格式化工具（公開純函式）。
/// 將 Active Directory 完整辨別名稱（DN）轉換為短名（CN 值），其餘格式保持原樣。
/// </summary>
public static class AccountDisplayFormatter
{
    /// <summary>
    /// 取得帳號顯示用短名。
    /// 規則：
    /// 1. null 或空白字串 → 回傳空字串。
    /// 2. DN 格式（如 CN=...,OU=...,DC=...） → 取第一個 CN 的值。
    ///    - 支援反斜線跳脫逗號（\,），不於該處斷開；跳脫用的反斜線本身不進顯示值。
    /// 3. 其他形狀（DOMAIN\name、name@domain.com、純短名、SID、無 CN 的 DN） → 原樣返回。
    /// </summary>
    public static string ToShortName(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return string.Empty;

        var trimmed = account.Trim();
        var cnIndex = FindFirstCnIndex(trimmed);
        if (cnIndex < 0)
            return trimmed;

        var value = new System.Text.StringBuilder();
        var isEscaped = false;

        for (var i = cnIndex + 3; i < trimmed.Length; i++)   // 跳過 "CN="
        {
            var c = trimmed[i];
            if (isEscaped)
            {
                // 跳脫的字元照原樣收下，反斜線本身不收——它是 DN 的語法，不是名字的一部分
                value.Append(c);
                isEscaped = false;
            }
            else if (c == '\\')
            {
                isEscaped = true;
            }
            else if (c is ',' or ';')
            {
                break;
            }
            else
            {
                value.Append(c);
            }
        }

        return value.ToString().Trim();
    }

    private static int FindFirstCnIndex(string s)
    {
        var pos = 0;
        while (pos < s.Length)
        {
            var idx = s.IndexOf("CN=", pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            // 必須在開頭，或者前一個非空白字元為逗號或分號
            if (idx == 0) return 0;

            var prevIdx = idx - 1;
            while (prevIdx >= 0 && char.IsWhiteSpace(s[prevIdx]))
            {
                prevIdx--;
            }

            if (prevIdx >= 0 && (s[prevIdx] == ',' || s[prevIdx] == ';'))
            {
                return idx;
            }

            pos = idx + 3;
        }

        return -1;
    }
}
