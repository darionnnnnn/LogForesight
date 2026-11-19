# 第 78 輪批次 4 實作規格：PRTG 每日評估與快照的重複載入

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（PRTG 各條、「不要假設外部系統回傳的欄位…」、快取與授權維度），PRTG 現況規格在 `docs/PRTG-SPEC.md`。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。finding、寫入內容、取消時機都相同，只減少重複讀取。
要量「改動前」的結果時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Core/Service/PrtgDailyPipeline.cs`
- `LogForesight.Core/Persistence/Sql/EfPrtgStore.cs`（只限新增查詢方法；既有方法不改）
- `LogForesight.Core/Service/CalibrationService.cs`（只限逐日讀狀態變更的迴圈）
- `LogForesight.Core/Service/PrtgValueAssessment.cs`、`PrtgValueReadiness.cs`（只限接受呼叫端傳入的感測器鏡像）
- `LogForesight.Web/Services/PrtgSnapshotHostedService.cs`
- `LogForesight.Web/Services/PrtgSnapshotDiagnosticsStore.cs`（若需要新增「一次記錄多次嘗試」的方法）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 4-1 狀態變更：每一天的資料只讀一次
`PrtgDailyPipeline` 第一段迴圈（由近到遠走 `days`）每個回望日呼叫 `prtgStore.GetStateChanges(day.AddDays(-1), day.AddDays(1))`，讀兩天份、整列（含訊息欄），讀回後才依監看政策的感測器篩選。相鄰兩個回望日重疊一天。
- 改成**滑動窗口**：每個日曆日的狀態變更只從資料庫讀一次；交給 `PrtgRuleEvaluator.Evaluate` 的仍是「前一天＋當天」兩天份，內容與排序（依感測器、再依時間）與現行完全相同。記憶體同時最多保留三天份（處理中的兩天＋下一天預讀可省略；以「不超過現行兩天份的 1.5 倍」為上界），處理完的日子要釋放。
- 監看政策感測器清單非空時，篩選下推到查詢條件（集合參數）；清單為空＝不限，與現行相同。
- **欄位**：先確認 `PrtgRuleEvaluator.Evaluate` 與其內部、以及它產生的 finding 實際讀了 `PrtgStateChangeRow` 的哪些欄位（`grep` 檢查每一個屬性存取）。只投影確定被用到的欄位；任何不確定的欄位一律保留。新查詢回傳的物件不得被當成完整列交給其他用途——在方法註解寫明「只含規則評估所需欄位，不可作為其他用途的輸入」。
- 在 `EfPrtgStore` 新增方法，既有 `GetStateChanges` 不改（其他呼叫端照用）。
- `CalibrationService` 的逐日迴圈同型處理（它也是兩天窗口、讀回後篩 `selected`）。

### 4-2 感測器鏡像一趟讀一次
每日路徑開頭已載入 `allSensors = prtgStore.GetSensorStatuses()`；之後就緒度（`PrtgValueReadiness.Compute`）、四條趨勢的候選（`PrtgValueAssessment.Candidates`）、持平證據又各自重讀整份鏡像。
- 這些呼叫改成使用開頭那一份（`PrtgValueAssessment` 已有接受外部來源的靜態多載，沿用；沒有的地方新增選用參數，不傳時照舊自己讀）。
- `deviceNames` 由 `GetAllDevices()`（整列含標籤、群組路徑）改成只取編號與名稱的新查詢；同編號多列時取第一筆的規則不變。
- 同一趟建立兩次的資源守門來源（`PrtgMirrorGuardSource`）改成共用一個實例；它讀的欄位不變。
- 先確認趨勢評估期間鏡像不會被這一趟自己寫入（若同一趟內會改寫感測器鏡像，則寫入之後的讀取不得沿用舊的那份——那一處維持重讀並在回報說明）。

### 4-3 快照診斷：一輪寫一次
`PrtgSnapshotHostedService` 每 100 顆感測器一批，每批呼叫兩次 `RecordDiagnostic`（嘗試、成功），每次對診斷 blob（最多 4 MB）整份讀改寫。
- 改成一輪內在記憶體累計嘗試數與成功數，輪末（含例外與取消時的 `finally`）一次寫入；寫入後該小時的數值與逐批寫入相同。
- 其他種類的診斷（略過、啟動、日誌停止等）不改。
- 已知差異（可接受，寫進註解）：輪中途查看診斷頁時，本輪的嘗試數要到輪末才出現；行程在輪中途崩潰時該輪的嘗試數不記入（診斷本來就是盡力而為）。

### 4-4 快照：同一週期內範圍只算一次
快照每個執行週期內 `ComputeBusinessScopeDevices` 被呼叫兩次（對帳前、領取前）。
- 同一週期內第二次呼叫時，若主機清單版本、主機對應版本、範圍版本都和第一次相同，沿用第一次的結果；任何一個不同就重算。版本讀取用既有的方法。
- 兩次對帳（`ReconcileRecentStateChanges`）是刻意分成兩個階段的狀態機，**不合併、不省略**；只共用範圍的計算結果。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 回望 1 天與 5 天的 finding（含順序）與改動前逐筆相同：有監看政策、政策為空、跨午夜持續失聯、沉默規則、同裝置多顆感測器各一例（先在未修改的程式記錄） |
| A2 | 回望 5 天時，狀態變更的讀取查詢次數為 6（每個日曆日一次；改動前為 5 次、每次兩天）且每個日曆日只出現在一次查詢的範圍內；政策外感測器的列不被讀回 |
| A3 | 校準匯出的結果與改動前相同 |
| A4 | 就緒度與四條趨勢、持平證據的結果與改動前相同；感測器鏡像（`GetSensorStatuses` 或同義查詢）一趟讀取 1 次 |
| A5 | 快照一輪後診斷資料的內容與改動前逐筆寫入相同：10 批全成功、其中 2 批失敗、輪中途取消各一例；一輪對診斷 blob 的整份讀改寫為 1 次 |
| A6 | 快照一個週期內，版本都沒變時業務範圍計算 1 次；第一次之後主機清單被修改時，第二次重算 |
| A7 | 取數範圍必填、不可信範圍不清除的既有守門維持綠；既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgRuleEvaluatorTests|FullyQualifiedName~PrtgTrend|FullyQualifiedName~PrtgValue|FullyQualifiedName~PrtgSnapshot|FullyQualifiedName~CalibrationServiceTests|FullyQualifiedName~PrtgFetchServiceTests|FullyQualifiedName~PrtgHostMap|FullyQualifiedName~PrtgResourceGuard|FullyQualifiedName~QueryPlanGuardTests|FullyQualifiedName~LongLivedCollectionGuardTests"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、4-1 投影保留了哪些欄位與理由、A2 改動前後的數字、驗收指令最後一行結果、未解項。
