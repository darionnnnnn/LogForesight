# SHARED-STANDARDS-PLAN：共用標準盤點（2026-07-27）

> 狀態：**S1–S12 已全部實作完成（2026-07-27）**；S13／S14（P3 選配）維持未做。
> 原則（使用者定案）：**可以共用同一套標準的就共用，不要各自實作再靠人力維持一致**。
> 本文件盤點整個專案「同一條規則寫了兩份以上」的地方，每項附現況證據、共用方案、行為變化與風險。
>
> 實作補記：
> - **S7** 實作為 `markdown-lite.js` 的兩個入口——`renderAiText`（區塊版：chat 泡泡、AI 判讀、
>   AI 歸納）與 `renderAiInline`（行內版：儀表板今日焦點，清單項內要接下鑽連結）；
>   `PromptGuidelines.LanguageReminder` 已接上全部四個 Web AI 呼叫點的 user prompt 尾端。
> - **S8** 泛型化為 `SettingsBoundClient<TSnapshot, TClient>`（任意快照形狀）——#9 AD 動態驗證
>   的快照是伺服器清單＋SearchBase／Filter，原規劃的 (BaseUrl, KeyEnc) 固定形狀塞不下。
> - **S12** 實作時多掃出三處同款問題並一併修正：records.js（today/defaultFrom/快捷期間鈕）與
>   audit.js（預設期間）也用了 toISOString 的 UTC 日期；handling-panel.js 的快速鈕是正確的
>   本地組法但屬重複實作——全部收斂到 format.js 的 toLocalDateString/todayLocal。
>
> 與 docs/WEB-FEEDBACK-PLAN.md 的關係：本文件是其**批次 0（共用基礎）**——
> 先把單一標準立起來，九項回饋（尤其 #5、#6）落在共用點上實作，而不是再添新的重複。

盤點結論總表（P1=九項回饋直接依賴、P2=順手一起做划算、P3=獨立的清理，可後補）：

| # | 主題 | 重複份數 | 優先 |
|---|------|---------|------|
| S1 | 嚴重度可見性過濾（GetVisibleSeverities） | 2 份實作＋2 處漏套 | P1 |
| S2 | 日風險等級字串與排序權重（高/中/低、RiskRank） | 常數散落 20+ 處、RiskRank 3 份 | P1 |
| S3 | 待辦母體規則（高＋中風險日） | 3 處呼叫端各自過濾 | P1 |
| S4 | 類別統計 DTO 組裝（Dashboard vs Report） | 2 份幾乎相同 | P1 |
| S5 | 主機排行組裝（Dashboard vs Report） | 2 份幾乎相同 | P2 |
| S6 | 涵蓋率缺口判定 | 2 份 | P2 |
| S7 | AI 語言規範與 AI 文字渲染 | prompt 各站各寫尾註、渲染 4+ 處各自來 | P1 |
| S8 | 設定快照式客戶端快取（WebAiService ↔ #9 AD） | 現 2 份、#9 會變 3 份 | P2 |
| S9 | Controller 查詢參數解析（ParseDate/ParseLongs/ParseStrings） | 4+ 份 | P2 |
| S10 | 合法值清單 vs enum（ValidSeverities/KnownCategories/KnownRisks） | 3 處手寫清單 | P2 |
| S11 | 前端嚴重度清單與徽章樣式 | 4 頁各自寫，且已出現樣式分歧 | P1 |
| S12 | 前端本地日期字串（含 reports.js 時區潛在 bug） | 2 份，其中 1 份有 bug | P2 |
| S13 | 類別/嚴重度中文名的 C#／JS 跨語言雙份 | 各 1 份 | P3 |
| S14 | 前端下鑽 URL 組裝與 KPI 卡渲染 | 3 頁重複片段 | P3 |

---

## S1 嚴重度可見性過濾：收斂到 RecordRepository 單一咽喉點　★核心

**現況（兩份實作＋兩處漏套，正是「各自處理再對齊」的病灶）**：
- 實作一：`SystemSettingsService.GetVisibleSeverities()`——Dashboard／Report 的類別統計用它。
- 實作二：`RecordQueryService.GetDetail` 內另寫一段 inline（`settings.SeverityDisplayMode == "GlobalFilter" ? ...`，
  RecordQueryService.cs:341-343），自己讀設定、自己比字串。
- 漏套一：`RecordQueryService` 依主機／依日期分組視圖的 Categories 聚合
  （RecordQueryService.cs:193、224）**完全沒過濾**——GlobalFilter 模式下，查詢頁分組列
  仍會列出未勾選層級的類別。這是現存 bug，不是設計差異。
- 漏套二：`ReportService.FindSignature`（跨主機簽章查詢）不過濾。

**共用方案**：把「問題嚴重度可見性」做成 `RecordRepository` 的**第二個強制過濾**，
與既有的主機可見範圍同一個位置、同一個理由（該類別的註解原話：
「這個展開如果散落在各個 Service，遲早有人忘了做」——嚴重度過濾已經應驗了這句話）：

- `Query`／`QueryPage`／`GetOne` 回傳前，若 `GetVisibleSeverities()` 非 null（SiteHidden 模式），
  將每筆 record 的 `TopIssues` 過濾為可見層級。
- 效果：Dashboard、Report、RecordQueryService（清單、分組、GetDetail、ClusterSignatures）、
  FindSignature、AI context（Chat/InterpretIssue 經 GetDetail）**全部自動繼承**，
  各 Service 現有的 `Visible(r)` lambda 與 GetDetail 的 inline 過濾**全部刪除**。
- `SystemSettingsService.GetVisibleSeverities()` 保留為唯一的規則出口，Repository 注入使用。

**行為變化（要在版本說明明講）**：
1. 查詢頁分組視圖的類別、簽章查詢，開始尊重全站隱藏（修正上述漏套）。
2. 日處理進度推導（DayHandlingDerivation 的輸入）看到的 TopIssues 變少——
   被隱藏層級的問題本來就不在未處理計算內（同一組 UnhandledSeverities），
   差異只在「已處理計數」不再包含被隱藏層級的已結案問題。與全站隱藏語意一致，接受。
3. 報告 txt 全文（IReportReader）不經 Repository，維持證據層原樣——這條線刻意不動。

**風險**：低-中。所有讀路徑集中改一處，靠測試矩陣掃行為：Repository 過濾的單元測試
＋既有 Dashboard/Report/RecordQuery 測試把 GlobalFilter 案例改為驗證「不再需要各自過濾」。

**依賴**：WEB-FEEDBACK-PLAN #5 的 SiteHidden 直接落在這個咽喉點上實作（模式簡化＝改
GetVisibleSeverities 的回傳條件，過濾機制不再分頁面）。

---

## S2 日風險等級常數與排序權重：Core 立單一 `RiskLevels`

**現況**：
- `"高"`／`"中"`／`"低"` 字串字面值散落：Web 的 RecordQueryService（189-191、220-222、747-749）、
  DashboardService（63-69、125-127、184、191-192）、ReportService（111-120、138-139、185-188）、
  批次的 LogAnalysisService（NormalizeRisk／MoreSevere）、SelfTestRunner:518、
  Core 的 RecordStorageShaper:19、EfAnalysisRecordStore:284/335-339。
- `RiskRank`（高=3 中=2 低=1）**三份**：RecordQueryService.cs:745、EfAnalysisRecordStore.cs:335
  （其註解自己承認「與 RecordQueryService.RiskRank／ReportService 內幾乎相同」）、
  以及 Dashboard/Report 排行榜的隱含排序規則。

**共用方案**：Core 新增 `RiskLevels` 靜態類別，成為唯一標準：
```csharp
public static class RiskLevels
{
    public const string High = "高"; public const string Medium = "中"; public const string Low = "低";
    public static readonly string[] All = { High, Medium, Low };
    /// <summary>排序權重（高=3 中=2 低=1，未知=0）——所有記憶體排序共用</summary>
    public static int Rank(string riskLevel) ...
    /// <summary>待辦／受影響主機的母體判定：高或中</summary>
    public static bool IsActionable(string riskLevel) ...
    /// <summary>批次 AI 回傳的等級正規化與比較（自 LogAnalysisService 搬入）</summary>
    public static string Normalize(string raw) ...  public static string MoreSevere(string a, string b) ...
}
```
- 批次 `LogAnalysisService` 的 `NormalizeRisk`／`MoreSevere` 搬進來（產生端與消費端同一套字典）。
- 各處字面值改引用常數；`AiInsightService.KnownRisks` 改 `RiskLevels.All`。
- **EF 例外**：EfAnalysisRecordStore.cs:284 的 inline 三元式是給 EF 翻譯 SQL 的，
  不能改成方法呼叫——改引用 `RiskLevels.High` 等 const（const 可進運算式樹），
  並在 335 的私有 RiskRank 上加註解指向 Core 版：「SQL 翻譯限制的必要複本，
  改 Core 版時此處同步」＋一條測試斷言兩者權重一致（把複本置於測試看管下）。

**風險**：純機械替換，行為零變化；靠編譯器與既有 804 測試保證。

---

## S3 待辦母體規則：搬進 `HandlingService.GetTodo` 內部

**現況**：「待辦母體＝高＋中風險日」這條規則由**呼叫端各自過濾**：
DashboardService.GetSummary:69、DashboardService.BuildGroupRisk:184，
WEB-FEEDBACK-PLAN #6 的報表處理進度將是第三處。

**共用方案**：`GetTodo(records)` 改為**自己套** `RiskLevels.IsActionable` 過濾，
呼叫端傳整批紀錄即可；介面註解同步改寫（「母體是傳入的風險日紀錄」→「母體規則在此強制」）。
#6 報表直接呼叫，不再複製過濾。

**風險**：低。呼叫端行為不變（過濾位置移動）；HandlingService 測試補「傳入含低風險日
仍只計高＋中」案例。

---

## S4 類別統計 DTO 組裝：Dashboard／Report 合為一份

**現況**：`DashboardService.BuildCategoryCards` 與 `ReportService.BuildCategories`
幾乎逐行相同（Visible lambda＋CategoryAggregator.Aggregate/Merge＋hostsPerCategory
＋DashboardCategoryDto 映射，各約 25 行）。

**共用方案**：S1 落地後 Visible lambda 消失，剩餘的「records → List&lt;DashboardCategoryDto&gt;」
抽成 Web 端共用靜態類 `RecordStatsBuilder.BuildCategoryCards(records)`，兩個 Service 呼叫同一份。

**風險**：無行為變化；兩邊測試合併驗同一個 builder。

---

## S5 主機排行組裝：同上合為一份

**現況**：`DashboardService.BuildHostRanking` 與 `ReportService.BuildHostRanking`
的 GroupBy／DashboardHostDto 映射／排序鏈（高風險日 → 關聯訊號日 → 中風險日，§DB-PLAN E）
兩份幾乎相同，差異只在 Dashboard 端 Take(10)、Report 端整批回傳後切分。

**共用方案**：`RecordStatsBuilder.BuildHostRanking(records, hostsByName)` 回傳完整排序清單，
排序鏈用 `RiskLevels.Rank` 家族；Dashboard 自行 Take(10)，Report 沿用現有 Top10＋其他彙總切分。

---

## S6 涵蓋率缺口判定：Core 計算屬性

**現況**：`r.DataIncomplete || r.SecurityLogAvailable == false` 在 DashboardService:65
與 ReportService:121 各寫一次（詳情頁前端 renderCoverage 是逐項呈現，不算重複）。

**共用方案**：`DailyAnalysisRecord` 加唯讀計算屬性 `HasCoverageGap`（不序列化，
`[JsonIgnore]`，避免動到兩個儲存後端的資料形狀），兩處改用。

---

## S7 AI 語言規範與 AI 文字渲染：一個出口進、一個出口出

**現況**：
- 進（prompt）：`PromptGuidelines.Language` 已共用，但 WEB-FEEDBACK-PLAN #2 的
  「尾端語言提醒」若在 AiInsightService 各呼叫點手寫字串，就是新的重複。
- 出（渲染）：AI 文字在前端至少 4 處各自渲染——chat 泡泡（chat-panel.js）、
  AI 判讀面板（record-detail.js aiInterpretPanel）、儀表板今日焦點（dashboard.js loadAiFocus）、
  查詢歸納（records.js）。「AI 徽章＋淡色區塊＋textContent」的組合每處自己拼，
  #3 的 markdown-lite 若只接 chat，其他處又會分岔。

**共用方案**：
- 進：`PromptGuidelines` 加 `LanguageReminder` 常數（尾端一句話版本），
  Web 四個 AI 呼叫點與批次（若需要）都引用它，不各寫。
- 出：#3 的 `markdown-lite.js` 匯出唯一入口 `renderAiText(container, text, { badge })`——
  內含 DOM 組裝（永不 innerHTML）、AI 徽章、樣式類別。四個渲染點全部改走它；
  之後任何新的 AI 輸出點沒有第二種寫法可抄。

---

## S8 「設定快照 → 重建客戶端」快取模式：抽一個小工具

**現況**：WebAiService 內同一套「lock＋snapshot 比對＋重建」寫了**兩份**
（GetClient／GetChatClient，各 ~30 行只差參數）；WEB-FEEDBACK-PLAN #9 的
DynamicAuthenticationProvider 需要第三份（LdapService 隨 DB 設定重建）。

**共用方案**：Web 端新增 `SettingsBoundClient<TClient>`（建構參數：snapshot 取值函式＋工廠），
三個使用點共用。快取語意（低頻重建、舊實例交給 GC）維持 WebAiService 現有註解的決策。

**風險**：低；WebAiService 行為不變，#9 少寫一份易錯的並行程式碼。

---

## S9 Controller 查詢參數解析：一份靜態工具

**現況**：`ParseDate`／`ParseLongs`／`ParseStrings` 在 RecordsController、AiController、
AuditController、DashboardController 至少四份（逐字相同）。

**共用方案**：`Controllers/Api/QueryStringParsing.cs` 靜態類別收一份；
RecordsController:148 的「解析失敗即丟 Validation」包裝一併收入（`ParseRequiredDate`）。
AuditController 的 `To 補到當日 23:59:59` 屬呼叫端語意，留在原地。

---

## S10 合法值清單與 enum 對齊：用測試看管，不再裸寫

**現況**：`SystemSettingsService.ValidSeverities`（手寫四字串）、
`AiInsightService.KnownCategories`（手寫八字串）、`KnownRisks`（手寫三字串）——
enum（IssueSeverity／IssueCategory）加值時這些清單不會有任何編譯錯誤，靜默漏。

**共用方案**：
- `KnownCategories` → `Enum.GetNames<IssueCategory>()`；`KnownRisks` → `RiskLevels.All`（S2）。
- `ValidSeverities` 承載「畫面勾選順序（由重到輕）」，不宜直接用 enum 宣告順序——
  保留陣列，但加一條測試斷言「陣列集合 == enum 名稱集合」，enum 加值時測試紅燈。

---

## S11 前端嚴重度清單與徽章：format.js 補齊、消除已發生的分歧

**現況（已經出現實際分歧的鐵證）**：
- `SEVERITY_ORDER`（record-detail.js:23）與 `SEVERITIES`（settings.js:11）兩份同值清單；
  reports.js 的 severityKeys（202-208）、dashboard.js severityBreakdown（253-257）又各自拼。
- **樣式分歧**：dashboard.js severityBreakdown 給 Low 用 `secondary` variant，
  format.js `SEVERITY_VARIANT` 給 Low 用 `neutral`——同一個「低」在儀表板與其他頁
  已經是兩種徽章底色。這正是「各自處理」的必然結果。

**共用方案**：format.js（本來就是「顯示格式化的單點定義」）補匯出：
- `SEVERITY_ORDER`（由重到輕陣列）——record-detail／settings／reports 改 import；
- `severityCountBadge(severity, count)`（顏色＋文字計數徽章）——dashboard 的
  severityBreakdown 改用它，Low 的底色回歸 `SEVERITY_VARIANT` 單一標準。

---

## S12 前端本地日期字串：format.js 收一份（順修時區 bug）

**現況**：
- record-detail.js:349-351 手寫 pad 組本地 `yyyy-MM-dd`（正確，附註解）；
- reports.js:430-431 `toISOString().slice(0,10)` 取的是 **UTC 日期**——台灣（UTC+8）
  凌晨 0–8 點開報表頁，預設期間會少算一天。潛在 bug，共用順手修掉。

**共用方案**：format.js 加 `toLocalDateString(date)` 與 `todayLocal()`，兩處改用；
其他頁面日後需要本地日期一律走這裡。

---

## S13 類別／嚴重度中文名的跨語言雙份（P3，選配）

**現況**：類別中文名 C# 一份（批次 RiskReportService.cs:125-133，txt 報告用）、
JS 一份（format.js CATEGORY_NAMES）。跨語言無法靠編譯器對齊。

**共用方案（兩段）**：
1. 先把 C# 版從批次的 RiskReportService 搬到 **Core**（`IssueCategoryNames`），
   批次與 Web 後端共用一份——這步沒有爭議，直接做。
2. 跨到 JS 的單一來源：_Layout.cshtml 由 Core 常數 server-render 一段
   `window.LF_META = {...}`（類別名、嚴重度名、風險等級），format.js 讀它、
   保留現值當 fallback。不加 API 請求、不動快取。
   評估：JS 側 format.js 已是單點，分歧風險低——此步標 P3，晚做或不做都可接受。

---

## S14 前端下鑽 URL 與 KPI 卡渲染（P3，選配）

- `/records?riskLevels=…&from=…&to=…` 的組裝在 dashboard.js／reports.js／record-detail.js
  重複 10+ 處 → format.js 或新 core 模組加 `recordsUrl(params)`（負責 encode 與拼接）。
- dashboard.js renderKpi 與 reports.js renderKpi 的統計卡 DOM 結構高度相似 →
  ui.js 抽 `renderStatCards(container, cards)`，對比徽章（reports 的 comparisonBadge）作為
  card 的可選欄位傳入。
- 皆為純顯示層重構、無行為變化；排 P3，避免與批次 A–E 的頁面改動互相踩線。

---

## 與 WEB-FEEDBACK-PLAN 批次的整合順序

```
批次 0a（共用基礎，先行）：S2 RiskLevels → S3 GetTodo 內建母體 → S9 參數解析 → S10 清單看管
批次 0b（咽喉點）：S1 Repository 嚴重度過濾（先在現有 GlobalFilter 語意下落地，行為含漏套修正）
批次 A：#1/#4 ＋ #3 的 markdown-lite 以 S7 renderAiText 形式落地 ＋ #2 用 S7 LanguageReminder ＋ S11/S12
批次 B：#5 模式簡化（SiteHidden）——只改 GetVisibleSeverities 條件與設定頁，機制已在 0b
批次 C：#6 報表——處理進度直接用 S3 後的 GetTodo；圖表組裝用 S4/S5 的 RecordStatsBuilder
批次 D：#7 批次新增使用者（沿用 SaveUser/SetUserGroups，本來就無重複）
批次 E：#9 AD（DynamicAuthenticationProvider 用 S8 SettingsBoundClient）→ #8
批次 F（收尾，選配）：S13 / S14
```

**測試基準**：每個批次結束跑全量 804；S1/S3 有行為變化（漏套修正、母體過濾位置移動），
其餘為零行為變化的收斂，紅燈即回歸訊號。
