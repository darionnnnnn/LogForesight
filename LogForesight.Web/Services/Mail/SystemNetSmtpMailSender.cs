using System.Net;
using System.Net.Mail;

namespace LogForesight.Web.Services.Mail;

/// <summary>
/// 以 <see cref="System.Net.Mail.SmtpClient"/> 實作的寄送（回饋十五輪批次D）。這個型別雖然
/// 標示為 legacy，但對內網 relay 場景（本專案的典型部署環境）已足夠，且不需要新增套件依賴；
/// 介面隔離（<see cref="ISmtpMailSender"/>）讓日後要換 MailKit 只動這一個類別，不影響呼叫端。
/// </summary>
public class SystemNetSmtpMailSender : ISmtpMailSender
{
    public async Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
    {
        using var client = new SmtpClient(connection.Server, connection.Port)
        {
            EnableSsl = connection.UseTls
        };

        // 帳號留空＝relay 不需要驗證（內網常見情境），沿用 SmtpClient 預設的匿名/Windows 整合驗證
        if (!string.IsNullOrWhiteSpace(connection.Account))
        {
            client.Credentials = new NetworkCredential(connection.Account, connection.Password ?? "");
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(message.From),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };
        foreach (var recipient in message.To)
        {
            mail.To.Add(recipient);
        }

        // .NET 5+ 的 SendMailAsync(MailMessage, CancellationToken) 多載，不需要事件式 API 的
        // SendAsyncCancel() 那套（那是給舊版 SendAsync(MailMessage, object) 用的，API 對不上）
        await client.SendMailAsync(mail, ct);
    }
}
