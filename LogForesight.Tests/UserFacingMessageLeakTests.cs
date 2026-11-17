using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 例外訊息不外洩（docs/WEB-SPEC.md §7.2）：小幫手規則，以及快照待寫檔檢查這個外洩點。
/// 其他外洩點的測試與各自的服務測試放在一起（立即派工、PRTG 規模估算、AI 額外請求欄位）。
/// </summary>
[Collection("NLogGlobalConfig")]
public class UserFacingMessageLeakTests : IDisposable
{
    private const string Secret = @"C:\secret\x.db";
    private const string SecretUrl = "http://10.0.0.1/api";

    private readonly string _dir;
    private readonly StorageBackend _backend;

    public UserFacingMessageLeakTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-userfacing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 領域例外與已登錄專屬型別_訊息原樣給使用者()
    {
        Assert.Equal("找不到", UserFacingMessage.For(DomainException.NotFound("找不到"), "備用"));
        Assert.Equal("忙碌", UserFacingMessage.For(new PrtgScopePurgeBusyException("忙碌"), "備用"));
        Assert.Equal("連不上", UserFacingMessage.For(new NetiqDiscoveryException("連不上"), "備用"));
    }

    [Fact]
    public void 一般例外_給備用說明不含原文()
    {
        foreach (var ex in new Exception[]
        {
            new IOException($"找不到檔案 {Secret}"),
            new HttpRequestException($"連線失敗 {SecretUrl}"),
            new InvalidOperationException(Secret),
        })
        {
            var message = UserFacingMessage.For(ex, "請稍後再試。");
            Assert.Equal("請稍後再試。", message);
        }
    }

    /// <summary>E1-a／E1-b：快照待寫檔完整性檢查遇到一般例外（檔案被鎖）→ 回應不含路徑，記錄檔含原文</summary>
    [WindowsFact]
    public void 快照待寫檔檢查_一般例外_回應不含路徑_記錄檔含原文()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.EnableIncremental("http://prtg.example", now);
        journal.AppendDelta("http://prtg.example", new PrtgSnapshotAccumulator.CheckpointDelta(
            new[] { new PrtgSnapshotAccumulator.CheckpointRow(now.AddHours(-2), 1, 12, 1, 12, 12) },
            Array.Empty<PrtgSnapshotAccumulator.CheckpointKey>()), null, null, now);

        var locked = File.Exists(journal.ManifestFilePath) ? journal.ManifestFilePath : journal.FilePath;
        using var capture = NLogCapture.For(typeof(PrtgSnapshotJournal));
        PrtgSnapshotJournalInspection inspection;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            inspection = journal.Inspect(now);

        Assert.False(inspection.IntegrityOk);
        Assert.DoesNotContain(_dir, inspection.Failure ?? "");
        Assert.DoesNotContain(Path.GetFileName(locked), inspection.Failure ?? "");
        Assert.Contains(capture.Logs, l => l.Contains(Path.GetFileName(locked)));
    }
}
