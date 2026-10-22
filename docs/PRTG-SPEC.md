# PRTG 整合規格

PRTG 是環境內既有的監控系統，提供連續的數值時序與狀態變更。LogForesight 把它鏡像到本地資料庫，
作為 NetIQ 事件之外的第二種訊號來源。本文件只描述現況；決策來龍去脈見 `docs/archive/README.md`。

## 1. 定位與邊界

- **主檔以 NetIQ 為準**：主機身分只來自主機主檔（`lf_blobs` 的 `hosts`）。PRTG device 以 IP（退路為名稱，§4）對應過去；
  對不到就跳過，不猜測、不自動建立主機。
- **兩個來源各自取數、各自失敗隔離**。沒有「PRTG＋NetIQ」的合併規則平台；合成發生在主機日結論層：
  PRTG finding 進當日 `lf_top_issues`、單向上調日風險、以獨立區塊進 AI prompt（§9）。
- **數值可信＝三條件**（§9b）：該日對應有效、該小時是可用列、單位可判。不再加其他閘門。
- **PRTG 不觸發即時信**：即時告警信排除 PRTG 問題；日／週摘要含 PRTG 段（郵件設定見 docs/WEB-SPEC.md §9.9b）。
- **模組可完整停用**：`PrtgEnabled` 預設關閉，關閉時夜間擷取、快照與回填完全短路（環境探測例外，§6）。每日管線、手動同步、回填與快照各用一個
  `PrtgOperationScope`，在 HTTP 請求前後與安全點重讀設定與監看範圍（人工對應／IP 排除與 `prtg_scope_revision` 同交易提交），變更後不開新請求、已提交資料保留。

## 2. 資料表（`lf_prtg_*`）

遵守 `docs/DB-SPEC.md` 的命名與可移植規範；建表走 `EnsureCreated`（新 DB）＋ `SchemaUpgrader` 冪等 DDL（既有 DB）。

| 表 | 內容 | 鍵 | 保留期 |
|---|---|---|---|
| `lf_prtg_devices` | device 結構鏡像（全站） | `objid` | 不依保留期清；同步成功後刪本趟沒出現的裝置（§3c） |
| `lf_prtg_sensors` | sensor 結構鏡像（只含取數範圍內裝置，§3c） | `objid` | 不依保留期清；同步成功後刪本趟沒刷新的列（§3c） |
| `lf_prtg_state_changes` | 狀態變更與訊息 | 自增 `id`，去重 `(sensor_objid, changed_at)` | `PrtgRetentionDays` |
| `lf_prtg_values` | hourly 數值 | 自增 `id`，唯一 `(sensor_objid, period_start)` | `PrtgRetentionDays` |
| `lf_prtg_host_map` | 主機對應（按日） | `(map_date, device_objid)` | `PrtgRetentionDays` |
| `lf_prtg_manual_map` | 人工主機對應 | `device_objid` | 不清 |
| `lf_prtg_ip_excludes` | IP 排除清單（§4b） | `ip` | 不清 |

- 清理依 `created_at`（不是事件時間）並走 `BatchedPrune`：重跑舊日期時依事件時間清會讓剛補的資料立刻消失。
- `lf_prtg_sensors.category`／`category_source`：結構同步後依 type 對照自動分類（`category_source=auto`，值見 `PrtgSensorCategories`：
  traffic／disk／cpu／memory／availability／hardware）。對照＝設定 `PrtgSensorTypeCategoryOverrides`（§7）優先，再查內建
  `PrtgSensorTypeCategoryMap`（availability＝`Ping`、`Port`、`HTTP`、`SNTP`、`DNS (DEPRECATED)`、`FTP`、`RDP (Remote Desktop)`、`Cisco IP SLA`；
  hardware＝`SNMP Cisco System Health`；其餘四類為主要 SNMP／WMI type）。`SNMP Linux Load Average` 刻意不歸 cpu（不是百分比）。
  自動分類只動 category 為 null 或來源為 auto 的列；來源不是 auto 的非 null 分類永不動，結構同步本身不寫這兩欄。
  消費端：規則挑選（§9）、跨來源佐證（§9）、未回報提示（§9）、數值評估（§9b）、資源守門（§12）。

### 資料品質旗標（`PrtgDataQuality`）

| 值 | 意義 | 會寫入 |
|---|---|---|
| `ok` | `historicdata` 整小時真平均 | 是（激進策略夜間取數、回填） |
| `sampled` | 快照（§3b）自行平均的小時值；`min_value`／`max_value` 為樣本極值，`coverage`＝樣本數 ÷ 期望樣本數 × 100 | 是 |
| `unknown` | PRTG 回報 unknown，或 coverage 為 0 | 是（數值欄 null） |
| `nodata` | 該時段沒有資料 | 是（數值欄 null） |

`paused`（暫停的 sensor 整段不抓）與 `untrusted`（尚無判定來源）是保留常數，不會寫入。

- 同一（sensor, 小時）：`ok` 覆蓋 `sampled`；`sampled` 對既有 `sampled` 以 coverage 加權合併，既有為 `ok` 時不動。快照批次 ID 與 `lf_prtg_sampled_batches` 同交易提交，重試不重複合併。
- **可用列**＝`ok`，或 `sampled` 且 `coverage ≥ 75`（`PrtgValueUsability.SampledMinCoverage`）。校準（§11）、數值評估（§9b）只認可用列。
- `lf_prtg_state_changes.quality` 一律寫 `ok`（狀態變更沒有品質判定依據）。

## 3. 擷取

每日擷取是夜間批次（`AnalysisOrchestrator.RunAsync`）內與本機分析、NetIQ 並行的第三條路徑；PRTG 失敗不讓整趟分析失敗，只有取消訊號穿透。

**順序**：裝置結構同步 → 主機對應（§4）與取數範圍（§3c）→ 範圍內感測器結構與狀態變更同步 → 狀態規則評估（§9）→
數值就緒度與數值規則（§9a、§9b）→ 跨日標註、抑制 → 發佈 finding 登錄簿並補追加已落地主機日 → 觸發式數值取數（§3a）。

**排程補評估**：逐日統計（`PrtgDayStat.NeedsReevaluation`）記下「該日未完整評估並發佈，或結構同步失敗」；排程觸發且範圍為全部主機時，`PrtgReevaluationPlanner.Select` 從最近 7 天的執行紀錄挑出這些日子（同一日以最晚一趟為準；當機而沒留下逐日統計的排程執行，以其處理的資料日入選），與昨天聯集一起評估。手動立即執行不自動併入。已知限制：同一日之後若有「指定主機更新」的成功紀錄，會蓋過先前全範圍的失敗。

**回望多日**（「立即執行」回望 N 天且範圍為全部主機；其餘只處理昨天）：結構同步與狀態變更只做一次；對應只重算最新一天，較舊日期沿用該日或之前最近一日
（往回 31 天，`PrtgTriggeredValueFetcher.HostMapLookbackDays`）。規則評估兩段：先逐日評估並映射，再逐日跨日標註、抑制、發佈、補追加。
`silent` 與數值規則只評估最新一天。每日結果存 `BatchRun.PrtgDays`。

**AI 完整性保證**：AI 判讀以 `SchedulerRunState.FindingsWaitCutoff` 擋住本趟 PRTG 處理範圍內的待補，`prtg-findings-ready` 後才放行；
寫回前比對 PRTG 證據指紋（`TryAttachAiResult`），不符即拒絕並維持待補；事後追加 finding 若指紋改變且風險非低，重新標記 AI 待補。
取消或逾窗未發佈的日子補發空集合，隔晚補評估重附 finding 後再判一次。週檢（`WeeklyCheckupService`）是取數執行內唯一同步呼叫 AI 的地方，
其餘判讀都由 AI 排程處理，順序與閘門見 docs/WEB-SPEC.md §9.10。

| 階段 | 端點 | 寫入 |
|---|---|---|
| 1. device 結構 | `table.json?content=devices`（全站） | `UpsertDevices` → 刪本趟沒出現的裝置 → 主機對應與取數範圍 |
| 2. sensor 結構 | `content=sensors&id=<裝置>`（範圍內逐台；超過 500 台改全站分頁再濾） | `UpsertSensors`（不覆蓋分類欄）→ 刪本趟沒刷新的列 |
| 3. 狀態變更 | `content=messages&id=<裝置>&filter_drel=…`（範圍內逐台） | `AppendStateChanges`（一個區間一次取完、依日分桶、去重） |
| 4. hourly 數值 | `historicdata.json?avg=3600` | `UpsertValues`；觸發式（§3a），只在激進策略執行（§3b） |

各階段獨立 try/catch，任一失敗其餘照跑。共同規則：
- **分頁只有一份實作**（`PrtgTablePager`，`count` 5000、寫入批次 500）：空頁／未滿頁／本頁沒有新列任一成立即停（PRTG 在 `start` 超出範圍時夾到末頁）；
  頁數上限 `max(⌈treesize ÷ 頁大小⌉ + 2, 400)`，未收斂擲 `PrtgPagingNotConvergedException`。一律帶 `sortby=objid`。
- **去重鍵是該 content 真正的唯一鍵**：預設 objid；messages 的 objid 是發出訊息的 sensor，列鍵為「objid＋時間」。
- **狀態變更逐裝置查**（`PrtgFetchService.FetchStateChangesRangeAsync`）：區間＝最早目標日前一天到今天；不對 `id=0` 查（實機 treesize 封頂 100 萬、
  `count=5` 也逾時）。要查哪些物件集中在 `StateChangeQueryObjects`。`filter_drel` 取涵蓋區間起點的最小級距（7days／30days／12months），
  用戶端區間過濾仍保留。每頁時間全部可解析、非遞增且早於「區間起點前一天」時提早停止。
- **PRTG 對大回應回 HTTP 200 空白 HTML**：`PrtgClient.GetJsonAsync` 遇到以 `<` 開頭的內容擲 `PrtgClientException`（含開頭 80 字）。
- 每個請求有 `PrtgTimeoutSeconds` 逾時，迴圈有上限並接受取消；記憶體有界（每 500 筆寫入一次、每批新 `DbContext`）。單一 sensor 失敗只影響它自己。
- **`historicdata` 解析**（`PrtgFetchService.ParseHistoricData`）：值取第一個 `value_raw`（主要頻道），缺失退回 `value_`／`value`；
  coverage 取 `coverage_raw ÷ 100`。**時間以 `datetime_raw`（UTC 的時段結束時刻）為準**；缺 `datetime_raw` 時改以顯示字串依服務主機地區設定推得，
  筆數另印；顯示字串與 `datetime_raw` 換算差 30 分鐘以上時警告「PRTG 帳號時區與服務主機不同」並計數（數值仍依 raw 落在正確小時）。
  狀態變更同樣以 `datetime_raw` 為準，缺值時的字串退路與丟列計數印在執行輸出。

### 3a. 觸發式數值取數

激進策略下夜間只對觸發主機取數，三重過濾：
1. 觸發主機＝當日 `risk_level` 高或中 ∪ PRTG 規則命中；
2. 經 `lf_prtg_host_map` 反查 device（該日或之前最近一日、往回 31 天），**只取 `ok`**；
3. device 上 type 命中 `PrtgSensorTypeWhitelist` 且未暫停的 sensor。

`PrtgValueFetchScope` 改變第 1 步的候選主機（判定收斂在 `PrtgValueFetchScope.SelectHosts`，夜間與回填共用）：

| 模式 | 候選主機 |
|---|---|
| `triggered`（預設） | 當日高／中風險 ∪ PRTG 規則命中 |
| `all-mapped` | 全部有 `ok` 對應的主機；激進且啟用時要求白名單非空（存檔擋、執行層 `EffectiveScope` 再退回 `triggered`） |
| `triggered-plus-list` | 觸發主機 ∪ `PrtgValueFetchExtraHosts`（名稱，執行時解析，對不到的列出並略過） |

- 不合法值退回 `triggered`；執行輸出與估算端點（`GET settings/prtg-fetch-scope/estimate`）印實際生效範圍。
- **與分析並行、採輪詢**：定期查已落地的分析結果取數，分析結束後再掃一次；`all-mapped` 首輪全部取完，該日沒有 `ok` 主機時出聲。
- 一律 hourly 聚合（`avg=3600`），絕不拉 raw。併發上限 `PrtgFetchConcurrency`（1–8，預設 2），夜間與回填共用；激進或回填時逾時建議 ≥120 秒。

### 3b. 取數策略與數值快照

| 項目 | `conservative`（預設） | `aggressive` |
|---|---|---|
| 快照間隔 | 15 分鐘 | 5 分鐘 |
| 夜間逐顆 `historicdata`（§3a） | 不執行，數值全由快照供應 | 執行，給觸發主機真平均 |

- 常數與判定在 `PrtgFetchStrategy`；不合法值退回保守。回填不受策略影響（永遠 `historicdata`）。切換策略不改既有列。

**快照服務**（`PrtgSnapshotHostedService`）：
- 每 60 秒檢查，依策略與退避間隔採樣；結構同步、回填與夜間 PRTG 階段執行中最多讓出一個採樣間隔。**先定目標再查**（`PrtgSnapshotTargetResolver`）：最近合格主機對應、有效主機、IP 排除與白名單決定精確 sensor 集合；目標為空不發請求。
- **有界查詢**：依 objid 排序，每批 100 顆（`filter_objid`，`count=batch+1` 為哨兵，回應不是恰好那一組就整批不收，`PrtgSnapshotCapacityResponseValidator`），
  最多 3 批在途；一輪必須在一個採樣間隔內完成，否則已取得批次保留、該輪計失敗並說明涵蓋率偏低。單批失敗只記數，全部失敗才進退避。
- **退避**：連續失敗每滿三次間隔加倍（上限 60 分鐘），成功即恢復；從最近一次嘗試起算。
- **待寫與重啟恢復**（`PrtgSnapshotJournal`，`DataRoot/pending/prtg-snapshot`）：每批追加 journal 段與固定 ID 待寫批次，SQL 提交後才確認。
  上限 20 萬列／64 MiB；本機待寫批次超過 30 日（`ReplayDays`）不再重播；不足或損壞時保留原資料並停止新採集。DB 批次鍵至少保留 32 日。
- 每輪通過前置條件後補抓「範圍內但鏡像沒有感測器」的裝置（每輪最多 50 台，§3c），守門覆寫清單中鏡像找不到的 sensor 所在裝置排最前。

## 3c. 取數範圍

感測器鏡像、狀態變更、快照只處理取數範圍內的裝置；裝置鏡像維持全站（主機對應要用全部 IP）。

**範圍**＝`PrtgScopeDevices.Compute`（唯一出口）的聯集：
1. 最近一次主機對應（回看 30 天）中 `ok` 的裝置；
2. 與主機有關的 `conflict`：一 IP 多主機，或同 IP 多裝置而該 IP 屬於啟用中主機；
3. 人工對應表的全部裝置；
4. 資源守門的裝置（§12）：Sentinel 與 PRTG 位址比對到的裝置、覆寫清單 sensor 所在裝置、corehealth sensor 所在裝置——不論守門是否啟用。

範圍提供者是 `FetchDayAsync` 的必填參數，在裝置階段之後、感測器階段之前呼叫一次：夜間一律重算最新日對應；手動同步只在裝置階段成功時重算；回填用既有對應。

**過期列清除**（判準：本趟沒被刷新到的列）：
- 裝置：裝置階段無例外、已收斂、寫入數 > 0 時，刪 `synced_at` 早於本趟起點的裝置（在重算對應之前）；本趟沒出現的超過鏡像一半時一台都不刪並警告。
- 感測器：範圍非空、階段無例外、寫入數 > 0 時刪未刷新的列。範圍內沒有任何對應成功的裝置時不清；本趟「查詢成功但回 0 顆」的裝置給一趟寬限（36 小時內刷新過的列先留）。
- 範圍外數值與狀態變更：`PrtgScopePurge.RunAfterStructureSync` 只在結構同步與對應全部成功、`CheckScope`（範圍非 null、非部分處理、監看裝置非空、裝置鏡像本趟更新）
  與 `CheckShrink`（已有基準且減少量不超過 `max(基準 × 30%, 50 台)`）都通過時才清。首次無基準等情況不自動清，由維護頁「監看範圍外資料」預覽後人工確認。
  資源守門所需的裝置與 sensor 不因數值範圍變小而被清。

範圍 ≤ 500 台（`PerDeviceSensorFetchLimit`）逐台查感測器，超過時全站分頁再濾。白天新進範圍的裝置由快照服務的範圍補抓帶進鏡像（§3b）。
「最後結構同步時間」取裝置表（`GetLatestStructureSyncedAt`）。

## 4. 主機對應

結構同步後、規則評估與數值取數前執行，逐日寫入 `lf_prtg_host_map`（同日重跑覆寫當日列）。

| 情況 | 結果 |
|---|---|
| IP 命中恰好一台活躍主機 | `ok` |
| IP 查無主機 | 走名稱退路（下節）；仍對不到為 `unmatched` |
| 一個 IP 由多台主機共用 | `conflict`，對到 HostId 最小者，Note 列其他候選 |
| 一個 IP 有多個 device | `conflict`，不填主機 |
| device 沒有 IP | 走名稱退路；仍對不到不產生列，計入「略過（無 IP）」 |
| device 有人工對應（§4a） | `ok`，Note 標人工指定 |
| device 的 IP 在排除清單（§4b） | 不產生列，計入「略過（已排除）」；人工對應優先於排除 |
| 同 IP 已有另一台 device 被人工指定 | 不產生列，計入「略過（同 IP 已有人工指定 device）」 |

停用與已合併的主機不參與。**對應作業只讀主機主檔，絕不寫回。**

### 位址怎麼比對

1. **純語法層**（`PrtgAddress.Normalize`，對外名稱 `PrtgHostMapper.NormalizeIp`）：去空白、scheme、路徑、query、port（裸 IPv6 不誤拆）、IPv4 前導零，結果須為合法 IP。
2. **解析層**（`PrtgAddressResolver`）：純語法層回 null 時，只有通過 `PrtgAddress.IsDnsCandidate` 的名稱才送 DNS（只查 IPv4、1 秒逾時可取消，
   同實例同名只解析一次、失敗也快取）。整串沒有字母、或第一段全數字且每段只含數字或佔位字 x（`10.2xx.x.x`）視為打壞的 IPv4，不送 DNS；IDN 不送。

device 分組鍵用解析層結果。IP 排除清單的儲存鍵是純語法值，device 側比對用解析後 IP。`lf_prtg_devices.Ip` 保存 PRTG 原始字串。

### 名稱退路（hostname）

IP 查無主機或 device 沒有 IP 時，以 `PrtgHostnameCandidates.Extract` 的候選名稱比對啟用中主機的名稱（`ShortName`，去網域、不分大小寫）：
候選＝裝置名稱括號內字串（含「去掉開頭單一字母＋`-`」變體）＋ `host` 欄非 IP 的值。

| 情況 | 結果 |
|---|---|
| 恰好一台主機、一台 device | `ok`，Note `hostname` |
| 多台主機 | `conflict`，衝突清單標籤 `hostname-multi` |
| 同一主機名有多台 device | `conflict`，Note「hostname 比對：同一主機名有 N 台 PRTG device」 |
| 該主機已有 IP 或人工對應的裝置 | `conflict`，標籤 `hostname-host-taken` |

比對是純字串，不做 DNS。執行摘要另列 `hostname=N`。

### 什麼時候會重算

夜間 PRTG 路徑重算最新一天（§3c）；手動同步（§5a）、人工對應或 IP 排除變更、主機主檔新增（有 IP）／IP 變更／停用／啟用／合併／解除合併
都重算今天（`PrtgHostMapRefresher`，PRTG 未啟用時零成本返回）。

### 4a. 人工對應（`lf_prtg_manual_map`）

管理者在維護頁的未對應／衝突清單把 device 指派給主機；長期有效、不按日，每日自動對應優先採用它。
有人工對應的 device 跳出 IP 分組；指定的主機已停用或不存在時回到自動判定並警告；刪除即恢復自動判定。新增、刪除、批次對應都寫稽核並帶 `expectedScopeRevision`。

### 4b. IP 排除（`lf_prtg_ip_excludes`）

排除粒度是 IP：該 IP 底下所有 device 都不產生對應列，因此不取數、不評規則。人工對應優先於排除；同 IP 有 device 被人工指定時其餘 device 略過。
三種略過原因在執行摘要分開列出（沒有也顯示 0）。新增與移除寫稽核（`prtg_ip_exclude_set`／`prtg_ip_exclude_delete`），寫入後同步重算當日對應。
主機詳情在主機 IP 被排除時明寫「此主機 IP 已排除 PRTG 對應」。

## 5. 歷史回填

手動離峰作業（PRTG 維護 → 歷史回填；排程作業頁看進度與停止），不掛排程，回填 `PrtgBackfillDays` 天（預設 30）的數值與狀態變更，不重跑結構同步。

- **先預覽再確認**：預覽與正式 runner 共用逐日目標 resolver（觸發範圍、各日高／中風險、往回 31 天的有效對應、白名單），列逐日主機與 sensor 數、
  historicdata 次數與配額時間下界。預覽綁定設定、範圍、日期與目標 fingerprint，五分鐘有效；start 須帶當次 PreviewId 與 Confirmed，漂移即拒絕。
- 狀態變更整趟只取一次（範圍在開始時以既有對應算一次），之後逐日做數值；單日失敗不中止。寫入有自然鍵去重，中斷後重跑不重複。
- 回填不做主機對應；某日沒有對應取該回填日往回 31 天內最近一日，仍無則略過該日；全部略過時整趟為失敗。
- 啟動閘門：未啟用、連線不齊、探測執行中、鏡像沒有感測器、取數執行中、同步執行中、近（回填天數＋31）天沒有任何主機對應。
- 指定主機回填（維護頁「指定主機補值」）只補數值、不回填狀態、不補派歷史 finding。
- 可停止（`POST prtg-backfill/cancel`，寫稽核）；結果寫「已停止：完成 X／N 天」。回填寫入 `ok` 列，覆蓋同一小時的 `sampled`。
- **完成水位**（blob `prtg_backfill_watermark`，`PrtgBackfillWatermarkStore`）：全量回填某日每顆目標 sensor 都取到才記完成，鍵為設定版本、
  監看範圍版本與該日目標指紋；任一不同或 7 天未更新即不採信。預覽列出先前已完成可略過的天數、剩餘請求數與逐日標示；start 預設
  `resume=true` 只跑剩餘日，全部完成時拒絕空跑；「全部重做」（`resume=false`）清水位全跑。狀態變更區間仍整段取一次（冪等）；
  指定主機補值與接續回填不讀不寫水位。
- 建議回填避開排程窗口（預覽的配額時間下界可供估算）：夜間取數遇到回填執行中最多等 30 分鐘，之後與它同時查詢 PRTG（§5a）。

## 5a. 同步結構與對應（手動）

當場補齊鏡像並重算今天的對應：① 裝置結構；② 今天的主機對應與取數範圍；③ 範圍內感測器結構與狀態變更（不取數值）。

- 背景工作，狀態走 `GET prtg-structure-sync/status`；`POST prtg-structure-sync/cancel` 當場中斷進行中的那一頁，沒有執行中時回 409。
- 上次結果持久化在 blob `prtg_sync_status`；從未執行過時為 null（畫面「尚未同步」）。夜間結構同步全部成功時也寫（來源 `nightly`）。
- 沒拿到任何裝置就不做對應。前置條件：已啟用、有位址、認證齊。
- **與取數互斥**：取數執行中拒絕同步與回填；回填執行中拒絕同步。反方向由 Core 的 `IPrtgManualWorkGate`（`Running`＝None／StructureSync／Backfill，
  `WaitUntilIdleAsync` 回列舉）表達，Web 以 `PrtgManualWorkGate` 合成同步與回填兩個狀態（接續回填不算）：同步或全量／指定主機回填執行中時，
  取數開始的 PRTG 日路徑先等（phase `prtg-wait-sync`，執行輸出寫明等的是哪一種與開始時刻）。等到的是**成功的同步**才跳過自己的結構同步
  （同步等待上限 90 分鐘）；等到回填結束照常同步；回填等到 30 分鐘上限仍在執行時與它同時進行並寫里程碑警告。
  立即執行 modal 在回填執行中多一行提示（docs/WEB-SPEC.md §9.10），不另開確認框。
- 入口：維護頁「鏡像狀態」頁籤與排程作業頁 PRTG 卡。啟用 `PrtgEnabled` 時不自動啟動。

## 6. 環境探測（probe）

維護頁「環境探測」頁籤提供兩種共用狀態與停止鈕的探測，結果只存在記憶體，可複製或下載（輸出可能含主機名、IP，分享前遮蔽）。
探測不檢查 `PrtgEnabled`。

**完整環境探測**（唯讀、背景執行，標頭含 InformationalVersion）：版本；device／sensor 總數；sensor type 分布與內建分類；群組樹；
IP 覆蓋（IPv4／DNS 名稱／無法判定，截斷警告）；分頁語意一行結論（`start` 與 `sortby`）；數值單位判定（四類內建對照各挑一顆）；
逐物件查詢驗證（9d-5：`id=<裝置>` 的 sensors 是否只回該裝置、messages 是否含下層感測器訊息）。遇空白 HTML 印「無法量測」並繼續。

**站台對照**（環境探測成功後接著跑，唯讀，`PrtgProbeSiteCheck`；自身出錯不改探測成敗）：
- [S1] 取數範圍試算：範圍台數與四項組成、鏡像中範圍外幾顆（不代表必然清除，§3c）。
- [S2] 範圍內裝置逐台查詢實測（最多 3 台、6 次 `table.json`），估感測器與狀態變更階段秒數。
- [S3] 資源守門目標偵測（`PrtgResourceGuardTargets.Detect`，直接查 PRTG）：以位址命中、改以 Core Health 找到或找不到；命中裝置逐台對照取數範圍。

**小範圍資料流驗證**：從最近對應挑一台主機的一顆白名單內未暫停 sensor，對昨天呼叫正式 `PrtgFetchService.FetchValuesForSensorsAsync` 並讀回；
本趟確實寫入且讀回至少一筆 `ok` 才通過。取數、同步或回填執行中拒絕啟動。

**數值資料就緒度卡**（`GET api/prtg/value-readiness`，Maintain 且非案件授與）：母體＝取數範圍內已對應、分類為 cpu／memory／disk／availability 的 sensor，
以 `PrtgValueReadiness.Compute`（28 日窗、最少 28 有效日）依 §9b 的原因分組計數；與每日管線執行輸出的就緒度行同一計算。

## 6a. 認證方式

由已儲存設定建立 client 的唯一入口是 `PrtgClientFactory.Create(settings)`；唯一例外是設定頁「測試連線」（用表單當下的值）。

| 模式 | 請求參數 | 適用 |
|---|---|---|
| `token`（預設） | `apitoken=<token>` | 新版 PRTG，可限唯讀、可單獨撤銷 |
| `password` | `username=&passhash=` | 舊版；系統保存密碼，第一次請求前呼叫 `getpasshash.htm` 換取 passhash（同實例只換一次） |
| `passhash` | `username=&passhash=` | 使用者自行提供 passhash；系統不保存密碼、不呼叫 `getpasshash.htm` |

- passhash 比照密碼加密、write-only。**憑證錯誤會黏住**（防帳號鎖定）：換取 passhash 遇 401/403／HTML 登入頁黏住；資料請求只在帳號類模式的 401 黏住。
- 憑證寫入依當前模式隔離。例外訊息不含 token、密碼、passhash（含 URL 編碼形式）。

## 7. 設定

全部存於 `SystemSettings`（DB）。

| 設定 | 預設 | 說明 |
|---|---|---|
| `PrtgEnabled` | false | 模組總開關 |
| `PrtgUrl` | — | core server 位址（http/https），啟用時必填 |
| `PrtgAuthMode` | `token` | `token`／`password`／`passhash`（§6a） |
| `PrtgApiTokenEnc`／`PrtgPasswordEnc`／`PrtgPasshashEnc` | — | 密文（AES-256-CBC），write-only |
| `PrtgUsername` | — | password 與 passhash 共用 |
| `PrtgIgnoreSslErrors` | false | 忽略憑證錯誤，每次建立連線記 WARN |
| `PrtgTimeoutSeconds`／`PrtgFetchConcurrency`／`PrtgBackfillDays` | 60／2／30 | 單次逾時（5–600）、併發上限（1–8）、回填天數（1–365） |
| `PrtgRetentionDays` | 180 | 鏡像保留天數（規則見 DB-SPEC 保留策略） |
| `PrtgSensorTypeWhitelist` | 7 種 type | 要擷取數值的 type（留空＝不限）。出廠：`Ping`、`SNMP Disk Free`、`SNMP CPU Load`、`SNMP Memory` 與網卡、多碟可用空間等分析型 type；Traffic 不列入。只管數值取數與快照，不影響規則母體 |
| `PrtgSensorTypeCategoryOverrides` | 空 | 一行一筆 `type=分類[:語意]`（§2、§9b）。語意可用 `cpu-percent-used`、`memory-percent-free`、`disk-percent-free`、`ping-ms`。存檔以 `PrtgSensorTypeCategoryMap.ParseOverrides` 驗證；會連帶改變資源守門偵測 |
| `PrtgValueFetchScope`／`PrtgValueFetchExtraHosts` | `triggered`／空 | 激進策略夜間取數範圍與額外主機名稱（§3a） |
| `PrtgFetchStrategy` | `conservative` | §3b |
| `PrtgResourceGuardEnabled` | false | 資源守門總開關（§12） |
| `PrtgResourceGuardSensorObjids` | 空 | 受監看 sensor 清單；空＝夜間自動偵測並填入（§12） |
| `PrtgResourceGuardCpuPercent`／`MemoryFreePercent` | 85／10 | CPU 達此值、可用記憶體低於此值算緊張 |
| `PrtgResourceGuardCheckSeconds`／`PauseMinutes`／`Strikes`／`MaxPauseMinutes` | 60／5／2／120 | 檢查間隔、緊張後重檢等待、連續超標次數、單趟累計暫停上限 |

- 密文欄位留空＝沿用既有，清除需另勾；啟用時依模式驗證憑證「新存或既有」皆算有。
- 既有站台已儲存的白名單不會被出廠值改寫；需要 Ping 數值時自行加入。

### 操作介面

| 頁面 | 內容 |
|---|---|
| PRTG 維護頁 `/admin/prtg`（Maintain） | 頁首「啟用準備」六項與全部確認後的「最近一次排程驗收」（§13）。頁籤：連線／擷取參數（啟用開關與取數範圍；**監看範圍（可選縮圈）**是參數表單之後的收合卡，預設收合、記住展開）／鏡像狀態（同步結構與對應、對應清單、範圍外資料）／環境探測（含就緒度卡、校準匯出 §11、資料搬運 §10）／歷史回填（全量含完成水位 §5、指定主機補值）／使用效果（末尾含**通知處理狀態**）。卡片細節見 docs/WEB-SPEC.md §9.9e |
| 設定頁「資源守門」頁籤 | 八個 `PrtgResourceGuard*` 欄位、「預覽／自動偵測並填入」、自動偵測日期註記（§12） |
| 排程作業頁 | 模組狀態、同步／回填進度與停止鈕、每日擷取進度軌；沒有啟動鈕 |

連線與參數的存檔走 `PUT settings/prtg`：欄位一律可空且「有送才更新」，跨欄位檢查用 effective 值（請求值 ?? 已存值），
一般設定與 PRTG 設定 PUT 都帶 `expectedRevision`，過期回 409。

## 8. API 端點

全部在 `/api/admin/settings` 之下，需 Maintain：

| 端點 | 用途 |
|---|---|
| `POST prtg-test`、`PUT prtg` | 測試連線、PRTG 專屬設定 |
| `GET prtg-mirror` | 鏡像狀態、對應摘要、快照狀態（最近成功、生效間隔、連續失敗、退避、暫停原因） |
| `POST prtg-probe/start`、`POST prtg-probe/data-flow/start`、`GET prtg-probe/status`、`POST prtg-probe/cancel` | 環境探測與小範圍資料流驗證 |
| `GET prtg-backfill/preview`、`POST prtg-backfill/start`、`POST prtg-backfill/cancel`、`GET prtg-backfill/status` | 歷史回填（§5） |
| `POST prtg-structure-sync/start`、`POST prtg-structure-sync/cancel`、`GET prtg-structure-sync/status` | 同步結構與對應（§5a） |
| `GET／PUT／DELETE prtg-manual-map`、`prtg-ip-excludes` | 人工對應（§4a）、IP 排除（§4b） |
| `GET prtg-host-map?status=&page=&pageSize=` | 對應清單分頁；衝突列帶 `conflictKind`（`multi-device`／`multi-host`／`hostname-multi`／`hostname-host-taken`） |
| `GET prtg-resource-guard/preview[?forceAuto=]` | 守門 sensor 與當下值；`source`＝`override`／`live`／`mirror-fallback`／`auto`；帶 `autoDetectedAt` |
| `GET prtg-fetch-scope/estimate?scope=` | 取數範圍規模估算 |

同一前綴下另有：`POST prtg-backfill/selected/preview`、`POST prtg-backfill/selected/start`（指定主機補值，§5）、`POST prtg-scope-purge/confirm`、`GET prtg-scope-purge/jobs/{jobId}`（範圍外資料清除，§3c）、
`GET prtg-export`、`prtg-import-transfers`（§10）。其他前綴（皆 Maintain）：`api/prtg/value-readiness`（§6）、`api/prtg/effectiveness`（§11）、`api/prtg/monitoring`（監看範圍）、
`api/prtg/operations`（補追加重播）、`api/prtg/risk-review`（待重評清單）、`api/prtg-snapshot-diagnostics`（快照診斷）；
主機詳情 `GET api/host-detail/{hostId}/prtg`（device、sensor 與數值資料狀態，§9b；可見性在 controller 檢查，案件授與回 403）。
連線失敗不是例外，回 `Success=false` 讓畫面就地顯示。

## 9. 狀態規則

正式每日路徑以 `PrtgRuleEvaluator` 讀 `lf_prtg_state_changes`（目標日前後一天）與感測器鏡像評估 `down`、`flapping`、`warning`、`silent`。
規則存在規則維護頁的 `prtg` 平台，**規則庫是執行期唯一真相**：門檻、分類、嚴重度、重大旗標、描述與知識庫取自命中的規則（`PrtgRuleCatalog` 只提供 seed 預設）。

### 內建規則（seed v11）

| Id | 代碼 | 適用分類 | 分類／嚴重度／重大 | 預設門檻 |
|---|---|---|---|---|
| `builtin-prtg-down` | down | 全部 | Service／High／否 | 60 分 |
| `builtin-prtg-down-availability` | down | availability | Service／High／是 | 30 分 |
| `builtin-prtg-down-hardware` | down | hardware | Hardware／High／否 | 60 分 |
| `builtin-prtg-flapping` | flapping | 全部 | Service／Medium／否 | 5 次往返 |
| `builtin-prtg-warning` | warning | 全部 | Resource／Medium／否 | 240 分 |
| `builtin-prtg-warning-disk` | warning | disk | Storage／High／否 | 240 分 |
| `builtin-prtg-warning-hardware` | warning | hardware | Hardware／High／否 | 120 分 |
| `builtin-prtg-silent` | silent | （不可指定） | Service／Medium／否 | 0 |
| `builtin-prtg-disk-free-trend` 等四條 | 數值趨勢 | 依代碼固定 | 見 §9a | 專用門檻，預設停用 |

- 語意：`down`＝Down 自進入時間起持續達門檻（當日恢復仍保留）；`flapping`＝當日相鄰 Down→Up 次數達門檻；`warning`＝Warning 累計達門檻；
  `silent`＝device 的 availability sensor 全部 Up、其餘未暫停 sensor 全部 Unknown 或無狀態。
- 30 分與 120 分為暫定值，待校準（§11）。描述不嵌門檻數字。
- **母體**＝鏡像中未暫停的 sensor（即取數範圍內），不受數值白名單限制（白名單預設不含 Traffic 等，拿來過濾會讓 Ping Down 永遠命中不了）。
- **依分類挑規則**：分類相符的規則優先，沒有才用不限分類的；候選多條取 Id 字典序最小並警告一次。silent 不可指定分類（`RuleValidator` 把關）。
- `Down (Acknowledged)` 仍算 Down，finding 標「已於 PRTG 確認」，不降低風險或重大旗標；`Down (Partial)` 不算確認（`PrtgSensorStatuses.IsAcknowledged`）。
- 狀態字串是 PRTG 原值，一律前綴比對且不分大小寫（`PrtgSensorStatuses`）；`prev_status` 恆為 null，持續時間只靠同 sensor 依時間排序的相鄰列推導。
- 規則庫沒有啟用中的 PRTG 規則時輸出「規則庫尚無啟用中的 PRTG 規則」並跳過評估。新增或修改內建規則必須遞增 `KnownIssueSeed.Version`；
  seed 比對（`RuleImportPlanner.ContentEqualExceptEnabled`）涵蓋全部內容欄位，由反射測試守住。

### 跨日標註與升級

`PrtgCrossDay` 以 EventKey 為單位（不限主機）看當日之前 14 天加當日；數值趨勢 finding 不參與（趨勢本身已是跨日判定）。
歷史＝`lf_top_issues` 命中日（`GetPrtgFindingHitDates`，每批 500 個）∪ 本趟較舊日期；本趟評估過的日期只認本趟結果。
`N≥2` 時 Detail 加「近 14 日第 N 次，連續第 M 日」；`M≥3` 或 `N≥3` 嚴重度升一級（封頂 High）；`down` 且 `M≥14` 加「已連續 M 日，故障尚未恢復」。
常數在 `PrtgRuleCatalog`，暫定待校準。查詢失敗只警告、用本趟資料判定。

### 抑制

規則型與簽章型抑制在發佈登錄簿前依主機標記 `Suppressed`（`SuppressionFilter.MarkSuppressed`）。被抑制的 finding 仍追加，不拉風險、不進 AI prompt 的 PRTG 段。
**標記誤報**：PRTG 問題某日標為誤報後，同主機同簽章 30 日內再出現視同已處理（`PrtgFalsePositiveWindow`，狀態判定與排行共用）：
不進日／週摘要、排行顯示「已標誤報」（每台主機的最近一次出現都在窗內才標）；finding 照常寫入主機日；改回其他狀態即解除；30 日後再出現視為新問題。

### finding 如何進入既有全鏈

- 簽章：`LogName=PRTG`、`Source=PRTG:{代碼}`、`EventId=0`、`EventKey=prtg:{代碼}:{objid}`（不得含 `|`，長度 ≤255）。判定是否 PRTG 一律看 `LogName`（`PrtgFindingMapper.IsPrtg`）。
- `RuleId`＝命中規則；`SampleMessages[0]`＝Detail；簽章帶 `PrtgSensorCategory`；數值趨勢 finding 另帶 `PrtgTrendEvidence`（§9a）。`Magnitude` 不寫進 `Count`（恆 1）。
- 分類與嚴重度取自規則物件，不走 `KnownIssueCatalog.Classify`。
- **追加對象**：當日已有分析紀錄的主機就地追加（依 `EventKey` 去重）；沒有紀錄但有至少一筆未抑制 finding 時建立 **PRTG 獨立主機日**
  （`AnalysisLogSource.Prtg=3`，`log_source` 欄）。之後日誌分析寫入同一主機日時原地更新同一筆紀錄並併回 PRTG finding。
  缺日補跑、重跑、首次回望、週檢、趨勢歷史、執行監看、排程預覽與儀表板「已分析主機日」都排除 PRTG 獨立主機日。詳情已精簡的紀錄不追加。
- 該日 PRTG 評估完成時登錄簿標記 `MarkEvaluated`；工作流程的 PRTG 腿以此判定成功，否則為等待（`prtg-day-not-evaluated`）。
- 每日執行輸出一行 finding 計數（含已抑制、已於 PRTG 確認、跨日升級、長期 Down）與 PRTG 獨立主機日數。

### 跨來源佐證

PRTG finding 追加時判定（`PrtgCorroboration`），寫進 `CorrelationAlerts`／`CorrelationAlertRefs`，走關聯抑制。同主機日配對屬弱佐證，配對不提高風險：

| PatternId | 事件側 | PRTG 側（未抑制） |
|---|---|---|
| `prtg-storage-corroborated` | 儲存 I/O 訊號 | hardware 分類的 warning 或 down |
| `prtg-capacity-corroborated` | `srv` 2013 | disk 分類的 warning |
| `prtg-outage-corroborated` | Kernel-Power 41／EventLog 6008 | availability 分類的 down 或 flapping |

冪等；模式被抑制時文字進 `SuppressedCorrelationAlerts`。歷史弱佐證由 `PrtgRiskReviewHostedService` 背景分批修訂。

### 未回報主機的 PRTG 提示

主機清單的「未回報」主機（`HostAdminService.IsSilent`）只讀鏡像標出 PRTG 現況（`ok` 對應的全部 device、未暫停 sensor）：
`down`＝有 availability 時看它們、否則看全部，任一 Down（「PRTG 鏡像：監測項目異常，請查感測器」）；`unknown`＝全部 Unknown 或無狀態（「PRTG：無資料」）；
`up`＝其餘（「有監測項目正常，未回報原因待查」）；`no-map`＝沒有 `ok` 對應。鏡像結構同步超過 2 天時加「（鏡像過期）」。

### finding 對日風險與 AI 的影響

任一未抑制且帶 `ElevatesDayRisk`（seed 只有 availability 的 down）→ 高；任一未抑制且嚴重度 High（含跨日升級）→ 中；其餘不變（「資料不足」不產生 finding）。
只升不降（`RiskLevels.MoreSevere`），AI 回寫同樣只升不降；風險依據記為 `prtg:{代碼}`；由低升為非低時標記待補 AI 判讀。
AI prompt 的【PRTG 監控訊號】區塊只餵已判定、未抑制的 finding，每列「[嚴重度] 規則描述：Detail」，不餵原始數值。

### 追加時機與 finding 登錄簿

規則評估後把「哪台主機命中哪些 finding」按日期發佈到登錄簿（`PrtgFindingsRegistry`）。主機當日紀錄在評估前已落地的，由 PRTG 路徑發佈後掃一次補追加（並補呼叫案件掛接）；
評估後才落地的，由兩條分析寫入路徑在紀錄剛寫完時就地併入（排在案件掛接與執行摘要之前）。

- 登錄簿按日期保存，某日沒有發佈時該日拿到空清單；兩條路對同一主機日以 `AttachExclusive` 序列化並依 `EventKey` 去重。
- 發佈帶每台主機的關聯抑制集合，兩條追加路徑共用。
- 全部日期發佈完送一次 `prtg-findings-ready`（不是進度）；逐日前送 `prtg-date-range`。PRTG 停用、初始化失敗、規則評估失敗、規則庫無規則一律發佈空集合（`finally` 保底）。

## 9a. 數值趨勢規則

四條規則只抓**慢性惡化**；一夜之間的突發由 PRTG 自身告警經狀態規則（warning／down）處理。全部預設停用，門檻暫定待校準。

| 代碼 | 量測（§9b 語意） | 命中（暫定） |
|---|---|---|
| `disk_free_trend` | 磁碟可用 %（`disk-percent-free`） | 低水位 20%、穩健下降 ≥0.5 pt/日、下降日 ≥70%、預估 7 日內到低水位或 30 日內耗盡 |
| `memory_used_trend` | 記憶體已用 %（`memory-percent-free` 轉向） | 35 日窗 ≥28 有效日；斜率 ≥0.3 pt/日；上升日 ≥70%；預估 7 日內 ≥90% |
| `cpu_load_trend` | CPU 已用 %（`cpu-percent-used`） | 同窗；斜率 ≥0.3 pt/日；上升日 ≥70%；近 7 日平均 ≥70% |
| `ping_latency_trend` | Ping 毫秒（`ping-ms`） | 同窗；近 7 日中位數 ≥ 前 28 日中位數 ×1.5 且 ≥50 ms；上升日 ≥60% |

- 判定：`PrtgDiskTrendEvaluator`（disk）與 `PrtgTrendEngine`（Theil–Sen 斜率、共用 `WorseningRatio`）；門檻型別 `PrtgDiskTrendThresholds`／`PrtgTrendThresholds`（`IsValid` 為合法範圍唯一出口）。
- 記憶體可用 % 在 `PrtgValueAssessment.EvaluateTrend` 轉成已用 %（100 − 值）——全專案只在這一處轉向。正式評估與草稿試算都經它。
- 解除與再發：最新有效日回到門檻安全側且斜率不再惡化即不命中；同一問題仍在與已處理後再發由既有簽章去重與處理狀態處理。
- **每日評估**（`PrtgDailyPipeline`）：只評估最新的已完成日、只讀已落盤資料，不等當趟取數、不呼叫 PRTG；候選依分類分頁（每頁 100 顆），
  每顆每日最多一筆 finding。每條規則印一行「{標籤}趨勢評估（日期）：N 顆，原因分組計數；新增 finding M 筆」，母體 0 印「無可評估 sensor」。
- **Detail 固定格式**（`PrtgTrendEvidence.Describe()`，唯一出口）：
  `{裝置}／{sensor}：{規則白話}；近 {N} 日有效 {M} 日；目前 {值}{單位}，每日 {斜率}{單位}；預估 {日期} 達 {門檻}{單位}；資料截至 {日}`；
  引擎沒給預估天數、或天數超過 `PrtgValueAssessment.MaxProjectionDays`（3,650）時「預估…」段改為「未預估到達 {門檻}{單位} 的日期」。
  簽章同時帶結構化 `PrtgTrendEvidence`（規則、窗口起訖、有效日、目前值、單位、斜率、門檻、預估日、來源 objid），詳情頁直接讀欄位，不解析文字。
- **啟用條件（後端強制）**：至少一顆對應有效、單位可判且達最少有效日、語意相符的 sensor；否則拒絕並指回環境探測的數值資料就緒度。匯入、儲存、啟用三處都檢查門檻與條件。
- **草稿試算**（規則頁，`RulesController` 的 `disk-trend-preview`）：四條共用同一個「完成日 × sensor」分頁骨架，判一列依規則選擇；只讀、不寫業務資料。
  結果列含就緒、排除原因、預期命中、目前值與單位，以及依目前抑制設定估計的抑制數。
- 時程：狀態型 finding 隔日第一次排程就有；趨勢型最早 28 個有效日後。

## 9b. 數值評估與就緒度

`PrtgValueAssessment` 是數值的唯一評估入口（每日管線、就緒度卡、規則啟用與試算、主機詳情共用）。只讀已落盤資料、每頁最多 100 顆。

**數值可信＝三條件**：(1) 該日 sensor 所在裝置有 `ok` 或人工對應，且是評估日的同一台主機（過去日取該日或之前最近一日，往回 31 天）；
(2) 該小時是可用列（§2）；(3) 單位可判。另加兩道合理性檢查：範圍外的小時不採用（值不合理）、近 7 個有效日每小時完全相同視為固定值。
每日代表值＝當日可用小時的平均，可用小時不足 12（`MinDailyUsableHours`）的日不列入。

**單位語意**（`PrtgValueSemantics`）：內建對照 `SNMP CPU Load`→`cpu-percent-used`、`SNMP Memory`→`memory-percent-free`、`SNMP Disk Free`→`disk-percent-free`、`Ping`→`ping-ms`；
`PrtgSensorTypeCategoryOverrides` 的 `type=分類:語意` 優先（`ParseSemanticOverrides`）。對照外一律「單位未知」。

**不足原因**（`PrtgValueReason`，中文標籤唯一出口 `PrtgValueReasonText.Label`）：就緒、有效日不足、品質不足、值不合理、固定值、單位未知、對應無效。
就緒度卡、規則頁試算與主機詳情對同一 sensor 顯示同一原因。主機詳情的「數值資料」欄：就緒為綠色徽章；有效日不足顯示「資料累積中 X／28 日」（灰色）；
其他原因為警示色；「資料不足」類不顯示為綠燈，也不產生 finding、不升風險。Maintain 另看到指向就緒度卡的連結，其他使用者不顯示設定入口。

## 10. 診斷資料傳輸（匯出／匯入）

維護頁提供唯讀診斷匯出與隔離匯入，用於在另一環境分析來源資料。匯入不覆寫正式 PRTG 表、規則、案件或通知。

- 匯出：區間 ≤366 天、HTTP 串流 JSON（V2，末尾 Manifest）、資料庫一致快照（SQL Server snapshot isolation／SQLite WAL，不滿足時拒絕）、keyset 分頁、128 MiB 額度（共用 256 MiB 帳本）。
- 匯入：`prtg-import-transfers` 持久工作、≤4 MiB 逐片傳送後串流驗證、雜湊一致才接受；綁定登入者與全站可見範圍；完成保留 7 天、放棄 1 天。舊 multipart `POST prtg-import` 回 410。
- 匯出不含 `Observations` 區段（`lf_prtg_observations` 已移除）；之前匯出的包仍帶此區段，匯入照驗欄位與筆數但不寫入任何地方。格式版本維持 2；
  **舊版站台匯不進新版匯出的包**（其驗證器要求該區段存在）。

## 11. 校準數值匯出與使用效果

`/admin/calibration`（頁面見 docs/WEB-SPEC.md §9.9f）回答四類資料量是否足夠並提供匯出；不是規則啟用閘門。
門檻是程式常數（`CalibrationConstants`），刻意不開設定。

| 項 | 單位 | 可用 | 充足 |
|---|---|---|---|
| PRTG 值型基線 | 有對應主機的白名單 sensor；某日涵蓋＝可用列 ≥12 | ≥10 台主機各涵蓋 ≥28 天 | ≥10 台各 ≥56 天 |
| PRTG 規則門檻 | sensor-日 | 狀態變更涵蓋 ≥28 天且 down ≥30 筆 | ≥56 天且 down ≥100 筆 |
| 數值取得量級 | 天 | 近 30 天有數值 ≥14 天 | ≥28 |
| 殘留判定門檻 | 含登入失敗明細的主機日 | ≥200 且 ≥14 天 | ≥1000 且 ≥28 天 |

- 分母為零判「不足」；PRTG 未啟用或鏡像無 sensor 判「無法取得」；門檻校準逐規則代碼進行。可用判定內嵌在 EF 查詢投影；判定快取 10 分鐘，匯出前強制重算。
- 匯出檔：自描述 JSON（`FormatVersion` 3），`MagnitudeSemanticsVersion=3`、`FormalEvaluationScope=state-change-messages`；含值型基線（56 天逐日聚合）、
  感測器摘要、type 輪廓（24 小時曲線，資料庫端聚合）、規則門檻（逐日命中、以最低門檻重評的 magnitude 樣本與 P50／P90／P99、`CurrentRules`）、
  數值取得量級、殘留判定指標（不含帳號名稱，與正式判定共用 `ResidualCredentialDetector.EvaluateMetrics`）。`detail=summary` 只匯出摘要。
- 匯出閘門：四項皆達「可用」才解鎖，否則勾「仍要匯出」覆寫；兩者都寫稽核。

**使用效果**（維護頁頁籤，`PrtgEffectivenessService`）：期間內 PRTG finding 數、低涵蓋取樣小時、建立的案件與交辦、已回覆、已抑制、靜音 profile、
PRTG 獨立主機日、佐證主機日，以及按規則代碼分列的「標記誤報／已處理」（分母是已標記的 PRTG 問題；全部為 0 時顯示「尚無標記」）。各指標分母不同，不是轉換漏斗。

## 12. 資源守門

夜間批次同時對 NetIQ 與 PRTG 大量查詢；守門定期讀受監看 sensor 的即時值，資源緊張就暫停取數。設定見 §7，預設關閉。

### 受監看的 sensor

`PrtgResourceGuardSensorObjids` 非空時直接用它；判定只有一份（`PrtgResourceGuardTargets.Resolve`）：
1. 目標位址＝每台 `Sentinel.BaseUrl` 的 host ＋ `PrtgUrl` 的 host。
2. 找位址相同的 device，三段依序命中即停：來源位址解析成 IP 後比 device 純語法 IP → 主機名稱字面比對 → device 側 DNS（只對名稱型 device，整趟預算 20 次，`DeviceDnsBudget.Limit`，用盡時警告）。
3. PRTG 主機對不到時改找掛 `corehealth` sensor 的 device。
4. 取命中 device 底下未暫停、分類為 cpu／memory 的 sensor；PRTG 主機另加 corehealth。找不到時回空清單並警告。

資料來源抽象成 `IPrtgResourceGuardSource`：夜間批次讀鏡像（`PrtgMirrorGuardSource`），預覽與自動偵測直接查 PRTG（`PrtgLiveGuardSource`，裝置與感測器全量各一次，
筆數少於 `treesize` 時警告截斷）。預覽查不通時退回鏡像並標 `mirror-fallback`。

### 夜間自動偵測並填入

`PrtgResourceGuardAutoDetect.EnsureAsync` 在每日管線建立作業範圍**之前**執行（守門啟用時）：
- 清單為空，或清單等於上次自動偵測結果且其中有 sensor 已不在鏡像 → 以直接查 PRTG 的來源偵測（15 秒逾時）。
- 結果寫回清單（objid 遞增排序）並記 blob `prtg_resource_guard_autodetect`（`DetectedAt`、`Objids`）；人工填寫的清單（不等於 blob）一律不覆寫。
- 逾時或失敗印「自動偵測未完成（原因），本趟不守門，取數照常進行」、不改清單；結果為空印「自動偵測沒有找到守門目標」。
- 偵測到的目標直接換上本趟已建立的守門實例（`PrtgResourceGuard.ReplaceTargets`），當晚就守得到，不等鏡像補抓。
- 設定頁守門預覽回傳 `autoDetectedAt`（`PrtgResourceGuardAutoDetect.Describe`），清單上方顯示「自動偵測 yyyy-MM-dd」。「自動偵測並填入」鈕保留為手動入口（帶 `forceAuto=true`，結果寫回輸入框、需按儲存）。

### 判定方向

`cpu` 值 ≥ `PrtgResourceGuardCpuPercent` 算超標；`memory`（主通道多為可用 %）與 `corehealth` 值 ≤ `PrtgResourceGuardMemoryFreePercent` 算超標——方向相反。
狀態非 Up、值不是百分比、objid 查無、分類不符一律忽略（不判超標）。判定方向有專屬測試釘住。

### 閘門

每趟一個實例，插在 NetIQ 每批查詢前與 PRTG 每個 `historicdata` 與狀態變更逐台查詢前；本機分析、回填、探測、快照不受守門。

- 未滿 `CheckSeconds` 直接放行；連續 `Strikes` 次超標才暫停 `PauseMinutes`；單趟累計超過 `MaxPauseMinutes` 放行並警告、該趟不再暫停。
- 併發時只有一個呼叫者讀值；取消穿透暫停；讀值失敗、對不到 sensor 一律放行並每趟警告一次。
- 進入／離開暫停寫 Milestone 與 `IRunConsole`；經 `IRunProgress` 送 `guard-paused`／`guard-resumed`（`ReportProgress` 內須排在 catch-all 之前），排程作業頁顯示「資源緊張，暫停中」。

## 13. 實機驗收劇本

程式測試不代表真實 PRTG 驗證；接上正式 PRTG 後依下表逐步確認，每步寫明看哪個畫面、正常長什麼樣、不正常去哪裡。
全部走完一次，PRTG 維護頁「啟用準備」六項皆確認、「最近一次排程驗收」三項皆為「已確認」，才算這個站台的 PRTG 整合可用。

| 步 | 做什麼 | 看哪裡 | 正常 | 不正常時 |
|---|---|---|---|---|
| 1 | 環境探測 → 完整環境探測 | 探測輸出 | 版本與 device／sensor 總數有值；9d-5 逐物件查詢驗證印「✓」；站台對照 [S1] 範圍台數合理、[S3] 守門目標找得到 | 「無法量測」或空白 HTML：先查 PRTG 位址與認證；[S1] 範圍外顆數過大：核對主機主檔 IP；9d-5 印「✗」：依 BACKLOG「狀態變更改逐感測器查詢」處理 |
| 2 | 鏡像狀態 → 同步結構與對應 | 對應摘要 | `ok` 台數接近預期、`unmatched` 與 `conflict` 可解釋；同步狀態來源「手動」且成功 | `ok=0`：主機主檔 IP 與 device 位址對不上（§4）；衝突：在衝突清單指派或排除 IP |
| 3 | 排程作業 → 立即執行（回望 1 天） | 狀態卡 PRTG 軌、執行詳情 `[PRTG]` 行 | 三個同步階段有進度；「PRTG 規則評估完成」行 finding 數與涉及主機數非零（站台正常時可為 0 但不得缺行）；就緒度行有分組計數 | 「規則庫尚無啟用中的 PRTG 規則」：規則維護頁套用內建規則；「無主機對應可用」：回到第 2 步 |
| 4 | 同一趟結束後看 AI 卡 | 排程作業 AI 分析卡 | 待補歸零、上次執行正常結束；閒置原因沒有 `waiting-fetch` 殘留 | `disabled`：設定 AI 服務；待補不減：看 AI 執行紀錄的失敗原因 |
| 5 | 隔晚排程後 | 執行總表 PRTG 徽章、PRTG 卡「待補評估」 | 昨日徽章 `success`、待補評估為空 | `partial`／`failed`：執行詳情找第一個 ✗；待補評估有日期：下一晚會自動補，連續出現就查該日原因 |
| 6 | 快照 | 鏡像狀態 → 快照最近 24 個完整小時 | 有目標的小時中 ≥75% 品質達標、最近一小時有成功 | 跳過原因是 `structure-sync-active`／`backfill-active` 屬正常讓路；連續 `insufficient`：看快照間隔是否被退避拉長 |
| 7 | 回頭看維護頁 | 啟用準備與最近一次排程驗收 | 六項已確認、三項已確認 | 哪一項待確認就回到對應的步 |
