# 回饋第 48 輪規劃：PRTG 取數縮圈到有對應的主機＋第 46 輪待辦收尾

> 狀態：實作完成，待換模型體檢（`feature/feedback-48`）
> 基準：dev@413a0af（4807 綠／略過 10）
> 來源：使用者回饋——主機只有 20 台，「立即執行」回望 30 天卻要翻 PRTG 約 100 萬筆狀態變更；
> 附實機環境探測輸出（6388 device／42998 sensor、type 分布 33 種、9d-4 顯示 treesize 封頂在 1000000）。
> 同時收掉第 46 輪「待使用者提供資料才做」的項目。

## 作業總覽

- **委派模型**：agy（Gemini）；額度用完改派 `impl-low`（Opus low），切換點記在「執行紀錄」且不換回。
- **核對用 subagent**：`scan-low`（Opus low）。規劃、定案逐條比對、驗收由 Claude 親做。
- 規格檔一段一檔放 scratchpad，不讓執行端看整份規劃。委派期間 Claude 不動 repo。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 取數範圍集合單一出口 `PrtgScopeDevices`；「數值目標集合」三處建法收斂 | 中 | 無 |
| B | 結構同步與狀態變更縮圈：階段重排、sensors 只同步範圍內、範圍外列清除、messages 逐 device 取、移除 `id=0` 取法 | 大 | A |
| C | 數值快照改 `filter_objid` 分批；白天新進範圍的 device 補抓 sensors | 中 | A、B（共用逐 device 取 sensors） |
| D | 環境探測：新增 9d-5（逐物件 messages 驗證）、步驟 8 messages 診斷改用單一 device | 小 | 無（可最先做，先推 dev 讓使用者跑） |
| E | 內建 sensor type 分類對照補齊（第 46 輪待辦）；prompt 已抑制守門確認 | 小 | 無 |
| F | 文件同步（PRTG-SPEC／BACKLOG／README 升級注意） | 小 | 全部 |

建議順序：**D → A → B → C → E → F**。D 先做完就可推 dev 讓使用者跑一次探測；B 的取法照「逐 device」實作，
9d-5 結果若顯示不含下層 sensor，只需改 B 定案 6 的單一切換點（見該條）。

---

## 批次A：取數範圍集合單一出口

### 現況與核對結果

- 範圍設定（`PrtgValueFetchScope`）只管「數值」；devices／sensors／messages／快照四條路徑沒有範圍概念
  （`PrtgFetchService.cs:272-357,381-470,1034-1048`、`PrtgSnapshotHostedService.cs:287`）。
- 「有對應的 device 集合」沒有共用方法，六處手寫、回看天數各不相同：`PrtgTriggeredValueFetcher.cs:66-80`、
  `PrtgDailyPipeline.cs:223-259`、`PrtgBackfillRunner.cs:143-148,183-187`、`PrtgSnapshotHostedService.cs:429-445`、
  `SettingsController.cs:316-344`、`CalibrationService.cs:416-418,704-706`。BACKLOG 已登記其中三處
  （快照／估算／校準）顆數對不上。
- 資源守門夜間自動偵測讀鏡像（`PrtgResourceGuard.cs:111`、`SettingsController.cs:476,484`）：
  以 Sentinel／PRTG 位址比對 device，PRTG 位址對不到時退回「鏡像中 type 含 corehealth 的 sensor 所在 device」
  （`PrtgResourceGuardTargets.cs:163-182`）。實機探測顯示 Core Health 所在 device 不是 IPv4（0/1），
  **這個環境很可能就是走 fallback**——縮圈不能把它弄丟。

### 定案

1. 新增 Core 靜態類別／服務 `PrtgScopeDevices`（名稱暫定），回傳**取數範圍 device objid 集合 S**：
   - 最新一日主機對應（`GetLatestHostMap()`）中 `MapStatus` 為 `ok` 的 `DeviceObjid`；
   - ∪ `conflict` 列中**位址命中啟用中主機主檔 IP 的**那些（兩種衝突都算：一 IP 多主機本來就帶 `HostId`；
     同 IP 多 device 的 `HostId` 為 null，要用與 mapper 同一套位址正規化去比主機 IP）。
     **不能整批收 conflict**：mapper 對「同 IP 多 device」不看主機主檔就標 conflict（`PrtgHostMapper.cs:164-182`），
     6388 台裡重複 IP 的群組與主機無關的可能有數百台，整批收等於縮圈破功。
     收 conflict 的理由（更正討論時的說法——衝突頁本身不顯示 sensor）：一 IP 多主機的列帶 `HostId`、會被歸戶；
     同 IP 多 device 的列一經指派就立刻是 ok，資料已在鏡像裡不必等補抓；
   - ∪ 人工對應表全部 `DeviceObjid`；
   - ∪ 資源守門裝置：沿用 `PrtgResourceGuardTargets` 既有的位址比對（Sentinel host、PRTG host，含 DNS 預算），
     **不論守門是否啟用都算**（設定頁的預覽在啟用前就要讀得到鏡像）；
   - ∪ 鏡像中現存 corehealth sensor 的 `DeviceObjid`（保住 fallback；升級時既有全站鏡像裡有它，之後因所在 device 在 S 內而持續更新）。
   - `unmatched`、無 IP、IP 排除、人工兄弟略過的 device **不含**。
   - 不選「回看 N 天對應的聯集」：對應只對最新一日重算，歷史日的對應列不保證存在；回望多日取數以「現在有對應的 device」為準。
2. 位址比對邏輯**只有一份**：從 `PrtgResourceGuardTargets` 抽出「位址 → device」那段供兩邊共用，不複製。
3. S 為空是合法狀態（尚未對應到任何主機、守門位址也對不到）：回空集合，由消費端各自決定（B、C 定案有寫）。
4. 「數值目標集合」（哪些 sensor 要取數值／進快照）收斂成一個建法：快照（`PrtgSnapshotHostedService.cs:425-456`）、
   取數估算（`SettingsController.cs:316-344`）、校準兩處（`CalibrationService.cs:416,704`）共用；
   口徑＝**當天 `ok` 對應 device（無則最新一日）＋白名單＋未暫停**（即快照現行口徑，SPEC §3b:228）。
   夜間／觸發式／回填三處是「逐日主機歸戶」，語意不同，**本輪不動**。

### 改動

1. 新增 `PrtgScopeDevices`＋單元測試。
2. `PrtgResourceGuardTargets` 抽出位址比對供共用（行為不變，既有守門測試全綠為準）。
3. 新增「數值目標集合」共用建法；估算與校準兩處改用（快照那處在批次 C 一起改）。

### 測試／驗收

- S 的組成：種 ok／一 IP 多主機 conflict／同 IP 雙 device 且 IP 命中主機的 conflict／**同 IP 雙 device 但 IP 不在主機主檔的 conflict**／
  unmatched／人工／IP 排除各一組＋一台位址等於 Sentinel host 的 device＋一顆 corehealth sensor →
  S 恰不含「IP 不在主機主檔的 conflict」、unmatched、IP 排除（突變：conflict 不比主機 IP 整批收、拿掉 corehealth 分支各須紅）。
- 無任何對應列、無守門位址 → 回空集合不擲例外。
- 估算端點與校準的顆數對同一份種子資料**相等**（這就是 BACKLOG 那條「對不上」的反例）。
- grep：`GetLatestHostMap` 的直接呼叫端不再出現在快照／估算／校準三處。

---

## 批次B：結構同步與狀態變更縮圈

### 現況與核對結果

- `FetchDayAsync`（`PrtgFetchService.cs:80-246`）順序＝階段 1 devices 全站 → 階段 2 sensors 全站（42998 筆，只 upsert 不刪）→
  階段 3 messages `id=0` 全站 → 階段 4 數值。主機對應在它**之後**才由呼叫端執行
  （`PrtgDailyPipeline.cs:143-149`、`PrtgStructureSyncRunner.cs:87-91`）。回填傳 `syncStructure:false` 且不做對應。
- messages：`filter_drel` 只有 7days／30days／12months 級距；實機 treesize 封頂 1000000，回望 30 天等於翻 200 頁以上；
  探測步驟 8 連 `count=5` 都 60 秒逾時。`columns` 要了 `parent` 但沒讀沒存（`:383,403-412`）。
- 規則評估母體 `GetSensorStatuses()` 讀全表（`PrtgDailyPipeline.cs:204`、`CalibrationService.cs:1433`）；
  只 upsert 不刪代表縮圈後範圍外的列**狀態會永久凍結**，仍被拿去評估。
- **既有缺陷（複檢時發現）**：devices 與 sensors 鏡像**從不刪列**（`EfPrtgStore.cs:60-160` 只有 upsert，全檔無對這兩表的刪除）。
  PRTG 端刪掉的 device／sensor 永遠留在鏡像、狀態凍結：已刪 sensor 若最後狀態是 Down 會一直被規則評估成長期 Down；
  已刪 device 仍會對到主機。全站抓時這只是髒資料；改成逐 device 查詢後，**已刪 device 每晚必查詢失敗**，
  還會讓定案 4 的清除前置條件（階段零失敗）永遠不成立——必須一併處理。
- 主機對應**不只在同步時重算**：`PrtgHostMapRefresher.TryRefreshToday()`（`LogForesight.Web/Services/PrtgHostMapRefresher.cs`）
  在人工對應變更、IP 排除變更、主機主檔變更（新增／改 IP／停用／合併，含 NetIQ 主機同步）時都會重算今天的對應，
  跑在 HTTP 請求執行緒上、不打 PRTG。縮圈後，這條路徑新進 S 的 device 在鏡像裡沒有 sensor（見批次 C 定案 5）。
- `FetchDayAsync` 測試呼叫點 68 處（`PrtgFetchServiceTests.cs`），替身以 `content=` 路由、messages 期待 `id=0`。
- `id=<device objid>` 查 messages 是否含下層 sensor 的訊息：**未實測**（PRTG 裝置頁的 Log 頁籤行為如此，但沒有本環境證據）。
  使用者定案「先做，不行再改」。

### 定案

1. **devices 維持全站同步**（主機對應要用全部 device 的 IP；6388 筆成本低）。
2. **階段重排**：devices → 主機對應 → 算 S → sensors（範圍內）→ messages（範圍內）。
   `FetchDayAsync` 新增**必填**的範圍提供者參數（暫定 `Func<CancellationToken, IReadOnlyCollection<long>>`），
   在階段 1 之後、階段 2 之前呼叫一次；`syncStructure:false` 時在階段 3 之前呼叫一次。
   - 夜間與「同步結構與對應」：提供者內先跑 `PrtgHostMapper.MapForDate` 再回 S；既有保護照舊——
     **階段 1 有失敗且裝置數為 0 時不重算對應**（避免洗成一堆 unmatched），此時 S 取自既有對應。
   - 夜間「沿用手動同步剛更新的鏡像」（`skipStructureSync`）：仍重算對應、仍算 S，只是不重抓 sensors。
   - 回填：不重算對應，S 取自既有最新對應。
   - **必填而非可選**：可選參數會讓漏接的呼叫端靜默退回全站（本專案「可選相依」已犯五次）。
3. **sensors 只同步 S 內 device**：逐 device 以 `content=sensors&id=<device objid>` 取（沿用分頁器與 `PrtgFetchConcurrency` 併發）。
   |S| 超過門檻（**暫定 500 台**）時改回全站分頁抓、寫入前濾成 S——兩種取法寫進鏡像的內容必須相同。
   單台失敗隔離：其餘照做，失敗台數計入該階段失敗並在輸出列出台數。
4. **過期列清除（單一判準：本趟沒被刷新到的列）**：
   - sensors：階段 2 結束後刪除 `synced_at` 早於本趟起點的 sensor 列（分批刪，兩後端皆可）。
     一條判準同時收掉「範圍外」與「PRTG 端已刪除」兩種列，不另寫 `DeviceObjid ∉ S` 的第二份判定。
   - devices：階段 1 結束後刪除 `synced_at` 早於本趟起點的 device 列，**在重算對應之前**做（已刪 device 才不會再對到主機、不會進 S）。
     額外保險：本次會刪掉的台數超過既有台數的一半（暫定）就不刪並出聲——PRTG API 帳號權限被縮小時回傳會合法地少一大塊，
     那不是「device 被刪了」。人工對應表不受影響（`lf_prtg_manual_map` 不清）。
   - 破壞性操作，前置條件**全部成立**才執行：該階段零失敗且已收斂、寫入數大於 0；sensors 另加 S 非空、本趟對應沒有失敗（或本趟本來就不重算對應）。
   - 長得像「該刪」的合法情況：對應暫時失敗、PRTG 失聯只抓到半套 devices、主機主檔暫時讀不到——都被前置條件擋下，列進驗收反例。
   - 人工分類（`category_source=manual`）的列一併刪：逐顆人工分類 UI 尚未存在（BACKLOG），目前只有匯入會產生；裝置離開範圍即不保留。
   - 升級後第一趟會刪約 4 萬列，屬預期；執行輸出印刪除列數。
   - 數值表／快照表／狀態變更表中範圍外 sensor 的舊列**不主動刪**，交給保留期（使用者定案）。
5. **規則評估母體**隨鏡像自然縮小，不另寫過濾（BACKLOG「先過濾未對應感測器」隨之結案）。
   `GetStateChangeCoverageSummary`（校準涵蓋摘要）改為只計鏡像中存在的 sensor，避免保留期內新舊口徑混算（暫定做法，執行端可依查詢成本改為等價寫法）。
6. **messages 逐物件取**：對 S 內每台 device 發 `content=messages&id=<objid>[&filter_drel=…]`，
   級距選法、用戶端日期過濾、依時間提早停止、列鍵 `objid|datetime`、DB 去重鍵全部沿用。
   - 「要查哪些物件 id」集中在**單一方法**（暫定 `StateChangeQueryObjects`）——目前回 device objid；
     9d-5 若證實不含下層 sensor，只改這一處回傳 S 內未暫停 sensor 的 objid，其餘不動。
   - 併發用 `PrtgFetchConcurrency`（預設 2）；單台失敗隔離，任一台失敗則該階段計失敗，輸出列失敗台數與前 5 台的名稱／objid
     （管理者要能直接去 PRTG 查那一台），已寫入的保留（冪等）。
   - 每台查詢前過資源守門（`_guard.WaitIfBusyAsync`，與階段 4 同一個接點）：逐台查詢對 PRTG 的壓力型態接近 historicdata。
   - 逐台的分頁器進度行（「/ 約 N 筆」）不逐台印——500 台會把執行紀錄洗掉；只印定案 7 的彙總與失敗明細。
   - 已知限制：分頁器固定 `sortby=objid`，一台 device 多顆 sensor 時頁內時間不是遞減，「依時間提早停止」多半不會觸發；
     每台的量小（`filter_drel` 窗內數百到數千筆），不另改排序。
   - 每台頁數上限沿用分頁器既有保險絲；**不另加整段逾時**（縮圈後每台數頁內結束）。
   - S 為空：階段 2、3 略過並印一行說明，不計失敗。
   - `id=0` 取法**整個移除**，不留退回路徑：實機上 `id=0` 連 `count=5` 都會 60 秒逾時，它不是可靠的退路。
     逐台成本未實測（9d-5 會量）；以每台 0.3～1 秒、預設併發 2 估，20 台數秒、500 台 1～4 分鐘、6388 台 16～53 分鐘。
     彙總行印「平均每台耗時」，BACKLOG「messages 改推送」的觸發條件改用這個數字。
   - `columns` 拿掉沒用到的 `parent`。
7. **進度與可觀測**：階段 3 進度改「第 i／N 台」；結束印一行「查詢 N 台、取回 a 筆、區間內新增 b 筆、c 台無任何訊息」。
   **N 台全部 0 筆**時加印警告：可能是 `id=<device>` 不含下層感測器訊息，請跑環境探測看 9d-5——這是「先做，不行再改」的訊號線。
   取回列的 objid 不屬於該 device 在鏡像中的 sensor 時只計數印出、照常寫入（新增 sensor 尚未同步屬正常）。
8. 鏡像狀態卡的「感測器數」標示改為範圍內的語意（純文字調整）。

### 改動

1. `PrtgFetchService`：範圍提供者參數、階段重排、sensors 逐 device＋門檻退回、messages 逐物件、移除 `id=0` 與 `parent`。
2. `EfPrtgStore`：刪除範圍外 sensor 列的方法；涵蓋摘要口徑。
3. 三個呼叫端接上提供者：`PrtgDailyPipeline`（對應移進提供者，里程碑與失敗訊息照舊）、`PrtgStructureSyncRunner`、`PrtgBackfillRunner`。
4. 測試：`PrtgFetchServiceTests` 68 處呼叫點與替身路由機械性跟進（斷言不得刪，只換路由與參數）；
   `PrtgDailyPipelineTests`／`PrtgStructureSyncRunnerTests`／`PrtgBackfillRunnerTests` 跟進。
5. 前端：鏡像狀態卡文字；`wwwroot/js/core/run-phases.js:24` 狀態變更階段的單位「筆」改「台」
   （sensors 階段逐 device 取時的 done／total 語意也要與該檔的單位一致，執行端核對 `:22-24` 三行）。

委派拆段（暫定）：B-1 範圍提供者＋階段重排＋三呼叫端＋測試機械跟進（行為仍全站，先讓 68 處綠）→
B-2 devices 過期列清除（獨立於縮圈，先做先驗）→ B-3 sensors 縮圈＋清除 → B-4 messages 逐物件＋守門＋可觀測＋移除 `id=0`。

### 測試／驗收

- 替身記錄每個請求：S＝{A,B} 時，sensors 與 messages 請求的 `id` 集合恰為 {A,B}，**沒有任何 `id=0` 與無 `id` 的 sensors 請求**
  （突變：提供者回傳被忽略 → 紅）。grep：`id=0` 在 `PrtgFetchService.cs` 零命中。
- 順序：提供者在 devices 請求之後、第一個 sensors 請求之前被呼叫恰一次（用替身記錄呼叫序，不用時間）；`syncStructure:false` 時在第一個 messages 請求之前恰一次。
- sensors 清除：種範圍外 3 列＋範圍內 2 列＋範圍內但 PRTG 已不回傳的 1 列 → 成功趟後只剩 2 列。
  反例各一條必須**不刪**：S 空、階段 1 未收斂、階段 2 單台失敗、對應擲例外、階段 2 寫入 0 筆。
- devices 清除：鏡像 10 台、PRTG 回 9 台 → 剩 9 台且該台不再出現在重算後的對應；鏡像 10 台、PRTG 回 4 台 → **不刪**並輸出警告；階段 1 未收斂 → 不刪。
  順序斷言：device 清除發生在 mapper 被呼叫之前（替身記錄呼叫序）。
- 失敗明細：兩台逾時 → 輸出含這兩台的名稱與 objid。守門：注入忙碌的守門替身 → messages 請求在放行前為零。
- |S| 超過門檻走全站抓時，寫入鏡像的列與逐台抓相同（同一份替身資料兩種路徑比對）。
- messages：單台逾時 → 其餘台照寫、階段計失敗、輸出含失敗台數；全部 0 筆 → 輸出含 9d-5 提示字樣；S 空 → 零 messages 請求且 `Failures` 不增加。
- 既有 messages 測試（級距 Theory、提早停止四分支、同 sensor 多筆、重跑新增 0）改路由後斷言原樣全綠。
- 夜間：階段 1 失敗且裝置 0 → 不呼叫 mapper、S 來自既有對應（突變：照樣呼叫 mapper → 紅）。
- 全套測試綠。

---

## 批次C：數值快照縮圈

### 現況

`PrtgSnapshotHostedService.cs:287` 每 5～15 分鐘單發抓全站 42998 筆（實測 17.5 秒、13 MB），回來才在 `:342` 濾成目標。
`PrtgResourceGuardProbe.cs:117` 已有 `filter_objid` 每批 50 的取法。

### 定案

1. 目標 sensor 以 `filter_objid` 每批 50 查詢（沿用守門那份組查詢的寫法，不複製第二份）；
   目標顆數超過門檻（**暫定 2000 顆**）時維持現行單發全站＋treesize 比對。
2. 分批模式的「取不齊要出聲」：回傳顆數少於要求顆數時，每輪彙總印一行「要求 x、取回 y、缺 z 顆」（sensor 在 PRTG 端被刪或改 id，下次結構同步後自癒），不逐批洗版。
3. 單批失敗：該批略過、其餘照寫，彙總行帶失敗批數；全部失敗才算本輪失敗（沿用現行失敗處理）。
4. 目標集合改用批次 A 的共用建法；目標為空時本輪不發任何請求。
5. **範圍補抓（白天新進範圍的 device）**：主機新增／改 IP、人工對應、IP 排除變更都會當場重算對應（見批次 B 現況），
   縮圈前 sensor 早就在鏡像裡，對應一成立主機詳情的 PRTG 頁籤、未回報提示、觸發式取數立刻可用；
   縮圈後這些 device 要等到下一次同步才有 sensor——這是退化，必須補上。
   - 做法：快照服務每輪檢查時，找出「在 S 內、鏡像裡一顆 sensor 都沒有」的 device，逐台以 `content=sensors&id=<device>` 補抓並 upsert
     （與結構同步同一個取法與 mapper，不寫第二份）。**不放在重算對應的請求執行緒上**：那條路徑刻意不打 PRTG，
     NetIQ 主機同步一次可能新增上百台。
   - 每輪最多補 50 台（暫定），其餘留給下一輪或下一次同步；取數／同步／回填／探測任一在跑時本輪不補（沿用既有互斥閘門）。
   - PRTG 上真的 0 顆 sensor 的 device 會每輪都被當成待補：補抓過且回傳 0 筆的 device 記在記憶體，直到下一次結構同步完成或服務重啟才再試。
   - 補抓只補 sensors，不補 messages：規則評估只在夜間／立即執行時發生，那時會先做完整的範圍同步。
   - 補上之前，未回報提示對這台顯示「無法判定」（`PrtgPresenceHint.Classify` 對空清單回 unknown，現況即如此）；
     主機詳情 PRTG 頁籤的空狀態文字改為說明「感測器清單將在數分鐘內自動補上」（純文字）。
   - 補抓寫入的列 `synced_at` 是當下時間，不會被同時間開始的同步誤刪（清除判準是早於該趟起點）。

### 測試／驗收

- 補抓：S 內兩台、其中一台鏡像無 sensor → 恰對那一台發一個 sensors 請求並寫入；回 0 筆的 device 下一輪不再發請求；
  互斥閘門忙碌時零請求；待補 60 台 → 本輪恰 50 個請求。

- 目標 120 顆 → 恰 3 個請求、每個都帶 `filter_objid`、無 `count=50000` 請求（突變：門檻判斷反向 → 紅）。
- 目標 2001 顆 → 單發全站且 treesize 比對仍在。目標 0 顆 → 零請求。
- 缺顆彙總行：要求 50 回 48 → 輸出含「缺 2」。

---

## 批次D：環境探測

### 定案

1. **新增 9d-5「逐物件查詢驗證」**：探測只拿得到 `PrtgClient`（`PrtgProbeRunner.RunAsync(client, console)`，沒有 store），
   所以從探測自己抓到的資料挑最多 3 台 device（優先挑底下有非 Up sensor 的，比較可能有訊息）。每台依序：
   (a) `content=sensors&id=<device>&columns=objid` —— 驗證批次 B／C 要用的「逐 device 取 sensors」在本機可用，並取得該台的 sensor objid；
   (b) `content=messages&id=<device>&filter_drel=7days&count=50&columns=objid,datetime` —— 印耗時、列數、treesize、不重複 objid 數；
   (c) 以 (a) 的 objid 判定三態：
   「✓ 含下層感測器訊息」／「✗ 只有裝置自身——取數需改為逐感測器」／「無資料，無法判定」。
   另對其中一顆 sensor 發 `id=<sensor>` 同查詢印耗時，供改逐 sensor 時估算；
   再以 (a) 取得的 objid（最多 50 顆）發一次 `content=sensors&filter_objid=…&columns=objid,lastvalue_raw` 印耗時與回傳顆數，驗證批次 C 的分批取法。
2. **步驟 8 的 messages 分頁語意診斷**改用 9d-5 挑到的第一台 device（`id=0` 在本環境必逾時，永遠「無法判定」）；挑不到 device 時印說明略過。
3. 9d-4 保留（記錄 `id=0` 的 treesize 封頂事實）。成本行（「本步驟會發 N 次…」）的次數跟著更新。

### 測試／驗收

- 三態各一條測試（替身回下層 sensor objid／只回 device objid／空陣列）；突變：判定反向 → 紅。
- 步驟 8 的 messages 請求不再含 `id=0`；S 空且全站無 device 時不擲例外。

---

## 批次E：內建分類對照與 prompt 守門

### 現況

- `PrtgSensorTypeCategoryMap`（`PrtgConstants.cs:111-125`）無任何 hardware 條目；type 清單本輪到位。
- 第 46 輪待議「AI prompt 事件清單排除已抑制」：核對發現**已在第 47 輪批次 B-1 完成**
  （`AnalysisPromptBuilder.cs:54,208-209`，commit fbc34cf），本輪只確認有守門測試。

### 定案

1. 內建對照新增：
   - `hardware`：`SNMP Cisco System Health`
   - `availability`：`Port`、`HTTP`、`SNTP`、`DNS (DEPRECATED)`、`FTP`、`RDP (Remote Desktop)`、`Cisco IP SLA`
2. **`SNMP Linux Load Average` 不歸 cpu**（推翻討論時的提案）：資源守門自動偵測會收 cpu 分類的 sensor 並以百分比門檻（預設 85）比對，
   load average 不是百分比，歸進去會讓守門誤判。維持未分類，需要時由補充對照指定。
3. 維持未分類：`SNMP Custom*`、`SNMP Library`、`SSH Script`、`EXE/Script Advanced`、`Sensor Factory`、
   `System／Probe／Core Health`（PRTG 自身健康，守門在用）、`SNMP System Uptime`／`SNMP Uptime v2`。
4. 行為變更（寫進 README 升級注意）：下次結構同步後，範圍內主機的 Port／HTTP 等 sensor 開始適用 availability 規則，可能出現新的 PRTG 問題。
   hardware 規則與儲存故障佐證在目前環境**多半仍不會命中**——Cisco System Health 掛在網路設備上，NetIQ 主機清單是伺服器。
5. prompt：確認事件清單（已知問題段與其他段）排除已抑制各有一條測試；缺就補，不改正式碼。

### 測試／驗收

- 對照表逐條斷言（含不分大小寫）；`SNMP Linux Load Average` 解析為 null 的反例一條。
- 守門自動偵測既有測試全綠（分類表變動會連動守門，SPEC §7）。

---

## 批次F：文件

- `PRTG-SPEC.md`：§2（內建對照現況）、§3（階段順序、範圍集合、sensors／messages 取法、清除前置條件、進度與輸出）、
  §3b（快照分批與門檻）、§5／§5a（回填與手動同步的範圍來源）、§6（9d-5、步驟 8）、§11（校準口徑為範圍內）、§12（守門與範圍集合的關係）。
- `BACKLOG.md`：移除「內建 hardware 分類對照條目」「快照目標集合三處建法收斂」；「結構同步的取數縮圈」只留 (c)；
  「messages 改推送」觸發條件改寫為縮圈後的實測；新增「全新安裝且 PRTG 位址對不到 device 時，夜間守門自動偵測找不到 corehealth（鏡像不含範圍外 sensor）——
  需在維護頁用即時來源偵測一次並存入覆寫清單」。校準批次 E 維持原條目。
- `PRTG-SPEC.md` 另補：鏡像過期列清除規則與保險（§1 或 §3）；範圍補抓（§3b）；資料搬運（匯入的範圍外 sensor 會在下一次同步被清掉，
  匯入端的主機清單決定留下什麼）；§11 校準註明「分佈母體＝範圍內主機的 sensor」——全站分佈由上萬顆交換器流量 sensor 主導，
  拿來定伺服器規則的門檻反而失準，但樣本數會小很多，判讀「可用」時要看樣本數。
- `README.md` 升級注意：第一趟同步會清掉約 4 萬列範圍外 sensor；校準與鏡像摘要數字改為範圍內；availability 規則涵蓋的 type 變多。
- 現行文件只寫現況，不留輪次敘事。

## 明確不做（本輪定案）

- **一次性刪除範圍外的狀態變更／數值舊列**：交給保留期（使用者定案）。
- **`FetchDayAsync` 階段 4 的全量分支**（`PrtgBackfillRunner.cs:123` 才走得到）：縮圈後它的對象已是範圍內 sensor，不再是「全站」，不移除。
- **`id=0` 全量翻的退回路徑**：不保留，理由見批次 B 定案 6。
- **夜間整段逾時／夜間停止鈕**：縮圈後風險已降，不加。
- **維護頁即時守門偵測（`PrtgLiveGuardSource`）的全站單發**：手動、低頻，且全新安裝時靠它找 corehealth，維持。
- **逐日主機歸戶三處（夜間／觸發式／回填）的建法收斂**：語意與「範圍集合」不同，不併。
- **新增範圍相關設定鍵**：門檻（500 台、2000 顆）寫成常數；沒有調整需求前不做成設定（「有設定無行為」紅線的反面：沒有需求不加設定）。
- **校準結果（第 46 輪批次 E）**：本輪未取得資料，留 BACKLOG。
- **Probe 吞 `OperationCanceledException` 等 BACKLOG 既有未修項**：不在本輪。

## 升級注意

- 無 schema 變更。升級後第一次夜間或手動同步：重算對應 → 清除範圍外 sensor 列 → messages 只抓範圍內。
- 鏡像狀態的感測器數、校準各卡、匯出筆數會大幅下降，屬預期。
- 尚未對應到任何主機的站台：階段 2、3 會略過；先到 PRTG 維護頁完成對應。
- 第一趟同步同時會清掉 PRTG 端早已刪除的 device／sensor；原本掛在這些 sensor 上的「長期 Down」類問題之後不再出現。
- 退版：回到舊版後下一次同步會把全站 sensors 重新抓回來，無需手動處理。

## 規劃完成後複檢

- **與既有設計衝突**：
  ① SPEC §3「結構鏡像＝PRTG 全站現況」被本輪推翻為「devices 全站、sensors 範圍內」，批次 F 改寫；
  ② 第 46 輪「規則母體脫離取數白名單」不受影響（母體仍不看白名單，只是縮到範圍內 device）；
  ③ 第 44 輪「回望連動 PRTG、狀態變更整趟只取一次」保留——仍是一趟一次，只是改成逐台；
  ④ 守門 corehealth fallback 與縮圈衝突 → A 定案 1 第四項＋BACKLOG 新條目；
  ⑤ 分類表變動連動守門 → 因此撤回 Load Average→cpu。
- **批次間**：A 的共用建法由 C 消費（快照）；B-1 先只接參數不改行為，讓 68 處測試在縮圈前先綠；D 獨立可先行。
- **四個坑**：「什麼算一個」——S 的組成與空集合行為已寫；破壞性判準——清除的四個前置條件與反例已寫；
  單向閘門——無；移除類——`id=0` 的依賴方（探測五處中步驟 8 改、9d-4 保留並註明）、`parent` 欄無消費端（已核對）。
- **待驗證的技術事實**（未寫成硬契約）：`id=<device>` 是否含下層訊息（9d-5 與 B 定案 7 的警告線把關）；
  `content=sensors&id=<device>` 為 PRTG 標準用法，B-2 實作時以替身為準、實機由使用者第一趟同步輸出確認。
- 複檢新增事項：上列 ④⑤ 兩項已回寫定案；其餘無新增。

### 第二輪複檢（整體／程式／使用者／管理者四角度＋尖銳質疑對照）

逐角色走一遍操作，再回頭核對程式碼。**抓到四個會出事的缺口，皆已回寫定案**：

| # | 角度 | 缺口 | 證據 | 處置 |
|---|---|---|---|---|
| 1 | 程式 | 「S 含 conflict」會把與主機無關的重複 IP device 整批收進來（mapper 對同 IP 多 device 不看主機主檔就標 conflict） | `PrtgHostMapper.cs:164-182`、`SettingsController.cs:731-746`（候選主機可為空） | A 定案 1：conflict 要位址命中主機主檔才收 |
| 2 | 程式／管理者 | 鏡像從不刪列；已刪 device 改成逐台查詢後每晚失敗，且讓清除前置條件永遠不成立 | `EfPrtgStore.cs` 對 devices／sensors 無任何刪除 | B 定案 4：以 `synced_at` 為單一判準清過期列，devices 加過半保險 |
| 3 | 使用者／管理者 | 對應在主機異動、人工對應、IP 排除時當場重算（規劃原先誤以為只在同步時算）；縮圈後新進範圍的 device 沒有 sensor，主機詳情頁籤、未回報提示、觸發式取數要等到隔天 | `PrtgHostMapRefresher.cs`、`HostAdminService.cs:91-119` | C 定案 5：快照服務背景補抓 |
| 4 | 程式 | 探測拿不到 store，原寫「優先挑 S 內 device」做不到 | `PrtgProbeRunner.cs:13` | D 定案 1 改為從探測自身資料挑，並順便驗證逐 device 取 sensors 與 `filter_objid` |

尖銳質疑對照（預期會被問的，與規劃的回答）：

- 「逐台查 messages 你根本沒測過，憑什麼整個拔掉 `id=0`？」——`id=0` 在實機連 `count=5` 都逾時，它本來就不是能用的退路；
  逐台取法有 9d-5 實機驗證、全部 0 筆的警告線、單一切換點三道保險。
- 「門檻 500 台、2000 顆、50 台、過半不刪，數字哪來的？」——全部標暫定，來源是探測 9a／9d-1 的實測耗時換算；寫成常數不做設定，有實測再調。
- 「清 4 萬列，刪錯怎麼辦？」——sensors 與 devices 都是 PRTG 的鏡像，下一次同步即可重建；唯一不可重建的人工對應表不在清除範圍。
- 「校準樣本從 4 萬顆變 140 顆，校準還有意義嗎？」——規則只作用在範圍內主機，門檻該由這群主機的分佈決定；樣本數變小是事實，寫進 §11 提醒判讀。
- 「每晚某一台慢就整個階段計失敗，是不是變吵？」——是，但輸出指名是哪幾台，其餘台照常落地；靜默吞掉才是本專案一再踩的坑。
- 「hardware 規則補了對照還是不會中，做它幹嘛？」——對照表是環境事實，成本是九行；不會中的原因（硬體 sensor 不在伺服器上）寫進文件，不假裝有效。

上帝視角盤點（本輪改動波及、已確認不需處理或已納入）：
主機清單未回報提示（只讀 ok 對應 device 的 sensor，範圍內）；主機詳情／儀表板 PRTG 表（`GetSensorsByDevice`，範圍內）；
觸發式取數與回填（目標來自鏡像，隨之縮小）；資源守門夜間偵測（Sentinel／PRTG device 在 S 內）；維護頁即時偵測（不走鏡像，不受影響）；
補充對照頁的「未分類 type」清單只剩範圍內出現的 type（合理）；匯出／匯入（F 已寫）；
依鏡像版本戳失效的快取——清除發生在同步內、同步結束本來就會換戳，B-3 驗收時 grep 確認；
「最後結構同步時間」取 sensors 的 `synced_at` 最大值（`EfPrtgStore.cs:621`）——S 非空時照常更新，S 空時沒有任何 ok 對應、提示一律是 no-map，不受影響；
進度軌的單位寫死在前端（`run-phases.js:24` 狀態變更＝「筆」）——B-4 要一併改成「台」，列入該段白名單與驗收（B 改動 5）。

## 設計修正（規格階段，回寫規劃）

| # | 批次 | 原定案 | 規格實際做法 | 理由 |
|---|---|---|---|---|
| 1 | A | 估算與校準改用共用建法、快照在 C 改 | A 一次把快照與估算改成與校準相同的 `GetLatestHostMapWithDate(30, 今天)`；校準本來就是這個口徑不動 | 三處差的只是「對應從哪一天取」，收斂成同一支既有查詢即可，不需要新的共用建法 |
| 2 | B | 感測器清除前置條件含「本趟對應沒有失敗」 | 拿掉這一條，保留 S 非空、階段 2 零失敗且已收斂、寫入數 > 0 | 對應失敗時 S 取自既有（上一次成功的）對應，照它清除是正確的；感測器鏡像是 PRTG 的複本，清錯也在下一趟同步重建。裝置清除另有「過半不刪」保險 |
| 3 | B | 進度軌只改狀態變更的單位 | 感測器階段的單位也改「台」；全站退路模式不再以列數當分子（改不定進度） | 逐台模式的分子是台數，同一個 phase 不能兩種單位 |
| 4 | C | 快照只改取法 | 另改 PRTG 維護頁策略說明（原寫「全量快照、單次 10～15 秒」）與「鏡像感測器數」標籤、主機詳情空感測器文字 | 寫規格時 grep 到的畫面文字，不改就是說謊 |
| 5 | B | FetchDayAsync 範圍提供者 `Func<CancellationToken, IReadOnlyCollection<long>>` | `Func<bool devicesRefreshed, PrtgScopeResult>` | 提供者要知道階段 1 是否成功才能決定重不重算對應；回傳計數供輸出 |
| 6 | B | 回填直接用範圍集合 | `PrtgBackfillRunner.RunAsync` 新增必填 `scopeDeviceObjids`，由 `PrtgBackfillService` 以 `PrtgScopeDevices.Compute` 算好傳入；狀態變更進度回呼語意改為台數 | 回填的觸發式路徑直接呼叫狀態變更取數、不經 FetchDayAsync |
| 7 | A | 範圍含守門裝置 | 另含守門**覆寫清單** sensor 所在裝置 | 覆寫的 sensor 若被清出鏡像，守門讀不到分類會靜默失效 |

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| D | agy（gemini-3.8-flash-high）→ impl-low（Opus low） | 通過（探測 43→57；全套 4821 綠／略過 10） | Claude 親驗：建置 0 錯 0 警告；驗收 grep 3～6 成立；兩檔無 BOM、CRLF、無 NUL；測試變更只收窄比對不弱化；impl-low 突變「(b) ✓ 判定恆 false」→ 2 紅，檔案備份還原 | **委派模型切換起點**：agy 約 9 分鐘結束、stdout 停在「等待建置完成」、未跑驗收、留 3 紅（本機規則「回報與 diff 不符」）→ 本段起改派 impl-low，不換回。3 紅根因皆在測試：規格 5.1／5.3 的「任何請求不含 `id=0&filter_drel=7days`」「不含 count=5 的 messages 請求」會誤中 9d-4 保留的查詢（規格錯誤），`count=5` 字串誤中 `count=50`。已知限制：9d-5 (a) 的預期集合來自步驟 3 的全站清單，清單被截斷時 (a) 可能誤判 ✗（步驟 3 已印截斷警告，不另處理） |
| A | impl-low（Opus low） | 通過（新增 6 條；全套 4827 綠／略過 10） | Claude 親驗：全套重跑、建置無警告、7 檔無 BOM／CRLF 一致；突變「conflict 整批收」「拿掉 corehealth 項」各 2 紅 | 快照改為回看 30 天的最近對應（原為今天退一天），與校準同口徑；組成測試需設 `PrtgUrl` 讓守門 fallback 不觸發，corehealth 項才測得到 |
| B-1 | impl-low | 通過（全套 4837 綠） | Claude 親驗：全套重跑、16 檔 CRLF／BOM 一致、斷言零刪改；突變「provider 移到階段 2 後」「手動同步裝置未更新仍對應」皆紅 | **規格錯誤一處（Claude 寫錯）**：夜間「裝置未更新就不重算對應」會讓 PRTG 連不上的那晚最新日沒有對應、finding 歸不了戶——鏡像在失敗時不會縮小，重算是安全的。回饋後改為夜間一律重算，執行端撤回為配合錯誤規格而改的兩個既有測試準備資料，新增強證據測試（最新日對應表確有該裝置 ok 列），突變 3 紅。手動同步維持原保護 |
| E | Claude 自做 | 通過（全套 4851 綠） | 分類對照逐條 Theory＋刻意不分類反例 Theory | `HTTP`／`dns` 補進對照後，三個既有測試原以它們當「不在對照表」的範例而紅，範例改用 `SNMP Custom`／`SSH Script`，斷言不動。prompt 事件清單排除已抑制：第 47 輪 B-1 已完成且 `IssueMuteTests.cs:239-241` 已涵蓋兩段，未新增 |
| B-2 | impl-low | 通過（全套 4860 綠） | 全套重跑；突變「拿掉過半保險」「清除移到範圍計算後」「放寬為不看收斂」皆紅 | 核對 `synced_at` 在 SQL Server 為 `datetime2`（`SchemaUpgrader.cs:778,816`），`<` 比較不會因精度捨入把整表判過期——這對沒有保險的感測器清除尤其關鍵 |
| B-3 | impl-low | 通過（全套 4873 綠） | 全套重跑、6 檔 CRLF；突變「逐台不帶 id」「清除不看逐台失敗」「全站模式不濾範圍」皆紅；併發程式 semaphore 取得在 try 之外 | 兩條既有斷言依規格改（進度單位改台數、範圍 null 不查 sensors）；補規格漏抄的定案 5（涵蓋摘要只計鏡像中感測器） |
| B-4 | impl-low | 通過（全套 4882 綠） | 全套重跑、9 檔 CRLF／BOM；突變「回到 id=0」21 紅、「提早停止狀態跨物件」「拿掉守門」各 1 紅 | 8 條斷言變更逐條有理由（id=0→id=裝置、輸出格式、範圍 null 不查 messages）；DTO 註解仍寫「筆數」（白名單外）由 Claude 補改 |
| C | impl-low＋Claude | 通過（全套 4899 綠） | 全套重跑、12 檔 CRLF／BOM；突變分批門檻反向 7 紅、不排除已確認為空 2 紅、補抓例外外拋 1 紅 | Claude 補兩處：(1) 探測 9d-5 自組 `filter_objid` 改用共用方法；(2) **補抓會推高感測器 `synced_at`，讓主機清單「PRTG 提示已過期」判斷被誤判為新**——`GetLatestStructureSyncedAt` 改取裝置表（只有結構同步會寫），連帶移除執行端「補抓後重讀基準」的權宜寫法並修 null→值 的清空條件；突變「改回感測器表」3 紅 |
| C-2（計畫外） | impl-low＋Claude | 通過（全套 4903 綠） | 突變「查到的裝置不併入」「不排除查不到的 objid」「覆寫裝置不優先」皆紅 | 寫文件時發現：全新安裝時守門覆寫清單的感測器所在裝置不在範圍、永遠進不了鏡像，守門讀不到分類而靜默失效。範圍補抓改為以 `filter_objid` 查出所在裝置並補抓，覆寫裝置排在 50 台上限之前（Claude 補改排序並加測試） |
| F | Claude | 完成 | 文件殘留全站敘述 grep 清零（`id=0` 只剩說明它為何不用、9d-4 的診斷） | PRTG-SPEC 新增 §3c 取數範圍；§2／§3／§3b／§4／§5／§5a／§6／§9／§10／§11／§12 同步；BACKLOG 移除兩條已完成、改寫三條、新增三條；README 升級注意；CLAUDE.md 基線、文件地圖、新增「不要讓 PRTG 取數對整台 PRTG 查」 |

## 體檢交接

- 全套：**4903 綠／略過 10（總計 4913）**，基線 4807 → +96。建置 0 錯誤；全量重建有一條基準既有警告 `CalibrationServiceTests.cs(1190,73) CS8629`（本輪未動該檔）。
- 委派：D 段起 agy 失敗改派 impl-low（Opus low），之後全程 impl-low；Claude 親做 E、F、C／C-2 的補修與全部驗收。
- 規劃外新增：C-2（守門覆寫清單補抓）、`GetLatestStructureSyncedAt` 改取裝置表。
- 體檢建議重點：
  1. 夜間 provider 的對應時機與 `BuildNightlySyncStatus` 的成功判定是否仍一致（對應移進 `FetchDayAsync` 內）。
  2. 感測器清除無保險：確認沒有任何路徑會讓範圍「錯誤地小但非空」（例如主機主檔暫時讀不到回空清單而不擲例外）。
  3. 快照服務每輪 `PrtgScopeDevices.Compute`（含守門位址 DNS）與 `GetAllSensors()` 的成本。
  4. `id=<裝置>` 是否含下層感測器訊息**未經實機驗證**：待使用者升級後跑探測 9d-5。
- 待使用者實測：探測 9d-5 結論、第一趟同步的清除列數與各階段耗時、主機清單未回報提示、守門是否仍偵測到 PRTG core server。
