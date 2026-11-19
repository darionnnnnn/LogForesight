namespace LogForesight.Core.Analysis;

/// <summary>
/// 小模型（實測環境：Gemma 4 26B、context 20480）context 預算的保守估算與截斷防線。
/// 目的不是精確計算 token 數，而是在客戶端擋住「明顯會爆 context」的情況——
/// 不能依賴 server 端爆 context 時的行為（可能靜默截頭、可能報錯），必須自己先擋。
/// 估算刻意抓保守：CJK 字元約 1:1（實際通常 <1 token/字，抓 1:1 是往「高估佔用」的方向保守），
/// 其餘（多半是 ASCII）約 3.5 字元 1 token。
/// </summary>
public static class PromptBudget
{
    /// <summary>context 總長度，依實際 AI 環境設定（appsettings.json 若換模型，此常數應一併確認）</summary>
    public const int ContextWindowTokens = 20480;

    /// <summary>保留 10% 安全餘裕後，prompt＋輸出合計可用的 token 數</summary>
    public static readonly int UsableTokens = (int)(ContextWindowTokens * 0.9);

    /// <summary>
    /// 保守估算：漢字、日文假名、全形標點各以 1 token 計（全形標點與漢字同樣是一字一格，不能當半形算）；
    /// 擴充區漢字（CJK Extension B 以後，UTF-16 代理對）罕用字常拆成多個 token，一個字以 2 計。
    /// 其餘（多半是 ASCII）約 3.5 字元 1 token。不追求精確計數，只求不低估。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int cjk = 0;
        int other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = CjkWeight(rune.Value);
            if (w > 0) cjk += w;
            else other += rune.Utf16SequenceLength;
        }

        return cjk + (int)Math.Ceiling(other / 3.5);
    }

    /// <summary>該碼點的 token 估算權重；0＝不是 CJK 類字元（走 ASCII 比率）。</summary>
    private static int CjkWeight(int cp)
    {
        if (cp >= 0x20000 && cp <= 0x323AF) return 2;   // 擴充區 B～H（代理對）
        if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0xF900 && cp <= 0xFAFF)) return 1;
        if (cp >= 0x2E80 && cp <= 0x2FDF) return 1;     // 部首補充、康熙部首
        if (cp >= 0x3000 && cp <= 0x30FF) return 1;     // CJK 標點（、。「」…）與日文假名
        if (cp >= 0xFE30 && cp <= 0xFE4F) return 1;     // CJK 相容形式（直排標點）
        if (cp >= 0xFF00 && cp <= 0xFFEF) return 1;     // 全形與半形形式（，；：？！（）等）
        if (cp == 0x2014 || cp == 0x2026 || (cp >= 0x2018 && cp <= 0x201D)) return 1;   // — … ‘’“”
        return 0;
    }

    /// <summary>
    /// prompt 加上預期輸出上限是否會超出可用預算。呼叫端應在超出時套用該呼叫類型的截斷策略
    /// （深入分析：從原始 log 區尾端截斷；週體檢/總覽：本來就已在組裝時做輸入塑形，這裡只是最後一道防線）。
    /// </summary>
    public static bool ExceedsBudget(string prompt, int maxOutputTokens, out int estimatedPromptTokens)
    {
        estimatedPromptTokens = EstimateTokens(prompt);
        return estimatedPromptTokens + maxOutputTokens > UsableTokens;
    }
}
