using System.Text.Json;

namespace LogForesight;

/// <summary>
/// 權限/角色異動監控的快照存取，與分析紀錄分開（不同的生命週期與存取模式：這裡只需要「最新一份」）。
/// 整份快照存一筆 <see cref="IJsonBlobStore"/>（key=permission_snapshot）。
/// </summary>
public class JsonPermissionSnapshotStore
{
    private readonly IJsonBlobStore _blob;

    public JsonPermissionSnapshotStore(IJsonBlobStore blob) => _blob = blob;

    /// <summary>無快照（首次執行）回傳 null</summary>
    public PermissionSnapshot? Load()
    {
        var text = _blob.Read();
        if (text == null) return null;

        try
        {
            return JsonSerializer.Deserialize<PermissionSnapshot>(text, LfJsonOptions.Pretty);
        }
        catch (JsonException)
        {
            Console.WriteLine("  權限快照損毀，本次重建基準（不產生異動告警）。");
            return null;
        }
    }

    public void Save(PermissionSnapshot snapshot) =>
        _blob.Mutate<object?>(_ => (JsonSerializer.Serialize(snapshot, LfJsonOptions.Pretty), null));
}
