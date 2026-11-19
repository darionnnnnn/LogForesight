# 第 78 輪批次 2b 實作規格：PRTG 路徑的單點等價最佳化

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「不要在不可信範圍做清除」「不要讓 PRTG 取數對整台 PRTG 查」「不要用 GetLatestHostMapWithDate…」「不要替 PRTG 數值再加閘門」幾條），PRTG 現況規格在 `docs/PRTG-SPEC.md`。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。改動前後寫進資料庫的內容、回傳值、順序都相同，只減少往返次數與運算量。
要量「改動前」的數字時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Core/Persistence/Sql/PrtgScopePurgeStore.cs`（只限 `RunBatch`）
- `LogForesight.Core/Persistence/Sql/PrtgRiskProjectionStore.cs`
- `LogForesight.Web/Services/PrtgRiskReviewHostedService.cs`
- `LogForesight.Web/Controllers/Api/PrtgRiskReviewController.cs`
- `LogForesight.Core/Service/PrtgValueAssessment.cs`、`PrtgValueReadiness.cs`
- `LogForesight.Core/Service/PrtgDailyPipeline.cs`（只限呼叫 `AssessPage` 與 `PrtgRuleEvaluator.Evaluate` 的地方）
- `LogForesight.Web/Services/RuleAdminService.cs`（只限呼叫 `AssessPage` 的地方）
- `LogForesight.Core/Service/CalibrationService.cs`（只限呼叫 `PrtgRuleEvaluator.Evaluate` 的地方）
- `LogForesight.Core/Service/PrtgRuleEvaluator.cs`
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 2-5 範圍外清除：沒有候選感測器時不逐頁讀表
`PrtgScopePurgeStore.RunBatch` 每批以編號區間從兩張表（數值、狀態變更）各讀 250 列，再在記憶體比對是否屬於候選感測器；候選集合為空時仍一頁一頁走到水位線，每頁一個 Serializable 交易。
- 改成：候選集合為空時，該批直接把兩個游標設為各自的水位線（不讀表），其餘流程照舊——同一個交易、同樣的範圍與授權檢查、所有權檢查、`RefreshFinalCounts`、`RecordBaselineInTransaction`、狀態轉為 `complete`。
- 候選集合非空時行為**完全不變**。
- 不得放寬或移動任何保護：`CheckScope`、`CheckShrink`、父作業所有權、範圍圍欄、授權圍欄都在原處照常執行。
- 作業最終的狀態、刪除筆數（0）、剩餘筆數、`CountsReconciled`、基準線紀錄與走完全程相同；`Revision` 與 `UpdatedAtUtc` 會因批次數不同而不同，這兩欄是並行權杖與時間戳，可接受。

### 2-6 歷史弱佐證重評：內容有改才推進資料版本戳
`PrtgRiskProjectionStore.RunBatch` 回傳「處理過的候選數」，`PrtgRiskReviewHostedService` 與 `PrtgRiskReviewController` 只要它大於 0 就推進 `DataVersionStamp`——即使沒有任何一列內容被改寫（`PrtgHistoricalRiskReview.Apply` 回傳 false 時只更新版本欄）。
- `RunBatch` 另外回報「實際改寫內容的列數」（`Apply` 回傳 true 而改寫 `ContentJson` 的列）。保留既有回傳值語意給迴圈終止條件使用。
- 兩個呼叫端改成：實際改寫數大於 0 才推進版本戳。背景服務的迴圈終止條件仍是「處理過的候選數為 0」。
- 重評寫進資料庫的內容不變。

### 2-7 數值評估：有效對應日期一趟只算一次
`PrtgValueAssessment.AssessPage` 每頁都呼叫 `_store.GetOkHostMapDates(mapsFrom, mapsEnd)`，結果只取決於 `completedDay`、`windowDays` 與今天，與頁無關。
- 比照既有的 `newestHostMap` 參數做法：新增選用參數（呼叫端在分頁迴圈外算好傳入），並提供計算它的公開方法（以同樣的 `mapsFrom`／`mapsEnd` 算法）；不傳時本頁自己算（舊行為）。
- 所有分頁迴圈呼叫端都改成迴圈外算一次：`PrtgValueReadiness`、`PrtgDailyPipeline` 的趨勢評估與持平證據、`RuleAdminService`（先 `grep -rn "AssessPage(" LogForesight.Core LogForesight.Web` 列出全部）。
- 「今天」在同一趟內要一致：迴圈外算的那一次與舊做法每頁各算一次，若跨午夜執行會不同——以迴圈開始時為準（等同第一頁的結果），在註解寫明。

### 2-8 規則評估：每日重建的對照表與依裝置合併
`PrtgRuleEvaluator.Evaluate` 每次呼叫都從 `sensorStatuses` 重建感測器分類與類型對照表；每日路徑對每個回望日呼叫一次，輸入相同。`FoldByDevice` 對每個裝置掃描整份 finding 清單。
- 新增可選的預建對照表參數（由呼叫端在迴圈外建一次），不傳時照舊自己建；對照表的建法（`GroupBy(...).First()`）必須與現行相同。
- `FoldByDevice` 改成先依裝置分組再處理；輸出的 finding 內容與**順序**和現行相同。
- 呼叫端：`PrtgDailyPipeline`、`CalibrationService`（`grep -rn "PrtgRuleEvaluator.Evaluate(" LogForesight.Core LogForesight.Web` 列出全部），迴圈外建一次傳入。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 清除：候選為空時作業的最終狀態與各計數欄位和改動前相同（先在未修改的程式記錄），讀取兩張表的指令數為 0；候選非空時刪除結果、游標、最終狀態和改動前相同；範圍檢查不通過時仍不建立作業、不清除 |
| A2 | 重評：一批全部沒有內容變動 → 不推進版本戳；其中 1 筆有變動 → 推進一次；背景服務在候選處理完之前不提早結束；控制器同樣規則；資料列最終內容與改動前相同 |
| A3 | 數值評估：就緒度、四條趨勢、持平證據、規則管理頁試算的結果與改動前逐筆相同；多頁情境下有效對應日期查詢 1 次（以 `SqlCommandRecorder` 或計數證明）；不傳參數的呼叫行為不變 |
| A4 | 規則評估：回望 1 天與 5 天、同裝置多顆感測器、同代碼多條規則的 finding 與順序和改動前相同；校準匯出結果相同 |
| A5 | 既有測試全綠；取數範圍必填、不可信範圍不清除的守門維持綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~ScopePurge|FullyQualifiedName~PrtgHistoricalRiskReviewTests|FullyQualifiedName~PrtgRiskReview|FullyQualifiedName~PrtgValueAssessment|FullyQualifiedName~PrtgValueReadiness|FullyQualifiedName~PrtgTrend|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgRuleEvaluatorTests|FullyQualifiedName~CalibrationServiceTests|FullyQualifiedName~RuleAdmin|FullyQualifiedName~DataVersion"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、A1 與 A3 改動前後的數字、驗收指令最後一行結果、未解項。
