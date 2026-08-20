# 權限異動待辦頁面改版規劃

## 0. 背景與範圍

### 輸入

| 編號 | 需求 |
|---|---|
| P1 | 加上顯示用篩選條件，可搜尋網段、帳號、主機等資訊 |
| P2 | 資訊區塊重點顯示主機(IP)、帳號、行為說明等易辨識資訊 |
| P3 | 加上分頁與排序功能 |
| P4 | 預設依時間排序，最新在最上面 |
| P5 | 標記總筆數 |
| P6 | 說明這個異動改動了什麼；自定義類別、提示屬於哪種類別且可篩選 |
| P7 | 可一次選擇某種類別、全部打勾、一次性送出通過審核 |
| P8 | 表格需支援一鍵全部展開／收合 |

### 已定案決策（含根因）

1. **根因一：這張表根本不是表。** 權限異動存成 append-only JSONL 塞進 `lf_log_lines`，欄位全埋在 JSON 字串內、無索引，`Query`／`Get`／`CountPending` 每次都整份反序列化後記憶體 LINQ。在此結構上做分頁只是把畫面問題蓋掉，後端反而每頁全掃一次。
   → **正規化為 `lf_permission_changes` 真表**，篩選／排序／分頁全部下推 SQL。
2. **根因二：要顯示的欄位不存在。** IP 沒有、帳號沒有結構化欄位（被塞進 Before/After 字串）、行為說明（`AlertText`）有存但畫面從未顯示、類別只有 11 個彼此不成體系的 `ChangeType` 字串。
   → **新增結構化欄位**：類別、操作者帳號、目標帳號、高風險標記；IP 由主機主檔取得。
3. **根因三：確認狀態不在表裡。** 確認狀態存在單一 blob（整份讀改寫、無樂觀鎖、last-write-wins），批次核准會嚴重放大此問題。
   → **確認狀態併進同一張表**（status / confirmed_by / confirmed_at / note 欄），廢除 `perm_confirms` blob。批次核准即單一 `UPDATE … WHERE change_id IN (…) AND status='pending'`，原子性、狀態篩選下推、覆寫問題三者一起收。
4. **根因四：這頁是全站唯一不走列表慣例的頁面。** 既有 `PagedResult<T>`／`Paging.Normalize`／`renderTable`／`renderPagination`／跨頁勾選樣板它一個都沒用。
   → **改成表格並拉回既有慣例**，不為本頁發明新元件。
5. **操作者帳號本來就查回來了。** Sentinel Q1 投影已含 `sun`（發起操作的帳號），但 `SentinelEventMapper` 映射時丟棄。
   → **擴充映射層不再丟棄**，不需改查詢、不需重抓資料。
6. **類別採系統內建固定類別**（使用者不可增刪）。理由：類別要被索引與批次核准依賴，開放自訂需引入規則引擎與歷史資料重分類策略，範圍不成比例。
7. **列表形態採表格 + 點列展開詳情**，並提供一鍵全部展開／收合。

### 明確不做

| 不做 | 理由 |
|---|---|
| 使用者自訂類別管理頁 | 決策 6。未來要開放時類別 key 已是欄位，加規則引擎即可，不需再改資料層 |
| Sentinel 投影新增 `sip`／`shn`（來源 IP／發起端機器名） | 需改查詢語句與實機驗證，本輪只取已投影的 `sun`。列入 BACKLOG |
| 案件授權（CaseGrant）者可見權限異動 | 現行 `VisibleHostNames()` 刻意不含此路徑，屬既有授權模型決策 |
| 保留期清理孤兒確認列 | 正規化後確認狀態與異動同列，`Prune` 一併刪除，問題自動消失 |
| EF Core Migrations | 專案定案為自製冪等 DDL（`SchemaUpgrader`），沿用 |

---

## 1. 事實核對摘要

| 項目 | 判定 | 證據 |
|---|---|---|
| 頁面型態 | MVC View 空殼 + ES module 打 API，無 PageModel | `PagesController.cs:56`、`wwwroot/js/pages/permission-changes.js` |
| 現有篩選 | 只有 status 四頁籤 | `HandlingController.cs:177` |
| 現有排序 | 無 UI，後端寫死 `DetectedAt` 降冪 | `PermissionChangeStore.cs:70` |
| 現有分頁／總筆數 | 皆無 | — |
| 主機 IP | 紀錄與 DTO 皆無此欄位；只在 `WebHost.IpAddress`；NetIQ 主機的 HostName 本身常就是 IP | `WebHost.cs:31` |
| 帳號 | 無結構化欄位，被塞進 `Before`/`After` | `HostDayPostProcessor.cs:255-288` |
| 行為說明 | `AlertText` 已存（500 字截斷），畫面完全沒顯示 | `PermissionChangeRecord.cs:32` |
| 網段 | 有 `CidrMatcher`（支援 CIDR／萬用字元／單一 IP），無 Subnet 資料表 | `Analysis/CidrMatcher.cs` |
| 儲存層 | 無 `lf_permission_changes` 表（文件宣稱有）；JSONL + blob | `PermissionChangeStore.cs:108` |
| 確認狀態 | 單一 blob 整份讀改寫，無樂觀鎖 | `PermissionChangeStore.cs:118-137` |
| ChangeType 值 | NetIQ 4 種 + 彙總 1 種；本機監控 7 種，兩組不同集合，模型註解已與實際不符 | `HostDayPostProcessor.cs:71-88`、`PermissionMonitorService.cs:84-167` |
| 操作者帳號 | Sentinel `sun` 已投影查回，映射層丟棄 | `SentinelEventMapper.cs:67-81`、`SentinelFieldMap.cs` |
| 既有慣例 | `PagedResult<T>`、`Paging.Normalize`(上限 200)、`renderTable`(sortKey)、`renderPagination`、`PAGE_SIZE_OPTIONS=[10,20,30,50,100]`、`DEFAULT_PAGE_SIZE=20`；QueryString 一律 `page/pageSize/sort/dir` | `Models/PagedResult.cs`、`Services/Paging.cs`、`wwwroot/js/core/ui.js` |
| 批次操作慣例 | 跨頁勾選 Map、三態全選、「全選符合篩選」端點回 `{ids,total,truncated}`、逐筆結果不做全有全無、稽核一次操作寫一筆 | `wwwroot/js/pages/hosts.js`、`AdminController.cs:133/166`、`IssueHandlingCommandService.cs:769` |
| 正規化樣板 | `HandlingBlobMigrator`（背景執行、單一交易、狀態旗標為被寫下的事實、不刪舊 blob）+ HostedService + `MigrationGateMiddleware` + 健康檢查 | `Persistence/Sql/HandlingBlobMigrator.cs` |
| 建表樣板 | `SchemaUpgrader` 冪等 DDL，單一入口 + `isSqlite` 分支，CREATE TABLE SQL 兩份常數 | `Persistence/Sql/SchemaUpgrader.cs:73-93` |
| 測試 | `PermissionChangeService` 與該 API **零測試** | — |
| 授權 | `ConfirmPermission` 僅 User／Admin，另有主機／問題負責人補授；`GET` 端點無 `[Permission]` 標註，只靠可見範圍過濾 | `RoleCapabilityMap.cs`、`UserCapabilityResolver.cs:53` |

### 順手抓到、本輪一併處理的既有問題

| 編號 | 問題 | 嚴重度 | 處理 |
|---|---|---|---|
| B1 | `maxCount` 恆為 200 且前端從不傳，第 201 筆之後**無聲消失** | 高（漏看待辦） | 作業 C 分頁後消失 |
| B2 | 儀表板每次載入 `CountPending()` → 整表反序列化只為數一個數字 | 中 | 作業 A：改 SQL `COUNT(*)` |
| B3 | `WEB-SPEC.md:1228` 寫 API 有 `page` 參數、`DB-SPEC.md:450` 寫有 `lf_permission_changes` 表，**兩者都不存在** | 中（文件說謊） | 作業 F |
| B4 | 此服務零測試 | 中 | 各作業自帶測試 |
| B5 | `Account Name:` 被列為成員名第四順位前綴，而 Windows 訊息中 `Subject` 區段的 `Account Name`（操作者）出現在 `Member` 區段之前 → **`Member Name:` 缺席時把操作者誤寫成成員** | 高（資料錯標） | 作業 B |
| B6 | 稽核政策變更（4717/4718/4719/4907）無 Before/After 分支，**恆為空字串** | 中 | 作業 B |
| B7 | 確認狀態 blob 無樂觀鎖，last-write-wins；批次會放大 | 高 | 根因三，作業 A |
| B8 | 本機監控來源 `DetectedAt` 是寫入當下 `DateTime.Now`（整批同一時間戳），非異動發生時間 | 低（語意誤導） | 作業 E：畫面誠實標示 |
| B9 | `VisibleHostNames()` 對 ViewAll 角色（Admin／Dev／Manager）回傳**全部**主機名，服務層一律把它當白名單下推；而 Store 的語意是 `null` ＝不限。最常見的角色每次查詢都在建一個涵蓋全體的清單去比對全體，結果與不加條件相同 | 中（現況就在發生，正規化後成本更明顯） | 作業 C：可見範圍為全體時不下推名單 |
| B10 | `"perm_changes"`／`"perm_confirms"` 字串鍵在四處各自 `new`，無共用常數 | 低 | 作業 A 順手收斂 |

---

## 2. 作業總覽

**本輪委派模型**：`gemini-3.7-flash-high`。開工前（2026-08-20）查到的額度：Gemini 池週限剩 13%、五小時限 100%（週限 08-21 12:18Z 重置）；Claude and GPT 池週限 **0%（用罄，08-22 才重置）**，等於沒有第二選項。使用者未指派模型。整輪只用這一種；週限若在中途用罄則停下告知使用者，不換模型硬跑。

分支：`feature/permission-changes`（自 dev@b912724 開出）。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | 資料層正規化：`lf_permission_changes` 真表（含新欄位與確認狀態）＋索引＋舊資料遷移 | — | agy |
| B | 來源端欄位擴充與擷取修正：操作者帳號不再丟棄、B5／B6 修正、填入新欄位 | A-1 | agy |
| C | 查詢契約：`PagedResult` ＋ 篩選／排序／分頁，網段比對 | A | agy |
| D | 批次核准：原子更新、逐筆結果、稽核 | A、C | agy |
| E | 前端改版：表格、篩選列、展開詳情、勾選批次、分頁排序總筆數 | C、D | agy |
| F | 收尾：跨段回頭 grep、文件更新、終檢 | A~E | Claude |

---

## 3. 作業明細

### 作業 A-階段 1：權限異動模型欄位與類別推導契約

- **背景**：權限異動紀錄目前只有主機名、時間、對象、異動類型、前後值、告警文字、來源、EventId。要支援依類別篩選與依帳號搜尋，必須先有結構化欄位與一個穩定的類別推導函式。
- **契約**：
  - `PermissionChangeRecord` 新增欄位（名稱為對外契約）：`Category`（string，非空）、`IsPrivilegedTarget`（bool，預設 false）、`InitiatorAccount`（string?，本階段留空，作業 B 填）、`TargetAccount`（string?）。
  - **類別值域（固定，不可增刪）**：

    | key | 顯示標籤 | 涵蓋來源 |
    |---|---|---|
    | `group_member` | 群組成員異動 | ChangeType `成員新增`／`成員移除`（NetIQ 4728/4732/4756/4729/4733/4757 與本機 Administrators 群組） |
    | `folder_acl` | 資料夾權限異動 | `權限新增（ACL 規則）`／`權限移除（ACL 規則）`／`權限變更`(4670) |
    | `owner_change` | 擁有者變更 | `擁有者變更` |
    | `folder_access` | 資料夾存取狀態 | `無法存取`／`恢復可存取` |
    | `audit_policy` | 稽核政策變更 | 4717／4718／4719／4907 |
    | `summary` | 權限異動彙總 | `權限異動（彙總）` |
    | `other` | 其他 | 推導不出時的退路（**不得為空字串或 null**） |

  - 推導必須是**純函式**（輸入 ChangeType + EventId → 類別 key），可在遷移舊資料時離線重算——這是舊資料能補上類別的前提。
  - `IsPrivilegedTarget` 判定（**暫定**，執行端可依實作事實推翻並記錄理由）：`Category == group_member` 且 `ChangeType == 成員新增` 且 `Target` 命中特權群組關鍵字（不分大小寫）：`Administrators`、`Domain Admins`、`Enterprise Admins`、`Schema Admins`、`Account Operators`、`Backup Operators`、`本機 Administrators 群組`。關鍵字集中定義於一處常數。
  - 類別 key ↔ 中文標籤的對應**集中在一處**，由後端提供給前端；前端不得自行硬寫一份對照表。
  - 修正 `PermissionChangeRecord` 上與實際值不符的 ChangeType 註解（現註解漏列 NetIQ 的稽核政策變更與彙總）。
- **範圍**：可動 `LogForesight.Core/Models/PermissionChangeRecord.cs` 與新增的類別推導型別、對應測試。**不准動**：`docs/`、前端、Store／Service。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：**10 個相異 ChangeType 值每一個**都推導到預期類別且無一落入 `other`。清單：NetIQ 4 種（`成員新增`／`成員移除`／`權限變更`／`稽核政策變更`）＋彙總 1 種（`權限異動（彙總）`）＋本機 7 種（`成員新增`／`成員移除`／`無法存取`／`恢復可存取`／`擁有者變更`／`權限新增（ACL 規則）`／`權限移除（ACL 規則）`），其中 `成員新增`／`成員移除` 兩個值**兩來源共用**，故相異值為 10 而非 12
  - 未知 ChangeType 落入 `other` 而非拋例外或空字串；`IsPrivilegedTarget` 對「本機 Administrators 群組成員新增」為 true、對「成員移除」為 false、對一般共用資料夾為 false
  - grep 確認類別 key 字串未在推導函式與 API 契約層（DTO／查詢參數繫結）以外的地方被硬寫

### 作業 A-階段 2：建表、索引與查詢下推

- **背景**：權限異動與其確認狀態目前分別存在 JSONL log 與單一 blob，無法在 SQL 端篩選排序分頁，確認狀態還是 last-write-wins。
- **契約**：
  - 新表 `lf_permission_changes`，**一列＝一筆異動且含確認狀態**（不另建確認表）。欄位至少涵蓋：`change_id`（唯一）、`host_name`、`host_name_key`（比照 `lf_issue_handling` 既有正規化鍵慣例）、`detected_at`、`target`、`change_type`、`category`、`is_privileged_target`、`initiator_account`、`target_account`、`before_value`、`after_value`、`alert_text`、`source`、`event_id`、`status`（預設 `pending`）、`confirmed_by`、`confirmed_by_account`、`confirmed_at`、`confirm_note`、`created_at`。
  - 依 `SchemaUpgrader` 既有樣板：冪等 DDL、單一入口 + `isSqlite` 分支、CREATE TABLE SQL 兩份常數（Sqlite `INTEGER … AUTOINCREMENT` vs SqlServer `bigint … IDENTITY`）。
  - 索引：`change_id` 唯一；`(status, detected_at)`；`(detected_at)`；`(host_name_key, detected_at)`；`(category, status)`；`created_at`（保留期清理用，也是 `GetDedupeKeys` 的篩選欄）。
    **`dedupe_key` 不建索引**（規劃初版寫要建，實作時更正）：沒有任何查詢以它為條件，而它由「主機名(≤255)｜Ticks(19)｜EventId｜AlertText(≤503)」串成、最長約 790 字元，貼著 SQL Server 非叢集索引鍵 1700 bytes（850 nvarchar 字元）的上限。欄位本身**不得設長度上限**——設成 `nvarchar(512)` 在 SQLite（TEXT 無長度）測不出來，到 SQL Server 會變成寫入時「字串或二進位資料會被截斷」。
  - 去重鍵須成為**獨立欄位並建索引**（`dedupe_key`）。現行 `GetDedupeKeys` 註解已載明「逐主機日呼叫在 3000 台規模下會變成數萬次全表掃描」，正規化後**不得保留任何需要整表讀出才能判定去重的路徑**。
  - `PermissionChangeStore` 改走 EF：
    - 寫入端維持既有去重語意（`DedupeKey` 的組成不變），改為以 `dedupe_key` 欄位在 SQL 層判定
    - `CountPending()` 改為 `COUNT(*)`，不得整表物化（修 B2）
    - `Get(changeId)` 改為單列查詢，不得全表掃描
    - 確認寫入改為**條件式更新**：只在 `status='pending'` 時成功，被他人搶先時回報「已被處理」而非覆寫（修 B7）
    - **`Prune(retentionDays)` 維持依「附加時間」（`created_at`）的既有語意，不得改用 `detected_at`。** 反例：NetIQ 重跑一個 100 天前的主機日時，該列 `detected_at` 是舊日期但 `created_at` 是現在；若依 `detected_at` 清理，這筆剛寫入的資料會在下一次清理時立刻消失，使用者永遠看不到重跑補出來的待辦。排序與時間篩選用 `detected_at`，保留期用 `created_at`，兩者是不同語意
  - `"perm_changes"`／`"perm_confirms"` 字串鍵收斂為共用常數（修 B10）；四處建構方式一併對齊。
- **範圍**：`LogForesight.Core/Persistence/**`、`LfDbContext`、`SchemaUpgrader`、四處註冊點。**不准動**：前端、`docs/`、`PermissionChangeService` 的公開方法簽章。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：同一 `change_id` 重複附加不產生第二列；`CountPending` 與逐列狀態一致；**兩個併發確認同一筆時只有一個成功、另一個得到「已被處理」而非靜默覆寫**；`Prune` 刪掉逾期列且不影響保留期內的列
  - `SchemaUpgrader` 在「表不存在」與「表已存在」兩種情況重複執行皆不拋例外（冪等）

### 作業 A-階段 3：舊資料遷移

- **背景**：既有部署的權限異動在 `perm_changes` JSONL 與 `perm_confirms` blob 內，升級後必須出現在新表，否則使用者的待辦會整批消失。
- **契約**：
  - 比照 `HandlingBlobMigrator` 的四條既有約束：整份包在單一交易；完成與否是**被寫下的事實**（狀態旗標）而非從表空不空反推；**不刪舊 blob／舊 log**（保留為備份）；解析失敗直接拋不吞。
  - **背景執行**（不得放在啟動路徑，Windows 服務有 30 秒 SCM 逾時），沿用既有 HostedService／遷移閘門／健康檢查的接線方式。
  - 遷移時對每列**重算 `category` 與 `is_privileged_target`**（A-1 純函式）；`initiator_account`／`target_account` 盡力從既有 `AlertText`／`Before`／`After` 補，抽不到留 null（畫面顯示「—」，**不得填假值**）。
  - 確認狀態 blob 依 `change_id` 併入對應列；找不到對應異動的孤兒確認列記 warn 後略過，不得拋。
  - **遷移閘門必須涵蓋本頁的寫入路徑。** 現行 `MigrationGateMiddleware.GuardedPrefixes` 只有 `/api/handling` 與 `/api/records`，而本頁的端點路由是 `/api/permission-changes`（雖然它的 controller 住在 `HandlingController.cs` 這個檔案裡，路由並不在 `/api/handling` 之下）——**不補這一條，遷移進行中使用者按下確認或批次核准，寫入會落在還沒搬完的表上，隨後遷移的整份寫入撞唯一索引，或造成新舊兩份資料並存**。同時閘門判定需一併考慮本輪新增的遷移狀態，不能只看處理狀態遷移器。
  - **繞過條件**（單向閘門的反例）：狀態卡在 `Running`（行程被砍）必須能退回 `Pending` 重跑；已完成後若舊 blob 又出現新內容（降版後再升版），需能重新評估而非永久跳過。
- **範圍**：`LogForesight.Core/Persistence/Sql/**` 新增遷移器與狀態、`LogForesight.Web` 既有 HostedService／閘門／健康檢查的接線。**不准動**：其他作業檔案、`docs/`。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：舊 JSONL＋blob 遷移後新表列數與內容正確、確認狀態正確附著；**重複執行不會產生重複列**；遷移中途失敗後狀態退回 `Pending` 且 `LastError` 有值；孤兒確認列不致失敗
  - grep 確認舊 blob／log 未被刪除

### 作業 B-階段 1：NetIQ 映射層帶入操作者帳號

- **背景**：Sentinel Q1 投影已包含 `sun`（發起操作的帳號），但映射成 `EventLogEntryData` 時只保留 7 個欄位而丟棄。這是唯一不需重抓資料就能回答「是誰做的」的路徑。
- **契約**：
  - `EventLogEntryData` 新增 `InitiatorAccount`（string?）；`SentinelEventMapper` 不再丟棄 `sun`。
  - 此欄位對非 NetIQ 來源為 null，**所有既有取用 `EventLogEntryData` 的分析路徑行為必須不變**。
  - 不改 Sentinel 查詢語句、不新增投影欄位。
- **範圍**：`LogForesight.Core/Models/EventLogEntryData.cs`、`LogForesight.Core/Analysis/SentinelEventMapper.cs` 及其測試。**不准動**：`SentinelQueryBuilder`、其他分析器、`docs/`。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠（既有 Sentinel 映射測試不得被改寫成配合新行為）
  - 新增測試須證明：`sun` 有值時映射到 `InitiatorAccount`；`sun` 缺席時為 null 而非空字串；其餘 7 個既有欄位映射結果與改動前完全相同

### 作業 B-階段 2：擷取修正與新欄位填值

- **背景**：權限異動後處理目前只解析 4 個前綴族群，且「第一個命中就鎖定」的機制會在 `Member Name:` 缺席時把 `Subject` 區段的操作者帳號誤寫成成員（B5）；稽核政策變更類事件沒有任何 Before/After 分支，畫面上等於什麼都沒說（B6）。
- **契約**：
  - **B5 修正**：操作者帳號與目標帳號必須**分別擷取、不得共用同一組前綴**。目標帳號只取 `Member Name:`／`成員名稱`；操作者帳號優先取 `InitiatorAccount`（B-1），退而取訊息中 `Subject` 區段的 `Account Name:`／`帳戶名稱`。**任何情況下都不得把操作者寫進 `Before`／`After`**。
  - **B6 修正**：稽核政策變更類（4717/4718/4719/4907）必須產出可讀的 Before/After 或等效說明（例如政策類別／子類別／變更內容行）；抽不到時給明確的「（訊息未提供）」而非空字串。
  - 填入新欄位：`Category`、`IsPrivilegedTarget`、`InitiatorAccount`、`TargetAccount`。
  - `PermissionMonitorService`（本機監控來源）同樣填入新欄位：目標帳號取成員名（僅群組成員類），操作者帳號為 null（本機快照比對無從得知）。
  - 既有去重鍵語意與每主機日 50 筆上限行為**不得改變**。
  - **跨段：帳號擷取只能有一份實作。** A-3 的遷移器已經為了補舊資料的帳號欄位寫了一套擷取（`PermissionChangeMigrator.ExtractAccounts`），且它猜的操作者前綴（`Caller User Name:`／`操作者帳號:`）在 Windows 事件訊息中並不存在、實際永遠抽不到。本階段定義的正式擷取規則落地後，**遷移器必須改呼叫同一個函式**，不得兩處各留一份。
- **範圍**：`HostDayPostProcessor.cs`、`PermissionMonitorService.cs`、`AnalysisOrchestrator.cs` 寫入段及其測試。**不准動**：其他作業檔案、`docs/`。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：**訊息中同時含 `Subject:Account Name` 與 `Member Name` 時，兩者分別落在操作者與目標帳號欄，且 `After` 是成員而非操作者**；**訊息中只有 `Subject:Account Name`（無 `Member Name`）時不再把操作者寫進 `After`**；4719 事件的 Before/After 不為空字串；每筆新產出的紀錄 `Category` 皆非空
  - 既有 `NetiqPermissionChangePostProcessorTests` 的 8 個測試不得被刪除或弱化

### 作業 C-階段 1：查詢契約（篩選／排序／分頁）

- **背景**：現行查詢只有 `status` 與 `maxCount`，回 `List<T>` 不回總數，第 201 筆之後無聲消失。
- **契約**：
  - `PermissionChangeService.Query` 改回 `PagedResult<PermissionChangeDto>`，參數：

    | 參數 | 語意 |
    |---|---|
    | `q` | 關鍵字，比對主機名／操作者帳號／目標帳號／對象(Target)／行為說明(AlertText)，不分大小寫、包含比對 |
    | `subnet` | 網段，接受 CIDR（`192.168.1.0/24`）、萬用字元（`192.168.1.*`）、單一 IP，交由既有 `CidrMatcher.Parse` 解析；解析失敗回驗證錯誤，不得靜默忽略 |
    | `category` | 類別 key，可多選（逗號串，比照既有 `statuses`／`actions` 慣例） |
    | `status` | `pending`／`authorized`／`suspicious`，**單選**（對應畫面四個頁籤；不帶＝全部）。與 `category` 的多選不同，此處刻意不做多選——見 E-2 |
    | `source` | 來源（本機監控／NetIQ 事件） |
    | `from` / `to` | 時間範圍（依 `detected_at`） |
    | `sort` | `detectedAt`（預設）／`hostName`／`category`／`status` |
    | `dir` | `asc`／`desc`，**預設 `desc`** |
    | `page` / `pageSize` | 比照既有慣例，走 `Paging.Normalize`（上限 200） |

  - `PagedResult.Total` 必須是**套用所有篩選後的真實總筆數**，不受 pageSize 影響（P5 的資料來源）。
  - `PermissionChangeDto` 新增：`Category`、`CategoryLabel`、`IsPrivilegedTarget`、`InitiatorAccount`、`TargetAccount`、`HostIp`、`SummaryText`。
    - `HostIp` 取得順序（**暫定**）：主機主檔 `WebHost.IpAddress` → 若為 null 且 `HostName` 本身可解析為 IP 則用 `HostName` → 否則 null。**不做 IP 快照欄位**（IP 會變動，顯示最新較合理）。沿用現行「一次 `GetAll()` 建主機字典」的作法，**不得逐列查主機**。
    - `SummaryText`（P6「說明這個異動改動了什麼」）：一句話摘要，**由後端產生**（由類別、對象、前後值組成的人話）。前端只顯示不組字串——摘要規則若散在前端，會與後端的類別標籤各自演化成兩套說法。
  - 現行 `Confirm()` 回傳值是「重新全查一次再取單筆」，正規化後**必須改為單列查詢**。
  - **網段篩選實作方式**：`CidrMatcher` 是 C# 判定、無法下推 SQL，作法為先以主機主檔算出符合網段的主機名單，再以主機名下推查詢。
  - **主機名單下推規則（含 B9 修正）**：
    - EF 8 的 primitive collection 會翻成單一 JSON 參數（SQL Server `OPENJSON`／SQLite `json_each`），**不是 N 個參數，不存在 2100 參數上限問題**。專案既有三處呼叫點（`EfIssueHandlingStore.cs:56`、`EfIssueAggregateQuery.cs:72`、`RecordRepository.cs:240`）已實測並留下註解，本輪沿用同一機制。
    - **可見範圍為全體時（ViewAll 角色）不得下推主機名單**，須傳「不限」語意（修 B9）。建立涵蓋全體的清單去比對全體，與不加條件結果相同卻多付出序列化與 OPENJSON 比對成本。
    - 網段名單同樣下推；若網段命中的主機數接近全體，執行端可自行判斷是否值得下推（此為效能取捨，不影響正確性）。
    - **禁止**以「讀出後在記憶體過濾」作為任何 fallback。`Total` 與分頁一旦回到記憶體計算，就等於退回本輪根因一要消滅的全表反序列化。
  - 移除 `maxCount` 靜默截斷（B1）。
  - `GET /api/permission-changes` 沿用現行「無 `[Permission]` 標註、只靠可見範圍過濾」的既有行為，本輪不改。
- **範圍**：`Services/PermissionChangeService.cs`、`Models/Dto/HandlingDtos.cs`、`Controllers/Api/HandlingController.cs` 的 `PermissionChangesController`、`Persistence/PermissionChangeStore.cs` 查詢段及測試。**不准動**：前端、`docs/`、批次端點。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：預設排序為 `detected_at` 降冪；`Total` 在 `pageSize=1` 時仍等於全部符合筆數；`q` 能同時命中操作者帳號與行為說明；`subnet` 以 CIDR 與萬用字元各命中預期主機、格式錯誤回驗證錯誤；多選 `category` 為 OR 語意；`SummaryText` 對每個類別都產出非空且不含未替換的樣板殘留；**可見範圍名單超過門檻時結果與未超過門檻時完全一致**（同一組資料、兩種路徑比對）；不可見主機的紀錄不出現在任何頁

### 作業 D-階段 1：批次核准

- **背景**：使用者需要「篩出某一類別 → 全部打勾 → 一次送出通過審核」。現行只有單筆確認端點，且確認狀態寫入無併發保護。
- **契約**：
  - 新端點 `POST /api/permission-changes/confirm/batch`
    - 請求：`{ changeIds: string[], status: "authorized"|"suspicious", note?: string }`
    - `changeIds` 至少 1 筆（比照既有 `[MinLength(1)]` 慣例，錯誤訊息用人話）
    - 一次上限**暫定 500 筆**，超過回驗證錯誤並告知上限
    - `status == suspicious` 時 `note` 必填（比照現行單筆規則）；`pending` 不接受
    - 需 `ConfirmPermission` 能力
  - 回應：`{ updatedCount, skipped: [{ changeId, hostName, reason }] }`。**逐筆結果、不做全有全無**。略過原因至少涵蓋：已被他人處理、不在可見範圍、找不到。
  - 更新必須是**條件式原子更新**（只更新 `status='pending'` 的列），不得先讀後寫。
  - 新端點 `GET /api/permission-changes/ids`：與清單查詢**共用同一組篩選參數**，回 `{ changeIds, total, truncated }`，只回 `pending` 列；上限比照既有慣例並誠實回報 `truncated`。**清單與此端點的篩選條件組裝必須共用同一段程式碼**，不得各寫一份（既有 `hosts.js` 明列此教訓）。
  - 稽核：**一次批次寫一筆**，新增專屬 action `perm_confirm_batch`（理由同 `issue_bulk_close`：影響範圍跨主機，稽核查詢要能單獨篩出）。detail 至少含 `Status`、`Count`、`ChangeIds`、`HostNames`、`Categories`、`Note`、`Skipped`。整批共用同一個 `occurredAt`。
  - 現行單筆 confirm 端點行為與訊息**不得改變**。
- **範圍**：`PermissionChangeService`、`HandlingDtos.cs`、`PermissionChangesController`、`AuditEntry.cs` action 常數、`AuditQueryService` 中文對照及測試。**不准動**：前端、`docs/`。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - 新增測試須證明：批次授權後每一筆狀態與確認人正確；**清單中混入「已被他人確認」的列時該列進 `skipped` 且其餘列照樣成功**；不可見主機的 changeId 進 `skipped` 且不洩漏存在性；`suspicious` 缺 note 被擋；超過上限被擋；稽核**只寫一筆**且 action 為 `perm_confirm_batch`；`ids` 端點與清單端點在同一組篩選下回傳的 id 集合一致

### 作業 E-階段 1：表格骨架（排序／分頁／總筆數）

- **背景**：本頁目前是全站唯一不走 `renderTable`／`renderPagination` 的列表頁，卡片形態無法承載排序表頭與勾選欄。
- **契約**：
  - 卡片列表改為 `renderTable`，欄位順序：`[勾選] │ 時間 │ 主機 (IP) │ 帳號 │ 類別 │ 異動說明 │ 狀態`
    - 「主機 (IP)」：主機名為主、IP 為次要小字；IP 取不到時不顯示括號（不得顯示「(—)」）
    - 「帳號」：兩行，`操作者` 與 `目標帳號`，各帶小字標籤；缺值顯示「—」
    - 「類別」：中文標籤徽章；`IsPrivilegedTarget` 為 true 時**額外**顯示高風險徽章（不取代類別徽章）
    - 「異動說明」：直接顯示後端的 `SummaryText`，過長截斷，完整內容在展開區；**前端不得自行組裝摘要字串**
    - 「狀態」：沿用現行狀態徽章樣式
  - 排序：時間／主機／類別／狀態四欄可排序，**下推後端**（比照 `audit.js`／`hosts.js`，非 `sortRows` 前端排序）；預設 `detectedAt` 降冪（P4）
  - 分頁：`renderPagination`，`loadPageSize`／`savePageSize` 的 localStorage key 為 `permissionChanges`，前端一律明傳 `pageSize`
  - 總筆數：以 `lastResult.total` 顯示「共 N 筆」（P5），篩選變動即時更新
  - 表格需 `overflow-x: auto` 包裹（既有 UX 規範：表格不得撐破版面）
  - 本機監控來源的時間需標示語意為「偵測時間」（B8），以 tooltip 或欄位說明呈現，不得讓使用者誤以為是異動發生時間
  - 視覺沿用專案既有企業藍主題與 Fira 字型，**不得引入新色票或字型**；資料密度比照 dashboard 級（緊湊列高）
- **範圍**：`wwwroot/js/pages/permission-changes.js`、`Views/Pages/PermissionChanges.cshtml`、必要的 `wwwroot/css`。**不准動**：`core/ui.js` 的既有共用行為（如需擴充須說明理由）、其他頁面、`docs/`、後端。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠
  - grep 確認頁面確實使用 `renderTable` 與 `renderPagination`，且**未出現自製分頁或自製排序邏輯**
  - grep 確認前端未硬寫類別 key ↔ 中文標籤對照表（標籤來自後端 DTO）
  - 頁面在 375／768／1280 寬度下無水平捲動溢出（表格自身可捲）

### 作業 E-階段 2：篩選列

- **背景**：現行只有四個狀態頁籤。使用者需要依網段、帳號、主機、類別搜尋。
- **契約**：
  - 篩選列欄位：關鍵字（`q`，placeholder 說明可搜主機／帳號／對象／說明）、網段（`subnet`，placeholder 給格式範例）、類別（多選）、狀態（多選）、來源、時間範圍（`from`／`to`）
  - **狀態維持現行四個頁籤（待確認／授權操作／可疑／全部）作為主要切換，篩選列不再放狀態欄位**——兩套並存會讓「頁籤選待確認、篩選選可疑」變成無解的矛盾狀態。頁籤即 `status` 參數的來源（「全部」＝不帶）
  - 篩選條件記憶 localStorage 並同步 URL query（比照 `WEB-SPEC` §8.6 第 1、2 條）
  - 任一篩選變動時 `page` 重設為 1
  - 有「清除篩選」動作
  - 網段格式錯誤時，錯誤訊息顯示在該欄位旁（不得只在頁面頂端）
- **範圍**：同 E-1。**不准動**：後端、其他頁面、`docs/`。
- **驗收**：
  - grep 確認篩選參數組裝**只有一份**（清單查詢與「全選符合條件」共用）
  - 重新整理頁面後篩選條件仍在；貼上帶 query 的網址能重現同一組篩選
  - 網段輸入 `192.168.1.0/24`、`192.168.1.*`、`192.168.1.10` 三種格式皆可查詢；輸入 `abc` 顯示欄位層級錯誤訊息

### 作業 E-階段 3：展開詳情與一鍵全部展開／收合

- **背景**：ACL 規則字串與 Security Descriptor 動輒上百字，塞進表格欄一定爆版；但使用者審核時需要看到完整內容。
- **契約**：
  - 點擊列（或列上的展開控制項）展開詳情區，內容至少含：異動前／異動後完整值、行為說明（`AlertText`）原文、對象(Target)、來源、EventId、確認資訊（已處理時：確認結果／確認人／確認時間／說明）
  - 表格上方提供**一鍵全部展開／收合**控制項，作用範圍為**當頁**；按鈕文字隨當前狀態切換（全部展開 ↔ 全部收合）
  - 翻頁或重新查詢後回復為全部收合（**暫定**；執行端若認為保留展開狀態較合理可推翻並記錄理由）
  - 展開控制項需可鍵盤操作且有 `aria-expanded`；點擊勾選框不得觸發展開（比照 `hosts.js` 既有 `stopPropagation` 處理）
  - 展開／收合過渡動畫 150～300ms，並遵守 `prefers-reduced-motion`
- **範圍**：同 E-1。**不准動**：後端、其他頁面、`docs/`。
- **驗收**：
  - 鍵盤 Tab 可到達展開控制項並以 Enter／Space 操作，`aria-expanded` 狀態正確
  - 全部展開後再全部收合，無殘留展開列
  - 勾選框點擊不會連帶展開該列

### 作業 E-階段 4：勾選與批次核准

- **背景**：使用者要能篩出某一類別後一次全部打勾送出。專案已有跨頁勾選的成熟樣板可沿用。
- **契約**：
  - 勾選欄**只在 `pending` 列出現**（已確認的不可改回，後端本來就擋）
  - 表頭全選只作用於當頁可勾選列，並維持三態（全選／部分 indeterminate／未選）
  - 選取狀態**跨頁保留**（比照 `hosts.js` 的 Map 作法）
  - 提供「選取全部符合條件的 N 筆」，走 `GET /api/permission-changes/ids`；`truncated` 為 true 時以 toast 明確告知上限與請分批
  - 選取列顯示「已選 N 筆」與「清除選取」
  - 批次動作：「標記為授權操作」「標記為可疑」；可疑時說明欄必填
  - **送出前 modal 預覽**：顯示「將把 N 筆標記為○○」，並按類別與主機分組摘要（資料取自勾選當下已有的物件，不重新打 API）
  - 送出後依回應顯示結果：全部成功顯示「已處理 N 筆」；有略過時顯示「已處理 N 筆，略過 M 筆」並可展開看略過原因；接著清空選取並重新載入
  - 批次進行中按鈕需禁用並有載入回饋（不得可重複點擊）
- **範圍**：同 E-1。**不准動**：後端、其他頁面、`docs/`。
- **驗收**：
  - grep 確認「全選符合條件」與清單查詢共用同一份篩選參數組裝
  - 勾選數筆後翻頁再翻回，選取狀態仍在
  - 已確認的列沒有勾選框，且「全選當頁」不會把它們算進去
  - 標記可疑時未填說明會被前端擋下並提示

### 作業 F（Claude 執行）

- F-1：跨段產出鏈回頭 grep——後端本輪新增的每個 DTO 欄位（`Category`／`CategoryLabel`／`IsPrivilegedTarget`／`InitiatorAccount`／`TargetAccount`／`HostIp`）逐一 grep 前端消費點；本規劃 §0 定案裡「畫面上會顯示 X」的每一句逐一 grep 實作。
- F-2：文件更新（見 §5）。
- F-3：終檢（見 §7）。

---

## 4. 測試計畫

| 作業-階段 | 測試名稱要點 | 要證明的行為 |
|---|---|---|
| A-1 | 各ChangeType推導類別_全部有對應 | 11 個既有值無一落入 `other` |
| A-1 | 未知ChangeType_落入其他類別 | 退路存在且非空 |
| A-1 | 特權群組成員新增_標記為高風險 | 判定條件正確、成員移除不標 |
| A-2 | 重複附加同一異動_不產生第二列 | 去重語意在 SQL 層維持 |
| A-2 | 併發確認同一筆_只有一個成功 | 修 B7 |
| A-2 | 待確認筆數_不物化全表 | 修 B2 |
| A-2 | 保留期清理_依附加時間而非偵測時間 | 重跑舊主機日補出的待辦不會立刻被刪 |
| A-2 | 去重判定_不需讀出整表 | 消除數萬次全表掃描 |
| A-3 | 遷移未完成時_確認與批次端點回503 | 遷移閘門涵蓋 `/api/permission-changes` |
| A-3 | 舊JSONL與確認blob遷移_列數與內容正確 | 升級不掉資料 |
| A-3 | 遷移重複執行_不產生重複列 | 冪等 |
| A-3 | 遷移中途失敗_狀態退回待處理並記錄錯誤 | 可重跑 |
| B-1 | Sentinel事件含sun_映射到操作者帳號 | 不再丟棄 |
| B-1 | 既有七欄位映射_與改動前一致 | 不破壞既有分析 |
| B-2 | 訊息同時含Subject與Member_操作者不寫進異動後 | 修 B5 |
| B-2 | 訊息只有SubjectAccountName_不誤標為成員 | 修 B5 |
| B-2 | 稽核政策變更事件_異動前後不為空字串 | 修 B6 |
| C-1 | 預設查詢_依偵測時間降冪 | P4 |
| C-1 | 分頁大小為一_總筆數仍為全部符合筆數 | P5 |
| C-1 | 關鍵字_命中操作者帳號與行為說明 | P1 |
| C-1 | 網段_CIDR與萬用字元各命中預期主機 | P1 |
| C-1 | 網段格式錯誤_回驗證錯誤 | 不靜默忽略 |
| C-1 | 可見範圍為全體時_不下推主機名單 | 修 B9 |
| C-1 | 可見範圍受限時_結果與可見範圍為全體時的子集一致 | 下推與不下推兩條路徑語意相同 |
| C-1 | 不可見主機的紀錄_不出現在任何頁 | 授權不因分頁而破功 |
| D-1 | 批次授權_每筆狀態與確認人正確 | P7 |
| D-1 | 混入已被他人確認的列_該列略過其餘成功 | 逐筆結果 |
| D-1 | 不可見主機的changeId_進略過清單 | 不洩漏存在性 |
| D-1 | 批次可疑缺說明_被擋 | 與單筆規則一致 |
| D-1 | 批次操作_稽核只寫一筆且為專屬action | 稽核可單獨篩出 |
| D-1 | ids端點與清單端點_同一篩選下集合一致 | 篩選條件不漂移 |

作業 E（前端）：專案前端無自動化測試框架，慣例為 grep 驗收＋手動驗證，各階段的驗收條目即為其測試計畫，不另列於本表。

---

## 5. 文件更新（Claude 在全部驗收後才寫）

| 文件 | 改什麼 |
|---|---|
| `docs/WEB-SPEC.md` §9.5 | 權限異動待辦頁改寫：表格欄位、篩選項目、排序與分頁、批次核准流程、展開詳情。**修正 B3**：現行寫的 `?status=&page=` 與實作不符，改為本輪定案的完整參數表 |
| `docs/WEB-SPEC.md` §8.6 | 若展開列／一鍵全展是全站首見樣式，補一條共用規範 |
| `docs/DB-SPEC.md` | **修正 B3**：新增 `lf_permission_changes` 真表定義（欄位、索引、確認狀態併入的理由）；更正既有「權限異動存在 lf_permission_changes」的錯誤敘述與 JSONL 描述 |
| `docs/DETECTION-SPEC.md` | 補類別對應表與 4717/4718/4719/4907 的 Before/After 產出規則 |
| `README.md` | 權限異動待辦功能描述更新（篩選、批次核准） |
| `LogForesight.Web/HelpContent/09-permissions.md` | 怎麼篩、怎麼批次核准、批次上限、「偵測時間」語意（B8）、高風險徽章的意思 |
| `docs/BACKLOG.md` | 移入本輪不做的項目：Sentinel 投影加 `sip`／`shn`、使用者自訂類別 |

---

## 6. 風險與回滾

| 風險 | 影響 | 對策 |
|---|---|---|
| 遷移在正式環境資料量大時耗時過久 | 升級後一段時間內待辦頁看不到舊資料 | 背景執行 + 遷移閘門 + 健康檢查可查進度（沿用既有機制）；不刪舊 blob |
| SQL Server 未實機驗證（既有已知風險） | 新表 DDL 或查詢在 SqlServer 炸 | 建表 SQL 兩份常數逐欄對照；避開 Sqlite 過但 SqlServer 炸的寫法（既有教訓：HAVING 引用外層欄位）；上線前以實機 script 驗證 |
| 大名單下推導致 SQL Server 對 `OPENJSON` 基數估計偏低、選錯 join 策略 | 大環境查詢變慢（非失敗） | ViewAll 不下推名單後，大名單只剩「網段命中極多主機」一種情境；必要時執行端可調整下推策略，但不得改用記憶體過濾 |
| 正式環境 SQL Server 相容性層級低於 130（無 `OPENJSON`） | 查詢執行失敗 | 既有三處呼叫點已依賴同一機制，此為既有環境前提而非本輪新增；若該環境跑得動既有功能即跑得動本頁 |
| 改 `EventLogEntryData`／`SentinelEventMapper` 影響共用管線 | NetIQ 全線分析受波及 | B-1 獨立成段可單獨回滾；驗收要求既有七欄位映射結果不變、既有測試不得被改寫 |
| 批次一次核准大量紀錄後才發現誤判 | 稽核追溯壓力 | 稽核一次寫一筆且含完整 changeIds／HostNames／Categories；送出前 modal 預覽；狀態不可改回是既有設計，說明書須寫清楚 |
| 前端一次改動過大 | 迴歸風險 | 拆四階段各自可驗收；`core/ui.js` 共用行為不得被改動 |
| 遷移進行中使用者按下確認／批次核准 | 新舊兩份資料並存，或遷移寫入撞唯一索引 | A-3 契約要求把 `/api/permission-changes` 納入遷移閘門（現行閘門不涵蓋此路由） |

回滾單位：每個作業可獨立 commit／回滾。作業 A 已上線後回滾需注意新表資料不會回寫舊 blob——舊 blob 是升級前快照，回滾等於遺失升級後新增的確認動作，合併前需向使用者確認。

---

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | agy | 通過 | 2332 支（2326 綠／6 略過／0 紅），較基準 2288 增 44；建置唯一警告為既有的 `EfIssueAggregateQuery.cs:987`，不在 diff 內。10 個相異 ChangeType 值逐一 grep 確認皆在測資中 | 過度設計 2 處由 Claude 自行移除：① `ResolveCategory()` 是 `Resolve()` 的純別名且零呼叫者；② `IsPrivilegedTarget()` 多一個 optional `category` 參數，可傳入與 `changeType` 互相矛盾的值而靜默回 false。另：agy 移除了 `PermissionChangeRecord.cs` 的 UTF-8 BOM，清點後全專案 447 個 .cs 僅 32 個有 BOM，判定為正規化，保留不復原 |
| A-2 | agy | 通過 | 2340 支（2334 綠／6 略過／0 紅），較 A-1 後的 2332 增 8；建置警告數不變（仍只有既有的 `EfIssueAggregateQuery.cs:987`）。逐一查證：確認寫入是 `ExecuteUpdate` 帶 `WHERE status=pending` 的單一敘述；`CountPending` 走 SQL COUNT、`Prune` 走 `ExecuteDelete`、`GetDedupeKeys` 只投影單欄，無「先 ToList 再 Where」；三個既有測試檔只改建構 store 的那一行，斷言未動 | **跨 provider 真 bug（Claude 修）**：`dedupe_key` 被宣告 `nvarchar(512)` 並建索引，但該鍵最長約 790 字元。SQLite 的 TEXT 無長度限制故測試全綠，SQL Server 上首筆長告警文字寫入即拋「字串或二進位資料會被截斷」。修法為移除索引並取消長度上限（見 §A-2 索引段的更正），程式碼留註解禁止改回。**此為規劃者自身的錯**：初版把「讓 GetDedupeKeys 不必整表讀出」的理由掛在 `dedupe_key` 索引上，但該查詢真正需要的是 `created_at` 索引 |
| A-3 | agy | 通過 | 2352 支（2346 綠／6 略過／0 紅），較 A-2 後的 2340 增 12（agy 11 ＋ Claude 回歸測試 1）。查證通過：獨立遷移器與獨立狀態 blob key、單一交易、失敗退回 Pending 並寫 LastError、不刪舊 log／blob、類別以 A-1 純函式重算、孤兒確認列 warn 略過、閘門分流正確（權限異動走新狀態、既有兩前綴不變、唯讀方法放行） | **資料遺失風險（Claude 修）**：重入保護照抄 `HandlingBlobMigrator` 的 `if (ctx.Xxx.Any()) return;`。該寫法在處理狀態上安全（那三張表只有 HTTP 寫入、閘門擋得住），但權限異動的 `AppendChanges` 還有背景排程分析這個非 HTTP 呼叫端——夜間分析在遷移完成前寫進一列，整批舊資料就被判定為已搬而永久消失，狀態仍顯示完成。改為逐筆比對 `change_id` 只補未搬的，並加回歸測試 `遷移前背景分析已寫入新表_舊資料仍然全部搬進來`。同時違反該遷移器自身註解第 2 條「完成與否是被寫下的事實，不是從目標表空不空反推」。<br>小修：移除未使用的 `_logFactory` 建構參數；log 行反序列化選項由 `Pretty` 改為寫入端用的 `Compact`。<br>**規格外補強**：`SchemaUpgrader` 的 `lf_permission_changes` CREATE TABLE 常數原本在任何測試中都沒被執行過（`EfSqliteFixture` 只跑 `EnsureCreated`，之後 `CreateTableIfMissing` 恆為 no-op），已補上「空連線直接 Upgrade」測試 |
| B-1 | agy | 通過 | 2358 支（2352 綠／6 略過／0 紅），較 A-3 後的 2352 增 6。查證：`SentinelQueryBuilder`／`SentinelFieldMap` 未被修改、Linux 映射路徑未被動、`sun` 缺席與空白皆得 null、既有 7 欄逐欄斷言不變 | 無。本段未發現問題，Claude 未做任何修正 |
| B-2 | agy | 通過 | 2367 支（2361 綠／6 略過／0 紅），較 B-1 後的 2358 增 9；建置 0 警告。查證：新的 `PermissionChangeExtractor` 是**分區段**剖析（`SubjectAccountName` 與 `MemberAccountName` 為獨立欄位，區段標題同時認 `Subject`／「主體」），操作者在結構上不可能流進成員欄位——修法對到根因，不是調前綴順序；4717／4718／4719 各有分支且抽不到時填「（訊息未提供）」；全 repo 只剩一份 `TryExtractValue`，遷移器改呼叫共用函式；`NetiqPermissionChangePostProcessorTests` 既有 8 支方法名稱逐一 grep 確認全在 | 無。本段未發現問題，Claude 未做任何修正 |
| C-1 | agy | 通過 | 2381 支（2375 綠／6 略過／0 紅），較 B-2 後的 2367 增 14；建置 0 警告。查證：前端零改動、`maxCount` 已從 controller 與 service 移除、Store 內無「先 ToList 再 Where」、ViewAll 時 `hostNames` 傳 null（有網段條件時才算名單）、篩選組裝是單一份 `BuildFilter` 且註解明寫供批次端點共用、`SummaryText` 由後端 `GenerateSummaryText` 產生 | 無需修正。觀察：Store 保留了 `Query(hostNames, status, maxCount)` 相容多載，生產程式碼零呼叫、僅測試使用——這是為了不改既有測試斷言（規格禁止）而留的 shim，D-1 或 F 收尾時可評估移除 |
| D-1 | agy | 通過 | 2394 支（2388 綠／6 略過／0 紅），較 C-1 後的 2381 增 13；建置 0 警告。查證：批次更新是單一 `ExecuteUpdate` 帶 `IN (…) AND status=pending`；單筆與批次共用同一個 `SaveConfirmationsInternal`（無兩份判定）；`ids` 端點呼叫 `BuildFilter`；稽核 action 常數與 `AuditQueryService` 中文對照皆補上；不可見主機的略過原因與「找不到」共用同一句話，不洩漏存在性 | 無需修正。**殘留脆弱點（記入 SQL Server 實機驗證項）**：批次判斷「哪幾筆是本次真正更新到的」是靠更新後重讀比對 `ConfirmedAt` 是否等於本次時間戳。SQLite（TEXT 存 ISO）與 SQL Server（`datetime2` 預設 7 位精度）都能完整往返故正確，但它依賴時間戳的位元級往返精度——若欄位精度改成 `datetime2(3)`，成功的列會被誤報成「已被他人處理」（不掉資料，但訊息錯誤） |
| E-1 | agy | 通過 | 測試維持 2394 支（純前端段）；`.mjs` 副檔名下 `node --check` 兩檔語法皆過。查證：只動 3 個前端檔、零 `.cs`；`core/ui.js` 只新增 `toggleAllTableDetails` 而 `renderTable`／`renderPagination` 未改；用 eager 的 `rowDetail`；無 `sortRows`、無類別對照表、無自製分頁 DOM；查詢參數集中一處組裝且帶齊 sort/dir/page/pageSize | **與 E-3 合併執行**（查證發現 `renderTable` 本就支援 `rowDetail`／`onRowExpand`，展開列不需自製，E-3 縮成一個全展收控制項；且 Gemini 週限吃緊）。<br>agy 回報的 `node --check` 是偽陰性（`.js` 被當 CommonJS，ESM 必失敗），Claude 改用 `.mjs` 重驗才是有效結論。<br>**留意**：新增的 `toggleAllTableDetails` 直接操作 DOM，會**繞過 lazy 的 `onRowExpand` 填充**。本頁用 eager 的 `rowDetail` 故安全，但它已是 `core/ui.js` 公開 API，其他頁面若以 lazy 模式呼叫會展出空白詳情列 |
| E-2 | | | | |
| E-3 | | | | |
| E-4 | | | | |
| F | | | | |

### 終檢（併回前）

全部作業驗收後、合併前，開兩個獨立審查各審一次全 diff：

- **程式碼**：新 bug／與規劃不符／過度設計或錯誤重複／測試假通過／同型遺漏
- **文件**：規劃契約逐條對照實作、現行文件與程式碼核對、寫作紀律與跨文件重複

終檢前先做跨段產出鏈回頭 grep（作業 F-1）。終檢的高嚴重度或推翻既有定案的發現，須先自己讀該條程式碼路徑驗證機制成立與否，再決定是否加開作業。
