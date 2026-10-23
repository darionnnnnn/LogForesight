using System.Text.RegularExpressions;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書使用者版的寫作規範守門（docs/HELP-WRITING-GUIDE.md §3–§5）：讀者是非 IT 人員，
/// 且畫面渲染器 markdown-lite 只支援單層結構。只檢查機器判得出來的部分；用詞與正確性仍靠人工比對。
/// AI 版（ContentForAi）不受這些限制。
/// </summary>
public class HelpWritingGuideTests
{
    private const int MaxSentenceLength = 45;

    // 沿革字眼：現行說明只寫現在的樣子（§3）。「過去 N 天」這類時間說法不算，所以不列「過去」。
    private static readonly string[] HistoryWords = { "原本", "後來", "已不存在", "本版", "已改為", "已改由" };

    // 實作名詞：API 路徑與權限代碼不進使用者版（§3）
    private static readonly Regex Implementation = new(@"/api/|\bCapability\b|\bMaintain\b|\bViewAll\b|\bDevMonitor\b|\bConfirmPermission\b|\bViewAudit\b");

    private static IEnumerable<(string Id, int Line, string Text)> UserLines()
    {
        foreach (var chapter in new HelpContentService().Chapters.Where(c => c.Type == "markdown"))
        {
            var lines = chapter.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                yield return (chapter.Id, i + 1, lines[i]);
        }
    }

    [Fact]
    public void 使用者版只用markdownlite支援的單層結構()
    {
        var violations = UserLines()
            .Where(l => l.Text.StartsWith("###") || l.Text.StartsWith('>') || Regex.IsMatch(l.Text, @"^\s+([-*]|\d+\.)\s"))
            .Select(l => $"{l.Id}:{l.Line} {l.Text}")
            .ToList();

        Assert.True(violations.Count == 0, "不支援的結構（### 標題、引用、巢狀清單）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 使用者版不寫沿革字眼與實作名詞()
    {
        var violations = UserLines()
            .Where(l => HistoryWords.Any(w => l.Text.Contains(w, StringComparison.Ordinal)) || Implementation.IsMatch(l.Text))
            .Select(l => $"{l.Id}:{l.Line} {l.Text}")
            .ToList();

        Assert.True(violations.Count == 0, "沿革字眼或實作名詞：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 使用者版每句不超過45字_表格除外()
    {
        var violations = new List<string>();
        foreach (var (id, line, text) in UserLines().Where(l => !l.Text.TrimStart().StartsWith('|')))
        {
            var body = Regex.Replace(text, @"^\s*(\d+\.|-|#+)\s*", "");
            foreach (var sentence in Regex.Split(body, "[。！？]"))
            {
                // 行內代碼與粗體記號不算字數：它們是要使用者照打的字或排版記號
                var length = Regex.Replace(sentence, @"`[^`]*`|\*\*", "").Trim().Length;
                if (length > MaxSentenceLength) violations.Add($"{id}:{line} ({length} 字) {sentence.Trim()}");
            }
        }

        Assert.True(violations.Count == 0, $"超過 {MaxSentenceLength} 字的句子：\n" + string.Join("\n", violations));
    }
}
