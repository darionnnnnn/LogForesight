using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Analysis;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// D1：掃描併發度參數全鏈接線測試。
/// 驗證 Concurrency 參數從 NetiqScanRequest 經 AdminController、
/// NetiqDiscoveryService（StartScan / ScanAsync / DiscoverAsync）一路傳至 INetiqDirectoryClient。
/// </summary>
public class NetiqScanConcurrencyChainTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeImportLogStore _importLogs = new();
    private readonly RecordingAuditService _audit = new();

    private NetiqDiscoveryService CreateService(FakeClient client, params SentinelServer[] servers) =>
        new(new FakeNetiqServerCatalog(servers), client, _hosts, _hostGroups, _sentinels,
            _importLogs, new FakeCurrentUser(), _audit);

    private static SentinelServer Discoverable(string name) =>
        new() { Name = name, BaseUrl = "https://x", Username = "u", Password = "p" };

    private static async Task<NetiqScanJobDto> WaitForJobAsync(
        NetiqDiscoveryService svc, string jobId, Func<NetiqScanJobDto, bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var status = svc.GetScanStatus(jobId);
            if (condition(status)) return status;
            await Task.Yield();
        }
        return svc.GetScanStatus(jobId);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄（無 LogForesight.sln）");
    }

    // ── 1. 全鏈到底：ScanAsync 傳入 3 → FakeClient 收到 3 ────────────────────

    [Fact]
    public async Task 全鏈到底_ScanAsync傳入併發度3_假Client收到併發度3()
    {
        var client = new FakeClient();
        var svc = CreateService(client, Discoverable("S1"));

        await svc.ScanAsync("S1", "10.1.2", default, concurrency: 3);

        Assert.Equal(3, client.LastConcurrency);
    }

    // ── 2. 全鏈到底：不傳併發度 → FakeClient 收到預設值 1 ─────────────────────

    [Fact]
    public async Task 全鏈到底_ScanAsync不傳併發度_假Client收到預設併發度1()
    {
        var client = new FakeClient();
        var svc = CreateService(client, Discoverable("S1"));

        await svc.ScanAsync("S1", "10.1.2", default);

        Assert.Equal(1, client.LastConcurrency);
    }

    // ── 3. 模型驗證走真實路徑（Validator.TryValidateObject）───────────────────

    [Fact]
    public void 模型驗證_Concurrency為5_驗證失敗且訊息含併發查詢數()
    {
        var request = new NetiqScanRequest
        {
            Server = "S1",
            SubnetPrefix = "192.168.0",
            Concurrency = 5
        };

        var results = new List<ValidationResult>();
        var context = new ValidationContext(request);
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.ErrorMessage != null && r.ErrorMessage.Contains("併發查詢數"));
    }

    [Fact]
    public void 模型驗證_Concurrency為3_驗證通過()
    {
        var request = new NetiqScanRequest
        {
            Server = "S1",
            SubnetPrefix = "192.168.0",
            Concurrency = 3
        };

        var results = new List<ValidationResult>();
        var context = new ValidationContext(request);
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(NetiqScanRequest.Concurrency)));
    }

    [Fact]
    public void 模型驗證_Concurrency為null_驗證通過()
    {
        var request = new NetiqScanRequest
        {
            Server = "S1",
            SubnetPrefix = "192.168.0",
            Concurrency = null
        };

        var results = new List<ValidationResult>();
        var context = new ValidationContext(request);
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(NetiqScanRequest.Concurrency)));
    }

    // ── 4. Controller 轉手點：原始檔字串比對 ───────────────────────────────────

    [Fact]
    public void Controller轉手點_原始檔字串比對_AdminController含requestConcurrency與預設值1()
    {
        var repoRoot = FindRepoRoot();
        var controllerPath = Path.Combine(repoRoot, "LogForesight.Web", "Controllers", "Api", "AdminController.cs");
        var content = File.ReadAllText(controllerPath);

        Assert.Contains("request.Concurrency", content);
        Assert.Contains("request.Concurrency ?? 1", content);
    }

    // ── 補充：StartScan 背景工作全鏈驗證 ───────────────────────────────────────

    [Fact]
    public async Task 全鏈到底_StartScan背景工作傳入併發度2_假Client收到併發度2()
    {
        var client = new FakeClient();
        var svc = CreateService(client, Discoverable("S1"));

        var jobId = svc.StartScan("S1", "10.1.2", concurrency: 2);
        var finalStatus = await WaitForJobAsync(svc, jobId, j => j.Status != "running");

        Assert.Equal("completed", finalStatus.Status);
        Assert.Equal(2, client.LastConcurrency);
    }
}
