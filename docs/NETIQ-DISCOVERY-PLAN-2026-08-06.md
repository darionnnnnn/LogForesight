# NetIQ 主機探索成本改善＋開發環境 console 編碼 修復規劃（2026-08-06）

> **狀態：只規劃，未修改任何程式碼。決策已於 2026-08-06 兩輪回覆定案**（見 §七）：
> ESM 權限現階段要不到 → 甲改為「保留能力、每台 Sentinel 手動開啟」；
> 補充掃描參數依評估採 60 分鐘/10,000 筆；**丁（背景掃描）暫緩不先做**；
> console 修法定案=啟動時指定 UTF-8 即可。
> 第二輪並要求**涵蓋保證**：新方式不得因固定取回筆數漏掉網段內主機——
> 批 1 因此升級為「窄化＋殘差輪掃」版（§三），並否決「飽和早停」提案。
>
> 兩個獨立主題共用一份規劃：(一) NetIQ 探索掃描太耗時且佔用 Sentinel 資源；
> (二) Rider 執行時 console 中文亂碼。兩者互不相依，commit 必須分開。

---

## 一、問題：探索的成本花在錯的東西上

### 現況機制（`SentinelRestDirectoryClient.ListHostsAsync`）

1. 計數查詢：`repip:{prefix}.*`（**全事件、無任何窄化**，
   [SentinelQueryBuilder.BuildSubnetDiscoveryFilter](../LogForesight.Core/Analysis/SentinelQueryBuilder.cs)）
   拿近 24h 的 `found`；
2. 依 `found` 反推掃描窗口：`24h × (50,000 ÷ found)`，下限 5 分鐘；
3. 主查詢在該窗口內最多拉 **50,000 筆事件**（50 頁 × 1,000），
   本地對 `repip` distinct、`sn` 取眾數當顯示名稱；
4. 整趟 90 秒總預算，逾時整個放棄（不回半套）。

### 三個症狀是同一個病

| 症狀 | 機制 |
|---|---|
| **慢** | 最多 50 次連續翻頁，實測單次往返 ~1.7 秒 → 分頁迴圈本身就可能吃掉 85 秒 |
| **佔資源** | 為了幾十～幾百台的清單，要 Sentinel 索引、序列化、傳輸五萬筆事件 |
| **涵蓋不完整** | 事件越多窗口越短——實測網段 57 萬筆/日 → 窗口壓到 ~2 小時；更吵的網段壓到 5 分鐘下限，只剩「現在正在講話的主機」掃得到 |

病根：**成本正比於「事件量」，但我們要的是「主機清單」**——每台主機只需要
至少一筆事件證明它存在，現在卻在下載牠 24 小時講過的每一句話。
90 秒預算與「不回半套」是症狀處理，不是解法。

### 為什麼當初這樣設計（docs/archive/HISTORY.md 2026-07-29 段）

- ESM `/objects/eventsource`（Sentinel 的事件來源目錄，本該是主機清單的正解）
  被 **401/403 拒絕**——探索帳號有 event-search 權限、無 ESM 物件讀取權限；
- 全站 24h distinct 在 2,470 萬筆/日下不可行。

兩條路堵死才退到「網段事件掃描」。**關鍵：ESM 是被權限擋住，不是 API 不存在**——
這是可以去談的，不是技術死路。

---

## 二、方案總覽與順序

| 方案 | 一句話 | 前置條件 | 定案處置（2026-08-06，兩輪） |
|---|---|---|---|
| **乙：filter 窄化＋殘差輪掃** | 低量頻道當探針＋server 端排除已見主機——成本正比主機數，且**上限觸頂不再漏機**（涵蓋保證，§3.0） | 無 | **第 1 批** |
| **手段二：重掃已知排除** | 重掃時 `NOT` 掉已登錄主機——無新機時一次計數查詢就收工 | 併入乙 | **第 1 批**（§3.3） |
| **甲：ESM 目錄** | 一個 GET 換完整清單（含安靜主機） | ESM 唯讀權限 | **第 2 批**——權限要不到，改「保留能力」：per-Sentinel 開關、預設關（§五） |
| **丁：背景掃描＋快取** | 新增 Sentinel 即背景掃描，精靈看快取 | 乙 | **暫緩**（第二輪決策「不先掃描」）；設計保留於 §四備用。且手段二讓重掃趨近免費，丁的必要性再降 |
| 飽和早停 | 連續數頁無新主機就停止翻頁 | — | **否決**——機率性手段，安靜主機的事件可能全在停掉之後的頁面，違反涵蓋保證；由殘差輪掃取代（同樣省掉重複頁面，但 server 端排除**保證**不漏） |
| 丙：分散取樣 | 24h 切 N 段各拉少量 | — | 不排——乙已把窗口撐回 24h |
| 戊：AD 電腦物件 | 權威清單在 AD 不在 Sentinel | — | 不在本規劃——不同的問題（存在 vs 有回報），獨立評估 |

---

## 三、第 1 批（乙・涵蓋保證版）：filter 窄化＋殘差輪掃

> **2026-08-06 第二次修訂**：使用者要求「新方式不得像現行一樣，因固定取回筆數
> 造成網段內有主機卻漏掉」。因此本批升級為**涵蓋保證版**：
> (1) **移除自適應窗口壓縮**（它正是現行靜默漏機的機制——超量就縮窗，
> 縮掉的正是安靜主機）；(2) 上限觸頂改用**殘差輪掃**處理（server 端排除已見
> 主機後重查，數學上保證收斂）；(3) 曾在討論中提出的「飽和早停」**否決不採**
> ——它是機率性手段，主機事件在時間軸上成叢，安靜主機的幾筆事件可能全落在
> 停止翻頁之後，與涵蓋要求直接牴觸。記錄在此防止日後被重新提案。

### 3.0 涵蓋保證（先把承諾寫成可驗證的句子）

掃描結束時，**以下兩類主機保證入列，或明確警告「不完整」——二者必居其一，
絕不靜默漏掉**：

- (a) 近 24 小時內有 ≥1 筆 System/Application 事件的主機（主掃描）；
- (b) 近 60 分鐘內有 ≥1 筆任何頻道事件的主機（補充掃描）。

兩個窗口都掛零的主機，是**事件掃描原理上看不到的**（它沒講話）——這是資料源
的極限，不是實作缺陷，只有 ESM 目錄（§五）能覆蓋；CoverageNote 永遠把這件事
講明。與現行設計的本質差異：現行的「窗口壓縮＋固定 50,000 筆」會在**沒有任何
警告成立的情況下**漏掉窗口外的主機；新設計凡有漏掉的可能，**必有 Warning**。

### 3.1 主掃描：窄化＋殘差輪掃

**窄化 filter**（`BuildSubnetProbeFilter`）：

```
({repip}:{prefix}.*) AND ({rv150}:System OR {rv150}:Application)
```

依第三輪 probe 實測：單台主機日量 ~31 萬筆中 Security 佔 **99.95%**，
System=3、Application=152——窄化後每台貢獻 **~155 筆/日**。
100 台 /24 ≈ 15,500 筆，**遠低於** 50,000 上限 → 絕大多數情況
**第 1 輪、24h 全窗口、無截斷**就結束，涵蓋 (a) 直接成立。

**殘差輪掃（上限觸頂時的收斂機制，取代窗口壓縮）**：

```
第 1 輪：窄化 filter，窗口固定 24h，cap 50,000
  未截斷 → 完整，結束
  截斷   → 已見 repip 加入排除清單，進下一輪
第 n 輪（n ≤ MaxResidualRounds = 5）：
  窄化 filter AND NOT ({repip}:(已見ip1 OR 已見ip2 OR …))
  → server 端直接濾掉已發現主機的事件，取回的**只有還沒見過的主機**
  未截斷 → 完整，結束
  截斷且 (輪數用盡 或 排除清單 > ExclusionClauseLimit = 500)
    → Warning「本網段主機數超出掃描能力上限，清單可能不完整，請縮小網段
      （如把 /16 拆成 /24 分次掃）」——**知道可能漏，就說出來**
```

- 收斂性：每輪取回的事件全部來自新主機，新主機數嚴格遞增、殘差嚴格遞減；
  5 輪 × 50,000 筆的上界對應約 1,600 台/網段（155 筆/台/日），遠超單網段實務。
- 子句上限 500 的由來：Lucene 預設 `maxClauseCount=1024`，留一半餘裕給
  窄化子句與未來擴充；/24 全滿 254 台也在限內。
- **排除失效偵測（必做的安全網）**：`NOT` 子句在本環境**尚未實測**
  （probe 驗過 OR 50~100 子句、片語、萬用字元，沒驗過 NOT）。每輪取回後檢查：
  事件的 repip 若出現在排除清單中 → NOT 無效 → **立即停止輪掃**＋Warning
  「排除語法在此環境無效，已退回單輪結果（可能不完整），請回報」。
  沒有這道偵測，NOT 失效會讓每輪重複取回同一批主機、白燒五輪。

### 3.2 補充掃描：全事件短窗＋排除主掃描已見

```
filter：repip:{prefix}.* AND NOT (主掃描已見的全部 repip)
窗口：固定近 60 分鐘（SupplementWindowMinutes = 60）
cap：10,000（SupplementMaxResults），殘差輪掃同 3.1、上限 2 輪
```

**排除主掃描已見是本段的關鍵改進**（第一版規劃沒有）：不排除的話，
補充掃描取回的絕大多數是已發現主機的 Security 洪流——純浪費；排除後殘差
只剩「主掃描沒看到的主機」的事件，量極小，涵蓋 (b) 在 cap 內輕鬆成立。
截斷仍發生時同樣進殘差輪（至多 2 輪）→ 仍截斷才 Warning。
主掃描已見清單若超過子句上限 → 補充掃描退回無排除版＋沿用截斷警告
（此時主掃描本身多半也已在警告狀態，訊息合併不重複轟炸）。

兩段結果依 `repip` 聯集，顯示名稱 `PickMostCommonHostName` 沿用
（輪與輪之間主機不重複，無合併衝突）。

### 3.3 重掃：已登錄主機排除（增量探索）

精靈對**已有登錄主機的網段**重掃時，把該 Sentinel 轄下、IP 落在此網段的
已登錄主機做成排除清單傳入（`ListHostsAsync` 新增選填參數
`knownIps`，預設 null＝首掃行為不變）：

- 主掃描第 1 輪就帶 `NOT (已登錄…)` → 沒有新主機時 **found=0，一次查詢結束、
  取回 0 筆**——重掃從「又是五萬筆」變成趨近免費；
- 已登錄主機**不需要重新被發現**：`NetiqDiscoveryService.BuildScanResult` 端
  把已登錄主機從 host store 合成進結果（標 `Exists=true`，顯示名稱取 store 值）
  ——精靈畫面的「既有/新發現」分組**維持原樣**，UI 零改動；
- 已登錄清單超過子句上限（大網段數百台）→ 不排除、退回一般掃描
  （行為等同首掃，只是慢，不是錯）；
- 涵蓋語意不變：排除的是「已經知道的」，保證 (a)(b) 對**未知主機**依然成立。

### 3.4 改哪些檔

| 檔案 | 改動 |
|---|---|
| `LogForesight.Core/Analysis/SentinelQueryBuilder.cs` | 新增 `BuildSubnetProbeFilter(subnetInput, excludeIps)`（窄化＋選填排除）與 `BuildSubnetDiscoveryFilter` 的排除多載；排除清單逐 IP 過格式驗證（同白名單原則，天然免疫注入）；頻道值用 `SentinelFieldMap.LogName` 常數 |
| `LogForesight.Web/Services/NetiqDirectoryClient.cs`（`SentinelRestDirectoryClient`） | `ListHostsAsync` 改「主掃描殘差輪掃＋補充掃描殘差輪掃＋聯集」；**刪除**自適應窗口壓縮整段；新常數 `MaxResidualRounds=5`／`SupplementResidualRounds=2`／`ExclusionClauseLimit=500`／`SupplementWindowMinutes=60`／`SupplementMaxResults=10_000`（各附理由註解）；排除失效偵測；`knownIps` 選填參數 |
| `INetiqDirectoryClient`／`StubNetiqDirectoryClient` | 介面加 `knownIps` **選填**參數（預設 null，既有呼叫端零改動）；Stub 忽略之 |
| `LogForesight.Web/Services/NetiqDiscoveryService.cs` | 重掃時組 `knownIps`（該 Sentinel×網段的已登錄 IP）；`BuildScanResult` 合成已登錄主機列（`Exists=true`） |
| `LogForesight.Tests/SentinelQueryBuilderTests.cs` | 窄化子句／排除子句組字串；排除 IP 格式驗證；非法輸入仍擲 ArgumentException |
| `LogForesight.Tests/`（directory client 測試） | 殘差輪掃：第 1 輪截斷→第 2 輪帶 NOT→聯集完整；輪數/子句上限觸頂→Warning；**排除失效偵測**（假 handler 回已排除的 repip→停止＋Warning）；補充掃描帶主掃描排除；`knownIps` 重掃 found=0 短路；Stub 忽略 knownIps；總預算逾時整趟放棄（語意不變） |
| `docs/NETIQ-API-REFERENCE.md` | 網段掃描段落改寫：殘差輪掃設計、涵蓋保證句、`NOT` 子句待實證標記（試點核對清單+1） |

### 3.5 誠實申報

- CoverageNote：「掃描涵蓋『10.1.2.*』近 24 小時內有 System/Application 事件、
  或近 60 分鐘內有任何事件回報的主機，共 N 台（已登錄 K 台未重查）。」
- 只有三種 Warning，各對應一個明確動作：超出掃描能力（→縮小網段）、
  排除語法無效（→回報定案）、環境不轉送 System/Application 的疑慮
  （主掃描 0 台但補充掃描有台數時提示，→跑診斷確認）。
- 計數查詢保留但改用**窄化 filter**，用途只剩 CoverageNote 的量級資訊與
  「found 遠超 5 輪能力就提前建議縮網段」的預檢——不再驅動窗口。

### 3.6 不改的東西

- 精靈 UI／匯入 token 防線：零改動（3.3 的合成在 Service 層完成）。
- 90 秒總預算、「逾時不回半套」：保留。多輪掃描仍受同一預算約束——
  預算內做不完就明確失敗，這與涵蓋保證不矛盾：保證的形狀是
  「完整 或 顯性警告 或 顯性失敗」，唯一被消滅的是**靜默漏掉**。
- 夜間取數管線（Q1 watchlist 查詢）：完全不動。

### 3.7 驗收

1. 單元測試全綠（3.4 表列）。
2. Stub 模式精靈流程不變。
3. 真實環境試點：同一網段改前/改後對照——耗時降、台數不減；
   重掃第二次（無新機）應在 ~2 秒內結束（一次計數＋一次殘差查詢）。
4. **涵蓋保證的實測驗證**：故意掃一個含「安靜主機」（24h 只有零星
   System 事件）的網段，該主機必須入列——這正是現行設計會漏的那種。

### 3.8 風險與退路

| 風險 | 處置 |
|---|---|
| `NOT` 子句在此環境無效（未實證） | 排除失效偵測（3.1）當場抓到、停止輪掃、顯性警告；退回行為=單輪掃描（不劣於現行）。試點核對清單+1 |
| 某環境 collector 不轉送 System/Application | 補充掃描仍在；主掃描 0 台＋補充有台數 → 提示跑診斷；證實後 `BuildSubnetProbeFilter` 退回全事件＝改一處 |
| 超大網段（>1,600 台或已登錄>500） | 輪數/子句上限觸頂 → 顯性警告建議拆 /24；不靜默、不半套 |
| 多輪 job 增加 Sentinel job 數 | 每輪用完即刪；輪間沿用 `QueryDelayMs` 節流；輪數上限 5 |

---

## 四、丁：探索背景化＋結果快取（**暫緩**，設計保留備用）

> **2026-08-06 第二輪決策：不先做背景掃描。** 本節設計原樣保留——
> 日後要啟用時，前提與形狀都不會變。另註：批 1 的重掃已知排除（§3.3）
> 讓重掃成本趨近一次計數查詢，「互動等待」的痛已大幅縮小，
> 丁重啟與否建議等批 1 實測數據再議。

原定觸發語意：「新增 NetIQ（Sentinel）後開始背景掃描」。
可行，而且正是最對的時機——但有一個前提要先講清楚：**新增當下系統不知道
這台 Sentinel 管哪些網段**（掃描必須給網段，這是探索設計的根，見
`INetiqDirectoryClient.ListHostsAsync` 的參數註解：全站盲掃已被 2,470 萬筆/日
實測否決）。所以「新增後自動掃」的完整形狀是：
**Sentinel 登錄時順手填「探索網段」清單 → 存檔即觸發背景掃描 → 之後每晚刷新**。

### 4.1 資料模型：Sentinel 增加「探索網段」欄位

| 檔案 | 改動 |
|---|---|
| `LogForesight.Core/Models/Sentinel.cs` | 新增 `List<string> DiscoverySubnets`（正規化後的前綴字串，如 `"10.1.2"`；空清單＝不背景掃描，既有資料反序列化自然得到空清單，**免遷移**——Sentinel 走 blob 儲存，無 DDL） |
| `LogForesight.Web/Services/SentinelAdminService.cs`（`SaveSentinel`） | 收新欄位；逐筆過 `SentinelQueryBuilder.NormalizeSubnetPrefix` 驗證（非法網段=validation 錯誤，訊息沿用既有的人話版本）；去重 |
| NetIQ 維護頁（Sentinel 編輯表單＋`netiq.js`） | 「探索網段」多值輸入（textarea 一行一個即可，不做花式 tag 元件——管理員一年填不了幾次）；欄位說明講清楚兩件事：填了才會背景掃描、精靈手動掃描不受此限 |

### 4.2 快取儲存與執行載體

| 檔案 | 改動 |
|---|---|
| `LogForesight.Web/Services/`（新）`NetiqScanCacheStore.cs` | 走 `StorageBackend.Blob("netiq_scan_cache")`（`JsonBlobSingleton` 型）。內容：`SentinelId × 正規化網段` → `{ Hosts, CoverageNote, Warnings, ScannedAt, DurationMs, Success, Error }`。**失敗也是一筆快取**——「昨晚掃 10.1.2.* 失敗：逾時」比「什麼都沒有」誠實，精靈要顯示得出來 |
| `LogForesight.Web/Services/`（新）`NetiqScanRunState.cs`＋背景執行邏輯 | 沿用 `NetiqProbeService` 的既有模式（Task.Run＋run state 單一執行 gate，不新發明）：同一時間整站至多一輪背景掃描在跑；逐 Sentinel、逐網段**依序**執行，網段之間 `Task.Yield()`（S-3 同一個理由——這跑在站台行程內）；單一網段沿用互動掃描的 90 秒預算（背景沒有人在等，但預算守的是「單一 job 不失控」，不是體感） |

### 4.3 兩個觸發點

**(1) Sentinel 存檔後（定案的主觸發）**：`SentinelAdminService.SaveSentinel`
成功後，若「有探索帳密（`CanDiscover`）且 `DiscoverySubnets` 非空且
（新建 或 網段清單有變動）」→ 觸發該台的背景掃描。**只掃這一台**，
不順手全站重掃——管理員改 A 台的設定，B 台沒理由跟著動。

**(2) 每晚刷新**：`SchedulerHostedService` 在 orchestrator `RunAsync` 成功返回後
（Web 層 hook，**Core 零改動**），對全部 `Active` 且可探索、有網段的 Sentinel
依序刷新快取。放在夜間分析之後的理由：那個時段本來就在跟 Sentinel 講話，
邊際成本最低；且分析剛結束，thread pool／連線池壓力已釋放。
排程停用（`ScheduleOptions.Enabled=false`）的環境就只有觸發點 (1)——
可接受：快取過期時精靈照樣可手動重掃。

### 4.4 精靈端：快取優先、手動即刷新

- 掃描步驟改為：選了 Sentinel＋輸入網段後，**先查快取**——命中且未過期
  （建議 48 小時，常數附理由：兩個夜間刷新週期，錯過一晚仍有效）→ 直接顯示
  結果＋「資料時間：8/6 02:14（背景掃描）」＋「重新掃描」按鈕；
  未命中或按了重新掃描 → 走既有互動路徑，**成功後回寫同一份快取**
  （手動與背景共用單一事實來源，不會出現兩套結果打架）。
- 匯入 token 流程不變：使用快取結果時，把快取快照登錄成 `PendingScan` 發 token
  ——`Import` 端「只接受掃描過的 IP」的防線原樣保留。
- 精靈輸入的網段若不在該台 `DiscoverySubnets`，加一個「記住此網段供背景掃描」
  勾選（選配，第一版可不做——先把主流程立起來）。

### 4.5 不做的（明確講）

- **不自動匯入**：背景掃描只填快取，發現新主機仍要人進精靈勾選——
  既有定案 7「勾選送出即落盤」的語意不變，探索是變快，不是變自動。
- 不做跨 Sentinel 平行背景掃描：背景沒有體感壓力，依序＋讓出對站台最溫柔。

### 4.6 測試與驗收

- 測試：`SaveSentinel` 網段驗證與去重；「新建有網段→觸發」「網段沒變→不觸發」；
  run state 單一執行 gate；快取回寫（手動與背景寫同一 key）；失敗也落快取。
- 驗收：新增一台 Sentinel（填網段）→ 數分鐘內快取出現；進精靈直接看到結果
  不用等；按重新掃描走互動路徑且快取更新；隔晚快取時間戳刷新。

---

## 五、第 2 批（甲）：ESM 目錄——保留能力、每台 Sentinel 手動開啟

**權限現階段要不到（2026-08-06 定案）**，因此設計目標從「主來源」改為
「**保留能力**：有權限的環境打開就能用，沒權限的環境完全無感」。
兩個後果要誠實面對：

1. **本環境無法驗證回應形狀**——probe 步驟 6 拿不到 200 的輸出，
   解析器只能做防禦性實作，不能假裝「實測定案」；
2. 因此**不能自動啟用**（原規劃的「每次掃描先試 ESM」作廢）：
   自動信任一個沒驗證過的解析結果，錯了會讓探索清單靜默變形。

### 5.1 每台 Sentinel 一個開關，預設關

| 檔案 | 改動 |
|---|---|
| `LogForesight.Core/Models/Sentinel.cs` | 新增 `bool UseEsmDirectory`（預設 `false`）。per-Sentinel 而非全域——不同 Sentinel 的帳號權限本來就可能不同 |
| NetIQ 維護頁 | 開關「以 ESM 事件來源目錄探索（需 ESM 唯讀權限）」＋說明文字：「開啟前請先在『診斷』分頁執行一次診斷，確認步驟 6 能取得事件來源清單」——**把驗證閘門放在人的流程裡**，補程式驗證不了的那一段 |

### 5.2 防禦性解析器（`LogForesight.Core/Analysis/SentinelEsmDirectory.cs`，新）

沒有實測樣本，解析器的設計原則是「**寧可退路，不可猜錯**」：

- 輸入：`RawGetAsync("/SentinelRESTServices/objects/eventsource")` 的 JSON；
- 逐條目在**候選欄位名清單**中找 IPv4（嚴格 regex 驗證）與名稱——
  候選清單依公開 7.0 apidoc 與 Sentinel 物件慣例列舉，**程式碼註解明講
  「未經本環境實測，候選而已」**；
- **成功閘門**：至少一個條目解析出合法 IPv4 → 才算 ESM 結果可用；
  解析出 0 台 → 視為「格式與預期不符」，**不是**「該 Sentinel 沒主機」；
- 回傳附原始條目數與解析成功數，讓 CoverageNote 說得出
  「目錄共 N 條、可解析 M 台」。

### 5.3 探索流程（`SentinelRestDirectoryClient`）

```
UseEsmDirectory=false（預設）→ 完全跳過，走第 1 批事件掃描（零行為差異）
UseEsmDirectory=true：
  RawGetAsync → 200 且解析閘門通過 → 依前綴過濾 → 回傳
      CoverageNote：「來源：Sentinel 事件來源目錄（完整清單，含目前無事件
      回報的主機；目錄共 N 條、對應本網段 M 台）」
  401/403 → Warning「此帳號無 ESM 權限，已改用事件掃描——請關閉此開關或調整權限」
            ＋ 落到事件掃描
  200 但解析 0 台 → Warning「ESM 回應格式與預期不符，已改用事件掃描；
            請至診斷分頁執行步驟 6 並回報輸出以定案格式」＋ 落到事件掃描
```

開關開著但每次都退路 → 每次都有 Warning——刻意吵，逼人把開關關掉或把格式
回報回來定案，不讓「壞掉的捷徑」安靜地假裝在工作。

### 5.4 背景掃描（丁，暫緩）的銜接

丁日後若啟用，背景掃描走同一個 `ListHostsAsync`，開關開啟時自動享受 ESM；
快取條目記錄來源（`目錄` vs `事件掃描`），精靈顯示得出來。暫緩期間本節無事可做。

### 5.5 測試與驗收

- 測試：解析器（合成樣本：候選欄位命中/不命中/混合；0 台 → 不可用）；
  開關關閉零行為差異；403 退路；200-不可解析退路（含 Warning 文案）。
- 驗收（要等有權限的環境）：開啟開關 → 診斷步驟 6 有輸出 → 探索
  CoverageNote 顯示目錄來源、台數 ≥ 事件掃描、耗時 < 5 秒。
  **拿到真實輸出後**：把樣本存進測試 fixture、依實際欄位收斂候選清單、
  更新 `NETIQ-API-REFERENCE.md` 新章節——防禦版轉正的必經步驟。

---

## 六、console 中文亂碼（Rider）

### 6.1 成因分析

專案**沒有任何地方**設定 `Console.OutputEncoding`；`nlog.config` 的 Console target
也沒有 `encoding` 屬性（File target 有 `utf-8`，所以 `logs/web.log` 是好的，
**只有畫面亂**——這正是編碼問題的指紋）。Windows 繁中環境下：

- 輸出端：.NET console 預設跟隨系統代碼頁（CP950/Big5）；stdout 被重導向時
  行為又不同；
- 讀取端：Rider 用自己的預設編碼解碼子行程輸出。

兩端不一致＝亂碼。**方向決定修法**，先診斷。

### 6.2 診斷（一分鐘，動手前先做）

Windows Terminal：`chcp 65001` 後 `dotnet run --project LogForesight.Web`——

| 結果 | 結論 |
|---|---|
| 終端機正常、Rider 亂 | Rider 解碼端 → 修 6.3 (b) 為主，(a) 照做（讓行為變確定） |
| 兩邊都亂 | 應用程式輸出端 → 修 6.3 (a) 即解 |

### 6.3 修法（兩邊都做不衝突）

**(a) 應用程式端（推薦必做）**——`LogForesight.Web/Program.cs` Main 最前面
（`--hash-password` 分支之前，任何輸出發生前）：

```csharp
// 開發期 console 輸出一律 UTF-8（正式環境走檔案 log 與 HTTP，不受影響）。
// 兩個刻意：
//   1. UTF8Encoding(false)＝不寫 BOM——部分終端會把 BOM 顯示成開頭怪字元；
//   2. try/catch IOException——以 Windows 服務身分執行時沒有主控台，
//      設定 OutputEncoding 會擲例外，不能讓編碼小事炸掉服務啟動。
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch (IOException) { /* 無主控台（Windows 服務）——本來就沒有畫面可亂 */ }
```

**不動 `nlog.config`**（2026-08-06 定案：啟動時指定 UTF-8 即可）——NLog 的
Console target 走 `Console.Out` 輸出，`OutputEncoding` 設了它自然跟著；
多加一個 `encoding` 屬性是同一件事寫兩遍，之後有人只改其中一處就會分歧。

**(b) Rider 端（若診斷指向解碼端）**：Settings → Editor → File Encodings →
Global Encoding 設 UTF-8；仍不行則 Help → Edit Custom VM Options 加
`-Dfile.encoding=UTF-8` 重啟。這是各開發機自己的設定，**不進版控**，
但把步驟記進本文件（下一個裝 Rider 的人不用重查）。

### 6.4 影響面與驗收

- 純開發期體感：檔案 log 已是 UTF-8、Web 回應有自己的 charset，**正式環境零影響**；
  Windows 服務模式因 try/catch 也零影響。
- 驗收：Rider 執行視窗中文正常；`chcp 65001` 終端機正常；以服務註冊啟動不炸；
  `logs/web.log` 內容前後一致。
- **獨立小 commit**：它碰 `Program.cs` 啟動路徑，不要和 NetIQ 改動混在一起。

---

## 七、批次切法與決策紀錄

### 批次（兩輪決策後定版）

| 批 | 內容 | commit | 前置 |
|---|---|---|---|
| 0 | console 編碼：`Program.cs` 啟動時指定 UTF-8（§6.3a，**不動 nlog.config**） | 獨立一個 | 無——隨時可做 |
| 1 | 乙・涵蓋保證版：窄化＋殘差輪掃＋補充掃描（帶排除）＋重掃已知排除＋文件 | 一個 | 無 |
| 2 | 甲：ESM 防禦性解析器＋per-Sentinel 開關（預設關）＋文件 | 一個 | 批 1（退路）；**驗收**要等有 ESM 權限的環境 |
| — | 丁：背景掃描＋快取 | — | **暫緩**（設計保留 §四），批 1 實測後再議 |

### 決策紀錄（2026-08-06 使用者回覆，兩輪）

1. **ESM 權限**：現階段要不到 → 甲改為「保留能力」設計：每台 Sentinel 一個
   `UseEsmDirectory` 開關、預設關；解析器防禦性實作（成功閘門＋退路），
   有權限的環境依 §5.1 的人工驗證流程開啟。原「每次掃描先試 ESM」作廢——
   不能自動信任沒驗證過的解析。
2. **補充掃描參數**：依評估採建議值 **60 分鐘窗／10,000 筆上限**。
3. **丁**：第一輪定案要做、第二輪改為**暫緩**（「決定不先掃描」）——設計
   原樣保留於 §四，重啟時機等批 1 實測數據（重掃已知排除已讓重掃趨近免費）。
4. **console 亂碼**：定案=啟動時 `Console.OutputEncoding` 指定 UTF-8 即可
   （含 Windows 服務無主控台的 try/catch），不動 nlog.config。
5. **涵蓋保證（第二輪新增要求）**：新方式不得因固定取回筆數漏掉網段內主機
   → 批 1 升級為殘差輪掃版（§3.0 的可驗證承諾：完整、或顯性警告、或顯性失敗，
   **靜默漏掉被消滅**）；「飽和早停」提案因違反此要求否決（§二表）。
