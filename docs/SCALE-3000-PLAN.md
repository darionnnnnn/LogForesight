# 三千台規模化規劃（SCALE-3000）

目標規模自 300 台（現行實測環境）提高到 **2000–3000 台**，並支援**年度同期比較**
（區間上限自 90 天放寬到 366 天）。本規劃以「硬體不一定救得了寫法」為前提，
一律採嚴格假設調校。

## 1. 估算基準

硬體可調、程式碼的漸近成本不可調，所以下列數字全部取**保守上緣**：

| 符號 | 意義 | 取值 | 說明 |
|---|---|---|---|
| H | 主機數 | 3000 | 目標上限 |
| D | 查詢天數 | 90（儀表板）／732（報表含前期） | 366 天區間 + 等長比較期 |
| I | 每筆紀錄的 `lf_top_issues` 子列數 | 8 | **待實測校正**，見 §6 |
| R | 保留天數 | 760 | 年度同期比較的前提 |
| Bh | hosts blob 大小 | 4 MB | 3000 台 × 約 1.3 KB／台 JSON |

推導出的資料量：

| 表 | 列數（H=3000、R=760） |
|---|---|
| `lf_daily_records` | 228 萬 |
| `lf_top_issues` | 1824 萬 |
| `lf_issue_handling` | 隨人工標記成長，非 H×R 級 |

## 2. 瓶頸清單

依「不修就過不去」排序。每一項標明證據位置與 3000 台推估。

### B1 — 主機清單是無快取的 JSON blob（最高優先）

**證據**：`ServiceCollectionExtensions.cs:33` 註冊 `IHostStore` 為 Singleton `HostStore`；
`HostStore.cs:9` `GetAll() => Read()`；`JsonBlobCollection.cs:26` `Read() => Deserialize(_blob.Read())`；
`EfJsonBlobStore.Read()` 每次都對 `lf_blobs` 取 `nvarchar(max)` 全文。**沒有任何快取層。**

呼叫點 48 處，且每請求多次：`RecordListQueryService` 兩次（:73、:103）、
`HandlingHistoryQueryService` 兩次（:116、:166）、`IssueHandlingCommandService` 兩次、
`EfIssueAggregateQuery` 每次查詢建一次 `HostAliasIndex`、`HostVisibilityResolver` 數次。

**為什麼排第一**：成本與查詢區間、與天數**完全無關**——不論使用者選 7 天還是 366 天，
每個請求都要付。3000 台下單一請求反序列化十餘次 4 MB JSON ＝ 40–90 MB 配置與對應 GC 壓力，
**每個請求**。後面每一項的改善都會被這一項吃掉。

**寫入面同樣有問題**：`JsonBlobCollection.Mutate` 是整份 blob 讀→改→寫，
包在 `EfJsonBlobStore` 的行程內 `lock` ＋ DB 交易內。夜間分析逐台更新 `LastReportAt`
（`HostStore.cs:65/73/85`）＝ 3000 次全量 4 MB 重寫序列化在同一把鎖後面。
`HostStore.cs:178` 已有 `MutateBatch`，但未全面套用。

### B2 — 處理狀態比對是 O(N×M) 線性掃描

**證據**：`HandlingHistoryQueryService.FilterByScope`（:130）與 `GetTodo`（:180、:186）
都在 `foreach (var record in actionable)` 迴圈內對 `handlings` 做 `FirstOrDefault`、
對 `issueHandlings` 做 `Where`——兩者都是整份清單線性掃描。

**推估**：報表 366 天、3000 台，actionable 紀錄取一成 ＝ 11 萬筆，
`handlings`／`issueHandlings` 同量級。11 萬 × 11 萬 ＝ **10^10 次字串比對**。
這不是「慢」，是請求永不返回。

這一項與 B3 同屬報表／儀表板路徑，但它是**演算法層級**的問題，
即使把資料量壓下來也該修——改成 `Dictionary<(name, date), …>` 索引即可，
是純機械改動、無語意風險。

### B3 — 報表長區間 × 記憶體 KPI（硬失敗）

**證據**：`ReportService.GetSummary:39/47` 對本期與等長前期各呼叫一次
`_repository.QueryLightweight`；`BuildKpi`（:126）與 `BuildTrend`（:148）
吃完整的 `List<DailyAnalysisRecord>`。

`QueryLightweight`（`EfAnalysisRecordStore.cs:450`）省掉的只是 `ContentJson` 反序列化，
**不是列數**——它第二趟把這些 record_id 的全部 `lf_top_issues` 子列撈回、
逐列物化成 `LogIssueSignature`。

| 場景 | H | D | 主列 | `LogIssueSignature` 物件 |
|---|---|---|---|---|
| 儀表板 90 天 | 300 | 90 | 2.7 萬 | 21.6 萬 |
| 儀表板 90 天 | 3000 | 90 | 27 萬 | 216 萬 |
| 報表 366 天＋前期 | 3000 | 732 | 220 萬 | **1760 萬** |

1760 萬個帶字串欄位的物件，每個保守 150–200 bytes ＝ **2.6–3.5 GB 瞬間配置**，
而同一行程還跑著夜間分析（本專案定案不拆 worker）。這是 OOM，不是變慢。

### B4 — 儀表板剩餘的記憶體彙總

**證據**：`DashboardService.cs:59` `QueryLightweight(new RecordQueryFilter { From = from })`
（**無 `To`**，靠 `DashboardController.cs:30` 的 `Math.Clamp(days, 1, 90)` 擋住）；
之後 `BuildHostRanking`（:78）、`HighRiskDays`／`MediumRiskDays`（:102-103）、
`BuildGroupRisk`（:150）全在記憶體。

3000 台 ＝ 216 萬個問題物件，**每次進首頁**。首頁是全站開啟頻率最高的一頁。

`Categories` 與 `TopIssues` 已走 SQL 聚合（`AggregateByCategory`／`IssueRankingBuilder.Build`），
路子現成，剩下這幾個沒跟上。

### B5 — 合併主機在儀表板未解析

**證據**：`DashboardService.cs:78` 用 `visibleHosts.ToDictionary(h => h.HostName)`、
`BuildGroupRisk:154` 用 `records.GroupBy(r => r.Host)`——兩者一致，但都不經
`HostLookup`（`HostIdentity.cs:180`，會把墓碑列解析成存活主機）。

墓碑列的紀錄從主機排行與群組風險雙雙消失，卻仍計入 `dto.HighRiskDays`（:102，直接數 records），
同一頁上的分項加總與總數不符。主機合併在 3000 台規模是必用功能，這個落差會被放大。

`EfIssueAggregateQuery.ActionableOccurrences:381` 已經用 `HostAliasIndex` 做對了，
B4 下推 SQL 時沿用同一套即可，不必另立規則。

### B6 — 保留期 × 儲存量與 YoY 的取捨

`SystemSettings.DefaultRetentionDays = 120`（`SystemSettings.cs:146`）。
去年同期比較需要 R ≥ 760，直接是六倍。

**主要成本在 `lf_daily_records.ContentJson`（nvarchar(max)）**，不在列數。
而年度同期比較需要的 KPI 與趨勢**沒有一項讀 ContentJson**——它們全部來自抽出欄
（`headline`／`error_count`／`warning_count`／`risk_level`／`data_incomplete`／
`security_log_available`，回饋十九輪批次B 建立）與 `lf_top_issues`。

### B7 — 比較基準是「緊鄰前期」而非「去年同期」

**證據**：`ReportService.cs:43-46`

```csharp
var span = (to.Date - from.Date).Days + 1;
var previousTo = from.Date.AddDays(-1);
var previousFrom = previousTo.AddDays(-span + 1);
```

選 `2026-01-01 ~ 2026-08-15`（227 天），比較期是 `2025-06-18 ~ 2025-12-31`，
不是 `2025-01-01 ~ 2025-08-15`。對 7／30／90 天快捷鍵這是正確設計
（註解已說明「等長才可比」），對年度同期比較則會把季節性混進去。

### B8 — AI 段與初次回補

**穩態沒問題**：實測 3000 主機日／小時；3000 台每日新增 3000 個主機日 ＝ 約一小時。
`NetiqPipelineService.cs:151` 的單一消費者足夠，**FIFO 不需要改**。

**初次回補不是穩態**：`InitialHistoryDays` 預設 120（`SystemSettings.cs:142`），
3000 台 ＝ 36 萬個主機日 ＝ 約 **120 小時／五天**。期間站台數字全部不完整。

`AiFollowupQueue.Capacity = 200` 硬寫死（`AiFollowupQueue.cs:21`），回補期間必然長時間背壓。
畫面會誠實顯示「暫停中」，行為正確，但容量該可調。

### B9 — 啟動路徑與逾時（低風險收尾）

- `SchemaUpgrader.SeedIssueFirstSeenIfEmpty`（:150）對 `lf_top_issues` 全表 GROUP BY，
  在 `StorageBackend` 建構子內、`app.Run()` 之前。3000 台是千萬列級，
  且現有索引 `(record_date, source_name, event_id)` 服務不了
  `GROUP BY UPPER(source_name), event_id`——必然全表掃。撞 SCM 30 秒逾時的風險成立
  （同一支旁邊 `StorageBackend.cs:86` 已為 `HandlingMigrator` 寫下同樣的警語）。
- `StorageBackend.cs:56` `UseSqlServer` 未設 `CommandTimeout`，走 ADO.NET 預設 30 秒。
  分析與前景共用同一個 DB，3000 台下前景逾時會從偶發變常態——使用者看到的是紅字錯誤，
  不是「慢」，會抵消掉「執行中告示」想達成的效果。

### B10 — 與規模無關的既有缺陷（獨立可併）

| 項目 | 證據 | 影響 |
|---|---|---|
| 處理歷程 LogId 重號 | `EfRecordHandlingStore.cs:32` 實例層級快取；Web 端 Singleton（`ServiceCollectionExtensions.cs:53`）vs 分析端 `new StorageBackend`（`AnalysisOrchestrator.cs:147`） | LogId 未對外顯示，僅影響 `OrderBy(CreatedAt).ThenBy(LogId)` 的同秒次序 |
| 權限偵測盲區 | `PermissionMonitorService.cs:107` 只有 `Console.WriteLine`，無配對 `Log.*` | console 退場後「無法讀取 Administrators 群組成員」完全消失，違反「沒查 ≠ 沒事」 |
| 儀表板輪詢不停 | `dashboard.js` `refreshRunActivity` 在 `!isRunning` 只 `replaceChildren()`，無 `clearInterval` | 與上方註解「跑完自動停止輪詢」矛盾，長期每 30 秒打 API |
| 報告路徑未淨化 | `FileReportSink.cs:18` `Path.Combine(_exportDir, host)`，host 來自 NetIQ 探索 | 含 `..` 或分隔字元時寫出 export 之外 |
| 重複呼叫 | `ReportService.cs:88-89` `StatsPending()` 呼叫兩次 | 純整理 |
| 死程式碼 | Core 內 25 處 `Console.WriteLine`（8 個檔案），多數有 Log 配對 | console 退場後無輸出目的地 |
| 部署韌性 | `README.md:507` `sc create` 段無 recovery、無 `ServicesPipeTimeout` | 服務掛掉後不自動重啟 |

---

## 2.5 本輪完成紀錄（2026-08-16，`feature/scale-3000`）

S1～S6 全數完成，測試 2086 → 2159 全綠。以下只記**與原規劃不同的決策**與**驗收所得**，
逐段的機制細節見程式碼註解與 `docs/DB-SPEC.md`。

### 與原規劃不同的三個決策

| 項目 | 原規劃 | 實際採用 | 理由 |
|---|---|---|---|
| S3-b 處理狀態 | (a) 物化三欄 ＋ 六個失效觸發點 ＋ 漂移偵測 | **(b) 查詢時下推 SQL** | 問題鍵的五個組成欄位 `lf_top_issues` 全都有，`Derive` 可用 EF LINQ 表達成 GROUP BY。沒有儲存狀態就沒有失效觸發點、沒有漂移、不需要回填。同步保證改由一支等價性測試承擔 |
| S5 首見日種子 | 搬背景 ＋ 加閘門維持時序 | **改成「較早者勝」的冪等合併** | 逐日 upsert 本來就是較早者勝；把種子改成同一套語意後，時序需求整個消失，不需要閘門。排序問題轉成可交換操作，比加鎖維持順序更穩 |
| AI 佇列容量可設定 | 規劃列入 | **不做** | `NetiqOptions` 有完整 DTO ＋ 前端鏈，為一個只在初次上線有意義的旋鈕拉整條鏈不成比例；實測也證明 AI 不是瓶頸，背壓是正確行為不是缺陷 |

### 實測數字（`LogForesight.Tests/Scale/RetentionScopeBenchmarks.cs`）

500 台 × 200 天 ＝ 10 萬筆紀錄、80 萬問題子列：

| 量測 | 值 |
|---|---|
| `ContentJson` 平均 | **5.3 KB/筆** → 3000 台留兩年約 12 GB，詳情只留 120 天約 1.9 GB |
| 清理範圍（限縮本機 vs 未限縮） | **0 筆 vs 39,500 筆** |
| `Prune` 吞吐 | 39,500 筆／34.6 秒（約 1,140 列/秒，含子表刪除） |
| `PruneDetails` 吞吐 | 30,000 筆／5.0 秒（UPDATE 比 cascade delete 快 5 倍） |

單次清除上限 5 萬列 → 首次啟用時積壓分多晚排掉。3000 台跑滿一年未清約 73 萬列，
約需 15 個晚上、每晚約 44 秒。

### 驗收抓到的問題（依嚴重度）

| 抓到什麼 | 為什麼危險 |
|---|---|
| **保留期清理只作用於本機**（既有 bug） | `AnalysisOrchestrator` 用綁定本機的 store 清理，NetIQ 數千台主機的紀錄從未被清；`DB-SPEC` 的保留策略表寫的卻是全表適用，文件與實作長期不一致 |
| **`PruneDetails` 寫入空字串會讓讀取端拋 `JsonException`** | `Deserialize` 對空字串是丟例外不是回 null，既有的 `?? new()` 接不到。清理一開啟，詳情頁與所有 Query 路徑都會 500 |
| **`FlushHostTouches` 的競態** | 「複製再 Clear」會吃掉別台 Sentinel 剛寫入的項目，主機 `LastReportAt` 靜默消失、畫面顯示成無回報，沒有任何錯誤訊息 |
| **快取命中也搶鎖** | 連命中都進 `lock` 且鎖內含 SQL，單請求十幾次 `GetAll()` 加併發會排隊，換來新的咽喉點、抵消加快取的目的 |
| **主機排行先聚合再反聚合** | 把聚合展開成「每風險日一個假紀錄」，上百萬物件；且假紀錄沒有關聯訊號與標題，那幾欄靜默變空，而當時測試只驗天數所以全綠 |
| 兩處呼叫端修改 `GetAll()` 取得的快取物件 | `Upsert` 未提交甚至失敗時，變更已對所有讀取者可見，而 `Active` 影響主機可見性 |

### 委派品質（agy）

九段委派、一輪回饋（S3-a2 實作正確但測試一支未加）。另外一次刪掉 13 支既有測試
（含與任務無關的 `HasOverdueIssue`），已還原並把新測試移到獨立檔案。
**「測試總數必須大於 N」這條數字化驗收是唯一抓到後者的機制**。

### 已知限制（未做，不是漏做）

- **SqlServer 未實測**：測試只跑得到 SQLite。S5 與 S3-b2 的 SQL 已刻意收斂到
  兩個 provider 都無爭議的語法（避開 `HAVING` 引用外層／未分組欄位、避開手寫字串串接），
  但這是「降低風險」不是「已驗證」。
- **前端未做瀏覽器實測**：相關頁面在 `[Authorize]` 之後，無登入憑證。
  已做 JS 語法檢查與全套測試。
- **快取與 SQL 下推的實際效益未實機量測**：`SqlPerformanceMonitor` 會記錄
  `blob:hosts:Read` 與各聚合耗時，實測時可據此確認。
- **`scope != all` 的報表路徑仍在記憶體**：母體只有高／中風險日、量級小一階，
  且已受 366 天上限約束。真的成為瓶頸再處理。
- **`BatchRunStore` 有與 `EfRecordHandlingStore` 相同的雙實例序號結構**
  （Web Singleton ＋ 分析端自建），且建構式做全量讀。目前 Web 端唯讀所以不會撞號，
  但屬定時炸彈——哪天有人在 Web 端加寫入路徑就會靜默重號。

---

## 3. 分階段規劃

分支：自 `dev` 開 `feature/scale-3000`。各階段獨立可測，順序不可換——
S1 不先做，S3／S4 的量測全部失真。

### S1 — 主機清單快取層（對應 B1）　**已完成**

實作分三段委派執行，各段獨立驗收：`lf_blobs` 加 `version` 欄 → 快取層 → 寫入面批次化。
測試 2086 → 2099 全綠。機制的現行事實見 `docs/DB-SPEC.md` §F，以下只記決策與驗收所得。

**版本權杖不重用 `updated_at`**：它已是 EF 並發權杖且值取自 `DateTime.Now`，
Windows 解析度約 15.6 ms，同 tick 寫入會拿到相同戳記。主機清單是授權可見範圍的來源，
漏更新等於可能看到不該看到的主機——所以另立 `version` 欄，並且不動既有的並發權杖機制
（那是正確性機制，不該夾在效能改動裡碰）。

**快取放在 `JsonBlobCollection` 而非 `EfJsonBlobStore`**：昂貴的是反序列化不只是 DB 讀取，
放在 blob 層只省掉讀取，4 MB JSON 照樣每次重新解析。

**驗收抓到的三件事**：

| 抓到什麼 | 根因 | 處置 |
|---|---|---|
| `NetiqOrphanSweeper`／`SentinelIdBackfiller` 對 `GetAll()` 的結果直接改物件屬性再逐台 `Upsert` | 加了快取之後，這等於直接改到快取內容——`Upsert` 尚未提交、甚至提交失敗時，變更已對所有讀取者可見，而 `Active` 影響主機可見性 | 兩處都改走 `MutateBatch`（它的 mutation 拿到的是當場反序列化的新清單），與 N+1 一併解決 |
| `FlushHostTouches` 用「複製一份再 `Clear()`」取出緩衝 | 多台 Sentinel 平行處理、各自跑完都會 flush，`Clear()` 會把複製之後由別台寫入的新項目一併刪掉——那台主機的 `LastReportAt` 靜默消失、畫面顯示成無回報 | 改為逐鍵 `TryRemove`，只移除自己真的取走的鍵 |
| `lf_blobs.version` 的 schema 升級路徑無測試 | SQLite 測試走 `EnsureCreated`，欄位從 EF 模型建出，`SchemaUpgrader` 那行在測試中從未執行——錯了會是新環境全綠、只有正式環境炸 | 比照該檔既有的「舊 schema 缺某欄」樣式補測試 |

**踩過的坑，不要改回去**：快取命中時回傳的是淺複製。看起來像可以省掉的一次配置，
但拿掉之後呼叫端對清單做 `Clear()`／排序會直接改壞快取（已有測試釘住）。

**刻意不動的**：`SetHighVolume` 逐台寫入（既有 `if (!IsHighVolume)` 護欄已避免重複寫入，
觸發頻率極低）、`AnalysisOrchestrator.Touch`（一趟執行一次）、Web 端單筆 `Upsert`。

**尚未驗證**：快取對真實請求的效果只有程式碼推論與單元測試，沒有實機量測。
`SqlPerformanceMonitor` 會記錄 `blob:hosts:Read`，實測時可據此確認單一請求的次數是否降到 0–1。

---

**讀取面**：在 `HostStore` 之上加一層版本感知快取。`lf_blobs` 已有 `UpdatedAt` 欄
（`EfJsonBlobStore` 的 `BlobRow`），以它當版本戳：

- 快取持有 `(反序列化結果, 版本戳)`。
- `GetAll()` 先以 `SELECT updated_at WHERE blob_key = 'hosts'` 探測（單列走 PK，微秒級），
  戳記未變就回快取。
- **必須跨行程正確**：Web 與分析同行程時靠記憶體即可，但本專案保留了分析獨立執行的可能
  （`AnalysisOrchestrator` 自建 `StorageBackend`、schema 用跨行程 mutex），
  所以不能用「只有自己寫入才失效」的假設——一律走版本戳探測。
- 快取層放在 `JsonBlobCollection` 還是只給 `HostStore`：**只給 hosts／host_groups／
  group_access 三個**。users 等其他 blob 不隨主機數成長，加快取只增加失效正確性風險。

**寫入面**：盤點分析路徑上逐台呼叫 `Mutate` 的位置（`HostStore.cs:53/80` 的
`LastReportAt` 更新是主要來源），改走既有的 `MutateBatch`——一趟分析一次重寫，
不是每台一次。

**驗收**：
- 單一儀表板請求的 `blob:hosts:Read` 次數自十餘次降到 0–1 次（`SqlPerformanceMonitor` 可量）。
- 新增測試：blob 被另一個 `HostStore` 實例寫入後，快取實例的下一次 `GetAll()` 讀到新值。
- 新增測試：`MutateBatch` 路徑下 N 台主機的 `LastReportAt` 更新只觸發一次 blob 寫入。

**風險**：快取失效漏判會讓 Web 看到過期主機清單，而主機清單是**授權可見範圍的來源**——
過期＝可能看到不該看到的主機。因此版本戳探測不可省略、不可加 TTL 寬限。

**回滾**：快取層是獨立的裝飾類別，DI 改回原本註冊即回到現況。

### S2 — 保留期兩層拆分（對應 B6）

把單一 `RetentionDays` 拆成兩層：

| 層 | 內容 | 建議預設 | 理由 |
|---|---|---|---|
| 詳情層 | `lf_daily_records.ContentJson` | 120 天 | 只有風險日詳情頁讀它 |
| 統計層 | 抽出欄、`lf_top_issues`、`lf_issue_first_seen` | 760 天 | 年度同期比較的資料來源 |

實作為「詳情層過期時把 `ContentJson` 設為 NULL 並標記，整列不刪」——
列還在，抽出欄還在，只是詳情不可得。

**必須同時處理的誠實申報**：詳情頁對「統計還在、詳情已過期」的紀錄要明說
（沿用既有的 `data_incomplete`／「統計中」那套語彙，不要靜默顯示空白）。

**設定面**：新增 `DetailRetentionDays`，驗證 `1 <= DetailRetentionDays <= RetentionDays`
（同 `RiskyEventRetentionDays` 的既有慣例，`SystemSettingsService.cs:131`）。
依專案紅線，此設定必須有消費端——消費端就是 prune 作業與詳情頁的過期提示。

**設定頁的刪除警語（定案）**：`Views/Pages/Settings.cshtml` 的「資料保留」分頁
（:282，四個欄位在 :300／:308／:316／:324）目前的 popover 說明只寫「自動清除」，
沒有講清楚是**永久刪除、不可復原**。本階段一併改：

- 分頁頂端加一則常駐警語（非 popover，不必點開就看得見）：
  明確寫出「超過保留天數的資料會在每次批次啟動時**永久刪除，無法復原**」。
- 四個欄位（歷史資料／執行歷程／稽核紀錄／風險 log 暫存）＋新增的詳情保留，
  各自的 popover 補上刪除範圍與不可復原字樣，措辭統一。
- **調小任一保留天數時要二次確認**：跳出對話框列出「此變更將立即使 N 天以前的
  X 類資料進入刪除範圍」，使用者確認才送出。調大不需要確認。
- 新增的詳情保留欄位要說明它與歷史資料保留的關係：
  「詳情刪除後，該日的統計、風險等級與問題清單仍然保留，只有原始樣本訊息不可再查看」。

**驗收**：
- 抽出欄與 `lf_top_issues` 在超過 `DetailRetentionDays` 後仍可查、KPI 數字不變。
- 詳情頁對已清詳情的紀錄顯示明確提示，不是空白。
- 儲存量：`SUM(DATALENGTH(content_json))` 在 prune 後降到約 120/760。
- 設定頁：調小保留天數觸發確認對話框；取消則設定不變。

**風險**：這是**不可逆的資料清除**。上線前必須確認 §6 的實測數字，
且第一次執行要能 dry-run（只報告會清多少，不真的清）。

**回滾**：設定調回等於 `RetentionDays` 即停止清除，但**已清的詳情回不來**。

### S3 — 報表 SQL 化 ＋ 區間上限 ＋ 同期比較（對應 B2/B3/B7）

三件事必須同一批做——區間放寬到 366 天的前提是先把記憶體路徑拆掉。

**S3-a：KPI／趨勢下推 SQL。** `BuildKpi` 與 `BuildTrend` 全部是 COUNT／SUM／
COUNT DISTINCT，可直接對抽出欄聚合：

| 欄位 | 下推方式 |
|---|---|
| `TotalIssues` | `COUNT(*)` on `lf_top_issues` join 期間內紀錄 |
| `HighRiskDays` | `COUNT(*) WHERE risk_level = 'High'` |
| `AffectedHosts` | `COUNT(DISTINCT host_id)` where actionable（**用 host_id 不是 host name**，順帶修 B5） |
| `CoverageGapDays` | `COUNT(*) WHERE data_incomplete = 1 OR security_log_available = 0`（`DailyAnalysisRecord.cs:132` 的定義） |
| `Trend` 逐日 | `GROUP BY record_date`，缺日在 C# 端補 0（維持既有「不略過空白日」的行為） |

**S3-b：`handlingScope` 物化（定案採方案 a）。** 這是全案複雜度最高的一項，設計如下。

#### 可行性判定：可行，關鍵前提已驗證

`Derive`（`DayHandlingDerivation.cs:29`）的輸出**與時間無關**——這是物化成立的必要條件，
而它成立的理由要寫下來，因為它並不顯然：`observing` 狀態在別處（`IssueGroupStatusResolver.cs:47-48`）
會依 `IsObservationExpired` 在 Processing／Open 之間翻轉，但 `Derive` 刻意**不看到期**
（`DayHandlingDerivation.cs:47-48`：「不論觀察中或已到期都是『有人在管』，到期只是加上逾期提示」）。
所以日狀態本身可以安全物化。

**若日後有人讓 `Derive` 開始看 `today`，這個物化立刻失效。** 該處要留下註解與測試護欄。

#### 需要三個欄位，不是一個

盤點 `FilterByScope`（四個 scope）與 `GetTodo`（四個計數）的全部輸出：

| 新欄位 | 型別 | 服務對象 | 說明 |
|---|---|---|---|
| `day_handling_status` | nvarchar(30) NULL | `unresolved`／`open` scope、`GetTodo` 三態計數 | 存**內部**狀態不是 `ExternalOf` 後的三態——內部狀態是嚴格更多的資訊（`wont_fix`／`false_positive`／`known_noise` 各自可辨），`ExternalOf` 是它的純函式，讀取時再映射即可，不損失語意 |
| `day_has_handler` | bit | `unassigned` scope | 對應 `HasHandler`（:149）：日層級 `HandlerId` 或當日問題屬某進行中案件 |
| `day_overdue_after` | datetime2 NULL | `GetTodo.OverdueCount` | 見下 |

**逾期的時間相依性怎麼消掉**：`OverdueCount`（:207-213）依賴 `DateTime.Today`，
表面上不可物化。作法是**把時間相依性收斂成一個門檻日**——
`day_overdue_after` ＝「這一天從哪一日起算逾期」，取下列各項的最小值：

- 日層級：`handling.DueDate`，若該日 `IsUnresolved`（注意用**內部**狀態判定，同 :14）
- 問題層級：`in_progress` 標記的 `DueDate`
- 問題層級：`observing` 標記的觀察到期日

之後 `逾期 ⟺ day_overdue_after < @today`，一句 SQL 述詞，不需要每天重算。

#### 六個失效觸發點

日狀態由多個來源推導，**任何改變推導輸入的寫入都要更新這三欄**。完整盤點：

| # | 觸發 | 影響範圍 | 路徑 |
|---|---|---|---|
| 1 | 分析寫入／覆寫該日紀錄（`TopIssues` 變動） | 該主機該日 | 逐日寫入 |
| 2 | 日層級標記（狀態／處理人／期限） | 該主機該日 | `DayHandlingCommandService` |
| 3 | 問題層級標記 | 該主機該日 | `IssueHandlingCommandService` |
| 4 | 案件掛接／結案 | 該主機的多個日期 | `IssueCaseCoordinator.AttachNewDay`（:200）、案件結案 |
| 5 | `UnhandledSeverities` 設定變更 | **全表** | `SystemSettingsService` |
| 6 | 主機合併 | 該主機全部日期 | 合併改變 `NameOf(record)`，等於換一組適用的 handling 列 |

第 5、6 兩項是這個設計最容易被漏掉的：它們**不經過任何逐日寫入路徑**，
但會讓既有的物化值整片失效。第 6 項尤其隱蔽——處理狀態以**主機名稱**為鍵，
而合併改變了紀錄對應到哪個名稱。

#### 防漂移設計（本案的主要風險控制）

這個專案已經有「改共用欄位漏改讀取端」的前科（見 `docs/archive/README.md` 索引中
欄位漂移那一輪），六個觸發點分散實作幾乎必然重演。因此：

1. **單一咽喉點**：新增 `DayHandlingProjection`，只暴露
   `Recompute(hostNameKey, IReadOnlyCollection<DateTime> dates)` 一個方法。
   觸發點 1–4、6 全部只呼叫它，**不允許任何地方直接寫這三個欄位**。
2. **全表失效走既有機制**：觸發點 5 沿用 `extract_version` 遞增 ＋ `DailyRecordBackfiller`
   （回饋十九輪批次B 已建立的同一套），回填期間沿用既有的「統計中」誠實標示。
   觸發點 6 若合併台數多，同樣走這條而非逐日重算。
3. **漂移偵測**：夜間分析後抽樣重算並與物化值比對，不一致就記 Warn 並計數；
   計數surfaced 到健康檢查頁。這與本專案「沒查 ≠ 沒事」的既有原則一致——
   物化欄的正確性不能只靠「我們有記得呼叫」。
4. **測試護欄**：對每一個觸發點各一個測試，斷言「操作後物化值 == 當場重算值」。
   另加一個測試斷言 `Derive` 的簽章不含時間參數（防止未來有人加 `today` 進去）。

#### 索引

`(day_handling_status, record_date)` 與 `(day_overdue_after)`。
`day_has_handler` 選擇度低（多數為 false），不單獨建索引，
靠 `record_date` 範圍先窄化。

#### 為什麼不採 (b)

(b)（把 O(N×M) 線性掃描換成 Dictionary 索引）成本低、無語意風險，
但仍需把期間內全部紀錄與全部處理狀態載進記憶體，B3 的 1760 萬物件原封不動。
366 天區間下撐不住。

**但 (b) 的內容仍要做**——它是 (a) 的過渡與退路：先把索引化改掉，
`FilterByScope`／`GetTodo` 立刻從 10^10 降到 O(N)，而後 (a) 落地時這兩支才被 SQL 述詞取代。
若 (a) 因漂移問題需要暫時退場，(b) 就是可用的回滾目標。

**S3-c：區間上限與同期比較。**

- 後端 `ReportsController.Summary` 加 clamp：上限 **366 天**。超過回明確錯誤
  （「查詢範圍過大，請縮小期間」），**不靜默截斷**——靜默截斷會讓使用者以為看到的是全年。
- 新增 `compare` 參數：`previous`（預設，維持現行等長前期）／`yoy`（去年同期，
  `previousFrom = from.AddYears(-1)`、`previousTo = to.AddYears(-1)`）。
- 前端 `Reports.cshtml` 的 date input 補 `min`／`max`；`reports.js` 加比較基準切換；
  區間 ≥ 180 天時預設 `yoy`。
- **誠實邊界**：比較期落在保留期之外時，前端要明說「去年同期資料已超出保留期」，
  不能顯示 0 讓人誤以為去年沒問題。這一點在 `RetentionDays` 剛調大的第一年必然會遇到。

**S3 內部順序**（不可換）：

```
S3-b1  FilterByScope／GetTodo 索引化（消 O(N×M)）      ← 低風險，先落地當退路
S3-b2  三欄物化 + DayHandlingProjection 咽喉點 + 漂移偵測
S3-a   KPI／趨勢下推 SQL（依賴 b2 的 scope 述詞）
S3-c   366 天 clamp + yoy 比較模式 + 前端
```

**驗收**：
- 366 天 × 3000 台的模擬資料集（`LogForesight.Tests/Scale/ScaleDataSet.cs` 已有骨架）
  下，報表 API 不再呼叫 `QueryLightweight`，回應時間與記憶體高水位入表。
- 六個失效觸發點各一個「操作後物化值 == 當場重算值」測試。
- `Derive` 簽章不含時間參數的護欄測試。
- `yoy` 模式的比較期日期正確（含閏年：2024-02-29 的去年同期取 2023-02-28，需明確定案並測）。
- 超過 366 天回 400 與明確訊息，不回截斷後的結果。
- 比較期落在保留期之外時，前端顯示「超出保留期」而非 0。

### S4 — 儀表板彙總下推（對應 B4/B5）

把 `BuildHostRanking`／`HighRiskDays`／`MediumRiskDays`／`CoverageGapDays`／
`BuildGroupRisk` 改為 SQL 聚合，`DashboardService` 不再呼叫 `QueryLightweight`。

- 主機排行：`GROUP BY host_id`，經 `HostAliasIndex` 解析成存活主機後再合併
  （照抄 `EfIssueAggregateQuery.ActionableOccurrences:381/420` 的既有作法）。
- 群組風險：群組成員 ⊆ 可見範圍，用同一份 `GROUP BY host_id` 結果在記憶體做子集彙總
  （與回饋十九輪批次I 對 `actionableResolved` 的處理同型），不逐群組查詢。
- **不加 TTL 快取**：分析一天只變一次確實誘人，但快取會製造「分析剛跑完、畫面還是舊的」
  這類無法誠實申報的狀態，與本專案的一貫取捨衝突。下推之後也不需要。

**驗收**：
- `dto.HighRiskDays` 與 `GroupRisk` 分項加總在**有合併主機**的資料集下一致
  （這是 B5 的回歸測試，現行程式碼會失敗）。
- 3000 台 × 90 天資料集下首頁 API 不再物化問題物件。

### S5 — 啟動路徑與逾時（對應 B9/B8 容量）

- `SeedIssueFirstSeenIfEmpty` 搬到背景 HostedService，沿用 `MigrationGateMiddleware`
  的「搬移中」語彙。**時序硬需求**：種子必須在任何逐日 upsert 之前完成
  （`SchemaUpgrader.cs:132` 已說明錯值一旦寫入永久固定），所以背景化的同時要加閘門——
  種子未完成前，`lf_issue_first_seen` 的逐日寫入必須被擋住或延後，不能兩者並行。
  這是本階段唯一的正確性風險點。
- 順帶補索引 `(source_name, event_id)` 讓種子的 GROUP BY 有索引可用；
  建索引本身也在背景做。
- `StorageBackend` 的 `UseSqlServer` 加 `CommandTimeout`：分析側長（300 秒）、
  前景側短（60 秒），沿用既有的 `maxPoolSize` 參數同一條路徑分流。
  `ApiExceptionFilter` 把 SQL 逾時翻成可行動訊息。
- `AiFollowupQueue.Capacity` 改為可設定（回補期間調大），預設維持 200。

### S6 — 與規模無關的既有缺陷（B10）

七項獨立可併，無相依。`Console.WriteLine` 清理要逐處確認有 Log 配對才刪；
`RuleBootstrapper` 的規則庫載入摘要兩行目前執行詳情看不到，改成 Milestone 而非刪除。

---

## 4. 明確不做的事

| 項目 | 理由 |
|---|---|
| AI 段改依 HostId 分桶的 N 個消費者 | 穩態實測 3000 主機日／小時，3000 台每日增量約一小時，單一消費者足夠。FIFO 是語意保證（隔日 prompt 引用前一日 AI 摘要）而非效能包袱，沒有實測瓶頸就不動有正確性語意的機制。回補期靠分批上線程序解決，不靠並行度。 |
| 儀表板／報表加 TTL 快取 | 製造無法誠實申報的過期狀態；S3／S4 下推之後不需要。 |
| 拆獨立 analysis worker 行程 | 前輪已定案不拆（`StorageBackend.cs` 註解）。S1 的版本戳快取與 S5 的 timeout 分流已把同行程的主要代價處理掉。 |
| 改用 EF Core Migrations | `docs/DB-SPEC.md` 定案 13，雙 provider 維護成本不成比例。 |

## 5. 階段相依與建議節奏

```
S1 ──────────────> S3-b1 ─> S3-b2 ─> S3-a ─> S3-c ──> S4
 │                   ↑
 └─ S2 ─────────────┘
S5、S6 任何時候可插入
```

S3 是全案重心，四個子階段各自可獨立併入 `dev` 實測。
S3-b1 單獨落地就已經讓現行 90 天／300 台環境明顯變快，值得先出。

S1 必須最先——它是唯一與查詢區間無關的固定成本，不先移除，S3／S4 的量測全部失真。
S2 是 S3 的資料前提（沒有兩層保留期，366 天區間的儲存成本無法接受）。
S5／S6 無相依，可作為等待實測回饋期間的填充。

每階段併入 `dev` 後由使用者實測，全案完成再併 `master`（專案分支慣例）。
測試基線 2086 綠，各階段不得下降。

## 6. 待校正的實測數字

規劃以估算值成立，但下列四個數字會改變**級距**而非細節，取得後回頭校正 §1：

```sql
-- I：每筆紀錄平均幾個問題子列
SELECT COUNT(*) AS issue_rows,
       COUNT(DISTINCT record_id) AS records,
       CAST(COUNT(*) AS float) / NULLIF(COUNT(DISTINCT record_id), 0) AS avg_issues
FROM lf_top_issues;

-- ContentJson 佔比（決定 S2 能省多少）
SELECT AVG(DATALENGTH(content_json)) / 1024.0 AS avg_kb,
       MAX(DATALENGTH(content_json)) / 1024.0 AS max_kb,
       SUM(DATALENGTH(content_json)) / 1048576.0 AS total_mb
FROM lf_daily_records;

-- Bh：hosts blob 現況（× 10 推估 3000 台）
SELECT DATALENGTH(content) / 1024.0 AS hosts_kb
FROM lf_blobs WHERE blob_key = 'hosts';
```

第四個不用 SQL：上次 3000 主機日那趟執行的 **`AiQueued` 與 `AiCompleted` 實際值**。
若實際進 AI 佇列的比例遠低於一成，則 B8「穩態足夠」的結論需要重新評估，
「AI 分桶不做」也要跟著重新評估。
