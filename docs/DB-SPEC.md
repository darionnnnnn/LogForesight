# 資料庫欄位級規格（DB-SPEC）

> 除非必要否則不要讀取 docs/archive/ 內容，避免浪費 token。
>
> 本文件是資料庫 schema 的現況欄位級定案：資料表設計、索引、保留策略、Web 查詢情境對應、
> Schema 升級機制。**全部資料走 SQL**（`Storage.Type` 為 `Sqlite`／`SqlServer` 二選一，
> 無檔案後端）；實際落地的 provider 架構（EF Core、`lf_blobs`/`lf_log_lines` 抽象）見
> WEB-SPEC.md §10.5。DB 選型與雙 DB 可移植規則不影響本文件任何已定案欄位。
> 起草緣起、JSONL→DB 切換期的過渡機制與已完成的前置準備事項，見
> docs/archive/HISTORY.md「資料庫與 Web 查詢／AI 問答規劃」段。

## 需求

1. **維護人員**：進入畫面即可選「自己負責的主機」＋日期區間＋風險層級＋風險類型做搜尋，
   查找自己主機的問題；**風險報告直接在畫面顯示**（現有 txt 全文照出）
2. **主管**：一眼看出目前有哪些風險類型、數量、緊急程度
3. **AI 問答為未來選項**：視屆時資源決定是否做；schema 保留設計但不圍繞它做任何取捨
4. 保留策略見下方「保留策略」一節（現況已分四種期間，非單一統一年限）
5. 全部設計維持雙 DB 可移植（SQL Server／Oracle）
6. Web 是獨立的查詢應用，讀同一個 DB

## 雙 DB 可移植規則（所有表遵守）

| 規則 | 原因 |
|---|---|
| **資料表一律 `lf_` 前綴**、索引 `ix_lf_` 前綴；識別字全小寫 snake_case、**長度 ≤ 30 字元**（含前綴，最長 `lf_record_handling_log` = 22 ✓）、避開兩家保留字 | 前綴避免與公司共用 DB 中其他系統的表衝突、一眼可辨識歸屬；Oracle 12.2 之前識別字上限 30 bytes。大小寫說明：未加引號時 SQL Server 預設不分大小寫、Oracle 一律轉大寫（實體名即 `LF_...`），文件以小寫書寫、DDL 不加引號，兩家行為一致 |
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
`WeeklyCheckupResult`、`PermissionChangeRecord`、深析 `DeepDiveItem`），JSONL→DB 匯入器因此是
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
  last_login_at  timestamp NULL                   -- 最近一次登入成功；null = 從未登入
                                                  -- （詳見 docs/archive/FEEDBACK-11-PLAN.md §3；
                                                  --  JSON 後端缺欄容忍、零遷移。唯一寫入點
                                                  --  IUserStore.TouchLogin，刻意不走 Upsert）

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
  ai_pending       bool NOT NULL DEFAULT 0        -- NetIQ 搜尋與 AI 判讀
                                                  -- 脫鉤的第三態（統計已寫入、AI 段排隊中），
                                                  -- 與 ai_analyzed=false 是不同語意
  security_log_available bool NULL                -- 三態：NULL=未嘗試
  data_incomplete  bool NOT NULL
  headline         nvarchar(200)                  -- AI 白話標題（↔ DailyAnalysisRecord.Headline）
  summary          nvarchar(2000)                 -- AI 白話敘述（↔ Summary，序列化欄位名不變）
  trend_assessment nvarchar(2000)
  action           nvarchar(500)                  -- AI 白話行動建議（↔ Action，取代原 recommendations_json 多項清單）
  screened_tail_count  int NOT NULL
  screening_notes_json text                       -- List<string>
  uncovered_checks_json text                      -- List<string>（未檢查項目申報）
  report_id        bigint NULL FK → lf_reports       -- 有風險報告時指向全文
  created_at       timestamp NOT NULL
  extract_version  int NOT NULL DEFAULT 0            -- 抽出欄（headline/
                                                     -- data_incomplete/security_log_available/
                                                     -- error_count/warning_count/ai_analyzed/
                                                     -- ai_pending）的回填版本號，0=舊列尚未回填、
                                                     -- 由 DailyRecordBackfiller 背景補齊（同
                                                     -- lf_top_issues 既有的回填機制）
  UNIQUE (host_id, record_date)
```

```
lf_issue_first_seen                                  -- 問題的機房首見日
                                                     -- （不受查詢期間截斷，供呈現用；
                                                     -- 與 lf_top_issues.record_date 的
                                                     -- MIN 不同——那受查詢期間截斷，這張表
                                                     -- insert-if-absent、之後不論查哪個期間
                                                     -- 都不變）
  source_key     nvarchar(255) NOT NULL           -- Source 正規化大寫（collation-safety，
                                                   -- 同 host_name_key 慣例）
  event_id       int NOT NULL
  source_name    nvarchar(255) NOT NULL           -- 顯示用原始大小寫
  first_seen     date NOT NULL
  PK (source_key, event_id)
```

機房首見日的維護合併（`SchemaUpgrader.MergeIssueFirstSeenSeed`）是**增量**的：以
`lf_blobs` 的 `issue_first_seen_watermark`（`lf_top_issues` 的 `MAX(record_id)`）為界，
只掃新列補新組合、並在新列日期更早時修正既有組合（回補會寫入日期更早的新列）；
全表掃描的修正段只在初次回補跑一次，之後由 `issue_first_seen_full_done` 旗標擋掉。

`lf_issue_first_seen` 寫入時 insert-if-absent、已存在時只在新日期較早才更新（並行寫入
撞唯一鍵時視為正常情況，改走條件式 UPDATE 補寫，不是需要中止分析的錯誤——見
`EfAnalysisRecordStore.UpsertFirstSeen`）。鍵刻意用 `(source_key, event_id)`，不含
`log_name`/`entry_type`：首見的語意是「這個問題」（依問題視角的分組鍵），不是某個完整
簽章第一次出現。

歷史資料的補齊由背景服務 `IssueFirstSeenSeedHostedService` 呼叫
`SchemaUpgrader.MergeIssueFirstSeenSeed`（自 `lf_top_issues` 合併）。**閘門是浮水印**：
以 `lf_top_issues` 目前的最大 `record_id` 存進 blob（鍵 `issue_first_seen_watermark`），
啟動時比對相同就整段跳過——那兩段 SQL 是 `GROUP BY UPPER(source_name), event_id` 的全表掃，
千萬列級環境下不能每次重啟都跑一遍（回饋二十輪 C）。合併吃分析等級的 300 秒逾時
（不是前景的 60 秒），失敗每 30 分重試最多 3 次後停止，狀態經
`/api/health/detail` 申報並在三次失敗時反映為降級——補不完的症狀是老問題被
`PriorityScore` 當成 7 天內的新問題，畫面上完全看不出來，所以必須申報。

**`ContentJson`（`DailyRecordRow.ContentJson`）新增序列化欄位（詳見
docs/archive/FEEDBACK-12-PLAN.md §3.5/§4.2，無 schema 變更，兩者都只是完整 `DailyAnalysisRecord`
JSON 裡多出的欄位，不是新增資料表欄位）**：
- `AiPending`（bool）：NetIQ 搜尋與 AI 判讀脫鉤後的第三態——統計已寫入、AI 段還在排隊或
  執行中。與既有的 `ai_analyzed=false`（AI 判定不需要或已失敗）是不同語意，見
  docs/DETECTION-SPEC.md「NetIQ 搜尋與 AI 判讀脫鉤」一節。
- `LogIssueSignature.EventKey`：問題簽章的第五個分組鍵欄位（Linux 事件命中規則時的規則 Id，
  Windows 事件恆空字串）。**已抽出成 `lf_top_issues.event_key` 欄**
  （處理狀態以完整簽章為鍵，`IssueSignatureKey` 需要它才組得回五段完整簽章、
  join 得到「這個問題有沒有結論」）。但**「依問題視角」的
  跨主機/跨日聚合鍵仍是 `(Source, EventId)`、不含 EventKey**——同 program 命中不同規則的
  Linux 問題仍會併成一組，這是有意識接受的 v1 限制，真的有 Linux 流量進來後再評估
  （見 docs/LINUX-RULES.md）。

**`AttachAiResult`（`IAnalysisRecordStore`）**：AI 段完成後覆寫暫代的 `ContentJson`
內容，比照既有 `AttachWeeklyCheckup` 的模式——**同時更新抽出欄 `risk_level`**（AI 有可能把
程式判定的風險等級往上拉），只改 `ContentJson` 而漏改抽出欄會讓依 `risk_level` 篩選的查詢
（清單頁、儀表板）看不到這筆紀錄升級後的真正風險，是本專案反覆出現過的「抽出欄漂移」
bug 家族的同一種模式，寫入路徑因此固定放在同一處做兩件事。

```
lf_top_issues                                        -- ↔ LogIssueSignature（欄位一比一，趨勢數字全保留）
                                                     -- 同時是**問題聚合的事實表**
                                                     -- （docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4）：
                                                     -- 「這個問題影響幾台、跨哪段期間、出現幾天」
                                                     -- 由本表 GROUP BY 直接回答，不再把整段期間的
                                                     -- 紀錄撈回記憶體聚合。
  issue_id         bigint PK
  record_id        bigint FK → lf_daily_records NOT NULL
  host_id          bigint NOT NULL DEFAULT 0         -- 去正規化自父列，且映射為**存活主機**：
                                                     -- 直接用紀錄的 host_id 會讓合併過的機器
                                                     -- （墓碑列＋存活列）被算成兩台
  record_date      date NOT NULL                     -- 去正規化自父列（期間跨度／出現密度）
  elevates_day_risk bit NOT NULL DEFAULT 0           -- 「重大」旗標（排行清單的嚴重度維度）
  log_name         nvarchar(50) NOT NULL
  source_name      nvarchar(255) NOT NULL         -- 'source' 是 Oracle 慣用字，改名避開
  event_id         int NOT NULL
  entry_type       nvarchar(20) NOT NULL
  event_count      int NOT NULL                   -- 'count' 是保留字，改名
  category         nvarchar(20) NOT NULL          -- Storage/Hardware/Security/...
  severity         nvarchar(10) NOT NULL          -- Low/Medium/High/Critical
  known_issue      nvarchar(500) NULL             -- 命中規則表時的中文說明（
                                                  --   依問題視角的說明欄直接查此欄不解 JSON）
  event_key        nvarchar(255) NOT NULL DEFAULT ''  -- 完整簽章第五段（
                                                  --   Linux 規則 Id，Windows 恆空字串；聚合鍵仍是
                                                  --   (source_name, event_id)，見 ContentJson 補充節）
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
  critical_count int NOT NULL DEFAULT 0           -- ↓ Web 報表需求新增：各嚴重度簽章數分解
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

> 增補：Web 報表的「類別×嚴重度」堆疊圖需要嚴重度分解，增列四個
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

lf_permission_changes                                -- ↔ PermissionChangeRecord ＋ 人工確認狀態（同一列）
  id                   bigint PK IDENTITY
  change_id            nvarchar(64) NOT NULL            -- GUID("N")，對外識別用；唯一性由下方索引保證（DDL 無 UNIQUE 子句）
  dedupe_key           nvarchar(max) NOT NULL           -- 主機|事件時間Ticks|EventId|告警文字
  host_name            nvarchar(255) NOT NULL           -- 非正規化字串，不是 FK
  host_name_key        nvarchar(255) NOT NULL           -- 不分大小寫比對用的正規化鍵
  detected_at          datetime2 NOT NULL               -- 事件發生時間（排序與時間篩選用）
  created_at           datetime2 NOT NULL               -- 寫進資料庫的時間（保留期清理用）
  target               nvarchar(max) NOT NULL           -- 資料夾路徑或群組名稱（不得設長度上限：Windows 長路徑輕易超過 512，SQLite 測不出、SQL Server 會截斷）
  change_type          nvarchar(64) NOT NULL            -- 現行產生 9 個相異值（既有資料另可能有舊值「權限異動（彙總）」），對應類別見 DETECTION-SPEC「權限異動類別」
  category             nvarchar(64) NOT NULL            -- 類別 key，由 change_type/event_id 純函式推導
  is_privileged_target bit NOT NULL                     -- 加入特權群組＝高風險
  initiator_account    nvarchar(255) NULL               -- 操作者（NetIQ sun 或訊息 Subject 區段）
  target_account       nvarchar(255) NULL               -- 被異動的成員／目標帳號
  object_type          nvarchar(64) NULL                -- 4670 的物件類型（Token／File／Key…），決定類別分流與句型
  process_name         nvarchar(max) NULL               -- 4670 的處理程序名稱（完整路徑，不設長度上限同 target）
  covered_from         datetime2 NULL                   -- 彙總列涵蓋的首筆事件時間（逐則列與既有列為 NULL）
  covered_to           datetime2 NULL                   -- 彙總列涵蓋的末筆事件時間
  pair_count           int NULL                         -- 彙總列合併掉的成對數（AlertText 裡的數字是給人看的句子，不可拿來排序）
  before_value         nvarchar(max) NOT NULL
  after_value          nvarchar(max) NOT NULL
  alert_text           nvarchar(max) NOT NULL           -- 原始訊息前 500 字（清單與去重鍵用）
  raw_text             nvarchar(max) NULL               -- 未截斷的原始事件訊息（展開明細用；升級前寫入的列與彙總列為 NULL，不回填）
  source               nvarchar(64) NOT NULL            -- '本機監控' | 'NetIQ 事件'
  event_id             int NULL
  status               nvarchar(30) NOT NULL             -- 'pending' | 'authorized' | 'suspicious'（預設值由應用層填，DDL 無 DEFAULT 子句）
  confirmed_by         bigint NULL
  confirmed_by_account nvarchar(255) NULL
  confirmed_at         datetime2 NULL
  confirm_note         nvarchar(max) NULL

索引：change_id 唯一；(status, detected_at)；detected_at；(host_name_key, detected_at)；
      (category, status)；created_at
```

**確認狀態與異動同列，不另建表。** 三個理由：狀態要能在 SQL 端篩選；批次核准要能做成
單一 `UPDATE … WHERE change_id IN (…) AND status='pending'` 的原子操作；確認若存在獨立的
整份讀改寫容器，兩人同時確認會後寫覆蓋先寫，使用者收不到任何提示。

**`detected_at` 與 `created_at` 不可互相取代。** 排序與時間篩選用 `detected_at`；保留期清理
用 `created_at`。反例：NetIQ 重跑一個 100 天前的主機日，寫出的列 `detected_at` 是 100 天前、
`created_at` 是現在——依 `detected_at` 清理的話這批剛補出來的待辦會立刻消失。**不要改回去。**

**`dedupe_key` 不設長度上限也不建索引。** 它由「主機名(≤255)｜Ticks(19)｜EventId｜
AlertText(≤503)」串成，最長約 790 字元：設成 `nvarchar(512)` 在 SQLite（TEXT 無長度）測不
出來，到 SQL Server 會變成寫入時「字串或二進位資料會被截斷」；而 SQL Server 非叢集索引鍵
上限 1700 bytes（850 個 nvarchar 字元），790 字元的鍵本來就不適合當索引鍵。沒有任何查詢以
它為條件（`GetDedupeKeys` 是依 `created_at` 篩選後投影這一欄），索引不存在也不影響效能。

**升級路徑**：舊部署的資料在 log key `perm_changes`（JSONL）與 blob key `perm_confirms`，
由 `PermissionChangeMigrator` 背景搬移。它有**自己的**遷移狀態（blob key
`permission_change_migration`），刻意不併進 `HandlingBlobMigrator`——既有部署的處理狀態遷移
早已是 `Completed`，而 `Evaluate()` 對 `Completed` 直接短路，併進去就永遠不會執行。
遷移期間 `MigrationGateMiddleware` 擋下 `/api/permission-changes` 的非 GET 請求（GET 放行）。
重入保護是**逐筆比對 `change_id`**，不是「表裡有資料就整批跳過」——遷移閘門只擋得住 HTTP 寫入，
而背景排程的分析流程也會寫這張表，夜間分析先寫進一列就會讓整批舊資料被誤判成已搬而永久消失。
`HandlingBlobMigrator` 的三張表情況相同（`AnalysisOrchestrator` 讓夜間分析直接寫），同樣採
逐筆比對自然鍵。舊 log 與舊 blob **不刪**，保留為備份。

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

AI 問答已降為未來選項（決策：先把報告顯示與查詢做好，問答視資源再議）。
下列兩張表**暫不建**；設計保留在此，屆時要做時 schema 不需重新討論。
其餘所有表的設計皆不依賴問答功能。

> **註**：風險日詳情頁已上線一個**精簡版**對話（`POST api/ai/chat`，見 WEB-SPEC §9.3）——
> 單日單一問題為範圍、10 輪上限、**不持久化**（前端持有 transcript，每輪送全量），因此不需要這兩張表。
> 本節設計對應的是跨日、存 DB、開 session 的完整問答，兩者是不同功能；完整版重啟時本設計依然適用。
> 精簡版已依本節「範例訊息以資料圍欄框住、system prompt 重申非指令」的 injection 預警實作。

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
lf_daily_records:  UNIQUE(host_id, record_date)；(record_date, risk_level) —「今天全機房哪些主機有風險」；(extract_version) — 回填掃描
lf_issue_first_seen: PK(source_key, event_id)
lf_top_issues:     (record_id)；(event_id, source_name) — 跨主機找同一簽章
                   (record_date, source_name, event_id)；(host_id, record_date) — 問題聚合
lf_issue_handling: UNIQUE(host_name_key, record_date, issue_key)；(host_name_key, record_date)；(case_id)
                   -- 另有 created_at 欄：僅新增列時落、更新不覆寫，舊列為 NULL；
                   -- 目前無消費端，是 MTTA 成效指標（docs/BACKLOG.md）的資料基礎

lf_issue_cases:    (host_name_key, issue_key, closed_at)；(handler_id, closed_at)
lf_record_handling: UNIQUE(host_name_key, record_date)；(handler_id)；(status)
lf_deep_dive_analyses: (record_id)
lf_record_alerts:  (record_id)
lf_reports:        (host_id, report_date)
lf_permission_changes: 見上方定義區塊（不在此重列，避免兩份索引清單各自演化）
lf_weekly_checkups: UNIQUE(host_id, checkup_date)
lf_qa_messages:    UNIQUE(session_id, seq)
```

### 保留策略

保留期**不是單一年限**，而是依資料性質分成四個（另有 `InitialHistoryDays` 決定首次回補幾天，
與保留期同受 90 天下限約束，但它不刪資料）：

| 設定（`SystemSettings`） | 預設 | 適用對象 |
|---|---|---|
| `RetentionDays` | 180 | 分析紀錄與其附屬狀態：`lf_daily_records`／`lf_top_issues`／`lf_issue_handling`／`lf_record_handling`／`lf_issue_cases`（僅已結案）／export 報告檔 |
| `RawEventRetentionDays` | 120 | 原始事件內容：`lf_daily_records.content_json`（風險日詳情的原始樣本訊息，**只清內容、整列保留**）與 `lf_risky_events`（風險 log 暫存）——兩者刪的都是原始事件文字，舊版的 `DetailRetentionDays`／`RiskyEventRetentionDays` 兩鍵已合併，升級時自動取兩舊值較小者遷移 |
| `AuditRetentionDays` | 730 | 稽核類：`audit`、**`handling_log`（處理歷程）**、`lf_permission_changes`（依 `created_at`，含 `raw_text` 原始訊息全文，見其定義區塊） |
| `RunLogRetentionDays` | 120 | 執行歷程：`batch_runs`／`batch_run_logs`／`import_logs` |

五個天數的**下限一律 90 天**（`SystemSettings.MinRetentionDays`），只在寫入時驗證；
讀取端不 clamp，既有部署存過的較短天數照舊生效。

**清理一律涵蓋全部主機**：夜間作業用未限縮的 `RecordStore()`，不是綁定本機識別的那個實例。
NetIQ 機房主機的紀錄不屬於本機，用限縮實例等於保留期只對跑分析的那台機器生效
（實測：500 台 × 200 天的資料集，限縮可清 0 筆、未限縮 39,500 筆）。
`RecordStore(ownerHost)` 的限縮語意本身是對的（缺日判定與趨勢基準只該看本機），
錯的是拿它來清理——回歸防線見 `LogForesight.Tests/Scale/RetentionScopeBenchmarks.cs`。

**升級告知**：修好之後的第一個副作用是「這些設定值第一次真正生效」——既有部署升級後第一晚
就會開始真的刪 NetIQ 主機的過期資料。若當初因為「反正沒作用」把天數設得很短，那是一次
無法復原的刪除。README 的「升級注意事項」與說明書「資料保留」段都有對應警語（回饋二十輪 F）。
積壓超過單次上限（`EfAnalysisRecordStore.MaxPruneRowsPerRun` = 50,000）時，執行畫面會申報
剩餘筆數**與預估還需幾次執行**——只報筆數的話，「陸續清完」與「卡住了」在畫面上分不出來。

**為什麼詳情要獨立一層**（SCALE-3000 S2）：`content_json` 是整張表的儲存量大宗
（實測平均 5.3 KB/筆），而年度同期比較需要的 KPI 與趨勢**沒有一項讀它**——
那些全部來自抽出欄與 `lf_top_issues`。3000 台若把分析紀錄留兩年（`RetentionDays` 760），
連 `content_json` 一起留約 12 GB；原始事件內容只留 120 天則約 1.9 GB。
清除方式是把 `content_json` 設為空字串並標記 `detail_pruned`，**不刪列**，
統計、風險等級、問題清單一律不受影響。驗證 `RawEventRetentionDays <= RetentionDays`——
原始內容活得比它所屬的紀錄久沒有意義。

**處理歷程跟稽核而不是跟執行歷程**（docs/archive/SCALE-FIX-PLAN-2026-08-06.md G4）：
它記的是「誰在什麼時候把這個問題標成什麼、為什麼」——那是**追責用的證據**，
不是「這次跑了什麼」的執行紀錄。用 90 天的話，證據會比被追究的事件更早消失。

**已結案的案件依「結案時間」而不是事件日期清理**（同上 S-4）：
進行中案件**不論多舊都留著**，因為它代表「還沒處理完」。
一個掛了兩年沒人動的案件正是最該被看見的那種，依事件日期清掉等於幫忙把爛帳藏起來。

**應用層滾動清理**，掛在夜間分析的清理段（`AnalysisOrchestrator`）。
順序上**處理狀態排在分析紀錄之後**——後者決定哪些日期已過期，
順序反過來會漏掉剛被判定過期的那幾天，要等下次執行才補上。
作法一律是**只撈主鍵 → 分批 `ExecuteDelete` → 單次上限 20 萬列、超過留待下次**
（`BatchedPrune`）：整批載入實體再 `RemoveRange`，在 6000 台環境等於先把數 GB
讀進記憶體只為了刪掉它們，而且是一筆會鎖住整張表的超長交易。
**不需要分割表**，可移植性規則不受影響。

容量估算（2000 台；括號內為 6000 台）：

| 資料 | 年增量估算 | 保留期內穩態 |
|---|---|---|
| lf_daily_records | 2000 台 × 365 天 ≈ 73 萬列/年 | 120 天約 24 萬列（6000 台：72 萬） |
| lf_top_issues（大宗） | × 平均 15 簽章 ≈ 1,100 萬列/年 | 120 天約 360 萬列（6000 台：1,080 萬） |
| lf_issue_handling | 已標記的問題日，推估為 top_issues 的 10~30% | 120 天約 36~110 萬列（6000 台：110~320 萬） |
| lf_record_handling | 每台每個處理過的日 ≤ 1 列 | 與 lf_daily_records 同量級以下 |
| lf_issue_cases | 每台每個問題一案、跨日不重複 | 數萬列量級，遠小於上列 |
| handling_log（保留 730 天） | 每次標記／指派一列，批次標記亦逐筆記錄 | **唯一以稽核年限成長的一張**，6000 台屬千萬列級 |
| lf_record_categories/alerts | 各數百萬列/年 | 各 <1,000 萬列 |
| lf_reports.content（文字大宗） | 風險日約 10% × 30KB ≈ 2GB/年 | ~4~5GB |

這個量級對 SQL Server / Oracle 仍屬輕鬆，靠既有索引即可。配套不變：

- **Schema 演進採「只增不改」**：新版本只加欄位（nullable 或有預設值）、不改不刪既有欄位，
  舊資料永遠可讀；配合 EF Core migration 記錄版本。
- **檔案端 120 天輪替**（與首次回補天數一致）：txt 定位為「臨時資料庫」，
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
| 權限異動檢核 | `lf_permission_changes` WHERE status='pending'（授權範圍內的主機），可再依類別／關鍵字／網段／時間篩選 |
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

## Schema 升級機制（已落實）

`LfDbContext` 靠 `Database.EnsureCreated()` 建表——**只在資料庫不存在時**建立整套 schema，
對已存在的 DB **不會**補新表或新欄位。NetIQ Web 整併那一輪（`Sentinel`／`SentinelId`／
`CreatedAt` 等新增欄位）全部落在既有的 `lf_blobs` JSON 文件裡，零 DDL 異動，當時沒有
撞到這個限制；但下一次需要新增真表或對既有真表加欄位時，`EnsureCreated()` 對已上線的
資料庫什麼都不會做，異動不會生效也不會報錯，靜默失敗最難查——這正是 P0-3（`lf_log_lines`
清理需要的 `created_at` 欄）撞上的情況，因此本輪落實機制。

**方針（已落實）**：採**自製冪等 DDL**（開機時檢查→缺什麼補什麼，可重複執行不出錯），
**不用 EF Core Migrations**——雙 provider（Sqlite／SqlServer）各自維護一份 migration 歷史的
長期成本，對這個專案的變更頻率不成比例；自製 DDL 檢查腳本反而更貼近現有「`EnsureCreated`
全有全無」的簡單心智模型，只是把它從「只在全新庫做一次」延伸成「每次啟動都補差異」。

實作：`LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs`，於 `StorageBackend` 建構時
的 `EnsureCreated()` 之後呼叫（Web 啟動建立 singleton backend 時就會跑到）。
每一步是「檢查缺什麼（SQLite 查 `pragma_table_info`/`pragma_index_list`，SqlServer 查
`INFORMATION_SCHEMA.COLUMNS`/`sys.indexes`）→ 缺才補（`ALTER TABLE ADD`／`CREATE INDEX`）」，
新建的 DB 因 `EnsureCreated` 已建好最新 schema，每一步在新 DB 上都是 no-op。
首個落地案例：`lf_log_lines` 補 `created_at` 欄＋`(log_key, created_at)` 索引
（docs/archive/HISTORY.md P0-3 的前置需求）。未建 `lf_schema_version` 版本表——
冪等檢查本身就是狀態，步驟數量還不到需要額外版本追蹤的規模。

## 補充設計說明

### A. 問題處理狀態追蹤

處理狀態、**預計完成日**、**處理說明**（可能查詢後決定不處理、或已更換
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

### B. 主機識別與新舊資料綁定

環境中大量是 VM，`hw_uuid` 在 VM 重建時會變，不是可靠的比對依據，因此不建自動比對機制，
採**純人工綁定**：

- `lf_hosts` 存 `host_name`（識別鍵）＋ `ip_address`（最近已知 IP，人在辨認新舊主機時
  最實用的線索——顯示在主機清單上讓人看，不做任何程式比對）
- **綁定操作**：Web 管理功能上，在新主機頁面**輸入（或從停用主機清單選取）舊主機的 ID**
  → 確認後執行合併：子表（lf_daily_records 等）的 host_id 重指到新主機，
  舊列標 `merged_into`＋`active=false` 留墓碑，歷史可追溯「這台曾經叫什麼」
- 綁定錯了可反向修復（墓碑還在，重指回去即可），但仍建議確認後再按

判斷成本留給人、機制只做「執行合併」這一件事——schema 面只需要 `merged_into` 一個欄位。

### C. Security 資料的長期保存政策（未定案）

長期保存政策尚未決定，待核對 Security 頻道實際覆蓋現況後再議（見 [docs/BACKLOG.md](BACKLOG.md)）。
schema 不需為此預先改動（`key_details` 本來就 nullable）；屆時若要限制，方案備選：
(a) `key_details` 單獨設保留年限（到期置 NULL，統計數字不動）；
(b) Web 查閱 Security 類資料時寫存取稽核。

### D. 文字搜尋的範圍

不做自由文字搜尋——搜尋以主篩選（主機/日期區間/風險層級/風險類型）＋ Event ID 查詢為範圍，
現有索引全包；全文檢索在雙 DB 下語法不同、且缺乏明確的欄位範圍需求。
未來若出現明確的搜尋情境（知道要搜哪個欄位、為了什麼任務），再回來評估。

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

**唯一留待後續的開放項**：C 節 Security 長期保存政策的第二步（試點階段核對抓得到什麼後回頭決定，
見 docs/BACKLOG.md）。schema 本身已無開放問題。

### F. `lf_blobs.version`：整份型 store 的快取失效權杖

`lf_blobs` 每一列存一整份 JSON（key＝store 名稱）。`hosts`／`host_groups`／`group_access`
三份隨主機數成長，3000 台的 `hosts` 約 4 MB，而 `IHostStore.GetAll()` 在單一 HTTP 請求內
會被呼叫十餘次（`HostLookup`／`HostAliasIndex`／可見範圍解析各自都要）。
`JsonBlobCollection` 因此對這三份、且只有這三份啟用讀取快取。

`version` 是 `bigint`，`EfJsonBlobStore.Mutate` 每次寫入遞增 1，唯一用途是讓快取判斷
「內容有沒有變過」——`ReadVersion()` 只讀這一個整數欄，不拉整份內容。

| 欄位 | 用途 | 誰維護 |
|---|---|---|
| `updated_at` | EF 並發權杖（樂觀鎖，防更新遺失） | EF 於 `SaveChanges` 比對原始值 |
| `version` | 快取失效判定 | `Mutate` 遞增 |

**兩者不可合併成一個**：`updated_at` 取自 `DateTime.Now`，Windows 上實際解析度約 15.6 ms，
同一個 tick 內的兩次寫入會拿到相同戳記。主機清單是**授權可見範圍的來源**，
漏一次更新等於使用者可能看到不該看到的主機。

其餘 store（`users`、`sentinels`、`rules`…）不啟用快取：它們不隨主機數成長，
加快取只是徒增失效正確性的風險面。

**`IHostStore.DataVersion`**（回饋二十七輪）：把 `version` 對外曝光成主機清單的資料版本，
供**上層**判定「用這份清單建出來的東西要不要重建」。目前唯一消費端是
`EfIssueAggregateQuery` 的主機別名索引快照——清單本身早有快取，但**由它建出來的索引**沒有，
而報表一次請求會呼叫八個以上的聚合方法，等於把 3000 台的三張字典重建八次。
索引快取沿用同一套判準（每次探測版本、不設 TTL）：別名索引決定紀錄歸屬哪一台主機，
過期索引會讓查詢結果落到錯誤的主機上。**快照存的版本必須是「讀內容之前」探測到的那個**——
存成「建完後再讀一次的新版本」的話，兩次讀之間若有人改了主機，就會把新版本號配上舊內容，
過期索引一路命中到下次寫入為止；反過來版本偏舊只會多重建一次，是安全的失敗方向。

**`ai_usage`**（回饋二十七輪）：AI token 用量統計，單一物件型 blob（`AiUsageStore`）。
內容＝累計起算日＋不受裁切的累計值（呼叫次數／prompt／completion／total／未回報 usage 次數）
＋每日列（保留 90 天，超過裁掉）。**累計與每日是兩套數字**：每日只供趨勢檢視，
使用者問的「目前累計用了多少」要的是不隨裁切變小的那一個。不另開資料表的理由是它跟著 DB
備份與搬遷走，且量小、寫入頻率等同 AI 呼叫頻率（已被請求佇列序列化）。不啟用讀取快取。

兩條不可放寬的約束：

- **每次 `Read()` 都探測版本，不設 TTL。** 探測是單列主鍵查詢（微秒級），
  沒有理由為它在授權正確性上做時間折衷。
- **快取命中時回傳淺複製，呼叫端不得修改清單內的物件。** 淺複製只保護清單本身
  （增刪排序安全），物件是共用參考。要改主機資料一律走 `MutateBatch`——
  它的 mutation 拿到的是當場從資料庫反序列化的全新清單，不是快取物件。
