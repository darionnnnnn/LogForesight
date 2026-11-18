# 第 78 輪批次 2a 實作規格：夜間持久層的單點等價最佳化

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「K 的來源鍵就緒閘門」「不要建出不屬於交辦單的進行中案件」兩條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。改動前後寫進資料庫的內容、回傳值、順序都相同，只減少往返次數與運算量。
要量「改動前」的數字時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Core/Persistence/Sql/EfAnalysisRecordStore.cs`（只限 `UpsertFirstSeen`）
- `LogForesight.Core/Persistence/IssueCaseCoordinator.cs`（只限 `AttachNewDay`）
- `LogForesight.Core/Persistence/HostStore.cs`
- `LogForesight.Core/Service/NetiqPipelineService.cs`（只限計畫後「每個日期找需要它的主機」那段）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 2-1 首見日一個主機日只查一次
`EfAnalysisRecordStore.UpsertFirstSeen` 對該主機日每個不同的 (來源, 事件編號) 各開一個 context、先 `Any` 再 UPDATE 或 INSERT，至少 2 次往返。
- 改成：一次查詢取出這批鍵中已存在的鍵；已存在者以條件式 UPDATE（只在 `first_seen > 本日` 時更新——可合併成一道或逐鍵皆可，但往返數不得隨問題數線性成長）；不存在者批次 INSERT。
- 保留語意：INSERT 撞唯一鍵（另一條執行緒同時第一次寫同一個新問題）時，**逐筆降級**：對撞鍵的那些鍵改走條件式 UPDATE；某一個鍵失敗只影響那個鍵（記警告、這次先不更新），不得讓同批其他鍵或已寫入的主機日失敗。現有的 `IsUniqueViolation` 判定沿用。
- `source_key` 的算法（`WorkOrderIssueKey.SourceKeyOf`）、`source_name` 截斷（`ColumnClamp`）不變。

### 2-2 掛接既有案件的歷程一次寫入
`IssueCaseCoordinator.AttachNewDay` 的迴圈內逐筆呼叫 `_handlingLog.AppendLog`（每次先讀尾端 100 行探號再 INSERT）。
- 改成迴圈內累積到清單，迴圈結束、`_issueHandlings.SaveMany(toSave)` **之前**一次呼叫 `_handlingLog.AppendLogs(...)`（同檔其他路徑與 `WorkOrderCoordinator` 已用此法）。沒有歷程時不呼叫。
- 歷程內容與相對順序與逐筆寫入相同；編號連續遞增。
- `SyncStatus` 等其他方法不改。

### 2-3 主機單筆查找不做線性掃描
`HostStore.Get(hostId)` 與 `FindByName(hostName)` 每次對整份清單 `FirstOrDefault` 線性掃描，夜間在逐主機迴圈與逐主機日呼叫。
- 在 `HostStore` 內，針對同一份快照（`ReadSnapshot()` 回傳的同一個物件參考）建一次以編號、以名稱（不分大小寫）為鍵的索引；快照換了（版本變動）就重建。索引只能綁在快照上，不得跨版本沿用。
- **重複時取第一筆**：同一編號或同名有多筆時，回傳與 `FirstOrDefault` 相同的那一筆（清單順序中的第一個）。
- 每次呼叫仍照舊做版本探測（`ReadSnapshot()` 不變），所以別的請求或行程寫入後下一次呼叫就看得到。
- 索引是 singleton 上的可變狀態：用不可變物件整個替換（與快照同生命週期），並在 `LogForesight.Tests/LongLivedCollectionGuardTests.cs` 登錄（上限＝主機數，清除時機＝快照替換）。

### 2-4 計畫階段「哪些主機要查某一天」
`NetiqPipelineService` 中 `plans.Where(p => p.MissingDates.Contains(date))`（`MissingDates` 是 List）對每個日期線性比對所有主機的清單。
- 迴圈外先建 `日期 → 該日要查的 plan 清單`，plan 在每個清單中的順序與原本 `Where` 的結果相同。
- 其他邏輯（高流量主機分批、批次大小）不動。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 首見日：全新問題、已存在且本日較早（應更新）、已存在且本日較晚（不更新）、同一主機日 5 個問題混合上述三種——寫入後 `lf_issue_first_seen` 的內容與改動前相同（先在未修改的程式上寫好並確認通過） |
| A2 | 首見日往返數：同一主機日 1 個問題與 10 個問題的指令數（`SqlCommandRecorder`，見 `LogForesight.Tests/TestDoubles/SqlCommandRecorder.cs` 與其既有用法）相同或接近，不隨問題數線性成長；改動前的數字寫進回報 |
| A3 | 撞鍵降級：兩條執行緒同時第一次寫同一個新問題（用**檔案型** SQLite，不要共用 `EfSqliteFixture` 的記憶體連線），兩邊都不擲例外，最後首見日是較早的那天；另一例：同批其中一個鍵撞鍵，其餘鍵照常寫入 |
| A4 | 歷程：一次掛接 3 件既有案件＋1 件自動套用結論，歷程內容、相對順序、編號連續性與改動前相同；尾端探號讀取由 4 次降為 1 次（可用計數替身或量 `ReadLastLines` 呼叫） |
| A5 | 主機查找：存在、不存在、名稱大小寫不同、同名兩台（取到清單中第一台）、同編號兩筆（取第一筆）；另一個 `HostStore` 實例（或直接寫 blob）修改清單後，下一次 `Get`／`FindByName` 回傳新內容 |
| A6 | 主機查找成本：1,000 台的清單連續查 1,000 次，索引只建一次（以計數或配置量證明，不寫毫秒門檻） |
| A7 | NetIQ 計畫：每個日期得到的主機清單與順序和改動前相同（有缺日、各主機缺不同天、回望 7 天各一例） |
| A8 | 既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~AnalysisRecordStoreContractTests|FullyQualifiedName~IssueAggregateQueryTests|FullyQualifiedName~IssueFirstSeen|FullyQualifiedName~FirstSeen|FullyQualifiedName~IssueCaseCoordinator|FullyQualifiedName~WorkOrderCoordinatorTests|FullyQualifiedName~HostStore|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~EmptyRecheckTests|FullyQualifiedName~LongLivedCollectionGuardTests|FullyQualifiedName~HostDayPostProcessor"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、A2 與 A4 改動前後的數字、驗收指令最後一行結果、未解項。
