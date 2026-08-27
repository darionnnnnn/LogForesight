# 報表（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/reports`。
- **存取權限**：所有已登入使用者均可存取（`NAV_SECTIONS` 中 `requires: null`）。但 `isServerAdmin` 帳號因最小授權原則（僅具備系統維護與稽核權限，無業務資料檢視權限）隱藏此頁（`layout.js` 之 `BUSINESS_PAGES`）。
- **後端 API**：`GET /api/reports/summary?from={from}&to={to}&handlingScope={handlingScope}&compare={compare}`，對應服務為 `ReportService.GetSummary`。

## 篩選條件與工具列控制項

1. **起始日期（`#report-from`）與結束日期（`#report-to`）**：
   - 控制項：`<input type="date">`，上限設定為當天（`DateTime.Now`）。
   - 區間限制：若起訖天數差超過 366 天，前端會以 Toast 提示「查詢區間不可超過 366 天，請縮小範圍」並中斷查詢。
2. **比較模式（`#report-compare`）**：
   - 選項 `previous`（對比前一期）：依所選區間長度向前推算等長期間（例：查詢期間為 2026-08-01 至 2026-08-07 共 7 天，前一期為 2026-07-25 至 2026-07-31）。
   - 選項 `yoy`（對比去年同期）：起訖日期各自減 1 年（`from.AddYears(-1)` 至 `to.AddYears(-1)`）。
   - 自動預設邏輯：若使用者未手動變更過比較模式，查詢區間天數 ≥ 180 天時自動選用 `yoy`，否則選用 `previous`。
3. **期間快捷按鈕（`data-range="1|7|30|90"`）**：
   - 提供「昨日」（1 天）、「近 7 天」、「近 30 天」、「近 90 天」。
   - 計算基準：結束日期一律為「昨天」（`DateTime.Today.AddDays(-1)`），避免當日尚未完成夜間分析導致最後一天必然無資料。
4. **顯示範圍 Chips（`#report-scope-chips`）**：
   - 控制項：單選按鈕群組，參數名稱為 `handlingScope`。
   - `all`（全部）：預設值，統計母體包含所有處理狀態。在 URL 中不保留參數以保持網址整潔。
   - `unresolved`（未結案）：過濾僅保留未結案項目，下鑽至問題查詢時附加參數 `&statuses=open,in_progress`。
   - `open`（未處理）：過濾僅保留未處理項目，下鑽至問題查詢時附加參數 `&statuses=open`。
   - `unassigned`（未指派）：過濾僅保留未指派處理人項目，下鑽至問題查詢時附加參數 `&unassigned=true`。
   - 切換時會重新向後端請求資料，重新計算整頁 KPI 與圖表。
5. **查同一問題的其他主機**：
   - 連結按鈕導向 `/records`。
   - 旁附 Popover 提示：告知使用者可前往問題查詢頁輸入 Event ID／來源並切換為「依問題」視角，進行跨主機同簽章查詢。
6. **自訂圖表（`#btn-customize-charts`）**：
   - 點擊開啟 Modal `#chart-picker-modal`。
   - 註冊表 `CHART_REGISTRY` 包含 6 個圖表 ID：`trend`、`category`、`host`、`risk`、`affected-hosts`、`handling-progress`。
   - 勾選狀態存於 localStorage 鍵 `lf.reports.visibleCharts`。未勾選的圖表其外層容器會被加上 `d-none`，且不呼叫 Chart.js 建構以節省效能；重新勾選時執行 lazy render。
7. **列印 / 存成 PDF（`#btn-print-report`）**：
   - 執行 `window.print()`。在列印樣式中（`.lf-no-print`）會自動隱藏工具列、自訂圖表按鈕、視角切換等控制項，並於頁首顯示列印標題 `#print-title`（格式：`LogForesight 風險報表 YYYY-MM-DD ～ YYYY-MM-DD（顯示範圍名稱）`）。

## KPI 卡指標定義與下鑽

容器為 `#report-kpi`，呈現 4 張指標卡：

1. **問題總數（`kpi.totalIssues`）**：
   - 定義：期間內所有主機日中被列入重點問題（`TopIssues`）的累計數量（包含低風險日的問題）。
   - 下鑽路徑：`/records?riskLevels=高,中,低&from={from}&to={to}{scopeDrillParams}`。顯式指定高/中/低等級，避免問題查詢頁預設隱藏低風險導致筆數不符。
2. **高風險日（`kpi.highRiskDays`）**：
   - 定義：期間內每日綜合判定為高風險（`RiskLevels.High`）的主機日總數。
   - 下鑽路徑：`/records?riskLevels=高&from={from}&to={to}{scopeDrillParams}`。
3. **受影響主機（`kpi.affectedHosts`）**：
   - 定義：期間內至少出現一天高風險或中風險（`RiskLevels.IsActionable`）的相異主機總台數（跨日去重）。
   - 下鑽路徑：`/records?riskLevels=高,中&from={from}&to={to}{scopeDrillParams}`。
4. **涵蓋率缺口天數（`kpi.coverageGapDays`）**：
   - 定義：期間內標記為 `HasCoverageGap`（資料不完整、分析失敗或 Security log 未讀取）的主機日總數。
   - 下鑽：無下鑽連結（`url: null`）。卡片下方附提示說明「資料不完整或 Security log 未讀取——沒告警不等於沒問題」。
5. **前期對比徽章（`comparisonBadge`）**：
   - 計算公式：`delta = current - previous`，`percent = Math.round((delta / previous) * 100)`。
   - 語意反轉：告警數上升代表風險變差，因此 `delta > 0` 顯示紅色（`text-danger`，`↑ {percent}%（前期 {previous}）`）；`delta < 0` 顯示綠色（`text-success`，`↓ {percent}%（前期 {previous}）`）；`delta == 0` 顯示灰色（`text-muted`，`與前期持平`）。
   - 例外狀態：若前期為 0 且當期為 0，顯示「與前期相同」；若前期為 0 但當期大於 0，顯示紅色「↑ 前期為 0」。
6. **保留期過期警示（`comparisonOutOfRetention`）**：
   - 當前一期的結束日期早於系統資料保留門檻（`DateTime.Today - RetentionDays`）時，後端回傳 `comparisonOutOfRetention: true`。
   - 前端於 KPI 卡上方呈現黃色警示區塊：「比較期間的資料已超過保留期，對比數字不具參考價值。」。

## 六大圖表卡片詳細規範

### 1. 告警數量趨勢（`trend-section` / `#trend-chart`）
- **圖表類型**：折線圖（Chart.js line）。
- **資料來源**：`currentData.trend`（逐日資料，無資料日由後端補 0 而非略過，以維持折線連續性）。
- **維度與系列**：
  - 高風險（`highRisk`）：紅色線條。
  - 中風險（`mediumRisk`）：黃色線條。若系統設定中未勾選顯示中風險日（`!visibleDayRisk.has('中')`），則中風險系列完全不繪製，避免貼底 0 線誤導使用者。
- **下鑽機制**：點擊資料點呼叫 `drillTo`，導向 `/records?riskLevels={高|中}&from={date}&to={date}`。
- **工具列（Table 檢視）**：欄位為「日期」、「高風險」、「中風險」、「錯誤數」。
- **無資料狀態**：呼叫 `charts.renderNoData` 顯示「此期間沒有告警資料」。

### 2. 風險類型分布（`category-section` / `#category-chart`）
- **圖表類型**：水平堆疊長條圖（Chart.js horizontal bar，`indexAxis: 'y'`）。
- **資料來源**：`currentData.categories`。
- **維度與堆疊**：Y 軸為問題類別名稱（中文對照，如儲存、安全、系統等）；X 軸為事件數量；堆疊層級依嚴重度順序分為高（High）、中（Medium）、低（Low）。
- **顯示範圍限制**：因本圖表為後端 SQL 端跨主機跨日聚合的獨立投影，母體不受日層級的 `handlingScope` 影響。當 `currentScope !== 'all'` 時，卡片副標題（`#category-subtitle`）會明確標註「不受「顯示範圍」篩選影響」。
- **下鑽機制**：點擊長條特定嚴重度區段，導向 `/records?categories={category}&severity={severity}&riskLevels=高,中,低&from={from}&to={to}`。
- **工具列（Table 檢視）**：欄位為「類型」、「高」、「中」、「低」、「問題數」、「期間累計」、「主機數」。其中問題數為去重風險資訊筆數，高/中/低三者之和即為問題數。

### 3. 主機告警排行與問題排行（`host-section` / `#host-chart`）
- **視角切換器（`#rank-mode-toggle`）**：
  - 按鈕「主機」（`data-rank-mode="host"`）與「問題」（`data-rank-mode="issue"`）。
  - 切換狀態儲存於 localStorage 鍵 `lf.reports.rankMode`，預設為 `host`。
- **主機排行模式（`renderHostChart`）**：
  - 圖表：水平堆疊長條圖，顯示 Top 10 主機的高風險日數（紅）與中風險日數（黃）。
  - 超過 10 台主機時，第 11 台起由後端合併為一筆「其他 N 台」彙總長條（不可點擊下鑽）。
  - 下鑽：點擊個別主機長條導向 `/hosts/{hostId}`。
  - 檢視全部（`#host-view-all`）：當有「其他」主機存在時顯示連結，導向 `/records?view=host&riskLevels=高,中&from={from}&to={to}`。
  - 工具列（Table 檢視）：欄位為「主機」、「高風險日」、「中風險日」、「關聯訊號日」、「最新狀況」。
- **問題排行模式（`renderIssueRankChart`）**：
  - 圖表：水平長條圖，依問題的**優先度分數（`priorityScore`）**由高至低排序，長條長度代表事件總次數（`totalCount`）。
  - 排除規則：受影響主機全部都已有結論（已處理／不處理／誤報／已知雜訊）的問題不會納入排行；被排除的數量顯示於副標題「另有 N 個問題已有結論（未列入）」。
  - 超過 10 個問題時，第 11 個起由後端合併為一筆「其他 N 個問題」彙總長條（不可點擊下鑽）。
  - 下鑽：點擊個別問題長條導向 `/records?view=issue&source={source}&eventId={eventId}&riskLevels=高,中,低&from={from}&to={to}`。
  - 副標題綜合說明（`#host-rank-subtitle`）：組合顯示「共 N 個問題」＋（若 scope≠all）「；不受「顯示範圍」篩選影響」＋（若 `issueStatsPending`）「；統計整理中，數字可能不完整」＋（若 `concludedIssueCount > 0`）「；另有 N 個問題已有結論（未列入）」。
  - 檢視全部（`#host-view-all`）：當有「其他」問題存在時顯示連結，導向 `/records?view=issue&riskLevels=高,中,低&from={from}&to={to}`。
  - 工具列（Table 檢視）：欄位為「問題」、「分數」、「分類」、「最高嚴重度」、「主機數」、「風險日數」、「事件次數」、「vs 基準」、「首見（機房）」。

### 4. 風險層級占比（`risk-section` / `#risk-chart`）
- **圖表類型**：甜甜圈圖（Chart.js doughnut）。
- **計算公式**：高風險日數（`high`）與中風險日數（`medium`）之占比。中心文字顯示高風險百分比 `Math.round((high / (high + medium)) * 100)%`。
- **下鑽機制**：點擊圖塊或下方圖例項目導向 `/records?riskLevels={高|中}&from={from}&to={to}`。
- **無資料狀態**：顯示「此期間沒有風險日」。

### 5. 受影響主機占比（`affected-hosts-section` / `#affected-hosts-chart`）
- **圖表類型**：甜甜圈圖。
- **計算公式**：分子為受影響主機數（`kpi.affectedHosts`），分母為可見主機總數（`totalHosts`）。其餘主機數為 `Math.max(total - affected, 0)`。中心文字顯示受影響百分比 `Math.round((affected / total) * 100)%`。
- **下鑽機制**：點擊「受影響」圖塊或圖例導向 `/records?riskLevels=高,中&from={from}&to={to}`；「其餘」為彙總類別無法下鑽。
- **無資料狀態**：可見主機總數為 0 時顯示「尚無主機資料」。

### 6. 處理進度（`handling-progress-section` / `#handling-progress-chart`）
- **圖表類型**：甜甜圈圖。
- **計算公式**：分子為期間內高/中風險日已結案數（`handling.resolvedCount`），分母為高/中風險日總待辦數（`handling.totalCount`）。未完成數為 `total - resolvedCount`。中心文字顯示已處理百分比 `Math.round((resolvedCount / total) * 100)%`。
- **下鑽機制**：點擊「未完成」圖塊或圖例導向 `/records?statuses=open,in_progress&riskLevels=高,中&from={from}&to={to}`；「已處理」因分散在各日無單一篩選條件可對應，不提供下鑽。
- **隱藏規則**：當 `handlingScope !== 'all'` 時，因母體已抽離已結案資料，處理進度百分比恆為 0% 或 100% 而失去參考意義，`isChartHidden` 會強制將此圖表隱藏（`d-none`），不論自訂圖表中是否勾選。
- **無資料狀態**：總數為 0 時顯示「此期間沒有高／中風險日」。

## 常見問答與邊界狀況（Q&A）

- **Q: 為什麼自訂圖表勾選了「處理進度」，畫面上卻看不到？**
  - **A**: 檢查上方「顯示範圍」是否選取了「未結案」、「未處理」或「未指派」。當顯示範圍非「全部」時，已結案項目已被後端過濾排除，處理進度圖表失去對比資訊量，系統會自動將其隱藏。切換回「全部」即可恢復顯示。
- **Q: 為什麼點擊「風險類型分布」或「問題排行」下鑽後，清單筆數和上方的顯示範圍對不起來？**
  - **A**: 「風險類型分布」與「問題排行」屬於跨主機跨日的問題維度獨立聚合投影，不受上方日層級「顯示範圍」篩選影響。卡片副標題已標明「不受「顯示範圍」篩選影響」。
- **Q: 為什麼問題排行的列表筆數少於總問題數，但又沒有看到某些常見的告警？**
  - **A**: 排行榜僅列出 Top 10 問題，其餘併入「其他 N 個問題」。此外，若某問題在所有受影響主機上均已獲得結論（例如全數標記為已知雜訊、誤報或已處理），該問題會被自動排除出重點排行，排除筆數會標註在副標題「另有 N 個問題已有結論（未列入）」。
- **Q: 為什麼 KPI 卡片的「受影響主機」數字點進去後，問題查詢清單的列數大於該數字？**
  - **A**: KPI 卡片上的「受影響主機」是去重後的主機台數（一台主機在查詢期間內有多天出問題仍只算 1 台），而問題查詢頁預設列出的是逐「主機×日」的明細記錄。要對照主機總數，請參考問題查詢頁頂部的「共 N 台主機（去重）」。
- **Q: 為什麼前期對比數字出現紅色向上箭頭？**
  - **A**: 在日誌告警與風險系統中，告警數量「上升」代表系統風險增加、情況惡化，因此以紅色（`text-danger`）呈現警告；告警數量「下降」代表改善，以綠色（`text-success`）呈現。
