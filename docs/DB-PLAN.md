# 資料庫與 Web 查詢／AI 問答規劃（欄位級定案）

> 規劃日期：2026-07-20。本文件是 PLAN.md Phase 5 的展開，因新需求（Web 查詢＋AI 問答）提前細化。
> 原則：**現在把欄位與介面定案到「DB 一到手就能直接建表接上」的程度**，並先做好資料保全
> （見「現在就能做的準備」），DB 選型（SQL Server 或 Oracle）不影響任何已定案內容。
>
> **實作狀態（2026-07-23）：SQL 後端已完成**（SCALE-2000-PLAN Phase C）。本文件為欄位級定案的
> **設計依據**，仍然有效；實際落地的 provider 架構（**Sqlite/SqlServer 二選一**、EF Core、
> `lf_blobs`/`lf_log_lines` 抽象）與現況說明見 WEB-SPEC.md §10.5。本文以下的「待 DB 就緒」語句
> 指的是規劃當下的時序，非現況。
>
> **（2026-07-24 補記）Jsonl 檔案後端已全面退役**（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 10）：
> 沒有服役中的 Jsonl 正式資料需要遷移，`--import-history` 匯入器確定**不做**（定案 10）；
> `Storage.Type` 設成 `Jsonl` 一律於啟動時報錯。本文件下方仍以「txt/JSONL」描述當初從檔案
> 過渡到 DB 的機制設計，是規劃當下已達成目的的歷史記錄，不代表現行架構仍有 Jsonl 選項。

## 需求（2026-07-20 第二輪更新）

1. **維護人員**：進入畫面即可選「自己負責的主機」＋日期區間＋風險層級＋風險類型做搜尋，
   查找自己主機的問題；**風險報告直接在畫面顯示**（現有 txt 全文照出）
2. **主管**：一眼看出目前有哪些風險類型、數量、緊急程度
3. **AI 問答降為未來選項**：視屆時資源決定是否做；schema 保留設計但不圍繞它做任何取捨
4. ~~DB 以長期保存為目標~~ →（2026-07-20 修訂）**統一保留年限 `DbRetentionDays`=730**
   （未來三年只改設定 1095），見「保留策略」
5. DB 尚未就緒，**可能是 SQL Server 或 Oracle** → 全部設計必須雙 DB 可移植
6. LogForesight.exe 維持批次分析職責不變；Web 是獨立的查詢應用，讀同一個 DB

## 雙 DB 可移植規則（所有表遵守）

| 規則 | 原因 |
|---|---|
| **資料表一律 `lf_` 前綴**（2026-07-20 定案）、索引 `ix_lf_` 前綴；識別字全小寫 snake_case、**長度 ≤ 30 字元**（含前綴，最長 `lf_record_handling_log` = 22 ✓）、避開兩家保留字 | 前綴避免與公司共用 DB 中其他系統的表衝突、一眼可辨識歸屬；Oracle 12.2 之前識別字上限 30 bytes。大小寫說明：未加引號時 SQL Server 預設不分大小寫、Oracle 一律轉大寫（實體名即 `LF_...`），文件以小寫書寫、DDL 不加引號，兩家行為一致 |
| 型別只用兩家共通的抽象：`bigint` / `int` / `nvarchar(n)` / `text(大文字)` / `date` / `timestamp` / `bool` | 對應表見下；建表 DDL 等 DB 定案後由此機械翻譯 |
| 布林一律 `bool`（SQL Server `BIT`／Oracle `NUMBER(1)`+CHECK）；**三態布林用 nullable**（如 `security_log_available`：NULL=未嘗試） | 兩家都沒有共通的原生 BOOLEAN（Oracle 23ai 才有，不可假設） |
| 巢狀/清單資料存 **JSON 文字欄**（`text`），**不用**任何一家的 JSON 原生型別與 JSON 函式 | 解析在應用層做（同一套 System.Text.Json 模型）；避免綁死單一 DB 的 JSON 查詢語法 |
| 主鍵由 **ORM/應用層產生**（identity/sequence 由 provider 各自處理），程式碼不出現 DB 專屬語法 | EF Core 對兩家都會自動選對機制 |
| **可空文字欄位**：空字串一律正規化為 NULL 再入庫 | Oracle 把 `''` 視為 NULL，不正規化的話兩家行為不一致 |
| 不用 stored procedure / trigger / view 承載邏輯，全部在應用層 | 換 DB 零遷移成本；邏輯留在可測試的 C# |
| 分頁/日期運算交給 ORM 產生 | OFFSET-FETCH 與 ROWNUM 語法不同，手寫 SQL 會分岔 |

型別對應（實作時機械翻譯用）：

| 抽象 | SQL Server | Oracle |
|---|---|---|
| bigint / int | BIGINT / INT | NUMBER(19) / NUMBER(10) |
| nvarchar(n) | NVARCHAR(n) | NVARCHAR2(n) |
| text | NVARCHAR(MAX) | NCLOB |
| date / timestamp | DATE / DATETIME2 | DATE / TIMESTAMP |
| bool | BIT | NUMBER(1) + CHECK (0,1) |

## 資料表設計（欄位級）

設計原則：**每張表都是現有 C# 模型的一比一投影**（`DailyAnalysisRecord`、`LogIssueSignature`、
`WeeklyCheckupResult`、`PermissionChangeDetail`、深析 `DeepDiveItem`），JSONL→DB 匯入器因此是
機械化轉換，不需要任何語意判斷。

### 主機與授權（Web「只看自己負責的主機」的基礎）

```
lf_hosts
  host_id        bigint PK
  host_name      nvarchar(255)  UNIQUE NOT NULL   -- 本機=Environment.MachineName；NetIQ=Sentinel 主機名
  ip_address     nvarchar(45)   NULL              -- 最近已知 IP（45 字元容納 IPv6）
  ip_updated_at  timestamp      NULL
  netiq_server   nvarchar(50)   NULL              -- 所屬 Sentinel 的 Name（路由/顯示屬性，非識別鍵；本機為 NULL）
  role_desc      nvarchar(500)                    -- 對應 HostRoles / ServerDescription
  source         nvarchar(20)   NOT NULL          -- 'local' | 'netiq'
  active         bool           NOT NULL
  merged_into    bigint NULL FK → lf_hosts           -- 人工綁定後的墓碑指標（見「主機識別」節）
  last_report_at timestamp                        -- 最近一筆分析寫入時間（「無回報主機」告警的依據）

lf_users
  user_id        bigint PK
  account        nvarchar(255)  UNIQUE NOT NULL   -- AD 帳號（驗證交給 AD/SSO，本表只做對應與授權）
  display_name   nvarchar(255)
  email          nvarchar(255)
  is_admin       bool NOT NULL                    -- true = 可看全部主機（維運主管/資安）
  active         bool NOT NULL

lf_user_host_map                                     -- 使用者負責哪些主機
  user_id        bigint FK → lf_users
  host_id        bigint FK → lf_hosts
  granted_at     timestamp
  PK (user_id, host_id)
```

授權模型：一般使用者只能查 `lf_user_host_map` 有列的主機；`is_admin` 看全部。
**授權過濾在查詢層強制**（所有 Web API 的查詢都先 join 授權表），AI 問答的 context 組裝也走
同一條路——非管理員的問答不可能拿到別人主機的資料（見 AI 問答章節）。

### 每日分析（結構化風險資料——Web 儀表板與 AI 問答的主資料）

```
lf_daily_records                                     -- ↔ DailyAnalysisRecord
  record_id        bigint PK
  host_id          bigint FK → lf_hosts NOT NULL
  record_date      date NOT NULL
  risk_level       nvarchar(10) NOT NULL          -- 高/中/低
  error_count      int NOT NULL
  warning_count    int NOT NULL
  audit_count      int NOT NULL
  ai_analyzed      bool NOT NULL
  security_log_available bool NULL                -- 三態：NULL=未嘗試
  data_incomplete  bool NOT NULL
  headline         nvarchar(200)                  -- AI 白話標題（2026-07-20 AI 角色轉換，↔ DailyAnalysisRecord.Headline）
  summary          nvarchar(2000)                 -- AI 白話敘述（↔ Summary，序列化欄位名不變）
  trend_assessment nvarchar(2000)
  action           nvarchar(500)                  -- AI 白話行動建議（↔ Action，取代原 recommendations_json 多項清單）
  screened_tail_count  int NOT NULL
  screening_notes_json text                       -- List<string>
  uncovered_checks_json text                      -- List<string>（未檢查項目申報）
  report_id        bigint NULL FK → lf_reports       -- 有風險報告時指向全文
  created_at       timestamp NOT NULL
  UNIQUE (host_id, record_date)

lf_top_issues                                        -- ↔ LogIssueSignature（欄位一比一，趨勢數字全保留）
  issue_id         bigint PK
  record_id        bigint FK → lf_daily_records NOT NULL
  log_name         nvarchar(50) NOT NULL
  source_name      nvarchar(255) NOT NULL         -- 'source' 是 Oracle 慣用字，改名避開
  event_id         int NOT NULL
  entry_type       nvarchar(20) NOT NULL
  event_count      int NOT NULL                   -- 'count' 是保留字，改名
  category         nvarchar(20) NOT NULL          -- Storage/Hardware/Security/...
  severity         nvarchar(10) NOT NULL          -- Low/Medium/High/Critical
  known_issue      nvarchar(500) NULL             -- 命中規則表時的中文說明
  first_seen       nvarchar(5)                    -- HH:mm（沿用現有模型；跨日聚合無意義所以不用 timestamp）
  last_seen        nvarchar(5)
  distinct_msg_count int NOT NULL
  trend            nvarchar(20) NOT NULL          -- New/Rising/Recurring/Declining/Unknown
  prev_day_count   int NULL
  history_avg      float NULL                     -- SQL Server FLOAT / Oracle BINARY_DOUBLE
  days_seen        int NOT NULL
  sample_messages_json text NULL                  -- 低風險日為 NULL（精簡策略，與 JSONL 一致）
  key_details      nvarchar(1000) NULL            -- Security 事件的帳號/IP 彙總

lf_record_alerts                                     -- ↔ TrendAlerts / CorrelationAlerts（+未來 fleet）
  alert_id       bigint PK
  record_id      bigint FK → lf_daily_records NOT NULL
  kind           nvarchar(20) NOT NULL            -- 'trend' | 'correlation' | 'fleet'
  alert_text     nvarchar(1000) NOT NULL

lf_record_categories                                 -- 當日的「類別彙總」（寫入時由 lf_top_issues 算好）
  record_id      bigint FK → lf_daily_records NOT NULL
  category       nvarchar(20) NOT NULL            -- Storage/Hardware/Security/Service/Resource/Backup/Config/Other
  issue_count    int NOT NULL                     -- 該類別當日簽章數
  total_events   int NOT NULL                     -- 該類別當日事件總筆數
  max_severity   nvarchar(10) NOT NULL            -- 該類別當日最高嚴重度
  critical_count int NOT NULL DEFAULT 0           -- ↓ 2026-07-21 Web 報表需求新增：各嚴重度簽章數分解
  high_count     int NOT NULL DEFAULT 0           --   （「類別×嚴重度」堆疊圖與下鑽篩選直接查此表，
  medium_count   int NOT NULL DEFAULT 0           --    不掃 lf_top_issues；見 WEB-SPEC.md §10.4）
  low_count      int NOT NULL DEFAULT 0
  PK (record_id, category)
```

`lf_record_categories` 是為「進畫面就篩選風險類型」與主管儀表板新增的**彙總表**：
「風險類型」的篩選與統計若每次都掃 `lf_top_issues` 再聚合，畫面一開就是全表掃描；
這張表在批次寫入時一次算好（write-once，資料本來就不會變），
「本週儲存裝置類 Critical 有幾台/幾天」變成一個索引查詢。這延續整個專案的原則：
**能確定性預先算好的東西不要留到查詢時算**——批次端如此，DB 端也如此。

> 2026-07-21 增補：Web 報表的「類別×嚴重度」堆疊圖需要嚴重度分解，增列四個
> `*_count` 欄（見上）。彙總計算定義為 Core 的純函數（`CategoryAggregator`），
> SQL 寫入路徑與 JSONL 查詢期聚合共用同一份——與 `RecordStorageShaper` 同一套
> 單點原則（一致性機制 #4），分析邏輯不受影響。詳見 WEB-SPEC.md §10.4。

### 深入分析（本次規劃的關鍵新增——AI 問答與跨主機查詢需要結構化）

先前「深析只存報告全文」的延後決策**被新需求推翻**：Web 問答要能「把某主機某天的深析結果
餵給 AI 當 context」、查詢要能「跨主機找提到同一根因的分析」，鎖在 txt 裡都做不到。

```
lf_deep_dive_analyses                                -- ↔ RiskReportService.DeepDiveItem
  analysis_id    bigint PK
  record_id      bigint FK → lf_daily_records NOT NULL
  category       nvarchar(20) NOT NULL            -- 該次深析呼叫的類別
  seq            int NOT NULL                     -- 類別內排序（依嚴重程度）
  problem        nvarchar(1000) NOT NULL
  impact         nvarchar(2000)
  likely_causes_json text                         -- List<string>
  next_steps_json    text                         -- List<string>
```

### 每週體檢與權限異動

```
lf_weekly_checkups                                   -- ↔ WeeklyCheckupResult
  checkup_id     bigint PK
  host_id        bigint FK → lf_hosts NOT NULL
  checkup_date   date NOT NULL
  has_findings   bool NOT NULL
  conclusion     nvarchar(2000) NOT NULL
  report_id      bigint NULL FK → lf_reports
  UNIQUE (host_id, checkup_date)

lf_permission_changes                                -- ↔ PermissionChangeDetail ＋ Web 化的人工確認流程
  change_id      bigint PK
  host_id        bigint FK → lf_hosts NOT NULL
  detected_at    timestamp NOT NULL
  target         nvarchar(1000) NOT NULL          -- 資料夾路徑或群組名稱
  change_type    nvarchar(50) NOT NULL            -- 成員新增/成員移除/擁有者變更/權限新增/權限移除/無法存取
  before_state   text
  after_state    text
  confirm_status nvarchar(20) NOT NULL DEFAULT 'pending'  -- 'pending' | 'authorized' | 'suspicious'
  confirmed_by   bigint NULL FK → lf_users
  confirmed_at   timestamp NULL
  confirm_note   nvarchar(500) NULL
```

`confirm_status` 是把現有「被異動項目明細（人工防護層）」搬上 Web 的自然延伸：
現在使用者只能看報告檔逐項自問「這是授權操作嗎」，上 Web 後可以逐筆點掉（authorized）
或標記可疑（suspicious），**pending 清單本身就是待辦事項**，比 txt 報告可追蹤得多。

### 報告全文（人看的完整內容，與結構化資料並存的第二層）

```
lf_reports                                           -- ↔ export\ 下的 txt 報告
  report_id      bigint PK
  host_id        bigint FK → lf_hosts NOT NULL
  report_date    date NOT NULL
  kind           nvarchar(20) NOT NULL            -- 'daily_risk' | 'weekly_checkup' | 'permission'
  risk_level     nvarchar(10) NULL                -- daily_risk 才有
  categories     nvarchar(200) NULL               -- 儲存裝置+安全（檔名裡的類別串）
  file_name      nvarchar(255) NOT NULL           -- 原始檔名（顯示與追溯用）
  content        text NOT NULL                    -- 報告全文
  created_at     timestamp NOT NULL
```

風險報告在 DB 裡因此是**兩層**：
- **結構化層**（`lf_daily_records`＋`lf_top_issues`＋`lf_record_alerts`＋`lf_deep_dive_analyses`）：
  Web 篩選、統計、排序、餵 AI context 都用這層
- **全文層**（`lf_reports.content`）：使用者點開看完整報告時顯示，一字不差保留現有 txt 格式

### AI 問答（⏸ 未來選項——視資源決定，僅保留設計）

AI 問答已降為未來選項（2026-07-20 決策：先把報告顯示與查詢做好，問答視資源再議）。
下列兩張表**暫不建**；設計保留在此，屆時要做時 schema 不需重新討論。
其餘所有表的設計皆不依賴問答功能。

```
lf_qa_sessions
  session_id     bigint PK
  user_id        bigint FK → lf_users NOT NULL
  host_id        bigint FK → lf_hosts NOT NULL       -- 一個對話限定一台主機（授權與 context 都單純）
  title          nvarchar(200)                    -- 首個問題截斷生成
  started_at     timestamp NOT NULL

lf_qa_messages
  message_id     bigint PK
  session_id     bigint FK → lf_qa_sessions NOT NULL
  seq            int NOT NULL
  role           nvarchar(10) NOT NULL            -- 'user' | 'assistant'
  content        text NOT NULL
  context_dates  nvarchar(200) NULL               -- assistant 回合：本次 context 取用的日期範圍（稽核用）
  prompt_tokens  int NULL                         -- assistant 回合：實際 prompt 估算（容量觀測）
  created_at     timestamp NOT NULL
  UNIQUE (session_id, seq)
```

### 索引

```
lf_daily_records:  UNIQUE(host_id, record_date)；(record_date, risk_level) —「今天全機房哪些主機有風險」
lf_top_issues:     (record_id)；(event_id, source_name) — 跨主機找同一簽章（join lf_daily_records 取主機/日期）
lf_deep_dive_analyses: (record_id)
lf_record_alerts:  (record_id)
lf_reports:        (host_id, report_date)
lf_permission_changes: (confirm_status)、(host_id, detected_at) — pending 待辦清單
lf_weekly_checkups: UNIQUE(host_id, checkup_date)
lf_qa_messages:    UNIQUE(session_id, seq)
```

### 保留策略（2026-07-20 修訂：統一年限，取代原「長期保存不清理」）

- **`Storage.DbRetentionDays` 預設 730**（兩年）；未來若改三年只動設定（1095），不動程式。
- **全部資料表統一適用**——含 `lf_permission_changes`、`lf_record_handling(_log)`、`lf_reports`：
  「時間過了當時紀錄也不重要了」，到期直接刪、未來有需要再修改（2026-07-20 決策；
  曾提議的稽核類資料排除年限**已否決**）。
- **應用層滾動清理**：批次 exe 每晚執行時（與 txt Prune 同位置）刪除
  `record_date < 今天 − DbRetentionDays` 的資料，FK 子表先刪
  （handling_log → handling → deep_dives → top_issues → categories → alerts → daily_records → reports）。
  每晚僅刪一天份（2000 台約 3 萬列 lf_top_issues），**不需要分割表**，可移植性規則不受影響。

容量估算（2000 台、保留兩年的穩態）：

| 資料 | 年增量估算 | 兩年穩態 |
|---|---|---|
| lf_daily_records | 2000 台 × 365 天 ≈ 73 萬列/年 | ~146 萬列 |
| lf_top_issues（大宗） | × 平均 15 簽章 ≈ 1,100 萬列/年 | ~2,200 萬列 |
| lf_record_categories/alerts | 各數百萬列/年 | 各 <1,000 萬列 |
| lf_reports.content（文字大宗） | 風險日約 10% × 30KB ≈ 2GB/年 | ~4~5GB |

這個量級對 SQL Server / Oracle 仍屬輕鬆，靠既有索引即可。配套不變：

- **Schema 演進採「只增不改」**：新版本只加欄位（nullable 或有預設值）、不改不刪既有欄位，
  舊資料永遠可讀；配合 EF Core migration 記錄版本。
- **檔案端 120 天輪替（原 90 天，2026-07-24 配合首次回補 120 天調整）**：txt 定位為「臨時資料庫」，
  DB 上線時最多匯入近 120 天歷史——此限制已知悉並接受，保留年限自 DB 上線日起算。

## Web 查詢情境 → 資料表對應（驗證 schema 夠用）

進畫面的主篩選列（主機／日期區間／風險層級／風險類型）全部落在索引欄位上：

| 情境 | 查詢路徑 |
|---|---|
| **主篩選**：我的主機＋日期區間＋風險層級 | `lf_user_host_map` → `lf_daily_records` WHERE host_id IN (...) AND record_date BETWEEN ... AND risk_level IN (...) |
| **主篩選**：＋風險類型 | 上式 join `lf_record_categories` WHERE category IN (...)（可再加 max_severity 條件） |
| 我負責的主機現況總覽 | 每台 host 取 `lf_daily_records` 最新一筆（risk_level、summary、data_incomplete、uncovered 標記） |
| **主管儀表板**：本日/本週各風險類型的數量與緊急程度 | `lf_record_categories` join `lf_daily_records`（日期範圍）GROUP BY category → issue_count 加總、max_severity 分布、涉及主機數 |
| **主管儀表板**：高風險主機排行 | `lf_daily_records` WHERE 日期範圍 GROUP BY host_id，依風險日數/最高風險排序 |
| **主管儀表板**：未處理／逾期清單 | `lf_record_handling` WHERE status IN ('open','in_progress') [AND due_date < 今天] join `lf_daily_records`/`lf_hosts`（授權範圍內） |
| 風險日的處理歷程 | `lf_record_handling_log` WHERE record_id ORDER BY created_at（指派→查修→結案的完整敘事） |
| 單一主機風險時間軸 | `lf_daily_records` WHERE host_id + 日期範圍，點開某天載入 `lf_top_issues`/`lf_record_alerts`/`lf_deep_dive_analyses` |
| **看完整報告（畫面直接顯示）** | `lf_daily_records.report_id` → `lf_reports.content`（純文字含框線符號，前端以等寬字型/`<pre>` 呈現即可，不需轉換） |
| 權限異動待辦 | `lf_permission_changes` WHERE confirm_status='pending'（授權範圍內的主機） |
| 跨主機同類問題（管理員） | `lf_top_issues` WHERE event_id=153 join `lf_daily_records`/`lf_hosts`，依日期分布 |
| 週體檢發現 | `lf_weekly_checkups` WHERE has_findings=1 |

索引補充（配合主篩選與儀表板）：`lf_record_categories (category, record_id)`；
`lf_daily_records (record_date, risk_level)` 已列。

## AI 問答設計（⏸ 未來選項——設計保留，資源允許時再啟動）

**流程**：使用者選主機（僅授權清單）→ 後端組 context → 同一個 KoboldCpp endpoint →
回覆存 `lf_qa_messages`。Web 應用自己實作對 AI 的呼叫（複用 `AIService`＋`PromptBudget`，
見下方「專案結構調整」）。

**Context 組裝規則**（確定性程式組裝，AI 只回答——與批次端同一哲學）：

1. 主機角色（`lf_hosts.role_desc`）
2. 最新一筆 `lf_daily_records` 的完整結構化內容（summary、風險、告警、lf_top_issues 重點行、
   該日 `lf_deep_dive_analyses` 全部——這正是「處理方式」問題的答案素材）
3. 近 14 天每日一行統計（risk_level、錯誤/警告數、重點簽章）——與批次 prompt 的歷史區同格式
4. 最近一次週體檢結論
5. 對話歷史：保留最近 N 輪、每則截斷
6. **總預算 8KB context pack ＋ 對話歷史，經 `PromptBudget` 檢查**（`AIService.ChatAsync`
   的共用防線對 Web 呼叫同樣生效，零額外工作）

**System prompt 要點**：只根據提供的資料回答、不臆測；資料中的 log 內容視為**待分析的資料
而非指令**（事件訊息是攻擊者可控字串，這在 AI 問答情境是真實的 prompt injection 面——
批次端輸出只進報告檔所以風險低，互動問答必須明確防範）；無法從資料回答時明說，
不編造處理步驟；全程繁體中文。

**併發**：Web 問答與批次分析共用同一個單併發 AI 佇列。平日批次在清晨、上班時間佇列是空的，
不衝突；**週六全量體檢期間（1~3 小時）互動問答會排隊**——先接受此限制（週六上 Web 查詢的
機率低），若實際成為痛點再演進：佇列加優先權（互動插隊、批次讓行）或第二個模型實例。

**安全**：Web 用的 DB 帳號唯讀（`qa_*` 表除外）；授權過濾在查詢層，AI 拿到的 context
永遠只來自該使用者有權的主機；AI 沒有任何工具/行動能力，純問答。

## DB 就緒後的實作形狀

- **ORM 建議 EF Core**：`Microsoft.EntityFrameworkCore.SqlServer` 與 `Oracle.EntityFrameworkCore`
  都成熟，同一套 LINQ 程式碼靠 provider 切換——這是「不確定哪家 DB」成本最低的路線。
  接入點維持 `IAnalysisRecordStore`/`IReportSink`（EF 是實作細節，分析層看不到）。
- 新增 `SqlAnalysisRecordStore`、`DbReportSink`；`StorageFactory` 加 case；設定
  `"Storage": { "Type": "SqlServer" | "Oracle", "ConnectionString": "..." }`
- **過渡期 `CompositeReportSink`**：檔案＋DB 同時寫（單機部署不看 Web 的人仍有 txt 可看）
- **匯入器**：~~`--import-history` 讀 `history.txt`（結構化紀錄）＋ `export\*.txt`（報告全文，
  檔名還原日期/風險/類別）→ 入庫，舊資料不流失~~——**（2026-07-24 定案 10）確定不做**：
  SQL 後端上線時沒有服役中的 Jsonl 正式資料需要遷移，這支工具從未被建立也不再需要
- **Web 應用**：獨立 ASP.NET Core 專案，讀同一 DB；批次 exe 職責不變

**專案結構調整（實作時）**：抽 `LogForesight.Core` 類別庫（Models、Analysis、Persistence 介面、
`AIService`、`PromptBudget`），exe 與 Web 專案都引用——現在不動，DB 階段的第一步。

## txt ↔ DB 一致性保證（2026-07-20 新增——降低切換與雙軌維護成本的具體機制）

「txt 是臨時資料庫、之後換正式 DB」要順利，靠的不是宣示而是下列機制，
每一項都指名由誰保證：

| # | 機制 | 說明 |
|---|---|---|
| 1 | **單一模型契約** | JSONL 序列化的就是 `DailyAnalysisRecord` 等 C# 模型；DB 每張表是同一模型的欄位投影（機械對應：PascalCase → snake_case，僅保留字改名例外：`Count`→`event_count`、`Source`→`source_name`）。**模型改欄位＝兩個後端同時改**，不存在只改一邊的路徑 |
| 2 | **介面語意即規格** ✅ 已落實（2026-07-21） | 兩個後端都實作 `IAnalysisRecordStore`。`ReadRecent(anchorDate, days)`（**顯式錨定**日期區間 `[anchor-(days-1), anchor]`、升冪、錨定日之後不回傳，DB 對應 `WHERE date BETWEEN`）、`HasAnyRecord`（DB 對應 `EXISTS`）、`HasRecord`（同日冪等防護）、`Prune`、`AttachWeeklyCheckup`（更新既有列）的語意寫在介面註解，實作不得偏離。詳見 docs/HISTORY-STORE-FIX-PLAN.md |
| 3 | **合約測試（contract tests）** ✅ 已落實（2026-07-21） | `AnalysisRecordStoreContractTests` 抽象基底已建立（`JsonlAnalysisRecordStoreContractTests` 為其第一個實作）：同一組案例分別跑在 Jsonl 與未來的 DB 實作上，**DB 實作必須通過與 txt 完全相同的測試**才算完成——一致性由測試強制，不靠 code review 肉眼比對。JSONL 特有的壞行容錯與原子重寫案例留在 `JsonlAnalysisRecordStoreTests`，不進基底 |
| 4 | **精簡策略單點化**（pre-work #3） | 「無風險日砍範例訊息、留全部數字」目前是 `JsonlAnalysisRecordStore` 的私有方法——規則長在單一實作裡，DB 實作就得複製一份，遲早漂移。抽成共用的 `RecordStorageShaper`（純函數），兩個後端都呼叫同一份規則 |
| 5 | **同一份 JSON 序列化設定** | DB 的 `*_json` 欄位用與 JSONL 相同的 System.Text.Json 選項與同一批模型類別序列化；列舉存字串（`JsonStringEnumConverter`）、風險等級存中文字串，兩邊逐字一致 |
| 6 | **匯入後抽樣核對** | JSONL → DB 匯入器跑完後，自動抽 N 天以 `ReadRecent` 分別從兩後端讀回、逐欄位比對，一致才算匯入成功（驗收內建，不靠人工抽查） |
| 7 | **雙寫過渡期**（`CompositeReportSink`／雙 store） | 切換初期檔案與 DB 同時寫，任何不一致當天就會被發現（而不是檔案停用後才發現 DB 少了東西），穩定後再停檔案端 |

## 現在就能做的準備（DB 未定也不受影響）

> 原第 1 項「檔案保留 90 → 365 天」**已被否決**（2026-07-20 決策：txt 定位為臨時資料庫，
> 90 天即可，DB 上線時只匯入近 90 天的限制已接受），自清單移除。

| # | 事項 | 狀態 |
|---|---|---|
| 1 | **深析結構化結果存進 JSONL**：`DailyAnalysisRecord` 加 `DeepDives` 欄位（`CategoryDeepDive`/`DeepDiveFinding`），`RiskReportService.GenerateAsync` 每類別深析成功後同步寫入 `record.DeepDives`（渲染邏輯不變、報告全文照舊） | ✅ 已完成（2026-07-20）。低風險日恆為空清單（未觸發深析），已有測試覆蓋 |
| 2 | **紀錄加 `Host` 欄位**：`LogAnalysisService` 新增 `host` 建構參數，未指定時預設 `Environment.MachineName` | ✅ 已完成（2026-07-20） |
| 3 | **精簡策略抽成共用 `RecordStorageShaper`**：自 `JsonlAnalysisRecordStore` 私有方法抽出至 `Persistence/RecordStorageShaper.cs`（純函數，行為零改變），`Append` 改呼叫它 | ✅ 已完成（2026-07-20），獨立單元測試（`RecordStorageShaperTests`） |
| 4 | Schema 欄位級定案（本文件） | ✅ 已完成 |

驗證：建置零警告，112 個單元測試與 64 項 `--selftest` 全數通過。

審查後加固兩項（2026-07-20）：
- **列舉存字串一致性**：`CategoryDeepDive.Category` 補上 `JsonStringEnumConverter`，與
  `LogIssueSignature` 及一致性機制 #5 對齊（存 `"Storage"` 非整數），未來 DB 匯入直接對應字串。
- **精簡策略防漏欄位**：`RecordStorageShaper` 是逐欄位手動複製，「未來加欄位忘了複製 →
  低風險日靜默掉資料」是真實陷阱（本次加 `Host`/`DeepDives` 時就得手動補）。新增反射式測試
  把每個頂層欄位設非預設值後比對，漏複製即測試失敗（已實測拿掉一欄會 FAIL）。

**已知覆蓋缺口**：`RiskReportService.GenerateAsync` 內「深析結果寫入 `record.DeepDives`」
這段接線本身沒有自動化測試（`AIService` 目前是具體類別、未抽介面，缺 mock 基礎設施；
新增這層 mock 對一個 15 行直線邏輯不成比例）——已用 `RecordStorageShaper`／
`JsonlAnalysisRecordStore` 兩層測試涵蓋資料模型的序列化/精簡正確性，接線本身靠程式碼審閱
與建置驗證，未來要補測試時要先解決 AIService 缺乏介面的問題。
（本機 IP 的收集屬 DB 階段——它是 host 層級的一次性資訊，匯入時當場收集即可，
不需要跟著每日紀錄存。）

## Schema 升級機制（定案 13，2026-07-24）

`LfDbContext` 目前靠 `Database.EnsureCreated()` 建表——**只在資料庫不存在時**建立整套 schema，
對已存在的 DB **不會**補新表或新欄位。NetIQ Web 整併這一輪（`Sentinel`／`SentinelId`／
`CreatedAt` 等新增欄位）全部落在既有的 `lf_blobs` JSON 文件裡，零 DDL 異動，所以這次沒有
撞到這個限制；但這是**未來的地雷**——下一次需要新增真表或對既有真表加欄位時，
`EnsureCreated()` 對已上線的資料庫什麼都不會做，異動不會生效也不會報錯，靜默失敗最難查。

**方針（先寫下來，本輪不建機制）**：屆時採**自製冪等 DDL**（開機時檢查→缺什麼補什麼，
可重複執行不出錯），**不用 EF Core Migrations**——雙 provider（Sqlite／SqlServer）各自維護
一份 migration 歷史的長期成本，對這個專案的變更頻率不成比例；自製 DDL 檢查腳本反而更貼近
現有「`EnsureCreated` 全有全無」的簡單心智模型，只是把它從「只在全新庫做一次」延伸成
「每次啟動都補差異」。

## 使用場景盤點與待討論細節（2026-07-20 第二輪，續規劃）

### A. 問題處理狀態追蹤（✅ 已確認納入，2026-07-20——含預計完成日與處理歷程）

主管要「一眼看出有哪些風險」，下一句話幾乎必然是**「那這些風險有人在處理嗎？」**
已確認的需求細節：處理狀態、**預計完成日**、**處理說明**（可能查詢後決定不處理、或已更換
硬體等——說明要讓後續查看的人快速了解）、**處理人員**（可被指派，或自動帶入負責人）。

```
lf_record_handling                                   -- 風險日處理狀態（當前快照，儀表板查這張）
  record_id      bigint PK FK → lf_daily_records     -- 一筆風險日一個狀態
  status         nvarchar(20) NOT NULL DEFAULT 'open'
                 -- 'open'(未處理) | 'in_progress'(處理中) | 'resolved'(已處理)
                 -- | 'wont_fix'(評估後決定不處理——說明寫在 note)
                 -- | 'false_positive'(誤報) | 'known_noise'(已知雜訊)
  handler_id     bigint NULL FK → lf_users           -- 處理人員：可指派；未指派時可依 lf_user_host_map 自動帶入該主機負責人
  due_date       date NULL                        -- 預計完成日（儀表板「逾期未處理」的依據）
  note           nvarchar(1000) NULL              -- 處理說明：為何不處理/已更換硬體等
  updated_at     timestamp NOT NULL

lf_record_handling_log                               -- 處理歷程（append-only，保留完整敘事）
  log_id         bigint PK
  record_id      bigint FK → lf_daily_records NOT NULL
  status         nvarchar(20) NOT NULL
  handler_id     bigint NULL FK → lf_users
  note           nvarchar(1000) NULL
  created_at     timestamp NOT NULL
```

**為什麼快照＋歷程兩張表**：處理說明會隨事件演進（指派 → 查修中 → 換了硬體 → 結案），
單一 note 欄位每次更新就把前一段說明蓋掉，「後續查看快速了解」會只剩最後一句。
`lf_record_handling_log` 每次狀態/說明異動追加一列，完整敘事保留；`lf_record_handling`
是當前快照，讓儀表板的「未處理清單」「逾期清單」不用每次都撈歷程算最新狀態。

- 主管儀表板從「有哪些風險」升級成「有哪些風險**還沒人處理**」＋「哪些**已逾期**」
  （status IN ('open','in_progress') AND due_date < 今天）
- `known_noise` 標記有第二層價值：累積起來就是 `KnownIssueCatalog` 規則表調校的
  待辦清單，有資料依據而不是憑印象
- 粒度：以「風險日」為單位；更細的追蹤應接公司工單系統而非在此重造
- 索引：`lf_record_handling (status)`、`(due_date)`

### B. 主機識別與新舊資料綁定（✅ 定案簡化版，2026-07-20 第四輪——純人工綁定）

第三輪的「hw_uuid 三層證據＋程式建議」設計**已簡化移除**：環境中大量是 VM，
hw_uuid 在 VM 重建時會變，所謂強證據並不強，為它建收集與比對機制是過度設計。
定案為**純人工綁定**：

- `lf_hosts` 存 `host_name`（識別鍵）＋ `ip_address`（最近已知 IP，人在辨認新舊主機時
  最實用的線索——顯示在主機清單上讓人看，不做任何程式比對）
- **綁定操作**：Web 管理功能上，在新主機頁面**輸入（或從停用主機清單選取）舊主機的 ID**
  → 確認後執行合併：子表（lf_daily_records 等）的 host_id 重指到新主機，
  舊列標 `merged_into`＋`active=false` 留墓碑，歷史可追溯「這台曾經叫什麼」
- 綁定錯了可反向修復（墓碑還在，重指回去即可），但仍建議確認後再按

判斷成本留給人、機制只做「執行合併」這一件事——schema 面只需要 `merged_into` 一個欄位。

### C. Security 資料的長期保存政策（⏳ 分兩步，2026-07-20 決策）

決策：**先確認 Security 資料實際抓得到什麼**（本機看正式環境的執行權限、NetIQ 看 Phase 1
probe 的頻道覆蓋結果），**再決定長期保存政策**。schema 不需為此改動（`key_details` 本來就
nullable），屆時若要限制，方案備選：(a) `key_details` 單獨設保留年限（到期置 NULL，
統計數字不動）；(b) Web 查閱 Security 類資料時寫存取稽核。
→ 追蹤點：Phase 1 probe 結果出來後回到本節做第二步決定。

### D. 文字搜尋的範圍（✅ 已決：不做自由文字搜尋，2026-07-20）

搜尋以主篩選（主機/日期區間/風險層級/風險類型）＋ Event ID 查詢為範圍，現有索引全包。
自由文字搜尋不做（「不知道要以哪個欄位為主」——這正是它的本質問題，全文檢索兩家語法
又完全不同）。未來若出現明確的搜尋情境（知道要搜哪個欄位、為了什麼任務），再回來評估。

### E. 其他已考慮、暫不動作的項目

| 項目 | 判斷 |
|---|---|
| 機房總覽（Phase 3 的 fleet summary） | 屆時依「只增不改」新增 `lf_fleet_summaries(summary_date UNIQUE, content, ...)` 一張表即可；跨主機關聯訊號已由 `lf_record_alerts.kind='fleet'` 預留 |
| 主機頻道覆蓋清單（Phase 3） | 屆時在 `lf_hosts` 加 nullable 欄位（如 `channels_json`）即可，符合只增不改 |
| 通知管道（Phase 4）與 Web 整合 | 通知內容附 Web 報告連結（`report_id` 為穩定識別），屆時自然銜接，schema 已支援 |
| 匯出報表（月報 Excel 等） | 主管若需要，從結構化層產生；未來選項，不影響 schema |
| 儀表板「緊急程度」排序定義 | 風險層級 → 有無關聯訊號 → 類別最高嚴重度，全部可從現有欄位計算，不需新欄位 |
| Web 存取稽核（誰看過什麼） | 若公司政策要求再加 access_log 表，獨立於現有設計 |
| 時區 | `record_date` 為主機當地日期；全部主機同在台灣時區的前提下無議題（跨時區部署時再議） |
| 報告顯示格式 | `lf_reports.content` 純文字直接 `<pre>` 顯示（含框線符號）；未來要好看的 HTML 版，從結構化層渲染，不動全文層 |

## 決策狀態彙整（2026-07-20 第三輪後）

| # | 決策點 | 狀態 |
|---|---|---|
| 1 | 檔案保留天數 | ✅ 120 天（2026-07-24 由 90 天調整；txt=臨時資料庫，DB 上線僅匯入近 120 天，已接受） |
| 2 | 處理狀態追蹤 | ✅ 納入：狀態＋預計完成日＋處理說明＋處理人員（可指派/自動帶入負責人）＋歷程 log |
| 3 | 主機識別 | ✅ 存 IP（顯示用線索）；**純人工綁定**——輸入/選取舊主機 ID 即合併，`merged_into` 留墓碑；hw_uuid 與程式建議機制已因 VM 環境簡化移除 |
| 4 | Security 長期保存 | ⏳ 兩步走：先確認抓得到什麼（本機權限＋Phase 1 probe），再決定保存政策 |
| 5 | 自由文字搜尋 | ✅ 不做（欄位主體不明確；主篩選＋Event ID 已涵蓋） |
| 6 | Web 驗證/細節 | ⏸ 後議（lf_users 表按 AD 假設設計，屆時可改） |
| 7 | DB 保留年限 | ✅ 統一 `DbRetentionDays`=730（未來三年改 1095）；全表適用含權限異動/處理歷程，到期直接刪；應用層每晚滾動清理（2026-07-20） |
| 8 | 多 Sentinel 主機歸屬 | ✅ `lf_hosts.netiq_server` 記錄所屬 Sentinel（路由/顯示屬性）；IP 全域唯一維持識別鍵（2026-07-20） |
| 9 | Jsonl 檔案後端 | ✅ **已退役**（2026-07-24，定案 10）：`Storage.Type` 收斂為 Sqlite／SqlServer 二選一，設成 `Jsonl` 啟動即報錯 |
| 10 | `--import-history` 匯入器 | ✅ **確定不做**（2026-07-24，定案 10）：沒有服役中的 Jsonl 正式資料需要遷移 |
| 11 | Schema 升級機制 | ✅ 本輪零 DDL，暫不建機制；方針已定案（定案 13，見上節）——未來採自製冪等 DDL，不用 EF Migrations |

**唯一留待後續的開放項**：#4 的第二步（probe 後回到本節 C）。schema 本身已無開放問題。
