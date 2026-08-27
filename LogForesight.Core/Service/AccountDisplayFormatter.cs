using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>單一使用者名稱顯示規則：已編譯的樣式與取代文字。</summary>
public sealed record AccountDisplayRule(Regex Regex, string Replacement)
{
    /// <summary>原始樣式文字，僅供錯誤訊息辨識是哪一條規則。</summary>
    public string Pattern => Regex.ToString();
}

/// <summary>
/// 使用者名稱顯示規則集合（不可變）。設定文字解析一次後由整批查詢共用，
/// 不要每列重新解析——正則表達式的編譯成本不低。
/// </summary>
public sealed class AccountDisplayRuleSet
{
    public static readonly AccountDisplayRuleSet Empty = new(Array.Empty<AccountDisplayRule>());

    public IReadOnlyList<AccountDisplayRule> Rules { get; }

    public AccountDisplayRuleSet(IReadOnlyList<AccountDisplayRule> rules) => Rules = rules;
}

/// <summary>
/// 帳號顯示名稱格式化工具（公開純函式）。
/// 將 Active Directory 完整辨別名稱（DN）轉換為短名（CN 值），其餘格式保持原樣。
/// 支援套用管理員自訂的正則取代規則。
/// </summary>
public static class AccountDisplayFormatter
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>每次正則比對的逾時保護（100 毫秒），避免災難性回溯鎖死執行緒</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
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

        // CN 有名無值（「CN=,OU=x」）時退回原值：短名化是為了好讀，不是把帳號變不見
        var shortName = value.ToString().Trim();
        return shortName.Length > 0 ? shortName : trimmed;
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

    /// <summary>
    /// 解析多行規則文字為可執行的規則集合。
    /// 規則格式：一行一條「樣式 => 取代文字」，以 # 開頭為註解，空白行略過。
    /// 樣式為 .NET 正則表達式，依序套用。
    /// 單一規則若無法編譯時略過並記錄警告，不讓整個解析失敗。
    /// </summary>
    public static AccountDisplayRuleSet ParseRules(string? rulesText)
    {
        if (string.IsNullOrWhiteSpace(rulesText))
            return AccountDisplayRuleSet.Empty;

        var rules = new List<AccountDisplayRule>();
        var lines = rulesText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var arrowIdx = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrowIdx < 0)
            {
                Log.Warn("使用者名稱顯示規則第 {0} 行缺少「=>」分隔符號，已略過：{1}", i + 1, line);
                continue;
            }

            var pattern = line[..arrowIdx].Trim();
            var replacement = line[(arrowIdx + 2)..].Trim();

            if (pattern.Length == 0)
            {
                Log.Warn("使用者名稱顯示規則第 {0} 行樣式為空，已略過：{1}", i + 1, line);
                continue;
            }

            try
            {
                rules.Add(new AccountDisplayRule(new Regex(pattern, RegexOptions.None, RegexTimeout), replacement));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "使用者名稱顯示規則第 {0} 行樣式「{1}」無法編譯，已略過：{2}", i + 1, pattern, ex.Message);
            }
        }

        return rules.Count == 0 ? AccountDisplayRuleSet.Empty : new AccountDisplayRuleSet(rules);
    }

    /// <summary>
    /// 驗證多行規則文字，若有語法錯誤回傳 false 並提供繁體中文錯誤訊息（含行號），合法回傳 true。
    /// </summary>
    public static bool TryValidateRules(string? rulesText, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rulesText))
            return true;

        var lines = rulesText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var lineNum = i + 1;
            var arrowIdx = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrowIdx < 0)
            {
                error = $"第 {lineNum} 行規則格式錯誤：缺少「=>」分隔符號。";
                return false;
            }

            var pattern = line[..arrowIdx].Trim();
            if (pattern.Length == 0)
            {
                error = $"第 {lineNum} 行正則表達式樣式不可為空。";
                return false;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"第 {lineNum} 行正則表達式語法錯誤：{ex.Message}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 取得套用顯示規則後的帳號顯示名稱。
    /// 先進行既有短名化（DN 取 CN，其餘原樣），再依序套用規則清單。
    /// 無規則時輸出與 <see cref="ToShortName(string?)"/> 完全相同。
    /// </summary>
    public static string Format(string? account, AccountDisplayRuleSet rules)
    {
        var shortName = ToShortName(account);
        if (string.IsNullOrEmpty(shortName) || rules == null || rules.Rules.Count == 0)
            return shortName;

        var result = shortName;
        foreach (var rule in rules.Rules)
        {
            try
            {
                result = rule.Regex.Replace(result, rule.Replacement);
            }
            catch (RegexMatchTimeoutException ex)
            {
                Log.Warn(ex, "套用使用者名稱顯示規則樣式「{0}」逾時，已略過此規則。", rule.Pattern);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "套用使用者名稱顯示規則樣式「{0}」失敗，已略過此規則：{1}", rule.Pattern, ex.Message);
            }
        }

        return result;
    }
}
