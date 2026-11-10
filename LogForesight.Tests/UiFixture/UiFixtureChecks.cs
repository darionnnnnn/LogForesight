using LogForesight.Web.Auth;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// UI 驗收資料集的矩陣格子：每一格一個具名斷言，失敗訊息由測試類補上格子名稱。
/// 讀回方式以「走產品的 store」為主（主機、使用者、處理狀態、案件、規則），
/// 分析紀錄內文才直接讀列的 ContentJson（與 <c>EfAnalysisRecordStore</c> 同一套序列化）。
/// </summary>
internal sealed class UiFixtureChecks
{
    private readonly UiFixtureDataSet _d;
    private readonly HostStore _hosts;
    private readonly UserStore _users;
    private readonly UserGroupStore _userGroups;

    public UiFixtureChecks(UiFixtureDataSet d)
    {
        _d = d;
        _hosts = new HostStore(d.Backend.Blob("hosts"));
        _users = new UserStore(d.Backend.Blob("users"), d.Backend.Blob("user_last_login"));
        _userGroups = new UserGroupStore(d.Backend.Blob("user_groups"));
    }

    // ── 讀回 helper ─────────────────────────────────────────────────────────

    public WebHost Host(string key) => _hosts.Get(_d.HostIds[key]) ?? throw new Xunit.Sdk.XunitException($"主機 {key} 不存在");

    public DailyRecordRow Row(string dayKey)
    {
        var (hostId, date) = _d.Days[dayKey];
        using var ctx = _d.Backend.CreateContext();
        return ctx.DailyRecords.AsNoTracking().Single(r => r.HostId == hostId && r.RecordDate == date.Date);
    }

    public DailyAnalysisRecord Rec(string dayKey)
    {
        var row = Row(dayKey);
        var rec = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)
                  ?? throw new Xunit.Sdk.XunitException($"情境日 {dayKey} 的 ContentJson 無法反序列化");
        rec.AiPending = row.AiPending;
        return rec;
    }

    public List<DailyRecordRow> RowsOf(string hostKey)
    {
        using var ctx = _d.Backend.CreateContext();
        var id = _d.HostIds[hostKey];
        return ctx.DailyRecords.AsNoTracking().Where(r => r.HostId == id).ToList();
    }

    public LogIssueSignature Issue(string dayKey, string sourceStartsWith, int? eventId = null) =>
        Rec(dayKey).TopIssues.FirstOrDefault(i => i.Source.StartsWith(sourceStartsWith, StringComparison.Ordinal) && (eventId == null || i.EventId == eventId))
        ?? throw new Xunit.Sdk.XunitException($"情境日 {dayKey} 找不到問題 {sourceStartsWith} {eventId}");

    public List<KnownIssueRule> Rules() => KnownIssueCatalog.ResolveRules(new KnownIssueRuleStore(_d.Backend.Blob("rules")));

    public string? Explanation(string source, int eventId) => KnownIssueCatalog.PlainExplanationFor(Rules(), source, eventId);

    public WebUser User(string account) => _users.FindByAccount(account) ?? throw new Xunit.Sdk.XunitException($"帳號 {account} 不存在");

    public IReadOnlySet<Capability> Capabilities(string account)
    {
        var user = User(account);
        var roles = _userGroups.GetAll().Where(g => g.Active && user.GroupIds.Contains(g.GroupId)).Select(g => g.Role);
        return RoleCapabilityMap.For(roles);
    }

    /// <summary>受限使用者的可見主機（部門群組 → 主機群組授權；不含負責人與案件授與兩條路徑）</summary>
    public HashSet<long> GroupVisibleHostIds(string account)
    {
        var user = User(account);
        var access = new GroupAccessStore(_d.Backend.Blob("group_access")).GetAll();
        var hostGroups = access.Where(a => user.GroupIds.Contains(a.UserGroupId)).Select(a => a.HostGroupId).ToHashSet();
        return _hosts.GetAll().Where(h => h.Active && h.GroupIds.Any(hostGroups.Contains)).Select(h => h.HostId).ToHashSet();
    }

    private static void True(bool condition, string message) => Assert.True(condition, message);

    // ── 矩陣 ────────────────────────────────────────────────────────────────

    public static readonly Dictionary<string, Action<UiFixtureChecks>> Cells = new()
    {
        // ═══ 主機 ═══
        ["主機.Windows"] = c => { var h = c.Host("win"); Assert.Equal(WebHost.OsWindows, h.Os); },
        ["主機.Linux"] = c =>
        {
            Assert.Equal(WebHost.OsLinux, c.Host("linux").Os);
            True(c.RowsOf("linux").Count > 0, "Linux 主機沒有任何紀錄");
        },
        ["主機.只有PRTG"] = c =>
        {
            var rows = c.RowsOf("prtg");
            True(rows.Count > 0, "PRTG 主機沒有紀錄");
            True(rows.All(r => r.LogSource == (short)AnalysisLogSource.Prtg), "PRTG 主機的紀錄應全部來自 PRTG");
        },
        ["主機.名稱63字元"] = c => Assert.Equal(63, c.Host("long63").HostName.Length),
        ["主機.FQDN"] = c => True(c.Host("fqdn").HostName.Count(ch => ch == '.') >= 2 && c.Host("fqdn-long").HostName.Count(ch => ch == '.') >= 4, "FQDN 名稱點數不足"),
        ["主機.只有IP當名稱"] = c =>
        {
            var h = c.Host("ip-only");
            Assert.True(IpSyntax.IsStrictIpv4(h.HostName), "名稱應為 IP");
            Assert.False(string.IsNullOrEmpty(h.DisplayName));
        },
        ["主機.中文描述"] = c => True(c.Host("fin").RoleDesc.Length > 60 && c.Host("fin").RoleDesc.Any(ch => ch > 0x4E00), "中文長描述不足"),
        ["主機.無群組"] = c => { var h = c.Host("no-group"); Assert.Empty(h.GroupIds); Assert.True(h.Active); },
        ["主機.停用"] = c => { var h = c.Host("disabled"); Assert.False(h.Active); Assert.Null(h.MergedInto); },
        ["主機.已合併墓碑"] = c =>
        {
            var h = c.Host("tomb");
            Assert.Equal(c._d.HostIds["win"], h.MergedInto);
            Assert.False(h.Active);
            True(c.RowsOf("tomb").Count > 0, "墓碑沒有歷史紀錄");
        },
        ["主機.靜默未回報"] = c =>
        {
            var h = c.Host("silent");
            Assert.True(h.Active);
            Assert.True(h.LastReportAt < DateTime.Today.AddDays(-14), "LastReportAt 應很久以前");
            Assert.True(c.RowsOf("silent").Max(r => r.RecordDate) <= DateTime.Today.AddDays(-14), "最後一筆紀錄應很久以前");
        },
        ["主機.名稱含HTML字元"] = c => True(c.Host("hostile").HostName.Contains('<') && c.Host("hostile").HostName.Contains('&'), "名稱應含 < 與 &"),
        ["主機.群組0台"] = c =>
        {
            var id = c._d.HostGroupIds["empty"];
            Assert.DoesNotContain(c._hosts.GetAll(), h => h.GroupIds.Contains(id));
        },
        ["主機.群組很多台"] = c =>
        {
            var id = c._d.HostGroupIds["bulk"];
            True(c._hosts.GetAll().Count(h => h.GroupIds.Contains(id)) >= 40, "大群組應有 40 台以上");
        },
        ["主機.群組超長名稱"] = c =>
        {
            var name = new HostGroupStore(c._d.Backend.Blob("host_groups")).Get(c._d.HostGroupIds["longname"])!.GroupName;
            True(name.Length > 50, "超長群組名稱");
        },

        // ═══ 風險日 ═══
        ["風險日.高中低三級"] = c =>
        {
            using var ctx = c._d.Backend.CreateContext();
            var levels = ctx.DailyRecords.Select(r => r.RiskLevel).Distinct().ToList();
            Assert.Contains(RiskLevels.High, levels);
            Assert.Contains(RiskLevels.Medium, levels);
            Assert.Contains(RiskLevels.Low, levels);
        },
        ["風險日.0個問題"] = c => Assert.Empty(c.Rec("issues-0").TopIssues),
        ["風險日.1個問題"] = c => Assert.Single(c.Rec("issues-1").TopIssues),
        ["風險日.60個以上問題"] = c => True(c.Rec("issues-60").TopIssues.Count >= 60, "問題數不足 60"),
        ["風險日.詳情已清除"] = c =>
        {
            var row = c.Row("detail-pruned");
            Assert.True(row.DetailPruned);
            Assert.Equal(string.Empty, row.ContentJson);
        },
        ["風險日.LogSource.Netiq"] = c => Assert.Equal((short)AnalysisLogSource.Netiq, c.Row("all-kinds").LogSource),
        ["風險日.LogSource.Local"] = c => Assert.Equal((short)AnalysisLogSource.Local, c.Row("logsource-local").LogSource),
        ["風險日.LogSource.Prtg"] = c => Assert.Equal((short)AnalysisLogSource.Prtg, c.Row("logsource-prtg").LogSource),
        ["風險日.LogSource.Unknown"] = c => Assert.Equal((short)AnalysisLogSource.Unknown, c.Row("logsource-unknown").LogSource),
        ["風險日.資料不完整"] = c =>
        {
            var r = c.Rec("data-incomplete");
            Assert.True(r.DataIncomplete);
            Assert.False(r.SecurityLogAvailable);
            True(r.UncoveredChecks.Count >= 3, "未涵蓋項目不足");
            True(c.Row("data-incomplete").DataIncomplete, "抽出欄 DataIncomplete 應同步");
        },
        ["風險日.趨勢告警與結構化版"] = c =>
        {
            var r = c.Rec("all-kinds");
            Assert.NotEmpty(r.TrendAlerts);
            Assert.Equal(r.TrendAlerts.Count, r.TrendAlertRefs.Count);
            Assert.Contains(r.TrendAlertRefs, x => x.Kind == TrendAlertKinds.Signature && x.IssueKey != null);
            Assert.Contains(r.TrendAlertRefs, x => x.Kind == TrendAlertKinds.VolumeError);
            Assert.Contains(r.TrendAlertRefs, x => x.Kind == TrendAlertKinds.VolumeAudit);
        },
        ["風險日.關聯告警與結構化版"] = c =>
        {
            var r = c.Rec("all-kinds");
            Assert.NotEmpty(r.CorrelationAlerts);
            Assert.Equal(r.CorrelationAlerts.Count, r.CorrelationAlertRefs.Count);
        },
        ["風險日.已抑制的告警"] = c =>
        {
            var r = c.Rec("all-kinds");
            Assert.NotEmpty(r.SuppressedTrendAlerts);
            Assert.NotEmpty(r.SuppressedCorrelationAlerts);
        },

        // ═══ AI 摘要 ═══
        ["AI.未產生"] = c => { var r = c.Rec("ai-not-generated"); Assert.False(r.AiAnalyzed); Assert.False(r.AiPending); },
        ["AI.成功"] = c => { var r = c.Rec("all-kinds"); Assert.True(r.AiAnalyzed); Assert.False(r.AiPending); Assert.NotEmpty(r.Summary); },
        ["AI.降級.2次"] = c =>
        {
            Assert.Equal(2, c.Row("ai-degraded-2").AiDegradedAttempts);
            Assert.EndsWith("（AI 摘要待補，第 2 次）", c.Rec("ai-degraded-2").Headline);
        },
        ["AI.降級.放棄"] = c =>
        {
            Assert.Equal(3, c.Row("ai-abandoned-3").AiDegradedAttempts);
            Assert.EndsWith(AiDegradation.AbandonedSuffix, c.Rec("ai-abandoned-3").Headline);
        },
        ["AI.待補"] = c => { Assert.True(c.Row("ai-pending").AiPending); Assert.True(c.Rec("ai-pending").RiskReportPending); },
        ["AI.只有Headline"] = c =>
        {
            var r = c.Rec("ai-headline-only");
            Assert.NotEmpty(r.Headline);
            Assert.Empty(r.Summary); Assert.Empty(r.TrendAssessment); Assert.Empty(r.Action); Assert.Empty(r.Brief);
        },
        ["AI.缺一段"] = c =>
        {
            var r = c.Rec("ai-missing-one");
            Assert.Empty(r.TrendAssessment); Assert.NotEmpty(r.Summary); Assert.NotEmpty(r.Action);
        },
        ["AI.缺兩段"] = c =>
        {
            var r = c.Rec("ai-missing-two");
            Assert.Empty(r.TrendAssessment); Assert.Empty(r.Action); Assert.NotEmpty(r.Summary);
        },
        ["AI.單格800字以上"] = c =>
        {
            var r = c.Rec("ai-800");
            foreach (var (name, text) in new[] { ("Summary", r.Summary), ("TrendAssessment", r.TrendAssessment), ("Action", r.Action), ("Brief", r.Brief) })
                True(text.Length >= 800, $"{name} 只有 {text.Length} 字");
            True(r.Headline.Length >= 200, "標題應很長");
        },
        ["AI.Brief有"] = c => Assert.NotEmpty(c.Rec("all-kinds").Brief),
        ["AI.Brief無"] = c => Assert.Empty(c.Rec("ai-missing-one").Brief),
        ["AI.內容含Markdown表格清單箭頭與錢字號"] = c =>
        {
            var r = c.Rec("ai-markdown");
            Assert.Contains("|---|", r.Summary);
            Assert.Contains("- 第一項", r.Summary);
            Assert.Contains("$\\rightarrow$", r.Summary + r.Action);
            Assert.Contains("$100", r.Summary);
        },
        ["AI.DeepDives有"] = c => Assert.True(c.Rec("all-kinds").DeepDives.Count >= 2),
        ["AI.DeepDives無"] = c => Assert.Empty(c.Rec("ai-headline-only").DeepDives),

        // ═══ 問題列 ═══
        ["問題列.八種類別"] = c =>
            Assert.Equal(Enum.GetValues<IssueCategory>().Length, c.Rec("all-kinds").TopIssues.Select(i => i.Category).Distinct().Count()),
        ["問題列.三級嚴重度"] = c =>
        {
            var sev = c.Rec("all-kinds").TopIssues.Select(i => i.Severity).Distinct().ToList();
            Assert.Contains(IssueSeverity.Low, sev); Assert.Contains(IssueSeverity.Medium, sev); Assert.Contains(IssueSeverity.High, sev);
        },
        ["問題列.ElevatesDayRisk"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.ElevatesDayRisk),
        ["問題列.已抑制"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Suppressed),
        ["問題列.KnownIssue有"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => !string.IsNullOrEmpty(i.KnownIssue)),
        ["問題列.KnownIssue無"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => string.IsNullOrEmpty(i.KnownIssue)),
        ["問題列.FirstSeen與LastSeen"] = c =>
        {
            var issues = c.Rec("all-kinds").TopIssues;
            Assert.Contains(issues, i => i.FirstSeen == "00:00" && i.LastSeen == "23:59");
            Assert.Contains(issues, i => i.FirstSeen == i.LastSeen);
        },

        // ═══ 問題內文 ═══
        ["內文.白話說明0字"] = c =>
        {
            // 沒有規則命中：白話說明為 null、沒有 RuleId、因此也沒有處置參考面板
            Assert.Null(c.Explanation("UiFixture-Explain-Empty", 9001));
            Assert.Null(c.Issue("hostile-long", "UiFixture-Explain-Empty").RuleId);
        },
        ["內文.白話說明一行"] = c =>
        {
            var text = c.Explanation("UiFixture-Explain-OneLine", 9002);
            True(text != null && text.Length > 0 && !text.Contains('\n') && text.Length < 60, "應為一行短說明");
        },
        ["內文.白話說明超過300字"] = c => True((c.Explanation("UiFixture-Explain-Long", 9003)?.Length ?? 0) > 300, "說明應超過 300 字"),
        ["內文.白話說明1000字元連續字串"] = c =>
        {
            var text = c.Explanation("UiFixture-Explain-Blob", 9004);
            Assert.Equal(1000, text?.Length);
            Assert.DoesNotContain(' ', text!);
        },
        ["內文.無空白2000字元於樣本訊息"] = c =>
        {
            var sample = c.Issue("hostile-long", "UiFixture-Explain-Blob").SampleMessages.Single();
            Assert.Equal(2000, sample.Length);
            Assert.DoesNotContain(' ', sample);
        },
        ["內文.Source255字元"] = c =>
        {
            var i = c.Issue("hostile-long", "UiFixture-LongSource-");
            Assert.Equal(255, i.Source.Length);
            Assert.DoesNotContain(' ', i.Source);
        },
        ["內文.中英混排長句"] = c =>
        {
            var text = c.Explanation("UiFixture-Explain-Mixed", 9007) ?? "";
            True(text.Length > 300 && text.Contains("Windows Server") && text.Contains("service") && text.Any(ch => ch > 0x4E00), "應為中英混排長句");
        },
        ["內文.含script與符號的字串"] = c =>
        {
            var r = c.Rec("hostile-long");
            var hostile = c.Issue("hostile-long", "UiFixture-Explain-Hostile");
            foreach (var s in new[] { r.Headline, r.Summary, r.Action, hostile.KnownIssue ?? "", hostile.SampleMessages[0], c.Explanation("UiFixture-Explain-Hostile", 9005) ?? "" })
            {
                Assert.Contains("<script>alert(1)</script>", s);
                Assert.Contains("&", s); Assert.Contains("\"", s); Assert.Contains("'", s);
            }
            Assert.Contains(r.TopIssues, i => i.Source.Contains("<script>"));
        },

        // ═══ 佐證 ═══
        ["佐證.帳號0個"] = c => { var i = c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4776); Assert.Empty(i.KeyAccounts!); Assert.Null(i.KeyDetails); },
        ["佐證.帳號1個"] = c => Assert.Single(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4771).KeyAccounts!),
        ["佐證.帳號50個"] = c => Assert.Equal(50, c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4625).KeyAccounts!.Count),
        ["佐證.舊版整段訊息1"] = c => LegacyCheck(c, 4728, UiFixtureText.Legacy1),
        ["佐證.舊版整段訊息2"] = c => LegacyCheck(c, 4719, UiFixtureText.Legacy2),
        ["佐證.舊版整段訊息3帳號與來源欄"] = c =>
        {
            LegacyCheck(c, 4625, UiFixtureText.Legacy3);
            var d = c.Issue("legacy-messages", "Microsoft-Windows-Security-Auditing", 4625).LoginFailureDetails!;
            Assert.Contains(d, x => x.Account == UiFixtureText.Legacy3 && x.Source == UiFixtureText.Legacy3Source);
        },
        ["佐證.登入失敗明細0組"] = c => Assert.Empty(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4776).LoginFailureDetails!),
        ["佐證.登入失敗明細1組"] = c => Assert.Single(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4771).LoginFailureDetails!),
        ["佐證.登入失敗明細50組且截斷"] = c =>
        {
            var i = c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4625);
            Assert.Equal(50, i.LoginFailureDetails!.Count);
            Assert.True(i.LoginFailureDetailsTruncated);
            True(i.LoginFailureTotalCount > i.LoginFailureDetails.Sum(d => d.Count) - 1, "總次數應大於等於明細加總");
        },
        ["佐證.樣本1則"] = c => Assert.Single(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4771).SampleMessages),
        ["佐證.樣本5則"] = c => Assert.Equal(5, c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4624).SampleMessages.Count),
        ["佐證.樣本多行"] = c => Assert.Contains(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4625).SampleMessages, m => m.Contains('\n')),
        ["佐證.相異訊息數大於1"] = c => True(c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4625).DistinctMessageCount > 1, "DistinctMessageCount 應大於 1"),
        ["佐證.來源觀測0筆"] = c => Assert.Empty(c.Issue("evidence", "disk", 7).SourceObservations),
        ["佐證.來源觀測1筆"] = c => Assert.Single(c.Issue("evidence", "disk", 11).SourceObservations),
        ["佐證.來源觀測上限且截斷"] = c =>
        {
            // 站台對來源觀測有 64 筆與 8 KB 兩道上限，單筆序列化約 330 位元組，8 KB 先到
            var i = c.Issue("evidence", "disk", 51);
            True(i.SourceObservations.Count >= 10 && i.SourceObservations.Count < 64, $"來源觀測應塞到 8 KB 上限（實得 {i.SourceObservations.Count} 筆）");
            Assert.True(i.SourceObservationsTruncated);
        },
        ["佐證.殘留憑證判定"] = c =>
        {
            var i = c.Issue("evidence", "Microsoft-Windows-Security-Auditing", 4740);
            Assert.True(i.ResidualCredentialRetry);
            Assert.False(string.IsNullOrWhiteSpace(i.ResidualCredentialBasis));
        },
        ["佐證.PRTG趨勢佐證"] = c =>
        {
            var i = c.Issue("logsource-prtg", "PRTG:disk_free_trend");
            Assert.NotNull(i.PrtgTrendEvidence);
            Assert.Equal("disk_free_trend", i.PrtgTrendEvidence!.RuleCode);
            Assert.Equal("disk", i.PrtgSensorCategory);
        },

        // ═══ 趨勢 ═══
        ["趨勢.首次出現"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Trend == IssueTrend.New && i.PreviousDayCount == 0),
        ["趨勢.重複發生"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Trend == IssueTrend.Recurring && i.DaysSeenInHistory > 0),
        ["趨勢.頻率上升"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Trend == IssueTrend.Rising && i.HistoryDailyAverage.HasValue),
        ["趨勢.無基準"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues,
            i => i.Trend == IssueTrend.Unknown && i.PreviousDayCount == null && i.HistoryDailyAverage == null),
        ["趨勢.次數0"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Count == 0),
        ["趨勢.次數1"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Count == 1),
        ["趨勢.次數999"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Count == 999),
        ["趨勢.次數1234567"] = c => Assert.Contains(c.Rec("all-kinds").TopIssues, i => i.Count == 1_234_567),

        // ═══ 處理狀態 ═══
        ["處理狀態.八態皆有"] = c =>
        {
            var (_, date) = c._d.Days["handling"];
            var got = c._d.Backend.IssueHandlingStore().GetForDay(c.Host("win").HostName, date).Select(h => h.Status).ToHashSet();
            foreach (var status in IssueHandlingStatuses.All) Assert.Contains(status, got);
        },
        ["處理狀態.逾期"] = c =>
        {
            var (_, date) = c._d.Days["handling"];
            Assert.Contains(c._d.Backend.IssueHandlingStore().GetForDay(c.Host("win").HostName, date),
                h => h.Status == IssueHandlingStatuses.InProgress && h.DueDate < DateTime.Today);
        },
        ["處理狀態.觀察到期"] = c =>
        {
            var (_, date) = c._d.Days["handling"];
            var all = c._d.Backend.IssueHandlingStore().GetForDay(c.Host("win").HostName, date);
            Assert.Contains(all, h => IssueHandlingStatuses.IsObservationExpired(h.Status, h.DueDate, DateTime.Today));
            Assert.Contains(all, h => IssueHandlingStatuses.IsObservationActive(h.Status, h.DueDate, DateTime.Today));
        },
        ["處理狀態.超長備註"] = c =>
        {
            var (_, date) = c._d.Days["handling"];
            Assert.Contains(c._d.Backend.IssueHandlingStore().GetForDay(c.Host("win").HostName, date), h => (h.Note?.Length ?? 0) > 500);
        },
        ["處理狀態.先前處理過"] = c =>
        {
            var h = c._d.Backend.IssueHandlingStore().GetMany(new[] { c.Host("win").HostName }, DateTime.MinValue, DateTime.Today.AddDays(-23));
            Assert.Contains(h, x => IssueHandlingStatuses.IsClosed(x.Status));
        },
        ["處理狀態.進行中案件處理人ui_handler"] = c =>
        {
            var open = c._d.Backend.IssueCaseStore().GetOpenByHandler(c._d.UserIds["ui-handler"]);
            True(open.Count >= 1, "ui-handler 沒有進行中案件");
            Assert.All(open, x => Assert.NotNull(x.WorkOrderId));
        },
        ["處理狀態.案件處理人超長顯示名稱"] = c =>
        {
            var u = c.User("ui-handler-long");
            True(u.DisplayName.Length > 30, "顯示名稱應超長");
            True(c._d.Backend.IssueCaseStore().GetOpenByHandler(u.UserId).Count >= 1, "超長名稱處理人沒有案件");
        },
        ["處理狀態.同名不同帳號的兩個使用者"] = c =>
        {
            var a = c.User("ui-same-a"); var b = c.User("ui-same-b");
            Assert.Equal(a.DisplayName, b.DisplayName);
            Assert.NotEqual(a.UserId, b.UserId);
            True(c._d.Backend.IssueCaseStore().GetOpenByHandler(a.UserId).Count >= 1 && c._d.Backend.IssueCaseStore().GetOpenByHandler(b.UserId).Count >= 1,
                "兩個同名使用者都要有案件");
        },
        ["處理狀態.交辦單逾期"] = c =>
        {
            var o = c._d.Backend.WorkOrderStore().Get(c._d.WorkOrderIds["ui-handler-long"]);
            Assert.True(o?.DueDate < DateTime.Today);
        },

        // ═══ 使用者與群組 ═══
        ["使用者.三個角色帳號存在"] = c =>
        {
            Assert.Contains(Capability.Maintain, c.Capabilities("ui-admin"));
            Assert.Contains(Capability.Assign, c.Capabilities("ui-admin"));
            Assert.Contains(Capability.ViewAll, c.Capabilities("ui-admin"));
            var handler = c.Capabilities("ui-handler");
            Assert.Contains(Capability.Handle, handler);
            Assert.DoesNotContain(Capability.ViewAll, handler);
            Assert.DoesNotContain(Capability.Assign, handler);
            Assert.DoesNotContain(Capability.Maintain, handler);
            Assert.DoesNotContain(Capability.ViewAll, c.Capabilities("ui-viewer"));
        },
        ["使用者.檢視者只看得到一部分主機"] = c =>
        {
            var viewer = c.GroupVisibleHostIds("ui-viewer");
            var handler = c.GroupVisibleHostIds("ui-handler");
            var all = c._hosts.GetAll().Count(h => h.Active);
            True(viewer.Count > 0 && viewer.Count < handler.Count && handler.Count < all, $"可見主機數應 檢視者({viewer.Count}) < 處理人({handler.Count}) < 全部({all})");
        },
        ["使用者.長顯示名稱"] = c => True(c.User("ui-handler-long").DisplayName.Length > 30, "顯示名稱應超長"),
        ["使用者.停用"] = c => Assert.False(c.User("ui-disabled").Active),
        ["使用者.暫停接單"] = c => Assert.True(c.User("ui-paused").DispatchPaused),
        ["使用者.無群組"] = c => Assert.Empty(c.User("ui-nogroup").GroupIds),
        ["使用者.顯示名稱含HTML"] = c => Assert.Contains("<b>", c.User("ui-hostile").DisplayName),
        ["使用者.派工池旗標"] = c =>
        {
            Assert.True(c._userGroups.Get(c._d.UserGroupIds["ops"])!.DispatchPool);
            Assert.False(c._userGroups.Get(c._d.UserGroupIds["viewers"])!.DispatchPool);
        },
        ["使用者.內建群組不重複建立"] = c =>
        {
            var groups = c._userGroups.GetAll();
            foreach (var role in new[] { UserRole.Admin, UserRole.Manager, UserRole.Dev })
                Assert.Single(groups, g => g.Builtin && g.Role == role);
        },

        ["規則.自訂白話說明規則通過產品驗證"] = c =>
        {
            var rules = c.Rules();
            var custom = rules.Where(r => r.Id.StartsWith("ui-fixture-expl-", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, custom.Count);
            var result = RuleValidator.Validate(rules);
            Assert.DoesNotContain(result.SkippedRules, x => x.Rule.Id.StartsWith("ui-fixture-expl-", StringComparison.Ordinal));
        },

        // ═══ 產品讀取路徑 ═══
        ["讀取路徑.紀錄查詢能讀回全部非清除紀錄"] = c =>
        {
            using var ctx = c._d.Backend.CreateContext();
            var expected = ctx.DailyRecords.Count(r => !r.DetailPruned);
            var records = c._d.Backend.RecordStore().Query(new RecordQueryFilter());
            True(records.Count >= expected, $"Query 讀回 {records.Count} 筆，預期至少 {expected}");
        },
        ["讀取路徑.站台AI設定判定"] = c =>
        {
            var s = new SystemSettingsStore(c._d.Backend.Blob("system_settings")).Get();
            var configured = AiProviders.IsConfigured(s.AiProvider, s.AiBaseUrl, s.AiApiKeyEnc, s.AiAzureDeployment);
            Assert.Equal(c._d.Variant == UiFixtureVariant.AiConfigured, configured);
        },
    };

    private static void LegacyCheck(UiFixtureChecks c, int eventId, string message)
    {
        var i = c.Issue("legacy-messages", "Microsoft-Windows-Security-Auditing", eventId);
        Assert.Contains(message, i.KeyAccounts!);
        Assert.Contains(message, i.KeyDetails!);   // 原樣寫入、未清理
    }
}
