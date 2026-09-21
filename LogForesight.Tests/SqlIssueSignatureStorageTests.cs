using System.Text.Json;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

public sealed class SqlIssueSignatureStorageTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly ITestOutputHelper _output;

    public SqlIssueSignatureStorageTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IssueHandlingStore_以CSharpComparer命中Unicode大小寫legacy列_更新保留原字並清除同語義列()
    {
        const string host = "SRV-K";
        var day = DateTime.Today;
        var oldKey = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);
        var requestedKey = IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error);

        using (var ctx = _fixture.NewContext())
        {
            ctx.IssueHandlings.AddRange(
                new IssueHandlingRow
                {
                    HostName = host, HostNameKey = HostNameKey.Of(host), RecordDate = day,
                    IssueKey = oldKey, Status = IssueHandlingStatuses.Resolved,
                    Note = "legacy newest", UpdatedAt = day.AddHours(2)
                },
                new IssueHandlingRow
                {
                    HostName = host, HostNameKey = HostNameKey.Of(host), RecordDate = day,
                    IssueKey = requestedKey, Status = IssueHandlingStatuses.InProgress,
                    Note = "other spelling", UpdatedAt = day.AddHours(1)
                });
            ctx.SaveChanges();
        }

        var store = new EfIssueHandlingStore(_fixture.NewContext);
        var latest = store.GetLatestNote(requestedKey, new[] { HostNameKey.Of(host) });

        Assert.NotNull(latest);
        Assert.Equal("legacy newest", latest.Value.Note);

        store.Save(new IssueHandling
        {
            HostName = host, Date = day, IssueKey = requestedKey,
            Status = IssueHandlingStatuses.Observing, Note = "updated through variant",
            UpdatedAt = day.AddHours(3)
        });

        var rows = store.GetForDay(host, day);
        Assert.Single(rows);
        Assert.Contains(rows, r => r.IssueKey == oldKey && r.Status == IssueHandlingStatuses.Observing);

        store.Clear(host, day, requestedKey);
        Assert.Empty(store.GetForDay(host, day));
    }

    [Fact]
    public void IssueHandlingStore_可見主機粗篩不擴大讀取範圍()
    {
        var day = DateTime.Today;
        var key = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);
        using (var ctx = _fixture.NewContext())
        {
            ctx.IssueHandlings.AddRange(
                NewHandlingRow("SRV-VISIBLE", day, key, "visible note", day.AddHours(1)),
                NewHandlingRow("SRV-HIDDEN", day, key, "hidden note", day.AddHours(2)));
            ctx.SaveChanges();
        }

        var result = new EfIssueHandlingStore(_fixture.NewContext)
            .GetLatestNote(IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error),
                new[] { HostNameKey.Of("SRV-VISIBLE") });

        Assert.NotNull(result);
        Assert.Equal("SRV-VISIBLE", result.Value.HostName);
        Assert.Equal("visible note", result.Value.Note);
    }

    [Fact]
    public void IssueCaseStore_命中Unicode大小寫legacy案件且IssueKeysOnHost使用同一comparer()
    {
        const string host = "SRV-K";
        var oldKey = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);
        var requestedKey = IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error);
        using (var ctx = _fixture.NewContext())
        {
            ctx.IssueCases.Add(new IssueCaseRow
            {
                CaseId = "case-legacy", HostName = host, HostNameKey = HostNameKey.Of(host),
                IssueKey = oldKey, IssueLabel = "legacy", Status = IssueHandlingStatuses.InProgress,
                HandlerId = 7, FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
                CreatedAt = DateTime.Today, UpdatedAt = DateTime.Today
            });
            ctx.SaveChanges();
        }

        var store = new EfIssueCaseStore(_fixture.NewContext);
        var open = store.GetOpen("srv-k", requestedKey);

        Assert.NotNull(open);
        Assert.Equal(oldKey, open!.IssueKey);
        Assert.Contains(requestedKey, store.IssueKeysOnHost(7, host));
        Assert.Null(store.GetOpen("SRV-OTHER", requestedKey));
    }

    [Fact]
    public void IssueHandlingStore_無主機限制的大量note仍命中正確legacy鍵並輸出耗時()
    {
        const int noiseRows = 5000;
        var day = DateTime.Today;
        var requestedKey = IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error);
        var legacyKey = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);

        using (var ctx = _fixture.NewContext())
        {
            var rows = Enumerable.Range(0, noiseRows)
                .Select(i => NewHandlingRow(
                    $"SRV-N-{i:D4}", day, IssueSignatureKey.For("System", $"noise-{i}", 153, System.Diagnostics.EventLogEntryType.Error),
                    $"noise-{i}", day.AddMinutes(i)))
                .ToList();
            rows.Add(NewHandlingRow("SRV-MATCH", day, legacyKey, "legacy answer", day.AddDays(2)));
            ctx.IssueHandlings.AddRange(rows);
            ctx.SaveChanges();
        }

        var watch = Stopwatch.StartNew();
        var result = new EfIssueHandlingStore(_fixture.NewContext).GetLatestNote(requestedKey, null);
        watch.Stop();

        Assert.NotNull(result);
        Assert.Equal("legacy answer", result.Value.Note);
        _output.WriteLine($"GetLatestNote visibleHostNameKeys=null scanned_note_rows={noiseRows + 1} elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}");
    }

    [Fact]
    public void HandlingBlobMigrator_混合大小寫legacy雙列按UpdatedAt合併且保留被選列原字()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-sql-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "migration.db");
        try
        {
            StorageBackend Seeded()
            {
                var backend = new StorageBackend(
                    new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" }, dir);
                var day = DateTime.Today;
                var oldKey = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);
                var newerKey = IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error);
                backend.Blob("issue_handling").Mutate<object?>(_ =>
                    (JsonSerializer.Serialize(new List<IssueHandling>
                    {
                        new() { HostName = "SRV-M", Date = day, IssueKey = newerKey,
                            Status = IssueHandlingStatuses.InProgress, UpdatedAt = day.AddHours(1) },
                        new() { HostName = "SRV-M", Date = day, IssueKey = oldKey,
                            Status = IssueHandlingStatuses.Observing, UpdatedAt = day.AddHours(2) }
                    }, LfJsonOptions.Pretty), null));
                backend.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));
                return new StorageBackend(
                    new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" }, dir);
            }

            var backend = Seeded();
            backend.HandlingMigrator.Run(CancellationToken.None);

            var row = Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-M", DateTime.Today));
            Assert.Equal(IssueHandlingStatuses.Observing, row.Status);
            Assert.Equal(IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error), row.IssueKey);
            Assert.Equal(1, backend.HandlingMigrator.State.IssueHandlingRows);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static IssueHandlingRow NewHandlingRow(string host, DateTime day, string issueKey, string note, DateTime updatedAt) => new()
    {
        HostName = host,
        HostNameKey = HostNameKey.Of(host),
        RecordDate = day,
        IssueKey = issueKey,
        Status = IssueHandlingStatuses.Resolved,
        Note = note,
        UpdatedAt = updatedAt
    };
}
