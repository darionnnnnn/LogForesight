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
}
