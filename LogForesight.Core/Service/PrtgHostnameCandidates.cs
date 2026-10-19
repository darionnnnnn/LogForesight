using System.Text.RegularExpressions;

namespace LogForesight.Core.Service;

public static class PrtgHostnameCandidates
{
    private static readonly Regex BracketRegex = new(@"\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex PrefixRegex = new(@"^[A-Za-z]-(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// 正規化成短主機名：trim、去掉開頭 scheme（如 "http://"）、去掉 "/" 之後的路徑、去掉 ":port"、取第一個 "." 之前、轉小寫；
    /// 結果為空字串或本身是 IP（PrtgHostMapper.NormalizeIp(值) != null）時回 null。
    /// </summary>
    public static string? ShortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var s = value.Trim();

        // 輸入本身是 IP 則回 null
        if (PrtgHostMapper.NormalizeIp(s) != null) return null;

        // 去掉開頭 scheme（如 "http://"）
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            s = s[(schemeIdx + 3)..];
        }

        // 去掉 "/" 之後的路徑
        var slashIdx = s.IndexOf('/');
        if (slashIdx >= 0)
        {
            s = s[..slashIdx];
        }

        // 去掉 ":port"
        var colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            s = s[..colonIdx];
        }

        // 去掉 port 後若本身是 IP 則回 null
        if (PrtgHostMapper.NormalizeIp(s) != null) return null;

        // 取第一個 "." 之前
        var dotIdx = s.IndexOf('.');
        if (dotIdx >= 0)
        {
            s = s[..dotIdx];
        }

        // 轉小寫
        s = s.Trim().ToLowerInvariant();

        // 結果為空字串或本身是 IP 時回 null
        if (s.Length == 0 || PrtgHostMapper.NormalizeIp(s) != null)
        {
            return null;
        }

        return s;
    }

    /// <summary>
    /// 依序產生候選短名（已 ShortName 正規化、去重、保持順序）：
    /// (a) 裝置名稱中第一對「內容非空白」的半形括號 () 內字串；名稱裡完全沒有括號時用整個名稱；有括號但全是空括號時沒有 (a)。
    /// (b) (a) 若以「單一英文字母＋'-'」開頭（regex ^[A-Za-z]-(.+)$），去掉那兩個字元後的字串；兩個以上字母開頭（如 "VM-"）不去。
    /// (c) hostField 非空且不是 IP（PrtgHostMapper.NormalizeIp(hostField) == null）時的 hostField。
    /// </summary>
    public static IReadOnlyList<string> Extract(string? deviceName, string? hostField)
    {
        var list = new List<string>();

        void AddCandidate(string? raw)
        {
            var shortName = ShortName(raw);
            if (shortName != null && !list.Contains(shortName))
            {
                list.Add(shortName);
            }
        }

        string? candidateA = null;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var trimmed = deviceName.Trim();
            var hasBrackets = trimmed.Contains('(') || trimmed.Contains(')');
            if (!hasBrackets)
            {
                candidateA = trimmed;
            }
            else
            {
                foreach (Match match in BracketRegex.Matches(trimmed))
                {
                    var content = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        candidateA = content;
                        break;
                    }
                }
            }
        }

        if (candidateA != null)
        {
            var shortA = ShortName(candidateA);
            if (shortA != null)
            {
                AddCandidate(shortA);

                var matchB = PrefixRegex.Match(shortA);
                if (matchB.Success)
                {
                    AddCandidate(matchB.Groups[1].Value);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(hostField) && PrtgHostMapper.NormalizeIp(hostField) == null)
        {
            AddCandidate(hostField);
        }

        return list;
    }
}
