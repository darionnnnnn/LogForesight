# 兩千台量級擴展規劃（SCALE-2000-PLAN）

> 2026-07-23 定案，同日擴寫為細部設計版（v2）。
> 範圍：NetIQ 主動探索匯入、負責人員匯入、網段綁定群組、兩千台量級的 Web 呈現調整、
> AI 介入（W1＋W2）。
> 已定決策：SQL 後端納入本輪（SQL Server）；負責人匯入帳號不存在時自動建立；
> AI 範圍 W1＋W2（自然語言轉篩選列實驗性、不在本輪）。
> 明確排除：WEB-SPEC 總體檢列出的 P3 規格債（主機詳情補區塊等），另行處理。

## 0. 為什麼 SQL 後端是前提

`history.txt` 在 2000 台 × 90 天 ≈ 18 萬行（估 0.5～2GB），Web 每次查詢全檔重讀＋
記憶體篩選——單機情境的刻意簡化（WEB-SPEC §10.4），兩千台下每頁卡數秒到數十秒。
架構已鋪路：`IAnalysisRecordQuery` 等介面與合約測試就緒（DB-PLAN），SQL 實作繼承
同一組合約案例即可保證語意逐位一致。**量級 UI 調整（§5）都建立在它之上。**

---

## 1. NetIQ 主動探索匯入（Phase B）

### 1.1 設定契約（批次 appsettings.json，唯一事實來源、Web 唯讀）

```jsonc
"NetIq": {
  "Servers": [
    {
      "Name": "SENTINEL-A",
      "BaseUrl": "https://sentinel-a.corp.local",
      "Username": "svc-lfquery",          // 新增
      "Password": ""                       // 新增；正式環境以環境變數覆寫：
    }                                      //   NetIq__Servers__0__Password
  ],
  ...
}
```

- `SentinelServer` 類別（Core/Configuration/AppSettings.cs）加 `Username`/`Password` 欄。
- 帳密缺漏時：該 Sentinel 的「掃描」鈕停用並附提示（設定不完整），不擋其他 Sentinel。
- 密碼絕不回傳前端：Web 端 API 只回「此 Sentinel 可否掃描」布林。

### 1.2 探索介面（Core，環境隔離）

```csharp
public record NetiqDiscoveredHost(string HostName, string IpAddress);

public interface INetiqDirectoryClient
{
    /// <summary>列出該 Sentinel 管理的全部主機。連線/認證失敗擲 NetiqDiscoveryException（訊息可示人）。</summary>
    Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer server, CancellationToken ct);
}
```

- 實作一：`SentinelRestDirectoryClient`——Sentinel REST API 真連線。認證方式與端點
  屬環境細節（不同版本 Sentinel API 不同），**待真實環境驗證前不定案**，先以
  基本驗證＋可設定端點路徑實作，錯誤訊息完整落 log。
- 實作二：`StubNetiqDirectoryClient`——Development 環境注入，回傳固定示範資料
  （三個 /24 網段、約 60 台、其中數台與既有主機重複），整條 UI 流程可離線開發與驗收。
- DI：`ServiceCollectionExtensions` 依環境切換（Development→Stub，其餘→Rest）。

### 1.3 API（Maintain 能力）

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `/api/admin/netiq/servers` | Sentinel 清單＋各自可否掃描（帳密齊備） |
| POST | `/api/admin/netiq/scan` | `{ server }` → 掃描並回網段分組結果（見下），結果同時暫存 token（30 分鐘，同 ImportService 模式） |
| POST | `/api/admin/netiq/import` | `{ token, selectedIps: [] }` → 匯入勾選主機 |

掃描回應：

```jsonc
{
  "token": "…",
  "subnets": [
    {
      "cidr": "10.1.2.0/24",
      "totalCount": 37,
      "existingCount": 5,        // 已登錄（HostName 比對，不分大小寫）
      "hosts": [ { "hostName": "SRV-A", "ipAddress": "10.1.2.11", "exists": true }, … ]
    }
  ]
}
```

### 1.4 UI（主機頁「從 NetIQ 匯入」精靈，三步）

1. 選 Sentinel（不可掃描的顯示原因）→ 掃描（loading 明示「正在向 Sentinel 查詢」）。
2. 網段核取清單：每列 `10.1.2.0/24（37 台，5 台已登錄）`，勾網段＝勾整段；
   可展開逐台調整。逐台分三類（§1.7 的重疊比對在此兌現）：
   - **新主機**：預設勾選；
   - **已存在（使用中）**：預設不勾（再勾＝更新該台的 DisplayName/Sentinel 歸屬）；
   - **與停用主機重疊**（帶 `OrphanedFromSentinel` 標記且 IP 一致）：獨立分類醒目顯示
     「原屬 {舊 Sentinel}，因移除而停用」，**預設勾選**——勾選匯入＝原列復活
     （同 HostId，歷史/群組/負責人零斷裂），非新增一列。
3. 預覽（新增 N 台、更新 M 台、**重新啟用 R 台**）→ 套用 → 結果摘要＋稽核。

### 1.5 寫入語意

沿用 `NetiqHostService` Upsert：`HostName=IP`（NetIQ 來源慣例）、`DisplayName=掃描到的主機名`、
`Source='netiq'`、IP 衝突軟處理、既有 GroupIds/OwnerUserIds/RoleDesc 保留。
重新啟用（重疊類）額外做：`Active=true`、`NetiqServer=新 Sentinel`、
`OrphanedFromSentinel=null`。

### 1.6 邊界與測試

- 掃描逾時（30 秒）→ 明確錯誤，不留半套狀態（掃描是唯讀操作）。
- 同 IP 重複出現在掃描結果 → 保留第一筆並列入「略過」清單。
- 測試：Stub 走全流程（掃描→勾選→匯入→稽核）；網段分組純函數單元測試；
  已存在主機更新不洗掉群組/負責人（釘既有 Upsert 慣例）；
  重疊復活保留 HostId 與全部關聯（歷史、群組、負責人、處理狀態）。

### 1.7 Sentinel 生命週期：移除與汰換

需求：Sentinel 自設定移除 → 停用其所屬主機；移除舊＋加入新（汰換）→ 停用後，
新 Sentinel 掃描結果與停用主機重疊者，重新綁定到新 Sentinel。

**模型增補**：`WebHost` 加 `OrphanedFromSentinel`（string?，預設 null）——
「這台因為哪台 Sentinel 被移除而遭系統停用」。要獨立欄位而不是從
「Active=false ＋ NetiqServer 不在設定中」推導，是為了把**系統停用**與
**管理員手動停用**分開：手動停用代表人已表態不要這台，汰換 Sentinel 時
不得替人反悔自動復活；只有帶此標記的主機才進 §1.4 的「重疊」分類。

**孤兒偵測與停用（批次啟動時）**：

- 位置：批次啟動流程、主機登記（Touch）之前，新增 `NetiqOrphanSweeper`：
  掃 `Source='netiq' && Active && NetiqServer 有值` 的主機，其 `NetiqServer`
  不在當前 `NetIq.Servers[]` 名單 → `Active=false`、`OrphanedFromSentinel=舊名`。
- 為什麼放批次而不是 Web：設定檔的主人是批次（唯一事實來源既有決策），
  且不停用的後果正是批次面的——這些主機永遠不會被任何一輪查詢帶到，
  變成「看起來在監控、實際沒人看」的靜默黑洞（README「沒告警 ≠ 沒問題」
  在主機生命週期上的版本）。停用讓狀態誠實：未回報卡與主機頁看得見。
- 稽核：系統帳號一筆彙總（「Sentinel 'X' 已自設定移除，停用所屬主機 N 台」＋逐台明細
  進 detail），批次 console/log 同步 WARN。
- **安全欄杆（防設定檔誤刪）**：`Servers[]` 為空但存在使用中的 netiq 主機時，
  **跳過**孤兒處理並記 ERROR（「Sentinel 名單為空但有 N 台 NetIQ 主機，疑似設定檔
  損毀，未執行自動停用」）——整段被註解/檔案壞掉不該演變成全站停用。
  單一 Sentinel 移除（名單非空）照常處理。

**Web 呈現**：

- 主機頁 banner：存在 `OrphanedFromSentinel` 主機時顯示
  「N 台主機因 Sentinel 移除而停用——若已架設新 Sentinel，請用『從 NetIQ 匯入』
  重新綁定」，點擊帶篩選進主機清單。
- `NetiqOverviewDto` 加 `OrphanedCount`（與既有 PendingAssignment/IpConflict 並列）。

**重疊比對規則（汰換的第二段）**：

- 主鍵比對：新掃描結果的 IP 與孤兒主機的 `HostName`（NetIQ 來源即 IP）完全一致
  → 進 §1.4「重疊」分類，預設勾選，匯入即復活重綁。
  這與既有 Upsert 的自然鍵語意（FindByName(ip)）完全一致，不引入新比對機制。
- **DisplayName 相同但 IP 不同**（機器搬到新 Sentinel 順便換了 IP）：
  只列入精靈的「可能是同一台」提示區，**不自動勾選、不自動綁定**——
  名稱比對自動綁定違反「主機識別採純人工綁定」的既有定案（2026-07-21），
  由使用者自行判斷後走既有的人工合併流程。
- 手動路徑不受影響：主機頁既有的啟用/停用照常可用；手動重新啟用一台
  孤兒主機時一併清除 `OrphanedFromSentinel`（人已表態，標記使命結束）。

**邊界與測試**：

- Sentinel 改名（設定中移除舊名＋加入新名）：效果同汰換——舊名主機停用、
  新名掃描重疊全中、精靈一次復活。SOP 寫進 README 設定說明。
- 同時移除多台 Sentinel：逐台分組稽核。
- 手動停用在先、Sentinel 移除在後：該主機已 Active=false，sweeper 不碰
  （不覆蓋 OrphanedFromSentinel＝null 的手動語意），掃描重疊時列「已存在（停用中）」
  不自動勾。
- 測試：sweeper 停用範圍正確（不碰 local 來源/已停用/待歸屬 null 者——
  待歸屬主機沒有所屬 Sentinel，移除任何 Sentinel 都不影響它們）；
  空名單欄杆；復活流程全關聯保留；手動停用不被自動復活。

---

## 2. 負責人員匯入（Phase A）

### 2.1 CSV 契約（`ImportKind.Owners`，owners.csv）

```
host_name,ip_address,owner_account
SRV-OO-WEB01,10.1.2.11,DOMAIN\wangxm
SRV-OO-WEB01,10.1.2.11,DOMAIN\lidh      ← 同主機多列＝多位負責人
,10.2.3.21,DOMAIN\chenyt                ← host_name 空白時以 IP 比對
```

- RequiredHeaders：`owner_account` ＋（`host_name` 或 `ip_address` 至少一欄有值，逐列驗證）。
- 進既有 CSV 匯入頁（模板下載/預覽/套用/稽核全繼承 ImportService 框架）。

### 2.2 比對與寫入規則

1. 先以 `host_name` 找主機（不分大小寫）；查無且 `ip_address` 有值 → 以 IpAddress 欄比對
   （多台同 IP → 錯誤列：「IP 對應多台主機，請改用 host_name」）。
2. 兩欄都給且指向不同主機 → 錯誤列（交叉驗證不一致）。
3. 查無主機 → 錯誤列（主機不自動建立——主機的建立途徑是批次 Touch / NetIQ 匯入 /
   hosts.csv，負責人檔不該成為第四條建立途徑）。
4. **帳號不存在 → 自動建立**（DisplayName=帳號、User 角色、無使用者群組、Active）。
   預覽以獨立區塊明列「將新增 N 個帳號」。與 hosts.csv 的「擋下」刻意不同：
   兩千台情境手動先建帳號不現實，且帳號真偽在 LDAP 模式登入時自然驗證。
5. 取代語意：檔案中出現的主機，其 OwnerUserIds **整組取代**為檔案內容；
   未出現的主機不動。同列重複帳號去重。

### 2.3 測試

多負責人聚合、IP fallback、交叉驗證不一致、自動建帳號（含重跑冪等）、
取代不累加、未出現主機不動。

---

## 3. 網段綁定主機群組（Phase A）

### 3.1 `CidrMatcher`（Core 純函數）

```csharp
public static class CidrMatcher
{
    /// <summary>解析 "10.1.2.0/24"、"10.1.2.*"、"10.1.2.15"；非法格式回 null（呼叫端轉驗證錯誤）</summary>
    public static CidrRange? Parse(string pattern);
    public static bool Matches(CidrRange range, string ipAddress);
}
```

- 支援：CIDR（/8～/32）、萬用字元（`10.1.*`＝後綴全部）、單一 IP。IPv4 only（環境無 v6 需求）。
- 單元測試釘邊界：網段界線（.0/.255）、/31//32、前導零拒收、萬用字元位置限制（只允許尾端連續段）。

### 3.2 API（Maintain 能力）

| 方法 | 路徑 | 說明 |
|---|---|---|
| POST | `/api/admin/host-groups/{id}/members/preview` | `{ pattern }`（網段）或 `{ query }`（hostname/IP 關鍵字）→ 命中主機清單（含現有群組、是否已在本群組） |
| POST | `/api/admin/host-groups/{id}/members` | `{ hostIds: [], removeFromOthers: bool }` → 套用＋稽核 |

預覽回應每列：`{ hostId, hostName, ipAddress, currentGroups: [], alreadyInTarget }`。

### 3.3 UI（群組管理頁「批次加入成員」）

- 模式切換：網段 / 搜尋，共用同一張預覽表。
- **已屬其他群組 → 顯性通知**：該列「現有群組」欄以主色徽章列出，表頭統計
  「N 台已屬其他群組」。預設仍勾選（GroupIds 本允許多重），可逐台取消；
  「同時移出原群組」為明確的 checkbox 選項（預設關）。
- `alreadyInTarget` 列顯示「已在本群組」且不可勾。
- 比對範圍同時涵蓋 HostName 與 IpAddress 欄（NetIQ 主機 HostName 即 IP，本機主機兩欄不同）。

### 3.4 測試

CidrMatcher 邊界全套；preview/apply 一致性；removeFromOthers 只動預覽中勾選的主機；
墓碑列（MergedInto != null）排除在命中結果外。

---

## 4. SQL Server 後端（Phase C，工程最大）

### 4.1 範圍

- EF Core ＋ SQL Server provider；schema 依 docs/DB-PLAN.md 既有定案
  （lf_daily_records / lf_top_issues / lf_record_categories / lf_record_alerts /
  lf_deep_dive_analyses / lf_weekly_checkups / lf_hosts / lf_users / lf_reports /
  lf_permission_changes / lf_record_handling(+log) …）。
- **schema 增補**：`lf_issue_handling`（host_name, date, issue_key, status, actor_id,
  actor_account, note, updated_at；PK＝前三欄）——對應本輪新增的 IssueHandling 模型。
- 各 store 新增 EF 實作、StorageFactory 加 "SqlServer" case；Service 層零修改。
- 寫入路徑以 `CategoryAggregator` 填 lf_record_categories（DB-PLAN 一致性機制 #4/#5）。
- `--import-history`：history.txt＋webdata 一次性匯入器，自然鍵冪等、可重跑、
  分批交易（每 1000 筆一批，中斷可續）。

### 4.2 驗收

- 合約測試同一組案例跑雙後端全綠（尤其 Hosts 空集合＝零結果的授權語意）。
- 2000 台 × 90 天種子資料：儀表板/問題查詢/報表 P95 < 1 秒。
- JSONL 模式行為零改變（既有部署不受影響）。

---

## 5. 兩千台量級的 Web 呈現調整（Phase D，依賴 C）

| 區域 | 調整 | 細節 |
|---|---|---|
| 儀表板 | 未回報主機改**計數卡＋下鑽** | 卡片「N 台超過 2 天未回報」→ 點入主機頁（未回報篩選預置）；不再整表渲染 |
| 儀表板 | 新增**依群組風險概況** | 每群組一列：主機數/高風險日/中風險日/未處理數，點列 → 問題查詢帶群組篩選。兩千台的主要動線是「先群組後下鑽」 |
| 問題查詢 | 主機篩選改**搜尋式 autocomplete** | 輸入 2 字元後查 `/api/hosts?query=`（伺服器端前綴比對、上限 20 筆）；已選主機顯示為可移除 chip |
| 問題查詢 | 篩選列加**主機群組 chip** | 後端 `RecordSearchRequest` 加 `GroupIds`，展開為主機集合後交集可見範圍 |
| 主機管理 | 伺服器分頁＋搜尋＋篩選 | 篩選：來源/Sentinel/群組/啟用/未回報；預設每頁 50 |
| 執行監控 | 矩陣改彙總 | 每日一列：成功/失敗/未跑計數＋失敗主機名（上限 10 台＋「其他 N 台」）；點日期 → 該日異常清單 |
| 全站 | 清單 API 一律分頁 | pageSize clamp 既有（≤200），新端點一體適用 |

---

## 6. AI 介入（Phase E，W1＋W2）

原則：**程式能確定性算的不交給 AI**；AI 只做「幫人看懂、幫人排序」。
輸入一律是已彙總的結構化統計（prompt 小），輸出短（≤200 tokens），
koboldcpp no-thinking 下實測目標 3~5 秒。

### 6.1 基礎建設

- `AIService` 自批次專案搬至 Core（namespace 不變 `LogForesight`，批次側零修改；
  Core 需加 Polly 套件參照，NLog 已有）。既有的單一請求佇列（SemaphoreSlim(1,1)）
  正好保護單卡 GPU：Web 與批次各自行程各自排隊，時段天然錯開（批次凌晨、Web 日間）。
- AI 位址設定沿用「批次 appsettings 唯一事實來源、Web 唯讀」模式（同 Sentinel 名單）。
- Web 端獨立參數：逾時 10 秒（批次 600 秒不適用互動情境）、MaxTokens 256。
- 快取：`webdata/ai_cache.json`（`JsonCollectionFile` 基底；鍵＝功能＋日期＋輸入雜湊，
  值＝AI 輸出＋產生時間；啟動時清 7 天前舊項）。SQL 階段轉表。
- 失敗行為鐵律:任何 AI 失敗都靜默降級——卡片隱藏、按鈕恢復、頁面功能不受影響。
- 安全：AI 輸出永遠 `textContent` 呈現；AI 回傳的下鑽參數必須通過白名單驗證
  （類別/風險層級/日期格式）才組連結，驗不過就只顯示文字不給連結。

### 6.2 W1-1 儀表板「今日焦點」

- 輸入：本期彙總 DTO（風險日數、分類統計、主機排行前 5、關聯訊號清單）——全是現成資料。
- Prompt 契約：回 JSON `{"items":[{"text":"…","link":{"categories":"…","riskLevels":"…"}}]}`，
  最多 3 條、每條 ≤ 60 字。
- 呈現：儀表板頂部卡「AI 今日焦點」；快取鍵＝日期＋輸入雜湊（資料沒變不重算，
  同日多人瀏覽只有第一人觸發呼叫）；載入中顯示骨架、逾時整卡消失。

### 6.3 W1-2 查詢結果 AI 歸納

- 前置（確定性、不靠 AI）：後端對目前查詢結果做跨主機同簽章聚類
  （Source+EventId 分組 → 主機數、總次數、日期範圍），取前 5 組。
- AI 只做最後一哩：把聚類結果講成 ≤ 3 句白話（「7 台主機同日出現 disk 153，疑似共通儲存設備」）。
- 觸發：結果列上方「AI 歸納」按鈕（使用者主動點，不自動呼叫——查詢頁高頻，
  自動呼叫會把 AI 佇列塞滿）；同查詢條件雜湊快取。

### 6.4 W2 詳情頁快速判讀

- 觸發：問題列展開面板內「AI 判讀」按鈕（僅未命中規則的「其他」類別顯示——
  規則命中的已有靜態知識庫，重複給 AI 講一次是浪費）。
- 輸入：該問題簽章＋趨勢欄位＋當日關聯訊號＋範例訊息（截 500 字元）。
- 輸出：兩句話（「要不要緊」＋「先做什麼」），≤ 100 字；快取鍵＝主機＋日期＋issue_key。

### 6.5 測試

- AIService 搬遷後批次測試全綠（僅組件搬移）。
- Web AI 各功能：AI 成功/逾時/回傳非 JSON 三態的 UI 行為（成功渲染、其餘靜默）；
  快取命中不發第二次請求；下鑽參數白名單驗證（惡意參數只顯示文字）。
- koboldcpp 實測：no-thinking 下三個功能各自 < 5 秒（超標就砍輸入篇幅，不放寬逾時）。

---

## 7. 施工順序與相依

```
Phase A（可並行，不依賴 SQL）：負責人匯入 ＋ 網段綁定群組
Phase B：NetIQ 探索匯入（Stub 先行，真連線待 Sentinel 環境）
Phase E：AI 基礎建設 → W1 → W2（不依賴 SQL，可提前）
Phase C：SQL Server 後端（合約測試護航）
Phase D：量級 UI 調整（依賴 C）
```

## 8. 統一驗收

- 匯入類（A/B）全走 Preview→Apply；預覽數字與套用結果一致；稽核完整。
- Phase C：合約測試雙後端全綠；2000 台種子資料主要頁面 P95 < 1 秒。
- Phase E：AI 掛掉時所有頁面功能不受影響（純加值層）；快取命中零 AI 呼叫。
- 批次端相依提醒（不在 Web 範圍）：取數走 NetIQ 遠端 pipeline（docs/PLAN.md）；
  AI 每日總覽 no-thinking 3~5 秒/台 × 2000 ≈ 2~3 小時，「低風險日不呼叫 AI」
  既有策略是主要減量手段，必要時再加並行度控制。
