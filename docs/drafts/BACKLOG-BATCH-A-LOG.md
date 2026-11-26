# 第 81 輪補修批次 A：BACKLOG 整理逐條紀錄

> 過程紀錄，隨 `FEEDBACK-81-3-PLAN.md` 歸檔。原行號以整理前的 BACKLOG（dev@7e7d3950，925 行）為準。

做法：腳本 整理腳本 依原行號（925 行）逐條動作；每個動作先核對該行開頭，核對失敗整體中止；行範圍替換由檔尾往檔頭做，行內替換先做。只動 `docs/BACKLOG.md`。

## 基準（動工前）
- `grep -c '^- ' docs/BACKLOG.md`＝263、`grep -c '^### '`＝39、`grep -c '^| '`＝9、行數 925。

## A1 移除已解決（每條移除前當下重查）
| 原行號｜動作｜改了什麼｜證據 |
|---|
| 14（G9-E2-05）｜移除子項｜G9 的 ID 清單 `04～08` 改為 `04、07、08`（05 已移除）｜`WorkOrderBoardService.cs:318 FillSections` 分 Dispatchable／UnassignableSection／NotDispatched 三段；`DashboardService.cs:117` 用 `pending.DispatchableIssues`、`UnassignableIssues`、`TooLarge`（不含嚴重度未達段）|
| 14（G9-E2-06）｜移除子項｜同上，06 已移除｜`IssueNames.cs:21,30`（`PrtgKindText` 單一出口）；`LogForesight.Tests/IssueNameParityTests.cs:17`（守門）|
| 225｜移除｜整條（稽核起日晚於迄日）；所在小節剩一條，小節標題與其「觸發時機」句同步改成只指權限異動那條（224 行：刪掉「稽核頁在 from > to 時提示或對調」）｜`AuditController.cs:37 ParseDateRange`；`QueryStringParsing.cs`：`from > to` 時 `(from, to) = (to, from)`（`throwOnReversed` 為 false 時對調）|
| 234｜移除｜整條（新增使用者撞號直接更新）｜`UserAdminService.cs:235 if (request.IsNew && existing != null)` 擲驗證錯誤 |
| 265｜移除｜整個 `###` 小節（PRTG 鏡像沒有感測器單位；連同其後空行）｜`PrtgFetchService.cs:1069 Unit = PrtgJson.GetString(el, "unit")` |
| 318｜改寫（不移除）｜「3.（已完成，編號保留）…」改成一行指標：「3. Linux 主機的欄位形狀與 `sev` 對應：已由多輪 probe 定案並實作，見 NETIQ-API-REFERENCE §4a、LINUX-RULES」｜移除前 `git grep` 該編號：`LogForesight.Core/Service/NetiqPipelineService.cs:869` 註解寫「docs/BACKLOG.md 未決事項 #3」仍引用編號（該註解實指 Defender/RDP 頻道，編號本來就對不上，屬程式碼註解、不在本批範圍）→ 依規劃保留編號；`NETIQ-API-REFERENCE.md:228` 有 `## 4a`；`LINUX-RULES.md` 存在 |
| 379｜移除｜整條（AI 設定頁測試連線）｜`Settings.cshtml:113 #ai-test-btn`、`settings.js:1821` |
| 522｜移除＋新增現況一行｜移除「被指派歷程不含已被改派走的案件」；同節在原位留一條「被指派歷程的改派列只涵蓋有原處理人紀錄的改派」（舊紀錄沒有原處理人時不猜測）｜`UserAdminService.cs:197-215 GetReassignments`＋「已改派給 {name}」；`UserAdminService.cs:196` 註解「舊紀錄沒有原處理人時不猜測」；`RecordHandling.cs:41 PreviousHandlerId` |
| 729｜移除｜整條（來源觀測超過 64 筆靜默截斷）｜`LogAggregator.cs:60,245 SourceObservationsTruncated`；`record-detail.js:1664,1776`「清單已截斷」 |
- 驗收：9 條原標題關鍵字 `git grep -c` 於 BACKLOG 全為 0（見下方驗收輸出）；14 行其餘子項（E2-01、02、04、07、08）仍在。
- 與規劃差異：9 條實際為「8 條移除＋1 條（318）改成一行指標」，因 318 有程式碼註解引用。421 依任務指示改寫不移除（見 A2）。

## A2
| 421｜改寫｜標題改為「blob 是否有內容只判非空白（7-S2 殘餘，不處理）」，內文改現況句（`HasBlobContent` 只判 `!string.IsNullOrWhiteSpace(Read())`），保留「無已知受影響案例、價值低於修正成本」｜`HandlingBlobMigrator.cs:286 private bool HasBlobContent(string key) => !string.IsNullOrWhiteSpace(_blobFactory(key).Read());` |

## A3 改寫（證據以當下指令重數）
| 原行號｜改了什麼｜依據 |
|---|
| 19｜序言「不改運作邏輯的部分已完成」改成只講「以下是…刻意未做的項目」｜`ls docs/archive/FEEDBACK-78-PLAN.md` 存在 |
| 25（版本戳）｜「任何成功的非 GET 請求都讓…失效」改為預設推進＋`[SkipDataVersionBump]` 免推進｜`Program.cs:300-326 DataVersionStampPolicy.ShouldBump`（排除法）；`git grep SkipDataVersionBump` 命中 AdminController、AiController、AuthController、CspReportController、HandlingController…（`LogForesight.Web/Controllers/Api`）|
| 54（A7）｜三個測試類別→兩個，並點名｜`git grep -c "NetiqSourceAttempt.Mark(" -- LogForesight.Tests`：`NetiqPipelineBaselineTests.cs:1`、`RunMonitorServiceTests.cs:3`；Core/Web 命中 0；`NetiqSourceAttempt.cs:64 RecordBatch` 存在 |
| 79｜去掉「第二波」「在 VM 的 D2 資料集量測」過程敘事，改現況句｜—（純敘事）|
| 84｜限縮為「問題查詢清單頁的伺服器端 CSV 匯出」，補報表已有伺服端 CSV｜`ReportCsv.cs:22 MaxRows = 50_000`；`records.js:2408` 複製為 CSV；`IAnalysisRecordQuery.cs:143 QueryPage` |
| 106｜刪掉已達成的建議「只對當頁 50 組」、改述現況｜`DismissedIssueService.cs:22 PageSize = 50`、`:80 pageGroups`、`:99 ClosedByOf`（只對當頁）；`AdminReminderService.cs:155-160` 仍逐張上報單分頁查成員 |
| 177（行內）｜補「只有成員為零的空單會自動關閉為取消」｜`WorkOrderCoordinator.cs:136 catch when (created && CountOf(order.WorkOrderId).Total == 0)`，寫 Cancelled「建單失敗自動關閉」 |
| 204-206｜與 CLAUDE.md 重複的 PRTG 配額／EfSqliteFixture 段落刪除，留量測做法與最慢清單｜`CLAUDE.md` 同兩條規則在「不要做」；`ls LogForesight.Tests/tools/test-timing.py`、三個測試檔皆在 |
| 219｜徽章數字與清單重數：字面 23 處＋`PRTG_OUTCOME_META` 5 項；禁用 regex 改 `\bbadge (bg\|text-bg)-`｜`git grep -cE "badge (bg\|text-bg)-"`：Prtg.cshtml 10、dashboard 1、prtg-admin 5、prtg-calibration 5、rules 1、settings 1（format.js 1 為註解不計）＝23；`runs.js:53-58` 有 5 個 `bg:` 且 `:66 badge ${meta.bg}`，「失敗」`bg-danger` 仍成立（評估表說排程作業頁「失敗」未命中，是漏看樣板字串）|
| 237-240｜小節改名「次要文字對比守門未涵蓋表格滑過與選取列底色」，數字改為 5.81／5.55／5.34 並說明對比本身已達標｜`base.css:113 --lf-text-muted #5b6678`；python 計算對 #ffffff 5.81、#f8fafc 5.55、#eff6ff 5.34；`AccessibilityUiTests.cs:177-178` 次要文字守門只列 `--lf-card-bg`、`--lf-gray-50` |
| 249（行內）｜「約 165 個測試檔（約 1,000 處）」→「165 個測試檔（約 1,060 處）」；「約 380 處」→「約 370 處」｜`git grep -lE "DateTime\.Now" -- LogForesight.Tests`＝165 檔、`-c` 加總 1064；`git grep -cE "DateTime\.(Now\|Today\|UtcNow)" -- LogForesight.Core LogForesight.Web` 加總 373 |
| 254（行內）｜「148 處」→「`*.cs` 共 186 行：Core 與 Web 15 行、測試 171 行」｜`git grep -c '\\r\\n' -- '*.cs'` 加總 186；Core+Web `*.cs` 15、Tests 171；`ci.yml:21 os: [windows-latest, macos-latest]` |
| 296｜補互動逾時是程式常數（8／60 秒），觸發條件改為「調到接近或超過 2 分鐘」｜`WebAiService.cs:146 TimeoutSeconds = 8`、`:176 TimeoutSeconds = 60`；`SettingsBoundClient.cs:23,42` 預設兩分鐘；`SystemSettings.cs:101 AiTimeoutSeconds` 屬背景批次 |
| 298｜補預設 90 天下界｜`AuditLogStore.cs:37 DefaultRangeDays = 90`、`:88` 有篩選無起日以今天−90 天為下界 |
| 360-367（MCP）｜決策歷程句改現況（目前不提供；內建詢問 AI 走確定性預取，理由留一句並指向 WEB-SPEC §9.3）｜`git grep -li "MCP server\|ModelContextProtocol" -- LogForesight.Core LogForesight.Web '*.csproj'` 無命中；`WEB-SPEC.md:1687-1688` 有 MCP 評估結論 |
| 368-373（AI 用量）｜去掉「規劃 B2 契約寫…實作只有五欄」，改現況｜`AiUsageDtos.cs`：Date／Calls／PromptTokens／CompletionTokens／TotalTokens／CallsWithoutUsage，無件數欄 |
| 425｜「可能配錯說明」改「同 program 多條規則時不給說明（回 null）」，觸發條件對應調整｜`KnownIssueCatalog.cs:456-478 FindLinuxRuleByProgram`：`if (found != null) return null;` |
| 444-453（MTTA/MTTR）｜去掉標題「輪」與矛盾的括號句，改成一段現況＋原四個待決問題｜`ReportEffectivenessService` 有帳齡／再發生率；`IssueCase.cs:59 ClosedAt` |
| 461-463｜「淺副本」改為「清單唯讀、DTO 共用」，刪「複製清單」半句｜`IssueRankingCache.cs:55,58` 回 `IReadOnlyList<IssueRankingDto>`；`DashboardDtos.cs:126-` DTO 欄位皆 `{ get; set; }` |
| 504-509｜標題與內文「四次」→「多次」，刪「索引部分已解決」改陳述現有索引｜`EfIssueAggregateQuery.cs` `ApplyRiskLevels(` 呼叫 178、250、397、703、772、1788、2445、2482、2519＝9 處；`LfDbContext.cs:306`、`SchemaUpgrader.cs:188` 有 `IX_lf_daily_records_risk_date` |
| 513-517｜刪「§9 未竟」「使用者實測後」，只留案件敘事「由 ○○○ 處理」仍為單值｜`HandlingHistoryQueryService.cs:103`、`DayHandlingCommandService.cs:417` 已走 `NameFormat.UserLabel`；`RecordDetailQueryService.cs:322,506` 仍 `?.DisplayName`；`record-detail.js:738-739` |
| 532-536｜改為「鎖已涵蓋建單／併入／改派／拆單／取消／夜間，殘留＝背景逐日同步未取鎖、多行程」｜`WorkOrderCoordinator.cs:44 OrderMutationLock`，`:96,174,433,500,588,631,776` lock；`IssueCaseCoordinator.cs` grep lock 無命中 |
| 541-542｜縮成「不模擬當日非結案標記與機房結論自動套用」｜`WEB-SPEC.md:1853-1856`（試跑含 ⓪ 靜音、最近出現日已結案者整筆排除）|
| 568-572（通知）｜去掉「Email 部分已解決」「完成可信度重構」敘事，改現況｜—（評估表建議保留，但敘事字眼命中，只改措辭不改結論）|
| 589-591｜縮成條件式一句｜`SqlPerformanceMonitor.cs:37 TopSlowCapacity = 10` |
| 596-600｜刪「前端三頁只做 JS 語法檢查」半句與「本來」｜`EfJsonBlobStore.cs:51,74` 有 `blob:{key}:Read／ReadVersion` 埋點；CLAUDE.md 有 UiTests／`*.behavior.mjs`／量測工具慣例 |
| 637（行內）｜`AdminController`（20 個）→（21 個）｜`git grep -c "\[FromQuery" -- Controllers/Api/<檔>`：Admin 21、Audit 9、Rules 7、Handling 無 |
| 651（行內）｜「約 60 處」→「78 處」｜`git grep -c "new BatchRunRecorder(" -- LogForesight.Tests` 加總 78 |
| 657（行內）｜hash 載入兩次縮成「負載看板」「待派」頁籤，註明預設頁籤已避開｜`work-orders.js:301` 預標 active；`:581 loadedTabs.add('load')`、`:766,787 add('gaps')`；`WorkOrders.cshtml:18-21` 頁籤 data-tab |
| 659｜補「集合元素數已有上限」｜`DefaultInputLimitsFilter.cs:34 CountOf(value) > DefaultMaxCollectionCount` |
| 681（行內）｜「（10 處）」→「（7 處）」｜`IIssueAggregateQuery.cs` `visibleSeverities … = null`：80、92、148、157、169、198、209＝7 |
| 720-724｜刪「改增量後」「經查證確認」｜`SchemaUpgrader.cs:804 IssueFirstSeenFullDoneBlobKey = "issue_first_seen_full_done"`；`StorageBackend.cs:397 MergeIssueFirstSeenSeed(bool force = false)` |
| 749（行內）｜「約 1600 行」→「約 2,600 行」｜`wc -l runs.js`＝2608 |
| 757｜「218 處」→「含 btn 類別的 class 屬性約 280 處」｜`git grep -ohE 'class="[^"]*btn[^"]*"' -- 'LogForesight.Web/Views/*.cshtml' \| wc -l`＝280 |
| 758｜「44 處」→「38 處（Views 14、JS 24）」｜`git grep -nE "alert alert-"`：Views 14、wwwroot/js 24 |
| 762｜徽章數字：JS 16→13、PRTG 維護頁 10 處＝`Prtg.cshtml` 10，補 `runs.js` 樣板｜同 219 的 grep |
| 763｜「多數」改成實際：19 處 aria-pressed 在 8 個檔，`date-range.js` 等仍只切 `.active`｜`git grep -c aria-pressed -- LogForesight.Web/wwwroot/js` 加總 19、8 檔；`date-range.js:52-54 setActiveChip` 只 `classList.toggle('active')`；audit/groups/records/runs/users 檔內無 aria-pressed 但有 `classList.toggle('active'` |
| 837-838｜改成「只有手動結構同步會推進版本戳；夜間管線與快照寫入不推進」｜`PrtgStructureSyncService.cs:420 if (success) _versionStamp.Bump()`；`PrtgDeviceIndexCache.cs:60` 註解同義；`DashboardService.cs:297` |
| 875-877｜刪「已改用」，改陳述「作業期間用 ReadStructural／ReadRawSemantic，預覽與佇列雜湊仍用 Read()」｜`PrtgScopeRevisionReader.cs:35-44`；`PrtgBackfillService.cs:459,473`、`PrtgSnapshotHostedService.cs:443,1068,1092,1134,1232` 仍 `.Read()` |
| 894-895｜刪「規劃允許…實作則是」，註明固定全類別（PRTG-SPEC §10）｜`PRTG-SPEC.md:566 ## 10. 診斷資料傳輸`、`:570` 匯出固定全類別 |
| 896-898｜改成「收尾掃描是刻意的」｜`PrtgTriggeredValueFetcher.cs:205-209` 註解與 `if (!singlePass) await ScanAndFetchAsync()` |
| 899-901｜補 `busy_timeout=5000` 已緩解｜`SqlitePragmaInterceptor.cs:23,40` |
| 764（行內，評估表之外）｜「補充對照已完成」→「已實作」，避開敘事字眼｜—（只換詞）|
| 224（評估表之外）｜225 行移除後，小節「觸發時機」句同步刪去稽核頁那半句｜見 A1 225 |

## A4 合併
| 464 + 475（= 評估表 D 區第 6、9 條，同一組）｜合併成一條（保留 464 位置）｜保留條含：非 all 範圍記憶體推導、`DayHandlingDerivation`、`QueryLightweight`＋`FilterByScope`、母體僅高／中風險、366 天上限（`ParseDateRange`）、`DeriveDayHandling` 可接 SQL 的條件｜`DayHandlingDerivation.cs:9`、`ReportService.cs:199-208`、`IIssueAggregateQuery.cs:257 DeriveDayHandling` 皆存在；475 條原有的「觸發」＝「真的成為瓶頸時可接上」已併入 |

## A5 新增／併入
| 位置｜動作｜內容｜證據 |
|---|
| 553 後｜新增｜立即派工／人工追加併入已過期舊單，新成員沿用單的過期期限（附觸發時機）｜`DefaultDueDays.cs:41 MemberDueDate`（夜間用，`WorkOrderCoordinator.cs:689`）；`WorkOrderCoordinator.cs:209 MembersInto`、`:241 DueDate = order.DueDate` |
| 633 後｜新增｜儀表板摘要主體約 2～3 秒（最慢 3.05）＋啟動後第一次今日焦點等 3～4 秒（併入評估表第 3、4 條，附觸發時機）｜`DashboardService.cs:74 GetSummary`、`:146 BuildBody`；`AiController.cs:122 today-focus`。數字取自 `docs/drafts/BACKLOG-REVIEW.md` 第 3 節第 3、4 條（本批未重新量測）|
| 633 後｜新增｜資料保留清除一次讀入全部「不處理 30 天解除」候選（附觸發時機）｜`EfIssueCaseStore.cs:677 ReleasedCaseIds`（`.ToList()` 後逐筆 `DismissalRelease.IsReleased`）、`:659 Prune` 呼叫 |
| 763 後｜新增｜必填標示星號與文字兩種寫法並存（附觸發時機）｜`lf-required-mark` 於 Views 17 行（`Groups.cshtml:65` 等）；文字「（必填）」於 `core/ui.js:645`、`hide-triage-panel.js:185`、`issue-mute-modal.js:184`、`IssueOwners.cshtml:80` |
| 632｜併入｜「問題鍵在資料庫端區分大小寫」併進既有定序條目，標題改為「…出現點分組與問題鍵比對」｜`EfIssueCaseStore.cs:94` 文件註解：問題鍵以字串全等比對、記憶體端 `IssueSignatureKeyComparer` 才視為同鍵 |

## A6（批次 B 已完成 7 條；移除前 `git grep` 確認程式已含修正）
| 原行號｜動作｜證據 |
|---|
| 14（E1-01，B1）｜移除子項：G9 清單去掉 `E1-01` 與其說明句｜`dashboard.js:431 url: loginFailuresUrl()`、`:465-468 /audit?actions=login_failed&from=…&to=…`（提交 5f95f51e）|
| 15（E6-10，B2）｜移除子項：G10 清單去掉 `10`｜`PrtgTransferController.cs:98`（放棄）、`:194`（完成）`_audit.Record(AuditActions.PrtgDataImport, …)`（提交 cefd0760）|
| 17（T-02，B3）｜移除子項：`G12 其餘（T-01）`｜`NodeRuntime.cs:12 PipesIncomplete`、`:137` `RunOrFail` 檢查、`:186`（提交 ceb3a2ce）|
| 220（B4）｜移除整條（`icon: 'lock'`）｜`handler-detail.js:177,186 icon: 'slash-circle'`；`img/icons.svg:24 slash-circle`；`git grep "'lock'" -- wwwroot/js` 無命中（提交 05bb9be3）|
| 232（B5）｜移除整條（兩處「AI 判讀」同名）｜`record-detail.js:1655` 標題仍「AI 判讀」，`:2457 makeButton('產生判讀')`（提交 74526b34）|
| 351（B6）｜移除整條（下拉列出解不開密碼的 Sentinel）｜`netiq-import-wizard.js:29-30 … && s.credentialState !== 'undecryptable'`（提交 3fabf212）|
| 564（B7）｜移除整條（權限異動排序）｜`PermissionChangeStore.cs:407-408 OrderBy(r => r.HostNameKey)`（提交 eb67d501）|
| 633 後（B7 同型普查）｜新增｜日紀錄查詢「依主機」排序用 `DailyRecordRow.HostName`，兩後端順序不同；修法要正規化鍵欄＋回填＋索引或改 HostId 排序（附觸發時機）｜`EfAnalysisRecordStore.cs:1786 "host" => … OrderBy(r => r.HostName)`；`LfDbContext.cs:257-258` DailyRecordRow 只有 `host_id`／`host_name`，無鍵欄；`git grep -nE "OrderBy(Descending)?\(r => r\.HostName\b"` 全專案只命中這一處 |

## A7
| 54｜改｜見 A3 54 |
| 911｜補「退場時一併拿掉 `MigrationGateMiddleware` 的權限異動分支，兩分支拿掉後整個中介層可刪（含 Program.cs 註冊）」｜`MigrationGateMiddleware.cs:25-31,44,55` 只有兩個分支（權限異動 `/api/permission-changes`、處理狀態 `HandlingGuardedPrefixes`）；`Program.cs:240 app.UseMiddleware<MigrationGateMiddleware>()` |
| 915｜判定依據拆成兩種｜`WorkOrderBackfiller.cs:47 CountPending()`、`:61,82`；`DailyRecordBackfiller.cs:36 CurrentVersion = 3`、`:103 ExtractVersion < CurrentVersion` |
- 兩節條目數不變：「待決的已知缺陷」10 條 bullet（`awk` 取該節 `^- ` 前後皆 10）、升級期表格 1 表頭＋8 資料列＝9 列（`^| `），前後相同。

## 沒照評估表做的條目
- 65 行（評估 A#57）：評估說 `WorkOrderQueries.DueSoonDays` 不在 `WorkOrderQueries`；實查 `WorkOrder.cs:309 public static class WorkOrderQueries`、`:379 DueSoonDays` 在同一類別，原敘述正確，不改。
- 12～17 行（評估 A#5、6、8、15、16、18、21 的改寫）：BACKLOG 這幾行只列 ID，沒有子項描述可改寫，不動（僅依 A1／A6 拿掉 05、06、E1-01、E6-10、T-02）。
- 150 行（B#7）、408 行（C#40）：評估表自己寫「可精簡／無需動」，屬選用精簡，事實無誤，不動。
- 231 行（B#21a「送出並回覆下一張」）：評估寫「改寫或移除，需實測確認」；`handler-detail.js:762-890` 顯示回覆走彈窗、不再展開列，但無實測證據，不動。
- 302 行（C#8 NetIQ 接線）、617 行（D#53）、754 行（E#43）：評估狀態為「無法判定」，任務規定不動（含 754 行的「報表已改成折線」「下一輪預定」字樣，見下方掃描）。
- 47 行：「其他驗證屬性已改成…」是敘事字眼，但屬「待決的已知缺陷」節，任務限定該節只修 54／911／915 行，不動。
- 906 行（升級期表頭說明）、891 行（`Magnitude` 非缺陷）：評估建議保留，不動。
- 568 行（Email 推播）：評估建議保留，但條目含「已解決」「重構」敘事，為遵守現行文件不寫過程敘事而只改措辭。

## 驗收
- 三個計數式子（基準 − 移除 − 合併 ＋ 新增 ＝ 現值）：
  - 清單項目（`^- `）：263 − 9（220、225、232、234、351、379、522、564、729）− 1（合併 475 進 464）＋ 6（A5 四條：立即派工過期期限、儀表板摘要效能、保留清除讀入、必填標示；＋522 現況一條；＋B7 同型普查一條）＝ 259；現值 259 成立。
  - `###` 小節：39 − 1（265 PRTG 鏡像沒有感測器單位）− 0 ＋ 0 ＝ 38；現值 38 成立。
  - 表格列（`^| `）：9 − 0 − 0 ＋ 0 ＝ 9；現值 9 成立（911、915 為就地修改）。
- 敘事字眼掃描（`grep -n "原本\|後來\|已改成\|已改用\|已完成\|第 [0-9]* 輪\|第二波"`）剩 4 處（47、680、694、845）：
  - 47 行「其他驗證屬性已改成…」：敘事，但在受限節（只准修 54／911／915），未動，待主代理裁示。
  - 680 行「已完成跳過 N 台」：引用畫面上的里程碑文字。
  - 694 行「報表已改成折線…」：敘事，屬評估「無法判定」的條目（新視覺方向第二階段），未動，待裁示。
  - 845 行「對應旗標已完成後才可移除」：狀態條件（旗標值為完成），非沿革；升級期表頭說明，未動。
- `DocNarrativeGuardTests`＋`HelpContentServiceTests` 定向篩選：16 通過／0 失敗。
- 引用檢查：`docs/archive/FEEDBACK-78-PLAN.md`、`docs/NETIQ-API-REFERENCE.md §4a`、`docs/LINUX-RULES.md`、`docs/WEB-SPEC.md §9.3`、`docs/PRTG-SPEC.md §10`、`docs/DESIGN-SYSTEM.md §8.8`、`docs/drafts/ENV-PROBE-PLAN.md` 皆存在。
