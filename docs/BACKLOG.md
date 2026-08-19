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
  這是分批上線程序（依 Sentinel 或主機群組分梯次啟用）要解的，不是程式碼；
  `AiFollowupQueue.Capacity = 200` 回補期間必然長時間背壓，畫面會誠實顯示「搜尋暫停中」，
  屬正確行為，刻意不做成設定。
- **年度同期比較的資料前提**：`RetentionDays` 預設 120 天，`compare=yoy` 的比較期資料早已被清除，
  前端會依 `comparisonOutOfRetention` 顯示提示。要做真正的年度比較需把 `RetentionDays` 調到
  760 以上，儲存量靠 `DetailRetentionDays` 留 120 天壓下來（實測 3000 台兩年：詳情全留約 12 GB、
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
- **儀表板單次請求仍發約 10～12 支聚合查詢**：資料一天只變一次，加一層短 TTL 的行程內快取
  成本不高。刻意不做的理由：快取鍵必須含可見範圍與顯示設定的雜湊，這類與授權相關的快取
  一旦鍵漏了維度就是越權（使用者看到不該看的主機）；且回饋二十輪 E 改寫
  `GetDayHandlingRaw` 後成本形狀已變，應先用 `issues:GetDayHandlingRaw` 等效能鍵實測，
  再決定要不要快取、快取哪一層。
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
問題檔案 blob（既有行為，本輪索引放寬讓字典更大），3000 台規模值得改成每輪快照。

### 首見日的「完整重算」入口

`SchemaUpgrader.MergeIssueFirstSeenSeed` 改增量後，全表掃描的修正段只在初次回補跑一次
（`issue_first_seen_full_done` 旗標）。保留期清理刪列、或重新分析舊日期產生新 record_id
這兩種情況下，理論上仍可能讓既有組合的首見日偏晚。方法的 `force=true` 會完整重算（浮水印視為 0、全掃修正段照跑），但目前沒有使用者入口呼叫它——需要時再決定接成維護頁按鈕或低頻排程。實務影響：首見日只用於
「這個問題第一次出現在機房是什麼時候」的參考顯示，偏晚不影響判定與告警。
