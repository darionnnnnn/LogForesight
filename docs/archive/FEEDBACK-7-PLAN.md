# 回饋第七輪規劃（FEEDBACK-7-PLAN）

> 規劃與實作日期：2026-08-04。狀態：**五項全部完成並已合併 dev**。使用者實測 Web
> 排程化版本（dev@85303f0）後回饋五項問題：(1) 排程設定「執行窗口」時間欄位太窄，
> 時間被遮擋；(2) 掃描匯入的網段範例用了實際內網例子，要改通用例；(3) AI 功能要
> 以「系統設定是否有設定 AI」為單一開關；(4) 「立即執行」本機失敗且 NetIQ 全部
> 主機未執行；(5) sln 中疑似閒置專案。

| # | 項目 | 規模 | 分支 |
|---|---|---|---|
| 1 | 排程時間欄位寬度 | 極小 | `feature/feedback-7` |
| 2 | 網段範例改通用 | 小 | `feature/feedback-7` |
| 3 | AI 未設定自動短路統計模式＋隱藏相關 UI | 中 | `feature/feedback-7` |
| 4 | 立即執行必炸 bug＋失敗回饋缺失 | 大 | `feature/feedback-7` |
| 5 | console 專案退場（WEB-SCHEDULER-PLAN Phase 5） | 大 | `feature/console-retirement` |

---

## 1. 排程執行窗口時間欄位寬度不足

**根因**：`runs.js` 的 `renderScheduleWindows()` 對兩個 `input[type=time]` 硬寫
`style.maxWidth='130px'`，Chrome/Edge 原生 time picker（HH:MM + 時鐘圖示 + spinner）
實測約需 140~150px，`max-width` 上限把數字擠掉。

**修法**：改成固定 `style.width='150px'`（[runs.js:462,474](../LogForesight.Web/wwwroot/js/pages/runs.js)）。

---

## 2. 掃描匯入與立即執行的網段範例改通用

使用者可見文字的網段範例統一改用 `192.168.0`／`192.168.0.0/24`（原用 `10.1.2` 系列，
容易被誤認為真實內網位址）：

- `imports.js` 掃描匯入輸入框 placeholder
- `Imports.cshtml` 掃描匯入卡片 popover 說明
- `Runs.cshtml` 立即執行 modal 網段輸入框 placeholder
- `runs.js` 立即執行網段預覽的驗證提示文字
- `SentinelQueryBuilder.cs` 三處例外訊息（網段格式錯誤、單一 IP 誤判、網段太籠統）
  ＋相關 XML 文件註解
- `NetiqDtos.cs` 的 XML 文件註解
- `docs/WEB-SPEC.md`、`README.md` 的對應範例

**不動的部分**：`SentinelQueryBuilderTests` 等測試把 `10.1.2` 當任意合法輸入資料使用、
不斷言訊息字面內容，改文案不影響測試，維持不動；歷史文件（NETIQ-API-PLAN 的真實
probe 紀錄等）保留原文，不回溯改寫。

---

## 3. AI 未設定時自動短路統計模式並隱藏相關 UI

### 決策

使用者釐清「AI 有設定就預設開啟」實際是指**排程/立即執行的 AI 分析自動化**（本來就
沒有手動開關、一律使用 AI），而非「AI 診斷傾印」這個除錯用開關（後者維持手動、不
預設開啟，但 AI 未設定時隱藏）。

### Core：`AiSettings.IsConfigured` 貫穿 `useAi`

- [AppSettings.cs](../LogForesight.Core/Configuration/AppSettings.cs)：`AiSettings` 新增
  `public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);`——呼叫前提是
  `RuntimeSettingsResolver.ApplySystemSettingsOverrides` 已套用過 DB 覆寫（DB 存過但
  刻意清空 BaseUrl 時會覆寫成空字串，即「刻意停用」）。
- `AnalysisOrchestrator.RunAsync`：`var useAi = settings.Ai.IsConfigured;`，未設定時印出
  「AI 未設定：本次以統計模式執行」milestone；`useAi` 貫穿 `RunLocalAnalysisAsync`（傳給
  `AnalyzeDayAsync` 既有的 `useAi` 參數）與 `RunNetiqAnalysisAsync`。**維持建構
  `AIService`**（4a/4b 修好後建構子本身無害，`useAi=false` 時單純不會被呼叫）。
- `RecordAiCall` 加 `useAi &&` 前置條件（本機與 NetIQ 路徑皆同步修改）——否則統計模式
  下非低風險日會被誤記成「AI 呼叫失敗」，污染執行明細的失敗計數。
- `WeeklyCheckupService.RunAsync` 加 `bool useAi = true`：窗口內無訊號的確定性早退路徑
  不變；有訊號且 `!useAi` 時回 `Completed=false`、結論「（AI 未設定，體檢敘事暫缺，設定
  後下次執行自動補跑）」，沿用既有「未完成不寫入歷史、下次補跑」語意，不發任何網路請求。
- `NetiqPipelineService` 加建構子參數 `bool useAi = true`（機房 pipeline 每次執行都重新
  建構，用建構參數而非替四個內部方法都加一個參數）。

**效果**：AI 未設定時不再逐日嘗試打逾時再降級（原本會浪費整晚的 retry 時間），本機/
NetIQ/體檢三處都乾淨地一次判斷、直接走統計模式。

### Web：四處未接 AI 狀態的 UI 缺口

判斷點沿用既有 `GET /api/ai/status`（`WebAiService.Available`），範式參照
`records.js`／`record-detail.js` 已有的 `aiAvailable` 用法：

| 位置 | 做法 |
|---|---|
| 儀表板「AI 今日焦點」 | `dashboard.js` 的 `loadAiFocus()` 先查 `ai/status`，不可用直接 return（原本無條件打 `today-focus`） |
| 排程頁「AI 診斷傾印」開關＋徽章 | `Runs.cshtml` 包 `id="schedule-debug-dump-wrap"`；`runs.js` 的 `loadSchedule()` 平行查 ai status，未設定時對 wrap 與徽章加 `d-none`。**隱藏不改值**——開關值照常載入/回傳，避免隱藏期間存檔把設定意外歸零 |
| 執行明細「AI 呼叫」統計列 | `runs.js` 的 `renderStats()`：`aiAvailable \|\| detail.aiCalls > 0` 才顯示——歷史上真的呼叫過 AI 的執行紀錄仍如實呈現 |
| NetIQ「詢問 AI 現場查詢」勾選 | `Netiq.cshtml` 包 `id="opt-chat-live-fetch-wrap"`；`netiq.js` 的 `loadOptions()` 加 ai status 查詢，未設定時隱藏；送出仍照常帶當前值 |

後端不需新守門：`AiController` 各端點已有 `_ai.Available` 早退。

---

## 4. 立即執行必炸 bug（本機失敗且 NetIQ 全部主機未執行）

### 根因

Web 用 `builder.Configuration.Get<WebAppSettings>()`（`Program.cs:48`）透過
`IConfiguration` binder 綁定 `AiSettings.ExtraRequestFields`（`Dictionary<string, JsonElement>`）。
實測確認 binder 對這個型別的行為**比原先設想更細緻**：巢狀物件（如
`chat_template_kwargs`）binder 能正確綁出合法 JsonElement；**純量值**（如
`rep_pen: 1.3` 這種沒有子節點的葉節點）才會被綁成 `default(JsonElement)`
（`ValueKind=Undefined`）。`AIService` 建構子（`AIService.cs:97`）對這種空值呼叫
`GetRawText()` 必定丟 `InvalidOperationException`——這正是實際 log 炸點的原文
（`AnalysisOrchestrator.cs:139` 呼叫 `new AIService(...)` 時炸掉）。

建構點在本機（344）與 NetIQ（354）分析**之前**、全域 `try`/`catch`（419）**之內**，
一炸全滅——與 AI 有無設定無關，**只要從 Web 觸發執行就必炸**。

### 修法

- **`AIService.cs:92-99` 縱深防禦**：迴圈跳過 `ValueKind == JsonValueKind.Undefined`
  的項目並 `Log.Warn`，不讓組態綁定層的壞值擴散成整趟執行中止。
- **`AiExtraFieldsLoader`（新增，`LogForesight.Web/Configuration/`）主修**：繞過
  `ConfigurationBinder`，直接用 `JsonDocument`/`JsonSerializer` 重讀
  `appsettings.json` 與 `appsettings.{Environment}.json` 的 `Ai:ExtraRequestFields`
  節點（與批次 `AppSettings.Load`、`WebAiService.LoadBatchAiSettings` 走同一種正確
  路徑）；`Program.cs` 綁定後立即覆寫該欄位，兩份設定檔皆無節點時回復型別預設值，
  避免沿用 binder 產生的壞字典。

  實作過程中，回歸測試（見下）意外抓到 `AiExtraFieldsLoader` 自身的一個 bug：
  `JsonSerializer.Deserialize` 沒開 `AllowTrailingCommas`/`ReadCommentHandling`，
  真實 appsettings.json 的尾逗號會讓反序列化靜默失敗、回傳 null——已修正並補上對應
  測試案例。

### 失敗回饋：「上次執行結果」

原本失敗只寫 NLog，`SchedulerHostedService.TriggerRunAsync` 只回報「有沒有真的
開始」，`ScheduleController.Run` 因此永遠回「已開始執行。」的成功 toast——使用者對
「立即執行」的最後印象停在這裡，實際上執行 191ms 後就整個炸掉，只能翻 log 檔才知道。

- `SchedulerRunState` 新增 `RunOutcome`（`Success`/`Message`/`Trigger`/`EndedAt`）與
  `LastOutcome` 屬性；`EndRun(RunOutcome? outcome)`——`outcome` 為 `null`（跨行程
  Mutex 逾時、沒有真的開始過）時保留前一筆結果，不用「沒開始」蓋掉「上次真的跑過的
  結果」。
- `SchedulerHostedService` 背景工作捕捉三種結局（`orchestrator` 回傳結果、
  `acquired=false`、外層例外）填成 `RunOutcome` 傳給 `EndRun`。
- `ScheduleStatusDto` 加 `LastRunSuccess`/`LastRunMessage`/`LastRunTriggerText`/
  `LastRunEndedAt`；`ScheduleController.GetStatus` 填入。
- `runs.js` 的 `refreshScheduleStatus()`：閒置時顯示「上次執行：成功（觸發來源，
  時間）」（灰字）或「上次執行：失敗（觸發來源，時間）— 訊息」（紅字）；使用者手動
  取消特判成「上次執行：已停止」（不算失敗，中性語氣）。站台重啟後歸零顯示為空，
  完整歷史仍查執行總表（`BatchRun` 已有失敗列的持久紀錄）。

### 驗證

瀏覽器實測完整重現與修復確認：清空系統設定的 AI 位址（`available:false`）後觸發
`POST /api/admin/schedule/run`（`scope:all`），3 秒後查狀態得
`{"isRunning":false,"lastRunSuccess":true,"lastRunMessage":null,"lastRunTriggerText":"手動（svc-lfadmin）"}`，
`preview_logs` 確認無「執行失敗」字樣（先前必現的
`InvalidOperationException` at `AIService.cs:97` 已不再出現）；排程頁狀態卡顯示
「上次執行：成功（手動（svc-lfadmin），2026-08-04 09:48）」。

---

## 5. console 專案退場（WEB-SCHEDULER-PLAN Phase 5）

### 盤點結論

sln 中沒有無人引用的孤兒專案。唯一候選是批次 console 專案（`LogForesight`），原定
需 Web 排程 ≥5 晚實際環境驗證通過才移除（2026-07-31 定案 #5）。**使用者本輪知情
豁免該驗證閘門，決定提前執行 Phase 5**（決策修訂記於 docs/HISTORY.md「決策 20
修訂」）。

### 四個專屬類別的處置

console 專案除 `Program.cs`／csproj／`nlog.config`／`appsettings.json` 外，僅剩四個
專屬類別（`LogForesight/Service/`），逐一核實均可直接刪、不需搬 Core：

| 類別 | 判定依據 |
|---|---|
| `RuleImporter` | Web `RuleAdminService` 已完全涵蓋（直接呼叫 Core `RuleImportPlanner.BuildPlan/Apply`）；規則頁的「內建規則升級」橫幅＋預覽/套用對話框是現行正式入口 |
| `SelfTestRunner` | 依 2026-07-31 定案 #9 隨 console 退役；其中「關聯層事件 ID 對齊規則表」的檢查邏輯（`CheckCorrelationIdsExistInRules`）此前沒有對應的自動化測試覆蓋，退場前先港為新增測試 `CorrelationAnalyzerRuleAlignmentTests`（8 個案例，涵蓋全部 8 組事件 ID 群組），把「手動跑才檢查」升級成「每次建置都檢查」，覆蓋不打折 |
| `NetiqProbeCli` | probe 已 Web 化（NetIQ 維護頁「診斷」分頁），Core `NetiqProbeRunner` 查詢邏輯不受影響 |
| `ConsoleRunConsole` | `IRunConsole` 介面留在 Core（Web `WebRunConsole` 實作它），僅刪 console adapter 本體 |

### 清理範圍

1. 刪除 `LogForesight/` 整個專案目錄。
2. `LogForesight.sln`：移除專案宣告與組態區塊。
3. `LogForesight.Tests.csproj`：移除對 console 專案的 `ProjectReference`。
4. `RuleImporterTests.cs`：移除 `RuleImporterRunContractTests` 類（測 `RuleImporter.Run`
   的 CLI 編排契約，隨 CLI 退役；`RuleImportPlanner` 純函數測試全數保留）。
5. `LogForesight.Core.csproj`：移除 `InternalsVisibleTo Include="LogForesight"`。
6. 相關程式碼註解同步更新（不再宣稱 console 仍存在）：`CorrelationAnalyzer.cs`、
   `KnownIssueCatalogTests.cs`、`AnalysisOrchestrator.cs`、`NetiqProbeRunner.cs`、
   `RuleAdminService.cs`（`--selftest`／`--import-rules` 用語改為描述現況）、
   `_Layout.cshtml`。
7. **`Program.cs`（Web）連帶簡化**：開發環境原本會推算「同一 repo 內批次
   `LogForesight` 專案的輸出目錄」當 `DataRoot` 預設值（`TryResolveSiblingBatchDataRoot`）
   ——console 專案已刪，這個推算目標不存在了，且 `StorageSettings.ResolveDataRoot()`
   本來就會在 `DataRoot` 留空時退回 `AppContext.BaseDirectory`（Web 自己的輸出目錄），
   直接移除該函式與呼叫點，改用這個更簡單、天然正確的預設值。連帶更新
   `appsettings.Development.json`（含 `.example`）的說明註解、啟動時的資料根目錄
   健檢訊息措辭（不再提「批次 LogForesight.exe」）。
8. **使用者可見文字修正**：`runs.js` 執行總表的空狀態提示原文「請確認
   `LogForesight.exe` 的排程已設定」（引用一個已刪除的執行檔）改為「請至上方
   『排程設定』啟用排程，或按『立即執行』手動觸發」。
9. `.gitignore`：移除指向已刪除 `LogForesight/` 目錄的執行期資料規則（`history.txt`／
   `rules.json`／`rundata\` 等），避免規則指向不存在的路徑。
10. **README.md 全面改寫**：專案結構、架構流程收尾文字、部署驗證（`--selftest`／
    `--debug-dump` 改為描述 `dotnet test`＋規則頁驗證＋排程頁「AI 診斷傾印」開關）、
    規則庫維護 SOP（`--import-rules` 改規則頁橫幅流程）、NetIQ probe 驗證（改「診斷」
    分頁）、使用方式（`LogForesight.exe` 改「排程／立即執行」兩種觸發方式）、正式環境
    排程（schtasks 改 Web 排程設定頁）、Web 部署（單一部署單位，移除「與批次同機」的
    目錄配置與雙服務帳號權限說明）、正式環境穩定性設計表（單一執行個體改行程內
    `SchedulerRunState` gate＋具名 Mutex 防護、新增「執行結果可見」列取代舊的
    「Exit code」列）、診斷用檔案 Log（改指 `logs\web.log`，NLog 目錄解析改用
    `NLog.Web.AspNetCore` 的標準機制，移除已不存在的手動自我檢查段落）。
11. **docs/WEB-SPEC.md**：測試清單移除 `RuleImporterRunContractTests`；規則升級／
    probe 診斷分頁兩處「過渡期 console CLI 薄殼」措辭改為「已隨 Phase 5 退場移除」。
    歷史決策記錄段（§6.1 JWT 決策、Phase 0 架構圖等）維持原文，不回溯改寫。
12. **docs/WEB-SCHEDULER-PLAN.md**：§0/§1.5/§1.6 標記 Phase 5 已完成，附決策修訂
    說明（≥5 晚驗證由使用者知情豁免）；**明確**步驟 1~2（schtasks 刪除、部署面
    exe／appsettings 移除）仍是使用者在實際部署主機上的動作，本次退場只涵蓋原始碼
    與文件層面。
13. **docs/FEEDBACK-6-PLAN.md**：閒置檔案盤點表逐列標記為「✅ 已刪」。
14. **docs/HISTORY.md**：「決策 20 修訂」——one-shot＋工作排程器模型正式推翻，改為
    Web 常駐排程引擎；記錄 Phase 5 提前執行的決策與風險評估。

### 風險與緩解

- **冷回退**：console 移除後只剩 `git revert` 專案移除 commit＋重建部署一途，無
  schtasks 熱回退。緩解：分析冪等，缺漏日靠既有回補機制自動補齊，修復期間不會
  永久缺資料。
- **部署面尚未執行**：本輪只處理原始碼與文件；使用者在實際生產主機上刪除
  schtasks、移除批次 exe 與 appsettings 仍待另行執行。

### 驗證

`dotnet build` 全解決方案 0 警告 0 錯誤；`dotnet test` 全綠
**1286 個測試**（較退場前 1281 個：移除 3 個 `RuleImporterRunContractTests`、
新增 8 個 `CorrelationAnalyzerRuleAlignmentTests`）。

---

## 全案體檢（2026-08-04，兩分支併入 dev 後逐項對照規劃重掃）

體檢揪出並修正四類問題（合為 dev 上的體檢 commit）：

1. **規劃缺漏——分支二第 6 步「WebAiService 協調」漏做**（唯一漏掉的規劃項）：
   `_batchSettings = LoadBatchAiSettings(...) ?? settings.Ai;`——console 退場後
   `LoadBatchAiSettings` 讀 `{DataRoot}\appsettings.json`，預設情況（DataRoot＝Web
   自己的目錄）碰巧讀得到 Web 的 appsettings 所以能動，但部署面把 DataRoot 指到
   獨立資料目錄時該檔不存在，AI 進階參數（逾時/ExtraRequestFields/懲罰參數）與
   `UpdatedAt==null` 的 BaseUrl 退路會靜默退回型別預設值。補上後 `_batchSettings`
   保證非 null（三處 `?.` 用法一併收斂），`Available` 與排程路徑的 `IsConfigured`
   收斂到同一份設定來源。
2. **覆蓋缺口——`SelfTestRunner.RunSentinelQueryChecks` 沒有對等測試**：該檢查
   （真實種子規則表建出的 Sentinel filter 結構驗證）隨 console 刪除後就沒有任何
   自動化驗證。移植為 `SentinelQueryBuilderTests` 檔尾的 4 個新測試（IP 批次與
   generic 子句、Windows 事件 ID 可下推、基準 Security ID 反映在 rv40 子句、
   MatchAllEventIds 不混入聯集）。
3. **使用者可見的殘留**——`RuleBootstrapper` 的 UpdateHint 仍教使用者「可執行
   --import-rules」（已刪除的 CLI）：改指 Web 規則維護頁的升級橫幅，
   `RuleBootstrapperTests` 的字面斷言同步更新。
4. **過時註解清理**（describing console/CLI 為現行狀態的註解，歷史事實陳述不動）：
   `AnalysisOrchestrator`（類別頭注/RunScope/Args/Trigger/OrchestratorResult/全域
   catch）、`IPromptDumper`（--debug-dump → AI 診斷傾印）、`SlowTrendAnalyzer`、
   `KnownIssueSeed`、`IKnownIssueRuleStore`、`RuleBootstrapper`（含移除一段指涉
   已刪 `--suppress` CLI 的重複 summary）、`SentinelClient`（欄位對應已定案）、
   `RuleAdminServiceTests`／`RuleImporterTests`／`SentinelQueryBuilderTests` 檔頭。

**體檢後的實機煙霧測試**（console 退場後 Web 首次冷啟動——DataRoot 預設值改變是
本輪最大的執行環境改動）：全新環境啟動 → DataRoot 正確落在 Web 自己的輸出目錄、
自動建 schema＋種子群組＋64 條規則、首次執行警告以新措辭正確顯示 → 清空 AI →
「立即執行」全新 DB 的 120 天首次回補以統計模式 **8 秒完成**、逐日輸出「統計模式，
AI 未設定」、體檢正確回報「未完成待補跑」、零 error → 狀態卡顯示「上次執行：成功
（手動（svc-lfadmin），13:11）」、「AI 診斷傾印」正確隱藏 → AI 設定還原。

## 測試變化總計

- 分支一新增：`AIServiceExtraRequestFieldsTests`（3）、`AiExtraFieldsLoaderTests`
  （6）、`SchedulerRunStateTests`（5）、`WeeklyCheckupServiceTests` 新增 2 案例、
  `AppSettingsLoadTests` 新增 `IsConfigured` 3 案例（Theory）。
- 分支二新增：`CorrelationAnalyzerRuleAlignmentTests`（8）。
- 分支二移除：`RuleImporterRunContractTests`（3）。
- 體檢新增：`SentinelQueryBuilderTests` 種子規則表結構性驗證（4，自
  `RunSentinelQueryChecks` 移植）。
- 基線 1262 → 分支一合併後 1281 → 分支二合併後 1286 → 體檢後 **1290**，全綠。

## Git 流程

`feature/feedback-7`（項目 1~4）→ 併 `dev` → `feature/console-retirement`（項目 5，
自併入後的 `dev` 開）→ 併 `dev` → 全案體檢 commit（dev）。依既有慣例，
`dev` 驗證無誤後才併 `master`。
