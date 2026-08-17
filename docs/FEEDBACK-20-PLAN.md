# 回饋第二十輪規劃（外部審查七項＋使用者回饋十九項）

## 0. 背景與範圍

- 輸入一：外部審查對 `ce296b9` 的新發現 P1～P7（上一輪 12 項驗收全過，不再處理）。
- 輸入二：使用者實測回饋 Q1～Q19（問題查詢欄位改版、月曆時間軸、排程只補缺 AI、AI provider、CRON 撞鍵等）。
- 一併收的 BACKLOG 同構項：`visibleSeverities` 選填、`blob:hosts:Read` 驗證盲區。
- **明確不做**：儀表板行程內快取（先實測 perf key）；`lf_top_issues.source_key` 持久化欄位（C 加閘門後成本消失）；AI「測試連線回傳範例 JSON」（列 BACKLOG）；升級時自動改既有 timeout 600→1200（等於偷改設定）。
- **已定案決策**：
  1. 報表 from>to 回 400；其他 controller 只 swap 不設上限。
  2. 可見範圍立場：待辦／KPI／排行／風險類型卡**皆**尊重顯示設定（日風險等級＋SiteHidden 嚴重度），一張畫面一份母體；`visibleSeverities` 改必填。
  3. 首見日合併失敗重試 3 次（暫定 30 分）後停止並在 health 標紅。
  4. Q17：卡片與下鑽統一母體，下鑽頁加「共 N 台主機（去重）」。
  5. Q19：`GET /api/settings/display` 補 `VisibleSeverities`；所有嚴重度／日風險篩選 UI 一律依它隱藏；「另有 N 項」只計使用者自選篩選。日風險與嚴重度兩條都測。
  6. Q12：重試 prompt 附上一次錯誤（迭代修補）＋失敗後保留可補跑狀態。
  7. Q10：provider 三選一（OpenAI 相容本機／OpenAI 官方／Azure OpenAI），既有設定歸本機不影響升級。
  8. Q6：首見合併成一欄，兩值不同時**換行**顯示並以 icon 標記差異。
  9. Q9：timeout 預設 1200 只影響新安裝。
  10. UI 設計套用 `ui-ux-pro-max`：查得方案「Data-Dense Dashboard／企業藍／Fira」與現行 token 相同，不換 token；契約採用其檢核：hover 150–300ms、clickable 皆 cursor-pointer、focus-visible、`prefers-reduced-motion`、375/768/1024/1440 斷點、SVG icon 不用 emoji。

## 1. 事實核對摘要

| 項 | 狀態 | 證據（節錄） |
|---|---|---|
| P1 | ✅ | `DashboardController.cs:124` 算 days 在 `ReportService.cs:35` swap 前；Records/Ai/Audit/Handling from/to 亦無正規化 |
| P2 | ✅ | `EfIssueAggregateQuery.cs:450` 寫死高/中；ReportService 短路不含 Todo；測試未斷言 Todo |
| P3 | ✅ | 無閘門、Singleton backend→60s、不重試、無 health；五個 HostedService 中唯一三者皆無 |
| P4 | ✅ | `NetiqPipelineService.cs:770-800` 先 TryRemove 再 MutateBatch 無 try/catch |
| P5 | ✅更重 | 5 相關子查詢／6 層 EXISTS／串接鍵 6 次；無獨立 perf key；串接模式僅此方法 |
| P6 | ✅ | 無升級警語；只報筆數 |
| P7 | ✅ | `ReadVersion` 未計 perf；SQL 串接鍵與 `IssueSignatureKey.For` 兩份定義；無 SqlServer 測試 |
| Q1/Q13 | ✅ | `core/ui.js:510` caret inline＋block div → 換行；共用元件 |
| Q2 | ✅ | `_Layout.cshtml:26` 純 div；儀表板路由 `/`；三個 id 由 settings.js 即時替換 |
| Q3/Q4 | ✅ | 密度 `d-inline-flex`；處理概況後端單一字串 |
| Q5 | ✅ | `format.js:203`；倍數＝最近出現日台數÷30 天中位數 |
| Q6 | ✅ | 本期首見受期間截斷；機房首見查 `lf_issue_first_seen` 不受過濾 |
| Q7 | ✅ | `renderHeader` 鉤子＋`helpIcon` 已存在全站僅 2 處用；03-issues.md 只 25 行 |
| Q8/Q12 | ✅ | AI 完全失敗 → `AiAnalyzed=false, AiPending=false` 永不補跑；三次 prompt 相同 |
| Q9 | ✅ | `SystemSettings.AiTimeoutSeconds=600`、`AppSettings` 600 |
| Q10 | ✅ | 無 provider；URL 硬編、Bearer only、model 寫死 |
| Q11 | ✅ | `Aggregate()` GroupBy 用原始 SourceName，`KeyOf` ToUpper 折疊撞鍵；同型 `IssueRankingBuilder:102`、`MailIssueDigest:49`、`HandlingHistoryQueryService:135/271` |
| Q14 | ✅ | 七欄無 `sortKey`；`sortRows` 可照抄 `users.js` |
| Q15 | ✅ | 扁平日期陣列、inline style；無月曆元件 |
| Q16 | ✅ | `--bs-table-hover-bg: var(--lf-primary-soft)` |
| Q17 | ✅ | 卡片 `ParseUnhandledSeverities` vs 下鑽 `ParseVisibleSeverities`；`AggregateByCategory` 無 riskLevels；卡片跨問題去重 |
| Q18 | ✅ | `trendAlertItem` 無 `flex-shrink-0` 包裝；`button()` 無 text 無 aria-label |
| Q19 | ✅ | display API 只回 `VisibleDayRiskLevels`；主機詳情時間軸未讀顯示設定 |

## 2. 作業總覽

本輪委派模型：{開工前查兩池額度後填}｜使用者未指派。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | 日期區間正規化單一入口（P1） | — | agy |
| B | 可見範圍在聚合路徑一致套用（P2、Q17、Q19 後端） | — | agy |
| C | 首見日合併：閘門／300s／重試／health（P3） | — | agy |
| D | FlushHostTouches 容錯（P4） | — | Claude |
| E | GetDayHandlingRaw 改寫（P5、P7） | B | agy |
| F | 保留期升級警語＋估晚數（P6） | — | Claude |
| G | 觀測性：ReadVersion 計數、註解（P7） | — | Claude |
| I | 問題鍵大小寫正規化（Q11） | — | agy |
| J | 表格共用元件修正（Q1/Q13/Q18/Q7 表頭 help） | — | agy |
| K | 問題查詢「依問題」欄位改版（Q1/Q3/Q4/Q5/Q6/Q7） | J | agy |
| L | 小型 UI：brand／主機詳情排序／卡片 hover／前端篩選依顯示設定隱藏（Q2/Q14/Q16/Q19 前端） | B、J | agy |
| M | 月曆式風險時間軸（Q15） | L | agy |
| N | 排程「只補缺 AI」＋AI 失敗可補跑（Q8/Q12b） | — | agy |
| O | AI provider＋timeout 預設＋重試迭代修補（Q10/Q9/Q12a） | — | agy |
| P | 說明書擴充（Q5/Q7/Q17/O） | K、O | agy |
| H | 文件收尾（BACKLOG／README／WEB-SPEC／DB-SPEC） | 全部 | Claude |

執行順序建議：後端先（A、B、I、C、N、O 可平行；E 接 B）→ D/F/G Claude → J → K/L → M → P → H。

## 3. 作業明細

### 作業 A-階段 1：日期區間解析單一入口
- **背景**：報表 366 天上限在 controller 算、swap 在 service 做，顛倒區間繞過上限；四個 controller 的 from/to 也各自解析。
- **契約**：
  - `QueryStringParsing` 提供「解析 from/to 區間」方法：回傳正規化 (from, to)；預設顛倒即 swap；可選 `maxDays`（含首尾）超過丟 `DomainException.Validation`；可選「顛倒視為錯誤」丟 Validation。
  - Reports summary 用「顛倒→400、maxDays=366」；yoy 比較期不另檢查。
  - Records／Ai／Audit／Handling 改走同一入口，只 swap；既有預設值不變。
  - `ReportService.GetSummary` 的 swap 保留為防禦。
- **範圍**：`LogForesight.Web/Controllers/Api/*`、`QueryStringParsing.cs`、Tests；不動 service、docs。
- **驗收**：build 零警告、test 全綠；新增 `Reports_from大於to_回400`、`Reports_區間367天_回400`、`Reports_區間366天_通過`、`Records_from大於to_自動交換不報錯`；grep `(toDate - fromDate).Days` 不在 controller。
- **回報格式**：改檔清單、測試數字（總／綠／紅）、偏離契約之處與理由。（以下各階段同）

### 作業 B-階段 1：日風險等級與嚴重度在聚合路徑一致套用
- **背景**：`AggregateDayTodo`／`ActionableOccurrences`／`AggregateByCategory` 不接受日風險可見範圍；`visibleSeverities` 選填漏套；風險類型卡用 `ParseUnhandledSeverities` 而下鑽用 `ParseVisibleSeverities`。
- **契約**：
  - `IIssueAggregateQuery` 的 `AggregateDayTodo`、`ActionableOccurrences`、`AggregateByCategory` 新增 `IReadOnlySet<string>? riskLevels`：null＝既有母體；空集合由呼叫端短路不下推（沿用 `ResolveDayRiskLevels` 註解語意）。
  - `GetTodoByRange`／`ResolveActionable`／DashboardService／ReportService 傳入與 KPI 相同的 riskLevels；ReportService 的 nothingVisible 短路涵蓋 Todo。
  - `visibleSeverities` 相關參數改必填，所有呼叫端明確傳入。
  - 風險類型卡（`AggregateByCategory`）改用 `visibleSeverities`（與下鑽相同）；嚴重度篩選粒度與 `Aggregate` 對齊。
  - `GET /api/settings/display` DTO 新增 `VisibleSeverities`（null／全集＝不限制）。
  - 依問題查詢回應新增「去重主機總數」欄位（供下鑽頁顯示「共 N 台主機（去重）」），定義與卡片 `affectedHosts` 相同。
- **範圍**：Core/Persistence 介面與各實作、Web/Services、DisplaySettings DTO/Controller、RecordListQueryService DTO、Tests；不動前端、docs、GetDayHandlingRaw 查詢形狀。
- **驗收**：build 零警告、test 全綠；既有 `GetSummary_日風險等級只顯示高時_…` 補 `Todo.TotalCount` 斷言；新增 `Report_日風險只顯示高時_Todo不含中風險日`、`Report_全部隱藏時_Todo為零`、`Todo_SiteHidden隱藏嚴重度_不計入`、`風險類型卡_主機數與依問題下鑽去重主機數相等`、`DisplaySettings_回傳VisibleSeverities`；grep `visibleSeverities` 無 `= null`。

### 作業 C-階段 1：首見日合併閘門、逾時、重試與申報
- **背景**：每次啟動無條件跑兩段全表掃、吃 60 秒逾時、失敗即結束、health 看不到。
- **契約**：
  - 閘門：完成後記錄浮水印（暫定 `lf_top_issues` MAX(record_id)，存 blob）；啟動時浮水印等於目前值即跳過。
  - 逾時：使用分析等級 CommandTimeout（300s），不得改 Web Singleton 逾時。
  - 重試：失敗後間隔暫定 30 分，最多 3 次；三次皆失敗停止並標記錯誤。
  - 申報：Progress（進行中／完成／失敗次數／最後錯誤）接入 `HealthService`→`/api/health/detail`，比照 backfill；失敗 3 次視為 degraded。
  - 兩段 SQL 內容不改。
- **範圍**：`IssueFirstSeenSeedHostedService`、`SchemaUpgrader.MergeIssueFirstSeenSeed`（可加回傳值）、Health*、Tests。
- **驗收**：新增 `首見日合併_浮水印未變_跳過不執行`、`首見日合併_失敗三次_停止並標記`、`HealthDetail_含首見日合併狀態`。

### 作業 D（Claude）：FlushHostTouches 容錯
- MutateBatch 包 try/catch→`Log.Warn`，失敗時 touches 放回字典（已有較新值則保留較新）；`RunAsync` finally 不得拋出。測試 `FlushHostTouches_寫入失敗_touch保留待下次`。

### 作業 E-階段 1：GetDayHandlingRaw 改寫
- **背景**：每列 5 個相關子查詢、6 層 EXISTS、問題鍵 SQL 端字串串接；SqlServer 未實測，翻譯失敗即首頁 500。
- **契約**：
  - 結果語意逐項相同（total／closed／anyInProgress／anyCaseHandler／anyOverdueIssue、riskLevels 母體、墓碑排除、todayForOverdue）。
  - 查詢不得含字串串接鍵比對；問題鍵在記憶體以 `IssueSignatureKey.For` 組（範本 `Aggregate`），或先 GROUP BY 聚衍生表再 join——執行端擇一並回報理由。
  - 加獨立 perf key `issues:GetDayHandlingRaw`。
  - 拉回列數以「期間高/中風險日×主機」及對應 handling 列為限。
- **範圍**：`EfIssueAggregateQuery.cs` 該方法與私有輔助、Tests；不動介面。
- **驗收**：既有合約測試不改斷言全過；新增 ≥2 條覆蓋 closed 兩個 Any 分支與 overdue 分支；grep `+ "|" +` 在 Core 零命中。

### 作業 F（Claude）：保留期升級警語與估晚數
- README 升級段加警語（升級前確認 `RetentionDays`／`DetailRetentionDays`，先前對 NetIQ 主機未生效）；申報文字加「依每次上限估約 M 次執行」。

### 作業 G（Claude）：觀測性
- `ReadVersion` 加 `blob:{key}:ReadVersion` 記錄；註解「微秒級」改「SQLite 微秒／SqlServer 一次往返」。

### 作業 I-階段 1：問題鍵大小寫正規化
- **背景**：`Aggregate()` 依原始 SourceName 分組，SQLite 大小寫敏感 → `cron`/`CRON` 兩筆，`IssueProfile.KeyOf` 折疊後 `ToDictionary` 撞鍵，問題檔案頁 500；另三處同型。
- **契約**：
  - `EfIssueAggregateQuery.Aggregate()`（含 `RecentIssues`）分組鍵改為大小寫不敏感（與 `KeyOf`／`FirstSeenFor` 的 upper 語意一致）；輸出 `Source` 取該組任一原始值（暫定 MIN）。
  - `IssueOwnerAdminService.RecentAggregatesByKey`、`HandlingHistoryQueryService` 兩處 `ToDictionary` 改防禦式（同鍵合併：HostCount 取最大／LastSeen 取最新，或依 `IssueProfile.IndexByKey` 慣例取第一筆），不得再拋 ArgumentException。
  - `IssueRankingBuilder`／`MailIssueDigest` 因上游合併自然不再重複，不另改；若其 ToDictionary 仍可能撞鍵則同樣防禦。
- **範圍**：上列檔案＋Tests；不動前端。
- **驗收**：新增 `Aggregate_來源大小寫不同_合併為一筆`、`IssueOwners_List_來源大小寫重複_不拋例外`、`HandlingHistory_主機名大小寫重複_不拋例外`；grep：Web/Services 內 `.ToDictionary(` 每處旁有 GroupBy 或 Ordinal comparer 或註解說明唯一性。

### 作業 J-階段 1：表格共用元件修正
- **背景**：`renderTable` 展開 caret 與 block 內容換行（Q1/Q13）；表頭 help icon 機制存在但無便捷入口；圖示按鈕無 aria-label；頻率異常按鈕被壓縮（Q18）。
- **契約**：
  - `renderTable` 首欄 caret 與內容同列（caret 頂端對齊多行內容），所有既有可展開表格自動受益，不得改各頁 cell 結構。
  - 新增共用 `headerWithHelp(title, content)`（沿用 `helpIcon` popover，hover/focus 觸發），供 `renderHeader` 使用。
  - `button()` 無文字時以 `title` 補 `aria-label`。
  - `trendAlertItem` 右側比照 `correlationAlertItem` 加 `flex-shrink-0` 包裝。
  - 設計檢核：hover 過渡 150–300ms、clickable 有 cursor-pointer、focus-visible、`prefers-reduced-motion` 尊重。
- **範圍**：`wwwroot/js/core/ui.js`、`record-detail.js` 的 `trendAlertItem`、`site.css`；不動 records.js 欄位定義（K 負責）。
- **驗收**：build 全綠；無前端測試，改以人工核對清單回報：問題查詢依問題／風險日詳情重點問題／主機詳情三處展開表格首欄不換行；頻率異常按鈕在 375px 寬不溢出；grep `aria-label` 出現在 `button()`。

### 作業 K-階段 1：後端 DTO 補欄
- **契約**：`IssueGroupDto`（依問題列表）新增 `PlainExplanation`（來源 `KnownIssueRule.PlainExplanation`，無命中為 null）與處理概況三個整數欄（未處理／處理中／已處理）；既有 `handlingSummary` 字串保留一版供 CSV 匯出；`IssueRankingBuilder` 同步帶 `PlainExplanation`（儀表板重點問題也能顯示）。
- **範圍**：`RecordListQueryService`、`IssueRankingBuilder`、DTO、Tests。
- **驗收**：新增 `依問題查詢_命中規則_帶出白話說明`、`依問題查詢_處理概況三數字_與字串一致`。

### 作業 K-階段 2：「依問題」列表前端改版
- **背景**：Q1/Q3/Q4/Q5/Q6/Q7 全在 `records.js renderIssueView()` 欄位定義。
- **契約**：
  - 問題欄：來源(EventId) 下方一行 `PlainExplanation`，字級比主文小一階、單行 `text-truncate` 不撐開欄寬（欄設最大寬度）、hover 顯示完整說明（title 或 popover）；無說明不佔行。
  - 出現密度：數字在上、進度條換行在下，欄寬不變；保留數字文字（既有註解要求）。
  - 處理概況：三種狀態各一行（未處理／處理中／已處理），0 的段仍顯示但淡色，讓各列對齊。
  - 首見欄合併：預設顯示機房首見；本期首見晚於機房首見時**換行**顯示第二行「本期 mm-dd」並附 SVG icon（如 clock-history）標記兩者不同，hover 說明兩者定義；相同時只一行。CSV 匯出仍輸出兩欄。
  - vs 基準：改為兩行——第一行「基準 N 台/日 → M 台」，第二行倍數徽章：≥2 危險色、1～2 中性、<1 顯示「收斂 ×0.9」淡色；hover 說明倍數定義與 <1 的讀法。儀表板同 cell 一致（共用 format）。
  - 表頭：出現密度／處理概況／首見／vs 基準／總次數／主機數 加 `headerWithHelp`，文案與說明書一致（P 提供）。
  - 設計檢核同 J；375/768/1024/1440 不橫向捲動（表格容器 overflow-x 除外）。
- **範圍**：`records.js`、`dashboard.js`（vs 基準 cell）、`core/format.js`、`site.css`；不動後端。
- **驗收**：build 全綠；人工核對清單：六項各一張截圖描述；CSV 匯出欄數與內容不變。

### 作業 L-階段 1：小型 UI 修正
- **契約**：
  - `lf-sidebar__brand` 改可點連結至 `/`，保留三個 id，hover/focus 樣式，無底線。
  - 主機詳情「重點問題（期間彙總）」接共用 `sortKey`/`sortRows`（本地排序），預設依總次數降冪。
  - `lf-card:hover`（含 `lf-card__body`）套用與表格相同的 hover 底色 `--lf-primary-soft`，過渡 150–300ms；不影響 `lf-card--clickable` 既有位移。
  - 前端所有嚴重度／日風險篩選 UI 依 `getDisplaySettings()` 的 `VisibleSeverities`／`VisibleDayRiskLevels` 隱藏不可見選項並取消其 active（風險日詳情嚴重度鈕、問題查詢已做者維持、主機詳情時間軸配色跳過被隱藏等級視為無紀錄）；「另有 N 項」只計使用者自選篩選。
- **範圍**：`_Layout.cshtml`、`host-detail.js`、`record-detail.js`、`site.css`、`core/api.js`；依賴 B 的 DTO。
- **驗收**：build 全綠；人工核對：brand 可點；排序表頭可點且方向切換；卡片 hover 底色；管理者隱藏「低」後風險日詳情無「低」鈕且不出現「另有 N 項」提示。

### 作業 M-階段 1：月曆式風險時間軸
- **背景**：現行 22px 方塊扁平排列看不出日期。專案無月曆元件；後端 `detail.timeline` 形狀不改。
- **契約**：
  - 依 `date` 分月，每月一個網格：月份標題、星期表頭（一～日）、月初補空格；多個月份**左右並排**，寬度不足（<768px）時堆疊。
  - 每格：有紀錄可點連結至風險日詳情，配色沿用既有 `cellColor` 變數；hover 顯示「yyyy-MM-dd｜等級｜headline」；格內顯示日數字（小字）。
  - 保留既有圖例、與重點問題的連動高亮（`.lf-timeline-cell--highlight/--dim`）。
  - inline style 移入 `site.css` 新 class（`.lf-calendar*`）；設計檢核同 J；SVG icon；focus-visible。
- **範圍**：`host-detail.js renderTimeline()`、`site.css`、`HostDetail.cshtml`（容器）；不動後端。
- **驗收**：build 全綠；人工核對：90 天顯示 3～4 個月並排；375px 堆疊；hover 有日期；點格子導向正確日期；高亮連動仍作用。

### 作業 N-階段 1：排程「只補缺 AI」與 AI 失敗可補跑
- **背景**：`MissingDateFinder` 只看有無紀錄；AI 完全失敗的主機日 `AiAnalyzed=false, AiPending=false` 永不補跑；孤兒補跑只撈 `AiPending`。
- **契約**：
  - `TriggerRunRequest`／`RunRequest` 新增旗標（暫定 `OnlyMissingOrFailed`，預設 false）：為 true 時「待跑」定義＝缺紀錄 **或** `AiAnalyzed=false`；已成功且有 AI 分析的主機日略過。
  - 為 true 時 AI 補跑掃描條件放寬為 `AiPending || !AiAnalyzed`。
  - AI 完全失敗（RawContent 空）的主機日狀態改為可被補跑（暫定保留 `AiPending=true` 並在 headline 註明「AI 待補」），不影響統計結果與風險等級。
  - Runs 頁「立即執行」modal 加勾選「只補跑失敗或未執行的主機（略過已成功且有 AI 分析）」；預覽台數依旗標計算。
  - 執行結果／主控台輸出區分「略過（已完成）」與「補跑」。
- **範圍**：Core/Service（`HostDayPostProcessor`、`NetiqPipelineService`、`LogAnalysisService` 失敗分支、`AnalysisOrchestrator`）、`ScheduleController`、DTO、`Runs.cshtml`、`runs.js`、Tests。
- **驗收**：新增 `MissingDateFinder_requireAi_缺AI的主機日視為待跑`、`立即執行_只補缺AI_已完成主機被略過`、`AI完全失敗_主機日標記可補跑`；既有排程測試全綠。

### 作業 O-階段 1：AI 服務 provider 設定模型
- **背景**：無 provider 概念；URL 硬編 `/v1/chat/completions`、Bearer only、model 寫死 `local-model`。
- **契約**：
  - 設定新增 `AiProvider`（`LocalCompatible`｜`OpenAI`｜`AzureOpenAI`，預設 LocalCompatible）、`AiModel`（OpenAI 用；本機預設 `local-model`）、`AiAzureDeployment`、`AiAzureApiVersion`（暫定預設 `2024-10-21`）；API key 沿用既有加密欄位。
  - `AiTimeoutSeconds` 出廠預設改 1200（DB 模型與 appsettings 兩處），既有值不動。
  - Resolver／`AiSettings` 傳遞新欄位；`WebAiService` fingerprint 納入新欄位。
  - DB schema／migration 依既有 SystemSettings 儲存機制（若為 blob 則無 migration）。
- **範圍**：Core/Configuration、Core/Models、RuntimeSettingsResolver、SystemSettingsService、DTO、Tests；不動 AIService 呼叫邏輯（階段 2）。
- **驗收**：新增 `設定_provider預設LocalCompatible_舊設定不受影響`、`設定_timeout出廠預設1200`；build 全綠。

### 作業 O-階段 2：AIService 依 provider 組請求＋重試迭代修補
- **契約**：
  - LocalCompatible：`{base}/v1/chat/completions`、Bearer（有 key 才帶）、model=AiModel。
  - OpenAI：`https://api.openai.com/v1/chat/completions`（BaseUrl 可覆寫）、Bearer、model 必填。
  - AzureOpenAI：`{base}/openai/deployments/{deployment}/chat/completions?api-version={ver}`、`api-key` header、不送 model。
  - `ChatJsonAsync` 第 2 次起的重試，把上一次失敗原因（解析錯誤或驗證訊息＋回覆預覽片段）附進 user prompt 尾端要求修正；第一次 prompt 不變。
  - 設定頁 AI 區塊：provider 下拉，依選擇顯示所需欄位（本機：BaseUrl／key 選填／model；OpenAI：key／model；Azure：endpoint／deployment／api-version／key）；儲存驗證必填。
- **範圍**：`AIService.cs`、`Settings.cshtml`、`settings.js`、Tests。
- **驗收**：新增 `AIService_Azure_URL與header正確`、`AIService_OpenAI_Bearer與model`、`ChatJson_重試時prompt附上次錯誤`；grep `"local-model"` 只剩預設值一處。

### 作業 P-階段 1：說明書擴充
- **契約**：`03-issues.md` 補依問題各欄（問題說明來源、出現密度、處理概況、首見合併與 icon 意義、vs 基準含 <1 讀法、總次數 vs 主機數、去重主機總數）；`02-dashboard.md` 補風險類型卡計數定義與下鑽對應；`12-settings.md` 補 provider 三種與必填、timeout 預設 1200（既有不變）；`04`（排程）補「只補缺 AI」；`manifest.json` keywords 同步。文案須與 K 表頭 help 一致（K 執行端回報其文案，P 據此對齊）。
- **範圍**：`LogForesight.Web/HelpContent/*`；不動程式。
- **驗收**：`HelpContentServiceTests`／`HelpChapterScorerTests` 全綠；grep 各新欄名出現在對應章。

### 作業 H（Claude）：文件收尾
- BACKLOG：刪 `visibleSeverities` 項；更正「已受 366 上限約束」前提；`blob:hosts:Read` 驗證補 ReadVersion；新增 `source_key` 欄位、儀表板快取、AI 測試連線三條遞延。
- README 升級警語（F）；WEB-SPEC：health detail 新欄位、日期區間規則、display API 新欄位、依問題 DTO 新欄；DB-SPEC：SystemSettings 新鍵、首見日合併浮水印。

## 4. 測試計畫
見各階段驗收；新增測試合計 ≥ 30；既有全綠。前端無自動測試，J/K/L/M 以人工核對清單＋截圖描述回報，Claude 以瀏覽器實際開頁驗收。

## 5. 文件更新
作業 P（說明書）＋作業 H（技術文件），全部驗收後才寫。

## 6. 風險與回滾
- B 改必填觸及多處呼叫端，編譯期可見；E 為首頁關鍵路徑，靠合約測試守語意，SqlServer 仍需實機開儀表板驗證。
- I 改分組鍵會改變「同來源不同大小寫」的歷史呈現（合併成一筆），屬預期。
- O 動設定模型；provider 預設歸本機保證升級零行為變化。
- N 改 AI 失敗分支狀態，需確認不觸發無限重跑（只在旗標開啟時才補）。
- 各作業獨立 commit，可單獨回滾。

## 7. 執行紀錄

分支 `feature/feedback-20`（自 dev @ce296b9）。基準測試 2168 綠／2174 總計。
委派模型：開工時查得 Claude 池週限 65%、Gemini 週限 30%，使用者指派先用 `claude-sonnet-4-6`；
A 段後 Claude 五小時額度用罄，**自 B1 起改用 `gemini-3.7-flash-high`** 並沿用至今。

執行時的規劃調整：作業 B 拆成 B1（riskLevels 貫通）／B2（嚴重度母體統一＋display API），
作業 O 拆成 O1（設定層）／O2（AIService 請求組法）——原規劃各為一段，實際規模過大。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A | agy | `f752116` | 2174→2180，0 警告 | 加了 BOM、改壞既有註解、順手改動無關錯誤訊息、4/5 支新測試只測工具函式沒穿過 controller → Claude 修正並補一支走 AuditController 的接線測試 |
| B1 | agy | `53b0879` | 2180→2185 | 參數名用 `dayRiskLevels`，與同概念的既有 `riskLevels` 不一致 → 統一命名。順帶修好 ReportService 全隱藏短路不涵蓋 Todo（P2 的第二個不一致） |
| B2 | agy | `d6f6d0a` | 2185→2189 | Q17 根因確認為**傳錯設定**（卡片傳 ParseUnhandledSeverities、下鑽傳 ParseVisibleSeverities）。過度設計三處：AffectedHosts 別名屬性（同值序列化成兩個 JSON 欄位）、分頁邏輯複製兩份繞過共用 Paginate、無用 using → Claude 移除 |
| I | agy | `e172870` | 2189→2193 | agy 正確地連帶改了三個查詢字典的鍵。Claude 驗收另抓到：`Aggregate` 輸出的 Source 改成 MIN 後，IssueRankingBuilder／MailIssueDigest 用它當跨期比較鍵會靜默落空（老問題誤判為新出現）→ 兩處改用 KeyOf 並補回歸測試 |
| C | agy | `5469054` | 2193→2197 | 用 public static 可變欄位（LastMergeOutcome＋執行計數器）傳遞結果，並為此把 SchemaUpgrader 開放成 public → 改用既有回傳值、刪除靜態欄位、改回 internal（專案已有 InternalsVisibleTo） |
| D/F/G | Claude | `9418fb0` | 2197→2198 | D 的測試首次紅是自己寫錯（零事件主機刻意不累積回報時間），補事件後通過並以突變測試確認斷言有效 |
| N | agy | `4c371b9` | 2198→2203 | 預覽的 IAnalysisRecordQuery 宣告成可選依賴、null 時靜默回總台數 → 改必填；預覽用 `Query` 會反序列化整份 ContentJson 而它是打字防抖觸發的路徑 → 改 `QueryLightweight` |
| O1 | agy | `4620329` | 2203→2210 | 重複貼上的註解區塊；provider 必填判定寫成三份（Available＋兩個工廠）→ 抽成共用判定；AppSettings.IsConfigured 註解被刪掉的「刻意清空位址＝刻意停用」語意補回 |

核對階段的一處誤判已更正：`HandlingHistoryQueryService` 的兩處 `ToDictionary` 原被列為與 CRON 撞鍵同型的風險，
實際上鍵 `(host_name_key, record_date)` 在資料表有 unique 索引（註解已寫明），是安全的，作業 I 明文禁止動它。

**待執行**：O2（AIService 依 provider 組請求＋重試迭代修補）、E（GetDayHandlingRaw 改寫）、
J／K／L／M（前端四段）、P（說明書）、H（文件收尾）。
