# 回饋第二十七輪規劃（FEEDBACK-27）

日期：2026-08-24。輸入：12 項使用者回饋（P1~P12）。分支：自 dev 開 `feature/feedback-27`。

## 核對結論（摘要）

| 項 | 判定 | 根因 |
|---|---|---|
| P1 AI token 統計 | 功能不存在 | `AIService` 回應 DTO 未定義 `usage`，token 數被靜默丟棄；無計量層 |
| P2 AI 佇列進度不可見 | 屬實 | 背壓期 `netiq-backpressure` 以主機日數字覆寫子進度軌且只報一次；佇列深度未暴露 |
| P3.1 vs基準排版 | 屬實 | `format.js issueBaselineCell()` 目前兩行（唯一出口，三頁共用） |
| P3.2 最近出現折行 | 屬實 | `records.js` 該欄缺 nowrap/寬度控制 |
| P4 彙總類別沒資料 | 真資料 bug | 去重跳過已入庫事件→重跑批成對數跌破門檻→else 分支 `DeleteByDedupeKey` 刪掉正確彙總列；被合併明細從未入庫 |
| P5 說明文字截斷 | 屬實 | `.lf-issue-explanation` 硬寫 `max-width:18rem`（權限異動頁另有 28rem 同型） |
| P6 chips 無間隔 | 屬實 | `HandlerDetail.cshtml` 容器漏 `lf-toolbar__chips`（全站 20 處唯一漏網） |
| P7 圖表底不對齊 | 屬實 | 左 chart `height:100%`、右圓餅 `height:auto`＋圖例推高；左右 header 高度不同 |
| P8 表格btn疊加 | 屬實 | `charts.js attachToolbar()` 只 append 不清空（按鈕與隱藏表格都疊；排行雙視角共用容器同病） |
| P9 登入字體變化 | 部分 | 非 CSS focus 規則，是瀏覽器 autofill 用 UA 字型；需明確定義 `.form-control` 與 `-webkit-autofill` 字級字型 |
| P10 負責人無 filter | 屬實 | `checkboxList()` 無篩選無捲動上限；主機頁負責人勾選同型 |
| P11 報表/儀表板效能 | 有 8 個候選點 | 一次請求 12+ 次串列聚合、3 次全主機載入、群組×主機 O(n×m)、非 All scope 記憶體全載入等 |
| P12 說明書 | 部分 | 缺 4 章（報表/稽核/問題檔案/NetIQ 維護）＋數處與實作不符；AI 與使用者共用同一份內容 |

## 定案（與使用者討論結果）

1. P4 兩個問題都修（刪彙總分支＋Prune 時間基準），既有遺失資料靠修正後**再跑一次資料重跑**重建，不寫一次性回補。
2. P1 計量存放走 `lf_blobs` 單例 blob（沿用 `ScheduleOptionsStore` 模式），不開新表、不進 migration、不放 Db 目錄 json 檔（不隨 DB 備份、IIS 檔案權限變數）。單價存 `SystemSettings`。快取命中不計量（計量在 `AIService` 層）。
3. P11 八個候選點本輪全做；**測試先行**：每點先由 Claude 寫下可機器驗證的驗收（行為不變＋查詢次數/複雜度斷言）再動實作。
4. P12 雙版本＝逐章 `NN-*.ai.md`（缺檔 fallback 簡明版）；AI 問答吃詳細版、使用者手冊吃簡明版。四缺章全補。
5. 委派：整輪委派模型＝**agy**。資料正確性/計量語意/效能架構級（A、B1、B3 後端、F 的驗收測試與架構級）由 Claude 做；UI 機械修、統計頁 UI、filterable 清單、說明書結構與補章初稿委派 agy。文件與測試規劃一律 Claude。

## 作業總覽

| 作業 | 主題 | 執行者 |
|---|---|---|
| A | P4 權限異動彙總重跑修復 | Claude |
| B | P1+P2 AI 可觀測性（計量＋佇列進度） | B1/B3=Claude，B2=agy |
| C | P3/P5/P6/P8/P9 UI 單點修 | agy |
| D | P7 報表圖表底對齊 | agy |
| E | P10 可篩選勾選清單 | agy |
| F | P11 報表/儀表板效能 | 測試=Claude；F1/F2=agy，F3/F4/F5=Claude |
| G | P12 說明書雙版本＋補章 | 結構=agy、補章初稿=agy、內容審校=Claude |

委派模型：agy。**前半用 claude-sonnet-4-6，自作業 G2 起改為 gemini-3.7-flash-high 並不再換回**
（原因：agy 的 Claude/GPT 組五小時額度在 G2 委派時用罄，週餘 33%；Gemini 組餘 98%。
使用者定案：往後以 gemini 為主力，額度不足才回頭用 claude 組）。
每階段抄成獨立規格檔委派，Claude 獨立重驗不採信摘要，同段 3 輪為限。**委派期間 Claude 不動同一 repo。**

---

## 作業A：權限異動彙總重跑修復（P4）— Claude

### A1 刪彙總分支改為不動 — ✅ 完成
實作時發現根因比規劃寫的更前面一步：**配對是在去重後的子集上做的**。去重先跑 → 重跑時
`recordsToAppend` 只剩沒見過的事件 → 成對數假性跌破門檻 → 才走到刪除分支。定案契約：
1. 配對改在「來源本次回傳的當日全量事件」上做（去重移到配對之後）——根因修法。
2. 達門檻 → Upsert 彙總列並移除成對明細（行為不變）。
3. 未達門檻但**已有**該主機日彙總列 → 保留彙總列，且這批成對事件不逐則列出（它們正是彙總涵蓋的同一批，逐則列會與彙總重複計算）。
4. 未達門檻且無既有彙總列 → 全部逐則（行為不變）。
5. **任何情況都不刪彙總列**；`DeleteByDedupeKey` 保留為通用 store 方法，註解標明不再用於撤彙總。
重構：`ExtractRoutineSyncPairs` 拆為 `FindRoutineSyncPairs`（只認事實、不改集合）＋`BuildRoutineSummary`。
- 驗收（全綠）：重跑回傳完整當日事件→彙總列仍在且 PairCount 不變；重跑只回子集→彙總列仍在、PairCount 不被覆寫成 3、成對事件不重複列出；重跑無事件→彙總列仍在；無既有彙總列且未達門檻→全部逐則 6 筆。全套 2502 綠。

### A2 Prune 時間基準對齊 — **撤銷（前提不成立）**
規劃時的理由是「重跑會刷新 `CreatedAt`，等於替舊資料續命」。實作時讀程式碼推翻：
- 明細列由去重鍵擋住，重跑不會重寫，`CreatedAt` 不變。
- 彙總列走 `UpsertByDedupeKey`，只更新 Target/AlertText/Covered\*/PairCount，**不動 `CreatedAt`**。
- 「彙總列 `CreatedAt` 被刷新」只發生在它先被舊 bug 刪掉、再重新 Insert 時——A1 修好後這條路徑不存在。

且既有測試 `Prune依寫入時間刪列_事件時間久遠但剛寫入資料庫的列清理後仍存在` 編碼的是刻意契約：
NetIQ 回補的舊事件（detected_at 可達 100 天前）不該一寫入就被清掉。改判準會讓剛匯入的資料立刻消失。
結論：維持 `CreatedAt` 判準不變，僅把上述理由寫進 `Prune` 註解（原註解只說「依寫入時間」沒說為什麼）。

### A3 文件與驗證
- `DB-SPEC.md`/`DETECTION-SPEC.md` 相應段落更新；`HostDayPostProcessor.cs:207-209` 舊註解改寫為新語意。
- 使用者實測路徑：修正併入後再跑一次資料重跑，確認彙總類別恢復。

## 作業B：AI 可觀測性（P1+P2）

### B1 token 計量層 — Claude
- 行為契約：
  - `AIService` 回應 DTO 補 `usage`（prompt/completion/total tokens）；每次**實際發出的 HTTP 呼叫**（含重試成功那次；快取命中不算）記一筆到日計量。
  - 新增 `AiUsageStore`（`lf_blobs` 單例 blob）：按日累計 `{date, calls, promptTokens, completionTokens, totalTokens}`，保留 90 天（暫定），另存「累計起算日＋累計總量」；「清空重算」＝重置累計並清日列。
  - `usage` 缺席（部分本機模型不回）時 calls 照計、token 記 0，畫面標示「該日有 N 次呼叫未回報 token」。
  - `SystemSettings` 新增單價鍵（暫定：每百萬 input token 價、每百萬 output token 價，皆可空；空＝不估費）。消費端＝設定頁估費顯示（滿足「新增設定必須有消費端」紅線）。
- 驗收：新測試——計量累加、跨日分列、快取命中不計、usage 缺席路徑、清空重置。全綠。

### B2 設定頁統計 UI — agy
- 行為契約：設定頁 AI 頁籤新增統計區：今日消耗、累計消耗（含起算日）、清空重算按鈕（confirm 後打 API）、單價輸入與估算金額（前端即時算，單價存後端）、展開 30 天表格（每日：呼叫數/AI 整理件數/prompt/completion/total）。新增對應 admin API（讀統計、清空、存單價）。
- 不能破壞：AI 頁籤既有設定表單與進階折疊區行為。
- 驗收：API 整合測試（讀/清/存單價）；手動驗 UI。

### B3 佇列深度與進度顯示 — Claude
- 行為契約：
  - `AiFollowupQueue` 暴露佇列深度與已消化數。
  - 背壓期間子進度改報 AI 佇列語意：`netiq-backpressure` 改帶「已消化/已入列」且**隨每件 AI 完成持續更新**（不再只報進入瞬間一次、不再用主機日數字）；`runs.js` 單位表補上對應「件」。
  - 同型一併修：`HealthService.cs:67` 自承的「體檢頁停在搜尋凍結數字」症狀，改讀同一組 AI 進度。
  - `HelpContent/10-scheduler.md` 文案改為與實作相符（歸入作業G一併審）。
- 驗收：新測試——背壓期間狀態 DTO 帶 AI 消化數且隨完成遞增；`runs.js` 顯示由手動驗證截圖。

## 作業C：UI 單點修（P3/P5/P6/P8/P9）— agy

### C1 vs基準三行版（P3.1）
- 契約：`issueBaselineCell()` 改三行：`基準 N 台/日`／`→ M 台`（暫定分行點照使用者範例：「基準 378 台/日」「→ 392 台」）／徽章行不變。`issueBaselineText()`（CSV/表格文字版）語意同步更新——兩份輸出內容一致，僅格式不同。三個呼叫端（records/dashboard/reports）都要目視驗。
- 驗收：format 相關既有測試調整＋新斷言；三頁截圖。

### C2 最近出現固定寬（P3.2）
- 契約：依問題列表「最近出現」欄日期不得折行（nowrap 或固定 min-width，作法由執行端選）。
- 驗收：手動驗證窄視窗下不折行。

### C3 說明文字截斷（P5）
- 契約：`.lf-issue-explanation` 移除硬寫 `18rem`/`28rem`，改由欄位實際可用寬度決定（`max-width:100%`＋表格欄寬控制）；records 與 permission-changes 兩頁一併，超出仍以 ellipsis 截斷（tooltip 顯示全文的既有行為若存在則保留）。
- 驗收：`auditd (0)` 該列說明完整顯示；窄視窗仍 ellipsis 不撐破版面。

### C4 chips 間隔（P6）
- 契約：`HandlerDetail.cshtml` chips 容器補 `lf-toolbar__chips`，與全站其餘 19 處一致。
- 驗收：目視間隔正常。

### C5 toolbar 疊加（P8）
- 契約：`attachToolbar()` 重繪時不得殘留舊按鈕與舊隱藏表格（清空既有產物再建）；主機/問題排行**共用容器切換視角**也不得殘留另一視角的按鈕/表格。多次點擊期間快捷後 DOM 中該容器各只有一份 toolbar 與 table。
- 驗收：連點「近30天」×3 後檢查 DOM 僅一顆表格鈕、一份表格；切換排行視角後同樣成立。

### C6 登入字體一致（P9）
- 契約：`.form-control` 明確定義 `font-size` 與 `font-family`（採用目前聚焦後的站台字型與大小，即「放大後那個」），並補 `input:-webkit-autofill` 同字級字型，全站表單一致、非登入頁專屬。
- 不能破壞：全站其他表單視覺（字級不得因此變動——若現值已等同站台字級則僅 autofill 需補）。
- 驗收：登入頁 autofill 狀態與聚焦後字體一致；抽查兩個其他頁表單無視覺回歸。

## 作業D：報表圖表底對齊（P7）— agy
- 契約：報表頁左「主機告警排行」長條圖與右下三張圓餅圖的**圖形底緣**在 lg 以上視口對齊。可動手段：右側圓餅容器高度規則、圖例定位、左右卡 header 高度貼齊；不得改動圖表資料與互動行為。
- 驗收：1920 寬截圖目視底緣對齊；縮至 md 以下不破版。

## 作業E：可篩選勾選清單（P10）— agy
- 契約：`checkboxList()` 新增 `filterable` 選項：頂端文字輸入即時篩選（比對顯示名，不分大小寫）、清單容器捲動高度上限（暫定約 12 列高）；未啟用時行為與現狀完全相同。啟用點：問題檔案負責人（issue-owners）與主機負責人（hosts）兩處；其餘呼叫端不動。
- 不能破壞：既有勾選值的讀寫、五個呼叫端的現行為。
- 驗收：新增 ui 層測試或以 API/DOM 驗證篩選後勾選值不丟失（篩掉的已勾項仍保留勾選）；兩頁手動驗。

## 作業F：報表/儀表板效能（P11）— 測試先行

**先行階段 F0（Claude）**：為下列各點寫驗收測試——以現有 TestDoubles 計數查詢次數／以行為測試鎖定輸出不變（同輸入下 API 回應 JSON 等值）。F0 綠（現狀基線）後才開始改。

### F1 全主機表載入收斂 — agy
- 契約：一次 `/api/report/summary` 請求內主機表投影載入至多 1 次（現況 ≥3：ReportService 兩分支＋IssueRankingBuilder＋visibility）；以參數傳遞取代重查。輸出不變。
- 驗收：F0 計數測試斷言載入次數；行為測試等值。

### F2 儀表板群組索引反轉 — agy
- 契約：群組風險計算改為先建 `groupId → hosts` 索引一次，總複雜度由 O(群組×主機) 降為 O(主機×每台群組數)。輸出不變（含排序）。
- 驗收：行為等值測試；（可選）Scale 壓測數字記錄。

### F3 KPI 聚合收斂 — Claude（實作時改向，經使用者同意本輪做）
- **推翻原契約「平行化」**：測試後端所有 DbContext 共用同一條 SQLite 連線（`EfSqliteFixture`），
  執行緒平行在測試裡無法安全驗證＝出貨一段測不到的並行程式碼。改為**合併查詢**收斂往返：
  本期＋前期 KPI 由兩次方法呼叫（各 3 個查詢，共 6 次往返＋兩次 setup）合併為一次
  `AggregateReportKpiPair`（單一 context：一次載入兩期 stats 列＋兩次 TotalIssues 彙總=3 次往返；
  受影響主機數改由 stats 列在記憶體算，省掉兩次 DISTINCT 查詢）。省 wall-clock 的目的相同、機制可測。
- 不能破壞：兩期各自的 KPI 五個數字與原本逐期呼叫完全相同（含 riskLevels/visibleSeverities/主機合併語意）。
- 驗收：等值測試——同一批資料下 Pair 結果與兩次單期呼叫逐欄位相同（含 yoy 非連續期間、主機合併去重）。

### F4 報表↔儀表板共用聚合快取 — Claude
- 契約修正：資料寫入版本戳不可靠（聚合橫跨 records＋handling 多表，沒有單一版本錨點；
  硬湊會做出「假失效」讓兩頁分岔——本專案踩過）。改為**短 TTL（暫定 30 秒）記憶體快取**：
  `IssueRankingBuilder.Build` 結果以 (from, to, 可見主機集合, totalHosts) 為鍵快取於 Singleton
  快取件（builder 本身是 Scoped，快取必須獨立成 Singleton 才跨請求生效）；時鐘可注入供測試。
  30 秒內處理狀態變更反映延遲屬可接受（頁面本身也非即時）；快取回傳副本，呼叫端不得改到共用物件。
- 驗收：計數測試——同鍵第二次 Build 不重打聚合、TTL 過期重算、不同可見範圍各自獨立（授權不得串味）。

### F5 非 All scope 的 SQL 端窄化 — Claude（範圍修正）
- 契約修正：日層級處理狀態由 `DayHandlingDerivation`（TopIssues＋handling＋案件＋嚴重度設定）
  在記憶體推導，**整段下推 SQL 不可行**。但核對 `FilterByScope` 發現：scope != All 時非 actionable
  （低風險）紀錄一律被丟棄——所以可以把 `RiskLevels IN (高,中)` 下推進 `QueryLightweight`
  （filter 已有現成欄位），本期與前期兩次載入都套。90 天路徑從整段載入變成只載高/中風險列
  （實務上低風險占絕大多數），行為等值。
- 索引核對：`lf_daily_records` 已有 `(HostId, RecordDate)`＋`RecordDate`、`lf_top_issues` 已有
  `(HostId, Date, Source, EventId)`＋`Date`，報表聚合條件已覆蓋，本輪不加索引。
- 驗收：等值測試（含低風險紀錄的資料集在非 All scope 下輸出不變）；下推斷言（spy/直測 store 過濾）。

## 作業G：說明書雙版本＋補章（P12）

### G1 雙版本結構 — agy
- 契約：`HelpContent` 支援每章可選的 AI 詳細版檔（暫定命名 `NN-*.ai.md`，manifest 對應欄位）；`HelpContentService` 載入雙內容，`GetManual()` 回簡明版（現行為不變），`HelpQaService.BuildUserPrompt` 改用詳細版、缺檔 fallback 簡明版。`HelpChapterScorer` 選章機制與 token 預算不變（預算以詳細版長度計）。
- 不能破壞：既有三支 Help 測試表達的契約（可調整測試以反映新結構，但簡明版對外形狀不變）。
- 驗收：新測試——有 ai 版章走 ai 版、無 ai 版 fallback、GetManual 不含 ai 版內容。

### G2 補四缺章初稿 — agy
- 契約：新增報表、稽核日誌、問題檔案、NetIQ 維護四章（簡明版＋AI 詳細版各一），manifest 補 keywords（含「報表」）；內容需對照實際頁面功能撰寫（agy 先讀對應 View/js 再寫），簡明版重點式、詳細版涵蓋每個控制項與欄位語意。
- 驗收：Claude 逐章對照畫面審校（此為人工驗收，缺漏由 Claude 補寫）；manifest/載入測試綠。

### G3 既有章修訂＋全冊校準 — Claude
- 契約：修 `10-scheduler.md` AI 進度文案（配合 B3 新行為）、`12-settings.md` 補 AI 進階參數與新統計區、`13-glossary.md` 補 vs基準/偏離倍數/收斂擴散欄位解讀；全冊過一次與本輪改動（三行版 vs基準、filterable 清單等）對照。
- 驗收：終檢文件輪逐條對照。

---

## 併回前終檢
照 plan-before-dev：兩個獨立 Explore 全 diff 審（程式碼/文件）；便宜檢查先做——跨段產出鏈 grep（B1 計量欄位→B2 前端消費點；A 改動→權限異動頁）、彙總/聚合規則改動 grep 同資料所有聚合入口（F4/F5 與儀表板卡片）、G 補章與實作逐句對照。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1 | Claude | 完成 | 4 條新測試＋全套 2502 綠 | 根因比規劃更前面一步（配對做在去重後子集）；契約改為「配對先於去重」＋四分支決策，已回寫規劃 |
| A2 | Claude | 撤銷 | — | 前提「重跑刷新 CreatedAt」經讀碼推翻；既有測試編碼刻意契約。只補註解說明理由 |
| B1 | Claude | 完成 | 8 條新測試 | usage 原本在反序列化時靜默丟棄；計量點放 HTTP 完成當下（含重試），快取命中不經此路徑 |
| B2 | agy(claude) | 完成 | 四個既有 API 簽章逐一 grep 對照 | 無臆造 API；trash 圖示確認在 sprite 內 |
| B3 | Claude | 完成 | 背壓進度測試（分母為件數非主機日） | 為測試把 AiQueueCapacity 開成 internal 可覆寫，正式路徑仍用預設 200 |
| C1~C6 | agy(claude) | 完成 | 逐項對照＋登入頁字級實測 | **抓到真錯誤**：表單字級被設成 body 的 .95rem，會全站縮小且與需求方向相反（實測 .form-control 生效值 1rem＝19.4px），已改回；另還原它移除的三個檔案 BOM |
| D | agy(claude) | 部分 | 待使用者實測確認 | 它只調右卡 header 高度（對齊的是「上緣」）；「底緣」是否對齊需畫面確認——該頁需登入，Claude 無法代為登入驗證 |
| E | agy(claude) | 完成 | 以真實模組在瀏覽器實測 | 關鍵風險點（篩掉的已勾選項目值不遺失）實測成立；未啟用 filterable 時 DOM 與舊行為一致 |
| F0 | Claude | 完成 | 計數測試先紅（13 次） | 先寫驗收再改，符合使用者要求 |
| F1/F2 | Claude | 完成 | 13→4 次並訂為契約上限；快取失效測試 | **實測推翻規劃前提**：主機清單早有版本探測快取，真正的重複工是別名索引重建 8~12 次 |
| F3 | Claude | 完成（改向） | 4 條等值測試 | 平行化不可測（測試共用單一 SQLite 連線）→ 改合併查詢，KPI 往返 6→3 |
| F4 | Claude | 完成 | 4 條快取契約測試 | 版本戳不可行（跨多表無單一錨點）→ 短 TTL 30 秒；鍵含可見範圍防串味 |
| F5 | Claude | 完成（範圍修正） | 低風險列不影響輸出的等值測試 | 整段下推不可行（狀態由記憶體推導）→ 改下推風險等級；索引核對後不加索引 |
| G1 | agy→Claude | 完成 | 3 條新測試 | agy 中途停在旁白且無法編譯（`entry.AiFile` 未定義），Claude 接手；並把 fallback 從載入端移進型別 |
| G2 | agy(gemini) | 完成 | manifest 合法性＋icon 存在＋四項可證偽宣稱抽查 | **本段起委派模型改為 gemini-3.7-flash-high**（agy Claude 組五小時額度用罄），之後不換回 |
| G3 | agy(gemini) | 完成 | 逐項核對 SystemSettings | **抓到五處內容錯誤**（JSON 重問預設 3→2、兩個 token 上限寫成 0、兩個懲罰值寫成 0）＋LaTeX 語法（手冊渲染器不支援，使用者會看到原始 `$\ge 2.0$`），均已修正；另補接線測試防「aiFile 打錯只會靜默 fallback」 |
