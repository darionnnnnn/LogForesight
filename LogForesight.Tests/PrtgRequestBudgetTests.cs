using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 53 輪 A1.2 驗收測試：PRTG 正式入口共用請求預算（單程序階段）。
/// 涵蓋：
/// 1. historicdata 60秒滾動上限 5 次、邊界精確（59.999秒仍不得送、60秒才能送）、跨 client 共用不重置。
/// 2. table 1秒滾動上限 2 次、第三次等 1 秒、不套用 5/min 限制。
/// 3. 多個 history 等配額時，table 仍可取得名額；且實際 HTTP 在途永不超過 4。
/// 4. 取消排隊不發 HTTP 且不漏 permit；已發失敗/例外/body讀取失敗/認證交換後釋放在途名額且請求計數精確。
/// 5. 端點分類大小寫、前綴斜線、query 變更與未知端點支援（精確比對檔名，不誤把 status 當 table）。
/// 6. 兩個正式建立入口（PrtgClientFactory 與 SystemSettingsService.TestPrtgAsync）接上共享 budget。
/// 7. 4 在途佔滿時解除後，在途等待與新增請求不得突發突破滑動窗口上限。
/// </summary>
public class PrtgRequestBudgetTests
{
    private const string BaseUrl = "https://prtg.example.com";
    private const string Token = "secret-token-test";

    [Fact]
    public async Task HistoricData_同budget兩個client交錯5次可送_第6次在59點999秒仍不得送_60秒邊界才能送_其他client建立或釋放不重置()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        var handler = new BudgetTestHandler(clock);
        using var clientA = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);
        using var clientB = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 交錯發送 5 次 historicdata：Client A 發 3 次，Client B 發 2 次
        var t1 = clientA.GetJsonAsync("/api/historicdata.json?id=101");
        var t2 = clientB.GetJsonAsync("/api/historicdata.json?id=102");
        var t3 = clientA.GetJsonAsync("/api/historicdata.json?id=103");
        var t4 = clientB.GetJsonAsync("/api/historicdata.json?id=104");
        var t5 = clientA.GetJsonAsync("/api/historicdata.json?id=105");

        await Task.WhenAll(t1, t2, t3, t4, t5);
        Assert.Equal(5, handler.Requests.Count);

        // 另外建立第三個 client 並立即 Dispose，確認配額不被重置
        using (var clientC = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget))
        {
            Assert.NotNull(clientC);
        }

        // 第 6 次請求：在 60 秒滑動窗口內應被阻擋等待
        var t6 = clientA.GetJsonAsync("/api/historicdata.json?id=106");

        // 明確等待 waiter 進入佇列
        await WaitForWaiterCountAsync(budget,1, TimeSpan.FromSeconds(5));

        // 59.999 秒時仍不得發送
        clock.Advance(TimeSpan.FromSeconds(59.999));
        Assert.False(t6.IsCompleted, "第 6 次 historicdata 在 59.999 秒仍應處於等待狀態，不得發出");
        Assert.Equal(5, handler.Requests.Count);

        // 達到 60 秒邊界，第 6 次才允許發送
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await handler.WaitUntilRequestCountAsync(6, TimeSpan.FromSeconds(5));
        await t6;
        Assert.Equal(6, handler.Requests.Count);
        Assert.Contains("id=106", handler.Requests[5].Url);
    }

    [Fact]
    public async Task Table_恰2次_第三次等1秒_不受5次每分限制()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);
        var handler = new BudgetTestHandler(clock);
        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 前 2 次 table 立即送出
        var req1 = client.GetJsonAsync("/api/table.json?content=sensors&i=1");
        var req2 = client.GetJsonAsync("/api/table.json?content=sensors&i=2");
        await Task.WhenAll(req1, req2);
        Assert.Equal(2, handler.Requests.Count);

        // 第 3 次 table 應等待 1 秒
        var req3 = client.GetJsonAsync("/api/table.json?content=sensors&i=3");
        await WaitForWaiterCountAsync(budget,1, TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(req3.IsCompleted, "第 3 次 table 在 0.999 秒仍應等待");
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await handler.WaitUntilRequestCountAsync(3, TimeSpan.FromSeconds(5));
        await req3;
        Assert.Equal(3, handler.Requests.Count);

        // 驗證不受 historicdata 的 5/min 限制：在隨後數秒內可再送出多個 table 請求
        for (int i = 4; i <= 8; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await client.GetJsonAsync($"/api/table.json?content=sensors&i={i}");
        }
        Assert.Equal(8, handler.Requests.Count);
    }

    [Fact]
    public async Task 多個history等配額時_table仍可取得名額且在途永不超4()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);
        var handler = new BudgetTestHandler(clock);
        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 發滿 5 次 historicdata
        for (int i = 1; i <= 5; i++)
        {
            await client.GetJsonAsync($"/api/historicdata.json?id={i}");
        }
        Assert.Equal(5, handler.Requests.Count);

        // 啟動 4 個等待 60 秒配額的 historicdata
        using var waitingCts = new CancellationTokenSource();
        var waitingHistoryTasks = Enumerable.Range(6, 4)
            .Select(i => client.GetJsonAsync($"/api/historicdata.json?id={i}", waitingCts.Token))
            .ToArray();

        await WaitForWaiterCountAsync(budget,4, TimeSpan.FromSeconds(5));
        foreach (var task in waitingHistoryTasks)
        {
            Assert.False(task.IsCompleted);
        }

        // 等待配額的 historicdata 不得佔用 HTTP 在途名額：table 請求應能順利取得名額並發出
        var tableTask = client.GetJsonAsync("/api/table.json?content=sensors");
        await handler.WaitUntilRequestCountAsync(6, TimeSpan.FromSeconds(5));
        await tableTask;

        Assert.Equal(6, handler.Requests.Count);
        Assert.Contains("table.json", handler.Requests.Last().Url);

        // 在途請求峰值絕不超過 4
        Assert.True(handler.MaxConcurrentInFlight <= 4, $"在途請求峰值 {handler.MaxConcurrentInFlight} 超過上限 4");
        waitingCts.Cancel();
        foreach (var pending in waitingHistoryTasks)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task 等待者取消_不發HTTP且不漏permit_源請求計數精確()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);
        var handler = new BudgetTestHandler(clock);
        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 發滿 5 次 historicdata
        for (int i = 1; i <= 5; i++)
        {
            await client.GetJsonAsync($"/api/historicdata.json?id={i}");
        }

        using var cts = new CancellationTokenSource();
        var blockedTask = client.GetJsonAsync("/api/historicdata.json?id=blocked", cts.Token);
        await WaitForWaiterCountAsync(budget,1, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedTask);

        // 未發送 HTTP 請求
        Assert.Equal(5, handler.Requests.Count);

        // 推進時間到 60 秒後，下一個等待者能正常取得名額
        clock.Advance(TimeSpan.FromSeconds(60));
        await client.GetJsonAsync("/api/historicdata.json?id=after-cancel");
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task 已發失敗與body讀取失敗後釋放在途名額_配額不退回()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        var failHandler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = (req, ct) =>
            {
                if (req.RequestUri!.ToString().Contains("fail-network"))
                {
                    throw new HttpRequestException("Simulated connection reset");
                }
                if (req.RequestUri!.ToString().Contains("fail-body"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ExplodingContent()
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        };

        using var client = new PrtgClient(BaseUrl, Token, 30, false, failHandler, PrtgAuthModes.Token, "", "", "", budget);

        // 1. 網路例外
        await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/historicdata.json?fail-network=1"));

        // 2. Body 讀取例外
        await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/historicdata.json?fail-body=1"));

        // 配額不因 HTTP 失敗立刻退回：再發 3 次（共 5 次），第 6 次會被擋
        await client.GetJsonAsync("/api/historicdata.json?id=3");
        await client.GetJsonAsync("/api/historicdata.json?id=4");
        await client.GetJsonAsync("/api/historicdata.json?id=5");

        var blocked = client.GetJsonAsync("/api/historicdata.json?id=6");
        await WaitForWaiterCountAsync(budget,1, TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(59.999));
        Assert.False(blocked.IsCompleted, "已發失敗仍計入滾動配額，第 6 次在 59.999 秒不得發送");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await handlerWait(failHandler, 6);
        await blocked;

        static async Task handlerWait(BudgetTestHandler h, int count)
        {
            await h.WaitUntilRequestCountAsync(count, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task 帳密模式_getpasshash與JSON分別取得在途名額不巢狀死鎖()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);
        var handler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("exchanged-hash", Encoding.UTF8, "text/plain")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
                });
            }
        };

        using var client = new PrtgClient(
            baseUrl: BaseUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: handler,
            authMode: PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "secret",
            passhashOrEmpty: "",
            budget: budget);

        var result = await client.GetJsonAsync("/api/table.json?content=sensors");
        Assert.Equal("{\"status\":\"ok\"}", result);

        // 應有 getpasshash 與 table.json 兩個請求
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("getpasshash.htm", handler.Requests[0].Url);
        Assert.Contains("table.json", handler.Requests[1].Url);
        Assert.True(handler.MaxConcurrentInFlight <= 4);
    }

    [Theory]
    [InlineData("/api/historicdata.json?id=1", PrtgEndpointCategory.HistoricData)]
    [InlineData("api/HISTORICDATA.JSON?id=1", PrtgEndpointCategory.HistoricData)]
    [InlineData("///api/HistoricData.csv?id=1", PrtgEndpointCategory.HistoricData)]
    [InlineData("/api/table.json?content=sensors", PrtgEndpointCategory.Table)]
    [InlineData("api/TABLE.JSON", PrtgEndpointCategory.Table)]
    [InlineData("/api/table.xml?content=devices", PrtgEndpointCategory.Table)]
    [InlineData("/api/table.json?content=historicdata", PrtgEndpointCategory.Table)]
    [InlineData("/api/stable/status.json", PrtgEndpointCategory.Other)]
    [InlineData("/api/getpasshash.htm?username=admin", PrtgEndpointCategory.Other)]
    [InlineData("/api/unknown/endpoint", PrtgEndpointCategory.Other)]
    [InlineData("", PrtgEndpointCategory.Other)]
    [InlineData(null, PrtgEndpointCategory.Other)]
    public void Classify_端點分類正確處理大小寫與query與未知路徑(string? input, PrtgEndpointCategory expected)
    {
        Assert.Equal(expected, PrtgRequestBudget.Classify(input));
    }

    [Fact]
    public async Task Regression_4在途佔滿時_t0等待之table與t2新增之table不得在解除後同時突發4個_應恰2個()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        var releaseStatusTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tableSendTimes = new List<TimeSpan>();
        var tableLock = new object();

        var handler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = async (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("status"))
                {
                    await releaseStatusTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (req.RequestUri.AbsolutePath.Contains("table"))
                {
                    lock (tableLock)
                    {
                        tableSendTimes.Add(clock.Elapsed);
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 4 個 status 請求佔滿在途 4 個名額
        var busyTasks = Enumerable.Range(0, 4)
            .Select(i => client.GetJsonAsync($"/api/status.json?id={i}"))
            .ToArray();

        // 確保 4 個 status 請求已確實進入 Handler 佔用在途
        await handler.WaitUntilInFlightAsync(4, TimeSpan.FromSeconds(5));

        // 在 t0 放入 2 個 table 請求
        var firstTableTasks = Enumerable.Range(0, 2)
            .Select(i => client.GetJsonAsync($"/api/table.json?id={i}"))
            .ToArray();

        await WaitForWaiterCountAsync(budget,2, TimeSpan.FromSeconds(5));

        // 時鐘推到 t2 (經過 2 秒)
        clock.Advance(TimeSpan.FromSeconds(2));

        // 在 t2 放入 2 個 table 請求
        var secondTableTasks = Enumerable.Range(2, 2)
            .Select(i => client.GetJsonAsync($"/api/table.json?id={i}"))
            .ToArray();

        await WaitForWaiterCountAsync(budget,4, TimeSpan.FromSeconds(5));

        // 解除 4 個 status 請求，釋放在途名額
        releaseStatusTcs.SetResult(true);
        await Task.WhenAll(busyTasks).WaitAsync(TimeSpan.FromSeconds(5));

        // 等待所有已被准許的 table 請求進入 handler
        await handler.WaitUntilRequestCountAsync(6, TimeSpan.FromSeconds(5)); // 4 status + 2 table
        await WaitForWaiterCountAsync(budget, 2, TimeSpan.FromSeconds(5));

        // 驗證：在 t2 當下，最多只能送出 2 個 table 請求，不得 4 個全部突發！
        lock (tableLock)
        {
            Assert.Equal(2, tableSendTimes.Count);
            Assert.All(tableSendTimes, t => Assert.Equal(TimeSpan.FromSeconds(2), t));
        }

        // 後續 2 個 table 必須等待 1 秒（t3 時送出）
        clock.Advance(TimeSpan.FromSeconds(1));
        await handler.WaitUntilRequestCountAsync(8, TimeSpan.FromSeconds(5)); // 4 status + 4 table
        await Task.WhenAll(firstTableTasks.Concat(secondTableTasks)).WaitAsync(TimeSpan.FromSeconds(5));

        lock (tableLock)
        {
            Assert.Equal(4, tableSendTimes.Count);
            Assert.Equal(TimeSpan.FromSeconds(3), tableSendTimes[2]);
            Assert.Equal(TimeSpan.FromSeconds(3), tableSendTimes[3]);
        }
    }

    [Fact]
    public async Task Regression_4在途佔滿時_t0等待之historic與t60新增之historic不得在解除後同時突發10個_應恰5個()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        var releaseStatusTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var historicSendTimes = new List<TimeSpan>();
        var historicLock = new object();

        var handler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = async (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("status"))
                {
                    await releaseStatusTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (req.RequestUri.AbsolutePath.Contains("historicdata"))
                {
                    lock (historicLock)
                    {
                        historicSendTimes.Add(clock.Elapsed);
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 4 個 status 請求佔滿在途
        var busyTasks = Enumerable.Range(0, 4)
            .Select(i => client.GetJsonAsync($"/api/status.json?id={i}"))
            .ToArray();

        await handler.WaitUntilInFlightAsync(4, TimeSpan.FromSeconds(5));

        // 在 t0 放入 5 個 historic 請求
        var firstHistoricTasks = Enumerable.Range(0, 5)
            .Select(i => client.GetJsonAsync($"/api/historicdata.json?id={i}"))
            .ToArray();

        await WaitForWaiterCountAsync(budget,5, TimeSpan.FromSeconds(5));

        // 時鐘推到 t60 (經過 60 秒)
        clock.Advance(TimeSpan.FromSeconds(60));

        // 在 t60 放入 5 個 historic 請求
        var secondHistoricTasks = Enumerable.Range(5, 5)
            .Select(i => client.GetJsonAsync($"/api/historicdata.json?id={i}"))
            .ToArray();

        await WaitForWaiterCountAsync(budget,10, TimeSpan.FromSeconds(5));

        // 解除 4 個 status 請求
        releaseStatusTcs.SetResult(true);
        await Task.WhenAll(busyTasks).WaitAsync(TimeSpan.FromSeconds(5));

        // 等待已被准許的請求執行（預期剛好 5 個）
        await handler.WaitUntilRequestCountAsync(9, TimeSpan.FromSeconds(5)); // 4 status + 5 historic
        await WaitForWaiterCountAsync(budget, 5, TimeSpan.FromSeconds(5));

        // 驗證：在 t60 當下，最多只能送出 5 個 historicdata 請求，不得 10 個全部突發！
        lock (historicLock)
        {
            Assert.Equal(5, historicSendTimes.Count);
            Assert.All(historicSendTimes, t => Assert.Equal(TimeSpan.FromSeconds(60), t));
        }

        // 後續 5 個必須等待另一個 60 秒視窗（t120 時送出）
        clock.Advance(TimeSpan.FromSeconds(60));
        await handler.WaitUntilRequestCountAsync(14, TimeSpan.FromSeconds(5)); // 4 status + 10 historic
        await Task.WhenAll(firstHistoricTasks.Concat(secondHistoricTasks)).WaitAsync(TimeSpan.FromSeconds(5));

        lock (historicLock)
        {
            Assert.Equal(10, historicSendTimes.Count);
            for (int i = 5; i < 10; i++)
            {
                Assert.Equal(TimeSpan.FromSeconds(120), historicSendTimes[i]);
            }
        }
    }

    [Fact]
    public async Task Regression_Checkpoint延遲_Table已准許但未送出前時鐘推進2秒_解除後不得與新請求突發4個_應恰2個()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        using var reachedCheckpoint = new CountdownEvent(2);
        using var releaseCheckpoint = new ManualResetEventSlim();
        var tableSendTimes = new List<TimeSpan>();
        var tableLock = new object();

        var handler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("table"))
                {
                    lock (tableLock)
                    {
                        tableSendTimes.Add(clock.Elapsed);
                    }
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        };

        PrtgClient MakeClient(bool pauseAtCheckpoint)
        {
            var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);
            if (pauseAtCheckpoint)
            {
                var n = 0;
                client.OperationCheckpoint = () =>
                {
                    if (Interlocked.Increment(ref n) == 3)
                    {
                        reachedCheckpoint.Signal();
                        if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Checkpoint wait timed out");
                    }
                };
            }
            return client;
        }

        using var client1 = MakeClient(true);
        using var client2 = MakeClient(true);
        using var client3 = MakeClient(false);

        var task1 = Task.Run(() => client1.GetJsonAsync("/api/table.json?id=1"));
        var task2 = Task.Run(() => client2.GetJsonAsync("/api/table.json?id=2"));

        Assert.True(reachedCheckpoint.Wait(TimeSpan.FromSeconds(5)), "前兩個請求應到達 Checkpoint");

        // 時鐘推過 2 秒（Table 視窗為 1 秒）
        clock.Advance(TimeSpan.FromSeconds(2));

        // 此時 client3 嘗試發送 2 個 table 請求
        var task3 = Task.Run(() => client3.GetJsonAsync("/api/table.json?id=3"));
        var task4 = Task.Run(() => client3.GetJsonAsync("/api/table.json?id=4"));
        await WaitForWaiterCountAsync(budget, 2, TimeSpan.FromSeconds(5));

        // 放行前兩個暫停中的 checkpoint
        releaseCheckpoint.Set();

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(5));

        // 驗證：在 t=2s 當下，最多只能送出 2 個 table 請求！
        lock (tableLock)
        {
            var sendsAtT2 = tableSendTimes.Count(t => t == TimeSpan.FromSeconds(2));
            Assert.Equal(2, sendsAtT2);
        }

        // 後續 2 個必須等待時鐘推進至 t=3s (另一個 1 秒視窗)
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(task3, task4).WaitAsync(TimeSpan.FromSeconds(5));

        lock (tableLock)
        {
            Assert.Equal(4, tableSendTimes.Count);
            var sendsAtT3 = tableSendTimes.Count(t => t == TimeSpan.FromSeconds(3));
            Assert.Equal(2, sendsAtT3);
        }
    }

    [Fact]
    public async Task Regression_Checkpoint延遲_Historic已准許但未送出前時鐘推進60秒_解除後不得與新請求突發10個_應恰5個()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        using var reachedCheckpoint = new CountdownEvent(4);
        using var releaseCheckpoint = new ManualResetEventSlim();
        var historicSendTimes = new List<TimeSpan>();
        var historicLock = new object();

        var handler = new BudgetTestHandler(clock)
        {
            CustomSendAsync = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("historicdata"))
                {
                    lock (historicLock)
                    {
                        historicSendTimes.Add(clock.Elapsed);
                    }
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        };

        PrtgClient MakeClient(bool pauseAtCheckpoint)
        {
            var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);
            if (pauseAtCheckpoint)
            {
                var n = 0;
                client.OperationCheckpoint = () =>
                {
                    if (Interlocked.Increment(ref n) == 3)
                    {
                        reachedCheckpoint.Signal();
                        if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Checkpoint wait timed out");
                    }
                };
            }
            return client;
        }

        var pausedClients = Enumerable.Range(0, 4).Select(_ => MakeClient(true)).ToList();
        var newClients = Enumerable.Range(0, 5).Select(_ => MakeClient(false)).ToList();

        try
        {
            var firstTasks = pausedClients.Select((c, i) => Task.Run(() => c.GetJsonAsync($"/api/historicdata.json?id={i}"))).ToArray();

            Assert.True(reachedCheckpoint.Wait(TimeSpan.FromSeconds(5)), "前 4 個 historic 請求應到達 Checkpoint 佔滿在途");

            // 時鐘推過 60 秒
            clock.Advance(TimeSpan.FromSeconds(60));

            // 此時放入 5 個新的 historic 請求（因在途與預留皆滿，進入佇列）
            var secondTasks = newClients.Select((c, i) => Task.Run(() => c.GetJsonAsync($"/api/historicdata.json?id={i + 10}"))).ToArray();
            await WaitForWaiterCountAsync(budget, 5, TimeSpan.FromSeconds(5));

            // 放行前 4 個 checkpoint
            releaseCheckpoint.Set();

            await Task.WhenAll(firstTasks).WaitAsync(TimeSpan.FromSeconds(5));

            // Task.Run 的入列順序不等於陣列順序，只等待實際發送數及其餘等待者。
            await handler.WaitUntilRequestCountAsync(5, TimeSpan.FromSeconds(5));
            await WaitForWaiterCountAsync(budget, 4, TimeSpan.FromSeconds(5));

            // 驗證：在 t=60s 當下，恰送出 5 個 historic 請求（4 個解除 + 1 個遞補），不得全部突發！
            lock (historicLock)
            {
                var sendsAtT60 = historicSendTimes.Count(t => t == TimeSpan.FromSeconds(60));
                Assert.Equal(5, sendsAtT60);
            }

            // 後續 4 個必須等待時鐘推進至 t=120s (另一個 60 秒視窗)
            clock.Advance(TimeSpan.FromSeconds(60));
            await handler.WaitUntilRequestCountAsync(9, TimeSpan.FromSeconds(5));
            await Task.WhenAll(secondTasks).WaitAsync(TimeSpan.FromSeconds(5));

            lock (historicLock)
            {
                Assert.Equal(9, historicSendTimes.Count);
                var sendsAtT120 = historicSendTimes.Count(t => t == TimeSpan.FromSeconds(120));
                Assert.Equal(4, sendsAtT120);
            }
        }
        finally
        {
            foreach (var c in pausedClients) c.Dispose();
            foreach (var c in newClients) c.Dispose();
        }
    }

    [Fact]
    public async Task Regression_Pending預留取消後不卡住_後續等待者立即取得配額()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);

        using var reachedCheckpoint = new ManualResetEventSlim();
        using var triggerCancel = new ManualResetEventSlim();

        var handler = new BudgetTestHandler(clock);

        using var client1 = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);
        using var client2 = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);
        using var client3 = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        using var cts = new CancellationTokenSource();
        var n = 0;
        client1.OperationCheckpoint = () =>
        {
            if (Interlocked.Increment(ref n) == 3)
            {
                reachedCheckpoint.Set();
                triggerCancel.Wait(TimeSpan.FromSeconds(5));
                cts.Cancel();
            }
        };

        // client1 取得 Table 名額 (1/2) 並停在 Checkpoint
        var task1 = Task.Run(() => client1.GetJsonAsync("/api/table.json?id=1", cts.Token));
        Assert.True(reachedCheckpoint.Wait(TimeSpan.FromSeconds(5)));

        // client2 取得 Table 名額 (2/2) 並成功發送
        await client2.GetJsonAsync("/api/table.json?id=2");
        Assert.Single(handler.Requests);

        // 此時 Table 額度已滿 (client1 pending + client2 sent)
        // client3 請求 Table，應進入佇列等待
        var task3 = Task.Run(() => client3.GetJsonAsync("/api/table.json?id=3"));
        await WaitForWaiterCountAsync(budget, 1, TimeSpan.FromSeconds(5));

        // 觸發 client1 在 checkpoint 取消
        triggerCancel.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);

        // 驗證：client1 的 pending 預留釋放後，client3 立即取得配額並成功發送，不需等待 1 秒！
        var result3 = await task3.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result3);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Regression_UTC時鐘跳動但單調時間未推進時_不刷新滑動窗口額度()
    {
        var clock = new TestPrtgClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = new PrtgRequestBudget(clock);
        var handler = new BudgetTestHandler(clock);

        using var client = new PrtgClient(BaseUrl, Token, 30, false, handler, PrtgAuthModes.Token, "", "", "", budget);

        // 發滿 2 次 Table
        await client.GetJsonAsync("/api/table.json?id=1");
        await client.GetJsonAsync("/api/table.json?id=2");
        Assert.Equal(2, handler.Requests.Count);

        // 系統 UTC 時間往前跳動 1 小時，但單調經過時間未推進
        clock.JumpUtcOnly(TimeSpan.FromHours(1));

        // 第 3 次 Table 請求仍應被阻擋等待，不得因為 UTC 時間跳動而刷新額度
        var task3 = Task.Run(() => client.GetJsonAsync("/api/table.json?id=3"));

        await WaitForWaiterCountAsync(budget, 1, TimeSpan.FromSeconds(5));
        Assert.False(task3.IsCompleted, "單調時間未推進時，即使 UTC 時間跳動，Table 限流亦不得放行");
        Assert.Equal(2, handler.Requests.Count);

        // 單調時間推進 1 秒後才允許放行
        clock.Advance(TimeSpan.FromSeconds(1));
        await task3.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task 租約重複標記不刷新時間_釋放後不得再標記送出()
    {
        var clock = new TestPrtgClock(DateTimeOffset.UtcNow);
        var budget = new PrtgRequestBudget(clock);
        var first = await budget.AcquireAsync(PrtgEndpointCategory.Table);
        first.MarkRequestSent();
        using (var second = await budget.AcquireAsync(PrtgEndpointCategory.Table))
            second.MarkRequestSent();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        first.MarkRequestSent();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.MarkRequestSent());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using var next = await budget.AcquireAsync(PrtgEndpointCategory.Table).WaitAsync(TimeSpan.FromSeconds(5));
        next.MarkRequestSent();
    }

    [Fact]
    public void 正式入口接線_PrtgClientFactory與SystemSettingsService皆使用SharedBudget()
    {
        var settings = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("token-test")
        };

        using var factoryClient = PrtgClientFactory.Create(settings);
        Assert.Same(PrtgRequestBudget.Shared, factoryClient.Budget);

        var store = new FakeSystemSettingsStore();
        store.Update(s =>
        {
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token-test");
        });

        var fx = new EfSqliteFixture();
        try
        {
            var service = new SystemSettingsService(
                store,
                FakeCurrentUser.WithCapabilities(),
                new RecordingAuditService(),
                new FakeUserStore(),
                new MailNotificationService(
                    store,
                    new FakeSmtpMailSender(),
                    new FakeHostStore(),
                    new FakeUserStore(),
                    new FakeUserGroupStore(),
                    new FakeGroupAccessStore(),
                    new FakeAnalysisRecordQuery(),
                    new FakeHandlingStore(),
                    new MailNotifyStateStore(fx.Blob("mail_notify_state")),
                    new ScheduleFreshnessService(
                        new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs")),
                        new ScheduleOptionsStore(fx.Blob("schedule_options")))),
                new FakeReportUsageQuery());

            Assert.Same(PrtgRequestBudget.Shared, service.PrtgBudget);
        }
        finally
        {
            fx.Dispose();
        }
    }

    private static async Task WaitForWaiterCountAsync(PrtgRequestBudget budget, int expected, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (budget.WaiterCount >= expected) return;
            await Task.Delay(5);
        }
        throw new TimeoutException($"Timed out waiting for {expected} waiters (current: {budget.WaiterCount})");
    }

    private sealed class BudgetTestHandler : HttpMessageHandler
    {
        private readonly IPrtgClock? _clock;
        private int _currentInFlight;
        private int _maxInFlight;
        private readonly object _lock = new();

        public List<RecordedRequest> Requests { get; } = new();
        public int MaxConcurrentInFlight => _maxInFlight;

        public BudgetTestHandler(IPrtgClock? clock = null)
        {
            _clock = clock;
        }

        public async Task WaitUntilInFlightAsync(int expected, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_lock)
                {
                    if (_currentInFlight >= expected) return;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException($"Timed out waiting for {expected} in-flight requests (current: {_currentInFlight})");
        }

        public async Task WaitUntilRequestCountAsync(int expected, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_lock)
                {
                    if (Requests.Count >= expected) return;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException($"Timed out waiting for {expected} requests (current: {Requests.Count})");
        }

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? CustomSendAsync { get; set; }

        public sealed record RecordedRequest(HttpMethod Method, string Url, DateTimeOffset Time, TimeSpan Elapsed);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var time = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
            var elapsed = _clock?.Elapsed ?? TimeSpan.Zero;
            lock (_lock)
            {
                _currentInFlight++;
                if (_currentInFlight > _maxInFlight) _maxInFlight = _currentInFlight;
                Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), time, elapsed));
            }

            try
            {
                if (CustomSendAsync != null)
                {
                    return await CustomSendAsync(request, cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                lock (_lock)
                {
                    _currentInFlight--;
                }
            }
        }
    }

    private sealed class ExplodingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("Simulated body read failure mid-stream");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            throw new IOException("Simulated body stream failure");
    }

    public sealed class TestPrtgClock : IPrtgClock
    {
        private DateTimeOffset _now;
        private TimeSpan _elapsed = TimeSpan.Zero;
        private readonly List<WaitingTimer> _timers = new();
        private readonly object _lock = new();

        public TestPrtgClock(DateTimeOffset initial)
        {
            _now = initial;
        }

        public DateTimeOffset UtcNow
        {
            get { lock (_lock) return _now; }
        }

        public TimeSpan Elapsed
        {
            get { lock (_lock) return _elapsed; }
        }

        public void JumpUtcOnly(TimeSpan duration)
        {
            lock (_lock)
            {
                _now = _now.Add(duration);
            }
        }

        public int WaitingTimerCount
        {
            get { lock (_lock) return _timers.Count; }
        }

        public async Task WaitUntilTimersCountAsync(int expected, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_lock)
                {
                    if (_timers.Count >= expected) return;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException($"Timed out waiting for {expected} timers in TestPrtgClock (current: {WaitingTimerCount})");
        }

        public void Advance(TimeSpan duration)
        {
            List<TaskCompletionSource<bool>> toTrigger = new();
            lock (_lock)
            {
                _now = _now.Add(duration);
                _elapsed += duration;
                for (int i = _timers.Count - 1; i >= 0; i--)
                {
                    if (_timers[i].DueTime <= _now)
                    {
                        toTrigger.Add(_timers[i].Tcs);
                        _timers.RemoveAt(i);
                    }
                }
            }
            foreach (var tcs in toTrigger)
            {
                tcs.TrySetResult(true);
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new WaitingTimer(UtcNow.Add(delay), tcs);

            var reg = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    _timers.Remove(timer);
                }
                tcs.TrySetCanceled(cancellationToken);
            });

            lock (_lock)
            {
                if (_now >= timer.DueTime)
                {
                    reg.Dispose();
                    return Task.CompletedTask;
                }
                _timers.Add(timer);
            }

            return tcs.Task;
        }

        private sealed record WaitingTimer(DateTimeOffset DueTime, TaskCompletionSource<bool> Tcs);
    }
}
