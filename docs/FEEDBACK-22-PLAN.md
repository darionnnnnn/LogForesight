# 回饋第二十二輪規劃（FEEDBACK-22）

## 0. 背景與範圍

輸入：使用者四項回饋（SQLite 路徑、權限異動彙總、異動內容可讀性、品牌字級間距）。

**已定案決策**（與使用者討論定案 2026-08-20）：
- P1：預設路徑改 `{DataRoot}\Db\logforesight.db`，**不做舊檔搬移**——既有部署啟動時會在新位置建全新空庫，舊檔遺留原地，此後果使用者已知悉並接受。根因處置：同步更新 `Program.cs` 資料檔哨兵與所有文件，避免哨兵失效或文件說謊。
- P2：**移除** 50 筆/主機日上限與「權限異動（彙總）」機制，全部逐則入庫、可查詢。根因：彙總是為降噪而犧牲可查性，使用者要的是完整可查；量增由既有 `created_at` 保留天數清理（`PermissionChangeStore.cs:383`）兜底。
- P3：可讀性只做**通用**處理：前端保留換行（pre-wrap）＋依「key: value 逐行」標準格式自動拆表格；解析不到的行原樣顯示。**不做**使用者自訂關鍵參數機制、**不針對**特定事件類型客製（避免 NetIQ 格式異動即失效）。
- P4：品牌名稱/副標字級放大、副標更貼近名稱；套用 `ui-ux-pro-max` 定值；登入頁 `.lf-login__brand` 同步。
- 順手發現的彙總去重 bug（去重鍵含筆數，重跑產生第二筆）隨 P2 機制移除而消滅，不另修。

**明確不做**：P1 檔案搬移；P2 上限改設定鍵（機制整個移除）；P3 自訂參數機制、SDDL 翻譯、後端 DTO 結構化（純前端展示層）。

## 1. 事實核對摘要

| 項 | 判定 | 證據 |
|---|---|---|
| P1 路徑組裝單點 | ✅ | `StorageBackend.cs:47`；哨兵 `Program.cs:73-85`；文件 README×6 處、WEB-SPEC §（2174 行附近）、appsettings 註解×3 |
| P1 附屬檔 | ✅ | 無工具引用 -wal/-shm；不搬移故無 sidecar 議題 |
| P2 彙總機制 | ✅ | `HostDayPostProcessor.cs:69`（常數 50）、`:179-183`（溢位累加）、`:207-233`（彙總列）；`PermissionChangeRecord.cs:21-26`（ChangeType 合法值含「權限異動（彙總）」） |
| P2 清理兜底 | ✅ | `PermissionChangeStore.cs:383` 依 created_at 清理 |
| P3 行為說明是 NetIQ 原文 | ✅ | `HostDayPostProcessor.cs:175` 只截 500 字；`SentinelFieldMap.cs:36`（msg 欄） |
| P3 前端吞換行 | ✅ | `permission-changes.js:555,590-602` textContent＋無 pre-wrap |
| P3 格式為 key: value 逐行 | ✅ | `PermissionChangeExtractor.cs:290-434` 既有逐行解析器可為參考（中英雙語、全形冒號） |
| P4 CSS 位置 | ✅ | `site.css:483-585`（側欄）、`:1906-1935`（登入頁）；`site.css:93` 字級 20px 上限省略號警告 |

## 2. 作業總覽

委派模型：本輪 P2/P3 委派 agy，模型與兩池額度**開工前查**（gemini-delegate §3.5）後回填：`{model=＿｜Claude池=＿｜Gemini池=＿｜使用者未指派}`。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | SQLite 預設路徑改 Db\ 子資料夾＋哨兵＋文件 | 無 | Claude |
| B | 移除權限異動彙總機制，全逐則入庫 | 無 | agy |
| C | 異動明細前端可讀性（pre-wrap＋通用 key:value 拆欄） | 無 | agy |
| D | 品牌名稱/副標字級與間距（含登入頁） | 無 | Claude（ui-ux-pro-max） |

四作業互不依賴，各自獨立 commit；B 與 C 動同一頁前端的機率低（B 主要在 Core），仍序列執行避免 agy 還原並行編輯。

## 3. 作業明細

### 作業 A（Claude 自做，不外包）
- 契約：`ConnectionString` 空時預設 `Data Source={DataRoot}\Db\logforesight.db`；目錄不存在時建立（`Directory.CreateDirectory` 冪等）。`Program.cs` 哨兵改查 `Db\logforesight.db`。文件同步：README 6 處、WEB-SPEC、appsettings 註解×3。
- 驗收：build 零警告、test 全綠；grep 全庫（排除 bin/obj/archive/Tests 自建路徑）不應再出現「`{DataRoot}` 直下 logforesight.db」語意的描述。

### 作業 B-階段 1：移除彙總機制＋測試（agy）
- **背景**：權限異動每主機日超過 50 筆後改寫一筆彙總列，現要求全部逐則入庫。
- **契約**：
  - 移除 `HostDayPostProcessor` 的 `MaxPermissionChangeRecordsPerHostDay` 上限、溢位累加與彙總列產生邏輯；所有通過去重的異動一律逐則寫入。
  - `PermissionChangeRecord.ChangeType` 合法值註解移除「權限異動（彙總）」；但**既有資料庫中的彙總列不刪除**——它們是歷史事實，前端與查詢需繼續容忍此值存在（不得因未知 ChangeType 而炸）。
  - 逐則去重鍵維持既有 `DedupeKey` 語意不變（重跑冪等）。
- **範圍**：可動 `LogForesight.Core`（HostDayPostProcessor、PermissionChangeRecord 註解）、對應測試。不准動 docs/、前端、其他作業檔案；不順手重構。
- **驗收**：`dotnet build` 零警告、`dotnet test` 全綠。既有彙總相關測試改為證明「超過 50 筆仍逐則入庫」（測試名稱例：`超過既有上限的異動全數逐則寫入`、`重跑同一主機日不產生重複列`）。grep `MaxPermissionChangeRecordsPerHostDay`、「本日另有」應為 0 命中（測試中的歷史相容 fixture 除外，若有需說明）。
- **回報格式**：改動檔案清單（一行一檔）、測試數字（總/綠/紅）、偏離契約處與理由。

### 作業 C-階段 1：明細可讀性（agy）
- **背景**：權限異動明細的「行為說明／異動前／異動後」是多行 key: value 文字（NetIQ 原文或自組），前端 textContent 吞換行致擠成一行。
- **契約**：
  - 三欄位渲染保留原始換行（pre-wrap 或等效）。
  - 新增**通用**逐行解析：符合「`key: value`」（半形/全形冒號皆可；冒號後為空的行視為區段標題）的行，以雙欄表格（key 一欄、value 一欄）呈現，區段標題行以視覺分組呈現；**任何解析不到的行原樣單欄顯示，整體失敗時退回純文字**。解析純屬展示層，不改後端、不改儲存。
  - 不得針對特定 EventId 或特定 key 名寫死分支（通用規則唯一例外：區段標題判定）。
  - 維持 `textContent`／不得引入 innerHTML 插值（專案紅線：AI/外部文字不得被當 HTML 解析）。
  - 長值（如 SDDL）維持 monospace 且可換行，不截斷。
- **範圍**：可動 `LogForesight.Web\wwwroot\js\pages\permission-changes.js`、必要的共用 css/js（新增優於修改共用）。不准動後端、docs/、其他作業檔案。
- **驗收**：`dotnet build` 零警告、`dotnet test` 全綠。若專案無前端測試機制，以 grep 驗收：pre-wrap（或等效 class）出現於三欄位渲染路徑；`innerHTML` 未新增含變數插值的用法。回報需附「多行 key:value 輸入 → 渲染結構」的文字說明。
- **回報格式**：同 B。

### 作業 D（Claude 自做，ui-ux-pro-max）
- 契約：側欄品牌名稱字級微升（注意 `site.css:93` 省略號上限）、副標字級微升且 `margin-top` 縮小使其更貼近名稱；登入頁 `.lf-login__brand` 同步調整；具體值由 ui-ux-pro-max 查詢後定。
- 驗收：build/test 全綠；瀏覽器實看側欄（預設品牌與長名稱兩種）與登入頁，無省略號誤觸發、無溢版。

## 4. 測試計畫

- B：`超過既有上限的異動全數逐則寫入`、`重跑同一主機日不產生重複列`、`既有彙總列的 ChangeType 值仍可通過查詢與 DTO 映射`（防炸）。
- C：無自動化前端測試，依 grep＋人工渲染說明驗收。
- A/D：既有測試全綠＋grep／目視。

## 5. 文件更新（全部驗收後 Claude 寫）

- README：設定表與部署目錄樹改 `Db\logforesight.db`。
- WEB-SPEC：SQLite 路徑描述；權限異動頁「彙總」相關描述移除、補「全數逐則」與明細拆欄行為。
- DB-SPEC：若有彙總相關描述則同步；保留天數清理描述不變。
- 本檔完工後依文件紀律四步收尾進 `docs/archive/`。

## 6. 風險與回滾

- P1：既有部署升級後歷史「消失」（舊檔遺留原地）——已定案接受；文件需在 README 升級注意寫明「舊檔位於 {DataRoot} 直下，如需沿用請手動搬至 Db\」。
- P2：吵雜 DC 量增（實例：單日 9.2 萬則）——保留天數清理兜底；若實測發現查詢頁變慢，另開輪處理索引/分頁（BACKLOG 候補）。
- P3：通用解析對極端格式（value 內含冒號）可能拆錯——契約已規定失敗退回原樣，風險受控。
- 回滾：四作業獨立 commit，單獨 revert 即可。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （待執行） | | | | |
