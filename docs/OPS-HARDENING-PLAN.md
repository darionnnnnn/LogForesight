# 營運強化與主機停用隱藏規劃（OPS-HARDENING-PLAN）

> 2026-07-27 規劃版。三項待決策已由使用者依建議定案（遵循定案 13／停用主機處理狀態編輯一併鎖定／
> handling_log 與 perm_changes 本輪不清）；P1-2 排序下推的範圍決策使用者選擇「加欄位，做到底」。
> **執行進度**：批次 1（N-1、P0-2、文件修正）、批次 2（P0-1 SchemaUpgrader、
> lf_log_lines.created_at、P0-3 清理與設定頁）、批次 3（P0-4 SQL 重試、P0-5 LF_CRYPTO_KEY）、
> 批次 4（P1-2 分頁下推）、批次 5（P1-3 Windows Service＋README 部署文件、P1-4 export 清理/版本號/CI）
> 已全數完成並測試通過（見底部「執行記錄」）。本規劃案 P0/P1 範圍已全部落地，P2 維持 backlog。
> 範圍：P0 營運債（schema 升級、dev 金鑰封鎖、log 清理、SQL 重試、加密金鑰來源）、
> P1（查詢分頁下推、Web 部署文件、營運小項）、以及新需求「主機停用後隱藏歷史資料」。
> 本文所有「現況」描述均已對照 2026-07-27 的原始碼逐一驗證，file:line 為當日位置。

---

## 0. 結論總覽

| 項目 | 建議 | 風險 | 依賴 |
|---|---|---|---|
| N-1 主機停用隱藏 | `VisibilityService` 單點加 `Active` 過濾 | 低（單點、可逆） | 無 |
| P0-1 schema 升級 | **遵循既有定案 13：自製冪等 DDL**，不採 EF Migrations（與原提案不同，見 §2） | 中 | 無（但 P0-3、P1-2 依賴它） |
| P0-2 dev 金鑰封鎖 | `Validate()` 加已知 dev 值黑名單 | 低 | 無 |
| P0-3 lf_log_lines 清理 | 批次啟動時 Prune＋SystemSettings 新欄位＋設定頁 | 低 | P0-1（需加時間戳欄） |
| P0-4 SQL 重試 | `EnableRetryOnFailure`＋包 `EfJsonBlobStore.Mutate` 的交易 | 中（交易點已證實存在） | 無 |
| P0-5 加密金鑰 | `LF_CRYPTO_KEY` 環境變數＋解密雙金鑰 fallback | 低 | 無 |
| P1-2 分頁下推 | 新增 `QueryPage`；可下推條件推到 SQL、殘餘條件記憶體驗證 | 中 | P0-1（稽核時間欄） |
| P1-3 Web 部署 | `UseWindowsService()`＋README 部署章節 | 低 | 無 |
| P1-4 小項 | export 清理／版本號／CI 一次帶掉 | 低 | 無 |

**需要決策的三點**（建議已列，定案後才動工）：
1. **P0-1 與 DB-PLAN 定案 13 衝突**：定案 13（2026-07-24）明文「不用 EF Core Migrations、採自製冪等 DDL」。本文建議遵循定案 13；若要改採 EF Migrations 應明文推翻該定案並更新 DB-PLAN。
2. **N-1 停用主機的處理狀態編輯是否一併封鎖**：單點過濾方案下會自然封鎖（404），建議接受；若要「唯讀可看」需另開例外，複雜度上升。
3. **P0-3 各 log key 的保留政策**（§4 表），特別是 `handling_log` 與 `perm_changes` 建議本輪**不**清理。

---

## 1. N-1 主機停用後隱藏歷史資料（新需求）

### 1.1 需求語意

主機 `Active=false`（管理頁手動停用或 Sentinel 移除觸發的系統停用）後：
- 歷史紀錄頁（明細／依主機／依日期彙總）不再出現該主機的任何紀錄；
- 儀表板所有計數（總主機數、風險日、類別卡、排行、群組風險、待辦）不計入；
- 報表（KPI、趨勢、排行、簽章查詢）不計入，含「前期比較」的分母；
- 資料**只保留在資料庫**，不刪除；重新啟用後全部復原（完全可逆）。

### 1.2 現況盤點（已驗證）

- `VisibilityService.GetVisibleHostIds()`（`LogForesight.Web/Services/VisibilityService.cs:55`）**完全不看 `Active`**——停用主機今天在所有查詢中照常可見。
- 所有紀錄查詢都經過 `RecordRepository.Query/GetOne`（`Repositories/RecordRepository.cs:44,58`），而它強制以 `GetVisibleHostIds()` 交集——**單一咽喉點存在**。消費端：`RecordQueryService`（含 ClusterSignatures）、`DashboardService`、`ReportService`、`HandlingService`（待辦推導自傳入的 records）。
- 主機下拉選單 `HostsController.cs:37` **已經**過濾 `h.Active`；儀表板的無回報計數（`DashboardService.cs:160`）與群組風險（`:178`）也已看 `Active`。唯 `TotalHosts`（`:55`）與紀錄類統計未過濾。
- 墓碑列（合併來源）也是 `Active=false`（`JsonHostStore.cs:122`），但其歷史**必須**持續經由存活主機可見——`RecordRepository.VisibleHostKeys()`（`:77`）是從可見主機出發做別名展開，墓碑不必自己在可見集合內。
- 批次端 `NetiqHostList.Listed`（`Core/Models/NetiqHostList.cs:18`）已要求 `Active`——停用主機不會再產生新資料，批次不需要改。
- 主機管理頁 `HostAdminService.GetHosts` **不經過** VisibilityService，`inactive` 篩選 chip（`HostAdminService.cs:123`）續存——管理者仍看得到停用主機本身（這正是「資料還在」的入口）。

### 1.3 方案

**方案 A（建議）：`VisibilityService.GetVisibleHostIds()` 單點排除 `Active=false`**

```
// GetVisibleHostIds() 兩個分支（ViewAll 與群組授權）都改為只納入 h.Active 的主機
```

- ViewAll 分支（`VisibilityService.cs:64`）與群組授權分支（`:93`）各加 `.Where(h => h.Active)`。
- 這是 WEB-SPEC §7.1 明文的「不可繞過的最後防線」，語意本來就是「這台主機的資料你現在看不看得到」——把「停用」納入正是這個抽象該管的事。

**方案 B（不建議）：各消費端自行過濾**——觸點至少 6 處（RecordQueryService×4、Dashboard、Report），且未來新查詢頁忘了加就漏，正是 RecordRepository 註解裡「散落各 Service 遲早有人忘」要避免的形狀。

### 1.4 方案 A 的全影響面（逐點確認）

| 消費端 | 影響 | 評估 |
|---|---|---|
| `RecordRepository.Query/GetOne` | 停用主機的紀錄自動消失（含 HostId=0 舊紀錄——名稱 fallback 的名單同樣來自可見集合） | ✅ 正是需求 |
| `RecordRepository.VisibleHostKeys` | 從「可見（=啟用）主機」展開墓碑，墓碑歷史照常歸戶到存活主機 | ✅ 不受影響 |
| 儀表板 `TotalHosts`／類別卡／排行／風險日計數 | 全部自動排除 | ✅ 正是需求 |
| 報表 KPI／趨勢／排行／前期比較 | 全部自動排除，分母一致 | ✅ 正是需求 |
| `HandlingService` 待辦（`GetTodo` 吃已過濾 records） | 停用主機的待辦從儀表板消失 | ⚠ 語意副作用 1（見下） |
| `RecordQueryService.GetHostDetail`（`:363` EnsureVisible） | 停用主機的主機詳情頁回 404 | ✅ 一致（管理頁仍可看主機本身） |
| `HandlingService`（`:447` EnsureVisible） | 停用主機的處理狀態不能再編輯（404） | ⚠ 語意副作用 2 |
| `PermissionChangeService`（`:91,:131`） | 停用主機的待確認權限異動隱藏、不計入儀表板 pending 數 | ⚠ 語意副作用 3 |
| `HostsController` 下拉、Silent 計數、GroupRisk | 原本就過濾 Active，變成雙重過濾 | ✅ 無害 |
| 主機管理頁（HostAdminService） | 不經 VisibilityService，完全不受影響 | ✅ 需求要的「資料入口」 |
| 稽核頁（AuditQueryService） | 不以主機為軸過濾，停用主機相關的**操作稽核**仍可見 | ✅ 建議維持（稽核是人的操作紀錄，不是主機資料；隱藏反而違反稽核完整性） |

**語意副作用（需求確認，建議全部接受）**：
1. 停用當下**處理中／逾期的待辦立即從儀表板消失**——主管視角的未結案數會下降。替代解（停用前擋「還有未結案」）會把停用變成流程，建議不做，改在管理頁停用時的確認文案提醒。
2. 停用主機的處理狀態**連編輯都不行**（不只是看不到）。要恢復操作＝先重新啟用。
3. 停用主機的權限異動確認會懸置（不會過期也不困擾任何人；重新啟用後回來）。

### 1.5 測試

- `VisibilityServiceTests`：停用主機不在可見集合（ViewAll 與群組授權兩分支各一案）；重新啟用後回來。
- 新增整合案：停用主機後 `RecordQueryService.Search` 不含其紀錄；`DashboardService.GetSummary` 的 `TotalHosts`／風險日計數排除；墓碑（合併來源）的歷史仍經存活主機可見（防回歸——這是本改動最容易誤傷的點）。
- `HostAdminServiceTests`：`inactive` 篩選不受影響（既有案續跑即可）。

---

## 2. P0-1 資料庫 schema 升級機制

### 2.1 現況（已驗證）＋與既有定案的衝突

- schema 全靠 `EnsureCreated()`（`StorageFactory.cs:60`），對既有 DB 完全不動，屬實。
- **但 DB-PLAN.md:416「Schema 升級機制（定案 13，2026-07-24）」已明文決策**：屆時採**自製冪等 DDL**（開機檢查→缺什麼補什麼），**不用 EF Core Migrations**——理由是雙 provider migration 歷史的長期維護成本，以及自製 DDL 更貼近現有「EnsureCreated 全有全無」的心智模型。
- 原提案（EF Migrations＋baseline）與定案 13 直接衝突。

### 2.2 建議：遵循定案 13，落實「SchemaUpgrader（自製冪等 DDL）」

推翻三天前的正式定案需要新事實；目前沒有——近期實際需要的 DDL 只有兩件小事（P0-3/P1-2 要在 `lf_log_lines` 加時間戳欄、可能加索引），冪等 DDL 完全夠用，EF Migrations 的 baseline 判斷／雙 provider 驗證成本反而更高。

**設計**：
- 新增 `LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs`：`Upgrade(LfDbContext ctx)`，在 `StorageFactory.GetDbFactory` 的 `EnsureCreated()` 之後呼叫（同一個 `_schemaLock` 內，批次與 Web 都會走到）。
- 內容為一串**冪等步驟**：每步「檢查（查 information_schema／PRAGMA table_info）→ 缺才補（ALTER TABLE ADD COLUMN／CREATE INDEX IF NOT EXISTS）」。Sqlite 與 SqlServer 的存在性檢查語法不同，以 provider 分支各寫一句（步驟少，不值得抽象層）。
- 一張 `lf_schema_version`（key-value 一列）**不是必要**：冪等檢查本身就是狀態，不引入版本號心智負擔；若步驟多到需要跳過已執行者再考慮。
- 每步記 log（`[SQL] schema 升級：lf_log_lines 補 created_at 欄`），失敗顯性拋出（沿用 `StorageFactory.cs:66` 的 fail-fast）。
- 測試：合約測試新增「舊 schema 的 Sqlite 檔（手工 CREATE 不含新欄）→ Upgrade → 欄位存在且可寫讀」。

**代價**：每一次 schema 變更都要手寫一步 DDL＋檢查。以本專案的變更頻率（定案 13 的判斷依據）可接受。

---

## 3. P0-2 已提交的公開 JWT 金鑰無 Production 封鎖

現況屬實：`LogForesight.Web/appsettings.json:45,60` 是公開已知的 `SecretKey` 與 serverAdmin `PasswordHash`；`WebAppSettings.Validate()`（`Configuration/AppSettings.cs:31`）擋 Production+Stub、擋短金鑰，但不擋「帶已知 dev 值上 Production」。

**作法（維持原提案方案 A）**：
- `WebAppSettings` 加私有常數清單 `KnownDevSecrets`（現行 appsettings.json 的 SecretKey 與 PasswordHash 兩個字串）。
- `Validate(isProduction)` 內：`isProduction` 且 `Jwt.SecretKey`／`Auth.ServerAdmin.PasswordHash` 命中清單 → 加入 errors，訊息指引 `Jwt__SecretKey`／`Auth__ServerAdmin__PasswordHash` 環境變數（與 appsettings.json:17-18 的既有註解一致）。
- 非 Production 不擋——本機測試合法使用這組值。
- 測試：`WebAuthTests` 比照既有 Stub 檢查案，新增「Production＋dev SecretKey → 啟動失敗」「Production＋覆寫後的值 → 通過」。
- 維護規則寫進常數旁註解：**未來再提交任何測試金鑰，必須同步加入此清單**（這是方案已知的殘餘風險）。

---

## 4. P0-3 lf_log_lines 無限成長

現況屬實：`EfJsonLogStore`（`Core/Persistence/Sql/EfJsonLogStore.cs`）只有 Append/Read；批次的既有 Prune 在 `Program.cs:445`（分析紀錄）；稽核查詢全撈記憶體過濾（`JsonAuditLogStore.cs:60,94`）。

### 4.1 資料層（依賴 §2 的 SchemaUpgrader）

- `lf_log_lines` 加 `created_at`（datetime，NULL 允許——既存列無值）＋ `(log_key, created_at)` 索引，由 SchemaUpgrader 補。
- `EfJsonLogStore.AppendLine` 寫入 `created_at = DateTime.Now`。
- 新增 `int Prune(DateTime cutoff)`：`DELETE WHERE log_key=@key AND created_at < @cutoff`（SQL 端整批刪，不撈回記憶體）。**`created_at IS NULL` 的既存列不刪**——無法斷定年代，寧可留著（它們是有限存量，隨保留期自然變成少數）。
  - 替代（不建議）：從行內 JSON 抽時間戳逐行判斷——各 key 的 JSON 結構不同（AuditEntry.OccurredAt、batch run 各自欄位），逐 key 寫解析器且全撈記憶體，違反本項的初衷。

### 4.2 保留政策（per-key，需求確認）

| log key | 政策 | 理由 |
|---|---|---|
| `batch_runs`、`batch_run_logs` | `RunLogRetentionDays`（預設 90） | WEB-SPEC §11-6 既定規劃 |
| `import_logs` | `RunLogRetentionDays` | 同屬執行歷程 |
| `audit` | `AuditRetentionDays`（預設 730） | WEB-SPEC §11-6 既定規劃 |
| `handling_log` | **本輪不清理** | 處理歷程是業務敘事（「為何當時不處理」），與稽核不同軸；要清理應獨立決策 |
| `perm_changes` | **本輪不清理** | 有「待確認」狀態機，逐筆確認前刪除等於湮滅告警 |

### 4.3 設定與觸發

- `SystemSettings`（`Core/Models/SystemSettings.cs`）加 `RunLogRetentionDays=90`、`AuditRetentionDays=730`；驗證下限（如 ≥7／≥90）在 `SystemSettingsService.Save`，比照 `RetentionDays` 的既有防呆（`SystemSettingsService.cs:62`）。
- `/admin/settings` 設定頁補兩欄（DTO：`SettingsDtos.cs`）——**使用者要求保留天數可在 Web 設定**，與現行 `RetentionDays` 同頁同機制。
- 觸發點：批次 `Program.cs:445` 既有 Prune 旁，依系統設定逐 key 呼叫 `EfJsonLogStore.Prune`。沿用「排程屬批次、Web 不養常駐工作」的既定架構；批次長期沒跑則 Web 端照樣成長，但那本身是 Runs 頁要抓的異常（原提案已載明，接受）。
- 測試：Prune 契約測試（Sqlite）——cutoff 前後、NULL 列不刪、不同 key 互不影響。

---

## 5. P0-4 SQL 無暫時性錯誤重試

現況屬實：全案無 `EnableRetryOnFailure`。**且原提案的「需檢查交易使用點」已證實命中**：`EfJsonBlobStore.Mutate` 使用 `ctx.Database.BeginTransaction()`（`Core/Persistence/Sql/EfJsonBlobStore.cs:46`）——這是所有 blob store（hosts/users/settings/…）的共用寫入路徑，execution strategy 與使用者自開交易不相容，不處理會在啟用重試後直接拋 `InvalidOperationException`。

**作法**：
- `StorageFactory.GetDbFactory` SqlServer 分支：`UseSqlServer(cs, o => o.EnableRetryOnFailure(maxRetryCount: 5))`。Sqlite 不加。
- `EfJsonBlobStore.Mutate` 的交易段改為：

```csharp
var strategy = ctx.Database.CreateExecutionStrategy();
strategy.Execute(() => { using var tx = ctx.Database.BeginTransaction(); ...; tx.Commit(); });
```

  - 無重試 provider 下 `CreateExecutionStrategy()` 回傳 NonRetrying 策略、行為不變——Sqlite 測試路徑照常，**不需要**分支。
  - 注意：`Execute` 內的委派必須可整段重放（冪等）。`Mutate` 本來就是「讀→改→寫＋樂觀鎖重試」的迴圈，重放安全；需確認委派內沒有捕捉外部可變狀態（實作時逐一檢視）。
- 全案再 grep 一次 `BeginTransaction|TransactionScope` 收尾（目前僅此一處，文件註 `WEB-SPEC.md:824` 同步更新）。
- 測試：既有 `EfJsonBlobStore` 契約測試在 Sqlite 上驗證包裝後行為不變（樂觀鎖衝突重試案續跑）。SqlServer 的實際重試行為無法在 CI 重現，靠 code review＋正式環境 log 觀察（每次重試 EF 會記 warning）。

---

## 6. P0-5 CryptoHelper 內嵌 AES 金鑰

現況屬實：`Core/CryptoHelper.cs:23` 內嵌金鑰，保護 `Sentinel.PasswordEnc` 與 `SystemSettings.AiApiKeyEnc`；類別註解自承混淆、並預告「日後改環境變數，介面不必變」。

**作法（原提案方案 A＋輪替細節）**：
- 靜態建構時讀 `LF_CRYPTO_KEY`（base64、必須恰為 32 bytes，格式錯誤→拋例外 fail-fast，不靜默退回）；未設定→沿用內嵌金鑰＋記一次 WARN（「正式環境建議設定 LF_CRYPTO_KEY」）。
- **解密雙金鑰 fallback**：`Decrypt` 先用現用金鑰，失敗（CryptographicException）再試內嵌舊金鑰——這讓「設定 LF_CRYPTO_KEY 當下、DB 裡還是舊密文」的過渡期不中斷；任何一次重存（管理頁儲存 Sentinel／AI 設定）就換成新金鑰密文。`Encrypt` 永遠只用現用金鑰。
  - 不做 `enc:v2:` 新前綴——金鑰換了但演算法沒換，前綴語意是「格式」不是「金鑰版本」；雙 key try 的成本可忽略（低頻操作）。
- 批次與 Web 同機共用同一把機器層級環境變數（README 部署章節寫明，見 §8）。
- 測試：`SystemSettingsService` AI 金鑰加密路徑（原清單的測試補強項）與 CryptoHelper 單元測試（env 金鑰加解密 round-trip、舊密文 fallback 解密、壞 base64 fail-fast）一起補。環境變數用 `SetEnvironmentVariable` 注入測試域需注意並行——建議 CryptoHelper 金鑰解析抽成 `internal static` 可注入函數，測試不動真 env。

---

## 7. P1-2 查詢先全撈再記憶體分頁

現況屬實（全部驗證）：
- `IAnalysisRecordQuery.Query` 無分頁（`Core/Persistence/IAnalysisRecordQuery.cs:53`）；`RecordQueryService` 記憶體 Skip/Take（`:127,:204`）。
- `EfAnalysisRecordStore.Query`（`Sql/EfAnalysisRecordStore.cs:174`）只下推日期／風險／host id 粗篩；**category/severity/eventId/source 全在記憶體**（`RecordFilterMatcher`）；`lf_top_issues` 有寫入（`:77-88`）但查詢端從未使用。
- 稽核 `JsonAuditLogStore.Query` 全表 `ReadAll()` 再過濾（`JsonAuditLogStore.cs:60`）。

**作法（增量、不動既有介面語意）**：

1. **新方法不動舊的**：`IAnalysisRecordQuery` 加 `PagedRecords QueryPage(RecordQueryFilter filter, int page, int pageSize)`；既有 `Query` 保留給批次與不分頁呼叫端。JSONL 已退役，只有 EF 一個實作要寫。
2. **下推層次**（EfAnalysisRecordStore）：
   - 已下推：日期、風險、host id。
   - 新下推：category／severity／eventId／source 以 `lf_top_issues` 的 `EXISTS` 子查詢（維度表當初就是 filter-only 設計，索引已建）。
   - **不可下推的殘餘**：HostId=0 舊列的名稱比對（刻意留在記憶體，`EfAnalysisRecordStore.cs:190-195` 的 collation 理由）、以及 `RecordQueryService` 的 Statuses/Overdue 過濾（狀態由 handling 資料推導，DB 不知道）。
   - 策略：**無殘餘條件時** SQL 端 `ORDER BY + OFFSET/FETCH` 真分頁；**有殘餘條件時**退回「SQL 過濾＋全窗撈回→記憶體殘餘過濾→分頁」，並在 log 標示走了哪條路。這保住正確性（Total 數字不能錯），把最常見的查詢（無狀態篩選）變快。
   - 語意守門：契約測試以同一組資料比對 `Query`（記憶體過濾）與 `QueryPage`（下推）結果逐位一致——這是把當初「記憶體與 SQL 語意一致」的設計原則搬到新路徑。
3. **排序**：清單頁的「風險→關聯→日期」排序中「有無關聯訊號」在 JSON 內。下推排序需把 `RiskRank` 對應到 `risk_level` 欄（可 CASE WHEN）＋日期；關聯訊號項只能近似或加欄。建議本輪排序下推做「風險→日期」，關聯訊號從排序鍵**暫時退位**（畫面仍顯示圖示）——或接受有殘餘時的全窗路徑。實作時擇一，先與使用者確認清單頁排序是否可簡化。
4. **稽核**：`IJsonLogStore` 加 `ReadPage(skip, take, desc)`（`(log_key, seq)` 索引已在）；date range 下推用 §4 的 `created_at` 欄（與 P0-3 同一次 schema 變更）。`JsonAuditLogStore.Query` 改成：條件全空時走 ReadPage 快路徑；有條件時仍撈範圍內（以 created_at 預篩）再記憶體過濾。`Count`（登入失敗卡）同樣以 created_at 預篩。

---

## 8. P1-3 Web 部署文件 ＋ P1-4 營運小項

### P1-3（維持原提案方案 A）
- `LogForesight.Web.csproj` 加 `Microsoft.Extensions.Hosting.WindowsServices`，`Program.cs` 加 `builder.Host.UseWindowsService()`（console 啟動無影響）。
- README 新增「Web 部署」章節：`sc create` 範例、Kestrel HTTPS（appsettings Kestrel 區段綁 pfx，憑證手動更新入 runbook）、環境變數清單（`ASPNETCORE_ENVIRONMENT=Production`、`Jwt__SecretKey`、`Auth__ServerAdmin__PasswordHash`、`LF_CRYPTO_KEY`）、防火牆限縮、與批次同機的目錄配置。

### P1-4（已驗證現況）
- **export 清理**：`FileReportSink` 寫 `export/*.txt`（`Program.cs:286`），全案無任何清理（已 grep 證實）。在 `Program.cs:445` 既有 Prune 旁依 `RetentionDays` 同步清理（以檔名日期或 LastWriteTime 判斷；檔名有固定日期前綴，用檔名較準）。
- **版本號**：無 `Directory.Build.props`（已證實）——新增並統一 `<Version>`；`--selftest` 輸出與 Web 頁尾顯示。
- **CI**：無任何 workflow（已證實）——最低限度一條 `dotnet build && dotnet test`（GitHub Actions 或本機 script，單人開發先求提交必跑測試）。

---

## 9. 測試補強與文件修正（隨對應批次帶）

測試（優先順序照原清單）：
1. `PermissionFilter` 403＋稽核（WEB-SPEC §12 明文要求，目前不存在）——獨立可先做。
2. `SystemSettingsService` AI 金鑰加密路徑——隨 P0-5。
3. `ImportService` 協調器、`AIService` JSON 容錯——次優先。
4. 其餘 Web 服務層依動到哪補到哪（本計畫會動到 `DashboardService`／`AuditQueryService`，隨 N-1／P1-2 補）。

文件修正（first batch 順手）：
- `NETIQ-API-PLAN.md` 標頭「尚未實作」→ 改為現況（SentinelClient/probe 已完成、待真實 probe 輸出）。
- `PLAN.md:288` DPAPI 段落 → 依 §6 定案改寫。
- `WEB-SPEC.md` §13 Phase 5「SQL 暫緩」／§12 引用已刪的 `JsonlAnalysisRecordStoreTests` → 更新；`:824` 的 Mutate 交易描述隨 §5 更新。
- `NETIQ-WEB-CONFIG-PLAN.md:116` `/admin/sentinels` → `/admin/netiq`。
- `DB-PLAN.md` 定案 13 段落 → 標註「已於本計畫落實」（若定案維持）。

---

## 10. 建議實作順序

```
批次 1（無依賴、風險低）：N-1 主機停用隱藏 ＋ P0-2 dev 金鑰黑名單 ＋ 文件修正
批次 2（schema 基礎）   ：P0-1 SchemaUpgrader ＋ lf_log_lines.created_at ＋ P0-3 清理與設定頁
批次 3（連線與金鑰）   ：P0-4 EnableRetryOnFailure（含 Mutate 改造）＋ P0-5 LF_CRYPTO_KEY
批次 4（效能）         ：P1-2 QueryPage 下推 ＋ 稽核 ReadPage（用批次 2 的欄位）
批次 5（部署與雜項）   ：P1-3 Windows Service＋README ＋ P1-4（export 清理/版本號/CI）
```

依賴關係：批次 2 是批次 4 的前置；其餘互相獨立。每批次一個 feature branch、跑全測試後合併。

P2（NetIQ 接線／EVTX 離線匯入／伺服器端 CSV 匯出）維持 backlog 不排；伺服器端 CSV 匯出屆時與 P1-2 的 QueryPage 同路徑實作（原清單的建議正確——匯出不該再走全撈）。

---

## 11. 執行記錄

### 批次 1（2026-07-27，已完成）

- **N-1**：`VisibilityService.GetVisibleHostIds()`（`LogForesight.Web/Services/VisibilityService.cs:63`）在 ViewAll 與群組授權兩分支之前先以 `.Where(h => h.Active)` 過濾主機清單，單點涵蓋全部查詢型 Service。墓碑列的歷史經 `RecordRepository.VisibleHostKeys` 從存活主機展開，不受影響（該處直接用 `_hosts.GetAll()`，不經過 VisibilityService 的過濾）。新增 3 條測試（`VisibilityServiceTests.cs`）。
- **P0-2**：`WebAppSettings`（`LogForesight.Web/Configuration/AppSettings.cs`）新增 `KnownDevSecrets` 黑名單，`Validate(isProduction)` 命中即 fail-fast。新增 4 條測試（`WebAppSettingsValidationTests`，`WebAuthTests.cs`）。
- 文件修正：NETIQ-API-PLAN.md、PLAN.md（DPAPI 段落改寫為實際 CryptoHelper 方案）、WEB-SPEC.md（§13 Phase 5、§12 死測試引用）、NETIQ-WEB-CONFIG-PLAN.md（路由歷史註記）。

### 批次 2（2026-07-27，已完成）

- **P0-1 SchemaUpgrader**：新增 `LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs`，於 `StorageFactory.GetDbFactory` 的 `EnsureCreated()` 後呼叫。以 `pragma_table_info`/`pragma_index_list`（SQLite）與 `INFORMATION_SCHEMA.COLUMNS`/`sys.indexes`（SqlServer）判斷欄位/索引是否存在，缺才用 `ALTER TABLE`/`CREATE INDEX` 補。識別字組字串一律先組成區域變數再呼叫 `ExecuteSqlRaw`（避免 EF1002 內插字串警告，值本身皆為內部常數非外部輸入）。4 條測試（`SchemaUpgraderTests.cs`）：舊 schema 補欄位、補索引、既存列 CreatedAt 維持 null、新 schema 上重複執行冪等不拋例外。DB-PLAN.md 定案 13 段落已更新標註「已落實」。
- **lf_log_lines.created_at**：`LogLineRow` 新增 `DateTime? CreatedAt`（`LfDbContext.cs`），`EfJsonLogStore.AppendLine` 寫入時間戳記。
- **P0-3 清理**：`IJsonLogStore` 加 `int Prune(DateTime cutoff)`（`CreatedAt == null` 的既存列不刪）；`IBatchRunStore`／`IImportLogStore`／`IAuditLogStore` 各加 `int Prune(int retentionDays)` 委派至底層。**未**動 `IRecordHandlingStore`／`IPermissionChangeStore`（依決策，業務敘事與待確認狀態機本輪不清）。`SystemSettings` 新增 `RunLogRetentionDays`(90)／`AuditRetentionDays`(730)，`SystemSettingsService`／`SettingsDtos`／`/admin/settings` 頁（Settings.cshtml + settings.js）三處同步串接，Web UI 手動驗證過（瀏覽器實測存值 45→重新整理→讀回 45→改回 90）。批次 `Program.cs` 於既有 Prune 段落旁（`historyService.Prune` 之後）呼叫 `batchRunStore`／`ImportLogStore`／`AuditLogStore` 的 Prune，包在 try/catch 內不中斷主分析流程。7 條新測試（`EfJsonLogStorePruneTests.cs` 4 條、`SystemSettingsServiceTests.cs` 2 條、`FakeImportLogStore` 補介面實作）。

### 批次 3（2026-07-27，已完成）

- **P0-4 SQL 重試**：`StorageFactory.GetDbFactory` 的 SqlServer 分支加 `EnableRetryOnFailure(maxRetryCount: 5)`；Sqlite 不動。`EfJsonBlobStore.Mutate` 原本的 `ctx.Database.BeginTransaction()` 與 execution strategy 不相容（啟用重試後會直接拋 `InvalidOperationException`），改為 `probe.Database.CreateExecutionStrategy().Execute(() => { using var ctx = _contextFactory(); ... })`——每次執行策略重試都用全新 `DbContext`，避免變更追蹤殘留上一次嘗試加入的列。Sqlite 上 `CreateExecutionStrategy()` 回傳 `NonRetryingExecutionStrategy`，對現有測試行為零影響（全量測試套件跑過，無回歸）。新增 1 條測試（`EfWebdataStoreTests.Mutate_遇到暫時性例外時自動重試並成功落地`，直接注入 `DbUpdateException` 驗證外層重試迴圈仍正常運作——未嘗試用雙 `DbContext` 模擬真實並發，因為 `EfSqliteFixture` 為了讓 in-memory DB 跨 context 保留內容而共用同一條實體連線，這與正式環境「不同連線」的並發語意不同，直接丟例外更精準穩定）。
- **P0-5 加密金鑰**：`CryptoHelper`（`LogForesight.Core/CryptoHelper.cs`）改讀環境變數 `LF_CRYPTO_KEY`（base64，需恰為 32 bytes），未設定時 fallback 內嵌金鑰並記一次 WARN；格式錯誤或長度不對一律 fail-fast（不靜默當作未設定）。`Decrypt` 現用金鑰解不開時自動退回內嵌金鑰再試一次，支援金鑰輪替過渡期（DB 裡舊金鑰時代密文仍解得開；任一次重新加密即換成新金鑰密文）。金鑰解析抽成 `internal static ResolveKey(string? envValue)` 純函數，`Encrypt`/`Decrypt` 的核心邏輯抽成 `internal static EncryptWith`/`DecryptWith`（接受金鑰參數）——測試藉此直接驗證各種情境，完全不碰真的環境變數（避免 xUnit 平行執行測試類別時互相干擾）。新增 12 條測試（`CryptoHelperKeyResolutionTests`）涵蓋：未設定/空白回內嵌金鑰、非法 base64 與長度不對 fail-fast、合法金鑰採用、指定金鑰往返、雙金鑰 fallback 成功、兩把都解不開時仍拋例外。另外補了原清單提到的 `SystemSettingsService` AI 金鑰加密路徑測試（4 條：加密存放不留明碼、DTO write-only 不外洩、留空沿用既有金鑰、ClearAiApiKey 清除）。
- **未做**：README 部署章節的 `LF_CRYPTO_KEY` 說明留給批次 5（P1-3）——那裡會一次寫完整的 Web 部署環境變數清單（`ASPNETCORE_ENVIRONMENT`／`Jwt__SecretKey`／`Auth__ServerAdmin__PasswordHash`／`LF_CRYPTO_KEY`），現在單獨補一小段之後還要重寫，不如一次到位；`CryptoHelper` 類別本身的 XML doc 已完整說明用途與設定方式。

### 批次 4（2026-07-27，已完成）

範圍決策：使用者選擇「加欄位，做到底」——`lf_daily_records` 加 `has_correlation` 欄，讓問題查詢頁
清單排序（風險等級→有無關聯訊號→日期）三鍵全部可下推，而不是只做部分下推留下排序退位的妥協。

- **schema**：`DailyRecordRow` 加 `HasCorrelation`（bool，預設 false）；`EfAnalysisRecordStore.Append` 寫入時同步計算 `shaped.CorrelationAlerts.Count > 0`；`SchemaUpgrader` 補上既有 DB 的欄位升級步驟（`AddColumnIfMissing` 的簽章順手改為接受完整欄位定義字串，才能表達 `NOT NULL DEFAULT 0`，`created_at` 呼叫端同步補上明確的 `NULL` 後綴）。實測（見下）：真的在既有 dev DB 上啟動一次，log 確認補欄位成功、無錯誤。
- **查詢下推**：`EfAnalysisRecordStore` 抽出共用的 `ApplyPushableFilters`（`Query`／`QueryPage` 共用單點），新增 Category／MinSeverity／EventId／Source 以 `lf_top_issues` EXISTS 子查詢下推——這張維度表當初就是為 filter-only 設計，索引已建，此前查詢端從未用到。`Query()` 沿用既有記憶體排序＋分頁（批次與不分頁呼叫端用）；新增 `QueryPage(filter, page, pageSize)`：偵測資料庫是否存在 `HostId=0` 舊列（一次 `Any()` 查詢，有索引很便宜）——沒有就 SQL 端 `CASE WHEN`（風險等級）＋`HasCorrelation`＋`RecordDate` 三鍵排序＋`OFFSET/FETCH` 真分頁；有就退回「SQL 過濾＋整批撈回→記憶體排序＋分頁」，正確性優先。7 條契約測試（`RecordQueryTests.cs`）逐位驗證兩條路徑與 `Query()` 語意一致，含排序正確性、跨頁完整性、授權空集合語意、HostId=0 退回路徑。
- **稽核分頁**：`IJsonLogStore` 加 `ReadLines(from,to)`（不分頁窄化）與 `ReadPage(skip,take)`（全表真分頁）；`JsonAuditLogStore.Query` 完全無篩選條件時走 `ReadPage`（SQL 端分頁，不必先讀全表——稽核頁的預設檢視就是這個情境）；有任何篩選條件時以 `created_at` 範圍先在 SQL 端窄化候選集（沒有時間戳記的既存列一律視為候選，精確判斷交給記憶體端既有的 `Matches`），其餘欄位維持原本的記憶體過濾。`Count`（儀表板登入失敗卡）同樣改用範圍窄化。10 條測試（`JsonAuditLogStorePageTests.cs`）涵蓋兩條路徑與 null-CreatedAt 既存列的精確性。
- **Web 層串接**：`RecordRepository` 加 `QueryPage`（與 `Query` 共用 `ApplyVisibility` 授權過濾邏輯）；`RecordQueryService.Search` 依 `request.Statuses`／`request.Overdue` 是否有值分支——兩者皆無時走新的 `QueryPage` 快速路徑（只為**當頁**載入處理狀態，這是 2000 台規模下清單頁最常見瀏覽情境的效能關鍵路徑）；任一有值時退回既有「全撈→算處理狀態→篩選→排序→分頁」邏輯（該邏輯本身未改動，因為 Statuses/Overdue 依賴的 handling 資料不在 SQL 裡，天生無法只看某一頁）。`Search()` 先前**零測試覆蓋**，新增 7 條端到端測試（`RecordQueryServiceSearchTests.cs`，真串接 `EfAnalysisRecordStore`＋`RecordRepository`，不是重新實作簡化邏輯）涵蓋排序、分頁、處理狀態顯示、授權邊界、兩條路徑的篩選正確性。
- **手動驗證**：啟動真實 Web 服務對著既有 dev SQLite DB 跑——`/records` 頁確認明細排序/處理狀態/狀態篩選（慢速路徑）都正確；`/audit` 頁確認無篩選（快速路徑，56 筆分頁）與依動作篩選（慢速路徑，narrowing 到 12 筆）都正確；全程 0 個瀏覽器主控台錯誤、0 個伺服器錯誤 log；schema 升級 log 確認 `has_correlation` 在真實既有資料庫上補欄成功。

### 批次 5（2026-07-27，已完成）

- **P1-3 Windows Service**：`LogForesight.Web.csproj` 加 `Microsoft.Extensions.Hosting.WindowsServices`，`Program.cs` 開頭加 `builder.Host.UseWindowsService()`——對一般 `dotnet run`／直接執行 `.exe` 完全無影響（實測瀏覽器對著本機啟動的站台走一輪，行為不變），只在被服務控制管理器啟動時才切換生命週期管理。
- **P1-3 README 部署文件**：新增「Web 部署」章節（`sc create` 服務範例、Kestrel HTTPS 憑證設定含環境變數覆寫密碼、正式環境必用環境變數清單彙整——含批次 3 延後至此的 `LF_CRYPTO_KEY` 說明、防火牆限縮、與批次同機的目錄配置範例）。
- **P1-4 export 清理**：新增 `LogForesight/Service/ExportReportPruner.cs`（獨立可測試類別，未寫成 Program.cs 的內嵌 local function——這是刪檔案的邏輯，值得有測試覆蓋）。依檔名固定的 `yyyy-MM-dd` 前綴（`RiskReportService.BuildFileName`）判斷是否超過 `RetentionDays`，比 LastWriteTime 更準；遞迴掃描涵蓋 NetIQ 多主機情境的 `export\{host}\` 子目錄。批次 `Program.cs` 在既有清理段落旁呼叫，同樣包 try/catch。10 條測試（`ExportReportPrunerTests.cs`）涵蓋邊界日、子目錄、格式不符略過、目錄不存在。
- **P1-4 版本號**：新增根目錄 `Directory.Build.props`（`<Version>1.0.0</Version>`，MSBuild 自動套用到所有專案，取代各自散落的預設 1.0.0.0）；`SelfTestRunner` 輸出標頭加版本號；Web `_Layout.cshtml` 側欄頁尾加版本顯示（實測 `--selftest` 印出「版本 1.0.0.0」、瀏覽器 DOM 確認頁尾顯示「v1.0.0.0」）。
- **P1-4 CI**：新增 `.github/workflows/ci.yml`，`windows-latest`（net8.0-windows 讀 Windows Event Log／AD，只能在 Windows 建置測試）跑 `dotnet build --configuration Release` ＋ `dotnet test --configuration Release --no-build`，push 與 PR 都觸發。單人開發先求「提交必跑測試」，不上 lint/覆蓋率/多環境矩陣。本機以 Release 組態實跑驗證過整個指令序列（804/804 通過）才寫進 workflow，不是憑空假設 CI 環境行為。

批次 1+2+3+4+5 合計新增 69 條測試，總數 735→804，全數通過；建置 0 警告 0 錯誤（Debug 與 Release 組態皆驗證過）。尚未 commit（工作目錄含批次 1-5 的所有變更）。**本規劃案 P0＋P1 範圍已全部完成**，P2（NetIQ 接線／EVTX 離線匯入／伺服器端 CSV 匯出）維持 backlog，等對應觸發條件成立（真實 Sentinel probe 輸出／實際離線調查需求／P1-2 QueryPage 基礎已就位可隨時排）再開專案計畫。
