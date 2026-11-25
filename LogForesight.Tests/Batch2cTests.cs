using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 2c：2-9／2-10／2-11 是結果不變的最佳化（輸出與改動前的 <see cref="Batch2cGoldens"/> 逐字相同），
/// 2-13 是 PRTG 補齊估算計入請求配額的缺陷修正。
/// </summary>
public class Batch2cTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly PrtgScopeEstimateController _scope;

    public Batch2cTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-b2c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }, _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _scope = PrtgControllerFactory.ScopeEstimate(new StubSettingsService(), _backend, _hosts);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── A1 執行總表 ──────────────────────────────────────────────

    [Fact]
    public void A1_執行總表與展開某日_含各排序與分頁_與改動前逐字相同()
    {
        var output = Batch2cScenarios.RunMonitor();

        Assert.Equal(Batch2cGoldens.RunMonitorLength, output.Length);
        Assert.Equal(Batch2cGoldens.RunMonitorSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant());
    }

    // ── A2 處理狀態推導 ───────────────────────────────────────────

    [Fact]
    public void A2_處理狀態推導_同鍵取第一筆_案件優先_沿用誤判期_與改動前相同()
    {
        Assert.Equal(Batch2cGoldens.Occurrence, Batch2cScenarios.Occurrence());
    }

    // ── A3 交辦摘要 ───────────────────────────────────────────────

    [Fact]
    public async Task A3_交辦摘要_三張單內容與改動前相同_主機清單只讀一次()
    {
        var (body, hostReads) = await Batch2cScenarios.Digest();

        Assert.Equal(Batch2cGoldens.Digest, body);
        Assert.Equal(1, hostReads);
    }

    // ── A4 估算 ───────────────────────────────────────────────────

    private void SeedSensors(int sensorCount, int concurrency)
    {
        var store = _backend.PrtgStore();
        var host = _hosts.Upsert(new WebHost { HostName = "srv-est", IpAddress = "10.70.0.1", Active = true });
        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            new() { MapDate = DateTime.Today, DeviceObjid = 7001, HostId = host.HostId, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(Enumerable.Range(1, sensorCount)
            .Select(i => new PrtgSensorRow { Objid = 7100 + i, DeviceObjid = 7001, Name = "ping" + i, SensorType = "Ping" })
            .ToList(), DateTime.Now);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string> { "Ping" };
            s.PrtgFetchConcurrency = concurrency;
        });
    }

    [Theory]
    [InlineData(1, 1, 1)]    // 1 次查詢：併發下界 1 分鐘，配額下界 0
    [InlineData(1, 3, 1)]
    [InlineData(5, 1, 1)]    // 5 次以內配額下界為 0，與改動前相同
    [InlineData(2, 2, 1)]
    public void A4_查詢數五以下_分鐘數與改動前相同(int sensors, int days, int expectedMinutes)
    {
        SeedSensors(sensors, concurrency: 2);

        var estimate = _scope.EstimatePrtgValues(days).Data!;

        Assert.True(estimate.Queries <= 5);
        Assert.Equal(expectedMinutes, estimate.Minutes);
        Assert.Equal(0, PrtgFullBackfillPlan.QuotaLowerBoundSeconds(estimate.Queries));
    }

    [Fact]
    public void A4_查詢數兩千且併發二_分鐘數取配額下界()
    {
        SeedSensors(1, concurrency: 2);

        var estimate = _scope.EstimatePrtgValues(days: 2000).Data!;

        Assert.Equal(2000, estimate.Queries);
        // 併發算法只有 25 分鐘；配額「任一 60 秒最多 5 次」的下界才是 399 分鐘
        Assert.Equal((int)Math.Ceiling(PrtgFullBackfillPlan.QuotaLowerBoundSeconds(2000) / 60.0), estimate.Minutes);
        Assert.Equal(399, estimate.Minutes);
    }

    [Fact]
    public void A4_查詢數零_分鐘數為零()
    {
        SeedSensors(1, concurrency: 2);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgSensorTypeWhitelist = new List<string>());

        var estimate = _scope.EstimatePrtgValues(days: 30).Data!;

        Assert.Equal(0, estimate.Queries);
        Assert.Equal(0, estimate.Minutes);
    }

    [Fact]
    public void A4_配額算式只存在於PrtgFullBackfillPlan一處()
    {
        var root = FindRepoRoot();
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}LogForesight.Tests{Path.DirectorySeparatorChar}") &&
                        // 併行開發的 git worktree 放在 .claude/ 底下（不進版控），那裡是整份原始碼的副本，不算第二處
                        !f.StartsWith(Path.Combine(root, ".claude") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("/ 5) * 60"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "PrtgFullBackfillPlan.cs" }, hits);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到專案根目錄");
    }

    private sealed class StubSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Get() => new();
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
