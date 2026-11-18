# 第 78 輪批次 0 實作規格：郵件通知的重複寄送與無止盡重跑

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（地圖與「不要做」）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。
只改下列白名單內的檔案；看到白名單外的未提交改動不得還原。

白名單：
- `LogForesight.Web/Services/Mail/MailNotificationService.cs`
- `LogForesight.Core/Service/HostDayWorkflowState.cs`（只限 `HostDayWorkflowFingerprint` 類別）
- `LogForesight.Core/Persistence/MailNotifyStateStore.cs`
- `LogForesight.Web/wwwroot/js/pages/prtg-admin.js`（只限狀態文字對照表）
- `LogForesight.Tests/MailNotificationServiceTestsDedupe.cs`（主代理已寫好的驗收測試，可新增案例，**不得改寫或刪除既有案例的斷言**）
- 新增測試檔可以；既有測試檔只能新增案例，不得改既有斷言。若既有測試因本規格而必須調整，停下來在回報中說明，不要自己改。
- `docs/WEB-SPEC.md`（郵件通知一節，約 2636 行附近）

## 背景（已由主代理以實際執行的測試確認）

郵件判斷「已寄過」用 `WorkflowRecordKey(...)`，其中 `WorkflowMailIntent(...)` 納入 `HostDayWorkflowFingerprint.ForRecord(record)`。
`ForRecord` 含 `AiAnalyzed`、`AiPending`、`DetailPruned` 三個與信件內容無關的狀態旗標。AI 寫回只翻轉這些旗標，鍵就變了：
- 設定裡直接填的信箱（沒有帳號）→ 即時通知與執行摘要都會**再寄一封**。
- 有帳號的收件人 → 不重寄，但即時通知新建一筆 `UrgentOutbox` 項目停在 `pending`，之後永遠不會變成終態。
`RetryPendingUrgentAsync` 每 60 秒被呼叫，只要有一筆非終態項目就整個重跑 `NotifyAfterRunAsync`，因此從此每分鐘重跑整套。
三處終態判定（`MailNotificationService.cs` 約 320、1181 行與 `MailNotifyStateStore.IsFullyTerminal`）認得 `cancelled-formal-revoked`，但沒有任何程式寫入它。

## 行為契約

### 0-1 郵件內容指紋排除三個狀態旗標
- 在 `HostDayWorkflowFingerprint` 新增郵件用的內容指紋：與 `ForRecord` 序列化同一份欄位，但 `AiAnalyzed`、`AiPending`、`DetailPruned` 三欄固定為 `false`。實作方式：把 `ForRecordWithIdentity` 參數化（三個旗標可覆寫），`ForRecord` 行為與輸出**逐位元不變**；新方法呼叫同一個私有函式，不複製欄位清單。
- `WorkflowMailIntent` 改用這個新指紋。這是郵件唯一使用 `ForRecord` 的地方；`ForRecord` 的其他呼叫端（AI、報告、紀錄寫回）**一律不動**。
- 風險等級、問題清單、告警等仍納入：AI 把風險等級上調是真的內容變更，必須重新通知。

### 0-2 升級相容（舊鍵辨識）
升級前存下的鍵（`SummarySentKeys`、`UrgentSentKeys`、`UrgentOutbox` 的鍵、工作流裡的郵件意圖）是舊算法：`ForRecord` 用當時的實際旗標值。
- 判斷「是否已寄」的兩處（執行摘要的 `qualifying` 篩選、即時通知的 `pending` 篩選）：新鍵不在已寄集合時，再以舊算法、三個旗標所有組合（8 種）算出的鍵逐一比對，任一把在集合裡就視為已寄。
- 舊鍵辨識寫成一個獨立的私有方法，註解寫明「升級滿 14 天回望期後可移除」（同 `LegacyParentFingerprint` 的做法）。
- 不得在升級後把回望期內已寄的主機日重寄一次。

### 0-3 待送項目的出口
- 新增終態 `superseded`（「已被取代」）。三處終態判定改成呼叫同一個公開靜態方法 `MailNotifyStateStore.IsTerminalUrgentStatus(string status)`，認得 `smtp-accepted`、`cancelled-formal-revoked`、`no-qualified-recipient`、`superseded`；驗收測試已在呼叫這個方法。`IsFullyTerminal`：`superseded` 的項目不論收件人狀態都算完全終態（可被保留清除移除）。
- `SendUrgentNotificationsAsync`：算出本趟 `candidates` 與各自的 `IntentKey` 之後、`if (pending.Count == 0) return;` **之前**，把 `UrgentOutbox` 裡「狀態不是終態、而且鍵不在本趟 `candidates` 的鍵集合裡」的項目狀態改成 `superseded`（一次 `_state.Update`；沒有要改的就不寫）。
  - 只在本趟正常算到這一步時做；讀取紀錄失敗、例外、取消時不做（自然成立：在例外之後不會執行到）。
  - 之後若同一把鍵又被算出，現有邏輯會把它設回 `pending`（`if (intent.Status != "smtp-accepted") intent.Status = "pending";`），不需另外處理；請確認這條路徑對 `superseded` 成立。
- 即時通知停用：`NotifyAfterRunAsync` 在設定成功讀取、且 `!settings.MailEnabled || !settings.MailUrgentEnabled` 時，把全部非終態項目改成 `superseded`。重新啟用後，回望期內仍未寄的主機日照常重新建立並寄出。

### 0-4 重試退避
- `RetryPendingUrgentAsync` 加退避，狀態只放在記憶體（實例欄位，`MailNotificationService` 是 singleton）：下一次允許執行的時間、目前間隔、記下的設定 `Revision`。
- 一次重試觸發的整套執行若**沒有進度**，間隔加倍：1 → 2 → 4 → 8 → 16 → 30 分鐘封頂（上限暫定 30 分鐘）。「進度」＝執行前後比較，有任何項目變成終態，或有任何收件人狀態變成 `smtp-accepted`。
- 回到 1 分鐘的條件：有進度；目前 `SystemSettings.Revision` 與記下的不同；或取數結束觸發的 `NotifyAfterRunAsync`（public 入口，非由重試呼叫）成功跑完一次。
- 取數結束觸發的 `NotifyAfterRunAsync` 不受退避影響。
- 時間一律用注入的 `_clock`（`TimeProvider`），不得直接讀 `DateTime.Now`／`UtcNow`。
- 新增的實例欄位若是集合，要在 `LogForesight.Tests/LongLivedCollectionGuardTests.cs` 登錄（純量欄位不需要）。

### 0-5 維護頁狀態文字
`prtg-admin.js` 的 `operationsStatus` 標籤表補上 `superseded: '已被取代，不再寄送'` 與 `'cancelled-formal-revoked': '已取消'`。

### 文件
`docs/WEB-SPEC.md` 郵件一節補三件事（只寫現況，不寫「原本／後來／第 N 輪」）：去重鍵的內容指紋不含 AI 與詳情清除的狀態旗標；即時通知待送項目在不再對應現行紀錄或即時通知停用時標為「已被取代」；重試沒有進度時的退避規則。

## 驗收（全部要由你寫成測試並跑綠，主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | `MailNotificationServiceTestsDedupe` 既有 7 個案例全綠（目前 5 紅） |
| A2 | `ForRecord` 的輸出對同一筆紀錄與改動前逐位元相同（新增測試：固定一筆紀錄，比對改動前算出的雜湊字串常數——請在改動前先跑一次取得該值寫進測試） |
| A3 | 升級相容：已寄集合與 `UrgentOutbox` 預先放入舊算法、旗標為 (AiPending=true, AiAnalyzed=false) 的鍵，紀錄目前旗標為 (AiPending=false, AiAnalyzed=true)，一次通知後不重寄、不新增非終態項目；摘要與即時兩個通道各一例 |
| A4 | 待送項目已不對應現行紀錄：紀錄的 `RecordId` 改變、紀錄滑出回望期（日期改成 20 天前）、紀錄已刪除，三例各一次通知後該項目變成 `superseded`，且之後 `RetryPendingUrgentAsync` 不再觸發寄送 |
| A5 | 寄信失敗而鍵仍對應現行紀錄的項目：維持非終態、會被重試、SMTP 恢復後寄出（既有 `NetiqUrgentStatisticsOnlyRecipientTracksFailureThenAcceptedRetry` 維持綠即可，另補一例有帳號收件人的） |
| A6 | 紀錄查詢擲例外的那一趟：沒有任何項目被改成 `superseded` |
| A7 | 即時通知停用：非終態項目變成 `superseded`；重新啟用後回望期內未寄的主機日寄出一封 |
| A8 | 退避（以可控的假時鐘 `TimeProvider`）：SMTP 持續失敗時，重試實際執行整套流程的時間點間隔為 1、2、4、8、16 分鐘，第 6 次起為 30 分鐘；中間 SMTP 恢復寄成功後回到 1 分鐘；修改設定（`Revision` 改變）回到 1 分鐘；退避期間呼叫 `NotifyAfterRunAsync` 照常執行 |
| A9 | `superseded` 項目在 `MailNotifyStateStore.PruneTerminalUrgentIntents` 超過保留期時會被移除 |
| A10 | 前端：`prtg-admin.js` 改動後 `JsModuleImport|UiTests|BehaviorTests` 維持綠 |
| A11 | 既有郵件測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~MailNotificationService|FullyQualifiedName~WorkOrderOutboxTests|FullyQualifiedName~HostDayWorkflow|FullyQualifiedName~RetentionPrunerTests|FullyQualifiedName~MailNotifyState|FullyQualifiedName~LongLivedCollectionGuardTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~DocNarrativeGuardTests|FullyQualifiedName~PrtgOperations"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或發現契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（200 字內）：改了哪些檔案、每條驗收對應的測試名稱、驗收指令的最後一行結果、未解項。
