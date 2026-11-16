namespace LogForesight.Tests.Scenarios;

// ── 情境描述（C# 建構，不另造檔案格式）──────────────────────────────────────
// 一個 Scenario＝主機、使用者、系統設定、逐日事件、逐日人為動作、預期與已知差異。
// 天數一律是「第 N 天」（1 起）；真實日期只在驅動器內部以錨點換算，輸出不外洩。

/// <summary>事件等級：對應 Sentinel 欄位（sev／xdasoutcome）的反向映射</summary>
public enum ScenarioEventLevel
{
    Information,
    Warning,
    Error,
    /// <summary>Security 頻道的失敗稽核（如 4625）</summary>
    FailureAudit,
    /// <summary>Security 頻道的成功稽核（如 4624）</summary>
    SuccessAudit
}

/// <summary>一筆合成事件。<see cref="At"/> 是當天的當地時間（00:00 起算）。</summary>
public sealed record ScenarioEvent(
    TimeSpan At,
    string LogName,
    string Source,
    int EventId,
    ScenarioEventLevel Level,
    string Message)
{
    /// <summary>Linux 事件的 program（syslog sp 欄）；null 時視為 Windows 事件</summary>
    public string? LinuxProgram { get; init; }
}

/// <summary>主機描述。IP 若未指定，由驅動器依登錄順序配發（10.76.0.n），保證同情境跑兩次相同。</summary>
public sealed class HostSpec
{
    public HostSpec(string name) => Name = name;

    public string Name { get; }

    /// <summary>true＝Linux；預設 Windows</summary>
    public bool IsLinux { get; init; }

    /// <summary>所屬主機群組名稱（可多個；不存在的群組由驅動器自動建立）</summary>
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    /// <summary>主機負責人帳號（可選；帳號必須也在 Users 內）</summary>
    public IReadOnlyList<string> Owners { get; init; } = Array.Empty<string>();

    /// <summary>Sentinel 來源名稱；預設 S1</summary>
    public string Sentinel { get; init; } = "S1";

    public string? Ip { get; init; }
}

/// <summary>
/// 使用者描述。角色以「群組角色」表達：驅動器為每位使用者建一個專屬使用者群組
/// （名稱 ug-帳號），群組的 Role／派工池旗標／主機群組授權都在這個群組上。
/// </summary>
public sealed class UserSpec
{
    public UserSpec(string account, LogForesight.Core.Models.UserRole role = LogForesight.Core.Models.UserRole.User)
    {
        Account = account;
        Role = role;
    }

    public string Account { get; }

    public LogForesight.Core.Models.UserRole Role { get; }

    /// <summary>此使用者的群組可看的主機群組名稱（群組授權；Manager／Dev／Admin 本來就看得到全部）</summary>
    public IReadOnlyList<string> AuthorizedHostGroups { get; init; } = Array.Empty<string>();

    /// <summary>所屬群組是否為派工池</summary>
    public bool InDispatchPool { get; init; }

    /// <summary>暫停接單</summary>
    public bool DispatchPaused { get; init; }

    /// <summary>有沒有 email（沒有 email 的人收不到通知信，outbox 會標 NoEmail）</summary>
    public bool HasEmail { get; init; } = true;

    /// <summary>false＝一開始就停用（登入會失敗，其後的請求以匿名處理）</summary>
    public bool Active { get; init; } = true;
}

/// <summary>系統設定。其餘一律預設值；<see cref="Customize"/> 供情境額外調整任何 SystemSettings 欄位。</summary>
public sealed class ScenarioSettings
{
    public bool AutoDispatchEnabled { get; init; } = true;

    /// <summary>true＝開交辦通知（MailEnabled 與 MailNotifyWorkOrders），這樣通知佇列才有東西可觀察；
    /// 驅動器不會寄任何信（寄送由站台的背景服務負責，測試站台已移除全部背景服務）。</summary>
    public bool WorkOrderMailQueue { get; init; } = true;

    public Action<LogForesight.Core.Models.SystemSettings>? Customize { get; init; }
}

/// <summary>某一天的人為動作與事件</summary>
public sealed class DayPlan
{
    internal List<(string Host, IReadOnlyList<ScenarioEvent> Events)> EventSets { get; } = new();
    internal List<ScenarioAction> ActionList { get; } = new();

    public int Day { get; }

    public DayPlan(int day) => Day = day;

    /// <summary>當天某台主機的事件（可呼叫多次，同主機會合併）</summary>
    public DayPlan Events(string host, IEnumerable<ScenarioEvent> events)
    {
        EventSets.Add((host, events.ToList()));
        return this;
    }

    /// <summary>當天的人為動作，依宣告順序執行（在當天分析與派工之後）</summary>
    public DayPlan Act(params ScenarioAction[] actions)
    {
        ActionList.AddRange(actions);
        return this;
    }
}

/// <summary>
/// 一個完整情境。建構完直接丟給 <see cref="ScenarioDriver"/>。
/// 天數預設取「事件／動作／預期出現過的最大天」，也可用 <see cref="Days"/> 明確指定（例如尾端要多跑幾天無事件的夜晚）。
/// </summary>
public sealed class Scenario
{
    private readonly SortedDictionary<int, DayPlan> _days = new();

    public Scenario(string name) => Name = name;

    public string Name { get; }

    public ScenarioSettings Settings { get; private set; } = new();

    public List<HostSpec> Hosts { get; } = new();

    public List<UserSpec> Users { get; } = new();

    /// <summary>沒有任何主機或使用者引用、但動作會用到的主機群組（例如「把主機改到新群組」的目標群組）</summary>
    public List<string> ExtraHostGroups { get; } = new();

    public List<ScenarioExpectation> Expectations { get; } = new();

    public List<KnownDifference> KnownDifferences { get; } = new();

    public int? ExplicitDays { get; private set; }

    public IReadOnlyDictionary<int, DayPlan> Plans => _days;

    public Scenario WithSettings(ScenarioSettings settings)
    {
        Settings = settings;
        return this;
    }

    public Scenario WithHost(HostSpec host)
    {
        Hosts.Add(host);
        return this;
    }

    public Scenario WithHostGroup(string name)
    {
        ExtraHostGroups.Add(name);
        return this;
    }

    public Scenario WithUser(UserSpec user)
    {
        Users.Add(user);
        return this;
    }

    /// <summary>取得（必要時建立）某一天的計畫</summary>
    public DayPlan Day(int day)
    {
        if (day < 1) throw new ArgumentOutOfRangeException(nameof(day), "天數從 1 起算");
        if (!_days.TryGetValue(day, out var plan)) _days[day] = plan = new DayPlan(day);
        return plan;
    }

    /// <summary>流暢寫法：<c>scenario.Day(2, d =&gt; d.Act(...))</c></summary>
    public Scenario Day(int day, Action<DayPlan> configure)
    {
        configure(Day(day));
        return this;
    }

    public Scenario Days(int totalDays)
    {
        ExplicitDays = totalDays;
        return this;
    }

    public Scenario Expect(ScenarioExpectation expectation)
    {
        Expectations.Add(expectation);
        return this;
    }

    /// <summary>登記一項已知差異：<paramref name="expectationKey"/> 這項預期目前與實際不一致，原因記在編號對應的評估記錄。</summary>
    public Scenario KnownDifference(string recordId, string expectationKey)
    {
        KnownDifferences.Add(new KnownDifference(recordId, expectationKey));
        return this;
    }

    /// <summary>實際要跑的天數</summary>
    public int TotalDays
    {
        get
        {
            var max = Math.Max(_days.Count == 0 ? 0 : _days.Keys.Max(), Expectations.Count == 0 ? 0 : Expectations.Max(e => e.Day));
            return Math.Max(ExplicitDays ?? 0, max);
        }
    }
}
