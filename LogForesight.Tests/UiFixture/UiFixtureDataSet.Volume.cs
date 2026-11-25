using LogForesight.Core.Models;
using LogForesight.Core.Persistence;

namespace LogForesight.Tests;

/// <summary>
/// 量體疊加層（可用性實測用）：設 <c>LF_UI_FIXTURE_VOLUME=&lt;主機數&gt;</c> 時，在邊界資料集之上再加
/// N 台主機 × 28 天的分析紀錄、十個主機群組與數十張經 <see cref="WorkOrderCoordinator"/> 建立的交辦單，
/// 讓各角色的操作步驟能在「資料量有一定程度」時實測。未設環境變數時完全不執行，不影響邊界資料集與其斷言。
/// </summary>
public sealed partial class UiFixtureDataSet
{
    public const string VolumeEnvVar = "LF_UI_FIXTURE_VOLUME";

    private void BuildVolume(int? hostCountOverride = null)
    {
        var hostCount = hostCountOverride ?? (int.TryParse(Environment.GetEnvironmentVariable(VolumeEnvVar), out var fromEnv) ? fromEnv : 0);
        if (hostCount <= 0) return;

        var rnd = new Random(20261124);
        var groups = new List<long>();
        for (var g = 1; g <= 10; g++)
            groups.Add(_hostGroupStore.Upsert(new HostGroup { GroupName = $"vol-機房{g:D2}", Active = true }).GroupId);

        // 維運一組看得到前五個量體群組、受限檢視者看第六個
        var access = new GroupAccessStore(Backend.Blob("group_access"));
        access.SetForUserGroup(UserGroupIds["ops"], new[] { HostGroupIds["rack-a"], HostGroupIds["dmz"], HostGroupIds["bulk"] }.Concat(groups.Take(5)));
        access.SetForUserGroup(UserGroupIds["viewers"], new[] { HostGroupIds["dmz"], groups[5] });

        var roles = new[] { "AD 網域控制站", "檔案伺服器", "SQL Server 資料庫", "IIS 網站", "列印伺服器", "備份伺服器", "應用程式伺服器", "跳板機", "" };
        var winSentinel = new SentinelStore(Backend.Blob("sentinels")).GetAll().First(s => s.Os == WebHost.OsWindows);
        var linuxSentinel = new SentinelStore(Backend.Blob("sentinels")).GetAll().First(s => s.Os == WebHost.OsLinux);
        var owners = new[] { UserIds["ui-handler"], UserIds["ui-same-a"], UserIds["ui-owner"] };
        var volHosts = new List<WebHost>();
        _hostStore.MutateBatch(hosts =>
        {
            var next = hosts.Max(h => h.HostId) + 1;
            for (var i = 1; i <= hostCount; i++)
            {
                var linux = i % 10 == 0;
                var groupIdx = (i - 1) % groups.Count;
                var host = new WebHost
                {
                    HostId = next++,
                    HostName = linux ? $"vol-lnx{i:D4}" : $"VOL-WIN{i:D4}",
                    IpAddress = $"10.50.{i / 250}.{i % 250 + 1}", IpUpdatedAt = Yesterday,
                    Os = linux ? WebHost.OsLinux : WebHost.OsWindows, Source = "netiq", Active = true,
                    SentinelId = linux ? linuxSentinel.SentinelId : winSentinel.SentinelId,
                    NetiqServer = linux ? linuxSentinel.Name : winSentinel.Name,
                    RoleDesc = roles[i % roles.Length], Tier = i % 10 == 3 ? WebHost.TierCore : WebHost.TierStandard,
                    LastReportAt = Yesterday.AddHours(3), CreatedAt = DateTime.Today.AddDays(-120),
                    GroupIds = new List<long> { groups[groupIdx] },
                    OwnerUserIds = i % 7 == 0 ? new List<long> { owners[i % owners.Length] } : new List<long>()
                };
                hosts.Add(host);
                volHosts.Add(host);
                _hostsById[host.HostId] = host;
            }
        });

        // 簽章池：前兩個幾乎每台都有（雜訊）、中段依冪次遞減、尾端 5% 為高嚴重度
        var winPool = BuildVolumePool(rnd);
        foreach (var host in volHosts)
        {
            var linux = host.Os == WebHost.OsLinux;
            var hostSeed = rnd.Next();
            var hostRnd = new Random(hostSeed);
            // 每台主機挑一組「常駐」問題，每天在其中抽樣，讓趨勢與依問題聚合有重複形狀
            var resident = Enumerable.Range(0, 10).Select(_ => PickPowerLaw(hostRnd, winPool.Count)).Distinct().ToList();
            var badHost = hostRnd.NextDouble() < 0.08;
            for (var d = MaxDaysAgo; d >= 1; d--)
            {
                if (hostRnd.NextDouble() < 0.03) continue;   // 偶有缺漏日
                var issues = new List<LogIssueSignature>();
                if (linux)
                {
                    issues.Add(LinuxSig("sshd", "sshd:auth-fail", IssueCategory.Security, IssueSeverity.Medium, hostRnd.Next(3, 400), null,
                        "Failed password for invalid user admin from 203.0.113.7 port 52211 ssh2"));
                    issues.Add(LinuxSig("systemd", "systemd:unit-failed", IssueCategory.Service, IssueSeverity.Low, hostRnd.Next(1, 20), null,
                        "Failed to start Daily apt upgrade and clean activities."));
                    if (badHost && hostRnd.NextDouble() < 0.4)
                        issues.Add(LinuxSig("kernel", "kernel:io-error", IssueCategory.Storage, IssueSeverity.High, hostRnd.Next(1, 30), null,
                            "blk_update_request: I/O error, dev sda, sector 123456"));
                }
                else
                {
                    issues.Add(Clone(winPool[0], hostRnd.Next(5, 120)));
                    if (hostRnd.NextDouble() < 0.9) issues.Add(Clone(winPool[1], hostRnd.Next(1, 30)));
                    foreach (var idx in resident.Where(_ => hostRnd.NextDouble() < 0.45))
                        issues.Add(Clone(winPool[idx], hostRnd.Next(1, 60)));
                    if (badHost && hostRnd.NextDouble() < 0.35)
                        issues.Add(Clone(winPool[winPool.Count - 1 - hostRnd.Next(0, 6)], hostRnd.Next(1, 25)));
                }

                issues = issues.GroupBy(i => (i.Source, i.EventId, i.EventKey)).Select(g => g.First()).ToList();
                var risk = issues.Any(i => i.ElevatesDayRisk || i.Severity == IssueSeverity.High) ? RiskLevels.High
                    : issues.Count(i => i.Severity == IssueSeverity.Medium) >= 3 ? RiskLevels.Medium : RiskLevels.Low;
                var r = new DailyAnalysisRecord
                {
                    HostId = host.HostId, Host = host.HostName, Date = DayOf(d), RiskLevel = risk,
                    LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
                    SecurityLogAvailable = true, ChannelsRead = new List<string> { "System", "Application", "Security" }
                };
                Fill(r, issues);
                if (risk != RiskLevels.Low && hostRnd.NextDouble() < 0.6)
                    Ai(r, $"{host.HostName} 出現{(risk == RiskLevels.High ? "硬體或儲存異常" : "多項服務錯誤")}",
                        "當日有數項錯誤重複發生，建議檢查相關服務與硬體狀態。", "與前幾天相比次數持平。", "請確認服務狀態並檢查磁碟健康。");
                Save(r);
            }
        }

        WriteVolumeWorkOrders(volHosts, winPool, rnd);
    }

    private static LogIssueSignature Clone(LogIssueSignature s, int count)
    {
        var c = Sig(s.LogName, s.Source, s.EventId, s.Category, s.Severity, count, s.ElevatesDayRisk, s.KnownIssue, s.RuleId, s.Trend);
        return c;
    }

    private static int PickPowerLaw(Random rnd, int n)
    {
        // 2..n-7 之間冪次分布（前段常見、尾段稀有）；0、1 與最後 6 個另外處理
        var x = Math.Pow(rnd.NextDouble(), 2.2);
        return 2 + (int)(x * (n - 9));
    }

    private static List<LogIssueSignature> BuildVolumePool(Random rnd)
    {
        var pool = new List<LogIssueSignature>
        {
            Dcom(10), Schannel(5),
            W32Time(3), Scm7034(2), AppError(2),
            Sig("System", "Service Control Manager", 7000, IssueCategory.Service, IssueSeverity.Medium, 1, known: "服務無法啟動"),
            Sig("System", "Service Control Manager", 7031, IssueCategory.Service, IssueSeverity.Medium, 1, known: "服務意外終止並將重新啟動"),
            Sig("System", "Microsoft-Windows-GroupPolicy", 1129, IssueCategory.Config, IssueSeverity.Medium, 1, known: "群組原則無法連線網域控制站"),
            Sig("System", "NETLOGON", 5719, IssueCategory.Config, IssueSeverity.Medium, 1, known: "無法與網域控制站建立安全通道"),
            Sig("Application", "Application Hang", 1002, IssueCategory.Service, IssueSeverity.Medium, 1),
            Sig("Application", ".NET Runtime", 1026, IssueCategory.Service, IssueSeverity.Medium, 1),
            Sig("Application", "MSSQLSERVER", 17806, IssueCategory.Security, IssueSeverity.Medium, 1),
            Sig("Application", "MSSQLSERVER", 833, IssueCategory.Storage, IssueSeverity.Medium, 1, known: "SQL Server I/O 要求逾時"),
            Sig("System", "Microsoft-Windows-WindowsUpdateClient", 20, IssueCategory.Config, IssueSeverity.Low, 1),
            Sig("System", "Tcpip", 4227, IssueCategory.Config, IssueSeverity.Low, 1),
            Sig("System", "volsnap", 25, IssueCategory.Storage, IssueSeverity.Medium, 1, known: "陰影複製被刪除"),
            Sig("System", "Microsoft-Windows-Kerberos-Key-Distribution-Center", 14, IssueCategory.Security, IssueSeverity.Medium, 1),
            Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, IssueCategory.Security, IssueSeverity.Medium, 1, known: "登入失敗"),
            Sig("System", "LsaSrv", 40960, IssueCategory.Security, IssueSeverity.Medium, 1),
            Sig("System", "Microsoft-Windows-DNS-Client", 1014, IssueCategory.Config, IssueSeverity.Low, 1),
        };
        for (var i = 0; i < 80; i++)
            pool.Add(Sig("Application", $"Vendor-App-{i + 1:D2}", 1000 + i, i % 3 == 0 ? IssueCategory.Service : IssueCategory.Config,
                i % 4 == 0 ? IssueSeverity.Medium : IssueSeverity.Low, 1));
        // 尾端：高嚴重度（硬體／儲存）
        pool.Add(Sig("System", "Ntfs", 55, IssueCategory.Storage, IssueSeverity.High, 1, elevates: true, known: "檔案系統結構損毀", trend: IssueTrend.New));
        pool.Add(Sig("System", "Microsoft-Windows-WHEA-Logger", 17, IssueCategory.Hardware, IssueSeverity.High, 1, elevates: true, known: "硬體可更正錯誤", trend: IssueTrend.Rising));
        pool.Add(Sig("System", "Microsoft-Windows-WHEA-Logger", 18, IssueCategory.Hardware, IssueSeverity.High, 1, elevates: true, known: "硬體不可更正錯誤"));
        pool.Add(KernelPower41(1));
        pool.Add(Sig("System", "storahci", 129, IssueCategory.Storage, IssueSeverity.High, 1, elevates: true, known: "儲存控制器重設"));
        pool.Add(Disk153(1));
        return pool;
    }

    /// <summary>交辦單：挑最新一天有高／中嚴重度問題的主機，依問題分組建單（經協調器，案件與逐日處理列一致）</summary>
    private void WriteVolumeWorkOrders(List<WebHost> volHosts, List<LogIssueSignature> pool, Random rnd)
    {
        var coordinator = new WorkOrderCoordinator(
            Backend.WorkOrderStore(), Backend.IssueCaseStore(), Backend.IssueHandlingStore(),
            new IssueCaseCoordinator(Backend.IssueCaseStore(), Backend.IssueHandlingStore(), Backend.RecordHandlingStore(),
                Backend.RecordStore(), _hostStore, new IssueOwnerStore(Backend.Blob("issue_owners"), TimeProvider.System)),
            Backend.RecordHandlingStore(), _hostStore);

        var handlers = new[] { "ui-handler", "ui-handler-long", "ui-same-a", "ui-same-b", "ui-handler", "ui-handler" };
        var visibleToOps = volHosts.Where((h, i) => (i % 10) < 5).Select(h => h.HostName).ToHashSet();
        var records = Backend.RecordStore();
        var created = 0;
        // 由舊到新建單：併入既有單時「最近新增」不會早於該單的建立時間（LastAppendedAt >= CreatedAt）
        foreach (var daysAgo in new[] { 8, 5, 3, 2, 1 })
        {
            var date = DayOf(daysAgo);
            var dayRecords = records.Query(new RecordQueryFilter { From = date, To = date })
                .Where(r => visibleToOps.Contains(r.Host)).ToList();
            var byIssue = dayRecords
                .SelectMany(r => r.TopIssues.Where(i => i.Severity != IssueSeverity.Low).Select(i => (r.Host, Issue: i)))
                .GroupBy(x => IssueSignatureKey.For(x.Issue))
                .OrderByDescending(g => g.First().Issue.Severity).ThenByDescending(g => g.Count())
                .Take(daysAgo == 1 ? 18 : 8);
            foreach (var g in byIssue)
            {
                var first = g.First().Issue;
                var account = handlers[created % handlers.Length];
                var members = g.Take(rnd.Next(1, 40)).Select(x => new WorkOrderMember
                {
                    HostName = x.Host, IssueKey = g.Key, IssueLabel = first.SourceEventLabel, TriggerDate = date
                }).ToList();
                var outcome = coordinator.Create(new WorkOrderCreateRequest
                {
                    Source = first.Source, EventId = first.EventId, IssueLabel = first.SourceEventLabel,
                    HandlerId = UserIds[account], Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts,
                    Note = created % 3 == 0 ? "請於本週內確認並回覆處理結果。" : null,
                    DueDate = created % 5 == 0 ? DateTime.Today.AddDays(-2) : DateTime.Today.AddDays(rnd.Next(2, 14)),
                    Members = members, ReassignConflicts = false,
                    Actor = new WorkOrderActor { ActorId = UserIds["ui-admin"], ActorAccount = "ui-admin", OccurredAt = date.AddHours(10) }
                });
                created++;
                // 約三分之一已有回覆（部分成員已處理）
                if (outcome.WorkOrderId > 0 && created % 3 == 1)
                {
                    var caseIds = Backend.IssueCaseStore().GetOpenByHandler(UserIds[account])
                        .Where(c => c.WorkOrderId == outcome.WorkOrderId).Select(c => c.CaseId).Take(Math.Max(1, members.Count / 2)).ToList();
                    if (caseIds.Count > 0)
                        coordinator.Reply(outcome.WorkOrderId, caseIds, IssueHandlingStatuses.Resolved, "已重新啟動服務，觀察後無再發生。", null,
                            new WorkOrderActor { ActorId = UserIds[account], ActorAccount = account, OccurredAt = date.AddHours(16) });
                }
            }
        }
    }
}
