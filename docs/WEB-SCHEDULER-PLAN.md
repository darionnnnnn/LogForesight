# Web 排程化與風險 log 暫存規劃（WEB-SCHEDULER-PLAN）

> 規劃日期：2026-07-31。狀態：**規劃完成，未實作**（console 全退役方向已於
> 2026-07-31 與使用者確認）。
> 兩項需求：(1) 檢討 console 批次的去留，把排程職責搬進 Web（自訂排程時間、
> 執行時間區間、手動觸發指定/全部主機、可設定回補天數）；(2) 取回並判定有風險的
> log 暫存資料庫 14 天（設定頁可調），讓「詢問 AI」直接從資料庫取得原始事件。

---

## §0 結論先講

- **Console 定案「終局完全退役、分兩階段退場」**（2026-07-31 使用者確認方向）：
  排程、時間區間、手動觸發、進度可視化全部搬進 Web（方案 A：Web 行程內
  BackgroundService）。**過渡期保留薄殼 console**（行為逐字不變）作為試點期的
  交叉驗證工具、緊急備援、與 Linux Sentinel probe 的載具；退役閘門（Web 排程
  穩定＋Linux probe 定案＋Web 承接功能實際用過）全數通過後，自解決方案移除
  console 專案。屆時各 CLI 的出路：`--import-rules` 搬 Web 規則頁（1.4.9）、
  `--debug-dump` 搬排程設定開關（1.4.10）、`--suppress` 系列與 `--host-list`
  本就被 Web 頁面涵蓋直接刪、`--selftest` 接受退役、`--netiq-probe` 於閘門時
  依剩餘需求決定（1.4.11）。這會**推翻 docs/HISTORY.md 既有決策 20**（one-shot
  ＋工作排程器、Web 不養常駐背景工作），須正式改決策——理由見 §1.2。
- **需求二（風險 log 暫存 DB）合適且獨立**，不依賴需求一，改動面小、價值直接
  （AI 對話從 15 秒的 Sentinel 即時查詢變成毫秒級 DB 查詢，且**本機直讀主機
  首次獲得原始事件注入能力**——現行 live fetch 只支援 NetIQ 主機）。建議先做。
- 實作順序：**Phase 1（需求二）→ Phase 2（服務搬遷重構，只搬不改）→
  Phase 3（Web 排程引擎與 UI）→ Phase 4（CLI 職責搬 Web）→ Phase 5（試點與
  schtasks 退場）→ Phase 6（console 正式退役）**。每個 Phase 獨立可發布、
  獨立可回退，行為保護不變式見 §3.1。

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
| 7 | CLI 工具：`--selftest`／`--netiq-probe`／`--import-rules`／`--suppress` 系列／`--host-list`／`--debug-dump` | 各 CLI 類別 | **逐項處置，終局隨 console 全退役**：import-rules 搬 Web（1.4.9）、debug-dump 搬 Web（1.4.10）、suppress／host-list 已被 Web 規則頁「告警抑制」分頁與主機頁涵蓋（直接刪）、selftest 接受消失、probe 於退役閘門時決定（1.4.11） |

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
- 不搬：`SelfTestRunner`、`RuleImporter`、`RuleBootstrapper`（RuleBootstrapper 其實
  Web 啟動也需要？——現況 Web 假設批次已 bootstrap 過；排程搬 Web 後首次執行
  bootstrap 也應由排程執行前置完成，**RuleBootstrapper 一併搬 Core**）、
  各 CLI 類別（`HostListCli`／`SuppressionCli`／`NetiqProbeCli`）留在 console。

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

#### 1.4.3 排程設定模型（新 blob：`schedule_options`）

```csharp
public class ScheduleOptions
{
    public bool Enabled { get; set; } = false;          // 預設關閉：升級後行為不變，schtasks 續用
    public string StartTime { get; set; } = "01:00";    // 每日觸發時刻（HH:mm，本地時區）
    public string WindowEnd { get; set; } = "07:00";    // 執行窗結束；到點要求優雅停止
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
}
```

語意定案（皆為可測的純函數 `ScheduleCalculator`）：

- **每日一次**，`StartTime` 到點且 `Enabled` 即觸發完整執行（`RunScope.Full`）。
  不做 cron 表達式——本系統的分析單位是「日」，一天多次觸發只會撞上冪等跳過，
  做了是假功能。
- **時間窗跨午夜支援**（`22:00`→`06:00`）：`WindowEnd` 到點時對進行中的執行發
  cancel，pipeline 停在主機日邊界；**未完成的主機/日期不需要任何補償機制**，
  它們就是「缺漏日」，下個窗口的執行靠既有 `HasRecord` 回補自動續跑。這正是
  1.2 說的「冪等是搬遷最大資產」。
- **漏跑補償**：Web 服務重啟錯過 `StartTime` 時，若當下仍在窗內且今日尚未跑過
  （查 BatchRun 當日紀錄），啟動後補觸發一次；已出窗則等明天——與工作排程器
  「錯過即跳過」一致，不做更聰明的事。
- **`Enabled=false` 的部署仍走 schtasks**：兩軌並存靠具名 Mutex 保證不重疊，
  過渡期見 1.5。

#### 1.4.4 手動觸發

新增 API（`Maintain` 能力，寫稽核）：

- `POST /api/admin/schedule/run`——全部主機（等同排程觸發，`RunScope.Full`）。
- `POST /api/admin/schedule/run-host`——body：`hostId`＋可選 `backfillDays`
  （1..14，一次性覆寫，不落地設定）。本機直讀主機走 `LocalOnly`；NetIQ 主機走
  `NetiqHosts([hostId])`（orchestrator 把主機清單過濾到單台後跑同一條 pipeline）。
- `POST /api/admin/schedule/cancel`——要求優雅停止進行中的執行。
- `GET  /api/admin/schedule/status`——目前狀態（閒置/執行中＋當前 milestone、
  是否排程觸發、預計下次觸發時刻）。

定案點：

- **手動觸發不受時間窗限制**——管理者顯式按下按鈕就是明確意圖（白天對 Sentinel
  加查詢負載是他知情的選擇）；UI 在窗外觸發時顯示提醒文字即可。
- 手動觸發的執行同樣寫 BatchRun（`BatchRun` 模型增加 `Trigger` 欄位：
  `schedule`／`manual:{帳號}`／`console`；舊紀錄 null 顯示「工作排程器」），
  Runs 頁 3 個字的欄位就能回答「昨晚那次是誰跑的」。
- 使用者原話「手動觸發**更新**指定主機」＝重新分析該主機缺漏日；已分析日仍冪等
  跳過。若要「強制重析今天已有紀錄的日子」屬另一個功能（要先刪紀錄），**本輪
  不做**——與「同一天重複執行不產生重複紀錄」的承諾衝突，真有需求另案。

#### 1.4.5 UI

「系統管理 > 排程」不另開新頁，**併入既有「執行監控」（Runs）頁**頂部新增
「排程設定」卡：Enabled 開關、StartTime/WindowEnd、下次觸發時刻、目前執行狀態
（進行中顯示最新 milestone＋取消鈕）、「立即執行（全部）」。「指定主機更新」
按鈕放**主機詳情頁**（就近原則：看著這台主機覺得資料舊了，當場按），帶
`backfillDays` 輸入。

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

1. 部署文件指引：Web 服務帳號加入本機「Event Log Readers」群組（比 SYSTEM/管理員
   權限小得多，符合最小權限）。
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
- **`--netiq-probe`：留到退役閘門時決定**。Linux Sentinel 接入的 P3 閘門
  要再跑一輪 probe（已排定的具體用途、輸出貼回對話定案欄位形狀，CLI 最省事）
  ——所以「Linux probe 完成」列為 console 退役的前置閘門。閘門過後二擇一：
  (a) 預期再無 probe 需求 → 隨 console 一起退役；(b) 預期仍要對新 Sentinel
  驗欄位 → NetIQ 維護頁加「診斷」分頁，跑同一組查詢、輸出放可複製的文字區塊。
  本規劃不預先實作，屆時依實際需求定。

### 1.5 兩階段退場

#### 階段一（過渡期，對應 Phase 3~5）：console 保留、行為逐字不變

1. Phase 3 上線後 `ScheduleOptions.Enabled` 預設 false，schtasks 照舊——
   零行為變化的發布。
2. Phase 4 完成 CLI 職責搬 Web（規則升級橫幅/套用、診斷傾印開關）；console
   對應 CLI 照舊可用（同一份 Core 邏輯，防漂移靠 1.4.9 的對等測試）。
3. 試點：開 `Enabled`、**停用（不刪）schtasks**，連續觀察 **≥5 晚**：Runs 頁
   紀錄完整且無卡「執行中」殘留、export 報告與過去格式一致、風險判定結果與
   預期相符。期間 console 的角色：交叉驗證工具（手動跑一次比對輸出）與緊急
   備援（Web 服務出狀況時上伺服器手動跑，具名 Mutex 保證與排程不重疊）。
4. 穩定後刪 schtasks 工作；README「排程（正式環境）」章節改寫、部署文件更新；
   docs/HISTORY.md 補「決策 20 修訂」條目。

#### 階段二（Phase 6）：正式退役

**閘門（全部滿足才動手）**：

- Web 排程已無雙軌依賴（階段一收尾完成後又穩定運行一段時間）；
- Linux Sentinel 已接入並完成該輪 `--netiq-probe`（LINUX-RULES-PLAN P3 定案）；
- 規則升級與診斷傾印的 Web 承接**實際被使用過至少一次**——不是「寫完沒人
  用過」就刪掉備援。

**動作**：

- 解決方案移除 `LogForesight`（console）專案；`SelfTestRunner`、各 CLI 類別
  隨之刪除；Core 內只被 console 用到的殘留（若有）一併清理。
- **`Batch\` 目錄的資料不動**：`logforesight.db`（若 Sqlite）與 `export\` 是
  資料不是程式，`Storage:DataRoot` 指向不變；只移除 exe 與批次版
  appsettings.json。部署文件的目錄配置圖更新。
- README 全面改寫：架構圖的 console 節點、「使用方式」、selftest 章節、
  部署章節。
- `--netiq-probe` 依 1.4.11 擇一處置。
- 具名 Mutex（`Global\LogForesight`）**保留**在 orchestrator——成本趨近零，
  防未來任何第二行程誤配置（例如兩個 Web 實例被誤設指向同一 DataRoot）。

**回退路徑**：階段二之前的任何時點，重新啟用 schtasks 即回到現行模式
（「console 行為從未被改變」是階段一的鐵律，所以回退零風險）；階段二之後
回退＝git revert 專案移除的 commit＋重新部署 exe，**資料層無任何需要回滾的
遷移**——新表/新 blob 對舊版程式不可見也不礙事（JSON 反序列化容忍未知欄位、
SchemaUpgrader 只加不減）。

### 1.6 影響面清單

| 區塊 | 影響 |
|---|---|
| `LogForesight/Service/*`（11 檔） | 搬 Core；console csproj 變薄 |
| `Program.cs` | 主流程抽出後剩 CLI 分派＋adapter，約砍 6 成 |
| `LogForesight.Core` | 新增 orchestrator、`IRunConsole`、`ScheduleCalculator`、`ScheduleOptions`＋store（blob，`lf_blobs` key=`schedule_options`，零 DDL） |
| `LogForesight.Web` | 新 `BackgroundService`（`SchedulerHostedService`）、`ScheduleController`、Runs 頁排程卡（含 DebugDump 開關與徽章）、主機詳情頁觸發鈕、appsettings 新區段、DI 註冊 |
| `BatchRun` 模型 | 加 `Trigger` 欄位（JSON 反序列化容忍缺欄，零遷移） |
| Web 規則頁 | seed 版本橫幅＋「預覽差異/套用」對話框；`RuleImporter` 拆 Core 純函數 `RuleImportPlanner`（1.4.9） |
| console（階段二） | 專案自解決方案移除、CLI 類別與 `SelfTestRunner` 刪除；`Batch\` 只移除 exe 與 appsettings，資料目錄不動 |
| 測試 | 搬遷後全綠是 Phase 2 閘門；新增 `ScheduleCalculator`（時間窗/跨午夜/漏跑補償）、觸發 API 授權/驗證、取消停在日邊界（stub pipeline）、`RuleImportPlanner` 與 CLI 對等測試 |
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

「有風險」的簽章資格（任一命中）：

1. 命中規則且嚴重度 Medium 以上（含被抑制的——抑制只關通知，紀錄照常的既有
   語意延伸）；
2. 出現在 `TrendAlerts` 的簽章（首次出現/頻率上升/總量突增的構成事件）；
3. 出現在 `CorrelationAlerts` 的構成事件（攻擊鏈/故障鏈的原始證據——這是
   入侵調查時最想看原文的一塊）。

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

### 2.3 容量估算

單筆上限約 4KB（2000 字 nvarchar）；最壞情境 2000 台全數當日 500 筆封頂
＝100 萬列/日×4KB≈4GB/日——但那是「全站同日全部淪陷」的末日場景；典型情境
（少數主機有風險、每台數十筆）≈ 每日數 MB，14 天合計數十 MB。SqlServer 無虞；
SQLite（開發/測試）同樣可承受。若試點觀察到量超預期，先調小每簽章/每主機上限
（常數），再考慮把上限開成設定。

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
| 1 | 需求二全部（表/寫入/清理/設定/對話整合） | 測試全綠＋手動驗證一輪對話注入；未命中暫存時 fallback 行為與現行一致 |
| 2 | 服務搬遷＋orchestrator 抽取（**只搬不改**） | 1163+ 測試全綠；console 輸出與現行逐字一致；`--selftest` 照常通過 |
| 3 | Web 排程引擎＋設定/觸發 API＋UI＋稽核 | `Enabled=false` 發布零行為變化；schtasks 執行完全不受影響 |
| 4 | CLI 職責搬 Web：規則升級橫幅/套用（1.4.9）＋AI 診斷傾印開關（1.4.10） | `RuleImportPlanner` 與 CLI 對等測試綠；console CLI 輸出逐字不變 |
| 5 | 試點雙軌 → schtasks 退場＋文件收尾＋HISTORY 決策 20 修訂 | 連續 ≥5 晚 Runs 紀錄/報告與 schtasks 時代一致 |
| 6 | console 正式退役（專案移除＋README 全面改寫＋probe 處置） | 三項退役閘門全過（見 1.5 階段二）；回退演練通過 |

Phase 1 隨時可先行；Phase 2~6 嚴格依序。每個 Phase 各自是一個可獨立合併、
可獨立回退的發布單位（依既有分支流程：feature → dev 驗證 → master）。

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
- **console 行為凍結**：直到 Phase 6 移除前，console 的輸出、exit code、CLI
  介面一律不變（Phase 2 的「只搬不改」與 Phase 4 的薄包裝都以此為驗收線）；
  這保證任何時點「重啟 schtasks」都是完整的回退路徑。
- **雙軌互斥**：具名 Mutex `Global\LogForesight` 全程保留，Web 排程與 console
  手動/schtasks 執行永不重疊（後到者跳過並記警告，與現行行為一致）。

### 待定案問題（實作前請確認）

1. **時間窗語意**：`WindowEnd` 到點優雅停止、未完成日子等下個窗口自動續跑
   （本規劃的預設）——可接受？或希望同日窗口重新進入時就續跑？
2. **手動觸發不受時間窗限制**——可接受？
3. **需求二入庫資格**採簽章級（規則命中 Medium+／趨勢／關聯），不看日風險等級
   ——可接受？（替代：只存風險「中」以上的日子，範圍更小但低風險日的趨勢異常
   簽章就問不到原文）
4. **Web 服務帳號**是否可加入本機 Event Log Readers 群組（影響本機直讀的
   Security 覆蓋，不影響 NetIQ 主機）？
5. **試點穩定閘門天數**：建議連續 ≥5 晚（Phase 5）——可接受？
6. ~~Console 保留範圍~~ **已定案（2026-07-31）**：終局全退役、兩階段退場，
   過渡期薄殼保留；`--netiq-probe` 的 Web 化與否留到退役閘門時依剩餘需求決定。
