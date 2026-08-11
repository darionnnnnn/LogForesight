namespace LogForesight.Web.Services.Mail;

/// <summary>一封要送出的信（回饋十五輪批次D）：純資料，不含連線設定——連線設定另外由
/// <see cref="SmtpConnectionSpec"/> 帶入，讓「這次連哪個 SMTP」與「寄什麼內容」分開，
/// 測試寄信（用表單目前值，可能還沒儲存）與正式排程觸發共用同一組實作。</summary>
public sealed record MailMessageSpec(
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string Body);

/// <summary>SMTP 連線設定，來源可能是已儲存的 SystemSettings，也可能是設定頁「測試寄信」
/// 當下表單裡尚未儲存的值——兩條路徑共用同一個型別與同一個 sender 實作。</summary>
public sealed record SmtpConnectionSpec(
    string Server,
    int Port,
    bool UseTls,
    string Account,
    string? Password);

/// <summary>
/// SMTP 寄送的抽象（回饋十五輪批次D）：介面隔離讓業務邏輯（<see cref="MailNotificationService"/>）
/// 與實際傳輸方式解耦——測試打樁不必真的連 SMTP，日後要換成 MailKit 之類的套件也只動
/// <see cref="SystemNetSmtpMailSender"/> 這一個類別。
/// </summary>
public interface ISmtpMailSender
{
    Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default);
}
