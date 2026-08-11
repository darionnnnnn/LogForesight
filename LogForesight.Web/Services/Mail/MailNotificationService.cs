using System.Text;
using NLog;

namespace LogForesight.Web.Services.Mail;

/// <summary>
/// 郵件通知的組信與寄送（回饋十五輪批次D）。三路觸發共用：
/// 1. 排程/手動觸發執行結束後（<see cref="NotifyAfterRunAsync"/>）——摘要 + 緊急即時判定同一個掛載點。
/// 2. 每日／每週定時彙總（<see cref="CheckAndSendDailyWeeklyAsync"/>）。
/// 3. 高風險日即時通知內建在 <see cref="NotifyAfterRunAsync"/> 裡（見其文件說明「即時」的定義）。
///
/// **通知永遠不能弄掛分析**：所有對外方法內部自行 try/catch 到底，失敗只記 NLog WARN，
/// 不對外拋例外——呼叫端（SchedulerHostedService／排程輪詢）不需要、也不應該因為一封信寄失敗
/// 而讓整個執行流程報錯。
/// </summary>
public class MailNotificationService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ISystemSettingsStore _settingsStore;
    private readonly ISmtpMailSender _sender;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IAnalysisRecordQuery _records;
    private readonly MailNotifyStateStore _state;

    public MailNotificationService(
        ISystemSettingsStore settingsStore,
        ISmtpMailSender sender,
        IHostStore hosts,
        IUserStore users,
        IAnalysisRecordQuery records,
        MailNotifyStateStore state)
    {
        _settingsStore = settingsStore;
        _sender = sender;
        _hosts = hosts;
        _users = users;
        _records = records;
        _state = state;
    }

    // ── 對外三路觸發 ──────────────────────────────────────────────────────

    /// <summary>
    /// 觸發來源執行結束後呼叫（<c>SchedulerHostedService.TriggerRunAsync</c> 的收尾處，
    /// 排程與手動觸發共用同一個掛載點）。兩件事在此一起判定：
    ///
    /// 1. <see cref="SystemSettings.MailOnRunCompleted"/>：本次「今天」產生的 High/Medium
    ///    （依 <see cref="SystemSettings.MailMinRiskLevel"/>）紀錄彙整成一封摘要信。
    /// 2. <see cref="SystemSettings.MailUrgentEnabled"/>：「今天」新出現的 High 風險紀錄，
    ///    每個主機日只寄一次（<see cref="MailNotifyState.UrgentSentKeys"/> 去重），不受每日/每週
    ///    時刻限制——這就是「即時」的落地方式：不是在分析管線內部插入通知呼叫（Core 分析管線
    ///    不該依賴 Web 層的 SMTP 設定），而是緊接著「這一批執行剛完成」判定，時效上等同即時，
    ///    且不需要改動任何已充分測試的 Core 分析程式碼。
    ///
    /// <paramref name="targetDate"/>：簡化假設——批次通常分析「今天」（daily run），呼叫端直接
    /// 傳 <c>DateTime.Today</c> 即可；未來若排程改成回補多天，這裡的日期範圍可以再擴充。
    /// </summary>
    public async Task NotifyAfterRunAsync(DateTime targetDate, CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        if (!settings.MailEnabled) return;

        try
        {
            var records = QueryDate(targetDate);

            if (settings.MailOnRunCompleted)
            {
                await SendRunSummaryAsync(settings, targetDate, records, ct);
            }

            if (settings.MailUrgentEnabled)
            {
                await SendUrgentNotificationsAsync(settings, records, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 執行結束後的通知處理失敗（不影響分析結果本身）");
        }
    }

    /// <summary>
    /// 每日／每週定時彙總——由 <c>SchedulerHostedService</c> 的既有 60 秒輪詢一併呼叫。
    /// 用「上次寄送日」防同一天重複觸發（輪詢每分鐘跑一次，時刻比對命中的那一分鐘內可能被
    /// 呼叫多次）。時刻比對用「現在 &gt;= 設定時刻」而非精準相等——輪詢間隔與伺服器負載都可能
    /// 讓精準比對錯過那一分鐘，>= 加上「今天還沒寄過」的狀態防重複，兩者合起來才是可靠的每日觸發。
    /// </summary>
    public async Task CheckAndSendDailyWeeklyAsync(DateTime now, CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        if (!settings.MailEnabled) return;

        try
        {
            var today = now.ToString("yyyy-MM-dd");

            if (settings.MailDailyEnabled &&
                TimeSpan.TryParse(settings.MailDailyTime, out var dailyTime) &&
                now.TimeOfDay >= dailyTime &&
                _state.Get().LastDailySentDate != today)
            {
                await SendDigestAsync(settings, now, windowDays: 1, isWeekly: false, ct);
                _state.Update(s => s.LastDailySentDate = today);
            }

            if (settings.MailWeeklyEnabled &&
                Enum.TryParse<DayOfWeek>(settings.MailWeeklyDayOfWeek, ignoreCase: true, out var weeklyDay) &&
                now.DayOfWeek == weeklyDay &&
                TimeSpan.TryParse(settings.MailWeeklyTime, out var weeklyTime) &&
                now.TimeOfDay >= weeklyTime &&
                _state.Get().LastWeeklySentDate != today)
            {
                await SendDigestAsync(settings, now, windowDays: 7, isWeekly: true, ct);
                _state.Update(s => s.LastWeeklySentDate = today);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 每日／每週定時彙總處理失敗");
        }
    }

    /// <summary>測試寄信（設定頁「測試寄信」鈕）：用表單目前值（可能還沒儲存），不落地任何狀態。</summary>
    public async Task SendTestAsync(SmtpConnectionSpec connection, string from, List<string> recipients,
        string subjectTemplate, string bodyIntro, CancellationToken ct = default)
    {
        var subject = ExpandTemplate(subjectTemplate, "測試", DateTime.Today.ToString("yyyy-MM-dd"), "-", "測試", "這是一封測試郵件");
        var bodyBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bodyIntro)) bodyBuilder.AppendLine(bodyIntro).AppendLine();
        bodyBuilder.AppendLine("這是 LogForesight 的測試郵件，收到即代表 SMTP 設定正確可用。");
        await _sender.SendAsync(connection, new MailMessageSpec(from, recipients, subject, bodyBuilder.ToString()), ct);
    }

    // ── 內部：查詢與收件人解析 ────────────────────────────────────────────

    private List<DailyAnalysisRecord> QueryDate(DateTime date) =>
        _records.Query(new RecordQueryFilter { Hosts = null, From = date.Date, To = date.Date });

    /// <summary>全域收件人 + （選填）主機負責人 email，去重不分大小寫</summary>
    private List<string> ResolveRecipients(SystemSettings settings, WebHost? host)
    {
        var recipients = new List<string>(settings.MailRecipients);
        if (settings.MailNotifyHostOwners && host != null)
        {
            foreach (var ownerId in host.OwnerUserIds)
            {
                var email = _users.Get(ownerId)?.Email;
                if (!string.IsNullOrWhiteSpace(email)) recipients.Add(email);
            }
        }
        return recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private SmtpConnectionSpec ResolveConnection(SystemSettings settings) => new(
        settings.SmtpServer, settings.SmtpPort, settings.SmtpUseTls, settings.SmtpAccount,
        string.IsNullOrEmpty(settings.SmtpPasswordEnc) ? null : CryptoHelper.Decrypt(settings.SmtpPasswordEnc));

    private static string ExpandTemplate(string template, string host, string date, string risk, string type, string summary) =>
        template
            .Replace("{site}", WebBrandName)
            .Replace("{host}", host)
            .Replace("{date}", date)
            .Replace("{risk}", risk)
            .Replace("{type}", type)
            .Replace("{summary}", summary);

    /// <summary>品牌名稱走系統設定（外觀分頁），不是這個服務的職責範圍——郵件主旨的 {site} 直接
    /// 用固定產品名，品牌自訂的套用範圍留在畫面顯示層，避免這裡多依賴一份設定讀取</summary>
    private const string WebBrandName = "LogForesight";

    // ── 內部：組信 ────────────────────────────────────────────────────────

    private async Task SendRunSummaryAsync(SystemSettings settings, DateTime targetDate,
        List<DailyAnalysisRecord> records, CancellationToken ct)
    {
        var minRank = RiskLevels.Rank(settings.MailMinRiskLevel);
        var qualifying = records.Where(r => RiskLevels.Rank(r.RiskLevel) >= minRank).ToList();
        if (qualifying.Count == 0) return;

        var recipients = ResolveRecipients(settings, host: null);
        if (recipients.Count == 0) return;

        var dateText = targetDate.ToString("yyyy-MM-dd");
        var summary = $"{qualifying.Count} 台主機達 {settings.MailMinRiskLevel} 風險以上";
        var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, settings.MailMinRiskLevel, "執行摘要", summary);

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine($"{dateText} 執行摘要：{summary}").AppendLine();
        foreach (var record in qualifying.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel)))
        {
            var hostName = ResolveHostDisplayName(record);
            body.AppendLine($"  - [{record.RiskLevel}風險] {hostName}　錯誤 {record.ErrorCount}／警告 {record.WarningCount}" +
                             (string.IsNullOrWhiteSpace(record.Headline) ? "" : $"　{record.Headline}"));
        }

        await SendSafeAsync(settings, recipients, subject, body.ToString(), ct);
    }

    private async Task SendUrgentNotificationsAsync(SystemSettings settings, List<DailyAnalysisRecord> records, CancellationToken ct)
    {
        var highRisk = records.Where(r => r.RiskLevel == RiskLevels.High).ToList();
        if (highRisk.Count == 0) return;

        var state = _state.Get();
        var pending = highRisk.Where(r => !state.UrgentSentKeys.Contains(UrgentKey(r))).ToList();
        if (pending.Count == 0) return;

        foreach (var record in pending)
        {
            var host = _hosts.Get(record.HostId);
            var recipients = ResolveRecipients(settings, host);
            if (recipients.Count == 0) continue;

            var hostName = ResolveHostDisplayName(record);
            var dateText = record.Date.ToString("yyyy-MM-dd");
            var subject = ExpandTemplate(settings.MailSubjectTemplate, hostName, dateText, RiskLevels.High, "高風險即時通知",
                record.Headline?.Length > 0 ? record.Headline : "偵測到高風險訊號");

            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
            body.AppendLine($"主機 {hostName} 於 {dateText} 判定為高風險日。");
            if (!string.IsNullOrWhiteSpace(record.Headline)) body.AppendLine($"狀況：{record.Headline}");
            if (!string.IsNullOrWhiteSpace(record.RiskBasis)) body.AppendLine($"判定依據：{record.RiskBasis}");

            await SendSafeAsync(settings, recipients, subject, body.ToString(), ct);
        }

        // 標記已寄＋順便清掉超過保留期的舊 key，不無限增長（用 RetentionDays 當保留期，
        // 與業務資料同一個保留週期，沒有理由讓已到期資料的通知紀錄留得比資料本身久）
        var cutoff = DateTime.Today.AddDays(-settings.RetentionDays);
        _state.Update(s =>
        {
            foreach (var record in pending) s.UrgentSentKeys.Add(UrgentKey(record));
            s.UrgentSentKeys.RemoveWhere(key => IsBeforeCutoff(key, cutoff));
        });
    }

    private async Task SendDigestAsync(SystemSettings settings, DateTime now, int windowDays, bool isWeekly, CancellationToken ct)
    {
        var recipients = ResolveRecipients(settings, host: null);
        if (recipients.Count == 0) return;

        var from = now.Date.AddDays(-(windowDays - 1));
        var records = _records.Query(new RecordQueryFilter { Hosts = null, From = from, To = now.Date });
        var minRank = RiskLevels.Rank(settings.MailMinRiskLevel);
        var qualifying = records.Where(r => RiskLevels.Rank(r.RiskLevel) >= minRank).ToList();

        var type = isWeekly ? "週報" : "每日摘要";
        var dateText = now.ToString("yyyy-MM-dd");
        var summary = qualifying.Count == 0 ? "期間內無達門檻的風險日" : $"{qualifying.Count} 個主機日達 {settings.MailMinRiskLevel} 風險以上";
        var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, settings.MailMinRiskLevel, type, summary);

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine($"{type}（{from:yyyy-MM-dd} ~ {now:yyyy-MM-dd}）：{summary}").AppendLine();

        foreach (var group in qualifying.GroupBy(r => r.HostId))
        {
            var hostName = ResolveHostDisplayName(group.First());
            var highCount = group.Count(r => r.RiskLevel == RiskLevels.High);
            var mediumCount = group.Count(r => r.RiskLevel == RiskLevels.Medium);
            body.AppendLine($"  - {hostName}：高風險 {highCount} 天、中風險 {mediumCount} 天");
        }

        await SendSafeAsync(settings, recipients, subject, body.ToString(), ct);
    }

    // ── 內部：共用工具 ────────────────────────────────────────────────────

    private string ResolveHostDisplayName(DailyAnalysisRecord record) =>
        _hosts.Get(record.HostId)?.DisplayName is { Length: > 0 } display ? $"{record.Host}（{display}）" : record.Host;

    private static string UrgentKey(DailyAnalysisRecord record) => $"{record.HostId}|{record.Date:yyyy-MM-dd}";

    private static bool IsBeforeCutoff(string key, DateTime cutoff)
    {
        var parts = key.Split('|');
        return parts.Length == 2 && DateTime.TryParse(parts[1], out var date) && date < cutoff;
    }

    /// <summary>
    /// 寄送失敗只記 WARN，不往外拋——這是三路觸發共用的最後一道防線，呼叫端（排程輪詢／
    /// 執行收尾）不該因為一封信寄失敗而受影響。SmtpClient 逾時無獨立設定，共用 30 秒合理上限，
    /// relay 通常是內網，30 秒逾時仍未回應代表連線本身有問題，繼續等沒有意義。
    /// </summary>
    private async Task SendSafeAsync(SystemSettings settings, List<string> recipients, string subject, string body, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await _sender.SendAsync(ResolveConnection(settings),
                new MailMessageSpec(settings.MailFrom, recipients, subject, body), timeoutCts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 寄送失敗：{Subject}（收件人 {Count} 位）", subject, recipients.Count);
        }
    }
}
