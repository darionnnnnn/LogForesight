using LogForesight.Core.Persistence;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// 使用者顯示名稱規則服務介面。
/// 統一於出口處套用管理員設定的使用者名稱顯示規則（AccountDisplayRules）。
/// </summary>
public interface IUserDisplayNameService
{
    /// <summary>讀取當前設定的 AccountDisplayRules 並套用規則後回傳；displayName 為 null 或空字串時回傳空字串。</summary>
    string Of(string? displayName);

    /// <summary>
    /// 先對 displayName 套用規則，再組成「顯示名稱(帳號)」（半形括號）；
    /// 套用規則後為空時，只回傳帳號。
    /// </summary>
    string WithAccount(string? displayName, string account);

    /// <summary>回傳本次解析出的規則集，供呼叫端一次解析、迴圈內重複使用。</summary>
    AccountDisplayRuleSet CurrentRules();
}

/// <summary>
/// <see cref="IUserDisplayNameService"/> 的實作。
/// 以設定文字為鍵進行記憶化，避免每筆資料重複解析正則表達式。
/// </summary>
public class UserDisplayNameService : IUserDisplayNameService
{
    private readonly ISystemSettingsStore _settingsStore;
    private readonly object _ruleLock = new();
    private string? _cachedRulesText;
    private AccountDisplayRuleSet? _cachedRuleSet;

    public UserDisplayNameService(ISystemSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public AccountDisplayRuleSet CurrentRules()
    {
        var currentText = _settingsStore.Get()?.AccountDisplayRules ?? string.Empty;
        lock (_ruleLock)
        {
            if (_cachedRuleSet != null && _cachedRulesText == currentText)
            {
                return _cachedRuleSet;
            }

            var parsed = AccountDisplayFormatter.ParseRules(currentText);
            _cachedRulesText = currentText;
            _cachedRuleSet = parsed;
            return parsed;
        }
    }

    public string Of(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return string.Empty;

        return AccountDisplayFormatter.Format(displayName, CurrentRules());
    }

    public string WithAccount(string? displayName, string account)
    {
        var formatted = Of(displayName);
        return string.IsNullOrEmpty(formatted) ? account : $"{formatted}({account})";
    }
}
