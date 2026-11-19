# R2 夜間取數與 PRTG 唯讀體檢（origin/dev..HEAD）

方法：逐檔讀 diff 與周邊程式（未執行 dotnet test、未寫探針；專案檔未動）。所有「是否重現」欄皆為「否」，以程式推演為主；沒有把握者標「推論」。

## 發現

### 1. 中｜PrtgRiskProjectionStore.cs:48-68、PrtgRiskReviewHostedService.cs:25、PrtgRiskReviewController.cs:41
- 失敗情境：批次只把列標成 `unavailable`（`DetailPruned` 或 `ContentJson` 無法解析，`record == null`）或由 `pending/unavailable` 改回 `current` 時，`contentRewritten` 仍為 false，資料版本戳不推進。但 `RiskReviewStatus` 本身有消費端：`DashboardService.cs:160`（`PendingRiskReviewDays` 計數，整包走 `SummaryCache`，鍵含版本戳）、`EfAnalysisRecordStore.cs:2146`（`unavailable` 會讓讀出的紀錄帶 `RiskReview`）、控制器 `PrtgRiskReviewController.cs:30`。升級後第一輪重評時，超過原始事件保留期的列多半是 `DetailPruned`，會大量只改狀態不改內容 → 儀表板「待修訂」數與列表標記在 TTL 內與資料庫不一致。版本戳只被「內容有改寫」帶動，而被讀取的欄位是狀態欄。
- 是否重現：否（推論，TTL 短，影響限於 TTL 期間）。
- 建議：把「讀取端可見的欄位有變」當成推進條件：`RiskReviewStatus` 與批次前不同、或內容改寫，任一成立就 `rewritten++`（只更新 `RiskProjectionVersion` 且狀態沒變才不推進）；並補「只轉成 unavailable 的批次要推進」測試。

### 2. 低｜EfAnalysisRecordStore.cs:270-340（UpsertFirstSeen）
- 失敗情境（SQL Server，推論）：批次多列 INSERT／多條件 UPDATE 在兩台 Sentinel 並行、鍵順序不同（`pairs` 依 TopIssues 順序）時可能互相等鎖而死結（1205）。`StorageBackend.cs:165` 啟用 `EnableRetryOnFailure(5)`，原始 SQL 會被執行策略重試；只有連續 5 次仍失敗才擲 `RetryLimitExceededException`，它不在 `batchEx is DbUpdateException or SqliteException or SqlException` 篩選內，會穿出 `UpsertFirstSeen`，讓已提交主交易的主機日被呼叫端判失敗（該處註解明說不可如此）。機率很低（重試中第二次通常變成 2627 撞鍵，會被攔下降級）。另外 `toUpdate` 的 UPDATE 沒有 try/catch，這點與改前相同（改前單句 UPDATE 也沒包）。
- 是否重現：否（推論）。
- 建議：批次 INSERT 與 UPDATE 前對 `pairs` 依 (source_key, event_id) 排序，鎖順序一致即不會對鎖；順手把 `toUpdate` 與 catch 篩選補上 `RetryLimitExceededException`／一般例外只記警告（首見日是輔助資料）。

### 3. 低｜EfAnalysisRecordStore.cs:283-293
- 失敗情境（推論）：一次查詢以 `keys.Contains(f.SourceKey)` 取出該 source_key 的所有 event_id 列，再在記憶體比對；Windows 每個來源可有數百個 event_id，一個主機日牽涉 10～20 個來源就是上千列。3000 台×120 天回補等於每個主機日多讀千列（改前是每問題一次 EXISTS 的點查，往返多但每次極輕）。批次 2a 的度量是「指令數」，沒量「讀回列數」。Linux 的 EventId 恆 0，不受影響。
- 是否重現：否。
- 建議：查詢加上 `eventIds.Contains(f.EventId)` 縮小候選（或改用鍵元組 join）；回補類情境補一個「讀回列數」度量再決定。

### 4. 低｜PrtgSnapshotDiagnosticsStore.cs:1
- 檔首變成雙 BOM（`ef bb bf ef bb bf using`）。多出的 U+FEFF 在 Roslyn 目前可編譯，但屬無意義的檔案污染，工具鏈（部分 diff／grep／檢查）可能誤判。
- 是否重現：是（xxd 直接可見）。
- 建議：移除多出的一組 BOM。

### 5. 低｜過度設計／只為測試存在的正式碼
- `PrtgRiskReviewHostedService.cs`：建構子參數 `TimeSpan? startupDelay` 只供測試縮短延遲，正式 DI 永遠不傳。
- `HostStore.LookupIndexBuilds`、`PrtgStateChangeWindow.ResidentDays`、`PrtgSnapshotHostedService.BusinessScopeComputeCount`：只有測試讀取的計數器（程式註解已自述「測試用」）。
- `PrtgRiskProjectionStore.RunBatch(...)` 轉接多載：正式碼已無呼叫端（兩處都改用 `RunBatchDetailed`），只剩測試或相容用途。
- `PrtgStateChangeWindow` 兩個建構子，`Func` 版本只為測試替身。
- `HostLookupIndex` 同時建 ById 與 ByName 兩個字典；只呼叫 `Get` 的單次使用者（每個新 `HostStore` 實例各有一份索引）也會付出兩份建構成本。
- 是否重現：不適用。建議：計數器與 `startupDelay` 可保留但維持 `internal`；`RunBatch` 轉接若無用途就刪；索引可延遲建立各自的字典。皆不影響正確性。

## 已推演、未發現問題的項目（含理由）

1. 狀態變更滑動窗口（`PrtgStateChangeWindow`，PrtgDailyPipeline.cs:1353-、CalibrationService.cs:2154-）：每日 `day = days[i].Date`，窗口取 [day-1, day) 與 [day, day+1)，聯集與改前 `[day-1, day+1)` 完全相同；各日已排序，合併用穩定排序，跨日同 sensor 不可能同時間；跨午夜持續 Down 的前導列由前一天提供；監看政策空集合＝不限、非空下推 `SensorObjid IN`，與改前記憶體篩選等價（校準端 `selected` 外層已保證非空，不會誤變成不限）。評估器 `PrtgRuleEvaluator` 只讀 `SensorObjid`／`ChangedAt`／`Status` 三欄；`GetStateChangesForRuleEvaluation` 的結果只用在這兩處，沒有被拿去他用。降序、升序、不連續日期都只多讀不錯讀；記憶體最多留兩天。新增 `ThenBy(Id)` 只讓原本不確定的同毫秒順序變確定。
2. `PrtgSensorLookups`、`ResolveOkHostMapDates`、`allSensors` 沿用：視窗起迄與 `AssessPage` 內部計算一致；僅在跨午夜時 `today` 與逐頁重算有一頁差，對應日期若在午夜後才新增也因不在 okDates 內被排除，結果等同改前最壞情形。感測器鏡像沿用的前提（本趟評估期間不再寫入鏡像）成立：取數與清除都在 line 397 之前。
3. `PrtgScopeRevisionReader` 政策指紋快取：版本在讀內容前取得，競爭時至多下個檢查點重算；整段在 `_gate` 內，只有字串與長整數欄位；`PrtgSnapshotHostedService` 單一實例共用安全，無集合會累積。
4. `PrtgOperationScope` 沿用範圍字串：短路求值保留（設定已變時不讀範圍），`Publish` 原本就會自己讀。停止／設定變更的取消語意不變。
5. 業務範圍快取（`ComputeBusinessScopeDevices`）：鍵含主機清單版本、主機對應修訂（`ReplaceHostMapForDate`、刪除路徑皆遞增）與完整範圍版本（人工對應走 `WriteScopeChange` 遞增）；每個 tick 開頭清除，兩個 tick 交錯也只會多算。僅剩 `_addressResolver` 的 DNS 結果不在鍵內，但被每週期清除限制在一輪內。
6. 診斷輪末寫入：`finally` 涵蓋例外與取消；「嘗試／成功」計數與改前逐批寫入語意相同；取消判斷 `OCE when (batchToken.IsCancellationRequested)`、`OCE when (!ct.IsCancellationRequested && roundDeadline.IsCancellationRequested)` 符合 CLAUDE.md 規則。已知差異（輪中途看不到本輪嘗試數、崩潰丟失）已在程式註解載明。
7. `PrtgScopePurgeStore.ReadCandidatePage` 候選為空：游標直接到水位線，與改前最後一頁回傳 `watermark` 同值，其餘守門（範圍柵欄、授權柵欄、`RecordBaselineInTransaction`）仍在同交易內，未繞過。
8. `HostStore` 索引：以快照物件參考為鍵，版本變動時 `ReadSnapshot` 回新物件即重建；`TryAdd` 保留第一筆，與 `FirstOrDefault` 同；`FindByName(null)` 退回線性。多執行緒最多重複建一次。`PrtgHostMapRefresher` 共用 `HostStore`：`PrtgHostMapper` 只讀不改元素。
9. `PerRunIssueOwnerStore`：每趟新建、鎖內複製清單、協調器只讀元素；中途網頁端修改的結論要到下一趟生效，已在註解載明為可接受差異。批次 `AppendLogs`：LogId 續號探測回看 100 列，SQL Server MERGE 重排只在單批 42 列內，仍涵蓋；失敗時與改前差異只在「迴圈中途例外時前面的歷程不再先寫入」，主交易本來就在其後。
10. `PlansByMissingDate`：每個日期內順序等於輸入順序，重複日期去重，與逐日 `Where(Contains)` 同。
11. `QueryDayStatusLightweight`／`CountLightweightAtLeast`：有問題層級條件時退回原路徑；`RecordFilterMatcher` 其餘條件只用日期與風險等級（皆有投影）；計數只算 `host_id != 0` 的列，確為下界；可見性（嚴重度）只裁 `TopIssues` 不裁紀錄，不影響下界。
12. `UpsertFirstSeen` 其他面向：參數量（INSERT 每段 100 列＝400、UPDATE 每段 201、查詢每段 500 個鍵）都在 SQL Server 2100 與 SQLite 上限內；唯一鍵競態走整批回復後逐筆降級，撞鍵再走「較早才更新」；`SourceKeyOf` 大寫化後以序數比對，與 SQL Server 不分大小寫排序規則不一致時只會多走一次降級，不會漏寫或覆蓋較早日期。
