using System.Net;
using System.Text;
using LogForesight.Core;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 失敗依種類給中文說明（批次 E-2：E5-b、E5-c、E5-d）。
/// 說明文字只有 <see cref="PrtgFailureText"/> 一份對照；底層原文附在後面。
/// </summary>
public class PrtgFailureTextTests
{
    private const string Url = "https://prtg.example.com";
    private const string Token = "prtg-secret-token-12345";
    private const string Path = "/api/table.json?content=sensors";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => onSend(request, ct);
    }

    private static PrtgClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> onSend) =>
        new(Url, Token, 30, false, new Handler(onSend), PrtgAuthModes.Token, "", "", "",
            new LogForesight.Core.Service.PrtgRequestBudget(new LogForesight.Tests.TestDoubles.AcceleratedPrtgClock()));

    [Fact]
    public async Task E5b_逾時_訊息是逾時說明且英文原文附在後面()
    {
        using var client = Client((_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.", new TimeoutException()));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.Equal(PrtgFailureKind.Timeout, ex.Kind);
        Assert.Contains("逾時", ex.Message);
        Assert.Contains(PrtgFailureText.Timeout, ex.Message);
        Assert.False(ex.Message.StartsWith("The request was canceled", StringComparison.OrdinalIgnoreCase));
        Assert.True(ex.Message.IndexOf("逾時", StringComparison.Ordinal) < ex.Message.IndexOf("canceled", StringComparison.Ordinal), "中文說明要在原文前面");
    }

    [Fact]
    public async Task E5b_連不上_訊息是連不上說明()
    {
        using var client = Client((_, _) => throw new HttpRequestException("No connection could be made", new System.Net.Sockets.SocketException()));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.Equal(PrtgFailureKind.Transport, ex.Kind);
        Assert.Contains(PrtgFailureText.Unreachable, ex.Message);
        Assert.DoesNotContain("逾時", ex.Message);
    }

    [Fact]
    public async Task E5b_認證失敗_訊息是認證說明()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.Contains("認證失敗", ex.Message);
        Assert.Equal(PrtgFailureText.CredentialToken, ex.Message);
    }

    [Fact]
    public async Task E5b_伺服器放棄_空白OK頁_訊息是放棄說明()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<HTML><BODY><B class=\"no-content\">OK</B></BODY></HTML>", Encoding.UTF8, "text/html")
        }));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.Equal(PrtgFailureKind.EmptyPage, ex.Kind);
        Assert.Equal(PrtgFailureText.ServerGaveUp, ex.Message);
    }

    private static HttpResponseMessage BadRequest(string body) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/xml")
    };

    [Fact]
    public async Task E5c_400加XML錯誤_訊息帶出PRTG的錯誤文字()
    {
        using var client = Client((_, _) => Task.FromResult(BadRequest(
            "<?xml version=\"1.0\"?><prtg><version>23.1</version><error>Sorry, the column &quot;foo&quot; is unknown.</error></prtg>")));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.Equal(PrtgFailureKind.HttpStatus, ex.Kind);
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Sorry, the column \"foo\" is unknown.", ex.Message);
        Assert.StartsWith(PrtgFailureText.BadRequest, ex.Message);
    }

    [Fact]
    public async Task E5c_400的錯誤文字5000字_被截斷()
    {
        var longText = new string('錯', 5000);
        using var client = Client((_, _) => Task.FromResult(BadRequest($"<prtg><error>{longText}</error></prtg>")));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.DoesNotContain(longText, ex.Message);
        Assert.True(ex.Message.Length < PrtgFailureText.MaxOriginalLength + 100, $"訊息長度 {ex.Message.Length}");
        Assert.EndsWith("…）", ex.Message);
    }

    [Fact]
    public async Task E5c_400的錯誤文字含憑證_已遮蔽()
    {
        using var client = Client((_, _) => Task.FromResult(BadRequest($"<prtg><error>bad apitoken={Token}</error></prtg>")));
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync(Path));
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task E5d_使用者按停止_仍是取消不說成逾時()
    {
        using var cts = new CancellationTokenSource();
        using var client = Client(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJsonAsync(Path, cts.Token));
    }

    [Fact]
    public void 初始化失敗_一般例外只給固定說明_用戶端例外沿用其中文訊息()
    {
        Assert.Equal(PrtgFailureText.InitFailed, PrtgFailureText.ForInit(new InvalidOperationException(@"C:\secret\path.cfg 不存在")));
        Assert.Equal("PRTG 未設定連線位址。", PrtgFailureText.ForInit(new PrtgClientException("PRTG 未設定連線位址。")));
    }
}
