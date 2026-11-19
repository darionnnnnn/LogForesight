# 第 78 輪批次 3b 實作規格：網頁請求內的重複整份讀取與快取鍵重組

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（特別是「不要新增鍵沒涵蓋授權維度的快取」與 `LongLivedCollectionGuardTests` 那一條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。快取鍵字串的內容逐字不變、回應內容不變，只減少重複的讀取與字串組合。

白名單：
- `LogForesight.Core/Persistence/ScheduleOptionsStore.cs`
- 新增一個 Web 類別檔（3-7 的共用鍵片段）
- `LogForesight.Web/Services/SummaryCache.cs`、`IssueRankingCache.cs`、`ActionableSnapshotCache.cs`、`NextUnhandledSequenceCache.cs`（只限 `KeyOf`）
- `LogForesight.Web/Services/DashboardService.cs`（只限傳給 `SummaryCache.KeyOf` 的主機識別集合）
- `LogForesight.Tests/LongLivedCollectionGuardTests.cs`（登錄用）
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 契約

### 3-6 排程選項開啟版本快取
`ScheduleOptionsStore` 建構時沒有開 `cached`，每次 `Get()` 都整份讀取 blob；排程頁輪詢（執行中每 3 秒）的多個端點與執行總表都會讀它。
- 改成 `base(blob, cached: true)`。`JsonBlobSingleton` 的快取只省「讀取內容」那一趟，命中時仍每次反序列化出新物件（見其類別註解），呼叫端做「讀→改→寫」不受影響。
- 先 `grep -rn "ScheduleOptionsStore" LogForesight.Core LogForesight.Web` 確認所有使用方式，沒有依賴「每次都讀到資料庫最新內容而不經版本比對」的地方（版本快取每次仍探測版本，寫入後下一次讀取就是新內容）。

### 3-7 可見主機識別的鍵片段：同一份集合只組一次
`SummaryCache`、`IssueRankingCache`、`ActionableSnapshotCache`、`NextUnhandledSequenceCache` 的 `KeyOf` 各自把可見主機識別排序後以逗號串接（3000 台約 20 KB 字串），儀表板一次請求做 3 次、報表 2 次，命中時也做。
- 新增一個共用的靜態方法產生這個片段（排序、逗號串接，**輸出與現行逐字相同**），以集合物件參考為鍵記憶結果（`System.Runtime.CompilerServices.ConditionalWeakTable<object, string>`：鍵物件被回收時條目自動消失，不會造成長駐洩漏）。四個 `KeyOf` 改呼叫它；`null` 的處理（`*`）維持各自現行寫法。
- 記憶以參考為鍵，因此**只有在集合不會被修改時才正確**：確認傳進來的集合（`IVisibilityService.GetVisibleHostIds()` 回傳的 `IReadOnlySet<long>` 等）建立後不會被修改；若有呼叫端傳入可能被修改的可變清單，該處不得受益於記憶——最簡單的作法是只對 `IReadOnlySet<long>` 型別記憶，其他型別每次重算。在註解寫明。
- `DashboardService` 呼叫 `SummaryCache.KeyOf` 時傳的是 `visibleHosts.Select(h => h.HostId).ToList()`（每次新清單）。改成傳同一請求的 `IVisibilityService.GetVisibleHostIds()`——**先確認兩者是同一組主機**（`visibleHosts` 是否就是 `GetVisibleHosts()`，而它由 `GetVisibleHostIds()` 篩出）；不是同一組就不改這一處並在回報說明。
- `ConditionalWeakTable` 是 static 的長駐結構：在 `LongLivedCollectionGuardTests` 登錄（清除時機＝鍵物件被回收）。

### 3-2（取消，不做）
郵件一次通知內的主機、使用者、問題負責人清單：主流程只讀 2～3 次，大部分讀取發生在寄送前後的重查，而重查必須讀最新資料（它是「寄送中資料或設定變了就不寄」的安全閘門）。效益小、與安全閘門衝突，不做。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 排程選項：另一個連線（或另一個 store 實例）寫入後，下一次 `Get()` 得到新值；連續兩次 `Get()` 回傳不同物件實例；沒有寫入時第二次 `Get()` 不整份讀取 blob（以計數證明） |
| A2 | 四個 `KeyOf` 的輸出對同一組輸入與改動前逐字相同（含 `null`、空集合、未排序輸入、3,000 個識別）——在測試中以改動前的寫法另算一份比對 |
| A3 | 同一個 `IReadOnlySet<long>` 物件呼叫兩次，排序串接只做一次（以計數或配置量證明）；兩個內容不同的集合得到不同片段；內容相同但物件不同的兩個集合得到相同片段 |
| A4 | 傳入可變清單時，清單被修改後再呼叫得到反映修改後內容的片段（證明可變型別不被記憶） |
| A5 | 儀表板回應與改動前相同；兩個可見範圍不同的使用者不共用快取條目（既有授權相關測試維持綠） |
| A6 | 既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~ScheduleOptions|FullyQualifiedName~JsonBlobSingletonCacheTests|FullyQualifiedName~SummaryCache|FullyQualifiedName~IssueRankingCache|FullyQualifiedName~ActionableSnapshot|FullyQualifiedName~NextUnhandled|FullyQualifiedName~DashboardServiceTests|FullyQualifiedName~ReportServiceTests|FullyQualifiedName~ScheduleController|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~LongLivedCollectionGuardTests|FullyQualifiedName~Authorization|FullyQualifiedName~Visibility"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 回報（200 字內）：改了哪些檔案、每條驗收對應的測試名稱、DashboardService 那一處有沒有改與理由、驗收指令最後一行結果、未解項。
