# NetIQ Sentinel 取數 API 實作規劃（NETIQ-API-PLAN）

> 2026-07-24 規劃定案候選版；**`SentinelClient` / `--netiq-probe` CLI 已實作完成**，
> 卡在等使用者於真實 Sentinel 環境跑 `--netiq-probe` 貼回實際輸出，才能繼續後續欄位對應。
> 範圍：依 NetIQ 原廠文件確認的 Sentinel REST API 事實，落成批次端取數 pipeline
> （`SentinelClient` / `SentinelStatsSource` / `--netiq-probe`，對應 docs/HISTORY.md Phase 1–2）
> 與 Web 端 `SentinelRestDirectoryClient` 骨架補完。
> 設計主軸：**盡可能降低 Sentinel server 負擔**（§5 有完整對策清單）。
> 本文件與 docs/HISTORY.md「Sentinel 8.5 查詢設計」互補：docs/HISTORY.md 定了 Q1~Q4 的高階形式，
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
| 清理 | `DELETE /SentinelRESTServices/objects/event-search/{id}` | **用完即刪**（docs/HISTORY.md 既有決策），不留 job 佔用 server 資源 |

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
`submitGroupEval` 屬內部 Distributed Search 介面，不在支援面上）。因此 docs/HISTORY.md
「GROUP BY 經 REST 不可用時的退回方案」**直接轉為正案**：

> Q1 ＝ watchlist Lucene 篩選（server 端先過濾掉 99% 事件）＋欄位投影（每筆只回
> 主機/來源/EventID/dt 四欄）＋分頁拉回**本地計數聚合**。

負擔評估：server 端做的是它本來就最擅長的索引查詢與序列化，聚合的 CPU/記憶體成本
由我方批次主機承擔；傳輸量＝watchlist 命中事件 × 每筆約 100~200 bytes。全機房正常日
估數萬筆（多數主機一天只有零星 watchlist 事件），最壞（多台被暴力破解狂刷 4625）數十萬筆
＝幾十 MB，夜間窗口內可接受。probe #3 仍會實測 8.5 的 apidoc 是否多出聚合端點——有就用，
沒有也不影響本設計成立。

## 2. 與既有規劃/程式的對帳

> **2026-07-24 修正**：本節原依 docs/HISTORY.md 撰寫，假設帳密設定仍以
> `appsettings.NetIq.Servers`（per-server `Username`/`Password`）為事實來源、密碼用
> DPAPI 保護。**這個前提已被 docs/HISTORY.md（同日稍晚定案）取代**：
> Sentinel 連線設定現在由 **Web 維護、存 `ISentinelStore`（webdata blob，key=`sentinels`）**，
> 密碼用 **Core 既有的 `CryptoHelper`（AES-256，`enc:v1:` 前綴，金鑰內嵌程式）**加密，
> 不是 DPAPI；`appsettings.NetIq.Servers` 降為「store 為空時的一次性種子」。以下對帳表已更新為現況。
>
> **2026-07-27 再修正**：節流設定與種子的落點又演進了一步——`NetIqSettings` 類別與
> appsettings.json 的 `NetIq` 區段（含 `Servers` 種子）**已整個移除**，`SentinelSeeder` 退役。
> 節流參數改為 `NetiqOptions`（webdata blob，key=`netiq_options`），由 Web
> 「系統管理 > NetIQ 維護」頁（`/admin/netiq`）維護，批次與 Web 都從
> `StorageFactory.CreateNetiqOptionsStore` 讀同一份；`SentinelClient`／`NetiqProbeCli`
> 的參數型別同步改為 `NetiqOptions`（欄位不變）。下表第 4 列「仍留在 `AppSettings.NetIq`」
> 的描述已過時，僅保留當時脈絡。

| 項目 | 既有狀態（2026-07-24 確認） | 本規劃處置 |
|---|---|---|
| 帳密事實來源 | **`ISentinelStore`**（DB blob）為主，Web CRUD 維護；`appsettings.NetIq.Servers` 僅一次性種子（`SentinelSeeder`） | `SentinelClient` 的連線資訊一律吃既有 `SentinelServer` 投影物件（`Name`/`BaseUrl`/`Username`/明碼 `Password`）——批次與 Web 都已有現成的「讀 store→解密」程式碼可重用（見 §3.0） |
| 密碼保護 | **`CryptoHelper.Encrypt/Decrypt`（AES-256，`enc:v1:` 前綴）已實作**，非 DPAPI；解密只在讀出時做一次，明碼只留在行程記憶體 | 沿用既有 Helper，**不新增 DPAPI 或 `--protect-netiq-password`**（原規劃此項作廢） |
| `SentinelRestDirectoryClient` | 骨架端點打 `/SecurityManager/rest/hosts`（占位、非 Sentinel 端點）＋Basic auth 直打；已改吃 `INetiqServerCatalog`（內部即 `ISentinelStore`）取得含明碼密碼的 `SentinelServer` | 整段改寫：連線資訊來源不變，只改「怎麼打 API」——走 §1.1 SAML 認證＋§4.4 的探索查詢 |
| `NetIqSettings` 節流欄位 | 刻意未加（「有設定無行為」紅線） | 本次連同行為一起加：`QueryDelayMs`/`PageSize`/`TimeoutSeconds`/`RetryCount`/`MaxResultsPerJob`/`AllowInvalidCertificates`（這些是**查詢行為**設定，與帳密事實來源無關，仍留在 `AppSettings.NetIq`，批次與 Web 各自從自己的 appsettings 讀） |
| Q4 頻道覆蓋 | docs/HISTORY.md 降為每週 | 不變，實作照每週 |

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
> 3. **`ISentinelClient` 介面已移除（2026-07-28 簡化重構）**：從未有第二個實作或測試假件，
>    純屬型別多型的殘留；`SentinelClient` 類別保留、只是不再 `: ISentinelClient`，改直接實作
>    `IAsyncDisposable`。呼叫端一律注入具體類別 `SentinelClient`，本節與 §3.4 的介面型別提及處
>    在實作上已是這個具體類別。

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

### 3.2 `SentinelStatsSource`（原設計；**實作已改走決策 B2**，見下）

> **2026-07-29 實作註記**：本節的統計抽象層設計（`IDailyStatsSource`／`DailySignatureStats`）
> **未依原樣實作**——實際落地是決策 B2：Sentinel 事件直接映射成 `EventLogEntryData`
> （`SentinelEventMapper`），整條既有分析路徑零改動重用，實作為
> `LogForesight/Service/NetiqPipelineService.cs`（Phase 4）。因此 **Q2 已取消**（msg 直接投影在
> Q1 內，`SampleFetchMode` 設定隨之退役）；Q3 的角色由 Q1 已含 msg 的事件天然涵蓋；
> Q4 頻道覆蓋申報延後（§9 未決事項 #3）。以下原文保留供設計脈絡對照，**不代表現況**。

業務層：把 Q1~Q4 組裝成 `DailySignatureStats`（與 `LocalStatsSource` 同一輸出模型，
下游五層偵測零改變——docs/HISTORY.md 抽象層「日統計」定案的兌現）。

- **Q1 主聚合**：per-Sentinel、per-日。filter＝`(watchlist Lucene) AND (IP 清單批次)`；
  fields＝主機、來源、EventID、dt 四欄。IP 清單分批（預設 50 台/批，probe #8 實測
  Lucene 子句上限後調整；Lucene 預設 maxClauseCount 1024，50 台遠低於限）。
  本地以 `(host, source, eventId)` 分組計 count/min(dt)/max(dt) → 簽章統計。
- **Q2 範例訊息**：只對「進 prompt 的簽章」逐一小查詢（filter 鎖單簽章、fields 含 msg、
  `max-results: 3`）。`SampleFetchMode: Reduced` 時僅 Security 與 Other 類簽章查。
- **Q3 風險主機原始 log**：風險日才觸發，單主機小查詢、20 筆預算（沿用既有）。
- **Q4 頻道覆蓋**：每週一次，per-Sentinel 全清單 IP 查近 24h、fields＝主機＋頻道，
  本地 distinct → 未收 Security 頻道主機清單（覆蓋率誠實申報）。
- **失敗隔離**（docs/HISTORY.md 既有決策的落點）：單一 IP 批次失敗 → 該批主機當日標
  「查詢失敗、資料不完整」，其他批照常；單台 Sentinel 整台失聯 → 其轄下主機全標、
  機房總覽「來源狀態」列失聯 Sentinel。

### 3.3 欄位對應（probe 定案前的候選）

Windows Event ID 在 Sentinel schema 的落點是 probe #2 的實測項。候選欄位（Tips 頁核對）：
`evt`（事件名）、`msg`（訊息）、`sev`、`dt`、`shn`/`sip`（來源主機/IP）、`sun`/`dun`（帳號）、
`pn`（產品）、`rv40`/`rv25`/`xdasid`/`ei`（外部事件代碼候選——**以現場 Tips 頁與實際事件
樣本為準，不預先寫死**）。對應表落地為 `SentinelFieldMap`（設定可覆寫的字典，per-server
覆寫保留為保險），probe 輸出直接產生此表的草稿。

### 3.4 `SentinelRestDirectoryClient` 補完（Web 探索）

> **2026-07-29 Phase 5 已改為「網段範圍掃描」實作，以下為原始設計、僅留供對照，
> 不代表現況**——原設計「近 24h 全事件 distinct」已被第一輪 probe（近 24h found≈2470 萬筆）
> 推翻。實際做法見本節末的「Phase 5 定案」段落與 §9 未決事項 #1。

改寫為走同一 `ISentinelClient`：探索＝「近 24h 對該 Sentinel 全事件做主機欄位投影
＋本地 distinct」（等同 Q4 的單次版）。注意：

- 全事件量大，探索**只投影主機名/IP 兩欄**＋`max-results` 上限（如 50 萬）；
  超限代表環境事件量超乎預期，回報「請縮小掃描範圍」而不是硬拉。
- 探索是互動操作：輪詢預算 30 秒（既有 UI 逾時），超時明確報錯（job 照樣 DELETE）。
- Web 端與批次端共用 Core 的 client，但**各自實例**（批次夜間、Web 日間，天然錯開；
  同一 Sentinel 的併發上限仍由各實例的單一佇列保護，最壞 2 併發，可接受）。

#### Phase 5 定案：網段範圍掃描（取代上述原設計）

ESM `/objects/eventsource` 端點被權限拒絕（見 §8 第二輪輸出），且全站 24h distinct
在 2470 萬筆/天下不可行，兩條路都走不通。使用者在 Sentinel Web UI 實測
`repip:10.232.11.*` 這類**前綴萬用字元查詢確實有過濾效果**（23,926 筆/1h vs 全站
150 萬筆/1h），因此改用「輸入網段前綴 → 該網段自己的自適應窗口查詢」，完全不碰 ESM API：

- **輸入**：使用者輸入網段前綴（如 `10.232.11`）或 CIDR（`10.232.11.0/24`／`/16`），
  由 `SentinelQueryBuilder.NormalizeSubnetPrefix` 驗證與正規化（至少 2 段，拒絕單段
  「等同全站」與完整 4 段單一 IP）、`BuildSubnetDiscoveryFilter` 組出
  `repip:{prefix}.*`（`SentinelQueryBuilder.cs`）。
- **自適應時間窗**（`SentinelRestDirectoryClient.ListHostsAsync`）：先送一次
  `max-results:1` 的計數查詢取得該網段近 24h found 數；found=0 直接短路回報「近 24 小時
  無事件」；否則窗口＝`found ≤ 50000 ? 24h : max(5分鐘, 24h × 50000/found)`——
  量級越高窗口越窄，讓查詢結果穩定落在可互動回應的範圍（真實 759,052 筆/24h 的探索用
  probe 樣本換算約 1.6 小時窗口）。
- **主查詢**只投影 `repip`／`sn` 兩欄（不含事件內容），依 `repip` 分桶、組內 `sn` 眾數
  當作該主機顯示名稱（收斂 log 雜訊裡的微小拼寫差異）；掃描結果的顯示名稱直接帶進
  匯入（`NetiqImportApplier.Apply` 的 `displayNameByIp` 參數），新主機當下就有名字，
  不用等夜間批次 `TouchNetiq` 回填。
- **誠實申報涵蓋範圍**：`NetiqDiscoveryResult.CoverageNote`／`Warnings` 說明實際掃描窗口
  與截斷情形，安靜的主機（窗口內剛好沒事件回報）不在結果內，UI 提示改用主機頁／CSV 手動登錄。
- 互動逾時 30 秒、`PageSize`/`MaxResultsPerJob` 覆寫為探索專用值，與批次夜間查詢的參數
  互不影響（各自的 `NetiqOptions` 實例）。
- **整趟掃描另有 90 秒總預算**（`InteractiveTotalBudgetSeconds`）：上面那個 30 秒只約束
  *單次* REST 呼叫與 job 輪詢階段，**分頁迴圈不受它約束**——50,000 筆÷1000 筆/頁＝最多 50 次
  連續翻頁，以實測單次往返約 1.7 秒計，最壞可以讓管理員在精靈畫面前乾等一分半以上而沒有任何
  上限。總預算用盡時**明確報錯並建議縮小網段，不回傳半套結果**（半套清單會被誤認為該網段的
  完整主機名單，違反「沒查 ≠ 沒事」的誠實申報原則）。呼叫端自己取消（管理員關掉分頁）與
  預算用盡是兩件事，前者維持正常的取消語意、不包裝成假的失敗訊息。

### 3.5 `--netiq-probe`（Phase 1 閘門，輸出貼回對話定案）

一鍵對每台設定的 Sentinel 依序執行、輸出成一份可貼回的報告（敏感值遮罩）。實際實作的步驟
（`LogForesight/Service/NetiqProbeCli.cs`）：

1. 認證＋小範圍 event-search（近 1h、`max-results:3`、全欄位）：傾印原始 JSON → 定案欄位對應。
2. `dt` 界線初測：近 2 小時拆兩段查 found 數，含/不含語意與時區人工核對。
3. 分頁：`pgsize` 100/500/1000 三點採樣耗時。
4. IP 篩選批次上限：以 10/50/100 個 IP 子句（`repip` 欄位）各查一次，找出安全批次大小。
5. 失敗路徑：非法 filter（預期 400）。
6~12. **第二輪（2026-07-29，第一輪真實輸出後新增）**：ESM `eventsource` 清單（探索備案／OS 判別）、
   登入事件 4624/4625 取樣（主機歸屬鍵／`sun` 帳號語意）、Linux 主機樣本（`--sample-linux-ip`）、
   System/Application 頻道覆蓋（`--sample-ip`）、generic `sev:[3 TO 5]` 量級、dt 邊界精確核對
   （鎖單台主機、近 1 小時拆兩個 30 分鐘，把 found 壓到可人工比對的量級）、`obssvcname`
   完整片語 vs 部分詞查詢行為。
   步驟 8 需要 `--sample-linux-ip`、步驟 9／11 需要 `--sample-ip`，省略時明確標示略過；
   其餘步驟（含步驟 6 的 eventsource 清單）不需要任何參數。
   需人工核對的步驟（2／11）一律印出**絕對**時間區間（UTC＋本機並列），否則操作者無從在
   Web UI 重現同一段區間。
13. 失敗路徑：錯誤密碼認證（預期 401，獨立 client 執行、放在最後避免污染前面步驟的 token 狀態）。

probe 全程遵守單一佇列＋節流，對 server 負擔可忽略。**第一輪真實輸出**（元大環境，Sentinel「162」）
已推翻「日估數萬筆」的估計（近 24h found≈2470 萬筆），也推翻了 §3.4「探索走近 24h 全事件 distinct」
的原設計；欄位對應大致定案見下表，主機歸屬鍵／探索方案／頻道覆蓋現況待第二輪輸出收斂
（見 §9 未決事項）。

**第一輪已定案的欄位對應**（Windows/AD 事件，取代本節與 §3.3 原先推測的 `shn`/`sip`/`evt`）：

| 語意 | 欄位 | 備註 |
|---|---|---|
| Windows EventID | `rv40` | 以 4634／4624／4771／4627 多筆交叉實證（配 `evt` 中文事件名一致） |
| 事件來源（provider） | `obssvcname` | 值如 `Microsoft-Windows-Security-Auditing`，`SourcePattern` 比對對象 |
| 頻道（LogName） | `rv150` | 值如 `Security`／`System`／`Application` |
| **主機歸屬鍵** | **`repip`** | **第二輪定案**：四台 DC 對到四個各自不同的 `repip`（一對一，非共用代理），且 `--sample-ip` 以 `repip:` 查得到該台資料。**這是「這筆 log 屬於哪台主機」的鍵** |
| 主機名 | `dhn`／`sn` | 兩者同值，即 `repip` 那台主機的名稱 |
| 用戶端 IP | `sip` | **不是主機自身**——是發起連線的遠端來源（同一台 DC 的三筆登入事件有三個不同 `sip`）。第一輪誤判為「本環境不存在」，其實只是登出事件沒帶 |
| 時間 | `dt` | ISO-8601 UTC；`estz` 為事件來源時區（`Asia/Taipei`，實測本機＝UTC+8 相符） |
| 嚴重度 | `sev` | 0～5；已見 `sev=4`（Kerberos 預先驗證失敗）與 `sev=0`（成功登入），與 Error/Warning 的完整對應仍待更多樣本 |
| 訊息／事件名 | `msg`／`evt` | 皆為繁中——AI 層與報告可直接使用 |
| 帳號 | `sun`／`dun` | **第二輪定案為帳號名**：已見 `vtit.brk`（具名帳號）、`13456`／`182713`（員工編號式帳號）、`-`（無）。`iuid`／`tuid` 為 SID |
| OS／collector 判別 | `pn`／`agent`／`port` | 三者同值（`Microsoft Active Directory and Windows`）；Linux 對照值仍缺（本環境的 Sentinel「162」無 Linux 主機） |

## 4. 查詢 payload（**已實作**，`SentinelQueryBuilder.BuildWindowsFilter`，2026-07-29）

Q2（單簽章範例查詢）**已取消**——msg 已在 Q1 投影欄位內，不需要另查範例訊息，比原規劃少一類查詢。

```jsonc
// Q1（每 IP 批次一個 job，實際欄位名，三輪 probe 實證）
{
  "filter": "(repip:10.1.2.11 OR repip:10.1.2.12 OR …) AND ((rv40:(4625 OR 4720 OR …)) OR ((rv150:System OR rv150:Application) AND sev:[2 TO 5]))",
  "start": "<當地日00:00→UTC>", "end": "<翌日00:00→UTC>",
  "fields": "repip,sn,rv40,obssvcname,rv150,dt,sev,msg,evt,sun,xdasoutcome",
  "pgsize": 500, "max-results": 100000, "type": "USER",
  "init-user": "svc-lfquery", "InitiatingHostName": "<批次主機名>"
}
```

- `rv40` 聯集只含**有明確 EventId 的 Windows 規則**；`MatchAllEventIds` 規則（WHEA-Logger／
  Resource-Exhaustion／VSS，皆為 System 來源）沒有具體 ID 可下推，靠 generic 分支撈進來後
  本地 `Classify` 精準比對——這條路徑本來就存在，零額外成本。
- Security 頻道**沒有**獨立分支：規則命中的 Security 事件由 `rv40` 聯集涵蓋（種子規則本來就是
  為高價值 Security EventId 寫的），未被任何規則覆蓋的「未知失敗 ID」在 v1 不會被撈入——
  這是相對本機模式（FailureAudit 不論 ID 全收）的已知涵蓋縮小，原因是 Security 頻道「這是失敗
  稽核」的可靠判定要靠 `xdasoutcome`（見 §3.5 表），而拿它組 Lucene 條件（`xdasoutcome:0` 取反）
  的可行性未經 probe 驗證。留待試點階段决定是否值得補一條 `NOT xdasoutcome:0` 分支。
- watchlist Lucene 字串由 `KnownIssueCatalog.Rules` **程式生成**
  （`LogForesight.Core/Analysis/SentinelQueryBuilder.cs`，規則已外部化，watchlist 推導既有；
  純函數不讀任何全域可變狀態，呼叫端傳入規則清單）；`--selftest` 新增 `RunSentinelQueryChecks`
  驗證規則表改了、filter 的 `rv40` 子句跟著對。

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
| 11 | ~~**Q4 降為每週**~~ | Q4 覆蓋申報已延後（§9 未決事項 #3），此措施屆時再議 |
| 12 | ~~**Q2 可降級**（SampleFetchMode）~~ | **已隨 Q2 取消一併退役（2026-07-29）**——msg 直接投影在 Q1 內，Q2 這類查詢不存在了 |
| 13 | **退避重試（Polly）** | 503＝server 忙，指數退避讓路而不是重錘 |
| 14 | **type:USER＋表明身分** | SIEM 管理者在 Active Searches 看得到、可管理可取消——當個好房客 |

## 6. 設定檔增補（`NetIqSettings`，`LogForesight.Core/Configuration/AppSettings.cs`）

> 2026-07-24 修正：`Servers` 欄位現況是「store 為空時的一次性種子」（見 §2.1），
> 不再是連線資訊的日常事實來源，但仍是合法的種子輸入格式，故保留在範例中。
> 下列**只有節流／行為欄位是本次新增**；`Servers` 本身結構不變。
>
> 2026-07-27 已再演進：整段移到 `NetiqOptions`（webdata blob，Web「NetIQ 維護」頁），
> appsettings 的 `NetIq` 區段整個移除（見 §2 的修正註記）。
> 2026-07-29：`SampleFetchMode` 隨 Q2 取消一併退役（「有設定無行為」紅線），
> 下方範例保留當時樣貌供歷史對照。

```jsonc
"NetIq": {
  "Servers": [ { "Name": "SENTINEL-A", "BaseUrl": "…", "Username": "…", "Password": "" } ],  // 既有欄位，現況見 §2.1
  "SampleFetchMode": "Full",        // 已退役（2026-07-29，Q2 取消）
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

- **單元（已完成）**：`SentinelQueryBuilder`（IP 批次子句、規則 EventId 聯集去重排序、
  停用/MatchAllEventIds/Linux 規則排除、空清單防呆）；`SentinelEventMapper`（欄位對應、
  UTC→本機時區、msg 缺席退回 evt、數字欄位容錯、批次略過計數）；`SentinelFieldMap.MapEntryType`
  （Security 頻道靠 xdasOutcome、非 Security 靠 sev 門檻）；`SentinelClient` 既有測試
  （token 過期重放、job 清理保證、max-results 截斷旗標、`HttpMessageHandler` stub）。
  fixture 依三輪 probe 真實輸出的欄位「形狀」建構，**值已去識別化**（真實 IP／主機名／網域
  換成範例假值，不落地任何客戶識別資訊）。
- **合約（已完成）**：`SentinelPipelineContractTests`——Sentinel 事件經 `SentinelEventMapper`
  映射後餵進與本機路徑**完全相同**的 `LogAggregator.Aggregate`／`KnownIssueCatalog.Classify`，
  驗證產出的 `LogIssueSignature`（RuleId／Category／Severity／ElevatesDayRisk／聚合分組鍵）
  與本機路徑餵等價輸入的結果同構——這是「整條五層偵測零改動重用」設計主張（決策 B2）的
  實測驗證，不是文件宣稱。
- **`--selftest`（已完成）**：`RunSentinelQueryChecks`——用目前生效的規則表實際跑一次
  `SentinelQueryBuilder`，驗證 IP 子句／generic 分支/rv40 聯集正確反映規則表、
  MatchAllEventIds 規則不混入下推聯集。
- **閘門**：probe 三輪已完成（欄位對應／主機歸屬鍵／量級／頻道覆蓋樣本皆已取得，見 §8）。
  下一步是 2~3 台試點端到端（Phase 4，見 docs/BACKLOG.md）→ 全量。

## 8. 實作順序

1. `SentinelClient` ＋ 單元測試（stub HTTP）（**不含** DPAPI／密碼保護 CLI，見 §2.1 修正）
2. `--netiq-probe` → **閘門：真實環境輸出貼回定案欄位對應／批次大小／時區**
3. `SentinelFieldMap` ＋ watchlist→Lucene 產生器 ＋ 本地聚合
4. `SentinelStatsSource`（Q1→Q2→Q3→Q4）＋ 合約測試
5. `SentinelRestDirectoryClient` 改寫（Web 探索走真 API，連線資訊來源不變只換 API 呼叫方式）
6. 試點 → 全量（docs/HISTORY.md Phase 2→3 原路線）

**2026-07-24 實作進度**：步驟 1～2 已完成（`SentinelClient`／`--netiq-probe`／設定欄位／單元測試）。

**2026-07-29 第一輪真實輸出已取得**（Sentinel「162」）：§3.5 表格所列欄位對應已定案，
但同時發現量級遠超估計（近 24h found≈2470 萬筆）、原探索設計（§3.4「近 24h 全事件 distinct」）不可行、
且「主機歸屬鍵是哪個欄位」（`repip` 是否等於主機自身 IP）成為最關鍵未決項。當天已擴充
`--netiq-probe` 加第二輪查詢（步驟 6～12，見 §3.5）＋`SentinelClient.RawGetAsync`（打 ESM
`/objects/eventsource` 等一般資源用，不走 event-search job 生命週期）。

**2026-07-29 第二輪真實輸出已取得**（同日，`--sample-ip 10.232.11.11`；該 Sentinel 無 Linux 主機
故略過 Linux 段）。**最關鍵的閘門已解除**：

- **主機歸屬鍵定案為 `repip`**——四台 DC（`tc-brkdc01`／`tp-brkdc12`／`tp-brkdc13`／`tp-brkdc21`）
  對到四個各自不同的 `repip`（`10.218.9.1`／`10.216.9.2`／`10.216.9.3`／`10.220.8.100`），一對一、
  非共用的 collector 代理 IP；`--sample-ip` 用 `repip:` 也確實查得到該台資料。同時釐清 `sip` 是
  **用戶端來源 IP**（同一台 DC 的三筆登入事件有三個不同 `sip`），不是主機自身。
- **`sun` 定案為帳號名**（見 §3.5 表）。
- **System／Application 頻道有資料但量極少**：`repip:10.232.11.11` 近 24h System=3、Application=152，
  而該主機總量約 31 萬筆/日（步驟 11 推算），即 **99.95% 是 Security**。研判 collector 對這兩個頻道
  只轉送 Error/Warning 等級（恰與本機模式的 `ErrorWarningOnly` 同策略）。磁碟／服務／硬體類規則
  **有**資料來源，但實際涵蓋的嚴重度區間待步驟 9 的樣本傾印確認。
- **ESM `/objects/eventsource` 端點被拒（401/403）**：目前的探索帳號有 event-search 權限但無
  ESM 物件讀取權限。探索方案因此仍未定（見 §9）。
- **發現並修正 `SentinelClient` 的真實 bug**：Sentinel 的 JSON 解析器**不接受 `\uXXXX` 轉義序列**，
  而 `System.Text.Json` 預設編碼器正好會把 `"` 寫成 `"`、非 ASCII 寫成 `\uXXXX`，導致
  **片語查詢（`obssvcname:"…"`）整個被 400 拒絕**——片語是規則來源下推 Lucene 的必要語法，
  這在正式取數管線會是全面性故障。已改用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  （`SentinelClient.JobBodyJsonOptions`）並加回歸測試。同一個 bug 也讓步驟 5（非法 filter 應被拒絕）
  一直**為了錯的理由通過**——原本用中文的測試字串是在 JSON 解析階段就被拒，根本沒走到 Lucene
  語法檢查，已改為純 ASCII 並加上「錯誤訊息若仍是 invalid JSON 就是轉義還有問題」的自我檢查。

**2026-07-29 第三輪真實輸出已取得**（轉義修正後重跑同一支 `--sample-ip`）。技術未決項幾乎全收斂：

- **主機名欄位鏈定案**：`sn`＝記錄這筆 log 的主機自己（觀察者，與 `repip` 成對，`DisplayName`
  回填用它）；`dhn`＝目的地主機；**`shn` 存在**（跨主機認證事件出現，發起端機器名——第一輪
  「本環境無 shn」是誤判，登出事件不帶而已）；`sip`＝發起端 IP。關聯層未來可用
  `sun`/`sip`/`shn` 結構化欄位，不必解析 msg 文字。
- **`obssvcname` 確定是 term 欄位、不斷詞**（步驟 12：完整片語 found=142205、部分詞 found=0）——
  規則 `SourcePattern` 的子字串比對**不能**下推 Lucene，Q1 下推改為只靠 `rv40` 聯集＋generic
  分支，來源比對留在本地 `Classify`（本來就是權威判定，見 §4）。
  轉義修正在真實環境實證成功：步驟 5 這次真的收到 Lucene parse error、步驟 12 片語查詢可用。
- **collector 有轉送 Information 級**（步驟 9 樣本：System 頻道 NTFS「磁碟區健康情況良好」sev=1、
  Application 頻道 Security-SPP 通知 sev=1）——上一輪「只轉送 Error/Warning」的推論**是錯的**，
  量少（3／152 筆/日）是主機本來就少，不是被過濾。sev 對應候選：0＝Security 成功稽核、
  1＝Information、4＝稽核失敗（Kerberos 4771）；**Warning 是否＝2、Error 是否＝3 仍未實證**
  （這 24h 剛好沒有真 Error 樣本），已在 `SentinelFieldMap.MapEntryType` 標註候選門檻，留待
  試點核對，不影響規則命中（規則比對 Source＋EventId，與 EntryType 無關）。
- 意外收穫：樣本主機其實是另一個網域的 DC——證實同一台 Sentinel 收多網域主機、`repip`
  歸屬不受網域影響。

**不再開第四輪 probe**——剩餘未決項全部移到試點階段核對（見下）。

**2026-07-29 Phase 3 已實作**（`LogForesight.Core/Analysis/SentinelFieldMap.cs`／
`SentinelEventMapper.cs`／`SentinelQueryBuilder.cs`，只有 Windows 分支——Linux 那台 Sentinel
尚未接入，沒有真實 probe 樣本可依據，見 docs/LINUX-RULES-PLAN.md P3）：
1008 個單元測試綠、`--selftest` 136 項全過（含新增的 `RunSentinelQueryChecks`），
含合約測試證實 Sentinel 路徑與本機路徑聚合分類結果同構（決策 B2 的實測驗證）。

**2026-07-29 Phase 4 已實作**（`LogForesight/Service/NetiqPipelineService.cs`，機房 pipeline 本體）：
`Program.cs` 本機分析完成後接機房迴圈，逐 Sentinel → 逐日（跨主機遞增、批次 IP≤50）→
`SentinelQueryBuilder` 建 filter → `SentinelClient.SearchAsync` 取事件 → 依 `repip` 分桶 →
`SentinelEventMapper` 映射 → 逐主機餵進與本機路徑相同的 `LogAnalysisService.AnalyzeDayAsync`。
**只支援 Windows 主機**（Linux 明確標示「尚未支援」，不靜默略過）。當日續跑靠既有
`HasRecord`（各主機 owner-host 隔離的 record store），凌晨排程跑到一半掛掉、白天重跑只補
未完成的主機/日期。**2026-07-30 修訂**（docs/FEEDBACK-3-PLAN.md #1）：回補天數改為可設定的
`NetiqOptions.BackfillDays`（預設 1，「系統管理 > NetIQ 維護」可調，上限 14），首次執行與
缺漏日回補統一套用同一個值——原本「首次深度回補 14 天」的例外路徑已移除，2000 台規模下
不管是首次登錄還是排程漏跑，對 Sentinel 做大量歷史日查詢都不現實。
**同日修訂**（docs/FEEDBACK-3-PLAN.md #2）：per-Sentinel 迴圈改 `Parallel.ForEachAsync`
平行處理（`NetiqOptions.MaxParallelServers`，預設 2、設 1＝完全依序的逃生門）——各台
Sentinel 轄下主機互不重疊、各自獨立連線，跨台平行不破壞「同一台主機不同日期依序」的趨勢
比對前提（該限制只在單一主機內成立）；`NetiqPipelineResult` 計數與 `BatchRunRecorder`
已改為執行緒安全更新。失敗隔離兩層：單一批次查詢失敗只影響該批（其餘批次照跑）、單一 Sentinel 整體失敗
不影響其他 Sentinel。截斷（`Truncated`）時整批主機統一標記 `dataIncomplete`（無法判斷截斷影響
哪幾台，寧可全部誠實申報不完整）。

**v1 已知限制**（刻意延後，非遺漏）：
- `securityLogAvailable` 固定 true、`channels` 固定 null——Defender/RDP Operational 頻道在
  Sentinel 端的覆蓋現況尚未驗證（§9 未決事項 #3），v1 不宣稱「已檢查且無異常」，但也還沒有
  正式的「不適用」誠實申報；待試點確認覆蓋現況後再補。
- 逐主機呼叫 `HasAnyRecord`/`HasRecord`（最多 14 次）屬 O(主機數×天數) 的個別查詢，
  2~3 台試點量級無感，但**尚未針對兩千台規模做批次化優化**，全量放量前需評估。
- 建置零警告、**既有 1008 個單元測試與 `--selftest` 136 項維持全綠**（Phase 4 本身是 I/O 導向的
  orchestration 層，比照 `Program.cs` 既有的本機分析迴圈——不直接單元測試，靠
  build/既有回歸測試/`--selftest` 撐住正確性，端到端驗證留給試點）。

**2026-07-29 Phase 5 已實作**（NetIQ 主機發現／新增，取代 §3.4 原設計，解除 §9 未決事項 #1）：
使用者在 Sentinel Web UI 實測確認 `repip:{prefix}.*` 前綴萬用字元查詢有真實過濾效果，改為
「網段範圍掃描」（見 §3.4「Phase 5 定案」）：`SentinelQueryBuilder.NormalizeSubnetPrefix`／
`BuildSubnetDiscoveryFilter`（Core，網段輸入驗證與 filter 組出）＋`SentinelRestDirectoryClient`
全面改寫（Web，計數查詢→自適應窗口→主查詢→依 `repip` 分桶、`sn` 眾數取顯示名稱）＋
`NetiqImportApplier.Apply` 新增 `displayNameByIp` 參數（新主機匯入當下即帶入真實機器名，
只套用在全新主機，既有主機/復活孤兒的 `DisplayName` 一律不動）。移除已死的
`SentinelAdminService.CreateAndScanAsync`／`netiq/create-and-scan` 端點／
`CreateAndScanSentinelRequest`（新增 Sentinel 與掃描已拆成兩個獨立操作，一鍵合併從未被
UI 使用）。匯入精靈（`imports.js`／`Imports.cshtml`）新增網段輸入框與涵蓋範圍/警告訊息顯示。
建置零警告、1037 個單元測試（含新增的 `SentinelRestDirectoryClientTests` 10 項 stub HTTP
測試、`SentinelQueryBuilderTests` 網段正規化 10 餘項、`NetiqLifecycleTests` 顯示名稱透傳
2 項）與 `--selftest` 136 項全綠；Dev/Stub 模式下端到端走過完整精靈流程
（掃描→勾選→分組→匯入）並在真正執行的 Web 應用程式中核對匯入結果。

## 9. 未決事項

**已收斂**：Windows EventID（`rv40`）／事件來源（`obssvcname`，term 不斷詞）／頻道（`rv150`）／
時間（`dt`，UTC）／**主機歸屬鍵（`repip`）**／用戶端 IP（`sip`）／發起端機器名（`shn`）／
帳號（`sun`／`dun`）／System/Application 頻道確實轉送 Information 級事件的欄位落點；
IP 篩選 10/50/100 子句語法皆被接受（約 1.7 秒，與子句數無明顯相關）；片語查詢可用
（轉義修正後實證）。`SentinelFieldMap`／`SentinelEventMapper`／`SentinelQueryBuilder` 已依此實作。

**仍未決（全部移到試點階段核對，不再開第四輪 probe）**：

1. ~~**探索方案**~~ **已解決（2026-07-29 Phase 5）**：ESM `/objects/eventsource` 被權限拒絕、
   全站 24h distinct 不可行，兩條原始路都走不通。使用者實測確認 `repip:{prefix}.*` 前綴
   萬用字元查詢有真實過濾效果，改為「網段範圍掃描＋自適應時間窗」，完全不碰 ESM API，
   見 §3.4「Phase 5 定案」與 §8 Phase 5 段落。掃描結果的涵蓋範圍（時間窗、是否截斷）誠實
   申報在 `CoverageNote`／`Warnings`，窗口內安靜的主機不在結果內、UI 提示改走主機頁／CSV。
2. **sev 的 Warning/Error 確切門檻**（`SentinelQueryBuilder.GenericErrorSeverityMin` 目前取 2，
   `SentinelFieldMap.MapEntryType` 的 2/3 分界皆為候選值）——試點主機跑一晚，簽章的 EntryType
   分布對照該主機本機 Event Viewer 核對。
3. Defender/RDP Operational 頻道有無進 Sentinel——試點時查
   `rv150:"Microsoft-Windows-Windows Defender/Operational"` found（片語查詢現在可用了）；
   沒有＝該偵測面誠實申報不適用。
4. Linux 主機的欄位形狀（program 落點、`pn` 對照值、`sev`↔syslog priority 對應）——
   使用者已確認此環境 Windows／Linux 的 NetIQ **已完全拆分成不同 Sentinel**，「162」上完全
   沒有 Linux 主機。閘門因此是「Linux 那台 Sentinel 何時接入 LogForesight」，接入後對它跑一次
   `--netiq-probe` 即可定案，不是本環境的資料缺口（見 docs/LINUX-RULES-PLAN.md §3、P3）。
5. 真實 watchlist 形狀查詢（`rv40` 事件 ID 集合＋50 台 `repip`）的耗時與命中量——
   決定夜間窗時程與批次大小，待主機清單就緒後實測。
6. `dt` 邊界的人工核對（步驟 11 已輸出可重現的絕對區間與可數的 found 值，待人工於 Web UI 比對）。
7. 8.5 apidoc 是否有聚合端點（有→Q1 改走聚合，§1.3 設計降為退路；尚待人工開啟 apidoc 頁面確認）。
8. 多網卡主機以哪個 IP 回報（「查無資料」假象風險，docs/HISTORY.md probe #7）。
9. token 有效期長短（決定長輪收集中是否需要主動換發）。
