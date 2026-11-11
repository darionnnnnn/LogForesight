using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// 資料集的第二批資料（A-0c）：交辦單各狀態、問題負責與靜音、自訂／退役規則、排程執行紀錄、稽核、
/// 權限異動、PRTG 鏡像、NetIQ 嘗試帳本。全部走產品 store 或產品的寫入入口。
/// </summary>
public sealed partial class UiFixtureDataSet
{
    // 站台啟動不得真的對外連線：PRTG 保持未啟用（PrtgEnabled=false，位址用 .invalid、不放任何憑證），
    // 排程維持關閉（ScheduleOptions.Enabled 預設 false）。鏡像資料照常寫進資料表，維護頁讀得到。
    public const string PrtgUrl = "https://prtg.invalid";

    /// <summary>NetIQ 嘗試帳本使用的主機日（鍵 → 主機鍵、距今天數）</summary>
    public Dictionary<string, (string HostKey, int DaysAgo, string Status)> NetiqLedger { get; } = new();

    public Dictionary<string, long> RunIds { get; } = new();
    public List<string> PermissionChangeIds { get; } = new();
    public long PrtgDeviceMapped { get; private set; }

    private void BuildExtended()
    {
        WriteExtraWorkOrders();
        WriteIssueProfiles();
        WriteBatchRuns();
        WriteAudit();
        WritePermissionChanges();
        WritePrtg();
        WriteNetiqLedger();
    }

    private static IssueCase NewCase(string id, string host, string source, int eventId, string status, long handler, long orderId,
        DateTime linked, DateTime? due = null, DateTime? closedAt = null, string? note = null) => new()
    {
        CaseId = id, HostName = host, IssueKey = IssueSignatureKey.For("System", source, eventId, LogEntryType.Error),
        IssueLabel = $"{source} EventId {eventId}", Status = status, HandlerId = handler, DueDate = due,
        FirstLinkedDate = linked.AddDays(-1), LastLinkedDate = linked, CreatedAt = linked.AddDays(-1).AddHours(9),
        CreatedByAccount = "ui-admin", UpdatedAt = linked.AddHours(10), ClosedAt = closedAt, WorkOrderId = orderId,
        SourceName = source, EventId = eventId, Note = note
    };

    // ── 交辦單：已結案／上報中／暫停（靜音中）／200 成員／超長說明 ──────────────

    private void WriteExtraWorkOrders()
    {
        var orders = Backend.WorkOrderStore();
        var now = DateTime.Now;
        var admin = UserIds["ui-admin"];
        var win = _hosts["win"].HostName;
        var yesterday = Yesterday;
        var cases = new List<IssueCase>();

        long Order(string key, string account, string source, int eventId, string? note, DateTime? due, DateTime? lastReply,
            DateTime? closedAt = null, string? closedReason = null, string? label = null)
        {
            var id = orders.Insert(new WorkOrder
            {
                SourceName = source, EventId = eventId, IssueLabel = label ?? $"{source} EventId {eventId}", HandlerId = UserIds[account],
                Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, Note = note, DueDate = due,
                CreatedById = admin, CreatedByAccount = "ui-admin", CreatedAt = now.AddDays(-5), LastReplyAt = lastReply,
                ClosedAt = closedAt, ClosedReason = closedReason, UpdatedAt = now
            });
            WorkOrderIds[key] = id;
            orders.AppendEvent(new WorkOrderEvent
            {
                WorkOrderId = id, Action = WorkOrderEventActions.Created, ActorId = admin, ActorAccount = "ui-admin",
                MemberDelta = 1, CreatedAt = now.AddDays(-5)
            });
            if (lastReply != null)
                orders.AppendEvent(new WorkOrderEvent
                {
                    WorkOrderId = id, Action = WorkOrderEventActions.Replied, ActorId = UserIds[account], ActorAccount = account,
                    Note = "已收到，處理中。", CreatedAt = lastReply.Value
                });
            return id;
        }

        // 已結案：成員全部結案，系統自動結案（all_closed）
        var closed = Order("closed", "ui-handler", "Ui-Closed-Issue", 8600, "已處理完畢，感謝協助。", yesterday.AddDays(-3), now.AddDays(-2),
            closedAt: now.AddDays(-1), closedReason: WorkOrderCloseReasons.AllClosed);
        cases.Add(NewCase("uicase-closed", win, "Ui-Closed-Issue", 8600, IssueHandlingStatuses.Resolved, UserIds["ui-handler"], closed,
            yesterday.AddDays(-4), closedAt: now.AddDays(-1), note: "更換磁碟後恢復正常。"));

        // 上報中：成員被處理人標為「無法處理」，等管理員決定
        var escalated = Order("escalated", "ui-same-a", "Ui-Escalated-Issue", 8610, null, yesterday.AddDays(2), now.AddDays(-1));
        cases.Add(NewCase("uicase-escalated", win, "Ui-Escalated-Issue", 8610, IssueHandlingStatuses.Escalated, UserIds["ui-same-a"], escalated,
            yesterday.AddDays(-2), note: "需要廠商介入，已超出我的權限。"));

        // 暫停：這張單的問題目前被靜音（見 WriteIssueProfiles 的 Ui-Muted-Issue）
        var paused = Order("paused", "ui-handler", "Ui-Muted-Issue", 8620, "此問題已知會在維護窗口期間出現，暫時靜音。", yesterday.AddDays(5), now.AddHours(-20));
        cases.Add(NewCase("uicase-paused", win, "Ui-Muted-Issue", 8620, IssueHandlingStatuses.InProgress, UserIds["ui-handler"], paused,
            yesterday.AddDays(-1), yesterday.AddDays(5)));

        // 200 個成員：40 台 × 5 種 EntryType（同一問題在不同主機、不同登錄類型各算一個成員）
        var mass = Order("mass", "ui-handler", "Ui-Mass-Issue", 8800, "全站一起處理的問題，成員很多。", yesterday.AddDays(10), now.AddHours(-6));
        var entryTypes = new[] { LogEntryType.Error, LogEntryType.Warning, LogEntryType.Information, LogEntryType.SuccessAudit, LogEntryType.FailureAudit };
        var n = 0;
        for (var h = 1; h <= 40; h++)
            foreach (var entryType in entryTypes)
            {
                n++;
                var statuses = new[] { IssueHandlingStatuses.InProgress, IssueHandlingStatuses.Observing, IssueHandlingStatuses.InProgress, IssueHandlingStatuses.Open };
                cases.Add(new IssueCase
                {
                    CaseId = $"uimass{n:D4}", HostName = $"BULK-{h:D3}", IssueKey = IssueSignatureKey.For("System", "Ui-Mass-Issue", 8800, entryType),
                    IssueLabel = "Ui-Mass-Issue EventId 8800", Status = statuses[n % statuses.Length], HandlerId = UserIds["ui-handler"],
                    DueDate = n % 7 == 0 ? yesterday.AddDays(-2) : yesterday.AddDays(10),
                    FirstLinkedDate = yesterday.AddDays(-3), LastLinkedDate = yesterday, CreatedAt = now.AddDays(-4),
                    CreatedByAccount = "ui-admin", UpdatedAt = now.AddDays(-1), WorkOrderId = mass, SourceName = "Ui-Mass-Issue", EventId = 8800
                });
            }

        // 超長說明：備註、問題標籤都很長，含 HTML 字元
        var longNote = UiFixtureText.RepeatTo("超長說明：這張交辦單的備註刻意寫得非常長，包含中英文 mixed content、網址 https://example.com/very/long/path/that/never/breaks，以及多行內容。\n", 1500)
                       + UiFixtureText.Hostile;
        var longOrder = Order("long-note", "ui-handler-long", "Ui-LongNote-Issue", 8900, longNote, yesterday.AddDays(3), null,
            label: "Ui-LongNote-Issue EventId 8900 " + UiFixtureText.RepeatTo("超長的問題標籤", 160));
        cases.Add(NewCase("uicase-longnote", win, "Ui-LongNote-Issue", 8900, IssueHandlingStatuses.InProgress, UserIds["ui-handler-long"], longOrder,
            yesterday.AddDays(-1), yesterday.AddDays(3), note: UiFixtureText.RepeatTo("案件備註也很長，", 400)));

        Backend.IssueCaseStore().SaveMany(cases);
    }

    // ── 問題負責與靜音 ──────────────────────────────────────────────────────

    private void WriteIssueProfiles()
    {
        var owners = new IssueOwnerStore(Backend.Blob("issue_owners"));
        var admin = UserIds["ui-admin"];
        var today = DateTime.Today;

        MuteInterval Mute(int fromOffset, int toOffset, string reason) => new()
        {
            From = today.AddDays(fromOffset), To = today.AddDays(toOffset), Reason = reason, ById = admin, ByAccount = "ui-admin",
            At = today.AddDays(Math.Min(fromOffset, 0)).AddHours(9)
        };

        void Profile(string source, int eventId, Action<IssueProfile> apply) =>
            owners.Update(source, eventId, p =>
            {
                p.SourceName = source; p.EventId = eventId; p.UpdatedByAccount = "ui-admin"; p.UpdatedAt = DateTime.Now;
                apply(p);
            });

        Profile("Ui-Case-Issue-1", 8001, p =>
        {
            p.OwnerUserIds = new List<long> { UserIds["ui-handler"], admin };
            p.Note = "磁碟相關問題由維運一組負責。";
        });
        Profile("Ui-Case-Issue-2", 8002, p =>
        {
            p.OwnerUserIds = new List<long> { UserIds["ui-same-a"], UserIds["ui-same-b"], UserIds["ui-handler-long"] };
            p.Note = UiFixtureText.RepeatTo("負責說明很長，", 600) + UiFixtureText.Hostile;
        });
        // 目前靜音中（會讓交辦單「暫停」）＋一段過去已結束的靜音
        Profile("Ui-Muted-Issue", 8620, p =>
        {
            p.OwnerUserIds = new List<long> { UserIds["ui-handler"] };
            p.Mutes = new List<MuteInterval>
            {
                Mute(-3, 10, "維護窗口期間會反覆出現，先靜音到窗口結束。" + UiFixtureText.RepeatTo("原因補充，", 200)),
                Mute(-40, -30, "上一次維護窗口")
            };
        });
        Profile("Ui-Mute-Expired", 8630, p => { p.Mutes = new List<MuteInterval> { Mute(-20, -10, "已到期的靜音") }; });
        Profile("Ui-Mute-Future", 8631, p => { p.Mutes = new List<MuteInterval> { Mute(5, 12, "預約的靜音（尚未開始）") }; });
        // 已有結論、自動套用、多段靜音、HTML 字元
        Profile("UiFixture-<script>alert(1)</script>&\"'", 9100, p =>
        {
            p.Note = UiFixtureText.Hostile;
            p.ConclusionStatus = IssueHandlingStatuses.KnownNoise;
            p.ConclusionNote = "判定為已知雜訊：" + UiFixtureText.Hostile;
            p.ConcludedById = admin; p.ConcludedByAccount = "ui-admin"; p.ConcludedAt = DateTime.Now.AddDays(-2);
            p.AutoApply = true;
            p.Mutes = new List<MuteInterval> { Mute(-1, 1, UiFixtureText.Hostile), Mute(3, 4, "第二段"), Mute(8, 9, "第三段") };
        });
        Profile(UiFixtureText.Source255, 9006, p => { p.OwnerUserIds = new List<long> { admin }; p.Note = "來源名稱長達 255 字元。"; });
    }

    // ── 規則：自訂（超長描述、有／無知識庫）、退役 ──────────────────────────

    private static void AddExtendedRules(List<KnownIssueRule> rules)
    {
        KnownIssueRule Custom(string id, string pattern, int eventId, string description, string explanation, string impact,
            string[]? causes, string[]? steps, bool enabled = true) => new()
        {
            Id = id, Origin = "custom", Enabled = enabled, Scope = "all", Platform = "windows",
            SourcePattern = pattern, EventIds = new[] { eventId }, Category = IssueCategory.Config, Severity = IssueSeverity.Medium,
            Description = description, PlainExplanation = explanation, Impact = impact,
            LikelyCauses = causes ?? Array.Empty<string>(), NextSteps = steps ?? Array.Empty<string>(), ModifiedAt = DateTime.Now
        };

        rules.Add(Custom("ui-fixture-rule-longdesc", "UiFixture-Rule-LongDesc", 9201,
            UiFixtureText.RepeatTo("規則描述長達五百字元的上限，用來檢查規則清單與編輯畫面在極端長度下的排版。", 500)[..500],
            "有完整處置知識庫的自訂規則。", "影響範圍說明。",
            new[] { "可能原因一：" + UiFixtureText.RepeatTo("很長的原因，", 240)[..240], "可能原因二" },
            new[] { "步驟一：" + UiFixtureText.RepeatTo("很長的步驟，", 240)[..240], "步驟二", "步驟三" }));
        rules.Add(Custom("ui-fixture-rule-kb", "UiFixture-Rule-Kb", 9202, "有處置知識庫的一般自訂規則", "白話說明一行。", "影響說明一行。",
            new[] { "原因 A", "原因 B" }, new[] { "步驟 1", "步驟 2" }));
        // 沒有處置知識庫：產品的 RuleValidator 不接受（白話／影響／原因／步驟都必填），這裡直接寫入規則庫，
        // 用來看維護頁對「知識庫全空」的規則怎麼呈現
        rules.Add(Custom("ui-fixture-rule-nokb", "UiFixture-Rule-NoKb", 9203, "沒有任何處置知識庫的自訂規則", "", "", null, null));
        rules.Add(Custom("ui-fixture-rule-disabled", "UiFixture-Rule-Disabled", 9204, "停用中的自訂規則", "停用中。", "無。",
            new[] { "原因" }, new[] { "步驟" }, enabled: false));

        // 退役規則：已移除、只能保持停用的 PRTG 規則代碼（歷史 finding 仍可讀）
        rules.Add(new KnownIssueRule
        {
            Id = "ui-fixture-rule-retired", Origin = "custom", Enabled = false, Scope = "all", Platform = "prtg",
            PrtgRuleCode = "resource_cpu_sustained_pressure", PrtgThreshold = 120,
            Category = IssueCategory.Resource, Severity = IssueSeverity.Medium,
            Description = "（已退役）CPU 兩小時持續高壓", PlainExplanation = "舊版的兩小時資源壓力規則，已移除。",
            Impact = "無。", LikelyCauses = new[] { "已退役" }, NextSteps = new[] { "請改用數值趨勢規則" }, ModifiedAt = DateTime.Now
        });
    }

    // ── 排程執行紀錄 ────────────────────────────────────────────────────────

    private void WriteBatchRuns()
    {
        var store = new BatchRunStore(Backend.LogStore("batch_runs"), Backend.LogStore("batch_run_logs"));
        var today = DateTime.Today;
        var backdate = new List<(long RunId, DateTime At, bool Running)>();

        RunPhaseRecord Phase(string key, DateTime start, TimeSpan duration, string outcome, string? note = null) =>
            new(key, start) { FinishedAt = start + duration, Outcome = outcome, Note = note };

        BatchRun Run(string key, int daysAgo, string trigger, Action<BatchRun>? configure = null, double startHour = 1.0)
        {
            var start = today.AddDays(-daysAgo).AddHours(startHour);
            var run = new BatchRun
            {
                HostName = "UI-WEB-01", StartedAt = start, FinishedAt = start.AddMinutes(95), ExitCode = 0, AppVersion = "1.0.99.0+uifixture",
                Args = "--scheduled", DaysAnalyzed = 1, AiCalls = 6, AiFailures = 0, WarnCount = 0, ErrorCount = 0, Trigger = trigger,
                CatchUp = false, WindowInstanceStart = start, Scope = BatchRunScopes.Full, LocalDaysAnalyzed = 1, LocalDaysFailed = 0,
                NetiqDaysAnalyzed = 1, NetiqDaysFailed = 0, NetiqHostsSkipped = 0, PrtgOutcome = BatchRun.PrtgOutcomeDisabled,
                PrtgOutcomeNote = BatchRun.PrtgNoteNotEnabled
            };
            configure?.Invoke(run);
            run.RunId = store.StartRun(run);
            RunIds[key] = run.RunId;
            return run;
        }

        void Finish(BatchRun run) { store.FinishRun(run); backdate.Add((run.RunId, run.FinishedAt ?? run.StartedAt, false)); }

        void Log(BatchRun run, string level, string message, string? ex = null, double minutesIn = 1)
        {
            store.AppendLog(new BatchRunLog
            {
                RunId = run.RunId, LoggedAt = run.StartedAt.AddMinutes(minutesIn), Level = level,
                Logger = "LogForesight.Service.NetiqPipelineService", Message = message, ExceptionText = ex
            });
        }

        // 階段時間軸：短（秒）到長（小時）懸殊
        List<RunPhaseRecord> Phases(DateTime start) => new()
        {
            Phase(RunPhases.Local, start, TimeSpan.FromSeconds(3), RunPhaseOutcomes.Success),
            Phase(RunPhases.Netiq, start.AddSeconds(4), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(12), RunPhaseOutcomes.Success),
            Phase(RunPhases.PrtgSync, start.AddHours(3).AddMinutes(13), TimeSpan.FromMinutes(4), RunPhaseOutcomes.Skipped, "PRTG 未啟用"),
        };

        // ① 成功（階段長短懸殊）
        var ok = Run("success", 1, BatchRunTriggers.Schedule, r =>
        {
            r.FinishedAt = r.StartedAt.AddHours(3).AddMinutes(20);
            r.Phases = Phases(r.StartedAt);
        });
        Log(ok, "Info", "完成：1 天、40 台主機。");
        Finish(ok);

        // ② 有警告（含訊息極長的一筆，內含多行與 HTML 字元）
        var warn = Run("warnings", 2, BatchRunTriggers.Schedule, r =>
        {
            r.WarnCount = 14; r.NetiqHostsSkipped = 2; r.AiFailures = 1;
            r.Phases = Phases(r.StartedAt);
        });
        Log(warn, "Warn", "主機 BULK-007 取數逾時，略過。", minutesIn: 12);
        Log(warn, "Warn", "AI 回覆格式異常（第 1 次），已降級為規則層結論。", minutesIn: 40);
        Log(warn, "Warn", UiFixtureText.RepeatTo("超長的警告訊息：Sentinel 回傳了一大段無法解析的內容，包含多行文字與 <script>alert(1)</script> & \"引號\"。\n", 9000),
            "System.InvalidOperationException: " + UiFixtureText.RepeatTo("例外訊息很長 ", 3000) + "\n   at LogForesight.Service.NetiqPipelineService.Run()\n   at ...", 55);
        Finish(warn);

        // ③ 失敗
        var failed = Run("failed", 3, BatchRunTriggers.Schedule, r =>
        {
            r.ExitCode = 1; r.ErrorCount = 3; r.FinishedAt = r.StartedAt.AddMinutes(7); r.DaysAnalyzed = 0; r.NetiqDaysAnalyzed = 0; r.NetiqDaysFailed = 1;
            r.Phases = new List<RunPhaseRecord>
            {
                Phase(RunPhases.Local, r.StartedAt, TimeSpan.FromSeconds(2), RunPhaseOutcomes.Success),
                Phase(RunPhases.Netiq, r.StartedAt.AddSeconds(3), TimeSpan.FromMinutes(7), RunPhaseOutcomes.Failed, "Sentinel 連線失敗")
            };
        });
        Log(failed, "Error", "無法連線 Sentinel（UI-Sentinel-Win）：名稱解析失敗。", "System.Net.Http.HttpRequestException: 不明主機。\n   at ...", 5);
        Log(failed, "Error", "NetIQ 取數整體失敗，本趟結束。", minutesIn: 6);
        Finish(failed);

        // ④ 中斷（站台重啟時被標記）
        var interrupted = Run("interrupted", 4, BatchRunTriggers.Schedule, r =>
        {
            r.ExitCode = BatchRun.ExitCodeInterrupted; r.Interrupted = true; r.FinishedAt = r.StartedAt.AddHours(2);
            r.Phases = new List<RunPhaseRecord>
            {
                Phase(RunPhases.Local, r.StartedAt, TimeSpan.FromSeconds(3), RunPhaseOutcomes.Success),
                new(RunPhases.Netiq, r.StartedAt.AddSeconds(4)) { FinishedAt = null }
            };
        });
        Log(interrupted, "Info", "站台重啟時發現此執行未正常結束，標記為異常中斷", minutesIn: 120);
        Finish(interrupted);

        // ⑤ 已回補（補跑）
        var catchUp = Run("catch-up", 5, BatchRunTriggers.Schedule, r =>
        {
            r.CatchUp = true; r.DaysAnalyzed = 3; r.FinishedAt = r.StartedAt.AddHours(5);
            r.Phases = Phases(r.StartedAt);
        }, startHour: 3);
        Log(catchUp, "Info", "補跑：前兩天沒有成功的排程執行，一併回補。");
        Finish(catchUp);

        // ⑥ 手動、使用者按下停止
        var stopped = Run("stopped", 6, BatchRunTriggers.ManualPrefix + "ui-admin", r =>
        {
            r.Stopped = true; r.ExitCode = 1; r.FinishedAt = r.StartedAt.AddMinutes(25); r.Scope = BatchRunScopes.NetiqHosts;
            r.Phases = new List<RunPhaseRecord>
            {
                Phase(RunPhases.Netiq, r.StartedAt, TimeSpan.FromMinutes(25), RunPhaseOutcomes.Stopped)
            };
        }, startHour: 10);
        Log(stopped, "Info", "使用者要求停止，已在單台邊界停下。", minutesIn: 25);
        Finish(stopped);

        // ⑦ AI 背景判讀（不算取數排程）
        var ai = Run("ai-job", 1, BatchRunTriggers.Schedule, r =>
        {
            r.JobType = BatchRun.JobTypeAi; r.AiCalls = 38; r.AiFailures = 2; r.DaysAnalyzed = 0; r.FinishedAt = r.StartedAt.AddMinutes(48);
        }, startHour: 12);
        Log(ai, "Info", "AI 判讀完成：38 筆，2 筆失敗。");
        Finish(ai);

        // ⑧ 執行中：站台啟動會把「啟動前開始、未結束」的執行標成中斷，所以開始時間放在未來數小時，
        // 才不會在站台一啟動就被關掉（畫面上的已執行時間會是負的，見回報）
        var running = Run("running", 0, BatchRunTriggers.ManualPrefix + "ui-admin", r =>
        {
            r.StartedAt = DateTime.Now.AddHours(6); r.FinishedAt = null; r.ExitCode = null; r.WindowInstanceStart = null;
            r.Phases = new List<RunPhaseRecord>
            {
                Phase(RunPhases.Local, r.StartedAt, TimeSpan.FromSeconds(3), RunPhaseOutcomes.Success),
                new(RunPhases.Netiq, r.StartedAt.AddSeconds(4))
            };
        });
        store.FinishRun(running);   // 寫第二列（同 RunId，仍無結束時間）：與實際執行中的寫入形狀一致

        BackdateLogLines(backdate.Where(b => !b.Running));
    }

    /// <summary>
    /// 執行紀錄與診斷行的查詢會依「附加時間」窄化（診斷行必然寫在執行期間），
    /// 但資料集是現在才寫的過去資料：把附加時間改成執行的業務時間，畫面才找得到那些診斷行。
    /// </summary>
    private void BackdateLogLines(IEnumerable<(long RunId, DateTime At, bool Running)> runs)
    {
        using var ctx = Backend.CreateContext();
        foreach (var (runId, at, _) in runs)
        {
            var key1 = $"\"RunId\":{runId},";
            ctx.LogLines.Where(l => (l.LogKey == "batch_runs" || l.LogKey == "batch_run_logs") && l.Line.Contains(key1))
                .ExecuteUpdate(s => s.SetProperty(l => l.CreatedAt, (DateTime?)at.AddMinutes(-1)));
        }
    }

    // ── 稽核紀錄 ────────────────────────────────────────────────────────────

    private void WriteAudit()
    {
        var store = new AuditLogStore(Backend.LogStore("audit"));
        var today = DateTime.Today;
        var written = new List<AuditEntry>();

        void Add(int daysAgo, double hour, string account, string action, string summary, AuditResult result = AuditResult.Ok,
            string? kind = null, string? target = null, string? detail = null, string? ip = "10.0.0.15", bool anonymous = false)
        {
            var e = new AuditEntry
            {
                OccurredAt = today.AddDays(-daysAgo).AddHours(hour), UserId = anonymous ? null : UserIds.GetValueOrDefault(account),
                Account = account, Action = action, TargetKind = kind, TargetId = target, Summary = summary, DetailJson = detail,
                IpAddress = ip, Result = result
            };
            store.Append(e);
            written.Add(e);
        }

        Add(1, 8.5, "ui-admin", AuditActions.Login, "登入成功（Stub）", kind: "auth");
        Add(1, 8.6, "ui-handler", AuditActions.Login, "登入成功（Stub）", ip: "10.0.0.22", kind: "auth");
        Add(1, 8.7, "unknown-user", AuditActions.LoginFailed, "憑證驗證未通過（Stub）", AuditResult.Failed, "auth", ip: "203.0.113.50", anonymous: true);
        Add(1, 9.0, "ui-viewer", AuditActions.AccessDenied, "嘗試存取 /admin/users，缺少 Maintain 能力", AuditResult.Denied, "page", "/admin/users");
        Add(2, 10.0, "ui-admin", AuditActions.WorkOrderCreate, "建立交辦單 #1：Ui-Case-Issue-1 → ui-handler（1 個成員）", kind: "work_order", target: "1");
        Add(2, 10.2, "ui-admin", AuditActions.WorkOrderReassign, "交辦單 #2 改派給 ui-handler-long", kind: "work_order", target: "2");
        Add(2, 11.0, "ui-handler", AuditActions.HandlingStatus, "將 Ui-Handle-resolved 標記為已處理", kind: "issue", target: "UI-WIN-NORMAL");
        Add(3, 9.0, "ui-admin", AuditActions.SettingsUpdate, "更新系統設定：AI 位址、保留天數", kind: "settings",
            detail: "{\"before\":{\"AiBaseUrl\":\"\"},\"after\":{\"AiBaseUrl\":\"http://127.0.0.1:8080\"}}");
        Add(3, 9.5, "ui-admin", AuditActions.RuleUpdate, "更新規則 ui-fixture-rule-kb", kind: "rule", target: "ui-fixture-rule-kb");
        Add(3, 9.6, "ui-admin", AuditActions.RuleDisable, "停用規則 ui-fixture-rule-disabled", kind: "rule", target: "ui-fixture-rule-disabled");
        Add(4, 13.0, "ui-admin", AuditActions.HostMerge, "合併主機 OLD-UI-WIN-NORMAL → UI-WIN-NORMAL", kind: "host", target: "15");
        Add(4, 13.5, "ui-admin", AuditActions.UserUpdate, "更新使用者 ui-paused：暫停接單", kind: "user", target: "9");
        Add(5, 14.0, "ui-admin", AuditActions.PrtgConnectionTest, "PRTG 連線測試失敗：名稱解析失敗（prtg.invalid）", AuditResult.Failed, "prtg");
        Add(5, 14.5, "ui-admin", AuditActions.ScheduleManualRun, "手動執行取數（全部來源）", kind: "schedule");
        Add(6, 15.0, "svc-lfadmin", AuditActions.Login, "serverAdmin 本地救援帳號登入成功", kind: "auth", anonymous: true);
        Add(6, 15.2, "ui-hostile", AuditActions.HandlingNote, "備註：" + UiFixtureText.Hostile, kind: "issue", target: UiFixtureText.Hostile);

        // 長摘要與長細節
        Add(7, 9.0, "ui-admin", AuditActions.ImportApply,
            UiFixtureText.RepeatTo("匯入主機清單：新增 120 台、更新 45 台、略過 3 台（名稱重複）。摘要刻意寫得很長，用來檢查稽核清單的欄寬與換行。", 1200),
            kind: "import", target: UiFixtureText.Contiguous2000[..300],
            detail: "{\"rows\":" + UiFixtureText.RepeatTo("[\"BULK-001\",\"BULK-002\"],", 3000) + "\"end\":true}");
        Add(7, 9.5, "ui-admin", AuditActions.SettingsUpdate, UiFixtureText.Contiguous2000, AuditResult.Failed, "settings");

        // 其餘分散在三十天內，湊出分頁與時間序
        var actions = new[] { AuditActions.Login, AuditActions.HandlingStatus, AuditActions.WorkOrderAppend, AuditActions.IssueMute, AuditActions.HostUpdate, AuditActions.AccessGrant };
        var results = new[] { AuditResult.Ok, AuditResult.Ok, AuditResult.Ok, AuditResult.Failed, AuditResult.Denied };
        for (var i = 0; i < 24; i++)
            Add(8 + i, 8 + (i % 9), i % 3 == 0 ? "ui-admin" : i % 3 == 1 ? "ui-handler" : "ui-manager", actions[i % actions.Length],
                $"例行操作 #{i + 1}", results[i % results.Length], "misc", $"T{i}");

        // 稽核列查詢依附加時間窄化：把附加時間改成事件發生時間
        using var ctx = Backend.CreateContext();
        foreach (var e in written)
        {
            var key = $"\"AuditId\":{e.AuditId},";
            var at = e.OccurredAt;
            ctx.LogLines.Where(l => l.LogKey == "audit" && l.Line.Contains(key))
                .ExecuteUpdate(s => s.SetProperty(l => l.CreatedAt, (DateTime?)at));
        }
    }

    // ── 權限異動 ────────────────────────────────────────────────────────────

    private void WritePermissionChanges()
    {
        var store = Backend.PermissionChanges();
        var winHost = _hosts["win"].HostName;
        var finHost = _hosts["fin"].HostName;
        var now = DateTime.Now;

        PermissionChangeRecord Parsed(string host, int hoursAgo, string category, string target, string type, string before, string after,
            string initiator, string? targetAccount, bool privileged, int eventId, string? objectType = null) => new()
        {
            HostName = host, DetectedAt = now.AddHours(-hoursAgo), Target = target, ChangeType = type, Before = before, After = after,
            AlertText = $"{host} 的 {target}：{type}（{initiator} → {targetAccount ?? "-"}）", Source = PermissionChangeSources.Local,
            EventId = eventId, Category = category, IsPrivilegedTarget = privileged, InitiatorAccount = initiator, TargetAccount = targetAccount,
            ObjectType = objectType
        };

        var rows = new List<PermissionChangeRecord>
        {
            // 可解析成鍵值（欄位都有）
            Parsed(winHost, 2, PermissionCategory.GroupMember, "Administrators", "成員新增", "（無）", @"CORP\alice", @"CORP\admin01", @"CORP\alice", true, 4732),
            Parsed(winHost, 5, PermissionCategory.FolderAcl, @"D:\Share\Finance", "權限變更", "Users:Read", "Users:Modify", @"CORP\fileadmin", "Users", false, 4670, "File"),
            Parsed(finHost, 8, PermissionCategory.OwnerChange, @"E:\DB\Backup", "擁有者變更", @"CORP\dba", @"CORP\unknown-svc", @"CORP\dba", @"CORP\unknown-svc", true, 4670, "Directory"),
            Parsed(finHost, 30, PermissionCategory.AuditPolicy, "Audit Policy", "稽核政策變更", "Success, Failure", "None", "SYSTEM", null, true, 4719),
            // 超長鍵值與 HTML 字元
            Parsed(winHost, 40, PermissionCategory.ObjectAcl, UiFixtureText.Contiguous2000[..400], "權限變更 " + UiFixtureText.Hostile, UiFixtureText.RepeatTo("舊值 ", 500), UiFixtureText.RepeatTo("新值 ", 500),
                UiFixtureText.Hostile, UiFixtureText.Hostile, false, 4670, "Registry"),
            // 解析不出鍵值、走退路：只有原始文字，欄位全空、分類為「其他」
            new()
            {
                HostName = winHost, DetectedAt = now.AddHours(-12), Target = string.Empty, ChangeType = "權限異動", Before = string.Empty, After = string.Empty,
                AlertText = "偵測到權限異動，但無法解析細節。", RawText = "Security-Auditing 4670 :: (unparseable) " + UiFixtureText.RepeatTo("rawdata=0x00ff ", 2500),
                Source = PermissionChangeSources.Netiq, EventId = 4670, Category = PermissionCategory.Other
            },
            new()
            {
                HostName = finHost, DetectedAt = now.AddHours(-20), Target = string.Empty, ChangeType = "權限異動", Before = string.Empty, After = string.Empty,
                AlertText = "NetIQ 事件：權限異動（沒有可解析的欄位）", RawText = "???", Source = PermissionChangeSources.Netiq, EventId = 4728,
                Category = PermissionCategory.Other
            }
        };
        store.AppendChanges(rows);
        PermissionChangeIds.AddRange(rows.Select(r => r.ChangeId));

        // 三種狀態：待確認（預設）／已確認授權／可疑
        var handler = UserIds["ui-handler"];
        store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = rows[1].ChangeId, Status = PermissionConfirmStatuses.Authorized, ConfirmedBy = handler, ConfirmedByAccount = "ui-handler",
            ConfirmedAt = now.AddHours(-3), Note = "檔案伺服器維護作業，已核對變更單。"
        });
        store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = rows[2].ChangeId, Status = PermissionConfirmStatuses.Suspicious, ConfirmedBy = handler, ConfirmedByAccount = "ui-handler",
            ConfirmedAt = now.AddHours(-2), Note = UiFixtureText.RepeatTo("可疑：沒有對應的變更單，已通知資安。", 400)
        });
    }

    // ── PRTG 鏡像 ───────────────────────────────────────────────────────────

    private void WritePrtg()
    {
        // 位址用 .invalid、不放憑證、不啟用：站台啟動後不會真的去連線；維護頁仍讀得到下面寫的鏡像資料
        new SystemSettingsStore(Backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = false;
            s.PrtgUrl = PrtgUrl;
        });

        var store = Backend.PrtgStore();
        var now = DateTime.Now;
        var mapDate = Yesterday;

        PrtgDeviceRow Device(long id, string name, string? ip, string status, bool paused = false) => new()
        {
            Objid = id, Name = name, GroupPath = "Root/UI/" + (id % 2 == 0 ? "Servers" : "Network"), Ip = ip, Tags = "ui-fixture",
            Status = status, Paused = paused, SyncedAt = now, CreatedAt = now.AddDays(-30)
        };

        var devices = new List<PrtgDeviceRow>
        {
            Device(2001, "UI-WIN-NORMAL", "10.10.1.10", "Up"),
            Device(2010, HostFqdn, "10.10.1.20", "Warning"),
            Device(2011, "FIN-DB-01", "10.10.5.5", "Down"),
            Device(2012, "UI-PRTG-SW01", "10.20.9.1", "Up"),
            Device(2013, "ui-linux-web01", "10.10.2.30", "Unusual"),
            Device(2020, "UI-UNMATCHED-AP01", "192.0.2.77", "Up"),                 // 沒有任何主機用這個 IP
            Device(2021, "UI-UNMATCHED-NOIP", null, "Paused by User", paused: true),
            Device(2030, "UI-CONFLICT-DEVICE", "10.10.1.63", "Up"),                  // IP 同時屬於多台主機
            Device(2031, UiFixtureText.RepeatTo("超長裝置名稱-", 200)[..200], "10.99.0.1", "Up"),
            Device(2040, "UI-MANUAL-MAPPED", "10.99.0.2", "Up")
        };
        store.UpsertDevices(devices, now);

        PrtgSensorRow Sensor(long id, long device, string name, string type, string? unit, string status, string? category, bool paused = false) => new()
        {
            Objid = id, DeviceObjid = device, Name = name, SensorType = type, Unit = unit, Status = status, Category = category,
            CategorySource = category == null ? null : "auto", Paused = paused, Tags = "ui-fixture", SyncedAt = now, CreatedAt = now.AddDays(-30)
        };

        var longSensor = "Sensor 名稱超長：" + UiFixtureText.RepeatTo("磁碟 C: 可用空間 (Percent) / ", 300)[..300];
        var sensors = new List<PrtgSensorRow>
        {
            Sensor(2002, 2001, "磁碟可用空間 C:", "snmpdiskfree", "%", "Up", PrtgSensorCategories.Disk),
            Sensor(2003, 2001, "Ping", "ping", "ms", "Up", PrtgSensorCategories.Availability),
            Sensor(2004, 2001, "CPU 負載", "snmpcpuload", "%", "Up", PrtgSensorCategories.Cpu),
            Sensor(2005, 2001, "記憶體可用", "snmpmemory", "%", "Warning", PrtgSensorCategories.Memory),
            Sensor(2050, 2010, longSensor, "snmpdiskfree", "%", "Warning", PrtgSensorCategories.Disk),                 // 超長 sensor 名稱
            Sensor(2051, 2010, "沒有任何數值的 sensor", "ping", "ms", "Up", PrtgSensorCategories.Availability),       // 無數值
            Sensor(2052, 2011, "單位不可判的 sensor", "snmpcustom", null, "Down", null),                               // 單位不可判
            Sensor(2053, 2011, "單位亂碼的 sensor", "snmpcustom", "???", "Up", null),
            Sensor(2054, 2012, "交換器流量", "snmptraffic", "kbit/s", "Up", PrtgSensorCategories.Traffic),
            Sensor(2055, 2013, "Linux 磁碟", "snmplinuxdiskfree", "%", "Unusual", PrtgSensorCategories.Disk),
            Sensor(2056, 2020, "孤兒裝置 Ping", "ping", "ms", "Up", PrtgSensorCategories.Availability),
            Sensor(2057, 2021, "暫停的 sensor", "ping", "ms", "Paused by User", PrtgSensorCategories.Availability, paused: true),
            Sensor(2058, 2030, "衝突裝置 Ping", "ping", "ms", "Up", PrtgSensorCategories.Availability),
            Sensor(2059, 2031, "超長裝置的 sensor", "ping", "ms", "Up", PrtgSensorCategories.Availability)
        };
        store.UpsertSensors(sensors, now);

        // 數值：昨天 24 小時。有數值（品質 ok）／單位不可判（有值但 sensor 沒單位）／快照取樣（覆蓋率高與低）
        var values = new List<PrtgValueRow>();
        PrtgValueRow V(long sensor, int hour, double avg, string quality, double coverage = 100) => new()
        {
            SensorObjid = sensor, PeriodStart = Yesterday.AddHours(hour), AvgValue = avg, MinValue = avg - 1, MaxValue = avg + 1,
            Coverage = coverage, Quality = quality, CreatedAt = now
        };

        for (var h = 0; h < 24; h++)
        {
            values.Add(V(2002, h, 40 - h * 0.3, PrtgDataQuality.Ok));            // 磁碟可用空間緩慢下降
            values.Add(V(2004, h, 30 + (h % 6) * 3, PrtgDataQuality.Ok));
            values.Add(V(2050, h, 14 - h * 0.05, PrtgDataQuality.Ok));
            values.Add(V(2052, h, 120 + h, PrtgDataQuality.Ok));                  // sensor 沒有單位：單位不可判
            values.Add(V(2054, h, 5000 + h * 20, PrtgDataQuality.Sampled, h % 2 == 0 ? 90 : 40));   // 快照取樣，覆蓋率高低各半
        }
        store.UpsertValues(values);

        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 2005, ChangedAt = Yesterday.AddHours(3), Status = "Warning", PrevStatus = "Up", Message = "可用記憶體低於 15%", Quality = PrtgDataQuality.Ok, CreatedAt = now },
            new() { SensorObjid = 2052, ChangedAt = Yesterday.AddHours(5), Status = "Down", PrevStatus = "Up", Message = UiFixtureText.RepeatTo("逾時 ", 900), Quality = PrtgDataQuality.Ok, CreatedAt = now },
            new() { SensorObjid = 2054, ChangedAt = Yesterday.AddHours(9), Status = "Up", PrevStatus = "Down", Message = "OK", Quality = PrtgDataQuality.Ok, CreatedAt = now }
        });

        // 主機對應：已對應／未對應／衝突
        PrtgHostMapRow Map(long device, string? ip, long? hostId, string? hostName, string status, string? note = null) => new()
        {
            MapDate = mapDate, DeviceObjid = device, Ip = ip, HostId = hostId, HostName = hostName, MapStatus = status, Note = note, CreatedAt = now
        };

        var winId = HostIds["win"]; var fqdnId = HostIds["fqdn"]; var finId = HostIds["fin"]; var prtgId = HostIds["prtg"]; var linuxId = HostIds["linux"];
        store.ReplaceHostMapForDate(mapDate, new List<PrtgHostMapRow>
        {
            Map(2001, "10.10.1.10", winId, _hosts["win"].HostName, PrtgMapStatus.Ok),
            Map(2010, "10.10.1.20", fqdnId, HostFqdn, PrtgMapStatus.Ok),
            Map(2011, "10.10.5.5", finId, "FIN-DB-01", PrtgMapStatus.Ok),
            Map(2012, "10.20.9.1", prtgId, "UI-PRTG-SW01", PrtgMapStatus.Ok),
            Map(2013, "10.10.2.30", linuxId, "ui-linux-web01", PrtgMapStatus.Ok),
            Map(2020, "192.0.2.77", null, null, PrtgMapStatus.Unmatched, "沒有任何主機使用這個 IP"),
            Map(2021, null, null, null, PrtgMapStatus.Unmatched, "裝置沒有 IP"),
            Map(2030, "10.10.1.63", null, null, PrtgMapStatus.Conflict, "多台主機同一個 IP：" + HostName63 + "、UI-WIN-NORMAL"),
            Map(2031, "10.99.0.1", null, null, PrtgMapStatus.Unmatched, UiFixtureText.RepeatTo("備註很長 ", 400))
        });
        PrtgDeviceMapped = 2001;

        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 2040, HostId = HostIds["local"], CreatedBy = "ui-admin", Note = "人工指定對應", CreatedAt = now });
        store.UpsertIpExclude(new PrtgIpExcludeRow { Ip = "10.99.0.250", Note = "測試用排除 IP", CreatedBy = "ui-admin", CreatedAt = now });

        new PrtgStructureSyncStatusStore(Backend.Blob(PrtgStructureSyncStatusStore.BlobKey)).Update(st => st.CopyFrom(new PrtgStructureSyncStatus
        {
            Source = PrtgStructureSyncStatus.SourceNightly, CompletedAt = now.AddHours(-9), Success = true, ElapsedSeconds = 42.5,
            Devices = devices.Count, Sensors = sensors.Count, MapDate = mapDate, MapOk = 5, MapManual = 1, MapConflict = 1, MapUnmatched = 3,
            MapSkippedNoIp = 1, MapMappedHosts = 5, MapUnmappedHosts = 50
        }));
    }

    // ── NetIQ 嘗試帳本 ──────────────────────────────────────────────────────

    private void WriteNetiqLedger()
    {
        var utc = DateTime.UtcNow;
        long run = 900;

        void Record(string key, string hostKey, int daysAgo, string status, int minutesAgo) =>
            RecordAt(key, hostKey, daysAgo, status, utc.AddMinutes(-minutesAgo));

        void RecordAt(string key, string hostKey, int daysAgo, string status, DateTime attemptAtUtc)
        {
            NetiqSourceAttempt.RecordBatch(Backend, new[] { HostIds[hostKey] }, DayOf(daysAgo), ++run, status, attemptAtUtc);
            NetiqLedger[key] = (hostKey, daysAgo, status);
        }

        Record("success", "fqdn", 1, NetiqAttemptStatus.Success, 600);
        Record("partial", "fqdn", 2, NetiqAttemptStatus.Partial, 580);
        Record("failed", "fqdn", 3, NetiqAttemptStatus.Failed, 560);
        Record("stopped", "fqdn", 4, NetiqAttemptStatus.Stopped, 540);
        Record("empty-once", "fqdn", 5, NetiqAttemptStatus.EmptyOnce, 520);
        Record("retained-incomplete", "fqdn", 6, NetiqAttemptStatus.RetainedIncomplete, 500);
        // 同一主機日在「今天」連續失敗三趟：自動重試次數用盡。重試次數以本地日期計，三趟都要落在今天——
        // 以「現在往前 N 分鐘」排的話，凌晨產生資料集時前幾趟會落到昨天、次數不夠（時段相依的假失敗）
        var todayStartUtc = DateTime.Today.ToUniversalTime();
        for (var i = 1; i <= 3; i++)
            RecordAt("failed-exhausted", "ip-only", 3, NetiqAttemptStatus.Failed, todayStartUtc.AddSeconds(i));
    }

    // ── 情境 ────────────────────────────────────────────────────────────────

    private void RegisterExtendedScenarios()
    {
        foreach (var (key, title) in new[]
                 {
                     ("closed", "交辦單：已結案"), ("escalated", "交辦單：上報中（成員無法處理）"), ("paused", "交辦單：暫停（問題靜音中）"),
                     ("mass", "交辦單：200 個成員"), ("long-note", "交辦單：超長說明")
                 })
            Scenarios.Add(new UiFixtureScenario($"work-order.{key}", title, $"/work-orders/{WorkOrderIds[key]}"));
        Scenarios.Add(new UiFixtureScenario("work-order.unreplied", "交辦單：未回覆（尚無任何回覆）", $"/work-orders/{WorkOrderIds["ui-handler"]}"));

        Scenarios.Add(new UiFixtureScenario("page.runs", "排程執行紀錄（成功／警告／失敗／中斷／回補／停止／AI／執行中）", "/runs"));
        Scenarios.Add(new UiFixtureScenario("page.runs-warnings", "排程執行詳情：有警告（含超長訊息）", $"/runs?run={RunIds["warnings"]}"));
        Scenarios.Add(new UiFixtureScenario("page.runs-failed", "排程執行詳情：失敗", $"/runs?run={RunIds["failed"]}"));
        Scenarios.Add(new UiFixtureScenario("page.audit", "稽核紀錄（長摘要、成功／失敗／被拒）", "/audit"));
        Scenarios.Add(new UiFixtureScenario("page.permission-changes", "權限異動（可解析／退路、待確認／已授權／可疑）", "/permission-changes"));
        Scenarios.Add(new UiFixtureScenario("page.admin-prtg", "PRTG 維護（裝置鏡像、對應、sensor、數值）", "/admin/prtg"));
        Scenarios.Add(new UiFixtureScenario("page.admin-netiq", "NetIQ 維護（嘗試帳本各狀態）", "/admin/netiq"));
        Scenarios.Add(new UiFixtureScenario("page.admin-issue-owners", "問題負責與靜音", "/admin/issue-owners"));
        Scenarios.Add(new UiFixtureScenario("page.admin-calibration", "校準匯出（有 PRTG 數值）", "/admin/calibration"));
        Scenarios.Add(new UiFixtureScenario("page.admin-settings", "系統設定", "/admin/settings"));
        Scenarios.Add(new UiFixtureScenario("page.admin-imports", "匯入", "/admin/imports"));
    }
}
