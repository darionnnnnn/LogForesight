using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="PermissionSnapshotStore"/> 讀寫原本各自 new 一份不同的 JsonSerializerOptions
/// （Save 縮排、Load 完全不帶選項）——收斂共用 <see cref="LfJsonOptions"/> 之前，先釘住
/// 「舊格式（Load 端原本沒有的選項）存下的內容，新選項仍讀得回來」這個相容性前提。
/// </summary>
public class PermissionSnapshotStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private PermissionSnapshotStore Store() => new(_fx.Blob("permission_snapshot"));

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SaveAndLoadRoundTrip保留欄位()
    {
        var store = Store();
        var snapshot = new PermissionSnapshot
        {
            CapturedAt = new DateTime(2026, 7, 21),
            AdministratorsMembers = new List<string> { "alice", "bob" },
            Folders = new Dictionary<string, FolderAclSnapshot>
            {
                ["C:\\data"] = new() { Accessible = true, Owner = "SYSTEM", Rules = new List<string> { "Everyone:R" } }
            }
        };

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.CapturedAt, loaded!.CapturedAt);
        Assert.Equal(snapshot.AdministratorsMembers, loaded.AdministratorsMembers);
        var folder = Assert.Single(loaded.Folders);
        Assert.Equal("C:\\data", folder.Key);
        Assert.True(folder.Value.Accessible);
        Assert.Equal("SYSTEM", folder.Value.Owner);
    }

    /// <summary>
    /// 舊版 Load() 不帶任何選項（等於 PascalCase 完全比對、無大小寫容忍）。
    /// 收斂後的 LfJsonOptions.Pretty 多了 PropertyNameCaseInsensitive，只會更寬鬆，
    /// 用舊版行為存下的內容（欄位名稱本來就是 PascalCase）驗證仍讀得回來。
    /// </summary>
    [Fact]
    public void 舊版無選項存下的內容_新共用選項仍讀得回來()
    {
        var blob = _fx.Blob("permission_snapshot");
        var snapshot = new PermissionSnapshot
        {
            CapturedAt = new DateTime(2026, 7, 1),
            AdministratorsMembers = new List<string> { "legacy" }
        };

        // 模擬舊版 Save：new JsonSerializerOptions { WriteIndented = true }（無 CI、無 Encoder、無 Converters）
        var legacyJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        blob.Mutate<object?>(_ => (legacyJson, null));

        var store = new PermissionSnapshotStore(blob);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.CapturedAt, loaded!.CapturedAt);
        Assert.Equal(snapshot.AdministratorsMembers, loaded.AdministratorsMembers);
    }
}
