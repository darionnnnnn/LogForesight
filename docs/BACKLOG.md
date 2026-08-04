# 待辦事項（BACKLOG）

> 本文件彙整目前**已知但刻意未做**的項目，收斂自 2026-07-28 的文件整併（原散落在
> SHARED-STANDARDS-PLAN、OPS-HARDENING-PLAN 與 refactor/simplify-2026-07 簡化重構分支的
> 體檢紀錄，這些來源文件已歸檔至 [docs/HISTORY.md](HISTORY.md)）。
> 每項附觸發條件或建議時機；沒有時程表——遇到相關需求或有餘裕時再排入。

## 前端共用抽取（原 SHARED-STANDARDS-PLAN S13／S14，P3 選配）

- **S13：類別／嚴重度中文名的 C#／JS 跨語言雙份**——類別中文名 C# 一份（批次
  `RiskReportService.cs`，txt 報告用）、JS 一份（`format.js` 的 `CATEGORY_NAMES`）。
  跨語言無法靠編譯器對齊，目前用人工保持一致。方案：先把 C# 版搬到 Core
  （`IssueCategoryNames`，批次與 Web 共用一份，這步無爭議可直接做）；
  再由 `_Layout.cshtml` server-render `window.LF_META = {...}`（類別名/嚴重度名/風險等級）
  供 `format.js` 讀取、保留現值當 fallback。分歧風險目前低，晚做或不做皆可接受。

- **S14 剩餘部分：前端下鑽 URL 組裝共用**——`/records?riskLevels=…&from=…&to=…` 的組裝在
  `dashboard.js`／`reports.js`／`record-detail.js` 重複 10+ 處，尚未抽出共用的
  `recordsUrl(params)` helper。
  （S14 的另一半——KPI 卡渲染共用——已於 refactor/simplify-2026-07 分支 Phase 7 以
  `core/ui.js` 的 `statCard()` 完成，取代 `dashboard.js`／`reports.js` 各自的 KPI 卡拼裝；
  `dashboard.js` 的分類卡／未回報主機卡與 `imports.js` 的迷你統計格因結構真的不同
  （無卡片外框、多圖示/徽章列）刻意未套用，避免為求一致而過度抽象。）

## 營運與規模擴充（原 OPS-HARDENING-PLAN §10 P2，未排期）

- **NetIQ 接線**：`SentinelStatsSource` 的實際取數邏輯依賴 `--netiq-probe` 真實環境輸出。
  **三輪皆已於 2026-07-29 取得**，技術未決項幾乎全收斂（主機歸屬鍵＝`repip`、`obssvcname` 不斷詞、
  System/Application 確實轉送 Information 級事件），過程中修正一個會讓正式管線片語查詢全面失敗的
  JSON 轉義 bug（見 [docs/NETIQ-API-PLAN.md](NETIQ-API-PLAN.md) §3.5、§8、§9）。
  **`SentinelFieldMap`／`SentinelEventMapper`／`SentinelQueryBuilder`（watchlist→Lucene 產生器）
  已實作完成**（Phase 3，2026-07-29，`LogForesight.Core/Analysis/`，含合約測試證實 Sentinel 路徑
  與本機路徑聚合分類結果同構）。**機房 pipeline 本體（`NetiqPipelineService`）也已實作完成**
  （Phase 4，2026-07-29，`LogForesight/Service/`，只支援 Windows 主機）——`Program.cs` 本機分析後
  接機房迴圈，逐日/批次取數（2026-07-30 起多台 Sentinel 平行處理＋回補窗口可設定，
  `NetiqOptions.MaxParallelServers`／`BackfillDays`，docs/FEEDBACK-3-PLAN.md #1/#2），
  當日續跑靠既有 `HasRecord` 機制。**尚未經過真實
  Sentinel 端到端驗證**（試點閘門：Web 主機頁登錄 2~3 台實際主機跑 2~3 晚，核對 sev 門檻、
  Defender/RDP 頻道覆蓋、真實批次耗時；2000 台規模放量前需評估逐主機 `HasRecord` 查詢的批次化）。
  **探索方案已解決**（Phase 5，2026-07-29）：ESM 權限被拒、全站 24h distinct 不可行皆走不通，
  改用使用者實測驗證過的「網段範圍掃描」——`repip:{prefix}.*` 前綴萬用字元查詢＋自適應時間窗，
  完全不碰 ESM API（見 [docs/NETIQ-API-PLAN.md](NETIQ-API-PLAN.md) §3.4「Phase 5 定案」）。
  見 [docs/LINUX-RULES-PLAN.md](LINUX-RULES-PLAN.md) §10 的 P3 閘門（Linux 那台 Sentinel
  尚未接入，此環境 Windows/Linux 已完全拆分成不同 Sentinel）。
- **EVTX 離線匯入**：實際離線調查需求出現時再開規劃。
- **伺服器端 CSV 匯出**：目前清單頁「複製為 CSV」為前端序列化當前頁；伺服器端全量匯出
  應與 `QueryPage` 下推查詢同路徑實作（避免匯出又走一次全撈）。

## AI 整合觀察項（原 FEEDBACK-4-PLAN.md §5 MCP 評估，2026-07-30）

- **LogForesight as MCP server（供外部 AI 客戶端使用，非內建小模型）**：評估「詢問 AI 現場取數」
  該不該讓地端小模型透過 MCP 自主決定查什麼時，結論是不採（模型無 function calling、地端小模型
  工具遵循度不可靠、多輪 agent loop 對 60 秒逾時預算不夠、log 內容攻擊者可控會把工具呼叫決策
  暴露給注入內容），改採伺服器端確定性預取（見 docs/WEB-SPEC.md §9.3 詢問 AI 對話區塊一節）。
  但反過來——把 LogForesight 的查詢能力（問題查詢／主機詳情／處理人工作頁／問題案件）包成
  **MCP server 讓外部 AI 客戶端**（例如 Claude Desktop／Claude Code）直接查——是獨立的整合題目，
  與「餵 context 給內建小模型」無關。觀察需求出現、有明確使用情境（例如分析師想在自己的 AI 工具
  裡直接問「這台主機最近有哪些未結案問題」）時再另案規劃。

## 本次簡化重構（refactor/simplify-2026-07）遞延項

- **`RecordsController` 的查詢參數尚未收斂為查詢模型類別**：`RecordsController.cs` 目前仍有
  41 個 `[FromQuery]` 參數（3 個端點各約 13～15 個；2026-07-29 表頭排序功能又加了 `sort`/`dir`
  各三份，收斂的價值更高了），Phase 6f 體檢時判斷「model binding 語意屬
  行為相鄰、無把關測試」而暫緩合併成單一查詢模型類別，改記入本清單。需要先补一輪端到端測試
  釘住目前的 model binding 行為（空值/預設值/大小寫等），才能安全地做這個重構。

## 回饋第八輪遞延項（docs/FEEDBACK-8-PLAN.md，2026-08-04）

- **使用者名稱「顯示名稱(帳號)」格式的全面套用**：本輪依規劃盤點清單收斂了主要顯示點
  （處理人連結欄、指派下拉、操作者、稽核帳號欄、各設定頁更新者、TriggerText、工作頁標題），
  但**名稱清單與敘事句**（負責人欄徽章、處理面板目前處理人、歷程「處理人：○○○」、
  「由 ○○○ 處理」等案件敘事、權限異動確認人）維持顯示名稱或帳號單值——這些點的 DTO
  只帶名稱單值，全面套用需再擴 6+ 個 DTO。使用者實測後若要求全面一致再排入。
- **無案件的跨日觀察**：觀察中（observing）的跨日繼承依附進行中案件（`AttachNewDay` 只掛
  案件）；沒有案件時標記只涵蓋該日，該問題明天再發生會作為新的未處理日現身。若實務上
  「未指派也要跨日觀察」的需求成立，需要類似已知雜訊記憶的主機×簽章記憶機制——
  避免草率引入同一問題的第三套跨日協調機制，觀察需求明確後再規劃。

## 使用方式

發現新的「已知但不做」項目時，加進本檔對應分類（或新增分類）；解決後移除該條目，
不需要保留刪除紀錄——本檔只反映**現況待辦**，歷史脈絡查 [docs/HISTORY.md](HISTORY.md)
或 git log。
