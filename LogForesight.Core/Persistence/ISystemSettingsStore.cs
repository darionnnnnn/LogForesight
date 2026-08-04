namespace LogForesight.Core.Persistence;

/// <summary>全站系統設定的讀寫（↔ webdata blob，key=system_settings）。單一物件，非清單</summary>
public interface ISystemSettingsStore
{
    SystemSettings Get();

    /// <summary>讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證），mutation 直接修改傳入的物件</summary>
    SystemSettings Update(Action<SystemSettings> mutation);
}

/// <summary><see cref="ISystemSettingsStore"/> 的實作，共用邏輯見 <see cref="JsonBlobSingleton{T}"/>。</summary>
public class SystemSettingsStore : JsonBlobSingleton<SystemSettings>, ISystemSettingsStore
{
    public SystemSettingsStore(EfJsonBlobStore blob) : base(blob) { }

    protected override void Touch(SystemSettings value) => value.UpdatedAt = DateTime.Now;
}
