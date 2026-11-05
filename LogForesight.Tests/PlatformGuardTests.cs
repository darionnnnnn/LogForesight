using System.Text.Json;
using LogForesight.Web.Extensions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 非 Windows 平台守門（批次 E-1）：Windows 專屬能力（Event Log、權限監控、AD 驗證）在非 Windows
/// 要退化成明確、不擲例外的回應。以注入的 <c>isWindows=false</c> 在 Windows 上也能驗證非 Windows 分支。
/// </summary>
public class PlatformGuardTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 非Windows_EventLogService讀取回ReadFailed_原因固定且不擲例外()
    {
        var service = new EventLogService(() => false);

        var result = service.ScanRange(new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), "System");

        Assert.True(result.ReadFailed);
        Assert.True(result.PlatformUnsupported);
        Assert.False(result.Complete);
        Assert.Empty(result.Entries);
        Assert.Equal("此平台不支援 Windows Event Log 讀取", EventLogService.UnsupportedPlatformReason);
    }

    [Fact]
    public async Task 非Windows_多來源掃描整趟標示平台不支援_且每個來源都ReadFailed()
    {
        var service = new EventLogService(() => false);

        var result = await service.ScanRangeFromAllAsync(new DateTime(2026, 1, 1), new DateTime(2026, 1, 2));

        Assert.True(result.PlatformUnsupported);
        Assert.Empty(result.Entries);
        Assert.All(result.BySource.Values, r => Assert.True(r.ReadFailed));
    }

    [Fact]
    public void 非Windows_PermissionMonitor_Check回空結果並標示平台不支援()
    {
        var store = new PermissionSnapshotStore(_fx.Blob("permission_snapshot"));
        var monitor = new PermissionMonitorService(new PermissionSettings(), store, () => false);

        var result = monitor.Check();

        Assert.True(result.PlatformUnsupported);
        Assert.Empty(result.Alerts);
        Assert.Empty(result.Details);
        // 不該建立基準快照：非 Windows 根本沒有可比對的資料
        Assert.Null(store.Load());
        Assert.Equal("此平台不支援本機權限監控", PermissionMonitorService.UnsupportedPlatformMessage);
    }

    [Fact]
    public void 非Windows_驗證方式選擇走fallback()
    {
        var fallback = new FakeAuthenticationProvider();

        var selected = ServiceCollectionExtensions.SelectAuthenticationProvider(new FakeSystemSettingsStore(), fallback, () => false);

        Assert.Same(fallback, selected);
    }

    [WindowsFact]
    public void Windows_驗證方式選擇包成DynamicAuthenticationProvider()
    {
        var fallback = new FakeAuthenticationProvider();

        var selected = ServiceCollectionExtensions.SelectAuthenticationProvider(new FakeSystemSettingsStore(), fallback, () => true);

        Assert.Equal("DynamicAuthenticationProvider", selected.GetType().Name);
    }

    [Fact]
    public void WindowsFact在非Windows時Skip非空_Windows時為null()
    {
        Assert.False(string.IsNullOrWhiteSpace(WindowsPlatform.SkipReason(isWindows: false)));
        Assert.Null(WindowsPlatform.SkipReason(isWindows: true));
    }

    [Fact]
    public void 測試的HttpListener不得先Stop再Dispose()
    {
        // Unix 的 HttpListener 是 .NET 自己實作：Stop 後 Dispose 會再綁一次 port，
        // 該 port 已被平行測試拿走就擲 Address already in use。Windows 走 http.sys 測不出來，一律用 Close。
        var root = Path.Combine(FindRepoRoot(), "LogForesight.Tests");
        var stopCall = "listener." + "Stop()";
        var sep = Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Where(f => File.ReadAllText(f) is var text && text.Contains("HttpListener", StringComparison.Ordinal)
                && text.Contains(stopCall, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "下列測試檔對 HttpListener 呼叫 Stop，請改用 Close：" + string.Join("、", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到專案根目錄");
    }

    [Fact]
    public void LogEntryType整數值與既有序列化契約一致()
    {
        Assert.Equal(1, (int)LogEntryType.Error);
        Assert.Equal(2, (int)LogEntryType.Warning);
        Assert.Equal(4, (int)LogEntryType.Information);
        Assert.Equal(8, (int)LogEntryType.SuccessAudit);
        Assert.Equal(16, (int)LogEntryType.FailureAudit);
    }

    [Theory]
    [InlineData(LogEntryType.Error, "Error")]
    [InlineData(LogEntryType.Warning, "Warning")]
    [InlineData(LogEntryType.Information, "Information")]
    [InlineData(LogEntryType.SuccessAudit, "SuccessAudit")]
    [InlineData(LogEntryType.FailureAudit, "FailureAudit")]
    public void LogIssueSignature_EntryType以名稱序列化且讀得回(LogEntryType type, string name)
    {
        var signature = new LogIssueSignature { LogName = "System", Source = "Test", EventId = 7, EntryType = type };

        var json = JsonSerializer.Serialize(signature);
        Assert.Contains($"\"EntryType\":\"{name}\"", json);

        var back = JsonSerializer.Deserialize<LogIssueSignature>(json);
        Assert.NotNull(back);
        Assert.Equal(type, back!.EntryType);
    }

    [Fact]
    public void LogIssueSignature_Critical的無名稱值0以數字序列化且讀得回()
    {
        var signature = new LogIssueSignature { LogName = "System", Source = "Test", EventId = 41, EntryType = (LogEntryType)0 };
        var json = JsonSerializer.Serialize(signature);
        Assert.Contains("\"EntryType\":0", json);
        Assert.Equal((LogEntryType)0, JsonSerializer.Deserialize<LogIssueSignature>(json)!.EntryType);
    }

    [Fact]
    public void LogIssueSignature_舊資料以名稱存下的EntryType仍讀得回()
    {
        var back = JsonSerializer.Deserialize<LogIssueSignature>("{\"LogName\":\"System\",\"EntryType\":\"Warning\"}");

        Assert.NotNull(back);
        Assert.Equal(LogEntryType.Warning, back!.EntryType);
    }
}
