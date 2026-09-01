# 回饋第 36 輪規劃
> 狀態：批次A、B 皆完成已併 dev（3202 綠）；待實測探測結果後另輪規劃 sensor 縮圈
> 基準：dev@320c082（3197 綠）
> 來源：使用者回饋（SQLite 慢查詢、PRTG 探測未設定即靜默失敗、探測結果評估）

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | PRTG 探測解析修正（unit 樣本、IPv4 覆蓋交叉統計）＋探測/回填按鈕錯誤顯示 | 小 | 無；完成後先推 dev，取得新探測結果 |
| B | 慢查詢：索引（雙後端）＋ Sqlite PRAGMA ＋ ActionableOccurrences 查詢層快取 | 中 | 無 |

建議順序：A → 推 dev → B。sensor 擷取縮圈（規則對應才收）**待批次A 的新探測結果出爐後另行規劃**。

## 批次A：探測解析修正與錯誤顯示

### 現況與核對結果
1. `PrtgProbeRunner.cs:69` 向 `table.json?content=sensors` 要 `unit` 欄——PRTG sensors 表**沒有這個欄位**，未知欄位被靜默忽略，因此所有 type 的 unit 樣本恆為「無」。單位資訊實際存在於 `lastvalue`（格式化字串，如 `92 %`、`12 kbit/s`）。
2. `PrtgProbeRunner.cs:174-177` dependency 判定：PRTG 每個 sensor 預設相依父物件，`dependency` 欄幾乎必為非空 → 100% 是預設值假象，輸出未加註。
3. `settings.js:1556-1571`（探測）與 `1522-1544`（回填）：`api.post(..., {silent:true})` ＋空 catch，後端 400 的訊息（如「尚未設定 PRTG 連線位址」）被完全吞掉，畫面靜默。對照「測試連線」（1257-1293）有前置空值檢查＋就地顯示，不一致。VS 看到的例外是偵錯器第一次機會中斷，後端行為（`ApiExceptionFilter` → 400＋訊息）正確。

### 定案
- unit 樣本改由 `lastvalue` 推導（去除前導數值後取單位字串）；`unit` 欄保留作 fallback（若未來版本提供）。
- 新增「依 type 的 IPv4 device 覆蓋」統計：sensors 取 `parentid`，與步驟 6 的 device host 資料交叉，回答「規則對應的 type 中有多少 sensor 落在可對應主機的 device 上」——直接支撐下一輪縮圈規劃。不多打 API（重用步驟 3 與 6 的既有查詢）。
- dependency 輸出加一行預設值註記，不改判定邏輯。
- 前端探測/回填：加 URL 空值前置提示（比照測試連線），catch 就地顯示 `error.message`。

### 改動
1. `PrtgProbeRunner.cs`：columns 加 `lastvalue,parentid`；`SensorTypeSample` 增欄；lastvalue 單位萃取（`-`、空值、純數字 → null）；步驟 6 後輸出 [7] 依 type 的 IPv4 覆蓋；dependency 註記行。
2. `PrtgProbeRunnerTests.cs`：補 lastvalue 推導、IPv4 交叉統計、`-`/髒值容錯測試。
3. `settings.js`：`bindPrtgProbe`／`bindPrtgBackfill` 錯誤就地顯示＋前置檢查。

### 測試／驗收
- `dotnet test` 全綠（基線 3197＋新增）。
- 手動：未設 URL 按探測/回填 → 畫面就地出現訊息；設好後探測輸出含 unit 樣本與 [7] 區塊。

## 批次B：慢查詢（雙後端合適性為前提）

### 現況與核對結果
四支慢查詢在 `EfIssueAggregateQuery.cs`（主表 `lf_top_issues`；ActionableOccurrences join `lf_daily_records`）。根因：
1. `lf_daily_records.risk_level` 無索引（ActionableOccurrences 主瓶頸）；`event_id IN＋日期範圍` 只有 `(record_date, source_name, event_id)` 可用，前導欄為範圍等於掃全期間。
2. Sqlite 零 PRAGMA（6GB 檔用預設 2MB cache）＋ `Pooling=False`（StorageBackend.cs:280，規避 EF user-function 釋放 bug，維持）→ 每查詢冷 cache。
3. `ActionableOccurrences` 是四支中唯一無查詢層快取者；啟動期 HostedService 無條件 `Bump()` 打掉 `SummaryCache`，故重跑一次。

### 定案（含 SQL Server 合適性）
- **B-1 索引（雙後端皆建）**：`lf_daily_records(risk_level, record_date)`、`lf_top_issues(event_id, record_date)`。兩者都是標準 B-tree 複合索引，SQL Server 語意相同、同樣受益；照既有慣例 model（EnsureCreated）與 `SchemaUpgrader.AddIndexIfMissing` 兩處維護。
- **B-2 PRAGMA（僅 Sqlite 分支）**：連線開啟時設 `cache_size`（負值 KB，約 256MB）與 `mmap_size`；不動 journal_mode（WAL 影響部署檔案佈局，列入不做）。SQL Server 分支不涉入。
- **B-3 快取**：`ActionableOccurrences` 納入版本戳快取（比照 `SummaryCache` 的 `DataVersionStamp` 機制），與後端無關、雙後端同受益。
- 記憶體聚合下推（A4）**先不做**：待 B-1/B-2 實測後若仍超門檻再議。

### 測試／驗收
- `dotnet test` 全綠；SchemaUpgrader 索引冪等測試（比照既有 `AddIndexIfMissing` 測試樣式，Sqlite＋SQL Server DDL 兩型）。
- 實測：啟動後儀表板首載 [SQL][慢] 四支查詢應明顯下降或消失。

## 明確不做（本輪定案）
- **sensor 擷取縮圈**（只收規則對應的 type、可擴充新增）：待批次A 新探測結果（unit 樣本＋IPv4 覆蓋交叉）出爐後另輪規劃。
- journal_mode=WAL：對讀多場景有利但改變部署檔案佈局（-wal/-shm），暫緩。
- 記憶體聚合下推 SQL（A4）：索引與 PRAGMA 實測後再議。
- 啟動期 HostedService 無條件 `Bump()` 收斂：影響僅冷啟兩次重算，B-3 落地後成本已低，記入 BACKLOG。
- dependency 100% 假象的深入分析（`dependency_raw` 分辨預設/人工）：分析層尚未用到相依性，只加註記。
