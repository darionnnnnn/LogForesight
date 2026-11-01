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

    /// <summary>可手動撥快的時鐘，驗證快取到期</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    [Fact]
    public void Resolve_查無_59秒內不重查_61秒後重查()
    {
        var clock = new ManualClock();
        var dns = new FakeDns(_ => Array.Empty<IPAddress>());
        var resolver = new PrtgAddressResolver(dns.Lookup, clock);

        Assert.Null(resolver.Resolve("gone.local"));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(resolver.Resolve("gone.local"));
        Assert.Single(dns.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(resolver.Resolve("gone.local"));
        Assert.Equal(2, dns.Calls.Count);
    }

    [Fact]
    public void Resolve_成功_5分鐘內不重查_之後重查()
    {
        var clock = new ManualClock();
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup, clock);

        Assert.Equal("10.5.5.5", resolver.Resolve("prtg.local"));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal("10.5.5.5", resolver.Resolve("prtg.local"));
        Assert.Single(dns.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("10.5.5.5", resolver.Resolve("prtg.local"));
        Assert.Equal(2, dns.Calls.Count);
    }

    [Fact]
    public void Resolve_逾時_不寫快取_下一次立即重查()
    {
        var clock = new ManualClock();
        var attempts = 0;
        var resolver = new PrtgAddressResolver(host =>
        {
            attempts++;
            return attempts == 1 ? null : new[] { IPAddress.Parse("10.7.7.7") };
        }, clock);

        Assert.Null(resolver.Resolve("slow.local"));
        Assert.Equal("10.7.7.7", resolver.Resolve("slow.local"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Resolve_逾時路徑經正式DNS包裝_不寫快取()
    {
        var clock = new FireOnDemandTimeProvider();
        var calls = 0;
        var resolver = new PrtgAddressResolver(async (host, ct) =>
        {
            calls++;
            clock.FireAll();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<IPAddress>();
        }, clock);

        Assert.Null(resolver.Resolve("slow.invalid"));
        Assert.Null(resolver.Resolve("slow.invalid"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Resolve_32執行緒並行解析同一組名稱_不擲例外且查詢次數有界()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(host => { lock (dns) return dns.Lookup(host); });
        var names = Enumerable.Range(0, 10).Select(i => $"host{i}.local").ToArray();
        // 先在主執行緒各查一次把快取填滿：之後 32 執行緒的並行讀取一次 DNS 都不該再打
        // （冷快取下同名同時未命中的次數取決於排程時機，在全量測試負載下不是穩定的上界）
        foreach (var n in names) Assert.Equal("10.5.5.5", resolver.Resolve(n));
        Assert.Equal(names.Length, dns.Calls.Count);

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            foreach (var n in names) Assert.Equal("10.5.5.5", resolver.Resolve(n));
        })).ToArray();
        Task.WaitAll(tasks);

        Assert.Equal(names.Length, dns.Calls.Count);
    }

    [Fact]
    public void Resolve_達條目上限_新名稱不進快取但仍回傳解析結果()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);
        for (var i = 0; i < PrtgAddressResolver.MaxEntries; i++)
            resolver.Resolve($"fill{i}.local");
        Assert.Equal(PrtgAddressResolver.MaxEntries, dns.Calls.Count);

        Assert.Equal("10.5.5.5", resolver.Resolve("extra.local"));
        Assert.Equal("10.5.5.5", resolver.Resolve("extra.local"));
        Assert.Equal(PrtgAddressResolver.MaxEntries + 2, dns.Calls.Count);

        // 既有項目不被清空
        Assert.Equal("10.5.5.5", resolver.Resolve("fill0.local"));
        Assert.Equal(PrtgAddressResolver.MaxEntries + 2, dns.Calls.Count);
    }

    [Fact]
    public void Resolve_達條目上限且既有條目過期_先清過期再收新名稱()
    {
        var clock = new ManualClock();
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup, clock);
        for (var i = 0; i < PrtgAddressResolver.MaxEntries; i++)
            resolver.Resolve($"fill{i}.local");

        clock.Advance(TimeSpan.FromMinutes(6));   // 全部超過正快取 5 分鐘
        resolver.Resolve("extra.local");
        var calls = dns.Calls.Count;
        Assert.Equal("10.5.5.5", resolver.Resolve("extra.local"));
        Assert.Equal(calls, dns.Calls.Count);     // 第二次命中快取，沒有再查 DNS
    }

    [Fact]
    public void Invalidate_發生在DNS查詢進行中_該次結果不寫回快取()
    {
        PrtgAddressResolver? resolver = null;
        var dns = new FakeDns(_ =>
        {
            resolver!.Invalidate();   // 使用者在這筆查詢卡住期間按了「同步結構與對應」
            return new[] { IPAddress.Parse("10.5.5.5") };
        });
        resolver = new PrtgAddressResolver(dns.Lookup);

        Assert.Equal("10.5.5.5", resolver.Resolve("late.local"));
        Assert.Equal("10.5.5.5", resolver.Resolve("late.local"));
        Assert.Equal(2, dns.Calls.Count);   // 第二次仍查 DNS：修好前的結果沒有被記住
    }

    [Fact]
    public void Invalidate_清空快取後重查()
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("10.5.5.5") });
        var resolver = new PrtgAddressResolver(dns.Lookup);

        resolver.Resolve("prtg.local");
        resolver.Invalidate();
        resolver.Resolve("prtg.local");

        Assert.Equal(2, dns.Calls.Count);
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
