using System.Text.Json;

namespace LogForesight;

/// <summary><see cref="IPermissionSnapshotStore"/> 的實作：整份快照存一筆 <see cref="IJsonBlobStore"/>（key=permission_snapshot）</summary>
public class JsonPermissionSnapshotStore : IPermissionSnapshotStore
{
    private readonly IJsonBlobStore _blob;

    public JsonPermissionSnapshotStore(IJsonBlobStore blob) => _blob = blob;

    public PermissionSnapshot? Load()
    {
        var text = _blob.Read();
        if (text == null) return null;

        try
        {
            return JsonSerializer.Deserialize<PermissionSnapshot>(text);
        }
        catch (JsonException)
        {
            Console.WriteLine("  權限快照損毀，本次重建基準（不產生異動告警）。");
            return null;
        }
    }

    public void Save(PermissionSnapshot snapshot) =>
        _blob.Mutate<object?>(_ => (JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), null));
}
