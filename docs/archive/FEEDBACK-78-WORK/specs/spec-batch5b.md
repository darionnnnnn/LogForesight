# 第 78 輪批次 5b 實作規格：依日期視角、報表統計、處理狀態推導與執行總表展開

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「K 的來源鍵就緒閘門」、「不要把執行總表的列當成執行日」、授權維度相關各條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。每個入口回傳的每個欄位、排序、錯誤訊息與改動前相同，只減少讀回記憶體的列數與查詢次數。
對帳測試一律**先在未修改的程式上寫好並確認通過**再開始改（可用 `git worktree add` 在 scratchpad 建 HEAD 乾淨副本；不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Core/Persistence/Sql/EfIssueAggregateQuery.cs`（只限 `AggregateByDate`、`CategoriesByDate`、`AggregateReportKpi`、`AggregateReportKpiPair`）
- `LogForesight.Web/Services/RecordListQueryService.cs`（只限 `FindHandling` 與其呼叫、依處理狀態篩選的上限檢查）
- `LogForesight.Web/Repositories/RecordRepository.cs`（只在 5-7 需要新增計數方法時）
- `LogForesight.Web/Controllers/Api/ScheduleController.cs`（只限 `run-preview` 讀紀錄的那一處）
- `LogForesight.Core/Persistence/Sql/EfAnalysisRecordStore.cs` 與其介面（只在 5-7 需要新增窄投影查詢時；既有方法不改）
- `LogForesight.Web/Services/RunMonitorService.cs`（只限 `GetDayDetailPage`／`BuildDayRows` 讀工作流的方式）
- `LogForesight.Core/Service/HostDayWorkflowState.cs`（只限在 `HostDayWorkflowStore`／`HostDayWorkflowService` 新增批次讀取）
- 測試替身只能為新增的介面方法補實作；新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 5-4 依日期視角在查詢端分組（有前提）
`AggregateByDate` 把每個主機日一列拉回記憶體，以（存活主機, 日期）去重（合併主機同一天兩列取風險等級較高者）後再分組；風險等級篩選時把允許的紀錄編號整批物化成集合再當參數傳回查詢；`CategoriesByDate` 把期間內全部問題列的（日期, 類別, 嚴重度）拉回才分組。
- 查詢範圍內**沒有被合併的主機**時（判斷方式與批次 5a 的 5-1 共用同一處；若 5a 尚未合併進來就在本檔新增，並在回報說明），每日各風險等級的計數與關聯旗標在查詢端分組；允許的紀錄編號改用子查詢（同檔 `AggregateByHost` 已是這種寫法）；類別清單在查詢端以（日期, 類別）分組取所需值。有合併主機時走現行路徑。
- 輸出的每個欄位、日期排序、類別順序與現行相同。

### 5-5 報表風險統計在查詢端計數
`AggregateReportKpi`（與 `Pair`）把每個主機日的（風險, 是否缺資料, 安全記錄可用）拉回再數。改成查詢端計數（`COUNT` 加條件）；受影響主機數現行已是查詢端相異後解析合併主機，維持。輸出相同。

### 5-7 處理狀態推導、上限檢查前移、執行預覽窄查詢
- **處理列索引**：`FindHandling` 對每筆紀錄在全部處理列中線性比對（現行主機名不分大小寫＋日期）。改成迴圈外建（主機名不分大小寫, 日期）→ 第一筆處理列的查找表。同一鍵多筆時取到的必須與 `FirstOrDefault` 相同（清單順序中的第一筆）。所有呼叫 `FindHandling` 的地方一起改。
- **上限檢查前移（有前提）**：依處理狀態篩選的慢路徑先 `QueryLightweight` 整批載入、再檢查是否超過 `HandlingFilterCandidateLimit`。若能以**與 `QueryLightweight` 完全相同的篩選條件**（含可見範圍、嚴重度與日風險顯示設定、靜音等 `RecordRepository` 套用的全部條件）做一次計數查詢，就改成先計數、超過就在載入之前拒絕，訊息不變。做不到完全相同的條件就**不做這一項**並在回報說明原因。
- **執行預覽窄查詢**：`ScheduleController` 的 `run-preview` 只用到主機、日期、AI 狀態、資料來源，卻呼叫連帶問題子列的 `QueryLightweight`。新增只投影所需欄位、不讀問題子表的查詢；篩選條件與現行相同（先確認 `QueryLightweight` 在這裡套用了哪些條件）。

### 5-8 執行總表展開：以整體工作流排序時批次讀取
`GetDayDetailPage` 以「整體工作流」排序時對每台主機逐一讀工作流（3000 台＝3000 次往返）。
- 在 `HostDayWorkflowStore` 新增依（主機清單, 日期）一次讀多列的方法（分段，每段鍵數有上限），反序列化與單筆 `Get` 相同（大小上限檢查相同；超限的列行為要與單筆 `Get` 一致——單筆會擲例外，批次讀取時請保持同樣的結果，或在回報說明你如何處理並以測試證明對畫面輸出相同）。
- `BuildDayRows` 在需要工作流時改用批次讀取；不需要時（其他排序）維持只讀當頁。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 依日期視角：無合併主機（走新路徑）、有合併主機且同一天兩個識別各有紀錄（走舊路徑、取較高風險）、風險等級篩選、類別／事件／來源／最低嚴重度篩選、嚴重度全站隱藏各一例，結果與改動前逐欄相同；無合併主機時讀回列數不隨主機數成長 |
| A2 | 報表統計：單期與兩期的各計數與改動前相同（含缺資料、安全記錄不可用、合併主機） |
| A3 | 處理列：同主機同日兩筆（取第一筆）、主機名大小寫不同、合併前寫在舊名稱下的紀錄各一例，清單結果與改動前相同；比對次數不隨「紀錄數 × 處理列數」成長 |
| A4 | 上限（若有做）：符合條件剛好等於上限時正常回應；多一筆時拒絕、訊息與改動前相同、且拒絕前沒有載入紀錄內容（上限以測試可調的方式注入，不建五萬筆資料）；可見範圍受限的使用者計數只算自己看得到的 |
| A5 | 執行預覽的回應與改動前相同；查詢不讀問題子表 |
| A6 | 展開某日：各排序方式（含整體工作流升冪、降冪）與第 1、2 頁、狀態篩選的回應與改動前相同；以整體工作流排序時工作流讀取往返數不隨主機數成長（40 台與 200 台） |
| A7 | SQL Server 的新查詢可翻譯（`ToQueryString`，未實連）；既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~IssueAggregateQueryTests|FullyQualifiedName~EfIssueAggregateQueryReadyParityTests|FullyQualifiedName~RecordListQueryServiceTests|FullyQualifiedName~ReportServiceTests|FullyQualifiedName~ReportAnalyticsTests|FullyQualifiedName~DashboardServiceTests|FullyQualifiedName~NextUnhandledShortcutTests|FullyQualifiedName~RunPreview|FullyQualifiedName~ScheduleController|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~HostDayWorkflow|FullyQualifiedName~IssueExclusionTests|FullyQualifiedName~QueryPlanGuardTests|FullyQualifiedName~Authorization|FullyQualifiedName~ControllerRouteSnapshot|FullyQualifiedName~RecordRepository"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 完成前自己對每一項做一次突變（把最佳化改回舊做法或把條件改壞，確認至少一個測試轉紅，再用檔案備份還原並 cmp 確認）。
- 回報（300 字內）：改了哪些檔案、每條驗收對應的測試名稱、哪些項目因前提不成立而未做、突變結果、驗收指令最後一行結果、未解項。
