# NetIQ 維護（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/admin/netiq`。
- **存取權限**：需要 `Maintain` 能力（`Capability.Maintain`，admin 角色具備）。
- **後端端點**：
  - Sentinel 列表：`GET /api/admin/sentinels`，對應 `SentinelAdminController.List`。
  - Sentinel 儲存：`POST /api/admin/sentinels`，對應 `SentinelAdminController.Save`。
  - Sentinel 啟用切換：`PUT /api/admin/sentinels/{id}/active`，對應 `SentinelAdminController.SetActive`。
  - Sentinel 刪除：`DELETE /api/admin/sentinels/{id}`，對應 `SentinelAdminController.Delete`。
  - Sentinel 測試連線：`POST /api/admin/sentinels/test-connection`，對應 `SentinelAdminController.TestConnection`。
  - 節流參數讀取與儲存：`GET /api/admin/netiq/options`、`PUT /api/admin/netiq/options`，對應 `NetiqOptionsController` 與 `NetiqOptionsService`。
  - 網段掃描：`POST /api/admin/netiq/scan`，對應 `NetiqImportController.Scan`。
  - 匯入提交：`POST /api/admin/netiq/import`，對應 `NetiqImportController.Apply`。
  - 匯入日誌：`GET /api/imports/logs`。
  - 診斷啟動與狀態輪詢：`POST /api/admin/netiq/probe/start`、`GET /api/admin/netiq/probe/status`，對應 `NetiqProbeController` 與 `NetiqProbeService`。

## 頁籤一【設定（config）】詳細規範

### 1. Sentinel 清單（`#sentinel-list`）
- 頂部顯示 Sentinel 數量提示（`#sentinel-count`，`共 N 台`）。
- 按鈕「新增 Sentinel」（`#btn-new-sentinel`）：呼叫 `openSentinelModal(null)`。
- 表格欄位：
  1. `名稱`：`s.name`。
  2. `連線位址`：`s.baseUrl`。
  3. `作業系統`：`s.os === 'linux' ? 'Linux' : 'Windows'`。
  4. `探索帳密`：若 `canDiscover` 為 true 顯示綠色徽章「已設定」；若 `hasPassword` 但缺帳號顯示「缺帳號」；否則顯示「未設定」。
  5. `主機數`：轄下關聯之主機總數。
  6. `狀態`：`active` 為 true 顯示綠色徽章「啟用」；false 顯示灰色徽章「停用（暫停輪巡）」。
  7. `操作`：
     - `編輯` 按鈕：開啟 `#sentinel-modal`。
     - `啟用 / 停用` 按鈕：呼叫 `PUT /api/admin/sentinels/{id}/active`。
     - `刪除` 按鈕：若 `hostCount > 0` 彈出警告「轄下有 N 台使用中的主機，刪除後這些主機會停用並標記為孤兒（可於主機頁重新綁定到其他 Sentinel，歷史紀錄不受影響）。確定要刪除嗎？」，確認後呼叫 `DELETE`。

### 2. Sentinel 編輯彈窗（`#sentinel-modal`）
- `#sentinel-name`：名稱（文字，必填）。
- `#sentinel-base-url`：連線位址（例如 `https://sentinel.corp.local`）。
- `#sentinel-username`：探索帳號。
- `#sentinel-password`：探索密碼（write-only 欄位，留空表示沿用既有密碼不變更）。
- `#sentinel-os`：作業系統下拉選單（`windows` / `linux`）。
- `#sentinel-use-esm`：以 ESM 事件來源目錄探索（核取方塊）。
  - ESM 目錄包含已註冊但目前無事件回報的主機完整清單。
  - 需要帳號具備 ESM 唯讀權限；若未具備權限，掃描時會自動降級為一般事件掃描並附帶警告。
- `#sentinel-test-connection`：「測試連線」按鈕。
  - 呼叫 `POST /api/admin/sentinels/test-connection`。
  - 密碼留空時會沿用後端已儲存之密碼進行測試。
  - 測試結果顯示於 `#sentinel-test-result`，包含成功/失敗訊息與往返耗時（毫秒）。
- `#sentinel-save`：儲存按鈕。

### 3. 連線與節流參數表單（`#netiq-options-form`）
對應 `NetiqOptions` 模型：
- `#opt-query-delay`（`queryDelayMs`）：節流間隔（0~60000 毫秒），每次 REST API 呼叫之間的等待時間。
- `#opt-page-size`（`pageSize`）：單頁筆數（1~5000）。
- `#opt-max-results`（`maxResultsPerJob`）：單一查詢最多筆數（1~10000000），超過時截斷防爆量。
- `#opt-timeout`（`timeoutSeconds`）：逾時秒數（1~3600 秒）。
- `#opt-retry-count`（`retryCount`）：失敗重試次數（0~20 次）。
- `#opt-backfill-days`（`backfillDays`）：每次執行回望天數（1~30 天），每台主機執行時往回檢查缺漏天數（已完成者略過）。正式環境建議為 1。
- `#opt-max-parallel-servers`（`maxParallelServers`）：同時處理幾台 Sentinel（1~3），硬性上限為 3（同進程架構限制）。
- `#opt-max-parallel-queries`（`maxParallelQueriesPerServer`）：單台 Sentinel 內查詢平行度（1~4）。
- `#opt-allow-invalid-certs`（`allowInvalidCertificates`）：略過憑證驗證（核取方塊）。
- `#opt-offline-demo`（`useOfflineDemoData`）：使用離線示範資料（僅非 Production 環境可見，容器 `#opt-offline-demo-wrap`；開啟時顯示黃色徽章 `#opt-offline-demo-badge`「示範資料開啟中」）。
- `#opt-chat-live-fetch`（`chatLiveFetchEnabled`）：詢問 AI 查無暫存時即時向 Sentinel 查詢現場事件（容器 `#opt-chat-live-fetch-wrap`，僅在 AI 服務可用時顯示；全站併發限制 1、快取 10 分鐘，僅對 NetIQ 主機有效）。
- `#netiq-options-save`：儲存按鈕，呼叫 `PUT /api/admin/netiq/options`。
- `#netiq-options-updated`：顯示最後更新時間與更新者名稱/帳號。

## 頁籤二【匯入（import）】詳細規範

由獨立模組 `netiq-import-wizard.js` 管理：

### 1. 掃描選擇器（`#netiq-scan-picker`）
- `#scan-sentinel-select`：Sentinel 下拉選單，過濾僅列出啟用且帳密齊備者（`active && canDiscover`）。
- `#scan-subnet-input`：網段輸入框（必填，支援前綴如 `192.168.0` 或 CIDR `192.168.0.0/24`）。
- 點擊「掃描匯入」呼叫 `POST /api/admin/netiq/scan`，取得 `NetiqScanResultDto` 並開啟精靈 Modal `#netiq-wizard-modal`。

### 2. 掃描匯入精靈 Modal（`#netiq-wizard-modal`）
- **步驟一：網段主機勾選（`#wizard-pane-subnets`）**：
  - `#wizard-coverage-note`：顯示掃描涵蓋說明（告知僅涵蓋掃描窗口內有事件回報者）。
  - `#wizard-warnings`：若有警告（如 ESM 權限不足降級警告）以警示方塊列出。
  - `#wizard-select-new`：「全選新主機」按鈕（重設勾選：新主機與可復活主機設為 checked，既有使用中主機設為 unchecked）。
  - `#wizard-select-none`：「全不選」按鈕。
  - 網段摺疊清單（`<details>`，若單一網段主機數超過 20 台預設摺疊）：
    - Summary 標題顯示 CIDR、總台數、已登錄數、可復活數，並具備網段全選核取方塊。
    - 主機方格（Grid 排版）：顯示 `IP 主機名稱`，並依狀態附上徽章：
      - `已登錄`（`host.exists`）：既有主機，預設不勾選。
      - `可復活`（`host.orphanOverlap`）：原屬其他 Sentinel 因刪除/移除而停用之主機，預設勾選。
- **步驟二：群組指派（`#wizard-pane-groups`）**：
  - `#wizard-os`：作業系統下拉選單（`windows` / `linux`，預設為該 Sentinel 之 OS，僅影響本次新增主機）。
  - `#wizard-tier`：分級下拉選單（`core` / `standard` / `test`，預設為 `standard`，僅影響本次新增主機）。
  - 網段群組對應清單（`#wizard-group-assign`）：
    - 下拉選項包含「未分組（僅 admin 可見）」（`skip`）、「既有群組」（`existing:{groupId}`）與「＋ 建立新群組…」（`new`）。
    - 選擇建立新群組時展開輸入框 `#newNameInput` 輸入新群組名稱。
- **提交匯入（`wizardSubmitImport`）**：
  - 呼叫 `POST /api/admin/netiq/import`，送出 Token、選取之 IP 清單、網段群組指派、OS 與 Tier。
  - 匯入成功後跳出 Toast 提示「已匯入：新增 N、更新 M、復活 K」，關閉精靈並自動重載 Sentinel 清單與掃描日誌。

### 3. 最近的掃描匯入紀錄（`#netiq-import-logs`）
- 列出 `kind === 'Netiq'` 的匯入紀錄。
- 欄位包含時間、來源、操作者、結果（新增/更新/復活數及新建群組名稱）。
- 附「全部匯入紀錄」按鈕連結至 `/admin/imports`。

## 頁籤三【診斷（probe）】詳細規範

### 1. 輸入與控制項
- `#probe-sentinel`：Sentinel 下拉選單（必選）。
- `#probe-sample-ip`：樣本 Windows IP（選填，用於核對主機歸屬鍵、頻道覆蓋、時間邊界）。
- `#probe-sample-linux-ip`：樣本 Linux IP（選填）。
- `#probe-start`：「執行診斷」按鈕。
  - 點擊後呼叫 `POST /api/admin/netiq/probe/start`，將按鈕設為 disabled。
  - 背景啟動非同步診斷工作，前端啟動輪詢（每 2 秒向 `GET /api/admin/netiq/probe/status` 查詢狀態）。

### 2. 狀態與輸出
- `#probe-state`：顯示執行狀態與進度訊息（執行中顯示 Spinner 與最新步驟訊息；完成後顯示上次執行時間與成功/錯誤狀態）。
- `#probe-output`：唯讀 `<textarea>`，以等寬字體顯示診斷詳細輸出日誌（自動捲動至底部）。
- `#probe-copy`：「複製」按鈕，點擊將診斷輸出內容複製至剪貼簿。

## 常見問答與邊界狀況（Q&A）

- **Q: 刪除一台 Sentinel 後，原本掛在該 Sentinel 下的主機會遺失嗎？**
  - **A**: 不會遺失。刪除 Sentinel 時，其轄下主機會被設定為停用（`Active = false`）並標記為孤兒主機（`Orphan = true`），過往的所有分析紀錄與稽核軌跡完全保留。管理員可在「系統管理 > 主機」頁中將孤兒主機重新綁定至其他 Sentinel，或在網段掃描精靈中透過「可復活」勾選重新啟用。
- **Q: 為什麼網段掃描時，某些確定在線上的主機沒有被掃描出來？**
  - **A**: NetIQ 事件掃描原理是向 Sentinel 查詢在掃描窗口（近 24 小時等）內有上報事件的日誌來源。若主機雖然開機但在該時段內完全沒有產生並傳送事件至 Sentinel，事件掃描將無法取得該 IP。若帳號具備權限，可開啟「以 ESM 事件來源目錄探索」；若無 ESM 權限，請至「系統管理 > 主機」頁手動登錄該主機。
- **Q: 連線與節流參數中的平行度設定可以調到多大？**
  - **A**: 「同時處理幾台 Sentinel」上限為 3，「單台內查詢平行度」上限為 4。由於分析排程與網站前端 UI 在架構上運行於同一伺服器行程內，若平行度設得過高會耗盡行程執行緒資源，導致前端網頁操作卡頓，因此系統設有硬性上限。
- **Q: 什麼是「可復活」主機？**
  - **A**: 當某台主機過去曾隸屬於某台 Sentinel，後來因該 Sentinel 被刪除或主機被移除而處於停用孤兒狀態時，在後續網段掃描中若再次掃描到該 IP，系統會為其標註「可復活」徽章，並預設勾選。匯入時會自動將其恢復啟用並綁定至當前 Sentinel。
