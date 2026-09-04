# 回饋第 38 輪規劃：PRTG 維護頁重組、衝突對應操作、排程狀態卡與執行紀錄結構化、資源守門

> 狀態：規劃完成，待使用者確認後開始委派
> 基準：dev@233e6c9（3378 綠、略過 6）
> 來源：使用者實測回饋清單（PRTG 維護頁 1.2~1.8、排程作業頁 2.1~2.5、資源守門 3）
> 委派模型：agy `gemini-3.8-flash-high`，整輪不換；幾行內小修標「Claude」。

## 已定案的討論結果

| 待決 | 結論 |
|---|---|
| 1.1 | 刪除（空項） |
| 排除粒度 | **以 IP 為單位**：使用者的語意是「NetIQ 的這個 IP 不去查 PRTG」。被排除的 IP 底下所有 device 都不進對應、不取數、不評規則 |
| 2.4 呈現位置 | 採管理者「今晚各路有沒有跑好」視角：**執行紀錄列表分欄**（本機／NetIQ／PRTG 各自的成果與失敗數）＋**執行總表每日列加一格 PRTG 狀態**（成功／部分失敗／失敗／未啟用）。總表主體仍是主機×日，不拆成兩區——PRTG 沒有主機日語意，硬拆會做出一張空表 |
| 本機路徑 AI | **本機改與 NetIQ 一致，只標 `AiPending` 交給 AI 分析排程**。理由見批次E |
| 資源守門 sensor 來源 | **先自動偵測，維護頁提供覆寫清單**；門檻與間隔開設定，預設值見批次F |
| 慢 SQL 不計入狀態、AI 排程錯誤不再算進取數執行 | 接受 |

## 核對表（步驟 1 產出，摘要）

| 項目 | 判定 | 證據 |
|---|---|---|
| 1.2 分頁與排除 | ⚠️ | `SettingsController.GetPrtgMirrorStatus` 衝突／未對應各 `Take(20)`；前端 `renderList` 無分頁；`core/ui.js` 有 `renderPagination` 可重用；「排除」不存在。衝突分兩型：A「同 IP 多 device」不填主機（`PrtgHostMapper.cs:113-136`）、B「一 device 的 IP 對到多台主機」對到 HostId 最小者（`:170-192`） |
| 1.3 Objid 欄位 | ⚠️ | 已 `readonly`，外觀像可編輯 |
| 1.4 下拉來源 | ✅ | `GET /api/admin/hosts?pageSize=2000` 全部主機；**後端 `Paging.Normalize` 夾成 200 台**，超過的主機選不到 |
| 1.5／1.7 搬家 | ✅ | 校準匯出是獨立頁 `/admin/calibration`＋`layout.js:48` 選單項＋`calibration.js`；資料搬運在「鏡像狀態」頁籤第二張卡 |
| 1.6 未對應清單 | ✅ | 只有此 UI 消費；取數（`PrtgTriggeredValueFetcher.cs:48`）與規則只認 `Ok` |
| 1.8 拆頁籤 | ✅ | 同一張卡、同一顆儲存鈕、同一個 `PUT prtg`；端點欄位已「一律可空、有送才更新」 |
| 2.1 慢半拍 | ✅ | `runs.js:544-562` init 先等 options／ai-status／settings 三支回來才第一次打 status；cshtml 初值空白 |
| 2.2 並行 | ⚠️ 半對 | 三路早已並行（`AnalysisOrchestrator.cs:622-631`）。本機軌**沒有 `local-done`**（NetIQ、PRTG 都有），跑完停在「本機分析 1/1」 |
| 2.3 準備中 | ✅ | `runs.js:906-932` total=0 時丟掉軌別標籤；`prtg-sync` 送 0/0、觸發式取數輪詢期間無分母；NetIQ 完工後軌直接消失 |
| 2.4 拆分 | ✅ | `BatchRun` 只有全域計數，NetIQ／PRTG 成果只在 Milestone 文字 |
| 2.5 本機錯誤 | ❌ 描述失真 | `BatchRunRecorder` 掛的 NLog target 是**全行程所有 logger** 的 Warn 以上規則（`BatchRunRecorder.cs:176-182`）；執行期間前景慢 SQL、儀表板焦點 AI 逾時（`WebAiService.cs:125` 的 8 秒互動 client）、AI 排程的 JSON 失敗全被記進這趟；`WarnCount>0` 即判「有警告」。「影響主機」是執行站台名（`RunMonitorService.cs:357-361`） |
| 3 可行性 | ⚠️ 可做 | `PrtgClient` 只拉 hourly 歷史值；`lastvalue` 僅探測時讀過不落地；排程與 pipeline 只有取消沒有暫停 |

順手發現：人工對應「移除」鈕 `confirmAction` 誤用（`prtg-admin.js:352`，字串＋callback，實際簽章物件＋Promise）→ 刪除永遠打不出去；`ai-status` 每 3 秒呼叫 `CountPendingAi`（實機 7~15 秒）；AI JSON 契約失敗訊息不含回覆片段無法診斷；`LatestOccurrences` 把 `source_name` 過濾留在記憶體（記 BACKLOG，本輪不動）。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 執行者 |
|---|---|---|---|---|
| A | PRTG 維護頁重組：四頁籤、校準匯出與資料搬運搬進「環境探測」、未對應清單移除、Objid 欄位隱藏、hash 頁籤 | 中 | 無 | agy 3 段 |
| B | 衝突清單：分頁端點、IP 排除、依衝突型別分岔的指派 modal、兩個 bug | 中 | A（頁面結構） | agy 3 段 |
| C | 排程狀態卡：首次載入、`local-done`、完工軌保留、不定進度帶軌別、觸發式取數計數 | 中 | 無 | agy 2 段 |
| D | 執行紀錄結構化＋錯誤歸屬：`BatchRun` 分路欄位、NLog 作用域、AI 排程自有 recorder、慢 SQL 不計狀態、`CountPendingAi` 快取、AI 回覆片段 | 大 | C（同檔 runs.js／SchedulerRunState） | agy 4 段 |
| E | 本機路徑改走 AI 分析排程 | 小 | D（歸屬修正後才看得出效果） | agy 1 段 |
| F | 資源守門：設定、自動偵測、即時值讀取、閘門、狀態卡顯示 | 大 | A（設定卡位置）、C（狀態卡） | agy 4 段 |

建議順序：A → B → C → D → E → F。A、B 同檔 `prtg-admin.js`；C、D 同檔 `runs.js`／`SchedulerRunState`；F 最後，它同時消費 A 的頁籤與 C 的狀態卡。

---

## 批次A：PRTG 維護頁重組

### 現況與核對結果
- `Prtg.cshtml` 三頁籤（`config`／`mirror`／`probe`），`ui.js` 的 `bindTabs` 無 hash 路由，重新整理一律回第一頁籤。
- 校準匯出：`PagesController.cs:122-124` 路由、`Calibration.cshtml`、`calibration.js`、`layout.js:48` 選單項、`CalibrationController`（API）。
- 資料搬運：`Prtg.cshtml:274-304`、`prtg-admin.js:575-658`；匯入用原生 `fetch` 繞過 `api.js`（自補 `X-Requested-By`）。
- 未對應清單：`Prtg.cshtml:230-248`、`prtg-admin.js:310`、`SettingsController.cs:195-199`、DTO `PrtgMirrorStatusDto.Unmatched`。
- 「環境探測」頁籤內三層都叫環境探測（頁籤、卡片、`<summary>`），預設收合。

### 定案
- **四頁籤**：`connection` 連線（位址、認證三選一、測試連線、儲存）／`params` 擷取參數（忽略 SSL、逾時、併發、回填天數、保留天數、白名單、儲存；批次F 的資源守門卡也放這裡）／`mirror` 鏡像狀態／`probe` 環境探測（三張卡：環境探測、校準數值匯出、資料搬運（開發用））。
- 連線與參數**各自一顆儲存鈕**，都走 `PUT api/admin/settings/prtg`，**只送該頁籤的欄位**（端點契約「有送才更新」已成立，後端不動）。測試連線只在連線頁籤。
- 校準匯出：`/admin/calibration` 路由、cshtml、選單項移除；`calibration.js` 改為被 `prtg-admin.js` 引入的模組（`pages/prtg-calibration.js` 之類，命名由執行端定），DOM id 不與既有衝突；`CalibrationController` API 與授權不動。**頁籤切到環境探測時不自動跑判定**（維持「按重新計算才跑」）。
- 資料搬運卡搬到環境探測頁籤，匯入改走 `api.js`（若 `api.js` 不支援 FormData，就在 `api.js` 補一個上傳出口，不在頁面自組 header）。
- 環境探測卡拿掉 `<details>` 收合，頁籤本身就是入口；標題只保留卡片一層。
- 未對應清單整段移除（cshtml、js、DTO 欄位 `Unmatched`、controller 的 `Take(20)`）；摘要 badge 的 unmatched **計數保留**。
- `bindTabs` 加選項 `{ hash: true }`：切換時寫 `location.hash = tab`，初始化時若 hash 命中某頁籤就啟用它；其他頁面不傳此選項行為不變。**PRTG 頁與排程作業頁的互連連結**（排程頁「回填天數在 PRTG 維護 頁設定」、設定頁「資料保留」指路）改指向對應頁籤的 hash。
- Objid 欄位隱藏，改在 modal 標題顯示「指派 device {objid}」。
- 「測試連線」需要逾時與忽略 SSL 兩個值（它們在擷取參數頁籤）：測試請求一併帶上參數頁籤當下的這兩個欄位值（未改動就是已儲存值），不讓測試結果與儲存後的行為分岔。
- `/admin/calibration` 路由**保留為轉址**到 `/admin/prtg#probe`（舊書籤與操作說明書連結不 404；Claude 自做）。

### 驗收
- `dotnet test` 全綠；`/admin/calibration` 在 `layout.js`、cshtml 零命中，`PagesController` 只剩轉址一處；`Unmatched` 在 `PrtgProbeDtos.cs`／`prtg-admin.js`／`Prtg.cshtml` 零命中；`X-Requested-By` 在 `prtg-admin.js` 零命中。
- `PrtgAdminPageUiTests` 更新為四頁籤存在性；新增測試：`bindTabs` hash 行為（純函式測試或 UI 字串測試皆可，至少斷言 `hash` 選項存在且預設關）。
- 瀏覽器實測：`/admin/prtg#probe` 直達環境探測；連線頁籤只送連線欄位（network 觀察 payload 不含 `prtgTimeoutSeconds`）。

---

## 批次B：衝突清單分頁、IP 排除、依型別指派

### 現況與核對結果
- 衝突型別 A／B 混在同一表，只靠 Note 區分；指派 modal 目標主機來源是全部主機（被 `Paging` 夾 200）；「移除」鈕 `confirmAction` 誤用。
- 人工對應：`lf_prtg_manual_map`（`device_objid` PK），`PrtgHostMapper.cs:44-93` 優先採用。`PrtgHostMapper` 有完整測試 `PrtgHostMapperTests.cs`。
- 型別 A 的衍生問題：對其中一台 device 做人工對應後，同 IP 剩下的 device 若只剩一台，下一次自動對應會把它判成 `ok` 對到同一台主機（人工對應的 device 已跳出分組）。

### 定案
- **新表 `lf_prtg_ip_excludes`**：`ip`（PK，nvarchar 64，存前後去空白、小寫）、`note`（512）、`created_by`（64）、`created_at`。不清（同 `lf_prtg_manual_map`）。`EnsureCreated`＋`SchemaUpgrader` 冪等 DDL 兩邊維護；DB-SPEC 補一列。
- **對應規則新增兩條**（`PrtgHostMapper`）：
  1. device 的 IP 命中排除清單 → **不產生對應列**，計入「略過（已排除）」，Milestone 與 `prtg-mirror` 摘要各列出排除數。人工對應優先序仍高於排除（同一 device 既有人工對應又被排除 IP 時採人工對應並在 Note 標示）。
  2. 同 IP 有 device 具人工對應時，**其餘 device 不再進入 IP 分組判定**，計入「略過（同 IP 已有人工指定 device）」，不產生列。
  三種「略過」原因在 Milestone 文字分開列出，什麼都沒略過時各項顯示 0。
- **端點**：新增 `GET api/admin/settings/prtg-host-map?status=conflict&page=&pageSize=`（`Paging.Normalize`，pageSize 預設 20），回 `{ items, total, page, pageSize }`，items 帶 `conflictKind`（`multi-device`／`multi-host`）、該 IP 命中的主機清單（型別 A 為 IP 查到的主機 0~N 台、型別 B 為 Note 內的候選）與同 IP 的 device 清單（objid、name、group path）。`prtg-mirror` 只留摘要計數，不再帶清單。`GET／PUT／DELETE api/admin/settings/prtg-ip-excludes` 三個端點，PUT 與 DELETE 寫稽核（新增 `AuditActions` 兩個常數）。
- **清單 UI**：衝突清單用 `renderPagination`；每列顯示型別徽章、IP、device 數／候選主機數、操作兩顆：「指派」「排除此 IP」。排除需 `confirmAction` 且訊息寫明「此 IP 底下 N 台 device 將不再對應與取數」。排除清單獨立一張卡（IP、備註、建立者、時間、移除鈕）。
- **指派 modal 依型別分岔**：
  - 型別 A：radio 列出該 IP 的全部 device（objid＋名稱＋群組路徑），目標主機：IP 命中恰一台時**唯讀顯示**；命中 0 台或多台時給下拉（只列命中的主機，0 台時列全部主機）。送出＝對選中的 device 寫人工對應。
  - 型別 B：目標主機下拉**只列候選主機**（Note 內那 N 台），device 固定。
  - 全部主機的來源改走**新的不分頁端點** `GET api/admin/hosts/all?activeOnly=true`（只回 id、name、ip，Maintain 權限），不再用 `pageSize=2000`；`cachedHosts` 在每次開 modal 時重抓。
- 修 `confirmAction` 呼叫；人工對應清單多顯示「同 IP 另有 N 台 device 已略過」。
- **操作後立即重算今日對應**：衝突清單來自最近一次夜間對應的快照，指派／排除／移除後那一列會留到隔天才消失，使用者會以為沒生效。指派、排除與其移除的四個端點在寫入成功後**同步呼叫 `PrtgHostMapper.MapForDate(今天)`**（就地取代今日列，成本只是讀 device 與主機主檔），重算失敗只回傳警告文字不讓操作失敗；前端操作後重抓清單與摘要。這也讓觸發式取數當晚就能用到新的對應。
- 主機明細頁 PRTG 區塊：主機 IP 在排除清單時顯示「此主機 IP 已排除 PRTG 對應」而不是「無對應」（主機清單的 PRTG 篩選 chips 不動）。

### 驗收
- `PrtgHostMapperTests` 新增：排除 IP 不產生列且計數正確；人工對應優先於排除；同 IP 另一台在人工對應後不再變 `ok`；排除清單為空時行為與現況完全相同（既有測試全綠即證）。
- 新端點測試：分頁 total 正確、`conflictKind` 判定、pageSize 超上限被夾。
- `pageSize=2000` 在 `prtg-admin.js` 零命中；`confirmAction(\`` 字串呼叫形式零命中。
- 瀏覽器實測：移除人工對應真的送出 DELETE；型別 A modal 出現 device radio。

---

## 批次C：排程狀態卡

### 現況與核對結果
- `runs.js:544-562` init 順序；`Runs.cshtml:10` 狀態文字初值空白。
- `AnalysisOrchestrator.cs:1058` `netiq-done`、`:1320` `prtg-done`；本機路徑（`:728` `RunLocalAnalysisAsync`）無完工訊號。
- `SchedulerRunState.ReportProgress`：done 訊號直接清空該軌欄位；`LatestActivity()` 依 netiq > local > prtg。
- `runs.js:906-932` `updateProgressBar` total=0 時只印「準備中…」。
- 觸發式取數（`PrtgTriggeredValueFetcher.cs:100-122`）輪詢期間無分母。

### 定案
- **首次載入**：`refreshScheduleStatus()` 與 options／ai-status／settings 同時發出（不等三支回來）；cshtml 狀態文字初值「載入中…」。
- **`local-done`**：`RunLocalAnalysisAsync` 的 finally 送 `("local-done", 0, 0)`，只在曾送過 `local` 回報時送（與 `netiq-done` 同條件語意）；`SchedulerRunState` 新增常數。
- **完工軌保留**：三個 done 訊號改為**保留該軌最後的 done／total、設該軌 `Completed=true`**，不清空；`EndRun` 才全部歸零。`LatestActivity()` 跳過已完成的軌（優先序不變）。status API 加 `localCompleted／netiqCompleted／prtgCompleted`。前端已完成的軌畫成滿格、無條紋、文字「本機分析　已完成 x / y 主機日」。**既有反例測試**（PRTG phase 不蓋 NetIQ、done 後 LatestActivity 落回本機）必須改成新契約仍成立。
- **不定進度帶軌別**：total=0 時文字為「{軌別}　準備中…」。
- **觸發式取數計數**：`prtg-triggered` 回報 `(done=累計已取 sensor 數, total=0)` 時，前端顯示「PRTG 觸發式取數　已取 n 個 sensor（等待分析結果）」；分析結束收尾掃描後 total 仍為 0，維持此文案直到 `prtg-done`。
- 「上次執行結果」區塊不動。

### 驗收
- `SchedulerRunStateTests` 新增：`local-done` 保留數字並標記完成；三軌完成後 `LatestActivity()` 回 null；`EndRun` 清空 Completed。
- `dotnet test` 全綠；`"local-done"` 在 `AnalysisOrchestrator.cs` 與 `SchedulerRunState.cs` 各恰一處字面值（其餘走常數）。
- 瀏覽器實測：手動執行只含本機時，本機軌跑完顯示「已完成」且整趟結束後消失。

---

## 批次D：執行紀錄結構化與錯誤歸屬

### 現況與核對結果
- `BatchRun`（`BatchRunStore.cs:13-59`，JSON-line 儲存）只有全域 `DaysAnalyzed／AiCalls／AiFailures／WarnCount／ErrorCount`。
- NLog target 全行程收集；`ComputeStatus`（`RunMonitorService.cs:229-240`）`WarnCount>0` → warning。
- AI 排程 `AiAnalysisHostedService.cs:178-188` 直接 new `BatchRun`，無 recorder、無 target。
- `ScheduleController.GetAiStatus` 每次呼叫 `CountPendingAi`；前端執行中每 3 秒輪詢。
- `AIService.ChatJsonAsync` 最終失敗訊息（`:418-419`）不含回覆內容。
- 執行紀錄列表 `RunListItemDto`、每日彙總 `RunDaySummaryDto`、詳情 `RunDetailDto`。

### 定案
- **`BatchRun` 新增可空欄位**（舊紀錄反序列化為 null，畫面顯示「—」）：`LocalDaysAnalyzed／LocalDaysFailed`、`NetiqDaysAnalyzed／NetiqDaysFailed／NetiqHostsSkipped`、`PrtgOutcome`（`disabled`／`success`／`partial`／`failed`／null）、`PrtgSensorsFetched／PrtgSensorsFailed／PrtgTriggeredHosts`。由 `AnalysisOrchestrator` 各路收尾時經 `BatchRunRecorder` 新方法寫入（lock 比照既有計數）。`DaysAnalyzed` 維持＝本機＋NetIQ 合計，舊讀取端不變。`PrtgOutcome` 判定：未啟用→`disabled`；結構同步失敗→`failed`；同步成功但有 sensor 失敗→`partial`；否則 `success`。「什麼算一個失敗 sensor」沿用 `FetchValuesForSensorsAsync` 回傳的 failedSensors。
- **呈現**：執行紀錄列表新增三欄「本機／NetIQ／PRTG」（各為「分析 n（失敗 m）」與 PRTG 狀態徽章＋sensor 數）；執行詳情頭部同樣三格；執行總表每日列加一格「PRTG」徽章（該日取數類 BatchRun 中取最後一筆的 `PrtgOutcome`，無紀錄顯示「—」；**BatchRun 歸到哪一天沿用該列既有主機狀態的日期歸屬規則**，不另訂）。WEB-SPEC §9.10 同步。
- **NLog 作用域**：`BatchRunRecorder` 建立時 `ScopeContext.PushProperty("lf_run_id", runId)`，`Finish`／`Dispose` 時 pop；target `Write` 只收 `ScopeContext` 內 `lf_run_id` 等於自己 runId 的事件。前景請求與其他背景服務因此不再被記入。**繞過路徑**：無 scope 的事件（例如執行緒池外的 Timer callback）會被丟棄——這是預期行為，完整診斷仍在 `logs\logforesight.log`。
  **AsyncLocal 的坑**：scope 必須在**涵蓋整趟執行的那個 async 方法本體**內推入（現況 recorder 在 `AnalysisOrchestrator.RunAsync` 本體 `:204` 直接建構，符合）；若日後把建構搬進一個被 `await` 的輔助 async 方法，scope 在該方法返回時就消失、之後整趟的 Warn 全部丟失且沒有任何訊號。recorder 的註解要寫明這條限制，並以測試釘住「在 recorder 建構後的 `await` 之後、`Parallel.ForEachAsync` 子任務內的 Warn 仍被記錄」。
- **AI 排程改用 `BatchRunRecorder`**（建構參數加 `jobType`），自己的 Warn 以上進自己的執行紀錄；`AiCalls／AiFailures` 寫法不變。
- **慢 SQL 不計狀態**：`OnLogRecorded` 對 logger 名稱為 `SqlPerformanceMonitor` 的事件**照樣寫入詳情但不遞增 `WarnCount`**；`ComputeStatus` 不動。
- **`CountPendingAi` 快取**：`AiAnalysisRunState`（或 `ScheduleController`）持有 30 秒快取（暫定），AI 排程每輪結束時使快取失效。
- **AI 回覆片段**：`ChatJsonAsync` 最終失敗訊息附「最後回覆前 300 字（換行與連續空白摺成單一空白）」；回覆為空時寫「（空回覆）」。**不**附 prompt。
- 異常彙總「影響主機」欄改名「執行站台」（只改文案與 WEB-SPEC，不改資料）。

### 驗收
- 新增測試：scope 內 Warn 被記錄（含 `Parallel.ForEachAsync` 子任務內）、scope 外不被記錄——**「scope 外」要用 `ExecutionContext.SuppressFlow()` 或另一個 runId 的 scope 模擬，不能用 `Task.Run`（它會繼承 AsyncLocal，測試會假通過）**；`SqlPerformanceMonitor` 事件寫入但 `WarnCount` 為 0；舊格式 JSON-line 反序列化新欄位為 null；`ComputeStatus` 對 `PrtgOutcome=failed` 仍以既有規則判定（PRTG 失敗不改整趟狀態，與 PRTG-SPEC §3 失敗語意一致）。
- `new BatchRun` 在 `AiAnalysisHostedService.cs` 零命中。
- `dotnet test` 全綠；測試總數比基準多。
- 瀏覽器實測：執行期間開儀表板觸發慢 SQL，執行詳情不出現該 Warn。

---

## 批次E：本機路徑改走 AI 分析排程

### 現況與核對結果
- 本機 `AnalyzeDayAsync(useAi: settings.Ai.IsConfigured)` 同步呼叫 AI（screening＋主分析＋逐類別 DeepDive，JSON 重試 3 次），NetIQ 走 `BuildStatisticalRecordAsync` 只標 `AiPending`。
- AI 分析排程 `ScheduleOptions.AiEnabled` 預設 false；待補件數卡在排程作業頁。

### 評估（回答決定 4）
排程全流程現況：取數執行（本機 ∥ NetIQ ∥ PRTG）→ 各自寫紀錄 → AI 分析排程在自己的窗口消化 `AiPending`。本機是唯一還在取數執行內同步等 AI 的路徑，後果：本機軌被 AI 重試拖長、AI 失敗混進取數執行的錯誤、兩路 AI 策略不一致（NetIQ 低風險日不跑 AI、本機全跑）。**建議統一**：本機同樣只標 `AiPending`。代價：AI 排程未啟用時本機主機也不會有 AI 判讀——這與 NetIQ 主機今天的處境相同，屬一致而非退化；排程作業頁已有待補積壓數，再加一行提示即可。

### 可行性核對（複檢補）
- 本機路徑已在每日分析後寫入風險 log 暫存（`AnalysisOrchestrator.cs:912` `ReplaceRiskyEvents`），AI 排程的 `RetryAiAsync` 正是從暫存重建輸入並含深析報告（`LogAnalysisService.cs:544-575`、排程端有 `RiskReportService`），本機待補件走同一條路不缺資料。
- 本機主機正常會被登記而有 `HostId`（`:267-272`）；`HostId=0`（登記失敗）時暫存與待補查詢都對不上。
- 週期性體檢（`WeeklyCheckupService`，獨立 AI 型別）不在本輪範圍，維持行內呼叫。

### 定案
- 本機路徑改走與 NetIQ 相同的統計＋`AiPending` 流程（`HostDayPostProcessor`／`NeedsBackfill` 語意不變：`AiPending=true` 不算待補）。`useAi` 對本機只剩「AI 未設定時 `AiPending=false`」的用途。**本機 `HostId=0` 時維持統計模式、`AiPending=false`，並寫一則 Milestone 警告**（不能標一筆永遠沒人消化的待補）。
- 排程作業頁 AI 分析狀態卡：AI 已設定、AI 排程未啟用且待補 > 0 時顯示警示「AI 分析排程未啟用，N 件待補不會被處理」。
- 「立即執行」modal 的預設模式文案若提到「AI 已設定且未分析」，維持不變（判定本來就排除 `AiPending`）。

### 驗收
- 新增測試：本機分析在 AI 已設定時寫出 `AiPending=true` 且不呼叫 AI 服務（替身斷言零呼叫）；AI 未設定時 `AiPending=false`。
- 依賴「`AnalyzeDayAsync(useAi:true)` 行內完成 AI」的既有測試依新契約改寫或移除（移除要在執行紀錄寫明理由）；`LogAnalysisServiceRetryAiTests` 測的是 `RetryAiAsync`，**不在受影響清單**，必須維持全綠。
- `dotnet test` 全綠。

---

## 批次F：資源守門

### 現況與核對結果
- `PrtgClient.GetJsonAsync` 為唯一資料入口；`lastvalue` 只在 `PrtgProbeRunner.cs:73` 讀過（格式化字串）。
- `NetiqPipelineService.cs:364-380` 每日依 `IpBatchSize=50` 分批、`Parallel.ForEachAsync`；`PrtgFetchService.FetchValuesAsync` 以 semaphore 控併發；本機逐日迴圈 `AnalysisOrchestrator.cs:893-922`。
- `SchedulerHostedService` 只有取消語意；`SchedulerRunState` 無暫停狀態。
- Sentinel 位址在 `Sentinel.BaseUrl`；PRTG 位址在 `PrtgUrl`；sensor 分類 `lf_prtg_sensors.category`（cpu／memory／disk／traffic）。

### 定案
- **設定**（`SystemSettings`，全部有消費端＝守門本身）：

  | 設定 | 預設（暫定） | 說明 |
  |---|---|---|
  | `PrtgResourceGuardEnabled` | false | 總開關 |
  | `PrtgResourceGuardSensorObjids` | 空 | 覆寫清單，一行一個 objid；**空＝自動偵測** |
  | `PrtgResourceGuardCpuPercent` | 85 | CPU 使用率 ≥ 此值算緊張（1~100） |
  | `PrtgResourceGuardMemoryFreePercent` | 10 | 可用記憶體 ≤ 此值算緊張（0~99） |
  | `PrtgResourceGuardCheckSeconds` | 60 | 檢查間隔（15~600） |
  | `PrtgResourceGuardPauseMinutes` | 5 | 緊張後等待再檢查（1~60） |
  | `PrtgResourceGuardStrikes` | 2 | 連續幾次超標才算緊張（1~10） |
  | `PrtgResourceGuardMaxPauseMinutes` | 120 | **單趟累計暫停上限**（10~600）。門檻設錯時整晚只會暫停、什麼都沒分析、每晚重演；超過上限後放行並寫警告 Milestone，之後這趟不再暫停 |

  守門只要求 PRTG 位址與認證（同環境探測），**不要求 `PrtgEnabled`**；但自動偵測要讀鏡像表，鏡像為空（從未同步）時視為沒找到 sensor、放行並警告。放在 PRTG 維護頁「擷取參數」頁籤的獨立卡「資源守門」，同一顆儲存鈕、同一個 `PUT prtg` 端點（欄位可空）。卡內附「預覽受監看 sensor」按鈕：呼叫新端點 `GET api/admin/settings/prtg-resource-guard/preview` 回自動偵測（或覆寫清單）的 sensor 清單與**當下讀到的值**，讓使用者在啟用前確認偵測結果與數值語意。
- **自動偵測**（空清單時，每趟執行開始時算一次）：
  1. NetIQ 主機＝每台 `Sentinel.BaseUrl` 的 host 部分；PRTG 主機＝`PrtgUrl` 的 host 部分；
  2. 在 `lf_prtg_devices` 找 `Ip` 與上述 host **不分大小寫相等**的 device（IP 或 DNS 名稱皆可）；**不相等時做一次 DNS 解析**（host→IP 清單，解析失敗忽略），拿解析結果再比一次——Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，不解析會全數落空；PRTG 主機另有 fallback：任一 sensor type 為 `corehealth` 的 device（PRTG 的 Core Health sensor 只掛在 core server 自己身上）。找不到的主機記一行警告；
  3. 取這些 device 底下 `category` 為 `cpu` 或 `memory`、未暫停的 sensor；PRTG 主機另加 `corehealth` sensor（其主通道為 Health 百分比，**低於門檻**算緊張，門檻沿用可用記憶體門檻，暫定）。
  一個都沒找到時守門**視為不緊張並記警告**（守門不能反過來把排程卡死）。
- **即時值讀取**：`table.json?content=sensors&columns=objid,name,device,type,status,lastvalue,lastvalue_raw&filter_objid=…`（一次帶全部 objid，暫定，實機驗證形狀），走既有 `PrtgClient`。判定用 `lastvalue_raw`；`lastvalue` 字串不含 `%` 的 sensor 視為無法判定、忽略並每趟只警告一次。CPU 類：值 ≥ CPU 門檻；memory 類：值 ≤ 可用門檻（PRTG 記憶體 sensor 主通道多為「可用百分比」，**預覽端點會顯示通道名稱與值供人工確認**）。sensor 狀態非 Up 時忽略該 sensor。
- **閘門**：每趟執行建立一個 `ResourceGuard`（Core），提供 `WaitIfBusyAsync(ct)`。內部：距上次檢查未滿 `CheckSeconds` 直接放行；否則讀值，連續 `Strikes` 次任一 sensor 超標即進入暫停，暫停期間所有呼叫端一起等 `PauseMinutes` 後重檢，直到不緊張才放行。取消訊號穿透暫停。守門未啟用時 `WaitIfBusyAsync` 為 no-op。**插入點**：NetIQ 每批查詢之前、PRTG 每個 sensor `historicdata` 之前（每日取數與觸發式取數共用同一個 `FetchValuesForSensorsAsync`，一處即涵蓋）。**本機分析不插閘門**——它只讀本機事件與資料庫，不碰 NetIQ 與 PRTG，暫停它沒有意義。PRTG 連不上或讀取例外 → 放行並每趟只警告一次。守門實例由 `AnalysisOrchestrator` 每趟建立、經既有的 context 物件傳入 pipeline 與 fetch service（可選參數，歷史回填與探測不傳＝不受守門）。
- **可觀測性**：進入／離開暫停各寫一則 Milestone（含觸發 sensor、值、第幾次檢查）；`SchedulerRunState` 新增 `PausedReason`（null＝未暫停），status API 帶出，狀態卡在標題下顯示「資源緊張，暫停中：{sensor} {值}（第 k 次檢查，{時間}後重檢）」。窗口 End 到點時既有優雅停止照舊（暫停中也會被取消），**不延長窗口**。
- 歷史回填與環境探測**不受守門**（離峰手動作業）。

### 驗收
- 單元測試（用假 client）：未啟用 no-op；連續 2 次超標才暫停、1 次不暫停；暫停後值回落即放行；讀取例外放行且只警告一次；取消訊號在暫停中拋出；累計暫停超過上限後放行且之後不再暫停；自動偵測依 host 大小寫不敏感命中、`corehealth` fallback 命中、無命中回空且警告；`lastvalue` 不含 `%` 的 sensor 被忽略。
- `dotnet test` 全綠；設定八鍵每個在 Core 有消費端（grep 每鍵 ≥ 2 處非設定檔命中）。
- 實機：預覽端點回得出 NetIQ 與 PRTG 主機的 CPU／記憶體 sensor 與現值（這一步是 `lastvalue_raw` 語意的實測門檻，不通過就先調整判定再啟用）。

---

## 文件同步清單（收尾時）

- PRTG-SPEC §4 對應規則（排除、同 IP 人工指定後的略過）、§4a、§7 設定表（守門八鍵）、§8 端點、「操作介面」表（四頁籤、校準與搬運搬家）、新增 §12 資源守門。
- WEB-SPEC §9.9e（四頁籤、hash）、§9.9f 改為「併入 PRTG 維護頁環境探測頁籤」並保留 API、§9.10（狀態卡完工軌、執行紀錄分欄、每日列 PRTG 格、暫停顯示、AI 排程未啟用提示）、§11（NLog 作用域規則）。
- DB-SPEC：`lf_prtg_ip_excludes`。
- DETECTION-SPEC「兩個獨立排程」：本機路徑也走 AI 排程。
- BACKLOG：`LatestOccurrences` 記憶體過濾下推；守門的「上次同步成功時間」仍未做。
- CLAUDE.md 測試基線更新。

## 複檢（規劃完成後）

- **與既有設計的衝突**：批次C 推翻「done 訊號清空欄位」設計（WEB-SPEC §9.10 明寫），改為保留＋Completed，理由是使用者要看得到完工痕跡；`LatestActivity()` 跳過已完成軌讓原本清空的目的（告示落回還在推進的路）仍成立。批次E 推翻「本機同步 AI」，DETECTION-SPEC 同步。批次B 在 PRTG-SPEC §4 表新增兩列「略過」情況，與「不新增第四個 map_status」相容（略過不產生列）。
- **批次間**：A 與 B 同動 `prtg-admin.js`（B 依賴 A 的頁籤與 modal 結構）；C 與 D 同動 `SchedulerRunState`／`runs.js`（D 的 status 欄位在 C 之後加）；F 的設定卡放在 A 建立的「擷取參數」頁籤、`PausedReason` 顯示在 C 改過的狀態卡。
- **四個坑**：分母為零→守門「一個 sensor 都沒找到」與「全部無法判定」皆放行並警告；略過計數各項為 0 也要顯示。破壞性判準→IP 排除只影響對應與取數，不刪既有 `lf_prtg_host_map`／`lf_prtg_values` 歷史列。單向閘門→NLog 作用域的繞過路徑已寫明（無 scope 事件丟棄，完整 log 仍在檔案）。移除類→`/admin/calibration` 頁面的依賴方只有選單與頁面路由；`Unmatched` DTO 欄位的消費端只有本頁；本機同步 AI 的依賴測試在批次E 白名單內處理。
- 升級路徑：`BatchRun` 新欄位可空、舊 JSON-line 照讀；新表走 `SchemaUpgrader` 冪等 DDL；守門預設關閉、AI 排程行為不因本輪改變（僅本機改走它）。
- **第二輪複檢（四角度）補入**：A 測試連線需帶參數頁籤的逾時／忽略 SSL、舊校準路由轉址；B 操作後立即重算今日對應（否則清單隔天才變）、主機明細顯示已排除；D 的 AsyncLocal 推入位置限制與「scope 外」測試不能用 `Task.Run`；E 補可行性核對（本機已寫風險 log 暫存、`HostId=0` 邊界、體檢不在範圍）並更正白名單（`LogAnalysisServiceRetryAiTests` 不受影響）；F 移除本機插入點、加 DNS 解析與 `corehealth` fallback、加單趟累計暫停上限、守門不要求 `PrtgEnabled`。
- 複檢完成，新增事項已併入上文，無另開待決。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| | | | | |
