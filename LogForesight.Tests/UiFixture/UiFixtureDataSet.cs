using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>同一份資料的兩種 AI 設定：站台的「AI 已設定」判定走 <c>AiProviders.IsConfigured</c>。</summary>
public enum UiFixtureVariant
{
    /// <summary>AI 位址有值（指向不會回應的本機位址，截圖時不會真的呼叫模型）。</summary>
    AiConfigured,

    /// <summary>AI 位址為空：站台判定未設定。</summary>
    AiNotConfigured
}

/// <summary>一個可截圖的情境：鍵、說明與站台路徑（不含 PathBase）。</summary>
public sealed record UiFixtureScenario(string Key, string Title, string Path);

/// <summary>
/// UI 驗收用的多樣化資料集：經產品的 <see cref="StorageBackend"/> 與各 store 寫入一份檔案型 SQLite，
/// <b>不求量、求每種資料邊界都有一例</b>（長字串、空值、極端數量、各種狀態），
/// 用它啟動站台後截圖，檢查版面會不會被撐壞。
///
/// 與 <see cref="ScaleDataSet"/> 的分工：那邊求量、這邊求邊界；能走 store／分析紀錄寫入入口的都走產品路徑，
/// DTO 形狀才是真的。固定亂數種子；日期以「今天」為基準往前推（最近一天＝昨天，跨度不超過 30 天）。
/// </summary>
public sealed partial class UiFixtureDataSet : IDisposable
{
    // ── 對外資料 ────────────────────────────────────────────────────────────

    public string RootDir { get; }
    public string DbPath { get; }
    public UiFixtureVariant Variant { get; }
    public StorageBackend Backend { get; }

    /// <summary>主機鍵 → HostId（鍵見 <see cref="BuildHosts"/>）</summary>
    public Dictionary<string, long> HostIds { get; } = new();

    /// <summary>帳號 → UserId</summary>
    public Dictionary<string, long> UserIds { get; } = new();

    public Dictionary<string, long> HostGroupIds { get; } = new();
    public Dictionary<string, long> UserGroupIds { get; } = new();
    public Dictionary<string, long> WorkOrderIds { get; } = new();

    /// <summary>情境鍵 → 主機日（風險日詳情頁）</summary>
    public Dictionary<string, (long HostId, DateTime Date)> Days { get; } = new();

    public List<UiFixtureScenario> Scenarios { get; } = new();

    /// <summary>資料集的目標日期範圍：最舊一天到最新一天（昨天）</summary>
    public DateTime FirstDay => Yesterday.AddDays(-MaxDaysAgo + 1);
    public DateTime Yesterday { get; } = DateTime.Today.AddDays(-1);

    private const int MaxDaysAgo = 28;

    // 自訂規則 Id 在測試與產生器之間共用
    public const string RuleExplOneLine = "ui-fixture-expl-oneline";
    public const string RuleExplLong = "ui-fixture-expl-long";
    public const string RuleExplBlob = "ui-fixture-expl-blob";
    public const string RuleExplHostile = "ui-fixture-expl-hostile";
    public const string RuleExplMixed = "ui-fixture-expl-mixed";
    public const string RuleExplLongSource = "ui-fixture-expl-longsource";

    // ── 內部狀態 ────────────────────────────────────────────────────────────

    private readonly Random _rnd = new(20261110);
    private readonly EfAnalysisRecordStore _records;
    private readonly HostStore _hostStore;
    private readonly UserStore _userStore;
    private readonly UserGroupStore _userGroupStore;
    private readonly HostGroupStore _hostGroupStore;
    private readonly Dictionary<string, WebHost> _hosts = new();
    private readonly Dictionary<long, WebHost> _hostsById = new();

    private UiFixtureDataSet(string rootDir, UiFixtureVariant variant, StorageBackend backend)
    {
        RootDir = rootDir;
        Variant = variant;
        Backend = backend;
        DbPath = StorageBackend.DefaultSqlitePath(rootDir);
        _records = backend.RecordStore();
        _hostStore = new HostStore(backend.Blob("hosts"));
        _userStore = new UserStore(backend.Blob("users"), backend.Blob("user_last_login"));
        _userGroupStore = new UserGroupStore(backend.Blob("user_groups"));
        _hostGroupStore = new HostGroupStore(backend.Blob("host_groups"));
    }

    /// <summary>
    /// 在 <c>rootDir/Db/logforesight.db</c> 產生資料庫（已存在則先刪除重建）。
    /// 站台以 <c>--Storage:DataRoot=rootDir --Storage:Type=Sqlite</c> 啟動即讀得到。
    /// </summary>
    public static UiFixtureDataSet Generate(string rootDir, UiFixtureVariant variant)
    {
        Directory.CreateDirectory(rootDir);
        var dbDir = Path.Combine(rootDir, "Db");
        if (Directory.Exists(dbDir))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dbDir, recursive: true);
        }

        // Type=Sqlite、ConnectionString 留空：db 落在 DataRoot\Db\logforesight.db，與站台的預設路徑規則同一條
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, rootDir);
        var set = new UiFixtureDataSet(rootDir, variant, backend);
        try
        {
            set.Build();
        }
        finally
        {
            set.Checkpoint();
        }

        set.WriteScenarioFile();
        return set;
    }

    private void Build()
    {
        ApplyVariant(Backend, Variant);
        BuildIdentity();
        WriteRules();
        BuildHosts();
        WriteRecords();
        WriteHandlingAndCases();
        BuildExtended();
        RegisterScenarios();
    }

    // ── 變體（AI 設定）──────────────────────────────────────────────────────

    /// <summary>
    /// 套用 AI 變體：兩份資料<b>只差這裡寫的設定</b>。獨立成靜態方法，讓測試能複製已產生的資料庫後
    /// 只切換變體來驗證，不必為了「只差 AI 設定」這件事把整份資料集再產生一次。
    /// </summary>
    internal static void ApplyVariant(StorageBackend backend, UiFixtureVariant variant)
    {
        var store = new SystemSettingsStore(backend.Blob("system_settings"));
        store.Update(s =>
        {
            // 站台的「AI 已設定」唯一定義：AiProviders.IsConfigured（Local 供應者只看位址是否有值）。
            // 位址指向本機不會回應的埠：已設定的站台畫面完整，但不會真的呼叫模型
            s.AiProvider = AiProviders.Local;
            s.AiBaseUrl = variant == UiFixtureVariant.AiConfigured ? "http://127.0.0.1:8080" : "";
            s.AiModel = "local-model";
        });

        // 「AI 執行窗口」清空（兩個變體一致，資料才一樣）：已設定變體若留預設的全天窗口，
        // 站台啟動 10 秒後 AI 排程就會對待補紀錄打不會回應的位址，把待補／降級的紀錄改寫成別的狀態，
        // 截圖就不是資料集宣告的樣子
        new ScheduleOptionsStore(backend.Blob("schedule_options")).Update(o => o.AiWindows = new List<ScheduleWindow>());
    }

    // ── 使用者、群組、授權 ──────────────────────────────────────────────────

    private void BuildIdentity()
    {
        // 主機群組：0 台／很多台／超長名稱／一般
        foreach (var (key, name) in new[]
                 {
                     ("rack-a", "ui-機房A"),
                     ("dmz", "ui-DMZ"),
                     ("empty", "ui-空群組（0 台）"),
                     ("bulk", "ui-大群組（很多台）"),
                     ("longname", "ui-超長主機群組名稱：資訊處系統維運科第二組負責之跨機房核心系統主機清單（含備援、測試與災難復原演練環境，另含尚未完成除役程序的舊世代設備）")
                 })
            HostGroupIds[key] = _hostGroupStore.Upsert(new HostGroup { GroupName = name, Active = true }).GroupId;

        // 內建三個角色群組（與 IdentityService.EnsureSeedGroups 同名同角色；站台啟動時看到 Builtin 就不重建）
        long Group(string key, string name, UserRole role, bool builtin, bool pool = false)
        {
            var g = _userGroupStore.Upsert(new UserGroup { GroupName = name, Role = role, Builtin = builtin, Active = true });
            if (pool) _userGroupStore.SetDispatchPool(g.GroupId, true);
            UserGroupIds[key] = g.GroupId;
            return g.GroupId;
        }

        var admin = Group("admin", "admin", UserRole.Admin, true);
        var manager = Group("manager", "manager", UserRole.Manager, true);
        Group("dev", "dev", UserRole.Dev, true);
        // 一般角色（User）：處理能力＋授權範圍內的主機（可見範圍由 group_access 決定）
        var ops = Group("ops", "ui-維運一組", UserRole.User, false, pool: true);
        var viewers = Group("viewers", "ui-受限檢視者", UserRole.User, false);
        Group("ops-empty", "ui-空成員群組", UserRole.User, false);

        var access = new GroupAccessStore(Backend.Blob("group_access"));
        access.SetForUserGroup(ops, new[] { HostGroupIds["rack-a"], HostGroupIds["dmz"], HostGroupIds["bulk"] });
        access.SetForUserGroup(viewers, new[] { HostGroupIds["dmz"] });

        long User(string account, string display, long[] groups, bool active = true, string? email = null)
        {
            var u = _userStore.Upsert(new WebUser
            {
                Account = account, DisplayName = display, Email = email, Active = active, GroupIds = groups.ToList()
            });
            UserIds[account] = u.UserId;
            return u.UserId;
        }

        User("ui-admin", "介面測試管理員", new[] { admin }, email: "ui-admin@example.test");
        User("ui-manager", "介面測試主管", new[] { manager });
        User("ui-handler", "介面測試處理人", new[] { ops }, email: "ui-handler@example.test");
        User("ui-viewer", "介面測試檢視者", new[] { viewers });
        User("ui-handler-long", "超長顯示名稱的處理人員（資訊處系統維運科第二組資深工程師兼專案負責人）", new[] { ops });
        // 同名不同帳號：顯示名稱相同，只有帳號分得出誰是誰
        User("ui-same-a", "王小明", new[] { ops });
        User("ui-same-b", "王小明", new[] { ops });
        User("ui-disabled", "已停用的使用者", new[] { ops }, active: false);
        User("ui-paused", "暫停接單的使用者", new[] { ops });
        User("ui-nogroup", "沒有任何群組的使用者", Array.Empty<long>());
        User("ui-hostile", Hostile.DisplayName, new[] { ops });
        _userStore.SetDispatchPaused(UserIds["ui-paused"], true);
    }

    private static class Hostile
    {
        public const string DisplayName = "<b>粗體</b> & \"引號\" '單引號'";
    }

    // ── 自訂規則（白話說明邊界）──────────────────────────────────────────────

    private void WriteRules()
    {
        var store = new KnownIssueRuleStore(Backend.Blob("rules"));
        var (content, _) = RuleBootstrapper.LoadContent(store);

        KnownIssueRule Rule(string id, string sourcePattern, int eventId, string explanation, bool withSteps = true) => new()
        {
            Id = id, Origin = "custom", Enabled = true, Scope = "all", Platform = "windows",
            SourcePattern = sourcePattern, EventIds = new[] { eventId },
            Category = IssueCategory.Other, Severity = IssueSeverity.Medium,
            Description = "UI 驗收用自訂規則（白話說明長度邊界）",
            PlainExplanation = explanation,
            Impact = withSteps ? "影響範圍：" + UiFixtureText.OneLineExplanation : "",
            LikelyCauses = withSteps ? new[] { "可能原因一", "可能原因二：" + UiFixtureText.MixedUnit } : Array.Empty<string>(),
            NextSteps = withSteps ? new[] { "建議作法一", "建議作法二" } : Array.Empty<string>(),
            ModifiedAt = DateTime.Now
        };

        content.Rules.AddRange(new[]
        {
            // 白話說明 0 字＝沒有任何規則命中（產品驗證不接受空白的 PlainExplanation，空說明只會出現在未命中規則的問題）
            Rule(RuleExplOneLine, "UiFixture-Explain-OneLine", 9002, UiFixtureText.OneLineExplanation),
            Rule(RuleExplLong, "UiFixture-Explain-Long", 9003, UiFixtureText.LongExplanation),
            Rule(RuleExplBlob, "UiFixture-Explain-Blob", 9004, UiFixtureText.Contiguous1000),
            Rule(RuleExplHostile, "UiFixture-Explain-Hostile", 9005, UiFixtureText.Hostile),
            Rule(RuleExplMixed, "UiFixture-Explain-Mixed", 9007, UiFixtureText.RepeatTo(UiFixtureText.MixedUnit, 600)),
            Rule(RuleExplLongSource, "UiFixture-LongSource-", 9006, "來源名稱長達 255 字元（簽章 Source 欄位上限）的問題說明。")
        });
        AddExtendedRules(content.Rules);
        store.Save(content);
    }

    // ── 主機 ────────────────────────────────────────────────────────────────

    /// <summary>63 字元主機名稱（DNS label 上限）</summary>
    public static readonly string HostName63 = ("UI-LONGNAME-" + new string('X', 60))[..63];

    public const string HostFqdn = "web01.corp.example.com";
    public const string HostFqdnLong = "app-server-01.dc2.region-north.sub.corp.example.com";
    public const string HostIpOnly = "10.216.8.77";
    public const string HostHostile = "UI-<i>HTML</i>&\"'";

    private void BuildHosts()
    {
        var winSentinel = new SentinelStore(Backend.Blob("sentinels")).Upsert(new Sentinel
        {
            Name = "UI-Sentinel-Win", BaseUrl = "https://sentinel-win.invalid:8443", Username = "ui",
            Active = true, Os = WebHost.OsWindows, CreatedAt = DateTime.Now.AddDays(-90)
        });
        var linuxSentinel = new SentinelStore(Backend.Blob("sentinels")).Upsert(new Sentinel
        {
            Name = "UI-Sentinel-Linux", BaseUrl = "https://sentinel-linux.invalid:8443", Username = "ui",
            Active = true, Os = WebHost.OsLinux, CreatedAt = DateTime.Now.AddDays(-90)
        });

        var rackA = HostGroupIds["rack-a"];
        var dmz = HostGroupIds["dmz"];
        var bulk = HostGroupIds["bulk"];
        var longGroup = HostGroupIds["longname"];
        var handler = UserIds["ui-handler"];
        var admin = UserIds["ui-admin"];
        var yesterdayMorning = Yesterday.AddHours(3);

        WebHost Host(string key, string name, string os = WebHost.OsWindows, long[]? groups = null, string source = "netiq",
            string? display = null, string? ip = null, string role = "", string tier = WebHost.TierStandard,
            bool active = true, DateTime? lastReport = null, int createdDaysAgo = 90, long[]? owners = null)
        {
            var host = _hostStore.Upsert(new WebHost
            {
                HostName = name, DisplayName = display, IpAddress = ip, IpUpdatedAt = ip == null ? null : Yesterday,
                Os = os, Source = source, Active = active, Tier = tier, RoleDesc = role,
                SentinelId = source == "netiq" ? (os == WebHost.OsLinux ? linuxSentinel.SentinelId : winSentinel.SentinelId) : null,
                NetiqServer = source == "netiq" ? (os == WebHost.OsLinux ? linuxSentinel.Name : winSentinel.Name) : null,
                LastReportAt = lastReport ?? yesterdayMorning,
                CreatedAt = DateTime.Today.AddDays(-createdDaysAgo),
                GroupIds = (groups ?? Array.Empty<long>()).ToList(),
                OwnerUserIds = (owners ?? Array.Empty<long>()).ToList()
            });
            _hosts[key] = host;
            _hostsById[host.HostId] = host;
            HostIds[key] = host.HostId;
            return host;
        }

        // 主角：Windows、多群組（含超長名稱群組）、有負責人，28 天全有紀錄＋所有情境日
        Host("win", "UI-WIN-NORMAL", groups: new[] { rackA, longGroup }, display: "標準 Windows 主機",
            ip: "10.10.1.10", role: "Web 應用程式伺服器", tier: WebHost.TierCore, owners: new[] { handler, admin });
        Host("long63", HostName63, groups: new[] { rackA }, display: "名稱剛好 63 字元的主機", ip: "10.10.1.63");
        Host("fqdn", HostFqdn, groups: new[] { rackA }, ip: "10.10.1.20",
            owners: new[] { handler, UserIds["ui-same-a"], UserIds["ui-same-b"] });   // 一台多位負責人
        Host("fqdn-long", HostFqdnLong, groups: new[] { dmz }, ip: "10.20.1.21", role: "DMZ 對外服務");
        // 只有 IP 當名稱（NetIQ 登錄慣例），顯示名稱來自 Sentinel 回報
        Host("ip-only", HostIpOnly, groups: new[] { dmz }, display: "VM-SPRINGBOARD7", ip: HostIpOnly);
        Host("fin", "FIN-DB-01", groups: new[] { rackA }, display: "財務系統資料庫主機", ip: "10.10.5.5",
            role: "財務系統資料庫主機（含每日批次與月結作業，備援於高雄機房，維運窗口為每月第一個週六凌晨二點至六點，變更須先經財務處與稽核室雙重簽核，並於變更前三個工作天通知所有使用單位窗口）",
            tier: WebHost.TierCore, owners: new[] { handler });
        Host("no-group", "UI-NOGROUP-WIN", display: "沒有任何群組的主機");
        Host("disabled", "UI-DISABLED-HOST", groups: new[] { rackA }, active: false, lastReport: DateTime.Today.AddDays(-10), display: "已停用主機");
        // 靜默未回報：建立很久、最後回報在 21 天前
        Host("silent", "UI-SILENT-HOST", groups: new[] { dmz }, lastReport: DateTime.Today.AddDays(-21).AddHours(3),
            createdDaysAgo: 120, display: "靜默未回報的主機");
        Host("linux", "ui-linux-web01", WebHost.OsLinux, groups: new[] { rackA }, ip: "10.10.2.30", role: "Linux 網站主機");
        Host("linux2", "ui-linux-db01", WebHost.OsLinux, groups: new[] { dmz }, ip: "10.20.2.31", tier: WebHost.TierTest);
        // 只有 PRTG：主機日紀錄全部來自 PRTG finding（LogSource=Prtg），沒有日誌事件
        Host("prtg", "UI-PRTG-SW01", groups: new[] { dmz }, ip: "10.20.9.1", role: "核心交換器（只由 PRTG 監控）");
        Host("local", "UI-LOCAL-HOST", source: "local", groups: new[] { rackA });
        Host("hostile", HostHostile, groups: new[] { rackA }, display: Hostile.DisplayName);
        // 墓碑：舊名稱併入 UI-WIN-NORMAL，歷史經別名展開併入
        Host("tomb", "OLD-UI-WIN-NORMAL", display: "舊主機名稱（已合併）", lastReport: DateTime.Today.AddDays(-27));
        _hostStore.Merge(HostIds["tomb"], HostIds["win"]);
        _hosts["tomb"] = _hostStore.Get(HostIds["tomb"])!;

        // 大群組：很多台。一次批次寫入（逐台 Upsert 是 40 次整份 blob 讀改寫，會拖慢產生時間）
        _hostStore.MutateBatch(hosts =>
        {
            var next = hosts.Max(h => h.HostId) + 1;
            for (var i = 1; i <= 40; i++)
            {
                var host = new WebHost
                {
                    HostId = next++, HostName = $"BULK-{i:D3}", Os = WebHost.OsWindows, Source = "netiq", Active = true,
                    SentinelId = winSentinel.SentinelId, NetiqServer = winSentinel.Name,
                    LastReportAt = yesterdayMorning, CreatedAt = DateTime.Today.AddDays(-30), GroupIds = new List<long> { bulk }
                };
                hosts.Add(host);
                _hosts[$"bulk{i:D2}"] = host;
                _hostsById[host.HostId] = host;
                HostIds[$"bulk{i:D2}"] = host.HostId;
            }
        });
    }

    // ── 分析紀錄 ────────────────────────────────────────────────────────────

    private DateTime DayOf(int daysAgo) => DateTime.Today.AddDays(-daysAgo);

    private void Save(DailyAnalysisRecord record) => _records.Append(record);

    private DailyAnalysisRecord NewRecord(string hostKey, int daysAgo, string risk,
        AnalysisLogSource source = AnalysisLogSource.Netiq)
    {
        var host = _hosts[hostKey];
        return new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = DayOf(daysAgo), RiskLevel = risk,
            LogSource = source, LatestNetiqAttemptStatus = source == AnalysisLogSource.Netiq ? "success" : null,
            SecurityLogAvailable = true, ChannelsRead = new List<string> { "System", "Application", "Security" },
            AiAnalyzed = false
        };
    }

    private void Fill(DailyAnalysisRecord r, IEnumerable<LogIssueSignature> issues)
    {
        r.TopIssues = issues.ToList();
        r.ErrorCount = r.TopIssues.Where(i => i.EntryType == LogEntryType.Error).Sum(i => i.Count);
        r.WarningCount = r.TopIssues.Where(i => i.EntryType != LogEntryType.Error).Sum(i => i.Count);
    }

    private static void Ai(DailyAnalysisRecord r, string headline, string summary, string trend, string action, string brief = "")
    {
        r.AiAnalyzed = true;
        r.Headline = headline;
        r.Summary = summary;
        r.TrendAssessment = trend;
        r.Action = action;
        r.Brief = brief;
    }

    /// <summary>記下一個情境日並把紀錄寫進去</summary>
    private void SaveDay(string key, DailyAnalysisRecord record)
    {
        Save(record);
        Days[key] = (record.HostId, record.Date);
    }

    private static LogIssueSignature Sig(string logName, string source, int eventId, IssueCategory category,
        IssueSeverity severity, int count, bool elevates = false, string? known = null, string? ruleId = null,
        IssueTrend trend = IssueTrend.Recurring, string first = "08:15", string last = "17:42", string? sample = null)
    {
        var s = new LogIssueSignature
        {
            LogName = logName, Source = source, EventId = eventId,
            EntryType = severity == IssueSeverity.Low ? LogEntryType.Warning : LogEntryType.Error,
            Category = category, Severity = severity, ElevatesDayRisk = elevates, KnownIssue = known, RuleId = ruleId,
            Count = count, FirstSeen = first, LastSeen = last, DistinctMessageCount = 1,
            SampleMessages = new List<string> { sample ?? $"{source} 於處理要求時發生錯誤（事件 {eventId}），詳見系統紀錄。" }
        };
        ApplyTrend(s, trend);
        return s;
    }

    private static void ApplyTrend(LogIssueSignature s, IssueTrend trend)
    {
        s.Trend = trend;
        switch (trend)
        {
            case IssueTrend.Unknown:   // 無基準：沒有任何歷史可比
                s.PreviousDayCount = null; s.HistoryDailyAverage = null; s.DaysSeenInHistory = 0;
                break;
            case IssueTrend.New:       // 首次出現
                s.PreviousDayCount = 0; s.HistoryDailyAverage = null; s.DaysSeenInHistory = 0;
                break;
            case IssueTrend.Rising:    // 頻率上升
                s.PreviousDayCount = Math.Max(1, s.Count / 4); s.HistoryDailyAverage = Math.Max(1, s.Count / 4.0); s.DaysSeenInHistory = 6;
                break;
            case IssueTrend.Recurring: // 重複發生
                s.PreviousDayCount = s.Count; s.HistoryDailyAverage = s.Count; s.DaysSeenInHistory = 12;
                break;
            case IssueTrend.Declining:
                s.PreviousDayCount = s.Count * 3; s.HistoryDailyAverage = s.Count * 2.5; s.DaysSeenInHistory = 20;
                break;
        }
    }

    // 常見簽章（背景資料用）
    private static LogIssueSignature Dcom(int count) => Sig("System", "Microsoft-Windows-DistributedCOM", 10016, IssueCategory.Config, IssueSeverity.Low, count);
    private static LogIssueSignature Schannel(int count) => Sig("System", "Schannel", 36887, IssueCategory.Config, IssueSeverity.Low, count);
    private static LogIssueSignature W32Time(int count) => Sig("System", "Microsoft-Windows-Time-Service", 134, IssueCategory.Config, IssueSeverity.Low, count);
    private static LogIssueSignature Scm7034(int count) => Sig("System", "Service Control Manager", 7034, IssueCategory.Service, IssueSeverity.Medium, count, known: "服務意外終止");
    private static LogIssueSignature AppError(int count) => Sig("Application", "Application Error", 1000, IssueCategory.Service, IssueSeverity.Medium, count);
    private static LogIssueSignature Disk153(int count) => Sig("System", "disk", 153, IssueCategory.Storage, IssueSeverity.High, count, elevates: true,
        known: "磁碟 I/O 錯誤或壞軌前兆，硬碟可能即將故障，應盡快備份並安排更換", ruleId: "builtin-storage-disk-io", trend: IssueTrend.Rising);
    private static LogIssueSignature KernelPower41(int count) => Sig("System", "Microsoft-Windows-Kernel-Power", 41, IssueCategory.Hardware, IssueSeverity.High, count,
        elevates: true, known: "系統非預期重新啟動", trend: IssueTrend.New);

    private static LogIssueSignature LinuxSig(string program, string eventKey, IssueCategory cat, IssueSeverity sev, int count, string? ruleId, string sample) =>
        new()
        {
            LogName = "Linux", Source = program, EventId = 0, EventKey = eventKey, EntryType = sev == IssueSeverity.Low ? LogEntryType.Warning : LogEntryType.Error,
            Category = cat, Severity = sev, ElevatesDayRisk = false, RuleId = ruleId, Count = count, FirstSeen = "00:12", LastSeen = "23:48",
            DistinctMessageCount = 2, SampleMessages = new List<string> { sample },
            Trend = IssueTrend.Recurring, PreviousDayCount = count, HistoryDailyAverage = count, DaysSeenInHistory = 9
        };

    private static LogIssueSignature PrtgSig(string ruleCode, long sensorObjid, string category, IssueCategory cat, IssueSeverity sev,
        string ruleId, string detail, PrtgTrendEvidence? evidence = null) =>
        new()
        {
            LogName = PrtgFindingMapper.PrtgLogName, Source = $"PRTG:{ruleCode}", EventId = 0, EntryType = LogEntryType.Warning,
            EventKey = $"prtg:{ruleCode}:{sensorObjid}", Count = 1, FirstSeen = "00:00", LastSeen = "23:59",
            SampleMessages = new List<string> { detail }, DistinctMessageCount = 1, Category = cat, Severity = sev,
            ElevatesDayRisk = sev == IssueSeverity.High, KnownIssue = detail, RuleId = ruleId, PrtgSensorCategory = category,
            PrtgTrendEvidence = evidence, Trend = IssueTrend.Unknown
        };

    private DailyAnalysisRecord Background(string hostKey, int daysAgo, AnalysisLogSource source = AnalysisLogSource.Netiq,
        double? forceHighChance = null)
    {
        var host = _hosts[hostKey];
        var roll = _rnd.NextDouble();
        var high = forceHighChance.HasValue ? roll < forceHighChance.Value : roll > 0.92;
        var medium = !high && roll > 0.62;
        var risk = high ? RiskLevels.High : medium ? RiskLevels.Medium : RiskLevels.Low;
        var r = NewRecord(hostKey, daysAgo, risk, source);
        var issues = new List<LogIssueSignature>();

        if (host.Os == WebHost.OsLinux)
        {
            r.LogSource = AnalysisLogSource.Netiq;
            issues.Add(LinuxSig("CRON", "cron-session", IssueCategory.Other, IssueSeverity.Low, _rnd.Next(20, 90), null, "pam_unix(cron:session): session opened for user root"));
            if (medium || high)
                issues.Add(LinuxSig("kernel", "oom-killer", IssueCategory.Resource, IssueSeverity.Medium, _rnd.Next(1, 6), null, "Out of memory: Killed process 4021 (java) total-vm:8123456kB"));
            if (high)
            {
                var ssh = LinuxSig("sshd", "ssh-bruteforce", IssueCategory.Security, IssueSeverity.High, _rnd.Next(200, 900), "builtin-linux-ssh-bruteforce",
                    "Failed password for invalid user admin from 203.0.113.9 port 51234 ssh2");
                ssh.LoginFailureDetails = new List<LoginFailureDetail>
                {
                    new() { Account = "admin", Source = "203.0.113.9", ReasonCode = "bad_password", Count = ssh.Count / 2 },
                    new() { Account = "root", Source = "203.0.113.10", ReasonCode = "bad_password", Count = ssh.Count / 3 }
                };
                ssh.LoginFailureTotalCount = ssh.LoginFailureDetails.Sum(d => d.Count);
                issues.Add(ssh);
            }
        }
        else if (source == AnalysisLogSource.Prtg)
        {
            r.ChannelsRead = null;
            r.SecurityLogAvailable = null;
            var sensor = 5000 + _rnd.Next(1, 50);
            issues.Add(high
                ? PrtgSig("down", sensor, "availability", IssueCategory.Service, IssueSeverity.High, "builtin-prtg-down", $"感測器 #{sensor} Ping 持續故障")
                : PrtgSig("warning", sensor, "traffic", IssueCategory.Resource, IssueSeverity.Medium, "builtin-prtg-warning", $"感測器 #{sensor} 流量達警告門檻"));
            risk = high ? RiskLevels.High : RiskLevels.Medium;
            r.RiskLevel = risk;
        }
        else
        {
            issues.Add(Dcom(_rnd.Next(10, 80)));
            if (_rnd.NextDouble() < 0.4) issues.Add(Schannel(_rnd.Next(2, 30)));
            if (_rnd.NextDouble() < 0.3) issues.Add(W32Time(_rnd.Next(1, 6)));
            if (medium || high) issues.Add(_rnd.NextDouble() < 0.5 ? Scm7034(_rnd.Next(1, 12)) : AppError(_rnd.Next(1, 20)));
            if (high) issues.Add(_rnd.NextDouble() < 0.5 ? Disk153(_rnd.Next(3, 40)) : KernelPower41(_rnd.Next(1, 4)));
            if (risk == RiskLevels.Low && _rnd.NextDouble() < 0.2) issues.Clear();
        }

        Fill(r, issues);
        if (risk == RiskLevels.Low)
        {
            // 低風險日：沒有需要 AI 解釋的事（統計模式紀錄）
            r.Headline = issues.Count == 0 ? "今天沒有發現需要處理的問題" : "只有常見的低風險紀錄，沒有需要處理的事";
            r.Summary = "今日僅有已知的低風險事件，數量與前幾日相近。";
            r.TrendAssessment = "與前七日相比無顯著變化。";
            r.Action = "不需處理。";
        }
        else
        {
            Ai(r, high ? $"{host.HostName} 出現需要儘快處理的硬體或安全前兆" : $"{host.HostName} 有幾項服務異常，建議本週內確認",
                "今日有數項異常事件，其中以服務異常與磁碟相關訊息較值得留意。",
                "與前幾日相比持續出現，尚未惡化。",
                high ? "請今天內確認備份並安排檢查。" : "請本週內確認相關服務是否正常。");
            r.RiskBasis = high ? "rule:disk EventId 153" : "medium";
        }

        return r;
    }

    private void WriteRecords()
    {
        // ① 主角主機：28 天，情境日見 BuildScenarioDays，其餘為背景日
        var scenarioDays = BuildScenarioDays();
        for (var d = MaxDaysAgo; d >= 1; d--)
        {
            if (scenarioDays.TryGetValue(d, out var make)) make();
            else Save(Background("win", d));
        }

        // ② 其他主機的背景日
        void Many(string key, IEnumerable<int> offsets, AnalysisLogSource source = AnalysisLogSource.Netiq, double? high = null)
        {
            foreach (var d in offsets.OrderByDescending(x => x)) Save(Background(key, d, source, high));
        }

        Many("long63", new[] { 8, 3, 1 });
        Many("fqdn", Enumerable.Range(1, 6));
        Many("fqdn-long", new[] { 4, 2, 1 });
        Many("ip-only", new[] { 3, 2, 1 });
        Many("fin", Enumerable.Range(1, 3), high: 0.5);
        Many("no-group", new[] { 3, 2, 1 });
        Many("disabled", Enumerable.Range(10, 2));
        Many("tomb", new[] { 28, 27 });
        Many("silent", Enumerable.Range(21, 3));
        Many("linux", Enumerable.Range(1, 4), high: 0.4);
        Many("linux2", new[] { 3, 2, 1 });
        Many("prtg", Enumerable.Range(1, 3), AnalysisLogSource.Prtg, high: 0.4);
        Many("local", new[] { 3, 2, 1 }, AnalysisLogSource.Local);
        Many("hostile", new[] { 2, 1 });
        // 大群組的重點是「台數很多」：只有前 3 台有一天紀錄，其餘是新登錄、尚無紀錄的主機（空狀態也是要檢查的畫面）
        for (var i = 1; i <= 3; i++) Many($"bulk{i:D2}", new[] { 1 });

        // 主機日誌的最後回報時間跟著最新紀錄走（靜默主機維持 21 天前；其餘為昨天凌晨）
        PatchRecordFlags();
    }

    /// <summary>
    /// <see cref="IAnalysisRecordStore.Append"/> 固定把降級次數寫 0，且沒有「清除詳情」的單筆入口
    /// （<c>PruneDetails</c> 是整體依日期門檻）——這兩個旗標在這裡直接改列，語意與產品寫入一致：
    /// 詳情清除＝ContentJson 清空、DetailPruned=true、AiPending=false（同 <c>PruneDetails</c>）。
    /// </summary>
    private void PatchRecordFlags()
    {
        using var ctx = Backend.CreateContext();

        void Degrade(string key, int attempts)
        {
            var (hostId, date) = Days[key];
            ctx.DailyRecords.Where(r => r.HostId == hostId && r.RecordDate == date.Date)
                .ExecuteUpdate(s => s.SetProperty(p => p.AiDegradedAttempts, attempts));
        }

        Degrade("ai-degraded-2", 2);
        Degrade("ai-abandoned-3", 3);

        var (prunedHost, prunedDate) = Days["detail-pruned"];
        ctx.DailyRecords.Where(r => r.HostId == prunedHost && r.RecordDate == prunedDate.Date)
            .ExecuteUpdate(s => s
                .SetProperty(p => p.ContentJson, string.Empty)
                .SetProperty(p => p.OriginalRiskContentJson, (string?)null)
                .SetProperty(p => p.WriteRevision, p => p.WriteRevision + 1)
                .SetProperty(p => p.AiPending, false)
                .SetProperty(p => p.DetailPruned, true));
    }

    // ── 情境日（主角主機）───────────────────────────────────────────────────

    private Dictionary<int, Action> BuildScenarioDays()
    {
        string Fmt(int d) => DayOf(d).ToString("MM/dd");

        return new Dictionary<int, Action>
        {
            [1] = () => SaveDay("all-kinds", AllKindsDay(1)),
            [2] = () => SaveDay("issues-60", ManyIssuesDay(2)),
            [3] = () =>
            {
                var r = NewRecord("win", 3, RiskLevels.Low);
                r.Headline = "今天沒有發現需要處理的問題";
                r.Summary = "今日沒有任何需要列入追蹤的事件。";
                r.TrendAssessment = "與前七日相比無顯著變化。";
                r.Action = "不需處理。";
                SaveDay("issues-0", r);
            },
            [4] = () =>
            {
                var r = NewRecord("win", 4, RiskLevels.Low);
                Fill(r, new[] { Dcom(37) });
                r.Headline = "只有一項常見的低風險紀錄";
                r.Summary = "DCOM 10016 為例行性權限提醒，數量與往常相近。";
                r.TrendAssessment = "與前七日相比無顯著變化。";
                r.Action = "不需處理。";
                SaveDay("issues-1", r);
            },
            [5] = () =>
            {
                // 統計模式：AI 未產生也未待補（AI 已定案不需要／已嘗試但失敗的既有語意）
                var r = NewRecord("win", 5, RiskLevels.Medium);
                Fill(r, new[] { Scm7034(4), AppError(9), Dcom(22) });
                r.Headline = "服務意外終止 今日出現 4 次";
                r.Summary = "統計模式：今日偵測到 3 類問題，其中服務異常 2 類。";
                r.TrendAssessment = "與前七日相比略有增加。";
                r.Action = "請確認相關服務是否持續異常。";
                r.RiskBasis = "medium";
                SaveDay("ai-not-generated", r);
            },
            [6] = () =>
            {
                var r = NewRecord("win", 6, RiskLevels.Medium);
                Fill(r, new[] { Scm7034(5), AppError(3) });
                r.AiAnalyzed = true;
                r.AiPending = true;   // 次數未達上限：仍會自動重試
                r.Headline = AiDegradation.ComposeHeadline("服務意外終止", 2);
                r.Summary = "規則層結論：服務意外終止。";
                r.RiskBasis = "medium";
                SaveDay("ai-degraded-2", r);
            },
            [7] = () =>
            {
                var r = NewRecord("win", 7, RiskLevels.Medium);
                Fill(r, new[] { AppError(11) });
                r.AiAnalyzed = true;
                r.AiPending = false;
                r.Headline = AiDegradation.ComposeHeadline("Application Error 今日出現 11 次", 3);
                r.Summary = "規則層結論：應用程式錯誤。";
                r.RiskBasis = "medium";
                SaveDay("ai-abandoned-3", r);
            },
            [8] = () =>
            {
                var r = NewRecord("win", 8, RiskLevels.High);
                var issues = new[] { Disk153(14), Scm7034(2) };
                Fill(r, issues);
                r.AiAnalyzed = false;
                r.AiPending = true;
                r.RiskReportPending = true;
                r.Headline = AiDegradation.BuildServiceFailedHeadline(issues);
                r.Summary = "統計段已完成，AI 白話摘要排隊中。";
                r.RiskBasis = "rule:disk EventId 153";
                SaveDay("ai-pending", r);
            },
            [9] = () =>
            {
                // 只有 Headline：其餘三段與 Brief 皆空
                var r = NewRecord("win", 9, RiskLevels.High);
                Fill(r, new[] { Disk153(8), KernelPower41(1) });
                Ai(r, "磁碟錯誤與意外重開機同時出現，建議今天內處理", "", "", "");
                r.RiskBasis = "rule:disk EventId 153";
                SaveDay("ai-headline-only", r);
            },
            [10] = () =>
            {
                // 缺一段（TrendAssessment 空）；順便掛週檢結果
                var r = NewRecord("win", 10, RiskLevels.Medium);
                Fill(r, new[] { Scm7034(6), Dcom(30) });
                Ai(r, "服務反覆停止，需要確認是否有人為操作", "今日服務控制管理員記錄了 6 次服務意外終止。", "", "請本週內確認服務啟動帳號與相依性。");
                r.RiskBasis = "medium";
                r.WeeklyCheckup = new WeeklyCheckupResult
                {
                    CheckupDate = DayOf(10), HasFindings = true,
                    Conclusion = "過去一週有兩項緩慢上升的趨勢：C 槽可用空間每日減少約 0.8%，以及非分頁集區使用量持續成長。"
                };
                SaveDay("ai-missing-one", r);
            },
            [11] = () =>
            {
                // 缺兩段（TrendAssessment、Action 皆空）
                var r = NewRecord("win", 11, RiskLevels.Medium);
                Fill(r, new[] { AppError(15) });
                Ai(r, "應用程式錯誤增加", "今日應用程式錯誤事件共 15 次。", "", "");
                r.RiskBasis = "medium";
                SaveDay("ai-missing-two", r);
            },
            [12] = () => SaveDay("ai-800", LongAiDay(12)),
            [13] = () =>
            {
                var r = NewRecord("win", 13, RiskLevels.Medium);
                Fill(r, new[] { Scm7034(9), Dcom(41) });
                Ai(r, "服務異常（含表格與清單）", UiFixtureText.Markdown, "與前一日相比增加 **3 次**。\n\n- 趨勢：上升\n- 基準：4 次",
                    "1. 先確認備份\n2. 再檢查服務帳號 $\\rightarrow$ 必要時重新啟動\n\n預估處理成本約 $100 元。",
                    brief: "重點：服務停止 9 次 $\\rightarrow$ 建議今天確認。\n\n- 風險：中\n- 成本：$100");
                r.RiskBasis = "medium";
                SaveDay("ai-markdown", r);
            },
            [14] = () =>
            {
                var r = NewRecord("win", 14, RiskLevels.Medium);
                Fill(r, new[] { Scm7034(3) });
                Ai(r, "資料不完整：部分時段與 Security 日誌未讀取", "僅取得部分時段的事件，結論可能低估。", "趨勢基準已排除本日。", "請確認 Sentinel 的事件保留設定。");
                r.DataIncomplete = true;
                r.SecurityLogAvailable = false;
                r.ChannelsRead = new List<string> { "System", "Application" };
                r.UncoveredChecks = new List<string>
                {
                    "無 Security 權限，入侵跡象規則表與相關關聯模式未檢查",
                    "System 日誌只讀到 06:00–12:00，其餘時段已被系統覆蓋",
                    UiFixtureText.RepeatTo("很長的未涵蓋項目說明，", 120)
                };
                r.LatestNetiqAttemptStatus = "partial";
                r.RiskBasis = "medium";
                SaveDay("data-incomplete", r);
            },
            [15] = () =>
            {
                var r = NewRecord("win", 15, RiskLevels.Low, AnalysisLogSource.Unknown);
                r.LatestNetiqAttemptStatus = null;
                r.HostId = _hosts["win"].HostId;
                Fill(r, new[] { Schannel(5) });
                r.Headline = "舊紀錄（來源不明）";
                r.Summary = "本欄位問世前寫入的舊紀錄。";
                r.TrendAssessment = "—";
                r.Action = "不需處理。";
                SaveDay("logsource-unknown", r);
            },
            [16] = () =>
            {
                var r = NewRecord("win", 16, RiskLevels.Medium, AnalysisLogSource.Local);
                Fill(r, new[] { AppError(5) });
                Ai(r, "本機事件日誌：應用程式錯誤", "本機直讀的事件日誌顯示應用程式錯誤。", "與前幾日相近。", "請確認相關應用程式。");
                r.RiskBasis = "medium";
                SaveDay("logsource-local", r);
            },
            [17] = () => SaveDay("logsource-prtg", PrtgDay(17)),
            [18] = () => SaveDay("legacy-messages", LegacyMessageDay(18)),
            [19] = () => SaveDay("evidence", EvidenceDay(19)),
            [20] = () => SaveDay("hostile-long", HostileDay(20)),
            [21] = () => SaveDay("handling", HandlingDay(21)),
            [22] = () => SaveDay("cases", CaseDay(22)),
            [24] = () =>
            {
                var r = Background("win", 24, forceHighChance: 0);
                r.RiskLevel = RiskLevels.Medium;
                Fill(r, new[] { Scm7034(7), AppError(4) });
                Ai(r, "服務異常（詳情已清除）", "原本的詳情已被保留期限清除。", "—", "—");
                SaveDay("detail-pruned", r);
            }
        }.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // d1：8 個類別 × 3 級嚴重度；趨勢四態；次數 0／1／999／1,234,567；抑制；趨勢與關聯告警；完整 AI 內容
    private DailyAnalysisRecord AllKindsDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);
        var counts = new[] { 1, 3, 12, 48, 999, 1_234_567, 0, 7 };
        var trends = new[] { IssueTrend.Recurring, IssueTrend.New, IssueTrend.Rising, IssueTrend.Unknown, IssueTrend.Declining };
        var issues = new List<LogIssueSignature>();
        var idx = 0;
        foreach (var cat in Enum.GetValues<IssueCategory>())
        {
            foreach (var sev in new[] { IssueSeverity.Low, IssueSeverity.Medium, IssueSeverity.High })
            {
                var elevates = sev == IssueSeverity.High && cat is IssueCategory.Hardware or IssueCategory.Storage or IssueCategory.Security;
                var s = Sig(cat == IssueCategory.Security ? "Security" : idx % 2 == 0 ? "System" : "Application",
                    $"Ui-{cat}-{sev}", 1000 + idx, cat, sev, counts[idx % counts.Length], elevates,
                    known: idx % 3 == 0 ? $"{cat} 類別的已知問題說明（{sev}）" : null,
                    trend: trends[idx % trends.Length],
                    first: idx % 4 == 0 ? "00:00" : "08:15", last: idx % 4 == 0 ? "23:59" : idx % 4 == 1 ? "08:15" : "17:42");
                // 已抑制：只影響「要不要吵」，仍照常顯示
                if (cat == IssueCategory.Config && sev == IssueSeverity.High) { s.Suppressed = true; s.ElevatesDayRisk = false; }
                issues.Add(s);
                idx++;
            }
        }

        // 一個帶規則 Id 的問題，讓詳情頁的處置參考面板有內容
        issues.Add(Disk153(48));
        Fill(r, issues);

        Ai(r,
            "磁碟與安全事件同時升高，建議今天先處理儲存裝置前兆",
            "今天有三類問題需要優先處理：磁碟 I/O 錯誤持續增加、帳號登入失敗大量出現、備份工作失敗。其餘為例行性的低風險紀錄。",
            "磁碟錯誤相較前一日增加，屬於惡化中的前兆；登入失敗為近三日首次出現。",
            "今天內先確認備份是否完整，再安排磁碟更換；同時檢查登入失敗的來源位址。",
            brief: "磁碟錯誤增加、登入失敗大量出現。請今天先確認備份，再安排磁碟更換。");
        r.RiskBasis = "rule:disk EventId 153";
        r.ScreenedTailCount = 12;
        r.ScreeningNotes = new List<string> { "Ui-Other-Low 次數偏高但無嚴重度升級", "Ui-Resource-Low 與磁碟事件時間重疊" };

        var disk = issues.First(i => i.EventId == 153);
        var first = issues[1];
        var second = issues[8];
        r.TrendAlerts = new List<string>
        {
            $"【首次出現】{first.Source} EventId {first.EventId}",
            $"【頻率上升】{disk.Source} EventId 153（48 次，歷史基準 12 次）",
            "【整體錯誤量突增】今日錯誤事件共 1,235,000 次，為近期平均的 9 倍",
            "【安全稽核事件量突增】今日稽核事件為近期平均的 4 倍"
        };
        r.TrendAlertRefs = new List<TrendAlertRef>
        {
            new() { Text = r.TrendAlerts[0], IssueKey = IssueSignatureKey.For(first), Kind = TrendAlertKinds.Signature },
            new() { Text = r.TrendAlerts[1], IssueKey = IssueSignatureKey.For(disk), Kind = TrendAlertKinds.Signature },
            new() { Text = r.TrendAlerts[2], IssueKey = null, Kind = TrendAlertKinds.VolumeError },
            new() { Text = r.TrendAlerts[3], IssueKey = null, Kind = TrendAlertKinds.VolumeAudit }
        };
        r.CorrelationAlerts = new List<string>
        {
            "【儲存裝置故障鏈】磁碟 I/O 錯誤與檔案系統損毀同日出現",
            $"【暴力破解→RDP 得手】{second.Source} 與登入失敗大量出現後有成功登入"
        };
        r.CorrelationAlertRefs = new List<CorrelationAlertRef>
        {
            new() { Text = r.CorrelationAlerts[0], PatternId = CorrelationPatternIds.StorageChain },
            new() { Text = r.CorrelationAlerts[1], PatternId = CorrelationPatternIds.BruteSuccess }
        };
        // 已抑制的告警：誠實申報「關掉的東西其實還在發生」
        r.SuppressedTrendAlerts = new List<string> { "【頻率上升】Ui-Config-High EventId 1021（999 次）— 已被抑制設定關掉" };
        r.SuppressedCorrelationAlerts = new List<string> { "【服務崩潰迴圈】Service Control Manager 7034 反覆出現 — 已被抑制設定關掉" };

        r.DeepDives = new List<CategoryDeepDive>
        {
            new()
            {
                Category = IssueCategory.Storage,
                Findings = new List<DeepDiveFinding>
                {
                    new()
                    {
                        Problem = "disk 153（I/O 重試）近三日持續增加",
                        LikelyCauses = new List<string> { "硬碟壞軌", "SAS/SATA 排線或背板接觸不良", "控制器韌體問題" },
                        Impact = "若硬碟故障且無有效備份，可能造成資料遺失與服務中斷。",
                        NextSteps = new List<string> { "確認最近一次完整備份", "檢查 SMART 與控制器日誌", "安排維護時段更換硬碟" }
                    },
                    new()
                    {
                        Problem = UiFixtureText.RepeatTo("第二個發現的問題描述會刻意寫得比較長，用來檢查卡片內多段文字的排版。", 300),
                        LikelyCauses = new List<string> { UiFixtureText.MixedUnit },
                        Impact = UiFixtureText.OneLineExplanation,
                        NextSteps = new List<string> { "步驟一", "步驟二" }
                    }
                }
            },
            new()
            {
                Category = IssueCategory.Security,
                Findings = new List<DeepDiveFinding>
                {
                    new()
                    {
                        Problem = "登入失敗集中在單一帳號與少數來源",
                        LikelyCauses = new List<string> { "密碼猜測攻擊", "服務帳號密碼過期後仍被排程重試" },
                        Impact = "帳號若被猜中將可橫向移動。",
                        NextSteps = new List<string> { "封鎖來源位址", "重設相關帳號密碼" }
                    }
                }
            }
        };
        return r;
    }

    // d2：65 個問題（60 個以上）
    private DailyAnalysisRecord ManyIssuesDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.Medium);
        var cats = Enum.GetValues<IssueCategory>();
        var issues = new List<LogIssueSignature>();
        for (var i = 1; i <= 65; i++)
        {
            var sev = i % 9 == 0 ? IssueSeverity.Medium : IssueSeverity.Low;
            issues.Add(Sig(i % 2 == 0 ? "System" : "Application", $"UiBulk-Source-{i:D2}", 3000 + i, cats[i % cats.Length], sev,
                1 + (i * 37) % 400, trend: i % 5 == 0 ? IssueTrend.New : IssueTrend.Recurring));
        }

        Fill(r, issues);
        Ai(r, "問題數量很多，但多為例行性的低風險紀錄", "今日共有 65 類事件，其中只有 7 類為中等嚴重度。",
            "與前一日相比類別數量增加。", "建議先處理中等嚴重度的 7 類。");
        r.RiskBasis = "medium";
        return r;
    }

    // d12：每個文字欄位 800 字以上
    private DailyAnalysisRecord LongAiDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);
        Fill(r, new[] { Disk153(21), Scm7034(8) });
        string Long(string unit, int n = 900) => UiFixtureText.RepeatTo(unit, n);
        Ai(r,
            Long("標題也可能很長：磁碟與服務同時出現異常，", 220),
            Long("這是一段非常長的摘要說明，用來檢查單一欄位超過八百字時的換行、留白與卡片高度。", 900),
            Long("趨勢評估同樣很長，描述近一個月來的變化、基準與可能的原因，並比較前後的差異。", 900),
            Long("行動建議也很長：請先確認備份、再確認服務、最後聯絡廠商，並且記錄每一個步驟與時間。", 900),
            brief: Long("重點速報很長很長，", 820));
        r.RiskBasis = "rule:disk EventId 153";
        return r;
    }

    // d17：只因 PRTG 建立（事件統計全零）＋數值趨勢佐證
    private DailyAnalysisRecord PrtgDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.Medium, AnalysisLogSource.Prtg);
        r.ChannelsRead = null;
        r.SecurityLogAvailable = null;
        var evidence = new PrtgTrendEvidence("disk_free_trend", 2001, 2002, "UI-WIN-NORMAL", "磁碟可用空間 C:",
            DateOnly.FromDateTime(DayOf(daysAgo + 14)), DateOnly.FromDateTime(DayOf(daysAgo)), 14, 13, 12.5, "%", -0.8, 5.0,
            DateOnly.FromDateTime(DayOf(daysAgo).AddDays(9)));
        Fill(r, new[]
        {
            PrtgSig("disk_free_trend", 2002, "disk", IssueCategory.Storage, IssueSeverity.Medium, "builtin-prtg-disk-free-trend", evidence.Describe(), evidence),
            PrtgSig("down", 2003, "availability", IssueCategory.Service, IssueSeverity.High, "builtin-prtg-down", "Ping 感測器持續故障 3 小時")
        });
        r.ErrorCount = 0;
        r.WarningCount = 0;
        Ai(r, "PRTG 發現磁碟可用空間持續下降", "PRTG 的數值趨勢顯示 C 槽可用空間每日減少約 0.8%。", "近 14 日有效 13 日。", "請於預估日期前清理或擴充。");
        r.RiskBasis = "trend";
        return r;
    }

    // d18：舊版把整段訊息當帳號／來源寫入的三種原樣值（不清理）
    private DailyAnalysisRecord LegacyMessageDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);
        const string sec = "Microsoft-Windows-Security-Auditing";

        LogIssueSignature Legacy(int eventId, string label, string message, string? ip)
        {
            var s = Sig("Security", sec, eventId, IssueCategory.Security, IssueSeverity.High, 3, sample: label);
            s.KeyAccounts = new List<string> { message };
            s.KeyIps = ip == null ? new List<string>() : new List<string> { ip };
            s.KeyDetails = UiFixtureText.KeyDetails(s.KeyAccounts, s.KeyIps);
            s.LoginFailureDetails = new List<LoginFailureDetail>();
            return s;
        }

        var group = Legacy(4728, "安全性群組成員新增", UiFixtureText.Legacy1, null);
        var audit = Legacy(4719, "系統稽核原則變更", UiFixtureText.Legacy2, null);
        var logon = Legacy(4625, "帳戶登入失敗", UiFixtureText.Legacy3, "10.216.8.188");
        logon.Count = 42;
        logon.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new()
            {
                Account = UiFixtureText.Legacy3, IsComputerAccount = false, Source = UiFixtureText.Legacy3Source,
                LogonType = 3, ReasonCode = "bad_password", Count = 40
            },
            new() { Account = "srvadm18.brk", Source = "10.216.8.188", LogonType = 3, ReasonCode = "bad_password", Count = 2 }
        };
        logon.LoginFailureTotalCount = 42;
        // 300 字元、沒有任何斷點的值：測帳號摘要在格子內折行，不越過趨勢與處理狀態欄（第 73 輪 D）
        var contiguous = Legacy(4740, "帳戶被鎖定", UiFixtureText.Contiguous2000[..300], null);
        Fill(r, new[] { group, audit, logon, contiguous });
        Ai(r, "稽核與群組變更事件出現", "安全性群組與稽核原則在同一時段被變更，另有登入失敗。", "近三日首次出現。", "請確認這些變更是否為授權作業。");
        r.RiskBasis = "high_issue:Microsoft-Windows-Security-Auditing EventId 4728";
        return r;
    }

    // d19：佐證各種數量（帳號 0／1／50、登入失敗明細、樣本、來源觀測、殘留憑證）
    private DailyAnalysisRecord EvidenceDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);
        const string sec = "Microsoft-Windows-Security-Auditing";
        var host = _hosts["win"].HostName;

        // 50 個帳號、50 組登入失敗明細（截斷）、5 則樣本（其中一則多行）、多個相異訊息
        var accounts50 = UiFixtureText.Accounts(50);
        var ips50 = Enumerable.Range(1, 50).Select(i => $"203.0.113.{i}").ToList();
        var e50 = Sig("Security", sec, 4625, IssueCategory.Security, IssueSeverity.High, 4321, elevates: true, trend: IssueTrend.Rising);
        e50.KeyAccounts = accounts50;
        e50.KeyIps = ips50;
        e50.KeyDetails = UiFixtureText.KeyDetails(accounts50, ips50);
        e50.DistinctMessageCount = 14;
        e50.SampleMessages = new List<string>
        {
            "帳戶登入失敗。\n\n主體:\n\t安全性識別碼: S-1-0-0\n\t帳戶名稱: -\n\n登入類型: 3",
            "帳戶登入失敗（第二則樣本）。",
            "帳戶登入失敗（第三則樣本）：" + UiFixtureText.MixedUnit,
            "帳戶登入失敗（第四則樣本）。",
            "帳戶登入失敗（第五則樣本）。"
        };
        var reasons = new[] { "bad_password", "account_locked", "account_disabled", "password_expired", "unknown" };
        e50.LoginFailureDetails = Enumerable.Range(0, 50).Select(i => new LoginFailureDetail
        {
            Account = accounts50[i], IsComputerAccount = i % 11 == 0, Source = ips50[i], LogonType = i % 3 == 0 ? 10 : 3,
            ReasonCode = reasons[i % reasons.Length], Count = 100 - i
        }).ToList();
        e50.LoginFailureTotalCount = 4321;
        e50.LoginFailureDetailsTruncated = true;

        // 1 個帳號、1 組明細、1 則樣本
        var e1 = Sig("Security", sec, 4771, IssueCategory.Security, IssueSeverity.Medium, 6);
        e1.KeyAccounts = new List<string> { "svc-backup" };
        e1.KeyIps = new List<string> { "10.0.0.5" };
        e1.KeyDetails = UiFixtureText.KeyDetails(e1.KeyAccounts, e1.KeyIps);
        e1.LoginFailureDetails = new List<LoginFailureDetail>
            { new() { Account = "svc-backup", Source = "10.0.0.5", LogonType = 3, ReasonCode = "password_expired", Count = 6 } };
        e1.LoginFailureTotalCount = 6;

        // 0 個帳號、0 組明細
        var e0 = Sig("Security", sec, 4776, IssueCategory.Security, IssueSeverity.Low, 2);
        e0.KeyAccounts = new List<string>();
        e0.KeyIps = new List<string>();
        e0.KeyDetails = null;
        e0.LoginFailureDetails = new List<LoginFailureDetail>();

        // 殘留憑證重試（判定為機械性重複，已由 High 降成 Medium）
        var residual = Sig("Security", sec, 4740, IssueCategory.Security, IssueSeverity.Medium, 288);
        residual.KeyAccounts = new List<string> { "svc-old-job" };
        residual.KeyIps = new List<string> { "10.0.0.9" };
        residual.KeyDetails = UiFixtureText.KeyDetails(residual.KeyAccounts, residual.KeyIps);
        residual.LoginFailureDetails = new List<LoginFailureDetail>
            { new() { Account = "svc-old-job", Source = "10.0.0.9", LogonType = 4, ReasonCode = "bad_password", Count = 288 } };
        residual.LoginFailureTotalCount = 288;
        residual.ResidualCredentialRetry = true;
        residual.ResidualCredentialBasis = "同一帳號 svc-old-job 對同一來源 10.0.0.9 每 5 分鐘重試一次、全天共 288 次，集中度 100%，判定為殘留憑證重試而非攻擊。";

        // 來源觀測：0／1／上限截斷（站台對來源觀測有 64 筆與 8 KB 兩道上限，後者先到）
        SourceEvidence Evidence(int i) => new()
        {
            SourceKind = SourceEvidenceKind.SentinelWindows, RelationContract = SourceEvidenceRelation.DiskIoToHardware,
            ResourceScope = SourceResourceScope.Volume, ExactResourceKey = $@"\\.\PHYSICALDRIVE{i}", ExactHostKey = host,
            EventTimeUtc = DateTimeOffset.UtcNow.AddHours(-i), SourceReference = $"evt-{i}",
            SourceReferenceQuality = SourceReferenceQuality.ExactNative
        };

        var obs0 = Sig("System", "disk", 7, IssueCategory.Storage, IssueSeverity.Medium, 3);
        var obs1 = Sig("System", "disk", 11, IssueCategory.Storage, IssueSeverity.Medium, 4);
        obs1.SourceObservations = SourceEvidence.BoundObservations(new[] { Evidence(1) }, out var trunc1);
        obs1.SourceObservationsTruncated = trunc1;
        var obsMax = Sig("System", "disk", 51, IssueCategory.Storage, IssueSeverity.High, 120, elevates: true);
        obsMax.SourceObservations = SourceEvidence.BoundObservations(Enumerable.Range(0, 100).Select(Evidence), out var truncMax);
        obsMax.SourceObservationsTruncated = truncMax;

        // 樣本：5 則（短）
        var samples5 = Sig("Security", sec, 4624, IssueCategory.Security, IssueSeverity.Low, 900);
        samples5.SampleMessages = Enumerable.Range(1, 5).Select(i => $"帳戶已成功登入（樣本 {i}）。").ToList();
        samples5.DistinctMessageCount = 5;

        Fill(r, new[] { e50, e1, e0, residual, obs0, obs1, obsMax, samples5 });
        Ai(r, "登入失敗大量集中，來源分散", "今日登入失敗超過四千次，帳號與來源都很分散，屬於可疑形狀。", "近三日持續上升。", "請封鎖可疑來源並通知帳號擁有者。");
        r.RiskBasis = "rule:Microsoft-Windows-Security-Auditing EventId 4625";
        return r;
    }

    // d20：HTML／引號／超長 Source／無空白長字串／中英混排／各種白話說明長度
    private DailyAnalysisRecord HostileDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);

        LogIssueSignature Ruled(string ruleId, string source, int eventId, string sample, IssueSeverity sev = IssueSeverity.Medium)
        {
            var s = Sig("Application", source, eventId, IssueCategory.Other, sev, 11, ruleId: ruleId, sample: sample);
            return s;
        }

        var empty = Sig("Application", "UiFixture-Explain-Empty", 9001, IssueCategory.Other, IssueSeverity.Medium, 11, sample: "沒有任何規則命中：白話說明 0 字、也沒有處置參考面板");
        var one = Ruled(RuleExplOneLine, "UiFixture-Explain-OneLine", 9002, "白話說明只有一行");
        var @long = Ruled(RuleExplLong, "UiFixture-Explain-Long", 9003, "白話說明超過 300 字");
        var blob = Ruled(RuleExplBlob, "UiFixture-Explain-Blob", 9004, UiFixtureText.Contiguous2000);   // 樣本訊息與 KnownIssue：2,000 字元無空白
        blob.KnownIssue = UiFixtureText.Contiguous2000;
        var hostile = Ruled(RuleExplHostile, "UiFixture-Explain-Hostile", 9005, UiFixtureText.Hostile, IssueSeverity.High);
        hostile.KnownIssue = UiFixtureText.Hostile;
        var mixed = Ruled(RuleExplMixed, "UiFixture-Explain-Mixed", 9007, UiFixtureText.RepeatTo(UiFixtureText.MixedUnit, 400));
        var longSource = Sig("Application", UiFixtureText.Source255, 9006, IssueCategory.Other, IssueSeverity.Medium, 5,
            ruleId: RuleExplLongSource, known: "Source 欄位長達 255 字元、沒有任何空白可以斷行", sample: "來源名稱長達 255 字元");
        var hostileSource = Sig("Application", "UiFixture-<script>alert(1)</script>&\"'", 9100, IssueCategory.Other, IssueSeverity.Low, 3,
            known: UiFixtureText.Hostile, sample: UiFixtureText.Hostile);

        Fill(r, new[] { empty, one, @long, blob, hostile, mixed, longSource, hostileSource });
        Ai(r, UiFixtureText.Hostile + " — " + UiFixtureText.MixedUnit,
            UiFixtureText.Hostile + "\n\n" + UiFixtureText.Contiguous2000,
            UiFixtureText.Hostile,
            UiFixtureText.Hostile + " " + UiFixtureText.MixedUnit,
            brief: UiFixtureText.Hostile);
        r.TrendAlerts = new List<string> { "【頻率上升】" + UiFixtureText.Hostile };
        r.TrendAlertRefs = new List<TrendAlertRef>
            { new() { Text = r.TrendAlerts[0], IssueKey = IssueSignatureKey.For(hostile), Kind = TrendAlertKinds.Signature } };
        r.CorrelationAlerts = new List<string> { "【跨日入侵鏈】" + UiFixtureText.Hostile };
        r.CorrelationAlertRefs = new List<CorrelationAlertRef>
            { new() { Text = r.CorrelationAlerts[0], PatternId = CorrelationPatternIds.IntrusionChain } };
        r.DeepDives = new List<CategoryDeepDive>
        {
            new()
            {
                Category = IssueCategory.Other,
                Findings = new List<DeepDiveFinding>
                {
                    new()
                    {
                        Problem = UiFixtureText.Hostile, LikelyCauses = new List<string> { UiFixtureText.Hostile, UiFixtureText.Contiguous2000 },
                        Impact = UiFixtureText.Hostile, NextSteps = new List<string> { UiFixtureText.Hostile }
                    }
                }
            }
        };
        r.RiskBasis = "high_issue:UiFixture-Explain-Hostile EventId 9005";
        return r;
    }

    // d21：處理狀態各態（每個問題一種狀態，寫入見 WriteHandlingAndCases）
    private readonly List<LogIssueSignature> _handlingIssues = new();

    private DailyAnalysisRecord HandlingDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.Medium);
        foreach (var status in IssueHandlingStatuses.All)
            _handlingIssues.Add(Sig("System", $"Ui-Handle-{status}", 7000 + _handlingIssues.Count, IssueCategory.Service,
                IssueSeverity.Medium, 10 + _handlingIssues.Count));
        // 兩個額外的「觀察」問題：一個尚在觀察期、一個觀察已到期；一個進行中但逾期
        _handlingIssues.Add(Sig("System", "Ui-Handle-observing-active", 7100, IssueCategory.Service, IssueSeverity.Medium, 5));
        _handlingIssues.Add(Sig("System", "Ui-Handle-overdue", 7101, IssueCategory.Service, IssueSeverity.High, 9));
        _handlingIssues.Add(Sig("System", "Ui-Handle-long-note", 7102, IssueCategory.Service, IssueSeverity.Medium, 4));
        Fill(r, _handlingIssues);
        Ai(r, "這一天的問題涵蓋所有處理狀態", "每個問題標記了不同的處理狀態，用來檢查狀態徽章與逾期提示。", "—", "請依各問題的狀態追蹤。");
        r.RiskBasis = "medium";
        return r;
    }

    // d22：進行中案件（寫入見 WriteHandlingAndCases）
    private readonly List<LogIssueSignature> _caseIssues = new();

    private DailyAnalysisRecord CaseDay(int daysAgo)
    {
        var r = NewRecord("win", daysAgo, RiskLevels.High);
        for (var i = 1; i <= 5; i++)
            _caseIssues.Add(Sig("System", $"Ui-Case-Issue-{i}", 8000 + i, i == 1 ? IssueCategory.Storage : IssueCategory.Service,
                i == 1 ? IssueSeverity.High : IssueSeverity.Medium, 10 * i, elevates: i == 1));
        Fill(r, _caseIssues);
        Ai(r, "數個問題已交辦給不同的處理人", "案件分別交辦給單一帳號、超長顯示名稱，以及同名不同帳號的兩位處理人。", "—", "請追蹤各案件進度。");
        r.RiskBasis = "high_issue:Ui-Case-Issue-1 EventId 8001";
        return r;
    }

    // ── 處理狀態、案件、交辦單 ───────────────────────────────────────────────

    private void WriteHandlingAndCases()
    {
        var hostName = _hosts["win"].HostName;
        var admin = UserIds["ui-admin"];
        var handler = UserIds["ui-handler"];
        var (_, handlingDate) = Days["handling"];
        var (_, caseDate) = Days["cases"];
        var now = DateTime.Now;

        // 處理狀態：IssueHandlingStatuses.All 八態＋觀察期內／到期、進行中逾期、超長與含 HTML 的備註
        var handlings = new List<IssueHandling>();
        IssueHandling H(LogIssueSignature sig, string status, string? note = null, DateTime? due = null, string account = "ui-handler", long? actor = null)
            => new()
            {
                HostName = hostName, Date = handlingDate, IssueKey = IssueSignatureKey.For(sig), Status = status,
                ActorId = actor ?? UserIds[account], ActorAccount = account, Note = note, DueDate = due, UpdatedAt = now.AddHours(-2)
            };

        foreach (var status in IssueHandlingStatuses.All)
        {
            var sig = _handlingIssues.First(i => i.Source == $"Ui-Handle-{status}");
            DateTime? due = status switch
            {
                IssueHandlingStatuses.Observing => DateTime.Today.AddDays(-2),   // 觀察到期
                IssueHandlingStatuses.InProgress => DateTime.Today.AddDays(3),
                _ => null
            };
            handlings.Add(H(sig, status, status == IssueHandlingStatuses.Open ? null : $"狀態 {status} 的備註", due));
        }

        handlings.Add(H(_handlingIssues.First(i => i.Source == "Ui-Handle-observing-active"), IssueHandlingStatuses.Observing,
            "觀察中，尚未到期", DateTime.Today.AddDays(5)));
        handlings.Add(H(_handlingIssues.First(i => i.Source == "Ui-Handle-overdue"), IssueHandlingStatuses.InProgress,
            "預計完成日已過", DateTime.Today.AddDays(-4)));
        handlings.Add(H(_handlingIssues.First(i => i.Source == "Ui-Handle-long-note"), IssueHandlingStatuses.Resolved,
            UiFixtureText.RepeatTo("備註很長的處理說明，包含中英文 mixed content 與 <b>HTML</b> & \"quotes\"。", 520) + UiFixtureText.Hostile));

        // 案件（交辦單成員）：四張交辦單 → 四位處理人（單一帳號、超長顯示名稱、同名不同帳號 ×2）
        var orders = Backend.WorkOrderStore();
        var cases = new List<IssueCase>();
        var caseHandlings = new List<IssueHandling>();
        var handlersByIssue = new[] { "ui-handler", "ui-handler-long", "ui-same-a", "ui-same-b" };
        for (var i = 0; i < handlersByIssue.Length; i++)
        {
            var sig = _caseIssues[i];
            var account = handlersByIssue[i];
            var due = i == 1 ? DateTime.Today.AddDays(-3) : DateTime.Today.AddDays(7);   // 其中一張逾期
            var orderId = orders.Insert(new WorkOrder
            {
                SourceName = sig.Source, EventId = sig.EventId, IssueLabel = sig.SourceEventLabel, HandlerId = UserIds[account],
                Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, AutoAttach = false,
                Note = i == 0 ? "請優先處理，必要時聯絡廠商。" : null, DueDate = due,
                CreatedById = admin, CreatedByAccount = "ui-admin", CreatedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-1)
            });
            WorkOrderIds[account] = orderId;
            orders.AppendEvent(new WorkOrderEvent
            {
                WorkOrderId = orderId, Action = WorkOrderEventActions.Created, ActorId = admin, ActorAccount = "ui-admin",
                MemberDelta = 1, CreatedAt = now.AddDays(-2)
            });

            var caseId = $"uicase{i + 1:D4}";
            cases.Add(new IssueCase
            {
                CaseId = caseId, HostName = hostName, IssueKey = IssueSignatureKey.For(sig), IssueLabel = sig.SourceEventLabel,
                Status = IssueHandlingStatuses.InProgress, HandlerId = UserIds[account], DueDate = due,
                FirstLinkedDate = caseDate.AddDays(-1), LastLinkedDate = caseDate, CreatedAt = now.AddDays(-2),
                CreatedByAccount = "ui-admin", UpdatedAt = now.AddDays(-1), WorkOrderId = orderId,
                SourceName = sig.Source, EventId = sig.EventId, Note = i == 0 ? "已聯絡廠商，等待回覆。" : null
            });
            caseHandlings.Add(new IssueHandling
            {
                HostName = hostName, Date = caseDate, IssueKey = IssueSignatureKey.For(sig), Status = IssueHandlingStatuses.InProgress,
                ActorId = UserIds[account], ActorAccount = account, DueDate = due, CaseId = caseId, UpdatedAt = now.AddDays(-1)
            });
        }

        // 「先前處理過」：第一個案件問題在更早的日子有結案紀錄
        handlings.Add(new IssueHandling
        {
            HostName = hostName, Date = DayOf(27), IssueKey = IssueSignatureKey.For(_caseIssues[0]), Status = IssueHandlingStatuses.Resolved,
            ActorId = handler, ActorAccount = "ui-handler", Note = "上次更換磁碟後解決。", UpdatedAt = DayOf(27).AddHours(15)
        });

        Backend.IssueHandlingStore().SaveMany(handlings.Concat(caseHandlings));
        Backend.IssueCaseStore().SaveMany(cases);
    }

    // ── 收尾 ────────────────────────────────────────────────────────────────

    private void RegisterScenarios()
    {
        void Day(string key, string title) =>
            Scenarios.Add(new UiFixtureScenario($"record.{key}", title,
                $"/records/{Days[key].HostId}/{Days[key].Date:yyyy-MM-dd}"));

        Day("all-kinds", "風險日（高）：8 類別×3 級嚴重度、趨勢四態、次數 0/1/999/1,234,567、抑制、趨勢與關聯告警、完整 AI、深入分析");
        Day("issues-60", "風險日（中）：65 個問題");
        Day("issues-0", "風險日（低）：0 個問題");
        Day("issues-1", "風險日（低）：1 個問題");
        Day("ai-not-generated", "AI 未產生（統計模式）");
        Day("ai-degraded-2", "AI 降級（第 2 次，仍會重試）");
        Day("ai-abandoned-3", "AI 降級（已放棄，連續 3 次格式異常）");
        Day("ai-pending", "AI 待補＋風險報告待補");
        Day("ai-headline-only", "AI 只有 Headline（其餘三段與 Brief 皆空）");
        Day("ai-missing-one", "AI 缺一段（TrendAssessment 空）＋週檢結果");
        Day("ai-missing-two", "AI 缺兩段（TrendAssessment、Action 空）");
        Day("ai-800", "AI 每格 800 字以上（含 Brief）");
        Day("ai-markdown", "AI 內容含 Markdown 表格／清單／$\\rightarrow$／$100");
        Day("data-incomplete", "資料不完整＋Security 未讀取＋未涵蓋項目");
        Day("logsource-unknown", "LogSource=Unknown（舊紀錄）");
        Day("logsource-local", "LogSource=Local");
        Day("logsource-prtg", "LogSource=Prtg（只因 PRTG 建立）＋數值趨勢佐證");
        Day("legacy-messages", "舊版整段訊息值三種（KeyDetails／登入失敗明細的帳號與來源欄）");
        Day("evidence", "佐證：帳號 0/1/50、登入失敗明細 0/1/50 截斷、樣本 1/5/多行、來源觀測 0/1/上限截斷、殘留憑證");
        Day("hostile-long", "HTML/引號、255 字 Source、2,000 字元連續字串、中英混排、白話說明 0/一行/300+/1000 連續字串（產品上限）");
        Day("handling", "處理狀態八態＋觀察到期/進行中逾期/超長備註");
        Day("cases", "進行中案件（4 位處理人：單一帳號、超長名稱、同名不同帳號 ×2）");
        Day("detail-pruned", "DetailPruned=true（詳情已清除）");

        var lastDay = Yesterday.ToString("yyyy-MM-dd");
        void Host(string key, string title) => Scenarios.Add(new UiFixtureScenario($"host.{key}", title, $"/hosts/{HostIds[key]}"));
        Host("win", "主機詳情：標準 Windows（兩個群組含超長名稱、有負責人、核心分級、28 天時間軸）");
        Host("long63", "主機詳情：63 字元主機名稱");
        Host("fqdn", "主機詳情：FQDN 名稱");
        Host("fqdn-long", "主機詳情：長 FQDN 名稱");
        Host("ip-only", "主機詳情：只有 IP 當名稱（顯示名稱 VM-SPRINGBOARD7）");
        Host("fin", "主機詳情：中文長描述（RoleDesc）");
        Host("no-group", "主機詳情：沒有群組");
        Host("disabled", "主機詳情：已停用");
        Host("tomb", "主機詳情：已合併的墓碑（MergedInto）");
        Host("silent", "主機詳情：靜默未回報（最後回報 21 天前）");
        Host("linux", "主機詳情：Linux");
        Host("prtg", "主機詳情：只有 PRTG");
        Host("local", "主機詳情：本機來源");
        Host("hostile", "主機詳情：名稱含 HTML 字元");
        Day2(lastDay);

        void Day2(string yesterday)
        {
            Scenarios.Add(new UiFixtureScenario("record.linux", "Linux 風險日（昨天）", $"/records/{HostIds["linux"]}/{yesterday}"));
            Scenarios.Add(new UiFixtureScenario("record.prtg-only", "只有 PRTG 的主機日（昨天）", $"/records/{HostIds["prtg"]}/{yesterday}"));
            Scenarios.Add(new UiFixtureScenario("record.fin", "中文描述主機日（昨天）", $"/records/{HostIds["fin"]}/{yesterday}"));
        }

        Scenarios.Add(new UiFixtureScenario("page.dashboard", "儀表板", "/"));
        Scenarios.Add(new UiFixtureScenario("page.records", "紀錄總覽（問題／主機視角）", "/records"));
        Scenarios.Add(new UiFixtureScenario("page.work-orders", "交辦單清單", "/work-orders"));
        foreach (var account in new[] { "ui-handler", "ui-handler-long", "ui-same-a", "ui-same-b" })
            Scenarios.Add(new UiFixtureScenario($"work-order.{account}", $"交辦單詳情（處理人 {account}）", $"/work-orders/{WorkOrderIds[account]}"));
        foreach (var account in new[] { "ui-handler", "ui-handler-long", "ui-same-a", "ui-same-b" })
            Scenarios.Add(new UiFixtureScenario($"handler.{account}", $"處理人頁面（{account}）", $"/handlers/{UserIds[account]}"));
        Scenarios.Add(new UiFixtureScenario("admin.hosts", "主機管理", "/admin/hosts"));
        Scenarios.Add(new UiFixtureScenario("admin.groups", "群組管理（主機群組 0 台／很多台／超長名稱）", "/admin/groups"));
        Scenarios.Add(new UiFixtureScenario("admin.users", "使用者管理（長顯示名稱／同名／停用／暫停接單）", "/admin/users"));
        foreach (var account in new[] { "ui-handler-long", "ui-same-a", "ui-disabled", "ui-paused", "ui-hostile" })
            Scenarios.Add(new UiFixtureScenario($"admin.user.{account}", $"使用者詳情（{account}）", $"/admin/users/{UserIds[account]}"));
        Scenarios.Add(new UiFixtureScenario("admin.rules", "規則維護（含自訂白話說明邊界規則）", "/admin/rules"));
        RegisterExtendedScenarios();
    }

    private void WriteScenarioFile()
    {
        var path = Path.Combine(RootDir, "ui-fixture-scenarios.json");
        var payload = new
        {
            variant = Variant.ToString(),
            dataRoot = RootDir,
            storage = new { type = "Sqlite", dataRoot = RootDir },
            yesterday = Yesterday.ToString("yyyy-MM-dd"),
            users = UserIds,
            hostIds = HostIds,
            scenarios = Scenarios.Select(s => new { s.Key, s.Title, s.Path })
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    /// <summary>WAL 併回主檔，讓複製或交給站台時只有一個 .db 檔</summary>
    private void Checkpoint()
    {
        SqliteConnection.ClearAllPools();
        using var conn = new SqliteConnection(StorageBackend.DisableSqlitePoolingIfUnset($"Data Source={DbPath}"));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
