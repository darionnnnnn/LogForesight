# WEB-FEEDBACK-2-PLAN：第二輪使用者回饋的規劃（2026-07-28，共 14 項）

> 狀態：**全部實作完成（2026-07-28）**，885 個測試綠（含新增的 6 個回歸測試）、
> 關鍵頁面（風險日詳情、報表、規則維護、設定）瀏覽器實測通過。
> 決策 D1–D4 於 2026-07-28 定案，見「決策點（已拍板）」一節。
>
> 前一輪（docs/WEB-FEEDBACK-PLAN.md）已全部完成；本輪回饋集中在
> **嚴重度層級、處理狀態一致性、報表圖表呈現**三個主題。
>
> 實作時與規劃的偏差（皆為澄清，非變更）：
> - **#1 RiskBasis／ElevatesDayRisk 不需要 SchemaUpgrader**：規劃時誤以為
>   `LogIssueSignature`／`DailyAnalysisRecord` 走 SQL 逐欄位持久化，實際上 SQL 後端
>   （`EfAnalysisRecordStore`）整筆序列化進 `ContentJson`，`lf_top_issues` 只是查詢用的
>   維度表（filter-only）。新增欄位隨 JSON round-trip 自動生效，無需資料庫遷移。
> - **#1 TrendAnalyzer 逃逸路徑**：升級判斷（High→原 Critical）不只發生在規則命中時，
>   也發生在「High 嚴重度問題頻率上升」這條路徑（`TrendAnalyzer.Escalate`）——
>   這條路徑原本會把嚴重度撞頂到 Critical、連帶讓當天判定高風險，B1 改用旗標時
>   一併在 Escalate 前記錄「原本是不是 High」以複製同一個效果，行為零改變。
> - **#1 CategoryAggregator 新增 ElevatesCount**：規劃未提及，實作中發現儀表板分類卡的
>   紅框顯著性（`criticalCount > 0`）三級化後恆為 0 會靜默失效，補上對應的旗標計數欄位。
> - **#6 RecordHandlingLog 免 SchemaUpgrader**：同 #1，處理歷程走 JSON blob，
>   新欄位（IssueKey/IssueLabel）直接生效。
> - **#6 批次時間戳改用共用值**：`SetIssueStatusBatch` 原本逐筆呼叫 `DateTime.Now`，
>   改成整批共用一個 `occurredAt`——前端 timeline 靠「同操作者＋同時間戳」分組收合，
>   逐次取值的微小時間差會讓分組失效。
> - **#1 Program.cs 遺漏點**：批次主控台的紅色警告橫幅（`criticalIssues` 篩選）原本
>   直接比對 `Severity==Critical`，B1 後改比對 `ElevatesDayRisk`，否則橫幅會失去逐項列出的內容。

## 批次分組（依相依與風險排序）

| 批次 | 項目 | 性質 |
|------|------|------|
| A | #2 設定頁未儲存提醒、#5 簽章查詢說明、#9 折行檢查、#10 報告全文整列可點、#14 原始訊息改名＋modal | 純前端小修，低風險，無相依（#14 先建 ui.js 通用 modal helper，#13 復用） |
| B | #3 圓餅圖改左圖右列、#4 移除全部 PNG 鈕 | 報表前端，低風險 |
| C | #12 對外三態、#6 歷程同步、#7 勾選與狀態拆欄、#8 已結案排序收合、#13 歷程限高＋modal 放大 | 處理狀態一致性，前後端，**#12 先做**（#6/#8 的顯示依賴三態定義）；#13 排在 #6 之後（放大檢視呈現的就是 D4 的逐筆歷程） |
| D | #1 嚴重度層級（B1＋「重大」標註）、#11 風險等級一致性 | 涉及批次分析核心與資料遷移，已拍板 B1 |

批次 C、D 各自內聚；A、B 可隨時穿插。建議順序 A → B → C → D。

---

## 決策點（已拍板，2026-07-28）

- **D1：採 B1 三級化＋日風險旗標，且旗標要在畫面上顯性標註**。
  全面停止產生 Critical，原 Critical 規則改為「高＋命中即列為高風險日」旗標，
  現行風險判定行為完全不變，畫面上嚴重度只剩高/中/低；**帶旗標的規則命中時，
  畫面另以「重大」標註呈現**（加強一致性的同時保留「這件事特別嚴重」的直覺），
  細節見 #1 方案第 6 點。
  （落選案備查：B2 僅顯示層合併——下鑽與統計會對不上；B3 維持四級只補文案。）
- **D2：#8 只做風險日詳情的重點問題**。問題查詢清單維持既有緊急程度排序
  （清單已有狀態篩選 chip 可用）。
- **D3：詳情頁計數器改為「已處理 N／處理中 O／未處理 M」**。
  已處理＝標成 resolved；處理中＝標成 in_progress；未處理＝從未標記
  （且非預設不處理/自動雜訊）或明確 open。不處理/誤報/已知雜訊仍三邊都不計
  （已有結論、不是待辦），實作見 #8 方案第 4 點。
- **D4：處理歷程逐筆詳實記錄，不做彙總列**。每個問題的每次狀態變更都要留下
  「誰、何時、對哪個問題、標成什麼狀態」的獨立紀錄；「攏統的彙總標記沒有意義」。
  實作見 #6 方案第 1 點（含 schema 變更與 timeline 顯示的分組呈現）。

---

## #1 嚴重度層級：設定與顯示多了一個「嚴重」

**現況與事實確認**：問題嚴重度是四級列舉 `IssueSeverity`（Low/Medium/High/Critical，
KnownIssueCatalog.cs:15），「嚴重」= Critical 的中文顯示名（format.js:45）。
**Critical 不是死碼，且是「高風險日」的直接判定依據**：

- 種子規則有多條 Critical：磁碟故障（Event 153/55）、WHEA 硬體錯誤、非預期關機 6008、
  安全日誌被清除 1102 等（KnownIssueSeed.cs）。
- 趨勢層頻率暴增會把 High 升級成 Critical（TrendAnalyzer.Escalate）。
- 批次的日風險判定：**有未抑制的 Critical 問題或 Critical 關聯訊號 → 當日=高風險**；
  只有 High 問題/趨勢異常/關聯訊號 → 中風險（LogAnalysisService.ComputeRuleBasedRisk，
  LogAnalysisService.cs:709）。

所以「實際上嚴重度只有高中低」是**觀察偏差**（Critical 事件本來就罕見），
不是程式多做了一級。但四級與日風險三級（高/中/低）字面撞在一起確實造成困惑
（#11 即其副作用），簡化有其價值。

**方案 B1（建議）：三級化＋「命中即列為高風險日」旗標**

核心想法：Critical 在系統裡真正的職責只有一個——「這條規則命中，當天就是高風險日」。
把這個職責顯性化成規則上的布林旗標，嚴重度就能安全地收斂成三級，
且**現行風險判定行為零改變**。

1. **Core／批次**：
   - `KnownIssueRule` 新增 `ElevatesDayRisk`（bool）；`CorrelationFinding` 同樣改為
     嚴重度 High＋旗標（CorrelationAnalyzer 中原 Critical 的組合）。
   - `ComputeRuleBasedRisk` 改看旗標：任一未抑制問題命中 `ElevatesDayRisk` 規則
     或關聯訊號帶旗標 → 高；其餘規則不變。
   - `TrendAnalyzer.Escalate` 升級封頂改為 High（原本可升到 Critical）。
   - `IssueSeverity` 列舉**保留 Critical 值不刪**——舊紀錄/舊規則反序列化不能爆；
     但所有新產出不再是 Critical。
   - SchemaUpgrader：規則表既有 Severity=Critical 的列 → Severity=High、
     ElevatesDayRisk=true（種子與使用者自訂規則一併遷移）。
2. **Web 讀取正規化（單一咽喉點）**：歷史紀錄裡已存的 Critical 在 DTO 映射時一律
   顯示為 High——落點放在 RecordQueryService/RecordStatsBuilder/ReportService 共用的
   嚴重度→DTO 轉換處（一個 helper，如 `SeverityDisplay.Normalize`），不在前端特判。
   報表類型分布的 `criticalCount` 於 DTO 端併入 `highCount`（DB 欄位保留不動，
   避免動 lf_record_categories schema）。
3. **設定**：`SystemSettings.UnhandledSeverities` 預設改 `{High, Medium}`；
   `ParseUnhandledSeverities` 把既有設定中的 "Critical" 正規化為 High（讀取時靜默轉換，
   既有部署不用手動改）。設定頁層級按鈕剩三顆。
4. **前端**：format.js `SEVERITY_ORDER`/`SEVERITY_NAMES`/`SEVERITY_VARIANT` 刪 Critical；
   charts.js `severityColors` 刪 Critical；reports.js 類型分布堆疊圖與表格欄「嚴重」移除；
   規則編輯器（Rules.cshtml:135）刪「嚴重」選項、新增「命中即列為高風險日」勾選
   （含說明文字：這正是原「嚴重」等級的實際作用）。
5. **不回改的部分**：舊報告 txt 全文中的 Critical 字樣是證據層，不回寫；
   AI prompt 中的嚴重度敘述同步改三級。
6. **「重大」標註（D1 拍板追加）**：旗標不只是後端判定依據，要在畫面上顯性呈現——
   使用者要一眼看得出「這條問題特別嚴重、是它讓今天變高風險日」：
   - **詳情頁重點問題列**：命中帶旗標規則的問題，在嚴重度徽章旁加「重大」徽章
     （danger 色系，tooltip：「命中重大規則——此類問題出現當日即列為高風險日
     （原「嚴重」等級）」）。資料流：TopIssueDto 增 `ElevatesDayRisk`（bool），
     RecordQueryService 組 DTO 時以 issue.RuleId 對照規則store 帶出。
   - **規則維護頁**：清單列與編輯表單都顯示這個旗標（編輯表單的勾選文字：
     「命中即列為高風險日（重大）」）；篩選 chips 增加「重大」快篩。
   - **跨主機同簽章查詢**（報表）：命中列同樣帶「重大」徽章——這個查詢正是
     「全環境共通重大問題」的主要排查入口。
   - format.js 新增 `elevatesBadge()` 之類的單點工廠，三處共用同一顆徽章
     （§8.2 顏色＋文字單一定義原則）。
   - 舊資料相容：歷史紀錄裡嚴重度仍是 Critical 的問題，讀取正規化成 High 時
     **一併視為帶「重大」標註**（Critical 本來就是這個語意），不會出現
     「舊的高風險日看不出誰是元凶」的斷層。

**影響檔案**：KnownIssueCatalog.cs、KnownIssueSeed.cs、TrendAnalyzer.cs、
CorrelationAnalyzer.cs、LogAnalysisService.cs、SchemaUpgrader、SystemSettings.cs、
SystemSettingsService.cs、RecordQueryService.cs、RecordStatsBuilder.cs、ReportService.cs、
RuleAdminService.cs、RuleDtos.cs、RecordDtos.cs（TopIssueDto/SignatureHitDto 加旗標）、
format.js、charts.js、reports.js、settings.js、rules.js、Rules.cshtml、Settings.cshtml 文案、
record-detail.js（重大徽章）。

**測試影響**（現有 879 綠）：TrendAnalyzerTests（升級到 Critical 的 case 改封頂 High）、
SelfTestRunner check「嚴重度升級為 Critical」、KnownIssueCatalogTests、
CategoryAggregatorTests、ReportServiceTests、RecordQueryServiceSearchTests（severity 篩選）、
SystemSettingsServiceTests、RuleAdminServiceTests；新增：旗標判定高風險日、
Critical 讀取正規化、設定遷移。

**風險**：中。動到批次判定核心，靠「旗標等價替換 Critical 判定」維持行為不變，
需要靠既有 RiskReportServiceTests/SelfTestRunner 驗證前後判定一致。

---

## #2 設定頁跳轉前提醒尚未儲存

**現況**：settings.js 無 dirty 追蹤；站台是 MPA（側欄連結都是整頁跳轉），
離開即丟失未儲存的修改。

**方案**：
1. settings.js 載入完成後對 `#settings-form` 監聽 `input`/`click`（層級與顯示按鈕是
   button toggle，不觸發 input 事件，需一併涵蓋）設 dirty 旗標；
   儲存成功後清除。
2. dirty 時掛 `beforeunload`（瀏覽器原生「確定要離開？」對話框）；MPA 下這一個 handler
   就涵蓋側欄跳轉、重新整理、關閉分頁，不需要攔截個別連結。
3. **排除**不屬於設定內容的欄位：AD 測試帳號/密碼、測試連線按鈕（測完就丟，不算未儲存）。

**影響檔案**：settings.js。可做成 ui.js 的通用 helper（`trackUnsaved(form, options)`）
供未來其他頁復用，但本輪只掛設定頁。

**風險**：無。注意 severity/顯示模式按鈕與 AD checkbox 都要觸發 dirty。

---

## #3 圓餅圖改「左圖右文字條列」；#4 移除所有 PNG 鈕

**現況**：三顆占比圖（風險層級占比/受影響主機占比/處理進度）是 doughnut、
legend 隱藏、中心疊百分比；每張圖卡工具列有「表格」切換與「PNG」下載
（charts.js attachToolbar:144-180）。圓餅圖本來就沒有 XY 軸——使用者指的應是
「表格切換後變成一張表」的呈現不直覺，以及切換/下載鈕多餘。

**方案**：
1. charts.js 新增 `attachDoughnutLegend(container, { items })`：每列
   「色點＋名稱＋數值＋百分比」，列本身可點（沿用該分段的 drillTo URL，
   與點圖同一個下鑽目的地）。
2. Reports.cshtml 三顆占比卡的 body 改兩欄（左 `lf-chart--sm`、右條列；
   手機窄幅時上下堆疊——flex-wrap 即可）；reports.js 三個 render 函式改呼叫新 helper，
   **不再對這三張卡呼叫 attachToolbar**（表格切換與 PNG 一併消失；數字已在右側條列，
   表格模式失去存在意義）。中心百分比保留。
3. #4：attachToolbar 移除 PNG 下載鈕（charts.js:166-176 整段刪除），
   趨勢/類型分布/主機排行保留「表格」切換（無障礙與精確讀值仍需要）。
   需要圖檔的情境走既有「列印 / 存成 PDF」。

**影響檔案**：charts.js、reports.js、Reports.cshtml、site.css（條列樣式）。
**風險**：無後端變動。注意 WEB-SPEC.md §8.3 規則 4 寫明「表格切換＋PNG 下載」，
需同步修訂規格文件。

---

## #5 跨主機同簽章查詢加上說明

**現況**：Reports.cshtml:126 只有標題與兩個輸入框，第一次看不懂要輸入什麼、查出來代表什麼。

**方案**：卡片標題下加一段說明（已按實作確認語意）：
> 輸入 Event ID（可加來源縮小範圍），找出**同一個事件簽章曾出現在哪些主機、哪些日子**——
> 用來判斷問題是單機個案還是全環境共通（例如同一批次更新後多台同時出現）。
> 查詢範圍：您有權檢視的主機、資料保留期內的全部紀錄（不受上方報表期間限制）。

最後一句很重要：簽章查詢**不吃**報表的 from/to（ReportService.FindSignature 不帶日期），
與畫面上方期間列並存時使用者一定會誤會範圍。

**影響檔案**：Reports.cshtml。**風險**：無。

---

## #6 處理狀態儲存後與處理歷程/問題查詢不同步

**現況與成因**（三個獨立缺口疊加）：

1. **問題層級標記完全不寫處理歷程**：`HandlingService.ApplyIssueStatus` 只寫
   issue store＋稽核，不 `AppendLog`（HandlingService.cs:223-263）；歷程只記日層級的
   Update/Assign。使用者批次「已處理」後，歷程最後一筆仍停在較早的
   「指派處理人／處理中／處理人：Wayne」——正是回饋附的畫面。
2. **處理面板顯示的是「存的日層級狀態」不是推導值**：指派處理人會把日層級自動推進成
   in_progress（HandlingService.cs:295-296），之後問題全結案也不會改寫這個存值；
   面板 chips 預選 `handling.status`（handling-panel.js:217），於是顯示「處理中」。
3. **清單顯示推導狀態**：只要勾選的問題沒涵蓋全部計入的問題（例如嚴重度篩選鈕
   隱藏了部分列、或右側還有未勾的），推導就是 in_progress（DayHandlingDerivation:54）
   ——語意正確（「已開始處理」）但畫面沒解釋，使用者以為儲存沒生效。

**方案**（歷程粒度依 D4 拍板：逐筆詳實，不彙總）：
1. **問題層級標記逐筆寫入處理歷程**：
   - `RecordHandlingLog` 新增兩個欄位（SchemaUpgrader 對 lf_record_handling_log 加欄）：
     - `IssueKey`（nullable string）——日層級操作為 null，問題層級操作存簽章鍵；
     - `IssueLabel`（nullable string）——顯示用的「Source EventId」文字**反正規化存下來**，
       歷程是追責紀錄，不能因為日後紀錄被清理/規則改名就查不回「當時標的是哪個問題」。
   - 新增 `HandlingActions.IssueStatus = "issue_status"`（ActionText「標記問題」）與
     `HandlingActions.IssueStatusCleared = "issue_status_cleared"`（ActionText「清除標記」）。
   - `ApplyIssueStatus` 每處理**一個問題寫一列**：Status＝套用的問題狀態、
     Note＝該次填的說明、ActorAccount/ActorId/CreatedAt 照既有欄位。
     批次勾 10 項就是 10 列——「誰、何時、對哪個問題、標成什麼」每一筆都查得到，
     這正是 D4 要求的粒度；歷程本來就是 append-only，量不構成儲存問題。
   - timeline 顯示端做**視覺分組**避免灌版：同一操作者、同一秒（同一次批次）的
     issue_status 列在畫面上合成一個區塊——標題「Wayne 於 07-28 14:03 標記 10 個問題為
     「已處理」」，展開後逐問題列出 IssueLabel（資料是逐筆的，只有呈現在收合，
     與 D4 不衝突：點開就是完整明細）。
2. `HandlingDto` 增 `DerivedStatus`/`DerivedStatusText`/`TotalIssues`/`ClosedIssues`
   （`HandlingService.Get` 內用既有 `ComputeProgress` 算，需補撈 record）；
   處理面板頂部顯示「目前狀態（由問題標記推導）：處理中（3/5 已結案）」，
   與清單頁看到的完全同源；日層級表單的 chips 預選也改用推導值。
3. 批次套用成功的 toast 帶回結果：「已套用；本日狀態：處理中（3/5 已結案）」
   （後端 BatchIssueStatusResultDto 已回傳 DayStatus/Total/Closed，前端沒用而已）。
4. **既有缺口一併補**：指派/日層級變更已逐筆入歷程（含操作者與時間），不動；
   自動帶入處理人（AutoAssign）已有系統列，不動。

**影響檔案**：HandlingService.cs、RecordHandling.cs（RecordHandlingLog 欄位＋HandlingActions）、
SchemaUpgrader、HandlingDtos.cs（HandlingLogDto 加 IssueLabel）、
handling-panel.js（timeline 分組顯示）、record-detail.js（toast）。
**測試**：HandlingServiceTests 新增「單筆/批次標記逐問題寫入歷程（含 IssueLabel/Actor）」
「清除標記寫入歷程」「Get 回傳推導狀態」；GetLogs 映射補 IssueLabel。
**風險**：低中。lf_record_handling_log 加欄屬增量 schema 變更（SchemaUpgrader 既定模式）；
舊列兩欄為 null，timeline 顯示不受影響。

---

## #7 「處理」欄的 checkbox 與狀態拆成兩欄

**現況**：詳情頁重點問題表的「處理」欄同時塞 checkbox＋狀態文字＋預計完成日
（record-detail.js checkboxControl:323-362），且「不處理（預設）」「已知雜訊（自動）」
兩種列沒有 checkbox（不能參與批次套用）。

**方案**：
1. `issueColumns()` 拆成「選取」欄（僅 checkbox，`canHandle` 才出現）與「處理狀態」欄
   （狀態徽章/文字＋預計完成日＋預設不處理/自動雜訊的行內動作）。
2. 「選取」欄表頭放全選 checkbox（勾/取消當前分節可見列——批次套用的常見手勢）。
3. 預設不處理與自動雜訊列**也給 checkbox**：批次套用本來就允許覆蓋任何問題的狀態
   （後端 SetIssueStatusBatch 不區分），前端沒理由擋。原本的「確認不處理/調回未處理」
   行內快捷鈕保留在狀態欄。

**影響檔案**：record-detail.js。與 #8 同一個函式群改動，**與 #8 合併實作**。
**風險**：低，純前端。

---

## #8 已結案項目排到最下方並收合

**現況**：分節內問題列順序就是後端 TopIssues 順序，已處理與未處理混排；
處理完的日子進頁面還是滿版列表。

**方案**（範圍依 D2 拍板：**僅風險日詳情**，問題查詢清單維持緊急程度排序不動）：
1. 每個類別分節內排序：**未處理（含明確 open）→ 處理中 → 其餘（已處理/不處理/誤報/
   已知雜訊/預設不處理/自動雜訊）**；同組內維持原相對順序（後端已按重要度排）。
2. 「其餘」組收合：分節表格只渲染未處理＋處理中列，尾端加一列
   「已處理／已有結論 N 項　展開▾」toggle（renderTable 之外自組一個 tfoot 列或
   分節內第二張表），展開狀態不持久化（每次進頁預設收合）。
3. 「另有 N 項因嚴重度篩選未顯示」提示不變——收合的項目**有顯示計數**，
   不會與「沒看到＝不存在」的誠實原則衝突。
4. **計數器改三段（D3 拍板）**：`renderProgress` 從「已處理 N／未處理 M」改為
   「**已處理 N／處理中 O／未處理 M**」——
   - 已處理＝`handlingStatus === 'resolved'`；
   - 處理中＝`handlingStatus === 'in_progress'`（新增的一段）；
   - 未處理＝明確 open 或從未標記（且非預設不處理/自動雜訊），計法不變；
   - 不處理/誤報/已知雜訊/預設不處理/自動雜訊照舊三邊都不計（已有結論，不是待辦）。
   任一段為 0 時該段省略，避免「已處理 0／處理中 0／未處理 12」這種噪音。

**影響檔案**：record-detail.js、site.css（toggle 列樣式）。
**風險**：低。注意收合狀態要在 `renderIssues` 重繪（篩選切換/批次套用重載）間表現一致；
收合分組的依據（狀態）會被批次套用即時改變，重載後列會「搬家」到新分組屬預期行為。

---

## #9 檢查不必要的折行（詢問 AI 送出中折行等）

**現況與成因**：`withBusy` 會把按鈕內容換成「spinner＋文字」（ui.js:383-393），
Bootstrap 按鈕預設**不**禁止換行；聊天輸入列是 `d-flex gap-2`，輸入框 flex 撐滿、
送出鈕寬度僅容「送出」二字，變成「spinner＋送出中」後必然折行（RecordDetail.cshtml:48-52）。

**方案**：
1. site.css 加全站規則 `.btn { white-space: nowrap; }`——專案內沒有依賴按鈕文字換行的
   設計（需全頁掃一次確認），這條治本，所有 withBusy 按鈕（送出中/歸納中/儲存中/
   測試中/判讀中/套用中）一次解決。
2. `#chat-send` 補 `flex-shrink: 0`，避免 nowrap 後按鈕被 flex 壓縮出現省略破版。
3. 全站巡檢其他折行點（同回饋的「例如」語氣，代表要普查）：
   - 詳情頁表格「時段」欄 `HH:mm~HH:mm` 窄欄折行 → `white-space: nowrap`；
   - 清單頁處理狀態欄「徽章＋逾期＋N/M」的折行；
   - 報表 KPI 對比列「↑ 12%（前期 34）」。
   實作時以瀏覽器實測為準，逐一補 nowrap 或調欄寬。

**影響檔案**：site.css（主要）、必要時各頁微調。
**風險**：低；nowrap 全站套用後需巡檢一次窄視窗（tablet 寬）確認沒有按鈕撐破容器。

---

## #10 風險報告全文：點整列即可展開

**現況**：只有「▾ 風險報告全文」那顆 btn-link 可點（RecordDetail.cshtml:58-63），
header 右側與空白區點了沒反應；複製/列印鈕在同一列。

**方案**：click handler 從 `#report-toggle` 移到整個 `.lf-card__header`；
複製/列印按鈕 `stopPropagation`；header 加 `cursor: pointer` 與 hover 底色
（沿用既有 clickable 視覺）；toggle 元素補 `aria-expanded` 同步。

**影響檔案**：record-detail.js（setupReportToggle）、RecordDetail.cshtml、site.css。
**風險**：無。

---

## #11 詳情頁顯示高風險、但問題最高嚴重度只有中

**現況與成因確認**：日風險等級與問題嚴重度是**刻意分開的兩套層級**
（RiskLevels.cs:7-9），「高風險日但看不到高嚴重度問題」有四種真實路徑：

1. **AI 上調**：AI 的 risk_level 只能把程式判定往上拉（LogAnalysisService.cs:264
   `MoreSevere`）——中風險的規則判定被 AI 拉成高，最可能是使用者遇到的情境。
2. **Critical 關聯訊號**：關聯鏈本身 Critical → 日=高，但關聯訊號不在重點問題表裡
   （在右側「程式偵測訊號」卡）。
3. **顯示設定隱藏**：SiteHidden 模式會把未勾選層級的問題從詳情頁過濾掉，
   但「風險等級判定不受顯示設定影響」是明文設計（SystemSettings.cs:26）——
   造成高風險的那條 Critical/High 問題可能根本沒顯示。
4. **抑制**：問題事後被抑制，舊紀錄的風險等級不回改（證據層）。

這不是 bug，但畫面沒有解釋，使用者的困惑合理。**若 D1 採 B1**，路徑 2 的來源
會顯性化成規則旗標，可解釋性大增。

**方案**（與 D1 的選擇無關皆可做）：
1. `DailyAnalysisRecord` 新增 `RiskBasis`（string，批次寫入判定依據代碼：
   `rule`（含旗標規則 id）/`correlation`/`trend`/`ai_raise`＋程式判定等級），
   SchemaUpgrader 加欄位；舊紀錄為空。
2. 詳情頁 header 的風險徽章旁顯示判定依據小字/tooltip：
   「高風險：AI 判讀上調（程式判定：中）」「高風險：磁碟故障規則命中」等；
   舊紀錄無 RiskBasis 時顯示通用說明
   （「日風險由規則命中＋趨勢＋關聯訊號＋AI 判讀綜合判定，與單一問題嚴重度非同一套層級」）。
3. SiteHidden 模式且當日有問題被過濾時，詳情頁補一行
   「部分問題已依全站顯示設定隱藏；風險等級以完整資料判定」（後端已知 hidden 數量
   才能顯示——RecordRepository 過濾時帶出 `hiddenIssueCount` 到 detail DTO）。

**影響檔案**：DailyAnalysisRecord、LogAnalysisService.cs、SchemaUpgrader、
RecordRepository、RecordDtos.cs、RecordQueryService.cs、record-detail.js。
**測試**：RiskReportServiceTests/儲存契約測試補 RiskBasis 欄位。
**風險**：中低。批次寫入新欄位屬增量 schema 變更，SchemaUpgrader 已有既定模式可循。

---

## #12 處理狀態對外一律三態：未處理／處理中／已處理

**現況**：狀態值域六種（open/in_progress/resolved/wont_fix/false_positive/known_noise）。
問題查詢清單、CSV、儀表板會直接露出「不處理/誤報/已知雜訊」徽章（format.js:68-75）；
日層級 fallback 為 wont_fix 等狀態的日子，清單「已處理」chip 查不到
（RecordQueryService.cs:147 精確比對）、報表處理進度的「未完成」下鑽也對不上
（GetTodo 只數 resolved）。

**方案**（單一事實來源收斂，比照 SHARED-STANDARDS 手法）：
1. Core `HandlingStatuses` 新增 `ExternalOf(status)`：open→open、in_progress→in_progress、
   **其餘（resolved/wont_fix/false_positive/known_noise）→ resolved**；
   加註「對外檢視三態；詳細結論只在詳情頁呈現」。
2. 後端套用點：
   - RecordQueryService：清單 DTO 的 `HandlingStatus`/`HandlingStatusText` 走 ExternalOf
     （文字＝未處理/處理中/已處理）；`Statuses` 篩選改比對 ExternalOf——
     「已處理」chip 從此涵蓋全部結案類；
   - DayHandlingDerivation fallback 出來的日層級狀態在**對外出口**正規化（推導本身不動）；
   - HandlingService.GetTodo：ResolvedCount 改數 ExternalOf==resolved（報表處理進度、
     儀表板 KPI 自動一致）。
3. 前端：format.js `handlingBadge` 收斂為三態（wont_fix/false_positive/known_noise
   併入「已處理」success 徽章）；**詳情頁不受影響**——問題列的狀態文字走
   issue 層級的 `handlingStatusText`（已處理/不處理/誤報/已知雜訊照舊詳列），
   處理面板的狀態 chips 也照舊六選——「只有 detail 頁才詳細說明處理方式」。
4. CSV 匯出、儀表板下鑽 URL（statuses=open,in_progress）語意不變但結果變準
   （wont_fix 日不再漏在「未完成」之外）。

**影響檔案**：RecordHandling.cs（HandlingStatuses）、RecordQueryService.cs、
HandlingService.cs、format.js、（檢查 dashboard.js/audit 顯示點）。
**測試**：HandlingServiceTests、RecordQueryServiceSearchTests 加三態映射與篩選 case；
既有斷言「不處理」文字的測試改「已處理」。
**風險**：低中。語意變更點要跟使用者確認一件事：清單上「已處理」徽章從此
**包含**不處理/誤報/已知雜訊（hover title 可帶原始結論，滑鼠移上去仍看得出細節）。

---

## #13 處理歷程固定高度＋modal 放大檢視

**現況**：詳情頁右欄的處理歷程卡（RecordDetail.cshtml:83-88）把 timeline 全量渲染，
沒有高度上限——歷程一長就把下方的「程式偵測訊號」「類型分布」「資料涵蓋率」推到
很深的位置。**#6 依 D4 改為逐問題逐筆記錄後，歷程只會更長**，本項是它的配套。

**方案**：
1. **卡片內限高**：site.css 對 `#handling-log` 設 `max-height`（約 320px，
   接近 4～5 筆的高度）＋ `overflow-y: auto`；右欄版面高度從此可預期。
2. **ui.js 新增通用資訊 modal helper**：`showDetailModal({ title, body, size })`——
   動態組 DOM、關閉即銷毀，骨架抽自既有 `confirmAction`（ui.js:165-197 已是同一套
   動態 modal 寫法，抽共用避免第三份複本）；`body` 收 DOM 節點（不是 HTML 字串，
   維持 S7 純文字組裝原則）、`size` 支援 `modal-lg`。**#14 共用同一個 helper**。
3. **放大檢視**：歷程卡 header 加「放大檢視」按鈕（icon＋文字，lf-no-print），
   點擊開 `modal-lg` 顯示完整歷程。`loadLogs` 拆成「取資料」與
   `renderTimeline(container, logs, { expanded })` 兩段——卡片內與 modal 內
   渲染同一份已載入的 logs，不重打 API；
   D4 的批次視覺分組在卡片內**預設收合**、modal 內**預設展開**
   （會開 modal 的人就是要看逐筆細節）。
4. 空歷程（「尚無處理紀錄」）時不顯示放大按鈕。

**影響檔案**：handling-panel.js、RecordDetail.cshtml、site.css、ui.js。
**風險**：無後端變動。注意 modal 開著時若批次套用重載頁面（onBatchSaved → load()），
modal 資料是舊的——關閉即可，不做即時同步（modal 是快照檢視）。

---

## #14 「範例訊息」名稱不明＋內容擠在一起

**現況**：詳情頁重點問題「說明」欄的「範例訊息」btn-link（record-detail.js:647-664），
hover/focus 觸發 Bootstrap popover，內容是 `sampleMessages.join('\n---\n')` 純文字。
site.css 其實已設過寬度與捲動（`.lf-sample-popover` max-width min(640px, 90vw)、
pre-wrap、限高 240px，site.css:957-965），但實際仍窄擠——實作時需實測確認成因
（popover 被 Popper 定位空間壓縮，或 `--bs-popover-max-width` 變數未生效），
不過**方案直接繞開 popover，不依賴查明**。名稱問題屬實：「範例訊息」看不出
指的是「這個問題實際觸發的 Windows 事件原始訊息樣本」。

**方案**：
1. **改名**：觸發鈕文字改「原始訊息 N 則」（如「原始訊息 3 則」），
   title/aria-label：「這個問題實際觸發的事件訊息樣本，供比對確認」。DTO 不變
   （sampleMessages 既有欄位）。
2. **互動改點擊開 modal**（共用 #13 的 `showDetailModal`，`modal-lg`）：
   - 每則訊息獨立一個區塊（等寬字型、pre-wrap），區塊間用邊框分隔——
     取代現在把 `---` 當分隔字串塞進同一段文字的做法；
   - modal 標題「原始訊息（Source EventId，共 N 則）」；
   - 事件訊息是攻擊者可控字串，維持 `textContent` 純文字組裝，不解析 HTML（S7）；
   - modal 寬度不受 popover 定位/max-width 限制，寬擠問題徹底解決，
     內容長也有完整捲動空間。
3. **移除 hover popover**：click 與 hover 兩套並存會曖昧（hover 看一半點下去變 modal），
   且省掉表格重繪時 `bootstrap.Popover` 實例殘留的隱患；
   `.lf-sample-popover` 樣式無其他使用處，一併刪除。

**影響檔案**：record-detail.js、ui.js（同 #13 的 helper）、site.css（刪 popover 樣式、
加訊息區塊樣式）。
**風險**：無。互動從 hover 變 click 是行為變更，但原本 trigger 本來就含 focus/click
（點擊維持顯示），使用者手勢相容。

---

## 與既有文件的同步

- WEB-SPEC.md §8.3（圖卡工具列含 PNG）、§8.2（嚴重度色彩四級）需隨 #3/#4/#1 修訂。
- SHARED-STANDARDS-PLAN.md S11（SEVERITY_ORDER）若採 B1 需同步。
- 完工後本文件標頭改「全部實作完成」並記錄與規劃的偏差，沿用前輪慣例。
