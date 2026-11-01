namespace LogForesight.Web.Services;

/// <summary>
/// 案件逐日同步背景服務的節拍判定（純邏輯，無 I/O、無時間依賴）。
///
/// 服務迴圈每 5 秒一拍，每拍問這裡「這拍要不要執行」：
/// 上一次有處理到資料 → 下一拍就再執行；沒處理到（閒置）→ 依序等 1、2、4、6 拍（5／10／20／30 秒），
/// 之後維持 6 拍（上限 30 秒）；資料版本與上次執行完成時記下的不同（有人寫了資料）→ 這拍立即執行並重設等待。
/// 記下的版本值必須是**執行完成後**的值：服務自己處理完會 Bump 一次版本，記執行前的值會讓它自我喚醒、退化成 5 秒輪詢。
/// 非執行緒安全：只由服務迴圈單一執行緒呼叫。
/// </summary>
public sealed class CaseDaySyncSchedule
{
    /// <summary>閒置時依序需要等待的拍數；用完後維持最後一個</summary>
    private static readonly int[] IdleWaitBeats = { 1, 2, 4, 6 };

    private long _beat;
    private long _nextRunBeat;
    private long _lastVersion;
    private bool _hasRun;
    private int _idleStep;

    /// <summary>每拍呼叫一次；回傳 true 表示這拍要執行。<paramref name="currentVersion"/> 為目前的 DataVersionStamp.Current。</summary>
    public bool ShouldRun(long currentVersion)
    {
        _beat++;

        // 第一拍一律執行（尚無任何基準）
        if (!_hasRun) return true;

        // 資料版本變動：立即執行，並把閒置退避重設
        if (currentVersion != _lastVersion)
        {
            _idleStep = 0;
            return true;
        }

        return _beat >= _nextRunBeat;
    }

    /// <summary>執行完成後呼叫：<paramref name="processedAny"/> 是否處理到資料；<paramref name="versionAfterRun"/> 為執行（含服務自己 Bump）完成後的版本值。</summary>
    public void OnRunCompleted(bool processedAny, long versionAfterRun)
    {
        _hasRun = true;
        _lastVersion = versionAfterRun;

        if (processedAny)
        {
            _idleStep = 0;
            _nextRunBeat = _beat + 1;
            return;
        }

        var wait = IdleWaitBeats[Math.Min(_idleStep, IdleWaitBeats.Length - 1)];
        _idleStep++;
        _nextRunBeat = _beat + wait;
    }
}
