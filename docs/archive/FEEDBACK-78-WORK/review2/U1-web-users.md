# U1 網頁使用者角度體檢（唯讀，讀碼為主）

範圍：`git diff 1e304931..HEAD`（HEAD=3103c878）。已先讀 R1～R3，下列不重複 R1～R3 已列項目。未改任何 repo 檔案、未新增探針專案（所有結論來自讀碼，未用探針重現）。

## 結論
從處理人、管理者、受限使用者、郵件收件人四種角度逐條追碼，**沒有找到高或中嚴重度的新缺陷**。下列為低嚴重度的新觀察（皆標明證據強度）；R3 的 F1（HashSet 內嵌常值放大 SQL）與 F2（趨勢分段 IN 清單過大）我獨立讀碼後同意，建議優先處理，不再重述。

## 發現

### N1｜低（推論）｜分段後的類型趨勢報表每段各自重新套用一次可見範圍，往返與快取鍵成本與段數成正比
- 情境：管理者開「報表 > 問題類型趨勢」，期間 366 天。
- 預期：改動目的是降記憶體尖峰，行為與整期一次載入相同。
- 實際：`ReportCategoryTrendService.cs:73-77` 迴圈內每段呼叫一次 `_repository.QueryLightweight`；`RecordRepository.cs:92-96`（QueryLightweight）每次都重跑 `ApplyVisibility`→`VisibleHostKeys()`（有每請求快取，只算一次）與 `filter.Hosts` 以 3,000 台 id 組成的 IN 條件，因此 53 段＝53 次含 3,000 個 id 的 IN（SQL Server 上為內嵌常值，見 R3 F1/F2）。結果數字正確，只是 SQL 文字與計畫無法重用；快取命中時不受影響（鍵在 `Get` 已算出）。
- 已重現：否。嚴重度：低（效能，非正確性）。
- 建議：與 R3 F2 同一修法（`LoadLightweightIssues` 改 List 參數並分塊），或段長依可見主機數動態決定。

### N2｜低｜`RunMonitorService.RunsByHostDay` 對 `BatchRun.HostName` 為 null 的列行為與舊版相同，但文件未載明
- 情境：執行紀錄中存在 HostName 為 null 的列（舊資料）。
- 實際：`RunMonitorService.cs` 的 `KeyComparer.GetHashCode` 以 `k.Host ?? ""` 處理 null、`Equals` 以 `string.Equals` 處理，查找端傳入的是 `host.HostName`；舊版 `string.Equals(r.HostName, hostName, OrdinalIgnoreCase)` 對 null 也回 false/true 的結果相同，未發現行為差異。列為「已確認一致」的邊界，不需處理。
- 嚴重度：無（僅註記）。

### N3｜低（設計取捨，已在規劃載明）｜重試退避讓 SMTP 恢復後的補寄延遲最長 30 分鐘
- 情境：收件人視角。SMTP 中斷 20 分鐘後恢復，待送的即時通知要等到下一次允許重試（1→2→4→8→16→30 分鐘）才寄出；改動前每分鐘重試。
- 實際：`MailNotificationService.cs` 的 `RetryPendingUrgentAsync` 退避段（`_retryNextUtc`／`_retryInterval`）。取數結束入口不受退避影響（`NotifyAfterRunAsync` 成功後把間隔重設為 1 分鐘）；設定版次改變、有任何進度也回到 1 分鐘。與 PLAN 0-4／0-A9 一致，屬已同意的行為差異。
- 已重現：否。嚴重度：低。建議：不改；若要保險，可在 WEB-SPEC 郵件節註明「SMTP 長時間失敗後恢復，補寄最慢延遲約 30 分鐘」，讓維運者有預期。

## 檢查過、確認沒問題的項目（簡短）
1. **快取鍵授權維度**：`VisibleHostKeyPart.Of` 只對 `FrozenSet<long>` 以物件參考記憶（`VisibleHostKeyPart.cs:25-29`），其餘型別每次重算；`VisibilityService.cs` 全部三條路徑（ViewAll／空集合／一般）都回 `FrozenSet`，輸出字串與舊的「排序後逗號串接」逐字相同（空集合為空字串，同舊）。不同使用者各有各的 `_cached`（Scoped），不會共用到別人的片段。
2. **`GetVisibleHostIds()` 型別**：`git grep` 無任何 `as/is/強制轉型 HashSet<long>、ISet<long>`；呼叫端只做 `Contains/Count/Where/ToHashSet/ToArray`。`GetVisibleHostIds(snapshot)` 多載仍回新建的 FrozenSet。
3. **儀表板鍵改用 `GetVisibleHostIds()`**（`DashboardService.cs:89`）：`GetVisibleHosts()` 是 `_hostSnapshot.Hosts.Where(visible.Contains)`，兩者同源同快照，鍵字串相同；`BuildSummary` 仍用 `visibleHosts`，鍵與內容沒有分岔。
4. **`CountLightweightAtLeast` 下界**：`RecordRepository.cs:98-109` 在副本上套 `ApplyVisibility`＋`TryApplyDayRiskVisibility`（空交集回 0，不下推空集合），`EfAnalysisRecordStore.cs`（CountLightweightAtLeast）只計 `host_id != 0 && 在清單內` 的列，帶問題層級條件回 null；`QueryLightweight` 後續的 `HostMatcher`（PK 優先）與 `RecordFilterMatcher`（日期／風險／同樣條件）不會丟掉 SQL 已通過的列，`ApplySeverityVisibility` 只改 TopIssues 不丟紀錄，所以「計數 ≤ 實際載入數」成立（受限使用者、日風險顯示設定、嚴重度全站隱藏三種情形都走同一咽喉）。計數不修改呼叫端 filter，閾值比較（`>`）與訊息與舊版相同。
5. **`CanReduceOccurrencesOnServer`／`NoMergedHostsInScope`／`HasDuplicateHostDay`**：不受限時看 `AnyMerged`（由完整主機清單算出，含墓碑），受限時對展開別名後的每個 id 檢查存活主機是否等於自己，任何一台被合併即退回舊路徑；來源鍵未回填時 `LatestOccurrences` 退回舊路徑；`AggregateByDate` 以同一識別同日兩列（含 host_id=0 的多列）退回舊路徑，結果與舊路徑同值（host_id=0 多列在舊路徑本來就併成一台）。每日計數、關聯日、`CategoriesByDate`（先取原始最大等級再正規化）與舊路徑等價。
6. **`AggregateHostTotals`**：母體條件逐條與 `Aggregate` 相同（含 `MailIssueDigest` 傳入的 visibleSeverities／riskLevels），主機數取存活主機去重，與 `SurvivingHostStats` 一致；`IssueRankingBuilder`、`MailIssueDigest` 對 `previous` 只讀 `HostCount`／`TotalCount`。
7. **報表兩期 KPI**：`CountRiskDays` 的 `DataIncomplete || SecurityLogAvailable == false` 對 null 的語意與舊記憶體版相同；受影響主機仍以存活主機去重；空集合回 0。
8. **報表類型趨勢分段**：`from/to` 在 `Get` 已 `.Date` 並確保 `from<=to`；每段 `[start, min(start+6, to)]` 連續不重疊；1 天期間只跑一段、366 天約 53 段，末段以 `to` 截斷；去重鍵含日期所以逐段重建 `seen` 等價；靜音以每筆日期判斷，跨段不受影響；跨月只影響 `ReportBucketing` 桶，與分段無關；全空時走到底的輸出（`Rows` 空、`CellMax=0`）與舊的提早回傳相同。
9. **執行總表**：`RunsByHostDay` 鍵（主機名不分大小寫＋`StartedAt.ToString("yyyy-MM-dd")`）與舊篩選相同，清單依開始時間由新到舊且穩定排序，回傳的共享清單呼叫端只讀（`LocalRuns` 等皆 `Where` 產生新清單）；展開某日 `BuildDayRows` 在「整體工作流」排序時以 `GetMany` 批次讀取，鍵以 `Date.Date` 正規化，缺列＝舊版 `Get` 回 null；狀態篩選、其他排序、換頁仍只讀當頁工作流，未改。
10. **排程頁 run-preview**：`QueryDayStatusLightweight` 與 `QueryLightweight` 同一個 `ApplyPushableFilters`、同一套 `HostMatcher`／`RecordFilterMatcher`／排序；`NeedsBackfill` 只讀 `LogSource／AiPending／AiAnalyzed／RiskLevel／LatestNetiqAttemptStatus`，其中前四個在投影內，`LatestNetiqAttemptStatus` 兩條路徑都未填，行為相同；帶問題層級條件時退回完整版。
11. **處理狀態查找表**：`HandlingIndex`、`OccurrenceStatusResolver` 的索引比較器 `Equals`/`GetHashCode` 一致，`TryAdd` 保留清單第一筆＝舊 `FirstOrDefault`；null 主機名在建表時略過，`DailyAnalysisRecord.Host` 預設空字串，查找端不會傳 null。
12. **郵件收件人角度**（補充 R1）：停用郵件／即時通知時 `RetryPendingUrgentAsync` 現在直接 return、`NotifyAfterRunCoreAsync` 的停用分支只 `ClosePlans()` 而不 supersede，所以停用→再啟用不會漏寄（R1 F1 已在 HEAD 修正）；`unresolvedHostDays` 讓取不到版本的主機日這趟不被 supersede（R1 F2 已修）；摘要與即時通知的 `LegacyKeySent` 在升級後避免重寄。`SupersedeUrgentOutbox` 不改 `UpdatedAtUtc`，維護頁不會被擠版。
13. **`ScheduleOptionsStore(cached:true)`**：快取原始字串、命中仍各自反序列化；每次讀取先探測版本，網頁與批次跨行程寫入立即可見。
14. **`HostStore.Get/FindByName` 索引**：綁在快照物件參考上，版本變動即重建；重複編號/名稱取第一筆，與舊 `FirstOrDefault` 相同；`null` 名稱走線性路徑。

## 沒有把握或未驗證
- 無法連 SQL Server：所有 SQL 形狀與定序結論（含 R3 F1/F2/F5）僅限讀碼與 R3 的 `ToQueryString` 探針；N1 為推論。
- 未重跑測試、未新增探針專案；以上「確認沒問題」皆為讀碼比對（含 R3 已跑的 231 筆定向測試作為旁證），沒有自行新增資料形狀（合併主機＋同主機日兩列＋來源鍵未回填的組合）探針。
