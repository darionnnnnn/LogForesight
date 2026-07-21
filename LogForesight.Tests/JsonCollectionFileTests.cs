using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="JsonCollectionFile{T}"/> 的跨程序互斥行為（以 <see cref="JsonHostStore"/> 為代表）。
///
/// 為什麼這件事值得專門測：`hosts.json` 是唯一由批次與 Web **兩個行程**共同寫入的檔案，
/// 而讀改寫沒有互斥時的失敗方式是安靜的——同一個 HostId 配給兩台主機（識別碼是分析紀錄的
/// 關聯鍵，撞號等於紀錄歸錯主機），或後寫的整份蓋掉先寫的。沒有測試釘住，
/// 日後有人為了「少開一個檔案」把鎖拿掉，不會有任何跡象。
/// </summary>
public class JsonCollectionFileLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public JsonCollectionFileLockTests()
    {
        _path = Path.Combine(_dir, "hosts.json");
    }

    /// <summary>
    /// 另一個行程持有鎖檔時，寫入必須等待而不是直接覆蓋。
    /// 這裡用「另一個 FileStream 獨占同一個鎖檔」模擬——對檔案系統而言，
    /// 另一個行程的handle 與本測試開的 handle 沒有區別。
    /// </summary>
    [Fact]
    public async Task 鎖檔被他方持有時_寫入等待至釋放()
    {
        var store = new JsonHostStore(_path);
        store.Upsert(new WebHost { HostName = "SRV-01" });

        var foreignHolder = new FileStream(
            _path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var write = Task.Run(() => store.Upsert(new WebHost { HostName = "SRV-02" }));

            var duringLock = await Task.WhenAny(write, Task.Delay(TimeSpan.FromMilliseconds(300)));
            Assert.NotSame(write, duringLock);   // 鎖被他方持有期間不該完成寫入

            foreignHolder.Dispose();

            var afterRelease = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(write, afterRelease);    // 釋放後應完成
            await write;
        }
        finally
        {
            foreignHolder.Dispose();
        }

        Assert.Equal(2, store.GetAll().Count);
    }

    /// <summary>鎖檔用完即消失，不在資料目錄累積</summary>
    [Fact]
    public void 正常寫入後_不留下鎖檔()
    {
        new JsonHostStore(_path).Upsert(new WebHost { HostName = "SRV-01" });

        Assert.False(File.Exists(_path + ".lock"));
    }

    /// <summary>
    /// 併發建立主機不得配出重複識別碼。行程內鎖已涵蓋這條路徑，
    /// 但識別碼現在是分析紀錄的關聯鍵，重複的後果是紀錄歸錯主機——值得明確釘住。
    /// </summary>
    [Fact]
    public void 併發建立主機_識別碼不重複()
    {
        var store = new JsonHostStore(_path);

        Parallel.For(0, 20, i => store.Upsert(new WebHost { HostName = $"SRV-{i:00}" }));

        var ids = store.GetAll().Select(h => h.HostId).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
