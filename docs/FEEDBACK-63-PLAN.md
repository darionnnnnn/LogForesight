# LogForesight 第 63 輪規劃：排程作業的診斷資料匯出（給 AI 分析）

> 狀態：實作中（`feature/feedback-63`）
> 基準：dev@6ab5480d（7,624 通過／10 略過，Windows）
> 來源：使用者 2026-11-03 需求——把執行紀錄與異常彙總匯出給 AI，找出專案尚未完善的細節
> 實作方式：主代理寫規格與驗收；改檔代理依複雜度選 Sonnet 5.5 或 Haiku 5.5；同一 checkout 一次一個改檔代理；主代理獨立重驗
> 設計面：套用 `ui-ux-pro-max`（§批次D 列出採用的指引），沿用 Bootstrap 與 `docs/DESIGN-SYSTEM.md`
> 資料量限制：本機沒有資料庫，下文的量級都來自程式上限，不是實測；**正式站台第一次匯出的檔案大小與耗時要回頭核對「暫定」數值**

## 0. 總結與定案

| 使用者定案 | 落實方式 |
|---|---|
| 匯出精簡版，不要一堆重複錯誤 | **只有一種格式**：同類訊息收斂成一組，附次數、趨勢與最多 3 個樣本；不提供含全部輸出的完整模式 |
| 由主代理評估什麼資訊值得分析 | 見 §1 的取捨表，以「AI 能回答什麼問題」決定留與不留 |
| 去識別化 | **一律去識別化，無開關**；認不出是否已遮乾淨時整份匯出失敗，不給部分檔案（批次B） |
| 加上背景資訊 | 白名單設定快照與每日狀態計數（批次C） |
| 套用 `ui-ux-pro-max` | 批次D |
| 「AI 回覆格式異常」也要匯出，分析之後如何避免 | 批次F 補上現有紀錄缺的證據，批次C 匯出案例與對照組；**本輪只蒐集證據，不改提示詞與判定**（§批次F） |

使用者於 2026-11-03 確認照規劃實作：Warn 彙總上畫面（批次A-4 等級篩選，預設不變）、異常案例匯出遮罩後的事件文字（批次F）。

評估中發現並納入本輪的問題：

| 編號 | 問題 | 根因 | 批次 |
|---|---|---|---|
| B1 | 說明書寫「同一個錯誤出現在幾台主機」，實際欄位是執行站台，判斷不了個案或通案 | `AffectedHosts` 來源是 `BatchRun.HostName`；另外分組時數字被正規化，樣本也只留一則 | A-3、E |
| B2 | 跨日長跑的趟次，開始時間早於期間起點時，站台顯示「（未知）」 | 錯誤依 `LoggedAt` 篩、趟次依 `StartedAt` 篩，兩邊範圍不一致 | A-2 |
| B3 | 異常中斷或失敗但沒有 Error 行的趟次不出現在異常彙總，空狀態文字讓人以為都正常 | 彙總的資料來源只有診斷行，沒有看趟次狀態 | A-3 |
| B4 | Warn 沒有彙總，畫面只有計數 | `GetRecentErrors` 只取 Error／Fatal | A-4、C |
| B5 | 每次載入或切換天數都把期間內所有診斷行（含 Output）讀進記憶體解析，再篩 Error | `EfJsonLogStore.ReadLines` 先 `ToList`，level 在 JSON 內無法下推 | A-1 |
| B6 | 診斷行沒有統一的機敏值遮罩；`Output` 也可能直接寫入完整例外 | 遮罩散在 `PrtgClient.StripSecrets` 與 `UrlSecrets`；**目前沒看到實際外洩，只是沒有保證** | B |
| B7 | 規格說 serverAdmin 沒有業務資料檢視，但它能在執行詳情看到輸出內容 | 先前為了佐證排程活著而刻意開放 | 記進 BACKLOG，本輪不改（§明確不做） |
| B8 | 共用下載函式的錯誤訊息寫死「證據檔」 | `readJsonDownload` 當初只服務 PRTG 證據檔 | D-4 |
| B9 | 「AI 回覆格式異常」混了解析失敗與內容檢查失敗，降級的 log 也沒寫原因，事後分不出是哪一種 | `LogAnalysisService.cs:687-696` 只看 `RawContent` 有沒有值，沒帶 `result.Error` | F |
| B10 | 查不出是否被 `max_tokens` 截斷，也對不回是哪台主機 | `OpenAIChoice` 沒讀 `finish_reason`；`AIService` 的診斷 log 沒有主機資訊 | F |

### 額外發現：問題訊號不只在 Warn

`Output`（`IRunConsole` 的白話訊息）固定 `Level=Info`，但 `Core/Service`、`Core/Analysis` 有 117 行含「⚠／✗／失敗／錯誤」的輸出，例如 NetIQ 批次查詢失敗、查詢結果被截斷、事件解析略過、權限不足的偵測盲區。**只匯出 Warn 以上會漏掉這些**，它們正是「尚未完善的細節」，所以批次C 要把它們收進訊號組。

不改寫入端結構化的理由：保留期內 120 天的舊資料無法回溯，而且改動面涵蓋上百個呼叫點。這裡依「行首符號」篩選是**只影響匯出多寡、不影響任何判定**的啟發式，與「不得用訊息內容做判定」的紅線不衝突；缺點是改文案會少抓，由批次C 的真實樣本測試守住。

## 1. 匯出內容評估

判準：**這份資料能幫 AI 回答哪個問題？** 回答不了、或與另一區重複的就不匯出。

| AI 要回答的問題 | 匯出的資料 | 為什麼需要 |
|---|---|---|
| 什麼問題反覆發生 | 訊號組：次數、涉及趟數、首末次、逐日次數 | 收斂重複，同時保留頻率 |
| 問題出在哪一段 | 訊號組的 logger、作業類型、發生階段 | 階段由 `LoggedAt` 對照該趟 `Phases` 推得，直接指向模組 |
| 是不是某版之後才發生 | 訊號組的逐版本次數、各版本趟數 | 區分退化與長期問題 |
| 根因是什麼 | 代表堆疊（去重，只留專案框架） | 樣本訊息看不到呼叫路徑 |
| 哪些趟次沒跑完 | 異常趟清單：狀態、結束代碼、中斷與停止、未成功階段 | 中斷趟可能沒有任何 Error 行（B3） |
| 哪裡慢 | 各階段耗時統計、慢 SQL 依操作彙總 | 效能問題不會以錯誤呈現 |
| 該跑沒跑 | 每日狀態計數、PRTG 逐日結局與原因 | 「沒跑」不會留下任何執行紀錄 |
| AI 分析是否穩定 | 呼叫數、失敗率、契約失敗組 | 判斷模型或 prompt 的問題 |
| 什麼內容會讓 AI 回覆格式異常 | 異常案例的回覆、失敗類別、輸入內容與特徵，加上同主機正常日的對照 | 沒有對照組，就分不出是內容造成還是模型本來就不穩 |
| 這是設定造成的嗎 | 排程與模組設定白名單 | 否則 AI 會把設定行為誤判成缺陷 |

### 不匯出

| 資料 | 理由 |
|---|---|
| 正常趟次的逐筆紀錄 | 只有統計意義，已進 `runStats` 與 `dailyHealth` |
| 一般 `Output` 行與里程碑 | 量最大（每趟最多約 2,000 行）且多為進度文字，也是事件內容外流的主要路徑 |
| 分析結果類輸出（高風險事件框、「頻率異常／慢速惡化」） | 那是**被監控主機**的狀況，不是專案缺陷 |
| 重複錯誤的每一則 | 改由次數與趨勢表達 |
| 失敗主機名單、帳號、顯示名稱 | 對找缺陷沒有幫助；台數保留 |
| 訊號組裡的 AI 回覆片段（`AIService.JsonContractError` 之後的 300 字） | 同一則回覆已進 `aiFormatAnomalies`，不重複；訊號組只留「有片段／空回覆」與長度 |
| 送給 AI 的 prompt 全文 | 主要是系統提示與歷史摘要；異常案例的問題清單已足以比對內容，要重現單一案例可用既有的「AI 診斷傾印」 |
| 堆疊的框架層與檔案路徑 | 框架層改成略過行數；路徑含建置環境資訊，只留檔名與行號 |

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 逐行讀取與異常彙總修正（B2、B3、B4、B5、B1 的趟數） | 中 | 無 |
| B | 去識別化服務與機敏值遮罩（B6） | 大 | 無 |
| F | AI 格式異常的證據蒐集（B9、B10） | 中 | 無 |
| C | 匯出內容組裝與 API | 大 | A、B、F |
| D | 排程作業頁 UI（含 B8） | 小 | A、C |
| E | 文件與說明書（含 B1 文字、B7 留痕） | 小 | A～D、F |

建議順序：A → B → F → C → D → E（批次F 的編號在後，是因為它是後來加入的項目）。每批驗收通過後 commit 並 push 到 `feature/feedback-63`。

## 批次A：逐行讀取與異常彙總修正

### 現況與核對結果

- `BatchRunStore.cs:420` `GetRecentErrors` 經 `ReadLogs`（`EfJsonLogStore.cs:233` `ToList`）載入全部診斷行後才篩 level。BACKLOG 只記了階段快照放大，沒有記這點。
- `RunMonitorService.cs:470-493` 聚合，`RunsController.cs:82` 是唯一入口，`runs.js:161` 是唯一呼叫端；`LogForesight.Tests` 目前**沒有** `GetErrorSummary` 的直接測試，改回傳形狀不會打破既有測試，但也沒有保護，要補。
- `runs.js:454-466` 異常彙總表用 `sortRows` 本地排序。

### 定案

- 不用 SQL `LIKE` 比對 JSON 文字篩 level：序列化大小寫與格式會靜默決定結果，且只省解析時間；要解決的是記憶體峰值。
- 回傳形狀改成物件，不另開端點，避免前端多發一個請求。

### 改動

- **A-1 逐行讀取**：`EfJsonLogStore` 新增不緩衝的日期範圍讀取（沿用 `ApplyDateRange`、`Measure`、依 `Seq` 排序）；`BatchRunStore` 新增依日期範圍逐筆處理診斷行的方法，單行損毀照舊跳過。`GetRecentErrors` 改走這條路徑，結果與改前相同。
- **A-2 跨窗口趟次**：解析站台時，期間內診斷行所屬的趟次都要找得到，不因開始時間早於期間起點而變「（未知）」；只有趟次已被保留期清除才顯示「（未知）」。查找不得逐筆呼叫 `GetRun`。
- **A-3 彙總補欄位**：
  - 每組新增**影響趟數**（去重 RunId 數）。
  - `/api/runs/errors` 回傳改為物件：組清單，加上「中斷或失敗但沒有錯誤紀錄的趟數」。
  - **什麼算一趟**：`BatchRunStatus.Compute` 為 `failed` 或 `stuck`，且 `ErrorCount == 0`；`running`、`stopped`、`warning` 不算。沒有這種趟次時數值為 0，畫面不顯示。
- **A-4 等級篩選**：`GET /api/runs/errors` 新增 `minLevel`，值為 `error`（預設）或 `warn`；不認得的值回驗證錯誤。Warn 組同時納入 `SqlPerformanceMonitor`，並標示「不計入警告數」。
- **A-5 正規化共用**：`NormalizeMessage` 抽成畫面與匯出共用的唯一實作（批次C 消費）。

### 測試 / 驗收

- `RunMonitorServiceTests` 補：同訊息不同數字併組、影響趟數去重、跨期間起點的趟次解得出站台、趟次已清除時顯示「（未知）」、0 趟與有趟的中斷計數、`minLevel` 預設值與非法值。
- `BatchRunStoreSqlPushdownTests` 補：逐行方法與舊方法結果相同（含損毀行、空範圍）、不呼叫緩衝版讀取（以替身確認）。
- 突變：把趟數改成次數、中斷計數拿掉 `ErrorCount == 0`，測試都要紅。
- 定向篩選：`FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~BatchRunStoreSqlPushdownTests`（前端部分在批次D）。

## 批次B：去識別化服務

### 現況與核對結果

- 需遮罩的值來源（都已核對欄位存在）：
  - `WebHost`：`HostName`、`DisplayName`、`IpAddress`、`NetiqServer`、`OrphanedFromSentinel`、`RoleDesc`；`HostGroup.GroupName`
  - `WebUser`：`Account`、`DisplayName`、`Email`
  - `Sentinel`：`Name`、`BaseUrl`、`Username`
  - `PrtgDeviceRow`：`Name`、`Ip`、`GroupPath`
  - `SystemSettings`：`BrandName`、`ServerDescription`、各服務位址
- `Output` 實際含 Sentinel 名稱、主機名、IP（`NetiqPipelineService.cs:306,332,821`），例外可能含路徑與位址。
- 全站沒有現成的去識別化工具；`IpSyntax` 是判斷 IP 的唯一入口（不得用 `IPAddress.TryParse`）。
- `IUserStore`、`IHostStore` 在 Web 可用，放 `LogForesight.Web/Services`，不把身分規則帶進 Core。

### 定案

- **一律去識別化，無開關**：沒有開關就不會有誤傳原文的路徑。
- **檔案內不放對照表，也不另外提供**：對照表等於把原值和代號放在一起。AI 的結論可用保留的 `runId` 與時間回到排程作業頁核對。
- **代號只在單一檔案內穩定**（依首次出現配發，不用雜湊）：雜湊可被字典反推。
- **主機的 IP 對應到同一個主機代號**：AI 才看得出「名稱與 IP 是同一台」。
- **機敏值比對不能只靠參數名**：除了 `UrlSecrets.Mask`，再比對已設定的密鑰實際值。

### 改動

- **B-1 遮罩順序（契約）**：密鑰 → URL → 已知字典（長字串優先）→ 樣式比對。順序不可對調，否則短字串會先切壞長字串。
- **B-2 字典比對**：
  - 不分大小寫；邊界為「前後不是英數、`-`、`_`、`.`」，中文相鄰視為邊界。
  - 空值與僅空白不進字典。
  - 群組路徑（`GroupPath`）切段後逐段比對。
- **B-3 樣式比對**：IPv4（經 `IpSyntax` 驗證）、IPv6、Email、URL、UNC（`\\server\share`）、`DOMAIN\user`、`C:\Users\<name>`、依頂級網域清單判定的 FQDN。因為批次F 會匯出事件訊息，另加事件常見的身分格式：SID（`S-1-5-…`）、`Account Name:`／`帳戶名稱:`、`user=`、`for invalid user`。
- **B-4 代號格式**：`host-001`、`user-001`、`sentinel-001`、`device-001`、`group-001`、`ip-001`、`url-001`、`domain-001`、`secret`。
- **B-5 堆疊處理**：`at Namespace.Type.Method(...)` 保留；`in <路徑>:line N` 只留檔名與行號；連續框架層（`System.*`、`Microsoft.*`）收成「略過 N 層」；其他行走完整遮罩。
- **B-6 結果掃描（失敗即停）**：序列化完成後，對**整份 JSON** 掃字典原值、IPv4、Email；有殘留就中止匯出、不給檔案，回新錯誤碼 `export_deidentification_failed`（登錄 `ApiErrorCodes`，同步 WEB-SPEC §7.2）。伺服器 log 只記類別與次數，不記原值。
- **B-7 字典來源失敗即停**：任一來源讀取失敗就中止，不以殘缺字典繼續。PRTG 未啟用或鏡像為空屬正常，不算失敗。

### 容易誤判的反例（必須進驗收）

| 情境 | 期望 |
|---|---|
| `System.Net.Http.HttpClient`、`LogForesight.Core.Service.X` | 不被當成 FQDN |
| 帳號 `dev` 與文字 `device`、`dev-server` | `device` 不動；`dev-server` 整段不被切開 |
| 主機 `SRV01` 與 `SRV010` | 各自對應，不互相截斷 |
| 主機名是 `AI`、`PRTG`、`web` 這類常見詞 | 保留清單（暫定：`AI`、`PRTG`、`NetIQ`、`Sentinel`）不遮，其餘寧可多遮 |
| 版本字串 `8.0.12.1`、日期 `2026.11.03` | 四段版本號會被當 IP 遮掉，接受（只在文字內；`appVersion` 欄位不走文字遮罩）；三段日期不動 |
| 中文緊鄰主機名：`主機SRV01失敗` | 遮成 `主機host-001失敗` |
| 字典為空（全新站台） | 仍能匯出，樣式比對照常生效 |
| 事件訊息 `Account Name: alice`、`S-1-5-21-…` | 帳號與 SID 被遮，欄位名保留（帳號不在 `WebUser` 也要遮得掉） |
| 保留詞與結果掃描 | 保留清單不算殘留，不得讓匯出失敗 |

### 測試 / 驗收

- `RunExportDeidentifierTests`（新增）：上表每條反例；每種來源欄位各一個含原值的樣本，輸出不得含原值；同值同代號、不同值不同代號；IP 對應到主機代號；密鑰實際值（不在 URL 內）被遮；B-6 掃描以刻意留原值的輸入觸發失敗；字典來源丟例外時中止。
- 規模：2,000 台主機字典、1 萬則訊息在 5 秒內（暫定），超過要回報並調整做法，不靠拿掉字典項。**這是唯一接受的效能基準，放在略過類 `LF_SCALE_BENCH`，不進一般全量**。
- 突變：對調 B-1 順序、拿掉 B-6 掃描、拿掉邊界判斷，測試都要紅。
- 定向篩選：`FullyQualifiedName~RunExportDeidentifierTests`。

## 批次C：匯出內容組裝與 API

### 現況與核對結果

- `CalibrationController` 的單一併發、有界輸出、稽核是既有樣板；前端 `api.download` 只接受 `application/json`，上限 64 MiB。
- `GetDaySummaries` 的分頁上限 90 只在控制器；服務層可以一次取完整天數。總表**不含今天**，訊號組與趟次**含今天**。
- `GetLogs` 逐趟呼叫會重複讀全表，不可用；走 A-1。
- `ApiErrorCodes.CaptureBusy`（503）與 `CapacityExceeded`（413）可沿用。
- `RunsController` 類別層權限是 `DevMonitor|Maintain`，`[RequireViewAll]` 再收窄：Dev、Admin 可匯出，serverAdmin 回 403。

### 定案

- 端點 `GET /api/runs/export?days=N`，天數上限與畫面同為 `MaxRunDays()`。
- 檔名 `run-diagnostics-yyyyMMdd-{N}d.json`，UTF-8、不轉義中文。
- **不新增任何設定鍵**：上限都是常數，符合「新增設定必須有消費端」紅線。
- 同一時間只允許一個匯出；不與校準共用閘門（兩者互不相關）。

### 檔案結構（schemaVersion 1，欄位名暫定、語意是契約）

| 區塊 | 內容 |
|---|---|
| `meta` | `schemaVersion`、匯出時間與 UTC 位移、目前版本、涵蓋起訖、天數、保留天數、`hasData`、去識別化摘要（各類代號數與「自由文字無法保證完全遮除」的提醒）、`omitted`（每一處省略的項目與數量） |
| `guide` | 給 AI 的分析提示與名詞解釋：狀態語意、null 與 0 的差別、D 與 D-1 的日期對應、總表不含今天、執行站台不等於被分析主機、慢 SQL 不計警告 |
| `context` | 白名單：排程啟用、窗口、AI 窗口、AI 並行數、本機分析、自動補跑、診斷傾印；PRTG 啟用與策略；AI 供應者類型、模型、逾時與重試；主機數（依來源）；保留天數 |
| `dailyHealth` | 逐日：各狀態台數、PRTG 結局、finding 數、原因（已遮罩）、當日總耗時；**不含主機名單** |
| `runStats` | 依作業類型與觸發類別（排程／手動／跟隨／舊紀錄）：趟數、各狀態數、AI 呼叫與失敗；各階段耗時（次數、中位數、P95、最大）與未成功次數；依版本：趟數、首末次、警告與錯誤數 |
| `signalGroups` | 每組：代碼（`S001` 起）、等級、來源（log 或 output）、logger、涉及階段、次數、涉及趟數、首末次、逐日次數（只列有發生的日子）、逐版本次數、最多 3 個不同樣本、代表堆疊、最近 3 個 `runId`、是否不計入警告 |
| `slowSql` | 依操作：次數、最大與平均毫秒；訊息解析不到時退回訊號組，不丟棄 |
| `abnormalRuns` | `failed`、`stuck`、`stopped`、`warning` 的趟次：`runId`、作業類型、觸發類別、是否補跑、起訖、耗時、狀態、結束代碼、分路結果、未成功階段、未成功的 PRTG 逐日、涉及的訊號代碼 |
| `aiFormatAnomalies` | 摘要、案例、對照組、歷次失敗統計（見 §批次F 的 F-4） |

### 改動

- **C-1 訊號收集**：
  - 收 Warn、Error、Fatal 全部，加上 `Output` 中行首（去空白後）為 `⚠` 或 `✗` 的行。
  - 排除分析結果類：行首為 `║` 的不會符合；「頻率異常／慢速惡化」明確排除。
  - **組鍵**＝遮罩後文字經 A-5 正規化 + logger；同組等級取最高。
  - **階段判定**：`LoggedAt` 落在該趟哪個階段的起訖內；落在多個取最內層子階段，都不符合記 `unknown`，趟次缺 `Phases`（舊紀錄）同樣記 `unknown`。
- **C-2 上限（暫定，超過要在 `meta.omitted` 寫明）**：
  - 訊號組 300 組，依等級、次數排序取前段；統計用的組字典 5,000 組，超過計入「其他」。
  - 樣本每則 500 字；代表堆疊 8 KB。
  - 異常趟 200 趟，`failed`／`stuck` 優先，再新到舊。
  - 檔案硬上限 16 MiB，超過回 413 `capacity_exceeded`，**不截斷**。以上限推算不應發生，發生代表上限算錯，要查。
- **C-3 分母為零**：期間內沒有任何趟次時仍回 200，`hasData=false`，各清單為空；P95 與平均在樣本為 0 時輸出 `null`，不輸出 0。
- **C-4 不變式**：訊號組中 `source=log` 的 Error＋Fatal 次數總和（含 `omitted` 內的筆數），必須等於同期 `/api/runs/errors` 的次數總和。**組數可以不同**：匯出先遮罩再分組，會把只差主機名的訊息併在一起。
- **C-5 進行中的趟次**：列入 `runStats` 的 `running`，不進 `abnormalRuns`；`guide` 說明這是匯出當下的快照。
- **C-6 稽核**：新增 `AuditActions.RunDiagnosticsExport`（`run_diagnostics_export`）與 `AuditQueryService.ActionNames` 中文對照（同 commit）。detail 只記天數、組數、趟數、位元組數與各類代號數，不記內容。稽核寫入失敗依 §11-4 不中斷，失敗路徑不寫成功稽核。
- **C-7 權限與併發**：`[RequireViewAll]`；`SemaphoreSlim` 取不到回 503 `capture_busy`；接受 `RequestAborted`，取消時不寫稽核；訊息不寫死「校準」。
- **C-9 AI 格式異常區塊**：依 F-4 組裝；`context` 另加 `AiMaxTokens`、`AiJsonRetryCount`，`guide` 寫明欄位長度上限（標題 60、其餘 600）與各失敗類別的意思；`meta` 標示 `includesEventText=true`。
- **C-8 路由快照**：`LF_UPDATE_ROUTE_SNAPSHOT=1` 重產 `controller-route-snapshot.txt`，只新增 export 一列，逐列確認差異。

### 測試 / 驗收

- `RunExportServiceTests`（新增）：
  - 重複錯誤 100 則收成 1 組且次數為 100；樣本不超過 3 且互不相同。
  - `⚠`、`✗` 的 Output 行進組，一般 Output 與 `║` 行、頻率異常行不進組；以 `NetiqPipelineService` 與 `AnalysisOrchestrator` 的實際文案當樣本。
  - 階段判定：在階段內、子階段內、階段外、舊紀錄缺 `Phases`。
  - C-4 不變式；C-3 空資料；上限截斷時 `omitted` 數字正確。
  - 正常趟不進 `abnormalRuns`，`warning` 與 `stuck` 會進；`stuck` 沒有 Error 行也會進（B3）。
  - 序列化後整份不含 fixture 內的主機、帳號、IP、密鑰原值（含 `aiFormatAnomalies` 的回覆與事件訊息）。
  - `AiJsonDiagnostics` 不進訊號組，C-4 不變式不受影響。
  - `context` 只有白名單：反射 `SystemSettings` 列出的密鑰欄位值不得出現。
- 控制器測試：serverAdmin 403、Dev 200、併發第二個 503、`days` 超限被夾住、回應 `application/json` 且 `Content-Disposition` 檔名正確、稽核一筆且不含原值、去識別化失敗時回錯誤且沒有檔案內容。
- `ControllerRouteSnapshotTests` 綠，差異只有一列。
- 突變：拿掉 C-1 的 `⚠` 判定、組鍵拿掉正規化、階段判定永遠回 `unknown`、拿掉 `[RequireViewAll]`，測試都要紅。
- 定向篩選：`FullyQualifiedName~RunExportServiceTests|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~ControllerRouteSnapshotTests`，加上控制器測試類別（實作後回頭確認命中數 > 0）。

## 批次D：排程作業頁 UI

### 現況與核對結果

- 工具列 `#runs-toolbar`（`Runs.cshtml:188`）在切到排程設定時整組隱藏，天數鈕由 `currentDays` 控制。
- 頁面主要按鈕已是「立即執行」，依 DESIGN-SYSTEM 每頁至多一個 `btn-primary`。
- `runs.js:824` 已取 `getCurrentUser`；`hasCapability(user, 'ViewAll')` 可用。
- CSP 禁止 inline `style=` 與事件屬性；`wwwroot/js/pages` 不得以含插值的樣板字串指派 `innerHTML`。

### 定案（`ui-ux-pro-max`）

採用的指引：判斷順序「可用性與語意 → 真實內容與響應式 → 一致性」、提交類動作要有明確回饋（ux #61）、窄螢幕表格維持可水平捲動的區域（ux #71）、WCAG 2.2 AA。檢索只有這三類相關，其餘結果離題未採用。

- **不做確認 modal**：匯出不會改變資料且有去識別化，多一層確認只會增加操作；說明放 `?` 提示。
- 按鈕放在**工具列天數鈕右側**：天數就是匯出範圍，位置本身說明了關聯；切到排程設定時隨工具列隱藏。

### 改動

- **D-1 匯出按鈕**：
  - `btn btn-sm btn-outline-secondary`，文字「匯出分析資料」，走 `icons.svg` 同一家族圖示；旁邊 `lf-help` 說明內容、已去識別化，以及「自由文字無法保證完全遮除，交給外部 AI 前請先瀏覽」。
  - 預設 `d-none`，有 `ViewAll` 才顯示。沒有此能力的帳號（serverAdmin）看不到，後端另外擋 403。
  - 點擊：`withBusy`（`匯出中…`，避免連按）→ `api.download`（`timeoutMs: 300000`，`silent: true`）→ `saveBlobAsFile` → 成功 `toast`。沒有資料時也下載，toast 說明期間內沒有執行紀錄。
  - 失敗以 toast 顯示伺服器訊息；503、413 與去識別化失敗的文案要能讓使用者知道下一步。
- **D-2 異常彙總**：
  - 卡片標題列加等級下拉（`Error 以上`、`Warn 以上`），附可見 label；換值只重取這一塊，用獨立序號守門，不重整三塊。
  - 新增「影響趟數」欄，可排序，在「次數」之後。
  - Warn 組的等級徽章用 warning 語意色；不計入警告的組標示「不計入警告數」。
  - Warn 空狀態文字另寫，不沿用「沒有 Error 或 Fatal」的句子。
- **D-3 無錯誤紀錄的趟次提示**：數量 > 0 時在表格上方顯示 `alert alert-secondary py-2 small`：「另有 N 趟中斷或失敗，但沒有錯誤紀錄。」並附切到執行紀錄頁籤的按鈕；數量為 0 不佔版面。空狀態與提示可以同時出現，空狀態文字改成不暗示「一切正常」。
- **D-4 下載文案（B8）**：`readJsonDownload` 訊息把「證據檔」改成「檔案」，同步 `JsBehavior/api-guard.behavior.mjs` 的斷言。
- 篩選與頁籤狀態：`runs.js` 的天數目前沒有同步 URL（只有頁籤 hash 與 `?run=` 深連結），等級下拉維持一致，不單獨同步 URL。

### 測試 / 驗收

- `RunsPageUiTests` 補：按鈕預設 `d-none`、等級下拉有 label、不含 `style=`。
- `*.behavior.mjs` 新案例（同步登錄 C# `InlineData`，mini-dom 替身補 `saveBlobAsFile` 等匯入）：有 `ViewAll` 才顯示、下載帶目前天數、切 `all` 時帶後端最大天數、下載失敗有 toast 且按鈕復原、N=0 不顯示提示、N>0 顯示、換等級只重取一塊且舊回應不覆寫。
- 瀏覽器實測（`preview_start`）：375px 與桌面寬度工具列換行正常、鍵盤可操作、焦點可見、Warn 長訊息與長中文不撐破表格、CSP 無違規；實際下載一份，人工瀏覽內容。**本機沒有資料時先以開發資料或測試種子驗，正式站台第一份由使用者回報大小。**
- 定向篩選：`FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~CspPolicyGuardTests`。

## 批次F：AI 格式異常的證據蒐集

### 現況與核對結果

- 畫面上的「AI 回覆格式異常，以下為原始內容」只有一個來源：`LogAnalysisService.cs:694`，條件是 `ChatJsonAsync` 失敗但有回覆內容（`:687`）。
- **它其實包含兩種失敗**：
  - 解析失敗：`AiJson.TryParse` 回 null（`AIService.cs:407`）。
  - 內容檢查失敗：JSON 合法，但必填欄位空白，或標題超過 60 字、其他欄位超過 600 字（`LogAnalysisService.cs:668-670`、`AIService.cs:416`）。
- 所以**目前無法只看訊息就斷定是格式問題**，也可能只是回答太長。以下是從程式碼確認存在、但發生比例未知的機制，都要靠資料驗證：

| 機制 | 程式碼依據 | 哪種內容可能觸發 |
|---|---|---|
| 欄位超長 | 上限 60／600 字 | 問題多、敘述長的主機日 |
| 欄位型別不符 | `AiAnalysisResult` 全是 `string`，用預設選項解析 | 模型把 `action` 寫成清單或物件時，整份解析失敗 |
| 選錯物件 | `AiJson.TryParse` 回傳**第一個解析得出的物件**；不相關的物件會得到全空的結果，後面真正的答案不再嘗試 | 回覆先引用 JSON 片段，或把答案包在外層物件裡 |
| 被截斷 | `AiMaxTokens` 預設 2048；補括號後仍可能未通過檢查 | 輸入多、要求長回覆 |

- **現有紀錄能查到的**：
  - `DailyAnalysisRecord.Headline`、`Summary` 留著案例與回覆前 600 字（`TextTruncation.Truncate`），超過就看不到結尾。
  - `TopIssues`、`TrendAlerts`、`CorrelationAlerts` 等輸入也在紀錄裡；`RetryAiAsync` 正是用它們重建輸入。
  - 執行紀錄的 Warn 有「JSON 解析失敗」「JSON 內容檢查未通過」與約 180 字預覽，但沒有主機資訊。
- **現有紀錄查不到的**：`finish_reason`、實際 prompt 長度、每欄長度、解析錯誤位置、每次嘗試各自的失敗原因。
- **案例會消失**：同一主機日重新分析成功後，`Headline` 被覆寫，舊案例就查不到了。
- 降級結果 `AiAnalyzed=true`（`LogAnalysisService.cs:692-696`），「只補跑失敗或未執行」不會再挑到它。
- `IAnalysisRecordQuery` 的實作只有 `EfAnalysisRecordStore`、`FakeRecordRepository`、`FakeAnalysisRecordQuery`，新增查詢方法的影響面可控。

### 定案

- **本輪只蒐集證據，不改行為**：不改提示詞、長度上限、解析容錯，也不改降級語意。現在還不知道主因，先改等於猜；第一份匯出分析完再決定。候選修法列在下方，不在本輪做。
- **歷史與新資料都要**：歷史案例立刻可分析，但細節有限；新資料要等下一次發生才有。`meta` 寫明哪天起才有詳細診斷，AI 才不會把缺資料誤判成沒發生。
- **診斷寫進既有執行紀錄**：不加資料表與設定，保留天數沿用執行紀錄，也不需要新的可選相依。互動功能（Web AI 卡片）的呼叫不在 recorder 範圍內，不納入。
- **診斷行不上畫面**：只給匯出用，避免 serverAdmin 能看的內容變多（B7），也避免「重要紀錄」被長 JSON 洗版。

### 改動

- **F-1 補齊呼叫資訊**：`AIService` 讀取 `finish_reason`，隨每次嘗試帶出 prompt 字數、回覆字數、token 數。只新增資料，呼叫行為不變。
- **F-2 失敗診斷（每次 `ChatJsonAsync` 最終失敗一筆）**：以獨立 logger `AiJsonDiagnostics` 寫一行 Warn，內容是精簡 JSON：
  - 呼叫資訊：`label`、回覆型別、模型、`maxTokens`、嘗試次數。
  - 每次嘗試：失敗類別、`finish_reason`、字數與 token 數、解析錯誤位置（`JsonException.Path`，不含內容）、解析得出時各欄長度、回覆開頭 1,500 字與結尾 500 字（暫定）。
  - **失敗類別**：`empty`、`no_json`、`truncated`、`type_mismatch`、`parse_other`、`validation`。內容檢查是呼叫端傳入的判斷式，服務層不知道哪一欄不合格，所以記各欄長度，由讀者對照上限。
  - `AiJson.TryParse` 只新增能帶出錯誤的版本，**候選順序與回傳結果不變**。
- **F-3 寫入與顯示**：
  - `BatchRunRecorder` 把這個 logger 比照 `SqlPerformanceMonitor` 不計入 `WarnCount`（依 logger 名稱判定，不比對訊息）。
  - `RunMonitorService.GetDetail` 與異常彙總都排除這個 logger。
  - 主分析的 `label` 加上主機識別（暫定 `daily-{日期}-h{HostId}`），讓診斷對得回主機日；`LogAnalysisService.cs:693` 的 Warn 補上 `result.Error`。
- **F-4 匯出組裝（`aiFormatAnomalies`，由 C-9 消費）**：
  - **什麼算一個案例**：期間內 `Headline` 等於該常數的主機日紀錄。常數抽成共用，不各寫一份字面值。新增「依日期範圍與標題精確查詢」的查詢方法，不走 `QueryLightweight`，因為後者是輕量投影，欄位不能當完整輸入用。
  - `summary`：案例數；依日期、作業系統、資料來源、回覆類別的分布；Warn 歷史統計出的解析失敗與內容檢查失敗次數（依回覆型別）；詳細診斷的起始日。
  - `cases`（新到舊，上限 100，暫定）：主機代號、日期、作業系統、資料來源、風險、嘗試次數、遮罩後的回覆前 600 字、回覆類別、輸入特徵、對應診斷。
  - **回覆類別（從保留的 600 字判斷）**：開頭是 `{`、Markdown 圍欄、陣列、一般文字、空白；達 600 字上限就另標 `cut_off`，**不推測被截掉的內容**。
  - `input`（只給最新 50 個案例，暫定）：前 20 個問題的來源、Event ID、次數、遮罩後訊息前 300 字，加上趨勢與關聯提示（遮罩後）。
  - **輸入特徵**：問題數、規則命中數、錯誤／警告／稽核數、趨勢與關聯提示數、資料是否不完整、輸入總字數、最長單則字數，以及 `"`、`\`、`{`、`[`、`<`、控制字元、非 BMP 字元、路徑、JSON／XML 樣式片段的出現次數，還有非 ASCII 比例。
  - **對照組**：同主機、最接近的日期（前後 7 天內）、`AiAnalyzed=true`、不是異常案例、不是低風險跳過的紀錄，每案最多 1 筆，只放特徵；找不到就是 `null`，不換別台。`comparison` 列出案例與對照的各特徵中位數，**任一組為 0 筆時輸出 `null`**，並附樣本數。
  - 沒有案例時回傳空清單和 `caseCount=0`，不算錯誤。

### 分析後的候選修法（本輪不做，留待第一份匯出）

| 若資料顯示 | 候選方向 |
|---|---|
| 主因是欄位超長 | 提示詞明確要求字數；或寬鬆截斷取代整份重問 |
| 主因是型別不符 | 解析時把清單接成文字、物件取文字欄位 |
| 主因是選錯物件 | `TryParse` 依必填欄位挑候選，不取第一個 |
| 主因是截斷 | 調整 `AiMaxTokens` 或輸入塑形 |
| 集中在特定內容 | 組 prompt 前處理該內容（跳脫、裁短、去控制字元） |
| 不論主因 | 訊息依失敗類別顯示；重新評估 `AiAnalyzed=true` 是否讓案例再也補不到 |

### 測試 / 驗收

- `AIServiceJsonDiagnosticsTests`（新增）：
  - 每個失敗類別各一個輸入，類別正確：空回覆、純文字、截斷且 `finish_reason=length`、`action` 為陣列（Path 指到 `$.action`）、合法但超長。
  - 成功或重試後成功都不寫診斷；傳輸層失敗不寫。
  - 開頭與結尾超過上限時正確截斷；沒有 `finish_reason` 的回應輸出 `null`。
  - `TryParse` 結果在既有案例上不變。
- `BatchRunRecorderScopeTests` 補：診斷行不增加 `WarnCount`，仍寫入執行紀錄。
- `RunMonitorServiceTests` 補：`GetDetail` 與異常彙總都看不到診斷行。
- `LogAnalysisServiceRetryAiTests` 補：既有降級結果不變（標題、風險、`AiAnalyzed`）；`label` 含主機識別；新 Warn 帶失敗原因。修改前先 grep 既有對 `daily-` 的斷言。
- `RunExportServiceTests` 補：
  - 內容檢查失敗與解析失敗都算案例；重新分析成功後不再是案例。
  - 正常紀錄與低風險跳過不算案例；對照組不選自己、不跨主機、超過 7 天回 `null`。
  - 剛好 600 字標 `cut_off`；`comparison` 樣本為 0 時輸出 `null`，不是 0。
  - 案例超過上限時 `omitted` 正確；事件訊息與回覆裡的原值全部被遮。
- 突變：把 `Headline` 比對改成包含比對、對照組拿掉同主機條件、診斷拿掉 `WarnCount` 豁免、`TryParse` 新版本改動候選順序，測試都要紅。
- 定向篩選：`FullyQualifiedName~AIServiceJsonDiagnosticsTests|FullyQualifiedName~BatchRunRecorderScopeTests|FullyQualifiedName~LogAnalysisServiceRetryAiTests|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~AIServiceExtraRequestFieldsTests|FullyQualifiedName~BranchOutcomeAndCacheTests`（含 `ChatJsonAsync` 的既有呼叫端；新類別寫完後確認命中數 > 0）。

## 批次E：文件與說明書

### 改動

- `docs/WEB-SPEC.md`：§9.10 新增匯出區塊（入口、內容、上限、去識別化、權限）與異常彙總新欄位；§7.2 錯誤碼；§11 稽核動作清單與 `AiJsonDiagnostics`（不計警告、不上畫面、只給匯出）。
- `LogForesight.Web/HelpContent/10-scheduler.md`、`10-scheduler.ai.md`：修正「幾台主機」為實際語意（B1），新增匯出說明；遵守 `docs/HELP-WRITING-GUIDE.md`。
- `RunMonitorService.cs:467` 註解同步（程式碼隨批次A 改）。
- `docs/BACKLOG.md`：新增兩條——B7 serverAdmin 可見輸出內容（觸發時機：serverAdmin 權限盤點時）；代號對照表（觸發時機：實際使用時常常回查不到）。

### 測試 / 驗收

- `FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~DocNarrativeGuardTests`。
- 全量 `dotnet test`，結果寫進體檢交接。

## 明確不做（本輪定案）

- **CSV 或多檔格式**：JSON 已足夠，AI 也不需要試算表形式。
- **完整模式（含全部 Output）**：與精簡目標相反，也是外流主要路徑；要看單趟細節仍可用「檢視執行」。
- **匯出對照表**：理由見批次B 定案。
- **定時自動匯出、站內直接送 AI 分析**：本輪需求只是手動取得資料。
- **改寫 `IRunConsole` 為結構化等級**：理由見 §0。
- **調整 serverAdmin 對執行詳情的檢視權限**：刻意開放的既有行為，只記 BACKLOG（B7）。
- **本輪修 AI 格式異常的成因**：提示詞、長度上限、解析容錯、降級語意都等第一份匯出分析後再決定（§批次F）。
- **匯出 prompt 全文、互動 AI 卡片的失敗**：前者理由見 §1；後者不在 recorder 範圍內。
- **其他頁面的既有匯出（校準、PRTG 鏡像）套用去識別化**：用途不同；校準與搬運需要真值。
- **修 BACKLOG 既有的階段快照放大**：觸發條件未達；`dailyHealth` 與 `runStats` 只讀不放大。

## 規劃完成後複檢

逐項回頭檢查後，結果如下。**這一節有發現並已改進本文，沒有留下新的待決。**

- **與既有行為的衝突**
  - WEB-SPEC §11「查詢不記稽核」：既有的校準與 PRTG 匯出都有記，全站資料離站屬同一類，本輪比照並更新文字。
  - `/api/runs/errors` 改形狀：唯一呼叫端是 `runs.js:161`，已納入批次D；C-4 不變式要跟著讀新形狀。
  - `CLAUDE.md`「慢 SQL 不計入 `WarnCount`」：Warn 組保留標示，不改計數。
  - `CLAUDE.md`「判定不得比對訊息內容」：§0 已交代 `⚠` 篩選不是判定；批次A 也沒有用文字判斷狀態。
- **與「不附 prompt」規格的關係**：WEB-SPEC §11 指的是錯誤訊息不附 prompt，本輪仍遵守；匯出的是紀錄裡既有的問題清單（遮罩後），不是 prompt。
- **與「不匯出事件內容」的衝突**：§1 排除一般事件文字，批次F 需要它才能判斷內容，因此只開放給異常案例，設數量與字數上限，加強事件身分格式的遮罩，`meta` 也會標示。
- **批次之間**：批次F 的 `AiJsonDiagnostics` 由 A-3、A-4 的彙總與 C-1 的訊號組排除，三處共用同一個 logger 常數；F 排在 C 之前，C-9 才有資料可組。A-5 的正規化在 A 只搬移不改語意，C 是在遮罩後才呼叫，畫面組數不變。D 依賴 A-3 的新形狀與 C 的端點，順序已排在後面。
- **四個坑**
  - 什麼算一個、分母為零：A-3 趟次定義、C-1 組鍵、C-3 空資料。
  - 破壞性判準的反例：去識別化屬於此類（多遮損失語意、少遮造成外洩），已列反例表，失敗方向一律是多遮或中止。
  - 單向閘門：B-6 中止匯出的繞過路徑是修字典或保留清單，**不提供略過開關**；誤判時使用者會看到錯誤碼，開發者依 log 的類別與次數修正。
  - 移除類：本輪沒有移除對外介面；AI 回覆片段只在匯出省略，畫面與儲存不變。
- **升級與既有資料**：不動 schema、不寫回資料；舊紀錄缺 `Phases`、`Trigger`、`PrtgDays` 時各欄輸出 `null` 或歸入 `unknown`／舊紀錄類別，C-1 與 `runStats` 已交代。
- **本次補進本文的細節**：逐行讀取要避免 `GetLogs` 的重複讀取；組字典需要上限；IP 應對應到主機代號；`GroupPath` 要切段比對；AI 回覆片段要從匯出移除。

## 驗收篩選實跑紀錄

2026-11-03 於 dev@6ab5480d 實跑既有類別的定向篩選，命中 152 條且全部通過：

`FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~BatchRunStoreSqlPushdownTests|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~RunsTimelineBehaviorTests|FullyQualifiedName~ControllerRouteSnapshotTests|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout`

這次沒有分別統計各類別命中數。本輪新增的 `RunExportDeidentifierTests`、`RunExportServiceTests` 與控制器測試還不存在，寫完後要回頭跑一次，確認命中數 > 0 且包含新案例。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A | Sonnet 5.5 medium | 完成 | 主代理重跑定向 564 綠、代理突變兩項皆紅、diff 逐段檢視 | 分組鍵多了「是否不計警告」，讓欄位值單一；`misc-pages.behavior.mjs` 替身隨 A-3 形狀連動 |
