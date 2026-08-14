# 排程回饋第十二輪規劃（2026-08-07）

> **狀態（2026-08-07）：全案（第 1／2／3／4A／4B／4C／4.6）已全部實作完成＋全案體檢＋文件
> 同步，於 `feature/feedback-12` 分支逐批提交；1640 測試綠（基線 1543）。使用者原始指示
> 「把所有拼圖補上，不留不實作項目」的範圍**已無殘留項目**——Linux 主機從掃描精靈納入、
> 排程／立即執行、Sentinel 取數、五層偵測到 AI 判讀，已與 Windows 主機同一條管線走完整趟，
> 分支待使用者實測後併 dev、再併 master（見 docs/logforesight-git-branch-workflow 慣例）。
>
> 診斷輪 A（樣本 IP 10.xx.45.101）、輪 B（2026-08-07 第三次 probe，樣本 IP
> 10.xx.11.66）、第四次 probe（8g，msg 片語實證）**四輪皆已執行完畢並全數定案**：
> 欄位主形狀、sp term 語意、sev 分佈與 EntryType 門檻、program 量級吵靜分類、collector 形態、
> msg 片語查詢行為、暴力破解訊息格式與 4C regex（見 §4.0 實證表）。
> **4B（Sentinel Linux 取數分支）／4C（SSH 攻擊鏈關聯）／4.6（止血拆除＋體檢＋文件同步）
> 皆已完成**：`SentinelFieldMap`／`SentinelEventMapper.MapLinux`／
> `SentinelQueryBuilder.BuildLinuxFilter`／`LinuxCorrelationAnalyzer` 全數落地並有專屬測試；
> 過渡期止血（1.1/1.2）已拆除；體檢額外揪出並修復一個真實 bug：「詢問 AI 現場取數」對 Linux
> 主機恆用不存在的 `EventId` 欄位查詢，永遠靜默 0 筆（見 `SentinelEventFetchService.BuildQuery`）。
> 全案體檢另揪出一個真實 bug 並已修復：`AiFollowupQueue.EnqueueAsync` 在取消時可能被
> 誤判成「分析失敗」，實際上統計紀錄已成功寫入（見 commit `6c45371`）。
> 對象是使用者實測後的三項回饋：
> ①手動執行排程是否有依「同時處理幾台 Sentinel」並行、
> ②不要因為等 AI 而停下 NetIQ 搜尋、
> ③手動立即執行時 Linux 主機沒有執行——**含 Linux 事件取數管線的根治**（使用者指示：
> 把所有拼圖補上，不留「不實作」項目）。
> 基準：dev 最新（實作前先跑 `dotnet test` 記下基線綠燈數）。
>
> 每一項寫到「改哪個檔、改什麼、為什麼是這個作法、會不會動到別人、怎麼驗收」。

---

## 零、先講整體：三項回饋的定性

| 回饋 | 調查結論 | 本輪處置 |
|---|---|---|
| ① 併行度沒生效？ | **不是 bug**。手動與排程在 `SchedulerHostedService.TriggerRunAsync` 就合流，`NetiqPipelineService.RunAsync` 的 `Parallel.ForEachAsync` 確實吃 `MaxParallelServers`。觀感落差來自：硬上限被夾在 3 但 UI 允許填 8 且不提示；併行粒度是「Sentinel 台數」不是主機數；**AI 呼叫全域單通道，跨 Sentinel 互拖**（與②同病根） | 第 2 批：UI 與驗證上限誠實收斂到 3 |
| ② AI 拖住搜尋 | **屬實**。`NetiqPipelineService.RunBatchDayAsync` 批內逐台 `await` AI；本批 AI 沒跑完，下一個日期的 `SearchAsync` 不會發出。AI timeout 600s × Polly 重試 3 × JSON 重問 2，單一主機日理論可卡數十分鐘 | 第 3 批（主菜）：同一次執行內 producer/consumer 兩階段，搜尋＋統計先跑完，AI 由背景消費者補上。含本機路徑同步脫鉤與 AiPending 孤兒補跑（§3.9/§3.10） |
| ③ Linux 沒執行 | **不是排程 bug**。Linux 規則面（模型／17 條種子／驗證／Web CRUD／匯入）100% 完成且有測試護欄，但取數側是零：`FindLinuxRule` 生產程式碼零呼叫者、`SentinelFieldMap`/`SentinelQueryBuilder` 無 Linux 分支、文件寫的 `EventKey` 簽章與「關聯層不適用申報」**其實都尚未實作**（HISTORY.md:3512 列 P3）。短期落差：預覽台數含 Linux、單機立即執行靜默 no-op | 第 1 批：三項止血（過渡期誠實顯示，第 4 批收尾拆除）＋**第 4 批：Linux 取數管線根治**（4A 不需資料可先做；輪 A 已實證欄位主形狀，4B 的 filter 與 sev 對應待輪 B 量級資料） |

**實作順序**：第 1 批 → 第 2 批 → 第 3 批 → 第 4A → **診斷輪 B（使用者執行）** → 第 4B → 第 4C
（診斷輪 A 已於 2026-08-07 完成，實證見 §4.0）。
第 1/2/3 批互不相依，各自可獨立合併 dev；第 3 批與第 4 批動到管線核心，各自整批完成並體檢。
第 4A 與第 3 批可平行（改不同層），但合併時 4A 排後避免衝突。

---

## 一、第 1 批：Linux 落差止血（過渡期措施，第 4 批收尾拆除）

> 定位變更：原本是長期措施，現在 Linux 取數納入本輪根治，這批變成「取數上線前的窗口期
> 誠實顯示」。仍然先做——4B 卡外部資料閘門，窗口期長短不可控，期間不能留著誤導的 UI。
> 1.1/1.2 在 §4.6 收尾時拆除；1.3 是通用改善，永久保留。

### 1.1 執行前預覽分開回報 Windows／Linux 台數

**檔案**：
- `LogForesight.Web/Controllers/Api/ScheduleController.cs`（`RunPreview` 104~111 行、`ResolveScope` 162~216 行）
- `LogForesight.Web/Models/Dto/`（`RunPreviewDto`）
- `LogForesight.Web/wwwroot/js/pages/runs.js`（735 行附近的預覽訊息）

**現況**：`ResolveScope` 只回 `HostIds`，`RunPreview` 回單一 `HostCount`，不分 OS。
畫面說「目前有 50 台主機符合條件」，實際執行時 pipeline 把 Linux 剔掉只查 42 台——
這個落差只在手動路徑看得見（排程沒有預覽 UI），正是使用者把問題歸咎於「手動執行」的原因。

**改法**：
1. `ResolveScope` 的回傳從 `List<long>? HostIds` 擴充為帶 OS 資訊——最小做法是讓 all/segment
   兩個分支在挑選時同時算出 `linuxCount`（`NetiqTarget` 本來就有 `Os` 欄位，`HostListProviders.cs:12`），
   回傳 tuple 多帶一個 `int LinuxCount`。不新建 DTO 類別、不改 `HostListSelection`。
2. `RunPreviewDto` 加 `LinuxCount`；`HostCount` 語意維持「總數」不變（避免動到既有消費端）。
3. `runs.js` 預覽文案：`LinuxCount > 0` 時改為
   「目前有 N 台主機符合條件，其中 M 台 Linux 主機暫不查詢（Linux 事件取數尚未支援）」。

**為什麼不直接把 Linux 從預覽數扣掉**：預覽的既有語意是「這個範圍涵蓋幾台」，扣掉會讓
主機頁看到的台數跟預覽對不上，又製造新落差；明講「涵蓋但暫不查詢」才符合誠實申報原則。

**影響面**：只有排程作業頁的預覽；`Run` 端點行為不變。

**驗收**：環境中放至少一台 Active 的 Linux NetIQ 主機，scope=all 預覽顯示「其中 1 台 Linux 主機暫不查詢」；
scope=segment 選到含 Linux 主機的網段同樣顯示；純 Windows 範圍時文案與現行相同（不出現 Linux 字樣）。

### 1.2 對單一 Linux 主機「立即執行」明確擋下

**檔案**：
- `LogForesight.Web/Controllers/Api/ScheduleController.cs`（`ResolveScope` 的 `case "host"`，196~211 行）
- `LogForesight.Web/wwwroot/js/pages/host-detail.js`（399 行附近的立即執行按鈕）

**現況**：Linux 主機是 Pollable 的，`scope=host` 一路放行，API 回「已開始執行」，
然後 pipeline 內 `windowsTargets.Count == 0` 直接 return——成功回饋＋零結果，完全靜默。

**改法**：
1. `case "host"` 在既有 Pollable 擋截（206~209 行）**之前**加一段：
   `host.Os == WebHost.OsLinux` 時 `throw DomainException.Validation("「{host.HostName}」是 Linux 主機，事件取數尚未支援（規則面已就緒，待 Linux Sentinel 接入），本次無法執行。")`。
   寫法與訊息口吻比照緊接在後的 Pollable 擋截。
2. `host-detail.js`：載入主機詳情時若 `os === 'linux'`，立即執行按鈕改為 disabled 並附
   title 提示「Linux 事件取數尚未支援」——後端擋截是保險絲，前端停用是主要體驗。

**為什麼在 Controller 擋而不是在 pipeline 內補訊息**：pipeline 是手動／排程共用路徑，
排程自動跑到 Linux 主機時「跳過＋警告」是正確行為（1.3 讓它可見）；只有「使用者針對
單一 Linux 主機主動按下執行」這個情境，明確拒絕才是對的——兩個語意不同，不能混在同一層。

**影響面**：只影響 `scope=host` 且目標為 Linux 的請求；scope=all/segment 涵蓋 Linux 時
維持「跳過＋警告」不擋（否則混合環境永遠無法執行）。

**驗收**：對 Linux 主機的詳情頁——按鈕 disabled；直接打 API `POST /api/admin/schedule/run {scope:"host"}`
回 validation 錯誤且訊息明確；Windows 主機不受影響。

### 1.3 Pipeline 警告上收到執行監控里程碑（永久保留）

**檔案**：`LogForesight.Core/Service/AnalysisOrchestrator.cs`（`RunNetiqAnalysisAsync` 內 708~711 行）

**現況**：`NetiqPipelineResult.Warnings`（Linux 跳過、Sentinel 失聯、帳密未設定等）只印在
執行詳情 console；710 行的完成 `Milestone` 只有成功／失敗計數。使用者看排程作業頁的
里程碑列表時，完全看不到「有 M 台 Linux 主機沒查」。

**改法**：`netiqResult.Warnings.Count > 0` 時，於既有完成 milestone 之後補一條
`runRecorder.Milestone($"⚠ 本次有 {count} 項警告：{前兩條逐字}…（完整清單見執行詳情）")`——
彙整成一條，不逐條灌爆里程碑列表（警告可能隨 Sentinel 數量成長）。

**影響面**：只多一條 milestone；`BatchRunRecorder`／`RunMonitorService` 介面不動。

**驗收**：含 Linux 主機的 scope=all 手動執行，排程作業頁該筆執行的里程碑出現警告條目；
純 Windows 且無異常時不出現。

---

## 二、第 2 批：併行度上限誠實收斂到 3

**檔案**：
- `LogForesight.Core/Models/NetiqOptions.cs`（56~64 行，`MaxParallelServers`）
- `LogForesight.Core/Service/AnalysisOrchestrator.cs`（119 行，`MaxParallelServersInWeb`）
- `LogForesight.Web/Models/Dto/NetiqDtos.cs`（263~265 行，`[Range(1, 8)]`）
- `LogForesight.Web/Views/Pages/Netiq.cshtml`（76~83 行，`max="8"`）
- `LogForesight.Web/wwwroot/js/pages/netiq.js`（234 行填值）
- `LogForesight.Web/Services/NetiqOptionsService.cs`（讀取端）

**現況**：維護頁允許填 1~8，執行時 `ResolveParallelism` 夾在
`AnalysisOrchestrator.MaxParallelServersInWeb = 3`（理由：分析與網站同行程，平行度太高拖慢前景畫面）。
夾住時只在執行詳情印一行提示，設定頁完全不知情——「調了 8 卻只跑 3」就是①的觀感來源之一。

**改法**（統一上限為 3，單一常數來源）：
1. 把上限常數搬到 Core 的設定模型：`NetiqOptions.MaxParallelServersLimit`（`public const int = 3`），
   `AnalysisOrchestrator.MaxParallelServersInWeb` 改為引用它（或直接刪除、呼叫端改引新常數）——
   attribute 參數需要編譯期常數，`internal const` 跨專案引用不到，這是搬家的原因。
2. `NetiqDtos` 的 `[Range]` 上限、`Netiq.cshtml` 的 `max`、欄位說明文字全部改 3，
   說明文字明講理由：「分析與網站同一個行程，平行度上限 3，避免拖慢前景畫面」。
3. `NetiqOptionsService`（或 store 的 `Get`）讀取時 clamp：既有已存 4~8 的環境載入後顯示 3，
   否則表單載入 8、瀏覽器 max=3 驗證會卡住整張表單存不了檔。
4. `NetiqPipelineService.ResolveParallelism` 保留原樣——它是執行期的最後防線（防手改 DB blob）。

**為什麼收斂而不是提示**：上限 3 是行程架構決策（docs/SCALE-FIX-PLAN-2026-08-06.md 已定案
「排程維持在 Web 行程內」），短期內不會放寬；UI 允許填一個永遠不會生效的值，
再多提示都只是把矛盾轉嫁給使用者。哪天拆出獨立 worker 再把上限一起放寬。

**影響面**：`ResolveParallelism` 行為不變（既有單元測試 `NetiqPipelineServiceLookbackTests` 不動）；
已存 >3 的環境行為不變（本來就只跑 3），只有顯示值變誠實。

**驗收**：維護頁欄位 max=3 且有理由說明；先在 DB 塞 `maxParallelServers: 8` 再開頁，顯示 3 且可正常存檔；
`[Range]` 拒絕 API 直打 4；補 `NetiqOptionsService` clamp 的單元測試。

---

## 三、第 3 批（主菜之一）：AI 與 NetIQ 搜尋脫鉤

### 3.1 設計總覽

```
現況（批內逐台 await，AI 把搜尋整個拖住）：
  date1 ─ Search(50台) ─ 主機1[統計+AI] → 主機2[統計+AI] → … → 主機50[統計+AI]
  date2 ─ Search(50台) ─ …          ↑ 這個 Search 要等上面全部 AI 跑完
  （且全部 Sentinel 的 AI 共用 AIService 內 SemaphoreSlim(1,1)，平行度 3 對 AI 無效）

目標（同一次執行內 producer/consumer）：
  主線（快）：Search → 五層確定性偵測 → 統計結果先寫入（AiPending=true）→ 推進佇列 → 下一批/下一天
  消費者（慢，單一背景 Task）：取件 → 重讀歷史 → 前置掃描 → 主分析 → 深析報告 → AttachAiResult 寫回
  RunAsync 結束定義 = 所有 Sentinel 搜尋完成 且 佇列清空（AI 仍在同一筆 BatchRun 內）
```

**關鍵語意決策**：
- **同 run 內消化，不做跨執行背景佇列**：`BatchRunRecorder` 是 `using` 綁定 run 生命週期
  （`AnalysisOrchestrator.cs:178`），AI 移出 run 外會讓執行監控的「AI 呼叫 N（失敗 M）」統計歸零；
  且原始 log 不落地，跨執行佇列必須新增資料表——都是本輪不需要付的成本。
- **單一消費者、FIFO**：`AIService` 的 semaphore 本來就把 AI 序列化成單通道（保護 AI API），
  多消費者沒有意義；FIFO 又天然保證「同一台主機的日期遞增順序」——這讓消費者在
  **取件當下重讀歷史**（`store.ReadRecent`）就能拿到前一天已附掛的 AI 摘要，
  `AnalysisPromptBuilder` 的隔日脈絡引用（249 行 `h.AiAnalyzed && Summary`）語意**完全不降級**。
- **低風險免 AI 日不入列**：現行 `skipAiForLowRisk` 的日子（四層無訊號、未分類事件不多）
  在階段 1 就直接寫入定案紀錄，與現行完全一致、零 AI 呼叫——佇列只裝真正需要 AI 的主機日，
  2000 台規模下絕大多數日子不進佇列，這是 AI 時間預算成立的既有前提，不能破壞。

### 3.2 佇列與工作項

**新檔案**：`LogForesight.Core/Service/AiFollowupQueue.cs`（名稱可再議，與 pipeline 同目錄）

- `System.Threading.Channels` 的 **bounded** `Channel<AiWorkItem>`，容量常數建議 `200`。
  有界的理由：工作項帶著該主機日的 mapped events（深析報告需要原始 log 摘錄，而 log 不落地，
  只能隨件攜帶），無界佇列在 AI 大幅落後時就是 OOM 候選——本專案才在規模化輪被 OOM 咬過
  （docs/SCALE-ISSUE-FIRST-PLAN.md S2），不再開第二個口子。滿載時 `WriteAsync` 背壓讓搜尋暫停，
  屬「AI 落後 200 個主機日」的極端情況，記憶體保護優先。
- `AiWorkItem`（record）內容——階段 1 已算好、階段 2 直接用，不重算：
  `HostPlan`（含 Target 與 Store）、`date`、`issues`、`errorCount/warningCount/auditCount`、
  `trendAlerts`、`correlations`、`ruleRisk`、`riskBasis`、`uncoveredChecks`、`dataIncomplete`、
  `securityLogAvailable`、`channels`、`events`（mapped，供深析報告）、`activeSuppressions`。
  **歷史不入件**——消費時重讀（見 3.1 關鍵決策）。

### 3.3 `LogAnalysisService` 拆分

**檔案**：`LogForesight.Core/Service/LogAnalysisService.cs`（169~301 行 AI 段）

**改法**：`AnalyzeDayAsync` 拆成兩個公開入口，統計邏輯零複製：
1. 既有 `AnalyzeDayAsync` 保留簽章，內部重整為「統計段」＋「AI 段」兩個私有方法——
   `useAi=false` 情境呼叫它，行為與現行完全相同。
2. 新增 `BuildStatisticalRecordAsync(...)`：只跑統計段，回傳 `(DailyAnalysisRecord record, AiEligibility eligibility)`，
   其中 eligibility 帶著 3.2 工作項所需的中間產物；「需要 AI」的判準沿用現行條件
   `useAi && (!lowRisk || tailIssues.Count >= MinTailForLowRiskScreening)`。
   需要 AI 時 record 以統計內容寫入且 `AiPending=true`、headline 用中性文案（見 3.5）。
3. 新增 `CompleteAiAsync(AiWorkItem, ct)`：消費者用。內容＝現行 169~258 行的 AI 段
   （前置掃描 → 主分析 → 三種降級分支）＋深析報告產出，回傳 `AiOutcome`
   （headline/summary/trendAssessment/action/riskLevel/riskBasis/aiAnalyzed/screening 欄位/reportFile/deepDives）。
   歷史在此方法內以 `store.ReadRecent` 重讀後餵給 prompt builder。

**深析報告的時機**：AI 入列的主機日，階段 1 **不**產報告檔（現行是風險中以上就產）；
報告統一在 `CompleteAiAsync` 內、AI 定案後產出——避免同一天先產統計版再被 AI 版覆蓋的
檔案管理問題。代價：執行中途被取消的入列項沒有報告檔（紀錄本身完整），由 §3.10 的
孤兒補跑補主分析（報告檔仍從缺，誠實標示）。低風險免 AI 日不受影響（本來就不產報告）。

### 3.4 `NetiqPipelineService` 改造

**檔案**：`LogForesight.Core/Service/NetiqPipelineService.cs`

1. `RunAsync`（95 行）：開場建立佇列與單一消費者 Task；`Parallel.ForEachAsync` 結束後
   `channel.Writer.Complete()`，`await` 消費者收尾，再印總結（169 行）——總結行加上
   AI 段統計（完成／失敗／因取消放棄件數）。
2. `AnalyzeHostDayAsync`（317 行）：改呼叫 `BuildStatisticalRecordAsync`；寫入與
   `HostDayPostProcessor.AttachCase`／`ReplaceRiskyEvents`／`TouchNetiq` 維持在階段 1
   （三者只依賴 TopIssues 與 events，AI 不改這些）；`RecordAiCallIfApplicable` **移到消費者**
   （AI 呼叫發生在階段 2，計數跟著走）；需要 AI 時 `await queue.WriteAsync(item)`。
3. 消費者迴圈：逐件 `CompleteAiAsync` → `store.AttachAiResult(...)`（3.5）→
   `runRecorder.RecordAiCall(...)` → 進度回報。單件失敗只記警告與失敗計數，
   沿用現行「任一步失敗不讓該主機日作廢」的失敗邊界哲學（record 已在階段 1 落地）。
4. `NetiqPipelineResult` 加 `AiQueued`／`AiCompleted`／`AiAbandoned` 計數（Interlocked，
   照既有並發慣例）；進度條見 3.7。

### 3.5 寫回：`AttachAiResult` 與 `AiPending` 狀態

**檔案**：
- `LogForesight.Core/Models/DailyAnalysisRecord.cs`（新欄位 `AiPending`，預設 false）
- `LogForesight.Core/Persistence/IAnalysisRecordStore.cs`＋兩個實作（Sql 的 `EfAnalysisRecordStore`、
  以及測試替身；比照 `AttachWeeklyCheckup` 的既有樣板，`EfAnalysisRecordStore.cs:110~128`）
- `LogForesight.Web/Models/Dto/RecordDtos.cs`、`RecordDetailQueryService.cs`、`RecordListQueryService.cs`
- `LogForesight.Web/wwwroot/js/pages/record-detail.js`（263~283 行 badge）、`records.js`（508~518 行灰字）

**`AttachAiResult(DateTime date, AiOutcome outcome)`**：讀 row → 反序列化 ContentJson →
覆寫 Headline/Summary/TrendAssessment/Action/RiskLevel/RiskBasis/AiAnalyzed/
ScreenedTailCount/ScreeningNotes/ReportFile/DeepDives、`AiPending=false` → 重新序列化 →
**同步更新抽出欄 `row.RiskLevel`**（AI `ai_raise` 拉高等級時，清單／排行／儀表板讀的是抽出欄，
漏更新就是先前「欄位漂移」bug 家族的重演）→ `SaveChanges`。找不到列時比照
`AttachWeeklyCheckup` 記 Warn 安靜略過。`lf_top_issues` 不需更新（AI 不改 TopIssues）。

**`AiPending` 的顯示語意**（避免「排隊中」被誤讀成「失敗」）：
- 執行進行中（前端已有 run 狀態可查）＋ `AiPending=true` → 「AI 分析中」（新 badge，中性色）。
- 無進行中執行 ＋ `AiPending=true`（執行被取消留下的孤兒）→ 「統計模式（AI 未完成）」，
  下次執行由 §3.10 補跑。
- `AiPending=false` → 現行兩態不變（AI 產出／統計模式）。
- 階段 1 的暫代 headline 用「（統計已完成，AI 分析排隊中）」，與既有統計模式文案區隔。

### 3.6 取消與失敗語意

- **`AIService` 補 `CancellationToken`**（`LogForesight.Core/Analysis/AIService.cs`，
  `ChatAsync`/`ChatJsonAsync` 142 行起）：現況簽章沒有 ct，使用者按「停止」只能停在
  日期邊界，掐不斷進行中的 AI 呼叫（最壞 600s×重試）。貫穿到 Polly `ExecuteAsync` 與
  `HttpClient` 呼叫；所有既有呼叫端（LogAnalysisService／AnalysisPromptBuilder／
  RiskReportService／WeeklyCheckupService／WebAiService）跟著傳遞。這一項獨立成 commit，
  對現行為零行為變更（不取消時 token 是 default）。
- **單件 AI 失敗**：沿用現行三種降級分支（格式異常保原文／完全失敗記統計模式說明），
  結果照樣 `AttachAiResult` 寫回（`AiPending=false`），計入 `RecordAiCall(failed)`。
- **執行取消**：消費者收到 ct 後停止取件，佇列剩餘項計入 `AiAbandoned` 並印明細行；
  這些主機日的紀錄維持 `AiPending=true`（顯示語意見 3.5），下次執行由 §3.10 補跑。

### 3.7 進度與執行監控

**檔案**：`LogForesight.Web/Services/SchedulerRunState.cs`（28~30 行單一 phase）、
`WebRunProgress.cs`、`RunMonitorService.cs`、`wwwroot/js/pages/runs.js`（phase 文案）

`SchedulerRunState` 一次只存一個 phase，本輪不動這個結構（phase 切換足以表達，
多 phase 並列是 UI 重工，收益不成比例）：
- 搜尋進行中：照舊 `Report("netiq", HostDaysDone, HostDaysTotal)`——分子分母語意不變
  （統計完成即算 done，AI 不在內），進度條不再被 AI 卡住、會順順走完。
- 搜尋全部完成、佇列還有件：切到 `Report("netiq-ai", AiCompleted+AiAbandoned, AiQueued)`，
  `runs.js` 對應文案「AI 白話分析補寫中（X/Y）」。
- 里程碑：完成 milestone（`AnalysisOrchestrator.cs:710`）加註 AI 統計
  「…AI 分析 X 件（失敗 Y、放棄 Z）」。

### 3.8 測試計畫（骨架先行，改動才有安全網）

**現況**：`NetiqPipelineService` 本體零整合測試（既有兩個測試檔只測靜態純函式與計數器原子性）；
`AIService` 是具象類別（`AnalysisOrchestrator.cs:184` 直接 `new`），`SentinelClient` 也是
pipeline 內部 `new`（223 行）——兩者都注入不了假替身。

**順序**（每步獨立可綠）：
1. 抽 `IAiService` 介面（`ChatAsync`/`ChatJsonAsync` 簽章照搬，含新增的 ct），
   `AIService` 實作之；`LogAnalysisService`／`AnalysisPromptBuilder`／`RiskReportService`／
   `WeeklyCheckupService` 建構參數改吃介面。純機械改，行為零變更。
2. `NetiqPipelineService` 建構子加 `Func<Sentinel, ISentinelSearchClient>` 工廠參數
   （預設值＝現行 `new SentinelClient`），`SentinelClient` 已有的搜尋介面若尚未抽出則一併抽。
   ——這個注入點同時是第 4 批 Linux 整合測試的地基，一魚兩吃。
3. 補 pipeline 整合測試骨架（fake client 回固定事件、fake AI 可控延遲/失敗）——先對
   **現行為**釘 2~3 個基準測試（批次查詢→逐台分析→寫入），確認骨架能抓住行為後才動迴圈。
4. 兩階段改造後的新測試：
   - **搜尋不等 AI**：fake AI 掛 `TaskCompletionSource` 永不完成 → 所有日期的 Search 均已發出、
     統計紀錄均已寫入（`AiPending=true`）；放行後 AttachAiResult 全數補上。
   - **FIFO 保序**：同主機三天入列，驗證 day2 的 prompt（fake AI 收到的內容）包含 day1 的 AI 摘要。
   - **AttachAiResult 抽出欄**：`ai_raise` 情境下 `lf_daily_records.risk_level` 與 ContentJson 一致
     （比照全 store 普查輪對欄位漂移的釘法）。
   - **取消**：ct 取消後剩餘件計入 `AiAbandoned`、record 維持 `AiPending=true`、run 正常收尾。
   - **背壓**：容量 2 的佇列塞 3 件不死鎖（消費者取件後 producer 續行）。
5. 受影響的既有測試逐一過：`CaseGrantVisibilityTests`（`AiAnalyzed` 斷言）、
   `RiskReportServiceTests`、`RecordStorageShaperTests`（新欄位 `AiPending` 的 shaping 行為——
   低風險精簡路徑不該把 pending 紀錄的欄位剪掉）、`BatchRunRecorderConcurrencyTests`。

### 3.9 本機分析路徑——評估後定案不做（維持現行同步呼叫）

**原規劃**：本機逐日迴圈改走與 NetIQ 相同的兩階段——`BuildStatisticalRecordAsync` 寫入
＋入列共用佇列，佇列與消費者從 `NetiqPipelineService` 上移到 orchestrator 層級，本機段
與 NetIQ 段共用消費者。

**實作前重新評估後改為不做，理由**：
1. **現行「執行結果總表」（`RunLocalAnalysisAsync` 迴圈結束後的 `══════════ 本次執行結果 ══════════`
   區塊）依賴每筆 `result.LocalResults` 在印出當下就是 AI 定案後的最終內容**
   （`RiskLevel`／`ReportFile`）。改成兩階段會讓這個總表印出時大部分日期還是
   「AI 分析排隊中」的暫代內容，這是本機路徑「一次执行、當場看到完整結果」的既有體驗，
   拆分會讓這個體驗明顯變差，且需要额外設計「總表要不要等 AI 或事後重印」，複雜度
   不亞於重新做一次 §3.4。
2. **本機路徑只有一台主機，不是本輪要解決的問題**。使用者的原始回饋（②）是
   「NetIQ 搜尋被 AI 拖住」——多台 Sentinel、上百上千台主機的搜尋序列化才是真正的痛點；
   本機一次執行只回補少數天數，AI 慢頂多讓「這台機器自己的排程」晚幾分鐘結束，
   不會連鎖拖累其他主機，風險/效益比與 NetIQ 段完全不同量級。
3. 共用佇列會讓 `NetiqPipelineService` 的佇列所有權（現在完全自持、`RunAsync` 內建立/
   收尾）變成由 orchestrator 外部注入，是對已完成並通過測試（§3.4/§3.6 的三個決定性測試）
   的核心元件做侵入性修改，用「次要瓶頸」換「核心元件的迴歸風險」不划算。

**維持現行行為**：`RunLocalAnalysisAsync` 繼續呼叫組合式 `AnalyzeDayAsync`（統計段+AI 段
同步跑完才進下一天），與拆分前逐位一致。若未來本機路徑本身也遇到「AI 慢拖累」的回饋
（而不是現在推測的次要瓶頸），再回頭評估——屆時「執行結果總表要不要等 AI」這個產品
決策應該由實際回饋驅動，不是這輪憑空假設。

### 3.10 AiPending 孤兒補跑（取消自癒）

**檔案**：`LogForesight.Core/Service/NetiqPipelineService.cs`（掃描缺漏日的同一段，199 行附近）、
`LogForesight.Core/Persistence/IAnalysisRecordStore.cs`（查詢 AiPending 紀錄的方法）、
`LogForesight.Core/Service/LogAnalysisService.cs`（由 record 重建 prompt 的補跑入口）

**改法**：每次執行在逐主機算 `missingDates` 的同一個迴圈裡，同時撈出 lookback 窗口內
`AiPending=true` 的既有紀錄，包成「補跑型」工作項入列。補跑型與一般型的差異：
- prompt 輸入**由 record 重建**——`TopIssues`／`TrendAlerts`／`CorrelationAlerts`／計數
  全部持久化在 ContentJson，足以重建主分析 prompt；歷史照常消費時重讀。
- **前置掃描與深析報告不補**（兩者需要原始 log，而 log 不落地）；`AttachAiResult` 時
  `ScreeningNotes` 保留原值、`ReportFile` 維持 null——報告從缺是取消當下已知的代價，
  補跑補的是白話摘要與風險等級，不偽造一份沒有 log 佐證的深析。
- 一般缺漏日工作項優先、補跑項殿後（佇列尾端），不影響當日主線。

**為什麼要做**：3.6 的取消語意若沒有補跑，孤兒紀錄會永遠停在「統計模式（AI 未完成）」
（該日已有紀錄、不再是缺漏日），等於取消一次就永久缺一塊——自癒機制讓取消變成無代價操作。

**驗收**：執行中途取消留下孤兒 → 下次執行後孤兒的 `AiPending=false`、有 AI headline、
`ReportFile` 為 null；執行監控 AI 統計含補跑件數。

---

## 四、第 4 批（主菜之二）：Linux 事件取數管線根治

> 調查定性：規則面 100% 就緒（`KnownIssueRule.Platform`＋三個 Linux 比對欄位、
> `FindLinuxRule` 含測試、17 條種子、Web 三分頁 CRUD、匯入／驗證／遮蔽偵測全支援），
> 缺的全在取數與聚合側，且 `docs/LINUX-RULES.md` §簽章鍵與聚合、§關聯層申報
> 是**設計文，程式碼不存在**（HISTORY.md:3512 列 P3）。本批把設計文變成實作。
>
> 分三波：**4A 不需外部資料**（診斷強化＋簽章聚合＋申報＋顯示面）；
> **4B 主形狀已依輪 A 定案**（`Program=sp`、`repip` 歸屬鍵、投影清單），僅剩 filter
> 內容子句與 sev 門檻待輪 B 量級資料；
> **4C 關聯規則已定案走 msg 解析路線**（`sun`/`sip` 欄位不存在），正則以輪 B sshd 樣本定案。

### 4.0 資料閘門與實證結果

**輪 A——已執行（2026-08-07，Sentinel「118_linux」，https://10.xx.7.118:8443，
樣本 IP 10.xx.45.101）。實證與定案**：

| 問題 | 實證（probe 原文依據） | 定案 |
|---|---|---|
| program 落點 | 每筆事件帶 **`sp`**（`sp=systemd`／`NetworkManager`／`kernel`），且 `msg` 以 `program:` 或 `program[pid]:` 前綴開頭（`msg=systemd: Starting…`、`msg=NetworkManager[1383]: <warn>…`） | **`Program = sp`**；`msg` 前綴解析留作 `sp` 缺席時的 fallback |
| 主機歸屬鍵 | 步驟 8 `repip:10.xx.45.101` found=15576（近 24h） | **與 Windows 同為 `repip`**，`BuildIpClause` 直接重用 |
| 主機名 | `sn=stkomsdb1`／`VM-NATFA02`（回報主機自身名） | `sn` 沿用，DisplayName 回填照舊 |
| 正規化事件名 | `evt="NetIQ Universal Event {program} Event"`——樣板字串，資訊量＝program 本身 | **`evt` 無正規化語意，不使用**；seed 的 `EventNamePattern` **定案維持留空**（Web 端仍可維護此欄，等未來接到有正規化 collector 的環境再啟用） |
| collector 形態 | `pn`＝`agent`＝`port`＝`"NetIQ Universal Event"`、`rt2="Full Text Parser"`；**`sun`／`sip`／`dhn`／`obssvcname`／`rv40` 全部不存在** | 泛用 syslog collector＋全文解析——msg 是未結構化原始 syslog 行；4C 的帳號級關聯只能靠 msg 文字解析（見 §4.5） |
| facility | `rv150=DAEMON`／`KERNEL`（大寫 facility——同名欄位在 Windows 上是頻道名） | 投影帶回但第一版不參與比對；`LogName` 仍照設計固定 `"Linux"` |
| 時間 | `dt` ISO-8601 UTC（`estz=Asia/Taipei` 佐證時區基準） | 與 Windows 同一條解析路徑 |
| 量級 | 全站 **9.46M 筆/24h**（步驟 3）；樣本主機 **15,576 筆/24h**（多為 kernel 分割區雜訊）；`sev:[3 TO 5]` 全站僅 **2,384 筆/24h**；樣本三筆皆 `sev=1`——**含 NetworkManager `<warn>` 訊息** | generic 高嚴重度子句極便宜；**`sev`↔syslog priority 對應存疑**（warn 訊息落在 sev=1），待輪 B 定案；`sp:kernel` 整拉有單 job 100k 截斷風險（樣本主機一台就 1.5 萬/日），filter 需混合下推（§4.4） |
| Windows 事件 | 步驟 7 `rv40:4624/4625` found=0 | 純 Linux Sentinel，證實「同台不混平台」環境事實 |
| ESM 目錄 | 步驟 6 驗證被拒（與 Windows 那台相同） | 主機探索照舊走事件投影 distinct 備案 |
| 批次/分頁 | 步驟 4：100 個 IP 子句接受（~1.7s）；步驟 3：pgsize 1000 於 833ms | 批次機制照 Windows 沿用，上限無虞 |

**輪 B——4A 的診斷強化（§4.1）合併後再跑一次，要補的證據**（每項對應 4B 的一個未決點）：
1. **`sp` 查詢行為**：`sp:kernel` term 查詢 found、大小寫敏感度（`sp:networkmanager` vs
   `sp:NetworkManager`）、前綴萬用字元（`sp:user*`）——決定 program 白名單能否下推 Lucene。
2. **`msg` 片語查詢行為**：`msg:"I/O error"` 之類片語 found 與同義部分詞比對——決定吵雜
   program 能否用 message 關鍵字下推減量。
3. **`sev` 分佈**：`sev:0`~`sev:5` 逐值 found（24h）——定案 `MapEntryTypeLinux` 門檻與
   generic 子句下界。
4. **`sev=2` 與 `sev:[3 TO 5]` 各 3 筆樣本全文**——對照 msg 內的 priority 痕跡
   （`<warn>`／error 字樣），驗證 sev 是否承載 syslog priority。
5. **17 條種子 program 的量級**：`sp:{program}` 逐一 found（24h）——找出吵雜 program
   （kernel／systemd 候選），決定哪些需要 message 下推、`IpBatchSize` 是否需要 Linux 專用值。
6. **`sshd` 事件樣本全文**（近 7 天、10 筆）——定案 4C 的帳號/IP 解析格式
   （「Failed password for … from …」）與 seed v5 的 `MessagePatterns` 校正。

**第二次 probe（2026-08-07，4A 診斷強化合併後首跑，但未填 Linux 樣本 IP）——
輪 B 未達成，另有兩項新實證**：

1. **判定：輪 B 資料仍缺**。8／8b～8f 全數印「略過（未提供樣本 IP）」（§4.1 的設計如預期
   運作——六個新步驟掛同一開關）。**後續動作：到 NetIQ 維護→「診斷」分頁，「Linux 樣本
   IP」欄填 `10.xx.45.101`（輪 A 同一台）重跑一次並貼回完整輸出**。Windows 樣本 IP 留空
   即可（本站無 Windows 主機，步驟 9/11 對此站無意義）。
2. **新實證 A（重要）：同一台 Sentinel 存在第二種 collector 形態，欄位形狀是
   per-collector、不是 per-Sentinel**。步驟 1 的三筆最新樣本（`repip=10.xx.74.41`、
   `sn=VM-PA-SOAR`——SOAR 設備自身的 conmon 日誌）與輪 A 樣本形狀不同：
   - `pn`＝`agent`＝`port`＝**「Universal Common Event Format」**（輪 A 是「NetIQ
     Universal Event」）、`rt2` 同為 Full Text Parser；
   - **`obssvcname=conmon` 存在且值＝syslog program**——輪 A 寫「`obssvcname` 不存在」
     必須修正為「NetIQ Universal Event collector 的事件不存在；CEF collector 的事件有」；
   - 投影傾印中**看不到 `sp`**；`msg` 仍帶 `program[pid]:` 前綴（`conmon[4125835]:`）；
   - `evt` 仍是樣板字串（「Universal Common Event Format conmon Event」）、`rv150=USER`
     （facility）——與輪 A 的「evt 無語意」「rv150=facility」定案一致，不動。

   **對 §4.4 的設計修訂**（已寫回該節）：mapper 的 `Source` 解析改為三段 fallback 鏈
   `sp` → `obssvcname` → `msg` 前綴解析（結構化欄位優先、正則殿後），三路皆失敗計入
   既有的解析失敗警告；`obssvcname` 加入 Linux 投影欄位清單。filter 下推面新增一個
   風險：若受監控主機有事件走 CEF 路徑，`sp:{program}` 白名單子句會漏抓——輪 B 步驟 8
   重跑時**核對樣本主機的 `pn` 與 `sp` 存在性**（輪 A 該台是 NetIQ Universal Event 路徑、
   `sp` 在），若受監控主機全走該路徑則 `sp` 下推安全；否則 program 子句改
   `(sp:X OR obssvcname:X)` 或退回 repip＋sev 不做 program 下推。
   風險定位：`VM-PA-SOAR` 是 SOAR 設備自身、未登錄為受監控主機，pipeline 只查已登錄
   主機的 `repip`，這台的形狀不直接影響；但 4B 實作必須防「`sp` 缺席→program 空字串→
   全部聚成 Other」的靜默降級。**對已完成的 4A 零影響**（EventKey 聚合在 mapper 下游，
   吃的是已映射好的 `EventLogEntryData`，`Source` 怎麼來它不知道也不需要知道）。
3. **新實證 B（次要）**：
   - 步驟 5 的 Lucene 錯誤訊息列出 `<NOT>` 為合法 token——BACKLOG 6b（`NOT` 子句支援）
     的弱正面證據（文法層支援；執行語意仍由既有的 runtime 偵測把關，不據此放鬆）。
   - 步驟 12 在本站無效證：查的是 Windows provider 名（本站不存在），兩者 found=0 是
     「值不存在」不是「斷詞行為」——不需改 probe，Windows 那台已驗過；若輪 B 後決定
     採 `obssvcname` 下推，屆時再小幅擴充 8c 加一行 `obssvcname:conmon` 驗證即可。
   - 量級一致性：9.54M 筆/24h（輪 A 9.46M）、`sev:[3 TO 5]`=2,401/24h（輪 A 2,384）、
     樣本 msg 帶 `<nwarn>` 卻落在 `sev=1`——再次強化「sev 不可靠承載 syslog priority」
     的輪 A 疑點，混合下推＋`MapEntryTypeLinux` 門檻待輪 B 的設計不變。

**輪 B 實證（2026-08-07 第三次 probe，樣本 IP `10.xx.11.66`＝VM-LQLA1，
found=1,661/24h）——六項證據五項定案，一項規劃缺口補 8g**：

| 輪 B 項 | 實證（probe 原文依據） | 定案 |
|---|---|---|
| 1. `sp` 查詢行為 | `sp:kernel`=305,019（term 有效）；`sp:networkmanager`＝`sp:NetworkManager`＝1,855,133（**大小寫不敏感**）；`sp:user*`=7 而 `sp:user`=0、`sp:su`=51,702 而 sudo 另計（**exact term、非子字串**；前綴萬用字元有效） | program 子句可下推，不需處理大小寫；**Lucene term ≠ 本地 Contains**——一律以 `sp:{pattern}*` 前綴萬用字元近似（user→useradd/usermod/userdel、group→groupadd… 皆為前綴關係；「非前綴包含」的殘餘情境接受並記錄，本地 `FindLinuxRule` 仍是唯一判定） |
| 2. `msg` 片語行為 | **未實測——規劃缺口**：§4.1 設計時只把輪 B 第 1 項分給 8c，第 2 項沒有任何步驟對應（是規劃缺口、不是實作漂移）；8f 因 `sp:sshd` 有值也未觸發 msg 退路 | **補新步驟 8g**（§4.1 追加項），第四次 probe 後定案吵 program 的 msg 子句——這是 4B filter 與 4C regex 的最後閘門 |
| 3. `sev` 分佈 | 0=1,866,756、1=7,671,839、2=8、3=972、4=1,403、5=20（合計≈9.54M ✓） | `MapEntryTypeLinux` **定案**：`0~1→Information、2→Warning、3~5→Error`；同時誠實記錄——Linux 的警告數（sev2 全站 8 筆/日）幾乎恆零、錯誤數只反映 sev3-5 的 2.4k/日，計數品質受限於 collector 的 sev 品質，偵測（program＋message 比對）不受影響 |
| 4. `sev` 語意 | `<warn>`（NetworkManager）與 `level=error`（dockerd）都落 sev=1；「pam_unix(crond:session): session opened」落 sev3-5；gkr-pam 警告落 sev=2 | **sev 確定不承載 syslog priority 語意**——只作計數與 generic 網，不作偵測依據；generic 子句定 `sev:[2 TO 5]`（全站 2,403 筆/日，極便宜，比 [3 TO 5] 多收的 sev2 僅 8 筆） |
| 5. program 量級 | **吵**：systemd 1,958,144、kernel 305,019、sshd 244,480（樣本顯示大宗是 SFTP opendir/closedir 與 pam session 雜訊）、sudo 218,945、su 51,702；**靜**：chronyd 3,414、CRON 2,701、auditd 112、smartd 28、`user*` 7、group*/gpasswd≈0、ntpd 0 | 推翻原設計「sshd/sudo 量級小可整拉」——**五個吵 program（sshd/sudo/su/kernel/systemd）一律要 msg 下推**（sshd 一項 244k/日就足以讓 50 台批次撞 100k 截斷線）；靜 program 整拉。子句從規則現算：無 MessagePatterns 的規則產 `sp:{p}*`，有的產 `(sp:{p}* AND msg:(…))`（見 §4.4 修訂） |
| 6. sshd 樣本 | 8f 取到最新 10 筆全是 session/SFTP 雜訊、**無 Failed password 樣本**；但 sev3-5 樣本見外網 IP（203.66.132.63、47.239.13.202）與 Sentinel 自家關聯規則「Large number of authentication attempts…」發動——**環境確實有暴破流量，4C 有真實價值** | 4C regex 樣本改由 8g 的目標查詢取得（`sp:sshd AND msg:"Failed password"` 近 7 天 5 筆全文）；另一實證：**部分 sshd 事件的 msg 沒有 `program[pid]:` 前綴**（snmpd 樣本有、sshd 的 pam/SFTP 行沒有）→ 4C regex 不得錨定前綴、mapper 的 msg 前綴 fallback 也不可假設必然存在（本來就是第三順位，不變） |
| 7.（第二次 probe 追加項）collector 形態 | 樣本主機 10 筆全走 `pn=NetIQ Universal Event`、`sp` 皆在；欄位名聯集（48 欄）**無 `obssvcname`**；步驟 1 順帶看到的其他主機（VM-AppAnalysis、VM-EWTWAA16）也同路徑 | **受監控主機走 sp 路徑，`sp` 下推安全**；`obssvcname` 只保留在 mapper fallback 鏈（CEF 防禦），filter 不用它、8c 不需再擴充 |

**涵蓋範圍誠實申報（輪 B 後新增的 v1 已知限制）**：Linux 取數的檢索範圍＝
「規則 program（前綴萬用）∪ `sev:[2 TO 5]`」——低 sev 且未命中任何規則 program 的事件
（如樣本主機每分鐘一筆的 snmpd 雜訊）**不會被取回**，也就不會進趨勢層的「首次出現」
偵測。這與 Windows 面「Security 未知失敗 ID 不撈」（BACKLOG 未決 #10）同款的檢索面
縮小，隨 4B 文件同步寫入 DETECTION-SPEC 的 Linux 章節，不裝作全量。

**第四次 probe（2026-08-07，8g 首跑）——msg 片語實證完成，資料閘門全數解除**：

| 8g 查詢 | found | 定案 |
|---|---|---|
| `msg:"Failed password"`（24h） | 149 | **msg 片語查詢有效** |
| `msg:"authentication failure"`（24h） | 40 | 同上，sudo/su 關鍵片語可下推 |
| `msg:"I/O error"`（24h） | 10 | **斜線片語有效**——tokenization 不是障礙（環境裡真的有磁碟 I/O 錯誤在發生，10 筆/日） |
| `msg:"oom-kill"`／`msg:oom`（24h） | 0／2 | 無法區分「24h 剛好沒有 OOM」與「連字號斷詞問題」——**不依賴此片語**：真實 OOM 訊息（「Out of memory: Killed process…」）必含已驗證可行的純字片語「Out of memory」「Killed process」，`oom-kill` 留在本地規則、filter 面即使匹配不到也只是無害的空分支 |
| `sp:sshd AND msg:("Failed password" OR "Invalid user")`（7 天） | 977 | **欄位群組多片語語法有效**（≥ 單片語的 725，語意正確） |
| `sp:systemd AND msg:"entered failed state"`（24h） | 1 | **吵 program＋片語組合下推有效且量極小**——systemd 從 1.96M/日壓到 1 筆/日，這條路成立 |
| `sp:sshd AND msg:"Failed password"`（7 天）＋5 筆全文 | 725 | 暴破訊息格式定案：「`Failed password for invalid user {user} from {ip} port {port} ssh2`」——無 program 前綴、`invalid user` 為可選段、來源皆內網 IP（10.yy.2.219／10.zz.2.55，帳號是員工編號式——內部掃描或設定錯誤的用戶端，非外網攻擊，但格式與外網攻擊完全相同） |

**8g 之外的環境觀察（順手記錄）**：本輪 `sev:[3 TO 5]` 樣本出現 Sentinel 自家
Syslog_UDP connector 的「Dropped 29,623 messages so far」——**此 Sentinel 的 syslog
接收端在丟訊息（速率過載）**，代表事件面的完整性在來源端就不保證；我方無從逐主機
偵測這種丟失，屬環境層事實，隨 4B 文件同步記入 NETIQ-API-REFERENCE 供日後排查
「主機明明有事件卻查不到」時參考。connector 自身訊息的 repip 不會匹配受監控主機，
不影響我們的 IP 篩選查詢。

**結論：4B／4C 的全部資料閘門已解除**，可以開始實作（§4.4/§4.5 已依 8g 定案改寫）。

### 4.1 診斷分頁 Linux 深掘強化（4A）

**檔案**：`LogForesight.Core/Service/NetiqProbeRunner.cs`（步驟 8：220~243 行）

**現況限制**：每值截 80 字（`Preview`，374 行）→ `msg` 全文看不到；每步只印 3 筆
（欄位是 per-event 稀疏字典，3 筆蓋不住不同 program 的欄位集）；無欄位名聯集；無自訂查詢。

**改法**（只加不改——檔頭明寫輸出是「貼回對話的純文字契約，不可隨意改寫」，
既有步驟的格式一字不動，新能力全部以新步驟追加；各步驟直接對應 §4.0 輪 B 證據清單）：
1. 步驟 8 樣本數 3 → 10，並在傾印後追加一行「欄位名聯集：{k1}，{k2}，…」
   （10 筆樣本所有鍵的 distinct，缺席欄位看不到的問題靠樣本數緩解＋聯集彙總）。
2. 新步驟 8b：對步驟 8 的每筆樣本，`msg` **全文另行傾印**（不截斷、一筆一段），
   供 `MessagePatterns` 子字串校正——只有 `msg`，其他欄位維持 80 字截斷。
3. 新步驟 8c：`sp` 查詢行為實證（輪 B 第 1 項）——`sp:kernel`（term found）、
   `sp:networkmanager` vs `sp:NetworkManager`（大小寫）、`sp:user*`（前綴萬用字元），
   比照步驟 12 對 `obssvcname` 的既有寫法逐一印 found。
4. 新步驟 8d：`sev` 分佈（輪 B 第 3 項）——`sev:0`~`sev:5` 逐值 found（近 24h），
   一行一值；再取 `sev:2` 與 `sev:[3 TO 5]` 各 3 筆樣本、`msg` 全文傾印（輪 B 第 4 項）。
5. 新步驟 8e：種子 program 量級（輪 B 第 5 項）——對 17 條種子的 program 清單
   （從 `KnownIssueCatalog.Rules` 的 linux 規則現取，不硬編）逐一 `sp:{program}` 查
   found（近 24h），一行一 program。
6. 新步驟 8f：`sshd` 樣本（輪 B 第 6 項）——`sp:sshd` 近 7 天、10 筆，`msg` 全文傾印；
   found=0 時改試 `msg:sshd` 並明講改用了退路。
7. 新步驟一律掛在「有填 Linux 樣本 IP」條件下；未填時各步印「略過（未提供樣本 IP）」，
   維持既有明講慣例（8c/8d/8e 實際不需要 IP，但跟著同一開關走，避免 Windows 環境的
   診斷輸出被 Linux 段落稀釋）。

**驗收**：對 Windows Sentinel 跑一輪診斷（不填 Linux 樣本 IP），既有 13 步輸出與現行
逐字相同（契約不破）；填 Linux 樣本 IP 時出現 8（擴充）/8b~8f 新段落。

**實作完成**（2026-08-07）：`Step` 新增字串標籤多載（既有 13 個整數編號步驟透過
`index.ToString()` 轉呼叫，輸出格式零改變），8b~8f 全部掛在 `sampleLinuxIp` 同一個
開關下、未填時六行皆印「略過」。8e 的種子 program 清單改由 `KnownIssueCatalog.Rules`
現取（Distinct，目前 17 條種子聚成的相異 program 少於 17，如兩條 ssh 規則共用
`sshd`），不是規劃文字寫的「17 條逐一查」，屬同一份資料的忠實反映，非落差。

**刻意不做**：NetIQ ProbeRunner 沒有既有的單元測試（直接 `new SentinelClient(server,
settings)` 連真實網路，不像 `NetiqPipelineService` 有 3.8-2 的工廠注入點可替換假
client）。為了讓這支純人工診斷工具變得可測而額外引入 DI 注入點，超出本次「只加不改」
的範圍且與這支工具的定位（貼回對話的人工核對契約，不是自動化管線）不成比例，故不做，
維持原本零測試覆蓋的現況——與 §3.9（本機路徑同步脫鉤）同一種「評估後決定不做」的
成本效益判斷。

**輪 B 後追加規劃（4B.0，已實作 2026-08-07）——新步驟 8g「msg 片語查詢行為＋暴破樣本」**：
補上 §4.0 輪 B 第 2 項在原設計中漏掉的對應步驟（8c 當時只給了第 1 項），同掛
「Linux 樣本 IP」開關、未填印「略過」、一行一結果：

1. `msg:"Failed password"`（近 24h found）——基本片語有效性；
2. `sp:sshd AND msg:"Failed password"`（近 7 天）found ＋ **5 筆 msg 全文**——
   兼作 4C regex 的實際樣本（8f 的最新 10 筆全是 session/SFTP 雜訊撈不到）；
3. `sp:sshd AND msg:("Failed password" OR "Invalid user")`（近 7 天 found）——
   欄位群組多片語語法有效性（found 應 ≥ 第 2 項）；
4. `msg:"authentication failure"`（近 24h found）——sudo/su 規則的關鍵片語；
5. `msg:"I/O error"`（近 24h found）——**斜線 tokenization 邊界**（kernel 規則）；
6. `msg:"oom-kill"` 與 `msg:oom`（近 24h found 各一行）——**連字號 tokenization 邊界**；
7. `sp:systemd AND msg:"entered failed state"`（近 24h found）——吵 program 片語下推
   組合有效性（systemd 整拉 1.96M/日絕不可行，這條子句是 systemd 規則能否留在檢索
   範圍的關鍵）。

執行程序：實作 8g（小幅、只加不改）→ 建置測試 → 請使用者對 118_linux 帶同一個
樣本 IP 第四次執行診斷並貼回 → 依結果定案 §4.4 第 3 點的 msg 子句與 §4.5 的 regex
→ 4B 主體實作開跑。個別片語若實測不可靠（found 明顯低於預期或語法被拒），該規則的
子句退成 `sp:{p}*` 整拉：kernel（305k/日）以縮小 `IpBatchSize` 控量；systemd 若片語
不可行則**該規則退出檢索範圍並在文件申報**（1.96M/日無論如何不能整拉——寧可誠實縮小
涵蓋，不可截斷汙染整批 DataIncomplete）。

### 4.2 事件模型與簽章聚合（4A，實作 LINUX-RULES.md §131-146 的設計文）

**檔案**：
- `LogForesight.Core/Models/EventLogEntryData.cs`（新欄位）
- `LogForesight.Core/Analysis/LogAggregator.cs`（89~129 行聚合、192~195 行 KeyDetails）
- `LogForesight.Core/Analysis/KnownIssueCatalog.cs`（`Classify` 334~353 行）
- `LogForesight.Core/Models/IssueHandling.cs`（`IssueSignatureKey.For` 57~63 行）
- `LogForesight.Core/Analysis/TrendAnalyzer.cs`（157 行 `SameIssue`）、`SlowTrendAnalyzer.cs`（95 行）
- `LogForesight.Core/Models/RiskyEvent.cs`（23~26 行鍵）

**改法**：
1. **不新增 `EventName` 欄位**（輪 A 定案變更）：`evt` 在此環境是
   「NetIQ Universal Event {program} Event」樣板字串，無正規化語意——加欄位就是
   沒有寫入者的死欄位（HISTORY 當初對 EventKey 延後的同一個教訓）。`FindLinuxRule` 的
   `eventName` 參數保留（介面不動、既有測試不動），aggregator 呼叫時傳 `null`；
   未來接到有正規化名的 collector 再補欄位與寫入者。`Program` 也不另設欄位——
   沿用文件定案 `Source=program`。
2. `LogIssueSignature` 加 `EventKey`（string，預設空），**語意較 LINUX-RULES.md §131-146
   簡化**：只在「規則命中」時填規則 Id，未命中留空——正規化名層在此環境不存在（見上），
   `{program}/{priority}` 層與既有四元組重複（`Source` 已是 program、`EntryType` 已在鍵中），
   填了只是把同一群事件換個名字。EventKey 要解的核心問題不變且仍然成立：
   **同 program 多規則**（ssh-bruteforce 與 ssh-accept 同為 `sp=sshd`）在簽章、處理狀態、
   案件鍵上分得開。偏離設計文的部分回寫 LINUX-RULES.md（§4.6）。
3. **聚合鍵擴為五元組** `(LogName, Source, EventId, EntryType, EventKey)`；
   `IssueSignatureKey.For` 只在 `EventKey` 非空時附加 `|{eventKey}` 尾段——
   **Windows 事件 EventKey 恆空，既有鍵字串一字不變，零遷移、零相容處理**。
   `TrendAnalyzer`／`SlowTrendAnalyzer` 的 `SameIssue` 與 `RiskyEvent` 鍵同步擴充
   （同樣「空即不比」語意）。
4. `LogAggregator` 加 Linux 分路（以 `LogName == "Linux"` 判定）：**逐事件**先呼叫
   `KnownIssueCatalog.FindLinuxRule(Source, eventName: null, Message)`（生產程式碼
   第一個呼叫者），命中者 `EventKey=規則 Id`，再以五元組聚合；TopIssue 直接帶著
   逐事件比對到的規則（分類／嚴重度／處置建議），**不再走 `Classify`→`FindRule`**
   （後者顯式排除 Linux，維持現狀，Windows 路徑零改動）。
   ——為什麼逐事件而不是聚合後比對：`MessagePatterns` 比對的對象是訊息全文，
   聚合後只剩 SampleMessages（且低風險日會被 shaper 剪掉），先比對後聚合才不漏。
4. `ShouldExtractKeyDetails` 維持只認 Windows 三頻道（Linux 的 KeyDetails 本來就無此語意），
   加註解明示是刻意。
5. **`ChannelCoverage.WasRead` 對 `LogName="Linux"` 必須回 true**——否則慢速趨勢層會
   靜默整批略過 Linux 簽章（調查發現的地雷）；加回歸測試釘住。

### 4.3 關聯層申報與顯示面（4A）

**檔案**：
- `LogForesight.Core/Service/LogAnalysisService.cs`（`AnalyzeDayAsync` 簽章、`BuildUncoveredChecks` 372 行）
- `LogForesight.Core/Analysis/CorrelationAnalyzer.cs`
- `LogForesight.Core/Service/RiskReportService.cs`（476 行 `FormatIssue`）、`SlowTrendAnalyzer.cs`（83 行告警文字）
- `LogForesight.Web/wwwroot/js/pages/record-detail.js`、`records.js`（問題列顯示）

**改法**：
1. `AnalyzeDayAsync`／`BuildStatisticalRecordAsync` 加 `hostOs` 參數（預設 windows，
   本機路徑不變）；`hostOs=linux` 時 `BuildUncoveredChecks` 追加一條
   「關聯層（攻擊鏈/故障鏈比對）不適用於 Linux 主機——本版僅規則層＋趨勢層＋慢速趨勢層」
   ——文件（BACKLOG/LINUX-RULES）寫「固定申報」但程式不存在，這裡把它變成真的；
   `UncoveredChecks` 的三個既有消費端（報告／console／詳情頁）零改動自動生效。
   4C 完成後這條文案改為「Linux 關聯層僅涵蓋 SSH 攻擊鏈」（誠實申報涵蓋範圍，不是拿掉）。
2. `CorrelationAnalyzer` 對 Linux 事件短路（`hostOs=linux` 時不執行，或入口處濾掉
   `LogName=="Linux"`）——現行分析器無 Platform 概念，Linux 事件餵進去會被 Windows
   事件 ID 群組靜默誤讀。
3. 顯示劣化修正：`FormatIssue`／慢速趨勢告警文字／前端問題列，`EventId==0 && EventKey 非空`
   時顯示 `{Source}（{EventKey}）` 取代 `{Source} EventId 0`——這是 HISTORY P3 清單裡
   「詳情頁 program 顯示」項的落地。

### 4.4 Sentinel 取數分支（4B，欄位定案後實作）

**檔案**：
- `LogForesight.Core/Analysis/SentinelFieldMap.cs`（Linux 常數＋`MapEntryType` Linux 分支）
- `LogForesight.Core/Analysis/SentinelEventMapper.cs`（依 OS 分路）
- `LogForesight.Core/Analysis/SentinelQueryBuilder.cs`（`BuildLinuxFilter`）
- `LogForesight.Core/Service/NetiqPipelineService.cs`（141~152 行擋板拆除、252 行 filter 分路、
  263 行投影欄位分路）
- `LogForesight.Core/Analysis/KnownIssueSeed.cs`（seed v5）

**改法**（主形狀已依輪 A 定案；標「輪 B」者為僅剩的未決值）：
1. `SentinelFieldMap` 加 Linux 段常數（輪 A 定案＋第二次 probe 修訂）：`LinuxProgram = "sp"`、
   `LinuxFacility = "rv150"`（投影帶回、第一版不參與比對）、
   `LinuxQ1ProjectionFields = repip, sn, sp, obssvcname, rv150, dt, sev, msg`
   ——不含 `evt`（樣板字串不值頻寬）、不含 `rv40`／`sun`／`sip`（Linux 事件無此欄，
   輪 A 實證）；**`obssvcname` 納入**（第二次 probe 修訂：CEF collector 路徑的事件
   `sp` 缺席、program 落在 `obssvcname`，見 §4.0 新實證 A——短欄位，頻寬成本可忽略）。
   `MapEntryTypeLinux(sev)` **定案（輪 B 第 3/4 項）**：
   `0~1→Information、2→Warning、3~5→Error`——輪 B 實證 sev 不承載 syslog priority
   語意（`<warn>`/`level=error` 都落 sev=1、session opened 落 sev3-5），此對應是
   「計數用途的務實選擇」而非語意還原；規則比對不依賴 EntryType（program＋message），
   誤差只影響錯誤/警告計數（Linux 警告數幾乎恆零、錯誤數只反映 sev3-5，已在 §4.0
   輪 B 表誠實記錄）與 generic 收集範圍，風險可控。同一模板訊息的 sev 觀察上一致
   （dockerd warning/error 同落 1），五元組簽章被 EntryType 拆裂的風險低，列入 4B
   體檢觀察項。
2. `SentinelEventMapper.MapAll` 加 `os` 參數分路：Linux 產出
   `LogName="Linux"`、`Source` 走**三段 fallback 鏈**（第二次 probe 修訂）：
   `sp` →（缺席時）`obssvcname` →（再缺席時）`msg` 前綴 `program:`／`program[pid]:`
   正則解析 → 全部失敗退空字串**並計入既有的解析失敗計數**（`totalSkipped` 同款警告
   機制，防「program 靜默變空→全部聚成 Other」的降級不被看見）；結構化欄位優先、
   正則殿後——CEF 路徑的樣本三個來源都有值且一致（`obssvcname=conmon`＝msg 前綴），
   鏈的順序只影響取值成本不影響結果。其餘：`EventId=0`、`Message={msg 全文}`
   （保留 program 前綴，比對與顯示都用得上）、`EntryType=MapEntryTypeLinux(sev)`；
   `dt` 解析失敗整筆略過的既有語意共用。
3. `SentinelQueryBuilder.BuildLinuxFilter(ips, rules)`：形狀比照 `BuildWindowsFilter`
   （空 IP 擲例外、`BuildIpClause` 重用），內容子句採**混合下推**（依輪 B 定案版）：
   `{IP 子句} AND ({規則子句聯集} OR sev:[2 TO 5])`——
   - **子句產生規則（8g 後定案版）**：以**吵 program 常數集** `{sshd, sudo, su, kernel,
     systemd}`（輪 B 第 5 項量級實證的環境事實，hardcode 為附註解的常數）分兩型——
     (a) 規則的 `ProgramPattern` 屬吵集 → `(sp:{p}* AND msg:("片語1" OR "片語2" …))`
     （片語＝該 program 全部規則的 `MessagePatterns` 聯集；8g 已實證片語查詢、斜線片語、
     欄位群組多片語、吵 program＋片語組合全部有效，systemd 從 1.96M/日壓到 1 筆/日）；
     (b) 其餘規則（靜 program，含無 `MessagePatterns` 的帳號異動類）→ `sp:{p}*` 整拉
     ——靜 program 量級最大不過 chronyd 3.4k/日，整拉順便**避開片語標點的殘餘風險**
     （chronyd 的「Can't synchronise」帶撇號、CRON 的「(CRON) ERROR」帶括號，
     這些不需要冒險下推）。前綴萬用字元語意（輪 B 第 1 項）：`sp` 是 exact term、
     大小寫不敏感，`user`→useradd 這類前綴關係靠 `*` 補上；
   - **不依賴 `oom-kill` 片語**（8g：found=0 無法排除斷詞問題）：真實 OOM 訊息必含
     已驗證的「Out of memory」／「Killed process」，`oom-kill` 留在本地規則、filter 面
     即使空匹配也無害；
   - 下推永遠是**超集需求**（本地 `FindLinuxRule` 仍是唯一判定）；
   - generic `sev:[2 TO 5]`（輪 B 第 3/4 項定案）：全站 2,403 筆/日，極便宜，是
     「未知 program 高 sev 事件」唯一的檢索通道；
   - **總量評估（8g 後）**：靜 program 整拉 ~6.3k/日＋sshd 暴破面 ~150/日＋sudo/su
     失敗 ~40/日＋kernel 片語 ~10/日＋systemd ~1/日＋sev 網 2.4k/日≈**全站不到 1 萬筆/日**，
     再經 repip 批次切分後遠低於 100k 截斷線——**`IpBatchSize` 維持 50 共用，
     不需要 Linux 專用常數**；
   - **檢索涵蓋限制**（輪 B 後新增，見 §4.0 誠實申報段）：低 sev 且未命中規則 program
     的事件不在檢索範圍，隨 4B 文件同步寫入 DETECTION-SPEC；
   - ~~CEF 路徑風險~~ **已解除**（輪 B 第 7 項：受監控主機全走 `sp` 路徑，欄位聯集
     無 `obssvcname`）——`sp` 下推安全；`obssvcname` 僅留 mapper fallback 鏈。
4. `NetiqPipelineService`：**拆掉 Windows 擋板**——`RunServerAsync` 依 `target.Os`
   把 targets 分成兩組（同一台 Sentinel 依環境事實只會有單一 OS，但程式不依賴此假設），
   filter builder／投影欄位／mapper 分路，批次與逐日結構共用；`IpBatchSize` **定案維持
   50 共用**（8g 後總量評估：檢索面全站不到 1 萬筆/日，遠低於截斷線，不需要 per-OS
   常數）；類別註解「只支援 Windows」同步改寫。1.1/1.2 的止血同步拆除（§4.6）。
5. 掃描精靈：`BuildSubnetProbeFilter`（129~134 行）寫死
   `rv150:System OR rv150:Application` 頻道子句——Linux Sentinel 上主掃描必然 0 台
   （輪 A 實證 `rv150` 在 Linux 承載 facility：`DAEMON`／`KERNEL`）。
   依 `Sentinel.Os` 分支：linux 時內容子句退回 `sev:[0 TO 5]`；
   `NETIQ-DISCOVERY-PLAN-2026-08-06.md:208` 預留的退路正式落地並把 Linux 情境明寫進文件。
6. ~~seed v5~~ **評估後定案：不校正 `MessagePatterns` 內容，不遞增 `Version`**（2026-08-07）。
   逐條核對 8g／四輪 probe 的真實樣本後，**沒有找到任何與現有 `MessagePatterns` 矛盾的證據**——
   `ssh-bruteforce` 的樣本「`Failed password for invalid user 1838651 from … ssh2`」同時
   驗證了「Failed password」與「Invalid user」兩條既有片語（大小寫不敏感比對，樣本裡的
   小寫 "invalid user" 命中規則的 "Invalid user"）；kernel 家族的「I/O error」經 8g 直接
   查證有效（found=10/24h）；其餘片語（authentication failure／oom-kill 等）沒有反證，
   只是未逐一取樣，不構成「錯誤」。`Version` 是「內容有變才遞增」的訊號（Web 端「內建規則
   升級」據此提示既有部署要不要重新匯入）——沒有實質內容差異卻遞增版本號，只會讓現有
   部署收到一個「有更新」但比對起來什麼都沒變的空歡喜提示，比不遞增更誤導。
   `KnownIssueSeed.cs:17-19` 版本註解的「16 條」已隨批 4A 文件同步修正為 17（見
   docs/BACKLOG.md／docs/LINUX-RULES.md 的既有記錄），此處不重複。

### 4.5 Linux 攻擊鏈關聯（4C）

**檔案**：`LogForesight.Core/Analysis/CorrelationAnalyzer.cs`（或獨立的 Linux 關聯類別）、
`LogForesight.Core/Analysis/KnownIssueSeed.cs`

**改法**：補 BACKLOG 記載的【SSH 暴力破解→得手】關聯（與 Windows【破解得手】同構）：
同日 `builtin-linux-ssh-bruteforce` 簽章計數達門檻 ＋ `builtin-linux-ssh-accept` 簽章出現。

**輪 A 已定案走「msg 解析」路線**：此 collector 是 Full Text Parser，`sun`／`sip` 欄位
不存在，帳號/IP 只存在於 msg 文字中。好在 sshd 的訊息格式是上游 OpenSSH 寫死的穩定格式
（「`Failed password for [invalid user] {user} from {ip} port …`」、
「`Accepted password|publickey for {user} from {ip} port …`」），適合正則抽取：
- **細版**（優先）：從 ssh-bruteforce／ssh-accept 命中事件的 msg 抽 `{user}`／`{ip}`，
  做「同帳號或同來源 IP」的精確關聯——失敗堆與成功登入同源才告警，誤報最低。
  **正則已依 8g 實際樣本定案（2026-08-07 第四次 probe）**——失敗面樣本格式：
  「`Failed password for invalid user 1838651 from 10.yy.2.219 port 54500 ssh2`」，
  無 program 前綴、`invalid user ` 為可選段：
  - 失敗：`Failed password for (?:invalid user )?(\S+) from (\d{1,3}(?:\.\d{1,3}){3}) port \d+`
  - 成功：`Accepted (?:password|publickey) for (\S+) from (\d{1,3}(?:\.\d{1,3}){3}) port \d+`
    ——成功面訊息在四輪 probe 都未直接取樣到（8f 只見 pam session opened），格式取
    OpenSSH 上游標準模板；若此環境 sshd 的 LogLevel 不記 Accepted 行，ssh-accept 簽章
    會恆零、關聯永不觸發——**誠實不誤報的行為**，試點時核對即可，不值得為此再跑一輪 probe。
  **regex 不得錨定行首前綴**（輪 B 實證部分 sshd 事件的 msg 沒有 `program[pid]:` 前綴）。
  另輪 B 已證實環境有真實暴破流量（8g 樣本的來源全是**內網 IP**、帳號為員工編號式——
  內部掃描或設定錯誤的用戶端；sev3-5 樣本另見外網 IP 203.66.132.63/47.239.13.202——
  兩種形態都存在，格式相同），4C 的價值不是理論性的。
- **個別事件解析失敗時降級**：解析不出帳號/IP 的事件落入主機級計數（粗版語意），
  告警描述明講「部分事件無法解析帳號，已以主機級比對」——不因格式漂移靜默漏報。
命中時 `ElevatesDayRisk` 語意比照 Windows 關聯鏈（拉高當日風險）。
完成後把 4.3 的申報文案改為涵蓋範圍申報（見 4.3）。

### 4.6 收尾：止血拆除、周邊確認、文件失準修正（2026-08-07 全部完成）

1. ✓ **拆除 1.1/1.2**：`ResolveScope` 回傳 tuple 拿掉 `LinuxCount`、`RunPreviewDto.LinuxCount`
   刪除、`scope=host` 的 Linux 擋截整段移除、`runs.js`／`host-detail.js` 按鈕與文案恢復；
   1.3 的警告 milestone 保留（通用機制）。體檢：全文 grep 確認無測試鎖定
   `ResolveScope`/`RunPreview` 的 Linux 專屬行為，拆除後全套件 1640 測試 0 失敗。
2. ✓ **無回報告警自然痊癒確認**：grep 確認 `BuildSilentHosts` 無 OS 分支——Linux 主機開始
   被查詢後 `TouchNetiq` 自然生效，「未回報主機」計數卡不再恆列 Linux 主機，不需要額外程式碼。
3. ✓ **體檢清單**（逐項獨立覆核，非僅沿用規劃文的原始判斷）：
   - 風險日詳情頁 OS 徽章：`RecordDetailQueryService.HostOs = host?.Os`→
     `record-detail.js` 渲染 Windows/Linux 徽章，正確。
   - 主機頁 OS 篩選：`hosts.js` 的 `osFilter` 正確接入查詢參數，正確。
   - 規則頁抑制主機下拉：已依規則 `Platform` 過濾，正確。
   - 週末體檢：純數字統計，無 OS 分支，不受影響。
   - 案件掛接：`IssueSignatureKey.For` 全站 ~15 處呼叫皆用含 `EventKey` 的多載，Linux
     簽章各自成鍵不撞鍵，零漂移。
   - 詢問 AI 即時取數：**體檢揪出真實 bug**——`SentinelEventFetchService` 不分 OS 一律用
     `EventId`（`rv40`）組 filter，Linux 事件無此欄位，現場取數對 Linux 主機從未真正運作過
     且無任何錯誤提示。抽出 `BuildQuery` 純函數，Linux 分路改用 program 子句，
     新增 3 個測試，已修復並納入全套件驗證。
4. ✓ **文件失準修正**（調查發現，順手歸零）：
   - ✓ `docs/BACKLOG.md:63`／`docs/LINUX-RULES.md:148`：「固定申報關聯層不適用」寫成已實作，
     實際是 P3 未做——**已隨 4A 文件同步修正**（4.3 已落地，改為現在式）。
   - ✓ `docs/RULES-SPEC.md:7`：指向已不存在的 `Service/RuleImporter.cs`——**已隨 4A 文件
     同步修正**為 `Analysis/RuleImportPlanner.cs`。
   - ✓ `KnownIssueSeed.cs:17-19`：v4 註解「16 條」→ 17 條——**已隨 4A 文件同步修正**
     （seed v5 時版本號再遞增）。
   - ✓ `docs/NETIQ-API-REFERENCE.md` §4a：「`sun`／`sip`／`dhn`／`obssvcname`／`rv40` 全部
     不存在」的絕對敘述失準——**已改寫為 per-collector 敘述並補 CEF 形態列**，同時整節
     擴充為四輪 probe 的完整定案證據表。
   - ✓ 額外發現並修正（原檢查清單未列，體檢時一併處理）：`DETECTION-SPEC.md`／
     `LINUX-RULES.md`／`BACKLOG.md`／`WEB-SPEC.md`／`NETIQ-DISCOVERY-PLAN-2026-08-06.md`
     共五份文件仍殘留「待輪 B」「待批 4B 落地」「只支援 Windows」等批 4B/4C 完工前的敘述，
     一併同步至實作現況（sev→EntryType 映射表也一併修正——原表設想 syslog priority 文字，
     四輪 probe 證實 `sev` 不可靠承載該語意，改記錄實際採用的計數用途映射）。

### 4.7 測試計畫

- **4A**：`LogAggregator` Linux 分路（逐事件比對→EventKey 聚合；ssh-bruteforce 與
  ssh-accept 分成兩個簽章）；`IssueSignatureKey` 相容（Windows 鍵字串與現行逐字相同——
  這條是升級安全的關鍵釘）；`TrendAnalyzer`/`SlowTrendAnalyzer` 五元組 `SameIssue`；
  `ChannelCoverage.WasRead("Linux")`；`BuildUncoveredChecks` Linux 申報；
  `FindLinuxRule` 經 aggregator 的整合命中（17 條種子逐條，比照既有
  `Linux規則各自宣告的比對路都能命中自己` 的 MemberData 寫法）；probe 新步驟輸出格式。
- **4B**：`SentinelEventMapper` Linux 分路（欄位定案後）——含 **Source 三段 fallback 鏈**
  逐段測試（`sp` 有值走 `sp`；`sp` 缺席 `obssvcname` 有值走它；兩者皆缺從 msg 前綴解析
  `program:`／`program[pid]:` 兩種形；全缺退空並計入解析失敗——用第二次 probe 的 CEF
  樣本形狀當測資）；`BuildLinuxFilter`
  （空 IP 例外／IP 子句／`sev` 門檻）；pipeline 整合測試（3.8 的 fake client 骨架餵
  syslog 形狀事件 → TopIssues 命中 ssh-bruteforce → 紀錄寫入含正確 EventKey）；
  掃描精靈 Linux filter 分支。
- **4C**：關聯規則命中／不命中／`ElevatesDayRisk`；申報文案切換。
- 既有護欄回歸：`RuleImporterTests` 反射逐欄比對、`NetiqLifecycleTests` OS 三案、
  `FakeHostStore` 的 `Os` 欄位（NETIQ-DISCOVERY-PLAN:518 教訓——測試替身漏抄 Os 會讓
  「Linux 套對規則面」白測）。

---

## 五、文件同步（依 CLAUDE.md 慣例，隨實作批次一起改）

| 文件 | 改什麼 |
|---|---|
| `docs/DETECTION-SPEC.md` | AI 呼叫時機章節改寫（兩階段模型、`AiPending` 三態、深析報告時機、取消與補跑語意）；Linux 章節從「⏸ 取數管線未完成」改為實作現況（五層對 Linux 的適用性表：規則✓趨勢✓慢速✓關聯=SSH 鏈✓AI✓） |
| `docs/WEB-SPEC.md` | 執行監控（`netiq-ai` phase、里程碑 AI 統計與警告條目）；紀錄頁「AI 分析中」badge；NetIQ 維護頁併行度上限 3；診斷分頁新步驟 8b/8c；排程作業頁預覽文案（過渡期＋拆除後兩版） |
| `docs/DB-SPEC.md` | `ContentJson` 新欄位 `AiPending`／`EventName`／`EventKey`（無 schema 變更，序列化欄位）；`AttachAiResult` 抽出欄同步規則 |
| `docs/LINUX-RULES.md` | §簽章鍵與聚合、§關聯層從設計文改為實作現況；seed v5 校正記錄 |
| `docs/NETIQ-API-REFERENCE.md` | 新增 Linux Sentinel 欄位實證表（輪 A/B 結果） |
| `docs/BACKLOG.md` | 移除「Linux 事件取數管線」「Linux 攻擊鏈關聯層」「Linux 欄位形狀」三條（完成）；AiPending 補跑不再需要列入（§3.10 已含） |
| `docs/RULES-SPEC.md` | `RuleImporter` 路徑失準修正 |

---

## 六、批次表（從章節機械式產生——上一輪的教訓：不要手抄漏項）

| 批 | 項 | 內容 | 主要檔案 | 閘門 |
|---|---|---|---|---|
| 1 | 1.1 | 預覽分開回報 Windows/Linux（過渡期） | ScheduleController、RunPreviewDto、runs.js | — |
| 1 | 1.2 | Linux 單機立即執行擋截（過渡期） | ScheduleController、host-detail.js | — |
| 1 | 1.3 | Pipeline 警告上收 milestone（永久） | AnalysisOrchestrator | — |
| 2 | 二 | 併行度上限收斂 3 | NetiqOptions、NetiqDtos、Netiq.cshtml、netiq.js、NetiqOptionsService | — |
| 3 | 3.8-1 | 抽 IAiService（含 ct 貫穿，獨立 commit） | AIService 及五個消費端 | — |
| 3 | 3.8-2 | SentinelClient 工廠注入 | NetiqPipelineService | — |
| 3 | 3.8-3 | Pipeline 基準測試骨架 | LogForesight.Tests 新測試檔 | — |
| 3 | 3.2 | AiFollowupQueue＋AiWorkItem | Core/Service 新檔 | — |
| 3 | 3.3 | LogAnalysisService 拆分 | LogAnalysisService | — |
| 3 | 3.4 | Pipeline 兩階段改造＋消費者＋Result 計數 | NetiqPipelineService | — |
| 3 | 3.5 | AiPending＋AttachAiResult＋DTO＋前端三態 | DailyAnalysisRecord、IAnalysisRecordStore、EfAnalysisRecordStore、RecordDtos、兩個 QueryService、record-detail.js、records.js | — |
| 3 | 3.6 | 取消/失敗語意 | NetiqPipelineService、前端 | — |
| 3 | 3.7 | 進度 phase 切換＋milestone AI 統計 | WebRunProgress、RunMonitorService、runs.js、AnalysisOrchestrator | — |
| 3 | 3.9 | 本機路徑同步脫鉤（佇列上移 orchestrator） | AnalysisOrchestrator | — |
| 3 | 3.10 | AiPending 孤兒補跑 | NetiqPipelineService、IAnalysisRecordStore、LogAnalysisService | — |
| 3 | 3.8-4/5 | 新行為測試＋既有測試修整 | LogForesight.Tests | — |
| 4A | 4.1 | 診斷分頁 Linux 深掘（步驟 8 擴充＋8b/8c） | NetiqProbeRunner | — |
| 4A | 4.2 | EventName/EventKey＋五元組聚合＋Linux 分路比對 | EventLogEntryData、LogAggregator、KnownIssueCatalog、IssueHandling、TrendAnalyzer、SlowTrendAnalyzer、RiskyEvent、ChannelCoverage | — |
| 4A | 4.3 | 關聯層申報＋Linux 短路＋EventId 0 顯示修正 | LogAnalysisService、CorrelationAnalyzer、RiskReportService、SlowTrendAnalyzer、record-detail.js、records.js | — |
| — | 4.0-A | ~~使用者執行診斷輪 A 並貼回~~ **已完成 2026-08-07**（欄位主形狀定案） | （無程式改動） | ✓ |
| — | 4.0-B | ~~使用者執行診斷輪 B 並貼回~~ **已完成 2026-08-07（第三次 probe）**——六項證據五項定案（sp term 語意/sev 分佈與門檻/program 量級吵靜分類/collector 形態）；msg 片語為規劃缺口，補 4B.0 | （無程式改動） | ✓ |
| 4B.0 | §4.1 追加 | ~~probe 新步驟 8g＋使用者第四次執行~~ **✓ 已完成 2026-08-07**（msg 片語全面實證，見 §4.0 第四次 probe 表） | NetiqProbeRunner | ✓ |
| 4B | 4.4 | ~~FieldMap/Mapper/QueryBuilder Linux 分支＋擋板拆除＋掃描精靈分支＋seed v5~~ **✓ 已完成 2026-08-07** | SentinelFieldMap、SentinelEventMapper、SentinelQueryBuilder、NetiqPipelineService、KnownIssueSeed | ✓ |
| 4C | 4.5 | ~~SSH 攻擊鏈關聯（msg 解析細版＋逐事件降級）~~ **✓ 已完成 2026-08-07** | CorrelationAnalyzer、KnownIssueSeed | ✓ |
| 4 | 4.6 | ~~止血拆除＋周邊體檢＋文件失準修正~~ **✓ 已完成 2026-08-07**（體檢額外揪出並修復 `SentinelEventFetchService` 的 Linux 現場取數 bug） | ScheduleController、host-detail.js、SentinelEventFetchService、docs | ✓ |
| 4 | 4.7 | ~~Linux 測試全套~~ **✓ 已完成**（4B/4C 共 +26 測試，全套件 1640 通過 0 失敗） | LogForesight.Tests | ✓ |
| — | 五 | ~~文件七份同步~~ **✓ 已完成**（4A 隨批次同步；4B/4C 完工後再補一輪五份文件同步，2026-08-07） | docs/ | ✓ |

---

## 七、驗收與體檢

1. **測試基線**：實作前 `dotnet test` 記錄綠燈數；每批完成後回到全綠且只增不減。
2. **端到端情境（第 1~3 批，含 Linux 主機與至少兩台 Sentinel 的測試資料）**：
   - scope=all 手動執行：預覽含 Linux 文案（過渡期）→ 執行詳情第一行「N 台 Sentinel，平行度 M」→
     搜尋進度不因 AI 停滯（觀察 console：不同日期的批次查詢在 AI 完成前就持續出現）→
     收尾 milestone 含 AI 統計與警告條目。
   - 執行中開紀錄清單／詳情：入列主機日顯示「AI 分析中」；執行結束後翻成 AI 產出內容，
     `ai_raise` 的日子清單風險等級同步變高。
   - 執行中按停止：於合理時間內停下（不再等最壞 600s×重試）；孤兒紀錄顯示
     「統計模式（AI 未完成）」；**下次執行孤兒被補跑**（AiPending 歸 false、報告檔誠實從缺）。
   - AI 未設定環境（`useAi=false`）：全程統計模式、零入列，行為與現行完全相同。
3. **端到端情境（第 4 批，Linux Sentinel 接入後）**：
   - 診斷輪 A/B 輸出貼回、欄位定案記入 NETIQ-API-REFERENCE。
   - scope=all 執行涵蓋 Linux 主機：執行詳情出現 Linux 主機的查詢與分析行、
     紀錄詳情顯示 program（EventKey）而非「EventId 0」、UncoveredChecks 含關聯層申報、
     未回報主機卡的 Linux 主機在兩天內消失（TouchNetiq 生效）。
   - 對一台 Linux 主機模擬 SSH 暴破（或以歷史資料回補）：ssh-bruteforce 命中、達門檻
     ＋成功登入時關聯告警、當日風險拉高。
   - 預覽不再出現「Linux 暫不查詢」文案；Linux 主機詳情頁「立即執行」可用且真的執行。
4. **全案體檢輪**（照慣例）：欄位漂移普查（AttachAiResult 抽出欄、FakeHostStore/替身的
   新欄位 EventName/EventKey/AiPending 抄寫）、並發計數普查（新增計數器）、
   `IssueSignatureKey` 既有鍵不變的升級安全驗證、文件與實作對讀。

### 全案完工後第二輪體檢（2026-08-07，4B/4C/4.6 收畢後全面重掃）

逐節對照 §4.4/§4.5/§4.6 定案設計與實作、審查 4B/4C 全部新程式碼、全 docs 殘留掃描。結果：

- **§4.4 六項／§4.5 全項逐一핵對相符**：`LinuxQ1ProjectionFields` 內容、`MapEntryTypeLinux`
  門檻、Source 三段 fallback 鏈（含「三段皆失敗回 null 併入既有略過計數」的取捨，與規劃
  括號內意圖一致）、`BuildLinuxFilter` 混合下推形狀、吵 program 常數集、`EscapeLucenePhrase`、
  掃描精靈 os 分支、seed 不動、4C regex／門檻／降級語意——零偏離。
- **`RunServerAsync` OS 分組的髒值疑點排除**：`Os` 既非 windows 亦非 linux 的值會被分組靜默
  丟掉，但 `WebHost.NormalizeOs` 是全部四條寫入路徑的單一正規化點（主機頁/NetIQ 登錄/CSV/
  精靈），儲存值不變式成立，該情境經查不可達，非 bug。
- **揪出並修復一個真實缺口**：`RuleValidator.CheckLinuxFields` 未限制 `ProgramPattern` 字元集
  ——它以裸 term 進 Lucene filter（`sp:{pattern}*`，無 `MessagePatterns` 那種引號＋跳脫保護），
  管理者存入帶空白/`(`/`:`/`*` 的規則會讓整份 Q1 filter 語法壞掉、Linux 夜間取數整批失敗。
  補上「僅接受英數字與 `_`/`.`/`-`」驗證（字元集對齊 mapper 的 msg 前綴 program 正則；
  載入時不合格＝該條跳過＋顯性警告，不炸整份規則表）＋7 個測試；17 條種子經既有種子
  合格性測試連帶驗證全數通過。
- **文件修正**：NETIQ-API-REFERENCE.md §4a 兩處懸空引用（誤指不存在的 §5a→改 §4a-1）；
  WEB-SPEC.md 診斷分頁段補漏 8g；README.md 兩處舊敘述（開頭簡介＋NetIQ 取數段的
  「只支援 Windows」）；RULES-SPEC.md／LINUX-RULES.md 補 ProgramPattern 字元集限制記錄。
- 全套件 **1647 通過 0 失敗**（前輪 1640 +7 驗證測試），0 警告 0 錯誤。
