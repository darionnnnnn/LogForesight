# LogForesight 擴充規劃（已確認，待實作）

> 本文件是需求討論的收斂結果。實作按階段進行，每階段有驗證閘門。
> 規劃日期：2026-07-20

## 背景與目標

- 現況：單機版，讀本機 System/Application/Security，四層偵測（規則/趨勢/關聯/AI），地端 KoboldCpp 判讀。
- AI 環境：**Gemma 4 26B、context 20480**——所有呼叫的 prompt＋輸出必須在此預算內（見「AI 呼叫 context 預算」章節）。
- 目標：接上 NetIQ Sentinel **8.5** 取得**上百至數百台**主機的 Event Log 做集中分析；本機維持直讀。
- 未來：紀錄與結果寫入 DB＋查詢介面——Phase 0 先抽持久層介面（見「持久層抽象」章節）。
- 實測 AI 成本：每主機日約 1~20 秒。

## 核心設計決策

### A. 分級分析（規模對策）

- 規則/趨勢/關聯三層＋跨主機關聯層：**全部主機每天跑**（純計算，秒級）。
- AI 每日判讀：**只給被前四層標記的主機**（規則命中 Medium 以上、趨勢異常、關聯訊號）。
- 未標記主機日照寫 history（`AiAnalyzed=false` 統計模式，沿用現有語意）。
- 深入分析：**不設上限**（`MaxDeepDiveHostsPerRun=0` 預設無上限），僅按嚴重度排序（最嚴重先做）。安全閥設定保留給臨時限流情境。
- 機房總覽：每天 1 次 AI 呼叫，吃第五層產出＋各主機一行結論。

### B. 每週體檢（已確認：週末跑，全量 AI 可接受）

- 觸發：`WeeklyCheckupDay`（預設週六）當天全主機各一次；任何執行日發現某主機距上次體檢 >7 天即補做。
- 輸入：該主機近 7 天 history 統計＋上次體檢結論摘要（連續性）。
- 任務：週對週的緩慢趨勢、低度異常累積、營運健康雜項——補「慢速攻擊躲在每日 2 倍門檻下」的盲點。
- 輸出：history 新欄位 `WeeklyCheckup`；**有發現才**輸出 `export\{host}\{date}_週檢.txt`；機房總覽列「體檢有發現的主機」。
- 單機版先實作（Phase 0），多機直接複用。

### C. 抽象層放在「日統計」不是「原始事件」

```
IDailyStatsSource（per-host / per-day 聚合簽章統計）
├─ LocalStatsSource    = 現有 EventLogService + LogAggregator
└─ SentinelStatsSource = Sentinel server 端 GROUP BY 聚合直接組統計
```

原始事件只在兩處需要，用針對性小查詢補：
1. 進 prompt 簽章的範例訊息/KeyDetails（每簽章 limit 3）
2. 風險主機報告的原始 log（沿用 20 筆預算）

### D. history 紀錄策略（已確認）

- per-host 檔案：`history\{host}.txt`；本機沿用現有 `history.txt`。
- **無風險日精簡：數字全留、文字砍掉**——全部簽章的計數/嚴重度/趨勢數字/FirstSeen~LastSeen 完整保留（趨勢基準零損失），SampleMessages 與 KeyDetails 不落地（回查走 Sentinel）。
  - ⚠ 不可只留 top N 簽章——會破壞 14 日平均與「首次出現」判定。
- 保留 90 天不變。

### E. Security 無權限 → 覆蓋率誠實申報（已確認）

- 本機讀取失敗時，console＋報告輸出固定區塊，**逐條列出未執行的偵測**：入侵跡象規則表、涉 Security 的關聯模式（入侵鏈/持久化/滅跡/提權植入/跨日入侵鏈）、4624 破解得手比對。
- history 加 `SecurityLogAvailable`；無權限日的 Security 簽章**排除在趨勢基準外**（避免權限恢復日的假性暴增；恢復後短期的「首次出現」告警屬正常，報告註明）。
- Sentinel 側：主機發現查詢擴充 `GROUP BY 主機, 頻道`，未收 Security 頻道的主機在總覽標注「入侵偵測未覆蓋」→ 天然覆蓋率清單。

## AI 呼叫 context 預算（Gemma 4 26B，ctx 20480）

規則：prompt tokens＋max_tokens ≤ 20480，留 10% 餘裕 → 可用約 18,400。
估算採保守假設（CJK 1 字≈1 token、ASCII ≈3.5 字元/token）。

| 呼叫 | prompt 上限 | 估算 tokens | 輸出上限 | 判定 |
|---|---|---|---|---|
| 每日主分析 | 10KB（既有） | ~3,000~4,500 | 1,536 | ✅ |
| 前置掃描（每批 20 項） | ~2KB | ~800 | 1,536 | ✅ |
| 深入分析（每類別） | **16KB（新增硬上限）** | ~4,000~6,000 | 8,192 | ✅（上限必落實） |
| 週體檢 | **6KB（新定）** | ~2,500 | 1,536 | ✅ |
| 機房總覽 | **8KB（新定）** | ~3,500 | 2,048 | ✅（需輸入塑形） |

落實項：

1. **深入分析 16KB prompt 硬上限**：超出時從「原始 log 區」尾端截斷（問題清單與主分析摘要永不截斷），報告註明已截斷。這是唯一貼近預算的呼叫（8192 輸出保留後 prompt 只剩 ~10K tokens），異常長的事件訊息（如例外全堆疊 × 20 筆）沒有上限就會爆。
2. **週體檢輸入塑形**：程式端先做週彙整——每簽章一行（7 天逐日次數＋趨勢），依嚴重度取前 40 行；加 7 天每日風險等級與一句摘要、上次體檢結論（截 300 字）。不把 7 天 history 原樣串接。
3. **機房總覽輸入塑形**：只有「有訊號的主機」有自己的行（依嚴重度排序、上限 40 行，超出併成類別統計一行）；無訊號主機整體一行；無回報主機名單上限 20＋「等 N 台」；跨主機關聯區塊不設限。
4. **`PromptBudget` 共用防線**（純函數）：每次呼叫前保守估算 tokens，超標記 WARN 並套用該呼叫類型的截斷策略——不依賴 server 端爆 context 的行為。

結論：既有設計全部在小模型可處理範圍內，分級分析路線不變；以上四項為新增的護欄。

## 持久層抽象與 DB 擴充設計（Phase 0 抽介面，DB 為未來新增）

### 介面（新增 `Persistence/` 資料夾）

```csharp
public interface IAnalysisRecordReader
{
    IReadOnlyList<DailyAnalysisRecord> GetRecent(string host, int days);
    bool HasRecord(string host, DateOnly date);
    DateOnly? LastWeeklyCheckupDate(string host);
}

public interface IAnalysisRecordWriter
{
    void Append(string host, DailyAnalysisRecord record);   // append-only、同日冪等由呼叫端 HasRecord 防護
    int Prune(string host, int retentionDays);
}

public interface IReportSink   // 報告先組「結構化內容模型」再交 sink 輸出（內容與呈現分離）
{
    ReportRef WriteDailyRiskReport(RiskReportModel report);
    ReportRef WriteWeeklyCheckupReport(WeeklyCheckupModel report);
    ReportRef WriteFleetSummary(FleetSummaryModel summary);
    ReportRef WritePermissionReport(PermissionReportModel report);
}
// ReportRef = 檔案路徑或 DB id 的抽象；history 的 ReportFile 欄位改存 ReportRef

public interface IPermissionSnapshotStore
{
    PermissionSnapshot? Load();
    void Save(PermissionSnapshot snapshot);
}
```

### 原則與模式對照

| 原則/模式 | 落點 |
|---|---|
| SRP | 三個持久化關注點各自介面；RiskReportService 拆「內容組裝」與「輸出」兩職責 |
| OCP | 新後端＝新實作類別，分析層零修改 |
| LSP | 介面契約寫明 append-only／日期冪等語意 |
| ISP | Reader/Writer 分離；未來查詢 UI 只依賴 Reader |
| DIP | Service 建構子收介面；Program.cs 維持手動 composition root（刻意不引入 DI container） |
| Repository | `JsonlAnalysisRecordStore`（收編現有 LogHistoryService）→ 未來 `SqliteAnalysisRecordStore` |
| Strategy + Factory | `StorageFactory.Create(settings.Storage)`，設定 `"Storage": { "Type": "Jsonl" }` 切換後端 |
| Composite | `CompositeReportSink`：過渡期同時寫檔案＋DB，呼叫端無感 |

### 未來 DB（屆時直接照此做，現在不實作）

首選 **SQLite**（單檔免伺服器，符合「資料夾搬走即部署」哲學；要集中查詢再換 SQL Server，隔著介面只是多一個實作）。schema 草案：

```
hosts(id, name, role)
daily_records(id, host_id, date, risk_level, error_count, warning_count,
              audit_count, ai_analyzed, security_log_available, data_incomplete,
              summary, trend_assessment, report_ref, payload_json)
top_issues(record_id, source, event_id, entry_type, count, severity,
           category, trend, first_seen, last_seen, details_json)
alerts(record_id, kind /*trend|correlation|fleet*/, text)
weekly_checkups(host_id, date, has_findings, conclusion, payload_json)
permission_changes(date, target, change_type, before, after)
reports(id, kind, host_id, date, content)
索引：(host_id, date)、(source, event_id, date)
```

附遷移工具：JSONL → DB 匯入器（同一套模型，舊資料不流失）。

## 偵測面補強（Phase 0）

1. **4625→4624 破解得手關聯**：當日 4625 ≥10 時回撈當日 4624，比對相同帳號/IP 的成功登入 → 新關聯模式【破解得手】，Critical。（4624 平時不收，條件式撈取避免 SuccessAudit 量爆炸。）
2. **資料完整性標記**：倒序掃描記下實際可回溯的最早事件時間；早於它的回補日在 history 標 `DataIncomplete`，趨勢基準排除這些日子，報告註明。
3. 候選（後續）：4672 特權登入、4648 明示認證；4688 與 Operational 頻道（Defender/RDP/PowerShell）走 Sentinel 收錄面處理。

## 驗證機制（Phase 0）

- **測試專案**：規則/趨勢/關聯（含新增模式）合成事件測試；Sentinel 欄位對應測試（probe 真實回應存 fixture）。
- **`--selftest`**：注入合成事件跑完整 pipeline（不寫 history、AI 用 stub），輸出「應命中/實際命中」清單。新主機部署先跑。
- **`--debug-dump`**：單次執行完整輸出 prompt 與 AI 原始回應到 `diag\`（平時關閉，驗證期用）。
- 遠端驗證流程：console 輸出＋`logs\logforesight.log`＋history 對應行＋export 報告＋appsettings 貼回對話分析（敏感資訊先遮罩）。

## Sentinel 8.5 查詢設計

| # | 查詢 | 形式 | 頻率 |
|---|---|---|---|
| Q1 | 全機房日聚合：`SELECT count(*), min(dt), max(dt) WHERE (watchlist Lucene) GROUP BY 主機,來源,EventID OVER 當日` | 1 個聚合查詢（不分主機） | 每日/缺漏日各 1 |
| Q2 | 標記主機簽章範例：單一 (host,source,eventId) 篩選＋欄位投影＋limit 3 | 小查詢 | 每進 prompt 簽章 1 次（估 50~200/日） |
| Q3 | 風險主機原始 log（報告用） | 小查詢 | 每風險主機數次 |
| Q4 | 主機發現＋頻道覆蓋：`GROUP BY 主機,頻道 OVER 近24h` | 1 次 | 每日 1 |

負擔控制：單一併發佇列、排程錯峰、search job 用完即 DELETE、Polly 退避重試、欄位投影。

**GROUP BY 經 REST 不可用時的退回方案**：Q1 改 watchlist 篩選＋只投影 host/source/eventId/dt 四欄＋分頁拉回本地計數。

主機清單：Q4 自動發現＋`HostInclude`/`HostExclude` 樣式過濾，不手工維護清單。昨日有、今日無回報的主機列入總覽告警（agent 或主機掛了）。

### `--netiq-probe` 驗證項（Phase 1 閘門，輸出貼回對話定案）

1. 認證方式（API 帳號實測）
2. 欄位對應：Windows EventID / 來源 / 主機名 / 訊息全文在 Sentinel schema 的哪個欄位
3. GROUP BY 語法能否經 REST 直接用（決定 Q1 走聚合或退回方案）
4. `dt` 時區基準與日切界
5. 各主機頻道覆蓋與詳細度
6. 分頁上限與 search job 生命週期/DELETE

## 設定檔規劃

```json
{
  "Ai": { "（現有不變）": "" },
  "Permissions": { "WatchedFolders": [] },
  "Analysis": {
    "WeeklyCheckupDay": "Saturday",
    "MaxDeepDiveHostsPerRun": 0,
    "ServerDescription": "（自 Program.cs 常數搬入）"
  },
  "NetIq": {
    "Enabled": false,
    "BaseUrl": "https://sentinel:8443",
    "HostInclude": ["*"], "HostExclude": [],
    "HostRoles": { "DC01": "AD 網域控制站", "WEB-*": "對外 IIS" },
    "PageSize": 500, "TimeoutSeconds": 120, "RetryCount": 3
  },
  "Storage": { "Type": "Jsonl" }
}
```

認證欄位待 probe 後定案。`Enabled:false` 保證單機部署不受影響。

## 檔案層級變更

| 檔案 | 變更 |
|---|---|
| `Models/DailySignatureStats.cs`（新） | 聚合簽章統計模型（自 LogAggregator 輸出抽出，兩來源共用） |
| `Persistence/`（新資料夾） | `IAnalysisRecordReader/Writer`、`IReportSink`、`IPermissionSnapshotStore`、`ReportRef`、`JsonlAnalysisRecordStore`、`FileReportSink`、`StorageFactory`（現有檔案格式收編為預設實作，行為零改變） |
| `Models/`（報告內容模型，新） | `RiskReportModel`、`WeeklyCheckupModel`、`FleetSummaryModel`、`PermissionReportModel`（結構化內容與 txt 呈現分離） |
| `Analysis/PromptBudget.cs`（新） | 保守 token 估算＋各呼叫類型截斷策略（純函數） |
| `Service/SentinelClient.cs`（新） | REST 封裝：auth、job 建立/取頁/DELETE、Polly、單一併發 |
| `Service/SentinelStatsSource.cs`（新） | Q1~Q4 組裝、欄位對應、→ DailySignatureStats |
| `Service/LocalStatsSource.cs`（新） | 包裝現有 EventLogService＋LogAggregator |
| `Analysis/FleetCorrelationAnalyzer.cs`（新） | 跨主機：同 IP 打多台 4625、多台同日儲存錯誤、同帳號多台提權、多台同時段非預期重啟、無回報主機 |
| `Analysis/CorrelationAnalyzer.cs` | 新增【破解得手】模式 |
| `Service/EventLogService.cs` | 條件式 4624 撈取、最早可回溯時間、Security 無權限的能力申報 |
| `Service/LogHistoryService.cs` | per-host 檔案、無風險日精簡序列化、`SecurityLogAvailable`/`DataIncomplete` 欄位 |
| `Analysis/TrendAnalyzer.cs` | 基準排除 DataIncomplete 日與無 Security 權限日的 Security 簽章 |
| `Service/LogAnalysisService.cs` | 吃 DailySignatureStats；AI 條件觸發；週體檢呼叫 |
| `Service/RiskReportService.cs` | 主機層目錄、機房總覽報告、週檢報告、未檢查項目區塊 |
| `Configuration/AppSettings.cs` | Analysis/NetIq 區段 |
| `Program.cs` | 本機 pipeline（原樣）→ 機房 pipeline；ServerDescription 移設定檔；`--selftest`/`--debug-dump`/`--netiq-probe` |
| 測試專案（新） | 上述測試 |

## 階段與驗證閘門

| 階段 | 內容 | 閘門 |
|---|---|---|
| 0 | **持久層介面抽取（行為零改變的重構，最先做）** / selftest / debug-dump / 測試專案 / DataIncomplete / 4624 關聯 / Security 未檢查申報＋基準排除 / 無風險日精簡 / **每週體檢（單機版，含 6KB 輸入塑形）** / **深入分析 16KB prompt 硬上限＋PromptBudget** / ServerDescription 進設定檔 | 兩台機器 selftest＋真實執行輸出貼回分析 |
| 1 | SentinelClient + `--netiq-probe` | probe 輸出貼回，定案欄位對應與 Q1 形式 |
| 2 | SentinelStatsSource，2~3 台試點端到端 | 試點輸出貼回比對 |
| 3 | 全量：自動發現、分級 AI、週末全量體檢、第五層、機房總覽、覆蓋率清單 | 首次全量耗時分布＋總覽貼回調參 |
| 4 | 通知管道（Email / Teams webhook 擇一） | 實際收到通知 |
| 5（未來，時機由使用者決定） | SQLite 後端實作＋JSONL 匯入器；之後查詢 UI（只依賴 Reader 介面） | UI 查得到歷史資料 |

回補策略：NetIQ 主機首次接入只回補統計基準（不做 AI，幾分鐘完成）；AI 自次日起服務被標記主機；首個週末做第一輪全量體檢。

## Phase 0 實作狀態（2026-07-20 完成並通過審查）

10 項全數完成，建置零警告、106 單元測試 + 64 項 selftest 全過。審查發現並修正 2 處、3 項刻意延後：

**修正**
- 週體檢 AI 失敗曾會消耗整週額度（違背補跑意圖）：改為 `WeeklyCheckupResult.Completed=false` 時不寫入歷史，留待下次補跑。
- `PromptBudget` 原本只接在週體檢，未達計畫「共用防線」定位：改放 `AIService.ChatAsync`（所有呼叫的單一咽喉點），每次呼叫送出前估算超標即記 WARN。

**刻意延後（對應後續階段，非缺漏）**
- 報告結構化模型（`RiskReportModel` 等）：未建。`IReportSink` 收「已渲染文字」，與 DB schema 的 `reports(content)` text 欄位一致；可查詢的結構化資料走 `IAnalysisRecordStore`（對應 `daily_records`/`top_issues`/`alerts`）。兩條路分工，非缺漏。
- `CompositeReportSink`：Phase 5（DB 與檔案並存的過渡期）才需要。
- `MaxDeepDiveHostsPerRun`：已定義於設定檔但無行為，是 Phase 3 機房限流安全閥，單機無可限流。

## 時間預算估算（300 台）

- 平日：確定性計算秒級＋Q1/Q4 查詢＋標記主機 AI（5~15% × 1~20s ≈ 1~15 分）＋深入分析（無上限，嚴重度排序）＋總覽 1 次。
- 週六：上述＋全量體檢 300 × 1~20s ≈ 5~100 分，整段估 1~3 小時（已確認可接受）。

## 已確認的需求決策紀錄

1. 週末全量體檢：要做，長時間佔用 AI 可接受（2026-07-20）
2. 深入分析不設台數上限，僅嚴重度排序（2026-07-20）
3. 無風險日精簡紀錄：數字全留、文字砍掉，基準完整（2026-07-20）
4. Security 無權限：條列未檢查項目即可，不視為錯誤（2026-07-20）
5. 本機維持直讀，不走 Sentinel（2026-07-20）
6. Sentinel 8.5、數百台規模、API 帳號申請中；測試輸出貼回對話分析（2026-07-20）
7. AI 環境定案：Gemma 4 26B、context 20480；全部呼叫經預算驗算通過，新增深入分析 16KB 上限、週體檢/總覽輸入塑形、PromptBudget 護欄（2026-07-20）
8. 未來寫入 DB＋查詢介面：Phase 0 先抽持久層介面（Repository/Strategy/Composite，讀寫分離），現有檔案格式為預設實作；DB 首選 SQLite、schema 草案已列，屆時零架構異動（2026-07-20）
