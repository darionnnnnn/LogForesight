namespace LogForesight.Core.Analysis;

/// <summary>第一次嘗試的結果類別（見 <see cref="AiGrammarMemory.ReportFirstAttempt"/>）。</summary>
public enum FirstAttemptOutcome
{
    /// <summary>空回覆（診斷類別為 empty 或 no_json，即 <c>AIService.RetryWithoutGrammar</c> 為真的那兩類）</summary>
    Empty,

    /// <summary>解析並通過內容檢查</summary>
    Success,

    /// <summary>其他失敗（內容檢查沒過、截斷、型別不符…）</summary>
    OtherFailure
}

/// <summary>
/// 強制格式（<c>response_format</c>）的啟用狀態跨呼叫記住（第 70 輪 C-1）。
/// 地端模型在帶強制格式時第一次常回空陣列，<c>ChatJsonAsync</c> 第二次關掉強制格式才救回；
/// 每次呼叫各自重撞一次，多打約六成 AI 呼叫。這裡記住「某端點＋模型＋回覆型別」連續撞到空回覆的次數，
/// 達門檻後該鍵的第一次嘗試就不帶強制格式，並每隔固定次數探測一次、探測成功即恢復。
///
/// 純邏輯、不做網路、不落地；一把鎖保護全部狀態。鍵由呼叫端組成（端點＋模型＋型別名稱），
/// 換模型或換端點自然是另一個鍵，不需要任何重置邏輯。
/// </summary>
public sealed class AiGrammarMemory
{
    /// <summary>連續幾次「帶強制格式、第一次嘗試空回覆」後停用強制格式（暫定）</summary>
    public const int DisableThreshold = 3;

    /// <summary>停用後每第幾次呼叫帶強制格式探測一次（暫定）</summary>
    public const int ProbeInterval = 20;

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    private sealed class State
    {
        public int ConsecutiveEmpty;
        public bool Disabled;
        public int CallsWhileDisabled;
    }

    /// <summary>組鍵：端點＋模型＋回覆型別名稱。</summary>
    public static string BuildKey(string endpoint, string? model, string responseType) =>
        $"{endpoint}\u0001{model}\u0001{responseType}";

    /// <summary>
    /// 這次呼叫的第一次嘗試要不要帶強制格式。預設 true；該鍵停用時回 false，
    /// 但每第 <see cref="ProbeInterval"/> 次呼叫回 true 一次當探測。
    /// </summary>
    public bool UseGrammarFirst(string key)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(key, out var state) || !state.Disabled) return true;
            state.CallsWhileDisabled++;
            if (state.CallsWhileDisabled % ProbeInterval != 0) return false;
            return true;
        }
    }

    /// <summary>
    /// 回報第一次嘗試的結果（傳輸層失敗不回報）。
    /// 帶強制格式且空回覆 → 連續計數＋1，達 <see cref="DisableThreshold"/> 即停用；
    /// 帶強制格式且非空回覆 → 計數歸零、恢復啟用（探測成功即恢復）；
    /// 不帶強制格式且非成功 → 恢復啟用、計數歸零（不帶反而失敗就回到預設）；
    /// 不帶強制格式且成功 → 不變。
    /// </summary>
    public void ReportFirstAttempt(string key, bool usedGrammar, FirstAttemptOutcome outcome)
    {
        lock (_lock)
        {
            if (usedGrammar)
            {
                if (outcome == FirstAttemptOutcome.Empty)
                {
                    var state = GetOrAdd(key);
                    state.ConsecutiveEmpty++;
                    if (state.ConsecutiveEmpty >= DisableThreshold && !state.Disabled)
                    {
                        state.Disabled = true;
                        state.CallsWhileDisabled = 0;
                    }
                }
                else if (_states.TryGetValue(key, out var state))
                {
                    Reset(state);
                }
            }
            else if (outcome != FirstAttemptOutcome.Success && _states.TryGetValue(key, out var state))
            {
                Reset(state);
            }
        }
    }

    /// <summary>該鍵目前是否處於停用（僅供診斷與測試，不影響呼叫計數）。</summary>
    public bool IsDisabled(string key)
    {
        lock (_lock) return _states.TryGetValue(key, out var state) && state.Disabled;
    }

    private State GetOrAdd(string key)
    {
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new State();
        return state;
    }

    private static void Reset(State state)
    {
        state.ConsecutiveEmpty = 0;
        state.Disabled = false;
        state.CallsWhileDisabled = 0;
    }
}
