# NetIQ Sentinel 取數 API 參考

> 本文件是 Sentinel REST API 的現況技術參考（認證、事件查詢、欄位對應、查詢 payload）——
> 公開的 Sentinel REST API 文件（7.0/8.2 版）未涵蓋事件查詢結果頁的確切 JSON 結構，
> 下列欄位對應是本環境（8.5）以 NetIQ 維護頁「診斷」分頁的驗證查詢核對後的定案結果。
> 換一套 Sentinel 環境時，仍應以現場 `apidoc`（`https://<sentinel>:8443/SentinelRESTServices/apidoc/`）
> 與 Web UI「Tips」頁為準，本文件的欄位對應是本環境的實測結果，不是原廠保證的通用契約。
> 實作見 `LogForesight.Core/Analysis/SentinelClient.cs`、`SentinelFieldMap.cs`、
> `SentinelEventMapper.cs`、`SentinelQueryBuilder.cs`。取數管線的整體設計（機房 pipeline、
> 網段掃描探索、節流參數）見 docs/WEB-SPEC.md §9.9a／§10.2；欄位對應的實測過程與尚未核對的
> 開放項見 docs/BACKLOG.md。

## 1. 原廠文件依據

| 文件 | 內容 | 位置 |
|---|---|---|
| Sentinel REST API 參考（隨機安裝） | **8.5 環境的最終權威**：每台 Sentinel 自帶完整 API 文件 | `https://<sentinel>:8443/SentinelRESTServices/apidoc/en/index.html` |
| Sentinel API（Beta）公開文件 | 認證流程、EventSearch/EventSearchStatus 全部操作（7.0 版，端點形狀與 8.x 相同） | https://www.novell.com/developer/plugin-sdk/ref/restapi/7.0/ |
| Search Query Syntax（User Guide） | Lucene 查詢語法、可搜尋欄位 | https://www.microfocus.com/documentation/sentinel/8.6/s86-user/bvg1rjs.html |
| 事件欄位清單 | 各安裝實際欄位以 Sentinel 主介面右上「Tips」頁為準（文件明載） | Sentinel Web UI → Tips |

## 2. 認證：SAML token（不是每次 Basic）

1. **取 token**：`POST https://<sentinel>:8443/SentinelAuthServices/auth/tokens`，
   header 帶 `Authorization: Basic <base64(user:pass)>`，回應 JSON 內含 SAML token。
2. **之後所有呼叫**：header 帶 `Authorization: X-SAML <token>`，**不再送帳密**。
3. **驗證 token**（可選）：`GET /SentinelRESTServices/preauthorize?path=...&httpMethod=GET`
   → `{"Authorized":"true"}`。
4. **登出**：`DELETE /SentinelAuthServices/auth/tokens/<token>`。

token 是 server 端 session 資源。**整輪收集共用一個 token、結束時 DELETE 登出**，不每個查詢
重新認證（認證是相對昂貴的操作，2000 台量級下每查詢一次認證即自我 DoS）。token 過期
（401/403）由 `SentinelClient` 統一攔截：重新認證一次後重放原請求，仍失敗才報錯。

## 3. 事件查詢：event-search job 生命週期

Sentinel 的事件查詢是**非同步 search job**，不是同步 query：

| 步驟 | 呼叫 | 說明 |
|---|---|---|
| 建立 | `POST /SentinelRESTServices/objects/event-search`（201 Created，回 `@href`） | body 見下 |
| 查狀態 | `GET /SentinelRESTServices/objects/event-search/{id}`（或 event-search-status） | `status`：0 Pending / 1 Running / 2 Completed / 3 CompletedWithErrors / 4 Unavailable / 5 Canceled / 6 AccessDenied；`found`＝符合總數、`avail`＝目前可取數、`results`＝**第一頁結果的 URL** |
| 取結果 | `GET <results URL>` 逐頁 | 每頁 `pgsize` 筆，跟隨回應中的下一頁連結 |
| 清理 | `DELETE /SentinelRESTServices/objects/event-search/{id}` | **用完即刪**，不留 job 佔用 server 資源 |

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

- `start` 含、`end` 不含——日切界用「當地日 00:00 轉 UTC」到「翌日 00:00 轉 UTC」，不會重複
  也不會漏。
- `type` 用 `USER`；並填 `init-user`/`ip`/`InitiatingHostName` 表明身分——Sentinel 管理端的
  「Active Searches」畫面看得到是誰在查，SIEM 管理者可辨識、可管理。

**沒有 GROUP BY**：公開 REST API 只有 search job，沒有伺服器端聚合（GROUP BY／facet 不在
公開端點中）。本專案的取數策略是 **watchlist Lucene 篩選（server 端先過濾掉多數事件）＋
欄位投影（每筆只回必要欄位）＋分頁拉回本地計數聚合**，聚合的 CPU/記憶體成本由取數端主機承擔。

### 3.4 網段範圍掃描（主機探索）

**目的**：發現一個網段裡有哪些主機在向這台 Sentinel 回報，供 NetIQ 匯入精靈勾選登錄。
（ESM `/objects/eventsource` 才是「已註冊主機目錄」的正解，但本環境的探索帳號被 401/403
拒絕——那是**權限**問題不是 API 不存在，見 docs/archive/HISTORY.md 2026-07-29 第二輪 probe。）

**2026-08-06 涵蓋保證改版**（docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §三）。
改版前用「自適應窗口」控制取回筆數：事件越多、掃描窗口越短（下限曾是 5 分鐘），
而被裁掉的時間裡安靜主機的少數幾筆事件一併消失，**畫面上沒有任何跡象**。
那是靜默漏機。現行設計把窗口固定在 24 小時，改用下面兩件事控制成本：

**(1) 窄化 filter（成本結構的改變）**

```
(repip:{prefix}.* AND (rv150:System OR rv150:Application))
```

第三輪 probe 實測單台主機日量約 31 萬筆，其中 **Security 佔 99.95%**
（System=3、Application=152）。排除 Security 後每台主機只貢獻約 **155 筆/日**——
取回量因此**從正比於「事件量」變成正比於「主機數」**。探索要的本來就是
「每台主機至少一筆事件」而不是「所有事件」。

**(2) 殘差輪掃（上限觸頂時的收斂機制）**

單輪取回觸頂（`found` > 實際取回）時，把**已發現的主機**排除後重查：

```
(repip:{prefix}.* AND (rv150:System OR rv150:Application)) AND NOT (repip:a OR repip:b …)
```

每輪取回的事件全部來自「還沒見過的主機」，新主機數嚴格遞增、殘差嚴格遞減，收斂是必然的。
上限：5 輪、排除子句 500 個（Lucene 預設 `maxClauseCount`=1024，留一半餘裕）。

**三段合起來**：

| 段 | filter | 窗口 | 單輪上限 | 職責 |
|---|---|---|---|---|
| 主掃描 | 窄化（System/Application） | 24 小時 | 50,000 | 涵蓋「一天內有低量頻道事件」的主機 |
| 補充掃描 | 全事件，**排除主掃描已見** | 60 分鐘 | 10,000 | 撈 Security-only 主機——短窗對 Security 涵蓋率天生高 |
| 殘差輪 | 各段自己的 filter＋NOT 已見 | 同上 | 同上 | 觸頂時收斂，主掃描 ≤5 輪、補充 ≤2 輪 |

**涵蓋保證**：掃描結束時，(a) 近 24 小時內有 ≥1 筆 System/Application 事件的主機、
(b) 近 60 分鐘內有 ≥1 筆任何頻道事件的主機——**保證入列，或明確警告「清單可能不完整」**。
兩個窗口都完全沒講話的主機是事件掃描原理上看不到的（資料源極限，只有 ESM 目錄能覆蓋），
`CoverageNote` 永遠把這件事講明。結果只有三種：完整、顯性警告、顯性失敗——
**沒有第四種「靜默漏掉」**。

**重掃增量**：精靈重掃已有登錄主機的網段時，把該 Sentinel 轄下、IP 落在此網段、
且乾淨登錄（非孤兒、非合併）的主機當 `knownIps` 一併排除——沒有新主機時 `found=0`，
一次查詢就結束。孤兒主機**刻意不排除**：它出現在掃描結果裡是「又活過來了」的訊號。

**⚠ `NOT` 子句尚未在本環境實測**（probe 驗過 OR 50~100 子句、片語、前綴萬用字元，
沒驗過 NOT）。實作加了偵測：任一輪取回的事件若含已排除的 `repip`，即判定排除未生效，
當場停止輪掃並顯性警告。試點時應核對此項（見 docs/BACKLOG.md）。

## 4. 欄位對應（Windows / AD 事件，本環境實測定案）

| 語意 | 欄位 | 備註 |
|---|---|---|
| Windows EventID | `rv40` | 以 4634／4624／4771／4627 多筆交叉實證（配 `evt` 中文事件名一致） |
| 事件來源（provider） | `obssvcname` | 值如 `Microsoft-Windows-Security-Auditing`，`SourcePattern` 比對對象。**是 term 欄位、不斷詞**——完整片語查詢可用（found 有效），部分詞查詢 found=0，因此 `SourcePattern` 的子字串比對**不能**下推 Lucene，來源比對留在本地 `Classify`（本來就是權威判定） |
| 頻道（LogName） | `rv150` | 值如 `Security`／`System`／`Application` |
| **主機歸屬鍵** | **`repip`** | **這是「這筆 log 屬於哪台主機」的鍵**——多台主機對到各自不同的 `repip`，一對一、非共用代理 |
| 主機名 | `sn`／`dhn` | `sn`＝記錄這筆 log 的主機自己（觀察者，與 `repip` 成對，`DisplayName` 回填用它）；`dhn`＝目的地主機 |
| 用戶端 IP | `sip` | **不是主機自身**——是發起連線的遠端來源（同一台主機的多筆登入事件可有不同 `sip`） |
| 發起端機器名 | `shn` | 跨主機認證事件才出現（登出事件不帶），關聯層可用 `sun`/`sip`/`shn` 結構化欄位而不必解析 msg 文字 |
| 時間 | `dt` | ISO-8601 UTC；`estz` 為事件來源時區 |
| 嚴重度 | `sev` | 0～5；已確認 0＝Security 成功稽核、1＝Information、4＝稽核失敗（如 Kerberos 4771）。**Warning／Error 的確切門檻（候選 2／3）尚未實證**，`SentinelFieldMap.MapEntryType` 已標註候選門檻，待試點環境有真實 Error 樣本後核對；不影響規則命中（規則比對 Source＋EventId，與 EntryType 無關） |
| 訊息／事件名 | `msg`／`evt` | 皆為繁中，AI 層與報告可直接使用；`msg` 已投影在主查詢內，不需要另外查範例訊息 |
| 帳號 | `sun`／`dun` | 帳號名（已見具名帳號、員工編號式帳號、`-`＝無）。`iuid`／`tuid` 為 SID |
| OS／collector 判別 | `pn`／`agent`／`port` | 三者同值（如 `Microsoft Active Directory and Windows`）；本專案改採「OS 判別交由 Sentinel 層級」（見 docs/LINUX-RULES.md §主機 OS 標記），不逐事件判別 |
| System／Application 頻道覆蓋 | — | collector 確實會轉送這兩個頻道的事件（含 Information 級），但量遠小於 Security（同一主機可能是千分之一量級）——這是 collector 轉送策略本身如此，不是被過濾 |

`SentinelFieldMap`（設定可覆寫的字典，per-server 覆寫保留為保險）承載上表對應，`--sample-ip`
一類的驗證查詢輸出可直接核對此表是否仍成立。

## 5. 查詢 payload（`SentinelQueryBuilder.BuildWindowsFilter`）

```jsonc
// Q1（每 IP 批次一個 job，實際欄位名）
{
  "filter": "(repip:10.1.2.11 OR repip:10.1.2.12 OR …) AND ((rv40:(4625 OR 4720 OR …)) OR ((rv150:System OR rv150:Application) AND sev:[2 TO 5]))",
  "start": "<當地日00:00→UTC>", "end": "<翌日00:00→UTC>",
  "fields": "repip,sn,rv40,obssvcname,rv150,dt,sev,msg,evt,sun,xdasoutcome",
  "pgsize": 500, "max-results": 100000, "type": "USER",
  "init-user": "svc-lfquery", "InitiatingHostName": "<查詢端主機名>"
}
```

- `rv40` 聯集只含**有明確 EventId 的 Windows 規則**；`MatchAllEventIds` 規則（WHEA-Logger／
  Resource-Exhaustion／VSS，皆為 System 來源）沒有具體 ID 可下推，靠 generic 分支撈進來後
  本地 `Classify` 精準比對。
- Security 頻道**沒有**獨立分支：規則命中的 Security 事件由 `rv40` 聯集涵蓋（種子規則本來就是
  為高價值 Security EventId 寫的），未被任何規則覆蓋的「未知失敗 ID」目前不會被撈入——
  這是相對本機模式（FailureAudit 不論 ID 全收）的已知涵蓋縮小；若要補上需要靠 `xdasoutcome`
  （見上表）組出 `NOT xdasoutcome:0` 之類的分支，可行性尚待驗證（見 docs/BACKLOG.md）。
- watchlist Lucene 字串由 `KnownIssueCatalog.Rules` **程式生成**（`SentinelQueryBuilder.cs`），
  規則已外部化，watchlist 推導與規則庫同步；規則表改了，產生的子句自動跟著對（見
  `RunSentinelQueryChecks` 測試）。
- IP 篩選以批次子句組成（`repip:ip1 OR repip:ip2 OR …`）；10/50/100 個 IP 子句皆可正常查詢
  （耗時與子句數無明顯相關），批次大小由 `NetiqOptions` 節流參數控制。
- 片語查詢（`obssvcname:"…"`）需注意 JSON 轉義：`System.Text.Json` 預設編碼器會把非 ASCII
  字元寫成 `\uXXXX`，但 Sentinel 的 JSON 解析器不接受這種轉義序列，會導致片語查詢整個被
  400 拒絕。`SentinelClient` 已改用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  （`SentinelClient.JobBodyJsonOptions`）避開此問題。

## 6. 降低 Sentinel 負擔的措施

| # | 措施 | 為什麼有效 |
|---|---|---|
| 1 | **watchlist 先在 server 端過濾**（Lucene filter） | 只索引查詢命中事件，不全量拉回 |
| 2 | **欄位投影**（fields） | 每筆只回必要欄位，序列化與傳輸成本降一個數量級 |
| 3 | **單一併發 per Sentinel** | 任一時刻每台最多 1 個查詢在跑；不與現場操作人員搶資源 |
| 4 | **跨 Sentinel 平行** | 不同 Sentinel 是獨立系統，平行不增加單台負擔 |
| 5 | **夜間窗執行** | 避開日間互動查詢尖峰 |
| 6 | **token 重用＋登出** | 整輪一次認證；不製造認證風暴、不留殭屍 session |
| 7 | **job 用完即 DELETE** | 不佔用 server 的 search job 資源與快取 |
| 8 | **`QueryDelayMs` 節流** | 呼叫間隔可調；哪台 Sentinel 反映負載即可單獨放慢 |
| 9 | **`max-results` 安全閥** | 異常爆量日不無限制拉取；截斷誠實標 `DataIncomplete` |
| 10 | **增量收集** | 已分析日永不重查；每天只查該查的日子 |
| 11 | **退避重試（Polly）** | 503＝server 忙，指數退避讓路而不是重錘 |
| 12 | **`type:USER`＋表明身分** | SIEM 管理者在 Active Searches 看得到、可管理可取消 |

## 7. 設定（`NetiqOptions`，Web「NetIQ 維護」頁維護）

節流／行為參數（各處皆經 `NetiqOptionsStore`——DB blob `netiq_options`——讀同一份）：
`QueryDelayMs`／`PageSize`／`MaxResultsPerJob`／`TimeoutSeconds`／`RetryCount`／
`AllowInvalidCertificates`（自簽憑證環境的顯式逃生門，啟用即 WARN）。連線帳密另存
`ISentinelStore`（每台 Sentinel 各自一組，密碼以 `CryptoHelper` AES-256 加密）。
