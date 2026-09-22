using System.Net;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 位址解析器（<see cref="PrtgAddressResolver"/>）的行為：
/// 合法 IP 走純語法正規化不查 DNS、主機名稱才查 DNS、失敗回 null、同一名稱只查一次。
/// </summary>
public class PrtgAddressResolverTests
{
    /// <summary>記錄呼叫次數與收到的參數的假 DNS。</summary>
    private sealed class FakeDns
    {
        private readonly Func<string, IPAddress[]> _behavior;

        public FakeDns(Func<string, IPAddress[]> behavior) => _behavior = behavior;

        public List<string> Calls { get; } = new();

        public IPAddress[] Lookup(string host)
        {
            Calls.Add(host);
            return _behavior(host);
        }
    }

    [Fact]
    public void Resolve_合法IP帶port_直接回傳且不查DNS()
    {
        var dns = new FakeDns(_ => Array.Empty<IPAddress>());
        var resolver = new PrtgAddressResolver(dns.Lookup);

        var result = resolver.Resolve("10.1.2.3:8080");

        Assert.Equal("10.1.2.3", result);
        Assert.Empty(dns.Calls);
    }

    [Fact]
    public void Resolve_主機名稱_以DNS解析出的IPv4回傳()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        var result = resolver.Resolve("prtg.local");

        Assert.Equal("10.5.5.5", result);
    }

    [Fact]
    public void Resolve_主機名稱帶port_查詢時不含port()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        var result = resolver.Resolve("prtg.local:8080");

        Assert.Equal("10.5.5.5", result);
        Assert.Equal(new[] { "prtg.local" }, dns.Calls);
    }

    [Fact]
    public void Resolve_只解析出IPv6_回null()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("fe80::1") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Null(resolver.Resolve("prtg.local"));
    }

    [Fact]
    public void Resolve_DNS擲例外_回null不擲出()
    {
        var dns = new FakeDns(_ => throw new InvalidOperationException("boom"));
        var resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Null(resolver.Resolve("prtg.local"));
    }

    [Fact]
    public void Resolve_同一名稱查兩次_成功結果只解析一次()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        var first = resolver.Resolve("prtg.local");
        var second = resolver.Resolve("prtg.local");

        Assert.Equal("10.5.5.5", first);
        Assert.Equal("10.5.5.5", second);
        Assert.Single(dns.Calls);
    }

    [Fact]
    public void Resolve_同一名稱查兩次_失敗結果也只解析一次()
    {
        // 失敗結果不快取的話，解析不到的位址每次都要再付一次 2 秒逾時。
        var dns = new FakeDns(_ => Array.Empty<IPAddress>());
        var resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Null(resolver.Resolve("unknown.local"));
        Assert.Null(resolver.Resolve("unknown.local"));
        Assert.Single(dns.Calls);
    }

    [Fact]
    public void Resolve_空白與null_回null且不查DNS()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve("   "));
        Assert.Empty(dns.Calls);
    }

    [Fact]
    public void Resolve_壞掉的IPv4佔位值_回null且完全不查DNS()
    {
        var dns = new FakeDns(_ => throw new InvalidOperationException("不該被呼叫"));
        var resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Null(resolver.Resolve("10.2xx.x.x"));
        Assert.Null(resolver.Resolve("10.2.3.256"));
        Assert.Null(resolver.Resolve("10.2.3.4 (old)"));
        Assert.Empty(dns.Calls);
    }

    [Fact]
    public void Resolve_內部憑證主機名帶scheme與port_DNS收到純主機名()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.9.9.9") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        var result = resolver.Resolve("https://netiq.corp.local:8443/");

        Assert.Equal("10.9.9.9", result);
        Assert.Equal(new[] { "netiq.corp.local" }, dns.Calls);
    }

    /// <summary>
    /// 正式解析路徑（逾時保護）：替身 DNS 模擬「解析器一直不回」，只有被取消才結束；
    /// 手動時鐘在 DNS 被呼叫時讓逾時計時器到期。驗證逾時計時器是 1 秒、到期即取消查詢、
    /// 不擲例外、回 null，且不必真的等待。
    /// 不走真實 DNS 與真實計時器：全套負載下執行緒池飢餓會讓逾時回呼晚到而偶發紅燈。
    /// </summary>
    [Fact]
    public void Resolve_真DNS解析保留網域_回null且在逾時內返回()
    {
        var clock = new FireOnDemandTimeProvider();
        var lookups = new List<string>();
        var resolver = new PrtgAddressResolver(async (host, ct) =>
        {
            lookups.Add(host);
            clock.FireAll();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<IPAddress>();
        }, clock);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = resolver.Resolve("nonexistent-host.invalid");

        sw.Stop();
        Assert.Null(result);
        Assert.Equal(new[] { "nonexistent-host.invalid" }, lookups);
        Assert.Equal(new[] { PrtgAddressResolver.DnsTimeout }, clock.DueTimes);
        Assert.True(clock.DueTimes.Single() < TimeSpan.FromSeconds(3), $"逾時 {clock.DueTimes.Single()}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"耗時 {sw.Elapsed}");
    }

    /// <summary>手動時鐘：記下每個計時器的到期時間與回呼，<see cref="FireAll"/> 時一次觸發，其餘時間永不到期。</summary>
    private sealed class FireOnDemandTimeProvider : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = new();

        public List<TimeSpan> DueTimes { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                _timers.Add((callback, state));
                DueTimes.Add(dueTime);
            }
            return new NeverTimer();
        }

        public void FireAll()
        {
            List<(TimerCallback Callback, object? State)> due;
            lock (_timers) due = _timers.ToList();
            foreach (var (callback, state) in due) callback(state);
        }

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
