# 回饋第十八輪規劃（FEEDBACK-18）

> 基準：dev@1159459（回饋十七輪終檢收尾，1854 測試綠）。
> 來源：外部審查回饋四項（郵件全表拉取、本機失敗靜音通知、首次啟用總帳信、本機 AI 排隊）
> ＋四項新功能需求（問題負責人、無法處理上報、就緒度檢查、首次啟動精靈）＋順手修文案漂移。
> 所有回饋的程式碼引用已逐項核對屬實；與使用者討論後的定案記錄於各批次「定案」小節。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 郵件查詢 RiskLevels 下推（回饋一） | 小 | 無 |
| B | 通知閘門改「有產出就通知」（回饋二，方案A） | 小 | 無 |
| C | 郵件啟用時預填已通知狀態（回饋三，甲案） | 小 | 無 |
| D | 本機分析停用開關＋排程文件補洞（回饋四） | 中 | 無 |
| E | 負責人授權文案漂移修正（順手修） | 極小 | 無 |
| F | 問題負責人（新功能 5-1） | 大 | 無（G 的通知可先行） |
| G | 「無法處理」狀態＋admin 群組通知（新功能 5-2） | 中 | 無 |
| H | 就緒度檢查＋首次啟動精靈（新功能 5-3＋5-4 合併） | 大 | 無 |

建議實作順序：**A → B → C → E → D → G → F → H**（放量前的三個郵件修正最優先；E 順手；G 比 F 小且 F 的通知路由可借用 G 建立的即時寄信骨架；H 最後做，因為它的就緒度判定要引用其他批次落地後的最終狀態面貌）。

---

## 批次A：郵件查詢 RiskLevels 下推（回饋一）

### 現況與核對結果

- `MailNotificationService.NotifyAfterRunAsync`（`LogForesight.Web/Services/Mail/MailNotificationService.cs:129`）以 `Hosts = null`、無 `RiskLevels` 查近 14 天全表，風險過濾在記憶體做（`SendRunSummaryAsync` :361 用 Rank、`SendUrgentNotificationsAsync` :390 只挑 High）。2000 台 × 14 天 ≈ 28,000 列全量 ContentJson 反序列化。
- `EfAnalysisRecordStore.ApplyPushableFilters`（`LogForesight.Core/Persistence/Sql/EfAnalysisRecordStore.cs:294-298`）**早已支援** RiskLevels 下推，走 `lf_daily_records.risk_level` 抽出欄。
- **同病一處回饋未提**：`SendDigestAsync`（`MailNotificationService.cs:646-648`）每日／週報同樣無下推，週報一次拉 7 天全主機。
- `MailMinRiskLevel` 依 `SystemSettings.cs:213` 限定只能是「高」或「中」，因此下推集合只會是 `{高}` 或 `{高,中}`——緊急通知要的 High 永遠是其子集，**一次查詢供兩路共用不會有涵蓋缺口**。

### 改動

1. **A-1 `RiskLevels.AtOrAbove(string min)`**（`LogForesight.Core/RiskLevels.cs`）：回傳達到指定等級（含）以上的等級陣列——`高→{高}`、`中→{高,中}`、`低→{高,中,低}`、未知→全集（fail-open，寧多查不漏通知）。附 XML 註解說明供查詢下推使用。
2. **A-2 `NotifyAfterRunAsync` 下推**（`MailNotificationService.cs:129`）：
   ```csharp
   var riskFilter = settings.MailOnRunCompleted
       ? RiskLevels.AtOrAbove(settings.MailMinRiskLevel)   // 摘要窗口是聯集需求
       : new[] { RiskLevels.High };                        // 只開緊急時再窄一級
   var records = _records.Query(new RecordQueryFilter {
       Hosts = null, From = from, To = to, RiskLevels = riskFilter });
   ```
   **既有的記憶體 Rank 過濾與 `r.RiskLevel == High` 過濾全部保留**（雙保險）：下推只是預篩，最終判定語意不變；這也讓測試替身尚未支援下推時行為不漂移（見 A-4）。
3. **A-3 `SendDigestAsync` 下推**（:646）：同法加 `RiskLevels = RiskLevels.AtOrAbove(settings.MailMinRiskLevel)`，記憶體過濾保留。
4. **A-4 測試替身與下推斷言**（本批的關鍵，不是附屬品）：
   - `FakeAnalysisRecordQuery.Query`（`LogForesight.Tests/TestDoubles/StoreFakes.cs:329-337`）目前**只過濾 Hosts/From/To、完全忽略 RiskLevels**——補上 RiskLevels 過濾（比照 EF 語意：`filter.RiskLevels is { Count: > 0 }` 時 `Contains`）。`FakeRecordRepository`（`HandlingFakes.cs:118-123`）同補。這正是「欄位漂移在測試替身」bug 家族的同款地形，兩個 Fake 都不受合約測試約束，必須手動對齊。
   - `FakeAnalysisRecordQuery` 加 `LastFilter` 捕捉欄位，新增測試斷言：(a) 摘要＋緊急同開、門檻=中 → 下推 `{高,中}`；(b) 只開緊急 → 下推 `{高}`；(c) 週報 → 下推 `AtOrAbove(門檻)`。沒有這組斷言，下推寫錯（漏等級）不會有任何測試抓到。
   - `RiskLevelsTests` 補 `AtOrAbove` 三態＋未知值 fail-open 的單元測試。

### 受影響的既有測試

`MailNotificationServiceTests` 的門檻過濾測試（:128、:168、:214、:149、:731、:782）在「Fake 補下推＋服務保留記憶體過濾」的組合下**全數不需改動即應維持綠**——若有紅，代表下推語意寫錯，不是測試該改。

### 驗收

- 郵件三路查詢的 `RecordQueryFilter.RiskLevels` 均非 null（由新斷言測試保證）。
- SQL log（`EfAnalysisRecordStore.Query` 的 Debug 行 :362）可觀察 `risk=` 參數帶值、DB 列數大幅下降。

---

## 批次B：通知閘門改「有產出就通知」（回饋二，方案A）

### 現況與定案

- 閘門在 `SchedulerHostedService.cs:221` `if (outcome is { Success: true })`；本機路徑刻意無 try/catch（`AnalysisOrchestrator.cs:502-508` 註解明示），本機拋例外 → 外層 catch → `Success=false` → NetIQ 已寫入的 2000 台結果一封信都不寄，且 `UrgentSentKeys` 未標記，症狀是「延遲一天」難以察覺。
- **定案（使用者確認）**：只做方案A——閘門改成「執行成功**或**有寫入任何新紀錄」就通知；**不動**批次E 的失敗語意（本機失敗仍整趟判失敗、執行監控照實顯示）。**使用者取消**後已寫入的部分也觸發通知（已確認 OK；站台關閉情境由 `ApplicationStopping` 權杖天然取消通知，不受影響）。

### 改動

1. `RunOutcome` 增欄位 `AnyRecordsWritten`（定義處：`SchedulerRunState.cs` 一帶，實作時以 `RunOutcome` 的宣告位置為準）。
2. `SchedulerHostedService.TriggerRunAsync` 的閉包內（:188 一帶），從 `result` 現成資料計算——**不需要**新增 orchestrator 欄位：
   ```csharp
   var anyWritten = result.LocalResults.Count > 0
       || (result.NetiqResult?.HostDaysAnalyzed ?? 0) > 0;
   outcome = new RunOutcome(result.Success, ..., anyWritten);
   ```
   例外路徑（:203）的 `RunOutcome` 帶 `AnyRecordsWritten: false`（能走到那裡代表 orchestrator 環境層級炸掉，`result` 不可信）。
3. 閘門（:221）改：`if (outcome is { } o && (o.Success || o.AnyRecordsWritten))`，並更新註解說明「失敗但有產出」的語意（本機壞、NetIQ 完成的情境）。

### 注意事項

- `OrchestratorResult.NetiqResult` 在 NetIQ 路徑內部 catch 吞例外時可能為 null（`RunNetiqAnalysisAsync` :815-821 失敗不設值）——`?? 0` 已涵蓋。
- 取消時 `RunAsync` 走 :553 的 `OperationCanceledException` catch 後**正常 return result**，`LocalResults`／`NetiqResult` 保有取消前已完成的內容，`anyWritten` 判定自然成立。

### 測試

- `OrchestratorResult` 層：本機拋例外、NetIQ 有寫入 → `Success=false` 且呼叫端可判定有產出（可直接對 `OrchestratorResult` 斷言，不必整合測 scheduler）。
- 閘門邏輯若可抽成純函式（`ShouldNotify(RunOutcome)`）則單元測試三態：成功、失敗無產出、失敗有產出。

---

## 批次C：郵件啟用時預填已通知狀態（回饋三，甲案）

### 定案

- 採「**儲存設定時預填**」而非寄送時特判；語意為**（甲）從啟用起算**——啟用（含關閉後重新啟用）郵件通知時，窗口內既有的歷史紀錄一律標為已通知，第一封信只涵蓋啟用後新產出的紀錄。關閉期間的積壓不補寄。
- **不能無條件預填**：既有掛載點 `SystemSettingsService.Update` 的 `ResetRecipientFailureStreaks()`（`SystemSettingsService.cs:284`）是每次儲存都呼叫的；預填若比照，任何一次儲存設定都會把當下 pending 標成已通知，**製造真漏寄**。必須限定在「由關轉開」的那次儲存。

### 改動

1. **判定點**：`SystemSettingsService.Update` 已有 `:208 var before = _store.Get();` 與 `:210 var saved = _store.Update(...)`，兩者在 :280-286 之間都在作用域內。逐路判定（分路預填，避免一路已開、另一路後開時漏預填）：
   ```csharp
   var summaryTurnedOn = saved.MailEnabled && saved.MailOnRunCompleted
       && !(before.MailEnabled && before.MailOnRunCompleted);
   var urgentTurnedOn = saved.MailEnabled && saved.MailUrgentEnabled
       && !(before.MailEnabled && before.MailUrgentEnabled);
   if (summaryTurnedOn || urgentTurnedOn)
       _mail.MarkExistingRecordsAsNotified(summaryTurnedOn, urgentTurnedOn);
   ```
2. **`MailNotificationService.MarkExistingRecordsAsNotified(bool summary, bool urgent)`**（新方法，放在 `ResetRecipientFailureStreaks` 旁）：
   - 窗口與 `NotifyAfterRunAsync` 同源（`NotifyLookbackDays`、`to = Today-1`）。
   - **兩個集合都填「窗口內全部紀錄」的 key**（不只達門檻的）：對門檻切換免疫——若之後把 `MailMinRiskLevel` 由高改中，啟用前的中風險紀錄不會突然變成 pending 積壓。
   - 查詢用 `IAnalysisRecordQuery.ListHostDates`（只取主機×日期，不反序列化 ContentJson）組 `RecordKey` 格式（`HostId|yyyy-MM-dd`，:695）；若 `ListHostDates` 簽章不含 HostId 則退而用 `Query` 一次性查詢（一次性成本可接受，實作時確認簽章後定案）。
   - `_state.Update` 一次寫入兩集合；內部 try/catch 到底（「通知永遠不能弄掛」原則同樣適用於設定儲存路徑）。
3. **每日／週報不受影響**：它們靠 `LastDailySentDate`／`LastWeeklySentDate` 防重複、窗口固定 1／7 天，本來就沒有積壓問題，不預填。

### 測試

- 由關轉開（MailEnabled）→ 兩集合含窗口內既有紀錄 key、下一次 `NotifyAfterRunAsync` 對舊紀錄不寄。
- 只開摘要不開緊急 → 只有對應轉開的那次才觸發；已開狀態下重複儲存（改無關欄位）→ **不**預填。
- 關閉再重新啟用 → 再次預填（關閉期間新增的紀錄也被標記）。
- Fake 需補：`FakeAnalysisRecordQuery.ListHostDates` 目前 `NotImplementedException`（`StoreFakes.cs:348`），依最終選用的查詢路補實作。

### 文件

`HelpContent/12-settings.md` 郵件段補一句：「啟用（或重新啟用）通知後，僅通知啟用之後新產出的分析結果；啟用前的歷史紀錄不會補寄。」

---

## 批次D：本機分析停用開關＋排程文件補洞（回饋四）

### 定案

- Web 主機非重點監控目標，**不做**本機 AI 佇列統一（FEEDBACK-12 §3.9 的三個不做理由經重新核對依然成立：佇列自持於 `NetiqPipelineService.RunAsync` 內部、`AiFollowupJob` 為 private nested、執行結果總表依賴同步定案值，統一的成本遠大於收益）。
- 新增**「停用本機分析」開關**：停用後排程／手動執行只跑 NetIQ；若同一台機器日後也以 IP 登錄為 NetIQ 主機，仍照常從 Sentinel 取數（兩者鍵不同——本機用 MachineName、NetIQ 用 IP，`HostStore` 天然是兩筆主機列，無需去重改動）。
- 補 `10-scheduler.md` 的 AI 佇列行為說明。

### D-1 停用開關（完整比照 DebugDump 既有流向）

| 層 | 位置 | 改動 |
|---|---|---|
| 模型 | `Core/Models/ScheduleOptions.cs:20-35` | 加 `public bool LocalAnalysisEnabled { get; set; } = true;`（預設 true＝零行為變化） |
| DTO | `Models/Dto/ScheduleDtos.cs`（`ScheduleOptionsDto`／`SaveScheduleOptionsRequest`） | 同名欄位 |
| API | `Controllers/Api/ScheduleController.cs:56-69` | Update 落地＋稽核字串帶「本機分析已停用」 |
| 觸發覆寫 | `SchedulerHostedService.cs:147-154` | `effectiveRequest` 比照 DebugDump 統一以排程設定為準：`IncludeLocal = _scheduleOptionsStore.Get().LocalAnalysisEnabled` |
| RunRequest | `AnalysisOrchestrator.cs:19-36` | 加 `public bool IncludeLocal { get; init; } = true;` |
| 生效點 | `AnalysisOrchestrator.cs:491` | `var localTask = request.Scope != RunScope.NetiqHosts && request.IncludeLocal ? RunLocalAnalysisAsync(...) : Task.CompletedTask;` 並在停用時印一行 console／Milestone「本機分析已停用（排程設定），本次僅執行 NetIQ 機房分析」——執行詳情要能看出是設定行為不是漏跑 |
| 指定主機更新 | `ScheduleController.cs:206`（`case "host"` 的 local 分支） | 停用時回 400「本機分析已停用，請先於排程作業頁啟用」；前端 `host-detail.js:385-390` 對 `Source=='local'` 且停用時隱藏「指定主機更新」按鈕（options 已可查） |
| 台數預覽 | `ScheduleController.cs:113-116`／`:176` | `includesLocal` 改讀新旗標 |
| UI | `Views/Pages/Runs.cshtml`（排程設定卡，`#schedule-debug-dump-wrap` 旁） | checkbox「分析本機主機」＋說明文字（停用情境：Web 主機本身不需監控、或改由 NetIQ 取數）；`runs.js:508-518`／`:585-600` 載入與儲存同步 |
| Modal 文案 | `Runs.cshtml:153`、`:166` | 「全部主機」與回補天數 popover 的本機描述依旗標動態調整（`runs.js` 開 modal 時套用） |
| 執行監控 | `Services/RunMonitorService.cs:111`／`:136` | 停用時本機主機格顯示「本機分析已停用」而非「未執行」（避免誤判漏跑）；`hostStore.Touch` 在共同段（`AnalysisOrchestrator.cs:244`）不受影響，LastReportAt 照常更新，儀表板「久未回報」不會誤報 |

進度條**不需要改**：停用後 orchestrator 不會回報 `local` phase，`runs.js:738-740` 的「有回報過才顯示」會自動隱藏本機軌（批次十七E 的既有收益）。

### D-2 文件補洞（`LogForesight.Web/HelpContent/10-scheduler.md`）

該檔 :16 已有本機/NetIQ 並行說明，補上目前完全缺失的：

1. NetIQ 側「統計先寫、AI 事後補」兩階段：畫面上「AI 分析排隊中」暫代狀態的意義、`netiq-ai` 子進度條。
2. AI 佇列滿載時搜尋暫停（背壓）的表現。
3. 取消後 AiPending 孤兒由下次執行自動補跑。
4. **本機路徑的 AI 是同步的**：與 NetIQ 共用同一個 AI 服務序列化排隊，本機進度條在中段長時間停留屬正常現象，不是卡住。
5. 「分析本機主機」開關的用途與停用後的行為（含「同一台機器以 IP 登錄為 NetIQ 主機時仍會從 Sentinel 取數」）。

### 測試

- `ScheduleOptions` 序列化預設值（舊 blob 無此欄 → true）。
- orchestrator：`IncludeLocal=false` 時本機路徑不執行、NetIQ 照跑；`Scope=LocalOnly` 且停用 → 由 API 層擋（400），orchestrator 不需處理矛盾組合。
- `ScheduleController`：host 分支對 local 主機停用時 400；台數預覽不含本機。

---

## 批次E：負責人授權文案漂移修正（順手修）

回饋十一輪 §2b 後「負責人＝第二條授權路徑（可見範圍 ∪ 負責主機）＋隱含 Handle」已落地（`VisibilityService.cs:161-165`、`UserCapabilityResolver.cs:50`），但兩處文案仍是改版前語意，會誤導管理者：

1. `Views/Pages/Hosts.cshtml:135`：「負責人不會自動取得檢視權限，檢視權來自部門群組授權。」→ 改為與 `OwnerCsvImporter.cs:116-118`（已改對）一致：「套用後，負責人會自動取得所負責主機的檢視權，並具備處理狀態維護權限。」
2. `Core/Models/WebHost.cs:98-101` 的 XML 註解同步更新（「與授權是兩件事」段落改述為第二條授權路徑）。

無測試影響（純文案／註解）。

---

## 批次F：問題負責人（新功能 5-1）

### 定案語意

使用者定調：「整個服務都是以問題為主，其他面向為輔，問題負責人也是相同概念」——即問題負責人**比照主機負責人的完整概念**，落在**問題（簽章）層級、跨主機**：

- **粒度＝(Source, EventId)**：與「依問題」視角的 group key 完全一致（`RecordQueryHelpers.cs:10-14` 的 `GroupBy (Source, EventId)`；`IssueCase`/`IssueHandling`/`lf_top_issues` 都沒有 RuleId，別引入第二套鍵）。Linux 5 段簽章併組行為與現有視角一致，維持 v1 限制。
- **優先於主機負責人**的三個作用面：
  1. **自動帶入處理人**：問題負責人恰一人且未停用 → 優先帶入；否則落回既有的主機負責人恰一人規則（`DayHandlingCommandService.cs:267-300`）。
  2. **郵件通知路由**：record（主機日）內只要有任何達門檻問題有問題負責人 → 該 record 通知問題負責人（可能多位），**不再**通知主機負責人；record 內所有問題都無問題負責人時才落到主機負責人。record 粒度、優先取代語意，簡單可解釋。
  3. **授權路徑**：問題負責人自動可見「保留期內出現過其負責問題的主機」（第四條授權路徑，與主機負責人對稱），並比照主機負責人隱含 `User` 角色（Handle＋ConfirmPermission，不含 ViewAll）。

### F-1 資料模型與 store

```csharp
/// 問題負責人規則：以 (Source, EventId) 為鍵，跨主機生效
public class IssueOwnerRule
{
    public string SourceName { get; set; } = "";     // OrdinalIgnoreCase 比對，沿用 MatchesSignature 慣例
    public int EventId { get; set; }
    public List<long> OwnerUserIds { get; set; } = new();   // 可多人，與主機負責人對稱
    public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
}
```

- Store：`IssueOwnerStore : JsonBlobSingleton` 型（blob key `issue_owners`，內容為 List）——規則量級是數十~數百筆，blob 足夠；鍵唯一性（同 (Source,EventId) 只一筆）由 Save 時 upsert 保證。
- DI：Singleton（比照 `MailNotifyStateStore`，`ServiceCollectionExtensions.cs:81` 模式）——授權層（per-request）與郵件（Singleton）都要讀。

### F-2 授權路徑

- **主機反查**：新增 `IIssueAggregateQuery.HostIdsFor(IEnumerable<(string Source, int EventId)> issues, DateTime from, DateTime to)`（`Core/Persistence/IIssueAggregateQuery.cs`）——`lf_top_issues` 欄位與索引齊備（`(record_date, source_name, event_id)`，`LfDbContext.cs:138`），一句 GROUP BY 距離；排除 `host_id=0` 未回填舊列（與 `EfIssueAggregateQuery.cs:43` 同慣例，回填完成前舊資料的可見性缺口可接受並在註解申報）。窗口＝`RetentionDays`。
- **靜態 resolver**：`HostVisibilityResolver` 加 `GetIssueOwnedHostIds(IIssueOwnerStore, IIssueAggregateQuery, userId, retentionDays)`——維持「Singleton 的 MailNotificationService 也能用同一份邏輯」的既有架構理由（`VisibilityService.cs:170-173`）。
- `VisibilityService.GetVisibleHostIds`（:119-168）：聯集第四條路徑，沿用 `_cached` per-request 快取。
- `UserCapabilityResolver.cs:50`：`IsHostOwner || IsIssueOwner` → 隱含 `User` 角色。
- **`GetIssueKeyRestriction` 不變**：問題負責人取得的是主機級可見（比案件授與寬），不落入 issueKey 白名單限制——與主機負責人同層級。

### F-3 自動帶入處理人

`DayHandlingCommandService.cs:267-300` 的 auto-assign 判定改為兩段：

1. 該問題的 `IssueOwnerRule.OwnerUserIds` 恰一人且未停用 → 帶入，`HandlingActions.AutoAssign`（歷程註記「問題負責人」）。
2. 否則落回既有主機負責人恰一人規則。

問題層級指派（`BulkAssignIssueCase`，`IssueHandlingCommandService.cs:347-486`）若有同款預設處理人邏輯，同步套用同一優先序（實作時確認）。

### F-4 郵件路由

`MailNotificationService.ResolvePerRecipient`（:293-352）的負責人分支（:325-349）改為：

1. `MailNotifyHostOwners` 開啟時（開關語意擴為「通知負責人」，設定頁文字同步改「通知負責人（問題負責人優先，其次主機負責人）」）：
2. 逐 record：從 record.TopIssues 比對 `IssueOwnerRule`（(Source,EventId)，OrdinalIgnoreCase）→ 命中任一 → 收件人＝命中規則的 OwnerUserIds 的 email 聯集；全未命中 → 落回 `host.OwnerUserIds`。
3. `MailContext`（:227-232）擴充：一次批次預載 `IssueOwnerStore.GetAll()` 成查找字典，避免逐 record N+1（比照 B-2 慣例）。

### F-5 指派頁（UI）

- 路由 `/admin/issue-owners`，`[Permission(Capability.Maintain)]`（`PagesController` 比照 :93-95 模式）；側欄 `layout.js` `NAV_SECTIONS` 系統管理組加「問題負責人」（`requires: 'Maintain'`）。
- 內容（沿用既有企業藍設計系統與 Bootstrap 5.3.8，不引入新色票字型；套用 ui-ux-pro-max 準則）：
  - 規則清單表：問題（`Source EventId`＋近期出現統計：主機數／最近出現日，來源 `IIssueAggregateQuery.Aggregate` 現成投影）、負責人 badges、備註、更新資訊；表格外層 `overflow-x-auto`。
  - 新增／編輯 modal：問題選擇器（從近 N 天出現過的問題挑選，下拉＋搜尋；也允許手動輸入 Source＋EventId 以便預先指派尚未出現的問題）、負責人勾選清單（複用主機頁 `#host-owners` 的 checkboxList 模式，`hosts.js:594-597`）。
  - 空狀態引導（「尚未指派任何問題負責人，新增第一筆…」）、送出 loading→成功/失敗回饋、可點元素 cursor-pointer、觸控 44px、鍵盤焦點可見。
- API：`/api/admin/issue-owners` GET／PUT（upsert）／DELETE，`[Permission(Capability.Maintain)]`；寫入走稽核（`_audit.Record`，Before/After 帶負責人清單）。
- 問題查詢頁「依問題」視角的群組列順帶顯示問題負責人 badge（資料已在 `SearchByIssue` 聚合層，加一次字典查找即可）——讓「這個問題歸誰」在主視角一眼可見。

### F-6 測試

- Store：upsert 鍵唯一、大小寫不敏感比對。
- 授權：問題負責人可見含其問題的主機（EF 端 `HostIdsFor` 合約測試進 `EfIssueAggregateQuery` 測試組）；host_id=0 舊列排除；停用使用者不授權。
- 能力：IsIssueOwner 隱含 User 角色。
- auto-assign 優先序：問題負責人 > 主機負責人 > 不帶；多人不帶。
- 郵件路由：record 有問題負責人 → 只寄問題負責人；無 → 落回主機負責人；`MailContext` 預載（無 N+1）。
- **測試替身普查**：新 store 的 Fake 一開始就要與正式實作同語意（本輪 A-4 的教訓前置套用）。

---

## 批次G：「無法處理」狀態＋admin 群組通知（新功能 5-2）

### 定案

- **新增獨立狀態**，不沿用 `wont_fix`（語意不同：wont_fix＝評估後決定不處理；本需求＝處理不了、上報請 admin 決定結案或改指派）。
- 狀態名 `escalated`，中文「無法處理（待管理員決定）」；**屬未結**（案件保持進行中，等 admin 動作），對外三態歸「處理中」。
- 觸發時即時寄信給 **admin 群組成員**（`UserGroup.Role == UserRole.Admin && Active` 的群組內 Active 使用者，這是現成一等概念）。

### G-1 狀態值域（三套值域同步，防漂移）

| 位置 | 改動 |
|---|---|
| `Core/Models/IssueHandling.cs:113-137` `IssueHandlingStatuses` | 加 `Escalated = "escalated"`；**`Closed` 集合不含它**；`All`／合法值驗證同步 |
| `Core/Models/RecordHandling.cs:76-93` `HandlingStatuses` | 同步加入（日層級面板也能選，維持兩層值域對稱——這正是先前值域漂移 bug 家族的預防）；`Unresolved` 集合**含** escalated |
| `RecordHandling.cs:103-113` `ExternalOf` | escalated → 「處理中」 |
| 前端標籤三處 | `handling-panel.js:22-29`（＋原因必填表 :41 加 `escalated: { label: '無法處理原因（必填）', required: true }`，比照 wont_fix）、`issue-status-reply.js:14-21`、`format.js` 對外三態不動 |

### G-2 通知

1. **admin 成員解析共用化**：`IdentityService.HasNoAdmins()`（:235-245）的「admin 群組 → 成員」查找抽成可複用方法 `GetAdminMembers()`（回 Active 使用者清單；判定用 `g.Role == UserRole.Admin`，不能用群組名稱字串——群組可改名）。
2. **`MailNotificationService.NotifyEscalationAsync(EscalationNotice notice, CancellationToken ct)`**（新的第四路觸發，事件驅動即時單發，架構參考 `SendTestAsync`＋`SendSafeAsync` 這一對）：
   - `notice` 帶：問題標籤（Source EventId）、主機清單、回覆人帳號、原因、案件連結路徑。操作者身分由呼叫端傳入（Singleton 不能注入 Scoped 的 `ICurrentUser`，既有慣例）。
   - `settings.MailEnabled` 才寄；收件人＝admin 成員 email（去重、排除空白）；不去重狀態、不落地 SentKeys（事件即時信，重複上報就重複通知是正確行為）；內部 try/catch 到底。
   - 信件內容：主旨「問題上報：{問題} 負責人回覆無法處理」，內文含主機、原因、回覆人、「請至問題查詢頁決定結案或重新指派」。
3. **掛載點**：狀態變更三入口——`IssueHandlingCommandService.SetIssueStatus`（:65-107）、`SetIssueStatusBatch`（:109-）、`BulkSetIssueStatusByHandler`（:521-577）——狀態轉為 `escalated` 時，在 `_audit.Record` 之後 fire-and-forget（`_ = _mail.NotifyEscalationAsync(...)`，方法本身同步、不改簽章；MailNotificationService 內部已保證不拋）。日層級面板若也選了 escalated，同法（`DayHandlingCommandService` 對應點）。

### G-3 admin 側動線

- 問題查詢頁：escalated 顯示醒目 badge（warning 色系），既有狀態篩選 chips（`Records.cshtml:74-76` 是三態）不改結構——escalated 歸「處理中」桶，靠 badge 區分。
- admin 的後續動作走**既有**入口：重新指派（`PUT assign`，`Capability.Assign`）或批次結案（`bulk-close`）——不新建頁面。
- 信中連結導向 `/records`（帶問題篩選參數，若既有 URL 參數支援 source/eventId 深連結則帶上，實作時確認 `records.js` 的 URL 參數集）。

### G-4 測試

- 值域：escalated 合法、Closed 不含、ExternalOf 歸處理中、Unresolved 含（週報未處理數會計入——語意正確：上報中就是還沒處理完）。
- 原因必填（API 層驗證比照 wont_fix）。
- 通知：轉 escalated 寄給 admin 成員；admin 群組改名後仍寄（Role 判定）；MailEnabled=false 不寄；非 escalated 轉換不寄。
- `SystemSettingsServiceTests` 既有郵件測試不受影響（新路徑獨立）。

---

## 批次H：就緒度檢查＋首次啟動精靈（新功能 5-3＋5-4 合併）

### 定案

- 兩需求合併為**一個精靈頁**：checklist 即精靈骨架。
- 完成判定採**混合制**：各步「完成」自動判定（從系統狀態推導）、「跳過」手動；全部步驟達終態（完成或跳過）後，使用者可選擇隱藏教學文件清單裡的精靈入口。
- 隱藏是**全站**設定（setup 本來就是全站性質），存 blob 單例。

### H-1 就緒度 API：`GET /api/admin/setup/status`（`[Permission(Capability.Maintain)]`）

獨立端點、不塞進 `/api/health/detail`（那支已有六大塊、90% 內容無 UI 消費，繼續堆會更難用；health 是「現在健康嗎」、setup 是「設好了嗎」，關注點不同）。回傳：

```json
{ "steps": [ { "id": "...", "title": "...", "done": true, "skipped": false,
               "detail": "...", "targetUrl": "/admin/settings#..." } ],
  "allSettled": false, "hidden": false }
```

步驟清單與自動判定來源（依討論定案的順序）：

| # | 步驟 | done 判定（自動） | 跳轉目標 | 可跳過 |
|---|---|---|---|---|
| 1 | 儲存體 | `HealthService.ProbeStorage` OK（現成 :111-125） | —（純顯示） | 否（不 OK 站台本來就有問題） |
| 2 | 管理員帳號 | `!IdentityService.HasNoAdmins()`（現成 :235-245） | `/admin/users` | 否 |
| 3 | 郵件通知 | `MailEnabled && SmtpServer 非空` | `/admin/settings`（郵件段錨點） | 是 |
| 4 | AI 服務 | `settings.Ai.IsConfigured`（同 `/api/ai/status` 判定） | `/admin/settings`（AI 段錨點） | 是 |
| 5 | NetIQ Sentinel 與主機 | 啟用中 Sentinel ≥1 且 Pollable NetIQ 主機 ≥1（`SentinelStore`＋`NetiqHostList.Pollable` 現成） | `/admin/netiq` | 是 |
| 6 | 群組與授權 | 存在任一非 builtin 群組授權或任一主機負責人（`GroupAccessStore`／`OwnerUserIds`） | `/admin/groups` | 是 |
| 7 | 排程啟用 | `ScheduleOptions.Enabled` | `/runs` | 是 |

實作為 `SetupReadinessService`（Scoped），聚合現成 store 判定，不新增探測邏輯；規則版本（`RuleBootstrapper` SeedVersion）**不列步驟**——規則庫由 `RuleBootstrapper` 啟動自動就緒，使用者無事可做，列了只會困惑（規則升級提示已有規則維護頁橫幅負責）。

### H-2 精靈狀態 store

`SetupWizardStateStore : JsonBlobSingleton`（blob key `setup_wizard_state`）：

```csharp
public class SetupWizardState
{
    public HashSet<string> SkippedSteps { get; set; } = new();
    public bool Hidden { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
```

API：`POST /api/admin/setup/skip/{stepId}`（含取消跳過 toggle）、`POST /api/admin/setup/hidden`（body: bool；只有 `allSettled` 時前端才給隱藏選項，後端不強制——admin 想提前隱藏就讓他隱藏）。寫入走稽核。

### H-3 精靈頁 `/setup`

- `[Permission(Capability.Maintain)]`（`PagesController` 加路由，比照 :109-111）。**不進側欄**——入口在教學文件清單第一項（見 H-4）；直接輸入網址也可達（隱藏後仍可用，隱藏只影響清單入口）。
- 版面（沿用既有 `.lf-card` 與企業藍 tokens、Bootstrap 5.3.8；ui-ux-pro-max 準則落地）：
  - 頂部整體進度：「已完成 x／共 7 步（含跳過 y）」＋進度條（Step N of M 準則）。
  - 垂直 checklist stepper：每步一列——狀態圖示（完成 ✓ 綠／跳過 ⊘ 灰／待設定 ○）＋標題＋detail 一行＋動作鈕「前往設定」與「跳過此步」（跳過需可逆：「取消跳過」）。SVG 圖示不用 emoji；hover 150-300ms 過渡；焦點環保留。
  - **跳轉與返回**：「前往設定」以一般導頁前往目標頁並帶 `?from=setup`；目標頁（settings／users／groups／netiq／runs）的頁面 JS 偵測該參數時，在頁頂顯示一條可關閉的「返回啟動精靈」提示列（`layout.js` 或各頁共用小工具），點擊回 `/setup`。精靈頁每次載入重新拉 `/api/admin/setup/status`——回來即看到最新判定，不需要跨頁狀態同步。
  - **深連結**：目前聚焦步驟寫入 `location.hash`（`#step-3`），重整與分享可回到同一步（URL 反映狀態準則）。
  - `allSettled` 時顯示完成卡：「全部步驟已完成（含跳過 n 步）」＋「隱藏教學文件裡的精靈入口」checkbox（送出有 loading→成功回饋）；跳過的步驟列表仍可見、可回頭取消跳過。
  - 空狀態／載入：拉 status 期間 skeleton；失敗顯示重試。

### H-4 教學文件清單整合

- **manifest 擴充**：`HelpContent/manifest.json` 的 chapters **第一項**插入 `{ "id": "setup-wizard", "title": "首次啟動精靈", "type": "link", "href": "/setup", "icon": "…" }`；`HelpContentService` 的 `ManifestChapter`（:69-77）與 `HelpChapter`／`HelpChapterDto`（`HelpDtos.cs:5-16`）同步加 `Type`／`Href`（既有 md 章節 type 預設 "markdown"，欄位向後相容）。
- **Hidden 過濾**：`HelpController.GET /api/help/manual`（:29-30）組裝時，`SetupWizardState.Hidden == true` → 濾掉 `type=link` 的精靈項（`HelpContentService` 保持純內容載入、過濾放 Controller 或薄服務層，避免 Singleton 內容快取跟狀態耦合）。
- **前端渲染**：`help-manual.js` 的 `selectChapter`（:47-63）分支——`type === 'link'` 的章節不走 `renderAiText`（markdown-lite 刻意不支援連結），改自行 `createElement` 渲染一張導引卡（簡述精靈用途＋「開啟啟動精靈」按鈕導向 href）；nav 渲染（:32-45）與 `renderRelated` 不需改（只用 id/title/icon/related）。

### H-5 測試

- `SetupReadinessService`：七步判定各自的 true/false 組合（餵 fake store）；skipped 疊加；allSettled 計算（done ∪ skipped 全滿）。
- State store：跳過 toggle、hidden 落地、舊 blob 無欄位預設值。
- HelpController：hidden 時 manifest 少一項、非 hidden 時精靈在第一項且 type/href 正確；既有 14 章 DTO 欄位向後相容（Type 預設值）。
- 權限：/setup 與 setup API 皆 Maintain-only（比照既有 admin 頁測試慣例）。

---

## 全案收尾

1. **文件**：`docs/WEB-SPEC.md` 補問題負責人（授權路徑第四條、郵件路由優先序）、escalated 狀態、精靈；`HelpContent` 對應章節（03-issues 問題負責人、05-handling escalated、09-permissions 授權路徑、10-scheduler 批次D、12-settings 批次C 一句）；`13-glossary` 加「問題負責人」「無法處理（上報）」。
2. **體檢輪**（沿用歷輪慣例）：全案完成後跑一輪自查——重點盯「改共用欄位漏改讀取端」（F 的 ResolvePerRecipient、G 的三套值域）與「測試替身與正式實作語意漂移」（A-4、F-6 的 Fake）。
3. **驗收路徑**：dev 分支開 `feature/feedback-18`，各批次獨立 commit，全綠後併 dev，待實測後併 master（既有分支流程）。

## 明確不做（本輪定案）

- 本機 AI 佇列統一（回饋四）：成本遠大於收益，§3.9 理由依然成立；改以 D-1 停用開關＋D-2 文件補洞回應。
- 本機路徑失敗降級（回饋二方案B）：維持批次十七E 的硬失敗語意，只改通知閘門。
- 郵件「重新啟用補寄積壓」（回饋三乙案）：定案從啟用起算。
- `/api/health/detail` 大改：僅維持現狀，就緒度另立 `/api/admin/setup/status`。

## 體檢輪修正（8+1 角度平行審查，2 處 CONFIRMED、其餘 PLAUSIBLE 全數修正）

批次 A~H 全部完成後，對 `dev..feature/feedback-18` 整條 diff 跑一輪多角度審查，抓到：

- **CONFIRMED**：「依問題」視角把 escalated 誤算進未處理（`RecordListQueryService.SearchByIssue`／`BuildIssueGroup` 漏把 `Escalated` 併入處理中分支）。
- **CONFIRMED**：單筆／批次／跨主機三個回覆入口都會在「已上報再存一次」時重寄一封上報信（`IssueHandlingCommandService.NotifyEscalationIfNeeded` 原本沒有 `previousStatus` 防呆）——三處呼叫點皆已補上「只在轉入時通知」的判定，並各自補了轉入寄一次／已上報再存不重寄的迴歸測試（`HandlingServiceTests.cs`）。
- **PLAUSIBLE 並修正**：`SearchByIssue` 的 `usersById` N+1（每筆問題重查 `_users.Get`）→ 改成迴圈外建一次字典；`HostVisibilityResolver.GetIssueOwnedHostIds` 漏過濾 `Active`，撤場主機仍算進問題負責人可見範圍→ 補過濾＋`VisibilityServiceTests` 迴歸測試；`MailNotificationService.GetVisibleHostIds(userId)` 只聯集群組授權與主機負責人兩條路徑，全域收件人若只是問題負責人會看不到自己負責主機的明細→ 補 `IIssueAggregateQuery` 依賴並補迴歸測試；`RecordDetailQueryService` 的問題狀態文字 switch 忘了 escalated 分支；`issue-owners.js` 的 `renderSelectedSummary` 是死程式碼（宣告了沒接事件）→ 補接；`SetupReadinessService` 一次請求內 `_hosts.GetAll()` 重複兩次→ 收斂成一次；`IssueOwnerRule` 的 `(Source, EventId)` 比對邏輯在 4 處各自重複實作→ 收斂成 `IssueOwnerRule.Matches`／`KeyOf`／`IndexByKey` 三個靜態輔助方法；`setup.js` 的本地 `statusBadge` 遮蔽了 `core/format.js` 匯出的同名函式。
- **測試替身漂移**：`HandlingFakes.FakeRecordRepository` 的 `IAnalysisRecordQuery.Query` 明顯實作忘了套用 `filter.From`/`filter.To`（同一 fake 內的 `FakeAnalysisRecordQuery` 有套用，兩者語意不一致）→ 已補上。

修正後全量測試 1951 通過（5 個 ScaleBenchmarks 略過），0 失敗。
