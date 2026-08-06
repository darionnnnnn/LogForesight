# 規模化與「問題主視角」規劃（2026-08-06）

> **狀態：只規劃，未動任何程式碼。** 基準版本 dev@e9ae11e。
> 來源需求三項：(1) 主視角放在**問題本身**，並顯示該問題的**主機數**與**期間跨度**；
> (2) 程式面確保撐得住 **2000～6000 台**，不因瓶頸或鎖競爭造成錯誤或停擺；
> (3) 拆解 [UX-AUDIT-2026-08-05.md](UX-AUDIT-2026-08-05.md) 全文，找出**跨區塊的最佳解**，
> 每讀完一個區塊要以整體角度回看結論是否仍成立。
>
> 本文件回答的是「**改什麼、為什麼、對專案哪裡有影響、順序怎麼排**」，不含程式碼。

---

## 零、先講三個對體檢報告的修正

規劃前逐項讀碼複查，有三處與體檢報告的結論不同。這三處會**改變整份優先序**，先講。

### 修正 1：S1「可見範圍 IN 參數會直接拋例外」——**前提不成立（已實測）**

體檢 §7 S1 判定「2000 台展開成 2000 個 IN 參數，貼近 SQL Server 2100 上限與 SQLite 999 上限，
**上線當天全站查詢拋例外**」，並列為第 1 順位上線阻擋項。

實測結果（以本專案的 EF Core **8.0.10** 建立同形狀查詢，取 `ToQueryString()`）：

```sql
-- SQL Server（2500 個 id）
DECLARE @__ids_0 nvarchar(max) = N'[1,2,3,...]';
WHERE ([l].[HostId] <> CAST(0 AS bigint) AND [l].[HostId] IN (
    SELECT [i].[value] FROM OPENJSON(@__ids_0) WITH ([value] bigint '$') AS [i]
)) OR [l].[HostId] = CAST(0 AS bigint)

-- SQLite（2500 個 id）
WHERE ("l"."HostId" <> 0 AND "l"."HostId" IN (
    SELECT "i"."value" FROM json_each(@__ids_0) AS "i"
)) OR "l"."HostId" = 0
```

EF Core 8 的 primitive-collection 翻譯把整份清單送成**一個 JSON 參數**（`OPENJSON`／`json_each`），
**不是 N 個參數**。6000 台也一樣是 1 個參數，2100／999 的上限碰不到。

**但 S1 底下真正的問題比報告寫的更嚴重**，只是原因不同（見下節 N1）——報告指對了位置、指錯了病因。
把「會拋例外」當成第一順位，會讓人以為改完 IN 展開就安全了。

### 修正 2：S2「線性劣化，可獨立立案延後」——**它才是會硬失敗的那一個**

體檢 §9 明文把 S2 排除在上線阻擋項外（「線性劣化而非立即失效…上線後三個月內完成」）。
以 6000 台重算：

| 資料 | 列數（6000 台 × 90 天） | 單列 JSON | 整份大小 | 序列化後的 C# string |
|---|---|---|---|---|
| `issue_handling` | 每台每天 6 列 → **324 萬列** | ~400 B（`LfJsonOptions.Pretty` 有縮排） | **~1.3 GB** | **~2.6 GB（UTF-16）** |

.NET 單一物件上限是 2 GB（未開 `gcAllowVeryLargeObjects`）。
`JsonBlobCollection.Mutate` 的做法是 `JsonSerializer.Serialize(items)` 產生**整份字串**再寫回——
**約 400 萬列就會直接 `OutOfMemoryException`／字串長度上限，不是變慢，是拋例外**。
2000 台（108 萬列、~432 MB 字元、~864 MB string）雖然還沒到上限，但每一次標記都要在 LOH
配置近 1 GB 兩次（讀＋寫），多人同時操作時 GC 壓力足以讓整站停頓。

**結論倒過來：S1 不會炸、S2 會炸。** 而且 S2 同時是需求 (1) 的前置條件（見 §四）。

### 修正 3：規模基準改為 6000 台

體檢全篇以 2000 台推算。依需求改採 **6000 台 × 90 天**為設計上限，重算後
「量級差一個數量級」的項目有三處（`issue_handling` 的 string 上限、別名展開的 O(N²)、
儀表板全量載入的反序列化量），其餘結論不變。

---

## 一、規模基準（本文件統一採用）

| 項目 | 2000 台 | **6000 台（設計上限）** | 現況承載 |
|---|---|---|---|
| `lf_daily_records` 列數 | 18 萬 | **54 萬** | ✅ 真表＋索引 |
| `lf_daily_records` ContentJson 總量（平均 5 KB） | ~0.9 GB | **~2.7 GB** | ✅ 不整批讀就沒事 |
| `lf_top_issues` 列數（每天每台 15 個問題） | 270 萬 | **810 萬** | ✅ 真表，但目前只當篩選用 |
| `issue_handling` 列數 | 108 萬 | **324 萬** | ❌ 單一 JSON blob（見修正 2） |
| `issue_cases` 列數（進行中＋已結案） | 1～3 萬 | **3～9 萬** | ❌ 單一 JSON blob |
| `handling_log` 列數 | 數百萬 | **千萬級** | ⚠️ 真表但全表掃描讀取 |
| `hosts` blob | 2000 筆 ≈ 0.6 MB | **6000 筆 ≈ 1.8 MB** | ⚠️ 每次 `GetAll()` 全讀全解 |
| 相異問題簽章（Source+EventId） | 數百 | **數百～數千** | ❌ 呈現層無收斂手段 |

---

## 二、逐區塊拆解（含每區塊的「整體回看」）

### 2.1 體檢 §7「程式碼薄弱點」S1～S8

複查逐項結果：

| 項次 | 體檢判定 | 實查結果 | 調整 |
|---|---|---|---|
| S1 IN 參數上限 | 最高／會拋例外 | **前提不成立**（EF 8 用 OPENJSON） | 病因改判為 N1 |
| S2 整份 blob | 最高／線性劣化，可延後 | **會硬失敗（OOM）**，且是需求 (1) 的前置 | **升為第一順位** |
| S3 統一標記逐筆寫 | 高／小修 | 屬實，且**比報告更嚴重**（見 N2） | 維持高，作法要改 |
| S4 夜間掛接迴圈內重寫 | 高／小修 | 屬實 | 維持 |
| S5 歷程全表掃描 | 中 | 屬實；**站台啟動的那一次會拖垮首次請求**（見 N4） | 升為高 |
| S6 儀表板／報表全量載入 | 中 | 屬實，6000 台下是 GB 級反序列化 | 升為高（且與需求 1 同一刀） |
| S7 批次寫入同步 HTTP | 中 | 屬實 | 維持 |
| S8 其餘線性劣化 | 低 | 其中「HostAdminService 記憶體分頁」屬實；**HostLookup／Expand 一項被低估**（見 N1） | 拆出 N1 升為最高 |

#### 本次讀碼新增的五項（體檢未列）

**N1【最高】主機別名展開是 O(N²)，而且每次查詢重做、完全沒有快取**

- `RecordRepository.VisibleHostKeys()` 對**每一台**可見主機呼叫
  `HostIdentityResolver.Expand(allHosts, hostId)`，而 `Expand` 內部又要掃過全部主機
  （`hosts.Where(h => Surviving(hosts, h).HostId == hostId)`）。
  → **O(N²)：6000 台＝3600 萬次比對，每一次查詢都做一遍。**
- 更糟的是 `RecordListQueryService.BuildFilter`：
  `hostIds.SelectMany(id => _repository.ResolveHostKeys(id))`，而
  `ResolveHostKeys` ＝ `HostIdentityResolver.Expand(_hosts.GetAll(), hostId)`
  ——**`_hosts.GetAll()` 在迴圈裡**。使用者用主機群組篩 500 台
  ＝ **500 次「整份 hosts blob 讀 DB＋反序列化」＋500 次 O(N) 展開**。
- `HostStore.GetAll()` 就是 `Read()`，**沒有任何快取**：每一次呼叫都是一次 DB 讀取＋
  1.8 MB JSON 反序列化。全站叫它的地方有數十處（`HostLookup`、`HostDtoMapper`、
  `VisibilityService`、`PlanBulkClose`、`ResolveIssueOccurrences`…）。
- **這才是 S1 位置上真正的病**：不是參數上限，是「把可見範圍算出來」這件事本身太貴。

**N2【最高】`SaveMany` 的合併是 O(既有列數 × 本次列數)——批次合併不足以解決 S3／S4**

`IssueHandlingStore.SaveMany` 在 `Mutate` 內對每一筆要寫的資料做
`items.FirstOrDefault(h => SameIssue(...))` 線性搜尋。
6000 台時 `items` 是 324 萬列，一次 `BuildCase` 要寫 90 天
→ **2.9 億次字串比對**，還沒算整份序列化。
體檢 S3／S4 的建議是「累積起來一次 `SaveMany`」——方向對，但**只做這一步仍然會卡**，
`SaveMany` 本身必須先建索引（`Dictionary<(host,date,key)>`）才有意義。

**N3【高】「依問題」視角每個問題群組各讀一次整份 blob**

`RecordListQueryService.BuildIssueGroup` 是在 `.Select(g => BuildIssueGroup(g, ...))` 裡逐群組呼叫的，
而它內部有：

```csharp
var issueHandlings = _issueHandlings.GetMany(hostNames, from, to);  // → Read() 整份
var openCases = _cases.GetMany(hostNames);                          // → Read() 整份
```

→ **相異問題數 × 2 次整份 blob 反序列化**。1000 種問題 ＝ 2000 次讀 324 萬列的 blob。
**這正是需求 (1) 要當主視角的那個畫面**，也是體檢完全沒抓到的一項
（因為測試環境只有 1 台主機、56 個問題、blob 只有幾十列）。

**N4【高】站台啟動時整份讀取處理歷程；`GetLogs` 也是全表掃描**

`RecordHandlingStore` 建構式為了取 `_lastLogId` 呼叫 `ReadAllLogs()`
——它是 **Singleton**，所以在第一個需要它的請求上同步讀取千萬列 `lf_log_lines`
並逐行 JSON 解析。使用者看到的是「第一次開頁面轉很久甚至逾時」。
體檢 S5 有提到，但歸類在「中」；以 6000 台的歷程量看，這是啟動即停擺等級。

**N5【高】`QueryPage` 有一顆「舊列地雷」：只要存在一列 `HostId=0`，全站分頁退回記憶體**

```csharp
var hasLegacyHostRows = ctx.DailyRecords.Any(r => r.HostId == 0);
if (!hasLegacyHostRows) { /* SQL 端 OFFSET/FETCH 真分頁 */ }
// 否則：整個查詢窗撈回記憶體排序＋分頁
```

註解說得很清楚是為了正確性，也確實正確。但語意是**全域開關**：
資料庫裡只要殘留任何一列 `host_id = 0`（歷史資料、或某次匯入異常），
6000 台 × 30 天的問題查詢就會一次撈回 18 萬筆、反序列化近 1 GB——**每翻一頁一次**。
目前沒有任何地方會告訴管理者「你的 DB 裡有這種列」。

#### ▸ 整體回看（§7）

單看 §7 會得到「修 S1、S3、S4 三個小地方就能上線」的印象。放大到整份文件後結論不同：

1. **S1 換成 N1 之後，它與 S6、S8 其實是同一根因**——「主機集合的取得與傳遞」。
   分開修會改三次同一段程式。
2. **S2 從「可延後」變成「最前面」**：它既是硬失敗（OOM），
   又是 §10 提案 E（排除已有結論的問題）、X1（問題層級收斂）、M10（批次預覽）
   共同的資料前提。先做 S3／S4 的小修再回頭做 S2，那兩個小修會被 S2 重寫一次。
3. **S6 不該當成獨立的效能題**——它與需求 (1)「主視角改成問題」是同一件事：
   兩者都需要「在 SQL 端以問題為單位聚合」。分開做會做兩次。

### 2.2 體檢 §8「UI/UX 管不管得動」X1～X7

複查後同意全部七項的事實描述。以 `ui-ux-pro-max` 的準則庫覆核，補三點依據與一處建議改判：

| 項次 | 準則依據（ui-ux-pro-max） | 調整 |
|---|---|---|
| X1 問題種類上千無收斂 | Style/Anti-pattern 明列 **「No filtering」**；Bar Chart「categories > 50 → paginated table」 | 維持高；**且確認「上千種問題不可能用圖表呈現」，必須是可篩選的表** |
| X2 授權矩陣無規模設計 | Bulk Actions：「Do: multi-select and bulk edit／Don't: single row actions only」 | 維持高 |
| X3 批次選取無「以篩選全選」 | 同上 | 維持高 |
| X4 autocomplete 截斷不說 | Content/Truncation：「Do: truncate with ellipsis **and expand option**」 | 維持中；與專案自己的「誠實申報」原則一致 |
| X5 我的交辦變收件匣 | — | 維持中 |
| X6 無復原／影響範圍追溯 | — | 維持中 |
| X7 Top 5 資訊量塌陷 | Anomaly Detection 圖型（AA 級）：「shape marker not color only ＋ text annotation per anomaly ＋ **anomaly summary list panel**」 | 維持低（現象）但**它的解法就是 §10**，不應分開處理 |

#### ▸ 整體回看（§8）

- X1 與 X7 是同一個問題的兩端（清單端 / 首頁端），而 §10 是它們**唯一**的解。
  三者必須合併成一個工作項，否則會出現「首頁排序改了、清單沒改」的不一致。
- X2／X3 是**設定面**的規模缺口，與 X1／X7 的**監控面**沒有共用程式碼，
  可以獨立排期而不影響需求 (1)。這是本輪唯一可以安全切開的一塊。
- X4 是 N1 的下游：主機 autocomplete 之所以只能 `Take(20)`，
  是因為底層是整份 blob 掃描。N1 修好後這裡才有條件回傳正確總數。

### 2.3 體檢 §10「以問題重要性呈現」10.1～10.9

這一節的**設計方向全部採納**，但落地方式有三處調整：

| 提案 | 採納狀況 | 調整理由 |
|---|---|---|
| 10.2 四維度、**不合成單一分數** | ✅ 全採 | 與專案「可解釋不黑箱」（`RiskBasisText`）一致 |
| 10.3 五個時間形狀訊號 | ✅ 全採 | 這正好就是需求 (1) 的「主機數＋期間跨度」的完整版 |
| 10.4 首頁三塊問句 | ✅ 採納，但**改成一張卡三分頁** | 見下 |
| 10.5 每列 sparkline | ⚠️ **降為選配，排在最後** | 見下 |
| 10.6 排除已有結論的問題 | ✅ 全採，**且提前** | 見下 |
| 10.7 報表氣泡圖 | ❌ **建議不做**，改用「異常偵測」型 | 見下 |
| 10.8 DTO 擴充欄位 | ✅ 全採 | 但實作方式必須是 SQL 端聚合（見 §四） |
| 10.9 落地順序 | ⚠️ **重排** | 見 §五 |

**10.4 改成「一張卡三分頁」而非三塊並排**
三塊並排在 1024×768（體檢 M2 已證實是現實解析度）會各只剩約 330px 寬，
放不下「問題名稱＋主機數＋變化幅度」。專案已有的分頁切換模式
（報表「主機排行／問題排行」同一張卡切換）直接沿用即可，不必新增元件。
預設落在「今天有什麼不一樣」。

**10.5 sparkline 降為選配**
準則：Line Chart 需 **≥4 個資料點**（14 天足夠 ✅）、
但「Differentiate series by line style not color alone」與「**必須有可切換的資料表**」
兩條在一個表格內的迷你圖上很難成立——一列一條迷你折線，做不出圖例也做不出資料表切換。
既然 10.3 的「出現密度」「變化幅度」「是否仍在發生」三欄已經用**文字＋數字**回答了同一件事，
sparkline 是錦上添花而非必要。排最後，且做的話必須讓密度欄同時存在（不可用圖取代文字）。

**10.6 提前到第一步**（體檢 10.9 也是這個結論，此處確認並強化理由）
它是唯一「不需要新圖表就能立刻讓首頁有用」的一步，而且它是
`BuildIssueRanking` 那個設計失誤（不看處理狀態 → 已標成已知雜訊的問題永遠霸榜）的正解。
**但它有前置**：要知道「這個問題在全部受影響主機上是否都已有結論」，
就得對 `issue_handling` 做跨主機跨日的查詢——在目前的 blob 架構下這是整份反序列化。
**所以 10.6 的前置是 S2，不是「小成本」。** 體檢把它評為「小」是低估了。

**10.7 氣泡圖建議不做，改用「異常偵測」型**
準則庫對 Scatter/Bubble 的評等是 **Accessibility Grade B**（全庫最低的可用等級之一），
並明列「**When NOT to use: mobile-primary context**」；本站有 375px 斷點的既有承諾（體檢 M3）。
再加上「>5000 點需先聚合」——問題種類上千、乘上主機維度後必然超過。
同一份準則庫裡，「Anomaly Detection」型（Line Chart with Highlights）是 **AA 級**，
且它的 A11y fallback 恰好就是專案已有的模式：
「text alert annotation per anomaly ＋ **anomaly summary list panel alongside chart**」。
→ **報表改放「近 30 天問題數趨勢折線＋異常點標記（形狀而非只用顏色）＋右側異常問題清單」**，
比氣泡圖更符合準則、更便宜，而且直接回答 10.4 第一塊的問句。

#### ▸ 整體回看（§10）

- §10 表面上是「呈現層提案」，實際上**九成成本在資料層**：
  10.3 的五個訊號、10.6 的處理狀態、10.8 的八個欄位，
  在目前架構下每一個都是「把整段期間的紀錄撈回記憶體再算」或「整份 blob 反序列化」。
  **先做 UI 再回頭優化＝白做兩次**（§10.8 自己也這樣寫了，此處確認）。
- 反過來看：一旦資料層改成「問題」為聚合單位，§10 的一到四階段成本會大幅下降，
  X1／X7 也順帶解決。**§10 不是額外工作，它是需求 (2) 的驗收畫面。**

### 2.4 體檢 §1～§3（H1～H6／M1～M13／L1～L11）

這 30 項與規模無關者維持體檢第五節的分批，不重排。只有三項因為本規劃而改變位置：

| 項次 | 體檢位置 | 改判 | 理由 |
|---|---|---|---|
| **M10** 批次 modal 無筆數上限 | 第 2 批 | **併入本規劃的批次閘門**（§五 P2） | 它與 S3／S7／X3 是同一件事的四個面向，分開改會動到同一批檔案四次 |
| **M7** 儀表板無「我手上有幾件」 | 第 3 批（需設計決策） | **併入 §10 的首頁改版** | 首頁本來就要重排，順手做比另開一輪便宜；資料來源（`GetTodo`）已現成 |
| **H5** 整列可點無鍵盤支援 | 第 1 批 | **維持第 1 批，且必須先做** | 準則 Severity: High；且 §10 的新清單、三分頁卡都是整列可點，先修 `renderTable` 才不會又生出一批不可鍵盤操作的列 |

#### ▸ 整體回看（§1～§3）

第 0 批（H2／H3／H4／M9）與第 1 批（H5／H6／M1／M6／M4）都是**共用元件層**的小修，
與本規劃的資料層改造**完全不衝突、可平行進行**，而且第 1 批修完之後
§10 的新畫面天生就是可鍵盤操作的。**建議這兩批先合併成一個前置小輪次做掉**，
不要排在資料層改造之後——否則新畫面會再繼承一次同樣的缺陷。

---

## 三、收斂：44 項體檢發現 + 5 項新發現 → 五個根因

這是需求 (3) 「不侷限於單一區塊」的結論。逐項修會改到同一批檔案四五次；
按根因修，一刀解決一整群。

| 根因 | 一句話 | 涵蓋的體檢／新發現項目 |
|---|---|---|
| **A. 主機集合的取得與傳遞** | 每次查詢都重新讀整份 hosts、重算 O(N²) 別名展開、再把結果塞進查詢條件 | N1、S1、S8（HostAdminService／HostLookup／NextId）、X4 |
| **B. 整份 JSON blob 的讀改寫** | 會隨「主機數×天數」成長的資料放在單一 JSON 欄位裡 | S2、S3、S4、S5、N2、N3、N4 |
| **C. 全量載入後在記憶體聚合** | 問題不是 SQL 端的聚合單位，只能撈回全部紀錄再 GroupBy | S6、N5、X1、X7、§10 全部、需求 (1) |
| **D. 批次寫入沒有規模閘門** | 沒有上限、沒有進度、沒有背景化、沒有「以篩選全選」 | S3、S7、M10、X3、X6 |
| **E. 呈現層沒有把「問題」當一等公民** | 有依問題視角，但排行只看數量、清單無收斂、首頁無變化維度 | §10 全部、X1、X7、M7、需求 (1) |

**根因之間的相依**（決定施工順序）：

```
A ──┐
    ├──> C ──> E          A 不修，C 的每一次聚合都要先付一次 O(N²)
B ──┘                      B 不修，C 拿不到處理狀態、E 的 10.6 做不出來
    └──> D                 D 的寫入路徑建在 B 之上
```

---

## 四、需求 (1) 的資料模型設計（本規劃的核心）

> 需求原文：「整體主要面向放在問題本身，同時顯示包含此問題的主機數量與期間跨度」

### 4.1 現況為什麼做不到

「一個問題影響幾台、跨哪段期間」目前只能這樣算：

```
_repository.Query(filter)            → 撈回期間內全部 DailyAnalysisRecord（含完整 ContentJson）
  → 反序列化每一筆的 TopIssues
    → GroupIssuesBySignature 在記憶體 GroupBy
      → Distinct 算主機數、Min/Max 算日期
```

6000 台 × 30 天 ＝ **18 萬筆紀錄、約 900 MB ContentJson、數百萬個問題物件**，
儀表板每次載入都做一遍，報表還要再做一遍前期對比（雙倍）。這條路沒有優化空間，只能換路。

### 4.2 設計：把 `lf_top_issues` 從「篩選子表」升級為「問題事實表」

`lf_top_issues` 已經存在且已有索引，目前只被 `ApplyPushableFilters` 用來做 `EXISTS` 篩選。
它缺的是**聚合所需的維度**：

| 新增欄位 | 型別 | 來源 | 用途 |
|---|---|---|---|
| `host_id` | bigint | 父列 `lf_daily_records.host_id`（去正規化） | 主機數、可見範圍下推 |
| `record_date` | date | 父列 `record_date` | 期間跨度、出現密度、前期對比 |
| `event_count` | int | `LogIssueSignature.Count` | 總次數 |
| `elevates_day_risk` | bit | `LogIssueSignature.ElevatesDayRisk` | 「重大」旗標（10.2 維度 1 的缺口） |

新增索引：`(source_name, event_id, record_date)`、`(record_date, host_id)`。

聚合就變成一句 SQL：

```sql
SELECT source_name, event_id,
       COUNT(DISTINCT host_id)     AS host_count,      -- 主機數（需求 1）
       MIN(record_date)            AS first_seen,      -- ┐ 期間跨度（需求 1）
       MAX(record_date)            AS last_seen,       -- ┘
       COUNT(DISTINCT record_date) AS active_days,     -- 出現密度（10.3）
       SUM(event_count)            AS total_count,
       MAX(severity_rank)          AS max_severity,
       MAX(CAST(elevates_day_risk AS int)) AS elevates
FROM lf_top_issues
WHERE record_date BETWEEN @from AND @to
  AND host_id IN (…可見範圍…)
GROUP BY source_name, event_id
```

**一次查詢取代「撈 18 萬筆 JSON 回來 GroupBy」。** 前期對比就是同一句換日期區間。

### 4.3 這個設計的連鎖效益（為什麼它是「最佳解」而非只是優化）

| 它同時解決 | 怎麼解 |
|---|---|
| S6 儀表板／報表全量載入 | KPI／類別／問題排行改 `GROUP BY`，只把 Top N 拉回應用層 |
| X1 問題種類無收斂 | 有了 `host_count`，「只看影響 ≥N 台」變成 `HAVING`，不是撈回來再篩 |
| X7 Top 5 資訊量塌陷 | 前期對比只是同一句 SQL 換區間，「新出現／變化幅度」變成便宜的欄位 |
| §10.3 五個時間形狀訊號 | 五個全部由這句 SQL 直接產出，**不需要新的儲存** |
| §10.8 八個 DTO 欄位 | 除「處理狀態」外全部到位 |
| 需求 (1) 主機數＋期間跨度 | `COUNT(DISTINCT host_id)` 與 `MIN/MAX(record_date)` |

### 4.4 剩下的那一塊：處理狀態必須也能 join（＝S2）

`§10.6`（排除已有結論的問題）與依問題視角的「處理概況」需要
**(主機, 日期, 問題簽章) → 狀態**。這份資料現在在 `issue_handling` 這個 JSON blob 裡，
沒辦法 join。因此：

**`issue_handling`／`issue_cases`／`record_handling` 三份必須落成真表**，欄位對應現有模型即可：

```
lf_issue_handling   host_name / date / issue_key / status / actor_id / actor_account /
                    note / due_date / case_id / updated_at
                    PK(host_name, date, issue_key)
                    索引：(host_name, date)、(issue_key)、(case_id)、(status, due_date)

lf_issue_cases      case_id PK / host_name / issue_key / issue_label / status / handler_id /
                    note / due_date / first_linked_date / last_linked_date /
                    closed_at / created_at / created_by_account / updated_at
                    索引：(host_name, issue_key, closed_at)、(handler_id, closed_at)

lf_record_handling  host_name / date / status / handler_id / due_date / note / updated_at
                    PK(host_name, date)
                    索引：(handler_id)、(status)
```

**選 `host_name` 而非 `host_id` 當鍵**：維持與現有模型完全一致的語意
（處理狀態一律以**現行主機名稱**為鍵，見 `RecordListQueryService.HostNameOf` 的合併處理），
改鍵會牽動合併／墓碑列的整套規則，不在本規劃範圍內。

`hosts`／`users`／`user_groups`／`host_groups`／`group_access`／`rules`／`noise_marks`
**維持 blob**——它們隨組織規模成長（數千筆上限），不隨天數成長，
且 §4.5 的快取足以解決它們的成本。這一刀刻意只切「會隨主機數×天數成長」的三份。

### 4.5 A 根因的解法：blob 讀取加「版本戳快取」

不要在各 store 各自加快取（遲早有人漏掉失效）。改在**單一咽喉點**做：

- `lf_blobs.UpdatedAt` **已經是 EF 的 ConcurrencyToken**（每次寫入必更新）。
- `EfJsonBlobStore.Read()` 先做一次 `SELECT updated_at WHERE blob_key=@k`
  （PK seek，約 0.1 ms），與快取的版本戳相同就回傳快取的**已反序列化結果**。
- `JsonBlobCollection` 快取 `List<T>`，回傳唯讀視圖（或防禦性複製，見下方風險）。

**為什麼這樣仍然滿足 WEB-SPEC §7.1「範圍不進 JWT，每次請求即時解析」**：
版本戳查詢仍然打 DB，批次程序改了 hosts，Web 端下一次讀就會看到新版本戳而失效。
語意是「即時」，省掉的只有反序列化，**不是快取了授權結果**。

搭配兩項：
- `HostIdentityResolver` 增加「一次建索引」的入口（`Surviving` 的結果建成
  `Dictionary<hostId, survivingId>`，展開從 O(N²) 降為 **O(N)**），
  索引本身隨版本戳一起快取。
- `RecordListQueryService.BuildFilter` 改成先取一次索引再展開，不要在迴圈裡呼叫 `GetAll()`。

---

## 五、施工階段（含影響面與驗收）

> 每一階段都是**可獨立驗證、可獨立合併 dev** 的單位，沿用專案既有分支流程
> （feature → dev 實測 → master）。階段之間的閘門寫在「進入條件」。

### P0　量測基準與壓測資料（不改任何行為）

| 項目 | 內容 |
|---|---|
| **做什麼** | 一支測試專案內的資料產生器：可產出 N 台 × M 天的 `lf_daily_records`＋`lf_top_issues`＋`issue_handling`＋`issue_cases`，可指定問題種類數與案件密度。預設兩組：2000×90、6000×90 |
| **為什麼先做** | 體檢 §0 的最大盲區就是「規模未驗證」；沒有基準數字，後面每一階段都無法證明改善，也無法證明沒改壞 |
| **影響面** | 只加 `LogForesight.Tests`（或一支 dev-only 的產生器）；不動產品程式碼 |
| **驗收** | 在 6000×90 的 SQLite DB 上記錄下列基準耗時：儀表板 summary、報表 summary、問題查詢四視角首頁、統一標記預覽、夜間掛接。**這五個數字是後面所有階段的對照組** |
| **預期結果** | 多半會直接重現 §零 修正 2 的 OOM——那就是最好的證據 |

### P1　根因 A：主機集合的取得與傳遞

| 項目 | 內容 |
|---|---|
| **做什麼** | (a) `EfJsonBlobStore`／`JsonBlobCollection` 版本戳快取；(b) `HostIdentityResolver` 索引化，`VisibleHostKeys` O(N²)→O(N)；(c) `BuildFilter` 移除迴圈內 `GetAll()`；(d) ViewAll 時不下推 `Hosts` 條件（改為「不加 WHERE」，省掉 OPENJSON join 與那個讓索引失效的 `OR host_id = 0`）；(e) 主機 autocomplete 回傳總數（X4） |
| **影響面** | `LogForesight.Core/Persistence/JsonBlobCollection.cs`、`Sql/EfJsonBlobStore.cs`、`Models/HostIdentity.cs`；`LogForesight.Web/Repositories/RecordRepository.cs`、`Services/VisibilityService.cs`、`RecordListQueryService.cs`、`HostAdminService.cs`。**全站每一支查詢都會經過**，是本規劃風險最高的一階段 |
| **風險與對策** | ① 快取回傳的 `List<T>` 若被呼叫端就地修改會汙染快取 → 回傳唯讀包裝或複製，並以合約測試釘住；② ViewAll 不下推會改變 `filter.Hosts == null` 的語意（現行慣例是「空集合＝零結果」）→ 必須明確區分 null（不限制）與空集合（零結果），這是既有陷阱（`TryApplyDayRiskVisibility` 的註解已經踩過一次），要在型別或註解上講死；③ 批次與 Web 是兩個行程，快取失效靠版本戳，需補一條跨行程測試 |
| **驗收** | P0 的五個基準數字全部改善；既有 1384 條測試全綠；新增：快取失效合約測試、O(N) 展開的等價性測試（與舊實作逐位比對） |

### P2　根因 D：批次寫入的規模閘門（可與 P1 平行）

| 項目 | 內容 |
|---|---|
| **做什麼** | (a) `PreviewIssueCaseAssign`／`PreviewBulkClose` 加上限（建議 200）＋回傳總數，前端顯示「顯示前 200 台，共 N 台將受影響」（M10）；(b) 後端對單次批次寫入的規模設硬上限並回報實際寫入筆數（S7 的第一步）；(c) 主機批次工具列加「全選符合目前篩選的 N 台」——後端以**篩選條件**執行，不傳 ID 清單（X3，同時補回 hosts.csv 退役的能力缺口）；(d) 批次結果 modal 提供「檢視這次影響的清單」連結（X6） |
| **為什麼可平行** | 只動 Service 的入口與 DTO、前端 modal，不碰 P1 的查詢路徑 |
| **影響面** | `IssueHandlingCommandService`、`HostAdminService`、`records.js`、`hosts.js`、對應 DTO |
| **刻意不做** | S7 的「觸發→背景→輪詢」完整非同步化。理由：那要沿用排程那一套 Mutex＋進度狀態機，成本與 P3 相當；先用上限把問題壓在瀏覽器逾時之內，等 P3 讓單次寫入變快之後再評估還需不需要 |
| **驗收** | 6000 台環境對 `DCOM 10016` 開統一標記 modal，1 秒內回應且畫面誠實顯示總數；「以篩選全選」對 500 台套用群組成功 |

### P3　根因 B：處理狀態／案件／日快照落真表

| 項目 | 內容 |
|---|---|
| **進入條件** | P0 完成（要有 OOM 的實證與基準數字） |
| **做什麼** | (a) 依 §4.4 建三張表，走 `SchemaUpgrader` 的冪等 DDL（與 `lf_risky_events` 完全同一套作法，已有前例）；(b) `IssueHandlingStore`／`IssueCaseStore`／`RecordHandlingStore` 改成 EF 實作，**介面完全不變**；(c) 現有 blob 資料的一次性遷移（開機偵測到舊 blob 且新表為空時搬移，搬完保留 blob 當備份、只記 log 不刪）；(d) `GetLogs(hostName, date)` 下推 SQL，`_lastLogId` 改 `MAX(seq)`（S5／N4）；(e) `SaveMany` 的合併改索引化（N2） |
| **影響面** | Core 的三個 store 與 `LfDbContext`／`SchemaUpgrader`；**呼叫端理論上零修改**（介面不變是這個設計的重點）。但 `IssueCaseCoordinator`、`HandlingHistoryQueryService`、`RecordListQueryService`、`IssueHandlingCommandService`、`DayHandlingCommandService` 都會因為「不再需要整份讀」而有可簡化之處——本階段**不順手改**，留到 P4 一起 |
| **風險與對策** | ① 這是全案最大的一刀，也是唯一會動到既有資料的一刀 → 遷移必須冪等、可重跑、且失敗不破壞舊 blob；② 每個 store 依 WEB-SPEC §12 慣例**必須補一組 SQLite 合約測試**（「新增 store 時，SQLite 合約子類為必要項」）；③ 原子性語意改變：`Mutate` 的「整段互斥」換成資料庫交易＋PK 衝突處理，`EfJsonBlobStore` 的重試迴圈語意要在新實作上等價重現 |
| **驗收** | 合約測試逐位比對新舊實作；6000×90 的資料集上單筆標記 < 100 ms、`BuildCase`（90 天回溯）< 1 s；夜間掛接（6000 台）較 P0 基準大幅下降；OOM 不再重現 |
| **同時完成** | S2、S3、S4、S5、N2、N4 六項 |

### P4　根因 C：問題聚合下推 SQL（需求 (1) 的資料面）

| 項目 | 內容 |
|---|---|
| **進入條件** | P1＋P3 完成 |
| **做什麼** | (a) 依 §4.2 擴充 `lf_top_issues` 四欄＋兩索引（`SchemaUpgrader`）＋既有列的一次性回填；(b) 新增 `IIssueAggregateQuery`（Core）：期間＋可見範圍 → 問題聚合列（含 §10.3 五訊號與前期對比）；(c) `RecordStatsBuilder.BuildIssueRanking`／`DashboardService`／`ReportService`／`RecordListQueryService.SearchByIssue` 全部改走它；(d) 順手修 N5（`HostId=0` 舊列的全域退回）——改為「只對真的有舊列的主機退回」或提供管理頁的偵測與清理 |
| **回填的誠實邊界** | `host_id`／`record_date` 可由 join `lf_daily_records` 直接 UPDATE（便宜）；`event_count`／`elevates_day_risk` 只存在於 `ContentJson`，需逐筆解析回填 → 分批執行、可中斷續跑，**回填完成前排行的「總次數」欄要標示「統計中」**，不可靜默顯示偏低的數字 |
| **影響面** | `LfDbContext`、`SchemaUpgrader`、`EfAnalysisRecordStore`、`RecordStatsBuilder`、`DashboardService`、`ReportService`、`RecordListQueryService`、`IssueRankingDto`／`IssueGroupDto`。**批次分析邏輯零修改**——新欄位在寫入時由 `Append` 一併填入，同 `CategoryAggregator` 的既有分工（WEB-SPEC §10.3 的先例） |
| **驗收** | 儀表板／報表／依問題視角在 6000×90 下 < 1 s；新舊實作的數字逐位一致（以 P0 的資料集跑對照測試）；`SearchByIssue` 不再出現 N3 的「每群組兩次整份讀」 |
| **同時完成** | S6、N3、N5、X1（資料面）、§10.8 |

### P5　根因 E：呈現層改以問題為主視角（需求 (1) 的畫面面）

| 項目 | 內容 |
|---|---|
| **進入條件** | P4 完成；**且體檢第 0／1 批的共用元件小修（H2/H3/H4/M9/H5/H6/M1/M6/M4）已完成**——見 §2.4 整體回看 |
| **做什麼** | (a) 依問題視角清單加「主機數／期間跨度（`first_seen ~ last_seen`）／出現密度（`N/M 天`）／是否仍在發生」四欄，並加「只看影響 ≥N 台」與「本期新增」快捷（X1）；(b) 首頁「重點問題」改成一張卡三分頁（10.4 調整版），排除已有結論的問題並在卡底誠實申報「另有 N 個問題已有結論（未列入）」（10.6）；(c) 報表問題排行加「新出現」徽章與變化幅度（10.3）；(d) 報表新增「近 30 天問題趨勢＋異常點標記＋右側異常清單」取代 10.7 的氣泡圖；(e) 側欄「我的交辦」未結案數 badge（M7） |
| **設計準則對照** | 排行卡維持 Top N＋「其他 N 個」（Bar Chart「>15 類改表格」）；數字一律 `formatNumber` 千分位（Content/Number Formatting）；異常點用**形狀**而非只用顏色（Anomaly Detection A11y）；新欄位維持 `tabular-nums` 與 `--lf-font-mono`（DESIGN-SYSTEM §3）；三分頁與新清單列全部走 P5 前置修好的可鍵盤 `renderTable` |
| **影響面** | `dashboard.js`、`reports.js`、`records.js`、`core/ui.js`、`core/charts.js`、`site.css`；`WEB-SPEC.md` §9.1／§9.2／§9.6 需同步改寫 |
| **刻意延後** | 10.5 sparkline（見 §2.3）。若實測後仍要，另開小輪次 |
| **驗收** | 6000 台資料集下首頁三分頁各自有內容且不重複；已標成「已知雜訊」的 `DCOM 10016` 不再出現在重點清單，且「另有 N 個」可展開；1024×768 與 375px 下不破版 |

### P6　設定面規模工具（可與 P3～P5 平行，獨立輪次）

X2（授權矩陣搜尋／凍結表頭／以部門為主的清單檢視）、X5（我的交辦分頁與逾期快捷）。
與需求 (1)(2) 沒有共用程式碼，排期完全獨立，建議併下一輪回饋一起談。

---

## 六、對整個專案的影響評估

### 6.1 會動到的文件（必須同 commit 更新）

| 文件 | 需要改的段落 |
|---|---|
| `docs/DB-SPEC.md` | 新增 `lf_issue_handling`／`lf_issue_cases`／`lf_record_handling` 三表；`lf_top_issues` 增四欄；Schema 升級機制新增條目 |
| `docs/WEB-SPEC.md` | §9.1（首頁重點問題卡改版）、§9.2（依問題視角新欄位與篩選）、§9.6（報表）、**§10.2 儲存介面對照表**（三個 store 從 blob 移到真表）、§10.5（SQL 後端說明）、§12（新 store 的合約測試） |
| `docs/DESIGN-SYSTEM.md` | 若 P5 引入新的圖表型別，補一節（沿用既有 8 類色盤，不新增色票） |
| `docs/BACKLOG.md` | 移除「儀表板重點問題卡不含未處理數」（P5 解決）；`ExportReportPruner`／伺服器端 CSV 匯出可在 P4 之後重新評估 |
| `docs/UX-AUDIT-2026-08-05.md` | 建議**加一段修正註記**指向本文件 §零，避免日後有人依 S1 的錯誤前提排工 |

### 6.2 不會被影響的部分（先確認清楚，避免過度擔心）

- **批次分析邏輯零修改**：P4 的新欄位在 `EfAnalysisRecordStore.Append` 寫入時填入，
  分析層（`LogAnalysisService`／`CategoryAggregator`／`CorrelationAnalyzer`）看不到這張表。
  這與 WEB-SPEC §10.3 當初加 `lf_record_categories` 四個計數欄是同一套分工，已有前例。
- **授權模型不變**：三層授權、負責人路徑、案件授與的規則一條都不動。
  P1 只改「怎麼算得快」，不改「算出什麼」。
- **API 形狀大致不變**：P4／P5 只在既有 DTO 上**加欄位**（`IssueRankingDto`／`IssueGroupDto`），
  不改端點與參數；既有的下鑽 URL 全部沿用。
- **NetIQ／Linux／AI 三條線完全不受影響。**

### 6.3 風險總表

| 風險 | 等級 | 對策 |
|---|---|---|
| P3 資料遷移把既有處理狀態弄丟 | **高** | 冪等、可重跑、失敗不動舊 blob；遷移後 blob 保留不刪；遷移前後筆數與抽樣內容比對寫成測試 |
| P1 快取造成授權範圍過期（看到不該看的） | **高** | 版本戳每次仍打 DB；補跨行程失效測試；ViewAll 不下推的 null／空集合語意要在型別上講死 |
| P4 回填未完成期間數字偏低 | 中 | 畫面標示「統計中」，不靜默顯示錯數字（沿用專案「涵蓋範圍要誠實」原則） |
| 一次改太多導致無法定位回歸 | 中 | 六個階段各自可合併 dev 實測；每階段以 P0 的基準數字對照 |
| P5 前置（共用元件小修）沒先做，新畫面再繼承一次無障礙缺陷 | 中 | 寫成 P5 的進入條件（已列） |
| SQLite 與 SqlServer 語意漂移 | 中 | 三個新 store 各補 SQLite 合約測試（WEB-SPEC §12 既有規定） |

### 6.4 「死結／卡死停擺」的實查結論（需求 2 的直接回答）

- **典型死結（deadlock）：目前不存在。** 各 blob store 是各自的 Singleton、各持一把
  `EfJsonBlobStore._lock`，且 `Mutate` 的委派內只操作記憶體清單、**不會巢狀取得第二把鎖**，
  因此沒有鎖順序反轉的條件。
- **真正會發生的是「鎖擁塞」與「交易阻塞」，效果等同卡死**：
  1. 統一標記對 1000 台 × 30 天 ＝ 30,000 次 `Mutate`，每次都獨佔
     `issue_handling` 的那把鎖並序列化整份數百 MB 字串——期間**所有**碰到處理狀態的請求全部排隊。
  2. `lf_blobs` 是一張只有二十幾列的小表，SqlServer 的頁級鎖會讓**不同 key** 的並發寫入
     也互相阻塞；現有 `IsTransient` 重試上限 5 次，持續高載時會直接拋 500。
  3. `RecordHandlingStore` 的 Singleton 建構式整份讀 `handling_log`，
     千萬列時第一個觸發它的請求會長時間無回應（N4）。
- **P3 是這三項的共同解**：換成真表之後，寫入是單列 UPDATE／INSERT，
  鎖粒度從「整份文件」降到「一列」，第 1、2 點自然消失；第 3 點由 `MAX(seq)` 解決。
- **P2 的上限是 P3 之前的止血**：在 P3 完成前，用筆數上限把最壞情況壓在可接受範圍內。

---

## 七、與體檢報告優先序的差異對照

| | 體檢 §9 的上線阻擋順位 | 本規劃 | 差異理由 |
|---|---|---|---|
| 1 | S1 可見範圍 IN 參數 | **P1（根因 A）** | 位置對、病因錯：不是參數上限（已實測），是 O(N²) 展開＋無快取 |
| 2 | S3 統一標記逐筆寫入 | 併入 **P3** | 單獨做「累積後 SaveMany」不夠，`SaveMany` 本身是 O(N×K)（N2） |
| 3 | S4 夜間掛接迴圈內重寫 | 併入 **P3** | 同上 |
| 4 | M10 批次 modal 無上限 | **P2** | 維持 |
| 5 | X3 批次選取 | **P2** | 維持 |
| 6 | X2 授權矩陣 | **P6（獨立輪次）** | 與需求 (1)(2) 無共用程式碼，不該卡在同一條路徑上 |
| 7 | S6 全量載入聚合 | **P4** | 升級：它與需求 (1) 是同一刀 |
| — | S2 刻意排除在阻擋項外 | **P3，全案最前面的大刀** | 它是硬失敗（OOM），且是 10.6／X1／M10 的共同前提 |

---

## 八、第二輪上帝視角複查（2026-08-06 追加）

第一版寫完後重新從整體回看，發現**本規劃自身有三個設計缺陷**，另有**五項企業級穩定度問題**
是體檢與第一版規劃都沒有涵蓋的。以下修正取代前面對應段落的敘述。

### 8.1 本規劃自身的三個缺陷

**缺陷 1：P4 的聚合鍵與處理狀態的鍵對不起來，`§10.6` 因此做不出來**

- 「依問題」視角與 `BuildIssueRanking` 的分組鍵是 **(Source, EventId)**；
- 處理狀態（`IssueHandling.IssueKey`）的鍵是 **完整簽章** `LogName|Source|EventId|(int)EntryType`
  （`IssueSignatureKey.For`）。一個 (Source, EventId) 群組可能對應**多個**完整簽章。
- §4.2 的 `GROUP BY source_name, event_id` 拿不到 `issue_key`，
  就無法 join 處理狀態 → **10.6「排除已有結論的問題」在 P4 的設計下做不出來**。
- **修正**：`lf_top_issues` 除了 §4.2 的四欄，**再加 `log_name` 與 `entry_type` 兩欄**
  （或直接存組好的 `issue_key`）。聚合仍以 (Source, EventId) 為輸出單位，
  但子查詢能以完整簽章 join `lf_issue_handling`。索引改為
  `(source_name, event_id, record_date)` ＋ `(host_id, record_date, source_name, event_id)`。

**缺陷 2：`COUNT(DISTINCT host_id)` 會把合併過的主機重複計算**

- 主機合併後，舊識別（墓碑列）下的紀錄仍帶著舊 `host_id`。
  直接 `COUNT(DISTINCT host_id)` 會把同一台實體機器算成兩台。
- **順帶查到一個既有的不一致**：`RecordStatsBuilder.BuildIssueRanking` 用
  `x.Record.Host`（紀錄自帶的**原始名稱快照**）算主機數，
  而 `RecordListQueryService.BuildIssueGroup` 用 `HostNameOf(lookup, ...)`（映射到**存活主機**）。
  → 合併過的環境裡，**儀表板「重點問題」的主機數與依問題視角的主機數本來就會對不上**，
  WEB-SPEC §9.1「兩頁數字必然一致」的敘述在有合併主機時不成立。
- **修正**：`lf_top_issues.host_id` 一律寫入**存活主機 id**（寫入時經 `HostLookup` 映射），
  聚合才是「幾台實體機器」。既有列回填時同樣經映射。
  同時把 `BuildIssueRanking` 的主機數改走同一份規則，消掉既有不一致。

**缺陷 3：P3 換成真表會失去 blob 層的樂觀鎖，且 `host_name` 當鍵有 collation 陷阱**

- 現況 `lf_blobs.UpdatedAt` 是 EF `ConcurrencyToken`，
  這是「兩個人同時標記同一個問題不會靜默覆蓋」的**唯一防線**（WEB-SPEC §10.4）。
  換成 row 級的表之後，若不補 row 級並發權杖，就退化成「後寫的靜默蓋掉先寫的」——
  這正是體檢 §0 列為「未驗證」的併發衝突情境，會從「未驗證」變成「確定會發生」。
- §4.4 原本提議以 `host_name` 為主鍵成分。但 C# 的比對是 `OrdinalIgnoreCase`，
  SQLite 預設是 **BINARY（區分大小寫）**、SqlServer 常見為 **CI（不分）**——
  同一份資料在兩個後端行為不同。專案在 `EfAnalysisRecordStore.OwnedRows` 已經踩過這個坑
  （用 `UPPER()` 正規化），不該在新表再踩一次。
- **修正**：
  1. 三張新表**改以 `host_id` 為鍵**，`host_name` 降為顯示用快照欄。
     寫入端本來就由 hostId 解析（`DayHandlingCommandService` 的既有規則），
     讀取端也一律映射到存活主機，改用 id 反而比 name 更貼近真實語意，
     同時一併解掉 collation 與合併重複兩個問題。
  2. 每張表加 `updated_at` 當並發權杖，寫入以「條件式 UPDATE（`WHERE updated_at = @原值`）」執行，
     衝突時回 409 並讓前端提示「這筆已被他人變更，請重新整理」——
     不要靜默覆蓋，也不要無限重試。
  3. 遷移時 `host_name → host_id` 映射不到的列（主機已刪除）**搬進孤兒區並在管理頁列出**，
     不靜默丟棄（沿用專案「誠實申報」原則）。

### 8.2 企業級穩定度：五項體檢與第一版都沒涵蓋的

**E1【最高】夜間分析跑在 Web 行程內，與使用者請求搶同一份資源**

`SchedulerHostedService` 是 Web 的 `BackgroundService`，直接執行 `AnalysisOrchestrator`
（回饋第七輪 console 專案退場的結果）。6000 台的夜間分析與使用者請求共用
**同一個 thread pool、同一個 GC 堆、同一個 DB 連線池**。
分析期間的記憶體尖峰（尤其在 P3／P4 完成前）足以讓整站無回應，
而且沒有任何隔離手段（沒有連線數上限、沒有並行度節流、沒有讓出點）。

**E2【高】`Prune` 是「全部載入記憶體再逐列刪」**

```csharp
// EfAnalysisRecordStore.Prune
var stale = OwnedRows(ctx).Where(r => r.RecordDate < cutoff).ToList();  // 含完整 ContentJson
ctx.DailyRecords.RemoveRange(stale);
// EfJsonLogStore.Prune 同一形狀
```

正常每晚只刪一天份（6000 列）還好；但**只要停機幾天沒跑、或管理者調短保留天數**，
一次就要把數十萬列（含 GB 級 `ContentJson`）載進記憶體，再逐列 DELETE
＋`lf_top_issues` 的 cascade（數百萬列）。夜間批次會卡在清理階段，且是單一長交易。

**E3【高】啟動路徑會被 schema 升級與回填阻塞，且雙行程有 race**

`EnsureCreated` ＋ `SchemaUpgrader` 在啟動時同步執行。P3 建表、P4 回填若掛在同一條路徑上，
大 DB 會讓 **Windows Service 啟動逾時（預設 30 秒）**。
另外 `SchemaUpgrader` 是「檢查缺什麼→缺才補」，這個序列**不是原子的**——
Web 與批次（或多個實例）同時啟動時可能同時判定「缺」而重複執行 DDL。

**E4【中】`lf_log_lines` 是所有 append-only 資料的共用單表**

`audit`／`handling_log`／`batch_runs`／`batch_run_logs`／`import_logs`／`perm_changes`
全部擠在同一張表、同一個自增主鍵。千萬列規模下所有寫入都集中在索引尾端頁，
是天然的 INSERT 熱點；而且 `audit` 保留 730 天、`handling_log` 跟著同一張表一起長。

**E5【中】沒有健康檢查、沒有慢查詢門檻、沒有背壓**

目前只有 `[SQL]` 的 NLog 逐筆紀錄。要回答「現在是不是壞了」只能翻 log。
企業級部署需要一個可被監控系統輪詢的端點，以及「超過門檻的查詢主動告警」。

### 8.3 追加階段 P0.5：便宜的穩定度止血（建議立刻做）

以下四項**低風險、彼此無耦合、與所有其他階段無衝突**，成本合計小於 P1 一項：

| 項目 | 作法 | 解決 |
|---|---|---|
| `Prune` 改批次刪除 | 改用 EF Core 8 的 `ExecuteDelete()`（不載入實體）＋「每次最多刪 N 列、剩下的下次再刪」的刪除預算 | E2 |
| 啟動 DDL 互斥＋回填背景化 | DDL 用既有的 `NamedMutexGate` 包起來；回填一律背景執行＋進度可查詢＋未完成時畫面標示「統計中」 | E3 |
| `/health` 端點 | DB 連線、schema 版本、回填進度、排程狀態、最近一次批次結果 | E5 |
| 慢查詢門檻 | `[SQL]` log 已有耗時；超過門檻改記 Warn 並計數，`/health` 可查 | E5 |

### 8.4 E1（夜間分析與 Web 同行程）的處置建議

這是唯一需要你決定的架構取捨，兩條路：

| 方案 | 內容 | 代價 |
|---|---|---|
| **甲：維持同行程＋加隔離**（建議） | (1) orchestrator 走**獨立的連線字串**設 `Max Pool Size`，不與 Web 請求搶連線；(2) 每台主機之間讓出（`Task.Yield`／可設定的間隔），並限制 NetIQ 並行度；(3) 執行中於畫面標示「分析進行中，回應可能較慢」 | 小。但無法完全隔離 GC 與記憶體尖峰 |
| **乙：拆回獨立行程** | 重新引入一支 worker（Windows Service），Web 只負責觸發與看進度 | 大，且與回饋第七輪「console 專案退場」的決策相反；部署變成兩個服務 |

**建議走甲，並把決定點延後**：P3／P4 完成後，單台分析的 DB 成本與記憶體尖峰會大幅下降，
屆時用 P0 的基準數字重測；**若 6000 台實測下 Web 回應時間仍不可接受，再走乙**。
理由是乙的成本等同一次部署架構變更，不該在還沒有數據時先付。

### 8.5 修正後的階段順序

```
P0    壓測資料與基準量測（不改行為）
P0.5  穩定度止血：Prune／啟動互斥／health／慢查詢門檻     ← 新增，可立刻開工
前置  體檢第 0＋1 批共用元件小修（與資料層無關，可平行）
P1    根因 A：blob 版本戳快取＋別名展開改用既有 HostLookup
P2    根因 D：批次規模閘門（可與 P1 平行）
P3    根因 B：三張表落地（改 host_id 為鍵＋row 級並發權杖＋handling_log 移出共用表）
P4    根因 C：lf_top_issues 擴充（＋log_name/entry_type＋存活主機 id）＋SQL 聚合
P5    根因 E：問題主視角呈現層
P6    設定面規模工具（獨立輪次）
決策點 E1 是否拆行程 —— 以 P4 後的實測數據決定
```

## 九、下一步（需要你決定的六件事）

1. **階段切分是否接受**——特別是把 S2（P3）提前到 S3／S4 之前，這與體檢的建議相反。
2. **P0 要不要先做**——不改任何行為，但它是唯一能證明「修好了」的方法。
3. **P0.5 要不要立刻開工**——四項止血都很便宜，且與其他階段完全無耦合。
4. **前置的共用元件小修要不要先併成一個小輪次**——做完之後 P5 的新畫面天生可鍵盤操作。
5. **§8.1 缺陷 3 的鍵改成 `host_id`** 是否接受——這會動到遷移邏輯與孤兒處理。
6. **E1 走甲案（維持同行程＋隔離）** 是否接受，決定點延到 P4 之後。
