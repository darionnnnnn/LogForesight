namespace LogForesight.Core.Persistence;

/// <summary>排程設定的讀寫（↔ webdata blob，key=schedule_options）。單一物件，非清單</summary>
public interface IScheduleOptionsStore
{
    ScheduleOptions Get();

    /// <summary>讀→改→寫的原子更新，mutation 直接修改傳入的物件</summary>
    ScheduleOptions Update(Action<ScheduleOptions> mutation);
}

/// <summary><see cref="IScheduleOptionsStore"/> 的實作，共用邏輯見 <see cref="JsonBlobSingleton{T}"/>。
/// 內容不存在（首次執行）時回預設值——Enabled=false，行為與升級前相同。</summary>
public class ScheduleOptionsStore : JsonBlobSingleton<ScheduleOptions>, IScheduleOptionsStore
{
    public ScheduleOptionsStore(EfJsonBlobStore blob) : base(blob) { }

    protected override void Touch(ScheduleOptions value) => value.UpdatedAt = DateTime.Now;
}
