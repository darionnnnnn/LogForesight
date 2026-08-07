# 待辦事項（BACKLOG）

> 本文件彙整目前**已知但刻意未做**的項目，收斂自 2026-07-28 的文件整併（原散落在
> SHARED-STANDARDS-PLAN、OPS-HARDENING-PLAN 與 refactor/simplify-2026-07 簡化重構分支的
> 體檢紀錄，這些來源文件已歸檔至 [docs/archive/HISTORY.md](archive/HISTORY.md)）。
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

- **NetIQ 接線（試點驗證）**：取數邏輯（`SentinelFieldMap`／`SentinelEventMapper`／
  `SentinelQueryBuilder`，watchlist→Lucene 產生器）與機房 pipeline 本體
  （`NetiqPipelineService`，`LogForesight.Core/Service/`，Windows／Linux 主機皆支援，
  依 `Os` 分流查詢與映射，見 docs/FEEDBACK-12-PLAN.md §4B）皆已實作完成，
  欄位對應見 [docs/NETIQ-API-REFERENCE.md](NETIQ-API-REFERENCE.md)。Web 排程／立即執行本機
  分析後接機房迴圈，逐日/批次取數（多台 Sentinel 平行處理＋回補窗口可設定，
  `NetiqOptions.MaxParallelServers`／`BackfillDays`），當日續跑靠既有 `HasRecord` 機制。
  探索方案（NetIQ 匯入精靈的主機發現）已解決：改用「網段範圍掃描」，完全不碰 ESM API
  （權限被拒）與全站 24h distinct（不可行）。**2026-08-06 涵蓋保證改版**：移除自適應時間窗
  （事件越多窗口越短，被裁掉的時間裡安靜主機會**靜默**消失），改為窄化 filter
  （限 System/Application 頻道，成本正比主機數而非事件量）＋殘差輪掃（觸頂時排除已見主機重查）
  ＋全事件短窗補充掃描，見 docs/NETIQ-API-REFERENCE.md §3.4。
  **尚未經過真實 Sentinel 端到端驗證**——下一步是在 Web 主機頁登錄 2~3 台實際主機試跑
  2~3 晚，核對下列尚未實證的細節：
  1. `sev` 的 Warning/Error 確切門檻（目前為候選值，見 NETIQ-API-REFERENCE.md §4）。
  2. Defender/RDP Operational 頻道有無進 Sentinel（沒有則該偵測面誠實申報不適用）。
  3. ~~Linux 主機的欄位形狀／`sev` 對應門檻~~ **四輪 probe（2026-08-07，Sentinel「118_linux」）
     全數定案並實作完成**（`Program=sp`、主機歸屬鍵沿用 `repip`、`sev` 不可靠承載 syslog
     priority 語意改採計數用途的務實映射，見 [docs/NETIQ-API-REFERENCE.md](NETIQ-API-REFERENCE.md)
     §4a、[docs/LINUX-RULES.md](LINUX-RULES.md)）。
  4. 真實 watchlist 形狀查詢（事件 ID 集合＋50 台 IP 批次）的耗時與命中量，決定夜間窗時程與
     批次大小。
  5. `dt` 時間邊界的人工核對（絕對時間區間需在 Sentinel Web UI 重現比對）。
  6. 8.5 apidoc 是否有伺服器端聚合端點（有的話 Q1 查詢可以改走聚合，目前是本地聚合的退路）。
  6b. **`NOT` 子句（`AND NOT (repip:a OR repip:b …)`）是否被此環境的 Lucene 接受**——
      探索的殘差輪掃與重掃增量都靠它（docs/NETIQ-API-REFERENCE.md §3.4）。
      probe 只驗過 OR 子句、片語、前綴萬用字元，沒驗過 NOT。實作已加偵測
      （取回事件含已排除的 repip 即判定未生效、停止輪掃並顯性警告），
      試點時核對是否曾出現該警告。
  7. 多網卡主機以哪個 IP 回報（有「查無資料」假象的風險）。
  8. token 有效期長短（決定長輪收集中是否需要主動換發）。
  9. 2000 台規模放量前需評估逐主機 `HasRecord` 查詢的批次化（目前是 O(主機數×天數) 個別查詢）。
  10. Security 頻道規則未涵蓋的「未知失敗 ID」目前不會被撈入 Sentinel 路徑（相對本機模式的
      已知涵蓋縮小）；是否值得靠 `xdasoutcome` 補一條 `NOT xdasoutcome:0` 分支待評估。
- **EVTX 離線匯入**：實際離線調查需求出現時再開規劃。
- **伺服器端 CSV 匯出**：目前清單頁「複製為 CSV」為前端序列化當前頁；伺服器端全量匯出
  應與 `QueryPage` 下推查詢同路徑實作（避免匯出又走一次全撈）。

## AI 整合觀察項（原 docs/archive/FEEDBACK-4-PLAN.md §5 MCP 評估，2026-07-30）

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

## 架構重構（feature/arch-refactor）遞延項

- **`KnownIssueCatalog` 的 static 可變狀態改實例化**：`Rules`／`SecurityAuditWatchlist`／
  `ChannelWatchlists` 是三個獨立的 static 可變屬性，`RuleBootstrapper.Run` 呼叫
  `Initialize(...)` 依序覆寫——非原子（理論上讀者可能讀到 Rules 已更新但 Watchlist 還沒
  更新的中間態）。12 個檔案直接引用（貫穿 LogAnalysisService／CorrelationAnalyzer／
  RuleValidator／RiskReportService 等整條分析管線），且測試已必須在建構式/Dispose 手動呼叫
  `Initialize(KnownIssueSeed.CreateRules())` 重置共用靜態狀態，避免測試間互相汙染
  （ChannelWatchlistTests／KnownIssueCatalogTests／RuleBootstrapperTests／
  SentinelPipelineContractTests 等）。改為實例注入需要貫穿整條分析管線的建構式，
  風險與範圍等同 god class 拆分（Phase 7 等級），不是收斂性質的小清理，故不在本次
  架構重構範圍內處理。折衷方案（三個屬性包成一個不可變快照、用單次原子替換取代逐一
  覆寫）可以先解決「非原子」的理論風險且不需要動呼叫端，若之後要做可從這裡切入。

## 回饋第九輪遞延項（docs/archive/FEEDBACK-9-PLAN.md，2026-08-05）

- **常設自動指派規則（§6 解讀「乙」）**：讓「此問題簽章之後永遠自動指派給某人」，連未來
  新出現的主機也自動掛。§6 本輪只做「甲」（一次把目前受影響主機批次指派、建案件）。
  乙的實作草案（下一輪立案）：儲存 `auto_assign_rules`（簽章 key→handlerId＋啟用旗標/建立者/
  備註）；掛在 `HostDayPostProcessor.AttachCase` 之後——當日新問題命中規則且該主機無進行中
  案件時自動建案（沿用案件制，不另造同步機制）；UI 於批次指派 modal 加一顆「之後新主機也
  自動指派」勾選即建規則，規則清單/停用放規則或使用者維護頁（下一輪定）；他人已有進行中
  案件不搶走、命中寫稽核。
- **使用者名稱格式剩餘敘事句（§9 未竟）**：§9 已補齊負責人欄徽章、處理面板目前處理人、
  問題查詢處理人欄、權限異動確認人、匯入操作者、NetIQ 更新者。**仍為顯示名稱單值**的是
  少數敘事句：處理歷程「處理人：○○○」、案件敘事「由 ○○○ 處理」、跨主機批次指派略過清單
  「已由 ○○○ 處理中」——這些 DTO 只帶名稱單值，需再擴數個 DTO 或改走 NameFormat 敘事出口。
  使用者實測後若要求完全一致再排入。
- **無案件的跨日觀察**：觀察中（observing）的跨日繼承依附進行中案件（`AttachNewDay` 只掛
  案件）；沒有案件時標記只涵蓋該日，該問題明天再發生會作為新的未處理日現身。若實務上
  「未指派也要跨日觀察」的需求成立，需要類似已知雜訊記憶的主機×簽章記憶機制——
  避免草率引入同一問題的第三套跨日協調機制，觀察需求明確後再規劃。

## 回饋第十一輪遞延項（docs/archive/FEEDBACK-11-PLAN.md，2026-08-05）

- **本機主機失去「第一次分析前預先建檔＋分組」的批次途徑**（§2a 退役 hosts.csv 的已知損失）：
  本機主機現一律由批次分析首次執行時自動 Touch 登錄，之後才能在主機頁批次設定群組——
  上線初期若要「機器還沒回報就先掛好群組授權」，得等第一晚批次跑完。兩千台情境的主力是
  NetIQ 掃描匯入（可在匯入當下就指派群組），本機主機量少故接受。
  觸發條件：若出現「大批本機主機需在首次分析前就分好群組」的實際需求，再評估補一支
  只做「建檔＋分組」的輕量匯入——**不要把整個 hosts.csv 復活**，它當初的問題是一張表
  混了建檔／分組／負責人／OS 四件事，四種語意各有各的取代規則。

- **被指派歷程不含「已被改派走」的案件**（§3）：`IssueCase` 只保存**目前**處理人，
  案件改派後 `GetByHandler` 就查不到，使用者詳細頁的歷程因此看不到「他曾經被交辦、
  後來被改派給別人」那一段（那次改派記在該主機當日的處理歷程 `case_reassign`）。
  觸發條件：若稽核要求「單一使用者的完整交辦軌跡」，需在案件上記處理人變更歷史，
  或改由 `RecordHandlingLog` 的 `case_reassign` 反查——兩種作法都要先想清楚
  「歷程以案件為單一事實來源」會不會因此被拆成兩份。

- **儀表板「重點問題」卡不含未處理數**（§8-1）：卡片只做純紀錄聚合，處理概況要逐問題查
  handling 標記。**2026-08-06 更新**：`IssueRankingDto` 已備妥 `OpenHostCount`／
  `ResolvedHostCount` 兩個欄位與 `IssueRankingBuilder` 的 rollup 參數，
  `IIssueAggregateQuery` 也已回傳該群組的相異完整簽章（join 處理狀態的鍵）——
  剩下的只是把 `lf_issue_handling` 的逐簽章彙總接上去，並依 §10.6 讓「全部主機都已有結論」
  的問題退出重點清單（含卡底「另有 N 個問題已有結論（未列入）」的誠實出口）。
  屆時一併做 **D6 乙案**（SCALE-FIX-PLAN-2026-08-06.md）：報表問題排行套用「顯示範圍」
  選擇器——同一次 join 的事，甲案的常駐說明文字屆時移除。

## 使用方式

發現新的「已知但不做」項目時，加進本檔對應分類（或新增分類）；解決後移除該條目，
不需要保留刪除紀錄——本檔只反映**現況待辦**，歷史脈絡查 [docs/archive/HISTORY.md](archive/HISTORY.md)
或 git log。
