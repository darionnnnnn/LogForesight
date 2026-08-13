namespace LogForesight.Core.Persistence;

/// <summary>首次啟動精靈狀態的儲存（回饋十八輪批次H），單一物件型 blob，見 <see cref="JsonBlobSingleton{T}"/>。</summary>
public class SetupWizardStateStore : JsonBlobSingleton<SetupWizardState>
{
    public SetupWizardStateStore(EfJsonBlobStore blob) : base(blob) { }

    protected override void Touch(SetupWizardState value) => value.UpdatedAt = DateTime.Now;
}
