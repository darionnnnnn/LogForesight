# 問題檔案（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/admin/issue-owners`。
- **存取權限**：需要 `Maintain` 能力（`Capability.Maintain`，admin 角色或具備 Maintain 權限者可存取）。
- **後端端點**：
  - 列表查詢：`GET /api/admin/issue-owners`，對應服務為 `IssueOwnerAdminService.List`。
  - 近期問題候選清單：`GET /api/admin/issue-owners/recent-issues`，對應服務為 `IssueOwnerAdminService.RecentIssues`。
  - 儲存負責人規則：`PUT /api/admin/issue-owners`，對應服務為 `IssueOwnerAdminService.Upsert`。
  - 設定機房結論：`PUT /api/admin/issue-owners/{source}/{eventId}/conclusion`，對應服務為 `IssueOwnerAdminService.SetConclusion`。
  - 解除機房結論：`DELETE /api/admin/issue-owners/{source}/{eventId}/conclusion`，對應服務為 `IssueOwnerAdminService.ClearConclusion`。
  - 刪除規則：`DELETE /api/admin/issue-owners/{source}/{eventId}`，對應服務為 `IssueOwnerAdminService.Delete`。

## 資料模型與識別鍵設計

- **實體模型**：`IssueProfile`（由 `IIssueOwnerStore` 持久化儲存）。
- **唯一複合鍵**：`(SourceName, EventId)`。
  - **Windows 問題**：`SourceName` ＋ 正整數 `EventId`。顯示格式為 `{SourceName} ({EventId})`，例如 `disk (7)`、`DCOM (10016)`。
  - **Linux 問題**：`EventId` 恆為 `0`。顯示格式由 `FormatDisplayLabel` 格式化為純來源名稱 `{SourceName}`，絕不顯示無意義的 `(0)`。
- **儲存欄位**：
  - `SourceName`（字串，必填，忽略大小寫）。
  - `EventId`（整數，≥ 0）。
  - `OwnerUserIds`（使用者 ID 列表，`List<long>`）。
  - `Note`（備註字串，選填）。
  - `ConclusionStatus`（字串，`resolved` / `wont_fix` / `false_positive` / `known_noise` 或 null）。
  - `ConclusionNote`（字串，設定結論時必填）。
  - `ConcludedById` / `ConcludedByAccount` / `ConcludedAt`（紀錄下結論之人員與時間）。
  - `AutoApply`（布林值，是否在未來夜間分析中自動套用此結論）。
  - `UpdatedAt` / `UpdatedByAccount`（最後修改人員與時間）。

## 控制項與使用者介面細節

### 1. 清單視圖（`#issue-owner-list`）
- **標題工具列**：
  - 標題「問題檔案」。
  - Popover 說明按鈕：說明問題負責人與機房結論的跨主機長期效力。
  - 「新增規則」按鈕（`#issue-owner-new`）：點擊呼叫 `openModal(null)` 開啟彈窗。
- **表格欄位**：
  1. `問題`：由 `issueLabel` 渲染問題標籤（Windows 顯示 `Source (EventId)`，Linux 顯示 `Source`）。
  2. `負責人`：列出 `ownerNames`（`顯示名稱(帳號)` 逗號分隔），若未指派顯示「（無）」。
  3. `近期出現（30 天）`（`text-end`）：顯示 `recentHostCount` 台主機，最近 `recentLastSeen`（YYYY-MM-DD）；若過去 30 天內未出現則顯示「尚未出現」。
  4. `備註`：顯示 `note` 內容。
  5. `機房結論`：
     - 若無結論顯示淡色「（無）」。
     - 若有結論顯示結論狀態文字 `conclusionStatusText` ＋（若 `autoApply` 為 true 顯示「（自動套用中）」）＋ 下方小字 `conclusionNote` ＋ 下方小字 `由 {concludedByDisplayName}({concludedByAccount}) 於 {concludedAt} 設定`。
  6. `更新`：顯示 `updatedAt` 日期時間與更新者帳號/名稱。
  7. `操作`（`stickyLastColumn`）：
     - 編輯按鈕（`pencil` 圖示）：開啟編輯彈窗。
     - 刪除按鈕（`trash` 圖示，`outline-danger`）：跳出確認對話框進行刪除。

### 2. 新增／編輯彈窗（`#issue-owner-modal`）
- **視窗標題（`#issue-owner-modal-title`）**：新增時為「新增問題檔案」，編輯時為「編輯問題檔案」。
- **問題選擇區塊（`#issue-owner-picker-field`）**：
  - 下拉選單 `#issue-owner-picker`：資料來源為 `/api/admin/issue-owners/recent-issues`（近 30 天內出現過的問題，依受影響主機數降冪排序）。選項格式為 `{label} — {hostCount} 台主機`，若已被指派過則後綴 `（已指派）`。
  - 切換按鈕 `#issue-owner-manual-toggle`：「找不到？改為手動輸入」，點擊後隱藏選擇器並展開手動輸入欄位。
- **手動輸入區塊（`#issue-owner-manual-field`）**：
  - `#issue-owner-source`：來源（Source）文字輸入框，如 `disk`、`DCOM`。必填。
  - `#issue-owner-event-id`：Event ID 數字輸入框（`<input type="number" min="0">`）。必填。
- **編輯模式下的鎖定機制**：
  - 當編輯既有規則時，`#issue-owner-source`、`#issue-owner-event-id` 與 `#issue-owner-picker` 均設為 `disabled = true`，且切換手動按鈕被隱藏。此設計旨在防止編輯時誤改鍵值導致搬移規則與歷史對應錯亂。若欲變更問題鍵值，必須刪除後重新建立。
- **已選擇摘要（`#issue-owner-selected-summary`）**：
  - 即時監聽輸入與下拉變更，顯示「已選擇：{displayLabel}」，提供視覺回饋避免使用者儲存前混淆。
- **負責人選單（`#issue-owner-users`）**：
  - 多選核取方塊列表，列出所有狀態為啟用（`active: true`）的使用者。
  - 支援文字過濾搜尋。
- **備註輸入框（`#issue-owner-note`）**：
  - `<textarea rows="2">`，選填。
- **機房結論區塊（`#issue-owner-conclusion-section`）**：
  - **顯示時機**：僅在編輯既有規則（`rule != null`）時顯示；新增規則時此區塊強制隱藏（`d-none`），因為負責人與機房結論為兩支獨立 API，必須先有持久化的 `(Source, EventId)` 記錄方能設定結論。
  - 目前結論提示（`#issue-owner-conclusion-current`）：顯示當前已設定的結論狀態、原因與是否自動套用。
  - 解除結論按鈕（`#issue-owner-conclusion-clear`）：若已有結論時顯示。點擊彈出確認對話框，確認後呼叫 `DELETE .../conclusion` 解除結論。
  - 結論狀態選單（`#issue-owner-conclusion-status`）：
    - `""`（留空）：表示本次儲存不變更機房結論（既有結論維持原樣）。
    - `resolved`：已處理。
    - `wont_fix`：不處理。
    - `false_positive`：誤報。
    - `known_noise`：已知雜訊。
  - 結論詳細欄位（`#issue-owner-conclusion-fields`）：當 status 有選取非空值時展開。
    - `#issue-owner-conclusion-note`：原因文字方塊（必填，若為空送出時會提示「請填寫機房結論的原因」）。
    - `#issue-owner-conclusion-auto-apply`：核取方塊「之後新出現的主機日也自動套用這個結論」。
- **儲存送出邏輯（`form.submit`）**：
  1. 驗證 Source 與 Event ID 是否合法。
  2. 若有選取結論狀態，驗證結論原因是否已填寫。
  3. 呼叫 `PUT /api/admin/issue-owners` 更新負責人名單與備註。
  4. 若有設定機房結論，接續呼叫 `PUT /api/admin/issue-owners/{source}/{eventId}/conclusion` 更新結論。
  5. 顯示 Toast「已儲存問題檔案」，關閉彈窗並重新載入列表。

## 系統業務運作與聯鎖機制

1. **夜間批次分析自動建案**：
   - 當夜間批次分析於某主機某日產出問題時，若該問題命中 `IssueProfile` 且設有 `OwnerUserIds`，系統會自動建立處理案件（`IssueHandling`），並將處理人指派給名單中的**第一位負責人**（進該員「我的交辦」頁）。
2. **警報郵件路由優先權**：
   - 在「系統設定 > 郵件通知」啟用「同時通知負責人」時，系統會逐主機日檢查問題清單。若命中問題負責人規則，郵件改發給問題負責人，不再發給主機負責人；若問題負責人有多位，則一併通知所有問題負責人。
3. **授權穿透與可見範圍**：
   - 使用者一旦被指派為問題負責人，系統在計算其可見主機時（`IVisibilityService`），會自動納入在資料保留天數（`RetentionDays`）內曾出現該問題的所有主機。
   - 該使用者同時自動獲得 `Handle`（處理狀態維護）與 `ConfirmPermission`（權限異動確認）能力。
4. **機房結論自動套用（`AutoApply`）**：
   - 若問題檔案設定了機房結論並開啟 `AutoApply`，夜間分析於新主機日產出該問題時，會自動將該問題的狀態標記為指定結論（如 `known_noise`），並填入設定的原因與標註系統自動套用。
   - 該問題在報表與儀表板的重點排行計算中，若所有受影響主機均已有結論，會被自動排除出重點排行版面，減少背景雜訊干擾。
5. **解除結論之非回滾特性**：
   - 點擊「解除結論」僅會清除 `IssueProfile` 上的結論欄位並將 `AutoApply` 設為 false，未來新發生的事件不再自動套用。
   - 過去已經自動套用並寫入資料庫的歷史主機日紀錄**不會回滾**（誠實保留歷史事實）。若要修改歷史紀錄，需手動至問題查詢頁進行批次或個別狀態變更。
6. **稽核軌跡**：
   - 新增/變更負責人：記錄動作 `issue_owner_update`，記錄變更前後之負責人帳號名單與備註。
   - 設定機房結論：記錄動作 `issue_owner_update`，摘要註明結論類型、原因與是否自動套用。
   - 解除機房結論：記錄動作 `issue_owner_update`，摘要註明「解除問題 {source} {eventId} 的機房結論」。
   - 刪除問題檔案：記錄動作 `issue_owner_delete`，摘要註明原負責人名單。

## 常見問答與邊界狀況（Q&A）

- **Q: 為什麼新增問題檔案時沒有看到設定機房結論的選項？**
  - **A**: 負責人管理與機房結論是兩支職責分立的 API。新增模式下系統尚未持有該問題的 `(Source, EventId)` 主鍵，因此機房結論區塊在新增時隱藏。請先儲存負責人規則，建立問題檔案後，再次點擊列表的「編輯」按鈕即可在彈窗下方看到並設定機房結論。
- **Q: 解除機房結論後，以前被自動標記為「已知雜訊」的主機日會變回「未處理」嗎？**
  - **A**: 不會。系統的設計原則是誠實留痕，歷史已寫入的結案狀態不會自動回滾。解除結論只會讓「未來新出現的主機日」不再自動套用。若要修改過去的紀錄，請到問題查詢頁使用篩選與統一標記功能進行狀態翻案。
- **Q: Linux 問題在介面上該如何設定 Event ID？**
  - **A**: Linux 事件日誌在架構上無 Event ID，系統內部以 `0` 作為識別。在手動新增 Linux 問題時請在 Event ID 欄位填入 `0`，前端列表與標籤會自動格式化為純來源名稱（例如 `sshd`），不會顯示無意義的 `(0)`。
- **Q: 刪除問題檔案後，該問題產生的告警會由誰處理？**
  - **A**: 刪除問題檔案後，該問題不再有跨主機的專屬負責人，夜間分析產生的告警會落回「主機負責人」規則處理（若該主機有設定負責人）。若該問題曾設定過機房結論，刪除問題檔案時機房結論也會一併被移除。
