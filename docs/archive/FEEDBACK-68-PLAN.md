# LogForesight 第 68 輪規劃：風險日詳情可讀性、詢問 AI 改版、解析與排程訊號修正

> 狀態：全案完成已併 dev（實作與自查 Opus 5.5；換模型體檢 Fable 5.2）
> 基準：dev@f6426de7（Windows 8,748 綠／11 略過；macOS 8,512 綠／14 略過；全量約 8 分鐘）；完成時 Windows 8,861 綠／11 略過
> 來源：使用者回饋十項（風險日詳情畫面七項、PRTG 快照補抓一項、排程匯出診斷兩項）＋ 2026-11-09 排程作業頁匯出檔與兩份探測輸出＋五張截圖
> 實作方式：主代理寫階段規格，實作派 subagent（簡單小段 Haiku 5.5、其餘 Sonnet 5.5，effort 由主代理逐段評估；不用 agy）；同一 checkout 一次只派一個改檔代理；每段主代理獨立重跑驗收，不採信代理摘要；收尾換模型體檢
> 畫面相關段落一律套 `ui-ux-pro-max`（專案規則）；規劃時已載入，設計資料庫對「長文可讀性」無直接命中，B 批版面依 WCAG 2.2 AA、CJK 排版通則與 `docs/DESIGN-SYSTEM.md` v2 既有 token 定案
> 設計原則：每段只做根因需要的最小改動（KISS）；同一條規則只留一個實作點（DRY）；不為本輪新增設定、介面或抽象層，除非有第二個消費端（不過度設計）

## 背景（核對結論摘要）

1. **畫面可讀性**（回饋 #1、#2、#3、#6、#7、#4 的按鈕字樣）：版面與文案問題，根因明確。
2. **解析 bug**（#4 本體）：Sentinel 送來的 Windows 事件訊息主體是單行（欄位之間是多個空白），帳號／來源擷取「取到行尾」就把整段訊息當成值；垃圾值已寫進 `KeyAccounts`，下游的殘留憑證判定與密碼噴灑偵測都吃它。
3. **排程訊號**（#8、#9、#10）：站台約 11-05 升到含第 63-2～64 輪修正的版本後，夜間趟次只剩兩個警告來源（AI 首次嘗試回 `[]`、週檢掛不到停用的本機主機），其餘是環境事件且已正確呈現。比對匯出逐晚變化另找到一個疑似 bug（過去日的主機對應沿用）。快照服務停機期間的缺口目前不會自動補，移到下一輪。

### 核對表

| # | 項目 | 判定 | 證據 | 補充 |
|---|---|---|---|---|
| 1 | 「給主管的說明」改名「重點速報」 | ✅ 改名 | 徽章與 aria-label 在 `record-detail.js` `briefBlock`；報告 txt 前綴在 `RiskReportService.cs:361`（只寫出、沒有任何程式解析這個前綴）；說明書 04／13 與 `manifest.json` keywords；`WEB-SPEC`、`DESIGN-SYSTEM`、`DETECTION-SPEC`、`DB-SPEC`、`BACKLOG`；測試 `AiBriefTests.cs`、`AiStateFrontendTests.cs` | prompt 的欄位名 `brief` 與「照念給主管聽」的寫法指引不改——它定義內容語氣 |
| 2 | AI 摘要難讀 | ✅ 版面 | `aiSummaryBlock`：卡片滿版（截圖 2,100px），三段各一個 `<p>`、標籤與內文同行、每行超過 100 個全形字 | 讀者要在五秒內知道「什麼事、多嚴重、要做什麼」 |
| 3 | 收合鈕併進處理狀態 header | ✅ | `side-rail.js` 把「收合 ›」插在右欄 body 最前面，不屬於任何卡片；側軌只有這一頁使用 | 行為測試 `record-detail-rail.behavior.mjs` |
| 4a | 登入失敗明細與相關帳號是整段訊息 | ✅ **bug** | `LogAggregator.cs` `ExtractLastValue`（:636）與 `ExtractAccountsAndIps`：`Split('\n')` 後 `line.IndexOf(label)` 取**該行第一個**出現處到行尾。Sentinel 訊息主體是單行（`SentinelEventMapperTests` 樣本：「帳戶已順利登入。    主體：安全性識別碼：S-1-5-18   帳戶名稱：svc-test.corp」——欄位間多個空白、冒號半形全形都有、標籤後可能沒有空白），只有 `SentinelEventMapper` 為 4625／4771 追加的 `\nWorkstation Name:`／`\nSource Network Address:` 兩行有換行 | 單行訊息裡同一個標籤出現兩次（主旨的「-」與登入失敗帳戶），只看第一個也會錯——修法必須掃**每一個**出現處。截圖的「類型 —」「原因不明」是同一個原因。`PasswordSprayDetector` 以相異帳號數判噴灑，垃圾值讓每則訊息都像不同帳號 |
| 4b | 子項收合按鈕字太長 | ✅ | `collapsibleText` 字樣「顯示全文（共 N 字）」；來源佐證是一整句＋「檢視來源觀測（N）」 | `COLLAPSE_LIMIT` 300 全站共用；說明書 04 與 manifest 有「顯示全文」字樣 |
| 5 | 詢問 AI 免選問題 | ✅ 可做 | `/api/ai/chat` 必填 `issueKey`；context＝單一問題區塊＋現場事件（首輪）＋**當日報告全文（8k tokens 預算）**＋對話尾；`GetDetail` 在案件授與檢視時已把 headline／brief／summary 等清空，`GetReport` 回 null | 報告全文本來就在 prompt 裡，模型已經看得到當天所有問題的細節——這是 D 批不做 AI 路由的關鍵事實 |
| 6 | 脈絡移到證據上方 | ✅ 只動 View | `RecordDetail.cshtml` 兩個 `<details>`；`initRecordSections` 不依賴順序 | 說明書 04 表格順序同步 |
| 7 | 重點問題規則下放中文說明 | ✅ | 問題查詢：標題 → 白話說明 → `knownIssue`。詳情頁白話說明已經在 `issue.guidance.explanation`（`BuildGuidance` 取規則的 `PlainExplanation`），只是放在展開列 | 不需要新 DTO 欄位 |
| 8 | 快照停下後會不會補 | ❌ 不會 | 快照是即時值；`NoteResumed` 只印一行 Info；保守策略夜間不跑 `historicdata` | 移到第 69 輪（設計見文末） |
| 9a | AI 回覆 `[]`（S001／S004／S005） | ⚠️ 半解 | 169 次解析失敗、17 件降級案例回覆全是 `[\n]`。`AIService` 的 JSON 模式送 `response_format: {type: json_object}`，這個伺服器的文法允許最上層是陣列，`[]` 成了「想先思考」的逃生口。關文法重試上線後 11-02 起沒有最終失敗，但 11-05～11-09 每晚 12 個主機日中仍有 1～7 個首次嘗試失敗。`comparison` 顯示案例與對照組輸入特徵幾乎相同（inputChars 555 vs 598）＝不是輸入造成 | 成功前有失敗嘗試的呼叫不寫診斷行，匯出 `diagnostics.entries` 恆為 0，沒有類別、finishReason、思考字數可看 |
| 9b | S016 週檢找不到 11-08 紀錄 | ✅ bug | 週檢以本機主機為對象；本機主機已停用（`local active 0`）沒有紀錄，`AttachWeeklyCheckup` 就 Warn；`ShouldRun` 只看距上次天數、永遠為真，每趟重跑體檢 15～31 秒再警告 | NetIQ 路徑沒有週檢——本機停用的站台，週檢功能實際上沒有作用（見〈明確不做〉） |
| 9c | AI 降級趟次被算 failed、aiFailures 為 0 | ✅ 已修 | dev：`ChatJsonAsync` 最終失敗已是 Warn（`AIService.cs:549`）、`AiAnalysisHostedService.cs:344` 已把降級計入 `AiFailures` | 匯出的 Error 都在 11-02 前 |
| 9d | 解不開的「PRTG 密碼」 | ⚠️ 呈現 | `SecretInventory` 已標 `Retired`，匯出仍列進 `undecryptableSecrets` 並寫成「需要重新設定」 | — |
| 9e | S008 Security Event Log 讀取失敗 | ⚠️ 文案 | `EventLogService.cs:224,333` 不分例外型別同一句；實際是 `SecurityException` | — |
| 9f | S006／S009、S007／S010 | ✅ 已修 | 5096b796（11-05）、8055662d（11-04） | — |
| 9g | 11-02、11-08 執行日 prtgFindings 為 0 | ❓ 交使用者查 | 鄰日 2～3；分析的都是週六 | 見〈站台側待辦〉 |
| 9h | 10-22～10-26 連續五晚重評仍 `no_output`「沒有任何主機對應」 | ⚠️ **疑似 bug（E-0，先重現）** | 10-27 起已有成功對應，10-26 在 10-28 的趟次還有 finding；但 10-29～11-02 每晚重評窗（7 天）內這幾天都是 no_output。程式：過去日走 `GetLatestHostMapWithDate(anchor: d)` → `FindLatestHostMapDate`：回看窗內有任何對應列就回那天，否則回 d 之後**最早**一份——兩段都不看那份對應有沒有 `ok` 列。只有「最新日」那條路（`ResolveHostMapRows`）有檢查 `Any(Ok)` | 兩種可能都會造成這個現象：(a) 那幾天自己有一份全 unmatched 的對應；(b) 之後最早那份（10-27，被 10-28 16:27 的手動趟次重寫過）沒有 ok 列。站台資料才能確認是哪一種（查詢見〈站台側待辦〉 4） |
| 9i | `runMemory` 只有 1 趟 | ℹ️ 第 67 輪待辦未完成 | 站台 11-09 才升到含記憶體標記的版本 | 本輪不動 |
| 9j | 11-05、11-06 執行日約 1,850～1,990 秒 | ✅ 預期 | `prtg-value-fill` 兩趟各約 1,747 秒（手動勾「一併補齊」）；`historicdata` 每分鐘 5 次配額 | — |
| 9k | `failedOrStuckWithoutErrorRuns = 4`（run 21～24） | ⚠️ 呈現（E-5） | 憑證解不開 → NetIQ 12 主機日全失敗 → `result.Success=false` → 結束代碼 1 → failed，但整趟沒有任何 Error 行（`CredentialPreflight` 刻意只寫 Warn，註解：失敗由該路自己表達——而 NetIQ 那一路只印了 ⚠）。排程頁「異常彙總」預設只看 Error | 通則：結束代碼非 0 的趟次應至少有一行 Error 說明原因 |
| 9l | `dailyHealth.prtgTargetSensors` 全為 0 | ℹ️ 說明不足 | 保守策略夜間不跑逐顆取數，0 是真的 0；匯出 guide 沒交代 | 只補 guide 一句，不改資料 |
| 9m | 日誌來源副標（截圖 1） | ⚠️ 文案（B-2） | `record-detail.js:471`：`日誌來源：${detail.logSource}；最新 NetIQ 嘗試：${status}。此日可接受可信 PRTG 補充。`——列舉值與內部語意直接上畫面 | — |
| 10 | 探測輸出 | ✅ 正常 | 全部 ✓ | 三件站台操作列入〈站台側待辦〉 |

## 四視角複審紀錄（第二版改寫的依據）

以下是複審抓到、已回寫本文件的修正。每條都回程式碼核對過。

**使用者視角（以挑剔但講理的使用者為準）**

- 「白話說明搬到標題下，展開列又寫一次『說明』」——重複。→ A-4：展開列不再重複「說明」，只留影響／原因／步驟。
- 「重點速報與建議處置兩塊都上底色，哪個才是重點」——兩個強調等於沒有強調。→ B-1：只有重點速報有底色；建議處置用左色條與主色眉標。
- 「按鈕改成『展開…』之後我不知道會展開多長」——字數移到 `title` 與 `aria-label`，不消失。
- 「舊報告裡還寫『給主管的說明』，搜尋說明書舊名找不到」——→ A-1：詞彙表註明舊稱、manifest keywords 兩個名字都留。
- 「問一句話要等兩次 AI」——原規劃首輪多一次 AI 路由呼叫，地端模型一次就是幾十秒。→ D 批拿掉 AI 路由（理由見 D 定案）。
- 「AI 判斷錯焦點我沒辦法改」——→ D-3：焦點 chips 可移除、可自己指定。
- 「修好解析之後，舊日子的明細還是一坨」——歷史紀錄不回溯；→ C-2 的表格護欄讓舊資料至少會收合。升級注意事項寫明。

**管理者視角**

- 升級後行為會變的地方要事先知道：登入失敗類判定變準（風險日數可能變動）、報告前綴改名、過去日的 PRTG 歸戶可能補出問題。→ 新增〈升級注意事項〉。
- 「趟次失敗卻沒有 Error」是通則問題，不該只補憑證那一路。→ E-5 改成收尾點的單一不變式。
- 週檢在本機停用的站台形同沒有——不在本輪修，但必須讓管理者知道。→〈明確不做〉留痕並提醒進 BACKLOG。

**程式面視角**

- C-1 原寫「先切片段再找標籤」會把「帳戶名稱:  -」切成兩段、值永遠是空；且單行訊息同標籤多次出現必須逐一掃描。→ 契約重寫為「標籤後跳過空白、讀到邊界」。
- E-0 原想改 `FindLatestHostMapDate`，它被十多個「最近一次對應」呼叫端共用（維護頁對應清單要看得到 unmatched）。→ 改為新增「某日用哪一份」的單一查詢出口，只有五個 §4 呼叫端走它；逐 device 的 `PrtgHostMapLookup.Select` 不動（單一 device 的 conflict／unmatched 是真實狀態，不是「對應失敗」）。
- E-7 原想把 `CredentialPreflight` 改 Error，與它註解記載的定案衝突。→ 併入 E-5 的不變式。
- A-4 原想加 `IssueDto.PlainExplanation`，資料已在 `guidance.explanation`。→ 不加欄位。
- E-6 原想讓匯出依策略寫 null，需要多帶一個判斷來源。→ 只補 guide 文字。
- 「`[]` 連續三次關文法起手」：失敗是零散分布（每晚 1～7／12），連續三次的條件很少成立、成立時多半是巧合，卻要引入跨分片共用狀態；而真正的根因（`json_object` 文法允許陣列）的修法（改送物件根的 schema）要對真實伺服器實測才知道支不支援。→ 本輪只做診斷補強（E-2），兩個候選修法寫進 BACKLOG 並訂下判準。這條推翻第一版定案，理由如上。

**整體專案視角**

- D 的整日摘要必須取自 `GetDetail` 回傳的 DTO（案件授與檢視已清空），不能直接讀紀錄，否則繞過授權。
- C-2 改的是全站共用字樣，說明書與 manifest 的「顯示全文」要同步。
- E-5 的不變式要排除使用者停止的趟次，否則「已停止」會多出一筆錯誤。
- 本輪沒有 schema、設定、API 路由的新增（D 只在既有端點加欄位），`ControllerRouteSnapshotTests` 不應變動。

### 本輪規模

A 4 段、C 2 段、E 6 段、B 3 段、D 4 段，共 19 段，每段可獨立驗收與回滾。建議順序 A → C → E → B → D；A～B 完成可先併 dev 實測，D 接著做。快照缺口回補（F）移到第 69 輪。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A 文字與版面 | A-1 改名；A-2 收合鈕進 header；A-3 脈絡上移；A-4 白話說明上提 | 小 | 無 |
| C 帳號解析修正 | C-1 擷取邊界；C-2 收合字樣、來源佐證一行、明細表護欄 | 中 | 無 |
| E 排程訊號修正 | E-0 過去日對應沿用看 `ok`；E-1 週檢略過停用本機；E-2 recovered 診斷；E-3 匯出：Retired 密文分列＋guide 補述；E-4 Security 記錄檔權限訊息；E-5 失敗趟次必有 Error | 中 | 無 |
| B AI 摘要重排 | B-1 版面；B-2 副標白話化；B-3 文件 | 中 | A-1 |
| D 詢問 AI 改版 | D-1 context：整日摘要＋多焦點；D-2 焦點判定（確定性）與端點；D-3 前端焦點列；D-4 文件 | 中大 | 無 |

每段的「驗收標準」都是主代理能自己執行並判定的：單元／行為測試、grep 守門、無頭瀏覽器量測。需要真實站台才能確認的事一律放〈站台側待辦〉，不當驗收。

## 批次 A：文字與版面

### A-1 「給主管的說明」改名「重點速報」

- **定案**：畫面（徽章、複製鈕 aria-label「複製重點速報」）、新產生的報告 txt 前綴、說明書、規格文件一起改；舊報告不回溯；prompt 欄位名與寫法指引不改。詞彙表該詞條標題改「重點速報」並加一句「舊報告裡叫『給主管的說明』」；`manifest.json` keywords 兩個名字都保留（舊名仍搜得到）。
- **改動**：`record-detail.js` `briefBlock`；`RiskReportService.cs:361`；`04-record-detail.md`／`.ai.md`、`13-glossary.md`／`.ai.md`、`manifest.json`；`WEB-SPEC.md`、`DESIGN-SYSTEM.md`、`DETECTION-SPEC.md`（畫面名稱處）、`DB-SPEC.md` 註解、`BACKLOG.md` 兩條；`AiBriefTests.cs`、`AiStateFrontendTests.cs` 字串。
- **驗收標準**
  1. 行為測試：`briefBlock` 產生的徽章文字為「重點速報」、複製鈕 `aria-label` 為「複製重點速報」。
  2. `RiskReportService` 測試：有 Brief 的紀錄產出的報告含「重點速報：」、不含「給主管的說明：」。
  3. grep 守門：`LogForesight.Web/wwwroot`、`LogForesight.Web/Views` 內「給主管的說明」零命中；全專案（排除 `docs/archive`、`bin`、`obj`）只允許出現在 `AnalysisPromptBuilder`、`AiAnalysisResult`／`DailyAnalysisRecord`／`LogAnalysisService` 的程式註解、`DETECTION-SPEC` 對 prompt 的描述、詞彙表的「舊稱」句與 manifest keyword。
  4. `HelpContentServiceTests` 與說明書結構校驗綠；以「給主管的說明」與「重點速報」搜尋說明書都能命中 04 章（既有搜尋測試同型補一條）。

### A-2 收合鈕進「處理狀態」header

- **定案**：側軌模組新增一個選項指定收合鈕掛載元素，預設為第一個 item 目標卡的 `.lf-card__header`；按鈕靠右；< lg 與列印維持隱藏；收合後焦點回到最後觸發的側軌按鈕（不變）；案件授與檢視沒有側軌也就沒有按鈕（不變）。
- **改動**：`side-rail.js`、`record-detail.js`（呼叫點不必傳參，走預設）、`site.css`（`.lf-side-rail__collapse` 在 header 內的對齊）、`record-detail-rail.behavior.mjs`、說明書 04「點上方的『收合 ›』」改為「點『處理狀態』卡右上的『收合 ›』」。
- **驗收標準**
  1. 行為測試：建立側軌後，收合鈕的父元素是處理狀態卡的 `.lf-card__header`，右欄 body 的第一個子元素不再是按鈕。
  2. 行為測試：展開 → 點收合 → layout 沒有 open class、`document.activeElement` 是先前觸發的側軌按鈕（既有案例維持綠）。
  3. `site.css` 既有規則保證 < lg 與 `@media print` 下 `.lf-side-rail__collapse` 為 `display:none`（UiTests 以字面比對這兩條規則仍存在）。

### A-3 「脈絡」移到「證據」上方

- **改動**：`RecordDetail.cshtml` 對調兩個 `<details>`；說明書 04／`.ai.md` 表格兩列對調。
- **驗收標準**
  1. View 測試（字面比對）：`id="record-context"` 出現位置在 `id="record-evidence"` 之前。
  2. 既有 `initRecordSections` 相關行為測試維持綠（搬移目的地 id 不變）。
  3. 說明書結構校驗綠。

### A-4 白話說明上提到標題下

- **定案**：`issueCell` 順序改為：標題 → 白話說明（`issue.guidance?.explanation`，完整顯示）→ `knownIssue`（小字灰）→ logName → 徽章 → meta → 來源佐證 → 其餘不變。兩段文字相同時只顯示一次。展開列「處置參考」**不再顯示「說明」段**（已在標題下），保留影響／可能原因／處置步驟；只有說明、沒有其他三項的規則，展開列不出現。不新增 DTO 欄位。沒有白話說明時不補退路句。
- **改動**：`record-detail.js` `issueCell`、`guidancePanel`；`WEB-SPEC.md` §9.3 問題列描述；說明書 04 若描述「展開看說明」則同步。
- **驗收標準**（行為測試，新檔 `record-detail-issue-cell.behavior.mjs`，C# `InlineData` 登錄）
  1. 有 `guidance.explanation` 與 `knownIssue`：標題之後的第一個兄弟節點是白話說明、第二個是 `knownIssue`，兩者都在 logName 之前。
  2. 兩段文字相同：只出現一次。
  3. 沒有 `guidance`：標題後直接是 `knownIssue`（若有）或 logName，沒有空節點。
  4. `guidancePanel`：只有 `explanation` 的規則回 null；有 `impact` 的規則回傳的面板不含「說明」標籤。
  5. 文字以 `textContent` 寫入（`FrontendConsistencyUiTests` 綠）。

### 批次 A 定向驗收

`dotnet test --filter "FullyQualifiedName~RecordDetailRailBehaviorTests|FullyQualifiedName~AiBrief|FullyQualifiedName~AiState|FullyQualifiedName~RiskReportService|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~HelpManual|FullyQualifiedName~CaseGrant|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`（`--list-tests` 已核對命中：15／71／—／11／6／13／303／1／314／28；`RiskReportService` 與新行為測試類寫完後回頭確認被篩到）。

## 批次 C：帳號解析修正

### C-1 擷取邊界

- **根因**：取值規則是「到行尾」且只看每行第一個標籤出現處；Sentinel 訊息主體沒有換行。
- **定案（值的取法，三個擷取路徑共用一個 helper）**
  1. 在整則訊息中找出標籤的**每一個**出現處（不分大小寫；既有標籤表不變）。
  2. 每個出現處：跳過標籤後的空白與 tab（不跨換行），值從第一個非空白字元開始，讀到下列任一邊界為止：換行、tab、**連續兩個以上空白**、訊息結尾。值內的單一空白保留（RDP 的 `User: DOMAIN\user name`、英文句子）。
  3. 讀到的值若本身以冒號（`:` 或 `：`）結尾，視為空值（代表原欄位是空的、讀到了下一個標籤）。
  4. 值超過 **128 字（暫定）** 視為解析失敗、當成空值——任何情況下垃圾長字串都不得再進 `KeyAccounts` 或明細。
  5. 「-」視為空、電腦帳號（`$` 結尾）規則、`ExtractLastValue` 取最後一個非空值、`excludeSubstrings`（子狀態排除）等既有語意不變；排除判斷的範圍由「整行」改為「該出現處的標籤本身」。
- **不回溯**：歷史紀錄已存的垃圾值不重算。
- **改動**：`LogAggregator.cs`（新 helper；`ExtractLastValue`、`ExtractAccountsAndIps` 改用）；`DETECTION-SPEC.md` 帳號／IP 擷取一節補邊界規則。
- **驗收標準**（`LogAggregatorExtractionTests`、`PasswordSprayDetectorTests`）
  1. Sentinel 單行 zh-TW 4625（以匯出樣本改寫、含主旨「帳戶名稱:  -」與登入失敗帳戶「帳戶名稱:  user-002」兩處，後接 `\nWorkstation Name: WS01\nSource Network Address: 10.0.0.5`）：`Account == "user-002"`、`LogonType == 3`、`ReasonCode` 對應子狀態 `0xC000006A`、`Source == "WS01"`。
  2. 同上但沒有追加的兩行、訊息內有「工作站名稱:  WS01   來源網路位址:  10.0.0.5」：`Source == "WS01"`。
  3. 全形冒號且標籤後無空白（「帳戶名稱：svc-test.corp」）：取到 `svc-test.corp`。
  4. Sentinel 單行英文 4625、單行 4771（`Client Address: ::ffff:10.1.1.1`）各一案。
  5. 空欄位後直接接下一個標籤（「帳戶名稱:    帳戶網域:  CORP」）：帳號為空，不是「帳戶網域:」。
  6. 值超過 128 字的人工樣本：回空值。
  7. `ExtractAccountsAndIps` 對案例 1：帳號集合恰為 `{user-002}`（不含「-」、不含長字串）。
  8. 既有全部案例（換行＋tab 的本機格式、RDP `User:`）維持原結果——既有 18 條不得修改斷言。
  9. `PasswordSprayDetector`：同一來源、同一帳號的 20 則 Sentinel 單行訊息 → 相異帳號數為 1、不判噴灑。
  10. 突變（主代理執行）：把「連續兩個以上空白」改成「一個空白」→ 案例 8 中含空白值的既有案例或 RDP 案例紅；拿掉「掃每一個出現處」→ 案例 1 紅；拿掉 128 字上限 → 案例 6 紅。

### C-2 收合字樣、來源佐證一行、明細表護欄

- **定案**
  1. `collapsibleText` 切換鈕文字一律「展開…」／「收合」；`title` 與 `aria-label` 為「展開全文（共 N 字）」／「收合」。全站生效。
  2. 來源佐證改為一行小字「來源觀測 N 筆（M 筆有 UTC 時間、K 筆有原生參照）」，同一行尾接切換鈕「展開」／「收合」（`aria-label`「展開來源觀測」／「收合來源觀測」，`aria-expanded` 維持）；「群組顯示時段不是來源時間窗」移到展開後明細的第一行；清單截斷時在摘要行尾加「，清單已截斷」。沒有觀測時一行「來源觀測：未保存可核對的來源觀測」，無切換鈕。
  3. 登入失敗明細表「帳號」「來源」「原因」格內文字超過 **80 字（暫定）** 時套 `collapsibleText`（修好解析後正常值不會觸發；這是給歷史資料的護欄）。
- **改動**：`core/ui.js`；`record-detail.js` `sourceEvidenceBlock`、`loginFailureDetailsTable`；`collapsible-text.behavior.mjs`；說明書 04／`.ai.md` 與 `manifest.json` 的「顯示全文」「檢視來源觀測」字樣；`WEB-SPEC.md` §9.3。
- **驗收標準**
  1. `collapsible-text.behavior.mjs`：超過上限時按鈕文字為「展開…」、`aria-label` 含「共 N 字」且 N 等於實際字數；點擊後文字為「收合」、`aria-expanded="true"`；既有截斷位置案例（分隔符、代理對、列印還原）維持綠。
  2. 行為測試：`sourceEvidenceBlock` 3 筆觀測 → 摘要文字含「來源觀測 3 筆」、切換鈕文字「展開」、`aria-label`「展開來源觀測」；點擊後明細第一行含「群組顯示時段不是來源時間窗」；0 筆 → 無按鈕。
  3. 行為測試：明細表某格 200 字 → 該格內有切換鈕；30 字 → 沒有。
  4. grep 守門：`wwwroot/js` 與 `HelpContent` 內「顯示全文（」「檢視來源觀測」零命中（manifest keyword 以新字樣取代）。

### 批次 C 定向驗收

`--filter "FullyQualifiedName~LogAggregatorExtractionTests|FullyQualifiedName~LoginFailureAccountDisplayRulesTests|FullyQualifiedName~PasswordSprayDetectorTests|FullyQualifiedName~ResidualCredential|FullyQualifiedName~CollapsibleTextBehaviorTests|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`（命中 18／3／12／49／13／11／303／1／314／28）。

## 批次 E：排程訊號修正

### E-0 過去日的主機對應沿用要看 `ok`（先重現再修）

- **根因假設**：見核對表 9h。「一份對應完全沒有 `ok` 列」代表那次對應沒成功，不是可用的對應；最新日那條路已經這樣處理，過去日沒有。
- **定案**
  1. `EfPrtgStore` 新增單一查詢出口表達 PRTG-SPEC §4「某日用哪一份對應」：回看窗（31 天）內**最晚一份含至少一列 `ok`** 的對應；沒有則 day 之後、今天（含）之前**最早一份含 `ok`** 的對應；都沒有回空。
  2. 走這個出口的呼叫端（五個，都是 §4 語意）：`PrtgValueAssessment.ResolveHostMapRows`（過去日，以及最新日借前一份的那一步）、`PrtgBackfillRunner`、`PrtgFullBackfillPlan`、`PrtgTriggeredValueFetcher`、`PrtgBackfillService` 的逐日裝置清單。
  3. **不動**：`FindLatestHostMapDate` 與所有「最近一次對應」呼叫端（維護頁對應清單、衝突查詢、取數範圍、快照目標、探測）——它們要看到最新一份的真實狀態，含 unmatched。`PrtgHostMapLookup.Select`（逐 device）不動：單一 device 的 conflict／unmatched 是該 device 的真實狀態。`CalibrationService` 不動。
  4. 逐日說明維持「沿用 YYYY-MM-DD 的主機對應」；`MapAvailable` 的判定不變。
- **改動**：`EfPrtgStore.cs`（新方法）；上列五個呼叫端；`PRTG-SPEC.md` §4 補「含 `ok` 列的」；`PrtgHostMapLookup.cs` 註解更新「兩處規則」的描述。
- **驗收標準**（檔案型 SQLite；新測試類 `PrtgHostMapForDayTests`，另在 `PrtgDailyPipeline` 既有測試類補管線層案例）
  1. D 當天只有 unmatched 列、D+1 有 ok 列 → 回 D+1（修正前回 D——這條在修改前先寫並確認為紅，證明重現）。
  2. D 沒有任何列、D+1 全 unmatched、D+2 有 ok → 回 D+2（修正前回 D+1）。
  3. D 有 ok → 回 D；D-3 有 ok、D 只有 unmatched → 回 D-3。
  4. 窗內與之後都沒有含 ok 的對應 → 回空。
  5. 之後的對應日期晚於今天 → 不採用。
  6. 管線層：重評一個符合案例 1 的日子 → 該日 `MapAvailable` 為真、outcome 不是 `no_output`、逐日說明含「沿用」。
  7. 不變性：`GetLatestHostMapWithDate()`（無 anchor）在最新一份全 unmatched 時仍回那一份（維護頁語意不變）。
  8. SQL Server 翻譯：新查詢的 `ToQueryString` 能在 SqlServer provider 產生（既有 provider 翻譯測試同型補一條）。
  9. 突變：拿掉 ok 條件 → 案例 1、2 紅。

### E-1 週檢略過停用的本機主機

- **定案**：週檢的前置條件加「本機主機已登記（HostId≠0）且啟用」，判斷只寫在一處（`AnalysisOrchestrator` 呼叫 `ShouldRun` 之前，沿用它已算好的 `localHostDisabled`）。不成立時：checkup 階段記 `Skipped`、執行輸出一行（無 ⚠）「本機主機已停用，略過體檢（體檢以本機主機為對象）」；不呼叫體檢、不寫 Warn。
- **改動**：`AnalysisOrchestrator.cs` 週檢段；`WEB-SPEC.md` 排程流程一節補一句。
- **驗收標準**
  1. 本機主機停用的趟次：`WeeklyCheckupService.RunAsync` 未被呼叫、執行紀錄沒有「AttachWeeklyCheckup」Warn、checkup 階段 outcome 為 Skipped、輸出含「略過體檢」。
  2. 本機主機啟用且到期：行為同今（既有 `WeeklyCheckupServiceTests` 20 條維持綠）。
  3. 突變：條件反向 → 兩條各自紅。

### E-2 成功前有失敗嘗試也寫診斷（recovered）

- **定案**：`ChatJsonAsync` 最終成功但先前有內容層失敗的嘗試時，寫一筆診斷行，`EndedBy = "recovered"`，內容為那些失敗嘗試（類別、finishReason、字數、頭尾、思考字數——既有欄位）。診斷 logger 已被排除在警告計數之外，不影響趟次狀態。匯出 guide 補一句說明 `recovered`。
- **為什麼只做這個**：見複審紀錄。下一份匯出就能看到首次失敗的類別分布與 `reasoningChars`，據此決定 BACKLOG 裡兩個候選修法。
- **改動**：`AiJsonDiagnostics`（常數）；`AIService.ChatJsonAsync` 成功出口；`RunExportService` guide；`DETECTION-SPEC.md` AI JSON 重試一節。
- **驗收標準**（`AIServiceJsonDiagnosticsTests`、`RunExportServiceTests`）
  1. 假傳輸：第 1 次回 `[]`、第 2 次回合法 JSON → 回傳 Success、診斷行恰一筆、`EndedBy == "recovered"`、`AttemptDetails` 一筆且類別為 `empty`。
  2. 第 1 次就成功 → 不寫診斷行。
  3. 三次都失敗 → 仍是 `exhausted`（既有案例維持綠）。
  4. 匯出：含一筆 recovered 診斷的執行紀錄 → `diagnostics.entries == 1`、`byEndedBy` 含 `recovered`、`detailedDiagnosticsFirstSeen` 有值。
  5. 該趟 `WarnCount` 不因診斷行增加（recorder 測試）。

### E-3 匯出：Retired 密文分開列、guide 補述

- **定案**：`meta.deidentification.undecryptableSecrets` 只列目前用得到且解不開的；新增 `retiredUndecryptableSecrets` 列已不是目前認證方式的舊密文。guide 兩句分開：前者「需要重新設定」，後者「目前沒有使用，可到設定頁清空或忽略」。另在 guide【時間】或 dailyHealth 說明補一句「保守策略下夜間不逐顆取數，`prtgTargetSensors` 為 0 屬正常，數值由快照供應」。
- **改動**：`RunExportDeidentifierFactory.Secret`（依既有 `retired` 參數分流）、`RunExportDeidentifierSource`／`IRunExportMasker`／`RunExportDtos`／`RunExportService`；`WEB-SPEC.md` 匯出欄位表。
- **驗收標準**（`RunExportDeidentifierTests`、`RunExportServiceTests`）
  1. Retired 且解不開 → 只在 `retiredUndecryptableSecrets`；非 Retired 解不開 → 只在 `undecryptableSecrets`；兩者都解得開 → 兩個清單皆空。
  2. 匯出 JSON 含新欄位（空陣列也要輸出，維持「同資料重複匯出內容相同」）。
  3. guide 文字包含上述兩句（字面比對關鍵詞）。

### E-4 Security 記錄檔權限訊息

- **定案**：`EventLogService` 兩處 catch：`SecurityException` 或 `UnauthorizedAccessException` 時訊息為「讀取 {LogName} 記錄檔被拒：執行站台的帳號需加入本機「Event Log Readers」群組，或改以系統管理員身分執行」；其他例外維持原句。等級仍為 Warn。兩處共用一個訊息產生函式。
- **驗收標準**
  1. 單元測試（訊息產生函式）：傳入 `SecurityException` → 含「Event Log Readers」；傳入 `IOException` → 原句。
  2. 兩處 catch 都呼叫該函式（字面守門：`"讀取 {LogName} Event Log 失敗"` 字面只出現在該函式內）。

### E-5 結束代碼非 0 的趟次必有一行 Error

- **定案**：取數趟次的收尾點（`runRecorder.Finish` 之前）加單一不變式：趟次不成功（結束代碼將為 1）、不是使用者停止或窗口到點停止、且到目前為止 `ErrorCount == 0` 時，寫一行 Error：「本趟未成功完成：{原因摘要}」，原因摘要由已有的結果數字組成（例如「NetIQ 失敗 12 主機日」「本機分析失敗 N 天」），不重新判斷原因。`CredentialPreflight` 維持 Warn（它的註解定案不變）。AI 排程趟次同型檢查：`AiAnalysisHostedService` 在 `success == false` 時若沒有 Error 行，以既有的 `failureMessage` 寫一行 Error。
- **改動**：`AnalysisOrchestrator.cs` 收尾點；`AiAnalysisHostedService.cs` 收尾點；`WEB-SPEC.md` 趟次狀態一節補不變式。
- **驗收標準**
  1. 取數趟次：NetIQ 全部主機日失敗且過程只有 Warn → 執行紀錄有恰一行 Error、含「NetIQ 失敗」、結束代碼 1。
  2. 過程中已有 Error → 不追加（Error 行數不變）。
  3. 成功趟次、使用者停止的趟次 → 不追加。
  4. AI 排程：`success == false` 且無 Error → 追加一行；已有 Error → 不追加。
  5. 匯出層：上述案例 1 的趟次不再計入 `failedOrStuckWithoutErrorRuns`（`RunExportServiceTests`）。

### 批次 E 定向驗收

`--filter "FullyQualifiedName~PrtgHostMap|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgBackfill|FullyQualifiedName~PrtgValueAssessment|FullyQualifiedName~PrtgTriggeredValueFetcher|FullyQualifiedName~WeeklyCheckupServiceTests|FullyQualifiedName~AnalysisOrchestrator|FullyQualifiedName~AIServiceJsonDiagnosticsTests|FullyQualifiedName~AIServiceChatJsonTransportTests|FullyQualifiedName~RunExportDeidentifierTests|FullyQualifiedName~RunExportServiceTests|FullyQualifiedName~RunExportControllerTests|FullyQualifiedName~CredentialPreflight|FullyQualifiedName~EventLogService"`（命中：9／79／43／16／14／20／1／130／5／119／69／16／8／1）。

## 批次 B：AI 摘要重排

### B-1 版面

- **定案（閱讀順序：一句話標題 → 重點速報 → 狀況／趨勢／建議處置）**
  1. 標題：`fs-5`、字重 600、`max-width: 40em`。
  2. 重點速報：維持子卡（唯一有底色的區塊）與右上複製鈕；文字行高 1.7、`max-width: 40em`。
  3. 三格摘要：狀況／趨勢／建議處置各一格，容器為 CSS grid `repeat(auto-fit, minmax(20rem, 1fr))`、`gap` 用既有間距 token；每格是「眉標（`<h3>`，小字、600、灰）＋內文（1rem、行高 1.7、`max-width: 40em`）」。寬螢幕三欄、中等寬度自動兩欄、窄螢幕與列印單欄。
  4. 建議處置格：左側 3px 主色色條、眉標用主色，**不加底色**（全區塊只有重點速報一個底色強調）。不新增 icon。
  5. 缺值的格不渲染；三格都缺就沒有 grid。`aiAnalyzed=false`（統計模式替代文字）維持現行三行文字、不包框、不用 grid。
  6. 外層 `.lf-ai-block` 與「AI 摘要」徽章、`renderAiInline` 出口不變；不加展開收合、不加動畫；顏色只用既有 token（深色模式自動跟隨）。
  7. 尺寸一律 rem／em，使用者字級三檔下不得出現水平捲軸。
- **改動**：`record-detail.js` `aiSummaryBlock`；`site.css`（新增三個 class、`.lf-ai-brief__text` 行高與量寬、列印單欄）。
- **驗收標準**
  1. 行為測試（新檔 `ai-summary.behavior.mjs`）：五欄齊全 → 一個 grid 容器、三個 item、三個 `h3` 文字依序為「狀況」「趨勢」「建議處置」、只有第三個 item 帶 action class。
  2. 缺趨勢 → 兩個 item；狀況／趨勢／處置全缺 → 沒有 grid 容器、標題與重點速報仍在。
  3. `aiAnalyzed=false` → 沒有 `.lf-ai-block`、沒有 grid、三段文字仍以「狀況：」等標籤呈現。
  4. `CspPolicyGuardTests`、`FrontendConsistencyUiTests` 綠（無 inline style、無 innerHTML 插值）。
  5. 無頭 Edge 量測（主代理以腳本執行，1,366px 與 1,920px 兩種寬度，載入以固定假資料渲染的頁面片段）：每個 item 內文的實際寬度 ÷ 字級 ≤ 45；重點速報文字寬度 ÷ 字級 ≤ 45；1,920px 時三個 item 在同一列（`offsetTop` 相同）；根字級放大到「大」檔時 `document.documentElement.scrollWidth <= clientWidth`。量測數值寫進執行紀錄。
  6. `@media print` 規則存在且把 grid 設為單欄（UiTests 字面比對）。

### B-2 日誌來源副標白話化

- **定案**：副標句型改為「資料來源：NetIQ（最近一次取數：成功）。這一天可以再補上 PRTG 資料。」；不可補時「這一天不能再補 PRTG 資料，原分析保留；請核對來源或補跑 NetIQ。」來源名稱與 NetIQ 嘗試狀態各有一張對照表放在 `core/format.js`（來源：`Netiq`→NetIQ、`Local`→本機、`Prtg`→PRTG、未知→「未知」；狀態：依 `NetiqAttemptStatus` 既有值逐一給中文，未列到的值顯示「未知」）。PRTG-only 與案件授與檢視的既有句子不改。
- **改動**：`record-detail.js` `renderHeader`；`core/format.js`；`WEB-SPEC.md` §9.3 頁首；說明書 04 若引用舊句則同步。
- **驗收標準**
  1. 行為測試：`logSource='Netiq'`、狀態 success、可補 → 字串等於定案句型，且不含 `success`、`Netiq`、「可信」。
  2. 狀態為後端現有的每一個值 → 都有中文（測試讀 `NetiqAttemptStatus` 的常數清單逐一比對對照表，漏一個就紅——對照表與後端同步的守門）。
  3. 未知狀態值 → 顯示「未知」，不顯示原字串；狀態為 null → 句子為「資料來源：NetIQ（已保存的分析結果）。…」。
  4. grep 守門：`record-detail.js` 不含「最新 NetIQ 嘗試」。

### B-3 文件

- **改動**：`DESIGN-SYSTEM.md` 元件表（AI 摘要三格、重點速報）；`WEB-SPEC.md` §9.3 白話總覽一節；說明書 04「AI 摘要」描述。
- **驗收標準**：`HelpContentServiceTests` 與說明書結構校驗綠；現行文件 grep 不含「原本／後來／改為／第 68 輪」等敘事字眼（文件紀律）。

### 批次 B 定向驗收

`--filter "FullyQualifiedName~AiBrief|FullyQualifiedName~AiState|FullyQualifiedName~AiSummary|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~CspPolicyGuardTests"`。

## 批次 D：詢問 AI 改版（整日摘要＋焦點集合）

### 定案總則

- 每一輪的 context 固定包含**整日摘要**（薄）與既有的**報告全文**（有報告時）；另附 0～4 個**焦點問題**的詳細區塊（厚）。
- 焦點由確定性規則決定，**不呼叫 AI 做路由**：報告全文本來就在 prompt 裡，模型已能看到當天每個問題的細節；多一次路由呼叫在地端模型上是數十秒的等待與一個新的失敗點，換到的只是「替某幾個問題多附樣本訊息」。使用者提的「兩階段」要解決的是「不想先選問題才能問」，這個目的由下面的規則達成。
- 焦點順序：使用者指定或前輪延續 → 最新提問的關鍵字命中 → 都沒有時取嚴重度最高的前 3 個當預設焦點。
- 重新評估條件（寫進 BACKLOG）：實測若常出現「沒提到編號的提問答不到重點」，再考慮加 AI 路由。

### D-1 後端 context：整日摘要＋多焦點

- **定案**
  1. 整日摘要區塊**只取自 `GetDetail` 回傳的 DTO**（案件授與檢視時 headline 等已是空字串、`TopIssues` 已過濾，天然不外洩）：主機／日期／風險等級、headline、重點速報、狀況、趨勢、建議處置（空的欄位不印）、問題一覽（`TopIssues` 依嚴重度，最多 **15 筆（暫定）**，每筆一行：標籤、嚴重度、次數、趨勢短句、白話說明或 `knownIssue` 的前 40 字）。整段上限 **1,500 字（暫定）**，超出時從問題一覽尾端砍。
  2. 焦點區塊：每個焦點沿用既有 `BuildIssueContext`，加樣本訊息最多 2 則（圍欄文字同現行）。所有焦點區塊合計上限 **3,000 tokens（暫定，沿用既有 `TruncateToBudget`）**；超出時從最後一個焦點開始，先拿掉樣本訊息、再拿掉關鍵細節。
  3. 現場事件：只在首輪、焦點恰為 1 個、且該焦點來自使用者指定或關鍵字命中時查（與現行「使用者選定一個問題」的語意相同）；其餘情況不查。
  4. 報告全文預算＝既有算法，把整日摘要與焦點區塊算進「其餘內容」。
  5. system prompt 加一句：「提問涉及多個項目時分別說明並指出彼此關聯；提供的資料裡沒有的項目要明說。」
  6. `AiTextDto` 新增 `focus`（`[{ issueKey, label }]`）與 `focusSource`（`manual`／`keyword`／`default`）。
- **改動**：`AiInsightService.cs`（整日區塊 builder、`ChatAsync` 改收焦點清單）；`AiDtos`；`AiInsightServiceTests`。
- **驗收標準**（`AiInsightServiceTests`，以替身 AI 擷取實際送出的 prompt）
  1. 任一請求的 prompt 都含整日摘要區塊，長度 ≤ 1,500 字；20 個問題的日子 → 問題一覽恰 15 行以內且沒有超長。
  2. 兩個焦點 → prompt 含兩段問題區塊、各自的樣本訊息圍欄；零焦點不會發生（由 D-2 保證），傳入空清單時 builder 仍產生合法 prompt（防禦案例）。
  3. 四個焦點且樣本訊息很長 → 焦點區塊合計不超過預算，且被拿掉樣本的是後面的焦點。
  4. 案件授與檢視的 DTO（headline 等為空、`TopIssues` 只有被交辦的）→ prompt 不含原紀錄的 headline／速報字串、問題一覽只有被交辦的問題、不含報告全文。
  5. 首輪單一 `manual`／`keyword` 焦點 → 有呼叫現場事件查詢；首輪 `default` 焦點或兩個以上焦點 → 沒有呼叫。
  6. 回傳的 `focus` 與送進 prompt 的焦點一致、`focusSource` 正確。

### D-2 焦點判定與端點

- **定案**
  1. `ChatRequest` 新增 `focusKeys: string[]`；既有 `issueKey` 保留相容，視同 `focusKeys = [issueKey]`。兩者都沒有也合法。
  2. 焦點判定是一個純函式（輸入：`TopIssues`、`focusKeys`、最新一則使用者訊息、是否為手動指定；輸出：焦點清單與來源）：
     - 有效的 `focusKeys`（存在於 `TopIssues` 者，其餘靜默忽略）先保留順序；
     - 再補最新訊息的關鍵字命中：訊息含某問題的 EventId（**3 位以上**的完整數字，前後不得是數字；EventId 為 0 的問題不以數字比對）、Source 全名、Source 以 `-` 或 `:` 切開的最後一段（≥ 4 字元，不分大小寫）、Linux 規則 Id，或 `SourceEventLabel` 全文；
     - 合併去重後最多 **4 個**，超出的丟棄後補上的；
     - 仍為空 → 取 `TopIssues` 嚴重度最高的前 3 個（不足 3 個就全取），來源 `default`；
     - 來源標示：清單裡有任何 `focusKeys` 項 → `manual`；否則有關鍵字命中 → `keyword`；否則 `default`。
  3. `TopIssues` 為空的日子端點回 null（同現行；前端本來就不顯示對話卡）。
  4. 節流、輪數與訊息長度驗證、互動 lane 不變；一個請求仍只有一次 AI 呼叫。
- **改動**：新類別（Web 層，純函式）；`AiController.Chat`；`ChatRequest`。
- **驗收標準**（新測試類 `ChatFocusResolverTests`；控制器層案例放既有同型測試類）
  1. 「4625 和 4735 有關聯嗎」→ 兩個焦點、`keyword`。
  2. 「GroupPolicy 的錯誤怎麼辦」→ 命中 `Microsoft-Windows-GroupPolicy`。
  3. 「發生了 0 次嗎」「共 12 次」→ 不命中任何問題（EventId 0 與兩位數不比對）；「14625」不命中 4625。
  4. `focusKeys` 含一個不存在的 key → 忽略、不報錯。
  5. `focusKeys` 3 個＋訊息命中 2 個新的 → 回 4 個，前 3 個是 `focusKeys`。
  6. 無 `focusKeys`、無命中、5 個問題 → 回嚴重度前 3、`default`；只有 2 個問題 → 回 2 個。
  7. 舊請求只帶 `issueKey` → 等同單一焦點、`manual`。
  8. 控制器：案件授與檢視下 `focusKeys` 指向未被交辦的問題 → 被忽略（因為不在該使用者的 `TopIssues`）。
  9. `ControllerRouteSnapshotTests` 不變（沒有新路由）。
  10. 突變：EventId 位數門檻改 1 → 案例 3 紅；上限 4 改 5 → 案例 5 紅。

### D-3 前端：焦點列取代必選下拉

- **定案**
  1. 移除問題下拉；AI 可用且有重點問題時輸入框即可用；placeholder「直接發問，可以同時問多個問題，例如：4625 和 4735 有關聯嗎？」。
  2. 訊息區上方一列焦點列：顯示目前焦點 chips（`SourceEventLabel`，各有可聚焦的「×」移除鈕，`aria-label`「移除焦點：{標籤}」）；列尾「指定問題」按鈕開啟 checkbox 清單（`showDetailModal`；列出目前嚴重度篩選後可見的問題與嚴重度；勾滿 4 個後其餘停用）。還沒送出第一則且未指定時，焦點列顯示一行灰字「未指定，系統會依提問判斷」。
  3. 送出時帶目前的焦點為 `focusKeys`（使用者指定或前輪回來的）；回覆抵達後以回應的 `focus` 更新焦點列。`focusSource` 為 `keyword` 時該則回覆上方一行小字「已判定針對：A、B」；`default` 時「未偵測到指定問題，以最嚴重的幾項為主」。
  4. 變更焦點不重設對話；「清除重來」同時清空焦點。嚴重度篩選切換只影響「指定問題」清單，不重設對話、不移除既有焦點。
  5. 等待回覆期間焦點列的移除鈕、指定鈕與清除鈕停用；世代機制、10 輪計數、放大檢視（整組搬進 modal，含焦點列）不變。
- **改動**：`chat-panel.js`；`RecordDetail.cshtml` 對話卡；`record-detail.js`（`updateIssueOptions` 的呼叫改為更新可指定清單）；`site.css`（焦點列，chips 沿用 `lf-badge` 家族）；`chat-panel-generation.behavior.mjs` 改寫受影響案例、新 `chat-panel-focus.behavior.mjs`。
- **驗收標準**（行為測試）
  1. 初始：輸入框與送出鈕可用、沒有 `#chat-issue-select`。
  2. 送出 → 請求 body 含 `focusKeys`（初次為空陣列）；回應帶兩個 focus → 焦點列出現兩個 chip；下一次送出的 `focusKeys` 等於這兩個 key。
  3. 點 chip 的「×」→ chip 消失、訊息區內容不變、輪數不變。
  4. 指定清單已勾 4 個 → 第 5 個 checkbox 為 disabled。
  5. 模擬嚴重度篩選切換（呼叫更新可指定清單）→ 訊息與焦點不變。
  6. 等待期間：移除鈕、指定鈕、清除鈕皆 disabled；回覆後恢復。
  7. 清除重來 → 訊息與焦點皆清空、世代加一（既有世代案例維持綠：舊請求的回覆被丟棄）。
  8. `focusSource='default'` → 該則回覆上方有對應提示；`manual` → 沒有提示。
  9. chips 與提示以 `textContent` 寫入（`FrontendConsistencyUiTests` 綠）。

### D-4 文件

- **改動**：`WEB-SPEC.md` §9.3 詢問 AI 一節改寫（焦點規則、預算、回應欄位）、第 1488 行「詢問 AI 下拉選單」字樣、§7 API；`04-record-detail.md`／`.ai.md`；`manifest.json` keywords（加「焦點問題」）；`BACKLOG.md` 加「詢問 AI 的 AI 路由（重新評估條件）」。
- **驗收標準**：`HelpContentServiceTests`、說明書結構校驗綠；`WEB-SPEC.md` 內不再出現「下拉選擇，未選擇時輸入停用」。

### 批次 D 定向驗收

`--filter "FullyQualifiedName~AiInsightServiceTests|FullyQualifiedName~ChatPanel|FullyQualifiedName~ChatFocus|FullyQualifiedName~CaseGrant|FullyQualifiedName~ControllerRouteSnapshotTests|FullyQualifiedName~AiRequestGateTests|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`（既有命中 19／6／—／13／存在／17／11／303／1／314／28；`ChatFocus` 為新類別，寫完後回頭確認被篩到）。

實測劇本（交使用者，不是驗收）：同一天依序問「4625 和 4735 有關聯嗎」「今天整體怎麼樣」「那 GroupPolicy 的錯要怎麼處理」，看焦點列變化與回答是否各自對到問題。

## 升級注意事項（併 dev 後給管理者）

1. 登入失敗類的判定會變準：NetIQ 主機的「相關帳號」、登入失敗明細、密碼噴灑與殘留憑證判定從升級後的分析日開始使用正確的帳號；升級前的日子維持舊內容（過長的格會自動收合）。跨日比對在歷史窗口（約兩週）內會逐步恢復。
2. 新產生的報告寫「重點速報：」，舊報告仍是「給主管的說明：」。
3. 若最近 7 天內有「之前寫過全 unmatched 對應」的日子，升級後第一晚的 PRTG 重評可能替那些日子補出問題（依既有重評流程發佈）。
4. 沒有 schema 變更、沒有新設定；匯出檔多兩個欄位（`retiredUndecryptableSecrets`、診斷 `recovered`）。
5. 升級前照慣例先匯出一次。

## 站台側待辦（非程式改動，交使用者）

1. 資源守門：系統設定「資源守門」頁按「自動偵測並填入」（探測 S3 的處置）。
2. PRTG 端修正 6 台 host 值打壞的裝置（探測 [6] 的 objid）。
3. 取數範圍內 74 顆 sensor 若有「未分類」type，到 PRTG 維護頁「sensor type 分類補充對照」指定。
4. 確認 E-0 的根因是哪一種：查 `lf_prtg_host_map` 在 2026-10-20～10-29 每天各狀態的列數（依 map_date、map_status 分組計數）；並看 PRTG 維護頁待重評清單目前是否還掛著那幾天。
5. 兩個週六分析日（11-01、11-07）PRTG findings 為 0：到有 Disk Free 連續 Warning 的主機詳情看那兩天的狀態變更是否為空。
6. 第 67 輪待辦：站台跑幾晚後再匯出，看 `runMemory` 多趟的 `start.managedHeapBytes` 是否一路上升，再決定硬上限百分比。
7. 升級並跑過幾晚後再匯出一次：看 `aiFormatAnomalies.summary.diagnostics`（E-2 的 recovered 診斷）的類別與 `reasoningChars`，用來決定 BACKLOG 的 AI JSON 文法修法。

## 移至下一輪（第 69 輪預定）：PRTG 快照缺口自動回補

- **F-1 缺口判定（純函式）**：範圍內 sensor 最後一個 `sampled` 小時到現在整點之間、沒有任何樣本的連續小時；≥ 1 小時才算；回溯上限 `PrtgBackfillDays`；回補對象是缺口覆蓋到的日期（`historicdata` 一顆一天一次呼叫，小時粒度沒有更省）。
- **F-2 回補執行**：沿用回填寫 `ok` 覆蓋 `sampled` 的規則；受請求預算與互斥閘約束；以水位避免重做；夜間取數不等它，取數開始時自行停止、下次再續。
- **F-3 觸發**：快照服務啟動後首輪、暫停超過 1 小時恢復時排入；同時只有一個在跑；執行輸出與快照診斷各一行。
- **F-4 呈現與文件**：排程作業頁快照卡、`PRTG-SPEC.md` §3b、說明書。
- 成本估算：74 顆 sensor 一天缺口約 74 次 `historicdata`，約 15 分鐘。

## 明確不做（本輪定案）

- **詢問 AI 的 AI 路由**：理由見 D 定案總則；重新評估條件寫進 BACKLOG。
- **`[]` 的兩個候選修法**（進 BACKLOG，判準：下一份匯出的 recovered 診斷）：(a) JSON 模式改送「物件根」的 schema，讓 `[]` 在文法上不可能——要先對真實 llama.cpp 伺服器實測是否支援與輸出品質；(b) 同一趟連續空回覆後改關文法起手——只有在診斷顯示失敗是成串發生時才值得。
- **週檢涵蓋 NetIQ 主機**：現況週檢只做本機主機，本機停用的站台等於沒有週檢。這是功能範圍問題，不在本輪；BACKLOG 補一條並請使用者決定是否排入後續輪次。
- `prtgTargetSensors` 不改資料語意（只補 guide）；`CredentialPreflight` 維持 Warn。
- prompt 的 `brief` 欄位名與寫法指引不改；舊報告不回溯改名。
- 歷史紀錄的 `KeyAccounts`／`KeyDetails`／登入失敗明細不回溯重算。
- 「最近一次對應」類查詢（維護頁、取數範圍、快照目標）與逐 device 的對應選法不動。
- 問題查詢與儀表板的問題列不改；詢問 AI 不持久化、不放寬 10 輪、不改報告全文預算。
- 「脈絡」區塊預設收合的行為不改（對話卡在其中；使用者展開過會記住）。
- 第 67 輪的硬上限百分比不動；探測看到的 PRTG 端資料問題不加程式。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1、A-3 | Haiku 5.5 | 8df5c582 | 定向 708 綠 | 主代理補兩處漏改（執行輸出、DTO 註解）；脈絡上移後上方間距 `mt-3` 跟著第一個區塊 |
| A-2、A-4 | Sonnet 5.5 | bf060c2a | 定向 647 綠；突變（拿掉同文去重）轉紅 | 白名單外改 `side-rail.behavior.mjs` 取鈕方式（鈕搬家，案例不變） |
| C-1 | Sonnet 5.5 | fb106b3b | 解析 127 綠、下游 918 綠；三種突變皆轉紅 | 「雙空白改單空白」突變原本存活（無單一空白值的案例），主代理補 RDP 帳號含空白測試後轉紅 |
| C-2 | Sonnet 5.5 | e6f4ac62 | 定向 643 綠 | 說明書 16 章同字樣一併改；`HelpContentServiceTests` 斷言放寬為「來源觀測」 |
| E-0 | Sonnet 5.5 | b26805eb | PRTG 定向 383 綠；舊行為以測試重現（回 unmatched 那天）；突變拿掉 ok 條件 6 條轉紅 | `BuildOkMapDatesQuery` 由 public 收斂為 internal |
| E-1、E-4、E-5 | Haiku 5.5 | ff18f1f1 | 定向 232 綠 | 主代理補 E-5 停止防護（取消已觸發時不補寫）；AI 排程失敗路徑原本就寫 Error，不改 |
| E-2、E-3 | Sonnet 5.5 | 8fa3d8db | 定向 501 綠 | 三條既有測試改斷言（本段刻意改變的行為：recovered 診斷、舊密文分列） |
| B-1～B-3 | Sonnet 5.5 | a5657800 | 定向 702 綠；無頭 Edge 量測 1920／1366／800px 與字級「大」：每格每行 24～39 字、標題與速報 40em、1920px 三格同列、皆無水平捲軸 | 主代理合併重複的 `.lf-ai-brief__text` 規則 |
| D-1、D-2 | Sonnet 5.5 | 24bd8e62 | 定向 151 綠；突變（上限 4→5）轉紅 | 「位數門檻 3→1」突變原本存活，主代理補兩位數 EventId 案例後轉紅 |
| D-3、D-4 | Sonnet 5.5 | a00b1e0a | 定向 709 綠；突變轉紅 | 主代理修正：預設焦點只用於該輪、不沿用成下一輪的指定（否則之後的提問一直夾帶最嚴重 3 項）；空焦點一律顯示提示 |
| 收官 | 主代理 | 本 commit | 全量 1 紅 → 修正後定向 73 綠 | `CredentialPreflightTests` 一條斷言整趟 0 個 Error，與 E-5 定案衝突；改為「前置檢查仍是 Warn、唯一 Error 是 E-5 說明行」 |
| 自查 | 主代理 | 本 commit | 19 段逐項 grep 對照規劃產出全到位；E-0 五個呼叫端皆改；舊名只剩白名單位置；文件守門定向 860 綠 | 補三處文件遺漏：BACKLOG 加「AI 主分析首次回覆為 []」兩個候選修法與判準、NetIQ 能力條補「本機停用的站台沒有體檢」；PRTG-SPEC §4 修正描述（逐 device 選法不過濾狀態，與整份對應的選法不是同一規則） |
| 體檢 F1 | Sonnet 5.5 | 953e8c89 | 舊行為以測試重現（InsufficientDays）；PRTG 數值相關定向 288 綠；突變轉紅 | **E-0 引入的不一致**：規則歸戶跳過失敗的整份對應，數值評估逐 device 選法卻仍選到失敗那份的 unmatched 列，同一天規則有歸戶、數值判對應無效。新增 `GetOkHostMapDates`，逐 device 只在成功的整份對應中選 |
| 體檢 F2、F4 | Sonnet 5.5 | dcf96a2c | 定向 680 綠 | **D-3 引入的問題**：放大檢視（全螢幕 modal）中按「指定問題」疊開第二層 modal，Bootstrap 5.3 不支援。改為焦點列下方就地面板，「套用／取消／Esc」取代「關閉才套用」；焦點列加 `role="group"` |
| 體檢 F3、F5、F6 | 主代理 | 2591452a | 定向 694 綠 | 嚴重度排序收成一份（`ChatFocusResolver.SeverityRank`）；PRTG-only 日頁首改「資料來源：PRTG」與其他日一致；說明書補「問題名稱下方直接寫這是什麼問題」 |

## 複檢紀錄

- 第一版複檢：與既有設計衝突、批次相依、拆分原則四個坑、升級路徑——已併入。
- 輸入逐項比對（十項回饋、匯出各區段、兩份探測、五張截圖）：全部對到核對表；E-0 由此找到。
- 第二版（四視角＋整體視角）：見〈四視角複審紀錄〉；推翻第一版三處——D 的 AI 路由、E 的文法起手自適應、E-0 的修改點；刪去兩個不必要的資料變更（`IssueDto.PlainExplanation`、`prtgTargetSensors` 寫 null）；C-1 契約重寫。
- 驗收標準逐段檢查：每一條都是測試、grep 或腳本量測可判定；需要真實站台或真實 AI 伺服器的都移到〈站台側待辦〉。
- 規劃引用的既有名稱已 grep 驗證存在：`briefBlock`、`aiSummaryBlock`、`issueCell`、`guidancePanel`、`sourceEvidenceBlock`、`loginFailureDetailsTable`、`createSideRail`、`collapsibleText`、`ExtractLastValue`、`ExtractAccountsAndIps`、`ResolveHostMapRows`、`GetLatestHostMapWithDate`、`FindLatestHostMapDate`、`PrtgHostMapLookup.Select`、`WeeklyCheckupService.ShouldRun`、`localHostDisabled`、`AiJsonDiagnostics.Write`、`SecretInventory`（Core）、`RunExportDeidentifierFactory.Secret`、`CredentialPreflight.Run`、`AiController.Chat`、`AiInsightService.ChatAsync`／`BuildIssueContext`、`chat-panel.js` `updateIssueOptions`。
- 補查完成的三個事實：`NetiqAttemptStatus` 有七個常數（running／success／partial／retained-incomplete／failed／stopped／empty-once），B-2 對照表與守門測試以這七個為準，狀態為 null 時維持既有的「已保存的分析結果」；`RiskReportService` 既有測試 13 條（篩選詞有效）；`OrchestratorResult` 有 `FailureMessage` 與 `NetiqResult`，E-5 的原因摘要優先用 `FailureMessage`，為空時用 `NetiqResult` 的失敗主機日數組句。

## 體檢輪修正（換模型體檢：Fable 5.2，2026-11-10）

實作與自查由 Opus 5.5 完成；本節是換模型後對 `dev..feature/feedback-68` 整條 diff 的獨立體檢。規劃比對、獵 bug 與取捨由體檢方親做，未派代理。

**修正（3 條，皆在 `ChatFocusResolver`，commit db1b5398）**

1. **焦點滿了之後新問到的問題被靜默丟掉**（規劃契約本身的缺陷）。症狀：上限 4 個、規則是「前輪焦點先保留、新命中的補在後面」，幾輪後焦點滿了，再問「那 Ntfs 呢」不會進焦點，焦點列仍顯示舊的四個。修法：最新提問命中的排最前，請求帶來的焦點接在後面，被擠掉的是較舊的延續焦點。迴歸：`焦點已滿時再問到新的問題_新問題進焦點而不是被靜默丟掉`、`命中的問題已在focusKeys裡_不重複且移到最前`；既有「前三依序」案例改為新順序。本條推翻 D-2 定案第 2 點的合併順序。
2. **短來源名被其他英文字的片段誤命中**。症狀：來源以子字串比對，Linux 來源 `su` 會被 `issue`、`result`、`sudo` 命中，回答被帶去談無關的問題。已實測重現（三條轉紅）後修正：改為整詞比對，前後不得緊鄰英數或底線；緊鄰中文、標點、連字號不算相連。迴歸：`短來源名不得被其他英文字的片段誤命中`（3 例）、`短來源名整詞出現時命中`（3 例）、`來源名的後半段以連字號相連時仍命中`。
3. **請求的 `focusKeys` 沒有數量上限**。修法：只看前 16 個（`MaxRequestedKeys`）。迴歸：`請求帶超量focusKeys_只看前幾個_不擲例外`。DTO 註解與 WEB-SPEC 同步。

**查過、確認不需要修的**

- **F1 新增查詢的成本**：以實機規模（52 天 × 6,779 台＝35 萬列）實測，`GetHostMapForDay` 0.2 ms；`GetOkHostMapDates` 71 ms，與同一方法內既有的 `GetReadinessMaps`（66 ms）同級，取數範圍內的 sensor 通常只有一頁。不加索引、不改七個呼叫端。
- **同型普查**：Core 內沒有其他「逐行找標籤取到行尾」的解析（只有 `LogAggregator` 一處，已修）。
- **E-5 的連帶影響**：`ErrorCount` 的消費端只有趟次狀態（本來就由結束代碼判 failed）與排程頁「失敗卻沒有 Error 行」計數（會自然歸零），沒有其他副作用。
- **E-1 的連帶影響**：體檢階段記 `Skipped`，匯出與排程頁都不把 skipped 算成未成功；本機主機登記（`Touch`）不受執行範圍影響，不會誤印「未登記」。
- **本輪新增的 CSS class** 15 個都有使用端；現行文件的新增行沒有歷程敘事。
- **自查修正 commit（F1～F6）** 逐一重讀：F1 的排除條件與最新日借用分支不衝突；F2 面板在清除重來、等待回覆、放大檢視中的狀態轉移正確。

**已知殘留（不修，進 BACKLOG）**：欄位之間只有單一空白的來源，標籤值會讀到下一個欄位名稱之後。

**測試**：對最終樹（體檢修正、文件與歸檔都已 commit）跑全量一次——**8,861 通過／11 略過／0 失敗**（基線 8,748，多 113 筆）。dev 自分支起點沒有移動，併入後的樹與此相同，併入後不重跑。macOS 全量未跑（CI 涵蓋）。

## 終檢輪

終檢無新發現：併入前對體檢修正 commit（db1b5398）與文件 commit 再看一次 diff，未引入新問題；現行文件與程式一致。

**CLAUDE.md 新增四條「不要做」**：過去日的對應一律走 `GetHostMapForDay`；Windows 事件欄位不逐行取到行尾；`showDetailModal` 不疊開；詢問 AI 的焦點與整日摘要的唯一來源。

## 體檢交接

- **全量測試**（Windows，2026-11-10）：8,852 筆，第一次 8,840 通過／1 失敗／11 略過；唯一失敗是 E-5 與舊斷言的衝突（見執行紀錄「收官」列），修正後該類別定向 73 綠。修正只動測試檔一條斷言、沒有動正式程式碼，未再跑全量。最終 **8,841 通過／11 略過**，比基線 8,748 多 93 筆。
- **未驗證**：macOS 全量；真實瀏覽器中的風險日詳情頁（B 批以靜態樣本＋無頭 Edge 量測、D 批以行為測試驗證）；真實 llama.cpp 對多焦點 prompt 的回答品質（交使用者實測劇本）；E-0 在正式站台資料上的效果（〈站台側待辦〉4）。
- **自我體檢（Opus 5.5，2026-11-10）**：四視角＋整體視角逐檔讀改動，修正 6 項（見執行紀錄「體檢」列），其中 F1、F2 是本輪引入的問題。體檢後正式程式碼有改動（F1 數值評估、F2 對話面板），各以定向測試涵蓋，**未重跑全量**，併 dev 後的收尾全量要涵蓋。
- **已知殘留風險（未修）**：C-1 的欄位邊界以「連續兩個以上空白」判定；若某來源的單行訊息欄位之間只有單一空白，值會讀到下一個標籤為止（現有正式樣本皆為 2～4 個空白）。
- **換模型體檢建議重點**：
  1. C-1 擷取 helper 對本機 Event Log（有換行＋tab）以外的格式：英文版長句值、含雙空白的合法值是否被截斷。
  2. E-0 新查詢 `GetHostMapForDay` 的五個呼叫端是否還有漏網的 §4 語意呼叫點（grep `GetLatestHostMapWithDate(` 帶 anchor 的呼叫）。
  3. D-1 整日摘要的授權：所有欄位只來自 `RecordDetailDto`；案件授與檢視下 prompt 不含整日敘事。
  4. D-3 世代機制與焦點狀態在「等待中清除重來」「放大檢視中送出」的組合。
  5. CLAUDE.md 測試基線與文件地圖是否需要更新（收尾流程負責）。
