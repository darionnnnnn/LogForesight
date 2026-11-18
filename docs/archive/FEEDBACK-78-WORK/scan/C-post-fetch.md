# C：分析紀錄寫入之後的後段流程盤點（唯讀）

範圍：夜間掛接與自動派工 → 郵件通知 → AI 白話分析排程 → 工作流恢復 → 資料保留清除。
規模假設：H＝3000 台、保留 D＝180 天，主機日 ≈ 540,000 列（`DefaultRetentionDays=180`，`SystemSettings.cs:212`）；`RawEventRetentionDays` 預設 120（`SystemSettings.cs:244`），所以約 60 天的列是「詳情已清」的殘根。
路徑皆相對 `/Users/wayne/project/LogForesight/`。標「推論」者是我由程式碼推出、未實際執行驗證。

---

## 0. 流程圖（呼叫鏈）

```
SchedulerHostedService.TriggerRunAsync（Web/Services/SchedulerHostedService.cs:502）
 └ _mutexGate.RunExclusiveAsync（:530）                    ← 跨行程具名 Mutex，取數一次一趟
    └ AnalysisOrchestrator.RunAsync（Core/Service/AnalysisOrchestrator.cs:268）
       ├ RetentionPruner.Run（:589）                       ← 每一趟取數開頭都跑（不是每天一次）
       ├ _candidateSource.Build()（:634）                  ← 派工候選人快照（每趟一次）
       ├ DispatchContext.Build（:656）                     ← 派工脈絡（每趟一次）
       ├ new WorkOrderCoordinator / NightlyDispatch（:667-670）
       ├ new HostDayWorkflowService(new HostDayWorkflowStore(backend))（:675）  ← 每趟一個實例
       ├ localTask(:713) ∥ netiqTask(:726) ∥ prtgTask(:752)  ← 三路並行，共用 nightlyDispatch
       │   每個主機日（Orchestrator:1170-1207／NetiqPipelineService:845-920）：
       │     Append 紀錄 → ParentSucceeded（工作流）→ AttachPrtgFindings → SetPrtg（工作流）
       │     → HostDayPostProcessor.AttachCase（Core/Service/HostDayPostProcessor.cs:240）
       │           ├ workflow.CaptureDeliveryVersion
       │           ├ workflow.RecordCaseIntents
       │           ├ IssueCaseCoordinator.AttachNewDay（Core/Persistence/IssueCaseCoordinator.cs:168）
       │           ├ workflow.RecordCaseResults
       │           └ NightlyDispatch.DispatchDay（Core/Persistence/Dispatch/NightlyDispatch.cs:73）
       │                 lock(_ctx.Gate) → Decide → EnsureNightlyOrder／WriteNightlyMembersDetailed
       │                 → 每件問題 onResult → workflow.RecordDispatchOutcome
       │     → ReplaceRiskyEvents → RecordPermissionChanges
       ├ PrtgDailyPipeline 尾段（Core/Service/PrtgDailyPipeline.cs:930-965）：對「本趟碰過的主機日」Get + SetPrtg
       ├ finally：runCtx.Dispatch.FlushRun（:773）          ← 補 appended 事件、續掛停止標示、彙總
       └ 體檢（:799）、PRTG 數值接續補（:836）
 └（出了鎖，同一個 Task.Run 內依序）
    ├ _runState.EndRun(outcome)（:577）→ 觸發 RunEnded 事件 → AiAnalysisHostedService.OnFetchFollowUp（並行起跑）
    ├ _dataVersion.Bump()（:580）                          ← 喚醒 CaseDaySync、使儀表板快取失效
    ├ _mail.NotifyAfterRunAsync（:603）                    ← 整段同步等完，才往下
    ├ _mail.EnqueueNightlyDispatch(dispatchSummary)（:611）
    └ _adminReminders.RunAfterNightly（:616）

獨立背景服務（與上面並行）：
 ・SchedulerHostedService 60 秒輪詢 TickAsync（:146）→ SendDigests（:160）
      ├ PollMailSlot → MailNotificationService.CheckAndSendDailyWeeklyAsync（Mail:351）→ RetryPendingUrgentAsync（Mail:317）→（有未終結的緊急意圖時）NotifyAfterRunAsync
      └ PollMailSlot → CheckAndSendWorkOrderDigestAsync（Mail:633）
 ・AiAnalysisHostedService：60 秒輪詢 TickAsync（:154）＋取數事件即時跟隨（:81-82,:127）
 ・HostDayWorkflowRecoveryHostedService：15 分鐘一片（:18,:92）
 ・CaseDaySyncHostedService：5 秒一拍、閒置退避 1/2/4/6 拍、資料版本一變即喚醒（Web/Services/CaseDaySyncHostedService.cs:48-90）
 ・RunRetentionIfDueAsync（SchedulerHostedService.cs:656）：中午後、當天沒跑取數才補清除
```

---

## 1. 觸發與順序

| 步驟 | 觸發者／時機 | 證據 | 與誰會同時跑 |
|---|---|---|---|
| 保留清除 | 每一趟取數的開頭（含手動、續跑、補跑），且當天沒跑取數時由輪詢補做一次 | Orchestrator:589；Scheduler:656-689 | 取數本身尚未開始，晚於它的步驟不會碰到；但 AI／郵件／恢復／CaseDaySync 可能同時在跑 |
| 候選人快照、派工脈絡 | 每趟一次（取數主體之前） | Orchestrator:634,656 | 同上 |
| 掛接＋派工 | 每個主機日寫入後，由三路並行 worker 各自呼叫 | HostDayPostProcessor.cs:240-307 | 三路共用一個 NightlyDispatch，派工段以 `_ctx.Gate` 互斥（NightlyDispatch.cs:86） |
| FlushRun | 三路匯合後（含失敗） | Orchestrator:766-784 | |
| 郵件通知（執行後） | EndRun 之後、同一個 Task.Run 內序列呼叫，無論排程或手動 | Scheduler:601-604 | **與 AI 跟隨取數並行**（EndRun 同時觸發 RunEnded）；兩者不互斥 |
| 郵件重試 | 每 60 秒輪詢；`_state.Get()` 讀到有「未終結」緊急意圖就整支 NotifyAfterRunAsync 重跑 | Mail:317-323、Scheduler:166 | 與 NotifyAfterRun 用同一個 `_notifyGate`（Mail:72,186）序列化 |
| 每日／每週摘要 | 同 60 秒輪詢，時刻＋「今天沒寄過」 | Mail:351-389 | **不進 `_notifyGate`**，可與執行後通知並行 |
| 交辦摘要 | 同 60 秒輪詢 | Mail:633-702 | 不進 `_notifyGate` |
| 夜間派工入列／管理者提醒 | NotifyAfterRun 之後同序 | Scheduler:609-616 | |
| AI 排程 | ① 60 秒輪詢（窗口內，或取數結束 10 分鐘內 `FollowUpGrace`）② RunEnded／PrtgFindingsBecameReady 事件即時觸發（不看窗口） | AiAnalysisHostedService.cs:127-151,173-182；SchedulerRunState.cs:270,411,417 | 與郵件、恢復、取數並行；AI 自己併發 1 個「輪」，輪內依 `AiConcurrency`（1~8）按主機分片平行（:518-523） |
| 工作流恢復 | 15 分鐘一片；開始前若取數在跑就等（BackgroundWorkGate，每 30 秒複查），**但開始後取數才起跑則不互斥** | RecoveryHostedService:18,106；BackgroundWorkGate.cs:35-40 | 與 AI／郵件並行；跨行程以具名 mutex `LeaseName` 排他（:194,:229,:257，等 1 秒取不到就略過） |
| 案件逐日同步＋結案掃描 | 5 秒一拍；資料版本變就跑，沒事退避到 30 秒 | CaseDaySyncHostedService.cs:48-90 | 結案掃描經 `OrderMutationLock`，與夜間派工共鎖 |

---

## 2. 資料庫往返

### 2.1 每趟（取數一趟內）一次性的讀

| 項目 | 次數 | 證據 |
|---|---|---|
| `DispatchCandidateSource.Build` | users.GetAll、userGroups.GetAll、orders.GetAllActive 各 1；**每位具處理能力且（在池或有可續掛單）且未暫停的人：`CapturePrtgSnapshot`（有版本快取，命中只探一個整數）＋ userGroups.GetAll ＋ access.GetAll ＋ issueOwners.GetAll（blob 未快取）＋（有負責問題時）`HostIdsFor` 一次 SQL（掃 lf_top_issues 保留期全窗，`EfIssueAggregateQuery.cs:353`）** | DispatchCandidateSource.cs:41-84；HostVisibilityResolver.cs:111-128、78-107 |
| `DispatchContext.Build` | issueOwners.GetAll 1、orders.GetAllActive 1、LoadBoard 1、noiseMarks.GetAll 1、`cases.GetResolvedSince(30 天)` 1（全站已解決案件整批載入） | DispatchContext.cs:85-131；EfIssueCaseStore.cs:128 |
| 系統設定、憑證前置檢查 | 各 1 | Orchestrator:614-630 |
| 保留清除 | 見 §2.6 | |

### 2.2 每主機日（掛接＋派工）

`AttachCase`（HostDayPostProcessor.cs:240-307）每個主機日的往返（K＝該日 `Unassigned` 問題數，A＝該日掛進既有案件數）：

| 步驟 | 讀 | 寫 | 證據 |
|---|---|---|---|
| CaptureDeliveryVersion | 工作流 blob 1 | | HostDayWorkflowState.cs:821-829 |
| RecordCaseIntents | （工作流 RMW）| 工作流 1 | :575 |
| AttachNewDay | `GetOpenForHost` 1、`GetForDay` 1、`issueProfiles.GetAll()` 1（IssueOwnerStore **未快取**，每個主機日反序列化整份負責人檔） | `AppendLog` **A 次逐筆**（每次先 ReadLastLines(100) 並逐行 JSON 解析探號，再 INSERT）、`SaveMany` 2 次 | IssueCaseCoordinator.cs:172,183,194,220,264-265；EfRecordHandlingStore.cs:121-133,193-200；EfJsonLogStore.cs:103-128 |
| RecordCaseResults | （工作流 RMW）| 工作流 1 | :612 |
| DispatchDay | `hosts.FindByName` 1（HostStore 有版本快取，仍每次探版本一次 DB 往返）；每件問題：`IsDismissed` → 該主機**全部案件**一次（`CasesFor` 快取到被 `InvalidateCasesFor` 清掉）；PRTG 趨勢問題另有 `ActiveWarningOrderForSensor`（讀同一份 `CasesFor`）＋ `IsOrderActive` → `orders.Get` | | NightlyDispatch.cs:78；DispatchContext.cs:159-163,206-211,190-192 |
| 建單 `EnsureNightlyOrder` | `GetActiveFor` 1 | INSERT 單 1＋`AppendEvent(created)` 1（僅新建） | WorkOrderCoordinator.cs:516-533,1063-1082 |
| 寫成員 `WriteNightlyMembersLocked` | `GetOpenForHost` 1、涉及的每張單 `orders.Get` 1（個位數）、PRTG 問題每件 `CapturePrtgEvidence`（`hosts.FindByName` ＋ `records.GetOne` ＝**整列 ContentJson 反序列化**） | `cases.SaveMany` 1、`issueHandlings.SaveMany` 1、`AppendLogs`（批次，一次探號）1 | WorkOrderCoordinator.cs:557-633；IssueCaseCoordinator.cs:46-60 |
| 每件問題的 `RecordDispatchOutcome` | | 工作流 **K 次** RMW（delivered／failed 走 RecordCaseResults，其餘各自 Update） | HostDayWorkflowState.cs:633-658 |

小結：一個主機日**即使沒有任何問題**也至少是「Get ×1＋工作流 RMW ×2」（`AttachCase` 在 `hasWorkflow` 時無條件呼叫 RecordCaseIntents／RecordCaseResults，`HostDayPostProcessor.cs:254-262,269-283`；`AttachNewDay` 於 `issues.Count==0` 才提早返回，:170）。

### 2.3 每趟收尾（FlushRun，NightlyDispatch.cs:279-337）

- 每張有新增成員的單：`AppendEvent(appended)` ＋ `UpdateOrder`（讀＋寫）。
- 每張空單：`CloseEmptyDispatchOrder`。
- `FlushContinuationStops`：`AllActiveOrders` 在記憶體；有更新時 `SetContinuationStops` 1 次批次寫。
- 全部在 `lock(_ctx.Gate)` 內，並逐項取 `OrderMutationLock`（WorkOrderCoordinator.cs:41,979）。

### 2.4 郵件：每次 `NotifyAfterRunAsync`（Mail:176-309）

設 W＝窗口內有紀錄的主機日數（回望 `NotifyLookbackDays=14` 天 × H ≈ 42,000）、R＝收件人數、Q＝達門檻且尚未寄的主機日數。

| 步驟 | 往返 | 證據 |
|---|---|---|
| `QueryNotificationWorkflowPage` 每頁 500 | W/500 次輕量投影 | Mail:207；EfAnalysisRecordStore.cs:1380 |
| 逐主機日 `CaptureDeliveryVersion` | **W 次**工作流 blob 讀 | Mail:212 |
| `_records.Query(14 天, 風險下推)` | 1 次，**整批 ContentJson 載入＋反序列化**（Q 級） | Mail:280 |
| `ClosePlans()`（早退分支 :270 與結尾 :297 各一次） | `_settingsStore.Get` ×1＋再 `_records.Query` ×1＋`BuildContext` ×1＋`ResolvePerRecipient` ×2＋Migrate／Plan／Track×3；**之後對 W 逐一：`CaptureDeliveryVersion`（讀）→ 不需要的 lane 各 `ReplaceMailPlan`（RMW）→ `CloseMailPlan`（RMW）** | Mail:222-267 |
| ResolvePerRecipient（每次）| `GetSuspendedRecipients`→`_state.Get()`（≤4 MiB blob）1；每位對應到帳號的收件人 `GetVisibleHostIds` ＝ 快照＋groups＋access＋issueOwners.GetAll＋`HostIdsFor` SQL；`MailNotifyHostOwners` 開啟時每位負責人再算一次 | Mail:962-1026；HostVisibilityResolver.cs:111 |
| SendRunSummary／SendUrgent | `_state.Get()` 1、ResolvePerRecipient 各 2 次（candidates、qualifying/pending）、`_state.Update` 多次（outbox 建立、每收件人 starting／result、reservation 三段、failure streak、MarkSent） | Mail:1056-1069,1176-1221,1307,1352,1375,2209 |
| `Recheck`（**每位收件人每條 lane 兩次**：寄前 :1483、寄後 `onRecipientResult`）| 每次：`_settingsStore.Get`、`_records.Query(14 天)`、`CaptureMailIssueContributionScope`（再 **2 次 `_records.Query`**：本期 14 天＋前期 14 天，且對每個問題 `JsonSerializer.Serialize(issue)`）、`BuildContext`、`ResolvePerRecipient`、每筆 `CurrentWorkflowVersion`＝工作流 Get | Mail:1100-1133,1242-1295,1483,1799-1827,2073-2080 |
| 每個收件人的 `BuildRecipientIssueRows` | `_state.Get()` 1；快取未命中時 `MailIssueDigest.BuildDetailed` ＝ Aggregate×2＋ActionableOccurrences＋（有 PRTG 問題時）LatestOccurrences | Mail:1758-1797；MailIssueDigest.cs:48-121 |
| `PlanWorkflowMail` | 每個 record 每條 lane 1 次工作流 RMW（`ReplaceMailPlan`）| Mail:2306-2329 |
| `TrackAcceptedContributionWorkflow` | 每（收件人 × record）1 次 `CurrentWorkflowVersion`（Get）＋1 次 `RecordMail`（RMW） | Mail:2331-2376 |
| `MigrateLegacyAcceptedNetIqContributions` | 每（record × 看得到它的收件人）1 次 `_workflow.Get`，在雙層迴圈內 | Mail:2413-2430 |
| 每日／週報 `SendDigestAsync` | 1 次 `Query`＋`BuildContext`＋Resolve；每位收件人 `RecheckDigest` 再 `Query`＋Capture（2 Query）＋`BuildContext`＋Resolve | Mail:1829-1998 |

### 2.5 AI 排程：每筆待補紀錄（AiAnalysisHostedService.cs:533-669）

| 步驟 | 讀 | 寫 | 證據 |
|---|---|---|---|
| `GetOne` 完整紀錄 | 1（ContentJson 反序列化） | | :546 |
| `RestoreFromRecord` | 工作流 1 | 工作流 1 | :560；HostDayWorkflowState.cs:547 |
| `BeginAi` | 工作流 1 | 工作流 1 | :561 |
| `RetryAiAsync` | `ReadRecent(date, 14)`＝**14 列完整紀錄**、`QueryDay`（風險事件）、`LoadSuppressions` | | LogAnalysisService.cs:577-600 |
| 回寫前檢查 | `GetOne` 再 1、`IsCurrentAiVersion` 工作流 1 | | :581-584 |
| `TryAttachAiResult` | 同交易 Serializable：讀該列、`ForRecord`＋`PrtgInputFingerprint` | 該列 1 | EfAnalysisRecordStore.cs:520-600 |
| `CompleteAi` | 工作流 1 | 工作流 1 | :613 |
| 降級分支 | 再 `GetOne` 1 | | :618 |
| 失敗／取消分支 `CompleteWorkflowAiAttempt` | 工作流 Get 1＋`GetOne` 1 | `CompleteAi` RMW | :711-736 |

每輪另有：`CountPendingAi`（實機 7～15 秒，註解 :480）**輪頭 `TriggerRunAsync` 查 1 次（:272）＋處理迴圈 `pendingEstimate ??=` 再查 1 次（:508）＋整批失敗防線／結尾各最多 1 次（:493,:691）**；`QueryPendingAi` 每批至少 1～3 次（`SelectNextBatch`，:430-451：近日層 1 次，積壓層 anchor 1 次＋區塊 1 次）；每個分片 `CreateAnalysisServiceForHost` 另 new `IssueOwnerStore`、`KnownIssueRuleStore`、`RiskyEventStore`、`ReportStore`（:770-795）。

輪詢（每 60 秒）：`BlockingIdleReason`（:190-217）的 `pendingCount` 走 `GetPendingAiCount` 30 秒快取（AiAnalysisRunState.cs:293-322），但 **「跟隨取數」期間每次 Tick 先 `InvalidatePendingAiCache`（:172）＝每 60 秒真的下一次 `CountPendingAi`**；`OnFetchFollowUp` 每個事件也先失效再查（:138-140）。

### 2.6 保留清除（RetentionPruner.Run，Core/Service/RetentionPruner.cs:48-262）

依序 13 段，各自獨立失敗邊界：歷史紀錄 `Prune`（每趟上限 5 萬列、2000 一批，只撈主鍵、ExecuteDelete，EfAnalysisRecordStore.cs:899-960）→ `PruneDetails`（同上限，ExecuteUpdate 把 content_json 清空並 `WriteRevision+1`、`AiPending=false`，:1035-1085）＋`CountPrunableRecords` → 執行歷程／匯入 → 稽核 → NetIQ 取數狀態 → 權限異動 → 風險 log → 處理狀態（三表＋單＋事件＋歷程）→ 報告 → PRTG 鏡像＋診斷包 → AI 傾印檔 → `PruneTerminalUrgentIntents`（**讀寫整份郵件狀態 blob，Serializable，MailNotifyStateStore.cs:188-200**）→ 寫 `retention_state`。
**沒有任何一段清工作流 `workflow_*` blob**（grep 確認 RetentionPruner.cs 無 workflow）；它們只靠恢復服務的孤兒清掃（見 §5）。

### 2.7 工作流狀態（每主機日一列 blob，key＝`workflow_{hostId}_{yyyyMMdd}`，上限 64 KiB）完整生命週期讀寫表

「RMW」＝`HostDayWorkflowStore.Update`：1 個具名 OS Mutex（`new Mutex(Global\LogForesight.Workflow.{key})`，NamedMutexGate.cs:94-120）＋1 個交易＋2 次 SELECT（有界讀 `ReadBoundedInContext` ＋ `FirstOrDefault` 讀內容）＋反序列化＋序列化 2 次（含大小檢查）＋UPDATE（**無「內容沒變就不寫」**，HostDayWorkflowState.cs:165-189；`Get` 是 1 次有界讀）。

| # | 階段 | 觸發步驟 | 讀 | 寫 | 備註 |
|---|---|---|---|---|---|
| 1 | 取數：父紀錄 | `ParentSucceeded`（Orchestrator:1184／Netiq:877） | 1 | 1 | 同時算 `ForParentRecord` |
| 2 | 取數：PRTG 腿 | `SetPrtg`（Orchestrator:1191／Netiq:907，僅 PRTG 啟用且該日已發佈）| 0–1 | 0–1 | |
| 3 | 取數：案件意圖 | `CaptureDeliveryVersion`＋`RecordCaseIntents`＋`RecordCaseResults` | 1＋(2 RMW) | 2 | 每主機日無條件 |
| 4 | 取數：派工結果 | `RecordDispatchOutcome` × K | K | K | 每件 unassigned 問題一次 |
| 5 | 取數收尾：PRTG | PrtgDailyPipeline:946-958：本趟碰過的主機日 `Get`＋`SetPrtg` | 1＋1 | 1 | `_touched` 只在該趟實例內 |
| 6 | AI（僅 AiPending 者） | `RestoreFromRecord`、`BeginAi`、`IsCurrentAiVersion`、`CompleteAi` | 3 RMW＋1 Get | 3 | 失敗路徑另加 Get＋RMW |
| 7 | 郵件：窗口掃描 | NotifyAfterRun 起頭 `CaptureDeliveryVersion`（Mail:212）| W | 0 | 14 天 × 全站 |
| 8 | 郵件：ClosePlans | 每主機日 `CaptureDeliveryVersion`＋最多 2×`ReplaceMailPlan`＋`CloseMailPlan` | 1＋3 RMW | 3 | **每次 NotifyAfterRun 對 14 天窗口每個有 version 的主機日重寫一遍**（見 §7 候選 1）|
| 9 | 郵件：寄送 | `PlanWorkflowMail`、`TrackAccepted…`、`MigrateLegacy…`、`TrackWorkflowMail`、`CurrentWorkflowVersion`、`HasAcceptedMailPart`、`IsRecoveryWaiting` | 約 (R×record)×數次 | `ReplaceMailPlan`＋`RecordMail` | |
| 10 | 恢復：熱窗口 | 每 15 分鐘每列（昨天往前 3 天）`ReconcileAuthorityRecord` → `TryUpdateForRecovery` | 3 SELECT（含 `DailyRecords` 存在性）| 0–1（內容沒變即不寫）| **Serializable**，同樣具名 Mutex |
| 11 | 恢復：深層 | 每 15 分鐘 100 列 | 同上 | 0–1 | 全保留期約需 ≈ 5,400 片 ≈ 56 天（推論：540k 列 ÷ 100 列/片 × 15 分鐘） |
| 12 | 恢復：孤兒 | 每片 100 個 key，保留期外／無父紀錄者 `ParentDeleted…`＋`Delete` | 每 key 1–2 | 0–2 | 全部 key 掃一遍同樣約 56 天（推論）；掃完休息 1 天（`OrphanNextSweepAtUtc`，:269）|
| 13 | 恢復：等待標記 | `MarkRecoveryWaiting`（僅超限／無效列）| 1 | 0–1 | |
| 14 | 恢復：RunMonitor 讀 | `_workflow.Get`（畫面）| n | | 不屬後段，列出供對照（RunMonitorService.cs:298,346）|

一個**正常低風險主機日**（無 AI）在取數階段就是：RMW ×(1＋0~1＋2)＋Get ×(1~2)；之後每次 NotifyAfterRun 再加 Get ×2＋RMW ×3（有 version 時），每 15 分鐘恢復掃 3 天窗口再加一次 Serializable 讀交易。

---

## 3. 記憶體

### 3.1 整趟（取數）存活

| 集合 | 大小隨什麼成長 | 證據 |
|---|---|---|
| `DispatchCandidatePool.ByUserId[*].VisibleHostIds`（`HashSet<long>`） | 候選人數 × 可見主機數（全站可見者 ≈ H 個 long × 人數） | DispatchCandidateSource.cs:55-76 |
| `DispatchContext._profiles/_activeOrders/_loads/_noiseByHost/_continuityByHost` | 負責人規則數、進行中單數、雜訊標記數、近 30 天已解決案件數 | DispatchContext.cs:85-131 |
| `DispatchContext._casesByHost` | **逐主機延遲載入、整趟不清（除非該主機有成員成功寫入而被 `InvalidateCasesFor`）**；每台主機載入的是「該主機全部案件（含已結案）」，隨（被處理過的主機數 × 該主機案件史）成長 | DispatchContext.cs:27,194-195,206-211 |
| `DispatchContext._dismissedByHost`、`_chosen`、`_registeredOrders`、`_skipCounts` | 主機數／問題數 | :26,21-23 |
| `NightlyDispatch._addedByOrder/_recurrenceByOrder/_handlerByOrder/_labelByOrder/_createdOrders/_blockedByOrder/_continuedOrders/_midrunCounts/_unassignable` | 本趟涉及的單數；`_unassignable` 為（問題,原因）→ 主機名集合 | NightlyDispatch.cs:52-64；FlushRun 才清（:326-334） |
| `HostDayWorkflowService._touched`（Dictionary<日, HashSet<hostId>>） | 本趟碰過的主機日數（H × 回補天數）；趟後隨實例丟棄 | HostDayWorkflowState.cs:371-385 |
| 本機／NetIQ 路徑的完整 `DailyAnalysisRecord` | 一次一個主機日（非整趟） | — |

### 3.2 常駐（singleton／static）

| 集合 | 成長／上限 | 證據 |
|---|---|---|
| `WorkOrderCoordinator.OrderMutationLock`（static object） | 無集合，僅鎖 | WorkOrderCoordinator.cs:41 |
| `HostDayWorkflowRecoveryHostedService._waiting`／`_oversized`（ConcurrentDictionary） | 只放異常列；超過 2 天且又隔了一整趟深層巡掃才丟（`InMemoryRetention`） | :39,46,54,426-436 |
| `MailNotificationService._ledgerFullWarnedAtUtc` | 有界（過期清掉）| Mail:2112 |
| `AiAnalysisHostedService` 的 `attempted` | 每輪區域變數，大小＝本輪失敗者 | :468 |
| 郵件狀態 blob `MailNotifyState`（雖非記憶體常駐，但每次 `_state.Get()` 整份載入）| `AcceptedMailContributionReceipts` 上限 12,000 鍵、整 blob 上限 4 MiB | MailNotifyStateStore.cs:12,31；Mail:2101 |

### 3.3 一次載入整期紀錄的地方

- `NotifyAfterRunAsync`：`_records.Query(14 天)` 兩次（:280、ClosePlans :227）＋ 14 天的 `notificationDays`（W≈42,000 筆 record×host×date×risk）＋`mailVersions`（W 筆）。
- `Recheck`（每收件人每 lane 2 次）→ `CaptureMailIssueContributionScope`：**本期 14 天＋前期 14 天**各一次整批 `Query`；且 `Flatten` 對每個問題 `JsonSerializer.Serialize(issue)` 存成字串（Mail:1818-1824）。量隨 `VisibleDayRiskLevels`（預設高／中，`SystemSettings.cs:73`）內的主機日數；若設定全勾（`ResolveVisibleDayRiskLevels` 回 null，SystemSettingsService.cs:138-142）則是全部主機日。
- `MailIssueContributionScope.CacheKey()`（MailIssueDigest.cs，檔案後段）把 Current＋Previous 全部貢獻（含每筆 `IssueEvidenceJson`）串成**單一字串**當快取鍵，`BuildIssueRowsCached` 另加排序後的可見主機 id 串（Mail:1790）；每位收件人一份。
- AI：`RetryAiAsync` 每筆讀 14 列完整紀錄（LogAnalysisService.cs:577）。
- 恢復：每頁 100 列、每列最多 `MaximumPayloadPrefixCharacters`，熱窗口每片最多 90 頁（9,000 列）。
- `DispatchContext.Build`：`GetResolvedSince(30 天)`、`GetAllActive`、`noiseMarks.GetAll` 全表。
- 郵件 `BuildContext`：`_hosts.GetAll()`（3000 台複製成清單＋字典）、`_users.GetAll()`、`IssueOwners.GetAll()`（未快取，每次整份反序列化）——**每位收件人每條 lane 呼叫 2～3 次**（Mail:1113,1239,1250,1981）。
- 交辦摘要 `LoadDigestOrder`：每張單 `_hosts.GetAll()` ＋ `DisabledHostKeys`（Mail:764-775）。

---

## 4. 鎖與交易

| 鎖／交易 | 範圍 | 持有期間做的事 | 證據 |
|---|---|---|---|
| `WorkOrderCoordinator.OrderMutationLock`（**static，全行程一把**） | 建單、追加、改派、拆單、取消、退回、結案、回覆、改期限、`RecomputeClosure`、`CloseEmptyDispatchOrder`、`EnsureNightlyOrder`、`WriteNightlyMembers`、`RetractDayAssignments`（Reentrant Monitor） | **持鎖期間全是資料庫呼叫**：`GetActiveFor`／INSERT／`AppendEvent`；`GetOpenForHost`、`orders.Get`、`CapturePrtgEvidence`（含 `records.GetOne` 讀整列）、`SaveMany`×2、`AppendLogs`；Web 端操作（含 `SweepClosures`→`RecomputeClosure`）與夜間派工排同一條隊 | WorkOrderCoordinator.cs:41,93,170,358,425,513,554,679,703,763,808,835,874,970,979 |
| `DispatchContext.Gate`（每趟一把） | 三路並行共用；`DispatchDay` 與 `FlushRun` 整段 | 決策（記憶體＋`CasesFor` 的 DB 讀）→ 取 `OrderMutationLock`（建單、寫成員）→ **持 Gate 期間還呼叫 `onResult` 回調 → 工作流 RMW（具名 Mutex＋交易）**（Report，NightlyDispatch.cs:218-224；呼叫點 :114-188 都在 :86 的 lock 內） | NightlyDispatch.cs:86,281 |
| 鎖順序 | Gate → OrderMutationLock → 工作流 Mutex／EfJsonBlobStore 實例鎖；Web 路徑只取 OrderMutationLock（不取 Gate）→ 沒有反向，不會死結（推論：未發現反向取鎖） | | |
| `EfJsonBlobStore._lock`（**實例欄位**，EfJsonBlobStore.cs:29,203） | 每個 `backend.Blob(key)` 都 `new` 一個新實例（StorageBackend.cs:284），所以工作流每次 Update 的行程內鎖等於**無跨呼叫序列化效果**；真正的互斥靠 ① 具名 Mutex（逾時 30 秒，`Update` 取不到就丟 `TimeoutException`）② DB 交易 | | EfJsonBlobStore.cs:203-283；HostDayWorkflowState.cs:350-358 |
| 工作流 `Update` 的隔離層級 | **預設（Unspecified）**；`TryUpdateForRecovery`／`TryUpdateForOrphanRecovery`／`TryMarkRecoveryWaiting` 為 **Serializable**（並帶 `skipUnchangedContent: true`） | | HostDayWorkflowState.cs:172-185 vs :226,274,338 |
| `MailNotifyStateStore.MutateWithContext` | **Serializable**（預設參數），整份 ≤4 MiB blob 讀改寫 | 每次 `_state.Update`（outbox、預約、streak、MarkSent…）一個 | MailNotifyStateStore.cs:43-58 |
| `MailNotificationService._notifyGate`（SemaphoreSlim 1） | `NotifyAfterRunAsync` 整段（含 **SMTP 寄送、每收件人 Recheck 的整批 DB 查詢、工作流寫入**） | SMTP 呼叫（`SendSafeDetailedAsync`，逾時 30 秒／封，註解 Scheduler:597）在 gate 內；RetryPendingUrgent 也要排這個 gate | Mail:72,186-303,1528 |
| `EfRecordHandlingStore._logLock`／`EfJsonLogStore._lock`（實例鎖） | 每次 `AppendLog`：鎖內 `ReadLastLines(100)` ＋ 100 次逐行 `JsonLogParser.Parse` ＋ INSERT | 夜間實例與 Web singleton 是兩個實例，互不序列化，靠探號避免重號（EfRecordHandlingStore.cs:125-132 的註解） | |
| `RecoveryService._lease`（具名 Mutex） | 每頁整段（含 `Reconcile`、`RetryPendingReports`） | 逐列工作流 Serializable 交易＋（有待補報告時）重建 `AIService`、`LogAnalysisService`、讀整份 `HostStore` | RecoveryHostedService:194,229,257,444-501 |
| AI | `Parallel.ForEachAsync` 分片；AI HTTP 呼叫經全站 `IAiRequestGate`；每筆工作流寫入沒有持有 AI 呼叫期間的鎖（`RestoreFromRecord`/`BeginAi` 在呼叫前、`CompleteAi` 在呼叫後）| | AiAnalysisHostedService.cs:518-569 |

---

## 5. 跨步驟耦合（連鎖一條一條列）

**C1. 取數 → 工作流版本 → 郵件／AI 重評**
- 取數重跑同一主機日（或 `ParentSucceeded` 判定指紋變）→ `DecisionVersion++`、`ResetDelivery`（清空案件意圖與郵件意圖，HostDayWorkflowState.cs:399-423,1011 `ResetDelivery`）、Ai 回到 Waiting → 後續 `CaptureDeliveryVersion` 版本不同 → 郵件 `CurrentWorkflowVersion` 為 false、AI `IsCurrentAiVersion` 為 false → 舊寄信／舊 AI 結果被捨棄（Mail:2073-2080；AiAnalysisHostedService:581-589）。

**C2. AI 寫回 → 郵件內容指紋改變（推論，需實測確認）**
- `TryAttachAiResult` 寫入 `AiAnalyzed=true`、`AiPending=false`、可能上調 `RiskLevel`（EfAnalysisRecordStore.cs:560-585）。
- 父指紋 `ForParentRecord` 不含這些欄位（HostDayWorkflowState.cs:1079-1103），所以**不**推進 `DecisionVersion`，也不重設已完成的 PRTG／案件腿。
- 但郵件意圖 `WorkflowMailIntent` 用的是 `ForRecord(record)`（Mail:2047-2056），`ForRecord` 含 `RiskLevel/AiAnalyzed/AiPending/DetailPruned`（HostDayWorkflowState.cs:1127-1163）→ AI 寫回後，該主機日在下一次 `NotifyAfterRun` 被算成「新內容」：`SummarySentKeys`／`UrgentSentKeys` 以含內容雜湊的鍵比對（Mail:1061-1066,1189），舊鍵不命中，該日重新進 `qualifying`／`pending`、重走 `ResolvePerRecipient`／`Recheck`；實際重寄由每問題貢獻帳本（`FilterAcceptedMailContributions`，Mail:2279-2304，用的是問題 JSON，不含 AI 欄位）擋住，所以**不會重寄，但整套成本重付**，且該新鍵不會被標記已寄，直到窗口（14 天）滑出前每次 NotifyAfterRun 都重評。
- AI 寫回**不會**主動觸發 `NotifyAfterRun`（AI 輪尾只 `_dataVersion.Bump()`，AiAnalysisHostedService.cs:361）；AI 上調風險而新達門檻的主機日要等下一趟取數結束或緊急意圖重試才會被通知（事實；是否為預期行為我不判斷）。

**C3. 掛接／派工寫入 → 資料版本戳 → 快取失效與 CaseDaySync 喚醒**
- 取數期間夜間派工的寫入**不 Bump**（Bump 只在 EndRun 之後 Scheduler:580、AI 輪尾 AiAnalysisHostedService:361、CaseDaySync 有處理時 :80、HTTP 非 GET 端點 Program.cs:227-232）。
- 取數結束 `Bump` → `IssueRankingCache`／`ActionableSnapshotCache` 整批失效（ServiceCollectionExtensions.cs:379,417）；`CaseDaySyncHostedService` 偵測版本變化立刻跑一次 `ApplyPendingCaseDays(200)` ＋ `SweepClosures(200)`（:48-65）。AI 每輪結束也 Bump → 再喚醒一次。

**C4. 工作流恢復 ↔ 取數／AI（推論：每個新主機日會被恢復多改寫一次）**
- 取數寫入 `ParentRunId = RunId 數字字串`（Orchestrator:1184／Netiq:877）；恢復的「是否已是目前狀態」判定 `IsRecoveryStateCurrent` 要求 `state.ParentRunId == record.LatestNetiqAttemptAtUtc?.ToString("O")`（HostDayWorkflowState.cs:1013-1016）。兩者格式不同 → 恢復的第一次熱窗口掃描判為「不是目前狀態」→ `ApplyRecoveredParentSuccess` 寫回 ISO 字串（:906-934）。之後 AI 的 `RestoreFromRecord` 與恢復寫同樣的值（:549），所以不會互相拉扯；**不是迴圈，是每次取數寫入後一次性的多餘 RMW**。需實測確認。
- `PruneDetails`（保留清除）對超過 `RawEventRetentionDays` 的列執行 `WriteRevision+1`、清 `content_json`（EfAnalysisRecordStore.cs:1064-1071）→ 該列在恢復深層掃描時被反序列化成「殘根」（`Deserialize` DetailPruned 分支，:1980-1996），`ForParentRecord(殘根)` 與原指紋不同 → `ApplyRecoveredParentSuccess` 判「changed」→ `DecisionVersion++`＋`ResetDelivery`（:922-934）。這是「保留清除 → 恢復 → 工作流重設」的連鎖（推論；影響的是已超過 120 天、不再通知的列，但仍是每列一次寫入）。
- 恢復讀列時與 AI／取數寫回的衝突靠 `WriteRevision` 圍欄（`TryUpdateForRecovery` 只在 revision 吻合時才寫，:205-207），衝突時回 false，下一輪重試（RecoveryHostedService:373-374 log Warn）。

**C5. 郵件重試自我維持（有「A 觸發 B、B 又觸發 A」的形狀）**
- `RetryPendingUrgentAsync`（每 60 秒）只要 UrgentOutbox 有 `Status` 不在 {smtp-accepted, cancelled-formal-revoked, no-qualified-recipient} 的項目（Mail:319-321），就呼叫整支 `NotifyAfterRunAsync`；`NotifyAfterRunAsync` 本身會 `_state.Update` 把意圖設回 `pending`（Mail:1212）並在 `ClosePlans` 大量寫工作流（候選 1）。SMTP 持續失敗（`failed-or-unknown`）或收件人沒有可見主機等情況下，這條每分鐘重跑一次整套（含 14 天窗口 Query、ClosePlans 寫入）直到意圖終結——**迴圈成立條件是 SMTP 一直失敗；沒有指數退避，只有收件人 streak 熔斷（`RecipientFailureThreshold=3`，Mail:64）**。
- `ReplaceMailPlan` 把 `MailPlanClosed=false`（HostDayWorkflowState.cs:785），`CloseMailPlan` 再設回 true（:795-808）→ 每輪都讓所有窗口內主機日的郵件腿短暫變成「未完成」，RunMonitor 的 `MailWorkflowState`（RunMonitorService.cs:362-365）與 `IsComplete` 會閃動。

**C6. AI ↔ 取數（有設計好的錯開）**
- `SelectNextBatch` 以 `SchedulerRunState.FindingsWaitCutoff` 閘門，取數中尚未發佈 PRTG finding 的日期不判讀（AiAnalysisHostedService.cs:430-451）；PRTG finding 一發佈就觸發 AI（事件 `PrtgFindingsBecameReady`）。AI 寫回不會重新觸發取數。**沒有 A↔B 迴圈**。
- `MissingDateFinder`／`NeedsBackfill` 明確不把 `AiPending` 當缺漏日，避免取數與 AI 互相打架（HostDayPostProcessor.cs:123-150）。

**C7. 派工閘門互相餵資料**
- `FlushRun` 的 `FlushContinuationStops` 更新單的 `ContinuationStoppedReason`，影響下一趟 `DispatchContext.Build` 的決策與 Web 畫面；`RecordNightlyAppended` 更新 `LastAppendedAt`（WorkOrderCoordinator.cs:636-644）。

---

## 6. 父指紋

| 函式 | 做了什麼 | 呼叫點（檔案:行） |
|---|---|---|
| `ForParentRecord(record)` → `ParentFingerprint(Compact(record), record.RecordId)`（HostDayWorkflowState.cs:1068-1103）| ① `RecordStorageShaper.Compact`：**整份 `DailyAnalysisRecord` 淺複製＋每個 `LogIssueSignature` 重建一個新物件**（含 `SourceEvidence.BoundObservations`）、`SampleMessages = new List` ② 匿名物件序列化成 JSON（含 `TopIssues` 去掉 PRTG、`CorrelationAlerts` 依 `PrtgCorroboration.OwnsCorrelation` 過濾）③ UTF-8 位元組 ④ SHA-256 ⑤ 十六進位字串 | 取數：Orchestrator:1185、NetiqPipelineService:878（各一次/主機日）；`RestoreFromRecord`（:550，AI 每筆一次）；`IsRecoveryStateCurrent`（:1012，恢復每列每 15 分鐘一次）；`ApplyRecoveredParentSuccess`（:909，僅在判定非目前狀態時）|
| `LegacyParentFingerprint(record)`（:1075）| 同上但**不 Compact**，直接序列化傳入內容 | `RestoreFromRecord`（:556，**急切求值**——不論是否需要都先算，作為 `equivalentFingerprint` 傳入）；`ApplyRecoveredParentSuccess`（:919，僅當主指紋不符才算，短路 `||`）|
| `ForRecord`／`ForReportInput`／`PrtgInputFingerprint`（同檔 :1117-1163；`ForRecordWithIdentity` 序列化**整個 `TopIssues` 與全部欄位**）| 不經 Compact；序列化整份 | AI：:583（`current`、`full` 各一次）、:602；`TryAttachAiResult` 在 Serializable 交易內（EfAnalysisRecordStore.cs:421-425,468-469,551,568,609）；`RiskReportService:70-71`；`LogAnalysisService:202-206,492-533,579`；**郵件：`WorkflowMailIntent`（Mail:2047-2056）每次呼叫一次 `ForRecord` ＋ `PrtgFindingMapper.Fingerprint`**——`WorkflowRecordKey`（逐 record 逐 lane 多次）、`PlanWorkflowMail`、`TrackWorkflowMail`（每收件人每 record）、`IsWorkflowMailPartAccepted`、`SameWorkflowMailContent`（一次比對呼叫 2 次）都各算一次，**同一筆 record 在一次 NotifyAfterRun 內被重複算多次**（無記憶化）|

同一筆紀錄在一次操作內是否重算：
- **AI 每筆**：`ForParentRecord`＋`LegacyParentFingerprint`（RestoreFromRecord）＋`ForRecord(current)`＋`ForRecord(full)`＋`ForRecord(full)`（:602）＋`PrtgInputFingerprint` ×2＋（`RetryAiAsync` 內 :579、`RiskReportService`、`FinalizeGuardedReportAsync` :516-533 各 3–4 次 ForRecord/ForReportInput）＋ `TryAttachAiResult` 交易內再 `ForRecord`／`PrtgInputFingerprint` 各一次 → **同一筆在一筆 AI 作業中 ForRecord 至少 4–6 次**。
- **恢復每列**：`ForParentRecord` 1 次（非目前狀態時 2 次，含 `ApplyRecovered…`），之後 `TryUpdateForRecovery` 內另對 state 做 2 次序列化比較（:214,221）。
- **取數**：低風險日 `ForStorage` 已在 `Append` 時做過一次 `Compact`（EfAnalysisRecordStore.cs:99），`ForParentRecord` 再做一次。
- 精簡投影「冪等」，但每次都配置新物件；沒有任何快取以 `(RecordId, WriteRevision)` 為鍵。

---

## 7. 最佳化候選

（每條：現況／成本隨什麼成長／可能的等價改法／**影響到哪些其他步驟或呼叫端**／風險）

### 候選 1：`ClosePlans` 與 `ReplaceMailPlan`／`CloseMailPlan` 的無條件寫入（最大單點）
- 現況：`NotifyAfterRunAsync` 先翻 14 天分頁（Mail:207）對每個主機日讀一次工作流（:212），結尾 `ClosePlans`（:222-267）再對每個有 version 的主機日：讀（:256）→ 不在所需 lane 時 `ReplaceMailPlan` 空計畫（:261,263，各一次 RMW）→ `CloseMailPlan`（:264，RMW）。`HostDayWorkflowStore.Update` 沒有「內容不變不寫」（HostDayWorkflowState.cs:165-189；恢復路徑有 `skipUnchangedContent`，:227）。**信件功能關閉時（:268-272）仍會走完這段**。
- 成本：每次 `NotifyAfterRun` ≈ W×(讀 1~2 ＋ 寫 ≤3)，W≈H×14≈42,000 → 最多約 126,000 次「具名 Mutex＋交易＋2 讀＋1 寫」；觸發頻率＝每趟取數結束＋緊急重試每 60 秒（C5）。
- 等價改法：(a) `Update` 加「序列化後與原內容相同就不寫」（恢復路徑已證明可行：HostDayWorkflowState.cs:214-218）；(b) 在 ClosePlans 內先用已讀到的 state 判定是否已是目標狀態（`MailPlanClosed && MailRequiredLanes 相同 && 各 lane 無殘留 intent`）再決定要不要呼叫；(c) 不需要的 lane 一次動作（把兩次 `ReplaceMailPlan`＋`CloseMailPlan` 合成單一 Update）。
- 影響範圍：`ReplaceMailPlan` 呼叫端＝`MailNotificationService`（Mail:261,263,2327）；`CloseMailPlan`＝Mail:264；工作流 `UpdatedAt` 目前**沒有 Web 端消費者**（grep `UpdatedAt` 於 RunMonitorService.ApplyWorkflow 未使用，RunMonitorService.cs:352-368）；`MailWorkflowState`／`IsComplete` 的閃動（C5）會消失——屬可見行為差異但朝正確方向；`RecoveryHostedService` 的 `IsRecoveryStateCurrent` 不讀郵件欄位（:1008-1033）；測試：搜尋 `ReplaceMailPlan`／`CloseMailPlan`／`MailPlanClosed` 的測試要同步（我未逐一列出）。
- 風險：低～中。`Update` 是共用基礎（取數、AI、郵件、恢復都用），改共用層要全部呼叫端回歸；較安全是只改郵件這兩個轉換函式。

### 候選 2：郵件 `Recheck` 每位收件人每條 lane 兩次整批重查
- 現況：`SendPerRecipientAsync` 寄前 `recheck`（Mail:1483）與寄後 `onRecipientResult`（:1146,1367）各呼叫 `Recheck`；每次 `_records.Query(14 天)`（Mail:1106,1253,1975）＋`CaptureMailIssueContributionScope`（2 次 `Query`，Mail:1808）＋`BuildContext`＋`ResolvePerRecipient`（含每收件人 `GetVisibleHostIds` 與 `HostIdsFor` SQL）＋每筆 `CurrentWorkflowVersion`（工作流讀）。
- 成本：R（收件人）× 2 lane × 2 次 × (3 次整批 Query ＋ Resolve) ＋ record 數 × 工作流讀。
- 等價改法：一趟 `NotifyAfterRun` 內快取「設定指紋、`Query` 結果、`MailContext`、Resolve 結果」，以（設定指紋＋資料版本戳或 `RecordId`/`WriteRevision` 集合）驗證沒變才沿用；寄前寄後兩次若中間沒寫入可共用同一份。`CurrentWorkflowVersion` 對同一 record 在一次 Recheck 內只讀一次（已有 `mailVersions` 的快取版本做比較基準，但每次仍重讀 DB 做當前值比對）。
- 影響範圍：`Recheck` 的目的是「寄送過程中資料或設定變了就不寄」（Mail:1100-1133 註解與判定）——任何快取都會削弱這個保證，需保留「寄前重查」的語意（至少在 SMTP 呼叫前必須是新的）；`SendRunSummaryAsync`（Mail:1100）、`SendUrgentNotificationsAsync`（:1242）、`SendDigestAsync`（:1969）三處各有自己的 Recheck；測試 `MailNotificationService*Tests` 大量依賴此行為。
- 風險：高（語意是安全閘門）。建議只動「寄後」那次（`recordingOutcome:true`），且需先確認其結果用途（Mail:1146-1153）。

### 候選 3：`CaptureMailIssueContributionScope`／`CacheKey()` 的整期展開
- 現況：每次呼叫讀本期＋前期兩個 14 天窗口並為每個問題序列化一份 JSON（Mail:1799-1827）；`CacheKey()` 再把全部貢獻（含 JSON）串成單一字串當字典鍵（MailIssueDigest.cs `CacheKey`），`BuildIssueRowsCached` 每位收件人都做（Mail:1790）。
- 成本：28 天 × 高／中風險主機日 × 問題數，字串體積＝這些 JSON 總和 × 收件人數（同可見範圍的收件人共用一份鍵，但鍵本身要先組出來才知道共用）。
- 等價改法：鍵改用雜湊（對每筆貢獻已有 `AcceptedMailContributionKey` 的 SHA-256 形式）、貢獻在 `Capture` 時只存雜湊不存 JSON、兩個窗口改成一次 `Query` 後分流。
- 影響範圍：`MailIssueContributionScope` 消費端＝`MailIssueDigest.BuildDetailed`（`FilterIncludedContributions`／`BuildContributionMap`／`ResolveOverdueKeys`）、`CreateRenderedMailContributions`、`TryReserveRenderedMailContributions`（用 `IssueEvidenceJson` 計貢獻鍵，Mail:2205-2207）、`BuildRecipientIssueRows`（Mail:1769-1775，用 `AcceptedMailContributionKey(…, item.IssueEvidenceJson)`）——**`IssueEvidenceJson` 是 durable 帳本鍵的輸入**，不能改格式（升級會讓既有收據失效，見 Mail:2027-2029 註解與 `MailNotifyStateStore.cs:21` 保留天數說明）；只能改「中間字串怎麼保存」。
- 風險：中（鍵演算法不可動）。

### 候選 4：`WorkflowMailIntent`／`ForRecord` 無記憶化
- 現況：每次呼叫序列化整個 `TopIssues` 與全欄位＋SHA-256（HostDayWorkflowState.cs:1127-1163；Mail:2050-2054）。
- 成本：（record 數 × 收件人數 × 每輪呼叫次數）。
- 等價改法：在一次 `NotifyAfterRun`／一次 `Recheck` 內以 `(RecordId, 物件參考)` 記憶化 `ForRecord` 與 `PrtgFindingMapper.Fingerprint`（純函式，輸入為不可變快照時等價）。
- 影響範圍：`ForRecord` 呼叫端見 §6 表（AI :583,:602、EfAnalysisRecordStore:421,468,568、LogAnalysisService、RiskReportService、Mail）。記憶化只能放在呼叫端區域，不能放在 `record` 物件上——`record` 物件在 AI 路徑會被就地修改（`FinalizeGuardedReportAsync` 對 `record` 寫 `ReportFile` 等，LogAnalysisService.cs:540-548）；`AttachPrtgFindings` 也就地改 `record.TopIssues`（HostDayPostProcessor.cs:205-227）。
- 風險：低（若限定在單次操作範圍）；中（若做成物件層快取，會因就地修改而失準）。

### 候選 5：`AttachCase` 對沒有問題的主機日仍做 3 次工作流往返
- 現況：`hasWorkflow` 時無條件 `CaptureDeliveryVersion`＋`RecordCaseIntents`＋`RecordCaseResults`（HostDayPostProcessor.cs:247-283），即使 `topIssues` 為空；每件 unassigned 問題再各一次 `RecordDispatchOutcome`（RMW）。
- 成本：每主機日 ≥3 往返 ＋ K。
- 等價改法：同一主機日的 intents／results／各 outcome 先在記憶體累積，最後一次 `Update` 寫回；或 `topIssues` 為空時只在「狀態尚未標記計畫已關閉」才寫。
- 影響範圍：`RecordCaseIntents`／`RecordCaseResults`／`RecordDispatchOutcome` 的語意由 `CaseIsComplete`（HostDayWorkflowState.cs:84-86）與 `HostDayPostProcessor.AttachCase` 回傳值（失敗→下次補掛）決定；`CaseDispositionReasons` 持久化原因（:654）；`IsComplete`／`WorkflowOutcome` 被 RunMonitor 與恢復使用；NightlyDispatch `Report` 的錯誤處理（NightlyDispatch.cs:218-224）要求「結果已確定、回寫失敗保持待補而不重複派工」——合併寫入會改變「某件問題的回寫失敗」的粒度。
- 風險：中高（失敗語意）。

### 候選 6：`DispatchContext` 持 `Gate` 期間做工作流 RMW 與逐主機整份案件載入
- 現況：`DispatchDay` 在 `lock(_ctx.Gate)` 內（NightlyDispatch.cs:86）呼叫 `onResult`→工作流 Update（:114-188 所呼叫的 `Report` 在 lock 內）、`CasesFor` 整主機案件讀（DispatchContext.cs:206-211）。三路並行的另外兩路在此排隊。
- 成本：每件問題一次持鎖的工作流 RMW；`InvalidateCasesFor` 讓同主機之後的日期重載整份案件（NightlyDispatch.cs:198）。
- 等價改法：`onResult` 回調先收集，離開 Gate 後再寫；`CasesFor` 改成只載入「關鍵為這些簽章的案件」（現行 `GetOpenForHost` 已有、`IsDismissed` 需要的是最近一件未取消案件，可由查詢端 SQL 取代整主機載入）。
- 影響範圍：`DismissedCases` 的判定規則唯一一份（CLAUDE.md 紅線，DispatchContext.cs:240-256），`DispatchContext.DismissedKeys` 被 `DayHandlingCommandService.PlanCasesForDay`（Web，:369）共用；`PreloadDismissed` 被 `WorkOrderBoardService`（:458）使用。改載入方式不可分岔判定。
- 風險：中。

### 候選 7：`IssueOwnerStore` 未快取、`AttachNewDay` 每主機日讀 `issueProfiles.GetAll()`
- 現況：`IssueOwnerStore : JsonBlobCollection<IssueProfile>`，預設 `cached:false`（IIssueOwnerStore.cs:24-33），`AttachNewDay` 每主機日 `GetAll()`（IssueCaseCoordinator.cs:194）；郵件 `BuildContext`（Mail:881）、`CreateAnalysisServiceForHost`（AiAnalysisHostedService.cs:782）、恢復（:466）、候選人 `GetIssueOwnedHostIds`（HostVisibilityResolver.cs:90）都會 new 或呼叫。
- 成本：（主機日數 ＋ 收件人數 × 2~3 ＋ AI 分片數）× 負責人檔大小。
- 等價改法：`cached:true`（比照 `NoiseMarkStore`、`HostStore` 的版本探測快取）。
- 影響範圍：**所有 `IIssueOwnerStore` 消費端**（`GetAll` 回的物件是共用參考，呼叫端不可修改——`JsonBlobCollection.Read` 註解 JsonBlobCollection.cs:77-82）；先 grep 有無就地修改 `GetAll()` 回傳物件者（我未逐一檢查所有消費端）。`MuteAwareSuppressionStore` 也包這個 store。
- 風險：低～中（取決於有無就地修改）。

### 候選 8：`AttachNewDay` 逐筆 `AppendLog`
- 現況：掛入既有案件每件問題 `_handlingLog.AppendLog`（IssueCaseCoordinator.cs:220,253），每次先 `ReadLastLines(100)` 並逐行 JSON 解析探號再 INSERT（EfRecordHandlingStore.cs:125-132,193-200；EfJsonLogStore.cs:103-128）。同一流程的 `WriteNightlyMembersLocked` 已用批次 `AppendLogs`（:630）。
- 成本：掛接件數 A × (100 行讀＋解析＋INSERT)。
- 等價改法：累積成清單，`SaveMany` 同處一次 `AppendLogs`。
- 影響範圍：歷程順序與 `LogId` 連號（`AppendLogs` 一次配號、`AppendLog` 每次配號，兩者結果對讀取端等價）；`SyncStatus`（同檔 :128-134，Web 互動）仍逐筆，不在夜間路徑。
- 風險：低。

### 候選 9：`CountPendingAi` 與 `QueryPendingAi` 的呼叫頻率
- 現況：每輪 `TriggerRunAsync` 查 1 次＋迴圈首批 `pendingEstimate ??=` 再查 1 次（AiAnalysisHostedService.cs:272,508）；跟隨取數期間每次輪詢先失效快取（:172）；`OnFetchFollowUp` 每事件失效（:138）。
- 成本：實機 7～15 秒/次（註解 :480）× 每輪 ≥2 ＋ 跟隨期間每分鐘 1 次。
- 等價改法：`TriggerRunAsync` 把已查到的 `totalPending` 傳進處理迴圈當 `pendingEstimate` 初值（兩次之間沒有寫入就是同一個數）；`OnFetchFollowUp` 查完的數寫回快取而不是只失效。
- 影響範圍：`AiAnalysisRunState.GetPendingAiCount`（ScheduleController.cs:519 也用，狀態 API）、`ReportProgress` 的分母（:509）、`TryBeginRun(trigger, totalPending, …)`。
- 風險：低。

### 候選 10：`RetryAiAsync` 每筆 `ReadRecent(14)` 與 `GetOne` 多次
- 現況：每筆讀 14 列完整紀錄（LogAnalysisService.cs:577），外加 `GetOne` 2～3 次（AiAnalysisHostedService.cs:546,581,618）。
- 成本：AI 待補數 × 16+ 列完整反序列化。
- 等價改法：同一主機分片內（序列處理，同主機連續日）滑動窗口重用歷史；`GetOne` 第二次改用 `TryAttachAiResult` 內已有的 Serializable 讀（它本來就會讀並驗證指紋）。
- 影響範圍：「歷史引用前一天的 AI 結論」要求同主機舊→新先寫回（`SelectNextBatch` 註解 :427-429）——滑動窗口必須在寫回後更新；`TryAttachAiResult` 是唯一正式寫回點（EfAnalysisRecordStore.cs:520）。
- 風險：中。

### 候選 11：恢復熱窗口每 15 分鐘全量重掃
- 現況：熱窗口（昨天往前 3 天，約 H×3＝9,000 列）每片整個再掃一遍（每頁 100 列＋最多 90 頁/片，RecoveryHostedService:148-157,696-697），每列一個 Serializable 交易＋具名 Mutex（HostDayWorkflowState.cs:192-233）＋讀整頁 ContentJson 前綴＋`ForParentRecord`。完成後下一片 `HotCompletedAtUtc` 重置（:199-203）。
- 成本：H×3 每 15 分鐘。
- 等價改法：以 `WriteRevision`/`RecordId` 的「上次已對帳版本」做增量（列版本沒變且啟用狀態沒變就跳過，超大列已有這種記憶 `_oversized`，:326-331）。
- 影響範圍：恢復的目的是修復「寫入中斷」造成的不一致，增量化會削弱它對「工作流側單獨損壞」的覆蓋（`IsRecoveryStateCurrent` 同時檢查工作流 blob 內容，不只看列版本，HostDayWorkflowState.cs:1008-1033）；`GetWaitingSummary`（健康頁）與 `_waiting` 的 2 天保留語意。
- 風險：中高。

### 候選 12：工作流 blob 保留期外的清掃速度
- 現況：保留清除不含 `workflow_*`（RetentionPruner.cs 無 workflow）；只有恢復的孤兒頁（每片 1 頁＝100 key，:173-183）＋完成一輪後休息 1 天（:269）。推論：key 數 ≈ H×D＋積壓，一輪約 56 天，過期 blob 可能存活到下一輪掃到。
- 成本：lf_blobs 列數與 `StartsWith("workflow_")` 分頁掃描。
- 等價改法：`RetentionPruner` 加一段以 blob key 日期後綴＋`UpdatedAt` 做批次刪除（比照 PRTG 診斷包的做法，RetentionPruner.cs:178-185）；
- 影響範圍：恢復孤兒清掃（`Delete` 的具名 Mutex 語意）、`GetExistingHostDaysPage`（分頁游標 `workflow_recovery_cursor` 要排除，:114-118）、檔名格式 `Key()`＝`{hostId}_{yyyyMMdd}`（HostDayWorkflowState.cs:358）。
- 風險：低～中。

### 候選 13：郵件狀態 blob（≤4 MiB）每次整份讀寫
- 現況：`_state.Get()`／`_state.Update`／`GetSuspendedRecipients()` 在 MailNotificationService.cs 內合計 38 處（grep 計），Serializable，每次 ≤4 MiB 反序列化＋序列化；`RetryPendingUrgentAsync` 每 60 秒 `Get` 一次、`BuildRecipientIssueRows` 每收件人一次、`FilterAcceptedMailContributions` 每次呼叫一次。
- 成本：呼叫數 × blob 大小（帳本上限 12,000 鍵，穩態可接近滿載）。
- 等價改法：單次操作內快取一次 `Get`（讀者只讀）；`RetryPendingUrgentAsync` 的探測改成只讀一個小欄位（例如把 `UrgentOutbox` 未終結計數另存，或用 `EfJsonBlobStore.ReadVersion` 比對上次已看版本）。
- 影響範圍：`MailNotifyStateStore` 所有呼叫端（Mail、AdminReminder、Health、retention）；`ReadBoundedValueWithVersion` 的有界讀語意。
- 風險：中。

### 候選 14：`DispatchCandidateSource.Build` 每位候選人重算可見主機
- 現況：每位候選人 `HostVisibilityResolver.GetVisibleHostIds`（DispatchCandidateSource.cs:64-65）：`userGroups.GetAll`、`access.GetAll`、`issueOwners.GetAll`、`HostIdsFor` SQL（有負責問題者）各一次。`WorkOrderCandidateService.Get`（Web，:65-80）也有同一形狀，已以「快照只取一次」改善。
- 成本：候選人數 × 4 次讀＋SQL。
- 等價改法：把 `userGroups/access/issueOwners` 讀出一次後傳入重載（`HostVisibilityResolver` 已有 `PrtgHostSnapshot` 重載，HostVisibilityResolver.cs:117）；`HostIdsFor` 可批次合併（`HostIdsByIssue` 介面已有，EfIssueAggregateQuery.cs:388）。
- 影響範圍：`HostVisibilityResolver` 三條授權路徑「規則只有一份」（檔頭註解 :6-16），`VisibilityPathEquivalenceTests` 守門；`VisibilityService`（Web 登入者路徑）、Mail `GetVisibleHostIds`、`WorkOrderCandidateService` 共用。只能新增重載、不可複製規則。
- 風險：低～中。

### 候選 15：每個交辦摘要單 `LoadDigestOrder` 內 `_hosts.GetAll()`
- 現況：每張單呼叫 `_hosts.GetAll()` 與 `WorkOrderQueries.DisabledHostKeys`（Mail:764-775）。
- 等價改法：`CheckAndSendWorkOrderDigestAsync` 開頭算一次傳入（`users` 已如此，Mail:639）。
- 影響範圍：`WorkOrderQueries.DisabledHostKeys` 是「唯一一份」口徑（註解 Mail:769）；只是提到呼叫外面。
- 風險：低。

---

## 8. 不確定或沒讀到

1. **`AdminReminderService.ComputeResult`（RunAfterNightly 內）** 沒追完；只知道它在 NotifyAfterRun／EnqueueNightlyDispatch 之後同序呼叫、內含 `_state.Update` 兩次。
2. **`IssueCaseCoordinator.SubmitCaseDays`／`FindCandidateDays`／`ResolveEligibleDays`**（日層級指派 `Assign`→`WorkOrderCoordinator.Create`→`MembersInto` 的回溯展開）沒深入：只確認 Web 互動路徑每問題一次 `Create`＋`PreviewMembers`（DayHandlingCommandService.cs:392,440），且 `ValidateMembers` 每次讀整份 `_hosts.GetAll()`（WorkOrderCoordinator.cs:272）。夜間路徑不走它（`WriteNightlyMembersLocked` 自成一套，註解 :536-538）。
3. **「統一標記」**（`IssueHandlingCommandService.SetIssueStatusBatch`／`BulkCloseIssue`、`DayHandlingCommandService.Update`）只看了方法清單，未追其往返與鎖；它們屬 Web 互動，不在取數後段自動鏈上，但經 `OrderMutationLock`／`SyncStatus` 與夜間派工共用資源。
4. **工作流 blob 的實際大小分布** 未量測；上限 64 KiB（HostDayWorkflowState.cs:123），但 `CaseIntents`（≤64 個 64 字元雜湊）＋郵件意圖（≤4）＋Dictionary 欄位，典型應在數 KB，未證實。
5. **`EfIssueAggregateQuery.Aggregate`／`ActionableOccurrences`／`LatestOccurrences`、`OccurrenceStatusResolver.Resolve`** 在 `MailIssueDigest.BuildDetailed` 內，成本只從呼叫次數估，未讀 SQL。
6. **`PrtgDailyPipeline` 其餘部分**（取數、規則評估、`PrtgFindingsRegistry.AttachExclusive`）不在本範圍，只讀了尾段的工作流對帳（:930-965）。
7. **`SuppressionStore`／`MuteAwareSuppressionStore`／`KnownIssueRuleStore`** 每次 `new`、每次讀 blob 的頻率沒逐一追（AI 分片 :780-795、恢復 :464-467、交辦摘要 `getRules`）。
8. **C2（AI 寫回造成郵件內容指紋改變）與 C4（`ParentRunId` 格式差異、`PruneDetails` 殘根造成重設）是由程式碼靜態推導，沒有實際執行或看測試確認**；建議規劃階段先寫會紅的定向測試再決定要不要動。
9. **工作流 `Update` 在 SQLite 與 SQL Server 兩個後端的實際 SQL 次數**（`ReadBoundedInContext` 在 SQL Server 分支的語句、`SerializeSqlServerWriters` 是否啟用）只讀了 SQLite 分支與 `MutateWithContext` 骨架；SQL Server 端可能每次再多一次 `sp_getapplock`（EfJsonBlobStore.cs:287-302，僅在 `_serializeSqlServerWriters` 時）。
10. **BatchRunRecorder／執行紀錄寫入**（每趟 `RecordDayAnalyzed` 等）不在本範圍。
11. 保留清除每段 `Prune` 的 SQL 實際索引走法（`OwnedRows(ctx)`、`HostNameKey`）未檢查執行計畫。
12. 我沒有看測試專案，因此各候選「測試要同步」只能說「需要」，沒有列出具體測試類別（CLAUDE.md 的守門測試清單：`HandoffClockGuardTests`、`LongLivedCollectionGuardTests`、`IssueCaseCreationGuardTests`、`ControllerRouteSnapshotTests` 等與本批相關者，應在規劃時依實際改動逐項對照）。
