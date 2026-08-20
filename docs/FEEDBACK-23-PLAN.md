# 回饋第二十三輪規劃（FEEDBACK-23）：權限異動列表白話化

## 0. 背景與範圍

輸入：使用者回饋——權限異動待辦未展開時看不出「哪個帳號被加進哪個群組」這類關鍵訊息。

**已定案決策**（2026-08-20 與使用者討論定案）：
- 採**方案 B**：不拆欄，改良「異動說明」欄為白話主句。根因是句子品質不是欄位數——
  句子沒用操作者、DN 未短名化、Target 有錯置 bug。
- 白話句**不含主機名**（主機欄同列已有）；由後端 `GenerateSummaryText` 統一組
  （既有註解明定後端統一組裝，避免前後端規則漂移）。
- **Target 錯置 bug**：`PermissionChangeExtractor` 剖不出群組／物件名時不再把
  `evt.Source` 塞進 Target（事件來源不是異動對象），改存空字串；句子對空值降級。
  **既有壞資料**（Target 已存成「…事件來源名 (EventId n)」形狀者）在顯示層辨識並同樣降級，
  不動資料庫。
- **DN 短名化**：`CN=xxx,OU=…` 取 CN 值顯示，`DOMAIN\name` 與純短名原樣；後端單一規則點，
  句子與帳號欄共用；完整值留 title 與展開明細。
- **EventId DTO 缺欄**：順手修（展開明細恆顯「—」的既有 bug）。
- 本機監控來源操作者恆缺屬資料源限制：句型降級處理，不硬造。

**明確不做**：拆帳號欄／新增群組欄；新欄排序（後端白名單不擴充）；改動資料庫既有資料。

## 1. 事實核對摘要

| 項 | 判定 | 證據 |
|---|---|---|
| SummaryText 沒用操作者、DN 原樣入句 | ✅ | `PermissionChangeService.GenerateSummaryText`（:423-532）；GroupMember 句用 After??TargetAccount（常為完整 DN） |
| Target 錯置 | ❌bug | `PermissionChangeExtractor.cs:82-88` fallback 填 `evt.Source`；`HostDayPostProcessor.cs:176-177` 傳入 `Microsoft-Windows-Security-Auditing` |
| DTO 無 EventId | ❌bug | `HandlingDtos.cs:561-587` 無此欄；`permission-changes.js:775` 讀 `change.eventId` 恆 undefined |
| 帳號欄無 truncate | ⚠️ | `accountCell`（:476-498）無寬度控制，長 DN 撐版 |
| 白話句範式 | ✅ 可沿用 | `records.js` `issueGroupCell` 主句＋`.lf-issue-explanation` 小字（含 tabIndex tooltip）；`site.css:1468-1481` |
| 兩來源完整度 | ✅ | 本機來源 InitiatorAccount 恆 null；NetIQ 取 sun 常缺；彙總舊列兩帳號欄可能皆 null |
| 排序白名單 | ✅ | `PermissionChangeStore.cs:213-241` 只認 hostname/category/status/detectedat |

## 2. 作業總覽

委派模型：`gemini-3.7-flash-high`（開工前查：Gemini 週限 3%／五小時限 96%；
Claude 池週限 0%——依 §3.5「Claude 池用罄時整輪切回 Gemini」）。
使用者指示「把 agy 額度用完再自己做」仍適用：額度中斷則該段起由 Claude 接手。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | Extractor：Target 不再錯置＋DN 短名化規則（Core 單一規則點） | 無 | agy |
| B | DTO 補欄（EventId＋兩個顯示用短名）＋GenerateSummaryText 白話句改寫 | A（用短名化與空 Target 降級） | agy |
| C | 前端：異動說明欄改白話句範式、帳號欄用短名＋truncate | B（DTO 新欄位） | Claude |

序列執行 A → B → C；A、B 各自可獨立 commit 回滾，C 隨 B 之後。

## 3. 作業明細

### 作業 A-階段 1：Target 語意修正＋帳號顯示短名（agy）
- **背景**：`PermissionChangeExtractor.Extract` 在訊息剖不出群組名／物件名／目標帳戶時，
  把事件來源名（如 `Microsoft-Windows-Security-Auditing (EventId 4756)`）當成 Target 存入，
  下游組句時被當成群組名顯示，語意錯置。另外帳號值常是完整 DN，顯示層需要短名。
- **契約**：
  1. Target 的 fallback 移除：剖不出對象時 Target 為空字串。事件來源與 EventId 本來就存在
     各自欄位，不塞進 Target。
  2. 新增一個**公開的純函式**「帳號顯示短名」：輸入任意帳號字串，`CN=值,…` 形狀取第一個
     CN 的值（值內含逗號跳脫的情境以 AD 慣例處理，處理不了原樣返回）、其他形狀
     （`DOMAIN\name`、`name@domain`、純短名、SID）原樣返回；null/空白返回空。位置放
     Core（顯示層與服務層都會用），不做設定項。
  3. `IsPrivilegedTarget` 等既有以 Target 判斷的邏輯行為不變（Target 變空的列本來也
     命中不了特權關鍵字，確認無測試依賴 fallback 值）。
- **範圍**：`LogForesight.Core`（Extractor 與新函式）＋對應測試。不准動 Web、docs/、前端。
- **驗收**：build 零新警告；test 全綠且**既有測試一支不少**（基準 2397 總／略過 6），
  新增至少 6 支：剖不出對象時 Target 為空、事件來源不再出現在 Target、
  CN 短名化（含多層 OU）、`DOMAIN\name` 原樣、null／空白、含跳脫逗號的 CN 原樣或正確取值。
  grep：Extractor 內不應再出現把 eventSource 組進 target 的邏輯。
- **回報**：改動檔案清單、測試數字、逐測資列出短名化輸入→輸出。

### 作業 B-階段 1：DTO 補欄＋白話句改寫（agy）
- **背景**：列表「異動說明」欄要讓人一眼看懂「誰把哪個帳號加進哪個群組」。句子由後端
  `GenerateSummaryText` 統一組；DTO 缺 EventId（展開明細恆顯「—」）與顯示用短名欄位。
- **契約**：
  1. `PermissionChangeDto` 新增：`EventId`（int，來源為既有模型欄位）、
     `InitiatorAccountDisplay`、`TargetAccountDisplay`（用作業 A 的短名函式；原始完整值
     欄位保留不動）。
  2. `GenerateSummaryText` 逐類別改為白話主句，**句中帳號一律用短名**：
     - 群組成員類（暫定句型）：有操作者「{操作者} 將 {成員} 加入群組 {群組}」／
       「…自群組 {群組} 移除」；無操作者「{成員} 被加入群組 {群組}」；群組名空（含
       顯示層辨識既有壞資料：Target 符合「事件來源名 (EventId n)」形狀視同空）→
       「{成員} 被加入群組（未能解析群組名稱）」。
     - 資料夾權限類：「{操作者 }變更 {路徑} 的權限」（Before/After 是 SDDL，不入句）；
       新增/移除 ACL 規則句型比照（「將 {對象} 的權限規則加入/移除於 {路徑}」暫定）。
     - 擁有者變更：「{路徑} 擁有者由 {前} 變更為 {後}」（維持，補操作者前綴）。
     - 存取狀態／稽核政策：現行句可讀性尚可，補操作者前綴即可（暫定）。
     - 彙總舊列維持現行分支。
     - 所有「{操作者 }」前綴：操作者空則整段省略（不留孤兒空格與連接詞）。
  3. 句型具體措辭標**暫定**：執行端可依實際欄位內容微調語序，但「操作者→動作→對象」
     的資訊順序、帳號用短名、群組名空降級三條是硬契約。用詞遵守 WEB-SPEC §8.6a
     （台灣 IT 慣用詞）。
- **範圍**：`LogForesight.Web`（DTO、PermissionChangeService）＋對應測試。不准動 Core
  （作業 A 已完成的介面直接用）、前端 js、docs/。
- **驗收**：build 零新警告；test 全綠且既有一支不少，新增至少 8 支覆蓋：有/無操作者兩句型、
  群組名空降級、既有壞資料形狀辨識降級、DN 短名入句、EventId 有值、彙總舊列句不變、
  兩顯示欄位空值行為。**真實資料樣本**：以「`CN=33951 [Li Zhihui],OU=1220000000,…` 加入
  `_6110H1220000000`、操作者 `admin_ad.brk`」與「Target=`Microsoft-Windows-Security-Auditing
  (EventId 4756)` 的既有壞列」兩例，回報逐例列出 SummaryText 實際輸出。
- **回報**：同 A。

### 作業 C（Claude 自做）
- 契約：異動說明欄改沿用 `.lf-issue-explanation` 範式（tabIndex tooltip、樣式類別取代
  inline 360px）；帳號欄兩行改用 `*Display` 欄位＋truncate，title 帶完整值；展開明細的
  對象／原始值顯示不變（完整 DN 在明細）。瀏覽器實測含長 DN 列與舊壞資料列。
- 驗收：build/test 全綠；實測截圖確認未展開即可讀出「誰把誰加進哪個群組」。

## 4. 測試計畫

見各作業驗收；C 無自動化前端測試，以瀏覽器實測＋grep 驗收。

## 5. 文件更新（全部驗收後 Claude 寫）

- WEB-SPEC §9.5：異動說明欄白話句規則（資訊順序、短名化、降級句）、帳號欄顯示規則。
- DETECTION-SPEC「權限異動類別」段：Target 語意修正（剖不出對象＝空，不再退事件來源）。
- HelpContent 09：列表閱讀方式一段。
- 本檔完工後歸檔 docs/archive/。

## 6. 風險與回滾

- Target 改存空影響下游以 Target 判斷的邏輯——作業 A 契約已含行為不變驗收；風險殘餘在
  未被測試覆蓋的消費點，終檢做同型 grep。
- 句型改寫讓既有斷言 SummaryText 的測試需改——依「既有測試一支不少」原則只准改斷言值
  不准刪測試，回報逐支列出。
- 三作業獨立 commit，單獨 revert 即可。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （待執行） | | | | |
