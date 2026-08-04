# 回饋第六輪規劃（FEEDBACK-6-PLAN）——排程 Web 化落地與 console 退役

> 規劃日期：2026-07-31。狀態：**Q1~Q6 已全數定案（見 §6 定案紀錄），可開工；
> 尚未實作**。五項回饋：(1) NetIQ 維護頁「詢問 AI 即時查詢」設定文字已與
> Phase 1 後的實際行為不符；(2) 沒有排程時間設定頁面、也沒有手動觸發執行的
> 位置（追加：左側選單也要有入口，見 §2 側欄小節）；(3) 已經不使用的專案與
> 檔案自解決方案移除；(4) 說明文字非必要常駐顯示的改為 icon 滑過顯示
> （第五輪 §6 的二次收斂）；(5) 全站用詞檢視（隨 Q3 答覆擴充：以官方用詞或
> 一般台灣 IT 用詞為主，檢視整個網站）。
>
> **(2) 與 (3) 不是新設計——正是 docs/WEB-SCHEDULER-PLAN.md 已定案的
> Phase 2~5**（九項問題 2026-07-31 全數拍板，設計細節齊備）。本文件的職責是：
> 確認該規劃對照目前程式碼仍然成立、界定本輪範圍與順序、補上 (1) 這個
> Phase 1 收尾遺漏與 (4)(5) 的範圍與基準。

| # | 項目 | 對應 | 規模 |
|---|------|------|------|
| 1 | NetIQ 頁 ChatLiveFetch 設定文字過時 | Phase 1 收尾遺漏，本文件 §1 | 極小 |
| 2 ✅ | 排程設定頁面＋手動觸發＋側欄入口「排程作業」 | WEB-SCHEDULER-PLAN **Phase 2＋3**，本文件 §2 | 大 |
| 3 | 移除不使用的專案與檔案 | WEB-SCHEDULER-PLAN **Phase 4＋5**，本文件 §3 | 大 |
| 4 | 說明文字二次收斂（非必要常駐 → icon） | 第五輪 §6 續作，本文件 §4 | 小中 |
| 5 | 全站用詞檢視（官方用詞／台灣 IT 慣用詞） | 本文件 §5 | 小中 |

---

## 1. NetIQ 頁「詢問 AI 即時查詢」設定文字過時

### 現況與成因

Phase 1（風險 log 暫存 DB）上線後，「詢問 AI」的取數順序已是**先查
`lf_risky_events` 暫存（毫秒級、本機與 NetIQ 主機皆有、不受開關影響），查無才
fallback Sentinel 即時查詢**（`RiskyEventLookupService`，見 WEB-SCHEDULER-PLAN
§2.2.4）。後端註解、WEB-SPEC §9.3／§9.9a 當時已同步改寫，但 **NetIQ 維護頁的
UI 文字漏了**（`Netiq.cshtml` 86~92 行）：

- checkbox 標籤仍寫「詢問 AI 詢問當下向 Sentinel 即時查詢現場事件（僅 NetIQ 主機）」
- form-text 仍寫「對話的第一輪會對該主機所屬 Sentinel 發一次即時查詢」

讀起來像即時查詢是唯一/主要路徑，與實際行為矛盾——這正是使用者回報的困惑點。

### 全站掃描結果（2026-07-31）

過時的**使用者可見文字只有 Netiq.cshtml 這一處**。其餘皆已正確：

- `chat-panel.js` 的「已取回現場事件 N 則納入分析」刻意不標注來源（Phase 1
  體檢時已定案：事件多數來自暫存，標注來源會說謊）——不改。
- `AiController`／`AiInsightService`／`RiskyEventLookupService` 註解均已寫明
  DB-first＋fallback——不改。
- WEB-SPEC §9.3／§9.9a 已於 Phase 1 更新（§9.9a 明寫「2026-07-31 起此即時查詢
  降為 fallback」）——不改。

### 做法（保留開關、改寫文字）

**開關本身仍有真實作用**（它把關的是「查無暫存時要不要打 Sentinel」這條
fallback 路徑——白天對 Sentinel 的額外查詢負載這個顧慮沒有消失），不拿掉；
只把標籤與說明改寫成反映兩層語意：

- 標籤改為：「詢問 AI 查無暫存資料時，向 Sentinel 即時查詢現場事件（僅 NetIQ 主機）」
- form-text 改為（要點）：對話第一輪**先查風險 log 暫存資料庫**（毫秒級、
  不打 Sentinel、本開關管不到）；只有暫存查無（超過保留天數、功能上線前分析的
  日子、不屬風險簽章）才用到本開關——開啟時 fallback 向該主機所屬 Sentinel
  發一次即時查詢。既有的節流說明（併發 1、快取 10 分鐘、僅 NetIQ 主機）保留。

### 影響確認

- 純文字改動，零行為變化、零 API 變化；`NetiqOptions.ChatLiveFetchEnabled`
  欄位與序列化不動。
- 不需要新測試；文件面 WEB-SPEC 已是對的，不用再動。
- 措辭基準（2026-07-31 定案 Q3）：以**官方用詞或一般台灣 IT 用詞**為主；
  並隨此答覆擴充出 §5 全站用詞檢視。

---

## 2. 排程設定＋手動觸發（＝WEB-SCHEDULER-PLAN Phase 2＋3）

### 需求對應

使用者要的兩件事在 WEB-SCHEDULER-PLAN 全部已設計定案：

| 使用者回報 | 既有定案 |
|---|---|
| 沒有排程時間設定的頁面 | §1.4.3 `ScheduleOptions`（多窗口，上限 4，跨午夜支援）＋§1.4.5 UI：**併入執行監控（Runs）頁**頂部「排程設定」卡（回饋第五輪 Q6 也再次確認過位置） |
| 沒有手動觸發執行的位置 | §1.4.4 手動觸發 API（run-preview／run／cancel／status；範圍 all/segment/host；≥50 台加強警示；不受時間窗限制）＋Runs 頁「立即執行」鈕＋主機詳情頁「指定主機更新」鈕 |

### 對照目前程式碼的有效性確認（2026-07-31 重新核過）

規劃距今雖只有一天，但中間過了回饋第五輪 12 個 commit，逐點確認無失效：

1. **搬遷清單仍準確**：`LogForesight/Service/` 現存 16 檔，其中
   `RuleBootstrapper` 已提前搬 Core（FEEDBACK-5 §10，規劃 §1.4.1 已註記），
   剩餘待搬 11 檔＋不搬的 CLI 類 4 檔（`SelfTestRunner`／`HostListCli`／
   `SuppressionCli`／`NetiqProbeCli`）＋`RuleImporter`（Phase 4 拆純函數）。
2. **Runs 頁結構未變**（61 行 cshtml＋403 行 runs.js）：頂部插「排程設定」卡
   無衝突；第五輪的表格排序等改動不影響此頁。
3. **測試基準線更新為 1214**（規劃寫 1163——第五輪淨增 51 個）；Phase 2
   「只搬不改」的閘門改以 1214 全綠為準。
4. **`Program.cs` 已 924 行**（規劃寫約 890——Phase 1 掛接風險事件寫入所致），
   抽 `AnalysisOrchestrator` 時多帶這一段，掛接語意不變。
5. **Web `Program.cs` 啟動區**第五輪加了規則庫 bootstrap——
   `SchedulerHostedService` 註冊與它同區共存，無衝突。
6. 第五輪的 modal 寬版（§7）與 help icon（§6）規範適用於本輪新 UI：
   「立即執行」確認對話框、排程卡的欄位說明照 WEB-SPEC §8.6 第 9~11 條做。

### 本輪範圍界定

- **Phase 2（服務搬遷，只搬不改）**：§1.4.1 清單 11 檔搬 Core＋§1.4.2 抽
  `AnalysisOrchestrator`／`IRunConsole`／ct 貫通本機迴圈／具名 Mutex 保留／
  `OrchestratorResult`。驗收：console 行為（含彩色輸出）逐字不變、1214 測試綠。
  獨立 commit 群、單獨可回退。
  **相依補充（2026-07-31 核對 csproj）**：三專案同為 `net8.0-windows`，Core 已有
  `System.Diagnostics.EventLog`／NLog／Polly——搬遷唯一要補的套件是
  `PermissionMonitorService` 用的 `System.IO.FileSystem.AccessControl`（自
  console csproj 移入 Core）；console 的 `System.DirectoryServices.AccountManagement`
  是 AD 驗證用、Web 已自有，不隨搬遷動。
- **Phase 3（排程引擎＋UI）**：§1.4.3 `ScheduleOptions` blob＋`ScheduleCalculator`
  純函數（多窗口/跨午夜/重疊驗證/漏跑補償全部可單測）、`SchedulerHostedService`、
  §1.4.4 四支 API、§1.4.5 Runs 頁排程卡＋主機詳情頁觸發鈕、`BatchRun.Trigger`
  欄位、§1.4.6 Web appsettings 新區段、§1.4.7 權限監控基準目錄說明、
  §1.4.8 部署文件（Event Log Readers）。
- **`Enabled` 預設 false**：部署本身零行為變化，schtasks 續用——使用者何時
  切換由 §3 的試點流程決定。

### Phase 2 實作結果與規劃差異（2026-07-31）

11 檔搬遷（`git mv`，namespace 不變、內容零改動）與 `AnalysisOrchestrator` 抽取
皆已完成，`dotnet build`/`dotnet test` 全綠（1214），並以 `git diff` 逐行核對
搬移前後的 Program.cs 主流程內容**逐字相同**（僅 `Console.WriteLine`→
`console.WriteLine`、`WithColor`→`console.WithColor`、區域變數改讀
`RetentionOptions` 記錄型別）確認零行為漂移。與規劃 §1.4.2 snippet 的差異：

1. **`RunRequest`／`OrchestratorResult` 取代單純的 `(RunScope, backfillOverride)`
   參數**：規劃的方法簽名只列 `RunScope scope, int? backfillOverride`，實作
   時發現還需要 `DebugDump`（決定要不要掛 `FilePromptDumper`）與 `Args`
   （`BatchRunRecorder` 的命令列欄位）才能重現現有行為，改用請求物件封裝，
   `BackfillOverride` 欄位先留著（結構就位、尚未接線，Phase 3 手動觸發時才
   會真正套用到 `NetiqPipelineService`）。
2. **具名 Mutex 未搬進 orchestrator**：規劃寫「具名 Mutex 保留在 orchestrator」，
   評估後改為**暫緩**——目前的 `using var instanceMutex` 是「整個 process
   持有到結束、靠行程結束時 OS 自動釋放」的一次性鎖，console 的 one-shot
   生命週期下這樣寫沒問題；但 Web 排程是長駐行程，同一個 `RunAsync` 會被
   呼叫很多次，每次都要**明確** `ReleaseMutex()`，而 `Mutex.ReleaseMutex()`
   要求呼叫執行緒與取得時同一條——`async/await` 底下延續執行緒不保證相同，
   貿然搬會埋下難重現的 `ApplicationException`。這屬於 Web 排程器自身的
   併發設計問題，不是「只搬不改」範圍內能安全處理的，留給 Phase 3 設計
   `SchedulerHostedService` 時一併解決（可能改用單一背景執行緒跑排程迴圈，
   或改用不受執行緒親和性限制的鎖原語）。console 端的既有 Mutex 保護不變。
3. **`IRunConsole` 用兩個通用原語而非逐一對應的語意方法**：規劃提到
   `IRunConsole` 輸出抽象時舉例「Info/Warn/Alert/Section 這類語意方法」；
   實作改用 `WriteLine(string)`／`WithColor(ConsoleColor, Action)` 兩個更底層
   的原語，`AnalysisOrchestrator` 內文保留與原本**逐字相同**的格式化邏輯
   （框線字元、色彩區塊），只是呼叫對象從 `Console.*` 換成 `console.*`。
   改用語意方法需要把每處輸出重新分類命名，風險是在分類過程中不小心改到
   格式；兩個原語版本零風險，且 Web adapter（Phase 3）一樣能把
   `WriteLine`→NLog info、`WithColor` 的顏色映射成訊息前綴或忽略。
4. **`RunScope.LocalOnly`／`NetiqHosts` 的篩選語意是本次新設計，非既有行為**：
   規劃只定義了列舉存在（給 Phase 3 手動觸發用），沒有規範細節。實作採
   `LocalOnly`＝跳過整個 NetIQ 段、`NetiqHosts([ids])`＝跳過本機逐日分析段
   且把 `HostListResult` 篩到只剩指定 HostId（不產生「被排除」警告，因為
   那些主機本來就沒被要求這次更新）；`Full`（唯一現在會被呼叫到的分支）
   行為與原本完全相同。權限檢查／清理／體檢三個維護性步驟在三種範圍下都
   照跑不跳過——手動更新單一主機時仍一併做本來就會做的維護工作，避免
   「小範圍手動觸發」與「排程完整執行」的維護頻率產生分歧。這組語意將在
   Phase 3 設計手動觸發 API 時對照真正的 UI 需求覆核，必要時調整。
5. **設定載入（`AppSettings.Load`／`SystemSettings` DB 覆寫合併）留在
   Program.cs，未進 orchestrator**：規劃 §1.4.2「每次執行重建服務」是要求
   Web 排程每次觸發都重讀設定，但沒規定「重讀」的程式碼要放在 orchestrator
   內部或呼叫端。實作選擇留在呼叫端（`RunAsync` 收 `AppSettings settings,
   string dataRoot, RetentionOptions retention` 三個已解析好的參數）——
   console 本來就得在 orchestrator 呼叫**之前**载入設定（CLI 分派段
   `--import-rules`／`--netiq-probe` 也需要），沒有理由在 orchestrator 內部
   再重複一次；Phase 3 的 `SchedulerHostedService` 只要在每次觸發前呼叫同一段
   設定載入邏輯（屆時視情況抽成共用靜態方法）即可達成「重新讀取」，不需要
   把設定解析寫死進 orchestrator。

### 側欄入口與權限（2026-07-31 追加：回應「左側選單沒有排程入口」）

使用者指出左側選單沒有排程設定相關入口。盤點後發現這不只是命名問題，
還牽出一個**權限缺口**：

**現況**：側欄「系統」區的「執行監控」（`/runs`）掛 `DevMonitor` 能力
（dev＋admin 持有）；排程設定與手動觸發規劃為 `Maintain`（admin＋serverAdmin
持有）。兩個集合交集只有 admin：

- dev：進得了執行監控頁，但**不該**能改排程／觸發執行（無 Maintain）✓ 語意正確
- admin：兩者皆有 ✓ 無問題
- **serverAdmin：有 Maintain 卻進不了 `/runs`**——排程設定放在執行監控頁的話，
  救援帳號在全新環境完成初始設定時搆不到排程，且側欄完全看不到入口 ✗

**方案比較**：

| 方案 | 作法 | 問題 |
|---|---|---|
| **A（採用）** | 側欄項目改名「執行監控」→**「排程作業」**（名稱為 2026-07-31 定案 Q5）；`/runs` 頁面權限放寬為 **DevMonitor 或 Maintain（任一）**；頁內排程卡依能力分層顯示 | 無——見下方細節 |
| B | 系統管理區另加「排程設定」項連到 `/runs` | 兩個側欄入口指同一頁，active 高亮同時亮兩條，混淆 |
| C | 獨立 `/admin/schedule` 頁 | 推翻第五輪 Q6 定案（排程 UI 維持執行監控頁）；且把「設定排程」與「看它有沒有跑」拆兩頁，違反同一視野的原設計 |

**方案 A 細節**：

1. **側欄**：「執行監控」改名**「排程作業」**（icon `activity` 不變，
   位置仍在「系統」區；頁面標題與麵包屑同步改）——名稱直接回答
   「排程設定在哪」，「作業」同時涵蓋排程設定、手動觸發與執行紀錄三件事。
   `layout.js` 的 nav `requires` 由單一能力字串擴為**支援陣列（任一命中即顯示）**，
   此項掛 `['DevMonitor', 'Maintain']`。
2. **頁面權限**：`PagesController.Runs()` 的 `[Permission]` 同步放寬——
   `PermissionAttribute` 擴為接受 params 多能力（任一持有即過，attribute 建構子
   簽名相容既有單能力用法，其餘頁面零改動）。
3. **頁內能力分層**（前端依 `hasCapability` 顯示＋後端各 API 自行把關，雙層）：
   - 排程卡的**唯讀狀態**（下次觸發時刻、目前執行中/閒置、觸發來源）：
     這本來就是「監控」資訊，dev 看得到；
   - 排程卡的**編輯欄位**（Enabled 開關、窗口編輯、DebugDump）與
     **立即執行／停止**鈕：僅 Maintain 顯示；
   - 執行總表／異常彙總等既有監控區塊：維持現狀（頁面能進就看得到——
     serverAdmin 因此多看到執行紀錄，屬監控資訊非業務資料，可接受且對
     救援診斷有益）。
4. **API 權限微調**（相對 WEB-SCHEDULER-PLAN §1.4.4 的兩處刻意偏離）：
   - `GET /api/admin/schedule/status` 由 Maintain 放寬為 **DevMonitor 或
     Maintain**——dev 的排程狀態列要有資料可渲染；`run-preview`／`run`／
     `cancel` 與設定讀寫維持 **Maintain**。
   - 既有 Runs 資料 API（`RunsController`，目前類別層級 `DevMonitor`）同步
     放寬為**任一**——否則 serverAdmin 進得了頁面卻拿不到執行總表資料，
     等於只放寬半套。
5. **文件**：WEB-SPEC §7.1 能力表、§9.9（Runs 頁）與側欄清單同步更新；
   WEB-SCHEDULER-PLAN §1.4.5 補註記指向本節。

**取捨說明**：放寬 `/runs` 給 Maintain 等於讓 serverAdmin 多看到執行監控資料。
serverAdmin 的設計原則是「依用途給權」（救援＋初始設定）——排程設定屬初始
設定的一部分，執行紀錄是確認排程活著的必要回饋，兩者都在用途內；業務資料
（儀表板/問題查詢/報表）仍然一項都看不到，最小授權的實質未被稀釋。

### Phase 3 全部完成（2026-07-31）

`ScheduleOptions`／`ScheduleCalculator`（39 測試，格式/重疊/跨午夜/漏跑補償
全涵蓋）／`NamedMutexGate`（5 測試，含跨執行緒續行與逾時競爭）／
`SchedulerHostedService`／6 支 API（options GET/PUT、status、run-preview、
run、cancel）／側欄改名與權限放寬／排程作業頁排程卡／主機詳情頁「指定主機
更新」鈕，全部完成並提交（7 個 commit）。1258 測試綠（1214+44）。

**與規劃的差異**（詳見各 commit message，此處彙總）：

1. **API 從「4 支」變成「6 支」**：規劃 §1.4.4 只列 run-preview／run／cancel／
   status 四支；實作時發現排程卡需要讀寫 `ScheduleOptions`（Enabled／
   Windows／DebugDump），沒有對應端點就沒東西可存/讀，補上
   `GET/PUT api/admin/schedule/options` 兩支，行為單純（CRUD＋驗證），
   不影響原四支的設計。
2. **`RunPreviewDto` 精簡為單一 `HostCount`**：規劃提到「排除統計」，實作
   評估後判斷「這個範圍會跑幾台」才是使用者按下「立即執行」前真正要的
   資訊，詳細排除清單（待歸屬/衝突/停用）已存在於主機頁，不重複呈現。
3. **`host` 範圍在後端加了 Pollable 檢查**（規劃未提，實測時發現）：若不查
   一台已停用/待歸屬的 NetIQ 主機，`run-preview` 會誠實顯示「1 台」但實際
   觸發時 orchestrator 內部會把它濾掉、靜默變 0 台——這正是全案反覆強調的
   「不靜默少幾台」的一個新違例，加驗證擋下並給出具體原因。
4. **`BackfillOverride` 的實際套用**（Phase 2 記錄的待辦）：`RunRequest` 早在
   Phase 2 就定義了這個欄位但沒接線，Phase 3 建 API 時順手把它套進
   `NetiqPipelineService` 建構前的 `netiqOptions.BackfillDays` 覆寫。
5. **具名 Mutex 的 Web 安全包裝**（Phase 2 明確記錄留給 Phase 3）：`NamedMutexGate`
   把 acquire/release 整段包進單一 `Task.Run` 委派解決執行緒親和性問題，
   已如期在本輪完成，見獨立 commit 的完整說明。
6. **瀏覽器實測中途換測試帳號**：一開始用 serverAdmin（`svc-lfadmin`）測試
   主機詳情頁的「指定主機更新」鈕，反覆 404 才想起 serverAdmin 依權限模型
   本就沒有業務資料檢視能力——換成 admin 群組的一般帳號後恢復正常。
   這不是本輪程式碼的缺陷，是測試步驟一開始選錯帳號，記錄下來避免下次
   重蹈覆轍。

---

## 3. 移除不使用的專案與檔案（＝WEB-SCHEDULER-PLAN Phase 4＋5）

### 「不使用」的盤點結果（2026-07-31 全案掃描）

先講結論：**現在就真正無用的檔案，只有功能已被 Web 完全涵蓋的兩個 CLI 類**；
console 專案整體「看起來不使用」但**還不能刪**——它目前仍是每晚分析的唯一
執行載具，移除的前置條件正是 §2 做完。逐類盤點：

| 候選 | 判定 | 依據 |
|---|---|---|
| `SuppressionCli.cs`／`HostListCli.cs` | **可立即刪**（見下方建議） | 功能已被 Web 規則頁「告警抑制」分頁與主機頁完全涵蓋，WEB-SCHEDULER-PLAN 定案 #9 本就判「直接刪」；留著是第二套會漂移的入口 |
| console 專案（`LogForesight`） | ✅ **已刪（回饋第七輪，2026-08-04）** | 原定 Phase 5 才能刪（排程/分析還在它身上，移除閘門＝Web 排程試點 ≥5 晚驗證通過，定案 #5）；本輪由使用者知情豁免該閘門提前執行，見 docs/HISTORY.md「決策 20 修訂」與 docs/FEEDBACK-7-PLAN.md |
| `SelfTestRunner.cs` | ✅ **已刪** | 定案 #9：selftest 接受退役；關聯 ID 對齊檢查改由 `CorrelationAnalyzerRuleAlignmentTests`（自動化測試）接手，覆蓋不打折 |
| `NetiqProbeCli.cs` | ✅ **已刪** | probe 已 Web 化（NetIQ 維護頁「診斷」分頁），Core `NetiqProbeRunner` 邏輯不受影響 |
| `RuleImporter.cs` | ✅ **已刪** | 規則升級 SOP 已完全 Web 化（規則頁橫幅＋預覽/套用對話框），直接刪、未搬 Core（Web `RuleAdminService` 已用 `RuleImportPlanner` 涵蓋同等功能） |
| 19 個頁面 JS／wwwroot 靜態資源 | 無孤兒 | 逐檔核對：每個 pages/*.js 都被 cshtml 或其他模組引用；css/img/lib 皆在用 |
| docs/ 11 份文件 | 全數保留 | 逐份核對引用數（RULES-PLAN 51 處、DB-PLAN 22 處、NETIQ-API-PLAN 31 處、LINUX-RULES-PLAN 34 處、FEEDBACK-3/4 各 34/41 處——程式碼註解大量指回這些文件，是活的參照不是遺物） |
| 兩份 `appsettings.json`（批次/Web） | ✅ 批次那份已隨 console 專案刪除 | §1.4.6 定案：隨部署的基礎設施參數，維持檔案配置 |

### `SuppressionCli`／`HostListCli` 提前到本輪首段刪除（Q1 已定案：提前刪）

定案 #9 已判這兩個 CLI「直接刪」，原排程是隨 Phase 5 一起；提前刪的理由：

**引用覆核（2026-07-31）**：兩類別的程式引用只有 `Program.cs` 的 CLI 分派段，
**零測試相依**；`StoreHostListProvider`（1.4.4 run-preview 要複用的清單語意）
在 `HostListProviders.cs`，是另一個檔案，不受影響。文件引用僅
HISTORY.md／RULES-PLAN.md 的歷史紀錄段（紀錄不回溯改寫，維持原文）；
README 的 `--suppress`／`--host-list` 兩節屬現行操作指引，同 commit 刪除
並改指 Web 對應頁面。

- 使用者本輪明確要求移除不使用的東西，這兩個是**現在就能安全刪**的全部。
- Phase 2 的「console 行為逐字不變」驗收指的是**分析管線輸出**；棄用的 CLI
  參數移除是已定案的功能退場，不在該不變式範圍（README 同步刪
  `--suppress`／`--host-list` 兩節即可，SOP 指向 Web 頁面）。
- 風險：若有人在伺服器上仍用這兩個指令操作——但 Web 抑制分頁/主機頁
  2026-07 起就是主要入口，README 也早標注 CLI 為「沒有 Web 時」的備援。

### Phase 4／5 範圍（依 WEB-SCHEDULER-PLAN 原設計，無變更）

- **Phase 4（CLI 職責搬 Web）**：§1.4.9 規則升級（`RuleImportPlanner` 拆
  Core＋Web 規則頁橫幅/預覽/套用＋CLI 對等測試）、§1.4.10 AI 診斷傾印開關
  （`ScheduleOptions.DebugDump`＋Runs 頁警示徽章）、§1.4.11 probe 診斷分頁
  （`NetiqProbeRunner` 拆 Core＋NetIQ 維護頁「診斷」分頁，背景執行＋輪詢，
  獨立 probe gate 併發 1）。
- **Phase 5（退場）**：§1.5 五步驟——部署（Enabled=false 零變化）→ 開
  Enabled＋停用 schtasks（熱回退窗口）→ **連續 ≥5 晚驗證（使用者實際環境，
  時程由使用者控制）** → 刪 schtasks＋自方案移除 console 專案＋清 Core 內
  只被 console 用到的殘留＋部署面移除 exe 與批次 appsettings → 文件收尾
  （README 架構圖/使用方式/selftest/部署/規則升級 SOP、HISTORY 決策 20 修訂）。
  含冷回退演練（revert→build→跑一晚）確認回退路徑真實可走。

### 影響確認

- **§2 與 §3 有硬依賴**：console 移除（3）必須在排程 Web 化（2）試點通過之後；
  本輪能交付到「Phase 4 完成＋Phase 5 就緒」，最後的移除 commit 卡在使用者的
  ≥5 晚驗證之後——這不是拖延，是定案 #5 的閘門。
- 移除後回退只剩冷回退（git revert＋重建部署），已是定案 #9 的知情選擇；
  緩解措施（試點驗證、冷回退演練、分析冪等自癒）照 §1.5 原設計。

### Phase 4 全部完成（2026-07-31）

三個子項（§1.4.9 規則升級／§1.4.10 AI 診斷傾印開關／§1.4.11 probe 診斷分頁）
全部完成，**Phase 4 結案，Phase 5 就緒**（實際移除仍卡使用者 ≥5 晚驗證，
不在本輪範圍）。1258 測試綠（較 Phase 3 結束時持平，本輪未新增測試——
理由見下方「與規劃的差異」第 3 點）。

1. **§1.4.9 規則升級**：`RuleImportPlanner`（`BuildPlan`／`Apply`）拆到 Core，
   console `RuleImporter.cs` 改為薄殼；Web 規則頁加 seed 版本橫幅、預覽/套用
   對話框、三支 API（`import-status`／`import-preview`／`import-apply`）。
   `RuleImporterTests` 10 處呼叫改指新類別，斷言不變。
2. **§1.4.10 AI 診斷傾印開關**：實際上在 Phase 3 建排程卡（`ScheduleOptions.DebugDump`
   ＋徽章）時已一併做完，本輪只需確認並補一個一致性修正——`ScheduleController.Run()`
   手動觸發原本沒有套用 `DebugDump`，只有 `SchedulerHostedService.TickAsync`
   （排程觸發）有套，造成手動「立即執行」／「指定主機更新」會靜默忽略這個
   開關。修正把套用點統一移進 `SchedulerHostedService.TriggerRunAsync` 本身，
   讓排程與手動兩條觸發路徑都以當下的排程設定為準（獨立 commit
   `fix(web): AI 診斷傾印開關統一以排程設定為準，不受觸發來源影響`）。
3. **§1.4.11 probe 診斷分頁**：`NetiqProbeCli.cs` 原本的 13 個驗證步驟＋輸出格式
   逐字搬進 Core 的 `NetiqProbeRunner`（console 與 Web 共用同一份，任何一邊都
   不再各自維護查詢邏輯）；`SentinelConnectionFactory` 順帶從 `internal` 改
   `public`（本就是同一份解密邏輯，Web 沒理由再寫一份）。NetIQ 維護頁改成
   「設定」／「診斷」兩分頁（`bindTabs`，沿用 Settings 頁既有模式）；「診斷」
   分頁選一台 Sentinel＋選填 Windows／Linux 樣本 IP，觸發後背景執行、
   2 秒輪詢、輸出即時累積到唯讀 textarea（「即時 tail」效果）＋複製鈕。
   `NetiqProbeRunState` 是獨立的併發 1 gate，刻意與 `SchedulerRunState`
   分開——不被夜間分析互斥擋住。瀏覽器實測：對假 Sentinel（`sentinel.test:8443`）
   觸發診斷，13 個步驟逐一失敗隔離、正確跑完並顯示「✗ 執行中發生錯誤」，
   稽核紀錄正確寫入且分頁切換不影響背景執行。

**與規劃的差異**：

1. **稽核動作代碼中文對照表有既有缺口，本輪一併補齊**：核對
   `AuditQueryService.ActionNames` 時發現 Phase 3 新增的
   `ScheduleOptionsUpdate`／`ScheduleManualRun`／`ScheduleManualCancel` 與
   §1.4.9 新增的 `RuleSeedImport` 都沒有補進這張表（稽核頁會顯示原始代碼
   字串而非中文），與本輪新增的 `NetiqProbeRun` 一起補上，不是新 bug、是
   撿到既有遺漏順手修。
2. **probe 稽核寫在 Controller 而非 Service**：`AdminController` 原本的慣例是
   稽核寫在各自的 Scoped Service 內（`SentinelAdminService`／`NetiqOptionsService`）；
   `NetiqProbeService` 因為要背景執行（`Task.Run`）必須是 Singleton，
   而 `IAuditService` 是 Scoped、無法注入 Singleton，所以稽核呼叫留在
   Controller——與 Phase 3 `ScheduleController.Run()` 手動觸發排程分析的
   既有作法一致，不是本輪新發明的例外。
3. **probe 沒有新增獨立單元測試**：規劃 §1.4.11 原文「既有 stub HTTP 單元測試
   沿用」——`NetiqProbeRunner` 的查詢邏輯完全複用已受測的 `SentinelClient`
   （`SentinelEventMapperTests`／`SentinelFieldMapTests` 等既有 stub HTTP
   測試涵蓋），原 `NetiqProbeCli` 本身也從未有專屬單元測試（純輸出格式化，
   靠瀏覽器/console 實測核對），拆分後維持同樣的驗證方式，未新增測試檔。

---

## 4. 說明文字二次收斂（非必要常駐 → icon）

### 需求

第五輪 §6 已做過一輪收斂（約 50 處逐一分類，31 處收進 icon），當時的保留標準
偏寬（「影響資料正確性或不可逆的警告」以外，連「陳述驗證限制」「營運調校
指引」也保留常駐）。使用者本輪要求**更嚴**：非必要常駐顯示的，一律改為 icon
滑過才顯示。

### 現存量與二次分類基準（2026-07-31 盤點）

目前殘留常駐 `form-text` **23 處**（Settings 7、Hosts 5、Netiq 4、Rules 4、
Groups／PermissionChanges／Users 各 1）＋頁首 `.lf-hint` 5 處（Imports 2、
Rules 2、PermissionChanges 1）。二次收斂的分類基準（比第五輪嚴）：

1. **僅保留**「不看見就可能立刻造成損失」的警告——不可逆操作
   （「建立後不可修改」）、資料可見性後果（「未分組只有 admin 看得到」）、
   送出會被擋的硬性限制中**與當前輸入直接相關**者。
2. **收進 icon**：驗證限制的完整說明（送出被擋時 toast 會再講一次，欄位旁
   不必常駐）、營運調校指引（Netiq 頁的回補天數/平行度長段建議）、格式範例、
   資料來源說明——第五輪因「陳述硬性限制」「調校警告」而保留的，這輪多數
   降為 icon。
3. `.lf-hint` 頁首說明維持既有「一行式＋popover 雙層」不動（第五輪原則 3，
   本身已是收斂形態）；`Hosts.cshtml` 批次貼上的格式說明含 `<code>` 排版、
   popover `html:false` 保不住，維持常駐（技術限制，第五輪已註記）。

逐處對照表沿用第五輪模式：**實作時整理、附於本節供驗收**（第五輪 Q5 同款）。

### 本輪新 UI 一體適用

§2／§3 新增的介面（排程卡欄位、立即執行對話框、probe 診斷分頁、規則升級
預覽）自始依 WEB-SPEC §8.6 第 10 條的 icon 慣例設計，不產生新的常駐說明債；
唯排程卡的「AI 診斷傾印開啟中」警示徽章與 ≥50 台紅字警示屬狀態警告非欄位
說明，常駐顯示（那正是要打擾使用者的東西）。

### 影響確認

- 純前端 markup 調整＋既有 `helpIcon`／popover 機制，零行為、零 API 變化。
- 與 §1 的 NetIQ 頁文字改寫同檔（`Netiq.cshtml`），實作時合併處理避免兩次
  相鄰 commit 碰同一段。

### 實作結果與驗收對照（2026-07-31）

逐頁覆核 23 處常駐 `form-text`，5 處**收進 icon**（皆為描述性說明或已被
toast 涵蓋的驗證限制），其餘維持常駐（實際比對後多數仍落在「不可逆／
資料可見性後果／送出被擋的硬性限制」三類，比第五輪的估計更靠近保留側，
差異記錄如下）：

| 檔案／欄位 | 處置 | 判準 |
|---|---|---|
| Settings「日風險等級顯示」必要項說明 | **收** | 純 UI 狀態描述（為何鎖住），非三類任一 |
| Settings AI「API 位址」留空停用說明 | **收** | 重新評估：非不可逆（隨時可補填）、非資料可見性、非送出阻擋，屬功能影響描述 |
| Settings「歷史資料保留天數」 | **收** | 主要內容為驗證限制（不可小於回補天數），toast 已涵蓋 |
| Settings「風險 log 暫存保留天數」 | **收** | 同上（不可大於保留天數） |
| Netiq「每次執行回補天數」／「同時處理幾台 Sentinel」 | **收**（隨 §1 一併處理） | 營運調校指引，符合本輪明列的收斂類型 |
| Settings「AI API 金鑰」動態 hint | **維持常駐** | 差異：原評估可收，覆核時發現該欄由 `settings.js renderAiFields()` 依 `aiHasApiKey` 動態改寫文字（「已設定金鑰；留空儲存＝沿用既有金鑰」），屬「留空是否會清空既有金鑰」的誤解可致不可逆損失，且是動態狀態非靜態文字，改 icon 需額外接線，效益與成本不成比例，判斯留 |
| Settings「AD 驗證啟用」說明 | 維持常駐 | 立即影響全體登入行為（不可逆／高風險），第五輪既定保留 |
| Settings「日風險等級顯示」段落說明、Hosts 主機名稱／主機群組／負責人 hint、Netiq Sentinel 密碼「留空＝不變更」、Rules 4 處、Groups 角色鎖定說明、PermissionChanges 必填提示、Users 帳號說明 | 維持常駐 | 逐一核對仍屬不可逆操作／資料可見性後果／送出阻擋三類，或文字本身已是條件式短句（Groups 角色說明只在編輯 builtin 群組時才出現，非恆常占用版面，效益不足以再拆 icon） |
| Hosts 批次貼上格式說明 | 維持常駐 | 含 `<code>` 排版，popover `html:false` 保不住（技術限制，第五輪已註記） |

**與規劃差異**：原估「這輪多數降為 icon」，覆核後實際降為 icon 的是 5 處
（含 §1 一併處理的 2 處）而非「多數」——逐一比對後發現第五輪的保留判斷
本身已相對嚴謹，二次收斂的邊際空間集中在「驗證限制型」與「UI 狀態描述型」
兩類，其餘（尤其牽涉不可逆設定變更或資料可見性）核實後仍應保留。
瀏覽器實測確認 icon 版 popover 正常顯示、AI 金鑰動態 hint 行為不變、
1214 測試綠燈。

---

## 5. 全站用詞檢視（官方用詞／台灣 IT 慣用詞）

### 需求（隨 Q3 答覆擴充）

文字措辭以**官方用詞（微軟正體中文詞彙）或一般台灣 IT 用詞**為主，
並檢視整個網站的用詞一致性。

### 檢視範圍（四個使用者可見的字串面）

1. **Razor views**（`Views/Pages/*.cshtml`＋`_Layout.cshtml`）——靜態標籤、
   說明、modal 文字。
2. **前端 JS**（`wwwroot/js/`）——動態產生的 `textContent`、toast、確認框、
   空狀態、表頭。
3. **後端使用者可見字串**——`DomainException` 訊息（API 錯誤直接顯示於前端，
   WEB-SPEC §8.6-4 明定不轉譯）、稽核 summary、`RiskReportService` 報告 txt
   （每日產出的正式文件）、批次 console 輸出（過渡期仍在用）。
4. **README 與部署文件**的操作指引段（程式碼註解不在本輪範圍——註解是
   開發者溝通，量大且不影響使用者）。

### 初掃結果（2026-07-31）

站台底子乾淨：常見陸詞家族（刷新/保存/設置/服務器/網絡/數據/信息/軟件/
硬件/加載/默認/運行/界面/連接/字段/郵件）**前端全部零命中**；
「用戶端」（client 的微軟官方譯名）與「通過驗證」（動詞，非介詞誤用）
屬正確用法。真正要處理的是**一致性**問題：

| 詞組 | 現況 | 統一方向 |
|---|---|---|
| 點擊（22 處）vs 點選（3 處） | 混用 | 統一**「點選」**（一般台灣 IT 慣用；微軟官方「按一下」過於拘謹，與站台語氣不合） |
| 查看（13 處）vs 檢視（既有多處） | 混用 | 原則統一**「檢視」**（微軟官方 view 譯名，站台權限文案已用「檢視權限」）；口語句中自然的「看」不強改（例：「點此查看」→「點此檢視」，但「看得到這台主機」不動） |
| 其他 | 實作時逐頁掃描補列 | 以微軟語言入口網（Microsoft Language Portal）詞彙為優先參照，無官方詞才用台灣業界慣用詞 |

### 做法與驗收

- 實作時以詞表掃描（上表＋逐頁人工通讀）處理四個字串面；報告 txt 與稽核
  summary 的既有歷史資料**不回溯改寫**（證據層原則），只改產生端。
- 逐處變更整理成對照表附於本節驗收（同 §4 模式）；測試中若有 assert 比對到
  被改的字串，同 commit 內同步修正。
- 本輪新 UI（§2/§3）自始依統一後詞彙撰寫。

### 實作結果與驗收對照（2026-07-31）

**與規劃的關鍵差異**：實際逐處核對後，「點擊」的 22 處**全部是程式碼註解**
（開發者溝通、明列不在範圍），使用者可見文字裡唯一的「點選」用例
（`Dashboard.cshtml`）已經是目標詞——**這一對詞組實際上零改動**，規劃階段
的粗略 grep 未區分註解與 UI 文字，覆核後修正。

「查看」9 處使用者可見字串（`Reports.cshtml` 1 處、`dashboard.js` 2 處、
`handler-detail.js`／`host-detail.js`／`record-detail.js`／`settings.js`
各 1 處、`runs.js` 2 處）全數改為「檢視」；`KnownIssueSeed.cs` 的 6 處
「查看」**判斷不改**——那是規則的處置建議文字（如「查看該服務對應的應用
程式日誌」），屬於「去檢查／查看紀錄」的調查指令語氣，與 UI 導覽用的
「檢視」（查看畫面／頁面內容）語意脈絡不同，強改成「檢視」反而生硬，
判定為 §5 範圍內「口語句中自然的「看」不強改」原則的延伸案例。

其餘候選詞組（登錄/登入、確定/確認、帳號/帳戶等）逐一核對用法後**皆屬
語意正確的既有區分**，非不一致（例：「登錄」專指主機登記入清單、「登入」
專指使用者驗證登入，兩者意義不同不應合併）；後端 `DomainException`／
README 掃描零命中「查看」「點擊」。全站用詞一致性問題實際上遠比初估輕微，
本輪只需 9 處字串異動，零後端變更、零測試斷言衝突。

---

## 6. 定案紀錄（2026-07-31 使用者全數拍板）

| # | 問題 | 定案 |
|---|------|------|
| Q1 | `SuppressionCli`／`HostListCli` 提前刪或隨 Phase 5？ | **依建議：提前到本輪首段刪除**，README 同步改 |
| Q2 | 本輪範圍到 Phase 2+3 或一口氣到 Phase 4＋Phase 5 就緒？ | **全部處理**（做到 Phase 4 完成＋Phase 5 就緒；console 實際移除仍卡 ≥5 晚試點閘門） |
| Q3 | §1 文字措辭 | **依官方用詞或一般台灣 IT 用詞為主，同時檢視整個網站用詞**——擴充為 §5 全站用詞檢視 |
| Q4 | 分支基底 | **從 dev 開新分支**（`feature/web-scheduler`） |
| Q5 | 側欄項目名稱 | **「排程作業」** |
| Q6 | §4 二次收斂基準 | **評估後非必要常駐項目都改**——照 §4 擬定基準執行，逐處對照表實作後附於 §4 驗收 |

## 7. 實作順序（已可開工）

1. ✅ §1 NetIQ 頁文字修正＋§4 說明文字二次收斂＋§5 全站用詞檢視
2. ✅ 刪 `SuppressionCli`／`HostListCli`＋README 對應兩節（Q1 定案）
3. ✅ Phase 2 服務搬遷（只搬不改；1214 測試綠＋console 輸出逐字不變，`git diff`
   逐行核對確認；`AnalysisOrchestrator`／`IRunConsole` 抽取一併完成）
4. ✅ Phase 3 排程引擎＋UI（`ScheduleOptions`／`ScheduleCalculator`／
   `NamedMutexGate`／`SchedulerHostedService`／6 支 API／側欄改名＋權限放寬／
   排程卡／主機詳情觸發鈕，全部完成，1258 測試綠，明細見 §2 末段）
5. ✅ Phase 4 CLI 職責搬 Web（規則升級 → 傾印開關 → probe 診斷分頁，逐塊 commit，
   明細見 §3「Phase 4 全部完成」）
6. ✅ 全案體檢 → 併 dev（使用者驗證含排程實跑仍待使用者本人執行，見下方）
7. Phase 5 退場步驟 1~2 由部署執行；**≥5 晚試點後**回頭做步驟 4~5
   （console 移除＋文件收尾，屆時另一個小輪收尾）

每步維持既有流程：feature 分支、逐步 commit、測試綠才前進。

### 全案體檢（2026-07-31）

- **靜態掃描**：`git diff dev...feature/web-scheduler` 全文掃過（72 檔、
  4593 插入/1560 刪除），檢查殘留除錯輸出／TODO／寫死密碼等紅旗字串，
  無異常命中。
- **稽核對照表缺口**：核對 `AuditQueryService.ActionNames` 時發現 Phase 3／
  §1.4.9 新增的 4 個動作代碼沒有補上中文對照（見 §3「Phase 4 全部完成」
  第 1 點），本輪一併修正。
- **一致性瀏覽器實測**（單一登入 session 內連續走過三個本輪改動最集中的
  頁面，確認彼此無互相影響）：排程作業頁（排程卡／執行總表／異常彙總
  皆正常渲染，無主控台錯誤）→ 規則維護頁（`import-status` 正確回報
  `hasUpdate:false`，橫幅正確隱藏）→ NetIQ 維護頁兩分頁（「設定」分頁
  Sentinel 清單/選項表單正常；「診斷」分頁確認伺服器重啟後 `NetiqProbeRunState`
  正確重置為乾淨狀態，不是殘留上次執行的假象）。
- **測試**：1258 測試綠（`dotnet test` 完整跑過，非增量）。
- **分支狀態**：`feature/web-scheduler` 的 merge-base 就是 `dev` 目前
  HEAD（單線往前，dev 這段時間沒有其他人推新 commit），併入為乾淨的
  fast-forward／無衝突合併。

### 二輪體檢（2026-08-04，逐條對照規劃驗收條款重掃）

併 dev 後依使用者要求對全部 Phase 修改重新逐條對照規劃，**揪出三處
Phase 3 排程引擎的規劃落差**（Phase 4 的規則升級／probe 部分乾淨），
已全部修正並於 dev 上直接補 commit：

1. **窗口 End 的優雅停止沒有實作**（規劃 §1.4.3 明文「End 到點對進行中的
   執行發 cancel」）：`ScheduleCalculator.IsWithinAnyWindow` 寫好了、測試也
   綠，但生產程式零呼叫端。修正：`SchedulerHostedService.TickAsync` 執行中
   時檢查——排程觸發的執行落在所有窗口之外即 `TryCancel()`（手動觸發不受
   窗限、不在此停；刻意放在 Enabled 檢查之前，執行中途關掉 Enabled 這次
   仍按窗口停）。
2. **`TriggerRunAsync` 同步等完整趟分析**：`POST api/admin/schedule/run` 的
   HTTP 請求會被掛住直到分析結束（可能數小時），回應訊息「已開始執行」
   名不符實；排程觸發時輪詢迴圈也被卡死——這正是缺陷 1 無從實作的根因。
   之前瀏覽器實測刻意未真正送出 POST run（避免對 dev DB 寫入分析資料），
   所以沒被抓到。修正：改「確定開始就返回」——`TryBeginRun` gate 照舊，
   分析移入背景工作（只依賴 Singleton，與 `NetiqProbeService` 同款），
   `TaskCompletionSource` 只等「進入 Mutex 保護區（＝真的開始）或逾時拿
   不到鎖」，最久 5 秒。實測 POST run 33ms 返回。
3. **取消的執行被記成「失敗」**（規劃 §1.4.4 明文「是『已停止』不是
   『失敗』，更不是卡『執行中』」，且要記里程碑）：OCE 展開時
   `BatchRunRecorder.Dispose` 一律回填 exit 1，Runs 頁顯示「失敗」。修正：
   recorder 收下 CancellationToken（console 傳 None 行為不變），Dispose 分
   得出「取消權杖已觸發＝優雅停止」——記里程碑「執行已優雅停止…」＋
   `BatchRun.Stopped = true`（JSON 缺欄容忍，零遷移）＋exit 0；
   `RunMonitorService`／Runs 頁新增獨立「已停止」狀態（總表新欄＋圖例＋
   詳情狀態文字），不列失敗主機清單。**與規劃的差異**：規劃的里程碑文字
   含停止者帳號「使用者手動停止（{帳號}）」，但里程碑由 Core 的
   orchestrator/recorder 寫入、取消可能來自手動／窗口 End／站台關閉三種
   來源，Core 拿不到（也不該拿）Web 的登入身分——改用中性文字，停止者
   帳號已在稽核 `schedule_manual_cancel` 紀錄裡，不重複第二份。
4. 順帶補齊 **WEB-SPEC.md 規格記錄缺漏**（§1.6 影響面清單本就列了
   「WEB-SPEC（新 API/頁面）」但首輪漏做）：§9.4 指定主機更新鈕、§9.7
   規則升級、§9.9a 診斷分頁＋probe API、§9.10 改名/權限放寬/排程卡/
   排程 API/「已停止」狀態/觸發來源欄、§11-1 動作代碼類別與中文對照表
   同 commit 規則。README/HISTORY 的全面改寫仍屬 Phase 5 文件收尾，
   不在本輪。

驗證：新增 4 條測試（stopped 狀態判定優先於錯誤計數、舊紀錄缺欄容忍、
recorder 取消/未取消 Dispose 對照組），1262 全綠；瀏覽器實測
「觸發 33ms 返回 → status 顯示執行中（手動 d1tester）→ cancel → 總表
『已停止』欄計 1、失敗 0、失敗主機清單空、詳情顯示已停止＋優雅停止
里程碑」全程通過，無主控台錯誤。
