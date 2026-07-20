using System.Text.Json;

namespace LogForesight;

/// <summary>預設的權限快照實作：單一 JSON 檔案（執行檔同目錄的 permission_snapshot.json）</summary>
public class FilePermissionSnapshotStore : IPermissionSnapshotStore
{
    private readonly string _snapshotPath;

    public FilePermissionSnapshotStore(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath ?? Path.Combine(AppContext.BaseDirectory, "permission_snapshot.json");
    }

    public PermissionSnapshot? Load()
    {
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PermissionSnapshot>(File.ReadAllText(_snapshotPath));
        }
        catch (JsonException)
        {
            Console.WriteLine("  權限快照檔損毀，本次重建基準（不產生異動告警）。");
            return null;
        }
    }

    public void Save(PermissionSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_snapshotPath, json);
    }
}
