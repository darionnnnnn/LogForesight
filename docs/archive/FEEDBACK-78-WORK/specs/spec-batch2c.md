# 第 78 輪批次 2c 實作規格：網頁端的單點等價最佳化與 PRTG 補齊估算修正

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「不要把執行總表的列當成執行日」「不要讓控制器宣告回傳父型別…」兩條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：2-9～2-11 **不改運作邏輯**（結果、順序相同，只減少運算與讀取）。2-13 是經使用者同意納入的**缺陷修正**，只改它指定的數字。
要量「改動前」的結果時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Web/Services/RunMonitorService.cs`
- `LogForesight.Web/Services/OccurrenceStatusResolver.cs`
- `LogForesight.Web/Services/Mail/MailNotificationService.cs`（只限交辦摘要 `LoadDigestOrder` 與其呼叫端）
- `LogForesight.Web/Controllers/Api/PrtgScopeEstimateController.cs`（只限 `EstimatePrtgValues`）
- `LogForesight.Web/Models/Dto/ScheduleDtos.cs`（只限 `PrtgValuesEstimateDto` 的註解）
- `docs/WEB-SPEC.md`（只限描述該估算的句子，若有）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 2-9 執行總表：依主機與日期的執行紀錄先建索引
`RunMonitorService.RunsForHostOnDate(runs, hostName, dateStr)` 每次對整份 `runs` 線性篩選並對每筆做 `StartedAt.ToString("yyyy-MM-dd")`；總表對「天數 × 主機數」各呼叫一次，展開某日對每台主機呼叫兩次。
- 改成：在 `GetDaySummaries`、`GetDayDetailPage`（與它呼叫的 `BuildDayRows`）等使用處，進入迴圈前把 `runs` 建成以（主機名不分大小寫, 日期字串）為鍵的查找表，清單內依 `StartedAt` 由新到舊排序（與現行 `OrderByDescending` 相同；同時間時的順序要與現行一致——現行是穩定排序，保持來源順序）。
- 日期鍵的算法必須與現行逐字相同（`StartedAt.ToString("yyyy-MM-dd")`，處理日，不要改成資料日）。
- 回傳的 DTO 與排序結果不變。

### 2-10 處理狀態推導：出現點的案件與處理列查找
`OccurrenceStatusResolver.Resolve` 對每個出現點在該主機的案件清單與處理列清單中線性尋找（`FirstOrDefault` 以 `IssueSignatureKeyComparer` 比對問題鍵、處理列再比對日期）。
- 改成每主機預建查找表：案件以問題鍵（`IssueSignatureKeyComparer`）為鍵；處理列以（問題鍵, 日期）為鍵。**同一鍵有多筆時取到的那一筆必須與 `FirstOrDefault` 相同**（清單順序中的第一筆）。
- `PrtgFalsePositiveWindow.Covering(...)` 仍以該主機的完整處理列清單呼叫，不改。
- 主機字典、設定讀取不在本項範圍（批次 3 處理）。

### 2-11 交辦摘要：主機清單在迴圈外讀一次
`LoadDigestOrder` 每張單呼叫一次 `_hosts.GetAll()` 與 `WorkOrderQueries.DisabledHostKeys(...)`。
- 呼叫端（交辦摘要的寄送流程）開頭算一次停用主機鍵，傳入 `LoadDigestOrder`；`DisabledHostKeys` 仍是唯一口徑，不另寫一份。
- 每封摘要的內容與現行相同。

### 2-13 PRTG 補齊時間估算計入請求配額（缺陷修正）
`PrtgScopeEstimateController.EstimatePrtgValues` 以「查詢數 × 1.5 秒 ÷ 併發 ÷ 60」估算分鐘數，沒有計入 historicdata「任一 60 秒最多 5 次」的配額（`PrtgRequestBudget`）。畫面文字是「至少約 N 分鐘」。
- 改成：分鐘數＝「依併發算出的分鐘數」與「配額下界換算的分鐘數」兩者較大者。配額下界**呼叫既有的** `PrtgFullBackfillPlan.QuotaLowerBoundSeconds(long requests)`，不另寫算式；秒換分鐘的進位方式與現行相同（`Math.Ceiling`）。
- DTO 欄位、其他欄位、畫面文字不變；`PrtgValuesEstimateDto.Minutes` 的註解更新為新的算法。
- `docs/WEB-SPEC.md` 若有描述這個估算的句子，同步改寫（只寫現況）。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 執行總表與展開某日（含各排序方式與第 1、2 頁）的回應與改動前逐欄相同；情境含同一台主機同一天多趟執行、主機名大小寫不同、跨午夜開始的執行（先在未修改的程式記錄預期值） |
| A2 | 處理狀態推導：有未結案案件、只有處理列、同主機同日同問題兩筆處理列（取第一筆）、同主機同問題兩件案件（取第一件）、沿用誤判期——結果與改動前相同 |
| A3 | 交辦摘要：3 張單的摘要內容與改動前相同；主機清單讀取 1 次（以計數替身證明） |
| A4 | 估算：查詢數 5 以下時分鐘數與改動前相同；查詢數 2,000、併發 2 時分鐘數等於 `ceil(QuotaLowerBoundSeconds(2000) / 60)`；查詢數 0 時為 0；正式碼中 `/ 5) * 60` 這類配額算式只出現在 `PrtgFullBackfillPlan` 一處（`git grep` 檢查，判準先以現有那一處確認會命中） |
| A5 | 既有測試全綠；`ControllerRouteSnapshotTests` 維持綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunDayTimingBuilderTests|FullyQualifiedName~RunExportServiceTests|FullyQualifiedName~OccurrenceStatus|FullyQualifiedName~IssueHandlingRollup|FullyQualifiedName~RecordListQueryServiceTests|FullyQualifiedName~IssueRankingBuilderTests|FullyQualifiedName~MailNotificationService|FullyQualifiedName~MailIssueDigest|FullyQualifiedName~WorkOrderDigest|FullyQualifiedName~PrtgFetchScopeEstimateEndpointTests|FullyQualifiedName~PrtgFullBackfillPlanTests|FullyQualifiedName~ControllerRouteSnapshot|FullyQualifiedName~DocNarrativeGuardTests"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、驗收指令最後一行結果、未解項。
