using System.Net;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Tests.Authorization;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 錯誤輸出邊界（第 79 輪批次 B1、B2、B4）：給使用者的回應一律是信封，且不含非領域例外的原文。
/// 以「帶獨特標記字串的例外」驗證：回應裡出現標記＝原文外流。取代以變數名比對 .Message 的文字守門
/// （那種守門比不到 state.LastError 這類寫法，遷移閘門因此曾把例外原文回給使用者）。
/// </summary>
public class ErrorOutputBoundaryTests
{
    private const string Marker = "ZZ-SECRET-TABLE lf_internal C:\\data\\path";

    private static async Task<(bool Handled, int Status, string Body)> RunHandlerAsync(Exception ex, string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        var handled = await new ApiExceptionHandler().TryHandleAsync(http, ex, CancellationToken.None);
        http.Response.Body.Position = 0;
        return (handled, http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    public static IEnumerable<object[]> FrameworkExceptions() => new[]
    {
        new object[] { new InvalidOperationException(Marker) },
        new object[] { new KeyNotFoundException(Marker) },
        new object[] { new IOException(Marker) },
        new object[] { new Microsoft.EntityFrameworkCore.DbUpdateException(Marker) }
    };

    [Theory]
    [MemberData(nameof(FrameworkExceptions))]
    public async Task 中介層階段的框架例外_API回信封且不含原文(Exception ex)
    {
        var (handled, status, body) = await RunHandlerAsync(ex, "/api/records");
        Assert.True(handled);
        Assert.Equal(500, status);
        Assert.DoesNotContain("ZZ-SECRET", body);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ApiErrorCodes.ServerError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 領域例外_中介層階段同樣回原訊息與對應狀態()
    {
        var (handled, status, body) = await RunHandlerAsync(DomainException.Validation("欄位格式不正確"), "/api/records");
        Assert.True(handled);
        Assert.Equal(400, status);
        Assert.Contains("欄位格式不正確", body);
    }

    [Fact]
    public async Task 頁面請求不由API處理器接手_交給錯誤頁()
    {
        var (handled, _, _) = await RunHandlerAsync(new InvalidOperationException(Marker), "/records");
        Assert.False(handled);
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, 413, ApiErrorCodes.PayloadTooLarge)]
    [InlineData(StatusCodes.Status400BadRequest, 400, ApiErrorCodes.ValidationFailed)]
    public async Task 請求格式錯誤不落到500(int kestrelStatus, int expectedStatus, string expectedCode)
    {
        var (_, status, body) = await RunHandlerAsync(new BadHttpRequestException(Marker, kestrelStatus), "/api/imports");
        Assert.Equal(expectedStatus, status);
        Assert.Contains(expectedCode, body);
        Assert.DoesNotContain("ZZ-SECRET", body);
    }

    [Theory]
    [InlineData(403, ApiErrorCodes.Forbidden)]
    [InlineData(404, ApiErrorCodes.NotFound)]
    [InlineData(405, ApiErrorCodes.MethodNotAllowed)]
    [InlineData(413, ApiErrorCodes.PayloadTooLarge)]
    [InlineData(415, ApiErrorCodes.UnsupportedMediaType)]
    public void 空本文的錯誤狀態有對應的信封(int status, string code)
    {
        Assert.Equal(code, ApiStatusEnvelope.For(status)?.Code);
        Assert.Equal(status, ApiErrorCodes.StatusOf(code));
    }

    [Fact]
    public void 遷移失敗存進狀態的說明不含例外原文()
    {
        var text = MigrationFailureText.For(new InvalidOperationException(Marker));
        Assert.DoesNotContain("ZZ-SECRET", text);
        Assert.Contains(nameof(InvalidOperationException), text);
    }
}

/// <summary>整站組裝：狀態碼信封真的接在管線上（路由找不到、方法不允許）。</summary>
[Collection(AuthorizationHttpCollection.Name)]
public class ErrorOutputBoundaryHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    public ErrorOutputBoundaryHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    [Fact]
    public async Task 不存在的API路由回404信封()
    {
        var (status, body) = await _fx.GetAsync(_fx.Admin, "/api/zz-route-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains(ApiErrorCodes.NotFound, body);
    }

    [Fact]
    public async Task 方法不允許回405信封()
    {
        var (status, body, _) = await _fx.SendAsync(_fx.Admin, HttpMethod.Delete, "/api/runs/summary");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, status);
        Assert.Contains(ApiErrorCodes.MethodNotAllowed, body);
    }
}

/// <summary>模型繫結失敗的信封訊息是中文（第 79 輪批次 B8）</summary>
[Collection(AuthorizationHttpCollection.Name)]
public class ModelBindingMessageHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    public ModelBindingMessageHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    [Fact]
    public async Task 查詢參數型別錯誤回中文訊息()
    {
        var (status, body) = await _fx.GetAsync(_fx.Admin, "/api/runs/summary?days=abc");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(ApiErrorCodes.ValidationFailed, body);
        Assert.DoesNotContain("is not valid", body);
        Assert.DoesNotContain("The value", body);
    }
}

/// <summary>匿名登入端點的請求大小上限（第 79 輪批次 C8）</summary>
[Collection(AuthorizationHttpCollection.Name)]
public class LoginRequestSizeHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    public LoginRequestSizeHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    [Fact]
    public async Task 超過上限的登入請求在讀取本文前被拒_正常大小照常處理()
    {
        var oversized = new StringContent("{\"account\":\"" + new string('a', 8192) + "\",\"password\":\"x\"}",
            System.Text.Encoding.UTF8, "application/json");
        // TestServer 沒有實作請求本文大小的功能介面，上限只在 Kestrel／IIS 上由框架執行；
        // 這裡驗兩件事：端點宣告了上限，以及超大請求不會被當成正常登入處理（DTO 的長度上限同樣擋下）
        var limit = typeof(LogForesight.Web.Controllers.Api.AuthController).GetMethod("Login")!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>().Single();
        Assert.NotNull(limit);
        var (status, _, _) = await _fx.SendAsync(_fx.Anonymous, HttpMethod.Post, "/api/auth/login", content: oversized);
        Assert.True(status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, status.ToString());

        var (normalStatus, _, _) = await _fx.SendAsync(_fx.Anonymous, HttpMethod.Post, "/api/auth/login",
            body: new { account = "nobody", password = "wrong" });
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, normalStatus);
    }
}

/// <summary>目錄驗證並行上限與安全標頭（第 79 輪批次 G15、G16）</summary>
[Collection(AuthorizationHttpCollection.Name)]
public class LoginConcurrencyAndHeadersHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    public LoginConcurrencyAndHeadersHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    [Fact]
    public void 登入端點不套用端點層並行限流_上限只在目錄驗證這一步()
    {
        // 端點層限流在讀出帳號前生效，會把 AD 掛掉時唯一入口的救援帳號一起擋掉
        var attributes = typeof(LogForesight.Web.Controllers.Api.AuthController).GetMethod("Login")!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), inherit: false);
        Assert.Empty(attributes);
    }

    [Fact]
    public async Task 並行上限內的登入照常處理()
    {
        var tasks = Enumerable.Range(0, LogForesight.Web.Auth.DirectoryVerifyGate.Concurrent)
            .Select(_ => _fx.SendAsync(_fx.Anonymous, HttpMethod.Post, "/api/auth/login", body: new { account = "nobody", password = "x" }));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.Status));
    }
}

/// <summary>目錄驗證並行閘門的單元行為（名額、佇列、歸還）</summary>
public class DirectoryVerifyGateTests
{
    [Fact]
    public async Task 名額與佇列滿了回null_歸還後可再進入()
    {
        var gate = new LogForesight.Web.Auth.DirectoryVerifyGate();
        var held = Enumerable.Range(0, LogForesight.Web.Auth.DirectoryVerifyGate.Concurrent).Select(_ => gate.TryEnterAsync().GetAwaiter().GetResult()).ToList();
        Assert.All(held, h => Assert.NotNull(h));

        var queued = Enumerable.Range(0, LogForesight.Web.Auth.DirectoryVerifyGate.Queued)
            .Select(_ => gate.TryEnterAsync()).ToList();
        var settle = DateTime.UtcNow.AddSeconds(30);
        while (gate.Waiting < LogForesight.Web.Auth.DirectoryVerifyGate.Queued && DateTime.UtcNow < settle) await Task.Delay(20);
        Assert.Equal(LogForesight.Web.Auth.DirectoryVerifyGate.Queued, gate.Waiting);
        Assert.Null(await gate.TryEnterAsync());   // 名額與佇列都滿

        foreach (var h in held) h!.Dispose();
        // 排隊者依序拿到名額：每拿到一個就歸還，下一個才輪得到
        foreach (var task in queued)
        {
            var permit = await task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(permit);
            permit!.Dispose();
        }
        using var again = await gate.TryEnterAsync();
        Assert.NotNull(again);
    }
}
