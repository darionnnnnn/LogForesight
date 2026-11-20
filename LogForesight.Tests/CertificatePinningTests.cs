using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LogForesight.Core;
using LogForesight.Tests.TestDoubles;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 憑證指紋信任（第 79 輪批次 E1～E3）：以測試內自建、自簽憑證的 HTTPS 伺服器實際連線，
/// 驗證指紋相符連得上、不符連不上且訊息指明指紋不符、沒填且沒開放行時自簽憑證連不上，
/// 以及探測（測試連線）回報的指紋等於該憑證的雜湊。
/// </summary>
public class CertificatePinningTests
{
    // ── 指紋格式（邊界兩側） ──

    [Theory]
    [InlineData("ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abcdef01 23456789 abcdef01 23456789 abcdef01 23456789 abcdef01 23456789")]
    public void 指紋格式_常見貼上寫法都正規化成大寫無分隔(string input)
    {
        Assert.True(ServerCertificateTrust.TryNormalize(input, out var pin));
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", pin);
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345678")]    // 63 字
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567890")]  // 65 字
    [InlineData("GBCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]   // 非十六進位
    [InlineData("AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01")]          // SHA-1 長度
    public void 指紋格式_長度或字元不對就拒收(string input)
        => Assert.False(ServerCertificateTrust.TryNormalize(input, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 指紋格式_空白等於沒設(string? input)
    {
        Assert.True(ServerCertificateTrust.TryNormalize(input, out var pin));
        Assert.Null(pin);
    }

    // ── 多個信任指紋（多台各自自簽的 Sentinel） ──

    private static readonly string PinA = new('A', 64);
    private static readonly string PinB = new('b', 64);

    [Fact]
    public void 指紋清單_逗號分號換行混用都能分隔_正規化為大寫逗號分隔且去重()
    {
        var input = PinA + ";" + PinB + "\r\n" + PinA.ToLowerInvariant() + ",,\n  ";
        Assert.True(ServerCertificateTrust.TryNormalize(input, out var pins));
        Assert.Equal(PinA + "," + PinB.ToUpperInvariant(), pins);

        // 單一指紋仍可含冒號與空白
        var colon = string.Join(":", Enumerable.Range(0, 32).Select(_ => "AB"));
        Assert.True(ServerCertificateTrust.TryNormalize(colon + "\n" + PinB, out var two));
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)) + "," + PinB.ToUpperInvariant(), two);
    }

    [Fact]
    public void 指紋清單_好的一項加壞的一項_整欄被拒()
    {
        Assert.False(ServerCertificateTrust.TryNormalize(PinA + "," + "ZZ", out var pins));
        Assert.Null(pins);
        Assert.False(ServerCertificateTrust.TryNormalize(PinA + "\n" + new string('A', 63), out _));
    }

    [Fact]
    public async Task Sentinel_信任清單內兩個指紋各自的伺服器都連得上_清單外的被拒()
    {
        await using var one = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        await using var two = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        await using var third = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        var pins = one.Sha256 + "\n" + two.Sha256;

        foreach (var server in new[] { one, two })
        {
            await using var ok = new SentinelClient(SentinelServer(server), new NetiqOptions
            {
                RetryCount = 0, TimeoutSeconds = 10, TrustedCertificateSha256 = pins
            });
            await ok.TestConnectionAsync();
            Assert.Equal(server.Sha256, ok.PresentedCertificateSha256);
        }

        await using var rejected = new SentinelClient(SentinelServer(third), new NetiqOptions
        {
            RetryCount = 0, TimeoutSeconds = 10, TrustedCertificateSha256 = pins, AllowInvalidCertificates = true
        });
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() => rejected.TestConnectionAsync());
        Assert.Equal(SentinelClientException.CertificateRejectedCode, ex.SafeCode);
        Assert.Contains(third.Sha256, ex.Message);
    }

    [Fact]
    public void Sentinel_信任清單含壞指紋_建立用戶端就失敗()
        => Assert.Throws<SentinelClientException>(() => new SentinelClient(
            new SentinelServer { Name = "S", BaseUrl = "https://localhost:1", Username = "u", Password = "p" },
            new NetiqOptions { TrustedCertificateSha256 = PinA + ",not-a-hash" }));

    // ── 實際 TLS 連線：PRTG ──

    [Fact]
    public async Task PRTG_指紋相符_自簽憑證連得上且回報出示的指紋()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        using var client = Prtg(server, pin: server.Sha256, ignore: false);

        await client.TestConnectionAsync();

        Assert.Equal(server.Sha256, client.PresentedCertificateSha256);
    }

    [Fact]
    public async Task PRTG_指紋相符時_冒號小寫寫法也採用()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        var colon = string.Join(":", Enumerable.Range(0, 32).Select(i => server.Sha256.Substring(i * 2, 2))).ToLowerInvariant();
        using var client = Prtg(server, pin: colon, ignore: false);
        await client.TestConnectionAsync();
    }

    [Fact]
    public async Task PRTG_指紋不符_連不上且訊息指明指紋不符與出示的指紋()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        // 指紋設了就不看「忽略憑證錯誤」：開著也不能放行不符的憑證
        using var client = Prtg(server, pin: new string('A', 64), ignore: true);

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.TestConnectionAsync());

        Assert.Contains("指紋", ex.Message);
        Assert.Contains("不符", ex.Message);
        Assert.Contains(server.Sha256, ex.Message);
        Assert.Equal(PrtgFailureKind.Other, ex.Kind);
    }

    [Fact]
    public async Task PRTG_沒填指紋且沒開放行_自簽憑證連不上()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        using var client = Prtg(server, pin: null, ignore: false);

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.TestConnectionAsync());

        Assert.Contains("不被信任", ex.Message);
        Assert.Equal(server.Sha256, client.PresentedCertificateSha256);
    }

    [Fact]
    public async Task PRTG_沒填指紋但開了放行_維持舊行為連得上()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        using var client = Prtg(server, pin: null, ignore: true);
        await client.TestConnectionAsync();
    }

    [Fact]
    public void PRTG_指紋格式不對_建立用戶端就失敗()
        => Assert.Throws<PrtgClientException>(() =>
            new PrtgClient("https://prtg.local", "tok", 30, false, null, PrtgAuthModes.Token, "", "", "",
                trustedCertificateSha256: "not-a-hash"));

    [Fact]
    public async Task PRTG_改設定後下一個用戶端就採用新指紋()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"treesize\":0,\"sensors\":[]}");
        var settings = new LogForesight.Core.Models.SystemSettings
        {
            PrtgUrl = server.Url, PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("tok"), PrtgTrustedCertificateSha256 = new string('B', 64)
        };
        using (var before = LogForesight.Core.Service.PrtgClientFactory.Create(settings))
            await Assert.ThrowsAsync<PrtgClientException>(() => before.TestConnectionAsync());

        settings.PrtgTrustedCertificateSha256 = server.Sha256;
        using var after = LogForesight.Core.Service.PrtgClientFactory.Create(settings);
        await after.TestConnectionAsync();
    }

    // ── 實際 TLS 連線：Sentinel ──

    [Fact]
    public async Task Sentinel_指紋相符_自簽憑證連得上且回報出示的指紋()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        await using var client = new SentinelClient(SentinelServer(server), new NetiqOptions
        {
            RetryCount = 0, TimeoutSeconds = 10, TrustedCertificateSha256 = server.Sha256
        });

        await client.TestConnectionAsync();

        Assert.Equal(server.Sha256, client.PresentedCertificateSha256);
    }

    [Fact]
    public async Task Sentinel_指紋不符_連不上_訊息指明且不重試()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        await using var client = new SentinelClient(SentinelServer(server), new NetiqOptions
        {
            RetryCount = 3, TimeoutSeconds = 10, TrustedCertificateSha256 = new string('C', 64), AllowInvalidCertificates = true
        });

        var ex = await Assert.ThrowsAsync<SentinelClientException>(() => client.TestConnectionAsync());

        Assert.Equal(SentinelClientException.CertificateRejectedCode, ex.SafeCode);
        Assert.Contains("指紋", ex.Message);
        Assert.Contains(server.Sha256, ex.Message);
        Assert.Equal(1, server.Handshakes);
    }

    [Fact]
    public async Task Sentinel_沒填指紋且沒開放行_自簽憑證連不上()
    {
        await using var server = await SelfSignedHttpsServer.StartAsync("{\"Token\":\"tok\"}");
        await using var client = new SentinelClient(SentinelServer(server), new NetiqOptions { RetryCount = 0, TimeoutSeconds = 10 });

        var ex = await Assert.ThrowsAsync<SentinelClientException>(() => client.TestConnectionAsync());
        Assert.Equal(SentinelClientException.CertificateRejectedCode, ex.SafeCode);
    }

    // ── 健康頁原因（E2） ──

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(true, "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(false, null, false)]
    public void 健康原因_開了放行且沒填指紋才列出(bool ignore, string? pin, bool expected)
        => Assert.Equal(expected, ServerCertificateTrust.IsUnpinnedBypass(pin, ignore));

    [Fact]
    public void 健康原因_PRTG未啟用不列_Sentinel照列_填了指紋後消失()
    {
        var prtgOff = new LogForesight.Core.Models.SystemSettings { PrtgEnabled = false, PrtgIgnoreSslErrors = true };
        var netiq = new NetiqOptions { AllowInvalidCertificates = true };
        Assert.Equal(new[] { "Sentinel" }, HealthService.UnpinnedCertificateBypass(prtgOff, netiq, hasActiveSentinel: true));

        var prtgOn = new LogForesight.Core.Models.SystemSettings { PrtgEnabled = true, PrtgIgnoreSslErrors = true };
        Assert.Equal(new[] { "PRTG", "Sentinel" }, HealthService.UnpinnedCertificateBypass(prtgOn, netiq, hasActiveSentinel: true));

        prtgOn.PrtgTrustedCertificateSha256 = new string('D', 64);
        netiq.TrustedCertificateSha256 = new string('E', 64);
        Assert.Empty(HealthService.UnpinnedCertificateBypass(prtgOn, netiq, hasActiveSentinel: true));
    }

    [Fact]
    public void 健康原因_沒有啟用中的Sentinel時不列_有啟用才列()
    {
        var prtgOff = new LogForesight.Core.Models.SystemSettings { PrtgEnabled = false };
        var netiq = new NetiqOptions { AllowInvalidCertificates = true };

        Assert.Empty(HealthService.UnpinnedCertificateBypass(prtgOff, netiq, hasActiveSentinel: false));
        Assert.Equal(new[] { "Sentinel" }, HealthService.UnpinnedCertificateBypass(prtgOff, netiq, hasActiveSentinel: true));
    }

    [Fact]
    public void 健康原因訊息_依來源指出設定位置()
    {
        var both = HealthAttention.Evaluate(
            new HealthAttentionInput { UnpinnedCertificateBypass = new[] { "PRTG", "Sentinel" } }, DateTime.Now)
            .Single(r => r.Key == HealthAttentionKeys.CertificateTrust);
        Assert.Contains("PRTG 維護的擷取參數", both.Text);
        Assert.Contains("NetIQ 維護的連線與節流參數", both.Text);

        var prtgOnly = HealthAttention.Evaluate(
            new HealthAttentionInput { UnpinnedCertificateBypass = new[] { "PRTG" } }, DateTime.Now)
            .Single(r => r.Key == HealthAttentionKeys.CertificateTrust);
        Assert.Contains("PRTG 維護的擷取參數", prtgOnly.Text);
        Assert.DoesNotContain("NetIQ", prtgOnly.Text);
    }

    [Fact]
    public void 健康整體狀態_含憑證原因_沒有時不含()
    {
        var with = HealthAttention.Evaluate(new HealthAttentionInput { UnpinnedCertificateBypass = new[] { "PRTG" } }, DateTime.Now);
        Assert.Contains(with, r => r.Key == HealthAttentionKeys.CertificateTrust && r.Text.Contains("PRTG", StringComparison.Ordinal));

        var without = HealthAttention.Evaluate(new HealthAttentionInput(), DateTime.Now);
        Assert.DoesNotContain(without, r => r.Key == HealthAttentionKeys.CertificateTrust);
    }

    // ── 替身 ──

    private static PrtgClient Prtg(SelfSignedHttpsServer server, string? pin, bool ignore) =>
        new(server.Url, "tok", 10, ignore, null, PrtgAuthModes.Token, "", "", "",
            budget: new PrtgRequestBudget(new AcceleratedPrtgClock()), trustedCertificateSha256: pin);

    private static SentinelServer SentinelServer(SelfSignedHttpsServer server) => new()
    {
        Name = "SENTINEL-PIN", BaseUrl = server.Url, Username = "svc", Password = "pw"
    };

    /// <summary>
    /// 最小 HTTPS 伺服器：自簽憑證（CN=localhost），每個連線讀完一個請求後回固定 JSON 並關閉。
    /// 只為了讓用戶端走完真正的 TLS 交握與憑證驗證，不模擬任何 PRTG／Sentinel 協定細節。
    /// </summary>
    private sealed class SelfSignedHttpsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly string _body;
        private readonly CancellationTokenSource _cts = new();
        private int _handshakes;

        private SelfSignedHttpsServer(X509Certificate2 certificate, string body)
        {
            _certificate = certificate;
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            Sha256 = ServerCertificateTrust.Sha256Of(certificate);
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }
        public string Sha256 { get; }
        /// <summary>收到的 TLS 交握嘗試次數（不論成功與否）</summary>
        public int Handshakes => Volatile.Read(ref _handshakes);

        public static Task<SelfSignedHttpsServer> StartAsync(string body)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // macOS 的 SslStream 伺服器端要可匯出的私鑰：經 PFX 來回一次
            var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "t"), "t");
            return Task.FromResult(new SelfSignedHttpsServer(certificate, body));
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    await using var ssl = new SslStream(tcp.GetStream(), false);
                    Interlocked.Increment(ref _handshakes);
                    await ssl.AuthenticateAsServerAsync(_certificate);
                    var buffer = new byte[16 * 1024];
                    var received = new StringBuilder();
                    while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var n = await ssl.ReadAsync(buffer);
                        if (n == 0) return;
                        received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var text = received.ToString();
                    var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                    var lengthLine = text[..headerEnd].Split("\r\n")
                        .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var expected = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    var have = Encoding.ASCII.GetByteCount(text[headerEnd..]);
                    while (have < expected)
                    {
                        var n = await ssl.ReadAsync(buffer);
                        if (n == 0) break;
                        have += n;
                    }
                    var payload = Encoding.UTF8.GetBytes(_body);
                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(head);
                    await ssl.WriteAsync(payload);
                    await ssl.FlushAsync();
                }
                catch (Exception)
                {
                    // 用戶端拒絕憑證時交握失敗，這是被測行為本身
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _certificate.Dispose();
            _cts.Dispose();
        }
    }
}
