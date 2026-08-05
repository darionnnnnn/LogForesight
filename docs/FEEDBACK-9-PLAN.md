# 回饋第九輪規劃（2026-08-05）

十項回饋＋文件整理（§11）＋appsettings 精簡（§12）＋NetIQ 連線預設（§13）的規劃明細。
**本文件僅規劃，尚未實作**。方向性決策已於 2026-08-05 與使用者確認：**§1 採方案 A、
§5 採單選顯示範圍、§6 本輪做甲＋乙另立案排下一輪、§11 CLAUDE.md 與 README 瘦身都做、
§12 AI 進階參數移設定頁／Ui 區段刪除／Auth:Ldap 退役、§13 依規劃執行**（見文末決策紀錄）。

分支依既定流程：自 `dev` 開 `feature/feedback-9`，完成後併 `dev` 實測、確認無誤才併 `master`。
注意：`dev` 目前領先 `master`（架構重構＋UI v2 皆待實測），本輪會疊在其上。

---

## §1 appsettings 預設帳號只能新增 admin 帳號，其他功能都不能用

### 現況與根因

`svc-lfadmin` 是 **serverAdmin 本地救援帳號**，能力刻意只給 `Maintain`＋`ViewAudit`
（`RoleCapabilityMap.ForServerAdmin()`，docs/WEB-SPEC.md §6.2 設計定案：「依用途給權，不是萬能帳號」）。
`VisibilityService` 對 serverAdmin 回**空主機集合**——所以儀表板／問題查詢／報表／風險日詳情
全部空白，處理與指派也不可用。這不是 bug，是設計；但「開箱即測」的實際體驗是：
登入預設帳號後除了維護頁什麼都看不到，與 appsettings 註解宣稱的「開箱即可登入」落差很大。

維護頁其實不只「新增帳號」：規則、主機、群組、匯入、NetIQ、排程、稽核都可用；
「只能新增 admin 帳號」的觀感來自「業務資料頁全空」。

### 方案（三選一，建議 A）

- **A（建議）：Stub 模式自動 seed 測試 admin＋serverAdmin 引導卡。**
  - `Auth:Provider=Stub`（僅測試環境；正式環境用 Stub 會被啟動擋下）時，啟動 seed 一個
    測試使用者（如 `demo-admin`／顯示名稱「測試管理員」）＋一個 admin 角色使用者群組，冪等
    （已存在同帳號即跳過）。Stub 不驗密碼，seed 完即可直接登入測全站功能。
  - serverAdmin 登入後，儀表板位置顯示引導卡：說明此帳號用途（維護與救援、不看業務資料）、
    測試模式下可改用 `demo-admin` 登入、正式環境的建帳號步驟。
  - 優點：不動 §6.2 權限模型（正式環境救援帳號維持最小權限），只修「開箱體驗」。
  - 影響：`Program.cs`（seed，僅 Stub 分支）、儀表板前端（serverAdmin 判斷已有 `IsServerAdmin`
    claim 可用，`/api/auth/me` 需帶旗標——先確認現有 DTO）、appsettings 註解、README、WEB-SPEC §5/§6.2。
- **B：serverAdmin 直接給 Admin 全能力。** 開箱最方便，但正式環境的本地救援帳號（密碼在
  設定檔雜湊）變成可看可改全部業務資料的萬能帳號，違反 §6.2 定案，稽核面最差。不建議。
- **C：serverAdmin 加 `ViewAll`（唯讀業務資料）。** 折衷；但處理／指派流程仍測不了，
  「開箱測整站」的目的只達成一半，且一樣動到 §6.2。除非使用者明確想要，不建議。

### 測試

seed 冪等測試、Ldap 模式不 seed、serverAdmin 能力集不變（既有測試已涵蓋）。

---

## §2 執行總表：點日期改為就地展開（不再跳到頁面最下方）

### 現況

`runs.js` 的執行總表每日一列，點日期呼叫 `showDayDetail()`——把結果 render 進頁面下方的
`run-day-detail-card` 並 `scrollIntoView`。使用者要在總表與最下方之間來回跳，動線差。

### 方案

改用 `renderTable` 的列展開機制，在**該日期列正下方**展開當日逐主機明細：

1. **`ui.js`／`renderTable` 擴充懶載入展開**：現有 `rowDetail` 是進頁即建好 DOM（eager）。
   新增選項 `onRowExpand(row, container)`——首次展開該列時才呼叫（懶載入），之後展開／收合
   直接重用。既有 `rowDetail` 呼叫端行為完全不變（向後相容），只是多一條路。
   14 天 × 2000 台的明細絕不能進頁全抓，懶載入是必要條件。
2. **`runs.js`**：總表列掛 `onRowExpand`，展開時 fetch `/api/runs/day/{date}` 並把現有
   `renderDayDetail`（含排序＋分頁＋每頁筆數）改造成「per-date 實例」render 進展開列
   （每個日期的排序/分頁狀態獨立，允許同時展開多天）。日期文字不再需要是 `<a href="#">`。
3. **移除** `Runs.cshtml` 的 `run-day-detail-card` 區塊與關閉鈕、`runs.js` 對應程式。
4. **順手一致化（建議一併做）**：「檢視執行」的 `run-detail-card` 有一樣的「跳到最下方」問題，
   改用共用 `showDetailModal`（執行詳情 stats＋log 清單放 modal-xl），移除底部卡片。
   若想控制範圍也可本輪不動，只做日期展開。

API 不動。風險低；`renderTable` 是全站共用元件，改完要走查各清單頁不受影響。

---

## §3 立即執行回補三天，其他日期卻顯示「未執行」

### 現況與根因

執行總表的日期歸屬邏輯（`RunMonitorService`）：

- **本機主機**：某日期的狀態只看「那天有沒有 `BatchRun`（`StartedAt` 落在當天）」。
- **NetIQ 主機**：沒有逐台 BatchRun，改用代理指標「date−1 的分析紀錄存在＝success」。

立即執行回補時，本機的缺漏日分析紀錄會寫到**被補的那些日期**（最多到昨天），但 BatchRun
只登記在觸發當天——於是資料明明補齊了，總表上其他日期仍顯示「未執行」。NetIQ 主機反而
因為代理指標看得到回補結果。這是本機路徑缺 fallback 的呈現落差，不是資料問題。

另一個隱藏誤導：立即執行 modal 的「回補天數」欄位**只覆寫 NetIQ 的 BackfillDays**
（`RunRequest.BackfillOverride` → `netiqOptions.BackfillDays`）；本機主機一律自動回補
趨勢窗口內的缺漏日，與此欄位無關。說明文字沒講，使用者自然以為欄位管本機。

### 方案

1. **本機主機補 record-fallback 狀態「已回補」**：`GetDaySummaries`／`GetDayDetail` 中，
   本機主機某日沒有任何 BatchRun 時，改查該主機 date−1 的分析紀錄（`ListHostDates` 已有，
   目前只餵 NetIQ，把查詢範圍放寬到全部主機即可，同一次查詢不加額外成本）；存在則標
   新狀態 `backfilled`（顯示「已回補」，淺綠），不存在才是「未執行」。
   - 刻意**不**標成 success：「當天真的有跑」與「後來補的資料」要分得出來，這符合本頁
     「沒跑看起來跟沒事一模一樣」的誠實原則。
   - NetIQ 主機維持現狀（它只有代理指標，全改「已回補」會讓 success 失去意義）。
2. **圖例**加「已回補」一色（`STATUS_META`）；摘要列多一欄或併入成功欄？——建議獨立一欄
   「已回補」，欄位已多（9 欄），可把「已停止」「異常中斷」等低頻欄維持現狀，僅加一欄。
3. **modal 文案修正**：「回補天數」說明改為「僅影響 NetIQ 主機的回補窗口；本機主機每次執行
   都會自動回補趨勢窗口內的缺漏日，不受此欄位影響」。

### 測試

`RunMonitorService` 單元測試：本機主機無 BatchRun＋有 date−1 紀錄 → `backfilled`；
無紀錄 → `none`；有 BatchRun 時不走 fallback。

---

## §4 報表一頁化＋跨主機同簽章查詢去留

### 簽章查詢：建議自報表頁移除，併入問題查詢

「跨主機同簽章查詢」＝輸入 Event ID（＋來源）列出出現過的主機×日期。問題查詢**已完整涵蓋**：
明細視角支援 `eventId` 篩選（表單本來就有 Event ID 欄）、「依問題」視角就是簽章彙總、
點列可下鑽、admin 還能直接指派——功能面是嚴格超集，只差兩點：

- 問題查詢受日期區間限制；簽章查詢刻意查全保留期。→ 使用者在問題查詢把區間拉大即可，
  或按「清除條件」後選近 90 天；可在問題查詢的 Event ID 欄說明中補一句。
- 問題查詢的「來源」目前只能由下鑽帶入。→ 篩選列**補一個「來源」輸入欄**（小改動）。

結論：報表頁移除該卡（報表回歸「彙總與趨勢」單一職責，也是一頁化的前提）；
WEB-SPEC §9.6 同步更新。若使用者仍想保留入口，可在報表頁尾放一行連結導向問題查詢。

### 一頁化版面

目標：1920×1080 桌面、預設圖表全開時，KPI＋六張圖表一屏內完整呈現，頁面不出現垂直捲軸
（lg 以下斷點自然堆疊、允許捲動；列印沿用現有樣式）。

- 版面由「KPI 列＋兩列圖表＋簽章卡」改為：
  - 列 1：KPI 四卡（現狀）
  - 列 2：告警數量趨勢（col-6）＋風險類型分布（col-6）
  - 列 3：主機告警排行（col-6，從整列改回半寬）＋三顆占比小圖（col-6 內部三欄）
- `.lf-chart` 高度 280px → 約 220~240px（實測調整）；`.lf-chart--sm` 160px 維持。
- 主機告警排行從整列縮半寬後，Top 10 的橫條 label 空間變小——實測若擠，排行上限
  Top 10 → Top 8（後端 `HostRankingLimit`），或僅圖面截短 label 補 tooltip。
- 自訂圖表 modal、隱藏圖表的行為不變；隱藏部分圖表時版面自動收緊（Bootstrap grid 原生行為）。

---

## §5 報表加處理狀態顯示範圍

「隱藏已處理」「隱藏處理中」「僅顯示未指派」三個獨立 checkbox 彼此重疊
（「僅未指派」蘊含前兩者），依題意重新設計勾選邏輯：

### 建議：單選「顯示範圍」segmented control（非三顆 checkbox）

期間列右側加一組 chip 單選（同站內既有 chip 樣式）：

| 選項 | 語意（日層級推導狀態，與儀表板待辦同源） |
|---|---|
| 全部（預設） | 現行為，完全不過濾 |
| 未結案 | 排除已處理（resolved／wont_fix 等已結案態）的風險日 |
| 未處理 | 再排除處理中——只看還沒人動的 |
| 未指派 | 只看沒有處理人的風險日 |

單選讓每個數字都有明確母體，避免 checkbox 組合出「隱藏已處理＋僅未指派」這種語意不明的狀態。

### 實作

- `/api/reports/summary` 加 `handlingScope=all|unresolved|open|unassigned`（預設 all）。
- `ReportService.GetSummary`：查回 records 後，以 `DayHandlingDerivation`（與
  `HandlingHistoryQueryService.GetTodo` 同一套規則，不另發明第二份語意）為每筆風險日推導
  日層級狀態與是否已指派，**先過濾再聚合**——KPI、趨勢、類型分布、排行、占比全部反映同一範圍；
  前期對比（previousRecords）套同一 scope，「變好變壞」才可比。
- 涵蓋率缺口 KPI 照樣隨母體過濾（報表呈現的就是「篩選後的世界」，不做例外）。
- scope≠all 時「處理進度」小圖隱藏（母體被抽掉已處理後恆 0%／100%，沒有資訊量）。
- 各下鑽 URL 依 scope 附帶對應 `statuses=`／未指派條件，點進去的清單筆數與卡片數字對得上；
  「未指派」下鑽需要問題查詢支援（見 §10 的 by-issue／明細「未指派」條件，兩案共用同一參數）。
- scope 選擇存 URL（可分享）＋不入 localStorage（報表以「全部」為誠實預設）。

### 測試

ReportService scope 過濾單元測試（四種 scope × KPI/趨勢筆數）；前期對比同 scope。

---

## §6 依問題一次性指派的設定方式評估

### 現況

依問題視角已有「指派」鈕（FEEDBACK-4 §4）：開 modal 預覽受影響主機（已由他人案件涵蓋的
標示且預設不勾）、選處理人＋說明＋預計完成日、送出後逐主機建**案件**。案件制天然涵蓋
「之後新增的風險日自動掛進案件」，所以「同一問題交給某人持續處理」的核心語意已成立。

### 「一次性」的兩種解讀（2026-08-05 定案：甲本輪做、乙立案排下一輪）

- **甲：一次動作把目前所有受影響主機指派給同一人**——已實作，本輪做體驗補強。
- **乙：常設規則「此簽章之後永遠自動指派給某人」**（連未來新出現的主機也自動掛）——
  使用者確認要做，但成本約一整輪（新規則儲存＋排程掛接點＋維護 UI＋停用/審計），
  **另立案排入下一輪**，本輪先把概要記進 BACKLOG。概要草案：
  - 儲存：`auto_assign_rules`（簽章 key → handlerId，含啟用旗標、建立者、備註）；
  - 掛接點：`HostDayPostProcessor.AttachCase` 之後——當日新問題比對規則命中且該主機
    無進行中案件時自動建案（沿用現有案件制，不發明第二套同步機制）；
  - UI：批次指派 modal 加一顆勾選「之後新出現的主機也自動指派給此人」即建立規則；
    規則清單與停用放在規則維護頁或使用者維護頁（下一輪定）；
  - 邊界：他人已有進行中案件不搶走（與現行批次指派一致）、規則命中寫稽核。

### 本輪補強（解讀甲）

1. 處理人選擇改用 §8 的可搜尋選單（帳號＋顯示名稱關鍵字過濾）。
2. 受影響主機清單加「全選／全不選」與主機名關鍵字過濾（台數多時目前只能逐台勾）。
3. 清單與提示文字統一 `使用者名稱(帳號)` 格式（§9）。
4. modal 內補一句既有語意的說明：「指派會建立案件；這些主機之後同問題的新風險日會自動
   掛進案件並同步狀態，直到結案」——把「一次性 vs 持續」講清楚，正面回答本項疑問。

---

## §7 儀表板「風險類型」避免換行空洞

### 根因

`.lf-category-grid` 為 `repeat(auto-fit, minmax(13rem, 20rem))`：欄寬上限 20rem 固定，
類別數少或容器更寬時右側留大片空白；換行後末列卡片也不會伸展，觀感「缺乏內容」。

### 方案

- `minmax(13rem, 20rem)` → `minmax(13rem, 1fr)`：auto-fit 摺疊空軌後，卡片平均撐滿整列，
  換行時每列都填滿，不再有右側空洞。類別最多 8 個（固定值域），最壞情況 8 卡兩列 4+4，正常。
- 卡片極少（1~2 個）時會偏寬——可接受（資訊密度低本來就該讓卡片大）；若實測觀感不佳，
  備選：外層容器加 `max-width` 或卡片內容改橫向排列（數字＋嚴重度同列）讓寬卡不顯得空。
- 純 CSS 一行改動，走查亮/暗色與 lg/md/sm 斷點即可。

---

## §8 處理人下拉前加關鍵字篩選

### 方案：共用「可搜尋使用者選單」元件

`ui.js` 新增 `searchableUserSelect(users, { selectedId, includeNone, pinnedNames, onChange })`：

- 文字框＋下拉清單（沿用主機 autocomplete 的 dropdown 樣式）；輸入即前端過濾，
  比對**顯示名稱＋帳號**（不分大小寫）；選項文字一律 `formatUserName()`。
- 使用者清單已由呼叫端一次載入（`/api/admin/users`），純前端過濾即可，不需新 API。
- 保留既有行為：「（未指派）」選項、負責人置頂並標示「（負責人）」。

### 套用點

1. 風險日詳情處理面板 `assignField`（本項主訴求）。
2. §6 批次指派 modal 的處理人選單（同元件，不寫第二份）。

純前端，無 API／DB 改動。測試以手動走查為主（現有處理流程測試不受影響）。

---

## §9 使用者資訊統一顯示 `使用者名稱(帳號)`

規則本身在第八輪 #6 已定（前端 `formatUserName()`／後端 `NameFormat.FormatAccount()`），
本輪是**全站盤點補漏**。已盤到的缺口：

### 前端（DTO 缺顯示名稱，需後端補欄位）

| 位置 | 現況 |
|---|---|
| `permission-changes.js:104` 確認者 | 只顯示帳號（`confirmedByAccount`）→ DTO 補 displayName |
| `imports.js:230` 匯入紀錄操作者 | 只顯示帳號 → DTO 補 displayName |
| `netiq.js:240` NetIQ 設定更新者 | 只顯示帳號 → DTO 補 displayName（`ScheduleOptionsDto` 已有前例可循） |

### 後端（只回 DisplayName，前端拿不到帳號）

`HandlerName`／`ownerNames` 家族：`RecordListQueryService:616`（明細清單處理人欄，
含「（案件）」後綴的組字）、`RecordDetailQueryService:200/363`（處理歷程、問題列處理人）、
`DayHandlingCommandService:319`（指派 toast 的「已指派給 X」）、
`IssueHandlingCommandService:319/332`（「已由 X 處理中」、批次指派回應）、
處理面板的主機負責人清單、主機維護頁的負責人欄。

**策略**（沿用第八輪定案，不發明新規則）：

- 清單／欄位型輸出：DTO 補 `*Account` 欄位，前端 `formatUserName()` 格式化（單一格式出口）。
- 後端組好的敘事字串（toast 訊息、彙總文字）：改走 `NameFormat.FormatAccount()`。
- CSV 匯出欄與報告全文中的人名一併帶格式。
- **刻意不改**：使用者維護頁的「帳號」「顯示名稱」分欄（本來就是明細表格，合併反而退化）；
  匯入器的「(未知:{id})」等既有回退措辭（NameFormat 註解已載明是刻意差異）。

驗收：grep 盤點（`handlerName|displayName|account` 的 render 出口）＋逐頁人工走查。

---

## §10 問題查詢以「依問題」為預設，強化問題角度動線

### 預設視角

主機量大後「依主機／依日期」單獨看都難以下手，「依問題」才回答「現在環境裡有哪些問題、
誰在處理」。但不能無腦把預設改成 issue——全站大量下鑽連結（報表 KPI、儀表板卡片、圖表
drillTo）都是**不帶 `view` 參數**的明細語意連結（帶 `statuses`／`severity` 等明細專屬條件），
預設改 issue 會讓這些連結全部落到不支援其條件的視角，數字對不上。

**規則**：URL **完全沒有查詢參數**（使用者從側欄直接進頁）→ 預設 `issue`；
帶任何查詢參數→ 維持現行預設 `detail`（下鑽連結零改動、零風險）。規則簡單、可測、可解釋。
「清除條件」按鈕導向無參數網址，自然回到依問題視角，行為一致。

### 依問題視角補處理狀態篩選

現況 status chips 在彙總視角一律停用。本輪讓 **by-issue 支援**：

- `/api/records/by-issue` 加 `statuses`（映射到問題層級「處理概況」三態：未處理／處理中／已處理，
  group 建好後 post-filter，與現有 `BuildIssueGroup` 的三態推導同源）與 `unassigned=true`
  （`handlers` 空）。§5 報表「未指派」下鑽即連到這裡，兩案共用參數語意。
- `setActiveView`：issue 視角啟用 status chips（tooltip 說明此處篩的是「處理概況」）；
  chips 旁加一顆「未指派」chip（僅 issue 視角顯示）。依主機／依日期維持停用。

### 問題角度的處理動線

- **admin 指派**：列內「指派」鈕維持＋§6 的 modal 補強。
- **使用者（Handle）處理**：點列展開（`renderTable` 的 `onRowExpand`，與 §2 同一機制）
  顯示該問題「受影響主機×最近日期」清單（重用 `issue-cases/preview` 端點或等價查詢，
  以可見範圍過濾），每列直連該主機該日的風險日詳情並帶 `issueKey` 高亮——把「看到問題 →
  去處理它」從目前的「下鑽明細→再點日期→再找問題」三步縮成一步。
  - **跨主機×跨日期的批次「標記狀態」**（在 issue 視角直接把一個問題標已處理）刻意
    **不在本輪做**：需要新的跨日批次 API 與案件/雜訊記憶的複雜互動，且多數結案情境
    已由案件同步涵蓋。留待實測後評估。
- 篩選列補「來源」輸入欄（§4 簽章查詢併入的配套）。

### 測試

by-issue statuses/unassigned 過濾單元測試；預設視角規則（無參數 issue／有參數 detail）
前端邏輯簡單，以手動走查＋既有 URL 同步測試涵蓋。

---

## §11 專案文件整理（降低 token 消耗）

### 現況盤點

docs/ 的基本秩序其實已經在：WEB-SPEC 是 Web 唯一事實來源（1,416 行）、完成的規劃案已歸檔
`docs/archive/`（HISTORY.md 3,624 行＋歷輪 FEEDBACK-N-PLAN，只按需讀取）、BACKLOG 已收斂
散落的待辦。真正的 token 消耗來源是：

1. **沒有 CLAUDE.md**——每個新 session 都要重新探索專案結構、慣例、測試方式，
   重複掃 README 與 WEB-SPEC 的大段內容。這是最大也最便宜的節省點。
2. **README 995 行**——入口文件混入深度規格：危險訊號清單（約百行）、趨勢判定規則、
   RDP 誤報設計、小模型效能策略（約 40 行）等。任何人（或 AI）想「先了解專案」
   就得吃下近千行，其中多數與當下任務無關。
3. WEB-SPEC 行數大但**不建議拆**：程式碼註解大量引用其 §編號，拆檔引用全斷，
   拆分收益遠小於斷鏈成本。

### 方案

1. **新增根目錄 `CLAUDE.md`（約 60 行，入口地圖不是百科）**：
   - 一句話定位＋專案結構速覽；
   - 文件地圖：「做什麼事讀哪份文件哪一節」（改 Web → WEB-SPEC §N；改規則 → RULES-SPEC；
     查歷史決策 → archive/HISTORY.md……）；
   - 關鍵慣例：分支流程（feature → dev 實測 → master）、`dotnet test` 與目前測試數、
     說明文字用台灣繁中、完成的規劃案歸檔 archive/；
   - 明確的「不要做」清單（例如不要把規劃內容寫回 README、不要拆 WEB-SPEC）。
2. **README 瘦身至約 250 行**：保留定位、專案結構、架構圖、使用方式、部署驗證；
   深度內容**原樣搬移**（不改寫）到新檔 `docs/DETECTION-SPEC.md`（五層偵測、危險訊號
   清單、趨勢/關聯規則、AI 輔助資訊、小模型策略），README 留一行連結。
   危險訊號清單與 RULES-SPEC 有部分重疊——刻意不合併（一份講「偵測什麼」、
   一份講「規則機制怎麼運作」），搬移時加互指連結。
3. **歸檔紀律明文化**（寫進 CLAUDE.md）：規劃案完成即移 `docs/archive/`；
   本輪 FEEDBACK-9-PLAN 完成後同樣歸檔。
4. **不動**：WEB-SPEC／DB-SPEC／RULES-SPEC／LINUX-RULES／NETIQ-API-REFERENCE／
   DESIGN-SYSTEM／BACKLOG（各司其職、無明顯重疊）；archive/ 全部維持原位。

### 風險

README 內容搬移會讓既有引用斷鏈——搬移後全案 grep 修引用（程式碼註解引用 README
特定章節的地方不多，需逐一盤點；文件間連結一併修）。

---

## §12 appsettings.json 精簡：能進設定頁的移進設定頁

### 原則

appsettings.json 只保留**啟動與安全前提**——站台還沒起來、DB 還沒連上之前就必須知道的事。
其餘可調整項目一律以「系統管理 > 設定」（DB `SystemSettings`）為事實來源。
已在設定頁有對應項的，appsettings 退路直接移除。

### 逐區段盤點與處置

| 區段 | 處置 | 理由 |
|---|---|---|
| `Storage` | **留** | DB 的位置不能存在 DB 裡，啟動前提 |
| `Jwt` | **留但精簡** | 簽章金鑰屬安全基礎設施不入 DB；json 檔只留 `SecretKey`（＋必要時 `ExpireHours`），`Issuer`/`Audience`/`CookieName` 改吃程式碼預設值、自 json 移除 |
| `Auth` | **留但收斂**（2026-08-05 定案） | 登入鏈不能依賴 DB 可用性（救援帳號正是 DB／AD 壞掉時的入口），`ServerAdmin` 整組保留；**`Ldap` 區段退役**——AD 驗證統一以設定頁（`AdAuthEnabled`/`AdServers`）為單一事實來源，`Provider` 值域縮為 `Stub`（測試）＋設定頁 AD；`LdapAuthenticationProvider` 舊路徑移除。升級相容：啟動偵測到舊 `Ldap:Domain` 有值且 DB AD 未設定時，log 警告提示遷移（不擋啟動） |
| `Ai` | **移除區段** | `BaseUrl`：設定頁已是事實來源，且 DB 預設值同為 `http://localhost:8080`，退路值＝預設值，移除後行為不變。節流與進階參數（`TimeoutSeconds`/`RetryCount`/`RetryDelaySeconds`/`JsonRetryCount`/`MaxTokens`/`DeepDiveMaxTokens`/`FrequencyPenalty`/`PresencePenalty`/`ExtraRequestFields`）遷移至 `SystemSettings`＋設定頁「AI 服務」新增**進階參數**折疊區（`ExtraRequestFields` 以 JSON 文字欄編輯＋儲存時驗證）。**遷移零成本的關鍵**：新欄位的程式碼預設值直接採用現行 appsettings.json 出廠值（含 `rep_pen: 1.3`、penalty 0.8 等），舊部署升級後行為不變 |
| `Permissions:WatchedFolders` | **移設定頁** | `SystemSettings` 加欄位；設定頁新「分析參數」分節，一行一路徑編輯 |
| `Analysis`（`ServerDescription`/`CheckupIntervalDays`/`Channels`） | **移設定頁** | 同上「分析參數」分節；`Channels` 以 `ChannelCatalog` 已知頻道 checkbox＋自訂輸入呈現 |
| `Import`（`MaxFileSizeKb`/`MaxRows`） | **移設定頁** | 併入「保留天數與限制」分節 |
| `Ui:DefaultPageSize` | **直接刪** | 盤點無任何消費端——「有設定無行為」是本專案紅線 |
| `Ui:DashboardDefaultDays`/`RunMatrixDays` | **改常數，區段整刪**（2026-08-05 定案） | API 的 fallback 預設，前端各自另有 localStorage 記憶；寫死 7 天/14 天常數，`Ui` 區段連同 `DefaultPageSize` 整個移除 |
| `Netiq:DiscoveryClient` | **移除** | 改為 NetIQ 維護頁開關，見 §13 |
| `AllowedHosts` | **留** | ASP.NET 框架必要 |

### 實作要點

- 批次執行取值鏈：`RuntimeSettingsResolver.ApplySystemSettingsOverrides` 擴充——排程／立即執行
  組 `AppSettings` 時，AI 進階參數、監控資料夾、分析參數全部改由 DB 覆寫（原本只覆 AI 位址/金鑰）。
- `SystemSettingsService.Update` 補各新欄位驗證（逾時/重試範圍、JSON 格式、頻道名可解析、
  匯入上限範圍）；設定頁 `settings.js` 增列對應分節。
- `WebAppSettings.Validate` 同步刪除已移除區段的檢查；appsettings.json 檔頭註解全面改寫
  （只剩 Storage/Jwt/Auth 的說明，篇幅大幅縮短——本身也呼應 §11 的省 token 目標）。
- 文件：WEB-SPEC §5 組態表重寫、README 使用方式一節同步。

### 討論項（2026-08-05 已全數定案，結論已併入上表）

1. AI 進階節流參數 → **全部移設定頁**（出廠值＝現行 appsettings 值，零遷移）。
2. `Ui` 區段 → **改程式碼常數，整段刪除**。
3. `Auth:Ldap:Domain` → **退役，AD 驗證統一走設定頁**（升級偵測舊值 log 提示）。

精簡後 appsettings.json 只剩四個區段：`Storage`、`Jwt`（SecretKey/ExpireHours）、
`Auth`（Provider=Stub＋ServerAdmin）、`AllowedHosts`——全是啟動與安全前提。

### 測試

RuntimeSettingsResolver 覆寫測試（各新欄位進 orchestrator 設定）、Update 驗證測試、
升級相容（舊 blob 缺新欄位 → 預設值＝現行出廠行為）。

---

## §13 NetIQ 預設真實連線；離線示範資料改為測試模式下的設定頁開關

### 現況

掃描匯入精靈的探索 client 由 appsettings `Netiq:DiscoveryClient`（Auto/Stub/Real）在
DI 註冊時決定；`Auto`（出廠值）＝ Development 環境自動用 `StubNetiqDirectoryClient`
離線示範資料。問題：開發/測試機**預設就是假資料**，使用者要顯式改設定才連得到真實
Sentinel，方向顛倒；且開關埋在 appsettings，與 §12 的收斂方向不合。
（示範資料被誤認為真實掃描的教訓已發生過——2026-07-30 掃描精靈假資料誤認 bug。）

### 方案

1. **預設一律真實連線**：移除 `Netiq:DiscoveryClient`／`NetiqDiscoverySettings`／
   `ShouldUseStubNetiqClient`／`Validate` 對應檢查。
2. **`NetiqOptions`（DB、NetIQ 維護頁）新增 `UseOfflineDemoData`，預設 `false`**：
   - DI 的 `INetiqDirectoryClient` factory 改為執行期決策（scoped 解析時讀
     `NetiqOptionsStore`＋環境）：**僅非 Production 且開關開啟**才回 Stub；
     Production 無條件真連線（後端 `Update` 在 Production 拒絕開啟＋前端隱藏開關，雙保險，
     沿用「假資料不得上正式」的既有原則）。
   - NetIQ 維護頁在測試模式顯示開關「使用離線示範資料（固定台數/網段，非真實掃描）」；
     開啟時頁面顯示常駐警示 badge，掃描精靈結果也顯著標示「示範資料」。
3. **行為改變要講明**：開發機沒有可連的 Sentinel 時，精靈會誠實回連線錯誤（而不是
   默默給假資料）——這正是本項要的方向；需要離線展示時到 NetIQ 維護頁顯式開啟。
4. 文件：README、WEB-SPEC §9.9a、NETIQ-API-REFERENCE §3.4 同步；appsettings 註解刪 Netiq 段。

### 測試

factory 決策測試（Production 恆 Real；非 Production 依開關）、`Update` 在 Production
拒絕開啟的驗證測試、既有 `ShouldUseStubNetiqClient` 測試改寫。

---

## 決策紀錄（2026-08-05 與使用者確認）

1. **§1**：採**方案 A**（Stub seed 測試 admin＋serverAdmin 引導卡，不動 §6.2 權限模型）。
2. **§5**：採**單選顯示範圍**（全部／未結案／未處理／未指派 chip 單選）。
3. **§6**：**甲＋乙都要**——本輪做甲（批次指派補強），乙（常設自動指派規則）概要記入
   BACKLOG、下一輪立案實作（草案見 §6）。
4. **§11**：**CLAUDE.md＋README 瘦身都做**。
5. **§2 順手項**（「檢視執行」一併 modal 化）：依建議一併做；實作時若發現範圍失控可先縮回
   只做日期展開。
6. **§12**（第二批確認）：AI 進階參數**全移設定頁**；`Ui` 區段**改常數整刪**；
   `Auth:Ldap` **退役**、AD 驗證統一走設定頁。
7. **§13**：依規劃執行（NetIQ 預設真連線；離線示範資料＝非 Production 限定的
   NetIQ 維護頁開關）。

## 建議實作順序與規模

| 順序 | 項目 | 規模 | 理由 |
|---|---|---|---|
| 0 | §11 文件整理 | S~M | 與程式碼改動零耦合，可先行；CLAUDE.md 讓後續每輪都省 |
| 1 | §7 儀表板 grid | S | 一行 CSS，獨立 |
| 2 | §8 可搜尋處理人選單 | S | 純前端共用元件，§6 依賴它 |
| 3 | §2 執行總表展開 | M | ui.js 擴充是 §10 展開的前置 |
| 4 | §3 已回補狀態＋文案 | M | 後端小改＋測試 |
| 5 | §9 名稱格式盤點補漏 | M | 面廣但機械式 |
| 6 | §6 批次指派補強 | S | 依賴 §8 |
| 7 | §4 報表一頁化＋簽章查詢併入 | M | 版面調整＋問題查詢補「來源」欄 |
| 8 | §5 報表顯示範圍 | M~L | 後端 scope＋前端＋下鑽一致性 |
| 9 | §10 依問題預設＋篩選＋展開 | L | 依賴 §2/§5/§8 的產物 |
| 10 | §1 開箱體驗 | M | 方案 A 已定案 |
| 11 | §12 appsettings 精簡 | L | 動到設定頁/驗證/RuntimeSettingsResolver/文件，獨立成一個 commit 群 |
| 12 | §13 NetIQ 真連線預設 | M | 依賴 §12 的 appsettings 清理順序（Netiq 區段一併移除） |

全程維持測試綠（目前 1321），每項各自帶單元測試與 WEB-SPEC 對應章節更新。
