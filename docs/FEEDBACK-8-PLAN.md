# 回饋第八輪規劃（FEEDBACK-8-PLAN）

> 規劃日期：2026-08-04。狀態：**規劃中，尚未實作**。使用者實測回饋第七輪版本
> （dev@b374321）後回饋七項：(1) 等待動畫全站統一；(2) 排程執行進度條＋完成後刷新
> 執行總表；(3) 手動執行只跑 NetIQ 主機、本機顯示未執行；(4) 處理狀態加「觀察 N 天」；
> (5) 問題查詢「依問題」視角未過濾低風險；(6) 使用者名稱欄位固定顯示「顯示名稱(帳號)」；
> (7) SQLite「unable to delete/modify user-function due to active statements」錯誤。

| # | 項目 | 規模 | 性質 |
|---|---|---|---|
| 7 | SQLite user-function 併發錯誤 | 小 | bug 根因修復（先做——#3 可能是它的下游症狀） |
| 3 | 手動執行本機顯示未執行 | 中 | bug 診斷＋修復 |
| 2 | 排程執行進度條＋總表自動刷新 | 大 | 功能（含 NetiqPipelineService 輸出管道補洞） |
| 1 | 等待動畫全站統一 | 中 | UX 一致性 |
| 5 | 依問題視角低風險未過濾 | 小 | 語意修正 |
| 6 | 使用者名稱顯示格式統一 | 中 | UX 一致性（前後端皆有） |
| 4 | 處理狀態「觀察 N 天」 | 大 | 新功能（狀態機擴充） |

建議實作順序如上表（7 → 3 → 2 → 1 → 5 → 6 → 4）：#7 是最可能的共同根因，先修；
#3 依賴 #7 修復後的實測驗證；#2 與 #1 都動 runs.js／狀態卡，連著做；#4 動狀態機，
爆炸半徑最大，放最後獨立驗證。

---

## 7. SQLite Error 5：unable to delete/modify user-function due to active statements

### 現況與根因分析

錯誤發生在 `EfJsonBlobStore.Read()`（[EfJsonBlobStore.cs:39](../LogForesight.Core/Persistence/Sql/EfJsonBlobStore.cs)）
的查詢收尾：`RelationalDataReader.Dispose()` → `SqliteConnection.Close()` →
`SqliteConnection.Deactivate()` 時炸出。呼叫鏈是 Web 請求
`HostsController.GetVisibleHostGroups` → `VisibilityService` → `HostStore.GetAll()`。

這是 Microsoft.Data.Sqlite 的已知情境：

- Microsoft.Data.Sqlite 6.0 起**預設開啟連線池**（Pooling=True）。`Close()` 時實體連線
  回池前會走 `Deactivate()`——把 EF Core Sqlite provider 每次開連線時註冊的 user
  function（`ef_*`、`regexp` 等）從實體連線上移除。
- 若那顆實體連線上還有**未 finalize 的 statement**（併發情境下另一個查詢還掛在同一顆
  實體連線上），`sqlite3_create_function` 會回 SQLITE_BUSY(5)，就是這個錯誤訊息。
- 觸發時機與使用者情境吻合：**排程／立即執行在背景大量讀寫**（分析管線、BatchRun
  逐筆 log）＋ **Web 前景請求同時查詢**（主機頁 10 秒輪詢側欄）。單執行緒下永遠不會發生，
  所以測試（:memory: 專屬連線）與平時操作都碰不到。

程式碼這邊沒有 leaked reader：`EfJsonBlobStore`／`EfJsonLogStore`／`EfAnalysisRecordStore`
的查詢全部 `ToList()` 落地、context 全部 `using` 即拋——不是我們洩漏，是池化本身的
併發縫隙。正式環境 SqlServer 不受影響（此錯誤是 Sqlite provider 專屬）。

### 修法

1. **關閉 Sqlite 連線池**（單點改 [StorageFactory.cs](../LogForesight.Core/Persistence/StorageFactory.cs)
   `GetDbFactory` 的 Sqlite 分支）：改用 `SqliteConnectionStringBuilder` 組連線字串，
   使用者的 ConnectionString 沒有明寫 `Pooling` 時補上 `Pooling=False`（有明寫則尊重）。
   這是此錯誤的標準 workaround——不池化時 `Close()` 直接關閉實體連線，沒有「清乾淨
   還池」這一步。
   - 效能代價：每個查詢多一次開檔。webdata 查詢都是小查詢、Sqlite 定位是開發／小規模
     部署（docs/DB-PLAN.md），可接受；正式環境 SqlServer 完全不走這條路。
2. **`IsTransient` 補納此訊息**（[EfJsonBlobStore.cs:94](../LogForesight.Core/Persistence/Sql/EfJsonBlobStore.cs)
   加 `msg.Contains("user-function")`）：`Mutate` 的既有重試迴圈順便涵蓋，當作第二道保險。
   `Read()` 目前無重試，**不另加**——關池後根因已除，讀取端加重試是為不存在的情境
   增加複雜度。

### 影響範圍確認

- 測試不受影響：`EfSqliteFixture`／`SchemaUpgraderTests` 用自管的 `:memory:` 連線
  （`DataSource=:memory:` 開著不關），不經過 `StorageFactory.GetDbFactory`。
- `EnsureCreated`／`SchemaUpgrader` 走同一個 factory，關池後行為不變（只是每次
  開新連線）。
- 與 #3 的關聯見下節——同一個錯誤若發生在 `CreateBatchRunStore`／`StartRun`，
  症狀正是「執行監控本機未執行」。

---

## 3. 手動執行只執行 NetIQ 主機、本機顯示未執行

### 程式面事實（已逐條核對）

- 立即執行「全部主機」→ `ScheduleController.ResolveScope("all")` → `RunScope.Full`
  （[ScheduleController.cs:164](../LogForesight.Web/Controllers/Api/ScheduleController.cs)）。
- `AnalysisOrchestrator.RunAsync` 對 `Full` **一定會跑本機段**（`Scope != NetiqHosts`
  才進 `RunLocalAnalysisAsync`，[AnalysisOrchestrator.cs:355](../LogForesight.Core/Service/AnalysisOrchestrator.cs)）。
  也就是說「範圍＝全部主機」在程式路徑上不存在『跳過本機』的分支。
- 但本機段有**合法的靜默路徑**：近 14 天缺漏日為 0（昨晚排程已分析過昨天）時印
  「已有分析紀錄，跳過」直接 return——白天手動執行幾乎必然命中，觀感上就是
  「只有 NetIQ 主機在跑」（NetIQ 主機因回補窗口大多還有缺漏日）。
- 執行監控「本機」列的狀態**只看 BatchRun 紀錄**（`RunMonitorService.BuildCell`），
  而 BatchRun 列是整趟執行掛在本機主機名下登記的——只要 `BatchRunRecorder` 有成功
  `StartRun`，本機該日就會顯示成功（即使 DaysAnalyzed=0）。**顯示「未執行」代表
  這趟執行連 BatchRun 列都沒寫成**。

### 根因假說（依可能性排序）與驗證步驟

1. **BatchRun 登記失敗（最可能，與 #7 同根因）**：`CreateBatchRunStore` 建構時
   `ReadAllRunLines()` 讀全表、`StartRun` 寫入——任一步撞上 #7 的 SqliteException，
   `AnalysisOrchestrator` 與 `BatchRunRecorder` 都會**吞掉並繼續**（設計如此：執行監控
   不能成為批次故障點），結果就是：NetIQ 主機狀態照樣成功（它看的是分析紀錄，
   `NetiqStatus` 不依賴 BatchRun）、本機顯示未執行、分析其實有跑。與使用者觀察完全吻合。
   - 驗證：翻 `logs\logforesight.log` 找「執行紀錄儲存初始化失敗」「批次執行紀錄登記失敗」；
     或查 DB `lf_log_lines` 中 `log_key='batch_runs'` 在手動執行時段有無新列。
2. **本機缺漏日為 0 的觀感問題（次要，必然存在）**：即使 1 修好，白天手動執行時
   本機「跳過」的訊息只在執行明細 log 裡，狀態卡上一閃而過。

### 修法

- 根本修復＝#7（關池）。修完後請使用者重測一次立即執行，確認本機列恢復顯示。
- 補強（不論 1 是否為真因都做）：
  - `AnalysisOrchestrator` 的 batchRunStore 建立失敗、`BatchRunRecorder.StartRun` 失敗時，
    除了 NLog Warn，**也把警告透過 `console.WriteLine` 送進執行狀態**
    （`WebRunConsole` → `SchedulerRunState.LatestMessage`），使用者在狀態卡上看得到
    「執行紀錄登記失敗，執行監控本次將顯示不到這趟執行」，不再是純靜默。
  - 本機跳過訊息改得更明確：「本機近 N 天皆已有分析紀錄，本次無需回補（非未執行）」，
    並照常 `Milestone` 落地，執行詳情裡查得到。
- **不做**：執行監控對「範圍不含本機」的手動執行（網段／單機）另設狀態。BatchRun 列
  本來就是「整趟執行」的紀錄，語意上掛本機名下成功即可；為範圍語意再加一態會讓
  監控頁複雜化，實益低。

---

## 2. 排程執行進度條（手動＋自動）＋執行完刷新總表

### 現況缺口

1. 狀態卡只有 `LatestMessage`（最後一行輸出文字），無量化進度。
2. **NetIQ 管線的輸出根本進不了 Web**：`NetiqPipelineService` 整支用 `Console.WriteLine`
   （[NetiqPipelineService.cs:85 等約 15 處](../LogForesight.Core/Service/NetiqPipelineService.cs)），
   不是 `IRunConsole`——console 專案退場後這些輸出直接消失，排程跑到 NetIQ 段
   （整晚執行的大宗）時狀態卡訊息就凍結在本機段的最後一行。
3. 執行結束後前端不刷新：`runs.js` 的執行總表只在進頁／切天數時 `load()`，
   狀態輪詢（10 秒）只更新狀態卡。

### 修法

#### Core：輸出管道補洞＋進度回報

- **`NetiqPipelineService` 全面改走 `IRunConsole`**：建構子加 `IRunConsole console`
  參數，`Console.WriteLine` 逐處替換（文字內容不變）。這同時是 #1 的前置——
  NetIQ 段的活動從此對 Web 可見。測試端沿用既有的測試用 console stub（無則補一個
  `NullRunConsole`）。
- **新增進度回報介面**（放 AnalysisOrchestrator.cs 旁）：

  ```csharp
  public interface IRunProgress
  {
      /// phase：local|netiq；done/total：主機日粒度
      void Report(string phase, int done, int total);
  }
  ```

  `AnalysisOrchestrator.RunAsync` 加參數 `IRunProgress? progress = null`：
  - 本機段：`missingDates` 算出後 `Report("local", 0, N)`，逐日分析完 `Report("local", i, N)`。
  - NetIQ 段：把 progress 傳進 `NetiqPipelineService`。各 Sentinel 的 `RunServerAsync`
    算完 plans 後把「本 Sentinel 的主機日總數」累加進共享 total（`Interlocked`），
    每完成／跳過／失敗一個主機日累加 done。total 隨平行掃描逐步增大是可接受的
    （進度條百分比只會變準，不會倒退超過使用者預期——前端顯示「x / y 主機日」
    而不是只有百分比，數字自己會說話）。
- 取消與失敗不需要清進度——`EndRun` 時整組歸零（見下）。

#### Web：狀態擴充＋前端進度條＋完成刷新

- `SchedulerRunState` 加 `ProgressPhase`／`ProgressDone`／`ProgressTotal`
  （同一把 `_lock`；`EndRun` 歸零）。`SchedulerHostedService.TriggerRunAsync` 建立
  `IRunProgress` adapter 寫入 run state，傳給 orchestrator。
- `ScheduleStatusDto` 加 `progressPhase`／`progressDone`／`progressTotal`。
- `runs.js` `refreshScheduleStatus()`：
  - `isRunning && progressTotal > 0` → 狀態卡顯示 Bootstrap `.progress` 進度條＋
    「本機分析／NetIQ 機房分析　x / y 主機日」；`progressTotal == 0`（剛啟動、
    清理階段）→ 顯示不定進度（`.progress-bar-striped.progress-bar-animated` 滿版），
    配合 #1 的 spinner。
  - **偵測 `isRunning` 由 true → false 的變化**：呼叫 `load()` 刷新執行總表與異常彙總，
    並 toast「執行已結束」。手動與排程觸發共用同一條輪詢，天然都涵蓋。
  - 執行中把輪詢間隔從 10 秒縮到 3 秒（閒置維持 10 秒）——進度條要有「在動」的感覺；
    比照 MessageService 訊息輪詢的做法，狀態 API 極輕，負載無虞。

#### 不做

- SSE／WebSocket 即時推播：輪詢已足夠，為一根進度條引入長連線基礎設施不成比例。
- 逐 Sentinel 的分項進度：狀態卡是「一眼看進度」，明細本來就在執行詳情頁。

---

## 1. 等待動畫全站統一

### 現況盤點（動畫出口）

| 模式 | 現有實作 | 狀態 |
|---|---|---|
| 表格載入骨架列 | `ui.js renderLoading()`（§8.6-6） | ✅ 已統一，各清單頁皆用 |
| 按鈕忙碌 | `ui.js withBusy()`（spinner＋「⋯中」） | ✅ 已統一 |
| 區塊內文字型等待 | **各頁自寫純文字，無動畫** | ❌ 本輪要收斂的對象 |

純文字（無動畫）清單：

- [imports.js:342](../LogForesight.Web/wwwroot/js/pages/imports.js)：掃描匯入精靈
  `wizardNote('掃描中…')`——**就是使用者點名的「netiq 掃描」**，Sentinel 掃描可長達
  數十秒，只有靜態字。
- [netiq.js:311](../LogForesight.Web/wwwroot/js/pages/netiq.js)：probe「執行中（…）」
  輪詢文字。
- [host-detail.js:225](../LogForesight.Web/wwwroot/js/pages/host-detail.js)：「載入中…」。
- [records.js:714](../LogForesight.Web/wwwroot/js/pages/records.js)：「載入受影響主機…」。
- [rules.js:810](../LogForesight.Web/wwwroot/js/pages/rules.js)：摘要「載入中…」。
- runs.js 狀態卡「執行中」文字（併入 #2 的進度條改造）。

### 修法

- `ui.js` 新增單一出口：

  ```js
  /** 區塊內等待指示（§8.6-6 的行內版）：spinner＋文字，取代各頁自寫的純文字載入 */
  export function renderSpinner(container, text = '載入中…')
  ```

  產出 `<div class="text-muted small d-flex align-items-center gap-2">
  <span class="spinner-border spinner-border-sm"></span>{text}</div>`，
  `container.replaceChildren(...)` 語意與 `renderLoading` 一致。
- 上表各處逐一替換為 `renderSpinner(el, '掃描中…')` 等；netiq probe 執行中狀態
  在文字前掛 spinner（輪詢更新時只換文字節點，不重建 spinner，避免動畫重置閃爍）。
- `site.css` §8.6 區塊補一行註解，把「三種等待模式與各自出口」寫成規範，防止之後
  再長出第四種寫法。
- 順手體檢：確認 `renderLoading` 骨架列的 CSS 動畫（§8.6-6）在深色模式下可見。

### 不做

- 全頁遮罩／overlay：現有頁面都是區塊級載入，遮罩會把可操作的部份一起鎖住，倒退。

---

## 5. 問題查詢「依問題」視角：低風險未被篩選

### 根因（語意錯位，不是漏寫條件）

- 上方「風險層級」chips 篩的是**日風險等級**（記錄層），`SearchByIssue` 有確實
  套用（`BuildFilter` → `RecordQueryFilter.RiskLevels`）——高／中風險**日**的紀錄
  才進得來。
- 但依問題視角一列一個問題，顯示的「嚴重度」是**問題層級**的 High/Medium/Low
  （中文同樣顯示高／中／低，[format.js:48](../LogForesight.Web/wwwroot/js/core/format.js)）。
  高風險日裡本來就同時有低嚴重度的問題，於是預設高＋中的篩選下，清單照樣出現
  「低」——使用者視角就是「篩選沒生效」。

### 修法

`RecordQueryService.SearchByIssue`（[RecordQueryService.cs:259](../LogForesight.Web/Services/RecordQueryService.cs)）：
分組後**把同一組選擇再套用到問題嚴重度**——`request.RiskLevels`（高/中/低）映射為
`IssueSeverity`（High/Medium/Low），`MaxSeverity` 不在集合內的問題組整組濾除。

- 日風險過濾**維持不變**、再疊加嚴重度過濾（結果只會更窄）：
  「高＋中」＝高／中風險日裡的高／中嚴重度問題，與篩選 chips 的字面承諾一致。
- 未勾任何等級（不限）＝不過濾，行為不變。
- 「依主機」「依日期」視角不動——它們的高/中/低欄位是日風險計數，語意本來就對。
- 前端 `records.js` 不用改（chips 值已在送出的 `riskLevels` 裡）；補一個 chips 區
  的 tooltip：「依問題視角同時以此過濾問題嚴重度」。
- 測試：`SearchByIssue` 補「高風險日內的低嚴重度問題，預設高＋中篩選下不出現；
  勾低後出現」的案例。

---

## 6. 使用者名稱欄位固定顯示「顯示名稱(帳號)」

### 原則

- 單一格式化出口，前端 `format.js` 新增：

  ```js
  /** 使用者名稱的唯一顯示格式：顯示名稱(帳號)；缺顯示名稱時退回帳號 */
  export function formatUserName(displayName, account)
  ```

  規則：兩者皆有 → `顯示名稱(帳號)`（半形括號，依使用者指定格式）；只有帳號 →
  帳號；只有顯示名稱 → 顯示名稱。後端只在「DTO 目前只有帳號、查不到顯示名稱」
  的地方補資料，不在後端組字串（顯示格式是前端的事，後端給素材）。
- 既有的全形「（）」組合（如 hosts.js 負責人選單）一併改半形，全站一種寫法。

### 逐點盤點與改法

**前端只差格式（DTO 已有兩值）**：

- [hosts.js:550](../LogForesight.Web/wwwroot/js/pages/hosts.js) 負責人選單（改半形）。
- [records.js:655,763](../LogForesight.Web/wwwroot/js/pages/records.js) 依問題「處理人」欄、
  處理人篩選下拉——`IssueGroupHandlerDto` 需補 `account`。
- [handling-panel.js:193](../LogForesight.Web/wwwroot/js/pages/handling-panel.js) 處理人下拉。
- [handler-detail.js:41](../LogForesight.Web/wwwroot/js/pages/handler-detail.js) 工作頁標題
  ——DTO 需補 `account`。
- [layout.js:240](../LogForesight.Web/wwwroot/js/core/layout.js) 右上角目前使用者：
  空間有限，**維持只顯示顯示名稱、title 提示完整格式**（title 改用 `formatUserName`）。
- 使用者管理清單（users.js）：「帳號」「顯示名稱」本來就是兩欄，**不合併**——
  管理表格的欄位語意清楚，硬改反而重複。

**後端 DTO 只有帳號、需補顯示名稱**（以 `IUserStore` 依帳號查詢，查無則前端自然
退回只顯帳號；一律新增欄位、不改既有欄位語意，零破壞）：

- 處理歷程 `actorAccount`（[record-detail.js:439](../LogForesight.Web/wwwroot/js/pages/record-detail.js)、
  handling-panel.js 操作者）→ DTO 加 `actorDisplayName`。
- 稽核紀錄（audit.js 操作者欄）→ 同上。
- 排程設定 `updatedByAccount`（runs.js:455）、系統設定（settings.js:183）、
  NetIQ 維護（netiq.js:240）→ 各 DTO 加 `updatedByDisplayName`。
- 執行監控「誰跑的」`TriggerText` 的「手動（帳號）」（RunMonitorService.cs:230、
  ScheduleController.cs:226）：後端組字改為解析顯示名稱後帶入
  「手動（顯示名稱(帳號)）」——這兩處是後端組字串的既有出口，順著改，不搬到前端。
- 效能：以上頁面單頁筆數有限，`UserStore.GetAll()` 一次載入做字典即可，不逐筆查。

**刻意不動**：登入頁、LDAP 錯誤訊息（帳號輸入情境，還沒有顯示名稱可言）；
CSV 匯出維持現欄位（外部介面，加欄位另議）。

---

## 4. 處理狀態加「觀察 N 天」

### 需求解讀

處理人判斷「先看幾天再說」：觀察期間這台主機的這個問題**不再進入待辦／告警**
（儀表板待辦、逾期、問題清單的未處理計數），但**處理中的人隨時查得到、確認得了**；
觀察到期後若問題仍在發生，回到待辦。

### 設計：掛在問題層級狀態機＋案件上，讀取端推導到期

新增問題層級狀態 `observing`（觀察中）：

- **Model**（[IssueHandling.cs](../LogForesight.Core/Models/IssueHandling.cs)、
  [IssueCase.cs](../LogForesight.Core/Models/IssueCase.cs)）：
  - `IssueHandlingStatuses` 加 `Observing = "observing"`——**非結案類**（不進 `Closed`），
    加進 `All`。
  - `IssueHandling`／`IssueCase` 加 `DateTime? ObserveUntil`（僅 observing 時有值；
    落盤時比照 DueDate 的既有清空規則：狀態離開 observing 就清空）。
    UI 输入「觀察 N 天」→ 後端存 `今天+N`（絕對日期，重啟／跨日語意穩定）。
  - 舊資料反序列化 `ObserveUntil=null`、狀態值域是字串——零遷移。
- **到期語意＝讀取時推導，不跑背景作業**（與「缺列即未處理」同一哲學，避免再養
  一個排程）：單點定義

  ```csharp
  // IssueHandlingStatuses
  public static bool IsObservationActive(IssueHandling h, DateTime today) =>
      h.Status == Observing && h.ObserveUntil.HasValue && today.Date <= h.ObserveUntil.Value.Date;
  ```

  觀察**有效**→ 視同「已在處理、不吵」；觀察**到期**→ 視同 `in_progress` 且逾期
  （DueDate 語意借用 ObserveUntil）——自然回到既有的待辦／逾期通道，儀表板的
  逾期紅字就是「觀察到期、問題還在」的提示，不必新增通知機制。

- **推導規則改動（爆炸半徑核心，全部單點）**：
  - [DayHandlingDerivation.cs](../LogForesight.Web/Services/DayHandlingDerivation.cs)：
    `Derive` 的 `anyInProgress` 判斷納入 observing（有問題在觀察 → 當日不算 open）；
    `HasOverdueIssue` 納入「observing 且 ObserveUntil 已過」。
  - 儀表板待辦（DashboardService.GetTodo）：日狀態仍由 Derive 推導，不另改——
    觀察中的問題不再把日子拉成待辦，觀察到期則以逾期現身。
  - `HandlingStatuses.ExternalOf`：對外三態把 observing 收斂為 `in_progress`
    （對外部檢視而言就是「有人在管」）。
  - `RecordQueryService.BuildIssueGroup` 處理概況：observing 有效 → 計入「處理中」；
    到期 → 計入「未處理」。
- **批次掛接免改**：`IssueCaseCoordinator.AttachNewDay` 只掛進行中案件、掛接列帶
  案件現狀——案件狀態為 observing 時新產生的風險日自動繼承 observing，觀察期間
  新出現的日子不會變成新告警，正是要的行為。需確認的一點：案件展開／掛接時
  `ObserveUntil` 要一併帶到逐日列（比照 DueDate 的展開路徑）。
- **UI**：
  - 風險日詳情／處理面板（handling-panel.js）狀態下拉加「觀察中」，選取時顯示
    「觀察天數」數字欄（預設 7，1~90）；已在觀察的問題顯示「觀察至 yyyy-MM-dd
    （剩 n 天）」徽章；到期後徽章轉紅「觀察到期，問題仍在發生」。
  - 案件（跨日）操作同步支援——與 in_progress 的既有批次套用動線一致。
  - 處理人工作頁（handler-detail）加「觀察中」分組，處理中的人隨時確認——
    這就是「要讓處理中的使用者可以確認」的落點。
  - 問題查詢依問題視角的處理概況文字沿用三態，不加欄位。
- **歷程與稽核**：狀態變更走既有 `HandlingActions.IssueStatus` 歷程，Note 自動補
  「觀察至 yyyy-MM-dd」；案件層走 `CaseSync` 既有通道。
- **測試**：Derivation（觀察有效／到期兩態 × 日狀態推導）、ExternalOf、
  BuildIssueGroup 處理概況、Coordinator 掛接繼承 ObserveUntil、到期逾期判定。

### 與既有「告警抑制」（RuleSuppression）的邊界

抑制是**規則×主機**層級、影響批次分析的告警呈現與日風險拉抬；觀察是**問題×主機**
層級、只影響 Web 的待辦／處理狀態呈現，**不動分析、不動風險等級、不動報告**——
事件照常偵測與寫入（這正是「觀察」的意義：要看它還發不發生）。兩者職責不重疊，
規劃上不合併；文件（WEB-SPEC §處理狀態）補一段兩者的分工說明。

---

## 驗證計畫

1. `dotnet build`＋全測試綠（現況 1290 綠為基準）。
2. #7/#3：本機以 Sqlite 模式起 Web，開排程執行的同時連打主機頁（重現併發），
   確認不再出現 user-function 錯誤、執行監控本機列正常登記。
3. #2/#1：立即執行「全部主機」，狀態卡出現進度條並推進、NetIQ 段訊息可見、
   結束後總表自動刷新；掃描精靈／probe 出現 spinner。
4. #5：造高風險日含低嚴重度問題的資料，依問題視角預設不出現、勾「低」後出現。
5. #6：逐頁走查盤點清單中的每個顯示點。
6. #4：完整走一輪「標觀察 → 排程掛新日 → 儀表板不吵 → 到期 → 逾期現身」的劇本
   （測試用 ObserveUntil 直接塞過去日期模擬到期）。

## 待使用者確認的決策點

1. **#4 觀察天數**：預設 7 天、上限 90，是否合適？觀察到期的提醒方式採
   「以逾期紅字現身於儀表板／清單」（不另發通知），可以嗎？
2. **#6 括號**：依指定用半形 `(帳號)`；使用者管理頁維持「帳號／顯示名稱」兩欄不合併
   ——如要合併請告知。
3. **#5 語意**：「高＋中」解讀為「高／中風險日 × 高／中嚴重度問題」（雙重過濾、
   結果更窄），如期望改為「只看問題嚴重度、不管日風險」請告知。
