using System.Text.Json;

namespace LogForesight;

/// <summary>
/// NetIQ 連線與節流參數的讀寫（↔ webdata blob，key=netiq_options）。單一物件，非清單。
/// 整份 JSON 存一筆 blob，與 <see cref="SystemSettingsStore"/> 同一套模式
/// （單一物件而非清單，故不繼承 <see cref="JsonBlobCollection{T}"/>）。
/// </summary>
public class NetiqOptionsStore
{
    private readonly IJsonBlobStore _blob;

    public NetiqOptionsStore(IJsonBlobStore blob) => _blob = blob;

    public NetiqOptions Get() => Deserialize(_blob.Read());

    /// <summary>讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證），mutation 直接修改傳入的物件</summary>
    public NetiqOptions Update(Action<NetiqOptions> mutation) =>
        _blob.Mutate(raw =>
        {
            var options = Deserialize(raw);
            mutation(options);
            options.UpdatedAt = DateTime.Now;
            return (JsonSerializer.Serialize(options, LfJsonOptions.Pretty), options);
        });

    private static NetiqOptions Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new NetiqOptions()
            : JsonSerializer.Deserialize<NetiqOptions>(json, LfJsonOptions.Pretty) ?? new NetiqOptions();
}
