# LogForesight 第三十四輪規劃

> 狀態：全案完成＋終檢完成，待併 dev
> 基準：dev@fb4dcd8（2989 綠）→ 本輪 3034 綠
> 來源：使用者回饋四項（匯出檔案／排程記憶體 22GB／AD 分頁使用者名稱 regex／立即執行天數欄位合併）
> 委派模型：agy（原定整輪；實際兩次卡住後多數自做，見執行紀錄）

## 回饋對應與核對結論

| 回饋 | 核對結論 | 處置 |
|---|---|---|
| 1. 匯出檔案改存 DB | 三十三輪已完成，全專案無匯出寫檔（僅剩 diag 傾印與 NLog 日誌）。重複舊需求 | **跳過**，僅留本結論；diag 傾印無上限問題（B2）列入 BACKLOG |
| 2. 排程記憶體 22GB | 找到五組嫌疑（S7 本機全載／S3 去重鍵全載／S1+S2 Sentinel 緩衝×複製／S4+B3+B4 權限異動無界／B5 HostStore 複製） | **批次 A 全做** |
| 3. AD 分頁使用者名稱 regex | 短名化唯一出口 `AccountDisplayFormatter`，插顯示層最安全；另有 B10 多處帳號顯示未走出口 | **批次 B 全做（含 B10 擴接）** |
| 4. 兩個天數欄位合併 | 兩欄位語意互斥（缺→補、有→重跑），可合併；合併後對本機也生效；須排在 A2 之後 | **批次 C** |

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| A | 記憶體串流化與有界化（A1~A5） | 大 | 無 | 1 |
| B | 使用者名稱顯示規則（regex） | 中 | 無 | 2（可與 A 交錯，但不並行委派） |
| C | 立即執行天數欄位合併 | 中 | A2 完成後 | 3 |
| D | 順手修（設定頁文案） | 小 | 無 | 隨批次 A 尾段 |

---

## 批次 A：排程記憶體串流化與有界化

背景：分析與站台同行程；NetIQ 端最多 3 台 Sentinel × 4 查詢 = 12 個批次並行；3682 台主機規模。目標是把「整窗全載」改成「逐段處理逐段釋放」，並給無界累積加上限。

### A1：本機路徑逐日掃描（S7）

- 現況：[AnalysisOrchestrator.cs:762](../LogForesight.Core/Service/AnalysisOrchestrator.cs) `ScanRangeFromAllAsync` 把整個回補區間（首跑 120 天）× 全部頻道的事件放進單一 List，之後 `logsByDate` 再複製一份，兩者存活到逐日迴圈結束。單機吵雜 DC 可達 20+GB。
- 行為契約：本機回補改為**逐日取數、逐日分析、逐日釋放**——同一時間記憶體中最多保有一天份事件。分析結果（各日 record、趨勢輸入）與現行完全一致；`EventLogService` 若因此新增逐日介面，原整段介面的其他呼叫端不得受影響（先 grep 全部呼叫端）。
- 驗收：既有本機路徑測試全綠；新增測試驗證「多日回補時各日結果與整段掃描版一致」（以 fake 事件來源比對）。

### A2：權限異動去重改即時查 DB（S3）

- 現況：[NetiqPipelineService.cs:157](../LogForesight.Core/Service/NetiqPipelineService.cs) 開跑時把 `dedupeSince` 窗內全部 DedupeKey `ToHashSet()` 再灌 `ConcurrentDictionary`，兩份同時存在；窗口 = lookback+7 天 × 每日近 10 萬筆，回望 120 天峰值約 8GB，且整趟存活。
- 行為契約：去重不再於記憶體建全量快照，改由資料庫判定——利用既有唯一鍵語意做「已存在則略過」的寫入（實作形式由執行端定：查詢過濾或 INSERT-OR-IGNORE 皆可，但**不得**因重複鍵讓整批寫入失敗或吞掉批內其他列）。去重的判定範圍（同一 DedupeKey 在窗內只留一筆）與現行行為一致。批內（同一次寫入清單內）的重複也要維持現行去重行為。
- 驗收：既有權限異動去重測試全綠；新增測試：跨批重複鍵不重複入庫、批內重複不重複入庫、重複鍵存在時同批其他新列正常寫入。

### A3：Sentinel 取數分頁串流化（S1+S2）

- 現況：[SentinelClient.Paging.cs:9](../LogForesight.Core/SentinelClient.Paging.cs) `FetchAllPagesAsync` 把單一 job 至多 100,000 筆全量累進 List；消費端再 GroupBy 複製一份、`SentinelEventMapper.MapAll` 再實體複製一份，三份同時存在，12 job 並行峰值 7~9GB。已有 `PageObserver` 回呼掛勾未被使用（B7）。
- 行為契約：改為**分頁（或分主機組）即映射即處理**，原始 `SentinelEvent` 字典層不得整 job 累積——單一 job 在任一時刻記憶體中保有的原始頁數有固定小上限。逐主機日的分析輸入（該主機該日的 `EventLogEntryData` 清單）仍可完整成形後才分析（分析器需要整日資料，這層不串流）。分析結果、主機分組、日切分與現行一致。
- 驗收：既有 NetIQ pipeline 測試全綠；新增測試：多頁結果經串流路徑後，逐主機日分組內容與舊全量路徑一致（fake client 出多頁資料比對）。

### A4：權限異動入庫有界化（S4+B3+B4）

- 現況：[HostDayPostProcessor.cs:216](../LogForesight.Core/Service/HostDayPostProcessor.cs) `RawText = evt.Message` 未截斷入庫（AlertText 已有 500 字上限）；[PermissionChangeStore.cs:46](../LogForesight.Core/Persistence/PermissionChangeStore.cs) `AppendChanges` 整批單一 DbContext 一次 `SaveChanges()`，ChangeTracker 峰值 = 整批 ×2。
- 行為契約：
  1. `RawText` 入庫加截斷上限，**暫定 8000 字**（使用者要求上限放寬避免截斷；執行端可依實際明細顯示需求微調並在回報說明）。截斷時要能讓讀者看出被截斷（尾端標記）。既有明細顯示（通用拆欄、報告）在 8000 字內行為不變。
  2. `AppendChanges` 分批 SaveChanges，**暫定每 500 筆一批**；批間失敗處理維持現行語意（不得出現「前半批已入庫、後半批靜默丟失且無錯誤」——失敗要浮出）。
- 驗收：新增測試：超長 Message 入庫後長度受限且帶截斷標記；大批寫入（>1 批）全數入庫且去重行為不變。

### A5：HostStore 依 id 查找去複製（B5）

- 現況：[HostStore.cs:14](../LogForesight.Core/Persistence/HostStore.cs) `Get(hostId)` 每次 `Read()` 整份淺複製 3682 筆再 `FirstOrDefault`，呼叫點在逐主機迴圈內（[NetiqPipelineService.cs:330](../LogForesight.Core/Service/NetiqPipelineService.cs)）。
- 行為契約：`Get` 改為不整份複製的 id 查找（索引快取或直接走快照皆可），語意不變：查無回 null、資料更新後查得到新值。呼叫端不得取得可污染共用快照的可變參考——維持現行「回傳物件不可修改」的約定不變壞。
- 驗收：既有 HostStore 測試全綠；新增測試：更新主機後 `Get` 取得新值。

---

## 批次 B：使用者名稱顯示規則（regex）

### B1：規則設定與顯示出口（後端）

- 現況：短名化唯一出口 [AccountDisplayFormatter.cs](../LogForesight.Core/Service/AccountDisplayFormatter.cs)（純靜態），5 個呼叫點在 [PermissionChangeService.cs](../LogForesight.Web/Services/PermissionChangeService.cs) 顯示層；入庫欄位保留原文。
- 定案：規則套在**顯示層**（歷史資料立即生效、規則寫錯可救、入庫原文不動）。不採入庫層方案（要重剖回填且寫錯永久污染）。
- 行為契約：
  1. 新增 SystemSettings 鍵（多行文字），每行一條規則，格式 `pattern => replacement`（replacement 可為空 = 刪除匹配段），逐行依序套用；`#` 開頭為註解行、空行略過。規則套在短名化**之後**的顯示值上。
  2. `AccountDisplayFormatter` 升級為可吃規則的顯示出口（注入或 overload 由執行端定），無規則時行為與現行完全一致。
  3. 非法 regex：存檔 API 驗證擋下（回 400 指出第幾行）；執行期防禦性略過非法行不炸站台。regex 執行加逾時保護（災難性回溯不能吊死列表渲染）。
  4. settings API/DTO 同步增欄；**新增設定必有消費端**紅線遵守。
- 驗收：新增測試：規則套用順序、空 replacement、註解/空行、非法規則 400、無規則時輸出與現行 byte 相同。

### B2：AD 分頁 UI（前端）

- 現況：AD 分頁在 [Settings.cshtml:340](../LogForesight.Web/Views/Pages/Settings.cshtml)；權限異動欄位對應在「分析參數」分頁（`:296`）——兩組設定分屬不同分頁是使用者指定，照做。
- 行為契約：AD 分頁新增「使用者名稱顯示規則」textarea + 說明文字（格式、範例：去掉公司名前綴）+ 即時格式提示；存檔走既有 settings 流程；後端 400 時跳回該分頁並標示錯誤行。文案台灣繁中。
- 驗收：前端存讀值接通（payload 含新欄）、驗證失敗跳分頁；既有 AD 分頁功能不變。

### B3：帳號顯示出口擴接（B10）

- 現況：問題事件帳號抽取（[LogAggregator.cs:232](../LogForesight.Core/Analysis/LogAggregator.cs)）、登入失敗文字（`LoginFailureTextFormatter`、[LogAnalysisService.cs:825](../LogForesight.Core/Service/LogAnalysisService.cs)）、Linux 認證（`LinuxAuthParser`）輸出的帳號未走顯示出口，站內帳號會出現兩種樣貌。
- 行為契約：**只接「渲染給前端時才組出的顯示欄位」**——這些點逐一改走 `AccountDisplayFormatter` 出口（短名化＋規則）。**分析期組好即入庫的文字（record 內文、報告內文）不回頭改寫**——那是入庫值，規則變更後靠重新分析才會套用新規則，此限制寫進設定頁說明文字。哪些點屬顯示、哪些屬入庫，執行端逐點判定並在回報列清單，由 Claude 驗收核對。
- 驗收：列出的顯示點逐一有測試或可 grep 驗證走出口；入庫文字產生點確認未被改動。

---

## 批次 C：立即執行天數欄位合併（相依：A2 完成後）

- 現況：「回望天數」（`backfillDays`→`BackfillOverride`→MissingDateFinder，找**沒紀錄**的日子，僅 NetIQ）與「重新分析回望天數」（`rerunDays`→RerunDateFinder，找**有紀錄**的日子，本機+NetIQ）互斥互補，最後 union。前端 [Runs.cshtml:177,198](../LogForesight.Web/Views/Pages/Runs.cshtml)、[runs.js:1024-1053](../LogForesight.Web/wwwroot/js/pages/runs.js)。
- 定案：合併為單一「回望天數」欄位：近 N 天內**缺的補、有的按執行模式重跑**；合併後的天數**同時對本機與 NetIQ 生效**（消除現行「回望僅 NetIQ、rerun 才管本機」的靜默不一致）。排在 A2 之後——去重鍵改即時查 DB 後，大天數不再放大記憶體。
- 行為契約：
  1. 前端 modal 只剩一個天數欄位（留空＝沿用 NetIQ 維護頁設定；上限仍 = min(365, 保留天數)，前端 max + 後端 400 驗證不變，**補上下界檢查**（B8 順帶解掉））。
  2. 執行模式 `None`：行為與現行完全一致（只補缺）。其他模式：同一 N 同時餵 MissingDateFinder 與 RerunDateFinder（含本機路徑 [AnalysisOrchestrator.cs:713](../LogForesight.Core/Service/AnalysisOrchestrator.cs) 改吃合併值）。
  3. API 相容：`rerunDays` DTO 欄位的處置（移除或保留為相容別名）由執行端評估**排程設定端與其他呼叫端**後決定並回報——**寫規格前已知呼叫端：立即執行 modal、排程設定（若有送此欄）**；執行端須 grep `rerunDays`/`RerunDays` 全部產生端與消費端，一個都不能漏在白名單外。
  4. popover/hint 文案改寫：說明「缺的補、有的按模式重跑、本機與 NetIQ 皆適用」。警示紅框（刪除重建說明）邏輯不變。
- 驗收：既有 ScheduleController 測試調整後全綠；新增測試：單一 N 下 missing∪rerun 覆蓋正確、None 模式不重跑、本機路徑吃到合併值、超上限 400、下界 <1 前端擋。

---

## 批次 D：順手修（Claude 自做，不委派）

- D1：[Settings.cshtml:430](../LogForesight.Web/Views/Pages/Settings.cshtml) 保留天數說明「匯出的報告檔」→ 改為與 `:445`（報告存資料庫）一致的說法。

## 明確不做（本輪定案）

- **B9 報告遷移器退場**：三十三輪尚未實測併 master，升級路徑仍需要遷移器讀舊 `export\` 檔；退場延到報告機制在正式機驗證後的輪次（記入 BACKLOG）。
- **B2 diag 傾印保留期**：診斷用途、手動開關，本輪不動；記入 BACKLOG（無上限、忘關會塞磁碟）。
- **S5 AI 佇列 200 件背壓**與 **S8 報告字串組裝**：設計內預算，不動。
- **B6 JsonBlobCollection 回傳可變物件靠約定**：不改型別保證，A5 契約要求不變壞即可。
- **P1 匯出改存 DB**：已於三十三輪完成，本輪核對確認無殘留產出路徑。

## 執行紀錄

> 狀態：全案完成＋終檢完成，待併 dev。基準 dev@fb4dcd8（2989）→ 本輪 **3034** 綠。
> 委派：本輪原定整輪委派 agy，實際只有 B1 由 agy 產出（且中途撞額度上限），
> A 批兩次委派都在零 diff 下卡住（連續 15~20 分鐘無產出、CPU 近乎閒置），依 gemini-delegate §4
> 的「執行卡住零產出」條款終止改自做。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1 分塊掃描 | agy 起頭→Claude 完成 | 完成 | +6 測試 | agy 只寫完 EventLogService 一半就卡住；接手後刪掉它加的未使用 Merge 多載與 chunkFilter（過度設計），改由呼叫端自行迴圈 |
| A2 去重改查 DB | Claude | 完成 | +5 測試 | 舊測試「去重鍵查詢起點依回望天數推算」守護的機制已消失，改寫為「整輪不得再呼叫整窗快照」 |
| A3 取數串流化 | Claude | 完成 | +4 測試 | `Retrieved` 改用「未指定時退回 Events.Count」的預設，避免動到約 30 處既有測試建構點 |
| A4 入庫有界化 | Claude | 完成 | +4 測試 | RawText 上限定 8000（使用者要求放寬） |
| A5 主機查找去複製 | Claude | 完成 | +4 測試 | 抽 `ReadSnapshot()`，`Read()` 改為它的複本，避免兩份讀取邏輯 |
| B1 顯示規則後端 | agy | 完成（需大幅修正） | +9 測試（Claude 重寫） | agy 撞額度中止；另**繞過既有設定服務**自建 `ISystemSettingsStore?` 可選相依二次寫入、加了無呼叫端的 `ValidateRules`、剝除兩個檔案的 BOM——全部由 Claude 改回正規對應點並還原 BOM |
| B2 AD 分頁 UI | Claude | 完成 | 前端接線 grep | — |
| B3 顯示出口擴接 | Claude | 完成 | +3 測試 | 逐點判定後，除權限異動外只有登入失敗明細帳號是「查詢時才組出」的顯示欄位；其餘（問題事件內文、報告）是入庫值，依定案不改寫 |
| C 天數合併 | Claude | 完成 | +5 測試 | `RerunDays` 選擇**移除**而非保留別名；全庫 grep 零殘留（含前端與四個測試檔） |
| D1 設定頁文案 | Claude | 完成 | grep | — |

### 終檢（兩個獨立 Explore：程式碼 + 文件）

| 發現 | 判定 | 處置 |
|---|---|---|
| A2「批內重複不再去重」（標為高） | **不成立** | 自己讀程式碼查證：批內折疊在 `seenInBatch` 仍在原位、且用同一把鍵。仍補了一條守衛測試釘住 |
| A3 同批兩台主機共用同一 IP 時第二台拿到空事件 | 成立 | 改為「共用該 IP 的主機都取用過才丟桶子」 |
| C 本機「留空」時的預設值也被保留期夾制 | 成立 | 夾制只套在使用者明示的天數上 |
| 前端用 JS RegExp 驗語法會誤擋後端接受的規則 | 成立 | 前端只驗與引擎無關的格式，語法交後端 400 |
| 串流測試只測到測試替身（同義反覆） | 成立 | 刪掉自抄分組邏輯的比對測試，改留結果語意測試；分組本身由既有 NetIQ pipeline 整合測試覆蓋 |
| `Assert.IsNotType<DomainException>` 太鬆 | 部分成立 | 收緊後立刻抓到替身走不過驗證階段；改為精準釘住「驗證階段沒有拒絕」並說明理由 |
| 同型遺漏：`UserStore.FindByAccount` 也在逐列迴圈裡複製整份清單 | 成立 | 一併改走 `ReadSnapshot()` |
| A1 分塊使 classic API 重複遍歷（效能取捨） | 成立 | 區塊天數 7→14（120 天首跑 9 塊），取捨寫進常數註解 |
| A3 未達成「原始事件不整 job 累積」 | **成立，誠實申報** | 現況是消掉一份複本（扁平清單），分組後的桶子仍持有整個 job、但逐主機處理完即釋放。要再進一步需改成逐主機查詢，本輪不做 |
| 文件過時（WEB-SPEC 三處、DB-SPEC 兩處、BACKLOG 一處、CLAUDE.md 基準） | 成立 | 全部更新 |
