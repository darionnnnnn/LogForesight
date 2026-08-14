using System.Diagnostics;
using LogForesight.Web.Models.Dto;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 規模基準量測（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P0）——**預設不執行**，
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
    /// 「標記一個問題」的成長曲線（規劃 §8.5.1 的**改版後**對照）。
    ///
    /// 改版前這份資料是整份 JSON blob，每標記一次就要讀出整份、反序列化、改一筆、
    /// 再整份序列化寫回。實測（記錄於 docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.5.1）：
    ///   1 萬列 113ms／10 萬列 987ms／50 萬列 3,320ms／100 萬列 6,841ms（配置 2.4 GB），
    /// 完全線性，外推 6000 台 × 90 天的 324 萬列會撞上 .NET 的 2 GB 單一物件上限。
    ///
    /// P3 之後改真表，單筆標記是一列 UPDATE，這條曲線應該**與列數無關**（平的）。
    /// 若哪天有人把它改回整份讀改寫，這裡會立刻重新變成線性成長。
    /// </summary>
    [ScaleFact]
    public void 單筆標記成本不隨資料量成長()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small, _out.WriteLine);
        var store = data.Backend.IssueHandlingStore();
        var host = data.LivingHosts[0];

        _out.WriteLine("");
        _out.WriteLine("== issue_handling：單筆標記成本 vs 資料量（改版後應為平線）==");
        _out.WriteLine($"{"表內列數",12} | {"單次標記 ms",12} | {"配置 MB",10} | {"SaveMany 90 列 ms",18}");

        var baseDate = DateTime.Today.AddDays(-3000);
        var written = 0;

        foreach (var target in new[] { 10_000, 50_000, 100_000, 250_000, 500_000, 1_000_000 })
        {
            // 補到目標列數（只寫新增的部分）
            var batch = new List<IssueHandling>(target - written);
            for (var i = written; i < target; i++)
            {
                batch.Add(new IssueHandling
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
            store.SaveMany(batch);
            written = target;

            // 使用者標記一個問題的實際成本
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

            // N2 的對照：一次「案件建案回溯 90 天」規模的批次寫。改版前在 100 萬列的 blob 上
            // 要 11.3 秒（合併是 O(既有列 × 本次列)），改版後合併走索引、應與資料量無關
            var caseBatch = Enumerable.Range(0, 90).Select(d => new IssueHandling
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
            store.SaveMany(caseBatch);
            batchSw.Stop();

            _out.WriteLine($"{target,12:N0} | {sw.ElapsedMilliseconds,12:N0} | {allocated / 1024.0 / 1024.0,10:N1} | {batchSw.ElapsedMilliseconds,18:N0}");
        }
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

    /// <summary>
    /// **升級路徑的實測**（docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三、體檢 S-1／G1）。
    ///
    /// 造一份 100 萬列的**舊格式** issue_handling blob，走完整的升級流程：
    ///   1. 建立後端（＝服務啟動）必須在**秒級**完成，不能被搬移拖住（G1）；
    ///   2. 此時處理狀態應為唯讀（寫入被擋）；
    ///   3. 背景搬移完成後解除唯讀，且筆數正確。
    ///
    /// 沒有這條測試，S-1 與 G1 的修復都只是推論——而這一組正是全案唯一會丟資料的部分。
    /// </summary>
    [ScaleFact]
    public void 升級路徑_百萬列blob的遷移()
    {
        const int rows = 1_000_000;
        var dir = Path.Combine(Path.GetTempPath(), "lf-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "upgrade.db");

        StorageBackend NewBackend() => new(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" }, dir);

        try
        {
            _out.WriteLine($"== 升級路徑實測（舊格式 blob {rows:N0} 列）==");

            var sw = Stopwatch.StartNew();
            var legacy = new List<IssueHandling>(rows);
            var baseDate = DateTime.Today.AddDays(-3000);
            for (var i = 0; i < rows; i++)
            {
                // 鍵必須真的相異：(host, date, issueKey) 是唯一鍵，遷移會去重。
                // 用 i%6000／i%2000／i%1200 這種寫法會因為整除關係只產生 6000 種組合
                // （2000 與 1200 都整除 6000，後兩者完全由 i%6000 決定）——
                // 造出來的「100 萬列」會被正確地去重成 6000 列，測到的就不是規模。
                // 這裡讓主機取餘、日期取商，6000 × 167 ≈ 100 萬個相異 (主機, 日期) 組合。
                legacy.Add(new IssueHandling
                {
                    HostName = $"SRV-{i % 6000:D5}",
                    Date = baseDate.AddDays(i / 6000),
                    IssueKey = "System|Source-0001|1001|1",
                    Status = IssueHandlingStatuses.KnownNoise,
                    ActorId = 1, ActorAccount = "admin", Note = "舊資料", UpdatedAt = DateTime.Now
                });
            }

            var seeding = NewBackend();
            var json = System.Text.Json.JsonSerializer.Serialize(legacy, LfJsonOptions.Pretty);
            seeding.Blob("issue_handling").Mutate<object?>(_ => (json, null));
            // 還原成「舊版本的資料庫沒有遷移標記」
            seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));
            _out.WriteLine($"  造資料：blob {json.Length / 1024 / 1024:N0} MB（{json.Length:N0} 字元）— {sw.Elapsed.TotalSeconds:N1} 秒");
            legacy.Clear();

            // 1) 啟動必須是秒級（舊實作在這裡同步搬完，會撞服務 30 秒逾時）
            sw.Restart();
            var backend = NewBackend();
            sw.Stop();
            var startupMs = sw.ElapsedMilliseconds;

            var afterStartup = backend.HandlingMigrator.State;
            _out.WriteLine($"  啟動耗時：{startupMs:N0} ms；狀態＝{afterStartup.State}；擋寫入＝{afterStartup.ShouldBlockWrites}");
            Assert.Equal(HandlingMigrationState.Pending, afterStartup.State);
            Assert.True(afterStartup.ShouldBlockWrites);
            Assert.True(startupMs < 10_000, $"啟動花了 {startupMs} ms——遷移不該卡在啟動路徑上");

            // 2) 背景搬移
            sw.Restart();
            backend.HandlingMigrator.Run(CancellationToken.None);
            sw.Stop();

            var done = backend.HandlingMigrator.State;
            _out.WriteLine($"  搬移耗時：{sw.Elapsed.TotalSeconds:N1} 秒；狀態＝{done.State}；" +
                           $"issue_handling {done.IssueHandlingRows:N0} 列");

            Assert.Equal(HandlingMigrationState.Completed, done.State);
            Assert.False(done.ShouldBlockWrites);
            Assert.Equal(rows, done.IssueHandlingRows);
            Assert.False(string.IsNullOrWhiteSpace(backend.Blob("issue_handling").Read()));   // 搬完不刪
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* 暫存刪不掉不影響結論 */ }
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
