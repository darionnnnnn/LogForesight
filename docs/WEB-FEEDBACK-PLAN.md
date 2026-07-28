# WEB-FEEDBACK-PLAN：十二項使用者回饋的規劃（2026-07-27）

> 狀態：**全部實作完成（2026-07-27）**，879 個測試綠、關鍵頁面瀏覽器實測通過。
> 拍板原則：層級與處理指標**整站統一**（不允許各頁各自範圍）；AD 失敗細節不對使用者顯示；
> 設定頁提供 AD 測試連線；批次新增遇既存帳號由使用者決定是否覆蓋權限。
>
> 實作時與規劃的偏差（皆為改善方向）：
> - **#3 prompt 端不再要求「純文字」**：原規劃是「prompt 要求純文字＋渲染器兜底」，實作後
>   markdown-lite 已能安全渲染粗體／清單，要求純文字反而放棄有用的排版——刻意不加該指令。
> - **S8 泛型化**：SettingsBoundClient 從 (BaseUrl, KeyEnc) 固定形狀改為 `<TSnapshot, TClient>`
>   任意快照——AD 動態驗證的快照是伺服器清單＋SearchBase／Filter，原形狀塞不下。
> - **S7 渲染出口涵蓋四個點**：chat 泡泡（區塊版 renderAiText）＋ AI 判讀／AI 歸納（區塊版）＋
>   儀表板今日焦點（行內版 renderAiInline，清單項內要接下鑽連結）。

批次分組（依相依與風險排序；**批次 0 見 docs/SHARED-STANDARDS-PLAN.md**——
先立共用標準，各項回饋落在共用點上實作，不再各自處理）：

| 批次 | 項目 | 性質 |
|------|------|------|
| 0 | 共用標準 S1–S12（SHARED-STANDARDS-PLAN.md） | 單一事實來源收斂，先行 |
| A | #1 等待動畫、#3 Markdown 呈現（走 S7 renderAiText）、#2 台灣用語強化（走 S7 LanguageReminder）、#4 下拉連動、#10 固定高度＋自動捲底、#12 清除鈕圖示 | 純前端＋prompt 微調，低風險 |
| A2 | #11 報告全文餵入 AI 對話（PromptBudget 預算控管） | 後端 prompt 組裝，中小型 |
| B | #5 層級對應與連動（機制落在 S1 Repository 咽喉點） | 模式簡化＋全站統一過濾，已定案 |
| C | #6 報表圖表改版（處理進度走 S3、統計組裝走 S4/S5） | 前後端，中型 |
| D | #7 批次新增使用者 | 前後端，中型 |
| E | #9 AD 設定與動態驗證（快取走 S8）→ #8 AD 自動補資料 | 驗證層改造，#8 依賴 #9 |

---

## #1 詢問 AI 沒有等待中的提示動畫

**現況**：`chat-panel.js onSubmit` 呼叫 `withBusy(send, '')`——busyText 傳空字串，
`withBusy` 只 disable 按鈕、不顯示 spinner（ui.js:383），訊息區也沒有任何「AI 思考中」的視覺回饋。
地端模型一輪回覆可能要數秒～數十秒（timeout 60 秒），使用者只看到畫面靜止。

**方案**：
1. 訊息區加「輸入中」泡泡：`renderMessages()` 增加 `pending` 狀態，送出後在對話尾端
   渲染一顆 assistant 樣式的泡泡，內容是三點跳動動畫（純 CSS `@keyframes`，新增 `.lf-typing` 到 site.css）。
   收到回覆或失敗後移除。
2. 送出鈕改 `withBusy(send, '送出中')`，沿用既有 spinner 樣式。

**影響檔案**：`wwwroot/js/pages/chat-panel.js`、`wwwroot/css/site.css`。
**風險**：無；純前端，JS 無測試涵蓋。

---

## #2 AI 回覆需要台灣用語繁體中文

**現況**：`AiInsightService.ChatAsync` 的 system prompt 已含 `PromptGuidelines.Language`
（記憶體/硬碟/網路等詞彙白名單＋簡體字黑名單）。但小模型在多輪對話攤平成長 user prompt 後，
對 system prompt 尾端規範的遵循度會下降，仍會漏出簡體或大陸用語。

**方案**（先做 1，2 視效果保留）：
1. **尾端強化**：在攤平後的 user prompt 最後（`【新問題】…` 之後）追加一行
   「（請全程以台灣繁體中文與台灣資訊業用語回答，勿使用簡體字）」——模型對 prompt 尾端的指令遵循度最高。
   InterpretIssue／QuerySummary／TodayFocus 若也有同樣問題，比照辦理。
2. **偵測重生（保留選項）**：回覆若含常見簡體字（「内、盘、络、认、据、启」等小集合偵測），
   重打一次並在 prompt 註明。代價是最壞情況延遲翻倍，互動情境不划算——先不做，觀察 1 的效果。

不建議引入 OpenCC 之類的轉換庫：簡→繁逐字轉換處理不了「用語」問題（默认→預設不是字對字），
且違反專案「不增外部依賴」的傾向。

**影響檔案**：`Services/AiInsightService.cs`。
**測試影響**：AiInsightService 相關測試若有斷言 prompt 內容需同步更新。

---

## #3 AI 回覆的 Markdown 呈現

**現況**：AI 回覆一律 `textContent`＋`white-space: pre-wrap`（chat-panel.js:157–159），
這是刻意的安全設計（AI 產出不可信任為 HTML），但模型愛輸出 `**粗體**`、`- 清單` 等
Markdown 語法，畫面上就是原樣的星號。

**方案**：兩頭並進——
1. **Prompt 端**：chat 的 system prompt 加「以純文字回答，不要使用 Markdown 語法（粗體星號、井字標題等）」。
   小模型不會百分之百聽話，所以還需要 2。
2. **前端輕量渲染器**：新增 `wwwroot/js/core/markdown-lite.js`，把回覆文字轉成 **DOM 節點**
   （`document.createElement`＋`textContent` 組裝，**全程不碰 innerHTML**，維持既有 XSS 防線）。
   只支援安全子集：`**粗體**`、`` `行內代碼` ``、`- `/`1. ` 清單、`#` 開頭行轉粗體行、換行。
   其餘語法（連結、圖片、HTML）一律當純文字。不引入外部 Markdown 庫。
3. 套用範圍：chat 泡泡先做；「AI 判讀」「查詢歸納」「今日焦點」等其他 AI 文字輸出點
   共用同一個模組，之後視需要接上。

**影響檔案**：新增 `wwwroot/js/core/markdown-lite.js`；`chat-panel.js`；`AiInsightService.cs`（prompt）。
**風險**：低。渲染器不解析 HTML、不產生連結，攻擊面沒有變大。

---

## #4 詢問 AI 下拉選單應跟隨重點問題的嚴重度篩選

**現況**：`record-detail.js load()` 把 `currentDetail.topIssues` 整包傳給 `initChatPanel`
（Locked 模式已先過濾），但**前端嚴重度篩選鈕（activeSeverities）與下拉選單不連動**——
使用者關掉「低」，下拉裡仍列得出低嚴重度的問題。

**方案**：
1. `initChatPanel` 改收「目前篩選後」的清單：`topIssues.filter(i => activeSeverities.has(i.severity))`。
2. chat-panel.js 匯出 `updateIssueOptions(issues)`：嚴重度鈕切換時（`renderSeverityFilter` 的 click handler）
   重建下拉選項。若目前選中的 issueKey 已不在清單中 → 重置選擇與對話（回到「請先選擇」、清空 messages）；
   仍在清單中 → 保留選擇與對話不動。
3. 後端 `AiController.Chat` 不用改——它驗證 issueKey 存在於 `detail.TopIssues` 即可，
   前端篩選是顯示層行為（與 DefaultHidden 模式語意一致：資料還在，只是預設不顯示）。

**影響檔案**：`record-detail.js`、`chat-panel.js`。
**風險**：低。注意 chat-panel 以 cloneNode 重綁事件的既有模式，更新選項時不要重複綁 listener。

---

## #5 設定的「層級」與實際資料層級對不上、連動有問題　⚠ 決策點

**現況診斷**（這項一半是設計如此、一半是真的不一致）：

系統裡有**兩套不同的層級**：
- **問題嚴重度**（IssueSeverity：Critical/High/Medium/Low，畫面顯示「嚴重/高/中/低」）——
  設定頁「未處理計算層級」勾的是這個。
- **日風險等級**（RiskLevel：高/中/低）——批次分析時算定的證據層，
  儀表板「高風險日/中風險日」KPI、趨勢圖、風險層級占比、風險主機排行用的是這個。
  `SystemSettings.SeverityDisplayMode` 註解明講「不影響風險等級判定與報告全文」。

所以「沒有選『中』，儀表板卻還顯示中與低風險」有兩層原因：
1. 日風險等級本來就不受此設定影響（設計如此，但畫面上完全沒有說明，使用者無從分辨兩套「高中低」）。
2. 就算看的是問題嚴重度（風險類型卡的嚴重度徽章分解），**只有 GlobalFilter 模式**會過濾聚合
   （`SystemSettingsService.GetVisibleSeverities` 只在 GlobalFilter 回集合，其他模式回 null）——
   Locked 模式號稱「完全隱藏」，卻只藏詳情頁，儀表板／報表的徽章與計數照樣出現未勾選層級。這是真的不一致。

**定案（2026-07-27，依「整站統一、不要各頁各自範圍」的原則）**：

1. **顯示模式從三個簡化為兩個**——三模式的差異本身就是「各頁範圍不同」的來源，直接收斂：
   - `DefaultHidden`（預設隱藏，仍可手動開啟）：維持現況，純顯示層行為。
   - `SiteHidden`（全站隱藏）：未勾選層級的問題**在整個 Web 後端查詢層一律排除**——
     詳情頁重點問題、AI 對話下拉、AI 聚類輸入（ClusterSignatures）、儀表板類別卡、
     報表類型分布圖、問題查詢頁的問題層欄位與下鑽、簽章查詢，全部同一套過濾，沒有例外頁。
     實作錨點（**SHARED-STANDARDS-PLAN S1**）：過濾收斂到 `RecordRepository` 單一咽喉點，
     `GetVisibleSeverities()` 在 SiteHidden 回集合、各 Service 不再各自過濾；
     詳情頁不再靠前端 Locked 特判，後端給什麼就是什麼。
   - **舊值遷移**：blob 裡既存的 `Locked`／`GlobalFilter` 在 `SystemSettingsService.Get()`
     讀取時正規化為 `SiteHidden`（兩者語意都被新模式涵蓋且更嚴格一致）；
     `Update()` 只接受新的兩個值。不動 blob 本身，下次儲存自然寫入新值。
2. **文案對齊**：設定頁「未處理計算層級」明確標示為「問題嚴重度（嚴重/高/中/低）」，
   並加說明「日風險等級（高/中/低風險日）由批次分析算定，不受此設定影響」；
   儀表板高/中風險日 KPI 卡加 tooltip 註明同一句。
3. **日風險等級維持不連動**（詳細理由見下）：風險等級是批次算定寫進報告 txt 的證據層，
   且它不是嚴重度的彙總（關聯訊號/趨勢也會拉高風險），無法靠「扣掉某層級的問題」可靠重算；
   Web 重算會讓畫面與報告全文、既有待辦數字對不上，違反誠實原則。以 2 的文案讓兩套層級可分辨。

**影響檔案**：`SystemSettingsService.cs`（GetVisibleSeverities＋值正規化）、`RecordQueryService.cs`
（GetDetail／ClusterSignatures 接上過濾）、`record-detail.js`（移除 Locked 前端特判）、
`settings.js`（兩模式）、`Settings.cshtml`（文案）、`SystemSettings.cs`（註解更新）。
**測試影響**：SystemSettingsService 補正規化案例；Dashboard／Report／RecordQuery 的
GlobalFilter 測試改為 SiteHidden；原 Locked「只藏詳情頁」的測試反轉。

---

## #6 報表圖表改版（圓餅圖縮小＋管理者指標＋自選圖表）

**現況**：Reports.cshtml 是 2×2 等寬網格，「風險層級占比」doughnut 只有兩個值（高/中風險日數）
卻佔 1/4 版面；報表沒有「主機母體」與「處理進度」視角。

**方案**：
1. **新增管理者指標**（後端 `ReportSummaryDto` 擴充）：
   - `TotalHosts`：可見且啟用的主機總數（ReportService 注入 `IVisibilityService`，
     與 DashboardService 同一來源，數字才對得上）。
   - `Handling`：期間內高＋中風險日的處理彙總（注入 `IHandlingService`，
     沿用 `GetTodo` 的日層級規則；若 `GetTodo` 目前沒回 resolved 數，擴充它而不是另寫一套推導）。
   - 前端據此畫兩顆新的小 doughnut：「受影響主機占比」（affectedHosts/totalHosts）、
     「處理進度」（已處理/待辦母體），中央疊大字百分比。
2. **版面**：原「風險層級占比」與兩顆新圖合併成一列三顆小占比圖（col-lg-4×3，高度約現行一半），
   騰出的位置讓「主機告警排行」可以放寬。趨勢與類型分布維持上排。
3. **自選圖表 modal**：
   - reports.js 建圖表註冊表 `{id, title, sectionEl, render}`；
   - 工具列加「自訂圖表」鈕開 Bootstrap modal，checkbox 逐圖勾選；
   - 勾選狀態存 `localStorage('lf.reports.visibleCharts')`，預設全開；
   - 隱藏的圖不呼叫 render（省一次 Chart.js 建構），重新勾選時才 lazy render；
   - 列印沿用畫面狀態（隱藏的卡片有 d-none 就不會印）。

**定案（2026-07-27）**：「處理進度」母體採**日層級**（與儀表板待辦同一套 `GetTodo` 規則），
理由：全站唯一的跨頁處理指標（儀表板待辦 KPI）已是日層級，報表沿用同一規則，
儀表板、報表、下鑽清單三處數字才會相等；問題層級的已處理/未處理計數器是詳情頁的
頁內視角（含低風險預設不處理、自動雜訊等推導），拿來做全站百分比會隨顯示設定漂移。
詳細理由見文末「#5/#6 統一性說明」。

**影響檔案**：`ReportService.cs`＋`DashboardDtos.cs`（或 ReportDtos）、`ReportsController`（無需改，DTO 帶出即可）、
`Reports.cshtml`、`reports.js`、`site.css`。
**測試影響**：ReportService 測試補 TotalHosts／Handling 欄位案例。

---

## #7 手動新增使用者支援一次多筆

**現況**：modal 一次一筆（POST `/api/admin/users` ＋ PUT `/users/{id}/groups`）。

**方案**：
1. **UI**：modal 頂部加「單筆／多筆」切換。多筆模式：
   - 帳號欄換成 textarea（一行一個帳號，也接受逗號分隔）；
   - 隱藏顯示名稱、Email 欄位（顯示名稱後端預設＝帳號；Email 留空，配合 #8 由 AD 登入時補）；
   - 群組勾選照舊，套用到整批。
2. **後端**：新增 `POST /api/admin/users/batch`
   （`BatchCreateUsersRequest { Accounts: List<string>, GroupIds: List<long>, Active: bool, OverwriteExisting: bool }`），
   Service 端 `BatchCreateUsers`：
   - 逐帳號 trim、去重、去空白；上限（建議 100 筆）防手滑貼整份名冊；
   - 群組存在性驗證沿用 `SetUserGroups` 的規則；
   - **已存在帳號**依 `OverwriteExisting` 決定：false → 跳過不動；true → 以這批勾選的群組
     **整組取代**其群組（走既有 `SetUserGroups`，沿用其 Before/After 稽核）；
     顯示名稱與 Email 兩種情況都不動；
   - 回傳結果分類：新增成功／已存在（跳過或已覆蓋）／格式不合；
   - 稽核：每個新增使用者一筆 `UserCreate`（與單筆一致），另補一筆批次摘要。
3. **前端流程（定案 2026-07-27）**：送出前先比對頁面已載入的使用者清單，
   發現已存在帳號時跳 `confirmAction` 告警，**列出那些帳號**，讓使用者選擇：
   「跳過已存在」或「以這次勾選的群組覆蓋其權限」——選了才送出（對應 OverwriteExisting）。
   前端比對只是 UX，後端仍以自己的查詢結果為準（避免兩人同時操作的競態）。
   完成後 toast＋結果清單（「新增 8 筆、覆蓋 2 筆（a、b）」）。

**影響檔案**：`AdminController.cs`、`AdminDtos.cs`、`UserAdminService.cs`、`Users.cshtml`、`users.js`。
**測試影響**：UserAdminService 補批次案例（去重、已存在、群組不存在、上限）。

---

## #8 只填帳號的使用者，AD 登入時自動補顯示名稱與 Email

**現況**：`IdentityService.Login` 驗證通過後只讀 `lf_users`，顯示名稱空白時 fallback 帳號；
`LdapAuthenticationProvider.Verify` 只回 bool，拿不到 AD 上的 displayName/mail。

**方案**（依賴 #9 的 LdapService）：
1. `CredentialCheckResult` 擴充：`record CredentialCheckResult(bool Success, string? FailureReason = null, LdapUserInfo? UserInfo = null)`。
   AD provider 驗證成功時順手 `GetUser`（同一次 bind 的連線內查詢，參考程式碼已具備）；
   Stub 回 null；查詢失敗不影響登入（補資料是加值，不是門檻）。
2. `IdentityService.Login` 在「使用者存在且啟用」之後：
   - `DisplayName` 為空**或等於帳號**（手動/批次新增的預設值，視同未填）且 AD 有 displayName → 補；
   - `Email` 為 null 且 AD 有 mail → 補；
   - 有任一異動才 `Upsert`＋寫一筆 `UserUpdate` 稽核（summary 註明「AD 登入自動同步」）。
   - 已知取捨：使用者手動把顯示名稱改成與帳號相同字串時會被 AD 值覆寫——機率低、影響小，接受。
3. 只在登入當下同步，不做背景批次同步（沒有 service account，設計上就拿不到別人的資料——
   參考程式碼的 bind 模型是「用使用者自己的帳密查自己」，這也省掉保管服務帳號密碼的整包問題）。

**影響檔案**：`Auth/IAuthenticationProvider.cs`、`Auth/LdapAuthenticationProvider.cs`（改寫，見 #9）、
`Services/IdentityService.cs`。
**測試影響**：IdentityService 測試補「補資料／不覆寫已填值／AD 查詢失敗仍登入成功」案例。

---

## #9 設定頁自訂 AD 主機與啟用 AD 驗證　⚠ 決策點

**現況**：驗證方式在 `appsettings.json`（`Auth:Provider` = Stub/Ldap）**啟動時定死**，
DI 註冊 singleton；Ldap 走 `PrincipalContext(ContextType.Domain, domain)`，只支援單一網域名稱、
拿不到失敗原因、也查不到使用者資訊。使用者提供的參考實作
（System.DirectoryServices 直接 bind、多伺服器輪詢、子錯誤碼解析、RFC 4515 跳脫、GetUser/Query 投影）功能完整得多。

**方案**：

### 9.1 設定模型（SystemSettings 擴充，存 webdata blob，純新增欄位、無 schema 變更）
```
AdAuthEnabled     bool          預設 false
AdServers         List<string>  IP 或 LDAP URL，依序嘗試
AdSearchBase      string        選填（DC=corp,DC=com）
AdSearchFilter    string        預設 "(sAMAccountName={0})"
```
不需要儲存任何 AD 服務帳號密碼——bind 一律用登入者自己的帳密（參考實作的模型），
整包規劃**沒有新機密要保管**，也就不動 LF_CRYPTO_KEY 相關機制。

### 9.2 移植參考實作
新增 `LogForesight.Web/Auth/Ldap/`：`LdapOptions`、`LdapAuthStatus`、`LdapUserInfo`、`LdapService`
（依專案慣例調整註解與 NLog）。csproj 補 `System.DirectoryServices` 套件參考
（現有僅 AccountManagement）；類別掛 `[SupportedOSPlatform("windows")]`，與現況一致。

### 9.3 動態 Provider（核心改動）
新增 `DynamicAuthenticationProvider : IAuthenticationProvider`，取代 DI 裡依 appsettings 二選一的註冊：
- 每次 `Verify`／`RequiresPassword` 讀 `ISystemSettingsStore`：
  - `AdAuthEnabled && AdServers 非空` → 用 DB 設定建 `LdapService` 驗證
    （比照 `WebAiService` 的 snapshot 模式快取實例，設定變更即生效、不必重啟站台）；
  - 否則 → 委派給 appsettings 決定的原 provider（Stub 或既有 Ldap）。
- 這正是「測試模式（Stub）開啟 AD 後也走 AD 驗證」的實現方式；
  `WebAppSettings.Validate` 禁止正式環境用 Stub 的欄杆**維持不變**（DB 開關可被關掉，
  不能取代部署層的強制）。
- **鎖死風險與逃生門**：admin 若填錯伺服器把所有人擋在門外，`serverAdmin` 本地救援帳號
  不經任何 Provider（IdentityService 既有順序），永遠進得來——設定頁 hint 要明講這點。
- 失敗原因處理（**定案 2026-07-27：不顯示**）：`LdapAuthStatus` 的細分（密碼過期／帳號鎖定／停用…）
  **只進診斷 log 與稽核 detail**，前端一律「帳號或密碼錯誤」——不洩漏帳號狀態的既有原則不變。

### 9.4 設定頁 UI 與 API
- Settings.cshtml 新增「AD 驗證」卡：啟用開關、伺服器 textarea（一行一台）、
  SearchBase／SearchFilter（進階，收合）、逃生門說明文字。
- `SystemSettingsDto`／`UpdateSystemSettingsRequest` 對應擴充；
  `SystemSettingsService.Update` 驗證：啟用時至少一台伺服器、URL 格式粗檢；稽核 Before/After 帶伺服器清單。
- 「測試連線」鈕（**定案 2026-07-27：要做**）：`POST /api/admin/settings/ad-test`，
  用**管理者當場輸入的帳密**對表單目前填的伺服器清單試 bind（未儲存的值也能測），
  回成功／`LdapAuthStatus` 細節（這裡是 admin 對自己測試，細節可以顯示）。
  密碼不落盤、不進稽核 detail；稽核只記「執行了 AD 測試連線」與對象伺服器。

### 9.5 既有 LdapAuthenticationProvider 的去留
改寫為使用新 LdapService（appsettings 的 `Auth:Ldap:Domain` 對應成單一 server），
或保留原 PrincipalContext 實作僅供 fallback。建議**改寫統一**，兩套 AD 程式碼並存遲早漂移；
`Auth:Ldap:Domain` 設定鍵保留向下相容。

**影響檔案**：`Core/Models/SystemSettings.cs`、`Web/Auth/*`（新增 Ldap 資料夾＋Dynamic provider）、
`Extensions/ServiceCollectionExtensions.cs`（DI）、`Services/SystemSettingsService.cs`、
`Models/Dto/SettingsDtos.cs`、`Controllers/Api/SettingsController.cs`（或 AdminController 既有 settings 端點）、
`Settings.cshtml`、`settings.js`、`LogForesight.Web.csproj`。
**測試影響**：LdapService 本體依賴 DirectoryEntry 難以單元測試，保持薄、不強測；
測試火力放在 DynamicAuthenticationProvider 的切換邏輯（DB 開關開/關、伺服器清單空、fallback）
與 SystemSettingsService 的驗證。既有 IdentityService 測試用 Stub 不受影響。

---

## #10 詢問 AI 區塊固定高度＋scrollbar＋回覆後自動捲底

**現況**：`#chat-messages` 沒有高度限制，對話越長卡片越高，把下方的報告全文卡越推越遠。
`renderMessages()` 尾端其實**已經有** `container.scrollTop = container.scrollHeight`
（chat-panel.js:166）——只是容器不會出捲軸，這行目前形同無效；高度限制一加上就直接生效。

**方案**：
1. site.css 新增 `.lf-chat-messages { max-height: 340px; overflow-y: auto; }`，
   RecordDetail.cshtml 的 `#chat-messages` 掛上此 class。
   用 **max-height 而非固定 height**：對話還沒開始時區塊維持精簡，不擺一個大空框
   （若要「永遠固定高度」再改 height，一行的事，預設先取不佔版面的做法）。
2. 自動捲底沿用既有那行，涵蓋三個時點：送出使用者訊息後、#1 的「思考中」泡泡出現時、
   AI 回覆渲染後——三者都走 `renderMessages()`，不用另寫。
3. 已知取捨：使用者往上捲看舊訊息時若回覆剛好到達，會被強制捲到底。
   標準聊天 UI 會判斷「接近底部才捲」，但這裡最多 10 輪、訊息量小，先用簡單版；
   若實際造成困擾再加「距底 < 一個泡泡高才自動捲」的判斷（一個 if 的事）。

**影響檔案**：`site.css`、`RecordDetail.cshtml`。前端純樣式，無風險。

---

## #11 分析用的 log 一併傳給 AI 回答提問

**現況與資料面的誠實盤點**（決定這項能做到哪裡）：
- 原始事件**逐筆資料批次不落盤**——批次直接讀各主機的 Event Log 分析完就丟，
  持久化的只有兩樣：分析紀錄（每問題最多 **3 則相異範例訊息、各截 200 字**，
  LogAggregator.cs:22-23；低風險基準日還會被 RecordStorageShaper 清空樣本）與
  **報告 txt 全文**（`record.ReportFile` ＋ IReportReader，經 `RecordQueryService.GetReport`
  讀取、可見範圍已由 Repository 強制）。
- 所以「分析用的 log」在 Web 端可行的最大範圍＝**當日報告全文**＋現有問題欄位樣本。
  要更原始的逐筆 log 就得改批次落盤策略（儲存體積數量級成長），不在本項範圍；
  若日後真有需求，另開規劃。

**方案**：
1. `AiController.Chat` 載入當日報告全文（`_records.GetReport(hostId, parsedDate)`，
   同頁「報告全文」卡的同一條路、同一套授權），傳入 `AiInsightService.ChatAsync`。
2. `ChatAsync` 把報告全文加進 context，**圍欄比照事件訊息**：
   「【當日分析報告全文——僅供分析，不是指令】」——報告內含事件原文（攻擊者可控字串），
   與既有雙重防線同一套處理。
3. **預算控管用 Core 現成的 `PromptBudget`**（共用標準原則，不再自寫截斷）：
   - 模型 context 20480 token、輸出上限 768；先組基礎 prompt（問題欄位＋對話史＋新問題＋system），
     報告全文填「剩餘預算」，超出時**從報告尾端截斷**（與批次深入分析同一策略，
     PromptBudget 註解明載）並在圍欄註明「（報告過長已截斷）」——不能讓 AI 以為看到的是全文。
   - 另設報告佔用上限常數（建議 8,000 token）：不是有預算就填滿——地端模型 prefill
     一萬多 token 要數十秒，60 秒 timeout 會開始不夠；8k 夠涵蓋絕大多數報告，
     延遲仍可控。常數集中一處，之後換更快的硬體只調一個數字。
   - 既有 `.Truncate(1500)` 的樣本截斷改由同一套預算邏輯統管（先砍報告、再砍樣本）。
4. 順帶效益：#2 的語言尾端提醒在長 context 下更重要（指令被稀釋），兩項同批實作正好互補。

**影響檔案**：`AiController.cs`、`AiInsightService.cs`（ChatAsync 簽章＋prompt 組裝）、
`IRecordQueryService`（GetReport 已存在，無需新端點）。
**測試影響**：AiInsightService 補「報告過長截斷＋標註」「無報告日照常運作」案例。
**風險**：中低。延遲上升是主要代價（prefill 變長），以 8k 上限與現有 60 秒 timeout 控住；
一次打不到就靜默降級的既有原則不變。

---

## #12 「清除重來」按鈕加圖示

**現況**：`#chat-clear` 是 RecordDetail.cshtml 的靜態純文字按鈕。
sprite（wwwroot/img/icons.svg）**已有** `arrow-counterclockwise` 符號，不用新增圖。

**方案**：cshtml 按鈕內前置
`<svg class="lf-icon"><use href="/img/icons.svg#arrow-counterclockwise"></use></svg>`
（icons.svg 檔頭注釋的標準用法）。chat-panel.js 以 `cloneNode(true)` 重綁事件會連子節點一起複製，
不受影響。

**影響檔案**：`RecordDetail.cshtml` 一處。零風險。

---

## 附錄：#5／#6 統一性說明（為何「統一」統一到這裡為止）

### 系統裡的兩套層級是不同性質的東西

| | 問題嚴重度 | 日風險等級 |
|---|---|---|
| 值 | Critical/High/Medium/Low（嚴重/高/中/低） | 高/中/低 |
| 掛在 | 單一問題（事件簽章） | 主機×日期的分析紀錄 |
| 誰算的 | 規則層逐問題標定 | 批次分析綜合判定（規則命中＋趨勢異常＋**關聯訊號**） |
| 落在哪 | 分析紀錄的 TopIssues | 分析紀錄本身＋報告 txt 全文 |
| 設定頁勾選影響 | ✅（SiteHidden 全站過濾） | ❌（證據層，事後不可改寫） |

**日風險不是嚴重度的加總**：一天被判「高風險」可能是因為攻擊鏈/故障鏈的關聯訊號，
而不是任何單一問題的嚴重度；把 Medium 問題從畫面藏掉之後，「這天還算不算中風險」
沒有可靠的重算方法——除非把整套批次判定邏輯搬進 Web 查詢層重跑一次。
那會造成：(1) 兩份判定邏輯遲早漂移；(2) 畫面數字與報告 txt、已存檔/已列印的報表對不上，
違反「報告是證據」的誠實原則；(3) 待辦（掛在高＋中風險日上）跟著漂移，處理歷程對不回去。
所以統一的邊界劃在：**問題層級的東西全站統一過濾；日層級的東西全站統一不動**——
兩套各自內部一致，畫面文案負責讓人分得出是哪一套。

### 處理狀態也有同構的兩層

- **日層級**（RecordHandling）：整天的處理狀態，儀表板待辦 KPI 用它（母體＝高＋中風險日）。
- **問題層級**（IssueHandling）：詳情頁逐問題的狀態，含「低風險預設不處理」「已知雜訊自動判讀」等推導。

報表 #6 的「處理進度」採日層級，理由同上一節的統一原則：全站已存在的跨頁處理指標
（儀表板待辦）是日層級，報表沿用同一套 `GetTodo` 規則，儀表板 KPI、報表占比圖、
下鑽出去的清單筆數三處才會是同一個數字。問題層級若拿來做全站占比，
分母會被推導狀態與 #5 的顯示設定牽動（藏掉的層級要不要算？），數字站不住。
詳情頁的已處理/未處理計數器維持頁內視角，不與全站指標混用。

---

## 整體風險與相容性

- **既有部署升級**：SystemSettings 新欄位皆有預設值（AdAuthEnabled=false），
  blob 反序列化向下相容；行為在管理者主動開啟前完全不變（與該類別既有原則一致）。
- **#5 是唯一改變既有數字呈現的項目**（Locked 模式的統計會變少），需在版本說明明講。
- **平台**：#9 的 System.DirectoryServices 僅 Windows——專案目標框架本就是 net8.0-windows，無影響。
- **測試基準**：現有 804 綠；各批次完成後全量跑一次。
