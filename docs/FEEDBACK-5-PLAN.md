# 回饋第五輪規劃（FEEDBACK-5-PLAN）——介面細節收斂與批次群組維護

2026-07-31 起草；同日依使用者追加需求補入 §9（設定頁頁籤）與 §10（規則庫初始化
缺口，全新環境實測回報）；同日 **Q1~Q6 全數定案**（見 §11 定案紀錄，其中 Q2
「徹底清除 git 歷史」展開為 §2b 獨立作業程序）。**僅規劃，尚未實作**；
實作順序與分支策略見 §12（分支基底已因 Phase 1 併入 dev 而修訂）。

| # | 項目 | 層面 | 規模 |
|---|------|------|------|
| 1 ✅ | 登入／登出鈕文字置中 | Web 前端 CSS | 極小 |
| 2 | 文件與程式碼中的真實環境內容全面通用化（IP／主機名／帳號） | 文件＋Core＋測試 | 中 |
| 3 ✅ | 側欄使用者名稱被遮擋 | Web 前端 CSS | 小 |
| 4 | 已處理過的問題再次發生：加「查看先前處理」按鈕＋modal | Web 前後端 | 中 |
| 5 | 勾選 checkbox 併入處理狀態欄右上角 | — | **第四輪已完成**（§5） |
| 6 ✅ | 常駐說明文字收斂為 icon＋滑過顯示 | Web 前端 | 中 |
| 7 ✅ | modal 全面寬版化與排版檢視 | Web 前端 | 中 |
| 8 | 主機頁批次勾選多台一次加入／修改群組 | Web 前後端 | 中大 |
| 9 ✅ | 設定頁頁籤化＋儲存鈕常駐畫面下方 | Web 前端 | 中 |
| 10 ✅ | 規則庫初始化缺口：全新環境規則頁 500（Web 啟動冪等 bootstrap） | Core＋Web 啟動 | 小 |

---

## 1. 登入／登出鈕文字置中

### 現況與成因

2026-07-22 專業化改版時 `site.css` 把 `.btn` 改成
`display: inline-flex; align-items: center; gap: .4em`（為了圖示＋文字對齊），
但沒有補水平置中——inline-flex 的主軸預設 `justify-content: flex-start`。
一般按鈕寬度貼齊內容看不出差異，**滿寬（`w-100`）按鈕內容全部靠左**：

- 登入頁送出鈕（`Login.cshtml` `#login-submit`，`btn-primary w-100`）
- 側欄登出鈕（`_Layout.cshtml` `#lf-logout`，`btn-outline-light w-100`）
- 處理面板儲存鈕（`handling-panel.js` 的 submit，`btn-primary w-100`）

### 做法

`site.css` 的 `.btn` 補一行 `justify-content: center;`。

### 影響確認

- 非滿寬按鈕：flex 容器寬度貼內容，置中與否無視覺差異——零影響。
- 全站掃過 `w-100` 按鈕僅上述三顆，皆應置中，沒有刻意靠左的滿寬按鈕。
- 主機頁待辦佇列卡（`hosts.js` renderQueues）雖是 `<button class="lf-card w-100 text-start …">`
  但**沒有 `.btn` class**，不受影響。

---

## 2. 真實環境內容通用化

### 盤點結果（2026-07-31 全案掃描）

**真實網段 `10.232.11`**（probe 實測環境）散落 11 個檔案：

| 檔案 | 位置 |
|------|------|
| `LogForesight.Core/Analysis/SentinelQueryBuilder.cs` | 註解＋**使用者可見的錯誤訊息**共 8 處（87/96/97/110/120/121/134/171/186/193 行附近） |
| `LogForesight.Web/Models/Dto/NetiqDtos.cs` | 註解範例 |
| `LogForesight.Web/Services/NetiqDirectoryClient.cs` | 註解範例 ×2 |
| `LogForesight.Web/wwwroot/js/pages/imports.js` | 網段輸入框 placeholder |
| `LogForesight.Web/Views/Pages/Imports.cshtml` | 掃描說明 popover |
| `README.md` §NetIQ | 精靈流程說明 |
| `docs/WEB-SPEC.md` §精靈 | 範例 |
| `docs/WEB-SCHEDULER-PLAN.md` §193 | 範例 |
| `docs/NETIQ-API-PLAN.md` | probe 紀錄多處（235/238/241/417/425 行附近） |
| `LogForesight.Tests/SentinelQueryBuilderTests.cs` | 測資 14 處 |
| `LogForesight.Tests/SentinelRestDirectoryClientTests.cs` | 測資 22 處 |

**真實 DC 主機名與 IP**（probe 第二輪實測輸出，寫進 `docs/NETIQ-API-PLAN.md` §417-455）：

- 主機名 `tc-brkdc01`／`tp-brkdc12`／`tp-brkdc13`／`tp-brkdc21`
- 對應 `repip`：`10.218.9.1`／`10.216.9.2`／`10.216.9.3`／`10.220.8.100`
- 樣本 IP `10.232.11.11`（近 24h System=3、Application=152 等實測數字本身不敏感，IP 要換）

**測試檔中的疑似真實主機名**：`SentinelRestDirectoryClientTests.cs` 的
`tc-crecdc01`／`tp-brkdc01`（測資字串，assert 有比對到，要連動改）。

**疑似真實帳號**：`DOMAIN\wangxm`／`lidh`／`chenyt`／`chenxy`——出現在
`docs/HISTORY.md`（CSV 匯入範例 §1336-1338）與 `Users.cshtml`（form-text 與
placeholder 範例）。是否為真實帳號待確認（§11 Q1），**建議不論真假一律通用化**。

**確認過無問題**：兩份 `appsettings.json` 的 SecretKey／PasswordHash 皆為明確標註的
「公開已知測試值」；`sentinel-a.corp.local`／`10.1.2.x`／`SRV-OO-WEB01` 等均為既有通用範例。

### 替換方案

一律換成專案既有的通用範例體系，讓文件前後一致：

| 真實值 | 替換為 |
|--------|--------|
| 網段 `10.232.11`（含 `.0/24`、`.*`、`.11`） | `10.1.2`（專案文件既有的範例網段） |
| DC 主機名 `tc-brkdc01` 等四台 | `dc01`～`dc04` |
| DC IP `10.218.9.1` 等四個 | `10.1.8.1`～`10.1.8.4` |
| 測試檔 `tc-crecdc01`／`tp-brkdc01` | `srv-dc01`／`srv-dc02`（arrange 與 assert 同步改） |
| `DOMAIN\wangxm` 等帳號 | `DOMAIN\user1`／`user2`…（HISTORY.md 範例的「同主機多列」語意保留） |

- probe 的實測結論文字（欄位對應、筆數量級、耗時）**保留**——那是設計依據，
  只有可識別環境的 IP／主機名要換。
- 測試改完跑整套測試確認綠（測資字串與 assert 是成對的，漏改會直接紅）。
- **本文件自身也要清**：上方盤點表為了指認而列出了真實值，§2 實作的同一個
  commit 內須把本文件的盤點表一併通用化（改為「真實網段」「DC 主機名 ×4」等
  描述性文字＋檔案位置，不留原值）——否則等於把要清的東西集中抄一份進版控。
- 盤點補充（2026-07-31，Phase 1 之後）：`docs/WEB-SCHEDULER-PLAN.md` 的出現處
  在 §1.4.4（網段輸入語法範例），一併替換。

### §2b 歷史徹底清除作業（Q2 定案：要清）

只改現行版本無法移除歷史 commit 裡的舊值——已定案**改寫歷史徹底清除**。
這是一次性的破壞性作業，與 §2 的現行內容通用化**分兩段執行**：

**時點（關鍵約束）**：排在「本輪全部完成併入 master、且樹上沒有任何未合併
feature 分支」之後的空檔執行——改寫歷史必須一次涵蓋所有分支，進行中的分支
在 force push 後全部要重建，趁樹乾淨時做代價最小。順序上即：§2 現行內容
通用化（隨本輪正常 commit）→ 本輪併 dev 驗證 → 併 master →（樹淨空）→
執行本作業。

**工具與步驟**（`git filter-repo --replace-text`，比 BFG 適合字串替換情境）：

1. 在 repo **外**準備 `replacements.txt`：舊值→新值對照（§2 替換表的全部
   項目——網段、四台 DC 主機名、四個 DC IP、測試檔主機名、四個帳號）。
   **此檔案本身就是敏感值清單，絕不入版控、絕不進 repo 目錄，用完即刪。**
2. 全新 `git clone --mirror` 一份，於 mirror 上跑
   `git filter-repo --replace-text replacements.txt`。
3. 驗證：對改寫後的庫逐一 `git log -S"<舊值>" --all` 全庫搜尋，必須全部零命中
   （含 tags）；抽查幾個歷史 commit 的檔案內容確認替換正確（filter-repo 的
   replace-text 預設以 `***REMOVED***` 取代，須在對照檔明確給新值才是換成
   通用範例）。
4. `git push --force --mirror` 回 GitHub（所有 branch＋tags 一次覆蓋）。
5. 兩台開發機的既有 clone **全部作廢重拉**（fresh clone；不要 pull——舊物件
   會透過本地 reflog 留存）。
6. **GitHub 端殘留**：force push 後舊 commit 物件在 GitHub 伺服器端仍可能以
   dangling object 形式暫存一段時間（透過舊 SHA 直連仍可能讀到），且若曾有
   PR 引用會留下 `refs/pull/*`。private repo 下實際暴露面很小；要立即清除
   需向 GitHub Support 申請跑 GC。本規劃採「private repo＋等自然過期」，
   若要申請 GC 屆時再提。

**連帶後果（接受，不另處理）**：

- **全部 commit SHA 改變**。docs/HISTORY.md 與各規劃文件內文引用的歷史
  SHA（`242055b`、`5632f3d`…）將指向不存在的 commit——這些引用是敘事性
  標記而非連結，接受其成為「舊編號」，不逐一改寫（改寫反而破壞歷史紀錄的
  真實性）；本作業完成後在 HISTORY.md 加一條說明「YYYY-MM-DD 歷史改寫，
  此前文件中引用的 commit SHA 為改寫前編號」。
- 既有 clone 的本地分支/未推送內容需在作業前確認皆已處理完畢（時點約束
  已涵蓋）。

---

## 3. 側欄使用者名稱被遮擋

### 現況與成因

`_Layout.cshtml` 的 `#lf-current-user` 掛了 `text-truncate`，但它是
`.lf-sidebar__user`（flex 容器）的子項——flex 子項預設 `min-width: auto`，
不會縮到內容寬度以下，`text-truncate` 的省略號因此永遠不會出現；
名字一長就把版面撐爆、被側欄邊界硬生生裁掉（＝使用者看到的「遮擋」）。
另外 `layout.js` 只把 `title` 設成帳號，滑過看不到完整顯示名。

### 做法

- `site.css`：`.lf-sidebar__user #lf-current-user { min-width: 0; flex: 1 1 auto; }`
  ——讓 `text-truncate` 真正生效，超長名字以「…」收尾、不再被硬裁。
- `layout.js` `renderCurrentUser`：`title` 改成 `顯示名（帳號）`，
  滑過即可看到完整內容（與省略號互補）。

不採「換行顯示完整名字」：側欄 footer 高度固定的視覺節奏比完整顯示重要，
tooltip 已補足完整資訊。

---

## 4. 「查看先前處理」按鈕＋modal

### 需求

風險日詳情中，**之前處理過（結案過）的問題再次發生**時，處理狀態欄提供一顆按鈕，
點開 modal 看先前的處理方式。**「處理中」與「未處理」的歷史不列入**——
使用者要的是「上次怎麼解的」，不是完整流水帳。

### 資料面：判斷「處理過」

既有三層資料都能回答，取用如下：

1. **逐日問題標記 `IssueHandling`**（主資料源）：同主機同 `IssueKey`、
   日期早於本日、狀態屬結案類（`IssueHandlingStatuses.Closed`：
   resolved／wont_fix／false_positive／known_noise）的列。
   每列有 `Note`／`ActorAccount`／`UpdatedAt`／`CaseId`，足以呈現「當時怎麼標的」。
2. **已結案案件 `IssueCase`**（補充摘要）：`ClosedAt != null` 的歷史案件有
   處理人（`HandlerId`）、期間（`FirstLinkedDate`～`LastLinkedDate`）、最後說明快照，
   modal 頂部以「上次案件」摘要呈現，比逐日列更接近「先前處理方式」的答案。
3. 已知雜訊記憶（NoiseMark）**不需要**納入：再次發生時畫面已自動顯示
   「已知雜訊（自動）」徽章＋記憶備註，語意已涵蓋。

### 後端

- **`IIssueHandlingStore` 增查詢**：`List<IssueHandling> GetForHost(string hostName)`
  （或 `GetPriorClosed(hostName, beforeDate)`——建議前者，結案類過濾放 Service，
  與其餘 store「只管持久化、業務規則在呼叫端」的分工一致）。
  單主機的標記列量級小（每風險日最多數十列），整撈可行；SQL 後端有 host 索引。
- **`TopIssueDto` 增欄** `bool HasPriorHandling`：
  `RecordQueryService.GetDetail` 已有該日 `GetForDay`，再以上述查詢一次撈回本主機
  全部標記列，過濾「日期 < 本日 且 狀態屬 Closed」後按 `IssueKey` 建集合，
  逐問題打旗標。已結案案件同理併入判斷（`IIssueCaseStore.GetMany([host])`
  過濾 `ClosedAt != null`）。
- **新端點** `GET /api/records/{hostId}/{date}/handling/issue-history?issueKey=…`
  （issueKey 含 `|`，走 query string 不進路由）。權限沿用 record detail 既有的
  主機檢視檢查。回傳：
  - `cases`: 已結案案件摘要（狀態、處理人名、期間、結案時間、最後說明）
  - `entries`: 逐日結案標記（日期、狀態、說明、操作者、是否出自案件），
    **只含結案類**，日期倒序。

### 前端（record-detail.js）

- `statusCell` 的內容區（`statusControl` 輸出下方）在 `issue.hasPriorHandling`
  時加一顆 `btn btn-sm btn-link p-0`「先前處理」（icon `clock-history`），
  **canHandle 與否都顯示**——唯讀角色同樣需要參考上次解法。
- 點擊 → 打新端點 → `showDetailModal({ size: 'modal-lg' })`：
  - 頂部：案件摘要卡（若有）——「上次由 ○○○ 處理，YYYY-MM-DD 結案（已處理）」＋說明。
  - 下方：逐日標記列表（沿用 handling-panel `renderLogItem` 的視覺語彙：
    狀態徽章＋說明＋日期／操作者），modal 排版遵循 §7 的寬版原則。
- 與既有「已知雜訊（自動）」徽章並存不衝突：雜訊記憶答「這是什麼」，
  本按鈕答「上次怎麼處理的」。

### 影響確認

- 清單頁／儀表板／報表零改動（只動 detail 路徑與新端點）。
- `GetDetail` 多兩次單主機查詢，Sqlite/SqlServer 皆是索引命中的小查詢，
  2000 台規模下無虞（detail 是單主機頁面）。

---

## 5. 勾選 checkbox 併入處理狀態欄——第四輪已完成

`docs/FEEDBACK-4-PLAN.md` §1 已實作完全相同的需求（master@5632f3d，2026-07-31 合併）：
「選取」欄已移除，逐列 checkbox 絕對定位在「處理狀態」欄右上角
（`site.css` `.lf-status-cell__wrap`／`.lf-status-cell__checkbox`，加大點擊範圍至
1.25rem），全選移到表頭右側（`record-detail.js` `statusHeader`）。

**推測使用者測試的是合併前的部署**。本輪不重做，請更新部署後確認；
若指的是其他頁面的表格（如主機詳情「依問題」檢視），再回報具體位置。

---

## 6. 常駐說明收斂為 icon＋滑過顯示

### 現況

全站已有兩套機制：`.lf-hint`（一行式常駐說明）與 `.lf-help`
（icon 鈕開 popover，`layout.js` `initHelpPopovers` 統一初始化，trigger=focus）。
問題是**常駐的太多**：

| 位置 | 常駐說明數 |
|------|-----------|
| Settings.cshtml | form-text ×13 |
| Hosts.cshtml（主機 modal） | form-text ×9 |
| Rules.cshtml | form-text ×9＋lf-hint ×2 |
| Netiq.cshtml（Sentinel modal 等） | form-text ×9 |
| Users.cshtml | form-text ×3 |
| handling-panel.js（唯讀欄提示） | form-text ×3 |
| Imports／PermissionChanges／Groups | 各 1~2 |

### 收斂原則（本輪的判斷標準，逐處套用）

1. **保留常駐**：影響資料正確性或不可逆的警告——例如
   「主機名稱建立後不可修改」「未分組時只有 admin 看得到」「停用後立即無法登入」
   「留空＝不變更（密碼）」。填錯的代價高，不能藏在 icon 後面。
2. **收進 icon**：敘述「這個欄位是幹嘛的／資料從哪來」的說明——例如
   「名單來自 NetIQ 維護頁」「會影響 AI 判讀」「決定套用哪個平台的規則面」。
   看過一次就記得，常駐只是干擾。
3. 已是 `.lf-hint`＋popover 雙層的頁首說明維持現狀（第一層已經夠短）。

### 實作

- `ui.js` 新 helper `helpIcon(content)`：產生 `.lf-help` 按鈕並**自行初始化** popover
  （`layout.js` 的 `initHelpPopovers` 只涵蓋頁面載入時就在 DOM 的節點，
  JS 動態產生的欄位說明要靠 helper 自帶）。
- **trigger 統一改 `'hover focus'`**（現行 focus 需點擊）：滑鼠滑過即顯示、
  移開即收，鍵盤 focus 亦可觸發（可及性）；`layout.js` 與 helper 同步調整。
- cshtml 靜態欄位：form-text 改為 label 旁 `.lf-help` icon
  （`data-bs-toggle="popover" data-bs-content="…"`），逐頁套用上述原則。
- 逐頁清單於實作時整理成對照表附在本文件（哪些留、哪些收，供驗收核對）。

### 驗收對照表（2026-07-31 實作後補記）

**Settings.cshtml**（13 項，6 收 7 留）：

| 欄位 | 處置 | 理由 |
|---|---|---|
| severity-display-mode-hint | 留 | 說明目前選擇的顯示模式後果 |
| 日風險等級「高」鎖定說明 | 留 | 解釋為何按鈕被鎖住無法取消 |
| AI API 位址 | 留 | 「留空會停用 AI」——填錯代價高 |
| AI API 金鑰 hint | 留 | 「留空＝沿用既有金鑰」，plan 明列的 keep 範例 |
| AD 驗證啟用說明 | 留 | 明確不可逆後果＋救援帳號說明，plan 明列的 keep 範例 |
| 歷史資料保留天數 | 留 | 陳述硬性驗證限制（不可小於回補天數） |
| 風險 log 暫存保留天數 | 留 | 陳述硬性驗證限制（不可大於保留天數） |
| AD 伺服器 | 收 | 純格式說明 |
| 查詢基準 DN | 收 | 純格式說明 |
| 查詢過濾器 | 收 | 純格式說明 |
| 首次執行回補天數 | 收 | 純描述，無限制陳述 |
| 執行歷程保留天數 | 收 | 純描述，無限制陳述 |
| 稽核紀錄保留天數 | 收 | 純描述，無限制陳述 |

**Hosts.cshtml**（9 項，5 收 4 留）：主機名稱建立後不可修改／主機群組未分組只有 admin
看得到／負責人不會自動取得檢視權限／批次貼上格式說明（含 `<code>` 排版，popover
`html:false` 保不住排版故維持常駐）**留**；IP 位址／所屬 Sentinel／角色描述／作業系統
類型／批次作業系統**收**。

**Rules.cshtml**（9 項，5 收 4 留＋2 個 lf-hint 維持不變）：規則 Id 命名規則／Program
比對的跨欄位必填提示／訊息子字串留空語意／命中即列高風險日的後果說明**留**；來源比對
範例／正規化事件名／次數門檻／知識庫用途／抑制原因用途**收**。順手清掉 4 處已被 JS
覆寫、markup 上卻寫死 `data-bs-trigger="focus"` 的過期屬性（Imports／PermissionChanges／
Rules ×2）。

**Netiq.cshtml**（9 項，5 收 4 留）：每次執行回補天數與平行度的營運調校警告／詢問 AI
即時查詢的負載提醒／Sentinel 密碼「留空＝不變更」**留**；節流間隔／單一查詢上限／
Sentinel 名稱／探索帳號／作業系統**收**。

**Users.cshtml**（3 項，2 收 1 留）：帳號建立後不可修改**留**；批次帳號格式說明／
群組作用說明**收**。

**handling-panel.js**（4 個候選，2 收 2 留）：目前狀態的雙軌語意提醒／處理人唯讀原因
**留**；主機負責人的「去哪改」提示／處理人可另指派他人的澄清**收**（`readonlyField`
新增 `hintAsIcon` 參數控制，`assignField` 直接掛 `helpIcon`）。

**Imports／PermissionChanges／Groups**：確認後**皆維持現狀**——Imports／
PermissionChanges 的頁首說明已是 `.lf-hint`＋popover 雙層（原則 3 豁免）；
PermissionChanges 的「必填：請說明可疑之處」動態提示留（陳述是否必填，填錯代價高）；
Groups 的角色說明（builtin 群組角色鎖定原因）留（解釋鎖定 UI 狀態）。

---

## 7. modal 全面寬版化

### 盤點與處置

| Modal | 現況 | 處置 |
|-------|------|------|
| `user-modal`（Users） | 預設寬、6 組欄位直排一長條 | `modal-lg`＋`row g-3 col-md-6` 兩欄（帳號／顯示名稱、Email／啟用；群組清單滿寬） |
| `group-modal`（Groups） | 預設寬、直排 | `modal-lg`＋兩欄（名稱／角色；主機群組授權清單滿寬） |
| `sentinel-modal`（Netiq） | 預設寬、5 組欄位直排 | `modal-lg`＋兩欄（名稱／連線位址、探索帳號／密碼、OS） |
| `suppress-modal`（Rules） | 預設寬 | `modal-lg`＋兩欄（主機／生效天數；原因滿寬） |
| `confirm-modal`（PermissionChanges） | 預設寬；異動明細多筆時一長條 | `modal-lg`（明細區塊可讀性優先） |
| `chart-picker-modal`（Reports) | 預設寬、選項直排 | `modal-lg`＋選項改兩欄 grid |
| `host-modal`／`bulk-modal`／`members-modal`／`rule-modal`／`restore-modal`／`netiq-wizard-modal` | 已 `modal-lg`／`modal-xl` | 不動 |
| `showDetailModal` 呼叫端（處理歷程／批次指派／原始訊息／放大檢視） | 已 `modal-lg`／fullscreen | 不動 |
| `confirmAction` 二次確認框 | 預設寬 | 不動——短訊息寬版反而鬆散 |

### 原則入規範

`docs/WEB-SPEC.md` §8.6 補一條：**表單 modal 欄位 ≥3 組即 `modal-lg`＋兩欄 grid；
檢視型 modal 一律 `modal-lg` 起跳**；內容仍可能超高者加 `modal-dialog-scrollable`。
新 modal 一律照此，不再出現「細細一長排」。

### 影響確認

- 寬版後兩欄排列在 <992px（`modal-lg` 斷點以下）自動退回單欄（Bootstrap grid 原生行為），
  窄螢幕不受影響。
- `trackUnsaved` 與各表單的提交邏輯只認 id，不受排版變動影響。

---

## 8. 主機頁批次勾選改群組

### 需求

已加入的主機能一次勾選多台，批次「加入指定群組」或「修改（取代）群組」。

### 前端（hosts.js／Hosts.cshtml）

- 表格首欄加勾選欄：逐列 checkbox＋表頭全選（**沿用 record-detail 第四輪的視覺與
  互動模式**；主機頁欄位多、不需要併進其他欄，獨立首欄即可）。
  `mergedInto` 的列不給 checkbox（已併入的主機群組無意義）；停用主機可勾（改群組合理）。
- **選取跨頁保留**（`Set<hostId>`）：伺服器端分頁下，使用者常要跨頁勾選；
  工具列出現「已選 N 台」計數＋「批次設定群組」＋「清除選取」，
  翻頁／篩選不清空，僅「清除選取」與套用成功後清空。
- 新「批次設定群組」modal（遵循 §7：`modal-lg`）：
  - 頂部：已勾主機清單（沿用 `.lf-bulk-assign-hosts` 限高捲動樣式，
    顯示主機名＋現有群組徽章——套用前看得到「會動到誰」）。
  - 模式單選：**加入**（既有群組保留，勾選的群組加上去）／
    **取代**（改為僅勾選的群組；會導致未分組時紅字警告「N 台將變成未分組，
    只有 admin 看得到」）。
  - 群組 checkbox 清單（`checkboxList` 既有 helper）。

### 後端

- **新端點** `PUT /api/admin/hosts/groups/batch`
  `{ hostIds: long[], groupIds: long[], mode: "add" | "replace" }`（Maintain 權限）。
  不逐台呼叫既有 `PUT /hosts/{id}/groups`——50 台就是 50 個請求＋50 次
  blob 讀改寫，批次端點一次 Mutate 完成。
- `HostAdminService` 增 `SetGroupsBatch`：逐台算出新群組集合
  （add＝聯集；replace＝直接取代），略過 `mergedInto` 主機（回報略過清單）。
- **稽核**：寫一筆彙總 audit（動作、模式、群組名清單、主機名清單、台數）——
  群組異動改變可見範圍，是既有稽核明確要求查得到的事；
  彙總一筆比 50 筆散列更能回答「那天那次批次改了什麼」。
- 回傳 `{ updatedCount, skipped: [{hostName, reason}] }`，前端 toast＋重載。

### 影響確認

- 群組變更的下游（可見性、未分組告警、群組 chip 篩選）全部讀既有資料，零改動。
- 與 NetIQ 匯入精靈的「依網段指派群組」不重疊：那是匯入時、這是事後維護。

---

## 9. 設定頁頁籤化＋儲存鈕常駐

### 需求

設定項目多且長（四張卡一路往下），使用者要捲到底才找得到儲存鈕。頁面頂部加
頁籤點選切換各設定區，儲存鈕常駐畫面下方。

### 做法（沿用規則頁既有的手作頁籤模式，不引入新機制）

- **頁籤**：`Settings.cshtml` 現有四張 `lf-card` 對應四個頁籤——
  「層級與顯示｜AI 服務｜AD 驗證｜資料保留」。標記沿用規則頁的
  `<ul class="nav nav-tabs" id="settings-tabs">`＋`data-tab` 按鈕，每張卡外包
  `<section data-panel="…">`，settings.js 依 rules.js 同款寫法切換顯示/隱藏
  （純顯示層，DOM 結構與欄位 id 全不變）。
- **單一 form 維持不變**：後端 `PUT api/admin/settings` 是整份物件更新，頁籤只是
  顯示分區，儲存仍一次送出全部四區——拆成逐頁籤儲存需要 partial update 語意與
  API 改動，本輪不做（也避免「A 頁籤改了沒存就切去 B 存」的半套狀態）。
- **儲存列常駐**：儲存鈕＋`#settings-updated` 最後更新資訊包進
  `.lf-settings-footer`（`position: sticky; bottom: 0;`＋背景色＋上邊框——
  側欄已有 sticky 先例），任何頁籤、任何捲動位置都看得到；未儲存離開提醒
  仍由既有 `trackUnsaved` 負責，不重做。
- **隱藏頁籤的驗證陷阱（實作重點）**：HTML5 `required`/`min`/`max` 驗證失敗時，
  瀏覽器無法 focus 藏在非作用中頁籤（`d-none`）裡的欄位，submit 會**靜默失敗**。
  處理：submit handler 先 `form.checkValidity()`，不過就找第一個 `:invalid`
  欄位、先切換到它所在的頁籤、再 `reportValidity()`；既有的 JS 層 toast 驗證
  （保留天數大小關係、AD 伺服器必填）同樣先切到對應頁籤再 toast。
- AD 測試連線區塊本來就在 AD 卡內，隨頁籤走，零改動。
- 選配不做：URL hash 深連結（`#tab=ai`）——目前沒有任何跨頁導向到特定設定區的
  需求，加了是無消費端的功能。

### 影響確認

- settings.js 只加「頁籤切換」與「驗證跳籤」，載入/收集/儲存邏輯與欄位 id 全不變。
- 與 §6（說明收斂）動同一批檔案：§9 先定結構、§6 再收說明（順序見 §12）。
- 與 WEB-SCHEDULER-PLAN Phase 3 的關係：排程設定依該規劃 §1.4.5 放**執行監控頁**
  （設定與執行中狀態/停止鈕就近）；若最終決定改放設定頁（見 §11 Q6），頁籤結構
  讓它有現成位置——本輪**不**預留空頁籤。

### 實作定案與規劃的差異（2026-07-31 實作後補記）

三處與規劃字面不同：

1. **不用 `<section data-panel>` 包卡片，`data-panel` 直接掛在既有的 `.lf-card`
   div 上**——`ui.js` 的 `bindTabs(tabsEl)` 用 `tabsEl.parentElement.querySelectorAll
   ('[data-panel]')` 找面板，屬性掛在誰身上不影響查詢，多包一層 `<section>` 純屬
   多餘標記。
2. **「HTML5 驗證陷阱」實際不成立，因此未實作 `checkValidity`/`reportValidity`
   跳籤**：`Settings.cshtml` 的 `<form id="settings-form" novalidate>` 早已存在
   `novalidate`——瀏覽器原生驗證本來就被關閉，`required`/`min`/`max` 只是語意/
   UX 標記，submit 事件不受影響地直接進 JS handler。真正需要跳籤的只有既有的
   4 處 JS 層 toast 驗證（已依規劃處理）。
3. **踩到 `bindTabs` 沒有初始化行為的坑，已修正**：`bindTabs` 只在點擊時切換
   `.d-none`，不會在載入時套用初始狀態——非預設頁籤的卡片**必須在 Razor 標記
   本身就帶 `d-none`**（3 張非預設卡片：`ai`／`ad`／`retention`），否則四張卡
   全部同時顯示。這與規則頁 `Rules.cshtml` 的既有寫法（`data-panel="suppressions"
   class="d-none"`）一致，規劃文件當時沒寫清楚這個前提，補記於此避免下次頁籤化
   其他頁面時重踩。

實機驗證（dev server）：頁籤切換正確隱藏/顯示對應卡片；儲存列
`position: sticky` 確認生效；驗證失敗時（測試案例：初次回補天數設超過歷史
保留天數）從非對應頁籤送出，確認自動跳轉到欄位所在頁籤再顯示 toast；跳籤點擊
不觸發未儲存離開提醒（`#settings-tabs` 在 `<form>` 外，click 不冒泡進表單）。

---

## 10. 規則庫初始化缺口：全新環境規則頁 500

### 現況與成因（2026-07-31 使用者於全新環境實測回報）

錯誤鏈：`lf_blobs` 無 `rules` key → `KnownIssueRuleStore.Load()` 回「檔案不存在」
→ `RuleAdminService.LoadContent()` 拋 `InvalidOperationException` → 規則維護頁
API 500。`rules` blob **只有批次的 `RuleBootstrapper.Run`（Program.cs 啟動）會
初始化**，Web 開站即假設「批次至少跑過一次」；全新 clone＋全新 DB（Web 啟動的
EnsureCreated 只建空表）必炸。

- **非 Phase 1 迴歸**：缺口自 2026-07-24 規則入庫起即存在。
- 全站只有規則維護頁受害：`RecordQueryService` 讀規則失敗會優雅降級用內建種子，
  `RuleAdminService.LoadContent` 直接拋（該頁是編輯入口，靜默用種子反而危險，
  拋錯本身是對的——錯在環境不該走到這裡）。
- 立即解法（不等本修正）：該環境跑一次批次 `LogForesight.exe` 即恢復。
- console 全退役（WEB-SCHEDULER-PLAN Phase 5）後 Web 本來就必須自己 bootstrap，
  本修正等於把該規劃 §1.4.1 搬遷清單中 RuleBootstrapper 的那一項**提前做掉**。

### 做法

1. `RuleBootstrapper.cs` 自 console 搬 Core：namespace 本來就是 `LogForesight`、
   內容零改動（Core 已有 `Console.WriteLine` 先例——AIService 重試訊息），
   console 呼叫端與既有 `RuleBootstrapperTests` 全部不動。
2. Web `Program.cs` 的「啟動時的資料準備」區段（`EnsureSeedGroups` 旁）新增，
   整段 try/catch：bootstrap 失敗記 Error、**不擋站台啟動**（規則頁屆時顯示
   原錯誤，其他頁照常）：
   - `RuleBootstrapper.Run(ruleStore)`——冪等：blob 存在只載入＋驗證（遮蔽
     警告、跳過條目進 Web 啟動 log，與批次同一套），不存在才寫入內建種子；
   - `RuleSeedStore.Sync(...)` 原廠種子鏡像同步（同批次 Program.cs 作法）——
     順手補掉同類缺口：目前全新環境按「回復預設」會叫使用者「先執行一次批次」。
3. 併發安全不需新機制：批次與 Web 同時首啟同時寫種子——blob 樂觀鎖＋重試已
   涵蓋，且兩邊寫入內容相同，無害。
4. 測試：空 blob store → bootstrap → `RuleAdminService.GetRules`／
   `GetSuppressions` 正常（走 EfSqliteFixture 的整合測試）。
5. 文件：README 與 WEB-SPEC §9.7 補「Web 啟動也會冪等初始化規則庫，全新環境
   不再需要先跑批次」；WEB-SCHEDULER-PLAN §1.4.1 標注 RuleBootstrapper 已提前
   搬遷；Web 啟動的「資料根目錄底下找不到資料檔→規則頁會空白」警告文字順手
   修正（bootstrap 後規則頁不再空白，剩分析資料空白的提醒仍有效）。

### 影響確認

- console 行為逐字不變（呼叫同一份 Core 邏輯）。
- Web 啟動多一次 blob 讀取（毫秒級）；規則維護頁在任何環境不再 500。

---

## 11. 定案紀錄（2026-07-31 使用者全數拍板）

| # | 問題 | 定案 |
|---|------|------|
| Q1 | `DOMAIN\wangxm` 等四個帳號是否真實？ | **不論真假一律換**成通用範例（`user1`～`user4`，§2 替換表） |
| Q2 | Git 歷史中的真實值要不要徹底清除？ | **徹底清除、改為通用範例**——作業程序、時點約束與連帶後果見 §2b（於本輪全部併入 master、樹上無未合併分支後執行） |
| Q3 | #5 是否為舊版部署造成的誤會？ | **等本輪全部修改完成後再一併測試確認**（不單獨驗證）；本輪不重做 #5 |
| Q4 | #8 批次模式要不要第三種「移除指定群組」？ | **依建議**：只做加入／取代兩種，移除以「取代」達成 |
| Q5 | #6 的保留／收斂清單要不要先逐條核可？ | **實作後驗收**：依 §6 原則實作時判斷，實作時整理成對照表附於本文件供驗收核對 |
| Q6 | 排程設定 UI（WEB-SCHEDULER-PLAN Phase 3）位置？ | **維持規劃的執行監控頁**（§1.4.5 不變）；§9 設定頁不預留排程頁籤 |

## 12. 實作順序與分支

1. ✅ §10 規則庫初始化修正（獨立、最小、使用者測試環境馬上受益——**先做先出**）
2. ✅ §1＋§3（CSS 小修，一個 commit）
3. ✅ §9 設定頁頁籤（先定頁面結構，§6 才不會改兩次；實作差異見 §9 末段）
4. ✅ §7 modal 寬版化（純前端，無 API 變動；6 個 modal 皆改 modal-lg 兩欄，
   chart-picker-modal 的選項清單由 JS 動態產生，額外改 reports.js 包 col）
5. ✅ §6 說明收斂（依賴 §7／§9 定稿的排版；對照表已附於 §6 末段）
6. §2 通用化（獨立、跨檔案多，單獨 commit 方便 review；測試須綠；含本文件
   自身盤點表的通用化）
7. §4 先前處理 modal（前後端，含新端點與測試）
8. §8 批次群組（前後端，含新端點與測試）
9. 全案體檢 → 併 dev → **使用者一次性總驗證**（Q3 定案：#5 等全部改完再一併
   測，Phase 1 風險 log 暫存也在同一關）→ 併 master
10. **§2b 歷史改寫作業**（獨立於本輪 commit 之外的一次性作業）：master 收齊、
    樹上無未合併分支後執行——mirror clone → filter-repo → 驗證零殘留 →
    force push → 兩台開發機重新 clone → HISTORY.md 補改寫說明

**分支（2026-07-31 修訂）**：原「從 master 開」的前提已不成立——§9 與 §10 都會
動到 Phase 1 剛改過的檔案（`Settings.cshtml`／`settings.js`／Web `Program.cs`
啟動區），且使用者正在 dev 上驗證、§10 正是為了讓那個驗證環境能用。改為
**從 dev（cfcf908，含 Phase 1）開 `feature/feedback-round5`**；完成後併回 dev
一起驗證，確認無誤後 Phase 1＋本輪一併併 master（既定流程不變，只是同一關
過閘）。替代案「仍從 master 開、合併時解衝突」不建議：衝突必然發生，
且 dev 驗證本來就是同一道關卡，分開開分支沒有換到任何隔離效益。
