# 回饋第十九輪規劃：問題主視角一次到位（FEEDBACK-19）

> 狀態：**規劃完成，待實作**。基準：dev（feedback-18 結案後，1963 測試綠）。
> 來源：外部審查回饋（問題主視角／嚴重程度判讀／架構落差三大面向）＋使用者六項定案。
> 除非必要否則不要讀取 docs/archive/ 內容。

## §0 背景：查證結論摘要

本輪回饋與 2026-08-06 的規模化輪（scale-issue-first）系出同源。動工前全面查證現況，結論：

**已解、不再處理的批評**：事實表已落地（`lf_top_issues` 即簽章×主機×日×次數×嚴重度）；
聚合是寫入當下同步插列＋查詢時 GROUP BY，回填只是存量修補；處理狀態八態＋三層指派已完整；
權限能力分層與 /setup 精靈已存在。

**仍成立的核心批評**：前端「問題主視角」完成了（依問題 tab 最左、無參數預設 issue），
但後端只有排行卡走 SQL——問題清單四視角、儀表板其餘卡片、報表 KPI 全部仍是
`_repository.Query()` 全期間 blob 進記憶體。風險類型卡大數字是跨日直接累加；「今天」按鈕必為空
（分析只產到昨天）且觸發「本期無風險訊號」綠橫幅；Todo 單位是風險日數；無 fleet 層級結論共享；
郵件本文仍是主機×日明細；無機房級基準線、無優先度分數、無主機分級；`FirstSeen` 被查詢期間截斷。

**查證中抓到的既有 bug（批次A 處理）**：
1. `IssueRankingBuilder.Build` 的 `handlingByIssue` 兩個正式呼叫端（`DashboardService.cs:69`、
   `ReportService.cs:51`）都沒傳 → `OpenHostCount`/`ResolvedHostCount` 恆 0，§10.6 整條未生效。
2. 「今天」按鈕＋綠橫幅組合把「沒資料」顯示成「沒事」（郵件路徑已修過同 bug，儀表板未同步）。
3. `lf_top_issues.host_id` 註解宣稱「映射到存活主機」，實際寫入端直接存紀錄自帶 HostId、
   `MergeHost` 不回寫 → 合併主機在 SQL 聚合重複計數（文件與實作矛盾）。
4. `EfAnalysisRecordStore.Append` 兩次 `SaveChanges` 無交易 → 主列成功子列失敗會靜默漏數。

## §1 使用者定案（2026-08-14）

| # | 決策 |
|---|---|
| D1 | fleet 層級**一次到位做到好**，但改法須先評估影響（→ §2 設計決策一） |
| D2 | 優先度分數依評估定案（→ 定案：基準線偏離＋最小版固定公式分數，無權重設定 UI） |
| D3 | 儀表板／報表期間右端**全面右移為昨天**（近 7 天＝昨天往前 7 天） |
| D4 | saved view／訂閱**本輪不做** |
| D5 | **單一首頁**，依可見權限顯示數量；不做多套落地頁 |
| D6 | 範圍**一次到位**（含郵件問題優先與統計強化） |

程式面約束（使用者明示）：符合 SOLID／KISS；需求已明確，**不過度設計、不留多餘接口**。

## §2 全案設計決策

### 決策一：fleet 層 ＝「問題檔案」（IssueProfile），不新造案件實體

評估過三案：(a) 新增 fleet 案件實體（鍵＝簽章）並把主機案件降級；(b) 完全不動，只做讀取面；
(c) 擴充既有 `IssueOwnerRule` 為問題檔案。**定案 (c)**，理由：

- (a) 會貫穿 `IssueCaseCoordinator`、可見範圍授權（主機層語意）、日狀態推導與既有案件遷移，
  等級不亞於一次架構重構；且回饋要的三件事（跨主機結論＋自動套用、問題層負責人、問題生命週期）
  都能以 (c)＋讀取面聚合取得，(主機,簽章) 案件仍是「誰在處理哪台」的正確粒度，不該消失。
- `IssueOwnerRule` 的鍵就是 (Source, EventId)、比對已單點化（`Matches`/`KeyOf`/`IndexByKey`），
  blob 反序列化對新欄位天然容忍（同 `IssueHandling.CaseId` 前例）——**沿用 blob key
  `issue_owners` 擴欄＝零資料遷移**，六個既有消費端（可見範圍／郵件路由／自動帶入處理人／
  badge／下拉／隱含 User 角色）零行為變更。

`IssueProfile`（類別自 `IssueOwnerRule` 改名，檔內註記 blob key 命名歷史）：

```
既有欄：SourceName, EventId, OwnerUserIds, Note, UpdatedAt, UpdatedByAccount
新增欄：ConclusionStatus   (string?  結案四態之一；null＝無結論)
        ConclusionNote     (string   結論原因，必填)
        ConcludedById / ConcludedByAccount / ConcludedAt
        AutoApply          (bool     之後新出現的主機日是否自動套用)
```

**自動套用掛鉤**：`IssueCaseCoordinator.AttachNewDay`（`IssueCaseCoordinator.cs:191`）。
- 解除 `:194` 的提早返回（`openCases.Count == 0` 時 fleet 規則也要生效——那正是主要情境）。
- 優先序：**當日已有人工標記 ＞ 進行中案件繼承 ＞ fleet 結論自動套用**（前兩者為既有行為，
  與統一標記「有人接手整台略過」的既有設計一致）。
- 套用＝寫 `IssueHandling{ Status=ConclusionStatus, Note="〔機房結論〕"+ConclusionNote,
  ActorId=null, ActorAccount="", CaseId=null }`（系統寫入慣例同 CaseAttach）＋處理歷程
  `HandlingActions.FleetApply`。詳情頁顯示即 Note 本文，不另做徽章、不加來源欄。
- **不寫 NoiseMark**：NoiseMark 是（主機×完整簽章）的顯示層記憶，只在「無 handling 列」時
  提供預設判讀；fleet 套用直接落 handling 列，NoiseMark 無事可做。不做 fleet 版 NoiseMark，
  避免 `ResolveIssueStatus` 出現三來源優先序。

**既有日的套用**：沿用 `BulkCloseIssue` 原樣（含 5000 筆上限與預覽、可見範圍過濾、
「有進行中案件的主機整台略過」）。**不做背景大規模歷史回寫**：極端案例（6000 台×120 天）
的寫入量不可控，且批次D 把 Todo 改為問題口徑後，殘留歷史日對使用者可見數字的影響大幅下降，
剩餘的隨保留期自然消退。UI 超限時引導縮小區間分次執行（既有行為）。

**解除結論**＝清結論欄位（AutoApply 停止），已寫入的 handling 列**不回滾**（誠實留痕；
要翻案走既有逐日／統一標記改 open）。

**不含**：常設自動指派處理人（BACKLOG 乙案）——負責人路由（`DefaultHandlerId` 問題負責人優先）
已涵蓋「帶入處理人」需求，自動建案仍無明確需求，維持遞延。

### 決策二：讀取路徑全面 SQL 化的高度原則

「組」級資料在記憶體處理**可以**，「主機日×blob」級**不行**——病灶是 O(主機×天) 的
blob 反序列化，不是幾百個問題組的記憶體運算。因此：

- 依問題視角：SQL 聚合出全部組（一組一列）→ 記憶體做處理狀態 join＋過濾＋排序＋分頁。
  不強行把 `DayHandlingDerivation`／組狀態機翻成 SQL（保住單一事實來源的 C# 純函數）。
- 明細慢速路徑：SQL 撈**輕量列**（真表欄位，不解 blob）→ 既有純函數推導 → 分頁。
- blob（`content_json`）降級為**單筆詳情頁的權威來源**，所有清單/聚合路徑不再碰它。

**跨後端注意**：`COUNT(DISTINCT a,b)` 多欄 distinct 兩個後端都不直接支援，一律用
`Select(new{...}).Distinct().Count()`（EF 翻成子查詢），儲存合約測試雙後端驗證。

**授權下推鐵律**：每條新聚合查詢必傳 `visibleHostIds`，慣例「null＝不限、空集合＝零結果」
（`EfIssueAggregateQuery.cs:29-31`），逐條補授權測試。D5 由此自然滿足：單一首頁，
所有數字都是可見範圍內的數字。

### 決策三：口徑定義（批次C／D 的規格）

- **期間錨點**：全站讀取面的期間右端＝`DateTime.Today.AddDays(-1)`（分析只產到昨天）。
  按鈕「今天」→「**昨日**」；相對日文案（「今天仍在發生」等）改以昨日為基準。
- **風險資訊（一筆）**＝期間內去重的 (主機, 問題)：同主機同問題多天合併為一筆。
- 風險類型卡：**大數字＝風險資訊筆數**；小字＝「期間累計 M 筆（主機×日）」；
  「N 台主機」保留；嚴重度徽章改去重口徑（每筆風險資訊取期間內最高嚴重度歸類）。
- Todo：**大字＝未處理問題 X 個**、副標「影響 N 台・未處理風險日 M」；逾期同改問題口徑；
  群組卡「未處理」同步為群組內未處理問題數。

### 決策四：統計強化的取捨

- **機房級基準線**（CP 值最高）：每簽章過去 30 天（至昨日止）「出現日的影響台數」中位數；
  偏離倍數＝最近活躍日台數 ÷ 基準。查詢時對**當頁的組**計算（有界），不建聚合表。
- **PriorityScore 最小版**：`static IssuePriorityScorer` 純函數、常數權重寫死、無設定項、
  無擴充接口；輸出總分＋成分明細（前端可展開「為什麼是 N 分」）。只改**重點問題卡**的排序，
  其他視角排序不變。公式見批次G。
- **Host Tier**：`WebHost.Tier` 三級（核心／一般／測試，預設一般），blob 零遷移；
  只餵分數與徽章，不做獨立權重系統。
- **fleet first-seen**：新真表 `lf_issue_first_seen`（分析寫入時 insert-if-absent），
  不受查詢期間與（未來的）保留期修剪截斷；期間內 `FirstSeen` 改標示為「本期首見」。
- **不做**：擴散斜率獨立指標（`PreviousHostCount` 對比＋基準偏離已涵蓋語意）、
  MTTA/MTTR 呈現（資料基礎見批次B B8，成效指標另案）。

## §3 批次總表

| 批次 | 內容 | 依賴 |
|---|---|---|
| A | 正確性修復四項（§0 bug 1~4）＋§10.6 接通＋報表顯示範圍選擇器 | 無 |
| B | 資料基盤：新抽欄、`lf_issue_first_seen`、存量回填、正確性缺口收斂 | 無 |
| C | 期間右移昨日（儀表板／報表／文案／下鑽） | 無 |
| D | 儀表板口徑：風險類型雙數字、Todo 問題口徑、群組卡 | B |
| E | 讀取路徑全面 SQL 化（四視角、明細慢速路徑、儀表板其餘卡、報表、Cluster） | B |
| F | 問題檔案（IssueProfile 擴充＋結論＋自動套用＋管理頁） | 無（UI 與 E 弱相關） |
| G | 統計強化：基準線、PriorityScore、Host Tier、fleet first-seen 呈現 | B、E |
| H | 郵件問題優先 | D（逾期 rollup）、B |
| I | 文件同步＋BACKLOG 收斂＋全案體檢 | 全部 |

## §4 批次明細

### 批次A：正確性修復

**A1 — §10.6 接通＋重點清單排除已有結論（含 BACKLOG「儀表板重點問題卡不含未處理數」與 D6 乙案）**
- `DashboardService.cs:69`、`ReportService.cs:51` 補傳 `handlingByIssue`：以
  `IssueAggregate.IssueKeys`（`DistinctSignatures` 已回傳）批查 `lf_issue_handling`／
  `lf_issue_cases` 組 `IssueHandlingRollup`（`LookupRollup` 已寫好，目前是死碼）。
- 重點問題卡：全部主機已有結論的問題退出清單，卡底「另有 N 個問題已有結論（未列入）」。
- 前端重點問題卡補「未處理」欄（`OpenHostCount`）。
- 報表問題排行套「顯示範圍」選擇器（全部／排除已有結論），移除甲案常駐說明文字。
- 測試：rollup 接線後排行結果含未處理數；全結論問題退出＋卡底計數。

**A2 — 主機合併計數修復**
- `HostAdminService.MergeHost` 補 `UPDATE lf_top_issues／lf_daily_records SET host_id=@目標
  WHERE host_id=@來源`（`host_name` 不動，保留當時名稱事實）。
- 修正 `LfDbContext.cs:285`／`IssueRankingBuilder.cs:13-18`／DB-SPEC 的「已修掉」宣稱為實況。
- 測試：合併後 SQL 聚合 `HostCount` 不重複；再合併（鏈式墓碑）情境。

**A3 — `EfAnalysisRecordStore.Append` 交易化**
- `BeginTransaction` 包住主列與 `lf_top_issues` 子列的兩次 `SaveChanges`。
- 測試：子列寫入失敗時主列回滾（雙後端）。

**A4 — `DashboardController.Summary` 的 `days` Clamp（1..90，同 host-detail 慣例）。**

### 批次B：資料基盤（schema＋回填）

寫入端：`EfAnalysisRecordStore.Append`（shaped 抽取）；DDL：`SchemaUpgrader` 冪等補欄（既有慣例）。

**B1 — `lf_daily_records` 新抽欄**（供批次E 全面 SQL 化；來源皆為 blob 既有屬性）：
`headline`、`data_incomplete`、`security_log_available`（存兩個原始事實欄，涵蓋缺口
`(data_incomplete=1 OR security_log_available=0)` 查詢端算，不做合成欄避免衍生漂移）、
`error_count`、`warning_count`、`ai_analyzed`、`ai_pending`。

**B2 — `lf_top_issues` 新抽欄**：`known_issue`、`event_key`（Linux 完整簽章第五段——
解掉「處理狀態 join 對不上／同 program 不同規則併組」的既知缺口，`IssueHandling.cs:69-71`
的 v1 限制就此收斂；`IssueSignatureKey` 組回邏輯同步吃五段）。

**B3 — 新表 `lf_issue_first_seen`**：`(source_key PK, event_id PK, source_name, first_seen)`。
- 寫入：`Append` 對當日相異 (Source,EventId) insert-if-absent（量小、走 PK）。
- 種子：一次性 `INSERT ... SELECT MIN(record_date) FROM lf_top_issues GROUP BY`。
- 誠實限制（文件註記）：種子值受建表當時保留期下限截斷，之後不再被截斷。

**B4 — 舊 Critical 正規化一次性資料修正**：`UPDATE lf_top_issues` 把 Critical →
High＋`elevates_day_risk=1`（與 `RecordRepository.NormalizeLegacySeverity:160` 同語意）。
blob 讀取端正規化保留（單筆詳情仍走 blob）。

**B5 — `has_correlation` 舊列回填**（從 blob 的 `CorrelationAlerts.Count>0` 回寫）。

**B6 — 存量回填機制**：`lf_daily_records`／`lf_top_issues` 各加 `extract_version int NOT NULL
DEFAULT 0`（本輪寫入＝1；回填掃 `<1`），沿用 `TopIssueBackfiller` 的「毫秒級判定＋背景搬＋
`IssueStatsPending` 旗標」整套機制把 B1/B2/B4/B5 從 ContentJson 補到舊列。版本欄取代
`record_date==MinValue` 這種哨兵手法，回填完成前儀表板照現行方式顯示「統計回補中」。

**B7 — 嚴重度可見性 SQL 下推共用元件**：把 SiteHidden 嚴重度集合＋Critical→高映射轉成
`severity_rank` 條件的單一 helper（`RecordRepository.cs:121` 的記憶體過濾語意等價搬到 SQL 端），
批次D／E 所有聚合共用。附等價性測試（同資料兩路徑同結果）。

**B8 — `lf_issue_handling.created_at` 欄**：寫入時落、UpdatedAt 續當並發權杖、本輪不消費。
理由：MTTA（首次被人碰的時間）事後補不回來，成效指標輪（另案）需要它；僅此一欄，
不建任何查詢接口。

### 批次C：期間右移昨日

- `DashboardService.cs:44,52`：錨點 `anchor = DateTime.Today.AddDays(-1)`；
  `from = anchor.AddDays(-days+1)`、`To = anchor`。
- `Dashboard.cshtml:11`：「今天」→「昨日」（`data-days=1` 不變）。
- `ReportService` 本期／等長前期同步以昨日為錨。
- 相對日文案全面校準：`DaysSinceLastSeen` 以 anchor 計；`dashboard.js` 的
  「今天仍在發生」→「昨日仍在發生」；`issueSpanCell`／`lastSeenCell`／host-detail 逐一核對。
- 下鑽連結的 from/to 同步右移。
- 郵件（已右移）、`RunMonitorService`（已右移）不動；全站 `DateTime.Today` 讀取面用途普查一輪，
  逐處判定「錨點」還是「牆鐘」，清單記入實作紀錄。
- 驗收：瀏覽器實測「昨日」有資料；綠橫幅只在真無風險時出現。

### 批次D：儀表板口徑

**D1 — 風險類型卡雙數字＋SQL 化**
- `IIssueAggregateQuery` 新增 `AggregateByCategory(from, to, hostIds, severityFilter)`：
  per category 回傳 `RiskItemCount`（distinct (host_id, source, event_id)）、
  `CumulativeCount`（COUNT(*)）、`AffectedHosts`、去重口徑的 High/Medium/Low/Elevates
  （每筆風險資訊取期間內最高嚴重度）。
- `DashboardService`／`ReportService` 改走之；`RecordStatsBuilder.BuildCategoryCards`
  與 `CategoryAggregator.Merge` 的呼叫端退場（報表同步口徑——口徑不一致正是本輪要解的病）。
- 前端：大數字＝`riskItemCount`、副標「N 台主機」、小字「期間累計 M 筆（主機×日）」、
  徽章 tooltip 說明口徑。
- 測試：同一資料集（一主機一問題連續 3 天）大數字＝1、小字＝3；嚴重度可見性下推等價。

**D2 — Todo 問題口徑**
- 新查詢服務 `IssueTodoQuery`（Web 服務層）：期間內可行動日（高／中）依簽章 rollup →
  `OpenIssueCount`／`InProgressIssueCount`／`OverdueIssueCount`＋`AffectedHostCount`；
  狀態判定抽用**共用組狀態解析器**（見 E0，單一事實來源，勿另寫第二套）。
- `HandlingTodoDto` 保留日數欄位供副標；KPI 卡大字「未處理問題 X 個」、
  副標「影響 N 台・未處理風險日 M」；下鑽 `/records?view=issue&statuses=open`。
- 群組卡 `UnhandledCount` 同步為群組內未處理問題數。
- 逾期 rollup 同時是批次H「逾期」區塊的資料來源（一份查詢兩處用）。

### 批次E：讀取路徑全面 SQL 化

**E0 — 共用組狀態解析器（前置）**：把 `BuildIssueGroup:406-440` 的組狀態機
（案件優先→最近出現日 handling→觀察到期→unhandledSeverities 預設）抽成純函數
`IssueGroupStatusResolver`：輸入＝每主機最近出現日＋簽章＋severity（SQL 供給）×
批載的 handling/case 列；輸出 GroupStatus／HandlingSummary／Handlers。先以既有測試
釘住行為再搬（行為不變是硬驗收）。

**E1 — `SearchByIssue`**：`Aggregate`（擴充 `KnownIssue`＝latest、簽章含 event_key 五段、
未指定期間時以 `MIN/MAX(record_date)` 純量查詢當 `PeriodDays` 分母）→ 組列表（組數級）→
E0 join → statuses／unassigned／riskLevels（問題嚴重度映射 `:363`）／categories 過濾 →
排序（severity|hostCount|dayCount|totalCount|lastSeen）→ 分頁。全程零 blob。
展開列沿用既有明細端點（主機清單本來就是延遲載入，API 形狀不變）。

**E2 — `SearchByDate`（全 SQL，欄位全有對應）；`SearchByHost`（SQL＋B1 `headline`；
latest 列取法：GROUP BY host 取 `MAX(record_date)` 後一趟補查該列 headline/risk）。**

**E3 — 明細視角**：快速路徑改讀 B1 新欄（`QueryPage` 回頁不再解 blob）；慢速路徑三篩選
（statuses/overdue/unassigned）改「SQL 撈輕量列（risk_level＋三張處理真表＋top_issues
簽章/severity）→ `DayHandlingDerivation` 純函數 → 分頁」，`RecordListItemDto` 全欄位由
SQL 欄供給。`:50-53` 的過時註解一併更正。

**E4 — 儀表板其餘卡**：`BuildHostRanking`（SQL＋headline）、`BuildGroupRisk`、
高／中風險日、涵蓋缺口（B1 兩欄）全改 SQL；`BuildSilentHosts` 不碰 record，不動。

**E5 — `ReportService`**：KPI／Trend（`error_count`）／主機排行全 SQL；
`FilterByScope`（HandlingScope）同 E3 輕量列＋純函數兩段式。

**E6 — `ClusterSignatures`**：改 `Aggregate` 子集（欄位是現成真子集，最低風險項）。

**E7 — 退場普查**：`RecordStatsBuilder` 記憶體版、`RecordListQueryService` 全撈路徑移除；
`_repository.Query` 殘餘呼叫端全數列表，僅保留單筆詳情等單點用途（普查清單記入實作紀錄，
防「改共用路徑漏改讀取端」家族回歸）。

**E8 — 授權下推檢查表**：每條新查詢 visibleHostIds 傳遞逐條測試（含空集合＝零結果）。

### 批次F：問題檔案（設計見 §2 決策一）

- F1 `IssueOwnerRule` → `IssueProfile` 改名擴欄；blob key `issue_owners` 沿用；
  `IssueOwnerStore.Upsert` 逐欄複製補新欄（`:42-48` 註解點名的必改點）；
  六個消費端只跟著改型別名，行為不變（測試釘住）。
- F2 `AttachNewDay` 擴充 fleet 套用（優先序／寫入形狀／`HandlingActions.FleetApply`
  ／解除提早返回，見 §2）；profiles 每次 attach 一次性載入（blob 全讀本來就輕）。
- F3 設定入口：依問題視角動作欄「機房結論」（權限同統一標記：Assign＋Handle）＝
  既有統一標記 dialog 擴充「之後新出現的主機日自動套用」checkbox；落盤＝既有
  `BulkCloseIssue`（既有日）＋profile 結論欄 Upsert；稽核沿用 `IssueBulkClose`＋
  profile 更新稽核。
- F4 `/admin/issue-owners` 頁演進為「問題檔案」：負責人＋機房結論並列顯示、解除結論、
  首見（機房）欄（B3）；導覽項改名；API 擴充（PUT 收結論欄位、DELETE conclusion 子資源
  或 PUT 清空——實作時擇一，以既有 REST 慣例為準）。
- F5 詳情頁：fleet 套用列自然顯示「〔機房結論〕…」Note，不另做徽章。
- 測試：優先序三情境（人工標記不覆蓋／案件優先／無案件自動套用）、解除後停止套用、
  AttachNewDay 冪等、`escalated` 等非結案態不得作為結論值域。

### 批次G：統計強化

**G1 — 機房級基準線**：`IIssueAggregateQuery` 新增 `DailyHostCounts(signatures, from, to,
hostIds)`（GROUP BY (source,event_id,record_date) → distinct host 數）；服務層對**當頁組**取
過去 30 天（至昨日止）出現日台數中位數為基準，偏離倍數＝最近活躍日台數 ÷ 基準。
呈現：重點問題卡與依問題視角新欄「vs 基準」（例：「基準 3 台/日 → 昨日 12 台（×4.0）」；
基準期出現不足 N 日（N=3）顯示「新問題，無基準」）。

**G2 — Host Tier**：`WebHost.Tier`（核心／一般／測試，預設一般；hosts blob 零遷移）；
主機頁單台編輯＋批次設定；NetIQ／CSV 匯入選填欄；主機清單與詳情顯示徽章。

**G3 — PriorityScore 最小版**：`static IssuePriorityScorer.Score(input) -> (total, components)`，
常數寫死、無設定、無接口。公式（實作時以此為準，微調記入偏離註記）：

```
score = 100 × severityW × (0.5 + 0.5×hostRatio) × spreadW × noveltyW × openW × tierW
  severityW：高=1.0 / 中=0.6 / 低=0.3（去重後最高嚴重度）
  spreadW  ：基準偏離倍數 d → clamp(0.6 + 0.2×log2(max(d,1)), 0.6, 1.6)；無基準=1.2（新問題）
  noveltyW ：fleet first-seen ≤7 天=1.3，≤30 天=1.1，否則 1.0
  openW    ：0.5 + 0.5×(OpenHostCount / HostCount)（全處理完→折半，呼應 §10.6）
  tierW    ：受影響主機最高分級 核心=1.2 / 一般=1.0 / 測試=0.7
```

重點問題卡改依 score 排序，列展開顯示成分明細（「為什麼是 N 分」）；其他視角排序不變。

**G4 — fleet first-seen 呈現**：重點問題卡與依問題視角「首見（機房）」；期間欄改標「本期首見」。

### 批次H：郵件問題優先

- H1 共用組信查詢 `MailIssueDigest`（Singleton-safe：純函數＋注入 `IIssueAggregateQuery` 與
  D2 的逾期 rollup 查詢，比照 `HostVisibilityResolver` 模式；不注入 Scoped 的
  `IssueRankingBuilder`）：對收件人可見 hostIds 產出問題行，分區
  **新出現**（等長前期無此簽章）／**擴散中**（HostCount > PreviousHostCount）／
  **逾期**（rollup overdue）／**其他高風險**；每行
  `{Source}/{EventId}（{Category}）｜影響 N 台（前期 M）｜{區塊標記}`。
- H2 三種信改版：執行摘要＝統計行＋問題優先區塊，主機日明細縮為「請至站台」連結；
  每日／週報＝問題優先；高風險即時＝逐問題行＋主機日附錄（上限 20 行維持）。
  行上限語意改為「問題行」上限（50／20 沿用數值）。
- H3 效能：每收件人本期＋前期各一次 GROUP BY；相同可見範圍集合在同批次內共用結果
  （以 hostIds 集合雜湊為鍵的批次內字典，非常駐快取）。
- H4 `MailNotificationServiceTests` 全面改版（可見範圍切分／分區判定／截斷／零筆不寄沿用）。

### 批次I：文件同步＋收斂＋全案體檢

- WEB-SPEC：儀表板（口徑／昨日錨點／分數）、問題查詢（SQL 化資料流）、問題檔案、
  郵件章節、主機 Tier；DB-SPEC：新欄／新表／extract_version 回填／host_id 合併回寫；
  README 行為變更申報（期間右移、卡片口徑、排行改分數、郵件本文改版）；
  HelpContent 操作說明書同步（含 AI 可信度說明補一段固定文字——句級標示技術上不可行，
  現有「AI 區塊框＋ai_raise 依據碼」即誠實落點）。
- BACKLOG 收斂：移除「儀表板重點問題卡不含未處理數」（A1 解）、D6 乙案（A1 解）；
  「常設自動指派乙案」改註記與問題檔案的關係後續留；補「MTTA/MTTR 成效指標輪」條目
  （資料基礎：`created_at`＋`IssueCase` 時間軸＋處理歷程真表化議題）。
- 全案體檢輪（依慣例獨立 commit）：規劃逐項比對＋diff 獵 bug＋文件稽核＋雙後端合約測試。

## §5 明確不做／遞延（本輪定案）

| 項目 | 處置 | 理由 |
|---|---|---|
| saved view／訂閱 | 遞延 | 使用者定案 D4 |
| 角色化落地頁 | 不做 | 使用者定案 D5：單一首頁按權限顯示 |
| AI 句級可信度標示 | 不做 | 地端小模型輸出無法可靠溯源到句；現有二分＋說明文字為誠實落點 |
| fleet 案件實體（鍵＝簽章） | 不做 | §2 決策一：問題檔案＋讀取面聚合已滿足需求，避免案件模型重構 |
| fleet 結論的歷史日大規模回寫 | 不做 | 寫入量不可控；Todo 問題口徑化＋保留期消退已解可見影響 |
| MTTA／MTTR 呈現 | 遞延 | 本輪只落 `created_at` 資料基礎（B8） |
| 擴散斜率獨立指標 | 不做 | 前期對比＋基準偏離已涵蓋 |
| 常設自動指派處理人（BACKLOG 乙案） | 維持遞延 | 負責人路由已涵蓋帶入處理人 |

## §6 風險與體檢重點（實作時逐條核對）

1. **「改共用欄位漏改讀取端」家族**（歷輪最常見回歸）：B1/B2 新欄的「寫入端／回填端／
   讀取端」三方一致性測試；E7 的 `_repository.Query` 殘餘普查清單是防線。
2. **口徑變更的前後端一致**：大數字／小字／tooltip 的口徑說明必須同一套詞；
   報表與儀表板共用查詢，杜絕再分家。
3. **授權下推**：E8 檢查表逐條測試，空集合＝零結果的慣例不可破。
4. **雙後端等價**：多欄 distinct count 的 EF 寫法；`extract_version` DDL 冪等；
   B4 一次性 UPDATE 在兩後端各自驗證。
5. **行為變更申報**：期間右移、卡片口徑、排行改分數、郵件本文改版——README／spec 同步，
   實測前讓使用者知道畫面會變什麼。
6. **E0 行為不變是硬驗收**：先測試釘住再搬移，搬移 commit 不得夾帶行為修改。

---

## 實作紀錄與規劃偏離（實作時填寫）

### 批次A（完成，commit `d27ede7`，1966 測試綠）

四項全部完成，三處與規劃原文有偏離，理由如下：

1. **A2 主機合併修復的做法與規劃原文不同**：規劃草稿曾設想「MergeHost 回寫
   `lf_top_issues.host_id`」，實作前重新評估發現這會破壞 `UnmergeHost` 的反向修復能力
   （回寫後無法分辨目標主機底下哪些列原本屬於來源主機）。改為**查詢端解析**——
   `EfIssueAggregateQuery` 新增主機合併鏈解析（`HostAliasIndex`），與既有 blob 路徑
   （`HostLookup`／`RecordRepository`）用同一套邏輯，`host_id` 維持「紀錄當下識別」的
   歷史事實不變。連帶修掉一個規劃未預期的第二個問題：可見範圍只傳存活主機 id 時，
   舊識別下的合併前歷史會被 `WHERE host_id IN (...)` 整段濾掉（比雙重計數更嚴重）。
2. **A1 的 rollup 接線比規劃原文描述的更完整**：規劃原文只說「補傳
   `handlingByIssue`」，實作時發現這個可選參數本身就是死碼的成因（兩個呼叫端「忘記
   傳」正是問題所在），因此把計算收進 `IssueRankingBuilder` 內部（新增
   `IssueHandlingRollupQuery` 服務＋ `IIssueAggregateQuery.LatestOccurrences` 查詢
   方法），呼叫端不再需要知道 rollup 存在。§10.6 的排除邏輯（`ExcludeConcluded`）與
   兩頁footer/副標文字亦一併完成（原規劃第二條）。
3. **報表「顯示範圍」選擇器（D6 乙案的 UI 部分）延後**：與使用者確認後，這輪只做
   「固定排除已有結論＋誠實顯示排除筆數」，不做選擇器 UI（獨立功能，且需要
   ui-ux-pro-max 設計決策的範圍超出本批次），已記入 BACKLOG。

依 CLAUDE.md 規則，動手前就 A1 的前端呈現（未處理欄＋排除筆數文字）詢問是否套用
`ui-ux-pro-max`；使用者選擇套用，實作沿用專案既有 `docs/DESIGN-SYSTEM.md`（企業藍＋
Fira，已持久化的設計系統）與既有元件慣例（`renderTable`／footer 容器置於表格外以避免
`replaceChildren` 清空的既有寫法），未重新產生新的色彩/字型/版面規格。

瀏覽器端到端驗證（真實 dev DB，非僅單元測試）：儀表板「重點問題」卡與報表「問題排行」
在同一份資料上都顯示「另有 29 個問題已有結論（未列入）」，「未處理」欄數字與逐問題
的處理概況吻合，兩頁數字一致——直接驗證了這輪要解的「三個畫面數字對不起來」那類缺陷
在這個路徑上已經收斂。

### 批次B（完成，commit `2a010fd`，1981 測試綠）

B1/B2/B3/B6/B8 皆按規劃落地；B4、B7 與規劃原文有偏離：

1. **B4 改為 SQL 端正規化，不是一次性 UPDATE**：實作前重讀
   `RecordRepository.NormalizeLegacySeverity` 的類別註解，發現它明文規定「只在讀取時於
   記憶體正規化，不回寫資料庫——證據層是事後不可改寫的批次判定結果」。原規劃的
   「一次性資料修正」會直接違反這條既有原則。改為新增 `LegacySeverityRank`（Core 共用
   靜態類別，`Normalize`／`ForcesElevate` 兩個純函數），套用在
   `EfIssueAggregateQuery.Aggregate` 的 `MaxSeverityRank`／`ElevatesDayRisk` 計算——
   與 blob 路徑同一條規則的 SQL 端版本，行為對齊但不碰資料本身。
2. **B7（嚴重度可見性 SQL 下推）延後**：目前沒有任何消費端（批次D/E 才會需要），
   先建接口會是「不留多餘接口」原則要避免的那種東西。留到批次D 實際要用時再做。
3. **加做一項規劃未列的修復**：`RecordStorageShaper` 低風險日精簡路徑漏抄
   `LogIssueSignature.EventKey`——這是本輪 B2 新增 event_key 抽出欄時才會踩到的既有 bug
   （低風險日的 Linux 規則命中在精簡後會遺失完整簽章第五段），順手修掉並補回歸測試。
4. **`lf_issue_first_seen` 的 upsert 設計比規劃原文更細**：規劃只寫「insert-if-absent」，
   實作時發現 NetIQ 回補（`BackfillDays`）與多台主機平行處理會讓「較晚寫入卻是較早日期」
   與「兩台主機同時第一次寫入同一個新問題」都是真實會發生的情境，因此改成「取較早日期」
   的條件式 UPDATE＋撞唯一鍵時的重試邏輯，且刻意獨立於主交易之外（首見日是輔助呈現資料，
   不該讓它的競態害當天的分析結果整筆遺失）。

瀏覽器對真實 dev DB（已累積批次A測試時的資料）端到端驗證：啟動時 schema 升級一次補齊
14 個欄位＋1 張新表零錯誤，`DailyRecordBackfiller` 自動抓到 112 筆待回填舊列並完成回填，
儀表板／報表畫面數字與批次A驗證時完全一致（未受 schema 變更影響）。

### 批次C（完成，commit `e8dea0d`，1982 測試綠）

按規劃落地，範圍比原文列的「儀表板／報表」再稍寬一點——實作前對全站 `DateTime.Today`
逐處核對用途（規劃明列的「普查一輪」），發現「問題查詢」頁（Records）的「近 N 天」快捷範圍
與預設篩選窗、主機詳情頁的風險時間軸與問題發生明細窗口是同一類「查詢期間終點」錨點，
不動的話會跟儀表板/報表口徑不一致（同一個問題在不同頁顯示不同的「幾天前」），因此一併修正。
真正的「牆鐘」語意（到期日／逾期判斷／保留期 cutoff／郵件寄送時間戳）核對後維持不動。

`IssueRankingBuilder.Build` 的 `DaysSinceLastSeen` 順帶修成「以查詢的 `to` 為準」而不是
另外抓一次 `DateTime.Today`——這不只是配合本批次的錨點右移，也修正了一個獨立的既有問題：
報表檢視歷史區間（`to` 為過去某天）時，舊實作仍會用「真實今天」計算天數，答非所問。

前端新增 `analysisAnchorLocal()`（format.js）與既有 `todayLocal()` 並存，避免下一個人
在到期日／逾期判斷等真實時鐘情境誤用新的錨點函式。

### 批次E0（提前完成，commit `f079eb0`）

D2 明確要求「狀態判定抽用共用組狀態解析器（E0），勿另寫第二套」，所以在 D2 之前先做：
`RecordListQueryService.BuildIssueGroup` 與 `IssueHandlingRollupQuery`（批次A 產物）過去
各自實作同一套三態判定規則，抽成 `IssueGroupStatusResolver`（Core.Models 純函數）兩處共用。

抽取時抓到一個由重構自己引入又自己抓到的問題：一度把「今天」參數誤用成分析錨點
（批次C 的昨天），但觀察到期／逾期比對的是使用者設定的行事曆期限（真實時鐘），不是
分析資料的錨點——兩者是不同的「今天」，混用會讓「觀察至昨天」的案子誤判成還在觀察中。
既有測試 `SearchByIssue_觀察中未到期算處理中_到期算未處理` 當場抓到，改回真實
`DateTime.Today` 後測試通過，另補一條 `IssueHandlingRollupQuery` 這條路徑原本沒覆蓋到的
同款回歸測試。

### 批次D（完成，commit `124c101`+`6b6f8a8`，2004 測試綠）

**D1 風險類型卡雙數字**：新增 `IIssueAggregateQuery.AggregateByCategory`（SQL 兩層
GROUP BY：先依 (類別,主機,問題) 取期間內最高嚴重度與累計次數，host_id 解析成存活主機後
再依類別彙總），取代 `RecordStatsBuilder.BuildCategoryCards` 對整段期間 `DailyAnalysisRecord`
在記憶體用 `CategoryAggregator` 逐日彙總再合併的版本。`DashboardCategoryDto` 改雙數字
（`RiskItemCount` 大數字去重／`CumulativeCount` 小字累計），高/中/低分桶也改依風險資訊的
期間內最高嚴重度（三桶之和＝RiskItemCount）。移除未使用的 `MaxSeverity`／`CriticalCount`
欄位（順帶修正唯一消費 `MaxSeverity` 的 `AiInsightService` 呼叫端）。

報表的風險類型卡與既有的問題排行同一個限制：SQL 聚合是跨主機跨日的獨立投影，不受
「顯示範圍」（日層級處理狀態）篩選影響——比照問題排行既有的 scopeNote 做法，新增
`category-subtitle` 顯示同款提示，不做成「靜默不回應篩選」。嚴重度可見性沿用
`ParseUnhandledSeverities`（查證確認：SiteHidden 模式下的 `GetVisibleSeverities` 是
同一個底層欄位算出來的，交集恆等於自身，維持既有行為不變，不需要另外實作 B7 那種
「兩種設定路徑」的下推）。

**D2 Todo 改問題口徑**：新增 `IIssueAggregateQuery.ActionableOccurrences`（join
`lf_top_issues`／`lf_daily_records`，母體＝可行動風險日，與 `HandlingHistoryQueryService.GetTodo`
既有定義一致）＋ `IssueTodoQuery`（依 (Source,EventId) 去重計數）。新增
`OccurrenceStatusResolver` 共用骨架（批次載入 handling/case＋逐筆判定），`IssueHandlingRollupQuery`
一併重構為用它——避免這份樣板碼在批次H 的逾期摘要出現前就已經有第三份拷貝。
`DashboardDto.IssueTodo`（KPI 卡主要數字）與既有 `Todo`（風險日數，退居副標與報表
處理進度圖表用）並存，角色不重疊。群組風險卡 `UnhandledCount` 同步為問題口徑。

兩批瀏覽器端到端驗證：儀表板「其他」類別「8 個問題．期間累計 97 筆」與報表同一資料集
逐位相同；KPI 卡「11 未處理問題．影響 1 台．未處理風險日 9」。

批次E（讀取路徑全面SQL化）尚未開始，E0 已提前完成。

### 批次E1（完成，commit `7239edb`，2004 測試綠）

`SearchByIssue` 改由 `IIssueAggregateQuery.Aggregate`（問題數級的候選集）→ 依篩選條件
（EventId/Source/Categories/RiskLevels）窄化 → `LatestOccurrences`（窄化後問題 × 可見主機，
擴充 `KnownIssue` 欄位）→ `OccurrenceStatusResolver`（沿用批次D 骨架）逐主機判定處理概況
→ 組 DTO → 剩餘的 statuses／unassigned 過濾 → 排序 → 分頁。全程不再整批載入
`DailyAnalysisRecord`。這裡繞過 `_repository.Query` 內建的 `ApplyVisibility`，改新增
`ResolveVisibleHostIds`：交集 `IVisibilityService.GetVisibleHostIds()` 與請求的
HostIds/GroupIds，空集合＝零結果，語意與既有授權慣例一致。

**與規劃原文一處刻意偏離**：舊版 `request.RiskLevels`（風險層級 chips）被套用兩次——
一次在 `BuildFilter` 當**日風險等級**預篩記錄，一次在分組後當**問題嚴重度**過濾
group——兩種語意疊加，但日風險預篩其實與「依問題視角看的是問題嚴重度」這條既有設計
原則本身衝突（程式碼原有註解也承認這點），且新版 `IIssueAggregateQuery.Aggregate` 介面
沒有、也不需要新增「日風險」這個參數（沒有其他消費端需要）。新版只保留問題嚴重度過濾，
拿掉日風險預篩——測試覆蓋範圍內（`SearchByIssue_高風險日內的低嚴重度問題` 等）結果不變，
理論上唯一會外顯的情境（篩單一日風險等級、但某問題自身嚴重度跨日風險等級不一致）
沒有既有測試覆蓋，屬於刻意簡化而非疏漏。EventId/Source 過濾語意重新核對後與舊版
（`RecordFilterMatcher` 的精確比對）完全相同，並非規劃原文推測的「語意調整」。

瀏覽器對真實 dev DB 端到端驗證：`/records?view=issue` 近 30 天顯示 19 個問題，
處理概況三態、處理人（陳工程師／劉負責人／王主管／測試管理員）、涵蓋範圍、出現密度、
總次數與排序皆正確；勾選「未處理」狀態 chip 正確窄化為 3 筆，與明細列出的
「1 台未處理」筆數一致。

下一步：E2（`SearchByDate`／`SearchByHost` 全 SQL 化）。

### 批次E2（完成，commit `789ddb0`，2004 測試綠）

`SearchByHost`／`SearchByDate` 改由 `IIssueAggregateQuery.AggregateByHost`／`AggregateByDate`
兩句 GROUP BY（`lf_daily_records`＋`lf_top_issues`）回答，不再整批載入紀錄。
Categories／EventId／Source／MinSeverity 過濾比照 `EfAnalysisRecordStore.ApplyPushableFilters`
的 exists 子查詢寫法（新增 `ApplyIssueExistsFilters` 共用私有方法）；RiskLevels 為母體篩選
（篩掉的日子連同分桶一起消失，語意同 `RecordFilterMatcher`）。

**體檢揪出兩個 E1/E2 共通的真缺口**（不是規劃階段預期的偏離，是實作時測試抓到才發現）：

1. **`SearchByIssue` 漏接 `request.Severity`**（報表「類別×嚴重度」下鑽用的門檻篩選，
   `reports.js` 的 `severity=` 連結）——E1 提交時完全遺漏這個過濾維度，直到寫 E2 時
   核對 API controller 的四個端點共用同一組查詢參數才發現。已補上（`aggregate.MaxSeverityRank >= 門檻`）。

2. **SiteHidden 模式（問題嚴重度可見性，docs/archive/HISTORY.md S1）整段漏接**——
   全新 SQL 聚合路徑繞過 `RecordRepository` 的「單一咽喉」（`ApplySeverityVisibility`），
   E1 提交時完全沒注意到這一層過濾的存在，直到既有測試
   `SearchByHost_SiteHidden模式下類別聚合不含被隱藏層級` 失敗才發現波及 E1／E2 全部方法。
   修法：`LegacySeverityRank.ExpandVisibleRanks` 把可見嚴重度集合展開成可直接比對的原始
   severity_rank 集合（含 Critical→High 的正規化關係），貫穿 `Aggregate`／`LatestOccurrences`／
   `ActionableOccurrences`／`AggregateByDate`／`AggregateByHost` 五個方法（`ActionableOccurrences`
   目前無呼叫端傳入非 null 值，是為了不留下「同一份規則、有的方法做有的方法沒做」的不一致，
   一次補齊，不是本輪新增使用）。`RecordListQueryService` 新增 `ISystemSettingsService`
   依賴，取 `GetVisibleSeverities()` 轉型後傳入。

   `AggregateByCategory`（批次D，儀表板／報表風險類型卡）查證後確認**不受影響、不需要修改**：
   它的 `allowedSeverities` 是回饋十三輪新增項3的既有例外規則（「不論 SeverityDisplayMode
   為何值，一律只計入 UnhandledSeverities」），與 SiteHidden 是兩套獨立機制，只是恰好底層
   欄位相同——在 SiteHidden 模式下兩者交集等於自身，不套用不影響既有正確性；在 DefaultHidden
   模式下這個例外規則本來就該繼續套用。這是查證後排除的假警報，不是遺漏。

瀏覽器對真實 dev DB 端到端驗證：`/records?view=host` 近 90 天顯示 1 台主機（高3／中20／低0／
關聯3／類型 chips／最新狀況）；`/records?view=date` 近 90 天顯示 23 天，逐日高/中/低/關聯/
類型正確、分頁正常；`/records?view=issue` 重新驗證仍是 19 個問題，數字與 E1 驗證時逐位相同
（本環境非 SiteHidden 模式，新增的過濾不應改變既有畫面，驗證結果符合預期）。

下一步：E3（明細視角快速/慢速路徑 SQL 化）。

### 批次E3（完成，commit `6aceb79`，2004 測試綠）

只做了慢速路徑（Statuses/Overdue/Unassigned 篩選啟用時）：新增
`IAnalysisRecordQuery.QueryLightweight`／`IRecordRepository.QueryLightweight`，
與既有 `Query` 套用完全相同的可見範圍／日風險等級顯示／SiteHidden 嚴重度可見性
（同一個單一咽喉，直接重用既有 `ApplyVisibility`／`TryApplyDayRiskVisibility`／
`ApplySeverityVisibility`），差別只在回傳的 `DailyAnalysisRecord` 不反序列化整份
`ContentJson`——只填處理狀態判定/分類/風險等級需要的欄位（B1 抽出欄＋TopIssues
的判定用子集，從 `lf_top_issues` 重建 `LogIssueSignature`，不含 SampleMessages／
KeyDetails 等風險日詳情頁專用內容），`CorrelationAlerts` 用單一佔位元素表達
「有沒有」。`Search()` 的慢速路徑改呼叫它，下游 `DeriveProgress`／
`ComputeIsOverdue`／`IsUnassigned`／`ToListItem` 全部零改動（都只消費
`DailyAnalysisRecord` 既有欄位，這是選擇「重用同一個型別、只換資料來源」
而不是新開一個輕量型別的原因——影響面降到最小）。

**與規劃原文的取捨**：快速路徑（`QueryPage`，無 Statuses/Overdue/Unassigned 時，
多數瀏覽情境）本已只為「這一頁」載入，量級是 pageSize 不是全量，維持現狀不動——
規劃原文「快速路徑改讀 B1 新欄」屬於錦上添花的效能微調，不是 N3 那類真正的
規模化風險，本輪聚焦在慢速路徑這個真正的熱點，避免不成比例的改動換取邊際效益。

順帶更正 `Search()` 頂部一句過時註解（仍寫著已退役的 handling.json／
issue_handling.json 檔案後端，改成現行的 SQL 表名）。

瀏覽器對真實 dev DB 端到端驗證：明細視角近 90 天勾選「已處理」正確走
QueryLightweight，回傳的風險等級／涵蓋缺口（DataIncomplete/SecurityLogAvailable
推導）／風險類型／處理狀態／處理人（含案件 fallback「（案件）」標示）全部正確。

下一步：E4（儀表板其餘卡片全 SQL 化）。

### 批次E4（完成，commit `0ab65b3`，2004 測試綠）

比規劃原文更簡單：`DashboardService.GetSummary` 唯一的整批載入點
（`_repository.Query(new RecordQueryFilter { From = from })`）一次換成
`QueryLightweight`（批次E3 建的機制），下游 `RecordStatsBuilder.BuildHostRanking`／
`BuildGroupRisk`／`HighRiskDays`／`MediumRiskDays`／`CoverageGapDays`／
`HandlingHistoryQueryService.GetTodo` 五個消費點逐一核對後確認全部只讀取
`DailyAnalysisRecord` 的判定用欄位（RiskLevel／CorrelationAlerts 有無／Date／
Host／Headline／TopIssues／HasCoverageGap），QueryLightweight 全部覆蓋，
不需要像規劃原文設想的分別把 BuildHostRanking／BuildGroupRisk 改寫成呼叫
`AggregateByHost`——那樣反而要多維護一套「按群組彙總」的邏輯，這裡一行
swap 就達到同樣的效能效果（不再反序列化整份 ContentJson），下游零改動、
零回歸風險。`BuildSilentHosts` 本就不碰 records，維持不動。

瀏覽器對真實 dev DB 端到端驗證：近 30 天儀表板全部數字（KPI 卡／風險類型卡／
重點問題／高風險主機／依群組風險概況）與改版前逐位相同。

下一步：E5（ReportService 全 SQL 化）。

### 批次E5（完成，commit `cec4922`，2004 測試綠）

同批次E4 的簡化手法：`ReportService.GetSummary` 兩處整批載入（本期＋前一等長
期間比較）改呼叫 `QueryLightweight`。核對 `FilterByScope`／`BuildKpi`／
`BuildTrend`／`GetTodo` 四個下游消費點，全部只讀取判定用欄位，一行 swap 完成，
零下游改動。

瀏覽器對真實 dev DB 端到端驗證：`/reports` 近 30 天全部區塊正確渲染；額外測試
`handlingScope=open` 篩選——KPI／趨勢／主機排行正確歸零（FilterByScope 在這份
資料上找不到符合的日子），風險類型分布／問題排行維持不受影響（符合既有的
「不受 scope 篩選」文件化例外規則）。

下一步：E6（ClusterSignatures 改 Aggregate 子集）。

### 批次E6（完成，commit `ea0a073`，2007 測試綠）

`ClusterSignatures`（AI 歸納用，跨主機同簽章聚類）改呼叫 `IIssueAggregateQuery.Aggregate`
取代整批載入——`IssueClusterDto`（Source/EventId/HostCount/TotalCount）是
`IssueAggregate` 的現成真子集，過濾邏輯與 `SearchByIssue`（批次E1）同一套
（EventId/Source/Categories/RiskLevels/Severity，含 SiteHidden 嚴重度可見性）。

**驗證方式的取捨**：這支方法先前完全沒有測試覆蓋，且本機 dev 環境未設定 AI
（`/api/ai/query-summary` 因 `_ai.Available=false` 短路回 `data:null`，不會真的
呼叫到 `ClusterSignatures`），沒辦法瀏覽器端到端驗證。改為補三個單元測試釘住
行為（只留跨主機問題／依主機數排序取前五／套用風險類型過濾），全數綠燈——
這是本輪唯一一個無法瀏覽器驗證、改用單元測試補強信心的批次。

下一步：E7（退場普查）。

### 批次E7（完成，commit `746d2c8`，2007 測試綠）

- **死碼移除**：`RecordStatsBuilder.BuildIssueRanking` 確認零呼叫端（`IssueRankingBuilder`
  早在批次A之前的規模化輪就已取代它，死碼一直沒清掉）——移除；順帶修正
  `IssueRankingDto` 的 XML 文件註解，原本仍引用這支已刪除的方法，改指向實際
  供資的 `IssueRankingBuilder`。
- **`_repository.Query` 殘餘呼叫端普查**（共 4 處，全數核對後確認刻意不動）：
  - `RecordDetailQueryService.GetHostDetail`／`GetHostIssueOccurrences`——範圍是
    單一主機的天數視窗（時間軸／重點問題展開），不是跨主機聚合，量級與
    E1-E6 解的「M 主機 × N 天」問題不同量級；且用 `applyDayRiskVisibility:false`
    豁免（時間軸必須看完整證據，不受日風險等級顯示設定影響），`QueryLightweight`
    目前的簽章不支援這個參數，貿然套用會是行為改變而非單純效能優化。
  - `IssueHandlingCommandService.PlanBulkClose`／`ResolveIssueOccurrences`——
    寫入路徑指令（統一標記／指派前置查詢），範圍是單一 (Source,EventId) 問題
    的出現範圍，天然受「這個問題影響多廣」限制（多數問題影響個位數主機），
    不是全站規模；且已有 `MaxBulkCloseDayWrites` 上限守門。批次E的範疇是
    「讀取路徑」，寫入指令不在此列。

下一步：E8（授權下推檢查表）。

### 批次E8（完成，無程式碼變更——純查證）

逐條核對批次E新增／改動的六個 SQL 聚合方法（`EfIssueAggregateQuery.Aggregate`／
`LatestOccurrences`／`ActionableOccurrences`／`AggregateByCategory`（批次D既有）／
`AggregateByDate`／`AggregateByHost`），確認全部在方法開頭有
`if (hostIds != null && hostIds.Count == 0) return 空清單;` 這道守門——空集合
＝零結果，不是「不限制」。

`RecordListQueryService` 的四個呼叫端（`SearchByHost`／`SearchByDate`／
`SearchByIssue`／`ClusterSignatures`）全部經由同一個 `ResolveVisibleHostIds`
取得可見範圍，該方法保證回傳明確清單（永不回傳 null），可見範圍為空時
回傳空清單——與上述六個方法的守門語意銜接正確。

`QueryLightweight`（批次E3-E5 用，走 `RecordRepository` 既有的
`ApplyVisibility`／`HostMatcher` 機制）不是新機制，是重用 `Query`／`QueryPage`
早已驗證過的授權路徑——`HostMatcher` 對空 `HostKey` 集合的 `Matches()` 恆回
`false`（`_hostIds`／`_hostNames` 兩個 HashSet 皆空，兩個分支都比對不到），
逐位核對後確認這正是「空集合＝零結果」在這條路徑上的具體實作，沒有新增
授權風險。

未發現任何授權下推缺口。批次E（讀取路徑全面 SQL 化）全案完成。

---

## 批次E 全案結案摘要

E0~E8 全部完成，commit 序列 `f079eb0`→`51b1a3d`，dev 分支 `feature/feedback-19`，
2007 測試綠。體檢輪（E1/E2）揪出兩個影響全部新 SQL 路徑的真缺口——
`request.Severity` 報表下鑽門檻漏接、SiteHidden 問題嚴重度可見性整段漏接——
兩者都已修正並在 E1~E6 逐一驗證過。E7 退場普查移除一處死碼、修正一處過時
文件參照，並核對剩餘 4 處 `_repository.Query` 呼叫端皆屬單一主機／單一問題
範疇，非批次E範疇的規模化風險。除批次E6（AI 未設定，無法瀏覽器驗證，
改用單元測試補強）外，全部批次皆對真實 dev DB 端到端瀏覽器驗證過。

---

### 批次F1+F2（完成）

`IssueOwnerRule` → `IssueProfile` 改名（`git mv`，純改名 commit）＋擴充六個欄位
（`ConclusionStatus`／`ConclusionNote`／`ConcludedById`／`ConcludedByAccount`／
`ConcludedAt`／`AutoApply`）；blob key `issue_owners` 沿用，零資料遷移。
`AttachNewDay` 改為三層優先序（人工標記／既有處理 → 進行中案件 → fleet 結論
`AutoApply=true` 時自動套用），fleet 套用寫入 `HandlingActions.FleetApply`，
刻意不寫 NoiseMark（避免 `ResolveIssueStatus` 多一個判斷來源）。

### 批次F3+F4（完成）

設定入口採單一共用服務方法（`IssueOwnerAdminService.SetConclusion`／
`ClearConclusion`），供兩個呼叫端共用：F3 統一標記 dialog 的「自動套用」
checkbox（`records.js`，勾選時 `BulkCloseIssueRequest.AutoApply=true`，
`IssueHandlingCommandService.BulkCloseIssue` 內呼叫）、F4「問題檔案」管理頁
（`/admin/issue-owners`，`issue-owners.js`／`IssueOwners.cshtml`）的機房結論
編輯區。API 採 `PUT/DELETE {source}/{eventId}/conclusion` 子資源慣例。

**體檢揪出真 bug**：既有的 `IssueOwnerAdminService.Upsert`（負責人單獨儲存
路徑）原本建構全新 `IssueProfile` 未帶入既有機房結論欄位，會在每次「只改
負責人、沒動結論」的表單儲存時靜默清空已設定的機房結論——`IssueOwnerStore.
Upsert` 本身的逐欄複製沒問題，問題出在呼叫端沒把「它不認識的欄位」原樣
帶回去。修正為先 `before = _issueOwners.Get(...)` 再複製六個結論欄位到新建
的 `IssueProfile`，並補迴歸測試
`IssueOwnerAdminServiceTests.Upsert負責人表單_不清空既有機房結論`。

瀏覽器體檢也抓到一處純前端 bug：`issue-owners.js` 的 `openModal()` 有一行
寫死的舊文字「編輯問題負責人／新增問題負責人規則」，蓋掉了 cshtml 已經改
過的標題——只有實際點開 modal 讀畫面文字才抓得到，程式碼審閱看不出來
（cshtml 的預設標題與 JS 的執行期覆寫互相獨立，各自看都「正確」）。

真實 dev SQLite 端到端瀏覽器驗證：問題檔案頁新增／編輯／設定結論／解除結論
／刪除全流程；統一標記勾選自動套用 → API 200 → 回問題檔案頁確認機房結論
正確落地，證明 F3 的勾選正確驅動 F4 背後的同一個 `SetConclusion`。

commit `a71088e`，2023 測試綠。

### 批次F5（確認免動工，無程式碼變更）

比對 `IssueCaseCoordinator.AttachNewDay` 的 fleet 套用寫入
（`ActorId=null, ActorAccount=string.Empty, Note="〔機房結論〕"+結論原因`）
與既有、已上線的案件套用寫入（`HandlingActions.CaseAttach`，同樣
`ActorId=null, ActorAccount=string.Empty`）——形狀完全一致。詳情頁
`record-detail.js` 的 `issueHistoryEntryItem` 對 `entry.note` 是通用渲染
（沒有依 `Action` 分支特殊處理），`formatUserName(null, '')` 回傳空字串、
不會顯示「undefined」之類的殘影。既有的 `CaseAttach` 寫入已經在既有功能
中驗證過能正確顯示，`FleetApply` 走同一條渲染路徑，屬程式碼結構上的必然
等價，不需要重新開瀏覽器逐項核對。維持規劃原文「不另做徽章」的判斷。

### 批次F 全案結案摘要

F1~F5 全部完成。F1/F2 commit（見批次F1+F2 段），F3/F4 commit `a71088e`，F5
確認免動工。核心設計（IssueProfile 承載 fleet 結論、三層優先序、共用
SetConclusion/ClearConclusion 服務方法）與 §2 決策一原文一致，無偏離。
體檢揪出一個後端真 bug（Upsert 清空既有結論）與一個前端真 bug（modal 標題
殘留舊文字），皆已修正並補測試／重新驗證。2023 測試綠。

下一步：批次G（統計強化：基準線／PriorityScore／HostTier）。

---

### 批次G2：主機分級（完成）

`WebHost.Tier`（core/standard/test，預設 standard）：blob（`lf_hosts`）零遷移，
`NormalizeTier` 同 `NormalizeOs` 慣例。主機頁單台編輯下拉＋批次設定分級（批次
走既有 `IHostStore.MutateBatch`，同 `SetGroupsBatch` 一次寫入的理由，不逐台
`Upsert`）；NetIQ 單筆／批次登錄與掃描精靈套用（`NetiqImportApplier.Apply`
新增 `tier` 參數）皆為選填欄，只套用在本次**新增**的主機——與既有 `os` 參數
同一原則。**與規劃原文「NetIQ／CSV 匯入選填欄」的落地方式不同**：hosts.csv
已於回饋十一輪 §2a 退役，現存的匯入路徑只剩 NetIQ 掃描精靈（`
NetiqImportApplier`）與單筆/批次登錄（`NetiqHostService`），沒有獨立的 CSV
匯入器可掛；`OwnerCsvImporter` 職責僅限負責人，不是合適的掛載點。主機清單與
詳情頁顯示分級徽章。

**體檢揪出真 bug**：`HostStore.Upsert` 對「已存在」主機的逐欄複製分支漏抄
`Tier`——新主機因為走 `Add` 分支（直接用傳入物件）不會踩到，只有編輯既有
主機才會現形。這是本輪第二次踩到同一個 bug class（批次F4 的
`IssueOwnerAdminService.Upsert` 也是逐欄複製漏抄新欄位）：只用全新物件寫的
單元測試測不出來，只有實際在瀏覽器對既有主機儲存才抓到——之後新增欄位時，
任何「取出既有物件→逐欄複製到新物件」的 Upsert 分支都要優先檢查這一類。
已修正 `HostStore.Upsert` 與測試替身 `FakeHostStore.Upsert`（兩者當初一起
漏，這次也一起補），並補迴歸測試專門釘住「編輯既有主機」這條路徑（不能只測
新增）。

真實 dev SQLite 端到端瀏覽器驗證：單台編輯分級、批次設定分級（三台一次套
用）、主機清單/詳情頁徽章正確顯示、NetIQ 掃描精靈分級欄位存在且選項正確。
commit `94d8169`，2035 測試綠。

下一步：批次G1（機房級基準線）。

---

### 批次G1+G4：機房級基準線＋fleet 首見呈現（完成）

`IIssueAggregateQuery.DailyHostCounts` 一句 GROUP BY 取代整批載入，只查當頁組
（排行結果的問題集合，與 `LatestOccurrences` 同一個規模假設），不是整份表。
`IssueBaselineCalculator`（Web 層純函式，無 I/O）依 (Source,EventId) 分組：
基準期固定「查詢期間終點（`to`）往前 30 天」——與批次C「不另外抓一次真實
時鐘」同一原則，這裡的錨點也是呼叫端傳入的 `to`，不是 `DateTime.Today`；
基準＝出現日台數中位數，偏離倍數＝最近出現日台數 ÷ 基準；出現不足 3 天
（規劃定案 N=3）視為新問題、無基準。`IssueRankingBuilder`（儀表板/報表共用）
與 `RecordListQueryService.SearchByIssue`（依問題視角）共用同一份計算，三頁
「vs 基準」數字因此必然一致——與既有 `IssueRankingBuilder` 抽出共用投影同一
個理由。

G4：`IIssueAggregateQuery.FirstSeenFor` 讀 `lf_issue_first_seen`（批次B落地、
本輪才有消費端）。三個消費頁新增「首見（機房）」欄；原「涵蓋範圍」欄改標
「本期首見」並簡化為只顯示首見單一日期——**與規劃原文字面略有差異**：不再
顯示「首見~最近出現」的範圍字串，範圍所需的「還在不在發生」資訊已由既有的
「距今天數」提示（如「16 天前」／「昨日仍在發生」）帶出，兩者同時保留會語意
重複。CSV 匯出（依問題視角）同步補上機房首見／基準台數／偏離倍數三欄，用
原始數值而非預先格式化字串——CSV 是給人貼進試算表排序用的。

前端三頁（dashboard.js／reports.js／records.js）的「vs 基準」文字措辭收斂進
`core/format.js` 的 `issueBaselineText`，不三處各寫一份四捨五入規則。

真實 dev SQLite 端到端瀏覽器驗證：儀表板重點問題卡、報表問題排行（圖表資料
表）、依問題視角三處欄位與數字互相一致；CSV 匯出含新欄位（基準台數／偏離
倍數在無基準時正確留空，不是印出 null 字樣）。

新增 12 個單元測試（`IssueBaselineCalculator` 純函式 7 個＋`IssueRankingBuilder`
端到端 3 個，含中位數奇偶筆數邊界／Source 大小寫正規化＋`SearchByIssue`
端到端 2 個），commit `b6c0ae1`，2047 測試綠。

下一步：批次G3（PriorityScore 最小版，依賴 G1/G2/G4 已完成的資料）。
