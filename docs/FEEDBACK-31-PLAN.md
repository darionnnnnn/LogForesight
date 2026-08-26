# LogForesight 第三十一輪規劃：規則更新後的舊日重新分析

> 狀態：規劃中（待使用者定案後實作）
> 基準：dev@37ec709（2886 綠，略過 6）
> 來源：使用者需求——規則新增/修改後，能對過去約 30 天的資料以新規則重新分析；修改舊規則時舊問題要被清除、以新規則結果取代。

## 需求演進與方案定調

原始需求是「規則異動後，下次手動/自動排程自動補跑前 30 天」。核對後發現兩個關鍵事實改變了方案形狀：

1. **問題列（`lf_top_issues`）沒有 `rule_id` 欄**，RuleId 只存在 `content_json` JSON 內；且一天的風險等級/類別彙總/趨勢是全規則共同算出的——「只清除某條規則的問題」在資料層做不到也無法局部修補。→ 清除單位定為**主機日整日**：刪該日紀錄與子表、走既有分析路徑整日重建。新增與修改規則走同一條路。
2. **原始事件不存 DB**（本機即時掃 Windows Event Log、NetIQ 重查 Sentinel），重跑＝重新向來源取數。可補回多少天受來源保留量限制，不受 DB 保留期限制。

討論後使用者提出簡化：**不做規則異動自動追蹤，改為手動執行時由使用者主動選擇重跑**。採納，理由：
- 省掉「異動追蹤狀態 blob＋自動排程消費」整個機制；規則什麼時候改完、什麼時候該重跑，使用者最清楚。
- 自動排程維持現行「缺日回補」語意不變，不會在無人看管時做破壞性動作。
- 代價：規則改完若忘了手動重跑，舊日子就維持舊結果。接受（UI 於規則維護頁儲存成功後提示「如需回溯套用請至排程頁執行重新分析」補救，見批次 D）。

**未選方案（留檔備查）**：(a) 規則異動追蹤＋排程自動補跑——機制多一整套、且自動排程獲得破壞性行為，收益不成比例；(b) 逐規則局部清除——資料層做不到（無 rule_id 欄），加抽出欄＋回填的成本遠大於整日重跑。

### 執行模式定案（2026-08-26 第二次討論）

使用者定案：改成**下拉選單四個模式**，預設為安全模式，重跑範圍以「處理狀態」分級；選到破壞性模式時明確警示。

| # | 模式（下拉選單） | 重跑哪些既有主機日 | 破壞性 |
|---|---|---|---|
| 1 | 只補跑失敗或未執行（**預設**） | 不重跑既有日：缺日＋AiPending＋AI 未分析（批次 A 修好後才真正生效） | 無 |
| 2 | 重跑未處理的日子 | 該日**沒有任何**問題處理紀錄（缺列＝未處理） | 低 |
| 3 | 重跑未處理＋已指派未完成的日子 | 模式 2 ＋ 該日只有**非結案類**處理紀錄（in_progress／observing／escalated／open，含案件掛接的日子） | 中，警示 |
| 4 | 全部重跑（含已處理） | 窗口內全部既有日，**含已有結案類**（resolved／wont_fix／false_positive／known_noise）**紀錄的日子** | 高，強警示＋確認對話框 |

- 日層級判準（暫定契約，重跑單位仍是主機日、處理狀態是問題層級，映射規則）：
  模式 2＝該主機日在 `lf_issue_handling` **無任何列**；模式 3＝該主機日**無結案類**列（可有非結案類列）；模式 4＝不過濾。混合日（同日部分問題已結案、部分未處理）依此歸入模式 4 才會重跑——判準用「有無列」而非日層級推導狀態，確定性高且不依賴 DayHandlingDerivation 的推導細節。
- 模式 3/4 選取時顯示警示；模式 4 警示明講「**已處理的項目也會被刪除重建**（該日分析結果含 AI 結論、深度分析將刪除，以新規則重新產生）」。
- **處理紀錄本身不刪**（`lf_issue_handling`／歷程／案件）：鍵是 `(host, date, issue_key)`，重跑後同鍵問題自動接回既有結論。副作用：處理紀錄成為孤兒列的成因有**兩種**——(a) 新規則不再產生某問題；(b) **規則變更改變了 issue_key 本身**：Linux 規則 Id 是簽章第五段（`IssueSignatureKey.For` 的 EventKey 尾段，IssueHandling.cs:62-77），新增/修改 Linux 規則會讓同一事件重跑後的鍵從 4 段變 5 段或換值，舊結論接不回來。兩種孤兒都保留當歷史，不清（清了毀掉稽核軌跡）；此點若使用者想要「連處理紀錄一起刪」再議。RULES-SPEC 的「回溯套用」一節（批次 D）要寫明 (b) 這個副作用。
- 天數輸入（預設 30）只在模式 2/3/4 顯示；模式 1 維持現行行為與欄位。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 實作方 |
|---|---|---|---|---|
| A | 修 `TriggerRunAsync` 漏抄 `OnlyMissingOrFailed` 真 bug | 小 | 無 | Claude |
| B | 紀錄層「刪除主機日」能力（含子表一致性） | 中 | 無 | 待定（可委派） |
| C | 重跑模式接進 orchestrator 與手動執行 API | 中 | A、B | 待定（可委派） |
| D | UI（三選一、預覽申報、確認提示、規則頁提示）＋文件 | 中 | C | 待定（可委派） |

建議順序 A → B → C → D。

## 批次 A：修 `OnlyMissingOrFailed` 失效 bug

### 現況與核對結果
`SchedulerHostedService.TriggerRunAsync`（SchedulerHostedService.cs:150-158）重建 `RunRequest` 時只複製 Scope/HostIds/BackfillOverride/DebugDump/IncludeLocal/Trigger，**漏抄 `OnlyMissingOrFailed`**；而 `ScheduleController.Run`（ScheduleController.cs:210）有設定它、稽核訊息（:219）也印「僅補跑失敗或未執行」。→ 該勾選目前只影響稽核文字與 run-preview 預估，**實際執行從未生效**。

### 定案
本輪修正。此欄位是批次 C 新模式的同型前車之鑑——新增 `RunRequest` 欄位時必然再踩。

### 改動
1. `TriggerRunAsync` 補上 `OnlyMissingOrFailed` 傳遞；該處加註解「新增 RunRequest 欄位必須同步此處」。
2. 批次 C 新增的重跑旗標也必經此處，屆時以測試鎖住「欄位全數傳遞」。

### 測試 / 驗收
- 單元測試：透過 `TriggerRunAsync` 觸發時，orchestrator 收到的 `RunRequest.OnlyMissingOrFailed` 與呼叫端一致（true/false 各一）。
- 既有 2886 測試維持全綠。

## 批次 B：紀錄層「刪除主機日」能力

### 現況與核對結果
- `IAnalysisRecordStore` 只有 `Append`/`Prune`/`AttachWeeklyCheckup`/`AttachAiResult`，**無刪除指定主機日的方法**；`Append` 無條件新增（EfAnalysisRecordStore.cs:102），同日防重靠呼叫端 `HasRecord`。
- 一個主機日的落點（**實查修正**）：只有 `lf_daily_records` 主列＋`lf_top_issues` 子表。
  `lf_record_categories`／`lf_record_alerts`／`lf_deep_dive_analyses` **並不存在**——LfDbContext.cs:15
  明載「完整正規化（alerts/categories/deep_dives 各自成表）留待特定查詢需要時再加」，類別彙總
  目前寫在 `content_json` 內，隨主列一併刪除。`lf_risky_events` 每日寫入本就是 `ReplaceDay`
  （HostDayPostProcessor.cs:133），重跑時自然覆蓋，不需另刪。
- 既有 `Prune` 已是「先刪子表再刪主表、不依賴 FK cascade」的樣板（EfAnalysisRecordStore.cs:294-296），刪除主機日照同一形狀。
- `Prune` 有單次 50,000 列上限與「陸續清完 vs 卡住」申報的前例（DB-SPEC:441-443）。

### 定案
新增「以主機＋日期集合為單位刪除分析紀錄」的介面方法，Sqlite/SqlServer 共用同一份 EF 實作，刪除主列與 `lf_top_issues` 子列；分批執行並回報刪除筆數。**不動** `lf_issue_first_seen`、`lf_issue_handling`/`lf_record_handling`、`lf_issue_cases`、郵件已寄鍵——處理狀態鍵是 `(host, date, issue_key)`，重跑後自動接回，這是本設計成立的前提。

### 改動
1. `IAnalysisRecordStore` 新增刪除方法（介面名稱與簽章由實作端定，契約：指定 host＋日期集合，主列與子表一致刪除，回傳刪除的主機日數；冪等——刪不存在的日子不報錯）。
2. 雙後端實作；子表刪除與主列同一交易（單一主機日為交易邊界即可，暫定）。
3. 單次呼叫的批量上限與分批策略（暫定：沿用既有 BatchedPrune 的批次大小精神，由實作端依實測定，執行紀錄寫明理由）。

### 測試 / 驗收
- 契約測試（比照 `AnalysisRecordStoreContractTests` 形式，Sqlite 實跑）：建立含 top_issues 子列的主機日 → 刪除 → 主列與子列皆無殘留；相鄰日、其他主機同日不受影響；重複刪除冪等。
- 刪除後 `HasRecord` 回 false、`MissingDateFinder` 視為缺日。
- handling／first_seen／mail 狀態不被觸碰（測試斷言表列數不變）。

## 批次 C：重跑模式接進執行路徑

### 現況與核對結果
- 手動執行：`POST api/schedule/run` → `ScheduleController.Run`（ScheduleController.cs:195-229）組 `RunRequest` → `SchedulerHostedService.TriggerRunAsync` → `AnalysisOrchestrator.RunAsync`。
- 日期選取：`MissingDateFinder.Find`（HostDayPostProcessor.cs:15-37）以 `HasRecord` 跳過已有日；本機回望＝有紀錄時 `TrendWindowDays(14)`（AnalysisOrchestrator.cs:695-696），NetIQ 回望＝`BackfillDays` 夾在有效上限內（三道夾子：NetiqOptions.cs:82-86、ScheduleController.cs:184-193、AnalysisOrchestrator.cs:870-876）。
- 清理段跑在分析之前（AnalysisOrchestrator.cs:411-531）。

### 定案
`RunRequest` 以**執行模式**（enum：模式 1～4，見「執行模式定案」表）取代原本要新增的旗標；模式 1 即現行 `OnlyMissingOrFailed` 語意（既有布林欄位如何過渡由實作端定：可保留並由模式推導，或以模式欄位取代並相容舊呼叫端，執行紀錄寫明選擇）。模式 2/3/4（重跑模式）下：

- **候選日過濾**：窗口內既有主機日先按「執行模式定案」的日層級判準過濾（查 `lf_issue_handling` 該主機日有無列／有無結案類列），只有通過過濾的日子進入重跑；缺日照常回補。過濾查詢須批次化（不可逐日逐主機查一次 DB）。

- **窗口**：重跑天數由手動執行時指定（UI 預設 30），夾在既有有效上限（`GetEffectiveBackfillDaysLimit`）內；本機路徑同樣以此值取代 `TrendWindowDays`，NetIQ 路徑取代 `BackfillDays`。範圍＝窗口起日～昨天。
- **刪除時機＝逐日就地取代，不整批預刪**：對窗口內每個主機日，先完成該日重新掃描/分析，**產出可寫入的新結果後**才刪舊列、緊接 Append（同一主機日內完成刪＋寫）。理由：來源（Event Log/Sentinel）可能已滾掉舊事件，整批預刪會把「舊結果尚在、新資料取不到」的日子變成永久空洞。
- **來源無資料的保護（暫定契約）**：該日重掃結果完全無事件、而既有紀錄有事件 → **保留舊紀錄不取代**，計入申報（「N 個主機日因來源已無資料而保留原結果」）。什麼算「完全無事件」：掃描成功但事件數為 0；掃描本身失敗（頻道不可讀、Sentinel 查詢錯誤）同樣保留舊紀錄。
- **AI 不自動重跑**：重跑寫入的日子照一般新分析日的規則決定 `AiPending`（低風險日不標）；不主動對整窗口花 AI token。使用者事後可用「只補跑失敗或未執行」補 AI——兩模式因此互補。
- **首見日**：接受「新規則在舊日挖出的問題被視為近 7 天新問題、優先度拉高」——語意上對使用者就是新發現，不做首見日回填。`lf_issue_first_seen` 只升不降的既有保護維持。
- **郵件**：不重寄（`{host}|{date}` 已寄鍵不清）。重跑找到的新高風險不逐日通知，靠 UI 申報（批次 D）。
- **案件**：重跑日照常走 `IssueCaseCoordinator.AttachNewDay`，可能對進行中案件產生新活動訊號——接受，屬既有機制自然行為。
- **NetIQ 權限異動段**：重跑路徑不繞過 HostDayPostProcessor.cs:228-234 的「來源只回子集時不撤彙總列」保護分支。
- **併發/取消**：沿用既有 `SchedulerRunState`＋`NamedMutexGate`；取消時停在主機日邊界（該日若已刪未寫完成，交易邊界保證不留半日——批次 B 契約）。
- 自動排程**不會**進入重跑模式（`SchedulerHostedService` 排程觸發永遠組一般 `RunRequest`），以測試鎖住。

### 改動
1. `RunRequest` 加執行模式＋重跑天數；`ScheduleController.Run` 與 `run-preview` 接收、驗證（模式合法值、天數範圍）；`TriggerRunAsync` 傳遞（批次 A 的同步點）。
2. Orchestrator：重跑模式下候選日＝「窗口內全部日子」經處理狀態過濾（模式 2/3/4 判準）；逐日「新結果就緒→刪→寫」流程；來源無資料保留舊紀錄；申報計數（重跑成功日數／因處理狀態跳過日數／保留原結果日數）進 `BatchRunRecorder` 執行歷程。
3. `run-preview` 在重跑模式回報預估影響：窗口、主機數、將重跑的既有主機日數、因處理狀態跳過的日數。
4. 稽核：`ScheduleManualRun` 稽核訊息含執行模式與天數。

### 測試 / 驗收
- 模式驗證：非法模式值回 400；天數超上限回 400（比照既有 `ValidateBackfillDays` 行為）。
- 處理狀態過濾：三種日子（無處理列／只有非結案列／有結案列）在模式 2/3/4 下的納入與跳過各自斷言；混合日（結案＋未處理並存）只有模式 4 重跑。
- 重跑行為：已有紀錄的日子被重新分析且結果取代（record_id 改變、內容反映新規則）；來源無事件的日子保留舊紀錄並計入申報；掃描失敗同樣保留。
- 處理紀錄存續：某 issue_key 標「已處理」→ 模式 4 重跑 → 處理紀錄仍在且接回新列；新規則不再產生該問題時處理列保留（孤兒）。
- 自動排程觸發永遠是模式 1 語意，不會進入重跑。
- 郵件：重跑後已寄鍵日子不重寄（測試斷言寄送清單為空）。

## 批次 D：UI 與文件

### 現況與核對結果
- 手動執行面板現有：範圍、主機、回補天數（NetIQ）、「只補跑失敗或未執行」勾選（WEB-SPEC §排程頁）；上限值由 API 帶回（WEB-SPEC:2219-2228）。
- 規則維護頁 `/admin/rules` 儲存成功後無任何後續指引。

### 定案
1. 手動執行面板的「只補跑失敗或未執行」勾選改為**下拉選單四模式**（見「執行模式定案」表，預設模式 1）。模式 2/3/4 顯示天數輸入（預設 30、上限由 API 帶回）與分級警示：模式 3 提示「已指派但尚未完成處理的日子也會被刪除重新分析」；模式 4 強警示「**已處理的項目也會被刪除重建**——窗口內既有分析結果（含 AI 結論與深度分析）將刪除，以新規則重新產生；來源已無資料的日子會保留原結果；處理結論本身不會被刪除，同問題會自動接回」。模式 3/4 **送出前確認對話框**，內容含 run-preview 的預估影響數（將重跑／跳過日數）。
2. 規則維護頁：儲存／匯入套用成功的 toast 加一句「規則變更只影響之後的分析；如需回溯套用，請至排程頁執行『重新分析既有日子』」。
3. 執行監控頁顯示重跑進度與完成申報（重跑 N 日／保留原結果 M 日）。
4. 前端路徑紀律照 CLAUDE.md（`appUrl()`/`api.js`，不寫死 `/`）。
5. 文件：WEB-SPEC（排程頁三選一、確認流程、申報）、DB-SPEC（刪除主機日契約）、RULES-SPEC（「規則變更的回溯套用」一節，交叉引用排程頁）；只寫現況不寫歷程。

### 測試 / 驗收
- API 形狀測試：run-preview 重跑模式回傳欄位齊全。
- 前端以現行測試慣例覆蓋（依專案既有前端測試形式，無自動化者以驗收清單人工檢查：三選一互斥、警示與確認出現、`appUrl` 紀律 grep）。

## 明確不做（本輪定案）

- 規則異動自動追蹤與自動排程補跑（未選方案 a）。
- 逐規則局部清除問題、`lf_top_issues` 加 `rule_id` 抽出欄（未選方案 b）。
- 重跑日自動重跑 AI／深度分析（用「只補跑失敗或未執行」補）。
- 首見日回填（接受新規則舊日命中視為新問題）。
- 補跑結果郵件摘要（先靠 UI 申報，需要再開輪）。
- 停用/刪除規則觸發清除（舊問題留在歷史；整日重跑抹除的代價大於收益）。**另注意**：`Enabled=false` 不等於不偵測（RULES-SPEC:32-37），且 Operational 頻道 watchlist 由啟用中規則的 EventIds 推導——「先停用舊規則再重跑舊日」會讓該事件在重跑結果中整段收不進來，形成資料缺口。不擋此操作，但批次 D 的 RULES-SPEC「回溯套用」一節必須寫明此警告。
- `lf_issue_first_seen` force 重算入口（BACKLOG 既有項，不因本輪開啟）。

## 作業總覽（委派）

實作方待定（批次 A 由 Claude 自做；B/C/D 可委派 agy，屆時每批次抄成獨立規格檔）。整輪委派模型：待定。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A | Claude | 完成 | 5 測試綠，突變測試（拿掉欄位傳遞）3 紅確認守衛有效 | 原規劃只說「補上傳遞」，實作時抽出 `ComposeEffectiveRequest` 純函式才測得到——`TriggerRunAsync` 相依過多具體類別無法直接建構 |
| D | agy（gemini-3.7-flash-high） | 完成，一次過（Claude 修一個真 bug） | 2933 總計全綠；三個 id 齊備、`role="alert"`、複用 `confirmAction`、原生 confirm 零命中、舊勾選框零殘留、BOM 全數保留 | **跨段接線真 bug**：前端送字串 `"All"`、DTO 是 enum，而站台未全域註冊 `JsonStringEnumConverter` → 正式環境會 400，**所有後端測試照樣全綠**（它們直接建物件不經 JSON）。Claude 補 `[JsonConverter]` 並新增 `TriggerRunRequestBindingTests`（先寫測試證明 4 紅，修後 5 綠） |
| C2 | agy（gemini-3.7-flash-high） | 完成，一次過（Claude 補兩處） | 2928 總計（+9）全綠；兩條路徑各接上 `DeleteDays`、上限判定未另寫一份；批次 A 的反射守衛測試綠＝新欄位確實同步 | ①**三個檔案的 UTF-8 BOM 被剝除**（已知失敗模式），Claude 還原；②本機路徑 `rerunLookback` 未二次夾制，Claude 比照 NetIQ 既有慣例補上 `GetEffectiveBackfillDaysLimit` 夾制（防保留期事後調小）；③我規格的驗收基準寫錯（上限判定命中數寫 1、實際 3），核對後確認未被改動 |
| C1 | agy（gemini-3.7-flash-high） | 完成，一次過 | 2919 總計（+15）全綠；零既有檔案修改、Web 端零命中、grep 證明結案判定確實委託 `IssueHandlingStatuses.IsClosed`；突變測試（把結案判定換成任意列）1 紅 | `All` 模式提早回傳、不查 handling——規格沒要求但語意正確且省一次查詢，接受 |
| B | agy（gemini-3.7-flash-high） | 完成，一次過 | 2904 總計（+7）全綠；diff 白名單相符、BOM 未動、Web 端零命中；複用 `OwnedRows`／`PruneBatchSize`，無過度設計 | 突變測試（拿掉子表刪除）仍全綠——查明是 `LfDbContext.cs:174` 的 `OnDelete(Cascade)` 在 SQLite 上代勞，**非測試假通過**：顯式刪子表是既有 `Prune` 的慣例（防 provider 間 cascade 不一致），既有 Prune 測試同樣無法分辨。留此限制，不加 SqlServer 實機測試 |
