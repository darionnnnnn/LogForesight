# 第 78 輪批次 1 實作規格：工作流狀態的多餘讀改寫

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「工作流父指紋」「不要讓…」各條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。
看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。同樣的輸入，改動前後寫進工作流的最終狀態、寄出的信、規劃結果都相同（`UpdatedAt` 除外），只減少讀寫次數。

白名單：
- `LogForesight.Core/Service/HostDayWorkflowState.cs`（`HostDayWorkflowStore`、`HostDayWorkflowService`）
- `LogForesight.Core/Service/HostDayPostProcessor.cs`（只限 `AttachCase`）
- `LogForesight.Web/Services/Mail/MailNotificationService.cs`（只限 `NotifyAfterRunAsync` 內的 `ClosePlans` 與 1-2、1-3 指定處）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 背景（主代理已讀碼確認）

`HostDayWorkflowStore.Update` 每次：建立具名系統互斥鎖、交易、2～4 次查詢、一次寫入，**不檢查內容是否改變**（`UpdatedAt` 每次都改）。恢復路徑 `TryUpdateForRecovery` 已有「序列化後與原內容相同就不寫」的寫法（比較在設定 `UpdatedAt` 之前，並傳 `skipUnchangedContent: true`）。

## 契約

### 1-1 郵件收尾：已是目標狀態就不寫
`MailNotificationService.NotifyAfterRunAsync` 的 `ClosePlans` 對回望期內每個主機日做：`CaptureDeliveryVersion`（讀）→ 不需要的通道各呼叫一次 `ReplaceMailPlan(…, 空計畫, version)` → `CloseMailPlan(…)`。最多 1 讀 3 寫。
- 在 `HostDayWorkflowService` 新增一個方法，在**一次**更新內依序套用「不需要的通道換成空計畫」與「結案」的同一段邏輯（把 `ReplaceMailPlan`、`CloseMailPlan` 的 lambda 主體抽成私有靜態方法，兩個既有公開方法改呼叫它們，行為不變；新方法依序呼叫）。版本不符（`MatchesVersion`）時不改任何東西。
- 這個新方法使用 `HostDayWorkflowStore` 新增的「內容不變就不寫」更新：比照 `TryUpdateForRecovery` 的比較方式（比較時 `UpdatedAt` 尚未改；不同才設 `UpdatedAt` 並寫入；傳 `skipUnchangedContent: true`），隔離層級維持 `Update` 的預設，同樣取具名互斥鎖、同樣的大小檢查。**既有的 `Update` 不改**——取數、AI、恢復等其他呼叫端行為完全不動。
- `ClosePlans` 迴圈改呼叫新方法；迴圈裡原本先做的 `CaptureDeliveryVersion` 讀取移除（新方法內的 `MatchesVersion` 與它判斷同一件事：`Parent == Succeeded`、非恢復等待、`ParentRecordId` 與 `DecisionVersion` 相符）。
- 寄送時呼叫的 `ReplaceMailPlan`（`PlanWorkflowMail`）不改。

### 1-2 巢狀比對改集合查找
`ClosePlans` 內 `summaryRecords.Any(record => RecordKey(record) == key)` 與 `urgentRecords.Any(...)`，以及 `SendRunSummaryAsync`／`SendUrgentNotificationsAsync` 中同型的「對清單逐筆 `RecordKey` 比對」（`grep -n 'Any(.*RecordKey' MailNotificationService.cs` 找出全部），改成迴圈外先建 `HashSet<string>`／字典再查。只改「比對是否存在」的寫法，結果相同。

### 1-3 郵件內容意圖在一次通知內只算一次（有前提）
`WorkflowMailIntent(kind, record, version, mailContextFingerprint)` 在一次 `NotifyAfterRunAsync` 內對同一筆紀錄被算多次（序列化整筆紀錄＋雜湊）。
- **前提**：先確認 `NotifyAfterRunAsync` 執行期間沒有任何路徑就地修改已取得的 `DailyAnalysisRecord` 物件（`grep` 對 `record.` 的指派、`TopIssues.Add/Remove/Clear`）；`RemovePrtgLanesTransiently` 等若是產生新物件就不算修改。前提不成立就**不做 1-3**，在回報說明。
- 成立時：以一個只存在於該次通知呼叫範圍內的字典記憶（鍵＝`kind`＋紀錄物件參考（`ReferenceEqualityComparer`）＋`version`＋`mailContextFingerprint`）。不得放在實例欄位、static 或紀錄物件上。

### 1-4 沒有問題的主機日：案件意圖與結果合成一次更新
`HostDayPostProcessor.AttachCase` 對 `expectedCaseKeys` 為空的主機日仍做 `RecordCaseIntents`（讀改寫）與 `RecordCaseResults`（讀改寫）兩次，兩次的輸入都是空集合。
- 在 `HostDayWorkflowService` 新增方法，在一次更新內依序套用 `RecordCaseIntents` 與 `RecordCaseResults` 的同一段邏輯（同樣抽成私有靜態方法，兩個既有方法改呼叫它們）。
- `AttachCase`：`hasWorkflow && expectedCaseKeys.Length == 0` 時改呼叫新方法一次，然後照常呼叫 `AttachNewDay` 與 `DispatchDay`（兩者對空清單會立即返回；保持呼叫是為了不改變流程）。其他情況完全不動。
- 例外處理與回傳值（`workflowTrackingFailed`）語意不變：新方法失敗時等同原本 `RecordCaseIntents` 失敗。
- 這裡用既有的 `Update`（新主機日本來就要寫），不用 1-1 的「不變就不寫」。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

計數方式：工作流每個主機日是一列 blob，鍵 `workflow_{hostId}_{yyyyMMdd}`；每次寫入會推進該 blob 的版本，可用 `backend.Blob(key).ReadVersion()` 前後相減得到寫入次數。若此方式不成立，改用其他與機器快慢無關的計數並在回報說明。

| # | 驗收 |
|---|---|
| A1 | **先在未修改的程式上**寫好對帳測試並確認通過，再開始改：同一份資料連跑兩趟 `NotifyAfterRunAsync`，記錄每個主機日最後的工作流狀態（序列化後去掉 `UpdatedAt`）與寄出的信。情境：摘要與即時兩通道、有收件人與無合格收件人、郵件停用。改動後逐項相同 |
| A2 | 第二趟（資料沒變）對已結案主機日的工作流寫入次數為 0；改動前的同一測試量到的次數 > 0（把兩個數字寫進回報） |
| A3 | 兩趟之間一個主機日的父紀錄版本變了（`RestoreFromRecord` 以不同指紋）、另一個主機日的應走通道變了（例如風險等級跨過摘要門檻）：只有這兩個被寫入，最後狀態與改動前的做法相同 |
| A4 | 版本不符的主機日：新方法不寫入 |
| A5 | 1-3（若有做）：一次通知內同一筆紀錄的意圖只算一次——可用計數包裝或在 `HostDayWorkflowFingerprint` 加測試用計數；不得在正式碼留下可變的 static 計數器（測試用的請以 internal 注入點或改測行為） |
| A6 | 空主機日：本機、NetIQ、PRTG 三條路徑經 `AttachCase` 之後的工作流狀態與改動前逐欄相同（`UpdatedAt` 除外）；工作流寫入次數由 2 降為 1 |
| A7 | 有問題的主機日：工作流狀態與寫入次數和改動前完全相同；某件問題派工回寫失敗時 `AttachCase` 仍回傳 false |
| A8 | `RecordCaseIntents`、`RecordCaseResults`、`ReplaceMailPlan`、`CloseMailPlan` 的既有行為不變（既有測試全綠） |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~MailNotificationService|FullyQualifiedName~WorkOrderOutboxTests|FullyQualifiedName~HostDayWorkflow|FullyQualifiedName~HostDayPostProcessor|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~WorkflowParentFingerprintRoundTripTests|FullyQualifiedName~LongLivedCollectionGuardTests"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、A2 改動前後的寫入次數、1-3 有沒有做與理由、驗收指令最後一行結果、未解項。
