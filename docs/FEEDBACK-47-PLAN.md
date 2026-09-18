# 回饋第 47 輪規劃：交辦單——大量主機下的派工、處理與靜音模式

> 狀態：實作中（分支 `feature/feedback-47`，2026-09-17 自 dev@d8e431b 開出）
> 基準：dev@d8e431b（第 46 輪已併入並歸檔；4293 綠、略過 6）
> 來源：使用者回饋——主機量大後「管理員逐一派工、使用者逐台回覆」難以維運；管理員一直派同樣的問題很累、不知道大家負擔重不重；需要能把某個問題靜音一段時間。
> 執行方式：**`impl-low`（Opus 5＋low effort）**，整輪不換；使用者 2026-09-16 同意沿用。subagent 核對走 `scan-low`（本輪已派三次，沿用）。
> UI 段（C-3、C-4、D-2、B-3）：使用者 2026-09-17 定案**不套用 `ui-ux-pro-max`**，全部沿用既有設計系統（`docs/DESIGN-SYSTEM.md`）與現有頁面元件。
> 版本：第二版依四角度複審補強；第三版補名詞表、端到端操作劇本、各階段契約要點、風險與回滾；第四版以 4000 台情境逐步模擬，落為定案 31～42、劇本 S8／S9、規模表與壓測四案例；第五版再檢視解法，三處換成更根本的做法（靜音區間、案件日同步作業、缺口＝派工試跑，定案 32／34／36／43／44，第十三節末）；第六版逐共用點 grep 呼叫端做既有功能影響矩陣（第十四節，定案 45～47）。定案 10、22 的敘述以定案 34 為準。

## 〇、名詞表（同步進 WEB-SPEC §8.6a）

| 名詞 | 英文識別 | 定義 |
|---|---|---|
| 交辦單 | work order | 一個問題 × 一組主機 × 一位處理人 × 說明／期限；派工、通知、檢視、回覆的單位 |
| 成員 | member | 交辦單下的一個（主機, 問題）案件；`IssueCase.WorkOrderId` 指回單 |
| 進行中／已結案／上報中／暫停／未回覆 | active／closed／escalated／paused／unreplied | 交辦單的推導狀態（不落盤為主狀態；`ClosedAt` 只是快取） |
| 範圍 | scope | 建單時記錄的主機條件：全站（`All`）／主機群組集合（`Groups`）／指定主機（`Hosts`） |
| 續掛 | auto-attach | 進行中交辦單在夜間掛接時自動納入範圍內新出現的同問題主機 |
| 派工池 | dispatch pool | 使用者群組旗標；池成員可被自動派工選中 |
| 暫停接單 | dispatch paused | 使用者旗標；自動派工與看板候選排除 |
| 派工閘門 | dispatch gate | ④⑤⑥ 共用的三道排除：已抑制、已知雜訊記憶、嚴重度不列入未處理計算 |
| 待派 | gap | 期間內出現、通過閘門、未靜音、卻沒有任何進行中案件的（問題→主機） |
| 代為結案 | admin close | admin 以自己名義把整張單結案 |
| 取消交辦 | cancel | 收回派錯的單：關案件、同步寫入的日子回到明確未處理 |
| 靜音 | mute | 問題檔案層級、有期限的「不吵、不顯示、不進待辦」 |
| 靜音區間 | mute interval | 問題檔案上的（起日, 迄日, 原因, 操作者）清單；某日某問題是否靜音只看紀錄日是否落在區間內 |
| 案件日同步作業 | case day sync worker | 背景把案件狀態展開到它涵蓋的逐日列；小量就地完成、大量分批 |

## 一、核對結果（規劃前事實）

| 項 | 判定 | 證據 |
|---|---|---|
| 工作單位＝「一台主機 × 一個問題」的案件；沒有「一個問題跨 N 台」的實體 | ❌ 根因 | `IssueCase.cs:13-26`；`IIssueCaseStore`（`IHandlingStores.cs:68-100`）無依問題跨主機查、無分頁；`lf_issue_cases` 無 IssueKey 前導索引（`LfDbContext.cs:276-277`） |
| 依問題視角「指派」預覽硬切 200 台、剩餘無續做入口、主機清單由前端送回、無規模守門 | ❌ | `IssueHandlingCommandService.cs:402-403,682`；`records.js:1416-1422`；bulk-close 有 5000 守門（:810）、bulk-assign 沒有 |
| 處理人「回覆處理狀態」＝自己名下該問題**全部**進行中案件、全有全無；無法部分回覆 | ❌ | `IssueHandlingCommandService.cs:592-606`；`issue-status-reply.js:104-110`（payload 無 hostIds） |
| 處理人得知有工作的唯一管道是側欄徽章；儀表板無「我的工作」視角；無派工通知 | ❌ | `dashboard.js:222-237`（待辦卡是全站口徑）；`MailNotificationService.cs` 只有執行摘要／週報／escalation |
| 工作沒有邊界：昨天被交辦 100 台、今晚自動建案變 120 台，分不出哪些是新的 | ❌ | 工作頁分組在前端（`handler-detail.js:155-185`），資料是「目前進行中案件」的即時集合 |
| 問題檔案負責人已會夜間自動建案（每主機一件、只給第一位負責人）；**不看 `Suppressed`、不看嚴重度、不看已知雜訊記憶** | ⚠️ 已有但有缺口 | `IssueCaseCoordinator.cs:270-322` |
| 日層級指派直接 `BuildCase`，不經任何單 | ⚠️ | `DayHandlingCommandService`（`PUT …/handling/assign`） |
| 案件沒有「取消／解除指派」操作，只有改派 | ⚠️ | `IssueCaseCoordinator.cs` 四個公開方法；Web 端 grep `CancelCase`／`Unassign` 零命中 |
| 抑制已有到期（`ExpiresAt`）、全站範圍（`Site`）、簽章目標；到期只是不再生效不自動刪 | ✅ 可搭 | `RuleSuppression.cs:120-121`；`SuppressionFilter.cs:20,79-81` |
| `lf_top_issues` 沒有 suppressed 欄；抑制中的問題仍進依問題視角與儀表板聚合；日狀態推導只看嚴重度、**不排除 `Suppressed`** | ⚠️ | `LfDbContext.cs:573-600`；`EfIssueAggregateQuery.cs` 零 `Suppressed` 命中；`DayHandlingDerivation.cs:37` |
| 「不處理（預設）」「已知雜訊（自動）」是**讀取時推導**、不落盤 | ✅ 先例 | WEB-SPEC §9.3-2、§9.3-3 |
| 可見範圍規則 `HostVisibilityResolver` 是 Web 的靜態純函式，參數全是 Core store | ✅ 可下沉 | `HostVisibilityResolver.cs:15-90`；`IIdentityStores.cs:45`；`IHostStores.cs:114,131` |
| 夜間掛接在 Core 自建 coordinator，不經 Web DI | ✅ 事實 | `AnalysisOrchestrator.cs:343-349` |
| 群組是授權範圍單位＋角色載體，沒有「可被派工」語意 | ✅ 事實 | WEB-SPEC §7.1:433-437；`WebIdentity.cs:62-78` |
| 使用者有 `Email` 欄位；郵件已有逐收件人路由與 admin 群組解析 | ✅ 可用 | `WebIdentity.cs:37`；`MailNotificationService.cs:248-256,448` |
| 頁面慣例：`PagesController` 一個 action 回 View、`Views/Pages/*.cshtml` 只載入 `~/js/pages/*.js` 模組；nav `requires` 已支援陣列（任一）；CSV＝前端序列化當前頁（§8.6-7）；稽核對照表 `AuditQueryService.ActionNames` | ✅ 慣例 | `PagesController.cs:46,106`；`HandlerDetail.cshtml:36`；`layout.js:110`；`records.js:1863`；`AuditQueryService.cs:41` |
| 規模疑慮 15 處（每請求把使用者全部案件建字典、工作頁 N+1、側欄徽章每頁打整份 workload、建案逐台掃全歷史、preview／plan 逐台查案件） | ⚠️ | `VisibilityService.cs:252`；`HandlingHistoryQueryService.cs:364-422`；`layout.js:212`；`IssueCaseCoordinator.cs:400-408`；`IssueHandlingCommandService.cs:383,861-867` |

順手抓到（本輪一併處理）：

| 項 | 判定 | 證據 |
|---|---|---|
| WEB-SPEC §9.3 說案件存 blob，§10.2 說是真表——同檔矛盾，真表是現況 | ❌ 文件 | WEB-SPEC.md:1161-1162 vs :2632-2653 |
| DB-SPEC 仍定義 `lf_user_host_map` 且查詢對應表建立在它上；WEB-SPEC §10.1 已明寫移除、程式碼零引用；DB-SPEC 也缺 `lf_issue_cases` 欄位定義 | ❌ 文件 | DB-SPEC.md:78-85,452,587；WEB-SPEC.md:2621 |
| BACKLOG「常設自動指派規則（乙）」說問題檔案只做了自動結論那一半；實際 `OwnerAutoAssign` 就是自動指派 | ❌ 文件過時 | BACKLOG.md:104-121 vs `IssueCaseCoordinator.cs:313` |
| 問題檔案多位負責人只有第一位拿到案件 | ⚠️ 行為 | `IssueCaseCoordinator.cs:318` |
| 統一標記的「之後自動套用」呼叫 `SetConclusion` 繞過 `Maintain`（BACKLOG 3-10） | ⚠️ 授權 | `IssueHandlingCommandService.cs:767-770` |
| bulk-assign 沒有對應 bulk-close 的規模守門 | ⚠️ | `IssueHandlingCommandService.cs:810` 只用於 BulkClose |

## 二、方向與定案

### 根因

系統的工作單位太細。每一個批次功能（批次指派、統一標記、一次回覆、工作頁依問題分組）都是在「每主機一件」的粒度上用迴圈與前端分組硬撐；主機量一大就露出 200 台預覽上限、全有全無回覆、沒有邊界、沒有通知、admin 每天重複派同一種問題。

### 定案總表

| # | 定案 | 不選的方案與理由 |
|---|---|---|
| 1 | 新增**交辦單**一層。既有案件不廢除，改為交辦單的**成員**。逐日 `IssueHandling` 投影、跨日同步、儀表板、報表零改動 | 不做「第三套狀態機」：交辦單**不存自己的狀態**。推翻 DB-SPEC「更細的追蹤應接公司工單系統而非在此重造」——那條原則寫在單機時期，本專案的處理鏈已是唯一事實來源，接外部工單只會多一份漂移 |
| 2 | 資料模型允許「多問題單」（問題欄可為 null），**本輪 UI 只從依問題視角建單（單一問題）**；主機詳情頁多問題打包列 BACKLOG | 一開始鎖單一問題日後要改鍵；現在只是欄位可空 |
| 3 | 派工端**後端依篩選條件解析主機集合**，前端不再送主機清單；取消 200 台預覽上限；不設成員數上限（寫入量由定案 32 的同步作業吸收），預覽誠實顯示台數與預估主機日 | 維持前端送清單會撐爆 payload、永遠有續做問題 |
| 4 | 群組分攤＝**一位處理人一張單**（依現有負載或平均輪流，沿用既有兩種確定性分攤） | — |
| 5 | 處理端：「我的交辦」以交辦單為列；展開看成員（分頁）；**部分回覆＝勾選成員後回覆**（預設全選） | modal 內逐台例外：與勾選同義、驗證難寫 |
| 6 | 交辦單記錄**範圍**＋「續掛」旗標（預設開；範圍為指定主機時不提供）。夜間掛接時範圍內新主機自動加入並標「新增 N 台」 | — |
| 7 | 問題檔案負責人自動建案改走交辦單：每位負責人此問題一張**系統交辦單**、續掛；多位負責人依負載最輕者（同分依帳號序） | 每位負責人都建案違反「同主機同問題只由一人處理」 |
| 8 | **自動派工池**：使用者群組加「派工池」旗標；系統設定加「自動派工」開關（預設關）。通過閘門、未被涵蓋的新（主機,問題）→ 派給「啟用、未暫停接單、池群組成員、看得到此主機」中負載最輕者；同趟同問題只建一張單；無候選＝進待派 | 池＝全部有 Handle 者會派到看不到的主機。負載口徑固定為「進行中交辦單成員數」，同分比進行中單數、再比帳號序——不做可設定加權 |
| 9 | **交辦總覽頁**（`/work-orders`）四頁籤：進行中／負載看板／待派／靜音中；「立即派工」把策略套到目前待派 | — |
| 10 | **靜音**掛在問題檔案：靜音區間（起日、迄日、原因、操作者，定案 34）；1／7／30／90／180 天＋自訂 1～365（暫定）。分析側以紀錄日查區間標 `Suppressed`；讀取側全部聚合入口排除＋註腳；處理側讀取時推導、交辦單推導為暫停；到期自動恢復、可提前解除 | 落盤改寫會寫上萬列歷程、提前解除無法還原 |
| 11 | 派工通知走郵件：建單／改派各一封給新處理人、改派時舊處理人一封「已移交」；續掛與自動派工**每趟每人一封**；開關 `MailNotifyWorkOrders` | — |
| 12 | `HostVisibilityResolver` **下沉到 Core**，Web 端呼叫同一份 | 由 Web 注入策略要穿三層；規則只依賴 Core 資料 |
| 13 | 既有 open 案件升級時回填成交辦單（依處理人×問題、origin=backfill、範圍＝指定主機、不續掛），冪等 | — |
| 14 | 舊端點 `bulk-assign`／`bulk-status`／`handler-candidates` **退役**；`bulk-close`（統一標記）保留 | — |
| 15 | 規模修正：案件授與按需查、工作頁 SQL 聚合、側欄輕量計數、建案候選日一次查、preview／plan 一次查 | — |
| 16 | **兩條不變式**：(a) 進行中案件必屬一張交辦單——日層級指派也走交辦單（併入或建 `day_assign` 單）；(b) 同（處理人, 問題）同時**最多一張**進行中單（部分唯一索引）——交辦給已有單的人＝追加成員，範圍取聯集（`All` > `Groups` 聯集 > `Hosts`） | 允許多張：動線被自己切碎 |
| 17 | **派工閘門**（④⑤⑥ 共用，**使用者已同意**）：跳過 `Suppressed`、有 `NoiseMark` 的（主機,簽章）、嚴重度不在 `UnhandledSeverities`。⑤ 加閘門是既有行為變更：不列入未處理計算的問題本來就是「預設不處理」，派給人等於製造假工作 | — |
| 18 | **取消交辦**（`Assign`）：關單（`cancelled`）、成員案件關閉、案件同步寫入（`CaseId`＝該案件）且非結案類的日子改回明確 `open`（逐日 `issue_status_cleared` 歷程）、使用者自己標的日子不動；稽核 `work_order_cancel` | 只留改派不是可販售軟體的行為 |
| 19 | **代為結案**（`Assign`＋`Handle`）：整張單以 admin 名義結案（結案四態、原因必填），逐案走 `SyncStatus`；稽核 `work_order_admin_close` | — |
| 20 | **未回覆指標**：`LastReplyAt`；清單與看板有「未回覆」篩選與天數；案件狀態語意不改 | 改成指派後 open 直到認領牽動全站三態口徑（BACKLOG） |
| 21 | 使用者「**暫停接單**」旗標（使用者維護頁）：候選排除、既有單保留並在看板標示 | — |
| 22 | 靜音的「到期後」語意（實作以定案 34 的區間為準）：靜音期間的日子到期後仍已有結論（紀錄日在區間內）；靜音前的未處理紀錄到期後回來（暫停不是結案），modal 明講 | 只靠「目前是否靜音」推導會讓到期日整批回待辦 |
| 23 | 保留：已結案交辦單與事件隨已結案案件同保留期；進行中永不清理 | — |
| 24 | 授權：交辦單可看者＝處理人本人、`Assign`、`ViewAll`（成員依**檢視者**可見範圍過濾）；其他 403（逐單檢查）。總覽頁 `Assign` 或 `ViewAll` 可看、寫入 `Assign`；立即派工與開關 `Maintain`；回覆 `Handle` 且限本人 | — |
| 25 | **「讓告警安靜」決策表**進 RULES-SPEC 與 UI 說明：抑制／靜音／機房結論／已知雜訊／觀察 | 六種機制沒有一張表使用者會用錯 |
| 26 | **多張單一次回覆**（自己的單） | — |
| 27 | **交辦單詳情頁** `/work-orders/{id}`：標頭、成員表（分頁、狀態篩選、**前端複製 CSV 當前頁**，沿用 §8.6-7）、時間軸 | 後端 CSV 端點：不符全站慣例 |
| 28 | 可觀測：趟末執行紀錄一行摘要；週報靜音段；體檢到期提醒 | — |
| 29 | 併發：交辦單 `UpdatedAt` 併發權杖；衝突樂觀重試一次，仍衝突記警告下趟補掛 | — |
| 30 | 規模：建單同步；`LF_SCALE_BENCH` 壓測；超 30 秒改背景作業（BACKLOG 卡位） | — |
| 31 | **案件表反正規化** `lf_issue_cases.source_name`／`event_id`（自 `issue_key` 解析）＋索引 `(source_name, event_id, closed_at)`；啟動時分批回填（C# 解析，以 NULL 為未回填標記，冪等）。衝突查詢、涵蓋欄、缺口 NOT EXISTS、依問題查案件全靠它 | 用 `issue_key LIKE`：無索引，4000 台下每次交辦與每頁涵蓋欄都是全表掃 |
| 32 | **案件日同步作業**（第五版取代「期間上限」）：把「讓案件涵蓋的逐日 `IssueHandling` 列與案件狀態一致」抽成一個冪等函式 `ReconcileCaseDays(案件)`；建案／回覆／取消／代為結案／改派一律**只同步改案件本身**並標 `DaySyncPending`，逐日列的展開由背景作業分批完成（先例：`TopIssueBackfiller`）。預估逐日寫入 ≤ 門檻（暫定 5000 列）時就地呼叫同一函式做完（既有測試量級全在門檻內、行為逐位不變），超過才進背景，畫面標「逐日同步中 N 台」。回溯窗口只剩兩種：夜間 ④⑤⑥ `DaysOnly(當日)`（不回溯，避免普遍性問題每晚十幾萬列）；人工交辦與日層級指派 `AllHistory`（由作業補齊）。**不設成員數與主機日上限**，預覽只誠實顯示「N 台／預估 M 主機日」 | 期間上限：只是把寫入量推到回覆時（`SyncStatus` 本來就展開全部歷史合格日，750 台 × 100 天回覆必逾時），且期間外舊日子在儀表板掛「未處理」直到有人回覆 |
| 33 | **同趟派工規則**：同問題同趟「**同處理人**一張單」（可見範圍分割下同一問題自然分成每部門一張）；新主機優先掛給本趟已為此問題選中且看得到該主機的處理人（同時多位取負載最輕）；候選須具 `Handle`（主管群組不可能被選中）；**延續性偏好**：最近 30 天（暫定）內處理過此主機此問題且已結案（`resolved`）的處理人若仍是候選則優先 | 「同趟同問題一張單」：部門成員看不到別部門主機，規則本身走不通 |
| 34 | **靜音區間**（第五版取代「分析時快照」）：問題檔案存 `Mutes: [{From, To, Reason, ById, ByAccount, At}]` 為唯一事實來源；「某日某問題靜音」＝該**紀錄日**落在任一區間；目前靜音中＝今天落在區間。分析側以紀錄日查區間標 `Suppressed`（重新分析永遠同答案）；SQL 端推導與聚合只組「與查詢期間重疊的區間」為排除條件（通常 0～5 個）；記憶體端同一判定；提前解除＝迄日截到昨天（起日＝今天則刪除區間），歷史區間留存供詳情頁顯示「靜音 9/16～12/14（王小明：原因）」。**`lf_top_issues` 不加欄、紀錄 JSON 不加欄** | 快照三處各存一份（JSON、欄位、問題檔案）；依執行時間判定讓重新分析舊日子語意飄；A-4／B-1 各加一欄互相耦合 |
| 35 | `NoiseMark` 全集進每趟快照（blob 一趟讀一次），不逐主機日讀 | — |
| 36 | 同步作業的批次形狀：每批 N 個案件（暫定 200）**一次**查全部主機的合格日、`SaveMany` 一次寫；就地路徑與背景路徑共用同一批次函式 | 逐案 `SyncStatus` 各掃一次主機歷史：750 台＝750 次查詢 |
| 37 | 回填設 `LastReplyAt`＝該組案件最近一次由處理人本人寫入的歷程時間（無則 null 並顯示「整併前無回覆紀錄」） | 全部 null：整批舊工作顯示「未回覆」誤導 |
| 38 | 靜音 modal 顯示「既有 N 張進行中交辦單（M 台）」並二選一：**暫停、到期恢復**（預設）或**代為結案為「不處理」**（原因同靜音原因，走定案 19 批次）；解除靜音時暫停的單恢復並在總覽以「自靜音恢復」篩選出現 | 只有暫停：90 天後三個月前的舊單回到隊列 |
| 39 | 待派清單每列附「無候選原因」：無池群組／無池成員看得到這些主機（列群組）／候選全部暫停接單；admin 據此修授權矩陣或加人 | — |
| 40 | 清單頁的成員計數、狀態彙總、依問題視角涵蓋欄：**頁內一次 GROUP BY**（以頁上的單號或問題集合為 IN 條件），不逐列 | — |
| 41 | 負載口徑「進行中成員數」標**暫定**；看板同時顯示成員數與單數；口徑改動只在 `WorkOrderDispatcher` 一處 | — |
| 42 | 回填改集合式：案件 `work_order_id` 以每組一句 UPDATE（IN 案件 id 清單，分批 1000）寫入，不逐筆追蹤；既有案件 `DaySyncPending=false`（歷史已由當時的 `BuildCase` 掛好） | — |
| 43 | **缺口＝派工試跑**（第五版取代 SQL 缺口查詢）：SQL 只取主機層級候選列（期間內出現、嚴重度在集合、不在目前靜音區間、NOT EXISTS 進行中案件；暫定上限 20000 列，超過要求縮小期間），記憶體用 `WorkOrderDispatcher` 試跑模式（不落盤）套閘門（含現行抑制與 `NoiseMark`）、可見範圍、選人→依問題分組回：主機數、建議處理人、無候選原因。畫面數字＝按「立即派工」會發生的事，**不需要「建單時再套閘門」註腳**；`lf_top_issues` 不加 `suppressed` 欄 | SQL 做缺口：`NoiseMark` 是 blob 套不進 SQL、現行抑制要看主機群組也套不進，只能加註腳；且要在 `lf_top_issues` 加欄 |
| 45 | **`Suppressed` 在 AI prompt 與郵件路由的一致化**（既有行為變更，理由同定案 17）：`AnalysisPromptBuilder` 目前只對 PRTG finding 過濾 `Suppressed`，Windows／Linux 被抑制或靜音的問題仍進 AI 敘事；`MailNotificationService` 的問題負責人路由也不看 `Suppressed`。改為兩處一律排除 `Suppressed` 問題——「不吵」包含 AI 白話總覽與負責人通知。報告 txt 的「已抑制」段維持列出（證據層），反查原因加靜音區間第三條路 | 不改：靜音了的問題仍會出現在 AI 總覽標題與負責人信件裡，使用者會認為靜音沒生效 |
| 46 | **快照的作用域**：每個會呼叫 `AttachNewDay` 的執行單位各建一份 `DispatchContext`（每日分析、PRTG 每日路徑、觸發式取數）——PRTG 路徑本來就走 `HostDayPostProcessor.AttachCase`，PRTG finding 因此同樣進閘門、續掛與自動派工，靜音以 (Source, 0) 鍵命中 | — |
| 47 | **`IssueExclusion` 沒有預設值**：`IIssueAggregateQuery` 每個呼叫端必須明寫 `IssueExclusion.None` 或帶靜音；套用清單見第十四節（授權／校準／規則命中統計／問題檔案選擇器／詳情頁基準一律 `None`） | 有預設值：新呼叫端會默默不排除或默默排除，兩種都查不出來 |
| 44 | 同步作業的可觀測與復原：`DaySyncPending` 是唯一狀態（重啟後續跑、任意順序皆可，函式冪等）；作業有工作時每 5 秒（暫定）跑一批；交辦單詳情顯示「逐日同步中 N/M 台」；執行紀錄頁（§9.10）不新增頁籤，只在活動告示顯示「案件逐日同步：待處理 N 件」 | — |

## 三、端到端操作劇本（驗收用；收尾時逐條走一遍）

每條劇本寫「角色／起點／步驟／預期／點擊數」。點擊數是設計目標，執行端不必精算，但劇本走不通就是缺口。

**S1 首次啟用（admin，一次性）**
1. 升級後啟動：schema 補齊、既有 open 案件回填成交辦單，啟動 log 記「回填 N 單／M 成員／未連結 K」。
2. 群組與授權頁：把「網管一組」「網管二組」勾為派工池（2 次點擊）。
3. 設定頁：開「自動派工」、確認「交辦單郵件」開（2 次）。
4. 問題檔案頁：既有負責人規則不動；預期今晚起負責人問題走系統交辦單。
預期：沒有任何舊資料消失；我的交辦頁對每個處理人顯示回填後的單（origin 標「系統整併」）。

**S2 admin 手動交辦一個問題到 300 台（依問題視角）**
1. 問題查詢（預設依問題視角）→ 找到「DCOM 10016」列，涵蓋欄顯示「0/300 台有交辦單」。
2. 點「交辦」→ modal 顯示「影響 300 台（依目前篩選 9/09～9/16）；閘門排除 12 台（已抑制 10、已知雜訊 2）；他人進行中 0」。
3. 選「使用者群組：網管一組（依現有負載）」→ 分攤預覽「甲 96／乙 96／丙 96」（288 台）；可展開清單分頁看主機；勾「續掛新主機」（預設勾）。
4. 送出 → toast「已建立 3 張交辦單（288 台）；閘門排除 12 台」；三位處理人各收一封信；涵蓋欄變「288/300」。
點擊數：4～5。第 301 台明天出現→夜間自動掛進負載最輕者的那張單，趟末彙總信。

**S3 處理人一次回覆＋部分例外（甲）**
1. 登入→側欄「我的交辦」徽章「1 單／96 台」；儀表板「我的交辦」卡同數字。
2. 我的交辦→一列「DCOM 10016｜96 台｜未回覆 1 天｜期限 9/23」。
3. 展開→成員表 96 列（分頁 50）；表頭全選→取消勾 5 台→「回覆」→已處理＋說明→送出 → toast「已更新 91 台、共 273 天」。
4. 勾那 5 台→「回覆」→處理中＋說明「待換硬碟」＋期限 → 送出。
5. 詳情頁時間軸出現兩筆回覆彙總；未回覆指標消失。
點擊數：約 9。

**S4 自動派工日常（無人操作）**
1. 夜間分析發現「Ntfs 55」在 40 台新出現；無交辦單、無負責人；閘門通過；自動派工開。
2. 策略選負載最輕的丁（池成員、看得到那 40 台中的 38 台；2 台落在丁看不到的群組→候選改為看得到的人，若無→待派）。
3. 建一張 `auto_dispatch` 單給丁（38 台）；2 台進待派。
4. 執行紀錄一行「自動派工：建 1 單／續掛 0 台／待派 2 組／閘門略過 7」；丁收一封彙總信。
5. 隔天 admin 開交辦總覽→「待派」頁籤看到那 2 台與建議處理人→一鍵建單。

**S5 靜音一個問題三個月（admin）**
1. 依問題視角「Schannel 36887」列→「靜音」→選「3 個月」＋原因「等憑證汰換專案」→ modal 三句提示 → 送出。
2. 立即：依問題視角不再列它、註腳「另有 1 個問題靜音中」；儀表板卡數字同步減少；有此問題的 2 張交辦單在處理人頁不列、註腳「另有 1 張暫停」；總覽「靜音中」頁籤列出（到期 12/15、原因、設定者）。
3. 今晚起分析：該問題 `Suppressed`＋快照，不拉日風險、不進即時告警；不建單不續掛。
4. 週報「靜音中問題」段列出它。
5. 12/16 起：問題再出現走正常派工；靜音期間的日子不回待辦；9/16 以前的未處理日子回到待辦（modal 當時已講）。
6. 提前解除：總覽靜音中頁籤「解除」→ 一切回到解除前狀態，寫一列稽核。

**S6 派錯了（admin）**
1. 交辦總覽→那張單→「取消交辦」→原因→確認。
2. 成員案件關閉、由案件同步寫入的處理中日子回到明確未處理、處理人自己回覆過已處理的日子不動；單標「已取消」；稽核一列；處理人收「已取消」信（併入改派信件型別）。

**S7 處理人離職（admin）**
1. 使用者頁停用帳號。
2. 交辦總覽→篩「處理人已停用」→逐單「改派」或「代為結案」；自動派工永不再選他。

**S8 admin 每日巡檢（目標 10 分鐘內）**
1. 交辦總覽→「待派」頁籤：昨晚 3 組待派，各附原因（2 組「無池成員看得到：待歸屬群組」、1 組「候選全部暫停接單」）→修矩陣或直接一鍵建單。
2. 「負載看板」：15 人各進行中單數／成員數／未回覆／逾期；甲 3 單 2100 台未回覆 4 天→點進去看。
3. 「進行中」篩「上報中」：2 張→改派或代為結案。
4. 儀表板重點問題卡：涵蓋率低的問題→依問題視角「交辦」。

**S9 修好又復發**
1. 乙把「Ntfs 55」40 台全回覆已處理→單結案。
2. 10 天後同問題在其中 30 台復發：② 無進行中案件、④ 無進行中單、⑤ 無負責人、⑥ 延續性偏好→乙仍是候選→建新單給乙（不是負載最輕的別人）；乙收信「復發：Ntfs 55（30 台）」，詳情頁「先前處理」可見上次怎麼解的。

### 4000 台情境放大檢視（規模假設與每步的量）

假設：4000 台（4 個部門群組各約 1000 台）、每晚 4000 主機日、每主機日 5 個重點問題（2 萬個問題評估）、全站約 500 種問題、派工池 15 人分屬 4 個部門群組、`UnhandledSeverities`＝高＋中、普遍性問題 2500 台級。

| 步驟 | 量 | 形狀要求（契約） |
|---|---|---|
| 升級回填 | 進行中案件 5k～10k 件→20～50 張單 | 集合式 UPDATE 分批（定案 42）；`source_name`／`event_id` 回填全部案件（含已結案，10 萬列級）分批 1000、以 NULL 為未回填標記（定案 31） |
| 夜間快照 | 15 人可見集合（各 ≤4000 id）、進行中單 50～500、負載 15 列、問題檔案、NoiseMark 全集 | 一趟各算一次（定案 35）；2 萬個問題評估全是字典查找 |
| 夜間建案（普遍性問題 3000 台） | 每部門一張單、各約 750 成員；當日 3000 列 `IssueHandling`＋3000 列歷程 | 只掛當日（定案 32）；同處理人一張單（定案 33）；與現行負責人建案量級相同 |
| 人工交辦 3000 台（歷史 60 天） | 同步段：3000 案件＋3000 列觸發日；背景：18 萬主機日＋18 萬歷程分 15 批 | 同步 < 5 秒；背景每批一次合格日查詢（定案 32、36）；預覽顯示「3000 台／預估 180000 主機日、背景同步」 |
| 衝突／涵蓋／缺口查詢 | 案件表 10 萬列級 | 走 `(source_name, event_id, closed_at)` 索引（定案 31）；缺口 NOT EXISTS 需 host_id↔host_name 對應（比照 `DeriveDayHandling` 既有 join 寫法） |
| 處理人回覆 750 台 | 5k 列＋5k 歷程 | `SyncStatusMany` 一次查候選日（定案 36） |
| 多單回覆 4 單 3000 台 | 2 萬列級 | 同上 |
| 靜音一個 2500 台問題 | 寫問題檔案 1 次；每次聚合多 0～5 個區間條件 | 區間單一來源（定案 34）；既有單 N 張二選一（定案 38） |
| 處理人回覆 750 台（各 60 天歷史） | 同步段 750 案件；4.5 萬列逐日展開進背景 | 分流（定案 32）；畫面「逐日同步中」 |
| 取消 750 台單 | 750 案件標 `Cancelled`＋關閉；日子回 open 進背景 | 同上 |
| 缺口試跑（7 天） | 候選列數千、記憶體決策 | 上限 20000 列（定案 43） |
| 清單頁 | 50 張單／頁、成員 50／頁 | 頁內一次 GROUP BY（定案 40） |
| 側欄徽章 | 每次換頁 | 單一聚合（D-1） |
| 郵件 | 每趟每人 ≤1 封；建單 1 封列前 20 台 | — |

## 四、批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| A | 交辦單核心（Core）：模型／store／schema／回填、協調、可見範圍下沉、派工策略、掛接新優先序 | 大 | 無 | 1 |
| C | 派工端（admin）：建單與五個單操作、總覽 API 與頁、詳情頁、設定與旗標 | 大 | A | 2 |
| D | 處理端（handler）：我的交辦改版、部分回覆、多單回覆、儀表板卡、徽章、輕量計數 | 中 | A、C-1 | 3 |
| B | 靜音：欄位、分析側抑制與快照、讀取側排除、推導、UI、決策表 | 中 | A、D-1 | 4 |
| E | 通知：郵件三型、週報靜音段 | 小 | A、C、B-1 | 5 |
| F | 規模修正與壓測 | 小 | A | 6 |
| 文件 | 見第十節 | 文件 | 全部 | 7 |

### 作業總覽（委派輪）

- **執行端**：`impl-low`，整輪不換。階段規格抄成 `.gemini-tasks/task-47-<階段>.md`（已在 `.git/info/exclude`），執行端不看本文件；規格送出前把該段「契約要點」與「驗收」逐條對照打勾；Claude 每段獨立驗收，不採信摘要。
- **UI 三段**排各作業最後；開工前確認 `ui-ux-pro-max`。
- **粒度**：每階段 1～3 個機制、實作與測試同段；「暫定」數值執行端可依事實推翻並在執行紀錄寫理由。
- **委派期間 Claude 不動 repo**；突變還原只用檔案備份。

| 階段 | 內容 | 主要檔案（白名單在規格檔） | 前置 |
|---|---|---|---|
| A-1 | 交辦單模型＋store＋schema＋回填＋保留 | `WorkOrder`／`WorkOrderEvent`（新）／`IWorkOrderStore`＋EF（新）／`IssueCase`／`LfDbContext`／`SchemaUpgrader`／回填器（新）／保留清理 | — |
| A-2a | 批次合格日查詢（事實表）、逐日寫入函式（指派／同步／取消三模式、冪等）、就地／背景分流與待同步意圖、背景同步服務 | `IAnalysisRecordQuery`＋EF＋兩個替身／`IssueCaseCoordinator`（只換合格日來源、新增批次寫入）／`IssueCase`＋案件 store／`SchemaUpgrader`／背景服務（新） | A-1 |
| A-2b | 交辦單協調：建、併、追加、改派、拆、取消、代為結案、結案推導、回覆時間、每趟快照 | `WorkOrderCoordinator`（新）／`IssueCaseCoordinator`（結案推導掛鉤） | A-2a |
| A-3 | 可見範圍下沉＋派工策略＋旗標欄位 | `HostVisibilityResolver`（搬）／`WorkOrderDispatcher`（新）／`UserGroup`／`WebUser`／`SystemSettings` | A-1 |
| A-4 | 掛接新優先序 ⓪～⑥＋閘門＋趟末摘要 | `IssueCaseCoordinator.AttachNewDay`／`HostDayPostProcessor`／`AnalysisOrchestrator` | A-2、A-3 |
| C-1 | 建單／追加／改派／拆單／取消／代為結案 API；日層級指派改走交辦單；舊端點退役 | `WorkOrdersController`（新）／`WorkOrderCommandService`（新）／`IssueHandlingCommandService`／`DayHandlingCommandService`／`AuditEntry`／`AuditQueryService` | A-4 |
| C-2 | 總覽 API：清單、詳情、成員、時間軸、看板、待派、立即派工、逐單授權 | `WorkOrderQueryService`（新）／`EfWorkOrderStore`／`WorkOrdersController` | C-1 |
| C-3 | 前端：交辦 modal 與涵蓋欄、總覽頁、詳情頁、nav | `records.js`／`work-orders.js`（新）／`work-order-detail.js`（新）／`WorkOrders.cshtml`／`WorkOrderDetail.cshtml`（新）／`PagesController`／`layout.js` | C-2（UI） |
| C-4 | 設定頁「自動派工」段、群組頁「派工池」、使用者頁「暫停接單」、郵件開關 | `settings.js`／`groups.js`／`users.js`／`AdminDtos`／`GroupAdminService`／`UserAdminService`／`SettingsDto` | A-3 |
| D-1 | 我的交辦 API（SQL 聚合）、單回覆、多單回覆、輕量計數；舊 bulk-status 退役 | `HandlingHistoryQueryService`／`WorkOrderQueryService`／`WorkOrderReplyController`（新）／`HandlersController` | C-1 |
| D-2 | 前端：我的交辦頁、回覆 modal、儀表板卡、詳情頁徽章、側欄徽章 | `handler-detail.js`／`issue-status-reply.js`／`dashboard.js`／`record-detail.js`／`layout.js` | D-1（UI） |
| B-1 | 靜音區間＋分析側抑制（依紀錄日）＋設定／解除 API＋稽核＋`Maintain` 修正 | `IssueProfile`／`SuppressionFilter`／`LogAnalysisService`／`IssueOwnerAdminService`／`IssueOwnersController` | A-1 |
| B-2 | 讀取側排除、推導、rollup、暫停推導、郵件 digest | `IIssueAggregateQuery`＋EF／`DayHandlingDerivation`／`IssueHandlingRollupQuery`／`RecordDetailQueryService`／`MailIssueDigest`／`WorkOrderQueryService` | B-1、D-1 |
| B-3 | 前端：靜音入口三處、總覽靜音頁籤、註腳、徽章、決策表說明 | `records.js`／`issue-owners.js`／`work-orders.js`／`dashboard.js`／`record-detail.js`／`reports.js`／`rules.js`（抑制段一行連結） | B-2（UI） |
| E-1 | 郵件三型＋趟末彙總＋週報靜音段＋開關 | `MailNotificationService`／`WorkOrderCommandService`／`AnalysisOrchestrator` | C-1、A-4、B-1 |
| F-1 | 案件授與按需查、preview／plan 一次查、壓測 | `VisibilityService`／`EfIssueCaseStore`／`IssueHandlingCommandService`／`Scale/` | A-1 |
| 文件 | 第十節 | Claude 親寫 | 全部 |

## 五、作業 A：交辦單核心（Core）

### 現況與核對結果

- 案件鍵＝（主機, 問題簽章），store 五個查詢全無分頁、無依問題跨主機查（`IHandlingStores.cs:68-100`）。
- `BuildCase` 逐主機呼叫、每次 `FindCandidateDays` 掃該主機全歷史（`IssueCaseCoordinator.cs:62-99,400-408`）。
- `AttachNewDay` 四層優先序，每主機日 `GetAll()` 讀整份問題檔案 blob（:240-243）；負責人建案無閘門。
- `HostVisibilityResolver` 四個靜態方法參數全是 Core store（`HostVisibilityResolver.cs:17-90`）。
- schema 升級走 `SchemaUpgrader` 冪等 DDL，新 DB 由 `EnsureCreated` 建（`SchemaUpgrader.cs:17-`）。
- 「調回未處理」有 clearing 路徑（`issue_status_cleared`，WEB-SPEC §9.3-9）。

### 定案

定案 1、2、6、7、8、12、13、16、17、18、19、20、21、23、29、30。補充：

- **推導狀態**：進行中＝有任一成員 `ClosedAt == null`；已結案＝成員全部結案（`ClosedAt` 落盤為快取、`ClosedReason`：`all_closed`／`moved`／`cancelled`／`admin_closed`）；上報中＝任一成員 `escalated`；暫停＝問題靜音中；未回覆＝`LastReplyAt == null`。零成員的單不可存在。
- **一單一人**：改派＝整張單換人（成員逐一 `ReassignCase`）；目標已有同問題進行中單→併入、原單 `moved`。拆單＝勾選成員移到另一人（同併入規則）。
- **範圍**：`ScopeKind` `All`／`Groups`／`Hosts`；`AutoAttach`（`Hosts` 時恆 false）。續掛判定：問題相同、單進行中、`AutoAttach`、主機在範圍內、無進行中案件、通過閘門。多張命中取最近建立者。
- **每趟快照**（`DispatchContext`）：問題檔案索引（含靜音集合）、進行中交辦單索引（依問題）、負載快照（含本趟已分派）、候選人池（啟用∧未暫停∧池群組∧各自可見主機集合，**一趟算一次**）、本趟建立的單、閘門略過計數。
- 舊資料：回填只處理進行中案件，依（處理人, Source, EventId）分組；`HandlerId` 空者不回填、啟動 log 記數。

### 契約要點（供階段規格抄錄）

**A-1**
- `WorkOrder` 欄位：`WorkOrderId`（bigint 自增）、`SourceName`／`EventId`（可空）、`IssueLabel`（≤200）、`HandlerId`（非空）、`Origin`（`manual`／`owner_rule`／`auto_dispatch`／`backfill`／`day_assign`）、`ScopeKind`、`ScopeGroupIds`（JSON 或逗號清單，執行端定）、`AutoAttach`、`Note`（≤1000）、`DueDate`、`CreatedById`／`CreatedByAccount`／`CreatedAt`、`LastAppendedAt`、`LastReplyAt`、`ClosedAt`／`ClosedReason`、`UpdatedAt`（併發權杖）。
- `WorkOrderEvent`：`EventId`、`WorkOrderId`、`Action`（`created`／`appended`／`merged_in`／`reassigned`／`split_out`／`split_in`／`cancelled`／`admin_closed`／`closed`／`reopened_by_member`）、`ActorId`／`ActorAccount`（系統 null）、`MemberDelta`、`Note`（≤1000）、`CreatedAt`。
- `IssueCase.WorkOrderId`（可空）；`IssueCase.SourceName`／`EventId`（反正規化，定案 31；`Save` 時由 `IssueKey` 解析寫入，解析失敗者兩欄留 null 且不參與依問題查）；`IssueCase.DaySyncPending`（bit，定案 32／44）；`IssueCase.Cancelled`（bit，取消交辦的案件標記，供同步函式決定「日子回到明確 open」）。
- 表：`lf_work_orders`（含 `source_key` 大寫正規化欄；索引 `(handler_id, closed_at)`、`(source_key, event_id, closed_at)`、**部分唯一** `(handler_id, source_key, event_id) WHERE closed_at IS NULL AND source_key IS NOT NULL`，兩後端同一份 DDL 字串）、`lf_work_order_events`（`(work_order_id, created_at)`）、`lf_issue_cases.work_order_id`＋`(work_order_id, closed_at)`、`lf_issue_cases.source_name`／`source_key`（nvarchar，可空；解析失敗寫 `source_key=''`）／`event_id`（int，可空）＋`(source_key, event_id, closed_at)`、`lf_issue_cases.day_sync_pending`＋索引 `(day_sync_pending)`（作業撿件）、`lf_issue_cases.cancelled`（bit 預設 0）。派工池與暫停接單是 blob 模型屬性、無 DDL（A-3）。所有字串寫入前依 `HasMaxLength` 截斷。
- `IWorkOrderStore`／`IIssueCaseStore` 新增：`GetOpenByIssue(source, eventId)`（走新索引）、`GetOpenMany(hostNames, source, eventId)`。
- `IWorkOrderStore`：`Get(id)`、`GetActiveByHandler(userId, page, filter)`、`GetActiveByIssue(source, eventId)`、`GetActiveFor(handlerId, source, eventId)`、`GetMembers(id, page, statusFilter)`、`CountMembers(id)`→（進行中／已結案／逾期／上報）、`LoadBoard(userIds?)`→每人（進行中單數、成員數、未回覆單數、逾期成員數、近 7 日結案數、最舊未結日）**一句 SQL**、`Save`／`SaveMany`、`AppendEvent`／`ListEvents(id)`、`PruneClosedBefore(date)`。
- 回填（兩段，皆冪等、皆分批）：(1) `source_name IS NULL` 的案件分批 1000 讀 `issue_key` 解析後集合式 UPDATE（全部案件含已結案）；(2) `work_order_id IS NULL AND closed_at IS NULL` 的案件依（處理人, source, eventId）分組，每組建一單（`Origin=backfill`、`ScopeKind=Hosts`、`AutoAttach=false`、`CreatedAt`＝該組最早 `CreatedAt`、`LastReplyAt`＝該組案件最近一次由處理人本人寫入的歷程時間或 null、事件 `created` 一筆 `MemberDelta=N`），案件 `work_order_id` 以每組一句 UPDATE（IN id 清單分批 1000）寫入（定案 42）。啟動 log 記「解析 N／建單 M／成員 K／未連結 J」。
- 保留：`PruneClosedBefore(今天−RetentionDays)` 併入既有保留路徑，事件隨單刪；進行中永不刪。

**A-2**
- **同步函式**（定案 32）`ReconcileCaseDays(案件批次, 時間戳)`：對每個案件取合格日（規則逐字沿用現行 `ResolveEligibleDays`：該日此問題無標記、或標記非結案且無 `CaseId`、或屬同案件；一批案件的合格日**一次**查），讓每個合格日的 `IssueHandling` 列等於案件現狀（`Status`／`Note`／`DueDate`／`CaseId`），有變動才寫一列歷程（動作沿用 `case_assign`／`case_sync`／`case_attach`；`Cancelled` 案件→非結案類且 `CaseId`＝此案件的日子寫明確 `open`＋`issue_status_cleared`）；完成後清 `DaySyncPending`。冪等：連跑兩次第二次零寫入。
- `WorkOrderCoordinator`（Core）：`Create(問題, 主機清單, 處理人, 範圍, 續掛, 說明, 期限, Origin, 操作者, 時間戳, 窗口)`→`WorkOrder`＋建案結果；**窗口**：`DaysOnly(當日)`（夜間，就地寫當日一列、不標 pending）／`AllHistory`（人工交辦與日層級指派：先寫觸發日一列讓畫面立即有反應，標 pending 交作業）；`SaveMany` 分批（暫定每批 5000 列）；`Append(單, 主機清單, …)`；`MergeInto(來源單, 目標單)`；`Reassign(單, 新處理人, …)`；`Split(單, 成員案件清單, 新處理人, …)`；`Cancel(單, 原因, …)`；`AdminClose(單, 結案狀態, 原因, …)`；`RecomputeClosure(單)`（冪等）；`TouchReply(單, 時間戳)`。
- `IssueCaseCoordinator.SyncStatus`／`ReassignCase` 結束時呼叫 `RecomputeClosure`（與 `TouchReply` 由呼叫端決定是否為回覆）。
- **就地／背景分流**（定案 32、36、44）：任何改案件狀態的操作（建案、回覆、多單回覆、取消、代為結案、改派）先改案件列並估算逐日寫入量（案件數 × 各主機合格日數，由同一次合格日查詢得知）；≤ 門檻（暫定 5000 列）就地呼叫 `ReconcileCaseDays` 做完，回應含 `{ synced: true }`；否則標 pending、回應 `{ synced: false, pendingCases: N }`。背景 `CaseDaySyncWorker`（掛在既有 `SchedulerHostedService` 迴圈，先例 `TopIssueBackfiller`）有 pending 時每 5 秒（暫定）撿一批 200 件呼叫同一函式。
- 既有 `IssueCaseCoordinator.BuildCase`／`SyncStatus`／`ReassignCase` 改為「改案件＋呼叫同步函式」的薄包裝，公開簽章與回傳不變，既有 119 個案件與處理測試**不改斷言**必須綠（這是同步函式語意逐位沿用的守門）。
- `/api/run-activity` 回應加 `caseDaySyncPending`（int）；`SchedulerRunState` 只加欄位，`isRunning`／`isFetchRun` 語意不動。
- `AttachNewDay` ② 掛入既有案件：仍就地寫當日一列（不經作業）。
- 取消語意：成員案件 `ClosedAt`＝現在；`IssueHandling` 列 `CaseId`＝該案件且狀態非結案類→寫明確 `open`＋`issue_status_cleared` 歷程（同一時間戳）；其餘不動。
- 併發：`Save` 遇併發衝突→重讀重算一次；再衝突→擲 `DomainException`（Web 端回 409 訊息「資料剛被更新，請重試」；夜間端記警告不中斷）。

**A-3**
- `HostVisibilityResolver` 搬到 Core（純函式簽章不變），Web 端以 using 轉向或薄轉呼叫；**不得留兩份**。
- `WorkOrderDispatcher.Decide(主機, 問題, 嚴重度, suppressed, hasNoiseMark, ctx)`→`Skip(gate_suppressed|gate_noise|gate_severity|muted|no_candidate|disabled)`／`AttachTo(單)`／`CreateFor(處理人, origin)`。候選＝啟用∧未暫停∧池群組成員∧具 `Handle`∧看得到此主機。選人順序（定案 33）：(1) 本趟已為此問題選中且看得到此主機的處理人（多位取負載最輕）；(2) 延續性偏好——最近 30 天（暫定）內此主機此問題最後一張已結案（`resolved`）單的處理人若仍是候選；(3) 負載最輕。負載（暫定）＝進行中單的進行中成員數（快照＋本趟）；決勝：成員數→單數→帳號序（Ordinal 不分大小寫）。`ctx` 內含 `NoiseMark` 全集（定案 35）。
- `UserGroup.DispatchPool`（builtin admin 群組不可為 true，儲存層驗證；群組 `Active=false` 時其成員不是候選）、`WebUser.DispatchPaused`、`SystemSettings.AutoDispatchEnabled`（預設 false；開啟但無池群組時儲存允許、設定頁顯示警告、夜間記一則 warn）。
- `INoiseMarkStore.GetAll()`（blob 一次讀）供快照；`DispatchContext` 由每個執行單位自建（定案 46）。

**A-4**
- 優先序：⓪ 紀錄日落在靜音區間→略過；① 既有標記／既有處理→略過；② 進行中案件→掛入；③ `AutoApply` 結論→`FleetApply`；閘門不過→略過並計數；④ 續掛；⑤ 負責人；⑥ 自動派工；無候選→待派計數。④⑤⑥ 建案一律 `DaysOnly(當日)`（定案 32，不回溯、不標 pending）。
- 靜音區間集合由每趟快照供給（B-1 前為空集合）。
- 趟末：`Log.Info` 一行「自動派工：建 N 單／續掛 M 台／待派 K 組／閘門略過 J（抑制 a／雜訊 b／嚴重度 c）」；`DispatchContext` 摘要交給 E-1 寄信。
- 稽核：`work_order_auto_attach`／`work_order_auto_dispatch` 每趟每單一列。

### 測試／驗收

- store 合約：分頁、依問題、依（處理人,問題）、負載聚合單一查詢（查詢計數替身）；部分唯一索引兩後端皆驗（SQLite 測試＋SQL Server DDL 字串靜態斷言含 `WHERE`）。
- 升級冪等：重跑無 DDL；欄位清單斷言（兩張新表＋四個新欄）。
- 回填：兩人 × 兩問題 × 各三台→四單、12 成員；重跑零新增；已結案不動；`HandlerId` 空者不連結且 log 有計數。
- 批次建案：300 台候選日查詢次數恆 1；**突變**：改回逐台後必須紅。
- 同步函式：冪等（連跑兩次第二次零寫入）；`Cancelled` 案件→日子回明確 `open` 有歷程、自標日子不動；就地／背景分流門檻（門檻＋1 列→回 `synced=false` 且 pending 標記；背景撿件後結果與就地逐位相同）；重啟續跑（pending 未清者下次撿到）；**突變**：把「自標日子不動」的條件拿掉必須紅。
- 既有 `IssueCaseCoordinatorTests`／`HandlingServiceTests` 不改斷言全綠（薄包裝守門）。
- 結案推導：全結→結案；成員重開→重開＋事件；移空→`moved`；取消→案件關、同步日子變明確 `open` 有歷程、自標日子不動（反例）；代為結案→逐案 `SyncStatus`、原因進歷程。
- 併入：三組範圍聯集；事件 `merged_in`。
- 策略：候選四條件各一反例；決勝**突變**（對調單數與帳號序必須紅）；無候選→`Skip`；`disabled`→`Skip`。
- 閘門：三組各證明 ④⑤⑥ 都不建案；⑤ 案例名稱標明「既有行為變更」。
- 優先序：每層一組「只命中此層」；④ 先於 ⑤；同趟同問題兩台→同一張新單；負載快照含本趟。
- 併發：兩個協調器對同單同時 `Append`→其中一方重試成功、成員數正確。
- 可見範圍下沉：Web 端 grep 零第二份；`CaseGrantVisibilityTests` 綠。
- 全量綠。

## 六、作業 C：派工端（admin）

### 現況與核對結果

- 指派 modal 三支並行載入、預覽 200 台硬切、前端組 `hostIds`（`records.js:1357-1422,1708-1720`）。
- `handler-candidates` 逐成員撈案件數（`IssueHandlingCommandService.cs:558-575`）。
- 日層級指派直接 `BuildCase`（`DayHandlingCommandService`）。
- 統一標記保留其 `Assign`＋`Handle` 疊加授權（WEB-SPEC §9.2）。
- 稽核動作常數在 `AuditEntry.cs`；對照表 `AuditQueryService.ActionNames`。
- 頁面：`PagesController` action＋`Views/Pages/*.cshtml`＋`~/js/pages/*.js`；nav `requires` 支援陣列。

### 定案

定案 3、4、9、14、16、18、19、24、27。補充見契約要點。

### 契約要點

**C-1（命令）**
- `POST api/work-orders/preview` 與 `POST api/work-orders`（同一 request 形狀）：`{ source, eventId, from, to, groupIds?, hostIds?, excludeHostIds?, assignment: { mode: single|group, handlerId?, groupId?, split: byLoad|roundRobin }, reassignConflicts, note, dueDate, autoAttach, scopeKind }`。解析：可見範圍內、期間內出現該問題、`Aggregate` 母體同依問題視角（`riskLevels` 同值）；扣 `excludeHostIds`；過閘門（排除者計數＋原因）。
- 預覽回：`{ affected, gateExcluded: {suppressed, noise, severity}, conflicts: [{handlerName, count}], mergeInto: [{handlerName, workOrderId}], allocation: [{handlerId, count}], hosts: 分頁 100, overLimit: bool }`。預覽與落盤共用計畫函式。
- 落盤回：`{ created: [{workOrderId, handlerId, memberCount}], merged: [...], skippedConflicts, reassigned, gateExcluded, warnings: [無檢視權／無 Handle 沿用] }`。
- 窗口＝`AllHistory`（定案 32）：觸發日就地寫一列，其餘合格日由同步作業補齊；預覽回 `{ hosts, estimatedHostDays, inline: bool }`，畫面顯示「N 台／預估 M 主機日」，`inline=false` 時提示「送出後逐日同步在背景完成，儀表板數字會在數分鐘內更新」。**不設成員數與主機日上限**；唯一防呆：解析結果為 0 台→拒絕建單。
- 單操作：`POST /{id}/append { hostIds }`、`/reassign { handlerId }`、`/split { caseIds, handlerId }`、`/cancel { reason }`（必填）、`/admin-close { status（結案四態）, reason }`（必填）。授權：前四者 `Assign`；`admin-close` 為 `Assign`＋`Handle` 疊加（獨立 controller，理由同統一標記）。
- 日層級指派（`PUT …/handling/assign`）改呼叫協調器：該處理人此問題有進行中單→`Append`（origin 不變），否則 `Create(Origin=day_assign, ScopeKind=Hosts)`；回應多帶 `workOrderId`；既有「不搶走／`reassign=true`」語意不變。
- 退役：`PreviewIssueCaseAssign`／`BulkAssignIssueCase`／`GetHandlerCandidates` 與對應端點；`records.js` 最小改接（送同樣的篩選條件到新端點，畫面改版留 C-3）。
- 稽核：`work_order_create`／`_append`／`_reassign`／`_split`／`_cancel`／`_admin_close`／`_auto_dispatch_run`，`targetKind="work_order"`、`targetId=單號`；`ActionNames` 同 commit 補中文。

**C-2（查詢）**
- `GET api/work-orders?handlerId&source&eventId&groupId&status=active|escalated|overdue|unreplied|paused|closed&handlerInactive&sort&page&pageSize`（預設 `active`，pageSize ≤100）。列：單號、問題、白話說明、處理人（顯示名稱(帳號)、停用／暫停徽章）、成員數與狀態彙總、逾期台數、未回覆天數、期限、來源、建立時間、最近新增、暫停旗標。
- `GET api/work-orders/{id}`（標頭）、`/members?page&status`（每頁 ≤200；成員：主機、群組、最近出現、天數、狀態、期限、逾期、連結）、`/timeline`（事件＋回覆彙總：回覆自 `RecordHandlingLog` 依「同操作者＋同時間戳」分組，含台數／天數／狀態／說明）。
- `GET api/work-orders/load-board?groupId`：每處理人一列（含停用、暫停標示）；`groupId` 篩池群組或指定群組。
- `GET api/work-orders/gaps?from&to&page`（預設昨天往前 7 天；定案 43 試跑）：SQL 取主機層級候選列（期間內出現、嚴重度在集合、不在目前靜音區間、NOT EXISTS 進行中案件——走定案 31 索引；host_id↔host_name 對應比照 `DeriveDayHandling` 既有寫法；上限暫定 20000 列，超過回 `tooLarge` 要求縮小期間），記憶體以 `WorkOrderDispatcher` 試跑模式逐列決策（閘門含現行抑制與 `NoiseMark`、可見範圍、選人），依問題分組回：主機數、閘門排除數、建議處理人（可能多位，依部門分割）、**無候選原因**（定案 39：`no_pool`／`no_visibility`（列出無人看得到的主機群組）／`all_paused`）。`POST api/work-orders/auto-dispatch` 走**同一個試跑再落盤**，兩者數字必相等（同口徑測試）。
- 清單頁的成員計數與狀態彙總：以頁上單號集合一次 GROUP BY（定案 40）；依問題視角涵蓋欄同法以頁上問題集合一次查。
- `POST api/work-orders/auto-dispatch { from, to }`（`Maintain`）：以一份快照對缺口逐問題套策略；回 `{ created: [{handlerId, count, workOrderIds}], unassigned: [{source, eventId, hostCount}] }`；稽核一列。
- 逐單授權單點函式：處理人本人／`Assign`／`ViewAll` 三者之一；成員依檢視者可見範圍過濾並回 `hiddenMemberCount`；否則 403（不是 404——單存在但無權）。

**C-3（前端）**
- 依問題視角：涵蓋欄「N/M 台」＋處理人 chip（連單）；動作鈕「交辦」／「統一標記」／「回覆」（自己有此問題進行中單時）；交辦 modal：計數與閘門摘要、分攤模式、併入提示、可展開分頁清單、衝突改派勾選、守門訊息、續掛勾選（範圍為指定主機時隱藏）。
- `/work-orders` 四頁籤；`/work-orders/{id}` 詳情（標頭、成員表含狀態篩選與「複製 CSV」、時間軸、admin 動作鈕）；nav「監控作業」加「交辦總覽」（`requires: ['Assign','ViewAll']`）。
- 全部連結走 `appUrl()`；不寫死路徑。

**C-4**
- 設定頁「自動派工」段：開關＋固定說明文字（候選條件、負載口徑、閘門三條）＋無池群組警告；郵件段 `MailNotifyWorkOrders`。
- 群組頁使用者群組頁籤「派工池」勾選（admin builtin 不可勾，前後端皆擋）；使用者頁「暫停接單」勾選（`Maintain`）。

### 測試／驗收

- 解析母體＝依問題視角 `Aggregate` 同口徑；`excludeHostIds`；閘門計數；上限＋1 零寫入；預覽＝落盤（**突變**：計畫函式在預覽路徑多跳一台必須紅）。
- 分攤：`byLoad` 用看板同一份聚合；每人一單；已有單併入；預覽分配＝落盤分配。
- 日層級指派：既有 `HandlingServiceTests` 指派案例綠，且案件有 `WorkOrderId`；不變式 16a 守門測試（任一路徑建出的進行中案件 `WorkOrderId` 非空）。
- 五個單操作：成員／歷程／事件／稽核斷言。
- 授權：三種可看者各一正例；其他 403；`ViewAll` 成員過濾＋`hiddenMemberCount`。
- 缺口：靜音／有案件／閘門不過各一反例；建議處理人＝策略。
- 立即派工：無候選回待派；同問題多台同一人一單。
- 稽核七個動作反射守門有中文。
- 舊端點 grep 前後端零命中；`BulkScaleGateTests` 改對交辦單守門。
- 全量綠。

## 七、作業 D：處理端（handler）

### 現況與核對結果

- workload 全量、N+1（`HandlingHistoryQueryService.cs:329-440`）；側欄徽章每頁打整份（`layout.js:212-213`）。
- 回覆 modal 三欄無主機清單；bulk-status 逐案無上限。
- 詳情頁案件徽章 `CaseHandlerAccount`；儀表板無個人視角。

### 定案

定案 5、11（前端）、15（工作頁與徽章）、20、24、26、27。

### 契約要點

**D-1**
- `GET api/handlers/{userId}/work-orders?page&pageSize&status=active|escalated|overdue|unreplied|closed&source&eventId&groupId&sort`（重用 C-2 清單服務，固定 `handlerId`）；KPI `{ activeWorkOrders, activeMembers, overdueMembers, unrepliedWorkOrders }`；「被指派的風險日」表維持既有端點。
- `POST api/work-orders/{id}/reply { caseIds?[], status, note, dueDate }`：`caseIds` 空＝全部進行中成員；值域同既有回覆 modal（`in_progress`／`observing`／`escalated`／結案四態／`open`）；必填規則沿用（wont_fix 說明、escalated 原因、observing 日期）；授權 `Handle` 且單的處理人＝自己，否則 403（admin 亦然）；逐案 `SyncStatus`＋`TouchReply`＋`RecomputeClosure`；回 `{ workOrders: 1, hosts: N, days: M }`。
- `POST api/work-orders/reply-many { workOrderIds[], status, note, dueDate }`：全部須為自己的進行中單，否則整筆 403 零寫入；套用到各單全部進行中成員；回 `{ workOrders, hosts, days }`。
- `GET api/handlers/me/badge` → `{ activeWorkOrders, activeMembers, overdueMembers, unrepliedWorkOrders }` 單一聚合；`layout.js` 徽章改打它。
- 退役 `bulk-status` 與 `IssueCaseStatusController`。

**D-2**
- 使用者詳細頁（§9.8a）「被指派歷程」加「所屬交辦單」欄（有值才顯示，連單）；`layout.js` 活動告示加「案件逐日同步：待處理 N 件」（讀 `run-activity` 新欄，不碰 `isRunning`）。
- 我的交辦頁：KPI 四格；篩選列（狀態／問題／群組）；交辦單表（列：問題、成員數與彙總、逾期、未回覆、期限、最近新增、來源徽章「系統整併／負責人規則／自動派工」）；多選勾選＋「回覆選取的單」；展開成員（分頁 50、狀態篩選、勾選、「回覆」、「去處理」、主機頁連結）；「依主機」次要頁籤（成員平鋪）；「被指派的風險日」維持；底部註腳「另有 N 張暫停」。
- 回覆 modal（`issue-status-reply.js`）：對象文字「本單 96 台中的 5 台」或「3 張單共 240 台」；欄位不變。
- 儀表板卡「我的交辦」（有 `Handle`）：進行中單／逾期／未回覆→連我的交辦。
- 詳情頁問題列徽章：有單→「單號＋處理人」連單；無單→舊徽章。

### 測試／驗收

- 清單分頁與篩選；查詢次數不隨單數增長。
- 部分回覆：勾 3 台→只 3 台；別人的單 403 零寫入（**突變**：拿掉檢查必須紅）；全結→單結案；`LastReplyAt` 更新。
- 多單回覆：含別人的單→整筆 403 零寫入。
- 徽章＝KPI 同口徑。
- 舊端點 grep 零命中；全量綠。

## 八、作業 B：靜音

### 現況與核對結果

- `IssueProfile` 已有負責人與結論（`IssueProfile.cs:30-67`）；管理頁與 service 有稽核。
- `SuppressionFilter.MarkSuppressed` 純函式（`SuppressionFilter.cs:27-49`）；`Suppressed` 不拉日風險、不進趨勢／AI（`LogAnalysisService.cs:948-954`、`AnalysisOrchestrator.cs:1133`）。
- `IssueProfile` 為 blob（`issue_owners`），加清單欄位不需 DDL；`lf_blobs.version` 可供快取失效判定。
- `IIssueAggregateQuery` 17 個方法無排除參數；`DayHandlingDerivation.Derive` 不看 `Suppressed`；`IssueHandlingRollupQuery.ExcludeConcluded` 決定重點問題與排行的「已有結論」。
- 詳情頁已有 `HiddenIssueCount` 誠實註腳。

### 定案

定案 10、22、25、28。

### 契約要點

**B-1**
- `IssueProfile.Mutes`：`List<MuteInterval { From, To, Reason（≤500，必填）, ById, ByAccount, At }>`（定案 34）；區間不重疊（新設定時若今天已在區間內＝延長：改該區間 `To`，另記稽核）；「目前靜音中」＝今天落在任一區間；「某紀錄日靜音」＝該日落在任一區間。舊 blob 缺欄＝空清單。
- `PUT api/issue-owners/{source}/{eventId}/mute { days（1～365）| until, reason, existingOrders: pause|close }`（`Maintain`；不存在的問題檔案自動建立空檔案再設）；`DELETE …/mute`（提前解除＝目前區間 `To`＝昨天，`From`＝今天則刪除）；到期日＝今天＋days−1。稽核 `issue_mute`（note 含區間與原因）／`issue_unmute`。
- `SuppressionFilter.MarkSuppressed(issues, activeSuppressions, muteIntervals, recordDate)`：`muteIntervals`＝(Source,EventId)→區間清單（OrdinalIgnoreCase）；**紀錄日**落在區間→`Suppressed=true`；集合由每趟快照供給。不寫任何快照欄位。
- 靜音區間快取：問題檔案 blob 以 `lf_blobs.version` 為鍵做跨請求快取（內容與使用者無關，註解明講），聚合查詢每次取用不重讀 blob。
- `Maintain` 修正：`SetConclusion`／`SetMute`／`ClearMute` 服務層檢查能力，不足擲授權例外整筆拒絕；既有 `HandlingServiceTests` 的 `AutoApply` 案例改以含 `Maintain` 的使用者呼叫，另補「無 `Maintain` 被拒且零寫入」。
- 定案 45：`AnalysisPromptBuilder` 對全部問題排除 `Suppressed`（不只 PRTG）；`RiskReportService` 「已抑制」段反查原因加靜音區間（顯示「靜音至 yyyy-MM-dd：原因」）。

**B-2**
- `IssueExclusion { CurrentlyMuted: (Source,EventId) 集合, MuteIntervals: 與查詢期間重疊的區間清單 }` 傳入 **`IIssueAggregateQuery` 全部方法**（反射守門：每個方法簽章含此型別參數，或介面改為統一 `IssueQueryFilter` 物件——擇一，守門測試必在）。兩種用法：**列表／卡片／排行**用 `CurrentlyMuted` 整個問題排除並回 `MutedIssueCount`；**待辦／逾期推導**（`DeriveDayHandling`、`AggregateDayTodo`）用「目前靜音中 或 紀錄日落在區間」視同已有結論——SQL 端把重疊區間組成 `(source, event_id, record_date BETWEEN)` 排除條件。
- 推導單點 `IsMuted(source, eventId, recordDate, snapshot)`：記憶體路徑（`DayHandlingDerivation.Derive`）與 SQL 路徑同一規則——兩條路徑同口徑測試（同一組資料兩邊算出的待辦數相等，且含「區間內的舊日子」「區間外的舊日子」「到期後的新日子」三型資料）。靜音問題視同已有結論（不計未處理、不計逾期）；`IssueHandlingRollupQuery.ExcludeConcluded` 同；`ToIssueDto` 給該日命中的區間（`MuteFrom`／`MuteTo`／`MuteReason`／`MutedByAccount`）；`WorkOrderQueryService` 以問題檔案快取推導暫停並在處理人清單排除＋`pausedCount`；`MailIssueDigest` 套排除。
- 靜音與 `AutoApply` 同時存在：靜音期間不套結論（⓪ 在 ③ 前）。

**B-3**
- 靜音 modal（依問題視角列內、問題檔案頁、總覽靜音頁籤解除）：時長六鈕＋自訂天數、原因必填、三句常駐提示（新資料不進待辦與告警；靜音前未處理到期後回來、永久結案用統一標記；群組範圍用抑制）；**既有進行中交辦單處置**（定案 38）：顯示「N 張單／M 台」＋單選「暫停，到期恢復」（預設）／「代為結案為不處理」（需 `Assign`＋`Handle`，原因同靜音原因）；`PUT …/mute` 多帶 `existingOrders: pause|close`。解除靜音後暫停的單恢復，總覽「進行中」多一個「自靜音恢復」篩選（`ResumedFromMuteAt` 推導自問題檔案的解除時間或到期日，不落盤）。
- 註腳位置：依問題視角、儀表板重點問題卡、報表排行、我的交辦（暫停）；詳情頁收合區徽章「靜音至 yyyy-MM-dd」tooltip 含原因與設定者。
- 決策表：設定頁與規則頁抑制段各一段短說明＋連到說明書。

### 測試／驗收

- 邊界：今天靜音 1 天→今天靜音中、明天不是；區間迄日＝昨天→不靜音；`days=0`／366→驗證拒絕；靜音中再設→延長同一區間不新增；區間不重疊（反例：跨區間的日子只命中一個）。
- 分析側：紀錄日在區間→`Suppressed`、日風險不拉抬；區間外不標；重新分析區間外的舊日子（在靜音期間執行）不標；**突變**：改以執行時間判定必須紅（上述第三例會翻）。
- 區間推導：靜音期間的日子到期後仍已有結論；靜音前未處理日子到期後回待辦；提前解除當天起恢復、解除前的日子仍已有結論（三組正反例）。
- 讀取側：反射守門；依問題視角不列且計數正確；儀表板卡＝下鑽（同口徑）；digest 與排行不含；缺口不含。
- 推導：不進待辦、不算逾期；解除前後逐日列逐位相同。
- 交辦單：暫停不列、`pausedCount` 正確、總覽頁籤列出；到期恢復。
- 掛接：⓪ 不建單不續掛；靜音＋`AutoApply`→不套結論。
- 授權：非 `Maintain` 三個入口皆拒絕零寫入；稽核對照守門延伸。

## 九、作業 E：通知

### 契約要點

- `NotifyWorkOrderAsync(kind: created|reassigned|transferred|cancelled, 單, 收件人)`：主旨「[LogForesight] 交辦：{問題}（{N} 台）」／「…已移交」／「…已取消」；內容：單號、問題與白話說明、主機數（前 20 台列名）、說明、期限、詳情頁連結（站台基底路徑設定）。改派：新處理人 `created`、舊處理人 `transferred`。
- `NotifyWorkOrderDigestAsync(趟末摘要)`：每位處理人一封，列本趟新建單與續掛台數；無內容不寄。
- 週報：加「靜音中問題」段（問題、到期日、原因、設定者）；為零不出現。
- 開關：`MailEnabled` 且 `MailNotifyWorkOrders`；收件人無 email→warn 一則不擲例外。
- 定案 45：問題負責人路由（`ResolvePerRecipient` 的 TopIssues 比對）排除 `Suppressed` 問題——靜音或抑制中的問題不觸發負責人通知。

### 測試／驗收

- 建單一封；改派兩封內容不同；取消一封；續掛每趟每人一封（兩單合併）；開關關閉零寄送；無 email 不擲例外；週報靜音段存在／為零不出現。
- 負責人路由：某日只有被抑制／靜音的問題命中負責人規則→該負責人不在收件人；同日另有未抑制問題命中→仍收（兩組）。

## 十、作業 F：規模修正

### 契約要點

- `VisibilityService`：`IsCaseGrantOnly(hostId)`／`GetIssueKeyRestriction(hostId)` 按需查（`EXISTS`／依主機查該使用者案件問題鍵），請求內以主機為鍵快取；`HandlingHistoryQueryService` 改用「該使用者案件涵蓋的主機 id 集合」單一查詢。語意不變。
- `GetOpenMany(hostNames, issueKey)`；`close-preview`／`PlanBulkClose` 改用。
- `LF_SCALE_BENCH`：「4000 台人工交辦建單同步段（目標 < 5 秒）與背景展開 4000 台 × 60 天（記錄總耗時與每批耗時）」「4000 主機日夜間掛接含自動派工（與現行掛接耗時差 < 20%）」「750 成員回覆同步段（< 3 秒）」「2000 缺口試跑（< 5 秒）」四案例（預設略過），輸出耗時寫進體檢交接。

### 測試／驗收

- `CaseGrantVisibilityTests` 綠不改斷言；查詢次數不隨案件數／主機數增長；壓測受 `LF_SCALE_BENCH` 守門。

## 十一、文件（Claude 親寫）

- WEB-SPEC：§7.1（派工池、暫停接單、可見範圍規則在 Core、交辦單授權）；§8.6a 名詞表；§9.1 我的交辦卡；§9.2 交辦／涵蓋欄／靜音／回覆；§9.3 真表修矛盾、徽章、靜音徽章、日層級指派走交辦單；§9.4a 全面改寫；新 §9.4b `/work-orders`＋`/work-orders/{id}`；§9.6 靜音註腳；§9.7 抑制段決策表連結；§9.8／9.8a 旗標；§9.9b 設定；§9.9c 說明書三條動線（S2／S3／S5）；§10.1／10.2 新表與 store；§11 稽核動作；`AttachNewDay` 七層＋閘門。
- DB-SPEC：新表與新欄（含部分唯一索引的 NULL 排除理由）；補 `lf_issue_cases` 欄位定義；**移除 `lf_user_host_map`**；容量估算與保留加交辦單；§A 原則改寫。
- RULES-SPEC：決策表。DETECTION-SPEC：靜音在分析側＋快照欄位。
- BACKLOG：移除三條、新增六條（第二版所列）。
- CLAUDE.md：測試基線（自 4293 起算）；「不要做」加兩條（不複製可見範圍規則；不建沒有交辦單的進行中案件）。
- 操作說明書嵌入資源 `LogForesight.Web/HelpContent/03-issues.md`、`04-record-detail.md`、`05-handling.md` 與對應 `.ai.md`：指派／回覆處理狀態段落改寫為交辦單動線（S2／S3／S5／S6），Claude 親寫；屬編譯資源，改動進 commit。
- 定稿前掃敘事字眼。

## 十二、風險與回滾

| 風險 | 緩解 | 回滾 |
|---|---|---|
| schema 升級在正式 SQL Server 失敗（部分唯一索引語法） | DDL 兩後端各寫、A-1 以 SQL Server DDL 字串靜態斷言；升級在測試 DB 先跑 | 全部 DDL 為**新增**（表／欄／索引），舊版程式碼可直接回退執行、忽略新欄 |
| 回填把不該合併的案件合併 | 只依（處理人, Source, EventId）分組、只處理進行中、冪等；啟動 log 有計數 | 新欄 `work_order_id` 清空＋刪新表即回到舊狀態（文件寫明 SQL） |
| 負責人閘門讓某些原本會建案的問題不再建案 | 定案 17 使用者已同意；趟末摘要有閘門略過計數；BACKLOG 記觸發條件 | 設定面無開關（刻意）；要恢復舊行為＝解除抑制／調整 `UnhandledSeverities` |
| 自動派工把工作派錯人 | 預設關；池由 admin 圈定；候選必須看得到主機；取消交辦可收回；趟末彙總信 | 關開關即停；取消交辦 |
| 靜音變盲區 | 上限 365、總覽頁籤、週報段、體檢提醒、到期自動回來 | 提前解除 |
| 建單／回覆同步逾時 | 就地／背景分流（定案 32、36、44）：同步請求只寫案件與觸發日，逐日展開超門檻進背景 | 作業可停（`DaySyncPending` 留著下次續跑） |
| 逐日列與案件短暫不一致（背景未跑完） | 畫面「逐日同步中 N 台」、活動告示待處理件數、作業每 5 秒撿件 | — |
| 靜音到期 SQL 與記憶體推導分岔 | 靜音區間單一事實來源（定案 34）＋兩路徑同口徑測試 | 區間存 blob，可直接編輯 |
| 啟動回填 10 萬列案件解析太慢 | 分批 1000、NULL 標記可中斷續跑、只在有未回填列時執行 | 中斷後下次啟動續跑 |
| 普遍性問題把一個人壓垮 | 可見範圍分割天然按部門分單；看板同時顯示成員數與單數；口徑一處可改（定案 41） | 改派／拆單 |
| 舊端點退役讓快取的前端 JS 打 404 | `asp-append-version` 已對模組加版本戳（`HandlerDetail.cshtml:36`） | — |

## 明確不做（本輪定案）

- IssueKey 大小寫比對統一（BACKLOG 3-13）。
- MTTA／MTTR 與「指派後為 open 直到認領」（BACKLOG 3-8；本輪只加未回覆指標）。
- 靜音的主機群組範圍（群組範圍用既有抑制）。
- 多問題交辦單的 UI 入口（以多單回覆補位）。
- 交辦單處理人變更歷史查詢。
- 站內即時通知。
- 負載加權、個人容量上限。
- 建單背景作業化（先同步＋壓測）。
- 後端 CSV 匯出端點（沿用前端複製當前頁）。

## 十三、複審記錄

### 第二版四角度補強

| 角度 | 發現 | 處置 |
|---|---|---|
| 程式面 | 日層級指派建出無單案件 | 定案 16a＋C-1＋守門測試 |
| 程式面 | 同人同問題多張單 | 定案 16b |
| 程式面 | 夜間建案無閘門 | 定案 17 |
| 程式面 | 推導不看 `Suppressed`，到期整批回待辦 | 定案 22 |
| 程式面 | 並行寫同單 | 定案 29 |
| 程式面 | 已結案單無保留 | 定案 23 |
| 程式面 | 單號可列舉無授權 | 定案 24 |
| 尖銳使用者 | 派錯怎麼收回 | 定案 18 |
| 尖銳使用者 | 六種安靜機制 | 定案 25 |
| 尖銳使用者 | 靜音＝盲區 | 上限＋頁籤＋週報＋體檢 |
| 尖銳使用者 | 到期舊的回不回來沒講 | 定案 22 三句 |
| 尖銳使用者 | 同性質一次處理沒落地 | 定案 26 |
| 尖銳使用者 | 我的交辦沒篩選／匯出／詳情 | 定案 27＋D-1 |
| 管理者 | 昨晚派了什麼 | 定案 28 |
| 管理者 | 休假／離職 | 定案 21＋19 |
| 管理者 | 派出去沒人動 | 定案 20 |
| 管理者 | 改派舊人不知情 | 定案 11 |
| 管理者（manager） | ViewAll 看不到看板 | 定案 24 |
| 整體 | 靜音放哪 | 問題檔案；決策表 |
| 整體 | 「未指派」chip 與缺口 | chip 不動；涵蓋欄＋缺口頁籤 |
| 整體 | 說明書未更新 | 文件批次 |

### 與既有功能的衝突／重複核對

| 既有功能 | 關係 | 處置 |
|---|---|---|
| 依問題視角「指派」 | 被「交辦」取代 | 退役 |
| 「回覆處理狀態」 | 被單回覆／多單回覆取代 | 退役 |
| 統一標記 | 互補（無案件主機的批量結論） | 保留；`AutoApply` 補 `Maintain` |
| 負責人自動建案 | 改走交辦單＋閘門 | 定案 7、17 |
| 機房結論 `AutoApply` | 同鍵；靜音期間優先 | ⓪ 在 ③ 前 |
| 規則抑制 | 靜音重用其分析側標記；範圍不同 | 決策表；兩張清單不合併 |
| `NoiseMark` | 閘門讀它 | 定案 17 |
| 觀察中 | 案件層級不動 | 回覆值域維持 |
| 案件授與 | 語意不變 | F-1 改查法 |
| 工作頁「依主機」 | 保留為次要頁籤 | D-2 |
| 「被指派的風險日」 | 不動 | — |
| 「下一筆未處理」 | 不動；靜音經推導不列 | — |
| 報表 `unassigned` scope | 不動 | — |
| 側欄徽章 | 改輕量端點 | D-1 |
| 郵件 escalation | 保留 | — |
| CSV 慣例（前端複製當前頁） | 沿用 | 定案 27 修正 |

### 規劃完成後複檢（第三版）

- **與既有行為的衝突**：(1) 「同主機同問題只由一人處理」維持；(2) 案件授與語意不變；(3) `ExternalOf` 不改；(4) 統一標記授權不變、`AutoApply` 加 `Maintain`；(5) `AttachNewDay` ①②③ 不變、⓪ 前插、閘門與 ④⑤⑥ 後插，⑤ 行為變更已同意；(6) 聚合入口一起改；(7) 日層級指派對使用者只多單號徽章；(8) 403 與 404 分清楚（單存在無權＝403）。
- **批次之間**：⓪ 在 B-1 前集合為空；D-2 註腳在 B-2 前條件式；C-1 最小改接與 C-3 改版順序固定；C-2 的成員／時間軸端點形狀在 C-2 定案後 D 不得再改；E-1 依賴 A-4 的趟末摘要與 B-1 的靜音集合，介面在各自段定案。
- **四個坑**：什麼算一個——負載＝進行中單的進行中成員數；零候選→待派；閘門略過有計數；`hiddenMemberCount` 誠實回報。破壞性判準——回填條件與反例；取消交辦只清 `CaseId`＝該案件且非結案類的日子（反例：自標日子、已結案日子）。單向閘門——靜音有期限＋提前解除；派工閘門不提供強制派（治本）。移除類——三個舊端點呼叫端各一檔在白名單；`BuildCase` 呼叫端兩檔在 C-1 白名單；`GetCaseGrants` 兩個消費端在 F-1 白名單。
- **既有資料路徑**：DDL 全部新增型、可回退；舊 blob 缺 `Mutes`＝空清單；既有案件回填時 `DaySyncPending=false`、`Cancelled=false`。
- 複檢結論：第三版新增名詞表、劇本 S1～S7、契約要點、風險與回滾、CSV 慣例修正；其餘無新增事項。

### 4000 台流程模擬（第四版）

逐步放大檢視 S1～S9 在 4000 台下的資料量與查詢形狀（表見第三節末），抓到 6 個會出事的點與 6 個要補的細節，落為定案 31～42：

| 發現 | 嚴重度 | 定案 |
|---|---|---|
| 靜音狀態只在記憶體推導，SQL 端待辦推導看不到，到期後儀表板與詳情頁分岔 | 高 | 34（第五版改為區間） |
| 人工交辦回溯全歷史：3000 台 × 60 天＝36 萬列同步寫入 | 高 | 32 |
| 案件表無 (source, event) 索引，衝突／涵蓋／缺口全表掃 | 高 | 31 |
| 「同趟同問題一張單」在可見範圍分割下走不通 | 中 | 33 |
| 夜間建案若回溯歷史＝3000 次主機歷史掃描 | 中 | 32 |
| `SyncStatus` 逐案掃主機歷史，750 台回覆＝750 次查詢 | 中 | 36 |
| `NoiseMark` blob 逐主機日讀 | 中 | 35 |
| 回填逐筆 EF 更新、`LastReplyAt` 全空 | 低 | 42、37 |
| 靜音 90 天後舊單回隊列 | 低 | 38 |
| 待派沒有原因、admin 不知道去修矩陣還是加人 | 低 | 39 |
| 清單逐列計數 | 低 | 40 |
| 修好復發派給別人 | 低 | 33 延續性偏好 |

- 複檢結論（第四版）：新增定案 31～42、劇本 S8／S9、規模表、壓測四案例。

### 第五版：解法再檢視（三處換更根本的做法）

| 原解法 | 問題 | 換成 | 定案 |
|---|---|---|---|
| 靜音「分析時快照」寫在 JSON 與 `lf_top_issues` | 同一事實三處各一份；依執行時間判定讓重新分析舊日子語意飄；A-4／B-1 各加一欄耦合 | **靜音區間**存問題檔案，唯一來源；一律以紀錄日判定；`lf_top_issues` 與 JSON 皆不加欄 | 34 |
| 人工交辦「只掛篩選期間」＋主機日上限 5 萬 | 只是把寫入量推到回覆時（`SyncStatus` 展開全部歷史合格日）；期間外舊日子掛「未處理」直到有人回覆 | **案件日同步作業**：一個冪等同步函式，小量就地、大量背景；所有改案件的操作都只同步改案件；不設上限 | 32、36、44 |
| 缺口在 SQL 做＋註腳「建單時再套閘門」 | `NoiseMark` 與現行抑制套不進 SQL；要在 `lf_top_issues` 加 `suppressed` 欄 | **缺口＝派工試跑**：SQL 取主機層級候選列，記憶體跑同一個策略的乾跑；數字即結果 | 43 |

- 連帶調整：名詞表「靜音快照」→「靜音區間」＋「案件日同步作業」；`IssueCase` 加 `DaySyncPending`／`Cancelled`；既有 `BuildCase`／`SyncStatus`／`ReassignCase` 改薄包裝且既有測試不改斷言；壓測改量同步段與背景段；風險表對應更新；靜音 modal 的 `existingOrders` 參數併入 mute 端點。
- **批次之間（第五版）**：同步函式與作業在 A-2；B-1 不再碰 `lf_top_issues`；`EfAnalysisRecordStore` 本輪只在 F-1 的查詢改法碰到，與第 46 輪 PRTG 追加路徑不重疊，但基準仍以第 46 輪併入後的 dev 為準。
- 複檢結論（第五版）：三處換解法、連帶調整如上。

### 第六版：既有功能影響核對（第十四節）

逐共用點 grep 全部呼叫端後新增：定案 45（AI prompt 與負責人路由排除 `Suppressed`，既有行為變更）、46（快照作用域含 PRTG 路徑）、47（`IssueExclusion` 無預設值＋套用清單）；補 `INoiseMarkStore.GetAll`、`run-activity` 新欄、使用者詳細頁交辦單欄、`HelpContent` 嵌入資源改寫、`HandlingServiceTests` 的 `AutoApply` 案例加 `Maintain`、`lf-bulk-assign-hosts` CSS 共用不可刪、`ReportService.FilterByScope` 記憶體路徑套靜音、`WeeklyCheckup` 靜音到期段。
- 複檢結論（第六版）：新增如上；其餘無新增事項。

## 十四、既有功能影響矩陣（第六版：逐共用點 grep 呼叫端後定案）

每個「本輪會改到的共用點」列出全部呼叫端，逐一判定：**跟著調**（納入哪一段）／**明寫不套**／**不受影響**。

### 14.1 `IIssueAggregateQuery`（加 `IssueExclusion` 參數，定案 47）

| 呼叫端 | 用途 | 處置 |
|---|---|---|
| `DashboardService`（`Aggregate`／`AggregateByCategory`／`AggregateByHost`／`AggregateReportKpi`） | 儀表板卡與下鑽 | 套靜音（B-2） |
| `RecordListQueryService`（`Aggregate`／`LatestOccurrences`／`AggregateByDate`／`AggregateByHost`／`DailyHostCounts`／`FirstSeenFor`） | 四個視角 | 套靜音（B-2）；`FirstSeenFor` 為問題級首見日，套與不套結果相同，明寫 `None` |
| `ReportService`（`AggregateByCategory`／`AggregateByHost`／`AggregateReportTrend`／`AggregateReportKpiPair`；另 `FilterByScope` 記憶體路徑） | 報表 | 套靜音（B-2），**含記憶體路徑 `FilterByScope`**（BACKLOG 3-9 那條） |
| `IssueRankingBuilder`（`Aggregate`／`HostIdsByIssue`／`DailyHostCounts`／`FirstSeenFor`） | 重點問題／排行 | 套靜音（B-2） |
| `MailIssueDigest`（`Aggregate`／`ActionableOccurrences`） | 郵件排行 | 套靜音（B-2） |
| `IssueHandlingRollupQuery`（`LatestOccurrences`）、`IssueTodoQuery`（`ActionableOccurrences`）、`HandlingHistoryQueryService`（`AggregateDayTodo`） | 待辦／已有結論 | 套靜音（B-2，含區間） |
| `HostVisibilityResolver.HostIdsFor` | 問題負責人可見範圍 | **`None`**：靜音不縮權限 |
| `RuleAdminService.Aggregate`（抑制預覽命中數）、`AggregatePrtgRuleHits`（規則頁，第 46 輪起帶 `hostIds`）、`CalibrationService.AggregatePrtgRuleHits` | 規則命中統計／校準 | **`None`**：命中統計要看全量 |
| `PrtgDailyPipeline.GetPrtgFindingHitDates`（第 46 輪新增，PRTG 跨日判定） | 偵測層 | **`None`**：靜音不影響偵測 |
| `DeriveDayHandling`（目前無外部呼叫端，供 SQL 端日狀態推導） | 日狀態 | 套靜音（含區間），維持介面一致 |
| `IssueOwnerAdminService.Aggregate`（近期問題選擇器） | 問題檔案頁挑問題 | **`None`**：靜音中的問題要能被選到才能解除 |
| `RecordDetailQueryService.Aggregate`（vs 基準） | 詳情頁問題級基準 | **`None`**：靜音問題在收合區仍顯示基準 |
| `LogAnalysisService`（`LogAggregator.Aggregate`） | 同名不同物，非本介面 | 不受影響 |

### 14.2 `SuppressionFilter.MarkSuppressed`（加靜音區間與紀錄日）

| 呼叫端 | 處置 |
|---|---|
| `LogAnalysisService.cs:202`、`:567`（第二個套用點，執行端確認用途後一併帶入） | 跟著調（B-1） |
| `PrtgDailyPipeline.cs:393`（PRTG finding，第 46 輪共用套用函式；`AttachCase` 在 :419） | 跟著調（B-1）；PRTG 靜音鍵 (Source, 0)。跨日升級 `PrtgCrossDay.Apply`（:382）在抑制標記之前，屬偵測層不受靜音影響；跨來源佐證 `PrtgCorroboration.Apply` 已排除 `Suppressed`（`PrtgCorroboration.cs:41,44`），靜音經標記自然生效 |
| `WeeklyCheckupService.cs:148`（只用 `ActiveForHost` 列到期抑制） | 加「7 天內到期的靜音區間仍在發生」段（B-2） |
| `RuleAdminService.PreviewSuppression` | 不呼叫 `MarkSuppressed`，不受影響 |

### 14.3 `DayHandlingDerivation.Derive`／`HasOverdueIssue`（加靜音判定）

| 呼叫端 | 處置 |
|---|---|
| `HandlingHistoryQueryService`（:159 待辦、:294／:312 工作頁、:422 風險日表） | 跟著調（B-2） |
| `HandlingProgressCalculator`（詳情頁進度） | 跟著調（B-2） |
| `RecordListQueryService`（:799 狀態、:819 逾期） | 跟著調（B-2） |

### 14.4 `HostVisibilityResolver`（搬到 Core）

| 呼叫端 | 處置 |
|---|---|
| `VisibilityService`（四處）、`MailNotificationService.cs:391`（Singleton 直接呼叫）、`IssueOwnedHostIdsCache`（註解引用） | 改 using（A-3）；簽章不變；`IssueOwnedHostIdsCache` 快取鍵含問題檔案版本，靜音編輯會使其失效，無害 |

### 14.5 退役端點的呼叫端

| 端點 | 呼叫端 | 處置 |
|---|---|---|
| `issue-cases/preview`、`handler-candidates`、`bulk-assign` | `records.js:1377,1625,1708` | C-1 最小改接、C-3 改版 |
| `bulk-status` | `issue-status-reply.js:104`（`handler-detail.js` 與 `records.js` 共用此 modal） | D-1／D-2 |
| — | `hosts.js:756` 與 `records.js:1521` 共用 CSS 類 `lf-bulk-assign-hosts`（主機頁批次群組 modal 也用） | **不可刪該 CSS**；C-3 白名單註明 |
| — | `HandlingServiceTests`（bulk-assign／bulk-status 案例）、`BulkScaleGateTests` | 改寫為交辦單版（C-1／D-1）；基線測試數會變，收官記錄差異 |

### 14.6 `IssueCase` 的其他消費端（新增欄位與不變式）

| 呼叫端 | 用途 | 處置 |
|---|---|---|
| `OccurrenceStatusResolver`（`GetMany` 開案字典） | 待辦／rollup 共用骨架 | 不受影響（欄位新增型） |
| `UserAdminService.cs:140`（`GetByHandler` 被指派歷程） | 使用者詳細頁 | 不受影響；D-2 順手加「所屬交辦單」欄（單號連結，有值才顯示） |
| `RecordListQueryService.cs:271`（處理人欄）、`RecordDetailQueryService`（:81／:103／:262 徽章與先前處理） | 清單／詳情 | 不受影響；徽章改交辦單在 D-2 |
| `HandlingProgressCalculator` | 進度 | 不受影響 |
| `HandlingBlobMigrator`（blob→表一次性遷移建案件列） | 舊部署升級 | 不改；它建出的列若無新欄值，由 A-1 啟動回填補齊（回填條件以 NULL 判定，順序天然正確） |
| `ScaleDataSet`（壓測資料） | F-1 | 補新欄值以便壓測真實 |

### 14.7 夜間鏈路

| 點 | 處置 |
|---|---|
| `AnalysisOrchestrator.cs:917-925`：`AttachPrtgFindings` → `AttachCase` → `ReplaceRiskyEvents` 順序 | 不改順序；PRTG finding 已在 `AttachCase` 之前追加，會進派工 |
| `PrtgDailyPipeline.cs:419` 也呼叫 `AttachCase` | 定案 46：各執行單位自建快照 |
| `INoiseMarkStore` 只有 `GetForHost`／`Get`／`Save`／`Delete` | A-3 加 `GetAll()`（blob 一次讀）供快照 |
| `IdentityService.cs:201`（setup 建 builtin 群組）、`GroupAdminService.cs:78`（Upsert） | 前者 `DispatchPool` 預設 false 不改；後者 DTO 對應（C-4） |

### 14.8 AI／郵件／報告

| 點 | 現況 | 處置 |
|---|---|---|
| `AnalysisPromptBuilder.cs:182` 只過濾 PRTG 的 `Suppressed` | Windows／Linux 抑制與靜音問題仍進 AI 敘事 | 定案 45（B-1） |
| `MailNotificationService.cs:523` 問題負責人路由不看 `Suppressed` | 靜音問題仍通知負責人 | 定案 45（E-1） |
| `RiskReportService.cs:435` 「已抑制」段反查原因兩條路 | 靜音問題會列在此段但反查不到原因 | 加靜音區間第三條路（B-1） |

### 14.9 測試守門與說明書

| 點 | 處置 |
|---|---|
| `AuditQueryServiceTests.cs:81-84` 反射比對 `AuditActions` 全部常數有中文 | 新稽核動作必補對照，否則紅——契約已要求 |
| 沒有 `SystemSettings` 屬性覆蓋的反射守門 | C-4 新設定要自己補設定頁與 DTO 往返測試 |
| `HandlingServiceTests.cs:1269` 以 `Create(Assign, Handle)` 呼叫 `BulkCloseIssue(AutoApply=true)` | B-1 加 `Maintain` 後此測試必紅——**改測試加 `Maintain`**，並補一條「無 `Maintain` 被拒」 |
| `HelpContent/03-issues.md`、`04-record-detail.md`、`05-handling.md` 與各自 `.ai.md`（嵌入資源，AI 問答也讀） | 提到指派／回覆處理狀態的段落改寫為交辦單動線（文件批次，Claude 親寫；**是編譯進組件的資源，改動要進 commit 且在該段白名單**） |
| `/api/run-activity` 回應形狀 | A-2 加 `caseDaySyncPending` 欄；`layout.js` 活動告示顯示（D-2）；**不得**影響 `isRunning`／`isFetchRun`（CLAUDE.md 紅線） |

### 14.10 明確不受影響

統一標記 `bulk-close` 的路徑與期間語意；「下一筆未處理」（`NextUnhandledSequenceCache`，走待辦推導自然吸收靜音）；「未指派」chip 與報表 `unassigned` scope（`HandlingScopes.Unassigned` 看 `openCasesDict`）；案件授與語意；主機合併墓碑（案件以主機名為鍵）；權限異動檢核；規則維護頁的抑制清單；NetIQ／PRTG 取數；設定精靈。

### 開工前重驗（第 46 輪併入後，2026-09-17）

| 項 | 結果 |
|---|---|
| 案件協調器、案件 store、派工命令服務、郵件服務、可見範圍規則、schema 升級器 | 第 46 輪未改動，規劃引用仍成立 |
| `SuppressionFilter.MarkSuppressed` | 第 46 輪抽成共用函式，呼叫端為 `LogAnalysisService:202`、`PrtgDailyPipeline:393`，與定案 34 相容 |
| `AnalysisPromptBuilder` | 仍只對 PRTG 過濾 `Suppressed`（:182），定案 45 仍成立 |
| PRTG 跨來源佐證 | 已排除 `Suppressed`，靜音經標記自然不拉風險 |
| PRTG 跨日升級 | 在抑制標記之前、屬偵測層，不受靜音影響 |
| `IIssueAggregateQuery` | 15→17 個方法（新增 `GetPrtgFindingHitDates`、`AggregatePrtgRuleHits` 加 `hostIds`）；矩陣已補，兩者皆 `None` |
| `WeeklyCheckupService` | 第 46 輪新增 PRTG 段（排除 `Suppressed`）；B-2 的靜音到期段併在同檔，執行端避開既有 PRTG 段 |
| `lf_top_issues` | 第 46 輪未加欄；第五版定案本來就不加欄，無衝突 |
| 測試基線 | 4293（略過 6） |

### A-1 規格撰寫時的事實修正（2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| `lf_user_groups.dispatch_pool`、`lf_users.dispatch_paused` 以 DDL 加欄 | 使用者與群組是 JSON blob 集合（`UserStore`／`UserGroupStore : JsonBlobCollection`），不是資料表 | 兩個旗標是 blob 模型屬性，無 DDL；移到 A-3，DB-SPEC 不寫這兩欄 |
| 部分唯一索引 `(handler_id, source_name, event_id) WHERE closed_at IS NULL`，兩後端各寫 DDL | SQL Server 唯一索引把 NULL 視為相等、SQLite 視為相異；多問題單（問題欄 null）在兩後端行為分岔 | 過濾條件改 `closed_at IS NULL AND source_key IS NOT NULL`；`CREATE UNIQUE INDEX … WHERE …` 兩後端同一份字串 |
| 以 `source_name` 比對與建索引 | SQLite `=` 分大小寫、SQL Server 預設不分；`lf_issue_first_seen` 已有 `source_key`（大寫正規化）慣例 | 交辦單與案件表都加 `source_key` 欄，比對與索引一律用它；`source_name` 只供顯示 |
| 回填「啟動時在 `SchemaUpgrader` 後執行」 | 啟動路徑受 Windows 服務 30 秒逾時限制；既有大量搬移一律「啟動判定、背景服務搬」（`TopIssueBackfillHostedService`） | 回填改背景 hosted service；整併完成前存在沒有 `work_order_id` 的進行中案件，**後續階段必須容忍 `WorkOrderId == null`**（查詢與推導不得假設非空） |
| 解析失敗的 `issue_key` 留 null | null 同時代表「尚未回填」，每次啟動會被重撈 | 解析失敗寫 `source_key=''`，查詢與分組把空字串視同不可依問題查 |
| `IWorkOrderStore.GetActiveByHandler(page, filter)`、`GetMembers(page, status)`、`LoadBoard` 含逾期與近 7 日結案 | 分頁與篩選形狀取決於 C-2 的 DTO；逾期定義尚未落地 | A-1 只提供不分頁的依處理人清單、成員分頁（無狀態篩選）、看板四欄；篩選、逾期、近 7 日結案在 C-2 補 |
| `lf_issue_cases` 只加 `work_order_id`／`source_name`／`event_id` | A-2 的同步作業需要 `day_sync_pending`、`cancelled` | 兩欄併入 A-1 的 DDL，一次升級 |

A-1 規格（`.gemini-tasks/task-47-A1.md`）與上列修正後的 PLAN A-1 契約逐條對照完成：模型兩類＋案件五欄、兩張表＋案件六欄＋七個索引、升級器（含部分唯一索引 helper）、案件 store 三個新查詢、交辦單 store 十個方法、兩階段背景整併、保留清理、三個新測試檔＋既有合約測試追加，全部進規格且各自有驗收條目。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | impl-low | 兩輪通過（4332 綠／略過 6，總 4338，+39） | Claude 獨立重跑建置與全套；白名單、CRLF／BOM 核對；自做突變（成員計數改逐單查詢→「50 張單只發一次 SQL」轉紅，還原 cmp 相同）；查證處理歷程唯一寫入點一律帶 `created_at` | 第一輪規格錯誤三處由執行端依事實調整並接受：處理歷程是 `lf_log_lines` 的 JSON 行（無 `lf_record_handling_log` 表）、整併器由 store 取連線工廠、案件表兩支既有索引 EF 與升級器名稱本來就不同（既有 DB 可能重複一份，收尾記 BACKLOG）。第一輪驗收退回三項：案件存檔會把整併寫入的 `work_order_id` 蓋回 null（改為模型為 null 時不覆寫）、整併每組三次提交無原子性（改單一交易）、`LastReplyAt` 從 seq 0 全掃處理歷程（改以 `created_at` 索引定位起點、只計案件建立之後的回覆）；三項各有測試與突變 |
| A-2a | impl-low | 兩輪通過（4354 綠／略過 6，總 4360，+22） | Claude 獨立重跑建置與全套；白名單、CRLF／BOM 以位元組核對；自做突變（冪等判準拿掉「UpdatedAt 等於本次 OccurredAt」→「冪等_同一意圖重送不重寫_換OccurredAt照常寫」轉紅，還原 cmp 相同）；查證 PruneDetails 只清 ContentJson、事實表列保留 | 既有四個方法行為不動、既有測試檔零改動。**口徑決定**：候選日改走事實表後，詳情已清除的日子會進候選日——接受，因為依問題視角／儀表板／待辦的 SQL 聚合本來就算這些日子，舊寫法建案時跳過它們，讓儀表板上永遠掛未處理。第一輪退回：取消模式只查案件連結日期範圍，範圍外但屬於本案件的列會漏（改依 case_id 批次精確查 `GetByCases`）。第二輪執行端以「介面預設實作擲例外」避開白名單外私有替身——不接受，Claude 親改：拿掉預設實作、`RerunDateFinderTests` 私有替身補一個回空的方法。建置出現的 CS8629 警告在 `CalibrationServiceTests.cs:1190`，為第 46 輪既有，本輪未碰 |
| A-2b | impl-low | 一輪通過（4385 綠／略過 6，總 4391，+31） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；確認 EF 真實 SQLite 上部分唯一索引衝突擲 `DbUpdateException`（並發建單退路成立）；自做突變（範圍聯集不併入來單群組→聯集 Theory 兩組轉紅，還原 cmp 相同） | 執行端兩處合理偏離接受：主機存在性以一次 `GetAll` 驗（逐成員 `FindByName` 會線性增長）；多加「成員 IssueKey 必須解析回單的來源與 EventId」檢查。Claude 親改一行：結案事件的操作者帳號由空字串改為 `AuditActions.SystemAccount`，與整併器寫入的系統事件一致。留意：`MembersInto` 先寫新案件逐日列、後存改連／改派案件，中間失敗沒有測試（不會產生零成員單） |
| A-3 | impl-low | 一輪通過（4433 綠／略過 6，總 4439，+48） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；確認候選人來源內部建的能力解析器有帶入問題檔案相依；自做突變（拿掉問題檔案相依）→**原 6 條測試全數存活**，證實缺口 | Claude 親補一條「只有問題負責人身分也收入」測試，突變後轉紅、還原 cmp 相同——沒有這條，能力解析漏帶問題檔案時所有純問題負責人會靜默消失於候選外，⑤ 派不出去。執行端三處偏離接受：靜音區間字典鍵改用 `IssueProfile.KeyOf` 回傳的 tuple（規格自相矛盾，B-1 沿用 tuple）；能力解析器在 DI 是 Scoped，候選人來源（Singleton）以同一批 Singleton store 內部建一份（規則未複製）；多問題單 `RegisterOrder` 不進索引但計單數。留意：`GetResolvedSince` 走 `closed_at`＋`status` 無前導索引，每趟一次掃案件表，放量後若慢再加索引 |
| A-4 | impl-low | 開工前停下回報一次＋一輪實作通過（4462 綠／略過 6，總 4468，+29） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF／BOM 以位元組核對；四個 NetIQ／PRTG 測試檔與交辦單協調器測試檔 diff 無斷言變動；自做突變（未指派清單拿掉「當日已有標記」條件→「機房結論優先不建單」轉紅，還原 cmp 相同） | 執行端開工前停下回報規格漏洞（協調器沒有逐日列 store、批次逐日寫入會回溯歷史且動作碼固定），採做法 A 並追加白名單兩檔。執行端順手補「改派」歷程動作的中文（反射守門一上就抓到既有缺口）——接受。Claude 親修兩處韌性缺口：①派工脈絡建立失敗會讓整趟分析失敗（例如問題檔案 blob 損毀）→ 新增「不可用脈絡」，決策第一步即略過並計為 `unavailable`、不讀任何資料，補測試並突變驗證；②三路任一路擲例外時趟末彙總被跳過、已寫入成員缺 `appended` 事件 → 匯合改 try/finally。接受的執行端判斷：撞唯一索引而採用的既有單也登記進脈絡；⑤ 掛進負責人既有人工單時歷程記負責人派送。留意：成員寫入在建單之後失敗會留下零成員單，由結案掃描收掉；嚴重度 `Critical`（三級化前歷史資料）不在未處理集合內，與既有日狀態推導一致 |
| C-1a | impl-low | 兩輪通過（4469 綠／略過 6，總 4475，+7） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；四個既有測試檔 diff 無斷言變動；執行端突變（改派分組拿掉）連帶讓兩條既有 `RecordQueryServiceSearchTests` 轉紅＝既有測試守得住新寫法；自做突變（追加零成員仍寫事件）→「Append_成員全被略過」轉紅，還原 cmp 相同 | Web 端不再呼叫 `BuildCase`／`ReassignCase`，「進行中案件必屬一張交辦單」自此成立（整併前的舊案件除外）。第一輪執行端回報 A-2b 遺留缺陷：`Create` 成員全被略過時仍建出零成員進行中單（詳情頁指派、當天問題都在別人手上時每個問題一張空單）→ 授權修協調器：成員分類抽成唯一私有方法 `ClassifyMembers`，有效成員為零時不建單、不寫事件、回 `WorkOrderId=0`；併入與追加同理。接受：無檢視權提示範圍與舊碼一致；處理人為空的進行中案件在勾改派時會被派給新處理人（舊行為是不動）；改派清單在寫入前推算有極小競態；`Create` 多一次 `GetOpenMany`（常數次）。`DayHandlingCommandService` 已不再使用 `IssueCaseCoordinator` 但保留相依，C-3 退役舊端點時一併清 |
| C-1b | impl-low | 一輪通過（4498 綠／略過 6，總 4504，+29） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；自做突變（輪流分攤改帳號降冪）→三條轉紅，還原 cmp 相同 | 執行端的突變抓到自己的測試缺口（單人不改派情境沒有測試，突變存活）並補上。接受：群組分攤時已有進行中案件的主機一律略過、不計入分攤——若原處理人正是群組成員，其案件本來就在他同問題的唯一進行中單裡，不會漏派；計畫到寫入之間被搶先建案時協調器的略過會附加回報；預估主機日只有預覽另查一次、不扣排除主機，標示「期間內」。**留給 C-3**：預覽的 `Hosts`／`TotalHosts` 含手動排除、雜訊排除與略過的主機（讓畫面可重新勾回），`AffectedHosts` 才是排除後數字；`EventId` 改為可空以回驗證錯誤 |
| C-2a | impl-low | 中途停下回報一次＋使用者暫停後恢復，一輪通過（4559 綠／略過 6，總 4565，+61） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；恢復時先以位元組核對 14 個未提交檔（被 heredoc 腳本改過的兩個介面檔中文完整、三個半成品新檔由 LF 轉 CRLF）；自做突變（資料庫版逾期條件 `<` 改 `<=`）→「QueryOrders_EF與替身同資料結果一致(overdue)」轉紅，還原 cmp 相同＝雙跑比對守得住兩後端分岔 | 執行端中途因新介面方法要求白名單外測試替身補一行轉發而停下，放行 `WorkOrderDispatcherTests.cs` 一行。執行端自承兩次違規（`sed -i` 使檔案變 LF、heredoc 跑中文 python），已核對無損。接受：隱藏台數跟隨目前狀態篩選；限主機的成員查詢把「主機名＋案件編號」兩欄抓回記憶體排序（查詢次數不增）；已刪除操作者顯示「（已刪除）」；回覆事件的台數為本次回覆涉及數 |
| C-2b | impl-low | 三輪通過（4599 綠／略過 6，總 4605，+40） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；自做突變（Windows 規則命中拿掉「全部事件編號」）→`KnownIssueCatalogRuleMayHitTests` 轉紅，還原 cmp 相同 | 第一輪驗收退回兩項：①立即派工不判定抑制會派出假工作（原規格列為已知限制，推翻）→抑制預覽的「規則可能命中」判定抽成唯一 `KnownIssueCatalog.RuleMayHit`，抑制預覽與試跑共用，試跑逐主機套生效中抑制（可能多擋、不會少擋）；②立即派工部分失敗不寫稽核→逐組吞下、`FailedGroups` 回報、最後一定寫稽核。第二輪執行端停下回報兩個阻礙並經決定：`SuppressionFilter` 由 internal 改 public（只改可見性）；PRTG 規則命中依 Source `PRTG:{代碼}`（`PrtgFindingMapper.TryGetRuleCode`）判定。**查證時發現跨段缺陷**：`LatestOccurrences`／`ActionableOccurrences` 分組不含 `event_key`、回傳四段鍵——既有的依問題視角／rollup／待辦／郵件摘要把已處理的 PRTG 與 Linux 命中規則問題算成未處理，C-1b 手動建單與 C-2b 試跑對這兩類問題建出錯的鍵（會重複派工）→ 另開 C-2c 修正。接受：`SuppressionFilter` 公開後其餘既有 public 方法一併可見；`RuleMayHit` 單元測試暫放 `WorkOrderBoardServiceTests.cs` 檔尾；關聯型與量能型抑制不作用在單一問題 |
| C-2c | impl-low＋Claude | 一輪通過（4612 綠／略過 6，總 4618，+13） | 執行端先寫測試、在修改前版本跑出 8 條紅（錯誤訊息直接顯示少了第五段）再修；突變（分組拿掉 EventKey）7 條＋1 條轉紅；Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套、位元組核對換行、確認組五段鍵只剩 `IssueSignatureKey` 五參數多載一處 | **修掉的既有缺陷**：出現點查詢分組不含 `event_key`，依問題視角處理概況、重點問題與排行的已有結論、待辦、郵件摘要把已處理的 PRTG 與 Linux 命中規則問題算成未處理；C-1b 手動建單與 C-2b 試跑對這兩類問題建出四段鍵（案件對不上、會重複派工、同主機多顆 sensor 被併成一件）。執行端回報規格外第二份組鍵（`EfIssueAggregateQuery.IssueKeyFor`，規格限「只改兩個方法」未動）→ Claude 親改為呼叫單點。執行端指出使用端計數可能受影響 → Claude 查證：排行（`IssueRankingBuilder.LookupRollup`）與待辦（`IssueTodoQuery.Aggregate`）本來就是「任一筆未處理即未處理」；**依問題視角 `BuildIssueGroup` 取「每台主機最近出現那筆」，同主機兩顆 sensor 較晚的已處理會蓋掉較早的未處理** → Claude 親改為每台取最差狀態、處理人從全部出現點收集，補測試並以修改前版本確認轉紅 |
| D-1 | impl-low | 一輪通過（4640 綠／略過 6，總 4646，+28） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；白名單與 CRLF 以位元組核對；自做突變（依問題回覆處理狀態拿掉補記交辦單回覆）→「依問題回覆處理狀態_每張涉及的單各一筆replied事件」轉紅，還原 cmp 相同。**未解事項**：突變還原並完整重建後的第一次全套出現 1 條失敗（名稱未留下），接著連續三次全套全綠、無法重現——收尾體檢要留意不穩定測試（嫌疑：本輪新增、依 `DateTime.Now`／今天判定的測試） | 接受：新回覆服務的郵件相依改為必要參數（不沿用舊服務的可選參數，依限制條款）；`RunActivityBannerTests.cs`（白名單外）只改建構；依問題回覆處理狀態現在當場推導交辦單結案（原本等背景掃描）。留意：`ApplyIssueStatus` 為判斷是否為處理人回覆，每次多查一次進行中案件，統一標記逐筆迴圈（上限 5000 主機日）查詢量因此翻倍——若放量後統一標記變慢，改由 `SyncStatus` 結果帶回案件的處理人與單號 |
| B-1 | impl-low | 開工前停下回報兩次＋一輪實作通過（4683 綠／略過 6，總 4689，+43） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套；位元組核對換行；自做突變（`ActiveForHost` 不排除靜音）→「篩選_…不含IssueMute」轉紅，還原 cmp 相同 | **執行端開工前抓到的規格漏洞**：①問題檔案管理服務加相依會讓白名單外建構點編譯失敗（第一次用 `new 類別名(` 盤點漏掉兩處 target-typed `new(`，第二次補報）→放行共 7 個測試檔只改建構；②**`IssueOwnerStore.Upsert` 對已存在的問題檔案逐欄複製、沒有複製 `Mutes`——照原規格做，延長與解除靜音會顯示成功卻沒寫入**→放行補一行，並普查所有重建問題檔案後存回的呼叫點（只有 `IssueOwnerAdminService.Upsert` 需補，已補）。執行端突變時發現「當日無靜音即回傳」的判斷會擋掉原突變，改成真正生效的突變後 6 條轉紅；另自加兩個突變（Upsert 保留、統一標記提前檢查 Maintain）皆轉紅。Claude 親補：測試替身 `FakeIssueOwnerStore.Upsert` 也補複製 `Mutes`（替身 `Get` 回傳同一參考，漏欄缺陷仍靠三條真實 store 測試守住）。接受：`EnsureMaintain` 為 public（統一標記需在寫入前呼叫）；今天已在靜音中再設定＝重設迄日（可縮短，文件寫「重設迄日」）；靜音判定只比日期；AI 補寫以 backend 讀同一份問題檔案 blob；提示詞排除改三處（含前置掃描），全部靜音時仍不說「當日無事件」 |
| B-2a | impl-low | 一輪通過（4713 綠／略過 6，總 4719，+30） | Claude 獨立重跑建置（`--no-incremental`，1 個既有警告）與全套（首跑即全綠）；52 檔位元組核對 BOM／CRLF；自做突變兩個：日狀態階梯拿掉「全部靜音→resolved」→3 條轉紅；記憶體逾期判定拿掉靜音過濾→SQL／記憶體同口徑測試轉紅；還原皆逐位元組相同 | **執行端推翻規格一處（接受）**：逐鍵 OR 平衡樹會被 EF 攤平成線性鏈，500 鍵時 SQLite 擲「Expression tree is too large (maximum depth 1000)」→改為 `event_id IN` 粗篩＋「大寫來源#事件」組合字串 IN（目前靜音中）＋依區間分組的單一 CASE（已到期區間），800 區間 SQLite 實跑、SQL Server 只驗翻譯。接受：`IssueRankingBuilder.Build`／`HandlingHistoryQueryService.GetTodo(ByRange)`／`HandlingProgressCalculator.ComputeProgress` 保留原簽章並自取一次 `Current()`（白名單外正式碼呼叫端不動），儀表板與報表走帶 exclusion 的版本以維持同一次請求一份；`IssueHandlingRollupQuery` 由呼叫端傳入不另加來源；`RecordDetailQueryService`／`DispatchCandidateSource` 不直接呼叫聚合方法故未改；報表整包快取鍵也加 token；`AggregateReportTrend` 與 `FirstSeenFor` 收參數但不作用（資料來自日紀錄／機房級事實）。**收尾留意**：①執行端首跑全套 1 條失敗未留名稱（第二次出現，D-1 後同型：重建後首跑），收尾要以完整輸出抓；②`NextUnhandledSequenceCache` 鍵無 token，換日最多殘留 30 秒；③SQL 與記憶體對「當日無對應問題列的處理狀態列」本來就不一致（既有，未碰）；④`IssueExclusion.Spans` 含全部歷史區間，久了粗篩清單變長——F-1 評估只帶與查詢期間重疊的區間；⑤排除條件在 SQL Server 大表上的效能未實測 |
| B-2b | impl-low | 一輪實作＋停下回報一條（守門測試寫死方法數 17，白名單外） | Claude 親改守門計數 17→18；獨立重建（1 個既有警告）與全套：4744 綠／略過 6，總 4751（+32），唯一失敗為下述既有不穩定測試；白名單與 BOM／CRLF／NUL 以位元組核對；自做突變兩個：處理人清單預設改 include→3 條轉紅；詳情頁紀錄日區間取法改恆假→1 條轉紅；還原皆逐位元組相同 | **抓到不穩定測試的名稱**（D-1、B-2a 兩次未留名）：`SentinelRestDirectoryClientTests.多段預算用盡回部分結果與警告_不擲例外`——總預算 1 秒、第 3 個 job 時 `Thread.Sleep(1200)`，全套負載下第 1 段本身就逼近 1 秒而未完成任何段，警告字樣不出現；單跑 3/3 綠，本分支未改該檔。**收尾修**：預算判定改注入時鐘，不靠真實等待。接受：`CountCurrentlyMutedIssues` SQL 端以組合鍵字串 DISTINCT 計數；`PausedMode` 值域檢查併入既有 `WorkOrderQueries.Validate`；詳情服務走測試門面時固定傳空問題檔案替身（門面測試看不到靜音欄）；儀表板與報表的靜音計數不套嚴重度／日風險母體（與 `IssueRankingBuilder.Build` 同口徑）；`ResumedFromMuteAt` 只看迄日早於今天的區間。執行端疑慮「交辦單 `source_key` 截斷 255 而組合鍵不截」經查不成立：所有問題表 `source_name` 皆 255 上限、靜音問題檔案的來源取自同一批資料 |
| E-1a | agy（gemini-3.8-flash-high） | 一輪通過（郵件測試 58 綠，+2） | Claude 核對 diff 只動白名單兩檔、位元組核對 BOM／CRLF／NUL；自做突變（拿掉 `!issue.Suppressed` 篩選）→兩條新測試轉紅，還原逐位元組相同 | 無偏離；全套留到 E-1 各段完成後一起跑 |
| E-1b | agy（gemini-3.8-flash-high） | 一輪通過（郵件＋設定測試 213 綠，郵件 +5） | Claude 核對 diff 只動白名單五檔、位元組核對 BOM／CRLF／NUL；自做突變（通知方法拿掉 `MailNotifyWorkOrders` 判斷）→「開關關閉時不寄」轉紅，還原逐位元組相同 | Claude 親修一行：「…等 N 台」原以名單長度 >20 判定，呼叫端只傳部分名單時會漏註總數，改為 `HostCount > min(名單數, 20)`；`SystemSettingsServiceTests` 原本就沒有郵件欄位往返測試，未加（依規格） |
| E-1c | agy（gemini-3.8-flash-high） | 實作完成但**沒跑完驗收就結束**（stdout 停在「等測試結果」＋ terminating background task，無回報） | Claude 自跑建置、兩條計數（1／7）與篩選測試（63 綠，+5）；位元組核對；自做突變（拿掉「收件人是操作者本人不寄」）→「處理人是操作者本人時不寄」轉紅，還原逐位元組相同 | **agy 違反限制條款**：`NotifyHandler` 新增可選參數 `resolvedRules = null`＋`??` 回退（可選參數第六犯）→Claude 親改為必填、取消路徑傳入解析後規則；`WorkOrderBoardServiceTests` 實際未建構指令服務，未改 |
| E-1d | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（夜間派工測試 9 綠，+1；提示詞加「驗收逐條跑完等結果出來再結束」後沒再中途結束） | Claude 核對 diff 只動白名單三檔、位元組核對；自做突變（掛入既有單的名稱查詢改恆走回退值）→「彙總列帶問題名稱」轉紅，還原逐位元組相同 | 無偏離；`OrchestratorResult.DispatchSummary` 指派只以 grep 驗證（排程端寄信在 E-1e 以測試覆蓋郵件方法） |
| E-1e | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（郵件＋排程測試 168 綠，+5） | Claude 核對 diff 只動白名單三檔、位元組核對；自做突變（週報「目前靜音中」判定日提前 30 天）→ 1 條轉紅，還原逐位元組相同；**E-1 全段完成後獨立重建（1 個既有警告）與全套：4763 綠／略過 6，總 4769（+18），零失敗** | Claude 親改：註解內的執行端標記「task-47-E1e」改為專案慣例「回饋第 47 輪 E-1」。**收尾清理**：前幾段測試檔仍有 `task-47-*` 註解標記（`HandlingServiceTests`、`HandlingStoreContractTests`、`IssueMuteTests` 等），收尾統一改寫。規劃原分 E-1d 夜間摘要／E-1e 週報，實際 E-1d 做 Core 彙總、E-1e 做摘要信＋排程接線＋週報靜音段 |
| F-1a | agy（gemini-3.8-flash-high） | 一輪實作並完整回報（相關測試 429 綠） | Claude 核對 diff 只動白名單 17 檔；自做突變（EF `HasCaseOnHost` 加限進行中）→ store 契約「含已結案」轉紅，還原逐位元組相同；修正後相關測試 109 綠 | **agy 剝掉兩個測試檔的 UTF-8 BOM**（`VisibilityFakes.cs`、`VisibilityServiceTests.cs`）→Claude 補回；新測試斷言 `HostNamesWithCases` 回傳順序，但實作 `Distinct` 無排序（SQL 端順序不保證、潛在不穩定測試）→Claude 在 EF 實作加 `OrderBy`。語意核對：原以 `HostName` 不分大小寫分組，新方法以 `HostNameKey` 比對，等價 |
| F-1b | agy（gemini-3.8-flash-high） | 正式碼與 4 條測試寫完後**因網路錯誤中斷**（「There was a network issue connecting to the server」），沒跑驗收 | Claude 自跑：建置 0 錯誤；`_cases.GetOpen(` 4→3、`IsCaseGrantOnly` 1→0、`ForRange(` 17 處；相關測試 232 綠；位元組核對；自做突變（`ForRange` 拿掉「目前靜音中的鍵一律保留」）→「只留期間內重疊區間且保留目前靜音中的鍵」轉紅，還原逐位元組相同 | 無偏離。弱點：SQL 測試只以 `IssueExclusionSql.Apply` 比對 narrow 前後字面值，`EfIssueAggregateQuery` 各方法有無呼叫 `ForRange` 靠 grep 計數守住 |
| F-1c | agy（gemini-3.8-flash-high） | 兩檔寫完但**沒產出回報就結束**（stdout 停在「正在單獨執行…」＋ terminating background task） | Claude 核對 diff 只動白名單兩檔、位元組核對；未設變數 4 個略過；**Claude 親自實跑壓測（1.14 小時）** | **壓測結果（2026-09-17，本機 SQLite）**：①人工交辦 4000 台同步段 10,820 ms（目標 <5 秒，**未達標**），建 4000 件；背景展開 4000×60 **3,195,691 ms（53 分鐘）**、8 批、每批約 400 秒；②750 成員回覆 336 ms（達標）；③夜間掛接 4000 主機日：不派工 20,146 ms、含自動派工 205,346 ms（差 919%，**未達標**），建 144 單、掛入 14,761 台；④缺口試跑 2000 台 3,767 ms 但**缺口 0 筆**（案例無效，待查）。**根因（Claude 以暫時診斷測試定位，500×60 一批 500 件 333 秒、其中候選日查詢僅 228 ms）**：背景展開  與夜間成員寫入都在迴圈逐筆 ——每筆先讀尾端續號再單筆 INSERT，SQLite 關閉連線池每次開新連線，約 10 ms／筆（500 件＝29,500 筆歷程）→ 開 F-1d 批次寫入 |
| F-1d | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（相關測試 557 綠） | Claude 核對 diff 只動白名單八檔、位元組核對；自做突變（批次給號 `next++` 改 `next`）→ 2 條續號測試轉紅，還原逐位元組相同 | 無偏離；單筆與批次共用 `PrepareAndSerialize`；`AppendLines` 照 execution strategy＋交易寫法。修正後重跑壓測數字見下一列 |
| F-1d 壓測重跑 | Claude | 修正後實跑 11 分鐘 | — | **背景展開 4000×60：53 分鐘→91 秒**（8 批、每批約 11 秒）；**夜間掛接含自動派工 205 秒→126 秒**（不派工 20 秒）；人工交辦 4000 台同步段 10.9 秒未變。夜間仍為對照組 6 倍，但對照組零寫入、派工組實際新建 14,761 件案件＋逐日列＋歷程（約 7 ms／成員，SQLite 無連線池下的寫入固有成本），**規劃原目標「差 < 20%」口徑不成立，改記錄每成員寫入成本**；逐主機日分批寫入的再優化進 BACKLOG 候選。**Claude 以暫時診斷測試再找到兩處**：①交辦預覽 2000×60 要 21 秒——為取單一問題主機日而對全部問題彙總（18.3 秒；單一問題查詢 0.67 秒、數字相同）；②待派試跑上限算在排除已派出之前，2000 台以上待派清單與立即派工永久 TooLarge（壓測「缺口 0 筆」即此）→兩者併入 F-1e |
| F-1e | agy（gemini-3.8-flash-high） | 實作完成但**沒跑完驗收就結束**（stdout 停在「等待」＋ terminating background task，無回報） | Claude 自跑建置與計數（`occurrences.Count > _maxOccurrences` 0、`IssueHostDayCount(` 1）；相關測試 144 綠；位元組核對；自做突變（上限判定改回全部出現點）→「已有進行中案件的出現點不佔上限」轉紅，還原逐位元組相同 | **agy 剝掉三個檔的 UTF-8 BOM**（`IIssueAggregateQuery.cs`、`EfIssueAggregateQuery.cs`、`RuleAdminFakes.cs`；BOM 在本輪第二次）→Claude 補回。**既有測試 `待派_出現點超過上限_TooLarge且不試跑` 轉紅未申報**：它斷言上限檢查在查進行中案件之前（`GetOpenKeysCalls == 0`），正是本段要改的順序→Claude 改為 1 並加註理由，其餘斷言不變。前端 TooLarge 文字未查（agy 未回報），收尾 UI 段一併看 |
| F-1f | Claude 親修 | 定位與修正（幾行內小修，不委派） | 暫時插樁量測 `Plan` 各步驟（備份還原逐位元組相同）；修正後交辦指令服務相關測試 46 綠 | **根因**：4000×10 建單的 `Plan` 中「處理人看不到的主機」清單逐台呼叫 `NameOf(candidate)`（顯示名稱規則每次讀系統設定，設定 store 雖快取但每次仍對 DB 核對版本，SQLite 無連線池約 1 ms／次）→ 單此一步 3.7 秒。修正：每位處理人名稱算一次；計畫的 `NameOf`（略過主機清單逐台取原處理人名稱）依 id 記住。**結果（4000 台 × 10 天）：預覽 4.6 秒→0.83 秒、建單 5.8 秒→2.2 秒**。**BACKLOG 候選**：`JsonBlobSingleton` 快取每次 `Get` 仍查版本，任何「迴圈內取設定／顯示名稱」都是逐次 DB 往返，全站同型寫法待普查 |
| C-3a | agy（gemini-3.8-flash-high） | 四檔寫完但**沒跑完驗收就結束**（無回報） | Claude 自跑建置、`innerHTML`／寫死路徑 grep（零命中，連結皆 `appUrl`）、前端守門測試 37 綠；新檔 BOM 與同資料夾既有檔一致；**瀏覽器實測**（Stub 登入 demo-admin）：導覽「交辦總覽」出現、三頁籤切換正常、待派預設期間為昨天往前 7 天、主控台零錯誤 | `kanban` 圖示不存在改用 `inbox`。**未驗**：開發 DB 無分析資料與交辦單，只驗到空狀態，表格有資料時的欄位呈現留待使用者實測或收尾造資料驗 |
| C-3a 補驗 | Claude | 以壓測資料產生器做示範資料庫（60 台 × 14 天、2 張單、部分回覆、1 個靜音問題），開發站台指向它實測 | 進行中與負載看板有資料時欄位正確、主控台零錯誤 | Claude 親修：成員數、單號、未回覆、期限、建立欄位 `text-nowrap`（原 1440 寬時「20／20 台」與日期被擠成直排）。**實測順帶發現（C-4 設計前提）**：`UserStore.Upsert`／`UserGroupStore.Upsert` 逐欄複製不含 `DispatchPaused`／`DispatchPool`；但所有 Upsert 呼叫端（使用者編輯、群組編輯、AD 登入同步、批次新增、負責人匯入）都是新建物件只帶部分欄位——**若把旗標加進 Upsert，編輯顯示名稱就會把暫停接單清掉**。C-4 必須比照 `SetGroups` 另開專責寫入方法，並補「編輯使用者／群組不影響派工旗標」的反例測試；`BlobStoreRoundTripTests` 為手列欄位、不會自動涵蓋新欄位 |
| C-3b | agy（gemini-3.8-flash-high） | 首次派工遇 Gemini 五小時額度用罄（零改動），重置後重派一輪寫完三檔；仍**沒跑完驗收就結束**（無回報） | Claude 自跑建置、`innerHTML`／寫死路徑 grep（零命中）、前端守門測試 37 綠；**瀏覽器實測（示範資料庫）**：標頭、成員表（60 台分兩頁）、時間軸、改派全程 POST→toast→重載正確，主控台零錯誤 | Claude 親修兩處顯示：①時間軸註記的狀態碼（`in_progress：…`）轉中文；②改派註記的 `2→3` 轉處理人名稱（缺使用者清單權限時回退顯示 id）。Core 不持有顯示文字，兩者都在前端對照 |
| C-3c | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（相關測試 135 綠，+3） | Claude 核對 diff（17 檔皆白名單內）、位元組核對；自做突變（已交辦計數過濾改 `> 1`）→ 1 條轉紅，還原逐位元組相同；**瀏覽器實測**：依問題視角「已交辦」欄出現、示範單 20／60 台數字正確、總覽頁 `?source=&eventId=` 篩選橫幅與狀態改「全部」皆正確 | Claude 親修分層：agy 為了讓 Web 取得 `source_key` 正規化規則，把 `EfWorkOrderStore.SourceKeyOf` 由 internal 改 public，等於讓查詢服務相依 SQL 實作類別 → 改為在模型層新增 `WorkOrderIssueKey.SourceKeyOf`（單一規則），EF store 委派並回復 internal |
| C-3d | agy（gemini-3.8-flash-high） | 一輪寫完（records.js 單檔 +315／−272），仍**沒跑完驗收就結束**（無回報） | Claude 自跑建置、六條驗收 grep（舊端點 0 命中、新端點 2、`lf-bulk-assign-hosts` 保留、無 `innerHTML` 插值）、前端測試 43 綠；位元組核對；**瀏覽器實測**：交辦 modal 預覽（47 台／預估 77 主機日／分配「新建單」）、送出後清單重整且涵蓋欄變 47／47 台、modal 自動關閉 | 舊回應覆蓋防護有做（`previewRequestId`）。舊三個端點自此無前端呼叫端，退役在 C-3e |
| C-3e | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（移除 −733 行；測試 −9 條、改寫 2 條，全套 4772 綠／略過 10） | Claude 自跑四條退役 grep（皆 0）、`--no-incremental` 重建（1 個既有警告）、全套；位元組核對 11 檔 | **agy 誠實回報既有失敗且未擅自改**：`PrtgAdminPageUiTests.僅PrtgAdmin啟用Hash其餘頁面維持不變` 因 C-3a 的交辦總覽也用 `hash: true` 而紅。該守門是刻意的慣例（頁籤狀態寫進網址要逐頁決定）→ Claude 改為**明列制**（prtg-admin.js＋work-orders.js）並在 summary 寫明理由；總覽頁需要「從別處直接連到待派／靜音中頁籤」，B-3 與儀表板都會用。測試處置：舊端點測試移除 9 條、借舊 API 當入口但驗仍存在行為的 2 條改走 `WorkOrderCommandService.Create`。教訓：本機 Git Bash heredoc 內含中文會讓比對字串失真，改檔一律 Write 落腳本；`PrtgAdminPageUiTests.cs` 是 CRLF，先以 `newline=''` 讀再比對 |
| C-4 | agy（gemini-3.8-flash-high） | 一輪通過並完整回報（相關測試 480 綠，+7） | Claude 自跑建置與四條驗收 grep（`Upsert` 內零命中旗標）、位元組核對 18 檔；自做突變（把 `DispatchPaused` 加進 `Upsert` 逐欄複製）→「編輯不影響暫停接單旗標」轉紅，還原逐位元組相同；**瀏覽器實測**：群組頁派工池欄（三個內建群組 disabled）、切換即存並回 toast、使用者頁「接單」欄、設定頁自動派工段三條說明與無池警告（設池後警告自動隱藏） | 兩個旗標依前提各開專責寫入方法（`SetDispatchPool`／`SetDispatchPaused`），未動 `Upsert`。自動派工段放在「郵件通知」頁籤（agy 理由：與交辦單通知同屬夜間派工配套，且單一開關另立頁籤過於稀疏）——收尾 UI 複檢時再確認位置是否合理 |
| D-2a | impl-low（Opus low；使用者指示本段起停用 agy） | 一輪通過並完整回報（全套 4779 綠／略過 10，總 4789；前端守門 68 綠） | Claude 核對三檔白名單、位元組核對、紅線 grep（`innerHTML` 插值 0、`hash: true` 0、`bulk-status` 0）；**瀏覽器實測**（以處理人 scale-alice 登入）：KPI 四格、三頁籤、兩張單、勾選框不觸發展開、點列才展開且延遲載入成員、「回覆選取的主機」對象文字「本單 20 台中的 19 台」、送出後 toast「已回覆 19 台」與 KPI 重整（逾期 20→1、未回覆 1→0） | agy 中途被停（使用者指示），半套 diff 已丟棄重做。執行端主動補三項並申報：①清單與成員區補分頁（否則超過一頁的單／主機靜默消失＝資料遺失）；②依問題回覆取單 `pageSize=100`（同人同問題超過 100 張單會只回覆前 100，判為實務不可能）；③`openIssueStatusReplyModal` 改 async 並自吞取單失敗（`records.js` 不在白名單，無人接 Promise）。Claude 另查證 `LastAppendedAt` 確為「最近加入成員時間」，欄名「最近新增」正確 |
| D-2b | impl-low | 一輪通過並完整回報（全套 4779 綠／略過 10；前端守門 68 綠）；第 4、5 項依規格分支**停下未做**（後端缺欄位） | Claude 核對兩檔白名單、位元組核對；**瀏覽器實測**：側欄徽章 80 台＋逾期樣式＋新 title（進行中 2 張／80 台；逾期 1 台／未回覆 0 張）、儀表板「我的交辦單 2」卡在「未處理問題」之後 | 執行端申報三項：①`isRunning` 兩行一字未動（只行號位移），逐日同步條走另一個分支；②0 張時卡片 variant 用 `secondary` 而非規格寫的 `neutral`（`statCard` 直接組 `text-{variant}`，Bootstrap 無 `text-neutral`）；③驗收第 4 條 grep 必然命中 `dashboard.js` 兩處**既有**無插值靜態 `innerHTML`（非紅線，規格禁止改無關既有碼）——驗收指令本身過嚴，收尾時修指令不修碼。**未實測**：逐日同步告示需大量成員才會進背景（43 台的單是就地寫入、`daySyncPendingCases=0`），示範資料觸發不到，程式碼僅審閱。**缺欄位待補**：`RecordDtos` 問題項與 `UserAssignmentHistoryDto` 都沒有交辦單號 → 開 D-2c |
| D-2c | impl-low | 一輪通過並完整回報（全套 4776 綠／略過 10，總 4786；自跑突變：兩處組裝改回 null → 2 條新測試轉紅） | Claude 核對 diff、位元組核對 18 檔、重跑全套；**瀏覽器實測**：使用者詳細頁「所屬交辦單 #4」欄、風險日詳情頁問題列「#2」連結（需切到「顯示所有問題」才看得到，因該日問題都已有結論） | **執行端抓到規格錯誤**：詳情頁的 `openCase` 不是 `IssueCase` 而是 value tuple，欄位要多穿三處（故該檔 `WorkOrderId` grep 為 4）。越界兩處經 Claude 覆核皆必要且最小：`WorkOrderReplyController` 的失效 cref、`JsonBlobSingletonCacheTests` 三筆把退役端點當樣本路徑的 InlineData（改為 `/api/work-orders/12/reply`，仍落在「應推進版本戳」那側）。測試處置：移除 3 條（驗舊端點回傳形狀／舊守門／舊扇出路徑）、改寫 1 條（「轉入無法處理只通知一次」改走 `WorkOrderReplyService.Reply`，斷言不變）、新增 2 條。**Claude 親自退役孤兒**：`bulk-status` 拿掉後 `WorkOrderCoordinator.RecordExternalReply` 已無正式碼呼叫端 → 連同兩條專屬測試移除，第三條保留並改名只驗 `TouchReply`（詳情頁逐筆標記仍在用）|
### A-2 設計修正（讀完案件協調器全文後，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 既有 `BuildCase`／`SyncStatus`／`ReassignCase` 改成「改案件＋呼叫同步函式」的薄包裝，同步函式「有變動才寫歷程、連跑兩次零寫入」 | 現行 `SyncStatus` 每次呼叫都把每個合格日寫一列並記一筆歷程（狀態沒變也寫），觸發日與其他日的歷程動作不同；`BuildCase` 觸發日記 `case_assign`。改成「有變動才寫」會改變既有歷程筆數，91 個處理測試與 17 個協調器測試的斷言會被迫改寫 | **既有四個方法的行為完全不動**，只把「找候選日」換成共用的批次查詢（單主機＝批次大小 1）；新增的批次逐日寫入函式只給交辦單操作與背景作業用。同一條合格日規則仍只有一份 |
| 冪等＝「連跑兩次第二次零寫入」 | 批次寫入沒有跨 store 交易（逐日列、歷程、案件分屬不同 store）；中斷後重跑會重複寫歷程 | 冪等判準改為「該日列已等於目標且 `UpdatedAt` 等於本次意圖的 `OccurredAt`」→ 跳過該日的列與歷程；同一次意圖重跑不重複，不同次意圖照常寫 |
| 候選日以 `_records.Query` 反序列化整台主機全部紀錄 | 3000 台一次交辦＝十幾萬份 `ContentJson` 反序列化；`lf_top_issues` 有 `event_key` 欄可組回五段簽章，PRTG 追加也同步寫入 | `IAnalysisRecordQuery` 新增批次候選日方法：EF 走 `lf_top_issues`（主機分批、C# 組鍵以 Ordinal 比對，同現行規則），兩個測試替身由記憶體紀錄實作；墓碑別名沿用 `HostIdentity.Expand` |
| 待同步只有 `DaySyncPending` 旗標 | 背景作業要知道「寫成什麼狀態、誰、何時、哪種模式」 | 案件加 `day_sync_intent`（JSON，可空）；待同步期間使用者再操作同一案件時新意圖覆蓋舊意圖（案件層狀態以最新為準；被覆蓋那次的逐日歷程不寫，操作本身已在稽核紀錄） |
| A-2 一段 | 同步函式與交辦單協調是兩個獨立機制，合在一段超過「每階段 1～3 個機制」 | 拆 A-2a／A-2b |

### A-2b 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 任一成員重開→交辦單重開並記 `reopened_by_member` | 全專案沒有把 `IssueCase.ClosedAt` 清回 null 的路徑，案件結案後不會重開；且重開會撞「同人同問題一張進行中單」的部分唯一索引 | 不做重開推導，刪除該事件常數 |
| `SyncStatus`／`ReassignCase` 結束時呼叫交辦單結案推導 | 案件協調器若依賴交辦單協調器會循環相依；改其建構子牽動 8 個測試建構點 | 交辦單協調器自己的操作當場重算；詳情頁逐筆標記造成的「全成員結案」由背景同步服務每輪掃描補上（單句 SQL 找「進行中但無進行中成員」的單）。回覆時間（`LastReplyAt`）在詳情頁逐筆標記時的更新移到 D-1 的 Web 端處理 |
| 改派逐案呼叫 `ReassignCase` | 每案一次 `GetOpen`，3000 台線性查詢 | 批次改派私有方法，逐案語意等同 `ReassignCase`（同樣記一筆 `case_reassign` 歷程），案件一次 `SaveMany` |

### A-3 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 定案 12：`HostVisibilityResolver` 下沉到 Core | 規則內判斷「看得到全部主機」依賴 Web 的 `RoleCapabilityMap`／`Capability`；搬到 Core 要連能力列舉一起搬，牽動全站 `[Permission]` 標註。派工另需「具處理能力」判斷，也是 Web 規則（`UserCapabilityResolver`） | **規則留在 Web**。Core 定義 `IDispatchCandidateSource`，Web 實作：每趟執行前以既有規則算出候選人快照（具 Handle 者、是否在池、是否暫停、池成員各自可見主機），當資料交給 Core。`AnalysisOrchestrator` 沒有自訂建構子且測試不直接建構，A-4 以 DI 注入零測試牽動。規則仍只有一份；CLAUDE.md 的新紅線改寫為「Core 不得複製可見範圍或能力規則」 |
| 派工閘門、④⑤⑥ 由 A-4 在掛接流程內逐層判斷 | 同一套判斷還要給 C-2 待派試跑用 | 全部決策收進純函式 `WorkOrderDispatcher.Decide`（不改脈絡），夜間流程與試跑共用；本趟增量由呼叫端 `Commit`／`RegisterOrder` |
| ⑤ 負責人要看得到主機 | 問題負責人的可見範圍規則本來就包含「出現過該問題的主機」 | ⑤ 不檢查可見主機、不要求在池，只排除停用、無處理能力、暫停接單者；全部不可用時落到 ⑥ |

### A-4 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| ⓪ 靜音排在 ① 之前，靜音日連既有案件都不掛 | 靜音日由推導視同已有結論；②③ 掛入既有案件或套機房結論不會讓它回到待辦，擋掉反而讓案件 `LastLinkedDate` 停滯 | ⓪ 只擋新派工（④⑤⑥），②③ 不受影響 |
| 每個執行單位各建一份派工脈絡 | 本機、NetIQ、PRTG 三路**並行**共用同一個 `AnalysisRunContext`；各建一份會讓兩路同時為同一人同一問題建單、負載各算各的 | 一趟一份 `NightlyDispatch`，三路共用，決策＋建單＋寫入整段在脈絡鎖內；鎖以「替身在寫入時斷言鎖已持有」驗證，不用壓力測試 |
| 夜間派工寫稽核 `work_order_auto_attach`／`work_order_auto_dispatch` | 稽核在 Web，Core 的夜間流程沒有稽核寫入端 | 夜間留痕改為交辦單事件：建單時 `created`、趟末每張有新增的單一筆 `appended`（`MemberDelta`＝本趟台數）；執行紀錄一行摘要 |
| 夜間建的負責人單「續掛」 | 自動派工的處理人不一定看得到全部主機，範圍續掛會掛到他看不到的主機 | 夜間建的單一律 `Hosts`＋不續掛；同問題新主機由 ⑤／⑥ 的「該人已有此問題進行中單→掛入」接上 |
| 夜間成員寫入交給既有批次逐日寫入 | 執行端開工前回報：`WorkOrderCoordinator` 沒有逐日列 store；`SubmitCaseDays` 會回溯全部歷史、歷程動作碼固定為指派／同步 | 協調器建構子加 `IIssueHandlingStore`，夜間成員直接寫當日一列與依來源的歷程動作；另開 `RecordNightlyAppended` 供趟末寫事件、`EnsureNightlyOrder` 回傳是否新建 |
| 「不再打擾」只在負責人建案 | 原規則寫在 `AttachNewDay` 內 | 搬成派工閘門 `gate_dismissed`，對續掛、負責人、自動派工一律適用；逐主機延遲載入一次 |

### C-1 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| C-1 一段：新 API＋既有批次指派退役＋日層級指派改走交辦單 | 退役舊批次指派得同時改前端 modal（逐台分攤、勾選清單），屬 UI 改版，開工前要先問 `ui-ux-pro-max`；但在 UI 改版前舊 modal 仍在用，若維持直接建案會持續產生沒有交辦單的散案 | 拆 **C-1a**：既有批次指派與詳情頁日層級指派的端點、DTO、稽核不變，寫入改走 `WorkOrderCoordinator`（不變式從此成立）；**C-1b**：新交辦單 API（解析、分攤、單操作、稽核）。舊端點與 modal 在 C-3 前端改版時一起退役 |
| 建單成員解析沿用 `ResolveIssueOccurrences`（整段紀錄反序列化） | 依問題視角已改走 `IIssueAggregateQuery.LatestOccurrences`（每台主機、完整簽章、最近出現日），範圍、日期、可見嚴重度、日風險等級由 `RecordListQueryService` 私有方法算 | C-1b 把這四個解析抽成一個公開方法，建單解析與依問題視角共用（同口徑） |
| 手動交辦也套派工閘門（抑制、雜訊、嚴重度） | 閘門是為了防「自動」派出假工作；抑制判定要逐主機套範圍與規則 Id，出現點查詢沒有這些欄位；管理者手動選的問題與主機是明確意圖 | 手動交辦只排除「該主機對該問題有已知雜訊記憶」的主機並回報台數；抑制與嚴重度不擋，預覽顯示提示 |

### C-2 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 時間軸的回覆彙總從處理歷程以「同操作者＋同時間戳」分組 | 處理歷程是 `lf_log_lines` 的 JSON 行、不帶案件 id；3000 台的單要掃大量歷程 | 協調器回覆時寫一筆 `replied` 交辦單事件（狀態、說明、台數），時間軸只讀事件表 |
| C-2 一段 | 清單／詳情／成員／時間軸與看板／待派試跑／立即派工是兩組獨立機制 | 拆 C-2a（查詢）與 C-2b（看板、待派、立即派工） |
| 待派試跑套全部派工閘門（含抑制） | 出現點查詢沒有規則 Id 與抑制範圍，無法在試跑判定抑制；重建簽章需要完整鍵解析（目前只有 Source／EventId） | 試跑不判定抑制，DTO 帶 `SuppressionNotEvaluated` 由畫面註明；補 `IssueSignatureKey.TryParseFull` 並以來回不變式測試守門；立即派工以設定複本強制開啟自動派工，不改存檔設定 |
| 待派試跑直接呼叫派工決策 | 「不再打擾」閘門逐主機延遲查案件，4000 台＝4000 次查詢 | C-2b 在派工脈絡加批次預載 |

### D-1 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| D-1 退役 `bulk-status` | 唯一呼叫端是前端 `issue-status-reply.js`，屬 D-2 改版；改走協調器 `Reply` 會把觸發日歷程動作由 `issue_status` 變成 `case_sync`，牽動既有測試 | 本段不退役、寫入路徑不改，只在寫完後依交辦單分組呼叫 `RecordExternalReply`（回覆時間＋`replied` 事件）；退役留 D-2 |
| 回覆時間只在交辦單回覆端點更新 | 處理人在風險日詳情逐筆標記同樣是在處理交辦的工作；只看回覆端點會讓「未回覆」指標誤報 | 詳情頁逐筆標記由處理人本人做時 `TouchReply`（只改時間、不寫事件，避免逐筆淹沒時間軸）；管理者代標不算 |
| 處理人清單沿用查詢端清單 | 查詢端清單授權在 controller 層（Assign 或 ViewAll），處理人本人沒有這兩個能力 | 另開 `ListForHandler`／`HandlerSummary`：本人或 Assign／ViewAll 可看，組裝與查詢端共用 |

### B-1 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| `SuppressionFilter.MarkSuppressed` 增加靜音區間參數，各分析入口各自讀問題檔案傳入 | 抑制清單來自四個分析入口（本機、NetIQ 快照、PRTG、AI 補寫），`LogAnalysisService` 沒有問題檔案相依、測試建構點十餘處；報告「已抑制」段的原因反查也只看抑制清單 | 靜音以記憶體合成的抑制型別 `IssueMute` 呈現：包裝層 `MuteAwareSuppressionStore` 讀取時合成、寫入時濾除，只包三個分析入口，規則頁用的 store 不包；`ActiveForHost` 等「現在生效中」的篩選排除它（體檢、到期提醒不會把過去區間當生效中）；`MarkSuppressed` 另收合成項目與紀錄日判定 |
| 靜音 modal 的「代為結案」 | 代為結案需 Assign＋Handle，靜音 API 只掛 Maintain | `ExistingOrders=close` 時服務層另檢查 Assign＋Handle，不足整筆 Forbidden 零寫入 |

### B-2 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 清單／卡片／排行只用「目前靜音中」整個問題排除；待辦／逾期另用「目前靜音中或紀錄日落在區間」 | 兩套判定要在 SQL 與記憶體各寫一份＝四份；到期後區間內的日子在清單出現、在待辦卻已有結論，兩個畫面對不起來 | **收斂成單一列判定**：某列（問題、紀錄日）靜音＝該問題目前靜音中 或 紀錄日落在其任一區間。清單與待辦同一條；記憶體 `IssueExclusion.IsMuted` 一份、SQL `IssueExclusionSql` 一份，以同口徑測試守住 |
| 靜音問題「視同已有結論」 | 只把靜音列排出計數時，一天只有靜音問題會 total=0 → 落回日層級狀態 open，仍進待辦 | 日狀態階梯加一級：total=0 且有「原本會計入的靜音問題」→ resolved；階梯三份（SQL 兩處＋記憶體）收成 `DayStatusRule.Resolve` |
| 參數加在介面全部方法 | 既有方法尾端多為可選參數，必填參數放不到最後 | `IssueExclusion exclusion` 一律放第一個參數；反射守門檢查型別存在且無預設值 |
| 靜音區間快取以 `lf_blobs.version` 為鍵 | 問題檔案 store 每次讀 blob、沒有對外版本；靜音只經 HTTP 寫入，非 GET 皆推進 `DataVersionStamp` | Web `IssueExclusionProvider` 以（`DataVersionStamp`、今天）為鍵；`IssueExclusion.CacheToken` 併入儀表板、排行、待辦快照三個快取鍵（換日到期也失效） |
| `AggregateByDate`／`AggregateByHost` 套靜音 | 日風險主機數來自 `lf_daily_records` 的分析當下風險等級，無法以查詢條件重算 | 排除只作用在問題列相關部分（類別／事件／來源／嚴重度篩選與類別清單）；靜音前被該問題拉高的日風險維持原值，與既有抑制同一取捨 |
| B-2 一段 | 聚合介面 17 方法＋十餘呼叫端＋兩條推導＋註腳／詳情／暫停單／週報 | 拆 **B-2a**（排除參數、EF、兩條推導、提供者、呼叫端接線、快取鍵）與 **B-2b**（`MutedIssueCount` 註腳、詳情頁靜音資訊、交辦單暫停與 `pausedCount`、週報到期段） |

### B-2b 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| `MutedIssueCount` 註腳 | 被排除的問題無法從排除後的結果得知數量；再跑一次不套排除的聚合太貴 | 聚合介面加 `CountCurrentlyMutedIssues`（只數目前靜音中、期間內有列的相異問題，篩選同 `Aggregate`）；已到期區間造成的部分日子不出現不計入註腳 |
| 交辦單暫停「在處理人清單排除＋`pausedCount`」 | 處理人摘要是單句 SQL 分組＋關聯子查詢；在記憶體扣除會讓分頁總數與徽章不準 | 暫停鍵（組合鍵）下推 `WorkOrderQuery`／`HandlerSummary`；處理人入口預設排除、總覽預設列出並標 `Paused`／`MutedUntil` |
| （未寫）負載看板是否排除暫停單 | 自動派工以負載挑人；暫停單到期即恢復 | **不排除**：負載不因靜音瞬間歸零再跳回；⓪ 已不為靜音問題建單 |
| 「自靜音恢復」篩選，`ResumedFromMuteAt` 推導自解除時間或到期日 | 解除靜音會把區間 `To` 改成昨天，到期與提前解除在資料上同形 | 單一定義：問題不在目前靜音中，且最近一個已結束區間的 `To` 落在今天前 1～7 天 → `To+1`；篩選以鍵集合下推，分頁總數正確 |
| 週報「7 天內到期的靜音區間仍在發生」 | 週報只拿到已包裝的抑制 store，合成項目已帶區間與原因 | 從同一次 `LoadAll()` 取 `IssueMute` 項目，不加新相依 |

### E-1 設計修正（寫規格時，2026-09-17）

| 規劃原寫法 | 實際事實 | 修正 |
|---|---|---|
| 內容含「詳情頁連結（站台基底路徑設定）」 | 系統設定沒有站台對外網址欄位；新增設定只為一條連結、且 IIS 子 Application 前綴也要另外處理 | 不加設定：內文最後一行「請至站台的『交辦單』頁檢視單號 N」（與既有上報通知同一寫法） |
| 郵件服務組出完整交辦內容（問題白話說明、主機名） | `MailNotificationService` 沒有規則與案件 store 相依，建構點十餘處 | 呼叫端（交辦指令服務）組 `WorkOrderNotice`（含白話說明、前 20 台主機名、收件人 email），郵件服務只負責格式與寄送；建構子不變 |
| 開關 `MailNotifyWorkOrders` | 既有郵件開關皆為選擇加入 | 預設 `false`；UI 在 C-4 |
| 改派：新處理人 created、舊處理人 transferred | 操作者可能就是收件人（管理者派給自己） | 收件人是目前操作者時不寄 |
| E-1 一段 | 本段起依使用者指示改委派 agy（Gemini），agy 規範一段 1～2 個機制 | 拆 **E-1a** 負責人路由排除抑制、**E-1b** 設定＋通知方法、**E-1c** 指令服務接線、**E-1d** 夜間摘要（趟末摘要經 `OrchestratorResult` 帶到排程端）、**E-1e** 週報靜音段 |

**A-1 留給後續階段的事實**（寫 A-2 以後的規格時必須帶上）：
- `EfWorkOrderStore.Save` 是整列覆寫＋`UpdatedAt` 併發檢查：協調層必須讀新值再改再存，不可拿舊物件只改部分欄位。
- 整併完成前存在 `WorkOrderId == null` 的進行中案件；案件存檔只在模型有值時寫 `work_order_id`（連結只會換單、不會變回 null）。
- 整併器留有 `internal` 測試鉤子 `BeforeEventWriteForTest` 與 `ScannedLogLines`，體檢時確認沒有正式碼呼叫端。
- `LoadBoard` 的進行中成員數用關聯子查詢，只驗過兩後端翻譯得出 SQL，未在 SQL Server 實跑；C-2 若擴充看板要一併看執行計畫。
- 交易內 `ExecuteUpdate` 只在 SQLite 驗過回滾。

## 體檢交接

（實作輪收官時填：全量測試總數、全綠與否、與基線 4128 的差、壓測數字、未竟事項。）
