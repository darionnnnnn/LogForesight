# PRTG 第 1 輪規劃（鏡像層與基礎架構）

> 狀態：規劃中
> 基準：dev@e75be3e（3073 綠，略過 6）
> 來源：PRTG 整合模組專案計畫書 v1.0＋與使用者討論定案
> 範圍：計畫書 P1（鏡像層、設定、擷取、主機對應、回填）。P2 之後等本輪取回真實資料再規劃。

## 0. 對計畫書 v1.0 的修訂（核對後定案）

| # | 計畫書條款 | 修訂 | 理由 |
|---|---|---|---|
| R1 | §2.2 PRTG 表用獨立 schema `prtg.*` | 改為表名前綴 `lf_prtg_*` | SQLite 無 schema、Oracle schema≡user，違反 DB-SPEC「一律 lf_ 前綴、識別字 ≤30 字元」雙 DB 可移植規範。模組邊界以前綴達成 |
| R2 | §6.5 全部儲存 UTC | 改為與現況一致存本地時間；來源時間戳在唯一映射點轉本地（比照 `SentinelEventMapper` 做法與註解理由） | 現況全面本地時間（`record_date` 為當地日期）。混存會在 UTC+8 造成靜默的 8 小時跨日偏移，且單一時區部署下 UTC 無實益 |
| R3 | §2.1 沿用「Plan/Exec/Answer 管線」 | 沿用範圍修正為 `IAiService`/`AIService` 層（連線、provider、token 統計、輸出消毒）；prompt 組裝與佇列驅動屬 PRTG 自建（P5 階段才做） | 該管線不存在；`AiWorkItem`/`AnalysisPromptBuilder` 深度綁定 log 概念 |
| R4 | §14「環境無 VIP/多網卡問題」 | 前提降級：一 IP 多主機是系統已承認的常態（`NetiqHostList.IpConflicts()`），對應層必須處理 | 與現況程式碼的防禦姿態矛盾 |
| R5 | §11 鏡像層「按月分割區」 | 移除分割區；沿用「索引＋保留期＋（量體證實需要時）預彙總表」 | DB-SPEC 明載「不需要分割表」為既定決策；量體待本輪 probe 實測後再議 |
| R6 | §13-5 NetIQ 維運事件覆蓋疑慮 | 降級為「已大半滿足」：92 條 builtin 已含服務崩潰、非預期關機（6008/41）、硬體錯誤 8 條，另有 sev≥2 generic 兜底 | 核對 `KnownIssueSeed.cs` 屬實；放寬收集試行不急 |

## 1. 基礎架構定案（與使用者討論 2026-08-31）

1. **主檔以 NetIQ 為主**：PRTG device 以 IP 對應既有 `HostStore` 主機；對得到才用，對不到一律跳過並標記（不猜測、不自動建主機）。PRTG 有、NetIQ 無的主機只進覆蓋率稽核清單。
2. **規則合併發生在主機層，不建合併平台**：不新增「PRTG+NETIQ+WINDOWS/LINUX」規則平台。NetIQ 規則（windows/linux）不動；PRTG 訊號規則日後以獨立平台「prtg」進規則維護。分析時查主機有無 PRTG 對應：有→兩來源訊號一起進合成層；無→純 NetIQ。主機獲得/失去 PRTG 對應是資料狀態變化，非設定變更。
3. **設定放「系統管理 > 設定」新增 PRTG 頁籤**（`SystemSettings` 單例，非 Sentinel 式多站台 store）：本期假設單一 PRTG core server。
4. **Sensor 人工對應頁面遞延**：本輪分類只做「記錄與統計」（probe 產出 type 分布），不做分類引擎也不做對應 UI；確認 L0 覆蓋率與實際用途後才規劃（進 BACKLOG 附觸發條件）。
5. **本輪先取數**：目標是把計畫書 §13 六個待驗證項中的 1~4（type 分布、版本、量體、相依性使用度）用真實資料回答，作為 P2/P3 設計輸入。
6. **finding 掛接方向預定調**（本輪不實作）：日後 PRTG finding 映射成 `LogIssueSignature`（`EventId=0`＋`EventKey`，同 Linux 規則模式），沿用 `lf_top_issues`＋處理狀態＋郵件＋排行全鏈，不另建獨立 finding 表與 UI。本輪 schema 不得與此方向衝突。

## 2. 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | `lf_prtg_*` schema＋`SchemaUpgrader` 步驟 | 中 | — |
| B | SystemSettings PRTG 頁籤＋連線測試＋probe | 中 | A |
| C | 每日擷取器（結構/狀態變更/hourly 值/相依性）＋掛進 Orchestrator 第三並行 Task | 大 | A、B |
| D | 主機對應（按日、衝突檢核、覆蓋率稽核） | 中 | C |
| E | 歷史回填（分批、可中斷續傳） | 中 | C |
| F | freshness＋保留期＋文件（PRTG-SPEC 新檔、DB-SPEC/WEB-SPEC 增補、計畫書修訂 v1.1） | 小 | 全部 |

建議順序 A→B→C→D→E→F。

## 3. 批次A：schema

### 現況與核對結果
- 真表 12 張全 `lf_` 前綴小寫 snake_case ≤30 字元（`LfDbContext` 11 個 `ToTable` 皆單參數，無 schema）。
- 建表＝`EnsureCreated()`；升級＝`SchemaUpgrader.Upgrade()` 自製冪等 DDL（無 EF Migrations、無版本表）。**新表必須同時進 `LfDbContext` 與 `SchemaUpgrader`，漏一邊對既有 DB 靜默失敗**（DB-SPEC:606-608 踩過的坑）。
- 保留期慣例：排序/篩選用事件時間欄、清理一律用 `created_at`（DB-SPEC:330-332 雙欄設計），清理走 `BatchedPrune`。

### 定案
新表五張（名稱暫定，執行端可依 ≤30 字元與語意微調並記錄理由）：

| 表 | 內容 | 鍵 |
|---|---|---|
| `lf_prtg_devices` | device 鏡像（objid、名稱、群組路徑、IP、tags、狀態、相依性 objid、最近同步時間） | objid |
| `lf_prtg_sensors` | sensor 鏡像（objid、device objid、名稱、type、tags、unit、狀態、閾值原文、分類欄位——本輪只填 L0 結果或 null＋來源層級） | objid |
| `lf_prtg_state_changes` | 狀態變更/訊息事件（sensor objid、發生時間〔本地〕、前後狀態、訊息） | 自增＋(sensor, 時間) 索引 |
| `lf_prtg_values` | hourly 聚合值（sensor objid、時段起〔本地〕、avg/min/max、涵蓋率或 downtime 原始欄、品質旗標：paused/unknown 區間標記） | (sensor, 時段) 唯一 |
| `lf_prtg_host_map` | 主機對應按日（date、ip、prtg device objid、host_id、對應狀態：ok/conflict/unmatched） | (date, objid) |

- 品質規範前置到 schema：`lf_prtg_values` 與 `lf_prtg_state_changes` 必須能區分「paused」「unknown」「無資料」三態（計畫書 §6.2），欄位形式由執行端定，但驗收會查三態可分。
- Sensor 重建偵測所需欄位（同 device 同名同 type 之歷史 objid）本輪僅保留原始欄位可事後判斷，不實作接續邏輯。
- 所有表帶 `created_at`（清理用）；時間欄一律本地時間（R2）。
- 升級路徑：既有 DB 靠 `SchemaUpgrader` 新增五表（冪等 CREATE TABLE IF NOT EXISTS／SqlServer 對應寫法，比照既有步驟形式）；新 DB 靠 `EnsureCreated`。

### 測試 / 驗收
1. 新 DB（Sqlite＋SqlServer 語法檢查層級）建出五表，欄位清單以 PRAGMA table_info／INFORMATION_SCHEMA 斷言寫成測試（規格即測試，防後續漂移）。
2. 對「已存在 12 張表的舊 DB」跑 `SchemaUpgrader`：五表補齊、既有表零變動；重跑一次 no-op。
3. 表名/索引名全部 ≤30 字元、`lf_prtg_`/`ix_lf_prtg_` 前綴（測試斷言）。

## 4. 批次B：設定頁籤＋連線測試＋probe

### 現況與核對結果
- `SystemSettings` 單例存 `lf_blobs`，加密憑證慣例：`*Enc` 欄＋`CryptoHelper`（AES-256-CBC、`enc:v1:`、`LF_CRYPTO_KEY`），DTO 只回 `HasXxx` 布林、空字串＝沿用不覆寫（`SentinelAdminService.cs:58` 模式）。
- 模組開關慣例：預設 false、消費端迴圈開頭 return（`ScheduleOptions.LocalAnalysisEnabled` 註解為範本）。
- 紅線：新增設定必須有消費端。
- NetIQ probe 前例：探測工具先行、使用者貼回真實輸出後才設計欄位對應。

### 定案
1. `SystemSettings` 新增：`PrtgEnabled`（預設 false，總開關）、`PrtgUrl`、`PrtgApiTokenEnc`、`PrtgIgnoreSslErrors`（預設 false）、`PrtgFetchConcurrency`（預設 2，範圍 1~3）。每個欄位當輪即有消費端（開關→擷取器短路；其餘→連線與擷取）。
2. 設定頁新增「PRTG」頁籤：欄位編輯＋「測試連線」按鈕。token 處理比照 SMTP/AI 金鑰（只回 HasToken、空字串沿用）。
3. **probe 功能**（本輪核心產出之一）：在 PRTG 頁籤提供「環境探測」，唯讀呼叫 PRTG API 產出：版本與 API 相容性、device/sensor 總數、sensor `type` 分布統計（type×數量×unit 樣本）、相依性設定使用比例、群組樹概要。結果以文字/表格呈現並可複製——回答計畫書 §13-1~4。
4. API 存取層抽象化為單一 client 類別（日後 API v1/v2 切換只動一處）；apitoken 認證、唯讀。

### 測試 / 驗收
1. 設定驗證測試：URL 格式、併發範圍 clamp、token 加密落地（讀回為 `enc:v1:` 前綴、DTO 不含密文）。
2. `PrtgEnabled=false` 時擷取路徑零呼叫（測試以假 client 斷言）。
3. probe 對假回應（fixture JSON）產出正確統計；連線失敗回明確錯誤不炸頁。
4. 前端：頁籤顯示、儲存、測試連線走 `api.js`（無寫死路徑）。

## 5. 批次C：每日擷取器

### 現況與核對結果
- 掛接點：`AnalysisOrchestrator.RunAsync` 內與 local/netiq 並列第三個 Task（`:578-606`），錯誤語意比照 NetIQ——內部吞例外記 log，不讓整趟失敗（計畫書 §2.2 錯誤隔離）。
- 記憶體紀律：分塊掃描（`SplitDateRange` 14 天/塊）、串流分組（`PageObserver`＋`StreamOnly`）、上限在型別建構當下套用。
- 排程：取數走 `SchedulerHostedService` 既有窗口，不另開 hosted service。

### 定案
1. 每日擷取四類（計畫書 §6.4）：device/sensor 結構＋tags＋閾值＋相依性（全量鏡像 upsert）、狀態變更與訊息（前一日增量）、hourly 值（前一日，PRTG API `avg=3600`，一律聚合不拉 raw）、PRTG 自身健康概要（存 freshness 附註即可，不建表）。
2. 併發對 PRTG ≤ `PrtgFetchConcurrency`（預設 2）；逐 sensor/逐頁串流寫入，不得整批載入記憶體。
3. **斷點續傳**：以「每資料類別×日期」為單位記錄完成水位；當日中斷後重跑只補未完成部分（冪等 upsert，`lf_prtg_values` 唯一鍵天然防重）。
4. probe 斷線期間資料標不可信（品質旗標）；paused/unknown 依 §6.2 標記，不混入。
5. `PrtgEnabled=false` 或未設定 URL/token → 短路跳過（比照 AI 未設定降級範式），執行紀錄標「未啟用」而非失敗。
6. Downtime 欄不採用，僅保留狀態變更原始資料供日後自算（計畫書 §6.2）。

### 測試 / 驗收
1. 假 client fixture：一日擷取寫入四類資料，重跑同日冪等（列數不變）。
2. 中斷模擬：某類別失敗後重跑，只補該類別，已完成類別不重打 API（以呼叫計數斷言）。
3. PRTG 擷取拋例外時，local/netiq 分析結果不受影響（Orchestrator 整合測試）。
4. paused 區間的值列帶品質旗標；unknown 不計入任何統計欄位。

## 6. 批次D：主機對應

### 現況與核對結果
- 主機清單為 `lf_blobs` JSON（`HostStore`），無 SQL join；應用層字典比對有前例（`EfIssueAggregateQuery` 主機別名索引快照）。
- 一 IP 多主機已有處理慣例：`IpConflicts()` 衝突分組、`Pollable()` 取 HostId 最小者輪巡。
- `WebHost.IpAddress` 註解明載「程式不拿它做比對」——本輪打破此約，需更新該註解與文件（記為明確例外：PRTG 對應是唯一以 IP 做比對的消費端）。

### 定案
1. 每日對應作業：取 `HostStore` 活躍主機 IP → 比對 `lf_prtg_devices` IP → 寫入 `lf_prtg_host_map`（按日，歷史回溯用當日對應）。
2. 衝突處理：同 IP 多 NetIQ 主機→沿用「HostId 最小」慣例對應並標 conflict；同 IP 多 PRTG device→標 conflict 不對應（進例外清單）；IP 空白→跳過。
3. 對應狀態三值：ok／conflict／unmatched（PRTG 有 NetIQ 無）。unmatched 清單即「監控覆蓋率稽核」的資料基礎——本輪只落表＋在 PRTG 頁籤顯示計數與清單，不做獨立報表頁。
4. 不自動建主機、不猜測。

### 測試 / 驗收
1. 對應矩陣測試：一對一、一 IP 多 NetIQ 主機、一 IP 多 device、IP 空白、PRTG 獨有——五種形狀各斷言對應狀態。
2. 按日儲存：兩天 IP 異動後，舊日查詢回舊對應。
3. 對應作業重跑同日冪等（就地取代該日列，不累積重複；「什麼算一列」＝(date, objid) 唯一）。

## 7. 批次E：歷史回填

### 現況與核對結果
- 前例：`InitialHistoryDays` 首次回補、`MissingDateFinder` 缺日回補、排程永不帶破壞性重跑。

### 定案
1. 回填範圍設定 `PrtgBackfillDays`（預設 30，暫定；上限受保留期約束）——只回填 hourly 值與狀態變更（結構鏡像本來就是現況全量）。
2. 手動觸發（PRTG 頁籤按鈕）＋分批逐日、由近往遠、可中斷續傳（沿用批次C水位機制）；不塞進夜間窗口首日一次跑完。
3. 回填中斷/失敗不影響每日擷取（兩者共用冪等寫入，天然相容）。
4. 計畫書 §6.3「基線需 4–8 週」的達成與否本輪不擋——基線是 P3 的事，本輪只確保回填機制能把資料補到位。

### 測試 / 驗收
1. 回填 N 日後中斷，重跑從斷點續（API 呼叫計數斷言不重抓已完成日）。
2. 回填與每日擷取交錯執行不產生重複列。

## 8. 批次F：freshness、保留期與文件

### 定案
1. freshness：新增輕量記錄（建議併入既有 `BatchRun`/執行紀錄體系或 `lf_blobs` 單鍵 JSON，執行端擇一並記理由）：每資料類別最後成功同步時間；PRTG 頁籤顯示。逾時告警與「基於過時資料」標注屬 P4/P5，本輪只落記錄與顯示。
2. 保留期：`PrtgRetentionDays`（預設 180，暫定，下限 90 比照全站）作用於 `lf_prtg_values`／`lf_prtg_state_changes`／`lf_prtg_host_map`，清理依 `created_at` 走 `BatchedPrune`，掛既有清理段；devices/sensors 結構鏡像不清（現況全量）。
3. 文件：新增 `docs/PRTG-SPEC.md`（現況規格：schema、擷取、對應、設定鍵）；DB-SPEC 增補五表；WEB-SPEC 增補設定頁籤；計畫書修訂 v1.1（本文件 §0 六項）；BACKLOG 增列「sensor 人工對應 UI（觸發條件：L0 覆蓋率 <85% 且 P2 啟動）」等遞延項。

### 測試 / 驗收
1. 保留期清理測試：過期列刪除、devices/sensors 不動、順序在既有清理段內不破壞現有測試。
2. freshness 在擷取成功/失敗後的值正確。
3. 文件複檢：PRTG-SPEC 與實作逐條核對（終檢項）。

## 9. 明確不做（本輪定案）

- L2~L5 分析層全部（特徵計算、弱訊號、合成、敘述化）——等本輪真實資料。
- Sensor 分類引擎（L0 對照表也不做，僅 probe 統計 type 分布）與人工對應 UI（BACKLOG 附觸發條件）。
- PRTG finding／`LogIssueSignature` 映射（方向已定調於 §1-6，實作在 P4）。
- 規則維護頁的「prtg」平台（P3/P4 才有規則可維護）。
- 分割區／月分表（R5）。
- 逾時獨立管道告警、過時資料標注（P4/P5）。
- Sensor 重建接續邏輯（只留原始欄位）。
- 多 PRTG server 支援（本期單一 core server；多 server 需求出現時再議 Sentinel 式 store 化）。

## 10. 複檢（規劃完成後）

- 與既有行為衝突：`WebHost.IpAddress`「不做比對」註解被 §6-D 打破→已列入批次D交付更新註解與文件。設定紅線「必須有消費端」→逐鍵核對：`PrtgBackfillDays`/`PrtgRetentionDays` 消費端皆在本輪（批次E/F）。清理順序掛既有清理段→批次F驗收含既有測試不破。
- 批次間介面：C 依賴 A 的品質旗標欄位與 B 的 client；D 消費 C 的 devices 鏡像；E 沿用 C 的水位機制——均已在各批次明寫。
- 四個坑：冪等「什麼算一列」已寫（values 唯一鍵、host_map (date,objid)）；破壞性判準——本輪唯一刪除是保留期清理，判準為 `created_at` 沿用全站慣例，無新型破壞；單向閘門——無一次性旗標設計；移除類——本輪零移除。
- 複檢完成，另發現一項已補：probe 斷線品質旗標原只在計畫書，已明寫進批次C定案4。

## 11. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （待實作開始填寫） | | | | |
