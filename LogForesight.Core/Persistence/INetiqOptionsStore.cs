using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight;

/// <summary>NetIQ 連線與節流參數的讀寫（↔ webdata blob，key=netiq_options）。單一物件，非清單</summary>
public interface INetiqOptionsStore
{
    NetiqOptions Get();

    /// <summary>讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證），mutation 直接修改傳入的物件</summary>
    NetiqOptions Update(Action<NetiqOptions> mutation);
}

/// <summary>
/// <see cref="INetiqOptionsStore"/> 的實作：整份 JSON 存一筆 blob，與 <see cref="JsonSystemSettingsStore"/> 同一套模式
/// （單一物件而非清單，故不繼承 <see cref="JsonBlobCollection{T}"/>）。
/// </summary>
public class JsonNetiqOptionsStore : INetiqOptionsStore
{
    private readonly IJsonBlobStore _blob;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonNetiqOptionsStore(IJsonBlobStore blob) => _blob = blob;

    public NetiqOptions Get() => Deserialize(_blob.Read());

    public NetiqOptions Update(Action<NetiqOptions> mutation) =>
        _blob.Mutate(raw =>
        {
            var options = Deserialize(raw);
            mutation(options);
            options.UpdatedAt = DateTime.Now;
            return (JsonSerializer.Serialize(options, JsonOptions), options);
        });

    private static NetiqOptions Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new NetiqOptions()
            : JsonSerializer.Deserialize<NetiqOptions>(json, JsonOptions) ?? new NetiqOptions();
}
