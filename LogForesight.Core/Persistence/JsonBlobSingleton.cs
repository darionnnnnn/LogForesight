using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 「整份 JSON 存一筆」的單一物件型 store 共用基底（system_settings、schedule_options、
/// netiq_options 等 webdata blob）——本體是單一物件而非清單，故不繼承
/// <see cref="JsonBlobCollection{T}"/>（它的 Read/Mutate 是針對 List&lt;T&gt; 設計的）。
///
/// 讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證）與「內容不存在
/// 時回預設值」的語意集中在這裡一次，避免每個單一物件型 store 各自重寫一份幾乎相同的邏輯。
/// </summary>
public abstract class JsonBlobSingleton<T> where T : new()
{
    private readonly EfJsonBlobStore _blob;

    protected JsonBlobSingleton(EfJsonBlobStore blob) => _blob = blob;

    public T Get() => Deserialize(_blob.Read());

    /// <summary>mutation 直接修改傳入的物件；成功後由 <see cref="Touch"/> 蓋章（如 UpdatedAt）</summary>
    public T Update(Action<T> mutation) =>
        _blob.Mutate(raw =>
        {
            var value = Deserialize(raw);
            mutation(value);
            Touch(value);
            return (JsonSerializer.Serialize(value, LfJsonOptions.Pretty), value);
        });

    /// <summary>Update 成功後的蓋章動作（例如 UpdatedAt=DateTime.Now）；預設不做事</summary>
    protected virtual void Touch(T value) { }

    /// <summary>反序列化後的掛勾（例如舊設定遷移）；預設不做事</summary>
    protected virtual void OnDeserialized(T value) { }

    /// <summary>內容不存在（首次執行）時回預設值——沿用型別的欄位預設值</summary>
    private T Deserialize(string? json)
    {
        var value = string.IsNullOrWhiteSpace(json)
            ? new T()
            : JsonSerializer.Deserialize<T>(json, LfJsonOptions.Pretty) ?? new T();
        OnDeserialized(value);
        return value;
    }
}
