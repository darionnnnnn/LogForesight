# NetIQ 主機探索成本改善＋開發環境 console 編碼 修復規劃（2026-08-06）

> **狀態：只規劃，未修改任何程式碼。**
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

| 方案 | 一句話 | 前置條件 | 本規劃的處置 |
|---|---|---|---|
| **乙：filter 窄化** | 用低量頻道當「主機存在的探針」，成本從正比事件量變成正比主機數 | 無 | **第 1 批，先做** |
| **甲：ESM 目錄** | 一個 GET 換完整清單（含安靜主機），治本 | SIEM 管理者開唯讀權限 | **第 2 批**，權限先去談，程式碼等 probe 定案回應形狀 |
| **丁：背景掃描＋快取** | 探索改夜間排程順掃，精靈看快取，不再互動乾等 | 無（設計依賴乙/甲擇一先落地） | **第 3 批，待決**——先問要不要 |
| 丙：分散取樣 | 24h 切 N 段各拉少量，涵蓋分布更好 | — | 不排——乙已把窗口撐回 24h，丙解的問題大半消失；甲落地後更無必要 |
| 戊：AD 電腦物件 | 權威清單在 AD 不在 Sentinel；「AD 有、Sentinel 沒有」正是最該報的盲區 | — | 不在本規劃——它回答的是不同的問題（存在 vs 有回報），範圍大，獨立評估 |

順序理由：乙**今天就能做**、不求人，慢與涵蓋差一起解；甲要等別人開權限，
但拿到後是正解，乙自動降級為 ESM 不可用時的退路——兩者不衝突，先後落地即可。

---

## 三、第 1 批（乙）：探索 filter 窄化＋補充掃描

### 3.1 核心改動：兩段掃描取代單段全事件掃描

**主掃描（窄化探針）**：

```
({repip}:{prefix}.*) AND ({rv150}:System OR {rv150}:Application)
```

依第三輪 probe 實測（`repip:10.1.2.11`）：單台主機日量 ~31 萬筆中
Security 佔 **99.95%**，System=3、Application=152——窄化後每台主機貢獻
**~155 筆/日**而不是 31 萬筆。效果：

| | 現況 | 主掃描（窄化） |
|---|---|---|
| 成本正比於 | 事件量（不可控） | **主機數**（可控：~155 筆/台/日） |
| 100 台 /24 | 50,000 筆、50 頁 | ~15,500 筆、16 頁 |
| 300 台 /16 段 | 同樣 50,000 筆但窗口被壓縮 | ~46,500 筆、**窗口仍是 24h 全開** |
| 涵蓋 | 2 小時～5 分鐘內講過話的 | **24 小時內講過話的** |

**補充掃描（Security-only 主機的保險）**：主掃描的已知風險是「某台主機 24h 內
System/Application 恰好零筆」（probe 樣本 System 才 3 筆/日，安靜主機某天掛零
完全可能）。補一段**全事件、固定短窗**的掃描：

```
filter：repip:{prefix}.*（沿用現行 BuildSubnetDiscoveryFilter）
窗口：固定近 60 分鐘
max-results：10,000（10 頁）
```

兩段結果依 `repip` **聯集**（顯示名稱以先到者為準、後到者補缺）。

**為什麼補充掃描敢用固定短窗**：它的職責只是「撈出這一小時在講話、
但主掃描漏掉的 Security-only 主機」——Security 事件量大正是這裡的優勢：
只要主機活著，60 分鐘內幾乎必有 Security 事件（登入、稽核、服務票證），
短窗對 Security 的涵蓋率遠高於對 System/Application。兩段各用自己擅長的
頻道×窗口組合，聯集後的涵蓋率**嚴格優於**現行單段設計。

**成本上界**：50,000（主）＋10,000（補）欄位投影後兩欄極小事件——
最壞情況仍低於現行（現行單段就頂到 50,000，而且常常真的頂到）；
一般情況（/24、數十台）主掃描只有幾頁，總成本降一個數量級。

### 3.2 改哪些檔

| 檔案 | 改動 |
|---|---|
| `LogForesight.Core/Analysis/SentinelQueryBuilder.cs` | 新增 `BuildSubnetProbeFilter(string subnetInput)`（窄化版）；既有 `BuildSubnetDiscoveryFilter` **原樣保留**（補充掃描與可能的除錯用途沿用）。頻道值用 `SentinelFieldMap.LogName` 常數組字串，不寫死 `rv150` |
| `LogForesight.Web/Services/NetiqDirectoryClient.cs`（`SentinelRestDirectoryClient`） | `ListHostsAsync` 改兩段掃描；新常數 `SupplementWindowMinutes = 60`、`SupplementMaxResults = 10_000`（各附註解講理由）；主掃描保留自適應窗口邏輯**當保險**（窄化後 found 仍超過 50,000 才會縮——例如巨型 /16）；`CoverageNote` 改寫（見 3.3）；聯集去重＋`PickMostCommonHostName` 沿用 |
| `LogForesight.Tests/SentinelQueryBuilderTests.cs` | `BuildSubnetProbeFilter` 的組字串測試（頻道子句、與 Normalize 的銜接、非法輸入仍擲 ArgumentException） |
| `LogForesight.Tests/`（directory client 既有測試檔） | 假 handler 驗證：兩段掃描各建一個 job；聯集去重（同 IP 兩段都出現只留一筆、名稱以主掃描為準）；主掃描截斷 → Warnings 有「結果可能不完整」；補充掃描 0 筆不報錯；總預算逾時仍整趟放棄不回半套（既有語意不變） |
| `docs/NETIQ-API-REFERENCE.md` | 「網段範圍掃描」段落改寫成兩段掃描設計＋窄化理由（含 probe 實測數據出處） |

### 3.3 CoverageNote 與 Warnings 的誠實申報（沿用既有原則）

- CoverageNote 改為兩段語意，例：
  「掃描涵蓋『10.1.2.*』近 24 小時內有 System/Application 事件、
  或近 60 分鐘內有任何事件回報的主機，共 N 台。」
- 主掃描被截斷（窄化後仍超量）→ Warning：「該網段主機數可能超出單次掃描上限，
  建議縮小網段」；補充掃描被截斷 → 不另外警告（它本來就只是保險，截斷代表
  Security 量大、而 Security 量大的主機主掃描多半也撈得到）——這個取捨寫進註解。
- 計數查詢仍保留（主掃描的自適應保險需要它），但改用**窄化 filter** 計數——
  計數本身也是一次查詢，沒理由用全事件去數。

### 3.4 不改的東西（明確講）

- `NetiqDiscoveryService`／精靈 UI／`INetiqDirectoryClient` 介面：**零改動**——
  改動全部封在 `SentinelRestDirectoryClient` 與 QueryBuilder 內。
- `StubNetiqDirectoryClient`：不改（示範資料與掃描策略無關）。
- 90 秒總預算、「逾時不回半套」：**保留**——成本降了之後這條防線更少被觸發，
  但它守的是「互動操作必須有明確結束」，與成本無關。
- 夜間取數管線（`NetiqPipelineService` 的 Q1 查詢）：**完全不動**——那條走的是
  watchlist 窄化，本來就是成本正比規則命中量的正確設計。

### 3.5 驗收

1. 單元測試全綠（含上表新增）。
2. Stub 模式精靈流程不變（UI 零改動的驗證）。
3. **真實環境**（試點時）：對同一網段各掃一次改前/改後，記錄
   耗時、翻頁數、回傳台數——預期耗時降、台數**不減**（聯集涵蓋率只增不減）。
   改後台數若反而少，代表該環境 System/Application 轉送策略與 probe 環境不同，
   此時退回全事件掃描並記錄（見 3.6）。

### 3.6 風險與退路

| 風險 | 處置 |
|---|---|
| 某環境 collector 不轉送 System/Application | 兩段掃描的補充段仍在（全事件短窗），不會全滅；若試點證實整個環境如此，`BuildSubnetProbeFilter` 退回全事件＝改一行，設計上已把 filter 選擇隔離在 QueryBuilder |
| 窄化後 found 極小、視窗計算除以小數 | 主掃描 found ≤ 50,000 → 窗口=24h（既有分支就是這樣），無新邏輯 |
| 兩段掃描=兩個 job | Sentinel 端 job 是輕量資源且用完即刪；相比省下的 34 頁翻頁，多一個 job 建立/刪除是零頭 |

---

## 四、第 2 批（甲）：ESM 事件來源目錄

### 4.1 先做的不是程式碼，是兩件事

**(1) 權限申請**（給 SIEM 管理者的具體請求，可直接轉述）：

> 請為 LogForesight 的探索帳號（現有 event-search 權限的那個）加開
> **ESM 物件唯讀權限**，至少涵蓋
> `GET /SentinelRESTServices/objects/eventsource`（事件來源清單）。
> 用途：以一次唯讀查詢取得已註冊主機清單，**取代**目前「拉取數萬筆事件
> 在本地 distinct」的探索方式——對 Sentinel 的負擔遠低於現況。
> 若有 `event-source-server`／`collector` 相關物件的唯讀權限一併開通更好
> （用於分辨 collector 與主機層級）。

**(2) 回應形狀定案**：權限開通後，到 NetIQ 維護頁「診斷」分頁跑一次 probe——
**步驟 6 現成就會打 `eventsource` 並印出前 2000 字元**
（[NetiqProbeRunner.cs:193](../LogForesight.Core/Service/NetiqProbeRunner.cs)），
不用寫任何新程式。把輸出貼回來定案：

| 待定案 | 為什麼重要 |
|---|---|
| 每列是「主機」還是「collector/connector」 | 第二輪 probe 已證實 repip 一對一非共用代理，大機率每台主機一列，但要眼見為憑 |
| IP／主機名落在哪個欄位、命名慣例 | 對應到 `NetiqDiscoveredHost(HostName, IpAddress)` |
| 有無「啟用/停用」「最後回報時間」欄位 | 有的話能直接支撐「應在而未回報」的訊號（戊的一半價值免費入袋） |
| 回應量級與分頁行為 | 決定要不要快取（單一 GET 若回 2000 台的 JSON 也只是幾百 KB，預期不用） |
| 是否含已移除/歷史來源 | 決定要不要過濾殭屍條目 |

### 4.2 程式碼設計（等 4.1 (2) 定案後才動工）

| 檔案 | 改動 |
|---|---|
| `LogForesight.Core/Analysis/`（新）`SentinelEsmDirectory.cs` | 回應解析器：JSON → `List<EsmEventSource>`（欄位依定案）。**以 probe 真實輸出當測試 fixture**（與 SentinelFieldMap 定案的方法論一致：實測樣本為準，不猜） |
| `LogForesight.Web/Services/NetiqDirectoryClient.cs` | `ListHostsAsync` 改「**ESM 優先、事件掃描退路**」：先 `RawGetAsync("/SentinelRESTServices/objects/eventsource")` → 解析 → 依 `NormalizeSubnetPrefix` 前綴過濾 → 回傳（CoverageNote 標明「來源：Sentinel 事件來源目錄（完整清單，含目前無事件回報的主機）」）；遇 401/403/解析失敗 → 記一筆 Info log → 落到第 1 批的兩段事件掃描（CoverageNote 維持事件掃描語意）。**每次掃描都先試 ESM**，不做能力快取——單一 GET 很便宜，快取「上次 403」反而會在權限開通後還繼續走退路 |
| `LogForesight.Tests/` | 解析器測試（真實 fixture）；fallback 測試（403 → 事件掃描被呼叫）；前綴過濾測試 |
| `docs/NETIQ-API-REFERENCE.md` | 新增「§ESM 事件來源目錄」章節：端點、權限需求、回應形狀定案、與事件掃描的主備關係 |

### 4.3 為什麼 ESM 值得當主來源（不只是比較快）

事件掃描的涵蓋語意天生是「**窗口內講過話的主機**」；ESM 目錄是「**已註冊的主機**」。
後者才對得上本系統的核心原則——「沒查 ≠ 沒事」：一台三天前掛掉、不再送 log 的
主機，事件掃描永遠掃不到（它不講話了），ESM 目錄看得到。探索用 ESM 之後，
「掃描結果=完整名單」的隱含誤解（現在靠 CoverageNote 文字消毒）從根本上消失。

### 4.4 驗收

1. 權限開通前：probe 步驟 6 回 401/403，探索走事件掃描（第 1 批行為）——退路實證。
2. 權限開通後：同一網段 ESM 掃描 vs 事件掃描比對台數——ESM 應 ≥ 事件掃描
   （多出來的正是安靜主機）；耗時應 < 5 秒。
3. 精靈 CoverageNote 正確標示來源。

---

## 五、第 3 批（丁，待決）：探索改背景化＋結果快取

**先回答要不要，再談怎麼做。** 機房主機清單變動很慢（新機上架是週/月級事件），
現在的設計卻讓管理員每次都即時等掃描。若甲落地（ESM 單一 GET 秒回），
互動等待的痛大半消失，**丁可能根本不必做**——因此本批排在甲之後評估。

若仍要做（例如 ESM 權限談不下來、乙的掃描仍要 30 秒級）：

- **儲存**：blob `netiq_scan_cache`（`EfJsonBlobStore`，per Sentinel×網段一份，
  含掃描時間戳與 CoverageNote）。
- **觸發**：夜間 pipeline 各 Sentinel 分析完成後，對「既有主機推導出的 /24 清單
  ＋歷史上手動掃過的網段」各跑一次第 1 批的掃描並更新快取——那個時段本來就在
  跟 Sentinel 講話，邊際成本最低。**注意 S-3**：這段跑在站台行程內，掃描要沿用
  分析的讓出與平行度上限，不能另開併發。
- **精靈 UI**：進入掃描步驟先顯示快取結果＋「資料時間：昨晚 02:14」＋
  「重新掃描」按鈕（走既有互動路徑）。
- **不做的**：不做自動匯入——發現新主機仍要人勾選（既有定案 7 的語意不變，
  探索只是變快，不是變自動）。

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

同時 `nlog.config` Console target 補 `encoding="utf-8"`（NLog 端的同一件事，
雙保險；File target 不動）。

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

## 七、批次切法與待你決定的事

### 批次

| 批 | 內容 | commit | 前置 |
|---|---|---|---|
| 0 | console 編碼（6.3a） | 獨立一個 | 無——隨時可做 |
| 1 | 乙：filter 窄化＋補充掃描＋文件 | 一個 | 無 |
| 2 | 甲：ESM 解析器＋ESM-first 探索＋文件 | 一個 | SIEM 權限開通＋probe 步驟 6 輸出定案 |
| 3 | 丁：背景化＋快取 | 一個 | **待決**，甲落地後再評估要不要 |

### 需要你決定／提供

1. **ESM 權限能不能要到？**（§4.1 的申請文字可直接轉給 SIEM 管理者）——
   能：批次 2 排程；不能：乙就是長期方案，丙屆時再評估。
2. **補充掃描的參數**：60 分鐘窗／10,000 筆上限是我的建議值（理由見 §3.1），
   接受或另有偏好？
3. **丁要不要做**：建議等甲的結果再決定；若現在就確定「探索一定要秒開」，
   批次 3 可提前與批次 1 並行設計。
4. **console 亂碼的診斷結果**（§6.2 一分鐘）：跑完告訴我方向，
   或直接授權我把 6.3(a) 做掉（它無論診斷結果如何都值得做）。
