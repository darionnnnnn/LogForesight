# 回饋第 37 輪規劃：校準數值匯出、PRTG 設定收斂與進度顯示、技術債清理

> 狀態：實作完成，待換模型體檢
> 基準：dev@0afbc7e（3296 綠、略過 6）
> 來源：BACKLOG 盤點（第四類「可排一輪清掉」）＋新需求（校準數值匯出頁、PRTG 進度顯示、PRTG 設定集中）
> 委派模型：agy `gemini-3.7-flash-high`，整輪不換；幾行內小修標「Claude」。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 執行者 |
|---|---|---|---|---|
| D | 小清理：`GetDedupeKeys` 死碼、`ISystemSettingsService` 預設實作、偶發測試改閘門、WEB-SPEC §9.9b 編號 | 小 | 無 | Claude |
| B | `diag/` 傾印上界與清理 | 小 | 無 | agy 1 段 |
| C | PRTG 收斂：`Max(map_date)` 單一實作、規則對照表單一來源＋守門測試、`Magnitude` 定位 | 中 | 無 | agy 2 段 |
| E | 4740 帳號走結構化欄位（修 `Take(5)` 漏判） | 中 | 無 | agy 1 段 |
| F | PRTG 設定集中到維護頁：專屬更新端點、設定頁移除 PRTG 頁籤、回填搬家 | 中 | 無 | agy 3 段 |
| G | PRTG 進度顯示：夜間擷取第三條進度軌、回填天數／sensor 進度 | 中 | F3（回填 UI 位置） | agy 3 段 |
| A | 校準數值匯出頁 `/admin/calibration` | 大 | C2（`Magnitude` 當校準分佈來源） | agy 5 段 |

建議順序：D → B → C → E → F → G → A。D 先做讓後續 diff 乾淨；A 最後做，因為它消費 C 的 `Magnitude` 與 F 之後的頁面配置。

---

## 批次D：小清理（Claude 自做）

### 現況與核對結果
- `PermissionChangeStore.GetDedupeKeys`（`LogForesight.Core/Persistence/PermissionChangeStore.cs:442`）生產呼叫端為零；沒有介面，靠 `virtual`。用它的測試：`EfPermissionChangeStoreTests.cs:282-316`、`NetiqPermissionChangePostProcessorTests.cs:19`、`NetiqPipelineServiceLookbackTests.cs:172`。`LfDbContext.cs:371` 註解引用它說明索引取捨。
- `ISystemSettingsService`（`LogForesight.Web/Services/SystemSettingsService.cs:58-61`）只剩 `TestPrtgAsync` 一個預設實作；替身 `FakeSystemSettingsService`（`Tests/TestDoubles/VisibilityFakes.cs:41`）少這個方法。
- `PrtgFetchServiceTests.FetchDayAsync_併發設定值真的被採用而非寫死`（`:504-536`）斷言峰值恰等於 3 是對的；不穩來自 `Task.Delay(150)` 時間相依。
- WEB-SPEC §9.9b 的編號從 5 跳到 8，缺 6、7（功能無缺，純文件）。

### 定案
- 刪 `GetDedupeKeys` 與專屬測試；`NetiqPermissionChangePostProcessorTests` 改用 `GetDedupeKeysForHost`；`NetiqPipelineServiceLookbackTests` 拿掉 override；`LfDbContext` 註解改為引用 `GetDedupeKeysForHost` 走 `(host_name_key, detected_at)` 的理由。
- `TestPrtgAsync` 改純宣告；替身補一行明確擲 `NotSupportedException`。
- 偶發測試改 `TaskCompletionSource` 閘門：前 3 個請求進來後才一起放行，斷言不變（仍為恰等於 3）。
- WEB-SPEC §9.9b 編號重排為連續（批次 F 改寫該節時一併處理，不在此重複改）。

### 驗收
- `dotnet test` 全綠；`GetDedupeKeys(` 全 repo（含 tests）零命中；`NotSupportedException` 在 `ISystemSettingsService` 介面內零命中。
- 偶發測試單獨連跑 20 次全綠。

---

## 批次B：`diag/` 傾印上界

### 現況與核對結果
- `FilePromptDumper`（`LogForesight.Core/Persistence/IPromptDumper.cs:23-41`）只有建目錄＋寫檔，無任何刪除或上限。每次 AI 呼叫一檔（含 JSON 重試的每次嘗試）。
- 開關：`ScheduleOptions.DebugDump`，UI 文案「AI 診斷傾印」（`Runs.cshtml:49`）。
- 專案沒有獨立 retention service；所有保留期清理集中在 `AnalysisOrchestrator` 夜間批次的同一段（`:441-551`），形狀一致：`try { Prune } catch { Log.Warn }`。

### 定案
- **不新增設定**。天數沿用既有 `RunLogRetentionDays`（同屬診斷類資料）；清理段掛在 PRTG 鏡像清理（`:551`）之後，形狀比照既有段。
- 另加**單次執行硬上限**：同一個 `FilePromptDumper` 實例寫滿 N 檔（暫定 2000）後停止寫入並寫一行警告到 console（只警告一次），避免忘關時單晚就塞爆。上限為常數，不開設定。
- 清理邏輯放在 `FilePromptDumper` 的靜態方法（依檔案最後寫入時間刪除超過天數者），只刪 `diag/` 內的 `.txt`，目錄不存在時零成本返回。

### 驗收
- 新增測試 ≥ 3：超過天數的檔被刪、未超過的保留、目錄不存在不擲例外；硬上限測試：寫入第 N+1 次不產生檔案且 console 只出現一次警告。
- `dotnet test` 全綠且測試總數比基準多。

---

## 批次C：PRTG 收斂

### 現況與核對結果
- 「最近一次有對應的日期」逐日回溯有兩份：`EfPrtgStore.GetLatestHostMap`（`:514-527`）與 `SettingsController.GetPrtgMirrorStatus`（`:175-186`，後者要多回傳命中日期，所以當初重抄）。呼叫端：`DashboardController.cs:132`、`HostAdminService.cs:192`。`lf_prtg_host_map` PK 是 `(map_date, device_objid)`，`Max(map_date)` 直接吃 PK，不需新索引。
- 四條規則的分類／嚴重度／`ElevatesDayRisk`／預設門檻寫了三份：`PrtgFindingMapper.cs:18-45`、`KnownIssueSeed.cs:1082-1113`、`PrtgRuleThresholds` 預設值（`PrtgRuleEvaluator.cs:7`）。只有 RuleCode 共用常數。
- `PrtgFinding.Magnitude`（`PrtgRuleEvaluator.cs:18`）無生產消費端，12 個測試靠它驗門檻邏輯。

### 定案
- **C1**：`EfPrtgStore` 新增單一方法回傳 `(MapDate, Rows)`（兩次查詢：先 `Max(map_date)` 於回看窗內，再取該日列），`GetLatestHostMap` 改成薄包裝，`SettingsController` 的迴圈刪除改呼叫新方法。回看窗仍為 30 天。全無資料時 `MapDate` 為 null、Rows 為空。
- **C2**：新增 PRTG 規則對照表（單一靜態類別），內容為 RuleCode → 分類、嚴重度、`ElevatesDayRisk`、一句話 `KnownIssue`、預設門檻。`PrtgFindingMapper` 的 switch 與 `KnownIssueSeed` 四筆 PRTG 規則的這幾個欄位、`PrtgRuleThresholds` 的預設值都從對照表取。Seed 的長文（`PlainExplanation`／`Impact`／`LikelyCauses`／`NextSteps`）留在 Seed，本來就只有一份。**不遞增 seed 版本**（值沒變）。補守門測試：對每個 RuleCode，Mapper 產出與 Seed 規則的分類／嚴重度／`ElevatesDayRisk` 一致。
- **`Magnitude` 定位**：保留，並在 record 加註解「供規則測試、Detail 文案與校準分佈（批次A）使用，刻意不落 `Count`」。**不**用它填 `Count`：整日 Down 會變 1440 次，汙染問題排行權重。

### 驗收
- C1：`GetHostMapForDate` 在 `SettingsController` 零命中；新增測試「資料稀疏（只有 20 天前一筆）仍取到該日」與「無資料回 null」。
- C2：`IssueCategory.`／`IssueSeverity.` 字面值在 `PrtgFindingMapper.cs` 零命中（全部改讀對照表）；守門測試存在且對四條規則逐一斷言；`dotnet test` 全綠。

---

## 批次E：4740 帳號走結構化欄位

### 現況與核對結果
- 剖析端 `ResidualCredentialDetector.ExtractAccountsFromKeyDetails`（`:265-299`）靠中文標籤「相關帳號」、全半形冒號／分號、剝掉省略號還原。
- 產生端 `LogAggregator.ExtractSecurityDetails`（`:297-311`）`Take(5)`：帳號超過 5 個，第 6 個起永遠不在 `KeyDetails`，交叉比對**靜默漏判**。這是正確性 bug，不只是可維護性。
- 登入失敗明細已完全結構化（`LoginFailureDetail`，封頂 50 組＋總量＋截斷旗標）；但 4740 不是登入失敗，走那條管線會進登入失敗明細 UI 與 AI prompt，且無 LogonType 會汙染集中度分母。
- 三個 4740 測試（`ResidualCredentialDetectorTests.cs:733-830`）以 `KeyDetails` 字串建構輸入。

### 定案（A 案）
- `LogIssueSignature` 新增未截斷的帳號清單欄位（名稱由執行端定，暫定 `KeyAccounts`，`List<string>?`），由 `LogAggregator` 在產生 `KeyDetails` 的同一處填入**完整**帳號集合（去重、保序）；封頂暫定 200 個並帶截斷旗標（比照登入失敗明細的作法）。**`KeyDetails` 顯示格式不變**。
- `ResidualCredentialDetector` 優先讀新欄位；新欄位為 null（舊 ContentJson）時 fallback 到既有字串剖析，fallback 保留到舊資料自然淘汰（`RawEventRetentionDays` 預設 120）。
- 新欄位隨既有 ContentJson 序列化（`RecordStorageShaper` 已處理的簽章欄位路徑），不加抽出欄、不動 schema。

### 驗收
- 新增測試：4740 有 7 個帳號、殘留帳號是第 6 個 → 命中（舊實作在此案例必失敗，作為突變證據）；新欄位為 null 時 fallback 仍命中既有三個測試案例。
- `Take(5)` 仍存在於 `KeyDetails` 顯示（顯示格式不變），但 `ResidualCredentialDetector` 內 `ExtractAccountsFromKeyDetails` 僅在 fallback 路徑被呼叫（測試以 null 欄位覆蓋）。

---

## 批次F：PRTG 設定集中到維護頁

### 現況與核對結果
- 設定頁 PRTG 頁籤（`Settings.cshtml:729-782`）剩 6 個純參數：忽略 SSL、逾時、併發、回填天數、保留天數、sensor type 白名單。前兩者與 PRTG 維護頁連線設定**重複可編**。
- PRTG 維護頁存檔（`prtg-admin.js:162-210`）是 `GET /api/admin/settings` 整包 spread 後 `PUT` 整包：`UpdateSystemSettingsRequest` 中 PRTG 六個參數為**非可空無條件寫入**（`SystemSettingsService.cs:450-455`），維護頁存檔會把 GET 與 PUT 之間別人在設定頁改的值回退。
- `PrtgEnabled` 已有單一用途端點（`PUT prtg-enabled`），總開關在 `/runs` 切換即存。
- 歷史回填**執行**在 `/runs`（`Runs.cshtml:152-165`），但天數在設定頁；`/runs` 沒顯示「將回填 N 天」。
- `PrtgRetentionDays ≤ RetentionDays` 跨欄位檢查在 `SystemSettingsService.Update:171`。
- WEB-SPEC §9.9b 第 8 節仍描述四個區塊（連線／鏡像／回填／探測），已與程式碼不符；PRTG-SPEC §7 操作介面表是對的。

### 定案
- **F1 後端**：新增 PRTG 專屬更新端點 `PUT api/admin/settings/prtg`，請求 DTO 含連線設定（URL、認證模式、帳號、三種秘密採「留空沿用＋`Clear*` 清除」既有慣例）與六個參數。驗證邏輯**與整包 `Update` 共用同一份**（抽成可共用的驗證方法，不複製）：認證依模式驗證、`PrtgRetentionDays ≤ RetentionDays`、Range。只寫 PRTG 欄位，稽核 before/after 只含 PRTG 欄位。`UpdateSystemSettingsRequest` 的六個 PRTG 參數改為可空「有送才更新」（比照既有四個可空欄位），整包 `Update` 中 `RetentionDays` 縮小時仍要檢查不得小於現有 `PrtgRetentionDays`。
- **F2 前端**：PRTG 維護頁連線設定頁籤擴為「連線與擷取參數」，加入六個欄位（同一顆儲存鈕），存檔改走新端點、**不再 GET 整包**；設定頁移除 PRTG 頁籤與 `settings.js` 對應讀寫驗證（頁籤變 7 個）；設定頁「資料保留」頁籤**不**接回 PRTG 保留天數（維持「PRTG 的東西都在 PRTG 頁」）。
- **F3 排程頁回填區文案與天數**（定案：動態工作留排程頁、靜態設定放 PRTG 維護頁）：歷史回填的操作與狀態**留在** `/runs`，`PrtgEnabled` 總開關也留。回填區說明改為指向 PRTG 維護頁，並在「開始回填」旁動態顯示「將回填 N 天」（N 取自 `loadSchedule()` 已抓的整包設定，不另打 API）。端點路由不變。
- 文件：WEB-SPEC §9.9b 第 8 節刪除（頁籤變 7 個、編號連續化，含批次D 的編號修正）、API 清單搬到 PRTG 頁章節；PRTG-SPEC §7 操作介面表改為兩列（維護頁／排程頁）；§8 端點表加新端點。

### 驗收
- 前端 `prtg-admin.js` 中 `api.get('/api/admin/settings')` 零命中；`Settings.cshtml` 中 `data-panel="prtg"` 零命中；`settings.js` 中 `prtg` 字串零命中。
- 新增測試：新端點只改 PRTG 欄位（改前後其他欄位逐一相等）、認證模式驗證錯誤被拒、`PrtgRetentionDays > RetentionDays` 被拒；整包 `Update` 不送 PRTG 參數時六個值不變。
- 頁面雙閘不變：新頁籤內容仍在 `Maintain` 下。

---

## 批次G：PRTG 進度顯示

### 現況與核對結果
- 全站唯一的量化進度是「主機日」進度條：`IRunProgress.Report(phase, done, total)` → `SchedulerRunState`（本機／NetIQ 兩組互不覆蓋欄位）→ `GET api/admin/schedule/status` → `runs.js` 的 `updateProgressBar`（標籤與單位已參數化，`PROGRESS_PHASE_UNIT` 預留為空物件）。
- `RunPrtgFetchAsync`（`AnalysisOrchestrator.cs:1051-1301`）解構出 `progress` 但一次都沒呼叫；`PrtgFetchService.FetchDayAsync` 無 progress 參數；逐 sensor 迴圈在私有 `FetchValuesAsync`（`:340-405`）已有 `activeSensors.Count` 分母與 `Interlocked` 計數。
- `SchedulerRunState.ReportProgress` 的 `else` 是 catch-all 寫進 NetIQ 主軌——加 PRTG phase 必須顯式分支，否則會蓋掉 NetIQ 進度條。
- 回填 `PrtgBackfillRunState` 是空類別繼承 `PrtgProbeRunState`，只有 running／訊息／輸出；`PrtgBackfillRunner` 迴圈裡 `i/days/successDays/failedDays` 現成，只差 callback。
- 環境探測沒有自然分母，維持現狀。

### 定案
- **G1 Core**：`PrtgFetchService` 新增進度回呼（形狀由執行端定，暫定 `Action<string stage, int done, int total>?`，**不可為可選建構子相依**，以方法參數傳入）；階段：結構同步（total=0 表示「準備中」）、數值取數（done/total = 已完成 sensor／目標 sensor）。觸發式取數 `FetchValuesForSensorsAsync` 走同一個回呼。`RunPrtgFetchAsync` 把 `progress` 接上，phase 字串以 `prtg` 為前綴（暫定 `prtg-sync`／`prtg-values`／`prtg-triggered`／`prtg-done`）。
- **G2 Web**：`SchedulerRunState` 加第三組 PRTG 進度欄位（顯式分支）、`prtg-done` 清空；`ScheduleStatusDto` 加對應欄位；`runs.js` 加第三條進度條，單位「sensor」，標籤依 phase 對照（結構同步中／數值取數中／觸發式取數中）。`LatestActivity()` 優先序：NetIQ > 本機 > PRTG（PRTG 是附屬流程）。
- **G3 回填**：`PrtgBackfillRunState` 自己持有 `DoneDays／TotalDays／CurrentDay／SensorDone／SensorTotal`（不動 `PrtgProbeRunState`），`PrtgBackfillRunner.RunAsync` 加回呼參數，`PrtgBackfillStatusDto` 加欄位，`/runs` 的回填區塊改為「第 X／N 天（yyyy-MM-dd）：sensor 已完成 a／b」進度條＋既有輸出 textarea（位置不變，定案見 F3）。

### 驗收
- 測試：`SchedulerRunState` 收到 `prtg-*` phase 時 NetIQ 欄位不變（反例測試）；`PrtgFetchService` 以 6 個 sensor 跑數值取數，回呼最後一次為 (6, 6) 且遞增單調；回填 runner 3 天跑完回呼收到 (3, 3)。
- 前端：三條進度條 DOM id 各自獨立；`PROGRESS_PHASE_LABEL` 含新 phase。
- 文件：WEB-SPEC `/runs` 章節補第三條進度軌；PRTG-SPEC §3a、§5 補進度語意。

---

## 批次A：校準數值匯出頁 `/admin/calibration`

### 現況與核對結果
- 值型基線：`EfPrtgStore` 沒有任何 count／min-max／per-sensor 覆蓋查詢，只有 `Max(period_start)`；`GetValues` 整段全載不可用於算量；`lf_prtg_values` 有 `UNIQUE(sensor_objid, period_start)`；`quality` 為 `unknown`／`nodata` 的列數值為 null，必須排除。主機層要經 `lf_prtg_host_map`（最近對應日）device→host 反查，sensor→device 由 `lf_prtg_sensors`。
- 規則門檻：`lf_top_issues` 對 PRTG finding 的 EventKey 為 `prtg:{規則}:{objid}`；**表上沒有 rule_id 欄**（`TopIssueRow` 只有 EventKey），分規則只能靠 EventKey 前綴。既有聚合以 `(Source, EventId)` 為鍵、EventId 恆 0，四條規則塌成一格。分佈（持續分鐘、往返次數）只有 `PrtgRuleEvaluator` 算得出來（`Magnitude`）。已核對評估器語意：`down` 本來就只納入**日終仍 Down** 的 sensor（`:62-63`），中途恢復者不在此規則；flap 與 warning 門檻設 0 會讓所有有變更的 sensor 都命中（0 ≥ 0），因此校準用的是**最低門檻**（Down 1 分鐘、flap 1 次、Warning 1 分鐘），得到的是「有事件的 sensor-日」全量分佈。
- 觸發式量級：每晚計數只在 console／milestone 字串；可由 `lf_prtg_values` 按 `period_start` 日期 group by 推導（distinct sensor 數、列數、各 quality 筆數）。
- 殘留判定：五個常數（`ResidualCredentialDetector.cs:14-18`）；輸入（`LoginFailureDetails`／總量／截斷）與結果只在 `content_json`，集中度等中間值**從未被存**；跨主機批次讀只能 `IAnalysisRecordQuery.Query`／`GetOne` 全反序列化；詳情精簡（`detail_pruned`）後明細不存在。哪些簽章會有明細已核對（`LogAggregator.cs:393-406`）：Windows 為 EventId∈{4625,4771}，Linux 為 `LogName = Linux` 且 EventKey ∈ `LinuxAuthParser.LoginFailureRuleIds`——候選主機日的篩選條件就用這兩條，不需執行端另外推導。
- NetIQ 10 項未實證細節屬人工探測，不是累積數據，不納入本頁。
- 頁面慣例：操作類頁面比照 `/admin/prtg`（`PagesController` 路由＋`[Permission(Maintain)]`＋`layout.js` 選單 `requires`）。下載走 fetch＋blob 才能顯示後端錯誤（`prtg-export` 的 `location.assign` 出錯會露整頁 JSON）。

### 定案
- 獨立頁 `/admin/calibration`，選單「校準數值匯出」歸「系統管理」群組，`Maintain`。頁面**沒有可存的設定欄位**。
- 四張卡（校準項），每張顯示：**目前累積量**（幾個關鍵數字）、**判定狀態**（不足／可用／充足／無法取得）、**怎麼補充**（依實際狀態生成的條列說明）。狀態計算是重查詢：按「重新計算」才跑，結果在記憶體快取（暫定 10 分鐘），頁面載入時顯示上次結果或「尚未計算」。
- 各項定義（數值皆**暫定**，執行端可依實作事實調整並回報）：

| 項 | 什麼算一個 | 累積量指標 | 可用 | 充足 | 無法取得 |
|---|---|---|---|---|---|
| PRTG 值型基線 | 一個「有對應主機的白名單 sensor」；一天算涵蓋＝該日 `quality=ok` 小時 ≥ 12 | 已對應主機數、涵蓋 ≥28 天的主機數、最早／最晚有效資料日、未對應 sensor 數 | ≥10 台主機各涵蓋 ≥28 天 | ≥10 台各 ≥56 天 | PRTG 未啟用或鏡像無 sensor |
| PRTG 規則門檻 | 一個 sensor-日（`down`：日終仍 Down；`flapping`：當日 ≥1 次往返；`warning`：當日累計 ≥1 分鐘；`silent`：一個 device-日） | 狀態變更涵蓋天數（distinct 日）、期間 Down／Warning／flapping 的 sensor-日數、silent device-日數 | 涵蓋 ≥28 天且 Down sensor-日 ≥30 | 涵蓋 ≥56 天且 ≥100 | 同上 |
| 觸發式取數量級 | 一晚 | 近 30 晚有資料的晚數、每晚 distinct sensor 數與列數的 min／中位／max、各 quality 佔比 | ≥14 晚 | ≥28 晚 | 同上 |
| 殘留判定門檻 | 一個含登入失敗明細（非空）且未精簡的主機日 | 主機日數、涵蓋天數、其中截斷比例、命中數 | ≥200 主機日且涵蓋 ≥14 天 | ≥1000 且 ≥28 天 | 詳情保留期內無任何候選 |

  分母為零（例如沒有任何 Down 事件）一律判「不足」，補充說明寫明「期間無事件，門檻無從校準，維持預設」；不得判為可用。
- 補充說明的生成規則（每項依狀態挑選適用者）：PRTG 未啟用→到 PRTG 維護頁啟用並完成連線；白名單命中 0→檢查 sensor type 白名單；`PrtgRetentionDays` 小於充足所需天數→調高；已對應主機不足→對特定主機跑歷史回填（指向回填入口）；涵蓋天數不足→顯示「預估還需 N 天」（充足所需 − 目前）；殘留候選不足→確認 4625／4771 與 Linux 登入失敗規則啟用、確認分析排程正常跑、詳情保留期是否太短。
- 匯出：單一 JSON 文字檔 `calibration-{yyyyMMdd}.json`（UTF-8 無 BOM），自描述（`formatVersion`、`exportedAt`、各項的判定摘要＋門檻現值＋資料集）。fetch＋blob 下載，錯誤以 toast 顯示。寫稽核（新 action 常數）。**閘門**：四項全部 ≥「可用」才啟用匯出鈕；否則按鈕停用並提供「仍要匯出」勾選覆寫（覆寫也寫進稽核 detail）。
  - 值型基線資料集：per-sensor **每日聚合**（sensor objid、device objid、host、sensor type、日期、avg／min／max、ok 小時數、unknown／nodata 筆數），區間＝最近 56 天。需要原始 hourly 時用既有 `prtg-export`。
  - 規則門檻資料集：以最低門檻（Down 1／flap 1／Warning 1、四條全啟用）逐日呼叫既有 `Evaluate` 得到的每 sensor-日 finding（規則、objid、`Magnitude`、日期）＋各規則分位數摘要（P50／P90／P99）＋ `lf_top_issues` 近 56 天每規則每日命中數（新增 EventKey 前綴 `prtg:{規則}:` 分組查詢；`Source = PRTG` 先過濾）＋目前門檻現值（從規則庫 prtg 平台讀）。逐日評估需要「前一日最後一筆」，執行端沿用夜間批次組裝 `Evaluate` 輸入的同一段方式，不另寫一份。
  - 觸發式量級資料集：近 30 晚每晚的 distinct sensor 數、列數、各 quality 筆數。
  - 殘留判定資料集：每候選主機日的候選組數、前二組集中度、機械型態佔比、單組集中度、跨日數、是否截斷、是否命中、事件類型（4625／4771／Linux），主機以 id 與名稱表示，**不含帳號名稱**。上限暫定最近 5000 主機日。指標計算須把 `ResidualCredentialDetector` 的中間值計算抽成可獨立呼叫的內部方法，判定邏輯與結果**不得改變**（既有測試全綠為證）。
- 資料存取：`EfPrtgStore` 新增聚合查詢（per-sensor 每日聚合、狀態變更涵蓋、每晚量級），一律 SQL 端聚合，**不得沿用 `GetValues` 全載**；殘留候選走 `lf_top_issues` 篩「EventId∈{4625,4771}」或「LogName = Linux 且 EventKey ∈ `LinuxAuthParser.LoginFailureRuleIds`」且紀錄未精簡，取 distinct 主機日（最近優先、受上限保護），再逐筆 `GetOne` 反序列化算指標。
- 拆段（agy）：A1 `EfPrtgStore` 聚合查詢＋測試；A2 規則命中分組查詢＋殘留指標抽出＋測試；A3 校準服務（判定、補充說明、匯出組裝）＋測試；A4 API（狀態／重新計算／匯出）＋稽核＋授權測試；A5 頁面（cshtml／js／選單）。

### 驗收
- A1：以 3 個 sensor × 3 天 × 混合 quality 的資料驗每日聚合排除 unknown／nodata、ok 小時數正確；SQLite 與既有雙後端測試慣例一致。
- A2：EventKey 前綴分組對 `prtg:down:1`、`prtg:down:2`、`prtg:flapping:1` 回 down=2、flapping=1，且非 PRTG 來源的 EventKey 不計入；殘留指標方法對既有三個典型案例回傳的集中度與判定結果一致；最低門檻評估對「當日一次往返」與「Warning 累計 30 分鐘」各產生一筆 finding 並帶正確 `Magnitude`。
- A3：每項四種狀態各至少一個測試；分母為零判不足；補充說明對「PRTG 未啟用」「涵蓋不足」各有斷言。
- A4：非 `Maintain` 角色 403；匯出寫稽核；覆寫匯出的 detail 含旗標。
- A5：選單出現、雙閘生效；閘門未達時按鈕 `disabled`。
- 全輪：`dotnet test` 全綠，測試總數比基準多，數字記入體檢交接。

---

## 明確不做（本輪定案）
- NetIQ 10 項未實證細節：人工探測，不進校準頁。
- `ReportFileMigrator` 與同批 5 個一次性 hosted service 退場：等正式機實測報告機制後的輪次一起盤點。
- `Magnitude` 填入 `Count`：會讓整日 Down 變 1440 次汙染排行。
- 4740 走登入失敗明細管線（B 案）：語意錯置、汙染集中度分母。
- 校準頁不做值型規則本身（L2～L5）：那是拿到匯出檔之後的下一輪。
- 匯出不壓縮、不增量；hourly 原始值不進校準檔（用既有 `prtg-export`）。
- 環境探測不加進度條（無自然分母）。
- 校準門檻不開設定（尚未校準的門檻再開設定是套娃）。

## 已定案的待決點
1. 回填與總開關留在排程作業頁（動態工作），六個參數搬到 PRTG 維護頁（靜態設定）。F3 改為文案與天數顯示。
2. 校準各項門檻數值採上表暫定值。

## 規劃完成後複檢（第二次，依定案與實際核對更新）
- 與既有設計衝突：F1 把六個 PRTG 參數改可空，與 PRTG-SPEC §7 blockquote「只出現在單一頁面的設定欄位一律可空、有送才更新」一致；整包 `Update` 的跨欄位檢查現為 `request.PrtgRetentionDays > request.RetentionDays`（`SystemSettingsService.cs:171`），改可空後必須用 effective 值比較，已寫入 F1。PRTG 認證驗證區（`:174-213`）以 effective 值運作，抽成共用方法後整包 `Update` 與新端點各呼叫一次。G2 顯式分支避免蓋掉 NetIQ 軌已寫入（`ReportProgress` 的 `else` 是 catch-all，`:161`）。A 的殘留指標抽出要求「判定不變」以既有測試為證。
- 批次間：C2 對照表與 A2 最低門檻評估都動 `PrtgRuleEvaluator`——C 先做；F2 與 G3 分別改 `prtg-admin.js`／`Prtg.cshtml` 與 `runs.js`／`Runs.cshtml`，檔案不重疊；F3 與 G3 都改 `/runs` 回填區——F3 只改文案與天數，G3 加進度條，依序做；D 的 WEB-SPEC 編號與 F 文件合併處理。
- 四個坑：分母為零已明寫；無破壞性判準（本輪無刪列洗欄）；閘門「仍要匯出」有覆寫路徑；移除類（`GetDedupeKeys`、設定頁 PRTG 頁籤、控制器迴圈、`prtg-admin.js` 的整包 GET）依賴方已列全。
- 修正的技術事實：`lf_top_issues` 無 `rule_id`；評估器門檻 0 的語意；Linux 登入失敗規則清單來源。
- 複檢完成，新增事項：以上三項技術事實已改寫進批次A。

## 併回前終檢

### 同型普查發現（規劃遺漏，終檢時補做）

**批次E 只修了帳號那一半**：`ExtractSecurityDetails` 對帳號與 IP 都做 `Take(5)`，
批次E 讓 4740 的帳號比對改讀結構化欄位，但 `CorrelationAnalyzer` 的
【暴力破解→RDP 得手】同樣以正則剖析 `KeyDetails` 取來源 IP，
第 6 個以後的 IP 在顯示字串裡永遠看不到 → 交集靜默漏掉、關聯不觸發。
根因與 4740 完全相同（把顯示格式當資料契約），修法對稱：
簽章新增 `KeyIps`／`KeyIpsTruncated`（未截斷、封頂 200），關聯層優先讀它、
為 null 時 fallback 回原正則，精簡投影比照 `KeyAccounts` 不保留。
已補證據測試（交集落在第 6 個 IP）並以突變驗證（忽略 `KeyIps` 後測試變紅）。

## 體檢交接

**全量測試**：通過 **3377**、略過 6、總計 3383（基準 dev：3296 通過／略過 6，淨增 81）。
建置零錯誤零警告。分支 `feature/feedback-37`，21 個 commit。

**兩個獨立終檢已跑完並處理**（程式碼審查、文件契約對照），發現與處置：

| 嚴重度 | 發現 | 處置 |
|---|---|---|
| 高 | 校準候選查詢無日期過濾且以 `Contains` 展開巨大 IN，正式機會撞雙後端的參數上限 | 改 EXISTS 子查詢、日期下推，判定與匯出共用一份 |
| 高 | 匯出的 `IsMatch` 恆 false（`history` 傳 null，條件 4 永遠不過） | 撈同主機回看窗歷史，窗長與判定端共用常數；補證據測試並突變驗證 |
| 高 | 規則門檻資料集漏做定案的一半（最低門檻評估＋分位數） | 補上 magnitude 樣本與 P50/P90/P99；`Magnitude` 至此才有正式消費端 |
| 中 | `UpdatePrtgSettingsRequest` 混用兩種「沒送」語意，只送 URL 會清空白名單 | 六個參數全改可空「有送才更新」 |
| 中 | 回填天數差一（開始處理第 i 天即回報 done=i） | 改回報 i-1，前端顯示改「正在處理第 daysDone+1 天」 |
| 中 | 規則對照表被抄第四份（不可達的死分支） | 刪除 |
| 中 | 以反射斷言 DTO 屬性名的同義反覆測試 | 刪除該半段（行為已由前半段驗證） |
| 中 | WEB-SPEC 端點清單漏列回填與總開關、人工對應少列 DELETE | 補齊 |
| 低 | `ToUpper` 阻斷索引、`GetValueCoverageSummary` 死碼、註解過期與裸批次編號、兩處過程語氣 | 全數修正 |

**同型普查另外抓到一個規劃遺漏**（已修，見上一節）：批次E 只修了帳號那一半，
來源 IP 走同一條被截斷的顯示字串，跨日關聯會漏掉第 6 個之後的 IP。

**規劃項目逐條比對後補做**（第二次比對定案與實作時發現，全部已補）：
- 規則門檻的判定改以 **down 的 sensor-日數**為準（定案是「Down sensor-日 ≥30」），
  原本用四條規則加總——會讓「down 只有 3 筆但 flapping 有 100 筆」被判成資料充足，
  而 down 的門檻其實仍無從校準。已補守門測試並突變驗證。
- 補齊四項的累積量指標：值型基線的最早／最晚有效資料日、未對應主機的 sensor 數；
  規則門檻的逐規則 sensor-日數與 silent device-日數；觸發式量級的每晚 sensor 數／列數
  min／中位／max 與有效資料佔比；殘留判定的截斷比例與現行門檻命中數。
- 殘留匯出列補「跨日數」——「差一天才成立」與「完全沒有」在校準時是不同的資訊。
- 分母為零時補上定案的專屬文案「期間無事件，門檻無從校準，維持預設值」。
- 判定結果加 10 分鐘行程內快取；狀態端點強制重算（按鈕就是要看當下數字），
  匯出沿用快取以免同一次操作把整組查詢跑兩遍。

過程中另修正一處不實際的測試資料：規則命中測試用 `prtg-down-60m` 當 EventKey，
實際格式是 `prtg:{規則}:{objid}`，改為逐規則判定後才暴露出來。

## 執行紀錄
| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （尚未開始） | | | | |
