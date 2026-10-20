# 第 55 輪規劃：企業級體質——排程韌性、交辦兩段式摘要、效能

> 狀態：**實作中**（feature/feedback-55；§0 待確認項 2026-10-20 使用者以「開始實作」照建議值定案）；第 56 輪規劃見 [FEEDBACK-56-PLAN.md](FEEDBACK-56-PLAN.md)，於本輪併 dev 後開工。
> 基準：dev@fcb54d2a（6,241 通過／10 略過，Windows）
> 來源：使用者 2026-10-20 要求以六個角度評估整體優化（排程三源順序穩定度／PRTG 實戰／告警可靠度／報表與運作速度／派工與回報流程／畫面排版）。評估結果見 §1；本輪只做角度 1～4 的後端部分，角度 5～6 另開第 56 輪。
> 實作方式：agy `gemini-3.8-flash-high` 為主，額度不足或兩次無聲零改動時改派 `impl-low`，接手起點記進 §8 執行紀錄；每段 Claude 獨立驗收（`git diff`、重跑驗收指令、關鍵斷言突變），不採信執行端摘要。
> 第二版改了什麼（審視後）：A-1 加跨行程互斥檢查與冪等說明；A-2 以結構化旗標取代字串前綴判定、補 AI 閘門縫隙說明與畫面指路；C-1 outbox 改獨立 store 且只存識別不存內容、寄送時渲染、容量上限、同 Tick 多時刻合併；C-4 新增「立即通知」選項（待確認）；D-4 驗收改為有資料＋ANALYZE 後看計畫、允許 covering index；§7 新增驗證環境與每段共同驗收；明確不做補兩條。

## 0. 使用者已定案與待確認

**已定案（2026-10-20）**
1. 排程韌性三缺口（中斷夜補跑、PRTG 缺日補評估、AI 跟隨持續喚醒）都做。
2. PRTG 介面語系為英文：`type_raw` 鍵化本輪不做，記 BACKLOG 觸發條件。
3. UI 工作套用 `ui-ux-pro-max`（第 56 輪）。
4. 派工與回報流程依實際使用情境重整（第 56 輪）。
5. 交辦通知改為**每天兩封摘要**：上午派的工 12:00 寄、之後派的工隔天 08:00 寄（本輪批次 C）。
6. `scripts/` 下 833 MB 未追蹤產物：核對為 2f201217 已移除的第 53 輪驗收基礎設施殘留（bin／obj／執行輸出／站台複本），無任何現行檔案或文件引用 → 開分支前整個刪除（§7）。
7. 依建議拆兩輪；驗收條件要明確可由 Claude 驗收。

**原待確認項（2026-10-20 全數照建議值定案）**
- **D-1 低風險日與狀態篩選**：帶處理狀態篩選時，候選集建議先在 SQL 端限縮為可行動等級（高／中），只有使用者明確勾選「低」才含低風險日。現況低風險日在推導裡因問題不計入會退回日層級狀態（多為 `open`），所以「未處理」篩選會把低風險日一起列出來。**問題：這是刻意的還是副作用？** 建議改為限縮（見 §6 D-1）。
- **C-3 上報信**：處理人標「無法處理」寄給 admin 群組的上報信維持即時單發（它是人為求助，不是派工）。建議維持。
- **C-4 立即通知選項**：交辦 modal 與改派對話框加一個預設不勾的「立即通知處理人」勾選；勾了的項目在下一次排程輪詢（≤60 秒）就寄，不等時刻。給真的急的單一個出口，其餘仍走兩封摘要。建議做（改動小，見 §5 C-4）。
- **A-1 補跑上限**：同一個排程窗口實例內，異常中斷只補跑一次（第二次中斷不再補，避免站台反覆崩潰時無限重跑）。建議採用。

## 1. 六角度評估結論（核對後）

| 角度 | 結論 | 本輪處置 |
|---|---|---|
| 1 排程三源順序 | 架構正確（本機 ∥ NetIQ ∥ PRTG，finding 發佈後 AI 跟隨；匯合、閘門、競態保護齊全）。缺口全在「中斷之後」：中斷夜不補跑、PRTG 缺日永不補評估、AI 跟隨只喚醒一次。另兩處文件過時 | 批次 A |
| 2 PRTG 實戰 | 功能完整；`type` 顯示名鍵化在英文語系下無害；門檻暫定需校準期；單一 core server | A-5 小項＋BACKLOG |
| 3 告警可靠度 | 判定層成熟（五層確定性、只升不降、抑制四型）；投遞層中等：交辦／上報信失敗不落地不重寄、處理人無 email 派工者不知道 | 批次 C |
| 4 速度 | 主路徑已 SQL 聚合；狀態篩選慢速路徑全撈保留期、報表非全部範圍雙期撈回（前端逾時 180 秒）、背景服務與前景共用連線池、索引未經查詢計畫驗證 | 批次 D |
| 5 派工回報 | 四入口全走 `WorkOrderCoordinator`；22 條 UX 斷點（403 預檢、人選未過濾、change 即送出、取消指派不收單、狀態清單五份、管理者看不到回覆） | 第 56 輪 |
| 6 排版 | token／圖表／表格出口統一；缺頁首元件、14 處自建表格、57 處 inline style、modal／label 無障礙、側欄對比 3.8:1、無麵包屑（規格有寫） | 第 56 輪 |

## 2. 核對表（本輪契約依賴的事實）

| 項目 | 結果 | 證據 |
|---|---|---|
| 中斷的排程執行仍算「已觸發」 | ✅ 成立 | `GetScheduleTriggerTimes` 取 Trigger==schedule 的 StartedAt（SchedulerHostedService.cs:249-262）；`WindowAlreadyTriggered` 只比時間（ScheduleCalculator.cs:185）；`FindMissedWindow` 條件 4「沒有任何一筆 ≥ 起」被中斷列擋下（:228） |
| 站台重啟時沒有人關閉 FinishedAt 為空的列 | ✅ 成立 | grep `FinishedAt == null` 只有 `BatchRunStatus.Compute`（BatchRunStore.cs:379）；畫面 6 小時後才顯示 stuck（RunMonitorService.cs:26） |
| 執行紀錄是 append-only、同 RunId 最後一行為準 | ✅ | `FinishRun` 即 `AppendRunLine`（BatchRunStore.cs:218-224）、讀取走 `LatestPerRun`（:244）——關閉中斷列＝再追加一行 |
| 跨行程互斥 | ✅ 可用 | `NamedMutexGate("Global\LogForesight")`（NamedMutexGate.cs:23）；IIS 重疊回收時新舊行程可能短暫並存 |
| 排程只評估昨天的 PRTG | ✅ 成立 | `BuildPrtgDays` 非 Full 或無回望都回 `today-1`（AnalysisOrchestrator.cs:1320-1345） |
| 被取消的 PRTG 日有留痕 | ✅ 成立 | `PrtgDayStat(Outcome=partial, Note="未完成評估：…")`（PrtgDailyPipeline.cs:806-820、899-905）；整段失敗為 `failed`（:928-934）；當機則**沒有** PrtgDays |
| AI 完整性閘門只看天數不看最早日 | ✅ | `PrtgDateRange` 送 `done=天數`，`PrtgRangeStart = Today − max(1, done)`（SchedulerRunState.cs:347-351）；非連續多日時較舊日期不被擋 |
| Web 沒有消費 PRTG 腿 Waiting／Overdue | ✅ 成立 | grep `Overdue` 只有交辦與處理狀態；`PrtgDayStat.Note` 只進執行總表 `prtgNote` 與執行詳情（RunMonitorService.cs:125-149、386-396） |
| AI 跟隨只在 finding 發佈時喚醒一次 | ✅ 成立 | `PrtgFindingsBecameReady` 只在 false→true 發（SchedulerRunState.cs:344-370）；`TickAsync` 窗口外直接回（AiAnalysisHostedService.cs:189-193）；迴圈 `rawPending.Count==0` 即結束（:338-342） |
| 本機路徑已走統計＋AiPending | ✅ 成立 | `AnalyzeDayStatisticalAsync(useAi: localUseAi)`、`hadAiWorkItem = record.AiPending`（AnalysisOrchestrator.cs:975-978）；DETECTION-SPEC「適用範圍」段過時 |
| 取數執行內仍有同步 AI 呼叫 | ✅ 成立（週體檢） | `WeeklyCheckupService.RunAsync` → `ChatJsonAsync`（WeeklyCheckupService.cs:126），在 `RunAsync` 匯合後呼叫（AnalysisOrchestrator.cs:674-679）；DETECTION-SPEC「沒有任何同步 AI 呼叫」句過時 |
| 交辦信現行四個即時寄送點 | ✅ | `WorkOrderCommandService.NotifyHandler`（:630-646，Create／Reassign／Split／Cancel 呼叫）、`DayHandlingCommandService`（:232-256）、`WorkOrderBoardService.RunAutoDispatch`（:351-382）；夜間摘要 `NotifyWorkOrderDigestAsync`（MailNotificationService.cs:504） |
| 「說明：Hosts」 | ✅ 缺陷 | WorkOrderBoardService.cs:363、:376 把 `WorkOrderScopes.Hosts` 傳進 `PlainExplanation` |
| 規格 vs 實作：日層級指派與立即派工不寄建單信 | ❌ 不符 | WEB-SPEC §9.4b:1547 寫不寄；兩處都在寄。批次 C 改成全部入摘要後此句改寫 |
| 交辦／上報信失敗無重寄 | ✅ 成立 | `SendSafeAsync` 失敗只 log（:2230-2255）；只有 urgent 有 `RetryPendingUrgentAsync` |
| 持久化郵件狀態的容器與上限 | ✅ | `MailNotifyState`（blob `mail_notify_state`）單次變更上限 4 MB（MailNotifyStateStore.cs:12）——outbox 若存主機名清單會撐爆，故獨立 store 且只存識別 |
| 每日／每週摘要的到點判定 | ✅ 可比照 | `CheckAndSendDailyWeeklyAsync`：`now.TimeOfDay >= T && LastXxxSentDate != today`（:326-360），由 SchedulerHostedService 每 60 秒 Tick 呼叫 |
| 記錄查詢慢速路徑 | ✅ 成立 | `needsHandlingFilter` 時 `QueryLightweight(filter)` 整批撈回再記憶體篩選分頁（RecordListQueryService.cs:93-186）；低風險日推導退回日層級狀態（DayStatusRule.Resolve，IssueExclusion.cs:147-153） |
| 報表非全部範圍 | ✅ 成立 | 本期＋前期各 `QueryLightweight`（已限高／中）再 `FilterByScope` 記憶體過濾（ReportService.cs:127-134、HandlingHistoryQueryService.cs:119-160）；範圍值只有 `all／unresolved／open／unassigned`，皆依推導 |
| 背景服務共用站台連線池 | ✅ 成立 | 四支 hosted service 注入 Singleton `StorageBackend`（ServiceCollectionExtensions.cs:34）；只有 `AnalysisOrchestrator` 自建 `StorageBackend(…, AnalysisMaxPoolSize)`；SQLite 下 `Pooling=false`、`busy_timeout=5000`、WAL（SqlitePragmaInterceptor.cs:28-35）；專案尚無 keyed DI 先例 |
| 索引清單 | ✅ 有文件無守門 | DB-SPEC §索引（:367-412）無任何查詢計畫測試 |
| 本機可跑站台做瀏覽器實測 | ✅ | `.claude/launch.json` 的 `web`（port 5041）；`Auth:Provider=Stub` 且非 Production 時 `demo-admin` 免密碼 |
| scripts/ 未追蹤 | ✅ 殘留 | 2f201217 刪除了 `scripts/build-identity`、`scripts/prtg-workload`、第 53 輪驗收檔；現存 `scripts/round53-user-matrix/runs/...`（站台複本 833 MB）、`prtg-workload/storage-generator`（59 MB）、`build-identity/bin|obj` 皆無引用 |

## 3. 批次總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A 排程韌性 | A-1 中斷執行偵測與補跑；A-2 PRTG 缺日補評估＋待補呈現；A-3 AI 跟隨持續喚醒；A-4 文件校正；A-5 PRTG 狀態卡顯示上一趟逐日原因 | 中 | 無 | 1 |
| C 交辦兩段式摘要 | C-1 交辦通知 outbox 與兩時刻摘要寄送；C-2 無 email 回饋；C-3 規格同步；C-4 立即通知選項（待確認） | 中 | 無 | 2 |
| D 效能 | D-1 狀態篩選路徑限縮與上限守門；D-2 報表非全部範圍一次查詢；D-3 背景服務獨立連線池；D-4 查詢計畫守門與補索引 | 大 | A-3 先於 D-3（同檔） | 3 |

批次間檔案交集：A 與 C 都動 `SchedulerHostedService.TickAsync`（A 在啟動段、C 在寄送段）；A-3 與 D-3 都動 `AiAnalysisHostedService` 建構與條件。順序 A → C → D，後者以前者結果為基準。各批次可獨立 commit 與回滾。

## 4. 批次 A：排程韌性

### A-1 中斷執行的偵測與補跑

**現況與核對**：見 §2 前四列。IIS 回收、當機、斷電都會留下 FinishedAt 為空的列；`ApplicationStopping` 只在正常關閉時 `TryCancel`（SchedulerHostedService.cs:70）。

**定案**
- 站台啟動時（`SchedulerHostedService.ExecuteAsync` 10 秒延遲後、第一次 Tick 前）**先以 0 逾時嘗試取得 `Global\LogForesight` 具名互斥**：拿不到＝另一個行程仍在跑（IIS 重疊回收），本次不關閉任何列、log 一行；拿到才關閉並立即釋放。關閉＝對所有 FinishedAt 為空的 `BatchRun`（取數與 AI 都算）追加一行：FinishedAt＝現在、ExitCode＝新常數 `BatchRun.ExitCodeInterrupted`（暫定 2）、新布林欄 `Interrupted=true`（JSON 缺欄容忍，舊列為 false），並補一筆 Milestone「站台重啟時發現此執行未正常結束，標記為異常中斷」。
- `BatchRunStatus` 新增 `Interrupted`，`Compute` 在 FinishedAt 非空且 `Interrupted` 時回它，優先於 Stopped／ExitCode 判定。執行總表把 Interrupted 與既有 Stuck 一起計入「異常中斷」計數（語意相同：沒有正常結束）；執行紀錄頁籤狀態文案「異常中斷」；`runs.js` 的 `STATUS_META` 補對應。
- 補跑：觸發時間清單改由純函數 `ScheduleCalculator.EffectiveScheduleTriggerTimes(runs, inMemory)` 產生——一個窗口實例內**第一筆** Interrupted 的排程執行不計入觸發時間，第二筆起照常計入。既有 `ShouldTriggerNow`（仍在窗口內）與 `FindMissedWindow`（窗口結束 4 小時內且 AutoCatchUp 開）因此自然補跑，不新增第三條觸發路徑。補跑的 `RunRequest.CatchUpNote`＝「補跑：上一趟排程執行（HH:mm 開始）異常中斷」，使用者在執行紀錄與狀態卡都看得到為什麼又跑了一次。
- 補跑的冪等性依賴既有機制、不新增：NetIQ 以 `HasRecord` 跳過已完成主機日、PRTG finding 以 EventKey 去重、AI 以 `ai_pending`、郵件以 `SummarySentKeys`／`UrgentSentKeys` 去重。
- 手動觸發的 Interrupted 列只關閉、不補跑。AI 的 Interrupted 列只關閉（AI 進度本來就在 `ai_pending`）。

**改動**
1. `BatchRun`／`BatchRunStore`：`Interrupted` 欄、`ExitCodeInterrupted` 常數、`CloseInterruptedRuns(DateTime now, Action<BatchRun> onClosed)`。
2. `BatchRunStatus.Compute` 與 `RunMonitorService` 計數／DTO；`runs.js` 文案；WEB-SPEC §9.10 狀態一覽補「異常中斷（重啟時關閉）」。
3. `ScheduleCalculator.EffectiveScheduleTriggerTimes`；`SchedulerHostedService.GetScheduleTriggerTimes` 改呼叫它（全部呼叫點：`TickAsync` 兩處、`NoteSkippedScheduleWindow` 一處）。
4. `SchedulerHostedService.ExecuteAsync` 啟動段：互斥試取 → 關閉 → log 關閉筆數。`NamedMutexGate` 補 `TryRunExclusive(TimeSpan.Zero, action)` 多載（若已有等價方法則沿用，不新增）。

**驗收**（Claude 執行）
- `ScheduleCalculatorTests` 新增 ≥4 案：(a) 窗口內一筆 Interrupted → `ShouldTriggerNow` 為 true；(b) 窗口內兩筆 Interrupted → false；(c) 一筆 Interrupted 加一筆正常 → false；(d) 窗口結束 2 小時、該窗口只有 Interrupted → `FindMissedWindow` 回該窗口。突變：把「第一筆不計入」改成「全部不計入」後 (b) 必須紅。
- `BatchRunStore` 測試：關閉後 `GetRecentRuns` 讀回 `Interrupted=true`、`Compute` 回 `interrupted`；舊 JSON（無該欄）反序列化為 false；關閉兩次不重複追加（第二次 0 筆）。
- `SchedulerHostedService` 測試（替身互斥）：互斥被占用時 `CloseInterruptedRuns` 不被呼叫。
- `RunsPageUiTests` 反射核對 `STATUS_META` 含 `interrupted`。
- `dotnet test --filter "FullyQualifiedName~ScheduleCalculator|FullyQualifiedName~Scheduler|FullyQualifiedName~RunMonitor|FullyQualifiedName~BatchRun"` 全綠，測試總數比基準多 ≥7。

### A-2 PRTG 缺日補評估與待補呈現

**定案**
- `PrtgDayStat` 新增布林 `NeedsReevaluation`（缺欄容忍）：`PrtgDailyPipeline` 在「作業取消」與「設定變更中斷」兩個 catch 寫逐日結果時設 true；整段 `failed` 的逐日結果也設 true。**判定不再依賴 Note 字串**。
- 新純函數 `PrtgReevaluationPlanner.Select(IReadOnlyList<BatchRun> recentRuns, DateTime today, int lookbackDays, int retentionDays)`（Core）：對最近 `lookbackDays`（常數 7）天內每個資料日 D，取 JobType 非 ai 的執行中**最後一筆含 D 的 `PrtgDayStat`**；`NeedsReevaluation` 為 true → D 待補；D 沒有任何 PrtgDayStat 但存在 `Interrupted` 的排程執行（StartedAt.Date == D+1）→ D 待補。排除超過保留期與 PRTG 未啟用；結果由近到遠；不含昨天（昨天本來就會評）。
- `AnalysisOrchestrator.RunAsync`：`request.Trigger == "schedule"` 且 Scope Full 且 PRTG 啟用時，`BuildPrtgDays` 的結果與待補日聯集（由近到遠、不重複）；執行輸出與 Milestone 印「PRTG 補評估 N 天：yyyy-MM-dd（原因）…」。手動立即執行不自動併入（使用者自己選回望天數）。
- 多日路徑沿用既有語意：對應只重算最新日、較舊日沿用該日或之前最近一日；`silent` 與數值規則只評最新日；狀態變更區間自動涵蓋 oldest−1（代價：補評估當晚狀態變更查詢的 `filter_drel` 由 7days 可能升到 30days，在執行輸出印出）。
- **AI 閘門縫隙（接受，不改）**：`PrtgRangeStart` 只看天數，非連續多日時較舊的待補日不被閘門擋住；若 AI 搶先判讀該日，`TryAttachAiResult` 的 PRTG 指紋守門會拒絕過期回寫並保持 `ai_pending`，下一輪重判——代價是一次多餘的 AI 呼叫，正確性不受影響。寫進 WEB-SPEC §9.10 閘門段。
- 呈現：`GET api/admin/schedule/status` 新增 `prtgPendingDays`（日期陣列，`RunMonitorService` 以同一個純函數計算、30 秒快取）；`/runs` PRTG 狀態卡閒置時多一列「待補評估：N 天（最早 yyyy-MM-dd），下一次排程自動補；要現在補請用「立即執行」並回望 M 天」，0 天不顯示該列；執行中不顯示。

**改動**
1. `PrtgDayStat.NeedsReevaluation` 與 `PrtgDailyPipeline` 三處寫入；`PrtgReevaluationPlanner`（Core/Service，純函數）＋測試。
2. `AnalysisOrchestrator.RunAsync` 聯集邏輯（`BatchRunStore` 已在該方法內可用）。
3. `ScheduleStatusDto.PrtgPendingDays`、`ScheduleController`／`RunMonitorService`、`runs.js` 一列。
4. PRTG-SPEC §3 回望多日段補「排程補評估」；WEB-SPEC §9.10 PRTG 卡與閘門段。

**驗收**
- `PrtgDailyPipelineTests`：取消與設定變更兩種中斷的逐日結果 `NeedsReevaluation=true`；成功日 false。
- Planner 測試 ≥6 案：NeedsReevaluation 日入選；同日後來一趟成功覆蓋 → 不入選；Interrupted 排程執行對應日入選；超過保留期不入選；順序由近到遠；最近 7 天沒有任何執行 → 空集合。突變：把「最後一筆」改「第一筆」後覆蓋案必紅。
- Orchestrator 測試：排程觸發時 `PrtgDailyPipeline` 收到的 days 包含待補日；手動觸發不含。
- `ScheduleControllerTests`：status 回應含 `prtgPendingDays`；`RunsPageUiTests` 含該列容器。
- 瀏覽器實測：以夾具寫入一筆 NeedsReevaluation 的執行紀錄，`/runs` PRTG 卡顯示該列與指路文字。

### A-3 AI 跟隨取數持續喚醒

**定案**
- AI 自動開跑的窗口條件改為三選一：在 AI 窗口內；**取數執行中且 `PrtgFindingsReady`**；**取數結束後 10 分鐘內**（`SchedulerRunState` 新增 `LastRunEndedAt`，`EndRun` 寫入）。後兩者 trigger 記 `fetch-followup`（與現行即時觸發同名），執行紀錄可分辨。
- `EndRun` 另發一次 `RunEnded` 事件，AI 服務訂閱後比照 `OnPrtgFindingsBecameReady` 立即嘗試一輪（同一套前置條件）。
- 既有完整性閘門（`FindingsWaitCutoff`）、存量校正閘門、AI 窗口 End 只停 `schedule` 觸發：皆不變。`AiIdleReasons.OutsideWindow` 語意不變（只在三條件都不成立時寫入）。
- 白天負載：現行即時觸發本來就不受窗口限制且會消化整批積壓，本項沒有新增白天 AI 負載形態，只是把「只喚醒一次」補成「跟著取數到結束」。

**改動**：`SchedulerRunState`（`LastRunEndedAt`、`RunEnded` 事件）、`AiAnalysisHostedService.TickAsync` 條件與訂閱、WEB-SPEC §9.10「AI 跟隨取數」段與 DETECTION-SPEC 對應段落改寫。

**驗收**
- `AiAnalysisSchedulerTests` 新增 ≥3 案：窗口外＋取數執行中＋finding 就緒 → `TickAsync` 觸發執行；窗口外＋取數結束 5 分鐘內 → 觸發；窗口外＋取數結束 15 分鐘 → 不觸發且 idleReason=`outside-window`。突變：把 10 分鐘改 0 後第二案必紅。
- 事件測試：`EndRun` 後 AI 服務收到喚醒（替身計數＝1）。

### A-4 文件校正
- DETECTION-SPEC：「取數執行內因此沒有任何同步 AI 呼叫」改為「除週體檢敘事外…」；刪「適用範圍：本機分析路徑仍是統計＋AI 一體」段，改一句「本機與 NetIQ 兩條路徑皆只算統計段並標 AiPending」。
- BACKLOG 新增：「PRTG sensor type 以顯示名 `type` 鍵化，介面語系非英文時白名單／語意／分類全部對不上；觸發條件：PRTG 語系改動或出現第二台非英文 core server；修法：鏡像同時存 `type_raw`，對照表改鍵」。
- 驗收：`grep -c "沒有任何同步 AI 呼叫" docs/DETECTION-SPEC.md` 為 0；`grep -c "type_raw" docs/BACKLOG.md` ≥1。

### A-5 PRTG 狀態卡顯示上一趟逐日原因
- `/runs` PRTG 卡閒置時，若最近一趟取數執行（JobType 非 ai）的 `PrtgDays` 任一日 Outcome 為 `no_output`／`failed`／`partial`，顯示一列「上一趟：{Note 或 Outcome 文案}」；資料走既有 `GET api/runs/list` 已回傳的 `PrtgDays`，不開新端點。「規則庫沒有啟用中的 PRTG 規則」的 Note 另附「前往規則維護」連結。
- 驗收：`RunsPageUiTests` 新增一案斷言該列的容器與文案鍵存在；瀏覽器實測以 `NoteNoRules` 的夾具執行紀錄驗證顯示與連結。

## 5. 批次 C：交辦兩段式摘要

### C-1 交辦通知 outbox 與兩時刻摘要

**定案（依使用者 2026-10-20）**：交辦類通知不再即時單發，改為**每天兩個時刻**各寄一封摘要給每位處理人：預設 `08:00` 與 `12:00`。某時刻 T 寄出「上一個時刻之後、T 之前」入列的全部項目。對應使用情境：上午派的工 12:00 收到，下午與夜間（含夜間自動派工）派的工隔天 08:00 收到。**高風險日的即時通知（`MailUrgentEnabled`）與每日／每週摘要完全不受影響**——本項只改「派工通知」。
- 入列對象（四個現行寄送點＋夜間摘要全部改入列）：建單（Created，含 origin manual／day_assign／auto_dispatch／owner_rule／backfill）、續掛新增成員（Appended）、移交（Transferred，原處理人）、取消（Cancelled）。
- 不入列：操作者給自己派單（維持現行）；`MailEnabled` 或 `MailNotifyWorkOrders` 關閉時不入列（開關語意不變）。
- **outbox 只存識別、不存內容**：新 store `WorkOrderOutboxStore`（`JsonBlobSingleton`，blob `work_order_outbox`，與 `mail_notify_state` 分開以免撐爆 4 MB 上限）。項目＝`{ Kind, WorkOrderId, RecipientUserId, EnqueuedAt, ActorAccount, MemberDelta, RecurrenceMembers, Reason, Immediate(C-4) }`，key＝`{kind}|{workOrderId}|{recipientUserId}|{enqueuedAtTicks}`。寄送時由 `WorkOrderDigestBuilder` 讀**當下**的交辦單（問題標籤、主機數與前 20 台、交辦說明、期限、處理人）渲染；單已不存在時該行寫「單號 N（已刪除）」。容量上限 20,000 項，超過時丟棄最舊並 log warn 一行（含筆數）。
- 時刻設定：`SystemSettings.MailWorkOrderDigestTimes`（`List<string>` HH:mm，預設 `["08:00","12:00"]`，1～4 組、不重複，儲存驗證比照 `MailDailyTime`）；設定頁郵件頁籤「交辦摘要時刻」欄位，說明文字「上午派的工 12:00 寄、之後派的工隔天 08:00 寄」。消費端＝寄送判定。
- 寄送：`SchedulerHostedService.TickAsync` 在 `CheckAndSendDailyWeeklyAsync` 之後呼叫 `CheckAndSendWorkOrderDigestAsync(now)`：找出**所有**已到點且今日尚未寄過的時刻（站台停機跨過兩個時刻時會同時到期），取其中最晚的 T，寄出 outbox 中 `EnqueuedAt < 今日 T` 的項目，依收件人分組，**每人一封**（多個時刻合併成一封，不會連收兩封）；內容分段「新交辦 N 張／新增成員 M 張／移交 K 張／取消 J 張」，主旨沿用 `MailSubjectTemplate`（type＝「交辦摘要」）。**該收件人寄送成功才移除其項目**；某收件人失敗則其項目保留到下一個時刻重試；處理完把到期時刻全部標為今日已寄（`WorkOrderDigestLastSentSlots`，存 `MailNotifyState`）。
- 收件人無 email：項目保留並標 `NoEmail=true`，不計入失敗；超過 7 天的項目由寄送時刻順手清除（log warn 含筆數）。
- 「說明：Hosts」隨寄送改為當下渲染而消失（`WorkOrderBoardService` 不再組 notice）。

**改動**
1. `WorkOrderOutboxStore`／`WorkOrderOutboxItem`（Core/Persistence）、`SystemSettings.MailWorkOrderDigestTimes`、`SystemSettingsService` 驗證與寫入、設定頁欄位（`Settings.cshtml`、`settings.js`，比照 `mail-daily-time`）、`MailNotifyState.WorkOrderDigestLastSentSlots`。
2. `MailNotificationService`：`EnqueueWorkOrderNotice(...)`（取代 `NotifyWorkOrderAsync`）、`EnqueueNightlyDispatch(NightlyDispatchSummary)`（取代 `NotifyWorkOrderDigestAsync`）、`CheckAndSendWorkOrderDigestAsync(now)`；舊兩方法移除，呼叫端全改（`WorkOrderCommandService.NotifyHandler`、`DayHandlingCommandService`、`WorkOrderBoardService`、`SchedulerHostedService` 夜間摘要處）。
3. `WorkOrderDigestBuilder`（Web/Services/Mail，純函數：項目＋當下單資料 → 主旨與內文），給測試直接驗文字。
4. `SchedulerHostedService.TickAsync` 掛接。

**驗收**
- `MailNotificationServiceTests` 新增 ≥9 案：(a) 11:30 入列、12:00 Tick 寄出、同人兩張單同一封且內文含兩個單號；(b) 13:00 入列、當天 12:00 之後任何 Tick 不寄、隔日 08:00 寄；(c) 同一時刻第二次 Tick 不重寄；(d) SMTP 失敗項目保留、下一時刻重寄、其他收件人不重複；(e) 無 email 項目保留且不算失敗；(f) 操作者給自己派單不入列；(g) 重建 service（模擬重啟）後 outbox 仍在並寄出；(h) 停機跨過 08:00 與 12:00 後第一次 Tick 只寄一封且含兩段期間的項目；(i) 超過 20,000 項時最舊被丟棄且 warn 一次。突變：拿掉 `EnqueuedAt < 今日 T` 的時間過濾後 (b) 必紅；把「最晚的 T」改成「最早的 T」後 (h) 必紅。
- `grep -rn "NotifyWorkOrderAsync\|NotifyWorkOrderDigestAsync" LogForesight.Web LogForesight.Core --include=*.cs | grep -v Tests` 為 0 筆。
- `WorkOrderBoardServiceTests` 既有「有寄、內文含單號」兩案改為「已入列、項目欄位正確」。
- `SystemSettingsServiceTests`：時刻格式錯、重複、超過 4 組各回驗證錯誤。
- `SettingsPageUiTests`（既有類）含新欄位 id。
- 瀏覽器實測：設定頁能看到並儲存時刻；郵件本身以測試替身驗證（本機無 SMTP）。

### C-2 無 email 的派工者回饋
- 交辦建單／改派／拆出／日層級指派的回應 DTO 新增 `HandlerHasNoEmail`（users store 查 email 是否空白）；前端 `records.js`、`handling-panel.js`、`work-order-detail.js` 成功 toast 補一句「處理人未設定 email，不會收到通知」。
- 驗收：服務層測試三案（有 email 為 false、空白為 true、null 為 true）；`FrontendConsistencyUiTests` grep 三頁含 `handlerHasNoEmail`；瀏覽器實測指派給無 email 帳號看到該句。

### C-3 規格同步
- WEB-SPEC §9.4b「通知」段改寫為兩時刻摘要語意（刪「日層級指派與立即派工不寄」句、刪「每趟每人一封」句，補立即通知選項）；§9.9b 郵件頁籤補欄位；§10.2 store 表補 `WorkOrderOutboxStore`；DB-SPEC blob 說明同步。
- 驗收：`grep -c "不寄建單信" docs/WEB-SPEC.md` 為 0；`grep -c "work_order_outbox" docs/WEB-SPEC.md docs/DB-SPEC.md` ≥2。

### C-4 立即通知選項（§0 待確認）
- 交辦 modal（`records.js`）與改派對話框（`work-order-detail.js`）加勾選「立即通知處理人（不等摘要時刻）」，預設不勾；`POST api/work-orders`／`{id}/reassign` 多一個 `notifyNow` 布林。入列項目 `Immediate=true`，`CheckAndSendWorkOrderDigestAsync` 每次 Tick（60 秒）先把 `Immediate` 項目依收件人各寄一封「交辦通知」（同一渲染器），成功即移除。
- 驗收：測試兩案（`notifyNow=true` 在下一次 Tick 寄出且不等時刻；`false` 等時刻）；UI 守門兩頁含 `notifyNow`。

## 6. 批次 D：效能

### D-1 狀態篩選路徑限縮與上限守門（待確認 §0）
**定案（建議）**
- `needsHandlingFilter` 時：若 `request.RiskLevels` 為空（使用者沒指定），SQL 端加 `RiskLevels ∈ {高, 中}`；使用者明確含「低」才查低風險日。清單頁篩選列在狀態 chip 啟用時顯示提示「狀態篩選只看高／中風險日，要含低風險請勾選風險等級」。儀表板「未處理」KPI 走 `IssueTodoQuery`，不受本項影響。
- 候選上限守門：慢速路徑 `QueryLightweight` 回傳列數 > `HandlingFilterCandidateLimit`（常數 50,000）時回 400 `DomainException.Validation`「符合條件的主機日超過 5 萬筆，請縮小日期範圍或主機範圍」，不靜默截斷；上限常數不開設定。
- 兩條都是「少撈」不是「改推導」：`DayHandlingDerivation` 不動。

**驗收**
- `RecordListQueryServiceTests` 新增：狀態篩選且未指定等級時 repository 收到的 filter 含高／中；明確含低時含低；候選超上限擲驗證例外（替身回傳 50,001 列）。突變：拿掉上限判斷後第三案必紅。
- 前端 UI 測試 grep 提示文案存在於 `records.js`；瀏覽器實測勾「未處理」看到提示。
- WEB-SPEC §9.2 補兩句。

### D-2 報表非全部範圍：一次查詢、一次載入
**核對**：`HandlingScopes` 只有 `all`／`unresolved`／`open`／`unassigned`（HandlingHistoryQueryService.cs:100-110），三個非全部值都依日狀態推導，**無法安全下推 SQL**（推導規則含未處理嚴重度集合與靜音排除，複製到 SQL 就是第二份規則）。現況成本＝本期與前期各一次 `QueryLightweight`（已限高／中）＋ `FilterByScope` 各自再載三張處理表一次（共六次表載入）。

**定案**
- `ReportService` 非全部範圍改為**一次** `QueryLightweight`（`From=min(previousFrom, from)`、`To=to`、高／中）後在記憶體依日期切成本期與前期；`FilterByScope` 新增可接受「預先載好的處理表三份」的多載，兩期共用同一次 `GetMany`（主機名聯集、日期區間聯集）。結果與現況逐筆相同，只是查詢與載入次數減半。
- 既有 `SummaryCache` 不動。`reports.js` 180 秒逾時維持；`ReportService` 補 performance 埋點 `report:scope-filter`（既有 `IPerformanceMonitor` 慣例），讓系統健康頁看得到這段耗時。
- 實機目標（使用者實測回報，不列自動驗收）：2,000 台 × 90 天非全部範圍首次載入 < 15 秒。

**驗收**
- `ReportServiceTests`：scope=open 時 repository 的 `QueryLightweight` 呼叫次數＝1（現況 2），處理表 `GetMany` 各 1 次（現況各 2 次）；KPI 本期／前期數字與改前相同（以同一夾具在改前先跑一次記下數字寫進測試）。突變：把切分日期的比較 `< from` 改成 `<= from` 後，夾具含邊界日的案必紅。
- `HealthControllerTests` 或既有 performance 測試：慢操作名稱清單含 `report:scope-filter`。

### D-3 背景服務獨立連線池
**定案**
- DI 以 .NET 8 keyed service 註冊第二個 `StorageBackend`（`AddKeyedSingleton<StorageBackend>("background", …)`，`MaxPoolSize`＝與 `AnalysisMaxPoolSize` 同一常數來源；SQLite 下與站台相同連線字串、無池、無額外成本）；`PrtgSnapshotHostedService`、`AiAnalysisHostedService`、`HostDayWorkflowRecoveryHostedService`、`PrtgRiskReviewHostedService` 改注入 `[FromKeyedServices("background")] StorageBackend`。它們內部 `new` 出來的 store 一律從這個 backend 建。專案尚無 keyed DI 先例，WEB-SPEC §4.3 DI 註冊表補一列說明何時該用。
- 跨實例一致性：`lf_blobs.version` 權杖與 `DataVersionStamp` 本來就跨實例有效；實作時 grep 這四支服務用到的「行程內快取」清單寫進 §8，逐一確認鍵含版本戳。
- Schema 確認仍只做一次（既有 `Global\LogForesight-Schema` mutex；第二實例啟動時重做一次確認、不重建）。

**驗收**
- DI 測試：解析四支服務後其 backend 參考不等於站台 Singleton。
- 整合測試（SQLite 檔案 DB）：背景 backend 寫入 blob 後站台 backend 立即讀到新值；反向亦然。
- 全套測試全綠。

### D-4 查詢計畫守門與補索引
**定案**
- 新測試類 `QueryPlanGuardTests`（SQLite）：先以 `SchemaUpgrader` 建全部索引、每張相關表塞 ≥500 列夾具、跑 `ANALYZE`，再以 EF `ToQueryString()` 取得 SQL 做 `EXPLAIN QUERY PLAN`。涵蓋八支查詢：記錄清單預設排序分頁（`QueryPage`）、`QueryLightweight`（日期＋風險）、`CountPendingAi`、`QueryPendingAi`、交辦單依處理人進行中、`lf_top_issues` 依問題日期聚合（`AggregateByCategory`）、`lf_record_handling` 逾期、`lf_log_lines` 近期執行紀錄。斷言每個計畫中主表那一行含 `USING INDEX` 或 `USING COVERING INDEX`（允許 `SCAN … USING COVERING INDEX`，不允許純 `SCAN <table>`）。
- 實作端先跑計畫，對純 SCAN 的查詢補索引（`SchemaUpgrader` 冪等建立，SQL Server 同步），把新增索引寫進 DB-SPEC §索引並在 §8 回報每支查詢的計畫前後。
- SQL Server 無法實測，索引語法以 `ToQueryString` 既有測試方式確認可翻譯。

**驗收**
- `QueryPlanGuardTests` 八案全綠；移除任一新索引後對應案必紅（Claude 以突變驗證至少一支）。
- DB-SPEC §索引與 `SchemaUpgrader` 的索引清單一致（既有反射／字串核對測試若有則沿用，無則新增一案）。

## 7. 驗證環境與每段共同驗收

- **瀏覽器實測**：`preview_start` 的 `web`（`dotnet run`，port 5041），`Auth:Provider=Stub` 以 `demo-admin` 登入；處理人視角以新建一個 `User` 角色帳號測。SMTP 無法實測，郵件內容一律以測試替身驗。
- **每段共同驗收**：`git diff --stat` 只含白名單檔；改到的檔案 BOM 與基準分支相同（`head -c3`）、無 NUL；`dotnet build` 0 警告（第 54 輪已達成的基線）；該段定向測試全綠；新增類別／方法／設定各對得上本文件一條定案（過度設計檢查）；同一判定不得出現兩份（DRY）。
- **全量測試**：每批次結束跑一次全量並記進 §8；實作輪收官再跑一次寫進「體檢交接」。
- **開分支前**：刪除 `scripts/build-identity`、`scripts/prtg-workload`、`scripts/round53-user-matrix` 三個未追蹤資料夾（`git status` 確認仍為 `??` 後 `rm -rf`），`git status --short` 乾淨才 `git switch -c feature/feedback-55`。

## 8. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | agy flash-medium（規格 task-55-a1） | 完成 | 0 警告；定向 192 綠；突變兩處皆紅（Skip(1)→全略過、拿掉 startedBefore） | Claude 審查補兩處：(1) 規格漏掉同行程競態——AI 排程與本程序同在啟動 10 秒後開跑，其執行會被誤關，改為只關本行程啟動前開始的執行（`Process.StartTime`）並補測試；(2) agy 加了規格禁止的可選參數 `windows = null`，改為必填。設計修正：不新增 `Interrupted` 狀態值，`Compute` 直接回既有 `Stuck`（畫面文案本來就是「異常中斷」），`runs.js` 免改 |
| A-2（含 A-5） | agy flash-medium（規格 task-55-a2） | 完成 | 0 警告；定向 202 綠；突變三處：規劃器改取最早一趟→2 案紅；拿掉 `!IsEvaluated` 起初**存活** | agy 的取消測試在進入逐日評估前就取消，走的是「補列」路徑，沒守到 `!IsEvaluated` 分支；Claude 補「逐日評估完、發佈前被取消」測試後突變轉紅。A-5 併進同一段：狀態 API 一次讀執行紀錄同時給「待補評估」與「上一趟」，不另開資料路徑。設計修正：狀態 API 不加 30 秒快取（閒置輪詢 10 秒、只讀 8 天執行紀錄，成本低）。另移除控制器多餘的 null 判斷、空原因時不留尾端冒號。已知限制寫進 PRTG-SPEC：之後的指定主機更新成功紀錄會蓋過先前全範圍失敗 |
| A-3 | agy flash-medium（規格 task-55-a3） | 完成 | 0 警告；定向 89 綠；突變寬限期判斷 → 2 案紅 | 無落差。規格把兩份文件誤寫成有 BOM，派工前已更正 |
| A-4 | Claude（純文件） | 完成 | `沒有任何同步 AI 呼叫` 0 筆、BACKLOG `type_raw` 1 筆 | 先於 A-3 做，避免兩者同改 DETECTION-SPEC 同一節 |
| C-1a（入列） | agy flash-high（規格 task-55-c1a） | 完成 | 0 警告；定向 669 綠；突變入列時間改回 UTC → 紅 | Claude 審查修三處：(1) 入列時間用 UTC，寄送時刻是本地 08:00／12:00，台灣時區會落錯時段，改本地時間並補斷言；(2) 夜間派工逐筆入列、每筆整份 blob 重寫，改一次入列；(3) agy 剝掉兩個測試檔的 BOM，已復原。被移除的 10 個舊寄信測試（20 台截斷、移交／取消措辭、復發、無 email、每人一封）列入 C-1b 規格必須移植。本段完成後交辦信暫停寄出，C-1b 補上 |
| C-1b（寄送，含 C-2／C-3／C-4） | agy flash-high 起跑後使用者改指定 **Sonnet 5.5 subagent**（之後所有委派改用 Sonnet）；agy 半成品存 scratchpad 後重設，由 Sonnet 從規格重做 | 完成 | 0 警告；定向 820 綠；Claude 獨立重做突變（取最晚時刻改最早）→ 3 案紅；38 檔 BOM／CRLF／NUL 全對 | 移植 C-1a 刪除的舊寄信行為共 21 案。實作決定：立即項目遇無 email 改回走摘要時刻；7 天後仍無 email 才清除（已補 email 者不清）；立即派工不加立即通知（規格只要求建單與改派）；建單的 HandlerHasNoEmail 為「任一處理人沒有 email」。|

## 9. 明確不做（本輪定案）

- PRTG `type_raw` 鍵化（語系英文；進 BACKLOG）。
- 深色模式、麵包屑補做（第 56 輪改規格）。
- PRTG 衝突清單分頁全表、DNS／守門即時來源 async（BACKLOG 觸發條件未達）。
- 記錄查詢慢速路徑的狀態推導改寫或物化快照（兩份事實來源風險高於收益；本輪只少撈不改推導）；同理不做以 DataVersionStamp 為鍵的推導結果行程內快取（任一寫入請求都推進版本戳，活躍時段會不斷重建）。
- AI 完整性閘門改成看最早待補日（A-2 已說明由指紋守門保正確，只多一次 AI 呼叫）。
- 內建規則升級改自動套用（RULES-SPEC 既有定案維持人工套用；A-5 把「規則庫無規則」做成看得到的指路）。
- 管理者回話／退回功能（第 56 輪）。
- 多台 PRTG core server、多執行個體排程。

## 10. 規劃完成後複檢

- **與既有行為/設計的衝突**：A-1 改變「觸發時間」判定——`NoteSkippedScheduleWindow` 共用同一份判定，已列入改動 3；A-1 啟動關閉列必須先過具名互斥，否則重疊回收時會誤關另一行程的執行；A-3 放寬 AI 自動開跑條件，`AiIdleReasons.OutsideWindow` 只在三條件皆否時寫入，狀態卡文案不變；C-1 推翻 WEB-SPEC §9.4b「不寄建單信」與「每趟每人一封」兩句，C-3 改寫；C-1 不影響高風險即時信與每日／週摘要；D-1 改變低風險日在狀態篩選下的可見性，已列待確認。
- **批次間衝突**：A 與 C 都動 `SchedulerHostedService.TickAsync`（A 啟動段、C 寄送段）；A-3 與 D-3 都動 `AiAnalysisHostedService`；順序 A → C → D。
- **拆分原則四個坑**：A-1「什麼算一個」＝一個窗口實例內第一筆 Interrupted；「一個都沒有」＝全部正常計入；互斥拿不到＝整段跳過不是失敗。A-2 分母為零（最近 7 天沒有任何執行）→ 空集合、不印補評估行。C-1 破壞性判準（移除 outbox 項目）只在該收件人 SMTP 接受後；反例：無 email 項目不移除、失敗不移除；容量上限丟最舊要 log。C-1 單向閘門（時刻已寄）繞過路徑＝失敗項目保留到下一時刻，不需人工重置。D-1 上限守門的合法值長得像它：真的有 5 萬筆待辦的站台——訊息要求縮範圍而非靜默截斷。移除類：C-1 移除兩個舊方法，呼叫端四處＋測試已列白名單。
- **升級／既有資料**：A-1 `Interrupted`、A-2 `NeedsReevaluation` 皆 JSON 缺欄容忍；C-1 新 blob 缺省空；D-4 新索引冪等、首次升級不要在排程窗口內（DB-SPEC 既有注意事項）。
- **SOLID／KISS／DRY 自查**：純函數（`EffectiveScheduleTriggerTimes`、`PrtgReevaluationPlanner`、`WorkOrderDigestBuilder`）各只有一個理由改變；outbox 不複製單的內容（單一事實來源在 `lf_work_orders`）；沒有新增抽象介面；沒有新增設定項除了有明確消費端的摘要時刻。
- 複檢完成，第二版新增待確認一項（C-4）已列 §0。
