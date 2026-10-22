# LogForesight 第 57 輪規劃

> 狀態：**全案完成，已併 dev**（2026-10-22；全量 6,478 通過／10 略過；B-1a 實機劇本與 B-4 Codex worktree 處置交使用者）。
> 基準：dev@5d7edc7a（6,436 通過／10 略過，第 56 輪收尾後）。
> 來源：使用者 2026-10-22 三個提問——(1) PRTG 是否已達企業級實用度與穩定度；(2) 自動／手動排程取得 NetIQ、本機 AI、PRTG 三者的穩定度與順序是否正確，避免 AI 解讀資料缺漏；(3) 使用者體驗是否良好。由 Claude 以整個專案角度核對程式碼、文件與實際啟動站台走查後提出，討論定案。
> 設計依據：使用者定案 UX 批次套用 `ui-ux-pro-max`。本輪不換風格、不動 token（DESIGN-SYSTEM v2 維持）；判斷順序沿 skill（可用性與語意 → 真實內容與響應式 → 一致性 → 效能動態），WCAG 2.2 AA 為檢查目標。本機檢索命中：UX 指引 #79「空狀態要有訊息與下一步」、#61「送出要有載入→成功／錯誤回饋」；頁籤／漸進揭露與設定清單兩題無命中，改依 DESIGN-SYSTEM §6b「日常動線用不到的內容收進進階收合區、說明超過兩行改對照表或收合」。
> 實作方式：Sonnet 5.5 subagent（Agent tool `model=sonnet`，effort **medium**），同一 checkout 一次只派一個改檔 subagent；使用者要求前景模式——桌面 app 的 Agent 工具沒有前景選項，改以主代理每段完成即獨立驗收、中途主動查進度替代。每段 Claude 自行跑該段列出的驗收指令與突變；前端段另以內建瀏覽器實測（§8）。
> **第二版改了什麼**（四角度重檢，§10 有逐條理由）：A-1 積壓層改「七日一塊、塊由新到舊、塊內同主機舊→新」（第一版整段舊→新會讓強制重新分析從 180 天前開始敘事）；A-2 互動車道加 5 秒等待上限、失敗照現行降級；A-3 等待回填改**有上限 30 分鐘**、逾時照常並行並留警告（第一版 2 小時會把昨天的 AI 判讀整段往後推）、不新增確認框；B-1b 改名「最近一次排程驗收」、AI 未設定顯示不適用、附執行詳情連結；B-3 白名單補 `PrtgLegacyJsonTransferValidator`；C-1 收合元件沿用 `localStorage` 既有前綴慣例、新增一個共用 helper；C-2 閒置原因由 `AiAnalysisHostedService` 自己提供、不另開服務；新增批次 D 的說明書同步（`20-prtg.md`／`10-scheduler.md` 連同 `.ai.md`）；每段補「驗收指令」與「我驗不到的部分」。

## 0. 定案摘要（2026-10-22）

| 待決 | 使用者決定 | 落到哪裡 |
|---|---|---|
| 1 正式環境事實 | PRTG 介面英文、單台 core server、曾接站台跑過但不確定是否達企業級 | 「sensor type 顯示名」與「多台 core」列入明確不做；B-1 劇本以「已接過、要驗證成熟度」為起點 |
| 2 AI 節流 | 甲：全站單一 AI 閘道、互動呼叫優先 | A-2 |
| 3 AI 積壓順序 | 甲：背景積壓舊→新、跟隨取數維持新→舊 | A-1（第二版細化為分塊） |
| 4 取數對回填 | 甲：PRTG 日路徑等待回填結束＋立即執行提示 | A-3（第二版加上限） |
| 5 UX | 套用 `ui-ux-pro-max`；C-1 甲：監看範圍與通知狀態搬進頁籤 | 批次 C |
| 6 實作方式 | Sonnet 5.5 medium，前景（見上） | 作業總覽 |

本輪由 Claude 自行判定、未另問使用者的兩項（理由見 §9）：原建議的 A-4「NetIQ 寫入前先併 PRTG finding」不做；「週檢移出取數執行」不做、記 BACKLOG。本輪**不新增任何設定欄位**（A-2 沿用 `AiConcurrency`，B-2 水位為內部 blob）。

## 1. 三問的結論（討論依據，簡述）

1. **PRTG 企業級實用度**：程式層成熟度高（12 節規格、101 個測試檔／2,712 條案例涉及 PRTG、逐階段失敗隔離、分頁上限、取數範圍必填、快照 journal 重啟恢復、設定變更 checkpoint、清除守門、資源守門、探測含站台對照、維護頁啟用準備清單）；**實證成熟度不足**——master@80ae01f（2026-08-27，R21～32）不含任何 PRTG 輪次，dev 領先 730 個 commit、281 個涉及 PRTG，BACKLOG 明載 3,000 主機／15,000 sensor／180 日負載未驗收。本輪把「實機驗收劇本＋畫面上可核對的排程驗收」當成交付物（B-1），不再加功能。
2. **排程順序與 AI 完整性**：逐行核對後判定**順序正確且有收斂保證**——三路並行，PRTG 逐日發佈登錄簿後送 `prtg-findings-ready`；AI 排程以 `FindingsWaitCutoff` 擋住本趟日期；寫回前比對 PRTG 證據指紋（`EfAnalysisRecordStore.TryAttachAiResult`）；事後追加 finding 且指紋變、風險非低時重新標 AI 待補（`EfAnalysisRecordStore.cs:748`）。取消或逾窗的日子隔晚補評估後會再判一次。缺口是 A-1～A-3 三項，皆非資料錯誤，是重複成本與互搶資源。
3. **UX**：三卡骨架、空狀態、確認框與排程設定頁籤都清楚。要改的是 PRTG 維護頁把兩個進階區塊壓在連線設定之前、AI 卡在 `backfill-pending` 時文案自相矛盾、PRTG 卡回填狀態空白、啟用準備清單含一項重整即歸零的「本次頁面連線測試」。

## 2. 核對表（本輪契約依賴的事實，皆已實際核對）

| 項目 | 判定 | 證據 | 補充 |
|---|---|---|---|
| 夜間三路並行與 PRTG 發佈時機 | ✅ | `AnalysisOrchestrator.cs:610-661`、`PrtgDailyPipeline.cs:591-709` | PRTG 全部日期發佈完才送一次就緒訊號；finally 對未發佈日補空集合 |
| AI 閘門與指紋收斂 | ✅ | `SchedulerRunState.cs:146-166`、`AiAnalysisHostedService.cs:351-357,471-482`、`EfAnalysisRecordStore.cs:746-755` | 風險非低且 PRTG 指紋變才重標；低風險日不標是既有定案 |
| AI 待補查詢順序 | ⚠️ | `EfAnalysisRecordStore.cs:1626` 新→舊；`AiAnalysisHostedService.cs:380-404` 群組內亦新→舊；`AnalysisPromptBuilder.cs:308-310` 近期歷史引用前日 AI 結論；`AiAnalysisSchedulerTests.cs:214` 釘住新→舊 | `QueryPendingAi` 唯一呼叫端是 `AiAnalysisHostedService.cs:339`；程式註解「同主機序列是因隔日引用前一天摘要」與實際順序矛盾 |
| AI 請求節流 | ❌ 只有逐實例 | `AIService.cs:76,237,316` 的 `SemaphoreSlim(1,1)` 是實例欄位；`new AIService(` 五處：`AnalysisOrchestrator.cs:311`、`AiAnalysisHostedService.cs:605`（每主機群組一個）、`HostDayWorkflowRecoveryHostedService.cs:290`、`WebAiService.cs:114,144` | 互動兩條（8 秒／60 秒逾時、不重試）與背景無優先序；AI 窗口預設全天（`ScheduleOptions.cs:58`） |
| 回填對取數的互斥方向 | ⚠️ 單向 | 回填擋取數：`PrtgBackfillService.cs:663`；取數不擋回填：`SchedulerHostedService.TickAsync` 無檢查；PRTG 日路徑只等手動同步：`PrtgDailyPipeline.cs:147-165`、介面 `IPrtgStructureSyncGate`、同步等待上限 `PrtgStructureSyncService.cs:93` 90 分鐘 | 接續回填（tail）本來就與取數同趟，不在此列 |
| NetIQ 落地與 PRTG 併入順序 | ✅ 正確、有小窗口 | `NetiqPipelineService.cs:684` Append → `:713` 併入；`EfAnalysisRecordStore.Append:181` 另有 PRTG 獨立主機日併回路徑 | 窗口內被 AI 讀到會被指紋拒絕再重跑，僅多一次 AI |
| 取數執行內同步呼叫 AI | ⚠️ 只剩週檢 | `AnalysisOrchestrator.cs:695`；本機／NetIQ 統計段 `allowAiDeepDive:false`（`LogAnalysisService.cs:497,515,539`） | AI 掛了只延後 RunEnded 一個逾時 |
| PRTG 維護頁版面順序 | ⚠️ | `Prtg.cshtml:5-95`：啟用準備 → 監看範圍（60 行、六組分頁器） → 通知處理狀態 → `#prtg-tabs` | 清單在 `prtg-admin.js:25-66` 前端組裝；頁籤 `bindTabs(..., {hash:true})`；站內連到此頁的深連結只有 `#params`、`#mirror`、`#probe`（`Runs.cshtml:123`、`Settings.cshtml:521,533`、`host-detail.js:784`），沒有任何頁連到監看範圍或通知處理狀態 |
| 說明書對版面的描述 | ⚠️ 要同步 | `HelpContent/20-prtg.md:3` 列六個頁籤與啟用準備；`10-scheduler.md` 描述排程頁三卡 | 改版面與文案時 `.md` 與 `.ai.md` 都要改 |
| AI 卡閒置原因 | ⚠️ | `AiAnalysisRunState.SetIdleReason` 由 `TickAsync` 每 60 秒寫；`ScheduleController.cs:501` 讀殘值；`runs.js:1196-1213`：待補 0 件一律「已全部完成」、提示只在待補 > 0 時顯示 | `ScheduleController` 已注入 `AiAnalysisHostedService`，閒置原因可由它直接提供 |
| PRTG 卡回填狀態 | ⚠️ | `Runs.cshtml:136` 初值「閒置」；`runs.js` 無上次執行時 `statusEl.textContent = ''` | 畫面實測為空白 |
| R54 遞延清理範圍 | ✅ 已盤點 | `lf_prtg_observations` 相關 19 檔：Core 9（含 `PrtgLegacyJsonTransferValidator.cs:125` 的 `Observations` 欄位表）、Web 1、Tests 8 | 搬運與診斷匯出含 `Observations` 類別，不是純死碼 |
| 回填預覽指紋 | ✅ 可當水位鍵 | `PrtgBackfillService.cs:230,337,398,415,698` 的 `TargetFingerprint`／`SettingsRevision`／`ScopeRevision` | B-2 直接沿用 |
| 收合元件 | ❌ 無現成 | `ui.js:230-241` 只有每頁筆數的 `localStorage` 慣例（`lf.pageSize.` 前綴） | C-1 新增一個共用 helper，沿用同一前綴慣例 |
| 分支衛生 | ⚠️ | 七條 `feature/prtg-feedback-53-*` 被 `C:/Users/eldar/Documents/Codex/.../work/` 下的 worktree 佔住 | B-4 收尾時處理，動手前列清單確認 |

## 3. 作業總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A 排程與 AI 收斂 | A-1 積壓順序分塊／A-2 全站 AI 閘道／A-3 PRTG 日路徑等回填／A-5 文件註記 | 中 | 無 | 1 |
| B PRTG 實證與清理 | B-1 驗收劇本＋排程驗收段／B-2 回填完成水位／B-3 R54 遞延清理／B-4 分支清理 | 中 | B-1b 在 C-4 之後 | 2 |
| C UX | C-1 維護頁重排／C-2 AI 卡文案與閒置原因／C-3 回填狀態空值／C-4 啟用準備清單 | 小～中 | C-4 先於 B-1b | 3（C-4 可提前） |
| D 文件與收尾 | PRTG-SPEC／WEB-SPEC／DB-SPEC／BACKLOG／CLAUDE.md／說明書同步 | 小 | A～C | 4 |

委派模型：整輪 Sonnet 5.5（medium）。每階段規格檔只放背景兩三句、契約、限制、驗收；不貼本文件。每段的「驗收指令」由主代理親自執行；「我驗不到的部分」列出來交給使用者實測。

設計原則（整輪共用）：不新增設定；新行為用既有介面擴充而非平行第二套（A-3 泛化現有閘門、C-2 共用同一判定函式）；相依走介面與 null-object 預設（A-2），測試不必改建構方式；每個新判定只存在一處。

## 4. 批次 A：排程與 AI 收斂

### A-1 AI 積壓順序分塊

**現況與核對結果**：見核對表第 3 列。背景消化積壓時整批新→舊，初次上線回補 120 天或 AI 中斷後恢復時，新日敘事看不到舊日的「當日結論」；穩態每晚一日不受影響。

**第二版修訂理由**：第一版「積壓層整段舊→新」在「規則更新後強制重新分析」情境會從 180 天前開始敘事，管理者最想看的最近幾週排最後；改成分塊同時保住「近者先」與「塊內連續」。

**定案**：待補分兩層，查詢在 SQL 端排序、不得整表載入：
- **近日層**＝紀錄日 ≥ 取數執行中時 `SchedulerRunState.PrtgRangeStart`、否則「今天 − 2」（暫定），新→舊——「取數跑到哪、判讀跟到哪」。
- **積壓層**＝其餘，以 **7 日為一塊**（暫定；塊邊界由近日層下界往回算），**塊由新到舊、塊內同主機舊→新**，主機之間以該主機在塊內最舊待補日由舊到新。
- 近日層被完整性閘門全部擋住時**直接取積壓層**，不再放大查詢批次（現行 `currentQueryBatchSize` 逐步加到 1,000 的機制移除）。
- 群組內序列方向跟著該層；`attempted` 集合、「本批全滅就停」防線、`AiIdleReasons.WaitingFetch` 語意不變；強制重新分析不另外處理（整批重標後自然走這套順序）。
- 介面：`QueryPendingAi` 改成接受一個小型查詢條件物件（日期上下界＋方向），不要用布林旗標堆疊；呼叫端只有 `AiAnalysisHostedService`。

**改動**：`IAnalysisRecordQuery.QueryPendingAi` 與 `EfAnalysisRecordStore` 實作；`AiAnalysisHostedService.ExecuteProcessingLoopAsync` 改為近日層→逐塊；移除批次放大；修正順序理由的註解。

**測試／驗收**（執行端自驗＋主代理重跑）：
- 同主機積壓 D-10、D-7、D-3（同一塊）→ 處理順序 D-10 → D-7 → D-3，且以記錄 prompt 的假 AI 斷言 D-7 的【近期歷史】含 D-10 的 AI 結論。
- 跨塊：D-20（舊塊）與 D-5（新塊）→ D-5 先；近日層 D-1、D-2 先於任何塊且新→舊；兩主機同塊時最舊待補日較早者先。
- 取數執行中、cutoff 擋住近日層時，第一批就取到積壓層；不再出現批次放大。
- 既有 `AiAnalysisSchedulerTests.cs:214` 的新→舊斷言改寫為新契約。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~EfAnalysisRecordStore"`；突變：把塊內排序改回新→舊，第一條必須紅；把「塊由新到舊」改成由舊到新，第二條必須紅。
- 我驗不到的部分：無（純後端）。

### A-2 全站 AI 閘道、互動優先

**現況與核對結果**：見核對表第 4 列。地端模型是序列的；併發 3 等於三條在途加互動呼叫排在後面。

**定案**：Core 新增行程單例閘道（介面＋實作＋null-object 預設），兩個車道：
- `interactive`：`WebAiService` 的兩條 client；併發上限 2（暫定）；**等待上限 5 秒**（暫定），逾時視同呼叫失敗，走現行的互動降級（卡片安靜消失、對話回「稍後再試」），不排隊到使用者放棄。
- `background`：`AnalysisOrchestrator`（含週檢、深析）、`AiAnalysisHostedService`、`HostDayWorkflowRecoveryHostedService`；併發上限＝`ScheduleOptions.AiConcurrency`，每次取許可時讀取（暫定）。
- **互動優先**：有 interactive 在途時 background 不開新請求；最長等 30 秒（暫定）後放行，防止連續互動造成背景飢餓；已在途的背景請求不中斷。
- 閘道由 `AIService` 建構式接收（車道參數），五處建構點全部帶上；未傳時用 null-object（行為＝現行逐實例序列），既有測試不必改。閘道在 `_requestQueue` 外層取得、`finally` 釋放，取消穿透不洩漏許可。
- 等待事件寫 NLog Info；不改 AI 窗口預設值、不改 UI、不新增設定。

**改動**：閘道介面／實作／null-object；`AIService` 多一個可選參數；五處建構點；Web DI 註冊單例並注入 `AnalysisOrchestrator`（它在 Core 自建 `AIService`）。

**測試／驗收**：
- 假 handler 延遲測試：背景三條在途、互動進來時在 5 秒內發出；互動在途時背景新請求等待、互動結束後放行；背景等待達 30 秒放行；互動等待逾 5 秒回失敗；取消時許可歸還；`AiConcurrency` 改值後下一次取許可生效。
- 守門測試：掃描 Core／Web 原始碼，`new AIService(` 每處都帶閘道引數（測試專案除外）。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~AiRequestGate|FullyQualifiedName~AIService|FullyQualifiedName~WebAiService"`；`grep -rn "new AIService(" --include=*.cs LogForesight.Core LogForesight.Web` 逐行目視；突變：拿掉「互動在途時背景等待」，第二條測試必須紅。
- 我驗不到的部分：真實地端模型下互動延遲的改善幅度（使用者實機感受）。

### A-3 PRTG 日路徑等待回填（有上限）

**現況與核對結果**：見核對表第 5 列。

**第二版修訂理由**：第一版「等 2 小時」會讓 `prtg-findings-ready` 延後，AI 完整性閘門因此把昨天的待補一起擋 2 小時；且逾時後仍並行、沒有避開負載。改為短上限＋明確警告，把決定權交回正在看畫面的管理者（可在 PRTG 卡停止回填）。

**定案**：把 `IPrtgStructureSyncGate` 泛化為「PRTG 手動作業閘門」：手動同步**或**全量／指定主機回填執行中時，PRTG 日路徑先等；phase 沿用 `prtg-wait-sync`，前端標籤改為「等待 PRTG 手動作業（同步／回填），可在 PRTG 卡停止」並顯示已等時間。
- 等到的是**同步且成功** → 跳過本趟結構同步（既有）；等到的是回填結束、或逾上限 → 本趟照常自行同步與評估。
- 回填的等待上限 **30 分鐘**（暫定）；逾上限時寫里程碑「回填仍在執行，PRTG 查詢將與它同時進行」並繼續（寫入冪等）。同步沿用既有 90 分鐘。
- 接續回填（tail）不在閘門內；NetIQ／本機兩路不受影響；排程 tick 不另加判斷。
- 立即執行 modal：**不新增確認框**，回填執行中時在既有 PRTG 提示區多一行「回填執行中：PRTG 日路徑最多等 30 分鐘，NetIQ 與本機照常」。
- 回傳值改為列舉（同步成功／其他）而非布林，呼叫端不靠布林猜語意。

**改動**：介面與 Web 實作（同步、回填兩個狀態來源合成）；DI 註冊；`PrtgDailyPipeline` 等待分支；`run-phases.js` 標籤；立即執行 modal 提示；PRTG-SPEC §5 加「建議回填避開排程窗口，預覽的時間下界可供估算」。

**測試／驗收**（`PrtgDailyPipelineTests`、`RunsPageUiTests`）：假閘門回填中 → 送出等待 phase、結束後不跳過結構同步；同步成功 → 跳過（既有保持）；逾上限 → 不跳過且有里程碑；modal 提示文案存在且只在回填執行中出現。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~PrtgStructureSync|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~JsBehavior|FullyQualifiedName~ApiTimeout"`；突變：把「等到回填也跳過同步」寫進去，第一條必須紅。
- 我驗不到的部分：dev 站台沒有 PRTG，等待分支只能靠測試；瀏覽器只驗排程頁載入無錯誤、標籤對照表含新文案。

### A-5 文件註記（併入批次 D）

PRTG-SPEC §3 與 WEB-SPEC §9.10 加「AI 完整性保證」一段（閘門、指紋、取消後隔晚重判、兩層順序）；§3 註明週檢是取數執行內唯一同步 AI 呼叫；BACKLOG 新增「週檢移出取數執行」（觸發：AI 逾時明顯拖長 RunEnded）。

## 5. 批次 B：PRTG 實證與清理

### B-1 實機驗收劇本＋最近一次排程驗收段

**定案**：
- **B-1a 文件**：PRTG-SPEC 新 §13「實機驗收劇本」。步驟：環境探測（看 9d-5、S1～S3）→ 同步結構與對應（看對應摘要 ok／conflict／unmatched）→ 立即執行回望 1 天（看 PRTG 軌三階段、`[PRTG]` 輸出的 finding 計數行與就緒度行）→ AI 跟隨（AI 卡待補歸零、無 `waiting-fetch`）→ 隔晚排程（執行總表 PRTG 徽章、待補評估為空）→ 快照 24 小時涵蓋率。每步寫「看哪個畫面哪個欄位、正常值、不正常時去哪裡」。不新增 API。
- **B-1b 畫面**：維護頁「啟用準備」6 項皆確認後，下方出現「最近一次排程驗收」段（第二版改名，不叫首晚——它會一直存在）三項：① 最近一趟**排程**取數的 PRTG 結局為 `success`，附該日 finding 數與歸戶主機數，並附「檢視執行」連結到該趟執行詳情；② 取數結束後 AI 待補歸零且上次 AI 執行成功——AI 未設定時顯示「不適用（AI 未設定）」而非待確認；③ 快照最近 24 完整小時涵蓋率 ≥ 75%（暫定，與可用列門檻同值）且最近一次快照成功在兩個採樣間隔內，附連結到既有卡；無快照資料時顯示「無資料」、不算通過。沒有任何排程執行時顯示「尚未有排程執行；立即執行不計入」——分母為零不得顯示通過。資料來源：`GET api/admin/schedule/status` 新增 `prtgLastScheduledDay`（日期／結局／finding／歸戶主機／執行 id，取最近一筆 `Trigger=="schedule"` 且非 AI 的 `BatchRun.PrtgDays`）；② 用既有 `ai-status`；③ 用既有快照診斷端點。

**改動**：PRTG-SPEC §13；status DTO 一欄；`prtg-admin.js` 清單段；`Prtg.cshtml` 容器；說明書 `20-prtg.md`／`.ai.md`。

**測試／驗收**：DTO 測試（無排程執行時為 null；有逐日統計時取最近一筆；立即執行不計入）；`PrtgAdminPageUiTests` 三項文案、「尚未有排程執行」分支、AI 未設定分支；劇本文件每步引用的畫面元素與文案以 grep 核對存在。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~ScheduleController|FullyQualifiedName~PrtgAdminPageUiTests|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~JsBehavior|FullyQualifiedName~ApiTimeout"`；瀏覽器：dev 站台（無排程執行）顯示「尚未有排程執行」分支。
- 我驗不到的部分：劇本在真實 PRTG 上逐步走一遍——由使用者實測並回報哪一步不符。

### B-2 回填完成水位

**現況**：BACKLOG「歷史回填沒有完成水位」；預覽已有 `TargetFingerprint`／`SettingsRevision`／`ScopeRevision`／`AnchorDate`。

**定案**：全量回填逐日完成後寫 blob `prtg_backfill_watermark`（暫名，自成一個 `JsonBlobSingleton` store，與 `PrtgStructureSyncStatusStore` 同型）：{AnchorDate, TargetFingerprint, SettingsRevision, ScopeRevision, CompletedDays, UpdatedAt}。
- 下一次全量回填預覽時，四個鍵全同且 `UpdatedAt` 在 7 天內（暫定）→ 預覽列出「已完成 X 天可跳過，剩餘 Y 天」，start 預設 `resume=true` 只跑剩餘日；任一鍵不同或過期 → 水位失效、全跑並覆寫。
- **單向閘門的繞過**：預覽／start 帶 `resume=false`（UI「全部重做」勾選，預設不勾）清水位全跑。
- 狀態變更區間仍整段取一次（冪等去重，暫定）；只跳過數值日。指定主機補值與 tail 不讀不寫水位。中斷（停止、例外、站台重啟）時已完成日保留。

**測試／驗收**（`PrtgBackfillService` 測試）：中斷在第 k 天後重啟只打剩餘天；fingerprint 變 → 全打；`resume=false` → 全打並清水位；過期 → 全打；selected／tail 不碰水位；預覽回應帶可跳過天數。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~PrtgBackfill|FullyQualifiedName~PrtgAdminPageUiTests"`；突變：拿掉過期判定，第四條必須紅。
- 我驗不到的部分：真實回填的中斷重啟（使用者實測）。

### B-3 R54 遞延清理

**定案**：DROP `lf_prtg_observations`（`SchemaUpgrader` 冪等 DROP TABLE，SQLite 與 SQL Server 皆 `IF EXISTS` 語意）；刪 `EfPrtgObservationStore`、`PrtgObservationRow`、DbSet、`StorageBackend.PrtgObservationStore()`、`RetentionPruner.cs:161-162`；`SourceHintFor` 搬到仍存在的類別（呼叫端 `PrtgMonitoringController.cs:150`）；搬運與診斷匯出移除 `Observations` 類別並遞增包版本；**匯入端與舊版驗證器對舊包的 `Observations` 區段接受但忽略**（不拒絕舊包）；已停寫欄位依 R54 定案不 DROP COLUMN。升級說明：DROP 前請備份（寫進 DB-SPEC 升級注意）。
- 移除類依賴方（全部進白名單）：Core `EfPrtgObservationStore`、`LfDbContext`、`PrtgObservationRow`、`SchemaUpgrader`、`StorageBackend`、`PrtgDataTransfer`、`PrtgDiagnosticExportSource`、`PrtgLegacyJsonTransferValidator`、`RetentionPruner`；Web `PrtgMonitoringController`；Tests `PrtgDailyPipelineTests`、`PrtgDiagnosticExportHttpTests`、`PrtgDiagnosticExportSourceTests`、`PrtgDiagnosticImportIsolationTests`、`PrtgMonitoringContractTests`、`PrtgObservationStoreTests`（刪）、`RetentionPrunerTests`、`SchemaUpgraderPrtgTests`。

**測試／驗收**：`SchemaUpgraderPrtgTests` 加「既有 DB 升級後表不存在、重複升級不報錯」；匯入測試加「含 Observations 的舊版包仍可匯入且忽略該區段」；全專案 grep `PrtgObservation`／`lf_prtg_observations` 零命中（文件的歷史索引除外）。
- **驗收指令**：`dotnet test --filter "FullyQualifiedName~SchemaUpgraderPrtgTests|FullyQualifiedName~PrtgDiagnostic|FullyQualifiedName~PrtgTransfer|FullyQualifiedName~RetentionPrunerTests|FullyQualifiedName~PrtgMonitoring"`；`grep -rn "PrtgObservation\|lf_prtg_observations" --include=*.cs .` 零命中；dev 站台啟動後以 Python `sqlite3` 查 `sqlite_master` 無該表。
- 我驗不到的部分：SQL Server 端的 DROP（只驗 provider 翻譯）。

### B-4 分支與 worktree 清理（收尾時、使用者確認後）

列出七條 `feature/prtg-feedback-53-*` 與對應 worktree 路徑，逐一核對 `git status` 無未提交變更、分支內容已在 dev 後再 `git worktree remove` 與 `git branch -D`；任一有未提交變更或未併內容就停下來問。不碰 `backup/*`。驗收：`git worktree list` 與 `git branch` 不再出現這七條。

## 6. 批次 C：UX（套用 ui-ux-pro-max）

驗證方式照 skill `references/verification.md`：配色／版面類以實際頁面、真實中文、375px 與桌面、字級三檔檢查；表單／導航類測鍵盤 Tab／Enter／Escape 與焦點可見。三段的前端驗收指令共用：`dotnet test --filter "FullyQualifiedName~PrtgAdminPageUiTests|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~FrontendConsistencyUiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~JsBehavior|FullyQualifiedName~ApiTimeout"`。

### C-1 PRTG 維護頁重排

**定案**：頁面順序改為 啟用準備 → 兩個 banner → `#prtg-tabs` 頁籤。
- 「監看範圍（可選縮圈）」整卡搬進「擷取參數」頁籤，排在取數範圍設定之後，**預設收合**；收合用 Bootstrap collapse，觸發鈕帶 `aria-expanded`／`aria-controls`，展開狀態記在 `localStorage`（新增 `ui.js` 共用 helper，key 前綴 `lf.collapse.`，沿用 `lf.pageSize.` 的 try/catch 寫法）。卡標題保留、說明文字不變。
- 「通知處理狀態」搬進「使用效果」頁籤末尾。
- 不改 API、不改資料邏輯；元素 id 不變；hash 深連結（`#params`、`#effectiveness`）維持；啟用準備清單的頁籤跳轉按鈕行為不變；分頁器等初始化若依賴可見性，改在展開時觸發。
- 不選的方案：只收合不搬——首屏仍是三個區塊標題堆疊，連線設定仍要捲兩屏。

**測試／驗收**：`PrtgAdminPageUiTests` 結構斷言（`#prtg-monitoring-form` 在 `[data-panel="params"]` 內、`#prtg-operations-status` 在 `[data-panel="effectiveness"]` 內、`#prtg-tabs` 是啟用準備與 banner 之後第一個區塊；頂層函式不得重複定義守衛保留）；JsBehavior：collapse helper 在 `localStorage` 擲例外時不壞、展開狀態可記憶；瀏覽器：連線頁籤首屏可見、`#params` 深連結展開後監看範圍可操作、375px 不破版、Tab 焦點可見、收合鈕 Enter／Space 可用、字級「大」不破版。
- 我驗不到的部分：無。

### C-2 AI 卡閒置原因與待補文案

**定案**：
- 閒置原因改為**讀取時即時計算**：`AiAnalysisHostedService` 提供一個無副作用的「目前閒置原因」方法，`TickAsync` 與 `GET ai-status`（`ScheduleController` 已注入該服務）共用同一份判定；`SetIdleReason` 殘值不再作為 API 來源。
- 待補 0 件且原因為 `disabled` 或 `backfill-pending` 時不顯示「已全部完成」，改顯示「0 件（AI 服務未設定）」／「0 件（啟動後資料校正中，稍後再看）」；提示列在這兩種原因下即使待補為 0 也顯示。
- 閒置時 `progressPhase` 回 null（dev 站台實測閒置仍回 `netiq-ai` 殘值）。
- 核對 WEB-SPEC「`disabled` 時背景補跑窗口列顯示 AI 服務未設定」是否已實作，未實作一併補。

**測試／驗收**：閒置原因方法五種原因單元測試（執行中為 null）；`RunsPageUiTests` 文案；瀏覽器：dev 站台先在設定頁清掉 AI 位址（dev DB）看 `disabled` 分支，再填回看 `no-pending` 分支。
- 我驗不到的部分：`backfill-pending` 分支只能靠測試（dev 站台回填幾秒就完成）。

### C-3 PRTG 卡回填狀態空值

**定案**：無上次執行時顯示「尚未回填」（而非空字串）；其餘文案不變。驗收：UI 字串測試＋JsBehavior；瀏覽器 dev 站台看到「尚未回填」。

### C-4 啟用準備清單

**定案**：移除「本次頁面連線測試；重新載入後需再確認」這一項（清單變 6 項）；連線測試結果維持在連線頁籤內的既有 `#prtg-test-result` 就地顯示。B-1b 的段接在 6 項之後、只在 6 項皆確認時出現。驗收：`PrtgAdminPageUiTests` 清單項目數與文案；說明書 `20-prtg.md` 同步；瀏覽器實測。

## 7. 批次 D：文件與收尾

- PRTG-SPEC：§3（AI 完整性保證、週檢註記）、§5（回填水位、`resume`、建議避開排程窗口）、§5a（閘門泛化為手動作業、30 分鐘上限）、§7 操作介面表（維護頁版面）、§10（匯出不含 Observations、舊包相容）、新 §13 驗收劇本。
- WEB-SPEC：§9.9e（維護頁區塊歸屬、收合記憶）、§9.10（AI 兩層分塊順序、閒置原因即時計算、立即執行提示列、回填狀態文案、`prtgLastScheduledDay`）。
- DB-SPEC：`lf_prtg_observations` 移除與升級注意（備份）。
- 說明書：`HelpContent/20-prtg.md`＋`.ai.md`（頁面順序、監看範圍位置、6 項清單、排程驗收段）、`10-scheduler.md`＋`.ai.md`（AI 卡文案、等待手動作業、回填狀態）。
- BACKLOG：刪「歷史回填沒有完成水位」「第 54 輪遞延」；加「週檢移出取數執行」；保留 TOCTOU、連線池、多台 core、runs.js 拆檔。
- CLAUDE.md：基線數字；「不要做」加一條「AI 呼叫一律經全站閘道，不得另 new 無閘道的 `AIService`」。
- 驗收指令：`dotnet test --filter "FullyQualifiedName~HelpContentServiceTests"`；文件內「原本／後來／第 N 輪」敘事字眼 grep 為零；收官跑全量 `dotnet test` 填進「體檢交接」。

## 8. 驗證環境與瀏覽器實測清單

- 內建瀏覽器 `preview_start` 的 `web`（`.claude/launch.json`），Development 設定走 SQLite，Stub 登入 `demo-admin`；dev DB 無 PRTG 連線、無排程執行紀錄、AI 已設定（實測 `idleReason=backfill-pending`、`aiConcurrency=3`）。
- 可在 dev 站台驗的：C-1 版面與鍵盤、C-2 的 `disabled`／`no-pending` 分支、C-3、C-4、B-1b 的「尚未有排程執行」分支、A-3 標籤對照表、B-3 啟動後表不存在。
- 驗不到的（交使用者實機）：A-2 互動延遲改善幅度、A-3 等待回填分支、B-1a 劇本逐步、B-2 中斷重啟、B-3 SQL Server DROP。
- 前端段定向測試一律含 `JsModuleImport|JsBehavior|ApiTimeout`（第 55 輪教訓）。

## 9. 明確不做（本輪定案）

- **sensor type 顯示名改 `type_raw`**：正式環境 PRTG 介面為英文（待決 1），觸發條件未成立。
- **多台 PRTG core server**：環境只有一台。
- **A-4 NetIQ 寫入前先併 PRTG finding**：DB 端 `AttachPrtgFindings` 有 baseline 風險下限、replacement 撤銷加權、`RiskReportPending` 三段邏輯，記憶體端沒有；提前併入要嘛複製第二份判定、要嘛大改 Append，而收益只是省掉毫秒級窗口內偶發的一次 AI 呼叫，且既有指紋機制已保證正確。
- **週檢移出取數執行**：影響只有 AI 逾時時延後一個逾時的 RunEnded；記 BACKLOG 待觸發。
- **AI 窗口預設改夜間**：A-2 閘道已解決互動優先，改預設值會讓既有站台行為改變。
- **暫停回填讓夜間取數優先**：要為回填新增暫停狀態機，超出 A-3 的收益。
- **探測／回填／同步／取數的共同鎖**（TOCTOU）、**背景服務獨立連線池**、**runs.js 拆檔**：維持 BACKLOG 既有判斷與觸發條件。

## 10. 四角度重檢紀錄（第二版）

**使用者角度（尖銳但有理）**
- 「規則改完強制重新分析，為什麼從半年前開始講？」→ A-1 改分塊。
- 「我的詢問 AI 為什麼卡 8 秒沒反應？」→ A-2 互動等待 5 秒上限、失敗照現行安靜降級，不讓使用者盯著轉圈。
- 「夜間取數為什麼乾等兩小時，昨天的 AI 判讀也跟著晚？」→ A-3 改 30 分鐘上限＋警告，決定權交給能按停止的人。
- 「立即執行又多一個確認框？」→ A-3 不新增確認框，只在既有提示區加一行。
- 「首晚驗收？我已經上線一個月了」→ B-1b 改名「最近一次排程驗收」，附執行詳情連結。
- 「0 件（存量校正完成前不可信）是什麼？」→ C-2 改白話「啟動後資料校正中，稍後再看」。
- 「監看範圍藏進收合區我怎麼找？」→ 它就在擷取參數的取數範圍正下方，標題保留；啟用準備清單本來就不列它（可選功能）。

**管理者角度**
- 不新增設定、不改預設窗口，升級後唯一的行為變更是 AI 處理順序與互動優先；B-3 的 DROP 不可逆，升級注意要求備份。
- 回填水位讓中斷重啟不再重打 30 天；預覽明示可跳過天數。
- 企業級與否的證據改在畫面上（B-1b）而不是靠翻執行紀錄。

**程式面（SOLID／KISS／DRY）**
- A-2 閘道走介面＋null-object，`AIService` 只多一個可選相依（DIP、OCP）；不集中改成工廠——五處建構點各自的設定來源不同，硬抽工廠反而是為了 DRY 而 DRY。
- A-3 泛化既有閘門而非再加第二個介面；回傳列舉取代布林語意猜測。
- A-1 查詢條件用小型物件而非布林旗標；呼叫端只有一處。
- C-2 判定函式放在已持有全部相依的 `AiAnalysisHostedService`，控制器既有注入直接用，不另開服務。
- C-1 收合 helper 一個、沿用既有 `localStorage` 慣例；B-2 水位 store 與既有狀態 store 同型。
- 移除類（B-3）19 檔全部列入白名單，含第一版漏掉的舊版驗證器。

**上帝視角（整體影響）**
- A-2 影響所有 AI 呼叫：測試靠 null-object 零改動；`HostDayWorkflowRecoveryHostedService` 的報告重生歸背景車道。
- A-3 介面改名影響 DI 註冊、`PrtgDailyPipeline`、`PrtgStructureSyncService`、`SchedulerHostedService` 注入與 `PrtgDailyPipelineTests` 的假閘門——全部列入該段白名單。
- A-1 不影響 `fetch-followup`：取數結束後 `PrtgRangeStart` 歸零，近日層退回「今天 − 2」，回望多日的其餘日期落進最新塊。
- C-1 不影響任何站內深連結（核對表：只有 `#params`／`#mirror`／`#probe`）。
- 說明書與 `HelpContentServiceTests` 在 D 批次同步，避免「畫面改了、說明書還在講舊版面」。
- 快照服務「結構同步、回填與夜間 PRTG 階段執行中讓出一個採樣間隔」的既有行為不受 A-3 影響。

## 11. 規劃完成後複檢

- **與既有設計的衝突**：A-1 移除 `currentQueryBatchSize` 放大（程式註解有、規格無）；A-2 保留逐實例 `_requestQueue`、閘道在外層；A-3 推翻 PRTG-SPEC §5a「只等手動同步」；B-3 推翻「匯出固定全類別」；C-2 推翻 WEB-SPEC「由 `TickAsync` 在每個提前返回處寫入」。皆在批次 D 回寫。
- **批次之間**：C-4 與 B-1b 同在 `renderSetupSummary`，C-4 先；A-3 與 B-2 都動 `PrtgBackfillService`，A-3 只讀執行狀態、B-2 改 TryPrepare 與逐日迴圈，先 A 後 B；A-2 與 C-2 都動 `AiAnalysisHostedService`，A-2 只改 `ResolveAiService`，C-2 改閒置判定，先 A 後 C。
- **拆分原則四坑**：什麼算一個／分母為零——A-1 近日層與塊以日期界定、B-1b 無排程執行時不顯示通過、③ 無快照資料時顯示「無資料」不算通過；破壞性判準反例——B-3 DROP 只在升級跑一次且 R54 起已停寫，升級注意要求備份；單向閘門繞過——B-2 的 `resume=false`；移除類依賴方——B-3 已列全部 19 檔。
- **升級與既有資料**：B-2 新 blob、無既有資料；B-3 舊匯出包含 Observations 時匯入忽略；C-1 `localStorage` 新鍵、無相容問題；A-1 既有 `ai_pending` 列不需遷移。
- 複檢完成，第二版無新增待決事項。

## 12. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 積壓順序分塊 | Claude（Opus 5.5，邏輯精細、已逐行追過，自做比寫規格省） | `PendingAiQuery`（範圍＋方向）取代批次放大；`SelectNextBatch` 每批重算近日層→七日塊；兩個替身共用 `PendingAiFake` | AI 排程／EF 查詢／查詢計畫 64 綠；突變兩條（片內改新→舊、錨點改最舊）各紅 2 條 | **簡化**：近日層固定「今天−2」，不跟 `PrtgRangeStart`——回望多日時其餘日期落進塊、完整性閘門照擋，少一個相依。既有兩條測試（情境 4、AI 端點停止）把舊順序寫進斷言，依新契約改寫。成功者從 attempted 移除，查詢列數只需多取失敗者 |
| A-2 全站 AI 閘道 | Claude（Opus 5.5，併發原語自做） | `IAiRequestGate`／`AiRequestGate`／`NullAiRequestGate`；`AIService` 多兩個可選參數（gate、lane）；五處建構點全接，互動兩個實例走互動車道；DI 單例，背景上限讀 `AiConcurrency` | 閘道 9 條＋AI 排程＋AIService＋WebAiService＋工作流恢復 86 綠；DI 組裝 13 綠；突變（拿掉互動優先）紅 1 條 | **設計修正**：閘道改在本實例佇列**之後**取（規劃寫外層）——排在自己佇列裡的呼叫不該佔全站許可。守門測試掃 Core／Web 的 `new AIService(` 須帶 `gate:`，鎖定 5 處 |
| A-3 PRTG 日路徑等回填 | Claude（Opus 5.5） | `IPrtgStructureSyncGate` 改名泛化為 `IPrtgManualWorkGate`（`Running` 三值、等待結果四值列舉）；Web 新增 `PrtgManualWorkGate` 合成同步與回填（tail 不算）；回填上限 30 分鐘常數放 Core 介面，訊息與實作共用；立即執行 modal 加一行靜態提示，隨回填狀態輪詢切換、開 modal 時重查 | 管線／同步服務／排程頁 UI／JS 匯入與行為／API 逾時／DI 組裝 174 綠；突變（回填結束也跳過結構同步）紅 2 條 | **簡化**：規劃寫「顯示已等時間」，改為等待訊息帶開始時刻（「自 HH:mm 起等待」）——狀態卡最新訊息就看得到，不必為一個數字新增進度欄位。瀏覽器實測併到批次 C 一次做 |
| C-4＋B-1b 啟用準備清單與最近一次排程驗收 | Claude（Opus 5.5） | 清單移除「本次頁面連線測試」並刪掉只為它存在的狀態與監聽（測試結果區的「未儲存值」提示保留）；`SchedulerHostedService.IsScheduledFetchRun` 成為排程判準唯一出口，`ScheduledRunTimes` 與新的 `LastScheduledPrtgDay` 共用；status 加 `prtgLastScheduledDay`；判定收斂在純模組 `core/prtg-acceptance.js`，畫面只排版 | 排程判準／PRTG 維護頁 UI／驗收行為（node）／排程控制器／前端一致性／JS 匯入與行為 80 綠；突變兩條（快照門檻放寬、排程判準拿掉）各紅 | **設計修正**：快照項不用規劃寫的「涵蓋率 ≥75%＋兩個採樣間隔」，改為「有目標的完整小時中品質達標 ≥75%＋最近一個完整小時有成功快照」——快照診斷服務明載彙總計數不能證明逐顆涵蓋，硬算涵蓋率等於造數；只用既有一支端點。**規劃錯誤**：`PrtgAdminPageUiTests` 原本不存在，本段新建。**過程失誤**：第二條突變前漏備份，以反向精確替換還原並以 diff 逐行確認。dev 站台沒有 PRTG，驗收段只能靠行為測試；畫面出現與否交使用者實機 |
| B-2a 回填完成水位（後端） | Claude（Opus 5.5） | 新增 `PrtgBackfillWatermarkStore`（blob `prtg_backfill_watermark`）；runner 加 `skipDays`／`dayCompleted`，只有每顆目標 sensor 都取到的日子才記完成；預覽多 `CompletedDays`／`RemainingHistoricRequests`／逐日 `AlreadyCompleted`；啟動帶 `Resume`（預設 true），全部完成時拒絕空跑 | 回填相關 51 綠；突變兩條（拿掉過期判定、部分失敗也記完成）各紅 1 條 | **設計修正**：水位鍵改為「設定版本＋範圍版本＋**逐日**目標指紋」，不用規劃寫的整份計畫指紋與錨點日——整份指紋跨午夜就全失效，逐日指紋讓「目標變了的那天」單獨重跑。**拆段**：B-2 拆成 a（後端）與 b（畫面勾選與預覽顯示）。狀態變更區間仍整段取一次（冪等），未縮到剩餘日 |
| B-2b 回填完成水位（畫面） | Claude（Opus 5.5） | 預覽有已完成日子時顯示說明與「全部重做」勾選、逐日表加「本次」欄；全部完成且未勾選時停用啟動鈕並說明（後端必拒就不讓人送）；確認框寫出略過或重做；送出帶 `resume` | PRTG 維護頁 UI／判定行為（node，含新腳本 `prtg-full-backfill.behavior.mjs`）／前端一致性／JS 匯入與行為 71 綠；突變（啟動鈕不看勾選）紅 1 條 | 瀏覽器實測：dev 站台沒有 PRTG 鏡像，預覽會回「沒有感測器」，水位區塊只能靠行為測試 |
| C-1 PRTG 維護頁重排 | Claude（Opus 5.5） | 監看範圍整卡移到擷取參數頁籤（參數表單**之後**——它是獨立表單，不可巢狀）、Bootstrap collapse 預設收合；通知處理狀態移到使用效果頁籤末尾；`ui.js` 新增 `bindRememberedCollapse`（`lf.collapse.` 前綴、localStorage 讀寫皆 try/catch、載入時直接帶 show 類別不播動畫）；監看範圍的儲存鈕改外框樣式 | UI／JS 匯入與行為／前端一致性／無障礙等 208 綠；突變（拿掉記憶還原）紅 1 條 | **補正**：同一頁籤出現兩顆實心主要按鈕違反 DESIGN-SYSTEM §6b，監看範圍儲存改 `btn-outline-primary` 並加守門。瀏覽器實測在 C-3 後一次做 |
| C-2 AI 卡閒置原因與待補文案 | Claude（Fable 5.1） | `AiAnalysisHostedService.BlockingIdleReason` 成為輪詢與 `CurrentIdleReason`（狀態 API 讀取時即時判定）共用的唯一判定；`ai-status` 閒置時 `progressPhase` 回 null；待補 0 件在 `disabled`／`backfill-pending` 不寫「已全部完成」、提示照顯示；文案改白話 | AI 排程／狀態碼守門／執行告示／排程頁 UI／JS 137 綠；突變（改回讀輪詢殘值）紅 2 條 | 控制器在測試以 `aiScheduler: null!` 建構時退回殘值（與 `_scheduler` 既有慣例一致）；「等取數」不擋開跑，只在其他條件都成立時才報 |
| B-3 R54 遞延清理 | Sonnet 5.5 subagent（medium，獨立 worktree；派出後使用者改指定 Opus 5.5，本段不重派）→ Claude cherry-pick 後獨立驗收 | 刪表模型、store、DbSet、保留清除；`SchemaUpgrader.DropTableIfExists` 兩後端冪等 DROP；`SourceHintFor` 搬到 `PrtgDataTransfer`；匯出（含串流 writer 的 `OrderedProperties`，代理補抓到白名單外一檔）不再有 Observations；驗證器把 Observations 當「舊包才有的可選區段」，筆數照核對、不寫入；格式版本維持 2 | Claude 自跑：schema／診斷匯出入／搬運／保留清除／監看／管線／查詢計畫／DI 224 綠；grep 只剩 DROP 與測試表名；BOM／CRLF 逐檔核對不變；突變（驗證器不再接受舊包區段）紅 1 條 | **版本決定**：不升到 3——升了會讓既有 V2 接受檢查與舊包全部失效，與「舊包照收」衝突；代價是**舊版站台匯不進新版匯出的包**（舊驗證器要求 Observations 存在），寫進 DB-SPEC 升級注意。代理 worktree 起點是 master，自行 reset 到分支尖端再做，提交內容與分支無衝突 |
| C-3 PRTG 卡回填狀態空值 | Claude（Fable 5.1） | 從未回填過顯示「尚未回填」、初值「載入中…」 | 排程頁 UI／前端一致性／JS 95 綠 | 一行文案，不做突變 |
| 批次 C 瀏覽器實測 | Claude（Fable 5.1，內建瀏覽器 dev 站台） | — | PRTG 維護頁：啟用準備六項 → 兩個 banner → 頁籤，連線頁籤首屏可見；`#params` 深連結落在擷取參數、監看範圍預設收合，滑鼠／Space／Enter 皆可切換、`aria-expanded` 與按鈕文字同步、`localStorage` 記住並在重整後還原；通知處理狀態在使用效果頁籤；同頁籤只有一顆實心主要按鈕；375px 與大字級皆無橫向溢出。排程作業頁：回填狀態「尚未回填」、閒置時 `progressPhase` 為 null、`no-pending` 時「0 件（已全部完成）」、無回填時 modal 提示隱藏。dev 資料庫升級後 `lf_prtg_observations` 與其索引已不存在 | 驗不到：`disabled`／`backfill-pending` 文案（dev DB 的 AI 已設定且校正瞬間完成）、排程驗收段（dev 沒有 PRTG，六項不會全部確認）——皆有行為測試覆蓋。瀏覽器 console 的 403 是 BACKLOG 既有的「全新安裝主機為空時 PRTG 範圍端點 403」，非本輪引入 |
| D 文件與說明書 | Claude（Fable 5.1） | PRTG-SPEC §3／§5／§5a／§7／§10／新 §13、WEB-SPEC §9.9e／§9.10、DB-SPEC、BACKLOG、CLAUDE.md、說明書四檔（含修正兩段過時敘述：AI 啟用開關、本機 AI 同步呼叫） | 說明書守門 19 綠；現行文件敘事字眼掃描無新增 | — |
| B-4 分支清理 | Claude（只盤點，未動） | — | 七條 `feature/prtg-feedback-53-*` 皆已併入 dev（領先 0），但各自的 Codex worktree 都有**未提交的真實程式改動**（probe 7＋3 檔、r01-scope 52＋18、r06 13＋2、r09-snapshot 51＋13、r10-metadata 56＋22、r10-visibility 55＋14、r12 6＋1；修改檔含 `CryptoHelper.cs`、`HostStore.cs` 等） | 依規劃停下來問：這些 worktree 是 Codex 時期的工作副本，內容未進任何分支；要刪請使用者確認（或先把想留的改動另存）。本輪的代理 worktree `agent-aa00c1ce…` 已清除 |

## 體檢交接

- 全量 `dotnet test`（feature/feedback-57，Windows，2026-10-22）：**6,471 通過／10 略過／0 失敗**，總計 6,481；基線 6,436 → +35（新增的閘道、順序、水位、驗收、觀察表移除、UI 守門等測試）。略過的 10 條是規模壓測（`LF_SCALE_BENCH=1` 才跑）。
- 逐段定向測試與突變結果見「執行紀錄」；每段的突變都還原並以 `cmp` 確認位元組相同、重新建置。
- 規劃逐條比對：A-1～A-3、B-1b、B-2a/b、B-3、C-1～C-4、D 全部有程式碼錨點，該消失的東西（批次放大、舊閘門介面、`PrtgObservation`、過時說明書敘述、BACKLOG 已做條目）皆零命中；B-1a 劇本與 B-4 分支刪除交使用者。
- 體檢時請特別看：A-2 閘道的取消路徑（`EnterAsync` 等待中取消不洩漏許可）、A-1 `SelectNextBatch` 在 cutoff 落在近日層內時的邊界（cutoff 早於 today−2 時近日層整層被擋）、B-3 的 SQL Server DROP 分支（只驗編譯）、B-2 的狀態變更區間未縮小（BACKLOG 已記）。
- 瀏覽器實測涵蓋與驗不到的部分見「批次 C 瀏覽器實測」列。

## 體檢輪修正

體檢方 Claude（Fable 5.1；實作主要由 Opus 5.5 完成），對 `dev..feature/feedback-57`（13 commit、75 檔）做規劃逐條比對、四視角獵 bug（兩個 Opus 5.5 medium 唯讀代理：程式碼多視角＋探針、規劃與文件比對；共回報 20 條，採納 17 條、2 條記錄不改、1 條與既有設計一致不改）、架構契合與文件稽核。每條程式修正都有迴歸測試，並以突變（備份→改壞→定向紅→還原 `cmp`→重建）證明測試會咬人。

| # | 哪裡 | 症狀 | 怎麼修 | 迴歸測試 |
|---|---|---|---|---|
| 1 | `PrtgBackfillRunner`（P1，代理探針重現） | 回填天數內沒有主機對應的日子永遠不記完成水位：「全部完成」湊不齊、拒絕空跑被繞過；續跑只剩這種日子時整趟回失敗（「沒有任何一天有主機對應」）還重翻狀態變更區間 | 無對應、無目標 sensor 的日子也記完成（指紋含對應，之後有對應就不算完成） | `PrtgFullBackfillPreviewServiceTests.完成水位_無主機對應的日子也算完成_全部完成後拒絕空跑`＋突變 |
| 2 | `AIService`／`IAiRequestGate` | 規劃承諾互動排隊逾 5 秒降級，但閘道在實例佇列之後取、上限只套在閘道上：同一互動實例的第二個問題在佇列裡等到前一個答完 | 介面加 `InteractiveMaxWait`（null 閘道為無限）；互動車道的實例佇列也套同一上限，逾時回失敗不發請求；訊息共用一句 | `AiRequestGateTests.互動車道在實例佇列排隊逾上限也降級不發請求`、`背景車道的實例佇列不設上限`＋突變 |
| 3 | `PrtgManualWorkGate.WaitUntilIdleAsync` | 管線讀到「同步進行中」後呼叫等待，等待裡重讀 `IsRunning`；同步剛好在兩次讀取之間結束就掉進回填分支：回「回填已結束」且剛成功的同步再做一次 | 簽名改 `WaitUntilIdleAsync(observed, ct)`，以呼叫端看到的種類為準 | `PrtgStructureSyncServiceTests.手動作業閘門_呼叫端看到的是同步而同步已結束_不誤判成回填`＋突變；管線替身斷言收到的種類 |
| 4 | `AiAnalysisHostedService.OnFetchFollowUp` | CLAUDE.md 與 WEB-SPEC 都寫「閒置原因只有一份判定」，即時觸發卻自己抄一份 disabled／backfill／no-pending | `BlockingIdleReason` 加 `requireWindow`，即時觸發走它（不看窗口） | `AiAnalysisSchedulerTests.跟隨取數_即時觸發的前置判定走BlockingIdleReason_不另抄一份`（源碼守門）＋突變 |
| 5 | `runs.js` 立即執行 modal 提示 | 提示依賴回填狀態端點，該端點對權限只及部分主機的維護者回 403，提示永遠不出現但 PRTG 日路徑照樣會等 | `ScheduleStatusDto.PrtgBackfillWaiting`（scheduler 從閘門讀），提示改看它、開 modal 重查排程狀態 | `RunsPageUiTests.立即執行Modal在手動回填執行中顯示等待提示` 改寫 |
| 6 | `core/prtg-acceptance.js` AI 項 | `idleReason=backfill-pending` 且待補 0 件顯示「已確認」，與排程頁「0 件（資料校正中）」矛盾 | 回 pending、文案同一句 | `prtg-acceptance.behavior.mjs ai`＋突變 |
| 7 | 同上，結局用詞 | 驗收段 `partial` 寫「部分成功」、執行總表徽章寫「部分失敗」 | 與 `PRTG_OUTCOME_META` 同一套用詞 | `prtg-acceptance.behavior.mjs scheduledRun`＋突變 |
| 8 | 同上＋`runs.js` | 定案「檢視執行連到該趟執行詳情」只連到總表，DTO 的 `RunId` 無消費端 | 連結 `/runs?run=<id>#list`；`runs.js` 讀 `?run=` 直接開執行詳情 | 同上；`RunsPageUiTests.執行詳情深連結與AI設定狀態即時同步` |
| 9 | 同上，無排程文案 | 「近 8 天」在前端寫死（後端是 `LookbackDays+1`），文件又寫另一句 | 文案不寫天數；WEB-SPEC 照實際 | 同上 |
| 10 | `prtg-admin.js` 快照表 | 「品質達標列／目標」欄顯示 `availableValues`（快照寫入數，可能重複計數），驗收判定用 `databaseUsableValues`；§13 叫人看那欄會與驗收段對不上 | 欄改 `databaseUsableValues`，另加「快照寫入」欄 | `PrtgAdminPageUiTests.快照表品質達標欄與驗收同一個數_驗收段可重新確認_全部重做勾選重畫不洗掉` |
| 11 | 同上，驗收段 | 三個來源只在載入時讀一次，取數或 AI 跑完畫面不更新、失敗也沒有重試 | 段內「重新確認」鈕重抓三者 | 同上 |
| 12 | 同上，全量回填預覽 | 勾了「全部重做」再按重新預覽，勾選被重畫洗掉 | 重畫前記住勾選帶回 | 同上 |
| 13 | `runs.js` AI 卡 | `aiAvailable` 只載入時讀 `/api/admin/settings`，`idleReason` 改成即時後同一張卡會一半新一半舊 | 跟著 `idleReason !== 'disabled'` 走 | `RunsPageUiTests.執行詳情深連結與AI設定狀態即時同步` |
| 14 | `ScheduleDtos.cs`、`StoreFakes.cs` | 新類別插在既有 `<summary>` 與類別之間，兩個既有類別失去說明、新類別掛兩段 | 搬回原位 | 編譯 |
| 15 | `PrtgDailyPipeline` | §5a 寫等待輸出附開始時刻，只有回填分支有 | 同步分支也寫 | `PrtgDailyPipelineTests` 既有 |
| 16 | 文件 | CLAUDE.md 篩選詞 `JsBehavior` 一條都篩不到（類別名是 `*BehaviorTests`）；WEB-SPEC §9.10 把取數路徑的深析算進背景車道（實際關閉）、缺完整性保證交叉引用；PRTG-SPEC §13 第 1 步引用不存在的 BACKLOG 條目（BACKLOG 自己也引用）、第 3 步自相矛盾、第 4～6 步寫代碼而畫面顯示中文、第 7 步 AI 未設定走不到；§5 缺預覽欄位名；說明書兩處沿革字眼 | 逐條改正；說明書補「互動排隊逾 5 秒會放棄」 | `HelpContentServiceTests`、文件守門 |
| 17 | 規劃比對差異回寫 | B-2「預覽帶 `resume=false`」沒做：前端以勾選切換，預覽同時給兩組數字，不需要參數（設計簡化，本處回寫）；C-3 的 JsBehavior 驗收只做字串測試（一行文案）；各段驗收指令的篩選詞與實際類別不符：A-1 `EfRecordQueryTests`、B-1b `PrtgAcceptanceBehaviorTests`＋`SchedulerTriggerQueryTests`、B-2 `PrtgFullBackfillPreviewServiceTests`、B-3 `LegacyObservationsRemovalTests` | 記錄，不改程式 | — |

記錄不改：代理提出 `lastRunSuccess` 為 null（站台重啟後）時驗收 ② 算通過——是刻意的（行程內狀態、沒有失敗證據），寫進 WEB-SPEC；A-1 近日層固定今天−2 與 A-2 閘道在佇列之後取，皆為實作輪已回寫的設計修正，體檢只補上第 2 條的佇列上限。代理「已查過沒問題」的組合（閘道壓測、TCS 交換、cutoff 邊界、水位寫入次數、B-3 四種組合、收合初始狀態、`progressPhase` null 消費端）由主代理讀碼複核，不再列。

定向測試（閘道／同步／管線／回填／AI 排程／排程頁 UI／維護頁 UI／驗收行為／觸發查詢／控制器／前端一致性／模組匯入／逾時行為／觀察表移除／AIService）：288 通過。全量 `dotnet test`（2026-10-22，Windows）：**6,478 通過／10 略過／0 失敗**，總計 6,488；實作輪 6,471 → +7（新增迴歸測試 7 條）。

## 終檢輪

Claude（Opus 5.5）併 dev 前複查體檢修正 commit：AI 實例佇列逾時路徑未取得佇列即返回、不會多還許可；回填無對應日記完成只在全量觸發分支、指定主機補值不碰水位；
閘門等待只在 `Running != None` 時呼叫；跟隨取數即時觸發行為與原本相同（僅判定來源合一）；深連結只接受純數字 `run`；AI 卡兩個「可用」來源（`/api/ai/status` 與 `idleReason`）同一判準。
文件稽核：現行文件無新增沿革敘事，PLAN「明確不做」已在。dev 自分支起點未移動，併入後的樹與跑過全量的分支相同，未重跑全量。**終檢無新發現。**
