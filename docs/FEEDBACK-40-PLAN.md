# 回饋修正第 40 輪規劃

> 狀態：規劃完成、待實作
> 基準：dev@e61aec9（3586 綠，略過 6）
> 來源：使用者回饋六項（狀態卡高度／AI 與 PRTG 三路同步／資源守門搬設定頁／自動偵測直查 PRTG／
> PRTG 主機對應時機／排程流程梳理）
> 實作方式：委派 agy（`claude-opus-4-6-thinking`），Claude 規劃與驗收；額度用盡改 Claude 自做（切換點記在執行紀錄，不換回）。
> 分支：自 `dev` 開 `feature/feedback-40`。

## 0. 核對結果摘要

| 項 | 判定 | 根因 |
|---|---|---|
| P1 狀態卡高度 | 成立 | `.lf-run-status-grid` 是 `align-items:start`；卡片無 flex column；三卡固定列本就不同 |
| P2a AI 不跟著跑 | 成立 | AI 是獨立常駐服務，有自己的開關 `AiEnabled`（預設關）；取數執行不通知它，只靠 60 秒輪詢 |
| P2b PRTG 軌位置 | 成立 | 後端三軌已分離，前端把 PRTG 軌畫在取數卡 |
| P2c 三路不並行 | 半對 | 三個 Task 同時啟動；`triggered` 模式取數等分析結束是設計；**`all-mapped` 模式候選與分析無關卻同樣空等到結束**（bug） |
| P3 守門位置 | 可行 | 守門在 PRTG 維護頁「擷取參數」卡內；消費端是 NetIQ 與 PRTG 兩路；整包設定 DTO 已可空 |
| P4 自動偵測落空 | 成立 | `Resolve` 只查鏡像表；鏡像表只有夜間結構同步會填；沒有手動同步入口 |
| P5 對應時機／秒完成 | 成立 | 對應只在夜間 PRTG 路徑與人工對應變更時跑；**`NormalizeIp` 只 trim＋小寫，「IP:port」與 DNS 名稱原樣比對必落空**；零已對應主機時靜默 return、`prtg-done` 無條件送出標「已完成」 |
| P6 流程梳理 | — | 收斂進 A／C／E／G |

順手發現（本輪一併處理）：主機新增／改 IP 不重算對應（只有人工對應與排除會）；PRTG-SPEC §4「用 DNS 名稱→略過」與實作（落 `unmatched`）不符；
守門在夜間批次於兩路啟動前建構，首晚鏡像為空時自動偵測必落空（由 D 的 objid 覆寫清單補救，文件說明）；
`TryRemapToday` 是 `SettingsController` 私有方法，其他觸發點無法共用。

## 1. 定案（與使用者討論後）

1. **AI 開關移除**：`AiEnabled` 退場。AI 服務已設定（`AiProvider` 有值）即一律啟用＝跟隨取數即時開跑＋在 `AiWindows` 內背景消化積壓。存量校正閘門照舊。
2. **PRTG 裝置位址正規化＋DNS 解析**：使用者環境 device `host` 是「IP＋port」或 DNS 名稱。對應與守門共用同一套位址正規化（去 port、非 IP 走 DNS 解析）。
3. **自動偵測走 PRTG 即時查詢**，鏡像表退為 fallback；另加「同步結構與對應」背景工作作為鏡像與對應的手動入口。
4. 設定頁新頁籤名稱「**資源守門**」。
5. P1 不套 `ui-ux-pro-max`（純對齊修正）。
6. 第一次執行是白天手動跑、夜間排程尚未跑過——升級後首次「同步結構與對應」由使用者手動觸發。

## 2. 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| B | 位址正規化＋對應觸發點＋取數收尾修正 | 中 | 無 | 1 |
| C | 「同步結構與對應」背景工作 | 中 | B1（正規化）、B2（重算服務） | 2 |
| D | 守門自動偵測走 PRTG 即時查詢 | 中 | B1 | 3 |
| E | AI 跟隨取數、移除 `AiEnabled` | 中 | 無 | 4 |
| A | 狀態卡版面重定義（三卡等高、PRTG 卡承載每日擷取軌與同步狀態） | 中 | C（狀態端點）、E（AI 卡欄位） | 5 |
| F | 資源守門搬設定頁 | 中 | D（按鈕呼叫的端點形狀） | 6 |
| G | 文件同步＋終檢 | 小 | 全部 | 7 |

每批一個 commit，訊息 `feat: <內容>（回饋第 40 輪批次X）`。

---

## 批次B：位址正規化、對應觸發點、取數收尾

### B1 PRTG 位址正規化（對應與守門共用）

**現況**：`PrtgHostMapper.NormalizeIp` 只 trim＋小寫，**有 20 個呼叫點**（Core 對應、`EfPrtgStore` 的 IP 排除鍵、`SettingsController` 六處、`DashboardController`）；守門 `PrtgResourceGuardTargets` 對 Sentinel 位址做 DNS 解析（`FindDevicesForHost`／`ResolveHostAddressesWithTimeout`），但對 device 側的 `Ip` 直接字串比對。

**分層（本輪關鍵設計判斷）**：正規化拆成**兩層**，因為 `NormalizeIp` 同時被當成「IP 排除清單的儲存鍵」與「device 分組鍵」——把 DNS 併進單一入口會讓每次存取排除清單、每個 device 分組都做一次 DNS 查詢，且分組鍵會隨 DNS 結果變動。

**契約**

*第一層：純語法正規化（無 IO，取代 `NormalizeIp` 的實作）*
- 新增 Core 的位址正規化工具（純靜態、無 IO、無快取），輸入任意位址字串，輸出「正規化 IPv4／IPv6 字串或 null」：
  - 去前後空白；去 scheme（`http://`、`https://`）；去尾端路徑與 query；去尾端 port（`host:數字`；IPv6 中括號形式 `[::1]:8080` 也要能拆，裸 IPv6 不得被誤拆）。
  - 剩餘字串是合法 IP → `IPAddress.Parse(...).ToString()`（消除前導零與大小寫差異）。
  - 不是 IP → **回 null**（不做 DNS）。
- `PrtgHostMapper.NormalizeIp` 改為委派給它，**簽章與可見性不變**，20 個呼叫點不動。
- **破壞性判準與反例**：語意從「任何非空字串都回值」收緊為「只有 IP 回值」。受影響的是 IP 排除清單——舊資料可能存了非 IP 字串（`UpsertIpExclude` 原本不驗證），收緊後 `DeleteIpExclude` 會回 0、使用者刪不掉自己存過的列。
  因此 `DeleteIpExclude` 改為**先以原始字串（trim 後）精準比對刪除，再以正規化值刪除**，兩者任一命中即算成功；`UpsertIpExclude` 維持「正規化為 null 就不寫入」。合法值長得像它的反例：`"10.1.2.3"`（正規化成功，走新路徑）與 `"prtg-old-name"`（正規化 null，走原始字串路徑）都必須可刪。

*第二層：位址解析器（有 IO，只給對應與守門用）*
- 新增可注入的解析介面（單一方法：原始位址字串 → 正規化 IPv4 或 null），正式實作為：
  1. 先跑第一層；成功直接回（**不做 DNS**）。
  2. 失敗時取「去 scheme／port／路徑後的主機名稱 token」做 DNS 解析（逾時 2 秒，暫定），取第一個 IPv4，再跑一次第一層。
  3. 無 IPv4、解析失敗或逾時 → null，不擲例外。
- **實例內快取**：同一名稱在同一個解析器實例內只解析一次（含失敗結果，避免逾時重複付 2 秒）。**不得使用 static 快取**——跨趟快取會讓 DNS 變更在站台重啟前不生效。
- 生命週期：`MapForDate` 一趟建一個、守門 `Resolve` 一次建一個。
- 守門既有的 `ResolveHostAddressesWithTimeout` 與 `FindDevicesForHost` 內的 DNS 邏輯**移除並改用解析器**，不得留兩份 DNS 程式碼。
- **守門的比對鍵有三段優先序**（B1-step1 補充）：IP → DNS 解析出的 IP → **主機名稱字面**。
  第三段是既有能力，不可移除：device 的 `Ip` 欄位也可能填 DNS 名稱，而內網名稱未必進得了 DNS，
  兩邊填同一個名稱時仍應命中。對應（`MapForDate`）**不走第三段**——主機側只有 IP，名稱比對無意義，
  維持「解析不到就略過（無 IP）」。

*消費端行為*
- `lf_prtg_devices.Ip` 欄位**保持存 PRTG 原始字串**，正規化只在比對時做——鏡像必須是 PRTG 現況，不可洗資料。
- 對應（`MapForDate`）：device 側與主機側（`WebHost.IpAddress`）都改用解析器取得比對值；device 解析為 null → 計入 `skipped_no_ip`；**排除清單與 device 分組鍵仍用第一層**（純語法），避免分組鍵隨 DNS 變動。
- 守門（`Resolve`）：device 側、Sentinel `BaseUrl` 側、`PrtgUrl` 側三者都走解析器（使用者環境是「IP＋port」或 DNS 名稱）。既有的 corehealth fallback 與 `Uri.TryCreate` 取 host 的流程保留。
- 比對一律在「雙方都解析成 IPv4 字串」之後進行；任一側為 null 即視為對不到，訊息要說明是哪一側解析失敗。

**不能破壞**：既有一對一／衝突／人工對應／排除的判定順序與結果；`Note` 長度截斷。

**驗收**
- 第一層單元測試：`"10.1.2.3:8080"`→`10.1.2.3`；`"https://10.1.2.3:443/"`→`10.1.2.3`；`" 010.001.002.003 "`→`10.1.2.3`；`"[fe80::1]:80"`→`fe80::1`；`"::1"`（裸 IPv6，不得被誤拆成 `:` + port）→`::1`；`"prtg.local"`→null；`""`／null→null。
- 第二層單元測試（假解析器）：IP 直接回不呼叫 DNS（斷言解析器呼叫次數 0）；名稱解析成功回 IPv4；解析失敗回 null；**同一名稱查兩次只解析一次**（含失敗案例）。
- `PrtgHostMapperTests` 新增：device host 帶 port 對到主機 `ok`；DNS 名稱可解析對到 `ok`；解析失敗計入 `skipped_no_ip` 而非 `unmatched`。
- `PrtgResourceGuardTargetsTests` 新增：device `Ip` 帶 port 仍能命中；Sentinel `BaseUrl` 為「IP:port」與為 DNS 名稱兩種都能命中。
- `EfPrtgStoreTests` 新增：存入非 IP 的舊排除列後仍可用原字串刪除（回 1）；IP 列以帶 port 的字串刪除也命中。
- grep：`Dns.GetHostAddresses` 在 `LogForesight.Core` 只有解析器一處（`grep -rc` 應為 1）。
- 全套測試綠，總數比 3586 多。

### B2 對應重算服務＋主機新增／改 IP 觸發

**現況**：`SettingsController.TryRemapToday()` 私有，四個人工對應／排除端點呼叫；`HostAdminService` 已算出 `ipChanged` 但不重算。

**契約**
- 把「重算今天對應」抽成 Web 服務（單一入口，回傳警告字串或 null，內部吞例外並記 WARN，語意與現行相同）；四個既有呼叫點改用它。
- `HostAdminService` 在「新增主機且有 IP」或「IP 變更」或「Active／MergedInto 變更」時呼叫它；PRTG 未啟用時零成本直接返回。
- 呼叫必須在主機寫入**之後**，且失敗不影響主機儲存結果（只在回應帶警告，比照人工對應端點的 `RemapWarning`）。

**驗收**
- `HostAdminServiceTests`：改 IP 後重算被呼叫一次、只改描述欄位不呼叫、PRTG 停用不呼叫。
- grep：`TryRemapToday` 在 Controller 內零定義。

### B3 取數收尾修正

**現況**：`PrtgTriggeredValueFetcher` 在 `all-mapped` 仍輪詢到分析結束；零候選主機靜默 return；`prtg-done` 一律 `(0,0)`。

**契約**
- 生效範圍為 `all-mapped` 時，首輪取完即回傳，不進輪詢迴圈、不做收尾掃描（候選集合與分析無關）。`triggered`／`triggered-plus-list` 行為不變。
- 「已對應主機」定義：該日 `lf_prtg_host_map` 中 `MapStatus=ok` 且 `HostId` 非空（人工對應也是 `ok`，算在內）。
- `all-mapped` 且已對應主機為零：印一行警告（明說「該日沒有任何 `ok` 對應的主機，請先執行『同步結構與對應』或檢查鏡像狀態」）並寫里程碑；結果仍為 `success`（跑了、沒東西可抓，不是失敗）。
- `triggered` 模式零觸發主機是常態（沒人出問題），**不**警告。
- `prtg-done` 改帶數字：done＝實際取數主機數、total＝目標 sensor 數（暫定；前端據此顯示「已完成：主機 N 台／sensor M 個」）。`SchedulerRunState` 的 PRTG 軌在完工時保留這組數字。
- 執行紀錄 PRTG 欄位不新增狀態值（四值不變）。

**驗收**
- `PrtgTriggeredValueFetcherTests`：`all-mapped` 下 `analysisCompleted` 恆 false 也能在一輪內回傳；零對應主機印出警告行；`triggered` 零主機不印警告。
- `SchedulerRunStateTests`：`prtg-done (3, 40)` 後 `PrtgCompleted=true` 且軌數字為 3/40。
- `RunsPageUiTests` 反射對照表仍全綠（phase 清單不變）。

---

## 批次C：「同步結構與對應」背景工作

**現況**：結構同步唯一呼叫點在夜間 PRTG 路徑；回填 `syncStructure:false`；沒有手動入口。實機結構同步可能跑數十分鐘（見 WEB-SPEC §9.10），不能塞在 HTTP 請求裡。

**契約**
- 新增 Web 背景工作（比照 `PrtgBackfillService`：單飛、進度、`GET status`、`POST start`，權限 `Maintain`），Core 側對應的 runner 做三步：
  1. 結構同步（沿用 `FetchDayAsync(today, syncStructure:true, fetchValues:false)` 的結構階段，三個 `prtg-sync-*` phase 照回報）；
  2. `MapForDate(今天)`；
  3. 產出摘要：裝置數、sensor 數、對應 ok／manual／conflict／unmatched／skipped 各數、耗時。
- 狀態端點回：`running`、目前 phase 與進度、上次完成時間、上次摘要（含對應各數）、上次錯誤訊息。上次摘要**持久化**（`SystemSettings` 以外的 blob，暫定 `prtg_sync_status`），重啟後仍看得到；從未跑過時為 null，前端顯示「尚未同步」。
- **互斥**：
  - 取數執行進行中 → 拒絕啟動同步工作（409，訊息說明）。
  - 同步工作進行中，夜間／手動取數執行開始 → PRTG 日路徑**等待同步工作結束**，然後**跳過**自己的結構同步階段（鏡像剛更新），對應照做（對昨天）。等待期間 PRTG 軌顯示「等待手動同步完成」（新 phase，暫定 `prtg-wait-sync`，加進 `RunPhases` 與前端對照表）。
  - 歷史回填與同步工作可並行（回填不碰結構表）。
- 入口：排程作業頁 PRTG 卡按鈕「同步結構與對應」（批次 A 接）；PRTG 維護頁「鏡像狀態」頁籤同一顆按鈕與同一份狀態（本批先接維護頁）。
- 啟用 `PrtgEnabled` 時**不**自動啟動；切換即存的回應帶一句提示「請執行『同步結構與對應』」。

**不能破壞**：夜間 PRTG 路徑在沒有同步工作時行為完全不變；回填行為不變。

**驗收**
- Runner 測試（假 client／store）：三步順序、摘要數字正確、結構同步失敗時不做對應且狀態記錯誤。
- 互斥測試：取數執行中 `start` 回 409；同步進行中啟動取數，PRTG 路徑不呼叫 `syncStructure:true` 且對應照跑（以替身斷言呼叫參數）。
- 狀態持久化測試：完成後重建服務仍讀得到上次摘要。
- `RunsPageUiTests` 反射對照表涵蓋新 phase。

---

## 批次D：守門自動偵測走 PRTG 即時查詢

**現況**：`PrtgResourceGuardTargets.Resolve(prtgStore, …)` 讀鏡像表；preview 端點只在拿到 objid 後才打 PRTG。

**契約**
- `Resolve` 的裝置／sensor 來源抽象成介面（兩個實作：鏡像表、PRTG 即時），判定邏輯（位址比對、corehealth fallback、cpu／memory 篩選）**只有一份**。
- 即時實作：向 PRTG 查裝置清單（欄位 objid、host、name，分頁沿用既有 table.json 走法；裝置數量級遠小於 sensor，全量可接受），命中裝置後只查該裝置底下的 sensor（`filter_parentid` 暫定；若實機不支援，退路是以 `content=sensors&columns=objid,parentid,type,status` 全量分頁後記憶體過濾，量大但仍為單次操作）。
- preview 端點：`forceAuto=true` 或鏡像表裝置數為 0 → 走即時；即時失敗（連線、逾時）→ 退回鏡像並在回應 `source` 標明（新增值 `live`／`mirror-fallback`，既有 `override`／`auto` 語意不變），訊息說明退路原因。
- 夜間批次 `PrtgResourceGuard.TryCreate` **維持讀鏡像**（零成本、無外部呼叫）；文件說明「首晚鏡像為空時自動偵測落空，先用自動填入把 objid 存進覆寫清單」。
- 訊息「找不到主機「X」對應的 PRTG 裝置」保留，但即時模式下加註「（已直接查詢 PRTG）」讓使用者分得出不是鏡像沒資料。

**驗收**
- `PrtgResourceGuardTargetsTests`：同一組輸入用兩種來源得到相同 objid 集合。
- 即時來源測試（假 HTTP）：裝置分頁合併、只對命中裝置查 sensor、失敗擲出可辨識例外。
- `PrtgResourceGuardPreviewEndpointTests`：鏡像為空自動走即時；即時失敗回 `mirror-fallback` 並帶訊息；`forceAuto=false` 且鏡像有資料仍走鏡像。

---

## 批次E：AI 跟隨取數、移除 `AiEnabled`

**現況**：`ScheduleOptions.AiEnabled`（預設 false）、`AiWindows`、`AiConcurrency`；`TickAsync` 以 `AiEnabled` 為第一道閘；`prtg-findings-ready` 只設旗標，無推送。

**契約**
- 移除 `ScheduleOptions.AiEnabled`；舊 blob 內殘留欄位由反序列化忽略（升級零動作）。`GET ai-status` 移除 `aiEnabled`；`nextTriggerTime` 語意改為「下一個背景補跑窗口起點」（在窗口內時為 null）。
- 「AI 已設定」＝`SystemSettings.AiProvider` 非空（沿用 `HostDayPostProcessor` 判 `aiConfigured` 的同一判準，不另造一份）。未設定時 `idleReason=disabled` 文案改為「AI 服務未設定」。
- **跟隨取數**：`SchedulerRunState` 在 `PrtgFindingsReady` 由 false 變 true 時發出通知（事件／回呼，兩個 HostedService 不得互相注入建構）。AI 服務收到後：無執行中、存量校正已完成、有待補 → 立即 `TriggerRunAsync`，trigger 沿用取數執行的來源（`manual`→不受窗口；`schedule`→受 `AiWindows` End 到點停）。不滿足時只更新閒置原因，等背景輪詢。
- 背景輪詢（60 秒）保留：在 `AiWindows` 內且有待補即跑，用於消化積壓與重試失敗件。
- 取數執行中的既有閘門（跳過今天／昨天直到 findings 就緒）不變。
- 前端：排程設定「AI 分析排程」區塊移除啟用勾選，改一行說明「AI 分析在每次取數執行後自動進行；以下設定背景補跑窗口與併發數」；AI 卡移除「排程未啟用」提示與 `disabled` 那條帶件數文案；「下次觸發」列改「背景補跑窗口」顯示窗口文字。

**升級注意**：原本 AI 排程關閉的站台升級後 AI 會自動開始跑（存量校正閘門先擋一次全庫）。

**驗收**
- `ScheduleOptionsTests`：含 `aiEnabled` 的舊 JSON 可反序列化且無該屬性。
- `AiAnalysisSchedulerTests`：findings 就緒通知→立即觸發（不等 tick）；執行中收到通知不重入；存量校正未完成不觸發；`manual` 來源在窗口外仍跑、`schedule` 來源在窗口外不跑。
- grep：`AiEnabled` 在 Core／Web／js／Views 零命中（測試除外，測試也應清零）。
- `RunsPageUiTests`：閒置原因對照表無 `disabled` 帶件數分支。

---

## 批次A：狀態卡版面重定義

**現況**：見 §0 P1／P2b。

**契約**
- CSS：`.lf-run-status-grid` 改 `align-items:stretch`；卡片 `display:flex; flex-direction:column`，按鈕列貼底（`margin-top:auto`）；三卡等高由 grid 保證，不寫死 `min-height`。單欄（≤991px）時各卡自然高度。
- 三卡定義：
  - **取數執行**：本機／NetIQ 兩條軌（PRTG 軌移出）、既有 kv 列、最新訊息、立即執行／停止。
  - **AI 分析**：依批次 E 欄位。
  - **PRTG**（改名，原「PRTG 歷史回填」）：kv 列「總開關」（啟用／未啟用，來源既有 settings 查詢）、「結構同步」（上次完成時間＋對應 ok／conflict／unmatched 摘要，來源批次 C 狀態端點；未跑過顯示「尚未同步」）、「每日擷取」進度軌（原第三軌；閒置時顯示上次執行結果文字）、「歷史回填」列與進度軌、按鈕列：同步結構與對應／開始回填／詳細輸出／複製。回填輸出仍預設收合。
- PRTG 卡的最新訊息列：取執行輸出中 `[PRTG] ` 前綴的最後一行（暫定；若狀態 DTO 沒有分路訊息則顯示共用的最新訊息，並在執行紀錄中註明）。
- `prtg-done` 完成文字：「已完成：主機 N 台／sensor M 個」；PRTG 啟用且 N=0 且生效範圍 `all-mapped` → 「已完成（無已對應主機）」；PRTG 停用 → 「未啟用」。
- 三卡輪詢頻率不變；進度軌預留高度規則不變（PRTG 卡兩條軌各自預留）。
- 沒有 `Maintain` 時新按鈕隱藏，狀態列照顯示（§9.10 既有規則）。

**驗收**
- `RunsPageUiTests`：取數卡 DOM 內無 `schedule-prtg-*` 元素；PRTG 卡含同步狀態元素與兩條軌；反射對照表全綠。
- 瀏覽器實測（本機 dev server）：三卡等高（`getBoundingClientRect().height` 三者相等）、單欄模式不重疊、收合展開回填輸出不影響另兩張卡。
- 無 inline style（grep `style="` 在 `Runs.cshtml` 零命中）。

---

## 批次F：資源守門搬設定頁

**契約**
- 設定頁新頁籤「資源守門」（放在「資料保留」之後、「郵件通知」之前，暫定）：八個守門欄位＋「預覽受監看 sensor」「自動偵測並填入」兩鈕（呼叫批次 D 的 preview 端點，行為與現行維護頁一致）。頁籤頂端一行說明：「守門同時節制 NetIQ 取數與 PRTG 擷取；資源數據取自 PRTG，需先在 PRTG 維護頁啟用並設定連線」，PRTG 未啟用時該行改為警示樣式，欄位仍可編輯。
- 儲存走整包 `PUT /api/admin/settings`（欄位可空、有送才更新，現行已支援）；守門欄位的驗證（範圍、objid 格式）在整包與 `PUT settings/prtg` **共用同一份**。
- PRTG 維護頁「擷取參數」頁籤移除守門卡，原位留一行指路連結到設定頁該頁籤；`PUT settings/prtg` 仍接受守門欄位（相容，可空）。
- 設定頁「儲存」的跨欄位驗證不得因守門欄位被擋（守門欄位彼此無跨欄位關係）。

**驗收**
- 設定頁 UI 測試：頁籤數 8、守門欄位 id 存在；`PrtgAdminPageUiTests`：維護頁無守門欄位 id、有指路連結。
- `SettingsController` 測試：整包只送守門欄位時其他 PRTG 設定不變；objid 非數字被拒；兩個端點對同一非法值回同一錯誤訊息。
- 瀏覽器實測：設定頁按「自動偵測並填入」可填入 objid 並儲存成功。

---

## 批次G：文件同步與終檢

- PRTG-SPEC：§4 對應（位址正規化、DNS、觸發時機三處）、§7 操作介面（設定頁有「資源守門」頁籤、PRTG 卡）、§8 端點（同步工作兩支、preview `source` 新值）、§12（來源抽象、首晚說明、頁籤位置）、新 §5a「同步結構與對應」（含互斥）。
- WEB-SPEC：§9.9b 設定頁頁籤、§9.9e 維護頁、§9.10 三卡定義／AI 未啟用提示刪除／閒置原因／phase 清單加 `prtg-wait-sync`／`prtg-done` 帶數字。
- DETECTION-SPEC「兩個獨立排程」：改為「取數排程與 AI 服務」，刪除「各有自己的啟用開關」，補跟隨機制。
- DB-SPEC：新 blob `prtg_sync_status`。
- README 升級注意：AI 自動啟用；升級後先手動「同步結構與對應」。
- CLAUDE.md 測試基線更新。
- 終檢：兩個獨立 Explore（程式碼／文件）；便宜檢查先做：`aiEnabled` 前端消費點 grep、`prtg-done` 數字前端消費點、`source` 新值前端文案、定案 §1 六條逐條對照。

---

## 3. 規劃完成後複檢

- **與既有設計衝突**：
  - DETECTION-SPEC「各有自己的啟用開關」與 WEB-SPEC「AI 分析排程未啟用的提示」——本輪推翻，批次 E／G 改文件。
  - PRTG-SPEC §7「設定頁沒有 PRTG 頁籤」——本輪推翻為「設定頁有『資源守門』頁籤，其餘 PRTG 設定仍在維護頁」，理由：守門節制的是 NetIQ 與 PRTG 兩路。
  - PRTG-SPEC §4「device 沒有 IP（用 DNS 名稱）→ 略過」——實作原本落 `unmatched`，B1 後改為「解析失敗才略過、解析成功照對」，文件同步改。
  - 第 35 輪「AI 排程預設關閉」的顧慮（升級存量搶跑）由存量校正閘門承接，E 保留該閘門。
- **批次之間**：B1 正規化被 B（對應）、C（對應）、D（守門）三處消費，先做；C 的狀態端點被 A 消費，C 在 A 之前；E 改 `ai-status` DTO，A 的 AI 卡在 E 之後；F 呼叫 D 的 preview 端點，F 在 D 之後。C 與夜間路徑的互斥、C 與回填的並行已明寫。
- **四個坑**：
  - 什麼算一個／分母為零：B3 明寫「已對應主機」定義與零主機在兩種模式的不同處置；`prtg-done` 數字零時的三種文案在 A。
  - 破壞性判準：B1 明寫鏡像 `Ip` 欄位不洗資料；E 移除欄位靠反序列化忽略，不改 blob。
  - 單向閘門：C 的互斥兩向都寫了繞過路徑（同步中取數等待再跳過；取數中同步被拒）。
  - 移除類依賴方：`AiEnabled` 的消費點（`TickAsync`、`GetAiStatus`、`runs.js` 兩處、`Runs.cshtml` 勾選、測試）列在 E 驗收 grep；`TryRemapToday` 四個呼叫點在 B2。
- **升級路徑**：AI 自動啟用、首次手動同步、`prtg_sync_status` 為 null 的顯示——皆已交代。
- 複檢完成，另新增一項：C 的等待邏輯需要 PRTG 日路徑能查到同步工作狀態（Core 不能依賴 Web 服務）——契約補為「以注入的『等待同步完成』委派傳入 `PrtgDailyPipeline`，無同步機制時傳 null＝行為不變」。

## 4. 執行紀錄

本輪委派模型：agy 的 `claude-opus-4-6-thinking`（整輪同一個，額度用盡才改 Claude 自做）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| F 資源守門搬設定頁 | Claude（agy 五小時額度歸零） | 通過 | 全套 3636 綠（+1）；守門欄位在設定頁、維護頁零殘留；`prtg-admin.js` 中 `prtg-guard` 零命中（欄位實作只有一份）；BOM 與 NUL 無新增 | 前端沒有把守門那 100 行複製到設定頁，而是抽成獨立模組 `prtg-guard.js`（`loadGuardFields`／`collectGuardPayload`／`bindGuardPreview` 三個出口），設定頁 import 使用，維護頁整段移除。**有四條舊測試守著「守門在 PRTG 維護頁」的舊契約**，本輪推翻後全部改為指向新位置（含「記憶體標籤要有可用二字」這條方向性守門，不可因搬遷而遺失）。全套跑到一條 `SentinelRestDirectoryClientTests` 紅，單獨重跑 47 綠、本輪未改該檔，確認是 BACKLOG 已記載的並行負載不穩定測試（清單首位），非本輪造成 |
| A 狀態卡版面重定義 | Claude（agy 五小時額度歸零） | 通過 | 全套 3635 綠（+2）；瀏覽器實測（載入真正的 site.css）1400px 寬三卡各 377px 等高、三個按鈕列同在 y=341；800px 寬單欄堆疊不重疊；BOM 與 NUL 皆無新增 | 兩件事與規劃不同：(1) 規劃寫 PRTG 卡要有「最新訊息」列，實作時發現狀態 API 的 `latestMessage` 是整趟共用的最後一行、取數卡已在顯示，再放一次只是重複，且沒有分路訊息可填——**移除該元素**並在頁面留註解說明改由執行詳情承擔（每行有 `[PRTG]` 前綴）；(2) 卡片 id 由 `prtg-backfill-section` 改為 `prtg-status-card`，有**兩個**測試檔引用舊 id，第二個是跑全套才發現的。等高不寫死 `min-height`，改由 grid `stretch` 加卡片 flex column 與 `lf-run-actions` 貼底達成，內容增減不必回頭調數字 |
| E AI 跟隨取數、移除 `AiEnabled` | Claude（agy 五小時額度歸零） | 通過 | 全套 3633 綠（+4）；`aiEnabled`／`AiEnabled` 在程式碼與前端零命中；BOM 三個原本就有 BOM 的檔維持原樣、其餘無 BOM、無 NUL；突變即時觸發訂閱→3 紅 | 移除類改動的兩個漏網點都被抓到：(1) `runs.js` 有**兩處**消費 `aiEnabled`，第一輪只改了狀態卡那處，排程設定表單載入那處是靠事後 grep 清零才發現；(2) `RunsPageUiTests` 有一條守住舊契約的測試（斷言畫面含「AI 分析排程未啟用」），本輪推翻該行為後它變紅——改寫成守住新契約（開關與文案必須整組消失、閒置說明仍在）。即時觸發刻意不看 AI 執行窗口：跟隨取數是「取數跑到哪、判讀跟到哪」，窗口只管背景消化積壓，否則手動執行一趟還要等窗口開了 AI 才動 |
| D 守門自動偵測走 PRTG 即時查詢 | Claude（agy 五小時額度歸零） | 通過 | 全套 3629 綠（+3）；判定邏輯單一入口（`FindDevicesForHost` 三處引用皆同一份）；夜間批次仍讀鏡像（`PrtgMirrorGuardSource` 一處）；BOM 五檔與 dev 一致、無 NUL；突變即時查詢判定→1 紅 | `source` 新增兩個值 `live` 與 `mirror-fallback`。既有「留空時為 auto」的測試仍成立，因為它的 `PrtgUrl` 為空、在到達新分支前就提早返回——另補一條涵蓋新行為的測試，避免既有測試的綠燈被誤讀成新路徑有覆蓋 |
| C 同步結構與對應背景工作 | Claude（agy 五小時額度歸零） | 通過 | 全套 3626 綠（+7）；BOM 十三檔與 dev 一致、無 NUL；突變「結構同步零裝置的失敗判定」→1 紅、突變「跳過重複結構同步」→1 紅 | 測試抓到兩個規劃時沒想到的事實：(1) 結構同步的分頁失敗**不擲例外**，只累加 `Failures`，原本的成功判定會把「PRTG 整台連不上」當成一次成功同步（裝置 0、感測器 0）並把整份對應洗空——補上「有失敗且零裝置就不做對應」的閘門，有失敗但拿到裝置則照做對應但不報成功；(2) 「跳過重複結構同步」一開始沒有任何測試守得住，後來找到可觀測點（結構同步第一階段會印「開始同步 PRTG 裝置結構鏡像」，跳過時完全不進那三個階段），並補了一條對照組測試避免「沒有訊息」只是因為整段沒跑到 |
| B3 取數收尾與完工訊號 | Claude（agy 五小時額度歸零） | 通過 | 全套 3619 綠（+5）；警告字串只有一處；`PrtgValueFetchScope.cs` 零改動；BOM 五檔與 dev 一致、無 NUL；突變單輪收工條件→2 紅 | 自寫的測試先踩到規格自己強調的坑：`whitelist` 傳 null 時 `all-mapped` 會被 `EffectiveScope` 退回 `triggered`，量到的不是目標行為，且輪詢無限跑把測試 host 掛住。改為傳非空白名單，並把 `CancellationToken.None` 換成 15 秒取消權杖當偵測器——單輪收工失效時測試乾脆失敗而不是掛死 |
| B1-step2 DNS 解析器＋對應與守門改用 | agy（正式碼）＋Claude（測試） | 通過 | 全套 3614 綠（+13）；Core 內 DNS 只剩解析器一處；守門殘留方法 0、名稱退路仍在；BOM 八檔與 dev 一致、無 NUL；突變解析器使用點與快取寫入→3 紅 | agy 在改完正式碼後被自己的 subagent rate limit 截斷（exit 0 但測試檔一處未改、新測試檔未建、驗收未跑，30 個呼叫點編譯不過）。查額度：Claude/GPT 組五小時窗口 0%、週 48%，依使用者指示改 Claude 自做測試部分。正式碼品質經檢視符合規格（含保留 step1 補的名稱退路），予以保留 |
| B1-step1 純語法正規化層 | agy claude-opus-4-6-thinking | 通過（含 Claude 小修） | 全套 3601 綠（基線 3586，+15）；BOM 與 dev 一致、無 NUL；突變 port 拆解→2 紅、突變守門名稱 fallback→1 紅 | agy 交出時有 1 紅並宣稱「B2 再修」。實為**規格漏洞**：`NormalizeIp` 語意收緊打斷了守門「device 與 Sentinel 都填同一個 DNS 名稱」的字面比對能力，而 DNS 解不到的內網名稱在 B2 也救不回。Claude 小修：`PrtgAddress` 抽出 `HostToken`，守門 `FindDevicesForHost` 在前兩段都落空時加一段名稱字面比對。**契約補充**：守門的比對鍵優先序為「IP → DNS 解析出的 IP → 主機名稱字面」；對應（`MapForDate`）不走第三段（主機側只有 IP，名稱比對無意義），維持「解析不到就略過（無 IP）」 |

## 5. 體檢交接

（實作完成後填：測試總數、全綠與否、與基線 3586 的差）
