using LogForesight.Web.Models;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 郵件通知（docs/archive/FEEDBACK-15-PLAN.md 批次D）：三路觸發（執行結束後摘要／高風險即時／
/// 每日每週定時彙總）全部打在 fake <see cref="ISmtpMailSender"/> 上，不碰真實 SMTP——
/// 斷言收件人解析（全域＋負責人去重）、標題模板展開、門檻過濾、緊急通知去重。
/// </summary>
public class MailNotificationServiceTests : IDisposable
{
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _records, _handlings,
            new MailNotifyStateStore(_fx.Blob("mail_notify_state")));

    private static DailyAnalysisRecord Record(long hostId, string host, DateTime date, string riskLevel,
        string headline = "", string? riskBasis = null) => new()
    {
        HostId = hostId,
        Host = host,
        Date = date,
        RiskLevel = riskLevel,
        Headline = headline,
        RiskBasis = riskBasis ?? ""
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

    // ── NotifyAfterRunAsync：執行摘要 + 高風險即時通知 ──────────────────────

    [Fact]
    public async Task NotifyAfterRunAsync_MailEnabled為false時不寄送任何信()
    {
        _settingsStore.Update(s => { s.MailEnabled = false; s.MailOnRunCompleted = true; s.MailUrgentEnabled = true; });
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_執行摘要只納入達門檻的紀錄()
    {
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailMinRiskLevel = RiskLevels.Medium; });
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High, "host1標頭"));
        _records.Add(Record(2, "host2", DateTime.Today, RiskLevels.Low, "host2標頭"));

        await Create().NotifyAfterRunAsync(DateTime.Today);

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
        EnableMail(s => { s.MailOnRunCompleted = true; s.MailMinRiskLevel = RiskLevels.Medium; });
        for (var i = 0; i < 55; i++)
        {
            _records.Add(Record(i, $"host{i}", DateTime.Today, RiskLevels.High));
        }

        await Create().NotifyAfterRunAsync(DateTime.Today);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host0", sent.Message.Body);
        Assert.Contains("host49", sent.Message.Body);
        Assert.DoesNotContain("host50", sent.Message.Body);
        Assert.Contains("其餘 5 台請至站台", sent.Message.Body);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_無達門檻紀錄時不寄摘要信()
    {
        EnableMail(s => s.MailOnRunCompleted = true);
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.Low));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_高風險即時通知同一主機日只寄一次()
    {
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High, "host1標頭"));
        var service = Create();

        await service.NotifyAfterRunAsync(DateTime.Today);
        Assert.Single(_sender.Sent);

        // 模擬同一天又觸發一次執行（排程輪詢或手動再次觸發）：不應重複寄送
        await service.NotifyAfterRunAsync(DateTime.Today);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_中風險不觸發高風險即時通知()
    {
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.Medium));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        Assert.Empty(_sender.Sent);
    }

    /// <summary>回饋十六輪批次A-1：高風險即時通知改按收件人聚合——全域收件人與非全域的
    /// 主機負責人各自收到「一封」信，不再合併成單封多收件人的信（不同人該看到的內容不同：
    /// 全域收件人看全部，負責人只看自己的主機）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_全域收件人與負責人分別各收一封()
    {
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains(_sender.Sent, s => s.Message.To.Count == 1 && s.Message.To[0] == "ops@test.local");
        Assert.Contains(_sender.Sent, s => s.Message.To.Count == 1 && s.Message.To[0] == "owner1@test.local");
    }

    /// <summary>負責人信箱與全域收件人相同（不分大小寫）時，不重複收信——他已經在全域信
    /// 裡看到全部內容，不需要再收一封只列自己主機的信。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_負責人與全域收件人相同時不重複收信()
    {
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "OPS@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        var sent = Assert.Single(_sender.Sent);
        Assert.Single(sent.Message.To);
    }

    /// <summary>全域收件人收到的信涵蓋「本次全部」pending 主機，不只是自己負責的那些。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_全域收件人的信涵蓋全部高風險主機()
    {
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High, "host1事故"));
        _records.Add(Record(2, "host2", DateTime.Today, RiskLevels.High, "host2事故"));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1", sent.Message.Body);
        Assert.Contains("host2", sent.Message.Body);
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
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High));
        var service = Create();

        await service.NotifyAfterRunAsync(DateTime.Today);
        Assert.Empty(_sender.Sent);

        // 補上收件人設定後，同一天的高風險主機日要能自動補寄
        _settingsStore.Update(s => s.MailRecipients = new List<string> { "ops@test.local" });
        await service.NotifyAfterRunAsync(DateTime.Today);
        Assert.Single(_sender.Sent);
    }

    /// <summary>標記語意是「涵蓋此 record 的信全部寄成功才標記」（回饋十六輪體檢發現2a 的根修：
    /// 原本吞掉例外、無條件全部標記已寄）——只要有一位涵蓋到這個 record 的收件人失敗，這個
    /// record 就整批留到下次重寄，即使其他收件人已經成功過也會再收到一次重複信。這是刻意的
    /// 保守取捨：寧可讓已收到的人重複收到，也不讓任何一位該收到的人漏收。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_有收件人失敗時該record下次對所有收件人重寄()
    {
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", DateTime.Today, RiskLevels.High));

        _sender.ThrowOnSendForRecipient = "owner1@test.local";
        var service = Create();
        await service.NotifyAfterRunAsync(DateTime.Today);
        Assert.Single(_sender.Sent);              // 只有全域 ops 成功；owner1 失敗未計入 Sent
        Assert.Equal(2, _sender.Attempts.Count);  // 但兩位都嘗試過寄送

        // 這筆高風險主機日尚未被標記已寄（owner1 那封失敗）——下次執行兩位都會重新收到，
        // ops 因此重複收到一次
        _sender.ThrowOnSendForRecipient = null;
        await service.NotifyAfterRunAsync(DateTime.Today);
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
            _records.Add(Record(host.HostId, $"host{i}", DateTime.Today, RiskLevels.High));
        }
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Create().NotifyAfterRunAsync(DateTime.Today);

        // 4 位收件人、連續失敗門檻 3：第 3 次失敗後熔斷，第 4 位完全不被嘗試寄送
        Assert.Equal(3, _sender.Attempts.Count);
    }

    /// <summary>外層取消（服務停止／執行被取消）時未寄成的批次不標記已寄，
    /// 下次（沒有取消）要能正常補寄。取消要重新拋出並被 NotifyAfterRunAsync 的外層
    /// try/catch 吞掉，不能讓通知路徑弄掛呼叫端（回饋十六輪體檢發現2a）。</summary>
    [Fact]
    public async Task NotifyAfterRunAsync_外層取消時不標記已寄且下次可補寄()
    {
        EnableMail(s => s.MailUrgentEnabled = true);
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High));
        using var cts = new CancellationTokenSource();
        _sender.ThrowOnSend = new OperationCanceledException(cts.Token);
        cts.Cancel();

        await Create().NotifyAfterRunAsync(DateTime.Today, cts.Token);
        Assert.Empty(_sender.Sent);

        _sender.ThrowOnSend = null;
        await Create().NotifyAfterRunAsync(DateTime.Today);
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
        _records.Add(Record(host1.HostId, "host1", DateTime.Today, RiskLevels.High));
        _records.Add(Record(host2.HostId, "host2", DateTime.Today, RiskLevels.High));

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
        await service.NotifyAfterRunAsync(DateTime.Today, cts.Token);
        Assert.Single(_sender.Sent);   // 只有 owner1 寄成

        // 下一次執行：record1 已標記不重寄，只補寄 owner2 的 record2
        _sender.OnSend = null;
        _sender.ThrowOnSendForRecipient = null;
        _sender.ThrowOnSendForRecipientError = null;
        await service.NotifyAfterRunAsync(DateTime.Today);
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

        await Create().NotifyAfterRunAsync(DateTime.Today);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_寄送失敗不拋出例外()
    {
        EnableMail(s => s.MailOnRunCompleted = true);
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        _records.Add(Record(1, "host1", DateTime.Today, RiskLevels.High));

        // SendSafeAsync 內部已 try/catch 到底——這裡不拋例外就是通過，呼叫端（排程輪詢／
        // 執行收尾）不該因為一封信寄失敗而受影響
        await Create().NotifyAfterRunAsync(DateTime.Today);
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

    /// <summary>週報彙總範圍（docs/FEEDBACK-15-PLAN.md D-3）：過去 7 日達門檻的主機日
    /// 逐主機彙總＋未處理數；窗口外的紀錄不計入。</summary>
    [Fact]
    public async Task 週報彙總過去七日達門檻的主機日並附未處理數()
    {
        var now = new DateTime(2026, 8, 10, 9, 0, 0);
        EnableMail(s =>
        {
            s.MailWeeklyEnabled = true;
            s.MailWeeklyDayOfWeek = now.DayOfWeek.ToString();
            s.MailWeeklyTime = "08:00";
            s.MailMinRiskLevel = RiskLevels.Medium;   // 門檻放到中，高＋中兩筆都要計入
        });
        _records.Add(Record(1, "host1", now.Date.AddDays(-2), RiskLevels.High));
        _records.Add(Record(1, "host1", now.Date.AddDays(-1), RiskLevels.Medium));
        _records.Add(Record(1, "host1", now.Date.AddDays(-10), RiskLevels.High));   // 窗口外，不計入
        _handlings.Save(new RecordHandling { HostName = "host1", Date = now.Date.AddDays(-2), Status = HandlingStatuses.Open });

        await Create().CheckAndSendDailyWeeklyAsync(now);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("host1：高風險 1 天、中風險 1 天", sent.Message.Body);
        Assert.Contains("未處理（含處理中）的風險日共 1 筆", sent.Message.Body);
    }

    [Fact]
    public async Task CheckAndSendDailyWeeklyAsync_只在指定星期寄週報()
    {
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
}
