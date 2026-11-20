using System.Net;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 領域訊號以型別或列舉判斷、不比對通用例外或文字（第 79 輪批次 B5）。
/// 每條都有「該判成 A」與「不該判成 A」兩側。
/// </summary>
public class DomainSignalTests
{
    private sealed class FixedBodyHandler(int bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) });
    }

    private static async Task<HttpRequestException> CaptureAsync(HttpClient client)
    {
        try { await client.GetStringAsync("http://probe.invalid/"); }
        catch (HttpRequestException ex) { return ex; }
        throw new Xunit.Sdk.XunitException("預期擲出 HttpRequestException");
    }

    [Fact]
    public async Task 回應超過緩衝上限判為超量_以框架錯誤分類而非訊息()
    {
        using var client = new HttpClient(new FixedBodyHandler(2048)) { MaxResponseContentBufferSize = 1024 };
        Assert.True(PrtgClient.IsOverCapacity(await CaptureAsync(client)));
    }

    [Fact]
    public void 訊息含上限位元組數但分類不是超量_不判為超量()
    {
        // 舊判準只看訊息是否含 268435456；同樣的訊息配上連線錯誤分類不能算超量
        var ex = new HttpRequestException(HttpRequestError.ConnectionError,
            $"buffer size: {PrtgClient.MaxResponseBytes}", inner: null);
        Assert.False(PrtgClient.IsOverCapacity(ex));
    }

    [Fact]
    public void 本機寫入的通用例外不是永久失敗_該裝置留在待補()
    {
        Assert.False(PrtgFetchService.IsPermanentFailure(new InvalidOperationException("A second operation was started on this context")));
    }

    [Fact]
    public void 回應被本機保護規則拒收是永久失敗()
    {
        Assert.True(PrtgFetchService.IsPermanentFailure(new PrtgDeviceResponseRejectedException("單一裝置補抓超過上限")));
    }

    [Theory]
    [InlineData(new long[0], "NoData")]
    [InlineData(new long[] { 101, 5 }, "IncludesSensors")]
    [InlineData(new long[] { 5, 5 }, "DeviceOnly")]
    [InlineData(new long[] { 999 }, "ForeignObjids")]
    public void 探測判定回列舉_顯示文字只由列舉轉出(long[] returned, string expected)
    {
        var verdict = PrtgProbeRunner.JudgeDeviceMessagesScope(5, new HashSet<long> { 101, 102 }, returned);
        Assert.Equal(expected, verdict.ToString());
        Assert.False(string.IsNullOrWhiteSpace(PrtgProbeRunner.DescribeDeviceMessagesScope(verdict)));
    }
}
