# NetIQ Sentinel 取數 API 實作規劃（NETIQ-API-PLAN）

> 2026-07-24 規劃定案候選版，**尚未實作**。
> 範圍：依 NetIQ 原廠文件確認的 Sentinel REST API 事實，落成批次端取數 pipeline
> （`SentinelClient` / `SentinelStatsSource` / `--netiq-probe`，對應 docs/PLAN.md Phase 1–2）
> 與 Web 端 `SentinelRestDirectoryClient` 骨架補完。
> 設計主軸：**盡可能降低 Sentinel server 負擔**（§5 有完整對策清單）。
> 本文件與 docs/PLAN.md「Sentinel 8.5 查詢設計」互補：PLAN.md 定了 Q1~Q4 的高階形式，
> 本文件把「API 怎麼呼叫」落到端點、payload 與類別層級。

## 0. 原廠文件依據

| 文件 | 內容 | 位置 |
|---|---|---|
| Sentinel REST API 參考（隨機安裝） | **8.5 環境的最終權威**：每台 Sentinel 自帶完整 API 文件 | `https://<sentinel>:8443/SentinelRESTServices/apidoc/en/index.html` |
| Sentinel API（Beta）公開文件 | 認證流程、EventSearch/EventSearchStatus 全部操作（7.0 版，端點形狀與 8.x 相同） | https://www.novell.com/developer/plugin-sdk/ref/restapi/7.0/ |
| Search Query Syntax（User Guide） | Lucene 查詢語法、可搜尋欄位 | https://www.microfocus.com/documentation/sentinel/8.6/s86-user/bvg1rjs.html |
| 事件欄位清單 | 各安裝實際欄位以 Sentinel 主介面右上「Tips」頁為準（文件明載） | Sentinel Web UI → Tips |

> 實作前的第一步（probe）就是打開部署環境的 `apidoc` 與 Tips 頁核對本文件——
> 公開文件是 7.0/8.2 版，任何出入以現場 8.5 的 apidoc 為準。

## 1. 原廠 API 事實整理（已由文件確認）

### 1.1 認證：SAML token（不是每次 Basic）

1. **取 token**：`POST https://<sentinel>:8443/SentinelAuthServices/auth/tokens`，
   header 帶 `Authorization: Basic <base64(user:pass)>`，回應 JSON 內含 SAML token。
2. **之後所有呼叫**：header 帶 `Authorization: X-SAML <token>`，**不再送帳密**。
3. **驗證 token**（可選）：`GET /SentinelRESTServices/preauthorize?path=...&httpMethod=GET`
   → `{"Authorized":"true"}`。
4. **登出**：`DELETE /SentinelAuthServices/auth/tokens/<token>`。

含義：token 是 server 端 session 資源。**整輪收集共用一個 token、結束時 DELETE 登出**，
不能每個查詢重新認證（認證是相對昂貴的操作，2000 台量級下每查詢一次認證＝自己 DoS 自己）。
token 過期的表現（401/403）由 client 統一攔截：重新認證一次後重放原請求，仍失敗才報錯。

### 1.2 事件查詢：event-search job 生命週期

Sentinel 的事件查詢是**非同步 search job**，不是同步 query：

| 步驟 | 呼叫 | 說明 |
|---|---|---|
| 建立 | `POST /SentinelRESTServices/objects/event-search`（201 Created，回 `@href`） | body 見下 |
| 查狀態 | `GET /SentinelRESTServices/objects/event-search/{id}`（或 event-search-status） | `status`：0 Pending / 1 Running / 2 Completed / 3 CompletedWithErrors / 4 Unavailable / 5 Canceled / 6 AccessDenied；`found`＝符合總數、`avail`＝目前可取數、`results`＝**第一頁結果的 URL** |
| 取結果 | `GET <results URL>` 逐頁 | 每頁 `pgsize` 筆，跟隨回應中的下一頁連結 |
| 清理 | `DELETE /SentinelRESTServices/objects/event-search/{id}` | **用完即刪**（PLAN.md 既有決策），不留 job 佔用 server 資源 |

建立 job 的 body 欄位（原廠文件欄位名）：

```jsonc
{
  "filter": "(sev:[0 TO 5]) AND (shn:SRV-A)",   // Lucene 語法
  "start": "2026-07-23T16:00:00.000Z",           // 含（inclusive），ISO-8601 UTC
  "end":   "2026-07-24T16:00:00.000Z",           // 不含（exclusive）
  "fields": "…",                                  // 欄位投影：只回需要的欄位
  "pgsize": 500,                                  // 單頁筆數
  "max-results": 100000,                          // 此 job 最多回傳筆數（安全閥）
  "type": "USER"                                  // SYSTEM/USER/REPORT/DATASYNC/DIST
}
```

- `start` 含、`end` 不含——日切界剛好用「當地日 00:00 轉 UTC」到「翌日 00:00 轉 UTC」，
  不會重複也不會漏（`dt` 時區基準仍列 probe #4 實測確認）。
- `type` 用 `USER`；並填 `init-user`/`ip`/`InitiatingHostName` 表明身分——
  Sentinel 管理端的「Active Searches」畫面看得到是誰在查，SIEM 管理者可辨識、可管理。

### 1.3 沒有 GROUP BY——退回方案轉正

公開 REST API **只有 search job，沒有伺服器端聚合**（GROUP BY / facet 不在公開端點中；
`submitGroupEval` 屬內部 Distributed Search 介面，不在支援面上）。因此 PLAN.md
「GROUP BY 經 REST 不可用時的退回方案」**直接轉為正案**：

> Q1 ＝ watchlist Lucene 篩選（server 端先過濾掉 99% 事件）＋欄位投影（每筆只回
> 主機/來源/EventID/dt 四欄）＋分頁拉回**本地計數聚合**。

負擔評估：server 端做的是它本來就最擅長的索引查詢與序列化，聚合的 CPU/記憶體成本
由我方批次主機承擔；傳輸量＝watchlist 命中事件 × 每筆約 100~200 bytes。全機房正常日
估數萬筆（多數主機一天只有零星 watchlist 事件），最壞（多台被暴力破解狂刷 4625）數十萬筆
＝幾十 MB，夜間窗口內可接受。probe #3 仍會實測 8.5 的 apidoc 是否多出聚合端點——有就用，
沒有也不影響本設計成立。

## 2. 與既有規劃/程式的對帳

> **2026-07-24 修正**：本節原依 PLAN.md／SCALE-2000-PLAN 撰寫，假設帳密設定仍以
> `appsettings.NetIq.Servers`（per-server `Username`/`Password`）為事實來源、密碼用
> DPAPI 保護。**這個前提已被 docs/NETIQ-WEB-CONFIG-PLAN.md（同日稍晚定案）取代**：
> Sentinel 連線設定現在由 **Web 維護、存 `ISentinelStore`（webdata blob，key=`sentinels`）**，
> 密碼用 **Core 既有的 `CryptoHelper`（AES-256，`enc:v1:` 前綴，金鑰內嵌程式）**加密，
> 不是 DPAPI；`appsettings.NetIq.Servers` 降為「store 為空時的一次性種子」。以下對帳表已更新為現況。

| 項目 | 既有狀態（2026-07-24 確認） | 本規劃處置 |
|---|---|---|
| 帳密事實來源 | **`ISentinelStore`**（DB blob）為主，Web CRUD 維護；`appsettings.NetIq.Servers` 僅一次性種子（`SentinelSeeder`） | `SentinelClient` 的連線資訊一律吃既有 `SentinelServer` 投影物件（`Name`/`BaseUrl`/`Username`/明碼 `Password`）——批次與 Web 都已有現成的「讀 store→解密」程式碼可重用（見 §3.0） |
| 密碼保護 | **`CryptoHelper.Encrypt/Decrypt`（AES-256，`enc:v1:` 前綴）已實作**，非 DPAPI；解密只在讀出時做一次，明碼只留在行程記憶體 | 沿用既有 Helper，**不新增 DPAPI 或 `--protect-netiq-password`**（原規劃此項作廢） |
| `SentinelRestDirectoryClient` | 骨架端點打 `/SecurityManager/rest/hosts`（占位、非 Sentinel 端點）＋Basic auth 直打；已改吃 `INetiqServerCatalog`（內部即 `ISentinelStore`）取得含明碼密碼的 `SentinelServer` | 整段改寫：連線資訊來源不變，只改「怎麼打 API」——走 §1.1 SAML 認證＋§4.4 的探索查詢 |
| `NetIqSettings` 節流欄位 | 刻意未加（「有設定無行為」紅線） | 本次連同行為一起加：`QueryDelayMs`/`PageSize`/`TimeoutSeconds`/`RetryCount`/`MaxResultsPerJob`/`AllowInvalidCertificates`（這些是**查詢行為**設定，與帳密事實來源無關，仍留在 `AppSettings.NetIq`，批次與 Web 各自從自己的 appsettings 讀） |
| Q4 頻道覆蓋 | PLAN.md 降為每週 | 不變，實作照每週 |

### 2.1 連線資訊怎麼取得（batch／Web 各自的既有管道）

- **批次**：`StorageFactory.CreateSentinelStore(settings.Storage, dataRoot)` 取得 `ISentinelStore`，
  逐筆 `CryptoHelper.IsEncrypted(s.PasswordEnc) ? CryptoHelper.Decrypt(s.PasswordEnc) : s.PasswordEnc`
  解密後組 `SentinelServer`——與 Web 端 `NetiqServerCatalog.ToProjection` 是同一段邏輯，
  重複兩份是因為批次（console exe）與 Web 是不同的部署單元、沒有共用 DI 容器，
  這段幾行的投影邏輯不值得為此新增一個跨專案的介面。
- **Web**：既有 `INetiqServerCatalog.GetServer(name)` 直接回傳解密好的 `SentinelServer`。
- 兩邊拿到的都是同一個 `SentinelServer` 類別（`LogForesight.Core/Configuration/AppSettings.cs`），
  `SentinelClient` 的建構子只認這個型別，不關心密碼從哪個 store 解出來的。

## 3. 元件設計

### 3.1 `SentinelClient`（新，LogForesight.Core/Service 層；批次與 Web 共用）

單一職責：**REST 協定封裝**——認證生命週期、search job 生命週期、分頁、重試、節流。
不懂任何業務語意（watchlist、簽章統計都不在這層）。

```csharp
/// <summary>單筆事件的投影結果：欄位名→值（欄位對應交由呼叫端解讀）</summary>
public sealed record SentinelEvent(IReadOnlyDictionary<string, string> Fields);

public sealed record SentinelSearchRequest(
    string LuceneFilter,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    IReadOnlyList<string> Fields,          // 投影欄位；空＝全欄位（僅 probe 用）
    int? MaxResults = null);               // null＝用設定的 MaxResultsPerJob

public interface ISentinelClient : IAsyncDisposable
{
    /// <summary>建立 job→輪詢完成→逐頁串流→DELETE job。IAsyncEnumerable 讓呼叫端
    /// 邊收邊聚合，不整批堆記憶體。job 未達 Completed（status 2）即擲例外。</summary>
    IAsyncEnumerable<SentinelEvent> SearchAsync(SentinelSearchRequest request, CancellationToken ct);

    /// <summary>同 SearchAsync 但只取 found 計數，不拉結果頁（探索前的量級預估用）</summary>
    Task<long> CountAsync(SentinelSearchRequest request, CancellationToken ct);
}
```

> **實作註記（2026-07-24，實際落地與上方草圖的兩處偏差）**：
> 1. `SearchAsync` 回傳**物化的 `SentinelSearchResult`**（`Events` 清單＋`Found`＋`Truncated`
>    截斷旗標），不是草圖的 `IAsyncEnumerable` 串流——目前的呼叫端（probe、之後的 Q2/Q3/Q4
>    小查詢）單次結果都小，物化簡單且 `Truncated` 語意更清楚；Q1 大量取數是否需要
>    改回串流（記憶體考量），留待 `SentinelStatsSource` 實作時依 probe 實測的事件量再定，
>    介面在同一個檔案內、屆時是局部改動。
> 2. `CountAsync` **未實作**——目前沒有任何呼叫端（probe 用 `MaxResults:1` 的 SearchAsync
>    拿 `Found` 即可），依「有介面無使用者就不留」的專案慣例，等 Q1 量級預估真的需要時再加。

內部行為（全部集中在這一層，呼叫端零感知）：

- **token 快取**：首次呼叫才認證；401/403 → 重新認證一次重放；`DisposeAsync` 時 DELETE token 登出。
- **單一併發佇列 per instance**：`SemaphoreSlim(1,1)`（同 `AIService` 慣例）——一台 Sentinel
  同時間只有一個 job 在跑；跨 Sentinel 由呼叫端各建一個 client 實例平行。
- **輪詢**：建立 job 後以 500ms 起步、上限 5 秒的遞增間隔輪詢 status，總逾時 `TimeoutSeconds`。
- **節流**：每個 REST 呼叫之間 `QueryDelayMs`（含輪詢與翻頁）。
- **Polly 重試**：503/逾時/網路錯誤 → 指數退避＋抖動，`RetryCount` 次；4xx 不重試（打錯就是打錯）。
- **清理保證**：`finally` 中 DELETE job——包含取頁中途失敗、呼叫端提前放棄（enumerator dispose）。
- **max-results 安全閥**：job 完成後 `found > 實際取回數` 時回報截斷旗標（呼叫端據此標 DataIncomplete）。
- **憑證**：Sentinel 常見自簽憑證。預設嚴格驗證；`AllowInvalidCertificates: true` 為顯式逃生門
  （啟用時 log WARN），不做靜默放行。
- **log 紅線**：密碼與 token 永不落 log；診斷 log 記「端點＋filter 長度＋耗時＋found」摘要。

### 3.2 `SentinelStatsSource`（新，批次；實作 `IDailyStatsSource`）

業務層：把 Q1~Q4 組裝成 `DailySignatureStats`（與 `LocalStatsSource` 同一輸出模型，
下游五層偵測零改變——PLAN.md 抽象層「日統計」定案的兌現）。

- **Q1 主聚合**：per-Sentinel、per-日。filter＝`(watchlist Lucene) AND (IP 清單批次)`；
  fields＝主機、來源、EventID、dt 四欄。IP 清單分批（預設 50 台/批，probe #8 實測
  Lucene 子句上限後調整；Lucene 預設 maxClauseCount 1024，50 台遠低於限）。
  本地以 `(host, source, eventId)` 分組計 count/min(dt)/max(dt) → 簽章統計。
- **Q2 範例訊息**：只對「進 prompt 的簽章」逐一小查詢（filter 鎖單簽章、fields 含 msg、
  `max-results: 3`）。`SampleFetchMode: Reduced` 時僅 Security 與 Other 類簽章查。
- **Q3 風險主機原始 log**：風險日才觸發，單主機小查詢、20 筆預算（沿用既有）。
- **Q4 頻道覆蓋**：每週一次，per-Sentinel 全清單 IP 查近 24h、fields＝主機＋頻道，
  本地 distinct → 未收 Security 頻道主機清單（覆蓋率誠實申報）。
- **失敗隔離**（PLAN.md 既有決策的落點）：單一 IP 批次失敗 → 該批主機當日標
  「查詢失敗、資料不完整」，其他批照常；單台 Sentinel 整台失聯 → 其轄下主機全標、
  機房總覽「來源狀態」列失聯 Sentinel。

### 3.3 欄位對應（probe 定案前的候選）

Windows Event ID 在 Sentinel schema 的落點是 probe #2 的實測項。候選欄位（Tips 頁核對）：
`evt`（事件名）、`msg`（訊息）、`sev`、`dt`、`shn`/`sip`（來源主機/IP）、`sun`/`dun`（帳號）、
`pn`（產品）、`rv40`/`rv25`/`xdasid`/`ei`（外部事件代碼候選——**以現場 Tips 頁與實際事件
樣本為準，不預先寫死**）。對應表落地為 `SentinelFieldMap`（設定可覆寫的字典，per-server
覆寫保留為保險），probe 輸出直接產生此表的草稿。

### 3.4 `SentinelRestDirectoryClient` 補完（Web 探索）

改寫為走同一 `ISentinelClient`：探索＝「近 24h 對該 Sentinel 全事件做主機欄位投影
＋本地 distinct」（等同 Q4 的單次版）。注意：

- 全事件量大，探索**只投影主機名/IP 兩欄**＋`max-results` 上限（如 50 萬）；
  超限代表環境事件量超乎預期，回報「請縮小掃描範圍」而不是硬拉。
- 探索是互動操作：輪詢預算 30 秒（既有 UI 逾時），超時明確報錯（job 照樣 DELETE）。
- Web 端與批次端共用 Core 的 client，但**各自實例**（批次夜間、Web 日間，天然錯開；
  同一 Sentinel 的併發上限仍由各實例的單一佇列保護，最壞 2 併發，可接受）。

### 3.5 `--netiq-probe`（Phase 1 閘門，輸出貼回對話定案）

一鍵對每台設定的 Sentinel 依序執行、輸出成一份可貼回的報告（敏感值遮罩）：

1. 認證：取 token 成功與否、耗時；token 重用第二次呼叫是否有效。
2. 小範圍 event-search（近 1h、`max-results: 20`、全欄位）：傾印 3 筆原始 JSON
   → 定案欄位對應（Windows EventID / 來源 / 主機名 / IP / 訊息 / dt 格式與時區）。
3. apidoc 檢查提示：印出該台 `…/apidoc/en/index.html` 網址，人工確認有無聚合端點。
4. `dt` 界線實測：同一事件以不同 start/end 查詢驗證含/不含語意與時區。
5. 頻道覆蓋：Q4 單次版，列各主機頻道。
6. 分頁：`pgsize` 大小 vs 回應耗時的三點採樣（100/500/1000）；job DELETE 後再 GET 確認 404。
7. IP 篩選批次上限：以 10/50/100 個 IP 子句的 filter 各查一次，找出安全批次大小。
8. 失敗路徑：錯誤密碼認證（預期 401）、非法 filter（預期 400）確認錯誤可辨識。

probe 全程遵守單一佇列＋節流，總量約 15~20 個小查詢，對 server 負擔可忽略。

## 4. 查詢 payload 草案（欄位名以 probe 定案後代入）

```jsonc
// Q1（每 IP 批次一個 job；<EVTID> 等欄位名 probe 後代入）
{
  "filter": "(sip:(10.1.2.11 OR 10.1.2.12 OR …)) AND ((<SRC>:disk AND <EVTID>:(7 OR 11 OR 51 OR 52 OR 153)) OR (<EVTID>:4625) OR …)",
  "start": "<當地日00:00→UTC>", "end": "<翌日00:00→UTC>",
  "fields": "shn,sip,<SRC>,<EVTID>,dt",
  "pgsize": 500, "max-results": 100000, "type": "USER",
  "init-user": "svc-lfquery", "InitiatingHostName": "<批次主機名>"
}

// Q2（單簽章範例，每簽章一次）
{ "filter": "(sip:10.1.2.11) AND (<SRC>:disk) AND (<EVTID>:153)",
  "start": "…", "end": "…", "fields": "msg,dt,sun,sip", "pgsize": 3, "max-results": 3, "type": "USER" }
```

watchlist Lucene 字串由 `KnownIssueCatalog`／rules.json **程式生成**（規則已外部化，
watchlist 推導既有；新增一個「規則表 → Lucene 子句」的純函數，進單元測試與 `--selftest`）。

## 5. 降低 Sentinel 負擔的措施總表

| # | 措施 | 為什麼有效 |
|---|---|---|
| 1 | **watchlist 先在 server 端過濾**（Lucene filter） | 只索引查詢命中事件，不全量拉回；99% 事件不出 server |
| 2 | **欄位投影**（fields） | Q1 每筆只回 4 欄，序列化與傳輸成本降一個數量級 |
| 3 | **單一併發 per Sentinel** | 任一時刻每台最多 1 個我方 job；不與現場操作人員搶資源 |
| 4 | **跨 Sentinel 平行** | 不同 Sentinel 是獨立系統，平行不增加單台負擔、縮短總時程 |
| 5 | **01:00 夜間窗** | 避開日間互動查詢尖峰 |
| 6 | **token 重用＋登出** | 整輪一次認證；不製造認證風暴、不留殭屍 session |
| 7 | **job 用完即 DELETE** | 不佔用 server 的 search job 資源與快取 |
| 8 | **`QueryDelayMs` 節流** | 呼叫間隔可調；哪台 Sentinel 反映負載即可單獨放慢 |
| 9 | **`max-results` 安全閥** | 異常爆量日不無限制拉取；截斷誠實標 DataIncomplete |
| 10 | **增量收集**（缺漏日回補機制沿用） | 已分析日永不重查；每天只查該查的日子 |
| 11 | **Q4 降為每週** | 覆蓋狀態變化慢，不必每日全清單掃描 |
| 12 | **Q2 可降級**（SampleFetchMode） | 負載敏感環境可只查 Security/Other 簽章範例 |
| 13 | **退避重試（Polly）** | 503＝server 忙，指數退避讓路而不是重錘 |
| 14 | **type:USER＋表明身分** | SIEM 管理者在 Active Searches 看得到、可管理可取消——當個好房客 |

## 6. 設定檔增補（`NetIqSettings`，`LogForesight.Core/Configuration/AppSettings.cs`）

> 2026-07-24 修正：`Servers` 欄位現況是「store 為空時的一次性種子」（見 §2.1），
> 不再是連線資訊的日常事實來源，但仍是合法的種子輸入格式，故保留在範例中。
> 下列**只有節流／行為欄位是本次新增**；`Servers` 本身結構不變。

```jsonc
"NetIq": {
  "Servers": [ { "Name": "SENTINEL-A", "BaseUrl": "…", "Username": "…", "Password": "" } ],  // 既有欄位，現況見 §2.1
  "SampleFetchMode": "Full",        // 新增：Full | Reduced
  "QueryDelayMs": 0,                // 新增
  "PageSize": 500,                  // 新增
  "MaxResultsPerJob": 100000,       // 新增
  "TimeoutSeconds": 120,            // 新增
  "RetryCount": 3,                  // 新增
  "AllowInvalidCertificates": false // 新增：自簽憑證環境的顯式逃生門（啟用即 WARN）
}
```

- 密碼保護沿用既有 `CryptoHelper`（見 §2.1），**不新增** DPAPI 或 CLI 保護指令。
- 新增的節流／行為欄位**連同行為一起實作**（「有設定無行為」紅線）；沒有 `Enabled` 開關——
  Sentinel 清單本身為空（`ISentinelStore` 無資料）時機房 pipeline 自然無主機可查、零副作用，
  不需要疊加一個語意重複的旗標。

## 7. 測試計畫

- **單元**：watchlist→Lucene 產生器（含跳脫、IP 批次切分）；本地聚合純函數
  （事件流→簽章統計，含 min/max dt）；欄位對應解讀（probe 真實回應存 fixture）；
  token 過期重放、job 清理保證、max-results 截斷旗標（`HttpMessageHandler` stub）。
- **合約**：`SentinelStatsSource` 與 `LocalStatsSource` 餵等價輸入 → `DailySignatureStats`
  逐位一致（抽象層語意保證）。
- **`--selftest`**：新增「規則表→Lucene 子句」推導檢查（規則改了、Lucene 跟著對）。
- **閘門**：probe 輸出貼回對話定案欄位對應 → 2~3 台試點端到端（Phase 2）→ 全量。

## 8. 實作順序

1. `SentinelClient` ＋ 單元測試（stub HTTP）（**不含** DPAPI／密碼保護 CLI，見 §2.1 修正）
2. `--netiq-probe` → **閘門：真實環境輸出貼回定案欄位對應／批次大小／時區**
3. `SentinelFieldMap` ＋ watchlist→Lucene 產生器 ＋ 本地聚合
4. `SentinelStatsSource`（Q1→Q2→Q3→Q4）＋ 合約測試
5. `SentinelRestDirectoryClient` 改寫（Web 探索走真 API，連線資訊來源不變只換 API 呼叫方式）
6. 試點 → 全量（PLAN.md Phase 2→3 原路線）

**2026-07-24 實作進度**：步驟 1～2 已完成（`SentinelClient`／`--netiq-probe`／設定欄位／單元測試），
**步驟 2 的真實環境輸出尚未取得**——步驟 3～6 全部依賴 probe 定案的欄位對應，故本輪先停在這裡，
待使用者在實際 Sentinel 環境跑過 `--netiq-probe` 並貼回輸出後再繼續。

## 9. 未決事項（全部由 probe 收斂）

1. Windows EventID／來源／頻道在 8.5 schema 的欄位落點（§3.3 候選）。
2. `dt` 時區與日切界實測。
3. 8.5 apidoc 是否有聚合端點（有→Q1 改走聚合，§1.3 設計降為退路）。
4. IP 篩選單一 filter 的安全批次大小（預設 50 待實測）。
5. 多網卡主機以哪個 IP 回報（「查無資料」假象風險，PLAN.md probe #7）。
6. token 有效期長短（決定長輪收集中是否需要主動換發）。
