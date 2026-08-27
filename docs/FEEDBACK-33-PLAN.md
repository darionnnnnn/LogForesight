# FEEDBACK-33-PLAN：報告全文改存資料庫，移除 export\ 檔案輸出

本輪主題：把三種報告（風險日報告／週檢報告／權限異動報告）的全文從站台主機的 `export\`
目錄搬進資料庫的 `lf_reports` 表，並移除檔案輸出、檔案讀取與檔案清理的全部實作。

完成後 `StorageBackend` 註解宣稱的「全部資料走 SQL，無檔案」才第一次真正成立。

---

## 0. 作業總覽

| 項目 | 值 |
|---|---|
| 分支 | `feature/feedback-33`（自 `dev` 開） |
| 執行者 | Claude 自行實作（**未委派**）。使用者核准的是「開始全部作業」與「套用 ui-ux-pro-max」，沒有要求委派；本輪跨 Core／Web／前端三層且多處是契約改動，可靠性優先 |
| 測試基線 | 開工 2993 綠 → 完工 **2988 綠**（略過 6）。刪 34 條（檔案輸出/讀取/清理的測試，鎖的是已移除的行為）、新增 29 條 |
| 完工條件 | 全綠、文件同步、瀏覽器實測三個入口、併回 `dev` |

### 作業清單

| 作業 | 主題 | 可獨立 commit |
|---|---|---|
| A | `lf_reports` 資料表與 DB 報告 sink／reader | ✅ |
| B | 呼叫端切換至 DB sink、主機綁定修正、檔案實作移除 | ✅ |
| C | 既有 `export\` 報告一次性遷入 | ✅ |
| D | 保留期收斂與設定頁空間告知 | ✅ |
| E | Web 讀取端：風險報告改讀 DB、週檢／權限異動新入口、下載 txt | ✅ |
| F | 現行文件同步 | ✅ |
| Z | 體檢輪＋併回前終檢 | — |

### 執行紀錄

| 作業-階段 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|
| A-1 建表與實體對應 | 完成 | `ReportSchemaContractTests` 兩條（PRAGMA 欄位清單、升級路徑與 EnsureCreated 一致且冪等） | **偏離定案 2**：唯一鍵改為 `(host_id, host_name, report_date, kind)`。`host_id = 0` 是「尚未登記」的哨兵值不是一台主機，只用 host_id 會讓兩台都還沒登記的主機在同一天互相覆蓋——正是本輪要修的碰撞換個地方重演 |
| A-2 sink／reader 實作 | 完成 | `ReportStoreTests` 12 條 | **偏離定案 4**（見下方「定案修正」） |
| A-3 保留期清理 | 完成 | 邊界日不刪／超過一天才刪／剛補寫的舊日期報告不被清掉／空表回 0 | 走既有 `BatchedPrune`（只撈主鍵＋分批＋單次上限），不自己再發明一次 |
| B-1 三個寫入端綁主機 | 完成 | `ReportHostBindingTests` 2 條＋`LogAnalysisServiceSplitTests` 補斷言 | 原規劃的「LogAnalysisService 傳主機」測試需要 test-only API 才寫得出來，改為在既有整合測試上加斷言 |
| B-2 移除檔案實作 | 完成 | `.cs` 內 `export` 僅剩遷移器與升級註解；全綠 | 刪 `FileReportSink`／`FileReportReader`／`ExportReportPruner` 與其 34 條測試 |
| C-1 遷移器 | 完成 | `ReportFileMigrationTests` 11 條（含「表中已有夜間分析寫入的列，舊資料仍完整搬入」） | **改為紀錄驅動**（見下方「定案修正」） |
| D-1 上下限收斂 | 完成 | `ReportRetentionSettingTests` 改寫 8 條（含取小遷移、不得反向拉長） | 既有測試鎖的是被推翻的決策，逐條改寫而非刪除 |
| D-2 設定頁空間告知 | 完成 | `ReportRetentionWebTests` 的原始檔比對加 6 條斷言；瀏覽器實測顯示「目前已存報告：3 份，佔用約 0.0 MB」 | 用實測值而非估算公式 |
| E-1 風險報告改讀 DB | 完成 | 瀏覽器實測 200；`CaseGrantVisibilityTests` 授權斷言 | `HasReport` 改為實查有無（見下方「定案修正」） |
| E-2 週檢／權限異動入口 | 完成 | 瀏覽器實測：詳情頁兩張卡片、權限異動列展開的對話框 | 權限異動入口只對「本機監控」來源的列顯示——NetIQ 來源沒有對應報告 |
| E-3 下載 txt | 完成 | 瀏覽器實測下載檔名 `2026-08-27_高風險_儲存裝置.txt`、工具列點擊不會收合卡片 | 前端組 Blob，未新增後端存檔路徑 |
| F 文件同步 | 完成 | 現行文件與 README 的 `export\` 零命中（僅存 DB-SPEC 升級路徑段） | CLAUDE.md 基線 2993 → 2988 |

### 定案修正（實作事實推翻規劃）

| # | 原定案 | 改為 | 理由 |
|---|---|---|---|
| 4 | `ReportFile` 值改存 `report_id`，讀取端依它反查 | 讀取端改走 **「主機×日期×種類」自然鍵**；`ReportFile` 不回頭改寫 | 三種報告只有風險報告有欄位存得下參照，週檢與權限異動從來沒有。統一走自然鍵之後三種報告同一條路徑，升級前留下的檔案路徑值也不必回頭改寫幾十萬列 JSON |
| 5 | 遷移掃 `export\` 目錄 | 遷移**由分析紀錄驅動**（權限異動報告除外） | 升級前的寫入端都沒帶主機識別，檔案全落在 export 根目錄，掃目錄只能全部歸給本機＝丟掉所有 NetIQ 主機的報告連結 |
| — | `HasReport` 維持看 `record.ReportFile` | 改為實查 `IReportReader.Exists` | 報告有自己的保留期、可能比紀錄先被清掉，旗標卻永遠留著，會給出點下去必定落空的入口 |

## 體檢輪修正（體檢方：claude-fable-5，實作方：claude-opus-5）

1. **`EfReportStore.Write` 的 upsert 鍵與 `Read` 語意不一致（真 bug）**
   - 哪裡：`Write` 的 upsert 查詢認四欄完全相等，`Read` 卻是 HostIdentity 語意（先比 id、0 列退名稱）。
   - 症狀：主機未登記時寫過報告（列上 host_id=0）、事後登記成功再重跑同一天——`Write` 找不到 0 列而
     **新增第二列**，同一主機日兩列並存，`Read` 沒有排序、讀到哪列不確定（可能顯示舊報告）。
   - 修法：upsert 查詢改用同一套 HostIdentity 語意並**認領**（命中 0 列時把 host_id/host_name 升級成
     現在的識別，表中始終一列）；`Read` 另加 `OrderByDescending(HostId)` 防禦排序，讓意外的兩列狀態
     也有確定答案。
   - 迴歸測試：`ReportStoreTests.未登記時寫過報告_登記後重跑同一天_認領原列不新增`。
2. **`report-view.js` 的 `toolbar` 帶著沒人用的 `onClick` 參數（過度設計）**——移除。
3. 檢視過但**判定不修**：`lf_reports` 在全新安裝會有 EF 預設名與 SchemaUpgrader 自訂名兩份唯一索引
   ——這是全專案既有的同型現象（BACKLOG「EnsureCreated 與 SchemaUpgrader 建出兩份同欄位索引」條
   已涵蓋），影響寫入吞吐不影響正確性，處理是 schema 層級的一輪，不在本輪加碼。

測試：2989 綠（略過 6）。體檢後基線 2988 → 2989（+1 迴歸測試）。

### 實作方自查抓到的真 bug（實作期間）

- **未登記主機的讀取會串台**：`Read`／`Exists` 的比對寫成 `r.HostId == host.HostId || (r.HostId == 0 && 名稱相符)`，
  查詢端自己是未登記主機（id 為 0）時第一段退化成 `r.HostId == 0`，會命中**所有**未登記主機的報告。
  補上 `host.HostId != 0` 前置條件；`ReportStoreTests.兩台皆未登記的主機_以名稱區分不互相覆蓋` 就是抓到它的那條。

---

## 1. 背景與定案

### 1.1 為什麼要做

`export\` 是全站唯一殘存的實體檔案交付物。正因為它留在檔案系統，長出三個問題：

- **多主機檔名碰撞（真 bug）**：`IReportSink.WriteAsync` 的 `host` 參數在正式環境**三個呼叫端全部沒傳**
  （風險報告走預設 `""`、週檢走預設 `""`、權限異動明寫 `host: ""`），`FileReportSink` 的
  `{主機}` 子目錄是死碼。結果是同一天、同風險等級、同類別組合的兩台 NetIQ 主機產生**完全相同的檔名**
  而互相覆蓋，多筆紀錄的 `ReportFile` 指向同一個檔——A 主機的紀錄點開會看到 B 主機的報告。
- **孤兒檔**：重新分析（第三十一輪逐日就地取代）後類別組合一變檔名就變，舊檔留到保留期滿，
  但對應的 DB 紀錄已被取代，那個檔永遠沒有入口。
- **有寫無讀**：三種報告只有風險報告有 Web 入口。週檢報告的路徑存在 `record.WeeklyCheckup.ReportFile`
  但 DTO 不吐；權限異動報告的路徑**完全沒有存**，只有 console 輸出一行。

以 `(host_id, report_date, kind)` 為鍵存進 DB 之後，前兩項是**結構性消失**，不需要各自寫補丁。
第三項由作業 E 補上入口。

### 1.2 定案

| # | 定案 | 理由 |
|---|---|---|
| 1 | 建 `lf_reports` 表，schema 以 `docs/DB-SPEC.md`「報告全文」段的既有設計為準 | 該設計早已定案、只是從未實作，本輪是落地不是重新設計 |
| 2 | `host_id` 沿用全站的 `host_id = 0 時以 host_name 歸戶` 慣例，**不設 FK**，並加 `host_name` 欄 | DB-SPEC 寫「FK → lf_hosts NOT NULL」是在 `HostKey`／`HostIdentity` 的 0 值 fallback 慣例確立之前。主機登記失敗時 `hostId` 會是 0，設 FK 會讓當晚的報告寫入直接炸掉，把「報告寫不出來」升級成「分析失敗」 |
| 3 | 同一 `(host_id, report_date, kind)` **upsert 取代**，不累積多列 | 重新分析同一天要就地取代，不是留兩份讓使用者猜哪份是現行的 |
| 4 | `DailyAnalysisRecord.ReportFile` **保留欄位、語意收斂為「報告參照」，值改存 `report_id` 的字串形式** | 保留 `RecordStorageShaper`／`EfAnalysisRecordStore`／`AiController` 一整串呼叫端零改動 |
| 5 | 既有 `export\` 報告**一次性遷入 DB，舊檔不刪** | 比照 `PermissionChangeMigrator`／`HandlingBlobMigrator` 的既有慣例。不遷的話升級後所有既有紀錄的「查看報告」全部從缺 |
| 6 | `ReportRetentionDays` 上限收斂為 **不得大於 `RetentionDays`**，出廠預設 1095 → **180** | 見 §1.3 |
| 7 | **不做 gzip 壓縮** | 3000 台 × 180 天 ≈ 4 GB，不值得為此引入 `varbinary` 與雙 provider 的壓縮/解壓路徑。記進 BACKLOG |
| 8 | 週檢報告與權限異動報告**本輪一併補 Web 入口** | 不補的話這兩種從「至少能去資料夾開」退化成「只進不出」，比現況更糟 |
| 9 | 報告檢視畫面提供**下載 .txt** | 移除檔案輸出後，使用者原本「去資料夾拿檔案寄給廠商」的交付路徑要有替代 |

### 1.3 保留期限的審慎評估（推翻第三十二輪的「兩者不互相約束」）

單份報告大小由 `RiskReportService` 的硬上限反推：原始 log 全檔上限 20 筆 × 每筆訊息截斷 500 字元
≈ 12 KB，加上問題清單（每類別 ≤4 項）、深入分析、標頭與總覽，**單份約 20～30 KB**。

| 主機數 | 風險日 30% 時每日份數 | 每日增量 | 留 180 天 | 留 1095 天 |
|---|---|---|---|---|
| 100 | 30 | 0.75 MB | 135 MB | 0.8 GB |
| 500 | 150 | 3.8 MB | 675 MB | 4.1 GB |
| 3000 | 900 | 22.5 MB | 4.0 GB | **24 GB** |

第三十二輪讓 `ReportRetentionDays` 可以大於 `RetentionDays`，立論是
「報告是純文字小檔，且**超過歷史保留天數之後檔案仍在磁碟上**，管理者去資料夾還拿得到」。
本輪把檔案拿掉，這個立論連同它的補償路徑一起消失：超過 `RetentionDays` 之後
`lf_daily_records` 已被清除，那份報告在站上**沒有任何入口可以點開**，留著純粹是佔 DB 空間的死資料。

因此收斂為 `ReportRetentionDays ≤ RetentionDays`（前後端皆驗證），既有部署設成 1095 的
比照第三十二輪保留鍵的**取小遷移**慣例自動收斂，並在遷移時寫一行 log 說明。

**空間告知採實測值而非估算公式**：設定頁顯示「目前已存 N 份報告，佔用約 X MB」——
`lf_reports` 的列數與 `content` 總長度是查得到的事實，比一條使用者無法驗證的估算公式誠實。

---

## 2. 作業 A：`lf_reports` 資料表與 DB 報告 sink／reader

### A-1 建表與實體對應

**目標**：`lf_reports` 在新舊 DB 上都存在，雙 provider 皆可用。

**行為契約**
- 在 `LfDbContext` 加對應實體，並在 `SchemaUpgrader` 以 `CreateTableIfMissing` 補上整張表
  （既有部署的 DB 已存在，`EnsureCreated` 不會建新表——這條在 `lf_risky_events` 的註解已寫明，照做）。
- 欄位（型別以 Sqlite／SqlServer 兩份 DDL 並存，比照 `lf_permission_changes` 的寫法）：

  | 欄位 | 型別 | 說明 |
  |---|---|---|
  | `report_id` | PK 自增 | Sqlite 必須是 `INTEGER`（rowid） |
  | `host_id` | bigint NOT NULL | 0 代表未登記，以 `host_name` 歸戶 |
  | `host_name` | nvarchar(255) NOT NULL | 同上；`host_id = 0` 時的歸戶鍵 |
  | `report_date` | date NOT NULL | 報告所屬日期（不是產生時間） |
  | `kind` | nvarchar(20) NOT NULL | `daily_risk` \| `weekly_checkup` \| `permission` |
  | `risk_level` | nvarchar(10) NULL | 僅 `daily_risk` |
  | `categories` | nvarchar(200) NULL | 類別串（如「儲存裝置+安全」） |
  | `file_name` | nvarchar(255) NOT NULL | 原始檔名格式，顯示與**下載檔名**用 |
  | `content` | text / nvarchar(max) NOT NULL | 報告全文 |
  | `created_at` | datetime2 NOT NULL | 產生時間；**保留期清理依這一欄** |

- 索引：`(host_id, report_date, kind)`（唯一，upsert 的判定鍵）、`(created_at)`（清理用）。
- `report_date` 與 `created_at` 不可互相取代：查詢與顯示用 `report_date`，保留期清理用 `created_at`
  ——理由同 `lf_permission_changes`（重跑 100 天前的主機日，依 `report_date` 清理會讓剛補出來的報告立刻消失）。

**驗收**
- 新建 DB 與既有 DB（先用舊 schema 建好、再跑升級）兩條路徑都能取得完整的 `lf_reports`。
- 以 `PRAGMA table_info(lf_reports)` 寫一條**欄位清單斷言**測試（規格即測試，防後續 schema 漂移）。
- 升級路徑重跑兩次不出錯（冪等）。

### A-2 `IReportSink` 契約調整與 DB 實作

**目標**：`EfReportStore`（暫定名）取代 `FileReportSink`，且主機綁定不再從缺。

**行為契約**
- `IReportSink.WriteAsync` 的 `host` 字串參數改為 **`HostKey`**（既有型別，含 `HostId`／`HostName`），
  並新增報告中繼資料參數（`riskLevel`／`categories`，可為 null）——`risk_level` 與 `categories`
  兩欄的消費端是作業 E 的報告檢視標頭，不是「有欄位無消費端」。
  > 參數形狀（單一 meta 物件 vs 兩個可空參數）標**暫定**，執行端可依實作事實決定，理由寫進回報。
- 回傳值語意由「檔案完整路徑」改為 **`report_id` 的字串形式**。
- 寫入為 upsert：`(host_id, report_date, kind)` 已存在時**整列取代**（含 `content`／`file_name`／
  `risk_level`／`categories`／`created_at`），不新增列。
- `IReportReader.Read(reportRef)` 的 DB 實作：`reportRef` 解析為 `report_id`；
  **解析不出數字時回 `null` 而不是拋例外**——那是舊部署留下的檔案路徑，作業 C 的遷移會處理，
  遷移完成前讀到舊值要安靜從缺（既有契約就是「找不到不是錯誤」）。

**驗收**
- 同一 `(host_id, date, kind)` 連寫兩次，表中仍只有一列且內容為第二次的（upsert 取代）。
- 兩台**不同主機**同一天、同風險等級、同類別組合各寫一份，兩列並存且各自讀回自己的內容
  （這條直接鎖住 §1.1 的檔名碰撞 bug，**必須有**）。
- `Read` 對非數字字串、對不存在的 id 皆回 `null` 不拋例外。
- Sqlite 與（可得時）SqlServer 合約測試同綠。

### A-3 DB 後端的保留期清理

**目標**：`ExportReportPruner` 的職責移進 DB 層。

**行為契約**
- 依 `created_at < 今日 - ReportRetentionDays` 刪列，回傳刪除筆數。
- 大量刪除要分批（比照既有 store 的 `Prune` 寫法），避免單一交易鎖表。
- **不刪除任何目錄或檔案**——檔案系統的清理隨 `ExportReportPruner` 一起退場。

**驗收**：邊界日（剛好等於保留天數）不刪、超過一天才刪；空表回 0 不拋例外。

---

## 3. 作業 B：呼叫端切換與檔案實作移除

### B-1 三個寫入端改綁主機

**目標**：三種報告都帶著正確的主機識別寫進 DB。

**行為契約**
- **風險報告**：`LogAnalysisService` 呼叫 `RiskReportService.GenerateAsync` 時傳入自己的
  `_host`／`_hostId`（目前完全沒傳）。`RiskReportService` 把 `record.RiskLevel` 與類別串一併帶給 sink。
- **週檢報告**：`AnalysisOrchestrator` 呼叫 `WeeklyCheckupService.RunAsync` 時傳入 `currentHost`／`currentHostId`。
- **權限異動報告**：`AnalysisOrchestrator` 改傳 `currentHost`／`currentHostId`（目前明寫 `host: ""`）。
- 三者的檔名組法（`{yyyy-MM-dd}_{風險等級}風險_{類別}.txt`／`_週檢.txt`／`_權限異動.txt`）
  **一字不改**，改存進 `file_name` 欄，作為顯示與下載檔名。

**驗收**
- NetIQ 多主機情境下，兩台主機同日的報告各自可讀回自己的內容（B 段的迴歸鎖）。
- 三種 `ReportKind` 各有一條端到端測試：產生 → 表中出現對應列且 `host_id`／`host_name` 正確。

### B-2 移除檔案實作

**目標**：專案內不再有任何報告寫檔／讀檔／掃目錄的程式碼。

**行為契約**
- 刪除 `FileReportSink`、`FileReportReader`、`ExportReportPruner`（含其 `TryParseReportDate`——
  作業 C 的遷移需要同樣的解析，**搬進遷移器內部**，不要留一個公用工具讓人以為檔案路徑還活著）。
- 刪除對應測試 `FileReportSinkTests`、`ExportReportPrunerTests`、`RecordQueryTests` 內的
  `FileReportReaderTests`；**不是改成配合新實作**——這些測試鎖的是已移除的行為。
- `AnalysisOrchestrator` 的報告清理段改呼叫 A-3。
- `ServiceCollectionExtensions` 的 `IReportReader` 註冊改為 DB 實作。

**驗收**
- 全專案 grep `export`（排除 `docs/archive`、`wwwroot/lib`、JS 的 `export` 關鍵字）在 `.cs` 內零命中。
- `Directory.CreateDirectory`／`File.WriteAllTextAsync` 在報告相關路徑零命中。
- 測試全綠（基線會因刪測試而下降，於執行紀錄註明刪除數）。

> **注意**：第三十二輪剛加的「報告年月分層」（`FileReportSink` 年月子目錄、`ExportReportPruner`
> 遞迴掃描與空目錄清除）本輪整段作廢，這是預期的，不是漏改。

---

## 4. 作業 C：既有 `export\` 報告一次性遷入

### C-1 遷移器

**目標**：升級後既有紀錄的「查看報告」不從缺。

**行為契約**
- 比照 `PermissionChangeMigrator`：自己的遷移狀態（獨立 blob key），**不併進** `HandlingBlobMigrator`
  （既有部署的處理狀態遷移早已 `Completed`，`Evaluate()` 對 `Completed` 直接短路，併進去就永遠不執行）。
- 掃 `{DataRoot}\export\` 底下所有 `.txt`（含主機子目錄與年月子目錄兩種既有版面），
  依檔名解析日期與 kind，寫入 `lf_reports`。
- **重入保護逐筆比對自然鍵 `(host_id, report_date, kind)`**，不是「表裡有資料就整批跳過」——
  夜間分析也會寫這張表，先寫進一列就會讓整批舊資料被誤判成已搬而永久消失（`PermissionChangeMigrator`
  的註解已寫明這個坑，照做）。
- 主機歸戶：檔案在 `export\{主機}\` 底下時取子目錄名；在 export 根目錄底下時歸給**本機**
  （`Environment.MachineName`），因為既有部署三個呼叫端都沒傳 host（§1.1），根目錄的檔案事實上就是本機的。
- **回填既有紀錄的 `ReportFile`**：舊值是絕對路徑，遷入後改寫為新的 `report_id`。
  路徑對不上任何遷入列的（檔案已被清理），**保持原值不動**，讀取端會安靜從缺（A-2 契約）。
- 舊檔**不刪**，保留為備份。
- `export\` 目錄不存在時直接標記完成，不視為錯誤（SqlServer 部署、或全新安裝）。

**驗收**
- 三種版面（根目錄／`{主機}\`／`{主機}\{yyyy-MM}\`）的檔案都能正確遷入且歸戶正確。
- 遷移器連跑兩次，表中列數不變（冪等）。
- 「表中已有夜間分析寫入的當日列」的情境下重跑遷移，舊資料仍完整遷入（鎖住上述整批跳過的坑）。
- 遷移前後，一筆既有紀錄的「查看報告」讀到的內容逐字相同。

---

## 5. 作業 D：保留期收斂與設定頁空間告知

### D-1 上下限與預設值收斂

**行為契約**
- `SystemSettings.DefaultReportRetentionDays`：1095 → **180**。
- 驗證規則改為 `MinRetentionDays(90) ≤ ReportRetentionDays ≤ RetentionDays`，
  **前端與後端 DTO 皆驗證**（比照 `RawEventRetentionDays ≤ RetentionDays` 的既有寫法）。
- **既有部署取小遷移**：讀取設定時若 `ReportRetentionDays > RetentionDays`，收斂為 `RetentionDays`
  並寫一行 log 說明原因。比照第三十二輪保留鍵合併的取小慣例。
- `RuntimeSettingsResolver` 既有的「超出合理範圍改用內建預設」分支要跟著改（目前上限寫死 3650）。

**驗收**
- `ReportRetentionDays = RetentionDays + 1` 驗證失敗，錯誤訊息含「報告保留天數」與「不可大於歷史資料保留天數」。
- 舊設定 blob（`ReportRetentionDays = 1095`、`RetentionDays = 180`）讀取後解析為 180。
- 既有的 `ReportRetentionSettingTests`／`ReportRetentionWebTests` 逐條檢視——
  斷言 1095 的那些鎖的是已推翻的決策，**改寫而非刪除**，並保留一條「舊值 1095 會被收斂」的遷移測試。

### D-2 設定頁的空間告知

**行為契約**
- 「報告保留天數」欄位的常駐說明改寫，必須明確告知**報告全文存在資料庫、會佔用資料庫空間**，
  且說明它為什麼不能大於歷史資料保留天數（超過之後分析紀錄已清除，報告在站上沒有入口）。
- 設定頁顯示**實測用量**：`lf_reports` 的列數與 `content` 總長度換算的概略 MB
  （「目前已存 N 份報告，佔用約 X MB」）。走既有的系統設定 API，不新增頁面。
- 用詞遵守 WEB-SPEC §8.6a 全站用詞規範。

**驗收**
- 用量數字在空表時顯示 0 而不是空白或錯誤。
- 說明文字含「資料庫」與空間佔用的字樣（可 grep 斷言）。
- 說明文字與實際驗證規則一致（終檢時逐條對照，§8 的「文件改了實作沒跟上」防線）。

---

## 6. 作業 E：Web 讀取端

### E-1 風險報告改讀 DB

**行為契約**
- `RecordDetailQueryService.GetReport` 改走 DB reader；授權判斷（案件授與者不得取得全文、
  回 `null` 而不是拋例外讓「詢問 AI」端點還能用）**一字不改**。
- `HasReport` 的判定維持看 `record.ReportFile` 是否有值。

**驗收**：既有的報告檢視與「詢問 AI」帶入報告兩條路徑行為不變（既有測試不得放寬）。

### E-2 週檢報告與權限異動報告的入口

**行為契約**
- 週檢報告：紀錄詳情頁既有的「體檢」區塊（目前只顯示結論）加上「查看完整報告」入口。
- 權限異動報告：權限異動檢核頁加上當日報告全文的入口。
- 兩者共用與風險報告**同一套**授權與呈現路徑（AI 產出一律 `textContent`／走 `markdown-lite`，
  不得被當 HTML 解析）。
- 查無報告時顯示「報告已不存在」，不是空白也不是錯誤。

**驗收**：兩個新端點各有授權測試（無權者取不到）＋查無資料回 `null` 的測試。

### E-3 下載 .txt

**行為契約**
- 報告檢視畫面加下載鈕，檔名用 `lf_reports.file_name`。
- 前端組 Blob 下載，**不新增後端存檔路徑**（否則等於把剛移除的檔案輸出從另一扇門放回來）。
- 路徑組裝走 `core/paths.js` 的 `appUrl()`／`api.js`，不得寫死 `/` 開頭（IIS 子 Application）。

**驗收**：下載檔名與內容與畫面顯示一致。

> **⚠ 動工前待確認**：E-2／E-3 涉及版面與元件的視覺設計決策。依全域規則，
> **開始實作前需先確認要不要套 `ui-ux-pro-max`**。

---

## 7. 作業 F：文件同步

| 文件 | 要改什麼 |
|---|---|
| `docs/DB-SPEC.md` | 「報告全文」段：`lf_reports` 由設計改為現況，欄位對齊實作（`host_name`、無 FK、upsert 鍵、`created_at` 清理）。保留策略表的 `ReportRetentionDays` 一列改寫（不再提 `export\`、不再說「檔案仍在磁碟」） |
| `docs/WEB-SPEC.md` | §1923 設定說明改寫（DB 空間、上限 ≤ `RetentionDays`）；§2325 儲存對照表的 `IReportSink` 一列改為 `lf_reports`，並刪掉「唯一保留的實體檔案交付物」的整句 |
| `docs/DETECTION-SPEC.md` | :545 週檢報告的 `export\...` 路徑敘述改為 DB |
| `README.md` | 架構圖 `RiskReportService/export/*.txt`、§風險報告整段、`Storage.DataRoot` 說明、部署目錄樹的 `export\` 一列、§診斷 log 那句「已完整保存在…風險報告（export\）」 |
| `docs/BACKLOG.md` | 加一條：報告全文壓縮（gzip）視 DB 用量再議 |
| `CLAUDE.md` | 測試基線數字更新 |

**驗收**：全 `docs/`（排除 archive）與 README grep `export\` 零命中；
現行文件不得出現「原本／後來／改為／第 N 輪」等敘事字眼。

---

## 8. 作業 Z：體檢輪與併回前終檢

### Z-1 體檢輪
對照本規劃逐條檢查缺漏／過度設計／新 bug。

### Z-2 終檢（兩個獨立 Explore，各審一次全 diff）
一份審程式碼、一份審文件。終檢前先做便宜的：

- **跨段產出鏈回頭 grep**：作業 A 新增的欄位（`risk_level`／`categories`／`file_name`）
  逐一 grep 前端消費點——**沒有消費點的欄位就是漏接**（定案 §1.2-1 承諾它們的消費端是 E-2 的標頭）。
- **schema 漂移防線**：A-1 的 `PRAGMA table_info` 斷言必須存在且與 DDL 一致
  （`CreateTableIfMissing` 讓漂移完全靜默，各段測試照綠）。
- **保留期同型普查**：grep 所有讀 `ReportRetentionDays` 的地方（`SystemSettings`／
  `RuntimeSettingsResolver`／`SettingsDtos` 兩處／`SystemSettingsService` 四處以上／前端），
  確認上限規則四處一致，沒有一處還留著 3650 或 1095。
- **反方向回頭**：D-1 推翻了第三十二輪的決策，逐一比對第三十二輪留在現行文件裡的相關敘述有沒有跟上。
- **`ReportFile` 語意改變的全鏈核對**：grep 全部 `ReportFile` 讀取點，確認沒有任何一處還在
  把它當路徑用（`Path.` 系列、字串含 `.txt` 的判斷、console 輸出時的措辭）。

---

## 9. 已知不做

- 報告全文壓縮（gzip）——見定案 7，記進 BACKLOG。
- 獨立於 `lf_daily_records` 的「報告清單頁」——`ReportRetentionDays ≤ RetentionDays` 之後，
  每份報告都有紀錄可掛，不需要另一個入口。
- 舊 `export\` 檔案的自動刪除——遷移後保留為備份，由管理者自行決定何時清掉。
