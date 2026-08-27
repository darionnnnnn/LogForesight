# 規則維護（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：`/admin/rules`（左側主選單為「規則維護」）。
- **分頁結構**：
  1. `Windows 規則`：維護適用於 Windows 事件日誌之偵測規則。
  2. `Linux 規則`：維護適用於 Linux syslog 之偵測規則。
  3. `告警抑制`：維護針對規則、簽章、關聯模式與總量之抑制設定。
- **存取權限**：需要 `Capability.Maintain`（僅 `admin` 與 `serverAdmin` 角色具備）。
- **後端端點**：
  - 規則清單查詢：`GET /api/rules`，對應 `RulesController.GetRules` 與 `RuleAdminService.GetRules`。
  - 即時語法與遮蔽驗證：`POST /api/rules/validate`，接收規則內容回傳驗證結果與遮蔽警告（不寫入）。
  - 儲存規則（新增/修改）：`POST /api/rules`，對應 `RuleAdminService.SaveRule`。
  - 啟用／停用切換：`PUT /api/rules/{ruleId}/enabled`，對應 `RuleAdminService.SetEnabled`。
  - 刪除規則：`DELETE /api/rules/{ruleId}`，僅允許刪除 `custom-` 開頭之自訂規則。
  - 回復原廠預設預覽：`GET /api/rules/{ruleId}/restore-preview`，回傳原廠與當前設定之前後對照。
  - 執行回復預設：`POST /api/rules/{ruleId}/restore`，對應 `RuleAdminService.RestoreSeed`。
  - 內建規則改版狀態／預覽／套用：`GET /api/rules/import-status`、`GET /api/rules/import-preview?overwriteBuiltin={bool}`、`POST /api/rules/import-apply`。

## 比對順序語意與遮蔽偵測機制

### 1. 比對順序（First Match Wins）
- 系統在批次分析進行規則比對（`KnownIssueCatalog.Classify`）時，嚴格依照規則清單由上而下的順序逐條比對，**第一個命中的規則生效**並賦予事件類別與知識庫內容。
- **清單順序決定機制**：順序完全由規則建立之先後順序決定（新增之規則預設附加於清單末端），介面刻意不支援隨意手動拖曳排序，以確保多主機批次比對行為的絕對穩定與可重現性。

### 2. 遮蔽偵測（Shadowing Detection）
- 當一條新規則或修改後的規則，其比對條件（如來源名稱與 Event ID 範圍）完全落在排在其前面的另一條已啟用規則涵蓋範圍之內時，該規則將永遠不會被執行命中（即被前面規則「遮蔽」）。
- **儲存前即時警告**：後端 `RuleValidator` 會在呼叫 `POST /api/rules/validate` 或儲存時進行集合包含運算。若偵測到遮蔽現象，系統會於介面跳出黃色警示，提示管理者調整比對範圍或順序。

### 3. 雙平台獨立比對
- Windows 規則與 Linux 規則在底層為各自獨立的規則清單與比對管線（`FindRule` vs `FindLinuxRule`），**兩平台規則互不干擾、絕不互相遮蔽**。

## 雙平台規則模型（`KnownIssueRule`）

規則實體模型存儲於資料庫 Blob（`lf_blobs`，key 為 `rules`），定義如下欄位：

### 1. 通用欄位
- `Id`（字串，必填，唯一主鍵）：
  - 內建規則固定以 `builtin-{類別}-{代表事件}` 命名（如 `builtin-storage-disk-io`）。
  - 自訂規則強制必須以 `custom-` 開頭（如 `custom-app-timeout`）。
  - **永久性原則**：規則 `Id` 一經建立永不變更，作為歷史紀錄、問題檔案與抑制設定關聯之錨點。
- `Origin`（字串）：`builtin`（原廠內建）或 `custom`（使用者自訂）。
- `Enabled`（布林值）：是否啟用。
- `Category`（字串，8 大類別）：`storage`（儲存裝置）、`hardware`（硬體）、`security`（安全）、`service`（服務）、`backup`（備份）、`config`（設定）、`resource`（資源）、`other`（其他）。
- `Severity`（字串）：`High`（高）、`Medium`（中）、`Low`（低）。
- `ElevatesDayRisk`（布林值，**重大旗標**）：
  - 若設為 `true`，只要該主機日命中此規則（且未被抑制），**當天強制直接判定為「高風險日」**。
  - 用於磁碟壞軌（disk 7/11）、安全日誌被清除（1102）等不可忽視的重大危機。
- **四個靜態知識庫欄位**：
  - `Summary`（白話說明）：用一句話說明此事件代表的意義。
  - `Impact`（影響範圍）：說明對系統可用性或安全性的潛在衝擊。
  - `Causes`（常見原因）：列舉導致此事件發生的常見根因。
  - `Remediation`（處置步驟）：提供維運工程師具體排查與修復指引。

### 2. Windows 專用比對欄位（`Platform = "windows"`）
- `SourcePattern`（字串，必填）：事件來源名稱，採不分大小寫子字串比對（如 `disk`、`Ntfs`、`Security-Auditing`）。
- `EventIds`（整數陣列）：比對的 Event ID 清單。
- `MatchAllEventIds`（布林值）：顯式宣告「只要來源名稱命中，不論 Event ID 為何均算命中」（如 `WHEA-Logger` 全硬體事件）。
  - **顯式防護設計**：系統嚴禁以空陣列隱含全比對，`RuleValidator` 強制要求若 `MatchAllEventIds` 為 false，則 `EventIds` 必須非空，防止誤刪 Event ID 導致比對範圍意外放大。

### 3. Linux 專用比對欄位（`Platform = "linux"`）
- `ProgramPattern`（字串，必填）：syslog 的程式名稱（如 `sshd`、`sudo`、`kernel`）。
  - **字元集限制**：僅接受英數字與 `_`、`.`、`-`。
  - **Lucene 查詢直通**：此欄位會直通 Sentinel 的 Lucene 查詢語法（`sp:{pattern}*`），包含空格或特殊符號會導致夜間查詢語法損毀。
  - **子字串順序限制**：`ProgramPattern` 採子字串比對，具體名稱（如 `sudo`）必須排在泛用名稱（如 `su`）之前。
- `MessagePatterns`（字串陣列）：比對 syslog 訊息內容的關鍵字子字串。
- `EventNamePattern`（字串，選填）：正規化事件名稱。

## 停用、遮蔽與抑制三者之語意邊界

此三個概念經常被混淆，底層生效層級完全不同：

| 動作／狀態 | 設定位置 | 關閉或影響的項目 | 完全不影響的項目（照常運作） |
|---|---|---|---|
| **停用規則**<br>（`Enabled = false`） | 規則維護頁<br>各規則之啟用開關 | 關閉該規則的「分類」與「靜態知識庫說明」（事件退回未命中規則之 Other 類別）。 | 事件照常被聚合統計、`TrendAnalyzer` 照常計算歷史頻率與升級、`CorrelationAnalyzer` 跨 log 關聯鏈照常偵測。事件絕不會憑空消失。 |
| **規則遮蔽**<br>（Shadowed） | 存檔時由系統自動<br>比對偵測的警告 | 該規則**永遠不會被命中**（被排在前面更廣泛的規則完全攔截）。 | 無任何元件被關閉，純粹為規則順序或比對範圍之邏輯衝突，需人工調整。 |
| **告警抑制**<br>（Suppression） | 「告警抑制」分頁<br>或詳情頁抑制捷徑 | 關閉通知與風險等級升級（排除於高/中風險日計算，不發送警報郵件）。 | 事件照常聚合、規則照常命中並記錄 `RuleId`、知識庫說明照常顯示、完整紀錄照常入庫；詳情頁收合於「已抑制告警」區塊中。 |

**精簡總結**：
- **停用**：這條規則不要分類與知識庫了。
- **遮蔽**：這條規則排錯位置，永遠輪不到它執行。
- **抑制**：這個訊號我知道是雜訊，記錄照留但不要發警報吵我。

## 修改內建規則之最佳實踐

1. **嚴禁直接編輯 `builtin-*` 內建規則**：
   - 若直接修改內建規則的內容，日後系統改版升級並套用新種子時，使用者的自訂修改可能會被原廠版本覆蓋。
2. **標準作業流程（SOP）**：
   - 步驟一：將該條 `builtin-*` 內建規則設為**停用（Enabled = false）**。
   - 步驟二：點擊「新增規則」，建立一條 `custom-` 開頭的新規則，填入調整後的門檻或文字內容。
   - 步驟三：`custom-` 規則永遠不會被任何原廠改版流程觸碰或覆寫。
3. **回復原廠預設（Restore Seed）**：
   - 若不慎改動了內建規則，可點擊「回復預設」按鈕。
   - 系統提供差異預覽（`restore-preview`），確認後將該規則還原為原廠出廠設定。

## 內建規則改版升級流程

當系統核心更新引入新版內建規則庫時，頁面頂部會出現升級提示橫幅「內建規則有更新（vX → vY）」：
1. 點擊「預覽差異」，彈窗列出新舊版本比對清單（包含新增規則、更新內容、略過項目與衝突項目）。
2. **「覆蓋已修改的內建規則」核取方塊**：
   - 未勾選：僅新增系統缺少的新內建規則，使用者修改過的內建規則維持現狀。
   - 勾選：以新版內容強制更新所有內建規則，但**嚴格保留使用者對該規則設定的 `Enabled` 啟用/停用狀態**（不會悄悄開啟被停用的規則）。
3. 確認無誤後點擊「套用」，立即將新規則寫入資料庫並重新載入記憶體。

## 規則異動之生效時間點

- 規則的新增、修改、停用與刪除，**僅影響「儲存之後」發起的分析執行**。
- 歷史已經分析完成並寫入資料庫的主機日紀錄（包含日風險等級、規則命中結果與報告全文）**屬於事後不可改寫的證據層，絕不進行回溯修改**。

## 常見問答與邊界狀況（Q&A）

- **Q: 為什麼我把一條磁碟規則停用後，那台主機當天仍然被標記為「高風險日」？**
  - **A**: 停用規則只會移除該規則的分類與知識庫說明。若該磁碟錯誤在當天伴隨有「非預期關機（Event 6008）」或觸發了「儲存連鎖」跨 log 關聯模式（`CorrelationAnalyzer`），關聯層仍會確定性將當天判定為高風險日。
- **Q: 為什麼新增自訂規則時，系統拒絕讓我將 ID 命名為 `builtin-myapp`？**
  - **A**: `builtin-` 前綴為原廠內建規則的保留命名空間，專供系統版本升級與原廠種子同步識別使用。自訂規則一律必須以 `custom-` 開頭（例如 `custom-myapp`），以避免日後原廠規則改版時發生主鍵碰撞。
- **Q: 在 Linux 規則中設定 `ProgramPattern` 為 `sshd: ` 為什麼儲存時報錯？**
  - **A**: Linux 的 `ProgramPattern` 欄位會直接代入 NetIQ Sentinel 的 Lucene 搜尋語法。冒號 `:` 與空格屬於 Lucene 語法保留字元，填入會破壞夜間背景取數查詢。請填入純程式識別名稱（如 `sshd`），訊息內容特徵請填寫於 `MessagePatterns` 欄位中。
