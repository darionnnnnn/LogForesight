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
  唯一的例外是環境探測（見 §6）——它的用途正是在啟用之前先摸清環境，因此只要求位址與認證資訊。

## 2. 資料表（`lf_prtg_*`）

六張表，全部遵守 `docs/DB-SPEC.md` 的命名與可移植規範（`lf_` 前綴、小寫 snake_case、
識別字 ≤30 字元、不使用 SQL schema 前綴）。建表走 `EnsureCreated`（新 DB）＋
`SchemaUpgrader` 的冪等 DDL（既有 DB），兩邊都要維護。

| 表 | 內容 | 鍵 | 保留期 |
|---|---|---|---|
| `lf_prtg_devices` | device 結構鏡像 | `objid`（PRTG 給的 id，非自增） | 不清（永遠是現況全量） |
| `lf_prtg_sensors` | sensor 結構鏡像 | `objid` | 不清 |
| `lf_prtg_state_changes` | 狀態變更與訊息 | 自增 `id`，去重依 `(sensor_objid, changed_at)` | `PrtgRetentionDays` |
| `lf_prtg_values` | hourly 聚合數值 | 自增 `id`，唯一索引 `(sensor_objid, period_start)` | `PrtgRetentionDays` |
| `lf_prtg_host_map` | 主機對應（按日，自動重算） | 複合主鍵 `(map_date, device_objid)` | `PrtgRetentionDays` |
| `lf_prtg_manual_map` | 人工主機對應（長期有效） | `device_objid` | **不清**（人工結果不隨保留期消失） |

- **時間一律存本地時間**，與 `lf_daily_records.record_date` 等既有欄位同一語意
  （全站無 UTC 欄位；混存會在 UTC+8 造成靜默的跨日偏移）。
- 清理一律依 `created_at`（不是事件時間）並走 `BatchedPrune`，理由同 `lf_reports`：
  重跑舊日期時依事件時間清會讓剛補出來的資料立刻消失。
- `lf_prtg_sensors.category` / `category_source` 是 sensor 語意分類欄位。
  每日結構同步後會依 type 對照表**自動填入**未分類者（`category_source` 為 `auto`，
  分類值見 `PrtgSensorCategories`：traffic／disk／cpu／memory）；對照表沒有的 type 維持 null。
  **自動填入只填 null，絕不覆蓋既有值**，且**每日結構同步本身絕不寫這兩欄**——
  人工指定的分類不能被同步或自動分類洗掉。

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

PRTG 路徑的執行順序是：**結構與狀態變更同步 → 主機對應（§4）→ 規則評估（§9）→
觸發式數值取數 → finding 追加**。順序不可任意調動，理由見各節；
其中 finding 追加必須排在觸發式取數之後，因為取數只在分析全部完成後才返回，
那時當日分析紀錄才存在（早於此追加會讓 finding 全數落空）。

每日擷取的階段各自獨立 try/catch，任一階段失敗其餘照跑（歷史回填只跑階段 3、4，見 §5）：

| 階段 | 端點 | 寫入 |
|---|---|---|
| 1. device 結構 | `table.json?content=devices` | `UpsertDevices`（全量 upsert） |
| 2. sensor 結構 | `table.json?content=sensors` | `UpsertSensors`（全量 upsert，不覆蓋分類欄） |
| 3. 狀態變更 | `table.json?content=messages` | `AppendStateChanges`（只取當日，去重） |
| 4. hourly 數值 | `historicdata.json?avg=3600` | `UpsertValues`（逐 sensor 寫入即釋放）。**觸發式，非全量**，見下 |

### 3a. 觸發式數值取數

實機環境有 42,393 個 sensor，逐一擷取一晚跑不完，且對「從沒出過問題的主機」取數沒有分析價值。
因此**數值只對觸發主機取**，三重過濾：

1. **觸發主機** ＝ 當日 `lf_daily_records.risk_level` 為高或中的主機
   ∪ PRTG 規則命中的主機（§9）；
2. 經**當日** `lf_prtg_host_map` 反查 device——**只取 `ok`**，
   `conflict` 歸屬不確定，納入會把數值掛到錯的主機上；
3. device 上 type 命中 `PrtgSensorTypeWhitelist`（§7）且未暫停的 sensor。

**與分析並行、採輪詢**：NetIQ pipeline 內部是巢狀並行迴圈，沒有「單一主機完成」的掛載點，
硬插回呼要動並行迴圈本體。改由 PRTG 路徑**定期查詢已落地的分析結果**，把新出現的問題主機
拿去取數——分析每寫完一台紀錄就已在資料庫裡，效果同樣是「邊分析邊抓」，且完全不動 NetIQ pipeline。
分析結束後**再掃一次**：統計段先寫入紀錄、AI 段可能事後上調風險，只靠過程中的輪詢會漏掉這些主機；
已抓過的主機靠去重集合不重抓。

無觸發主機時整段短路為 0 次 `historicdata` 呼叫，執行輸出會寫明觸發主機數、目標 sensor 數與寫入筆數。

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

已停用（`Active = false`）與已合併（有 `MergedInto` 墓碑）的主機不參與對應。
IP 比對會去除前後空白且不分大小寫。**對應作業只讀主機主檔，絕不寫回。**

> 註：`WebHost.IpAddress` 原本的定位是「最近已知的查詢線索，程式不拿它做比對」。
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

## 5. 歷史回填

手動觸發的離峰作業（**排程作業頁**），**不掛夜間排程**。從昨天往前回填
`PrtgBackfillDays` 天（預設 30）的數值與狀態變更，**不重跑 device／sensor 結構同步**——
結構鏡像永遠是現況，逐日重跑既是對 PRTG 做 N 次無謂的全量查詢，也會把「最後結構同步時間」
改寫成回填當下。回填時的 sensor 清單改從既有鏡像讀取。

- 逐日進行、單日失敗不中止整趟，最後輸出成功與失敗天數。
- **斷點續傳靠冪等**：所有寫入都有自然鍵去重，中斷後重跑同一區間不會產生重複資料，
  因此不需要額外的水位紀錄。
- **回填不做主機對應**：歷史對應無法重建，硬造出來的是假資料。
- **回填套用與每日擷取相同的三重過濾**（§3a）：只回填「該日曾為高／中風險」的主機、
  經該日（或最近一日）`ok` 對應的 device、且 type 命中白名單的 sensor。
  全量回填在實機是 42,393 sensor × 30 天，跑不完；對從沒出過問題的主機回填也沒有分析價值。
  某日沒有對應資料時取最近一日的對應（只讀既有 map，不重建歷史對應）；仍對不到就跳過該日數值。
- 狀態變更維持全量回填（單次 API 呼叫，成本低）。
- 回填與環境探測**互斥**（兩者會打同一台 PRTG），任一執行中時另一個拒絕啟動。

## 6. 環境探測（probe）

PRTG 維護頁的唯讀探測工具，背景執行、前端輪詢狀態。產出這個 PRTG 環境的結構統計：

1. PRTG 版本
2. device 與 sensor 總數
3. **sensor type 分布**（依數量排序，含 unit 樣本，以及累積覆蓋 50/80/90/95% 各需幾個 type）
4. 相依性（dependency）設定的使用比例
5. 群組樹概要
6. **IP 覆蓋概要**：有幾個 device 設了 IPv4、有幾個是 DNS 名稱——直接決定主機對應能對到多少

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
| `PrtgEnabled` | false | 模組總開關。關閉時整條路徑短路 |
| `PrtgUrl` | — | PRTG core server 位址（含 scheme）。啟用時必填且須為 http/https |
| `PrtgAuthMode` | `token` | 認證方式：`token`／`password`／`passhash`。見 §6a |
| `PrtgApiTokenEnc` | — | API token 密文（`CryptoHelper`，AES-256-CBC）。write-only，DTO 只回布林。`token` 模式使用 |
| `PrtgUsername` | — | PRTG 帳號。`password` 與 `passhash` 兩模式共用 |
| `PrtgPasswordEnc` | — | PRTG 密碼密文。write-only，DTO 只回布林。`password` 模式使用 |
| `PrtgPasshashEnc` | — | PRTG passhash 密文。write-only，DTO 只回布林。`passhash` 模式使用 |
| `PrtgIgnoreSslErrors` | false | 忽略憑證錯誤。自簽憑證環境的顯式逃生門，啟用時每次建立連線都記 WARN |
| `PrtgTimeoutSeconds` | 60 | 單次請求逾時（5~600） |
| `PrtgFetchConcurrency` | 2 | 對 PRTG 的併發上限（1~3） |
| `PrtgBackfillDays` | 30 | 歷史回填天數（1~365） |
| `PrtgRetentionDays` | 180 | 鏡像資料保留天數（下限、上限與收斂規則見 `docs/DB-SPEC.md` 保留策略） |
| `PrtgSensorTypeWhitelist` | 8 種分析型 type | 要擷取數值的 sensor type（一行一個，不分大小寫）。**留空＝不限制**。預設不含 Ping（量大且雜訊高，需要時自行加入） |

token、密碼與 passhash 的處理都與 SMTP 密碼、AI 金鑰完全對稱：留空＝沿用既有、要清除需另外勾選清除。
啟用 PRTG 時依模式驗證憑證是否齊備——**「新存或既有」皆算有**，否則密碼欄留空（＝沿用）
會被誤判成沒設定，使用者改任何其他設定都會被擋下。
解密一律先 `IsEncrypted` 判斷再 `Decrypt`（對非密文直接解密會擲例外）。

### 操作介面

功能依性質分置三頁——**設定頁只放參數，操作類不放在那裡**：

| 頁面 | 內容 |
|---|---|
| **PRTG 維護頁 `/admin/prtg`**（權限 Maintain，側欄「系統管理」內緊鄰 NetIQ） | 連線設定（含三選一認證切換）與**測試連線**、**鏡像狀態**（device／sensor 計數、各類資料最新時間點、白名單覆蓋量級、主機對應摘要與衝突／未對應清單、**人工對應清單與指派入口**）、**環境探測**（預設收合）、**資料搬運**（§10） |
| **排程作業頁** | `PrtgEnabled` 總開關（**切換即存**，走單一用途端點，不與排程表單同一顆儲存）、**歷史回填**操作與狀態 |
| **設定頁 PRTG 頁籤** | 純參數：白名單、併發、回填天數、保留天數、逾時、忽略 SSL。頂端有回連指向上面兩頁 |

`PrtgEnabled` 走 `PUT settings/prtg-enabled` 單一用途端點而不是整包設定更新：
整包更新會在「讀取到送出之間」覆蓋他人在設定頁的改動，也會被與 PRTG 無關的跨欄位驗證擋下。

> **設定頁不再送出的 PRTG 欄位（`PrtgEnabled`／`PrtgUrl`／`PrtgAuthMode`／`PrtgUsername`）
> 在 `UpdateSystemSettingsRequest` 中一律可空，且「有送才更新」**——它們無條件寫入時，
> 設定頁存檔會把 PRTG 位址清空、把模組關掉、或在帳密模式下直接擋下整個存檔。
> 新增任何「只出現在單一頁面的設定欄位」時都要套用同一規則。

> 「最新資料時間」是從鏡像資料推導的，不等於「最後一次成功同步的時間」：連續數晚擷取到
> 0 筆時這個時間不會變動。要分辨兩者需要獨立的同步紀錄，見 `docs/BACKLOG.md`。

## 8. API 端點

全部在 `/api/admin/settings` 之下，需 `Maintain` 權限：

| 端點 | 用途 |
|---|---|
| `POST prtg-test` | 測試連線（用表單當下的值，token／密碼／passhash 留空沿用已存） |
| `GET prtg-mirror` | 鏡像狀態與主機對應摘要 |
| `POST prtg-probe/start`、`GET prtg-probe/status` | 環境探測 |
| `POST prtg-backfill/start`、`GET prtg-backfill/status` | 歷史回填 |
| `PUT prtg-enabled` | PRTG 總開關（排程作業頁，只更新這一個欄位） |
| `GET／PUT／DELETE prtg-manual-map` | 人工主機對應的查詢、指派與移除（§4a） |
| `GET prtg-export`、`POST prtg-import` | 鏡像資料匯出／匯入（§10） |

主機明細的 PRTG 區塊另走 `GET /api/host-detail/{hostId}/prtg`（回該主機對應的 device 與其 sensor）。

連線失敗（含 401、逾時、憑證問題）**不是例外**，回 `Success = false` 讓畫面就地顯示；
只有輸入本身不合法才擲驗證例外。錯誤訊息保證不含 token、密碼與 passhash（原文與 URL 編碼形式都會遮蔽）。

## 9. 規則第一階（狀態變更型）

分析層的第一步，只用**狀態變更**這份既有資料（夜間單次 API 呼叫即取得，成本低），
不需要數值基線。四條規則逐 sensor／device 判定，門檻與啟用狀態存在規則維護頁的 `prtg` 平台：

| 規則代碼 | 語意 | 預設門檻 | 分類／嚴重度 |
|---|---|---|---|
| `down` | sensor 進入 Down 且日終未恢復 | 持續 ≥ 60 分鐘 | Service／High（提升日風險） |
| `flapping` | 一日內 Down↔Up 反覆 | ≥ 5 次往返 | Service／Medium |
| `warning` | Warning 狀態累計 | ≥ 4 小時 | Resource／Medium |
| `silent` | device 底下全部未暫停 sensor 皆 Unknown 或無狀態 | 整日 | Service／Medium |

判定細節（皆為踩過的坑，改動前先讀）：

- **狀態字串是 PRTG 原值、未正規化**，會出現 `Down (Acknowledged)`／`Down (Partial)` 等變體，
  因此一律**前綴比對且不分大小寫**，判定收斂在 `PrtgSensorStatuses` 的四個方法。
- **`prev_status` 恆為 null 不可依賴**：持續時長與往返次數只能靠同一 sensor 依時間排序的相鄰列推導。
- 每日擷取只保留當天的狀態變更，因此 **`down` 與 `warning` 都必須回查前一日的最後一筆**
  取得當日零時的起始狀態；持續時間**自當日零時起算**（不是從前一日進入的時點）。
  `warning` 少了這道回看時，「前一日進入 Warning、當日整天沒有變更」這個最典型的情境永遠不會命中。
- `silent` 的判定來源是 `lf_prtg_sensors.status`（結構同步的現況值），**不是狀態變更表**——
  健康的 sensor 本來就整天零筆變更，用變更表判定會讓全機房每天都被判為沉默。
  device 底下沒有未暫停 sensor 時不算沉默。
- 規則只看**白名單內**的 sensor（§7），與數值取數同一個母體。

### finding 如何進入既有全鏈

命中的 finding 映射成問題簽章寫入當日該主機的 `lf_top_issues`，
處理狀態、問題排行、郵件通知因此自動涵蓋它，**不另建 finding 表與獨立 UI**：

- `LogName` / `Source` 皆為 `PRTG`、`EventId = 0`、`EventKey = prtg:{規則代碼}:{objid}`
  （同 Linux 規則的「EventId=0 ＋ EventKey」模式）。
- **`EventKey` 不得含 `|`**：處理狀態鍵 `IssueSignatureKey` 以 `|` 分段解析，
  含 `|` 會讓 finding 靜默脫離處理狀態鏈。長度上限 255。
- 分類與嚴重度由 PRTG 專屬對照表給定，**不走 `KnownIssueCatalog` 的 Classify**——
  它只認 windows／linux 平台，PRTG 走它會讓 `EventKey` 被清空、finding 降成 Other。
- **只對「當天已有分析紀錄」的主機追加**：硬造一筆只有 PRTG finding 的紀錄會讓
  「未回報主機」「覆蓋缺口」等既有統計失真。沒被分析涵蓋的主機，其 finding 留待隔日。
- 詳情已被保留期精簡（`detail_pruned`）的紀錄不追加，避免把精簡後的殘骸寫回。
- 追加依 `EventKey` 去重，同一天重跑不產生重複。
- **命中主機會回饋觸發式取數的佇列**（§3a）——這是「PRTG 規則驅動加強取數」的閉環。

## 10. 資料搬運（匯出／匯入）

值型規則（磁碟趨勢、基線偏移等）要用**真實累積的數值**設計與驗證，但數值累積在正式機
（SQL Server）、規則開發在開發機（SQLite），兩個後端無法直接搬 DB 檔。
PRTG 維護頁因此提供跨後端的資料通道：

- **匯出**：選日期區間（上限 366 天），產出單一自描述 JSON 檔下載。
  結構表（devices／sensors／人工對應）全量，時序表（狀態變更／數值／按日對應）依區間篩選。
- **匯入**：上傳同一個檔案，全部走既有的自然鍵冪等寫入
  （數值依 `(sensor_objid, period_start)`、狀態變更依 `(sensor_objid, changed_at)`、結構表依 objid），
  **重複匯入不產生重複資料**，也**不覆蓋人工指定的 sensor 分類**。
  格式版本不符時拒絕匯入並說明支援版本。
- 匯出與匯入都寫稽核。本輪不做壓縮與增量匯出（等實際檔案大小出來再議）。

### 值型規則的資料取得流程

1. **累積**：觸發式取數（§3a）會自然累積「出過問題的主機」的數值；
   要為特定主機補基線時，對它跑一次歷史回填（§5，同樣只回填曾為高／中風險的主機）。
   值型規則的基線通常需要 4~8 週資料。
2. **搬運**：正式機匯出目標區間 → 開發機匯入。
3. **分析時的資料品質**：`lf_prtg_values.quality` 的 `ok`／`unknown`／`nodata`
   **不得混為一談**（見 §2 資料品質旗標）——`unknown` 與 `nodata` 的列數值欄為 null，
   代表「這個時段沒有可信資料」，計算基線與趨勢時必須排除，不能當成 0。
