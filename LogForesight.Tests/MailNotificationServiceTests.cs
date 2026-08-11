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
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _records, new MailNotifyStateStore(_fx.Blob("mail_notify_state")));

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

    [Fact]
    public async Task NotifyAfterRunAsync_同時通知主機負責人時合併負責人信箱()
    {
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "owner1@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("ops@test.local", sent.Message.To);
        Assert.Contains("owner1@test.local", sent.Message.To);
    }

    [Fact]
    public async Task NotifyAfterRunAsync_收件人不分大小寫去重()
    {
        // 全域收件人（EnableMail 預設）已含 "ops@test.local"，負責人信箱大小寫不同但視為同一人
        var owner = _users.Upsert(new WebUser { Account = "owner1", Email = "OPS@test.local", Active = true });
        var host = _hosts.Upsert(new WebHost { HostName = "host1", OwnerUserIds = new List<long> { owner.UserId } });
        EnableMail(s => { s.MailUrgentEnabled = true; s.MailNotifyHostOwners = true; });
        _records.Add(Record(host.HostId, "host1", DateTime.Today, RiskLevels.High));

        await Create().NotifyAfterRunAsync(DateTime.Today);

        var sent = Assert.Single(_sender.Sent);
        Assert.Single(sent.Message.To);
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
