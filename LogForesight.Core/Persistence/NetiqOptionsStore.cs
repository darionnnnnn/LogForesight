namespace LogForesight.Core.Persistence;

/// <summary>
/// NetIQ 連線與節流參數的讀寫（↔ webdata blob，key=netiq_options）。單一物件，非清單，
/// 共用邏輯見 <see cref="JsonBlobSingleton{T}"/>。
/// </summary>
public class NetiqOptionsStore : JsonBlobSingleton<NetiqOptions>
{
    public NetiqOptionsStore(EfJsonBlobStore blob) : base(blob) { }

    protected override void Touch(NetiqOptions value) => value.UpdatedAt = DateTime.Now;
}
