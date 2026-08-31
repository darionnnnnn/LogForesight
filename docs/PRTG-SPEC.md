# PRTG 整合規格

PRTG 是環境內既有的監控系統，提供**連續的數值時序**（sensor 每隔數分鐘量測一次）。
LogForesight 把它鏡像到本地資料庫，作為 NetIQ 離散事件之外的第二種訊號來源。

本文件描述**現況**：資料表、擷取、主機對應、設定與操作介面。分析層（特徵計算、弱訊號、
訊號合成、敘述化）尚未實作，見 `docs/BACKLOG.md`。

## 1. 定位與邊界

- **主檔以 NetIQ 為準**：主機身分永遠來自既有主機主檔（`lf_blobs` 的 `hosts`）。
  PRTG device 靠 IP 對應過去；**對不到就跳過，不猜測、不自動建立主機**。
- **PRTG 不取代 NetIQ**：兩者各自取數、各自失敗隔離。一台主機有 PRTG 對應時，
  日後的分析會同時看兩種訊號；沒有對應時就只有 NetIQ。這個分岔發生在主機層，
  **不存在「PRTG+NetIQ」的合併規則平台**——規則各自歸屬自己的來源。
- **模組可完整停用**：`PrtgEnabled` 預設關閉，關閉時夜間擷取與歷史回填完全短路、不建立任何連線。
  唯一的例外是環境探測（見 §6）——它的用途正是在啟用之前先摸清環境，因此只要求位址與 token。

## 2. 資料表（`lf_prtg_*`）

五張表，全部遵守 `docs/DB-SPEC.md` 的命名與可移植規範（`lf_` 前綴、小寫 snake_case、
識別字 ≤30 字元、不使用 SQL schema 前綴）。建表走 `EnsureCreated`（新 DB）＋
`SchemaUpgrader` 的冪等 DDL（既有 DB），兩邊都要維護。

| 表 | 內容 | 鍵 | 保留期 |
|---|---|---|---|
| `lf_prtg_devices` | device 結構鏡像 | `objid`（PRTG 給的 id，非自增） | 不清（永遠是現況全量） |
| `lf_prtg_sensors` | sensor 結構鏡像 | `objid` | 不清 |
| `lf_prtg_state_changes` | 狀態變更與訊息 | 自增 `id`，去重依 `(sensor_objid, changed_at)` | `PrtgRetentionDays` |
| `lf_prtg_values` | hourly 聚合數值 | 自增 `id`，唯一索引 `(sensor_objid, period_start)` | `PrtgRetentionDays` |
| `lf_prtg_host_map` | 主機對應（按日） | 複合主鍵 `(map_date, device_objid)` | `PrtgRetentionDays` |

- **時間一律存本地時間**，與 `lf_daily_records.record_date` 等既有欄位同一語意
  （全站無 UTC 欄位；混存會在 UTC+8 造成靜默的跨日偏移）。
- 清理一律依 `created_at`（不是事件時間）並走 `BatchedPrune`，理由同 `lf_reports`：
  重跑舊日期時依事件時間清會讓剛補出來的資料立刻消失。
- `lf_prtg_sensors.category` / `category_source` 是 sensor 語意分類欄位，**目前一律為 null**。
  欄位先備好，且**每日結構同步絕不覆蓋這兩欄**——日後人工指定的分類不能被同步洗掉。

### 資料品質旗標（`PrtgDataQuality`）

`lf_prtg_values.quality` 用下列值，**任何統計與基線計算都不得把它們混為一談**：

| 值 | 意義 | 目前是否會被寫入 |
|---|---|---|
| `ok` | 正常量測值 | 是 |
| `unknown` | PRTG 回報 unknown，或 coverage 為 0 | 是 |
| `nodata` | 該時段沒有資料 | 是 |
| `paused` | PRTG 上被暫停 | 否——被暫停的 sensor 整段不抓、不寫任何列 |
| `untrusted` | probe 斷線期間取得，不可信 | 否——常數已定義，尚無判定來源（見 BACKLOG） |

`unknown` 與 `nodata` 的列**仍然寫入**（數值欄為 null）——「這個時段沒有可信資料」
本身就是要保留的事實。

`lf_prtg_state_changes.quality` 欄位同樣存在，但狀態變更沒有可用的品質判定依據，
目前一律寫 `ok`。

## 3. 擷取

每日擷取掛在既有夜間批次（`AnalysisOrchestrator.RunAsync`）內，與本機分析、NetIQ 分析
**並行**為第三條路徑，沿用既有取數排程窗口，不另開排程。

失敗語意比照 NetIQ：內部吞掉例外只記 log 與執行輸出，**PRTG 失敗不會讓整趟分析失敗**；
只有取消訊號會穿透。

每日擷取四個階段各自獨立 try/catch，任一階段失敗其餘照跑（歷史回填只跑階段 3、4，見 §5）：

| 階段 | 端點 | 寫入 |
|---|---|---|
| 1. device 結構 | `table.json?content=devices` | `UpsertDevices`（全量 upsert） |
| 2. sensor 結構 | `table.json?content=sensors` | `UpsertSensors`（全量 upsert，不覆蓋分類欄） |
| 3. 狀態變更 | `table.json?content=messages` | `AppendStateChanges`（只取當日，去重） |
| 4. hourly 數值 | `historicdata.json?avg=3600` | `UpsertValues`（逐 sensor 寫入即釋放） |

- **一律拉 hourly 聚合（`avg=3600`），絕不拉 raw**——raw 查詢是 PRTG API 最昂貴的操作。
- 對 PRTG 的併發上限由 `PrtgFetchConcurrency`（1~3，預設 2）以 semaphore 控制，每日擷取與歷史回填共用同一設定。
- **分頁的停止條件是「未滿一頁」**，不只是「空頁」：PRTG 前面若有會忽略 `start` 參數的
  代理，只靠空頁判定會讓迴圈永遠跑不完、整趟夜間批次無聲卡死。
- 記憶體：逐頁轉換、累積滿 500 筆就寫一次；每批一個新的 `DbContext`（變更追蹤器每批歸零）。
  絕不把整份資料堆在記憶體最後才寫。
- 單一 sensor 的數值擷取失敗（逾時、404、暫時 5xx）只影響它自己，其餘 sensor 照樣落地並回報
  實際寫入筆數；只有「有 sensor 要抓卻一筆都沒抓到」才把該階段計為失敗。
- 數值的時間欄位解析失敗時**會計數並在執行輸出回報筆數**，不靜默略過
  （PRTG 依伺服器地區設定輸出時間字串，格式不符時整段會解析失敗——這種缺口必須被看見）。

## 4. 主機對應

每日擷取完成後執行，把 PRTG device 用 IP 對應到主機主檔，逐日寫入 `lf_prtg_host_map`
（歷史回溯要用當時的對應，所以按日保存；同日重跑就地取代不累積）。

| 情況 | 結果 |
|---|---|
| IP 命中恰好一台活躍主機 | `ok`，填入主機 |
| IP 查無對應主機 | `unmatched`，不填主機（這份清單即監控覆蓋率稽核的基礎） |
| 一個 IP 由多台主機共用 | `conflict`，**沿用既有慣例對應到 HostId 最小者**，Note 列出其他候選 |
| 一個 IP 有多個 PRTG device | `conflict`，**不填主機**——無法判斷哪個 device 代表那台主機，猜了會張冠李戴 |
| device 沒有 IP（用 DNS 名稱或未設） | 不產生對應列，計入「略過」 |

已停用（`Active = false`）與已合併（有 `MergedInto` 墓碑）的主機不參與對應。
IP 比對會去除前後空白且不分大小寫。**對應作業只讀主機主檔，絕不寫回。**

> 註：`WebHost.IpAddress` 原本的定位是「最近已知的查詢線索，程式不拿它做比對」。
> PRTG 對應是這條規則的唯一例外，且只用於 PRTG 側的關聯，不影響主機身分判定。

## 5. 歷史回填

手動觸發的離峰作業（設定頁 PRTG 頁籤），**不掛夜間排程**。從昨天往前回填
`PrtgBackfillDays` 天（預設 30）的數值與狀態變更，**不重跑 device／sensor 結構同步**——
結構鏡像永遠是現況，逐日重跑既是對 PRTG 做 N 次無謂的全量查詢，也會把「最後結構同步時間」
改寫成回填當下。回填時的 sensor 清單改從既有鏡像讀取。

- 逐日進行、單日失敗不中止整趟，最後輸出成功與失敗天數。
- **斷點續傳靠冪等**：所有寫入都有自然鍵去重，中斷後重跑同一區間不會產生重複資料，
  因此不需要額外的水位紀錄。
- **回填不做主機對應**：歷史對應無法重建，硬造出來的是假資料。
- 回填與環境探測**互斥**（兩者會打同一台 PRTG），任一執行中時另一個拒絕啟動。

## 6. 環境探測（probe）

設定頁 PRTG 頁籤的唯讀探測工具，背景執行、前端輪詢狀態。產出這個 PRTG 環境的結構統計：

1. PRTG 版本
2. device 與 sensor 總數
3. **sensor type 分布**（依數量排序，含 unit 樣本，以及累積覆蓋 50/80/90/95% 各需幾個 type）
4. 相依性（dependency）設定的使用比例
5. 群組樹概要
6. **IP 覆蓋概要**：有幾個 device 設了 IPv4、有幾個是 DNS 名稱——直接決定主機對應能對到多少

探測**不檢查 `PrtgEnabled`**（只需要位址與 token）：它的用途正是在啟用模組之前先摸清環境。

探測結果只存在記憶體（不落地），供人工檢視與複製。它的用途是回答「後續分析層該怎麼設計」，
特別是 sensor 語意分類要不要做、以及主機對應的實際覆蓋率。

## 6a. 認證方式

PRTG API 支援兩種認證，由 `PrtgAuthMode` 決定，**建立 client 的唯一入口是
`PrtgClientFactory.Create(settings)`**（憑證解密與「憑證齊不齊」的判定都只在這裡）：

| 模式 | 請求帶的參數 | 適用 |
|---|---|---|
| `token`（預設） | `apitoken=<token>` | 較新版本的 PRTG。可限定唯讀、可單獨撤銷，優先選它 |
| `password` | `username=<u>&passhash=<h>` | 舊版 PRTG 沒有 API token 功能時 |

`password` 模式的流程：client 在第一次實際請求之前，先呼叫
`GET /api/getpasshash.htm?username=&password=` 換取 passhash，**同一個 client 實例只換一次**
（併發請求以 semaphore 收斂），之後所有請求帶的是 passhash。
**密碼只出現在換取 passhash 那一次請求**，不會進入後續任何 URL。

**憑證錯誤會黏住**：帳號或密碼不對時，同一個 client 實例之後不再重打 `getpasshash.htm`，
直接回同一則錯誤。每個 sensor 的數值擷取各自呼叫一次 API，不黏的話密碼打錯就是
「sensor 數 × 登入失敗」，足以觸發 PRTG 端的帳號鎖定。傳輸類失敗（連不上、逾時）不黏，
那種重試是有意義的。

**憑證的寫入依當前認證方式隔離**：切換模式時另一種認證的欄位在畫面上是隱藏的，
送上來的是切換前的殘值；後端只處理當前模式那一組，否則會把另一組憑證靜默清空或覆寫。

不採用 PRTG 也支援的 `password=` 直掛：密碼會出現在每一個請求的 URL，
進 PRTG 的存取 log 與中間設備。

例外訊息保證不含 token、密碼、passhash（原文與 URL 編碼形式都會遮蔽），可直接顯示給操作者。

## 7. 設定（系統管理 > 設定 > PRTG）

全部存於 `SystemSettings`（DB），`appsettings.json` 不涉入。

| 設定 | 預設 | 說明 |
|---|---|---|
| `PrtgEnabled` | false | 模組總開關。關閉時整條路徑短路 |
| `PrtgUrl` | — | PRTG core server 位址（含 scheme）。啟用時必填且須為 http/https |
| `PrtgAuthMode` | `token` | 認證方式：`token`（API token）或 `password`（帳號密碼）。見 §6a |
| `PrtgApiTokenEnc` | — | API token 密文（`CryptoHelper`，AES-256-CBC）。write-only，DTO 只回布林。`token` 模式使用 |
| `PrtgUsername` | — | PRTG 帳號。`password` 模式使用 |
| `PrtgPasswordEnc` | — | PRTG 密碼密文。write-only，DTO 只回布林。`password` 模式使用 |
| `PrtgIgnoreSslErrors` | false | 忽略憑證錯誤。自簽憑證環境的顯式逃生門，啟用時每次建立連線都記 WARN |
| `PrtgTimeoutSeconds` | 60 | 單次請求逾時（5~600） |
| `PrtgFetchConcurrency` | 2 | 對 PRTG 的併發上限（1~3） |
| `PrtgBackfillDays` | 30 | 歷史回填天數（1~365） |
| `PrtgRetentionDays` | 180 | 鏡像資料保留天數（下限、上限與收斂規則見 `docs/DB-SPEC.md` 保留策略） |

token 與密碼的處理都與 SMTP 密碼、AI 金鑰完全對稱：留空＝沿用既有、要清除需另外勾選清除。
啟用 PRTG 時依模式驗證憑證是否齊備——**「新存或既有」皆算有**，否則密碼欄留空（＝沿用）
會被誤判成沒設定，使用者改任何其他設定都會被擋下。
解密一律先 `IsEncrypted` 判斷再 `Decrypt`（對非密文直接解密會擲例外）。

### 操作介面

設定頁 PRTG 頁籤提供：連線設定（含認證方式切換）與**測試連線**、**鏡像狀態**（device／sensor 計數、各類資料的
最新時間點、主機對應摘要與衝突／未對應清單）、**歷史回填**、**環境探測**（預設收合——接上 PRTG 那次會用、之後幾乎不再碰；執行中會自動展開）。

> 「最新資料時間」是從鏡像資料推導的，不等於「最後一次成功同步的時間」：連續數晚擷取到
> 0 筆時這個時間不會變動。要分辨兩者需要獨立的同步紀錄，見 `docs/BACKLOG.md`。

## 8. API 端點

全部在 `/api/admin/settings` 之下，需 `Maintain` 權限：

| 端點 | 用途 |
|---|---|
| `POST prtg-test` | 測試連線（用表單當下的值，token 留空沿用已存） |
| `GET prtg-mirror` | 鏡像狀態與主機對應摘要 |
| `POST prtg-probe/start`、`GET prtg-probe/status` | 環境探測 |
| `POST prtg-backfill/start`、`GET prtg-backfill/status` | 歷史回填 |

連線失敗（含 401、逾時、憑證問題）**不是例外**，回 `Success = false` 讓畫面就地顯示；
只有輸入本身不合法才擲驗證例外。錯誤訊息保證不含 apitoken（原文與 URL 編碼形式都會遮蔽）。
