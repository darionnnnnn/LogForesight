namespace LogForesight.Core.Models;

/// <summary>
/// 系統設定的郵件通知共用驗證與判定 helper（回饋五十輪批次 F-1b）。
/// 供 <see cref="LogForesight.Core.Models.SystemSettings"/> 相關服務共用，
/// 確保觸發項與收件人的判定規則在儲存驗證與就緒判定一致。
/// </summary>
public static class SystemSettingsMailHelper
{
    /// <summary>是否至少開啟一項通知觸發項</summary>
    public static bool HasAnyTriggerEnabled(bool onRunCompleted, bool dailyEnabled, bool weeklyEnabled, bool urgentEnabled) =>
        onRunCompleted || dailyEnabled || weeklyEnabled || urgentEnabled;

    /// <summary>是否至少開啟一項通知觸發項</summary>
    public static bool HasAnyTriggerEnabled(SystemSettings settings) =>
        HasAnyTriggerEnabled(settings.MailOnRunCompleted, settings.MailDailyEnabled, settings.MailWeeklyEnabled, settings.MailUrgentEnabled);

    /// <summary>收件人清單正規化：去除空白行、前後空白、去重（不分大小寫）</summary>
    public static List<string> NormalizeRecipients(IEnumerable<string>? recipients) =>
        (recipients ?? Enumerable.Empty<string>())
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>單一電子郵件位址是否合法</summary>
    public static bool IsValidEmail(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        return System.Net.Mail.MailAddress.TryCreate(address.Trim(), out _);
    }

    /// <summary>取得正規化後的所有有效收件人位址</summary>
    public static List<string> GetValidRecipients(IEnumerable<string>? recipients) =>
        NormalizeRecipients(recipients)
            .Where(IsValidEmail)
            .ToList();

    /// <summary>是否至少有一位有效收件人</summary>
    public static bool HasValidRecipient(IEnumerable<string>? recipients) =>
        GetValidRecipients(recipients).Count > 0;

    /// <summary>郵件步驟是否就緒：啟用、SMTP 伺服器非空、至少一項觸發項、至少一位有效收件人</summary>
    public static bool IsConfigured(SystemSettings settings) =>
        settings.MailEnabled &&
        !string.IsNullOrWhiteSpace(settings.SmtpServer) &&
        HasAnyTriggerEnabled(settings) &&
        HasValidRecipient(settings.MailRecipients);
}
