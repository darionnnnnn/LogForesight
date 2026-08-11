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

    /// <summary>執行摘要／每日週報信，明細行數上限（回饋十六輪批次A-5）：2000 台規模下
    /// 中風險以上可能有 600+ 筆，純文字信不該列到那個長度，超出改導向站台。</summary>
    private const int SummaryBodyLineLimit = 50;

    /// <summary>高風險即時通知單封信的主機明細行數上限（回饋十六輪批次A-1）。</summary>
    private const int UrgentBodyLineLimit = 20;

    /// <summary>高風險即時通知同一輪寄送的連續失敗次數上限，達到即熔斷剩餘寄送
    /// （回饋十六輪批次A-3）：SMTP 整台不通時，不把「30 秒逾時 × 剩餘收件人數」全部付掉。</summary>
    private const int UrgentFailureThreshold = 3;

    private readonly ISystemSettingsStore _settingsStore;
    private readonly ISmtpMailSender _sender;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IAnalysisRecordQuery _records;
    private readonly IRecordHandlingStore _handlings;
    private readonly MailNotifyStateStore _state;

    public MailNotificationService(
        ISystemSettingsStore settingsStore,
        ISmtpMailSender sender,
        IHostStore hosts,
        IUserStore users,
        IAnalysisRecordQuery records,
        IRecordHandlingStore handlings,
        MailNotifyStateStore state)
    {
        _settingsStore = settingsStore;
        _sender = sender;
        _hosts = hosts;
        _users = users;
        _records = records;
        _handlings = handlings;
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
        try
        {
            // 回饋十五輪體檢批G：settings 讀取原本在 try 外——ISystemSettingsStore.Get() 若拋例外
            // （blob 讀取失敗／並發衝突，見 DB-SPEC.md 的 ConcurrencyToken 說明）會直接穿透
            // NotifyAfterRunAsync，讓呼叫端（SchedulerHostedService）的外層 catch 誤把已成功的
            // 分析執行覆寫成失敗結果——「通知永遠不能弄掛分析」這句話必須連設定讀取都算在內。
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled) return;

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
        try
        {
            // 回饋十五輪體檢批G：settings 讀取原本在 try 外，未受保護——這支方法緊接著在
            // SchedulerHostedService.TickAsync 的排程窗口判斷之前呼叫且沒有自己的 try/catch
            // （文件註解宣稱「內部自行 try/catch 到底...不需要額外保護」），一旦 Get() 拋例外，
            // 整個 TickAsync 連同下方排程窗口判斷都會被跳過，等於通知路徑間接卡住了排程觸發。
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled) return;

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

        var ordered = qualifying.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel)).ToList();
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine($"{dateText} 執行摘要：{summary}").AppendLine();
        foreach (var record in ordered.Take(SummaryBodyLineLimit))
        {
            var hostName = ResolveHostDisplayName(record);
            body.AppendLine($"  - [{record.RiskLevel}風險] {hostName}　錯誤 {record.ErrorCount}／警告 {record.WarningCount}" +
                             (string.IsNullOrWhiteSpace(record.Headline) ? "" : $"　{record.Headline}"));
        }
        if (ordered.Count > SummaryBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {ordered.Count - SummaryBodyLineLimit} 台請至站台的問題查詢頁檢視。");
        }

        await SendSafeAsync(settings, recipients, subject, body.ToString(), ct);
    }

    /// <summary>
    /// 高風險即時通知：**按收件人聚合**（回饋十六輪批次A-1，取代原本「逐主機日一封信」）——
    /// 一位收件人一次執行最多只收一封信，信中簡述本次新增的高風險主機，細節導向站台。
    /// 這同時解決了 2000 台規模下「300~1000 個高風險主機日＝300~1000 封信」的序列寄送風暴
    /// （見回饋十六輪體檢發現1）：信件數從「主機數」降為「收件人數」，通常只有個位數到幾十封。
    ///
    /// 收件人解析分兩層：全域收件人（<see cref="SystemSettings.MailRecipients"/>）各自收到
    /// 一封列出「本次全部」pending 的信；有開啟 <see cref="SystemSettings.MailNotifyHostOwners"/>
    /// 時，非全域收件人的主機負責人另外收到一封只列「自己負責」主機的信——已經是全域收件人的
    /// 負責人不重複收信（他已經在全域信裡看到全部內容）。
    /// </summary>
    private async Task SendUrgentNotificationsAsync(SystemSettings settings, List<DailyAnalysisRecord> records, CancellationToken ct)
    {
        var highRisk = records.Where(r => r.RiskLevel == RiskLevels.High).ToList();
        if (highRisk.Count == 0) return;

        var state = _state.Get();
        var pending = highRisk.Where(r => !state.UrgentSentKeys.Contains(UrgentKey(r))).ToList();
        if (pending.Count == 0) return;

        var globalRecipients = settings.MailRecipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var globalSet = new HashSet<string>(globalRecipients, StringComparer.OrdinalIgnoreCase);

        // 保序：全域收件人（依 MailRecipients 清單順序）在前，負責人（依 pending 出現順序）在後——
        // 熔斷（下方 UrgentFailureThreshold）依這個順序判斷「連續」失敗，順序必須是明確保證的行為
        // 而非依賴 Dictionary 迭代順序這種實作細節。
        var recipientOrder = new List<string>();
        var perRecipient = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in globalRecipients)
        {
            perRecipient[email] = new List<DailyAnalysisRecord>(pending);
            recipientOrder.Add(email);
        }

        if (settings.MailNotifyHostOwners)
        {
            foreach (var record in pending)
            {
                var host = _hosts.Get(record.HostId);
                if (host == null) continue;

                foreach (var ownerId in host.OwnerUserIds)
                {
                    var email = _users.Get(ownerId)?.Email;
                    if (string.IsNullOrWhiteSpace(email) || globalSet.Contains(email)) continue;

                    if (!perRecipient.TryGetValue(email, out var list))
                    {
                        list = new List<DailyAnalysisRecord>();
                        perRecipient[email] = list;
                        recipientOrder.Add(email);
                    }
                    if (!list.Contains(record)) list.Add(record);
                }
            }
        }

        // 沒人可收（全域清單空且無負責人 email）：這批 pending 完全不標記，設定補齊後
        // 下次執行自動補寄——修掉「先啟用通知、後填收件人」那幾天永久漏寄的路徑（發現2b）
        if (perRecipient.Count == 0) return;

        // recordKey → 涵蓋到它的收件人清單，供寄送完成後判斷該不該標記已寄
        var coverage = new Dictionary<string, List<string>>();
        foreach (var (email, recs) in perRecipient)
        {
            foreach (var record in recs)
            {
                var key = UrgentKey(record);
                if (!coverage.TryGetValue(key, out var emails))
                {
                    emails = new List<string>();
                    coverage[key] = emails;
                }
                emails.Add(email);
            }
        }

        var recipientSuccess = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var consecutiveFailures = 0;
        var circuitBroken = false;

        foreach (var (email, recs) in perRecipient)
        {
            if (circuitBroken)
            {
                recipientSuccess[email] = false;
                continue;
            }

            var (subject, body) = BuildUrgentMessage(settings, recs);
            var success = await SendSafeAsync(settings, new List<string> { email }, subject, body, ct);
            recipientSuccess[email] = success;

            if (success)
            {
                consecutiveFailures = 0;
            }
            else if (++consecutiveFailures >= UrgentFailureThreshold)
            {
                // 連續失敗熔斷（發現1）：SMTP 整台不通時，不把「30 秒逾時 × 剩餘收件人數」
                // 全部付掉——聚合後收件人數通常不多，熔斷主要是保底，不是常態路徑
                circuitBroken = true;
                Log.Warn("[Mail] 連續 {N} 封高風險即時通知寄送失敗，本輪停止剩餘寄送（尚有 {Remaining} 位收件人未寄）。",
                    UrgentFailureThreshold, perRecipient.Count - recipientSuccess.Count);
            }
        }

        // 標記語意：涵蓋此 record 的信「全部」寄成功才標記已寄（發現2a 的根修）——寧可讓
        // 已收到的人下次重複收到（信量小、發生率低），也不漏寄。取消例外會在 SendSafeAsync
        // 內重新拋出，讓迴圈中斷、未寄到的收件人維持 recipientSuccess 缺項（視為未成功）。
        var cutoff = DateTime.Today.AddDays(-settings.RetentionDays);
        _state.Update(s =>
        {
            foreach (var record in pending)
            {
                var key = UrgentKey(record);
                if (coverage.TryGetValue(key, out var emails) &&
                    emails.All(e => recipientSuccess.TryGetValue(e, out var ok) && ok))
                {
                    s.UrgentSentKeys.Add(key);
                }
            }
            s.UrgentSentKeys.RemoveWhere(key => IsBeforeCutoff(key, cutoff));
        });
    }

    private (string Subject, string Body) BuildUrgentMessage(SystemSettings settings, List<DailyAnalysisRecord> records)
    {
        var ordered = records.OrderByDescending(r => r.Date).ThenBy(r => ResolveHostDisplayName(r)).ToList();
        var dateText = DateTime.Today.ToString("yyyy-MM-dd");

        string hostLabel;
        string summary;
        if (ordered.Count == 1)
        {
            hostLabel = ResolveHostDisplayName(ordered[0]);
            summary = ordered[0].Headline?.Length > 0 ? ordered[0].Headline : "偵測到高風險訊號";
        }
        else
        {
            hostLabel = $"{ordered.Count} 台主機";
            summary = $"{ordered.Count} 台主機新增高風險日";
        }

        var subject = ExpandTemplate(settings.MailSubjectTemplate, hostLabel, dateText, RiskLevels.High, "高風險即時通知", summary);

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(ordered.Count == 1
            ? $"主機 {hostLabel} 於 {ordered[0].Date:yyyy-MM-dd} 判定為高風險日。"
            : "本次執行新增以下高風險主機日：");
        body.AppendLine();

        foreach (var record in ordered.Take(UrgentBodyLineLimit))
        {
            var name = ResolveHostDisplayName(record);
            var line = $"  - {name}　{record.Date:yyyy-MM-dd}";
            if (!string.IsNullOrWhiteSpace(record.Headline)) line += $"　{record.Headline}";
            body.AppendLine(line);
        }
        if (ordered.Count > UrgentBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {ordered.Count - UrgentBodyLineLimit} 筆請至站台的問題查詢頁檢視。");
        }

        if (ordered.Count == 1 && !string.IsNullOrWhiteSpace(ordered[0].RiskBasis))
        {
            body.AppendLine().AppendLine($"判定依據：{ordered[0].RiskBasis}");
        }

        return (subject, body.ToString());
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

        var hostGroups = qualifying.GroupBy(r => r.HostId).ToList();
        foreach (var group in hostGroups.Take(SummaryBodyLineLimit))
        {
            var hostName = ResolveHostDisplayName(group.First());
            var highCount = group.Count(r => r.RiskLevel == RiskLevels.High);
            var mediumCount = group.Count(r => r.RiskLevel == RiskLevels.Medium);
            body.AppendLine($"  - {hostName}：高風險 {highCount} 天、中風險 {mediumCount} 天");
        }
        if (hostGroups.Count > SummaryBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {hostGroups.Count - SummaryBodyLineLimit} 台請至站台的問題查詢頁檢視。");
        }

        // 週報附未處理數（docs/FEEDBACK-15-PLAN.md D-3）：不限窗口——「還沒處理完」是當下狀態，
        // 一個上上週就掛著沒人動的風險日正是週報最該提醒的東西，只看近 7 天反而幫忙藏爛帳
        // （同 docs/DB-SPEC.md「進行中案件不論多舊都留著」的取向）。每日摘要不附：當日摘要
        // 的重點是「今天發生什麼」，待辦總量每天寄一次只會變成沒人看的固定噪音。
        if (isWeekly)
        {
            var unresolved = _handlings.GetUnresolved();
            body.AppendLine();
            body.AppendLine(unresolved.Count == 0
                ? "目前沒有未處理（含處理中）的風險日。"
                : $"目前未處理（含處理中）的風險日共 {unresolved.Count} 筆，請至站台的問題查詢頁檢視。");
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
    /// 寄送失敗只記 WARN、回 false，不往外拋——這是三路觸發共用的最後一道防線，呼叫端（排程輪詢／
    /// 執行收尾）不該因為一封信寄失敗而受影響。SmtpClient 逾時無獨立設定，共用 30 秒合理上限，
    /// relay 通常是內網，30 秒逾時仍未回應代表連線本身有問題，繼續等沒有意義。
    ///
    /// **回傳成敗**（回饋十六輪批次A-2）：<see cref="SendUrgentNotificationsAsync"/> 需要依成敗
    /// 決定要不要標記 <see cref="MailNotifyState.UrgentSentKeys"/>——原本吞掉一切例外、
    /// 迴圈跑完就無條件全部標記已寄，寄送失敗或服務中途停止時會讓高風險通知永久漏寄
    /// （回饋十六輪體檢發現2）。
    ///
    /// **外層真正的取消要求會重新拋出**（<c>ct.IsCancellationRequested</c>）：呼叫端（服務停止／
    /// 執行被取消）需要迴圈立即中斷，不能讓尚未寄出的收件人也被判定「處理過」。30 秒逾時觸發的
    /// 取消（外層 ct 本身未取消）視為單純寄送失敗，回 false 讓迴圈繼續處理下一位收件人。
    /// </summary>
    private async Task<bool> SendSafeAsync(SystemSettings settings, List<string> recipients, string subject, string body, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _sender.SendAsync(ResolveConnection(settings),
                new MailMessageSpec(settings.MailFrom, recipients, subject, body), timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 寄送失敗：{Subject}（收件人 {Count} 位）", subject, recipients.Count);
            return false;
        }
    }
}
