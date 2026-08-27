namespace LogForesight.Core.Analysis;

/// <summary>
/// AI token 用量計量的收集端（回饋二十七輪作業 B）。
///
/// 掛在 <see cref="AIService"/> 這個唯一的 HTTP 出口上，而不是各呼叫端：呼叫端有六處以上，
/// 且上層還有 <c>AiCacheStore</c>——在上層記會把「命中快取、沒真的呼叫」也算成消耗。
/// 記錄失敗不得影響 AI 呼叫本身（統計不是主線功能）。
/// </summary>
public interface IAiUsageMeter
{
    /// <param name="promptTokens">回應 usage.prompt_tokens；未回報時 0</param>
    /// <param name="completionTokens">回應 usage.completion_tokens；未回報時 0</param>
    /// <param name="totalTokens">回應 usage.total_tokens；未回報時 0</param>
    /// <param name="hasUsage">回應是否帶了 usage 欄位</param>
    void Record(int promptTokens, int completionTokens, int totalTokens, bool hasUsage);
}
