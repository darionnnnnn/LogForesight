using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 2c 的情境輸出（2-9 執行總表、2-10 處理狀態推導、2-11 交辦摘要）。
/// 同一份情境在改動前（HEAD 8225380d）的乾淨副本跑出 <see cref="Batch2cGoldens"/>，改動後必須逐字相同。
/// </summary>
internal static class Batch2cScenarios
{
    // ── 2-9 執行總表 ─────────────────────────────────────────────

    /// <summary>日期一律換成相對今天的標記，讓輸出不隨執行日改變</summary>
    private static string Normalize(string json)
    {
        var today = DateTime.Today;
        for (var offset = -9; offset <= 2; offset++)
            json = json.Replace(today.AddDays(offset).ToString("yyyy-MM-dd"), $"D{offset:+0;-0;0}");
        // 時區位移不進比對（各平台時區不同）
        return Regex.Replace(json, @"(T\d\d:\d\d:\d\d(?:\.\d+)?)[+-]\d\d:\d\d", "$1");
    }

    public static string RunMonitor()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new FakeHostStore();
        var runs = new BatchRunStore(fx.LogStore("runs"), fx.LogStore("run_logs"));
        var records = new EfAnalysisRecordStore(fx.NewContext, "test");
        var options = new ScheduleOptionsStore(fx.Blob("schedule_options"));
        var service = new RunMonitorService(runs, hosts, records, new FakeUserStore(), options,
            new UserDisplayNameService(new FakeSystemSettingsStore()));

        foreach (var name in new[] { "SRV-A", "SRV-B", "srv-c", "SRV-D" })
            hosts.Upsert(new WebHost { HostName = name, Source = "local" });

        var today = DateTime.Today;
        BatchRun Run(string host, DateTime start, DateTime? finish, int exit, int days) => new()
        {
            HostName = host, StartedAt = start, FinishedAt = finish, ExitCode = exit,
            DaysAnalyzed = days, WarnCount = days, ErrorCount = exit
        };

        // 同一台主機同一天多趟；其中兩趟開始時間相同（穩定排序要維持來源順序）
        runs.StartRun(Run("SRV-A", today.AddDays(-1).AddHours(9), today.AddDays(-1).AddHours(10), 0, 1));
        runs.StartRun(Run("SRV-A", today.AddDays(-1).AddHours(9), today.AddDays(-1).AddHours(11), 1, 2));
        runs.StartRun(Run("SRV-A", today.AddDays(-1).AddHours(14), today.AddDays(-1).AddHours(15), 0, 3));
        // 主機名大小寫不同於主機清單
        runs.StartRun(Run("srv-a", today.AddDays(-2).AddHours(8), today.AddDays(-2).AddHours(9), 0, 4));
        runs.StartRun(Run("srv-b", today.AddDays(-1).AddHours(7), today.AddDays(-1).AddHours(8), 1, 5));
        runs.StartRun(Run("SRV-C", today.AddDays(-3).AddHours(6), today.AddDays(-3).AddHours(7), 0, 6));
        // 跨午夜：D-2 23:50 開始、D-1 00:20 結束，處理日以開始時間為準（D-2）
        runs.StartRun(Run("SRV-B", today.AddDays(-2).AddHours(23).AddMinutes(50), today.AddDays(-1).AddMinutes(20), 0, 7));
        runs.StartRun(Run("SRV-D", today.AddDays(-4).AddHours(1), today.AddDays(-4).AddHours(2), 0, 8));
        // 不在主機清單的名稱
        runs.StartRun(Run("GHOST", today.AddDays(-1).AddHours(5), today.AddDays(-1).AddHours(6), 0, 9));

        var sb = new StringBuilder();
        sb.AppendLine("summaries(5,1,30)=" + Normalize(JsonSerializer.Serialize(service.GetDaySummaries(5, 1, 30))));
        sb.AppendLine("summaries(5,2,2)=" + Normalize(JsonSerializer.Serialize(service.GetDaySummaries(5, 2, 2))));
        sb.AppendLine("summaries(5,3,2)=" + Normalize(JsonSerializer.Serialize(service.GetDaySummaries(5, 3, 2))));
        foreach (var offset in new[] { -2, -3, -4, -5 })
        {
            var dataDate = today.AddDays(offset);
            foreach (var sort in new string?[] { null, "hostName", "status", "daysAnalyzed", "errorCount", "workflowComplete" })
            foreach (var dir in new[] { "asc", "desc" })
            foreach (var page in new[] { 1, 2 })
            {
                var result = service.GetDayDetailPage(dataDate, page, 2, sort, dir);
                sb.AppendLine($"detail(D{offset},{sort},{dir},p{page})=" + Normalize(JsonSerializer.Serialize(result)));
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ── 2-10 處理狀態推導 ─────────────────────────────────────────

    public static string Occurrence()
    {
        var hosts = new FakeHostStore();
        var handlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        var resolver = new OccurrenceStatusResolver(hosts, handlings, cases, new FakeSystemSettingsStore());

        var h1 = hosts.Upsert(new WebHost { HostName = "HOST-1" });
        var h2 = hosts.Upsert(new WebHost { HostName = "host-2" });
        string Key(string source, int id, string eventKey = "") =>
            IssueSignatureKey.For("Application", source, id, LogEntryType.Error, eventKey);
        var day = new DateTime(2026, 8, 10);

        // 同主機同問題兩件案件：兩種來源大小寫寫法，比對器視為同一個問題，取清單中第一件
        cases.Save(new IssueCase { CaseId = "case-first", HostName = "HOST-1", IssueKey = Key("Disk", 11), Status = IssueHandlingStatuses.Open, Note = "第一件" });
        cases.Save(new IssueCase { CaseId = "case-second", HostName = "host-1", IssueKey = Key("DISK", 11), Status = IssueHandlingStatuses.Open, Note = "第二件" });
        // 只有處理列：同主機同日同問題兩筆（寫法不同），取第一筆
        handlings.Save(new IssueHandling { HostName = "host-2", IssueKey = Key("Cpu", 22), Date = day, Status = IssueHandlingStatuses.Resolved, Note = "第一筆", UpdatedAt = day });
        handlings.Save(new IssueHandling { HostName = "HOST-2", IssueKey = Key("CPU", 22), Date = day, Status = IssueHandlingStatuses.FalsePositive, Note = "第二筆", UpdatedAt = day });
        // 不同日的處理列不可被取到
        handlings.Save(new IssueHandling { HostName = "host-2", IssueKey = Key("Net", 33), Date = day.AddDays(-1), Status = IssueHandlingStatuses.Resolved, Note = "前一日", UpdatedAt = day });
        // 沿用誤判期：PRTG 問題的誤判標記在更早的日子
        var prtgKey = IssueSignatureKey.For(PrtgFindingMapper.PrtgLogName, "PRTG:disk_free_trend", 0, LogEntryType.Warning, "sensor-1");
        handlings.Save(new IssueHandling { HostName = "host-2", IssueKey = prtgKey, Date = day.AddDays(-3), Status = IssueHandlingStatuses.FalsePositive, UpdatedAt = day });
        // 有案件的問題同時也有處理列：案件優先、不再看處理列
        cases.Save(new IssueCase { CaseId = "case-with-row", HostName = "host-2", IssueKey = Key("Mem", 44), Status = IssueHandlingStatuses.Open });
        handlings.Save(new IssueHandling { HostName = "host-2", IssueKey = Key("Mem", 44), Date = day, Status = IssueHandlingStatuses.Resolved, UpdatedAt = day });

        HostIssueOccurrence Occ(long host, string key, DateTime seen) => new()
        { HostId = host, IssueKey = key, LastSeen = seen, SeverityRank = (int)IssueSeverity.High };

        var occurrences = new[]
        {
            Occ(h1.HostId, Key("disk", 11), day),
            Occ(h2.HostId, Key("cpu", 22), day.AddHours(5)),
            Occ(h2.HostId, Key("Net", 33), day),
            Occ(h2.HostId, prtgKey, day),
            Occ(h2.HostId, Key("Mem", 44), day),
            Occ(h2.HostId, Key("None", 55), day),
            Occ(h1.HostId, Key("Cpu", 22), day),     // 另一台主機沒有這筆處理列
            Occ(999, Key("Disk", 11), day)           // 不存在的主機
        };
        var sb = new StringBuilder();
        foreach (var item in resolver.Resolve(occurrences, day, day))
            sb.AppendLine($"{item.Host.HostName}|{item.Occurrence.IssueKey}|{item.Status}|case={item.OpenCase?.CaseId}|" +
                          $"handling={item.Handling?.Status}/{item.Handling?.Note}|inherited={item.InheritedFalsePositive}");
        return sb.ToString().TrimEnd();
    }

    // ── 2-11 交辦摘要 ─────────────────────────────────────────────

    /// <summary>3 張單、同一位處理人、一封摘要；回傳信件本文與寄送期間 <c>IHostStore.GetAll</c> 的呼叫次數</summary>
    public static async Task<(string Body, int HostReads)> Digest()
    {
        using var fx = new EfSqliteFixture();
        var settings = new FakeSystemSettingsStore();
        var sender = new FakeSmtpMailSender();
        var hosts = new FakeHostStore();
        var users = new FakeUserStore();
        var cases = new FakeIssueCaseStore();
        var orders = new FakeWorkOrderStore(cases);
        var outbox = new WorkOrderOutboxStore(fx.Blob(WorkOrderOutboxStore.BlobKey));
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(fx.Blob("schedule_options")));
        var mail = new MailNotificationService(
            settings, sender, hosts, users, new FakeUserGroupStore(), new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), new MailNotifyStateStore(fx.Blob("mail_notify_state")),
            freshness, outbox, orders, cases, new FakeRuleStore(), new FakeIssueOwnerStore());
        settings.Update(s =>
        {
            s.MailEnabled = true;
            s.MailNotifyWorkOrders = true;
            s.MailWorkOrderDigestTimes = new List<string> { "08:00", "12:00" };
        });
        var alice = users.Upsert(new WebUser { Account = "alice", Email = "alice@test.local", Active = true });

        var d = new DateTime(2026, 10, 20);
        hosts.Upsert(new WebHost { HostName = "host-01", Active = true });
        hosts.Upsert(new WebHost { HostName = "host-02", Active = false });
        hosts.Upsert(new WebHost { HostName = "host-03", Active = true });
        foreach (var (label, count) in new[] { ("Schannel 36888", 3), ("DCOM 10016", 2), ("Disk 7", 1) })
        {
            var id = orders.Insert(new WorkOrder { IssueLabel = label, HandlerId = alice.UserId, Note = "備註-" + label, CreatedAt = d });
            for (var i = 1; i <= count; i++)
                cases.Save(new IssueCase
                {
                    CaseId = $"case-{id}-{i:D2}", HostName = $"host-{i:D2}", IssueKey = $"key-{id}", IssueLabel = label,
                    WorkOrderId = id, Status = IssueHandlingStatuses.Open
                });
            outbox.Enqueue(new WorkOrderOutboxItem
            {
                Kind = WorkOrderOutboxKinds.Created, WorkOrderId = id, RecipientUserId = alice.UserId,
                EnqueuedAt = d.AddHours(11).AddMinutes(id), ActorAccount = "admin"
            });
        }

        hosts.ResetCallCounts();
        await mail.CheckAndSendWorkOrderDigestAsync(d.AddHours(12));
        var sent = Assert.Single(sender.Sent);
        return (sent.Message.Body, hosts.GetAllCallCount);
    }
}
