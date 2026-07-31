# Web 排程化與風險 log 暫存規劃（WEB-SCHEDULER-PLAN）

> 規劃日期：2026-07-31。狀態：**Phase 1（§2 風險 log 暫存）已於 2026-07-31 實作
> 完成並通過全案體檢（實作與規劃的差異註記見 §2.2.5）；Phase 2~5 未開始**。
> 全部問題已定案（見 §3 定案紀錄 1~9）。
> 兩項需求：(1) 檢討 console 批次的去留，把排程職責搬進 Web（自訂排程時間、
> 執行時間區間、手動觸發指定/全部主機、可設定回補天數）；(2) 取回並判定有風險的
> log 暫存資料庫 14 天（設定頁可調），讓「詢問 AI」直接從資料庫取得原始事件。

---

## §0 結論先講

- **Console 定案「完全退役、直接移除」**（2026-07-31 使用者定案 #6/#9）：
  排程、時間區間、手動觸發、進度可視化全部搬進 Web（方案 A：Web 行程內
  BackgroundService）；CLI 職責全數由 Web 承接後（Phase 4），**Phase 5 即自
  解決方案移除 console 專案，不保留過渡期薄殼**。各 CLI 的出路：
  `--import-rules` 搬 Web 規則頁（1.4.9）、`--debug-dump` 搬排程設定開關
  （1.4.10）、`--netiq-probe` 搬 NetIQ 維護頁「診斷」分頁（**必做**——Linux
  Sentinel P3 閘門的載具，1.4.11）、`--suppress` 系列與 `--host-list` 本就被
  Web 頁面涵蓋直接刪、`--selftest` 接受退役（1.4.11）。代價：移除後只剩
  「git revert＋重建部署」的冷回退，無 schtasks 熱回退——風險與緩解見 1.5。
  這會**推翻 docs/HISTORY.md 既有決策 20**（one-shot＋工作排程器、Web 不養
  常駐背景工作），須正式改決策——理由見 §1.2。
- **需求二（風險 log 暫存 DB）合適且獨立**，不依賴需求一，改動面小、價值直接
  （AI 對話從 15 秒的 Sentinel 即時查詢變成毫秒級 DB 查詢，且**本機直讀主機
  首次獲得原始事件注入能力**——現行 live fetch 只支援 NetIQ 主機）。建議先做。
- 實作順序：**Phase 1（需求二）→ Phase 2（服務搬遷重構，只搬不改）→
  Phase 3（Web 排程引擎與 UI）→ Phase 4（CLI 職責搬 Web，含 probe 診斷分頁）
  → Phase 5（試點 ≥5 晚 → schtasks 退場＋console 移除）**。每個 Phase 獨立
  可發布，Phase 5 的移除步驟前均可熱回退，行為保護不變式見 §3.1。

---

## §1 需求一：Console 去留與 Web 排程

### 1.1 Console 目前承擔什麼（現況盤點）

`LogForesight.exe`（`Program.cs` 約 890 行，one-shot）每次執行依序做：

| # | 職責 | 實作位置 | 搬進 Web 的可行性 |
|---|---|---|---|
| 1 | 權限/角色異動檢查（ACL＋Administrators 快照比對） | `PermissionMonitorService` | 可，但監控基準目錄會變（見 1.4.7） |
| 2 | 各類保留天數清理（歷史/執行歷程/稽核/報告檔） | `Program.cs` 步驟 1~1c | 可，直接併入排程執行 |
| 3 | 本機直讀 Event Log 逐日分析 | `EventLogService`＋`LogAnalysisService` | 可，但 Security log 需要服務帳號權限（見 1.4.8） |
| 4 | NetIQ 機房分析（Sentinel 取數） | `NetiqPipelineService` | 可，純網路 I/O，無本機權限需求 |
| 5 | 體檢（due-date 輪巡） | `WeeklyCheckupService` | 可 |
| 6 | 執行紀錄登記/回填（Runs 頁資料來源） | `BatchRunRecorder` | 可，沿用同一 store |
| 7 | CLI 工具：`--selftest`／`--netiq-probe`／`--import-rules`／`--suppress` 系列／`--host-list`／`--debug-dump` | 各 CLI 類別 | **逐項處置後隨 console 移除**：import-rules 搬 Web（1.4.9）、debug-dump 搬 Web（1.4.10）、probe 搬 NetIQ 維護頁診斷分頁（必做，1.4.11）、suppress／host-list 已被 Web 規則頁「告警抑制」分頁與主機頁涵蓋（直接刪）、selftest 接受消失（1.4.11） |

關鍵事實：**上述 3~5 的核心服務目前都在 console 專案**（`LogForesight/Service/`），
Web 專案只引用 Core——要讓 Web 執行分析，這些服務必須搬進 Core（見 1.4.1）。

### 1.2 為什麼當初決定 one-shot、現在為什麼值得改

既有決策（docs/HISTORY.md 決策 20、`Program.cs:470` 註解「排程屬於批次、Web 不養
常駐背景工作」）成立的理由與現況變化：

| 當初的理由 | 現況 |
|---|---|
| 冪等設計（已分析日跳過、缺漏回補、體檢到期制）全部圍繞 one-shot 建立 | **冪等設計恰好是搬遷的最大資產**：排程引擎只要在對的時間「再跑一次同一個冪等流程」，不需要任何新的排程簿記；時間窗中斷後下個窗口自動續跑，靠的還是既有的 `HasRecord` 缺漏回補 |
| 工作排程器成熟穩定、失敗通知現成 | 換來的是**設定不可視**：排程時間改一次要上伺服器開 schtasks；漏跑原因（排程被停用、帳號密碼過期）Web 完全看不到，與「沒查 ≠ 沒事」原則相悖 |
| Web 與批次職責分離、權限分離 | Web 已是常駐 Windows 服務（`UseWindowsService()`），加 BackgroundService 不改變部署形態；權限議題確實存在，見 1.4.8 的取捨 |

使用者要的四件事（自訂排程時間、時間區間內才執行、手動觸發指定/全部主機、
設定回補天數）在 one-shot＋schtasks 模型下**前三件都做不到或做得很彆扭**
（手動觸發＝遠端桌面上去雙擊 exe），第四件已存在（`NetiqOptions.BackfillDays`）
但入口在 Web、執行在批次，管理者要「調大→跑一次→調回來」也得碰得到觸發按鈕才
順手。結論：搬。

### 1.3 方案比較

| 方案 | 作法 | 優點 | 致命傷 |
|---|---|---|---|
| **A：Web 行程內排程（推薦）** | 分析服務搬 Core，Web 加 `BackgroundService` 排程器，行程內直接跑分析 | 時間窗可用 `CancellationToken` 優雅停止（停在主機日邊界）；手動觸發即時、進度即時可視；單一服務部署 | 需要一輪服務搬遷重構；Web 服務帳號需要本機 Event Log 權限（僅影響本機直讀部分） |
| B：Web 排程器啟動 console exe 子行程 | Web 只管時間到 `Process.Start("LogForesight.exe")` | 重構最小、權限隔離不變 | **時間窗到只能砍行程**——kill 不會走 `BatchRunRecorder` 的 using-dispose，執行紀錄會卡「執行中」，正好復刻 2026-07 已修掉的問題；手動觸發指定主機需要為 exe 加參數協定；進度只能撈檔案 log |
| C：console 改常駐 Windows 服務，自己輪詢 DB 排程設定 | Web 純 UI 寫設定，批次服務讀設定執行 | 職責分離最徹底 | 兩個常駐服務；手動觸發要靠「DB 寫觸發列＋服務輪詢」的偽 IPC，延遲與狀態機都是新複雜度；與「不新增排程簿記」哲學相悖 |

**採 A**。B 的執行紀錄完整性缺陷與 C 的觸發偽 IPC 都是結構性的，不是調參能解。

### 1.4 詳細設計（方案 A）

#### 1.4.1 服務搬遷（console → Core，行為零改變的前置重構）

搬遷清單（`LogForesight/Service/` → `LogForesight.Core/`，namespace 維持
`LogForesight` 不變，序列化與測試零影響）：

- `LogAnalysisService`、`RiskReportService`、`EventLogService`、`EventRecordMapper`
- `NetiqPipelineService`、`SentinelConnectionFactory`、`HostListProviders`
- `WeeklyCheckupService`、`PermissionMonitorService`
- `BatchRunRecorder`、`ExportReportPruner`
- 不搬：`SelfTestRunner`、`RuleImporter`、各 CLI 類別（`HostListCli`／
  `SuppressionCli`／`NetiqProbeCli`）留在 console。
- **`RuleBootstrapper` 已提前搬 Core 並在 Web 端接線**（2026-07-31，
  docs/FEEDBACK-5-PLAN.md §10）：全新環境 Web 開站原本假設「批次已 bootstrap
  過」，規則維護頁對著空 blob 直接 500。Web `Program.cs` 啟動時現與批次共用
  同一份 `RuleBootstrapper.LoadContent`（不呼叫 `Run`，避免無謂初始化
  `KnownIssueCatalog` 全域分類狀態）＋種子鏡像同步，此段搬遷提前完成、
  不需等 Phase 2 整批動。

Core 目前已含 `AIService`、`SentinelClient`、分析五層、全部 store——搬遷主要是
機械性移動＋console csproj 引用不變，風險低但檔案數多，**獨立成一個 phase、
單獨驗證 1163 測試全綠再繼續**。

#### 1.4.2 AnalysisOrchestrator 抽取（拆解 Program.cs）

把 `Program.cs` 的主流程（權限檢查→清理→本機分析→NetIQ→體檢）抽成 Core 的
`AnalysisOrchestrator`，收斂為單一入口：

```csharp
public class AnalysisOrchestrator
{
    // scope: Full（排程/手動全部）| LocalOnly | NetiqHosts(IReadOnlyList<long> hostIds)
    // backfillOverride: 手動觸發時的一次性回補天數覆寫（null＝用 NetiqOptions.BackfillDays）
    Task<OrchestratorResult> RunAsync(RunScope scope, int? backfillOverride,
        IRunConsole console, CancellationToken ct);
}
```

設計要點：

- **`IRunConsole` 輸出抽象**：`Program.cs` 現在滿地 `Console.WriteLine`＋
  `WithColor`。抽介面（`Info/Warn/Alert/Section` 這類語意方法），console 端
  adapter 保留現有彩色輸出**逐字不變**，Web 端 adapter 落到 NLog＋
  `BatchRunRecorder` milestone。這是本重構工作量最大的一塊——但不做的話
  Web 執行時所有進度只進黑洞。
- **每次執行重建服務**：orchestrator 每次 `RunAsync` 重新讀 `SystemSettings`／
  `NetiqOptions`、重建 `AIService` 等（現在 one-shot 天生如此）；Web 常駐後
  不能把啟動時的設定快照用到天荒地老。
- **CancellationToken 全線貫通**：`NetiqPipelineService` 已有 ct（逐日迴圈
  `ThrowIfCancellationRequested`）；**本機逐日分析迴圈目前沒有 ct，需補**。
  取消語意＝停在「主機日」邊界（當前這一天分析完才停），AI 呼叫本身有逾時，
  不硬掐。
- **具名 Mutex 保留**：orchestrator 執行前取 `Global\LogForesight`——這是
  Web 排程與「有人在伺服器上手動跑 console」之間唯一的跨行程互斥，拿不到就
  記警告並跳過本次（與現行 console 行為一致）。行程內另有單一執行 gate
  （`SemaphoreSlim(1,1)`），手動觸發撞上排程執行中時直接拒絕並提示，不排隊。
- **exit code 語意改為 `OrchestratorResult`**：console 端映射回 0/1；Web 端
  寫入 BatchRun 的成敗欄位。

console 的 `Program.cs` 改為：CLI 分派（不變）＋`AnalysisOrchestrator.RunAsync(
Full, null, consoleAdapter, CancellationToken.None)`。**對既有部署，console 行為
逐字不變**——這是 Phase 2 的驗收標準。

#### 1.4.3 排程設定模型（新 blob：`schedule_options`；2026-07-31 定案 #1：多執行窗口）

```csharp
public class ScheduleWindow
{
    public string Start { get; set; } = "01:00";   // HH:mm，本地時區
    public string End { get; set; } = "07:00";     // 支援跨午夜（22:00 → 06:00）
}

public class ScheduleOptions
{
    public bool Enabled { get; set; } = false;      // 預設關閉：升級後行為不變，schtasks 續用
    public List<ScheduleWindow> Windows { get; set; } = new() { new ScheduleWindow() };
    public bool DebugDump { get; set; } = false;    // AI 診斷傾印，見 1.4.10
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
}
```

語意定案（皆為可測的純函數 `ScheduleCalculator`）：

- **多窗口**：每個窗口的 `Start` 到點觸發一次完整執行（`RunScope.Full`），
  `End` 到點對進行中的執行發 cancel（優雅停止，停在主機日邊界）。窗口數上限
  **4**（後端常數）——分析單位是「日」，更多窗口沒有對應的工作量。仍不做
  cron 表達式。
- **第二個以後的窗口天生是「補跑窗口」，不需要任何簿記**：執行冪等——前一
  窗口已完成的主機/日期在下一窗口被 `HasRecord` 跳過（NetIQ 主機的缺漏判定在
  發 Sentinel 查詢**之前**，全數完成時連查詢都不會發）；權限異動檢查每窗口照做
  一次（「每次執行都做」的既定語意，成本極低）；體檢 due-date 未到期或已完成
  不重跑。因此工作全部做完時後續窗口是快速 no-op，前一窗口被 `End` 中斷時則
  自動續跑剩餘主機/日期——這正是 1.2 說的「冪等是搬遷最大資產」。
- **儲存驗證（後端強制，非僅 UI）**：`HH:mm` 格式、`Start != End`、至少一個
  窗口、窗口間不重疊（跨午夜窗口先正規化成分鐘區間再驗）。重疊直接拒存並指出
  哪兩組衝突——重疊會造成「一邊要停、一邊要跑」的矛盾，不做聰明合併。
- **漏跑補償**：Web 服務啟動時，若當下位於某窗口內、且該窗口今日尚未觸發過
  （查 BatchRun 當日 `Trigger=schedule` 紀錄），補觸發一次；已出窗則等下一
  窗口——與工作排程器「錯過即跳過」一致，不做更聰明的事。
- **`Enabled=false` 的部署仍走 schtasks**：兩軌並存靠具名 Mutex 保證不重疊，
  過渡期見 1.5。

#### 1.4.4 手動觸發（2026-07-31 定案 #2：不受窗限、可手動停止、大量執行先確認、可選網段）

新增 API（皆 `Maintain` 能力，寫稽核）：

- `GET  /api/admin/schedule/run-preview?scope=all|segment&segment=…`——執行前
  預覽：回傳該範圍**實際會被查詢**的主機台數與排除統計（複用
  `StoreHostListProvider` 的清單語意，與 `--host-list` 同一套「不靜默少幾台」
  原則），供前端確認對話框顯示「目前有 XX 台，是否確定執行？」。
- `POST /api/admin/schedule/run`——body
  `{ scope: "all" | "segment" | "host", segment?, hostId?, backfillDays? }`：
  - `all`：等同排程觸發的完整執行（含本機直讀＋全部 NetIQ 主機）。
  - `segment`：只跑 IP 落在指定網段的 NetIQ 主機。網段輸入語法**與 NetIQ 匯入
    精靈一致**（前綴 `10.1.2` 或 CIDR `10.1.2.0/24`），解析邏輯共用同一
    份、不寫第二套；解析後無任何主機符合 → 拒絕並提示。本機直讀主機不參與
    網段範圍（它不在 NetIQ 清單，要單獨跑用 `host`）。
  - `host`：單一主機（本機直讀主機走 `LocalOnly`；NetIQ 主機走
    `NetiqHosts([hostId])`，orchestrator 過濾清單後跑同一條 pipeline）。
  - `backfillDays`（1..14）：一次性覆寫，不落地設定。
- `POST /api/admin/schedule/cancel`——優雅停止進行中的執行（**排程觸發或手動
  觸發皆可停**），停在主機日邊界；BatchRun 正常回填並記里程碑「使用者手動停止
  （{帳號}）」——是「已停止」不是「失敗」，更不是卡「執行中」。
- `GET  /api/admin/schedule/status`——閒置/執行中（觸發來源、當前 milestone、
  可否停止）、下次窗口觸發時刻。

確認機制（定案 #2「數量較多要先跳 alert」）：

- 前端**一律**先打 run-preview、在確認對話框顯示台數；台數 ≥ **50**（前端常數）
  時對話框加強警示（紅字提醒白天對 Sentinel 的查詢負載，並建議改用網段範圍
  縮小）。伺服器端不擋台數——管理者確認後就是明確意圖，真正的保護是全站單一
  執行 gate 與既有 Sentinel 節流設定（`QueryDelayMs` 等）。
- **手動觸發不受時間窗限制**；在窗外觸發時確認對話框附註提醒。

其他定案點：

- 手動觸發的執行同樣寫 BatchRun（`BatchRun` 模型增加 `Trigger` 欄位：
  `schedule`／`manual:{帳號}`／`console`；舊紀錄 null 顯示「工作排程器」），
  Runs 頁一個欄位就能回答「昨晚那次是誰跑的」。
- 「手動觸發**更新**指定主機」＝重新分析該主機缺漏日；已分析日仍冪等跳過。
  「強制重析已有紀錄的日子」屬另一個功能（要先刪紀錄），**本輪不做**——
  與「同一天重複執行不產生重複紀錄」的承諾衝突，真有需求另案。

#### 1.4.5 UI

「系統管理 > 排程」不另開新頁，**併入既有「執行監控」（Runs）頁**頂部新增
「排程設定」卡：

- Enabled 開關、窗口清單編輯（最多 4 組 Start/End，重疊即時提示）、
  AI 診斷傾印開關（開啟時顯示 1.4.10 的警示徽章）、下次觸發時刻；
- 目前執行狀態：進行中顯示觸發來源＋最新 milestone＋「停止」鈕；
- 「立即執行」鈕開對話框：範圍二選一（全部主機／網段輸入框）、可選
  `backfillDays`，即時顯示 run-preview 台數，≥50 台紅字加強警示，確認後送出。

「指定主機更新」按鈕放**主機詳情頁**（就近原則：看著這台主機覺得資料舊了，
當場按），帶 `backfillDays` 輸入，同樣先顯示確認。

#### 1.4.6 Web 端設定來源整併

orchestrator 需要的批次 `appsettings.json` 區段（`Ai` 節流參數、
`Analysis.Channels`／`ServerDescription`／`CheckupIntervalDays`、
`Permissions.WatchedFolders`）：Web 的 `appsettings.json` **新增同名區段**、
沿用 Core 既有 `AppSettings` 綁定（AI 位址/金鑰與保留天數本來就以 DB 為事實
來源，不受影響）。刻意不把這些搬 DB——它們是「隨部署的基礎設施參數」，
與規則/主機這類業務資料不同，維持檔案配置。批次與 Web 兩份 appsettings 過渡期
並存（各自服務自己的執行路徑），schtasks 退場後批次那份只剩 CLI 工具在用。

#### 1.4.7 權限異動監控的基準目錄變化

`PermissionMonitorService` 恆監控「執行檔自身目錄」＋快照存執行檔目錄——排程
搬 Web 後：

- 監控對象從 `Batch\` 變成 `Web\` 目錄（自我防竄改語意不變，保護的是實際執行
  分析的那個行程的 exe）；`Batch\` 目錄若仍想監控，加進 `WatchedFolders` 即可
  （部署文件寫明）。
- 快照檔 `permission_snapshot.json` 落 Web 目錄；**首次以 Web 執行時重建基準、
  不告警**（既有「無快照只建基準」行為天生涵蓋，無需新碼），但要在部署文件
  提醒「切換首晚不會有權限告警是預期行為」。

#### 1.4.8 本機直讀的 Security log 權限（部署決策，非程式問題）

console 排程可用 SYSTEM 跑；Web 服務帳號若沒進 Event Log Readers／管理員群組，
本機直讀部分的 Security 頻道會讀取被拒。三個緩解層次：

1. 部署文件指引（2026-07-31 定案 #4）：對部署當下那組 Web 服務帳號設定使用者
   權限——加入本機「Event Log Readers」群組即可（比 SYSTEM/管理員權限小得多，
   符合最小權限）；其餘維運操作交由 Web 內 admin 群組使用者處理，不做程式面的
   權限自檢。
2. 讀不到時**既有誠實申報機制原樣生效**（`SecurityLogAvailable=false`、
   `UncoveredChecks` 逐條列出）——不會靜默漏偵測，這條防線 2026-07 就建好了。
3. 真實環境本來就常以無管理員權限跑（README 明講），此變更沒有讓任何事變差。

正式環境的主力是 NetIQ 主機（Sentinel 取數，無本機權限需求），本機直讀只涵蓋
Web/批次所在那台伺服器自己，影響面有限。

#### 1.4.9 內建規則升級搬 Web（承接 `--import-rules`）

現況：console 啟動時比對 seed 版本提示「內建規則有更新（vX→vY）」；
`--import-rules` 預覽（列新增/更新/略過/衝突，不寫檔）、`--apply` 套用、
`--overwrite-builtin` 連同被使用者改過的 builtin 一併覆蓋（保留 `Enabled`
設定）；custom 規則永不觸碰。邏輯在 console 的 `RuleImporter`，計算與
`Console.WriteLine` 輸出混在一起。

設計：

- **重構（先拆純函數，兩端共用）**：`RuleImporter` 拆成 Core 的
  `RuleImportPlanner.Plan(seed, current) → RuleImportPlan`（逐條分類
  新增/更新/略過/衝突，零 I/O、可單測）＋`Apply(plan, overwriteBuiltin)`；
  **過渡期 console CLI 改為薄包裝，輸出格式逐字不變**——既有使用者照 README
  的升級 SOP 操作時看到的畫面完全一樣，這是「不影響現有功能」的把關點。
- **Web 規則頁**：清單頁已有「有新版種子可匯入」徽章基礎；加頁頂橫幅
  （庫內版本 < seed 版本時顯示「內建規則有更新 vX→vY」）→「預覽差異」對話框
  （逐條列四類，衝突＝使用者改過的 builtin，預設不勾）→「套用」按鈕（附
  checkbox「連同已修改的內建規則一併覆蓋（保留啟用狀態）」＝`--overwrite-builtin`
  語意）。套用走既有儲存前驗證管線（欄位/遮蔽/關聯覆蓋檢查原樣生效），
  需 Maintain 能力、寫操作稽核。
- **行為對等測試**：同一份 seed 與庫內容，`RuleImportPlanner` 的分類結果與
  CLI 預覽逐項一致——過渡期兩個入口並存時的防漂移閘門。

#### 1.4.10 AI 診斷傾印開關（承接 `--debug-dump`）

現況：`--debug-dump` 讓 `AIService` 掛 `FilePromptDumper`，每次 AI 呼叫的完整
prompt 與原始回應各輸出一檔到執行檔目錄 `diag\`；README 明講驗證完就該關
（持續佔磁碟）。

設計：

- `ScheduleOptions` 加 `DebugDump`（bool，預設 false）。放排程設定、不放
  `SystemSettings`——它作用於「下一次批次執行」，與排程卡同一生命週期，
  管理者在同一張卡上開關並看得到提醒。
- orchestrator 每次執行時讀取（1.4.2「每次執行重建服務」天生支援），true 時
  掛 `FilePromptDumper`，輸出目錄改為 `{DataRoot}\diag\`（執行行程換成 Web 後
  不落 Web 程式目錄，與 export\ 同樣統一到資料根）。
- Runs 頁排程卡於開啟時顯示醒目徽章「AI 診斷傾印開啟中（持續佔用磁碟，驗證完
  請關閉）」。**不做自動關閉**——隱式關閉會讓「怎麼沒 dump」變成新的謎題，
  與顯式原則相悖。
- 過渡期 console 的 `--debug-dump` 照舊（輸出仍在 exe 目錄 `diag\`，行為不變）；
  退役後只剩 Web 這一條。

#### 1.4.11 `--selftest` 與 `--netiq-probe` 的處置

- **`--selftest`：不搬、隨 console 退役**。三個理由：(1) 它的核心價值
  「不連 DB、零副作用、乾淨環境可跑」在 Web 端點上天生不成立（Web 活著＝DB
  已建好，違背零副作用承諾）；(2) 它驗的是內建種子，該邏輯已被單元測試
  （對內建種子逐條規則自動產生案例）與 Web 儲存前驗證（實際生效內容那一層）
  雙重涵蓋；(3) 集中式架構下 exe 只部署在一台伺服器，「換機部署前先跑」的
  場景本身大幅縮水。退役時 README 的 selftest 章節改寫為指向測試套件與
  Web 儲存前驗證。
- **`--netiq-probe`：搬 Web，必做**（2026-07-31 定案 #9「直接移除 console」
  的連帶必要條件——Linux Sentinel 接入的 P3 閘門還要再跑一輪 probe，console
  移除後必須有替代載具）。設計：NetIQ 維護頁加「診斷」分頁——選一台已設定的
  Sentinel、可選填樣本 IP（Windows/Linux 各一，對應 `--sample-ip`／
  `--sample-linux-ip`）→ 執行同一組驗證查詢（`NetiqProbeCli` 的查詢邏輯拆成
  Core 純服務 `NetiqProbeRunner`，過渡期 CLI 與 Web 共用同一份、既有 stub HTTP
  單元測試沿用）→ 完整診斷文字放唯讀 textarea＋「複製」鈕（**輸出契約不變**，
  仍是設計來貼回對話定案欄位形狀的純文字）。probe 屬長耗時操作（逐台 Sentinel
  十幾個查詢），走「觸發→背景執行→輪詢結果」避免 HTTP 逾時；需 Maintain
  能力、寫稽核（對 Sentinel 的主動查詢操作）；與排程/手動分析共用全站單一
  執行 gate **之外**、自成一個 probe gate（併發 1）——probe 是小規模診斷查詢，
  不該被夜間分析互斥擋住，但同時只允許一個 probe 在跑。

### 1.5 退場（直接移除，2026-07-31 定案 #9）

不保留過渡期薄殼：Phase 4 完成 CLI 職責搬 Web（含 probe 診斷分頁）後，
Phase 5 一次完成切換與移除，依序執行：

1. 部署含 Web 排程的版本，`Enabled` 仍 false → 確認發布本身零行為變化。
2. 開 `Enabled`、**停用（暫不刪）schtasks**——此時 console exe 還在磁碟上，
   是最後的熱回退窗口。
3. 連續 **≥5 晚**驗證（定案 #5）：Runs 頁紀錄完整且無卡「執行中」殘留、
   export 報告與過去格式一致、風險判定結果與預期相符。
4. 驗證通過 → 刪 schtasks 工作；自解決方案移除 `LogForesight`（console）專案
   （`SelfTestRunner`、各 CLI 類別隨之刪除，Core 內只被 console 用到的殘留
   一併清理）；部署面移除 exe 與批次版 appsettings.json。
5. 文件收尾：README 全面改寫（架構圖的 console 節點、「使用方式」、selftest
   章節、部署章節、規則升級 SOP 改指 Web）；docs/HISTORY.md 補「決策 20 修訂」
   條目；部署文件的目錄配置圖更新。

**`Batch\` 目錄的資料不動**：`logforesight.db`（若 Sqlite）與 `export\` 是
資料不是程式，`Storage:DataRoot` 指向不變，只移除執行檔。

**回退路徑（誠實申報）**：

- 步驟 3 期間（exe 還在）：重啟 schtasks 即**熱回退**，零風險。
- 步驟 4 之後：只剩**冷回退**——git revert 專案移除 commit＋重建部署 exe＋
  重建 schtasks。資料層無任何需要回滾的遷移（JSON 反序列化容忍未知欄位、
  SchemaUpgrader 只加不減），冷回退成本純粹是重建部署的工時。**Phase 5 收尾
  前在測試環境做一次冷回退演練**（revert→build→跑一晚），確認這條路真的
  走得通，不是紙上承諾。
- 接受的殘餘風險（使用者知情選擇，定案 #9）：移除後 Web 排程出問題只能修、
  不能熱退。緩解：步驟 3 的 ≥5 晚驗證把問題盡量擋在移除前；且分析冪等——
  修復期間漏跑的日子靠缺漏回補自癒，不會永久缺資料。

具名 Mutex（`Global\LogForesight`）**保留**在 orchestrator——成本趨近零，
防未來任何第二行程誤配置（例如兩個 Web 實例被誤設指向同一 DataRoot）。

### 1.6 影響面清單

| 區塊 | 影響 |
|---|---|
| `LogForesight/Service/*`（11 檔） | 搬 Core；console csproj 變薄 |
| `Program.cs` | 主流程抽出後剩 CLI 分派＋adapter，約砍 6 成 |
| `LogForesight.Core` | 新增 orchestrator、`IRunConsole`、`ScheduleCalculator`、`ScheduleOptions`＋store（blob，`lf_blobs` key=`schedule_options`，零 DDL） |
| `LogForesight.Web` | 新 `BackgroundService`（`SchedulerHostedService`）、`ScheduleController`、Runs 頁排程卡（含 DebugDump 開關與徽章）、主機詳情頁觸發鈕、appsettings 新區段、DI 註冊 |
| `BatchRun` 模型 | 加 `Trigger` 欄位（JSON 反序列化容忍缺欄，零遷移） |
| Web 規則頁 | seed 版本橫幅＋「預覽差異/套用」對話框；`RuleImporter` 拆 Core 純函數 `RuleImportPlanner`（1.4.9） |
| Web NetIQ 維護頁 | 「診斷」分頁（probe Web 化，1.4.11）；`NetiqProbeCli` 查詢邏輯拆 Core `NetiqProbeRunner` |
| console（Phase 5） | 專案自解決方案移除、CLI 類別與 `SelfTestRunner` 刪除；`Batch\` 只移除 exe 與 appsettings，資料目錄不動 |
| 測試 | 搬遷後全綠是 Phase 2 閘門；新增 `ScheduleCalculator`（多窗口/跨午夜/重疊驗證/漏跑補償）、觸發 API 授權/驗證＋run-preview、取消停在日邊界（stub pipeline）、`RuleImportPlanner` 與 CLI 對等測試、`NetiqProbeRunner` 沿用既有 stub HTTP 測試 |
| 文件 | README（架構圖、排程章節、使用方式、selftest、部署）、WEB-SPEC（新 API/頁面）、HISTORY（決策 20 修訂） |

### 1.7 風險與緩解

- **Web 重啟砍斷夜間執行**：缺漏回補天生自癒；`BatchRunRecorder` 在 host 的
  graceful shutdown（`IHostApplicationLifetime`）下正常 dispose 回填「失敗」。
  硬崩潰則卡「執行中」——與現行 console 被 kill 的行為相同，沒有變差。
- **長時執行（最壞 2.5h）佔 Web 行程資源**：分析主要是等 AI 推論的 I/O 等待，
  單一背景執行緒＋既有 AI 單一佇列，對 Web 請求處理無實質壓力；Sqlite 同行程
  併發反而比跨行程更單純。
- **重構半途的行為漂移**：Phase 2 嚴格「只搬不改」，靠 1163 測試＋console 輸出
  逐字比對把關。

---

## §2 需求二：風險 log 暫存資料庫

### 2.1 現況與缺口

- 原始事件只活在分析當下的記憶體；落地的只有：簽章的 `SampleMessages`
  （3 則×200 字）、風險報告 txt 的「相關原始 Log」（全站預算 20 筆）。
- 「詢問 AI」的現場取數（`SentinelEventFetchService`）是**即時打 Sentinel**：
  15 秒逾時、全站併發 1、10 分鐘快取、預設關閉、**只支援 NetIQ 主機**——
  本機直讀主機完全沒有原始事件可注入。
- 缺口：白天問 AI 要嘛吃 Sentinel 即時查詢的延遲與負載，要嘛（本機主機/開關
  關閉時）只有 200 字截斷樣本可看。

### 2.2 設計

#### 2.2.1 資料表（新表 `lf_risky_events`，走 `SchemaUpgrader` 冪等 DDL）

| 欄位 | 型別 | 說明 |
|---|---|---|
| `id` | bigint identity PK | |
| `host_id` | bigint | 歸戶鍵（同 `lf_daily_records` 慣例） |
| `date` | date | 分析日（不是寫入時刻） |
| `log_name` / `source` / `event_id` / `entry_type` | 同簽章四元組 | 查詢鍵 |
| `event_time` | datetime | 事件原始時間戳 |
| `message` | nvarchar(max) | 原文，**截 2000 字**（比 200 字樣本豐富一個量級，仍有硬上限） |
| `rule_id` | nvarchar(64) null | 命中規則 Id（供未來頻率報表，順手帶） |
| `created_at` | datetime | 清理與除錯用 |

索引：`ix_lf_risky_events (host_id, date, source, event_id)`（AI 對話的查詢形狀）
＋`ix_lf_risky_events_date (date)`（清理用）。

#### 2.2.2 入庫範圍（確定性規則，非「整天全存」）

單一咽喉點：`LogAnalysisService.AnalyzeDayAsync` 同時看得到原始事件與判定結果，
且**本機直讀與 NetIQ 兩條路徑都經過它**——在分析完成後、寫歷史紀錄旁，把
「有風險的 log」交給注入的 `IRiskyEventSink`（console/Web 都接 DB 實作；
單元測試接 null sink）。

「有風險」的簽章資格（2026-07-31 定案 #3＋邊界定案 #7/#8，任一命中即入庫）：

1. **命中規則表的簽章（含 Low「收集用」規則、含被抑制的）**——定案 #7 字面
   套用「規則命中就存」：日常 RDP/SSH 成功登入也照存，量由每簽章/每主機日
   上限與保留天數控制。好處是關聯訊號日（【破解得手】【暴力破解→RDP 得手】）
   的成功登入面原文證據**永遠在庫**，不用碰運氣；代價是穩態體積上升，
   估算見 2.3，收斂路徑也先講好在 2.3。
2. **出現在 `TrendAlerts` 的簽章**（定案 #8）——補齊未命中規則的 Other 類
   頻率異常的原文覆蓋；這類未知型態問題恰好最需要看原文。

關聯訊號的構成事件本身都是規則表內的事件 ID，資格 1 已天然涵蓋攻擊鏈/故障鏈
的原文證據，無缺口。

呈現量硬上限（與小模型策略同一哲學，防爆量日灌爆 DB）：

- 每簽章每日最多 **50 筆**（取時間軸首尾各半：首端看開始樣態、尾端看最新狀態；
  超出在該簽章標記截斷）；
- 每主機每日合計最多 **500 筆**（依嚴重度排序後截斷）；
- 低風險日若無任何合格簽章，自然零寫入，無需日風險門檻條件。

冪等：寫入前先 `DELETE WHERE host_id AND date`——與 `HasRecord` 跳過機制配合，
正常不會重寫；手動重析（未來若有）也不會殘留舊列。

#### 2.2.3 保留天數（設定頁可調）

- `SystemSettings` 新增 `RiskyEventRetentionDays`，**預設 14**；驗證
  `1 ≤ 值 ≤ RetentionDays`（暫存活得比分析紀錄久沒有意義）。
- 「系統管理 > 設定」頁保留天數區新增一欄，說明文字明講用途與空間代價。
- 清理掛在既有夜間清理段（`Program.cs` 步驟 1b 旁；排程搬 Web 後隨 orchestrator
  走，同一段程式）。

#### 2.2.4 讀取路徑（AI 對話整合）

`AiController.Chat` 首輪取數順序改為：

1. **先查 `lf_risky_events`**（host_id＋date＋source＋event_id，倒序取
   `MaxInjected`=20 筆）——毫秒級、不打 Sentinel、**本機主機也有**；
2. 查無（老於保留期、功能上線前分析的日子、不合格簽章）時 fallback 既有
   `SentinelEventFetchService` 即時查詢（維持 `ChatLiveFetchEnabled` 開關與
   全部節流語意不變）。

注入 prompt 的區塊沿用 `BuildLiveEventsBlock` 的圍欄與「非指令」聲明（內容同樣
是攻擊者可控字串，防線不因來源換成 DB 而減少）；`FetchedLogCount` 語意擴為
「本輪注入的原始事件則數」，前端顯示不變。授權繼承既有 `GetDetail` 可見範圍
（查詢以該次已驗證的 host_id 為鍵，不另開授權面）。

選配（本輪不做、列為後續可能）：風險日詳情頁直接顯示暫存原始 log 區塊——價值
明確但屬 UI 新功能，與本需求（餵 AI）分開議。

#### 2.2.5 實作定案與規劃的差異（2026-07-31 實作＋體檢後補記）

Phase 1 已實作完成（1193 測試綠），三處與規劃字面不同、皆為刻意選擇：

1. **寫入掛接點在呼叫端，不注入 LogAnalysisService**：規劃原寫「交給注入的
   IRiskyEventSink」；實作改為 `Program.cs` 主迴圈與 `NetiqPipelineService` 在
   `AnalyzeDayAsync` 返回後呼叫 `RiskyEventSelector.Select`＋`ReplaceDay`——
   與問題案件掛接（`IssueCaseCoordinator.AttachNewDay`）**完全相同的既有掛接
   模式**（同樣的兩個掛接點、同樣的 try/catch 失敗邊界），`LogAnalysisService`
   零改動。選取邏輯仍單一在 `RiskyEventSelector`（Core 純函數），單一咽喉的
   本意（不會兩條路徑各寫一套規則）未失。
2. **趨勢資格用結構化 `Trend` 欄位（New/Rising）判定，非回頭比對 TrendAlerts
   告警字串**：字串反向解析脆弱；代價是涵蓋面略寬於告警清單（未達告警門檻的
   低嚴重度首次出現、暖身期 Rising 也入庫）——這類未知型態恰好最需要原文，
   多存的量仍受兩層上限約束。
3. **新增保留期閘門 `WithinRetention`**（規劃未提的體檢補強）：回補超過暫存
   保留期的日子（首次執行 120 天深度回補最典型）跳過寫入——寫進去的列下次
   執行就被 Prune 清掉，純屬浪費。兩條路徑同一閘門。

另兩點如實記錄：文件更新落在 WEB-SPEC §9.3／§9.9a／§9.9b（README 沒有 AI 對話
章節，§2.4 原列的「README（AI 對話章節）」不適用）；`BuildLiveEventsBlock` 圍欄
原文寫死「Sentinel 即時查詢」已改為不標注來源（事件現在多數來自暫存，
標注會說謊）。

### 2.3 容量估算

單筆上限約 4KB（2000 字 nvarchar）；最壞情境 2000 台全數當日 500 筆封頂
＝100 萬列/日×4KB≈4GB/日——但那是「全站同日全部淪陷」的末日場景。

定案 #7（Low 收集用規則照存）後，**穩態體積的主要來源是 RDP/SSH 收集面**：
有 RDP 維運活動的主機每天穩定寫入數十筆（四個 RDP 簽章合計、受每簽章 50
上限管）。以 2000 台、平均 30 筆/日、單筆約 1KB 估算 ≈ 60MB/日 → 14 天穩態
≈ **840MB**。SqlServer 可承受；SQLite（開發/測試）台數少、無虞。試點觀察若
超出預期，收斂路徑依序是：(1) 把 Low 收集面改為「當日有關聯訊號才存」
（`RiskyEventSelector` 一行資格判斷，已知的第一顆旋鈕）；(2) 調小每簽章/
每主機日上限（常數）；(3) 把上限開成設定——不預先做，避免「有設定無行為」
的紅線風險。

### 2.4 影響面清單

| 區塊 | 影響 |
|---|---|
| Core | `RiskyEvent` 模型＋`IRiskyEventStore`（含 `ReplaceDay`/`Query`/`Prune`）＋兩後端實作＋`StorageFactory` 路由＋`SchemaUpgrader` 新表步驟；`LogAnalysisService` 建構子加可選 sink 參數＋挑選邏輯（純函數抽出：`RiskyEventSelector`，可單測） |
| console | `Program.cs` 建 sink 傳入（一行級）；夜間清理加一段 |
| Web | 設定頁欄位＋`SystemSettingsService` 驗證；`AiController.Chat` 改取數順序；DI 註冊 |
| 測試 | `RiskyEventSelector`（資格/上限/首尾取樣）、store 合約測試（兩後端）、清理、設定驗證、chat 取數優先序（DB 命中→不打 live fetch；未命中→fallback） |
| 文件 | README（AI 對話章節）、WEB-SPEC §9.3/設定頁 |

不動的東西：五層偵測、風險判定、報告產出、`SampleMessages`、live fetch 本體
——全部原樣，此案純加值層。

---

## §3 兩案關係與實作順序

需求二完全不依賴需求一（寫入咽喉點在 `LogAnalysisService`，誰排程誰執行都一樣）。
建議順序：

| Phase | 內容 | 出場閘門 |
|---|---|---|
| 1 ✅ | 需求二全部（表/寫入/清理/設定/對話整合）——**2026-07-31 完成**（含體檢，實作註記見 §2.2.5） | 已過：1193 測試綠；設定頁與 schema 升級（既有 DB 補建表）已於 dev server 實機驗證 |
| 2 | 服務搬遷＋orchestrator 抽取（**只搬不改**） | 1163+ 測試全綠；console 輸出與現行逐字一致；`--selftest` 照常通過 |
| 3 | Web 排程引擎＋設定/觸發 API＋UI＋稽核 | `Enabled=false` 發布零行為變化；schtasks 執行完全不受影響 |
| 4 | CLI 職責搬 Web：規則升級橫幅/套用（1.4.9）＋AI 診斷傾印開關（1.4.10）＋NetIQ 診斷分頁（probe Web 化，1.4.11） | `RuleImportPlanner` 對等測試綠；probe Web 輸出與 CLI 輸出對同一 Sentinel 一致；console CLI 輸出逐字不變 |
| 5 | 切換與移除：試點 ≥5 晚 → 刪 schtasks＋移除 console 專案＋README/HISTORY 收尾 | 試點通過；冷回退演練通過（見 1.5） |

Phase 1 隨時可先行；Phase 2~5 嚴格依序。每個 Phase 各自是一個可獨立合併的
發布單位（依既有分支流程：feature → dev 驗證 → master）；Phase 5 的移除
步驟前均可熱回退。

### §3.1 行為保護策略（「不影響現有功能」的不變式）

跨 Phase 通用原則：

- **新欄位一律容忍缺席**：`ScheduleOptions`（新 blob key，舊程式讀不到不礙事）、
  `BatchRun.Trigger`（nullable，舊紀錄顯示「工作排程器」）、
  `SystemSettings.RiskyEventRetentionDays`（缺欄用預設 14）——新版讀舊資料用
  預設值，舊版讀新資料自動忽略，**雙向零遷移**。
- **DB 只加不改**：新表 `lf_risky_events` 走 SchemaUpgrader 冪等 DDL，既有表
  零觸碰；任何 Phase 回退都不需要資料層回滾。
- **預設值即現狀**：`ScheduleOptions.Enabled=false`、`DebugDump=false`、
  暫存保留 14 天——每個 Phase 發布當下，不動設定的部署行為與發布前逐位一致。
- **console 行為凍結**：直到 Phase 5 移除前，console 的輸出、exit code、CLI
  介面一律不變（Phase 2 的「只搬不改」與 Phase 4 的薄包裝都以此為驗收線）；
  這保證移除前的任何時點「重啟 schtasks」都是完整的熱回退路徑；移除後轉為
  冷回退（1.5 誠實申報＋演練要求）。
- **雙軌互斥**：具名 Mutex `Global\LogForesight` 全程保留，Web 排程與 console
  手動/schtasks 執行永不重疊（後到者跳過並記警告，與現行行為一致）。

### 定案紀錄（2026-07-31 使用者回覆）

1. **時間窗**：優雅停止＋下個窗口自動續跑，**並支援設定多個執行窗口**
   （設計見 1.4.3：`ScheduleOptions.Windows`，上限 4 組、不重疊驗證）。
2. **手動觸發**：不受時間窗限制、可手動停止；執行前一律顯示台數確認
   （≥50 台加強警示）、支援網段範圍執行（設計見 1.4.4）。
3. **暫存入庫資格**：規則命中就存（見 2.2.2；附兩個邊界待確認 #7/#8）。
4. **Web 服務帳號**：對部署那組服務帳號設定使用者權限（Event Log Readers）
   即可，其餘交 Web 內 admin 群組使用者維運（見 1.4.8）。
5. **試點穩定閘門**：連續 ≥5 晚。
6. **Console 完全退役**。
7. **Low「收集用」規則命中照存**（全部規則命中都存，見 2.2.2；體積估算與
   收斂路徑見 2.3）。
8. **趨勢告警簽章納入暫存**（見 2.2.2）。
9. **Console 直接移除，不保留過渡期薄殼**——連帶條件：`--netiq-probe` 必須
   Web 化（1.4.11，Phase 4 必做項）；移除後僅冷回退（1.5 誠實申報＋Phase 5
   收尾前的冷回退演練）。

**全部問題已定案，無剩餘待確認項；可依本節順序開工。**
