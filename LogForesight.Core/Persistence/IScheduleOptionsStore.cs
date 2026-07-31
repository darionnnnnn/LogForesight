using System.Text.Json;
using LogForesight.Sql;

namespace LogForesight;

/// <summary>排程設定的讀寫（↔ webdata blob，key=schedule_options）。單一物件，非清單</summary>
public interface IScheduleOptionsStore
{
    ScheduleOptions Get();

    /// <summary>讀→改→寫的原子更新，mutation 直接修改傳入的物件</summary>
    ScheduleOptions Update(Action<ScheduleOptions> mutation);
}

/// <summary><see cref="IScheduleOptionsStore"/> 的實作：整份 JSON 存一筆 blob，同 <see cref="SystemSettingsStore"/> 的作法</summary>
public class ScheduleOptionsStore : IScheduleOptionsStore
{
    private readonly EfJsonBlobStore _blob;

    public ScheduleOptionsStore(EfJsonBlobStore blob) => _blob = blob;

    public ScheduleOptions Get() => Deserialize(_blob.Read());

    public ScheduleOptions Update(Action<ScheduleOptions> mutation) =>
        _blob.Mutate(raw =>
        {
            var options = Deserialize(raw);
            mutation(options);
            options.UpdatedAt = DateTime.Now;
            return (JsonSerializer.Serialize(options, LfJsonOptions.Pretty), options);
        });

    /// <summary>內容不存在（首次執行，或尚未有人存過設定）時回預設值——Enabled=false，行為與升級前相同</summary>
    private static ScheduleOptions Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ScheduleOptions()
            : JsonSerializer.Deserialize<ScheduleOptions>(json, LfJsonOptions.Pretty) ?? new ScheduleOptions();
}
