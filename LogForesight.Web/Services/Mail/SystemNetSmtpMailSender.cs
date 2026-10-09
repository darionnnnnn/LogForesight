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
    private const int SendTimeoutMilliseconds = 30_000;

    public async Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var client = new SmtpClient(connection.Server, connection.Port)
        {
            EnableSsl = connection.UseTls,
            Timeout = SendTimeoutMilliseconds
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

        // .NET 8's event-based SendMailAsync completion path can report success when its final
        // response read fails with IOException. The synchronous Send path propagates a missing
        // final DATA reply as failure, so run it on an owned worker and always join that worker.
        // Cancellation aborts/disposes the client; awaiting the worker prevents abandoned background sends.
        // A dedicated worker avoids occupying a shared ThreadPool thread during a slow relay response.
        var worker = Task.Factory.StartNew(() => client.Send(mail), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var cancellation = ct.Register(static state => CancelClient((SmtpClient)state!), client);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("SMTP send was cancelled.", ct);
        }
    }

    private static void CancelClient(SmtpClient client)
    {
        try
        {
            client.SendAsyncCancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed and its client was already disposed.
        }
        finally
        {
            // SmtpClient.Dispose aborts an in-progress send and marks an idle client unusable,
            // covering cancellation that races with the worker starting Send().
            client.Dispose();
        }
    }
}
