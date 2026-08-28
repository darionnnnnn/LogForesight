# LogForesight 回饋第三十五輪規劃

> 狀態：全案完成（體檢輪已過，見文末）
> 基準：dev@0c7b89e（3034 綠）
> 來源：使用者回饋 5 項＋討論新增 2 項（儀表板/報表快取、執行總表全部＋分頁）
> 實作方式：委派 agy 逐段實作，Claude 寫規格與獨立驗收。
> **委派模型**：批次A~B 用 gemini-3.7-flash-high；批次G 起 Gemini 組 5 小時額度用罄，
> 經使用者同意改用 claude-sonnet-4-6（批次G）；批次C 起 Claude/GPT 組週限亦用罄、
> Gemini 五小時窗重置，改回 gemini-3.7-flash-high（換回原因為備援組不可用，非任意切換）；
> 高風險段（批次D 並行與生命週期語意、批次E 記憶體結構）視委派結果決定是否收回自做

## 背景（核對結論摘要）

- 回饋 3（AD 設定沒變化）與回饋 4（顯示規則沒效果）同根因：規則解析正確且有測試，
  但套用點只接了權限異動與登入失敗明細兩處事件資料；側欄登入者與處理人下拉走
  `lf_users.DisplayName` 原值，從未接線。AD 區塊本身存檔即生效、無快取。
- 回饋 5 前提部分推翻：「先落地後補 AI」十二輪已做完。真瓶頸＝AI 消費者單條 FIFO＋
  佇列滿 200 反壓取數主線＋執行窗口到點 AI 整批放棄且孤兒補跑排最後。
- 回饋 2：三十四輪有界化皆在（22→10GB 相符）；剩餘熱點見批次E。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 執行窗口時間欄寬 | 小 | 無 |
| B | 使用者名稱顯示規則全站套用 | 中 | 無 |
| C | `ai_pending` 索引＋全域待補查詢 | 小 | 無（D 的前提） |
| D | 排程拆分：NetIQ 取數與 AI 分析成兩個獨立排程 | 大 | C |
| E | 記憶體第二輪有界化 | 大 | D（AI 佇列由 D 消滅後才動取數路徑） |
| F | 儀表板／報表結果快取 | 中 | 無 |
| G | 執行總表「全部」選項＋分頁 | 中 | 無 |

建議順序：A → B → G → C → D → E → F（A/B/G 可先收，C/D/E 一條鏈，F 獨立收尾）。

---

## 批次A：執行窗口時間欄寬

### 現況與核對結果
- `runs.js:556-573` 動態建 `input[type=time]` 時 inline `style.width='150px'`（上一輪已自
  130px 調過一次仍不足；12 小時制「上午 01:00」＋時鐘圖示約需 175~190px）。
- 全站僅此處用固定寬；其餘 time/date input 都靠 `col-auto` 自適應或容器 `min-width`。
- `site.css` 無任何 time input 規則、無共用 class；設定頁另有兩個郵件 time input
  （`Settings.cshtml:614,638`，`col-auto` 自適應、目前沒被裁）。

### 定案
不再用固定 `width` 追寬度（已失敗兩次）。新增共用 class（如 `.lf-time-input`：
`width:auto; min-width` 語意），排程窗口兩個 input 與設定頁兩個郵件 time input 一起套。

### 改動
1. `site.css` 新增共用 time input class；`runs.js` 移除 inline width 改掛 class；
   `Settings.cshtml` 兩處一併套用。

### 測試 / 驗收
- 瀏覽器實測（Browser pane）：排程作業頁 12 小時制下「上午 01:00」完整可見不被裁；
  設定頁郵件時間欄不回歸。截圖為證。

---

## 批次B：使用者名稱顯示規則全站套用

### 現況與核對結果
- 解析與套用引擎 `AccountDisplayFormatter.cs:121-238` 正常（`pattern => replacement`、
  逐行、`#` 註解、100ms timeout），測試已覆蓋 `$1` 取代。
- 既有套用點僅：`PermissionChangeService.cs:507/509/538/550/624/625`、
  `RecordDetailQueryService.cs:110/610`。
- 未套用：側欄登入者（`AuthController.cs:91-107` `/api/auth/me` → `layout.js:296-305`）、
  處理人下拉（`UserAdminService.cs:312` → `ui.js:1085`）、其他 `lf_users` 顯示名稱出口。
- `formatUserName`（`core/format.js:33-35`）硬拼 `顯示名稱(帳號)`——尾綴不歸規則管。

### 定案（使用者已確認）
- 規則套用到**所有**顯示 `DisplayName` 的出口；`(帳號)` 尾綴等「另外加上的部分」不動。
- 套用位置採**後端 DTO 出口**（顯示時轉換，DB 存原值不動）：凡是把 `lf_users.DisplayName`
  放進回應 DTO 的服務層，統一經過 `AccountDisplayFormatter.Format`。前端不重複套。

### 改動
1. 先 grep 普查所有把使用者 `DisplayName` 放進 DTO 的出口（已知：`/api/auth/me`、
   `UserAdminService` 使用者清單、處理人／負責人下拉來源、案件/待辦/稽核顯示名），
   逐一接上 Format；普查清單寫進執行紀錄，不得只改已知兩處。
2. 套用出口單點化：提供一個注入式 helper（讀當前規則＋Format），避免每個服務各自讀設定。
3. 補測試：`/api/auth/me` 與使用者清單 DTO 在規則 `^([^ ]+).* => $1` 下輸出「鄭孟瑋」；
   規則為空時原樣輸出。
4. 文件：WEB-SPEC §設定頁 3「套用點」清單改寫；`Settings.cshtml:401` popover 說明同步
   （明講：套用於全站顯示名稱，`(帳號)` 尾綴不受規則影響）。回饋 3 隨本批次消解，
   不另立批次。

### 測試 / 驗收
- 新增測試綠；既有 `AccountDisplayRulesTests` 不動仍綠。
- 瀏覽器實測：設定規則後側欄顯示「鄭孟瑋(帳號)」、處理人下拉同步生效。
- 普查驗收：`grep DisplayName` 的 DTO 映射點清單逐條標記「已套用／不適用＋理由」。

---

## 批次C：`ai_pending` 索引與全域待補查詢

### 現況與核對結果（**規劃前提已被推翻，本批次大幅縮編**）
- ~~`AiPending` 是 ContentJson 內序列化欄位~~ —— **錯誤**。實查程式碼：
  `ai_pending` **早已是 lf_daily_records 的真實資料表欄位**：
  `SchemaUpgrader.cs:112`（冪等加欄已在）、`LfDbContext.cs:125`（欄位映射）、
  `:409`（row 模型屬性）；寫入端也早已同步：`EfAnalysisRecordStore.cs:99`（Append）、
  `:232`（AttachAiResult 清 false）、`DailyRecordBackfiller.cs:97`、
  `RecordStorageShaper.cs:44`；讀取端可投影：`:584`、`:630`。
  原核對之所以判斷錯，是引用了 `docs/DB-SPEC.md:159-164` 的**過時敘述**而非程式碼。
  （教訓：資料層事實斷言必須 grep 程式碼，文件不算證據。）
- 因此**加欄不需要做**。但「存量回填不需要」這句話在實作驗收時被推翻一半——
  見下方「實作中發現的既有 bug」。實際缺的原本只有兩項：
  1. `ai_pending` **沒有索引**（`LfDbContext.cs:135` 只有 ExtractVersion 有索引）。
  2. **沒有全域待補查詢**：現存唯一的 pending 掃描是
     `NetiqPipelineService.cs:342-348`，逐主機 `ReadRecent` 再記憶體過濾，
     且會反序列化整包 ContentJson。

### 定案（使用者已確認要仔細評估影響面；依上方推翻結論縮編）
欄位與寫入端同步**早已存在**，故不加欄、不做存量回填；本批次只補
「索引」與「全域待補查詢」兩項缺口，並把 pending 判定單點化到欄位。

### 改動（縮編後）
1. `ai_pending` **加索引**：形狀依批次D 的掃描查詢定——需支援「全庫 pending、
   日期新→舊排序、分頁取批」，故**暫定**複合索引 `(ai_pending, record_date)`；
   雙後端（Sqlite/SqlServer）皆加，SchemaUpgrader 冪等。
2. 新增**全域待補查詢 API**（`IAnalysisRecordQuery` 或既有查詢介面上擴充）：
   不分主機、直接查 `ai_pending=1`、依日期新→舊、可分頁取批、
   **不反序列化 ContentJson**（沿用 `QueryLightweight` 的投影作法）。
   同時提供「待補總數」計數（批次D 的積壓數顯示要用）。
3. 寫入端普查仍要做（確認沒有寫入路徑會讓欄位與實際狀態分岔），
   但因欄位早已存在且同步，預期是核對而非修改；核對清單寫進執行紀錄。
4. 讀取端：**pending 判定單點化**——所有讀取端（含 `NeedsBackfill`）一律改以
   欄位為事實來源，不再讀 ContentJson 內的 `AiPending`；理由：批次D 的
   強制重跑是整批 UPDATE 欄位（不重寫 ContentJson，代價不可行），
   ContentJson 內的值僅於補寫覆蓋時自然同步，兩者短暫分岔是設計內行為，
   讀取端若有第二來源就會判錯。`NeedsBackfill` 外顯語意不變。
5. 文件：DB-SPEC 欄位表與 §159-164 改寫。

### 實作中發現的既有 bug（驗收時查出，本批次一併修）

`ai_pending` **欄位在 AI 完成後從未被清為 0**：欄位只有 `Append`（新增列）與
`DailyRecordBackfiller` 會寫，而 `AttachAiResult` 只把 `AiPending=false` 寫進
ContentJson、沒有同步抽出欄（`EfAnalysisRecordStore` 該處只同步了 `RiskLevel`）。
因為在此之前沒有任何查詢端讀這個欄位（孤兒掃描走 ContentJson），漂移完全無聲。

影響：批次D 的 AI 排程以欄位為事實來源，不處理的話會把**整庫已完成的紀錄**
全部當成待補，「補跑歷史」變成「整庫重跑」。

處置（兩段）：
1. 新資料：`AttachAiResult` 補上 `row.AiPending = false` 與 `row.AiAnalyzed` 同步（委派段已含）。
2. 存量：**不新建校正器**——沿用既有 `DailyRecordBackfiller` 的 `ExtractVersion`
   版本機制（本來就是為「舊列欄位需重新同步」設計的，已具備分批／可中斷續跑／
   背景服務／進度），把版本推進到 2 即重新同步全部舊列的抽出欄。
   同時把 `CurrentVersion` 改為公開常數並讓 `Append` 引用它——原本 Append 寫死 1，
   版本一推進會讓新列永遠落在待回填集合裡被反覆處理。

### 測試 / 驗收
- 關鍵表欄位與索引寫成 PRAGMA 斷言（`table_info` ＋ `index_list`，規格即測試）。
- 測試：Append 後欄位=1；AttachAiResult 成功後=0；回填器不洗掉欄位。
- 全域待補查詢：跨主機撈得到、日期新→舊、分頁不重不漏、
  回傳不含 ContentJson 反序列化（以投影欄位斷言）、計數與清單一致。
- 索引升級冪等（重跑 SchemaUpgrader 不炸），雙後端照既有測試模式。

---

## 批次D：排程拆分——NetIQ 取數與 AI 分析兩個獨立排程

### 現況與核對結果
- 排程框架單一 job 無類型概念：`SchedulerHostedService` 單例、`SchedulerRunState`
  扁平欄位、`lf_batch_runs` 無 JobType、`TriggerText` 有兩份（`RunMonitorService.cs:277`、
  `ScheduleController.cs:356`）。
- AI 段目前活在取數 run 裡：有界佇列 200（滿載反壓取數主線
  `NetiqPipelineService.cs:471-492`）、單條 FIFO 消費者、窗口到點整批 `AiAbandoned`、
  孤兒補跑排在缺漏日之後。
- 獨立執行先例：`NetiqProbeRunState.cs`（自帶併發 1 gate、不與夜間分析互斥）。
- AI 序列化：`AIService` 內建 SemaphoreSlim（`ScheduleOptions.cs:39-41` 註解）。

### 定案（使用者已確認）
- **實質拆分**：取數排程只做 Sentinel 取數＋規則/趨勢/關聯分析＋落地
  （`AiPending=1`＋既有暫代字串），**不再內含 AI 佇列與消費者**；
  `netiq-backpressure` 進度軌與佇列反壓一併移除。
- 新增 **AI 分析排程**：獨立啟用開關＋獨立執行窗口（預設全天不限，可自行縮窗），
  常駐掃 `ai_pending=1`（走批次C 欄位），**隨時可開始／停止**（立即執行與停止按鈕；
  停止不遺失狀態——`ai_pending` 即事實佇列，重新開始就從頭重掃）。
  **涵蓋保證（使用者定案）**：立即執行**不帶天數參數**——只要資料庫有
  `ai_pending=1` 的紀錄就全部納入，不設回望上限（資料庫範圍本身已由保留期界定；
  掃描不得沿用取數側孤兒掃描的 lookback 限制）。
  **強制重新分析（使用者定案）**：AI 立即執行對話框加勾選
  「強制重新分析：把現有 AI 結果全部重跑（規則更新後使用）」。
  機制：整批把範圍內紀錄重標 `ai_pending=1`；**不清空既有 AI 文字**——
  舊結果保留顯示（配「分析中」徽章語意，沿用既有三態），補寫時逐筆覆蓋。
  理由：全庫清空會造成長時間空窗，重標＋覆蓋效果等同且可中斷續跑。
  需 `Capability.Maintain`＋二次確認＋稽核紀錄。
  **中斷與續跑情境（使用者要求全數考慮）**——進度以每列 `ai_pending` 持久化，
  「完成一筆清一筆」，故：
  1. 停止／窗口到點／站台重啟後再開始：殘餘 `=1` 即續點，已完成（`=0`）不重跑，
     **天然從中斷點繼續，永不從頭**。
  2. 續跑順序：仍對殘餘 pending 套「完整性閘門＋新→舊」，與首跑一致。
  3. 中斷後加了新規則想全部重跑：再按一次強制重新分析→全部重標（含已完成的），
     符合意圖。
  4. **進行中按強制重跑（唯一競態洞）**：正在分析的那筆若在重標後完成，
     `AttachAiResult` 清 0 會把重標洗掉——該筆用舊規則結果卻標已完成。
     定案：強制重跑的執行順序固定為「優雅停止當前執行→整批重標→自動開始」，
     以順序消滅競態，不引入世代戳等額外機制。
  5. 只想補新落地、不重跑舊的：不勾強制即是預設行為。
  實作前先核對三十一輪
  「四模式重新分析」是否已有 AI 重跑語意可沿用（有就收斂成同一機制，
  不做第二套；核對結果記執行紀錄）。
  **自動檢查**：不另設「每天 N 次檢查」——AI 排程啟用時即為常駐輪詢
  （間隔**暫定** 60 秒，沿用排程輪詢粒度），窗口內只要發現待補就自動開跑，
  涵蓋保證比固定次數檢查更強；使用者要暫停就關啟用開關或按停止。
  處理順序（使用者定案，完整度優先於新舊）：
  1. **完整性閘門**：只處理「資料已完整落地」的主機日——統計紀錄已寫入
     （`ai_pending=1` 本身以紀錄存在為前提）**且該主機日不在取數排程當前
     處理範圍內**（取數執行中正在抓/重算的日期先跳過，等它落完才納入；
     判定機制**暫定**以取數 run state 的進行中範圍為界，實作時定）。
  2. 通過閘門者之間依**日期新→舊**排序，把資料庫中所有未 AI 分析的主機日
     全部補上；同日內逐主機。
  取捨：隔日 prompt 引用前一天 AI 摘要改為 **best-effort**（前一天摘要已存在
  才帶入，不存在則略過）——新→舊補歷史時前一天常未分析，
  原「同主機升冪保引用鏈」語意讓位給「最近的完整資料先有結論」。
  輸入從既有紀錄重建（沿用 `RetryAiAsync` 路徑；前置掃描與深析報告依既有規格不補）。
- **併發度**：設定「AI 分析併發數」預設 1（本機 LLM 單線程），實作為
  「按主機分片、片內序列、片間平行」，供未來外部 LLM API 多線程。
- **互斥**：兩排程互不互斥、可同時跑（AI 呼叫端已由 AIService semaphore 序列化）；
  AI 排程自帶單獨 gate（照 NetiqProbeRunState 樣板）。取數 run 與 AI 排程的
  同主機日競態由「完整性閘門」處理（見下方處理順序）：取數正在處理的主機日
  AI 先跳過；若仍有邊界重疊（取數重算把列改寫並重設 `AiPending`），
  最壞結果是該主機日下一輪再補跑一次，可接受，寫進設計註解。
- **執行紀錄**：`lf_batch_runs` 加 `JobType` 欄（JSON append-only 天生相容），
  兩份 `TriggerText` 同步；排程作業頁執行紀錄列表可辨識兩種作業。
- **進度顯示**（使用者點名要評估）：排程作業頁新增 AI 排程區塊——啟用/窗口/併發設定、
  獨立進度條（單位「件」，done/total＝本輪已處理/待補總數）、待補積壓總數
  （`ai_pending=1` 計數，讓 17 小時 1.2 萬筆這種積壓看得見在收斂）、
  「立即執行」與「停止」按鈕（`Capability.Maintain`）。全站單行告示
  `LatestActivity` 併入 AI 排程狀態（優先序：取數 > AI > 本機，**暫定**）。

### 改動
1. `ScheduleOptions` 加 AI 排程區塊（`AiEnabled`／`AiWindows`／`AiConcurrency`，
   預設 啟用=關、全天、1）；Dto／儲存／設定 UI 同步。
2. `NetiqPipelineService` 摘除 AI 佇列、消費者、反壓、孤兒掃描（孤兒語意整個移交
   AI 排程）；`AiFollowupQueue` 與其測試退役或改寫。
3. 新增 AI 排程 hosted service＋run state（獨立 gate、進度、取消；窗口判斷沿用
   `ScheduleWindow` 既有跨午夜語意）；掃描查詢走 `ai_pending` 欄位分頁取批，
   不整批載入（單批筆數**暫定**，實作時定）。
4. `BatchRun.JobType`＋兩份 TriggerText＋`RunMonitorService` 執行紀錄視角分流
   （AI 作業走逐筆視角 `GetRunList`，不進主機×日執行總表）。
5. 排程作業頁 UI：AI 排程設定卡＋進度＋積壓數＋立即執行/停止；
   `ScheduleController` 新端點（status 併入或另立，**暫定**）。
6. 文件：WEB-SPEC 排程章節、DETECTION-SPEC 三態語意段、DB-SPEC 連動。

### 測試 / 驗收
- 取數 run 不再呼叫 AI：測試斷言取數路徑零 AI 呼叫、紀錄落地含暫代字串＋`ai_pending=1`。
- AI 排程：完整性閘門測試（取數進行中的主機日被跳過、取數落完後下一輪被撿到）；
  處理順序測試（閘門內日期新→舊、涵蓋全部 `ai_pending=1` 不漏日）；
  前一天摘要 best-effort 測試（存在才帶入、不存在不炸）；併發>1 時不同主機平行；
  停止→重新開始不遺失待補（`ai_pending` 驅動、冪等）；立即執行無天數參數、
  掃描無回望上限（造遠期 pending 資料仍被撿到）；啟用中新落地的 pending
  於下一輪輪詢自動被撿到（不需人工觸發）；強制重新分析測試（重標後全部
  重跑、舊文字在覆蓋前仍可讀、未勾選時不重標——已完成者不受影響）；
  續跑測試（處理 N 筆後中斷→重啟只跑殘餘、不重跑已完成）；
  進行中強制重跑測試（先停止再重標再開始的順序被遵守，無「舊結果標已完成」）；
  窗口到點優雅停止、
  未完成者留 pending；成功後欄位歸 0＋`AttachAiResult` 生效。
- 取消／互斥：兩排程同時跑不互擋；AI 排程重入被 gate 擋下。
- 既有 `NetiqPipelineAiDecouplingTests` 等依新架構改寫，不得刪測試了事
  （改寫清單記執行紀錄）。
- 瀏覽器實測：AI 排程卡設定可存、進度與積壓數會動、立即執行/停止有效。

---

## 批次E：記憶體第二輪有界化

### 現況與核對結果（熱點排序）
1. `NetiqPipelineService.cs:527` `eventsByIp`：整個 job（上限 10 萬筆）全量常駐，
   且 `SentinelClient.Paging.cs:110` 把 API 回傳**所有**屬性原樣入袋、訊息不截斷
   （~3-4KB/筆）；12 job 並行 ≈ 3.6~4.8GB——最大宗。
2. `HostDayPostProcessor.cs:196` 權限異動 `dayRecords`：唯一無界集合
   （RawText 8000 字×數萬筆/主機日）。
3. AI 佇列 200×~2MB——**由批次D 消滅，本批次不處理**。
4. `AnalysisOrchestrator.cs:791-795` 本機 14 天區塊：`scanResult`（含 `BySource` 重複
   參考）在整段 14 天分析期間全程存活。
5. 無 GC 設定：Web SDK 預設 Server GC，傾向保留已提交記憶體不還 OS——
   工作集 10GB 有一部分可能非活物件。
6. 次要：`plans`/`allDates` 逐天重複 ToList、`LocalResults` 整趟不釋放、
   `EfRiskyEventStore.ReplaceDay` 追蹤查詢、`PermissionChangeStore` Skip/Take O(n²)。

### 定案（使用者已確認全做）
1. **E1 取數事件入袋即瘦身**：進 `eventsByIp` 前即投影——只保留 mapper 實際消費的
   欄位、訊息長度上限截斷（上限值**暫定**，以 mapper 與規則引擎實際用量定，
   不得截掉規則比對所需內容——先普查規則引擎讀哪些欄位/長度再定）；
   並評估「逐 IP 映射完成即釋放原始桶」。行為契約：分析結果（規則命中、權限異動
   RawText、AI 輸入）與現況一致，僅記憶體形狀改變。
2. **E2 `dayRecords` 有界化**：權限異動逐主機日改分批（沿用 500 批次粒度）
   「建構→去重→落地→釋放」，任何時刻常駐筆數有上界；配對（Paired）語意不變。
3. **E3 本機區塊生命週期**：`scanResult` 切成逐日結構後即棄整塊參考
   （含 `BySource`），分析迴圈只持有當日；`DefaultScanChunkDays=14` 維持不動（暫定）。
4. **E4 GC 設定**：加 `runtimeconfig.template.json`，方向**暫定** `GCConserveMemory`
   （保 Server GC 吞吐）；以實測工作集決定最終值，實測結果記執行紀錄。
5. **E5 順手項**：`LocalResults` 改滾動彙總、`ReplaceDay` 追蹤範圍縮小、
   `PermissionChangeStore` Skip/Take 改索引列舉——各是幾行級，Claude 順手做，
   不單獨立段。

### 實作結果與前提修正

- **E2 前提被推翻（縮編為不做）**：規劃估「RawText 8000 字 × 數萬筆 ≈ 0.2~0.5GB／主機日」，
  但 `TextTruncation.Truncate` 在字串未超長時**回傳同一個參照**，而事件訊息普遍遠短於 8000 字
  ——`RawText` 沒有複製原文，只是指向 `evt.Message`。`dayRecords` 實際只有物件開銷與
  `AlertText`（500 字截斷才配置），量級是數十 MB 而非數百 MB；真正的大宗是事件本身（E1 範圍）。
  且原規劃的分批修法**會破壞語意**：`FindRoutineSyncPairs` 的例行同步門檻必須看當日完整集合，
  分批會讓成對數假性跌破門檻（該處註解已記載同型的坑）。
- **E1 調整**：做了「解析端欄位過濾」（只留兩組投影清單的聯集，不論 API 回什麼），
  **未做訊息截斷**——訊息是規則比對對象，截斷可能讓長訊息的規則命中失效，
  與規劃自己寫的「不得截掉規則比對所需內容」相衝；落地端已有 RawText(8000)／AlertText(500) 設限。
- **E1 遞延項**：「逐頁即映射即分析、不再整 job 累桶」需重構 `RunBatchDayAsync` 的分桶邏輯
  （同 IP 多主機共用桶子的語意要一併處理）並以真實資料驗證，本輪未做，記入 BACKLOG。
- E4 採 `runtimeconfig.template.json`（Server GC + `GCConserve=5` + 不保留 VM），
  實際工作集效果待使用者實測。

### 測試 / 驗收
- E1：規則命中結果對照測試（同一份樣本事件，瘦身前後分析輸出一致）；
  截斷上限有測試釘住「規則所需最長欄位不被截」。
- E2：3 萬筆單主機日樣本下常駐峰值有界（以批次粒度斷言處理過程分批呼叫）；
  去重與配對行為既有測試不動仍綠。
- E3：跨區塊分析結果不變（既有分塊測試）；新增「逐日釋放」的結構性測試（暫定形式）。
- 全批次總驗收：使用者實測排程期間工作集，目標相對 10GB 有感下降
  （量化門檻不設硬值——GC 與資料形狀相依，以實測記錄）。

---

## 批次F：儀表板／報表結果快取

### 現況與核對結果
- 儀表板單請求約 10~12 支聚合查詢（BACKLOG:282-286），全 SQL 端聚合，
  「刻意不快取」理由＝快取鍵必須含可見範圍與顯示設定，漏鍵＝越權。
- 報表頁 `handlingScope != All` 走記憶體路徑最貴（BACKLOG:244-248）；
  歷史報表全文（lf_reports）開啟是單列 PK 查詢，很便宜，**不在本批次範圍**。
- 現成樣板：`IssueRankingCache`（TTL 30s、鍵含 from/to/visibleHostIds/totalHosts、
  回副本），契約測試 `ReportPerformanceContractTests.cs:180-364` 可照抄；
  已知缺口：鍵不含 hostSnapshot、回傳淺副本（BACKLOG:377-383）。

### 定案（回應使用者「每次變更或多久沒收到變更就自動建 cache」）
- 採「**資料版本戳＋TTL**」而非背景預熱：全域資料版本戳（分析 run 落地、案件/待辦
  異動、權限異動審核等寫入路徑 bump），快取鍵＝版本戳＋期間參數＋可見範圍雜湊＋
  顯示設定維度。資料沒變→同鍵長期命中（等效「多久沒變更自動沿用」）；
  資料一變→鍵換掉，變更後首次請求重算（等效「每次變更自動重建」）。
- 不做逐使用者背景預熱：可見範圍因人而異，預熱要窮舉授權組合，成本與越權風險
  都不划算；版本戳方案讓「第二個以後的使用者」都吃快取，已covers主要痛點。
  （此為與使用者原提法的差異點，列為討論確認項→已於規劃階段說明，實作前再確認。）
- 套用面：儀表板 `GET /api/dashboard/summary` 與報表 `GET /api/reports/summary`
  整包回應快取；順手補 `IssueRankingCache` 兩個已知缺口。
- 版本戳 bump 點普查：所有會改變這兩頁數字的寫入路徑逐一列出（分析落地、認領/處理、
  權限異動審核、主機表異動、顯示設定變更），清單記執行紀錄——漏 bump＝顯示過期資料。

### 改動
1. 全域資料版本戳（行程內 Singleton＋寫入路徑 bump；多行程部署下退化為 TTL 保底，
   TTL 值**暫定**）。
2. 兩支 summary API 接入快取（照 IssueRankingCache 樣板新建，鍵含授權與顯示維度）。
3. 補 IssueRankingCache 缺口（hostSnapshot 進鍵、深副本或不可變回傳）。
4. 文件：WEB-SPEC 對應章節＋BACKLOG 移除已解條目。

### 測試 / 驗收
- 契約測試照 `ReportPerformanceContractTests` 模式：命中／TTL／**授權維度不同不共用**／
  版本戳 bump 後不命中。
- 越權紅線測試：兩個可見範圍不同的使用者相同參數請求，互不見對方資料。
- 瀏覽器實測：儀表板二次載入明顯變快；寫入（如認領一筆待辦）後刷新數字即時更新。

---

## 批次G：執行總表「全部」選項＋分頁

### 現況與核對結果
- 天數選擇器 `Runs.cshtml:115-117` 只有 7/14/30；`runs.js:46-48` 以 `days` 打
  `/api/runs/summary|errors|list`；後端 `RulesController.cs:148` 夾 1~90 天。
- `RunMonitorService.GetDaySummaries`（:57-101）逐日×逐主機算狀態，且
  `GetRecentRuns` 每次全量讀執行紀錄 blob——180 天×3682 台一次算完會很重，
  「全部」不能用「一次撈全部再前端分頁」的做法。
- 「設定頁的最大天數」有兩個候選：執行歷程保留 `RunLogRetentionDays`（預設 120）
  與業務資料保留 `RetentionDays`（預設 180）。執行總表是執行歷程視角
  （逾期後 BatchRun 已被清理，狀態只剩分析紀錄代理），故「全部」上限採
  **`RunLogRetentionDays`**（**暫定**，若使用者期望看到 180 天再改）。

### 定案
- 選擇器加第四鈕「全部」＝ `RunLogRetentionDays` 現值；其餘 7/14/30 不動。
- 分頁做在**伺服器端**：summary API 加分頁參數（以日期為分頁軸、新→舊，
  每頁天數**暫定 30**），每頁只計算該頁日期範圍的狀態；errors/list 沿用
  相同天數範圍語意，list 若筆數過大同步分頁（**暫定**，依實作時筆數評估）。
- 後端天數上限自 90 放寬為 `max(90, RunLogRetentionDays)`，仍夾上限防濫用。

### 改動
1. `RulesController` runs summary 端點加 `page`/`pageSize`（或 `offset`）與總頁數回傳；
   clamp 規則放寬；`RunMonitorService.GetDaySummaries` 改吃日期區間。
2. 前端：第四鈕「全部」（天數值向後端要或由設定 API 帶出，不寫死）；
   表格下方分頁控制（沿用站內既有分頁樣式）；切換天數時分頁重設回第一頁。
3. 文件：WEB-SPEC 排程作業章節 API 參數同步。

### 測試 / 驗收
- 分頁邊界測試：總天數不足一頁、剛好整頁、跨頁日期連續不重不漏。
- 「全部」上限跟隨 `RunLogRetentionDays` 設定值變動的測試。
- 瀏覽器實測：選「全部」載入時間可接受、分頁切換正常、7/14/30 行為不回歸。

## 明確不做（本輪定案）

- AI 佇列持久化為獨立表：`ai_pending` 欄位即事實佇列，夠用。
- 逐使用者背景預熱快取：見批次F 定案理由。
- `lf_batch_runs` blob 全量讀改造（BACKLOG:252-256）：與本輪無直接關係，維持遞延。
- AI token 統計對應件數（BACKLOG:369-374）：維持遞延；批次D 的積壓數已提供件數視角。
- 歷史報表（lf_reports）讀取優化：已是單列查詢，無需處理。

## 委派注意事項（前次委派實測所得，重新委派前務必套用）

> 本輪實作曾於前一條分支開跑後作廢重來（repo 分支誤調整），程式碼全數重做；
> 以下是當時委派 agy 實測到的具體落差，規格撰寫時直接預防。

1. **BOM 會被靜默剝除**：agy 的編輯工具會移除 UTF-8 BOM（前次 7 個檔案中招）。
   每段驗收必須逐檔比對 BOM，發現即補回。
2. **同檔案內「要改／不要改」必須寫成行為描述，不能只寫檔名**：前次規格把
   `UserAdminService` 列進「不要改寫入端」清單（本意只指建立使用者那條路徑），
   agy 因此**整檔跳過**，漏改 `ToDto` ——正是使用者截圖的處理人下拉資料源。
3. **隱藏耦合要在規格裡明寫**：指派下拉的「置頂＋標（負責人）」是拿
   `/api/admin/users` 的 displayName 與 `HandlingDto.OwnerNames`
   （`DayHandlingCommandService`，刻意不帶帳號）**逐字比對**——
   兩端必須同時套用顯示規則，只套一邊會靜默失效且無錯誤訊息。
   凡改動顯示名稱的批次，規格都要點名這條契約並要求測試釘住。
4. **測試要走 DI**：前次新增的 `/api/auth/me` 測試直接 new Controller，
   抓不到「新相依沒註冊」。驗收要求端到端或經 DI 解析。

## 執行紀錄

> 驗收一律由 Claude 獨立重跑，不採信委派方摘要。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A 時間欄寬 | Claude | 完成 | 建置 0 警告；BOM 三檔比對基準一致 | 幾行級未委派；改共用 class 取代寫死 px |
| B1 單點服務＋兩出口 | agy（gemini-3.7-flash-high） | 完成 | 3036 綠；白名單乾淨；/me 測試確實走 DI 容器 | 無落差 |
| B2 全站出口＋置頂修復 | agy（gemini-3.7-flash-high） | 完成 | 3044 綠；NameFormat 舊方法全 repo 零命中 | 見下方兩項 |
| G 執行總表 | 未開始 | | | |
| C 索引與全域查詢 | agy（gemini-3.7-flash-high） | 完成 | 3057 綠；反向驗收（不得誤加欄位）通過 | 驗收查出 ai_pending 欄位漂移既有 bug，存量校正由 Claude 補做 |
| D1 取數拆離 AI | agy（gemini-3.7-flash-high） | 完成 | 3050 綠；四條 grep 零命中 | BOM 剝除 1 檔已補 |
| D2 AI 獨立排程 | agy（gemini-3.7-flash-high） | 完成 | 3060 綠；8 情境測試 | 反射讀私有欄位、整批重標範圍過大、可選相依＋重複路由，皆由 Claude 修正 |
| D3 AI 排程 UI | agy（gemini-3.7-flash-high） | 完成 | 3060 綠；瀏覽器實測 | 計時器逐字複製一份，已合併；BOM 剝除 1 檔已補 |
| E 記憶體 | Claude | 完成 | 3062 綠 | E2 前提被查證推翻（縮編）；E1 訊息截斷不做、整 job 累桶遞延，理由見上 |
| F 快取 | Claude | 完成 | 3068 綠；6 條契約測試含越權紅線 | 入口多走訪一次主機表撞破既有效能契約，已修 |

### 批次B 落差與處置

1. **B1 造成的置頂破口（驗收時讀程式碼比對發現，測試全綠抓不到）**：
   B1 讓 `/api/admin/users` 的 displayName 套了規則，但 `HandlingDto.OwnerNames`
   仍是原值；兩者是指派下拉「置頂＋（負責人）」的逐字比對鍵（`ui.js` searchableUserSelect），
   規則一設就靜默失效且無錯誤訊息。→ 列為 B2 必修 1，兩處 OwnerNames 一併套規則，
   並加「兩端字串必須相等」的契約測試釘住。因此 B1／B2 綁定，不單獨併 dev。
2. **BOM 又被剝除 6 檔**（B2）：`BulkScaleGateTests`／`LinuxIssueOwnerAdminTests`／
   `NeedsBackfillTests`／`ScheduleController`／`IssueHandlingCommandService`／
   `IssueOwnerAdminService`，與前次幾乎同一組。規格已明寫禁止仍發生
   → 判定為 agy 編輯工具的固有行為，規格擋不住，只能每段驗收比對基準分支後補回。
   已逐檔補回並複驗全數一致。
3. **白名單外連帶（判定合理）**：`HostDtoMapper` 依規格改為可取得服務，
   因是 `internal static` 而多一個**必要**參數（非可選、非多載），
   呼叫端 `HostAdminService`／`NetiqHostService` 隨之調整。

## 體檢交接

> 本節依 `project-closeout` §0 建立：**體檢不得由實作方模型執行**。

- **實作模型**：Claude Opus 5（規格撰寫、獨立驗收、批次C 存量校正／E／F 親自實作）
- **委派模型**：批次A~B `gemini-3.7-flash-high` → 批次G `claude-sonnet-4-6`（Gemini 5 小時窗用罄）
  → 批次C 起改回 `gemini-3.7-flash-high`（Claude/GPT 週限亦用罄，非任意切換）
- **基準**：dev@0c7b89e（3034 綠）→ 本輪 **3068 綠／略過 6／失敗 0**
- **分支**：`feature/feedback-35`（自 dev 重建，18 個 commit，工作區乾淨）

### 實作方自認最沒把握、請優先看的地方

1. **批次D2 完整性閘門的退路作法**：規格允許「取數執行中時只跳過今天與昨天」。
   這個近似在「取數正在重跑很舊的日期」（回補歷史）時會失準——AI 可能撿走取數正在寫的舊日期。
   最壞後果是該主機日再補跑一次，但請確認這個推論成立、且沒有更糟的形狀。
2. **批次F 版本戳的涵蓋面**：bump 做在「任何成功的非 GET 請求」＋兩個背景排程結束。
   請確認沒有「會改變儀表板／報表數字、卻既不走 HTTP 也不在那兩個排程裡」的寫入路徑
   （例如其他 hosted service、遷移器、回填器）。漏掉時 TTL 只兜 30 秒，但仍是假新鮮。
3. **批次C 存量校正的副作用**：推進 `DailyRecordBackfiller.CurrentVersion` 會讓**全部既有列**
   重新回填一次。三千台×180 天的既有部署升級後會跑一趟全表回填（背景、可中斷續跑），
   請確認這個成本與既有的回填進度顯示相容，且不會與新的 AI 排程互搶資源。
4. **批次E1 的欄位過濾是否漏欄**：`ParseKeepFields` 取兩組投影清單的聯集。
   若有程式碼直接讀 `Fields["某個不在投影裡的鍵"]`，過濾後會靜默取不到值。
   請 grep `SentinelFieldMap` 以外的欄位字面值用法確認。
5. **AI 排程與取數排程同時執行的資源競爭**：兩者刻意不互斥，AI 呼叫由 AIService 的
   semaphore 序列化。請確認地端 LLM 在取數尖峰同時被打時不會拖垮整體。

### 已知遞延（非缺漏，已記 BACKLOG）

- E1 的「逐頁即映射即分析、不再整 job 累桶」需重構分桶邏輯並以真實資料驗證，本輪未做。

### 尚未實測的部分

- 記憶體實際改善數字（E1/E3/E4）需正式環境跑一晚才有結論。
- AI 獨立排程的實跑吞吐（含併發>1）僅有單元測試與本機 UI 驗證，未接真實地端 LLM 跑量。

## 體檢輪修正（體檢方：Claude Fable 5）

體檢方式：兩個獨立 Explore 各審一次全 diff（程式碼獵 bug／文件與規劃比對）＋體檢方親讀
最後手改段（D2 迴圈、F 快取）＋自跑全量測試。共確認 10 個真 bug、3 處 B2 漏網出口、
1 組死碼鏈與整批文件缺漏，全部修畢。逐項：

1. **AI 補跑吃輕量投影（高）**：D2 迴圈直接把 `QueryPendingAi` 的投影餵 `RetryAiAsync`——
   `CorrelationAlerts` 是 `"(lightweight)"` 佔位字串（會被當成高嚴重度關聯組進 prompt）、
   `TrendAlerts`／`AuditEventCount` 全空。跨段產出鏈斷點：批次C 的佔位設計是佇列用，D2 誤當
   完整輸入。修：處理前 `GetOne` 完整載入；補回歸測試並以突變驗證（退回投影即紅）。
2. **測試替身回完整紀錄遮蔽上一條**：兩個 fake 的 `QueryPendingAi` 改為與正式同形狀的輕量投影。
3. **儀表板快取鍵漏 ViewAudit 維度（越權，高）**：`RecentLoginFailures` 只有稽核權限者會填，
   可見主機相同、權限不同的帳號會共用快取。修：鍵加 `audit:{bool}` 維度。
4. **E1 欄位過濾關掉 probe 診斷（高）**：`keepFields:null` 逃生口零呼叫端，probe 的
   「全欄位聯集」功能靜默失效。修：`SentinelSearchRequest.RawFields` 旗標，probe 26 個呼叫點全帶。
5. **取數把 AI 積壓當缺漏日重抓（高）**：`NeedsBackfill` 規則 2（AiPending→補）在拆分後
   讓「只補跑失敗或未執行」對整個積壓重打 Sentinel、重寫紀錄再標回待補，與 AI 排程互相打架。
   修：移除該規則（待補歸 AI 排程；「AI 已定案失敗」的存量仍由規則 3 涵蓋），
   六個既有契約測試依新語意改寫。
6. **DetailPruned 舊列永久空轉**：詳情已清的列 ContentJson 為空，回填器解析失敗只推版本、
   pending 漂移保留；無回望上限的掃描會反覆撿起。修：`WhereAiPending`／`MarkAllForAiRerun`
   排除 `detail_pruned`；回填器解析失敗且已清詳情時順手清 pending。
7. **失敗紀錄單輪內反覆重試**：新→舊排序讓失敗者永遠排最前，O(N/50×k) 次 AI 呼叫。
   修：單輪 attempted 集合，同輪不重試同一筆（跨輪重試由下次輪詢自然發生）。
8. **強制重跑先重標後搶 gate**：被搶走時整庫已重標、API 卻回「已有執行」。修：順序改為
   停止→取得 gate→重標→開始。
9. **版本戳漏五個背景服務**：回填器／遷移器／種子服務改寫儀表板聚合欄位但不 bump。修：五處補。
10. **AI 執行不寫執行紀錄（PLAN 定案未做）**：補 `BatchRun.JobType`（"ai"）＋AI 排程
    StartRun/FinishRun（含完成／失敗件數）；主機×日總表排除 JobType=ai（避免幽靈主機），
    逐筆視角加「作業」欄。
11. **B2 三處漏網出口**：`DayHandlingCommandService.HandlerName`、
    `IssueHandlingCommandService.ExistingHandlerName`／`GetHandlerCandidates` 補套顯示規則。
12. **errors/list 天數上限仍寫死 90**：選「全部」時三頁籤口徑靜默不一致。修：與 summary 共用
    `max(90, RunLogRetentionDays)`。
13. **子進度鏈成孤兒死碼**：D1 拆除後 `netiq-ai`／`SubProgress*` 整條鏈（state／DTO／前端子進度條）
    無任何生產端。修：整組移除；全站告示 `/api/run-activity` 改為取數閒置時輪到 AI 排程
    （優先序 取數 > AI，同時補上 PLAN「LatestActivity 併 AI」的定案）。
14. **AI 自動開跑加回填閘門**：存量校正完成前不自動開跑（未校正的 pending 大量是已完成舊列）。
15. 次要：`SetLatestMessage` 死方法移除、`CancelAi` 錯誤訊息中性化、`MaxDays` 註解與行為對齊、
    強制重跑 UI 為獨立按鈕（實作變體，行為等價，此處回填說明）。

**未修、維持遞延**（誠實記錄）：`IssueRankingCache` 兩缺口（外層 SummaryCache 已縮小影響，
BACKLOG 註記）；取數 run 詳情的 AiCalls 對機房路徑恆 0（AI 件數改看 JobType=ai 的執行列，
屬新分工的真實狀態）；執行總表分頁後 `TotalHosts` 隨頁視窗略有語意差（實務被主機表聯集蓋過）。

**文件輪**：DETECTION-SPEC 兩階段節整段改寫為「兩個獨立排程」；WEB-SPEC 六處
（子進度／套用點／NeedsBackfill／執行紀錄 JobType／API 清單／徽章）；DB-SPEC 的 `ai_pending`
自「僅存於 ContentJson」修正為真實欄位＋事實來源單點化；BACKLOG 移除「儀表板刻意不快取」、
改寫背壓敘述、註記內層快取缺口；CLAUDE.md 測試基線更新；HelpContent 六檔
（兩份 scheduler 整節重寫＋AI 排程操作說明新增、記錄詳情三態、權限敘述、設定頁補
「使用者名稱顯示規則」一節）；Settings popover 補全站套用範圍說明。

測試：3068 → 體檢輪後全量綠（最終數字見合併 commit）。
