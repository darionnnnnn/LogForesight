using System.Text.Json;
using LogForesight.Core.Models;

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

    /// <summary>
    /// 舊版 blob 存放的是 DetailRetentionDays 與 RiskyEventRetentionDays，升級後若不處理會悄悄退回出廠預設。
    /// 這裡在反序列化後將舊值遷移至 <see cref="SystemSettings.RawEventRetentionDays"/>（取兩者中較小者以防本該清除的資料不被刪除），
    /// 並自 <see cref="SystemSettings.ExtensionData"/> 移除舊鍵，確保後續儲存時產出乾淨的新版 JSON。
    /// </summary>
    protected override void OnDeserialized(SystemSettings value)
    {
        if (value.ExtensionData == null || value.ExtensionData.Count == 0)
            return;

        int? detailDays = null;
        int? riskyDays = null;
        var keysToRemove = new List<string>();

        foreach (var (key, element) in value.ExtensionData)
        {
            if (string.Equals(key, "DetailRetentionDays", StringComparison.OrdinalIgnoreCase))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var d))
                    detailDays = d;
                keysToRemove.Add(key);
            }
            else if (string.Equals(key, "RiskyEventRetentionDays", StringComparison.OrdinalIgnoreCase))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var r))
                    riskyDays = r;
                keysToRemove.Add(key);
            }
        }

        if (detailDays.HasValue || riskyDays.HasValue)
        {
            var migrated = (detailDays, riskyDays) switch
            {
                (not null, not null) => Math.Min(detailDays.Value, riskyDays.Value),
                (not null, null) => detailDays.Value,
                (null, not null) => riskyDays.Value,
                _ => value.RawEventRetentionDays
            };
            value.RawEventRetentionDays = migrated;
        }

        foreach (var k in keysToRemove)
        {
            value.ExtensionData.Remove(k);
        }
    }
}
