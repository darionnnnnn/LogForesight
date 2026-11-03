using System.Text.RegularExpressions;
using LogForesight.Core.Models;

namespace LogForesight.Core.Analysis;

/// <summary>
/// 一筆分析紀錄餵給 AI 的輸入特徵（第 63 輪批次 F-4）：用來比較「格式異常的主機日」與「正常的主機日」
/// 輸入有什麼不同。只算次數與長度，**不含任何原文**，所以不需要去識別化。
///
/// 文字範圍（也就是 prompt 裡會出現的自由文字）：<see cref="LogIssueSignature.SampleMessages"/> 的每則範例訊息、
/// 每個問題的 <see cref="LogIssueSignature.Source"/>、<see cref="DailyAnalysisRecord.TrendAlerts"/>、
/// <see cref="DailyAnalysisRecord.CorrelationAlerts"/>、<see cref="DailyAnalysisRecord.UncoveredChecks"/>，
/// 每一項視為一「則」。
/// </summary>
public sealed record AiInputFeatures
{
    /// <summary>問題數（TopIssues 筆數）</summary>
    public int IssueCount { get; init; }
    /// <summary>規則命中數（有 RuleId 的問題）</summary>
    public int RuleHitCount { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int AuditCount { get; init; }
    public int TrendAlertCount { get; init; }
    public int CorrelationAlertCount { get; init; }
    public bool DataIncomplete { get; init; }

    /// <summary>文字範圍內所有項目的字元總數</summary>
    public int InputChars { get; init; }
    /// <summary>最長單則的字元數；沒有任何文字為 0</summary>
    public int LongestItemChars { get; init; }

    /// <summary>雙引號的出現次數</summary>
    public int QuoteCount { get; init; }
    /// <summary>反斜線的出現次數</summary>
    public int BackslashCount { get; init; }
    /// <summary>左大括號的出現次數</summary>
    public int OpenBraceCount { get; init; }
    /// <summary>左中括號的出現次數</summary>
    public int OpenBracketCount { get; init; }
    /// <summary>小於符號的出現次數</summary>
    public int LessThanCount { get; init; }
    /// <summary>控制字元（不含 \r \n \t）</summary>
    public int ControlCharCount { get; init; }
    /// <summary>非 BMP 字元（需要代理對的字元，如 emoji）的個數，一個字元算一次</summary>
    public int NonBmpCharCount { get; init; }
    /// <summary>路徑樣式片段（磁碟機路徑、UNC、至少兩段的 Unix 絕對路徑）</summary>
    public int PathLikeCount { get; init; }
    /// <summary>JSON 樣式片段：「"鍵":」形狀</summary>
    public int JsonLikeCount { get; init; }
    /// <summary>XML／HTML 樣式片段：開頭或結尾標籤形狀</summary>
    public int XmlLikeCount { get; init; }

    /// <summary>非 ASCII 字元占總字元的比例（0～1）；總字元數為 0 時為 null（沒有分母，不是 0）</summary>
    public double? NonAsciiRatio { get; init; }

    // 路徑：磁碟機代號、UNC、至少兩段的 Unix 絕對路徑（前面不能是字母數字、冒號、斜線，避開 URL 與時間）
    private static readonly Regex PathPattern = new(
        @"[A-Za-z]:\\|\\\\[^\\\s]+\\|(?<![\w:/.])/[\w.\-]+(?:/[\w.\-]+)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonPattern = new(
        "\"[^\"\\r\\n]{1,64}\"\\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XmlPattern = new(
        @"</?[A-Za-z][^<>\r\n]{0,200}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>從一筆（完整、非輕量）分析紀錄算出輸入特徵。純函式，不改動紀錄。</summary>
    public static AiInputFeatures From(DailyAnalysisRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var items = new List<string>();
        foreach (var issue in record.TopIssues)
        {
            if (!string.IsNullOrEmpty(issue.Source)) items.Add(issue.Source);
            if (issue.SampleMessages != null)
                items.AddRange(issue.SampleMessages.Where(m => !string.IsNullOrEmpty(m)));
        }
        items.AddRange(record.TrendAlerts.Where(s => !string.IsNullOrEmpty(s)));
        items.AddRange(record.CorrelationAlerts.Where(s => !string.IsNullOrEmpty(s)));
        items.AddRange(record.UncoveredChecks.Where(s => !string.IsNullOrEmpty(s)));

        int total = 0, longest = 0, quote = 0, backslash = 0, brace = 0, bracket = 0, lessThan = 0,
            control = 0, nonBmp = 0, nonAscii = 0, path = 0, json = 0, xml = 0;

        foreach (var text in items)
        {
            total += text.Length;
            longest = Math.Max(longest, text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"': quote++; break;
                    case '\\': backslash++; break;
                    case '{': brace++; break;
                    case '[': bracket++; break;
                    case '<': lessThan++; break;
                }
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') control++;
                if (c > 127) nonAscii++;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) nonBmp++;
            }

            path += PathPattern.Matches(text).Count;
            json += JsonPattern.Matches(text).Count;
            xml += XmlPattern.Matches(text).Count;
        }

        return new AiInputFeatures
        {
            IssueCount = record.TopIssues.Count,
            RuleHitCount = record.TopIssues.Count(i => !string.IsNullOrEmpty(i.RuleId)),
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditCount = record.AuditEventCount,
            TrendAlertCount = record.TrendAlerts.Count,
            CorrelationAlertCount = record.CorrelationAlerts.Count,
            DataIncomplete = record.DataIncomplete,
            InputChars = total,
            LongestItemChars = longest,
            QuoteCount = quote,
            BackslashCount = backslash,
            OpenBraceCount = brace,
            OpenBracketCount = bracket,
            LessThanCount = lessThan,
            ControlCharCount = control,
            NonBmpCharCount = nonBmp,
            PathLikeCount = path,
            JsonLikeCount = json,
            XmlLikeCount = xml,
            NonAsciiRatio = total == 0 ? null : (double)nonAscii / total
        };
    }
}
