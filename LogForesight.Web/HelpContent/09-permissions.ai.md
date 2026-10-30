# 授權與角色（AI 參考指引）

## 頁面基本資訊與存取架構

- **頁面路徑**：群組與授權管理 `/admin/groups`；使用者管理 `/admin/users`。
- **存取權限**：使用者與群組管理需要 `Capability.Maintain`。
- **權限異動檢核**另見「權限異動檢核」章（`23-permission-changes.ai.md`）。
- **救援帳號（serverAdmin）**：只有 `Maintain` 與 `ViewAudit`，不屬於任何群組角色；看不到業務資料頁。

## 雙軌授權體系：功能能力 vs 資料可見範圍

LogForesight 將授權嚴格拆分為兩個正交維度：

### 1. 功能能力（Capabilities，決定「你能做什麼動作」）
- 由系統七大能力常數構成：
  1. `ViewAll`：檢視全站所有主機與業務資料。
  2. `Handle`：維護主機日與問題的處理狀態。
  3. `Assign`：指派／改派問題案件的處理人（僅 admin）。
  4. `ConfirmPermission`：確認權限異動檢核（授權操作或標記可疑）。
  5. `Maintain`：維護規則、抑制、主機、使用者、群組、Sentinel 與系統設定。
  6. `DevMonitor`：檢視排程作業、AI 分析狀態、執行監控與除錯日誌。
  7. `ViewAudit`：檢視稽核紀錄與安全性稽核日誌。
- 四大角色映射：
  - `user`：`Handle`、`ConfirmPermission`。
  - `dev`：`ViewAll`、`DevMonitor`。
  - `manager`：`ViewAll`。
  - `admin`：全 7 種能力。
- **多群組聯集**：使用者能力取其所屬啟用中群組的**聯集（Union）**。

### 2. 資料可見範圍（Visibility，決定「你能看到哪些主機」）
系統透過四條獨立路徑解析使用者登入後的資料可見範圍：
1. **群組授權矩陣（Group Authorization Matrix）**：依「群組與授權」頁之授權矩陣勾選決定。
2. **主機負責人（Host Owner）**：是任一啟用中主機的負責人時，自動獲得該主機的檢視權，並自動隱含獲得 `Handle` + `ConfirmPermission` 能力。
3. **問題負責人（Issue Owner）**：在「問題負責與靜音」設定為某問題之負責人時，自動獲得資料保留期內出現該問題之主機的檢視權，同樣自動隱含獲得 `Handle` + `ConfirmPermission` 能力。
4. **交辦授與（Case Assignment Grant）**：被指派為特定問題案件處理人時，獲得對該單一問題的窄檢視權（看不到同日其他問題與 AI 總覽）。
