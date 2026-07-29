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

- **NetIQ 接線**：`SentinelStatsSource` 的實際取數邏輯（欄位對應、watchlist→Lucene 產生器）
  依賴 `--netiq-probe` 在真實 Sentinel 環境的輸出。**第一輪已於 2026-07-29 取得**（Windows 欄位
  對應大致定案，見 [docs/NETIQ-API-PLAN.md](NETIQ-API-PLAN.md) §3.5），但同時推翻了原本的事件量級
  估計與探索設計；**第二輪（probe 步驟 6～12，需 `--sample-ip`／`--sample-linux-ip`）尚未執行**，
  主機歸屬鍵、探索方案、頻道覆蓋現況都待它收斂。見同文件 §8、§9 與
  [docs/LINUX-RULES-PLAN.md](LINUX-RULES-PLAN.md) §10 的 P3 閘門。
- **EVTX 離線匯入**：實際離線調查需求出現時再開規劃。
- **伺服器端 CSV 匯出**：目前清單頁「複製為 CSV」為前端序列化當前頁；伺服器端全量匯出
  應與 `QueryPage` 下推查詢同路徑實作（避免匯出又走一次全撈）。

## 本次簡化重構（refactor/simplify-2026-07）遞延項

- **`RecordsController` 的查詢參數尚未收斂為查詢模型類別**：`RecordsController.cs` 目前仍有
  35 個 `[FromQuery]` 參數（3 個端點各約 11 個），Phase 6f 體檢時判斷「model binding 語意屬
  行為相鄰、無把關測試」而暫緩合併成單一查詢模型類別，改記入本清單。需要先补一輪端到端測試
  釘住目前的 model binding 行為（空值/預設值/大小寫等），才能安全地做這個重構。

## 使用方式

發現新的「已知但不做」項目時，加進本檔對應分類（或新增分類）；解決後移除該條目，
不需要保留刪除紀錄——本檔只反映**現況待辦**，歷史脈絡查 [docs/HISTORY.md](HISTORY.md)
或 git log。
