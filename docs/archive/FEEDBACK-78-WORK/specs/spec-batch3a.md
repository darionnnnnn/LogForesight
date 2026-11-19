# 第 78 輪批次 3a 實作規格：夜間與 PRTG 背景路徑的整份資料重複讀取

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是快取與授權維度、`LongLivedCollectionGuardTests`、PRTG 相關各條），PRTG 現況規格在 `docs/PRTG-SPEC.md`。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。改動前後的結果、寫入內容、取消時機都相同，只減少重複的讀取與反序列化。
「一次操作」＝一趟背景工作或一個網頁請求。跨操作仍要讀到最新資料。
要量「改動前」的數字時，可用 `git worktree add` 在 scratchpad 建一個 HEAD 的乾淨副本跑（不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Core/Service/AnalysisOrchestrator.cs`（只限建立夜間 `IssueCaseCoordinator` 的地方）
- 新增一個 Core 類別檔（3-1 的包裝）
- `LogForesight.Core/Service/PrtgDailyPipeline.cs`（只限建立 `PrtgOperationScope` 的那一行）
- `LogForesight.Core/Service/PrtgOperationScope.cs`
- `LogForesight.Core/Service/PrtgScopeRevisionReader.cs`
- `LogForesight.Web/Services/PrtgSnapshotHostedService.cs`（只限建立 `PrtgScopeRevisionReader` 的地方）
- `LogForesight.Web/Services/PrtgHostMapRefresher.cs`
- `LogForesight.Tests/LongLivedCollectionGuardTests.cs`（登錄用）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 3-1 夜間掛接：問題負責人清單一趟讀一次
`IssueCaseCoordinator.AttachNewDay` 每個有問題的主機日呼叫一次 `_issueProfiles.GetAll()`；夜間的協調器在 `AnalysisOrchestrator` 每趟新建，傳入 `new IssueOwnerStore(backend.Blob("issue_owners"))`（沒有快取，每次整份讀取並反序列化）。
- 新增一個 `IIssueOwnerStore` 包裝：`GetAll()` 第一次呼叫時讀一次並保留，之後回傳這一份的**複本清單**（清單是新的、元素可共用——先確認 `AttachNewDay` 不修改元素）；`Get`、`Update`、`Delete` 直接委派給內層，且 `Update`／`Delete` 之後丟棄保留的那一份（下次 `GetAll` 重讀）。
- 只在 `AnalysisOrchestrator` 建立夜間協調器時使用這個包裝。網頁端（DI 的 singleton 協調器）**不改**。
- 行為差異（可接受，寫進註解）：同一趟內，網頁端中途修改的問題負責人自動套用結論，要到下一趟才被夜間掛接採用——與同一趟的派工脈絡（`DispatchContext.Build` 趟首讀一次）一致。

### 3-3 PRTG 取消檢查點：一次檢查不重複讀同一份資料
`PrtgOperationScope.Checkpoint()` 每次：`_read()` 讀設定、`ScopeChanged()` 讀範圍、`Publish(...)` 內**再讀一次**範圍。
- `Checkpoint` 把 `ScopeChanged` 讀到的範圍字串傳給 `Publish` 使用，不再讀第二次。`CompletedStage`／`UpdateVersion`／`ReadVersions` 等其他呼叫 `Publish` 的路徑維持自己讀。
- `PrtgDailyPipeline` 建立檢查點時傳的 `() => new SystemSettingsStore(backend.Blob("system_settings")).Get()` 每次新建一個沒有快取的讀取器（版本探測＋整份讀取）。改成在建立檢查點前建一個 `SystemSettingsStore` 實例，lambda 呼叫它的 `Get()`（有版本快取：每次仍探測版本，版本變了就重讀）。
- **檢查次數、判斷內容、取消發生在哪一次檢查都不變。** 特別是「同一時間戳的匯入」這種情況（現有註解說明不能以更新時間略過比較）：`SystemSettingsStore` 的快取以資料庫列版本判斷，不以時間戳，所以仍會讀到新內容——請以測試證明。

### 3-4 範圍版本讀取器：監看政策只在版本變動時重讀，快照服務共用一個讀取器
`PrtgScopeRevisionReader.ReadRawSemantic()` 每次都新建 `PrtgMonitoringPolicyStore` 讀整份政策、排序、序列化、雜湊；`PrtgSnapshotHostedService` 每個執行週期新建 3 個以上讀取器（每個新實例第一次都重算主機指紋）。
- `ReadCore` 已在同一道查詢讀到政策 blob 的版本。讓 `ReadRawSemantic` 以政策版本快取它算出的政策指紋：版本沒變就沿用，變了才重讀（比照同檔主機指紋的寫法：版本在讀內容前取得）。輸出字串格式**逐字不變**（它是郵件冪等鍵與快照佇列雜湊的持久鍵）。
- `PrtgSnapshotHostedService` 改成持有**一個**讀取器實例（欄位），所有原本 `new PrtgScopeRevisionReader(_backend, _hosts)` 的地方改用它。讀取器內的快取都以資料庫版本為鍵、與使用者無關，在類別註解寫明「內容是純鏡像結構，與使用者無關，可跨週期共用」。讀取器內部已用 `_gate` 鎖，確認多執行緒呼叫安全。
- 讀取器若因此成為 singleton 上的長駐物件，其可變欄位都是純量（字串、版本號），不需登錄；若新增集合則要登錄。

### 3-5 主機對應重算：使用同一個主機清單讀取器
`PrtgHostMapRefresher.TryRefreshToday()` 每次 `new HostStore(_backend.Blob("hosts"))`，每次都冷讀整份主機清單（約 4 MB／3000 台）。
- 改成在建構時建立一個 `HostStore` 實例（欄位）重複使用；`HostStore` 以版本快取，主機清單被修改後下一次呼叫會讀到新內容。
- 其他行為不變。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 夜間掛接的結果（案件、處理列、歷程、交辦單成員）與改動前相同；一趟 3 個有問題的主機日，問題負責人 blob 讀取 1 次（改動前 3 次）；包裝的 `Update` 之後 `GetAll` 讀到新內容；網頁端協調器在負責人剛被修改後讀到新值 |
| A2 | 檢查點：設定變更、範圍變更、停用 PRTG、**同一時間戳的匯入**四種情況，取消發生在與改動前相同的那一次檢查（先在未修改的程式寫好並確認通過）；每次檢查的資料庫往返數下降（改動前後的數字寫進回報） |
| A3 | `PrtgOperationScope.ReadVersions()` 回傳的內容與改動前相同（同一序列操作下） |
| A4 | 範圍讀取器：`ReadRawSemantic`、`Read`、`ReadStructural` 的輸出字串與改動前逐字相同；政策沒變時連續呼叫兩次，政策 blob 只讀 1 次；政策被修改（含只改縮圈以外的欄位、只改縮圈）後下一次呼叫輸出與改動前做法相同 |
| A5 | 快照服務一個執行週期內主機指紋只計算一次；週期之間主機清單被修改時，下一個週期使用新內容 |
| A6 | 主機對應重算的結果與改動前相同；兩次重算之間主機清單沒變時，主機 blob 只整份讀取 1 次；清單被修改後下一次重算讀到新內容 |
| A7 | 既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~IssueCaseCoordinator|FullyQualifiedName~WorkOrderCoordinatorTests|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~HostDayPostProcessor|FullyQualifiedName~PrtgOperationScopeTests|FullyQualifiedName~PrtgScopeRevision|FullyQualifiedName~PrtgSnapshot|FullyQualifiedName~PrtgHostMap|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgBackfill|FullyQualifiedName~PrtgProbe|FullyQualifiedName~PrtgStructureSync|FullyQualifiedName~MailNotificationService|FullyQualifiedName~LongLivedCollectionGuardTests"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、A1／A2／A4 改動前後的數字、驗收指令最後一行結果、未解項。
