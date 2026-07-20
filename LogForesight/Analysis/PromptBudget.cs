namespace LogForesight;

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

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int cjk = text.Count(IsCjk);
        int other = text.Length - cjk;
        return cjk + (int)Math.Ceiling(other / 3.5);
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

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
