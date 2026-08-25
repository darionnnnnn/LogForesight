# 回饋第二十九輪規劃

> 狀態：規劃中
> 基準：dev@ba9bc4e（2567 綠，略過 6）
> 來源：使用者回饋兩項——(1) 正式機 serverAdmin 改 hash 後登入完全靜默；(2) 儀表板「未處理問題 36」vs 問題查詢「共 32 個問題」不一致
> 委派模型：agy（整輪一種）

## 核對結論（摘要）

- **P1 靜默登入**：serverAdmin 不需要 AD、直接比對 appsettings 的 PBKDF2 hash。「無訊息無 log」的最可能機制是
  `AuthCookie` 無條件 `Secure=true`（`Auth/AuthCookie.cs:21`）撞上 http 部署——登入 API 回 200，
  瀏覽器丟棄 cookie，轉址後被 302 打回登入頁；且 serverAdmin 成功/失敗路徑只寫稽核 DB、
  不寫 NLog（`Services/IdentityService.cs:72-88`），web.log 一片乾淨。
  次要機制：舊 `Path=/` cookie 殘留（刪除指令同被 http 丟棄）、登入頁 module 載入失敗退化成原生 GET submit、
  `ASPNETCORE_ENVIRONMENT=Development` 讓 `.Development.json` 蓋回測試 hash＋Provider=Stub、
  環境變數 `Auth__ServerAdmin__PasswordHash` 覆寫檔案。
  產 hash 工具已存在（`--hash-password`），非缺口；缺口是**可診斷性**（無 log、無格式驗證、無狀態提示）。
- **P2 計數不一致**：兩條疊加——(a) 儀表板 KPI 不套站台可見嚴重度（`IssueTodoQuery.cs:43` 傳 null），
  查詢頁有套；(b) 下鑽帶 `riskLevels=高,中,低` 意圖「不篩」，但 by-issue 視角把它解讀為問題嚴重度
  （`RecordListQueryService.cs:315-322` `MapRiskLevelToSeverities`），四級外 rank 被濾掉。
  同型普查另見四個聚合入口口徑不一（批次 D）。

## 定案

- P2 口徑以**問題查詢頁為準**（使用者視角）：KPI 卡套 visibleSeverities 向查詢頁看齊。
- 同型入口統一（批次 D 逐項定案見該節）。
- 登入錯誤訊息維持不洩漏帳號存在性；AD 未設定狀態改在「系統管理 > 設定」AD 區塊顯示。
- cookie `Secure` 改為跟隨 `Request.IsHttps`。使用者已確認正式機為 https 且 https 同樣無法登入——
  此項**非本案根因**，保留為防禦性修正（成本低、對 https 無影響），優先序降低。
- 正式機症狀（https、無訊息、無 log、無密碼錯誤）最符合機制：**登入頁 JS module 載入失敗 →
  表單退化為原生 GET submit**，POST 根本沒到後端。批次 B2 為主修；根因端（哪支檔載入失敗）
  需正式機瀏覽器 F12 佐證，見「正式機診斷指引」。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 執行者 |
|---|---|---|---|---|
| A | 登入可診斷性：cookie Secure 跟隨 IsHttps、serverAdmin 補 NLog、PasswordHash 格式啟動驗證 | 3 檔＋測試 | 無 | agy（A1/A2/A3 各一段） |
| B | 設定頁顯示 AD 狀態＋登入頁 module 失敗防呆 | 2 檔＋測試 | 無 | agy |
| C | 儀表板 KPI 與下鑽同口徑＋riskLevels 語意修正＋一致性測試 | 3~4 檔＋測試 | 無 | agy |
| D | 同型聚合入口統一（郵件摘要、群組概況、風險類型卡） | 2~3 檔＋測試 | C 先併 | agy |
| E | 文件收尾：WEB-SPEC §6.2 fallback 描述、部署章節診斷指引 | docs | A~D 後 | Claude |

建議順序：B → C，之後 A、D，最後 E（B 提前：對應正式機實際症狀）。

## 正式機診斷指引（不動 code，可與實作並行）

按登入後，逐一確認：
1. **網址列有沒有變成 `…/login?account=…&password=…`**（或任何 query）——有＝原生 GET 退化，
   即 module 載入失敗，前端根因確立。
2. **F12 > Console**：登入頁載入時有沒有紅字（module 404、MIME 錯誤、語法錯誤）？哪一支檔？
3. **F12 > Network**：按登入時有沒有發出 `POST …/api/auth/login`？回應碼與 body？
4. 若 POST 有出去且回 200：檢查 Response 的 `Set-Cookie lf_auth` 與後續轉址——屬 cookie/轉址問題。
5. DB 稽核表（或登入成功後的稽核查詢頁）：`login` / `login_failed` 紀錄有無。
6. 伺服器環境：`ASPNETCORE_ENVIRONMENT` 值、是否存在 `Auth__ServerAdmin__PasswordHash` 環境變數、
   部署目錄有無 `appsettings.Development.json` / `appsettings.Production.json`。

## 批次 A：登入可診斷性

### 現況與核對結果
- `Auth/AuthCookie.cs:21`：`Secure = true` 無條件；`:40-56` 刪舊 cookie 同樣 Secure=true。
- `Services/IdentityService.cs:72-88`：serverAdmin 成功/密碼錯/鎖定只寫稽核 DB（`_audit.RecordAuth`），無 NLog。
- `Configuration/AppSettings.cs:71-74`：PasswordHash 只驗非空＋Production 黑名單，**不驗格式**；
  貼明文或壞 base64 → `PasswordHasher.Verify`（`Auth/PasswordHasher.cs:34-47`）靜默 false。

### 改動
1. **A1 cookie Secure 跟隨連線**：`AuthCookie` 的寫入與刪除（含 legacy 根路徑刪除）之 `Secure`
   改依當次請求 `Request.IsHttps`。契約：https 下行為與現行完全相同；SameSite/HttpOnly/Path 不變。
2. **A2 serverAdmin 認證補 NLog**：serverAdmin 登入成功（Info）、密碼錯（Info）、鎖定（Warn）各補一則
   NLog，不含密碼或 hash 內容；稽核 DB 寫入行為不變。一般帳號路徑既有 log 不動。
3. **A3 PasswordHash 格式啟動驗證**：`AppSettings.Validate` 增加格式檢查——非 `PBKDF2$iters$salt$hash`
   形（iters 為正整數、salt/hash 為合法 base64）即 fail fast，訊息指引使用 `--hash-password` 產生。
   所有環境皆檢查（格式錯在任何環境都不可能登入成功，不是環境差異）。既有「Production 擋公開測試值」不動。

### 測試 / 驗收
- A1：單元測試——http 請求下 CookieOptions.Secure=false、https 下 true；刪除路徑同斷言。
- A2：以 fake logger 或攔截驗證三條路徑各落一則、內容不含 hash 字串。
- A3：正例（合法 hash 通過）＋反例（明文、缺段、iters 非數字、壞 base64 → 各拋且訊息含 `--hash-password`）。
- 全部：`dotnet test` 2567+ 綠。

## 批次 B：AD 狀態可見性＋登入頁防呆

### 現況與核對結果
- AD 未設定時 `UnconfiguredAdAuthenticationProvider` 的訊息在 `IdentityService.cs:92-99` 被丟棄，
  一律回「帳號或密碼錯誤。」（此行為**維持**，不洩漏資訊）。
- 設定頁 AD 區塊目前無「未設定/已設定」狀態摘要。
- `Views/Pages/Login.cshtml:29-46`＋`wwwroot/js/pages/login.js:40-69`：module 載入失敗時表單退化成
  原生 GET submit，**密碼進 URL/query**，且完全靜默。
- **密碼欄預設 `d-none`**（`Login.cshtml:37`），由 `login.js:24-34` 依 `/api/auth/options` 結果移除。
  故 module 鏈載入失敗時「密碼欄整個不出現」＋「按登入頁面重載無訊息」是同一根因的兩個表徵。
- `core/api.js:77-81` 的錯誤訊息永遠非空（有 `??` 預設），**排除**「有送出請求但錯誤訊息空白」的假說。

### 改動
1. **B1 設定頁 AD 狀態**：系統管理 > 設定的 AD 區塊顯示目前生效狀態（未設定→提示「一般帳號無法登入，
   請以 serverAdmin 登入」；已設定→伺服器數等摘要）。資料由既有設定讀取端提供，不新增設定鍵。
2. **B2 登入表單防呆＋失敗可見化**：契約——
   (a) JS 正常時行為完全不變；
   (b) module 鏈（login.js 及其 import）任何一支載入/求值失敗時，按登入**不得**以原生 GET 將密碼送進
   URL（不可送出 GET 是硬性）；
   (c) 該情境不得靜默：登入頁需以**不依賴 module 鏈**的最小手段（cshtml 內少量非 module inline
   script 偵測 module 未就緒）顯示明確錯誤訊息（例：「頁面資源載入失敗，請強制重新整理，
   若持續發生請聯絡管理者」）；
   (d) 密碼欄的預設隱藏在 module 失敗時同樣要有出路——降級提示須明講畫面不可用，
   不可留下「只有帳號欄、按了沒反應」的畫面。
   做法**暫定**交執行端，但 (a)~(d) 四條都要有對應驗收。

### 測試 / 驗收
- B1：設定頁 API/檢視測試——AdAuthEnabled 空與非空兩態的輸出。
- B2：cshtml 靜態斷言（表單不得為預設 GET、含 module 失敗提示的載體）＋既有登入流程測試不變綠。

## 批次 C：儀表板 KPI 與下鑽同口徑

### 現況與核對結果
- KPI：`DashboardService.cs:110` → `IssueTodoQuery.cs:39-47`，`visibleSeverities` 傳 null（不套站台隱藏）。
- 查詢頁：`RecordListQueryService.cs:294,299` 套 `ResolveVisibleSeverities()`；
  `riskLevels` 參數在 by-issue 視角被 `MapRiskLevelToSeverities`（`:315-322,620-626`）當問題嚴重度。
- 下鑽 URL：`dashboard.js:301-303` 帶 `riskLevels=高,中,低` 意圖不篩。
- 無測試斷言兩數字相等。

### 改動
1. **C1 KPI 套可見嚴重度**：`DashboardService` 呼叫 `IssueTodoQuery`／`ActionableOccurrences` 時傳入與
   查詢頁同一支 `ResolveVisibleSeverities()` 結果。受影響的儀表板 KPI（未處理問題、影響主機數等
   同一母體的欄位）一致套用。
2. **C2（實作前核對推翻，改列不做）**：原假說「下鑽帶 `riskLevels=高,中,低` 會濾掉四級外 rank」
   **不成立**——`IssueSeverity` 只有 Low/Medium/High/Critical 四個值
   （`LogForesight.Core/Analysis/KnownIssueCatalog.cs:17-23`），而 `MapRiskLevelToSeverities`
   把「高」展開為 High＋Critical，故三級全帶＝四值全帶＝實質不篩。`dashboard.js:300-302`
   的既有註解正確。此項不改。

3. **C2′ 問題去重的大小寫不對稱（實作前核對新發現，真 bug）**：
   KPI 端 `IssueTodoQuery.Aggregate` 的 `openIssues` 是
   `HashSet<(string Source, int EventId)>`，用**預設序數比較子（大小寫敏感）**，
   而 Source 由 `TryParseSignature` 原樣取出（`LogForesight.Core/Models/IssueHandling.cs:84-90`），
   上游 `ActionableOccurrences` 的 GROUP BY 也用原始 `SourceName`
   （`EfIssueAggregateQuery.cs:474`）；查詢頁的 `Aggregate` 則以
   `GROUP BY UPPER(source), event_id` 收斂（`EfIssueAggregateQuery.cs:81-118`）。
   → 同一個問題來源名稱大小寫不同時（例：`Disk` 與 `disk`），**KPI 算 2 個、查詢頁算 1 個**，
   方向正好是「儀表板較多」，與 36 > 32 相符。
   定案：KPI 端的四個 HashSet 一律改用大小寫不敏感比較子，與 SQL 端 `UPPER(source)` 對齊。
4. **C3 一致性測試**：新增端到端測試——同一批資料下，`dashboard/summary` 的 `openIssueCount` ==
   以下鑽同參數呼叫 by-issue 的 `total`。反例料至少含：一筆被站台隱藏嚴重度的未處理問題（驗 C1）、
   一組來源名稱僅大小寫不同的同一問題（驗 C2′）。測試骨架比照
   `LogForesight.Tests/DashboardServiceTests.cs:275-322`（既有的「風險類型卡主機數 ==
   by-issue DistinctHostCount」測試已把兩邊服務接好）。

### 測試 / 驗收
- C3 為主驗收；另補 C1 單元（隱藏嚴重度不計入 KPI）。既有 `DashboardServiceTests` 全綠（
  「與報表同窗口一致」等既有斷言若因口徑統一而需調整，須在回報中逐條列出理由）。

## 批次 D：同型聚合入口統一（C 併入後執行）

### 現況與定案（逐項）
| 入口 | 現況 | 定案 |
|---|---|---|
| 郵件問題摘要 `MailIssueDigest.cs:86` | 不傳 riskLevels，走預設「高或中」；也不套可見嚴重度 | **統一**：與儀表板 KPI 同參數（可見日風險等級＋可見嚴重度） |
| 群組風險概況 `UnhandledCount`（`DashboardService.cs:217,227`） | = Open + InProgress，與 KPI 卡（僅 Open）同名不同義 | **統一定義**：改僅計 Open；「處理中」若畫面需要另列，不與「未處理」混算。**暫定**——若畫面語意實為「未結案」，執行端可改標籤不改算法，理由寫執行紀錄 |
| 風險類型卡 `AggregateByCategory`（`DashboardService.cs:84`） | 已套 visibleSeverities，與 KPI 卡矛盾 | C1 完成後矛盾自動消失，本批只補一致性斷言 |
| 重點問題 Top5／報表排行 `ExcludeConcluded` | 排除全主機已結論的問題 | **維持**（刻意設計：排行榜聚焦未結案），於 WEB-SPEC 明文化 |

### 測試 / 驗收
- 郵件摘要：測試斷言其問題集合 == 儀表板 KPI 同窗口集合。
- 群組概況：加總各群組未處理數 == KPI 卡未處理問題所涉（同母體斷言，形式由執行端定）。

## 批次 E：文件收尾（Claude）

1. WEB-SPEC §6.2：修正 fallback 描述（第 168 行與訊息替換行為不符）；補 serverAdmin 認證 log 落點、
   cookie Secure 行為、PasswordHash 格式驗證。
2. README／WEB-SPEC 部署章節：補「登入靜默」診斷順序（稽核查詢頁看 login/login_failed →
   環境變數覆寫 → ASPNETCORE_ENVIRONMENT → http/cookie）。
3. WEB-SPEC：KPI 卡與下鑽同口徑契約、Top5 排除已結論為刻意設計。
4. 收尾照 CLAUDE.md 規劃案生命週期四步。

## 明確不做（本輪定案）

- 不改登入失敗訊息的資訊量（維持「帳號或密碼錯誤。」，不區分 AD 未設定）。
- 不動 `.Development.json` 的出貨行為（發行時是否排除屬部署面，記 BACKLOG candidate，本輪不改）。
- Top5／報表排行的 `ExcludeConcluded` 維持現狀（只補文件）。
- 舊「待辦四計數」以風險日數為單位與問題數並列的呈現，本輪不改（語意不同屬設計，非 bug）。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （待實作） | | | | |
