# NetIQ 主機清單 Web 維護與主機配對規劃

> 規劃日期：2026-07-21。本文件規劃三件事：
> (1) admin 在 Web 上維護 NetIQ 主機清單（取代 docs/PLAN.md 的 per-Sentinel txt 檔）；
> (2) NetIQ 主機與既有主機（CSV 預先登錄或本機回報建立）的**配對**；
> (3) 群組歸屬與 Sentinel 歸屬**脫鉤**——現有主機群組未必對應相同的 Sentinel。
>
> **修訂紀錄**
> - 第一輪（2026-07-21）：初版，識別鍵以 IP/主機名為自然鍵的方案。
> - 第二輪（2026-07-21）：三項使用者定案——**紀錄改以主機資料表 PK（HostId）關聯**
>   （取代自然鍵方案，原「矛盾 1」消解）；**Sentinel 歸屬未指定時由批次自動確認**
>   （節流、不瞬間大量查詢）；**IP 重複改軟處理**（不擋存檔，衝突佇列＋人工處置）。

## 識別設計（第二輪定案：PK 關聯）

### 核心：紀錄與主機以 `HostId` 關聯，不再以名稱/IP 字串比對

- `DailyAnalysisRecord` 新增 **`HostId`**（long）；既有 `Host` 字串欄位**保留**，
  角色降為「寫入當下的顯示名快照」＋舊資料相容（見下方遷移）。
- 批次寫入紀錄前一定先取得主機列（本機 `Touch`、NetIQ 走清單 provider——
  Web 維護模式下清單項目本身就有 `HostId`，**批次從清單直接拿到 PK，不需任何名稱解析**）。
- Web 查詢改以 `HostId` 比對：`RecordQueryFilter.HostNames` → `HostIds`。
  `VisibilityService` 本來就回傳 host id 集合，中間「id → 名稱」的轉換層整個移除，
  查詢路徑反而變簡單。
- DB 階段零轉換：`lf_daily_records.host_id` FK 正是 docs/DB-PLAN.md 既有欄位設計，
  JSONL 期就把關聯鍵寫對，匯入器不需要做名稱→id 的推斷。
- **原「矛盾 1」（IP vs Sentinel 主機名何者為鍵）消解**：身分＝PK；
  IP 只是「對 Sentinel 下查詢的條件」（監控屬性）；主機名只是顯示屬性。
  `WebHost.HostName` 在 NetIQ 列＝admin 登錄時的識別字串（通常填 IP），
  `DisplayName`＝Sentinel 回報的主機名（批次回填）；兩者都可改、都不影響紀錄歸戶。

### 舊資料相容與遷移

既有 `history.txt` 的紀錄只有 `Host` 字串、沒有 `HostId`：

- **查詢 fallback**：讀到 `HostId == 0/null` 的舊紀錄時，退回以 `Host` 字串
  （不分大小寫）比對主機列的 `HostName`——舊資料不遷移也查得到。
- **可選一次性回填**：`--backfill-hostid` 指令把 history 逐行補上 `HostId` 後整檔重寫
  （對不到主機列的行保留原樣並列警告）。建議在切換後找一天執行，讓 fallback 路徑退役。
- 兩用其一即可，合約測試對 fallback 行為加案例釘住。

### PK 方案的三個代價（必須正視，不是免費的）

1. **`hosts.json` 升級為身分錨點**：以前紀錄靠字串自我描述，主機檔壞了紀錄還在；
   改 PK 後 `hosts.json` 遺失/重建＝id 重配＝紀錄斷鏈。
   對策：`Host` 字串快照保留（斷鏈時人仍可辨認＋可重新 backfill）；
   `hosts.json` 列入部署備份清單（README 部署章節補一句）；DB 階段自然消失（identity column）。
2. **id 產生的併發**：`JsonHostStore` 配號是 max+1，批次（01:00 Touch）與 Web（白天 Upsert）
   同時建主機理論上會撞號。時段錯開後風險極低，但實作時 `JsonHostStore` 的建列路徑
   加檔案鎖（或寫入前重讀重配），成本一行，把理論風險關掉。
3. **報告/檔案命名**：per-host 報告目錄與 history 檔名（PLAN.md：`history\{host}.txt`）
   改用 `{HostId}_{HostName}` 前綴（id 保證唯一與追溯、名稱保留人類可讀性）。

### 別名展開（原「矛盾 2」，PK 下簡化）

Merge 之後查目標主機需涵蓋被併入主機的紀錄。PK 方案下實作簡化為：
查詢主機 X 時，`HostIds` = X.HostId ＋ 所有 `MergedInto == X.HostId` 的墓碑列 HostId
（一層即可；「墓碑不可再為 Merge 目標」在 `HostAdminService` 補檢查）。
這仍是**現有 Web 的缺口**（目前 Merge 後舊主機紀錄從畫面消失），不依賴 NetIQ，建議最先修。

## 核心設計決策

### 決策 A：清單即主機——不建第二張表

NetIQ 清單項目**直接就是 `WebHost` 列**（`Source='netiq'`），不新增獨立實體：

- admin 在 Web 新增清單項目 = `Upsert` 一列 `WebHost`：
  `HostName`（登錄識別字串，通常填 IP 或既知主機名）、`IpAddress`（查詢鍵，見 IP 衝突節）、
  `NetiqServer`（可留空→進入自動歸屬確認，見下）、`RoleDesc`、群組、負責人
- 批次的查詢清單 = `IHostStore.GetAll()` 篩
  `Source=='netiq' && Active && MergedInto==null && NetiqServer 已歸屬 && IP 無衝突`，
  按 `NetiqServer` 分組；provider 輸出 `(HostId, Ip, RoleDesc)`——**批次全程以 HostId 寫紀錄**
- 「停止監控」= 既有 `Active=false`（語意同 PLAN.md「移除 IP → 停止分析、history 保留」）
- 「主機搬遷 Sentinel」= 編輯 `NetiqServer`（路由屬性，PK 不變，歷史無縫延續）

### 決策 B：配對＝既有 Merge 的擴充，純人工

情境：公司先以 CSV 依主機名登錄主機（含群組/負責人），之後 NetIQ 清單接入、
同一台機器以另一列身分開始回報——兩列其實是同一台。

- **配對就是 `Merge`**：admin 在主機詳情頁把兩列併為一列。建議方向仍為
  「名稱列（CSV 登錄）併入 NetIQ 列」，因為監控設定（IP/Sentinel）在 NetIQ 列上；
  但 PK 方案下**方向不再影響歷史歸戶**（各自的紀錄掛各自的 HostId，靠別名展開聚合），
  綁錯用 `Unmerge` 可完全復原，不會有資料損失
- **Merge 擴充：描述性欄位搬移**——目標列的 `RoleDesc`/群組/負責人為空時自來源帶入
  （目標已有值則保留不覆蓋）；畫面提供併入預覽
- **`Unmerge` 反向修復**（新介面方法）：清 `MergedInto`、恢復 `Active`
- **純人工，不做程式比對**（維持 2026-07-20 決策 #12）：不自動綁定；
  目標選擇器旁列出同 `DisplayName`/同 `IpAddress` 的候選列作為**線索**，最終動作 admin 確認

### 決策 C：群組與 Sentinel 歸屬脫鉤

Sentinel 是**路由屬性**（哪台 Sentinel 查得到這台主機），群組是**授權/分類維度**：

- **不自動建立 per-Sentinel 群組**；要看「某台 Sentinel 轄下主機」是主機頁篩選條件，不是群組
- 新登錄的 NetIQ 主機未分組 → 依既有授權模型**只有 admin 看得到**（安全預設，
  新主機不會意外曝光給錯的部門）
- 配套：**「未分組主機」佇列**（主機頁篩選＋儀表板 admin 提示數）＋
  **批次指派**（勾選多台一次入組）；大量初始分組建議走既有 CSV 匯入的 `groups` 欄位

### 決策 D：清單主人交接——txt 與 Web 單一主人，設定切換 ⏸ 已廢止（2026-07-24）

> **廢止**：docs/NETIQ-WEB-CONFIG-PLAN.md 定案 12 決定 Txt 主機清單模式整支退役
> （`HostListSource`/`HostListDirectory` 設定、`TxtHostListProvider`、`--import-hosts` 全刪），
> 不是「切換」而是「拿掉其中一個主人」——Sentinel 連線設定進 Web 之後，「清單主人在 txt」的
> 過渡定位已消失，txt 內容需要匯入時改用 Web「批次貼上」（`bulk-modal`）即可，不需要專屬的
> 交接 SOP。原文保留供歷史對照：

- 批次抽 `IHostListProvider`：`TxtHostListProvider`（PLAN.md 原設計）與
  `StoreHostListProvider`（讀 `IHostStore`，本規劃）；
  設定 `NetIq.HostListSource: "Txt" | "Web"`（預設 `Txt`）
- **同一時間只有一個主人、不做雙向同步**（維持既定原則）
- 交接 SOP：`--import-hosts`（txt → host store，冪等 upsert）→ 核對 Web 主機頁筆數 →
  設定切 `Web` → txt 移除。Txt 模式下批次仍以 `Touch` 取得 HostId 後寫紀錄（PK 方案不分模式）

### 決策 E：Sentinel 名單來源——批次 appsettings 唯讀，不另建表 ⏸ 已修訂（2026-07-24）

> **修訂**：docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1、2 把單一事實來源從「批次 appsettings.json」
> 改為「共用儲存層（`sentinels` blob，`ISentinelStore`）」——當時的前提是「批次與 Web 靠
> `DataRoot` 共用檔案，設定檔就是共用點」；Phase C 之後共用點已是資料庫，讓 Web 直接管理
> Sentinel（完整 CRUD，含密碼加密）反而消除了「畫面選得到、批次卻查不到」的分歧風險。
> 「同一時間只有一個主人」原則不變，主人從 appsettings.json 換成共用 store。原文保留供歷史對照：

Web 的 Sentinel 下拉選單讀批次 `appsettings.json` 的 `NetIq.Servers`（同一 DataRoot、唯讀）。
BaseUrl/帳密只有批次需要；不建 `lf_sentinels` 表、不做 Web 端 Sentinel CRUD——
加一台 Sentinel 本來就要改批次設定，單一事實來源。

## Sentinel 歸屬自動確認（第二輪新增）

登錄/匯入時 `netiq_server` 可留空——不強迫 admin 知道每台主機歸哪台 Sentinel，
由批次自動確認，但**不能瞬間大量查詢造成 Sentinel 負擔**：

- **狀態**：`NetiqServer == null` 的活躍 NetIQ 列＝「待歸屬確認」，
  **不進**日常輪巡清單；Web 主機頁顯示狀態徽章與待確認數
- **執行者是批次，不是 Web**——Web 永遠不直接連 Sentinel（帳密與連線邏輯只存在批次，
  維持架構邊界）。每晚批次執行時處理待確認佇列；另提供 `--discover-hosts` 手動指令
  供初次大量匯入後立即跑
- **查詢方式（優化：分批聚合，不是一台一台）**：對每台 Sentinel 各發
  「這批待確認 IP（每批沿用 Q1 的分批大小，如 50 個）近 24h 是否有事件、`GROUP BY 主機`」
  的聚合查詢——成本是 `Sentinel 數 × ⌈待確認數/50⌉` 次查詢，而不是
  `待確認數 × Sentinel 數` 次逐台探測；沿用 per-server 單一併發佇列＋`QueryDelayMs` 節流，
  對 Sentinel 的負擔與日常 Q1 同級
- **每輪上限**：`NetIq.DiscoveryBatchLimit`（如每晚 500 台）封頂，超出的留下一輪——
  初次匯入 2000 台也不會在單晚打滿查詢
- **歸屬判定**：
  - 恰好一台 Sentinel 有事件 → 自動寫入 `NetiqServer`，次日起進日常輪巡（稽核記一筆「系統自動歸屬」）
  - 多台都有事件（轉送重疊）→ **不自動選**，列入 Web「多重歸屬待確認」清單附各台事件數，
    admin 人工擇定（與純人工綁定哲學一致）
  - 全部沒有 → 維持待確認並列入「查無資料」清單（IP 錯、agent 未回報、未納收錄——
    都是要人處理的事）；下一輪自動重試，重試 N 輪仍無則只留清單不再查（`DiscoveryMaxAttempts`）
- 已歸屬主機連續多日在其 Sentinel 查無資料時，既有「無資料主機」告警已涵蓋；
  是否自動重新探測留待實際營運看需求，本階段不做

## IP 重複的軟處理（第二輪定案：不擋存檔，衝突佇列）

IP 理論上全域唯一（已與網路端確認），但清單維護難免打錯或交接期重疊：

- **存檔不擋**：新增/匯入時 IP 與既有活躍 NetIQ 列重複，照樣存檔，
  但該狀況成為「IP 衝突」——**衝突是導出狀態**（同 IP 的活躍 NetIQ 列 ≥2 即衝突），
  不加欄位、沒有要維護的狀態機
- **輪巡行為**：衝突 IP 只輪巡**最早建立的那一列**（行為可預測），
  其餘列跳過並在執行 log 與機房總覽「來源狀態」記明「因 IP 衝突未輪巡」——
  不會兩列都查、重複寫紀錄
- **Web 衝突佇列**：主機頁篩選「IP 衝突」＋儀表板 admin 提示數；每組衝突並列顯示，
  處置三選：**改 IP**（打錯的情況）、**停用其中一列**（汰換交接的情況）、
  **Merge**（其實是同一台重複登錄的情況）；處置完衝突自動消失（導出狀態的好處）
- CSV 匯入遇衝突：照匯（同上），匯入結果報告列出衝突組數提醒去佇列處理

## 資料模型變更

| 項目 | 變更 |
|---|---|
| `DailyAnalysisRecord` | 新增 `HostId`（long；舊紀錄無此欄位＝0，查詢走名稱 fallback）；`Host` 保留為顯示名快照 |
| `WebHost` | 新增 `DisplayName`（Sentinel 回報主機名，批次回填；本機來源 null）；`HostName` 註解修正為「登錄識別字串，不再承擔紀錄關聯職責」 |
| `RecordQueryFilter` | `HostNames` → `HostIds`（`null`=不限、空集合=無權看任何主機的語意**必須保留**——授權正確性關鍵） |
| `IHostStore` | `TouchNetiq(long hostId, string? displayName, DateTime reportedAt)`（批次回填顯示名＋回報時間，不動 Web 欄位）；`Merge` 擴充描述性欄位搬移；新增 `Unmerge`；建列路徑加鎖防撞號 |
| `IHostListProvider`（新） | 輸出 per-Sentinel 的 `(HostId, Ip, RoleDesc)`；Txt 實作內部先 `Touch` 取得 HostId |
| 欄位所有權 | 批次寫：`LastReportAt`/`DisplayName`/`IpUpdatedAt`/自動歸屬的 `NetiqServer`。Web 寫：其餘。`MergedInto` 僅 Merge/Unmerge 路徑寫 |
| DB 對應 | `lf_hosts` 加 `display_name nvarchar(255) NULL`；`lf_daily_records.host_id` 原設計即 FK，零調整 |
| 設定 | `NetIq.HostListSource`、`NetIq.DiscoveryBatchLimit`、`NetIq.DiscoveryMaxAttempts` |

## Web UI（admin 專屬功能）

1. **主機頁擴充**：篩選（來源/Sentinel/未分組/待歸屬/IP 衝突/未配對）；
   單筆新增（IP 即時驗證格式、Sentinel 可留空=待歸屬）；
   **批次貼上**（textarea 多行 `IP[,角色描述]`，逐行驗證、不合法行列原因、合法行照常入庫）；
   停用/啟用；狀態徽章（待歸屬/查無資料/IP 衝突/多重歸屬待確認）
2. **配對操作**（主機詳情頁）：目標選擇器＋線索區（同 DisplayName/IpAddress 候選並列）；
   併入預覽（明列哪些空欄位將自來源帶入）；墓碑列標注「已併入 →」＋ `Unmerge`
3. **衝突/待確認佇列**：IP 衝突組、多重歸屬待確認、查無資料清單，各附處置動作
4. **未分組佇列**：未分組數提示＋勾選多台批次入組
5. **稽核**：新增/停用/批次貼上/配對/解除配對/衝突處置/系統自動歸屬全部寫入既有 `audit.jsonl`

## 批次端變更

| 檔案 | 變更 |
|---|---|
| `Service/HostListProviders.cs`（新） | `IHostListProvider` ＋ Txt/Store 兩實作；Store 實作負責排除待歸屬/衝突/墓碑列 |
| `Service/SentinelDiscovery.cs`（新，Phase 1 隨 SentinelClient） | 待歸屬佇列處理：分批聚合查詢、唯一命中自動歸屬、多重/無命中入清單；`DiscoveryBatchLimit`/`MaxAttempts` |
| `Program.cs` | 依 `HostListSource` 選 provider；`--import-hosts`、`--discover-hosts`、`--backfill-hostid` 指令 |
| 機房 pipeline | 紀錄一律以 `HostId` 寫入；分析後 `TouchNetiq` 回填 `DisplayName`/`LastReportAt`；「無資料主機」與「因衝突未輪巡」列入機房總覽 |

## 測試與驗收

- `JsonlAnalysisRecordStore`/查詢：**HostId 關聯＋舊紀錄名稱 fallback** 合約案例；
  `HostIds` 空集合=查無（授權語意）案例
- `HostStoreContractTests` 增：`TouchNetiq` 欄位所有權、Merge 搬移（目標空才帶入）、
  `Unmerge`、墓碑不可為 Merge 目標、建列撞號防護
- `VisibilityServiceTests` 增：未分組 netiq 主機一般使用者不可見/admin 可見
- `RecordQueryTests` 增：別名展開（併入後查目標主機看得到來源 HostId 下的紀錄）
- Discovery 單元測試（SentinelClient 抽介面 stub）：唯一命中自動歸屬、多重命中不自動、
  上限封頂、重試 N 輪後停查
- 衝突導出狀態測試：同 IP 兩活躍列→只輪巡最早列＋另一列標記；處置後衝突消失
- 端到端驗收（配合 PLAN.md Phase 2 試點）：Web 建試點清單（部分不填 Sentinel）→
  `--discover-hosts` 自動歸屬 → 批次以 `HostListSource=Web` 跑 → `DisplayName`/`LastReportAt` 回填 →
  未分組僅 admin 可見 → 入組後部門可見 → 配對一台 CSV 預登錄主機、歷史以 HostId 歸戶正確

## 實作順序

| 步驟 | 內容 | 備註 |
|---|---|---|
| 1 | `DailyAnalysisRecord.HostId`＋查詢改 `Hosts`＋名稱 fallback＋別名展開修復 | ✅ **已完成（2026-07-21）**，見下節 |
| 2 | `IHostStore` 擴充（TouchNetiq/Merge 搬移/Unmerge/建列鎖）＋合約測試 | ✅ **已完成（2026-07-21）**，見下節 |
| 3 | Web UI：清單維護＋批次貼上＋各佇列＋配對/解除 | ✅ **已完成（2026-07-21）**，見下節 |
| 4 | 批次 `IHostListProvider`＋`--import-hosts`＋`HostListSource`＋`--backfill-hostid` | ✅ **已完成（2026-07-21）**，見下節（`--backfill-hostid` 依開放問題 #1 未實作） |
| 5 | `SentinelDiscovery`＋`--discover-hosts` | 掛 Phase 1（隨 SentinelClient 一起做，分批聚合查詢與 Q1 共用機制） |
| 6 | Phase 2 試點端到端驗證 | 上節驗收清單 |

## 步驟 1 實作紀錄（2026-07-21 完成）

建置零警告、**407 單元測試**（新增 16 個）與 **76 項 `--selftest`** 全數通過。

**新增**
- `Core/Models/HostIdentity.cs`：`HostKey`（PK＋名稱快照）、`HostMatcher`（比對規則單點定義：
  PK 優先、`HostId==0` 才退回名稱）、`HostIdentityResolver`（`Expand` 別名展開／`Surviving`
  合併鏈跟隨）、`HostLookup`（紀錄→存活主機的 O(1) 索引）。
- `DailyAnalysisRecord.HostId`；`RecordStorageShaper` 同步複製（反射式測試已自動涵蓋）。
- `Tests/HostIdentityTests.cs`：解析純函數 11 案 ＋ `RecordRepository` 別名展開 5 案
  （含**授權反向防線**：未併入的其他主機不得因展開而進入可見範圍）。

**變更**
- `RecordQueryFilter.HostNames` → `Hosts`（`HostKey` 集合）；`IAnalysisRecordQuery.GetOne`
  改收識別集合，依傳入順序擇一（存活主機優先）。空集合＝查無資料的授權語意原樣保留。
- `RecordRepository`：`ResolveHostName` → `ResolveHostKeys`；可見範圍展開為
  「可見主機 ＋ 已併入它們的墓碑列」。
- `RecordQueryService`：清單/詳情/時間軸一律解析到**存活**主機；處理狀態改以存活主機名稱
  比對（否則合併前的風險日處理狀態會全部看起來像未處理）。
- `LogAnalysisService` 新增 `hostId` 建構參數；`Program.cs` 的主機登記提前到分析服務建立之前，
  以 `Touch(...).HostId` 取得 PK；登記失敗時 hostId 維持 0、走名稱 fallback，不中斷分析。
- `HostAdminService.MergeHost`：擋下「以墓碑為併入目標」。

**實作期間的三個修正（規劃時未預見）**
1. `GetHostDetail` 原本 `ToDictionary(r => r.Date.Date)`，合併當天兩個識別各有一筆時會因
   **重複鍵整頁例外**；改為依日期分組並取存活主機那筆。
2. `Expand` 原本只走一層 `MergedInto`，等於讓正確性依賴新加的寫入端守則；既有 `hosts.json`
   可能已存在合併鏈，改為**依存活主機遞移判斷**，查詢端自身即正確。守則保留為使用者體驗
   考量（併入已停用的主機看不出資料最後去哪），不再是載重的不變式。
3. 處理狀態（`handling.json`）以主機名稱為鍵，別名展開後必須用存活主機名稱查，
   否則合併前的紀錄狀態全部退回「未處理」。

**尚未處理（刻意）**：`--backfill-hostid` 未實作，依開放問題 #1 傾向永久保留名稱 fallback；
`PermissionChangeService` 與 `HandlingService.GetTodo` 仍以主機名稱運作（各自的 store 就以
名稱為鍵，不在本步驟範圍）。

## 步驟 2 實作紀錄（2026-07-21 完成）

建置零警告、**417 單元測試**（新增 10 個）與 76 項 `--selftest` 全數通過。

**新增**
- `WebHost.DisplayName`：Sentinel 回報的主機名稱，批次回填、Web 唯讀。
  NetIQ 主機以 IP 登錄，光看清單認不出是哪台機器，這個欄位補上人看得懂的名字。
- `IHostStore.TouchNetiq(hostId, displayName, reportedAt)`：以 **HostId 定位**而不是名稱——
  NetIQ 主機必定由 Web 清單維護、已經存在，用名稱補建的話 admin 打錯字就會默默多出一台幽靈主機。
  主機不存在回 null 並安靜略過（清單剛被刪的競態不該中斷分析）。
- `IHostStore.Unmerge(hostId)`：清墓碑標記＋恢復啟用；`HostAdminService.UnmergeHost`
  ＋`host_unmerge` 稽核＋`POST /api/admin/hosts/{id}/unmerge`。
- `Merge` 描述性欄位搬移：目標的角色描述／群組／負責人／顯示名／IP／Sentinel 為空時自來源帶入，
  目標已有值一律保留。**搬移是複製不是移動**，來源保留自己的值，`Unmerge` 才還原得回來。

**跨程序檔案鎖（規劃時列為「一行成本」，實作後認定必要且改在基底類別）**
- `JsonCollectionFile.Mutate` 現在同時持有行程內鎖與 `.lock` 鎖檔（`FileShare.None`＋
  `DeleteOnClose`，逾時 15 秒後讓例外外拋）。
- 原本只有行程內 `lock`＋原子替換：原子替換擋得住半截檔案，**擋不住更新遺失**——
  兩邊各自讀到舊值、後寫的整份蓋掉先寫的。`hosts.json` 正是批次與 Web 共同寫入的檔案
  （WEB-SPEC §10.2），而 §10.4 早已規定「跨程序以檔案鎖處理」，這次才真正落實。
- 後果具體：同一個 HostId 配給兩台主機（識別碼現在是紀錄的關聯鍵，撞號＝紀錄歸錯主機、
  跨越授權邊界），或批次的回報時間把 Web 剛設好的群組蓋掉。
- **用鎖檔而不是具名 Mutex**：批次由工作排程器執行、Web 是另一個行程，可能不在同一個
  登入工作階段——`Local\` Mutex 跨不過工作階段，`Global\` 需要 SeCreateGlobalPrivilege。

**尚未接線（步驟 3、4 的範圍）**：`TouchNetiq` 目前沒有呼叫端（機房 pipeline 尚未實作）；
`DisplayName` 已進 `HostDto` 但主機頁 UI 尚未顯示；`Unmerge` 的 API 已就緒但畫面按鈕在步驟 3。

## 步驟 3 實作紀錄（2026-07-21 完成）

建置零警告、**460 單元測試**（新增 43 個）、76 項 `--selftest` 通過，並**實際啟動 Web 端到端驗證**。

**Core（規則放這裡，不是 Web）**
- `Models/NetiqHostList.cs` 純函數：`Listed`／`PendingAssignment`／`IpConflicts`／`Pollable`／
  `Ungrouped`／`IsValidIp`／`ParseLine`。放 Core 的理由：步驟 4 的批次要用 `Pollable` 決定
  今晚查哪些主機，Web 要用同一組規則標示「這台為什麼沒被輪巡」——各寫一份就會出現
  「畫面說會查、批次其實沒查」，而那正是本系統最不能有的失敗方式。
- `IsValidIp` 刻意比 `IPAddress.TryParse` 嚴格：後者會把 `10.1` 收成 `10.0.0.1`，
  而清單上的 IP 是實際送去 Sentinel 篩選的條件，收下的後果是這台主機永遠查無資料。
  端到端驗證時 `10.1` 確實被擋下。

**設定（決策 E）**
- Core 加 `NetIqSettings.Servers`；批次 `appsettings.json` 加 `NetIq` 區段（空陣列佔位）。
  刻意只加這一個欄位——本專案有「有設定卻沒有對應行為會誤導使用者」的前例
  （`MaxDeepDiveHostsPerRun` 因此被移除），連線帳密等要等機房 pipeline 實作時才加。
- `NetiqServerCatalog`（Web）自 DataRoot 的批次 appsettings.json 唯讀解析，依檔案時間快取。

**Web**
- `NetiqHostService`＋API：`GET netiq/overview`、`POST netiq/hosts`、`POST netiq/hosts/bulk`、
  `PUT hosts/{id}/active`。批次貼上沿用 txt 格式（`IP[,角色描述]`、`#` 註解），
  不合法的行略過但**逐行回報行號與原因**——只說「略過 N 行」等於把系統知道的事推回給人做。
- 主機頁：待辦佇列卡（待歸屬／IP 衝突／未分組，可點擊即篩選）、狀態徽章、
  來源與 Sentinel 欄、`DisplayName` 副標、批次貼上、停用／啟用、解除合併。
  徽章只標「需要人做點什麼」的狀態——反過來做的話滿畫面徽章等於沒有徽章。

**端到端驗證（實際啟動 Web，Stub 驗證登入 admin）**
Sentinel 下拉正確帶出批次設定的名單（決策 E 成立）；批次貼上 6 行 → 新增 2 台、
略過 3 行並各自列出原因（不合法 IP／批內重複／`10.1` 簡寫）、註解行忽略；
未分組佇列由 1 → 3 即時更新；NetIQ 來源與 Sentinel 正確顯示。驗證用資料已清除。

**實作期間發現並修正的缺口**
- **驗證只掛在一條寫入路徑上**：`NetiqHostService.AddHost` 驗 IP 與 Sentinel，但
  `HostAdminService.SaveHost`（一般編輯表單，寫同一份資料）完全沒驗——從編輯表單就能
  繞過去存進不合格的值。已補上同一組驗證，並補 `HostAdminServiceTests`
  （這個 Service 先前完全沒有測試，所以連建構子改動都沒被抓到）。
- 順帶補上步驟 1、2 加入但一直沒有測試的守則：Merge 擋墓碑目標、擋已併入來源、
  `Unmerge` 對未合併主機報錯。

**尚未接線（步驟 4、5）**：`Pollable` 尚無呼叫端（機房 pipeline 未實作）；
配對的「線索區」（同 IP／同 DisplayName 候選並列）與併入預覽尚未做，
目前合併仍走既有的 API；`TouchNetiq` 待步驟 5 接線。

## 步驟 4 實作紀錄（2026-07-21 完成）

建置零警告、**473 單元測試**（新增 13 個）、76 項 `--selftest` 通過，並**實際執行 CLI 端到端驗證**。

**新增**
- `Service/HostListProviders.cs`：`IHostListProvider`＋`TxtHostListProvider`／`StoreHostListProvider`，
  輸出 `HostListResult`（依 Sentinel 分組的 `NetiqTarget(HostId, Ip, RoleDesc)`＋警告＋來源可用旗標）。
- `Service/NetiqTxtImporter.cs`：txt → 主機清單的單向覆寫同步。
- `Service/HostListCli.cs`：`--import-hosts`（Txt 模式專用）與 `--host-list`（兩模式皆可）。
- 設定 `NetIq.HostListSource`（預設 `Txt`）與 `NetIq.HostListDirectory`（預設 `hosts`）。

**關鍵設計：兩模式共用挑選尾段**
Txt 模式 = 「先以 txt 覆寫主機清單」＋ Web 模式完全相同的挑選邏輯（`HostListSelection.FromStore`）。
不是各寫一份挑選規則——這讓「換個來源、選出來的主機卻不一樣」在結構上不可能發生，
也就是步驟 4 驗收閘門的內容（對照測試逐一比對 Sentinel／HostId／IP／角色描述）。

**三個安全設計（都有測試釘住）**
1. **Web 模式下 `--import-hosts` 直接拒絕執行**：清單交接給 Web 之後再匯入 txt，會把 Web 上
   新增的主機當成「已從清單移除」而停用。這正是「同一時間只有一個主人」要防的事故，
   擋在程式裡而不是靠人記得。
2. **某台 Sentinel 的 txt 檔消失時，不停用其轄下主機**：只對「本次真的讀到檔案」的 Sentinel
   做移除判定。誤刪或檔案伺服器沒掛上，不該讓一整個機房靜默地停止被監控。
3. **「來源不可用」與「清單是空的」分開**：目錄不存在／沒有 txt → `SourceUsable=false`，
   機房分析跳過並明確提示，不會靜默當成「今天沒有主機要查」。

**排除原因逐一列出**：待歸屬、IP 衝突被跳過的主機都進 `Warnings`，console 以黃色顯示。
沿用「沒查 ≠ 沒事」原則——靜默排除等於製造一個沒人知道的監控盲區。

**端到端驗證（實際跑 exe）**：`--import-hosts` 匯入 3 台、壞行帶行號略過；移除一行後再匯入
正確停用該台並保留主機列；Web 模式下 `--import-hosts` 拒絕執行（exit 1）、`--host-list`
正常列出；無清單目錄時明確報告來源不可用。驗證用資料與設定已全部還原。

**刻意未實作**：`--backfill-hostid`（開放問題 #1）。舊紀錄的名稱 fallback 已有測試涵蓋，
90 天保留期內舊紀錄會自然輪出；整檔重寫的風險大於收益。需要時再加。

## 開放問題（第二輪）

1. **舊紀錄回填時機**：`--backfill-hostid` 建議在步驟 1 上線、確認 fallback 正常後擇日執行；
   或乾脆永久保留 fallback 不回填（90 天後舊紀錄自然輪出）。傾向後者（少一次整檔重寫風險），
   fallback 程式碼在 DB 匯入完成後才移除。
2. **多重歸屬命中是否要自動選事件數最多的那台**：本規劃選「不自動、人工擇定」
   （與純人工綁定一致，且多重命中本身就是異常值得看一眼）。若營運後發現量大再議。
3. **`DiscoveryBatchLimit` 初值**：建議 500/晚（4 台 Sentinel × 每台約 3 次分批查詢的量級），
   Phase 1 probe 後依實測調整。
