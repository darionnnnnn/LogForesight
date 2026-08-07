using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 連線與節流參數維護（§13，回饋第九輪）：重點在「離線示範資料不得上正式環境」——
/// 這是 <see cref="ServiceCollectionExtensions.UseStubNetiqClient"/>（DI 選型層）之外的第二道
/// 保險，擋在寫入端：Production 嘗試開啟直接拒絕，即使 DB 殘留 true 也強制關閉。
/// </summary>
public class NetiqOptionsServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private NetiqOptionsService Create(string environmentName) =>
        new(new NetiqOptionsStore(_fx.Blob("netiq_options")),
            FakeCurrentUser.WithCapabilities(),
            new RecordingAuditService(),
            new FakeWebHostEnvironment(environmentName));

    /// <summary>合格的基準請求（各欄位都在 Range 內），測試只覆寫要驗的那一項</summary>
    private static UpdateNetiqOptionsRequest Baseline() => new()
    {
        QueryDelayMs = 0,
        PageSize = 500,
        MaxResultsPerJob = 100_000,
        TimeoutSeconds = 120,
        RetryCount = 3,
        BackfillDays = 1,
        MaxParallelServers = 2
    };

    [Fact]
    public void 預設為真實連線()
    {
        // §13：出廠預設 false＝真連線（取代原本 Development 預設假資料的顛倒方向）
        Assert.False(Create("Development").Get().UseOfflineDemoData);
    }

    [Fact]
    public void 非Production_可開啟離線示範資料()
    {
        var service = Create("Development");
        var request = Baseline();
        request.UseOfflineDemoData = true;

        Assert.True(service.Update(request).UseOfflineDemoData);
    }

    [Fact]
    public void Production_開啟離線示範資料被拒絕()
    {
        var service = Create("Production");
        var request = Baseline();
        request.UseOfflineDemoData = true;

        var ex = Assert.Throws<DomainException>(() => service.Update(request));
        Assert.Contains("離線示範資料", ex.Message);
        Assert.False(service.Get().UseOfflineDemoData);   // 沒有落地
    }

    [Fact]
    public void Production_關閉時正常儲存其餘參數()
    {
        // 只擋「開啟」這個動作，其餘參數維護在正式環境照常可用
        var service = Create("Production");
        var request = Baseline();
        request.BackfillDays = 5;

        var saved = service.Update(request);
        Assert.Equal(5, saved.BackfillDays);
        Assert.False(saved.UseOfflineDemoData);
    }

    /// <summary>
    /// docs/FEEDBACK-12-PLAN.md §二：上限從 8 收斂到 3 之後，既有環境若曾存過 4~8 的值
    /// （DataAnnotations 的 [Range] 只在 MVC 模型繫結時生效，不會擋這裡直接呼叫 Update），
    /// Get() 要把它夾回 3，否則維護頁顯示 8、瀏覽器 max=3 驗證會擋住整張表單存不了檔。
    /// </summary>
    [Fact]
    public void 既有存值超過上限時讀取自動夾住()
    {
        var service = Create("Development");
        var request = Baseline();
        request.MaxParallelServers = 8;
        service.Update(request);

        Assert.Equal(NetiqOptions.MaxParallelServersLimit, service.Get().MaxParallelServers);
    }

    [Fact]
    public void 存值在上限內時讀取不受影響()
    {
        var service = Create("Development");
        var request = Baseline();
        request.MaxParallelServers = 2;
        service.Update(request);

        Assert.Equal(2, service.Get().MaxParallelServers);
    }
}

/// <summary>測試用的環境替身：只有 EnvironmentName 有意義（IsProduction()/IsDevelopment() 靠它判定）</summary>
internal sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public FakeWebHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "LogForesight.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
