# LogForesight 第 60 輪規劃：冗餘、死碼、結構、韌性（＋核對抓到的 bug）

> 狀態：**規劃中（定案版 v3：經使用者／管理者／程式面三視角審查與上帝視角複檢後修訂；等使用者確認後拆段寫規格；未開分支、未動程式）**
> 基準：dev@da1efddc（6,905 通過／10 略過，Windows）
> 來源：第 59 輪全專案體檢的草案 §4（H／I／J／K 四批），加上本輪核對時抓到的 bug 與設計問題
> 實作方式：Opus 5.5 主代理寫規格與驗收；改檔代理依複雜度用 Sonnet 5.5／Haiku 5.5（同第 59 輪），一個 checkout 一次只派一個改檔代理
> 核對方法：第 59 輪體檢的 12 份原始 findings 取出 48 條，分 5 組由唯讀 Sonnet 代理逐條對 dev@da1efddc 重核；主代理親核會改變處置的 12 條（§1 標 ★）；v3 再派兩個唯讀審查代理（使用者／管理者視角、程式面視角）審 PLAN，主代理逐條判定後併入（§2 定案表標 v3 者）
> 設計原則：不過度設計；同一件事只有一份（DRY）、一個單元一個理由改（SRP）、能沿用既有機制就不新造（KISS）——§2 有五條原本的抽象因此被砍掉或縮小

---

## 0. 總結與範圍

| 批次 | 條目 | 核對後 | 本輪處置 |
|---|---|---|---|
| Z 前置修正 | 核對新抓 bug 12 條＋六處小修 | 全部屬實（主代理親核 12 條） | 最先做，每條獨立 commit |
| H 刪死碼 | 15 | ✅9 ⚠️5 ❌0；A3-16 改保留 | 做 |
| I 收斂重複 | 15 | ✅7 ⚠️8；C2-06 不做基底、C3-17 降為純函式、C3-22 降 BACKLOG | 做（縮小範圍） |
| J 拆分與分層 | 6 | ✅3 ⚠️3；C1-08 只抽兩處 | 做（先補守門） |
| K 韌性 | 16 中的 9 條 | 見 §1 K 表 | 本輪做 K-1～K-6（CSP 移 61） |
| 61 效能 | K 其餘 7 條＋CSP | 核對結論已寫 §1 | 第 61 輪草案（§6） |

**範圍定案的理由**：使用者原則是「問題皆在本輪處理」。K 批裡「會讓使用者看到錯的結果或漏掉通知」的條目——OCE 記成 500、取數狀態交錯、重啟落在夜間取數、郵件帳本在 2000 台等級必然滿載並**漏寄**、AI 端點無每人配額、零安全標頭、`days` 無 Clamp、PathBase 魔法字串——都屬「問題」而非「優化」。真正可以等的是純效能（快取 TTL、輪詢退避、計畫重算、DNS 快取、索引型 schema 變更）與 CSP（沒有回報端點的 report-only 等於沒做，要和回報端點一起做），拆到 61 不是推遲問題，是避免把需要實機量測的題目塞進一個已經 50＋改動點的輪次。

---

## 1. 核對表（主代理親核 ★／代理核對）

### Z 本輪核對新抓到的 bug（全部 ★ 親核，皆成立）

| # | 問題 | 證據 | 嚴重度 |
|---|---|---|---|
| Z-1 | `GET api/admin/settings/prtg-host-map` 沒有 `RequireFullPrtgTransferAccess()`，同群 unmapped-hosts／name-mismatch／devices/search／manual-map 都有。受限 Maintain（有 Maintain 無 ViewAll）可取得衝突列的 `CandidateHosts`（全站啟用主機 id／名稱／IP，未過可見範圍）。WEB-SPEC :357 明寫「全站 PRTG 對應…只問是否持有 ViewAll」 | SettingsController.cs:1073-1224；:1098-1115 | **高（授權）** |
| Z-2 | 資源守門預覽：內層 `catch (OCE) when (ct.IsCancellationRequested) { throw; }` 被外層 `catch (Exception ex)` 接住，回 `Success=false`「解析受監看目標失敗」；同方法第二個 try（:768-771）是裸 `catch (OCE) { throw; }`，HttpClient 逾時會穿出 | SettingsController.cs:680-712、:768-771 | 中 |
| Z-3 | 記錄查詢：`records.js` 在所有視角都送 `overdue`／`unassigned`／`statuses` 並顯示「只看逾期」等可移除標籤；`ByHost`／`ByDate` 的 `BuildRequest` 沒有這三欄、`ByIssue` 沒有 `overdue`——畫面宣稱篩選生效、實際忽略 | RecordsController.cs:77-165；records.js:383-401、:437-444 | 中（使用者可見） |
| Z-4 | `PrtgAddress.Normalize` 用寬鬆 `IPAddress.TryParse`：`10.1`→`10.0.0.1`、`12345`→`0.0.48.57`、`0x7f.1`→`127.0.0.1`，PRTG device 的簡寫／純數字短名稱被當合法 IP，錯對應或假衝突，且不走名稱退路。`NetiqHostList.IsValidIp` 只數「3 個點」，`0x7f.0.0.1` 仍過——兩份規則分岔且各有漏洞。同型：`PermissionChangeService.cs:687`、`SentinelEventMapper.cs:201`、`PrtgProbeRunner.cs:268`（繼承） | PrtgAddress.cs:14-35；NetiqHostList.cs:86-97 | 中（正確性） |
| Z-5 | `ApiExceptionFilter` 對任何 `OperationCanceledException` 回 500＋`Log.Error` 完整堆疊 | ApiExceptionFilter.cs:21-58 | 中 |
| Z-6 | `ScheduleController.GetStatus` 逐屬性讀 `SchedulerRunState`、`IsRunning` 讀兩次（:139、:166）；`DashboardController.RunActivity`（:80-110）取數分支同型（AI 分支已用 `Snapshot()`）；取數側沒有 `Snapshot()` | ScheduleController.cs:125-205；DashboardController.cs:80-110；SchedulerRunState.cs getter 無鎖 | 中 |
| Z-7 | `fetch-followup` 在全站橫幅、排程頁、Runs 頁都以原字串顯示；`RunActivityBannerTests:49` 把原字串當預期（假守門） | RunTriggerText.cs:14-20；RunMonitorService.cs:419-425；layout.js:687、runs.js:478/1166 | 中（使用者可見） |
| Z-8 | PRTG 數值趨勢：只有磁碟評估失敗標 `Partial`＋note，記憶體／CPU／Ping 失敗只 Log 仍 `Success`；失敗路徑零測試 | PrtgDailyPipeline.cs:524-532 vs :592-600 | 中 |
| Z-9 | 最新日主機對應退路只在管線 `ResolveHostMapRows`，`PrtgValueAssessment.DeviceToHostOn`／`AssessPage` 沒有：最新日整天 unmatched 時狀態型 finding 能歸戶、數值就緒度卻 0 | PrtgDailyPipeline.cs:349-361；PrtgValueAssessment.cs:213-223、:258 | 中 |
| Z-10 | `/api/ai/today-focus` 的 `days` 沒 Clamp（正式入口 `DashboardController:35` 有 `Clamp(1,90)`） | AiController.cs:118 | 中 |
| Z-11 | PRTG 維護頁 `refreshPrtgMirrorCore` 三請求共用一個 try/catch，任何失敗都顯示成「載入 IP 排除清單失敗」；`risk-review/pending` 沒帶 `silent:true` 雙重提示 | prtg-admin.js:2143-2205、:2166 | 低（UI） |
| Z-12 | `TopIssueBackfiller` 就緒旗標只在記憶體，整個 `Run()` 排在 `BackgroundWorkGate` 之後；`Run()` 開頭 `Volatile.Write(0)` 倒退；`RunRiskySourceKeys` 每次啟動兩次 `Count()` 全掃；`Run()` 擲例外後無重試 | TopIssueBackfillHostedService.cs:35-77；BackgroundWorkGate.cs:52-55；TopIssueBackfiller.cs:40-60、:104-139、:188-192 | 中 |

六處小修（代理抓、主代理採信）：PermissionChangeMigration 的 `Bump()` 早於清理（:74 vs :85）；四支回填例外路徑不 `Bump`；`_currentUser` 可選卻無 null 檢查；`new HostStore(_backend.Blob("hosts"))` 繞過注入 **3 處**（SettingsController.cs:872、:1123、:1257）；`PrtgDailyPipeline.cs:385` 無效賦值；`PrtgSnapshotHostedService.WriteOutput` 對 `List` 無 lock（隨 H-1 的 A2b-13 消除）。**撤銷**：「`WebAiService` 吞 OCE」——該服務的方法沒有 ct 參數，HttpClient 逾時本就該降級，維持現狀。

### H 刪死碼

| ID | 判定 | 證據 | 補充／依賴方 |
|---|---|---|---|
| A2b-04 | ✅ | PrtgBackfillRunner.cs:170；非觸發分支 :249-270；唯一正式呼叫 PrtgBackfillService.cs:875 | 約 8 個測試改打觸發式；BACKLOG :412-413 刪；簽章 store／records 改必填 |
| A2b-05 | ⚠️ | `AssessStatus`／`BuildExportPackage` 正式零呼叫，78 處測試靠 `anchor`；`GetStatus` 的 128 MiB 預留被 CalibrationServiceTests:2877 斷言 | Core 已對 Tests `InternalsVisibleTo`（csproj:56）：兩方法改 `internal`＋註明；GetStatus 改 `Ok()` 並改 :2877 |
| A2b-06 | ⚠️ | Export／Import／`[NonAction] ImportPrtgData`／`PrtgImportResult`／`PrtgDataJsonOptions` 死；`PrtgDataPackage` 被兩個測試當舊檔 fixture；`CurrentFormatVersion`、`SourceHintFor` 有正式用途 | 保留 410 路由、`RetentionPruner.cs:171` 前綴清理、`AuditActions.PrtgDataImport`；DB-SPEC :830、BACKLOG :470；`PrtgDataJsonOptions` 在 SettingsController.cs:1695，**H-2 必須先於 J-2** |
| A2a-14 | ⚠️ | 11 個 readiness 符號死（`GetReadinessValues`／`GetReadinessMaps` 活）；`TryParseSourceStatus`、`beforePageRequest`＋`PrtgPageFenceRejectedException`、`TryBeginBackground`＋`BackgroundLease`、`WasAttached`、`RecordBaseline`、`TryGetRule`、`DrainBefore`／`DrainAll` 零正式呼叫 | `PrtgSqlServerIdScopeSourceTests:19` 改指向存活查詢且仍要有 IN 上限案；`_samplingCount`／`_attached` 隨之成只寫，整組收 |
| A2b-13 | ✅ | `ExecutionOutputs` 只寫、`Console` 從未指派；21 處測試讀 | 測試改讀 `Console` 縫；順帶消除 WriteOutput 競態 |
| B-17 | ✅ | BatchRunStore.cs:416 private 零呼叫 | 刪；:344 `ReadAllRunLines()` 回退無界記 BACKLOG |
| D1-10 | ⚠️ | `missingHourDate`、`renderIpExcludesLoading` 死；`refreshIpExcludes` 別名（2 呼叫端）；`categoryName` 零呼叫但 16 處繞過它 | `categoryName` 改為被採用（I-5） |
| C3-10 | ✅ | 試跑強制 `AutoDispatchEnabled=true`，`SkipDisabled` 分支不可達；DashboardServiceTests:631 假值 | 刪分支＋work-orders.js 四個死鍵＋DTO 註解；夜間派工 `SkipDisabled` 保留 |
| A3-16 | ⚠️→保留 | 外層包裝鍵 vs 內層欄位鍵不同層；實機外層鍵無記錄 | 明確不做，待實機確認 |
| A3-17 | ✅→改註解 | `plansPerIp` 不可達但是兜底 | 註解改「防禦性」 |
| A2a-19 ★ | ✅ | `identityChanged` 只寫不讀；裝置 upsert 也無條件遞增 | 刪 |
| H-new1 | ✅ | `IIssueOwnerStore.Upsert` 正式零呼叫，67＋測試、3 替身 | 介面移除，具體類別與 Fake 保留；測試植入一律用具體型別 |
| H-new2 | ✅ | `IsScheduledFetchRun` 轉接 | 刪 |
| H-new3 | ✅ | `Warnings`／`ManualMissingInMirror`／`ManualExcluded` 無讀取端 | 刪三欄；PRTG-SPEC :164 措辭 |
| H-new4 | ✅→改註解 | `NullAiRequestGate` 僅測試用，WEB-SPEC :2817 已載 | 註解改「僅供測試」 |

### I 收斂重複

| ID | 判定 | 證據與語意差異 | 處置（v3 修訂） |
|---|---|---|---|
| C2-06 | ⚠️ | 七支一次性＋CaseDaySync 常駐；BACKLOG 已有退場表缺兩列；Handling／Perm 刻意不經閘門 | 不做基底；四小修 |
| C3-17 | ⚠️ | 規則本體已單一（RecordRepository 兩個 static :103-133），重複只是 5 處各 2 行組合；空集合語意因介面而異 | **v3 縮小**：只加一個純函式 `RecordRepository.ResolveVisibility(SystemSettings) → (riskLevels, severities, nothingVisible)`，不做新類別、不讀設定 |
| C1-16 ★ | ✅ | 兩份可見範圍規則（都在 Web，不違反 Core 紅線）；差異：ViewAll 來源、存活檢查、快取、`retentionDays` 預設 | 先等價 Theory 再委派；ViewAll 保持 claim；`retentionDays` 必填；保留 `_cached`／`_hostSnapshot` 副作用；「停用使用者＋ViewAll」寫成預期差異案 |
| C2-10 ★ | ✅ | 兩份 switch；`ScheduleController:506` 已自己處理 null→「閒置」，`DashboardController:102` 以 `IsRunning` 擋 null | **v3 修訂**：`Of` 只處理非 null（含 `""`／`console`→「工作排程器」、`FetchFollowUp`→「AI 跟隨取數」），**不加旗標參數**；null 由呼叫端決定；RunMonitorService 刪自己的 switch |
| A2a-12 ★ | ✅ | 四類評估的閾值型別、finding 建構、規則選取各異，真重複的是外殼 | **v3 縮小**：只共用「try/catch→Partial＋note」失敗外殼 `TryEvaluateTrend(label, Action)`，評估本體各自保留；四類失敗皆 Partial |
| A2a-13 ★ | ⚠️ | 最新日退路只在管線 | **v3 修訂**：`ResolveRows(store, day, newest)` 放 `PrtgValueAssessment`（紅線指定的唯一入口），管線與 `DeviceToHostOn`／`AssessPage` 都走它，**不留「信任傳入」分支** |
| A2a-21 | ⚠️ 大部分已處理 | 剩 9 處裸字面 30 | 補 `LatestLookbackDays = 30` 常數 |
| D1-09 | ✅ | severity 四處真重複且「重大」tooltip 兩套；`renderUpdatedAt` 3 份；`renderPager` 差異在頁面狀態 | severity 合一，tooltip 統一為 glossary 字句「命中即列為高風險日（重大）」；`renderUpdatedAt(el, settings)` 進 `core/ui.js`；**v3 修訂**：pager 先 diff 三份，只差 id／callback 才抽 `renderPager(container, state, onPage)`，不做 options 工廠；`issueDensityCell` 加 layout；correlationCell 不併 |
| D2-16 | ✅ | 純複製 5 組；hostLink／removeMute／badge 有差異 | 純複製組收；`confirmAndClearMute` 半段；hostLink／badge 不併；permission-changes `statusBadge` 改名 |
| D2-18 | ✅ | 兩頁四件組雷同，另兩個自刻變體 | **v3 加限制**：`createPageSelection` 只含純狀態＋toggle／all／clear 三函式，頁面各自接 DOM；diff 後兩頁差異超過 3 個選項就放棄改只收 `syncSelectAll` |
| D2-19 | ✅ | rules.js 本地表與 format.js 逐項相同 | 改 import＋守門 |
| D2-20 | ⚠️ | 兩份語意不全同＋Rules.cshtml 第三份 | 字典 `{label:'連通性', short:'Ping'}`；glossary 補對照 |
| D2-21 | ⚠️ | 前端 Set 與後端一致；§9.8a 漏 escalated | DTO `IsUnresolved`；前端缺欄位時 fail-safe 視為未結案 |
| C1-25 | ⚠️ | factory 重複（合一消除漏帶 `IssueSourceKeyReady` 風險）；API 版本 16 處 | factory 取單例；`AiProviders.DefaultAzureApiVersion` |
| C3-22 | ⚠️ 高估 | `GetAll()` 有快取；成本在鎖與逐筆 SQL | BACKLOG |

### J 拆分與分層

| ID | 判定 | 證據 | 處置（v3 修訂） |
|---|---|---|---|
| C1-09 | ✅ | 1798 行、約 55 端點、12 個可選相依；無控制器名稱依賴；無路由／授權快照守門；`new SettingsController(` 13 檔 19 處 | 拆 5；共用的 ViewAll 檢查**改成 `[RequireViewAll]` filter attribute**（沿用 `PermissionAttribute` 的 TypeFilter 形狀），不做注入式 guard——attribute 才會被 J-0 快照看到，「忘了呼叫」就是 Z-1 的病根 |
| C1-08 | ⚠️ | 10 個控制器直連儲存層；catch 39 處多為必要轉譯 | 只抽兩處＋型別對照進 filter＋§4.2 修文；**J-3 排在 J-2 之前**（`GetPrtgHostMap` 否則被 Z-1→J-2→J-3 碰三次） |
| C1-23 | ✅ | 6 檔 14 控制器同檔 | 機械搬檔＋守門 |
| C1-14 ★ | ⚠️ | 61 個 `[FromQuery]`；`ParseLongs("abc")`→空集合當未指定；零控制器測試；同型 HandlingController 24、AdminController 18、SettingsController 11、RulesController 10 | 表驅動測試→`RecordQuery`→前端矩陣收斂；`ParseLongs` 非法輸入改回 null 讓呼叫端區分（Theory 覆蓋全部使用者）；其餘控制器 BACKLOG |
| D1-11 ★ | ⚠️ | 見 Z-11 | 拆四函式；失敗分區；JsBehavior 案例見 §9 |
| C1-10 | ⚠️ | 稽核漏 24 欄；`?? before.Revision` 在 :150 與 :593 兩處皆死碼；`_audit.Record` 共 5 站點（:499、:682、:824、:866、:954） | `SettingsAuditProjection.From(settings)`（命名避開「Snapshot」）＋反射守門；5 站點同型普查；兩處 `??` 一起刪 |

### K 韌性（本輪 K-1～K-6；其餘 61）

| ID | 判定 | 關鍵事實 | 處置（v3 修訂） |
|---|---|---|---|
| C2-11 ★／C1-18 ★／A2a-17 ★／B-07 ★ | ✅ | 見 Z-6／Z-5／Z-4／Z-12 | 在 Z |
| C1-27 | ✅ | 零安全標頭；無 iframe／embed（grep 0）；版本戳白名單現有**四套機制**（exact Whitelist 4 條、尾碼 3 條、`prtg-import-transfers` 前綴、`PrtgMonitoringController:210` 手動 Bump）漏 13 個唯讀 POST；CSP 無回報端點時 report-only 無人看得到 | **K-1**：nosniff／Referrer-Policy／`X-Frame-Options`（`Server:FrameOptions` 可設，預設 DENY；**這三個是立即生效的行為變更**，寫 README）；`[SkipDataVersionBump]` **取代全部四套機制**（Whitelist／尾碼／前綴／手動 Bump 都刪）；**CSP 整個移 61**（與回報端點一起做） |
| C1-21 | ✅ | 6 支 AI 端點只 tidy-note 有每人節流；`today-focus`／`query-summary` 是**進頁自動呼叫且 silent**，`interpret-issue`／`tidy-note`／`chat`／`help/ask` 是手動 | **K-3**：現有 `HandlingNoteTidyThrottle` 已是每 userId、可注入時鐘的形狀，泛化為 `PerUserThrottle(maxCalls, window)` 兩個具名實例：手動桶 6 次／60 秒、自動桶 30 次／60 秒（常數寫死，不進 SystemSettings）；cache hit 不扣額（沿用 :86「willCallAi 才扣」）；429 帶 `Retry-After` 與 `rate_limited`；前端 silent 路徑顯示「AI 暫時休息，約 N 秒後再試」而非隱藏 |
| C1-24 | ⚠️ 更廣 | §7.2 只列 6 碼、程式約 25 種；`ApiResponse.cs:39-47` 只 6 個 const，`StatusCodeFor` switch 只認 4 碼 | **K-2**：`ApiErrorCodes` 成為唯一登錄處（const＋狀態碼 dictionary）；`rate_limited`／`service_unavailable`／`auth_failed`；`capture_busy` 保留為領域碼；守門「程式碼用到的碼 ⊆ ApiErrorCodes」＋「§7.2 表 ＝ ApiErrorCodes」；**Z-5 先建登錄表並加 `UpstreamTimeout`**，K-2 補其餘 |
| C1-13 | ✅ | `Configuration["Server:PathBase"]` 唯一繞過強型別；無 `/` 開頭**現況就已啟動失敗**（`UsePathBase` 擲 ArgumentException）；三份文件各缺不同鍵 | **K-4**：`ServerSettings.PathBase`＋Validate **拒絕**（不自動補 `/`，錯誤訊息含鍵名與期望格式）；IIS 子 App＋PathBase 同時設定時記 Warn；`rg 'Configuration\["'` 一次盤點；守門降為「appsettings.json 鍵 ⊆ `WebAppSettings` 屬性」（反射）＋§5 人工同步 |
| E-7 | ✅ 更深 | 12,000 由 4 MiB 推導；180 天**在兩處硬編碼**（MailNotifyStateStore.cs:157、MailNotificationService.cs:2319）；最長回看窗口 28 天（14＋Previous 14）；`:1063` 另有 `Max(14, RetentionDays)` 窗口；SMTP 連線層失敗被判 Unknown | **K-5**：O1 兩處 180 合成一個常數，值＝45 天（覆蓋 28 天＋遷移邊際；**規格階段先確認 `:1063` 與 `:1306` 用 RetentionDays 的集合與收據誰是準**）；O2 連線建立前可證明的例外歸 `DefinitelyRejected`；健康分項五欄各帶一句白話；**更正**：O2 會讓原本永不重寄的連線層失敗案例重寄一次，這是刻意（寧重勿漏，且只限肯定沒寄出者） |
| B-22 | ⚠️ | 三處值得改 | **K-6**：刪多餘查詢；`PlanBulkClose` 上限 5,000 超限回 `validation`；`ReportFileMigrator` → 61 |
| C2-12／C2-20／A2b-17／A2a-16／B-10／B-19 | 見上版 | 純效能 | 61 |

---

## 2. 定案（v3；⇄＝v2 翻轉，v3＝審查後修訂）

| # | 題目 | 定案 | 理由 |
|---|---|---|---|
| 0 ⇄ | 範圍 | 60＝Z＋H＋I＋J＋K-1～K-6；61＝純效能 7 條＋CSP | 問題本輪處理；純效能與 CSP 需量測／回報端點 |
| 1 | C1-09 | 拆 5；ViewAll 檢查改 `[RequireViewAll]` attribute（v3） | attribute 可被 J-0 快照守門看到；注入式 guard 解決不了「忘了呼叫」 |
| 2 | C1-08 | 只抽兩處＋filter 型別對照＋§4.2 修文；J-3 先於 J-2（v3） | 全面清除是一整輪；同一段程式不該被碰三次 |
| 3 ⇄ | Z-3 | 前端收斂：矩陣＝明細全支援、依問題 statuses＋unassigned、依主機／日期皆不支援；不支援視角**不送參數、不顯示標籤、顯示一句提示**，但**篩選值保留在 URL／狀態，切回支援視角自動恢復**（v3：不清值）；下鑽入口已都落在依問題視角（reports.js:224、dashboard.js:403/505），不需改 | §10 既有設計；彙總視角套處理篩選是全撈退化；清值會讓「切去看一眼再切回」丟掉篩選 |
| 4 | A2a-12 | 四類失敗皆 Partial；只共用失敗外殼（v3） | 不足就顯示原因；萬用評估函式比四份重複更難讀 |
| 5 | D2-20 | `{label:'連通性', short:'Ping'}`；glossary 補對照 | 就緒度表是 ping-ms 數值 |
| 6 | A2b-17（61） | 同請求內重用；runner 逐日預檢是圍籬 | — |
| 7 | Z-4 | 單一入口 `IpSyntax.TryParseIpv4Strict`（恰 4 段十進位 ≤255，前導零允許）；PrtgAddress、NetiqHostList、PermissionChangeService:687、SentinelEventMapper:201 一併改；IPv6 維持 TryParse；**Netiq 清單行為會變**（`0x7f.0.0.1` 改拒）寫 README | 兩份規則各有漏洞；單一入口＋守門掃描 `IPAddress.TryParse(` 白名單 |
| 8 ⇄ | Z-1 | 只補 `prtg-host-map`；計數端點維持 Maintain 並補文件；**前端衝突卡 403 顯示「需要完整 PRTG 檢視權限」而非「載入失敗」**（v3）；`AdminController.hosts`／`hosts/all` 是主機管理職責（Maintain 全站）屬設計，WEB-SPEC :357 補一句 | `prtg-estimate` 是排程頁全 Maintain 在用；受限者現況半頁紅字要有一致提示 |
| 9 | C1-16 | 保持 claim；差異參數化；等價 Theory 把「停用使用者＋ViewAll」寫成預期差異 | middleware 已即時換發 |
| 10 | H-new1 | 介面移除 `Upsert` | 防整列覆寫 |
| 11 ⇄ | Z-12 | 閘門 A 快判＋閘門 B blob 旗標（與 rekey 同一組 helper）＋短路＋重試；快判用 `Any()`（EXISTS）不用 `Count()`；risky 表唯一寫入點 `EfRiskyEventStore.cs:49` 一律帶 SourceKey，旗標只會因「部分還原 DB」失準（寫進 DB-SPEC） | blob 非 schema；每次啟動兩次全掃是現在就付的代價 |
| 12 | A2b-05 | `internal`＋註明；GetStatus 改 `Ok()` | `InternalsVisibleTo` 已存在 |
| 13 v3 | C3-17 | 純函式，不做 `VisibilityFilter` 類別 | 規則本體已單一，新類別是為抽象而抽象 |
| 14 v3 | C2-10 | `Of` 不加 `legacyNull` 旗標 | 旗標參數＝兩個職責；呼叫端已各自處理 null |
| 15 v3 | A2a-13 | `ResolveRows` 放 `PrtgValueAssessment`，AssessPage 不留信任分支 | 紅線：數值評估唯一入口；第二套規則會再漂移 |
| 16 v3 | K-1 | `[SkipDataVersionBump]` 取代全部四套機制；CSP 移 61；XFO 可設 | 加第五套就是反 DRY；report-only 無回報端點無用 |
| 17 v3 | K-3 | 手動桶 6／60s、自動桶 30／60s，常數寫死；429 帶 Retry-After；前端顯示休息提示 | 自動呼叫會吃光手動配額；「可設」進 SystemSettings 踩紅線 |
| 18 v3 | K-4 | Validate 拒絕不自動補 `/` | 自動修正讓錯誤設定悄悄生效、cookie Path 跟著變 |
| 19 v3 | J-0 | 用 `IActionDescriptorCollectionProvider` 取 route＋filter，不手寫反射組 route；快照欄位：method、route、`[Permission]` 能力、`[AllowAnonymous]`、`[RequireViewAll]`、`[SkipDataVersionBump]`；無 `[Permission]` 白名單明列 Auth、Me、PrtgMonitoring、PrtgOperations、PrtgScopePurgeJobs 各附理由（`FallbackPolicy` 兜底） | 手寫 route 組裝易錯；現況五個控制器沒有 `[Permission]` 是事實 |
| 20 v3 | Z-1 測試層級 | 授權測試走 HTTP／endpoint 層（沿用 `PrtgDiagnosticExportHttpTests` 的 AddApplicationPart），不綁 `new SettingsController(` | J-2 一拆建構式就變 |

---

## 3. 批次與改動

### 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| Z 前置修正 | Z-1～Z-12＋五處小修 | 中 | Z-3→J-4、Z-7→I-2、Z-8/9→I-3、Z-11→J-5 | 1 |
| H 刪死碼 | 11 刪／3 改註解／1 保留 | 中 | 先於 J（`PrtgDataJsonOptions`、`ImportPrtgData`） | 2 |
| I 收斂重複 | 後端 6、前端 6、小修 3 | 大 | 前端上提先於 J-5；C1-16 先於可見範圍改動 | 3 |
| J 拆分與分層 | 守門→搬檔→**抽 Service**→拆 Settings→RecordQuery→D1-11→稽核投影 | 大 | H 之後；J-0 先綠；J-3 先於 J-2 | 4 |
| K 韌性 | K-1～K-6 | 中 | K-2 接 Z-5 的登錄表；K-1 attribute 在 J-2 之後貼（J-0 快照含此欄位，漏貼會紅） | 5 |

### 批次 Z：前置修正

1. Z-1：`prtg-host-map` 加 `[RequireViewAll]`（attribute 在 J-2 才全面導入，Z-1 先用既有 `RequireFullPrtgTransferAccess()` 一行，J-2 統一換 attribute）；`_currentUser` 改必填；三處 `new HostStore` 改用 `_hosts`；前端衝突卡 403 → 權限提示；WEB-SPEC :357 補計數端點例外與主機管理說明。
2. Z-2：兩個 try 都改 `catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))` 形狀；同型普查以 `rg "catch \(Exception" -B15` 列出前一個 catch 非 OCE 的站點（PrtgBackfillService.cs:641/797/899、PrtgProbeService.cs:258/309/335、PrtgStructureSyncService.cs:358/422/437、SettingsController.cs:1433/1460/1760）逐一判定附表。
3. Z-4：`IpSyntax.TryParseIpv4Strict` 單一入口；四處改用；守門掃 `IPAddress.TryParse(`（白名單：LoginThrottle.cs:127/210、AppSettings.cs:101、Program.cs:180、IpSyntax 本身）。
4. Z-5：建 `ApiErrorCodes`（const＋狀態碼 dictionary，搬入既有 6 碼）；`UpstreamTimeout`→503；OCE 分流：`RequestAborted.IsCancellationRequested`→499 不記 log；否則 503＋Warn。
5. Z-6：`SchedulerRunState.Snapshot()`；`GetStatus` 與 `RunActivity` 取數分支只用快照；刪 `LatestActivity()` 與 Snapshot 重疊部分。
6. Z-10：`days` Clamp 常數唯一來源。
7. Z-12：(a) 閘門 A 快判三條件（`Any(source_key IS NULL)`＝false、rekey blob 版本符合、`CountPending()==0`）在進 gate 前；(b) 旗標單調；(c) 閘門 B：`RunRiskySourceKeys` 驗證 remaining==0 後寫版本化 blob `risky_events_source_key_done`（沿用 rekey 的讀寫 helper），存在即短路；(d) 失敗重試 3 次／30 分鐘（可注入）。
8. 小修：Perm 遷移 Bump 移到清理後；四支回填 `finally { if (wroteAny) Bump }`；`PrtgDailyPipeline.cs:385` 無效賦值移除。

### 批次 H：刪死碼（三段，同 v2；WriteOutput lock 隨 H-1 A2b-13 消除）

H-1 PRTG 儲存與服務；H-2 回填與校準（A2b-05 改 internal）；H-3 其餘。每項規格附 `grep -w` 結果；H-new1 植入改具體型別。

### 批次 I：收斂重複

I-1 可見範圍（C1-16 等價 Theory→委派；C3-17 純函式）；I-2 排程文字（C2-10 無旗標＋fetch-followup）；I-3 PRTG 管線（A2a-12 失敗外殼；A2a-13 `ResolveRows` 進 Assessment；A2a-21 常數）；I-4 背景服務小修＋C1-25；I-5 前端上提（一）：D2-19、`categoryName` 16 處、severity 合一（glossary 字句）、`renderUpdatedAt(el, settings)`、`issueDensityCell` layout、pager（diff 後決定）；I-6 前端上提（二）：D2-16 純複製組、`confirmAndClearMute`、`statusBadge` 改名、D2-18（限制版）、D2-20 字典、D2-21。新守門：FrontendConsistencyUiTests 禁止頁面重宣告上提名稱。**規格逐段列「被 behavior.mjs 切出的函式」與「prelude 要補的符號」**（records-search 切 `search`；list-pages 切 audit／hosts／permission-changes／reports／dashboard 的 `search`／`load`；misc-pages 切 `loadReports`／`onScopeChipClick`／`mailLedgerNotice`／`requireScopeRevision`；prtg-transfer-controls 切 `bindPrtgDataTransfer`）；上提後**刪頁面本地同名宣告**（JsModuleImportTests 會把本地同名當區域宣告，靜默用本地版）。

### 批次 J：拆分與分層

J-0 守門（`IActionDescriptorCollectionProvider` 快照＋白名單＋一檔一控制器）→ J-1 搬檔 → **J-3 抽兩處 Service＋filter 型別對照＋§4.2** → J-2 拆 5（`[RequireViewAll]` attribute；可選相依必填；19 處測試建構搬遷）→ J-4 RecordQuery（表驅動先綠；`ParseLongs` 非法→null；前端矩陣收斂保留狀態；records-search prelude 補新符號；WEB-SPEC §10；BACKLOG :70-76）→ J-5 D1-11 → J-6 `SettingsAuditProjection`＋反射守門＋5 站點普查＋兩處 `??` 刪。

### 批次 K：韌性

K-1 標頭（nosniff／Referrer-Policy／XFO 可設；中介層排在 `CsrfHeaderMiddleware`／`ActiveUserMiddleware` 之前讓 401／403 也帶標頭）＋`[SkipDataVersionBump]`（刪四套舊機制；13 端點貼上；`DataVersionStampPolicyTests` 改 `SetEndpoint` 形狀）；K-2 `ApiErrorCodes` 補碼＋`StatusCodeFor` 由登錄表驅動＋§7.2 守門；K-3 `PerUserThrottle` 兩桶＋Retry-After＋前端提示；K-4 `ServerSettings.PathBase` Validate 拒絕＋IIS 雙前綴 Warn＋`Configuration["` 盤點＋反射守門；K-5 常數合一 45 天＋O2 分類＋健康五分項白話；K-6 刪多餘查詢＋`PlanBulkClose` 上限。

---

## 4. 文件、說明書與 BACKLOG 同步（隨批）

- WEB-SPEC：:357（Z-1 計數端點例外、主機管理屬 Maintain）；§4.2（J-3）；§7.2＝`ApiErrorCodes`（K-2）；§9.8a（D2-21）；§10 篩選矩陣（J-4）；§5 範例補鍵（K-4）；`SettingsController` 敘述改五個控制器（J-2）。
- PRTG-SPEC：:164 措辭（H-new3）；§1 嚴格 IPv4 一句（Z-4）；§9b 最新日退路（A2a-13）。
- DB-SPEC：:830；新 blob `risky_events_source_key_done`（含「部分還原 DB 會失準」）。
- NETIQ-API-REFERENCE：A3-16 待實機確認。
- **說明書（HelpContent，守門 `HelpContentServiceTests`）**：`03-issues.md:54`／`03-issues.ai.md:29-30`（逾期／未指派只在支援視角顯示標籤，Z-3）；`15-reports.ai.md:26` 核對下鑽仍落依問題視角；`10-scheduler.md` 補「AI 跟隨取數」一句（Z-7）；`20-prtg.md:76`／`20-prtg.ai.md:29` 補「部分成功＝某類趨勢未完成」與「最新日對應退路」（A2a-12／13）、就緒度類別名稱對照（D2-20）；`13-glossary` 補 Ping／連通性對照（D2-20）；AI 功能頁補「使用過於頻繁會暫停約一分鐘」（K-3）。
- README 升級注意（五條）：Z-4 嚴格 IPv4 影響 PRTG 對應、NetIQ 清單、權限異動網段三面；K-1 三個標頭立即生效、`Server:FrameOptions` 可調；K-4 PathBase 格式錯誤訊息更明確、IIS 子 App 勿重複設定；K-5 首輪可能補寄（連線層失敗案例與舊收據釋出）；J-2 NLog logger 名稱變更（`nlog.config` 無依賴，部署端自查）。
- CLAUDE.md：「K 的來源鍵就緒閘門必須分開」補一句「啟動快判只針對閘門 A（top issues＋first_seen）；閘門 B 由版本化 blob 短路」；收尾改基線。
- BACKLOG：刪 :412-413、:470；改寫 :70-76；升級期表補兩列、合併 :346／:584；新增 C3-22、`ReadAllRunLines` 回退、A3-16 待實機、其餘控制器 `[FromQuery]` 收斂（HandlingController 24 等）、§6 61 條目。

## 5. 明確不做（本輪定案）

A3-16；C2-06 共用基底；C3-22；correlationCell／hostLink／badge 合併；30→31；C1-08 全面清除；~25 處 `new SystemSettingsStore`；NullAiRequestGate 搬 Tests；A2b-05 改 78 處測試；Z-3 後端補彙總視角篩選；Z-1 擴到計數端點與 `admin/hosts`；CSP（61）；E-7 O3～O6；`VisibilityFilter` 類別；`RunTriggerText` 旗標參數；`PrtgAdminAccessGuard` 服務；pager options 工廠；`WebAiService` OCE 穿透（無 ct）；K-3 節流進 SystemSettings。

## 6. 第 61 輪草案（純效能＋CSP）

C2-12；C2-20；A2b-17；A2a-16；B-10；B-19；CSP（nonce 四段 inline script＋`POST /api/csp-report` 寫 NLog＋report-only→強制）＋frame-ancestors；E-7 O3（＋O4 政策）；`ReportFileMigrator` keyset；寄前重核每收件人重查；AI 快取整份 blob；`netiq_attempt_ledger_invalid` 回 200。

## 7. 規劃完成後複檢（v3）

- 與既有設計衝突：Z-3 不推翻 §10；Z-1 不擴到設計上屬 Maintain 的端點；Z-4 收緊三面並寫升級注意；A2a-12 Partial；C1-16 不改 ViewAll 來源；K-1 三標頭是行為變更（已更正 v2 的「不影響行為」）；K-5 O2 會重寄（已更正 v2 的「不增加重寄」）。
- 批次間同檔：SettingsController：Z-1／Z-2（行為）→ H-2（刪 ImportPrtgData／PrtgDataJsonOptions）→ J-3（抽 GetPrtgHostMap）→ J-2（拆）→ K-1（貼 attribute）；prtg-admin.js：H-3 → I-5 → J-5；ApiExceptionFilter：Z-5（登錄表＋OCE）→ J-3（型別對照）→ K-2（補碼）；RecordsController／records.js：J-4 一次處理 Z-3。
- 四個坑：什麼算一個——A2a-12「失敗」＝評估擲非 OCE 例外；K-3 一次＝一個真正呼叫 AI 的請求；分母為零——C1-16 空主機表案、K-5 五分項全零仍顯示；破壞性判準——Z-4 反例 `1.2.3.04` 合法、Netiq `0x7f.0.0.1` 改拒要寫升級注意；K-5 O2 只歸類連線建立前可證明的例外，DATA 後逾時仍 Unknown；K-6 超限回錯不截斷；單向閘門——Z-12 兩閘門各自可回退；K-1 attribute 漏貼只是多 Bump，靠 J-0 快照欄位抓；移除類依賴方——H 每條已列；K-1 刪四套機制時 `DataVersionStampPolicyTests` 全部改寫。
- 升級／既有資料：README 五條；Z-12 新 blob 首次由完整回填寫入；J 路由零改動。
- 複檢結論：v3 相對 v2 砍掉五個抽象（`VisibilityFilter`、`legacyNull` 旗標、`PrtgAdminAccessGuard`、pager options 工廠、萬用 `EvaluateValueTrendRule`）、撤銷一條不可實作項（WebAiService）、更正兩句錯誤陳述、補說明書七處與 README 五條、每項補專屬驗收（§9）。

## 8. 驗收篩選詞命中數（dev@da1efddc 實跑 `--list-tests`，Theory 算一條，共 6,621 條；SQL Server 面只驗 `ToQueryString` 形狀，不代表實機）

| 段 | 篩選詞 | 命中 |
|---|---|---|
| Z-1 | `PrtgHostMapEndpointTests\|PrtgDiagnosticImportIsolationTests\|SettingsController` | 34＋ |
| Z-4 | `PrtgAddressTests\|PrtgHostMapperTests` | 83 |
| Z-12 | `SourceKeyBackfillTests\|RiskySourceKeyBackfill\|IssueFirstSeenSourceKeyRekey` | 9 |
| H-1 | `PrtgReadiness\|PrtgStoreManagement\|PrtgSqlServerIdScope\|PrtgSamplingActivity\|PrtgFindingsRegistry\|PrtgSnapshotAccumulator\|PrtgSnapshotHostedService\|PrtgScopeDevices` | 157 |
| H-2 | `PrtgBackfillRunner\|Calibration\|PrtgDataTransfer\|PrtgDiagnosticImport\|PrtgTransferController\|PrtgDiagnosticExport\|RetentionPruner` | 154 |
| H-3 | `DashboardService\|SchedulerTriggerQuery\|IssueOwner\|UiTests\|JsModuleImport\|BehaviorTests\|ApiTimeout` | 451 |
| I-1 | `VisibilityService\|CaseGrantVisibility\|PrtgHostVisibilitySnapshot\|DispatchCandidateSource` | 57 |
| I-2 | `RunActivityBanner\|UserDisplayNameService` | 33 |
| I-3 | `PrtgDailyPipeline\|PrtgValue\|PrtgHostMapLookup` | 133 |
| 前端各段 | `UiTests\|JsModuleImport\|BehaviorTests\|ApiTimeout`（J-5 另加 `PrtgTransferControlsBehavior`） | 375 |
| J-2 | `PrtgHostMap\|PrtgBackfill\|PrtgStructure\|PrtgScope\|PrtgDiagnostic\|Schedule\|PrtgUnmatchedMap\|PrtgResourceGuard\|PrtgSnapshotObservability\|PrtgDeviceIndexCache\|PrtgFetchScope\|PrtgRunsCard\|ReportPerformanceContract` | 529 |
| J-4 | `RecordQueryService\|RecordsController`（控制器測試為新增） | 71 |
| J-6 | `SystemSettingsService` | 160 |
| K-1 | `DataVersionStampPolicy\|JsonBlobSingletonCache` | 28 |
| K-5 | `MailNotificationService\|MailIssueDigest` | 104 |

## 9. 逐項驗收總表（每項至少一條可機器判定、主代理能自己跑；「改前紅」＝先寫測試確認在改動前失敗）

| 項 | 驗收（測試形狀） | 改前紅 |
|---|---|---|
| Z-1 | HTTP 層 Theory：受限 Maintain→`prtg-host-map` 403、`prtg-estimate` 200；ViewAll→200。JsBehavior：host-map 403→衝突卡顯示權限提示字串、不顯示「載入失敗」。反射：`SettingsController` 建構式無 nullable `ICurrentUser`；grep `new HostStore(` 在 SettingsController＝0 | 是 |
| Z-2 | Fact×2 per try：ct 已取消→擲 OCE；ct 未取消的 OCE→`Success=false`／退回鏡像。普查附表每站點寫判定 | 是 |
| Z-3 | 後端 Theory（4 端點×overdue/statuses/unassigned→`RecordSearchRequest` 欄位）。behavior：依主機視角＋URL overdue→`buildQueryString` 不含 overdue、無標籤、提示元素可見；切回明細→overdue 恢復送出 | 是 |
| Z-4 | `IpSyntax` Theory：`10.1`、`1.2.3`、`12345`、`0`、`0x7f.1`、`1.2.3.4.5`、`256.1.1.1`→null；`1.2.3.04`→`1.2.3.4`。`NetiqHostList.IsValidIp`：`0x7f.0.0.1` 拒。`PrtgHostMapperTests`：device `10.1` 不對 `10.0.0.1`、`12345` 走名稱退路。守門：`IPAddress.TryParse(` 出現位置 ⊆ 白名單 | 是 |
| Z-5 | Theory：`RequestAborted` 已取消→499 且記憶體 NLog target 無 Error；未取消 OCE→503 `upstream_timeout` 且 Warn。反射：`ApiErrorCodes` 每個 const 在狀態碼表有映射 | 是 |
| Z-6 | 結構測試：`Snapshot()` 回傳 record 欄位集合＝DTO 需要欄位；程式碼掃描：`GetStatus`／`RunActivity` 函式本文 `_runState.` 只出現一次（`Snapshot()`）。不用多執行緒交錯（flaky） | 是 |
| Z-7 | Theory：`Of("fetch-followup")`＝「AI 跟隨取數」、`""`／`"console"`＝「工作排程器」、`manual:x`、未知原樣；掃描：RunMonitorService 不含 `TriggerText(` switch；RunActivityBannerTests:49 改具體字串 | 是 |
| Z-8 | 四類各一替身擲非 OCE→`PrtgOutcome=Partial`＋note 含類別名；規則缺失／無候選→Success（反例） | 是 |
| Z-9 | 最新日全 unmatched＋前一日 ok：狀態 finding 歸戶、`DeviceToHostOn` 非空、`AssessPage` 不回 `MappingInvalid`；掃描：`ResolveHostMapRows` 在 PrtgDailyPipeline＝0 | 是 |
| Z-10 | Theory days=0／-1／91／100000／null→區間同 Clamp；掃描：`Clamp(1, 90)` 字面只在常數定義處 | 是 |
| Z-11 | behavior：三請求之一 reject→只該區顯示對應 label 的錯誤、其餘區正常渲染；靜態：`risk-review/pending` 呼叫帶 `silent: true` | 是 |
| Z-12 | 完整資料＋`IsRunning=true`→快判 ready 且 gate 未取得；1 列 NULL→不成立；rekey blob 版本不符→不成立；旗標單調（`Run()` 後仍 true）；閘門 B blob 存在→risky 表零查詢（計數替身）；A 成立 B 不成立→`IRiskyEventStore` 舊路徑；重試序列 3 次／30 分；`ToQueryString` 含 `IS NULL` 且為 EXISTS 形狀。兩後端 | 是 |
| 小修 | Perm：替身 `DataVersionStamp` 記錄順序，清理擲例外時 Bump＝0；四支回填：例外後 Bump＝1；:385：掃描該行不含 `DiskAssessmentFailed =` | 是 |
| H-1～H-3 | 每項 `grep -w` 舊名在 Core／Web／wwwroot／Views＝0（規格附實跑結果）；`PrtgSqlServerIdScopeSourceTests` 仍含 IN 上限案；A2b-13 測試讀 `Console`；C3-10 DashboardServiceTests:631 不含 `"disabled"`；H-new1 掃描：`IIssueOwnerStore` 介面無 `Upsert` | grep 可判 |
| A2b-05 | 掃描：兩方法 `internal`；GetStatus 無 `RequestAdmission`／`ReserveTransient`；:2877 改為服務層互斥案 | — |
| I-1 C1-16 | Theory（使用者狀態×群組狀態×主機狀態×ViewAll／owner／issueOwner，含空主機表）：兩路徑集合相等，「停用使用者＋ViewAll」列預期差異；掃描：`HostVisibilityResolver.GetVisibleHostIds` 無預設參數 | 是 |
| I-1 C3-17 | 掃描：五呼叫端不再各自呼叫 `ResolveDayRiskLevels`／`ParseVisibleSeverities`；純函式 Theory：null／空／交集／非法字串 | — |
| I-3 A2a-21 | 常數測試同 `PrtgHostMapLookupTests:63` 形狀；掃描：`GetLatestHostMapWithDate(30` 字面＝0 | 是 |
| I-4 C2-06 | HostedService 外殼 Fact：閘門未開→不執行；例外→Task 不逸出且 log 一筆；退場表兩列存在（文件 grep） | 部分 |
| I-4 C1-25 | 掃描：`new EfIssueAggregateQuery(` 在 ServiceCollectionExtensions 只一處；`"2024-10-21"` 字面只在 `AiProviders` 與測試 | 是 |
| I-5／I-6 | FrontendConsistencyUiTests 新規則（頁面不得宣告 `renderPager`／`renderUpdatedAt`／`CATEGORY_NAMES`／`SEVERITY_ORDER`／`severityCell`／`ORIGIN_LABELS`）；D1-09 tooltip 掃描：「命中即列為高風險日（重大）」只在 format.js；D2-18 behavior：全選／清除／跨頁保留／換頁不清；D2-20 UiTests：Rules.cshtml option 文字＝字典 label；就緒度表 cell＝short；D2-21：`JsonSerializer.Serialize(HandlerDayItemDto)` 含 `isUnresolved`，前端缺欄位→視為未結案 | 是 |
| J-0 | 三守門綠；快照基準檔進 repo；白名單五控制器各附理由 | — |
| J-1 | 守門 (c) 綠；`WorkOrderQueryController.cs` 不存在 | 是 |
| J-2 | J-0 快照 diff＝0（含 `[RequireViewAll]`／`[SkipDataVersionBump]` 欄位）；§8 J-2 篩選全綠 | — |
| J-3 | Fact：`Prtg()` 替身記錄 `EnsureVisible`→`IsCaseGrantOnly` 順序；`GetPrtgHostMap` 抽離前後對同資料回傳 JSON 相等（黃金值） | — |
| J-4 | 表驅動測試改動前後皆綠；`ParseLongs("abc")`→null 的 Theory 覆蓋全部呼叫端；Z-3 behavior；WEB-SPEC §10 含矩陣（文件 grep） | 是 |
| J-5 | 同 Z-11＋`PrtgTransferControlsBehavior` 全綠 | 是 |
| J-6 | 反射守門：`SystemSettings` 公開屬性 ⊆ 投影 ∪ 白名單；Fact：`AiApiKeyEnc`／`SmtpPasswordEnc` 值不出現在 detail JSON；掃描：`?? before.Revision`＝0；5 站點附表 | 是 |
| K-1 | HTTP：API／頁面／靜態檔三類回應皆含三標頭；`Server:FrameOptions=""`→不送；401 回應也含標頭；`DataVersionStampPolicyTests` 改 `SetEndpoint`：13 端點不推進、`PUT prtg/monitoring` 推進；掃描：Program.cs 無 `ReadOnlyPostSuffixes`／`Whitelist`、PrtgMonitoringController 無手動 `Bump()`；J-0 快照含 attribute 欄位 | 是 |
| K-2 | 守門：程式碼 `Fail(`／`DomainException` 用到的碼 ⊆ `ApiErrorCodes`；§7.2 表列＝`ApiErrorCodes`（解析 markdown 表第一欄）；PermissionChangeMigrationTests:364 改 `service_unavailable` | 是 |
| K-3 | Theory：手動桶第 7 次 429 含 `Retry-After`；自動桶第 31 次 429；不同使用者不互擋；cache hit 不扣額；時鐘前進 60 秒恢復。behavior：silent 路徑 429→顯示休息提示元素 | 是 |
| K-4 | Theory：`""`、`"/app"`、`"/app/"` 通過；`"app"`→啟動例外訊息含 `Server:PathBase` 與期望格式；反射：appsettings.json 鍵 ⊆ `WebAppSettings` 屬性；掃描：`Configuration["` 只在白名單 | 是 |
| K-5 | 掃描：`AddDays(-180)` 在 Core／Web＝0、常數只一處；Fact：46 天前收據清除、44 天前保留、44 天前且仍在回看窗口的問題不重寄；連線被拒替身→預約釋放可重寄；DATA 後逾時→仍 Unknown；健康 DTO 含五分項 | 是 |
| K-6 | 掃描：`MarkExistingRecordsAsNotified` 只一次 `Query(`；Fact：`PlanBulkClose` 5,001 筆→`validation` | 是 |
| 說明書 | `HelpContentServiceTests` 既有結構校驗全綠；新增關鍵字守門：`03-issues.ai.md` 含「支援視角」、`20-prtg.md` 含「部分成功」、`10-scheduler.md` 含「AI 跟隨取數」 | 是 |
