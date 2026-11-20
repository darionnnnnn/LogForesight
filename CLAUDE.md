# CLAUDE.md — 專案入口地圖

LogForesight：分析 Windows Server 與 Linux 主機的日誌（Windows Event Log／Linux syslog），
以確定性規則／趨勢／關聯層**提早發現硬體故障前兆與入侵跡象**；地端小模型只把結論翻成白話。
唯一的執行與查詢介面是 `LogForesight.Web`（ASP.NET Core MVC，.NET 10）。

這份檔案是**地圖不是百科**：先在這裡定位，再開對應文件。別把規劃內容寫回 README。

## 專案結構

- `LogForesight.Core/` — 分析邏輯類別庫。`Analysis/`（純規則/趨勢/關聯）、`Models/`、
  `Persistence/`（Sqlite/SqlServer 雙後端，`StorageBackend` 唯一路由點）、`Service/`
  （`AnalysisOrchestrator` 分析主流程單一入口與三路並行的組裝點、NetIQ pipeline、
  `PrtgDailyPipeline` PRTG 每日路徑、體檢）。
  沿用批次時期 `namespace LogForesight`（資料夾不對應命名空間）。
- `LogForesight.Web/` — 執行/查詢/維護介面。`Controllers/Api/`、`Services/`、`Auth/`、
  `Repositories/`、`wwwroot/js/`（原生 ES Modules：`core/` 共用、`pages/` 逐頁）、
  `Views/Pages/`（頁面殼，只回 View、資料由前端 fetch）。依 ASP.NET 慣例
  資料夾對應命名空間 `LogForesight.Web.*`。
- `LogForesight.Tests/` — xUnit 單元測試。

## 文件地圖：做什麼事讀哪一份

| 任務 | 讀哪裡 |
|---|---|
| 改 Web（頁面/API/授權/前端/設定） | `docs/WEB-SPEC.md`（§編號被程式碼註解大量引用，**勿拆檔**） |
| 改交辦單／自動派工／問題靜音 | `docs/WEB-SPEC.md` §9.4b（夜間掛接優先序、逐日同步、靜音判定）；表結構 `docs/DB-SPEC.md` §A |
| 改偵測邏輯/危險訊號清單/趨勢/關聯/AI 策略 | `docs/DETECTION-SPEC.md` |
| 改規則機制（語意邊界/seed/匯入/DB 映射） | `docs/RULES-SPEC.md`；Linux 規則面 `docs/LINUX-RULES.md` |
| 改資料庫欄位/索引/保留/Schema 升級 | `docs/DB-SPEC.md` |
| 改 NetIQ/Sentinel 取數 | `docs/NETIQ-API-REFERENCE.md` |
| 改 PRTG 整合（鏡像表/取數範圍與過期清除/取數策略與快照/觸發式取數/主機對應/規則與分類/跨來源佐證/未回報提示/探測/回填/資料搬運/校準匯出） | `docs/PRTG-SPEC.md` |
| 設計系統色票/字型/token | `docs/DESIGN-SYSTEM.md` |
| 改操作說明書（`LogForesight.Web/HelpContent/`） | `docs/HELP-WRITING-GUIDE.md`（寫給非 IT 人員：骨架、句長、用詞、排版限制） |
| 查「已知但刻意未做」 | `docs/BACKLOG.md` |
| 追某個現況決策的來龍去脈 | `docs/archive/README.md` 索引 → 按需開**單一**檔案，**非必要不要讀、勿全掃** |

## 慣例

- **分支流程**：自 `dev` 開 `feature/*`，完成後併 `dev` 給使用者實測、確認無誤才併 `master`；
  併入後刪除該 `feature/*` 分支。每個批次驗收通過後在 `feature/*` 分支 commit 並 push；併 dev／master 需使用者同意。
- **測試**：`dotnet test`（根目錄）。改動需維持全綠——目前基線 Windows **11,974 通過／12 略過**、macOS **11,959 通過／27 略過**，全量 Windows 約 14 分鐘、macOS 約 3 分鐘（耗時分析用 `LogForesight.Tests/tools/test-timing.py`）。前端行為測試需要 Node.js 22.7 以上；CI 兩個平台都跑。環境變數由 `LogForesight.Tests/test.runsettings` 帶入，
  歷次基線見 `docs/archive/README.md`。略過的 12 筆是規模壓測，設 `LF_SCALE_BENCH=1` 才跑；macOS 另略過 `[WindowsFact]`／`[WindowsTheory]`。文件／說明書批次可先跑定向的
  `dotnet test --filter "FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~HelpWritingGuide"`（後者是說明書句長守門，名稱不含 HelpContent，漏掉就是超長句一路帶到全量）與 JSON／Markdown 結構校驗；
  不把定向結果當成整合基線。**改前端的段落**定向篩選至少要含 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`——
  後三類名稱不含 UiTests，漏掉就是漏 import、`mini-dom` 缺方法這種整頁掛掉的退化一路帶到輪末全量才紅；改 view markup 前先 `grep` 測試專案找被斷言的字串（斷言 markup 的測試不一定叫 UiTests），新增或改 `*.behavior.mjs` 加跑 `NodeRuntimeTests`，改現行文件加跑 `DocNarrativeGuardTests`；新增或搬動端點、在 `IIssueAggregateQuery` 加方法時加跑 `Authorization|ControllerRouteSnapshot|IssueExclusionTests`（授權涵蓋清單與介面反射守門，名稱與被改的服務無關，定向篩選掃不到）；
  全量裡單一 Node 行為案例約 5 秒逾時而其餘全綠時，單獨重跑該測試類，通過即為負載下的偶發；
  `LogForesight.Tests/JsBehavior/*.behavior.mjs` 以切函式＋替身執行頁面模組，頁面新增匯入的共用函式要在替身補一份。SQL Server 驗證限 EF `ToQueryString` provider 翻譯，未連線實際 SQL Server；模擬整合／SQLite 結果也不代表正式站台或真實 PRTG 驗證。
  部署前驗證＝跑測試（規則合法性、遮蔽偵測、關聯層覆蓋皆為自動化測試，非手動 CLI）。
- **版號**：`1.0.<輪次>.<修訂>`，規則寫在 `Directory.Build.props` 註解。開新一輪的第一個提交就把輪次改成該輪 `FEEDBACK-N-PLAN` 的 N、修訂歸 0；
  實作方自查修正、換模型體檢修正、併 dev 後的實測補修各加 1 次修訂（同一批多個 commit 只加一次）；零星修補只加修訂。
  第三碼由 `VersionRoundGuardTests` 守門，第四碼在收尾時檢查。
- **語言**：說明文字與註解用**台灣繁中**（專有名詞除外）。全站用詞規範見 WEB-SPEC §8.6a。
- **畫面工作**：凡是跟畫面有關（頁面、元件、版面、配色、間距、互動、無障礙）一律套 `ui-ux-pro-max`，以 `docs/DESIGN-SYSTEM.md`（v3：§8 文字角色、揭露、按鈕與分段控制、寬度、判斷層／佐證層、共用元件一覽、§8.8 標記與顏色訊號）為基準，不必再問。
  字級只引用 `--lf-font-size-*` 字階或文字角色類別、不寫數值，根字級交給瀏覽器與「小／中／大／特大／最大」偏好（DESIGN-SYSTEM §3a，`FontScaleGuardUiTests` 守門）。
  驗收用 UI 資料集＋截圖量測工具（DESIGN-SYSTEM §8.7）：改到的頁面在五個寬度下六項量測為 0 才算完成；樣式表是 `wwwroot/css/` 的 base／components／layout／pages 四層，唯一引用點 `Views/Shared/_Styles.cshtml`。
  表格欄寬照 DESIGN-SYSTEM §8.4a（短值欄 `lf-col-nowrap`、留一欄吸收、同卡多表 `lf-table-aligned`）；無框品牌色按鈕只放表格列、分段控制與清單列（`lf-list-row`），按鈕角色與「每個範圍至多一顆主要按鈕」由 `tools/ui-global-asserts.json` 在每個量測情境檢查。
  修版面缺陷先寫會紅的量測斷言、在未修正的建置確認它紅再修——六項量測抓不到欄位起點不一致、孤立元素這類版面關係。
- **設定事實來源**：可調整項以 DB「系統管理 > 設定」（`SystemSettings`）為準；
  `appsettings.json` 只留啟動與安全前提（Storage/Jwt〔登入 cookie 時限〕/Auth）。**新增設定必須有消費端**——
  「有設定無行為」是本專案紅線。
- **文件紀律（現行 vs 歷程）**：`docs/` 主目錄只放**現行事實**，`docs/archive/` 放**過程記錄**。
  - 讀取：預設只讀現行文件；`docs/archive/` 非必要不讀，要查時先看它的 `README.md` 索引再開單一檔案。
  - 寫作：現行文件不寫「原本是 X、後來改成 Y、第 N 輪做了什麼」——只陳述現況，背景最多一行
    連結到 `docs/archive/`。定稿前掃一次「原本／後來／改為／取代／第 N 輪／回饋／體檢輪」等
    敘事字眼，抓到就砍或搬走。
  - 規劃案生命週期：新一輪規劃寫 `docs/FEEDBACK-N-PLAN.md`，完工時四步收尾——
    `git mv` 進 `docs/archive/` → 在 `docs/archive/README.md` 補一行索引 →
    現行文件只留結論 → 同步被這一輪改到的其他現行文件。

## 不要做

- 不要把偵測邏輯/規劃內容寫回 README（README 只留定位、結構、部署、操作）。
- 不要拆 WEB-SPEC（會斷開大量 §編號交叉引用）。
- 不要新增沒有消費端的設定欄位。同一條紅線的變體：畫面可編輯的欄位（規則的分類／嚴重度／重大旗標）
  不得在執行路徑被靜態常數表取代——設定存得進去、行為不跟著走，與「有設定無行為」是同一件事。
- 不要讓 AI 產出被當成 HTML 解析（前端一律 `textContent`／走 `markdown-lite` 唯一出口）。外部系統字串（PRTG sensor／device 名稱等）
  同理：`wwwroot/js/pages` 不得以含插值的樣板字串指派 `innerHTML`（`FrontendConsistencyUiTests` 守門）。
- 不要在前端寫死 `/` 開頭的路徑：連結組裝與轉址走 `core/paths.js` 的 `appUrl()`、路由比對走
  `appPath()`、API 走 `api.js`（出口已補前綴）、cshtml 走 `~/` 或 `@Url.Content`。站台可能掛在
  IIS 子 Application，寫死會整站 404 或選單靜默失效（見 WEB-SPEC §8.1a）。
- 不要假設外部系統回傳的欄位有它看起來像有的保證：`objid` 不一定是列的唯一鍵（PRTG messages 的
  objid 是發出訊息的 sensor，同一天一顆 sensor 有很多列——拿它去重會靜默丟掉第二筆以後）；
  單次大 `count` 不一定取得齊（少於 `treesize` 就是被截斷，不比對就是靜默少一半）。
  去重鍵要對到該表真正的唯一鍵，單發大查詢一律比對 `treesize` 並在取不齊時出聲。
- 不要在執行期增減執行紀錄的 NLog target 或規則，也不要讓 recorder 共用可碰撞的識別：全行程只有 `RunLogCapture` 安裝的一個常駐 target，
  各 `BatchRunRecorder` 以 `ScopeContext` 的逐實例識別碼（不是 RunId）登記並被分派，沒有識別、識別為空字串或已登出的事件一律不處理；
  target 的 `Write` 只做記憶體內計數與入該 recorder 的有界佇列，寫庫在背景執行緒、`Finish` 前排空，不得在記錄路徑上同步呼叫資料庫。
- 不要拿 `/api/run-activity` 的 `isRunning` 當互斥判斷：它是「取數或 AI 任一在跑」的聯集，只能決定
  告示出不出現；判斷某個動作現在能不能做一律看 `isFetchRun`（後端只在取數執行中擋主機更新，
  AI 單獨在跑時那個動作是允許的）。
- 不要新增鍵沒涵蓋授權維度的快取：跨請求快取的鍵必須含可見主機集合（或 userId）與資料版本戳，
  少一維就是把 A 看得到的東西端給 B；快取內容若與使用者無關（純鏡像結構）要在註解明講理由。
  版本戳在背景寫入執行中不推進，所以有版本戳的快取仍要保留短 TTL 當上限（`IssueRankingCache`、`ActionableSnapshotCache` 即此形狀）。
  跨請求快取一律繼承 `BoundedTtlCache<T>`（TTL、版本清除、元素上限、LRU、統計、未命中經 `SingleFlight` 都只寫在它，子類別只組鍵與定型別）；其他未命中時的計算一律經 `SingleFlight`（同鍵並發只算一次，結果過大進不了快取時也一樣），否則多人同時開儀表板會各算一整份疊加；
  singleton 與 static 上新增的可變集合要在 `LongLivedCollectionGuardTests` 登錄上限或清除時機，站台以月計不重啟，沒有出口的集合就是慢速洩漏。
- 不要在 Core 複製可見範圍或處理能力規則：兩者依賴 Web 的 `RoleCapabilityMap`，派工候選人由 Web 的
  `IDispatchCandidateSource` 每趟算好快照交給 Core。複製一份就是兩套規則，改一邊另一邊靜默失準。
- 不要建出不屬於交辦單的進行中案件：任何建案路徑（交辦、日層級指派、夜間派工）一律經 `WorkOrderCoordinator`。
  繞過它直接寫 `IssueCase` 會產生沒有單的散案，處理人清單、徽章、看板全都數不到（`IssueCaseCreationGuardTests` 守住 `new IssueCase` 只出現在 `WorkOrderCoordinator.cs`）。
- 不要讓控制器宣告回傳父型別而實際回子型別：JSON 依**宣告型別**序列化，子型別多出來的欄位會被靜默吃掉，
  服務層測試照樣全綠（依問題端點的去重主機數曾因此消失多輪）。宣告型別寫成實際型別，必要時補序列化守門測試。
- 不要讓 PRTG 取數對整台 PRTG 查：感測器、狀態變更、快照一律只處理 `PrtgScopeDevices.Compute` 的取數範圍（PRTG-SPEC §3c）。
  實機 `messages&id=0` 的 treesize 封頂在 100 萬、連 `count=5` 都逾時；只有裝置鏡像是全站（主機對應要用全部 IP）。
  新增 PRTG 查詢路徑時先問「範圍從哪來」，範圍參數寫成必填——可選的話漏接的呼叫端會靜默退回全站。
- 不要用 `GetLatestHostMapWithDate(…, anchor)` 取「過去某日用哪一份對應」：一律走 `EfPrtgStore.GetHostMapForDay`（只採含 `ok` 列的整份對應，PRTG-SPEC §4）。
  全是 unmatched 的一份代表那次對應沒成功，選中它該日就永遠歸不了戶；逐 device 的選法（數值評估）要先以 `GetOkHostMapDates` 排除這種整份對應，
  規則歸戶與數值評估才會看到同一份。「最近一次對應」類讀取（維護頁、取數範圍、快照目標）不適用，它們要看到最新一份的真實狀態。
- 不要讓外部輪詢無上限等待：每次 HTTP 請求要有逾時，分頁／批次迴圈要有收斂條件與上限，
  並接受取消訊號；停止鈕必須能讓同步、回填與探測離開等待。長時間同步不能靠移除整段逾時
  來假裝可靠。
- 不要把處理人畫面寫成管理者案件畫面：處理人側使用「我的交辦」、「交辦單」、「主機」與「狀態」；
  「案件」只留在管理與進階說明。相關導覽名稱以 `layout.js` 與 View 的實際顯示文字校對，
  manifest 只是待比對資料，不是導覽名稱的來源。
- 不要替 PRTG 數值再加閘門：數值可信＝三條件（該日對應有效、可用列、單位可判，`PrtgValueAssessment` 是唯一評估入口，PRTG-SPEC §9b）。
  不足就顯示原因（`PrtgValueReason`），不要另造證據、資格或授權層——那是已整批移除的形狀（背景見 `docs/archive/README.md`）。可用 % 轉已用 % 只在 `PrtgValueAssessment.UsedPercent` 一處（趨勢評估與持續 Warning 的持平判定共用）。
- 不要在不可信範圍做清除：外部查詢未收斂、回傳空資料、權限縮小、範圍異常縮小、沒有成功對應
  或沒有成功寫入時，不得清除 PRTG 鏡像、狀態或數值資料；資源守門使用的裝置／sensor 需保留。
- 不要把執行總表的列當成執行日：`RunDaySummaryDto.Date`、`GET api/runs/day/{date}`、診斷匯出 `dailyHealth.date` 都是**資料日**（事件發生的那一天），處理日在 `RunDate`。
  換算只在 `RunMonitorService.GetDaySummaries`／`GetDayDetailPage`（內部以處理日計算、輸出標資料日）；新消費端要執行日就讀 `RunDate`，不要自己加減一天。
- 不要讓排程觸發判定各寫一套：所有排程觸發查詢走單一查詢點，明確排除 `BatchRun.JobType == ai`；
  AI 背景工作不算取數排程的新鮮度，也不能被用來補足排程成功判定。
- 不要把外部系統回傳的字串直接寫進有長度上限的欄位——一律依 `HasMaxLength` 截斷：經 `SaveChanges` 的由 `LfDbContext.PrepareForSave` 統一處理（鍵／索引欄超長該筆不寫），`ExecuteUpdate`／原生 SQL 繞過它，要在寫入處呼叫 `ColumnClamp.ClampFor`（同一個代理對安全的 `TextTruncation.SafeCut`），不寫字面值長度。
  外部來源（PRTG、NetIQ…）沒有長度保證，超長在 SQL Server 端會讓整批寫入一起擲截斷例外、
  SQLite 端卻靜默通過——兩個後端行為分岔，測試環境永遠看不到，正式機才爆。
- 不要另 new 一個不帶閘道的 `AIService`：所有 AI 呼叫一律經全站 `IAiRequestGate`（互動優先、背景上限隨 `AiConcurrency`），
  `AIService` 的建構子都把閘道列為必填，漏帶就編譯不過。也不要讓背景排程整輪只建一個實例給所有主機分片共用：
  `AIService` 內有逐實例的請求佇列，共用一個等於把全輪串成單線、`AiConcurrency` 形同失效（設定一輪只解析一次即可，實例每分片一個）。
  AI 待補順序與閒置原因各只有一份判定（`SelectNextBatch`、`BlockingIdleReason`），不要在控制器或畫面再算一次。
- 不要在測試 `new PrtgRequestBudget()`：真時鐘的配額會讓全量並行的測試彼此排隊到逾時，一律 `new PrtgRequestBudget(new AcceleratedPrtgClock())`（`PrtgRequestBudget.Shared` 在測試組件載入時已換成加速時鐘）。也不要讓並行寫入共用 `EfSqliteFixture` 那一條記憶體連線，並行案例用檔案型 DB。
- 不要以「逐行找到標籤、取到行尾」擷取 Windows 事件欄位：NetIQ／Sentinel 的訊息主體是單行（欄位間是連續空白、同一個標籤會出現多次），
  整段訊息會被當成值寫進帳號集合與登入失敗明細。標籤值一律走 `LogAggregator` 的擷取 helper（邊界規則見 DETECTION-SPEC「標籤值擷取邊界」）。
- 不要讓工作流父指紋（`HostDayWorkflowFingerprint.ForParentRecord`）吃到會隨儲存形狀改變的內容：分析當下以記憶體的完整紀錄算、AI 排程與工作流復原以資料庫讀回的紀錄再算，
  兩次不同就會把已完成的 PRTG、AI、郵件腿當成「父紀錄變了」重設，主機日停在「PRTG 待補」。指紋一律以 `RecordStorageShaper.Compact` 的投影計算、與風險等級無關
  （低風險日寫入時會精簡，風險等級又會在寫入後跨界），也不含 NetIQ 嘗試狀態與時間（帳本會在批次結束時回寫）；改精簡投影的欄位或指紋內容時，還原路徑要能認得先前各版算法（`LegacyParentFingerprints` 的做法），否則升級當下全部既有列重設一次。
- 不要讓郵件去重鍵吃到與信件內容無關的狀態：`WorkflowMailIntent` 一律用 `HostDayWorkflowFingerprint.ForMailContent`（AI 已分析／AI 待補／詳情已清除固定為 false），改回 `ForRecord` 就是 AI 寫回後重寄、或留下永遠待送的即時通知項目讓重試空轉。
  即時通知待送項目的終態只在 `MailNotifyStateStore.IsTerminalUrgentStatus` 判定；標成 `superseded` 只限「鍵已不對應本趟候選且該主機日取得到工作流版本」，停用即時通知時不標（重試入口改為不重跑），否則重新啟用後要等下一趟取數才補寄。
- 不要讓快取鍵的可見主機片段記憶可變集合：`VisibleHostKeyPart` 只對 `FrozenSet<long>` 以物件參考記憶（`VisibilityService.GetVisibleHostIds` 回傳它）；`HashSet` 雖實作 `IReadOnlySet` 但可被修改，這段字串是授權維度。
- 不要讓測試的預期摘要（golden）依賴真實的「今天」：摘要裡含日期（意圖鍵、主旨）時要用固定時鐘與固定日期，否則只在錄製當天會過。換行同理：摘要由 `AppendLine`、縮排 JSON 或郵件本文組成時，比對前把 CRLF 正規化成 LF，否則只在錄製的那個平台會過。
- 不要在別處寫 NetIQ「要不要重查」的狀態清單：判定只在 `NetiqAttemptStatus`（`NetiqAttemptStatusGuardTests` 守門）。帳本每個主機日只留一列，不能靠數列計次，累計隨回執寫入。
- 不要在交辦、靜音、觀察中的路徑直接讀系統時鐘：「現在／今天」一律走注入的 `TimeProvider`（`Core/Time/ClockExtensions`，store 取 `StorageBackend.Clock`），
  管線呼叫掛接與夜間派工時傳 `backend.Clock`——直接寫 `DateTime.Now` 的程式在情境測試撥動時鐘時會用到真實時間，到期、迄日這類行為就測不到（範圍清單與呼叫點由 `HandoffClockGuardTests` 守門）。
- 不要把例外的 `.Message` 放進回應、DTO 或維護主控台：一律 `UserFacingMessage.For(ex, 備用說明)`，原文只進記錄檔。交辦協調器給使用者看的規則違反擲 `WorkOrderRuleException`，
  新的專屬例外型別要同時登錄 `UserFacingMessage` 並在 `ApiExceptionFilter` 加對應（`UserFacingMessageGuardTests`、`ApiExceptionFilterTests` 守門）——
  框架層例外（EF／LINQ）的英文原文含實體名與路徑，轉成領域例外就是外洩。
- 不要另寫一份「系統要不要注意」的判斷：健康整體狀態只由 `HealthAttention.Evaluate` 決定，健康頁與導覽告示共用；健康頁新增警示卡要宣告 `data-health-alert` 並併入原因清單（`HealthAttentionGuardTests` 守門）。
- 不要另寫「不再打擾」的判定或不處理類狀態清單：閘門只在 `DispatchContext.DismissedCases`（最近一筆未取消案件、不處理類、`BlocksRedispatch` 不為 false），狀態清單只在 `IssueHandlingStatuses.Dismissals`；
  資料保留清除（`EfIssueCaseStore.Expired`）依同一定義保留仍有效的案件——兩邊定義分岔，處理人判定的不處理就會在保留期後悄悄失效。
- 不要拿版本號判斷站台部署了哪個 commit：版本號在同一個修訂內不隨 commit 變，一律看 `BuildVersion.Current.Label`（`1.0.70.0+短 sha`，執行紀錄、匯出與健康頁都是它）。
- 不要在 action 內手寫「全站檢視」判斷：全站 PRTG 對應類端點一律貼 `[RequireViewAll]`；新增或搬動控制器後 `ControllerRouteSnapshotTests` 必須綠，基準檔只在刻意改權限時以 `LF_UPDATE_ROUTE_SNAPSHOT=1` 重產並逐列檢查差異——漏一行檢查就是把全站主機身分給了受限角色。
- 不要用 `IPAddress.TryParse`／`Parse` 判斷「是不是 IP」：它把 `10.1`、`12345` 解成別台主機的位址；一律走 `IpSyntax`。由編譯器以符號比對禁用（根目錄 `BannedSymbols.txt`，`RS0030` 是建置錯誤），合法使用處以 `[SuppressMessage]` 附理由放行。
- 不要在 `Fail(`／`DomainException(` 傳錯誤碼字面：新碼先登錄到 `ApiErrorCodes`（常數＋狀態碼表），WEB-SPEC §7.2 同步。
  Core 擲給 Web 需要特定狀態碼的錯誤，用專屬例外型別交給 `ApiExceptionFilter` 對應（如 `ConcurrentUpdateException`、`PrtgScopePurgeBusyException`），不要在控制器比對例外訊息字串。
- 不要拿 `FileShare.Read` 當跨行程鎖：Unix 上 .NET 以 `flock` 模擬共用模式，只有 `FileShare.None` 是獨佔，其他值兩個持有者都會成功，Windows 上測不出來。
- 不要對測試的 `HttpListener` 呼叫 `Stop`：Unix 上 `Dispose` 會重新綁 port，被平行測試占走就擲例外，Windows 測不出來；一律用 `Close`（`LogForesight.Tests/BannedSymbols.txt` 禁用）。
- 不要在測試傳 node 實驗旗標：會隨大版本移除，一移除就是整批行為測試同時紅（`NodeRuntimeTests` 守門）。版本下限只在 `NodeRuntime.MinimumVersion` 判定。
- 不要在正式碼用 `System.Diagnostics.EventLogEntryType`：它的組件標 Windows 專屬，一律用 `LogEntryType`，只有 `EventRecordMapper.FromClassic` 是轉換點（`BannedSymbols.txt` 禁用）。呼叫 Windows 專屬 API 要有平台守門；`CA1416` 在 `Directory.Build.props` 是建置錯誤，不得用 `NoWarn`／`#pragma` 壓掉。
- 不要在 `showDetailModal` 開著時再開一個：Bootstrap 5.3 不支援疊開，內層關閉後背景可捲動、外層焦點鎖定失效。已在 modal 內的次要選擇用就地面板（樣板：`chat-panel.js` 的「指定問題」）。
- 詢問 AI 的焦點只由 `ChatFocusResolver` 決定（確定性規則、整詞比對、不呼叫 AI 做路由），整日摘要只取自 `GetDetail` 回傳的 DTO——另讀紀錄會把案件授與檢視範圍外的整日敘事帶進 prompt。
- 不要在頁面現場拼文字樣式、按鈕類別，也不要寫設計系統外的中斷點與按鈕變體：`text-danger|text-warning|text-success`、`small text-muted`、`btn-link`、
  頁面模組手寫 `'btn btn-…'` 全站禁止（`DesignSystemGuardUiTests`）。規則在 DESIGN-SYSTEM §8.1～§8.4、共用出口在 §8.6；
  同一結構在三個以上頁面出現才抽進 `core/`，抽了就刪掉各頁那份。
- 不要讓 `lf-text-data` 之類的短值在字中斷行：`overflow-wrap: anywhere` 會把表格欄最小寬度算成一個字（IP、日期逐字直排）；需要任意斷行的容器才用 `lf-break-anywhere`。
- 改 markup 的類別名之前，grep 全前端的 `querySelector`／`closest`：別的模組用舊類別找元素時不會報錯，只會靜默找不到
  （問題格標題改用角色類別後，處理面板的 AI 備註拿不到問題名稱）。
- 窄螢幕隱藏欄位、或把 toast 改成常駐訊息時各問一句：被藏的欄裡有沒有操作（連結、編輯鈕）要在摘要行保留；常駐訊息什麼時候清掉
  （「已儲存設定」不能掛在已經又改過的表單上）。
- 不要新增不帶 nonce 的 inline script：CSP 只放行每請求 nonce，漏帶的腳本在正式環境被瀏覽器擋掉（`CspPolicyGuardTests` 掃所有 view）。
  同理不要在 view 或 JS 字串寫 `style=` 屬性與 inline 事件屬性；動態樣式用 `classList` 或 `style.setProperty`。
- 不要新增 SQL 指令字串串接：值一律走參數，表名等常數插入要在使用處以 `[SuppressMessage("Security", "CA2100", Justification = …)]` 附理由。`CA2100`／`EF1002`／`EF1003`／`CA5394`／`CA1310` 在根目錄 `.editorconfig` 是建置錯誤；新規則升成錯誤前先確認零命中。
- 不要在 `runtimeconfig.template.json` 加白名單外的 `System.GC.*` 鍵：runtime 對認不得的鍵靜默忽略，錯鍵看起來有設定、實際沒生效（`System.GC.Conserve` 沉默了 32 輪）；鍵名進 `RuntimeConfigGuardTests` 白名單，生效值由啟動 log 與健康頁記憶體卡用 `GC.GetConfigurationVariables()` 讀回。讀回時 `GCHeapHardLimitPercent` 未設是值 0 不是缺鍵，判斷有無硬上限要看「值 > 0」。
- 不要用「是不是 `OperationCanceledException`」判斷使用者取消：HttpClient 逾時擲的 `TaskCanceledException` 也是 OCE，只有「OCE 且 ct 已取消」才是取消（樣板見 `PrtgScopeEstimateController`）。
- 新增 `*.behavior.mjs` 案例要同時在 C# `InlineData` 登錄，前端行為測試一律經 `NodeRuntime.RunOrFail` 執行（`NodeRuntimeTests` 守門兩件事）。
- K 的來源鍵就緒閘門必須分開：`IIssueAggregateQuery` 等 `lf_top_issues.source_key` 回填與
  `lf_issue_first_seen` 舊鍵重整完成；`IRiskyEventStore` 只等 `lf_risky_events.source_key`
  回填完成。後者在前者回填後開始，但不是聯合讀取閘門；每批 500 列。啟動快判（`TopIssueBackfiller.TryFastReady`）只針對前者、在進背景閘門前以索引可 seek 的 EXISTS 判定；後者以版本化 blob `risky_events_source_key_done` 短路，不得改回每次啟動 `Count()` 全表。就緒旗標只升不降。索引完整鍵序列是
  top `(source_key, event_id, record_date)`、risky `(host_id, date, source_key, event_id)`。
