# 回饋修正第 39 輪規劃

> 狀態：規劃中（待使用者確認後開分支 `feature/feedback-39`）
> 基準：dev@d4ffe61（3497 綠，略過 6）
> 來源：使用者回饋七項（排程頁總表／守門偵測按鈕／PRTG 同步卡住與 AI 未並行／規則合併評估／
> 排程流程梳理／PRTG 取數範圍／排程頁排版）
> 實作方式：整輪委派 `agy`；額度用完後由 Claude 接手（切換點記在執行紀錄，不換回）。
> 批次已切到 agy 粒度（一階段 1~2 個機制、逐條驗收）。

## 0. 核對結果摘要（細節見對話紀錄，此處只留結論）

| 項 | 判定 | 根因 |
|---|---|---|
| P1 總表含今天 | 成立 | 範圍錨在 `Today`；NetIQ 主機依 D-1 紀錄判定，白天今天列必然整列「未執行」 |
| P2 守門偵測按鈕 | 缺一個旗標 | `PrtgResourceGuardTargets.Resolve` 覆寫清單非空即短路，preview 端點無參數；儲存不驗 objid 存在 |
| P3a PRTG 卡「準備中」 | 必然 | `prtg-sync` 只送 `(0,0)`，三階段分頁完全不回報；messages 全量分頁無日期過濾；結構同步不吃併發設定 |
| P3b AI 未並行 | 明文行為 | AI 閘門：取數執行中剔除今天與昨天的待補，而取數產生的待補全是昨天；畫面不解釋原因 |
| P4 規則合併 | 落點錯層＋挖出 bug | `AttachPrtgFindings` 從不改 `RiskLevel`，`PrtgRuleCatalog.ElevatesDayRisk` 是死值；PRTG-SPEC §9「提升日風險」與實作不符 |
| P5 排程流程 | 有結構問題 | `RunPrtgFetchAsync` 275 行塞在 orchestrator；phase 裸字串散落三層；五個 RunState 各自複製 gate |
| P6 取數範圍 | 可做，有規模風險 | 三重過濾在觸發式取數與回填各寫一份；白名單留空＋全部主機＝真全量 42k sensor |
| P7 排版 | 成立 | 190 行巨卡承載五種職責、卡中卡、骨架與其他維護頁不一致；`runs.js` 1558 行 |

順手發現（本輪一併處理）：觸發式取數「分析結束再掃一次抓 AI 上調」在 AI 拆成獨立排程後已失效（批次 B 定案後語意恢復，見 B）；
`PrtgTriggeredValueFetcher` 類別註解寫「昨日風險」實為 `day`（D 修）；AI 排程處理迴圈多分片下 `ProgressTotal` 互相覆蓋（G3 修）；
PRTG-SPEC §3「finding 追加必須在觸發式取數之後」的理由敘述不精確（C 改文件）。

## 1. 定案（與使用者討論後）

1. **P1**：執行總表**移除今天列**。理由：當天資料隔天才跑，今天列沒有資訊量。執行中／手動執行的可見性由狀態卡與「執行紀錄」頁籤承擔（後兩者仍含今天）。
2. **P3b**：AI 閘門改為**逐主機日**：某主機的統計紀錄已落地、且該主機的 PRTG finding 已追加（或 PRTG 停用／失敗／該主機無 finding）即可跑 AI，不再等整趟取數結束。
3. **P4**：本輪做 (b) PRTG finding 單向上調日風險＋(d) AI prompt 獨立 PRTG 區塊；(c) 跨來源關聯留下一輪；(a) 規則引擎合併**明確不做**（與 PRTG-SPEC §1、BACKLOG 定案衝突，且輸入型別無共通抽象）。上調後的日子進 AI 待補是預期效果。
4. **P6**：三選一設定＋指定主機清單，且套用到歷史回填，順手把回填那份複製的過濾改成共用。
5. **P7**：套用 `ui-ux-pro-max`，方向見批次 F。
6. **P5**：四項全做（PRTG pipeline 抽類、phase 常數化、RunState 基底類別、手動觸發佔用窗口的可見性）。

## 2. 批次總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| E | 執行總表移除今天列 | 小 | 無 | 1 |
| A | PRTG 結構同步進度可觀測＋messages 日期過濾＋AI 閒置原因（後端） | 中 | 無 | 2 |
| B | AI 與取數並行：逐主機 PRTG finding 就緒閘門 | 中 | A1（進度 phase） | 3 |
| C | PRTG finding 上調風險＋prompt 獨立區塊 | 中 | B（追加時序） | 4 |
| D | 守門自動偵測按鈕＋取數範圍設定 | 中 | 無 | 5 |
| G1/G2 | phase 常數化＋PRTG pipeline 抽類 | 中 | A、B、C 定型後 | 6 |
| F | 排程頁重排（含 A3 的 UI 呈現） | 中大 | G1（前端讀常數清單） | 7 |
| G3/G4 | RunState 基底類別＋手動觸發佔用窗口可見性 | 中 | F（狀態卡欄位） | 8 |

每批一個 commit，訊息 `feat: <內容>（回饋第 39 輪批次X）`。

---

## 批次E：執行總表移除今天列

### 現況與核對結果
- `RunMonitorService.cs:71-98`：範圍 `[Today-days+1, Today]`，索引 0 = Today，迴圈無條件產生每一天。
- `BatchRunStore.cs:178` cutoff 同口徑；`RulesController.cs:163-170` 以 `days` 算 `totalPages`／`TotalDays`。
- 異常彙總 `GetErrorSummary`、執行紀錄 `GetRunList` 也直接用 `days`（含今天）。
- 測試 `RunMonitorServiceTests.cs:362,390,408-409` 斷言 Today 在第 1 頁。

### 定案
- 執行總表的 `days` 語意改為 `[Today-days, Today-1]`（仍是 N 列）；異常彙總與執行紀錄**維持含今天**（它們是事件清單，不是主機×日）。
- 前端「全部」鈕與 `maxDays` 不變。

### 改動
1. `RunMonitorService.GetDaySummary`（或對應方法）錨點改為 `Today-1`；分頁 `skipFromEnd`／`pageEnd` 一併位移；`TotalDays` 仍等於 `days`。
2. 測試改寫三處斷言；新增一條「今天不出現在任何頁」的斷言。
3. WEB-SPEC §9.10 執行總表段補一句「總表不含今天；今天的執行看狀態卡與執行紀錄」。

### 測試／驗收
- `RunMonitorServiceTests`：第 1 頁最新日 = Today-1；`days=7` 回 7 列且無 Today；分頁最後一頁列數正確。
- 異常彙總／執行紀錄測試不變且仍綠（證明口徑刻意分開）。

---

## 批次A：PRTG 結構同步可觀測＋AI 閒置原因

### 現況與核對結果
- `AnalysisOrchestrator.cs:1119`、`PrtgFetchService.cs:76` 各送一次 `prtg-sync (0,0)`；`FetchTablePagedAsync`（`PrtgFetchService.cs:563-632`）無 progress 參數。
- 每日擷取 `fetchValues:false`，下一個回報是 `prtg-triggered`，中間整段零回報。
- `FetchStateChangesAsync`（`:287-332`）query `content=messages&id=0` 無日期過濾，用戶端 `changedAt.Date != targetDate` 丟棄。
- `PrtgFetchServiceTests.cs:993` 斷言 `prtg-sync` 的 `Total == 0`。
- 前端 `runs.js:1055-1062` total=0 一律「準備中…」；`:1105` 已為 `prtg-triggered` 補「已取 N 個 sensor」特例。
- AI 閒置：`AiAnalysisHostedService.TickAsync` 五個 return 條件皆無 UI 訊號；`ai-status` 端點（`ScheduleController.cs:405-433`）只回執行狀態與件數。

### 定案
- `prtg-sync` 拆為三個子 phase：`prtg-sync-devices`／`prtg-sync-sensors`／`prtg-sync-messages`（字面值暫定，G1 常數化時統一）。分子＝已取列數，分母＝PRTG `table.json` 回應的 `treesize`（暫定；實機若無此欄位則分母 0、前端顯示「已取 N 筆」）。
- messages 改帶 PRTG 的相對日期過濾參數（`filter_drel`；暫定，需實機驗證），**用戶端日期過濾保留**（參數被忽略時仍正確，只是慢）。每日擷取固定用 `7days`（不用 `yesterday`：跨午夜的執行窗口在 messages 階段跑過零點時目標日會變成兩天前，`yesterday` 就漏了）；歷史回填依回填天數取能涵蓋的最小級距（30days／12months）。
- 結構同步階段仍**不**套 `PrtgFetchConcurrency`（分頁必須循序）、**不加整段逾時**（拍腦袋的倍數在大環境會誤殺合法的長同步；停止鈕的取消訊號已穿透分頁迴圈）。改為可觀測：每 50 頁寫一則 Milestone（「messages 已翻 N 頁 / 累計 M 筆」），讓「慢」與「卡」分得出來。
- AI 閒置原因：`ai-status` 新增 `idleReason`（`running-self`／`disabled`／`backfill-pending`／`outside-window`／`no-pending`／`waiting-fetch`（等待取數的 PRTG finding 就緒，B 批次後才會出現）／`null`＝執行中）。前端呈現在 F 批次。

### 改動
1. **A1**（`PrtgFetchService.cs`＋`PrtgFetchServiceTests.cs`）：`FetchTablePagedAsync` 加 progress 回呼，三階段各自送子 phase；解析 `treesize`；整段逾時。改寫 `:993` 斷言。`SchedulerRunState.ReportProgress` 的 `prtg-` 前綴分支已涵蓋，確認測試 `SchedulerRunStateTests` 補三個子 phase 不覆蓋 netiq 軌。
2. **A2**（`PrtgFetchService.cs`＋`PrtgBackfillRunner.cs`）：messages 相對日期參數；回填天數映射級距。測試：假 client 記錄 query 字串，斷言參數與級距。
3. **A3**（`AiAnalysisHostedService.cs`／`ScheduleController.cs`／`AiAnalysisSchedulerTests.cs`）：`TickAsync` 每個 return 前寫入 `AiAnalysisRunState.IdleReason`；`ai-status` 回傳。

### 測試／驗收
- A1：假 client 三張表各兩頁，斷言 phase 序列為 devices→sensors→messages 且 done 單調遞增、total = treesize；分頁逾時案例回失敗但後續階段仍執行。
- A2：每日擷取 → `filter_drel=7days`；回填 30 天 → `30days`、回填 200 天 → `12months`；用戶端過濾仍丟掉非目標日；假 client 回傳含非目標日資料時寫入筆數正確。
- A1 補：分頁每 50 頁一則 Milestone；取消訊號在第 N 頁中止且擲 `OperationCanceledException`。
- A3：五種前置條件各一個案例斷言 `idleReason`；狀態端點測試斷言欄位存在。
- 前端 `runs.js` 三個子 phase 的標籤在 F 批次補；A 批次先在 `PROGRESS_PHASE_LABEL` 加三筆（避免顯示裸字串），`RunsPageUiTests` 斷言存在。

---

## 批次B：AI 與取數並行（逐主機 PRTG finding 就緒閘門）

### 現況與核對結果
- `AiAnalysisHostedService.cs:256-266`：取數執行中剔除 `Date >= 昨天` 的待補；`AiAnalysisRunState` 與 `SchedulerRunState` 互不互斥。
- PRTG 路徑順序（`AnalysisOrchestrator.cs:1086-1360`）：結構同步→對應→規則評估（`:1160-1240`，不等 NetIQ）→觸發式取數（等 `analysisTask` 完成）→finding 追加（`:1298-1330`，一次性）。
- 紀錄寫入點：各主機 `IAnalysisRecordStore.Append`；`AttachPrtgFindings(hostId, day, findings)` 依 `EventKey` 去重，可重複呼叫。
- `SchedulerRunState` 在 Web，Core 不引用；Core 對 Web 的回報只有 `IRunProgress`／`IRunConsole`。

### 定案
- **紀錄「完整」的定義**：統計紀錄已落地，且本趟的 PRTG finding 已「就緒」——就緒＝規則評估完成、且對「就緒前已落地」的紀錄做完一次補追加；就緒之後落地的紀錄由寫入路徑**當場**追加。PRTG 停用、結構同步失敗、規則庫無 PRTG 規則 → 立即就緒（finding 為空）。
- **追加改為兩段式，取代現行「分析全部完成後一次追加」**：
  1. 執行內共享一個 finding 登錄簿（放在兩路共用的 `AnalysisRunContext`，規則評估完成時一次寫入不可變快照，之後只讀，NetIQ 平行 worker 讀取無競態）。
  2. **寫入路徑當場追加**：本機與 NetIQ 兩條路的主機日後處理（`HostDayPostProcessor` 既有的「案件掛接／風險 log 暫存」那一組）新增第一步「登錄簿已就緒 → 追加該主機 finding」，**排在案件掛接之前**，讓 PRTG finding 一併進問題案件（現行事後追加永遠進不了案件，見 C）。登錄簿未就緒時跳過，交給下一步。
  3. **就緒前落地的紀錄補追加一次**：規則評估完成後掃當日已存在的紀錄逐台追加，完成後宣告就緒。之後不再輪詢——就緒後的紀錄由第 2 步涵蓋。重跑模式覆寫紀錄時，第 2 步在覆寫後再跑一次，finding 自然重新追加（依 `EventKey` 冪等）。
- Core 新增小型介面（名稱暫定 `IPrtgFindingsGate`）：`MarkReady()`。由 Web 的 `SchedulerRunState` 實作並經 `AnalysisOrchestrator.RunAsync` 參數傳入（同 `IRunProgress` 慣例，null＝不回報）。
- AI 閘門改為：取數執行中且 `!Ready` → 沿用舊規則（剔除今天與昨天）；`Ready` → 全部合格。取數未執行 → 全部合格（不變）。
- 可觀測：就緒時寫一則 Milestone（「PRTG finding 就緒：N 台補追加」）；AI 閒置原因 `waiting-fetch` 只在「取數執行中且未就緒」出現。
- 這使「觸發式取數收尾再掃一次抓 AI 上調的主機」重新有意義（AI 可能在 `analysisTask` 完成前就上調風險）；不另改。
- **與現行 §3 順序的差異**：finding 追加不再排在觸發式取數之後；觸發式取數與追加互不相依（取數只看風險等級與規則命中主機，兩者在規則評估後都已知）。

### 改動
1. **B1**（Core：`AnalysisOrchestrator.cs`＋`HostDayPostProcessor.cs`＋`NetiqPipelineService.cs` 後處理呼叫點＋新介面檔）：登錄簿、寫入路徑追加、補追加一次、就緒宣告三種路徑；`RunPrtgFetchAsync` 步驟 5 移除。測試：假 gate 記錄 `MarkReady` 時機；就緒前落地的主機被補追加；就緒後落地的主機由後處理追加且進案件；重跑覆寫後 finding 仍在。
2. **B2**（Web：`SchedulerRunState.cs`／`AiAnalysisHostedService.cs`／`SchedulerHostedService.cs`＋`AiAnalysisSchedulerTests.cs`、`SchedulerRunStateTests.cs`）：狀態實作介面（`TryBeginRun`／`EndRun` 重設）；閘門改寫；`waiting-fetch` 閒置原因。

### 測試／驗收
- B1：紀錄在規則評估前落地的主機 X → 補追加階段追加；評估後落地的主機 Y → 後處理當場追加且 `AttachNewDay` 收到含 PRTG 的 TopIssues；PRTG 停用 → 執行開始即 `MarkReady` 且零 Attach 呼叫；規則評估擲例外 → 仍 `MarkReady`（空）且其餘 PRTG 階段照跑；取消穿透。
- B2：取數執行中、未就緒 → 昨天待補被剔除；就緒 → 昨天待補被處理；`EndRun` 後旗標重設；`idleReason=waiting-fetch` 只在未就緒時回傳。
- DETECTION-SPEC「兩個獨立排程」與 WEB-SPEC §9.10 改寫閘門敘述；PRTG-SPEC §3 執行順序改寫。

---

## 批次C：PRTG finding 上調日風險＋AI prompt 獨立區塊

### 現況與核對結果
- `ComputeRuleBasedRisk`（`LogAnalysisService.cs:961-976`）只看 issues／trend／correlations；`AttachPrtgFindings`（`EfAnalysisRecordStore.cs:245-318`）只改 `TopIssues`，不碰 `RiskLevel`，對照 `AttachAiResult`（`:230-232`）有寫 `RiskLevel`。
- `PrtgRuleCatalog.cs:26-31`：`down` High＋`ElevatesDayRisk=true`；`PrtgFindingMapper` 忠實寫入簽章。
- `RiskLevels.MoreSevere`（`RiskLevels.cs:37`）現成；`riskBasis` 代碼：`rule:`／`correlation`／`trend`／`high_issue:`／`ai_raise`。
- `HostDayPostProcessor.NeedsBackfill`（`:125-130`）：AI 已設定且非低風險且未分析且非待補 → 需要 AI。
- AI 補跑 `RetryAiAsync` 以 `pendingRecord.TopIssues` 當 issues 餵 `BuildPrompt`（`LogAnalysisService.cs:568`），PRTG 簽章會混在事件清單裡以 `PRTG#prtg:down:1234x1` 格式出現。

### 定案
- `AttachPrtgFindings` 追加後**重算**：`RiskLevel = MoreSevere(既有, PRTG 推導)`，PRTG 推導規則與 `ComputeRuleBasedRisk` 同語意（任一 `ElevatesDayRisk` → 高；任一 High → 中；否則不動）。**只升不降**。
- `riskBasis` 新增 `prtg:{規則代碼}`（多條以既有分隔慣例串接）；既有代碼不動。
- 風險由低升為非低、且 AI 已設定、`!AiAnalyzed`、`!DetailPruned` → 設 `AiPending=true`（與 `NeedsBackfill` 同判準；「AI 已設定」由呼叫端傳入布林）。`AiAnalyzed=true` 的紀錄（重跑情境）只升風險不重標。
- **問題案件**：追加後以合併後的 `TopIssues` 再呼叫一次案件掛接（`AttachNewDay` 冪等，已是「下次執行冪等補掛」的既定語意）——現行事後追加的 PRTG finding 從未進過案件，處理狀態、案件涵蓋日都看不到它。B 的寫入路徑追加已排在掛接之前，這條只補「補追加一次」那條路徑。
- **深析報告**：低風險日在統計段不產報告；被 PRTG 升為非低後進 AI 待補，AI 補跑時依既有機制重建報告（風險 log 暫存＋TopIssues），不另做。AI 未設定時不會有報告，與「AI 未設定的非低風險日」現況一致。
- **風險 log 暫存**不動：PRTG finding 沒有原始事件可存。
- **既有紀錄不回溯**：升級後既有紀錄的風險等級維持原值，下次追加（重跑或次日）才生效；不做一次性回填（回填會讓歷史風險日突然變多，管理者無從分辨是新問題還是舊資料被重算）。
- prompt：`AnalysisPromptBuilder.BuildPrompt` 把 `Source == "PRTG"` 的簽章**從事件清單抽出**，改成獨立【PRTG 監控訊號】區塊，每條以 `PrtgRuleCatalog` 的規則白話名＋device／sensor 名（取自簽章 `Description`，若無則 objid）呈現；只餵 finding，不餵 hourly 數值（符合 DETECTION-SPEC「AI 只翻譯已確定結論」）。近期歷史區塊沿用既有格式不動。
- 文件：PRTG-SPEC §1 補「finding 進入日風險等級與 AI 敘述」；§3 時序敘述改為「追加以主機紀錄落地為前提、與分析並行」；§9 表格「提升日風險」改為事實；DETECTION-SPEC 風險等級章節補 `prtg:` 基礎代碼。

### 改動
1. **C1**（`EfAnalysisRecordStore.cs`＋`IAnalysisRecordStore`＋`AnalysisOrchestrator` 呼叫端＋測試）：簽章改 `AttachPrtgFindings(hostId, date, findings, aiConfigured)`；重算與重標。
2. **C2**（`AnalysisPromptBuilder.cs`＋`AnalysisPromptBuilderTests`）：PRTG 區塊；事件清單排除 PRTG 簽章。

### 測試／驗收
- C1：低風險紀錄＋`down` finding → 高、basis 含 `prtg:down`、`AiPending=true`（AI 已設定）；AI 未設定 → 不標；`flapping`（Medium）→ 中；既有高風險＋PRTG Medium → 仍高；同一 finding 重複追加 → 風險與 basis 不重複累加；`DetailPruned` → 不追加不重標。
- C2：含 PRTG 簽章的 issues → prompt 有【PRTG 監控訊號】區塊且事件清單無 `PRTG#`；無 PRTG 簽章 → 無該區塊（不留空標題）。
- PRTG-SPEC／DETECTION-SPEC 改寫段落逐條對照實作。

---

## 批次D：守門自動偵測按鈕＋取數範圍設定

### 現況與核對結果
- `PrtgResourceGuardTargets.Resolve`（`:31`）覆寫清單非空即 return（`:38`）；preview（`SettingsController.cs:174-215`）無參數，`source` 依覆寫清單是否為空決定；DTO `PrtgResourceGuardSensorPreviewDto` 已含 objid／device／sensor／category／status／percentage。
- 前端 `Prtg.cshtml:224` textarea `#prtg-guard-sensor-objids`；`:230` 已有「預覽」鈕；`prtg-admin.js:303` `bindGuardPreview`。儲存解析只換行分隔、只驗正整數（`SystemSettingsService.cs:1104-1118`）。
- 觸發式取數三重過濾 `PrtgTriggeredValueFetcher.cs:43-100`；回填 `PrtgBackfillRunner.cs:84-137` 複製一份（差異：用最近一日對應）且已有全量分支 `:66-81`。
- 新設定鍵接觸點範本（`PrtgIgnoreSslErrors`）：`SystemSettings.cs:380`、`SettingsDtos.cs:155/403/476`、`SystemSettingsService.cs:426/619/484/513/650/673/1299`、驗證 `:1080-1140`、`Prtg.cshtml:128`、`prtg-admin.js:107/182`、`PrtgAdminPageUiTests`、`SystemSettingsServiceTests`。

### 定案
- **D1 守門**：preview 端點加 `forceAuto=true` 查詢參數（`Resolve` 加「忽略覆寫」參數）；維護頁守門卡新增「自動偵測並填入」鈕：呼叫 `forceAuto` → 把回傳 objid 一行一個寫進 textarea → 顯示「已填入 N 個，尚未儲存」；偵測結果為空時不清空 textarea、顯示警告。儲存時後端對不在 `lf_prtg_sensors` 的 objid **回警告不擋存**（鏡像可能尚未同步），警告列在回應 `warnings`，前端顯示。
- **D2 取數範圍**：新設定 `PrtgValueFetchScope`（字串列舉：`triggered`（預設，現況）／`all-mapped`／`triggered-plus-list`）＋ `PrtgValueFetchExtraHosts`（主機名稱一行一個，大小寫不敏感，只在第三種模式使用）。
  - `all-mapped`＝當日 `lf_prtg_host_map` 全部 `ok` 的 device；**白名單留空時拒絕儲存此模式**（否則是 42k 全量）。
  - `triggered-plus-list`＝現況觸發主機 ∪ 清單主機（名稱對不到主機主檔的在儲存時回警告、執行時略過並計數）。
  - 新增 `PrtgValueFetchTargetSelector`（Core，暫定名）收斂三重過濾，觸發式取數與回填共用；回填保留「該日無對應退回最近一日」的差異以參數表達。
  - 執行輸出與 Milestone 寫明模式與目標主機數；`all-mapped` 模式仍走輪詢迴圈（分析未完成前就開抓，不必等）。
  - **規模可見**：維護頁的取數範圍欄位旁提供「估算」（新端點 `GET settings/prtg-fetch-scope/estimate?scope=`，回各模式的目標 device 數與 sensor 數，依最新一日對應＋白名單計算）；估算 sensor 數 ≥ 5000（暫定常數）時畫面顯示琥珀警告「一晚可能跑不完，請縮小白名單或改用觸發模式」，**不擋存**。執行輸出同樣印出目標 sensor 數。
  - 指定主機清單以主機名稱存放，執行時對主機主檔解析（不分大小寫、排除已停用與已合併）；解析不到的在執行輸出列出名稱與數量，儲存時也回警告。
  - 順手修 `PrtgTriggeredValueFetcher` 類別註解「昨日」→「目標日」。
- 設定放維護頁「擷取參數」頁籤白名單旁；PRTG-SPEC §3a、§5、§7 補寫。

### 改動
1. **D1**（`PrtgResourceGuardTargets.cs`／`SettingsController.cs`／`SystemSettingsService.cs` 驗證＋`Prtg.cshtml`／`prtg-admin.js`＋三個既有測試檔）。
2. **D2a**（Core：`PrtgValueFetchTargetSelector` 新檔＋`PrtgTriggeredValueFetcher.cs`／`PrtgBackfillRunner.cs` 改用＋測試）。
3. **D2b**（設定鍵全鏈：模型／DTO／服務／驗證／稽核／`Prtg.cshtml`／`prtg-admin.js`／測試／PRTG-SPEC）。

### 測試／驗收
- D1：覆寫清單非空＋`forceAuto` → `source=auto` 且回自動偵測結果；無參數行為不變；UI 測試斷言按鈕 id 與 textarea 寫入函式存在；儲存含未知 objid → 200 且 `warnings` 非空。
- D2a：三種模式各一案例（觸發主機集合、全部 ok device、觸發∪清單）；`conflict` 對應永不納入；白名單為空＋`all-mapped` 在 selector 層也拒絕（雙重防線）。回填用同一 selector 且「退回最近一日」仍成立。
- D2b：存讀往返、null 沿用舊值、`all-mapped`＋空白名單回 400、未知主機名回警告；出廠預設 `triggered`；估算端點三種模式各一案例，`conflict` 對應不計入；稽核 Before/After 含新欄位。

---

## 批次G1／G2：phase 常數化＋PRTG pipeline 抽類

### 現況與核對結果
- phase 裸字串：Core（`AnalysisOrchestrator.cs:1119/1273`、`PrtgFetchService.cs:76/151/195/349`、`PrtgTriggeredValueFetcher.cs:118`、`PrtgResourceGuard`）、Web（`SchedulerRunState.ReportProgress` if/else 鏈＋`StartsWith("prtg-")`）、JS（`runs.js:1002-1009`）；只有三個 `*-done` 有常數。
- `RunPrtgFetchAsync` 275 行（`AnalysisOrchestrator.cs:1086-1360`）五步驟各自 try/catch；`AnalysisOrchestrator` 無專屬測試。

### 定案
- Core 新增 `RunPhases` 靜態類別集中全部 phase 字面值（含 A1 三個子 phase、guard 兩個）；Core 與 Web 一律引用常數；JS 仍維護對照表，但新增 UI 測試：反射列出 `RunPhases` 全部常數，斷言每個都出現在 `runs.js` 的標籤表（同 `RunsPageUiTests` 既有模式），漏一個測試紅。
- `RunPrtgFetchAsync` 抽成 `PrtgDailyPipeline`（Core/Service，暫定名）：建構子收依賴，`RunAsync(day, analysisTask, gate, ct)` 回傳分路結果結構（既有 `BatchRun` 分路欄位的來源）；`AnalysisOrchestrator` 只剩組裝與 `Task.WhenAll`。行為零變更，靠既有 `BatchRunBranchOutcomeTests`／`PrtgTriggeredValueFetcherTests` 與新增的 pipeline 測試守住。

### 改動
1. **G1**（`RunPhases` 新檔＋全部引用點替換＋`RunsPageUiTests`）。
2. **G2**（`PrtgDailyPipeline` 新檔＋`AnalysisOrchestrator.cs` 縮減＋`PrtgDailyPipelineTests` 新檔）。

### 測試／驗收
- G1：grep 全 Core／Web 無 `"prtg-sync"`、`"netiq"`、`"local"`、`"guard-paused"` 等裸 phase 字串（測試檔除外）；反射對照測試綠。
- G2：`AnalysisOrchestrator.cs` 少於 1250 行（暫定）；PRTG 停用／同步失敗／規則庫無規則三種路徑的分路結果與 gate 呼叫與 B1 一致。

---

## 批次F：排程作業頁重排

### 現況與核對結果
- `Runs.cshtml:5-190` 單一巨卡：左欄設定（取數／AI／PRTG 三小節以 `<hr>`＋`h6` 分隔）、右欄卡中卡（取數狀態＋`<hr>` AI 狀態＋`<hr>` PRTG 回填含 `rows=12` textarea）；`:195` 才是頁籤；`:203-212` 手刻工具列；7 處重複 inline style；10 處手寫 help 按鈕樣板；殘留註解 `:127-130`。
- 其他維護頁骨架：`nav-tabs mb-3` 置頂 → `[data-panel]` → 平行 `lf-card`；`bindTabs` 要求頁籤與面板同層手足（WEB-SPEC §9.10 踩坑）。
- 可用共用元件：`statCard`／`labelValue`／`headerWithHelp`／`.lf-toolbar` 家族／`.lf-card--critical|warning|ok`；本頁皆未用。無本頁專屬 CSS。
- `runs.js` 1558 行，`:602` 起為排程段，模組層可變狀態 17 個。

### 設計依據（ui-ux-pro-max）
- 產品型別對應「Status Page／Incident Management」：Data-Dense＋Trust；儀表風格「Real-Time Monitoring」；狀態色 綠／紅／琥珀＋中性底——與 DESIGN-SYSTEM v2（Data-Dense × Swiss、企業藍＋琥珀、`--lf-risk-*` 語意色）一致，**不引入新色與新字型**。
- UX 規則採用：預留非同步內容高度避免跳動（狀態卡輪詢時尺寸固定）、標題層級連續（h2 卡標→h3 小節）、字級沿用既有階、動效維持 Subtle（`--lf-transition`，不加卷動動畫）、觸控目標 ≥ 44px、圖示一律 SVG sprite。

### 定案：版面骨架
```
[狀態列]  三張等寬 lf-card（grid，≤768px 疊直）
   ├ 取數執行：狀態徽章／觸發來源／開始・已耗時／三條進度軌／最新訊息／[立即執行][停止]
   ├ AI 分析：狀態徽章／閒置原因（A3）／待補件數／進度軌／最新訊息／[立即補跑][停止][強制重新分析]
   └ PRTG 歷史回填：狀態／將回填 N 天／兩級進度／[開始回填]；輸出改成可收合「詳細輸出」（預設收起）
[nav-tabs 置頂]  執行總表 ｜ 異常彙總 ｜ 執行紀錄 ｜ 排程設定
   ├ 執行總表／異常彙總／執行紀錄：面板內沿用既有表格；圖例＋天數列改用 .lf-toolbar
   └ 排程設定：三張平行 lf-card（取數排程／AI 分析排程／PRTG 擷取（總開關＋指路維護頁））
              ＋單一儲存列（儲存排程設定／更新時間）
```
- 狀態卡用 `.lf-card--ok|warning|critical` 依狀態上色（執行中＝ok 藍系不上色、暫停＝warning、上次失敗＝critical），`labelValue` 取代手刻標籤／值；`headerWithHelp` 取代 10 處樣板。
- 進度軌高度、窄輸入寬度抽成 `site.css` 的 `.lf-run-progress`／`.lf-input-narrow`，移除全部 inline style。
- 頁籤與四個面板維持同層手足；`hash` 深連結沿用 `bindTabs` 既有能力（`#settings` 可直達設定）。
- `runs.js` 拆為 `runs.js`（頁籤三表＋頁面入口）、`runs-status.js`（三張狀態卡＋輪詢＋計時）、`runs-schedule.js`（設定面板＋立即執行 modal＋強制重跑 modal）、`runs-prtg-backfill.js`（回填卡）。共用狀態以一個小型 store 物件傳遞，不用全域變數。
- 移除殘留註解與已無 DOM 的「子進度」說明。
- 權限：`data-maintain-only` 規則不變；DevMonitor 仍可看四個頁籤、但設定面板唯讀（控制項 `disabled`＋頂端一行「僅 Maintain 可修改」，不是整塊隱藏——看得到設定值本身就是 DevMonitor 的需求）。
- **誠實的狀態文字**：進度軌「已完成 x / y」在該路有失敗時改為「已完成 x / y（失敗 n）」（分路失敗數狀態 API 已有）；PRTG 路徑停用時第三軌不顯示、PRTG 回填卡顯示「PRTG 未啟用」並指路設定頁籤；三張狀態卡在無資料時各有明確空狀態（「尚未執行過」／「無待補」），不留空白。
- **執行總表日期語意**：列日期旁加一次性說明（`headerWithHelp`）：「D 日的列＝D 日夜間執行、分析 D-1 的資料」——這是本頁最常被誤讀的一點，E 移除今天列後仍要講清楚。
- 狀態卡尺寸固定（進度軌區塊預留高度），輪詢更新不引起版面跳動。

### 改動
1. **F1**（`Runs.cshtml`＋`site.css`＋`RunsPageUiTests`）：骨架與樣式；斷言頁籤／面板同層、四個 `data-panel`、無 inline `style=` 於 Runs.cshtml、三張狀態卡 id。
2. **F2**（`runs.js` 拆檔＋`_Layout`／頁面 script 引用＋`RunsPageUiTests`）：行為零變更；A3 `idleReason` 文案對照表；A1 子 phase 標籤。
3. **F3**：瀏覽器實測（Claude 自行以 Browser 工具走查：三頁籤、設定儲存、立即執行 modal、回填卡收合、≤768px、字級三檔）；截圖附執行紀錄。

### 測試／驗收
- `RunsPageUiTests`：骨架斷言如上；`PROGRESS_PHASE_LABEL` 覆蓋 `RunPhases` 全部（G1 測試）；`idleReason` 六值皆有文案。
- 既有 `bindTabs` hash 行為：`/runs#settings` 直達設定面板。
- DESIGN-SYSTEM §6 交付前檢查表逐項走查（對比、focus、reduced-motion、768px、字級三檔）。

---

## 批次G3／G4：RunState 基底類別＋手動觸發佔用窗口可見性

### 現況與核對結果
- `SchedulerRunState`（270 行）、`AiAnalysisRunState`（189）、`PrtgProbeRunState`、`PrtgBackfillRunState : PrtgProbeRunState`、`NetiqProbeRunState`（76）各自實作 `IsRunning`／`TryBeginRun`／`TryCancel`／`EndRun`／lock／CTS 生命週期。
- `SchedulerRunState` 三組 (Phase, Done, Total, Completed) 欄位，`TryBeginRun`／`EndRun` 各手動重設 15 欄。
- AI 處理迴圈 `ReportProgress(totalDone, _runState.ProgressTotal, …)` 在並行分片下互相覆蓋。
- 手動觸發不受窗口 End 停止（WEB-SPEC 明文）；手動大回補佔住 gate 時，`TickAsync` 開頭 `IsRunning` 直接 return，該窗口的排程觸發靜默消失。

### 定案
- 新增 `BackgroundJobState` 基底（Web/Services，暫定名）：gate（併發 1）、`Trigger`／`StartedAt`／`TryCancel`／`EndRun`、`LatestMessage`；五個 RunState 改繼承，各自只留專屬欄位。`PrtgBackfillRunState` 仍繼承 `PrtgProbeRunState`（既有），後者改繼承基底。
- `SchedulerRunState` 三組進度欄位改為 `ProgressTrack` 值型別 ×3（`Phase/Done/Total/Completed`），重設收斂到一個 `Reset()`；status API 欄位名**不變**（DTO 映射層攤平）。
- AI 進度：分片內以 `Interlocked`／鎖更新 `Done`，`Total` 只在批次開頭設定一次。
- 手動觸發佔用：維持手動不受窗口 End 停止；但排程 tick 因 `IsRunning` 略過時，若當前執行是手動觸發且本窗口尚未觸發過，寫一則 Milestone 進**該手動執行**的執行紀錄（「排程窗口 HH:mm 的自動觸發已被本次手動執行佔用，將於下一窗口補跑」）並在狀態卡「下次觸發」旁顯示同一句（`SchedulerRunState.SkippedScheduleAt`，status API 新欄位）。每個窗口只寫一次。

### 改動
1. **G3**（基底類別新檔＋五個 RunState＋`ScheduleController` DTO 映射＋`SchedulerRunStateTests`／`AiAnalysisSchedulerTests`）。
2. **G4**（`SchedulerHostedService.cs`＋`SchedulerRunState.cs`＋`runs-status.js`＋測試）。

### 測試／驗收
- G3：既有 `SchedulerRunStateTests` 全綠；status API JSON 欄位名與改前逐一相同（反射比對測試）；AI 併發 4 分片下 `ProgressTotal` 不變、`Done` 單調。
- G4：手動執行中、窗口起點到達 → tick 略過且 Milestone 一次、`SkippedScheduleAt` 有值；同窗口第二次 tick 不重複；排程觸發的執行中不寫。

---

## 升級與營運影響（管理者視角）
- **零 schema 變更**：本輪不新增資料表或欄位；D2 兩個設定鍵走 `SystemSettings` 既有 blob，預設值＝現況行為。
- **API 相容**：`status`／`ai-status` 只新增欄位（`idleReason`、`skippedScheduleAt`），既有欄位名不變（G3 有反射比對測試）；preview 端點新增可選參數。
- **行為變化清單**（升級說明用）：執行總表不再有今天列；AI 分析在取數期間即開始（PRTG 就緒後）；PRTG `down`／`flapping`／`warning`／`silent` 命中會提升當日風險並可能觸發 AI 補跑——**升級後第一晚風險日數可能上升**，這是規格原本承諾但未生效的行為，文件明寫；messages 同步變快。
- **資源**：AI 與取數並行會同時對資料庫與 AI 端點施壓；既有 `AiConcurrency`（1~8）可調低，狀態卡的閒置原因讓管理者看得出是誰在等誰。
- **稽核**：新設定鍵進既有設定稽核；守門「自動偵測並填入」本身不寫稽核（未儲存），儲存時走既有稽核。
- **文件同步清單**：WEB-SPEC §9.9e（守門鈕、取數範圍、估算）、§9.10（骨架、總表口徑、閘門、閒置原因、佔用提示）；PRTG-SPEC §1、§3、§3a、§5、§7、§9、§12；DETECTION-SPEC 兩個獨立排程、風險等級基礎代碼；DESIGN-SYSTEM §7 落地對應補排程頁；CLAUDE.md 測試基線；BACKLOG 新增「跨來源關聯（P4 (c)）」與其前置說明。

## 明確不做（本輪定案）
- **規則引擎層合併 Windows／Linux／PRTG（P4 (a)）**：與 PRTG-SPEC §1、BACKLOG 定案衝突，三種判定輸入型別無共通抽象，規則表單無法呈現複合條件。
- **跨來源關聯規則（P4 (c)）**：需 PRTG 評估時序前移進 `LogAnalysisService`＋按 hostId／日期的 PRTG repository 方法，留下一輪；本輪 B／C 是其前置。
- **AI 閘門改為完全不等 PRTG**：會讓 AI 敘述看不到當日 PRTG finding，與 C 矛盾。
- **結構同步套 `PrtgFetchConcurrency`**：分頁必須循序。
- **執行總表為 PRTG 另闢一區**：沿用 R38 定案。
- **深色模式、新色、新字型**：DESIGN-SYSTEM v2 已定，本輪只調版面。

## 複檢（規劃完成後）
- 與既有設計衝突：PRTG-SPEC §1「規則各自歸屬來源」——C 只在主機層合成風險，不違反；§3「finding 追加必須在觸發式取數之後」——B 推翻，理由：前提是紀錄落地而非取數完成，文件同步改。WEB-SPEC §9.10「總表每日一列含今天」——E 推翻，文件同步改。DETECTION-SPEC 閘門敘述——B 改寫。
- 批次間衝突：A1／B1／C1／G2 都動 `AnalysisOrchestrator.RunPrtgFetchAsync`，順序 A→B→C→G2 且 G2 抽類時以當時行為為準；A3 後端與 F 前端分離，A 批次先補 `PROGRESS_PHASE_LABEL` 三筆避免裸字串；F2 拆檔與 G4 改 `runs-status.js`，G4 排在 F 後。
- 四個坑：B「什麼算完整」已寫（PRTG 停用／失敗算就緒）；C「只升不降」與「重複追加不累加」已列反例；D2「白名單為空＋all-mapped」單向閘門有雙重拒絕；E 分母＝`days` 不變。
- 移除類：G2 抽類會移除 `RunPrtgFetchAsync` 私有方法，唯一呼叫端 `RunAsync`；G1 常數化不移除任何公開成員。
- 升級路徑：D2 新設定鍵預設 `triggered` 零行為變化；C1 對既有紀錄不回溯重算（下次追加才生效）；B 對舊版無影響。
- 複檢完成，新增事項：A 批次先補前端標籤三筆（已寫入 A 驗收）。
- 第二次複檢（四視角）新增：B 改為寫入路徑追加＋一次補追加（原輪詢式在重跑覆寫與案件掛接兩點有洞）；C 補案件掛接、報告、不回溯三條；A 移除拍腦袋逾時、每日擷取固定 `7days`；D2 加估算端點與規模警告；F 加唯讀呈現、誠實狀態文字、日期語意說明、固定尺寸；新增「升級與營運影響」節。

## 執行紀錄
> 基準 commit：`eb1434c`（規劃文件）；測試基線實測 3497 綠／略過 6。
> 委派模型：`gemini-3.8-flash-high`（Gemini 組週配額於開工時僅剩 4%，2026-09-11 重置）。
> 額度耗盡後由 Claude 接手，**不切換到 Claude/GPT 組**（使用者定案）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| E-step1 執行總表移除今天列 | agy | 通過（`d3a4439`） | 三檔白名單內；全套 3499 綠（+2）；突變（錨點改回 `Today`）7 紅，測試有效；BOM 與基線一致、無 NUL；controller／前端／errors／list 未動 | 無。`windowDays` 需 +1 涵蓋本頁最舊日，規格已預先寫明，agy 有做對 |
| A-1/2/3 PRTG 同步可觀測＋messages 日期過濾＋AI 閒置原因 | Claude | 通過 | 全套 3511 綠（+12）；三個突變各自見紅（級距 7days→30days 2 紅、`SetIdleReason` 寫 null 4 紅、分頁進度回報短路 2 紅）；八個檔案 BOM 與基線一致、無 NUL | agy 的 Gemini 週配額在批次 E 後剩 2%，跑不完一段且中途斷線的半套 diff 成本更高，依定案改由 Claude 自做（**不切換 Claude/GPT 組**）。三處偏離規劃並已改文件：①「每 50 頁一則 Milestone」改為執行輸出（`IRunConsole`），PrtgFetchService 沒有 recorder，為此加相依屬過度設計，效果相同；②「每日固定 7days／回填依回填天數」改為統一依「目標日距今天數」取級距，每日擷取自然落在 7days，少一條分歧；③ `waiting-fetch` 閒置原因留到批次 B（現在加＝沒有消費端） |
| B-1/2 AI 與取數並行（PRTG finding 就緒閘門） | Claude | 通過 | 全套 3523 綠（+12）；兩個突變見紅（就緒分支短路 3 紅、追加不併入記憶體 2 紅）；十五個檔案 BOM 與基線一致、無 NUL | 兩處實作決定與規劃略有出入並已寫進文件：① 就緒訊號走既有 `IRunProgress`（新 phase `prtg-findings-ready`）而非新增 `IPrtgFindingsGate` 介面，與既有 `*-done` 完工訊號同型，零新參數；② 登錄簿未提供時用 `PrtgFindingsRegistry.Empty`（已就緒且無 finding）而非 nullable，消費端因此沒有永不執行的 null 分支。另 `AttachPrtgFindings` 提升到 `IAnalysisRecordStore` 介面（NetIQ 路徑手上只有介面型別；唯一實作，安全）。踩到的坑：閒置原因 `waiting-fetch` 一開始設在 `TickAsync` 開跑前，被隨後的 `TryBeginRun` 清掉，改設在背景執行 `EndRun` 之後才正確 |
| C-1/2 PRTG finding 上調日風險＋AI prompt 獨立區塊 | Claude | 通過 | 全套 3533 綠（+10）；三個突變見紅（`MoreSevere` 改直接覆寫 1 紅、prompt 不排除 PRTG 2 紅、第一版該測試對突變無力已補強）；十二個檔案 BOM 一致、無 NUL | 規劃寫「`flapping` 拉到中」有誤，實際上 Medium 嚴重度依既有 `ComputeRuleBasedRisk` 語意本就不拉風險（只有 High 才拉到中），只有 `down`（High＋`ElevatesDayRisk`）會拉高——已改測試預期與文件對照表，不改判定語意。踩到的坑：prompt「不混進事件清單」的第一版斷言檢查「區塊之前不含 PRTG」，但事件清單排在區塊之後，突變存活；改斷言 `AppendIssue` 的實際輸出格式 `PRTG/` 才抓得到 |
| D-1/2 守門自動偵測按鈕＋取數範圍設定 | Claude | 通過 | 全套 3554 綠（+21）；兩個突變見紅（`Normalize` 不退回預設 2 紅、白名單守門短路 2 紅）；JS 以 `node --check` 驗語法；十六個檔案 BOM 一致、無 NUL | 踩到兩次「反斜線被 bash→python 管線吞掉」（`join('\n')` 寫成真換行斷行），改用 Edit 工具修；HOST.md 已記載這個坑，往後 JS 字串逸出一律避開 heredoc |
| G1/G2 phase 常數化＋PRTG pipeline 抽類 | Claude | 通過 | 全套 3556 綠（+2）；正式碼 grep 裸 phase 字串零命中；突變（前端標籤表刪一筆）1 紅；`AnalysisOrchestrator` 1568→1236 行、`PrtgDailyPipeline` 356 行；行為零變更（既有測試全綠、無新增行為測試） | 反射對照測試一寫就抓到真缺口：`local`／`netiq` 在單位表沒有項目。查前端後確認那是刻意的（fallback 為「主機日」正確），改成只對 `prtg-` 開頭的 phase 要求單位——它們 fallback 成「主機日」才是錯的。抽類連帶把 `PrefixedRunConsole` 從 orchestrator 的 private 巢狀類別提為檔案層級 internal（兩條路徑共用） |
| F-1/2/3 排程作業頁重排 | Claude | 通過 | 全套 3559 綠（+3）；`node --check` 驗 JS；瀏覽器實測：桌面 1440 無水平溢出、三卡高度 280/265/250、平板 768 正確疊直單欄、設定頁籤切換時工具列隱藏、主控台零錯誤；`Runs.cshtml` 已無 inline style | 「狀態文字初值為載入中」的既有測試綁在被移除的 `schedule-status-text` 上，改指向新載體（取數與 AI 兩張卡各一個），意圖不變。全套第一次跑有一條 `SentinelRestDirectoryClientTests` 紅，單獨重跑與再次全跑皆綠——時間敏感測試在並行負載下的偶發逾時，與本輪改動無關 |
| G3/G4 進度欄位收斂＋手動佔用窗口可見性 | Claude | 通過 | 全套 3564 綠（+5）；兩個突變見紅（ResetTracks 漏一軌 3 紅、分子改無條件覆寫 1 紅）；status API 欄位名未變（既有測試全綠） | **推翻規劃的「五個 RunState 抽共通基底」**：實際讀過四個類別後，共通部分只剩「一個 bool 與兩個時間戳」——兩個 probe state 沒有 CTS 也沒有 Trigger，`EndRun` 參數與 `Snapshot` 型別各不相同，抽出來會是個空殼，呼叫端仍要各自處理鎖與重設，屬於規格沒要求的抽象層。改為做規劃裡真正解決風險的部分：三軌進度收成值型別＋單一 `ResetTracks()`（消除兩處 12 欄手動同步），這才是「漏一個就殘留舊進度」的實際成因。AI 進度併發覆蓋一併修掉，測試用語意斷言而非壓力測試 |
