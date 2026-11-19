# D. 互動查詢路徑與全站快取盤點（唯讀）

範圍：使用者開頁面時，控制器到資料庫的整條鏈，以及所有跨請求快取。
標記：未標註者是實際讀到的程式碼；「推論」是由程式碼推得、未實測；「未追完」見最後一節。
路徑皆相對於 `/Users/wayne/project/LogForesight/`。簡稱：Web＝`LogForesight.Web`、Core＝`LogForesight.Core`、
Agg＝`Core/Persistence/Sql/EfIssueAggregateQuery.cs`。

---

## 0. 先講三個全域事實（後面每一節都會引用）

1. **一次 `_hosts.GetAll()`／`CapturePrtgSnapshot()` 都是一次 SQL 版本探測。**
   `JsonBlobCollection.ReadSnapshot()` 每次先 `_blob.ReadVersion()`（`Core/Persistence/JsonBlobCollection.cs:89`），版本沒變才重用快照；
   `GetAll()` 還多複製一份 3000 筆的清單（`:77`）。`HostStore.CapturePrtgSnapshot()` 同樣先探版本（`Core/Persistence/HostStore.cs:20-33`）。
2. **`JsonBlobSingleton.Get()` 每次都是「1 次 SQL 版本探測＋整份 JSON 反序列化」**（`Core/Persistence/JsonBlobSingleton.cs:50-70`）：
   開了 `cached` 的只有 `SystemSettingsStore`（`Core/Persistence/ISystemSettingsStore.cs:21`）；快取的是原始字串，命中時仍反序列化出新物件（註解明講是為了讀改寫安全）。
   其餘 JsonBlobSingleton（`ScheduleOptionsStore`、`UserPreferenceStore`、`NetiqOptionsStore`、`MailNotifyStateStore`…）是每次讀整份 blob。
3. **「資料版本戳」是記憶體裡的一個 long，不是資料表版本**（`Web/Services/SummaryCache.cs:14-22`）。
   `HostStore` 的 blob 版本（資料庫列版本）是另一個東西，只給 `EfIssueAggregateQuery.AliasIndex()`、各 JsonBlobCollection 快照用（見 §4.3）。

---

## 1. 各頁面呼叫鏈（縮排＝呼叫關係；〔SQL n〕＝該步發出的查詢數；★＝逐列拉回記憶體再處理）

### 1.1 每個 API 請求的共同前置
```
ActiveUserMiddleware（Web/Middleware/ActiveUserMiddleware.cs:28-84）
  users.Get(userId) → UserStore 快取集合，但先 ReadVersion〔SQL 1〕
  PermissionVersionStamp.Current → 記憶體
Program.cs:229-233：next() 之後 DataVersionStampPolicy.BumpIfNeeded（成功的非 GET／HEAD，且端點沒貼 [SkipDataVersionBump]）
```

### 1.2 儀表板 `GET /api/dashboard/summary`（頁面另外同時打 `/api/ai/today-focus`，它內部再呼叫一次 `_dashboard.GetSummary`：`Web/Controllers/Api/AiController.cs:122`）
```
DashboardService.GetSummary（Web/Services/DashboardService.cs:72-91）
  VisibilityService.GetVisibleHosts（Web/Services/VisibilityService.cs:253-260）：版本探測＋篩＋排序＋3000 筆 ToWebHost 複製（每次呼叫，無每請求快取）
  _exclusions.Current()（IssueExclusionProvider，鍵＝版本戳＋今天；未命中才讀 IssueOwnerStore 整份 blob）
  SummaryCache.KeyOf：3000 個 id 排序＋Join（約 20 KB 字串）
  SummaryCache.GetOrAdd → 命中則止；未命中 → SingleFlight → BuildSummary（:118-233）
    ① PendingDispatch（僅具 Assign）：WorkOrderBoardService.GetGaps → RunTrial（無快取，見 §1.8）
    ② RecordRepository.ResolveVisibility → 設定 Get() ×2（各 1 探測＋反序列化）
    ③ AggregateByHost〔SQL 3，Agg:1718-1835〕
    ④ DailyRecords.Count ×2〔SQL 2，DashboardService.cs:155-160〕
    ⑤ AggregateByCategory〔SQL 2，Agg:1521〕：GroupCategoryRiskItems＋LatestCategories
    ⑥ IssueRankingBuilder.Build（:180）→ IssueRankingCache.GetOrAdd，未命中 Compute（Web/Services/IssueRankingBuilder.cs:129-144）
         Aggregate(前期)〔SQL 5〕＋Aggregate(本期)〔SQL 5〕
         DailyHostCounts〔1〕★、FirstSeenFor〔1〕、HostIdsByIssue〔1〕★
         IssueHandlingRollupQuery.Build → LatestOccurrences〔1〕★★ → OccurrenceStatusResolver.Resolve〔≈4：hosts 探測、IssueHandling.GetMany、Case.GetOpenMany、settings〕
         （期間內有 PRTG 問題才有）LatestOccurrences〔1〕＋IssueHandlingStore.GetMany〔1〕（IssueRankingBuilder.cs:185-218）
    ⑦ CountCurrentlyMutedIssues〔SQL 0～1，Agg:215〕
    ⑧ BuildSilentHosts（記憶體）＋BuildSilentHostsPrtgDown（PRTG 啟用才有：GetLatestHostMapForHosts＋GetSensorStatesForDevices＋同步時間，約 3；列數隨「未回報主機數」成長，HostAdminService.cs:81-121）
    ⑨ IssueTodoQuery.ResolveActionable（ActionableSnapshotCache，未命中：ActionableOccurrences〔1〕＋Resolve〔≈4〕，IssueTodoQuery.cs:49-75）
    ⑩ AggregateReportKpi〔SQL 3，Agg:1077〕★ ← stats 逐主機日拉回
    ⑪ HandlingHistoryQueryService.GetTodoByRange（Web/Services/HandlingHistoryQueryService.cs:262）
         又一次 GetVisibleHostIds、_hosts.GetAll、`new HostAliasIndex`（:268，未快取）、settings Get
         AggregateDayTodo → GetDayHandlingRaw〔SQL 5～6，Agg:724-971〕★ ← 每個高／中風險主機日一列
         有墓碑（合併）主機才再 QueryLightweight ×2
    ⑫ PermissionChangeService.CountPending〔1；非 ViewAll 時另呼叫一次 GetVisibleHosts（PermissionChangeService.cs:706）〕
    ⑬ AuditLogStore.Count（僅具 ViewAudit）〔SQL 1〕★：24 小時內全部稽核行拉回、逐行 JSON 解析（Core/Persistence/AuditLogStore.cs:118-121）
```
合計（推論，依上面各步加總）：沒有 Assign 的 ViewAll 帳號，快取未命中約 **40～50 次**資料庫往返；有 Assign 再加約 **12～15 次**（RunTrial）；
快取全命中約 **1～2 次**（中介軟體的 users 探測＋GetVisibleHosts 的主機探測），但仍付「複製 3000 台＋排序＋組 20 KB 鍵」的 CPU／配置。

### 1.3 依問題視角 `GET /api/records/by-issue`（`RecordListQueryService.SearchByIssue`，Web/Services/RecordListQueryService.cs:398-535）——**沒有任何跨請求快取**
```
ResolveIssueScope → ResolveVisibleHostIds（visible.ToList()，3000）、設定 Get() ×2、_exclusions.Current()
Aggregate〔SQL 5〕（Agg:130-211）
CurrentlyMutedIssues〔SQL 2～3，Agg:238〕（有靜音才有）
_issueOwners.GetAll()（IssueOwnerStore 未開快取 → 整份 blob 讀＋反序列化）、_users.GetAll()
LatestOccurrences〔1〕★★ ＋ Resolve〔≈4〕（RecordListQueryService.cs:446-447；涵蓋「篩選後留下的全部問題」，不是當頁）
DailyHostCounts〔1〕★、FirstSeenFor〔1〕、規則 ResolveRules〔1〕
排序、Paginate 全在記憶體（:884-895）→ 換頁、換排序都整套重算
```
約 **15～18 次**往返，每次點頁碼／改排序都重來一遍。

### 1.4 依主機視角 `by-host`（RecordListQueryService.cs:304-353）／依日期視角 `by-date`（:355-396）
```
by-host：ResolveVisibleHostIds → AggregateByHost〔SQL 3〕→ _hosts.GetAll().ToDictionary(3000) → 全部組完再 Paginate（記憶體）
by-date：AggregateByDate〔SQL 2〕★★（Agg:1637-1715）→ Paginate（記憶體）
```
都沒有快取；預設期間：前端用 7 天（`wwwroot/js/pages/records.js:2234-2238`），但 API 沒帶 `from` 時 `ResolveDateRange` 取 `DateTime.MinValue`（RecordListQueryService.cs:705-706）＝整個保留期。

### 1.5 問題展開 `GET /api/records/issue-hosts`（RecordListQueryService.cs:825-882）
```
IssueHostDays〔1〕★（Agg:526，Distinct 後仍是「這個問題的主機日」）
_hosts.GetAll().ToDictionary、_users.GetAll().ToDictionary
當頁主機：LatestOccurrences〔1〕＋Resolve〔≈4〕
```
約 **7 次**；讀回量以「這一個問題」為界（註解與程式相符）。

### 1.6 「下一筆未處理」`GET /api/records/next-unhandled`（RecordListQueryService.cs:250-290；NextUnhandledSequenceCache）
```
GetVisibleHostIds → KeyOf（排序＋Join）→ 快取命中則止
未命中 → Search(Statuses=open/in_progress, RiskLevels=高中, enforceCandidateLimit:false) 慢路徑：
   QueryLightweight〔SQL 2〕★★★（整個保留期的高中風險主機日＋其全部問題列，EfAnalysisRecordStore.cs:1320-1378）
   _hosts.GetAll → HostLookup(3000)、LoadHandlings〔1〕、LoadIssueHandlings〔1〕、LoadOpenCases〔1〕
```

### 1.7 報表三支（`ReportsController`，期間上限 366 天：ReportsController.cs:28-37）
```
summary（ReportService.GetSummary，Web/Services/ReportService.cs:53-62 → BuildSummary :65-）
  GetVisibleHosts → SummaryCache 鍵 → 未命中：
  handlingScope=All（預設）：
    AggregateReportKpiPair〔SQL 3〕★（Agg:1145，兩期的主機日列拉回）
    AggregateReportTrend ×2〔SQL 2〕（SQL 端 GroupBy，量小）
    AggregateByHost〔3〕、GetTodoByRange〔≈7〕★
  handlingScope≠All：QueryLightweight（兩期，相鄰時合併成一次）★★★＋LoadScopeContext（3 張處理表）＋逐筆推導（ReportService.cs:143-175）
  IssueRankingBuilder.Build（與儀表板共用 IssueRankingCache）
  CountCurrentlyMutedIssues〔0～1〕、BuildCoverage → ScheduleOptionsStore.Get()（整份 blob）＋AggregateReportCoverage〔2〕（全歷史 MIN(record_date) GROUP BY host，Agg:1250）
category-trend（ReportCategoryTrendService.Get，Web/Services/ReportCategoryTrendService.cs:36-）
  → QueryLightweight(From,To)〔SQL 2〕★★★：**沒有風險等級過濾**，低風險日也整批拉回（:62）
  → 逐列逐問題去重、分桶（記憶體）
effectiveness（ReportEffectivenessService.Get，Web/Services/ReportEffectivenessService.cs:54-）
  → QueryLightweight(From, To+14 天)〔SQL 2〕★★★（:91，同樣不過濾風險等級）＋ IssueHandlingStore.GetMany〔1〕（:97，in 清單＝全部出現過的主機名）
```

### 1.8 交辦總覽（`WorkOrderQueryService`／`WorkOrderBoardService`）
```
清單 GET api/work-orders（QueryList，WorkOrderQueryService.cs:139-）：_users.GetAll、DisabledHostKeys(GetAll)、QueryOrders〔1〕、CountMembers〔1〕、ResolveRules〔1〕
負載看板 GET load-board（WorkOrderBoardService.cs:141）：LoadBoard〔1〕＋ _hosts／_users／_userGroups（後者未開快取）
徽章 GET handlers/me/badge、work-orders/unread-count：每位登入者每 60 秒各 1 次（wwwroot/js/core/badges.js:14,28-33）
   MyBadge → _exclusions.Current()＋DisabledHostKeys()（每次對 3000 台算兩次 HostNameKey.Of＋兩個 HashSet，WorkOrder.cs:332-341）＋HandlerSummary〔1〕
待派試跑 GET work-orders/gaps（GetGaps → RunTrial，WorkOrderBoardService.cs:191-256, 410-）——**沒有快取，每換一頁整段重跑**
  ResolveIssueScope → Aggregate(IssueExclusion.None)〔5〕 → GetOpenKeys〔1〕
  下界檢查通過才 LatestOccurrences〔1〕★★（>20000 出現點直接 TooLarge，:436）
  DispatchContext.Build（候選人快照、設定複本）、PreloadDismissed、suppressions.LoadAll、ResolveRules
  逐出現點 WorkOrderDispatcher.Decide（記憶體）
```

### 1.9 執行監控（`RunMonitorService`，Web/Services/RunMonitorService.cs）
```
總表 GetDaySummaries（:88-）
  BatchRunStore.GetRecentRuns(windowDays)（Core/Persistence/BatchRunStore.cs:420-437）：分頁串流整段 runs 日誌行，逐行反序列化，LatestPerRunStream 去重
  _records.ListHostDates〔1〕（rows＝頁內天數×主機數）、NetiqSourceAttempt.ReadLatest〔1〕
  AllHosts → _hosts.GetAll、依名稱 GroupBy 兩次
  for 每個資料日 × for 每台主機：RunsForHostOnDate(runs, hostName, dateStr)（:529-533，對整份 runs 線性掃＋每筆 StartedAt.ToString）
展開某日 GetDayDetailPage（:274-）→ BuildDayRows（:293-）：不管只要第幾頁，**全部主機都組一次**（status 篩選與排序需要），
  Where 與 Select 各呼叫一次 RunsForHostOnDate（:325,327）；sort=workflowComplete 時每台主機 `_workflow.Get`（最多 3000 次往返，:282,332）
```

### 1.10 排程頁輪詢（`wwwroot/js/pages/runs.js:1301-1322`）：執行中每 3 秒、閒置每 10 秒，每次同時打
```
GET admin/schedule/status（ScheduleController.cs:123-215）：
   ScheduleOptionsStore.Get()（整份 blob）、設定 Get()、閒置時 SchedulerHostedService.PrtgRunHealth（:694-714，PRTG 啟用時 GetRecentRuns(8 天) 串流＋規劃器）
GET admin/schedule/ai-status（:512-）：ScheduleOptionsStore.Get() 再一次、GetPendingAiCount（30 秒快取，未命中的查詢註解稱 7～15 秒，AiAnalysisRunState.cs:293-321）
GET admin/settings/prtg-structure-sync/status、GET admin/settings/prtg-backfill/status（runs.js:1859,2433）
```
另外**全站每位登入者**固定輪詢：`/api/run-activity`（30／60 秒，layout.js:681；每次 `CountDaySyncPending`〔SQL 1〕，RunActivityController.cs:60,84）、徽章（60 秒，上述）。

### 1.11 稽核頁 `GET /api/audit`（AuditLogStore.Query，Core/Persistence/AuditLogStore.cs:62-124）
```
無篩選：ReadPage〔SQL 2：COUNT(*)＋OFFSET/FETCH〕（EfJsonLogStore.cs:285-295）
有任一篩選：ReadLines(from,to)〔1〕★★★ 整段（缺 from 時補 90 天）全部稽核行拉回、逐行反序列化、記憶體過濾排序再分頁（:89-110）
使用者字典 _users.GetAll().ToDictionary
```

---

## 2. 問題 1：每個頁面一次請求的資料庫往返與「拉回記憶體再處理」

| 頁面 | 往返（約） | 拉回再處理的查詢（★） | 讀回列數隨什麼成長 |
|---|---|---|---|
| 儀表板（未命中） | 40～50（＋Assign 12～15） | `SurvivingHostCounts`／`SurvivingHostDayCounts`（Agg:2067-2118，`Distinct().ToList()` 後才 GroupBy）；`LatestOccurrences` 原始列（Agg:454-465）；`DailyHostCounts` 原始列（Agg:589-592）；`AggregateReportKpi.stats`（Agg:1099）；`GetDayHandlingRaw.hostDays`（Agg:754-770）；稽核 24h 行 | 主機數 × 天數（期間 7～90 天）× 每主機日的問題列數 |
| 依問題 | 15～18 | 同上（Aggregate×1、LatestOccurrences、DailyHostCounts） | 同上；每次翻頁／排序重算 |
| 依日期 | 2 | `AggregateByDate` 先把每個主機日一列（RecordId,HostId,日期,風險,關聯）拉回（Agg:1674-1681），再把期間內**全部**問題列的 (日期,類別,嚴重度) 拉回（`CategoriesByDate`，Agg:1880-1897，沒有 SQL GROUP BY） | 主機數 × 天數；問題列總數 |
| 依主機 | 3 | 否（SQL 端 GROUP BY；但 `latestRaw` 是相關子查詢，Agg:1765-1770，每列一次 MAX） | 主機數 |
| 問題展開 | 7 | `IssueHostDays`（Distinct 後）、當頁 `LatestOccurrences` | 該問題的主機日數 |
| 報表 summary（All） | 約 17 ＋（排行未命中則 +20） | `AggregateReportKpiPair.stats`（兩期各主機日一列，Agg:1174-1177）；`GetDayHandlingRaw` | 主機數 × (期間＋前期天數) |
| 報表 summary（非 All） | 約 10 ＋ 排行 | `QueryLightweight`×2 期＋三張處理表（ReportService.cs:143-175） | 高中風險主機日數 ＋其問題列 |
| 報表 category-trend | 2＋ | `QueryLightweight` 全期間**全風險等級**（ReportCategoryTrendService.cs:62） | 主機數 × 天數（最長 366 天）＋全部問題列 |
| 報表 effectiveness | 3＋ | `QueryLightweight`（期間＋14 天，全風險等級）＋`IssueHandlingStore.GetMany`（:91,97） | 同上＋主機名清單（IN 參數） |
| 交辦總覽清單／看板 | 5～6／3 | 否 | 交辦單頁大小 |
| 待派試跑 | 12～15 | `LatestOccurrences`（WorkOrderBoardService.cs:439，上限 2 萬出現點才擋）、`Aggregate` | 7 天內主機數 × 問題數 |
| 執行總表 | 4～5 ＋ 大量 CPU | `GetRecentRuns`（runs 日誌行）、`ListHostDates` | 天數 × runs；CPU 是天數 × 主機數 × runs（§7 C12） |
| 展開某日 | 4～5；workflow 排序時 ＋3000 | 同上 | 主機數 |
| 排程頁輪詢 | 每輪 ≈ 4 個請求，各 2～6 | `PrtgRunHealth.GetRecentRuns(8)` 每次閒置輪詢 | runs 日誌行 |
| 稽核頁 | 2；有篩選時 1＋整段 | 有篩選：整段稽核行（預設 90 天） | 稽核行數（天數 × 每日操作數） |

`QueryLightweight` 的細節（`Core/Persistence/Sql/EfAnalysisRecordStore.cs:1320-1378`、`LoadLightweightIssues` `:1928-1960`）：
先把符合條件的主機日整批 `.ToList()`，再以 `recordIds.Contains(t.RecordId)`（**一個含全部 id 的 JSON 參數**）把 lf_top_issues 子列整批拉回，
子列投影含 `SourceObservationsJson` 字串欄；3000 台 × 180 天 ≈ 54 萬個 id 在單一參數裡（推論：這是上限情境，實際取決於範圍）。
列表的狀態篩選慢路徑是**拉完才檢查上限**：`records.Count > HandlingFilterCandidateLimit`（RecordListQueryService.cs:147-151）。

---

## 3. 問題 2：同一請求內的重複工作

| 同一份資料 | 一次儀表板（未命中）內被讀幾次 | 位置 |
|---|---|---|
| 主機清單（每次＝1 探測＋清單複製） | 至少 6～8 次 | `OccurrenceStatusResolver.cs:34`（每次 Resolve 一次，儀表板內 2～3 次）；`HandlingHistoryQueryService.cs:267`；`WorkOrderBoardService.cs:143,441,457`（RunTrial）；`HostAdminService`；`IssueTodoQuery` 經 Resolve |
| 別名索引 | `EfIssueAggregateQuery.AliasIndex()` 版本快取，但**每個聚合方法各探測一次**（`DataVersion`＝1 次 SQL，Agg:80；方法內共 19 處呼叫）；另外三份獨立實作：`RecordRepository.AliasIndex()` 每請求建一份（Web/Repositories/RecordRepository.cs:273）、`HandlingHistoryQueryService.GetTodoByRange` 每次新建（:268）、`HostLookup`（每次新建，RecordListQueryService.cs:57,161、ReportCategoryTrend :67、ReportEffectiveness :94） | — |
| 系統設定 blob | `Get()` 每次 1 探測＋反序列化；儀表板內：`ResolveVisibility` 2 次、`DashboardService.cs:261`、Resolve（:51）×2～3、`GetTodoByRange`（:269）、`IssueTodoQuery`…≥ 8 次 | 見 §0.2 |
| 可見主機集合 | `GetVisibleHostIds()` 有每請求快取（VisibilityService.cs:106-111）；但 **`GetVisibleHosts()` 沒有**——每次呼叫複製 3000 筆（含 GroupIds／OwnerUserIds 兩個 List）並排序（:253-260） | Dashboard :77、Report :56、CategoryTrend :40、Effectiveness :57、PermissionChangeService :109,706、HostsController :40,83、IssueNoteController :37 |
| 可見主機 id 的 SummaryCache 鍵 | 每個快取各自 `OrderBy` ＋ `string.Join`（3000 個 id，約 20 KB）；儀表板 3 次（Summary／Ranking／Actionable），報表 2 次 | SummaryCache.cs:63-64；IssueRankingCache.cs:66-72；ActionableSnapshotCache.cs:61-72；NextUnhandledSequenceCache.cs:50-59 |
| 問題檔案（IssueOwnerStore，未開快取） | `IssueExclusionProvider` 每次版本戳推進後讀 1 次；`HostVisibilityResolver.GetIssueOwnedHostIds`（問題負責人才有）每次 1 次；依問題視角 `_issueOwners.GetAll()` 1 次 | IssueExclusionProvider.cs:44；HostVisibilityResolver.cs:90；RecordListQueryService.cs:~459 |
| 使用者清單 | `UserStore` 已開快取（探測＋共用）；各處 `GetAll().ToDictionary` 重建（儀表板內 0 次，交辦／依問題／稽核各 1 次） | — |
| 靜音排除條件 | 一次請求取一次，往下傳（設計如此），沒有重複 | DashboardService.cs:84 |
| 「同一批 TopIssues 列」 | 一次儀表板對 lf_top_issues 同一組期間篩選掃過至少：Aggregate 的 5 查詢×2 期、AggregateByCategory 2、DailyHostCounts、HostIdsByIssue、LatestOccurrences、ActionableOccurrences、GetDayHandlingRaw 的 3 | — |

### JsonBlobSingleton.Get 是否每次查版本？被哪些迴圈呼叫？
- 是：`Core/Persistence/JsonBlobSingleton.cs:55` 每次先 `ReadVersion()`；命中仍 `Deserialize(snapshot.Content)`（:57），所以「每呼叫＝1 次 SQL＋1 次完整 JSON 反序列化」。
- 沒有迴圈內呼叫的反例，但呼叫密度高：`ParseUnhandledSeverities()` 的 `_settings.Get()` 出現在 `OccurrenceStatusResolver.cs:51`（每次 Resolve）、`RecordListQueryService.cs:123,161`、`HandlingHistoryQueryService.cs:143,269,354`。
- `ScheduleOptionsStore.Get()`（未開快取＝直接讀整份 blob）：`ScheduleController.GetStatus`（:127）、`GetAiStatus`（:515）、`ReportService.BuildCoverage`（:236）、`RunMonitorService`（GetDaySummaries :124、BuildDayRows :299,303）。

---

## 4. 問題 3：快取清單

### 4.1 跨請求快取（表）

| 快取 | 鍵組成 | 含授權維度？ | 含資料版本戳？ | TTL | 條目上限／單筆上限 | 未命中經 SingleFlight？ | 失效 |
|---|---|---|---|---|---|---|---|
| `SummaryCache`（Web/Services/SummaryCache.cs） | 用途｜參數｜**3000 個 id 排序後逗號串接**（:63-64）；儀表板參數含 days、audit、assign、`exclusion.CacheToken` | 是（可見主機集合）；另以參數帶 ViewAudit／Assign 能力 | 條目存 Version，命中須 `entry.Version == 目前版本`（:95-98）；SingleFlight 鍵＝`key|version`（:74） | 30 秒（`IssueRankingCache.TtlSeconds`） | 256 條；**單筆無元素上限**（payload 是 DTO，不計數） | 是（:74） | 版本推進、TTL、LRU；每 TTL 掃一次（:112-117） |
| `IssueRankingCache`（IssueRankingCache.cs） | `v{version}|from|to|totalHosts|exclusionToken|hostIds` | 是（`visibleHostIds==null` 用 `*`，否則 3000 id） | 是（鍵內＋條目 Version，Set 時版本已前進就不寫入 :133） | 30 秒 | 256 條＋**全站元素合計 20 萬**；單筆 > 20 萬不入快取（:123-129） | 是（:86） | 寫入新鍵時整批清舊版本（:141）、讀取時每 TTL 掃（:156-167）、LRU |
| `ActionableSnapshotCache`（ActionableSnapshotCache.cs） | `v{version}|from|to|riskLevels|severities|exclusionToken|hostIds` | 是 | 是 | 30 秒 | 256 條＋20 萬元素；單筆超限不入 | 是（:86） | 同上 |
| `NextUnhandledSequenceCache`（NextUnhandledSequenceCache.cs） | `severities|riskLevels|hostIds`（鍵**不含版本戳**，版本存在條目內，:38,95-98） | 是（另含可見嚴重度／日風險等級） | 條目 Version 比對 | 30 秒 | 256 條；單筆即 ≤ 200 筆（`NextUnhandledScanSize`），無額外上限 | 是（:69，SingleFlight 鍵＝`key|version`） | 版本、TTL、LRU |
| `IssueOwnedHostIdsCache`（IssueOwnedHostIdsCache.cs） | `userId|version|retentionDays`（:49-50）——**不含大型字串** | 以 userId 為授權維度 | 是 | 30 秒（僅清理，註解稱新鮮度由版本負責，:20-22；但實作上 TTL 到了仍會重算，:75） | 256 條；單筆＝該使用者的 `HashSet<long>`（上限約 3000） | 是（:92） | **版本一推進整批清空**（:67-71） |
| `PrtgDeviceIndexCache`（PrtgDeviceIndexCache.cs:69-130） | 單一固定鍵 `all-devices` | 否（鏡像結構與使用者無關，註解有說明 :60-66） | **刻意不綁版本戳** | 30 秒 | 64 條；滿了整批清空（登錄於 LongLivedCollectionGuardTests） | 是（:115） | TTL |
| `IssueExclusionProvider`（IssueExclusionProvider.cs:21-62） | (版本戳, 今天) 單一快照 | 否（靜音是全站設定） | 是 | 無（換日也失效） | 1 份 | 否（鎖內重建） | 版本推進或換日 |
| `EfIssueAggregateQuery._aliasIndexSnapshot`（Agg:68-126） | 主機 blob 版本（資料庫列版本，**不是 DataVersionStamp**） | 否 | 主機 blob 版本 | 無 | 1 份 | 否（鎖＋雙重檢查） | 主機 blob 任何寫入 |
| `HostStore`／`HostGroupStore`／`GroupAccessStore`／`UserStore`／`NoiseMarkStore` | blob 版本探測（每次讀 1 次 SQL） | 否 | blob 版本 | 無 | 1 份快照（淺複製清單、共用元素） | 否 | blob 寫入 |
| `HostStore._prtgSnapshot`（HostStore.cs:17-33） | blob 版本 | 否 | 是 | 無 | 1 份（註解：3000 台約 4 MB） | 否 | blob 寫入 |
| `SystemSettingsStore` 快照 | blob 版本 | 否 | 是 | 無 | 1 份「字串」 | 否 | blob 寫入 |
| `AiAnalysisRunState.GetPendingAiCount` | 無鍵 | 否 | 否（以 generation 失效，:302,313） | 30 秒（:54） | 1 個值 | 否（註解承認冷快取可能兩個並發各查一次，:308） | AI 結束、整批重標 |
| `HealthService._attentionCache`（HealthService.cs:352-381） | 無鍵 | 否 | 否 | 30 秒 | 1 份 | 否 | TTL |
| `HealthController.Freshness` 借用 `SummaryCache`（HealthController.cs:103-105） | 鍵 `schedule-freshness||`（空集合） | 否（刻意；聯絡名單在快取外填） | 是 | 30 秒 | 佔 SummaryCache 的 1 條 | 是 | 同 SummaryCache |
| 靜態：`PrtgAddressResolver._cache`、`SentinelEventFetchService.Cache`、`CalibrationService` 評估快取、`Import.ImportService.Pending` 等 | 見 `LogForesight.Tests/LongLivedCollectionGuardTests.cs:23-82` 登錄表 | — | — | — | 皆有登錄上限或清除時機 | — | — |

### 4.2 鍵含大型字串的量級（推論）
- 3000 個 id × 平均約 6～7 位數字＋逗號 ≈ 20 KB 字元（UTF-16 約 40 KB）；`SummaryCache`、`IssueRankingCache`、`ActionableSnapshotCache`、`NextUnhandledSequenceCache` 各自保存一份鍵，最多各 256 條 → 最壞各約 10 MB 級（推論，只算鍵字串）。
- 鍵再加 `exclusion.CacheToken`：每個靜音區間約 40 字元（`Core/Persistence/IssueExclusion.cs:57-58`），靜音區間數無上限。
- `SummaryCache.GetOrAdd` 的 SingleFlight 鍵再串一次 `key + "|" + version`（SummaryCache.cs:74），暫時多一份鍵字串。
- **每次呼叫（含命中）都要重新排序＋Join**：`KeyOf` 沒有任何每請求記憶。

### 4.3 SingleFlight 本身（Web/Services/SingleFlight.cs:1-34）
- 同鍵並發只算一次；結果（含例外）共享；算完就移除。
- **同步阻塞**：`flight.Value` 是 `Lazy` 的 `ExecutionAndPublication`，後到的請求執行緒原地等（推論：多人同時未命中時，同一時間占用的是請求執行緒池執行緒，沒有 `await`）。
- 鍵不含授權判斷（類別註解明講）；各快取自己負責。

---

## 5. 問題 4：資料版本戳

**是什麼／存在哪**：`DataVersionStamp`（Web/Services/SummaryCache.cs:14-22），Singleton，`long _version`，只在記憶體；`Bump()` 為 `Interlocked.Increment`；行程重啟歸零（快取也是記憶體，一致）。
**使用者**：SummaryCache、IssueRankingCache、ActionableSnapshotCache、NextUnhandledSequenceCache、IssueOwnedHostIdsCache、IssueExclusionProvider、`CaseDaySyncSchedule`（版本變動喚醒，CaseDaySyncSchedule.cs:36-41）。

### 5.1 所有推進點（grep `Bump()` 結果）與分類

| 分類 | 推進點 | 頻率 |
|---|---|---|
| **每個請求一次** | `Program.cs:229-233` 中介軟體：任何成功的非 GET／HEAD 請求，端點沒貼 `[SkipDataVersionBump]`（Program.cs:298-306）。貼了的有：Settings(×3)、Rules(×2)、Auth(×2)、Admin(×2)、WorkOrders(×2)、WorkOrderBoard(×1)、WorkOrderView、Handling(1)、Ai(×2)、Imports、Help、Csp、PrtgMonitoring(×2)、PrtgBackfillAdmin、PrtgTransfer 全控制器。**未貼者（處理狀態儲存、指派、交辦建單等）每次成功都推進**。 | 每次使用者寫入 |
| 每個請求一次 | `Web/Controllers/Api/PrtgRiskReviewController.cs:41`（`RunBatch>0` 才推進） | 每次手動觸發 |
| **每趟一次** | `SchedulerHostedService.cs:580`（取數趟 `finally`，**執行結束後**才推進一次）；`AiAnalysisHostedService.cs:361`（AI 趟結束一次）；`PrtgStructureSyncService.cs:413`（結構同步成功一次） | 每夜 1～數次 |
| **每個背景批次／輪一次** | `CaseDaySyncHostedService.cs:80`：每 5 秒一拍，**有處理到資料才推進**（`ApplyPendingCaseDays` 迴圈跑完 → 一次）；有待處理案件時可以每 5 秒推進一次（推論：取決於夜間派工產生的 `DaySyncPending` 量，每批 200 件，BatchSize :25） | 派工後的一段時間，最快每 5 秒 |
| 每個背景批次一次 | `PrtgRiskReviewHostedService.cs:23`：每批 `RunBatch>0` 推進，批間隔 100 ms，直到 0（:24-27） | 重評期間，最快每 100 ms 一次（推論） |
| 每次一次性回填 | `TopIssueBackfillHostedService.cs:136`、`DailyRecordBackfillHostedService.cs:58`、`WorkOrderBackfillHostedService.cs:62`、`IssueFirstSeenSeedHostedService.cs:83`、`HandlingMigrationHostedService.cs:67`、`PermissionChangeMigrationHostedService.cs:104` | 升級後各 1 次（finally） |
| **每筆寫入／每主機日** | **沒有**：分析管線（`Core/Service/*`、`AnalysisOrchestrator`）寫入主機日時**不推進版本戳**（`Core` 目錄 grep 無 `DataVersion`）。 | — |

### 5.2 夜間取數／AI 寫入期間，版本戳多久變一次
- **取數趟與 AI 趟在寫入期間完全不推進**；只在結束時各推進 1 次（`SchedulerHostedService.cs:576-580` 註解承認「背景執行不走 HTTP 管線」才在結束補推進）。
- 因此期間內快取的新鮮度只靠 **30 秒 TTL** 保底（IssueRankingCache.cs:13-15 的註解同樣如此說明）。結果：夜間有人開儀表板，每 30 秒每個不同鍵重算一次（版本戳沒變，鍵相同，但 TTL 到期）。
- 同一時間若 `CaseDaySyncHostedService` 有積壓，會以每 5 秒一次的節奏推進版本戳 → 所有版本綁定快取（含 IssueOwnedHostIds 整批清空、IssueExclusionProvider 重建並重讀 IssueOwnerStore blob）同時失效（推論）。
- **白天使用者行為**：每一次處理狀態儲存／指派／建單（未貼 Skip 者）都是一次全域失效；N 位處理人在處理清單時，版本戳每次寫入前進，所有人的儀表板／報表／排行／可行動快照／下一筆未處理鍵在下一次請求全部未命中。

### 5.3 次要細節
- `SummaryCache.GetOrAdd` 把結果以**開始時的版本**寫入（SummaryCache.cs:74-84）；若計算期間版本已前進，條目照寫、之後不會命中，直到下次掃描或滿載才清；另外三個列表快取在 `Set` 有「版本已前進就不寫入」（IssueRankingCache.cs:133、ActionableSnapshotCache.cs:133）。
- 主機 blob 版本（資料庫列版本）每次寫入（含取數流程的回報時間批次寫回：`NetiqPipelineService.cs:977-1017`、`AnalysisOrchestrator.cs:419` 本機 Touch）都前進，使 `AliasIndex()`、`CapturePrtgSnapshot()`、`GetAll()` 在下一次呼叫重建（3000 台約 4 MB 反序列化）。

---

## 6. 問題 5：多人並發

- **不同可見範圍＝各算一份，完全不共用**：每個快取的鍵都含完整的可見主機 id 串，且每個底層查詢都以該集合下推（`expandedHostIds.Contains(...)`，EF 8 單一 JSON 參數），沒有「先算全站、再各自過濾」的路徑。
  所以 K 種不同授權範圍的使用者同時開儀表板＝K 份完整的 §1.2（各 40～50 次往返、各自拉回列）。
- 同一範圍（例如所有 ViewAll／admin 同一鍵）：SummaryCache 的 SingleFlight 合併，只算一份。
  儀表板→報表切換時，`IssueRankingCache`、`ActionableSnapshotCache` 在「期間與可見範圍與靜音條件相同」時共用（儀表板 days=30 對應報表預設 30 天，鍵相同）。
- **SingleFlight 不能合併的情況**：
  1. 鍵不同：不同可見範圍、不同期間、不同 `canAssign`／`canViewAudit`（儀表板參數內）、不同靜音 CacheToken。
  2. 時間差：版本一前進，舊鍵無法命中，後到請求重算；前一份結果對它沒用。
  3. **沒有快取就沒有合併**：依問題／依日期／依主機視角、待派試跑、處理成效／類型趨勢以外的所有查詢（`SearchByIssue` 等）每個請求各自完整計算，`Paginate` 在記憶體。
  4. 儀表板頁同時打 `summary` 與 `today-focus`，兩者在同一個 SummaryCache 鍵上合併（OK）；但 `RunTrial`（Assign 帳號）沒有快取，兩個請求若在 SummaryCache 之外…因為包在 `BuildSummary` 內，實際只在 SummaryCache 未命中時算 1 次（OK）。
  5. 報表三支（summary／category-trend／effectiveness）是三個不同的 SummaryCache 鍵，前端並行時各自拉自己的 `QueryLightweight`（category-trend 與 effectiveness 讀的是同一批主機日，卻分別載入兩次）。
- `IssueOwnedHostIdsCache` 是「以使用者為單位」的：問題負責人每次版本推進或 30 秒到期都要重跑一次整個保留期的 `HostIdsFor` 聚合（Agg:353-387，Distinct 後 `.ToList()`）。

---

## 7. 問題 6：記憶體

### 7.1 暫時配置隨「主機數 × 天數」成長（依嚴重度排序）
1. **`QueryLightweight` 家族**：category-trend／effectiveness 全風險等級、報表非 All 範圍、列表狀態篩選、下一筆未處理（整保留期）。逐主機日一個 `DailyAnalysisRecord`＋每個問題一個 `LogIssueSignature`（含 `SourceObservations` 反序列化）。3000 × 180 = 54 萬個主機日物件＋ 其問題列（上限情境，推論）。
2. **`LatestOccurrences`**（Agg:454-465）：原始列，不是 Distinct／GroupBy；投影含 `KnownIssue` 字串、`SourceName`、`LogName`、`EventKey`；`.ToList().Where(...).ToList()` 再複製一次；之後記憶體 GroupBy。**儀表板的處理概況彙總與依問題視角都對「期間內全部問題」呼叫它**（IssueHandlingRollupQuery.cs:50、RecordListQueryService.cs:446）。
3. **`SurvivingHostDayCounts`／`SurvivingHostCounts`**（Agg:2067-2118）：期間內所有 (來源, 事件, 主機[, 日期]) 的相異列 `.ToList()`；每列含兩個字串（SourceKey、SourceName）。每次 Aggregate 兩次，一次儀表板呼叫 Aggregate 兩次（本期＋前期）。
4. **`DailyHostCounts`**（Agg:589-592）：30 天基準窗內「頁上全部問題」的原始列，不 Distinct。
5. **`AggregateByDate`**（Agg:1674-1689, 1880-1897）：每主機日 1 列 ＋ 全部問題列的 (日期,類別,嚴重度) ＋ 把 `allowedRecordIds` 物化成 `HashSet<long>` 再當 JSON 參數傳回 SQL。
6. **`AggregateReportKpi`／`Pair`**（Agg:1099, 1174）：每主機日 3～5 欄。
7. **`GetDayHandlingRaw`**（Agg:754-870）：高中風險主機日一列 ＋ 該批主機的 IssueHandling 全部列 ＋ 持有未結案案件的主機**其整個期間**的 lf_top_issues 明細（Agg:848-872）；`recordIdsNeedingDetail.Contains(...)` 是大型 JSON 參數。
8. **`GetVisibleHosts()`**：每次 3000 筆深複製（含兩個 List）＋排序；一次報表三支＝3 份。
9. **`VisibilityService.GetVisibleHostIds`／`HashSet<long>`、`SummaryCache.KeyOf` 的 id 串**：每請求 3000 元素集合 ＋ 20 KB 字串，多次。
10. **稽核有篩選時整段拉回**（AuditLogStore.cs:89-110）：90 天稽核行全部字串＋反序列化物件。
11. **`RunMonitorService`**：`RunsForHostOnDate` 每次 `ToList()`＋`StartedAt.ToString`；3000 台 × 天數 × runs 的小物件與字串（§1.9）。

### 7.2 常駐集合（沒有或較弱上限者）
- 都有登錄：`LongLivedCollectionGuardTests.Registry`（`LogForesight.Tests/LongLivedCollectionGuardTests.cs:23-82`）。
- 弱點：
  - `SummaryCache`、`NextUnhandledSequenceCache`、`IssueOwnedHostIdsCache` 只限條目數 256，**不限元素數**（只有 IssueRanking／Actionable 有 20 萬元素上限）。三者元素量小，但鍵字串約 20～40 KB × 256。
  - `RevokedTokens._revoked` 刻意不設筆數上限（登錄表 :43，註解說明理由）。
  - **主機目錄在記憶體有多份副本**（推論，由程式推得）：`HostStore` 快照（`List<WebHost>`）、`_prtgSnapshot`（`PrtgHostSnapshot`，另一份 3000 筆）、`EfIssueAggregateQuery._aliasIndexSnapshot`（`HostAliasIndex` 三張字典）、`RecordRepository._aliasIndex`（每請求）、`HostLookup`（每請求／每呼叫）。主機 blob 每次寫入全部重建。
  - EF `Expression.Constant` 內嵌靜音鍵陣列（IssueExclusionSql.cs:97-121）：每一種不同的靜音設定產生不同的查詢文字／EF 編譯快取鍵（推論：EF 內部查詢快取成長隨靜音設定變化次數，未實測）。

---

## 8. 最佳化候選

格式：現況／成本隨什麼成長／可能的等價改法／影響到哪些其他頁面或呼叫端／風險。

### C1. `LatestOccurrences` 改 SQL 端分組（最大的單一配置）
- 現況：`Agg:438-495`，`.Select(...10 欄...).ToList().Where(wanted).ToList()` 後在記憶體依 (存活主機, LogName, SourceKey, EventId, EntryType, EventKey) 分組取最近。對照：`ActionableOccurrences`（Agg:640-706）已用 SQL `GROUP BY ... MAX(RecordDate)`（`GroupActionableOccurrences` Agg:1964 附近）。
- 成本：主機數 × 天數 × 每主機日的問題列數（期間 7～90 天）；每次儀表板排行未命中、每次依問題視角。
- 等價改法：SQL 端先 `GROUP BY (HostId, LogName, SourceKey, EventId, EntryType, EventKey)` 取 `MAX(RecordDate)`，再以 (組鍵＋最大日期) join 回一次取該日的 `MAX(SeverityRank)` 與對應 `KnownIssue`；別名合併（Surviving）仍在記憶體，列數降為「相異 (主機, 問題)」。
- 呼叫端（全部要一起驗）：`IssueRankingBuilder.cs:188`（PRTG 問題）、`IssueHandlingRollupQuery.cs:50`（所有問題）、`WorkOrderBoardService.cs:439`（RunTrial）、`RecordListQueryService.cs:446`（依問題）、`:851`（展開頁）、`WorkOrderCommandService.cs:614`（建單）、`Mail/MailIssueDigest.cs:105`（郵件摘要）。
- 風險：平手規則（`OrderByDescending(RecordDate).ThenByDescending(SeverityRank)`，Agg:476）與 `KnownIssue` 取「最近那列」的語意要逐位相同；合併主機同一天兩列的選擇；`IssueKey` 用 `g.Select(SourceName).Min()` 組出來（Agg:480-483）——改成 SQL 後 `MIN(SourceName)` 需對應 collation。需補等值測試（舊實作 vs 新實作逐欄比對）。

### C2. `Aggregate` 內 `SurvivingHostCounts` 由 `SurvivingHostDayCounts` 的列推得（省一次整批拉回）
- 現況：`Agg:183-184` 兩次獨立 `Distinct().ToList()`（`:2067`、`:2088`），後者的相異列（來源,事件,主機,日期）已包含前者要的（來源,事件,主機）。
- 成本：與 C1 同量級；儀表板每次兩期、依問題一次、待派一次、郵件摘要兩次。
- 等價改法：一次拉 (SourceKey, SourceName, EventId, HostId, RecordDate) 相異列，記憶體內同時算 HostCount 與 DayCount。
- 呼叫端：`Aggregate` 的全部呼叫端——`IssueRankingBuilder.cs:139,142`、`RecordListQueryService.cs:418,1025`（ClusterSignatures）、`WorkOrderBoardService.cs:427`、`Mail/MailIssueDigest.cs:83,88`、`IssueOwnerAdminService.cs:97,443`、`RuleAdminService.cs:736,743`。`IssueHostDayCount`（Agg:282，`WorkOrderCommandService.cs:113`）直接用 `SurvivingHostDayCounts`，不受影響。
- 風險：低；等值測試（HostCount／DayCount 逐欄）。

### C3. 前期 Aggregate 只取用到的欄位
- 現況：`IssueRankingBuilder.cs:139` 前期 `Aggregate` 完整 5 查詢，但後續只用 `prev.HostCount`、`prev.TotalCount`、`prev==null`（:229, 272-274）。`DayCount`、`IssueKeys`、`Category`、`ActiveDays` 前期都沒用。
- 成本：每次排行未命中省 3 個查詢（hostDays、signatures、latestCategories）與其拉回列數。
- 等價改法：`IIssueAggregateQuery` 新增輕量方法（只 GroupBy＋HostCounts）供前期使用；本期仍用完整 Aggregate。
- 影響：`IIssueAggregateQuery` 加方法 → `Authorization|ControllerRouteSnapshot|IssueExclusionTests` 反射守門要跑；測試替身（介面預設實作）要補；`MailIssueDigest.cs:83` 前期用法需另行確認（未讀其後續欄位使用）。
- 風險：低。

### C4. 版本戳粒度與重算成本（影響面最大，改法最不「等價」，列出取捨）
- 現況：`Program.cs:229-233` 全域單一戳；每次未貼 Skip 的寫入使六個快取同時失效；`IssueExclusionProvider` 每次重建要讀 `IssueOwnerStore` 整份 blob（IssueExclusionProvider.cs:44）。
- 成本：白天 N 位處理人頻繁寫入 → 儀表板／報表／排行每個鍵反覆整套重算（§1.2 的 40～50 次往返、§7.1 的拉回列）。
- 可能改法（由安全到不安全）：
  a. 不改失效頻率，只降「未命中成本」＝C1、C2、C3、C6、C7、C8（等價，推薦先做）。
  b. 分域戳：「分析資料戳」（取數／AI／回填）與「處理狀態戳」（處理狀態儲存、指派、交辦）分開，鍵依各快取實際依賴選用。**需先逐一確認每個快取內容是否依賴處理狀態**：IssueRankingCache 內含 `OpenHostCount`／`ResolvedHostCount`（rollup，IssueRankingBuilder.cs:179,276-277），ActionableSnapshotCache 的 `ResolvedOccurrence.Status` 也是處理狀態——所以兩者都必須吃處理狀態戳，分域對它們沒有好處；對 `IssueOwnedHostIdsCache`、`IssueExclusionProvider` 則可只吃「問題負責／靜音／授權」戳。
  c. 不建議：合併或延遲推進（TTL 以外的新鮮度承諾會變）。
- 影響：Dashboard、Report 三支、依問題／下一筆未處理、每位問題負責人的所有請求、靜音相關畫面。
- 風險：b 若漏分一個依賴就是「顯示過期資料」；與 `[SkipDataVersionBump]` 的「有疑慮就不要貼」原則相反。

### C5. 提供「可見範圍指紋」取代 3000 個 id 的字串鍵
- 現況：`SummaryCache.KeyOf`（:63-64）、`IssueRankingCache.KeyOf`（:66-72）、`ActionableSnapshotCache.KeyOf`（:61-72）、`NextUnhandledSequenceCache.KeyOf`（:50-59）各自排序＋Join；儀表板請求 3 次、報表 2 次，命中時也付。
- 成本：每次請求 O(n log n) 排序＋20 KB 字串配置，隨可見主機數成長；快取常駐鍵字串隨條目數成長。
- 等價改法：`VisibilityService` 每請求只算一次「排序後 id 的 128 位元以上雜湊」（或以 (主機快照版本, 使用者授權版本) 配合 id 清單去重表），四個快取鍵改用它；快取仍以完整比較處理碰撞（先比雜湊，再比 id 序列的長度／首尾），確保不會串味。
- 影響：四個快取＋`HealthController`（鍵為空集合）＋所有測試中直接呼叫 `KeyOf` 的案例（`SummaryCacheTests`、`IssueRankingCacheTests` 等，未盤點）。
- 風險：**授權邊界**——紅線是「少一維就是串味」；雜湊必須含全部 id、不可用 `GetHashCode`；建議 SHA-256 或 128 位元以上，並在測試補「不同集合不同鍵」。

### C6. `GetVisibleHosts()` 每請求記憶化，或改給唯讀投影
- 現況：`VisibilityService.cs:253-260` 每次複製 3000 台（`PrtgHostSnapshot.ToWebHost()` 複製 GroupIds／OwnerUserIds 兩個 List，PrtgHostSnapshot.cs:117-138）＋排序。
- 成本：隨主機數；每請求 1～3 次。
- 改法：Scoped 服務內存一份（鍵＝`_hostSnapshot` 版本），或讓只需 id／名稱／Active／Tier 的呼叫端（儀表板 KPI、報表排行：`ReportService.cs:77,172` 等）使用輕量投影。
- 呼叫端：`DashboardService:77`、`ReportService:56`、`ReportCategoryTrendService:40`、`ReportEffectivenessService:57`、`PermissionChangeService:109,706`、`HostsController:40,83`、`IssueNoteController:37`、`HostPrtgDetailService:44`。
- 風險：回傳是可變 `List<WebHost>`；若有呼叫端會改元素或清單，共用會互相污染（目前複製所以安全）→ 需逐一確認或回傳唯讀型別。

### C7. 每請求重複的主機／設定讀取（探測＋複製／反序列化）
- 現況：§3 表。`OccurrenceStatusResolver.cs:34` 每次 Resolve 建 3000 筆字典；`HandlingHistoryQueryService.cs:267-268` 每次 `new HostAliasIndex(...)`；各處 `_settings.Get()`。
- 改法：Scoped 的「請求內快照」服務（主機快照、別名索引、設定物件、靜音條件），讓所有服務取同一份；`EfIssueAggregateQuery` 的 19 處 `AliasIndex()` 探測可改為「傳入同一份快照」。
- 影響：全部上述服務；`GetAllCallCount` 效能契約測試（ReportService.cs:54-56 註解提到 `ReportServiceTests` 釘住）要同步調整。
- 風險：設定被另一請求改了之後，同一請求內讀到舊值（比現在「每次都最新」略弱，但更一致）；需確認沒有「同一請求先改後讀」的寫入路徑（交辦／設定儲存）。

### C8. 為 `IssueOwnerStore`、`UserGroupStore` 等高頻讀取的集合開 `cached: true`
- 現況：`IssueOwnerStore`（`Core/Persistence/IIssueOwnerStore.cs:28`）、`UserGroupStore`（`UserStore.cs:104`）建構子沒傳 `cached`，每次 `GetAll()` 整份 blob 讀＋反序列化；`ScheduleOptionsStore`／`UserPreferenceStore` 同。
- 成本：隨 blob 大小；非 ViewAll 使用者每個請求讀 UserGroupStore；每次版本戳推進讀 IssueOwnerStore。
- 風險（**重要**）：`JsonBlobCollection` 快取共用元素參照（JsonBlobCollection.cs:68 註解「呼叫端不可修改回傳清單中的物件屬性」）。開快取前必須逐一檢查所有 `GetAll()`／`Get()` 呼叫端有無「取出 → 改屬性 → 寫回」（`RecordRepository.cs:212-221` 註解明說 hosts 就是這個原因而不快取實體）。未做這項檢查前不要開。
- 影響：Issue 負責人與靜音相關頁面、授權解析、郵件。

### C9. `AggregateByDate` 與 `CategoriesByDate` 改 SQL 端分組；`allowedRecordIds` 改子查詢
- 現況：Agg:1674-1691, 1880-1897；對照 `AggregateByHost`（Agg:1773-1777）已用 `recordsQuery.Select(x => x.RecordId)` 子查詢。
- 改法：類別清單用 `GROUP BY RecordDate, Category`＋`MAX(SeverityRank)`＋`COUNT`（`AggregateByHost.categoriesRaw` 同形狀，Agg:1779-1788）；主機日的存活主機去重只在有合併主機時才需要拉列。
- 呼叫端：只有 `RecordListQueryService.cs:363`（依日期視角）。
- 風險：低到中（`Surviving` 去重語意與「較高風險等級勝出」要保持，Agg:1676-1681 註解）。

### C10. `AggregateReportKpi`／`Pair` 改 SQL 計數
- 現況：Agg:1099、1174 把每個主機日的 (風險,是否缺口,[日期,HostId]) 拉回再數。
- 改法：SQL `GROUP BY RiskLevel` 取計數、缺口以 `COUNT(CASE …)`；`AffectedHosts` 以 `SELECT DISTINCT HostId` 只拉主機 id（Agg:1101-1108 單期版本已是這樣）。
- 呼叫端：`DashboardService.cs:211`（單期）、`ReportService.cs:109`（Pair；介面預設實作是呼叫兩次單期，測試替身用）。
- 風險：低；既有等值測試釘住「Pair ＝ 逐期呼叫」。

### C11. category-trend／effectiveness 不要把低風險主機日整批載入
- 現況：`ReportCategoryTrendService.cs:62`、`ReportEffectivenessService.cs:91` 呼叫 `QueryLightweight` 不帶 `RiskLevels`；對照 `ReportService.cs:146-157` 已帶 `actionableLevels`（並註明「90 天下低風險列占絕大多數」）。
- 改法：
  - effectiveness 的三態分桶母體本來就是高中風險日（類別註解），再發生率才需要全風險日的「出現日」索引——可改成只對有已處理標記的 (主機, 簽章) 查 lf_top_issues（`BuildRecurrence` 本來就只為這些組合建索引，ReportEffectivenessService.cs:152-157）。
  - category-trend 只需要 (存活主機, 問題鍵, 日期, 類別, 嚴重度, 來源, 事件)；可由 SQL 對 lf_top_issues 相異化後回傳，不經 `DailyAnalysisRecord`。
- 呼叫端：這兩支的 SummaryCache 鍵與 DTO 不變；`QueryLightweight` 其他呼叫端（列表慢路徑、ReportService 非 All、HandlingHistoryQueryService:303、ScheduleController:244、Core 的 PRTG 取數／回填）不受影響。
- 風險：中到高——必須與 `RecordRepository.ApplySeverityVisibility`／`TryApplyDayRiskVisibility`（RecordRepository.cs:83-115 與其下）的顯示規則逐位等價；先寫等值測試（舊路徑 vs 新路徑同資料集比對）。

### C12. 執行總表的預索引
- 現況：`RunMonitorService.cs:182`（總表，天數 × 主機）、`:325,327`（展開，一台主機兩次）、`:529-533`；每次對整份 `runs` 線性掃＋每筆 `StartedAt.ToString("yyyy-MM-dd")`。
- 成本：CPU／配置 ∝ 主機數 × 天數 × runs（推論：runs 以「每夜數趟 × 保留天數」計，3000 台時單次展開約數百萬次字串格式化）。
- 改法：進函式前建 `Dictionary<(hostName不分大小寫, dateStr), List<BatchRun>>`；Where／Select 共用。
- 呼叫端：`GetDaySummaries`、`GetDayDetailPage`、`BuildDayRows`（診斷匯出是否也用，未追：`StatusOfRun` :541 與 `RunExportService`）。
- 風險：低（純等價）。

### C13. 展開某日只算當頁需要的列
- 現況：`GetDayDetailPage`（:274-299）無論 page 為何，`BuildDayRows` 全部主機組 DTO；`sort=workflowComplete` 時逐台 `_workflow.Get`（:282,332）＝最多 3000 次往返。
- 改法：status／排序鍵需要的欄位先算（輕量），DTO 與 workflow 只對當頁補；workflow 排序改批次讀取。
- 影響：只有執行總表展開；與 `RunDayHostStatusDto` 欄位語意不變。
- 風險：低到中（排序穩定性要與現行一致）。

### C14. 待派試跑加快取（儀表板與待派頁共用）
- 現況：`DashboardService.cs:99-116` 只要 `Total`／`UnassignableTotal`，卻跑完整 `RunTrial`（含 `Aggregate(IssueExclusion.None)` 5 查詢、`LatestOccurrences`、`DispatchContext`、逐出現點決策）；待派頁每換頁（`GetGaps` 每次一次完整試跑，`WorkOrderBoardService.cs:191`）。
- 改法：以 (版本戳, 可見範圍, 期間, 候選人快照版本, 設定) 為鍵快取 `Trial`（需 SingleFlight、元素上限；比照 ActionableSnapshotCache）。
- 影響：儀表板待派卡、待派頁、立即派工（`RunAutoDispatch` 不可用快取，必須重算；`WorkOrderBoardService.cs:259-`）。
- 風險：中——派工脈絡含「現在」、候選人快照、不再打擾、抑制、規則；鍵漏任何一維就是拿舊的派工決策當現在的。

### C15. 依問題／依主機／依日期結果加快取（排序、翻頁不重算）
- 現況：三個視角整份算完再在記憶體 `Paginate`（RecordListQueryService.cs:884-895），沒有快取；換頁／排序＝整套重算。
- 改法：以 SummaryCache 風格快取「分組結果清單」（鍵：過濾條件＋可見範圍＋版本戳，不含頁碼與排序）。
- 影響：三個視角；內容含 `Resolve` 的處理狀態，必須吃版本戳（C4 的相同考量）。**需要元素上限**（`SummaryCache` 沒有，應比照 IssueRankingCache）。
- 風險：中；`IssueSearchResultDto` 的單筆大小隨問題數。

### C16. `AuditLogStore` 有篩選時的整段讀取
- 現況：AuditLogStore.cs:89-110；`Count(24h)` 在儀表板內（已被 SummaryCache 包住，影響小）。
- 改法：等價做法有限——篩選欄位（動作、使用者、結果、對象）存在 JSON 行內，不能下推。若接受 schema 變更（新增欄位／索引）則超出「不改運作邏輯」範圍；列為「改了需 schema 升級」。
- 風險：高（schema）。僅建議列入 BACKLOG，不在本輪做。

### C17. 排程頁輪詢的後端成本
- 現況：`PrtgRunHealth`（SchedulerHostedService.cs:694-714）每次閒置輪詢（10 秒）`GetRecentRuns(8)`＋規劃器；`ScheduleOptionsStore.Get()` 每個端點各讀整份 blob。
- 改法：`PrtgRunHealth` 以「runs 日誌最大 RunId／最後一行 seq」為版本（類似 `AiAnalysisRunState` 30 秒快取）；`ScheduleOptionsStore` 開 `cached`（`JsonBlobSingleton` 已支援，命中仍反序列化，所以對「共用實例被改」安全）。
- 影響：排程頁、`HealthService`（健康頁）、`ScheduleFreshnessService`（是否也讀 runs，未追）。
- 風險：低（`JsonBlobSingleton` 的快取本來就是安全的）；`PrtgRunHealth` 快取要在 run 開始／結束時失效。

### C18. `OccurrenceStatusResolver.Resolve` 的內層線性查找與每次建字典
- 現況：`OccurrenceStatusResolver.cs:34`（每次 3000 字典）、`:59-66`（每個 occurrence 對該主機的案件與處理列 `FirstOrDefault`）。
- 成本：occurrences × 每主機處理列數；儀表板內呼叫 2～3 次。
- 改法：以 (IssueKey 比較器, 日期) 預建字典；主機字典由 C7 的請求內快照提供。
- 呼叫端：`IssueHandlingRollupQuery.cs:53`、`IssueTodoQuery.cs:73`、`RecordListQueryService.cs:447,850`、`MailIssueDigest.cs:110,205`（郵件摘要，Singleton 路徑，在背景）。
- 風險：低。

---

## 9. 不確定或沒讀到

- **`DispatchContext.Build`、`WorkOrderDispatcher.Decide`、`_candidates.Build()`（Web `IDispatchCandidateSource`）** 的內部查詢數未追；RunTrial 的「12～15 次」是推估（已讀到的 `Aggregate` 5、`GetOpenKeys` 1、`LatestOccurrences` 1、suppressions／rules／noiseMarks 各 1 次等）。
- **`PrtgStore.GetLatestHostMapForHosts`／`GetSensorStatesForDevices`** 的實作與在 3000 台全未回報時的列數未讀，只確認是 `IN` 主機 id 清單。
- **`IssueNoteController`、`HostDetailController`、`HandlingController`、`RecordDetailQueryService`** 的完整鏈未追（不在指定入口範圍）。
- **EF `Contains` 在 SQL Server／SQLite 的實際翻譯形狀**：程式註解稱單一 JSON 參數（OPENJSON／json_each）；本盤點沒有看實際產生的 SQL，也沒有量測 54 萬個元素的參數對兩個 provider 的實際行為。
- **`ReadAllRunLines`** 在 repo 中不存在（指定入口中提到的名稱）；實際讀取路徑是 `BatchRunStore.GetRecentRuns`／`GetRun`／`GetLogs`／`ForEachRecentLog`（Core/Persistence/BatchRunStore.cs:420,452,483,523）。
- **`EfJsonLogStore.EnumeratePageSize`** 的值未讀，`GetRecentRuns` 的記憶體峰值（逐頁串流）因此不確定；run 日誌每一趟有多少行（`StartRun`＋`FinishRun` 以外有無進度行）也未統計。
- **`NoiseMarkStore` 快取**：只確認 `cached: true`（INoiseMarkStore.cs:29）；它在互動查詢路徑的呼叫端（`WorkOrderBoardService` 的 DispatchContext、`RecordDetail` 一帶）未追。
- **`HandlingHistoryQueryService.LoadScopeContext`／`FilterByScope`／`ClassifyDays`** 的內部（報表非 All、處理成效）只讀到進入點與 `_store.GetMany`／`_issueStore.GetMany`／`_cases.GetOpenMany` 三個呼叫，沒逐行讀。
- **`ScheduleFreshnessService`／`HealthService` 完整成本**未追；只確認 `GetAttention` 有 30 秒快取。
- **所有「3000 台 × 180 天」的列數數字都是上限推估**，沒有實測資料；各表的「往返」數是加總 `Select/ToList/Count` 次數的估值，未以 SQL 效能監控（`SqlPerformanceMonitor`）核對。
- **`CaseDaySync` 與 `PrtgRiskReview` 的推進頻率**（§5.1）是由迴圈結構推得，沒有看實際 `DaySyncPending` 或重評批次量；實測應看 `DataVersionStamp.Current` 的變化率（目前健康頁是否暴露未查）。
- 沒有執行任何測試或建置；沒有修改專案檔。
