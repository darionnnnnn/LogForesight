using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 維護頁「診斷」分頁的背景執行入口（docs/WEB-SCHEDULER-PLAN.md §1.4.11）。
/// Singleton——內部只依賴同為 Singleton 的 store／<see cref="NetiqProbeRunState"/>，
/// 才能安全地在 <see cref="Task.Run(Func{Task})"/> 背景工作中使用，不受呼叫端請求範圍
/// （Scoped DbContext 等）生命週期影響。稽核寫入留在 Controller（<see cref="Auth.IAuditService"/>
/// 是 Scoped，無法注入 Singleton），與 <c>ScheduleController</c> 手動觸發排程分析的既有作法一致。
/// </summary>
public class NetiqProbeService
{
    private readonly ISentinelStore _sentinels;
    private readonly NetiqOptionsStore _netiqOptions;
    private readonly NetiqProbeRunState _state;

    public NetiqProbeService(ISentinelStore sentinels, NetiqOptionsStore netiqOptions, NetiqProbeRunState state)
    {
        _sentinels = sentinels;
        _netiqOptions = netiqOptions;
        _state = state;
    }

    public NetiqProbeStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        return new NetiqProbeStatusDto
        {
            IsRunning = s.IsRunning,
            SentinelId = s.SentinelId,
            SentinelName = s.SentinelName,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            Success = s.Success,
            LatestMessage = s.LatestMessage,
            Output = s.Output
        };
    }

    /// <summary>觸發一次 probe。回傳 false 時 <paramref name="sentinel"/> 可能為 null
    /// （找不到這台 Sentinel）或已被填入（其餘驗證失敗，如帳密未設定／已有 probe 在跑）</summary>
    public bool TryStart(long sentinelId, string? sampleIp, string? sampleLinuxIp, out Sentinel? sentinel, out string? validationError)
    {
        validationError = null;
        sentinel = _sentinels.Get(sentinelId);
        if (sentinel == null)
        {
            validationError = "找不到這台 Sentinel。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sentinel.Username) || string.IsNullOrWhiteSpace(sentinel.PasswordEnc))
        {
            validationError = "這台 Sentinel 尚未設定探索帳密，無法執行診斷。";
            return false;
        }

        if (!_state.TryBegin(sentinel.SentinelId, sentinel.Name))
        {
            validationError = "已有診斷正在執行中，請稍候再試。";
            return false;
        }

        var sentinelsToProbe = new List<Sentinel> { sentinel };
        var options = _netiqOptions.Get();
        var console = new WebProbeConsole(_state);

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                success = await NetiqProbeRunner.RunAsync(sentinelsToProbe, options, sampleIp, sampleLinuxIp, console);
            }
            catch (Exception ex)
            {
                console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}");
            }
            finally
            {
                _state.EndRun(success);
            }
        });

        return true;
    }
}
