# R3 互動查詢、報表與快取體檢（唯讀）

範圍：`git diff origin/dev..HEAD`（HEAD=4f367f23）。未修改專案檔、未 commit。
探針放在 scratchpad/review/probe（獨立專案，只引用 Core）。另跑定向測試 231 筆全綠
（IssueAggregate／RecordListBatch5b／ReportCategoryTrendSegment／RunDayDetailBatch5b／RunPreviewBatch5b／Batch3b／Batch2c／VisibleHostKey／FirstSeenBatch）。

注意：體檢期間工作樹出現與本範圍無關的未提交修改（PrtgRiskProjectionStore.cs、PrtgRiskReviewController.cs、PrtgRiskReviewHostedService.cs、PrtgBatch2bTests.cs），不是本次體檢造成的，已排除在外。

## 發現

### F1 ｜ 中 ｜ EfIssueAggregateQuery.cs:402（ExpandToAliasIds）、:652-690（BuildLatestOccurrenceQueries）、RecordRepository.cs:277（註解）
- 失敗情境：可見主機 3,000 台的使用者（含全站檢視者）開依問題視角展開問題，`LatestOccurrencesOnServer` 的查詢把 `q`（內含 `expanded.Contains(x.HostId)`）放進外層與分組子查詢兩處。`expanded` 宣告型別是 `HashSet<long>`，EF Core 8 對 `HashSet<T>.Contains` 不走 OPENJSON 參數而是把每個 id 內嵌成 `CAST(n AS bigint)` 常值。結果是單一 SQL 約 130 KB、每組可見範圍一份獨立文字、無法重用計畫；改動前只有約 65 KB。規模到 6,000 台時一句 SQL 內有 12,000 個常值，有觸發 SQL Server「expression services limit」的風險（此點為推論，沒有實際 SQL Server 可連）。
- 實際重現：是（探針 ToQueryString，SQL Server provider）。HashSet 宣告：長度 65,290、無 OPENJSON、無參數。List 宣告：14,421、OPENJSON 單一參數。latest join 形狀：130,613、無 OPENJSON。
- 連帶：RecordRepository.cs:277 註解寫「IN 清單在 EF 8 是單一 JSON 參數」，對 HashSet 宣告型別不成立（對 `List<long>`／`IReadOnlyCollection<long>` 才成立）。同樣的內嵌也出現在 AggregateByDate 查詢端路徑（recordsQuery 被 HasDuplicateHostDay、CategoriesByDate 的 allowedRecordIds、BuildDayRiskCountsQuery 各嵌一次）。內嵌行為本身是改動前就有的，但新查詢把它放大成倍數。
- 建議：`ExpandToAliasIds` 回傳 `List<long>`（或 `long[]`），EF 就會走 OPENJSON 單一參數；順便把註解改成與事實相符。

### F2 ｜ 中（推論） ｜ ReportCategoryTrendService.cs:19（SegmentDays=7）、EfAnalysisRecordStore.cs:2067（LoadLightweightIssues）
- 失敗情境：類型趨勢報表 3,000 台、不勾日風險限制，每個 7 天段約 21,000 個主機日，`LoadLightweightIssues(ctx, HashSet<long> recordIds)` 一次把 21,000 個 record_id 以常值內嵌進單一 `IN`（同 F1 的 HashSet 行為，約 500 KB SQL 文字，且沒有分塊）。分段把整期 1M 級降到 21k 級，但仍在 SQL Server 單一 IN 清單容易出事的範圍。同一請求在 366 天期間會發出約 53 段 × 2 道查詢＝約 106 趟往返（改動前 2 趟）；段與段之間也沒有一致性快照（背景寫入中途插入新日不會跨段，因為以日期為界，所以只影響整期是否含新日，不影響各格數字的正確性）。
- 實際重現：否（沒有 SQL Server；已用探針證實 HashSet 會內嵌常值，21k 筆的上限行為是推論）。
- 建議：`LoadLightweightIssues` 改成 `List<long>` 參數並分塊（例如 2,000 一塊），或依可見主機數動態決定 SegmentDays（段內主機日數控制在約 5,000 以下）。

### F3 ｜ 低 ｜ ReportService.cs:61、ReportCategoryTrendService.cs:47、ReportEffectivenessService.cs:61、VisibleHostKeyPart.cs:7-13
- 失敗情境：`VisibleHostKeyPart` 的說明寫「儀表板一次請求要組 3 次、報表 2 次」，但報表三支的快取鍵仍用 `visibleHosts.Select(h => h.HostId).ToList()`（List 型別，不是 FrozenSet），記憶化對它們不生效；每次請求（含命中快取時）仍對 3,000 個 id 排序並串接兩次以上。只有 DashboardService.cs:89 真的受益。屬效能收益沒落到宣稱的範圍，不是正確性問題；快取鍵內容與改動前逐字相同。
- 實際重現：否（程式碼閱讀）。
- 建議：三支報表改傳 `_visibility.GetVisibleHostIds()`（比照儀表板），或把說明改成只涵蓋儀表板。

### F4 ｜ 低 ｜ RecordListQueryService.cs:67-71、:1029-1033；HostStore.cs:83-93（過度設計）
- 失敗情境：沒有出錯，是只為測試存在的正式碼。
  - `HandlingProbeCount`／`CountProbes` 在 `HandlingIndex` 建表與每次 `Find` 都做一次 `Interlocked.Add`，成千上萬次呼叫都付這個成本，正式碼沒有任何消費端（自己的註解也寫「internal 僅供測試觀察」）。
  - 建構子多一個 `int? handlingFilterCandidateLimit` 參數，只為測試避免建五萬筆資料。
  - `HostStore.LookupIndexBuilds` 同理（成本很小）。
- 實際重現：不適用。
- 建議：保留計數器的話改成以替身／可注入的 `Action<int>` 只在測試組裝時帶入（正式組裝傳 null 即不呼叫）；或改用可觀察結果（例如以 Stopwatch／規模斷言）取代計數器。專案紅線是「有設定無行為」，此項是「有計數無消費端」，建議比照處理。

### F5 ｜ 低（推論） ｜ EfIssueAggregateQuery.cs:652-690（BuildLatestOccurrenceQueries）
- 失敗情境：SQL Server 預設定序不分大小寫且比較時忽略尾端空白。舊路徑在記憶體以序數比較分組；新路徑在查詢端 `GROUP BY (host, log_name, source_key, event_id, entry_type, event_key)` 與 join 用資料庫比較。同一主機同一問題若有兩個只差大小寫的 event_key（例如 PRTG／規則 Id 大小寫不一致），SQL 端會併成一組、只留最大日期那組的列，另一個變體的出現點消失；舊路徑是兩個出現點。SQLite（BINARY 定序）測不出來。ActionableOccurrences 既有 SQL 分組已有同樣前提，所以不是新類型的風險，只是範圍擴大。
- 實際重現：否。
- 建議：確認 event_key／log_name 實務上大小寫穩定即可接受；或在 `GROUP BY`／join 對這兩欄指定 `COLLATE` 為二進位定序（會影響索引使用，需評估）。

### F6 ｜ 低（已知取捨，文件已載明） ｜ EfIssueAggregateQuery.cs:587-596（NoMergedHostsInScope）、AggregateReportKpiPair（:1326）
- 站內只要有任何一台被合併的主機落在可見範圍內，查詢端路徑就整個退回舊路徑；全站檢視者的可見範圍展開別名後必含該墓碑，所以只要站台合併過一次，管理者／全站檢視者永遠走舊路徑，批次 5a／5b 的收益只落在群組授權的受限使用者。FEEDBACK-78-PLAN 批次 5a 備註已載明。
- 兩期 KPI 的往返由 3 變 6（改為常數讀回列數），同樣已載明。
- 建議：不需改動；驗收時別以全站管理者帳號量測，會量不到收益。

## 已逐項推演、未發現問題（含證據）

1. 授權維度／快取跨人共用：`VisibleHostKeyPart` 只對 `FrozenSet<long>` 以物件參考記憶，內容不可變，輸出與舊字串逐字相同；DashboardService.cs:89 的鍵改用 `GetVisibleHostIds()`，與 `GetVisibleHosts()` 同源（同一個 `_cached` 與同一份 `_hostSnapshot`），內容一致。IssueRanking／ActionableSnapshot／NextUnhandledSequence 的改動只換組鍵函式。`ConditionalWeakTable` 不持有集合，不是長駐集合。
2. `GetVisibleHostIds` 回傳 `FrozenSet`：介面型別仍是 `IReadOnlySet<long>`，全部呼叫端（git grep 逐一看過：PrtgBackfillAdmin、PrtgScopeEstimate:498、PrtgValueReadiness、DismissedIssue、HandlingHistory、RecordListQuery、PermissionChange、WorkOrderQuery 等）只做 `Contains`／`Count`／列舉／`ToHashSet`，沒有轉型成 `HashSet`／`ISet`（git grep 無命中）。EF 翻譯探針：SQL Server `ToQueryString` 對 `IReadOnlySet`、`IReadOnlyCollection`、`FrozenSet` 宣告型別都翻得出來（前者內嵌常值、後者 OPENJSON 參數）；SQLite 實際執行三種宣告型別皆成功。換成 FrozenSet 不改變翻譯形狀（形狀由宣告型別決定，與執行期型別無關）。
3. `CountLightweightAtLeast`：RecordRepository.cs:98-109 複製了 `RecordQueryFilter` 的全部欄位（共 8 個，與類別定義逐一核對），走同一個 `ApplyVisibility`／`TryApplyDayRiskVisibility`；store 端只計 `host_id != 0 且在清單內` 的列，與 `HostMatcher`（PK 優先、host_id=0 才比名稱）一致，下界成立；帶問題層級條件回 null 退回載入後檢查。判斷與改動前的差別只在「更早丟例外」，閾值比較與錯誤訊息相同。唯一的額外成本是低於上限時多一道 COUNT。
4. `QueryDayStatusLightweight`：`RecordFilterMatcher.Matches` 在沒有問題層級條件時只看日期與風險等級，投影欄位足夠；`NeedsBackfill` 用到的欄位（LogSource、AiPending、AiAnalyzed、RiskLevel）都在投影內。呼叫端 ScheduleController 用的是不帶可見範圍的 `IAnalysisRecordQuery`，與改動前相同。
5. AggregateByDate 查詢端路徑：條件（無合併主機＋同識別同日無重複列）成立時與舊路徑等價；host_id=0 的舊列在不受限查詢下會讓 `HasDuplicateHostDay` 為真而退回舊路徑（行為不變）。風險類型清單的母體在兩條路徑都以「已套全部篩選的日紀錄」收斂。
6. LatestOccurrences 查詢端路徑：IssueKey 的來源名仍在記憶體取最小值（避開資料庫定序），同一事件編號分屬兩來源時由記憶體精確配對收尾；靜音、可見嚴重度、日風險、可見主機等母體條件重用 `IssueRows`，與其他入口一致。
7. `AggregateHostTotals`：母體條件與 `Aggregate` 逐條相同；分組鍵與 `GroupIssueAggregates` 相同；主機數沿用存活主機去重；前一期不套 visibleSeverities／riskLevels，與改動前 `Aggregate(...)` 呼叫一致。
8. `HostDayWorkflowStore.GetMany`：鍵格式與單筆 `Key()` 相同；超限、含 NUL、位元組超限一律退回單筆 `Get`（例外相同）；`_workflow == null` 時 `ApplyWorkflow(dto, null)` 本來就是 no-op，省略呼叫等價；SQL Server 以 chunk 500、`List<string>` 參數走 OPENJSON。
9. `RunsByHostDay`／`HandlingIndex`／`OccurrenceStatusResolver` 查找表：比較器的 `Equals`／`GetHashCode` 一致（`IssueSignatureKeyComparer` 兩者分流相同）；同鍵取清單順序第一筆與 `FirstOrDefault` 相同；回傳的共享 List 沒有任何呼叫端修改（逐一檢視 dayRuns 用法）；null 主機名在建表時略過、查找時不會是 null（`DailyAnalysisRecord.Host` 預設空字串）。
10. 報表分段邊界：每段以日期為界、連續不重疊，去重鍵含日期所以逐段重建 `seen` 等價；`from>to` 時迴圈不執行，與舊行為一致。
11. `ScheduleOptionsStore(cached: true)`：快取的是原始字串、命中仍各自反序列化，呼叫端讀改寫不共用實例；每次仍探測版本，跨行程安全。
12. `UpsertFirstSeen` 批次化：參數索引、`first_seen > 本日` 保護、批次 INSERT 撞鍵後逐筆降級都正確；單句 INSERT 原子，SQL Server 400 參數、100 列在上限內。
13. PrtgScopeEstimate 的 `Minutes`：改為兩個下界取大者，`QuotaLowerBoundSeconds(requests<=1)=0`，沒有溢位或負值路徑；僅影響顯示。

## 沒有把握或未能驗證
- 沒有連線的 SQL Server：所有 SQL Server 結論僅限 provider 的 `ToQueryString`（可翻譯性與 SQL 形狀），F2 的上限行為與 F5 的定序行為為推論。
- 合併主機、墓碑、host_id=0、來源鍵未回填、靜音區間等資料形狀，由現有測試（IssueAggregateBatch5a／5b、IssueAggregateHostTotals、EfIssueAggregateQueryReadyParity 等，231 筆定向測試全綠）涵蓋；本次沒有另外新增資料形狀探針。
