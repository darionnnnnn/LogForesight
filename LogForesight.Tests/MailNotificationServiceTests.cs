using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 郵件通知（docs/archive/FEEDBACK-15-PLAN.md 批次D，回饋十七輪批次A/B 全面修正日期與權限範圍）。
/// 三路觸發（執行結束後摘要／高風險即時／每日每週定時彙總）全部打在 fake
/// <see cref="ISmtpMailSender"/> 上，不碰真實 SMTP。
///
/// **紀錄一律用「昨天」造**：分析永遠只產出到昨天（<see cref="MissingDateFinder"/>／
/// AnalysisOrchestrator 固定分析 yesterday），用 <c>DateTime.Today</c> 造紀錄是生產環境
/// 永不出現的狀態——這正是回饋十七輪頭號發現逃過先前 1828 條測試的原因：兩端的日期約定
/// 不一致，而測試同時扮演兩端，各自用「今天」互相對上，卻與生產環境的實際行為相反。
/// </summary>
public class MailNotificationServiceTests : IDisposable
{
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly EfSqliteFixture _fx = new();

    /// <summary>分析永遠只產出到昨天——所有測試紀錄以此為基準日，而非 DateTime.Today</summary>
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess, _records, _handlings,
            new MailNotifyStateStore(_fx.Blob("mail_notify_state")), _issueOwners, _issueAggregates);

    private static DailyAnalysisRecord Record(long hostId, string host, DateTime date, string riskLevel,
        string headline = "", string? riskBasis = null, params LogIssueSignature[] issues) => new()
    {
        HostId = hostId,
        Host = host,
        Date = date,
        RiskLevel = riskLevel,
        Headline = headline,
        RiskBasis = riskBasis ?? "",
        TopIssues = issues.ToList()
    };

    /// <summary>啟用郵件＋填妥連線與收件人的基準設定，測試只覆寫要驗的那一項（同 SystemSettingsServiceTests 慣例）</summary>
    private void EnableMail(Action<SystemSettings>? configure = null) =>
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string> { "ops@test.local" };
            configure?.Invoke(s);
        });

    /// <summary>建一個看得到所有主機的啟用帳號（ViewAll 角色），email 與參數相同——供需要
    /// 「收件人能解析到帳號、且可見範圍涵蓋全部」的測試使用（回饋十七輪批次B-4）。</summary>
    private WebUser CreateViewAllAccount(string email)
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        return _users.Upsert(new WebUser
        {
            Account = email, Email = email, Active = true, GroupIds = new List<long> { group.GroupId }
        });
    }

    // ── NotifyAfterRunAsync：執行摘要 + 高風險即時通知 ──────────────────────

    [Fact]
    public async Task NotifyAfterRunAsync_MailEnabled為false時不寄送任何信()
    {
        _settingsStore.Update(s => { s.MailEnabled = false; s.MailOnRunCompleted = true; s.MailUrgentEnabled = true; });
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.Empty(_sender.Sent);
    }

    /// <summary>回饋十七輪頭號發現的綁定測試：用真實 MissingDateFinder 產生「還沒有分析紀錄的日期」，
    /// 驗證窗口查詢確實涵蓋分析實際會產出紀錄的日期（昨天），而非原本錯誤查詢的「今天」。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_窗口涵蓋MissingDateFinder會回補的日期()
    {
        var store = new FakeAnalysisRecordReader();
        var missingDates = MissingDateFinder.Find(store, lookbackDays: 3);
        // 今天永遠不在缺漏日清單裡（offset 從 1 起算）——這正是頭號發現的根因
        Assert.DoesNotContain(DateTime.Today, missingDates);
        Assert.Contains(Yesterday, missingDates);

        var owner = CreateViewAllAccount("ops@test.local");
        var host = _hosts.Upsert(new WebHost { HostName = "host1" });
        EnableMail(s => s.MailUrgentEnabled = true);
        foreach (var date in missingDates)
        {
            _records.Add(Record(host.HostId, "host1", date, RiskLevels.High));
        }

        await Create().NotifyAfterRunAsync();

        Assert.NotEmpty(_sender.Sent);
        foreach (var date in missingDates)
        {
            Assert.Contains(_sender.Sent, s => s.Message.Body.Contains(date.ToString("yyyy-MM-dd")));
        }
        _ = owner;
    }

    /// <summary>只有「今天」的紀錄（分析實際上永不產生的狀態）不寄——佐證窗口的右邊界正確
    /// （回饋十七輪批次A-1：查詢窗口是「近 N 天、不含今天」）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_只有今天的紀錄時不寄()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailUrgentEnabled = true; });
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_執行摘要只納入達門檻的紀錄()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailMinRiskLevel = RiskLevels.Medium; });
        var host1 = _hosts.Upsert(new WebHost { HostName = "host1" });
        var host2 = _hosts.Upsert(new WebHost { HostName = "host2" });
        _records.Add(Record(host1.HostId, "host1", Yesterday, RiskLevels.High, "host1標頭"));
        _records.Add(Record(host2.HostId, "host2", Yesterday, RiskLevels.Low, "host2標頭"));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1", sent.Message.Body);
        Assert.DoesNotContain("host2", sent.Message.Body);
    }

    /// <summary>回饋十六輪批次A-5：達門檻紀錄超過 50 筆時，明細只列前 50 筆並補一行
    /// 「其餘 N 台請至站台檢視」——2000 台規模下 MailMinRiskLevel=中 可能有 600+ 筆，
    /// 純文字信不該無上限列出去。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_執行摘要超過上限時截斷並提示其餘筆數()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailMinRiskLevel = RiskLevels.Medium; });
        for (var i = 0; i < 55; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"host{i}" });
            _records.Add(Record(host.HostId, $"host{i}", Yesterday, RiskLevels.High));
        }

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host0", sent.Message.Body);
        Assert.Contains("host49", sent.Message.Body);
        Assert.DoesNotContain("host50", sent.Message.Body);
        Assert.Contains("其餘 5 台請至站台", sent.Message.Body);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_無達門檻紀錄時不寄摘要信()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailOnRunCompleted = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.Low));

        await Create().NotifyAfterRunAsync();

        Assert.Empty(_sender.Sent);
    }

    /// <summary>執行摘要改用 SummarySentKeys 去重（回饋十七輪批次A-2）：同一筆已摘要過的
    /// 主機日，下次執行的窗口查詢仍會撈到（14 天窗口內），但不該重複摘要。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_執行摘要同一主機日只摘要一次()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailOnRunCompleted = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);

        // 同一天窗口內再次觸發執行（排程輪詢或手動再次觸發）：不應重複摘要
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_高風險即時通知同一主機日只寄一次()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High, "host1標頭"));
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);

        // 模擬同一天又觸發一次執行（排程輪詢或手動再次觸發）：不應重複寄送
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_中風險不觸發高風險即時通知()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.Medium));

        await Create().NotifyAfterRunAsync();

        Assert.Empty(_sender.Sent);
    }

    // ── RiskLevels 查詢下推（回饋十八輪批次A）─────────────────────────────
    // 2000 台規模下每次執行後全表查 14 天會反序列化上萬列（見 docs/archive/FEEDBACK-18-PLAN.md 批次A）；
    // 修法是把風險過濾下推進 RecordQueryFilter，記憶體判定原樣保留（雙保險，語意不變）。
    // 這裡直接斷言 FakeAnalysisRecordQuery.LastFilter，而不是只看寄信結果——只看寄信結果的話，
    // 「下推漏了某個等級」這種寫錯不會被任何既有測試抓到（服務端的記憶體過濾會把漏洞蓋住）。

    [Fact]
    public async Task NotifyAfterRunAsync_摘要與緊急同開時下推門檻以上等級的聯集()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailUrgentEnabled = true; s.MailMinRiskLevel = RiskLevels.Medium; });
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.NotNull(_records.LastFilter);
        Assert.Equal(new[] { RiskLevels.High, RiskLevels.Medium }, _records.LastFilter!.RiskLevels);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_只開緊急時下推只查高風險()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.NotNull(_records.LastFilter);
        Assert.Equal(new[] { RiskLevels.High }, _records.LastFilter!.RiskLevels);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_摘要開啟時下推門檻以上等級()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailMinRiskLevel = RiskLevels.High; });
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.NotNull(_records.LastFilter);
        Assert.Equal(new[] { RiskLevels.High }, _records.LastFilter!.RiskLevels);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_摘要與緊急皆未開啟時不查詢也不寄送()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail();
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.Null(_records.LastFilter);
        Assert.Empty(_sender.Sent);
    }

    /// <summary>回饋十八輪批次A-3：每日／週報彙總同樣下推門檻以上等級（與摘要同一手法）。</summary>
    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_下推門檻以上等級()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailDailyEnabled = true; s.MailDailyTime = "08:00"; s.MailMinRiskLevel = RiskLevels.Medium; });
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().CheckAndSendDailyWeeklyAsync(new DateTime(2026, 8, 11, 9, 0, 0));

        Assert.NotNull(_records.LastFilter);
        Assert.Equal(new[] { RiskLevels.High, RiskLevels.Medium }, _records.LastFilter!.RiskLevels);
    }

    /// <summary>回饋十六輪批次A-1：高風險即時通知改按收件人聚合——全域收件人與非全域的
    /// 主機負責人各自收到「一封」信，不再合併成單封多收件人的信。全域收件人本身要能解析到
    /// 一個 ViewAll 帳號才看得到主機明細（回饋十七輪批次B-4）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_全域收件人與負責人分別各收一封()
    {
        CreateViewAllAccount("ops@test.local");
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains(_sender.Sent, s => s.Message.To.Count == 1 && s.Message.To[0] == "ops@test.local");
        Assert.Contains(_sender.Sent, s => s.Message.To.Count == 1 && s.Message.To[0] == "owner1@test.local");
        Assert.Contains(_sender.Sent, s => s.Message.To[0] == "owner1@test.local" && s.Message.Body.Contains("host1"));
    }

    /// <summary>負責人信箱與全域收件人相同（不分大小寫）時，不重複收信——他已經在全域信
    /// 裡看到全部內容，不需要再收一封只列自己主機的信。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_負責人與全域收件人相同時不重複收信()
    {
        var owner = CreateViewAllAccount("OPS@test.local");
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Single(sent.Message.To);
    }

    // ── 問題負責人優先於主機負責人（回饋十八輪批次F）─────────────────────────

    private static LogIssueSignature Issue(string source, int eventId) =>
        new() { LogName = "System", Source = source, EventId = eventId, Severity = IssueSeverity.High };

    /// <summary>這天的問題命中問題負責人規則時，通知對象是問題負責人，不是主機負責人——
    /// 「優先取代」不是「疊加」。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_問題命中規則時通知問題負責人不通知主機負責人()
    {
        CreateViewAllAccount("ops@test.local");
        var hostOwner = _users.Upsert(new WebUser { Account = "hostowner", Email = "hostowner@test.local", Active = true });
        var issueOwner = _users.Upsert(new WebUser { Account = "issueowner", Email = "issueowner@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { hostOwner.UserId } });
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { issueOwner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High, issues: Issue("disk", 153)));

        await Create().NotifyAfterRunAsync();

        Assert.Equal(2, _sender.Sent.Count);   // 全域收件人 ops + 問題負責人 issueowner
        Assert.Contains(_sender.Sent, s => s.Message.To[0] == "issueowner@test.local" && s.Message.Body.Contains("host1"));
        Assert.DoesNotContain(_sender.Sent, s => s.Message.To[0] == "hostowner@test.local");
    }

    /// <summary>當天問題都沒命中任何問題負責人規則時，落回既有的主機負責人行為。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_問題未命中規則時落回主機負責人()
    {
        CreateViewAllAccount("ops@test.local");
        var hostOwner = _users.Upsert(new WebUser { Account = "hostowner", Email = "hostowner@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { hostOwner.UserId } });
        _issueOwners.Upsert(new IssueProfile { SourceName = "network", EventId = 999, OwnerUserIds = new List<long> { hostOwner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High, issues: Issue("disk", 153)));

        await Create().NotifyAfterRunAsync();

        Assert.Contains(_sender.Sent, s => s.Message.To[0] == "hostowner@test.local");
    }

    /// <summary>同一批通知裡，不同主機日各自路由到不同對象——這是逐 record 的判定，不是全站開關。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_同批次不同主機日各自路由到不同對象()
    {
        CreateViewAllAccount("ops@test.local");
        var hostOwner = _users.Upsert(new WebUser { Account = "hostowner", Email = "hostowner@test.local", Active = true });
        var issueOwner = _users.Upsert(new WebUser { Account = "issueowner", Email = "issueowner@test.local", Active = true });
        var host1 = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { hostOwner.UserId } });
        var host2 = _hosts.Upsert(new WebHost { HostName = "host2", OwnerUserIds = new List<long> { hostOwner.UserId } });
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { issueOwner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host1.HostId, "host1", Yesterday, RiskLevels.High, issues: Issue("disk", 153)));
        _records.Add(Record(host2.HostId, "host2", Yesterday, RiskLevels.High, issues: Issue("network", 999)));

        await Create().NotifyAfterRunAsync();

        Assert.Contains(_sender.Sent, s => s.Message.To[0] == "issueowner@test.local" && s.Message.Body.Contains("host1") && !s.Message.Body.Contains("host2"));
        Assert.Contains(_sender.Sent, s => s.Message.To[0] == "hostowner@test.local" && s.Message.Body.Contains("host2") && !s.Message.Body.Contains("host1"));
    }

    /// <summary>回饋十八輪體檢輪修正：全域收件人若解析到的帳號本身是問題負責人，即使他不在任何
    /// 授權群組、也不是主機負責人，仍要在自己收到的那份摘要信裡看到該主機——原本
    /// GetVisibleHostIds(userId) 只聯集了群組授權與主機負責人兩條路徑，漏了問題負責人這第三條，
    /// 該收件人的明細會被過濾成空、只看得到統計行。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_全域收件人是問題負責人時看得到該主機明細()
    {
        var owner = _users.Upsert(new WebUser { Account = "issueowner", Email = "issueowner@test.local", Active = true });
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { owner.UserId } });
        var host = _hosts.Upsert(new WebHost { HostName = "host1" });
        _issueAggregates.HostIdsForResult = new HashSet<long> { host.HostId };
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailRecipients = new List<string> { "issueowner@test.local" }; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High, issues: Issue("disk", 153)));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("issueowner@test.local", sent.Message.To[0]);
        Assert.Contains("host1", sent.Message.Body);
    }

    /// <summary>全域收件人若能解析到帳號且可見範圍涵蓋全部主機，收到的信涵蓋「本次全部」
    /// pending 主機，不只是自己負責的那些（回饋十七輪批次B-4：可見範圍過濾，不是全域廣播）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_全域收件人可見範圍涵蓋時信件包含全部高風險主機()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        var host1 = _hosts.Upsert(new WebHost { HostName = "host1" });
        var host2 = _hosts.Upsert(new WebHost { HostName = "host2" });
        _records.Add(Record(host1.HostId, "host1", Yesterday, RiskLevels.High, "host1事故"));
        _records.Add(Record(host2.HostId, "host2", Yesterday, RiskLevels.High, "host2事故"));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1", sent.Message.Body);
        Assert.Contains("host2", sent.Message.Body);
    }

    /// <summary>
    /// 回饋十七輪批次B-4 核心：全域收件人的 email 若對應不到任何啟用中的使用者帳號
    /// （自由文字地址，如共用信箱，權限無從判定），只收統計行，不含任何主機名。
    /// </summary>
    [Fact]
    public async Task NotifyAfterRunAsync_收件人對應不到帳號時只收統計行不含主機明細()
    {
        // 不建立任何 WebUser——ops@test.local 純粹是自由文字地址
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.DoesNotContain("host1", sent.Message.Body);
    }

    /// <summary>對應到帳號但可見範圍不含該主機的收件人，只收統計行——與「對應不到帳號」
    /// 是不同情境，但呈現結果一致（回饋十七輪批次B-4）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_對應到帳號但看不到該主機時只收統計行()
    {
        // 一般群組（非 ViewAll、無主機授權）——帳號存在但可見範圍是空集合
        var group = _userGroups.Upsert(new UserGroup { GroupName = "viewers", Role = UserRole.User, Active = true });
        _users.Upsert(new WebUser { Account = "ops", Email = "ops@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.DoesNotContain("host1", sent.Message.Body);
        Assert.Contains("站台", sent.Message.Body);
    }

    /// <summary>回饋十七輪體檢輪抓到的真實漏寄：收件人只有部分可見範圍時（看得到host1、看不到
    /// host2），舊版 BuildUrgentMessage 的統計行是用該收件人自己過濾後的明細現算（此例只算得出
    /// 1），host2 連統計數字都沒被提到——卻因為 ops 那封信寄送成功，被 MarkSent 的 zero-coverage
    /// fallback 標記為已通知，成了真正意義上「沒人被告知過」的靜默漏寄。修法對齊
    /// SendRunSummaryAsync 既有做法：統計行改用全站聚合（未經過濾的 pending.Count），
    /// 讓「coverage 為空的紀錄仍由統計行如實反映其存在」這個 MarkSent 文件註解的前提，
    /// 在高風險即時通知這條路徑下也成立，不是只有摘要信才成立。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_部分可見範圍時統計行仍反映看不到的主機()
    {
        var viewerGroup = _userGroups.Upsert(new UserGroup { GroupName = "viewers", Role = UserRole.User, Active = true });
        _users.Upsert(new WebUser { Account = "ops", Email = "ops@test.local", Active = true, GroupIds = new List<long> { viewerGroup.GroupId } });
        _groupAccess.ReplaceAll(new[] { new GroupAccess { UserGroupId = viewerGroup.GroupId, HostGroupId = 10 } });

        var host1 = _hosts.Upsert(new WebHost { HostName = "host1", GroupIds = new List<long> { 10 } });
        var host2 = _hosts.Upsert(new WebHost { HostName = "host2", GroupIds = new List<long> { 20 } });
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(host1.HostId, "host1", Yesterday, RiskLevels.High));
        _records.Add(Record(host2.HostId, "host2", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1", sent.Message.Body);
        Assert.DoesNotContain("host2", sent.Message.Body);
        // 全站聚合統計行反映真實筆數 2（含 ops 看不到的 host2），不是只算 ops 自己可見的 1 筆
        Assert.Contains("2 筆", sent.Message.Body);
    }

    /// <summary>內容廣泛化（回饋十七輪批次B-3，使用者回饋「其他 8」）：信件不含 Headline
    /// 與 RiskBasis（判定依據）等具體錯誤內容，只留數量與等級等較廣泛的資訊。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_信件內容不含Headline與判定依據()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High,
            headline: "獨特標頭文字ABC123", riskBasis: "獨特判定依據XYZ789"));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.DoesNotContain("獨特標頭文字ABC123", sent.Message.Body);
        Assert.DoesNotContain("獨特判定依據XYZ789", sent.Message.Body);
        Assert.DoesNotContain("判定依據", sent.Message.Body);
    }

    /// <summary>沒有任何收件人（全域清單空、且無負責人）時，pending 完全不標記——
    /// 補設定後下次執行要能自動補寄（回饋十六輪體檢發現2b：先啟用通知後補收件人的部署順序）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_無任何收件人時不標記已寄且下次補寄()
    {
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string>();
            s.MailUrgentEnabled = true;
        });
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Empty(_sender.Sent);

        // 補上收件人設定後，同一批高風險主機日要能自動補寄
        CreateViewAllAccount("ops@test.local");
        _settingsStore.Update(s => s.MailRecipients = new List<string> { "ops@test.local" });
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
    }

    /// <summary>標記語意是「涵蓋此 record 的信全部寄成功才標記」（回饋十六輪體檢發現2a 的根修：
    /// 原本吞掉例外、無條件全部標記已寄）——只要有一位涵蓋到這個 record 的收件人失敗，這個
    /// record 就整批留到下次重寄，即使其他收件人已經成功過也會再收到一次重複信。這是刻意的
    /// 保守取捨：寧可讓已收到的人重複收到，也不讓任何一位該收到的人漏收。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_有收件人失敗時該record下次對所有收件人重寄()
    {
        CreateViewAllAccount("ops@test.local");
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", Yesterday, RiskLevels.High));

        _sender.ThrowOnSendForRecipient = "owner1@test.local";
        var service = Create();
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);              // 只有全域 ops 成功；owner1 失敗未計入 Sent
        Assert.Equal(2, _sender.Attempts.Count);  // 但兩位都嘗試過寄送

        // 這筆高風險主機日尚未被標記已寄（owner1 那封失敗）——下次執行兩位都會重新收到，
        // ops 因此重複收到一次
        _sender.ThrowOnSendForRecipient = null;
        await service.NotifyAfterRunAsync();
        Assert.Equal(3, _sender.Sent.Count);
        Assert.Equal(2, _sender.Sent.Count(s => s.Message.To[0] == "ops@test.local"));
        Assert.Single(_sender.Sent, s => s.Message.To[0] == "owner1@test.local");
    }

    /// <summary>連續失敗達門檻即熔斷本輪剩餘寄送（回饋十六輪批次A-3），不把逾時全部付掉——
    /// 第 4 位收件人完全不會被嘗試寄送（用 Attempts 驗證嘗試次數，因為全部失敗時 Sent 恆為空）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_連續失敗達門檻時熔斷本輪剩餘寄送()
    {
        var owners = Enumerable.Range(1, 4)
            .Select(i => _users.Upsert(new WebUser { Account = $"owner{i}", Email = $"owner{i}@test.local", Active = true }))
            .ToList();
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string>();   // 只靠負責人收信，避免全域信干擾計數
            s.MailUrgentEnabled = true;
            s.MailNotifyHostOwners = true;
        });
        for (var i = 0; i < owners.Count; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"host{i}", OwnerUserIds = new List<long> { owners[i].UserId } });
            _records.Add(Record(host.HostId, $"host{i}", Yesterday, RiskLevels.High));
        }
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Create().NotifyAfterRunAsync();

        // 4 位收件人、連續失敗門檻 3：第 3 次失敗後熔斷，第 4 位完全不被嘗試寄送
        Assert.Equal(3, _sender.Attempts.Count);
    }

    /// <summary>回饋十七輪批次B-1：單一收件人跨輪連續失敗達門檻（3 次）即從寄送清單排除，
    /// 不再綁架全域收件人跟著同一個壞地址每輪重複收信。與「本輪熔斷」是不同機制——
    /// 這裡驗證的是跨輪累計，每輪都只有這一位收件人（不觸發熔斷），連續 3 輪失敗後第 4 輪
    /// 起完全不再嘗試寄送給它。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_單一收件人跨輪連續失敗達門檻後被永久排除()
    {
        var owner = _users.Upsert(new WebUser { Account = "badowner", Email = "bad@test.local", Active = true });
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string>();
            s.MailUrgentEnabled = true;
            s.MailNotifyHostOwners = true;
        });
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        var service = Create();

        // 三輪，每輪一台新主機（不同 HostId|Date key，避免被 UrgentSentKeys 去重擋住）
        for (var i = 0; i < 3; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"badhost{i}", OwnerUserIds = new List<long> { owner.UserId } });
            _records.Add(Record(host.HostId, $"badhost{i}", Yesterday, RiskLevels.High));
            await service.NotifyAfterRunAsync();
        }
        Assert.Equal(3, _sender.Attempts.Count);

        // 第四輪：地址已連續失敗 3 次，本輪完全不嘗試寄送給它
        var host4 = _hosts.Upsert(new WebHost { HostName = "badhost3", OwnerUserIds = new List<long> { owner.UserId } });
        _records.Add(Record(host4.HostId, "badhost3", Yesterday, RiskLevels.High));
        await service.NotifyAfterRunAsync();
        Assert.Equal(3, _sender.Attempts.Count); // 沒有第 4 次嘗試

        Assert.Contains("bad@test.local", service.GetSuspendedRecipients());
    }

    /// <summary>熔斷跳過的收件人不計入跨輪失敗 streak（回饋十七輪批次B-1 規劃明列的測試）：
    /// 熔斷是「本輪 SMTP 整體異常、沒嘗試寄」，不是這個收件人地址本身的問題——被跳過的
    /// 收件人不該累積失敗次數，否則 SMTP 掛一輪就會讓排在熔斷點之後的所有人一起被記黑點。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_熔斷跳過的收件人不累積跨輪失敗計數()
    {
        EnableMail(s =>
        {
            s.MailUrgentEnabled = true;
            s.MailRecipients = new List<string>
            {
                "a@test.local", "b@test.local", "c@test.local", "d@test.local", "e@test.local"
            };
        });
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        // 連續失敗 3 次即熔斷：只有 a/b/c 被實際嘗試，d/e 被跳過
        Assert.Equal(3, _sender.Attempts.Count);
        var streaks = new MailNotifyStateStore(_fx.Blob("mail_notify_state")).Get().RecipientFailureStreaks;
        Assert.Equal(1, streaks["a@test.local"]);
        Assert.Equal(1, streaks["b@test.local"]);
        Assert.Equal(1, streaks["c@test.local"]);
        Assert.False(streaks.ContainsKey("d@test.local")); // 熔斷跳過＝本輪沒嘗試，不計黑點
        Assert.False(streaks.ContainsKey("e@test.local"));
    }

    /// <summary>停用帳號視為未對應（回饋十七輪批次B-4 規劃明列的測試）：與 VisibilityService
    /// 一致，停用優先於一切授權路徑——即使該帳號的群組是 ViewAll，只要 Active=false 就只收
    /// 統計行，不含任何主機明細。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_停用帳號的收件人只收統計行不含主機明細()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser
        {
            Account = "ops", Email = "ops@test.local", Active = false, GroupIds = new List<long> { group.GroupId }
        });
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        await Create().NotifyAfterRunAsync();

        var sent = Assert.Single(_sender.Sent);
        Assert.DoesNotContain("host1", sent.Message.Body);
        Assert.Contains("站台", sent.Message.Body);
    }

    /// <summary>寄送成功會把該收件人的失敗 streak 歸零——不是永久性的懲罰，地址一旦恢復
    /// 正常就不再被排除（回饋十七輪批次B-1）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_收件人寄送成功後失敗streak歸零()
    {
        var owner = _users.Upsert(new WebUser { Account = "flaky", Email = "flaky@test.local", Active = true });
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string>();
            s.MailUrgentEnabled = true;
            s.MailNotifyHostOwners = true;
        });
        var service = Create();

        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        for (var i = 0; i < 2; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"flakyhost{i}", OwnerUserIds = new List<long> { owner.UserId } });
            _records.Add(Record(host.HostId, $"flakyhost{i}", Yesterday, RiskLevels.High));
            await service.NotifyAfterRunAsync();
        }

        // 第三輪恢復正常：成功後 streak 歸零
        _sender.ThrowOnSend = null;
        var host3 = _hosts.Upsert(new WebHost { HostName = "flakyhost2", OwnerUserIds = new List<long> { owner.UserId } });
        _records.Add(Record(host3.HostId, "flakyhost2", Yesterday, RiskLevels.High));
        await service.NotifyAfterRunAsync();

        Assert.DoesNotContain("flaky@test.local", service.GetSuspendedRecipients());
    }

    /// <summary>設定頁儲存郵件設定時整份清空失敗 streak（回饋十七輪批次B-1）：
    /// 地址已改正，舊的失敗歷史不該繼續卡著它。</summary>
    [Fact]
    public void ResetRecipientFailureStreaks_清空所有收件人的失敗計數()
    {
        var service = Create();
        var state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        state.Update(s => s.RecipientFailureStreaks["bad@test.local"] = 5);

        service.ResetRecipientFailureStreaks();

        Assert.Empty(service.GetSuspendedRecipients());
    }

    /// <summary>外層取消（服務停止／執行被取消）時未寄成的批次不標記已寄，
    /// 下次（沒有取消）要能正常補寄。取消要重新拋出並被 NotifyAfterRunAsync 的外層
    /// try/catch 吞掉，不能讓通知路徑弄掛呼叫端（回饋十六輪體檢發現2a）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_外層取消時不標記已寄且下次可補寄()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));
        using var cts = new CancellationTokenSource();
        _sender.ThrowOnSend = new OperationCanceledException(cts.Token);
        cts.Cancel();

        await Create().NotifyAfterRunAsync(cts.Token);
        Assert.Empty(_sender.Sent);

        _sender.ThrowOnSend = null;
        await Create().NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
    }

    /// <summary>取消中斷寄送迴圈時，中斷前已「對所有收件人寄成功」的 record 仍要落地標記
    /// （標記在 finally），只有沒寄完的 record 下次補寄——服務停止的那一刻不該把已送達的
    /// 通知也變成下次的重複信（回饋十六輪體檢：原實作取消時跳過整段標記）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_取消中斷前已寄成的record仍標記_只補寄未完成的()
    {
        // 兩台主機各自一位負責人、無全域收件人：owner1（record1）先寄且成功，
        // owner2（record2）寄送時模擬取消——record1 已被其唯一收件人收到，應標記
        var owner1 = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var owner2 = _users.Upsert(new WebUser { Account = "owner2", Email = "owner2@test.local", Active = true });
        var host1 = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner1.UserId } });
        var host2 = _hosts.Upsert(new WebHost { HostName = "host2", OwnerUserIds = new List<long> { owner2.UserId } });
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string>();
            s.MailUrgentEnabled = true;
            s.MailNotifyHostOwners = true;
        });
        _records.Add(Record(host1.HostId, "host1", Yesterday, RiskLevels.High));
        _records.Add(Record(host2.HostId, "host2", Yesterday, RiskLevels.High));

        // 服務「寄送中途」被停止：owner1 照常寄成，寄到 owner2 那一刻才取消——
        // 取消不能發生在一開始（那樣連 gate 都不會過，正確地整批不寄），要發生在迴圈中途
        using var cts = new CancellationTokenSource();
        _sender.OnSend = message =>
        {
            if (message.To.Contains("owner2@test.local")) cts.Cancel();
        };
        _sender.ThrowOnSendForRecipient = "owner2@test.local";
        _sender.ThrowOnSendForRecipientError = new OperationCanceledException(cts.Token);

        var service = Create();
        await service.NotifyAfterRunAsync(cts.Token);
        Assert.Single(_sender.Sent);   // 只有 owner1 寄成

        // 下一次執行：record1 已標記不重寄，只補寄 owner2 的 record2
        _sender.OnSend = null;
        _sender.ThrowOnSendForRecipient = null;
        _sender.ThrowOnSendForRecipientError = null;
        await service.NotifyAfterRunAsync();
        Assert.Equal(2, _sender.Sent.Count);
        Assert.DoesNotContain(_sender.Sent.Skip(1), s => s.Message.To.Contains("owner1@test.local"));
        Assert.Contains(_sender.Sent.Skip(1), s => s.Message.To.Contains("owner2@test.local"));
    }

    /// <summary>回饋十五輪體檢批G：settings store 讀取本身拋例外（模擬 blob 讀取失敗／並發衝突）
    /// 時不能穿透——這裡曾經是真的漏洞：Get() 呼叫原本在 try 區塊外，例外會一路傳到
    /// SchedulerHostedService，讓已成功的分析執行被誤判為失敗。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_設定讀取本身拋例外時不拋出()
    {
        _settingsStore.ThrowOnGet = new InvalidOperationException("blob 讀取失敗");

        await Create().NotifyAfterRunAsync();
    }

    [Fact]
    public async Task NotifyAfterRunAsync_寄送失敗不拋出例外()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => s.MailOnRunCompleted = true);
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        _records.Add(Record(1, "host1", Yesterday, RiskLevels.High));

        // SendSafeAsync 內部已 try/catch 到底——這裡不拋例外就是通過，呼叫端（排程輪詢／
        // 執行收尾）不該因為一封信寄失敗而受影響
        await Create().NotifyAfterRunAsync();
    }

    // ── CheckAndSendDailyWeeklyAsync：每日／每週定時彙總 ────────────────────

    /// <summary>回饋十五輪體檢批G：CheckAndSendDailyWeeklyAsync 由 SchedulerHostedService.TickAsync
    /// 在排程窗口判斷「之前」呼叫、且呼叫端沒有自己的 try/catch——settings 讀取拋例外若沒被這裡
    /// 擋下，會連帶讓同一輪詢的排程窗口判斷整段被跳過，等於通知路徑間接卡住排程觸發。</summary>
    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_設定讀取本身拋例外時不拋出()
    {
        _settingsStore.ThrowOnGet = new InvalidOperationException("blob 讀取失敗");

        await Create().CheckAndSendDailyWeeklyAsync(DateTime.Today);
    }

    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_MailEnabled為false時不寄送()
    {
        _settingsStore.Update(s =>
        {
            s.MailEnabled = false;
            s.MailDailyEnabled = true;
            s.MailDailyTime = "00:00";
            s.MailRecipients = new List<string> { "ops@test.local" };
        });

        await Create().CheckAndSendDailyWeeklyAsync(DateTime.Today.AddHours(23));

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_到達時刻才寄且同一天只寄一次()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailDailyEnabled = true; s.MailDailyTime = "08:00"; });
        var before = new DateTime(2026, 8, 11, 7, 59, 0);
        var atTime = new DateTime(2026, 8, 11, 8, 0, 0);
        var service = Create();

        await service.CheckAndSendDailyWeeklyAsync(before);
        Assert.Empty(_sender.Sent);

        await service.CheckAndSendDailyWeeklyAsync(atTime);
        Assert.Single(_sender.Sent);

        // 輪詢每分鐘跑一次，同一天內再次命中時刻窗口不該重複寄
        await service.CheckAndSendDailyWeeklyAsync(atTime.AddMinutes(1));
        Assert.Single(_sender.Sent);
    }

    /// <summary>每日摘要窗口右移一天（回饋十七輪批次A-3）：分析永遠只產出到昨天，
    /// 「今天」的紀錄（理論上不存在的狀態）不該落在每日摘要窗口內。</summary>
    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_每日摘要窗口是昨天不含今天()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailDailyEnabled = true; s.MailDailyTime = "08:00"; s.MailMinRiskLevel = RiskLevels.Medium; });
        var now = new DateTime(2026, 8, 11, 9, 0, 0);
        var hostYesterday = _hosts.Upsert(new WebHost { HostName = "host-yesterday" });
        var hostToday = _hosts.Upsert(new WebHost { HostName = "host-today" });
        _records.Add(Record(hostYesterday.HostId, "host-yesterday", now.Date.AddDays(-1), RiskLevels.High));
        _records.Add(Record(hostToday.HostId, "host-today", now.Date, RiskLevels.High));

        await Create().CheckAndSendDailyWeeklyAsync(now);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host-yesterday", sent.Message.Body);
        Assert.DoesNotContain("host-today", sent.Message.Body);
    }

    /// <summary>回饋十七輪批次A-4：「無事時不寄」開關，預設 false（照寄）——期間內無事同時是
    /// 系統存活訊號。開啟後期間內無達門檻風險日時完全不寄。</summary>
    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_無事時預設仍寄()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailDailyEnabled = true; s.MailDailyTime = "08:00"; });

        await Create().CheckAndSendDailyWeeklyAsync(new DateTime(2026, 8, 11, 9, 0, 0));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("無達門檻", sent.Message.Body);
    }

    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_開啟無事不寄後期間內無事完全不寄()
    {
        CreateViewAllAccount("ops@test.local");
        EnableMail(s => { s.MailDailyEnabled = true; s.MailDailyTime = "08:00"; s.MailDigestSkipEmpty = true; });

        await Create().CheckAndSendDailyWeeklyAsync(new DateTime(2026, 8, 11, 9, 0, 0));

        Assert.Empty(_sender.Sent);
    }

    /// <summary>週報彙總範圍（docs/archive/FEEDBACK-15-PLAN.md D-3）：過去 7 個完整日（窗口右移一天，
    /// 昨天往回 7 天）達門檻的主機日逐主機彙總＋未處理數；窗口外的紀錄不計入。</summary>
    [Fact]
    public async Task 週報彙總過去七日達門檻的主機日並附未處理數()
    {
        CreateViewAllAccount("ops@test.local");
        var now = new DateTime(2026, 8, 10, 9, 0, 0);
        EnableMail(s =>
        {
            s.MailWeeklyEnabled = true;
            s.MailWeeklyDayOfWeek = now.DayOfWeek.ToString();
            s.MailWeeklyTime = "08:00";
            s.MailMinRiskLevel = RiskLevels.Medium;   // 門檻放到中，高＋中兩筆都要計入
        });
        var to = now.Date.AddDays(-1);
        var host1 = _hosts.Upsert(new WebHost { HostName = "host1" });
        _records.Add(Record(host1.HostId, "host1", to.AddDays(-1), RiskLevels.High));
        _records.Add(Record(host1.HostId, "host1", to, RiskLevels.Medium));
        _records.Add(Record(host1.HostId, "host1", to.AddDays(-9), RiskLevels.High));   // 窗口外，不計入
        _handlings.Save(new RecordHandling { HostName = "host1", Date = to.AddDays(-1), Status = HandlingStatuses.Open });

        await Create().CheckAndSendDailyWeeklyAsync(now);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1：高風險 1 天、中風險 1 天", sent.Message.Body);
        Assert.Contains("未處理（含處理中）的風險日共 1 筆", sent.Message.Body);
    }

    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_只在指定星期寄週報()
    {
        CreateViewAllAccount("ops@test.local");
        var targetDay = new DateTime(2026, 8, 10, 9, 0, 0);
        var otherDay = targetDay.AddDays(1);
        EnableMail(s =>
        {
            s.MailWeeklyEnabled = true;
            s.MailWeeklyDayOfWeek = targetDay.DayOfWeek.ToString();
            s.MailWeeklyTime = "08:00";
        });
        var service = Create();

        await service.CheckAndSendDailyWeeklyAsync(otherDay);
        Assert.Empty(_sender.Sent);

        await service.CheckAndSendDailyWeeklyAsync(targetDay);
        Assert.Single(_sender.Sent);
    }

    // ── SendTestAsync：測試寄信（設定頁「測試寄信」鈕）────────────────────

    [Fact]
    public async Task SendTestAsync_套用標題模板變數()
    {
        var connection = new SmtpConnectionSpec("smtp.test.local", 25, false, "", null);

        await Create().SendTestAsync(connection, "logforesight@test.local", new List<string> { "ops@test.local" },
            "[{site}] {type}：{date} {summary}", "自訂開頭文字");

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal($"[LogForesight] 測試：{DateTime.Today:yyyy-MM-dd} 這是一封測試郵件", sent.Message.Subject);
        Assert.Contains("自訂開頭文字", sent.Message.Body);
        Assert.Equal("logforesight@test.local", sent.Message.From);
        Assert.Equal(new[] { "ops@test.local" }, sent.Message.To);
        Assert.Same(connection, sent.Connection);
    }

    [Fact]
    public async Task SendTestAsync_寄送失敗會往外拋例外()
    {
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        var connection = new SmtpConnectionSpec("smtp.test.local", 25, false, "", null);

        // 與 NotifyAfterRunAsync／CheckAndSendDailyWeeklyAsync 不同——這裡是管理者主動點「測試寄信」，
        // 失敗要能看到，不能被吞掉（見 SystemSettingsService.TestMail 的 catch 處理）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().SendTestAsync(connection, "logforesight@test.local", new List<string> { "ops@test.local" },
                "[{site}] {type}：{date} {summary}", ""));
    }

    // ── NotifyEscalationAsync：問題上報通知（回饋十八輪批次G）─────────────────

    private static EscalationNotice Notice(string? reason = "換硬體超出我的權限") =>
        new("disk 7", "host1", "user1", reason);

    [Fact]
    public async Task NotifyEscalationAsync_寄給全部admin群組成員()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "a1", Email = "a1@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        _users.Upsert(new WebUser { Account = "a2", Email = "a2@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        EnableMail();

        await Create().NotifyEscalationAsync(Notice());

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "a1@test.local", "a2@test.local" }, sent.Message.To.OrderBy(e => e));
        Assert.Contains("disk 7", sent.Message.Body);
        Assert.Contains("無法處理", sent.Message.Body);
        Assert.Contains("換硬體超出我的權限", sent.Message.Body);
        Assert.Contains("user1", sent.Message.Body);
    }

    /// <summary>admin 判定必須用 UserGroup.Role，不能用群組名稱——群組改名後通知不能斷。</summary>
    [Fact]
    public async Task NotifyEscalationAsync_admin群組改名後仍寄()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "系統管理組（已改名）", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "a1", Email = "a1@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        EnableMail();

        await Create().NotifyEscalationAsync(Notice());

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task NotifyEscalationAsync_MailEnabled為false時不寄()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "a1", Email = "a1@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });

        await Create().NotifyEscalationAsync(Notice());

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task NotifyEscalationAsync_admin成員皆無email時不寄不拋()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "a1", Email = "", Active = true, GroupIds = new List<long> { group.GroupId } });
        EnableMail();

        await Create().NotifyEscalationAsync(Notice());

        Assert.Empty(_sender.Sent);
    }

    /// <summary>停用的 admin 帳號與停用的 admin 群組成員都不收信（與 HasNoAdmins 同一份判定）。</summary>
    [Fact]
    public async Task NotifyEscalationAsync_停用帳號與停用群組不收信()
    {
        var activeGroup = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        var inactiveGroup = _userGroups.Upsert(new UserGroup { GroupName = "old-admins", Role = UserRole.Admin, Active = false });
        _users.Upsert(new WebUser { Account = "ok", Email = "ok@test.local", Active = true, GroupIds = new List<long> { activeGroup.GroupId } });
        _users.Upsert(new WebUser { Account = "off", Email = "off@test.local", Active = false, GroupIds = new List<long> { activeGroup.GroupId } });
        _users.Upsert(new WebUser { Account = "gone", Email = "gone@test.local", Active = true, GroupIds = new List<long> { inactiveGroup.GroupId } });
        EnableMail();

        await Create().NotifyEscalationAsync(Notice());

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "ok@test.local" }, sent.Message.To);
    }

    /// <summary>寄送失敗不往外拋（fire-and-forget 的前提：狀態變更不能被通知失敗弄掛）。</summary>
    [Fact]
    public async Task NotifyEscalationAsync_寄送失敗不拋例外()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "a1", Email = "a1@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        EnableMail();
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Create().NotifyEscalationAsync(Notice());

        Assert.Empty(_sender.Sent);
    }
}

/// <summary>MissingDateFinder 測試用最小 IAnalysisRecordReader：全空歷史，模擬「近 N 天都沒有
/// 分析紀錄」的狀態，驗證 offset 從 1 起算、今天永遠不在缺漏日清單裡。</summary>
internal class FakeAnalysisRecordReader : IAnalysisRecordReader
{
    public List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days) => new();
    public bool HasAnyRecord() => false;
    public bool HasRecord(DateTime date) => false;
    public DateTime? LastWeeklyCheckupDate() => null;
}
