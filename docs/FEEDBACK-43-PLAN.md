# 回饋第 43 輪規劃：PRTG 取數效能——先量測再定案

> 狀態：規劃完成，六項待決已由使用者採納建議定案；批次 A～G 依序委派 impl-low
> 基準：dev@1c42277（3761 綠）
> 來源：使用者回報「抓取 sensor 有點慢」，PRTG 主機 CPU／RAM 約 60%
> 實作方式：批次 0 委派 `impl-low`（Opus，low）；規劃、驗收、後續批次定案由 Claude 做
> subagent：核對與終檢用 scan-low（Opus，low）；實作用 impl-low（Opus，low，本輪新建於 ~/.claude/agents/）

## 為什麼先量測

慢有兩個完全不同的來源，加速手段沒有交集，而且現在沒有任何一個數字能分辨：

| 來源 | 現況 | 可能的槓桿 | 不知道的事 |
|---|---|---|---|
| 結構同步分頁 | 每頁 500 筆寫死，sensors 要翻 86 頁、循序 | 分頁放大（探測已證明單次 50000 筆可行） | 每次請求的固定成本佔多少——決定放大有沒有效 |
| 數值擷取 | 每顆 sensor 一次 `historicdata`，併發鎖 1～3 | 併發上限放寬 | PRTG 端在併發 4／8 時延遲會不會放大（CPU 60% 看不出排隊） |
| 結構同步全量 | 每天重抓 42864 筆結構 | 先抓 objid 清單比對再補細節 | objid-only 查詢比全欄位省多少 |

沒有這三個數字就定案，等於拍腦袋。探測本來就是回答「環境長什麼樣」的工具，加一步量測是它的本分。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| 0（已完成） | 探測步驟 9：分頁大小、objid-only、historicdata 併發 | 1 檔＋測試＋spec §6 | 無 |
| 0b（已完成） | 探測補強：步驟 4 診斷、步驟 8 messages 時間判定、步驟 9d 快照／各 type 延遲／固定成本／messages 量級 | 同上 | 0 |
| A | historicdata 解析改用 `datetime_raw`／`value_raw`（P0：現況在這台 PRTG 一筆都解析不到） | Core 1 檔＋測試 | 無 |
| B | `PrtgClient` 辨識 PRTG 回的空白 HTML 頁，明講原因 | Core 1 檔＋測試 | 無 |
| C | 分頁大小 5000 與寫入批次 500 拆開 | Core 2 檔＋測試＋spec §3 | 無 |
| D | `PrtgFetchConcurrency` 範圍 1～8，說明文字改寫 | Web 3 檔＋測試 | 無 |
| E | 取數策略（保守／激進）設定＋快照背景服務＋夜間逐顆查詢依策略開關 | Core 4 檔、Web 5 檔、測試、spec | A、B |
| G | 校準與快照相容：可用列定義、三處品質統計、匯出欄位、判定文案 | Core 2 檔、Web 2 檔、測試、spec §11 | E |
| F | 文件：PRTG-SPEC §2／§3／§6／§11／§12、WEB-SPEC §9.9e／§9.9f、BACKLOG、CLAUDE.md 基線 | 文件 | A～G |

建議順序 A → B → C → D → E → G → F。A 到 D 各自獨立、都小，E 最大且依賴 A（快照與逐顆查詢寫同一張表，解析要先對）與 B（快照失敗要能分辨是空白頁）。

## 批次0：探測步驟 9 效能量測

### 現況與核對結果

- 探測（`PrtgProbeRunner`）現有 8 步，步驟 3 已用單次 `count=50000` 抓回全部 sensor，樣本存在 `sensorSamples` 但**沒有 objid 與 status**（`SensorTypeSample(Type, Unit, ParentId)`）。
- 步驟 8 是純診斷形式（失敗只印、不影響 `allOk`），步驟 9 沿用同一形式。
- 夜間取數的 historicdata 查詢形狀：`api/historicdata.json?id={objid}&avg=3600&sdate={yyyy-MM-dd-00-00-00}&edate={翌日 00:00}`（`PrtgFetchService.FetchValuesAsync`）。量測必須用同一形狀，否則量到的不是夜間的成本。
- 結構同步 sensors 的欄位：`objid,parentid,sensor,type,tags,unit,status,paused,dependency`。量測分頁大小要用同一組欄位。

### 定案

1. **步驟 9 三個子量測**，各自獨立容錯（任一失敗只印原因），整步不影響探測成敗：
   - **9a 分頁大小**：sensors、結構同步同一組欄位、`start=0`，依序 `count=500／2500／5000／50000`，每次印耗時、筆數、回應位元組，並換算「每千筆耗時」。結論行：`count=5000` 的每千筆耗時低於 `count=500` 的一半（**暫定**門檻）→「每次請求固定成本佔大宗，分頁放大有效」，否則「近似線性，放大效益有限」。
   - **9b objid-only**：sensors `count=50000`、`columns=objid` 一次，與 9a 的 50000 全欄位比耗時與位元組。結論行：objid-only 佔全欄位的百分比。
   - **9c historicdata 併發**：從步驟 3 樣本挑 status 不含「paus」的 sensor，type 優先常見型（Ping、SNMP CPU Load、SNMP Traffic 64bit、SNMP Memory），取 **64 顆分 4 組各 16 顆、不重複**——同一顆重查會吃到 PRTG 快取，讓後面的併發等級看起來變快。併發等級 1／2／4／8 各用一組；查詢形狀同夜間取數，日期取昨天。每級印：總耗時、平均與最大單次延遲、失敗數、回傳列數合計。結論行：各級相對等級 1 的「總耗時加速倍數」與「平均延遲放大倍數」；平均延遲放大超過 1.5 倍（**暫定**）的等級標「PRTG 端開始排隊」。不足 64 顆時用可得數量平均分配並印警告；一顆都沒有時印「無可用感測器，略過」。
2. **成本先講**：步驟開頭印「本步驟會發 64 次 historicdata 與 6 次 table.json」，讓管理者知道這一步不是免費的。
3. 步驟 3 的欄位加 `status`，樣本型別加 `Objid` 與 `Status`——9c 的選樣依賴它們。
4. 不寫資料庫、不新增設定、取消 token 穿透。

### 改動

1. `PrtgProbeRunner.cs`：步驟 9 與其三個子量測；步驟 3 樣本擴欄。
2. `PrtgProbeRunnerTests.cs`：新增測試，既有 stub 補路由。
3. `docs/PRTG-SPEC.md` §6：第 9 項一段。

### 測試／驗收

- 9a：四個 count 的查詢都發出、印四行耗時與一行結論。
- 9c：無可用 sensor → 印略過、探測仍回 true；樣本 8 顆 → 印分配警告、四級各一行；某次 historicdata 回 500 → 計入失敗數、不中斷、探測仍回 true。
- 既有探測測試全綠；全套 `dotnet test` 綠（基線 3761）。

## 批次 0 量測結果（2026-09-11 實機）

| 子量測 | 結果 | 意涵 |
|---|---|---|
| 9a | count=500 每次 3.9 秒、count=5000 每次 10.1 秒；每千筆 7.7 秒 → 2.0 秒 | 每次請求固定成本約 3 秒；感測器 86 頁 ≈ 5.5 分鐘，5000 一頁 ≈ 1.5 分鐘，再大沒有更省 |
| 9b | objid-only 9.3 秒、1.5 MB（全欄位的 12%／8%） | 清單比對便宜，但分頁放大後省的量不值得多一套邏輯 |
| 9c | historicdata 單次平均 21.5 秒、最大 95～107 秒；併發 8 總耗時 ×0.19、平均延遲 ×0.82 | 這個 API 在這台 PRTG 就是慢；併發 8 無排隊跡象 |
| 異常 | 步驟 4 相依性回 0 筆＋1 筆無法解析（上一輪同一查詢正常） | 整份回應無法當 JSON 解析，原始內容未知 |
| 異常 | messages 的 sortby=objid 無效、objid 非遞增 | messages 天生依時間排序，objid 判定不適用；要改看時間欄位 |

## 方案評估：值的取得方式

**改變優先順序的事實**：`lf_prtg_values` 目前沒有任何分析程式在讀（全 repo 只有儲存層與資料搬運碰它），
它在累積的是值型規則的基線（BACKLOG：校準頁判定「可用」後才有消費端）。每晚最貴的一段是在為「未來的消費端」
累積基線，而基線要的是**全部感測器長期樣本**，現在的取法只涵蓋觸發主機的幾百顆。

| 方案 | 取法 | 每日請求 | 涵蓋 | 精度 | 已知代價 | 要先驗證 |
|---|---|---|---|---|---|---|
| A 現況 | 每顆 historicdata、avg=3600、前一日 | 觸發主機感測器數（數百） | 幾百顆 | PRTG 真平均 | 單次 20 秒；all-mapped 範圍不可行 | — |
| B 快照 | 定時 table.json 一次拿全部 `lastvalue_raw`，自己平均成小時值 | 96（每 15 分）或 288（每 5 分） | 全部 42863 顆 | 取樣平均（每小時 4～12 點） | 站台白天要活著；主要頻道 only（現況亦然） | lastvalue_raw 與 historicdata value_ 尺度是否一致；快照一次成本；掃描間隔分布 |
| C 混合 | B 供日常基線；historicdata 只留回填與精確需求 | 同 B | 全部 | 同 B | 兩套邏輯並存 | 同 B |
| D 推送 | PRTG 通知（HTTP action）推狀態變更給站台 | 0（被動） | 全部 | 即時 | 要動 PRTG 端設定；只解 messages 不解數值 | messages 每晚量級（決定值不值得） |

**探測要補的證據**（批次 0b）：B 的三個驗證項、A 的「20 秒是不是固定成本」（1 小時 vs 1 天）、
各 type 的 historicdata 延遲（9c 只量了 Ping）、histdata 一列的原始鍵值（看頻道形狀）、messages 量級。
分頁放大（原批次 A）與快照方案無關，仍值得做；併發放寬對回填仍有價值，優先度降後。

## 批次0b：探測補強（診斷修正＋方案 B／D 的證據）

### 定案

1. 步驟 4：JSON 解析失敗時印回應長度與前 200 字（去換行），下次探測直接看得到原因。
2. 步驟 8 messages：查詢加 `datetime` 欄，判定頁內時間是否單調；單調 → 「依時間排序、順序穩定，分頁可行（列鍵含時間）」；sortby 無效改為資訊而非失敗。
3. 步驟 9d 四個子量測（純診斷，各自容錯）：
   - 9d-1 快照成本與分布：`columns=objid,status,interval,lastcheck,lastvalue,lastvalue_raw&count=50000` 一次；印耗時、筆數、位元組、interval 前 5 值分布、status 前 5 值分布、lastvalue_raw 可解析成數字的比例。
   - 9d-2 單位對照與各 type 延遲：五種 type（SNMP CPU Load、SNMP Memory、SNMP Disk Free、SNMP Traffic 64bit、Ping）各 3 顆未暫停、未被 9c 用過的感測器，各發一次 1 天 historicdata（同夜間形狀、循序）；每顆印 type、objid、耗時、histdata 最後一列原始鍵值（截 300 字）、快照的 lastvalue 與 lastvalue_raw；每 type 一行平均延遲。
   - 9d-3 固定成本：4 顆 Ping 查 1 小時、另 4 顆查 1 天（不重複、不與 9c／9d-2 重複）；印兩組平均延遲與列數。
   - 9d-4 messages 量級：`filter_drel=today` 與 `7days` 各 `count=1`，印 treesize。
4. 成本行更新：historicdata 64＋23 次、table.json 6＋3 次。

### 驗收

- 步驟 4 解析失敗 → 印「回應長度 N、開頭：…」；步驟 8 messages 時間單調 → 新結論字串；9d 各子量測對 `{}` 容錯、各印一行以上；探測回傳值不受影響；既有測試綠；全套綠（基線 3766）。

## 批次 0b 量測結果（2026-09-11 實機，第二次）

| 量測 | 結果 | 意涵 |
|---|---|---|
| 步驟 4／9a count=50000 | 兩者都拿到 73 bytes 的 HTML：`<HTML><BODY class="no-content"><B class="no-content">OK</B></BODY></HTML>`，HTTP 200 | PRTG 對大回應會**靜默放棄**回空白頁；昨天同一查詢成功。50000 不可靠，5000 兩次都穩 |
| 9c（第二次） | 併發 4 總耗時 ×0.39、延遲 ×1.23；併發 8 總耗時 ×0.55、延遲 ×1.81（排隊）；最大延遲 116 秒 | 併發表現日間差異大，PRTG 負載主導；8 會排隊且超過預設 60 秒逾時 |
| 9d-1 快照 | 六欄 42863 筆 37.8 秒、13 MB；interval 60 秒佔 77.6%、5 分鐘 13.0%；status Up 96.0%；lastvalue_raw 可解析 97.0% | 只帶兩欄的快照約 10 秒（9b 同量級）；每 5 分鐘一次不超過掃描密度 |
| 9d-2 尺度 | CPU／記憶體／磁碟／Ping：`lastvalue_raw` 與 histdata 第一頻道 `value_raw` 同尺度（磁碟連小數相同）；流量：快照是「最近一次掃描的傳輸量」、歷史值是「整小時總量」，同為位元組但語意不同 | 四類可直接當樣本；流量要標示為每次掃描量 |
| 9d-2 各 type 延遲 | CPU 11 秒、記憶體 34 秒、磁碟 23 秒、流量 49 秒、Ping 10 秒；單顆 0.3～72 秒 | historicdata 在這台就是慢且不穩 |
| 9d-3 | 1 小時 48 秒 vs 1 天 15 秒 | 成本以每次呼叫為主，與跨度無關 |
| 9d-4 | messages treesize today＝7days＝1,000,000 | 這個數字是上限值，沒有量級資訊；第三階段實際頁數要看夜間執行輸出 |
| histdata 形狀 | `datetime` 是本地化區間字串「2026/9/10 下午 11:00:00 - 上午 12:00:00」；`value` 是帶單位字串「4 %」；`value`／`value_raw` 每個頻道重複一組；另有 `datetime_raw`（OLE 日期）與 `value_raw`（純數字） | **現行解析用 `datetime`＋`value`，在這台 PRTG 一筆都解析不到**；夜間執行輸出應有「時間欄位無法解析」一行（待使用者確認） |

## 定案：兩種取數策略

**第一原則**：兩種策略的每個數值都從「這兩次探測實測能穩定取回」的範圍內挑。激進不是把數字推到極限，是在可靠範圍內取上緣。

| 項目 | 保守（預設） | 激進 | 證據 |
|---|---|---|---|
| 快照間隔 | 15 分鐘 | 5 分鐘 | 掃描間隔 77.6% 是 60 秒，5 分鐘不超過掃描密度；兩欄快照約 10 秒 |
| 快照查詢 | `columns=objid,lastvalue_raw,interval&count=50000`（同） | 同 | 9b 一欄 9.6 秒、9d-1 六欄 38 秒都成功；九欄 18 MB 失敗過 |
| 夜間逐顆歷史查詢（觸發主機） | **關**：數值全由快照供應 | **開**：沿用取數範圍設定，給觸發主機 PRTG 真平均 | 單顆 5～72 秒、每晚數百顆 |
| 回填併發 | 建議 2（設定獨立，範圍 1～8） | 建議 4 | 併發 4 兩次都加速 2 倍以上且延遲 ≤1.23；8 今天排隊 |
| 每日 PRTG 負擔（估） | 96 次快照 ≈ 16 分鐘，分散整天、同時只有 1 個請求 | 288 次快照 ≈ 48 分鐘 ＋ 夜間數百次 historicdata（併發 4 約 30～60 分鐘） | — |
| 自我保護（同） | 快照連續失敗 3 次 → 間隔加倍（上限 60 分鐘）並寫執行輸出；成功即恢復 | 同 | 第一原則 |

**激進策略的提醒文字**（UI 下拉切到激進時顯示，也寫進規格）：「激進策略每 5 分鐘對 PRTG 發一次全量快照，並在每晚對觸發主機逐顆查詢歷史值，PRTG 負載明顯較高。請先用保守策略觀察執行輸出裡的快照耗時與失敗數，確認 PRTG 承受得住再切換；切換後若看到快照間隔被自動拉長，代表 PRTG 已經回不來，請切回保守。」

**快照的資料語意**（寫進 PRTG-SPEC §2／§3）：
- 只存「有 ok 對應的裝置」底下、符合 sensor type 白名單、未暫停的感測器（與 `GetValueFetchTargets` 同一套選法，裝置集合取當天 host map 的 ok 對應；當天沒有就用前一天）。全量存 42863 顆會讓 `lf_prtg_values` 每天多 100 萬列，沒有消費端的資料不存。
- 每小時一列，`AvgValue`＝樣本平均、`MinValue`／`MaxValue`＝樣本極值（這兩欄從此有值）、`Coverage`＝樣本數 ÷ 期望樣本數 × 100（**與既有 PRTG coverage 同為百分比尺度**，期望樣本數＝60 ÷ 間隔分鐘）、`Quality`＝新值 `sampled`。
- **流量類（volume）主要頻道要正規化到小時量**：歷史值第一頻道是「整小時總量」（探測 9d-2：126,883 MB ≈ 296 Mbit/s），快照 `lastvalue_raw` 是「最近一次掃描的傳輸量」；不換算的話同一顆感測器的 ok 列與 sampled 列會差 60 倍，基線統計整個失真。快照查詢因此帶 `interval` 欄（`columns=objid,lastvalue_raw,interval`），對 type 屬於流量集合（**暫定**：`SNMP Traffic 64bit`、`SNMP Traffic 32bit`、`Windows Network Card`）的感測器，樣本值＝`lastvalue_raw × 3600 ÷ 掃描間隔秒數`；`interval` 解析不了時用 60 秒並在執行輸出計數。其他 type 不換算。
- 同一（感測器, 小時）若之後被逐顆歷史查詢或回填寫入，`ok` 覆蓋 `sampled`（Upsert 既有行為，精確值優先）。
- 快照累積在記憶體，整點寫入一次；站台重啟丟失的只有當前這一小時的部分樣本，coverage 會如實反映。

## 批次A：historicdata 解析改用原始欄位（P0）

### 現況與核對結果

- `ParseHistoricData` 只讀 `datetime`（`DateTime.TryParse`，目前文化）與 `value_`／`value`；全 repo 沒有 `datetime_raw`／`value_raw` 的解析。實機 histdata 的 `datetime` 是「起 - 訖」區間字串、`value` 帶單位，兩者都解析失敗。
- 唯一呼叫端是 `FetchValuesAsync`，夜間觸發式取數與歷史回填都走它。
- 既有測試替身全部只餵 `datetime`＋`value_`，沒有一個是實機形狀。

### 定案

1. 時間：優先 `datetime_raw`（數字，OLE 日期，`DateTime.FromOADate`）；缺失時退回 `datetime`，先以「 - 」切開取前段再 `TryParse`（InvariantCulture 與目前文化各試一次）。都失敗才計入無法解析。
2. 值：優先第一個 `value_raw`（數字或可解析字串）；缺失時退回既有 `value_`／`value` 邏輯。重複鍵取第一個＝主要頻道（System.Text.Json 的 `TryGetProperty` 行為）。
3. `coverage_raw`（整數，10000＝100%）優先於 `coverage` 字串。
4. 品質判定規則不變（NoData／Unknown／Ok）。
5. 既有測試不改斷言；新增一條用探測印出的實機列（CPU Load 那一列原文）驗證：時間＝2026-09-10 23:00、值＝3.5593、品質 ok、coverage 100。

### 驗收

- 實機形狀列解析正確；只有 `datetime`＋`value_` 的舊替身仍解析正確；`datetime_raw` 為非數字字串時退回 `datetime`。全套綠。

## 批次B：PrtgClient 辨識空白 HTML 頁

### 定案

1. `GetJsonAsync` 在狀態碼檢查通過、讀完內容後，若內容去空白後以 `<` 開頭，擲 `PrtgClientException`，訊息：「PRTG 回傳 HTML 而非 JSON（多半是伺服器端處理逾時或負載過高回的空白頁）：{開頭 80 字}」。既有的測試連線與 getpasshash 路徑已各有同型檢查，不動。
2. 影響面：分頁（階段失敗計數、訊息明確）、資源守門即時來源（退回鏡像，原因文字變得看得懂）、探測步驟 4／9a（改印無法量測＋原因）。探測步驟 4 的「回應無法解析」診斷行保留（其他非 HTML 的壞回應仍用得到）。

### 驗收

- 回 HTML 時擲例外且訊息含「HTML 而非 JSON」；回正常 JSON 不受影響；探測既有測試裡餵 HTML 的案例改斷言新訊息。全套綠。

## 批次C：分頁大小與寫入批次拆開

### 定案

1. `PrtgTablePager.FetchAsync` 的 `pageSize`（查詢 `count`、停止條件、上限推算）預設改 **5000**（暫定，兩次實測 10 秒／2 MB 穩定）；新增 `batchSize`（onBatch 沖洗門檻）預設 **500**，兩者獨立。
2. 「每滿 N 頁寫一行執行輸出」由 50 改為 **5**（感測器只剩 9 頁，50 永遠印不到）。
3. 記憶體：單頁最多 5000 筆 JSON（約 2 MB）＋ 500 筆緩衝，仍有界。
4. 三個呼叫端不動（吃預設）。

### 驗收

- 既有分頁測試改傳 `batchSize` 者維持語意；新增「pageSize 5000、batchSize 500 時 1200 筆資料 onBatch 被叫 3 次（500／500／200）且只發 1 次請求」。全套綠。

## 批次D：併發上限放寬

### 定案

1. 兩個 DTO 的 `[Range(1, 3)]` 改 `[Range(1, 8)]`，錯誤訊息同步；cshtml `max="8"`，說明文字改：「保守建議 2、激進建議 4；實測併發 8 時 PRTG 端開始排隊、單次延遲可能超過逾時。」
2. `SettingsController` 估算端點的註解與假設（「實機併發上限 3」）跟著改。
3. 新增測試：8 接受、9 拒絕。

## 批次E：取數策略設定＋快照背景服務

### 現況與核對結果

- 新增字串設定的接線共 14 處（模型、兩個更新 DTO、讀取 DTO、Service 的兩條寫入、驗證、四份稽核欄位、ToDto、前端載入／儲存、cshtml），樣板是 `PrtgValueFetchScope`。稽核四份是手寫匿名物件，漏一份不會編譯錯。
- 背景服務樣板：`AiAnalysisHostedService`（每輪先看前置條件、不成立整輪不跑、`TickAsync` internal 可單測）；既有服務的間隔都是編譯期常數，「間隔由設定決定」是新的。
- `GetValueFetchTargets(whitelist, deviceObjids)` 回未暫停的 sensor objid；`GetHostMapForDate(day)` 取當天對應；`UpsertValues` 以 (SensorObjid, PeriodStart) 為鍵、同鍵覆蓋、`CreatedAt` 一併更新。
- `PrtgDataQuality` 五個字串常數；讀取端三處都是 `== Ok`／`!= Ok` 與「其他」桶，新增 `sampled` 會落進「其他」，不會漏接但也不會有獨立計數（校準頁 §11 要不要分開算，見待決）。
- 「PRTG 擷取」下拉已合併 `PrtgEnabled` 與 `PrtgValueFetchScope` 兩個欄位；策略**不**併進去，另開一個下拉。

### 定案

1. 設定 `PrtgFetchStrategy`：`conservative`（預設）／`aggressive`，static class 同 `PrtgValueFetchScope` 形式（常數、IsValid、Normalize），並提供 `Profile(strategy)` 回 (SnapshotIntervalMinutes, NightlyExactValues)＝保守 (15, false)、激進 (5, true)。不合法值退回保守。
2. UI：擷取參數頁籤，緊接「PRTG 擷取」下拉之後新增「取數策略」下拉，兩個 option 各附一句說明；選到激進時顯示上面的提醒文字（`text-warning`）。popover 說明兩者差異與每日負擔估計。
3. 快照背景服務 `PrtgSnapshotHostedService`（Web，樣板 AiAnalysisHostedService）：每 60 秒 tick；前置條件依序：`PrtgEnabled`、連線設定齊、距上次成功快照 ≥ 目前間隔（含退避）、結構同步未執行中。任一不成立就整輪不跑並記閒置原因。
4. 快照一次：`content=sensors&columns=objid,lastvalue_raw,interval&count=50000`（9d-1 六欄 38 秒、9b 一欄 10 秒，三欄估 12～15 秒）；回傳筆數少於 treesize 時寫警告（同守門的截斷判定）；只累積目標集合內的 objid（目標集合＝當天 ok 對應裝置的 `GetValueFetchTargets(白名單)`，每小時刷新一次，當天沒有對應就用前一天）。
5. 累積器（Core，純類別可單測）：per objid 的 sum／count／min／max，整點翻頁時把上一小時寫成 `PrtgValueRow`（Quality＝`sampled`，Coverage＝count ÷ (60 ÷ 間隔)）。站台停止時把當前小時的部分樣本也寫出（coverage 如實）。
6. 退避：連續失敗（例外、HTML 頁、逾時）3 次 → 間隔加倍，上限 60 分鐘；成功即恢復設定值。每次狀態變化寫一行到執行輸出（走 NLog 與鏡像狀態的執行輸出）。
7. 夜間路徑：`PrtgDailyPipeline` 在策略為保守時**跳過觸發式取數階段**，執行輸出印「取數策略為保守，夜間不逐顆查詢歷史值，數值由快照供應」；激進時行為不變。歷史回填不受策略影響（永遠用 historicdata，併發吃設定值）。
8. 可觀測：鏡像狀態 DTO 加 `SnapshotLastAt`／`SnapshotSensors`／`SnapshotIntervalMinutes`（含退避後的實際值）／`SnapshotConsecutiveFailures`；鏡像頁籤「各類資料最新時間點」加一行「數值快照：最近 {時間}，{N} 顆，間隔 {M} 分鐘」，退避中標示。
9. `PrtgDataQuality.Sampled = "sampled"`；儲存層三處統計的「其他」桶改為明確計 `SampledCount`（校準頁 §11 之後要用）。
10. 不動 `PrtgValueFetchScope` 的語意；快照永遠是「全部 ok 對應裝置」，取數範圍只管激進的夜間逐顆與回填。

### 驗收

- 策略：預設保守；存讀往返；不合法值拒絕；四份稽核欄位都含策略（測試比對 Before／After 鍵集合）。
- 累積器：三個樣本平均／極值正確；整點翻頁寫出上一小時且 coverage＝3/12（間隔 5 分鐘）；跨小時不混算。
- 背景服務 tick：`PrtgEnabled` 關 → 不發請求；未到間隔 → 不發；HTML 回應 → 失敗計數＋1、三次後間隔加倍、成功後恢復；目標集合外的 objid 不進累積器。
- 夜間路徑：保守策略下 `PrtgDailyPipeline` 不呼叫觸發式取數且印指定字句；激進下照舊（既有測試加策略參數）。
- UI 測試：下拉存在、激進提醒文字存在、鏡像頁籤有快照行。
- 全套綠。

## 校準數值匯出的現況與快照的相容問題（核對結果）

- 校準（`CalibrationService`，Core）讀數值表只經 `EfPrtgStore` 三個查詢，**全部硬編 `Quality == ok`**：每日聚合、涵蓋摘要、量級。服務層只用 `SensorObjid`／`PeriodStart`／`AvgValue`／`Quality`；`MinValue`／`MaxValue`／`Coverage` 沒有任何讀取端。匯出檔的 `MinValue`／`MaxValue` 是「當日各小時 AvgValue 的極值」，不是列的極值欄。
- 值型基線判定：sensor 某日涵蓋＝該日 `ok` 列數 ≥ 12（常數 `ValueBaselineMinDailyOkHours`）；主機涵蓋天數取名下 sensor 最大值；10 台各 28 天可用、56 天充足。**快照寫的 `sampled` 會落進三處統計的「其他」桶**，不進 OkCount 也不進平均，保守策略下這一項永遠判不足。
- 「觸發式取數量級」判定以 `TotalCount > 0` 計天數，不受品質影響；但保守策略下沒有觸發式取數，名稱與 `OkRatio` 語意都會誤導。
- 匯出是單一 JSON（`FormatVersion`＋四個資料集），值型基線列有 `OkHours`／`UnknownCount`／`NodataCount`，沒有 Quality、Coverage、也沒有 `OtherCount`。
- 測試餵的數值列品質**一律 ok**，沒有任何一筆非 ok；`Coverage` 全檔沒設過值。
- **校準結果的下游**：程式碼沒有任何消費端，匯出檔只供人下載分析後回頭改 `CalibrationConstants`（規則門檻）與設計值型規則。值型規則（趨勢、基線偏移）在 BACKLOG，觸發條件正是「校準四項達可用」；目前 Core/Analysis 沒有任何 PRTG 數值的介面或佔位，`TrendAnalyzer` 系列全是 NetIQ 事件次數。

## 批次G：校準與快照相容

### 定案

1. **可用列（usable）**的統一定義，寫在 `EfPrtgStore` 一處供三個查詢共用：`Quality == ok`，或 `Quality == sampled 且 Coverage ≥ 50`（**暫定**：一小時內至少一半的期望樣本；保守 15 分鐘＝4 個樣本要有 2 個）。三處統計的桶改為 `OkCount`／`SampledCount`（僅計可用的 sampled）／`UnknownCount`／`NodataCount`／`OtherCount`（含 coverage 不足的 sampled、paused、untrusted）。
2. 值型基線判定：某日涵蓋＝該日**可用列數** ≥ 12（常數改名 `ValueBaselineMinDailyUsableHours`，值不變）；每日平均／極值納入可用列；`EarliestOkPeriod`／`LatestOkPeriod` 改為可用列的起訖。
3. 「觸發式取數量級」判定與文案改為「**數值取得量級**」：天數判定不變（任何品質的列）；`OkRatio` 改為 `UsableRatio`（可用列 ÷ 全部），另出 `SampledRatio`。UI 卡片標題、標籤表、規格同步。
4. 匯出 `FormatVersion` +1；值型基線列加 `SampledHours`、`MinObserved`／`MaxObserved`（當日各可用列 `MinValue`／`MaxValue` 的極值，沒有就 null；既有 `MinValue`／`MaxValue` 語意不變）；量級列加 `SampledCount`。
5. 補充說明文案（`Explanations`）把「ok 小時數」改成「可用小時數」，並在有 sampled 列時多一句「其中 N 小時為快照取樣值」。
6. 流量類 sampled 值已在批次 E 正規化到小時量，校準端不再另外處理；規格 §11 註明「流量類的 sampled 列是估算值（每次掃描量 × 3600 ÷ 掃描間隔）」。

### 驗收

- 三個查詢：ok 與 coverage ≥ 50 的 sampled 都算可用；coverage 49 的 sampled 落「其他」；unknown／nodata 各自計數不變。
- 值型基線：只有 sampled 列（coverage 100）的環境，10 台各 28 天 → 可用；同樣資料但 coverage 全 40 → 不足。
- 匯出：`FormatVersion` 變更、四個新欄位存在且值正確；既有欄位順序不變。
- 量級卡：標題與 KeyMetrics 鍵名改名，前端標籤表對應；UI 測試補字串斷言。
- 既有校準測試全綠（它們只餵 ok，語意不變）。

## 校準後的用途與後續方向（確認結果，寫進 BACKLOG）

校準匯出目前的用途是離線分析後回頭改常數，程式碼不讀它。快照讓「值型基線」在保守策略下也能累積：白名單內、有對應主機的感測器全部每小時一列，10 台主機 28 天的門檻預估最快一個月達標（現況只有觸發主機、且解析壞掉，永遠不會達標）。達標後的下一步已足夠具體，記進 BACKLOG 作為 R44 候選，觸發條件不變（校準四項達可用）：

- **值型規則第一階**（規則維護頁 prtg 平台）：per 感測器、per 小時段的 28 天基線（平均與標準差，只用可用列），三條規則——基線偏移（連續 N 小時超過 μ+kσ）、磁碟可用空間趨勢（線性外推 N 天內耗盡）、CPU／記憶體持續高檔（24 小時平均超過門檻）。門檻常數先用校準匯出的 P90／P99 定，同 §11 的作法。
- **資源守門改讀快照**：守門的即時值探針每趟批次打 PRTG，快照有了之後可以改讀最近一次快照（15 分鐘內），少一輪 PRTG 往返；快照過期才退回即時查詢。
- **校準頁加「快照涵蓋」指標**：目標集合有幾顆、最近 24 小時每顆平均 coverage，讓管理者知道基線在累積。

## 批次F：文件

- PRTG-SPEC §2：`lf_prtg_values` 的 `min_value`／`max_value` 從「無寫入邏輯」改為快照寫入；`quality` 加 `sampled`；先備欄位清單同步。
- PRTG-SPEC §3：分頁 5000／批次 500；histdata 解析改原始欄位；空白 HTML 頁；取數策略與快照（新小節 §3b）；夜間逐顆查詢依策略。
- PRTG-SPEC §6：探測步驟 4／9a 在空白頁時的輸出。
- PRTG-SPEC §11：可用列定義、`sampled` 與 coverage 門檻、數值取得量級改名、匯出新欄位與 FormatVersion、流量類估算值註記。
- PRTG-SPEC §12：守門即時來源在空白頁時的退回原因。
- WEB-SPEC §9.9f：校準卡片標題與指標改名。
- BACKLOG：上節「校準後的用途與後續方向」三項，觸發條件「校準四項達可用」。
- WEB-SPEC §9.9e：擷取參數頁籤新增策略下拉與提醒；鏡像頁籤快照行；併發說明文字。
- BACKLOG：objid 清單比對（觸發條件：分頁放大後感測器階段仍 > 5 分鐘）；messages 推送方案（觸發條件：夜間第三階段實測 > 10 分鐘）；historicdata 流量類多頻道（觸發條件：值型規則需要速率而非量）。
- CLAUDE.md：測試基線。

## 待決結果（使用者採納建議，2026-09-11）

1. 快照間隔保守 15／激進 5 分鐘、退避上限 60 分鐘：採納。
2. 分頁 5000、寫入批次 500：採納。
3. 併發範圍 1～8，說明文字建議保守 2／激進 4，預設 2：採納。
4. 保守策略下夜間完全不逐顆查詢：採納。
5. 快照只存 ok 對應裝置＋白名單型的感測器：採納。
6. 校準的 `sampled` 獨立計數並納入可用列：採納（批次 G）。

另外兩項仍缺實機資料，不阻擋實作：夜間執行輸出的階段 3 耗時（決定 messages 推送要不要提前）與「時間欄位無法解析」一行（驗證批次 A 的 P0 判斷；批次 A 不論如何都要做，原始欄位是正確做法）。

## 明確不做（本輪定案）

- 探測不改任何取數行為。
- 結構同步改單次 50000：兩次實測一次失敗（空白頁），不可靠。
- objid 清單比對、messages 推送、流量類多頻道：進 BACKLOG 附觸發條件。
- 快照存全部 42863 顆：沒有消費端的資料不存，每天百萬列會讓保留期內的表長到數千萬列。
- 併發上限超過 8：實測 8 已排隊。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 0 探測步驟 9 | general-purpose（opus；impl-low 定義本會話尚未登錄，紀律以提示內嵌） | 已實作 | Claude 親驗：全套 3766 綠（+5）、五條新測試、spec 段無敘事字眼、選樣優先序與不重複分組核對過 | 執行端三條沒把握：9c 平均延遲以 double 判 ≤0 但印 F0（真機延遲數十 ms 無觀感問題）；既有 BuildPagingStub 情境 9c 實際跑 1 顆而非略過（既有斷言不受影響）；測試 5「回 500」讀成 HTTP 500（與規格原意一致） |
| 0b 探測補強 | impl-low（opus，low） | 已實作 | Claude 親驗：全套 3778 綠（+12）、messages 時間判定與 9d 四子量測輸出核對 | 執行端四條沒把握：histdata 末列印出前把換行摺成空白（接受）；datetime 解析失敗時原本落到「漏列」警告，Claude 改成獨立的「無法判定」行；type 名稱完全比對（實機字串已核對一致）；9d-3 Ping 不足時的前半分法由執行端定 |
