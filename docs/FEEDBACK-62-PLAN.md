# LogForesight 第 62 輪規劃：儀表板漸進揭露、排程續跑與執行時間、說明文字收斂、macOS 可建置

> 狀態：實作中（feature/feedback-62）
> 基準：dev@b4757155（7,530 通過／10 略過，Windows；`--list-tests` 7,246 條，Theory 每組資料算一條）
> 來源：使用者 2026-11-02 五項重點（儀表板一排優先／執行紀錄時間／多窗口續跑／說明文字原則／macOS 開發）
> 實作方式：主代理寫規格與驗收；改檔代理由主代理依複雜度選 sonnet 或 haiku（使用者 2026-11-02 指定，取代前版的 opus 分工）；一個 checkout 一次只派一個改檔代理；每批驗證後各自 commit 並 push 到 `feature/feedback-62`
> 核對方法：五個唯讀代理盤點現況 → 主代理親讀程式碼重驗 → 兩個 opus 審查代理分別以「尖銳使用者＋管理者操作走查」與「程式面＋整體影響＋SOLID/KISS/DRY」審第一版 → 主代理逐條重驗後併入（§1 標 ◆ 者＝審查補充且已重驗屬實；§10 記錄審查發現與處置）
> 設計面：套用 `ui-ux-pro-max`（§2 開頭列引用的指引）

## 0. 總結與範圍

五項重點對應六個批次。排程兩題共用 `BatchRunRecorder`／`AnalysisOrchestrator`，**先定階段計時 API（C-1）再做續跑（B）**，續跑的里程碑直接用那支 API 寫，不留兩份呼叫點；儀表板（A）連同本頁說明文字一起改；其餘頁面的說明文字收斂放 D；macOS（E）改建置與平台守門，業務邏輯只動「非 Windows 時怎麼回應」；文件對齊（F）收尾。

**每條定案的驗收都寫成主代理在 Windows 上自己能跑的形式**（xUnit／Node 行為測試、建置指令、grep 斷言、本機 SQLite 站台的瀏覽器實測）。只有 E 批的「Mac 上實跑」必須由使用者執行，PLAN 明標「使用者驗收」並附 Windows 側能先驗的替代項。

評估與審查中發現並納入本輪的缺陷：

| 編號 | 問題 | 批次 |
|---|---|---|
| B1 | 「錯過窗口自動補跑」以 `schedule` 觸發在窗口外開跑，下一次 60 秒輪詢被「窗口外的排程執行一律優雅停止」取消，記成 Stopped 且算已觸發，補跑實際上幾乎補不到東西 | B |
| B2 | 補跑帶 `BackfillOverride=1`，本機回望從常態 14 天、NetIQ 回補從設定值縮成 1 天 | B |
| B4 | 跨午夜時目標日各處自取 `DateTime.Today`（orchestrator 六處、`MissingDateFinder` 四處、`RerunDateFinder` 三處、PRTG 三處），同趟可能出現兩個目標日 | B |
| C1 | 執行詳情 modal 只看 exitCode／stopped，列表用 `BatchRunStatus.Compute`，同一筆列表「失敗」modal「成功」 | C |
| C2 | 站台重啟時把重啟時刻當 FinishedAt，耗時含整段停機；未結束紀錄只回看 7 天，更早的永遠關不掉 | C |
| C3 | 輸出超過 1500 行後尾段 500 行到結束才寫入、`LoggedAt` 全等於結束時刻 | C |
| C4 | NetIQ 失敗、本機例外時沒有分路結束里程碑；PRTG 沒有整路起訖；AI 趟完全沒有里程碑 | C-1／B |
| D1 | 風險類型卡 hover 有 `translateY`，與 KPI 卡「hover 不位移」規則不一致（全站只有儀表板用到 `lf-card--clickable`） | A |
| F1 | WEB-SPEC §9.1 三處與程式不符（全綠文字、右下角補位、待辦區條件）；§10 仍寫實體表 `lf_batch_runs`；沒有 AutoCatchUp 段落 | F |

**第一版列為缺陷、審查後改判的**：B3「跟隨取數的 AI 趟不受 AI 窗口停止」是 WEB-SPEC §9.10（:2852-2854）與 Runs 頁 popover 明寫的刻意設計（取數跑到哪、判讀跟到哪），不是不一致——**本輪不改**（§5）。serverAdmin 的群組風險殼殘留不成立（兩張卡同一個 `.row`）。

## 1. 核對表（主代理親核；◆＝審查代理補充且重驗屬實）

### 批次 A：儀表板

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| KPI 列由 `kpiStrip(groups)` 唯一出口渲染，三組（風險 4／待處理 4／權限 2）順序寫死 | ✅ | dashboard.js:238-396、ui.js:1628-1681 | 待處理組現行順序：未處理問題→我的交辦單→待派工問題→未回報主機 |
| 組格線 `repeat(auto-fill, minmax(9rem,14rem))`，≥1200px 三組依卡數 flex 分寬並排，≤575px 兩欄 | ✅ | site.css:1118-1163 | 組寬由 flex 分配，純 CSS 斷點算不出每組欄數。使用者截圖（內容區約 1,600px）三組並排、每組兩欄，揭露在桌機**會**觸發；審查代理依 CSS 推算「桌機不會觸發」與實況不符，以瀏覽器實測為準（§2-9） |
| 風險類型卡順序由後端排（有高>有中>數量），前端照序渲染，最多 8 張，`auto-fit minmax(13rem,1fr)` | ✅ | RecordStatsBuilder.cs:41-42、dashboard.js:398-446、site.css:1212-1215 | `auto-fit` 的空軌在 computed 值裡是 `0px`，量欄數要**計入** |
| 既有收合元件 `bindRememberedCollapse`（Bootstrap collapse＋`lf.collapse.{key}`） | ✅ | ui.js:250-272 | 機制不同（事件 vs 量測），不合併；只共用 localStorage 鍵的讀寫 ◆ |
| 守門：`kpiStrip` 唯一出口、副標兩行佔位（:53 `min-height: 2.5em`）、`'沒回報 ≠ 沒問題'` 字串（:378）、`dashboard.js` 只允許 `--lf-swatch`、`lf-kpi` 不得有 transform（:722 只查 `.lf-kpi` 選擇器） | ✅ ◆ | FrontendConsistencyUiTests.cs、InlineStyleUiTests.cs:54 | `kpi-strip.behavior.mjs:70` 斷言空副標也有佔位節點 ◆ |
| 行為測試用 mini-dom：沒有 `getComputedStyle`／`ResizeObserver`／`[hidden]`，`classList` 只有 add／contains，沒有 `removeAttribute`／`hasAttribute` | ✅ ◆ | JsBehavior/mini-dom.mjs:30-57 | helper 的依賴全部可注入，mini-dom 補最小屬性方法 |
| 期間切換 `load()` 會 `replaceChildren` 重建格線（:395／:464／:835）；字級切換只改 `data-font-scale`，格線寬度不變 | ✅ ◆ | dashboard.js、layout.js:348-368 | observer 要能 dispose；字級切換要另發事件 |
| `translateY` 只影響儀表板（`lf-card--clickable`、`a.lf-stat` 全站只有 dashboard.js 用） | ✅ 親核 | grep | |
| 未回報主機卡沒有 `title`，tooltip 來自 hint（`title: card.title ?? card.hint`） | ✅ ◆ | dashboard.js:384 | 刪 hint 要補 title |
| 說明書 `02-dashboard*.md` 未引用將刪的副標句 | ✅ | grep 零命中 | |

### 批次 B：排程窗口與續跑

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| 觸發＝「每個窗口實例一次」，成功／失敗／Stopped 都算；只有 Interrupted 的第一筆不算 | ✅ | ScheduleCalculator.cs:161-181、:217-246 | |
| 第二窗口一定再觸發整趟 Full；目標日「昨天」 | ✅ | SchedulerHostedService.cs:247-256、AnalysisOrchestrator.cs:539 | |
| 窗口結束：對 `Trigger==schedule` 且不在窗口內的執行 `TryCancel`，記 Stopped | ✅ | SchedulerHostedService.cs:227-232、BatchRunRecorder.cs:319-326 | |
| 本機／NetIQ 靠缺漏日只補未完成主機日（主機日粒度續跑已成立） | ✅ | HostDayPostProcessor.cs:15-53、NetiqPipelineService.cs:343-381 | |
| PRTG 每趟整條重做；`FetchDayAsync`（結構同步、狀態變更、對應重算、範圍清除、夜間同步狀態）是唯一對外 I/O 段，規則評估／發佈／補追加全讀本地 store（`ResolveHostMapRows`、`GetStateChanges`）；已有 `skipStructureSync` 分支前例 | ✅ 親核 ◆ | PrtgDailyPipeline.cs:166-308、:365-416、:637-679 | **切點定在 `FetchDayAsync`**，不是「自身段」（第一版切法會讓登錄簿變空，見 §10） |
| `PrtgFindingsRegistry` 是一趟執行內的記憶體登錄簿；就緒後落地的主機日由 `AttachPrtgFindings` 從它併入；finally 對未發佈日補發空集合並送就緒訊號 | ✅ 親核 ◆ | PrtgFindingsRegistry.cs:5-22、PrtgDailyPipeline.cs:867-910、AnalysisOrchestrator.cs:1009 | |
| `PrtgDayStat.NeedsReevaluation = syncFailed \|\| historyAffectingFailure \|\| !IsEvaluated`；`ClassifyDay` 把單顆 sensor 取數失敗也算 partial | ✅ 親核 ◆ | PrtgDailyPipeline.cs:915-935 | 「完成」不能只看 Outcome |
| `BatchRun` 沒有 Scope／HostIds 欄位；指定主機更新的趟也寫 success 的 `PrtgDayStat` | ✅ ◆ | BatchRunStore.cs:32-137、PrtgDailyPipeline.cs:375 | 完成判定要排除非 Full 趟 |
| 「依 StartedAt 由新到舊找 `PrtgDays` 含 D 的非 AI 趟」已有三份：`PrtgReevaluationPlanner.Select`、`SchedulerHostedService.LastScheduledPrtgDay`（:380）、`RunMonitorService`（:124-143）；orchestrator 開跑時已讀 `recentRuns`（:649） | ✅ ◆ | | 抽一份共用，不新增 store 查詢 |
| 觸發式取數候選＝該日高／中風險主機，去重只在記憶體；`lf_prtg_values.quality` 分 `ok`／`sampled`；`GetValueFetchTargets` 另有回填兩個呼叫端；SQL Server 參數上限要 `Chunk` | ✅ ◆ | PrtgTriggeredValueFetcher.cs:99-150、PrtgConstants.cs:45-57、EfPrtgStore.cs:1288/2640 | |
| PRTG／NetIQ 都在同一趟 orchestrator 內與本機並行，受取數窗口管；例外：快照服務全天候、手動同步／回填 | ✅ | AnalysisOrchestrator.cs:640/666/676、PrtgSnapshotHostedService.cs:39-43 | 回答使用者問題 (b)：是 |
| B1 證據鏈：補跑 `Trigger=schedule`（:296）在窗口外開跑 → :227-229 取消 → Stopped 算已觸發；`AutoCatchUpMaxDelay=4h` | ✅ 親核 | SchedulerHostedService.cs:259-301、ScheduleCalculator.cs:273 | Interrupted 補跑是窗口內由 `ShouldTriggerNow` 觸發（:250-256），**不在 B1 範圍** ◆ |
| B2：`BackfillOverride` 消費端＝本機 :828、NetIQ :1131、PRTG :1361；Web 端 `ScheduleController` :211/228/372、`SchedulerHostedService` :294/409；`ComposeEffectiveRequest`（:400-416）註解明講漏抄欄位會靜默失效 | ✅ ◆ | AnalysisOrchestrator.cs | 不新增欄位，在消費端對補跑取 max |
| B4 呼叫點：orchestrator :495/:539/:645/:656/:696/:889；`HostDayPostProcessor` :22/:34/:48/:321；`RerunDateFinder` :24/:33/:34；`PrtgDailyPipeline` :471/:493/:534 | ✅ ◆ | | 本機也經 `MissingDateFinder`，不是只有 NetIQ |
| `BatchRun.CatchUp` 欄位已存在；`NoteSkippedScheduleWindow` 條件是 `Trigger != Schedule` | ✅ ◆ | BatchRunStore.cs:76、SchedulerHostedService.cs:236 | |

### 批次 C：執行時間

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| `BatchRun` 只有整趟起訖；分路只有計數欄與 `PrtgDays`；JSON 行存 `lf_log_lines`、同 RunId 取最後一列，加欄不需 DDL | ✅ | BatchRunStore.cs:32-152、:400-405 | |
| `GetRecentRuns` 是整段讀出＋JSON 解析＋`GroupBy().Last()`；排程 Tick 每 60 秒最多呼叫 5 次（:247/266/326/589/626），`GetRun` 每開 modal 讀 121 天 | ✅ ◆ | SchedulerHostedService.cs、BatchRunStore.cs | 快照列數要節制 |
| `RunPhases` 是進度軌契約：`SchedulerRunState` 依 `prtg-` 前綴分派、`RunsPageUiTests` 反射 `ProgressTracks` | ✅ ◆ | RunPhases.cs:15-45、run-phases.js | 計時鍵不得混進進度軌 |
| recorder 正式建構點兩處：orchestrator :292（`new BatchRunRecorder(`）、AI :262（target-typed `new(`）；console 已退場 | ✅ ◆ | | |
| 三路並行，recorder 計數以 `_countLock` 保護 | ✅ | BatchRunRecorder.cs:102-112 | 階段寫入同鎖 |
| 總表：**主機格以開跑日 `StartedAt.Date == D`**、分析紀錄取 D-1；只有 PRTG 欄以資料日歸屬；`GetDaySummaries` 在 :97-98 就濾掉 AI 趟 | ✅ 親核 ◆ | RunMonitorService.cs:95-143、:324 | 執行時間欄用開跑日，與主機格一致 |
| `InlineStyleUiTests` 白名單是「檔案 → 允許的 CSS 變數」，`runs.js` 已有 `--lf-progress` | ✅ ◆ | InlineStyleUiTests.cs:39-58 | |
| 執行紀錄頁無就地展開；總表有展開前例 | ✅ | runs.js:467-499、:545-646 | |

### 批次 D：說明文字

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| 文字散在各檔無字典；`?`＝Bootstrap popover 三種出口 | ✅ ◆ | ui.js:46/63/145、layout.js:311 | |
| 量級：副標約 32、欄位小字 115、`?` 約 85、說明段約 100、空狀態約 77 | ◆ | 盤點腳本（近似） | 附錄 D |
| 現行規範方向相反且三處重複（WEB-SPEC §8.6-10、§8.6a、DESIGN-SYSTEM §6b） | ✅ 親核 | WEB-SPEC.md:865-871、:915-930；DESIGN-SYSTEM.md:174-178 | 定義只放 §8.6a，其餘引用 |
| 會紅的守門：`EmptyStateGuidanceUiTests`、`PrtgAdminPageUiTests`、`RunsPageUiTests`、`DiskFreeTrendRuleEditorUiTests`（:140 鎖「可用 Tab 鍵」）、`FrontendConsistencyUiTests`、`HandlerWorkPageTests`、`NetiqScanPickerUiTests`、`SetupUxF2Tests`（:520 鎖 Hosts CSV 連結）、`RunActivityBannerTests`、`HandlerLandingTests`、`PrtgOutcomeNoOutputTests`、`ReportRetentionWebTests`、`DocCrossReferenceTests`；`PageHeaderUiTests` 兩條規則（清單內必須有容器並呼叫；清單外不得有容器） | ◆ | LogForesight.Tests/ | 後七類名稱不含 `UiTests`，篩選詞要點名 |
| Hosts「大量指派也可以用 CSV」那行本身是唯一 CSV 入口連結 | ✅ ◆ | Hosts.cshtml:63 | 附錄 D 改「縮成連結文字」 |

### 批次 E：Windows 相依

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| 三專案 `net8.0-windows`，原因是 `EventLogEntryType` | ✅ | 三 csproj | |
| **`System.Diagnostics.EventLog` 9.0.0 的 `lib/net8.0` 組件層級標 `[SupportedOSPlatform("windows")]`，且是 PNSE 替身（52 KB）；探針實測列舉賦值／switch／`is` 都報 CA1416，只有 `==` 不報** | ✅ 親核（重跑探針） | scratchpad/ca1416probe | 正式碼 16 檔 55 處、測試 87 檔引用該列舉 → 必須自訂列舉（§2-30） |
| Windows 專屬呼叫點：`EventLogReader`／`new EventLog`（已 catch）、`DirectoryEntry`／`PrincipalContext`（LDAP 已標註）、`PermissionMonitorService`（已 catch）、`CryptoKeyBootstrapper`（已 guard）、`DynamicAuthenticationProvider` 無條件建構（DI :155）、`LdapCredentialVerifier`、`TestAdConnection` | ✅ ◆ | ServiceCollectionExtensions.cs:155 等 | `UseWindowsService()` **沒有**平台標註、非 Windows 本來不做事，不必守門 ◆ |
| 專案無 `TreatWarningsAsErrors` | ✅ | | 以 `WarningsAsErrors=CA1416` 當真守門 |
| 測試無 OS 條件略過；Node 靠 PATH，找不到靜默略過；IDE 測試總管不繼承 shell 環境 | ✅ ◆ | NodeRuntime.cs:90,156,165 | `.runsettings`＋Homebrew 路徑探測 |
| `.gitattributes` 只有 3 條 `-text`，索引全 LF | ✅ ◆ | `git ls-files --eol` | |
| 約 209 個測試檔用 `DateTime.Now`；32 處 `Environment.NewLine` | ◆ | | 本輪只在 runsettings 固定 TZ；`\r\n` 字面比對要 grep |

## 2. 定案（第二版）

套用 `ui-ux-pro-max` 查到並採用的指引：#115「隱藏的溢出值必須有可操作的 +n 揭露」、#16「不盲目 `overflow:hidden`」、#112／#113「不以固定高度／截字換整齊」、#9「尊重 `prefers-reduced-motion`」、通則「hover 不是唯一資訊來源」「操作用 `button` 並暴露 `aria-expanded`」。沿用專案 Bootstrap＋原生 CSS 與 DESIGN-SYSTEM token，不引入新框架、字型或動畫庫。

每條定案後的 **【驗收】** 是主代理自己執行的檢查；標「使用者驗收」者主代理無法執行。

### A 儀表板

1. KPI 維持三組與組標題。分組對應角色（主管看風險、處理人看交辦、資安看權限），打平會讓處理人找不到自己的卡。
2. 待處理組順序：未處理問題 → 待派工問題 → 我的交辦單 → 未回報主機。
   【驗收】`dashboard-reveal.behavior.mjs`：以管理者（有 pendingDispatch）與處理人（無）兩組資料渲染，斷言待處理組 label 序列。
3. 第一排判定由新模組 `core/reveal.js`（名稱暫定）負責：量格線實際欄數（`getComputedStyle(grid).gridTemplateColumns` 的軌數，**`0px` 軌計入**，量到 0 欄＝尚未佈局＝一律不收合）、對第一排以外的子元素 `setAttribute('hidden','')`、渲染揭露鈕、`ResizeObserver` 觀察格線寬度變化重算、監聽 `window` 的 `lf:font-scale` 事件（`layout.js` 字級切換時派發，新增一行）重算、localStorage 記憶。依賴（量測、observer、storage）全部可注入；回傳 `dispose()`，呼叫端在 `replaceChildren` 前釋放；一個區域多個格線（KPI 三組）共用一個 observer 與一顆揭露鈕。與 `bindRememberedCollapse` 只共用 `lf.collapse.{key}` 鍵的讀寫（抽成兩個小函式）。
   【驗收】行為測試注入 `measureColumns`：2 欄 4 卡藏 2；量到 0 不藏；欄數改為 4 後重算全部可見；`dispose` 後 observer 已 disconnect（替身計數）；`hidden` 以屬性存在判定。mini-dom 補 `removeAttribute`／`hasAttribute`／`classList.remove`／`toggle` 與 `ResizeObserver` 替身。
4. 每個區域一顆揭露鈕（KPI 列一顆、風險類型一顆）：`<button type="button" class="btn btn-sm btn-outline-secondary" aria-expanded aria-controls>`，文字 KPI 為「顯示其餘 N 項指標」、風險類型為「顯示其餘 N 類」，展開後「收合」；被藏的卡中有告警時加註「（M 項告警）」——KPI 以 `text-danger` 數字判定、風險類型以 `lf-card--critical` 判定。放在該區域下方靠右。全部放得下時不渲染。
   【驗收】行為測試斷言按鈕文字（含告警數）、`aria-expanded` 切換、全部放得下時無按鈕；`FrontendConsistencyUiTests` 新增「儀表板揭露鈕為 button 且帶 aria-expanded」原始碼斷言。
5. 隱藏用 `hidden` 屬性（Bootstrap `[hidden]{display:none!important}` 蓋過 `.lf-kpi-card{display:block}`，註解寫明），無動畫；列印樣式 `@media print` 顯示全部卡片並隱藏揭露鈕。
   【驗收】`FrontendConsistencyUiTests` 新增 site.css 斷言：存在 `@media print` 區塊內的 `[hidden]` 還原規則與揭露鈕隱藏規則。
6. 展開狀態記 localStorage（`lf.collapse.dashboard-kpi`、`lf.collapse.dashboard-categories`），預設收合，不分使用者（共用電腦會沿用前一人的狀態，列入 §5）。
   【驗收】行為測試：storage 替身寫入 `open` 後重建→預設展開；寫入失敗（替身擲例外）不外擲。
7. 本頁說明文字依 §2-25：刪 KPI 副標「批次分析算定」×2、「資料不完整或未讀取」；「沒回報 ≠ 沒問題」從副標移到 `title`（WEB-SPEC §9.1 本就規定放 tooltip），PRTG 失聯台數 >0 時副標保留資料句「其中 N 台有 PRTG 監測項目異常」；待派工卡「近 7 天沒人接的問題」是資料口徑句，**保留**；刪卡片旁副標三句（風險類型、重點問題、依群組風險概況）；空狀態三處語意重複的 hint 收斂為同一句。高／中風險日的 `title` 長句保留並補到組標題旁一顆 `?`（「日風險等級由批次分析算定；顯示範圍受『日風險等級顯示』設定影響」是可見性後果，手機也要看得到）。
   【驗收】grep：`批次分析算定`、`資料不完整或未讀取`、`點選卡片檢視該類型` 在 dashboard.js／Dashboard.cshtml 零命中；`FrontendConsistencyUiTests.cs:378` 的 `'沒回報 ≠ 沒問題'` 斷言改為對 `title:` 的斷言；`RunActivityBannerTests`、`SetupUxF2Tests`（鎖 dashboard.js 字串）綠。
8. **保留** `.lf-kpi-card__sub` 的兩行 `min-height` 佔位（第一版要移除，審查指出跨組並排時 grid stretch 不跨組，底線會不齊）；DESIGN-SYSTEM §6b 只刪「說明文字放可見副標，不只藏在 tooltip」一句。移除 `a.lf-stat:hover .lf-card`／`.lf-card--clickable:hover` 的 `transform`（D1）。
   【驗收】`FrontendConsistencyUiTests.cs:53` 不變且綠；:719 的 transform 斷言擴充涵蓋 `.lf-card--clickable` 與 `a.lf-stat:hover`；`kpi-strip.behavior.mjs:70` 不變且綠。
9. 瀏覽器實測（主代理以 `preview_start` 開本機 SQLite 站台、Stub 登入、以測試資料讓八個類別都有數）：寬 1600／1200／768／375 各截圖，記錄每寬度 KPI 各組欄數、是否出現揭露鈕與其文字；展開後重新整理仍展開；切字級「大」後欄數重算；375px 風險類型一欄、揭露鈕文字「顯示其餘 7 類」。結果貼進執行紀錄。

### B 排程窗口與續跑（含 PRTG 兩段）

10. PRTG 第二窗口的切點是 **`FetchDayAsync`**（結構同步、狀態變更同步、主機對應重算、範圍清除、夜間同步狀態）。跳過條件（全部成立）：本趟為 `Trigger=schedule`（含自動補跑）且 Full 範圍；`recentRuns` 中存在非 AI、Full 範圍、`StartedAt.Date == today` 的趟，其 `PrtgDays` 涵蓋本趟全部日期，且每一天的 `PrtgDayStat.SourceSynced == true`（新欄位，暫定名：該趟對該日執行過 Full 範圍的 `FetchDayAsync` 且 `!syncFailed && !historyAffectingFailure`；非 Full 趟一律寫 false；舊 JSON 缺欄為 false）。跳過時規則評估、發佈、補追加、`MarkEvaluated`、workflow 對帳、`RecordPrtgDays` 照常執行（全部讀本地 store），`PrtgDayStat` 加 Note「沿用本日 HH:mm 的結構同步」。手動、console、指定主機更新一律不跳過。觸發式取數的 sensor 失敗不影響下一窗口是否跳過（它不改 `SourceSynced`）。
    【驗收】新測試類 `PrtgDailyPipelineResumeTests`：(a) 今日已有 SourceSynced 的排程趟→`FetchDayAsync` 替身呼叫數 0、`Publish` 內容非空且與 store 中 finding 一致、就緒訊號送出一次、`PrtgDays` 有該日且 Note 含「沿用」；(b) 同條件但本趟是手動→呼叫數 1；(c) 前一趟 `syncFailed`→呼叫數 1；(d) 前一趟為指定主機更新（SourceSynced=false）→呼叫數 1；(e) 舊 JSON 缺 `SourceSynced` 反序列化為 false。突變：把條件改成只看 Outcome→(c)(d) 必紅。
11. 觸發式取數每窗口都跑，sensor 目標排除「該日 `quality='ok'` 小時值已滿 24 列」者（新 store 方法，`UNIQUE(sensor_objid, period_start)` 可 seek，參數分批 `Chunk`；`sampled` 不算；不足 24 列者整日重取——historicdata 本來就回整天；只回 nodata 的 sensor 每窗口重取，接受）。不改 `GetValueFetchTargets` 簽章（回填兩個呼叫端不受影響）；不碰 `PrtgValueAssessment`（這是取數目標篩選，不是數值閘門）。
    【驗收】`PrtgTriggeredValueFetcherTests` 新增：A sensor 24 列 ok→不在目標；B 23 列→在；C 24 列 sampled→在；兩後端 `ToQueryString` 含 `quality` 條件。突變：把 24 改 1→B 必紅。
12. DRY：抽 `BatchRunQueries.LatestPrtgDayStat(runs, day)`（名稱暫定）供 `PrtgReevaluationPlanner.Select`、`SchedulerHostedService.LastScheduledPrtgDay`、`RunMonitorService` 與 §2-10 共用；orchestrator 把 :649 已讀的 `recentRuns` 傳給 pipeline，**不新增 store 查詢**。
    【驗收】grep：`OrderByDescending(r => r.StartedAt)` 搭 `PrtgDays` 的手寫迴圈在上述三處零命中；既有 `PrtgReevaluationPlannerTests`、`RunMonitorServiceTests` 綠。
13. 「接續」可見：趟首里程碑「本趟為今日第 n 個執行窗口（HH:mm～HH:mm）」（自動補跑寫「補跑（窗口外，停止點 HH:mm）」、手動寫「手動執行」）；本機、NetIQ、PRTG 三路以 C-1 的階段 API 開始／結束（§2-19），開始時備註待補量（本機「N 台待補」、NetIQ「M 主機日待補、K 已完成跳過」、PRTG「沿用本日同步」或「同步」），結束時無論成功／失敗／停止／部分都有結束紀錄。**不做趟首短路**（第一版 §2-10，審查指出要先重做逐主機規劃才知道待補量，偏重）：三路各自算出 0 自然結束，權限檢查、保留清除、派工、體檢照常。
    【驗收】`AnalysisOrchestratorMilestoneTests`（新，以替身 store 收集里程碑）：排程趟含「第 n 個執行窗口」；本機例外、NetIQ 失敗、取消三種情境都有對應結束紀錄且 outcome 正確；待補 0 的趟三路結束備註「無待補」。
14. B1：自動補跑趟（`AutoCatchUp` 路徑）不受取數窗口結束停止，改有自己的停止點＝開跑時刻＋錯過窗口的長度，到點優雅停止（與窗口結束同一條停止邏輯，只是比對的時刻不同）；`SchedulerRunState` 加 `CatchUpDeadline`（暫定）；`NoteSkippedScheduleWindow` 條件改為「非排程趟或補跑趟」；Interrupted 補跑維持受窗口停止；狀態卡顯示停止點。
    【驗收】`SchedulerTickOrderTests` 新增：補跑趟開跑後下一 Tick 不取消；超過停止點的 Tick 取消；Interrupted 補跑在窗口結束仍取消；補跑佔住下一窗口時有「略過窗口」提示。突變：拿掉 `CatchUpDeadline` 比對→第二案必紅。
15. B2：不新增欄位。orchestrator 在消費 `BackfillOverride` 的地方以一個共用函式 `EffectiveLookback(override, branchDefault, isCatchUp)`：補跑趟（`CatchUpNote != null`）取 `max(override, 該路常態值)`，其餘維持原語意；本機（:828）與 NetIQ（:1131）都走它，PRTG 維持回望缺口天數。
    【驗收】`ScheduleCatchUpTests` 新增：補跑 override=1、常態 14→本機 lookback 14；NetIQ 設定 3→3；非補跑 override=1→1。突變：把 max 改 min→必紅。
16. B4：`MissingDateFinder`／`RerunDateFinder` 的 `today` 改為必填參數；orchestrator 趟首取一次 `today` 傳給本機、NetIQ、PRTG（:645/:656）、體檢（:696）、權限檔名（:495）、本機掃描上界（:889）；`PrtgDailyPipeline` 三處同源。
    【驗收】grep：`DateTime.Today` 在 `HostDayPostProcessor.cs`、`RerunDateFinder.cs`、`PrtgDailyPipeline.cs` 零命中，`AnalysisOrchestrator.cs` 只剩趟首一處；`MissingDateFinderRequireAiTests`、`RerunDateFinderTests` 改傳 today 且綠；新增一案「today 固定為 D 時跨午夜仍算 D-1」。
17. 排程設定頁與說明書 `10-scheduler.md` 補一段多窗口語意（每窗口一次、本機／NetIQ 只補未完成、PRTG 沿用本日同步、補跑停止點）。
    【驗收】`HelpContentServiceTests`、說明書禁詞守門綠；WEB-SPEC §9.10 新增 AutoCatchUp 段（F）。
18. 實測（主代理，本機 SQLite）：窗口設「現在～+3 分」「+5～+8 分」，第二窗口的執行紀錄含「第 2 個執行窗口」與三路結束紀錄（PRTG 未設定時為「PRTG 未啟用」）；把窗口 1 改成在 Tick 後立即結束再開自動補跑，觀察補跑趟不被取消且狀態卡顯示停止點。

### C 執行時間

19. **C-1 先做、B 使用**：`BatchRun.Phases`（列表，每項：`Key`、`StartedAt`、`FinishedAt?`、`Outcome ∈ success|partial|failed|stopped|skipped`、`Note?`），recorder 提供 `IDisposable Phase(key, note?)`：建構寫 StartedAt＋開始里程碑；`Complete(outcome, note?)` 寫結束；Dispose 時若未 Complete，依 ct 已取消→stopped、否則→failed（例外路徑不留懸空階段）。同一鍵可多個實例（多日 `prtg-triggered`），以實例配對不以鍵配對。`RecordLocalOutcome`／`RecordNetiqOutcome`／`RecordPrtgOutcome` 只管計數，不動階段。所有寫入在 `_countLock` 內並複製後序列化。**快照只在頂層階段結束時追加一列**（本機、NetIQ、PRTG、權限、保留、派工、體檢、數值補齊，約 8 列），子階段（`prtg-*`）只隨 Finish 那列寫出；`_finished` 之後的 `Phase`／`Complete` 只記錄警告不追加列。計時鍵放新類別 `RunTimingPhases`（頂層：`permission-check`、`retention`、`local`、`netiq`、`prtg`、`dispatch`、`checkup`、`prtg-value-fill`、`ai`；子階段沿用 `RunPhases` 的 `prtg-*` 字面值），不進 `RunPhases.ProgressTracks`，規定不得傳給 `progress.Report`。
    【驗收】`BatchRunRecorderPhaseTests`（新）：Complete 寫起訖與 outcome；Dispose 未 Complete 且 ct 取消→stopped、未取消→failed；頂層結束追加列、子階段不追加；Finish 後再 Complete 不追加列且 `FinishedAt` 不被清空；同鍵兩實例各自配對；並行 20 執行緒各建階段無例外且計數正確（確定性：以替身在每次寫入時斷言鎖已持有）。`RunsPageUiTests` 既有反射案維持綠（`ProgressTracks` 未變）；新增反射案「`RunTimingPhases` 每個鍵在 `run-phases.js` 有文案」。
20. 整趟 `StartedAt` 以 orchestrator 的 stopwatch 起點為準：recorder 建構子加可選 `startedAt`（預設 `DateTime.Now`），只有 orchestrator 傳；AI 與測試不改。
    【驗收】`BatchRunRecorderPhaseTests` 一案：傳入的 `startedAt` 寫進 `BatchRun.StartedAt`；`RunOutputRecordingTests` 綠。
21. 執行紀錄頁：每列可就地展開顯示時間軸——每個頂層階段一條水平條，以 `style.setProperty('--lf-phase-start'／'--lf-phase-width')` 定位（`InlineStyleUiTests` 白名單加這兩個變數），旁列「HH:mm:ss～HH:mm:ss（耗時）」；`prtg-*` 子階段依前綴歸在 PRTG 軌下；三路並行條並排；`skipped` 淡色＋「跳過」；進行中階段（無訖）畫到「現在」並標「進行中」；Interrupted 趟畫到最後一個已結束階段並標「異常中斷」。既有 本機／NetIQ／PRTG 三欄在數字下方以小字第二行顯示該路耗時（不加新欄，避免手機表格更寬）。AI 趟單一階段 `ai`。
    【驗收】新 `runs-timeline.behavior.mjs`（切 `renderPhaseTimeline` 函式）：三階段輸入→三條、寬度變數值正確、跳過與進行中標記；`RunsPageUiTests` 新增「時間軸不得有 `style=` 屬性、只用兩個白名單變數」；瀏覽器實測一趟手動執行後展開截圖。
22. 執行總表：每個日期列新增欄組「執行時間（當天開跑）」：最早開始、最晚結束、本機／NetIQ／PRTG／AI 各路耗時（同日多趟取聯集）。歸屬鍵＝`StartedAt.Date == D`（與主機格一致；多日回補趟只算在開跑日）；AI 趟同鍵（AI 窗口全天時會含消化其他日積壓的時間，欄名標「AI 執行」不標「該日 AI」）。無趟顯示「—」；Interrupted 趟顯示開始與最後活動時刻並標記，不計入各路聯集。`GetDaySummaries` 要另外保留 AI 趟清單給這個欄組，不得進 `AllHosts`。
    【驗收】`RunMonitorServiceTests` 新增：同日兩趟重疊→聯集；Interrupted 排除且附註；AI 趟不出現在主機列；無趟「—」。突變：聯集改相加→必紅。
23. 舊紀錄無 `Phases` 顯示「—」，不從舊 log 推算。
    【驗收】`RunMonitorServiceTests` 一案：`Phases=null` 的趟三路耗時為 null、前端顯示「—」（行為測試）。
24. C1：`RunDetailDto` 帶 `Status`（同 `BatchRunStatus.Compute`）與 `Interrupted`，modal 改讀後端。C2：Interrupted 趟耗時顯示「—（異常中斷）」；`CloseUnfinishedRuns` 回看改為保留天數。C3：尾段輸出佇列存 `(時間, 內容)`，寫出時用原時間。
    【驗收】`RunMonitorServiceTests`：Error>0 的趟 detail.Status=failed；`InterruptedRunTests` 新增：8 天前未結束的趟在啟動時被關閉；`RunOutputRecordingTests` 新增：超量後尾段行 `LoggedAt` 早於 `FinishedAt`。

### D 說明文字

25. 三級判準只在 WEB-SPEC §8.6a 定義一次（取代現行二分法）；§8.6-10 與 DESIGN-SYSTEM §6b「資訊密度」只留一句「見 WEB-SPEC §8.6a」。判準：
    - **保留常駐**：不可逆操作、資料可見性後果、會擋住送出的硬性限制、特殊值語意（「留空＝」「0＝」）、空狀態的「下一步」、含資料的動態狀態句、影響當下決策的警示與口徑提醒。
    - **收進 `?`**：驗證細節、營運調校指引、格式範例、資料來源說明、口徑定義。
    - **刪除**：複述欄位名或標題、複述版面已呈現的事、與旁邊 `?` 或另一段重複、重複出現的同句提示。
    - 總則：「版面與標籤能說明的就不再寫一遍；真正要講的放 `?`；只有後果性的句子常駐。」含 `<code>` 的格式說明維持常駐（技術例外不變）。
    【驗收】`DocCrossReferenceTests` 綠；grep WEB-SPEC：「常駐顯示 vs 收進 icon」舊標題零命中、「三級」判準只出現在 §8.6a 一處。
26. 頁首規則：`renderPageHeader` 至少要有 subtitle／actions／help 其中之一，否則該頁移出 `PageHeaderUiTests.AppliedPages` 並移除容器；RecordDetail／HostDetail 頁首只留主體名稱。
    【驗收】`PageHeaderUiTests` 新增「`renderPageHeader` 呼叫不得三者皆空」原始碼斷言；`HandlerLandingTests` 綠。
27. Prtg 六個 callout 刪除，改成：每個頁籤標題旁一顆 `?` 承接「這頁做什麼」；PRTG 未設定時以一處空狀態列出啟用順序（連線 → 擷取參數 → 探測 → 回填 → 同步）；三句後果／口徑句常駐：「各數字的分母不同，不能互相換算成比例」、「結果包含不在主機清單內的裝置」、「不會開始取數；取數要到『擷取參數』勾選啟用」。「尚未執行判定查詢…」×4 改為一處 `renderEmpty`（下一步＝重新計算）。
    【驗收】`PrtgAdminPageUiTests` 的 callout 測試改為「不得有 `aria-label="這個頁籤的用途"`、六個頁籤各有 `?`、三句常駐仍在」；`EmptyStateGuidanceUiTests` 綠。
28. 附錄 D 逐頁處置（已依審查修訂），每階段產出最終三欄清單寫進執行紀錄。
    【驗收】每階段結束執行 grep 腳本：附錄 D 標「刪」的句子在對應檔零命中、標「留」的句子仍命中；定向測試篩選（§6 D 列）綠；`LocalizationLintTests`、`HandlerFacingWordingUiTests` 綠；瀏覽器實測 Settings／Prtg／Runs 三頁截圖，並以 `javascript_tool` 查 `.lf-page-header:empty`、連續兩個空 `.text-muted` 容器為 0。
29. Setup 與 Settings 同欄位的說明文字兩邊一致（同刪同留）。
    【驗收】grep 兩檔對應句子命中數相等；`SetupUxF2Tests`、`SetupAd/Ai/MailInlineUiTests` 綠。

### E macOS

30. TFM 改 `net8.0`（三專案）。`EventLogEntryType` 以自訂列舉 `LogEntryType`（暫定名，成員名稱與整數值完全相同：Error=1、Warning=2、Information=4、SuccessAudit=8、FailureAudit=16）取代所有正式碼與測試的使用；只在 `EventRecordMapper` 讀真實 Windows 事件的一處做轉換。JSON（`JsonStringEnumConverter` 以名稱序列化）與 DB（整數）契約不變。`Directory.Build.props` 加 `<WarningsAsErrors>$(WarningsAsErrors);CA1416</WarningsAsErrors>`，之後每次建置與 CI 都守，不另寫原始碼掃描守門（會變假守門）。
    【驗收】`dotnet build` 零警告零錯誤（CA1416 已是錯誤）；grep `EventLogEntryType` 在 Core／Web／Tests 只剩 `EventRecordMapper` 的轉換點；全量測試綠；一段既有 JSON 樣本（測試已有的紀錄 fixture）反序列化後 `EntryType` 值不變。
31. 平台守門以實際建置的 CA1416 錯誤清單為準（E-1 第一步：改 TFM、建置、取清單），不用推測清單。已知要處理：`EventLogService`（非 Windows 建構不擲、讀取回 `ReadFailed`，固定原因「此平台不支援 Windows Event Log 讀取」，整趟只輸出一行、不計 warn）、`EventRecordMapper`、`PermissionMonitorService`（非 Windows 回空結果＋一行輸出）、`DynamicAuthenticationProvider` 建構（非 Windows 直接用 fallback provider）、`LdapCredentialVerifier`、`TestAdConnection`（非 Windows 回「此平台不支援 AD 連線測試」錯誤碼，登錄 `ApiErrorCodes`）、`CryptoKeyBootstrapper`（Unix 以 `File.SetUnixFileMode` 設 600）。`UseWindowsService()` 不加守門。`EventLogService`／`PermissionMonitorService` 的平台判定以可注入的 `Func<bool> isWindows` 實作（預設 `OperatingSystem.IsWindows`），讓非 Windows 分支在 Windows 上可測。
    【驗收】`PlatformGuardTests`（新）：注入 `isWindows=false` 時 `EventLogService.Read` 回 ReadFailed 且原因固定、`PermissionMonitorService.Check` 回空且 Output 一行；`DynamicAuthenticationProvider` 在 `isWindows=false` 時解析為 fallback；`ApiErrorCodes` 新碼有狀態碼表項（既有守門）。
32. 測試在 Mac 上會「失敗而非略過」的 Windows 專屬案例，新增 `WindowsFact`／`WindowsTheory`（照 `NodeFact` 形式，非 Windows 回 Skip 並附原因）；哪些測試要掛由 E-1 在 Windows 上先掛「真的呼叫 Windows API」的已知類別（`CryptoKeyBootstrapperTests` 等），再由使用者第一次 Mac 全量的紅燈清單補齊（執行紀錄登記，第二次 Mac 全量應零紅）。
    【驗收】Windows 上 `WindowsFact` 案例照常執行；`NodeRuntimeTests` 同型守門新增「`WindowsFact` 在非 Windows 回 Skip」（以反射建構屬性並檢查 `Skip` 字串，不依賴 OS）。
33. 測試環境：Tests 專案加 `test.runsettings`（`LF_REQUIRE_NODE=1`、`TZ=Asia/Taipei`）並在 csproj 以 `RunSettingsFilePath` 指向；`NodeRuntime` 在 PATH 找不到時補探 `/opt/homebrew/bin/node`、`/usr/local/bin/node`。
    【驗收】Windows 上 `dotnet test` 讀到 runsettings（以一個讀 `LF_REQUIRE_NODE` 的守門測試確認等於 1）；`NodeRuntimeTests` 新增探測順序案（以替身檔案系統）。
34. `.gitattributes` 加 `* text=auto`（保留三條 `-text`）；README「macOS 開發」段（.NET 8 SDK、Node、`dotnet dev-certs https --trust`、`dotnet test` 已帶 runsettings、`dotnet run` 以 `Auth:Provider=Stub` 登入、本機主機分析在 Mac 會記「此平台不支援」）；CLAUDE.md 測試段註明平台；BACKLOG 記時區抽象（觸發：Mac 出現日界／DST 偶發紅）。
    【驗收】`git add --renormalize . && git status --porcelain` 為空；grep 測試專案 `"\r\n"` 字面比對清單（有則逐一改用 `Environment.NewLine` 或 normalize），記進執行紀錄。
35. **使用者驗收（Mac）**：`dotnet restore`／`build` 零錯誤；`dotnet test` 全綠（Skip 數＝Windows 的 10 ＋ `WindowsFact` 數，記錄實際數字）；`dotnet run --project LogForesight.Web` Stub 登入、儀表板與排程頁可開、手動「立即執行」不崩潰且執行輸出有「此平台不支援」兩行。全綠後主代理才做 E-3（CI `macos-latest` job，設兩個環境變數）。

### F 文件

36. WEB-SPEC §9.1（F1 三處＋揭露鈕）、§9.10（多窗口續跑、AutoCatchUp 與停止點、執行時間欄、時間軸）、§10／§11（`lf_log_lines` 口徑、`phases`）、§8.2／§8.6／§8.6a（D）；PRTG-SPEC §3（`FetchDayAsync` 沿用條件、`SourceSynced`、已取 sensor 排除）；DB-SPEC（`batch_runs` JSON 欄位 `phases`、`prtg_days[].sourceSynced`）；DESIGN-SYSTEM §6b；README；BACKLOG；說明書 `10-scheduler.md`、`02-dashboard.md`、PRTG 相關頁。
    【驗收】`DocCrossReferenceTests`、`HelpContentServiceTests`、說明書禁詞守門綠；grep 現行文件敘事字眼（原本／後來／改為／第 N 輪）零新增。

## 3. 批次與改動

### 批次總覽

| 順序 | 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|---|
| 1 | A | 儀表板揭露、順序、本頁說明文字、D1 | 中 | 無 |
| 2 | E | 自訂列舉＋TFM＋平台守門＋`WarningsAsErrors`＋runsettings＋文件（E-3 等 Mac 驗證） | 中大 | 無；之後所有批次在 CA1416 守門下開發 |
| 3 | C-1 | 階段計時模型、recorder API、`RunTimingPhases`、`StartedAt`、C3 | 中 | E（列舉改名後再動 Core） |
| 4 | B | PRTG `FetchDayAsync` 沿用、已取 sensor 排除、共用查詢、接續里程碑（用 C-1 API）、B1／B2／B4 | 大 | C-1 |
| 5 | C-2／C-3 | 服務層耗時與每日欄組、C1／C2、前端時間軸 | 大 | B（Recorder／Orchestrator 已定） |
| 6 | D | 規範、全站說明文字、守門測試 | 大（面廣） | A |
| 7 | F | 文件對齊 | 小 | 全部 |

### 各批次改動與驗收指令

**A**：改動＝`core/reveal.js`（新）、`core/ui.js`（抽 collapse 鍵讀寫）、`layout.js`（字級事件一行）、`dashboard.js`、`Dashboard.cshtml`、`site.css`、`JsBehavior/dashboard-reveal.behavior.mjs`（新）＋`mini-dom.mjs` 補方法、`FrontendConsistencyUiTests`、`DESIGN-SYSTEM §6b`、`WEB-SPEC §9.1`、`02-dashboard*.md`。
驗收指令：`dotnet test --filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~Dashboard|FullyQualifiedName~SetupUxF2|FullyQualifiedName~RunActivityBanner"`；§2-9 實測。突變：第一排判定改第二排→行為測試紅；`hidden` 改 class→紅。

**E**：改動＝`Directory.Build.props`、三 csproj、`LogEntryType`（新）＋全部引用點、`EventRecordMapper`、`EventLogService`、`PermissionMonitorService`、`ServiceCollectionExtensions`、`LdapCredentialVerifier`、`SystemSettingsService`、`CryptoKeyBootstrapper`、`ApiErrorCodes`、`WindowsFact`（新）、`test.runsettings`（新）、`NodeRuntime`、`PlatformGuardTests`（新）、`.gitattributes`、README、CLAUDE.md、BACKLOG、（E-3）ci.yml。
驗收指令：`dotnet build --no-incremental`（零警告）；全量 `dotnet test`；§2-30～34 的 grep。

**C-1**：改動＝`BatchRunStore`（`Phases`、`PrtgDayStat.SourceSynced` 欄位先一起加）、`BatchRunRecorder`、`RunTimingPhases`（新）、`run-phases.js`（文案表）、`AnalysisOrchestrator`（頂層階段掛點、`startedAt`）、`PrtgDailyPipeline`／`NetiqPipelineService`／`AiAnalysisHostedService`（階段掛點）、`BatchRunRecorderPhaseTests`（新）、`RunsPageUiTests` 反射案、DB-SPEC。
驗收指令：`dotnet test --filter "FullyQualifiedName~BatchRun|FullyQualifiedName~RunOutputRecording|FullyQualifiedName~CancellationSemantics|FullyQualifiedName~InterruptedRun|FullyQualifiedName~RunsPage|FullyQualifiedName~AiAnalysis"`。突變：Dispose 未 Complete 一律寫 success→紅；頂層結束不追加列→紅。

**B**：改動＝`PrtgDailyPipeline`、`PrtgTriggeredValueFetcher`、`EfPrtgStore`＋介面（已取 sensor 查詢）、`BatchRunQueries`（新，共用查詢）、`PrtgReevaluationPlanner`、`SchedulerHostedService`、`SchedulerRunState`、`ScheduleCalculator`（停止點計算）、`AnalysisOrchestrator`（接續里程碑、`EffectiveLookback`、`today` 下傳）、`HostDayPostProcessor`、`RerunDateFinder`、`NetiqPipelineService`、測試（`PrtgDailyPipelineResumeTests` 新、`PrtgTriggeredValueFetcherTests`、`SchedulerTickOrderTests`、`ScheduleCatchUpTests`、`AnalysisOrchestratorMilestoneTests` 新、`MissingDateFinderRequireAiTests`、`RerunDateFinderTests`）、PRTG-SPEC §3、WEB-SPEC §9.10、`10-scheduler.md`。
驗收指令：`dotnet test --filter "FullyQualifiedName~Schedule|FullyQualifiedName~Scheduler|FullyQualifiedName~InterruptedRun|FullyQualifiedName~PrtgReevaluation|FullyQualifiedName~AiAnalysis|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgTriggered|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~MissingDateFinder|FullyQualifiedName~RerunDate|FullyQualifiedName~BatchRun|FullyQualifiedName~AnalysisOrchestrator"`；§2-18 實測。

**C-2／C-3**：改動＝`RunMonitorService`、`RunDtos`、`RunsController`、`BatchRunStore.CloseUnfinishedRuns` 與呼叫端、`runs.js`、`site.css`、`InlineStyleUiTests` 白名單、`runs-timeline.behavior.mjs`（新）＋`InlineData`、`RunMonitorServiceTests`、`RunsPageUiTests`、WEB-SPEC §9.10／§10／§11。
驗收指令：`dotnet test --filter "FullyQualifiedName~BatchRun|FullyQualifiedName~RunMonitor|FullyQualifiedName~RunsPage|FullyQualifiedName~InterruptedRun|FullyQualifiedName~PrtgOutcomeNoOutput|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`；§2-21 實測。

**D**：改動＝WEB-SPEC §8.2／§8.6／§8.6a、DESIGN-SYSTEM §6b（D-1）；附錄 D 三組的 cshtml／js／測試（D-2～D-4）；被引用的 HelpContent。
驗收指令：`dotnet test --filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~HelpContent|FullyQualifiedName~LocalizationLint|FullyQualifiedName~HandlerWorkPage|FullyQualifiedName~HelpNavigation|FullyQualifiedName~SetupUxF2|FullyQualifiedName~SetupAd|FullyQualifiedName~SetupAi|FullyQualifiedName~SetupMail|FullyQualifiedName~HandlerLanding|FullyQualifiedName~ReportRetention|FullyQualifiedName~RunActivityBanner|FullyQualifiedName~DocCrossReference"`；§2-28 grep 腳本與實測。

**F**：驗收指令：`dotnet test --filter "FullyQualifiedName~DocCrossReference|FullyQualifiedName~HelpContent|FullyQualifiedName~HelpWriting|FullyQualifiedName~HelpManual|FullyQualifiedName~HelpNavigation"`；敘事字眼 grep。

**收官**：全量 `dotnet test`，數字與基線差寫進「體檢交接」。

## 4. 文件、說明書與 BACKLOG 同步（隨批）

| 批次 | 文件 |
|---|---|
| A | DESIGN-SYSTEM §6b（只改 KPI 條目）、WEB-SPEC §9.1、HelpContent/02-dashboard*.md |
| E | README、CLAUDE.md 測試段、BACKLOG（時區抽象） |
| C-1 | DB-SPEC（`phases`、`sourceSynced`） |
| B | WEB-SPEC §9.10、PRTG-SPEC §3、HelpContent/10-scheduler.md、BACKLOG（補跑只看最近一個已結束窗口） |
| C-2／C-3 | WEB-SPEC §9.10／§10／§11、HelpContent/10-scheduler.md |
| D | WEB-SPEC §8.2／§8.6／§8.6a、DESIGN-SYSTEM §6b（只改資訊密度條目）、被引用的 HelpContent |

## 5. 明確不做（本輪定案）

- 儀表板展開狀態不同步到後端偏好、不分使用者；不做 KPI 依數值動態換位。
- 不改「每窗口實例觸發一次」；不做趟首短路；PRTG 不做子階段各自的完成標記（切點只有 `FetchDayAsync`）。
- **B3 不改**：跟隨取數的 AI 趟不受 AI 窗口限制是 WEB-SPEC §9.10 明寫的刻意設計；若日後要讓窗口外只消化近日層，需使用者另行定案（影響「手動回補 N 天後 AI 只判讀 2 天」）。
- 不回填舊執行紀錄的階段時間；不新增 AI↔取數關聯鍵。
- `?` 氣泡只刪與旁邊文字重複者。
- 不做 `IClock`／固定時區重構；不改具名 Mutex；不寫 Linux systemd 部署文件；`UseWindowsService()` 不加守門。
- 補跑只看最近一個已結束窗口（記 BACKLOG）。
- 只回 nodata 的 sensor 每窗口重取（接受，寫進 PRTG-SPEC）。

## 6. 驗收篩選詞命中數（dev@b4757155 `--list-tests`，7,246 條）

| 批次 | 第二版篩選詞命中（§3 的 `--filter` 字串實跑） | 相對第一版新增的詞 | 新增測試落點（寫完後命中數必須增加） |
|---|---|---|---|
| A | 480 | `SetupUxF2`（24）、`RunActivityBanner`（17） | `DashboardRevealBehaviorTests`（名含 `BehaviorTests`） |
| E | 全量 7,246 | — | `PlatformGuardTests`、`NodeRuntimeTests` |
| C-1 | 167 | — | `BatchRunRecorderPhaseTests` |
| B | 452 | `RerunDate`（15）、`AnalysisOrchestrator`（目前 1） | `PrtgDailyPipelineResumeTests`、`AnalysisOrchestratorMilestoneTests` |
| C-2／3 | 518 | `PrtgOutcomeNoOutput`（7） | `RunsTimelineBehaviorTests` |
| D | 508 | `SetupUxF2`、`SetupAd/Ai/Mail`、`HandlerLanding`（13）、`ReportRetention`（16）、`RunActivityBanner`、`DocCrossReference`（1） | — |
| F | 22 | — | — |

每批新測試寫完再跑一次確認命中數增加；E-1 另確認 `CryptoKeyBootstrapper`（12）、`DynamicAuthenticationProvider`（8）兩類在掛 `WindowsFact` 後 Windows 上仍執行。

## 7. 規劃完成後複檢（第二版）

- **與既有設計的衝突**：推翻 DESIGN-SYSTEM §6b「說明放可見副標」與 WEB-SPEC §8.6-10／§8.6a 二分法（D-1 註明以 §8.6a 三級判準取代）；保留「空副標兩行佔位」（第一版要刪，撤回）。觸發判定仍走單一查詢點（補跑維持 `Trigger=schedule`，只加停止點）。「不要替 PRTG 數值再加閘門」：已取 sensor 排除是取數目標篩選，不碰 `PrtgValueAssessment`。「不在不可信範圍清除」：沿用路徑不執行 Purge。「AI 待補順序只有一份判定」：B3 不改，無涉。
- **批次之間**：C-1 先定 Phase API，B 的里程碑直接用它，不留兩份呼叫點；B 與 C-2 都改 `SchedulerHostedService`（B 改停止邏輯、C-2 改回看參數）依序做；A 與 D 都改 §6b 但各改不同條目；E 先行讓後續批次在 CA1416 守門下開發。
- **什麼算一個／分母為零**：揭露鈕隱藏 0 不渲染；量到 0 欄不收合；每日執行時間無趟「—」；PRTG 沿用條件缺任一項即重做；已取 sensor＝24 列 ok，23 列重取。
- **破壞性判準的反例**：已取排除不認 `sampled`；沿用條件排除非 Full 趟與 syncFailed 趟。
- **單向閘門的繞過**：PRTG 沿用只對排程趟，手動一律重做；補跑停止點到點就停，不會無限期。
- **移除類的依賴方**：`EventLogEntryType` 全部引用點（16＋87 檔）由建置錯誤逐一指出，不靠 grep；`translateY` 只影響儀表板；Prtg callout→測試改寫並保留三句常駐；頁首副標→不得三者皆空的新守門。
- **升級與既有資料**：`Phases`／`SourceSynced` 缺欄為 null／false；列舉名稱與值不變，JSON 與 DB 相容；`.gitattributes` 不改索引。
- **審查發現的處置**全部記在 §10，無遺留待決。

複檢完成。

## 8. 階段拆分與規格階段設計修正

| 階段 | 範圍 | 執行者 | 白名單（暫定，以 §3 改動清單為準） |
|---|---|---|---|
| A-1 | `core/reveal.js`＋mini-dom 補方法＋行為測試＋CSS（含 print） | sonnet | reveal.js、ui.js（collapse 鍵函式）、layout.js（事件一行）、site.css、JsBehavior、對應 C# 測試 |
| A-2 | dashboard.js／Dashboard.cshtml 套用、順序、說明文字、`?`、守門測試、規範 | sonnet | dashboard.js、Dashboard.cshtml、FrontendConsistencyUiTests、DESIGN-SYSTEM §6b KPI 條、WEB-SPEC §9.1、02-dashboard*.md |
| E-1 | TFM＋`LogEntryType`＋CA1416 清單修到零＋平台守門＋`WindowsFact`＋`PlatformGuardTests` | sonnet（建置錯誤驅動，機械性高） | §3E 改動清單 |
| E-2 | runsettings、NodeRuntime 探測、`.gitattributes`、README、CLAUDE.md、BACKLOG | 主代理 | — |
| E-3 | CI macos job（使用者 Mac 驗證後） | 主代理 | ci.yml |
| C-1 | Phases 模型＋recorder API＋`RunTimingPhases`＋掛點＋`startedAt`＋C3 | opus | §3C-1 清單 |
| B-1 | `FetchDayAsync` 沿用＋`SourceSynced`＋已取 sensor 排除＋`BatchRunQueries`＋測試 | opus | PrtgDailyPipeline、PrtgTriggeredValueFetcher、EfPrtgStore＋介面、BatchRunQueries、PrtgReevaluationPlanner、SchedulerHostedService（只改 LastScheduledPrtgDay 改用共用）、RunMonitorService（同）、測試 |
| B-2 | 接續里程碑（用 Phase API）、B4 `today` 下傳、`EffectiveLookback`（B2） | opus | AnalysisOrchestrator、HostDayPostProcessor、RerunDateFinder、NetiqPipelineService、PrtgDailyPipeline（三處 Today）、測試 |
| B-3 | B1 補跑停止點、狀態卡 | opus | SchedulerHostedService、SchedulerRunState、ScheduleCalculator、ScheduleDtos、runs.js（狀態卡一行）、測試 |
| C-2 | 服務層耗時與每日欄組、C1、C2 | opus | RunMonitorService、RunDtos、RunsController、BatchRunStore.CloseUnfinishedRuns、SchedulerHostedService（回看參數）、測試 |
| C-3 | runs.js 時間軸、run-phases.js 文案、CSS、UiTests、行為測試、文件 | sonnet | runs.js、run-phases.js、site.css、RunsPageUiTests、InlineStyleUiTests 白名單、WEB-SPEC、DB-SPEC |
| D-1 | 規範改寫 | 主代理 | WEB-SPEC §8.x、DESIGN-SYSTEM §6b 資訊密度條 |
| D-2 | 附錄 D 第一組 | sonnet | 該組 cshtml／js／測試 |
| D-3 | Settings＋Setup | sonnet | Settings／Setup 兩頁與測試 |
| D-4 | Prtg | sonnet | Prtg 三支 js、cshtml、PrtgAdminPageUiTests |
| F-1 | 文件對齊 | 主代理 | docs/*、HelpContent |

規格階段的簡化一律回寫到下方「設計修正」並指明補回段。

**設計修正**：
- A-2（實測推翻 §1A「組格線不改」）：1600px 時各組整組換列，KPI 實際佔兩排，且 14rem 上限在組間留白，達不到使用者要的「一橫排」。`kpiStrip` 加 `singleRow` 選項（只有儀表板用，其他頁的 KPI 不變）：576px 以上各組同排依卡數分寬、`auto-fit minmax(9rem,1fr)` 撐滿；575px 以下仍每組兩欄。
- E-1（實作發現）：`LogEntryType` 轉換點是 `EventRecordMapper.FromClassic`；非 Windows 的本機掃描與權限監控以結果上的 `PlatformUnsupported` 旗標告知呼叫端，由 orchestrator 各印一行，不走 Warn。`[WindowsFact]` 目前只掛 `DynamicAuthenticationProviderTests`，其餘由使用者第一次 Mac 全量的紅燈清單補齊（§2-32）。
- A-1：`revealFirstRow` 只用 `hidden` 屬性（`el.hidden`），mini-dom 不必補 `removeAttribute` 等方法；展開鍵與 `bindRememberedCollapse` 同前綴，兩行讀寫不抽共用函式（抽了要改 ui-collapse 行為測試的注入，得不償失）。

## 9. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| E-1 | sonnet（API 連線中斷，主代理接手驗收） | 完成 | `dotnet build --no-incremental` 0 錯誤、CA1416 零；全量 7,557 綠／10 略過／1 紅（新行為腳本未支援無參數列案例，已修並定向重跑） | 代理跑全量途中斷線，改檔已齊，主代理審 diff 與重跑 |
| E-2 | 主代理 | 完成 | `NodeRuntimeTests`、`PlatformGuard`、Help 定向綠；`git add --renormalize` 無差異 | Mac 全量與 E-3（CI job）等使用者實機（§2-35） |
| A-1／A-2 | 主代理 | 完成 | 定向 492 綠；突變「第二排」「改 class」皆紅；實測 1600／1200／768／375：KPI 同排（375 為每組兩欄）、無橫向捲動、無主控台錯誤、展開後重新整理仍展開 | 實測推翻「組格線不改」（見設計修正）；字級切換由行為測試驗（實測「大」未跨越欄數門檻） |

## 10. 審查回合紀錄（第一版 → 第二版）

兩個 opus 審查代理（使用者／管理者走查；程式面／整體影響）共提出 8 條嚴重、13 條中等、若干輕微與附錄 D 異議。主代理逐條重驗後處置：

| 來源 | 發現 | 處置 |
|---|---|---|
| 走查 S1／程式 S1 | 跳過「自身段」會讓記憶體登錄簿為空：第二窗口新落地的主機日拿不到 PRTG finding、workflow 記 Waiting、無 `PrtgDays`；第一版對「不發就緒訊號」的描述也錯（finally 會補發空集合） | **採納**：切點改 `FetchDayAsync`（§2-10） |
| 程式 S2／走查 M7 | 完成判定沒排除指定主機更新的趟 | **採納**：`SourceSynced` 只在 Full 趟為真 |
| 程式 S3 | 單顆 sensor 失敗也算 partial，會讓第二窗口整段重做 | **採納**：以 `SourceSynced` 判定，不看 Outcome |
| 走查 S2／程式 S8 | B1 豁免範圍過寬（含 Interrupted 補跑）、補跑會跑進上班時間 | **採納**：只對自動補跑；加停止點＝開跑＋錯過窗口長度 |
| 走查 S3 | B3 是 WEB-SPEC 明寫的刻意設計，改了會讓手動回補 N 天後 AI 只判讀 2 天 | **採納**：本輪不改，移 §5 |
| 走查 S4／程式 S6 | 每日聯集與 AI 歸屬口徑會誤讀；總表主機格其實是開跑日 | **採納**：改開跑日鍵、加最早開始／最晚結束、AI 欄名改「AI 執行」 |
| 走查 S5 | 刪 `min-height` 後跨組底線不齊 | **採納**：保留佔位 |
| 程式 S4／S5 | `EventLogEntryType` 組件層級標 Windows，CA1416 零與不改型別衝突；守門清單有漏有多 | **採納**（主代理重跑探針確認）：自訂列舉；清單以建置錯誤為準；`UseWindowsService` 不守門 |
| 程式 S7 | 每階段邊界追加快照會平方成長、並行與 Finish 後順序問題 | **採納**：只在頂層階段結束追加；鎖內複製；Finish 後不追加 |
| 走查 M1 | 推算桌機不會觸發揭露 | **不採**：使用者截圖內容區約 1,600px 時三組各兩欄，會觸發；以實測為準 |
| 走查 M2 | 手機上一顆共用鈕對應不直覺 | **部分採納**：按鈕文字含「指標／類」區分；不做每組一顆（三顆鈕更吵） |
| 走查 M3／程式 A | observer 洩漏、字級不重算、0px 軌、列印 | **採納**：dispose、`lf:font-scale` 事件、計入 0px、print 規則 |
| 走查 M4 | 未回報卡刪 hint 後 tooltip 消失；可見性後果只剩 hover | **採納**：補 `title`；組標題旁 `?` |
| 走查 M6／程式 B2 | NetIQ 回補也被縮短；新增欄位要同步 `ComposeEffectiveRequest` | **採納**：`EffectiveLookback` 對兩路取 max，不加欄位 |
| 走查 M8 | 「任一 ok 列」會讓部分小時永遠不補 | **採納**：24 列才算已取 |
| 走查 M9／程式 B | 趟首短路偏重且範圍未定 | **採納**：不短路 |
| 走查 M10 | Interrupted 只顯示「—」像沒跑 | **採納**：顯示開始與最後活動 |
| 走查 M12／附錄異議 | Prtg callout 刪掉後首次啟用流程斷；多條「刪」其實是後果句 | **採納**：頁籤 `?`＋未設定空狀態＋三句常駐；附錄 D 逐條修訂 |
| 走查 M13／程式 E | 文件寫環境變數不夠；IDE 不繼承 PATH；原始碼掃描是假守門 | **採納**：runsettings、Homebrew 探測、`WarningsAsErrors` |
| 程式 C | `RunPhases` 不可混入計時鍵；父鍵多餘；同鍵多實例配對；Phase API 應先於 B | **採納** |
| 程式 D | 判準三處重複；空頁首 | **採納**：只在 §8.6a 定義；頁首不得三者皆空 |
| 兩路 §6 | 改動有驗收無（約 20 條）；篩選詞漏七個測試類 | **採納**：每條定案附驗收；§6 補詞 |

---

## 附錄 D：說明文字逐頁清單（第二版；實作階段每段產出最終三欄清單）

欄位：頁面｜文字（原文節錄）｜位置｜處置（刪／`?`／留）｜依據（§2-25 款）；★＝審查後改判

**第一組：一般頁面**

| 頁面 | 文字 | 位置 | 處置 | 依據 |
|---|---|---|---|---|
| Records | 「請調整篩選條件或日期區間。」×4 | records.js | 留，同頁統一一種寫法 | 空狀態下一步 |
| Records | 「只套用到『尚未有人接手』的主機——已建立交辦的主機一律略過…」 | records.js:1316 | 留 | 後果 |
| Records ★ | 「交辦會建立交辦單：…之後同一問題的新風險日會自動掛進單裡，直到結案」 | records.js:1564 | 留（持續性後果） | 後果 |
| Records | 表頭 `?` 六顆 | records.js | 留 | 已是 `?` |
| RecordDetail | 「報告、偵測訊號與資料涵蓋率」「AI 問答與類型分布」 | RecordDetail.cshtml:114,122 | 刪 | 複述版面 |
| RecordDetail | 頁首「逐項處理這台主機這一天的風險問題。」 | record-detail.js:70 | 刪，頁首只留主體名稱（`HandlerLandingTests` 同步） | 複述 |
| handling-panel | 三句（身分檢視／狀態連動／套用覆蓋） | handling-panel.js:123,148,363 | 留 | 後果／動態 |
| HostDetail | 頁首「{主機} 的風險時間軸與處理狀況」 | host-detail.js | 刪，只留主機名 | 複述 |
| HostDetail | 「依來源／Event 分組，點列前往最近一次出現的當日詳情」 | HostDetail.cshtml:53 | 刪 | 複述版面 |
| HostDetail | `?`「立即更新回望天數」 | :21 | 留 | `?` |
| Hosts | 「選取後可批次設定群組；已併入其他主機的列不會被選到。」 | Hosts.cshtml:53 | 前半刪、後半留 | 可見性 |
| Hosts | 「決定哪些部門看得到這台主機。未分組時只有 admin 看得到。」 | :149 | 留 | 可見性 |
| Hosts | 「負責人會自動取得所負責主機的檢視權限與處理能力。」 | :341 | 留 | 可見性 |
| Hosts ★ | 「大量指派也可以用 CSV」 | :63 | 留，縮成連結文字「用 CSV 大量指派」（`SetupUxF2Tests.cs:520` 同步） | 唯一入口 |
| Hosts | 其餘四條純描述 form-text | | `?` | 收進 |
| Groups | 「勾選＝該部門可看見該群組的主機」 | groups.js:41 | 留 | 可見性 |
| Groups | `alert-light` 說明段 | :44 | `?` | 收進 |
| Users | 「AD 帳號，例如 DOMAIN\user1。建立後不可修改。」 | Users.cshtml:65 | 「建立後不可修改」留，格式範例 `?` | 不可逆 |
| UserDetail | 「進行中的跨日問題案件…」「已結案的問題案件（新到舊）」 | :27,35 | 刪 | 複述 |
| UserDetail | 兩顆長 `?` | :16,43 | 留、精簡 | `?` |
| Reports | 「不受『顯示範圍』篩選影響」 | reports.js:402 | 留 | 後果 |
| Reports | callout 一段 | | `?` | 收進 |
| Rules | 頁首「規則儲存前會自動驗證，不合格不會寫入。」 | rules.js:62 | 刪（頁首仍有 help，不會空） | 複述 |
| Rules | 「抑制只關掉通知與風險升級，事件仍照常聚合與紀錄。」 | Rules.cshtml:99 | 留 | 後果 |
| Rules | 「必須以 custom- 開頭」「至少填一個」「留空＝不看訊息內容」 | :140,165,178 | 留 | 硬性限制／特殊值 |
| Rules ★ | 「可用 Tab 鍵操作…」 | :237 | 刪（`DiskFreeTrendRuleEditorUiTests.cs:140` 同步） | 複述 |
| Rules | 其餘約 20 條純描述、`alert-warning` 趨勢說明 | | `?`（趨勢欄位字串受測試鎖，同步） | 收進 |
| Imports | 欄位格式說明兩段（含 `<code>`） | Imports.cshtml:19,60 | 留（技術例外） | 例外 |
| Netiq | 「套用於全部 Sentinel 的查詢行為，不是個別 Sentinel 的連線設定」 | Netiq.cshtml:38 | 留 | 後果 |
| Netiq | 「單一選定 Sentinel、近 1 小時、最多 3 筆事件…」 | :189 | `?` | 收進 |
| Netiq | 「混合平台請分兩次掃描匯入」「同樣只影響本次新增的主機」 | :307,318 | 留 | 後果 |
| Netiq | `?` 14 顆 | | 留；三顆不帶 `.lf-help` 的統一成 `helpIcon` | `?` |
| Runs | 「同一錯誤在幾台主機出現…」「逐筆列出每一次執行…」 | Runs.cshtml:220,230 | 刪 | 複述版面 |
| Runs ★ | AI 區整段 | :304 | 常駐一句「AI 跟著取數自動進行，不受窗口限制；這裡只設定背景補跑。」其餘 `?` | 口徑提醒 |
| Runs | 併發數 form-text 與 `?` 同句 | :323／:328 | 刪 form-text | 重複 |
| Runs | 「PRTG 歷史回填執行中…」、舊 AI 結論重跑 `alert-warning` | :395,416 | 留 | 警示 |
| HandlerDetail | 「每台主機上被交辦給你的問題，狀態會跟著交辦單同步」 | HandlerDetail.cshtml:53 | 留 | 後果 |
| HandlerDetail ★ | 「勾選…按『回覆選取的主機』；全部處理好可按『全選』」 | handler-detail.js:633 | 留、縮成「全部處理好可按『全選』」 | 手機操作捷徑 |
| IssueOwners | 「以問題為單位記錄負責人與機房結論，跨主機生效。」 | issue-owners.js:43 | 留「跨主機生效」 | 後果 |
| PermissionChanges ★ | 「系統負責發現異動，請逐筆確認每筆是否為授權操作。」 | :1275 | 留、縮成「請逐筆確認是否為授權操作」 | 責任分界 |
| PermissionChanges | 「只會選取待確認的項目；已確認的項目不會被選到」 | :62 | 留 | 後果 |
| WorkOrders | 長 `?` | :10 | 留、精簡 | `?` |
| WorkOrders | 「靜音期間新資料不進待辦、不告警；到期自動恢復。」 | :93 | 留 | 後果 |
| WorkOrderDetail | `lf-hint` 兩處 | work-order-detail.js:930,993 | 後果句留、描述句 `?` | |
| HelpManual | 「分析執行中，AI 回應可能較慢」 | | 留 | 動態 |
| Error | 說明段 | | 留 | |

**第二組：Settings／Setup**

| 頁面 | 文字 | 位置 | 處置 | 依據 |
|---|---|---|---|---|
| Settings | 區塊開頭說明約 21 段 | 各區 | 「不可改寫的證據層」「高風險日為必要顯示項目」留；其餘 `?`（區塊標題旁） | 混合 |
| Settings ★ | 「0＝不估費」×2、「留空＝使用出廠名稱」、「必須是 JSON 物件」、「不得大於…」 | :228,236,890 等 | 留 | 特殊值／硬性限制 |
| Settings ★ | 「本機 27B 級模型單次回應可能需數分鐘」 | :145 | 留（直接影響逾時設定） | 決策 |
| Settings | 資源守門「CPU 使用率達此值算緊張」「檢查間隔（秒）」、「Azure OpenAI 建立的模型部署名稱」等複述欄位名者 | :619-647,106 | 刪 | 複述 |
| Settings | 「之後指數遞增（10 → 20 → 40）」等調校指引 | :154 | `?` | 收進 |
| Settings | `?` 16 顆 | | 留；與相鄰小字重複者二擇一 | |
| Setup | form-text 8 條 | Setup.cshtml:50,130 | 與 Settings 同欄位同處置 | 一致 |

**第三組：Prtg**

| 頁面 | 文字 | 位置 | 處置 | 依據 |
|---|---|---|---|---|
| Prtg ★ | 六個 callout | Prtg.cshtml:27,68,114,227… | 刪；每頁籤 `?` 承接「這頁做什麼」；未設定時空狀態列啟用順序 | 三行式 |
| Prtg ★ | 「各數字的分母不同，不能互相換算成比例」 | :31 | 留 | 口徑警示 |
| Prtg ★ | 「結果包含不在主機清單內的裝置（全站診斷用）」 | :625 | 留 | 可見性 |
| Prtg ★ | 「不會開始取數；取數要到『擷取參數』勾選啟用」 | :118 | 留 | 下一步 |
| Prtg | 「尚未執行判定查詢，請點擊上方『重新計算』按鈕。」×4 | :699,725,750,775 | 改為一處 `renderEmpty` | 重複 |
| Prtg | 「不得大於歷史資料保留天數」「僅在自簽憑證…啟用」 | :315,292 | 留 | 硬性限制／後果 |
| Prtg | 其餘描述句 form-text、策略差異、範圍說明、校準補充清單 | :78,90,259,709… | `?` 或既有收合區 | 收進 |
| Prtg | 隱藏 caption 三處、空狀態 9 處 | | 留 | 無障礙／空狀態 |
