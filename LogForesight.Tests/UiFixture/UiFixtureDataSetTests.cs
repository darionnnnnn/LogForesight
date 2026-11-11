using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// UI 驗收資料集的常態保護（<see cref="UiFixtureDataSet"/>）：矩陣的每一格都要在資料庫裡查得到。
/// 沒有這組測試，往後改到 schema 或模型時資料集會悄悄少一格，截圖才發現版面根本沒被檢查到。
///
/// 設定環境變數 <c>LF_UI_FIXTURE=&lt;目錄&gt;</c> 時，兩個變體會產生到 <c>&lt;目錄&gt;/ai</c> 與 <c>&lt;目錄&gt;/noai</c>
/// 並保留（不刪），供站台啟動截圖使用；沒設就用暫存目錄、測完清掉。
/// </summary>
public sealed class UiFixtureDataSetFixture : IDisposable
{
    public UiFixtureDataSet Data { get; }
    public UiFixtureDataSet? NotConfigured { get; }
    public string Root { get; }
    public bool Keep { get; }
    public TimeSpan Elapsed { get; }

    public UiFixtureDataSetFixture()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var keepRoot = Environment.GetEnvironmentVariable("LF_UI_FIXTURE");
        Keep = !string.IsNullOrWhiteSpace(keepRoot);
        Root = Keep ? keepRoot! : Path.Combine(Path.GetTempPath(), "lf-ui-fixture", Guid.NewGuid().ToString("N"));
        Data = UiFixtureDataSet.Generate(Keep ? Path.Combine(Root, "ai") : Root, UiFixtureVariant.AiConfigured);
        if (Keep) NotConfigured = UiFixtureDataSet.Generate(Path.Combine(Root, "noai"), UiFixtureVariant.AiNotConfigured);
        Elapsed = sw.Elapsed;
    }

    public void Dispose()
    {
        Data.Dispose();
        NotConfigured?.Dispose();
        if (Keep) return;
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料庫刪不掉不影響結果
        }
    }
}

public sealed class UiFixtureDataSetTests : IClassFixture<UiFixtureDataSetFixture>
{
    private readonly UiFixtureDataSetFixture _fx;
    private readonly UiFixtureChecks _c;
    private readonly ITestOutputHelper _output;

    public UiFixtureDataSetTests(UiFixtureDataSetFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
        _c = new UiFixtureChecks(fx.Data);
    }

    public static IEnumerable<object[]> CellNames() => UiFixtureChecks.Cells.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(CellNames))]
    public void 矩陣格子_資料庫查得到(string cell)
    {
        var check = UiFixtureChecks.Cells[cell];
        try
        {
            check(_c);
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            throw new Xunit.Sdk.XunitException($"矩陣格子「{cell}」查不到或形狀不對：{ex.Message}");
        }
    }

    [Fact]
    public void 日期範圍_最近一天是昨天且跨度不超過三十天()
    {
        using var ctx = _fx.Data.Backend.CreateContext();
        var dates = ctx.DailyRecords.Select(r => r.RecordDate).Distinct().ToList();
        Assert.Equal(DateTime.Today.AddDays(-1), dates.Max());
        Assert.True((dates.Max() - dates.Min()).TotalDays < 30, "紀錄跨度應小於 30 天");
        Assert.All(dates, d => Assert.True(d < DateTime.Today));
    }

    [Fact]
    public void 情境清單_每個風險日路徑都指向存在的紀錄且鍵不重複()
    {
        var scenarios = _fx.Data.Scenarios;
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Key).Distinct().Count());
        Assert.All(scenarios, s => Assert.StartsWith("/", s.Path));

        using var ctx = _fx.Data.Backend.CreateContext();
        foreach (var s in scenarios.Where(s => s.Path.StartsWith("/records/") && s.Path.Count(ch => ch == '/') == 3))
        {
            var parts = s.Path.Split('/');
            var hostId = long.Parse(parts[2]);
            var date = DateTime.Parse(parts[3]);
            Assert.True(ctx.DailyRecords.Any(r => r.HostId == hostId && r.RecordDate == date), $"情境「{s.Key}」指向不存在的主機日：{s.Path}");
        }

        var json = Path.Combine(_fx.Data.RootDir, "ui-fixture-scenarios.json");
        Assert.True(File.Exists(json), "應同時輸出情境清單 JSON 供截圖工具使用");
    }

    [Fact]
    public void 變體_兩份資料只差AI設定()
    {
        // 保留模式已經產生了兩份真的資料集；平常只產生一份，另一份用「複製資料庫＋切換變體」驗證
        // （變體差異只在 ApplyVariant 寫的設定，這樣不必把整份資料集再產生一次）
        var ai = _fx.Data;
        var noai = _fx.NotConfigured;
        string? copyRoot = null;
        StorageBackend? copyBackend = null;
        try
        {
            StorageBackend noaiBackend;
            if (noai != null)
            {
                noaiBackend = noai.Backend;
            }
            else
            {
                copyRoot = Path.Combine(_fx.Root, "variant-copy-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(copyRoot, "Db"));
                File.Copy(ai.DbPath, StorageBackend.DefaultSqlitePath(copyRoot));
                copyBackend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, copyRoot);
                UiFixtureDataSet.ApplyVariant(copyBackend, UiFixtureVariant.AiNotConfigured);
                noaiBackend = copyBackend;
            }

            var aiSettings = new SystemSettingsStore(ai.Backend.Blob("system_settings")).Get();
            var noaiSettings = new SystemSettingsStore(noaiBackend.Blob("system_settings")).Get();
            Assert.True(AiProviders.IsConfigured(aiSettings.AiProvider, aiSettings.AiBaseUrl, aiSettings.AiApiKeyEnc, aiSettings.AiAzureDeployment));
            Assert.False(AiProviders.IsConfigured(noaiSettings.AiProvider, noaiSettings.AiBaseUrl, noaiSettings.AiApiKeyEnc, noaiSettings.AiAzureDeployment));

            // 兩份的 AI 執行窗口一致（都清空，資料才一樣）
            Assert.Empty(new ScheduleOptionsStore(ai.Backend.Blob("schedule_options")).Get().AiWindows);
            Assert.Empty(new ScheduleOptionsStore(noaiBackend.Blob("schedule_options")).Get().AiWindows);

            using var a = ai.Backend.CreateContext();
            using var b = noaiBackend.CreateContext();
            Assert.Equal(a.DailyRecords.Count(), b.DailyRecords.Count());
            Assert.Equal(a.TopIssues.Count(), b.TopIssues.Count());
            if (noai != null) Assert.Equal(ai.Scenarios.Select(s => s.Path), noai.Scenarios.Select(s => s.Path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (copyRoot != null)
            {
                try { Directory.Delete(copyRoot, recursive: true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void 資料庫路徑_站台以DataRoot啟動讀得到()
    {
        _output.WriteLine($"資料集產生耗時：{_fx.Elapsed.TotalSeconds:F1} 秒（保留模式含兩個變體）");
        Assert.Equal(Path.Combine(_fx.Data.RootDir, "Db", "logforesight.db"), _fx.Data.DbPath);
        Assert.True(File.Exists(_fx.Data.DbPath));
        if (_fx.Keep)
        {
            Assert.True(File.Exists(Path.Combine(_fx.Root, "ai", "Db", "logforesight.db")), "LF_UI_FIXTURE 應保留 ai 變體");
            Assert.True(File.Exists(Path.Combine(_fx.Root, "noai", "Db", "logforesight.db")), "LF_UI_FIXTURE 應保留 noai 變體");
        }
    }
}
