# NetIQ Sentinel 取數 API 參考

> 除非必要否則不要讀取 docs/archive/ 內容，避免浪費 token。
>
> 本文件是 Sentinel REST API 的現況技術參考（認證、事件查詢、欄位對應、查詢 payload）——
> 公開的 Sentinel REST API 文件（7.0/8.2 版）未涵蓋事件查詢結果頁的確切 JSON 結構，
> 下列欄位對應是本環境（8.5）以 NetIQ 維護頁「診斷」分頁的驗證查詢核對後的定案結果。
> 換一套 Sentinel 環境時，仍應以現場 `apidoc`（`https://<sentinel>:8443/SentinelRESTServices/apidoc/`）
> 與 Web UI「Tips」頁為準，本文件的欄位對應是本環境的實測結果，不是原廠保證的通用契約。
> 實作見 `LogForesight.Core/SentinelClient*.cs`（四個 partial：本體、`.Auth`、`.Search`、`.Paging`）與
> `LogForesight.Core/Analysis/` 的 `SentinelFieldMap.cs`、`SentinelEventMapper.cs`、`SentinelQueryBuilder.cs`。
> 取數管線的整體設計（機房 pipeline、網段掃描探索、節流參數）見 docs/WEB-SPEC.md §9.9a；
> 欄位對應的實測過程與尚未核對的開放項見 docs/BACKLOG.md。

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
（401/403）由 `SentinelClient` 統一攔截：先 best-effort 登出舊 token（5 秒逾時，失敗只記 Debug、不影響重新認證，避免殘留 session），
再重新認證一次並重放原請求，仍失敗才報錯。

## 3. 事件查詢：event-search job 生命週期

Sentinel 的事件查詢是**非同步 search job**，不是同步 query：

| 步驟 | 呼叫 | 說明 |
|---|---|---|
| 建立 | `POST /SentinelRESTServices/objects/event-search`（201 Created，回 `@href`） | body 見下 |
| 查狀態 | `GET /SentinelRESTServices/objects/event-search/{id}`（或 event-search-status） | `status`：0 Pending / 1 Running / 2 Completed / 3 CompletedWithErrors / 4 Unavailable / 5 Canceled / 6 AccessDenied；`found`＝符合總數、`avail`＝目前可取數、`results`＝**第一頁結果的 URL**。Completed 與 CompletedWithErrors 都視為終態並照常取結果（`State` 帶在結果上，目前沒有消費端、不改變成功判定）；Unavailable／Canceled／AccessDenied 擲 `SentinelClientException`；未知或缺席的狀態當作仍在跑 |
| 取結果 | `GET <results URL>` 逐頁 | 每頁 `pgsize` 筆；頁碼由本程式自算——把 results URL 的 `page=` 參數依序改為 1、2、3…（`SetPageQueryParam`），不依賴未文件化的下一頁連結；空頁、不足一頁或已取回 `min(found, max-results)` 筆即停 |
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
- `type` 用 `USER`；並填 `init-user`（探索帳號）與 `InitiatingHostName`（查詢端主機名）表明身分——Sentinel 管理端的
  「Active Searches」畫面看得到是誰在查，SIEM 管理者可辨識、可管理。
- **逾時有兩層**：`TimeoutSeconds` 同時是單次 HTTP 呼叫的逾時，也是 job 建立後輪詢至終態的完成期限（超過擲「查詢逾時」）；翻頁迴圈不受它約束。
- **取不回的結果**：`found` > 0 但結果頁一筆都取不回（讀不到或解析不出）時 `SentinelSearchResult.Unreadable` 為 true，pipeline 將該批記為失敗，
  不當成截斷、不二分重查；取回數 < `found` 才是 `Truncated`，pipeline 才二分重查並在收斂到單台後誠實標記資料不完整。

**沒有 GROUP BY**：公開 REST API 只有 search job，沒有伺服器端聚合（GROUP BY／facet 不在
公開端點中）。本專案的取數策略是 **watchlist Lucene 篩選（server 端先過濾掉多數事件）＋
欄位投影（每筆只回必要欄位）＋分頁拉回本地計數聚合**，聚合的 CPU/記憶體成本由取數端主機承擔。

### 3.4 網段範圍掃描（主機探索）

**目的**：發現一個網段裡有哪些主機在向這台 Sentinel 回報，供 NetIQ 匯入精靈勾選登錄。
（ESM `/objects/eventsource` 才是「已註冊主機目錄」的正解，但本環境的探索帳號被 401/403
拒絕——那是**權限**問題不是 API 不存在，詳見 docs/archive/HISTORY.md「第二輪 probe」一節。）

**涵蓋保證改版**（詳見 docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §三）。
改版前用「自適應窗口」控制取回筆數：事件越多、掃描窗口越短（下限曾是 5 分鐘），
而被裁掉的時間裡安靜主機的少數幾筆事件一併消失，**畫面上沒有任何跡象**。
那是靜默漏機。現行設計把窗口固定在 24 小時，改用下面兩件事控制成本：

**(1) 窄化 filter（成本結構的改變）**

```
(repip:{prefix}.* AND (rv150:System OR rv150:Application))
```

probe 實測單台主機日量約 31 萬筆，其中 **Security 佔 99.95%**
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
**排除清單依當前分段過濾**（見下方 (0)）——只帶這一段實際查詢範圍內已見的 IP，
不是整趟已見的全部；分段後單段最多 254 台，500 子句上限實務上不再是限制條件，
`ExclusionClauseLimit` 保留為防禦性的第二道閘門。NOT 未生效偵測也只比對該段的排除集合。

**(0) 網段分割（先於上述三段）**

使用者實務上輸入的是 `192.168` 這種 /16 級前綴。整段當成一次查詢送出時，
單輪 50,000 筆的上限會被少數吵雜主機壟斷（單台日量可達 31 萬筆），安靜主機擠不進來；
殘差排除也會逼近 500 子句上限。因此**寬前綴會自動展開成 /24 逐段掃描**
（`SentinelQueryBuilder.ExpandToSegments`），已見主機集合跨段共享。

掃描粒度可選（單次掃描生效，非持久設定）：

| 粒度 | 每段台數 | 位址子句表達方式 | 每個 /24 拆成 |
|---|---|---|---|
| `/24`（預設） | 254 | 前綴萬用字元 `repip:192.168.7.*` | 1 段 |
| `/26`（最細） | 62 | **逐 IP 明列 OR**（64 個位址子句） | 4 段 |

**為什麼 /26 不能用萬用字元或範圍查詢**：`.0`~`.63` 沒有共同的字串前綴，
而 Lucene 的範圍查詢是**字典序**——點分十進位在字典序下會亂序，不可用。
唯一可行的是逐 IP 明列 OR。

**刻意不提供 /25**：一次要送 128 個位址子句、加上段內排除最多再 128 個＝256 子句，
遠超本環境 probe 實證過的 OR 50~100 個；而拆成兩個 64 子句的查詢送出後，
它與 /26 在查詢層面完全等價，多這個選項只會讓人以為有第三種涵蓋粒度。

⚠ **子句數申報**：/26 為 64 個位址子句＋最多 64 個排除子句＝最多 128 個 OR/NOT 子句。
Lucene 預設上限 1024，結構上安全，但**本環境只實證過 50~100 個**，屬試點核對項
（被拒的症狀是背景工作 failed，訊息來自 Sentinel）。

分段數上限 **512**（`MaxSegments`）：`/16`×`/24`＝256 ✓、`/16`×`/26`＝1024 ✗ 被擋下
並提示改用較粗粒度或縮小範圍。細粒度的設計用途是「已知某個 /24 特別吵時針對它掃」，
不是拿來掃整個 /16。

**(3) 提早停頁**

探索要的是相異主機 IP，不是把事件翻完。單輪翻頁中**連續 3 頁沒有出現沒見過的主機就停止翻頁**
（`NoNewHostPagesBeforeStop`）。動機：一輪 50 頁 × 實測約 1.7 秒 ≈ 85 秒，
會吃光整趟預算、讓殘差輪掃永遠跑不完。

**提早停頁不等於已涵蓋**——後面的頁仍可能有沒見過的主機，所以該輪**仍視為觸頂**
（`SentinelSearchResult.Truncated` 的定義是「取回數 < found」，提早停頁時自然成立）
並繼續進殘差輪，涵蓋保證不受影響。

**時間預算**：掃描是**背景工作**（見 docs/WEB-SPEC.md §9.9a），總預算 900 秒。
多段情境下預算用盡時**回傳已完成分段的結果**並在警告中列出未掃描的網段
（分段的部分結果有明確邊界，不像單段的半套清單那樣無從解釋）；
單段情境維持既有行為——擲例外、不回半套。

**三段合起來**（以下每一段都在**單一分段**內執行）：

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
一次查詢就結束。已登錄主機清單**不設全域總數上限**（2000 台規模仍排除），只在送出前依當前分段過濾；
單段內排除超過 `ExclusionClauseLimit` 時，第一輪退回不排除照掃、後續輪次停止並警告。孤兒主機**刻意不排除**：它出現在掃描結果裡是「又活過來了」的訊號。

**⚠ `NOT` 子句尚未在本環境實測**（probe 驗過 OR 50~100 子句、片語、前綴萬用字元，
沒驗過 NOT）。實作加了偵測：任一輪取回的事件若含已排除的 `repip`，即判定排除未生效，
當場停止輪掃並顯性警告。試點時應核對此項（見 docs/BACKLOG.md）。

### 3.5 ESM 事件來源目錄（per-Sentinel 開關，預設關）

`GET /SentinelRESTServices/objects/eventsource`（一般 REST 資源，**不走** event-search
job 生命週期，以 `SentinelClient.RawGetAsync` 取得）。

這才是探索的正解：一次唯讀查詢拿到**已註冊主機的完整清單**，包含目前完全沒在回報的
主機——那正是 §3.4 事件掃描原理上看不到、而本系統「沒查 ≠ 沒事」最在意的一類。

**但本環境的探索帳號被 401/403 拒絕**，因此：

| 決定 | 理由 |
|---|---|
| 做成 per-Sentinel 開關 `Sentinel.UseEsmDirectory`，**預設 false** | 不同 Sentinel 的帳號權限本來就可能不同；沒有權限的環境完全無感 |
| **不自動嘗試**（不做「每次先試 ESM、失敗退路」） | 回應格式在本環境無法驗證，自動信任一個沒驗證過的解析結果，錯了會讓主機清單靜默變形 |
| 解析器防禦性實作（`SentinelEsmDirectory`） | 候選欄位名依公開 7.0 apidoc 與物件慣例列舉，**未經實測**；成功閘門＝至少解析出一個合法 IPv4 |
| 開啟前的驗證放在**人的流程** | 「診斷」分頁步驟 6 會打這個端點並印出回應——先看得到清單再開開關 |

**失敗一律退回 §3.4 的事件掃描，且一定發警告**（開關是人開的，開著卻每次走退路
代表這個環境不支援，訊息要讓人能決定「關掉開關」或「把回應貼回來定案」）：

| 情況 | 行為 |
|---|---|
| 401/403 | 警告「此帳號讀不到目錄…請確認具備 ESM 唯讀權限，或關閉此開關」＋事件掃描 |
| 200 但解析不出任何主機 | 警告「格式與預期不符（收到 N 筆條目）…請至診斷分頁執行並回報步驟 6 的輸出」＋事件掃描。**刻意不當成「這台 Sentinel 沒有主機」**——那會讓管理員以為機房空了 |

**拿到真實輸出之後**：把樣本存成測試 fixture、依實際欄位收斂候選清單、更新本節——
防禦版才算轉正。

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

## 4a. 欄位對應（Linux syslog，多輪 probe 實證定案，詳見 docs/archive/FEEDBACK-12-PLAN.md §4.0，已實作）

Sentinel「118_linux」，https://10.xx.7.118:8443，經多輪診斷。**欄位形狀與
filter 內容子句／`sev` 門檻皆已定案並實作**（`SentinelFieldMap`／`SentinelEventMapper`／
`SentinelQueryBuilder.BuildLinuxFilter`，批 4B）：

| 語意 | 欄位 | 實證依據 | 定案 |
|---|---|---|---|
| program | `sp` | 每筆事件帶 `sp=systemd`／`NetworkManager`／`kernel`／`sshd` 等；`msg` 也以 `program:` 或 `program[pid]:` 前綴開頭；exact term、**大小寫不敏感**、**支援前綴萬用字元**（輪 B：`sp:networkmanager`＝`sp:NetworkManager`；`sp:user*` 有效） | **`Program`＝`SentinelFieldMap.LinuxProgram`（`sp`）**；`SentinelEventMapper` 的 Source 三段 fallback 鏈：`sp`→`obssvcname`→`msg` 前綴正則解析 |
| 主機歸屬鍵 | `repip` | 與 Windows 同一輪已實證的回報者 IP 欄位 | 與 Windows 同為 `repip`，`BuildIpClause` 直接重用，不需要 Linux 專用版本 |
| 主機名 | `sn` | `sn=stkomsdb1`／`VM-NATFA02`（回報主機自身名） | 沿用，`DisplayName` 回填照舊 |
| 正規化事件名 | `evt` | 值恆為樣板字串 `"NetIQ Universal Event {program} Event"`（或 CEF 路徑的 `"Universal Common Event Format {program} Event"`），資訊量＝program 本身，無正規化語意 | **不使用**；seed 的 `EventNamePattern` 定案維持留空（Web 端仍可維護，等未來接到有正規化 collector 的環境再啟用） |
| collector 形態 | `pn`／`agent`／`port`／`rt2`／`obssvcname` | **per-collector，不是 per-Sentinel**：同一台 Sentinel 上見過兩種收集路徑——(a) 主流路徑「NetIQ Universal Event」＋Full Text Parser，`sp` 存在、`obssvcname` 不存在；(b) 少量「Universal Common Event Format」（CEF）路徑（第二次 probe 實證，SOAR 設備自身的 conmon 日誌），`sp` 缺席、program 落在 `obssvcname`。兩種路徑 `sun`／`sip`／`dhn`／`rv40` 皆不存在 | 泛用 syslog collector＋全文解析，`msg` 是未結構化原始 syslog 行；`LinuxQ1ProjectionFields` 同時投影 `sp` 與 `obssvcname` 因應兩種路徑；受監控主機（非 collector 自身）目前實測全走 (a) 路徑，filter 的 `sp:{program}*` 下推安全（見下方「量級」列）；4C 的帳號級關聯只能靠 `msg` 文字解析（見 docs/LINUX-RULES.md「關聯層」） |
| facility | `rv150` | `rv150=DAEMON`／`KERNEL`／`USER`（大寫 facility——同名欄位在 Windows 上是頻道名） | 投影帶回但不參與比對；`LogName` 固定 `"Linux"` |
| 時間 | `dt` | ISO-8601 UTC（`estz=Asia/Taipei` 佐證時區基準） | 與 Windows 同一條解析路徑 |
| 嚴重度 | `sev` | 分佈實測（全站/24h）：0=1.87M、1=7.67M、2=8、3=972、4=1,403、5=20。**不承載 syslog priority 語意**——NetworkManager 的 `<warn>` 與 dockerd 的 `level=error` 皆落在 sev=1，「pam session opened」反落在 sev3-5 | `SentinelFieldMap.MapEntryTypeLinux`：`0~1→Information、2→Warning、3~5→Error`（計數用途的務實選擇，不影響規則比對）；generic 收集門檻 `sev:[2 TO 5]`（`SentinelQueryBuilder.LinuxGenericSeverityMin`） |
| program 量級 | `sp` | 吵：systemd 1.96M／kernel 305k／sshd 244k／sudo 219k／su 52k 筆/日；靜：chronyd 3.4k／CRON 2.7k／auditd 112／smartd 29／帳號異動類 ≤7 筆/日 | `LinuxNoisyPrograms` 常數集（sshd/sudo/su/kernel/systemd）帶 `MessagePatterns` 下推控量，其餘整拉（見 §4a-1） |
| msg 片語 | `msg` | `"Failed password"`／`"I/O error"`／`"authentication failure"` 等片語查詢皆有效（含斜線）；欄位群組多片語語法有效；吵 program＋片語組合下推有效（`sp:systemd AND msg:"entered failed state"` 把 1.96M/日壓到 1 筆/日） | filter 內容子句採 program／msg 混合下推，見 §4a-1 |
| sshd 暴力破解樣本 | `msg` | 「`Failed password for invalid user {user} from {ip} port {port} ssh2`」，無 program 前綴、`invalid user` 為可選段；來源含內網與外網 IP，環境有真實暴破流量 | 4C `LinuxCorrelationAnalyzer` 的 regex 依此定案（見 docs/LINUX-RULES.md「關聯層」） |
| Windows 事件 | `rv40` | `rv40:(4624 OR 4625)` found=0 | 純 Linux Sentinel，證實「同台不混平台」環境事實 |
| ESM 目錄 | — | 驗證被拒（與 Windows 那台相同） | 主機探索照舊走事件投影 distinct 備案 |
| 批次/分頁 | — | 100 個 IP 子句接受（~1.7s）；pgsize 1000 於 833ms | 批次機制照 Windows 沿用，`IpBatchSize` 維持 50 共用（總量評估：檢索面全站 <1 萬筆/日，遠低於截斷線） |
| 環境觀察 | — | Sentinel 自家 Syslog_UDP connector 在丟訊息（`"Dropped 29,623 messages so far"`） | 來源端完整性不保證——我方無從逐主機偵測這種丟失，屬環境層事實，排查「主機明明有事件卻查不到」時可留意 |

**診斷分頁（`NetiqProbeRunner`）Linux 深掘步驟（docs/archive/FEEDBACK-12-PLAN.md §4.1／4B.0）**：
步驟 8 樣本數 3→10＋欄位名聯集；8b（`msg` 全文不截斷）、8c（`sp` 查詢行為）、
8d（`sev` 分佈＋樣本全文）、8e（種子 program 量級）、8f（`sshd` 樣本全文）、
8g（`msg` 片語查詢行為＋暴破樣本），皆掛在「有填 Linux 樣本 IP」同一個開關下，
是上表全部實證的直接來源。

### 4a-1. `BuildLinuxFilter` 內容子句（`SentinelQueryBuilder`，批 4B 實作）

`{IP 批次} AND ({規則子句聯集} OR sev:[2 TO 5])`——規則子句依 program 分組，
吵 program（`LinuxNoisyPrograms`：sshd/sudo/su/kernel/systemd）帶該 program 全部規則
`MessagePatterns` 聯集下推（`(sp:{p}* AND msg:("片語1" OR "片語2" …))`，片語跳脫雙引號／
反斜線防語法破壞），其餘 program 整拉（`sp:{p}*`，量級夠小，整拉也順便避開片語標點的
殘餘風險——chronyd「Can't synchronise」的撇號、CRON「(CRON) ERROR」的括號）。

## 5. 查詢 payload（`SentinelQueryBuilder.BuildWindowsFilter`）

```jsonc
// Q1（每 IP 批次一個 job，實際欄位名）
{
  "filter": "(repip:10.1.2.11 OR repip:10.1.2.12 OR …) AND ((rv40:(4625 OR 4720 OR …)) OR ((rv150:System OR rv150:Application) AND sev:[2 TO 5]))",
  "start": "<當地日00:00→UTC>", "end": "<翌日00:00→UTC>",
  "fields": "repip,sn,rv40,obssvcname,rv150,dt,sev,msg,evt,sun,xdasoutcome,shn,sip",   // 13 欄＝SentinelFieldMap.Q1ProjectionFields
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
  （耗時與子句數無明顯相關），批次大小為 `IpBatchSize`（50，Windows／Linux 共用）。
- **只放行合法 IPv4**（`SentinelQueryBuilder.IsValidIpv4`：四段、每段 1–3 位數字且 0–255）：位址會原樣插進 Lucene，一個壞值（IPv6、簡寫、亂字串）會讓同批所有主機被 Sentinel 以 400 整批拒絕。
  `BuildIpClause` 濾掉不合法者、全部不合法時擲 `ArgumentException`；pipeline 在計畫階段就把位址不是 IPv4 的主機排除，逐台警告「位址 X 不是 IPv4，未查詢」並標記未完整；
  作業系統標記不是 `windows`／`linux` 的主機同樣逐台警告、不查詢。這些主機不進進度分母，也不計入「已完成跳過」（那個計數的語意是已有分析紀錄）。
  Web 現場取數（詢問 AI）對單台查詢位址不是 IPv4 時回空查詢（視為查不到），不擲例外。
- 片語查詢（`obssvcname:"…"`）需注意 JSON 轉義：`System.Text.Json` 預設編碼器會把非 ASCII
  字元寫成 `\uXXXX`，但 Sentinel 的 JSON 解析器不接受這種轉義序列，會導致片語查詢整個被
  400 拒絕。`SentinelClient` 已改用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  （`SentinelClient.JobBodyJsonOptions`）避開此問題。

## 6. 降低 Sentinel 負擔的措施

| # | 措施 | 為什麼有效 |
|---|---|---|
| 1 | **watchlist 先在 server 端過濾**（Lucene filter） | 只索引查詢命中事件，不全量拉回 |
| 2 | **欄位投影**（fields） | 每筆只回必要欄位，序列化與傳輸成本降一個數量級 |
| 3 | **每個 client 單一併發；單台 Sentinel 內的平行度有上限** | 一個 `SentinelClient` 實例同一時間只有 1 個 job；`MaxParallelQueriesPerServer`（預設 1＝完全依序，上限 4）才會替同一台 Sentinel 建 client pool、讓同一天的主機批次同時開多條查詢；不與現場操作人員搶資源 |
| 4 | **跨 Sentinel 平行** | 不同 Sentinel 是獨立系統，平行不增加單台負擔；`MaxParallelServers` 預設 2、上限 3 |
| 5 | **夜間窗執行** | 避開日間互動查詢尖峰 |
| 6 | **token 重用＋登出** | 整輪一次認證；token 過期重新認證前先登出舊 token；不製造認證風暴、不留殭屍 session |
| 7 | **job 用完即 DELETE** | 不佔用 server 的 search job 資源與快取 |
| 8 | **`QueryDelayMs` 節流** | 呼叫間隔可調；哪台 Sentinel 反映負載即可單獨放慢 |
| 9 | **`max-results` 安全閥** | 異常爆量日不無限制拉取；截斷誠實標 `DataIncomplete` |
| 10 | **增量收集** | 已分析日永不重查；每天只查該查的日子 |
| 11 | **退避重試（Polly）** | 503＝server 忙，指數退避讓路而不是重錘 |
| 12 | **`type:USER`＋表明身分** | SIEM 管理者在 Active Searches 看得到、可管理可取消 |

## 7. 設定（`NetiqOptions`，Web「NetIQ 維護」頁維護）

節流／行為參數（各處皆經 `NetiqOptionsStore`——DB blob `netiq_options`——讀同一份；儲存時的範圍由 `UpdateNetiqOptionsRequest` 驗證）：

| 設定 | 預設 | 範圍 | 說明 |
|---|---|---|---|
| `QueryDelayMs` | 0 | 0–60,000 | 每次 REST 呼叫（建立／輪詢／翻頁）之間的節流間隔 |
| `PageSize` | 500 | 1–5,000 | event-search job 的單頁筆數（`pgsize`） |
| `MaxResultsPerJob` | 100,000 | 1–10,000,000 | 單一 job 最多回傳筆數（`max-results`），超過標記截斷 |
| `TimeoutSeconds` | 120 | 1–3,600 | 單次 HTTP 逾時，同時是 job 輪詢至終態的完成期限（§3） |
| `RetryCount` | 3 | 0–20 | Polly 重試：503／逾時／網路錯誤重試，4xx 不重試 |
| `AllowInvalidCertificates` | false | — | 自簽憑證環境的顯式逃生門，啟用即 WARN |
| `BackfillDays` | 1 | 1–365 | 每台主機每次執行最多回補幾天；實際上限＝歷史資料保留天數（不超過 365）；首次與非首次一律套用同一值，缺漏日不自動自癒 |
| `MaxParallelServers` | 2 | 1–3 | 同時處理幾台 Sentinel；1＝完全依序；上限是行程架構常數，既有存值超過時讀取夾住 |
| `MaxParallelQueriesPerServer` | 1 | 1–4 | 單台 Sentinel 內同時開幾條批次查詢（client pool 大小）；與 `MaxParallelServers` 的乘積決定夜間分析的 DB 連線池上限 |
| `ChatLiveFetchEnabled` | false | — | 詢問 AI 是否向 Sentinel 即時查詢現場事件（只對 NetIQ 主機；白天查詢負載須管理者顯式開啟） |
| `UseOfflineDemoData` | false | — | 掃描匯入使用離線示範資料；僅非 Production 可開 |

連線帳密另存 `ISentinelStore`（每台 Sentinel 各自一組，密碼以 `CryptoHelper` AES-256 加密）。

## 8. 來源佐證（SourceEvidence）與欄位形狀探測

**來源佐證**：`SentinelEventMapper` 為每筆映射後的事件附一份 `SourceEvidence`（`LogForesight.Core/Models/SourceEvidence.cs`），供跨來源（Sentinel 事件 × PRTG）弱佐證判定用：
- `SourceKind`＝`SentinelWindows`／`SentinelLinux`；`EventTimeUtc`＝`dt`；`ResourceScope`＝`repip` 能解析成 IP 時為 `Host`，否則 `Unknown`。
- `ExactHostKey`／`ExactResourceKey`＝`host-ip:<repip>`；NetIQ pipeline 在**該 IP 對到唯一一台已登錄目標**時，改成正規主機鍵 `host-id:<HostId>`
  （`SourceEvidence.WithCanonicalHost`）；IP 重複的目標維持 `host-ip:` 的弱鍵。
- `SourceReference` 一律空、`SourceReferenceQuality`＝`Unknown`：Sentinel 投影沒有逐筆事件的原生參照，所以 Sentinel 端證據永遠不滿足「精確參照」；
  `ProjectionFingerprint`＝投影欄位（依鍵排序）的 SHA-256，只用來辨識內容，不是可定址的來源參照。
- 同一主機日的觀測清單有界：最多 64 筆、序列化 ≤ 8 KiB，超過標記截斷（`SourceEvidence.BoundObservations`）。

**欄位形狀探測**（NetIQ 維護頁「診斷」分頁的第二個鈕，`POST api/admin/netiq/probe/metadata/start`，需 `Maintain`，寫稽核 `netiq_probe_run`，`Mode=metadata-shape`；帳密未設定的 Sentinel 拒絕啟動）：
- 與一般診斷共用同一個探測執行閘門（已有診斷執行中時拒絕）；由 `NetiqEvidenceMetadataProbeRunner` 執行，有界且不輸出任何值。
- 只送一個查詢：`sev:[0 TO 5]`、近 1 小時、最多 3 筆事件、1 頁、保留全部原始欄位；整體期限 30 秒，HTTP 請求 ≤ 12 次，單一回應 ≤ 512 KiB，欄位鍵 ≤ 128 個，報告 ≤ 32 KiB。
- 報告只列欄位形狀：欄位名稱（僅限安全字元，其餘只計數）、JSON 型別、值長度、是否可解析為時間、是否帶明確時區位移；不含任何欄位值與例外細節，失敗只回固定的原因代碼。
- 用途是核對候選欄位形狀（例如 `dt` 的時區位移）；報告明寫「欄位形狀不等於語意證明，不授權強制比對」。

