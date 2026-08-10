# 回饋第十四輪規劃（2026-08-10）

> **狀態（2026-08-10）：批次 A／B／C／E 已全部實作完成＋兩輪全案體檢，經 `feature/feedback-14`
> 併入 dev；1726 測試綠（基線 1697），0 警告 0 錯誤，各批次皆經瀏覽器端到端驗證。
> 批次 D（通知管道）暫緩待需求確認（見文末）。待使用者實測後併 master（依既有 git 分支慣例）。**
>
> 來源是使用者的 P0～P4 分級審查清單（9 項）＋「其他」UI 回饋 8 項。實作前先逐項核對主張與
> dev 實況的吻合度——**其中三處與程式碼實況不符，已推翻不做**（見「核對推翻項」一節，這是
> 本輪最重要的教訓：審查清單的技術主張必須先驗證再動工）。程式碼註解中的「回饋十四輪 X」
> 即指本文件的批次代號。

## 批次總表

| 批次 | 原清單 | 主題 | 主要落點 |
|---|---|---|---|
| A1 | P0 #1 | 總量基準零膨脹修復（非零日中位數） | TrendAnalyzer |
| A2 | P1 #2 | AiWorkItem.Logs 窄化移進型別＋訊息截斷 | LogAnalysisService／RiskyEventSelector／NetiqPipelineService |
| A3 | P1 #3 | HostPlan.GroupIds＋抑制清單 run 級快照 | NetiqPipelineService.HostPlan／LogAnalysisService |
| B1 | P3 #6 | 二分重查跨日記憶（IsHighVolume） | WebHost／HostStore／NetiqPipelineService |
| B2 | P4 #7 | WeeklyCheckup 補 AiAnalyzed 守衛 | WeeklyCheckupService.BuildPrompt |
| B3 | P4 #8+#9 | README 耗時拆兩段＋MaxTokens 2048＋併發配額註記 | README／AppSettings／SystemSettings |
| C1 | P3 #5 | Site／Group 抑制影響面預覽＋到期語意修正 | RuleAdminService／RulesController／rules.js／SuppressionFilter／AnalysisOrchestrator |
| E | 其他 1~8 | UI 六項（套用 ui-ux-pro-max） | site.css／Records.cshtml／dashboard.js／SchedulerRunState／Runs 頁／Imports.cshtml／ui.js |
| D | P2 #4 | 通知管道 | **暫緩**（無 SMTP 基礎設施，需求未談） |

## 核對推翻項（清單主張與程式碼實況不符）

1. **P4 #9「client pool 改 lazy 建立」前提不成立**：`SentinelClient.Auth.cs` 的
   `EnsureAuthenticatedAsync` 本來就是**首次 `SearchAsync` 才認證**，建構子只配置 HttpClient
   零網路成本；`DisposeAsync` 也只在 `_token != null` 時登出——「當天只有一個批次也付 4 次
   登入」這件事現在就不會發生。撤銷程式碼改動，只保留清單後半有價值的部分：README 的
   「放大平行度前先向 Sentinel 管理者確認併發配額」註記（3×4=12 併發 job／SAML session，
   撞配額的症狀是零星 `AddFailed` 貌似網路不穩）。
2. **P3 #5 的 M 值資料來源假設錯誤**：清單寫「資料在 `lf_top_issues`」，但該表**沒有
   `rule_id` 欄**（rule_id 在 `lf_risky_events`）。定案仍走 `lf_top_issues`（次數精準、
   不受風險暫存的每日上限與保留期限制），改以規則反解簽章條件比對——Windows 規則
   （SourcePattern＋EventIds）精準；Linux 規則因該表未存 EventKey 只能對到 program 層級，
   `ApproximateForLinux` 讓前端誠實標註「同來源程式合計」。
3. **P0 #1 順帶的「SlowTrendAnalyzer 中位數化」結構上不成立**：它比的是兩個 7 天窗口的
   **總量和**（近 7 天累計 vs 前 7 天累計），沒有「基準中位數」這個東西可以換；零膨脹也不會
   讓它退化（`priorTotal > 0` 守門已擋全零前期）。撤回順帶項，BACKLOG 條目已重新界定為
   「單日爆量墊高窗口總量」的獨立設計題。

## 定案決策（實作前與使用者確認）

| 決策點 | 定案 |
|---|---|
| A1 歷史無非零日時 | **維持門檻 10 照樣告警＋改文案**「近 N 日多數日無錯誤，今日出現 X 筆」——平常零錯誤的主機突然冒 10 筆本來就值得看一眼，只是不再印誤導的「基準 0 筆」 |
| A2 本機路徑一併窄化 | **接受，兩路徑統一**——本機深析報告的原始 log 池從全量縮成 risky 池（≤500 筆；報告端本來只用 20 筆、單筆最長 500 字），與 NetIQ 端已接受的行為一致 |
| C1 M 值資料來源 | **lf_top_issues**（見核對推翻項 2） |
| UI 批次 | **套用 ui-ux-pro-max**（實作批開場先跑 search.py） |

## 各批次關鍵決策

### A1 總量基準零膨脹修復（TrendAnalyzer）

整體錯誤量／稽核量突增的基準從「含零值的可靠歷史中位數」改為「**非零日**中位數」——
錯誤只在部分日子出現的主機，含零中位數落在 0，0×RisingFactor 恆為 0、倍率條件恆真，
規則退化成固定門檻「今日 ≥10 筆」且告警印出「基準 0 筆」。與簽章層 pastCounts（天然只收
非零日）語意對齊。歷史全零時維持門檻 10＋新文案（定案決策）。語意說明同步進
docs/DETECTION-SPEC.md「基準採中位數」一節。

### A2 AiWorkItem.Logs 窄化移進 BuildStatisticalRecordAsync

回饋十三輪 B1 的窄化原本由唯一呼叫端（NetiqPipelineService:554）事後補——記憶體安全的
不變量靠註解維持，第二個呼叫端出現時會靜默把 GB 級風險帶回來。移進型別建構本身
（`BuildStatisticalRecordAsync` 建 `AiWorkItem` 處，該處已持有 issues），呼叫端的
`with { Logs = ... }` 刪除。附帶：`SelectSourceEvents` 入列時把單筆訊息截到
`MaxMessageChars`（2000 字，與落庫版對齊；報告端最長只用 500 字，行為中性），
**回傳全新物件不就地修改**——原始 logs 後續還要餵 `ReplaceRiskyEvents`。

### A3 HostPlan.GroupIds＋抑制清單 run 級快照

核對發現比清單描述更嚴重：`HostStore.Get` = `JsonBlobCollection.Read()` **每次整份 blob
反序列化、無快取**——每主機日一次＝2000 台×14 天 28,000 次全量讀。`HostPlan` record 加
`GroupIds`（計畫階段每台主機 `Get` 一次，孤兒補跑共用同一值；B1 的 `IsHighVolume` 從同一次
Get 順便取得）。`ISuppressionStore.LoadAll` 同構問題：`RunAsync` 開始讀一次快照往下傳，
`LogAnalysisService` 加可選 `suppressionSnapshot` 參數（null＝本機路徑維持即時查）。
快照語意：run 內所有主機看同一份清單，執行中新增的抑制下次執行才生效——對批次規模反而
更一致。驗證：假 store 計數斷言 `Get` 次數＝主機數、`LoadAll` 次數＝1。

### B1 二分重查跨日記憶（IsHighVolume）

`WebHost` 加 `IsHighVolume`（JSON blob 存放，**免 schema migration**）。單台查詢仍截斷時
經 `IHostStore.SetHighVolume` 標記（已標記則不重複寫）；分批時旗標主機各自單獨成批，其餘照
`IpBatchSize` 分批——常態爆量主機不再每天從整批大小付 log₂ 深度的二分重查。**清除條件**（清單
未定，實作補上）：單獨批不再截斷**且** `Found < MaxResultsPerJob/2` 時自動清除——遲滯設計，
防臨界主機在「單獨成批／合批再二分」間日日震盪；門檻看 `Found`（Sentinel 回報總數，不受
截斷影響）而非取回筆數。旗標於計畫階段快照，run 中途變更下次執行生效。

### B2／B3 小項

- **B2**：`WeeklyCheckupService.BuildPrompt` 的每日摘要行補 `day.AiAnalyzed &&` 守衛（比照
  `AnalysisPromptBuilder` 同一守衛）——AiPending／統計模式的樣板 Summary（「（統計已完成，
  AI 分析排隊中）」等）不再被當成「當日結論」引給體檢 AI 延續敘事。
- **B3**：README AI 耗時拆兩段——主呼叫 5~15 秒／風險日深析另加 15~70 秒，上線 SOP 公式
  跟著拆（原「數十秒到一兩分鐘」是兩段疊加值，代入單次呼叫會把 AI 預算高估數倍）。
  `MaxTokens` 預設 1536→2048（`AppSettings`＋`SystemSettings` 兩處；**已存過系統設定的
  既有部署 DB 裡是 1536，需至設定頁手動改**）。README 補 Sentinel 併發配額註記（見核對
  推翻項 1）。

### C1 Site／Group 抑制影響面預覽（語意見 docs/RULES-SPEC.md、UI 見 WEB-SPEC §9.7）

- 新端點 `GET api/rules/{id}/suppression-preview?scope=&hostGroupId=`（Maintain；Host 範圍
  拒絕——單台主機不需要這道關卡）。N＝範圍內存活主機數；M＝`IIssueAggregateQuery` 對這些
  主機近 14 天的聚合，依規則比對條件加總（見核對推翻項 2）。
- rules.js 送出前彈確認框；預覽呼叫失敗不擋抑制流程（只是少了規模資訊）。
- Site 範圍「生效天數」空白時自動帶 30 天（可清空改回永久）。
- **到期通知語意修正**：同規則跨範圍並存（Host 到期、Site 永久仍生效）時，啟動階段的到期
  提示補「此規則仍受其他範圍的抑制設定生效中，解除這筆不會恢復告警」——
  `SuppressionFilter.StillSuppressedElsewhere`（純函數，用 ActiveForHost 反查、不會把到期
  項目自己誤判成仍生效）。

### E UI 六項（其他 1~8；#5 併入 B3；#3 盤點後無改動）

- **UI-1 側欄寬度**：`--lf-sidebar-width` 244px→15.25rem——2K 螢幕＋「大字級」偏好疊加後
  根字級衝到 20px 上限，品牌名稱需要的寬度超出固定 px 側欄、截成「LogForesig...」
  （瀏覽器實際重現：需 144px／可用 136px；修復後 197px／197px）。預設字級下畫面完全不變。
- **UI-2 依問題排最左**：Records 頁視角切換鈕序改「依問題｜明細｜依主機｜依日期」；
  儀表板「風險類型」卡下鑽補 `view=issue`——否則帶 categories 參數進頁會被 §10
  「帶參數預設回明細」規則接住，點類型卡看到的是逐筆明細而非依問題分組。
- **UI-3 icon 一致性**：盤點結果**無改動**——單一 Bootstrap Icons sprite 來源、28 個 symbol
  viewBox 全部 16×16、`.lf-icon` 1em 相對字級系統；JS 內少量 Unicode 字符（⚠✓✗↑↓→）屬
  文字流內的語意符號、不與 icon 系統競爭，保留。
- **UI-4 側欄品牌標記**：1.9rem→2.25rem（內部圖示 1.05→1.25rem 等比放大）——相對兩行品牌
  文字（約 3.1rem 高）視覺重量不足。
- **UI-6 主／子進度條分離**：`netiq-ai`／`netiq-backpressure`（AI 背景消化軌）與主進度
  （`netiq`，搜尋仍在推進）是**同時在跑**的兩件事，原本共用一組 `ProgressPhase/Done/Total`
  互相覆蓋，症狀是「進度卡住不動」。`SchedulerRunState` 拆主／子兩組欄位，排程頁畫兩條
  （子進度窄一階、只在有值時顯示）；單行讀取端由 `LatestActivity()` 單點決定取捨（見體檢紀錄）。
- **UI-7 匯入頁滿版**：`col-lg-6`→`col-12`——雙欄是三種 CSV 並存時期的殘留，§2a 退役後
  只剩一張卡片卻卡在半版寬。
- **UI-8 處理人 combobox**：`searchableUserSelect` 的搜尋框＋select 併成 `input-group`
  單行（搜尋框 40%、select 佔滿其餘）——搜尋框只是幫下拉篩選的輔助工具，原本各自成行
  看起來像兩個要分別填寫的欄位。

## 全案體檢紀錄

**第一輪（隨批次實作）**：批次 A～E 各自新增測試（合計 +29）；批次 A/C/E 經瀏覽器端到端
驗證（抑制預覽 Site／Group 全流程、側欄截字重現與修復、匯入頁滿版、combobox 單行、
子進度條版面）。體檢揪出 **UI-6 的跨批次回歸**：`RunActivityController`（一般使用者的
執行中告示）只讀主進度欄位，AI 消化階段畫面會停在搜尋階段的凍結數字——改子進度優先。

**第二輪（合併後全面重掃）**：
1. **同一回歸的第二個實例**：`HealthService.GetDetail` 的 `AnalysisPhase/Done/Total` 也只讀
   主進度（診斷頁在 AI 消化階段看起來像分析卡死）。同類 bug 出現兩次＝選擇邏輯不該散落各
   讀取端，收斂為 `SchedulerRunState.LatestActivity()`（子進度優先、鎖內一致快照）單點提供，
   兩個單行讀取端共用；補測試釘住。**教訓：拆共用狀態欄位時要全域搜尋所有讀取端。**
2. **B3 缺漏補上**：README 併發配額註記第一輪漏加（只寫在計畫裡），已補進「NetIQ 事件取數」
   平行度段落。
3. **文件收尾**：DETECTION-SPEC（總量基準非零日中位數）、RULES-SPEC（影響面預覽＋到期語意
   ＋Site 預設 30 天）、WEB-SPEC §9.7／§9.10（預覽端點＋主子進度條）、BACKLOG 兩則校準
   （SlowTrendAnalyzer 條目重新界定、`HasRecord` 批次化條目補記 A3 已收斂的同構問題）、
   本文件。

## 遞延項

- **批次 D（P2 #4 通知管道）**：全案無任何 SMTP 基礎設施，是從零的功能。動工前需與使用者
  確認：(1) 環境可用的 SMTP relay（主機／port／TLS／帳密）；(2) 收件人粒度（全域一組／
  依主機群組／依負責人）；(3) 每日摘要掛在排程結束處或獨立排程。分兩段落地的順序維持清單
  建議：先每日摘要 Email，再重大事件獨立高頻輪詢。已列 BACKLOG「回饋第十三輪遞延項」候選首位。
- **UI-4 的間距微調**：本輪只放大品牌標記；名稱／副標的字級與間距在放大後的視覺平衡可於
  使用者實測後再微調（無結構問題）。
