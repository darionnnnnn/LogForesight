# 系統總覽與登入（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/login`（登入頁面）與系統全域入口。
- **存取權限**：公開（匿名可存取 `/login` 與 `/api/auth/options`），其餘業務頁面均需通過身分驗證。
- **後端端點**：
  - 登入驗證選項：`GET /api/auth/options`，回傳當前設定之身分驗證提供者類型（`Provider`）與是否需要密碼（`RequiresPassword`）。
  - 登入請求：`POST /api/auth/login`，接收 `{ account, password }`，驗證成功後簽發 JWT 寫入 HttpOnly Cookie，並回傳 `CurrentUserDto`。
  - 登出請求：`POST /api/auth/logout`，清除身分驗證 Cookie，並記錄登出稽核日誌。
  - 目前身分查詢：`GET /api/auth/me`，回傳當前登入者之使用者 ID、帳號、顯示名稱、是否為救援管理員（`IsServerAdmin`）、能力清單（`Capabilities`）與是否需要管理員初始設定（`NeedsAdminSetup`）。

## 系統定位與監控架構

LogForesight 是一套專注於主機安全性與穩定性前兆偵測的分析系統，主要監控對象為：
1. **Windows Server Event Log**：包含傳統三大日誌（`System`、`Application`、`Security`）以及新式 Operational 頻道（如 `Microsoft-Windows-Windows Defender/Operational`、`Microsoft-Windows-TerminalServices-LocalSessionManager/Operational`、`RemoteConnectionManager/Operational`）。
2. **Linux 主機 syslog**：經由 NetIQ Sentinel 取數，監控 SSH 認證、`sudo`/`su` 提權、系統守護行程（systemd/daemon）異常與內核崩潰（OOM-killer/kernel panic）。

系統的核心使命是**提早發現硬體故障前兆與入侵跡象**，在問題擴大、系統當機或資料外洩前發出確定性示警。

## 身分驗證機制與帳號體系

### 1. AD / LDAP 企業目錄驗證
- **正式環境驗證**：透過 `System.DirectoryServices` / `LdapAuthenticationProvider` 與企業 Active Directory (AD) 進行 LDAP 帳密驗證。
- **AD 屬性自動同步**：在「系統管理 > 使用者」或「資料匯入」中僅填寫帳號建立的使用者，首次透過 AD 成功登入時，系統會自動自 AD 讀取並補齊 `displayName` 與 `mail`（僅在欄位為空或預設值時補入，不覆寫手動修改過的值），並記錄一筆「AD 登入自動同步」稽核。
- **錯誤防護**：帳號不存在、密碼錯誤或帳號被鎖定時，後端回傳明確子錯誤代碼，登入頁直接顯示中文錯誤訊息並停留在原頁面，避免整頁跳轉導致錯誤訊息遺失。

### 2. 本地救援管理員（`serverAdmin`）
- **定位**：獨立於 AD 的本地內建緊急應變帳號，帳號固定為 `serverAdmin`。
- **適用情境**：當 AD 伺服器斷線、LDAP 組態設定錯誤、網路中斷或全體管理員權限被誤鎖時，提供最後一道維運防線。
- **最小授權原則（Least Privilege）**：
  - `serverAdmin` 僅具備維護與稽核能力（`Capability.Maintain`、`Capability.ViewAudit`、`Capability.DevMonitor`）。
  - **刻意不具備業務資料檢視能力（無 `Capability.ViewAll`）**：登入後無法查看總覽儀表板（`/`）、問題查詢（`/records`）與報表（`/reports`）。
  - 這種設計是刻意的資安防護，確保救援帳號僅用於修復系統設定、排程與使用者帳號，不能用來窺探一般主機的業務安全日誌。

### 3. Session 與安全性防護
- **JWT Cookie 安全標記**：
  - `HttpOnly`：前端 JavaScript 無法讀取 Token，有效防範 XSS 攻擊竊取身分憑證。
  - `SameSite=Strict`：瀏覽器在跨站請求時一律不發送此 Cookie，構成 CSRF 防護的第一道防線。
  - `Secure`：限制僅在 HTTPS 加密通道傳輸（若部署於 IIS 子路徑亦依全站設定自動調整）。

## 角色與能力體系（Role & Capabilities）

系統權限架構分為「功能能力（Capability）」與「資料可見範圍（Visibility）」兩個正交維度：

### 1. 七大核心能力（`Capability`）
1. `ViewAll`：可檢視全站所有啟用中主機的分析結果與報表。
2. `Handle`：可維護風險日與個別問題簽章的處理狀態（未處理／處理中／已處理）。
3. `Assign`：可指派與改派問題案件的處理人（僅 admin 具備）。
4. `ConfirmPermission`：可確認權限異動檢核（授權操作或標記可疑）。
5. `Maintain`：可維護規則、告警抑制、主機、使用者、群組、Sentinel 與系統設定。
6. `DevMonitor`：可檢視排程作業、背景佇列、執行監控與除錯日誌。
7. `ViewAudit`：可檢視系統操作紀錄與安全性稽核日誌。

### 2. 四大角色能力映射（`RoleCapabilityMap`）
- **`user`（一般使用者）**：`Handle`、`ConfirmPermission`（無 `ViewAll`，僅可見被授權群組或自己負責的主機）。
- **`dev`（開發/維運人員）**：`ViewAll`、`DevMonitor`（唯讀全站主機業務資料與監控排程）。
- **`manager`（主管人員）**：`ViewAll`（唯讀全站主機業務資料與報表）。
- **`admin`（系統管理員）**：全 7 種能力（`ViewAll`、`Handle`、`Assign`、`ConfirmPermission`、`Maintain`、`DevMonitor`、`ViewAudit`）。

### 3. 多群組能力聯集（Union）
- 一個使用者若屬於多個群組，其最終能力集合為所有啟用中群組所對應能力的**聯集**（Union），而非取單一最高角色。
- 停用中的群組不計算能力。

### 4. 負責人隱含能力（`UserCapabilityResolver`）
- 若使用者是任一**啟用中主機的負責人**，或是任一**問題檔案的負責人**，系統在計算其能力時會自動隱含 `user` 角色（賦予 `Handle` + `ConfirmPermission`），使其登入後具備處理所屬主機/問題的權限，但**不賦予 `ViewAll`**。

## 五層偵測管線架構（Five-Layer Detection Architecture）

LogForesight 採用「確定性運算在前、AI 語意轉譯在後」的分層架構：

```
[原始日誌取數] ──> [1. 規則層] ──> [2. 趨勢層] ──> [3. 慢速趨勢層] ──> [4. 關聯層] ──> [確定性風險判定] ──> [5. AI 白話層]
                      │                │                 │                 │                 │                     │
                  已知危險簽章     14日中位數基準     7天vs前7天總量    跨log攻擊鏈/故障鏈   高/中/低風險日      白話標題/現況/處置
                  知識庫原因/處置   New/Rising升級    惡化倍數≥1.5      19種組合Pattern     (重大旗標強制高)     (純加值，離線不影響)
```

1. **第一層：規則層（`KnownIssueCatalog`）**
   - 確定性比對已知危險事件簽章（Windows Source + Event ID / Linux Program + Pattern）。
   - 命中規則即附帶靜態知識庫內容（白話說明、常見原因、處置步驟），零推論成本、零幻覺。
2. **第二層：趨勢層（`TrendAnalyzer`）**
   - 與過去 14 天歷史中位數基準比對，即時計算「首次出現（New）」、「頻率上升（Rising）」、「重複出現（Recurring）」、「頻率下降（Declining）」。
   - 單日暴量不會墊高基準，保證後續真正異常不被稀釋。
3. **第三層：慢速趨勢層（`SlowTrendAnalyzer`）**
   - 每日確定性比對近 7 天總量 vs 前 7 天總量（增長 $\ge 1.5$ 倍且達最小次數門檻），捕捉躲在單日門檻下的緩慢惡化前兆。
4. **第四層：關聯層（`CorrelationAnalyzer`）**
   - 跨 log 組合比對 19 種攻擊鏈與故障鏈模式（如「大量 4625 破解得手 + RDP 登入」、「Defender 防護關閉 + 惡意程式」、「儲存 I/O 錯誤 + 非預期關機」）。
   - 程式邏輯確定性比對，明確標註時序關係。
5. **第五層：AI 白話層（Gemma / 本機或雲端 LLM）**
   - 純加值轉譯：將前四層確定的風險等級、規則命中、趨勢與關聯訊號組合為 3 段白話敘述（現況、趨勢、處置建議）。
   - 只有「其他（Other）」類別未命中任何規則之問題，才會觸發獨立的 AI 深入分析。

### 確定性風險判定原則
- **高風險日**：規則命中「重大（`ElevatesDayRisk`）」旗標、關聯鏈命中重大模式、或單一問題嚴重度為 High 且未被抑制。
- **中風險日**：趨勢層出現頻率異常（Rising / New 達門檻）或關聯層命中非重大組合模式。
- **低風險日**：無任何異常趨勢與高階危險訊號。
- **AI 單向升級約束**：AI 判斷只能將風險等級往上拉（例如由中升為高），絕不能將程式判定的風險等級往下壓。
- **離線降級容錯（Graceful Degradation）**：若 AI 服務未設定、連線逾時或故障，系統自動降級為「純統計模式」，使用模板語句填充摘要，規則比對、趨勢分析與風險等級判定 100% 正常運作。

## 分析執行與觸發途徑

- **排程自動執行**：於「系統管理 > 排程作業」設定每日時間窗口（最多 4 組），背景輪詢觸發。
- **手動立即執行**：排程作業頁「立即執行」按鈕或單一主機頁「指定主機更新」，行為與排程完全一致。
- **初次執行歷史回補**：首次執行自動回溯檢查近 120 天日誌建立歷史基準；日常例行執行檢查近 14 天（通常僅補昨日）。

## 常見問答與邊界狀況（Q&A）

- **Q: 為什麼使用 `serverAdmin` 救援帳號登入後，儀表板和問題查詢頁面都顯示空白或沒有資料？**
  - **A**: 這是系統遵循最小權限原則的刻意設計。`serverAdmin` 僅擁有系統設定、規則維護、帳號管理與稽核檢視的維護能力（`Maintain`、`ViewAudit`），不具備 `ViewAll` 能力。救援帳號的職責是修復系統組態（如修正 AD 伺服器 IP 或還原管理員帳號），無法查看業務日誌資料。
- **Q: 當 AI 伺服器當機或斷線時，系統會停止分析或遺失告警嗎？**
  - **A**: 完全不會。系統的五層偵測架構中，前四層（規則、趨勢、慢速趨勢、關聯）與風險等級判定全部由 C# 程式確定性計算完成並直接寫入資料庫。AI 層僅負責白話文字生成，AI 離線時系統自動標記為「統計模式（AI 未分析）」，所有危險訊號與告警均完整保存。
- **Q: 使用者同時加入「業務使用者群組（Role: User）」與「維運工程師群組（Role: Dev）」，其能力如何判定？**
  - **A**: 系統會取兩者能力的聯集。該使用者將同時擁有 `User` 的能力（`Handle`、`ConfirmPermission`）以及 `Dev` 的能力（`ViewAll`、`DevMonitor`），因此既能瀏覽全站主機，也能對有權限的問題進行狀態維護與監控。
