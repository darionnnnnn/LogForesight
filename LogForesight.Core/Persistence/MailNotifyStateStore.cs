namespace LogForesight.Core.Persistence;

/// <summary>郵件通知寄送狀態的儲存（回饋十五輪批次D），單一物件型 blob，見 <see cref="JsonBlobSingleton{T}"/>。</summary>
public class MailNotifyStateStore : JsonBlobSingleton<MailNotifyState>
{
    public MailNotifyStateStore(EfJsonBlobStore blob) : base(blob) { }
}
