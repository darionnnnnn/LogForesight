# 回饋第 46 輪規劃：讓 PRTG 規則真正發揮作用

> 狀態：規劃中（待使用者確認後開分支）
> 基準：dev@45b14d7（4128 綠，略過 6）
> 來源：重新檢視 PRTG 規則的規劃——四條狀態變更規則已能產出 finding，但 finding 進入全鏈後接不上知識庫、抑制、趨勢、關聯與畫面，PRTG 的加入尚未發揮它該有的價值。
> 執行方式：`agy`（`gemini-3.8-flash-high`），使用者 2026-09-16 指定；規劃由 Fable 5.1、實作輪由 Opus 5 接手。subagent 核對走 `scan-low`（本輪已派過一次，沿用）。

## 核對結果（規劃前事實）

| 項 | 判定 | 證據 |
|---|---|---|
| finding 進 `lf_top_issues`、郵件、排行、處理狀態鏈 | ✅ 自然涵蓋 | 全站無 `prtg:` 前綴的排除分支 |
| RuleId 值域不一致 | ❌ bug | mapper 寫 `prtg-{code}`（`PrtgFindingMapper.cs:71`），seed Id 是 `builtin-prtg-{code}`（`KnownIssueSeed.cs:1082`）；詳情頁知識庫面板依 RuleId 反查（`RecordDetailQueryService.cs:666`）必落空；`PrtgFindingMapperTests` 把錯值寫進斷言 |
| 抑制對 PRTG 無效 | ❌ bug | `Suppressed` 唯一寫入點在 `LogAnalysisService.cs:205-220`，PRTG finding 在其回傳後才追加；`RiskFromFindings` 的 `!f.Suppressed` 是死碼；抑制預覽（`RuleAdminService.PreviewSuppression`）prtg 落 Windows 分支，命中數恆 0 |
| 規則頁可編輯的 Category／Severity／ElevatesDayRisk 對 PRTG 無效 | ❌ bug（有設定無行為） | `PrtgDailyPipeline.cs:205-225` 只從規則取 `PrtgThreshold`；分類／嚴重度／旗標一律取靜態 `PrtgRuleCatalog` |
| 排行與紀錄清單的白話說明 | ❌ | `KnownIssueCatalog.PlainExplanationFor` 對 EventId=0 只找 linux 規則 |
| AI prompt 與畫面文字 | ⚠️ | prompt 只餵 catalog 一句話（`AnalysisPromptBuilder.cs:179-192`），量值（`Detail`）與 sensor 名稱進不去；畫面上只有 `prtg:down:{objid}` |
| 趨勢層／關聯層／體檢 | ❌ 零 PRTG 輸入 | 追加點在 `AnalyzeDayStatisticalAsync` 之後（`AnalysisOrchestrator.cs:911-918`）；`CorrelationAnalyzer`、`LogAnalysisService`、`WeeklyCheckupService` 皆無 prtg |
| 門檻粒度 | ❌ 全域單值 | 同 ruleCode 多條規則後者覆蓋前者；`lf_prtg_sensors.category` 有寫入、零消費端；`Scope` 對 PRTG 不看 |
| 規則母體綁取數白名單 | ⚠️ | `PrtgDailyPipeline.cs:228-241,281`；白名單預設不含 Ping（`SystemSettings.cs:410-422`），`down` 永遠抓不到最直接的失聯訊號；messages 本來就是全量抓 |
| 未回報主機 | ❌ 不看 PRTG | `DashboardService.BuildSilentHosts`、`HostAdminService` 的 `silent` 篩選只看 `LastReportAt` |
| 規則儲存形式 | ✅ JSON 內容（`IKnownIssueRuleStore`），無正規化子表 | 為 `KnownIssueRule` 新增欄位不需 DDL |
| 現成的跨日聚合 | ✅ | `EfIssueAggregateQuery.AggregatePrtgRuleHits`（只餵校準頁） |
| 追加路徑手上有什麼 | ✅ | `HostDayPostProcessor.AttachPrtgFindings` 同時有記憶體 `record`（含 `TopIssues`／`CorrelationAlerts`）與該主機的 finding；DB 路徑 `EfAnalysisRecordStore.AttachPrtgFindings` 反序列化整份紀錄 |

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| A | finding 補成一等公民：規則驅動的映射（修 RuleId／分類／嚴重度）、抑制生效、說明文字與量值 | 中 | 無 | 1 |
| B | 規則母體與分類語意：母體脫鉤白名單、sensor 分類擴充與補充對照、依分類的規則覆寫 | 中 | A（映射改由規則驅動後才有「依分類挑規則」） | 2 |
| C | 跨日訊號：近 14 日重複／連續命中的標註與升級、體檢納入 PRTG | 小～中 | A | 3 |
| D | 跨來源佐證：追加時的關聯判定（儲存／失聯兩式）、未回報主機的 PRTG 佐證 | 中 | A、B（需要 `availability`／`disk` 分類） | 4 |
| E | 校準資料補上後的加強（本輪只寫文件卡位） | 文件 | 校準四項達「可用」 | — |

### 作業總覽（委派輪）

- **執行端**：A-1、A-2 為 `agy`（`gemini-3.8-flash-high`）；**自 A-3 起改 `impl-low`（Opus 5＋low effort），使用者 2026-09-16 指定，不換回**。A-3 的 agy 執行中途停止，半套改動收在 stash「agy A3 中止時的半套改動」不採用；階段規格抄成 `.gemini-tasks/task-46-<階段>.md`（已在 `.git/info/exclude`），執行端不看本文件。Claude 每段獨立驗收，不採信摘要。
- **不依賴外部清單的部分先做**：環境探測的 type 名單與校準結果後續補上。依賴它們的只有 B-3 的「內建 `hardware` 對照條目」與 E；其餘全部先做（`hardware` 分類常數、補充對照表、依分類規則、儲存故障佐證模式都不需要名單，測試直接種分類）。
- **UI 階段排最後**（B-4、D-3），設計方案待使用者答覆後才開工。

| 階段 | 內容 | 主要檔案（白名單在規格檔） | 前置 |
|---|---|---|---|
| A-1 | 規則驅動的評估與映射、已確認狀態、Detail 帶名稱、校準端跟改 | `PrtgRuleEvaluator`／`PrtgFindingMapper`／`PrtgRuleCatalog`／`PrtgConstants`／`PrtgDailyPipeline`／`CalibrationService`／`EfPrtgStore` | — |
| A-2 | 抑制在追加時生效（共用套用函式）、執行輸出計數 | `SuppressionFilter`／`LogAnalysisService`／`HostDayPostProcessor`／`PrtgDailyPipeline`／`EfAnalysisRecordStore`／兩個呼叫端 | A-1 |
| A-3 | 抑制預覽 prtg 分路、白話說明反查、prompt 格式、註解修正 | `RuleAdminService`／`KnownIssueCatalog`／`IssueRankingBuilder`／`RecordListQueryService`／`AnalysisPromptBuilder`／`LfDbContext` | A-1 |
| B-1 | 母體脫鉤白名單、同裝置折疊 | `PrtgDailyPipeline`／`PrtgRuleEvaluator` | A-1 |
| B-2 | 分類擴充＋補充對照表設定（含驗證與 auto 列重套）＋守門說明 | `PrtgConstants`／`SystemSettings`／`EfPrtgStore`／設定驗證 | — |
| B-3 | `PrtgSensorCategory` 欄位、驗證、依分類挑規則、seed v7（`hardware` 內建條目待名單） | `KnownIssueCatalog`／`RuleValidator`／`PrtgRuleEvaluator`／`KnownIssueSeed` | A-1、B-2 |
| B-4 | 規則頁「適用分類」、設定頁補充對照表、主機頁與探測輸出分類欄 | `rules.js`／`settings.js`／`host-detail.js`／`PrtgProbeRunner`／DTO | B-3（UI，待設計答覆） |
| C-1 | 跨日標註與升級、長期 Down 降噪、批次查詢 | `EfAnalysisRecordStore` 或 `EfIssueAggregateQuery`／`PrtgDailyPipeline`／`PrtgRuleCatalog` | A-2 |
| C-2 | 體檢 prompt PRTG 段 | `WeeklyCheckupService` | — |
| D-1 | 三個佐證模式（純函式）＋追加路徑寫關聯欄位＋抑制 PatternId | `PrtgCorroboration`（新）／`CorrelationPatternIds`／`CorrelationAnalyzer`／`HostDayPostProcessor`／`EfAnalysisRecordStore` | B-3、C-1 |
| D-2 | 未回報主機 PRTG hint（後端） | `HostAdminService`／`DashboardService`／`EfPrtgStore` | B-2 |
| D-3 | 未回報 chip 與儀表板 hint（前端） | `hosts.js`／`dashboard.js` | D-2（UI，待設計答覆） |
| 文件 | PRTG-SPEC／DETECTION-SPEC／RULES-SPEC／WEB-SPEC／BACKLOG／CLAUDE.md | Claude 親寫 | 全部 |

## 批次 A：finding 補成一等公民

### 現況與核對結果

見上表前六列。三個 bug 的共同根因是**映射不看規則庫**：`PrtgFindingMapper.ToSignature` 只拿 `PrtgRuleCatalog` 的靜態值，規則庫裡那條 `builtin-prtg-*`（含使用者可編輯的分類／嚴重度／旗標／知識庫）從頭到尾沒被用上。

### 定案

1. **映射改由「命中的規則」驅動**：評估時每筆 finding 帶著命中的 `KnownIssueRule`；簽章的 `RuleId`＝該規則 Id、`Category`／`Severity`／`ElevatesDayRisk`／`KnownIssue`（＝規則 `Description`）全部取自規則。`PrtgRuleCatalog` 退為 seed 預設值與「規則庫查無規則時」的保底，不再是執行期真相。
   - 不選「只把 `prtg-` 改成 `builtin-prtg-`」：那只修知識庫面板，規則頁可編輯欄位無效的 bug 還在。
2. **抑制在追加時生效**：PRTG 路徑在**發佈登錄簿之前**對每台主機的 finding 標記一次 `Suppressed`——規則型（RuleId）、簽章型（`IssueSignatureKey.For`）與既有 `LogAnalysisService` 同一套判定（抽成共用函式，不各寫一份）；兩條追加路徑（補追加、就地追加）收到的都是已標記的簽章，不各自再判。被抑制的 finding **仍追加**（`Suppressed=true`，與事件層語意一致：抑制是不吵不拉風險，不是不存在），`RiskFromFindings`／`RiskBasisFrom` 因此真的排除它。
   - 主機群組成員資格從主機主檔取（PRTG 路徑本來就載入全部主機）；抑制清單同 `AnalysisOrchestrator` 的來源（`SuppressionStore` 於 `backend.Blob("suppressions")`），每趟載一次。
   - 不選「兩條追加路徑各自比對」：同一判定寫兩處，而且就地追加那條的呼叫端要多傳三個參數。
3. **抑制預覽三向分路**：prtg 規則的命中數改以 `lf_top_issues` 的 `EventKey` 前綴 `prtg:{code}:` 統計（`AggregatePrtgRuleHits` 已有同型查詢），不再落 Windows 分支。
4. **說明文字**：
   - `PrtgFinding.Detail` 帶 sensor 名稱、type、device 名稱（例：「[Web01] Ping（Ping）持續 Down 達 720 分鐘，自 … 起」）；`silent` 帶 device 名稱與 sensor 數。
   - AI prompt 的【PRTG 監控訊號】每列同時餵規則描述與 `Detail`（格式暫定「- [嚴重度] {規則描述}：{Detail}」）。
   - `PlainExplanationFor` 加 prtg 分路：`Source == "PRTG"` 時不看 EventId，改由呼叫端傳入的 RuleId（聚合層已有 EventKey，可解出規則代碼再對回規則 Id）取白話說明；解不出時回 null。
5. `LfDbContext` 對 `category` 欄位的「一律為 null」註解改為現況（自動分類已落地）。
6. **`Down (Acknowledged)` 不再每天拉高風險**：PRTG 上已被人確認的 Down，判定仍算 Down（持續時間、flapping 照算，`Down (Partial)` 亦同），但 finding 標記「已於 PRTG 確認」：`ElevatesDayRisk` 強制關閉、嚴重度不變、`Detail` 註記。判定收斂在 `PrtgSensorStatuses` 新增的一個方法（前綴比對後看括號內容），不在評估器裡比字串。
   - 理由：PRTG 操作者已經接手的事，站台每天再判「高風險日」只會製造告警疲勞；但它仍是事實，不能不列。
7. **已抑制 finding 的下游**：案件掛接與郵件摘要沿用事件層對 `Suppressed` 的既有處理（事件層過濾則 PRTG 也過濾，反之亦然），不另寫分支——實作前 grep 事件層現況，寫進規格檔。
8. **執行輸出可觀測**：PRTG 路徑每日印一行計數「finding N 筆（其中已抑制 a、已於 PRTG 確認 b）」，後續批次各自追加欄位（折疊 c、跨日升級 d、長期 Down e、佐證 f）。管理者看執行紀錄就知道每道降噪各吃掉多少，不必翻資料庫。
9. **白話說明的規則反查**（A4 補充）：聚合層只有 EventKey，解出規則代碼後可能對到多條規則（B 加了分類覆寫）——只有一條啟用規則時用它；多條時取「不限分類」那條；連它都沒有時回 null。不猜分類。
10. seed 的 `Description` **不嵌門檻數字**（現行「（預設 60 分鐘）」在管理者改門檻後就是錯的）；門檻由畫面另列。
11. **問題排行不再把全部 PRTG 塌成一列**：排行與紀錄清單的聚合鍵是 `(Source, EventId)`（`IIssueAggregateQuery.cs:14-15`），PRTG finding 全部是 `("PRTG", 0)`，四條規則、幾百顆 sensor 在排行上只有一列「PRTG」。定案：簽章的 `Source` 改為 `PRTG:{規則代碼}`（例 `PRTG:down`），`LogName` 維持 `PRTG`；全站「這是不是 PRTG finding」的判定一律改看 `LogName`（正式碼比對點只有 4 處：prompt builder 三處、`AggregatePrtgRuleHits` 一處），收斂成 `PrtgFindingMapper.IsPrtg(signature)` 一個方法。白話說明（A9）因此直接從 `Source` 解出規則代碼，不必碰 EventKey。
    - 排行上同一規則跨 sensor 仍合成一列（「PRTG:down ／ 影響 N 台」），這是排行該有的粒度；逐 sensor 的明細在紀錄詳情。
    - 既有紀錄的 `Source="PRTG"` 不回寫，排行短期會多一列舊格式的「PRTG」，隨保留期消失（升級注意）。

### 改動

1. Core：`PrtgRuleEvaluator` 的輸入從「門檻三元組＋啟用代碼集合」改為「規則清單」，輸出 finding 帶命中規則；`PrtgFindingMapper.ToSignature` 改吃規則；`PrtgRuleCatalog` 保留 seed 常數與保底。
2. Core：抽出「對一組簽章套用主機有效抑制」的共用函式（`LogAnalysisService` 既有邏輯搬出來共用），`HostDayPostProcessor.AttachPrtgFindings` 與 `PrtgDailyPipeline` 補追加路徑呼叫；`EfAnalysisRecordStore.AttachPrtgFindings` 的 DB 路徑寫入的是已標記後的簽章。
3. Web：`RuleAdminService.PreviewSuppression` prtg 分路；`IssueRankingBuilder`／`RecordListQueryService` 的白話說明對 PRTG 走新分路。
4. Core：`Detail` 文案帶名稱（評估器需要 sensor／device 名稱字典，`PrtgDailyPipeline` 已載入全部 sensor，device 名稱由 store 補一支查詢或沿用既有）；`AnalysisPromptBuilder` PRTG 區塊格式。
5. 測試：`PrtgFindingMapperTests` 斷言改為 `builtin-prtg-*`；新增「規則頁改 Severity／ElevatesDayRisk 後 finding 跟著變」、「抑制 builtin-prtg-down 後日風險不被拉高但 finding 仍在」、「簽章型抑制對 PRTG finding 生效」、「預覽命中數＝14 日內 prtg:down: 前綴筆數」、「prompt 含 Detail 量值」、「`Down (Acknowledged)` 持續 90 分：finding 有、日風險不升、Detail 含已確認；`Down (Partial)` 照常升」、「白話說明反查在同代碼兩條規則時取不限分類那條」。
6. 校準服務（`CalibrationService.cs:1463`）是評估器的第二個呼叫端，以最低門檻重評——改成傳「最低門檻的規則清單」，白名單要含它。
7. 定案 10（描述不嵌數字）隨 seed v7 在 B-3 落地，不在 A 動 seed（避免同一輪兩次改 seed 內容）。

**定案→規格對照**（`.gemini-tasks/task-46-A1／A2／A3.md`）：定案 1、4（Detail）、6、11 與改動 6 → A1；定案 2、7、8 → A2；定案 3、4（prompt、白話）、5、9 → A3；定案 10 → B-3。每條都出現在該段的測試或驗收指令中，7 為事實回報。

### 測試／驗收

- 規則頁把 `builtin-prtg-down` 的 ElevatesDayRisk 關掉 → 當日 down finding 追加後日風險不升到「高」（突變：改回開啟必須升）。
- 詳情頁 PRTG finding 掛得出知識庫面板（`BuildGuidance` 非 null）。
- Site 範圍抑制 `builtin-prtg-down` → 追加的 finding `Suppressed=true`、`RiskBasis` 不記 `prtg:down`；抑制預覽命中數＞0（種 3 筆 `prtg:down:` 簽章）。
- `grep -rn '"prtg-' LogForesight.Core LogForesight.Web LogForesight.Tests --include=*.cs` 零命中（舊 RuleId 前綴不得殘留）。
- 全量測試綠。

## 批次 B：規則母體與分類語意

### 現況與核對結果

- 規則只看白名單內 sensor，silent 的分母也是白名單（`PrtgDailyPipeline.cs:233-241`）。白名單是為**數值取數**的量體設計的（Ping 因數值雜訊被排除），狀態變更本來就全量抓。
- `PrtgSensorCategories` 只有 traffic／disk／cpu／memory；對照表八個 type（`PrtgConstants.cs:97-111`）；自動分類只填 null。
- `category` 欄長 20（`LfDbContext.cs:171`）。

### 定案

1. **規則母體＝全部未暫停 sensor**，與取數白名單脫鉤（狀態變更已是全量，不增加 PRTG 負擔）。silent 的分母跟著改為 device 底下全部未暫停 sensor。文件（PRTG-SPEC §9「規則只看白名單內」）同步改寫；白名單的語意收斂為「數值取數與快照目標」。
   - 不選「另開規則專用 type 清單」：多一個設定、多一個要維護的清單，而 Ping 被排除正是清單化的後果。
2. **分類擴充**：新增 `availability`（Ping／連通性類）與 `hardware`（硬體健康：溫度、風扇、電源、RAID 等）。內建對照表先加 `Ping`→`availability`；其餘 type 名稱**待使用者提供實機探測的「Sensor Type 分布」清單後填入**（不憑記憶猜 type 字串）。
3. **補充對照表設定** `PrtgSensorTypeCategoryOverrides`（一行一個 `type=category`，不分大小寫；category 必須是六個合法值之一，存檔時驗證）。套用規則：補充表優先於內建表；每日結構同步的自動分類對 `category_source=auto` 的列**重新套用**（對照表改了要能生效），對非 auto 的列不動（人工分類契約不變）。消費端＝自動分類與規則覆寫，符合「新增設定必須有消費端」。
4. **依分類的規則覆寫**：`KnownIssueRule` 新增 `PrtgSensorCategory`（null＝全部分類）。同一 `PrtgRuleCode` 可有多條規則，評估時每顆 sensor 取「分類相符的規則」優先於「不限分類的規則」；同一層命中多條時取 Id 字典序最小並在執行輸出警告一次（規格寫明，不靜默）。`RuleValidator`：`PrtgSensorCategory` 必須是合法分類或空；`silent` 規則不可填分類（它是 device 層判定）。規則頁 prtg 表單加「適用分類」下拉（含「全部」）。
5. **內建規則調整（seed v7）**——依「availability 的 down 才是主機失聯、disk 的 warning 是資源前兆」的語意：

| Id | 代碼 | 分類 | Category／Severity／Elevates | 門檻 | 結果 |
|---|---|---|---|---|---|
| `builtin-prtg-down` | down | 全部 | Service／High／**false** | 60 分 | 拉「中」 |
| `builtin-prtg-down-availability`（新） | down | availability | Service／High／**true** | 30 分（暫定） | 拉「高」 |
| `builtin-prtg-down-hardware`（新） | down | hardware | Hardware／High／false | 60 分 | 拉「中」 |
| `builtin-prtg-warning` | warning | 全部 | Resource／Medium／false | 240 分 | 不拉 |
| `builtin-prtg-warning-disk`（新） | warning | disk | Storage／High／false | 240 分 | 拉「中」 |
| `builtin-prtg-warning-hardware`（新） | warning | hardware | Hardware／High／false | 120 分（暫定） | 拉「中」 |
| `builtin-prtg-flapping`、`builtin-prtg-silent` | — | — | 不變 | 不變 | 不變 |

   - 既有 `builtin-prtg-down` 從 elevates=true 改 false 是**行為變更**：升級後未套用 seed 更新前維持舊行為；套用後既有紀錄不回溯（同 PRTG-SPEC §9 既有原則）。
   - 新增規則各自帶完整知識庫四欄（白話／影響／原因／處置），內容針對該分類寫（例：disk warning 的處置是清理與擴充，不是「檢查網路」）。
6. **同裝置折疊**：母體放寬後，一台主機失聯會讓該裝置上每顆 sensor 各出一筆 `down`（PRTG 沒設相依性時尤其如此），問題排行被同一件事灌滿。定案：同一 device 當日若有 availability 類的 `down` finding，該裝置其他 sensor 的 `down`／`flapping` finding **不個別發出**，折疊進 availability 那筆的 `Detail`（「同裝置另有 N 顆 sensor 同時 Down」），`Magnitude` 不變。折疊只在「availability down 成立」時發生；沒有 availability sensor 的裝置照舊逐顆發出（那時每顆 sensor 的 down 各自是訊號）。折疊筆數進執行輸出計數。
   - PRTG 已設相依性的情境：被相依暫停的 sensor 在鏡像裡 `paused=true`（結構同步時點的現況），本來就在母體外；折疊處理的是沒設相依性的裝置。
7. **分類看得見**：主機頁 PRTG 區塊的 sensor 列與探測輸出「Sensor Type 分布」各加「分類」欄（未分類顯示「—」）。少了這個，管理者填了補充對照表也無從確認生效，而且探測輸出的未分類 type 正是填表的依據。
8. **分類的其他消費端**：資源守門的自動偵測依 `category` 取 cpu／memory sensor（PRTG-SPEC §12）——補充對照表改動會連帶改變守門偵測結果，設定頁該欄位的說明要寫明，PRTG-SPEC §12 加一句。

### 改動

1. Core：`PrtgDailyPipeline` 母體改全部未暫停 sensor；`PrtgSensorCategories` 加兩值；對照表加 `Ping`；`SystemSettings` 加 `PrtgSensorTypeCategoryOverrides`（含驗證）；`EfPrtgStore` 自動分類套補充表與「auto 列重新套用」。
2. Core：`KnownIssueRule.PrtgSensorCategory`（JSON 相容，無 DDL）；`RuleValidator`；`PrtgRuleEvaluator` 依分類挑規則；`KnownIssueSeed` v7 五條新規則＋既有 down 旗標調整。
3. Web：規則頁 prtg 表單「適用分類」欄位與列表顯示；設定頁補充對照表輸入（多行文字，位置在 PRTG 設定區）。
4. 文件：PRTG-SPEC §2（分類值）、§7（新設定）、§9（母體、分類覆寫、規則表）；RULES-SPEC 規則模型欄位表；DB-SPEC 若 `SystemSettings` 有欄位清單則補。
5. 測試：分類優先於全域、同層多條的警告、silent 不可填分類、補充表覆寫內建、auto 列重套而人工列不動、Ping sensor 的 down 在預設白名單下會命中、seed v7 升級橫幅出現、同裝置折疊（有 availability down 時其他 down 不出、無 availability sensor 時逐顆出）、主機頁 sensor DTO 帶 category。
6. Web／Core：主機頁 sensor DTO 與探測 type 分布輸出加分類；設定頁補充對照表說明含守門影響。

### 測試／驗收

- 白名單為預設值時，Ping sensor 持續 Down 90 分 → 產生 `builtin-prtg-down-availability` finding、日風險「高」；同一情境 traffic sensor → `builtin-prtg-down`、日風險「中」（突變：把 availability 規則停用，Ping 退回全域規則得「中」）。
- 補充對照表寫 `SNMP Custom=hardware` → 該 type 的 auto 列在下一次結構同步後 category 變 hardware；同 type 一列 `category_source=manual`（測試直接種）不變。
- 設定存 `foo=bogus` 被拒且訊息列出合法分類。
- 全量測試綠。

## 批次 C：跨日訊號

### 現況與核對結果

- 四條規則皆單日判定（`PrtgRuleEvaluator.cs:35-200`），無任何「連續 N 日」「第 N 次」語意；PRTG finding 不進 `TrendAnalyzer`（追加點在其後，且 `ChannelCoverage.WasRead` 對 `LogName=PRTG` 在新紀錄上會回 false，硬塞進趨勢層等於永遠暖身）。
- 體檢 `HasSignal` 只看風險／趨勢／關聯（`WeeklyCheckupService.cs:159`），prompt 逐日列 `TopIssues`（`:229`），PRTG finding 會混在事件裡出現。

### 定案

1. **PRTG 路徑自己做跨日標註**，不改趨勢層：規則評估後，對每筆 finding 查近 14 日（不含當日）`lf_top_issues` 同 `EventKey` 的命中日期集合，得到「14 日內第 N 次」與「連續第 M 日」。
   - 資料源選 `lf_top_issues` 而非重放狀態變更：便宜、與門檻變更解耦、且「命中」的定義與當時一致。代價是只有已歸戶主機日的命中算數，規格明寫。
2. 標註寫進 `Detail`（例：「…；近 14 日第 3 次，連續第 2 日」）；**升級條件**：連續 ≥3 日或 14 日內 ≥3 次（兩個常數暫定，放 `PrtgRuleCatalog`），嚴重度升一級（封頂 High，**不改** `ElevatesDayRisk`）。效果：持續三天的 disk warning 從「不拉」變成拉「中」；down 本來就是 High，只加文字。
3. 「什麼算一個」：以 EventKey 為單位（同 sensor 同規則），日期以 `record_date` 計；「一個都沒有」（首次）不加標註、不升級。
4. **體檢**：prompt 把窗口內的 PRTG finding 抽成獨立段「【PRTG 監控訊號】sensor X：窗口內 N 天命中（規則代碼）」，不再混在事件列；閘門 `HasSignal` 不改（PRTG 已透過日風險進來，High 而不拉風險的 finding 靠 C2 升級後也進得來）。
5. **長期 Down 降噪**：連續命中達 `ChronicDays`（暫定 14）日以上的 `down` finding，是「沒人從 PRTG 移除的死 sensor」的機率遠高於新事故——仍發出 finding，但 `ElevatesDayRisk` 關閉、嚴重度不升、`Detail` 註「已連續 M 日，建議在 PRTG 暫停該 sensor 或建立抑制」。C2 的升級與本條的降噪同時成立時以本條為準（連續 14 日既滿足「≥3 日」也滿足「≥14 日」，規格明寫優先序，不留給執行端猜）。筆數進執行輸出。
   - 這條與 A6（已確認）、A2（抑制）是三道各自獨立的降噪，執行輸出分開計數，管理者才分得出是哪一道在起作用。
6. **查詢形狀**：近 14 日命中日期用**每日一次**的批次查詢（傳入當日全部 EventKey 集合），不逐 finding 查；EventKey 不限主機（同一 sensor 換了對應主機仍算同一顆）。

### 改動

1. Core：store 加「依 EventKey 集合＋主機＋日期區間取命中日期」查詢（SQLite／SQL Server 皆可翻譯，`ToQueryString` 測試守住）；`PrtgDailyPipeline` 評估後標註；升級常數進 `PrtgRuleCatalog`。
2. Core：`WeeklyCheckupService.BuildPrompt` PRTG 段。
3. 文件：PRTG-SPEC §9 新增「跨日標註與升級」；DETECTION-SPEC 體檢節補一句。
4. 測試：種 14 日內 2 筆歷史命中 → Detail 含「第 3 次」且 Medium→High（突變：改成 1 筆歷史必須不升級）；連續日計算跨月；連續 14 日的 down → 不拉風險、Detail 含建議（突變：13 日必須照常拉）；體檢 prompt 含 PRTG 段且事件列不重複列 PRTG；歷史查詢對 300 個 EventKey 只發一次查詢（以 `ToQueryString` 或呼叫計數守住）。

### 測試／驗收

- 上述測試綠；歷史命中查詢在兩個後端的 `ToQueryString` 不含用戶端評估警告。
- 全量測試綠。

## 批次 D：跨來源佐證

### 現況與核對結果

- BACKLOG「跨來源關聯規則」的前置 (a)(b) 是為「把 PRTG 評估搬到分析之前」寫的；本輪改在**追加時**做關聯：`HostDayPostProcessor.AttachPrtgFindings` 手上同時有當日紀錄與 finding，AI 待補又等 `prtg-findings-ready` 才判讀，關聯結果趕得上 prompt。
- 儲存訊號集合（disk／Ntfs／storahci）與非預期關機（`Kernel-Power 41`／`EventLog 6008`）判定在 `CorrelationAnalyzer.cs:54,200,215`，是 internal。
- 未回報主機：儀表板計數卡下鑽 `hosts?status=silent`；主機清單無 PRTG 狀態。

### 定案

1. **兩個佐證模式**，Id 進 `CorrelationPatternIds`（抑制走既有 Correlation 目標型）：

| PatternId | 條件（同一主機日） | 嚴重度／旗標 | 文字（暫定） |
|---|---|---|---|
| `prtg-storage-corroborated` | 紀錄含儲存 I/O 訊號簽章（disk／Ntfs／storahci，同【儲存連鎖】集合）**且** PRTG finding 的 sensor 分類為 **hardware**（warning 或 down） | High／**elevates=true** | 【儲存故障雙重確認】事件日誌的磁碟 I/O 錯誤與 PRTG 硬體健康 sensor 同日示警，兩個獨立來源一致 |
| `prtg-capacity-corroborated` | 紀錄含 `srv 2013`（磁碟空間即將不足，seed 既有規則）**且** PRTG finding 的 sensor 分類為 **disk**（warning） | High／elevates=false | 【磁碟容量雙重確認】事件日誌與 PRTG 磁碟可用空間 sensor 同日示警 |
| `prtg-outage-corroborated` | 紀錄含非預期關機（41／6008）**且** PRTG finding 的 sensor 分類為 availability（down 或 flapping） | High／elevates=false | 【失聯獲 PRTG 證實】PRTG 同日觀測到主機失聯，非日誌誤報 |

   - 刻意**不**把「磁碟 I/O 錯誤 ＋ PRTG 磁碟可用空間 warning」當佐證：一個是硬體在壞、一個是空間快滿，兩件不相干的事湊在一起叫「雙重確認」是假佐證，尖銳的使用者一眼看穿。容量對容量、故障對故障，各配各的。
   - `hardware` 分類的 type 名單來自使用者（B2）；名單為空時儲存故障模式自然不命中，執行輸出不另警告（那是環境沒有硬體 sensor，不是錯）。

   - 判定是純函式（Core，輸入 `TopIssues`＋finding 清單），兩條追加路徑與 DB 路徑共用；被抑制的 finding 不參與。
   - 命中時寫 `CorrelationAlerts`／`CorrelationAlertRefs`（含 PatternId），`HasCorrelation` 旗標更新；Pattern 被抑制時進 `SuppressedCorrelationAlerts`。日風險依既有 `ElevatesDayRisk` 語意單向上調，`RiskBasis` 記 PatternId。
   - 儲存訊號與非預期關機的判定**沿用 `CorrelationAnalyzer` 同一組常數**（抽成可共用的判定，不複製 EventId 清單）。
2. **未回報主機的 PRTG 佐證**：主機清單 `status=silent` 的每列加 `prtgHint`，依該主機 `ok` 對應裝置底下**全部未暫停 sensor** 的鏡像現況分四種：`no-map`（無對應）／`down`（任一 availability 類 Down；沒有 availability 類時任一 Down）／`unknown`（全部 Unknown）／`up`（其餘）。清單以 chip 顯示（「PRTG：主機失聯」「PRTG：主機在線，問題在日誌取數端」「PRTG：無資料」「無 PRTG 對應」）；儀表板計數卡 hint 改為「其中 N 台 PRTG 顯示失聯」（N 由同一判定算）。
   - 措辭不寫「日誌管道中斷」：PRTG 的 Up 只證明主機在線，未回報的原因可能在 NetIQ、在本機代理、也可能是主機主檔對應錯，畫面只說事實不下診斷。
   - 只讀鏡像，不打 PRTG；鏡像同步時間 > 2 天時 hint 加「鏡像過期」。
   - **一次查詢**：對整批 silent 主機的裝置一次撈 sensor 現況再於記憶體分組，不逐主機查（兩千台規模的清單頁）。

### 改動

1. Core：`PrtgCorroboration`（純函式）＋ `CorrelationPatternIds` 兩個常數；`CorrelationAnalyzer` 的儲存／關機判定抽成共用；`HostDayPostProcessor.AttachPrtgFindings` 與 `EfAnalysisRecordStore.AttachPrtgFindings` 寫關聯欄位與旗標。
2. Web：`HostAdminService` silent 篩選加 hint；`DashboardService` 計數卡 hint；`hosts.js` chip；`dashboard.js` hint 文字。
3. 文件：DETECTION-SPEC 關聯模式表加兩列（標明「追加時判定」）；PRTG-SPEC §9 新節；WEB-SPEC 主機清單／儀表板對應 §；BACKLOG「跨來源關聯規則」條改寫為已做部分與仍遞延（值型）。
4. 測試：兩模式各一正一反；抑制 PatternId 後進 Suppressed 清單且不拉風險；DB 路徑寫入後 `HasCorrelation=true`；silent hint 四態；鏡像過期註記。

### 測試／驗收

- 種「disk 153 事件＋同日 hardware warning finding」→ 追加後 `CorrelationAlerts` 含儲存故障雙重確認、日風險「高」、`RiskBasis=prtg-storage-corroborated`（突變：finding 分類改 disk 必須**不**命中儲存故障模式；改成 srv 2013＋disk warning 命中容量模式且日風險只到「中」）。
- 種 41 事件＋availability flapping → 關聯文字有、日風險不因此變高。
- silent 清單 100 台主機的 hint 只發一次 sensor 現況查詢（呼叫計數）。
- 全量測試綠。

## 批次 E：校準資料補上後的加強（文件卡位，本輪不實作）

校準四項達「可用」後，依序評估下列項目；每項的觸發條件與資料依據寫在 BACKLOG（本輪改寫該區塊）：

1. **值型規則第一階**（BACKLOG 既有）：基線偏移／磁碟可用空間線性外推／CPU 記憶體持續高檔；門檻由 `ValueSensorSummaries` P90／P99 與 `ValueTypeProfiles` 小時曲線定。本輪 B4 的「依分類覆寫」機制直接可承載值型規則的分類門檻。
2. **四條狀態規則的門檻校準**：以校準檔「規則門檻 (b) 逐日重評分佈」決定 down／flapping／warning 門檻與 B5 暫定值（availability down 30 分、hardware warning 120 分）。
3. **跨日升級常數校準**：C2 的「連續 ≥3 日／14 日內 ≥3 次」以 `AggregatePrtgRuleHits` 的實際分佈校準。
4. **儲存佐證改用數值趨勢**：D1 的儲存模式目前吃 disk warning／down；有基線後改為「磁碟可用空間外推 N 天內耗盡」，佐證提前到 warning 之前。

## 升級注意（管理者角度，收尾時抄進 PRTG-SPEC 與 CLAUDE.md 慣例）

- **必須套用 seed v7**：規則維護頁升級橫幅出現才有五條新規則與 `builtin-prtg-down` 的旗標調整。未套用前四條舊規則照跑（A 的保底），但分類覆寫、折疊的 availability 判定都不會生效。曾手動修改過的 builtin 規則（`ModifiedBy` 有值）不會被自動覆蓋，需勾選才覆蓋——管理者調過的門檻不會被洗掉。
- **行為變更清單**（升級公告要列）：一般 `down` 從拉「高」改為拉「中」；Ping 的 down 開始命中（過去因白名單被排除）；`Down (Acknowledged)` 不再拉高風險；連續 14 日以上的 down 不再拉風險；同裝置多顆 down 折疊為一筆；既有對 `builtin-prtg-*` 建立的抑制**開始生效**（過去建了也沒作用）。
- **既有紀錄不回溯**：舊 finding 的 `RuleId=prtg-*`、`Source=PRTG` 留在紀錄裡，詳情頁對它們掛不出知識庫、排行上短期多一列舊格式「PRTG」，重跑當日才會換成新值，其餘隨保留期消失。
- 補充對照表影響資源守門的自動偵測（B8）。

## 使用者尖銳質疑對照（規劃時預想，收尾時逐條回看有沒有做到）

| 質疑 | 對應定案 |
|---|---|
| 「一台主機死了你給我 37 筆問題」 | B6 同裝置折疊 |
| 「finding 只寫 `prtg:down:12345`，我要自己去 PRTG 查那是什麼」 | A4 名稱與量值進 Detail、prompt、畫面 |
| 「PRTG 上我已經 acknowledge 了，你每天還判高風險」 | A6 |
| 「這顆 sensor 三個月前就壞了沒人拆，天天高風險」 | C5 長期 Down 降噪 |
| 「磁碟快滿跟磁碟 I/O 錯誤不是同一件事，別叫雙重確認」 | D1 容量對容量、故障對故障 |
| 「PRTG Up 不代表日誌管道壞了，別替我下診斷」 | D2 措辭改為只陳述事實 |
| 「規則頁的嚴重度我改了沒反應」 | A1 規則驅動映射 |
| 「我建了抑制沒用」 | A2、A3 |
| 「我填了 type 對照表，怎麼知道有沒有生效」 | B7 分類欄可見 |
| 「你到底降了多少噪音，還是把真事故一起吃掉」 | A8 執行輸出逐道計數 |

## 明確不做（本輪定案）

- 不把 PRTG finding 塞進 `TrendAnalyzer`／`SlowTrendAnalyzer`（`WasRead` 對 PRTG 頻道會永遠暖身；跨日語意由 C 在 PRTG 路徑自做）。
- 不把 PRTG 評估搬到分析之前（D 在追加時做關聯即可，不破壞失敗隔離與並行）。
- 不做 sensor 逐顆人工分類畫面（B3 的 type 補充對照表已能覆蓋環境差異；逐顆 UI 觸發條件維持 BACKLOG 原文）。
- 不做值型規則（E）。
- 未回報主機的 PRTG 佐證只讀鏡像，不即時打 PRTG。

## 規劃完成後複檢

- **與既有設計的衝突**：PRTG-SPEC §9「規則只看白名單內的 sensor，與數值取數同一個母體」被 B1 推翻，文件改寫並註明理由；§9 finding 風險表「任一帶 ElevatesDayRisk（目前只有 down）」改為依規則庫；「不走 `KnownIssueCatalog.Classify`」維持（A1 是吃規則物件，不是走 Classify）。
- **批次間衝突**：A 改評估器輸入形狀、B 再加分類挑選——同一介面兩次變動，規格檔要在 A 就把「輸入＝規則清單」定好，B 只加挑選邏輯；D 的 availability／disk 判定依賴 B 的分類，順序固定 A→B→C→D。
- **什麼算一個／分母為零**：C 的「第 N 次」以 EventKey＋日期計、首次不標；D2 沒有 availability 類 sensor 時退回「任一 Down」；B4 同層多條規則的處置寫明。
- **破壞性判準反例**：B3 補充表對 auto 列重套——若使用者把 type 從補充表移除，該 type 的列會退回內建表結果或 null，規格明寫且不視為資料遺失；人工列（非 auto）永不動。
- **單向閘門**：seed v7 橫幅未套用前四條舊規則照跑（A 的保底：規則庫有規則就用規則庫，沒有才用 catalog）。
- **移除類依賴方**：`PrtgRuleThresholds` 與 `enabledRuleCodes` 參數被規則清單取代——呼叫點只有 `PrtgDailyPipeline` 與校準（`CalibrationService` 以最低門檻重評，`:595,888`），校準端要跟著改成傳「最低門檻的規則清單」，白名單要含它。
- **升級路徑**：既有紀錄的 `RuleId=prtg-*` 不回寫；詳情頁對舊值查不到知識庫屬預期，文件註明。
- 複檢新增事項：校準端的呼叫點（上一條）原本沒列進 A 的改動，已補。
- **第二輪複檢（整體／程式／使用者／管理者四角度）新增**：A6 已確認狀態、A7 抑制下游、A8 執行輸出計數、A9 白話說明反查多條規則、A10 描述不嵌數字、B6 同裝置折疊、B7 分類可見、B8 守門連動、C5 長期 Down 降噪、C6 批次查詢、D1 假佐證修正（儲存故障改配 hardware、新增容量模式配 srv 2013）、D2 措辭與一次查詢、「升級注意」與「尖銳質疑對照」兩節。
- **降噪三道的優先序**（A2 抑制 → A6 已確認 → C5 長期）與 C2 升級的互動寫在 C5；折疊（B6）發生在評估器輸出前，其餘三道作用在已發出的 finding 上，順序固定：折疊 → 跨日標註／升級／長期降噪 → 已確認 → 抑制。
- **實作中新發現（2026-09-16，寫規格時核對）**：
  - `RuleImportPlanner.ContentEqualExceptEnabled` 漏比 `PrtgRuleCode`／`PrtgThreshold`，只改 PRTG 門檻的 seed 更新不會顯示「程式已更新」；既有反射測試只涵蓋複製方法。納入 B-3（補比對＋反射測試）。
  - 主機頁 PRTG 區塊 `host-detail.js` 以 `innerHTML` 插入 PRTG 回傳的 sensor 名稱與 type（全站唯一一處），外部字串未跳脫；同表狀態欄把 Down 的 sensor 也顯示綠色「正常」。納入 B-4（改 textContent、帶真實狀態與 device 名稱、加全站守門測試）。
  - PRTG 逐日迴圈由近到遠，回望多日時較舊日期尚未評估，跨日計數會少算。C-1 改為兩段式：先評估全部日期，再逐日標註、標記抑制、發佈。
  - 規格事實：B-1 需要 B-2 的 availability 分類，順序改為 B-2 → B-1 → B-3。
  - 「未回報」判定在 `HostAdminService` 與 `DashboardService` 各寫一份，D-2 收斂成一份。
- **待議（實作中發現，收尾時與使用者確認）**：AI prompt 的事件清單不排除已抑制事件（`AnalysisPromptBuilder` 無任何 `Suppressed` 判斷），PRTG 段則排除——建議事件清單一併排除或加註「已抑制」，屬事件層行為變更，未在本輪擅自改動。
- DETECTION-SPEC「五層偵測」表：規則層與關聯層各補一句 PRTG 來源（規則層：PRTG 規則庫的 prtg 平台；關聯層：追加時的三個佐證模式），收尾時同步。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | agy（gemini-3.8-flash-high） | 通過（809→816，含合約測試類別 846 綠） | grep 驗收、三個突變（已確認不拉風險、已確認判定、同代碼取 Id 最小）皆紅 | (1) 校準端改成只用「已啟用」規則並手抄一份規則複製：升級後未套 seed 時校準分佈變空、B-3 加欄位必漏抄——Claude 改為三條與規則庫無關的最低門檻合成規則；(2) 四檔被改成 LF、`EfIssueAggregateQuery.cs` BOM 被剝——已還原；(3) 驗收 grep `PrtgRuleThresholds` 撞到校準服務同名無關屬性、`"prtg-` 撞到檔名字串，是規格寫太寬，型別與舊 RuleId 實際零殘留；(4) `PrtgFinding.Acknowledged` 帶預設值（規格禁可選參數），影響僅測試建構，暫留 |
| A-2 | agy（gemini-3.8-flash-high） | 通過（66→71；相關類別 90 綠） | grep 驗收全過；突變「pipeline 不呼叫 MarkSuppressed」→ 2 紅（Group 非成員那條本應綠） | 下游事實：案件掛接（`IssueCaseCoordinator.AttachNewDay`）與郵件摘要對事件層的已抑制問題本來就不過濾，PRTG 與之一致，未另寫分支；執行輸出「已抑制」無獨立測試，由 Site 範圍測試間接涵蓋 |
| A-3 | impl-low（Opus 5 low） | 通過（365→377；全套 4152 綠） | grep 驗收全過；突變「白話說明拿掉 PRTG 分路」→ 4 紅、「預覽不篩主機」→ 1 紅 | (1) 規格寫「比照事件層對 Suppressed 的既有處理」是錯的事實：prompt 的事件清單**不**排除已抑制事件；PRTG 段依規格排除，兩段不一致，列入待議；(2) PRTG 預覽計數是 `lf_top_issues` 筆數（Count 恆 1），該表無 rule_id／分類欄，B-3 分類規則的預覽會是「同代碼全部分類」的上限值，列入 B-3 文件說明；(3) `LfDbContext` 同句註解共 3 處一併修正 |
| B-2 | impl-low（Opus 5 low） | 通過（244→274；全套 4182 綠） | grep 驗收全過；突變「分類合法判定恆真」→ 6 紅；執行端自行突變四處皆紅（讀取候選條件單獨拿掉不紅，因寫入前重取再判一次，兩道一起拿掉才紅） | (1) `PrtgFetchService` 原本拿不到設定，補充表改以建構子必要參數傳入，三個正式建構點（PRTG 每日路徑、結構同步、回填）與約 32 處測試建構跟改；(2) 兩個既有測試原用 `ping` 當「未分類 type」，Ping 改歸 availability 後換成其他 type；(3) 分類值一律轉小寫存；啟動時補充表解析錯誤的行靜默略過（設定層存檔已擋）；(4) 執行端覆寫了 scratchpad 同名突變腳本，已確認其突變皆還原，之後指示子代理用獨立子資料夾 |
| B-1 | impl-low（Opus 5 low） | 通過（855→863；全套 4190 綠） | grep 驗收全過；執行端突變「pipeline 加回白名單」「跳過折疊」皆紅；Claude 補突變「校準傳真實分類」→ 1 紅、「折疊結果不套用」→ 4 紅 | (1) 校準的規則門檻分佈仍用取數白名單過濾、且會被同裝置折疊——Claude 改為全部未暫停 sensor、分類刻意傳 null 不折疊（與規則門檻判定讀 `lf_top_issues` 實際命中的口徑一致），移除變成未用的 `settings` 參數並補一條測試；(2) 刪除過時測試「白名單過濾生效」（它在測試內自己模擬舊白名單過濾，已不代表任何正式行為）；(3) 分類為 null 的 sensor 不能當折疊主筆、但會被合併——Ping 分類尚未算出的那一晚該裝置不折疊，屬可接受的保守行為 |
| B-3 | impl-low（Opus 5 low） | 功能完成但全套 17 紅，Claude 修正後通過（1279→1305 相關；全套 4216 綠） | grep 驗收除 `RuleImportPlanner` 計數（`grep -c` 算行數、實際 6 次，規格寫法問題）外全過；執行端突變四處皆紅（含補強 Clone 反射測試後才紅）；Claude 突變「分類規則不優先」→ 4 紅 | (1) **正式碼 bug**：`CalibrationService` 以規則代碼 `ToDictionary` 取門檻現值，v7 同代碼多條規則時丟例外、校準匯出整個失敗——Claude 改為取不限分類規則當現值，`CalibrationPrtgRuleThresholdInfo` 加 `SensorCategory` 欄，匯出列出全部 8 條；(2) 8 條測試仍預期一般 down 拉「高」（v7 刻意改掉）——改用 `builtin-prtg-down-availability` 或改斷言為「中」；(3) 規則頁存分類時統一轉小寫；(4) 重複規則警告改為評估前對整份規則清單每個「代碼＋分類」判一次 |
