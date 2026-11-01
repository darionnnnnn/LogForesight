# LogForesight 第 61 輪規劃：遞延 bug、效能、CSP、守門測試、文件對齊

> 狀態：全案完成已併 dev
> 基準：dev@fe241168（7,357 通過／10 略過；`--list-tests` 共 7,082 條，Theory 算一條）
> 來源：第 59 輪 PLAN §5 草案（L 文件／M 守門）、第 60 輪 PLAN §6 草案（純效能＋CSP）、BACKLOG「第 60 輪體檢遞延」；三份合併去重 41 項
> 實作方式：主代理寫規格與驗收；改檔代理依複雜度用 Sonnet 5.5／Haiku 5.5；一個 checkout 一次只派一個改檔代理；每批驗證後各自 commit 並 push 到 `feature/feedback-61`
> 核對方法：主代理親核 41 項（§1）；兩個唯讀 Sonnet 審查代理分別以「尖銳使用者＋管理者」與「程式面＋整體影響」視角審提案，主代理逐條重驗後併入（§1 標 ◆ 者＝審查補充且已重驗屬實）

## 0. 總結與範圍

- 第 59 輪的原始 findings（敘事 69 處、註解失效引用 11＋7 處、BACKLOG 6 項過時）隨 `.gemini-tasks/r59` 刪除而失傳，L 批改以**現況重量＋守門測試驅動**：先讓 M 的文件守門紅，再修文件到綠，剩餘敘事句由唯讀代理分檔比對、主代理抽查。
- 五批建議順序 **P → N → O → M → L**：P／N 是獨立的行為修正與效能，O 改回應標頭要先於 M 的安全標頭測試擴充，L 依賴 M 的文件守門。
- 審查後的主要修正（詳 §1）：P-1 的根因在**外層無條件 `catch (OperationCanceledException)`**，只改內層無效，且 `LogAnalysisService` 兩站點是純 DB 降級路徑不該改；P-5 漏了「監看範圍正在清除中」那條會讓多個寫入端點回 500 的路徑；CSP 的 `img-src` 必須放行 `data:`（bootstrap 內建圖示 23 處、品牌 logo）；N-1 快取不能在服務層且 DTO 不可共享（控制器就地改寫 `AdminContacts`）；N-2 的喚醒可沿用 `DataVersionStamp`、不新造訊號；N-8 三份 `Recheck` 是三條車道各自的重核規則，**不合一**；N-10 帳本「自動釋放」前提不成立。
- 不做的事集中在 §5，每條附理由。

## 1. 核對表（主代理親核；◆＝審查代理補充後重驗屬實）

### 批次 P：體檢遞延的 bug

| ID | 判定 | 證據 | 補充 |
|---|---|---|---|
| P-1 Core 把 HttpClient 逾時（`TaskCanceledException`，ct 未取消）當使用者取消 | ✅ 但屬**一致化**而非活 bug ◆ | 內層 `when (ex is not OperationCanceledException)`：`AnalysisOrchestrator.cs:750`、`LogAnalysisService.cs:165／494／741`、`NetiqPipelineService.cs:243／807`。**外層無條件 `catch (OperationCanceledException)`**：`AnalysisOrchestrator.cs:756`（印「使用者取消」、`FailureMessage=使用者取消`）、`:1175`；`NetiqPipelineService.cs:527`（帳本記 `stopped`）、`:642` | ◆ 下游已把 ct 未取消的逾時轉成非 OCE：`SentinelClient.Auth.cs:37／107`（`TaskCanceledException && !ct.IsCancellationRequested` → `SentinelClientException`）、`AIService.cs:310`（降級為 `Success=false`）、`PrtgClient.cs:230／410`、`NetiqDirectoryClient.cs:461／540`（預算逾時與使用者取消分開）。◆ `LogAnalysisService.cs:165`（`PersistPendingRiskReport`，同步、無 ct、try 內只有 DB 附掛）與 `:494`（同為 DB 附掛）**沒有 HTTP 來源，且是刻意的降級路徑，不改**。`:741` 含 AI 深析，順手改。`WorkOrderBoardService.cs:308` 純 DB，不改。正確樣板：`PrtgScopeEstimateController.cs:289`、`AiProbeService.cs:206-212` |
| P-2 `refreshPrtgMirror` in-flight 去重吞掉寫入後重讀 | ✅ | `prtg-admin.js:2110-2114`；呼叫端 `:1295／1989／2373／2850／3023` | ◆ 舊快照會把 `scopeRevision`（`:2211`）蓋成寫入前的值，使用者下一次儲存被 409「範圍已被修改」；`refreshPrtgMirrorCore` 一趟含鏡像、待重評、清除預覽三組請求（`:2214-2216`），補跑＝整包再打，所以多次呼叫要合併成一個補跑 |
| P-3 記錄查詢 AI 歸納在途改篩選仍寫回 | ✅ | `records.js:112-146` 無世代比對；`:457` 篩選變更只清空 | ◆ 原提案「非 429 錯誤不清前次內容」現況已成立（失敗路徑不碰摘要區）；真正痛點是**非 429 失敗完全靜默**（`silent:true`、無 toast、無文字） |
| P-4 逐屬性讀 `SchedulerRunState` | ✅ 2 支 | `HealthService.cs:111（LatestActivity）／157-164`；`PrtgBackfillService.cs:272-273／724`；`Snapshot()` 已有（`SchedulerRunState.cs:359`，含 LatestActivity 三元組）◆ | `AiAnalysisHostedService` 只用事件與判定函式，不屬此型 |
| P-5 清除作業以例外訊息分流狀態碼 | ✅ 且**範圍更大** ◆ | 控制器 `PrtgScopePurgeJobsController.cs:77／135` 比對「另一個範圍外清除作業」；擲出端 `PrtgScopePurgeStore.cs:112／119／235`。◆ 另一條：`PrtgScopePurgeStore.EnsureScopeMutationSlotFree`（`:458-464`）擲 `InvalidOperationException("監看範圍正在清除中…")`，被 `EfPrtgStore.cs:119／286／288／985／1019／1129／1196／1235／2448`、`HostStore.cs:241` 所有範圍寫入路徑呼叫，走到 `ApiExceptionFilter` 的預設分支 → **HTTP 500＋通用訊息**（只有 `PrtgHostMappingAdminController:336` 有特例保護） | ◆ `PrtgScopeConflictException`（`EfPrtgStore.cs:3098`）語意是樂觀鎖版本衝突、訊息固定，**不可重用**；filter 已有 `ConcurrentUpdateException`／`PrtgTransferStoreException` 兩個 Core arm，加第三個是 3 行 |
| P-6 `netiq_attempt_ledger_invalid` 回 200 | ✅ | `ApiResponse.cs:144` 表登錄 200；`ScheduleController.cs:244／254` 直接 `return Fail(...)`——◆ `StatusOf` 只被 `ApiExceptionFilter` 讀取，直接回傳的 `Fail` 不經過濾器，HTTP 永遠 200 | ◆ 前端 `api.js:178-188` 對「200 但 success=false」與「4xx」走同一條 `ApiError` 路徑，唯一呼叫端 `runs.js:1876` 只顯示 `err.message`，改 409 畫面零差異。`ScheduleRunPreviewNetiqAttemptTests.cs:90` 直接呼叫方法斷言 `Success=false`，改 throw 要改測試 |
| P-7 `AssessPage` 每頁重讀最新日對應 | ✅ 小 | 每頁重讀 `ResolveHostMapRows(_store, newestDate, newestDate)`（`PrtgValueAssessment.cs:276-288`）；◆ 呼叫端 8 處：`PrtgDailyPipeline.cs:508／568`、`PrtgValueReadiness.cs:41`、`HostPrtgDetailService.cs:162`、`RuleAdminService.cs:429／531／576` | `_valueAssessment` 是長壽服務，不可在實例內記憶；只能由外層算一次傳入 |

### 批次 N：效能

| ID | 判定 | 證據 | 補充 |
|---|---|---|---|
| N-1 新鮮度告示每次換頁重算 | ✅ | `ScheduleFreshnessService.cs:25-57` 每次 `GetRecentRuns(14)`；呼叫端 4 處：`HealthController.cs:89`（`layout.js:735` 每頁、`dashboard.js:800` 進頁，儀表板連打兩次）、`HealthService.cs:104`（健康頁）、`HealthService.GetScheduleFreshness`、`MailNotificationService.cs:1752`（各自傳 `now`） | ◆ **陷阱**：`HealthController.cs:92` 就地改寫回傳 DTO 的 `AdminContacts`，快取若共享實例會把聯絡名單洩漏給其他使用者。◆ 既有 `SummaryCache.GetOrAdd`（`SummaryCache.cs:59`，版本戳＋30 秒 TTL）可直接用；`freshness-ack` 與排程設定儲存都是成功的 POST，`DataVersionStampPolicy`（`Program.cs:286-306`）會自動 `Bump()`，不需另寫失效點；排程結束不推進版本戳，由 30 秒 TTL 兜底 |
| N-2 CaseDaySync 固定 5 秒輪詢 | ✅ | `CaseDaySyncHostedService.cs:23／77`；已注入 `DataVersionStamp`（`:19`） | ◆ 同一迴圈還跑 `SweepClosures`（`:59`）：詳情頁逐筆標記不經交辦單協調器（`WorkOrderCoordinator.cs:1010-1019`），靠這個掃描補結案。待同步旗標只在 Core `IssueCaseCoordinator.cs:399-405` 寫入，Core 不能參照 Web。**但**兩條路徑的使用者動作都是成功 POST → `DataVersionStamp.Bump()`；夜間掛接在 `SchedulerHostedService.cs:506` 也 Bump——以版本戳變動當喚醒就涵蓋全部來源，不需新訊號。多行程部署本專案不支援（BACKLOG:443） |
| N-3 完整回填計畫算 5 次 | ⚠️ 已不成立 | `PrtgFullBackfillPlanBuilder.Build` 只剩 `PrtgBackfillService.cs:464` 一處 | §5 |
| N-4 未對應主機清單在請求執行緒做真 DNS | ✅ | ◆ `new PrtgAddressResolver()` Web **13** 處（`PrtgHostMappingAdminController:57`、`PrtgScopeEstimateController:262／278／286／381／445`、`PrtgScopePurgeJobsController:162`、`HostAdminService:318`、`PrtgBackfillService:461／864`、`PrtgHostMapRefresher:58／87`、`PrtgStructureSyncService:392`）＋ Core 6 處（`PrtgDailyPipeline:217／321`、`PrtgProbeSiteCheck:54／213`、`PrtgResourceGuard:127`、`PrtgResourceGuardAutoDetect:102`）；快取逐實例、`Dictionary` 非執行緒安全（`:21`）、無 TTL、無上限、1 秒逾時與「查無」都吞成空陣列（`:100-112`） | ◆ 單例化若把逾時寫進負快取，一次 DNS 慢就讓主機對應整個行程生命週期都對不到；Windows DNS Client 自己還有約 15 分鐘負快取疊在上面。◆ 「每請求預算」已在外部（`PrtgResourceGuardTargets.DeviceDnsBudget`，每趟新建），放進單例會讓並行請求互扣，**不做** |
| N-5 `GetLatestNote` 整表撈回 | ✅ | `EfIssueHandlingStore.cs:99-125` 投影含 `Note` 全文後 `ToList()`；呼叫端 `IssueNoteController.cs:42`；測試 `NoteReuseAndPhrasesTests`（3）、`SqlIssueSignatureStorageTests`（含 5,000 筆噪音案）◆ | ◆ 「串流到第一筆即停」在 SQL Server 早關 DataReader 時傳輸層可能仍讀完剩餘列，兩供應商行為不一致；改**鍵集分批** |
| N-6 `OwnedRows` OR＋`LastWeeklyCheckupDate` 全撈 | ✅ | `EfAnalysisRecordStore.cs:85-92`、`:1090-1097` | OR 改寫需實機查資料，本輪只做 Max 下推 |
| N-7 `ReportFileMigrator` 一次撈全部非低風險列 | ✅ 且連 `OriginalRiskContentJson` 一起載 ◆ | `ReportFileMigrator.cs:178-185`；唯一消費端 `MigrateAll` 的 `foreach`（`:134`），無進度分母 ◆ | 投影排除大欄位的效益大於分批 |
| N-8 寄前重核逐收件人重查 | ✅ 但**不該合一** ◆ | `Recheck` 三份：`SendRunSummaryAsync`（`:983`）、`SendUrgentNotificationsAsync`（`:1125`）、`RecheckDigest`（`:1832`）；設定檢查、風險集合、`withoutPrtg`、是否就地改 `views` 皆不同 | ◆ 「每位收件人寄送前重讀最新狀態」是刻意的重核時機，跨收件人共用一次查詢會破壞它；§5 |
| N-9 AI 快取整份 blob | ✅ 低價值 | `AiCacheStore.cs` | 只加筆數上限 |
| N-10 郵件貢獻帳本 O3／O4 | ⚠️ 前提不成立 ◆ | `MailNotifyStateStore.cs:26` `PreparedMailReservationLifetime = 5 分鐘`，`reserved` 已自動清（`:78-85`）；`attempted`／`unknown` 刻意永久佔用（`:62-64`）；預約結構只有 `ReservationId／Status／CreatedAtUtc`（`MailNotifyState.cs:58-64`），沒有進入 attempted 的時間戳 | 自動釋放 attempted＝重寄；§5。◆ 順手：`GetContributionLedgerStatus`（`MailNotificationService.cs:1989-2001`）的 `ReservedKeys` 把已逾 5 分鐘但尚未被清掉的也算進去，健康頁數字偏高 |
| N-11 `BuildCasesForDay`、`ReadAllRunLines` 回退、`CandidateArrayKeys` | — | 需量測／實機 | §5 |

### 批次 O：CSP 前置事實

| 項 | 現況 | 影響政策 |
|---|---|---|
| inline script | 四段：`_Layout.cshtml:26／28`、`Login.cshtml:22／56`（`:56` 是資源載入失敗退路，漏 nonce 會靜默失效）；`Error.cshtml` 無 | `script-src 'self' 'nonce-…'` |
| inline 事件屬性／`style=`／`<style>`／`eval`／`new Function`／`javascript:`／`srcdoc`／`<iframe>` | 0 | 不需 `'unsafe-inline'`／`'unsafe-eval'` |
| JS 設樣式 | 只用 `style.setProperty`（19 處）；Chart.js 4.4.7 與 bootstrap 只用 CSSOM | `style-src 'self'` |
| **`data:` 圖片** ◆ | `bootstrap.min.css` 含 **23 處** `url("data:image/svg+xml…")`（下拉箭頭、`btn-close`、checkbox／radio／switch）；品牌 logo 是 `data:image/png|jpeg;base64`（`_BrandInner.cshtml:7`、`settings.js:1003／1045`，`SystemSettingsService.cs:704-718` 只放行這兩型） | **`img-src 'self' data:`**，否則全站下拉沒箭頭、modal 關閉鈕空白、勾選看不到 |
| 字型 | `site.css:20-56` 本機 woff2 五個 ◆ | `font-src 'self'` |
| Worker | `prtg-diagnostic-upload.js:12` 同源 | `worker-src 'self'` |
| 下載／導向／表單 | `api.js:240` blob 下載；`location.href = appUrl(...)` 全同源；Login `<form method=post>` 無 action 由 JS 攔截 | `form-action 'self'`、`base-uri 'self'` |
| SVG icon | `<use href="icons.svg#…">` 同源 ◆ | — |
| 安全標頭中介層 | `SecurityHeadersPolicy.Middleware`（`Program.cs:312`）；`Server:FrameOptions` 三值在 `AppSettings.Validate` 驗證（`:108-113`） | CSP 加同一處；`frame-ancestors` 由 FrameOptions 推導，**空字串時整條指令省略** |
| CSRF 中介層 | `CsrfHeaderMiddleware.cs:41-48` 對 `/api` 非 GET 一律要求 `X-Requested-By` | 回報端點要白名單 |
| 反向代理 IP ◆ | `ForwardedHeaders` 只在 `Server:TrustedProxies` 非空時啟用（`Program.cs:171`），且 `KnownProxies` 只吃單一 IP | 未設定時回報節流以代理 IP 為鍵＝全站共用一桶（登入節流已是同樣特性） |
| NLog ◆ | `nlog.config` 規則：Warn 以上同時寫 `error.log` | 回報不可記 Warn |
| 全站授權 ◆ | `FallbackPolicy` 預設要求登入（`ServiceCollectionExtensions.cs:217-222`） | 端點要明確 `[AllowAnonymous]` |
| 開發環境 ◆ | `dotnet watch` 會注入無 nonce 的瀏覽器刷新腳本 | Development 未設定時用 ReportOnly |

### 批次 M：守門測試既有覆蓋

| 項 | 既有 | 差距 |
|---|---|---|
| 每端點都有授權 | `ControllerRouteSnapshotTests`：快照（method, route, permission, anon）＋「每個控制器都有授權宣告」＋一檔一控制器 | **已覆蓋，不做** |
| SQL Server DDL vs 設計期模型 | 只有 `lf_risky_events`（`SchemaUpgraderRiskyEventsTests:102`） | ◆ 基表 4 張（`lf_blobs`／`lf_daily_records`／`lf_log_lines`／`lf_top_issues`）純靠 `EnsureCreated`；其餘 **20** 張有 SQL Server DDL，但 14 個獨立常數（部分 `private`）＋ 3 張（purge_jobs／transfer_sessions／transfer_chunks）內嵌在 `Upgrade()` 三元運算式（`SchemaUpgrader.cs:293-330／367-460`）；格式不一（行內多欄、具名 DEFAULT、`dbo.` 前綴） |
| DB-SPEC 對 `ctx.Model` | 無 | ◆ DB-SPEC 只有 **9** 張表有欄位區塊（`lf_daily_records／issue_first_seen／top_issues／permission_changes／reports／record_handling／issue_cases／work_orders／work_order_events`），格式為縮排兩格的偽 DDL（型別是 `bool`／`text`／`timestamp`，不是 SQL Server 型別）；其餘 15 張只有散文 |
| 文件 §xref 存在性 | 無 | ◆ 引用格式雜（`§14-#4`、`§2.1 Phase 4`、`§3.8-1`、`§9.9e-1`、`§7.2`），distinct 約 72 個；**已失效**：`IRuleSeedStore.cs:18`「WEB-SPEC §14-#4」、`AnalysisOrchestrator.cs:255／274／332／500` 與 `BatchRunRecorder.cs:7`「§2.1」「§11-5」（實際失效的是 §14、§2.1 與「§11-5」寫法） |
| PatternId 數量對文件與說明書 | `CorrelationPatternIdsTests` 固定 23（18＋2＋3） | ◆ `DETECTION-SPEC.md:70-71`、`RULES-SPEC.md:220` 正確；**說明書三處寫「19 種」**：`01-overview.ai.md:88`、`04-record-detail.ai.md:66`、`07-suppression.ai.md:45` |
| `.ai.md`／`.md` 標題同步 | 無；20 章各兩檔 | 新增 |
| 無斷言測試 | 大括號配對精掃：**19 個**（`CacheEvictionTests` 4、`FrontendConsistencyUiTests` 3、`WebAuthTests` 2、`SourceKeyFastReadyTests` 2、`MailNotificationServiceTests` 2、其餘各 1） | 逐條補斷言 |
| `ForwardedHeaders`／`MigrationGate`／`CsrfHeader` | ForwardedHeaders 0 檔、MigrationGate 2 檔、Csrf 3 檔 | 補 ForwardedHeaders 與 MigrationGate handling 分支 |
| mini-dom `innerHTML` setter | `mini-dom.mjs:38` 只存字串、不清 `children`；behavior 檔用到僅 2 檔各 1 處 | 改一致 |
| `AuditActions` 中文名與 `access_denied` | `AuditEntry.cs:65` 78 個常數；`PermissionAttribute.cs:54` 字面；中文對照 `AuditQueryService.cs:50` | 補常數＋守門 |
| `BrandProvider` | 0 測試 | 補 |
| 共用 node 執行器 | ◆ `NodeRuntime` 只偵測；20 個呼叫檔四種形狀：A 腳本檔＋非同步讀＋15 秒逾時（6 檔）；B 同參數但**循序 `ReadToEnd` stdout 再 stderr**＋`WaitForExit(60_000)`（9 檔，stderr 塞滿管線會死鎖）；C `--eval`（`RunsPageUiTests`）；D stdin 餵 JS、**`WaitForExit()` 無逾時**（Setup 四檔）；`NodeRuntime.Probe` 自己也是循序讀（`:50-52`） | 新增 `NodeRuntime.Run`，統一「逾時 Kill 程序樹＋失敗」 |
| `NodeTheory` 略過數 | `NodeTheoryAttribute` 偵測不到即 Skip（`:68-75`） | 加環境變數分支（3 行） |
| `HandlerFacingWordingUiTests` 範圍 | 只掃 js pages／core 字串字面量（白名單 4 檔） | 擴到 cshtml 與信件字串 |

### 批次 L：文件現況重量

敘事字眼（原本／後來／改為／取代／第 N 輪／回饋 N／體檢輪／本輪／上一輪）：WEB-SPEC 63、BACKLOG 52、README 10、RULES-SPEC 9、DB-SPEC 9、DETECTION-SPEC 7、CLAUDE.md 7（多為規則本文引用）、LINUX-RULES 2、DESIGN-SYSTEM 2、其餘各 1。BACKLOG 50 個 `##` 中 10 個以「回饋 N 輪遞延」命名。README「後續方向」「文件地圖」兩章與 CLAUDE.md 文件地圖重疊；DB-SPEC `:517-518`「EF Core migration 記錄版本」「檔案端 120 天輪替」為過時句。

## 2. 定案（待使用者確認；標「預設」者未確認時依此執行）

| # | 待決 | 定案 |
|---|---|---|
| 1 | 範圍 | 預設：41 項一輪五批，順序 P→N→O→M→L；每批驗證後 commit＋push |
| 2 | CSP 模式 | 預設：`Server:Csp` 三態 `Off`／`ReportOnly`／`Enforce`；**未設定時：Development 環境 `ReportOnly`、其餘 `Enforce`**；`AppSettings.Validate` 只收三值（仿 FrameOptions），拼錯啟動失敗；回報端點兩模式都收；`frame-ancestors` 由 `Server:FrameOptions` 推導（DENY→`'none'`、SAMEORIGIN→`'self'`、空→省略指令）。替代方案「預設 ReportOnly 一版觀察期」：優點是零風險，缺點是多一輪才有保護；本輪補的兩條守門（lib CSS `data:` 掃描、輸出 HTML 每段 inline script 都有 nonce）已把審查抓到的兩個阻擋級問題變成測試可抓，故建議直接 Enforce |
| 3 | P-6 狀態碼 | 預設 409，以 `throw new DomainException(code, msg)` 走既有過濾器（不另設 `Response.StatusCode`） |
| 4 | N-6 | 預設：只做 `LastWeeklyCheckupDate` Max 下推；OR 條件留 BACKLOG 附實機 SQL |
| 5 | N-10 | 預設：**不做自動釋放**；順手修 `ReservedKeys` 只算未逾時者，健康頁帳本分項補一句「寄送中／不確定的項目不會自動釋放，因為釋放會重複寄信」 |
| 6 | N-2 退避上限 | 預設 **30 秒**（版本戳喚醒涵蓋所有使用者動作，上限只影響背景來源與多行程） |
| 7 | N-3／N-8／N-11 | 預設明確不做（§5） |
| 8 | 無斷言測試 | 預設：真無斷言者補斷言；只驗「不擲例外」者改 `var ex = Record.Exception(...)` ＋ `Assert.Null(ex)` 並註解意圖 |
| 9 | 敘事守門白名單 | 預設：CLAUDE.md「文件紀律」段規則本文、README「升級注意事項」章、BACKLOG 內指向 `docs/archive/` 的連結行、HELP-WRITING-GUIDE 禁詞清單本文；BACKLOG 改主題式分節（條目保留、刪輪次標題） |
| 10 | HelpWritingGuide 字詞守門擴到 `.ai.md` | 預設不擴（AI 參考版允許實作名詞） |
| 11 | DB-SPEC 欄位級範圍 | 預設：L-2 補齊 24 張表的欄位區塊（沿用既有偽 DDL 格式），M-2 守門涵蓋 24 張；替代：只守既有 9 張 |

## 3. 批次與改動

### 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| P | P-1～P-7 | 中 | 無 |
| N | N-1、N-2、N-4、N-5、N-6、N-7、N-9、N-10 順手項 | 中 | 無 |
| O | O-1 nonce 與標頭、O-2 回報端點、O-3 文件 | 中 | 先於 M-8 |
| M | M-1～M-14 | 大 | M-2／M-3／M-4／M-5 先紅再由 L 修綠 |
| L | L-1～L-4 | 大 | M |

### 批次 P：遞延 bug

**P-1 逾時 ≠ 取消（Orchestrator＋NetIQ 管線）**
- 契約：`AnalysisOrchestrator.cs:750／756／1175`、`NetiqPipelineService.cs:243（用 serverCt）／527／642／807`、`LogAnalysisService.cs:741` 改為「ct 已取消的 OCE 才走取消分支，其餘走原本的失敗分支」。逾時在各站點的結果：Orchestrator → 內層 `Fatal` 寫入、外層走 `catch (Exception)`，`FailureMessage` 改組成中文「呼叫外部服務逾時：{原訊息}」（.NET 原文是英文）；NetIQ 243 → 該 Sentinel 記 Warning＋`AddFailed(targets.Count)`，其餘 Sentinel 續跑；527／642 → 帳本記 `failed` 而非 `stopped`；807 → 主機日 `failed`；741 → `Log.Error` 回 null。`LogAnalysisService.cs:165／494` **不改**（純 DB 降級路徑）。ct 真被取消時全部路徑與現況相同（與 `SchedulerHostedService.cs:469-477` 的 outcome 處理一致）。
- 驗收：(T) 新 `CancellationSemanticsTests`：替身讓 Sentinel 查詢擲 `TaskCanceledException`（ct 未取消）→ `NetiqPipelineResult` 該 Sentinel 計入失敗、帳本 `failed`、其餘 Sentinel 有被查詢；Orchestrator 以替身管線擲 `TaskCanceledException` → `BatchRunRecorder` 替身收到 `Fatal`、`FailureMessage` 含「逾時」且不含「使用者取消」；ct 已取消重跑 → 走取消分支（帳本 `stopped`、訊息「使用者取消」）。(M) 任一站點改回舊式 → 對應案紅。(G) `rg "when \(ex is not OperationCanceledException\)" LogForesight.Core/Service/AnalysisOrchestrator.cs LogForesight.Core/Service/NetiqPipelineService.cs` ＝ 0；`rg "catch \(OperationCanceledException\)$" LogForesight.Core/Service/AnalysisOrchestrator.cs LogForesight.Core/Service/NetiqPipelineService.cs` ＝ 0。定向：`FullyQualifiedName~NetiqPipeline|FullyQualifiedName~LogAnalysisService|FullyQualifiedName~CancellationSemantics|FullyQualifiedName~BatchRun`。

**P-2 鏡像重讀補跑**
- 契約：in-flight 期間再呼叫 `refreshPrtgMirror()` 記「髒」並回傳「in-flight 結束後補跑一次」的 promise；補跑期間再被呼叫同樣合併到同一個補跑 promise，不疊加第三趟；in-flight 那趟的結果若已標髒則**不渲染**（避免舊值閃一下）；in-flight 失敗仍補跑；補跑完成後 `scopeRevision` 等於補跑那趟讀到的值。
- 驗收：(T) `prtg-mirror-refresh.behavior.mjs` 新案三件：第一趟未回時呼叫第二次 → api 替身恰兩輪、畫面與 revision 為第二輪值、第一輪結果未渲染；第一趟未回時連叫三次 → 仍兩輪且三個 promise 同時 resolve；第一趟失敗 → 仍補跑。(M) 拿掉髒旗標 → 第一案紅。定向：`FullyQualifiedName~PrtgMirrorRefresh`（現 6 條）。

**P-3 AI 歸納世代與失敗提示**
- 契約：點擊時記下當下查詢字串；回應抵達時查詢字串已變 → 不寫入、不 toast；非 429 失敗 → 不清摘要區，在摘要區下方顯示一行中性文字「這次歸納沒有成功，請稍後再試」（`textContent`），下一次成功或篩選變更即清掉。
- 驗收：(T) `records-search.behavior.mjs` 新案：按下歸納後改篩選再讓回應回來 → 摘要區空；非 429 錯誤 → 摘要區保留舊內容且多一行提示；429 → 既有「AI 暫時休息」案維持。(M) 拿掉世代比對 → 紅。定向：`FullyQualifiedName~RecordsSearch`（現 9 條）。

**P-4 排程狀態一次快照**
- 契約：`HealthService.GetStatus`（含 `LatestActivity`）與 `PrtgBackfillService` 兩處改讀一次 `Snapshot()`；輸出欄位不變。
- 驗收：(T) `HealthServiceTests` 新案：可控 `SchedulerRunState` 在讀取中途 `EndRun` → `AnalysisRunning`／`AnalysisTrigger`／`AnalysisStartedAt`／最新活動四欄來自同一快照。(G) `rg "_runState\.(IsRunning|Trigger|StartedAt|LastOutcome|LatestActivity)" LogForesight.Web/Services/HealthService.cs` ＝ 0、`rg "_schedulerRunState\.(IsRunning|StartedAt)" LogForesight.Web/Services/PrtgBackfillService.cs` ＝ 0。定向：`FullyQualifiedName~HealthService|FullyQualifiedName~SchedulerRunSnapshot`。

**P-5 清除作業專屬例外（含範圍寫入閘門）**
- 契約：Core 新增 `sealed class PrtgScopePurgeBusyException : Exception`（訊息透傳；仿 `ConcurrentUpdateException` 形狀）；`PrtgScopePurgeStore.cs:112／119／235` 與 `EnsureScopeMutationSlotFree`（`:463`）改擲它；`ApiExceptionFilter` 加一個 arm → 409／`conflict`；`PrtgScopePurgeJobsController.cs:77／135` 兩個字串比對 catch 刪除（其餘 `InvalidOperationException` → `validation` 維持）；`PrtgHostMappingAdminController:336` 的特例若只為此訊息而存在則一併簡化。
- 驗收：(T) `ApiExceptionFilterTests` 新案：擲 `PrtgScopePurgeBusyException` → 409＋`conflict`＋原訊息；(T) 新 `PrtgScopeWriteWhilePurgingTests`：清除作業 active 時，IP 排除新增、人工對應新增、主機儲存三個端點各回 409（不是 500）；既有 `PrtgScopePurge*` 18 條綠。(G) `rg "另一個範圍外清除作業|監看範圍正在清除中" LogForesight.Web` ＝ 0；`rg "new InvalidOperationException\(\"(另一個範圍外|監看範圍正在)" LogForesight.Core` ＝ 0。定向：`FullyQualifiedName~PrtgScopePurge|FullyQualifiedName~ApiExceptionFilter|FullyQualifiedName~PrtgHostMapping|FullyQualifiedName~PrtgScopeWrite`。

**P-6 帳本無效回 409**
- 契約：`ApiErrorCodes` 表改 409；`ScheduleController.cs:244／254` 改 `throw new DomainException(ApiErrorCodes.NetiqAttemptLedgerInvalid, msg)`；前端不改（已驗證顯示路徑相同）。
- 驗收：(T) `ScheduleRunPreviewNetiqAttemptTests` 兩案改為 `Assert.Throws<DomainException>` 且 `Code` 相符；(T) `ApiErrorCodeGuardTests` 新案「表中不得有 200」；`ApiExceptionFilterTests` 既有 DomainException 案覆蓋狀態碼。(G) `rg "\] = 200" LogForesight.Web/Models/ApiResponse.cs` ＝ 0。定向：`FullyQualifiedName~ScheduleRunPreview|FullyQualifiedName~ApiErrorCode|FullyQualifiedName~ApiExceptionFilter`。

**P-7 最新日對應外層算一次**
- 契約：`AssessPage` 增加可選參數接收預先算好的最新日對應結果；`PrtgDailyPipeline.cs:508／568` 兩個分頁迴圈與 `PrtgValueReadiness.cs:41` 在迴圈外算一次傳入；其餘 5 個呼叫端不傳、行為不變。不新增介面。
- 驗收：(T) `PrtgValueAssessmentTests` 新案：對應查詢替身計數，三頁評估只查一次；不傳參數時每頁一次（維持舊行為）；既有 14 條綠。(M) 外層改回每頁 → 計數案紅。定向：`FullyQualifiedName~PrtgValueAssessment|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgValueReadiness`。

### 批次 N：效能

**N-1 新鮮度快取（控制器層，沿用 `SummaryCache`）**
- 契約：`HealthController.Freshness` 以 `SummaryCache.GetOrAdd("schedule-freshness", …)` 取得結果（鍵不含使用者；註解寫明內容與使用者無關、`AdminContacts` 在快取外填），**回傳前複製一份 DTO** 再填 `AdminContacts`；`ScheduleFreshnessService` 本身不快取（健康頁與郵件各自傳 `now`）。失效：版本戳（ack／設定儲存自動 Bump）＋30 秒 TTL。
- 驗收：(T) `HealthControllerTests`（命中數待定名）新案：連續兩次呼叫 → `GetRecentRuns` 替身只被叫一次；非 Maintain 使用者呼叫後再以 Maintain 使用者呼叫 → 後者 `AdminContacts` 為 null（共享實例未被污染）；`DataVersionStamp.Bump()` 後 → 重算；可控時鐘過 31 秒 → 重算。(M) 拿掉複製 → 污染案紅。定向：`FullyQualifiedName~ScheduleFreshness|FullyQualifiedName~HealthController|FullyQualifiedName~SummaryCache`。

**N-2 CaseDaySync 退避＋版本戳喚醒**
- 契約：閒置時延遲 5→10→20→30 秒上限；每 5 秒檢查 `DataVersionStamp.Current` 是否變動，變動即執行並重設延遲；處理到資料時重設為 5 秒；保底：版本不變最多 30 秒也執行一次（含 `SweepClosures`）。不新增訊號型別、不改 Core。
- 驗收：(T) `CaseDaySyncTests` 新案（可控時鐘＋替身）：連續空轉 → 呼叫間隔序列 5／10／20／30／30；中途 Bump → 下一個 5 秒刻度即呼叫且間隔重設；有資料 → 連續呼叫無延遲；`SweepClosures` 每次執行都被呼叫。(M) 拿掉版本比對 → 喚醒案紅。定向：`FullyQualifiedName~CaseDaySync`（現 12 條）。

**N-4 位址解析單例**
- 契約：`PrtgAddressResolver` 改 `ConcurrentDictionary`；正快取 TTL 5 分鐘、負快取 60 秒、**逾時不寫快取**（`DnsLookupWithTimeout` 區分逾時與查無）、條目上限 10,000（超過時不寫入新項）、`Invalidate()`；Web 以 `IPrtgAddressResolver` 單例註冊，13 個 `new` 站點改注入；Core `PrtgDailyPipeline:217／321` 改由呼叫端傳入，其餘 Core 4 處（探測／守門一次性路徑）維持；`PrtgHostMapRefresher` 重新載入對應前呼叫 `Invalidate()`。不做每請求預算。
- 驗收：(T) `PrtgAddressResolverTests` 新案：同名第二次不呼叫 DNS 替身；查無 → 60 秒內不重查、61 秒後重查；逾時 → 下一次立即重查；32 執行緒並行同一組名稱不擲例外且 DNS 呼叫 ≤ 名稱數；第 10,001 個名稱不進快取。(G) `rg "new PrtgAddressResolver\(" LogForesight.Web` ＝ 0、`LogForesight.Core/Service/PrtgDailyPipeline.cs` ＝ 0。定向：`FullyQualifiedName~PrtgAddressResolver|FullyQualifiedName~PrtgHostMapping|FullyQualifiedName~PrtgScopeEstimate|FullyQualifiedName~HostAdmin|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgHostMapRefresher`。

**N-5 沿用說明鍵集分批**
- 契約：投影不含 `Note`；以（`UpdatedAt` 降冪、主鍵）鍵集每批 500 取回比對完整鍵，命中即以主鍵取一次 `Note`；比對器仍在 C#；結果與現行相同。
- 驗收：(T) `NoteReuseAndPhrasesTests`／`SqlIssueSignatureStorageTests` 既有綠；新案：1,201 筆噪音＋第 1,100 筆相符 → 查詢替身被叫 3 次、回傳正確；SQL Server `ToQueryString` 不含 `note` 欄（取 Note 那次除外）。定向：`FullyQualifiedName~NoteReuse|FullyQualifiedName~SqlIssueSignature|FullyQualifiedName~IssueNote`。

**N-6 週檢日期 Max 下推**
- 驗收：(T) `AnalysisRecordStoreContractTests` 新案：`ToQueryString` 含 `MAX(`；結果與既有案一致。定向：`FullyQualifiedName~AnalysisRecordStore`（現 58 條）。

**N-7 報告搬遷：投影＋keyset**
- 契約：`FromRecords` 投影只取 `RecordId／HostId／HostName／RecordDate／RiskLevel／CreatedAt／ContentJson`（不載 `OriginalRiskContentJson`），以 `RecordId > last` 每批 500、每批新 context 串流；`Exists` 擋重複維持。
- 驗收：(T) `ReportFileMigrationTests` 新案：1,201 列 → 3 批、全部搬到；中途取消 → 不發下一批；`ToQueryString` 不含 `original_risk_content_json`。定向：`FullyQualifiedName~ReportFileMigration`（現 11 條）。

**N-9 AI 快取上限**
- 驗收：(T) 新 `AiCacheStoreTests`：寫入 501 筆 → 最舊一筆不在；既有鍵覆寫不增筆數。

**N-10 順手：帳本 reserved 計數**
- 契約：`GetContributionLedgerStatus` 的 `ReservedKeys` 只算未逾 `PreparedMailReservationLifetime` 者；健康頁帳本分項補一句白話。
- 驗收：(T) `MailNotificationServiceTests` 新案：一筆 6 分鐘前的 reserved → `ReservedKeys` 0。定向：`FullyQualifiedName~MailNotificationService`（現 103 條）。

### 批次 O：CSP

**O-1 nonce、三態與標頭**
- 契約：每請求 nonce（16 bytes base64）放 `HttpContext.Items`，四段 inline script 加 `nonce`；`SecurityHeadersPolicy` 依 `Server:Csp`（未設定：Development→`ReportOnly`，其餘→`Enforce`）送 `Content-Security-Policy` 或 `-Report-Only`：`default-src 'self'; script-src 'self' 'nonce-{n}'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; frame-ancestors {推導，空則省略}; base-uri 'self'; form-action 'self'; object-src 'none'; report-uri {PathBase}/api/csp-report`；`Off` 不送。`AppSettings.Validate` 只收三值。
- 驗收：(T) `SecurityHardeningTests` 新案：三態各一（標頭名稱與有無）、未設定時依環境、FrameOptions 三值對 `frame-ancestors`（空→指令不存在）、PathBase 下 `report-uri` 帶前綴、同一回應內四段 `<script nonce>` 與標頭 nonce 相同且兩個請求 nonce 不同（`TestServer` 取登入頁與一般頁 HTML）；設定值錯誤啟動失敗 Theory。(T) 新守門 `CspPolicyGuardTests`：掃 `wwwroot/lib/**/*.css` 與 `wwwroot/css/*.css`，若出現 `url(data:` 則政策字串的 `img-src` 必含 `data:`；掃 `Views/**/*.cshtml` 每個無 `src` 的 `<script` 必帶 `nonce`。(G) `rg "<script>" LogForesight.Web/Views` ＝ 0。定向：`FullyQualifiedName~SecurityHardening|FullyQualifiedName~SecurityHeaders|FullyQualifiedName~CspPolicy|FullyQualifiedName~PathBase|FullyQualifiedName~LoginPageFallback`。

**O-2 回報端點**
- 契約：`POST /api/csp-report`：`[AllowAnonymous]`、`[SkipDataVersionBump]`；自行讀 body（接受 `application/csp-report`、`application/reports+json`、`application/json`），上限 8 KiB（超過 413 不記錄）；節流：把 `PerUserThrottle` 泛化為 `SlidingWindowThrottle<TKey>`（含 `MaxKeys` 上限，`PerUserThrottle : SlidingWindowThrottle<long>` 行為不變），以 `RemoteIpAddress` 為鍵每分鐘 10 筆（超過 429 不記錄）；同（`violated-directive`, `blocked-uri`）每分鐘最多記一筆；寫 NLog **Info**、logger 名稱 `LogForesight.Web.CspReport`（不進 `error.log`），欄位 `document-uri`／`violated-directive`／`blocked-uri`／`original-policy` 各截 512 字並移除換行；一律回 204；`CsrfHeaderMiddleware` 白名單此路徑；路由快照基準以 `LF_UPDATE_ROUTE_SNAPSHOT=1` 重產並逐列檢查只多這一列。
- 驗收：(T) 新 `CspReportControllerTests`：三種 content-type 各 204 且 log 替身收到一筆；超長 → 413 零紀錄；第 11 筆 → 429；同指令同來源第二筆一分鐘內不記錄；含換行的 `blocked-uri` 寫入後無換行；無 CSRF 標頭仍 204；匿名可達。(T) `PerUserThrottleTests` 既有綠；`ControllerRouteSnapshotTests` 綠且基準 diff 只一列。定向：`FullyQualifiedName~CspReport|FullyQualifiedName~ControllerRouteSnapshot|FullyQualifiedName~CsrfHeader|FullyQualifiedName~Throttle`。

**O-3 文件與升級注意**
- WEB-SPEC §5 補 `Server:Csp` 與 `TrustedProxies` 只接受單一 IP（叢集代理逐台列）；§8.1 inline script 例外改「四段皆帶 nonce，新增 inline script 必帶」；README 升級注意：CSP 預設強制與三值、嵌入需求改 FrameOptions、自訂品牌圖示仍可用、反向代理若自己也送 CSP 會取交集、未設 `TrustedProxies` 時回報節流全站共用一桶。

### 批次 M：守門測試

| ID | 內容 | 驗收（皆可機器判定） |
|---|---|---|
| M-1 DDL vs 模型 | 把 `SchemaUpgrader` 的 `private` DDL 常數改 `internal`、三張內嵌版抽成常數；新測試以 `UseSqlServer` 建 `ctx.Model`，對 20 張表比對 SQL Server DDL 的「欄名集合＋NULL／NOT NULL」（容忍行內多欄、`dbo.` 前綴、具名 DEFAULT）；型別不比 | 新 `SchemaUpgraderModelParityTests`（20 案 Theory）；(M) 在任一 DDL 多加一欄 → 紅 |
| M-2 DB-SPEC vs 模型 | 解析 DB-SPEC 偽 DDL 區塊（首行 `^lf_\w+\s+--`、欄位行 `^\s{2}([a-z_0-9]+)\s+\S+`、跳過 `PK (`／`UNIQUE (`／`FK`／`--` 延續行）；對 `ctx.Model` 24 張表**雙向**比對欄名集合＋可空性；文件沒有區塊的表直接紅 | 新 `DbSpecModelParityTests`；首次預期紅（15 張無區塊），L-2 修後綠；(M) 刪文件一欄 → 紅 |
| M-3 §xref 存在 | 掃 `Web`／`Core`／`Tests` 的 `*.cs|*.js|*.cshtml` 中 `(WEB|PRTG|DB|DETECTION|RULES)-SPEC(\.md)? ?§<id>`，`<id>` 取 `數字(.數字)*(字母)?` 前綴（`§14-#4`→`14`、`§9.9e-1`→`9.9e`），排除含 `docs/archive/` 的行；該文件須有標題 `^#{2,4} <id>[ .：]` 或 `^#{2,4} .*§<id>\b` | 新 `DocCrossReferenceTests`；首次預期紅（§14、§2.1、§11），L-1 修後綠 |
| M-4 敘事字眼 | 掃 `docs/*.md`＋`README.md`＋`CLAUDE.md`，禁詞＝CLAUDE.md 列的那組＋「本輪／上一輪／這一輪」；白名單照 §2-9 | 新 `DocNarrativeGuardTests`；首次預期紅，L-3 修後綠 |
| M-5 PatternId 數量 | `CorrelationPatternIdsTests` 補：DETECTION-SPEC、RULES-SPEC 與全部 `HelpContent/*.md` 中「N 種關聯」「共 N 個」「N 個模式」句型的 N ＝ `CorrelationPatternIds.All.Count`；不做通用數字掃描 | 既有類加一案；首次預期紅（三處 19），L-3 修 23；(M) 文件數字改錯 → 紅 |
| M-6 說明書標題同步 | 20 章 `.md`／`.ai.md`：H1 去掉「（AI 參考指引）」相等；H2 集合相等 | `HelpContentServiceTests` 加一案 |
| M-7 無斷言測試 | 19 個方法逐條補斷言或 `Record.Exception`＋`Assert.Null` | 精掃腳本進 `LogForesight.Tests/tools/noassert.py`，新 `TestHygieneTests` 以同邏輯（C#）掃描＝0 |
| M-8 中介層分支 | `ForwardedHeaders`：設定 `TrustedProxies` 後來自該 proxy 的 `X-Forwarded-For` 替換 `RemoteIp`，未知來源不替換，未設定時完全不處理；`MigrationGateMiddleware`：handling 遷移中非 GET → 503＋錯誤碼、GET 放行 | 新 `ForwardedHeadersTests`（3 案）、`MigrationGateMiddlewareTests` 補 handling 分支 |
| M-9 mini-dom | `innerHTML` setter 同時清 `children` 與 `_text`；`textContent` getter 不回傳 `_html` | 全部 `*.behavior.mjs` 逐案綠；`BehaviorTests` 154 條綠 |
| M-10 稽核常數 | `AuditActions.AccessDenied`；`PermissionAttribute` 改用；守門：`AuditActions` 每個常數在 `AuditQueryService` 中文對照表都有鍵，反之亦然 | 新 `AuditActionNamesTests`；(G) `rg "\"access_denied\"" LogForesight.Web --glob '!*Tests*'` 只剩對照表一處 |
| M-11 BrandProvider | 預設品牌、設定覆寫、空白圖示 → null、名稱截斷 | 新 `BrandProviderTests` 四案 |
| M-12 node 執行器 | `NodeRuntime.Run(workingDir, args, stdin?, timeout)` → `(exitCode, stdout, stderr)`，非同步並行讀兩個管線、逾時 Kill 程序樹並回傳失敗；`Probe` 也改用；20 個呼叫檔四種形狀全部改用（解析留在呼叫端）；守門：`LogForesight.Tests/*.cs` 除 `NodeRuntime.cs` 外不得出現 `ProcessStartInfo("node"` | 新 `NodeRuntimeTests`：故意不結束的腳本在逾時內失敗而非卡死；寫滿 stderr 的腳本不死鎖；(G) 守門 grep＝0；全部 behavior／UiTests 綠 |
| M-13 NodeTheory | `LF_REQUIRE_NODE=1` 且偵測不到 node → 失敗而非 Skip（屬性內 3 行） | `NodeRuntimeTests` 以環境變數替身驗兩態 |
| M-14 用詞守門擴充 | `HandlerFacingWordingUiTests` 加掃 `Views/**/*.cshtml`（白名單：`Prtg`、`Runs`、`Audit`、`UserDetail`）與 `Services/Mail/*.cs` 字串字面量 | 既有類加兩案；(M) 在處理人頁 cshtml 放「案件」→ 紅 |

### 批次 L：文件對齊

| ID | 內容 | 驗收 |
|---|---|---|
| L-1 §xref | 修 `IRuleSeedStore.cs:18`、`AnalysisOrchestrator.cs:255／274／332／500`、`BatchRunRecorder.cs:7` 與 M-3 其餘紅項（改引用或補標題，擇正確者） | M-3 綠 |
| L-2 DB-SPEC 欄位級 | 依 M-2 紅項補齊 24 張表欄位區塊（沿用既有偽 DDL 格式）；索引段對 `LfDbContext` 的 `HasIndex` 逐條；刪 `:517-518` 過時句；升級器行為一段對 `SchemaUpgrader` 類別註解 | M-2 綠；(D) 索引清單與 `HasIndex` 逐條比對結果寫執行紀錄 |
| L-3 敘事清理 | 依 M-4／M-5 紅項逐句改現況陳述或搬 archive（含三處「19 種」改 23）；WEB-SPEC 分四段由唯讀代理逐段對程式碼比對，主代理每段抽查 5 句 | M-4／M-5 綠；(D) 抽查紀錄 |
| L-4 README／CLAUDE／BACKLOG | README「後續方向」併入 BACKLOG、「文件地圖」改一句指向 CLAUDE.md；CLAUDE.md 基線與 `:102`「第 54 輪」句改現況；BACKLOG 由輪次標題改主題式分節（條目保留觸發時機），刪本輪做掉的 P／N 條目、新增 §5 遞延項 | (G) `rg "^## 回饋.*輪" docs/BACKLOG.md` ＝ 0；M-4 綠；(D) 刪除條目逐條對應本輪 ID |

## 4. 文件、說明書與 BACKLOG 同步（隨批）

- WEB-SPEC：§5（`Server:Csp`、`TrustedProxies` 單一 IP）、§7.2（`netiq_attempt_ledger_invalid` 409；`conflict` 新增清除作業佔用來源）、§8.1（nonce）、§9.4b 一句（逐日同步退避 30 秒＋版本戳喚醒）、§9.10（新鮮度快取在控制器層、30 秒）。
- PRTG-SPEC：§4 位址解析快取 TTL 與逾時不快取；§9b「最新日對應由管線外層算一次」；§3c 範圍寫入遇清除作業回 409。
- DB-SPEC：L-2 全部。
- 說明書：`10-scheduler.md` 補「告示最多延遲 30 秒」；AI 功能頁補「歸納失敗會提示」；三處「19 種」改 23；`12-settings` 健康頁帳本一句。
- README 升級注意：O-3 全部；`netiq_attempt_ledger_invalid` 狀態碼變更；執行紀錄中逾時從「已停止」改顯示為「失敗：呼叫外部服務逾時」。
- CLAUDE.md：「不要做」補「新增頁面 inline script 必須帶 nonce（`CspPolicyGuardTests` 守門）」與「Core 擲給 Web 分流狀態碼的錯誤用專屬例外型別，不以訊息字串比對」；收尾改基線。
- BACKLOG：刪本輪做掉的條目；N-6 OR、N-8、N-10、N-11、P-1 其餘 OCE 站點各留一條附本輪結論。

## 5. 明確不做（本輪定案）

- N-3 完整回填計畫重算：現況只算一次。
- N-8 三份 `Recheck` 合一：三條車道的設定檢查、風險集合、`withoutPrtg`、是否就地改 `views` 皆不同，合一要靠旗標，違反 KISS／SRP；「一次查窗口」會破壞逐收件人寄送前重核的時機。
- N-10 帳本自動釋放：`reserved` 已有 5 分鐘壽命；`attempted`／`unknown` 代表可能已進 SMTP，釋放＝重寄。若日後要解「帳本卡滿」，需加 `AttemptedAtUtc` 欄並配人工確認，另案。
- N-11 `BuildCasesForDay`（需量測）、`ReadAllRunLines` 回退（僅查舊於視窗的編號時發生、已有防護）、`CandidateArrayKeys`（需實機外層鍵）。
- N-6 `OwnedRows` OR 改寫：需實機查「`host_id` 不同但同名」列數。
- N-4 每請求 DNS 預算：預算已在 `PrtgResourceGuardTargets`，放進單例會互扣。
- P-1 `LogAnalysisService.cs:165／494`（純 DB 降級路徑）；PRTG 側 `PrtgDailyPipeline`／`PrtgScopeDevices`／`PrtgStructureSyncRunner`／`PrtgSnapshotHostedService` 的無條件 `catch (OCE)`（`PrtgClient` 已把 ct 未取消的逾時轉成 `PrtgClientException`，實際不會誤觸）；`NetiqDiscoveryService.cs:133`（`NetiqDirectoryClient` 已區分預算逾時與使用者取消）。
- M「每端點都有授權」：`ControllerRouteSnapshotTests` 已覆蓋。M-1 型別比對：第一版只比欄名與可空性。
- HelpWritingGuide 字詞守門擴到 `.ai.md`。
- CSP `report-to`／`Reporting-Endpoints` 雙軌：`report-uri` 兩大瀏覽器仍支援。

## 6. 驗收篩選詞命中數（dev@fe241168 `--list-tests`，Theory 算一條）

HealthService 10、ScheduleFreshness 8、CaseDaySync 12、PrtgAddressResolver 11、AnalysisRecordStore 58、ReportFileMigration 11、MailNotificationService 103、PrtgScopePurge 18、ScheduleRunPreview 4、ApiExceptionFilter 21、ApiErrorCode 4、PrtgMirrorRefresh 6、RecordsSearch 9、NetiqPipeline 61、LogAnalysisService 35、SecurityHeaders 10、PathBase 42、Audit 20、HelpContent 10、HelpWritingGuide 3、HelpNavigation 1、CorrelationPatternIds 1、SchemaUpgrader 51、ControllerRouteSnapshot 3、FrontendConsistency 43、HandlerFacingWording 3、JsModuleImport 1、BehaviorTests 154、ApiTimeout 28、UiTests 240、PrtgValueAssessment 14。
補跑確認：NoteReuse 12、SqlIssueSignature 5、PerUserThrottle 9、Throttle 20、BatchRun 48、SchedulerRunSnapshot 4、PrtgDailyPipeline 70、PrtgValueReadiness 7、LoginPageFallback 4、SecurityHardening 18、SummaryCache 6。
**命中 0、規格送出前要以實際類別名再跑一次或新建**：HealthController（N-1 測試改掛在 `ScheduleFreshness` 或新建）、PrtgHostMapRefresher、AiCache、MailNotifyState、CsrfHeader、MigrationGate、ForwardedHeaders、Brand、CancellationSemantics、CspPolicy、CspReport、PrtgScopeWrite、DocCrossReference、DocNarrative、SchemaUpgraderModelParity、DbSpecModelParity、TestHygiene、NodeRuntime、AuditActionNames（後 13 個為本輪新建，寫完後回頭跑一次篩選）。

## 7. 規劃完成後複檢

- **與既有設計衝突**：P-1 不動附掛降級路徑，與「附掛失敗只降級」一致；P-5 新例外型別遵循「業務錯誤走單點過濾器」慣例並消除字串比對；P-6 走 `DomainException`，`DataVersionStampPolicy` 對 4xx 不 Bump、GET 本就不 Bump；N-1 沿用 `SummaryCache` 符合 CLAUDE.md「有版本戳的快取仍保留短 TTL」且鍵不含使用者需註解理由；N-2 不改 Core、不新增訊號；N-4 單例的 TTL／上限對應「外部亂值不得讓快取無界」；O 的 `img-src data:` 與 `SystemSettingsService` 只放行 png／jpeg 的驗證相容；O-2 泛化節流器保留 `PerUserThrottle` 行為。
- **批次間同檔**：`ApiResponse.cs`（P-5 不動、P-6 改表）→ `ApiErrorCodeGuardTests` 一次處理；`ApiExceptionFilter.cs` 只在 P-5 改；`Program.cs`／`AppSettings.cs` 只在 O 改；`HealthController.cs` 只在 N-1 改；`NodeRuntime.cs` 只在 M-12／M-13 改；文件守門（M-2～M-5）先紅是刻意，L 之前不得把守門註解掉或加白名單繞過——規格明寫「紅是預期，不得改測試」。
- **四個坑**：什麼算一個——P-2「一次補跑」＝in-flight 結束後恰一趟；N-2「一次喚醒」＝版本值與上次不同；O-2「一筆」＝一個 HTTP 請求（陣列格式的多筆報告算一筆節流、各自寫 log）。分母為零——M-2 文件無區塊的表直接紅而非跳過；M-5 文件完全沒寫數字時測試仍以「至少出現一次」守住，否則砍掉文件句子就能騙過。破壞性判準——N-4 上限達到時「不寫入新項」而非清空；N-9 淘汰只看 `CreatedAt`；L-4 刪 BACKLOG 條目要逐條對應本輪 ID。單向閘門——M-13 環境變數只讓 Skip 變失敗，不反向；O 三態可隨時切回 `ReportOnly`／`Off`。移除類——P-5 刪字串比對 catch 時要 grep `PrtgHostMappingAdminController:336` 的特例；N-4 刪 13 個 `new` 時 Core 建構式保留給測試；M-12 刪 20 檔各自的 Process 程式碼由守門 grep 兜底。
- **升級／既有資料**：README 四條（CSP、`TrustedProxies`、409、逾時顯示變更）；無 schema 變更；N-4 新快取只在記憶體。
- **複檢結論**：相對提案 v1，砍掉三個不該做的改動（N-8 合一、N-10 自動釋放、P-1 兩站點）、改掉兩個會造成實害的設計（N-1 服務層快取共享 DTO、N-4 負快取吞逾時）、補上兩個阻擋級前置（`img-src data:`、Development ReportOnly）與一條被漏掉的 500 路徑（P-5 `EnsureScopeMutationSlotFree`）、把 N-2 的新訊號換成既有版本戳、O-2 節流改泛化既有類別而非新造；每項都有至少一條可機器判定的驗收與一條突變。

## 8. 階段拆分與規格階段設計修正

| 階段 | 範圍 | commit 單位 |
|---|---|---|
| P-a | P-1、P-7（Core） | 一個 |
| P-b | P-4、P-5、P-6（Web＋WEB-SPEC §7.2） | 一個 |
| P-c | P-2、P-3（前端） | 一個 |
| N-a | N-1、N-2、N-4（Web＋PRTG-SPEC／WEB-SPEC 各一句） | 一個 |
| N-b | N-5、N-6、N-7、N-9、N-10 | 一個 |
| O | O-1～O-3 | 一個 |
| M-a | M-12、M-13、M-9（測試基礎建設） | 一個 |
| M-b | M-1、M-7、M-8、M-10、M-11、M-14（程式碼守門） | 一個 |
| M-c | M-3＋L-1、M-5、M-6（文件守門與修正同段，交出即綠） | 一個 |
| M-d | M-2＋L-2（DB-SPEC） | 一個 |
| M-e1 | M-4＋L-3 第一部分（敘事守門與清理） | 一個 |
| M-e2 | L-4（BACKLOG 主題式重排、README 收斂） | 一個 |
| L-3b | WEB-SPEC 事實比對（唯讀代理分四段：§1～§6、§7～§8、§9、§10～§12），主代理重驗後修 | 一個 |

規格階段對 §3 的修正（實作以本表為準）：

| 項 | §3 原寫法 | 修正 | 理由 |
|---|---|---|---|
| N-4 | Core `PrtgDailyPipeline:217／321` 也改由呼叫端傳入 | Core 6 處全部不動，只改 Web 13 處 | Core 六處都在夜間管線／探測／守門內每趟建一次，逐趟快取本來就合理；效能問題只在 Web 請求執行緒 |
| N-4 | `PrtgHostMapRefresher` 重新載入前 `Invalidate()` | 改在 `PrtgStructureSyncService` 使用者觸發的同步開始時 `Invalidate()` | 使用者修好 DNS 後按的是「同步結構與對應」 |
| N-2 | 「每 5 秒檢查版本」的實作方式未定 | 抽純判定類別 `CaseDaySyncSchedule`，迴圈每 5 秒一拍問它要不要執行；記下的版本值取「執行完成後」 | 測試專案沒有假時鐘套件；服務自己在 finally `Bump()`，取執行前的值會自我喚醒成 5 秒輪詢 |
| P-5 | 新例外 `sealed : Exception` | 改 `sealed : InvalidOperationException` | 既有背景服務與控制器的 `catch (InvalidOperationException)` 行為不變；Web 只在會把它轉成 400 的 catch 加 `when (ex is not …)` |
| M-6 | H1 相等＋H2 集合相等 | 只比 H1（AI 版去掉「（AI 參考指引）」或「（AI 檢索版）」後綴） | 實測 23 章 H2 全部不同——AI 版刻意以檢索結構重寫，強推 H2 相同等於重寫 23 份文件 |
| M-4 | 禁詞＝CLAUDE.md 那組＋本輪／上一輪／這一輪 | 守門只鎖「第 N 輪」「回饋 N 輪」「體檢輪」「審查輪」；「原本／後來／改為／取代」改為 L-3 逐句人工判讀 | 「本輪」在 WEB-SPEC 是郵件寄送輪次等領域用語；「改為／取代」大量描述現行行為（WEB-SPEC 48 處） |
| L-3 | WEB-SPEC 分段未定 | 分四段 §1～§6、§7～§8、§9、§10～§12 | WEB-SPEC 現行有 §1～§12（§9 頁面規格約 2,000 行單獨一段） |
| P-2 | 「補跑期間的呼叫合併進補跑、總共只多一趟」 | 每一趟進行中被呼叫就再跑下一趟（同一趟內多次呼叫合併），只渲染最後一趟 | 原寫法讓補跑期間完成的寫入同樣讀到舊快照，等於把原 bug 往後挪一趟；驗收補 `callDuringRerunTriggersThirdRound` |
| M-c／M-d／M-e | M 先紅、L 再修綠（分兩段） | 守門與對應文件修正同段提交 | 全域規則要求每個 commit 可獨立建置、驗證且全綠 |

## 9. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 規劃 | Fable 5.1 主代理＋兩個 Sonnet 唯讀審查 | d3457f9e | — | — |
| P-a | Sonnet 5.5 實作；Opus 5.5 主代理驗收 | 8672c45a | 定向 362 綠；主代理突變（外層取消分支）轉紅 | 規格只要求單批逾時案，代理補「整台 Sentinel 逾時」才讓 :243 的突變轉紅；`EfPrtgStore` sealed，改用 `SqlPerformanceMonitor` 計數 |
| P-b | Sonnet 5.5；主代理 | 35469192 | 定向 165 綠；過濾器 arm／狀態碼表突變轉紅 | 主代理補 `PrtgHostMappingAdminController` 批次對應遇清除忙碌顯示原訊息（代理白名單外）；P-4 用結構守門取代行為測試（`HealthService` 相依過多） |
| P-c | Sonnet 5.5；主代理 | e4c9ae1e | 前端定向 521 綠；三項突變轉紅 | **規格缺陷**：原契約「補跑期間的呼叫合併、只多一趟」會讓補跑期間的寫入同樣讀到舊快照；主代理改為逐趟迴圈並補 `callDuringRerunTriggersThirdRound`（見 §8 修正表） |
| N-a | Sonnet 5.5；主代理 | 35057c7a | 定向＋DI 接線 619 綠；五項突變轉紅 | `ScheduleFreshnessService.GetScheduleFreshness` 加 `virtual` 供計數（白名單外，規格允許）；代理結束時留背景計時器，主代理 TaskStop |
| N-b | Sonnet 5.5；主代理 | dfc09b22 | 定向 609 綠；五項突變轉紅 | N-6 `Max()` 為純量聚合無法 `ToQueryString`，改用 SQLite 命令攔截器斷言 `MAX(`；N-7 `>`→`>=` 為等價突變（`Exists` 擋重複），改用游標不前進；發現一個從未登錄的 behavior 案例 → 追加為 M-a 的登錄守門 |
| O | Sonnet 5.5；主代理 | 03a0a1bb | 核心 149 綠（代理聯集 961 綠）；四項突變轉紅 | 匿名控制器列入 `ControllerRouteSnapshotTests.PermissionExempt`（白名單外一行，守門本來就要求）；本機有軟體改寫 localhost 回應標頭，CSP 標頭測試改在行程內執行中介層 |
| M-a | Sonnet 5.5；主代理 | 5e15f2c0 | 前端 423 綠（改前 410，略過 0）；三項突變轉紅 | 登錄守門抓出 prtg-mirror-refresh 三個從未執行的案例（含上一輪宣稱突變轉紅的 `overlappingRiskReviewCallsRenderPendingListOnce`），補登錄皆綠 |
| M-b | Sonnet 5.5；主代理 | 44cbaca7 | 定向 403 綠；六項突變轉紅；DDL 45 段搬移前後逐字相同 | M-1 比對納入升級器 `AddColumnIfMissing`（四張表的欄位由補欄而來）；`noassert.py` 有誤判，C# 守門自行抹除字串與註解 |
| M-c | Sonnet 5.5；主代理 | e920d137 | 定向 41 綠；三項突變轉紅 | M-5 regex 改認「合計」避免抓到子計數；另抓到第四處「19 種」；主代理更正 PLAN 中「WEB-SPEC 只有 §1～§9」的錯誤 |
| M-d | Sonnet 5.5；主代理 | f4afd32d | 定向 79 綠；兩項突變轉紅 | 舊 DB-SPEC 對 `lf_daily_records`、`lf_record_handling` 的描述已與模型脫節（刪 8 補 4、整塊重寫）；主代理親核傳輸綁定三欄用途後改寫註解 |
| M-e1 | Sonnet 5.5；主代理 | 87536d6b | 文件守門 25 綠；突變轉紅 | 主代理核對三處推論句（`ParseDateRange` 366 天成立） |
| M-e2 | Sonnet 5.5；主代理 | a4591146 | 文件守門綠；重排前後條目標題比對 | 42→15 章；缺少的 16 條皆為預期刪除或合併（主代理逐條追到去處） |
| §4 補漏 | 主代理 | 0e2eee9b | 說明書與文件守門 18 綠 | 逐項對照 §4 清單時發現說明書三處、PRTG-SPEC 兩句、README 兩條、CLAUDE.md 規則尚未落地；「排程告示延遲」寫進系統健康章（新鮮度所在處）而非排程章 |
| L-3b | 五個 Sonnet 唯讀比對＋Sonnet 改檔；主代理 | 74a118ff | 文件守門 26 綠；過時名稱 grep 歸零 | 47 條全部成立；主代理抽查「錯」類皆屬實，另補兩處白名單外的過時註解（`CategoryAggregatorTests`、`LfDbContext`） |

## 體檢交接

- 全量（主代理，Windows，所有段落提交後）：**7,524 通過／0 失敗／10 略過**，較基線 7,357／10 增加 167 條，略過數不變（規模壓測）。
- 規劃逐項比對：§3 P／N／O／M／L 與 §4 文件同步逐項對照，§4 有說明書三處、PRTG-SPEC 兩句、README 兩條、CLAUDE.md 規則在逐段實作時未落地，已於 0e2eee9b 補齊；其餘皆有對應 commit（見 §9）。
- 規格階段修正 9 條記於 §8；實作中最重要的一條是 P-2 原契約本身有缺陷（補跑期間的寫入仍會讀到舊快照），已由主代理改正並補測試。
- 本輪新增的守門會在日後持續擋退化：behavior 案例必須登錄、測試必須有斷言、不得直接起 node、文件章節引用存在、DB-SPEC＝模型、DDL＝模型、現行文件無輪次字樣、inline script 帶 nonce、lib CSS 用 data: 時 img-src 放行、稽核常數有中文名、處理人可見頁無「案件」。
- 尚未做：換模型體檢；瀏覽器實際操作（CSP 強制下各頁是否有被擋的資源——已以原始碼守門與 grep 覆蓋，未開瀏覽器；PRTG 維護頁補跑、AI 歸納提示以 behavior 測試覆蓋）；CLAUDE.md 基線數字（收尾時更新）。
- 已知限制：SQL Server 只驗 EF `ToQueryString` 翻譯形狀（N-6 的 MAX 以 SQLite 攔截器驗）；CSP 標頭測試在行程內執行中介層（本機有軟體改寫 localhost 回應標頭）；DNS 快取 TTL 與 CSP 回報節流未經實機驗證。
- 實作方自我體檢（Opus 5.5 主代理＋三個 Sonnet 5.5 唯讀代理分「後端正確性」「CSP 與前端使用者動線」「測試守門與文件」；發現逐條讀碼驗證後才改），修正 7 條，皆附突變或定向測試：
  1. `PrtgAddressResolver` 滿 1 萬筆後過期條目永不清除，快取永久停收新名稱、退化成每次查 DNS → 滿載時先清過期項（新測試，突變轉紅）。
  2. PRTG 維護頁 `refreshPrtgMirror` 在途旗標留給 `.finally` 清，結束到清除之間的呼叫會標髒卻拿到已結束的 promise、補跑遺失 → 與「沒被標髒」判定同步清除。
  3. CSP 回報只把 `\r\n` 換空白，其他控制字元（tab、NEL）仍進 web.log → 所有控制字元換空白（測試擴充，突變轉紅）。
  4. `SchemaUpgrader` 抽出 DDL 常數時插進 `WorkOrderActiveIssueFilter` 的 XML 註解與常數之間，註解貼錯對象 → 搬回。
  5. DB-SPEC 守門只查「模型有的表要有區塊」，文件殘留已刪表不會被抓 → 補反向檢查與突變案例。
  6. CLAUDE.md 寫「view／JS 不得有 `style=` 與 inline 事件屬性」卻沒有守門 → `CspPolicyGuardTests` 補一條（只認 HTML 標籤內的屬性、整檔比對涵蓋跨行標籤；突變轉紅）。
  7. 文件：WEB-SPEC 回報端點寫「接受三種 Content-Type」但程式不檢查 → 改成依本文形狀解析；補鍵數滿載時的行為；README 刪「舊版文件曾寫」沿革、補 CSP 出問題時先設 `ReportOnly` 再查 `LogForesight.Web.CspReport` 的退回步驟；PRTG-SPEC 同步解析快取滿載行為。
- 經驗證不成立或不改：`LogAnalysisService.cs:165／494`（§3 P-1 已定案純 DB 降級路徑不改）；記錄查詢翻頁丟棄進行中的 AI 歸納（翻頁本來就重查並清空摘要區，行為一致）；`AddColumnIfMissing` 解析失敗（會讓比對轉紅，非靜默）；CSP 回報節流每次掃描整個字典（沿用既有 `PerUserThrottle` 行為，上限 1 萬鍵）；匿名者灌滿回報節流鍵（只影響回報可見度，已寫進 WEB-SPEC）；`PrtgDailyPipeline` 候選為空時多查一次最新日對應（不影響正確性）。
- 自我體檢後定向（`PrtgAddressResolver|CspPolicyGuard|CspReport|DbSpecModelParity|SchemaUpgrader|DocNarrative|DocCrossReference|UiTests|JsModuleImport|BehaviorTests|ApiTimeout|PrtgMirror|HelpContentService|NodeRuntime`）：**557 通過／0 失敗**；未重跑全量（只動上述檔案，收尾併 dev 後跑一次）。
- 升級注意（README 已寫）：CSP 預設強制（Development 僅回報）、`Server:Csp` 三值、外部服務逾時改顯示為失敗、補跑預覽帳本不完整回 409、未設 `Server:TrustedProxies` 時 CSP 回報節流全站共用。

## 體檢輪修正

體檢方 Fable 5.1（實作與自查為 Opus 5.5）。三個 Sonnet 5.5 唯讀代理分區掃描（A 管理者操作流程＋後端對抗、B 使用者前端流程＋行為測試真偽、C 測試假守門＋文件稽核；共同規則要求每區最高三條附探針），規劃逐條比對由主代理親做；代理回報全部回到程式碼或探針重驗後才改。

**規劃比對**：§3 41 項與 §4 清單皆有對應實作與測試。與 §3 文字不同、經判定可接受者：P-5 的 `PrtgScopeWriteWhilePurgingTests` 以 store 層 `Busy_slot_throws_dedicated_exception…`（主機／人工對應／日對應，體檢補 IP 排除）＋ `ApiExceptionFilterTests` 409 案取代——三個端點到過濾器之間沒有會吞掉 `PrtgScopePurgeBusyException` 的 catch（`EnsureScopeMutationSlotFree` 11 個呼叫點、Web `catch (InvalidOperationException)` 全數帶 `when` 或不在路徑上）；M-7 `noassert.py` 未入庫（誤判多，C# `TestHygieneTests` 是唯一來源）；§4 的「排程告示延遲 30 秒」與「帳本一句」都寫在 `22-system-health`（新鮮度告示與帳本告示所在頁），`10-scheduler`／`12-settings` 不重複。

**修正（每條附迴歸測試或突變結果）**：
1. `PrtgAddressResolver`：`Invalidate()` 只清字典，清空前就卡在 DNS 的查詢回來後把修好前的結果寫回（正快取 5 分鐘）→ 加世代號，查詢開始後若被 Invalidate 就不寫回；新測試 `Invalidate_發生在DNS查詢進行中_該次結果不寫回快取`（拿掉世代比對轉紅）。
2. `CaseDaySyncHostedService`：沒處理到資料那趟記的是執行後版本，執行期間別人的 HTTP 寫入連同被吞掉，待辦最多多等 30 秒 → 沒處理到時記執行前的版本（有處理到仍記 Bump 後的值，避免自我喚醒）；`CaseDaySyncSchedule` 註解同步。服務層無測試，以狀態表核對（探針 p4.mjs：序列 5,10,20,40 → 5,10,20,25）。
3. `Program.cs`：`report-uri` 用 `PathBase.Value`，子 Application 路徑含非 ASCII 時標頭值含非 ASCII、Kestrel 在 `OnStarting` 擲例外 → 改 `ToUriComponent()`（與 `_Layout` 同一形式）；`SecurityHeadersTests` 既有案綠。
4. `SlidingWindowThrottle`：每次 `TryAcquire` 在鎖內對全部鍵做 LINQ 掃描，匿名的 CSP 回報端點鍵數可到 1 萬、單一請求最多約 157 個違規各掃一次 → 改為每個視窗最多掃一次或鍵數已滿時掃；`PerUserThrottleTests`／`LoginThrottleTests` 綠。
5. `CspReportController.Clean`：只換控制字元，U+2028／U+2029 與 bidi 等格式字元仍可混入日誌行 → 一併換空白；測試擴充（拿掉 Format 類別轉紅）。
6. `records.js` AI 歸納：429 提示沒有 `data-role` 且會清掉舊摘要，429 之後再失敗會疊兩行 → 429 與其他失敗走同一個提示出口（保留舊摘要、同一列覆寫）；mini-dom 的 `querySelector` 補 `[name="value"]`、`remove()` 真的脫離父節點（原本是空函式，去重從未被測到）；新案 `aiSummary429ThenFailureShowsSingleNoticeAndKeepsPrevious`（改回 `replaceChildren` 轉紅）；說明書 `03-issues` 提示位置（結果表上方）與節流行為同步。
7. `CspPolicyGuardTests`：nonce 只驗屬性存在，寫死字串也綠 → 值必須是 `@Context.GetCspNonce()`（把一處改成 `nonce="x"` 轉紅）。
8. 規劃驗收寫了但實作輪缺的測試補齊：`ReportFileMigrationTests` 已取消 token 不搬任何列；`HealthControllerFreshnessCacheTests` 版本不變、TTL 到期重算（`SummaryCache` 可注入時鐘）。
9. `records-search.behavior.mjs` 檔頭 BOM 累積到三個（dev 上兩個）→ 一個。
10. 現行文件沿革句改成現況陳述 14 處：`DESIGN-SYSTEM`（本輪不做）、`WEB-SPEC`（本輪範圍界定、規則改為、同時自 DB-SPEC 移除、與過去完全相同、過去沒有頂層 catch、過去只在風險日詳情、同輪把…改走 IRunConsole）、`RULES-SPEC`（console 退場後）、`DB-SPEC`（過去正是漏在這裡）、`DETECTION-SPEC`（過去的無閘門設計）、`BACKLOG` 三處「體檢」、`README`（批次退場後）。守門 regex 維持 §8 的定案（只鎖輪次字樣），這些字眼由人工判讀。

**查證後不改**：批次對應遇清除忙碌回 200＋失敗訊息（批次端點本來就是部分成功語意，單筆才 409）；逐日同步連續例外退避到 30 秒（避免 5 秒狂打，PLAN N-2 定案）；`SchedulerHostedService` 取消時以英文例外訊息當結果、逾時訊息含英文原文（既有行為，非本輪引入）；`Runs` 頁被用詞守門排除（Dev／Admin 角色可見，不是處理人頁）；`TestHygieneTests` 判定寬度、`DocCrossReferenceTests` 不比 `§8.6-8` 子項、`refreshPrtgMirror` 在途旗標空窗無法以行為測試重現且後兩個 `isStale` 檢查點未被案例走到、CSP 回報節流 1 萬鍵可被灌滿只影響回報可見度——皆屬低風險，不另立項。

體檢後定向（`PrtgAddressResolver|CspPolicyGuard|CspReport|ReportFileMigration|HealthControllerFreshness|CaseDaySync|Throttle|SecurityHeaders|SecurityHardening|UiTests|JsModuleImport|BehaviorTests|ApiTimeout|NodeRuntime|HelpContentService|DocNarrative|DocCrossReference|LoginThrottle|PrtgScopePurgeStore`）：**604 通過／0 失敗**；四條新斷言各做一次突變皆轉紅、備份還原並 touch。全量在併 dev 後跑（見終檢輪）。

## 終檢輪

- 併 dev（`--no-ff`，dev 自分支起點未移動、合併後樹與分支逐位元組相同）後全量 25 分鐘：7,528 通過／2 失敗／10 略過。兩條失敗都不是程式問題：(1) `HelpWritingGuideTests` 抓到體檢改寫的說明書句子用了禁詞「原本」→ 改「先前」；(2) `PrtgAddressResolverTests` 32 執行緒並行案斷言「DNS ≤ 20 次」在全量負載下不成立（冷快取下同名同時未命中的次數取決於排程時機，實作輪單跑過、全量下翻車）→ 改成先填滿快取再並行、斷言 DNS 次數不增加（確定性）。兩處都是測試與說明書文字，生產樹與全量跑過的那棵相同，只重跑定向 37 綠、不重跑全量；基線以 **7,530 通過／0 失敗／10 略過** 記（較實作輪 7,524 多 6 條＝體檢補的測試；較上輪基線 7,357 多 173 條）。
- 體檢修正 commit（a84b9494）單獨再掃：解析器世代號以 `Volatile.Read`／`Interlocked.Increment` 配對、`Resolve` 在快取未命中後才取世代；逐日同步版本基準兩種取法與 `ShouldRun` 的喚醒條件對得上（版本變動那拍沒處理到就記該版本，不會自我喚醒）；節流每視窗掃一次在鍵數飽和且全部存活時退回每次掃（與原行為相同，只在飽和時）；`ToUriComponent()` 對空 PathBase 回空字串、既有 `report-uri /api/csp-report` 斷言不變；mini-dom 的 `_parent` 只在替身內部使用。終檢無新發現。
- 文件稽核：文件守門（敘事、§引用、說明書 H1）綠；CLAUDE.md 基線改 7,530／10；PLAN 歸檔並補 `docs/archive/README.md` 索引。
