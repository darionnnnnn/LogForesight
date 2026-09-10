using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgTablePager 的分頁收斂行為：三道停止條件、頁數上限、去重與 sortby。
/// 這些情境在正常環境不會出現，但實機的 PRTG 在 start 超出範圍時會夾到最後一頁，
/// 只靠「未滿一頁」判定會讓夜間批次無聲卡死——替身必須真的模擬回滿頁。
/// </summary>
public class PrtgTablePagerTests
{
    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<string, string> Responder { get; set; } = _ => "{}";
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responder(url), Encoding.UTF8, "application/json")
            });
        }
    }

    private static string Page(string content, IEnumerable<long> objids, int? treesize = null)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        if (treesize.HasValue) sb.Append($"\"treesize\":{treesize.Value},");
        sb.Append($"\"{content}\":[");
        sb.Append(string.Join(",", objids.Select(id => $"{{\"objid\":{id}}}")));
        sb.Append("]}");
        return sb.ToString();
    }

    private static int StartOf(string url) => int.Parse(url.Split("start=")[1].Split('&')[0]);

    /// <summary>把回應交給分頁器，收集被寫出的 objid。</summary>
    private static async Task<(PrtgPagerResult Result, List<long> Written, StubHandler Handler, TestConsole Console)> RunAsync(
        Func<string, string> responder, int pageSize, int maxPagesWhenUnknown = PrtgTablePager.DefaultMaxPages,
        Func<JsonElement, string?>? rowKey = null, string content = "devices")
    {
        var handler = new StubHandler { Responder = responder };
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var console = new TestConsole();
        var written = new List<long>();

        var result = await PrtgTablePager.FetchAsync<long>(
            client, console, content, "objid", null,
            mapper: el => el.GetProperty("objid").GetInt64(),
            onBatch: batch => written.AddRange(batch),
            ct: CancellationToken.None,
            pageSize: pageSize,
            maxPagesWhenTreeSizeUnknown: maxPagesWhenUnknown,
            rowKey: rowKey);

        return (result, written, handler, console);
    }

    [Fact]
    public async Task 每頁回滿且objid固定時靠無新objid收斂()
    {
        // 忽略 start 的代理：每頁都回同一批資料。只靠「未滿一頁」會永遠跑不完。
        var (result, written, handler, _) = await RunAsync(
            _ => Page("devices", new long[] { 1, 2, 3, 4, 5 }), pageSize: 5);

        Assert.Equal(2, result.Pages);
        Assert.Equal(5, result.Mapped);
        Assert.Equal(5, result.DuplicateRows);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, written);
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task 超出範圍夾到最後一頁時收斂且不重複寫入()
    {
        // 實機行為：總筆數剛好是頁大小整數倍，start 超出範圍時回最後一頁而非空頁
        var all = Enumerable.Range(1, 10).Select(i => (long)i).ToArray();
        var (result, written, _, _) = await RunAsync(url =>
        {
            var start = StartOf(url);
            var slice = start < all.Length ? all.Skip(start).Take(5) : all.Skip(5).Take(5);
            return Page("devices", slice, treesize: 10);
        }, pageSize: 5);

        Assert.Equal(3, result.Pages);
        Assert.Equal(10, result.Mapped);
        Assert.Equal(5, result.DuplicateRows);
        Assert.Equal(all, written);
    }

    [Fact]
    public async Task 未滿一頁時照常收斂()
    {
        var (result, written, _, _) = await RunAsync(url =>
            StartOf(url) == 0
                ? Page("devices", new long[] { 1, 2, 3, 4, 5 }, treesize: 7)
                : Page("devices", new long[] { 6, 7 }, treesize: 7), pageSize: 5);

        Assert.Equal(2, result.Pages);
        Assert.Equal(7, result.Mapped);
        Assert.Equal(0, result.DuplicateRows);
        Assert.Equal(7, written.Count);
    }

    [Fact]
    public async Task 空頁時收斂()
    {
        var (result, _, _, _) = await RunAsync(url =>
            StartOf(url) == 0
                ? Page("devices", new long[] { 1, 2, 3, 4, 5 })
                : Page("devices", Array.Empty<long>()), pageSize: 5);

        Assert.Equal(2, result.Pages);
        Assert.Equal(5, result.Mapped);
    }

    [Fact]
    public async Task treesize缺失且每頁都是新objid時翻到上限擲例外()
    {
        var next = 0L;
        var ex = await Assert.ThrowsAsync<PrtgPagingNotConvergedException>(() => RunAsync(
            _ => Page("devices", Enumerable.Range(0, 5).Select(_ => ++next)),
            pageSize: 5, maxPagesWhenUnknown: 3));

        Assert.Equal("devices", ex.Content);
        // 上限 3 頁，但允許多讀一頁確認結尾（第 4 頁仍是滿頁全新才判定未收斂）
        Assert.Equal(4, ex.Pages);
        Assert.Equal(20, ex.ReadRows);
        Assert.Contains("分頁未收斂", ex.Message);
    }

    [Fact]
    public async Task treesize已知時上限跟著放大而不是被縮小()
    {
        // treesize 推算出的上限（52 頁）遠大於未知時的上限（3 頁）：
        // treesize 在帶 filter 的查詢下語意沒有保證，只能放大上限、不能縮小，
        // 否則合法的長同步會被誤判成未收斂。
        var next = 0L;
        var ex = await Assert.ThrowsAsync<PrtgPagingNotConvergedException>(() => RunAsync(
            _ => Page("devices", Enumerable.Range(0, 5).Select(_ => ++next), treesize: 250),
            pageSize: 5, maxPagesWhenUnknown: 3));

        Assert.Equal(53, ex.Pages); // ceil(250/5) + 2，再加確認結尾的那一頁
    }

    /// <summary>
    /// 總筆數剛好是「上限 × 頁大小」時，最後一頁是滿頁、下一頁才是空頁。
    /// 在最後一頁就判定未收斂，會把合法的收尾當成故障。
    /// </summary>
    [Fact]
    public async Task 總筆數剛好等於上限乘頁大小時仍能正常收斂()
    {
        var next = 0L;
        var (result, written, _, _) = await RunAsync(url =>
        {
            var start = StartOf(url);
            if (start >= 15) return Page("devices", Array.Empty<long>());
            return Page("devices", Enumerable.Range(0, 5).Select(_ => ++next));
        }, pageSize: 5, maxPagesWhenUnknown: 3);

        Assert.Equal(4, result.Pages);   // 3 頁資料 + 1 頁空頁
        Assert.Equal(15, written.Count);
    }

    /// <summary>
    /// messages 的 objid 是「發出訊息的 sensor」，同一天同一顆 sensor 會有很多列。
    /// 按 objid 去重會讓每顆 sensor 只留下第一筆狀態變更，而且整頁都是已見過的 objid 時
    /// 還會被當成結尾提早停止——兩個後果都是靜默的。
    /// </summary>
    [Fact]
    public async Task objid非唯一時用列鍵判重不會丟資料也不會提早停止()
    {
        // 三頁都是同一顆 sensor(9001) 的不同訊息（時間不同），第四頁才收尾。
        // 拿 objid 當鍵會在第二頁就判定「沒有新的」而停止，整段狀態變更只剩 5 筆。
        var seq = 0;
        string MessagePage(int count)
        {
            var rows = string.Join(",", Enumerable.Range(0, count)
                .Select(_ => $"{{\"objid\":9001,\"datetime\":\"2026-09-10 10:{seq++:00}\"}}"));
            return "{\"messages\":[" + rows + "]}";
        }

        var (result, written, _, _) = await RunAsync(
            url => StartOf(url) >= 15 ? MessagePage(2) : MessagePage(5),
            pageSize: 5, content: "messages",
            rowKey: el => el.GetProperty("objid").GetRawText() + "|" + el.GetProperty("datetime").GetString());

        Assert.Equal(4, result.Pages);
        Assert.Equal(0, result.DuplicateRows);
        Assert.Equal(17, written.Count);   // 5+5+5+2，一列都沒被丟掉
    }

    /// <summary>
    /// objid 非唯一時仍要收斂：夾到末頁時整頁內容與上一頁完全相同。
    /// </summary>
    [Fact]
    public async Task objid非唯一時夾到末頁仍會收斂()
    {
        // 夾到末頁＝整頁的列鍵都見過了
        string PageFor(int start)
        {
            var baseTime = start == 0 ? 0 : 10;
            var rows = string.Join(",", Enumerable.Range(0, 5)
                .Select(i => $"{{\"objid\":9001,\"datetime\":\"2026-09-10 10:{baseTime + i:00}\"}}"));
            return "{\"messages\":[" + rows + "]}";
        }

        var (result, written, handler, _) = await RunAsync(
            url => PageFor(StartOf(url) == 0 ? 0 : 5),
            pageSize: 5, content: "messages",
            rowKey: el => el.GetProperty("objid").GetRawText() + "|" + el.GetProperty("datetime").GetString());

        Assert.Equal(3, result.Pages);
        Assert.Equal(3, handler.RequestedUrls.Count);
        Assert.Equal(10, written.Count);
        Assert.Equal(5, result.DuplicateRows);
    }

    [Fact]
    public async Task 未收斂時已讀到的資料仍會寫出()
    {
        var written = new List<long>();
        var handler = new StubHandler();
        var next = 0L;
        handler.Responder = _ => Page("devices", Enumerable.Range(0, 5).Select(_ => ++next));

        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);

        await Assert.ThrowsAsync<PrtgPagingNotConvergedException>(() => PrtgTablePager.FetchAsync<long>(
            client, new TestConsole(), "devices", "objid", null,
            mapper: el => el.GetProperty("objid").GetInt64(),
            onBatch: batch => written.AddRange(batch),
            ct: CancellationToken.None,
            pageSize: 5,
            maxPagesWhenTreeSizeUnknown: 3));

        // 讀到的四頁 20 筆都已交給 onBatch（寫入是冪等 upsert，留著比丟掉好）
        Assert.Equal(20, written.Count);
    }

    [Fact]
    public async Task 查詢一律帶sortby與extraQuery()
    {
        var handler = new StubHandler { Responder = _ => Page("messages", Array.Empty<long>()) };
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);

        await PrtgTablePager.FetchAsync<long>(
            client, new TestConsole(), "messages", "objid", "id=0&filter_drel=7days",
            mapper: el => el.GetProperty("objid").GetInt64(),
            onBatch: _ => { },
            ct: CancellationToken.None,
            pageSize: 5);

        var url = Assert.Single(handler.RequestedUrls);
        Assert.Contains("sortby=objid", url);
        Assert.Contains("filter_drel=7days", url);
        Assert.Contains("content=messages", url);
    }

    [Fact]
    public async Task objid缺失的列不會被當成重複而被丟棄()
    {
        // messages 之外的 content 理論上都有 objid，但外部系統沒有保證。
        // 判不了重的列一律當新的，寧可重複寫入（upsert 冪等）也不能靜默丟資料。
        var handler = new StubHandler
        {
            Responder = url => StartOf(url) == 0
                ? "{\"devices\":[{\"device\":\"A\"},{\"device\":\"B\"},{\"objid\":1},{\"objid\":2},{\"objid\":3}]}"
                : "{\"devices\":[]}"
        };
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var written = new List<string>();

        var result = await PrtgTablePager.FetchAsync<string>(
            client, new TestConsole(), "devices", "objid", null,
            mapper: el => el.TryGetProperty("device", out var d) ? d.GetString() : "id",
            onBatch: batch => written.AddRange(batch),
            ct: CancellationToken.None,
            pageSize: 5);

        Assert.Equal(0, result.DuplicateRows);
        Assert.Equal(5, written.Count);
    }

    [Fact]
    public async Task 夾到末頁時進度分子不會超過分母()
    {
        var all = Enumerable.Range(1, 10).Select(i => (long)i).ToArray();
        var handler = new StubHandler
        {
            Responder = url =>
            {
                var start = StartOf(url);
                var slice = start < all.Length ? all.Skip(start).Take(5) : all.Skip(5).Take(5);
                return Page("devices", slice, treesize: 10);
            }
        };
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var reported = new List<(int Done, int Total)>();

        await PrtgTablePager.FetchAsync<long>(
            client, new TestConsole(), "devices", "objid", null,
            mapper: el => el.GetProperty("objid").GetInt64(),
            onBatch: _ => { },
            ct: CancellationToken.None,
            phase: "prtg-sync-devices",
            progress: (_, done, total) => reported.Add((done, total)),
            pageSize: 5);

        // 第三頁是重讀的末頁：分子若不夾住分母，畫面會出現 15 / 10
        Assert.All(reported, r => Assert.True(r.Total == 0 || r.Done <= r.Total,
            $"進度分子 {r.Done} 不應超過分母 {r.Total}"));
    }

    [Fact]
    public async Task 取消訊號會穿透分頁迴圈()
    {
        var handler = new StubHandler { Responder = _ => Page("devices", new long[] { 1, 2, 3, 4, 5 }) };
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrtgTablePager.FetchAsync<long>(
            client, new TestConsole(), "devices", "objid", null,
            mapper: el => el.GetProperty("objid").GetInt64(),
            onBatch: _ => { },
            ct: cts.Token,
            pageSize: 5));
    }
}
