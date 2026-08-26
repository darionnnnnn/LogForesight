using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelRestDirectoryClient：網段範圍掃描的真實 client
/// （docs/NETIQ-API-REFERENCE.md §3.4；**2026-08-06 涵蓋保證改版**
/// docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §三）。
///
/// <para><b>這組測試守的是「涵蓋保證」</b>：掃描結果只有三種——完整、
/// 顯性警告不完整、顯性失敗。被消滅的是第四種：**靜默漏掉主機**。
/// 舊版用「事件越多、掃描窗口越短」控制取回筆數，被裁掉的時間裡安靜主機的
/// 少數幾筆事件就這樣消失，而畫面上看不出來——那些「窗口依比例縮短」的舊測試
/// 因此連同該行為一起移除，不是忘了搬。</para>
///
/// 用 <see cref="ScriptedHandler"/> 依 job 建立順序給定回應，
/// 並保留每個 job 的 filter 供斷言（驗證排除子句真的送出去了）。
/// </summary>
public class SentinelRestDirectoryClientTests
{
    private static SentinelServer Server() => new()
    {
        Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443", Username = "svc", Password = "pw"
    };

    private static NetiqOptions Options() => new() { RetryCount = 1, TimeoutSeconds = 30, QueryDelayMs = 0 };

    private const string AuthUrl = "https://sentinel.local:8443/SentinelAuthServices/auth/tokens";
    private const string JobCollectionUrl = "https://sentinel.local:8443/SentinelRESTServices/objects/event-search";

    /// <summary>一個 job 的模擬回應：found 與實際回傳的事件（可傳單頁事件或多頁陣列）。
    /// truncated 由 <c>found > 事件數</c> 自然成立，與真實 client 的判斷同源。</summary>
    private sealed class JobScript
    {
        public int Found { get; }
        public (string Ip, string? Name)[][] Pages { get; }

        public JobScript(int found, params (string Ip, string? Name)[] events)
        {
            Found = found;
            Pages = new[] { events };
        }

        public JobScript(int found, (string Ip, string? Name)[][] pages)
        {
            Found = found;
            Pages = pages;
        }

        public int TotalEventsCount => Pages.Sum(p => p.Length);
    }

    private static JobScript Empty() => new(0);

    // ── 主掃描：一輪掃完 ────────────────────────────────────────────────────

    [Fact]
    public async Task 一輪掃完_主掃描與補充掃描各發一個job()
    {
        var handler = new ScriptedHandler(
            new JobScript(2, ("10.1.2.5", "srv-dc01"), ("10.1.2.6", "srv-dc02")),   // 主掃描
            Empty());                                                               // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, handler.Filters.Count);

        // 主掃描窄化到低量頻道（成本正比主機數而非事件量）；補充掃描是全事件短窗
        Assert.Contains("rv150:System", handler.Filters[0]);
        Assert.DoesNotContain("rv150:System", handler.Filters[1]);
    }

    /// <summary>
    /// 掃描窗口固定 24 小時，**不再隨事件量縮短**——那正是舊版靜默漏機的機制
    /// （事件越多窗口越短，被裁掉的時間裡安靜主機的少數幾筆事件就這樣消失，且無警告）。
    /// 這裡直接斷言送出去的時間區間，不是斷言文案；而且每一輪殘差輪掃都要維持 24 小時，
    /// 不能「第一輪 24 小時、後面偷偷縮短」。
    /// </summary>
    [Fact]
    public async Task 主掃描窗口固定24小時_不因事件量縮短()
    {
        // 每輪都給極大的 found（＝每輪都觸頂），把殘差輪掃全部跑滿
        var scripts = Enumerable.Range(0, SentinelRestDirectoryClient.MaxResidualRounds)
            .Select(i => new JobScript(9_999_999, ($"10.1.2.{i}", $"srv{i}")))
            .Append(Empty())   // 補充掃描
            .ToArray();
        var handler = new ScriptedHandler(scripts);

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var mainWindows = handler.Windows.Take(SentinelRestDirectoryClient.MaxResidualRounds);
        Assert.All(mainWindows, w => Assert.Equal(24, (w.End - w.Start).TotalHours, precision: 1));
    }

    [Fact]
    public async Task 補充掃描用短窗且排除主掃描已見的主機()
    {
        var handler = new ScriptedHandler(
            new JobScript(1, ("10.1.2.5", "srv1")),
            new JobScript(1, ("10.1.2.9", "srv9")));

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        // 短窗
        var (start, end) = handler.Windows[1];
        Assert.Equal(SentinelRestDirectoryClient.SupplementWindowMinutes, (end - start).TotalMinutes, precision: 1);

        // 排除主掃描已見：不排除的話取回的絕大多數是已知主機的 Security 洪流，純浪費
        Assert.Contains("NOT (repip:10.1.2.5)", handler.Filters[1]);

        // 兩段聯集
        Assert.Equal(2, result.Hosts.Count);
        Assert.Contains(result.Hosts, h => h.IpAddress == "10.1.2.9");
    }

    // ── 殘差輪掃：涵蓋保證的核心 ────────────────────────────────────────────

    /// <summary>
    /// 單輪取回觸頂時**不縮短窗口**，改為排除已發現的主機後重查——
    /// 下一輪取回的只有還沒見過的主機。這是「上限有限、但不會漏機」的關鍵。
    /// </summary>
    [Fact]
    public async Task 主掃描觸頂_排除已見主機後重查直到掃完()
    {
        var handler = new ScriptedHandler(
            new JobScript(500, ("10.1.2.5", "srv5")),                   // 第 1 輪：found > 取回 → 觸頂
            new JobScript(2, ("10.1.2.6", "srv6"), ("10.1.2.7", "srv7")), // 第 2 輪：殘差掃完
            Empty());                                                    // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(3, result.Hosts.Count);
        Assert.Empty(result.Warnings);   // 掃完了就不該有「可能不完整」的警告

        // 第 2 輪把第 1 輪見到的主機排除掉
        Assert.DoesNotContain("NOT", handler.Filters[0]);
        Assert.Contains("NOT (repip:10.1.2.5)", handler.Filters[1]);
    }

    /// <summary>輪數用盡仍未掃完＝**可能漏**，必須警告——涵蓋保證的三種結果之一</summary>
    [Fact]
    public async Task 輪數用盡仍未掃完_顯性警告而不是靜默回傳()
    {
        // 每輪都觸頂（found 恆大於取回筆數）
        var scripts = Enumerable.Range(0, SentinelRestDirectoryClient.MaxResidualRounds)
            .Select(i => new JobScript(9999, ($"10.1.2.{i}", $"srv{i}")))
            .Append(Empty())   // 補充掃描
            .ToArray();
        var handler = new ScriptedHandler(scripts);

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(SentinelRestDirectoryClient.MaxResidualRounds, result.Hosts.Count);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("主掃描", warning);
        Assert.Contains("清單可能不完整", warning);
        Assert.Contains("更小的網段", warning);   // 訊息要說得出下一步怎麼辦
    }

    /// <summary>
    /// NOT 子句在本環境**尚未實測**（probe 驗過 OR／片語／前綴萬用字元，沒驗過 NOT）。
    /// 萬一被 Sentinel 忽略，每輪都會取回同一批主機、白燒完輪數，而使用者只看到「掃很久」。
    /// 當場偵測、立刻停止、講清楚——不讓壞掉的機制安靜地假裝在工作。
    /// </summary>
    [Fact]
    public async Task 排除語法未生效_當場偵測並停止輪掃()
    {
        var handler = new ScriptedHandler(
            new JobScript(9999, ("10.1.2.5", "srv5")),   // 第 1 輪觸頂
            new JobScript(9999, ("10.1.2.5", "srv5")),   // 第 2 輪又回同一台＝NOT 沒生效
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Single(result.Hosts);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("排除語法", warning);
        Assert.Contains("未生效", warning);

        // 偵測到就停：不該把 5 輪燒完（2 輪主掃描 + 1 輪補充掃描）
        Assert.Equal(3, handler.Filters.Count);
    }

    /// <summary>
    /// 主掃描發現的主機超過排除子句上限時，補充掃描**退回無排除照掃**（規劃 §3.2）——
    /// 跳過它等於把「撈 Security-only 漏網主機」整段棄守；無排除地掃雖會混入已知主機的
    /// 事件（Absorb 去重吸掉），但仍可能在 cap 內補到未知主機，比不掃好。
    /// </summary>
    [Fact]
    public async Task 已見主機超過排除上限_補充掃描退回無排除照掃而不是跳過()
    {
        // 主掃描一輪回超過上限（501 台）且未截斷 → 補充掃描的排除清單放不進子句
        var manyHosts = Enumerable.Range(0, SentinelRestDirectoryClient.ExclusionClauseLimit + 1)
            .Select(i => ($"10.1.2.{i}", (string?)$"srv{i}"))
            .ToArray();
        var handler = new ScriptedHandler(
            new JobScript(manyHosts.Length, manyHosts),
            new JobScript(1, ("10.1.2.9", "supp")));

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, handler.Filters.Count);                 // 補充掃描真的跑了
        Assert.DoesNotContain("NOT", handler.Filters[1]);       // 而且是無排除版
        Assert.Contains(result.Hosts, h => h.IpAddress == "10.1.2.9");
        // 兩段都掃完（未截斷）→ 不該有「排除上限」的警告
        Assert.DoesNotContain(result.Warnings, w => w.Contains("排除上限"));
    }

    // ── 重掃增量：已登錄主機在 server 端就排除 ──────────────────────────────

    [Fact]
    public async Task 帶已登錄主機_第一輪就排除且結果不含它們()
    {
        var handler = new ScriptedHandler(Empty(), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None,
                knownIps: new[] { "10.1.2.11", "10.1.2.12" });

        Assert.Contains("NOT (repip:10.1.2.11 OR repip:10.1.2.12)", handler.Filters[0]);
        Assert.Empty(result.Hosts);   // 沒有新主機——重掃的正常結果
        Assert.Contains("已登錄 2 台未重查", result.CoverageNote);
    }

    /// <summary>已登錄數超過子句上限就整組放棄排除：退回一般掃描只是慢，
    /// 送出超長 filter 會被 Sentinel 整個拒絕——寧可慢也不要整趟失敗</summary>
    [Fact]
    public async Task 已登錄主機數超過子句上限_退回不排除的一般掃描()
    {
        var tooMany = Enumerable.Range(1, SentinelRestDirectoryClient.ExclusionClauseLimit + 1)
            .Select(i => $"10.1.{i / 250}.{i % 250}")
            .ToList();
        var handler = new ScriptedHandler(Empty(), Empty());

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None, knownIps: tooMany);

        Assert.DoesNotContain("NOT", handler.Filters[0]);
    }

    // ── 誠實申報 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 兩段皆無事件_講明白沒有回報而不是假裝掃到空網段()
    {
        var handler = new ScriptedHandler(Empty(), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Empty(result.Hosts);
        Assert.Contains("無任何事件回報", result.CoverageNote);
    }

    [Fact]
    public async Task CoverageNote講明兩個窗口與看不到的那一類主機()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.5", "srv5")), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Contains("24 小時", result.CoverageNote);
        Assert.Contains($"{SentinelRestDirectoryClient.SupplementWindowMinutes} 分鐘", result.CoverageNote);
        // 事件掃描原理上看不到「完全沒講話」的主機——這是資料源極限，不能不講
        Assert.Contains("原理上看不到", result.CoverageNote);
    }

    /// <summary>
    /// 主掃描 0 台、補充掃描有台數＝這台 Sentinel 的 collector 可能不轉送
    /// System/Application，窄化 filter 因此失效。說出來讓人去查，不要長期靠短窗硬撐
    /// 而沒有人知道涵蓋率為什麼偏低。
    /// </summary>
    [Fact]
    public async Task 主掃描零台但補充掃描有台數_提示頻道覆蓋可能有問題()
    {
        var handler = new ScriptedHandler(
            Empty(),
            new JobScript(1, ("10.1.2.9", "srv9")));

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("collector 可能不轉送", warning);
        Assert.Contains("診斷", warning);
    }

    [Fact]
    public async Task HostName取同一IP最常見的sn值()
    {
        var handler = new ScriptedHandler(
            new JobScript(3, ("10.1.2.5", "srv-dc01"), ("10.1.2.5", "srv-dc01"), ("10.1.2.5", "WEIRD")),
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var host = Assert.Single(result.Hosts);
        Assert.Equal("srv-dc01", host.HostName);
        Assert.Equal("10.1.2.5", host.IpAddress);
    }

    [Fact]
    public async Task sn缺席時HostName退回IP()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.9", null)), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal("10.1.2.9", Assert.Single(result.Hosts).HostName);
    }

    // ── ESM 事件來源目錄（§五）：開關預設關、失敗一律退回事件掃描且要吵 ────────

    private static SentinelServer EsmServer() => new()
    {
        Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443",
        Username = "svc", Password = "pw", UseEsmDirectory = true
    };

    /// <summary>開關關閉（預設）時**完全不碰** ESM 端點——既有環境零行為差異</summary>
    [Fact]
    public async Task ESM開關關閉_不打目錄端點()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.5", "srv5")), Empty());

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.False(handler.EsmRequested);
    }

    [Fact]
    public async Task ESM可用_直接回目錄結果且不發任何事件查詢()
    {
        var handler = new ScriptedHandler
        {
            // 三筆條目，其中 10.9.9.9 不在掃描的網段內——驗證前綴過濾
            EsmResponse = ("""[{"name":"SRV-A","ipAddress":"10.1.2.5"},{"name":"SRV-B","ipAddress":"10.1.2.6"},{"name":"OTHER","ipAddress":"10.9.9.9"}]""", HttpStatusCode.OK)
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(EsmServer(), "10.1.2", CancellationToken.None);

        // 只回本網段的兩台（10.9.9.9 被前綴過濾掉）
        Assert.Equal(2, result.Hosts.Count);
        Assert.Empty(handler.Filters);   // 一個事件查詢都沒發——這正是 ESM 的價值
        Assert.Empty(result.Warnings);

        // 涵蓋語意不同於事件掃描，要講明白
        Assert.Contains("事件來源目錄", result.CoverageNote);
        Assert.Contains("沒有事件回報的主機", result.CoverageNote);
        Assert.Contains("目錄共 3 筆條目", result.CoverageNote);
    }

    [Fact]
    public async Task ESM可用_已登錄主機仍被排除在結果外()
    {
        var handler = new ScriptedHandler
        {
            EsmResponse = ("""[{"name":"SRV-A","ipAddress":"10.1.2.5"},{"name":"SRV-B","ipAddress":"10.1.2.6"}]""",
                HttpStatusCode.OK)
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(EsmServer(), "10.1.2", CancellationToken.None, knownIps: new[] { "10.1.2.5" });

        Assert.Equal("10.1.2.6", Assert.Single(result.Hosts).IpAddress);
        Assert.Contains("已登錄的 1 台", result.CoverageNote);
    }

    /// <summary>無權限＝最可能發生的情況（本環境正是如此）：退回事件掃描，
    /// 並且告訴管理員該做什麼——關掉開關或去要權限</summary>
    [Fact]
    public async Task ESM無權限_退回事件掃描並顯性警告()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.5", "srv5")), Empty())
        {
            EsmResponse = ("Forbidden", HttpStatusCode.Forbidden)
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(EsmServer(), "10.1.2", CancellationToken.None);

        Assert.Single(result.Hosts);            // 事件掃描接手，結果照樣有
        Assert.Equal(2, handler.Filters.Count); // 主掃描＋補充掃描都跑了
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("ESM 唯讀權限", warning);
        Assert.Contains("關閉此開關", warning);   // 訊息要說得出下一步
    }

    /// <summary>
    /// 回應解析不出主機時，**絕不能**當成「這台 Sentinel 沒有主機」——那會讓管理員以為
    /// 機房空了。退回事件掃描，並要求把回應貼回來定案格式。
    /// </summary>
    [Fact]
    public async Task ESM回應格式不符_退回事件掃描而不是回報零台()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.5", "srv5")), Empty())
        {
            EsmResponse = ("""[{"unexpected":"shape"},{"also":"unexpected"}]""", HttpStatusCode.OK)
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(EsmServer(), "10.1.2", CancellationToken.None);

        Assert.Single(result.Hosts);   // 不是 0 台
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("格式與預期不符", warning);
        Assert.Contains("收到 2 筆條目", warning);
        Assert.Contains("診斷", warning);   // 要人回報輸出以便定案
    }

    // ── 輸入與失敗路徑（行為不變，沿用改版前的守則）────────────────────────

    [Fact]
    public async Task BaseUrl未設定_立即擲例外不發任何請求()
    {
        var handler = new ScriptedHandler();
        var client = new SentinelRestDirectoryClient(Options(), handler);
        var server = new SentinelServer { Name = "S", BaseUrl = "", Username = "u", Password = "p" };

        await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(server, "10.1.2", CancellationToken.None));
        Assert.Empty(handler.Filters);
    }

    [Fact]
    public async Task 網段格式不正確_轉成NetiqDiscoveryException()
    {
        var client = new SentinelRestDirectoryClient(Options(), new ScriptedHandler());

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "not-a-subnet", CancellationToken.None));
        Assert.Contains("網段格式不正確", ex.Message);
    }

    [Fact]
    public async Task Sentinel回應錯誤_轉成可顯示的NetiqDiscoveryException()
    {
        var handler = new ScriptedHandler { AuthStatus = HttpStatusCode.Unauthorized };
        var client = new SentinelRestDirectoryClient(Options(), handler);

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", CancellationToken.None));
        Assert.Contains("SENTINEL-A", ex.Message);
        Assert.DoesNotContain("pw", ex.Message);   // 密碼不得出現在例外訊息
    }

    /// <summary>
    /// 分頁迴圈不受單次逾時約束（SentinelClient 的 TimeoutSeconds 只管單次呼叫與 job 輪詢），
    /// 整趟掃描必須有自己的總預算，且逾時要**明確報錯**而不是回傳半套清單
    /// （半套會被誤認為該網段的完整名單）。殘差輪掃讓查詢次數變多，這道防線更重要。
    /// </summary>
    [Fact]
    public async Task 整趟掃描超過總預算_明確報錯且不回傳半套結果()
    {
        var handler = new ScriptedHandler(
            Enumerable.Range(0, 20).Select(i => new JobScript(9999, ($"10.1.2.{i}", $"h{i}"))).ToArray())
        {
            PageDelay = TimeSpan.FromMilliseconds(400)
        };

        // 總預算壓到 2 秒：驗證的是「有沒有這道 deadline」，不是正式值本身
        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 2);
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", CancellationToken.None));

        Assert.Contains("超過 2 秒", ex.Message);
        Assert.Contains("更小的網段", ex.Message);
    }

    /// <summary>呼叫端自己取消（管理員關掉分頁）不是掃描失敗，不該被包成假的錯誤訊息</summary>
    [Fact]
    public async Task 呼叫端取消_不轉成掃描失敗訊息()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(Empty()) { OnAuth = () => cts.Cancel() };

        var client = new SentinelRestDirectoryClient(Options(), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", cts.Token));
    }

    // ── 提早停頁：連續 K 頁無新主機即停止翻頁 ───────────────────────────────────

    /// <summary>
    /// 準備 10 頁（每頁 1000 筆達 pageSize 門檻），前 2 頁各有新主機，第 3~5 頁全是重複 IP →
    /// 連續 3 頁無新主機應於第 5 頁喊停，實際發出的分頁請求數為 5 頁而非 10 頁。
    /// </summary>
    [Fact]
    public async Task 連續3頁無新主機_停止翻頁且實際請求5頁()
    {
        // 準備 10 頁，每頁 1000 筆
        var page1 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var page2 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.2", (string?)"srv2")).ToArray();
        var duplicatePage = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.2", (string?)"srv2")).ToArray();

        var pages = new[]
        {
            page1,          // 第 1 頁：新主機 10.1.2.1 (consecutive=0)
            page2,          // 第 2 頁：新主機 10.1.2.2 (consecutive=0)
            duplicatePage,  // 第 3 頁：無新主機 (consecutive=1)
            duplicatePage,  // 第 4 頁：無新主機 (consecutive=2)
            duplicatePage,  // 第 5 頁：無新主機 (consecutive=3 → 停止)
            duplicatePage,  // 第 6 頁：不應被請求
            duplicatePage,  // 第 7 頁：不應被請求
            duplicatePage,  // 第 8 頁：不應被請求
            duplicatePage,  // 第 9 頁：不應被請求
            duplicatePage   // 第 10 頁：不應被請求
        };

        var handler = new ScriptedHandler(
            new JobScript(50_000, pages), // 主掃描第 1 輪
            Empty(),                      // 主掃描第 2 輪（因第 1 輪觸頂進殘差輪）
            Empty());                     // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        // 斷言分頁請求數確實為 5（而非 10）
        Assert.Equal(5, handler.PageRequestCount);
    }

    /// <summary>
    /// 提早停頁不等於已涵蓋：該輪取回數必小於 Found，Truncated 必然為 true，
    /// 且探索有繼續進殘差輪（第 2 輪查詢帶有 NOT 排除前一輪 IP）。
    /// </summary>
    [Fact]
    public async Task 提早停頁的那一輪仍視為觸頂_且繼續進殘差輪()
    {
        var page1 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var duplicatePage = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();

        var pages = new[]
        {
            page1,          // 第 1 頁：新主機 (consecutive=0)
            duplicatePage,  // 第 2 頁：無新主機 (consecutive=1)
            duplicatePage,  // 第 3 頁：無新主機 (consecutive=2)
            duplicatePage   // 第 4 頁：無新主機 (consecutive=3 → 停止)
        };

        var handler = new ScriptedHandler(
            new JobScript(50_000, pages),                                       // 第 1 輪：提早停頁，取回 4000 < 50000 觸頂
            new JobScript(1, ("10.1.2.9", "srv9")),                             // 第 2 輪（殘差輪）：發現新主機並掃完
            Empty());                                                           // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        // 兩輪主機聯集
        Assert.Equal(2, result.Hosts.Count);
        Assert.Contains(result.Hosts, h => h.IpAddress == "10.1.2.1");
        Assert.Contains(result.Hosts, h => h.IpAddress == "10.1.2.9");

        // 斷言殘差輪確實發出，且 filter 帶有第一輪的排除條件
        Assert.True(handler.Filters.Count >= 2);
        Assert.Contains("NOT (repip:10.1.2.1)", handler.Filters[1]);
    }

    /// <summary>
    /// 每頁都有新主機時不提早停止：10 頁每頁都有新 IP → 翻完全部 10 頁，不因提早停頁機制而少翻。
    /// </summary>
    [Fact]
    public async Task 每頁都有新主機_不提早停止_翻完全部頁數()
    {
        var pages = Enumerable.Range(1, 10)
            .Select(i => Enumerable.Range(0, 1000).Select(_ => ($"10.1.2.{i}", (string?)$"srv{i}")).ToArray())
            .ToArray();

        var handler = new ScriptedHandler(
            new JobScript(10_000, pages), // 主掃描第 1 輪：10 頁共 10000 筆，每頁都有新 IP
            Empty());                     // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(10, result.Hosts.Count);
        // 10 頁全部翻完，分頁請求數為 10
        Assert.Equal(10, handler.PageRequestCount);
    }

    /// <summary>
    /// 翻完且未觸頂時行為不變（回歸保護）：總筆數小於上限、每頁都有新主機 → Truncated 為 false、不進殘差輪。
    /// </summary>
    [Fact]
    public async Task 翻完且未觸頂_行為不變_不進殘差輪()
    {
        var page1 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var page2 = Enumerable.Range(0, 500).Select(_ => ("10.1.2.2", (string?)"srv2")).ToArray(); // 不足一頁筆數，為最後一頁

        var pages = new[] { page1, page2 };

        var handler = new ScriptedHandler(
            new JobScript(1500, pages), // 主掃描：Found=1500，取回 1500 筆未觸頂
            Empty());                   // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        // 主掃描未觸頂 → 只跑 1 輪主掃描 + 1 輪補充掃描，共 2 個 job
        Assert.Equal(2, handler.Filters.Count);
        Assert.Contains("NOT (repip:10.1.2.1 OR repip:10.1.2.2)", handler.Filters[1]);
    }

    /// <summary>
    /// PageObserver 為 null 時分頁行為與修改前完全相同（回歸保護）：既有不傳觀察者的呼叫端不受影響。
    /// </summary>
    [Fact]
    public async Task PageObserver為null_分頁行為與修改前完全相同()
    {
        var page1 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var duplicatePage = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();

        var pages = new[] { page1, duplicatePage, duplicatePage, duplicatePage, duplicatePage };

        var handler = new ScriptedHandler(new JobScript(5000, pages));

        await using var client = new SentinelClient(Server(), Options(), handler);
        // 不帶 PageObserver（預設為 null）
        var result = await client.SearchAsync(new SentinelSearchRequest(
            "filter", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            MaxResults: 5000, PageSize: 1000));

        // 雖然連續多頁無新主機，但 PageObserver 為 null，因此 5 頁全部取回
        Assert.Equal(5000, result.Events.Count);
        Assert.Equal(5, handler.PageRequestCount);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// 觀察者不會誤把上一輪已見的主機當新主機：第一輪已見 A、B，第二輪第 1~3 頁全是 A、B →
    /// 第二輪連續 3 頁計為無新主機並於第 3 頁停止翻頁。
    /// </summary>
    [Fact]
    public async Task 觀察者不會誤把上一輪已見主機當新主機()
    {
        // 第 1 輪：1 頁發現 10.1.2.1 與 10.1.2.2，但設定 Found=1000 觸發 Truncated 進殘差輪
        var round1Events = new[] { ("10.1.2.1", (string?)"srv1"), ("10.1.2.2", (string?)"srv2") };

        // 第 2 輪：4 頁，前 3 頁全是第 1 輪已見的主機，第 4 頁有新主機（不應被翻到）
        var round2Page1 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var round2Page2 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.2", (string?)"srv2")).ToArray();
        var round2Page3 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.1", (string?)"srv1")).ToArray();
        var round2Page4 = Enumerable.Range(0, 1000).Select(_ => ("10.1.2.3", (string?)"srv3")).ToArray();

        var round2Pages = new[] { round2Page1, round2Page2, round2Page3, round2Page4 };

        var handler = new ScriptedHandler(
            new JobScript(1000, round1Events), // 第 1 輪：取回 2 < Found 1000 觸頂（1 次分頁請求）
            new JobScript(5000, round2Pages),  // 第 2 輪：前 3 頁皆為已知主機，於第 3 頁喊停（3 次分頁請求）
            Empty());                          // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        // 第 1 輪 1 次 + 第 2 輪 3 次 = 4 次分頁請求（第 2 輪第 4 頁的新主機 10.1.2.3 未被翻到）
        Assert.Equal(4, handler.PageRequestCount);
    }

    // ── 逐段掃描（B2：/24 自動分割，task-B2-step1）───────────────────────────

    /// <summary>
    /// 跨段結果聯集正確：假 client 讓 192.168.0 回主機 A、192.168.7 回主機 B → 最終結果同時含 A 與 B。
    /// </summary>
    [Fact]
    public async Task 跨段結果聯集正確_不同子網段發現的主機皆入列()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.0.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.0.10", "srv-a"));
                if (filter.Contains("repip:192.168.7.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.7.20", "srv-b"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.0.10" && h.HostName == "srv-a");
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.7.20" && h.HostName == "srv-b");
    }

    /// <summary>
    /// 吵雜段壟斷不影響其他段：192.168.0 段觸頂且輪數用盡（有警告），192.168.9 段正常回一台安靜主機 →
    /// 該安靜主機仍在結果中。
    /// </summary>
    [Fact]
    public async Task 吵雜段壟斷不影響其他段_安靜主機仍被發現()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.0.*"))
                    return new JobScript(99_999, ("192.168.0.1", "noisy-host")); // 觸頂
                if (filter.Contains("repip:192.168.9.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.9.50", "quiet-host")); // 安靜主機
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None);

        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.9.50" && h.HostName == "quiet-host");
        Assert.Contains(result.Warnings, w => w.Contains("192.168.0") && w.Contains("清單可能不完整"));
    }

    /// <summary>
    /// 段內排除只帶該段 IP：192.168.0 段已見 3 台、192.168.9 段開始掃時，送出的 filter
    /// 不含 192.168.0.* 那三台的排除子句。
    /// </summary>
    [Fact]
    public async Task 段內排除只帶該段IP_不含其他段已見IP()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.0.*") && filter.Contains("rv150:System"))
                    return new JobScript(3, ("192.168.0.1", "h1"), ("192.168.0.2", "h2"), ("192.168.0.3", "h3"));
                return Empty();
            }
        };

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None);

        var seg9Filters = handler.Filters.Where(f => f.Contains("repip:192.168.9.*")).ToList();
        Assert.NotEmpty(seg9Filters);
        Assert.All(seg9Filters, f =>
        {
            Assert.DoesNotContain("192.168.0.1", f);
            Assert.DoesNotContain("192.168.0.2", f);
            Assert.DoesNotContain("192.168.0.3", f);
            Assert.DoesNotContain("NOT", f);
        });
    }

    /// <summary>
    /// 單段時 CoverageNote 不含分段敘述（回歸保護：使用者輸入 /24 時體感不變）。
    /// </summary>
    [Fact]
    public async Task 單段時CoverageNote不含分段敘述()
    {
        var handler = new ScriptedHandler(
            new JobScript(1, ("192.168.1.10", "srv-1")),
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.1", CancellationToken.None);

        Assert.DoesNotContain("子網段", result.CoverageNote);
        Assert.DoesNotContain("共掃", result.CoverageNote);
        Assert.Contains("掃描涵蓋「192.168.1.*」近 24 小時內有 System/Application 事件", result.CoverageNote);
    }

    /// <summary>
    /// 多段時 CoverageNote 說明段數與未收斂段。
    /// </summary>
    [Fact]
    public async Task 多段時CoverageNote說明段數與未收斂段()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.0.*"))
                    return new JobScript(99_999, ("192.168.0.1", "noisy0")); // 觸頂未收斂
                if (filter.Contains("repip:192.168.1.*"))
                    return new JobScript(99_999, ("192.168.1.1", "noisy1")); // 觸頂未收斂
                if (filter.Contains("repip:192.168.2.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.2.10", "srv-ok"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None);

        Assert.Contains("共掃 256 個子網段", result.CoverageNote);
        Assert.Contains("其中 2 段未能完整收斂（192.168.0、192.168.1）", result.CoverageNote);
    }

    /// <summary>
    /// 空段不產生警告：多數段回 0 筆 → warnings 不因此增加。
    /// </summary>
    [Fact]
    public async Task 空段不產生警告_多數段為空時warnings為空()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.10.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.10.5", "srv-10"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None);

        Assert.Single(result.Hosts);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// 多段預算用盡回部分結果＋警告：模擬預算耗盡 → 不擲例外，回已完成段的主機，
    /// 且 warnings 含「已完成 N/M 段」字樣。
    /// </summary>
    [Fact]
    public async Task 多段預算用盡回部分結果與警告_不擲例外()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.0.*") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.0.10", "srv-0"));
                return Empty();
            },
            OnJobCreated = jobCount =>
            {
                // 完成第 1 段（2 個 job）後，於建立第 3 個 job 時等待讓 1 秒預算逾時
                if (jobCount == 3)
                {
                    Thread.Sleep(1200);
                }
            }
        };

        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 1);
        var result = await client.ListHostsAsync(Server(), "192.168", CancellationToken.None);

        Assert.Single(result.Hosts);
        Assert.Equal("192.168.0.10", result.Hosts[0].IpAddress);
        Assert.Contains(result.Warnings, w => w.Contains("已完成 1/256 段") && w.Contains("未掃描的網段"));
    }

    /// <summary>
    /// 單段預算用盡仍擲例外（回歸保護）。
    /// </summary>
    [Fact]
    public async Task 單段預算用盡仍擲例外_回歸保護()
    {
        var handler = new ScriptedHandler(
            Enumerable.Range(0, 20).Select(i => new JobScript(9999, ($"10.1.2.{i}", $"h{i}"))).ToArray())
        {
            PageDelay = TimeSpan.FromMilliseconds(400)
        };

        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 2);
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", CancellationToken.None));

        Assert.Contains("超過 2 秒", ex.Message);
        Assert.Contains("更小的網段", ex.Message);
    }

    /// <summary>
    /// 段數超過上限擲 NetiqDiscoveryException，且訊息含建議改用較粗粒度的字樣。
    /// </summary>
    [Fact]
    public async Task 段數超過上限_擲NetiqDiscoveryException()
    {
        Assert.Equal(512, SentinelRestDirectoryClient.MaxSegments);

        var handler = new ScriptedHandler();
        var client = new SentinelRestDirectoryClient(Options(), handler);

        // 太寬的輸入（如只給 1 段）由 MinPrefixOctets 擋下並轉為 NetiqDiscoveryException
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10", CancellationToken.None));
        Assert.Contains("太籠統", ex.Message);
    }

    /// <summary>
    /// /26 段的 filter 是明列 OR、不含萬用字元。
    /// </summary>
    [Fact]
    public async Task 細粒度Slash26掃描送出明列OR_不含萬用字元()
    {
        var handler = new ScriptedHandler(
            new JobScript(1, ("192.168.7.10", "srv-1")),
            Empty(),
            Empty(),
            Empty(),
            Empty(),
            Empty(),
            Empty(),
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash26);

        Assert.Single(result.Hosts);
        Assert.NotEmpty(handler.Filters);

        var firstFilter = handler.Filters[0];
        Assert.Contains("repip:192.168.7.0 OR", firstFilter);
        Assert.DoesNotContain("192.168.7.*", firstFilter);
    }

    /// <summary>
    /// /24 段的 filter 仍是萬用字元（回歸保護）。
    /// </summary>
    [Fact]
    public async Task 預設Slash24掃描仍送出前綴萬用字元()
    {
        var handler = new ScriptedHandler(
            new JobScript(1, ("192.168.7.10", "srv-1")),
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash24);

        Assert.Single(result.Hosts);
        Assert.Contains("repip:192.168.7.*", handler.Filters[0]);
    }

    /// <summary>
    /// 不傳粒度時行為與 /24 完全相同（回歸保護）：比對送出的 filter 序列。
    /// </summary>
    [Fact]
    public async Task 不傳粒度時行為與Slash24完全相同_比對送出的filter序列()
    {
        var handlerDefault = new ScriptedHandler(
            new JobScript(1, ("192.168.7.10", "srv-1")),
            Empty());
        var handlerSlash24 = new ScriptedHandler(
            new JobScript(1, ("192.168.7.10", "srv-1")),
            Empty());

        var resultDefault = await new SentinelRestDirectoryClient(Options(), handlerDefault)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None);

        var resultSlash24 = await new SentinelRestDirectoryClient(Options(), handlerSlash24)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash24);

        Assert.Equal(handlerSlash24.Filters, handlerDefault.Filters);
        Assert.Equal(resultSlash24.CoverageNote, resultDefault.CoverageNote);
    }

    /// <summary>
    /// 段內排除跨 /25 兩段共享 /24 歸屬：第一段已見 2 台，第二段的 filter
    /// 帶有那 2 台的排除子句（因為同屬一個 /24）。
    /// </summary>
    [Fact]
    public async Task 細粒度段的排除只帶該段位址範圍內的已見主機()
    {
        // 排除清單依「這一段實際查詢的位址範圍」過濾，不是整個 /24：
        // 否則 /26 段（只查 64 個位址）會配上最多 254 個排除子句，其中大半根本不在查詢範圍內，
        // 把細粒度想省下來的子句預算再灌爆一次。
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                // 第一段（.0~.63）發現兩台
                if (filter.Contains("repip:192.168.7.0 OR") && filter.Contains("rv150:System"))
                    return new JobScript(2, ("192.168.7.10", "srv-a"), ("192.168.7.20", "srv-b"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash26);

        Assert.Equal(2, result.Hosts.Count);

        // 第二段（.64~.127）與第一段的位址範圍不重疊，不該帶第一段已見主機的排除子句
        var seg2Filters = handler.Filters.Where(f => f.Contains("repip:192.168.7.64 OR")).ToList();
        Assert.NotEmpty(seg2Filters);
        Assert.All(seg2Filters, f =>
        {
            // 第一段發現的 .10／.20 不在本段位址範圍內，本段因此完全不該有排除子句。
            // （不能直接斷言不含 "192.168.7.10" —— 本段合法包含 .100，會誤中子字串。）
            Assert.DoesNotContain("NOT (", f);
        });
    }

    /// <summary>
    /// CoverageNote 的未收斂段以 Label 呈現（/26 時應看到 x.x.x.0/26 形式而非只有 /24 前綴）。
    /// </summary>
    [Fact]
    public async Task CoverageNote未收斂段以Label呈現()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                if (filter.Contains("repip:192.168.7.0 OR"))
                    return new JobScript(99_999, ("192.168.7.1", "noisy0"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash26);

        Assert.Contains("共掃 4 個子網段", result.CoverageNote);
        Assert.Contains("192.168.7.0/26", result.CoverageNote);
    }

    /// <summary>
    /// 細粒度時安靜主機不被吵雜主機擠掉：/26 粒度下，
    /// 192.168.7.0/26 段觸頂用盡輪數（有警告），192.168.7.64/26 段的安靜主機仍在結果中。
    /// </summary>
    [Fact]
    public async Task 細粒度時安靜主機不被吵雜主機擠掉()
    {
        var handler = new ScriptedHandler
        {
            ScriptSelector = filter =>
            {
                // 192.168.7.0/26 觸頂用盡輪數
                if (filter.Contains("repip:192.168.7.0 OR"))
                    return new JobScript(99_999, ("192.168.7.1", "noisy-host"));
                // 192.168.7.64/26 回安靜主機
                if (filter.Contains("repip:192.168.7.64 OR") && filter.Contains("rv150:System"))
                    return new JobScript(1, ("192.168.7.70", "quiet-host"));
                return Empty();
            }
        };

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.7", CancellationToken.None, granularity: ScanGranularity.Slash26);

        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.7.70" && h.HostName == "quiet-host");
        Assert.Contains(result.Warnings, w => w.Contains("192.168.7.0/26") && w.Contains("清單可能不完整"));
    }

    /// <summary>
    /// 進度回報含段進度：多段掃描時 onProgress 收到的階段描述含 / 分數格式。
    /// </summary>
    [Fact]
    public async Task 多段掃描進度回報含段進度分數格式()
    {
        var progressMessages = new List<string>();
        var handler = new ScriptedHandler
        {
            ScriptSelector = _ => Empty()
        };

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168", CancellationToken.None,
                onProgress: (stage, count) => progressMessages.Add(stage));

        Assert.NotEmpty(progressMessages);
        Assert.Contains("掃描中 1/256（192.168.0）", progressMessages);
        Assert.Contains("掃描中 2/256（192.168.1）", progressMessages);
        Assert.Contains("掃描中 256/256（192.168.255）", progressMessages);
    }

    /// <summary>
    /// 單段進度回報維持既有文字（不出現「1/1」）。
    /// </summary>
    [Fact]
    public async Task 單段進度回報維持既有文字不出現分數()
    {
        var progressMessages = new List<string>();
        var handler = new ScriptedHandler(Empty(), Empty());

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "192.168.1", CancellationToken.None,
                onProgress: (stage, count) => progressMessages.Add(stage));

        Assert.Contains("主掃描中", progressMessages);
        Assert.Contains("主掃描完成", progressMessages);
        Assert.Contains("補充掃描中", progressMessages);
        Assert.DoesNotContain(progressMessages, m => m.Contains("1/1"));
        Assert.DoesNotContain(progressMessages, m => m.Contains("/"));
    }

    /// <summary>
    /// 依 job 建立順序回應的 Sentinel 替身：第 n 個建立的 job 套用第 n 個 <see cref="JobScript"/>。
    /// 同時記下每個 job 的 filter 與時間區間——殘差輪掃的斷言全部落在「送出去的查詢長什麼樣」，
    /// 那才是行為本身，比斷言文案穩固。
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly JobScript[] _scripts;
        private readonly Dictionary<int, JobScript> _resolvedScripts = new();
        private int _jobsCreated;

        public ScriptedHandler(params JobScript[] scripts) => _scripts = scripts;

        /// <summary>依 filter 動態決定 JobScript，若指定則優先於依序索引</summary>
        public Func<string, JobScript>? ScriptSelector { get; init; }

        /// <summary>當腳本耗盡時的預設 JobScript；若為 null 且超出範圍則擲例外</summary>
        public JobScript? DefaultScript { get; init; }

        /// <summary>當 job 建立時觸發（參數為已建立的 job 總數）</summary>
        public Action<int>? OnJobCreated { get; init; }

        /// <summary>每個 job 送出的 filter，依建立順序</summary>
        public List<string> Filters { get; } = new();

        /// <summary>每個 job 的查詢時間區間，依建立順序</summary>
        public List<(DateTimeOffset Start, DateTimeOffset End)> Windows { get; } = new();

        /// <summary>分頁請求的實際發出次數</summary>
        public int PageRequestCount { get; private set; }

        /// <summary>每次分頁請求的 URL</summary>
        public List<string> PageUrls { get; } = new();

        public HttpStatusCode AuthStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>模擬真實 Sentinel 的每頁往返延遲（實測約 1.7 秒），驗證總預算用</summary>
        public TimeSpan PageDelay { get; init; } = TimeSpan.Zero;

        public Action? OnAuth { get; init; }

        /// <summary>ESM 目錄端點的回應；null＝測試不預期它被呼叫（被呼叫會擲例外）</summary>
        public (string Body, HttpStatusCode Status)? EsmResponse { get; init; }

        /// <summary>ESM 端點有沒有被打過——驗證「開關關閉時完全不碰」</summary>
        public bool EsmRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Post && url == AuthUrl)
            {
                OnAuth?.Invoke();
                return AuthStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}")
                    : new HttpResponseMessage(AuthStatus);
            }

            if (request.Method == HttpMethod.Get && url.EndsWith(SentinelRestDirectoryClient.EsmEventSourcePath))
            {
                EsmRequested = true;
                if (EsmResponse == null)
                    throw new InvalidOperationException("本測試不預期 ESM 端點被呼叫（開關應為關閉）");

                var (body, status) = EsmResponse.Value;
                return status == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, body)
                    : new HttpResponseMessage(status) { Content = new StringContent(body) };
            }

            if (request.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var filter = doc.RootElement.GetProperty("filter").GetString()!;
                Filters.Add(filter);
                Windows.Add((
                    DateTimeOffset.Parse(doc.RootElement.GetProperty("start").GetString()!),
                    DateTimeOffset.Parse(doc.RootElement.GetProperty("end").GetString()!)));

                var index = _jobsCreated;
                _jobsCreated++;

                var script = ScriptSelector?.Invoke(filter)
                    ?? (index < _scripts.Length ? _scripts[index] : (DefaultScript ?? throw new InvalidOperationException($"測試腳本只給了 {_scripts.Length} 個 job，實際建立了第 {_jobsCreated} 個")));
                _resolvedScripts[index] = script;

                OnJobCreated?.Invoke(_jobsCreated);

                return Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{_jobsCreated}");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/results"))
            {
                if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, CancellationToken.None);
                PageRequestCount++;
                PageUrls.Add(url);
                var index = JobIndex(url);
                var script = _resolvedScripts[index];
                var page = ParsePageQuery(url);
                var pageEvents = (page >= 1 && page <= script.Pages.Length)
                    ? script.Pages[page - 1]
                    : Array.Empty<(string Ip, string? Name)>();
                return Json(HttpStatusCode.OK, EventsJson(pageEvents));
            }

            if (request.Method == HttpMethod.Get && url.StartsWith($"{JobCollectionUrl}/job"))
            {
                var index = JobIndex(url);
                if (!_resolvedScripts.TryGetValue(index, out var script))
                    throw new InvalidOperationException($"未找到第 {index + 1} 個 job 的解析腳本");

                var avail = script.TotalEventsCount;
                if (avail == 0)
                    return Json(HttpStatusCode.OK, $"{{\"status\":2,\"found\":{script.Found},\"avail\":0}}");

                return Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":{script.Found},\"avail\":{avail}," +
                    $"\"results\":{{\"@href\":\"{JobCollectionUrl}/job{index + 1}/results\"}}}}");
            }

            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);

            throw new InvalidOperationException($"未預期的請求：{request.Method} {url}");
        }

        /// <summary>從 .../jobN 或 .../jobN/results 取出 0-based 索引</summary>
        private static int JobIndex(string url)
        {
            var afterJob = url[(url.IndexOf("/job", StringComparison.Ordinal) + 4)..];
            var digits = new string(afterJob.TakeWhile(char.IsDigit).ToArray());
            return int.Parse(digits) - 1;
        }

        private static int ParsePageQuery(string url)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]page=(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : 1;
        }

        private static string EventsJson((string Ip, string? Name)[] events)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < events.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var (ip, name) = events[i];
                sb.Append($"{{\"repip\":\"{ip}\"");
                if (name != null) sb.Append($",\"sn\":\"{name}\"");
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json, string? location = null)
        {
            var resp = new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (location != null) resp.Headers.Location = new Uri(location);
            return resp;
        }
    }
}
