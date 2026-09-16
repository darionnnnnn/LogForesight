# 回饋第 45 輪規劃：畫面狀態一致性與資料載入效能

> 狀態：規劃中（使用者已定案，尚未實作）
> 基準：dev@47e43b6（3955 綠，略過 6）
> 來源：整體體檢——(1) 執行期狀態與畫面控制項不一致（例：取數執行中 AI 啟動鈕仍可按）；
> (2) 各功能頁在資料量大時的載入效能與快取缺口。評估過程另抓到的 bug 一併納入。

## 作業總覽

| 作業 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A | 執行狀態一致性：排程頁跨卡互斥、AI 可用性守門、全站執行中告示、狀態碼統一、前端 bug | 中 | 無 | 1 |
| B | 資料載入效能與快取：執行紀錄常駐投影、records 慢路徑、設定 blob 快取、可見範圍快取、PRTG 索引、慢查詢埋點、前端逾時與 loading、後端 bug | 大 | 無（與 A 不共檔，除 `runs.js` 只有 B6 的 loading 會碰） | 2 |

- **委派模型**：`impl-low`（Opus + low）分段委派；A7／B7 這類幾行的 bug 修正由 Claude 直接做。
  subagent 模型分層：主模型 Fable，掃描／終檢用 `scan-low`。
- **分支**：`feature/feedback-45`，自 dev@47e43b6 開。
- **不變式（全輪）**：不新增沒有消費端的設定；前端不寫死 `/` 路徑；狀態碼改動不得破壞
  `handling-panel.js` 既有的 409 分支；所有快取的鍵必須含可見範圍或資料版本戳，不得跨使用者洩漏。

---

## 作業 A：執行狀態一致性

### 現況與核對結果

| # | 事實 | 證據 |
|---|---|---|
| A-1 | 取數執行中，AI 卡「立即補跑 AI／強制重新分析」仍顯示且可按；前端只看 AI 自身 `isRunning`，後端 `ai-run` 沒有跨類型守門 | `runs.js:1022-1026`、`ScheduleController.cs:444-459` |
| A-2 | 取數執行中按「立即補跑」：待補紀錄被完整性閘門擋住，跑一輪空的就結束並標 `waiting-fetch`；按「強制重新分析」：整批重標並在 PRTG 資料未到齊時重跑 | `AiAnalysisHostedService.cs:278`、`:211-218` |
| A-3 | AI 服務未設定時兩顆 AI 啟動鈕仍顯示；同頁「AI 診斷傾印」有依 `aiAvailable` 隱藏，判定不一致；後端不檢查 `IWebAiService.Available` | `runs.js:646`、`:1022-1026` |
| A-4 | 「執行模式」下拉改成非預設值時 `updateRerunModeUI` 引用未定義的 `daysWrap` 擲 `ReferenceError`，警示文字與台數預覽不更新 | `runs.js:1471`（全 repo 唯一命中） |
| A-5 | 記錄詳情「AI 判讀」在 `finally` 內一律 `disabled=true`，逾時／失敗後也鎖死 | `record-detail.js:1966-1970` |
| A-6 | 取數與 AI 的停止鈕沒有 `withBusy`，可連點；第二下後端回 400 紅字 | `runs.js:1364-1380`、`:1396-1412` |
| A-7 | 頁面啟動時 `refreshPrtgSyncStatus`／`refreshPrtgBackfillStatus` 與 `loadSchedule` 並行，`prtgModuleEnabled` 初值 `null`，`=== false` 判斷不成立 → 首輪輪詢把 PRTG 兩顆啟動鈕設為可按，第二道檢查同樣不擋 | `runs.js:1230`、`:1702`、`:1256`、`:1744`、`:1800-1804` |
| A-8 | 強制重跑 modal 開著時輪詢不會關 modal，3 秒窗口內可送出並停止他人剛啟動的執行；modal 文案未提「會停止進行中執行」。後端此行為是設計（停止→gate→重標→重跑，消滅競態） | `runs.js:1418`、`AiAnalysisHostedService.cs:211-218` |
| A-9 | 狀態碼不一致：「沒有可停止的執行」排程／AI 回 400、PRTG 同步／回填回 409；「被互斥擋下」結構同步 409、回填 400 | `ScheduleController.cs:303,468`、`SettingsController.cs:165-166,190,216-223,248` |
| A-10 | 「分析進行中」告示只在儀表板（30 秒輪詢 `/api/run-activity`）；記錄／詳情／報表頁無感知；主機詳情「立即更新此主機」無排程狀態感知，按下去只得到 warning toast。`/api/run-activity` 刻意不掛 `[Permission]`（註解明寫） | `dashboard.js:63-110`、`host-detail.js:526-547`、`DashboardController.cs:38,58` |
| A-11 | 取數卡與 AI 卡狀態 DTO 已含觸發者文字（`TriggerText`／`LastRunTriggerText`） | `ScheduleController.cs:124,148` |
| A-12 | `aiAvailable` 五頁各打 `/api/ai/status` 各存一份（runs／records／record-detail／dashboard／netiq）；`core/api.js` 已有 `getCurrentUser`／`getDisplaySettings` 的模組快取模式 | `api.js:103-137` |
| A-13 | 4 處 async listener `try/finally` 無 catch（unhandled rejection）；`rule-validate` 無 `withBusy`；`setPrtgBackfillSpinnerText` 與 `setPrtgProbeSpinnerText` 逐行相同 | `runs.js:1260-1279`、`prtg-admin.js:1212-1232`、`rules.js:600`、`runs.js:1633`、`prtg-admin.js:960` |
| A-14 | `RunsPageUiTests` 以全檔子字串斷言為主，擋不住顯示邏輯錯誤（BACKLOG 已登錄） | `LogForesight.Tests/RunsPageUiTests.cs` |

### 定案

1. **取數執行中 → AI 卡兩顆啟動鈕都隱藏**，該位置改顯示一行說明「取數執行中，AI 分析會自動跟隨；結束後可手動補跑」。
   後端 `POST ai-run` 在 `SchedulerRunState.IsRunning` 時拒絕，回 **409**。
   守門**只在 API 層**：`AiAnalysisHostedService.TriggerRunAsync` 不加此檢查，否則取數內部的 `fetch-followup` 觸發會被自己擋掉、「AI 跟隨取數」失效。
   不選「只擋強制重跑、保留補跑」：補跑此時多半是空跑，留著只會讓人以為壞了。
2. **AI 未設定 → 兩顆啟動鈕隱藏**，位置改顯示「AI 服務未設定」並連到設定頁（與閒置說明同一文案來源）；後端 `ai-run` 在 `!Available` 時回 400。
3. **停止鈕加 `withBusy`**；強制重跑 modal：輪詢偵測到 AI `isRunning` 由 false→true 時停用確認鈕並顯示「已有執行開始，請關閉後重新確認」；
   modal 文案帶觸發者：「目前由〈TriggerText〉觸發的 AI 執行將被停止後重新開始」。後端「停止→重跑」行為不改。
4. **`prtgModuleEnabled` 三態語意收緊**：`null`（未知）視同未啟用——啟動鈕 disabled、點擊第二道檢查一併擋下；只有明確 `true` 才放行。
5. **狀態碼統一**：「沒有可停止的執行」與「被互斥擋下（另一執行中）」一律 **409**；輸入驗證錯誤維持 400。
   訊息文字維持既有中文（前端 toast 直接顯示伺服器訊息，不依狀態碼組字）。
6. **全站執行中告示搬到共用 layout**：所有已登入頁面都看得到；執行中 30 秒輪詢、閒置 60 秒；
   閒置時零高度不佔位；執行中單行、不可關閉、不推擠主內容（固定在頁首下方的既有告示位）；
   文字含觸發者與進度（沿用 `/api/run-activity` 的 DTO，欄位不夠就補 `triggerText`，儀表板與 AI 卡用同一欄位）。
   儀表板原本自己的告示移除（單一來源）。
7. **主機詳情「立即更新此主機」在排程執行中改為 disabled ＋ 說明「排程執行中，結束後可用」**，不隱藏（隱藏會被誤讀為權限被拿掉）。
   狀態來源同第 6 點的告示輪詢（layout 廣播事件，頁面訂閱），不另開輪詢。
8. **`aiAvailable` 收進 `core/api.js`** 的 `getAiStatus()`，模組快取一次，五頁改用；判定「AI 可用」全站只剩一處。
9. **bug 修正**：`daysWrap` 改為正確的元素引用或移除該行；AI 判讀只在**成功**時鎖鈕，失敗可重試；4 處補 catch（錯誤已由 api.js toast，catch 只吞）；`rule-validate` 加 `withBusy`；
   spinner 文字 helper 抽到 `core/ui.js`，兩頁改用。
10. 驗收改用**結構斷言**：按鈕顯示邏輯以「給定狀態 → 元素 class／disabled」的函式級測試或 DOM 模擬驗，不再新增全檔子字串斷言。

### 改動

1. `runs.js`：AI 卡按鈕顯示規則納入取數 `isRunning` 與 `aiAvailable`；說明列；停止鈕 `withBusy`；modal 防護與文案；`prtgModuleEnabled` 三態；`daysWrap` 修正；補 catch。
2. `ScheduleController.cs`：`ai-run` 兩道守門（取數執行中 409、AI 未設定 400）；`cancel`／`ai-cancel` 無執行可停改 409。
3. `SettingsController.cs`：`prtg-backfill/start` 互斥被擋改 409（驗證錯誤仍 400）。
4. `core/layout.js`＋`Views/Shared` 版型：全站告示與輪詢、廣播事件；`dashboard.js` 移除自己的告示。
5. `host-detail.js`：訂閱事件，執行中 disabled ＋ 說明。
6. `core/api.js`：`getAiStatus()`；五頁改用。
7. `core/ui.js`：spinner 文字 helper；`runs.js`／`prtg-admin.js` 改用。
8. `record-detail.js`：AI 判讀鎖鈕改成功時才鎖。
9. `rules.js`：`rule-validate` 加 `withBusy`。
10. 文件：WEB-SPEC §9.10「動作鈕互斥」改寫為跨卡規則（含第 1、2、4 點）；§8.6 新增「全站執行中告示」條；§7.2 API 慣例補狀態碼規則（409＝與另一執行互斥或無可停止對象）。

### 測試／驗收

- A-V1 給定 `schedule.isRunning=true`、`ai.isRunning=false`、`aiAvailable=true`：AI 卡兩顆啟動鈕 `d-none`、說明列可見；反例：`schedule.isRunning=false` 時兩顆可見、說明列隱藏。
- A-V2 給定 `aiAvailable=false`：兩顆啟動鈕 `d-none`、「AI 服務未設定」可見；反例：`true` 時不顯示該說明。
- A-V3 後端：取數執行中呼叫 `ai-run` → 409 且 `AiAnalysisRunState` 未進入執行；**反例**：`SchedulerRunState.IsRunning=true` 時由 `fetch-followup` 路徑觸發 `TriggerRunAsync` 仍能開跑（守門不在 service 層）。
- A-V4 後端：`Available=false` 呼叫 `ai-run` → 400。
- A-V5 `prtgModuleEnabled=null` 時 PRTG 兩顆啟動鈕 disabled 且點擊不送 API；`true` 時可按。
- A-V6 `cancel`／`ai-cancel`／`prtg-backfill/cancel`／`prtg-structure-sync/cancel` 無執行可停 → 全部 409；`prtg-backfill/start` 被互斥擋 → 409、回填天數不合法 → 400。
- A-V7 「執行模式」下拉切到每一個非 `None` 值不擲例外，警示文字顯示對應內容。
- A-V8 AI 判讀：模擬 API 失敗後按鈕仍可按；成功後 disabled。
- A-V9 layout：`run-activity.isRunning=false` 時告示元素高度 0 且無文字；`true` 時含觸發者與進度文字；輪詢間隔依狀態切換 30／60 秒（以可注入的計時器或斷言 `setTimeout` 參數驗）。儀表板檔案內不再有 `run-activity` 呼叫。
- A-V10 主機詳情：收到執行中事件後「立即更新」disabled 且說明可見；結束事件後恢復。
- A-V11 grep：`/api/ai/status` 在 `pages/` 下零直接呼叫，只剩 `core/api.js` 一處。
- A-V12 4 處 listener 皆有 catch；`rule-validate` 點擊期間 disabled。
- A-V13 現有 3955 測試全綠。

---

## 作業 B：資料載入效能與快取

### 現況與核對結果

| # | 事實 | 證據 |
|---|---|---|
| B-1 | 執行紀錄 store：三支列表 API、執行詳情 `GetRun`／`GetLogs`、以及**建構式**（算最後 id）全部把 `lf_log_lines` 該 `log_key` 全撈再逐行 JSON 解析；`(log_key, created_at)` 索引存在但不用；表只有 `log_key/seq/created_at/line`，`runId` 在 JSON 裡無法 SQL 過濾。保留 120 天（`RunLogRetentionDays`），`Prune` 依 `created_at` | `EfJsonLogStore.cs:26-34`、`BatchRunStore.cs:154-155,190-244`、`SystemSettings.cs:219` |
| B-2 | `/api/records` 帶 statuses／overdue／unassigned 走記憶體慢路徑，`from` 缺省＝`DateTime.MinValue`；`Progress(r)` 在篩選與投影各算一次。詳情頁「下一筆未處理」每次載入與每次批次儲存都打這支（不帶日期、pageSize 200）且與主載入串行 | `RecordListQueryService.cs:105-170,572-573`、`record-detail.js:160-181` |
| B-3 | 處理狀態是記憶體推導（`DayHandlingDerivation`），無法把狀態篩選下推 SQL（BACKLOG 規模化段） | `docs/BACKLOG.md` |
| B-4 | `JsonBlobSingleton.Get()` 每次讀 DB 並反序列化整份 blob，無快取；`SystemSettingsStore` 是它的子類，Web 端 35 個 `_store.Get()` 呼叫點；`JsonBlobCollection(cached:true)` 已有版本探測快取；`AiCacheStore` 未開快取 | `JsonBlobSingleton.cs:19`、`JsonBlobCollection.cs:30,61`、`AiCacheStore.cs:11` |
| B-5 | `SummaryCache` 由中介軟體在**任何**成功非 GET 請求後 `Bump()`（含改字級、登出、help 提問），註解自承取捨 | `Program.cs:198-213` |
| B-6 | 非 ViewAll 使用者若被指派為問題負責人，每個請求都跑一次橫跨整個保留期的 `HostIdsFor` 聚合算可見主機，快取只活在該請求；每頁側欄徽章另打一支 `/api/handlers/{me}/workload` | `HostVisibilityResolver.cs:62-86`、`VisibilityService.cs:112-197`、`layout.js:211` |
| B-7 | PRTG：`lf_prtg_values` 以 `period_start` 區間查、`lf_prtg_state_changes` 以 `changed_at` 區間查與 `Max()`，兩者都不是索引前導欄；主機詳情 PRTG 頁籤 `GetLatestHostMap()` 全表後記憶體取一台；衝突清單每次翻頁 `GetAllDevices()` 全表重建索引 | `EfPrtgStore.cs:551-588,784-802`、`DashboardController.cs:152-167`、`SettingsController.cs:617-660`、`DB-SPEC.md:465` |
| B-8 | `SqlPerformanceMonitor` 門檻 2000ms，只留累計數與「最慢一筆」；埋點只在 4 個 store，`EfPrtgStore`／`EfJsonLogStore`／`PermissionChangeStore` 沒有；健康頁只顯示累計數字 | `SqlPerformanceMonitor.cs:26,71-83`、`settings.js:563` |
| B-9 | `core/api.js` 的 fetch 無逾時；reports／settings／prtg-admin／prtg-calibration／help-manual 五頁無 loading 指示 | `api.js:47-56` |
| B-10 | 校準 residual 快取：命中判定讀 `_cachedAt`，但寫入 residual 時沒更新它（只有 `AssessStatus` 會寫）→ 未先 `AssessStatus` 就永不命中；剛 `AssessStatus` 過則舊 residual 被判新鮮 | `CalibrationService.cs:1220-1233,346,372` |
| B-11 | `permission-changes` 的 `pageSize` 可由 URL 指定，前端只檢查 `>0`，後端未經 `Paging.Normalize` | `permission-changes.js:149-151`、`PermissionChangeStore.cs:19` |
| B-12 | `SentinelEventFetchService.Cache` 無條目上限與清理 | `SentinelEventFetchService.cs:41-84` |
| B-13 | `LatestOccurrences` SQL 只用 `event_id+record_date`，`source_name` 拉回記憶體比對（BACKLOG 慢查詢，實機 7 秒以上） | `EfIssueAggregateQuery.cs:319-326` |
| B-14 | `IssueTodoQuery.ResolveActionable` 已有跨請求快取（`ActionableSnapshotCache`：from/to/可見主機/riskLevels/severities，TTL 30 秒） | `IssueTodoQuery.cs:45-52` |

### 定案

1. **執行紀錄查詢下推到 SQL**（B1）：`GetRecentRuns`／`GetRecentErrors` 改用 `EfJsonLogStore` 既有的
   `ReadLines(from, to)` 帶日期下界讀取（走既有 `(log_key, created_at)` 索引），cutoff 再往前放寬一天緩衝
   （附加時間與業務時間不同源，跨午夜會漏）；記憶體端的業務時間過濾全部保留，因為底層對
   `created_at` 為 null 的既存列一律視為在範圍內，SQL 窄化不保證精確。
   建構式算最後 id 改用既有的 `ReadLastLines(n)`（索引反向 seek，該方法的註解已寫明它就是為此而生），
   往回多讀幾行以免最後一行損毀導致重號。`GetRun`／`GetLogs` 以保留期或該次執行的起迄時間為窗口讀取後
   再依 runId 比對（runId 在 JSON 內、不是資料表欄位，無法 SQL 過濾）。
   **推翻原定案「改常駐記憶體投影」**（實作期核對發現）：`BatchRunStore` 在同一個 Web 行程裡有**兩個實例**
   ——DI 的 Singleton（頁面查詢用）與 `AnalysisOrchestrator` 執行分析時自己 `new` 的那一個（寫入用，
   見 `AnalysisOrchestrator.cs:204`）。兩者寫同一張表卻各自持有狀態，做成投影會讓頁面永遠看不到
   排程剛寫入的執行紀錄。要走投影就得先讓兩邊共用同一個實例，那是另一個層級的改動，本輪不做。
2. **records 慢路徑**（B2）：
   - 「下一筆未處理」捷徑改走**新端點** `GET /api/records/next-unhandled?hostId&date`：資料來源沿用既有處理狀態推導，窗口＝保留期（**不藏老案子**），
     結果走跨請求快取（鍵＝資料版本戳＋可見主機集合＋可見嚴重度，TTL 30 秒；是否直接複用 `ActionableSnapshotCache` 標**暫定**，執行端依實作事實決定），只回傳下一筆的 hostId／date。
     前端在主載入完成後**非阻塞**打；失敗不顯示捷徑（現行行為）。
   - `/api/records` **無狀態篩選**路徑：`from` 缺省改為「昨天往前 90 天」（與主機詳情 clamp 同源常數）；**有狀態篩選**路徑維持全保留期，但 `Progress(r)` 每筆只算一次（先算再篩再投影）。
   - `LatestOccurrences` 的 `source_name` 下推到 SQL（B13）。
3. **`JsonBlobSingleton` 加版本探測快取**（B4）：與 `JsonBlobCollection(cached:true)` 同一機制（每次 `Get` 先探測版本，版本相同回快取物件的**深副本或不可變快照**，不同才重讀）；
   `SystemSettingsStore` 開啟。
   **快取的是原始內容與版本、命中時仍各自反序列化出新物件**：單一物件型 store 的呼叫端會做讀→改→寫，
   `SystemSettingsService` 內已有註解明講它假設「每次 `Get()` 都是不同執行個體」，共用實例會讓前後快照變成同一個物件。
   **`AiCacheStore` 本輪不開**（規劃時定為要開，實作期改判）：它的內容是 AI 產出的整包文字（可能很大）
   且每次 `Put` 都推進版本讓快取立刻失效，效益不明而記憶體風險明確。
   回傳必須是副本：既有註解假設「每次 `Get()` 都是不同物件」（`SystemSettingsService.cs:320`），快取共用同一實例會讓讀→改→寫的 `before` 快照被汙染。
4. **`SummaryCache` 失效白名單**（B5）：中介軟體改為「非 GET 且路徑**不在**白名單」才 `Bump()`；白名單**只列明確不改分析資料的端點**：
   `display-settings`、`auth/*`、`help/ask`、`ai/interpret-issue`（讀取型）。其餘（含所有處理狀態、規則、設定、排程寫入）照舊推進。
5. **可見範圍跨請求快取**（B6）：`VisibilityService` 的可見主機集合改為跨請求快取，鍵＝userId＋`DataVersionStamp.Current`，TTL 60 秒，上限 256 筆整批清；
   版本戳一推進即失效，授權變更不延遲超過一次寫入。ViewAll 短路徑不變。
6. **PRTG**（B7）：`SchemaUpgrader` 補 `lf_prtg_values(period_start)` 與 `lf_prtg_state_changes(changed_at)` 兩條索引（兩後端）；
   主機詳情 PRTG 頁籤改依 hostId 查對應（store 新增依主機取最新對應的方法）；衝突清單的 device 索引改跨請求快取（鍵＝版本戳，TTL 30 秒）。
   **升級注意**：大表首次建索引會拖長站台啟動（SQLite）或鎖表（SQL Server），寫進 DB-SPEC 升級注意事項；不在夜間排程窗口內升級。
7. **慢查詢可觀測**（B8）：`EfPrtgStore`／`EfJsonLogStore`／`PermissionChangeStore` 補 `_performance?.Record` 埋點；
   `SqlPerformanceMonitor` 改記「最慢前 10 支（operation、次數、最大耗時、最近時間）」；健康頁對應顯示表格。
8. **前端逾時與 loading**（B9）：`core/api.js` **只對 GET** 加逾時（AbortController，預設 60 秒，可由呼叫端覆寫）；POST／PUT／DELETE 一律不逾時。
   逾時時 toast 文案「查詢逾時，請縮小範圍或稍後重試」並擲 `ApiError('timeout')`；`guardLoad` 遇 timeout 在容器內顯示錯誤與「重試」鈕。
   五頁補 `renderLoading`／`guardLoad`。逾時是止血不是效能修正：B1～B7 各有量化驗收。
9. **bug 修正**：校準 residual 快取寫入時一併更新自己的時間戳（與 `AssessStatus` 分開的 `_cachedResidualAt`）；`permission-changes` pageSize 走 `Paging.Normalize`；`SentinelEventFetchService.Cache` 加條目上限（暫定 512，超過整批清）。

### 改動

1. `LogForesight.Core/Persistence/Sql/EfJsonLogStore.cs`：時間下界／區間讀取。`BatchRunStore.cs`：記憶體投影＋窗口讀 log。
2. `LogForesight.Web/Controllers/Api/RecordsController.cs`＋`RecordListQueryService.cs`：`next-unhandled` 端點、缺省 from 常數、`Progress` 單次計算；`EfIssueAggregateQuery.cs`：`LatestOccurrences` 下推。`record-detail.js`：捷徑改端點與非阻塞。
3. `JsonBlobSingleton.cs`：版本探測快取＋每次回傳新物件；`SystemSettingsStore` 開啟。
4. `Program.cs`：白名單。
5. `VisibilityService.cs`：跨請求快取（新 `VisibilityCache` 單例，Scoped 服務先查它）。
6. `SchemaUpgrader.cs`＋`EfPrtgStore.cs`＋`DashboardController.cs`（host-detail prtg）＋`SettingsController.cs`（衝突清單索引快取）。
7. `SqlPerformanceMonitor.cs`＋三個 store 埋點＋`HealthService`／`settings.js` 健康頁籤。
8. `core/api.js`、`core/ui.js`（guardLoad timeout 分支）、五頁 loading。
9. `CalibrationService.cs`、`PermissionChangeStore.cs`／`PermissionChangeService.cs`、`SentinelEventFetchService.cs`。
10. 文件：DB-SPEC（索引＋升級注意）、WEB-SPEC §9.10（執行紀錄資料路徑）、§9.3（捷徑端點）、§8.6（逾時與 loading 規範）、§9.9b（健康頁慢查詢表）、PRTG-SPEC（校準快取）、BACKLOG（移除已修項：`LatestOccurrences`、衝突清單 device 索引；保留仍未修項）。

### 測試／驗收

- B-V1 執行紀錄：以可計數的假 log store 驗——(a) 建構式只讀 run 分區且帶時間下界，不讀 log 分區；(b) `GetRecentRuns`／`GetRun` 不觸發任何 store 讀取（全走投影）；(c) `GetLogs(runId)` 只讀該執行起迄窗口內的行；(d) `Register`→`Finish`→`GetRun` 立即看到結束列；(e) `Prune` 後投影不含被清的 run。**突變**：把 (b) 的投影讀取改回 store 讀取，(b) 必須紅。
- B-V2 `next-unhandled`：資料集含一筆 200 天前仍 open 的高風險日 → 端點必須能回它（不藏老案子）；同一版本戳 30 秒內第二次呼叫不觸發推導（計數器）；寫入任一處理狀態後（版本戳推進）下一次呼叫重新推導。前端：主載入完成前不呼叫該端點（順序斷言）。
- B-V3 `/api/records` 無狀態篩選且不帶 from → 查詢下界＝昨天−90 天；帶 statuses 且不帶 from → 下界＝保留期起點（**反例**：不可被 90 天限制）。`Progress` 在慢路徑對每筆恰好呼叫一次（計數替身）。
- B-V4 `LatestOccurrences`：以含同 event_id 不同 source_name 的資料驗結果不變，且 SQL 端回傳列數等於命中列數（不再拉回多餘列）。
- B-V5 `JsonBlobSingleton`：連續兩次 `Get` 只讀一次 blob 內容（版本探測允許）；`Update` 後下一次 `Get` 重讀；`Get` 回傳物件被呼叫端修改不影響下一次 `Get` 的內容（副本反例）。`SystemSettingsService` 既有讀→改→寫測試全綠。
- B-V6 白名單：對 `display-settings` PUT 後版本戳不變；對處理狀態寫入端點 POST 後版本戳必須推進（**反例守門**）；未知的新非 GET 端點預設推進。
- B-V7 可見範圍：同一 userId 60 秒內第二個請求不呼叫 `HostIdsFor`；版本戳推進後重新計算；不同 userId 互不命中；ViewAll 不進快取路徑。
- B-V8 PRTG：`SchemaUpgrader` 在 SQLite 測試庫升級後 `PRAGMA index_list` 含兩條新索引（規格即測試）；主機詳情 PRTG 頁籤只查該主機的對應列（計數替身）；衝突清單同版本戳翻頁不重建 device 索引。
- B-V9 慢查詢：三個 store 的慢操作會進監控；監控最多保留 10 支且依最大耗時排序；健康 DTO 含該清單。
- B-V10 api.js：GET 超過逾時擲 `ApiError('timeout')` 且 toast 一次；POST 不受逾時影響（以假 fetch 驗）；`guardLoad` 遇 timeout 渲染重試鈕，按下重跑 loader。五頁載入期間容器內有 loading 元素（結構斷言）。
- B-V11 校準：未呼叫 `AssessStatus` 直接兩次 `BuildResidualCandidateRows` 第二次命中；超過 TTL 不命中。`permission-changes` `pageSize=100000` 被夾到上限。Sentinel 快取超過上限後條目數不再成長。
- B-V12 現有測試全綠；新增測試數記入體檢交接。

---

## 明確不做（本輪定案）

- **報表 scope≠all 的 SQL 下推**：處理狀態推導在記憶體，下推是架構級改動；維持 BACKLOG。
- **`IssueRankingCache` 鍵補 `hostSnapshot`／深副本**：外層 `SummaryCache` 已縮小影響；維持 BACKLOG。
- **`prtg-admin.js` 全面改用 `renderTable`／`renderEmpty`**：純風格、範圍大、無行為差異。
- **儀表板首載冷路徑預熱**：每個使用者第一次載入 ≥10 支聚合無法靠快取消除；等 B5 白名單與 B6 生效後實測再議。
- **強制重跑改為「執行中一律拒絕」**：既有「停止→重跑」設計有明確競態理由，本輪只補告知與 modal 防護。
- **PRTG／NetIQ 探測加停止鈕**：探測本身短，且探測有自己的互斥；不動。
- **PRTG 衝突清單改 SQL 分頁**：維持 BACKLOG（本輪只做 device 索引快取）。

## 規劃完成後複檢

- **與既有設計的衝突**：定案 A1 推翻 WEB-SPEC §9.10「動作鈕互斥」原本只在卡內的規則，改為跨卡；文件同步改寫。
  定案 A6 移除儀表板獨有告示，§9.1 對應段要刪。定案 B3 與 `SystemSettingsService.cs:320` 註解的「每次 Get 都是不同物件」假設相容（回傳副本）。
  定案 B4 與 `Program.cs` 註解「新端點自動涵蓋」相容（白名單是排除法，新端點仍預設推進）。
- **批次之間的衝突**：A 與 B 共碰 `runs.js`（A 改按鈕邏輯、B6 只加 loading）與 `core/ui.js`（A 加 spinner helper、B 加 guardLoad timeout 分支）——先做 A 再做 B，B 的規格檔要以 A 完成後的檔案為基準。
- **四個坑**：
  - 什麼算一個／分母為零：B-V2「下一筆」在無未處理時回空、前端不顯示捷徑；B-V9 慢查詢清單為空時健康頁顯示「尚無慢查詢」不是空表。
  - 破壞性判準反例：B4 白名單反例（處理狀態寫入必推進）已列；B3 副本反例已列。
  - 單向閘門繞過：A1 的守門只在 API 層，內部 `fetch-followup` 必須繞過（A-V3 反例）；A4 的 `null` 視同未啟用，繞過路徑＝設定載入完成後 `renderPrtgModuleState` 重設為 `true`。
  - 移除類依賴方：A6 移除儀表板告示，依賴方只有 `dashboard.js` 自身（grep `run-activity` 僅 dashboard 與 health）；A8 `aiAvailable` 收斂的五個呼叫端全列於 A-12。
- **升級／既有資料**：B6 索引升級注意已寫；B1 投影啟動時舊資料照 `created_at` 載入，無 schema 變更。
- 複檢完成，新增事項已併入上文（A6 依賴方、B-V9 空清單）。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1 後端守門與狀態碼 | impl-low | 全綠 3967（+12） | 契約四條各有測試；Claude 另做兩次突變（停用取數守門、翻轉互斥旗標）皆轉紅 | 執行端把「回填已在執行中」也歸為互斥回 409（規格只列三項），理由是與結構同步的參照實作一致，採納 |
| A2 排程頁顯示規則 | impl-low | 全綠 3973（+6） | 三因子隱藏、說明列、六處旗標、停止鈕、modal 防護各有結構斷言；Claude 另做兩次突變皆轉紅 | Claude 驗收時誤用 `git checkout` 還原突變，洗掉尚未提交的 `runs.js`；由執行端以同一支腳本重跑產出位元組相同的檔案復原。往後突變一律先複製備份再改 |
| A3 全站執行中告示 | impl-low | 全綠 3986（+19） | 觸發者文字兩端一致、取數優先、輪詢間隔與不停掉、儀表板零命中、主機詳情訂閱各有測試；Claude 另做兩次突變皆轉紅 | 三處偏離皆採納：兩顆按鈕都停用並補 id、說明元素改由 JS 建立（cshtml 不在白名單）、事件名用字面量不跨檔 import（`asp-append-version` 會讓 layout 模組被執行第二次）。`RunMonitorService` 第三份觸發者文字對 null 回「工作排程器」是歷史紀錄語意，與即時狀態的「閒置」不同，刻意不收斂 |

## 體檢交接

（實作輪收官時填：全量測試總數、全綠與否、與基線 3955 的差。）
