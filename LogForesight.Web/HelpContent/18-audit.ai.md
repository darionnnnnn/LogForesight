# 稽核日誌（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/audit`（左側選單標籤為「操作紀錄」）。
- **存取權限**：需要 `ViewAudit` 能力（`Capability.ViewAudit`，admin 與 serverAdmin 角色具備）。
- **後端端點**：
  - 查詢操作紀錄：`GET /api/audit?from={from}&to={to}&actions={actions}&result={result}&dir={dir}&page={page}&pageSize={pageSize}`，對應 `AuditController.Query` 與 `AuditQueryService.Query`。
  - 動作代碼與中文名稱字典：`GET /api/audit/actions`，對應 `AuditController.Actions` 與 `AuditQueryService.GetActionNames`。

## 資料模型與持久化設計

- **實體模型**：`AuditEntry`（存於 `AuditLogStore`，資料表 `lf_audit_logs`）。
- **架構原則**：
  1. **Append-Only**：介面僅提供新增與分頁查詢，無任何更新（Update）或刪除（Delete）路徑，確保稽核紀錄不可竄改。
  2. **僅記寫入與身分事件**：不記錄一般的資料讀取與頁面瀏覽，防止稽核日誌膨脹為存取流量日誌。
  3. **帳號冗餘保存**：`Account` 欄位以字串形式冗餘存儲，即使關聯之使用者日後被更名、停用或刪除，稽核紀錄仍能獨立判讀。系統自動觸發之行為固定存儲為 `(system)`（`AuditActions.SystemAccount`）。
  4. **事前摘要生成**：在操作發生並寫入紀錄的當下，由後端組裝好人讀的白話中文摘要（`Summary`），不依賴查詢時自 `DetailJson` 推算。
  5. **敏感資料遮蔽**：使用者密碼、探索密碼、AI API 金鑰等敏感欄位絕不記入 `DetailJson`，僅保留「是否變動」等布林標記。
- **欄位定義**：
  - `AuditId`（`long`）：主鍵。
  - `OccurredAt`（`DateTime`）：操作發生時間。
  - `UserId`（`long?`）：操作者使用者 ID（系統行為或登入失敗之未知帳號為 null）。
  - `Account`（`string`）：操作者帳號。
  - `Action`（`string`）：動作代碼（見下方動作代碼清單）。
  - `TargetKind`（`string?`）：對象類型（例如 `handling`, `rule`, `user`, `host`, `group`, `import`, `auth`, `sentinel`, `settings`, `schedule`, `probe` 等）。
  - `TargetId`（`string?`）：對象識別碼字串。
  - `Summary`（`string`）：白話摘要。
  - `DetailJson`（`string?`）：欄位級變更前後 JSON 對照。
  - `IpAddress`（`string?`）：客戶端來源 IP。
  - `Result`（`AuditResult`）：結果狀態（`Ok`、`Denied`、`Failed`）。

## 支援的稽核動作代碼清單（AuditActions）

後端在 `AuditActions` 定義並於 `AuditQueryService.ActionNames` 對應繁體中文名稱：

1. **身分與安全事件**：
   - `login`：登入
   - `logout`：登出
   - `login_failed`：登入失敗
   - `session_expired`：工作階段逾期
   - `access_denied`：權限不足被拒
2. **問題處理與交辦**：
   - `handling_assign`：指派處理人
   - `handling_status`：變更處理狀態
   - `handling_note`：更新處理說明
   - `issue_bulk_close`：統一標記問題（跨主機跨日大規模結案操作）
3. **權限異動確認**：
   - `perm_confirm_authorized`：確認權限異動為授權
   - `perm_confirm_suspicious`：標記權限異動可疑
   - `perm_confirm_batch`：批次確認權限異動
4. **規則與抑制維護**：
   - `rule_create`：新增規則
   - `rule_update`：修改規則
   - `rule_enable`：啟用規則
   - `rule_disable`：停用規則
   - `rule_restore_seed`：回復規則預設
   - `rule_delete`：刪除規則
   - `rule_seed_import`：套用規則升級
   - `suppress_add`：新增抑制
   - `suppress_remove`：移除抑制
5. **使用者、主機與群組**：
   - `user_create`：新增使用者
   - `user_update`：更新使用者
   - `host_update`：更新主機
   - `host_merge`：合併主機
   - `host_unmerge`：解除主機合併
   - `group_create`：新增群組
   - `group_update`：更新群組
   - `group_delete`：刪除群組
   - `access_grant`：授予存取權
   - `access_revoke`：收回存取權
6. **問題檔案**：
   - `issue_owner_update`：設定問題檔案（負責人與機房結論變更）
   - `issue_owner_delete`：刪除問題檔案
7. **資料匯入**：
   - `import_apply`：套用 CSV 匯入
   - `netiq_import_applied`：NetIQ 掃描匯入
8. **Sentinel 與 NetIQ 維護**：
   - `sentinel_create`：新增 Sentinel
   - `sentinel_update`：更新 Sentinel
   - `sentinel_delete`：刪除 Sentinel
   - `sentinel_set_active`：變更 Sentinel 啟用狀態
   - `netiq_options_update`：更新 NetIQ 連線與節流參數
   - `netiq_probe_run`：執行 NetIQ API 診斷
9. **系統設定與排程作業**：
   - `settings_update`：更新系統設定
   - `schedule_options_update`：更新排程設定
   - `schedule_manual_run`：手動觸發分析
   - `schedule_manual_cancel`：取消執行中的分析

## 控制項與使用者介面細節

### 1. 篩選表單（`#audit-filter`）
- `#audit-from`：起始日期輸入框（`<input type="date">`），預設值為 6 天前之本地日期（`toLocalDateString`）。
- `#audit-to`：結束日期輸入框（`<input type="date">`），預設值為今日之本地日期。後端在查詢時會自動將結束時間擴充為 `parsedTo.AddDays(1).AddSeconds(-1)`（即當日 23:59:59），確保當天產生的所有紀錄均可被查出。
- `#audit-actions`：動作多選下拉選單（`<select multiple size="1">`），於初始化時向 `GET /api/audit/actions` 載入全量動作代碼與中文名稱。
- `#audit-result`：結果下拉選單，選項包含：
  - `""`（全部）
  - `Ok`（成功）
  - `Denied`（被拒）
  - `Failed`（失敗）
- `button[type="submit"]`：「查詢」按鈕。
- `#btn-denied`：「只看被拒的存取」快捷切換按鈕。
  - 點擊時將 `#audit-result` 在 `Denied` 與全部（`""`）之間切換，並自動同步自身的 `.active` 樣式。
  - 當從 URL 帶入參數（如儀表板登入失敗/被拒卡片下鑽 `/audit?result=Denied`）或手動變更下拉選單為 `Denied` 時，按鈕亦會自動呈現 active 狀態。

### 2. 清單表格（`#audit-list`）
- 總筆數提示（`#audit-count`）：顯示 `共 N 筆`。
- 表格欄位：
  1. `時間`：`occurredAt`，顯示格式為 `YYYY-MM-DD HH:mm:ss`。支援表頭排序（`sortKey: 'occurredAt'`），預設為降冪（`desc`，最新紀錄在最上方）。
  2. `帳號`：顯示格式為 `formatUserName(accountDisplayName, account)` 即 `顯示名稱(帳號)`。若找不到對應之使用者（如登入失敗打錯帳號、外部帳號或 `(system)` 系統帳號），則退回顯示原始 `account` 字串。
  3. `動作`：顯示動作的中文名稱（`actionText`）。
  4. `結果`：徽章呈現：
     - `ok`（成功）：`lf-badge--light` 淺色徽章，顯示「成功」。
     - `denied`（被拒）：`lf-badge--danger` 紅色徽章，顯示「被拒」。
     - `failed`（失敗）：`lf-badge--warning` 黃色徽章，顯示「失敗」。
  5. `內容`（`summaryCell`）：
     - 顯示白話中文摘要 `entry.summary`。
     - 若該筆紀錄包含 `detailJson`，右下方顯示「詳細」連結按鈕。點擊可展開摺疊的 `<pre class="report-text small">` 區塊，顯示美化排版後的 JSON 異動前後欄位差異。
  6. `來源 IP`：發起請求之客戶端 IP 位址（`ipAddress`）。
- 無資料狀態：顯示「沒有符合條件的操作紀錄」，提示「請調整日期區間或動作條件。」。

### 3. 分頁控制項（`#audit-pager`）
- 呼叫 `renderPagination` 渲染分頁列。
- 支援頁碼切換（換頁時自動平滑捲動至頂部）。
- 支援每頁筆數（`pageSize`）切換，設定值存於 localStorage 鍵 `lf.pagesize.audit`。

## 常見問答與邊界狀況（Q&A）

- **Q: 為什麼在操作紀錄中找不到查看主機詳情或搜尋問題的紀錄？**
  - **A**: 系統為了保持稽核日誌的高價值與高效能，設計原則上**僅記錄寫入類操作（新增、更新、刪除、指派、確認等）與身分安全性事件（登入、登出、權限被拒）**。日常的頁面瀏覽與查詢檢索屬於唯讀流量，不寫入稽核資料庫，避免重要軌跡被洗版。
- **Q: 為什麼有些操作的帳號顯示為 `(system)`？**
  - **A**: `(system)` 代表該操作是由系統後台自動化程序所觸發，而非由某位登入使用者手動操作。例如：夜間排程分析自動將問題指派給唯一問題負責人、自動套用機房結論結案等。
- **Q: 操作紀錄可以手動修改或刪除嗎？**
  - **A**: 不行。稽核日誌在架構上為 Append-only 設計，後端與資料存取層僅提供寫入與分頁查詢，沒有任何修改或刪除的端點與邏輯，以確保稽核證據力的完整與不可竄改。超過系統保留天數（`RetentionDays`）的紀錄則會由排程作業自動清理。
- **Q: 「只看被拒的存取」通常用於什麼場景？**
  - **A**: 該按鈕用於資安事件排查。當儀表板上出現權限異常或收到可疑警報時，可一鍵篩選出所有因權限不足而被系統安全中介軟體（PermissionFilter）阻擋的請求（`Denied`），快速分析是否有非授權帳號嘗試存取受保護資源。
