using System.Collections;
using System.Reflection;
using LogForesight.Core.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 每趟分析都 new 一個 <see cref="StorageBackend"/>：EF Core 必須重用同一份內部服務提供者，
/// 否則每份都快取在行程裡、隨排程次數成長。
///
/// EF Core 8 對 <c>CoreEventId.ManyServiceProvidersCreatedWarning</c>（超過 20 份）預設是**擲例外**，
/// 所以真的發生時那一趟會明確失敗、不會靜默累積；這裡在測試階段就守住「不會走到那一步」：
/// 連續建 25 個 backend，提供者快取不跟著長，也不擲例外。
/// 若日後在選項加上每次都是新實例的服務（例如 <c>UseMemoryCache(new MemoryCache(...))</c>、
/// <c>UseLoggerFactory(new ...)</c>），本測試會在第 21 個 backend 擲例外而紅。
/// </summary>
public sealed class StorageBackendEfProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-efprov-" + Guid.NewGuid().ToString("N"));

    public StorageBackendEfProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>EF 內部的提供者快取筆數（內部 API，讀不到時回 null，只略過這一項斷言）</summary>
    private static int? EfProviderCacheCount()
    {
        var type = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly
            .GetType("Microsoft.EntityFrameworkCore.Internal.ServiceProviderCache");
        var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var field = type?.GetField("_configurations", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(instance) is ICollection collection ? collection.Count : null;
    }

    private StorageBackend New(int i) =>
        new(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, i + ".db")}" }, _dir);

    [Fact]
    public void 連續建25個backend_EF內部服務提供者不隨之成長()
    {
        _ = New(0); // 讓這個選項形狀的提供者先存在
        var before = EfProviderCacheCount();

        for (var i = 1; i <= 25; i++) _ = New(i); // 每次都是新提供者的話，第 21 個會擲 ManyServiceProvidersCreatedWarning

        var after = EfProviderCacheCount();
        if (before is { } b && after is { } a)
            Assert.True(a - b < 5, $"EF 內部服務提供者隨 backend 成長：{b} → {a}");
    }
}
