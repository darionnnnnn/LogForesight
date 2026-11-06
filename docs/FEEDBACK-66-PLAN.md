# LogForesight 第 66 輪規劃

> 狀態：實作完成、終檢修正完成，待換模型體檢後併 dev（分支 `feature/feedback-66`）
> 基準：dev@6110f927（Windows 8,515 綠／11 略過；macOS 8,512 綠／14 略過；全量約 8 分鐘；第 65 輪＝macOS／Windows 雙平台一致已併入）
> 來源：使用者回饋 8 項（2026-11-06：排程執行時間與未完成項、列表 hover 連帶變色、PRTG 與 AI 順序、AI 摘要加給主管的說明、重點問題長文字收合、右欄預設收合、處理面板重排、說明書對齊）＋站台診斷匯出（2026-11-06）檢視出的順手發現
> 實作方式：主代理寫階段規格，實作派內建 Agent（Sonnet 5.5，effort 由主代理依段評估；純測試小段可用 Haiku 5.5）；同一 checkout 一次只派一個改檔代理；每段主代理獨立重跑驗收，不採信代理摘要
> 設計依據：本專案凡畫面相關一律套 `ui-ux-pro-max`（使用者 2026-11-06 定案），以 `docs/DESIGN-SYSTEM.md` v2 為既有基準；畫面批次（C 前端、D、E）依其「設計判斷順序」（可用性與語意 → 真實內容與響應式 → 一致性 → 效能與動態）與驗證表交付

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| Z | 前置：偶發紅測試（第 65 輪取回後 Windows 全量檢查發現） | 極小 | 無 | 0 |
| A | 表格列 hover 與 sticky 動作欄的選擇器滲漏（P2） | 小 | 無 | 1 |
| B | 排程總表：空資料待複查獨立成狀態、今日列「最後一趟結束時尚有」（P1、B5） | 中 | 無 | 2 |
| C | AI 契約新增「給主管的說明」欄位（P4） | 中 | 前端段在 A 之後 | 3 |
| D | 風險日詳情閱讀面：長文字收合、報告全文每次收合、右欄（處理狀態＋處理歷程）收成側軌（P5、P6） | 中（前端） | A | 4 |
| E | 處理面板與說明欄編輯器重排（P7） | 中（前端） | D 定右欄寬度 | 5 |
| F | 健康頁金鑰檔位置提示（B4） | 小 | 無 | 6 |
| G | 文件與說明書：P3 順序說明、全 24 章稽核（P8）、WEB-SPEC／DESIGN-SYSTEM／CLAUDE.md 同步 | 中 | A～F 定案後 | 7 |
| H | 收官：全量測試、終檢、體檢交接 | — | 全部 | 8 |

共用規則（每批次都適用）：
- 前端段的定向篩選至少含 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`；新增 `*.behavior.mjs` 案例要同時登錄 C# `InlineData`；頁面模組新匯入的共用函式要在替身補一份。
- 不新增 inline `style=`／inline 事件，動態樣式走 `classList`／`style.setProperty`（`InlineStyleUiTests` 守門）；外部字串與 AI 產出一律 `textContent`／`markdown-lite` 唯一出口。
- 「暫定」數值（300 字、150 字、側軌 3rem、右欄 20rem 等）由執行端依實作事實調整，調整要寫進執行紀錄。
- 說明書改動同時改使用者版與 AI 版，照 `docs/HELP-WRITING-GUIDE.md`。
- 瀏覽器走查資料：本機 dev 站台（SQLite）目前沒有分析紀錄；D／E 走查前先在 dev 站台「立即執行（只跑本機）」產生 host-001 的紀錄，產不出來就退回注入 DOM 探針（本輪重現 P2 的手法）。走查中「無法以測試判定」的項目在驗收段明標「手動項」，其餘都要能以 `dotnet test` 或探針數值判定。

## 批次 Z：前置——偶發紅測試

### 現況與核對結果

- 2026-11-06 自 GitHub 取回第 65 輪後，在 Windows 做完整建置與全量測試。開發機 Node 原為 v21.6.1，低於第 65 輪訂的下限 22.7（`test.runsettings` 設 `LF_REQUIRE_NODE=1`，前端行為測試會整批紅），已以 winget 升級為 v22.23.4（與 CI 釘的大版本 22 相同）後重跑。
- 結果：建置 0 錯誤；8,514 通過／1 失敗／11 略過，總數 8,526 與 CLAUDE.md 的 Windows 基線一致。
- 第 65 輪的正式碼改動只有 `PrtgSnapshotJournal` 鎖定檔改 `FileShare.None`；全專案沒有其他地方開這個 `.lock` 檔，Windows 上無行為差異。其餘改動都在測試與 CI。
- 失敗的是 `AiAnalysisSchedulerTests.情境7_強制重新分析_重標後全部重跑且舊AI文字保留顯示直到覆蓋`：單獨重跑 5 次紅 1 次。根因在測試：`TriggerRunAsync` 以 `Task.Run` 啟動背景執行後立刻回傳（`AiAnalysisHostedService.cs` 約 250 行起），測試只用固定 `Task.Delay(100)`（`AiAnalysisSchedulerTests.cs:601`）假設它已結束，接著重標兩筆紀錄；機器忙時背景迴圈尚未結束，會撿走剛重標的紀錄，重標後的斷言偶發失敗。正式碼的強制重新分析路徑會先停掉進行中的執行再重標，不受影響。
- 同型普查：`TriggerRunAsync` 後接固定延遲的只有這一處；同檔至少 9 處已用 `runState.WaitForCompletionAsync`（未執行時回 true、執行中等到結束，`AiAnalysisRunState.cs:235-253`）。

### 定案

1. 固定延遲改成等待背景執行真正結束，不改正式碼。

### 改動

1. 情境 7：取用 harness 回傳的 `runState`，`Task.Delay(100)` 換成 `Assert.True(await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10)))`；第 599 行的 `TriggerRunAsync` 回傳值也斷言為 true。

### 驗收

- `dotnet test --filter "FullyQualifiedName~AiAnalysisSchedulerTests"`（55 案）全綠；情境 7 單獨連跑 30 次（`-v q`，不開詳細記錄）全綠。
- 突變（主代理執行、以檔案備份還原）：暫時在 `ExecuteProcessingLoopAsync` 開頭加 `Thread.Sleep(300)`，舊寫法須穩定紅、新寫法須綠；驗完 `cp` 回原檔並 `touch`。第一次觸發時兩筆都已完成、不會呼叫假 AI，所以不能用假 AI 延遲當突變。

## 批次 A：表格列 hover 與 sticky 動作欄

### 現況與核對結果

- `site.css:2292-2305`：`.lf-table-wrap--sticky-action th:last-child / td:last-child` 與 hover 補色規則是**後代選擇器**，會命中 (1) 展開列 `tr.lf-row-detail` 的 colspan 格（唯一子元素，也是 `:last-child`）、(2) 展開格內巢狀表格的每一列最後一欄。
- 本機以注入 DOM 重現（2026-11-06）：hover 巢狀任一列時整個展開格變成 `--lf-primary-soft`（使用者說的「後方欄位一起變色」）、巢狀表最後一欄變成 `position: sticky` 帶左陰影、展開格原本 `.lf-row-detail > td` 的灰底（`site.css:1991`）被特異度較高的 sticky 白底蓋掉。
- Bootstrap 5.3.8 的 hover 底色不是 `background`，是 `.table > :not(caption) > * > *` 的 `box-shadow: inset 0 0 0 9999px var(--bs-table-bg-state)`（特異度 (0,1,1)），hover 時 `.table-hover > tbody > tr:hover > *`（(0,2,2)）設變數。巢狀 `.table` 自己重設 `--bs-table-bg-state: initial`，所以只有展開格本身與最後一欄出問題。
- `handler-detail.js:176` 的 `lf-row--highlight`（`site.css:1990`，(0,2,3)）與 `stickyLastColumn: true`（`:498`）同時使用：目前 sticky 規則 (0,2,1) 輸給高亮列，高亮色看得到；改成長子代鏈若特異度升到 (0,3,4) 會反過來蓋掉高亮列最後一欄。
- 同型普查：`renderTable` 的 `onRowExpand`／`rowDetail` 呼叫端共 **9 處**（records.js:863、runs.js:316 與 :642、host-detail.js:363、handler-detail.js:500、issue-owners.js:150、permission-changes.js:411、record-detail.js:1268 與 :1353），其中 `stickyLastColumn: true` 的有 records、issue-owners、handler-detail 三處。修共用 CSS 一次涵蓋。

### 定案

1. sticky 動作欄只作用在**標記容器直接包的那張表**的表頭與資料列最後一欄，不作用在展開列、不作用在巢狀表格。
2. 展開列 `tr.lf-row-detail` 不參與 hover 變色，維持灰底：以 `.lf-row-detail > td { box-shadow: none; }` 拿掉 Bootstrap 載入 hover 色的 inset 陰影（同級 (0,1,1)，`site.css` 後載入即贏），不靠重設變數（(0,2,1) 贏不了 (0,2,2)）。
3. 新選擇器以 `:where()` 壓平特異度，維持原本 (0,2,1) 上下，讓 `lf-row--highlight` 仍能蓋過 sticky 欄；hover 補色規則同樣只對直接資料列生效。
4. 列印規則同步改成同一組選擇器。

### 改動

1. `site.css` 三組 sticky 規則（基本、hover 補色、列印）改為子代鏈，例如 `.lf-table-wrap--sticky-action > table > :where(thead, tbody) > :where(tr:not(.lf-row-detail)) > :where(th, td):last-child`（寫法暫定，等價即可，特異度不得高於 (0,2,1)）。
2. `.lf-row-detail > td` 補 `box-shadow: none`。
3. 守門測試放 `TableConsistencyUiTests`（已在讀 `site.css`，`:111`）：解析所有含 `lf-table-wrap--sticky-action` 的選擇器（含逗號清單與 `@media print` 內層），斷言每個都以 `> table >` 子代鏈限定、不含後代形式的 `td:last-child`；斷言存在 `.lf-row-detail > td` 的 `box-shadow: none`。字串守門證明不了層疊結果，瀏覽器探針（下）不可省。

### 驗收

- `dotnet test --filter "FullyQualifiedName~TableConsistencyUiTests|FullyQualifiedName~InlineStyleUiTests|FullyQualifiedName~FrontendConsistencyUiTests|FullyQualifiedName~RunsPageUiTests"` 全綠（命中 4／3／45／45 案）。
- 突變：把 `site.css` 的選擇器改回後代形式，`TableConsistencyUiTests` 新守門須紅（被突變的是產品 CSS，不是測試）。
- 主代理以 dev 站台＋注入 DOM 探針（同重現手法）量測：hover 巢狀列時展開格 `background-color` 不變、巢狀最後一欄 `position` 為 `static`；父表資料列 hover 時最後一欄與其他欄同色；加 `lf-row--highlight` 的 sticky 表列，最後一欄 `background-color` 等於高亮色。四個數值都寫進執行紀錄。

## 批次 B：排程總表「空資料待複查」與今日列「最後一趟結束時尚有」

### 現況與核對結果

- 使用者畫面的「3（尚未處理）」：`NetiqStatus`（`RunMonitorService.cs:318-319`）把 `NetiqAttemptStatus.EmptyOnce`（NetIQ 回空資料、等下一窗口複查一次）回成 `"none"`，與「本趟沒排到」一起進 `NotRunCount`（`:194` 的 `default`）。逐主機明細格（`NetiqCell`，`:226`）用同一個函式，`StatusNote`（`:288`）會寫「空資料，下一窗口複查一次」，但日列彙總看不到；過去日期的 EmptyOnce 若沒有後續窗口，會一直掛著同一句（`NetiqStatusNote` 沒看 `AutoRetryExhausted`）。
- 「執行時間」欄（`run-timeline.js:118-131` `dayTimingText`／`:153` `renderDayTimingCell`、`RunDayTimingBuilder`）只算取數趟與跟隨 AI 趟的起訖，不知道當天是否還有待補。執行紀錄列有 `ResumeSummary`（`RunMonitorService.cs:525`）取自 `RunResume.Of(run)`——那是**整趟**結束當下的快照（NetIQ 主機日跨該趟處理的所有資料日、PRTG 裝置聯集；`RunResume.cs:9-21`），不是日列的量；EmptyOnce 不在 `RunResume` 裡（`EmptyDeferred` 只進 console），兩者不會重複計，但單位不同（主機日／裝置／顆／布林）。異常中斷的趟 `Phases` 可能不完整，`RunResume.Of` 可能回 `HasWork=false`。
- 前端 `STATUS_META`（`runs.js:29-42`）沒有的狀態鍵會退回 `none` 樣式（`:462`）；匯出 `RunExportService.cs:882` 的 `NotRun` 與 `:1365` 說明字串、`schemaVersion` 維持 1。
- 用詞現況：「待補」已有四種意思（狀態 `waiting`、執行紀錄「仍待補：…」、PRTG「N 天待補」、AI「待補分析」）；「待複查」畫面與說明書都沒出現過。

### 定案

1. **單點判定**：`NetiqStatus` 對 `EmptyOnce` 回新狀態值 `"recheck"`（名稱暫定），彙總 switch、逐主機明細、前端 `STATUS_META`（標籤「空資料待複查」、灰藍色系）、`RunDtos` 狀態清單註解、匯出的 `Closed` 白名單一起登錄；不再計入「未執行」。總表格子寫「N（尚未處理）」維持給真正沒排到的主機，「空資料待複查 M」是新計數 `RecheckCount`，與「未執行」並列同一格、各自有文字，不新增欄位。
2. `NetiqStatusNote` 對 EmptyOnce 分齡：今天與昨天的列維持「空資料，下一窗口複查一次」；更早的列改「空資料，已無自動複查；要補請用立即執行回望」。
3. **「最後一趟結束時尚有」只對今天的列顯示**：取今天最後一趟已結束且非異常中斷的取數趟，`RunResume.HasWork` 時在「執行時間」第二行小字追加「最後一趟結束時尚有：{Describe()}」；最後一趟是異常中斷時改寫「最後一趟異常中斷」。過去日期不顯示（那是當時的快照，後續趟已補過，顯示會誤導）。
4. 今天執行中且沒有已結束趟時，第一行寫「進行中」（日期欄已標「今日（執行中）」，不重複時間）。
5. 匯出 `dailyHealth`：新增 `recheckCount`、`pendingSummary`（今天的列才有值；走與 `prtgNote` 相同的 `TextCut` 遮罩）；說明字串明寫「`notRun` 自第 66 輪起不含空資料待複查，改看 `recheckCount`」。

### 改動

1. `RunMonitorService`：`NetiqStatus` 新狀態值；`GetDaySummaries` switch 加 case 與 `RecheckCount`；`PendingSummary` 只在 `IsToday` 計算（排除 `Interrupted`）；`NetiqStatusNote` 分齡。
2. `RunDaySummaryDto` 新增 `RecheckCount`、`PendingSummary`；`RunDtos` 狀態清單註解補 `recheck`。
3. `run-timeline.js`：`dayTimingText(timing)` 維持純粹；`renderDayTimingCell(timing, { pendingText, inProgress })` 加第二參數負責第二行追加與「進行中」；`runs.js` 的總表欄位傳入；「未執行」欄 render 併顯示 `RecheckCount`；`STATUS_META` 加 `recheck`。
4. `RunExportService`：`dailyHealth` 兩個新欄位、`Closed` 白名單、說明字串。
5. WEB-SPEC §9.10、說明書 10-scheduler.md（執行總表那格拆成條列，避免超過句長守門）、13 名詞解釋加「空資料待複查」。

### 驗收

- `dotnet test --filter "FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunDayTimingBuilderTests|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~RunsTimelineBehaviorTests|FullyQualifiedName~RunExportServiceTests"`（命中 67／17／45／16／68 案）全綠，含新增：
  - EmptyOnce 主機：日列 `RecheckCount` 1、`NotRunCount` 0；`GetDayDetail` 同一主機 `Status == "recheck"`，`StatusNote` 今天／昨天與三天前兩種文案。
  - 同日兩趟、第一趟有失敗、第二趟全成功：今天的列 `PendingSummary` 為 null；最後一趟 `Interrupted`：`PendingSummary` 為「最後一趟異常中斷」；非今天的列一律 null。
  - 前端：`renderDayTimingCell` 第二參數有 `pendingText` 時第二行含該文字、沒有時不含；`inProgress` 且無已結束趟時第一行為「進行中」；`STATUS_META.recheck` 存在且標籤為「空資料待複查」。
  - 匯出：`dailyHealth` 含 `recheckCount`／`pendingSummary`，說明字串含「recheckCount」。
- 突變：把 `NetiqStatus` 的 EmptyOnce 改回 `"none"`，`RunMonitorServiceTests` 新案須紅；把 `PendingSummary` 對非今天也計算，「非今天一律 null」須紅。
- 「什麼算一個」：待複查以主機日計（帳本一列一主機日）；「最後一趟結束時尚有」原樣沿用 `RunResume.Describe()` 的單位；當天沒有任何趟維持「—」。

## 批次 C：AI 契約新增「給主管的說明」欄位

### 現況與核對結果

- JSON 契約四欄 headline／story／trend_story／action（`AnalysisPromptBuilder.cs:224-230`），解析 `AiAnalysisResult`，`validate` 是單一布林把全部欄位 AND 在一起（`LogAnalysisService.cs:715-718`）；不合格走 JSON 重試，重試耗盡整個主機日降級成「格式異常」（`:735-749`）。
- 欄位流向（逐檔手抄，共六處寫入點）：`AiAnalysisResult` → `MainAnalysisResult`／tuple（`LogAnalysisService.cs:600-601`、`:668-670`）→ `scratch`（`:625-628`，`CloneForReport` 是 JSON 往返會自動帶）→ `AiOutcome`（位置式 record，`DailyAnalysisRecord.cs:233-248`；全專案 13 處建構，測試端 11 處）→ **`EfAnalysisRecordStore.AttachAiResultLocked` 把 outcome 四欄寫回 record（`:575-578`）**，接著用 `ForReportInput(record)` 與報告草稿指紋比對、不符就 `return false`（`:605-609`）→ `RecordStorageShaper`（`:42-45`；`RecordStorageShaperTests:195-222` 以反射逐屬性守門，新屬性沒進投影會先紅）→ `RecordDetailQueryService`（`:183-186`，案件授與檢視清空）→ `RecordDetailDto` → `record-detail.js:347-385`。
- 其他消費端：`RiskReportService.cs:352-362` 報告 txt 白話總覽；`PrtgHistoricalRiskReview.cs:72-74` 歷史修訂覆寫三欄；`AnalysisOrchestrator.cs:1423-1430` console 印三欄；`HostDayWorkflowFingerprint.ForReportInput`（`HostDayWorkflowState.cs:1098-1102`）把四欄納入報告輸入指紋——**該指紋不持久化**（`PendingRiskReportDraft` 標 `[JsonIgnore]`，`DailyAnalysisRecord.cs:11-12`），只在同一次操作內比對，不存在「升級後整批報告重寫」的問題。郵件、Telegram、週報、儀表板、匯出去識別化都不碰敘事欄；`AiOutputSanitizer` 對整段 JSON 簡繁轉換，新欄自動涵蓋；`content_json` 無長度上限、兩後端一致、`Brief` 不是抽出欄所以不動 `ExtractVersion`；專案沒設 `JsonUnmappedMemberHandling.Disallow`，回退舊版讀到新 JSON 會忽略新鍵。
- 前端 AI 區塊規範：WEB-SPEC §9.3「AI 產出標註」——`.lf-ai-block`（已有 3px 左色條）＋`lf-badge--secondary` 徽章，`aiAnalyzed=true` 才包框。

### 定案

1. 新欄位：JSON 鍵 `brief`、屬性 `Brief`、畫面標籤「給主管的說明」。prompt 契約：「2～3 句、可以直接照念給主管聽：影響範圍、有多嚴重、是否需要主管決策或支援；不寫處置步驟（那在 action）、不含 Event ID、技術名詞、主機 IP」。上限 `MaxBriefChars = 150`（暫定）。
2. **選填欄位不拖垮整日**：`Brief` 不進 `validate`、缺少時為空字串。成功路徑後處理（單一 helper）：超過 150 字先在最後一個句號／分號處截到上限內，截不出來就整欄丟棄；內容命中 IPv4 樣式、`Event ?ID`、`事件 ID`、`EventId` 任一就整欄丟棄；丟棄寫一行 Debug log（含主機日），不重試、不降級。
3. 只有 AI 產出時才有：低風險固定句、統計模式、格式異常降級、歷史風險修訂後一律空字串；`PrtgHistoricalRiskReview.Apply` 覆寫三欄時一併清空 `Brief`。
4. 既有紀錄不回補；要補只能走「強制重新分析」。說明書 04 章講清楚「舊日子沒有這一段是正常的」。
5. 報告 txt：白話總覽段在 headline 之後印一行「給主管的說明：…」，只在非空時印；舊報告不回溯。console 輸出同步多印一行（非空時）。
6. 報告輸入指紋：`Brief` 一律納入 `ForReportInput`（指紋不持久化，無相容性顧慮；一律納入比條件式簡單）。
7. `AiOutcome` 與 `MainAnalysisResult` 的 `Brief` 加在**尾端、帶預設值 `""`**，不動既有 13 處建構。
8. 畫面：headline 之後、「狀況」之前插入 `.lf-ai-brief` 子卡（暫定）：`--lf-primary-soft` 底、`--lf-primary-soft-border` 1px 邊框、不另加左色條（外層 `.lf-ai-block` 已有）、左上小徽章「給主管的說明」、內文字級介於 headline 與狀況段之間、右上「複製」小按鈕（`navigator.clipboard.writeText`，與 `report-view.js:48` 同一手法，`lf-no-print`）。案件授與檢視與 `aiAnalyzed=false` 不顯示。
9. 詢問 AI、說明書問答、`RiskReportService:213` 的【全局脈絡】不改；診斷匯出不加 `BriefMaxChars`（明確不做）。

### 改動

1. `AnalysisPromptBuilder` JSON 契約加 `brief`；`AiAnalysisResult.Brief`；`LogAnalysisService`：後處理 helper（定案 2）、`MainAnalysisResult`／tuple／`scratch`／`AiOutcome` 尾端參數、低風險與降級路徑給空字串。
2. `DailyAnalysisRecord.Brief`；**`EfAnalysisRecordStore.AttachAiResultLocked` 的 `record.Brief = attemptOutcome.Brief`**；`RecordStorageShaper`；`HostDayWorkflowFingerprint.ForReportInput`；`PrtgHistoricalRiskReview`；`RiskReportService`；`AnalysisOrchestrator` console。
3. `RecordDetailDto.Brief`、`RecordDetailQueryService`（案件授與清空）。
4. `record-detail.js` 渲染＋複製鈕；`site.css` 新 class；`DESIGN-SYSTEM.md` §7 補一行。
5. **敘事欄位同步守門**（DRY，取代抽值物件）：新測試以反射取 `AiOutcome` 與 `DailyAnalysisRecord` 同名的字串屬性，各設不同哨兵值，走 `TryAttachAiResult` 後逐一比對相等——下次再加敘事欄位時漏抄任何一處會自動紅。
6. 文件：WEB-SPEC §9.3「AI 產出標註」四段改五段；DETECTION-SPEC 的 AI 契約段；DB-SPEC `content_json` 說明；說明書 04 章與 13 名詞解釋。

### 驗收

- `dotnet test --filter "FullyQualifiedName~LogAnalysisServiceSplitTests|FullyQualifiedName~LogAnalysisServiceRetryAiTests|FullyQualifiedName~PromptHostKindTests|FullyQualifiedName~AnalysisPromptBuilderPrtgTests|FullyQualifiedName~RecordStorageShaperTests|FullyQualifiedName~RiskReportServiceTests|FullyQualifiedName~HostDayWorkflowTests|FullyQualifiedName~AnalysisRecordStoreContractTests|FullyQualifiedName~SqlRetryTransactionReplayTests|FullyQualifiedName~AiEvidenceVersionContractTests|FullyQualifiedName~PrtgFindingsRegistryTests|FullyQualifiedName~RecordListDetailAiStateTests|FullyQualifiedName~AiStateUiTests|FullyQualifiedName~AiStateBehaviorTests"` 全綠（類別都已確認存在；後三個 `AiOutcome` 建構點的類別是改 record 時最先壞的地方），含新增：
  - prompt 含 `"brief"` 鍵；回覆帶 `brief` 解析進 `Brief`；回覆缺 `brief` 時其餘欄位照常成功、`Brief` 為空、`AiDegraded=false`。
  - `brief` 200 字且第 120 字處有句號 → 截到句號；200 字無句號 → 空字串；含 `10.216.19.43`／`Event ID 4625` → 空字串；三種情況其餘四欄照常寫入、不觸發重試（假 AI 呼叫次數為 1）。
  - 契約測試：含非空 `Brief` 與 `ReportDraft` 的 `AiOutcome` 呼叫 `TryAttachAiResult` 回 true，讀回 `Brief` 等於 outcome（沿用 `AnalysisRecordStoreContractTests.cs:38-56` 的 `ExpectedAttachedAiReportInput` helper 並補 `Brief`）。
  - 敘事欄位同步守門（改動 5）通過。
  - 歷史風險修訂後 `Brief` 為空；低風險與統計模式紀錄 `Brief` 為空；報告 txt 非空時含「給主管的說明：」、空時不含；`RecordDetailDto.Brief` 案件授與檢視為空。
- 前端定向 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout` 全綠，含新增 behavior：`aiAnalyzed=true` 且 `brief` 非空時出現徽章「給主管的說明」且節點位於 headline 與「狀況」之間、有「複製」鈕；`brief` 空或 `aiAnalyzed=false` 時整個子卡不出現。
- 突變：拿掉 `AttachAiResultLocked` 的 `record.Brief` 賦值，契約測試與同步守門都須紅；把 `Brief` 塞回 `validate`，「缺 brief 不降級」須紅。
- 跨段回頭 grep（提前到本批）：`grep -rn "\.TrendAssessment\b" LogForesight.Core LogForesight.Web --include=*.cs` 的每個命中點逐一對照是否有 `Brief` 對應（刻意不改的三處：`RiskReportService:213`、週報、診斷匯出，寫進執行紀錄）。

## 批次 D：風險日詳情閱讀面

### 現況與核對結果（第二版修正：右欄執行時只有兩張卡）

- `RecordDetail.cshtml` 右欄 `col-lg-4` 寫了五張卡，但 `record-detail.js:23-34` 的 `initRecordSections()` 啟動就把 `alerts-card`、`coverage-card` 搬進底部「證據」`<details>`、`categories-card` 搬進「脈絡」`<details>`、`report-cards` 與 `chat-card` 也搬到下方。**右欄執行時只剩「處理狀態」與「處理歷程」兩張卡**，恰是使用者要收合的兩張；兩個 `<details>` 的開合記 `localStorage`（`:38-47`），列印時自動展開。
- 長文字：只有 `keyDetails` 有 3 行 `line-clamp`＋「顯示全部」（`record-detail.js:1647-1688`、`site.css:2176-2196`），靠 `scrollHeight` 量測與 `lf-remeasure`（`collapsedRestSection`，`:1359-1365`）補量；`:1435` 的註解寫明帳號／IP 彙總「不能真的藏起來」。說明 `knownIssue`、`residualCredentialBlock`、`prtgTrendEvidenceBlock`、`guidancePanel` 的說明／影響與 AI 判讀結果（點擊後才寫入，`:2193-2210`）、AI 深入分析每一條（結構化：problem／impact／可能原因／處置，`:2231-2275`）都沒有收合。來源觀測與原始訊息是按鈕開 modal，不在範圍。
- 報告全文卡 `reportCard` 預設收合但展開狀態記 `localStorage`（`report-view.js:110,153`；WEB-SPEC §9.3 第 1 條）；只有 `record-detail.js:2295-2296` 兩處傳 `storageKey`。
- 連到 `/records/{hostId}/{date}` 的入口：records.js（:605 列表整列、:912、:936「去處理」）、record-detail.js:228「下一筆未處理」、handler-detail.js（:1010、:1025「待處理風險日」）、user-detail.js（:234、:255「待處理風險日」、:272、:304）、host-detail.js（:234、:400、:530 時間軸）、prtg-admin.js:2266、伺服器端 `WorkOrderQueryService.cs:282`。`appUrl()` 只補前綴不吃 hash；本頁沒有用 `bindTabs` 的 hash 分頁，`#handling` 不衝突。勾選問題的 checkbox 在左欄（`:610-611`、`:640`），勾選會整張重建處理面板（`handling-panel.js:48-53`）；批次儲存後 `onBatchSaved` 會 `load()` 並清空選取（`:141-146`、`:206-208`）。
- 內容區寬度：側欄 15.25rem（244px），1024 視窗下內容區約 780px，`col-lg-4` 約 250px。

### 定案

1. **長文字收合共用元件**（`core/ui.js`，名稱暫定 `collapsibleText`）：可收合單位＝一個 DOM 容器，判準「容器 `textContent.length > 300`（常數，暫定）」，不量測版面。純文字容器收合時顯示前 300 字內**最後一個分隔符（空白、換行、`、`、`；`、`;`、`,`）之前**的內容＋「…」，不從帳號或 IP 中間切；markdown／結構化容器整段渲染後以 `max-height`＋底部漸層收合。按鈕文字「顯示全文（共 N 字）」／「收合」，`type="button"`、`aria-expanded`、`aria-controls`、`stopPropagation`、`lf-no-print`；列印一律全文。內容延後寫入（AI 判讀）的呼叫端在內容就緒後才呼叫。
2. 套用對象：keyDetails（取代 line-clamp，拿掉 `lf-remeasure` 派發與量測、`site.css:2176-2196` 與相關註解）、說明 `knownIssue`、殘留憑證說明、PRTG 趨勢證據文字、guidance 的說明／影響、AI 判讀結果、AI 深入分析每一條（以該條容器為單位）。只有超過判準的才出現按鈕。已知取捨寫進說明書：帳號／IP 彙總超過 300 字會先收起，點一下全開。
3. 報告全文（風險、體檢）每次進頁一律收合，不再記 `localStorage`（使用者要求）；列印維持畫面狀態（與現況相同）。
4. **右欄側軌**：≥ lg 時右欄（處理狀態＋處理歷程）預設收成寬約 3rem（暫定）的直式軌，兩顆按鈕：「處理狀態」顯示目前狀態文字＋「N/M 已結案」＋逾期時加「逾期」字樣（文字，不只靠顏色）；「處理歷程」顯示筆數。點任一顆展開整個右欄（寬 20rem，暫定；左欄 `flex: 1` 吃剩餘寬度）、捲到並聚焦該卡標題；欄頂「收合」鈕，收合後焦點回到軌上對應按鈕。`< lg` 不出現側軌、維持堆疊。列印：右欄展開、側軌隱藏。`caseGrantOnly`（案件授與檢視）不渲染側軌，右欄直接展開——那是這種使用者唯一能操作的區塊。
5. 自動展開與優先序：(a) 進頁時網址 hash 為 `#handling` → 展開並捲到處理狀態卡（不搶焦點），hash 只讀一次，讀完以 `history.replaceState` 清掉；(b) 第一次勾選任一問題 checkbox → 展開（版面只重排這一次）；(c) 之後使用者手動展開／收合的選擇**優先於**自動觸發，狀態存在頁面模組變數，不記 `localStorage`，批次儲存後的 `load()` 重繪沿用。勾選清空不自動收合。
6. 連結分類：以「去處理」為目的的連結帶 `#handling`——records.js:936「去處理」、record-detail.js:228「下一筆未處理」、handler-detail.js:1025 與 user-detail.js:255「待處理風險日」、`WorkOrderQueryService.cs:282`；其他（列表整列、主機時間軸、PRTG 維護）是查看，不帶。前端統一走 `core/paths.js` 新 helper `recordDetailUrl(hostId, date, { handle })`。
7. hash 與選取的判斷放 `record-detail.js`（頁面層，選取集合與 `caseGrantOnly` 都在那裡）；側軌本身是 `core/` 小模組（開／關／按鈕狀態），不放 cshtml。

### 改動

1. `core/ui.js` 新增共用元件＋`site.css` 對應 class（純文字與區塊兩種收合）。
2. `record-detail.js`：八個套用點改走共用元件；移除 `keyDetailsBlock` 量測與 `collapsedRestSection` 的 `lf-remeasure` 派發；`site.css` 刪 `.lf-issue-details--clamped` 與列印覆蓋。
3. `record-detail.js` 的兩張 `reportCard` 不傳 `storageKey`。
4. `RecordDetail.cshtml`＋新 `core/` 側軌模組＋`record-detail.js`＋`site.css`：右欄改「側軌＋可展開欄」版面（grid 或 flex，不用 inline style），兩顆按鈕的 `aria-controls`／`aria-expanded`，自動展開規則，`caseGrantOnly` 分支。
5. `core/paths.js` 新增 `recordDetailUrl`；五個「去處理」連結改用並帶 hash（含 `WorkOrderQueryService.cs:282`）。
6. 文件：WEB-SPEC §9.3（第 1 條改寫、新增側軌條目與連結分類）、DESIGN-SYSTEM §6b 補「右欄側軌」、說明書 04 章「先看哪裡」「右側與下方的卡片」改寫（右欄只有兩張卡；程式偵測訊號與類型分布在「證據」「脈絡」裡）。

### 驗收

- 前端定向 `dotnet test --filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"` 全綠，含新增 behavior 案例（登錄 `InlineData`；mini-dom 若缺 `textContent.length`／`classList` 以外的能力，在 `mini-dom.mjs` 補最小實作）：
  - 301 字純文字（第 290 字後是「、」）收合後顯示到「、」為止＋「…」、按鈕文字含「共 301 字」；300 字不出現按鈕；點按鈕後全文與「收合」，`aria-expanded` 翻轉。
  - 結構化容器 > 300 字時掛上收合 class 與按鈕，≤ 300 不掛；內容延後寫入後呼叫才評估。
  - `reportCard` 未傳 `storageKey` 時 `setReport` 後 `aria-expanded="false"` 且 `safeStorage` 無寫入。
  - 側軌：預設 `aria-expanded="false"`；hash `#handling` 時展開且處理狀態卡可見、`location.hash` 已被清空；第一次勾選後展開；手動收合後再勾選不展開；`caseGrantOnly` 時無側軌節點且右欄展開；`recordDetailUrl(1,'2026-11-05',{handle:true})` 以 `#handling` 結尾、不帶 `handle` 時沒有。
- `NextUnhandledShortcutUiTests`、`AccessibilityUiTests`、`CspPolicyGuardTests` 全綠。
- 突變：把判準改成 `>= 300`，「300 字不出現按鈕」須紅；拿掉 hash 判斷，對應案例須紅；拿掉「手動優先」旗標，「手動收合後再勾選不展開」須紅。
- 主代理探針（dev 站台，1024 與 1366 寬）：`document.documentElement.scrollWidth <= clientWidth`；側軌收合時左欄寬度 ≥ 內容區 − 3rem；展開時右欄寬 ≈ 20rem；數值寫進執行紀錄。手動項：Tab 到軌按鈕 → Enter 展開 → 焦點在卡標題 → 「收合」→ 焦點回軌按鈕；列印預覽右欄展開。
- grep：`lf-remeasure`、`lf-issue-details--clamped` 全專案零命中。

## 批次 E：處理面板與說明欄編輯器重排

### 現況與核對結果

- `handling-panel.js`：面板由多輪疊加——`readonlyField`「目前狀態」帶整句雙軌語意說明（該檔 `:170-173` 註解寫明「填錯／看漏代價較高，維持常駐」，只有主機負責人收進 icon）、處理人、`<hr>`、表單（狀態 chip：日層級 7 顆、批次 8 顆，`:29-30`；`lf-chip` 是 `white-space: nowrap`、標籤最長 11 字；`renderChips` 每次點擊 `replaceChildren` 重建、鍵盤焦點掉到 body，`:530-545`）、預計完成日、觀察天數、清除雜訊、說明 label＋textarea（`mb-3`）＋`noteFeedback`（`invalid-feedback`，靠 `.is-invalid ~ .invalid-feedback` 兄弟選擇器顯示）、`attachNoteEditor` 插入的計數列、規則提示、`w-100` 儲存鈕、最後更新。
- `note-editor.js`：`textarea.after(counterRow)`（`:117`），計數列左：字數＋沿用／常用語，右：AI 整理；草稿提示 `counterRow.before(notice)`；主鈕緊貼計數列（使用者截圖即此）。
- 兩個呼叫端 DOM 順序不同：面板是 [textarea, 計數列, feedback]；交辦回覆 modal（`issue-status-reply.js:89-103`）是 [textarea, noteError(`d-block`), 計數列]。
- 測試現況：`NoteEditorUiTests`／`HandlingStatusCatalogUiTests` 是原始碼字串斷言；`JsBehavior` 沒有 note-editor 行為測試；`mini-dom.mjs`（108 行）沒有 `after`／`before`／兄弟屬性。`handling-statuses.js` 已有 `shortLabel`。

### 定案（版面契約，取值暫定）

1. 面板分四區，區與區以 `border-top`＋`--lf-space-3` 分隔，不再用 `<hr>`：
   - **狀態摘要列**：目前狀態文字＋「N/M 已結案」＋「最後更新 時間」同一列；雙軌語意說明**維持常駐一行小字**但縮短成一句（不改成 help icon，沿用既有理由）。案件連動提示維持 `lf-hint`。
   - **人員**：主機負責人（唯讀）與處理人（選單＋指派鈕同列）兩列，標籤左、值右，窄時標籤在上。
   - **表單**：批次提示「已勾選 N 個問題…」**留在表單區頂端**（選 chip 時要看得到模式）；狀態 chips 改 CSS grid `repeat(auto-fill, minmax(8.5rem, 1fr))`（同時吃 7 顆與 8 顆、窄欄自動單欄），顯示 `shortLabel`、`title` 放全名；`renderChips` 改成只切換 `.active`、不重建節點（焦點不掉）；條件欄位緊接其下。
   - **說明欄**：label 單獨一列（label 文字會被 `updateFieldsForStatus` 改寫，不放工具）；textarea 拿掉 `mb-3`；**編輯器尾區**緊貼 textarea 下緣（同一外框、灰底 `--lf-gray-50`、上邊框省略）：左：字數＋常用語＋沿用上次，右：AI 整理；草稿提示在尾區內一行小字；錯誤訊息槽在尾區之後（見 2）。
   - **動作列**：`mt-3 pt-3 border-top`，主鈕「儲存」／「套用到 N 個問題」全寬 `btn-primary`。
2. `attachNoteEditor` 介面**小幅擴充**（不再宣稱 API 不變）：新增可選參數 `errorEl`，由編輯器放在尾區之後；不包裹 textarea（保住 `.is-invalid ~ .invalid-feedback` 的兄弟關係）。兩個呼叫端改成傳入自己的錯誤元素、拿掉 textarea 的 `mb-3`，DOM 順序統一為 [textarea, 尾區, 錯誤槽]。交辦回覆 modal 的主鈕由 modal footer 提供，動作列規則只適用面板。
3. 鍵盤與語意：label `for`、計數 `aria-live`、按鈕 `type="button"`；樣式走新 class（`.lf-handling`、`.lf-note-editor__tail`、`.lf-form-actions`，名稱暫定）。
4. 寬度契約以實測為準：1024 視窗、右欄展開 20rem（320px）與 `< lg` 堆疊都要放得下，長中文不截字、無橫向溢出。

### 改動

1. `note-editor.js`：尾區結構、`errorEl` 參數、提示位置；`handling-panel.js`、`issue-status-reply.js` 呼叫端各三行（傳 `errorEl`、拿掉 `mb-3`、不自行插入錯誤元素）。
2. `handling-panel.js`：四區結構、摘要列、chips grid 與 `renderChips` 不重建、動作列。
3. `site.css`：新 class；`DESIGN-SYSTEM.md` §7 補一行。
4. `mini-dom.mjs` 補 `after`／`before`／`nextElementSibling`／`previousElementSibling`／`insertAdjacentElement` 最小實作，讓結構斷言能跑；新增 `note-editor.behavior.mjs`（登錄 `InlineData`）。
5. 說明書 05 章新增「說明欄的三個輔助工具」小節（常用語、沿用上次的說明、AI 整理），操作段位置描述更新。

### 驗收

- `dotnet test --filter "FullyQualifiedName~NoteEditorUiTests|FullyQualifiedName~HandlingStatusCatalogUiTests|FullyQualifiedName~HandlingNoteAiTests|FullyQualifiedName~NoteReuseAndPhrasesTests|FullyQualifiedName~NodeRuntimeTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"` 全綠，含新增 behavior：尾區是 textarea 的 `nextElementSibling` 且含計數與「AI 整理」；傳入的 `errorEl` 是尾區的下一個兄弟；面板主鈕的前一個兄弟是動作列容器；chips 點擊後原節點仍在 DOM（`isSameNode`）且 `.active` 切換；7 顆與 8 顆兩種模式各一案。
- 突變：拿掉動作列容器、主鈕直接接在尾區後，結構案須紅；`renderChips` 改回 `replaceChildren`，「原節點仍在」須紅。
- 主代理探針（1024、1366、`< lg`）：面板容器 `scrollWidth <= clientWidth`；每顆 chip 的 `getBoundingClientRect().right` 不超出 chipGroup；寫進執行紀錄。手動項：Tab 順序 chips → 條件欄位 → 說明 → 常用語／沿用 → AI 整理 → 儲存；必填錯誤顯示在尾區之後並聚焦 textarea。

## 批次 F：健康頁金鑰檔位置提示（B4）

### 現況與核對結果

- 金鑰來源有三種（`CryptoHelper.cs:54` `KeySource`）：`env`（`LF_CRYPTO_KEY` 環境變數，根本沒有金鑰檔，`CryptoKeyBootstrapper.cs:118-124`）、`file`（`{DataRoot}\keys\lf-crypto.key`，DataRoot 留空時等於執行檔目錄，`AppSettings.cs:246`、`Program.cs:60`）、`embedded`（建檔失敗時退回內嵌金鑰，是最差狀態）。重新部署清掉執行目錄 → 金鑰檔遺失 → 所有已存憑證失效；`CryptoKeyBootstrapper.cs:150-155` 只在「金鑰檔遺失」分支寫啟動 log，畫面看不到；使用者每次升級後都要重輸 Sentinel／PRTG 憑證。
- `HealthDetailDto` 是扁平屬性袋（`HealthDtos.cs:35-80`），沒有檢查項目清單；`HealthService.cs:132-146` 的 `degraded` 會把整體狀態設成 degraded，`CryptoKeyMismatch` 以 `CryptoKeyBootstrapper` 的靜態旗標模式供讀（`:22-27`、`HealthService.cs:132`）；健康頁前端 `settings.js:924-930` 的 `cryptoStatus` 是文字列。
- F-1（PRTG treesize 提示一趟一行）**刪除**：第 64 輪 `5096b796` 已做（`PrtgTablePager.cs:289-300` 只 `Log.Debug`、`PrtgFetchService.cs:1134-1137` 全趟彙總一行）；使用者匯出裡一趟 11 則的 S009 措辭是舊版字串（現行碼搜不到），站台跑的是第 64 輪之前的 build，升級即消失。

### 定案

1. 判準先分來源：`env` → 正常（不警告）；`embedded` → 警告「金鑰檔無法建立，目前以內嵌金鑰運作，請檢查資料目錄權限」；`file` → 比對金鑰檔完整路徑是否位於 `AppContext.BaseDirectory` 之下（`Path.GetFullPath` 正規化、補結尾分隔符後比前綴、Windows 不分大小寫、其他平台區分；`C:\site` 不得命中 `C:\site-data`），在其下才警告「金鑰檔與部署目錄在一起，重新部署會清掉它並讓所有已存憑證失效；請把 DataRoot 設到部署目錄之外，或把 keys 資料夾排除在清除範圍（已排除者可忽略）」。
2. 旗標在 `CryptoKeyBootstrapper` 啟動時算好（沿用 `KeyMismatch` 靜態旗標模式，`RunWithTimeout` 已握有 `path`），`HealthService` 只讀；**不併入 `degraded`**（預設安裝就是這個情境，併入會讓所有預設站台升級後永遠 degraded、外部監控誤報），健康頁多一列文字＋圖示，`/api/health` 狀態不變。
3. 路徑判斷抽成可測的純函式（例如 `KeyFileLocation.IsUnder(path, baseDir, caseInsensitive)`）。

### 改動

1. `CryptoKeyBootstrapper`：來源分流與靜態旗標；純函式。
2. `HealthDetailDto`＋`HealthService`：新屬性（例如 `KeyFileLocationWarning: string?`）；`settings.js` 健康頁多一列。
3. 說明書 22 章與升級注意事項（預設安裝升級後會看到這一列）；BACKLOG 補一句「messages 的 treesize 語意未證實，真截斷與提醒不可分」（F-1 刪除後留痕）。

### 驗收

- `dotnet test --filter "FullyQualifiedName~HealthServiceTests|FullyQualifiedName~HealthTabUiTests|FullyQualifiedName~CryptoKeyBootstrapperTests"`（11／4／28 案）全綠，含新增：三種來源各一案；`file` 在部署目錄內→警告、在外→null、`C:\site` 對 `C:\site-data` →null、Windows 大小寫不同仍命中；`degraded` 不因此為 true；DTO 有新屬性。
- 突變：把前綴比對改成不補分隔符的 `StartsWith`，`site-data` 案須紅；把 `env` 分流拿掉，`env` 案須紅。

## 批次 G：文件與說明書

### 現況與核對結果

- P3（PRTG 與 AI 順序）核對結果：AI 跟隨取數由 `PrtgFindingsBecameReady` 觸發、`FindingsWaitCutoff` 完整性閘門擋住當日；`PrtgFindingsReady` 在逐日規則判定與 finding 補追加全部發佈後才送（`PrtgDailyPipeline.cs:789`）；訊號之後只剩觸發式數值取數與「接續補 PRTG 數值」，兩者只補數值與快照，不改當日 finding；prompt 的【PRTG 監控訊號】段讀的是已發佈 finding。**不需改程式**，只要說明書講清楚。
- 說明書 **24 組**章節（16 號有 `16-issue-owners` 與 `16-my-work` 兩份；使用者版＋AI 版共 48 個檔）。守門：`HelpContentServiceTests`（11）、`HelpWritingGuideTests`（3）、`DocNarrativeGuardTests`（3）、`HelpManualLayoutTests`（5）、`HelpNavigationConsistencyTests`（1）、`HelpQaServiceTests`（7）。05 章完全沒有常用語／沿用上次／AI 整理的說明；04 章「右側與下方的卡片」表漏了程式偵測訊號、且右欄描述與執行時版面不符；10 章第 83 行一格已過長。

### 定案

1. 全 24 章逐章稽核：以 View／JS 實際文案與 `layout.js` 導覽名稱為準，對照每章的按鈕名、欄位名、狀態名、步驟是否還存在；AI 版的門檻與流程對照程式；兩版不得矛盾。分三組由 scan subagent 唯讀列出「章節｜段落｜說明書寫法｜畫面實況｜建議」，主代理逐條裁決後改檔，稽核清單（條目數＝裁決數）歸檔到執行紀錄。
2. 本輪功能連動：04 風險日詳情（給主管的說明、長文字收合與已知取捨、報告每次收合、右欄側軌、舊日子沒有新段落是正常、偵測訊號與類型分布的位置）、05 處理狀態（面板四區位置＋三個輔助工具小節）、10 排程作業（空資料待複查、最後一趟結束時尚有、PRTG → AI 順序一段、表格格拆條列）、13 名詞解釋（給主管的說明、空資料待複查）、22 系統健康（金鑰檔位置）。
3. 現行文件：WEB-SPEC §9.3／§9.10／§8.6a 用詞表、DETECTION-SPEC AI 契約段、DB-SPEC `content_json` 說明、DESIGN-SYSTEM §6b／§7、BACKLOG（B1 條目補本次證據：23 案三次都回空陣列、重試已關文法仍如此；F-1 刪除留痕；B2 等補證據）。
4. `CLAUDE.md` 慣例段加一行：畫面相關工作一律套 `ui-ux-pro-max`、以 DESIGN-SYSTEM v2 為基準。

### 驗收

- `dotnet test --filter "FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~HelpWritingGuideTests|FullyQualifiedName~DocNarrativeGuardTests|FullyQualifiedName~HelpManualLayoutTests|FullyQualifiedName~HelpNavigationConsistencyTests|FullyQualifiedName~HelpQaServiceTests"` 全綠（不加 `--no-build`，說明書是資源檔）。
- 敘事字眼掃描（客觀指令）：`grep -nE "原本|後來|改為|取代|第 *[0-9]+ *輪|回饋|體檢輪" docs/*.md CLAUDE.md README.md` 對本輪改過的現行文件命中數為 0，或逐條說明屬允許清單（`DocNarrativeGuardTests` 同口徑）。
- 稽核清單：條目數＝裁決數（採納／不採納＋理由），寫進執行紀錄。

## 批次 H：收官

- 全量 `dotnet test` 一次（不帶 `--no-build`），結果（總數、全綠與否、與 8,515／11 的差）寫進「體檢交接」。
- 併回前終檢：兩輪獨立 diff 審查（程式碼：與規劃不符／同型遺漏／測試假通過；文件：契約逐條對照實作），跨段產出鏈回頭 grep（`Brief` 的回頭 grep 已在批次 C 做，終檢只複核；側軌的每個自動展開入口與五個 `#handling` 連結；`recheck` 的匯出、前端 meta、說明書）。

## 明確不做（本輪定案）

- **F-1 PRTG treesize 提示彙總**：第 64 輪 `5096b796` 已完成；使用者站台的 11 則 S009 來自升級前 build，升級到含第 64 輪的版本即消失。
- **B1 AI 回覆空陣列**：23 個主機日三次嘗試都回 `[]`，`AiJsonDiagnostics.Classify` 已把空陣列歸 `empty`、第二次起關掉文法並在提示尾句明講「不要輸出空陣列」仍如此；那批案例沒有詳細診斷（`detailedDiagnosticsFirstSeen = null`）。不盲改提示詞，維持 BACKLOG「AI 回覆格式異常的成因尚未處理」，等有詳細診斷的新案例再判。
- **B2 AI 趟標失敗但結束碼 0（S001／S004／S005）**：訊號內容不在使用者提供的片段內，待補證據後另案。
- **P3 改程式**：順序已正確，只補說明。
- **`brief` 進 JSON 重試／內容檢查**：選填欄位不得拖垮整日判讀（批次 C 定案 2）。
- **診斷匯出加 `BriefMaxChars`**、**抽「敘事欄位」值物件**：以同步守門測試取代。
- **右欄側軌狀態持久化**與**報告全文展開狀態持久化**：每次進頁回到預設（使用者要求）。
- **既有紀錄回補 `Brief`**：只能靠強制重新分析。
- **健康頁金鑰位置併入 degraded**：預設安裝即此情境，只做提示列。

## 三視角審查紀錄（2026-11-06，第二版依據）

三個獨立審查代理（維運使用者／Reddit 式尖銳使用者、管理者與營運、程式面與架構）共 65 條，主代理逐條對照程式碼核實後：採納 48 條（已併入各批次定案與驗收）、部分採納 9 條、不採納 8 條。不採納與部分採納的理由：

| 來源 | 主張 | 處置 |
|---|---|---|
| 使用者 R2 | 對 `canHandle` 使用者預設展開右欄 | 不採納：使用者明確要求預設收合；以軌上按鈕顯示狀態摘要與逾期字樣補足資訊 |
| 使用者 R3 | 偵測訊號要在軌上顯示或搬到頁首 | 部分：右欄執行時沒有偵測訊號卡（在「證據」details），本輪不動；04 章把位置寫清楚 |
| 使用者 R7 | keyDetails 保留 3 行 clamp | 部分：使用者指定 300 字；改為在分隔符處截斷、按鈕寫總字數、說明書列為已知取捨 |
| 使用者 R10 | 報告每次收合是否使用者要求 | 是使用者要求（回饋第 5 項），已註明 |
| 使用者 R15 | 目前狀態說明改 help icon | 不採納（與既有定案相反），改維持常駐並縮短 |
| 使用者 R22 | 加「複製」鈕 | 採納（沿用 `report-view.js` 的 clipboard 手法） |
| 管理者 R3 | 指紋「非空才納入」理由不成立 | 採納：指紋不持久化，改為一律納入（更簡單） |
| 管理者 R5／程式面 #3 | F-1 已於第 64 輪完成 | 採納：刪除 F-1 |
| 管理者 R15／程式面 #16 | 金鑰位置警告是否併入 degraded | 採納：不併入 |
| 程式面 #2 | 右欄執行時只有兩張卡，五按鈕側軌過度設計 | 採納：改成兩顆按鈕的側軌（仍需側軌，因為使用者要的是左欄變寬）|
| 程式面 #9 | 不抽值物件，用反射同步守門 | 採納 |
| 程式面 #19 | `dayTimingText` 維持純粹、摘要走第二參數 | 採納 |
| 使用者 R20 | 匯出口徑變更要註記 | 採納（說明字串） |
| 管理者 R18 | console 輸出與匯出限制 | console 採納、匯出 `BriefMaxChars` 明確不做 |

## 複檢（第二版）

- 與既有設計的衝突：WEB-SPEC §9.3 第 1 條「展開狀態記 localStorage」被批次 D 定案 3 推翻，批次 G 同步；§9.3 第 10 條歷程卡 `max-height`＋放大檢視與側軌並存不衝突；§9.10「尚未處理不是漏跑」在批次 B 擴寫（尚未處理＝沒排到；空資料待複查另計）；`readonlyField` 常駐說明的既有定案（FEEDBACK-5 §6）維持不推翻。
- 批次之間：A 與 D 都動 `site.css`，A 先；C 的前端段與 D 都動 `record-detail.js` 頂部摘要與問題列，順序 C → D；D 定右欄 20rem、E 在其內排版；B 與 F 不相干；E 的 `mini-dom` 擴充要在 D 之前還是之後？D 的 behavior 案例也可能用到兄弟屬性——**`mini-dom` 擴充提前到 D 的第一段**。
- 四個坑：什麼算一個（待複查以主機日計；長文字以容器 `textContent` 字元數計，分母為零即不顯示按鈕；「最後一趟結束時尚有」沿用 `RunResume` 單位）、破壞性判準（`Brief` 的後處理丟棄只影響選填欄位、其餘四欄照寫；歷史修訂清空與既有三欄同步；無洗欄位或刪列）、單向閘門（側軌手動優先旗標只活在頁面壽命內；hash 讀一次即清；指紋一律納入沒有閘門）、移除類（`lf-remeasure`／`.lf-issue-details--clamped` 唯一依賴方是 `collapsedRestSection` 與 `keyDetailsBlock`，一併移除並以 grep 零命中驗收；`attachNoteEditor` 介面是擴充不是移除；F-1 不做所以沒有移除）。
- 升級／回退：沒有 DDL；舊紀錄 `Brief` 為空、畫面不顯示；回退到第 65 輪 build 讀到含 `brief` 的 `content_json` 會忽略新鍵（專案沒設 `JsonUnmappedMemberHandling.Disallow`）；不需 `ExtractVersion`；每筆 `content_json` 約多 150 字；F-2 會讓預設安裝（DataRoot 留空）升級後在健康頁看到一列提示，寫進 22 章與升級注意；B 的匯出 `notRun` 口徑變更已在說明字串註記。
- 複檢完成，無新增待決事項。

## 體檢交接

- 實作：Claude Opus 5.5 主代理寫規格與驗收，各批次實作派 Sonnet 5.5；規劃與三視角審查：Claude Fable 5.1。
- 全量測試（Windows，Node 22.23.4）：**8,610 通過／0 失敗／11 略過**（7 分 35 秒），比本輪基線 8,515 多 95 案。第一次全量有 1 個側軌行為案例偶發紅（負載下 node 程序逾時，直接重跑與行為測試三輪皆綠），第二次全量未再出現。
- macOS 未在本輪實測；CI 會跑 macOS。
- 畫面驗證：內建瀏覽器面板隱藏時頁面不排版，版面寬度改用無頭 Edge 載入真實 CSS／模組量測；本機 dev 資料庫沒有分析紀錄，風險日詳情頁的完整互動（側軌展開後的處理面板、給主管的說明、長文字收合）沒有在真實資料上走過，需使用者實測。
- 實作方最沒把握處：(1) 側軌在真實資料頁面的展開／收合手感與 1024 寬下 236px 處理面板的實際可讀性；(2) `brief` 的品質取決於地端模型，150 字截斷與 IP／Event ID 丟棄會讓部分日子沒有這一段；(3) 健康頁金鑰位置提示在 IIS 實際部署路徑（含 symlink／UNC）下的判斷只以正規化路徑比對，未實測。
- 升級注意：無 DDL；舊紀錄沒有「給主管的說明」，要補用「強制重新分析」；預設安裝（DataRoot 留空）升級後健康頁會出現金鑰檔位置提示，屬預期；診斷匯出 `dailyHealth.notRun` 不再含空資料待複查（改看 `recheckCount`）；開發機前端行為測試需要 Node 22.7 以上。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 前置-環境 | 主代理 | Node v21.6.1 → v22.23.4（winget `OpenJS.NodeJS.22`，原地取代 MSI 安裝） | `node --version` | 第 65 輪新增下限 22.7，舊版會讓前端行為測試整批紅 |
| 前置-全量 | 主代理 | 建置 0 錯誤；8,514 通過／1 失敗／11 略過（7 分 34 秒） | 單獨重跑失敗案 5 次紅 1 次 | 偶發紅歸入批次 Z |
| Z | 主代理 | 情境 7 改等 `runState.WaitForCompletionAsync` 並斷言觸發成功 | 類別 55/55；情境 7 連跑 30/30；注入 300ms 延遲時新寫法仍綠且耗時 2→3 秒（等待確實涵蓋背景執行） | PLAN 的突變做不出來：在迴圈入口注入 100／104／110／120／300ms 延遲各跑 5 次，舊寫法都沒紅——競態窗口只有重標前後數毫秒，入口延遲到不了；改以「新寫法在慢背景執行下仍等得到」作為確定性證據 |
| A | 主代理 | sticky 三組規則改 `> table >` 子代鏈＋`:where()` 壓特異度、排除展開列；`.lf-row-detail > td` 補 `box-shadow: none`；`TableConsistencyUiTests` 新守門 | 定向 98/98；突變（基本規則改回後代選擇器）守門紅、還原綠；探針：巢狀列 hover 時展開格 `rgb(248,250,252)`／陰影 none、巢狀最後一欄 `static`；父列 hover 時第一欄陰影與最後一欄底色同為 `rgb(239,246,255)`；高亮列最後一欄 `rgb(239,246,255)` | 守門的選擇器切分要只切括號外的逗號（`:where(thead, tbody)`）並先去 CSS 註解；探針讀值要等兩個 animation frame，否則 hover 樣式尚未重算 |
| B | Sonnet 5.5 實作＋主代理驗收 | `NetiqStatus` EmptyOnce→`recheck`、`RecheckCount`／`PendingSummary`（只今天、排除異常中斷改寫）、待複查文案分齡、`renderDayTimingCell` 第二參數、`STATUS_META.recheck`、匯出兩欄＋說明字串；主代理修「未執行為 0 時不帶＋號」 | 定向 708/708（Debug 重建）；突變 m1（EmptyOnce 改回 none）紅 2、m2（非今天也算摘要）紅 1，還原綠 | 匯出沒有主機狀態值的封閉白名單，不必登錄 `recheck`；文件同步（WEB-SPEC §9.10、說明書 10／13 章）統一移到批次 G；代理遇到 Debug 檔案鎖是主代理的預覽站台，已停站台後改回 Debug 驗收 |
| C | Sonnet 5.5 實作＋主代理驗收 | `brief` 契約、`NormalizeBrief`（截句／IP 與 Event ID 丟棄）、六處寫入點含 `AttachAiResultLocked`、指紋一律納入、歷史修訂清空、報告與 console 印出、DTO（案件授與清空）、`aiSummaryBlock`／`briefBlock` 與複製鈕、`.lf-ai-brief` 樣式、反射式敘事同步守門 | 定向 776/776；突變 m1（拿掉 `record.Brief = attemptOutcome.Brief`）紅 2、m2（brief 進 validate）紅 2，還原綠 | 主代理把一條名稱與內容相反的測試改名；prompt 測試放新檔 `AiBriefTests.cs` 而非 `PromptHostKindTests`；同步守門用反射呼叫 record 的 init setter（執行期不擋，已註解） |
| D1 | Sonnet 5.5 實作＋主代理驗收 | `collapsibleText`（300 字、純文字在分隔符截斷＋「…」、區塊模式 max-height 漸層、列印全文、stopPropagation）套 8 處；移除 keyDetails 量測與 `lf-remeasure`、`.lf-issue-details--clamped`；報告卡不再記展開狀態；mini-dom 補兄弟節點 API | 前端定向＋新類別全綠；突變（`<= 300` 改 `< 300`）邊界案例紅、還原綠；grep `lf-remeasure`／`lf-issue-details--clamped` 0 命中 | 主代理修：原本每個元件各掛一組 window 列印監聽，清單重繪會累積——改成模組層單一監聽＋集合（節點離開文件即移除），並補「兩個元件只掛一組」案例 |
| G-說明書稽核 | 三個 Sonnet 5.5 唯讀稽核＋主代理裁決改檔 | 96 條（36＋32＋28）：與畫面不符、與邏輯不符、兩版矛盾、寫作規範、看不懂；另把 36 處分號串句拆開 | 說明書守門 38/38；manifest JSON 有效 | 採納 93 條；不採納 3 條：08 章 AI 版段落搬移（只補內容不搬結構）、18 章 AI 版完整動作代碼清單（改寫成「以畫面下拉為準」並補常用碼，避免與程式各維護一份）、02 章段落拆分後「已有結論」兩句留在 vs 基準段之後（語意仍屬重點問題卡，可接受）；畫面提示「未分組時只有 admin 看得到」與實際權限不符（ViewAll 角色都看得到），連同 Hosts.cshtml、hosts.js 三處與 alert-tools-table.js 一起修；04／05／22 章待 D2／E／F 後處理 |
| D2 | Sonnet 5.5 實作＋主代理驗收 | `core/side-rail.js`、詳情頁 grid 兩欄、兩顆側軌按鈕與摘要、`#handling` 讀一次即清、勾選自動展開＋手動優先、案件授與不建側軌、`recordDetailUrl` 與四處「去處理」連結 | 定向全綠（含 8 個側軌行為案例）；突變 m1（拿掉全選自動展開）紅、m2（拿掉手動優先）紅，還原綠；無頭 Edge 以真實 CSS 實測：1024 寬收合主欄 621／右欄 54、展開 439／236；1366 寬收合 963／54、展開 665／352；900 寬回到上下堆疊、側軌隱藏 | 主代理修三處：表頭全選未接自動展開（補上並以守門要求兩條路徑）；展開寬度原為 20rem，在字級「中」時 1024 寬主欄只剩 313px，改 `min(20rem, 34%)`；mini-dom 重複貼上的區塊移除。處理面板自行重載（指派後）時側軌摘要要等下一次 `load()` 才更新——可接受，記錄於此。內建瀏覽器面板隱藏時頁面不排版、讀到舊值，改用無頭 Edge 量測 |
| G-現行文件 | 主代理 | WEB-SPEC（固定動作欄、空資料待複查與今日待補、長文字收合、報告每次收合、AI 五段標註、右欄側軌與連結分類）、DETECTION-SPEC（brief 契約）、DB-SPEC、DESIGN-SYSTEM §7 三列、BACKLOG（B1 證據、treesize 提醒）、CLAUDE.md（畫面工作套 ui-ux-pro-max） | `DocNarrativeGuardTests` 3/3 | E、F 定案後再補處理面板與健康頁 |
| E | Sonnet 5.5 實作＋主代理驗收 | 處理面板四區（摘要、人員、表單、動作區）、`buildStatusChips` 只建一次節點、短標籤＋完整名稱、說明欄尾區與 `errorEl` 槽（兩個呼叫端統一順序）、移除 `<hr>` | 定向 656/656（含 7 個行為案例）；突變 m1（點擊重建 chips）3 案紅、m2（拿掉動作區 class）結構案紅，還原綠；無頭 Edge 載入真實模組實測：右欄 236px 與 352px、7／8 顆 chips 皆無溢出、無被截標籤 | 代理遇到的檔案鎖是主代理的預覽站台，主代理停站台後自行建置驗收；chips 最小格寬 7.5rem 在 236px 只能單欄（7～8 顆直排約 300px 高），主代理改 5.25rem 後 236px 兩欄、352px 三欄；「處理狀態」改為 `role=group` 加 `aria-labelledby`（chips 是一組按鈕，沒有可對應的 `for`） |
| G-04／05 章 | 主代理 | 04 章：給主管的說明、長文字收合、報告每次收合、右側直條與自動展開、卡片位置表；05 章：在風險日詳情標記處理狀態、說明欄三個輔助工具；兩章 AI 版同步；manifest 關鍵字 | 說明書守門全綠 | 22 章待 F |
| F | Sonnet 5.5 實作＋主代理驗收 | `EvaluateKeyLocationWarning`（env 不提示、embedded 提示、file 在部署目錄內提示；正規化＋補分隔符＋依平台大小寫）、啟動時算一次存靜態屬性、`HealthDetailDto.CryptoKeyLocationWarning`（不併入 degraded）、健康頁「密碼欄位加密」列接提示 | 定向 71/71；突變 m1（前綴不補分隔符）紅 2、m2（內嵌不提示）紅 1，還原綠 | 純函式與靜態屬性同名衝突，函式改名 `EvaluateKeyLocationWarning`；新測試類別放在獨立檔，主代理把檔名改成與類別一致（`HealthServiceKeyLocationWarningTests.cs`），避免和既有 `HealthServiceTests` 類別混淆；與其他動到金鑰靜態狀態的測試同屬停用平行的 `CryptoKeyState` 集合；22 章兩版同步（六張卡、確認靜音徽章、金鑰位置提示） |
| H-全量（第一次） | 主代理 | 8,593 通過／1 失敗／11 略過（8 分 9 秒），總數 8,605 | 失敗案為側軌行為 `expandWithoutFocusDoesNotFocus`：直接跑 5 次綠、所有行為測試（286 案）重跑 3 輪全綠 | 側軌模組完全同步、各案例為獨立 node 程序，判斷為全量負載下 node 程序逾時的偶發；終檢修正後的第二次全量若再出現就開詳細記錄追查 |
| H-文件終檢 | Sonnet 5.5 唯讀審查＋主代理核實 | 11 條（全低或資訊） | 採納 9 條：WEB-SPEC 未執行格寫法與彙總計數清單、長文字分隔符完整清單與無分隔符退路、給主管的說明顯示條件；04 章 AI 版「白話摘要三段」用語與「移到」沿革口氣、04 章補約 1000 像素；22 章使用者版拿掉 DataRoot 等實作名詞；13 章三列拆句 | 不採納 2 條：22 章 AI 版「寄件暫停」是既有內容，指健康頁「被暫停的郵件收件人」那一列；WEB-SPEC 實測寬度只在 DESIGN-SYSTEM 記錄，沒有矛盾 |
| H-程式終檢 | Sonnet 5.5 唯讀審查＋主代理核實 | 13 條（中 6、低 7） | 交終檢修正代理 10 項（見下一列）；只記錄 3 項：交辦單 PRTG 佐證連結（`WorkOrderQueryService`）是查看用途、刻意不帶 `#handling`；PRTG 趨勢證據是 8 列固定短欄位，刻意不收合；「未分組只有 admin 看得到」等畫面提示修正來自說明書稽核（ViewAll 角色 Dev／Manager／Admin 實際看得到全部啟用主機，`VisibilityService` 已核對） | — |
| H-終檢修正 | Sonnet 5.5 實作＋主代理驗收 | 10 項：brief 的 IP 過濾改前後非數字斷言（.NET 的 `` 把中文當字元，緊貼中文的 IP 原本會漏）；處理面板經 `onRefreshed` 回呼更新側軌摘要（含面板內部重載）；hash 清除不看側軌在不在；列印登記集合在登記時清掉已卸下節點；灰底區塊的收合漸層改漸變到 gray-50；刪除沒有消費端的 `markUserChoice`、`COLLAPSE_LIMIT` 取消 export；`reportCard` 註解；側軌摘要改用 `--lf-text-muted`；報告卡與頁面層側軌接線改成行為測試 | 定向 663/663；代理突變 2 條、主代理再做 2 條（IP 正則改回 `` 紅 2；案件授與早退放回 hash 處理之前紅），皆還原綠 | — |
| H-全量（第二次） | 主代理 | **8,610 通過／0 失敗／11 略過**（7 分 35 秒，總數 8,621） | 以 TRX 記錄執行；第一次的側軌偶發未再出現 | CLAUDE.md 基線更新為 8,610 |
| 規劃-審查 | 三個 Sonnet 5.5 審查代理＋主代理核實 | 65 條，採納 48／部分 9／不採納 8 | 高嚴重度主張逐條以程式碼核實（落庫賦值點、F-1 已完成、右欄兩張卡、KeySource 三種、validate 單一布林） | 見「三視角審查紀錄」 |
