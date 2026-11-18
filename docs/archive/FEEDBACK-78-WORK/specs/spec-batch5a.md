# 第 78 輪批次 5a 實作規格：問題聚合查詢不把逐日原始列拉回記憶體

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「K 的來源鍵就緒閘門」、「不要新增鍵沒涵蓋授權維度的快取」、「不要讓控制器宣告回傳父型別…」、`IIssueAggregateQuery` 反射守門相關各條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。每個入口回傳的每個欄位與改動前相同，只減少讀回記憶體的列數與查詢次數。
要量「改動前」的結果時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。對帳測試一律**先在未修改的程式上寫好並確認通過**再開始改。

白名單：
- `LogForesight.Core/Persistence/Sql/EfIssueAggregateQuery.cs`
- `LogForesight.Core/Persistence/IIssueAggregateQuery.cs`（只在 5-3 需要時新增方法；附介面預設實作或同步補所有實作與測試替身）
- `LogForesight.Web/Services/IssueRankingBuilder.cs`、`LogForesight.Web/Services/Mail/MailIssueDigest.cs`（只限 5-3 的前期查詢）
- 測試替身（`LogForesight.Tests/TestDoubles/` 內的 `FakeIssueAggregateQuery` 等）只能為新增的介面方法補實作
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 背景（主代理已讀碼確認）

- `LatestOccurrences`（經 `IssueRows`）在資料庫端只以 `event_id` 粗篩，把期間內**每一天的每一列**拉回記憶體，再比對來源、把主機解析成存活主機（合併主機）、依出現點分組取最近一次。要的是每個出現點一筆，讀回的卻是「出現點 × 出現天數」筆。呼叫端七處：`IssueRankingBuilder`、`IssueHandlingRollupQuery`、`RecordListQueryService`（兩處）、`WorkOrderBoardService`、`WorkOrderCommandService`、`MailIssueDigest`。
- 同檔的 `GroupActionableOccurrences` 已是在查詢端分組（`GROUP BY` 出現點鍵、`MAX(RecordDate)`、`MIN(SourceName)`）的寫法，可當樣板；它分來源鍵就緒／未就緒兩支。
- `Aggregate` 在主查詢之外另補四趟，其中 `SurvivingHostCounts` 與 `SurvivingHostDayCounts` 各自 `Distinct().ToList()` 拉回再在記憶體解析合併主機去重；後者拉回的 (來源鍵, 來源名, 事件, 主機, 日期) 已涵蓋前者需要的 (來源鍵, 來源名, 事件, 主機)。
- 儀表板排行（`IssueRankingBuilder`）與郵件摘要（`MailIssueDigest`）對**前一期**也呼叫完整的 `Aggregate`，但前一期只用到 `HostCount`（排行另用 `TotalCount`）與「是否存在」。

## 契約

### 5-1 每個出現點的最近一次在查詢端取得
- 走新路徑的條件（兩者都要成立，判斷只寫一處）：來源鍵已就緒（`_sourceKeyReady()`），而且**查詢範圍內沒有被合併的主機**。「查詢範圍內」＝本次會出現在結果裡的主機（受 `hostIds` 限制時是展開別名後的集合；不受限時是全部主機）中，沒有任何一台的存活主機不是它自己（`HostAliasIndex.Surviving(id).HostId != id`）。條件不成立就走現行路徑，完全不動。
- 新路徑：資料庫端以出現點鍵（主機、LogName、來源鍵、事件、EntryType、EventKey）分組取最近日期與 `MIN(SourceName)`；再只取「最近那一天」的列（可用關聯子查詢或 join，SQLite 與 SQL Server 都要能翻譯），在記憶體對這些少量的列套用**與現行完全相同**的挑選（`OrderByDescending(RecordDate).ThenByDescending(SeverityRank).First()`）取嚴重度與 `KnownIssue`。母體條件（靜音排除、期間、可見嚴重度、日風險等級、可見主機）必須與 `IssueRows` 完全相同——直接重用 `IssueRows` 回傳的查詢作為兩段查詢的來源。
- 現行先在記憶體以 `wanted` 比對來源；新路徑來源鍵已就緒，比對改在查詢端以 (來源鍵, 事件) 完成。**同一事件編號分屬兩個來源時只回傳被要求的那個**（來源名一個是另一個的前綴也一樣）。
- 輸出的每個欄位（`HostId`、`IssueKey`、`LastSeen`、`SeverityRank`、`KnownIssue`）與現行逐欄相同。

### 5-2 主機數與主機日數一次取得
`Aggregate` 內 `SurvivingHostCounts` 與 `SurvivingHostDayCounts` 改成拉一次 (來源鍵, 來源名, 事件, 主機, 日期) 的相異列，在記憶體同時算出兩者。合併主機的解析方式不變（這一項不需要「沒有合併主機」的條件）。`IssueHostDayCount` 等其他直接使用 `SurvivingHostDayCounts` 的地方不改。

### 5-3 前一期只算用到的欄位
新增一個只回傳每個問題（來源, 事件）的主機數與總次數的聚合（母體條件與 `Aggregate` 完全相同，含 `visibleSeverities`、`riskLevels` 參數）；`IssueRankingBuilder` 與 `MailIssueDigest` 的前一期改用它。新方法加在 `IIssueAggregateQuery` 時，要有靜音排除參數（反射守門 `IssueExclusionTests` 會檢查），並在介面提供預設實作（以 `Aggregate` 投影出結果）讓既有測試替身不必改；`EfIssueAggregateQuery` 覆寫成輕量版本。
- 前一期的鍵仍以 `IssueProfile.KeyOf(Source, EventId)` 正規化（大小寫不同的同名來源合併）。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A0 | 基線：儀表板排行、依問題視角第一頁、待派試跑、郵件摘要四個入口在兩組小級距（主機 10 與 40、期間 7 與 28 天）的讀回列數（`SqlCommandRecorder.ReadCalls`）與指令數，改動前後的數字寫進回報 |
| A1 | 新舊路徑結果逐欄相同（測試直接比對改動前的實作輸出）：無合併主機、有合併主機（合併前後兩個識別各有資料，應走舊路徑）、來源鍵未就緒（應走舊路徑）、可見範圍受限、只限部分主機而那些主機沒有合併（應走新路徑）各一組 |
| A2 | 無合併主機時，`LatestOccurrences` 的讀回列數不隨天數成長（7 天與 28 天比值接近 1；改動前接近 4） |
| A3 | 同一個事件編號分屬兩個來源（其中一個來源名是另一個的前綴）：只回傳被要求的那個 |
| A4 | 同主機同規則的兩顆 PRTG 感測器仍是兩個出現點；同一出現點最近一天有兩列（嚴重度不同）時取嚴重度較高者與其 `KnownIssue` |
| A5 | 靜音排除、可見嚴重度、日風險等級三個母體條件在新路徑都生效（各一例） |
| A6 | 5-2：`Aggregate` 的 `HostCount`、`DayCount` 與改動前逐欄相同（含合併主機）；整批拉回由 2 次降為 1 次 |
| A7 | 5-3：排行每一列的 `PreviousHostCount`、`PreviousTotalCount`、`IsNew` 與改動前相同；郵件摘要每列的前期主機數與分區相同；前一期查詢數下降 |
| A8 | 四個入口數字一致且與改動前相同：儀表板卡片、依問題視角筆數、報表排行、待派頁 |
| A9 | SQL Server 的新查詢可翻譯（`ToQueryString`，未實連）；`QueryPlanGuardTests` 維持綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~IssueAggregateQueryTests|FullyQualifiedName~EfIssueAggregateQueryReadyParityTests|FullyQualifiedName~IssueRankingBuilderTests|FullyQualifiedName~RecordListQueryServiceTests|FullyQualifiedName~WorkOrderBoardServiceTests|FullyQualifiedName~WorkOrderCommand|FullyQualifiedName~DashboardServiceTests|FullyQualifiedName~IssueExclusionTests|FullyQualifiedName~QueryPlanGuardTests|FullyQualifiedName~ReportServiceTests|FullyQualifiedName~MailIssueDigest|FullyQualifiedName~MailNotificationService|FullyQualifiedName~IssueHandlingRollup|FullyQualifiedName~Authorization|FullyQualifiedName~ControllerRouteSnapshot|FullyQualifiedName~SqlServerTranslation|FullyQualifiedName~ToQueryString"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾（例如「最近一天的列」在兩個資料庫無法用同一個 LINQ 表達），停下來回報，不要繞路。
- 完成前自己對 5-1、5-2、5-3 各做一次突變（把最佳化改回舊做法或把條件改壞，確認至少一個測試轉紅，再用檔案備份還原並 cmp 確認）。
- 回報（300 字內）：改了哪些檔案、每條驗收對應的測試名稱、A0 的數字、突變結果、驗收指令最後一行結果、未解項。
