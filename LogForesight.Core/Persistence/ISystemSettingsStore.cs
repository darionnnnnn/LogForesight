using System.Text.Json;

namespace LogForesight;

/// <summary>全站系統設定的讀寫（↔ webdata blob，key=system_settings）。單一物件，非清單</summary>
public interface ISystemSettingsStore
{
    SystemSettings Get();

    /// <summary>讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證），mutation 直接修改傳入的物件</summary>
    SystemSettings Update(Action<SystemSettings> mutation);
}

/// <summary>
/// <see cref="ISystemSettingsStore"/> 的實作：整份 JSON 存一筆 blob，與 <see cref="JsonBlobCollection{T}"/>
/// 系列 store 同一套底層（<see cref="IJsonBlobStore"/>），但本體是單一物件而非清單，
/// 故不繼承 JsonBlobCollection（它的 Read/Mutate 是針對 List&lt;T&gt; 設計的）。
/// </summary>
public class JsonSystemSettingsStore : ISystemSettingsStore
{
    private readonly IJsonBlobStore _blob;

    public JsonSystemSettingsStore(IJsonBlobStore blob) => _blob = blob;

    public SystemSettings Get() => Deserialize(_blob.Read());

    public SystemSettings Update(Action<SystemSettings> mutation) =>
        _blob.Mutate(raw =>
        {
            var settings = Deserialize(raw);
            mutation(settings);
            settings.UpdatedAt = DateTime.Now;
            return (JsonSerializer.Serialize(settings, LfJsonOptions.Pretty), settings);
        });

    /// <summary>內容不存在（首次執行）時回預設值——<see cref="SystemSettings"/> 的欄位預設值即沿用原本的寫死行為</summary>
    private static SystemSettings Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new SystemSettings()
            : JsonSerializer.Deserialize<SystemSettings>(json, LfJsonOptions.Pretty) ?? new SystemSettings();
}
