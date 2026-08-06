using System.Diagnostics;
using LogForesight.Web.Models.Dto;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 規模基準量測（docs/SCALE-ISSUE-FIRST-PLAN.md P0）——**預設不執行**，
/// 設 <c>LF_SCALE_BENCH=1</c> 才跑（見 <see cref="ScaleFactAttribute"/>）。
///
/// 這組數字是後續每一個階段的對照組：沒有它，「改好了」只是感覺。
/// 量的五條路徑對應體檢與規劃指認的五個根因，每一條都走**真正的 Service**
/// （見 <see cref="ScaleServices"/>），不是簡化過的替代實作。
///
/// 執行方式：
/// <code>
/// $env:LF_SCALE_BENCH=1; dotnet test --filter "FullyQualifiedName~ScaleBenchmarks"
/// </code>
/// 結果印在測試輸出（`--logger "console;verbosity=detailed"` 可見）。
/// </summary>
public class ScaleBenchmarks
{
    private readonly ITestOutputHelper _out;

    public ScaleBenchmarks(ITestOutputHelper output) => _out = output;

    [ScaleFact]
    public void 基準_2000台90天() => RunProfile(ScaleProfile.Baseline2000);

    [ScaleFact]
    public void 基準_6000台90天() => RunProfile(ScaleProfile.Target6000);

    /// <summary>
    /// 整份 blob 的成長曲線（規劃 §零 修正 2）：逐步加大 <c>issue_handling</c> 的列數，
    /// 量單次 <c>Mutate</c>（＝標記一個問題的實際成本）的耗時與配置量。
    ///
    /// 這是驗證「S2 是硬失敗而非線性劣化」的關鍵：序列化後的 C# string 逼近 .NET 的
    /// 2 GB 單一物件上限時會直接拋例外。**刻意不一路衝到炸**——量到明確的成長趨勢與
    /// 單次成本超過門檻即停，並把外推結果印出來，不必真的把測試機的記憶體吃光。
    /// </summary>
    [ScaleFact]
    public void 整份blob成長曲線()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small, _out.WriteLine);
        var store = new IssueHandlingStore(data.Backend.Blob("issue_handling"));
        var host = data.LivingHosts[0];

        _out.WriteLine("");
        _out.WriteLine("== issue_handling 整份 blob 成長曲線 ==");
        _out.WriteLine($"{"列數",12} | {"blob MB",10} | {"單次標記 ms",12} | {"配置 MB",10}");

        var rows = new List<IssueHandling>();
        var baseDate = DateTime.Today.AddDays(-3000);

        foreach (var target in new[] { 10_000, 50_000, 100_000, 250_000, 500_000, 1_000_000 })
        {
            while (rows.Count < target)
            {
                var i = rows.Count;
                rows.Add(new IssueHandling
                {
                    HostName = data.LivingHosts[i % data.LivingHosts.Count].HostName,
                    Date = baseDate.AddDays(i % 2000),
                    IssueKey = $"System|Source-{i % 1200:D4}|{1000 + i % 1200}|1",
                    Status = IssueHandlingStatuses.KnownNoise,
                    ActorId = 1,
                    ActorAccount = "scale-admin",
                    Note = "壓測資料集產生的處理說明。",
                    UpdatedAt = DateTime.Now
                });
            }

            // **刻意不用 SaveMany 建量**：SaveMany 的合併是 O(既有列 × 本次列)（規劃 N2），
            // 用它把 blob 灌到 25 萬列要跑數小時——那是另一個要修的問題，不該卡住這裡要量的東西。
            // 直接寫整份 JSON 建立量級，再量「使用者標記一個問題」的真實成本。
            data.Backend.Blob("issue_handling")
                .Mutate<object?>(_ => (System.Text.Json.JsonSerializer.Serialize(rows, LfJsonOptions.Pretty), null));

            // 一筆 Save → SaveMany(1 筆) → 一次整份讀→反序列化→合併→序列化→寫回
            var before = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            store.Save(new IssueHandling
            {
                HostName = host.HostName,
                Date = DateTime.Today,
                IssueKey = "System|Probe|9999|1",
                Status = IssueHandlingStatuses.Resolved,
                ActorId = 1,
                ActorAccount = "scale-admin",
                UpdatedAt = DateTime.Now
            });
            sw.Stop();
            var allocated = GC.GetTotalAllocatedBytes() - before;
            var blobMb = (data.Backend.Blob("issue_handling").Read()?.Length ?? 0) / 1024.0 / 1024.0;

            _out.WriteLine($"{target,12:N0} | {blobMb,10:N1} | {sw.ElapsedMilliseconds,12:N0} | {allocated / 1024.0 / 1024.0,10:N1}");

            // 單次標記已經超過 10 秒就沒有必要再往上量——結論已經成立
            if (sw.ElapsedMilliseconds > 10_000)
            {
                _out.WriteLine($"  → 單次標記已達 {sw.ElapsedMilliseconds:N0} ms，停止加量。");
                break;
            }
        }

        // N2 的獨立佐證：同一個 blob 上做一次「案件建案」規模的批次寫（90 天），
        // 量的是 SaveMany 的 O(既有列 × 本次列) 合併成本
        var batch = Enumerable.Range(0, 90).Select(d => new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-d),
            IssueKey = "System|Probe|8888|1",
            Status = IssueHandlingStatuses.InProgress,
            ActorId = 1,
            ActorAccount = "scale-admin",
            UpdatedAt = DateTime.Now
        }).ToList();

        var batchSw = Stopwatch.StartNew();
        store.SaveMany(batch);
        batchSw.Stop();
        _out.WriteLine($"  SaveMany（90 列，模擬 BuildCase 回溯 90 天）於 {rows.Count:N0} 列的 blob 上：{batchSw.ElapsedMilliseconds:N0} ms");
    }

    /// <summary>
    /// 主機別名展開的成長曲線（規劃根因 A／N1）。不需要資料庫，秒級即可跑完。
    ///
    /// 改寫前 <c>VisibleHostKeys</c> 對**每一台**可見主機呼叫一次 <c>Expand</c>，
    /// 而 <c>Expand</c> 內部又掃一次全部主機——是 O(N²)，且每次查詢重算一遍。
    /// 這裡量的是「建索引＋展開全部可見主機」的總成本，應該隨主機數**線性**成長；
    /// 若哪天有人把索引改回逐台掃描，這條曲線會立刻變成二次成長。
    /// </summary>
    [ScaleFact]
    public void 主機別名展開成長曲線()
    {
        _out.WriteLine("== 別名展開（建索引＋展開全部可見主機）==");
        _out.WriteLine($"{"主機數",10} | {"墓碑數",8} | {"耗時 ms",10} | {"展開鍵數",10}");

        foreach (var hostCount in new[] { 500, 1000, 2000, 4000, 6000 })
        {
            var tombstones = hostCount / 30;
            var hosts = new List<WebHost>(hostCount + tombstones);
            for (var i = 0; i < hostCount; i++)
                hosts.Add(new WebHost { HostId = i + 1, HostName = $"SRV-{i + 1:D5}", Active = true });
            for (var t = 0; t < tombstones; t++)
                hosts.Add(new WebHost
                {
                    HostId = hostCount + t + 1,
                    HostName = $"OLD-{t + 1:D5}",
                    Active = false,
                    MergedInto = t * 7 % hostCount + 1
                });

            var sw = Stopwatch.StartNew();
            var index = new HostAliasIndex(hosts);
            var keys = hosts.Where(h => h.Active)
                .SelectMany(h => index.Aliases(h.HostId))
                .DistinctBy(k => k.HostId)
                .ToList();
            sw.Stop();

            _out.WriteLine($"{hostCount,10:N0} | {tombstones,8:N0} | {sw.ElapsedMilliseconds,10:N0} | {keys.Count,10:N0}");
        }
    }

    private void RunProfile(ScaleProfile profile)
    {
        _out.WriteLine($"===== {profile} =====");
        _out.WriteLine("");
        _out.WriteLine("[產生資料]");

        var generateSw = Stopwatch.StartNew();
        using var data = ScaleDataSet.Generate(profile, _out.WriteLine);
        generateSw.Stop();

        var s = data.Stats;
        _out.WriteLine($"  合計：DB 檔 {s.DbFileBytes / 1024 / 1024:N0} MB、產生耗時 {generateSw.Elapsed.TotalSeconds:N1} 秒");
        _out.WriteLine("");

        var svc = new ScaleServices(data);
        var today = DateTime.Today;
        var from = today.AddDays(-profile.Days + 1);

        _out.WriteLine("[基準路徑]（每項量兩次，取第二次——第一次含 blob 首讀與 JIT）");
        _out.WriteLine($"{"路徑",-28} | {"第一次 ms",10} | {"第二次 ms",10} | 產出");

        Measure("儀表板 summary（7 天）", () =>
        {
            var dto = svc.Dashboard.GetSummary(7);
            return $"重點問題 {dto.TopIssues.Count} 筆、群組 {dto.GroupRisk.Count} 個";
        });

        Measure("報表 summary（30 天）", () =>
        {
            var dto = svc.Report.GetSummary(today.AddDays(-29), today);
            return $"問題排行 {dto.RankedIssueCount} 種、主機排行 {dto.RankedHostCount} 台";
        });

        Measure("問題查詢：明細（第 1 頁）", () =>
        {
            var page = svc.RecordList.Search(NewRequest(from, today));
            return $"符合 {page.Total:N0} 筆";
        });

        Measure("問題查詢：依主機", () =>
        {
            var page = svc.RecordList.SearchByHost(NewRequest(from, today));
            return $"符合 {page.Total:N0} 台";
        });

        Measure("問題查詢：依日期", () =>
        {
            var page = svc.RecordList.SearchByDate(NewRequest(from, today));
            return $"符合 {page.Total:N0} 天";
        });

        Measure("問題查詢：依問題", () =>
        {
            var page = svc.RecordList.SearchByIssue(NewRequest(from, today));
            return $"符合 {page.Total:N0} 種問題";
        });

        Measure("統一標記預覽（最普遍的問題）", () =>
        {
            var preview = svc.IssueCommands.PreviewBulkClose("DistributedCOM", 10016, from, today);
            return $"受影響 {preview.Hosts.Count:N0} 台";
        });

        Measure("夜間掛接（100 台）", () =>
        {
            var attached = 0;
            foreach (var host in data.LivingHosts.Take(100))
            {
                var issues = data.Signatures.Take(profile.IssuesPerRecord).ToList();
                attached += svc.CaseCoordinator.AttachNewDay(host.HostName, today, issues, DateTime.Now).AttachedCount;
            }
            return $"掛接 {attached:N0} 個問題（100 台，全量需 ×{profile.HostCount / 100.0:N0}）";
        });

        _out.WriteLine("");
        _out.WriteLine("[可見範圍解析]");
        Measure("VisibleHostKeys（經 Repository.Query）", () =>
        {
            var records = svc.Repository.Query(new RecordQueryFilter { From = today, To = today });
            return $"{records.Count:N0} 筆";
        });
    }

    private static RecordSearchRequest NewRequest(DateTime from, DateTime to) => new()
    {
        From = from,
        To = to,
        Page = 1,
        PageSize = 20
    };

    /// <summary>
    /// 第一次已經慢到這個程度就不跑第二次：第二次的用途是排除 blob 首讀與 JIT 的一次性成本，
    /// 但當單次就要一分鐘以上時，那點一次性成本早已不影響結論，重跑只是讓整份報告等兩倍。
    /// </summary>
    private const long SkipSecondRunAboveMs = 20_000;

    private void Measure(string name, Func<string> action)
    {
        string result;
        long first;
        long? second = null;
        try
        {
            var sw = Stopwatch.StartNew();
            result = action();
            first = sw.ElapsedMilliseconds;

            if (first < SkipSecondRunAboveMs)
            {
                sw.Restart();
                action();
                second = sw.ElapsedMilliseconds;
            }
        }
        catch (Exception ex)
        {
            // 量測本身失敗也是結論（例如 OOM）——不要讓一條路徑炸掉整份報告
            _out.WriteLine($"{name,-28} | {"—",10} | {"—",10} | 例外：{ex.GetType().Name}：{ex.Message}");
            return;
        }

        var secondText = second?.ToString("N0") ?? "（略過）";
        _out.WriteLine($"{name,-28} | {first,10:N0} | {secondText,10} | {result}");
    }
}
