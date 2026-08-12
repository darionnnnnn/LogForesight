# 回饋第十七輪實作規劃（FEEDBACK-17-PLAN）

> 來源：外部程式碼審視報告（頭號發現＋中2低2）＋使用者回饋八項（其他 1~8）。
> 全部發現已逐一對照 dev@236f08d 程式碼核實，**無一被推翻**。
> 四個決策點已與使用者定案（見「決策記錄」）。
> 狀態：**規劃完成，未實作**。

## 決策記錄

1. **UI 設計工作套用 ui-ux-pro-max**：已跑 skill 查詢，結論融入批次 F/G/H
   （沿用既有企業藍＋Fira 體系；sticky 目錄留 top 偏移；頁籤 active 態明確；
   表格一律 `overflow-x-auto` 包裹；章節 icon 沿用專案既有 `icons.svg` sprite，不引入新圖示庫）。
2. **本機＋NetIQ 並行本輪就做**（批次 E，本輪唯一架構性變更）。
3. **郵件權限範圍**：信件分「統計行（全站數字，無主機明細）」與「明細行（僅可見主機）」兩層；
   對應到使用者帳號的收件人收統計行＋自己可見範圍的明細行；**對應不到帳號的收件人只收統計行**。
4. **每日摘要「無事時不寄」開關預設＝寄**（存活訊號優先，可關）。

## 總覽

| 批次 | 主題 | 對應項目 | 併 master 閘門 |
|------|------|----------|----------------|
| A | 郵件通知日期修正 | 頭號發現 | **是（擋）** |
| B | 郵件可靠性＋內容＋權限範圍 | 中1、低3、低4＋其他 7、8 | 是（與 A 同批） |
| C | 趨勢層首次出現爆量出口 | 中2 | 否 |
| D | NetIQ 匯入批次化 | 其他 2 | 否 |
| E | 本機＋NetIQ 並行 | 其他 4（後半） | 否 |
| F | 排程作業頁改版 | 其他 4（前半） | 否 |
| G | 說明書版面＋內容＋AI 表格 | 其他 3、5、6 | 否 |
| H | 側欄 brand 圖示回正 | 其他 1 | 否 |
| I | 文件收尾＋全量測試＋體檢輪 | — | — |

---

## 批次 A：郵件通知日期修正（頭號發現）

**根因**：產出端（`MissingDateFinder` offset 從 1 起、`AnalysisOrchestrator` 固定分析
`yesterday`）從不產生「今天」的紀錄；消費端 `NotifyAfterRunAsync(DateTime.Today)` →
`QueryDate` 查 `From=To=今天`。執行後摘要與高風險即時通知**永遠零筆不寄**，
每日摘要（`windowDays:1` → 窗口＝今天）**每天寄一封「無事」假信**。週報涵蓋昨天倖存。

**核實補充**：`DailyAnalysisRecord` 沒有 CreatedAt 類欄位，「查本次執行新產生的紀錄」
無法靠時間戳達成——改用「窗口查詢＋已通知狀態去重」，整個修法留在 Web 郵件層，不動 Core。

### A-1 高風險即時通知：改窗口查詢

檔案：`LogForesight.Web/Services/Mail/MailNotificationService.cs`

- `NotifyAfterRunAsync` **移除 `targetDate` 參數**（呼叫端 `SchedulerHostedService:223`
  不再傳 `DateTime.Today`），內部自算窗口：`From = 今天-14, To = 昨天`。
  14＝立即執行回補天數上限（`run-now-backfill` max），設 `private const int NotifyLookbackDays = 14`。
- `UrgentSentKeys` 去重不變——回補多天產生的高風險日天然被涵蓋、只通知一次。
  去重鍵清理是 `RetentionDays` 截止，存活期 ≥ 窗口，無誤重寄風險。
- XML 註解整段改寫：刪除「簡化假設——批次通常分析今天」（該假設從一開始就與
  `MissingDateFinder` 相反，是本 bug 的源頭），改為說明窗口＋去重語意。

### A-2 執行後摘要：加 `SummarySentKeys` 狀態

檔案：同上＋`LogForesight.Core/Models/MailNotifyState.cs`

- `MailNotifyState` 加 `HashSet<string> SummarySentKeys`（`hostId|yyyy-MM-dd`，
  與 `UrgentSentKeys` 同形狀、同 `RetentionDays` 清理，清理收在既有 `finally` 的
  `_state.Update` 一起做）。
- `SendRunSummaryAsync` 改：同 A-1 窗口查詢 → 過濾 `RiskLevels.Rank >= MailMinRiskLevel`
  → 再過濾「未在 `SummarySentKeys`」→ 有剩才組信；**寄成功才標記**（一封全域信，
  成功＝整批標記；失敗不標，下次執行補寄——與 urgent 的「寧重寄不漏寄」同語意）。
- 語意變為「尚未摘要過的達門檻主機日」：上線首次會補寄一封涵蓋近 14 天未摘要日的信，
  屬正確行為（那些日子確實從未被通知過），不做特殊抑制。
- 明細行按日期分組顯示（一次執行可能補多天），行數上限沿用 `SummaryBodyLineLimit`。

### A-3 每日摘要／週報：窗口右移一天

檔案：同上（`SendDigestAsync`）

- `to = now.Date.AddDays(-1)`，`from = to.AddDays(-(windowDays - 1))`。
  每日摘要＝昨天整天；週報＝近七個完整日（昨天往回 7 天）。
- 信內窗口文字（`{from} ~ {to}`）跟著改，不再把「今天」印進窗尾。

### A-4 「無事時不寄」開關

檔案：`SystemSettings`＋`SendDigestAsync`＋設定頁郵件分頁（`Settings.cshtml`／`settings.js`）

- `SystemSettings` 加 `bool MailDigestSkipEmpty`，**預設 false＝照寄**（決策 4：
  無事信同時是系統存活訊號）。
- `SendDigestAsync`：`qualifying.Count == 0 && settings.MailDigestSkipEmpty` → 直接 return
  （**仍要更新 LastDaily/WeeklySentDate**，否則 60 秒輪詢當天每分鐘重進一次）。
- 設定頁郵件分頁加開關＋說明文字（「關閉後，期間內無達門檻風險日時不寄摘要信」）。

### A-5 綁定測試（防兩端約定再漂移）

檔案：`LogForesight.Tests/MailNotificationServiceTests.cs`＋新測試

- **既有測試全面把紀錄日期從 `DateTime.Today` 改為 `DateTime.Today.AddDays(-1)`**——
  現行測試構造了生產環境永不出現的狀態（今天的紀錄），是本 bug 逃過 1828 條測試的原因。
- 新增綁定測試：用真實 `MissingDateFinder.Find`（今天永不在清單內）產生日期 →
  對每個日期造 High 紀錄 → 呼叫 `NotifyAfterRunAsync` → 斷言 urgent 與摘要都有寄出。
- 新增反向測試：只有「今天」的紀錄（理論上不存在的狀態）→ 不寄，佐證窗口右邊界。
- `SendDigestAsync` 窗口測試：昨天的 High 紀錄 → 每日摘要要包含；今天的 → 不包含。

---

## 批次 B：郵件可靠性＋內容＋權限範圍

### B-1 永久失敗收件人熔斷（中1）

檔案：`MailNotifyState`＋`MailNotificationService`＋`/api/health/detail`＋設定頁

- `MailNotifyState` 加 `Dictionary<string, int> RecipientFailureStreaks`
  （key 一律 `ToLowerInvariant()` 正規化——JSON dictionary 無 comparer，序列化落地後
  大小寫敏感，必須在寫入端正規化）。
- 語意：**實際寄送失敗**才 +1；寄送成功歸零；**熔斷（circuit break）跳過的收件人不計**
  （跳過≠失敗，SMTP 整台掛掉不該把所有人的個人串灌爆）。
- 連續達 3 次（`const RecipientFailureThreshold = 3`）：該收件人本輪起跳過寄送、
  **從 urgent coverage 排除**（壞地址不再綁架整批標記），照記 WARN。
- 申報與復原：`/api/health/detail` 列「已暫停收件人」清單；設定頁郵件分頁顯示同一份
  ＋說明；**儲存郵件設定時清空全部 streaks**（改完地址自然復活，不做獨立重置鈕）。

### B-2 通知路徑 N+1 修正（低3）

檔案：`MailNotificationService`

- `SendUrgentNotificationsAsync`／`SendRunSummaryAsync`／`SendDigestAsync` 進入時一次
  `_hosts.GetAll()`／`_users.GetAll()` 建 `hostId → WebHost`、`userId → WebUser` 字典，
  `ResolveHostDisplayName`／owner 解析改吃字典（方法簽章加參數或抽 per-call context 物件）。
  `HostStore.Get` 每呼叫整份 blob 反序列化（`JsonBlobCollection.Read` 無快取），
  1000 筆 pending 就是 1000 次整份讀取——與十四輪 A3 修掉的形狀相同。

### B-3 信件內容廣泛化（其他 8）

檔案：`MailNotificationService`（`SendRunSummaryAsync`／`BuildUrgentMessage`）

- 移除明細行的 `Headline`、單筆時的 `RiskBasis`（「判定依據」段整個刪）。
- 保留：主機名（含顯示名）、日期、風險等級、錯誤／警告數量——「數量＋告警等級等
  較廣泛的資訊」。
- 主旨模板 `{summary}`：單台 urgent 目前直接放 Headline → 改通用文字「偵測到高風險訊號」；
  多台與摘要的計數式 summary 不變。

### B-4 收件人權限範圍（其他 7）

檔案：`MailNotificationService`＋`IVisibilityService`

**信件雙層設計（決策 3）**：

- **統計行**：全站數字（「N 台主機達 High 風險以上」「期間內 M 個主機日達門檻」），
  不含任何主機名——粒度粗到所有收件人都可看。
- **明細行**：僅列該收件人**可見**的主機（`IVisibilityService.GetVisibleHostIdsFor(userId)`，
  群組授權 ∪ 負責人，與站內同一套規則、同一個 API，不另造第二份權限邏輯）。

**對應規則**：

- 收件人 email → `IUserStore` 以 `OrdinalIgnoreCase` 比對 `Email` 欄位解析帳號；
  對應到多個帳號取聯集？——**不**，取第一個 Active 帳號（email 理應唯一，重複屬設定
  錯誤，記 WARN）。停用帳號視為未對應（停用優先於一切授權路徑，與 `VisibilityService` 一致）。
- 對應到帳號：統計行＋可見範圍明細行；可見集合為空 → 只有統計行（不寄空明細）。
- 未對應（共用信箱等）：只收統計行。

**三路信各自的改動**：

- 執行後摘要／每日／週報：從一封全域信 → **逐收件人組信**（沿用 urgent 的
  per-recipient 迴圈、保序與熔斷）。信量從 1 → 收件人數（通常個位數）。
  週報的未處理數是全站統計 → 統計行層級，所有人保留。
- urgent：全域收件人原本看「全部 pending」→ 改為可見範圍內的 pending；
  負責人路徑本來就只列自己負責的主機，不變。
- **coverage 語意調整**：record 的涵蓋者＝實際收到它「明細」的收件人。
  一筆 record 若無任何收件人可見（coverage 為空）→ 在「本輪至少一封統計信寄送成功」時
  照樣標記已通知——否則它永遠 pending、每輪重算白做工；統計行已如實反映它的存在。
- A-2 的 `SummarySentKeys` 標記同理：所有「涵蓋它明細的信」全寄成功才標記；
  coverage 為空者隨統計信成功標記。

### B-5 測試

- B-1：連續 3 失敗剔除、成功歸零、熔斷跳過不計、設定儲存清空。
- B-4：對應／未對應／停用帳號三態；可見過濾（A 只看得到 host1 → 明細只有 host1）；
  coverage 為空的標記語意；統計行數字不因過濾而變。
- B-3：信文不含 Headline/RiskBasis 字串。

---

## 批次 C：趨勢層「首次出現且爆量」出口（中2）

檔案：`LogForesight.Core/Analysis/TrendAnalyzer.cs`＋`TrendAnalyzerTests`

- 十六輪 B-1 的爆量例外只掛在 Rising 分支（:211）；New 分支（:147-172）只看
  `Severity >= High`，Other 類恆 Low → 未知簽章第一天 500 筆完全靜音。
- New 分支的 `else`（`sig.Trend = IssueTrend.New` 後）加例外：High 告警條件未命中且
  `sig.Count >= SurgeMinCount && !channelWarmingUp` → 告警
  「首次出現且大量：{label}（{severity}）今日 x{count}，近 N 日可靠歷史中從未發生」。
  首次出現無基準可乘，**只用絕對量門檻**（`SurgeMinCount = 100`），不用 `SurgeFactor`。
- **`channelWarmingUp` 閘門必須保留**——否則新頻道上線第一天每個 ≥100 筆簽章都告警，
  正是暖身期要防的切換日風暴。
- 不動嚴重度、不設 `ElevatesDayRisk`（與 Rising 爆量例外對齊：爆量例外只負責「被看見」，
  告警文字產生後 `ComputeRuleBasedRisk` 的 trendAlerts>0 → 中風險，已足夠）。
- `sig.Suppressed` 分流進 `suppressedAlerts`，`alertRefs` 同步（與既有兩處相同的三行模式）。
- 測試：500 筆告警／99 筆不告警／暖身期不告警／已抑制進 suppressedAlerts／
  High 首次出現不重複告警（走既有 High 分支，不進爆量例外）。
- `docs/DETECTION-SPEC.md`「Low 簽章趨勢出口」小節補這條出口。

---

## 批次 D：NetIQ 匯入批次化（其他 2）

**瓶頸核實**：`NetiqImportApplier.Apply` 對每台勾選主機 `FindByName`（整份 blob 讀）＋
`Upsert`（整份讀改寫），勾 500 台＝上千次整份 JSON 序列化往返＋DB roundtrip，
每次都在 `Mutate` 互斥鎖內。這就是匯入慢的主因（掃描本身是網路面，另計）。

### D-1 `IHostStore.MutateBatch`＋三態邏輯純函式化

檔案：`IHostStore`／`HostStore`／`NetiqImportApplier`／`FakeHostStore`

- `IHostStore` 加 `TResult MutateBatch<TResult>(Func<List<WebHost>, TResult> mutation)`
  （文件註明：僅供批次匯入／批次維護類操作，單筆操作走既有方法）。
  `HostStore` 實作＝直接轉呼叫基底 `Mutate`（一次讀改寫完成整批）。
- `NetiqImportApplier.Apply` 簽章不變，內部改為
  `hosts.MutateBatch(list => ApplyToList(list, ...))`；三態判定（復活／更新／新增）抽成
  **static 純函式 `ApplyToList(List<WebHost>, ...)`** 對 list 就地操作＋自配 HostId
  （`NextId` 邏輯在批內遞增）。
- **FakeHostStore 直接對內部 list 執行同一個純函式**——真 store 與測試替身共用單一邏輯，
  堵住「欄位漂移在測試替身」的既有 bug 家族形狀。
- 匯入完成 log 加耗時（`Log.Info` 台數＋毫秒），之後有沒有改善看得見。

### D-2 同型迴圈順帶收斂

檔案：`SentinelAdminService`

- 刪除 Sentinel 的孤兒化迴圈（每台 `Upsert`）→ 一次 `MutateBatch`。
- `SyncHostDisplaySnapshot` 改名快照同步迴圈 → 一次 `MutateBatch`。

### D-3 驗證

- 既有 `NetiqImportApplierTests` 全綠（行為零改變，只換執行形狀）。
- 加測試：批次內新增多台時 HostId 不重號（批內 `NextId` 遞增正確性）。

---

## 批次 E：本機＋NetIQ 並行（其他 4 後半，架構性變更）

**現況**：`AnalysisOrchestrator` 嚴格先 `RunLocalAnalysisAsync`（本機 1 台）再 NetIQ。
本機回補多天大量事件時，NetIQ 空等。**可行性依據**：NetIQ pipeline 本來就
`Parallel.ForEachAsync` 多 worker 並行寫共用 store（records／案件掛接／風險暫存／
runRecorder），本機主機與 NetIQ 主機集合不重疊——把本機當成「多一個並行 worker」。

### E-1 執行結構

檔案：`AnalysisOrchestrator`

- `Scope != RunScope.NetiqHosts` 時：`Task.WhenAll(RunLocalAnalysisAsync(...), NetIQ 段)`。
  `Scope == NetiqHosts` 維持只跑 NetIQ；沒有 NetIQ 主機時等同只跑本機。
- 收尾步驟（體檢、歷史清理、總結輸出）在 WhenAll 之後，順序不變。
- 失敗語意：任一路拋例外＝整趟失敗（維持現行嚴格），已寫入的另一路結果保留
  （冪等，下次補跑）。WhenAll 聚合例外取第一個，其餘記 log。

### E-2 console 輸出交錯

- 兩路共用 `WebRunConsole` 會交錯。本機路徑所有輸出行加「[本機] 」前綴
  （NetIQ 路徑既有 per-Sentinel logContext 前綴不動），交錯但可讀、即時、誠實。
  不做 buffer 後一次輸出——執行詳情的價值就是即時看到卡在哪。

### E-3 進度回報多軌化

檔案：`SchedulerRunState`／`RunDtos`／`runs.js`

- 現況單一 `ProgressPhase/Done/Total` 假設階段線性，並行後兩 phase 交錯 report 會讓
  進度條跳動。改為 **per-phase 進度字典**（`phase → (done, total)`），DTO 帶多筆，
  runs.js 逐 phase 顯示一列（本機 x/y、NetIQ a/b）；主進度條顯示合計。
  既有 AI 佇列子進度軌語意不變（仍是附屬軌）。

### E-4 取消與優雅停止

- 兩路共用同一 `runCts.Token`，`TryCancel` 行為不變（各自停在主機日邊界）。
  實作時逐一確認本機迴圈與 NetIQ 迴圈的 ct 檢查點都在（現況已各自有）。

### E-5 併發安全確認清單（實作前逐一過）

- `BatchRunRecorder` 計數器（NetIQ 多 worker 已並行呼叫 → 應已安全，確認）。
- `IssueCaseCoordinator.AttachNewDay`／`riskyEventStore.ReplaceDay`（同上）。
- `IAnalysisRecordStore` 寫入路徑（同上）。
- `WebRunConsole`／`WebRunProgress`（NetIQ 已並行寫 → 確認）。
- 唯一新增的併發對：本機 worker vs NetIQ workers——資料面不重疊（hostId 不同），
  風險集中在上列共用元件，全部是 NetIQ 並行已踩過的路。

### E-6 測試

- Orchestrator 整合測試：本機＋NetIQ 皆完成且紀錄齊全／本機失敗 NetIQ 成功＝整趟失敗
  但 NetIQ 紀錄保留／取消時兩路都停。
- `10-scheduler.md` 說明書同步（執行順序描述改並行）。

---

## 批次 F：排程作業頁改版（其他 4 前半）

### F-1 三區塊改子頁籤

檔案：`Runs.cshtml`／`runs.js`

- 執行總表／異常彙總／**執行紀錄（新）** 三頁籤，沿用設定頁既有頁籤模式
  （nav-tabs 樣式＋URL hash 深連結，與 Settings 頁行為一致）。active 態明確
  （ui-ux-pro-max：Active State 規則）。
- 天數切換鈕移到頁籤列同排右側，作用於當前頁籤（三個頁籤共用天數）。

### F-2 目前執行：開始時間＋耗時

檔案：`ScheduleController`（狀態 DTO）／`runs.js`

- `SchedulerRunState.StartedAt` **已存在**（:23），只是沒出到狀態 API——DTO 加欄位，
  UI 在「目前狀態」卡顯示「開始於 HH:mm:ss・已耗時 mm:ss」，前端每秒計時、輪詢校正。

### F-3 執行紀錄頁籤

檔案：`ScheduleController` 新端點（如 `/api/schedule/runs?days=N`）／`runs.js`

- 資料源 `BatchRunStore`（欄位已齊：StartedAt/FinishedAt/Trigger/Args/DaysAnalyzed/
  AiCalls/AiFailures/WarnCount/ErrorCount/Stopped/ExitCode）。
- 表格欄：開始、結束、耗時、觸發來源（沿用 Runs 頁既有的 Trigger 顯示字典）、
  範圍（Args）、分析日數、AI 呼叫/失敗、Warn/Error、狀態
  （成功／失敗／已停止／執行中＝FinishedAt null 且未逾時／異常中斷）。
- 表格包 `overflow-x-auto`（ui-ux-pro-max：Table Handling）；排序沿用既有 table-sort。
- 點列展開該次執行的 `BatchRunLog`（Warn 以上＋里程碑）——「執行詳情」有處落地。

---

## 批次 G：說明書改版（其他 3、5、6）

### G-1 版面重排（推翻十六輪 E-4）

檔案：`HelpManual.cshtml`／`help-manual.js`／`site.css`

- **刪除 `alignChapterBodyHeight` 整套 JS 量測**（max-height＋內捲動）。內容區自然展開、
  不出現內部 scrollbar、一次顯示全部內容。
- 左欄目錄卡改 `position: sticky`＋top 偏移（不遮內容；≥md 生效，md 以下維持堆疊）。
- 「詢問說明書」卡位置不動（內容下方）。

### G-2 章節目錄 icon

檔案：`help-manual.js`／`manifest.json`／`icons.svg`

- manifest 每章加 `icon` 欄位，目錄項渲染 icon＋標題。對照（實作時以 `icons.svg`
  現有符號優先，缺的自 Bootstrap Icons 補進 sprite）：
  overview=door-open、dashboard=speedometer2、issues=search、record-detail=file-earmark-text、
  handling=check2-square、rules=shield-check、suppression=bell-slash、hosts=hdd-network、
  permissions=people、scheduler=calendar3、imports=upload、settings=gear、
  glossary=book、faq=question-circle。

### G-3 規則維護內容補強（其他 6）

檔案：`HelpContent/06-rules.md`／`07-suppression.md`／`manifest.json` keywords

- 06-rules.md 擴寫：**停用 vs 遮蔽 vs 抑制的語意邊界**（各關掉什麼、各在哪一層生效）、
  matchOrder 比對順序與先搶先贏、規則改版行為、**變更生效時機＝下次執行、
  對既有紀錄不回溯**、內建規則與 custom 的關係。
- 07-suppression.md 補：抑制目標三型（簽章／關聯／音量）、範圍（主機群組／全站）、
  「關的是要不要吵、不是要不要記」＋詳情頁「已抑制的告警」誠實申報區塊的對應關係。
- 內容一律對照現行程式行為寫，不寫期望行為。

### G-4 AI 回答 markdown 表格轉 HTML（其他 5）

檔案：`markdown-lite.js`

- `renderBlocks` 加表格區塊解析：連續 `|` 開頭行＋第二行為 `|---|` 分隔列 → 判定為表格；
  createElement 組 `table/thead/tbody/tr/th/td`（**維持絕不 innerHTML 的安全設計**），
  cell 內容走既有 `appendInline`（粗體／行內代碼照常）。
- 外包 `overflow-x-auto` 容器＋沿用站內既有 table 樣式 class；不合法的偽表格
  （無分隔列）退回段落渲染，不誤判。
- 全站受益：聊天面板／風險日詳情／儀表板／問題查詢／說明書問答共用同一入口。
- 驗證：無 JS 測試框架，以固定範例字串手動實測（正常表格／欄數不齊／
  含 `**粗體**` cell／偽表格四情境），瀏覽器實測截圖。

---

## 批次 H：側欄 brand 圖示回正方形（其他 1）

檔案：`site.css`（`.lf-sidebar__brand*`）

- **成因**：十六輪 E-2 讓 `.lf-sidebar__brand` 用 `align-items: stretch`、標記用
  `min-height`——有副標時文字自然高度（約 60px）把 44px 寬的標記撐成瘦長矩形
  （E-2 註解自己承認「微幅變成瘦長矩形是合理代價」）。`object-fit: contain` 下點陣圖
  不變形，但**無 viewBox 的 SVG 會真的被拉伸**；即使不變形，瘦長的漸層底框視覺上也像拉長。
- 改法：`.lf-sidebar__brand-mark` 加 `aspect-ratio: 1 / 1`＋`align-self: center`，
  拿掉 min-height 撐高；`.lf-sidebar__brand-text` 的 space-between 對齊（E-2 成果）不動。
  登入頁 `.lf-login__brand-mark` 本來就是 3rem 固定方形，不動。
- 實測四情境：有副標／無副標／自訂圖示（SVG 與點陣各一）／長品牌名省略號
  （十三輪 G 的修正不能回歸）。

---

## 批次 I：文件收尾＋全量測試＋體檢輪

- `docs/WEB-SPEC.md`：郵件通知章節改寫（窗口＋去重語意、雙層信件設計、收件人權限）、
  排程作業頁三頁籤、並行執行。
- `docs/DETECTION-SPEC.md`：首次出現爆量出口。
- `docs/DB-SPEC.md`：`MailNotifyState` 新欄位。
- 說明書 `10-scheduler.md`／`12-settings.md` 同步。
- 全量測試綠 → 體檢輪（重點：批次 A/B 的日期與權限語意、批次 E 的併發、
  批次 D 的行為零改變）→ 併 dev。**批次 A＋B 未完成前不併 master**（現況兩條主要
  通知路徑從未寄出、第三條每天寄錯信）。

## 建議實作順序

A → B（同一批檔案、同一組測試，郵件層一次收攏）→ C → D → H → G → F → E → I。
E（並行）放最後的理由：它會動到 F 的進度顯示資料結構，先把 F 的頁籤與執行紀錄落地、
E 再改多軌進度，避免同一支 runs.js 前後改兩次方向。
（若希望降低單輪風險，E 可獨立成 feature 分支最後併入。）
