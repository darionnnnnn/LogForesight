# PRTG 整合規格

PRTG 是環境內既有的監控系統，提供**連續的數值時序**（sensor 每隔數分鐘量測一次）。
LogForesight 把它鏡像到本地資料庫，作為 NetIQ 離散事件之外的第二種訊號來源。

本文件描述**現況**：資料表、擷取、主機對應、設定與操作介面、狀態變更型規則與跨來源佐證。
值型分析（特徵計算、基線偏移、趨勢外推）尚未實作，見 `docs/BACKLOG.md`。

## 1. 定位與邊界

- **主檔以 NetIQ 為準**：主機身分永遠來自既有主機主檔（`lf_blobs` 的 `hosts`）。
  PRTG device 靠 IP 對應過去；**對不到就跳過，不猜測、不自動建立主機**。
- **PRTG 不取代 NetIQ**：兩者各自取數、各自失敗隔離。一台主機有 PRTG 對應時，
  日後的分析會同時看兩種訊號；沒有對應時就只有 NetIQ。這個分岔發生在主機層，
  **不存在「PRTG+NetIQ」的合併規則平台**——規則各自歸屬自己的來源。
  合成發生在**主機日的結論層**：PRTG finding 進當日 `lf_top_issues`、單向上調日風險、
  並以獨立區塊進 AI prompt（見 §9）。同一主機日事件日誌與 PRTG 同時示警的**跨來源佐證**在追加時判定、
  寫進紀錄的關聯欄位（§9）；以數值趨勢為依據的佐證屬於值型分析，見 `docs/BACKLOG.md`。
- **模組可完整停用**：`PrtgEnabled` 預設關閉，關閉時夜間擷取與歷史回填完全短路、不建立任何連線。
  唯一的例外是環境探測（見 §6）——它的用途正是在啟用之前先摸清環境，因此只要求位址與認證資訊。

## 2. 資料表（`lf_prtg_*`）

六張表，全部遵守 `docs/DB-SPEC.md` 的命名與可移植規範（`lf_` 前綴、小寫 snake_case、
識別字 ≤30 字元、不使用 SQL schema 前綴）。建表走 `EnsureCreated`（新 DB）＋
`SchemaUpgrader` 的冪等 DDL（既有 DB），兩邊都要維護。

| 表 | 內容 | 鍵 | 保留期 |
|---|---|---|---|
| `lf_prtg_devices` | device 結構鏡像（全站） | `objid`（PRTG 給的 id，非自增） | 不依保留期清；同步成功後刪除本趟沒出現的裝置（§3c） |
| `lf_prtg_sensors` | sensor 結構鏡像（**只含取數範圍內裝置**，§3c） | `objid` | 不依保留期清；同步成功後刪除本趟沒刷新到的列（§3c） |
| `lf_prtg_state_changes` | 狀態變更與訊息 | 自增 `id`，去重依 `(sensor_objid, changed_at)` | `PrtgRetentionDays` |
| `lf_prtg_values` | hourly 聚合數值 | 自增 `id`，唯一索引 `(sensor_objid, period_start)` | `PrtgRetentionDays` |
| `lf_prtg_host_map` | 主機對應（按日，自動重算） | 複合主鍵 `(map_date, device_objid)` | `PrtgRetentionDays` |
| `lf_prtg_manual_map` | 人工主機對應（長期有效） | `device_objid` | **不清**（人工結果不隨保留期消失） |
| `lf_prtg_ip_excludes` | IP 排除清單（長期有效，§4b） | `ip` | **不清** |

- **時間一律存本地時間**，與 `lf_daily_records.record_date` 等既有欄位同一語意
  （全站無 UTC 欄位；混存會在 UTC+8 造成靜默的跨日偏移）。
- 清理一律依 `created_at`（不是事件時間）並走 `BatchedPrune`，理由同 `lf_reports`：
  重跑舊日期時依事件時間清會讓剛補出來的資料立刻消失。
- `lf_prtg_sensors.category` / `category_source` 是 sensor 語意分類欄位。
  每日結構同步後依 type 對照**自動分類**（`category_source` 為 `auto`，分類值見 `PrtgSensorCategories`：
  traffic／disk／cpu／memory／availability／hardware）。對照＝設定 `PrtgSensorTypeCategoryOverrides`（§7）優先、
  再查內建表（`PrtgSensorTypeCategoryMap`，依實機探測的 type 分布挑選：availability＝`Ping`、`Port`、`HTTP`、`SNTP`、
  `DNS (DEPRECATED)`、`FTP`、`RDP (Remote Desktop)`、`Cisco IP SLA`；hardware＝`SNMP Cisco System Health`；
  traffic／disk／cpu／memory 各主要 SNMP／WMI type）。刻意不列的 type 與理由寫在類別註解——`SNMP Linux Load Average`
  不歸 cpu（不是百分比，資源守門會以百分比門檻誤判）、`SNMP Custom*` 等腳本型內容因環境而異、PRTG 自身健康 type 由守門另判。
  hardware 條目多半掛在網路設備上，只對應伺服器的站台 hardware 規則與儲存故障佐證可能仍不會命中。
  自動分類的候選是 **category 為 null 或來源為 auto** 的列：對照改了會重新套用，type 被移出兩張表時清回 null；
  **來源不是 auto 的非 null 分類永不動**（讀取與寫入前各判一次），且**每日結構同步本身絕不寫這兩欄**——
  人工指定的分類不能被同步或自動分類洗掉。分類的消費端：規則挑選與同裝置合併（§9）、跨來源佐證（§9）、
  未回報主機提示（§9）、資源守門自動偵測（§12）。

### 資料品質旗標（`PrtgDataQuality`）

`lf_prtg_values.quality` 用下列值，**任何統計與基線計算都不得把它們混為一談**：

| 值 | 意義 | 目前是否會被寫入 |
|---|---|---|
| `ok` | PRTG `historicdata` 的整小時真平均 | 是（激進策略夜間取數、歷史回填） |
| `sampled` | 站台以數值快照（§3b）自行平均的小時值；`min_value`／`max_value` 為該小時樣本極值，`coverage`＝樣本數 ÷ 期望樣本數 × 100 | 是（快照服務） |
| `unknown` | PRTG 回報 unknown，或 coverage 為 0 | 是 |
| `nodata` | 該時段沒有資料 | 是 |
| `paused` | PRTG 上被暫停 | 否——被暫停的 sensor 整段不抓、不寫任何列 |
| `untrusted` | probe 斷線期間取得，不可信 | 否——常數已定義，尚無判定來源（見 BACKLOG） |

`unknown` 與 `nodata` 的列**仍然寫入**（數值欄為 null）——「這個時段沒有可信資料」
本身就是要保留的事實。

**同一（sensor, 小時）的寫入優先序**：`ok` 覆蓋 `sampled`（精確值優先，走 `UpsertValues` 的同鍵覆蓋）；`sampled` 對既有 `sampled` **合併**（以 coverage 為權重加權平均、極值取聯集、coverage 相加，上限 100），既有列是 `ok` 時不動（`MergeSampledValues`）。站台一小時內重啟兩次，第二次寫出的部分樣本不會蓋掉第一次的。

**可用列（usable）**＝`ok`，或 `sampled` 且 `coverage ≥ 75`（常數 `PrtgValueUsability.SampledMinCoverage`）。校準（§11）與任何基線統計只認可用列；coverage 不足的 `sampled` 與 `unknown`／`nodata` 一樣排除。門檻 75 的意思是保守策略一小時 4 個樣本要有 3 個、激進 12 個要有 9 個——兩個樣本的平均不足以代表一小時。

`lf_prtg_state_changes.quality` 欄位同樣存在，但狀態變更沒有可用的品質判定依據，
目前一律寫 `ok`。

## 3. 擷取

每日擷取掛在既有夜間批次（`AnalysisOrchestrator.RunAsync`）內，與本機分析、NetIQ 分析
**並行**為第三條路徑，沿用既有取數排程窗口，不另開排程。

失敗語意比照 NetIQ：內部吞掉例外只記 log 與執行輸出，**PRTG 失敗不會讓整趟分析失敗**；
只有取消訊號會穿透。

PRTG 路徑的執行順序是：**裝置結構同步 → 主機對應（§4）與取數範圍（§3c）→ 範圍內的感測器結構與狀態變更同步 → 規則評估（§9）→
跨日標註、抑制標記 → 發佈 finding 登錄簿並補追加已落地的主機日 → 觸發式數值取數**。順序不可任意調動，理由見各節。

finding 的追加**不等取數**：追加的前提是「該主機當日紀錄已落地」，不是「取數完成」。
分析與 PRTG 並行，紀錄是一台一台寫進去的，所以追加分兩條路——規則評估完成時已落地的
由 PRTG 路徑掃一次補上，之後才落地的由兩條分析寫入路徑在紀錄剛寫完時就地併入。
時機、順序與登錄簿細節見 §9。

**回望多日**：排程作業頁「立即執行」指定回望 N 天、且範圍是全部主機時，PRTG 路徑處理昨天往前 N 天（與本機／NetIQ 同一個回望值與保留期上限；指定主機更新與夜間排程只處理昨天）。結構同步與狀態變更只做**一次**；主機對應只重算**最新一天**，較舊的日子沿用「該日或之前最近一日」的既有對應（往回 31 天，常數 `PrtgTriggeredValueFetcher.HostMapLookbackDays`）——拿今天的對應套到過去日會把裝置掛到錯的主機上，而且看起來與真的一模一樣；該日之前沒有任何對應時，那一天的 finding 不歸戶並在執行輸出寫明。規則評估分兩段：**先逐日評估全部日期並映射成簽章**，再逐日做跨日標註、抑制標記、發佈與補追加——跨日判定需要「本趟較舊日期」的結果，而逐日迴圈由近到遠，單段迴圈會少算（§9 跨日）。沉默規則（`silent`）只評估最新一天——它依 sensor 現況判定，對過去日評估是把今天的沉默套到過去。觸發式數值取數依策略逐日做（§3a 取數範圍、§3b 策略）。每日結果存進 `BatchRun.PrtgDays`（日期、結局、finding 數、歸戶主機數、有無對應、觸發主機、目標與失敗 sensor），執行總表依資料日期取用（docs/WEB-SPEC.md §9.10）。

每日擷取的階段各自獨立 try/catch，任一階段失敗其餘照跑（歷史回填只跑階段 3、4，見 §5）：

| 階段 | 端點 | 寫入 |
|---|---|---|
| 1. device 結構 | `table.json?content=devices`（全站） | `UpsertDevices`（全量 upsert）→ 刪除本趟沒出現的裝置 → 主機對應與取數範圍 |
| 2. sensor 結構 | `table.json?content=sensors&id=<裝置>`（範圍內逐台；範圍超過 500 台改全站分頁再濾） | `UpsertSensors`（不覆蓋分類欄）→ 刪除本趟沒刷新到的列 |
| 3. 狀態變更 | `table.json?content=messages&id=<裝置>&filter_drel=…`（範圍內逐台） | `AppendStateChanges`（一個日期區間一次取完、依日分桶，去重） |
| 4. hourly 數值 | `historicdata.json?avg=3600` | `UpsertValues`（逐 sensor 寫入即釋放）。**觸發式，非全量**，見下；**只在激進策略執行**（§3b） |

### 3a. 觸發式數值取數

實機環境有 4 萬多個 sensor（鏡像只保留取數範圍內的，§3c），逐一擷取一晚跑不完，且對「從沒出過問題的主機」取數沒有分析價值。
因此**預設數值只對觸發主機取**，三重過濾：

1. **觸發主機** ＝ 當日 `lf_daily_records.risk_level` 為高或中的主機
   ∪ PRTG 規則命中的主機（§9）；
2. 經 `lf_prtg_host_map` 反查 device（該日或之前最近一日的對應，往回 31 天，見 §3 回望多日）——**只取 `ok`**，
   `conflict` 歸屬不確定，納入會把數值掛到錯的主機上；
3. device 上 type 命中 `PrtgSensorTypeWhitelist`（§7）且未暫停的 sensor。

**取數範圍可放寬**（`PrtgValueFetchScope`，預設 `triggered`＝上述行為）。值型規則要設計基線時
會需要更廣的樣本，因此開放管理者改變第 1 步的候選主機來源；第 2、3 步的收斂三種模式完全相同：

| 模式 | 候選主機 |
|---|---|
| `triggered`（預設） | 當日高／中風險 ∪ PRTG 規則命中 |
| `all-mapped` | 全部有 `ok` 對應的主機。**要求 sensor type 白名單非空**，留空等於對全部 sensor 取數，存檔時就擋下 |
| `triggered-plus-list` | 觸發主機 ∪ `PrtgValueFetchExtraHosts` 指定的主機（名稱一行一個，不分大小寫） |

- **畫面上「關閉」是這個下拉的第一個選項**：選它送 `PrtgEnabled=false` 且**不送** `PrtgValueFetchScope`
  （範圍留著原值，重新啟用時不必再選一次）；選任一範圍送 `PrtgEnabled=true` 加該範圍。
  啟用與範圍本來就是同一個決定，拆成開關加下拉只會讓人設了範圍卻忘了開。
- 判定收斂在 `PrtgValueFetchScope.SelectHosts`，**每日擷取與歷史回填共用同一份**，不各寫一份。
- **`all-mapped` ＋ 空白名單有兩道閘門**：設定層存檔時拒絕，執行層（`EffectiveScope`）
  再退回 `triggered` 並在執行輸出說明。設定 blob 若由匯入或人工編輯繞過驗證寫進來，
  夜間批次不能就這樣去抓全機房。執行輸出與估算端點印的都是**實際生效**的範圍，不是設定值。
- 不合法或未設定的值一律退回 `triggered`——設定壞掉不該讓夜間批次改抓全機房。
- 指定主機以**名稱**存放，執行時解析成 id 並排除已停用與已合併的主機；對不到的名稱在執行輸出
  列出並略過（靜默略過會讓管理者以為設定生效了）。
- 維護頁提供**規模估算**（`GET settings/prtg-fetch-scope/estimate`）：把範圍放寬之前先看得到
  「一晚要抓幾個 sensor」，超過門檻顯示提醒但**不擋存**——跑不完的症狀是隔天資料不全、
  不是當下報錯，所以要在設定當下就講出來。`triggered` 的量逐日變動、事前算不出來，明講而不給假數字。

**`all-mapped` 單輪收工**：它的候選是「該日全部 `ok` 對應的主機」，與分析結果無關，
首輪就全部取完——之後的輪詢只是空等到分析結束，畫面上 PRTG 軌會長時間停在執行中。
這個模式因此不進輪詢迴圈、也不做收尾掃描。**該日一台 `ok` 對應主機都沒有時要出聲**
（管理者指定了「全部已對應主機」卻什麼都沒抓到，多半是鏡像還沒同步過），
訊息指路到「同步結構與對應」；`triggered` 模式沒有觸發主機是常態（當天沒人出問題），不警告。

**與分析並行、採輪詢**：NetIQ pipeline 內部是巢狀並行迴圈，沒有「單一主機完成」的掛載點，
硬插回呼要動並行迴圈本體。改由 PRTG 路徑**定期查詢已落地的分析結果**，把新出現的問題主機
拿去取數——分析每寫完一台紀錄就已在資料庫裡，效果同樣是「邊分析邊抓」，且完全不動 NetIQ pipeline。
分析結束後**再掃一次**：統計段先寫入紀錄、AI 段可能事後上調風險，只靠過程中的輪詢會漏掉這些主機；
已抓過的主機靠去重集合不重抓。

無觸發主機時整段短路為 0 次 `historicdata` 呼叫，執行輸出會寫明觸發主機數、目標 sensor 數與寫入筆數。

- **一律拉 hourly 聚合（`avg=3600`），絕不拉 raw**——raw 查詢是 PRTG API 最昂貴的操作。
- 對 PRTG 的併發上限由 `PrtgFetchConcurrency`（1~8，預設 2）以 semaphore 控制，每日擷取與歷史回填共用同一設定。
  實測 `historicdata` 單次 5～72 秒、最長超過 100 秒；併發 4 加速 2 倍以上且延遲放大 ≤1.23 倍，併發 8 時 PRTG 端開始排隊。畫面建議保守 2、激進 4，激進或回填時請求逾時建議 120 秒以上。
- **分頁只有一份實作**（`PrtgTablePager`），查詢分頁大小（`count`）預設 5000、寫入批次（`batchSize`）預設 500，兩者各自獨立。
  分頁大小不採用 50000 是因實機實測有高機率回傳空白 HTML 頁導致解析失敗。
  停止條件三道，任一成立即停：空頁或回應不是陣列／
  本頁未滿一頁／**本頁沒有任何沒見過的列**。第三道才是真正的收斂條件——實機的 PRTG 在
  `start` 超出範圍時會**夾到最後一頁**而不是回空頁，總筆數剛好是頁大小整數倍時，
  只靠前兩道會永遠跑不完、整趟夜間批次無聲卡死。
- **頁數上限是最後一道保險絲**：`treesize` 已知時取「⌈treesize ÷ 頁大小⌉ + 2」與 400 頁的較大者，
  未知時 400 頁（以單頁 5000 筆推算為 200 萬筆上限），並允許再多讀一頁確認結尾（總筆數剛好是「上限 × 頁大小」時，最後一頁是滿頁、
  下一頁才是空頁）。`treesize` 在帶 filter 的查詢下是否為過濾後筆數並無保證，因此**只能放大上限、
  不能縮小**，否則合法的長同步會被誤判。翻到上限仍未收斂時擲
  `PrtgPagingNotConvergedException`（含 content、頁數、已讀筆數、重複列數），**不靜默截斷**。
  該階段計為失敗，但**已寫入的筆數照樣回報**（寫入是冪等 upsert，留著比丟掉好）。
- **去重與收斂都以「該 content 真正的唯一鍵」為準**，預設是 objid。
  **messages 是例外**：它的 `objid` 是發出訊息的 sensor 而不是訊息自己的 id，同一天同一顆 sensor
  會有很多列，所以那條路徑的列鍵是「objid ＋ 時間」，與 `lf_prtg_state_changes` 的去重鍵一致。
  拿 objid 當鍵的話，每顆 sensor 只會留下第一筆狀態變更，而且整頁同一顆 sensor 時還會被當成
  分頁結尾提早停止——兩個後果都是靜默的。鍵取不到的列一律當新的：判不了重寧可重複寫入
  （寫入是冪等 upsert），不能靜默丟資料。夾到末頁時重複的列不再交給 mapper，
  階段完成行會寫出跳過的重複列數。
- **查詢一律帶 `sortby=objid`**：分頁的前提除了「遵守 `start`」還有「兩次查詢之間順序一致」。
  順序不穩定時會靜默漏列（去重擋得住重複、擋不住漏列）。不支援的版本會忽略這個參數。
  這台 PRTG 接不接受 `sortby`，用環境探測步驟 8 實測（§6）。
- **狀態變更一次取一個日期區間、逐裝置查詢**（`PrtgFetchService.FetchStateChangesRangeAsync`）：區間＝最早目標日的前一天到今天。
  對取數範圍（§3c）內每台裝置各查一次 `id=<裝置 objid>`，不對整台 PRTG（`id=0`）查——實機上 `id=0` 的 treesize 封頂在 100 萬、
  連 `count=5` 都會 60 秒逾時。「要查哪些物件」集中在 `StateChangeQueryObjects` 一處：若探測 9d-5 證實以裝置查詢不含底下感測器的訊息，
  只改那裡改成逐感測器。併發吃 `PrtgFetchConcurrency`，每台查詢前過資源守門（§12），單台失敗不影響其他台、階段計失敗並列出前 5 台的名稱與 objid。
  範圍為空或算不出來時整段略過、不發任何請求。範圍內每一台都取回 0 筆時加印一行指向探測 9d-5——那是「以裝置查詢不含感測器訊息」的訊號。
  規則評估看目標日前後一天的變更，只取目標日會讓跨午夜的 down 持續時間算不準；今天的部分列之後會被冪等補齊。
  每列依時間的日期分桶寫入，區間外的列丟棄。執行輸出印查詢台數、讀取筆數、新增筆數（「其餘已存在」——冪等重跑新增 0 是正常的）、
  區間內無訊息的台數、平均每台耗時、多日時的各日新增數；寫入的列中尚未在鏡像的感測器另計一行（新增的感測器下次結構同步補上，列照寫）。
  查詢帶相對日期過濾 `filter_drel`，取「涵蓋得到區間起點的最小級距」（7days／30days／12months；超過一年不帶）。
  **級距是滾動時間窗而非日曆日**（`7days` 從 `now-7d` 起算），邊界一律往上跳一階，寧可多抓。
  **用戶端的區間過濾仍然保留**：參數被忽略時結果照樣正確，只是慢；這台 PRTG 有沒有生效用環境探測 9d-4 判定（§6）。
  刻意不用 `today`／`yesterday`——跨午夜的執行窗口會整段漏掉。
- **依時間提早停止（只省查詢、不省資料，每台裝置各自判斷）**：messages 依時間由新到舊回傳，翻到整頁都早於區間時後面的頁全是不要的資料。
  每頁讀完自驗三件事，**全部成立才停**：本頁每一列的時間都解析得出來、依時間非遞增、本頁最新一筆早於「區間起點再前一天」。
  任一不成立就繼續翻到底——順序不穩或解析不了時寧可多翻，也不能少抓一筆。
  判斷放在 messages 的呼叫端，分頁器只提供「整頁讀完後問一次要不要停」的掛鉤（`PrtgTablePager` 的 `stopAfterPage`）；
  停下時算正常收斂。分頁固定帶 `sortby=objid`，一台裝置多顆感測器時頁內時間多半不是遞減，提早停止不常觸發；每台量小，不另改排序。
- **結構同步三階段各自回報進度**（`prtg-sync-devices`／`-sensors`／`-messages`）：
  裝置階段的分子是已讀取列數、分母取回應的 `treesize`，每滿 5 頁另寫一行執行輸出（常數 `ConsoleEveryPages`）；
  感測器與狀態變更階段逐台查詢，分子分母是**台數**（逐台不印分頁進度行，500 台會洗掉執行紀錄）。
  各階段完成時寫出**耗時**——沒有這個數字，「哪個階段值得優化」無從判斷。
  單一查詢的分頁必須循序；逐台的感測器與狀態變更階段在台與台之間套 `PrtgFetchConcurrency`。
  **不設整段逾時**——拍腦袋的倍數在大型環境會誤殺合法的長同步。中止的手段是 §5a 的停止鈕，
  收斂的保證是上面的三道停止條件與頁數上限（每台各自一份）。
- **PRTG 對大回應會回 HTTP 200 的空白 HTML 頁**（實測 73 bytes 的 `no-content OK`，同一查詢前一天成功）。`PrtgClient.GetJsonAsync` 讀到的內容去空白後以 `<` 開頭時擲 `PrtgClientException`，訊息含「PRTG 回傳 HTML 而非 JSON」與開頭 80 字。影響面：分頁階段計為失敗且原因明確、資源守門即時來源退回鏡像（§12）、探測子量測印「無法量測」＋原因（§6）、快照計一次失敗（§3b）。
- 記憶體：單頁最多 5000 筆 JSON（實測約 2 MB）＋ 500 筆緩衝，記憶體嚴格有界；逐頁轉換、每累積滿 500 筆就寫入一次；每批一個新的 `DbContext`（變更追蹤器每批歸零）。
  絕不把整份資料堆在記憶體最後才寫。
- 單一 sensor 的數值擷取失敗（逾時、404、暫時 5xx）只影響它自己，其餘 sensor 照樣落地並回報
  實際寫入筆數；只有「有 sensor 要抓卻一筆都沒抓到」才把該階段計為失敗。
  失敗數中的**逾時另外列出**：判定看例外鏈（`PrtgClient` 包裝例外時一律帶 `InnerException`），不比對在地化訊息字串。
- **`historicdata` 的欄位解析**（`PrtgFetchService.ParseHistoricData`）：
  - 值：取**第一個** `value_raw`（主要頻道；多頻道時同名鍵會重複出現，`JsonElement.TryGetProperty` 回的是最後一個，因此自行逐屬性取第一個）；缺失時退回 `value_`／`value`。
  - coverage：`coverage_raw ÷ 100`（10000＝100%）優先於 `coverage` 字串。
  - 時間：`datetime` 是本地化的區間字串（如「2026/9/10 下午 11:00:00 - 上午 12:00:00」），取「 - 」前段，先以伺服器目前文化、再以 InvariantCulture 解析。**`datetime_raw`（OLE 日期）只當退路**：實機同一列的 `datetime_raw` 與顯示字串差數小時，拿它當主來源會讓整份基線平移而無徵兆。用到退路的筆數在執行輸出單獨回報（「原始日期數值推得」）。
- 數值的時間欄位解析失敗時**會計數並在執行輸出回報筆數**，不靜默略過
  （PRTG 依伺服器地區設定輸出時間字串，格式不符時整段會解析失敗——這種缺口必須被看見）。

### 3b. 取數策略與數值快照

`PrtgFetchStrategy` 決定數值怎麼取。兩種策略的每個數值都取自實機探測能穩定取回的範圍，激進是可靠範圍的上緣，不是極限。

| 項目 | `conservative`（保守，預設） | `aggressive`（激進） |
|---|---|---|
| 快照間隔 | 15 分鐘 | 5 分鐘 |
| 夜間逐顆 `historicdata`（§3a 階段 4） | **不執行**，數值全由快照供應 | 執行，沿用取數範圍設定，給觸發主機 PRTG 真平均 |
| 每日 PRTG 負擔（估） | 約 96 次快照，分散整天、同時只有 1 個請求 | 約 288 次快照＋每晚數百次 `historicdata` |

- 常數與判定收斂在 `PrtgFetchStrategy`（`IsValid`／`Normalize`／`Profile`）；不合法或未設定的值退回保守。
- 保守策略下夜間路徑印「取數策略為保守，夜間不逐顆查詢歷史值，數值由快照供應。」；每晚開頭另印一行目前策略與快照間隔。
- **歷史回填不受策略影響**：永遠用 `historicdata`，併發吃 `PrtgFetchConcurrency`。
- 切換策略不回頭改既有列，只影響之後寫出的 coverage 期望值。

**升級注意**：策略預設保守，升級後夜間觸發式取數**不再執行**，數值改由快照供應；依賴夜間逐顆查詢的部署要切激進。

**快照服務**（`PrtgSnapshotHostedService`，Web 背景服務）：

- 每 60 秒檢查一次，依序全部成立才發快照：`PrtgEnabled`、連線設定齊備、結構同步未執行、歷史回填未執行、夜間取數不在 PRTG 階段（進度 phase 不以 `prtg-` 開頭）、距上次嘗試已滿目前間隔。前五項不成立時記下暫停原因供鏡像狀態顯示（§7 操作介面），不寫 log；「未到間隔」是正常等待，不算暫停。暫停超過 15 分鐘後恢復時寫一行執行輸出（原因、分鐘數、起訖時間）：激進策略回望多日時夜間取數會讓快照停上數小時，那段期間的 `sampled` 列 coverage 偏低，要看得出原因。
- **先定目標再查**：目標集合＝最近一次主機對應（錨點今天、回看 30 天，與校準 §11 同一個來源）中 `ok` 裝置上、符合白名單、未暫停的 sensor（`GetValueFetchTargets`），每小時刷新一次。目標為空時不發任何請求。
- 查詢：目標 ≤ 2000 顆（常數 `FilteredSnapshotLimit`，暫定）時依 objid 排序、每 50 顆一個 `table.json?content=sensors&columns=objid,lastvalue_raw,interval&filter_objid=…` 請求（組字串與批次大小和資源守門共用 `PrtgResourceGuardProbe.BuildObjidFilter`／`MaxBatchSize`）；取回少於要求時每輪印一行缺幾顆；單批失敗只記數、其餘照做，全部批次失敗才算本輪失敗。目標超過 2000 顆時退回單發全站 `count=50000`（實測約 10～15 秒），回傳筆數少於 `treesize` 時寫截斷警告。兩種取法共用同一段解析與累積。
- **累積器**（`PrtgSnapshotAccumulator`，Core）：per (objid, 小時) 累積 sum／count／min／max，整點把上一小時寫成 `sampled` 列（合併寫入，§2）；站台停止時把當前小時的部分樣本也寫出，coverage 如實反映缺口。
- **流量類正規化**：`lastvalue_raw` 是「最近一次掃描的傳輸量」，`historicdata` 第一頻道是「整小時總量」。type 屬於 `PrtgVolumeSensorTypes`（`SNMP Traffic 64bit`、`SNMP Traffic 32bit`、`Windows Network Card`）的樣本換算為 `lastvalue_raw × 3600 ÷ 掃描間隔秒數`；`interval` 接受純數字秒數與「60 s」「5 m」「1 h」（單位可用全稱），解析不了以 60 秒計並在每次快照的執行輸出回報筆數。其他 type 不換算（CPU／記憶體／磁碟／Ping 的 `lastvalue_raw` 與歷史值同尺度，已實測）。
- **退避**：連續失敗（例外、空白 HTML 頁、逾時）每滿 3 次，生效間隔加倍一次（第 3、6、9 次各加倍），上限 60 分鐘；成功即恢復策略設定值。間隔**從上次嘗試起算**——從上次成功起算時 PRTG 回不來就會每 60 秒重打一次快照。每次間隔變化寫一行執行輸出。
- 快照服務不受資源守門節制（它不在夜間批次內，且同時只有一個請求）；也不套 `PrtgFetchConcurrency`。
- 狀態變化（截斷、退避、恢復、寫入失敗）寫 NLog，服務內只保留最近 100 行供測試觀察，畫面不另列。
- **整點寫入失敗不丟樣本**：已從累積器取出的列留在待寫清單（上限 20 萬列，超過捨棄最舊），下次快照或站台停止時再寫；資料庫寫不進去不算 PRTG 失敗、不進退避。
- coverage 的期望樣本數以**策略設定的間隔**算，不用退避後的生效間隔——退避期間樣本本來就少，coverage 要如實變低，否則 1 個樣本會被算成滿涵蓋而通過可用門檻。
- **時間基準**：快照的小時邊界用站台本機時間，`historicdata` 的時間是 PRTG 伺服器本機時間；兩者同一時區才對得上。
- **規模估算**：取數範圍的估算端點另回快照目標數、每日列數、依保留天數估的總列數；總列數達 2,000 萬或白名單為空時帶警告。保留天數以 `min(PrtgRetentionDays, RetentionDays)` 近似，不含低於下限的退回邏輯。

### 3c. 取數範圍

整台 PRTG 有數千台裝置、數萬顆感測器，而會被歸戶的只有對應得到主機主檔的那些。感測器鏡像、狀態變更、快照
只處理**取數範圍**內的裝置；裝置鏡像維持全站（主機對應要用全部裝置的 IP 比對）。

**範圍**＝`PrtgScopeDevices.Compute`（唯一出口）算出的裝置集合，四項聯集：

1. 最近一次主機對應（回看 30 天）中 `ok` 的裝置；
2. `conflict` 中與主機有關的：一 IP 多主機（mapper 已指到一台主機），或同 IP 多裝置而該 IP 屬於某台啟用中主機
   （位址正規化與 mapper 共用 `PrtgHostMapper.BuildActiveHostIpLookup`）。同 IP 多裝置但與主機無關的 conflict **不收**——
   mapper 不看主機主檔就把它們標成 conflict，大型環境可能有數百台，整批收等於沒縮；
3. 人工對應表的全部裝置；
4. 資源守門的裝置（§12）：Sentinel 與 PRTG 位址比對到的裝置、覆寫清單感測器所在的裝置、鏡像中 corehealth 感測器所在的裝置——
   **不論守門是否啟用**。這些裝置多半不在主機主檔裡，少了它們，守門的自動偵測與 corehealth fallback 在縮圈後會找不到東西，
   覆寫清單的感測器會讀不到分類而被忽略。

`unmatched`、無 IP、IP 排除、同 IP 已有人工指定而略過的裝置不在範圍內。範圍為空（尚未對應到任何主機）時，
感測器與狀態變更階段略過並說明，不算失敗。

**範圍在哪裡算**：結構同步的範圍提供者（`FetchDayAsync` 的必填參數）在裝置階段之後、感測器階段之前呼叫一次——
夜間在裡面**一律**重算最新日對應（裝置階段失敗也照做：鏡像只在完整同步成功後才會刪列，拿既有鏡像重算是安全的，
且能反映白天主機主檔的異動）；手動同步只在裝置階段成功取得裝置時重算（§5a）；回填不重算，用既有對應。
參數刻意必填：可選的話漏接的呼叫端會靜默退回全站。

**過期列清除**（判準只有一條：本趟沒被刷新到的列）：

- 裝置：裝置階段無例外、已收斂、寫入數大於 0 時，刪除 `synced_at` 早於本趟起點的裝置，**在重算對應之前**做
  （已刪的裝置才不會再對到主機）。本趟沒出現的超過鏡像一半時一台都不刪並警告——API 帳號權限被縮小或查詢被截斷時
  回傳會合法地少一大塊，那不代表裝置被刪了。人工對應表不受影響。
- 感測器：範圍非空、感測器階段無例外（全站模式已收斂／逐台模式零失敗）、寫入數大於 0 時，刪除 `synced_at` 早於本趟起點的列——
  一條判準同時收掉「範圍外」與「PRTG 端已刪除」。不設過半保險：縮圈後第一趟本來就會刪掉九成以上，感測器鏡像是
  PRTG 的複本，清錯下一趟即可重建。範圍外感測器上的人工分類一併刪除。
- 狀態變更、數值、快照表中範圍外感測器的舊列不主動刪，交給保留期。

**感測器取法**：範圍 ≤ 500 台（常數 `PerDeviceSensorFetchLimit`，暫定）逐台查 `content=sensors&id=<裝置>`；超過時全站分頁、
寫入前濾成範圍內——兩種取法同一個 mapper、同一個過濾。

**白天新進範圍的裝置**：主機新增／改 IP、人工對應、IP 排除變更會當場重算今天的對應（§4），但那條路徑跑在請求執行緒上、
刻意不打 PRTG，新進範圍的裝置在鏡像裡還沒有感測器。快照服務（§3b）每輪（通過前置條件與間隔檢查後）找出範圍內
「鏡像一顆感測器都沒有」的裝置，每輪最多 50 台，以同一個逐台取法補抓感測器並重算自動分類（`BackfillSensorsForDevicesAsync`，
不清除任何列、不抓狀態變更）。PRTG 上真的沒有感測器的裝置記在記憶體，直到下一次結構同步（裝置表 `synced_at` 變新）才再試。
**守門覆寫清單**（§12）中鏡像找不到的感測器，同一輪以 `filter_objid` 查出所在裝置（`columns=objid,parentid`），
這些裝置**排在待補清單最前面**一併補抓；補上後範圍的守門項就會納入該裝置，之後由結構同步持續刷新。查不到的 objid 記住不再查，清空時機同上。
補抓例外只寫一行警告，不進快照退避。補上之前主機詳情的 PRTG 區塊說明「感測器清單尚未取得」，未回報提示為「無法判定」。

**「最後結構同步時間」取裝置表**（`GetLatestStructureSyncedAt`）：裝置只有結構同步會寫，感測器還會被範圍補抓零星寫入當下時間。

## 4. 主機對應

結構同步完成後、規則評估與數值取數之前執行（取數需要當日對應才知道要抓哪些 sensor），
把 PRTG device 用 IP 對應到主機主檔，逐日寫入 `lf_prtg_host_map`
（歷史回溯要用當時的對應，所以按日保存；同日重跑就地取代不累積）。

| 情況 | 結果 |
|---|---|
| IP 命中恰好一台活躍主機 | `ok`，填入主機 |
| IP 查無對應主機 | `unmatched`，不填主機（這份清單即監控覆蓋率稽核的基礎） |
| 一個 IP 由多台主機共用 | `conflict`，**沿用既有慣例對應到 HostId 最小者**，Note 列出其他候選 |
| 一個 IP 有多個 PRTG device | `conflict`，**不填主機**——無法判斷哪個 device 代表那台主機，猜了會張冠李戴 |
| device 沒有 IP（用 DNS 名稱或未設） | 不產生對應列，計入「略過」 |
| **device 有人工對應**（`lf_prtg_manual_map`） | `ok`，填入人工指定的主機，Note 標示為人工指定 |
| **device 的 IP 在排除清單**（`lf_prtg_ip_excludes`） | **不產生對應列**，計入「略過（已排除）」。人工對應優先序仍高於排除 |
| 同 IP 已有另一台 device 被人工指定 | **不產生對應列**，計入「略過（同 IP 已有人工指定 device）」 |

已停用（`Active = false`）與已合併（有 `MergedInto` 墓碑）的主機不參與對應。
**對應作業只讀主機主檔，絕不寫回。**

### 位址怎麼比對

PRTG device 的 `host` 欄位與主機主檔的 IP 都先過同一套正規化再比對，分兩層：

1. **純語法層**（`PrtgAddress.Normalize`，無 IO）：去前後空白、去 scheme、去路徑與 query、
   去 port（`10.1.2.3:8080` → `10.1.2.3`；`[::1]:8080` → `::1`，裸 IPv6 不得被誤拆）、
   去 IPv4 各段前導零，最後要求結果是合法 IP，否則回 null。
   `PrtgHostMapper.NormalizeIp` 是它的對外名稱，另有二十個呼叫點（IP 排除清單的儲存鍵等）
   共用同一份判定。
2. **解析層**（`PrtgAddressResolver`，有 IO）：純語法層回 null 時，先過 `PrtgAddress.IsDnsCandidate`
   ——只有長得像主機名稱的值（ASCII 英數、`-`、`_`、`.`，每段 1–63 字，結尾單一個點先去掉）才送 DNS；
   **整串沒有字母、或第一段全數字且每段只含數字或佔位字 x 的值視為打壞的 IPv4**（PRTG 裝置 host 常見
   `10.2xx.x.x`、`192.168.1.100x` 這種佔位值），直接回 null 不付 IO。條件刻意收窄：`163.com`、
   `1.dc.hq.tw`、`10.2.3.4-old` 都照常送 DNS——寧可多付一次逾時（失敗有快取）也不誤擋真主機。
   **非 ASCII（IDN）名稱不送 DNS**——內網幾乎不會有，且字面比對仍可命中。結尾單一個點由純語法層統一去掉。
   通過者以 `Dns.GetHostAddressesAsync` 只查 IPv4、逾時 1 秒可取消（不用 `Task.Run + Wait`：逾時後
   執行緒不回收，且 lambda 內的例外會讓偵錯器中斷）。**同一個實例內同一名稱只解析一次，失敗結果也快取**，
   避免解析不到的位址重複付逾時；不做跨趟的靜態快取（DNS 變更要能在下一趟生效）。

device 兩層都得不到 IP（名稱解析不到、或欄位根本沒填）→ 不產生對應列，計入「略過（無 IP）」。

**device 的分組鍵用解析層的結果**（不是純語法層）：填 DNS 名稱的 device 在純語法層一律回 null，
用它當鍵的話那些 device 根本進不了分組、也就永遠對不到主機。代價是同一組 DNS 名稱指向同一個位址時
會被判為「同 IP 多 device」的衝突，而 DNS 一改分組結果就跟著變——這是為了讓名稱型 device 能對應
而付的代價。**IP 排除清單的儲存鍵是純語法值**（`UpsertIpExclude` 只接受合法 IP），
而 device 側拿去比對的是解析後的 IP——因此以名稱建立、解析到被排除 IP 的 device 也會被排除，
這是要的行為：使用者排除的是那個位址，不是那種寫法。
`lf_prtg_devices.Ip` 一律保存 PRTG 原始字串，正規化只在比對時做——鏡像必須是 PRTG 的現況。

### 什麼時候會重算

| 時機 | 對哪一天 |
|---|---|
| 夜間 PRTG 路徑（裝置結構同步之後、感測器同步之前，§3c） | 最新一天 |
| **手動「同步結構與對應」**（§5a） | 今天 |
| 人工對應或 IP 排除清單變更 | 今天 |
| **主機主檔變更**：新增（有 IP）、IP 變更、停用／啟用、合併／解除合併 | 今天 |

主機主檔一改就重算，是因為對應的另一半來自那裡：不重算的話，新增一台主機後
要等到隔天夜間批次才對得上它的 PRTG device。PRTG 未啟用時這條路徑零成本直接返回。

> 註：`WebHost.IpAddress` 的定位是「最近已知的查詢線索，程式不拿它做比對」。
> PRTG 對應是這條規則的唯一例外，且只用於 PRTG 側的關聯，不影響主機身分判定。

### 4a. 人工對應（`lf_prtg_manual_map`）

自動對應對不到的 device（沒有 IP、IP 查無主機、一個 IP 多台 device）可由管理者
在 PRTG 維護頁的未對應／衝突清單指派給主機。人工對應**長期有效、不按日**，
且**每日自動對應一律優先採用它**——同 `lf_prtg_sensors.category` 的既有契約精神：
人工結果不被自動流程洗掉。

- 有人工對應的 device **完全跳出 IP 分組判定**：否則同 IP 的其他 device 會把它算進
  「此 IP 同時有 N 個 device」而被誤判成 conflict。
- 人工指定的主機**已停用或不存在**時，該 device 回到自動判定並輸出警告——
  否則主機一被合併或停用，那筆對應就會變成指向幽靈主機的假 `ok`。
- 刪除人工對應即恢復自動判定。新增與刪除都寫稽核。
- 不新增第四個 `map_status` 值（欄長 16，且下游多處以三個常數判定），
  人工來源以 Note 標示。

### 4b. IP 排除（`lf_prtg_ip_excludes`）

有些 IP 本來就不該去 PRTG 查（PRTG 側由別的系統負責、或該位址在 PRTG 是共用的）。
管理者可在 PRTG 維護頁的衝突清單直接把某個 IP 排除，**該 IP 底下所有 device 都不進對應、
不取數、不評規則**——因為它們根本不產生對應列，下游只認 `ok` 的既有邏輯自然把它們排除，
不需要在取數端另加判斷。

- **粒度是 IP 不是 device**：管理者的語意是「這個 IP 不要去查 PRTG」。以 device 為單位排除時，
  同 IP 的其他 device 仍會被對應，達不到目的。
- **人工對應優先於排除**：同一個 device 既有人工對應又被排除 IP 時採人工對應，Note 標示原因。
  排除是「預設不查」，人工指定是「明確要查」，後者是更強的意圖表達。
- **同 IP 有 device 被人工指定時，其餘 device 一律略過**。少了這條，管理者從「同 IP 多 device」
  的衝突中挑一台指派之後，剩下那台會因為分組只剩它一個而被自動判成 `ok` 對到同一台主機
  ——等於管理者的選擇被繞過。
- 三種「略過」原因在執行摘要與 Milestone **分開列出，什麼都沒略過時各項顯示 0**：
  「一個都沒有」與「這個功能不存在」在排查時必須分得出來。
- 新增與移除都寫稽核（`prtg_ip_exclude_set`／`prtg_ip_exclude_delete`）。**寫入後同步重算當日對應**（見 §8 端點說明）。
- 畫面上看得到的三處：主機明細的 PRTG 區塊在主機 IP 被排除時明寫「此主機 IP 已排除 PRTG 對應」
  （與「沒有對應」分開，否則無從分辨是還沒設定還是刻意排除）；人工對應清單每列附註
  「同 IP 另有 N 台 device 已略過」（N 只算真的被略過的，同 IP 上也有人工對應的不算）；
  維護頁的 IP 排除清單本身。

## 5. 歷史回填

手動觸發的離峰作業（**排程作業頁**），**不掛夜間排程**。從昨天往前回填
`PrtgBackfillDays` 天（預設 30）的數值與狀態變更，**不重跑 device／sensor 結構同步**——
結構鏡像永遠是現況，逐日重跑既是對 PRTG 做 N 次無謂的全量查詢，也會把「最後結構同步時間」
改寫成回填當下。回填時的 sensor 清單改從既有鏡像讀取。

- **狀態變更整趟只取一次**（區間＝最舊回填日的前一天到今天，§3a；只查取數範圍內裝置，範圍在回填開始時以既有對應算一次，§3c），之後逐日做數值；單日失敗不中止整趟，最後輸出成功、失敗、略過天數。進度的狀態變更段以台數顯示。
- **斷點續傳靠冪等**：所有寫入都有自然鍵去重，中斷後重跑同一區間不會產生重複資料，
  因此不需要額外的水位紀錄。
- **回填不做主機對應**：歷史對應無法重建，硬造出來的是假資料。
- **回填套用與每日擷取相同的三重過濾與取數範圍設定**（§3a，共用 `PrtgValueFetchScope.SelectHosts`）：
  預設只回填「該日曾為高／中風險」的主機、
  經該日（或最近一日）`ok` 對應的 device、且 type 命中白名單的 sensor。
  全量回填在實機是 42,393 sensor × 30 天，跑不完；對從沒出過問題的主機回填也沒有分析價值。
  某日沒有對應資料時取**該回填日往回**最近一日的對應（基準是正在回填的那一天，不是今天——
  拿今天當基準會取到回填當下的對應，與該日實際對應不符；只讀既有 map，不重建歷史對應）；
  往回找 31 天（`PrtgTriggeredValueFetcher.HostMapLookbackDays`），仍對不到就**略過**該日——計入略過天數、不算成功；
  全部略過時整趟結果為失敗並寫明「沒有任何一天有主機對應」。什麼都沒抓的成功最難察覺。
- **啟動閘門**（依序）：未啟用、連線不齊、環境探測執行中、鏡像沒有感測器、**取數執行中**、**同步結構與對應執行中**、**近（回填天數＋31）天沒有任何主機對應**。取數與同步執行中的訊息帶已執行分鐘數並指路停止鈕；沒有對應的訊息指路「同步結構與對應」。探測、取數、同步任一執行中都不放行：它們都打同一台 PRTG，疊在一起會互相拖慢（反向也成立：回填執行中，探測與同步都拒絕啟動）。對應的視窗要涵蓋「最舊回填日再往回 31 天」——逐日回填以各回填日為基準往回找對應，只看近 31 天會出現閘門放行、每一天都被略過的情形。
- **可停止**：`POST prtg-backfill/cancel`（權限同 start，寫稽核），排程作業頁 PRTG 卡的停止鈕只在回填執行中出現。停止後結果寫明「已停止：完成 X／N 天」，畫面標「已停止」。
- 回填**不受取數策略影響**（§3b），寫入的是 `ok` 列，會覆蓋同一小時的 `sampled` 列。

## 5a. 同步結構與對應（手動）

夜間排程會做結構同步，但那是每天一次的事：PRTG 剛啟用、或剛新增一批裝置時，
鏡像表要等到隔天才有內容，而主機對應、資源守門的自動偵測、觸發式取數全都建立在鏡像之上。
這條路徑讓管理者當場把鏡像補齊並立刻重算今天的對應。

**三步**（`fetchValues:false`，數值是夜間批次與回填的事）：① 裝置結構同步；② 對**今天**做主機對應並算取數範圍（§3c）；
③ 範圍內的感測器結構與狀態變更同步。產出摘要：裝置數、感測器數、對應各狀態的筆數、耗時。

- **背景工作而非同步端點**：實機要分頁讀完整棵裝置與感測器樹，可能跑上數十分鐘，
  塞在 HTTP 請求裡必然逾時。狀態與進度走 `GET prtg-structure-sync/status`。
- **可中止**：`POST prtg-structure-sync/cancel`（權限同 start，寫稽核）。沒有執行中時回 409——
  「沒東西可停」不是停止成功。兩個入口（維護頁鏡像頁籤、排程作業頁 PRTG 卡）各有一顆停止鈕，
  只在同步執行中出現。取消 token 一路傳進 HTTP 呼叫，進行中的那一頁查詢會**當場中斷**，
  不等它回來；已寫入鏡像的頁留著（寫入是冪等 upsert）。
  取消也會落地成「上次結果」，畫面說明鏡像可能不完整、夜間取數會重新同步。
- **上次結果持久化**（blob `prtg_sync_status`）：站台重啟後畫面仍要說得出上次同步是什麼時候、
  對應成果如何。**從未執行過時整個物件為 null，畫面顯示「尚未同步」**——
  這與「執行過但零筆」是不同的意思，不可用零值代表未執行。
- **夜間取數的結構同步也寫這份結果**（來源 `nightly`；手動同步寫 `manual`）：條件是這一趟真的做了結構同步、各階段沒有失敗、主機對應成功，任一不成立就不寫、保留上一次的結果——失敗的夜間同步不能把一筆成功的手動同步蓋成「上次失敗」，鏡像不完整時也不能宣稱成功。畫面在時間後標「（夜間取數）」或「（手動）」。欄位複製只有一份（`PrtgStructureSyncStatus.CopyFrom`）。
- **結構同步沒拿到任何裝置就不做對應**：分頁階段的失敗不擲例外、只累加計數，
  少了這道判斷，「PRTG 整台連不上」會被當成一次成功的同步（裝置 0、感測器 0）
  並把整份對應洗成空的。有失敗但拿到裝置時對應照做，但整體不報成功。
- **與取數執行互斥（兩個方向都要處理）**：
  - 取數執行進行中 → 拒絕啟動同步（那一趟本身就會同步結構與對應）。
  - 同步進行中，夜間或手動取數開始 → PRTG 日路徑**先等同步結束**（進度 phase `prtg-wait-sync`），
    **只有它成功結束時**才跳過自己的結構同步（鏡像剛更新過，重爬一次沒有新資訊），對應照做（對昨天）。
    被中止、有階段失敗、或等到上限仍未結束時鏡像是半套的，本趟照常自行同步——
    半套的鏡像與完整的鏡像在畫面上一模一樣，分不出來就會整晚用錯資料。
    Core 不認識 Web 的服務，這層依賴以 `IPrtgStructureSyncGate` 表達，未接上時行為完全不變。
  - 歷史回填執行中 → 拒絕啟動同步（反向的閘門在回填端，§5）。
- **前置條件**：`PrtgEnabled` 未啟用、未設定連線位址、或認證不齊時拒絕啟動並說明原因——
  這條路徑一開始就要對 PRTG 發動整棵樹的查詢，缺任何一項都不可能成功。
  未啟用時前端兩個入口（維護頁鏡像頁籤、排程作業頁 PRTG 卡）的按鈕先灰掉並指出開關在哪，
  後端的拒絕訊息同樣指路——只說「未啟用」而不說去哪開，使用者會在錯的頁面找。
- 入口在 PRTG 維護頁「鏡像狀態」頁籤與排程作業頁的 PRTG 狀態卡，兩處同一份狀態。
- 啟用 `PrtgEnabled` 時**不**自動啟動——啟用只是設定，什麼時候對 PRTG 發動一輪全量查詢由管理者決定。

## 6. 環境探測（probe）

PRTG 維護頁的唯讀探測工具，背景執行、前端輪詢狀態。產出這個 PRTG 環境的結構統計：

1. PRTG 版本
2. device 與 sensor 總數
3. **sensor type 分布**（依數量排序，含 unit 樣本與**內建分類**——只查內建對照、不含補充對照，未分類者提示到維護頁補充對照指定；
   以及累積覆蓋 50/80/90/95% 各需幾個 type）
4. 相依性（dependency）設定的使用比例；整份回應無法解析（不是 JSON、或根不是物件）時印出回應長度與開頭 200 字
5. 群組樹概要
6. **IP 覆蓋概要**：有幾個 device 設了 IPv4（判定與主機對應同一份純語法層，IP 帶 port 算 IP）、幾個是 DNS 名稱、
   幾個「無法判定」（打壞的 IP 如 `10.2xx.x.x`、含備註）並列出前 5 筆 objid——後者不會被解析也對不到主機，探測時就該看到。
   單次大 `count` 取到的筆數少於 `treesize` 時警告截斷（與 sensor 步驟同一道判定）；
   多於 `treesize` 時指出 `treesize` 不是該 content 的總筆數
7. Type × IPv4 覆蓋交叉統計
8. **分頁語意診斷**（純診斷，結果不影響探測成敗）：對 `devices`／`sensors`／`messages`（`messages` 帶 `id=<樣本裝置>&filter_drel=7days`——
   `id=0` 在大型環境必逾時；樣本裝置由步驟 3 的感測器挑，底下有非 Up 感測器者優先，挑不到就略過 messages）
   各發四次 `count=5` 查詢（`start=0`、`start=5`、`start=999999`，再加一次 `start=0&sortby=objid` 當排序對照），
   比較 objid 集合後歸納這台 PRTG 對 `start` 的處理：
   遵守且超出範圍回空頁（分頁可收斂）／超出範圍夾到末頁或回到第一頁（總筆數剛好整除頁大小時會重讀，
   分頁需要「本頁無新 objid 即停」的保險絲）／完全忽略 `start`（分頁抓不到第一頁以外的資料，只能單次大 `count`）。
   同一步另做**排序穩定性判定**：比對不帶與帶 `sortby=objid` 兩次查詢的頁內 objid 是否遞增，
   回答「這台 PRTG 接不接受 `sortby`」。順序不穩定時分頁會靜默漏列，去重擋得住重複、擋不住漏列。
   結構同步與資源守門的分頁迴圈都以「遵守 `start` 且順序穩定」為前提，這一步就是驗證那兩個前提。
   `messages` 的順序依據是時間不是 objid（objid 是發出訊息的 sensor），因此它的四次查詢帶
   `columns=objid,datetime`，順序判定看頁內 `datetime` 是否單調（全部可解析且非遞增或非遞減）；
   單調即代表分頁可行（列鍵含時間），`sortby=objid` 對它有效與否只是資訊、不影響結論；
   有任一筆 `datetime` 解析失敗就不下判斷。`devices`／`sensors` 仍以 objid 判順序。
9. **效能量測**（純診斷，結果不影響探測成敗）：各子量測獨立容錯，任一失敗只印原因、繼續下一個。
   成本為 87 次 `historicdata` 與最多 19 次 `table.json`。
   - **9a 分頁大小**：以結構同步同一組欄位（`objid,parentid,sensor,type,tags,unit,status,paused,dependency`）
     依序發 `count=500`／`2500`／`5000`／`50000` 各一次，每次印耗時、筆數、位元組數與**每千筆耗時**。
     結論行比較 `count=500` 與 `count=5000` 的每千筆耗時：後者低於前者一半代表每次請求的固定成本佔大宗、
     分頁放大有效；否則是回應時間與筆數近似線性、放大效益有限。任一次回 0 筆時印「樣本不足，無法判定」。
   - **9b objid-only**：`columns=objid&count=50000` 一次，與 9a 的 `count=50000` 那次比耗時與位元組的百分比，
     回答「只取 objid 能省多少」。9a 那次沒有結果時只印自己的數字。
   - **9c historicdata 併發**：由步驟 3 的樣本挑未暫停且有 objid 的感測器（依 type 優先序
     `Ping`→`SNMP CPU Load`→`SNMP Traffic 64bit`→`SNMP Memory`→其他），最多 64 顆分成**不重複**的四組
     （重查同一顆會吃到 PRTG 快取，讓後面的等級看起來變快），依序以併發 1／2／4／8 查同一天的
     `historicdata`（查詢形狀與夜間取數相同）。每級印總耗時、平均與最大延遲、失敗數與回傳列數；
     結論行以併發 1 為基準給總耗時倍率與加速倍數，平均延遲放大超過 1.5 倍時標記「PRTG 端開始排隊」。
     樣本不足 64 顆時盡量平均分配並註明結論僅供參考，一顆都沒有就略過。
   - **9d 值的取得方式**：回答「數值要走快照還是 `historicdata`」。9c 已量測過的 objid 不再重用
     （同一顆重查會吃到 PRTG 快取，量出來的延遲比實際快），9d-2 與 9d-3 挑的也彼此不重複。
     - **9d-1 快照成本與分布**：`columns=objid,status,interval,lastcheck,lastvalue,lastvalue_raw&count=50000`
       一次，印耗時、筆數、位元組數，以及 `interval`／`status` 各前 5 名的分布與
       `lastvalue_raw` 可解析為數字的比例（`InvariantCulture`）——後者決定快照的值能不能直接當數值用。
       `interval` 欄不可用時明說。每筆的 `lastvalue`／`lastvalue_raw` 留給 9d-2 對照。
     - **9d-2 單位對照與各 type 延遲**：`SNMP CPU Load`、`SNMP Memory`、`SNMP Disk Free`、
       `SNMP Traffic 64bit`、`Ping` 五種各挑最多 3 顆未暫停的感測器，循序各發一次 1 天 `historicdata`
       （查詢形狀與夜間取數相同），每顆印延遲、列數、`histdata` 末列原始內容（截 300 字）與快照的
       `lastvalue`／`lastvalue_raw`，每種 type 再印平均延遲。某 type 沒有樣本時明說。
     - **9d-3 固定成本**：另挑最多 8 顆 `Ping`，前半查 1 小時、後半查 1 天，循序發。
       比較兩組平均延遲與平均列數：1 小時延遲達 1 天的 0.7 倍以上代表成本以每次呼叫為主、
       與時間跨度關係小（回填可以拉大跨度省呼叫次數），否則是成本隨跨度成長。
     - **9d-4 messages 量級**：`content=messages&count=1&id=0` 配 `filter_drel=today` 與 `7days`
       各一次，只讀 `treesize`，看訊息量級。接著對 `today` 取第一頁與末頁（`start = treesize - 5`）的時間，判定這台 PRTG 的
       `filter_drel`：「生效」（末頁仍在今天）／「被忽略」（末頁早於今天）／「treesize 可能封頂」（末頁取不到資料）／「無法判定」。
       純診斷，只記錄整台 PRTG（`id=0`）的訊息量級；狀態變更的取法是逐裝置查詢（§3）。
     - **9d-5 逐物件查詢驗證**：驗證取數範圍（§3c）依賴的三個查詢在這台 PRTG 上成不成立。挑最多 3 台樣本裝置（同步驟 8），每台：
       (a) `content=sensors&id=<裝置>` 判「✓ 只回該裝置的感測器」／「✗ 回傳含其他裝置的感測器（id 參數未生效）」／無法判定；
       (b) `content=messages&id=<裝置>&filter_drel=7days&count=50` 判「✓ 含下層感測器訊息」／「✗ 只有裝置自身——狀態變更取數需改為逐感測器」／
       「⚠ 回傳的 objid 不屬於該裝置」／無資料。結論行彙總 (b)（任一台 ✓ 即 ✓）並印平均每台耗時。
       另以一顆感測器量 `id=<感測器>` 的 messages 耗時（改逐感測器時估算用），並以最多 50 顆量一次 `filter_objid` 分批取值（快照 §3b 的取法）。
       (a) 的預期集合來自步驟 3 的全站清單，那份清單被截斷時 (a) 可能誤判 ✗（步驟 3 已印截斷警告）。

任一子量測或步驟遇到 PRTG 回空白 HTML 頁（§3a）時，該項印「無法量測（PRTG 回傳 HTML 而非 JSON…）」並繼續下一項；步驟 4 的「回應無法解析」診斷行保留給其他非 HTML 的壞回應。

探測**不檢查 `PrtgEnabled`**（只需要位址與認證資訊）：它的用途正是在啟用模組之前先摸清環境。

探測結果只存在記憶體（不落地），供人工檢視與複製。它的用途是回答「後續分析層該怎麼設計」，
特別是 sensor 語意分類要不要做、以及主機對應的實際覆蓋率。

## 6a. 認證方式

PRTG API 支援三種認證，由 `PrtgAuthMode` 決定。**由已儲存設定建立 client 的唯一入口是
`PrtgClientFactory.Create(settings)`**（憑證解密與「憑證齊不齊」的判定都收斂在這裡）；
唯一例外是設定頁的「測試連線」——它用表單當下尚未存檔的值直接建構：

| 模式 | 請求帶的參數 | 適用 |
|---|---|---|
| `token`（預設） | `apitoken=<token>` | 較新版本的 PRTG。可限定唯讀、可單獨撤銷，優先選它 |
| `password` | `username=<u>&passhash=<h>` | 舊版 PRTG 沒有 API token 功能時。系統保存密碼並自動換取 passhash |
| `passhash` | `username=<u>&passhash=<h>` | 同上，但**由使用者自行提供 passhash**；系統不保存密碼、也不呼叫 `getpasshash.htm` |

`password` 模式的流程：client 在第一次實際請求之前，先呼叫
`GET /api/getpasshash.htm?username=&password=` 換取 passhash，**同一個 client 實例只換一次**
（併發請求以 semaphore 收斂），之後所有請求帶的是 passhash。
**密碼只出現在換取 passhash 那一次請求**，不會進入後續任何 URL。

`passhash` 模式則連那一次都沒有：使用者從 PRTG 取得 passhash（帳號設定頁的 Show passhash，
或自行呼叫 `getpasshash.htm`）後直接填入，client 建構時把它填進同一個快取欄位——
組 URL 與遮蔽都走與 `password` 模式完全相同的路徑，**系統端不存在任何帶密碼的請求**。
適用於「安全政策不允許第三方系統保存人員密碼」的環境。

passhash 等價於密碼（拿到就能用），因此**儲存等級比照密碼**：加密、write-only、DTO 只回布林。

**憑證錯誤會黏住**。理由是帳號鎖定：每個 sensor 的數值擷取各自呼叫一次 API，
不黏的話一組錯的帳號憑證就是「sensor 數 × 認證失敗」，足以觸發 PRTG 端的帳號鎖定。
一旦黏住，同一個 client 實例之後不再送出任何需要認證的請求，直接回同一則錯誤
（client 每趟執行新建，不會跨執行殘留）。兩條路徑的判定不同：

| 路徑 | 黏住 | 不黏 |
|---|---|---|
| 換取 passhash（`getpasshash.htm`，僅 `password` 模式） | HTTP 401/403、回應為 HTML 登入頁、passhash 為空白 | 傳輸類失敗（連不上、逾時）、HTTP 5xx |
| 資料請求（三種模式都走） | **帳號類認證**（`password`／`passhash`）的 HTTP 401 | HTTP 403、傳輸類失敗、以及 `token` 模式的任何狀態 |

資料請求只黏 401 不黏 403：403 可能是單一物件的授權不足（其他 sensor 仍讀得到），
黏住會讓一個沒權限的 sensor 拖垮整趟擷取。`token` 模式一律不黏——token 失效不會鎖任何帳號。
`passhash` 模式沒有換取步驟，它的保護完全來自資料請求那一列。

**憑證的寫入依當前認證方式隔離**：切換模式時其他認證方式的欄位在畫面上是隱藏的，
送上來的是切換前的殘值；後端只寫入當前模式那一組，否則會把別組憑證靜默清空或覆寫。
`PrtgUsername` 由 `password` 與 `passhash` 共用，兩者切換時不清空；`token` 模式不寫入它
（避免非設定頁的呼叫端沒帶它時把已存帳號清掉）。

不採用 PRTG 也支援的 `password=` 直掛：密碼會出現在每一個請求的 URL，
進 PRTG 的存取 log 與中間設備。

例外訊息保證不含 token、密碼、passhash（原文與 URL 編碼形式都會遮蔽），可直接顯示給操作者。

## 7. 設定

全部存於 `SystemSettings`（DB），`appsettings.json` 不涉入。

| 設定 | 預設 | 說明 |
|---|---|---|
| `PrtgEnabled` | false | 模組總開關。關閉時整條路徑短路。**畫面入口是維護頁「擷取參數」的取數範圍下拉**（見下方操作介面），不另設開關 |
| `PrtgUrl` | — | PRTG core server 位址（含 scheme）。啟用時必填且須為 http/https |
| `PrtgAuthMode` | `token` | 認證方式：`token`／`password`／`passhash`。見 §6a |
| `PrtgApiTokenEnc` | — | API token 密文（`CryptoHelper`，AES-256-CBC）。write-only，DTO 只回布林。`token` 模式使用 |
| `PrtgUsername` | — | PRTG 帳號。`password` 與 `passhash` 兩模式共用 |
| `PrtgPasswordEnc` | — | PRTG 密碼密文。write-only，DTO 只回布林。`password` 模式使用 |
| `PrtgPasshashEnc` | — | PRTG passhash 密文。write-only，DTO 只回布林。`passhash` 模式使用 |
| `PrtgIgnoreSslErrors` | false | 忽略憑證錯誤。自簽憑證環境的顯式逃生門，啟用時每次建立連線都記 WARN |
| `PrtgTimeoutSeconds` | 60 | 單次請求逾時（5~600） |
| `PrtgFetchConcurrency` | 2 | 對 PRTG 的併發上限（1~8）。建議保守 2、激進 4 |
| `PrtgBackfillDays` | 30 | 歷史回填天數（1~365） |
| `PrtgRetentionDays` | 180 | 鏡像資料保留天數（下限、上限與收斂規則見 `docs/DB-SPEC.md` 保留策略） |
| `PrtgSensorTypeWhitelist` | 8 種分析型 type | 要擷取**數值**的 sensor type（一行一個，不分大小寫）。**留空＝不限制**。預設不含 Ping（數值量大且雜訊高）。**只管數值取數與快照，不影響規則評估的母體**（§9） |
| `PrtgSensorTypeCategoryOverrides` | 空 | sensor type 分類補充對照（一行一個 `type=分類`，不分大小寫；分類為 traffic／disk／cpu／memory／availability／hardware）。優先於內建對照，下次結構同步後生效（§2）。存檔以 `PrtgSensorTypeCategoryMap.ParseOverrides` 驗證，錯誤列出行號與合法分類；分類值一律存小寫。**會連帶改變資源守門自動偵測**（§12） |
| `PrtgValueFetchScope` | `triggered` | 數值取數的主機範圍：`triggered`／`all-mapped`／`triggered-plus-list`（§3a）。`all-mapped` 要求白名單非空。畫面上與 `PrtgEnabled` 併成同一個四選一下拉，「關閉」不是合法值、只代表 `PrtgEnabled=false` |
| `PrtgValueFetchExtraHosts` | 空 | `triggered-plus-list` 模式額外納入的主機名稱（一行一個，不分大小寫） |
| `PrtgFetchStrategy` | `conservative` | 取數策略：`conservative`／`aggressive`（§3b）。決定快照間隔與夜間是否逐顆查詢歷史值 |
| `PrtgResourceGuardEnabled` | false | 資源守門總開關（§12） |
| `PrtgResourceGuardSensorObjids` | 空 | 受監看 sensor 的覆寫清單（一行一個 objid）。**留空＝自動偵測** |
| `PrtgResourceGuardCpuPercent` | 85 | CPU 使用率達此值算緊張（1~100） |
| `PrtgResourceGuardMemoryFreePercent` | 10 | **可用**記憶體低於此值算緊張（0~99）。方向與 CPU 相反 |
| `PrtgResourceGuardCheckSeconds` | 60 | 檢查間隔（15~600） |
| `PrtgResourceGuardPauseMinutes` | 5 | 判定緊張後等待多久再檢查（1~60） |
| `PrtgResourceGuardStrikes` | 2 | 連續幾次超標才算緊張（1~10） |
| `PrtgResourceGuardMaxPauseMinutes` | 120 | **單趟累計暫停上限**（10~600）。超過後放行並警告 |

token、密碼與 passhash 的處理都與 SMTP 密碼、AI 金鑰完全對稱：留空＝沿用既有、要清除需另外勾選清除。
啟用 PRTG 時依模式驗證憑證是否齊備——**「新存或既有」皆算有**，否則密碼欄留空（＝沿用）
會被誤判成沒設定，使用者改任何其他設定都會被擋下。
解密一律先 `IsEncrypted` 判斷再 `Decrypt`（對非密文直接解密會擲例外）。

### 操作介面

功能依性質分置——**靜態設定在 PRTG 維護頁，動態工作在排程作業頁**，
唯一的例外是**資源守門**：它同時節制 NetIQ 取數與 PRTG 擷取兩路，不只是 PRTG 的事，
因此設定放在系統設定頁的「資源守門」頁籤，與其他全站參數在一起（維護頁留一行指路）。
設定頁沒有其他 PRTG 頁籤（只在「資料保留」頁籤留一行指路）：

| 頁面 | 內容 |
|---|---|
| **PRTG 維護頁 `/admin/prtg`**（權限 Maintain，側欄「系統管理」內緊鄰 NetIQ） | 四個頁籤：連線／擷取參數（**首欄「PRTG 擷取」四選一下拉＝總開關＋取數範圍**）／鏡像狀態（含「同步結構與對應」§5a）／環境探測（含校準匯出 §11 與資料搬運 §10）。各頁籤的卡片與儲存行為見 docs/WEB-SPEC.md §9.9e，此處不重複 |
| **設定頁「資源守門」頁籤** | 八個 `PrtgResourceGuard*` 欄位與「預覽／自動偵測並填入」兩顆鈕（§12）。與該頁其他設定共用整包儲存，不另開儲存鈕——分兩顆時按其中一顆，另一區未存的改動會在重載時被覆蓋回舊值 |
| **排程作業頁** | 模組狀態（未啟用時附連結指向維護頁「擷取參數」；啟用時一併顯示生效範圍）、**同步結構與對應**與**歷史回填**的操作與進度、每日擷取進度軌（回填天數在維護頁設定，此處顯示「將回填 N 天」）。**這頁沒有開關**——兩個入口寫同一個值會互相蓋 |

連線、參數與 `PrtgEnabled` 的存檔一律走 `PUT settings/prtg` 專屬端點——不走整包設定更新：
整包更新會在「讀取到送出之間」覆蓋他人的改動，也會被與 PRTG 無關的跨欄位驗證擋下。
認證驗證與憑證寫入在整包更新與專屬端點之間**共用同一份實作**，不得各寫一份。

> **`UpdateSystemSettingsRequest` 中的 PRTG 欄位一律可空，且「有送才更新」**——
> 設定頁不送出任何 PRTG 欄位，無條件寫入時設定頁存檔會把 PRTG 位址清空、
> 把模組關掉、或把維護頁剛改好的擷取參數回退成讀取當下的舊值。
> 新增任何「只出現在單一頁面的設定欄位」時都要套用同一規則。
>
> **專屬端點的欄位同樣一律可空**：讓參數非可空帶預設值時，只想改 URL 的呼叫端
> 送 `{prtgUrl}` 就會把逾時與保留天數重設、白名單清空——白名單清空等於對全部 sensor
> 取數，大型環境會直接壓垮 PRTG core。
>
> **跨欄位檢查要用 effective 值**：`PrtgRetentionDays ≤ RetentionDays` 這類檢查
> 若直接比對請求中的兩個欄位，只調小 `RetentionDays` 而未送 `PrtgRetentionDays` 時
> 會拿到 null 而放行，上限形同失效。一律取「請求值 ?? 已儲存值」再比較。

> 「最新資料時間」是從鏡像資料推導的，不等於「最後一次成功同步的時間」：連續數晚擷取到
> 0 筆時這個時間不會變動。要分辨兩者需要獨立的同步紀錄，見 `docs/BACKLOG.md`。

## 8. API 端點

全部在 `/api/admin/settings` 之下，需 `Maintain` 權限：

| 端點 | 用途 |
|---|---|
| `POST prtg-test` | 測試連線（用表單當下的值，token／密碼／passhash 留空沿用已存） |
| `GET prtg-mirror` | 鏡像狀態與主機對應摘要；含快照最近成功時間、感測器數、生效間隔、連續失敗數、是否退避中、暫停原因 |
| `POST prtg-probe/start`、`GET prtg-probe/status` | 環境探測 |
| `POST prtg-backfill/start`、`POST prtg-backfill/cancel`、`GET prtg-backfill/status` | 歷史回填（§5）。status 含天數、當日 sensor 進度、讀取狀態變更進度與是否被停止；cancel 在沒有執行中時回 409 |
| `POST prtg-structure-sync/start`、`POST prtg-structure-sync/cancel`、`GET prtg-structure-sync/status` | 同步結構與對應（§5a）。status 含執行中進度與上次結果摘要；上次結果為 null 代表從未執行過。cancel 在沒有執行中時回 409；成功時回 200（只代表取消訊號已送出，實際結束要看 status） |
| `PUT prtg` | PRTG 專屬設定更新（維護頁「連線與參數」，只寫 PRTG 欄位；**含總開關 `PrtgEnabled`**，有送才更新） |
| `GET／PUT／DELETE prtg-manual-map` | 人工主機對應的查詢、指派與移除（§4a） |
| `GET prtg-host-map?status=conflict&page=&pageSize=` | 衝突清單分頁。每列帶 `conflictKind`（`multi-device`／`multi-host`）、同 IP 的 device 清單與候選主機清單，供指派介面依型別分岔 |
| `GET／PUT／DELETE prtg-ip-excludes` | IP 排除清單的查詢、新增與移除（§4b） |
| `GET prtg-resource-guard/preview[?forceAuto=]` | 預覽受監看 sensor 與其當下值（§12）。**不要求 `PrtgEnabled` 與守門開關**——用途正是在啟用前確認偵測結果與數值語意。`forceAuto=true` 忽略覆寫清單強制自動偵測。回應的 `source` 四值：`override`（覆寫清單）／`live`（直接查 PRTG）／`mirror-fallback`（查 PRTG 失敗、退回鏡像）／`auto`（讀鏡像） |
| `GET prtg-fetch-scope/estimate?scope=` | 取數範圍的規模估算（§3a）：回該模式涵蓋的主機／device／sensor 數，另回快照目標數、每日列數、保留期總列數與警告（§3b） |
| `GET prtg-export`、`POST prtg-import` | 鏡像資料匯出／匯入（§10） |

主機明細的 PRTG 區塊另走 `GET /api/host-detail/{hostId}/prtg`（回該主機對應的 device 與其 sensor；device 帶名稱、sensor 帶 PRTG 原始狀態與分類，
畫面狀態欄依前綴上色並顯示原字串，外部字串一律 `textContent`）；
它不經 `RecordDetailQueryService`，**可見性檢查在 controller 自己做**（同表其他端點是在 service 內做），
不可見的主機回 404（與全站慣例一致：不洩漏主機存在與否）。

連線失敗（含 401、逾時、憑證問題）**不是例外**，回 `Success = false` 讓畫面就地顯示；
只有輸入本身不合法才擲驗證例外。錯誤訊息保證不含 token、密碼與 passhash（原文與 URL 編碼形式都會遮蔽）。

## 9. 規則第一階（狀態變更型）

分析層的第一步，只用**狀態變更**這份既有資料（夜間單次 API 呼叫即取得，成本低），
不需要數值基線。規則存在規則維護頁的 `prtg` 平台，**規則庫是執行期唯一的真相**：
門檻、分類、嚴重度、「重大」旗標、描述與知識庫全部取自命中的那條規則（`PrtgRuleCatalog` 只提供 seed 預設值）。

### 內建規則（seed v7）

| Id | 代碼 | 適用分類 | 分類／嚴重度／重大 | 預設門檻 | 對日風險 |
|---|---|---|---|---|---|
| `builtin-prtg-down` | down | 全部 | Service／High／否 | 60 分 | 中 |
| `builtin-prtg-down-availability` | down | availability | Service／High／**是** | 30 分 | 高 |
| `builtin-prtg-down-hardware` | down | hardware | Hardware／High／否 | 60 分 | 中 |
| `builtin-prtg-flapping` | flapping | 全部 | Service／Medium／否 | 5 次往返 | 不變 |
| `builtin-prtg-warning` | warning | 全部 | Resource／Medium／否 | 240 分 | 不變 |
| `builtin-prtg-warning-disk` | warning | disk | Storage／High／否 | 240 分 | 中 |
| `builtin-prtg-warning-hardware` | warning | hardware | Hardware／High／否 | 120 分 | 中 |
| `builtin-prtg-silent` | silent | （不可指定） | Service／Medium／否 | 整日 | 不變 |

語意：`down`＝進入 Down 且日終未恢復、持續達門檻；`flapping`＝一日內 Down↔Up 往返達門檻；
`warning`＝Warning 累計達門檻；`silent`＝device 底下全部未暫停 sensor 皆 Unknown 或無狀態。
availability 的 30 分與 hardware warning 的 120 分是**暫定值**，待校準（§11）。
描述文字不嵌門檻數字——管理者改門檻後數字就會說謊，門檻由規則頁另列。

### 規則怎麼挑、母體是誰

- **母體＝鏡像中全部未暫停 sensor**（`GetSensorStatuses`），**不受取數白名單限制**。白名單是為數值量體設計的、
  預設不含 Ping，綁在規則上會讓最直接的失聯訊號永遠抓不到。鏡像只含取數範圍內裝置的 sensor（§3c），母體因此也只到範圍內，
  範圍外的 sensor 本來就歸不了戶，不必先算再丟。
  silent 的分母因此也是 device 底下全部未暫停 sensor。
- **依分類挑規則**（`PrtgRuleEvaluator` 內唯一一份）：規則欄位 `PrtgSensorCategory`（null＝全部分類）。
  逐 sensor、逐代碼挑：分類相符（不分大小寫）的規則優先，沒有才用不限分類的規則；候選多條取 Id 字典序最小，
  並對每個「代碼＋分類」組合在執行輸出警告一次；沒有候選就不評估。sensor 分類為 null 只會挑到不限分類的規則。
  silent 只用不限分類的規則（`RuleValidator` 禁止 silent 指定分類，也禁止非 prtg 規則填這欄）。
- **同裝置合併**：device 當日有 availability 分類 sensor 的 `down` 時，同裝置**非** availability sensor 的
  `down`／`flapping` 不另外發出，筆數附在 objid 最小那筆 availability down 的 Detail
  （「同裝置另有 N 顆 sensor 同時 Down 或震盪（已合併）」）。warning、silent 與 availability 自己的 finding 不受影響；
  沒有 availability down 的 device 不合併。分類尚未算出（null）的 sensor 不能當合併主筆。
- **已於 PRTG 確認**：`Down (Acknowledged)` 仍算 Down（持續時間、往返照算）。**只有 `down` finding** 依「進入 Down 區段的最後一筆狀態」
  標記已確認：**不帶「重大」旗標**（嚴重度不變，High 仍拉「中」）、判定敘述之後加「已於 PRTG 確認」——PRTG 操作者已接手的事不判高風險日。
  `flapping` 照算往返但不標確認；`Down (Partial)` 不算確認。判定收斂在 `PrtgSensorStatuses.IsAcknowledged`。

判定細節（皆為踩過的坑，改動前先讀）：

- **狀態字串是 PRTG 原值、未正規化**，會出現 `Down (Acknowledged)`／`Down (Partial)` 等變體，
  因此一律**前綴比對且不分大小寫**，判定收斂在 `PrtgSensorStatuses`。
- **`prev_status` 恆為 null 不可依賴**：持續時長與往返次數只能靠同一 sensor 依時間排序的相鄰列推導。
- 每日擷取只保留當天的狀態變更，因此 **`down` 與 `warning` 都必須回查前一日的最後一筆**
  取得當日零時的起始狀態；持續時間**自當日零時起算**（不是從前一日進入的時點）。
  `warning` 少了這道回看時，「前一日進入 Warning、當日整天沒有變更」這個最典型的情境永遠不會命中。
- `silent` 的判定來源是 `lf_prtg_sensors.status`（結構同步的現況值），**不是狀態變更表**——
  健康的 sensor 本來就整天零筆變更，用變更表判定會讓全機房每天都被判為沉默。
  device 底下沒有未暫停 sensor 時不算沉默。
- **既有部署升級後要到規則維護頁的升級橫幅套用內建規則更新**才會拿到 seed v7 的規則。
  規則庫沒有任何啟用中的 PRTG 規則時，夜間批次輸出「規則庫尚無啟用中的 PRTG 規則」並跳過評估，
  **不會靜默產生零 finding**。新增或修改內建規則時必須遞增 `KnownIssueSeed.Version`，否則橫幅不會出現；
  seed 比對（`RuleImportPlanner.ContentEqualExceptEnabled`）涵蓋全部內容欄位，由反射測試守住。

### 升級後的行為差異（部署前告知使用者）

- **必須到規則維護頁套用內建規則更新**才有分類規則；未套用前舊的四條規則照跑，分類覆寫、availability 合併與「連通性 down 才重大」都不生效。
  管理者修改過的內建規則不會被自動覆蓋（需勾選才覆蓋），調過的門檻不會被洗掉。
- 一般 `down`（不限分類）只把日風險拉到「中」，只有連通性分類的 down 拉到「高」。
- Ping 等不在取數白名單內的 sensor 開始參與規則評估；同裝置失聯時其他 sensor 的 down／flapping 合併為一筆。
- 已於 PRTG 確認的 down 不帶「重大」；連續 14 日以上的 down 不拉日風險；重複或連續出現的 finding 嚴重度升一級。
- 既有對 `builtin-prtg-*` 建立的**規則型**抑制開始生效；舊 PRTG 問題的**簽章型**抑制與處理狀態（鍵含 `Source`）不延續，
  升級後這些 finding 會以新問題出現在待辦，需重新標記或改建規則型抑制。
- 既有紀錄不回寫：舊 finding 的 `RuleId` 是舊格式（`prtg-{代碼}`）、`Source` 是 `PRTG`，詳情頁掛不出知識庫、
  問題排行會多一列舊格式的「PRTG」，重跑該日才換成新值，其餘隨保留期消失。

### 跨日標註、升級與長期 Down

PRTG 規則是單日判定，跨日語意在 PRTG 路徑自己補（`PrtgCrossDay`，不進 `TrendAnalyzer`——
`ChannelCoverage.WasRead` 對 PRTG 頻道會讓它永遠停在暖身期）。以 **EventKey 為單位、不限主機**
（同一顆 sensor 換了對應主機仍算同一顆），看當日之前 14 天加上當日：

- 歷史＝`lf_top_issues` 的命中日期（`GetPrtgFindingHitDates`，EventKey 每批最多 500 個，避開 SQL Server 參數上限）
  ∪ 本趟較舊日期的已歸戶 finding。**本趟有評估的日期只認本趟結果**——重跑時資料庫裡那幾天是上一趟寫的，
  門檻或規則改過後可能已不成立。
- `N`＝當日之前 14 天的命中次數加上當日這一次，`M`＝含當日往回連續命中的天數。`N ≥ 2` 時 Detail 加「近 14 日第 N 次，連續第 M 日」；首次不加字。
- **升級**：`M ≥ 3` 或 `N ≥ 3` 時嚴重度升一級（封頂 High，不動「重大」旗標）——持續三天的 disk warning 因此會拉「中」。
- **長期 Down**：`down` 且 `M ≥ 14` 視為沒人移除的死 sensor——關掉「重大」、嚴重度封頂 Medium（**不拉日風險**）、
  Detail 加「已連續 M 日，建議在 PRTG 暫停該 sensor 或建立抑制」，且不做上一條升級。
- 常數在 `PrtgRuleCatalog`（`CrossDayWindowDays`／`EscalateConsecutiveDays`／`EscalateHitsInWindow`／`ChronicDownDays`），暫定待校準。
- 查詢失敗只印警告、改用本趟資料判定，照常發佈。

### 抑制

規則型（RuleId）與簽章型（`IssueSignatureKey`）抑制對 PRTG finding 生效：PRTG 路徑在**發佈登錄簿之前**
依該主機（名稱＋群組）的有效抑制標記 `Suppressed`，與事件層共用 `SuppressionFilter.MarkSuppressed`，
兩條追加路徑拿到的都是已標記簽章。被抑制的 finding **仍追加**（抑制是不吵、不拉風險，不是不存在），
不進 AI prompt 的 PRTG 段。案件掛接與郵件摘要對已抑制問題的處理與事件層相同（兩者皆不另外過濾）。

規則頁的抑制影響面預覽對 prtg 規則依 EventKey 前綴 `prtg:{代碼}:` 在目標主機上計數。
`lf_top_issues` 沒有規則 Id 與分類欄，**分類規則的預覽數字是「同代碼全部分類」的上限**。

### finding 如何進入既有全鏈

命中的 finding 映射成問題簽章寫入當日該主機的 `lf_top_issues`，
處理狀態、問題排行、郵件通知因此自動涵蓋它，**不另建 finding 表與獨立 UI**：

- `LogName` 為 `PRTG`、`Source` 為 `PRTG:{規則代碼}`、`EventId = 0`、`EventKey = prtg:{規則代碼}:{objid}`。
  **判定「是不是 PRTG finding」一律看 `LogName`**（`PrtgFindingMapper.IsPrtg`）；`Source` 帶代碼讓問題排行
  依規則分列（聚合鍵是 `(Source, EventId)`，全部塞 `PRTG` 會讓所有規則、所有 sensor 塌成一列），
  規則代碼由 `PrtgFindingMapper.TryGetRuleCode` 解出。
- `RuleId`＝命中規則的 Id，詳情頁的知識庫面板據此反查；問題排行與紀錄清單的白話說明依 `Source` 解出代碼找規則
  （恰一條用它；多條時取不限分類那條；不限分類的也有多條時只認 `builtin-prtg-{代碼}`；都沒有回 null，不猜）。
- `SampleMessages[0]`＝Detail：帶 device 名稱、sensor 名稱、type 與量值（名稱查不到時以 objid 代替），
  跨日標註與合併筆數附在尾端。簽章另帶 `PrtgSensorCategory`（device 層的 silent 為 null），供跨來源佐證使用。
- `Magnitude`（分鐘數／往返次數）**不寫進 `Count`**（恆 1）——整日 Down 是 1440，會在排行的次數維度壓過真實事件。
- **`EventKey` 不得含 `|`**：處理狀態鍵 `IssueSignatureKey` 以 `|` 分段解析。長度上限 255。
- 分類與嚴重度取自規則物件，**不走 `KnownIssueCatalog.Classify`**——它只認 windows／linux 平台，PRTG 走它會讓 `EventKey` 被清空。
- **只對「當天已有分析紀錄」的主機追加**：硬造一筆只有 PRTG finding 的紀錄會讓
  「未回報主機」「覆蓋缺口」等既有統計失真。沒被分析涵蓋的主機，其 finding 留待隔日。
- 詳情已被保留期精簡（`detail_pruned`）的紀錄不追加，避免把精簡後的殘骸寫回。
- 追加依 `EventKey` 去重，同一天重跑不產生重複；**既有 finding 不回寫**（重跑同日不會更新舊 finding 的 Detail 或 RuleId）。
- **命中主機會回饋觸發式取數的佇列**（§3a）——只在激進策略成立；保守策略夜間不逐顆查詢（§3b），命中主機的數值來自快照。

每日執行輸出一行計數：「finding N 筆（其中已抑制 a、已於 PRTG 確認 b、已合併 c、跨日升級 d、長期 Down e 筆）」（N 為合併前總數），
補追加階段另計跨來源佐證筆數——每道降噪各吃掉多少要看得出來。

### 跨來源佐證

同一主機日事件日誌與 PRTG 兩個獨立來源同時示警時，在 PRTG finding **追加時**判定（`PrtgCorroboration`，
兩條追加路徑共用），寫進紀錄既有的 `CorrelationAlerts`／`CorrelationAlertRefs`，走既有的關聯抑制
（`RuleSuppression.TargetType=Correlation`）與「重大」語意。**不把 PRTG 評估搬到分析之前**，失敗隔離與並行不變。

| PatternId | 事件側 | PRTG 側（未抑制） | 風險 |
|---|---|---|---|
| `prtg-storage-corroborated` | 儲存 I/O 訊號（與【儲存連鎖】同一組判定） | hardware 分類的 warning 或 down | 高（重大） |
| `prtg-capacity-corroborated` | `srv` 2013（磁碟空間即將不足） | disk 分類的 warning | 中 |
| `prtg-outage-corroborated` | 非預期關機（Kernel-Power 41／EventLog 6008，同【儲存→當機】判定） | availability 分類的 down 或 flapping | 中 |

- **刻意配對**：磁碟 I/O 錯誤（硬體在壞）配硬體健康 sensor、空間不足配磁碟可用空間 sensor。
  「I/O 錯誤＋空間快滿」是兩件不相干的事，不當雙重確認。
- 事件側與 PRTG 側都只看未抑制的簽章；已於 PRTG 確認的 finding 仍參與。
- 冪等：同 PatternId 已在 `CorrelationAlertRefs` 就不加；模式被抑制時文字進 `SuppressedCorrelationAlerts`（同前綴已在就不重複）、不影響風險。
  抑制取消後重跑，佐證補進關聯告警並從已抑制清單移除——「曾被抑制」不等於「已存在」。
- 資料列 `HasCorrelation` 同步更新；風險只升不降，由低升為非低時比照下節標記待補 AI。
- 只在有新 finding 追加時判定——既有紀錄不回溯，重跑同一天若 finding 早已追加也不補判。
- 環境沒有任何 hardware 分類的 sensor 時（內建對照現況見 §2），儲存故障模式自然不命中，不另警告。

### 未回報主機的 PRTG 提示

主機清單的「未回報」主機（判定唯一一份：`HostAdminService.IsSilent`，儀表板計數卡共用）依鏡像標出 PRTG 現況，
只讀鏡像、不打 PRTG，PRTG 未啟用時不算：

| 值 | 判定（該主機 `ok` 對應的全部 device、未暫停 sensor 合併看） | 畫面文字 |
|---|---|---|
| `down` | 有 availability 分類 sensor 時只看它們：任一 Down；沒有 availability 時任一 Down | PRTG：主機失聯 |
| `unknown` | 同上範圍全部 Unknown 或無狀態（含一顆都沒有） | PRTG：無資料 |
| `up` | 其餘 | PRTG：主機在線，問題在日誌取數端 |
| `no-map` | 沒有 `ok` 對應 | 無 PRTG 對應 |

- 措辭只陳述事實：PRTG 的 Up 只證明主機在線，未回報原因可能在 NetIQ、本機代理或主機對應。
- 鏡像結構同步時間超過 2 天（或從未同步）時文字加「（鏡像過期）」。
- 清單只算本頁、sensor 狀態一次查回（device 每批最多 500 個）；儀表板計數卡的提示顯示為
  「沒回報 ≠ 沒問題；其中 N 台 PRTG 顯示失聯」。儀表板數字在整包摘要快取內，PRTG 鏡像寫入不推進版本戳，**最多落後一個快取 TTL**。

### finding 對日風險與 AI 的影響

追加時**單向上調**當日風險等級，判定與事件層的 `ComputeRuleBasedRisk` 同語意：

| finding | 風險 |
|---|---|
| 任一未被抑制且帶 `ElevatesDayRisk`（seed v7 只有 availability 的 down；已於 PRTG 確認與長期 Down 不帶） | 高 |
| 任一未被抑制且嚴重度為 High（含跨日升級後的） | 中 |
| 其餘 | 不改變 |

- **只升不降**：一律取 `RiskLevels.MoreSevere(既有, PRTG 推導)`。PRTG 是輔助訊號、看不到事件層的
  證據，絕不用它壓低既有等級。**AI 回寫也是只升不降**：AI 撿到紀錄的時點與 PRTG 追加可能交錯，
  AI 算出的等級是撿取當下的快照，回寫時同樣取 `MoreSevere(列上, AI)`，列上較高時連依據一起保留。
- 上調時風險依據記為 `prtg:{規則代碼}`，畫面才說得出是哪個訊號拉上去的。
- **風險由低升為非低時標記待補 AI 判讀**（判準同 `HostDayPostProcessor.NeedsBackfill`：
  AI 已設定、未分析、非 `detail_pruned`）。已完成 AI 的紀錄只升風險不重標——重標會讓已定案的
  內容被無謂重跑。深析報告由 AI 補寫時依既有機制重建，不另做。
- **既有紀錄不回溯**：升級後既有紀錄維持原風險，下次追加（重跑或次日）才生效。一次性回填會讓
  歷史風險日突然變多，管理者無從分辨是新問題還是舊資料被重算。
- AI prompt 另有獨立的【PRTG 監控訊號】區塊，**只餵已判定、未抑制的 finding、不餵原始數值**，每列「[嚴重度] 規則描述：Detail」
  （原始數值的解讀屬於特徵計算層，見 `docs/BACKLOG.md`；AI 只把已確定的結論翻成白話）。
  finding 不混進事件清單——它們的 `EventId` 恆為 0，混在一起會被當成一筆讀不出意義的事件。
  體檢另有獨立的 PRTG 段，見 docs/DETECTION-SPEC.md 體檢節。

### 追加時機與 finding 登錄簿

規則評估算完後把「哪台主機命中哪些 finding」發佈到一趟執行內共享的登錄簿
（`PrtgFindingsRegistry`），追加因此分兩條路：

| 主機當日紀錄落地的時點 | 誰追加 |
|---|---|
| 規則評估**之前**已落地 | PRTG 路徑在發佈後掃一次補追加 |
| 規則評估**之後**才落地 | 兩條分析寫入路徑（本機／NetIQ）在紀錄剛寫完時就地併入 |

- **登錄簿按日期保存，追加時比對**：兩條寫入路徑都在**逐日迴圈**裡呼叫；PRTG 逐日評估並逐日發佈，某日沒有發佈時該日一律拿到空清單。
  少了這道比對，回補多天缺漏日時每一天都會被掛上同一批 finding，且 `EventKey`
  （`prtg:{規則代碼}:{objid}`）不含日期、去重完全生效，重跑也不會自癒。
- **發佈時一併帶上每台主機的關聯抑制集合**（`SuppressedPatternIdsFor`），兩條追加路徑做跨來源佐證時用同一份，記憶體與資料庫才不會分岔。
- **資料庫端不寫時記憶體端也不改**：查無該主機當日列、或詳情已被保留期精簡時，
  資料庫整段早退——記憶體那份跟著早退，否則呼叫端用來組執行摘要的風險等級會與資料庫分岔。

- 就地追加**必須排在問題案件掛接與執行摘要之前**：案件掛接吃的是記憶體裡的 `TopIssues`、
  摘要吃的是 `RiskLevel`，晚一步併入的 finding 就永遠進不了問題案件與處理狀態鏈。
  因此就地追加同時寫記憶體與資料庫。補追加那條路的紀錄早在掛接跑完之後才被追加，
  它自己補呼叫一次案件掛接（`AttachNewDay` 冪等）。
- 兩條路都依 `EventKey` 去重，重跑同一天不產生重複；重跑模式覆寫紀錄後，寫入路徑會再走一次，
  finding 自然重新追加。**兩條路對同一個主機日在行程內序列化**（登錄簿的 `AttachExclusive`），
  否則與分析並行時兩個交易會各自讀到相同的既有鍵、各插一列。登錄簿另記住「已追加」的主機日：
  補追加先到時，寫入路徑的資料庫呼叫會因去重回 false，但記憶體那份仍要併入，執行摘要才與資料庫一致。
- **發佈本身是給 AI 分析排程看的旗標**（走 `IRunProgress` 的 `prtg-findings-ready` 訊號，
  不是進度，Web 端必須顯式分支且排在 `prtg-` 前綴分支之前，否則會把 PRTG 進度軌蓋成 0/0）。
  AI 排程據此判斷「待補現在可不可以判讀」，不必等整趟取數結束。回望多日時**全部日期發佈完才送一次**。
  逐日處理前另送 `prtg-date-range`（同樣不是進度：`done`＝本趟天數、`total`＝目前第幾天，0＝尚未開始逐日），
  Web 端據此把本趟日期範圍內的待補一律擋到就緒為止，並在 PRTG 進度軌顯示「第 i／N 天」。
- **PRTG 停用、初始化失敗、規則評估失敗、規則庫尚無 PRTG 規則一律發佈空集合**——
  「算不出東西」與「還沒算完」必須分得出來，不發佈的話 AI 會一路等到整趟取數結束，
  等於這個機制沒做。這道保底放在 PRTG 路徑的 `finally`，涵蓋所有提前返回與例外路徑。

## 10. 資料搬運（匯出／匯入）

值型規則（磁碟趨勢、基線偏移等）要用**真實累積的數值**設計與驗證，但數值累積在正式機
（SQL Server）、規則開發在開發機（SQLite），兩個後端無法直接搬 DB 檔。
PRTG 維護頁因此提供跨後端的資料通道：

- **匯出**：選日期區間（上限 366 天），產出單一自描述 JSON 檔下載。
  結構表（devices／sensors／人工對應）全量，時序表（狀態變更／數值／按日對應）依區間篩選。
  sensors 鏡像只含匯出端取數範圍內的；匯入端下一次結構同步會依自己的取數範圍清掉範圍外的列（§3c）。
- **匯入**：上傳同一個檔案，全部走既有的自然鍵冪等寫入
  （數值依 `(sensor_objid, period_start)`、狀態變更依 `(sensor_objid, changed_at)`、結構表依 objid），
  **重複匯入不產生重複資料**，也**不覆蓋人工指定的 sensor 分類**。
  數值列依鍵**無條件覆蓋**（不看 quality）：匯入的 `ok` 列會蓋掉本機 `sampled` 列（精確值優先，正確），匯入的 `sampled` 列也會蓋掉本機 `ok` 列——搬運方向是正式機→開發機，後者在實務上不會發生。
  格式版本不符時拒絕匯入並說明支援版本。
- 匯出與匯入都寫稽核。不做壓縮與增量匯出（等實際檔案大小出來再議）。

### 值型規則的資料取得流程

1. **累積**：數值快照（§3b）持續累積白名單內、有對應主機的全部 sensor 的 `sampled` 列；激進策略另由夜間逐顆查詢寫入觸發主機的 `ok` 列。
   要為特定主機補精確值時，對它跑一次歷史回填（§5，同樣只回填曾為高／中風險的主機）。
   值型規則的基線通常需要 4~8 週資料。
2. **搬運**：正式機匯出目標區間 → 開發機匯入。
3. **分析時的資料品質**：`lf_prtg_values.quality` 的 `ok`／`sampled`／`unknown`／`nodata`
   **不得混為一談**（見 §2 資料品質旗標）——`unknown` 與 `nodata` 的列數值欄為 null，
   代表「這個時段沒有可信資料」，計算基線與趨勢時必須排除，不能當成 0；基線只用可用列（§2）。

## 11. 校準數值匯出

值型規則與幾個保守門檻要靠真實累積的資料來校準。`/admin/calibration`
（頁面規格見 docs/WEB-SPEC.md §9.9f）回答「資料量夠了沒」，夠了就一次匯出。

### 四個校準項與門檻

門檻是程式常數（`CalibrationConstants`），**刻意不開設定**——尚未校準的門檻再開設定是套娃。
校準完成後直接改常數。

| 項 | 什麼算一個 | 可用 | 充足 |
|---|---|---|---|
| PRTG 值型基線 | 一個「有對應主機的白名單 sensor」；某 sensor 某天算涵蓋＝該日**可用列**數 ≥ 12（§2） | ≥10 台主機各涵蓋 ≥28 天 | ≥10 台各 ≥56 天 |
| PRTG 規則門檻 | 一個 sensor-日 | 狀態變更涵蓋 ≥28 天且 **down** 命中 ≥30 筆 | 涵蓋 ≥56 天且 down ≥100 筆 |
| 數值取得量級 | 一天 | 近 30 天內有數值的天數 ≥14（任何品質的列都算） | ≥28 |
| 殘留判定門檻 | 一個含登入失敗明細且未精簡的主機日 | ≥200 主機日且涵蓋 ≥14 天 | ≥1000 且 ≥28 天 |

判定規則：

- **主機的涵蓋天數＝其名下 sensor 涵蓋天數的最大值**——只要有一個 sensor 累積夠久，
  這台主機就有基線可用。
- sensor 白名單留空＝不限制，與取數端 `GetValueFetchTargets` 同一語意。
- **分母為零一律判「不足」**，不得判為可用。期間內完全沒有 Down 事件、沒有任何 sensor
  有數值時，門檻無從校準，維持預設值。
- PRTG 未啟用或鏡像無 sensor 時判「無法取得」，與「不足」區分開——前者是還沒開始，
  後者是開始了但量不夠。
- **門檻校準逐規則代碼進行**：判定看的是 down 的 sensor-日數而非四個代碼加總——
  「down 只有 3 筆但 flapping 有 100 筆」的環境，down 的門檻仍然無從校準。
  四個代碼各自的 sensor-日數都列在累積量指標裡。
- 補充說明一律帶實際數字（目前值與所需值、預估還需幾天），並會在
  `PrtgRetentionDays` 小於充足所需天數時提醒：資料會在累積足夠前被清掉。
  有可用的 `sampled` 列時多一句「可用小時中有 N 小時為快照取樣值」。
- 三個數值查詢（涵蓋摘要、每日聚合、每日量級）的可用判定**內嵌在 EF 查詢投影裡**、同一個運算式，計數拆成 `OkCount`／`SampledCount`（僅計可用的 sampled）／`UsableCount`／`UnknownCount`／`NodataCount`／`OtherCount`（含 coverage 不足的 sampled、paused、untrusted）。抽成 C# 方法會讓 EF 無法翻譯或退到用戶端評估。
- 門檻字典的鍵為 `MinDailyUsableHours`（值 12）。
- 值型基線卡另列：快照目標 sensor 數（`SnapshotTargets`）、近 24 小時有取樣的 sensor 數與平均 coverage（`SnapshotSensors24h`／`SnapshotCoverage24h`，含 coverage 不足的取樣列——回答「快照有沒有在跑」而非「可不可用」）、逐日列數（`ValueBaselineRows`，預告完整匯出大小）。
- 數值取得量級卡的比例：`UsableRatio`（可用列 ÷ 全部）、`SampledRatio`（可用 sampled ÷ 全部）、`OkRatio`（PRTG 真平均 ÷ 全部）。
- 判定結果在行程內快取 10 分鐘（累積量以「天」為單位變動，十分鐘內不會有不同結論）；
  「重新計算」按鈕強制重算，匯出則沿用快取，同一次匯出不必把整組查詢跑兩遍。

### 匯出檔

自描述 JSON（UTF-8 無 BOM，`FormatVersion` 2），含四項判定摘要、四個資料集與三個摘要資料集。匯出的主要讀者是設計值型規則與調門檻的開發端：原始逐日列留給腳本，摘要資料集可直接閱讀。`detail=summary` 只匯出摘要（`ValueBaselines` 為空、檔名加 `-summary`），預設全量。

- **值型基線**（`ValueBaselines`）：per-sensor **每日聚合**（區間 56 天），含平均／最小／最大（當日各可用列 `AvgValue` 的統計）、
  `OkHours`、`UnknownCount`／`NodataCount`，以及 `SampledHours`、`MinObserved`／`MaxObserved`（當日各可用列 `MinValue`／`MaxValue` 的極值，`ok` 列沒有這兩欄時為 null）。數值統計**只納入可用且非 null 的列**（§2）。流量類的 `sampled` 列是估算值（每次掃描量 × 3600 ÷ 掃描間隔）。需要原始 hourly 時走 §10 的資料搬運，不放進校準檔。
- **感測器摘要**（`ValueSensorSummaries`）：值型基線範圍內每顆 sensor 一列——主機、type、單位、是否流量正規化、天數、可用小時、取樣佔比、平均、標準差、P50／P90／P99、最大值。
- **type 輪廓**（`ValueTypeProfiles`）：每種 type 一列——sensor 數、可用小時、每日平均值的 P50／P90／P99／最大值，以及 0～23 時的平均值曲線（24 個數，沒資料的小時為 null）。小時曲線在資料庫端依 `(type, PeriodStart.Hour)` 聚合，SQLite 翻成 `strftime`、SQL Server 翻成 `DATEPART`（兩者以 `ToQueryString` 測試守住）。
- **條件**（`Context`）：匯出模式、取數策略、快照間隔、快照目標數、白名單、`PrtgRetentionDays`、統計視窗起訖、鏡像 device／sensor 總數、可用門檻、統計口徑說明。
- 統計口徑：分位數與標準差以**可用列的每日平均值**為樣本（非每小時值，每顆最多 56 個樣本、有界）；分位數用 nearest-rank，標準差用母體標準差。
- **規則門檻**：三個部分——
  (a) 近 56 天每個規則代碼每日命中數（依 `lf_top_issues` 的 EventKey 前綴分組：該表沒有 rule_id 與分類欄，只能分到代碼）；
  (b) **以最低門檻（Down 1 分鐘／flap 1 次／Warning 1 分鐘）逐日重新評估**得到的每 sensor-日
  magnitude 樣本，以及各規則代碼的 P50／P90／P99 摘要；(c) 規則庫全部 PRTG 規則的門檻現值（`CurrentRules`，帶 `SensorCategory`）。
  (b) 用三條與規則庫無關的合成規則評估（升級後尚未套用 seed、或管理者停用某條時分佈仍算得出來），
  母體與夜間規則評估相同（鏡像中未暫停的 sensor，即取數範圍內，§3c），且**不做同裝置合併**（分佈要看每顆 sensor 自己的量值）；
  規則只對範圍內主機生效，門檻本該由這群 sensor 的分佈決定——全站分佈由上萬顆交換器流量 sensor 主導反而失準；代價是樣本數小很多，判讀「可用」時要看樣本數。
  狀態變更涵蓋摘要同樣只計鏡像中存在的 sensor（保留期內還留著的範圍外舊列不混算）；
  摘要裡的「現值」取不限分類那條規則，分類規則的門檻看 `CurrentRules` 逐條對照。
  (b) 是校準的主要依據——現行門檻下的命中數只回答「照目前設定會報幾次」，要決定門檻該設多少
  必須看底層分佈（有多少 sensor-日的 Down 持續 30 分鐘、多少持續 120 分鐘）。門檻取 1 而不是 0：
  flap 與 warning 的判定是 `>=`，設 0 會讓當日零事件的 sensor 也算命中。
  `silent` 不納入分佈——它的判定來源是 sensor 現況而非變更表，逐日重放只會得到同一個數字複製 N 份。
- **數值取得量級**：近 30 天每日的相異 sensor 數、列數與各品質列數（含 `SampledCount`／`UsableCount`）。
- **殘留判定**：每候選主機日的指標（候選組數、明細總數、前二組集中度、機械型態佔比、
  單組集中度、是否截斷、是否命中），上限最近 5000 個主機日。
  **不含任何帳號名稱**——校準只需要統計形狀。指標計算與正式判定**共用同一份實作**
  （`ResidualCredentialDetector.EvaluateMetrics`，Core 內部方法），兩邊分岔會讓匯出的數字與實際判定不一致。

匯出閘門：四項全部達「可用」以上才解鎖，未達標需勾「仍要匯出」覆寫，兩者都寫稽核。

## 12. 資源守門

夜間批次會同時對 NetIQ 與 PRTG 大量查詢。PRTG 本來就在監控這些主機，因此**資源數據從 PRTG 拿**：
執行期間定期讀受監看 sensor 的即時值，資源緊張就暫停取數，等回落再繼續。
設定見 §7（八個 `PrtgResourceGuard*` 鍵），預設**關閉**。

### 受監看的 sensor 怎麼決定

`PrtgResourceGuardSensorObjids` 非空時直接用它（覆寫優先，完全不做偵測）；留空時自動偵測：

1. 目標位址＝每台 `Sentinel.BaseUrl` 的 host ＋ `PrtgUrl` 的 host。
2. 找 `Ip` 與位址相同的 device。**比對鍵有三段、依序命中即停，一段都不能少**：
   1. 來源位址解析成 IP（來源只有幾筆，名稱走 DNS），device 側**只做 §4 純語法層**後比 IP。
      **少了 DNS 幾乎必然全數落空**：Sentinel 慣以 DNS 名稱設定、PRTG device 慣填 IPv4。
   2. 對不到 → **主機名稱字面比對**（兩邊去 scheme／port 後相等）。device 的 `Ip` 也可能填 DNS 名稱，
      而內網名稱未必進得了 DNS，兩邊填同一個名稱時仍應命中；少了這段，內網名稱環境會整組失效。
   3. 仍對不到 → device 側才做 DNS，只對通過 `IsDnsCandidate` 的名稱型 device，且**整趟預算 20 次**
      （常數 `DeviceDnsBudget.Limit`，跨全部來源位址共用；同名只扣一次；純 IP 與亂值不扣）。
      用盡時輸出「裝置側 DNS 解析已達上限 20 次，其餘 N 台名稱型裝置未解析」並建議改以 IP 設定或用覆寫清單。
      這是保險絲不是閘門：偵測跑在 HTTP 請求執行緒與每趟批次上，幾百台名稱型 device 每台付一次逾時就是數分鐘。
      代價是預算之外的名稱型 device 即使解析後會命中也會被漏掉——有警告可循，而且多數環境在前兩段就命中。
3. PRTG 主機另有 fallback：位址對不到 device、或 `PrtgUrl` 根本解析不出 host 時，改找底下有 `corehealth` type sensor 的 device
   ——PRTG 的 Core Health sensor 只掛在 core server 自己身上。
4. 取命中 device 底下**未暫停**且 `category` 為 `cpu`／`memory` 的 sensor；PRTG 主機另加 corehealth。
   分類來自自動分類（§2），**`PrtgSensorTypeCategoryOverrides` 改了會連帶改變這裡的偵測結果**。

**一個 sensor 都找不到時回空清單並警告，不擲例外。**

### 裝置與感測器從哪裡來

判定邏輯（位址比對、corehealth fallback、cpu／memory 篩選）**只有一份**，
資料來源抽象成 `IPrtgResourceGuardSource`，兩個實作：

| 來源 | 誰用 | 為什麼 |
|---|---|---|
| 鏡像表 | **夜間批次**（`PrtgResourceGuard.TryCreate`） | 每趟批次啟動時跑，不能為了偵測多打一輪 PRTG |
| 直接查 PRTG | 維護／設定頁的「預覽」與「自動偵測並填入」 | 鏡像為空時讀鏡像必然一無所獲，而那正是最需要這顆按鈕的時候 |

鏡像的感測器只含取數範圍內的裝置（§3c），守門比對到的裝置、覆寫清單感測器所在裝置與 corehealth 所在裝置一律在範圍內，夜間讀鏡像不受縮圈影響。**全新安裝**且 PRTG 位址對不到裝置時，corehealth 所在裝置不在第一趟的範圍內，夜間偵測找不到它——在維護頁以直接查 PRTG 的來源偵測一次並存入覆寫清單；
快照服務的範圍補抓會查出這些感測器的所在裝置並補進鏡像（§3c），之後它們就在範圍內。

即時來源**單次取回裝置與感測器全量再於記憶體過濾**（不分頁），不用 `filter_parentid` 逐裝置查詢：
偵測要先比對完位址才知道命中哪些裝置，逐裝置查會變成 N 次往返；全量兩次往返反而少。
不分頁是因為這條路徑跑在 HTTP 請求執行緒上——數萬個感測器翻上百頁的逾時風險，
比一次取回的記憶體成本實際得多（每筆只有四個欄位）。

**取不齊一定要出聲**：實際筆數少於 `treesize`（或無 `treesize` 而剛好取到單次上限 50000 筆）時，
回應的 `warnings` 會寫出「只取到 N 個／總數 M」並指路到覆寫清單。靜默截斷的症狀是
「未偵測到任何受監看的感測器」，與「PRTG 上真的沒有」一模一樣，查不出原因。
單次上限擋不住的規模只能改用**覆寫清單直接指定 objid**，那不是分頁能解的問題。

**覆寫清單的內容要定期複查**：清單是「自動偵測並填入」當下的快照，感測器增減後不會自己更新；
偵測若在當時被截斷，存下來的也是不完整的清單，而畫面上與完整的清單長得一模一樣。
感測器數量成長過、或看到上面的截斷警告之後，重按一次「自動偵測並填入」再儲存。

preview 端點在「使用者按了自動偵測」或「鏡像裡一台裝置都沒有」時走即時查詢；
查不通（連不上、認證錯、逾時、PRTG 回空白 HTML 頁）就**退回鏡像並在回應標明 `mirror-fallback` 與原因**——
靜默退回會讓使用者以為「PRTG 上真的沒有這些裝置」。

夜間批次在鏡像為空的那一晚偵測必然落空。解法是先在畫面按「自動偵測並填入」
把 objid 存進覆寫清單（覆寫優先於偵測），或先跑一次「同步結構與對應」（§5a）把鏡像補齊。

### 判定方向（最容易寫反的地方）

| 分類 | 超標條件 |
|---|---|
| `cpu` | 值 **≥** `PrtgResourceGuardCpuPercent` |
| `memory` | 值 **≤** `PrtgResourceGuardMemoryFreePercent`（PRTG 記憶體 sensor 主通道多為**可用**百分比） |
| `corehealth` | 值 **≤** 同上門檻（健康度越低越糟） |

CPU 越高越糟、記憶體與健康度越低越糟，**方向相反**。設定頁的欄位標籤因此明寫「可用記憶體」，
寫成「使用率」會讓管理者把門檻設反。判定方向有專屬測試釘住。

**忽略而非判定超標**（四種，呼叫端可區分）：sensor 狀態非 Up（走 `PrtgSensorStatuses`，不自行比對字串）、
值不是百分比、objid 查無此 sensor、分類不在 cpu／memory／corehealth 之內。

### 閘門

每趟執行一個實例，插在**兩處**：NetIQ 每批查詢之前、PRTG 每個 `historicdata` 請求之前
（每日取數與觸發式取數共用同一個方法，一處即涵蓋）。
**本機分析不插**——它只讀本機事件與資料庫，暫停它沒有意義。
**歷史回填與環境探測不受守門**（離峰手動作業）；**數值快照也不受守門**（§3b，不在夜間批次內且同時只有一個請求）。

- 距上次檢查未滿 `CheckSeconds` 直接放行；讀值後未超標歸零計數。
- 連續達 `Strikes` 次才進入暫停，等 `PauseMinutes` 後重檢，直到不超標才放行。
- **單趟累計暫停超過 `MaxPauseMinutes` 後放行並警告，該趟之後不再暫停**。
  少了這道上限，門檻設錯會讓整晚只暫停、什麼都沒分析，而且**每晚重演**。
- 併發呼叫時只有一個呼叫者真的去讀值，其餘跟著等後直接放行，不會每條路各打一次 API。
- **取消訊號穿透暫停**；**讀值失敗、對不到 sensor、API 掛掉一律放行**，每趟只警告一次
  ——守門絕不能反過來把排程卡死。

### 可觀測性

進入暫停寫一則 Milestone（含觸發 sensor、實際值、第幾次檢查、預計等待），離開暫停再寫一則（原因與第幾次檢查），執行詳情看得到。
同一段文字也經 `IRunConsole` 進狀態卡的「最新訊息」列，所以暫停中的畫面同時有短徽章與含 sensor／數值的說明。
排程作業頁狀態卡另顯示「資源緊張，暫停中」徽章：閘門經 `IRunProgress` 送 `guard-paused`／
`guard-resumed` 兩個 phase，Web 端據此設 `SchedulerRunState.PausedReason`。
**這兩個 phase 在 `ReportProgress` 內必須有顯式分支且排在 catch-all 之前**
——最後一個分支是 NetIQ 主組的 catch-all，落進去會蓋掉 NetIQ 的進度條。

徽章只有短文字：`IRunProgress.Report` 只帶得動 `(phase, done, total)`，
詳細數值走 Milestone，**刻意不為此擴充該介面**。

### 啟用前先預覽

`GET settings/prtg-resource-guard/preview` 回受監看 sensor 清單與**當下的值**，
並標明來源是覆寫清單還是自動偵測。維護頁另有「自動偵測並填入」按鈕，帶 `forceAuto=true`
**忽略覆寫清單、強制重跑偵測**並把結果寫回輸入框——管理者最常見的操作是「已經手填了一些
objid，想重抓一次」，不忽略覆寫的話只會把手填值原樣吐回來。偵測結果為空時**不清空輸入框**
（手填的清單比一次失敗的偵測可信），填入後仍需按儲存才生效。它**不要求 `PrtgEnabled` 也不要求守門開關**
——用途正是在啟用之前確認偵測結果與數值語意（尤其記憶體 sensor 到底回可用還是使用百分比）。
