# U2 體檢：維運人員與夜間無人值守排程角度（唯讀）

範圍：`git diff 1e304931..HEAD`（HEAD=3103c878）。方法：逐段讀碼並與改動前（1e304931）逐行對照；未新增探針、未修改任何專案檔，`git status` 乾淨。
已先讀 R1～R3 與計畫「實作方自查」，以下不重複已處置項目。所有「是否重現」皆為「否（讀碼）」。

## 結論
沒有找到高、中嚴重度的新缺陷。夜間一趟、背景服務、長期運作三條線，本輪的「沿用／一次寫入／索引」在取消、例外、重跑、回補情境下，最終狀態與改動前一致。
以下 4 項為低嚴重度的脆弱點或殘餘差異，皆有程式碼位置；推論處已標明。

## 發現

### F1 低（推論）｜同一趟內只讀一次的 PRTG 有效對應日期，與逐頁讀取的對應列之間有時間差
- 情境：夜間每日評估跑到磁碟／趨勢分類的分頁迴圈時，管理者在網頁編輯主機 IP 或人工對應，`PrtgHostMapRefresher.TryRefreshToday` 同時重寫今天的對應（`ReplaceHostMapForDate`：整日刪除再寫入）。
- 預期：與改動前相同——每頁各自讀「有效對應日期」與「對應列」，兩者來自同一時刻附近。
- 實際：`ResolveOkHostMapDates` 在迴圈外只算一次（PrtgDailyPipeline.cs:560、:626；PrtgValueAssessment.cs:262），之後每頁的 `GetReadinessMaps` 仍即時讀（PrtgValueAssessment.cs:301-306）並以那份日期集合過濾。若迴圈開始前今天尚無 ok 對應、迴圈中途才寫入，後續頁面會把今天的新對應排除，該頁 sensor 沿用較早對應或判「無對應」；反向（先有後被整日重寫的瞬間）沒有問題，因為集合只含日期。
- 是否重現：否。影響範圍限單趟、單日、並且只在夜間評估與人工改對應重疊的分鐘內；下一趟自動修正。程式註解（PrtgValueAssessment.cs:255-262）已載明「今天以迴圈開始為準」，但沒提到對應列是即時讀。
- 建議：維持現狀即可；若要收斂，把 `GetReadinessMaps` 的讀取一併提到迴圈外（需改 `AssessPage` 簽章），或在註解補一句「對應列與日期集合可能相差一次中途寫入」。

### F2 低｜業務範圍快取回傳可變的 `HashSet<long>`，「呼叫端只讀」只靠註解
- 位置：PrtgSnapshotHostedService.cs:1174（`BusinessScopeCacheEntry.Devices`）、:1192（回傳型別）、:891（`RecentStateChangeScopeContext.BusinessDeviceObjids` 跨 await 保存同一個物件）。
- 情境：同一週期內第二次呼叫（領取前）取得與第一次（對帳前）同一個集合物件；對帳（EfPrtgStore.cs:372-416）、領取（:1086）目前只做 `Contains`／`Count`／`Order()`，我逐一確認沒有修改。
- 風險：日後任何一處加 `UnionWith`／`Remove`，會靜默污染同週期後續使用，且因為版本鍵不變，不會被重算洗掉。本專案對同類情形（`VisibleHostKeyPart`）已改用 `FrozenSet` 以型別保證。
- 是否重現：否（目前無修改路徑，是脆弱點不是現行缺陷）。
- 建議：快取項與回傳型別改成 `IReadOnlySet<long>`（建立時 `ToFrozenSet()`），讓「不可變」由型別保證；`RecentStateChangeScopeContext` 欄位同步改型別。

### F3 低｜`PerRunIssueOwnerStore` 的「元素唯讀」沒有守門，且 `Get` 與 `GetAll` 對同一趟內修改的可見性不一致
- 位置：PerRunIssueOwnerStore.cs:21-27（`GetAll` 回複本清單但元素共用）、:29（`Get` 直通底層）。
- 情境：夜間掛接一趟可長達數小時。`GetAll` 讀一次後整趟沿用（文件已載明為可接受）；但 `Get(source,eventId)` 是即時讀。目前 `IssueCaseCoordinator` 只呼叫 `GetAll`（IssueCaseCoordinator.cs:194），所以沒有不一致。日後若有人在同一個協調器內混用兩者，會在同一趟看到兩種版本。
- 預期／實際：行為與計畫自述一致；差在沒有守門測試鎖定「協調器內只用 `GetAll`、不修改回傳元素」。
- 是否重現：否。
- 建議：在 `PerRunIssueOwnerStore` 的 `Get` 也走快取（`_cached` 內查），或補一條守門測試掃 `IssueCaseCoordinator` 不得呼叫 `_issueProfiles.Get`／不得對 `IssueProfile` 指派屬性。

### F4 低（推論）｜首見日批次寫入遇到舊大小寫鍵時，每趟都會多走一次「整批失敗＋逐筆降級」
- 位置：EfAnalysisRecordStore.cs:283-293（`existing` 以序數比對）、:316-340（批次 INSERT 與降級）。
- 情境：SQL Server 不分大小寫定序下，若資料庫還留有「非大寫」的舊 `source_key` 列（來源鍵回填／重整完成前，或個別殘留）。查詢 `keys.Contains(f.SourceKey)` 會把它撈回來，但 `seenPairs.Contains((row.SourceKey,…))` 以序數比對不到，該鍵被當成「不存在」進 `toInsert`；批次 INSERT 撞 2627，整段（至多 100 列）退回逐筆，每筆再撞鍵、改走「較早才更新」。結果正確（首見日不會被蓋晚），只是該主機日多出一次失敗往返加最多 100 筆單句。
- 是否重現：否（沒有 SQL Server；R2 發現 12 已推演過「只多走一次降級」，此處補充是「殘留舊鍵時每趟都會發生，而非一次性」）。
- 建議：`existing` 組字典與 `seenPairs` 改用 `StringComparer.OrdinalIgnoreCase`（與 SQL Server 定序一致），或確認舊鍵重整完成後資料庫內已不存在非大寫鍵。

## 檢查過、確認沒問題的項目（含證據）

**夜間一趟**
1. NetIQ 計畫階段日期索引（NetiqPipelineService.cs:533、:964-982）：`MissingDates` 全程只讀（grep 全檔無修改），取消時 `RecordUnattempted` 仍用原計畫（:574）；每日 `needing.ToList()` 複本讓後續分批不動共用清單；重複日期以 `Distinct` 去重，日期鍵以 Ticks 比對，與 `List.Contains` 相同。
2. 首見日批次寫入：參數量（INSERT 100 列＝400、UPDATE 201、查詢鍵 500）在 SQL Server 2100／SQLite 上限內；兩台 Sentinel 平行撞鍵時整段 INSERT 原子失敗、逐筆降級、撞鍵走「first_seen > 本日才更新」，早的日期不會被蓋晚；批次 INSERT 以外的例外種類（DbUpdate／Sqlite／SqlClient）與改動前的暴露面相同（改動前 UPDATE 與 `Any()` 同樣未包 try，EfAnalysisRecordStore 舊版第 276-286 行）；`SourceName` 仍經 `ColumnClamp`。
3. 掛接歷程一次寫入（IssueCaseCoordinator.cs:201、:265）：仍在 `SaveMany` 之前寫入，順序不變；迴圈中途例外時改為「前面的歷程不再先寫」，主交易本來就在其後，反而少留孤兒歷程。
4. 空主機日合併寫入（HostDayPostProcessor.cs:254-267、:280）：`expectedCaseKeys` 為空時，意圖與結果兩段的輸入都是空集合；`AttachNewDay` 丟例外時原本 catch 補寫的也是空集合，最終 `CaseState` 同為 Succeeded／（若同一主機日先前已有意圖則同為 Deferred）。
5. PRTG 狀態變更滑動窗口：評估器只讀 `SensorObjid`／`ChangedAt`／`Status`（PrtgRuleEvaluator.cs 全檔 grep 無 `.Message`／`.PrevStatus`／`.Quality`／`.Id`）；範圍 `[day-1,day+1)` 與改動前 `GetStateChanges` 同為前含後不含；`from` 皆為 `.Date`（CalibrationService.cs:2099-2100）；監看政策空集合＝不限，非空才下推且以 `List` 走單一 OPENJSON 參數（EfPrtgStore.cs:2125-2127），不會像 `HashSet` 被內嵌成常值。
6. 感測器鏡像沿用（PrtgDailyPipeline.cs:397-409、:533、:554、:620、:726）：`PrtgValueAssessment.Candidates` 只用 Objid／DeviceObjid／Category／SensorType／SensorName，不用 Status，快照服務在取數階段被 `IsFetchRunningPrtgPhase` 擋住（PrtgSnapshotHostedService.cs:636），評估期間不會寫鏡像；`allSensors` 下游無任何 `Remove`／`Sort`。
7. 規則評估預建對照表（`PrtgSensorLookups`）：每日路徑與校準各自以對應的輸入建一份（校準 magnitude／formal 兩份分別傳入）。同裝置合併改為按裝置分組，anchor 取 sensor 最小者且同值取較早，與改動前穩定排序取第一筆相同。
8. 郵件收尾「不變不寫」：`UpdateIfChanged` 與 `Update` 的鎖、交易、大小檢查相同，列不存在仍建立；工作流 `UpdatedAt` 無讀取端（`HostDayWorkflowState` 僅 HostDayWorkflowState.cs 與 RunMonitorService 引用，無保留期以它判斷；保留期清除只看主機日）；`ReplaceMailPlanIfChanged`＋`CloseMailPlanIfChanged` 在同一個收尾內最終狀態與改動前一致。
9. 中途取消／例外／重跑同一天：郵件記憶只在單次通知呼叫內（區域變數）；重跑同一天產生新 RecordId，舊鍵項目依鍵不在 liveKeys 被標已取代，新鍵另建，無重複寄送；`HostDayWorkflowFingerprint.ForMailContent` 不含 AI／清除旗標，AI 寫回不重寄。

**背景服務**
10. 快照服務：診斷一輪寫一次的 `finally` 涵蓋例外、取消與「視窗未完成」（PrtgSnapshotHostedService.cs:709-783）；`RecordBatchCounts` 的 `Targets` 取最大值、保留期清除、容量檢查與逐批記錄相同；`Volatile.Read` 讀閉包計數正確。檔首 BOM 已修正（xxd 驗證）。
11. 業務範圍沿用：鍵 ＝ 主機清單版本＋主機對應修訂＋完整範圍版本，且版本先於資料讀取；`ReplaceHostMapForDate`、兩處刪除、`WriteScopeChange`（人工對應、IP 排除）都會遞增（EfPrtgStore.cs:987、:1058、:1138、:2563-2650），範圍清除的柵欄鍵清單也含此修訂（PrtgScopePurgeStore.cs:30）。每個 tick 開頭清除（:371、:488），主迴圈串行（:317-338），無並行存取。DNS 解析結果不在鍵內，被每週期清除限在一輪。
12. 歷史弱佐證重評：`startupDelay` 為建構子選用參數，註冊為 `AddHostedService<PrtgRiskReviewHostedService>()`（ServiceCollectionExtensions.cs:586），MS DI 對無法解析的選用參數使用預設值（`TimeSpan?`＝null），唯一建構子，無歧義（推論；計畫稱已以探針確認）。版本戳推進條件（`AwaitingReview` 轉換或內容改寫）經執行策略重試時會重置 `visibleChanged`。
13. 工作流恢復：`TryUpdateForRecovery` 等既有路徑未改；`UpdateIfChanged` 只用於 `RecordMail`／`ReplaceMailPlanIfChanged`／`CloseMailPlanIfChanged`；版本不符時為 no-op，不會覆蓋恢復寫入的狀態。
14. AI 排程：本輪 diff 未觸及 AI 排程、`SelectNextBatch`、`BlockingIdleReason`，無新風險。

**長期運作（singleton／static 新增狀態）**
15. `HostStore` 查找索引（HostStore.cs:60-108）：以快照物件參考為鍵，`HostStore` 一律 `cached: true`（HostStore.cs:11），舊版本的快照與索引在版本變動後被新物件取代，不累積；`_lookupIndex` volatile 發佈、字典建構完成後才可見；重複主機編號／名稱 `TryAdd` 保留第一筆，與 `FirstOrDefault` 同。
16. `PrtgScopeRevisionReader` 政策指紋快取：整段在 `_gate` 內，僅字串與長整數欄位，無集合；版本於讀內容前取得，競爭時至多下個檢查點重算；輸出字串格式與持久鍵逐字相同（`...{host}:{rawFp}`）。
17. `VisibleHostKeyPart` 的 `ConditionalWeakTable`：只對 `FrozenSet<long>` 以參考記憶，鍵物件回收條目即消失；`FrozenSet.Empty` 為單例但內容恆為空，輸出恆為空字串；`VisibilityService` 每請求各自凍結，不會把 A 的片段端給 B。
18. 郵件重試退避欄位：記憶體 only、`_retryLock` 保護、設定版次 `Revision` 每次儲存換新 Guid（ISystemSettingsStore.cs:26），匯入同時間戳也會換；重啟後 `MinValue` 立即重試；取數結束入口不受退避。唯一細節：夜間資源守門自動偵測會寫設定（PrtgResourceGuardAutoDetect.cs:129）而重置退避一次，最多多一次重試，無實質影響。
19. `PrtgHostMapRefresher` 持有單一 `HostStore`：`PrtgHostMapper` 只讀不改主機物件（PrtgHostMapper.cs 無對主機屬性的指派），共用快照安全。
20. `ScheduleOptionsStore(cached:true)`、`checkpointSettings`（`SystemSettingsStore` 快取）皆為版本探測快取，每次仍探版本、命中仍各自反序列化，不共用可變實例。

**主機異動／設定異動／匯入**
21. 主機新增／改 IP／合併／停用發生在夜間或快照週期中：`HostStore` 索引隨版本重建；快照業務範圍鍵含主機清單版本與範圍指紋（含 IP、Active、MergedInto）；`PrtgScopeRevisionReader` 的主機指紋以版本快取，版本在前、內容在後，不會把舊內容標成新版本。
22. 監看政策修改：快照的 `ReadRawSemantic` 柵欄仍含縮圈指紋，政策變更使進行中取值取消的語意不變；每日路徑的政策指紋以 `Read()`（含政策版本）計，不受本輪快取影響。
23. 系統設定匯入同時間戳：`PrtgOperationScope` 以內容比對（`SameSettings`）而非時間戳（PrtgOperationScope.cs:56）；沿用範圍字串的短路求值與改動前相同（設定已變時不讀範圍，`Publish` 自行讀）。

**範圍外清除候選為空的提早完成**
24. `ReadCandidatePage`（PrtgScopePurgeStore.cs:485-489）只在候選感測器集合為空時直接把游標推到水位線；同一交易內的範圍柵欄、授權柵欄、擁有權檢查、租約、`RefreshFinalCounts` 與完成時的 `RecordBaselineInTransaction`（:311-345）都在原處，未被繞過。作業最終欄位（`Status=complete`、`ActiveOwner`／`LeaseExpiresAtUtc` 清空、計數、游標＝水位線）與走完全程一致；差別只在 `Revision` 與 `UpdatedAtUtc` 的增量較少。

## 未能驗證
- SQL Server 實連：首見日批次 INSERT／UPDATE、`GetMany` 的 OPENJSON、`GetStateChangesForRuleEvaluation` 的 `Contains` 皆僅能由讀碼與 R3 的 `ToQueryString` 結果推論。
- DI 建立 `PrtgRiskReviewHostedService`：依 MS DI 對選用參數的既定行為與計畫所述探針，未自行重跑。
