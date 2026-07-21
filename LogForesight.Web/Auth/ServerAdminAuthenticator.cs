using System.Collections.Concurrent;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Auth;

/// <summary>serverAdmin 登入嘗試的結果</summary>
public enum ServerAdminLoginResult
{
    /// <summary>此帳號不是 serverAdmin，應交給一般流程處理</summary>
    NotServerAdmin,

    Success,

    WrongPassword,

    /// <summary>因連續失敗而鎖定中</summary>
    LockedOut
}

/// <summary>
/// serverAdmin 本地救援帳號的驗證與鎖定（docs/WEB-SPEC.md §6.2）。
///
/// 為什麼**只有這個帳號**有 Web 端鎖定：一般 AD 帳號的鎖定交由網域的帳戶鎖定原則
/// （一套原則、一個事實來源）；但 serverAdmin 是設定檔裡的本地帳號，AD 原則管不到它，
/// 而它又是最有價值的目標（能指派 admin）。沒有自帶鎖定的話，它就是整套系統唯一
/// 可以無限次嘗試的入口——一個專門偵測暴力破解的系統不該自己留這種洞。
///
/// 鎖定狀態存在記憶體：重啟站台會清空。可接受——重啟需要伺服器權限，
/// 能重啟站台的人本來就有更直接的手段，不是攻擊者繞過鎖定的實際路徑。
/// </summary>
public class ServerAdminAuthenticator
{
    private readonly ServerAdminSettings _settings;
    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);

    public ServerAdminAuthenticator(WebAppSettings settings)
    {
        _settings = settings.Auth.ServerAdmin;
    }

    public string Account => _settings.Account;

    public bool IsServerAdmin(string account) =>
        !string.IsNullOrWhiteSpace(_settings.Account) &&
        string.Equals(account, _settings.Account, StringComparison.OrdinalIgnoreCase);

    /// <summary>目前是否鎖定中（供登入頁顯示剩餘時間）</summary>
    public TimeSpan? LockedUntil(string account)
    {
        if (!_failures.TryGetValue(account, out var state)) return null;
        if (state.LockedUntil == null || state.LockedUntil <= DateTime.UtcNow) return null;
        return state.LockedUntil.Value - DateTime.UtcNow;
    }

    public ServerAdminLoginResult TryLogin(string account, string? password)
    {
        if (!IsServerAdmin(account)) return ServerAdminLoginResult.NotServerAdmin;

        if (LockedUntil(account) != null) return ServerAdminLoginResult.LockedOut;

        if (!string.IsNullOrEmpty(password) && PasswordHasher.Verify(password, _settings.PasswordHash))
        {
            _failures.TryRemove(account, out _);
            return ServerAdminLoginResult.Success;
        }

        RecordFailure(account);
        // 這次失敗剛好觸發鎖定時回報 LockedOut，讓使用者知道「不是再試一次就好」
        return LockedUntil(account) != null ? ServerAdminLoginResult.LockedOut : ServerAdminLoginResult.WrongPassword;
    }

    private void RecordFailure(string account)
    {
        _failures.AddOrUpdate(
            account,
            _ => new FailureState { Count = 1 },
            (_, state) =>
            {
                state.Count++;
                if (state.Count >= _settings.MaxFailedAttempts)
                {
                    state.LockedUntil = DateTime.UtcNow.AddMinutes(_settings.LockoutMinutes);
                    state.Count = 0;   // 鎖定期滿後重新計數
                }
                return state;
            });
    }

    private class FailureState
    {
        public int Count { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}
