namespace LogForesight.Web.Services;

/// <summary>
/// 操作說明書 AI 問答的選節計分（docs/archive/FEEDBACK-15-PLAN.md 批次E-3）：對 question 做關鍵字
/// 比對計分（title 命中 ×3、keywords ×2、內文 ×1），取最高分節＋其 related 節，依
/// <see cref="PromptBudget.EstimateTokens"/> 累計到約 12K token 上限即停止加節。
///
/// 純靜態、無外部依賴——刻意設計成這樣才能在單元測試裡直接餵合成的 <see cref="HelpChapter"/>
/// 清單驗證計分邏輯，不需要真的載入內嵌資源或打 AI。
/// </summary>
public static class HelpChapterScorer
{
    private const int TitleWeight = 3;
    private const int KeywordWeight = 2;
    private const int ContentWeight = 1;

    /// <summary>CJK 判定沿用 PromptBudget 的範圍（含常用漢字），bigram 切詞用</summary>
    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

    /// <summary>
    /// 中文以雙字元 bigram 切詞（"風險等級" → "風險"／"險等"／"等級"），英文／數字以連續字母數字
    /// 為一個詞、標點與空白斷詞。單一 CJK 字元（bigram 湊不出兩個字）仍收一個單字元 token，
    /// 避免一個字的關鍵字（罕見但不該直接漏接）永遠比對不到。
    /// </summary>
    internal static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var cjkRun = new List<char>();
        var wordRun = new System.Text.StringBuilder();

        void FlushCjk()
        {
            if (cjkRun.Count == 1)
            {
                tokens.Add(cjkRun[0].ToString());
            }
            else
            {
                for (var i = 0; i + 1 < cjkRun.Count; i++)
                {
                    tokens.Add(new string(new[] { cjkRun[i], cjkRun[i + 1] }));
                }
            }
            cjkRun.Clear();
        }

        void FlushWord()
        {
            if (wordRun.Length > 0) tokens.Add(wordRun.ToString());
            wordRun.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushWord();
                cjkRun.Add(ch);
            }
            else if (char.IsLetterOrDigit(ch))
            {
                FlushCjk();
                wordRun.Append(ch);
            }
            else
            {
                FlushCjk();
                FlushWord();
            }
        }
        FlushCjk();
        FlushWord();
        return tokens;
    }

    private static int Score(HashSet<string> questionTokens, HelpChapter chapter)
    {
        var titleTokens = Tokenize(chapter.Title);
        var keywordTokens = new HashSet<string>(chapter.Keywords.SelectMany(Tokenize), StringComparer.OrdinalIgnoreCase);
        var contentTokens = Tokenize(chapter.Content);

        return questionTokens.Count(t => titleTokens.Contains(t)) * TitleWeight
             + questionTokens.Count(t => keywordTokens.Contains(t)) * KeywordWeight
             + questionTokens.Count(t => contentTokens.Contains(t)) * ContentWeight;
    }

    /// <summary>
    /// 選節：取分數最高的一節＋其 manifest 的 related 節（依 related 清單順序，找不到對應
    /// 章節的 id 略過），再依 <paramref name="maxTokenBudget"/> 依序累計、超出即停止加節
    /// （已加入的節不會因為後面超標被移除，只是不再加更多）。完全比對不到任何章節時回傳空清單，
    /// 由呼叫端決定要不要仍呼叫 AI（見 HelpQaService：仍會呼叫，讓 AI 依系統提示誠實回答
    /// 「說明書未涵蓋」，而不是在這裡就用寫死的訊息取代 AI 的判斷）。
    /// </summary>
    public static List<HelpChapter> SelectChapters(string question, IReadOnlyList<HelpChapter> chapters, int maxTokenBudget)
    {
        var questionTokens = Tokenize(question);
        if (questionTokens.Count == 0 || chapters.Count == 0) return new List<HelpChapter>();

        var best = chapters
            .Select(c => (Chapter: c, Score: Score(questionTokens, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Chapter == null) return new List<HelpChapter>();

        var chapterById = chapters.ToDictionary(c => c.Id);
        var orderedIds = new List<string> { best.Chapter.Id };
        foreach (var relatedId in best.Chapter.Related)
        {
            if (chapterById.ContainsKey(relatedId) && !orderedIds.Contains(relatedId))
                orderedIds.Add(relatedId);
        }

        var result = new List<HelpChapter>();
        var usedTokens = 0;
        foreach (var id in orderedIds)
        {
            var chapter = chapterById[id];
            // 預算以「實際會塞進 prompt 的那一份」估算（回饋二十七輪作業 G）：AI 版通常比
            // 使用者版長，拿簡明版估會低估、超出預算
            var chapterTokens = PromptBudget.EstimateTokens(chapter.ContentForAi);
            if (result.Count > 0 && usedTokens + chapterTokens > maxTokenBudget) break;
            result.Add(chapter);
            usedTokens += chapterTokens;
        }
        return result;
    }
}
