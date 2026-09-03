# 待辦事項（BACKLOG）

> 除非必要否則不要讀取 docs/archive/ 內容，避免浪費 token。
>
> 本文件彙整目前**已知但刻意未做**的項目。每項附觸發條件或建議時機；
> 沒有時程表——遇到相關需求或有餘裕時再排入。

## 前端共用抽取（原 SHARED-STANDARDS-PLAN S13／S14，P3 選配）

- **S13：類別／嚴重度中文名的 C#／JS 跨語言雙份**——**C# 端已收斂**：
  `LogForesight.Core/Analysis/IssueCategoryNames` 是 C# 唯一字典，`RiskReportService.CategoryZh`
  （txt 報告）與 `MailIssueRow.FormatLine`（郵件，批次H 曾短暫長出第三份拷貝、體檢時收斂）
  皆委派它。**剩餘的是 JS 端**：`format.js` 的 `CATEGORY_NAMES`＋`rules.js` 一份局部拷貝，
  跨語言無法靠編譯器對齊，目前用人工保持一致。方案：由 `_Layout.cshtml` server-render
  `window.LF_META = {...}`（類別名/嚴重度名/風險等級）供 `format.js` 讀取、保留現值當
  fallback。分歧風險目前低，晚做或不做皆可接受。

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
  依 `Os` 分流查詢與映射，見 docs/archive/FEEDBACK-12-PLAN.md §4B）皆已實作完成，
  欄位對應見 [docs/NETIQ-API-REFERENCE.md](NETIQ-API-REFERENCE.md)。Web 排程／立即執行本機
  分析後接機房迴圈，逐日/批次取數（多台 Sentinel 平行處理＋回補窗口可設定，
  `NetiqOptions.MaxParallelServers`／`BackfillDays`），當日續跑靠既有 `HasRecord` 機制。
  探索方案（NetIQ 匯入精靈的主機發現）已解決：改用「網段範圍掃描」，完全不碰 ESM API
  （權限被拒）與全站 24h distinct（不可行）。**涵蓋保證改版**：移除自適應時間窗
  （事件越多窗口越短，被裁掉的時間裡安靜主機會**靜默**消失），改為窄化 filter
  （限 System/Application 頻道，成本正比主機數而非事件量）＋殘差輪掃（觸頂時排除已見主機重查）
  ＋全事件短窗補充掃描，見 docs/NETIQ-API-REFERENCE.md §3.4。
  **尚未經過真實 Sentinel 端到端驗證**——下一步是在 Web 主機頁登錄 2~3 台實際主機試跑
  2~3 晚，核對下列尚未實證的細節：
  1. `sev` 的 Warning/Error 確切門檻（目前為候選值，見 NETIQ-API-REFERENCE.md §4）。
  2. Defender/RDP Operational 頻道有無進 Sentinel（沒有則該偵測面誠實申報不適用）。
  3. ~~Linux 主機的欄位形狀／`sev` 對應門檻~~ **多輪 probe（Sentinel「118_linux」）
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
      同構的 `HostStore.Get`／`ISuppressionStore.LoadAll` 每主機日呼叫已收斂
      （改為計畫階段解析一次＋run 級快照，見 `NetiqPipelineService.HostPlan`）——`HasRecord`
      走的是 SQL 而非整份 blob 反序列化，性質不同，仍待放量前實測決定要不要批次化。
  10. Security 頻道規則未涵蓋的「未知失敗 ID」目前不會被撈入 Sentinel 路徑（相對本機模式的
      已知涵蓋縮小）；是否值得靠 `xdasoutcome` 補一條 `NOT xdasoutcome:0` 分支待評估。
- **EVTX 離線匯入**：實際離線調查需求出現時再開規劃。
- **伺服器端 CSV 匯出**：目前清單頁「複製為 CSV」為前端序列化當前頁；伺服器端全量匯出
  應與 `QueryPage` 下推查詢同路徑實作（避免匯出又走一次全撈）。

## AI 整合觀察項（原 docs/archive/FEEDBACK-4-PLAN.md §5 MCP 評估）

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
  41 個 `[FromQuery]` 參數（3 個端點各約 13～15 個；表頭排序功能又加了 `sort`/`dir`
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

## 案件指派與觀察機制遞延項（詳見 docs/archive/FEEDBACK-9-PLAN.md）

- **常設自動指派規則（§6 解讀「乙」）**：讓「此問題簽章之後永遠自動指派給某人」，連未來
  新出現的主機也自動掛。§6 本輪只做「甲」（一次把目前受影響主機批次指派、建案件）。
  乙的實作草案（下一輪立案）：儲存 `auto_assign_rules`（簽章 key→handlerId＋啟用旗標/建立者/
  備註）；掛在 `HostDayPostProcessor.AttachCase` 之後——當日新問題命中規則且該主機無進行中
  案件時自動建案（沿用案件制，不另造同步機制）；UI 於批次指派 modal 加一顆「之後新主機也
  自動指派」勾選即建規則，規則清單/停用放規則或使用者維護頁（下一輪定）；他人已有進行中
  案件不搶走、命中寫稽核。
  **與問題檔案的關係**：`IssueProfile.AutoApply`＋
  `ConclusionStatus` 已經是「這個問題簽章之後永遠自動套用某個結論」的落地（見
  `IssueCaseCoordinator.AttachNewDay` 的 fleet 套用分支）——概念上與這裡的「乙」有重疊
  （都是「簽章 key → 未來新主機日自動套用某個結果」），但落地的**結果**不同：問題檔案
  自動套用的是「結論」（closed 狀態，`HandlingActions.FleetApply`），這裡的乙要的是「自動
  指派處理人」（open/in_progress 狀態，建案件）。兩者若真的都要做，需要先想清楚「自動指派」
  與「自動結論」在 `AttachNewDay` 的三層優先序裡怎麼排（案件優先於問題檔案結論是已定案的
  既有順序，自動指派要插在哪一層）；現況是只做了「自動結論」那一半，「自動指派」仍是本項
  未解決的部分，不要誤以為問題檔案已經涵蓋了這一項。
- **使用者名稱格式剩餘敘事句（§9 未竟）**：§9 已補齊負責人欄徽章、處理面板目前處理人、
  問題查詢處理人欄、權限異動確認人、匯入操作者、NetIQ 更新者。**仍為顯示名稱單值**的是
  少數敘事句：處理歷程「處理人：○○○」、案件敘事「由 ○○○ 處理」、跨主機批次指派略過清單
  「已由 ○○○ 處理中」——這些 DTO 只帶名稱單值，需再擴數個 DTO 或改走 NameFormat 敘事出口。
  使用者實測後若要求完全一致再排入。
- **無案件的跨日觀察**：觀察中（observing）的跨日繼承依附進行中案件（`AttachNewDay` 只掛
  案件）；沒有案件時標記只涵蓋該日，該問題明天再發生會作為新的未處理日現身。若實務上
  「未指派也要跨日觀察」的需求成立，需要類似已知雜訊記憶的主機×簽章記憶機制——
  避免草率引入同一問題的第三套跨日協調機制，觀察需求明確後再規劃。

## 主機分組與案件歷程遞延項（詳見 docs/archive/FEEDBACK-11-PLAN.md）

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

- **報表問題排行的「顯示範圍」選擇器**（原 D6 乙案，SCALE-FIX-PLAN-2026-08-06.md）：
  排除已有結論的問題（規劃出處 docs/archive/SCALE-ISSUE-FIRST-PLAN.md §10.6；
  已由 `IssueHandlingRollupQuery`＋
  `IssueRankingBuilder.ExcludeConcluded`，儀表板重點問題卡與報表問題排行皆已接上、
  兩頁「另有 N 個問題已有結論」數字一致）目前是**固定行為**，沒有讓使用者切換「全部／
  排除已有結論」的選擇器——多數情境固定排除即符合需求，真的需要看全部（例如稽核）時
  再評估要不要加選擇器。既有的「不受『顯示範圍』篩選影響」常駐說明文字仍成立
  （那是日層級 handlingScope 與問題聚合母體不同的另一件事），不受本項影響。

## 成效指標遞延項（明確不做，記入待補）

- **MTTA/MTTR 成效指標輪**：平均認領時間（MTTA，問題出現到有人開始處理）／平均解決時間
  （MTTR，出現到結案）目前沒有任何一頁呈現，只有處理概況的靜態計數（未處理／處理中／已處理）。
  資料基礎已具備：`IssueHandling.created_at`（僅新增時落）＋`IssueCase`
  的建立/結案時間軸＋處理歷程（`RecordHandlingLog`）。**尚待決定的問題**（立案前需要先想清楚）：
  1. 「認領」的定義——案件建立算認領，還是狀態變成 `in_progress` 才算（觀察中算不算已認領）？
  2. 統計母體——依問題（Source,EventId）還是依主機日？兩者的「平均」意義不同。
  3. 呈現位置——報表新增卡片，還是使用者/處理人工作頁的個人指標？
  4. 歷史資料缺口——`created_at` 只在批次B之後新增的紀錄才有值，舊資料無法回溯計算，
     指標上線初期的分母天然縮水，需要在畫面上誠實標示「僅涵蓋 X 日後新增的紀錄」。
  觸發條件：使用者對「處理效率」有明確的稽核／管理需求時再立案規劃。

## 回饋三十輪遞延（登入失敗誤判分辨／NetIQ 探索／規則補強）

- **`/26` 細粒度的 128 子句未實測**：試點時核對是否被 Sentinel 拒絕（症狀是背景工作 failed、
  訊息來自 Sentinel）。子句數推導與「為什麼沒有 /25」見
  [docs/NETIQ-API-REFERENCE.md](NETIQ-API-REFERENCE.md) §3.4。
- **掃描工作的多行程情境未處理**：工作為行程內狀態（機制見 docs/WEB-SPEC.md §9.9a），
  多行程部署／IIS 應用集區回收下「全站僅一工作」的保證不成立、回收即失去進度——
  掃描可重跑，刻意不引入持久化；真的部署成多行程時再議。
- **`KeyDetails` 字串剖析仍留作舊資料 fallback**：4740 帳號比對讀結構化的 `KeyAccounts`、
  跨日關聯的來源 IP 讀 `KeyIps`（皆未截斷、封頂 200）；`ExtractAccountsFromKeyDetails` 與
  `CorrelationAnalyzer.ExtractIps` 只在對應欄位為 null（舊 ContentJson）時走。舊資料隨
  `RawEventRetentionDays`（預設 120 天）自然淘汰後，兩條 fallback 可一併移除。
- **殘留判定的門檻無設定入口**：集中度 0.8、機械型態佔比 0.8、回看 7 天、跨日門檻 2 天
  皆為 `private const`。刻意不開設定（本專案紅線是「新增設定必須有消費端」，且門檻尚未經實測校準）；
  試點觀察後若需要調，先改常數再考慮要不要開設定。
- **舊資料的截斷失真**：A1 之前寫入的 ContentJson 沒有總量／截斷欄位，殘留判定以
  「滿 50 組且無總量」保守推測為疑似截斷而不判定（誤傷的只是剛好 50 組的合法情況）。
  舊資料會隨保留期自然淘汰，不回溯重剖。
- **4768／4769／4776 刻意不加規則**：watchlist 只能整個事件 ID 取數——4769 在 AD 是全域超大量
  事件；4776 的成功與失敗共用同一個 ID，而規則模型沒有成功/失敗（`EntryType`）維度，
  加了會把 DC 上每一次 NTLM 成功驗證都當成「驗證失敗」計數。若未來規則模型擴充 EntryType
  比對條件、或 Sentinel 端可過濾失敗碼，再回頭評估。
- **4688／4663／5140／5145／4648／4616／4104 評估後不加**：量級或誤報面不成比例
  （4688 需另開稽核原則且量級極大；4648 排程與服務例行使用顯式憑證；4616 NTP 例行校時同 ID
  且規則模型無法按發起程序過濾；4104 的價值在指令內容，而 Windows 規則沒有訊息條件維度，
  整 ID 收等於全量雜訊）。
- **Linux `passwd`／`chage` 不納入帳號異動規則**：`passwd` 常由一般使用者例行變更自身密碼
  （或由 PAM 叫用），`chage` 多由自動化排程批次執行，納入無門檻的 Security High 會造成常態誤報。

## 其他遞延項

- **通知管道——Email 以外的推播**（Email 部分已解決）：
  Email（SMTP）已落地（執行摘要／每日週彙總／高風險即時三路觸發），
  並完成可信度重構（按收件人聚合、寄成功才標記、連續失敗熔斷）。剩餘候選是
  Telegram／Teams webhook 之類的即時通訊管道；本系統定位為第二層縱深防禦，即時性要求
  不如第一層監控，待有明確需求再排入。
- **抑制影響面預覽泛型化**（遞延）：`RuleAdminService.PreviewSuppression`
  目前只支援 Rule 型；Signature／Correlation／Volume 三型因此在服務層被限制僅 Host 範圍
  （無預覽的大範圍噤聲太危險，見 docs/RULES-SPEC.md「範圍支援矩陣」）。**若未來要為新三型
  開放 Group／Site、或在規則頁補新三型的建立入口，必須先把預覽泛型化**
  （`(targetType, targetKey, scope, groupId)`；Volume 型的 M 值可用「過去 14 天這些主機
  出現此類總量告警的天數」）——否則等於複製三扇沒有護欄的門。
- **Windows SMART（硬碟健康度）**：本輪審查提出，但 NetIQ Sentinel 路徑取不到 SMART 資料
  （SMART 需要直接存取硬碟控制器，Sentinel 只收集 Windows Event Log，兩者是不同的資料源）——
  要做的話得另外接 agent 或遠端 WMI 查詢，是獨立的資料蒐集題目，不是這批的規則/趨勢/關聯層
  能涵蓋的範圍，待有明確需求與資料源方案時另案規劃。
- **問題簽章級白話說明快取**：低優先。目前每次深入分析都重新呼叫 AI 生成白話說明，同一簽章
  重複出現時內容通常相近，理論上可快取共用；但 AI 呼叫本身已有既有的成本控制設計（統計模式
  短路、報告只在 Other 類別才呼叫），這項最佳化的邊際效益現階段不明顯，暫緩。
- **`SlowTrendAnalyzer` 的離群值敏感度**（重新界定，原標題「中位數化」不準確）：
  `TrendAnalyzer` 的基準是「一組每日次數取中位數」，所以換統計量講得通（先改為中位數、
  再進一步改為非零日中位數）；但 `SlowTrendAnalyzer` 比的是**兩個 7 天窗口的總量和**
  （近 7 天累計 vs 前 7 天累計），結構上根本沒有「基準中位數」這個東西可以換，零膨脹也不會
  讓它退化（`priorTotal > 0` 的守門已擋掉全零前期）。它真正的同類問題是「單日爆量墊高整個
  窗口總量」——那要改的是窗口內的聚合方式（如逐日取中位數再乘天數、或先削峰再加總），
  是另一個設計題，不是把 `Median()` 套上去就好。有實際誤判案例時再另案評估。
- **7-S2 殘餘：blob 內容 header 誤判陷阱不處理**：`HasBlobContent` 這類「先看 header 判斷內容
  是否存在」的檢查方式，在特定邊界情況下可能誤判（例如內容剛好是空陣列 `[]` 的合法狀態，
  跟「檔案不存在」的 header 特徵可能重疊）。審查中被提出，但目前沒有已知的實際受影響案例，
  價值低於修正成本，記錄理由供之後若真的遇到相關 bug 時查閱。
- **批次 4：專案外觀（README Quick Start／LICENSE 等）**：使用者定案本輪不做，留待後續有
  對外發布或新人上手需求明確時再排入。

## 規模化（SCALE-3000）遞延項（詳見 docs/archive/SCALE-3000-PLAN.md）

- **SqlServer 未實測**：測試只跑得到 SQLite。本輪新增的 SQL（首見日冪等合併、處理狀態推導、
  報表／儀表板聚合）已刻意收斂到兩個 provider 都無爭議的語法（避開 `HAVING` 引用外層／未分組欄位、
  問題鍵串接交給 EF 而非手寫 `||`／`+`），但這是「降低風險」不是「已驗證」。正式環境第一次
  跑分析與開報表時要看 nlog 有沒有 SQL 例外。
- **效益未實機量測**：`SqlPerformanceMonitor` 會記錄 `blob:hosts:Read`（S1 快取，單一請求應降到
  0～1 次）、`blob:hosts:ReadVersion`（回饋二十輪 G 補上——版本探測本來不計入任何 key，
  只看 `Read` 會漏掉每次 `GetAll()` 都做的那趟往返，是驗證方法本身的盲區）與各聚合方法的耗時，
  實測時據此確認。前端三頁（設定／詳情／報表）未做瀏覽器實測
  （在 `[Authorize]` 之後），只做了 JS 語法檢查。
- **`scope != all` 的報表路徑仍在記憶體**：`ReportService` 對 `unresolved`／`open`／`unassigned`
  三種顯示範圍仍以 `QueryLightweight` 載入整段期間的紀錄再 `FilterByScope`。母體只有高／中風險日、
  量級小一階，且 366 天上限現在真的擋得住（回饋二十輪 A 之前，起訖顛倒會算出負天數繞過檢查，
  進 service 才被交換成多年區間——那時這條的「已受上限約束」前提其實不成立）；
  `DeriveDayHandling` 已能提供 SQL 端的日狀態，真的成為瓶頸時可接上。
- **`AggregateByDate` 仍逐列物化**（回饋十九輪 E2 遺留）：輕量列、無 Headline，量級＝天數 × 主機數。
  「依日期」視角在 3000 台 × 長區間會感覺到；改法同本輪對 `AggregateByHost` 做的（SQL 端 GROUP BY 後
  才折併別名）。
- **`BatchRunStore` 有與修掉前的 `EfRecordHandlingStore` 相同的雙實例序號結構**（Web Singleton ＋
  分析端 `AnalysisOrchestrator` 自建），且建構式做 `ReadAllRunLines()`／`ReadAllLogs()` 全量讀。
  目前 Web 端只讀（`RunMonitorService` 四個 Get 方法）所以不會撞號，但哪天在 Web 端加寫入路徑
  就會靜默重號；建構式全量讀在執行歷程累積後也會拖慢站台啟動。改法同 S6 對 `EfRecordHandlingStore`
  做的：序號改為每次寫入重讀尾端。
- **初次上線的歷史回補**：實測穩態 3000 主機日／小時，3000 台每日增量約一小時沒問題；但
  `InitialHistoryDays` 預設 120 天 × 3000 台 ＝ 36 萬主機日 ≈ 五天，期間站台數字不完整。
  這是分批上線程序（依 Sentinel 或主機群組分梯次啟用）要解的，不是程式碼。
  回補期間的 AI 積壓由 AI 分析排程獨立消化（排程作業頁「待補分析」計數看得到收斂進度），
  取數不再被 AI 反壓拖慢。
- **年度同期比較的資料前提**：`RetentionDays` 預設 180 天，`compare=yoy` 的比較期資料早已被清除，
  前端會依 `comparisonOutOfRetention` 顯示提示。要做真正的年度比較需把 `RetentionDays` 調到
  760 以上，儲存量靠 `RawEventRetentionDays` 留 120 天壓下來（實測 3000 台兩年：詳情全留約 12 GB、
  詳情只留 120 天約 1.9 GB）；調大後第一次能做完整年度比較是一年後。

## 設計面債務（未排期）

- **`visibleSeverities` 參數仍是選填（形狀未改）**：立場已在回饋二十輪 B1／B2 定案並落地
  ——待辦／KPI／排行／風險類型卡皆尊重 SiteHidden 顯示設定，所有正式呼叫端都已明確傳入。
  但 `IIssueAggregateQuery` 上的參數形狀仍是 `= null` 選填（10 處），編譯器不會逼新呼叫端表態；
  這正是 `IssueRankingBuilder` 註解記錄過的踩雷模式（rollup 參數選填→兩個呼叫端都忘了傳→死了一整輪）。
  改必填要動介面與全部實作／替身，本輪判定非必要而未做；下次動到該介面時一併改。
- **AI 設定頁的「測試連線」**：三種 provider 各自的連線驗證形狀不同（本機端點無金鑰、OpenAI 官方
  要金鑰、Azure 要 deployment），本輪只做儲存驗證（缺必填欄位即回驗證錯誤）。真的打一次
  chat/completions 回範例 JSON 才叫測試連線，留待需求明確時做。
- **`lf_top_issues` 缺持久化的大小寫正規化來源鍵**：首見日合併的兩段 SQL 與
  `EfIssueAggregateQuery` 數處都用 `UPPER(source_name)` 比對／分組，`(event_id, source_name)`
  索引因此無法 seek。加一個寫入時就存大寫的 `source_key` 欄位＋索引可讓這些查詢 sargable。
  本輪（二十輪 C）加了浮水印閘門後，那兩段 SQL 只在資料真的變動時才跑，成本從「每次重啟」
  降成「有新資料時一次」，改 schema＋回填千萬列的風險已不成比例；等真的量到瓶頸再做。
- **依問題視角的白話說明只對 Windows 規則有效**：`KnownIssueCatalog.FindRule` 是
  Windows 專用（Linux 規則要靠 program＋訊息內容比對，見 `FindLinuxRule`），而問題清單這層
  只有 (來源, EventId)，湊不出 Linux 規則的比對條件。Linux 問題因此不會顯示白話說明——
  不是漏做，是這層資訊不足。要補得先決定「用 program 前綴粗略比對」是否可接受
  （會有誤配風險，Linux 規則本來就靠訊息內容區分）。
- **`SetConclusion` 的服務層能力檢查**（防禦縱深）：統一標記的 AutoApply 勾選路徑經
  `IssueHandlingCommandService`（`Assign`＋`Handle`）呼叫 `IssueOwnerAdminService.SetConclusion`，
  繞過了 `IssueOwnersController` 的 `[Permission(Maintain)]`。現行能力矩陣下 Assign 只有
  admin、無實際越權；若日後能力可自訂（出現有 Assign+Handle 但無 Maintain 的角色）就會變成
  真缺口。屆時在 `SetConclusion` 服務層內補能力檢查，把授權判定從 controller attribute
  下沉到服務層。
- **`UpsertFirstSeen` 的 SQLite 日期字串比較**：條件式 UPDATE（`first_seen > {date}`）在
  SQLite 是字典序文字比較，同一天二次寫入時因小數位數格式差異會觸發一次無意義的 UPDATE 並讓
  儲存格式在兩種寫法間漂移（**首見日的值不會錯**，跨日比較方向正確；SqlServer 不受影響）。
  若未來對 `first_seen` 做 ORDER BY／範圍查詢，先改走 EF 型別化路徑消除格式漂移。

## 使用方式

發現新的「已知但不做」項目時，加進本檔對應分類（或新增分類）；解決後移除該條目，
不需要保留刪除紀錄——本檔只反映**現況待辦**，歷史脈絡查 [docs/archive/HISTORY.md](archive/HISTORY.md)
或 git log。

### 問題負責人自動建案的規模觀察

自動建案是「每主機 × 每問題簽章一件」（與人工批次指派同粒度）。一條負責人規則命中普遍性
問題（DCOM 10016 之類）時，第一晚就是每台主機一件、全指派同一人；`VisibilityService.GetCaseGrants`
每個請求把該使用者全部案件建字典、workload 徽章與處理人工作頁跟著放大。目前刻意不加
上限（有設定就要有消費端，且還沒有實測數字）；上線後若出現淹沒，優先選項是「單次執行、
單一 profile 自動建案數上限＋超過只記 log」。另：`AttachNewDay` 每主機日 `GetAll()` 讀整份
問題檔案 blob（索引含有負責人的檔案後字典更大），3000 台規模值得改成每輪快照。

### 首見日的「完整重算」入口

`SchemaUpgrader.MergeIssueFirstSeenSeed` 改增量後，全表掃描的修正段只在初次回補跑一次
（`issue_first_seen_full_done` 旗標）。保留期清理刪列的情況下，種子回補理論上仍可能讓
既有組合的首見日偏晚（重新分析舊日期則不會——`UpsertFirstSeen` 以被分析日 upsert、
只在較早時往前更新，第三十一輪查證確認）。方法的 `force=true` 會完整重算（浮水印視為 0、全掃修正段照跑），但目前沒有使用者入口呼叫它——需要時再決定接成維護頁按鈕或低頻排程。實務影響：首見日只用於
「這個問題第一次出現在機房是什麼時候」的參考顯示，偏晚不影響判定與告警。

### 重新分析模式的 run-preview 預估與監控頁進度列

「立即執行」的重新分析模式（WEB-SPEC §10.9）目前 run-preview 只回台數、不預估「將重跑幾個
主機日／因處理狀態跳過幾日」，確認對話框也只有模式與天數；執行監控頁沒有重跑專屬進度列，
申報走執行詳情的 Milestone。刻意不做：預估需要逐主機掃既有紀錄＋處理狀態，3000 台規模下
預覽查詢成本不成比例。觸發條件：實際使用中出現「按下去才發現重跑範圍與預期差很多」的回饋
時，再評估抽樣預估或非同步預估。

### 「未處理問題」KPI 下鑽到依問題視角時語意不同

計數單位與狀態口徑其實一致（兩邊都是「相異 (Source, EventId) 中有任一主機未處理者」——
`IssueTodoQuery.Aggregate` 與 `RecordListQueryService` 的 `GroupStatus = unhandled > 0 ? open`），
差別在**母體**，而母體會連帶改變狀態：

- 卡片用 `ActionableOccurrences`，母體固定「日風險高／中」；
- 依問題視角用 `LatestOccurrences`，母體是全站「日風險等級顯示」設定允許的等級。

兩者都是取「每個 (主機, 問題) 在母體內的**最近一次**出現」再判定狀態，所以母體不同時
「最近一次」可能落在不同的日子、狀態就不同：某問題在高風險日未處理、之後又出現在低風險日
且已標記處理，卡片看到未處理、列表看到已處理。本機資料實測卡片 5、下鑽 1。

要對齊得讓依問題視角能表達「只看某些日風險等級的主機日」（與嚴重度分開的另一個參數），
或讓卡片改用同一個母體。兩者都會動到既有語意，值得連同「依問題視角要不要有獨立的日風險
篩選」一起決定，不適合順手加。風險類型卡沒有這個問題（母體與列表相同、也不篩嚴重度）。

### 依問題視角的日風險子查詢重複四次

依問題視角的日風險母體限縮（`ApplyRiskLevels`）在一次 `Aggregate` 呼叫裡會下四次同樣的
`lf_daily_records` 子查詢（主查詢＋三個輔助）。**索引部分已解決**——回饋三十六輪已加
`IX_lf_daily_records_risk_date (risk_level, record_date)`；剩下的是「同一個子查詢下四次」本身，
可改成一次取出 record_id 集合再共用。實測仍是瓶頸時再做。

## 權限異動檢核（PERMISSION-CHANGES 輪次遞延）

- **Sentinel 投影新增 `sip`／`shn`**（來源 IP／發起端機器名）：本輪只取已在投影內的 `sun`（操作者帳號）。這兩個欄位要改查詢語句並實機驗證，另案處理。
- **使用者自訂異動類別**：類別是系統內建的固定六類（key 已是資料表欄位）。要開放自訂需引入規則引擎與「規則改動後歷史資料如何重分類」的策略，範圍不成比例。
- **`EnsureCreated` 與 `SchemaUpgrader` 在全新安裝會建出兩份同欄位索引**：`AddIndexIfMissing` 依**索引名稱**判斷存在與否，而 EnsureCreated 建的是 EF 預設命名（`IX_lf_xxx_ChangeId`）、SchemaUpgrader 用的是自訂名稱（`IX_lf_xxx_change_id`），兩者不相等。這不是本輪造成的，`lf_issue_handling` 等表早已如此，影響是寫入吞吐與儲存空間、不影響正確性。要處理的話是全專案 schema 層級的一輪。
- **高風險（特權目標）判定的涵蓋範圍待議**：目前只標「成員新增到特權群組」。終檢建議一併涵蓋「成員移除」（把稽核人員移出 Domain Admins 是入侵後常見動作）與 ACL 授權對象（`權限新增（ACL 規則）` 的授權對象若是 Everyone／Domain Admins）。後者要改成比對 `Before`／`After` 的授權對象而非 `Target`（`Target` 是路徑，永遠不會命中群組關鍵字）。這是安全判定的範圍決策，需要與使用者確認後再改。
- **權限異動的批次確認上限（500）是否放寬**：吵雜 DC 單日曾達 9 萬則，但例行同步成對合併落地後量預期大幅下降。先觀察合併後的實際待確認量，再決定要不要放寬上限或提供「符合篩選全部確認」。
- **權限異動的查詢側效能在量增後未實測**：權限異動不設每主機日筆數上限，吵雜 DC 單日可達數萬則（實例 9.2 萬）。`/permission-changes` 的查詢與分頁在這個量級下是否仍可接受尚未實測；若實測出問題，處理方向是索引與查詢形狀，另案一輪。（寫入側的兩個無上界點已於回饋三十四輪處理：去重改逐主機日查資料庫、`raw_text` 截斷 8000 字、`AppendChanges` 每 500 筆分批。）
- **權限異動的排序在兩個 provider 上定序不同**：`OrderBy(HostName)` 在 SQLite 是 BINARY（大寫排在小寫前），SQL Server 依資料庫定序（多半不分大小寫）。分頁情境下順序不穩定可能造成翻頁時漏列或重複。可改為對已全大寫的 `HostNameKey` 排序。

## 回饋二十七輪遞延

- **AI 用量表格的「AI 整理件數」欄未做**：規劃 B2 的契約寫「每日：呼叫數／AI 整理件數／
  prompt／completion／total」，實作只有五欄（沒有「AI 整理件數」）。原因是後端沒有這個數字：
  計量掛在 `AIService` 這個 HTTP 出口，它看得到「發出幾次呼叫」，看不到「這些呼叫對應幾件
  AI 整理重點」（一件可能因重試而多次呼叫、也可能命中快取而零次呼叫）。要做的話得讓呼叫端
  帶一個「工作項識別」下來，或改在 `AiInsightService`／pipeline 層另記一組件數——
  兩者都會讓計量點從一處變成多處。先觀察使用者實際想看的是不是「呼叫次數」就夠。
- **每日的 `callsWithoutUsage` 有進 DTO 但畫面沒用到**：目前只在摘要區顯示累計值。
  若之後要在 30 天表格逐日標示，欄位已經在了。
- （註：儀表板／報表的**整包回應**已另有 `SummaryCache`——版本戳＋TTL、鍵含可見範圍與
  稽核能力維度；以下兩條是內層 `IssueRankingCache` 的既有缺口，影響被外層 30 秒 TTL
  縮小但未消除，維持遞延。）
- **`IssueRankingCache` 的鍵不含 `hostSnapshot`**：該參數會影響主機分級 → PriorityScore → 排序。
  目前兩個呼叫端（儀表板／報表）都傳同一份 `visibleHosts`，且它可由 `visibleHostIds` 決定，
  所以不會出錯；但若出現第三個呼叫端傳 `null`（退回全表），就會與前兩者共用同一把鍵卻拿到
  不同基準算出的分數。要嘛把它納入鍵，要嘛改成必填參數把假設變成型別保證。
- **快取回傳的是淺副本**：`IssueRankingCache` 複製清單但共用 `IssueRankingDto` 物件。
  目前呼叫端只做 `Take`／過濾、不改欄位，所以安全；哪天有人在取用後改 DTO 欄位，
  30 秒內所有請求都會看到被改過的值。要根治得回傳深副本或把 DTO 改成不可變。
- **報表頁「非全部顯示範圍」仍是記憶體推導**：日層級處理狀態由 `DayHandlingDerivation`
  （TopIssues＋handling＋案件＋嚴重度設定）在記憶體算，無法整段下推 SQL。本輪只把
  「只要高／中風險」下推，長區間仍會載入該範圍的全部 actionable 列。要再往下走得先把
  處理狀態的推導本身表達成 SQL，屬架構級改動。
- **一次報表請求仍有 4 次整份主機表走訪**：本輪從 13 降到 4（別名索引改快取）。
  剩下的來自可見性服務、處理狀態彙總的主機索引、索引首次建立，分屬不同服務邊界，
  要再收斂得把主機快照穿過好幾層 API，代價大於效益。契約上限已寫進測試防回升。
- **手冊渲染器不支援巢狀清單與標題層級**：`markdown-lite.js` 把 `#`~`######` 一律渲染成
  同一種粗體行，縮排的清單項目會脫離父清單。本輪的處理方式是把說明書內容改寫成單層，
  但這個限制會一直存在（下次寫章節的人不會知道）。要嘛在渲染器補層級與巢狀，
  要嘛在說明書寫作規範裡明文寫下來。
- **群組風險概況的列連結與「未處理」欄口徑不同**：該欄現在只計未處理問題數
  （與 KPI 卡同定義），但點列導向的是 `/records?groupIds={id}&riskLevels=高,中`——
  日紀錄視角、不帶 `statuses=open`，看到的不是那個數字所數的東西。整列共用一個連結，
  要收斂得先決定「點群組列要看什麼」（依問題視角還是日紀錄視角），屬版面動線決策。
- **`IssueKey` 的比對全專案是 `StringComparison.Ordinal`，SQL 端卻用 `UPPER(source)`**：
  記憶體端的問題計數已改為大小寫不敏感（KPI 與下鑽同口徑的前提），但處理狀態解析
  （`OccurrenceStatusResolver`）與其他十餘處 `IssueKey` 比對仍是 Ordinal。
  後果是來源名稱大小寫不同的同一問題，處理標記不會互通。要統一得一次改掉全部比對點
  並確認既有 `lf_issue_handling`／`lf_issue_cases` 資料的鍵寫法，範圍大於單輪回饋。

## 回饋三十四輪遞延

- **報告檔遷移器（`ReportFileMigrator` / `ReportFileMigrationHostedService`）尚未退場**：報告已全數存在資料庫，遷移器只在升級時讀舊 `export\` 檔。第三十三輪的報告機制尚未在正式機實測，升級路徑還需要它——待正式機驗證後的輪次再移除（連同 `export` 這個已死的目錄概念）。

- **NetIQ 取數仍以「整個 job 的事件桶」為單位**（回饋三十五輪批次E1 遞延）：`NetiqPipelineService`
  的 `eventsByIp` 持有該 job 全部事件（單 job 上限 10 萬筆），映射完才逐 IP 移除。本輪已在解析端
  加上欄位過濾讓**單筆**事件大小有上界，但**筆數**仍是整批。要再往下壓得改成「逐頁即映射即分析」，
  需一併處理「同一 IP 多台主機共用桶子」的既有語意（移除時機錯了會讓第二台拿到空事件、
  整天被誤判為來源未回報），且必須用真實資料驗證分析結果不變。等本輪的記憶體改善實測數字出來後再評估。

## 報告全文改存資料庫（遞延）

- **報告全文不壓縮**：`lf_reports.content` 以純文字存。單份約 20～30 KB，3000 台 × 180 天約
  4 GB，不值得為此引入 `varbinary` 與雙 provider 的壓縮／解壓路徑。若實際用量超出預期
  （設定頁會顯示實測份數與 MB），再評估 gzip。
- **舊 `export\` 檔案不自動刪除**：遷移後保留為備份，由管理者自行決定何時清掉。
- **既有檔名碰撞的舊資料不修復**：升級前多台主機同日同風險同類別的報告在檔案系統上互相覆蓋過，
  遷移忠實保留使用者當時看到的內容（幾筆紀錄指向同一個檔就是同一份）。升級後不再碰撞。

## PRTG 整合遞延

現況：鏡像層＋觸發式數值取數＋人工主機對應＋**狀態變更型規則（分析層第一階）**＋跨後端資料搬運
（見 docs/PRTG-SPEC.md）。以下是明確不做／待條件成熟的項目，多數要等值型規則的數值基線
累積足夠才有辦法設計。

- **分析層的值型部分（L2~L5：特徵計算／弱訊號偵測／訊號合成／LLM 敘述化）**：狀態變更型規則
  （第一階）已完成並接上 `lf_top_issues` 全鏈；值型規則要看數值趨勢與基線偏移，
  依賴實際累積的 hourly 數值。觸發條件：`/admin/calibration` 校準頁四項判定達「可用」
  （PRTG-SPEC §11，含累積量判斷與匯出檔），必要時再以 §10 搬運原始 hourly。
- **sensor 的人工分類 UI**（分類的是 sensor 的語意類別，與「device 對主機」的對應是兩件事）：
  **依 type 對照表的自動分類已完成**（`category_source = auto`，只填 null 絕不覆蓋）。
  仍未做的是**人工指定分類的畫面**；欄位與「不被同步洗掉」的契約都已就緒，可直接接手。
  觸發條件：自動對照表覆蓋率不足、且有人工調整的實際需求。
- **規則維護頁的「prtg」平台**：**狀態變更型（第一階）已完成**（四條規則、門檻可調、可停用，
  見 docs/PRTG-SPEC.md §9）。仍遞延的是**值型規則**（趨勢、基線偏移）——需要數值基線，
  累積量判斷與匯出見 PRTG-SPEC §11（原始 hourly 另走 §10）。**不會**新增「PRTG+NetIQ」合併平台——合併發生在主機層，
  規則各自歸屬自己的來源。
- **先備欄位／常數尚無寫入邏輯**（不是資料遺失，清單與現況見 docs/PRTG-SPEC.md §2）：
  `PrtgDataQuality.Untrusted`（需要 probe 斷線區間的資料來源）、`lf_prtg_state_changes.quality`
  （恆 `ok`，無品質判定依據）、`lf_prtg_sensors.thresholds_json`（未向 PRTG 索取閾值欄）、
  `lf_prtg_values.min_value`/`max_value`（hourly 聚合只取平均）、`lf_prtg_state_changes.prev_status`
  （PRTG messages 不提供前一狀態）。
- **PRTG 自身健康概要與獨立的 freshness 記錄**：目前的「最新資料時間」是從鏡像資料推導
  （`max(period_start)` 等），不是「最後一次成功同步的時間」——連續數晚擷取到 0 筆時，畫面上
  的時間不會變動，看起來像正常。要分辨這兩者需要獨立的每資料類別同步紀錄。
  觸發條件：實機運行後發現擷取靜默失敗難以察覺時。
- **歷史回填沒有完成水位**：靠寫入冪等達成「重跑不重複」，但**不會跳過已完成的日期**——
  回填 30 天中斷在第 29 天，重跑仍是 30 天全打一次 API。以目前的回填規模可接受。
- **探測與回填的互斥是 TOCTOU**：兩者各自「先看對方狀態再開始」，中間沒有共同的鎖，理論上
  兩個管理員同一秒各按一個按鈕可以同時起跑。實務上是手動觸發的低頻操作，暫不處理。
- **多台 PRTG core server**：本期假設單一 server（設定是 `SystemSettings` 單例）。要支援多台
  需比照 Sentinel 改成 store 化。觸發條件：環境真的出現第二台 PRTG。

### PRTG 第 4 輪未修項（體檢發現，判定不影響正確性）

以下經第 4 輪收尾體檢查證屬實，判定不會靜默造成錯誤結論，故未修；留在這裡免得日後當成新發現重查。

- **`PrtgFinding.Magnitude` 刻意不落 `Count`**（已定位，非缺陷）：整日 Down 會是 1440，
  用它填 `Count` 會讓 PRTG finding 在問題排行的「次數」維度壓過所有真實事件計數。
  它的用途是規則測試、`Detail` 文案與校準數值匯出的門檻分佈統計（PRTG-SPEC §11）。
- **PRTG 種子規則的長文說明沒有被讀**：`KnownIssueSeed` 的四條 PRTG 規則填了
  `PlainExplanation`／`Impact`／`LikelyCauses`／`NextSteps`（驗證器強制非空），
  但 `PrtgFindingMapper` 用的是 `PrtgRuleCatalog` 裡的一句話 `KnownIssue`。
  兩者並非重複（一句話用於問題簽章、長文用於規則維護頁），但長文目前沒有顯示入口。
  （分類／嚴重度／`ElevatesDayRisk`／預設門檻的三份手抄已收斂到 `PrtgRuleCatalog`，
  並有守門測試斷言 mapper 與種子一致。）
- **抑制影響面預覽把平台二分為 linux／非 linux**：`RuleAdminService.PreviewSuppression` 以 `isLinux` 分路，
  `prtg` 規則落到 Windows 分支用 EventIds 比對，命中數恆為 0。預覽只是提示用，不影響抑制本身生效；
  規則頁補 prtg 抑制入口時一併改成三向分路。
- **`PrtgDataPackage.FromDate`／`ToDate` 只寫不讀**：匯入端不校驗區間，屬輕度冗餘。
- **匯出不能選資料類別**：規劃原本允許只匯出某幾張表，實作是固定全類別。
  實際檔案過大時再加。
- **觸發式取數的收尾掃描會多跑一次**：輪詢迴圈是「先掃描、再判斷分析是否完成」，
  跳出後迴圈外還有一次收尾掃描，因此分析一開始就完成時會連續掃兩次。
  功能無害（去重集合擋住重抓，第二次立即返回），代價是多一次查詢。
- **`silent` 規則對「status 欄為空字串」也判定為沉默**：`IsUnknownOrEmpty` 把空值與
  `Unknown` 一視同仁。若某次結構同步成功但 status 欄未被填，會讓該 device 被誤判。
  目前沒有已知會產生空 status 的路徑，故未加保護；真的遇到時可加「最後結構同步時間過舊
  或全為空字串（而非 Unknown）時不判定」。

### 測試穩定性

- **一個時間敏感的偶發失敗測試**：
  `SentinelRestDirectoryClientTests.多段預算用盡回部分結果與警告_不擲例外`。
  單獨執行穩定通過，只在全套並行時偶發，會讓「全綠」不可靠。
  可比照 `PrtgFetchServiceTests` 併發峰值測試的放行閘門作法：前 N 個請求到齊才一起放行，
  斷言強度不變而時間相依消失。

### PRTG 已知未修項（不影響正確性）

以下項目經查證屬實，判定不會靜默造成錯誤結論，故暫不處理；留在這裡免得日後當成新發現重查。
（過程記錄見 docs/archive/PRTG-1-PLAN.md 終檢處置一節。）

- **`ReplaceHostMapForDate` 的刪與寫不在同一交易**（`EfPrtgStore`）：先用一個 context
  `ExecuteDelete` 該日全部列，再用分批寫入補回。刪完之後、寫入之前進程被回收的話，該日對應
  資料會消失，要等下一次每日分析才重建。修法是包一個交易。發生機率低、且下一輪重跑會自癒，
  但「該日舊資料先刪掉」這個順序本身值得改。
- **SQLite 後端下多執行緒同時寫 `lf_prtg_values` 可能踩寫鎖**：階段 4 的併發 task 各自開
  DbContext 寫入，`PrtgFetchConcurrency` 設 2~3 時在 SQLite 上可能出現 `database is locked`。
  正式環境是 SQL Server，SQLite 是測試與小型部署用；真的遇到時把數值寫入收斂成單一寫入者。
- **`PrtgProbeRunner.StepAsync` 的 catch 連 `OperationCanceledException` 一起吞**：取消時
  探測不會立即停，會把剩下的步驟跑完才結束。探測是短時間的唯讀操作，影響有限。
- **`PrtgProbeRunner` 只有 sensor type 分布那一步有「取樣數 < 總數」的截斷警告**，
  dependency／groups／IP 覆蓋三步沒有。sensor 超過 5 萬或 group 超過 1000 時會靜默少算，
  探測結論失真。
- **前端 probe 與 backfill 兩塊的渲染與輪詢邏輯幾乎逐字重複**（`settings.js`），
  `PrtgFetchService` 與 `PrtgProbeRunner` 之間的 `GetStringProperty` 與相依性判定也各有一份。
  兩者都是「同一判定寫兩處」，之後改規則時容易只改一邊。
- **`PrtgProbeStatusDto.StartedAt` / `PrtgBackfillStatusDto.StartedAt` 前端沒有消費點**：
  可以拿來顯示已執行時長，或移除。

- **`PrtgClient` 建構子的認證參數是有預設值的選用參數**（`authMode` 預設 token）：目前正式碼
  只有工廠與測試連線兩個呼叫點、都顯式傳，但日後新增呼叫點若漏傳會**靜默走 token 模式**而不是
  編譯失敗。移除預設值需改動十餘處測試呼叫，暫不動。
- **「併發下 passhash 只換一次」的測試只斷言請求計數**，沒斷言多個併發請求拿到同一組 passhash。

### PRTG UI 重構（已完成，保留結論）

三項已於 PRTG 第 4 輪落地：獨立維護頁 `/admin/prtg`、歷史回填與總開關搬到排程作業頁、
主機頁整合 PRTG 對應（清單篩選／明細區塊／人工對應）。現況見 docs/PRTG-SPEC.md §4a 與 §7。

