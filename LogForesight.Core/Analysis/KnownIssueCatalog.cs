namespace LogForesight.Core.Analysis;

public enum IssueCategory
{
    Hardware,   // CPU / 記憶體 / 電源
    Storage,    // 磁碟 / 檔案系統
    Security,   // 入侵跡象
    Service,    // 服務異常
    Resource,   // 資源耗盡
    Backup,     // 備份 / 還原能力
    Config,     // 設定性風險：憑證、時間同步、群組原則、網域連線
    Other
}

public enum IssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public class KnownIssueRule
{
    // ── 規則管理欄位（規則外部化，見 docs/RULES-SPEC.md）─────────────────────
    // 規則搬出程式碼後，這幾個欄位是「誰能改這條規則」「怎麼比對順序」「要不要吵」的地基，
    // 詳見 docs/RULES-SPEC.md。內建種子（KnownIssueSeed）會把這些欄位填好，程式碼裡的規則物件
    // 本身維持純資料、不含任何比對邏輯。

    /// <summary>穩定識別鍵，seed 同步與匯入都靠它指名道姓比對。一經出貨（隨版本釋出）永不改名，
    /// 語意大改時應該是「舊 Id 標記 Enabled=false + 新 Id 新增」，而不是重新命名同一個 Id。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>"builtin"（程式內建，seed/匯入會覆寫其內容）或 "custom"（使用者自訂，程式永不覆寫）</summary>
    public string Origin { get; init; } = "builtin";

    /// <summary>停用時 Classify 不會命中此規則（事件歸類為 Other/Low）。
    /// 停用不影響 CorrelationAnalyzer 與 TrendAnalyzer 對同一事件的偵測——見 docs/RULES-SPEC.md 的語意邊界。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>規則生效範圍，為未來多主機/群組規則卡位。此版本只接受 "all"（全域規則）。</summary>
    public string Scope { get; init; } = "all";

    /// <summary>true = 不看 EventIds、只要 SourcePattern 命中來源就算（如 WHEA-Logger 全部事件都要抓）。
    /// 這是顯式宣告，取代舊版「EventIds 空陣列 = 全比對」的隱含語意——正規化儲存後空子表列
    /// 很容易被誤解成「沒有事件」，顯式旗標讓這件事不會被資料遺失悄悄改變語意。</summary>
    public bool MatchAllEventIds { get; init; }

    /// <summary>為未來「同規則同主機下，只關閉部分比對範圍」的抑制粒度卡位，此版本必須為 null。</summary>
    public string? MatchFilter { get; init; }

    /// <summary>"windows"（預設，Event Log 來源＋Event ID 比對）或 "linux"（syslog program＋
    /// 事件名/訊息比對，見 <see cref="ProgramPattern"/> 等三欄位）。docs/LINUX-RULES.md D1：
    /// 雙平台共用同一規則模型與儲存，不分兩套 store。舊 rules.json 沒有這個欄位時，
    /// init 預設值就是「windows」——不需要額外的載入期正規化程式碼。</summary>
    public string Platform { get; init; } = "windows";

    public string SourcePattern { get; init; } = string.Empty;
    public int[] EventIds { get; init; } = Array.Empty<int>();

    // ── Linux 專用比對欄位（docs/LINUX-RULES.md §1.1/§1.2）─────────────────
    // Windows 規則（Platform="windows"）這三欄恆空；Linux 規則的 SourcePattern/EventIds/
    // MatchAllEventIds 恆空/false（由 RuleValidator 平台條件式驗證把關）。
    // 比對語意是兩條路的 OR：EventNamePattern 命中，或 ProgramPattern 命中且
    // （MessagePatterns 為空或其中任一子字串命中）——見 KnownIssueCatalog.FindLinuxRule。

    /// <summary>Linux：syslog identifier／process 名稱比對（如 sshd、sudo、kernel），
    /// 語意與 SourcePattern 相同（不分大小寫 Contains）。</summary>
    public string ProgramPattern { get; init; } = string.Empty;

    /// <summary>Linux：Sentinel 正規化事件名比對（collector 有做正規化的環境用這條路，
    /// 不需要另外比對訊息內容，最穩定）。可空——只靠 ProgramPattern+MessagePatterns 的規則不填。</summary>
    public string EventNamePattern { get; init; } = string.Empty;

    /// <summary>Linux：訊息子字串清單，OR 語意、不分大小寫 Contains，任一命中即算。
    /// 空陣列＝不看訊息內容，ProgramPattern 命中就算（如帳號異動類事件）。
    /// 刻意用子字串而非 regex——人工在 Web 頁維護時所見即所得，見 docs/LINUX-RULES.md §1.2。</summary>
    public string[] MessagePatterns { get; init; } = Array.Empty<string>();

    public IssueCategory Category { get; init; }

    /// <summary>
    /// Severity 不是 init——docs/archive/HISTORY.md #1（B1 三級化）需要在載入時把舊資料的
    /// Severity=Critical 就地正規化為 High（見 <see cref="NormalizeLegacyCriticalSeverity"/>），
    /// 而 KnownIssueRule 是普通 class 不是 record，沒有 `with` 表達式可用。
    /// </summary>
    public IssueSeverity Severity { get; set; }

    /// <summary>
    /// 命中即列為高風險日（docs/archive/HISTORY.md #1，B1 三級化）：三級化前的 Critical
    /// 唯一的實際作用就是這個——LogAnalysisService.ComputeRuleBasedRisk 只要當天命中一條
    /// 未被抑制的旗標規則（或關聯訊號帶旗標），就把當天判定為高風險日。畫面上另以「重大」
    /// 徽章顯性標註（取代原本「嚴重」等級給人的直覺），見 IssueDto/SignatureHitDto.ElevatesDayRisk。
    /// </summary>
    public bool ElevatesDayRisk { get; set; }

    public string Description { get; init; } = string.Empty;

    /// <summary>當日發生次數達到此值才算完整嚴重度，未達則降一級（例如零星的登入失敗屬正常雜訊）</summary>
    public int CountThreshold { get; init; } = 1;

    // ── 白話知識庫（2026-07-20 新增，AI 角色轉換）─────────────────────
    // 規則命中的問題不再呼叫 AI 深入分析，直接渲染這裡的靜態內容：同一 Event ID 的原因與處置
    // 幾乎不變，寫死比每次重新生成更快、更一致、零幻覺，AI 呼叫失敗時也不會從缺。
    // 四個欄位對應 DeepDiveFinding 的 Problem/Impact/LikelyCauses/NextSteps，報告端與 DB 端
    // 因此可用同一個模型類別呈現「規則來的」與「AI 來的」深析結果，不需要區分兩套結構。

    /// <summary>白話說明「這代表什麼」，給不懂 Event Log 的人看</summary>
    public string PlainExplanation { get; init; } = string.Empty;

    /// <summary>不處理會發生什麼</summary>
    public string Impact { get; init; } = string.Empty;

    /// <summary>常見原因，依可能性高低排序</summary>
    public string[] LikelyCauses { get; init; } = Array.Empty<string>();

    /// <summary>具體處置步驟</summary>
    public string[] NextSteps { get; init; } = Array.Empty<string>();

    // ── 修改追蹤（2026-07-21 Web 規則維護頁新增）──────────────────────────
    // null = 從未被人改過。用途有二：規則清單標示「已修改的 builtin」，
    // 以及程式改版時判斷「這條能不能安全地跟進新種子」——
    // 沒被改過的直接跟進，被改過的保留並提示，你的修改永遠不會被靜默蓋掉。

    /// <summary>最後修改者的使用者 Id（Web 維護）；null = 從未被修改</summary>
    public long? ModifiedBy { get; init; }

    public DateTime? ModifiedAt { get; init; }

    /// <summary>
    /// 供規則維護頁「覆蓋已修改的內建規則」（<c>overwriteBuiltin</c>）使用：以「這條種子規則」
    /// 為準複製出新規則，只保留呼叫端指定的 <paramref name="enabled"/>（使用者停用某條 builtin
    /// 是操作決定，不是「內容被改過」，匯入不該把它悄悄打開）。修改追蹤欄位刻意不複製——
    /// 覆蓋後這條規則就等於原廠內容，不再是「已修改的 builtin」。
    ///
    /// **為什麼放在這裡而不是 <see cref="RuleImportPlanner"/>**：逐欄複製散在別的檔案時，
    /// 新增規則欄位的人不會想到要去同步它，而漏抄的後果是靜默的行為降級——曾經漏抄
    /// <see cref="ElevatesDayRisk"/>（覆蓋後「重大」旗標被清成 false，該規則從此不再把當天
    /// 判定為高風險日）。複製邏輯貼著欄位宣告放，新增欄位時才看得到它。
    /// </summary>
    public KnownIssueRule CloneForSeedOverwrite(bool enabled) => new()
    {
        Id = Id,
        Origin = Origin,
        Enabled = enabled,
        Scope = Scope,
        Platform = Platform,
        MatchAllEventIds = MatchAllEventIds,
        MatchFilter = MatchFilter,
        SourcePattern = SourcePattern,
        EventIds = EventIds,
        ProgramPattern = ProgramPattern,
        EventNamePattern = EventNamePattern,
        MessagePatterns = MessagePatterns,
        Category = Category,
        Severity = Severity,
        ElevatesDayRisk = ElevatesDayRisk,
        Description = Description,
        CountThreshold = CountThreshold,
        PlainExplanation = PlainExplanation,
        Impact = Impact,
        LikelyCauses = LikelyCauses,
        NextSteps = NextSteps
    };
}

/// <summary>
/// 已知危險訊號的規則表。偵測「已知模式」交給確定性規則，不依賴 AI 模型的召回率；
/// AI 負責綜合判讀、趨勢比對與規則未涵蓋的新型態問題（見 docs/archive/HISTORY.md）。
///
/// 2026-07-21 規則外部化（見 docs/RULES-SPEC.md）：規則表不再是唯讀的程式碼常數，改由
/// <see cref="KnownIssueSeed"/> 提供內建種子，初次部署時寫入外部儲存（rules.json 概念上的
/// DB blob），之後在儲存端維護。<see cref="Rules"/> 預設等於內建種子（單元測試、selftest 的降級路徑
/// 不呼叫 <see cref="Initialize"/> 時，行為與規則外部化之前完全相同）；正常啟動流程由
/// RuleBootstrapper 載入、驗證後呼叫 Initialize 覆寫。
/// </summary>
public static class KnownIssueCatalog
{
    public static List<KnownIssueRule> Rules { get; private set; } = KnownIssueSeed.CreateRules();

    /// <summary>
    /// Security log 的 SuccessAudit 量極大，只挑高價值事件納入分析——2026-07-21 起改為推導結果，
    /// 不再是獨立維護的清單：凡是啟用中、來源可能命中 Security-Auditing 的規則，其 EventIds
    /// 聯集就是 watchlist。規則表新增一條 Security 規則時，watchlist 自動涵蓋，不需要另外記得
    /// 同步（原本寫死的清單容易漏改，見 docs/RULES-SPEC.md 的陷阱說明）。
    /// </summary>
    public static HashSet<int> SecurityAuditWatchlist { get; private set; } = DeriveWatchlist(Rules, ChannelCatalog.SecurityProbe);

    /// <summary>
    /// 各 Operational／Security 頻道的 watchlist（key = <see cref="ChannelPolicy.ProviderProbe"/>）。
    /// 與 <see cref="SecurityAuditWatchlist"/> 同一套推導機制，只是擴為多頻道：新增一條 Defender/RDP
    /// 規則時，對應頻道的 watchlist 自動涵蓋其 EventIds，不需要另外記得同步。
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<int>> ChannelWatchlists { get; private set; } = DeriveChannelWatchlists(Rules);

    /// <summary>
    /// 用已驗證過的規則清單覆寫目前生效的規則表，並重新推導所有頻道的 watchlist。
    /// 傳入的清單應已經過 <see cref="RuleValidator"/> 驗證；只有 <see cref="KnownIssueRule.Enabled"/>
    /// 為 true 的規則會真正參與比對——停用規則保留在儲存端供人查閱，但不影響 Classify/FindRule。
    /// </summary>
    public static void Initialize(List<KnownIssueRule> validatedRules)
    {
        Rules = validatedRules.Where(r => r.Enabled).ToList();
        SecurityAuditWatchlist = DeriveWatchlist(Rules, ChannelCatalog.SecurityProbe);
        ChannelWatchlists = DeriveChannelWatchlists(Rules);
    }

    /// <summary>
    /// 某頻道（以 provider 探測字串指名）是否把這個 EventId 納入 watchlist——供 EventLogService
    /// 判斷 Operational 頻道的 Information 等級事件要不要收。
    /// </summary>
    public static bool IsWatched(string providerProbe, int eventId) =>
        ChannelWatchlists.TryGetValue(providerProbe, out var watchlist) && watchlist.Contains(eventId);

    /// <summary>某頻道是否有任何 watchlist 事件（用來提示「頻道已啟用但規則表沒有對應規則」）。</summary>
    public static bool HasWatchlist(string providerProbe) =>
        ChannelWatchlists.TryGetValue(providerProbe, out var watchlist) && watchlist.Count > 0;

    private static Dictionary<string, HashSet<int>> DeriveChannelWatchlists(List<KnownIssueRule> rules)
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in ChannelCatalog.Defaults)
        {
            // ErrorWarningOnly 頻道靠 Error/Warning 等級收取，不需要 watchlist；沒有探測字串也無從推導。
            if (policy.Kind == ChannelInclusionKind.ErrorWarningOnly || string.IsNullOrEmpty(policy.ProviderProbe))
            {
                continue;
            }
            map[policy.ProviderProbe] = DeriveWatchlist(rules, policy.ProviderProbe);
        }
        return map;
    }

    /// <summary>
    /// 推導單一頻道的 watchlist：真正的比對邏輯（<see cref="FindRule"/>）是
    /// `實際來源.Contains(規則.SourcePattern)`，所以這裡反過來看——「這條規則的 SourcePattern 會不會
    /// 命中此頻道的事件」用該頻道的 provider 探測字串驗證即可，不需要另外維護一份「這條是不是某頻道規則」
    /// 的判斷邏輯。凡命中且非 MatchAllEventIds 的啟用規則，其 EventIds 併入 watchlist。
    /// </summary>
    private static HashSet<int> DeriveWatchlist(List<KnownIssueRule> rules, string providerProbe)
    {
        var watchlist = new HashSet<int>();
        if (string.IsNullOrEmpty(providerProbe))
        {
            return watchlist;
        }

        foreach (var rule in rules)
        {
            // Linux 規則沒有頻道 watchlist 的概念（見 docs/LINUX-RULES.md §4.2）；
            // 顯式排除，不依賴「SourcePattern 空字串 Contains 恆真但 EventIds 恆空」的隱含行為。
            if (rule.Platform != "windows")
            {
                continue;
            }

            bool sourceMatches = providerProbe.Contains(rule.SourcePattern, StringComparison.OrdinalIgnoreCase);
            if (!sourceMatches || rule.MatchAllEventIds)
            {
                continue;
            }

            foreach (var id in rule.EventIds)
            {
                watchlist.Add(id);
            }
        }

        return watchlist;
    }

    /// <summary>
    /// 依 (Source, EventId) 找出命中的 Windows 規則（不含次數門檻判斷）。與 Classify 共用比對邏輯，
    /// 供報告端在規則命中類別直接渲染靜態知識內容，不需要重新呼叫 AI 深入分析。
    /// 明確只看 Platform="windows" 規則——Linux 規則的 SourcePattern 恆空，
    /// 顯式排除比依賴「空字串 Contains 恆真但 EventIds 恆空」的隱含行為清楚（docs/LINUX-RULES.md §1.2）。
    /// </summary>
    public static KnownIssueRule? FindRule(string source, int eventId)
    {
        foreach (var rule in Rules)
        {
            if (rule.Platform != "windows")
            {
                continue;
            }

            bool sourceMatch = source.Contains(rule.SourcePattern, StringComparison.OrdinalIgnoreCase);
            bool idMatch = rule.MatchAllEventIds || rule.EventIds.Contains(eventId);

            if (sourceMatch && idMatch)
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// 依 (program, 正規化事件名, 訊息) 找出命中的 Linux 規則（不含次數門檻判斷）。
    /// 比對語意（docs/LINUX-RULES.md §1.2）是兩條路的 OR，讓同一份種子同時支援
    /// 「Sentinel 有做事件正規化」與「只收到 raw syslog」兩種環境：
    ///   (a) EventNamePattern 非空且命中該事件的正規化事件名
    ///   (b) ProgramPattern 非空且命中 program，且 MessagePatterns 為空或其中任一子字串命中訊息
    /// </summary>
    public static KnownIssueRule? FindLinuxRule(string program, string? eventName, string message)
    {
        foreach (var rule in Rules)
        {
            if (rule.Platform != "linux")
            {
                continue;
            }

            bool eventNameMatch = !string.IsNullOrEmpty(rule.EventNamePattern) &&
                !string.IsNullOrEmpty(eventName) &&
                eventName.Contains(rule.EventNamePattern, StringComparison.OrdinalIgnoreCase);

            bool programMatch = !string.IsNullOrEmpty(rule.ProgramPattern) &&
                program.Contains(rule.ProgramPattern, StringComparison.OrdinalIgnoreCase) &&
                (rule.MessagePatterns.Length == 0 ||
                 rule.MessagePatterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase)));

            if (eventNameMatch || programMatch)
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>用規則表為聚合後的事件簽章標記類別、嚴重度與命中的規則 Id</summary>
    public static void Classify(LogIssueSignature signature)
    {
        var rule = FindRule(signature.Source, signature.EventId);
        if (rule != null)
        {
            signature.Category = rule.Category;
            var thresholdMet = signature.Count >= rule.CountThreshold;
            signature.Severity = thresholdMet ? rule.Severity : Downgrade(rule.Severity);
            // 未達次數門檻時降級，旗標也跟著不算——舊制下「降到 High、沒到 Critical」本來就不會
            // 讓當天判定成高風險日，旗標要複製同一個行為（docs/archive/HISTORY.md #1）
            signature.ElevatesDayRisk = thresholdMet && rule.ElevatesDayRisk;
            signature.KnownIssue = rule.Description;
            signature.RuleId = rule.Id;
            return;
        }

        signature.Category = IssueCategory.Other;
        signature.Severity = IssueSeverity.Low;
        signature.ElevatesDayRisk = false;
    }

    private static IssueSeverity Downgrade(IssueSeverity s) =>
        s == IssueSeverity.Low ? IssueSeverity.Low : s - 1;

    /// <summary>
    /// 舊資料相容（docs/archive/HISTORY.md #1，B1 三級化）：規則檔裡若還存著三級化之前
    /// 寫入的 Severity=Critical（內建種子已遷移，但使用者自訂規則或舊部署的 rules.json 可能還是舊值），
    /// 載入時一律正規化為 High＋ElevatesDayRisk=true——Critical 原本唯一的實際作用就是這個，
    /// 正規化後行為不變。只在載入時於記憶體正規化，不覆寫原檔（與規則檔容錯設計一致，
    /// 見 docs/RULES-SPEC.md 陷阱 3；下次使用者透過 Web 儲存該規則時會自然寫回正規化後的值）。
    /// </summary>
    public static void NormalizeLegacyCriticalSeverity(IEnumerable<KnownIssueRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Severity == IssueSeverity.Critical)
            {
                rule.Severity = IssueSeverity.High;
                rule.ElevatesDayRisk = true;
            }
        }
    }
}
