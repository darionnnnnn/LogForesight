# LogForesight 歷程紀錄

> 本文件彙整 2026-07-20 至 2026-07-28 期間已完工的規劃案，依時序排列，內容逐字保留原文
> （含各文件自己的修訂/廢止/修正註記）。這是文件收斂（2026-07-28）的產物：15 份文件收斂為
> 現況文件＋本檔＋docs/BACKLOG.md，過往決策的來龍去脈與「為什麼」保留在此，
> 不再散落於多份各自獨立演進的規劃檔。
>
> 現況文件（README.md、docs/WEB-SPEC.md、docs/DB-PLAN.md、docs/RULES-PLAN.md、
> docs/LINUX-RULES-PLAN.md、docs/NETIQ-API-PLAN.md）只描述**目前的程式行為**；
> 本檔的內容一律是「當時如何決策、如何實作」的歷史記錄，其中提到的「本文件」「見上」等字樣
> 指原始文件自身，閱讀時請留意上下文。
>
> **內文引用的舊檔名對照**：各段原文中出現的 `docs/PLAN.md`、`docs/AI-ROLE-PLAN.md`、
> `docs/HISTORY-STORE-FIX-PLAN.md`、`docs/NETIQ-HOSTLIST-WEB-PLAN.md`、`docs/SCALE-2000-PLAN.md`、
> `docs/NETIQ-WEB-CONFIG-PLAN.md`、`docs/WEB-FEEDBACK-PLAN.md`、`docs/SHARED-STANDARDS-PLAN.md`、
> `docs/OPS-HARDENING-PLAN.md`、`docs/WEB-FEEDBACK-2-PLAN.md` **這 10 個檔案都已併入本檔**
> （逐字保留原文，故引用字樣未改寫）。要找它們的內容，依下方各段的日期標題對照即可——
> 每段標題都標明了「（原 docs/XXX.md）」。

---

## 2026-07-20 — LogForesight 擴充規劃（原 docs/PLAN.md）

> 本文件是需求討論的收斂結果。實作按階段進行，每階段有驗證閘門。
> 規劃日期：2026-07-20
>
> **現況（2026-07-27）**：NetIQ Web 整併、SQL 儲存後端等後續階段已分別在
> docs/NETIQ-WEB-CONFIG-PLAN.md、docs/DB-PLAN.md、docs/NETIQ-API-PLAN.md 定案並完成——
> 本文件保留作為原始需求脈絡，密碼保護章節已被 docs/OPS-HARDENING-PLAN.md P0-5 取代
> （見下方 DPAPI 段落的更新註記）。

### 背景與目標

- 現況：單機版，讀本機 System/Application/Security，四層偵測（規則/趨勢/關聯/AI），地端 KoboldCpp 判讀。
- AI 環境：**Gemma 4 26B、context 20480**——所有呼叫的 prompt＋輸出必須在此預算內（見「AI 呼叫 context 預算」章節）。
- 目標：接上 NetIQ Sentinel **8.5** 取得約 **2000 台** Windows 主機的 Event Log 做集中分析
  （2026-07-20 由「數百台」上修）；主機分散於**多台 Sentinel**（皆 8.5、共用同一組查詢帳密），本機維持直讀。
- 系統定位：**第二層縱深防禦**——多數緊急狀況由既有第一層監控承擔，本系統負責提早發現趨勢與
  第一層漏掉的訊號，故通知即時性要求不高（通知維持 Phase 4 不前移，2026-07-20 確認）。
- 未來：紀錄與結果寫入 DB＋查詢介面——Phase 0 先抽持久層介面（見「持久層抽象」章節）。
- 實測 AI 成本：每主機日約 1~20 秒。

### 核心設計決策

#### A. 分級分析（規模對策）

- 規則/趨勢/關聯三層＋跨主機關聯層：**全部主機每天跑**（純計算，秒級）。
- AI 每日判讀：**只給被前四層標記的主機**（規則命中 Medium 以上、趨勢異常、關聯訊號）。
- 未標記主機日照寫 history（`AiAnalyzed=false` 統計模式，沿用現有語意）。
- 深入分析：**不設上限**，僅按嚴重度排序（最嚴重先做）。（原保留的 `MaxDeepDiveHostsPerRun` 安全閥設定已於 2026-07-20 依過度設計體檢移除——有設定無行為會誤導使用者；Phase 3 若真需要限流，屆時連同行為一起實作。）
- 機房總覽：每天 1 次 AI 呼叫，吃第五層產出＋各主機一行結論。

#### B. 體檢（2026-07-20 重設計：每日確定性偵測＋7 天 due-date 輪巡敘事，取代原「週六全量」）

原「週六全量 AI 體檢」在 2000 台下破產（2000 × 1~20s ≈ 33 分~11 小時集中單日）。
重設計把體檢的兩件事拆開——「發現慢速斜線」是偵測、「講這段期間的故事」是敘事：

- **慢速趨勢偵測（每日、全主機、確定性）**：每日分析時 per 簽章比對「近 7 天總量 vs 前 7 天總量」
  （最低次數門檻＋1.5 倍，細部門檻實作時定），命中即產生趨勢告警並**計入當日風險下限「中」**
  （與頻率異常同級，已確認 2026-07-20）。「慢速惡化躲在每日 2 倍門檻下」的盲點由 AI 改為程式承擔：
  可單元測試、進 --selftest，偵測延遲從最壞 7 天縮到 1 天——品質升級而非妥協。
- **體檢敘事（AI）改 due-date 輪巡**：不設固定星期、不做 cohort 分桶——每晚執行時
  「距上次體檢 ≥ `Analysis.CheckupIntervalDays`（預設 7）」的主機即到期。首次接觸主機時把
  上次體檢日虛擬回填為 `今天 − hash(IP) mod 間隔`，2000 台自動均勻錯峰（每日到期約 1/7 ≈ 286 台）。
  停機自癒（到期主機下次執行自動一起處理）、主機增減零再平衡——是既有「>7 天補跑」機制的
  一般化，不新增任何排程簿記。
- **閘門**：到期主機中，窗口內有慢速趨勢告警/風險日/錯誤總量上升者才呼叫 AI 敘事；
  其餘寫確定性模板結論（「本期無累積性異常，程式比對通過」）並更新體檢日期。
  估每日 AI 體檢 ≈ 286 × 10~25% ≈ 29~71 次（0.5~24 分）；閘門通過率 50% 的災情週也僅 ~48 分。
- 輸入塑形沿用原設計（每簽章一行、7 天逐日次數、40 行上限）；AI 失敗不寫入、下次到期重試的語意不變。
- 設定：`WeeklyCheckupDay` 廢除 → `Analysis.CheckupIntervalDays`（預設 7；要改雙週只動設定）。
- 單機版體檢已於 Phase 0 實作，本重設計於多機階段套用；單機情境等同「一台主機每 7 天到期」，行為相容。
- 輸出不變：history `WeeklyCheckup` 欄位；**有發現才**輸出 `export\{host}\{date}_週檢.txt`；機房總覽列「體檢有發現的主機」。

#### C. 抽象層放在「日統計」不是「原始事件」

```
IDailyStatsSource（per-host / per-day 聚合簽章統計）
├─ LocalStatsSource    = 現有 EventLogService + LogAggregator
└─ SentinelStatsSource = Sentinel server 端 GROUP BY 聚合直接組統計
```

原始事件只在兩處需要，用針對性小查詢補：
1. 進 prompt 簽章的範例訊息/KeyDetails（每簽章 limit 3）
2. 風險主機報告的原始 log（沿用 20 筆預算）

#### D. history 紀錄策略（已確認）

- per-host 檔案：`history\{host}.txt`；本機沿用現有 `history.txt`。
- **無風險日精簡：數字全留、文字砍掉**——全部簽章的計數/嚴重度/趨勢數字/FirstSeen~LastSeen 完整保留（趨勢基準零損失），SampleMessages 與 KeyDetails 不落地（回查走 Sentinel）。
  - ⚠ 不可只留 top N 簽章——會破壞 14 日平均與「首次出現」判定。
- 保留 120 天（2026-07-24 由 90 天調整，配合首次執行回補 120 天，回補的歷史不會下次啟動即被清除）。

#### E. Security 無權限 → 覆蓋率誠實申報（已確認）

- 本機讀取失敗時，console＋報告輸出固定區塊，**逐條列出未執行的偵測**：入侵跡象規則表、涉 Security 的關聯模式（入侵鏈/持久化/滅跡/提權植入/跨日入侵鏈）、4624 破解得手比對。
- history 加 `SecurityLogAvailable`；無權限日的 Security 簽章**排除在趨勢基準外**（避免權限恢復日的假性暴增；恢復後短期的「首次出現」告警屬正常，報告註明）。
- Sentinel 側：主機發現查詢擴充 `GROUP BY 主機, 頻道`，未收 Security 頻道的主機在總覽標注「入侵偵測未覆蓋」→ 天然覆蓋率清單。

### AI 呼叫 context 預算（Gemma 4 26B，ctx 20480）

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

### 持久層抽象與 DB 擴充設計（Phase 0 抽介面，DB 為未來新增）

#### 介面（新增 `Persistence/` 資料夾）

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

#### 原則與模式對照

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

#### 未來 DB（屆時直接照此做，現在不實作）

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

### 偵測面補強（Phase 0）

1. **4625→4624 破解得手關聯**：當日 4625 ≥10 時回撈當日 4624，比對相同帳號/IP 的成功登入 → 新關聯模式【破解得手】，Critical。（4624 平時不收，條件式撈取避免 SuccessAudit 量爆炸。）
2. **資料完整性標記**：倒序掃描記下實際可回溯的最早事件時間；早於它的回補日在 history 標 `DataIncomplete`，趨勢基準排除這些日子，報告註明。
3. 候選（後續）：4672 特權登入、4648 明示認證；4688 走 Sentinel 收錄面處理。
   - **更新（2026-07，已完成）**：Defender / RDP 的 Operational 頻道原規劃走 Sentinel，改為
     **在本機直接以 `EventLogReader` 讀取**（見 README「EventLogReader 遷移＋Operational 頻道擴充」）。
     已納入 Defender（惡意程式偵測/防護遭關閉，seed v2 規則）與 RDP TerminalServices（Low 收集規則），
     並新增【暴力破解→RDP 得手】【防護遭關閉→惡意程式】【惡意程式→持久化】關聯。PowerShell 頻道仍待評估。

### 驗證機制（Phase 0）

- **測試專案**：規則/趨勢/關聯（含新增模式）合成事件測試；Sentinel 欄位對應測試（probe 真實回應存 fixture）。
- **`--selftest`**：注入合成事件跑完整 pipeline（不寫 history、AI 用 stub），輸出「應命中/實際命中」清單。新主機部署先跑。
- **`--debug-dump`**：單次執行完整輸出 prompt 與 AI 原始回應到 `diag\`（平時關閉，驗證期用）。
- 遠端驗證流程：console 輸出＋`logs\logforesight.log`＋history 對應行＋export 報告＋appsettings 貼回對話分析（敏感資訊先遮罩）。

### Sentinel 8.5 查詢設計

| # | 查詢 | 形式 | 頻率 |
|---|---|---|---|
| Q1 | 全機房日聚合：`SELECT count(*), min(dt), max(dt) WHERE (清單 IP 篩選 AND watchlist Lucene) GROUP BY 主機,來源,EventID OVER 當日` | 聚合查詢，**IP 清單過長時分批**（如每批 50 個 IP 一次查詢，避免 Lucene 篩選字串超長） | 每日/缺漏日各 1 輪 |
| Q2 | 標記主機簽章範例：單一 (host,source,eventId) 篩選＋欄位投影＋limit 3 | 小查詢 | 每進 prompt 簽章 1 次（估 50~200/日） |
| Q3 | 風險主機原始 log（報告用） | 小查詢 | 每風險主機數次 |
| Q4 | 清單主機的頻道覆蓋檢查：對清單 IP `GROUP BY 主機,頻道 OVER 近24h` | 1 輪 | 每日 1 |

負擔控制：**per-server 各一條單一併發佇列，跨 server 平行**（不同 Sentinel 為獨立系統，
平行不增加任何單台負擔、總收集時間 ≈ 最大單台耗時）、查詢最小間隔 `QueryDelayMs`、
01:00 夜間執行窗、search job 用完即 DELETE、Polly 退避重試、欄位投影、
**Q4 頻道覆蓋檢查降為每週**（覆蓋狀態變化慢）。

**Q2 取樣策略（2026-07-20 定案：預設不縮減）**：多台 Sentinel 分攤後單台負載回到原 300 台
評估可接受的範圍，且範例訊息對偵測層零作用（規則/趨勢/關聯只看簽章次數），縮減損失的是
敘事具體性（哪顆硬碟/哪個服務）與 DistinctMessageCount 判讀輔助——為保檢查品質預設全查。
保險開關 `NetIq.SampleFetchMode: Full | Reduced`（Reduced＝僅 Security 與 Other 類簽章查範例，
與 AI 白話翻譯角色一致），哪台 Sentinel 反映負載即可單獨降級、不用改版。

**GROUP BY 經 REST 不可用時的退回方案**：Q1 改 watchlist 篩選＋只投影 host/source/eventId/dt 四欄＋分頁拉回本地計數。

**失敗隔離**：單一批次/單一 IP 的查詢失敗只影響該批主機（該日標記「查詢失敗、資料不完整」，
比照 DataIncomplete 的基準排除邏輯），其他主機照常分析；Sentinel 整體連不上則機房 pipeline
當次跳過並明確告警，本機分析不受影響，缺的日子由既有 per-host 缺漏回補機制下次補上。

#### 多台 Sentinel（2026-07-20 新增：2000 台分散於多台、皆 8.5、共用帳密）

- **設定**：`NetIq.Servers: [{ Name, BaseUrl }, ...]`；`Account`/`Password` 各台共用一組。
- **路由**：per-server 清單檔 `hosts\{Name}.txt`（見下節）。**IP 全域唯一（已確認）仍為主機識別鍵**，
  server 僅路由/顯示屬性——history 檔名、報告目錄、DB 主機鍵皆不含 server；主機搬遷 Sentinel
  只改清單檔，歷史無縫延續。同一 IP 出現在兩個 server 檔 → 設定錯誤警告、取第一個。
- **失敗隔離升級**：單台 Sentinel 整台失聯 → 僅其轄下主機標記「當日查詢失敗、資料不完整」
  （沿用 DataIncomplete 基準排除），其他 server 照常；機房總覽新增「**來源狀態**」區塊，
  明確列出本日失聯的 Sentinel——「沒查 ≠ 沒事」原則的 server 層版本。
- **probe 每台各跑一次**：皆 8.5、欄位對應預期一份通用；per-server 覆寫機制保留為保險
  （實測代替假設）。
- **跨 server 關聯是集中分析的獨有價值**：跨主機關聯層在集中端計算，攻擊橫跨兩個機房時
  單一 Sentinel 各自看不到全貌，只有本系統看得到。

#### 主機清單：txt 檔匯入（2026-07-20 定案，取代原「自動發現」設計）

要處理的主機以 **IP 清單**為準，來源是**指定目錄下的 txt 檔**；未來 Web 介面上線後改由
Web 維護（寫入 `lf_hosts`），txt 停用——**同一時間只有一個主人**，不做雙向同步。

- **檔案位置**：`NetIq.HostListDirectory` 指定目錄；**檔名即 Sentinel 歸屬**——
  `{Servers[].Name}.txt` 一台 Sentinel 一檔（2026-07-20 多 Sentinel 定案），
  不對應任何 server Name 的檔案警告並略過
- **格式**：一行一台，`IP[,角色描述]`；`#` 開頭為註解、空行忽略；UTF-8（容忍 BOM）
- **驗證**：格式不合法的行**警告並略過**（不中斷）；重複 IP 去重並警告；
  目錄不存在或清單為空 → 機房 pipeline 跳過並明確提示（不視為錯誤）
- **清單變更語意**：新增 IP → 視為新主機，統計基準回補（不做 AI）後納入日常分析；
  移除 IP → 停止分析，既有 history 保留（DB 階段標 `active=false`）
- **主機識別**：以 IP 為 NetIQ 主機的識別鍵（per-host history 檔名、報告目錄都用 IP）；
  主機名稱從 Sentinel 事件欄位取得後作為顯示屬性記錄。前提假設：**伺服器為固定 IP**
  （DHCP 環境此設計不成立，目前環境為伺服器機房、假設成立）
- **無資料告警**：清單上的 IP 當日在 Sentinel 查無任何事件 → 列入機房總覽的
  「無資料主機」區塊（agent 停了、IP 寫錯、或未納入收錄——都是要人處理的事，
  不能靜默當成「今天很平靜」）
- **多網卡風險**：主機若以其他網卡的 IP 回報事件，清單 IP 會查無資料——列入 probe
  驗證項（見 #7），實測確認 Sentinel 記錄的是哪個 IP
- **DB 階段銜接**：`--import-hosts` 把 txt 匯入 `lf_hosts`（source='netiq'）；
  Web 維護上線前 txt 仍為主、每次執行重新讀取比對，上線後設定切換停用 txt 匯入

#### `--netiq-probe` 驗證項（Phase 1 閘門，輸出貼回對話定案）

1. 認證方式（API 帳號實測）
2. 欄位對應：Windows EventID / 來源 / 主機名 / 訊息全文在 Sentinel schema 的哪個欄位
3. GROUP BY 語法能否經 REST 直接用（決定 Q1 走聚合或退回方案）
4. `dt` 時區基準與日切界
5. 各主機頻道覆蓋與詳細度
6. 分頁上限與 search job 生命週期/DELETE
7. **主機 IP 欄位是否存在、記錄的是哪個 IP**（txt 清單以 IP 篩選的前提；多網卡主機是否以清單外的 IP 回報——會造成「查無資料」假象）；Security 頻道實際收錄範圍（DB-PLAN 決策點 #4 第二步的依據）
8. **以 IP 清單做 Lucene 篩選的實測**：單一查詢可容納幾個 IP 條件（決定 Q1 的分批大小）；IP 欄位可否用於 GROUP BY／篩選
9. **認證方式細節**：Basic auth 或 token 交換；session 逾時與重新認證行為（帳密欄位設計不受影響，只影響 SentinelClient 內部）

### 設定檔規劃

```json
{
  "Ai": { "（現有不變）": "" },
  "Permissions": { "WatchedFolders": [] },
  "Analysis": {
    "CheckupIntervalDays": 7,
    "ServerDescription": "（自 Program.cs 常數搬入）"
  },
  "NetIq": {
    "Enabled": false,
    "Servers": [ { "Name": "sentinel-a", "BaseUrl": "https://sentinel-a:8443" } ],
    "Account": "唯讀查詢帳號（各台 Sentinel 共用）",
    "Password": "明文，或 enc: 開頭的 DPAPI 加密值（見下）",
    "HostListDirectory": "hosts",
    "SampleFetchMode": "Full",
    "QueryDelayMs": 0,
    "PageSize": 500, "TimeoutSeconds": 120, "RetryCount": 3
  },
  "Storage": { "Type": "Jsonl" }
}
```

（`HostInclude`/`HostExclude`/`HostRoles` 已隨「txt 主機清單」定案移除——包含/排除語意由
txt 清單本身承擔，角色描述改為 txt 的第二欄；`MaxDeepDiveHostsPerRun` 已於 2026-07-20 移除。）

**認證與密碼保護**：

- `Account`/`Password` 對應 Sentinel 的**唯讀查詢帳號**（最小權限，已列入申請）；
  帳密如何送出（Basic auth 或先換 token）依 probe #1 實測結果實作，設定欄位不變
- **（已改採，取代本段原 DPAPI 提案）** `Sentinel.PasswordEnc` 實際採
  `CryptoHelper`（AES-256-CBC，`enc:v1:` 前綴），而非本段原規劃的 DPAPI：
  DPAPI machine 綁定會讓批次與 Web 兩個行程（甚至異機部署）互相解不開彼此的密文，
  與「批次寫、Web 讀」的既有資料流不相容。金鑰目前內嵌於程式（本質是混淆，見
  `CryptoHelper` 類別註解的防護邊界聲明）；docs/OPS-HARDENING-PLAN.md §6（P0-5，
  已定案改為環境變數 `LF_CRYPTO_KEY`，見本檔「2026-07-27 — 營運強化與主機停用隱藏規劃」段）
  已定案改為環境變數 `LF_CRYPTO_KEY`（未設定時 fallback 內嵌金鑰＋WARN，
  解密端雙金鑰嘗試以支援金鑰輪替過渡期），批次與 Web 共用同一把機器層級金鑰
- **密碼永不寫入任何 log**（診斷 log 記設定摘要時遮蔽此欄位）
- **版控紅線**：repo 裡的 appsettings.json 永遠只放空白佔位，真實帳密只存在部署目錄的副本

`Enabled:false` 保證單機部署不受影響。

### 檔案層級變更

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

### 階段與驗證閘門

| 階段 | 內容 | 閘門 |
|---|---|---|
| 0 | **持久層介面抽取（行為零改變的重構，最先做）** / selftest / debug-dump / 測試專案 / DataIncomplete / 4624 關聯 / Security 未檢查申報＋基準排除 / 無風險日精簡 / **每週體檢（單機版，含 6KB 輸入塑形）** / **深入分析 16KB prompt 硬上限＋PromptBudget** / ServerDescription 進設定檔 | 兩台機器 selftest＋真實執行輸出貼回分析 |
| 1 | SentinelClient + `--netiq-probe` | probe 輸出貼回，定案欄位對應與 Q1 形式 ｜**程式碼已完成 2026-07-24**（細部設計與原廠 API 事實見 docs/NETIQ-API-PLAN.md），閘門本身（真實環境 probe 輸出貼回）待執行 |
| 2 | SentinelStatsSource，2~3 台試點端到端 | 試點輸出貼回比對 |
| 3 | 全量：txt 主機清單（多 Sentinel 路由）、分級 AI、每日慢速趨勢偵測、體檢 due-date 輪巡＋閘門、第五層、機房總覽（含來源狀態）、覆蓋率清單 | 首次全量耗時分布＋總覽貼回調參 |
| 4 | 通知管道（Email / Teams webhook 擇一） | 實際收到通知 |
| 5（DB 就緒後啟動，欄位級設計已定案於 **docs/DB-PLAN.md**） | DB 後端（SQL Server 或 Oracle，EF Core provider 切換）＋JSONL/報告匯入器＋Web 查詢（依負責主機授權）＋AI 問答 | 建表、匯入舊資料、Web 查得到自己主機並可問答 |

回補策略：NetIQ 主機首次接入只回補統計基準（不做 AI，幾分鐘完成）；AI 自次日起服務被標記主機；首個週末做第一輪全量體檢。

### Phase 0 實作狀態（2026-07-20 完成並通過審查）

10 項全數完成，建置零警告、106 單元測試 + 64 項 selftest 全過。審查發現並修正 2 處、3 項刻意延後：

**修正**
- 週體檢 AI 失敗曾會消耗整週額度（違背補跑意圖）：改為 `WeeklyCheckupResult.Completed=false` 時不寫入歷史，留待下次補跑。
- `PromptBudget` 原本只接在週體檢，未達計畫「共用防線」定位：改放 `AIService.ChatAsync`（所有呼叫的單一咽喉點），每次呼叫送出前估算超標即記 WARN。

**刻意延後（對應後續階段，非缺漏）**
- 報告結構化模型（`RiskReportModel` 等）：未建。`IReportSink` 收「已渲染文字」，與 DB schema 的 `reports(content)` text 欄位一致；可查詢的結構化資料走 `IAnalysisRecordStore`（對應 `daily_records`/`top_issues`/`alerts`）。兩條路分工，非缺漏。
- `CompositeReportSink`：Phase 5（DB 與檔案並存的過渡期）才需要。
- ~~`MaxDeepDiveHostsPerRun`~~：過度設計體檢的唯一標記項（有設定無行為），**已於 2026-07-20 自程式碼與設定檔移除**；Phase 3 若需限流連同行為一起實作。
- ⚠ **深析「只存報告全文」的延後決策已被推翻**（2026-07-20，Web AI 問答需求）：深析結果需
  結構化落地（餵問答 context、跨主機查詢）。詳見 docs/DB-PLAN.md——其中「現在就能做的準備」
  第 1 項（`DailyAnalysisRecord.DeepDives` 欄位）有資料保全的時間壓力，應排入下一次實作。

### 時間預算估算（2000 台，2026-07-20 重算；01:00 起跑）

- 確定性四層＋每日慢速趨勢偵測：全主機，分鐘級。
- 收集：per-server 佇列平行，單台 Sentinel 只承擔轄下主機查詢量；總收集時間 ≈ 最大單台耗時。
- AI（單一佇列，嚴重度排序）：
  標記主機白話日報 100~300 × 1~20s ≈ 2 分~1.7 小時（翻譯層輸出短，實務偏下緣）
  ＋到期體檢敘事 29~71 次 ≈ 0.5~24 分
  ＋深入分析僅 Other 類（規則命中走靜態知識庫，見 docs/AI-ROLE-PLAN.md）≈ 趨近零
  ＋機房總覽 1 次。
- 合計典型 ~30 分、最壞 ~2.5 小時，01:00 起跑上班前收尾；原「週六全量體檢」單日尖峰
  已由 due-date 輪巡消除，週末不再特殊。
- 執行模型維持 **one-shot＋工作排程器**（01:00 起跑）：冪等設計（已分析日跳過、缺漏回補、
  體檢到期制）全部圍繞 one-shot 建立，不改常駐服務；程式冪等允許一日多次觸發
  （第二次僅做權限異動檢查＋缺漏補跑），日後要日內權限監控加排程觸發器即可、程式零修改。

### 已確認的需求決策紀錄

1. 週末全量體檢：要做，長時間佔用 AI 可接受（2026-07-20）
2. 深入分析不設台數上限，僅嚴重度排序（2026-07-20）
3. 無風險日精簡紀錄：數字全留、文字砍掉，基準完整（2026-07-20）
4. Security 無權限：條列未檢查項目即可，不視為錯誤（2026-07-20）
5. 本機維持直讀，不走 Sentinel（2026-07-20）
6. Sentinel 8.5、數百台規模、API 帳號申請中；測試輸出貼回對話分析（2026-07-20）
7. AI 環境定案：Gemma 4 26B、context 20480；全部呼叫經預算驗算通過，新增深入分析 16KB 上限、週體檢/總覽輸入塑形、PromptBudget 護欄（2026-07-20）
8. 未來寫入 DB＋查詢介面：Phase 0 先抽持久層介面（Repository/Strategy/Composite，讀寫分離），現有檔案格式為預設實作；DB 首選 SQLite、schema 草案已列，屆時零架構異動（2026-07-20）
9. Web 需求定案：使用者於 Web 查詢**自己負責的主機**狀態＋依已取得資訊**問 AI** 風險細節與處理方式；DB 為 SQL Server 或 Oracle（未定）→ 欄位級 schema 以雙 DB 可移植規則定案於 docs/DB-PLAN.md，取代原 SQLite 草案；ORM 建議 EF Core（provider 切換）；深析結構化落地由延後改為 pre-work（2026-07-20）
10. Web 需求第二輪修訂：AI 問答**降為未來選項**（視資源）；風險報告全文直接於畫面顯示；DB **長期保存**；主篩選＝主機/日期區間/風險層級/風險類型；主管儀表板看類型/數量/緊急程度 → 新增 `record_categories` 彙總表、保留策略改長期、**檔案保留 90→365 天列入 pre-work（時間壓力）**、提案 `record_handling` 處理狀態追蹤待確認（2026-07-20）
11. 第三輪定案：檔案保留**維持 90 天**（txt=臨時資料庫，DB 上線僅匯入近 90 天已接受，365 天提案否決）；處理狀態追蹤**納入**（＋預計完成日＋處理說明＋處理人員可指派/自動帶入＋`record_handling_log` 歷程）；主機識別**存 IP＋hw_uuid**、三層證據綁定機制（人工確認合併，不自動）；Security 長期保存分兩步（先 probe 確認抓得到什麼）；自由文字搜尋**不做**；Web 細節後議（2026-07-20）
12. 第四輪簡化：主機綁定的 hw_uuid 與程式建議機制**移除**（VM 環境下 UUID 重建即變、非可靠證據，收集/比對機制屬過度設計）→ 定案**純人工綁定**：Web 輸入/選取舊主機 ID 即合併，`hosts.merged_into` 留墓碑；IP 保留為顯示用線索、不做程式比對。同輪完成全案過度設計體檢，唯一標記項為 `MaxDeepDiveHostsPerRun`（有設定無行為），處置待使用者決定（2026-07-20）
13. 第五輪定案：**資料表一律 `lf_` 前綴**（索引 `ix_lf_`，含前綴仍全數 ≤30 字元）；**txt ↔ DB 一致性保證機制化**（單一模型契約、介面語意即規格、合約測試、精簡策略單點化 `RecordStorageShaper`、同一序列化設定、匯入後抽樣核對、雙寫過渡期）——pre-work 增為三項：DeepDives 入 JSONL、Host 欄位、RecordStorageShaper 抽取（2026-07-20）
14. 第六輪定案：`MaxDeepDiveHostsPerRun` **已自程式碼移除**（建置與 106 測試通過）；NetIQ 認證走 appsettings（Account＋Password，支援 `enc:` DPAPI 加密、密碼不落 log、repo 只放佔位）；**主機清單改為 txt 檔匯入**（`HostListDirectory` 目錄下 *.txt 合併、一行一台 `IP[,角色]`、以 IP 為 NetIQ 主機識別鍵、固定 IP 假設、無資料 IP 列入總覽告警、Web 維護上線後 txt 停用），取代原自動發現＋HostInclude/Exclude/HostRoles 設計；probe 增列 IP 欄位語意/IP 篩選批次上限/認證細節（2026-07-20）
15. **三項 pre-work 全數完成並驗證**（2026-07-20）：`DailyAnalysisRecord` 加 `Host`（`LogAnalysisService` 新建構參數，預設 `Environment.MachineName`）與 `DeepDives`（`CategoryDeepDive`/`DeepDiveFinding`，`RiskReportService.GenerateAsync` 深析成功後同步寫入）；精簡策略抽成 `Persistence/RecordStorageShaper.cs` 純函數，`JsonlAnalysisRecordStore` 改呼叫它。建置零警告、116 測試（新增 5 個）與 64 項 selftest 全過。已知覆蓋缺口：`RiskReportService` 內「深析寫入 DeepDives」的接線本身無自動化測試（`AIService` 未抽介面，缺 mock 基礎設施），詳見 docs/DB-PLAN.md「現在就能做的準備」
16. **規模上修：約 2000 台、多台 Sentinel**（皆 8.5、共用查詢帳密）；IP 全域唯一（已與網路端確認）
    維持識別鍵、server 為路由屬性（per-server 清單檔 `{Name}.txt`）；per-server 平行佇列；
    失敗隔離與覆蓋申報升級到 server 層；probe 每台各跑（2026-07-20）
17. **體檢重設計**：每日確定性慢速趨勢偵測（近 7 天 vs 前 7 天，命中計入風險下限「中」）＋
    AI 敘事 due-date 輪巡（`CheckupIntervalDays`=7、hash 錯峰、閘門、模板結論），取代週六全量；
    `WeeklyCheckupDay` 廢除；重要主機例外分級機制不需要（全部 7 天已足夠密）（2026-07-20）
18. **Q2 預設不縮減**（多 Sentinel 分攤＋範例對偵測層零作用）＋`SampleFetchMode` 保險開關；
    `QueryDelayMs` 節流、01:00 夜間執行窗、Q4 降為每週（2026-07-20）
19. **DB 保留統一年限**：`DbRetentionDays`=730（未來三年只改設定 1095），全表適用
    （含權限異動、處理歷程，稽核類排除提案已否決），到期直接刪；應用層每晚滾動清理——
    詳 docs/DB-PLAN.md（2026-07-20）
20. **執行模型維持 one-shot**＋工作排程器 01:00；通知維持 Phase 4 不前移
    （系統定位第二層縱深防禦，緊急狀況由第一層監控承擔）（2026-07-20）
21. **AI 角色轉換定案並升級為規模前提**（深析靜態化是 2000 台 AI 預算成立的先決條件）：
    詳本檔「2026-07-20 — AI 角色轉換規劃」段（2026-07-20）

---

## 2026-07-20 — AI 角色轉換規劃：分析引擎 → 白話翻譯層（原 docs/AI-ROLE-PLAN.md）

> 規劃日期：2026-07-20。地位：**2000 台規模的前置依賴**——深析靜態化（Phase A）是
> AI 時間預算成立的先決條件（見本檔上方 PLAN.md 段落「時間預算估算」），不是可選優化。
> 本文件管 AI 呼叫的契約與內容變化；體檢的排程面（due-date 輪巡＋閘門）定案於
> PLAN.md「核心設計決策 B」。

### 角色重定義

轉換前 AI 做四件事：判定風險、評估新型態事件、生成根因/處置、綜合趨勢。
轉換後只做一件事：**把程式算好的結論翻譯成不懂 Event ID 的人能看懂的話**。

| 職責 | 轉換後歸屬 |
|---|---|
| 偵測已知模式 | 規則層（不變） |
| 頻率異常/慢速趨勢 | 趨勢層＋每日慢速趨勢偵測（**強化**：慢速斜線從每週 AI 判讀改為每日確定性偵測） |
| 攻擊鏈/故障鏈 | 關聯層（不變） |
| 風險等級 | 確定性下限為主；AI 的 `risk_level` 欄位保留、**僅能向上拉**（零成本保險，機制不變） |
| 根因/處置知識 | 規則命中 → 靜態知識庫；`Other` 類 → AI |
| 人話敘事 | AI（唯一職責） |

效果示意——同一筆偵測結果，使用者第一眼看到的從：

> `[Critical/Storage] System/disk EventId 153 x47（03:12~23:40）（頻率上升：近14日平均 x8、昨日 x21）`

變成：

> 「這台伺服器的其中一顆硬碟正在壞掉，而且惡化得很快——同樣的錯誤兩週前平均一天 8 次、
> 昨天 21 次、今天 47 次。**今天就該做的事：確認備份是最新的，並安排更換這顆硬碟。**」

### 四個呼叫點的變化

#### 1. 深入分析 → 靜態知識庫（Phase A，收益最大）

- `KnownIssueRule` 擴充三個靜態欄位：`PlainExplanation`（白話說明「這代表什麼」）、
  `LikelyCauses`（常見原因，依可能性排序）、`NextSteps`（具體處置步驟）
- 規則命中的問題 → 報告直接渲染靜態模板，**零 AI 呼叫**；僅 `Other` 類別維持 AI 深析
- 約 25 條規則補寫知識文字（AI 起草、人工審定）；規則表自動測試涵蓋新欄位非空
- 效益：
  - 尖峰日（大範圍事件日恰是規則命中最多的日子）深析呼叫趨近零——2000 台預算的關鍵
  - 同一 Event ID 的建議 100% 一致、零幻覺、零延遲
  - AI 掛掉時報告的「可能原因/處置步驟」不再從缺——轉換前做不到

#### 2. 主分析 → 白話日報（Phase B）

- JSON 契約改為：
  - `headline`：一句話標題，非技術人員能懂
  - `story`：今天發生什麼，白話敘述、禁用 Event ID 與術語
  - `trend_story`：新問題/惡化/延續——接續前幾天脈絡講
  - `action`：現在該做什麼、多急（今天就做／本週內／持續觀察即可）
  - `risk_level` 保留、僅向上拉（NormalizeRisk＋MoreSevere 機制不變）
- System prompt 改翻譯官定位：「讀者是不懂 Event Log 的管理者；程式已完成所有判斷，
  你的工作是把結論講成人話，不要重新判斷、不要引用 Event ID」
- **低風險日不呼叫 AI**：固定模板句「今日無異常訊號，規則/趨勢/慢速趨勢/關聯四層檢查全數通過」。
  **唯一例外（2026-07-20 審查後補上）**：未分類（Other）事件種類達 `MinTailForLowRiskScreening`=20
  以上時仍執行前置掃描——那些事件規則層依定義沒看過，若連掃描都省掉就沒有任何一層檢視過它們；
  掃描若找到值得注意的項目則照常執行主分析，讓發現能經 `MoreSevere` 拉高當日風險等級
  （否則發現只會躺在 `ScreeningNotes` 裡不影響任何判定）。一般的低風險日仍維持零 AI 呼叫。
- 降級語意改正面表述：「偵測與建議完整，僅白話摘要從缺」（偵測不依賴 AI 後這是事實陳述）
- 輸出變短 → 單次呼叫時間靠近 1~20s 下緣（規模預算引用此假設）

#### 3. 報告與 console 雙層呈現（Phase B）

- 報告置頂新增「■ 白話總覽」區塊（headline＋story＋action），主管看完這段即可結束；
  現有技術區塊（問題清單、趨勢數字、原始 log）全部保留在下，供維運查證
- console 紅色橫幅先印 headline；風險等級加行動語意對照：
  高＝「需要立即處理」、中＝「本週內確認」、低＝「無需動作」

#### 4. 前置掃描限縮（Phase C）

- 只掃 `Other` 類尾巴項目——規則命中的尾巴已有靜態知識，不需 AI 意見
- 與 `NetIq.SampleFetchMode: Reduced` 的邏輯一致（範例訊息只有 Security/Other 真正需要）

#### 5. 體檢敘事（Phase C）

- 契約敘事化：上期觀察事項的後續＋本期累積訊號，寫成一段給人看的回顧
  （取代現行 `has_findings + conclusion` 兩欄的判定式輸出）
- 排程/閘門見 PLAN.md——每日確定性慢速趨勢偵測已把「發現」職責接走，體檢只剩「講故事」

### 慢速趨勢層的兩個不變量（2026-07-20 審查後確立）

1. **兩側視窗必須等長**：近期＝今日＋前 6 天（7 天）、前期＝再往前 7 天。長度不一致會讓平穩訊號
   也產生系統性倍率偏差，把 1.5 倍門檻實質放寬（8 vs 7 等於門檻降到約 1.31 倍）。
   已由單元測試與 `--selftest` 各一個「平穩訊號不誤觸發」案例釘住。
2. **未比對必須申報**：前期窗口可靠歷史不足 7 天時完全不比對。歷史本來就不足（部署未滿兩週）
   記 Info；歷史已達兩期長度卻仍不足（前期窗口含 `DataIncomplete` 的日子）記 WARN 並列入
   `UncoveredChecks`——靜默跳過會讓「沒告警」被誤讀成「沒問題」，違反專案的覆蓋率誠實申報原則。

### 品質保證（轉換中偵測面只升不降）

- 風險下限機制一字不動：規則/關聯 Critical → 高；High 問題/頻率異常/關聯訊號/慢速趨勢 → 中
- 慢速惡化偵測從「每週一次、依賴 AI 召回」升級為「每日、確定性、可測試、進 --selftest」
- Prompt injection 面縮小：AI 輸出不影響風險等級（僅向上）、不驅動任何自動化動作
- AI 完全失效時：偵測、風險等級、靜態處置建議、報告全部照常，只缺白話敘事

### 階段

| 階段 | 內容 | 備註 |
|---|---|---|
| A | 靜態知識庫＋深析限縮 Other | 不動任何契約；2000 台預算的先決條件，最先做 |
| B | 白話日報契約＋報告/console 雙層 | |
| C | 體檢敘事化＋前置掃描限縮 | |
| 同步 | README「四層偵測」表 AI 層職責改寫；機房總覽（Phase 3）天生即敘事層，直接沿用新角色 | |

---

## 2026-07-21 — history.txt 儲存層修正規劃：A1 原子寫入／A2 查詢語意／A3 合約測試基底（原 docs/HISTORY-STORE-FIX-PLAN.md）

> 規劃日期：2026-07-21。狀態：**已實作完成（2026-07-21）**，實作紀錄見文末。
> 三項都是「已上線程式碼」的問題，與 NetIQ 功能無關；A2 語意選項與規劃範圍
> 已於 2026-07-21 與使用者確認（顯式錨定日期／本輪只涵蓋 A1–A3）。
> 本案同時是 docs/DB-PLAN.md「txt ↔ DB 一致性保證」機制 #2（介面語意即規格）與
> #3（合約測試基底）在 `IAnalysisRecordStore` 上的落實，實作完成後回寫 DB-PLAN。

### 問題與定案總覽

| # | 問題 | 定案 |
|---|---|---|
| A2 | `ReadRecent(days)` 實作是「最近 N **筆**」（`OrderByDescending(Date).Take(days)`），介面註解卻寫「近 N **天**」；且 `TrendAnalyzer` **不會過濾 targetDate 之後的紀錄**，回補中間缺漏日時未來紀錄會混入該日的趨勢基準 | 介面改為 **`ReadRecent(DateTime anchorDate, int days)` 顯式錨定**，語意＝anchor 往回 N 天（含 anchor 當日）的日期區間 |
| A1 | `AttachWeeklyCheckup`／`Prune` 用 `File.WriteAllLines` 整檔重寫，Web 同時在讀同一份檔案，重寫瞬間可能讀到截斷內容；`TryParse` 對壞行**靜默丟棄**——Web 少幾天資料且無任何跡象 | 整檔重寫改「寫 temp → `File.Replace`」原子替換＋讀取端 tolerant share＋壞行顯性記 WARN；**不引入** `.lock` 跨程序鎖（理由見下） |
| A3 | `JsonlAnalysisRecordStoreTests` 仍是具體類別，`IAnalysisRecordStore` 的語意（ReadRecent 窗口、HasRecord 冪等、Prune 邊界、AttachWeeklyCheckup 更新語意）沒有合約測試釘住，DB 實作屆時無從驗收 | 仿 `HostStoreContractTests` 模式抽 **`AnalysisRecordStoreContractTests` 抽象基底**，A2 的新語意以合約案例固定 |

### A2：`ReadRecent` 顯式錨定日期

#### 潛在 bug 的具體情境（為什麼「以今天為錨」也不對）

`TrendAnalyzer.Apply`（`TrendAnalyzer.cs:44`）拿到 history 後只排除 `DataIncomplete` 與
Security 無權限日，**不會過濾日期**——`ReadRecent` 給什麼它就拿什麼當基準。
回補流程是「找出近 14 天缺漏的日子、由舊到新分析」，中間缺漏日的情境：

> 三天前的執行在寫入當日紀錄前中斷 → 該日缺紀錄，但昨天、今天都有。
> 下次執行回補該日時，`ReadRecent(14)` 取「最近 14 筆」＝**大多是該日之後的紀錄**，
> 未來的資料進了它的 14 日平均與「首次出現」判定。

以「今天」或「最新一筆」為錨的日期窗修不掉這個（錨仍落在缺漏日之後）；
只有把錨交給呼叫端（分析哪一天就錨在哪一天）才是結構性的修法。

#### 介面變更

```csharp
public interface IAnalysisRecordReader
{
    /// <summary>
    /// anchorDate 往回 days 天（日期區間 [anchor-(days-1), anchor]，含兩端）內的紀錄，依日期升冪。
    /// 錨定日之後的紀錄一律不回傳——呼叫端分析哪一天，基準就只能是那一天之前的世界。
    /// DB 實作對應：WHERE date >= @anchor - (days-1) AND date <= @anchor ORDER BY date。
    /// </summary>
    List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days);

    /// <summary>是否存在任何紀錄（首次執行判定用）。DB 實作對應：EXISTS</summary>
    bool HasAnyRecord();

    bool HasRecord(DateTime date);              // 不變
    DateTime? LastWeeklyCheckupDate();          // 不變
}
```

- **舊簽名 `ReadRecent(int days)` 直接移除、不留預設 anchor 的便利多載**——留著的話
  「忘了傳 anchor」編譯照過、行為照舊錯，正是本次要關掉的失誤路徑。呼叫端只有
  3 處＋1 個測試替身，全數明改。
- **含 anchor 當日**的理由：體檢在當日分析寫入之後執行，窗口必須含當天剛寫入的紀錄；
  每日分析呼叫時當日紀錄尚未寫入（`HasRecord` 防重複），自然不會撈到自己，兩情境同一語意即可。
- **新增 `HasAnyRecord()`** 的理由：現在 `WeeklyCheckupService.ShouldRun` 用
  `ReadRecent(1).Count == 0` 表達「從未有任何紀錄」；改日期窗後 `ReadRecent(today, 1)`
  的意思變成「**今天**有沒有紀錄」，語意不再等價。用意圖明確的方法取代，不讓
  「有沒有歷史」搭在查詢窗口的便車上。

#### 呼叫端調整（共 3 處＋測試替身）

| 位置 | 改法 | 行為影響 |
|---|---|---|
| `LogAnalysisService.AnalyzeDayAsync`（`LogAnalysisService.cs:129`） | `ReadRecent(targetDate, historyDays)` | **中間缺漏日 bug 修復**；順序回補時（檔案裡都是 targetDate 之前的紀錄）「往回 14 天」與「最近 14 筆」在無缺漏環境完全相同 |
| `WeeklyCheckupService.RunAsync`（`WeeklyCheckupService.cs:73`） | `ReadRecent(checkupDate, intervalDays)` | 無（體檢日就是最新日） |
| `WeeklyCheckupService.RunAsync` 找上次體檢結論（`WeeklyCheckupService.cs:92`） | `ReadRecent(checkupDate, Math.Max(21, intervalDays * 3))` | 上次體檢若落在 21 天窗外會找不到→「無延續脈絡可帶入」，本來就定義為非錯誤，可接受 |
| `WeeklyCheckupService.ShouldRun`（`WeeklyCheckupService.cs:60`） | 改用 `HasAnyRecord()` | 語意由「有沒有任何紀錄」明確承接，行為不變 |
| `WeeklyCheckupServiceTests` 的 `FakeReader` | 跟隨新簽名 | — |

#### 行為變更的誠實申報（實作時寫進 commit 訊息）

有缺漏日的既有環境，A2 上線後第一次執行的趨勢基準會與之前不同：

- 窗外的舊紀錄不再墊進基準 → `DaysSeenInHistory` 變小、原本被誤判為「重複發生」的
  簽章可能改判「首次出現」——**這是修正不是退化**（README 對趨勢層的描述本來就是
  「近 14 日」，本次讓名實相符）。
- 無缺漏的環境（絕大多數日子）行為完全不變。

### A1：整檔重寫原子化與讀取容錯

#### 前提釐清：為什麼**不需要** hosts.json 那套 `.lock` 跨程序鎖

`hosts.json` 有批次與 Web **兩個寫入者**，所以需要跨程序互斥（步驟 2 已做）。
`history.txt` 不同：**寫入者只有批次**（`Append`／`Prune`／`AttachWeeklyCheckup` 全在批次；
Web 經 `IAnalysisRecordQuery` 唯讀），且批次自身有 `Global\LogForesight` 單一執行個體
互斥鎖——寫入者對寫入者的競態**已在結構上排除**。剩下的唯一問題是
「讀者讀到重寫到一半的檔案」，這用原子替換就能解，加 `.lock` 只會讓 Web 的每次查詢
與批次寫入互相排隊，是純粹的代價沒有收益。

#### 變更明細（全部在 `JsonlAnalysisRecordStore`，介面不動）

1. **`RewriteAtomic(string[] lines)` 私有方法**：寫 `history.txt.tmp`（UTF-8 無 BOM）→
   `File.Replace(tmp, path, null)`；目標不存在時 `File.Move`。`AttachWeeklyCheckup`（:77）
   與 `Prune`（:116）的 `File.WriteAllLines` 改呼叫它。與 `JsonCollectionFile.WriteAtomic`
   同一手法，但不共用實作（一個是整份 JSON 陣列、一個是 JSONL 行集合，強行抽共用只會
   多一層無意義的抽象）。

2. **`File.Replace` 的 sharing violation 重試**：讀者持檔的瞬間 Replace 會擲 `IOException`。
   短退避重試（10 次 × 50ms 量級，實作時定常數）；超過仍失敗就**讓例外外拋**——
   單寫入者環境下衝突只可能來自秒級的讀取，重試必然成功；真的失敗代表有未知的長時間
   持檔者，Prune/體檢附掛靜默放棄比顯性失敗更糟。

3. **讀取端 tolerant share**：`ReadAll` 的 `File.ReadLines`（預設 `FileShare.Read`）改為
   `FileStream(path, Open, Read, FileShare.ReadWrite | FileShare.Delete)` ＋ `StreamReader`
   逐行——讀者從此不會擋住寫入端的 Replace（第 2 點的重試因此極少真的觸發），
   也容忍批次同時 Append。檔案不存在回空清單（維持現狀）。

4. **壞行顯性化**：`ReadAll` 統計 `TryParse` 失敗行數，>0 時記 WARN（含檔案路徑、
   壞行數、前幾筆的行號）。防 log 洪水：記住上次的壞行數，**數字有變化才記**——
   Web 每次查詢都全檔重讀，同一批壞行每次都刷 WARN 等於把訊號淹掉。

5. **`Append` 不變**（`File.AppendAllText`）：與第 3 點的讀者 share 相容；讀者恰好讀到
   「append 寫了半行」時該行 TryParse 失敗被略過，下一次讀取即恢復——瞬時現象，
   由第 4 點的 WARN 可見，可接受，不為此引入行級鎖。

### A3：`AnalysisRecordStoreContractTests` 合約測試基底

#### 結構

仿 `HostStoreContractTests`／`AnalysisRecordQueryContractTests` 既有模式：

```csharp
public abstract class AnalysisRecordStoreContractTests : IDisposable
{
    protected abstract IAnalysisRecordStore CreateStore();
    // ...共用的 Record 建構 helper
}

public class JsonlAnalysisRecordStoreContractTests : AnalysisRecordStoreContractTests { ... }
```

既有 `JsonlAnalysisRecordStoreTests` 的 5 個案例處置：

- 搬進基底（屬合約，兩後端都必須一致）：無風險日精簡（`RecordStorageShaper` 是共用
  規則，DB 後端也呼叫同一份）、風險中以上完整保留、週體檢附掛＋`LastWeeklyCheckupDate`、
  `Host`/`DeepDives` 序列化讀回、`HasRecord`。
- JSONL 特定（留在衍生類別或獨立測試類別）：壞行略過＋WARN 行為、原子重寫的併發案例。

#### 新增合約案例（釘住 A2 語意與既有未釘語意）

| 案例 | 釘住的語意 |
|---|---|
| `ReadRecent_錨定日往回N天_窗外較舊紀錄不回傳` | 缺漏日不得由更舊紀錄補位（14 日平均的分母誠實） |
| `ReadRecent_錨定日之後的紀錄不回傳` | **中間缺漏日 bug 的迴歸測試**——實作時先寫、A2 改完由紅轉綠 |
| `ReadRecent_含錨定當日` | 體檢窗口含當天剛寫入的紀錄 |
| `ReadRecent_依日期升冪` | 呼叫端（prompt 組裝）依賴的順序 |
| `HasAnyRecord_空為false_有紀錄為true` | `ShouldRun` 的首次執行判定 |
| `HasRecord_同日不同時刻視為同一天` | 回補冪等的日界比對 |
| `Prune_保留天數邊界_cutoff當天保留` | 邊界日不被誤刪（`>= cutoff`） |
| `AttachWeeklyCheckup_日期不存在_不擲例外不寫入` | 「安靜略過＋WARN」是契約不是實作巧合 |

#### JSONL 特定案例（A1 的驗證）

- `整檔重寫時有讀者持檔_寫入仍成功`：以 tolerant share 開一個讀取 handle 不放，
  執行 `AttachWeeklyCheckup` → 應成功（重試生效）且內容正確——手法同
  `JsonCollectionFileLockTests` 的外部持有者模擬。
- `壞行_略過且其餘紀錄照常讀回`：檔案中間插一行垃圾，`ReadRecent` 回傳其餘紀錄。

### 檔案異動清單

| 檔案 | 變更 | 對應 |
|---|---|---|
| `Core/Persistence/IAnalysisRecordStore.cs` | `ReadRecent` 新簽名＋語意註解（含 DB WHERE 對應）、新增 `HasAnyRecord` | A2 |
| `Core/Persistence/JsonlAnalysisRecordStore.cs` | `ReadRecent` 日期窗實作、`HasAnyRecord`、`RewriteAtomic`＋重試、tolerant share 讀取、壞行 WARN | A1+A2 |
| `LogForesight/Service/LogAnalysisService.cs` | `ReadRecent(targetDate, historyDays)` | A2 |
| `LogForesight/Service/WeeklyCheckupService.cs` | 三處呼叫改錨定＋`ShouldRun` 改 `HasAnyRecord` | A2 |
| `Tests/WeeklyCheckupServiceTests.cs` | `FakeReader` 跟隨新簽名 | A2 |
| `Tests/AnalysisRecordStoreContractTests.cs`（新） | 合約基底＋上表案例 | A3 |
| `Tests/JsonlAnalysisRecordStoreTests.cs` | 改為衍生類別＋JSONL 特定案例 | A3 |
| `docs/DB-PLAN.md` | 一致性機制 #2/#3 標注已於 `IAnalysisRecordStore` 落實 | 收尾 |

不需要動的：README（趨勢層「近 14 日」的描述本來就是 A2 之後的正確語意）、
`IAnalysisRecordQuery` 及其合約測試（另一介面，步驟 1 已完成）、`RecordStorageShaper`。

### 實作順序與驗收

1. **A3 先行（紅燈）**：建合約基底、搬既有案例、寫 A2 的新案例——
   「錨定日之後不回傳」此時應失敗，證明測試真的在測。
2. **A2**：介面＋實作＋3 個呼叫端一次改完（行為變更集中在同一個 commit，好回溯）。
   → 全測試轉綠。
3. **A1**：`RewriteAtomic`＋tolerant share＋WARN＋JSONL 特定案例。
4. **驗收**：建置零警告、全部單元測試、`--selftest` 76 項；手動驗證一項——
   批次執行中（可用回補大量日期製造長寫入窗）同時重整 Web 問題查詢頁，確認無缺天、
   無例外。

### 實作紀錄（2026-07-21 完成）

建置零警告、**490 單元測試**（新增 24 個）、76 項 `--selftest` 全數通過；
完整套件連跑 3 次、併發測試連跑 5 次均穩定。

**兩個迴歸測試已實測會分辨新舊行為**（沿用本專案「實測拿掉一欄會 FAIL」的做法）：
暫時還原 `Take(days)` → 3 個 `ReadRecent` 合約案例失敗；暫時還原 `WriteAllLines` →
`重寫期間_先前開啟的讀取handle仍看到完整舊內容` 失敗。

#### 規劃時未預見的三件事（都由併發測試逼出來）

規劃把「批次寫入期間 Web 查詢」列為**手動**驗收（重整頁面確認）。實作時改寫成
可重複執行的併發測試（持續重寫 × 持續讀取），結果一次抓出三個規劃階段沒想到的問題——
人工重整只能碰運氣撞上那幾毫秒，不可能發現這些：

1. **讀取端也需要重試**。規劃只在寫入端加了重試。實測讀者在 `File.Replace` 的瞬間開檔會
   擲 `IOException`（共用違規）——那會讓 Web 查詢在重寫瞬間直接噴錯誤頁，比原本的
   torn read 更糟。已補上 `OpenForRead` 的重試。
2. **`File.Replace` 有「目的檔短暫不存在」的空窗**。修掉第 1 點後，322 次併發讀取仍有
   **79 次讀到 0 筆**——空窗期的 `FileNotFoundException` 被當成「檔案不存在」回空清單。
   最終解法：讀取端對「檔案不見」也重試，但**只在此檔曾被成功開啟過時**
   （`_fileSeen` 旗標）——首次執行真的還沒有 history.txt 時不空等。
3. **`File.Move(overwrite)` 不是更好的替代**。中途改用它（無空窗）後，
   兩個「讀者持檔」測試轉為失敗：MoveFileEx 只要目的檔被開啟就直接失敗，
   Web 持續查詢時寫入端會反覆重試到放棄。

**最終取捨（寫進程式碼註解）**：選 `File.Replace` 而不是 `File.Move(overwrite)`，因為
「寫入端因讀者而失敗」無法在讀取端補救，而「替換空窗」可以——讀取端重試即可。
兩邊要一起看才成立，單獨改一邊都不對。

#### 一併修正

- `File.Replace`／開檔的重試改為同時涵蓋 `UnauthorizedAccessException`（與 `IOException`
  同屬暫時性碰撞，`JsonCollectionFile` 的鎖檔取得也是這樣處理）。
- 併發測試本身加保險絲：writer 的 `done.Cancel()` 移進 `finally`＋`CancelAfter(30s)`——
  第一版 writer 擲例外時 reader 會無限迴圈，把一個失敗的測試變成掛住整個測試回合。

#### 實際檔案異動

與規劃清單一致，額外多了 `_fileSeen`／`MissingFileRetryCount` 兩個欄位，
以及測試檔的併發案例。`docs/DB-PLAN.md` 一致性機制 #2／#3 已於本案在
`IAnalysisRecordStore` 落實。

### 風險與回退

- A2 是行為變更：有缺漏日的環境，第一次執行後趨勢判定可能與前次不同
  （「重複發生」→「首次出現」方向），屬修正，已於上文申報；無缺漏環境零差異。
- A1 純防禦性，無行為變更；`File.Replace` 重試失敗的例外外拋是新的失敗模式，
  但它取代的是「靜默寫壞檔案」，失敗方向正確。
- 回退：三項各自獨立 commit，任一項有問題可單獨 revert（A3 依賴 A2 的語意定案，
  但測試案例本身不影響產品行為）。

---

## 2026-07-21 — NetIQ 主機清單 Web 維護與主機配對規劃（原 docs/NETIQ-HOSTLIST-WEB-PLAN.md）

> 規劃日期：2026-07-21。本文件規劃三件事：
> (1) admin 在 Web 上維護 NetIQ 主機清單（取代 docs/PLAN.md 的 per-Sentinel txt 檔）；
> (2) NetIQ 主機與既有主機（CSV 預先登錄或本機回報建立）的**配對**；
> (3) 群組歸屬與 Sentinel 歸屬**脫鉤**——現有主機群組未必對應相同的 Sentinel。
>
> **修訂紀錄**
> - 第一輪（2026-07-21）：初版，識別鍵以 IP/主機名為自然鍵的方案。
> - 第二輪（2026-07-21）：三項使用者定案——**紀錄改以主機資料表 PK（HostId）關聯**
>   （取代自然鍵方案，原「矛盾 1」消解）；**Sentinel 歸屬未指定時由批次自動確認**
>   （節流、不瞬間大量查詢）；**IP 重複改軟處理**（不擋存檔，衝突佇列＋人工處置）。

### 識別設計（第二輪定案：PK 關聯）

#### 核心：紀錄與主機以 `HostId` 關聯，不再以名稱/IP 字串比對

- `DailyAnalysisRecord` 新增 **`HostId`**（long）；既有 `Host` 字串欄位**保留**，
  角色降為「寫入當下的顯示名快照」＋舊資料相容（見下方遷移）。
- 批次寫入紀錄前一定先取得主機列（本機 `Touch`、NetIQ 走清單 provider——
  Web 維護模式下清單項目本身就有 `HostId`，**批次從清單直接拿到 PK，不需任何名稱解析**）。
- Web 查詢改以 `HostId` 比對：`RecordQueryFilter.HostNames` → `HostIds`。
  `VisibilityService` 本來就回傳 host id 集合，中間「id → 名稱」的轉換層整個移除，
  查詢路徑反而變簡單。
- DB 階段零轉換：`lf_daily_records.host_id` FK 正是 docs/DB-PLAN.md 既有欄位設計，
  JSONL 期就把關聯鍵寫對，匯入器不需要做名稱→id 的推斷。
- **原「矛盾 1」（IP vs Sentinel 主機名何者為鍵）消解**：身分＝PK；
  IP 只是「對 Sentinel 下查詢的條件」（監控屬性）；主機名只是顯示屬性。
  `WebHost.HostName` 在 NetIQ 列＝admin 登錄時的識別字串（通常填 IP），
  `DisplayName`＝Sentinel 回報的主機名（批次回填）；兩者都可改、都不影響紀錄歸戶。

#### 舊資料相容與遷移

既有 `history.txt` 的紀錄只有 `Host` 字串、沒有 `HostId`：

- **查詢 fallback**：讀到 `HostId == 0/null` 的舊紀錄時，退回以 `Host` 字串
  （不分大小寫）比對主機列的 `HostName`——舊資料不遷移也查得到。
- **可選一次性回填**：`--backfill-hostid` 指令把 history 逐行補上 `HostId` 後整檔重寫
  （對不到主機列的行保留原樣並列警告）。建議在切換後找一天執行，讓 fallback 路徑退役。
- 兩用其一即可，合約測試對 fallback 行為加案例釘住。

#### PK 方案的三個代價（必須正視，不是免費的）

1. **`hosts.json` 升級為身分錨點**：以前紀錄靠字串自我描述，主機檔壞了紀錄還在；
   改 PK 後 `hosts.json` 遺失/重建＝id 重配＝紀錄斷鏈。
   對策：`Host` 字串快照保留（斷鏈時人仍可辨認＋可重新 backfill）；
   `hosts.json` 列入部署備份清單（README 部署章節補一句）；DB 階段自然消失（identity column）。
2. **id 產生的併發**：`JsonHostStore` 配號是 max+1，批次（01:00 Touch）與 Web（白天 Upsert）
   同時建主機理論上會撞號。時段錯開後風險極低，但實作時 `JsonHostStore` 的建列路徑
   加檔案鎖（或寫入前重讀重配），成本一行，把理論風險關掉。
3. **報告/檔案命名**：per-host 報告目錄與 history 檔名（PLAN.md：`history\{host}.txt`）
   改用 `{HostId}_{HostName}` 前綴（id 保證唯一與追溯、名稱保留人類可讀性）。

#### 別名展開（原「矛盾 2」，PK 下簡化）

Merge 之後查目標主機需涵蓋被併入主機的紀錄。PK 方案下實作簡化為：
查詢主機 X 時，`HostIds` = X.HostId ＋ 所有 `MergedInto == X.HostId` 的墓碑列 HostId
（一層即可；「墓碑不可再為 Merge 目標」在 `HostAdminService` 補檢查）。
這仍是**現有 Web 的缺口**（目前 Merge 後舊主機紀錄從畫面消失），不依賴 NetIQ，建議最先修。

### 核心設計決策

#### 決策 A：清單即主機——不建第二張表

NetIQ 清單項目**直接就是 `WebHost` 列**（`Source='netiq'`），不新增獨立實體：

- admin 在 Web 新增清單項目 = `Upsert` 一列 `WebHost`：
  `HostName`（登錄識別字串，通常填 IP 或既知主機名）、`IpAddress`（查詢鍵，見 IP 衝突節）、
  `NetiqServer`（可留空→進入自動歸屬確認，見下）、`RoleDesc`、群組、負責人
- 批次的查詢清單 = `IHostStore.GetAll()` 篩
  `Source=='netiq' && Active && MergedInto==null && NetiqServer 已歸屬 && IP 無衝突`，
  按 `NetiqServer` 分組；provider 輸出 `(HostId, Ip, RoleDesc)`——**批次全程以 HostId 寫紀錄**
- 「停止監控」= 既有 `Active=false`（語意同 PLAN.md「移除 IP → 停止分析、history 保留」）
- 「主機搬遷 Sentinel」= 編輯 `NetiqServer`（路由屬性，PK 不變，歷史無縫延續）

#### 決策 B：配對＝既有 Merge 的擴充，純人工

情境：公司先以 CSV 依主機名登錄主機（含群組/負責人），之後 NetIQ 清單接入、
同一台機器以另一列身分開始回報——兩列其實是同一台。

- **配對就是 `Merge`**：admin 在主機詳情頁把兩列併為一列。建議方向仍為
  「名稱列（CSV 登錄）併入 NetIQ 列」，因為監控設定（IP/Sentinel）在 NetIQ 列上；
  但 PK 方案下**方向不再影響歷史歸戶**（各自的紀錄掛各自的 HostId，靠別名展開聚合），
  綁錯用 `Unmerge` 可完全復原，不會有資料損失
- **Merge 擴充：描述性欄位搬移**——目標列的 `RoleDesc`/群組/負責人為空時自來源帶入
  （目標已有值則保留不覆蓋）；畫面提供併入預覽
- **`Unmerge` 反向修復**（新介面方法）：清 `MergedInto`、恢復 `Active`
- **純人工，不做程式比對**（維持 2026-07-20 決策 #12）：不自動綁定；
  目標選擇器旁列出同 `DisplayName`/同 `IpAddress` 的候選列作為**線索**，最終動作 admin 確認

#### 決策 C：群組與 Sentinel 歸屬脫鉤

Sentinel 是**路由屬性**（哪台 Sentinel 查得到這台主機），群組是**授權/分類維度**：

- **不自動建立 per-Sentinel 群組**；要看「某台 Sentinel 轄下主機」是主機頁篩選條件，不是群組
- 新登錄的 NetIQ 主機未分組 → 依既有授權模型**只有 admin 看得到**（安全預設，
  新主機不會意外曝光給錯的部門）
- 配套：**「未分組主機」佇列**（主機頁篩選＋儀表板 admin 提示數）＋
  **批次指派**（勾選多台一次入組）；大量初始分組建議走既有 CSV 匯入的 `groups` 欄位

#### 決策 D：清單主人交接——txt 與 Web 單一主人，設定切換 ⏸ 已廢止（2026-07-24）

> **廢止**：docs/NETIQ-WEB-CONFIG-PLAN.md 定案 12 決定 Txt 主機清單模式整支退役
> （`HostListSource`/`HostListDirectory` 設定、`TxtHostListProvider`、`--import-hosts` 全刪），
> 不是「切換」而是「拿掉其中一個主人」——Sentinel 連線設定進 Web 之後，「清單主人在 txt」的
> 過渡定位已消失，txt 內容需要匯入時改用 Web「批次貼上」（`bulk-modal`）即可，不需要專屬的
> 交接 SOP。原文保留供歷史對照：

- 批次抽 `IHostListProvider`：`TxtHostListProvider`（PLAN.md 原設計）與
  `StoreHostListProvider`（讀 `IHostStore`，本規劃）；
  設定 `NetIq.HostListSource: "Txt" | "Web"`（預設 `Txt`）
- **同一時間只有一個主人、不做雙向同步**（維持既定原則）
- 交接 SOP：`--import-hosts`（txt → host store，冪等 upsert）→ 核對 Web 主機頁筆數 →
  設定切 `Web` → txt 移除。Txt 模式下批次仍以 `Touch` 取得 HostId 後寫紀錄（PK 方案不分模式）

#### 決策 E：Sentinel 名單來源——批次 appsettings 唯讀，不另建表 ⏸ 已修訂（2026-07-24）

> **修訂**：docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1、2 把單一事實來源從「批次 appsettings.json」
> 改為「共用儲存層（`sentinels` blob，`ISentinelStore`）」——當時的前提是「批次與 Web 靠
> `DataRoot` 共用檔案，設定檔就是共用點」；Phase C 之後共用點已是資料庫，讓 Web 直接管理
> Sentinel（完整 CRUD，含密碼加密）反而消除了「畫面選得到、批次卻查不到」的分歧風險。
> 「同一時間只有一個主人」原則不變，主人從 appsettings.json 換成共用 store。原文保留供歷史對照：

Web 的 Sentinel 下拉選單讀批次 `appsettings.json` 的 `NetIq.Servers`（同一 DataRoot、唯讀）。
BaseUrl/帳密只有批次需要；不建 `lf_sentinels` 表、不做 Web 端 Sentinel CRUD——
加一台 Sentinel 本來就要改批次設定，單一事實來源。

### Sentinel 歸屬自動確認（第二輪新增）

登錄/匯入時 `netiq_server` 可留空——不強迫 admin 知道每台主機歸哪台 Sentinel，
由批次自動確認，但**不能瞬間大量查詢造成 Sentinel 負擔**：

- **狀態**：`NetiqServer == null` 的活躍 NetIQ 列＝「待歸屬確認」，
  **不進**日常輪巡清單；Web 主機頁顯示狀態徽章與待確認數
- **執行者是批次，不是 Web**——Web 永遠不直接連 Sentinel（帳密與連線邏輯只存在批次，
  維持架構邊界）。每晚批次執行時處理待確認佇列；另提供 `--discover-hosts` 手動指令
  供初次大量匯入後立即跑
- **查詢方式（優化：分批聚合，不是一台一台）**：對每台 Sentinel 各發
  「這批待確認 IP（每批沿用 Q1 的分批大小，如 50 個）近 24h 是否有事件、`GROUP BY 主機`」
  的聚合查詢——成本是 `Sentinel 數 × ⌈待確認數/50⌉` 次查詢，而不是
  `待確認數 × Sentinel 數` 次逐台探測；沿用 per-server 單一併發佇列＋`QueryDelayMs` 節流，
  對 Sentinel 的負擔與日常 Q1 同級
- **每輪上限**：`NetIq.DiscoveryBatchLimit`（如每晚 500 台）封頂，超出的留下一輪——
  初次匯入 2000 台也不會在單晚打滿查詢
- **歸屬判定**：
  - 恰好一台 Sentinel 有事件 → 自動寫入 `NetiqServer`，次日起進日常輪巡（稽核記一筆「系統自動歸屬」）
  - 多台都有事件（轉送重疊）→ **不自動選**，列入 Web「多重歸屬待確認」清單附各台事件數，
    admin 人工擇定（與純人工綁定哲學一致）
  - 全部沒有 → 維持待確認並列入「查無資料」清單（IP 錯、agent 未回報、未納收錄——
    都是要人處理的事）；下一輪自動重試，重試 N 輪仍無則只留清單不再查（`DiscoveryMaxAttempts`）
- 已歸屬主機連續多日在其 Sentinel 查無資料時，既有「無資料主機」告警已涵蓋；
  是否自動重新探測留待實際營運看需求，本階段不做

### IP 重複的軟處理（第二輪定案：不擋存檔，衝突佇列）

IP 理論上全域唯一（已與網路端確認），但清單維護難免打錯或交接期重疊：

- **存檔不擋**：新增/匯入時 IP 與既有活躍 NetIQ 列重複，照樣存檔，
  但該狀況成為「IP 衝突」——**衝突是導出狀態**（同 IP 的活躍 NetIQ 列 ≥2 即衝突），
  不加欄位、沒有要維護的狀態機
- **輪巡行為**：衝突 IP 只輪巡**最早建立的那一列**（行為可預測），
  其餘列跳過並在執行 log 與機房總覽「來源狀態」記明「因 IP 衝突未輪巡」——
  不會兩列都查、重複寫紀錄
- **Web 衝突佇列**：主機頁篩選「IP 衝突」＋儀表板 admin 提示數；每組衝突並列顯示，
  處置三選：**改 IP**（打錯的情況）、**停用其中一列**（汰換交接的情況）、
  **Merge**（其實是同一台重複登錄的情況）；處置完衝突自動消失（導出狀態的好處）
- CSV 匯入遇衝突：照匯（同上），匯入結果報告列出衝突組數提醒去佇列處理

### 資料模型變更

| 項目 | 變更 |
|---|---|
| `DailyAnalysisRecord` | 新增 `HostId`（long；舊紀錄無此欄位＝0，查詢走名稱 fallback）；`Host` 保留為顯示名快照 |
| `WebHost` | 新增 `DisplayName`（Sentinel 回報主機名，批次回填；本機來源 null）；`HostName` 註解修正為「登錄識別字串，不再承擔紀錄關聯職責」 |
| `RecordQueryFilter` | `HostNames` → `HostIds`（`null`=不限、空集合=無權看任何主機的語意**必須保留**——授權正確性關鍵） |
| `IHostStore` | `TouchNetiq(long hostId, string? displayName, DateTime reportedAt)`（批次回填顯示名＋回報時間，不動 Web 欄位）；`Merge` 擴充描述性欄位搬移；新增 `Unmerge`；建列路徑加鎖防撞號 |
| `IHostListProvider`（新） | 輸出 per-Sentinel 的 `(HostId, Ip, RoleDesc)`；Txt 實作內部先 `Touch` 取得 HostId |
| 欄位所有權 | 批次寫：`LastReportAt`/`DisplayName`/`IpUpdatedAt`/自動歸屬的 `NetiqServer`。Web 寫：其餘。`MergedInto` 僅 Merge/Unmerge 路徑寫 |
| DB 對應 | `lf_hosts` 加 `display_name nvarchar(255) NULL`；`lf_daily_records.host_id` 原設計即 FK，零調整 |
| 設定 | `NetIq.HostListSource`、`NetIq.DiscoveryBatchLimit`、`NetIq.DiscoveryMaxAttempts` |

### Web UI（admin 專屬功能）

1. **主機頁擴充**：篩選（來源/Sentinel/未分組/待歸屬/IP 衝突/未配對）；
   單筆新增（IP 即時驗證格式、Sentinel 可留空=待歸屬）；
   **批次貼上**（textarea 多行 `IP[,角色描述]`，逐行驗證、不合法行列原因、合法行照常入庫）；
   停用/啟用；狀態徽章（待歸屬/查無資料/IP 衝突/多重歸屬待確認）
2. **配對操作**（主機詳情頁）：目標選擇器＋線索區（同 DisplayName/IpAddress 候選並列）；
   併入預覽（明列哪些空欄位將自來源帶入）；墓碑列標注「已併入 →」＋ `Unmerge`
3. **衝突/待確認佇列**：IP 衝突組、多重歸屬待確認、查無資料清單，各附處置動作
4. **未分組佇列**：未分組數提示＋勾選多台批次入組
5. **稽核**：新增/停用/批次貼上/配對/解除配對/衝突處置/系統自動歸屬全部寫入既有 `audit.jsonl`

### 批次端變更

| 檔案 | 變更 |
|---|---|
| `Service/HostListProviders.cs`（新） | `IHostListProvider` ＋ Txt/Store 兩實作；Store 實作負責排除待歸屬/衝突/墓碑列 |
| `Service/SentinelDiscovery.cs`（新，Phase 1 隨 SentinelClient） | 待歸屬佇列處理：分批聚合查詢、唯一命中自動歸屬、多重/無命中入清單；`DiscoveryBatchLimit`/`MaxAttempts` |
| `Program.cs` | 依 `HostListSource` 選 provider；`--import-hosts`、`--discover-hosts`、`--backfill-hostid` 指令 |
| 機房 pipeline | 紀錄一律以 `HostId` 寫入；分析後 `TouchNetiq` 回填 `DisplayName`/`LastReportAt`；「無資料主機」與「因衝突未輪巡」列入機房總覽 |

### 測試與驗收

- `JsonlAnalysisRecordStore`/查詢：**HostId 關聯＋舊紀錄名稱 fallback** 合約案例；
  `HostIds` 空集合=查無（授權語意）案例
- `HostStoreContractTests` 增：`TouchNetiq` 欄位所有權、Merge 搬移（目標空才帶入）、
  `Unmerge`、墓碑不可為 Merge 目標、建列撞號防護
- `VisibilityServiceTests` 增：未分組 netiq 主機一般使用者不可見/admin 可見
- `RecordQueryTests` 增：別名展開（併入後查目標主機看得到來源 HostId 下的紀錄）
- Discovery 單元測試（SentinelClient 抽介面 stub）：唯一命中自動歸屬、多重命中不自動、
  上限封頂、重試 N 輪後停查
- 衝突導出狀態測試：同 IP 兩活躍列→只輪巡最早列＋另一列標記；處置後衝突消失
- 端到端驗收（配合 PLAN.md Phase 2 試點）：Web 建試點清單（部分不填 Sentinel）→
  `--discover-hosts` 自動歸屬 → 批次以 `HostListSource=Web` 跑 → `DisplayName`/`LastReportAt` 回填 →
  未分組僅 admin 可見 → 入組後部門可見 → 配對一台 CSV 預登錄主機、歷史以 HostId 歸戶正確

### 實作順序

| 步驟 | 內容 | 備註 |
|---|---|---|
| 1 | `DailyAnalysisRecord.HostId`＋查詢改 `Hosts`＋名稱 fallback＋別名展開修復 | ✅ **已完成（2026-07-21）**，見下節 |
| 2 | `IHostStore` 擴充（TouchNetiq/Merge 搬移/Unmerge/建列鎖）＋合約測試 | ✅ **已完成（2026-07-21）**，見下節 |
| 3 | Web UI：清單維護＋批次貼上＋各佇列＋配對/解除 | ✅ **已完成（2026-07-21）**，見下節 |
| 4 | 批次 `IHostListProvider`＋`--import-hosts`＋`HostListSource`＋`--backfill-hostid` | ✅ **已完成（2026-07-21）**，見下節（`--backfill-hostid` 依開放問題 #1 未實作） |
| 5 | `SentinelDiscovery`＋`--discover-hosts` | 掛 Phase 1（隨 SentinelClient 一起做，分批聚合查詢與 Q1 共用機制） |
| 6 | Phase 2 試點端到端驗證 | 上節驗收清單 |

### 步驟 1 實作紀錄（2026-07-21 完成）

建置零警告、**407 單元測試**（新增 16 個）與 **76 項 `--selftest`** 全數通過。

**新增**
- `Core/Models/HostIdentity.cs`：`HostKey`（PK＋名稱快照）、`HostMatcher`（比對規則單點定義：
  PK 優先、`HostId==0` 才退回名稱）、`HostIdentityResolver`（`Expand` 別名展開／`Surviving`
  合併鏈跟隨）、`HostLookup`（紀錄→存活主機的 O(1) 索引）。
- `DailyAnalysisRecord.HostId`；`RecordStorageShaper` 同步複製（反射式測試已自動涵蓋）。
- `Tests/HostIdentityTests.cs`：解析純函數 11 案 ＋ `RecordRepository` 別名展開 5 案
  （含**授權反向防線**：未併入的其他主機不得因展開而進入可見範圍）。

**變更**
- `RecordQueryFilter.HostNames` → `Hosts`（`HostKey` 集合）；`IAnalysisRecordQuery.GetOne`
  改收識別集合，依傳入順序擇一（存活主機優先）。空集合＝查無資料的授權語意原樣保留。
- `RecordRepository`：`ResolveHostName` → `ResolveHostKeys`；可見範圍展開為
  「可見主機 ＋ 已併入它們的墓碑列」。
- `RecordQueryService`：清單/詳情/時間軸一律解析到**存活**主機；處理狀態改以存活主機名稱
  比對（否則合併前的風險日處理狀態會全部看起來像未處理）。
- `LogAnalysisService` 新增 `hostId` 建構參數；`Program.cs` 的主機登記提前到分析服務建立之前，
  以 `Touch(...).HostId` 取得 PK；登記失敗時 hostId 維持 0、走名稱 fallback，不中斷分析。
- `HostAdminService.MergeHost`：擋下「以墓碑為併入目標」。

**實作期間的三個修正（規劃時未預見）**
1. `GetHostDetail` 原本 `ToDictionary(r => r.Date.Date)`，合併當天兩個識別各有一筆時會因
   **重複鍵整頁例外**；改為依日期分組並取存活主機那筆。
2. `Expand` 原本只走一層 `MergedInto`，等於讓正確性依賴新加的寫入端守則；既有 `hosts.json`
   可能已存在合併鏈，改為**依存活主機遞移判斷**，查詢端自身即正確。守則保留為使用者體驗
   考量（併入已停用的主機看不出資料最後去哪），不再是載重的不變式。
3. 處理狀態（`handling.json`）以主機名稱為鍵，別名展開後必須用存活主機名稱查，
   否則合併前的紀錄狀態全部退回「未處理」。

**尚未處理（刻意）**：`--backfill-hostid` 未實作，依開放問題 #1 傾向永久保留名稱 fallback；
`PermissionChangeService` 與 `HandlingService.GetTodo` 仍以主機名稱運作（各自的 store 就以
名稱為鍵，不在本步驟範圍）。

### 步驟 2 實作紀錄（2026-07-21 完成）

建置零警告、**417 單元測試**（新增 10 個）與 76 項 `--selftest` 全數通過。

**新增**
- `WebHost.DisplayName`：Sentinel 回報的主機名稱，批次回填、Web 唯讀。
  NetIQ 主機以 IP 登錄，光看清單認不出是哪台機器，這個欄位補上人看得懂的名字。
- `IHostStore.TouchNetiq(hostId, displayName, reportedAt)`：以 **HostId 定位**而不是名稱——
  NetIQ 主機必定由 Web 清單維護、已經存在，用名稱補建的話 admin 打錯字就會默默多出一台幽靈主機。
  主機不存在回 null 並安靜略過（清單剛被刪的競態不該中斷分析）。
- `IHostStore.Unmerge(hostId)`：清墓碑標記＋恢復啟用；`HostAdminService.UnmergeHost`
  ＋`host_unmerge` 稽核＋`POST /api/admin/hosts/{id}/unmerge`。
- `Merge` 描述性欄位搬移：目標的角色描述／群組／負責人／顯示名／IP／Sentinel 為空時自來源帶入，
  目標已有值一律保留。**搬移是複製不是移動**，來源保留自己的值，`Unmerge` 才還原得回來。

**跨程序檔案鎖（規劃時列為「一行成本」，實作後認定必要且改在基底類別）**
- `JsonCollectionFile.Mutate` 現在同時持有行程內鎖與 `.lock` 鎖檔（`FileShare.None`＋
  `DeleteOnClose`，逾時 15 秒後讓例外外拋）。
- 原本只有行程內 `lock`＋原子替換：原子替換擋得住半截檔案，**擋不住更新遺失**——
  兩邊各自讀到舊值、後寫的整份蓋掉先寫的。`hosts.json` 正是批次與 Web 共同寫入的檔案
  （WEB-SPEC §10.2），而 §10.4 早已規定「跨程序以檔案鎖處理」，這次才真正落實。
- 後果具體：同一個 HostId 配給兩台主機（識別碼現在是紀錄的關聯鍵，撞號＝紀錄歸錯主機、
  跨越授權邊界），或批次的回報時間把 Web 剛設好的群組蓋掉。
- **用鎖檔而不是具名 Mutex**：批次由工作排程器執行、Web 是另一個行程，可能不在同一個
  登入工作階段——`Local\` Mutex 跨不過工作階段，`Global\` 需要 SeCreateGlobalPrivilege。

**尚未接線（步驟 3、4 的範圍）**：`TouchNetiq` 目前沒有呼叫端（機房 pipeline 尚未實作）；
`DisplayName` 已進 `HostDto` 但主機頁 UI 尚未顯示；`Unmerge` 的 API 已就緒但畫面按鈕在步驟 3。

### 步驟 3 實作紀錄（2026-07-21 完成）

建置零警告、**460 單元測試**（新增 43 個）、76 項 `--selftest` 通過，並**實際啟動 Web 端到端驗證**。

**Core（規則放這裡，不是 Web）**
- `Models/NetiqHostList.cs` 純函數：`Listed`／`PendingAssignment`／`IpConflicts`／`Pollable`／
  `Ungrouped`／`IsValidIp`／`ParseLine`。放 Core 的理由：步驟 4 的批次要用 `Pollable` 決定
  今晚查哪些主機，Web 要用同一組規則標示「這台為什麼沒被輪巡」——各寫一份就會出現
  「畫面說會查、批次其實沒查」，而那正是本系統最不能有的失敗方式。
- `IsValidIp` 刻意比 `IPAddress.TryParse` 嚴格：後者會把 `10.1` 收成 `10.0.0.1`，
  而清單上的 IP 是實際送去 Sentinel 篩選的條件，收下的後果是這台主機永遠查無資料。
  端到端驗證時 `10.1` 確實被擋下。

**設定（決策 E）**
- Core 加 `NetIqSettings.Servers`；批次 `appsettings.json` 加 `NetIq` 區段（空陣列佔位）。
  刻意只加這一個欄位——本專案有「有設定卻沒有對應行為會誤導使用者」的前例
  （`MaxDeepDiveHostsPerRun` 因此被移除），連線帳密等要等機房 pipeline 實作時才加。
- `NetiqServerCatalog`（Web）自 DataRoot 的批次 appsettings.json 唯讀解析，依檔案時間快取。

**Web**
- `NetiqHostService`＋API：`GET netiq/overview`、`POST netiq/hosts`、`POST netiq/hosts/bulk`、
  `PUT hosts/{id}/active`。批次貼上沿用 txt 格式（`IP[,角色描述]`、`#` 註解），
  不合法的行略過但**逐行回報行號與原因**——只說「略過 N 行」等於把系統知道的事推回給人做。
- 主機頁：待辦佇列卡（待歸屬／IP 衝突／未分組，可點擊即篩選）、狀態徽章、
  來源與 Sentinel 欄、`DisplayName` 副標、批次貼上、停用／啟用、解除合併。
  徽章只標「需要人做點什麼」的狀態——反過來做的話滿畫面徽章等於沒有徽章。

**端到端驗證（實際啟動 Web，Stub 驗證登入 admin）**
Sentinel 下拉正確帶出批次設定的名單（決策 E 成立）；批次貼上 6 行 → 新增 2 台、
略過 3 行並各自列出原因（不合法 IP／批內重複／`10.1` 簡寫）、註解行忽略；
未分組佇列由 1 → 3 即時更新；NetIQ 來源與 Sentinel 正確顯示。驗證用資料已清除。

**實作期間發現並修正的缺口**
- **驗證只掛在一條寫入路徑上**：`NetiqHostService.AddHost` 驗 IP 與 Sentinel，但
  `HostAdminService.SaveHost`（一般編輯表單，寫同一份資料）完全沒驗——從編輯表單就能
  繞過去存進不合格的值。已補上同一組驗證，並補 `HostAdminServiceTests`
  （這個 Service 先前完全沒有測試，所以連建構子改動都沒被抓到）。
- 順帶補上步驟 1、2 加入但一直沒有測試的守則：Merge 擋墓碑目標、擋已併入來源、
  `Unmerge` 對未合併主機報錯。

**尚未接線（步驟 4、5）**：`Pollable` 尚無呼叫端（機房 pipeline 未實作）；
配對的「線索區」（同 IP／同 DisplayName 候選並列）與併入預覽尚未做，
目前合併仍走既有的 API；`TouchNetiq` 待步驟 5 接線。

### 步驟 4 實作紀錄（2026-07-21 完成）

建置零警告、**473 單元測試**（新增 13 個）、76 項 `--selftest` 通過，並**實際執行 CLI 端到端驗證**。

**新增**
- `Service/HostListProviders.cs`：`IHostListProvider`＋`TxtHostListProvider`／`StoreHostListProvider`，
  輸出 `HostListResult`（依 Sentinel 分組的 `NetiqTarget(HostId, Ip, RoleDesc)`＋警告＋來源可用旗標）。
- `Service/NetiqTxtImporter.cs`：txt → 主機清單的單向覆寫同步。
- `Service/HostListCli.cs`：`--import-hosts`（Txt 模式專用）與 `--host-list`（兩模式皆可）。
- 設定 `NetIq.HostListSource`（預設 `Txt`）與 `NetIq.HostListDirectory`（預設 `hosts`）。

**關鍵設計：兩模式共用挑選尾段**
Txt 模式 = 「先以 txt 覆寫主機清單」＋ Web 模式完全相同的挑選邏輯（`HostListSelection.FromStore`）。
不是各寫一份挑選規則——這讓「換個來源、選出來的主機卻不一樣」在結構上不可能發生，
也就是步驟 4 驗收閘門的內容（對照測試逐一比對 Sentinel／HostId／IP／角色描述）。

**三個安全設計（都有測試釘住）**
1. **Web 模式下 `--import-hosts` 直接拒絕執行**：清單交接給 Web 之後再匯入 txt，會把 Web 上
   新增的主機當成「已從清單移除」而停用。這正是「同一時間只有一個主人」要防的事故，
   擋在程式裡而不是靠人記得。
2. **某台 Sentinel 的 txt 檔消失時，不停用其轄下主機**：只對「本次真的讀到檔案」的 Sentinel
   做移除判定。誤刪或檔案伺服器沒掛上，不該讓一整個機房靜默地停止被監控。
3. **「來源不可用」與「清單是空的」分開**：目錄不存在／沒有 txt → `SourceUsable=false`，
   機房分析跳過並明確提示，不會靜默當成「今天沒有主機要查」。

**排除原因逐一列出**：待歸屬、IP 衝突被跳過的主機都進 `Warnings`，console 以黃色顯示。
沿用「沒查 ≠ 沒事」原則——靜默排除等於製造一個沒人知道的監控盲區。

**端到端驗證（實際跑 exe）**：`--import-hosts` 匯入 3 台、壞行帶行號略過；移除一行後再匯入
正確停用該台並保留主機列；Web 模式下 `--import-hosts` 拒絕執行（exit 1）、`--host-list`
正常列出；無清單目錄時明確報告來源不可用。驗證用資料與設定已全部還原。

**刻意未實作**：`--backfill-hostid`（開放問題 #1）。舊紀錄的名稱 fallback 已有測試涵蓋，
90 天保留期內舊紀錄會自然輪出；整檔重寫的風險大於收益。需要時再加。

### 開放問題（第二輪）

1. **舊紀錄回填時機**：`--backfill-hostid` 建議在步驟 1 上線、確認 fallback 正常後擇日執行；
   或乾脆永久保留 fallback 不回填（90 天後舊紀錄自然輪出）。傾向後者（少一次整檔重寫風險），
   fallback 程式碼在 DB 匯入完成後才移除。
2. **多重歸屬命中是否要自動選事件數最多的那台**：本規劃選「不自動、人工擇定」
   （與純人工綁定一致，且多重命中本身就是異常值得看一眼）。若營運後發現量大再議。
3. **`DiscoveryBatchLimit` 初值**：建議 500/晚（4 台 Sentinel × 每台約 3 次分批查詢的量級），
   Phase 1 probe 後依實測調整。

---

## 2026-07-23 — 兩千台量級擴展規劃 SCALE-2000-PLAN（原 docs/SCALE-2000-PLAN.md）

> 2026-07-23 定案，同日擴寫為細部設計版（v2）。
> 範圍：NetIQ 主動探索匯入、負責人員匯入、網段綁定群組、兩千台量級的 Web 呈現調整、
> AI 介入（W1＋W2）。
> 已定決策：SQL 後端納入本輪（SQL Server）；負責人匯入帳號不存在時自動建立；
> AI 範圍 W1＋W2（自然語言轉篩選列實驗性、不在本輪）。
> 明確排除：WEB-SPEC 總體檢列出的 P3 規格債（主機詳情補區塊等），另行處理。
>
> **實作狀態（2026-07-23）：Phase A/B/C/D/E 全部完成**，分支 `bugfix-ui-adjustments`
> （未併回主線）。707 單元測試綠。實作與過程摘要見本檔「2026-07-21 — WEB-SPEC §14」對應段落
> （原 WEB-SPEC.md §14「SCALE-2000 施工」）。
> 施工中一併修正：批次設定檔存在但解析失敗改為 fail-fast（見 docs/WEB-SPEC.md §5）。
> 本文件為**規劃定案版**，以下各節即最終實作的依據；與程式碼的落點註解交叉對照。

### 0. 為什麼 SQL 後端是前提

`history.txt` 在 2000 台 × 90 天 ≈ 18 萬行（估 0.5～2GB），Web 每次查詢全檔重讀＋
記憶體篩選——單機情境的刻意簡化（WEB-SPEC §10.4），兩千台下每頁卡數秒到數十秒。
架構已鋪路：`IAnalysisRecordQuery` 等介面與合約測試就緒（DB-PLAN），SQL 實作繼承
同一組合約案例即可保證語意逐位一致。**量級 UI 調整（§5）都建立在它之上。**

### 1. NetIQ 主動探索匯入（Phase B）

#### 1.1 設定契約（批次 appsettings.json，唯一事實來源、Web 唯讀）

```jsonc
"NetIq": {
  "Servers": [
    {
      "Name": "SENTINEL-A",
      "BaseUrl": "https://sentinel-a.corp.local",
      "Username": "svc-lfquery",          // 新增
      "Password": ""                       // 新增；正式環境以環境變數覆寫：
    }                                      //   NetIq__Servers__0__Password
  ],
  ...
}
```

- `SentinelServer` 類別（Core/Configuration/AppSettings.cs）加 `Username`/`Password` 欄。
- 帳密缺漏時：該 Sentinel 的「掃描」鈕停用並附提示（設定不完整），不擋其他 Sentinel。
- 密碼絕不回傳前端：Web 端 API 只回「此 Sentinel 可否掃描」布林。

#### 1.2 探索介面（Core，環境隔離）

```csharp
public record NetiqDiscoveredHost(string HostName, string IpAddress);

public interface INetiqDirectoryClient
{
    /// <summary>列出該 Sentinel 管理的全部主機。連線/認證失敗擲 NetiqDiscoveryException（訊息可示人）。</summary>
    Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer server, CancellationToken ct);
}
```

- 實作一：`SentinelRestDirectoryClient`——Sentinel REST API 真連線。認證方式與端點
  屬環境細節（不同版本 Sentinel API 不同），**待真實環境驗證前不定案**，先以
  基本驗證＋可設定端點路徑實作，錯誤訊息完整落 log。
- 實作二：`StubNetiqDirectoryClient`——Development 環境注入，回傳固定示範資料
  （三個 /24 網段、約 60 台、其中數台與既有主機重複），整條 UI 流程可離線開發與驗收。
- DI：`ServiceCollectionExtensions` 依環境切換（Development→Stub，其餘→Rest）。

#### 1.3 API（Maintain 能力）

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `/api/admin/netiq/servers` | Sentinel 清單＋各自可否掃描（帳密齊備） |
| POST | `/api/admin/netiq/scan` | `{ server }` → 掃描並回網段分組結果（見下），結果同時暫存 token（30 分鐘，同 ImportService 模式） |
| POST | `/api/admin/netiq/import` | `{ token, selectedIps: [] }` → 匯入勾選主機 |

掃描回應：

```jsonc
{
  "token": "…",
  "subnets": [
    {
      "cidr": "10.1.2.0/24",
      "totalCount": 37,
      "existingCount": 5,        // 已登錄（HostName 比對，不分大小寫）
      "hosts": [ { "hostName": "SRV-A", "ipAddress": "10.1.2.11", "exists": true }, … ]
    }
  ]
}
```

#### 1.4 UI（主機頁「從 NetIQ 匯入」精靈，三步）

1. 選 Sentinel（不可掃描的顯示原因）→ 掃描（loading 明示「正在向 Sentinel 查詢」）。
2. 網段核取清單：每列 `10.1.2.0/24（37 台，5 台已登錄）`，勾網段＝勾整段；
   可展開逐台調整。逐台分三類（§1.7 的重疊比對在此兌現）：
   - **新主機**：預設勾選；
   - **已存在（使用中）**：預設不勾（再勾＝更新該台的 DisplayName/Sentinel 歸屬）；
   - **與停用主機重疊**（帶 `OrphanedFromSentinel` 標記且 IP 一致）：獨立分類醒目顯示
     「原屬 {舊 Sentinel}，因移除而停用」，**預設勾選**——勾選匯入＝原列復活
     （同 HostId，歷史/群組/負責人零斷裂），非新增一列。
3. 預覽（新增 N 台、更新 M 台、**重新啟用 R 台**）→ 套用 → 結果摘要＋稽核。

#### 1.5 寫入語意

沿用 `NetiqHostService` Upsert：`HostName=IP`（NetIQ 來源慣例）、`DisplayName=掃描到的主機名`、
`Source='netiq'`、IP 衝突軟處理、既有 GroupIds/OwnerUserIds/RoleDesc 保留。
重新啟用（重疊類）額外做：`Active=true`、`NetiqServer=新 Sentinel`、
`OrphanedFromSentinel=null`。

#### 1.6 邊界與測試

- 掃描逾時（30 秒）→ 明確錯誤，不留半套狀態（掃描是唯讀操作）。
- 同 IP 重複出現在掃描結果 → 保留第一筆並列入「略過」清單。
- 測試：Stub 走全流程（掃描→勾選→匯入→稽核）；網段分組純函數單元測試；
  已存在主機更新不洗掉群組/負責人（釘既有 Upsert 慣例）；
  重疊復活保留 HostId 與全部關聯（歷史、群組、負責人、處理狀態）。

#### 1.7 Sentinel 生命週期：移除與汰換

需求：Sentinel 自設定移除 → 停用其所屬主機；移除舊＋加入新（汰換）→ 停用後，
新 Sentinel 掃描結果與停用主機重疊者，重新綁定到新 Sentinel。

**模型增補**：`WebHost` 加 `OrphanedFromSentinel`（string?，預設 null）——
「這台因為哪台 Sentinel 被移除而遭系統停用」。要獨立欄位而不是從
「Active=false ＋ NetiqServer 不在設定中」推導，是為了把**系統停用**與
**管理員手動停用**分開：手動停用代表人已表態不要這台，汰換 Sentinel 時
不得替人反悔自動復活；只有帶此標記的主機才進 §1.4 的「重疊」分類。

**孤兒偵測與停用（批次啟動時）**：

- 位置：批次啟動流程、主機登記（Touch）之前，新增 `NetiqOrphanSweeper`：
  掃 `Source='netiq' && Active && NetiqServer 有值` 的主機，其 `NetiqServer`
  不在當前 `NetIq.Servers[]` 名單 → `Active=false`、`OrphanedFromSentinel=舊名`。
- 為什麼放批次而不是 Web：設定檔的主人是批次（唯一事實來源既有決策），
  且不停用的後果正是批次面的——這些主機永遠不會被任何一輪查詢帶到，
  變成「看起來在監控、實際沒人看」的靜默黑洞（README「沒告警 ≠ 沒問題」
  在主機生命週期上的版本）。停用讓狀態誠實：未回報卡與主機頁看得見。
- 稽核：系統帳號一筆彙總（「Sentinel 'X' 已自設定移除，停用所屬主機 N 台」＋逐台明細
  進 detail），批次 console/log 同步 WARN。
- **安全欄杆（防設定檔誤刪）**：`Servers[]` 為空但存在使用中的 netiq 主機時，
  **跳過**孤兒處理並記 ERROR（「Sentinel 名單為空但有 N 台 NetIQ 主機，疑似設定檔
  損毀，未執行自動停用」）——整段被註解/檔案壞掉不該演變成全站停用。
  單一 Sentinel 移除（名單非空）照常處理。

**Web 呈現**：

- 主機頁 banner：存在 `OrphanedFromSentinel` 主機時顯示
  「N 台主機因 Sentinel 移除而停用——若已架設新 Sentinel，請用『從 NetIQ 匯入』
  重新綁定」，點擊帶篩選進主機清單。
- `NetiqOverviewDto` 加 `OrphanedCount`（與既有 PendingAssignment/IpConflict 並列）。

**重疊比對規則（汰換的第二段）**：

- 主鍵比對：新掃描結果的 IP 與孤兒主機的 `HostName`（NetIQ 來源即 IP）完全一致
  → 進 §1.4「重疊」分類，預設勾選，匯入即復活重綁。
  這與既有 Upsert 的自然鍵語意（FindByName(ip)）完全一致，不引入新比對機制。
- **DisplayName 相同但 IP 不同**（機器搬到新 Sentinel 順便換了 IP）：
  只列入精靈的「可能是同一台」提示區，**不自動勾選、不自動綁定**——
  名稱比對自動綁定違反「主機識別採純人工綁定」的既有定案（2026-07-21），
  由使用者自行判斷後走既有的人工合併流程。
- 手動路徑不受影響：主機頁既有的啟用/停用照常可用；手動重新啟用一台
  孤兒主機時一併清除 `OrphanedFromSentinel`（人已表態，標記使命結束）。

**邊界與測試**：

- Sentinel 改名（設定中移除舊名＋加入新名）：效果同汰換——舊名主機停用、
  新名掃描重疊全中、精靈一次復活。SOP 寫進 README 設定說明。
- 同時移除多台 Sentinel：逐台分組稽核。
- 手動停用在先、Sentinel 移除在後：該主機已 Active=false，sweeper 不碰
  （不覆蓋 OrphanedFromSentinel＝null 的手動語意），掃描重疊時列「已存在（停用中）」
  不自動勾。
- 測試：sweeper 停用範圍正確（不碰 local 來源/已停用/待歸屬 null 者——
  待歸屬主機沒有所屬 Sentinel，移除任何 Sentinel 都不影響它們）；
  空名單欄杆；復活流程全關聯保留；手動停用不被自動復活。

### 2. 負責人員匯入（Phase A）

#### 2.1 CSV 契約（`ImportKind.Owners`，owners.csv）

```
host_name,ip_address,owner_account
SRV-OO-WEB01,10.1.2.11,DOMAIN\user1
SRV-OO-WEB01,10.1.2.11,DOMAIN\user2     ← 同主機多列＝多位負責人
,10.2.3.21,DOMAIN\user3                 ← host_name 空白時以 IP 比對
```

- RequiredHeaders：`owner_account` ＋（`host_name` 或 `ip_address` 至少一欄有值，逐列驗證）。
- 進既有 CSV 匯入頁（模板下載/預覽/套用/稽核全繼承 ImportService 框架）。

#### 2.2 比對與寫入規則

1. 先以 `host_name` 找主機（不分大小寫）；查無且 `ip_address` 有值 → 以 IpAddress 欄比對
   （多台同 IP → 錯誤列：「IP 對應多台主機，請改用 host_name」）。
2. 兩欄都給且指向不同主機 → 錯誤列（交叉驗證不一致）。
3. 查無主機 → 錯誤列（主機不自動建立——主機的建立途徑是批次 Touch / NetIQ 匯入 /
   hosts.csv，負責人檔不該成為第四條建立途徑）。
4. **帳號不存在 → 自動建立**（DisplayName=帳號、User 角色、無使用者群組、Active）。
   預覽以獨立區塊明列「將新增 N 個帳號」。與 hosts.csv 的「擋下」刻意不同：
   兩千台情境手動先建帳號不現實，且帳號真偽在 LDAP 模式登入時自然驗證。
5. 取代語意：檔案中出現的主機，其 OwnerUserIds **整組取代**為檔案內容；
   未出現的主機不動。同列重複帳號去重。

#### 2.3 測試

多負責人聚合、IP fallback、交叉驗證不一致、自動建帳號（含重跑冪等）、
取代不累加、未出現主機不動。

### 3. 網段綁定主機群組（Phase A）

#### 3.1 `CidrMatcher`（Core 純函數）

```csharp
public static class CidrMatcher
{
    /// <summary>解析 "10.1.2.0/24"、"10.1.2.*"、"10.1.2.15"；非法格式回 null（呼叫端轉驗證錯誤）</summary>
    public static CidrRange? Parse(string pattern);
    public static bool Matches(CidrRange range, string ipAddress);
}
```

- 支援：CIDR（/8～/32）、萬用字元（`10.1.*`＝後綴全部）、單一 IP。IPv4 only（環境無 v6 需求）。
- 單元測試釘邊界：網段界線（.0/.255）、/31//32、前導零拒收、萬用字元位置限制（只允許尾端連續段）。

#### 3.2 API（Maintain 能力）

| 方法 | 路徑 | 說明 |
|---|---|---|
| POST | `/api/admin/host-groups/{id}/members/preview` | `{ pattern }`（網段）或 `{ query }`（hostname/IP 關鍵字）→ 命中主機清單（含現有群組、是否已在本群組） |
| POST | `/api/admin/host-groups/{id}/members` | `{ hostIds: [], removeFromOthers: bool }` → 套用＋稽核 |

預覽回應每列：`{ hostId, hostName, ipAddress, currentGroups: [], alreadyInTarget }`。

#### 3.3 UI（群組管理頁「批次加入成員」）

- 模式切換：網段 / 搜尋，共用同一張預覽表。
- **已屬其他群組 → 顯性通知**：該列「現有群組」欄以主色徽章列出，表頭統計
  「N 台已屬其他群組」。預設仍勾選（GroupIds 本允許多重），可逐台取消；
  「同時移出原群組」為明確的 checkbox 選項（預設關）。
- `alreadyInTarget` 列顯示「已在本群組」且不可勾。
- 比對範圍同時涵蓋 HostName 與 IpAddress 欄（NetIQ 主機 HostName 即 IP，本機主機兩欄不同）。

#### 3.4 測試

CidrMatcher 邊界全套；preview/apply 一致性；removeFromOthers 只動預覽中勾選的主機；
墓碑列（MergedInto != null）排除在命中結果外。

### 4. SQL Server 後端（Phase C，工程最大）

#### 4.1 範圍

- EF Core ＋ SQL Server provider；schema 依 docs/DB-PLAN.md 既有定案
  （lf_daily_records / lf_top_issues / lf_record_categories / lf_record_alerts /
  lf_deep_dive_analyses / lf_weekly_checkups / lf_hosts / lf_users / lf_reports /
  lf_permission_changes / lf_record_handling(+log) …）。
- **schema 增補**：`lf_issue_handling`（host_name, date, issue_key, status, actor_id,
  actor_account, note, updated_at；PK＝前三欄）——對應本輪新增的 IssueHandling 模型。
- 各 store 新增 EF 實作、StorageFactory 加 "SqlServer" case；Service 層零修改。
- 寫入路徑以 `CategoryAggregator` 填 lf_record_categories（DB-PLAN 一致性機制 #4/#5）。
- `--import-history`：history.txt＋webdata 一次性匯入器，自然鍵冪等、可重跑、
  分批交易（每 1000 筆一批，中斷可續）。

#### 4.2 驗收

- 合約測試同一組案例跑雙後端全綠（尤其 Hosts 空集合＝零結果的授權語意）。
- 2000 台 × 90 天種子資料：儀表板/問題查詢/報表 P95 < 1 秒。
- JSONL 模式行為零改變（既有部署不受影響）。

### 5. Web 呈現調整（Phase D，依賴 C）——2026-07-23 重新規劃

Phase D 擴充為五個工作包：D-0 視覺基盤 → D-1 風險日詳情改版 → D-2 清單頁快速篩選
→ D-3 NetIQ 匯入排程化 → D-4 量級調整（原 Phase D 內容）。
D-0 先行是因為 D-1/D-2 的新元件都要長在統一的樣式系統上，避免先做完再整批重刷。

#### 5.0 D-0 視覺基盤（設計代幣＋統一篩選工具列）

現況問題：搜尋與快速篩選的排版各頁各長一樣、間距配色零散（「像學生作業」），
整體配色單薄缺乏專業感。對策是**先建立小而完整的設計系統，再讓各頁遷入**：

- `site.css` 頂部集中 CSS 變數（設計代幣）：主色/輔色/危險/警告的色階、
  灰階（文字三級：主文/次要/提示）、間距刻度（4/8/12/16/24）、圓角、陰影兩級、
  表格列 hover 色。既有零散色值全部改引用代幣——之後調整配色只動一處。
- 配色方向：低飽和專業色系（深藍灰主色＋單一強調色），風險色（高/中/低）維持
  紅/橙/灰語意但統一色階；卡片加細邊框＋輕陰影、表頭底色、列 hover——資訊密度高的
  監控工具風格，不是行銷網站風格。
- 新增共用元件 `lf-toolbar`（core/ui.js）：**一列式篩選工具列**＝搜尋框＋chip 群組
  （單選/多選）＋排序下拉＋「清除全部」，統一間距與換行行為。問題查詢、規則維護、
  主機、使用者、稽核各頁全部改掛這個元件——排版一致性靠共用元件保證，不靠各頁自律。
- 驗收：各頁篩選區視覺一致；配色代幣化後全站無寫死色值（grep 驗證）。

#### 5.1 D-1 風險日詳情改版

| # | 調整 | 細節 |
|---|---|---|
| 1 | 報告全文預設收合 | `report-card` 預設收合只留標題列（含展開鈕＋複製鈕）；點標題展開。展開狀態記 localStorage（常看全文的人不必每次點） |
| 2 | 低風險預設「不處理（預設）」 | **推導不落盤**：Low 且無明確標記的問題，顯示灰色「不處理（預設）」徽章。使用者可一鍵「確認不處理」（落盤 wont_fix）或「調回未處理」（落盤明確 open 覆蓋預設）。issue-level 狀態值新增 `open`＝明確未處理 |
| 3 | 已知雜訊記憶 | 新增 webdata store `noise_marks`（主機＋Source＋EventId → 標記人/時間/備註；Blob 抽象，SQL 模式自動走 DB）。標「已知雜訊」時寫入；之後同主機同簽章的新問題**自動顯示「已知雜訊（自動）」**（推導，不落盤）。可一鍵「調回未處理」（落盤 open 覆蓋＋詢問是否刪除記憶）。與既有規則抑制提議並存：有 ruleId 走抑制（治本），無 ruleId 靠記憶（治標） |
| 4 | 類別標題列加底色 | `lf-issue-group__header` 加依最高嚴重度的淡色底（紅/橙/灰階），一眼區分分節；用 D-0 代幣 |
| 5 | 趨勢欄與文字整理 | `BuildTrendText`：Trend=New（首次出現）時**不輸出「前一日 0 次」**（矛盾資訊）；欄位文字加 `text-wrap` 與最大寬度適度換行。範例訊息由展開式 `<pre>` 改 **hover 泡泡**（popover：滑過顯示、點擊釘住可複製、再點關閉），列面只留「範例訊息」小圖示 |
| 6 | 處理欄改勾選＋細節側欄 | 下拉改 **checkbox**：勾選＝快速標「已處理」；勾選後右側浮出小面板選具體狀態（已處理/不處理/誤報/已知雜訊 chip），**依狀態動態調整欄位**：已處理→處理說明（選填）；不處理→原因（必填）；誤報→備註＋（可維護規則時）提議調整規則；已知雜訊→備註＋寫入記憶＋（有 ruleId 時）抑制提議。取消勾選＝清除標記。API 沿用 `PUT …/handling/issues`，request 加 `note` 欄位 |
| 7 | 計數器改「已處理／未處理」 | `detail-progress` 改為「已處理 X／未處理 Y」：X＝resolved，Y＝無標記（且非預設不處理/自動雜訊）；不處理/誤報/已知雜訊/預設類**兩邊都不計**——計數器回答「還剩幾件要動手」，不是「標了幾件」 |

#### 5.2 D-2 清單頁快速篩選與排序（規則維護／主機／使用者）

- 三頁全部掛 D-0 的 `lf-toolbar`：
  - **規則維護**：chip＝類別/嚴重度/來源（內建/自訂）/啟用狀態/有無抑制；排序＝嚴重度/類別/命中次數
  - **主機**：chip＝來源/Sentinel/群組/啟用/未回報；排序＝名稱/最後回報/風險日數（與 5.4 分頁整合）
  - **使用者**：chip＝群組/啟用/角色；排序＝帳號/最後登入
- chip 篩選為前端即時（已載入資料集內過濾）；主機頁資料量大，chip 改為帶進伺服器端查詢參數。

#### 5.3 D-3 NetIQ 匯入排程化（Web 觸發、批次載入）⏸ 已廢止（2026-07-24）

> **廢止**：本節的排程佇列設計已於 docs/NETIQ-WEB-CONFIG-PLAN.md 定案 7 推翻，改回
> 「Web 掃描/勾選後立即落盤」（`netiq_import_queue` store、`--apply-netiq-imports`、
> 排入/取消的稽核事件皆已刪除）。前提變化：當時的顧慮是「兩千台量級下主機異動集中在
> 批次時段一次落盤，避免上班時間 Web 端操作與正在跑的批次互踩」，但實測與重新評估後
> 認為這一步本身很輕量（純粹是幾十到幾百列的 upsert），真正重的規則檢查與趨勢分析
> 本來就要等下次批次——排程化增加的中間狀態（pending/applied/failed／取消）與
> UI 複雜度，換來的即時性防護在這個量級下不成比例。以下維持原文供歷史對照。

現況（**歷史記錄，已不適用**）：Web 掃描後直接 `Import()` 立即落盤。改為**排程佇列**模式：

- Web 掃描/預覽流程不變；「套用」改為「**排入匯入**」——寫入新 webdata store
  `netiq_import_queue`（Blob 抽象）：請求內容＝掃描結果快照＋操作人＋排入時間＋狀態
  （pending/applied/failed）。
- 實際載入由**批次**執行：每日批次開頭處理佇列（依 §1.7 Sentinel 生命週期規則落盤主機
  異動＋寫匯入紀錄＋稽核），或手動 `LogForesight.exe --apply-netiq-imports` 立即套用。
- Web 匯入頁顯示佇列狀態：排程中（可取消）/已套用（含結果數字）/失敗（含原因）。
- 理由：兩千台量級下主機異動集中在批次時段一次落盤，避免上班時間 Web 端大量主機
  停用/啟用與正在跑的批次互相踩踏；也符合「批次是資料主要寫入者」的職責劃分。

#### 5.4 D-4 量級調整（原 Phase D 內容，維持不變）

| 區域 | 調整 | 細節 |
|---|---|---|
| 儀表板 | 未回報主機改**計數卡＋下鑽** | 卡片「N 台超過 2 天未回報」→ 點入主機頁（未回報篩選預置）；不再整表渲染 |
| 儀表板 | 新增**依群組風險概況** | 每群組一列：主機數/高風險日/中風險日/未處理數，點列 → 問題查詢帶群組篩選。兩千台的主要動線是「先群組後下鑽」 |
| 問題查詢 | 主機篩選改**搜尋式 autocomplete** | 輸入 2 字元後查 `/api/hosts?query=`（伺服器端前綴比對、上限 20 筆）；已選主機顯示為可移除 chip |
| 問題查詢 | 篩選列加**主機群組 chip** | 後端 `RecordSearchRequest` 加 `GroupIds`，展開為主機集合後交集可見範圍 |
| 主機管理 | 伺服器分頁＋搜尋＋篩選 | 篩選：來源/Sentinel/群組/啟用/未回報；預設每頁 50（與 5.2 主機 toolbar 同一次施工） |
| 執行監控 | 矩陣改彙總 | 每日一列：成功/失敗/未跑計數＋失敗主機名（上限 10 台＋「其他 N 台」）；點日期 → 該日異常清單 |
| 全站 | 清單 API 一律分頁 | pageSize clamp 既有（≤200），新端點一體適用 |

#### 5.5 Phase D 測試重點

- 低風險預設推導：Low 無標記→顯示預設不處理且**不落盤**；確認→落盤 wont_fix；調回→落盤 open。
- 雜訊記憶：標記後同主機同簽章新問題自動顯示；明確 open 覆蓋自動；刪記憶後不再自動。
- 計數器語意：resolved 計已處理；預設不處理/自動雜訊/誤報等不進未處理。
- BuildTrendText：New 不帶前一日次數；Recurring 照舊。
- NetIQ 佇列：排入→pending；批次套用→applied＋主機異動＋稽核；取消→移除；失敗→failed＋原因。
- toolbar 元件：chip 多選/單選/清除、排序切換的行為單測（DOM 層以既有測試模式驗證）。

### 6. AI 介入（Phase E，W1＋W2）

原則：**程式能確定性算的不交給 AI**；AI 只做「幫人看懂、幫人排序」。
輸入一律是已彙總的結構化統計（prompt 小），輸出短（≤200 tokens），
koboldcpp no-thinking 下實測目標 3~5 秒。

#### 6.1 基礎建設

- `AIService` 自批次專案搬至 Core（namespace 不變 `LogForesight`，批次側零修改；
  Core 需加 Polly 套件參照，NLog 已有）。既有的單一請求佇列（SemaphoreSlim(1,1)）
  正好保護單卡 GPU：Web 與批次各自行程各自排隊，時段天然錯開（批次凌晨、Web 日間）。
- AI 位址設定沿用「批次 appsettings 唯一事實來源、Web 唯讀」模式（同 Sentinel 名單）。
- Web 端獨立參數：逾時 10 秒（批次 600 秒不適用互動情境）、MaxTokens 256。
- 快取：`webdata/ai_cache.json`（`JsonCollectionFile` 基底；鍵＝功能＋日期＋輸入雜湊，
  值＝AI 輸出＋產生時間；啟動時清 7 天前舊項）。SQL 階段轉表。
- 失敗行為鐵律:任何 AI 失敗都靜默降級——卡片隱藏、按鈕恢復、頁面功能不受影響。
- 安全：AI 輸出永遠 `textContent` 呈現；AI 回傳的下鑽參數必須通過白名單驗證
  （類別/風險層級/日期格式）才組連結，驗不過就只顯示文字不給連結。

#### 6.2 W1-1 儀表板「今日焦點」

- 輸入：本期彙總 DTO（風險日數、分類統計、主機排行前 5、關聯訊號清單）——全是現成資料。
- Prompt 契約：回 JSON `{"items":[{"text":"…","link":{"categories":"…","riskLevels":"…"}}]}`，
  最多 3 條、每條 ≤ 60 字。
- 呈現：儀表板頂部卡「AI 今日焦點」；快取鍵＝日期＋輸入雜湊（資料沒變不重算，
  同日多人瀏覽只有第一人觸發呼叫）；載入中顯示骨架、逾時整卡消失。

#### 6.3 W1-2 查詢結果 AI 歸納

- 前置（確定性、不靠 AI）：後端對目前查詢結果做跨主機同簽章聚類
  （Source+EventId 分組 → 主機數、總次數、日期範圍），取前 5 組。
- AI 只做最後一哩：把聚類結果講成 ≤ 3 句白話（「7 台主機同日出現 disk 153，疑似共通儲存設備」）。
- 觸發：結果列上方「AI 歸納」按鈕（使用者主動點，不自動呼叫——查詢頁高頻，
  自動呼叫會把 AI 佇列塞滿）；同查詢條件雜湊快取。

#### 6.4 W2 詳情頁快速判讀

- 觸發：問題列展開面板內「AI 判讀」按鈕（僅未命中規則的「其他」類別顯示——
  規則命中的已有靜態知識庫，重複給 AI 講一次是浪費）。
- 輸入：該問題簽章＋趨勢欄位＋當日關聯訊號＋範例訊息（截 500 字元）。
- 輸出：兩句話（「要不要緊」＋「先做什麼」），≤ 100 字；快取鍵＝主機＋日期＋issue_key。

#### 6.5 測試

- AIService 搬遷後批次測試全綠（僅組件搬移）。
- Web AI 各功能：AI 成功/逾時/回傳非 JSON 三態的 UI 行為（成功渲染、其餘靜默）；
  快取命中不發第二次請求；下鑽參數白名單驗證（惡意參數只顯示文字）。
- koboldcpp 實測：no-thinking 下三個功能各自 < 5 秒（超標就砍輸入篇幅，不放寬逾時）。

### 7. 施工順序與相依（✅ 全部完成 2026-07-23）

```
Phase A（可並行，不依賴 SQL）：負責人匯入 ＋ 網段綁定群組              ✅
Phase B：NetIQ 探索匯入（Stub 先行，真連線待 Sentinel 環境）          ✅
Phase E：AI 基礎建設 → W1 → W2（不依賴 SQL，可提前）                 ✅
Phase C：SQL 後端（三 provider Jsonl/Sqlite/SqlServer，合約測試護航）  ✅
Phase D：D-0 視覺基盤 → D-1 詳情改版 → D-2 清單篩選 → D-3 NetIQ 佇列 → D-4 量級調整（依賴 C）  ✅
```

### 8. 統一驗收

- 匯入類（A/B）全走 Preview→Apply；預覽數字與套用結果一致；稽核完整。
- Phase C：合約測試雙後端全綠；2000 台種子資料主要頁面 P95 < 1 秒。
- Phase E：AI 掛掉時所有頁面功能不受影響（純加值層）；快取命中零 AI 呼叫。
- 批次端相依提醒（不在 Web 範圍）：取數走 NetIQ 遠端 pipeline（本檔「2026-07-20 — LogForesight 擴充規劃」段）；
  AI 每日總覽 no-thinking 3~5 秒/台 × 2000 ≈ 2~3 小時，「低風險日不呼叫 AI」
  既有策略是主要減量手段，必要時再加並行度控制。

---

## 2026-07-24 — NetIQ Web 維護、群組功能擴充與 Jsonl 後端退役規劃（原 docs/NETIQ-WEB-CONFIG-PLAN.md）

> 規劃日期:2026-07-24。本文件收整四項需求的討論定案與六個實作 Phase:
> (1) Sentinel(NetIQ)連線設定改由 Web 維護,含「新增即掃描匯入」精靈;
> (2) 使用者群組編輯時可勾選可見主機群組(授權矩陣的初步設定入口);
> (3) 主機群組成員檢視/移除 modal;
> (4) 多對多歸屬確認(主機↔主機群組、使用者↔使用者群組——**模型層已是多對多,零改動**)。
> 並依討論擴大範圍:**Jsonl 檔案後端全面退役**(含 Txt 主機清單模式)、
> **NetIQ 匯入佇列(D-3)退役改即時落盤**。
>
> 本文件修訂了三份既有文件的決策,對照見「既有決策修訂」節;
> 各文件的修訂註記在 Phase 6 補上。

### 背景:為什麼既有決策可以改

| 既有決策 | 當時前提 | 前提的變化 |
|---|---|---|
| NETIQ-HOSTLIST-WEB-PLAN 決策 E:Sentinel 名單以批次 appsettings.json 為單一事實來源,Web 唯讀、不建管理表 | 批次與 Web 靠 DataRoot 共用**檔案**,設定檔就是共用點 | Phase C 之後預設 Sqlite、正式 SqlServer,共用點已是**資料庫**;`NetiqServerCatalog` 讀 `{DataRoot}\appsettings.json` 在 SqlServer 模式(Web 與批次可能不同機)本來就脆弱 |
| SCALE-2000-PLAN §5.3 D-3:NetIQ 匯入排入佇列、批次時段才落盤 | 防白天 Web 寫主機檔與正在跑的批次互踩(JSONL 檔案時代) | 跨程序檔案鎖已實作、SQL 後端有交易;主機列本身輕量,重的部分(規則檢查紀錄)本來就要等下次批次 |
| Storage.Type 三選一(Jsonl/Sqlite/SqlServer) | JSONL 是現行格式,SQL 是新軌 | Sqlite 已是預設與主測試路徑、正式環境 SqlServer;Jsonl 相容模式沒有服役對象,新功能還要多寫一份檔案實作 |
| 決策 D:Txt 主機清單模式(HostListSource=Txt)為交接過渡 | 清單主人從 txt 交接到 Web 需要過渡期 | Sentinel 連線設定都進 Web 之後,「主人在 txt」的定位消失;txt 內容用 Web 批次貼上即可匯入 |

### 定案彙整(2026-07-24,全部經使用者確認)

| # | 決策 | 內容 |
|---|---|---|
| 1 | Sentinel 改 Web 維護 | 名稱/BaseUrl/帳密存共用儲存層,Web CRUD(admin、稽核);批次與 Web 都改讀 store;`appsettings.NetIq.Servers` 只剩一次性種子用途 |
| 2 | Sentinel 存法 | **lf_blobs 文件(key=`sentinels`)**,不建真表——與其他 webdata store 同模式,名稱唯一性在 store 邏輯驗,**零 DDL** |
| 3 | 密碼保存 | Core 共用加解密 Helper(AES,金鑰內嵌程式,密文 `enc:v1:` 前綴)。防翻 DB、不防取得程式的人,邊界誠實註明。前端 write-only:已設定僅顯示「已設定」,留空=不變;稽核不記密碼 |
| 4 | 主機參照 Sentinel | `WebHost` 加 `SentinelId`(HostId 的前例:PK 參照,改名不斷鏈);`NetiqServer` 字串降為顯示快照;啟動時依名稱一次性回填 |
| 5 | Sentinel 刪除/停用 | 刪除=確認視窗明示「轄下 N 台將停用並標記孤兒」,走既有 `OrphanedFromSentinel` 流程;另提供「停用」(暫停輪巡、主機不動)作過渡選項 |
| 6 | 新增精靈 | 「自動掃描匯入」checkbox 預設勾;**掃描即帳密驗證**(對 Sentinel 唯一會用的 API 就是列主機);掃描成功**當下建立 Sentinel**,精靈中途取消=Sentinel 留著、主機沒匯 |
| 7 | 匯入即時落盤 | D-3 佇列退役;精靈「匯入」直接套用主機異動,結果記入匯入紀錄;儀表板要有內容仍等下次批次(使用者已知悉) |
| 8 | 匯入時群組指派 | 各網段選既有群組/建新群組/跳過;**新群組送出當下即建立**(空群組無害,可先設授權);跳過=未分組=僅 admin 可見(畫面提示);**既有主機的群組一律不動**(匯入不是隱性改權限) |
| 9 | 無回報告警豁免 | 新主機 `CreatedAt` 未滿一個批次週期不列入「無回報主機」告警,避免整批匯入即告警洪水 |
| 10 | Jsonl 後端退役 | 刪除 `Storage.Type="Jsonl"` 全部檔案實作;**不做 Jsonl→SQL 遷移工具**(無服役中的 Jsonl 正式資料);設成 Jsonl→啟動明確報錯 |
| 11 | lf_blobs 不正規化 | 「JSON 文件存 DB 一列」維持現狀;正規化觸發點見「未來觀察點」 |
| 12 | Txt 清單模式退役 | `HostListSource`/`HostListDirectory` 設定、`TxtHostListProvider`、`--import-hosts` 一併刪除 |
| 13 | Schema 升級機制 | 本輪零 DDL,**不建機制**;方針(未來採自製冪等 DDL,不用 EF Migrations)寫入 DB-PLAN |
| 14 | 掃描全部 Sentinel | 不做(新增即掃已涵蓋初次接入;日常巡檢需求出現再議) |
| 15 | 使用者群組勾主機群組 | 僅 `Role=User` 群組顯示(WEB-SPEC 決策 #13 不變);寫入沿用 `PUT /api/admin/access/{userGroupId}`,與授權矩陣同一條路 |
| 16 | 主機群組成員 modal | 「目前成員」(依 /24 分組、勾選移除、未分組警示)+「加入成員」(既有流程)兩頁籤 |
| 17 | 左側選單 | 「CSV 匯入」→「資料匯入」,NetIQ 匯入/Sentinel 管理/匯入紀錄整併入內;主機頁保留捷徑 |

#### 既有決策修訂對照

- **NETIQ-HOSTLIST-WEB-PLAN 決策 E** → 修訂:單一事實來源由「批次 appsettings.json」改為「共用儲存層(sentinels blob)」。「同一時間只有一個主人」原則不變,主人換位。
- **NETIQ-HOSTLIST-WEB-PLAN 決策 D** → 廢止:Txt 模式退役(定案 12)。
- **SCALE-2000-PLAN §5.3 D-3** → 廢止:匯入即時落盤(定案 7),前提變化見背景節。
- **DB-PLAN / WEB-SPEC §10** → Jsonl 後端退役、`--import-history` 確定不做;schema 升級方針補記。

### 已盤點的現況事實(實作前驗證過)

1. `WebHost.GroupIds` 與 `WebUser.GroupIds` 均為 `List<long>`,`GroupAccess` 多對多——**需求 4 零改動**。
2. 掃描精靈(掃描→/24 分組→勾選→佇列)已存在於主機頁;缺的是 Sentinel Web 維護、群組指派、即時落盤與搬家。
3. SQL 模式下 webdata 全部是 `lf_blobs`/`lf_log_lines` 文件,**沒有** `lf_hosts` 等真表;`SentinelId`/`CreatedAt` 只是 JSON 屬性,零 DDL。
4. `EfJsonBlobStore.Mutate` 在 SqlServer 預設隔離等級下「讀→改→寫」**擋不住更新遺失**(兩行程同讀舊值、後寫蓋先寫);SQLite 因資料庫級寫入鎖+busy 重試無此問題。檔案時代的 `.lock` 防的正是這件事,換 DB 後防線沒跟上——Phase 1 以 `UpdatedAt` 併發檢查補上。
5. `FilePermissionSnapshotStore`(permission_snapshot.json)目前**所有模式都走檔案**,屬「JSON 作為資料庫」殘留,一併收進 blob。
6. `EnsureCreated()` 只在資料庫不存在時建 schema,對既有 DB 不加表不加欄——本輪零 DDL 所以無事,但這是未來的地雷,方針記入 DB-PLAN(定案 13)。
7. 沒有任何 `--import-history` 遷移工具存在;確認不做(定案 10)。
8. 保留的檔案輸出(不屬「JSON 作為資料庫」):export\ 報告全文 txt、logs\、appsettings、Sqlite .db 本體。

### 資料模型變更

| 項目 | 變更 |
|---|---|
| `Sentinel`(新,Core) | `SentinelId`(long)、`Name`(唯一,不分大小寫)、`BaseUrl`、`Username`、`PasswordEnc`(密文)、`Active`、`CreatedAt`/`UpdatedAt` |
| `ISentinelStore`(新) | CRUD+配號;實作走 `IJsonBlobStore`(key=`sentinels`),與其他 webdata store 同模式 |
| `WebHost` | 新增 `SentinelId`(long?,null=待歸屬)、`CreatedAt`(告警豁免依據);`NetiqServer` 字串降為顯示快照(讀取端改吃 SentinelId) |
| `SentinelServer`(設定類) | 保留唯讀:種子匯入來源;`CanDiscover` 邏輯移到 `Sentinel` |
| `NetiqImportQueueEntry`/`INetiqImportQueueStore` | 刪除(佇列退役);匯入結果改記 `IImportLogStore` |
| `CryptoHelper`(新,Core) | `Encrypt`/`Decrypt`+`IsEncrypted`(`enc:v1:` 前綴);AES-256,金鑰內嵌 |
| `lf_blobs` | `UpdatedAt` 設為 EF ConcurrencyToken(更新遺失→例外→既有重試迴圈接手) |
| 設定 | 刪除 `NetIq.HostListSource`/`HostListDirectory`;`NetIq.Servers` 註解改為「僅供一次性種子匯入,維護請至 Web」;`Storage.Type` 合法值=Sqlite/SqlServer |

### 新增 Sentinel 精靈(定案流程)

1. **步驟 1:連線設定**——名稱(即時驗唯一)、BaseUrl、帳密、「自動掃描匯入」checkbox(預設勾)。
   未勾→「建立」單純存檔結束。勾選→「下一步」=以**尚未存檔**的帳密呼叫掃描(admin 專屬端點,帳密僅過境不落地):
   失敗→留在本步顯示錯誤(=帳密驗證失敗);成功→**當下建立 Sentinel**(進稽核),進步驟 2。
2. **步驟 2:勾選網段**——/24 分組,每網段顯示台數與「其中 N 台已登錄」;網段可展開至單台
   (徽章:已登錄/原屬 XX 已停用);預設全勾。
3. **步驟 3:指派群組**——各網段下拉:既有群組/建立新群組(送出當下即建)/跳過;
   跳過提示「未分組=僅 admin 可見」。整步可「跳過」。
4. **送出「匯入」**——即時套用:新增/復活主機寫 `SentinelId`、指派群組;既有主機只更新
   DisplayName/歸屬、**群組不動**;結果摘要(新增/更新/復活/略過各幾台)記入匯入紀錄+稽核。
5. 邊界:掃描結果 30 分鐘效期,逾期送出→退回步驟 2 要求重掃,已選網段盡量保留;
   精靈中途關閉→Sentinel 留存、主機未匯(之後可從 NetIQ 匯入頁籤補掃)。

### 實作步驟(六個 Phase,每個結束時建置零警告+測試全綠)

#### Phase 1:Jsonl 後端退役 ✅ 已完成(2026-07-24)

建置零警告、**654 項單元測試**全數通過、`--selftest`(99 項)通過。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 設定收斂 | `Storage.Type` 合法值 Sqlite/SqlServer(`IsValidType`);`Jsonl`/未知→啟動報錯(批次與 Web 兩邊的設定驗證) | ✅ |
| StorageFactory | `Blob`/`LogStore` 移除檔案分支與 `jsonlPath` 參數;全部 `Create*` 收斂;`CreateRecordStore`/`CreateRuleStore`/`CreateSuppressionStore` 改吃 `dataRoot` | ✅ |
| 刪除 | `FileJsonBlobStore`、`FileJsonLogStore`、`JsonlAnalysisRecordStore`、各 store 的檔案路徑便利建構子 | ✅ |
| 快照入庫 | `FilePermissionSnapshotStore` → `JsonPermissionSnapshotStore`(blob,key=`permission_snapshot`),檔案版刪除 | ✅ |
| 併發防線 | `lf_blobs.UpdatedAt` 加 `IsConcurrencyToken()`;新增 `Blob並發衝突_過期寫入被擋下` 測試,直接驗證 `DbUpdateConcurrencyException` | ✅ |
| 測試 | 刪 `JsonCollectionFileTests`、`JsonlAnalysisRecordStore*Tests`;各合約測試收斂為僅 Sqlite fixture;`SelfTestRunner` 移除讀 rules.json/suppressions.json 檔案的分支,改固定驗內建種子/不連 DB | ✅ |
| 佈線清理 | 批次 `Program.cs`、Web `ServiceCollectionExtensions.cs`/`Program.cs` 移除檔案路徑佈線;appsettings(批次/Web/Development/.example)與 README 註解改寫 | ✅ |
| **Txt 清單模式退役**(原訂 Phase 2,提前一併完成) | `TxtHostListProvider`、`NetiqTxtImporter`、`--import-hosts`、`NetIq.HostListSource`/`HostListDirectory`/`UsesWebHostList` 全數刪除;`StoreHostListProvider` 成為唯一來源;`HostListCli` 精簡為僅 `--host-list` | ✅ |

#### Phase 2:Sentinel Web 維護 ✅ 已完成(2026-07-24)

Txt 退役已提前併入 Phase 1(見上)。建置零警告、**687 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 端到端驗證(新增/編輯/停用/刪除全走過一輪,確認密碼不進 API 回應與稽核明細)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| Core | `Sentinel` 模型、`ISentinelStore`+blob 實作(key=`sentinels`)、`CryptoHelper`(AES-256,`enc:v1:` 前綴,定案 2、3) | ✅ |
| WebHost | `SentinelId`(long?)/`CreatedAt` 屬性;`SentinelIdBackfiller` 一次性回填(冪等,批次與 Web 啟動時都跑);`NetiqHostList.PendingAssignment`/`Pollable` 改吃 SentinelId,`Pollable` 新增可選的 `isSentinelActive` 排除停用 Sentinel 的主機 | ✅ |
| Web API | `SentinelAdminService`+`AdminController` 的 Sentinel CRUD(Maintain 能力、稽核、密碼 write-only);刪除直接孤兒化轄下主機(不沿用批次向的 `NetiqOrphanSweeper`——那支帶有「現存名單整個是空就安全跳過」的欄杆,會誤擋「刪除最後一台」這個合法操作);停用=暫停輪巡,主機不動 | ✅ |
| Web UI | 新頁面 `/admin/sentinels`(Sentinels.cshtml+sentinels.js,List+Modal 模式);「新增即掃描」精靈留待 Phase 4 資料匯入頁整併時做(**路由後續兩度變動**:先於本文件下方「退役」階段併入 `/admin/imports`,後於 feature/admin-settings-netiq-handling 分支再遷出為現行的 `/admin/netiq`,Sentinel CRUD 與 NetIQ 連線/節流設定同頁維護) | ✅ |
| Catalog | `NetiqServerCatalog` 改讀 `ISentinelStore`(介面不變,呼叫端零改動,密碼在此解密供探索用戶端使用);`SentinelServer` 加 `Id` 欄位 | ✅ |
| 種子 | `SentinelSeeder`:sentinels blob 為空時於 Web 啟動時自批次 `appsettings.NetIq.Servers` 一次匯入(密碼順手加密);找不到/解析失敗批次設定檔不擋啟動 | ✅ |
| 批次 | `NetiqOrphanSweeper`/`NetiqImportApplier` 改吃 SentinelId;`HostListProviders.cs` 的 `HostListSelection.FromStore` 改注入 `ISentinelStore`,分組鍵用 Sentinel 現存名稱(不是可能落後的 NetiqServer 快照),Sentinel 停用時排除並列警告 | ✅ |
| 寫入路徑 | `NetiqHostService.AddHost`/`BulkAddHosts`、`HostAdminService.SaveHost`(含依 Sentinel 篩選)一律解析 Name→SentinelId 後兩者一起寫;`SentinelAdminService` 改名時同步所有掛在該 Sentinel 下主機的 NetiqServer 顯示快照 | ✅ |

**實作期間的修正**:`SentinelAdminService.SaveSentinel` 原本在 `_sentinels.Upsert(...)` 呼叫**之後**才比較 `existing.Name` 是否變了——DB 後端的 `Read()` 每次回全新物件所以沒事,但簡單的記憶體型測試替身共用物件參考,`Upsert` 內部的變動會回頭污染 `existing` 這個先前抓到的參照,導致「改名」永遠判定為「沒改名」。改成呼叫 Upsert 前先把舊名稱存成區域變數,不依賴物件參考在多次呼叫之間保持不變——這對兩種後端都正確,而不是湊巧在其中一種上可行。

#### Phase 3:匯入佇列退役+即時匯入 ✅ 已完成(2026-07-24)

建置零警告、**687 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 以 Stub 探索用戶端走完整條端到端流程(新增 Sentinel → 掃描 60 台 → 勾選匯入 59 台 → 立即出現在主機頁 → 匯入紀錄與稽核皆正確記錄 → 刪除 Sentinel → 轄下主機正確孤兒化)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 刪除 | `INetiqImportQueueStore`/`JsonNetiqImportQueueStore`、`NetiqImportQueueEntry`/`NetiqImportQueueStatuses`、`NetiqImportQueueCli.cs`、`--apply-netiq-imports`、批次啟動時的佇列處理區塊、主機頁佇列卡與取消 UI、`NetiqQueueEntryDto`、稽核動作 `NetiqImportEnqueue`/`NetiqImportCancel` | ✅ |
| 即時套用 | `NetiqDiscoveryService.Enqueue` → `Import`:直接呼叫 `NetiqImportApplier.Apply`(簽章簡化為 `(serverName, selectedIps, hosts, sentinels)`,不再依賴佇列實體);token 用過即丟,同一次掃描不能重複套用兩次 | ✅ |
| 匯入紀錄 | 結果寫入既有的共用 `IImportLogStore`(`Kind="Netiq"`,`FileName` 借用欄位顯示 Sentinel 名稱;新增 `RevivedCount` 欄位)——與 CSV 匯入共用同一份「資料匯入」頁的稽核軌跡,不是另立一份 | ✅ |
| 告警豁免 | `HostAdminService.NewHostGracePeriod`(24 小時,public 常數)供 `DashboardService.BuildSilentHosts` 與 `HostAdminService` 的 `silent` 篩選共用;LastReportAt 為 null 時改看 CreatedAt 是否超過寬限期,已回報過的主機不受影響、沿用原本 2 天判定;連 hosts.js 的「尚未回報」紅字樣式也套用同一寬限期,避免「不算告警但畫面還是一片紅」的半吊子修法 | ✅ |
| 群組指派 | **刻意不做**(範圍收斂):決策 8 的「依網段指派群組」是「新增 Sentinel 精靈」步驟 3 的畫面設計,與現有這支泛用掃描精靈是兩個不同的 UI 流程;Phase 3 只做後端即時落盤,新匯入主機一律落在「未分組」安全預設,群組指派留給 Phase 4 的新精靈一併做 | ⏸ |
| 文件 | `docs/SCALE-2000-PLAN.md` §5.3 D-3 標記已廢止(附前提變化說明,原文保留供歷史對照);README「NetIQ 匯入佇列套用」章節改寫 | ✅ |

**實作期間的範圍判斷**:計畫原文的「即時套用」列著「依網段指派群組」,但推敲上一輪討論紀錄後發現那其實是「新增 Sentinel 精靈」(Phase 4)步驟 3 的設計,不是這支既有泛用掃描精靈的既定行為——加了會是提前把 Phase 4 的 UI 決策做掉，而不是 Phase 3「退役佇列」本身的份內事。改成只做後端(立即落盤),維持「新匯入主機未分組」的既有安全預設，群組指派整段留給 Phase 4 一次做。

#### Phase 4:精靈+資料匯入頁整併 ✅ 已完成(2026-07-24)

建置零警告、**693 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 走完整端到端流程(「新增 Sentinel」精靈勾自動掃描 → 60 台分兩網段 → 一網段選既有群組、一網段建新群組 → 完成匯入 23 新增+36 復活 → 主機/群組頁核實正確 → 「掃描匯入」對既有 Sentinel 走同一精靈 → 刪除 Sentinel 清理)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 導覽 | `layout.js` 選單「CSV 匯入」→「資料匯入」;移除獨立的「Sentinel 管理」項目與 `/admin/sentinels` 路由 | ✅ |
| 頁面整併 | `Imports.cshtml` 加 `nav nav-tabs`(CSV 匯入/NetIQ 匯入,沿用 `ui.js` 既有的 `bindTabs` 頁籤 helper,未再手刻);NetIQ 分頁含 Sentinel 清單(CRUD 由 `Sentinels.cshtml`/`sentinels.js` 併入 `imports.js`)+精靈進入點;`Sentinels.cshtml`/`sentinels.js` 整支刪除 | ✅ |
| 統一精靈 | `新增 Sentinel`(連線設定→可選自動掃描)與既有 Sentinel 的「掃描匯入」共用同一個 3 步驟 modal(連線/選主機/指派群組),不拆兩套 UI;`POST netiq/create-and-scan` 驗名稱唯一→裸帳密掃描→成功才建立 Sentinel(定案 6);網段選主機沿用原 hosts.js 掃描清單樣式;群組指派面板(未分組/既有群組/建立新群組)只影響本次新增的主機 | ✅ |
| 搬家 | 掃描精靈與匯入紀錄自 hosts.js 移至 imports.js;主機頁移除整套掃描 modal,改為連到 `/admin/imports` 的純連結;主機頁待辦卡(待歸屬/IP 衝突/未分組)保留不動 | ✅ |
| 退役 | 掃描精靈改為「進 Sentinel 詳情才掃」後,`netiq/scan-targets` 端點、`GetScanTargets`、`NetiqScanTargetDto`、`INetiqServerCatalog.GetServers()` 全鏈路失去唯一呼叫端,一併刪除(而非留著等未來用到) | ✅ |

#### Phase 5:群組功能(需求 2、3) ✅ 已完成(2026-07-24)

建置零警告、**698 項單元測試**全數通過,並實際啟動 Web 走完整端到端流程(新增 User 角色群組並勾主機群組→授權矩陣核實勾選正確落地→切換角色即時看到勾選框與說明文字互換→主機群組「目前成員」頁籤依 /24 展開、勾選主機看到即時「N 台將變未分組」警示→移出成員→主機頁核實該主機變回未分組→「加入成員」頁籤原有功能不受影響→復原測試資料)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 使用者群組 | 編輯 modal 內嵌主機群組勾選(僅 Role=User;其他角色即時切換顯示「此角色可檢視全部主機」);寫入走既有 `PUT access/{userGroupId}` API,不另開新端點;新建群組兩段式——群組本體與存取範圍分兩支請求送出,後者失敗會有獨立的警示 toast(「群組已儲存，但設定可檢視的主機群組時發生錯誤，請至授權矩陣頁籤手動設定」)並仍視為建立成功;新群組沒勾任何主機群組時略過第二支請求，避免留下「由（無）改為（無）」的空稽核紀錄，編輯既有群組則一律送出(含全部取消勾選＝收回全部授權) | ✅ |
| 主機群組 | 群組名稱本身變成點擊入口(改用連結樣式按鈕，原本並列的「加入成員」按鈕移除);開啟的 modal 用 `ui.js` 既有 `bindTabs` 分兩頁籤:「目前成員」(新，依 /24 用 `<details>` 摺疊分組，逐台勾選，`OtherGroupCount=0` 的主機顯示「移除後將未分組」徽章，勾選時即時算「N 台將變未分組」提示)/「加入成員」(既有網段批次加入邏輯原樣搬進頁籤，行為不變);新增 `GET host-groups/{id}/members`(依 `OtherGroupCount` 供前端判斷)與 `POST host-groups/{id}/members/remove`(只動被移出的那個群組 id，其餘既有群組不受影響，稽核走既有 `HostUpdate` 動作) | ✅ |

#### Phase 6:文件與收尾 ✅ 已完成(2026-07-24)

建置零警告、**698 項單元測試**全數通過、`--selftest`(99 項)通過,實際啟動 Web 確認各頁面
（總覽儀表板、資料匯入、群組與授權）無主控台錯誤——本 Phase 全程只動文件,程式碼零改動,
Phase 4/5 已各自做過精靈與群組功能的完整瀏覽器端到端驗證,本輪不重複執行,只做啟動健檢。

| 項目 | 內容 | 狀態 |
|---|---|---|
| `WEB-SPEC.md` §10 儲存章改寫 | §10.2 儲存介面表全面改寫(檔案路徑→`lf_blobs`/`lf_log_lines` key,補 `ISentinelStore`、退役 `INetiqImportQueueStore`);§10.3 移除「JSONL 查詢期即時聚合」的替代路徑敘述;§10.4 重寫為「Jsonl 退役與 blob 併發防線」(`ConcurrencyToken`+`Mutate` 重試機制取代原「檔案單一寫入者+原子替換」);§10.5 `Storage.Type` 由三選一改二選一;§1 決策表、§2/§3 系統全貌與 SOLID 對應表一併補記退役狀態 | ✅ |
| `SCALE-2000-PLAN` D-3 廢止註記 | 已於 Phase 3 完成(§5.3 標記已廢止並保留原文) | ✅（沿用既有） |
| `NETIQ-HOSTLIST-WEB-PLAN` 決策 D/E 修訂註記 | 決策 D(Txt 清單模式)標記已廢止、決策 E(Sentinel 唯讀來源)標記已修訂,各附前提變化說明,原文保留供歷史對照 | ✅ |
| `DB-PLAN` 補記 | 頂部免責宣告加 Jsonl 退役補記;「匯入器」`--import-history` 標記確定不做;新增「Schema 升級機制（定案 13）」小節說明未來自製冪等 DDL 的方針;決策狀態彙整表補三列(Jsonl 退役/`--import-history` 不做/schema 升級機制) | ✅ |
| `README.md` 部署章 | 「歷史資料庫（history.txt）」章節標記已退役,加現況說明(備份標的從檔案改為 DB)、欄位級說明原樣保留供資料模型參考;「多台伺服器」「DB 後端」兩則後續方向澄清 Sentinel/主機清單管理已完成、`Storage.Type` 改二選一 | ✅ |
| 收尾體檢(commit 前全面複查) | `JsonCollectionFile<T>` 基底類別更名 `JsonBlobCollection<T>`(名稱裡的「File」已名不符實——底層全走 `IJsonBlobStore`,且本表下方驗收清單明列此名不該殘留);一併清掉散落各 store docstring 的檔案時代語言(「JSONL 後端實作:webdata\xxx.json」「原子替換＋跨程序鎖」→「blob/log key=xxx」「原子讀改寫」),批次 Program.cs 資料根目錄註解同步更正。歷史章節(WEB-SPEC §14、本檔前段等)中的舊類別名依慣例保留 | ✅ |

**未在本輪處理**（明確排除,理由）：docs/WEB-SPEC.md §14「實作進度與過程中的定案」與
docs/DB-PLAN.md 各處「txt ↔ DB 一致性保證」等按時間戳記的歷史決策/進度記錄，均為**當下時序的
如實記錄**（如 2026-07-21 的 Phase 5 SQL 後端「暫緩」決定，事後已被 SCALE-2000-PLAN Phase C 推翻，
該推翻已記錄在 SCALE-2000-PLAN 自己的文件裡），逐條回頭改寫會讓「決策是什麼時候做的」這個資訊
本身失真，不符合本專案一路採用的「標記退役/修訂＋原文保留」慣例。

（2026-07-28 補記：WEB-SPEC.md §14 已於文件收斂時整段移入本檔，見「2026-07-21 起 — WEB-SPEC 實作進度與過程中的定案」段。）

### 測試與驗收重點

- **併發**:blob ConcurrencyToken 案例(兩個 store 實例交錯 Mutate,兩筆變更都存活)。
- **加解密**:roundtrip、`enc:v1:` 前綴辨識、未加密舊值(種子匯入前手填)的相容讀取。
- **SentinelId 回填**:名稱對得到→補 id;對不到→維持 null(=待歸屬)並列警告。
- **刪除 Sentinel**:轄下主機全部停用+`OrphanedFromSentinel` 正確;復活重綁流程不受影響。
- **精靈**:掃描失敗不建 Sentinel;成功建立後中途關閉不匯主機;token 逾期退回重掃;
  既有主機群組不被匯入覆蓋;跳過群組的主機僅 admin 可見。
- **告警豁免**:新匯入主機不觸發無回報告警;滿一週期後恢復納入。
- **退役完整性**:全庫 grep 無 `Jsonl`/`JsonCollectionFile`/`HostListSource`/`apply-netiq-imports` 殘留(註解與文件除外);`Storage.Type="Jsonl"` 啟動報錯訊息可理解。
- **群組**:Role=User 以外的群組編輯不出現勾選;移除成員的未分組警示數正確。

### 未來觀察點(本輪不做,記錄觸發條件)

1. **lf_hosts 正規化**:NetIQ 2000 台上線後,夜間批次每台 `TouchNetiq` 都是整份 hosts blob 重寫(一晚約 2000 次)。若實測批次時間或鎖衝突異常,第一個正規化 lf_hosts(DB-PLAN 表設計現成,`IHostStore` 介面遮蔽、服務層零改動)。
2. **Schema 升級機制**:第一次要動真表時建自製冪等 DDL(EnsureCreated 建的庫無 `__EFMigrationsHistory`,採 EF Migrations 需假基線;雙 provider migrations 維護成本高)。
3. **掃描全部 Sentinel**:日常「巡一輪找新機器」需求出現再議(定案 14)。
4. **報告全文入庫**(DB-PLAN `lf_reports`):export\ txt 維持檔案交付物;Web 需全文檢索時再議。
5. **密碼加密強化**:金鑰改環境變數(真加密)——內嵌金鑰的防護邊界已文件化,營運上有要求時升級。

---

## 2026-07-27 — SHARED-STANDARDS-PLAN：共用標準盤點（原 docs/SHARED-STANDARDS-PLAN.md）

> 狀態：**S1–S12 已全部實作完成（2026-07-27）**；S13／S14（P3 選配）維持未做
> （**2026-07-28 補記**：S14 的 KPI 卡渲染共用已於 refactor/simplify-2026-07 分支 Phase 7
> 以 `ui.js` 的 `statCard()` 完成，見 docs/BACKLOG.md 的說明；recordsUrl(params) 下鑽 URL 組裝
> 共用與 S13 仍未做，已轉入 docs/BACKLOG.md）。
> 原則（使用者定案）：**可以共用同一套標準的就共用，不要各自實作再靠人力維持一致**。
> 本文件盤點整個專案「同一條規則寫了兩份以上」的地方，每項附現況證據、共用方案、行為變化與風險。
>
> 實作補記：
> - **S7** 實作為 `markdown-lite.js` 的兩個入口——`renderAiText`（區塊版：chat 泡泡、AI 判讀、
>   AI 歸納）與 `renderAiInline`（行內版：儀表板今日焦點，清單項內要接下鑽連結）；
>   `PromptGuidelines.LanguageReminder` 已接上全部四個 Web AI 呼叫點的 user prompt 尾端。
> - **S8** 泛型化為 `SettingsBoundClient<TSnapshot, TClient>`（任意快照形狀）——#9 AD 動態驗證
>   的快照是伺服器清單＋SearchBase／Filter，原規劃的 (BaseUrl, KeyEnc) 固定形狀塞不下。
> - **S12** 實作時多掃出三處同款問題並一併修正：records.js（today/defaultFrom/快捷期間鈕）與
>   audit.js（預設期間）也用了 toISOString 的 UTC 日期；handling-panel.js 的快速鈕是正確的
>   本地組法但屬重複實作——全部收斂到 format.js 的 toLocalDateString/todayLocal。
>
> 與 docs/WEB-FEEDBACK-PLAN.md 的關係：本文件是其**批次 0（共用基礎）**——
> 先把單一標準立起來，九項回饋（尤其 #5、#6）落在共用點上實作，而不是再添新的重複。

盤點結論總表（P1=九項回饋直接依賴、P2=順手一起做划算、P3=獨立的清理，可後補）：

| # | 主題 | 重複份數 | 優先 |
|---|------|---------|------|
| S1 | 嚴重度可見性過濾（GetVisibleSeverities） | 2 份實作＋2 處漏套 | P1 |
| S2 | 日風險等級字串與排序權重（高/中/低、RiskRank） | 常數散落 20+ 處、RiskRank 3 份 | P1 |
| S3 | 待辦母體規則（高＋中風險日） | 3 處呼叫端各自過濾 | P1 |
| S4 | 類別統計 DTO 組裝（Dashboard vs Report） | 2 份幾乎相同 | P1 |
| S5 | 主機排行組裝（Dashboard vs Report） | 2 份幾乎相同 | P2 |
| S6 | 涵蓋率缺口判定 | 2 份 | P2 |
| S7 | AI 語言規範與 AI 文字渲染 | prompt 各站各寫尾註、渲染 4+ 處各自來 | P1 |
| S8 | 設定快照式客戶端快取（WebAiService ↔ #9 AD） | 現 2 份、#9 會變 3 份 | P2 |
| S9 | Controller 查詢參數解析（ParseDate/ParseLongs/ParseStrings） | 4+ 份 | P2 |
| S10 | 合法值清單 vs enum（ValidSeverities/KnownCategories/KnownRisks） | 3 處手寫清單 | P2 |
| S11 | 前端嚴重度清單與徽章樣式 | 4 頁各自寫，且已出現樣式分歧 | P1 |
| S12 | 前端本地日期字串（含 reports.js 時區潛在 bug） | 2 份，其中 1 份有 bug | P2 |
| S13 | 類別/嚴重度中文名的 C#／JS 跨語言雙份 | 各 1 份 | P3 |
| S14 | 前端下鑽 URL 組裝與 KPI 卡渲染 | 3 頁重複片段 | P3 |

### S1 嚴重度可見性過濾：收斂到 RecordRepository 單一咽喉點　★核心

**現況（兩份實作＋兩處漏套，正是「各自處理再對齊」的病灶）**：
- 實作一：`SystemSettingsService.GetVisibleSeverities()`——Dashboard／Report 的類別統計用它。
- 實作二：`RecordQueryService.GetDetail` 內另寫一段 inline（`settings.SeverityDisplayMode == "GlobalFilter" ? ...`，
  RecordQueryService.cs:341-343），自己讀設定、自己比字串。
- 漏套一：`RecordQueryService` 依主機／依日期分組視圖的 Categories 聚合
  （RecordQueryService.cs:193、224）**完全沒過濾**——GlobalFilter 模式下，查詢頁分組列
  仍會列出未勾選層級的類別。這是現存 bug，不是設計差異。
- 漏套二：`ReportService.FindSignature`（跨主機簽章查詢）不過濾。

**共用方案**：把「問題嚴重度可見性」做成 `RecordRepository` 的**第二個強制過濾**，
與既有的主機可見範圍同一個位置、同一個理由（該類別的註解原話：
「這個展開如果散落在各個 Service，遲早有人忘了做」——嚴重度過濾已經應驗了這句話）：

- `Query`／`QueryPage`／`GetOne` 回傳前，若 `GetVisibleSeverities()` 非 null（SiteHidden 模式），
  將每筆 record 的 `TopIssues` 過濾為可見層級。
- 效果：Dashboard、Report、RecordQueryService（清單、分組、GetDetail、ClusterSignatures）、
  FindSignature、AI context（Chat/InterpretIssue 經 GetDetail）**全部自動繼承**，
  各 Service 現有的 `Visible(r)` lambda 與 GetDetail 的 inline 過濾**全部刪除**。
- `SystemSettingsService.GetVisibleSeverities()` 保留為唯一的規則出口，Repository 注入使用。

**行為變化（要在版本說明明講）**：
1. 查詢頁分組視圖的類別、簽章查詢，開始尊重全站隱藏（修正上述漏套）。
2. 日處理進度推導（DayHandlingDerivation 的輸入）看到的 TopIssues 變少——
   被隱藏層級的問題本來就不在未處理計算內（同一組 UnhandledSeverities），
   差異只在「已處理計數」不再包含被隱藏層級的已結案問題。與全站隱藏語意一致，接受。
3. 報告 txt 全文（IReportReader）不經 Repository，維持證據層原樣——這條線刻意不動。

**風險**：低-中。所有讀路徑集中改一處，靠測試矩陣掃行為：Repository 過濾的單元測試
＋既有 Dashboard/Report/RecordQuery 測試把 GlobalFilter 案例改為驗證「不再需要各自過濾」。

**依賴**：WEB-FEEDBACK-PLAN #5 的 SiteHidden 直接落在這個咽喉點上實作（模式簡化＝改
GetVisibleSeverities 的回傳條件，過濾機制不再分頁面）。

### S2 日風險等級常數與排序權重：Core 立單一 `RiskLevels`

**現況**：
- `"高"`／`"中"`／`"低"` 字串字面值散落：Web 的 RecordQueryService（189-191、220-222、747-749）、
  DashboardService（63-69、125-127、184、191-192）、ReportService（111-120、138-139、185-188）、
  批次的 LogAnalysisService（NormalizeRisk／MoreSevere）、SelfTestRunner:518、
  Core 的 RecordStorageShaper:19、EfAnalysisRecordStore:284/335-339。
- `RiskRank`（高=3 中=2 低=1）**三份**：RecordQueryService.cs:745、EfAnalysisRecordStore.cs:335
  （其註解自己承認「與 RecordQueryService.RiskRank／ReportService 內幾乎相同」）、
  以及 Dashboard/Report 排行榜的隱含排序規則。

**共用方案**：Core 新增 `RiskLevels` 靜態類別，成為唯一標準：
```csharp
public static class RiskLevels
{
    public const string High = "高"; public const string Medium = "中"; public const string Low = "低";
    public static readonly string[] All = { High, Medium, Low };
    /// <summary>排序權重（高=3 中=2 低=1，未知=0）——所有記憶體排序共用</summary>
    public static int Rank(string riskLevel) ...
    /// <summary>待辦／受影響主機的母體判定：高或中</summary>
    public static bool IsActionable(string riskLevel) ...
    /// <summary>批次 AI 回傳的等級正規化與比較（自 LogAnalysisService 搬入）</summary>
    public static string Normalize(string raw) ...  public static string MoreSevere(string a, string b) ...
}
```
- 批次 `LogAnalysisService` 的 `NormalizeRisk`／`MoreSevere` 搬進來（產生端與消費端同一套字典）。
- 各處字面值改引用常數；`AiInsightService.KnownRisks` 改 `RiskLevels.All`。
- **EF 例外**：EfAnalysisRecordStore.cs:284 的 inline 三元式是給 EF 翻譯 SQL 的，
  不能改成方法呼叫——改引用 `RiskLevels.High` 等 const（const 可進運算式樹），
  並在 335 的私有 RiskRank 上加註解指向 Core 版：「SQL 翻譯限制的必要複本，
  改 Core 版時此處同步」＋一條測試斷言兩者權重一致（把複本置於測試看管下）。

**風險**：純機械替換，行為零變化；靠編譯器與既有 804 測試保證。

### S3 待辦母體規則：搬進 `HandlingService.GetTodo` 內部

**現況**：「待辦母體＝高＋中風險日」這條規則由**呼叫端各自過濾**：
DashboardService.GetSummary:69、DashboardService.BuildGroupRisk:184，
WEB-FEEDBACK-PLAN #6 的報表處理進度將是第三處。

**共用方案**：`GetTodo(records)` 改為**自己套** `RiskLevels.IsActionable` 過濾，
呼叫端傳整批紀錄即可；介面註解同步改寫（「母體是傳入的風險日紀錄」→「母體規則在此強制」）。
#6 報表直接呼叫，不再複製過濾。

**風險**：低。呼叫端行為不變（過濾位置移動）；HandlingService 測試補「傳入含低風險日
仍只計高＋中」案例。

### S4 類別統計 DTO 組裝：Dashboard／Report 合為一份

**現況**：`DashboardService.BuildCategoryCards` 與 `ReportService.BuildCategories`
幾乎逐行相同（Visible lambda＋CategoryAggregator.Aggregate/Merge＋hostsPerCategory
＋DashboardCategoryDto 映射，各約 25 行）。

**共用方案**：S1 落地後 Visible lambda 消失，剩餘的「records → List&lt;DashboardCategoryDto&gt;」
抽成 Web 端共用靜態類 `RecordStatsBuilder.BuildCategoryCards(records)`，兩個 Service 呼叫同一份。

**風險**：無行為變化；兩邊測試合併驗同一個 builder。

### S5 主機排行組裝：同上合為一份

**現況**：`DashboardService.BuildHostRanking` 與 `ReportService.BuildHostRanking`
的 GroupBy／DashboardHostDto 映射／排序鏈（高風險日 → 關聯訊號日 → 中風險日，§DB-PLAN E）
兩份幾乎相同，差異只在 Dashboard 端 Take(10)、Report 端整批回傳後切分。

**共用方案**：`RecordStatsBuilder.BuildHostRanking(records, hostsByName)` 回傳完整排序清單，
排序鏈用 `RiskLevels.Rank` 家族；Dashboard 自行 Take(10)，Report 沿用現有 Top10＋其他彙總切分。

### S6 涵蓋率缺口判定：Core 計算屬性

**現況**：`r.DataIncomplete || r.SecurityLogAvailable == false` 在 DashboardService:65
與 ReportService:121 各寫一次（詳情頁前端 renderCoverage 是逐項呈現，不算重複）。

**共用方案**：`DailyAnalysisRecord` 加唯讀計算屬性 `HasCoverageGap`（不序列化，
`[JsonIgnore]`，避免動到兩個儲存後端的資料形狀），兩處改用。

### S7 AI 語言規範與 AI 文字渲染：一個出口進、一個出口出

**現況**：
- 進（prompt）：`PromptGuidelines.Language` 已共用，但 WEB-FEEDBACK-PLAN #2 的
  「尾端語言提醒」若在 AiInsightService 各呼叫點手寫字串，就是新的重複。
- 出（渲染）：AI 文字在前端至少 4 處各自渲染——chat 泡泡（chat-panel.js）、
  AI 判讀面板（record-detail.js aiInterpretPanel）、儀表板今日焦點（dashboard.js loadAiFocus）、
  查詢歸納（records.js）。「AI 徽章＋淡色區塊＋textContent」的組合每處自己拼，
  #3 的 markdown-lite 若只接 chat，其他處又會分岔。

**共用方案**：
- 進：`PromptGuidelines` 加 `LanguageReminder` 常數（尾端一句話版本），
  Web 四個 AI 呼叫點與批次（若需要）都引用它，不各寫。
- 出：#3 的 `markdown-lite.js` 匯出唯一入口 `renderAiText(container, text, { badge })`——
  內含 DOM 組裝（永不 innerHTML）、AI 徽章、樣式類別。四個渲染點全部改走它；
  之後任何新的 AI 輸出點沒有第二種寫法可抄。

### S8 「設定快照 → 重建客戶端」快取模式：抽一個小工具

**現況**：WebAiService 內同一套「lock＋snapshot 比對＋重建」寫了**兩份**
（GetClient／GetChatClient，各 ~30 行只差參數）；WEB-FEEDBACK-PLAN #9 的
DynamicAuthenticationProvider 需要第三份（LdapService 隨 DB 設定重建）。

**共用方案**：Web 端新增 `SettingsBoundClient<TClient>`（建構參數：snapshot 取值函式＋工廠），
三個使用點共用。快取語意（低頻重建、舊實例交給 GC）維持 WebAiService 現有註解的決策。

**風險**：低；WebAiService 行為不變，#9 少寫一份易錯的並行程式碼。

### S9 Controller 查詢參數解析：一份靜態工具

**現況**：`ParseDate`／`ParseLongs`／`ParseStrings` 在 RecordsController、AiController、
AuditController、DashboardController 至少四份（逐字相同）。

**共用方案**：`Controllers/Api/QueryStringParsing.cs` 靜態類別收一份；
RecordsController:148 的「解析失敗即丟 Validation」包裝一併收入（`ParseRequiredDate`）。
AuditController 的 `To 補到當日 23:59:59` 屬呼叫端語意，留在原地。

### S10 合法值清單與 enum 對齊：用測試看管，不再裸寫

**現況**：`SystemSettingsService.ValidSeverities`（手寫四字串）、
`AiInsightService.KnownCategories`（手寫八字串）、`KnownRisks`（手寫三字串）——
enum（IssueSeverity／IssueCategory）加值時這些清單不會有任何編譯錯誤，靜默漏。

**共用方案**：
- `KnownCategories` → `Enum.GetNames<IssueCategory>()`；`KnownRisks` → `RiskLevels.All`（S2）。
- `ValidSeverities` 承載「畫面勾選順序（由重到輕）」，不宜直接用 enum 宣告順序——
  保留陣列，但加一條測試斷言「陣列集合 == enum 名稱集合」，enum 加值時測試紅燈。

### S11 前端嚴重度清單與徽章：format.js 補齊、消除已發生的分歧

**現況（已經出現實際分歧的鐵證）**：
- `SEVERITY_ORDER`（record-detail.js:23）與 `SEVERITIES`（settings.js:11）兩份同值清單；
  reports.js 的 severityKeys（202-208）、dashboard.js severityBreakdown（253-257）又各自拼。
- **樣式分歧**：dashboard.js severityBreakdown 給 Low 用 `secondary` variant，
  format.js `SEVERITY_VARIANT` 給 Low 用 `neutral`——同一個「低」在儀表板與其他頁
  已經是兩種徽章底色。這正是「各自處理」的必然結果。

**共用方案**：format.js（本來就是「顯示格式化的單點定義」）補匯出：
- `SEVERITY_ORDER`（由重到輕陣列）——record-detail／settings／reports 改 import；
- `severityCountBadge(severity, count)`（顏色＋文字計數徽章）——dashboard 的
  severityBreakdown 改用它，Low 的底色回歸 `SEVERITY_VARIANT` 單一標準。

### S12 前端本地日期字串：format.js 收一份（順修時區 bug）

**現況**：
- record-detail.js:349-351 手寫 pad 組本地 `yyyy-MM-dd`（正確，附註解）；
- reports.js:430-431 `toISOString().slice(0,10)` 取的是 **UTC 日期**——台灣（UTC+8）
  凌晨 0–8 點開報表頁，預設期間會少算一天。潛在 bug，共用順手修掉。

**共用方案**：format.js 加 `toLocalDateString(date)` 與 `todayLocal()`，兩處改用；
其他頁面日後需要本地日期一律走這裡。

### S13 類別／嚴重度中文名的跨語言雙份（P3，選配）

**現況**：類別中文名 C# 一份（批次 RiskReportService.cs:125-133，txt 報告用）、
JS 一份（format.js CATEGORY_NAMES）。跨語言無法靠編譯器對齊。

**共用方案（兩段）**：
1. 先把 C# 版從批次的 RiskReportService 搬到 **Core**（`IssueCategoryNames`），
   批次與 Web 後端共用一份——這步沒有爭議，直接做。
2. 跨到 JS 的單一來源：_Layout.cshtml 由 Core 常數 server-render 一段
   `window.LF_META = {...}`（類別名、嚴重度名、風險等級），format.js 讀它、
   保留現值當 fallback。不加 API 請求、不動快取。
   評估：JS 側 format.js 已是單點，分歧風險低——此步標 P3，晚做或不做都可接受。

### S14 前端下鑽 URL 與 KPI 卡渲染（P3，選配）

- `/records?riskLevels=…&from=…&to=…` 的組裝在 dashboard.js／reports.js／record-detail.js
  重複 10+ 處 → format.js 或新 core 模組加 `recordsUrl(params)`（負責 encode 與拼接）。
- dashboard.js renderKpi 與 reports.js renderKpi 的統計卡 DOM 結構高度相似 →
  ui.js 抽 `renderStatCards(container, cards)`，對比徽章（reports 的 comparisonBadge）作為
  card 的可選欄位傳入。
- 皆為純顯示層重構、無行為變化；排 P3，避免與批次 A–E 的頁面改動互相踩線。

### 與 WEB-FEEDBACK-PLAN 批次的整合順序

```
批次 0a（共用基礎，先行）：S2 RiskLevels → S3 GetTodo 內建母體 → S9 參數解析 → S10 清單看管
批次 0b（咽喉點）：S1 Repository 嚴重度過濾（先在現有 GlobalFilter 語意下落地，行為含漏套修正）
批次 A：#1/#4 ＋ #3 的 markdown-lite 以 S7 renderAiText 形式落地 ＋ #2 用 S7 LanguageReminder ＋ S11/S12
批次 B：#5 模式簡化（SiteHidden）——只改 GetVisibleSeverities 條件與設定頁，機制已在 0b
批次 C：#6 報表——處理進度直接用 S3 後的 GetTodo；圖表組裝用 S4/S5 的 RecordStatsBuilder
批次 D：#7 批次新增使用者（沿用 SaveUser/SetUserGroups，本來就無重複）
批次 E：#9 AD（DynamicAuthenticationProvider 用 S8 SettingsBoundClient）→ #8
批次 F（收尾，選配）：S13 / S14
```

**測試基準**：每個批次結束跑全量 804；S1/S3 有行為變化（漏套修正、母體過濾位置移動），
其餘為零行為變化的收斂，紅燈即回歸訊號。

---

## 2026-07-27 — WEB-FEEDBACK-PLAN：十二項使用者回饋的規劃（原 docs/WEB-FEEDBACK-PLAN.md）

> 狀態：**全部實作完成（2026-07-27）**，879 個測試綠、關鍵頁面瀏覽器實測通過。
> 拍板原則：層級與處理指標**整站統一**（不允許各頁各自範圍）；AD 失敗細節不對使用者顯示；
> 設定頁提供 AD 測試連線；批次新增遇既存帳號由使用者決定是否覆蓋權限。
>
> 實作時與規劃的偏差（皆為改善方向）：
> - **#3 prompt 端不再要求「純文字」**：原規劃是「prompt 要求純文字＋渲染器兜底」，實作後
>   markdown-lite 已能安全渲染粗體／清單，要求純文字反而放棄有用的排版——刻意不加該指令。
> - **S8 泛型化**：SettingsBoundClient 從 (BaseUrl, KeyEnc) 固定形狀改為 `<TSnapshot, TClient>`
>   任意快照——AD 動態驗證的快照是伺服器清單＋SearchBase／Filter，原形狀塞不下。
> - **S7 渲染出口涵蓋四個點**：chat 泡泡（區塊版 renderAiText）＋ AI 判讀／AI 歸納（區塊版）＋
>   儀表板今日焦點（行內版 renderAiInline，清單項內要接下鑽連結）。

批次分組（依相依與風險排序；**批次 0 見本檔上方「2026-07-27 — SHARED-STANDARDS-PLAN」段**——
先立共用標準，各項回饋落在共用點上實作，不再各自處理）：

| 批次 | 項目 | 性質 |
|------|------|------|
| 0 | 共用標準 S1–S12（SHARED-STANDARDS-PLAN） | 單一事實來源收斂，先行 |
| A | #1 等待動畫、#3 Markdown 呈現（走 S7 renderAiText）、#2 台灣用語強化（走 S7 LanguageReminder）、#4 下拉連動、#10 固定高度＋自動捲底、#12 清除鈕圖示 | 純前端＋prompt 微調，低風險 |
| A2 | #11 報告全文餵入 AI 對話（PromptBudget 預算控管） | 後端 prompt 組裝，中小型 |
| B | #5 層級對應與連動（機制落在 S1 Repository 咽喉點） | 模式簡化＋全站統一過濾，已定案 |
| C | #6 報表圖表改版（處理進度走 S3、統計組裝走 S4/S5） | 前後端，中型 |
| D | #7 批次新增使用者 | 前後端，中型 |
| E | #9 AD 設定與動態驗證（快取走 S8）→ #8 AD 自動補資料 | 驗證層改造，#8 依賴 #9 |

### #1 詢問 AI 沒有等待中的提示動畫

**現況**：`chat-panel.js onSubmit` 呼叫 `withBusy(send, '')`——busyText 傳空字串，
`withBusy` 只 disable 按鈕、不顯示 spinner（ui.js:383），訊息區也沒有任何「AI 思考中」的視覺回饋。
地端模型一輪回覆可能要數秒～數十秒（timeout 60 秒），使用者只看到畫面靜止。

**方案**：
1. 訊息區加「輸入中」泡泡：`renderMessages()` 增加 `pending` 狀態，送出後在對話尾端
   渲染一顆 assistant 樣式的泡泡，內容是三點跳動動畫（純 CSS `@keyframes`，新增 `.lf-typing` 到 site.css）。
   收到回覆或失敗後移除。
2. 送出鈕改 `withBusy(send, '送出中')`，沿用既有 spinner 樣式。

**影響檔案**：`wwwroot/js/pages/chat-panel.js`、`wwwroot/css/site.css`。
**風險**：無；純前端，JS 無測試涵蓋。

### #2 AI 回覆需要台灣用語繁體中文

**現況**：`AiInsightService.ChatAsync` 的 system prompt 已含 `PromptGuidelines.Language`
（記憶體/硬碟/網路等詞彙白名單＋簡體字黑名單）。但小模型在多輪對話攤平成長 user prompt 後，
對 system prompt 尾端規範的遵循度會下降，仍會漏出簡體或大陸用語。

**方案**（先做 1，2 視效果保留）：
1. **尾端強化**：在攤平後的 user prompt 最後（`【新問題】…` 之後）追加一行
   「（請全程以台灣繁體中文與台灣資訊業用語回答，勿使用簡體字）」——模型對 prompt 尾端的指令遵循度最高。
   InterpretIssue／QuerySummary／TodayFocus 若也有同樣問題，比照辦理。
2. **偵測重生（保留選項）**：回覆若含常見簡體字（「内、盘、络、认、据、启」等小集合偵測），
   重打一次並在 prompt 註明。代價是最壞情況延遲翻倍，互動情境不划算——先不做，觀察 1 的效果。

不建議引入 OpenCC 之類的轉換庫：簡→繁逐字轉換處理不了「用語」問題（默认→預設不是字對字），
且違反專案「不增外部依賴」的傾向。

**影響檔案**：`Services/AiInsightService.cs`。
**測試影響**：AiInsightService 相關測試若有斷言 prompt 內容需同步更新。

### #3 AI 回覆的 Markdown 呈現

**現況**：AI 回覆一律 `textContent`＋`white-space: pre-wrap`（chat-panel.js:157–159），
這是刻意的安全設計（AI 產出不可信任為 HTML），但模型愛輸出 `**粗體**`、`- 清單` 等
Markdown 語法，畫面上就是原樣的星號。

**方案**：兩頭並進——
1. **Prompt 端**：chat 的 system prompt 加「以純文字回答，不要使用 Markdown 語法（粗體星號、井字標題等）」。
   小模型不會百分之百聽話，所以還需要 2。
2. **前端輕量渲染器**：新增 `wwwroot/js/core/markdown-lite.js`，把回覆文字轉成 **DOM 節點**
   （`document.createElement`＋`textContent` 組裝，**全程不碰 innerHTML**，維持既有 XSS 防線）。
   只支援安全子集：`**粗體**`、`` `行內代碼` ``、`- `/`1. ` 清單、`#` 開頭行轉粗體行、換行。
   其餘語法（連結、圖片、HTML）一律當純文字。不引入外部 Markdown 庫。
3. 套用範圍：chat 泡泡先做；「AI 判讀」「查詢歸納」「今日焦點」等其他 AI 文字輸出點
   共用同一個模組，之後視需要接上。

**影響檔案**：新增 `wwwroot/js/core/markdown-lite.js`；`chat-panel.js`；`AiInsightService.cs`（prompt）。
**風險**：低。渲染器不解析 HTML、不產生連結，攻擊面沒有變大。

### #4 詢問 AI 下拉選單應跟隨重點問題的嚴重度篩選

**現況**：`record-detail.js load()` 把 `currentDetail.topIssues` 整包傳給 `initChatPanel`
（Locked 模式已先過濾），但**前端嚴重度篩選鈕（activeSeverities）與下拉選單不連動**——
使用者關掉「低」，下拉裡仍列得出低嚴重度的問題。

**方案**：
1. `initChatPanel` 改收「目前篩選後」的清單：`topIssues.filter(i => activeSeverities.has(i.severity))`。
2. chat-panel.js 匯出 `updateIssueOptions(issues)`：嚴重度鈕切換時（`renderSeverityFilter` 的 click handler）
   重建下拉選項。若目前選中的 issueKey 已不在清單中 → 重置選擇與對話（回到「請先選擇」、清空 messages）；
   仍在清單中 → 保留選擇與對話不動。
3. 後端 `AiController.Chat` 不用改——它驗證 issueKey 存在於 `detail.TopIssues` 即可，
   前端篩選是顯示層行為（與 DefaultHidden 模式語意一致：資料還在，只是預設不顯示）。

**影響檔案**：`record-detail.js`、`chat-panel.js`。
**風險**：低。注意 chat-panel 以 cloneNode 重綁事件的既有模式，更新選項時不要重複綁 listener。

### #5 設定的「層級」與實際資料層級對不上、連動有問題　⚠ 決策點

**現況診斷**（這項一半是設計如此、一半是真的不一致）：

系統裡有**兩套不同的層級**：
- **問題嚴重度**（IssueSeverity：Critical/High/Medium/Low，畫面顯示「嚴重/高/中/低」）——
  設定頁「未處理計算層級」勾的是這個。
- **日風險等級**（RiskLevel：高/中/低）——批次分析時算定的證據層，
  儀表板「高風險日/中風險日」KPI、趨勢圖、風險層級占比、風險主機排行用的是這個。
  `SystemSettings.SeverityDisplayMode` 註解明講「不影響風險等級判定與報告全文」。

所以「沒有選『中』，儀表板卻還顯示中與低風險」有兩層原因：
1. 日風險等級本來就不受此設定影響（設計如此，但畫面上完全沒有說明，使用者無從分辨兩套「高中低」）。
2. 就算看的是問題嚴重度（風險類型卡的嚴重度徽章分解），**只有 GlobalFilter 模式**會過濾聚合
   （`SystemSettingsService.GetVisibleSeverities` 只在 GlobalFilter 回集合，其他模式回 null）——
   Locked 模式號稱「完全隱藏」，卻只藏詳情頁，儀表板／報表的徽章與計數照樣出現未勾選層級。這是真的不一致。

**定案（2026-07-27，依「整站統一、不要各頁各自範圍」的原則）**：

1. **顯示模式從三個簡化為兩個**——三模式的差異本身就是「各頁範圍不同」的來源，直接收斂：
   - `DefaultHidden`（預設隱藏，仍可手動開啟）：維持現況，純顯示層行為。
   - `SiteHidden`（全站隱藏）：未勾選層級的問題**在整個 Web 後端查詢層一律排除**——
     詳情頁重點問題、AI 對話下拉、AI 聚類輸入（ClusterSignatures）、儀表板類別卡、
     報表類型分布圖、問題查詢頁的問題層欄位與下鑽、簽章查詢，全部同一套過濾，沒有例外頁。
     實作錨點（**SHARED-STANDARDS-PLAN S1**）：過濾收斂到 `RecordRepository` 單一咽喉點，
     `GetVisibleSeverities()` 在 SiteHidden 回集合、各 Service 不再各自過濾；
     詳情頁不再靠前端 Locked 特判，後端給什麼就是什麼。
   - **舊值遷移**：blob 裡既存的 `Locked`／`GlobalFilter` 在 `SystemSettingsService.Get()`
     讀取時正規化為 `SiteHidden`（兩者語意都被新模式涵蓋且更嚴格一致）；
     `Update()` 只接受新的兩個值。不動 blob 本身，下次儲存自然寫入新值。
2. **文案對齊**：設定頁「未處理計算層級」明確標示為「問題嚴重度（嚴重/高/中/低）」，
   並加說明「日風險等級（高/中/低風險日）由批次分析算定，不受此設定影響」；
   儀表板高/中風險日 KPI 卡加 tooltip 註明同一句。
3. **日風險等級維持不連動**（詳細理由見下）：風險等級是批次算定寫進報告 txt 的證據層，
   且它不是嚴重度的彙總（關聯訊號/趨勢也會拉高風險），無法靠「扣掉某層級的問題」可靠重算；
   Web 重算會讓畫面與報告全文、既有待辦數字對不上，違反誠實原則。以 2 的文案讓兩套層級可分辨。

**影響檔案**：`SystemSettingsService.cs`（GetVisibleSeverities＋值正規化）、`RecordQueryService.cs`
（GetDetail／ClusterSignatures 接上過濾）、`record-detail.js`（移除 Locked 前端特判）、
`settings.js`（兩模式）、`Settings.cshtml`（文案）、`SystemSettings.cs`（註解更新）。
**測試影響**：SystemSettingsService 補正規化案例；Dashboard／Report／RecordQuery 的
GlobalFilter 測試改為 SiteHidden；原 Locked「只藏詳情頁」的測試反轉。

### #6 報表圖表改版（圓餅圖縮小＋管理者指標＋自選圖表）

**現況**：Reports.cshtml 是 2×2 等寬網格，「風險層級占比」doughnut 只有兩個值（高/中風險日數）
卻佔 1/4 版面；報表沒有「主機母體」與「處理進度」視角。

**方案**：
1. **新增管理者指標**（後端 `ReportSummaryDto` 擴充）：
   - `TotalHosts`：可見且啟用的主機總數（ReportService 注入 `IVisibilityService`，
     與 DashboardService 同一來源，數字才對得上）。
   - `Handling`：期間內高＋中風險日的處理彙總（注入 `IHandlingService`，
     沿用 `GetTodo` 的日層級規則；若 `GetTodo` 目前沒回 resolved 數，擴充它而不是另寫一套推導）。
   - 前端據此畫兩顆新的小 doughnut：「受影響主機占比」（affectedHosts/totalHosts）、
     「處理進度」（已處理/待辦母體），中央疊大字百分比。
2. **版面**：原「風險層級占比」與兩顆新圖合併成一列三顆小占比圖（col-lg-4×3，高度約現行一半），
   騰出的位置讓「主機告警排行」可以放寬。趨勢與類型分布維持上排。
3. **自選圖表 modal**：
   - reports.js 建圖表註冊表 `{id, title, sectionEl, render}`；
   - 工具列加「自訂圖表」鈕開 Bootstrap modal，checkbox 逐圖勾選；
   - 勾選狀態存 `localStorage('lf.reports.visibleCharts')`，預設全開；
   - 隱藏的圖不呼叫 render（省一次 Chart.js 建構），重新勾選時才 lazy render；
   - 列印沿用畫面狀態（隱藏的卡片有 d-none 就不會印）。

**定案（2026-07-27）**：「處理進度」母體採**日層級**（與儀表板待辦同一套 `GetTodo` 規則），
理由：全站唯一的跨頁處理指標（儀表板待辦 KPI）已是日層級，報表沿用同一規則，
儀表板、報表、下鑽清單三處數字才會相等；問題層級的已處理/未處理計數器是詳情頁的
頁內視角（含低風險預設不處理、自動雜訊等推導），拿來做全站百分比會隨顯示設定漂移。

**影響檔案**：`ReportService.cs`＋`DashboardDtos.cs`（或 ReportDtos）、`ReportsController`（無需改，DTO 帶出即可）、
`Reports.cshtml`、`reports.js`、`site.css`。
**測試影響**：ReportService 測試補 TotalHosts／Handling 欄位案例。

### #7 手動新增使用者支援一次多筆

**現況**：modal 一次一筆（POST `/api/admin/users` ＋ PUT `/users/{id}/groups`）。

**方案**：
1. **UI**：modal 頂部加「單筆／多筆」切換。多筆模式：
   - 帳號欄換成 textarea（一行一個帳號，也接受逗號分隔）；
   - 隱藏顯示名稱、Email 欄位（顯示名稱後端預設＝帳號；Email 留空，配合 #8 由 AD 登入時補）；
   - 群組勾選照舊，套用到整批。
2. **後端**：新增 `POST /api/admin/users/batch`
   （`BatchCreateUsersRequest { Accounts: List<string>, GroupIds: List<long>, Active: bool, OverwriteExisting: bool }`），
   Service 端 `BatchCreateUsers`：
   - 逐帳號 trim、去重、去空白；上限（建議 100 筆）防手滑貼整份名冊；
   - 群組存在性驗證沿用 `SetUserGroups` 的規則；
   - **已存在帳號**依 `OverwriteExisting` 決定：false → 跳過不動；true → 以這批勾選的群組
     **整組取代**其群組（走既有 `SetUserGroups`，沿用其 Before/After 稽核）；
     顯示名稱與 Email 兩種情況都不動；
   - 回傳結果分類：新增成功／已存在（跳過或已覆蓋）／格式不合；
   - 稽核：每個新增使用者一筆 `UserCreate`（與單筆一致），另補一筆批次摘要。
3. **前端流程（定案 2026-07-27）**：送出前先比對頁面已載入的使用者清單，
   發現已存在帳號時跳 `confirmAction` 告警，**列出那些帳號**，讓使用者選擇：
   「跳過已存在」或「以這次勾選的群組覆蓋其權限」——選了才送出（對應 OverwriteExisting）。
   前端比對只是 UX，後端仍以自己的查詢結果為準（避免兩人同時操作的競態）。
   完成後 toast＋結果清單（「新增 8 筆、覆蓋 2 筆（a、b）」）。

**影響檔案**：`AdminController.cs`、`AdminDtos.cs`、`UserAdminService.cs`、`Users.cshtml`、`users.js`。
**測試影響**：UserAdminService 補批次案例（去重、已存在、群組不存在、上限）。

### #8 只填帳號的使用者，AD 登入時自動補顯示名稱與 Email

**現況**：`IdentityService.Login` 驗證通過後只讀 `lf_users`，顯示名稱空白時 fallback 帳號；
`LdapAuthenticationProvider.Verify` 只回 bool，拿不到 AD 上的 displayName/mail。

**方案**（依賴 #9 的 LdapService）：
1. `CredentialCheckResult` 擴充：`record CredentialCheckResult(bool Success, string? FailureReason = null, LdapUserInfo? UserInfo = null)`。
   AD provider 驗證成功時順手 `GetUser`（同一次 bind 的連線內查詢，參考程式碼已具備）；
   Stub 回 null；查詢失敗不影響登入（補資料是加值，不是門檻）。
2. `IdentityService.Login` 在「使用者存在且啟用」之後：
   - `DisplayName` 為空**或等於帳號**（手動/批次新增的預設值，視同未填）且 AD 有 displayName → 補；
   - `Email` 為 null 且 AD 有 mail → 補；
   - 有任一異動才 `Upsert`＋寫一筆 `UserUpdate` 稽核（summary 註明「AD 登入自動同步」）。
   - 已知取捨：使用者手動把顯示名稱改成與帳號相同字串時會被 AD 值覆寫——機率低、影響小，接受。
3. 只在登入當下同步，不做背景批次同步（沒有 service account，設計上就拿不到別人的資料——
   參考程式碼的 bind 模型是「用使用者自己的帳密查自己」，這也省掉保管服務帳號密碼的整包問題）。

**影響檔案**：`Auth/IAuthenticationProvider.cs`、`Auth/LdapAuthenticationProvider.cs`（改寫，見 #9）、
`Services/IdentityService.cs`。
**測試影響**：IdentityService 測試補「補資料／不覆寫已填值／AD 查詢失敗仍登入成功」案例。

### #9 設定頁自訂 AD 主機與啟用 AD 驗證　⚠ 決策點

**現況**：驗證方式在 `appsettings.json`（`Auth:Provider` = Stub/Ldap）**啟動時定死**，
DI 註冊 singleton；Ldap 走 `PrincipalContext(ContextType.Domain, domain)`，只支援單一網域名稱、
拿不到失敗原因、也查不到使用者資訊。使用者提供的參考實作
（System.DirectoryServices 直接 bind、多伺服器輪詢、子錯誤碼解析、RFC 4515 跳脫、GetUser/Query 投影）功能完整得多。

**方案**：

#### 9.1 設定模型（SystemSettings 擴充，存 webdata blob，純新增欄位、無 schema 變更）
```
AdAuthEnabled     bool          預設 false
AdServers         List<string>  IP 或 LDAP URL，依序嘗試
AdSearchBase      string        選填（DC=corp,DC=com）
AdSearchFilter    string        預設 "(sAMAccountName={0})"
```
不需要儲存任何 AD 服務帳號密碼——bind 一律用登入者自己的帳密（參考實作的模型），
整包規劃**沒有新機密要保管**，也就不動 LF_CRYPTO_KEY 相關機制。

#### 9.2 移植參考實作
新增 `LogForesight.Web/Auth/Ldap/`：`LdapOptions`、`LdapAuthStatus`、`LdapUserInfo`、`LdapService`
（依專案慣例調整註解與 NLog）。csproj 補 `System.DirectoryServices` 套件參考
（現有僅 AccountManagement）；類別掛 `[SupportedOSPlatform("windows")]`，與現況一致。

#### 9.3 動態 Provider（核心改動）
新增 `DynamicAuthenticationProvider : IAuthenticationProvider`，取代 DI 裡依 appsettings 二選一的註冊：
- 每次 `Verify`／`RequiresPassword` 讀 `ISystemSettingsStore`：
  - `AdAuthEnabled && AdServers 非空` → 用 DB 設定建 `LdapService` 驗證
    （比照 `WebAiService` 的 snapshot 模式快取實例，設定變更即生效、不必重啟站台）；
  - 否則 → 委派給 appsettings 決定的原 provider（Stub 或既有 Ldap）。
- 這正是「測試模式（Stub）開啟 AD 後也走 AD 驗證」的實現方式；
  `WebAppSettings.Validate` 禁止正式環境用 Stub 的欄杆**維持不變**（DB 開關可被關掉，
  不能取代部署層的強制）。
- **鎖死風險與逃生門**：admin 若填錯伺服器把所有人擋在門外，`serverAdmin` 本地救援帳號
  不經任何 Provider（IdentityService 既有順序），永遠進得來——設定頁 hint 要明講這點。
- 失敗原因處理（**定案 2026-07-27：不顯示**）：`LdapAuthStatus` 的細分（密碼過期／帳號鎖定／停用…）
  **只進診斷 log 與稽核 detail**，前端一律「帳號或密碼錯誤」——不洩漏帳號狀態的既有原則不變。

#### 9.4 設定頁 UI 與 API
- Settings.cshtml 新增「AD 驗證」卡：啟用開關、伺服器 textarea（一行一台）、
  SearchBase／SearchFilter（進階，收合）、逃生門說明文字。
- `SystemSettingsDto`／`UpdateSystemSettingsRequest` 對應擴充；
  `SystemSettingsService.Update` 驗證：啟用時至少一台伺服器、URL 格式粗檢；稽核 Before/After 帶伺服器清單。
- 「測試連線」鈕（**定案 2026-07-27：要做**）：`POST /api/admin/settings/ad-test`，
  用**管理者當場輸入的帳密**對表單目前填的伺服器清單試 bind（未儲存的值也能測），
  回成功／`LdapAuthStatus` 細節（這裡是 admin 對自己測試，細節可以顯示）。
  密碼不落盤、不進稽核 detail；稽核只記「執行了 AD 測試連線」與對象伺服器。

#### 9.5 既有 LdapAuthenticationProvider 的去留
改寫為使用新 LdapService（appsettings 的 `Auth:Ldap:Domain` 對應成單一 server），
或保留原 PrincipalContext 實作僅供 fallback。建議**改寫統一**，兩套 AD 程式碼並存遲早漂移；
`Auth:Ldap:Domain` 設定鍵保留向下相容。

**影響檔案**：`Core/Models/SystemSettings.cs`、`Web/Auth/*`（新增 Ldap 資料夾＋Dynamic provider）、
`Extensions/ServiceCollectionExtensions.cs`（DI）、`Services/SystemSettingsService.cs`、
`Models/Dto/SettingsDtos.cs`、`Controllers/Api/SettingsController.cs`（或 AdminController 既有 settings 端點）、
`Settings.cshtml`、`settings.js`、`LogForesight.Web.csproj`。
**測試影響**：LdapService 本體依賴 DirectoryEntry 難以單元測試，保持薄、不強測；
測試火力放在 DynamicAuthenticationProvider 的切換邏輯（DB 開關開/關、伺服器清單空、fallback）
與 SystemSettingsService 的驗證。既有 IdentityService 測試用 Stub 不受影響。

### #10 詢問 AI 區塊固定高度＋scrollbar＋回覆後自動捲底

**現況**：`#chat-messages` 沒有高度限制，對話越長卡片越高，把下方的報告全文卡越推越遠。
`renderMessages()` 尾端其實**已經有** `container.scrollTop = container.scrollHeight`
（chat-panel.js:166）——只是容器不會出捲軸，這行目前形同無效；高度限制一加上就直接生效。

**方案**：
1. site.css 新增 `.lf-chat-messages { max-height: 340px; overflow-y: auto; }`，
   RecordDetail.cshtml 的 `#chat-messages` 掛上此 class。
   用 **max-height 而非固定 height**：對話還沒開始時區塊維持精簡，不擺一個大空框
   （若要「永遠固定高度」再改 height，一行的事，預設先取不佔版面的做法）。
2. 自動捲底沿用既有那行，涵蓋三個時點：送出使用者訊息後、#1 的「思考中」泡泡出現時、
   AI 回覆渲染後——三者都走 `renderMessages()`，不用另寫。
3. 已知取捨：使用者往上捲看舊訊息時若回覆剛好到達，會被強制捲到底。
   標準聊天 UI 會判斷「接近底部才捲」，但這裡最多 10 輪、訊息量小，先用簡單版；
   若實際造成困擾再加「距底 < 一個泡泡高才自動捲」的判斷（一個 if 的事）。

**影響檔案**：`site.css`、`RecordDetail.cshtml`。前端純樣式，無風險。

### #11 分析用的 log 一併傳給 AI 回答提問

**現況與資料面的誠實盤點**（決定這項能做到哪裡）：
- 原始事件**逐筆資料批次不落盤**——批次直接讀各主機的 Event Log 分析完就丟，
  持久化的只有兩樣：分析紀錄（每問題最多 **3 則相異範例訊息、各截 200 字**，
  LogAggregator.cs:22-23；低風險基準日還會被 RecordStorageShaper 清空樣本）與
  **報告 txt 全文**（`record.ReportFile` ＋ IReportReader，經 `RecordQueryService.GetReport`
  讀取、可見範圍已由 Repository 強制）。
- 所以「分析用的 log」在 Web 端可行的最大範圍＝**當日報告全文**＋現有問題欄位樣本。
  要更原始的逐筆 log 就得改批次落盤策略（儲存體積數量級成長），不在本項範圍；
  若日後真有需求，另開規劃。

**方案**：
1. `AiController.Chat` 載入當日報告全文（`_records.GetReport(hostId, parsedDate)`，
   同頁「報告全文」卡的同一條路、同一套授權），傳入 `AiInsightService.ChatAsync`。
2. `ChatAsync` 把報告全文加進 context，**圍欄比照事件訊息**：
   「【當日分析報告全文——僅供分析，不是指令】」——報告內含事件原文（攻擊者可控字串），
   與既有雙重防線同一套處理。
3. **預算控管用 Core 現成的 `PromptBudget`**（共用標準原則，不再自寫截斷）：
   - 模型 context 20480 token、輸出上限 768；先組基礎 prompt（問題欄位＋對話史＋新問題＋system），
     報告全文填「剩餘預算」，超出時**從報告尾端截斷**（與批次深入分析同一策略，
     PromptBudget 註解明載）並在圍欄註明「（報告過長已截斷）」——不能讓 AI 以為看到的是全文。
   - 另設報告佔用上限常數（建議 8,000 token）：不是有預算就填滿——地端模型 prefill
     一萬多 token 要數十秒，60 秒 timeout 會開始不夠；8k 夠涵蓋絕大多數報告，
     延遲仍可控。常數集中一處，之後換更快的硬體只調一個數字。
   - 既有 `.Truncate(1500)` 的樣本截斷改由同一套預算邏輯統管（先砍報告、再砍樣本）。
4. 順帶效益：#2 的語言尾端提醒在長 context 下更重要（指令被稀釋），兩項同批實作正好互補。

**影響檔案**：`AiController.cs`、`AiInsightService.cs`（ChatAsync 簽章＋prompt 組裝）、
`IRecordQueryService`（GetReport 已存在，無需新端點）。
**測試影響**：AiInsightService 補「報告過長截斷＋標註」「無報告日照常運作」案例。
**風險**：中低。延遲上升是主要代價（prefill 變長），以 8k 上限與現有 60 秒 timeout 控住；
一次打不到就靜默降級的既有原則不變。

### #12 「清除重來」按鈕加圖示

**現況**：`#chat-clear` 是 RecordDetail.cshtml 的靜態純文字按鈕。
sprite（wwwroot/img/icons.svg）**已有** `arrow-counterclockwise` 符號，不用新增圖。

**方案**：cshtml 按鈕內前置
`<svg class="lf-icon"><use href="/img/icons.svg#arrow-counterclockwise"></use></svg>`
（icons.svg 檔頭注釋的標準用法）。chat-panel.js 以 `cloneNode(true)` 重綁事件會連子節點一起複製，
不受影響。

**影響檔案**：`RecordDetail.cshtml` 一處。零風險。

### 附錄：#5／#6 統一性說明（為何「統一」統一到這裡為止）

#### 系統裡的兩套層級是不同性質的東西

| | 問題嚴重度 | 日風險等級 |
|---|---|---|
| 值 | Critical/High/Medium/Low（嚴重/高/中/低） | 高/中/低 |
| 掛在 | 單一問題（事件簽章） | 主機×日期的分析紀錄 |
| 誰算的 | 規則層逐問題標定 | 批次分析綜合判定（規則命中＋趨勢異常＋**關聯訊號**） |
| 落在哪 | 分析紀錄的 TopIssues | 分析紀錄本身＋報告 txt 全文 |
| 設定頁勾選影響 | ✅（SiteHidden 全站過濾） | ❌（證據層，事後不可改寫） |

**日風險不是嚴重度的加總**：一天被判「高風險」可能是因為攻擊鏈/故障鏈的關聯訊號，
而不是任何單一問題的嚴重度；把 Medium 問題從畫面藏掉之後，「這天還算不算中風險」
沒有可靠的重算方法——除非把整套批次判定邏輯搬進 Web 查詢層重跑一次。
那會造成：(1) 兩份判定邏輯遲早漂移；(2) 畫面數字與報告 txt、已存檔/已列印的報表對不上，
違反「報告是證據」的誠實原則；(3) 待辦（掛在高＋中風險日上）跟著漂移，處理歷程對不回去。
所以統一的邊界劃在：**問題層級的東西全站統一過濾；日層級的東西全站統一不動**——
兩套各自內部一致，畫面文案負責讓人分得出是哪一套。

#### 處理狀態也有同構的兩層

- **日層級**（RecordHandling）：整天的處理狀態，儀表板待辦 KPI 用它（母體＝高＋中風險日）。
- **問題層級**（IssueHandling）：詳情頁逐問題的狀態，含「低風險預設不處理」「已知雜訊自動判讀」等推導。

報表 #6 的「處理進度」採日層級，理由同上一節的統一原則：全站已存在的跨頁處理指標
（儀表板待辦）是日層級，報表沿用同一套 `GetTodo` 規則，儀表板 KPI、報表占比圖、
下鑽出去的清單筆數三處才會是同一個數字。問題層級若拿來做全站占比，
分母會被推導狀態與 #5 的顯示設定牽動（藏掉的層級要不要算？），數字站不住。
詳情頁的已處理/未處理計數器維持頁內視角，不與全站指標混用。

### 整體風險與相容性

- **既有部署升級**：SystemSettings 新欄位皆有預設值（AdAuthEnabled=false），
  blob 反序列化向下相容；行為在管理者主動開啟前完全不變（與該類別既有原則一致）。
- **#5 是唯一改變既有數字呈現的項目**（Locked 模式的統計會變少），需在版本說明明講。
- **平台**：#9 的 System.DirectoryServices 僅 Windows——專案目標框架本就是 net8.0-windows，無影響。
- **測試基準**：現有 804 綠；各批次完成後全量跑一次。

---

## 2026-07-27 — 營運強化與主機停用隱藏規劃 OPS-HARDENING-PLAN（原 docs/OPS-HARDENING-PLAN.md）

> 2026-07-27 規劃版。三項待決策已由使用者依建議定案（遵循定案 13／停用主機處理狀態編輯一併鎖定／
> handling_log 與 perm_changes 本輪不清）；P1-2 排序下推的範圍決策使用者選擇「加欄位，做到底」。
> **執行進度**：批次 1（N-1、P0-2、文件修正）、批次 2（P0-1 SchemaUpgrader、
> lf_log_lines.created_at、P0-3 清理與設定頁）、批次 3（P0-4 SQL 重試、P0-5 LF_CRYPTO_KEY）、
> 批次 4（P1-2 分頁下推）、批次 5（P1-3 Windows Service＋README 部署文件、P1-4 export 清理/版本號/CI）
> 已全數完成並測試通過（見底部「執行記錄」）。本規劃案 P0/P1 範圍已全部落地，P2 維持 backlog
> （**2026-07-28 補記**：P2 三項——NetIQ 接線／EVTX 離線匯入／伺服器端 CSV 匯出——已轉入
> docs/BACKLOG.md）。
> 範圍：P0 營運債（schema 升級、dev 金鑰封鎖、log 清理、SQL 重試、加密金鑰來源）、
> P1（查詢分頁下推、Web 部署文件、營運小項）、以及新需求「主機停用後隱藏歷史資料」。
> 本文所有「現況」描述均已對照 2026-07-27 的原始碼逐一驗證，file:line 為當日位置。

### 0. 結論總覽

| 項目 | 建議 | 風險 | 依賴 |
|---|---|---|---|
| N-1 主機停用隱藏 | `VisibilityService` 單點加 `Active` 過濾 | 低（單點、可逆） | 無 |
| P0-1 schema 升級 | **遵循既有定案 13：自製冪等 DDL**，不採 EF Migrations（與原提案不同，見 §2） | 中 | 無（但 P0-3、P1-2 依賴它） |
| P0-2 dev 金鑰封鎖 | `Validate()` 加已知 dev 值黑名單 | 低 | 無 |
| P0-3 lf_log_lines 清理 | 批次啟動時 Prune＋SystemSettings 新欄位＋設定頁 | 低 | P0-1（需加時間戳欄） |
| P0-4 SQL 重試 | `EnableRetryOnFailure`＋包 `EfJsonBlobStore.Mutate` 的交易 | 中（交易點已證實存在） | 無 |
| P0-5 加密金鑰 | `LF_CRYPTO_KEY` 環境變數＋解密雙金鑰 fallback | 低 | 無 |
| P1-2 分頁下推 | 新增 `QueryPage`；可下推條件推到 SQL、殘餘條件記憶體驗證 | 中 | P0-1（稽核時間欄） |
| P1-3 Web 部署 | `UseWindowsService()`＋README 部署章節 | 低 | 無 |
| P1-4 小項 | export 清理／版本號／CI 一次帶掉 | 低 | 無 |

**需要決策的三點**（建議已列，定案後才動工）：
1. **P0-1 與 DB-PLAN 定案 13 衝突**：定案 13（2026-07-24）明文「不用 EF Core Migrations、採自製冪等 DDL」。本文建議遵循定案 13；若要改採 EF Migrations 應明文推翻該定案並更新 DB-PLAN。
2. **N-1 停用主機的處理狀態編輯是否一併封鎖**：單點過濾方案下會自然封鎖（404），建議接受；若要「唯讀可看」需另開例外，複雜度上升。
3. **P0-3 各 log key 的保留政策**（§4 表），特別是 `handling_log` 與 `perm_changes` 建議本輪**不**清理。

### 1. N-1 主機停用後隱藏歷史資料（新需求）

#### 1.1 需求語意

主機 `Active=false`（管理頁手動停用或 Sentinel 移除觸發的系統停用）後：
- 歷史紀錄頁（明細／依主機／依日期彙總）不再出現該主機的任何紀錄；
- 儀表板所有計數（總主機數、風險日、類別卡、排行、群組風險、待辦）不計入；
- 報表（KPI、趨勢、排行、簽章查詢）不計入，含「前期比較」的分母；
- 資料**只保留在資料庫**，不刪除；重新啟用後全部復原（完全可逆）。

#### 1.2 現況盤點（已驗證）

- `VisibilityService.GetVisibleHostIds()`（`LogForesight.Web/Services/VisibilityService.cs:55`）**完全不看 `Active`**——停用主機今天在所有查詢中照常可見。
- 所有紀錄查詢都經過 `RecordRepository.Query/GetOne`（`Repositories/RecordRepository.cs:44,58`），而它強制以 `GetVisibleHostIds()` 交集——**單一咽喉點存在**。消費端：`RecordQueryService`（含 ClusterSignatures）、`DashboardService`、`ReportService`、`HandlingService`（待辦推導自傳入的 records）。
- 主機下拉選單 `HostsController.cs:37` **已經**過濾 `h.Active`；儀表板的無回報計數（`DashboardService.cs:160`）與群組風險（`:178`）也已看 `Active`。唯 `TotalHosts`（`:55`）與紀錄類統計未過濾。
- 墓碑列（合併來源）也是 `Active=false`（`JsonHostStore.cs:122`），但其歷史**必須**持續經由存活主機可見——`RecordRepository.VisibleHostKeys()`（`:77`）是從可見主機出發做別名展開，墓碑不必自己在可見集合內。
- 批次端 `NetiqHostList.Listed`（`Core/Models/NetiqHostList.cs:18`）已要求 `Active`——停用主機不會再產生新資料，批次不需要改。
- 主機管理頁 `HostAdminService.GetHosts` **不經過** VisibilityService，`inactive` 篩選 chip（`HostAdminService.cs:123`）續存——管理者仍看得到停用主機本身（這正是「資料還在」的入口）。

#### 1.3 方案

**方案 A（建議）：`VisibilityService.GetVisibleHostIds()` 單點排除 `Active=false`**

```
// GetVisibleHostIds() 兩個分支（ViewAll 與群組授權）都改為只納入 h.Active 的主機
```

- ViewAll 分支（`VisibilityService.cs:64`）與群組授權分支（`:93`）各加 `.Where(h => h.Active)`。
- 這是 WEB-SPEC §7.1 明文的「不可繞過的最後防線」，語意本來就是「這台主機的資料你現在看不看得到」——把「停用」納入正是這個抽象該管的事。

**方案 B（不建議）：各消費端自行過濾**——觸點至少 6 處（RecordQueryService×4、Dashboard、Report），且未來新查詢頁忘了加就漏，正是 RecordRepository 註解裡「散落各 Service 遲早有人忘」要避免的形狀。

#### 1.4 方案 A 的全影響面（逐點確認）

| 消費端 | 影響 | 評估 |
|---|---|---|
| `RecordRepository.Query/GetOne` | 停用主機的紀錄自動消失（含 HostId=0 舊紀錄——名稱 fallback 的名單同樣來自可見集合） | ✅ 正是需求 |
| `RecordRepository.VisibleHostKeys` | 從「可見（=啟用）主機」展開墓碑，墓碑歷史照常歸戶到存活主機 | ✅ 不受影響 |
| 儀表板 `TotalHosts`／類別卡／排行／風險日計數 | 全部自動排除 | ✅ 正是需求 |
| 報表 KPI／趨勢／排行／前期比較 | 全部自動排除，分母一致 | ✅ 正是需求 |
| `HandlingService` 待辦（`GetTodo` 吃已過濾 records） | 停用主機的待辦從儀表板消失 | ⚠ 語意副作用 1（見下） |
| `RecordQueryService.GetHostDetail`（`:363` EnsureVisible） | 停用主機的主機詳情頁回 404 | ✅ 一致（管理頁仍可看主機本身） |
| `HandlingService`（`:447` EnsureVisible） | 停用主機的處理狀態不能再編輯（404） | ⚠ 語意副作用 2 |
| `PermissionChangeService`（`:91,:131`） | 停用主機的待確認權限異動隱藏、不計入儀表板 pending 數 | ⚠ 語意副作用 3 |
| `HostsController` 下拉、Silent 計數、GroupRisk | 原本就過濾 Active，變成雙重過濾 | ✅ 無害 |
| 主機管理頁（HostAdminService） | 不經 VisibilityService，完全不受影響 | ✅ 需求要的「資料入口」 |
| 稽核頁（AuditQueryService） | 不以主機為軸過濾，停用主機相關的**操作稽核**仍可見 | ✅ 建議維持（稽核是人的操作紀錄，不是主機資料；隱藏反而違反稽核完整性） |

**語意副作用（需求確認，建議全部接受）**：
1. 停用當下**處理中／逾期的待辦立即從儀表板消失**——主管視角的未結案數會下降。替代解（停用前擋「還有未結案」）會把停用變成流程，建議不做，改在管理頁停用時的確認文案提醒。
2. 停用主機的處理狀態**連編輯都不行**（不只是看不到）。要恢復操作＝先重新啟用。
3. 停用主機的權限異動確認會懸置（不會過期也不困擾任何人；重新啟用後回來）。

#### 1.5 測試

- `VisibilityServiceTests`：停用主機不在可見集合（ViewAll 與群組授權兩分支各一案）；重新啟用後回來。
- 新增整合案：停用主機後 `RecordQueryService.Search` 不含其紀錄；`DashboardService.GetSummary` 的 `TotalHosts`／風險日計數排除；墓碑（合併來源）的歷史仍經存活主機可見（防回歸——這是本改動最容易誤傷的點）。
- `HostAdminServiceTests`：`inactive` 篩選不受影響（既有案續跑即可）。

### 2. P0-1 資料庫 schema 升級機制

#### 2.1 現況（已驗證）＋與既有定案的衝突

- schema 全靠 `EnsureCreated()`（`StorageFactory.cs:60`），對既有 DB 完全不動，屬實。
- **但 DB-PLAN.md「Schema 升級機制（定案 13，2026-07-24）」已明文決策**：屆時採**自製冪等 DDL**（開機檢查→缺什麼補什麼），**不用 EF Core Migrations**——理由是雙 provider migration 歷史的長期維護成本，以及自製 DDL 更貼近現有「EnsureCreated 全有全無」的心智模型。
- 原提案（EF Migrations＋baseline）與定案 13 直接衝突。

#### 2.2 建議：遵循定案 13，落實「SchemaUpgrader（自製冪等 DDL）」

推翻三天前的正式定案需要新事實；目前沒有——近期實際需要的 DDL 只有兩件小事（P0-3/P1-2 要在 `lf_log_lines` 加時間戳欄、可能加索引），冪等 DDL 完全夠用，EF Migrations 的 baseline 判斷／雙 provider 驗證成本反而更高。

**設計**：
- 新增 `LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs`：`Upgrade(LfDbContext ctx)`，在 `StorageFactory.GetDbFactory` 的 `EnsureCreated()` 之後呼叫（同一個 `_schemaLock` 內，批次與 Web 都會走到）。
- 內容為一串**冪等步驟**：每步「檢查（查 information_schema／PRAGMA table_info）→ 缺才補（ALTER TABLE ADD COLUMN／CREATE INDEX IF NOT EXISTS）」。Sqlite 與 SqlServer 的存在性檢查語法不同，以 provider 分支各寫一句（步驟少，不值得抽象層）。
- 一張 `lf_schema_version`（key-value 一列）**不是必要**：冪等檢查本身就是狀態，不引入版本號心智負擔；若步驟多到需要跳過已執行者再考慮。
- 每步記 log（`[SQL] schema 升級：lf_log_lines 補 created_at 欄`），失敗顯性拋出（沿用 `StorageFactory.cs:66` 的 fail-fast）。
- 測試：合約測試新增「舊 schema 的 Sqlite 檔（手工 CREATE 不含新欄）→ Upgrade → 欄位存在且可寫讀」。

**代價**：每一次 schema 變更都要手寫一步 DDL＋檢查。以本專案的變更頻率（定案 13 的判斷依據）可接受。

### 3. P0-2 已提交的公開 JWT 金鑰無 Production 封鎖

現況屬實：`LogForesight.Web/appsettings.json:45,60` 是公開已知的 `SecretKey` 與 serverAdmin `PasswordHash`；`WebAppSettings.Validate()`（`Configuration/AppSettings.cs:31`）擋 Production+Stub、擋短金鑰，但不擋「帶已知 dev 值上 Production」。

**作法（維持原提案方案 A）**：
- `WebAppSettings` 加私有常數清單 `KnownDevSecrets`（現行 appsettings.json 的 SecretKey 與 PasswordHash 兩個字串）。
- `Validate(isProduction)` 內：`isProduction` 且 `Jwt.SecretKey`／`Auth.ServerAdmin.PasswordHash` 命中清單 → 加入 errors，訊息指引 `Jwt__SecretKey`／`Auth__ServerAdmin__PasswordHash` 環境變數（與 appsettings.json:17-18 的既有註解一致）。
- 非 Production 不擋——本機測試合法使用這組值。
- 測試：`WebAuthTests` 比照既有 Stub 檢查案，新增「Production＋dev SecretKey → 啟動失敗」「Production＋覆寫後的值 → 通過」。
- 維護規則寫進常數旁註解：**未來再提交任何測試金鑰，必須同步加入此清單**（這是方案已知的殘餘風險）。

### 4. P0-3 lf_log_lines 無限成長

現況屬實：`EfJsonLogStore`（`Core/Persistence/Sql/EfJsonLogStore.cs`）只有 Append/Read；批次的既有 Prune 在 `Program.cs:445`（分析紀錄）；稽核查詢全撈記憶體過濾（`JsonAuditLogStore.cs:60,94`）。

#### 4.1 資料層（依賴 §2 的 SchemaUpgrader）

- `lf_log_lines` 加 `created_at`（datetime，NULL 允許——既存列無值）＋ `(log_key, created_at)` 索引，由 SchemaUpgrader 補。
- `EfJsonLogStore.AppendLine` 寫入 `created_at = DateTime.Now`。
- 新增 `int Prune(DateTime cutoff)`：`DELETE WHERE log_key=@key AND created_at < @cutoff`（SQL 端整批刪，不撈回記憶體）。**`created_at IS NULL` 的既存列不刪**——無法斷定年代，寧可留著（它們是有限存量，隨保留期自然變成少數）。
  - 替代（不建議）：從行內 JSON 抽時間戳逐行判斷——各 key 的 JSON 結構不同（AuditEntry.OccurredAt、batch run 各自欄位），逐 key 寫解析器且全撈記憶體，違反本項的初衷。

#### 4.2 保留政策（per-key，需求確認）

| log key | 政策 | 理由 |
|---|---|---|
| `batch_runs`、`batch_run_logs` | `RunLogRetentionDays`（預設 90） | WEB-SPEC §11-6 既定規劃 |
| `import_logs` | `RunLogRetentionDays` | 同屬執行歷程 |
| `audit` | `AuditRetentionDays`（預設 730） | WEB-SPEC §11-6 既定規劃 |
| `handling_log` | **本輪不清理** | 處理歷程是業務敘事（「為何當時不處理」），與稽核不同軸；要清理應獨立決策 |
| `perm_changes` | **本輪不清理** | 有「待確認」狀態機，逐筆確認前刪除等於湮滅告警 |

#### 4.3 設定與觸發

- `SystemSettings`（`Core/Models/SystemSettings.cs`）加 `RunLogRetentionDays=90`、`AuditRetentionDays=730`；驗證下限（如 ≥7／≥90）在 `SystemSettingsService.Save`，比照 `RetentionDays` 的既有防呆（`SystemSettingsService.cs:62`）。
- `/admin/settings` 設定頁補兩欄（DTO：`SettingsDtos.cs`）——**使用者要求保留天數可在 Web 設定**，與現行 `RetentionDays` 同頁同機制。
- 觸發點：批次 `Program.cs:445` 既有 Prune 旁，依系統設定逐 key 呼叫 `EfJsonLogStore.Prune`。沿用「排程屬批次、Web 不養常駐工作」的既定架構；批次長期沒跑則 Web 端照樣成長，但那本身是 Runs 頁要抓的異常（原提案已載明，接受）。
- 測試：Prune 契約測試（Sqlite）——cutoff 前後、NULL 列不刪、不同 key 互不影響。

### 5. P0-4 SQL 無暫時性錯誤重試

現況屬實：全案無 `EnableRetryOnFailure`。**且原提案的「需檢查交易使用點」已證實命中**：`EfJsonBlobStore.Mutate` 使用 `ctx.Database.BeginTransaction()`（`Core/Persistence/Sql/EfJsonBlobStore.cs:46`）——這是所有 blob store（hosts/users/settings/…）的共用寫入路徑，execution strategy 與使用者自開交易不相容，不處理會在啟用重試後直接拋 `InvalidOperationException`。

**作法**：
- `StorageFactory.GetDbFactory` SqlServer 分支：`UseSqlServer(cs, o => o.EnableRetryOnFailure(maxRetryCount: 5))`。Sqlite 不加。
- `EfJsonBlobStore.Mutate` 的交易段改為：

```csharp
var strategy = ctx.Database.CreateExecutionStrategy();
strategy.Execute(() => { using var tx = ctx.Database.BeginTransaction(); ...; tx.Commit(); });
```

  - 無重試 provider 下 `CreateExecutionStrategy()` 回傳 NonRetrying 策略、行為不變——Sqlite 測試路徑照常，**不需要**分支。
  - 注意：`Execute` 內的委派必須可整段重放（冪等）。`Mutate` 本來就是「讀→改→寫＋樂觀鎖重試」的迴圈，重放安全；需確認委派內沒有捕捉外部可變狀態（實作時逐一檢視）。
- 全案再 grep 一次 `BeginTransaction|TransactionScope` 收尾（目前僅此一處，文件註 `WEB-SPEC.md:824` 同步更新）。
- 測試：既有 `EfJsonBlobStore` 契約測試在 Sqlite 上驗證包裝後行為不變（樂觀鎖衝突重試案續跑）。SqlServer 的實際重試行為無法在 CI 重現，靠 code review＋正式環境 log 觀察（每次重試 EF 會記 warning）。

### 6. P0-5 CryptoHelper 內嵌 AES 金鑰

現況屬實：`Core/CryptoHelper.cs:23` 內嵌金鑰，保護 `Sentinel.PasswordEnc` 與 `SystemSettings.AiApiKeyEnc`；類別註解自承混淆、並預告「日後改環境變數，介面不必變」。

**作法（原提案方案 A＋輪替細節）**：
- 靜態建構時讀 `LF_CRYPTO_KEY`（base64、必須恰為 32 bytes，格式錯誤→拋例外 fail-fast，不靜默退回）；未設定→沿用內嵌金鑰＋記一次 WARN（「正式環境建議設定 LF_CRYPTO_KEY」）。
- **解密雙金鑰 fallback**：`Decrypt` 先用現用金鑰，失敗（CryptographicException）再試內嵌舊金鑰——這讓「設定 LF_CRYPTO_KEY 當下、DB 裡還是舊密文」的過渡期不中斷；任何一次重存（管理頁儲存 Sentinel／AI 設定）就換成新金鑰密文。`Encrypt` 永遠只用現用金鑰。
  - 不做 `enc:v2:` 新前綴——金鑰換了但演算法沒換，前綴語意是「格式」不是「金鑰版本」；雙 key try 的成本可忽略（低頻操作）。
- 批次與 Web 同機共用同一把機器層級環境變數（README 部署章節寫明，見 §8）。
- 測試：`SystemSettingsService` AI 金鑰加密路徑（原清單的測試補強項）與 CryptoHelper 單元測試（env 金鑰加解密 round-trip、舊密文 fallback 解密、壞 base64 fail-fast）一起補。環境變數用 `SetEnvironmentVariable` 注入測試域需注意並行——建議 CryptoHelper 金鑰解析抽成 `internal static` 可注入函數，測試不動真 env。

### 7. P1-2 查詢先全撈再記憶體分頁

現況屬實（全部驗證）：
- `IAnalysisRecordQuery.Query` 無分頁（`Core/Persistence/IAnalysisRecordQuery.cs:53`）；`RecordQueryService` 記憶體 Skip/Take（`:127,:204`）。
- `EfAnalysisRecordStore.Query`（`Sql/EfAnalysisRecordStore.cs:174`）只下推日期／風險／host id 粗篩；**category/severity/eventId/source 全在記憶體**（`RecordFilterMatcher`）；`lf_top_issues` 有寫入（`:77-88`）但查詢端從未使用。
- 稽核 `JsonAuditLogStore.Query` 全表 `ReadAll()` 再過濾（`JsonAuditLogStore.cs:60`）。

**作法（增量、不動既有介面語意）**：

1. **新方法不動舊的**：`IAnalysisRecordQuery` 加 `PagedRecords QueryPage(RecordQueryFilter filter, int page, int pageSize)`；既有 `Query` 保留給批次與不分頁呼叫端。JSONL 已退役，只有 EF 一個實作要寫。
2. **下推層次**（EfAnalysisRecordStore）：
   - 已下推：日期、風險、host id。
   - 新下推：category／severity／eventId／source 以 `lf_top_issues` 的 `EXISTS` 子查詢（維度表當初就是 filter-only 設計，索引已建）。
   - **不可下推的殘餘**：HostId=0 舊列的名稱比對（刻意留在記憶體，`EfAnalysisRecordStore.cs:190-195` 的 collation 理由）、以及 `RecordQueryService` 的 Statuses/Overdue 過濾（狀態由 handling 資料推導，DB 不知道）。
   - 策略：**無殘餘條件時** SQL 端 `ORDER BY + OFFSET/FETCH` 真分頁；**有殘餘條件時**退回「SQL 過濾＋全窗撈回→記憶體殘餘過濾→分頁」，並在 log 標示走了哪條路。這保住正確性（Total 數字不能錯），把最常見的查詢（無狀態篩選）變快。
   - 語意守門：契約測試以同一組資料比對 `Query`（記憶體過濾）與 `QueryPage`（下推）結果逐位一致——這是把當初「記憶體與 SQL 語意一致」的設計原則搬到新路徑。
3. **排序**：清單頁的「風險→關聯→日期」排序中「有無關聯訊號」在 JSON 內。下推排序需把 `RiskRank` 對應到 `risk_level` 欄（可 CASE WHEN）＋日期；關聯訊號項只能近似或加欄。建議本輪排序下推做「風險→日期」，關聯訊號從排序鍵**暫時退位**（畫面仍顯示圖示）——或接受有殘餘時的全窗路徑。實作時擇一，先與使用者確認清單頁排序是否可簡化。
4. **稽核**：`IJsonLogStore` 加 `ReadPage(skip, take, desc)`（`(log_key, seq)` 索引已在）；date range 下推用 §4 的 `created_at` 欄（與 P0-3 同一次 schema 變更）。`JsonAuditLogStore.Query` 改成：條件全空時走 ReadPage 快路徑；有條件時仍撈範圍內（以 created_at 預篩）再記憶體過濾。`Count`（登入失敗卡）同樣以 created_at 預篩。

### 8. P1-3 Web 部署文件 ＋ P1-4 營運小項

#### P1-3（維持原提案方案 A）
- `LogForesight.Web.csproj` 加 `Microsoft.Extensions.Hosting.WindowsServices`，`Program.cs` 加 `builder.Host.UseWindowsService()`（console 啟動無影響）。
- README 新增「Web 部署」章節：`sc create` 範例、Kestrel HTTPS（appsettings Kestrel 區段綁 pfx，憑證手動更新入 runbook）、環境變數清單（`ASPNETCORE_ENVIRONMENT=Production`、`Jwt__SecretKey`、`Auth__ServerAdmin__PasswordHash`、`LF_CRYPTO_KEY`）、防火牆限縮、與批次同機的目錄配置。

#### P1-4（已驗證現況）
- **export 清理**：`FileReportSink` 寫 `export/*.txt`（`Program.cs:286`），全案無任何清理（已 grep 證實）。在 `Program.cs:445` 既有 Prune 旁依 `RetentionDays` 同步清理（以檔名日期或 LastWriteTime 判斷；檔名有固定日期前綴，用檔名較準）。
- **版本號**：無 `Directory.Build.props`（已證實）——新增並統一 `<Version>`；`--selftest` 輸出與 Web 頁尾顯示。
- **CI**：無任何 workflow（已證實）——最低限度一條 `dotnet build && dotnet test`（GitHub Actions 或本機 script，單人開發先求提交必跑測試）。

### 9. 測試補強與文件修正（隨對應批次帶）

測試（優先順序照原清單）：
1. `PermissionFilter` 403＋稽核（WEB-SPEC §12 明文要求，目前不存在）——獨立可先做。
2. `SystemSettingsService` AI 金鑰加密路徑——隨 P0-5。
3. `ImportService` 協調器、`AIService` JSON 容錯——次優先。
4. 其餘 Web 服務層依動到哪補到哪（本計畫會動到 `DashboardService`／`AuditQueryService`，隨 N-1／P1-2 補）。

文件修正（first batch 順手）：
- `NETIQ-API-PLAN.md` 標頭「尚未實作」→ 改為現況（SentinelClient/probe 已完成、待真實 probe 輸出）。
- `PLAN.md:288` DPAPI 段落 → 依 §6 定案改寫。
- `WEB-SPEC.md` §13 Phase 5「SQL 暫緩」／§12 引用已刪的 `JsonlAnalysisRecordStoreTests` → 更新；`:824` 的 Mutate 交易描述隨 §5 更新。
- `NETIQ-WEB-CONFIG-PLAN.md:116` `/admin/sentinels` → `/admin/netiq`。
- `DB-PLAN.md` 定案 13 段落 → 標註「已於本計畫落實」（若定案維持）。

### 10. 建議實作順序

```
批次 1（無依賴、風險低）：N-1 主機停用隱藏 ＋ P0-2 dev 金鑰黑名單 ＋ 文件修正
批次 2（schema 基礎）   ：P0-1 SchemaUpgrader ＋ lf_log_lines.created_at ＋ P0-3 清理與設定頁
批次 3（連線與金鑰）   ：P0-4 EnableRetryOnFailure（含 Mutate 改造）＋ P0-5 LF_CRYPTO_KEY
批次 4（效能）         ：P1-2 QueryPage 下推 ＋ 稽核 ReadPage（用批次 2 的欄位）
批次 5（部署與雜項）   ：P1-3 Windows Service＋README ＋ P1-4（export 清理/版本號/CI）
```

依賴關係：批次 2 是批次 4 的前置；其餘互相獨立。每批次一個 feature branch、跑全測試後合併。

P2（NetIQ 接線／EVTX 離線匯入／伺服器端 CSV 匯出）維持 backlog 不排；伺服器端 CSV 匯出屆時與 P1-2 的 QueryPage 同路徑實作（原清單的建議正確——匯出不該再走全撈）。

### 11. 執行記錄

#### 批次 1（2026-07-27，已完成）

- **N-1**：`VisibilityService.GetVisibleHostIds()`（`LogForesight.Web/Services/VisibilityService.cs:63`）在 ViewAll 與群組授權兩分支之前先以 `.Where(h => h.Active)` 過濾主機清單，單點涵蓋全部查詢型 Service。墓碑列的歷史經 `RecordRepository.VisibleHostKeys` 從存活主機展開，不受影響（該處直接用 `_hosts.GetAll()`，不經過 VisibilityService 的過濾）。新增 3 條測試（`VisibilityServiceTests.cs`）。
- **P0-2**：`WebAppSettings`（`LogForesight.Web/Configuration/AppSettings.cs`）新增 `KnownDevSecrets` 黑名單，`Validate(isProduction)` 命中即 fail-fast。新增 4 條測試（`WebAppSettingsValidationTests`，`WebAuthTests.cs`）。
- 文件修正：NETIQ-API-PLAN.md、PLAN.md（DPAPI 段落改寫為實際 CryptoHelper 方案）、WEB-SPEC.md（§13 Phase 5、§12 死測試引用）、NETIQ-WEB-CONFIG-PLAN.md（路由歷史註記）。

#### 批次 2（2026-07-27，已完成）

- **P0-1 SchemaUpgrader**：新增 `LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs`，於 `StorageFactory.GetDbFactory` 的 `EnsureCreated()` 後呼叫。以 `pragma_table_info`/`pragma_index_list`（SQLite）與 `INFORMATION_SCHEMA.COLUMNS`/`sys.indexes`（SqlServer）判斷欄位/索引是否存在，缺才用 `ALTER TABLE`/`CREATE INDEX` 補。識別字組字串一律先組成區域變數再呼叫 `ExecuteSqlRaw`（避免 EF1002 內插字串警告，值本身皆為內部常數非外部輸入）。4 條測試（`SchemaUpgraderTests.cs`）：舊 schema 補欄位、補索引、既存列 CreatedAt 維持 null、新 schema 上重複執行冪等不拋例外。DB-PLAN.md 定案 13 段落已更新標註「已落實」。
- **lf_log_lines.created_at**：`LogLineRow` 新增 `DateTime? CreatedAt`（`LfDbContext.cs`），`EfJsonLogStore.AppendLine` 寫入時間戳記。
- **P0-3 清理**：`IJsonLogStore` 加 `int Prune(DateTime cutoff)`（`CreatedAt == null` 的既存列不刪）；`IBatchRunStore`／`IImportLogStore`／`IAuditLogStore` 各加 `int Prune(int retentionDays)` 委派至底層。**未**動 `IRecordHandlingStore`／`IPermissionChangeStore`（依決策，業務敘事與待確認狀態機本輪不清）。`SystemSettings` 新增 `RunLogRetentionDays`(90)／`AuditRetentionDays`(730)，`SystemSettingsService`／`SettingsDtos`／`/admin/settings` 頁（Settings.cshtml + settings.js）三處同步串接，Web UI 手動驗證過（瀏覽器實測存值 45→重新整理→讀回 45→改回 90）。批次 `Program.cs` 於既有 Prune 段落旁（`historyService.Prune` 之後）呼叫 `batchRunStore`／`ImportLogStore`／`AuditLogStore` 的 Prune，包在 try/catch 內不中斷主分析流程。7 條新測試（`EfJsonLogStorePruneTests.cs` 4 條、`SystemSettingsServiceTests.cs` 2 條、`FakeImportLogStore` 補介面實作）。

#### 批次 3（2026-07-27，已完成）

- **P0-4 SQL 重試**：`StorageFactory.GetDbFactory` 的 SqlServer 分支加 `EnableRetryOnFailure(maxRetryCount: 5)`；Sqlite 不動。`EfJsonBlobStore.Mutate` 原本的 `ctx.Database.BeginTransaction()` 與 execution strategy 不相容（啟用重試後會直接拋 `InvalidOperationException`），改為 `probe.Database.CreateExecutionStrategy().Execute(() => { using var ctx = _contextFactory(); ... })`——每次執行策略重試都用全新 `DbContext`，避免變更追蹤殘留上一次嘗試加入的列。Sqlite 上 `CreateExecutionStrategy()` 回傳 `NonRetryingExecutionStrategy`，對現有測試行為零影響（全量測試套件跑過，無回歸）。新增 1 條測試（`EfWebdataStoreTests.Mutate_遇到暫時性例外時自動重試並成功落地`，直接注入 `DbUpdateException` 驗證外層重試迴圈仍正常運作——未嘗試用雙 `DbContext` 模擬真實並發，因為 `EfSqliteFixture` 為了讓 in-memory DB 跨 context 保留內容而共用同一條實體連線，這與正式環境「不同連線」的並發語意不同，直接丟例外更精準穩定）。
- **P0-5 加密金鑰**：`CryptoHelper`（`LogForesight.Core/CryptoHelper.cs`）改讀環境變數 `LF_CRYPTO_KEY`（base64，需恰為 32 bytes），未設定時 fallback 內嵌金鑰並記一次 WARN；格式錯誤或長度不對一律 fail-fast（不靜默當作未設定）。`Decrypt` 現用金鑰解不開時自動退回內嵌金鑰再試一次，支援金鑰輪替過渡期（DB 裡舊金鑰時代密文仍解得開；任一次重新加密即換成新金鑰密文）。金鑰解析抽成 `internal static ResolveKey(string? envValue)` 純函數，`Encrypt`/`Decrypt` 的核心邏輯抽成 `internal static EncryptWith`/`DecryptWith`（接受金鑰參數）——測試藉此直接驗證各種情境，完全不碰真的環境變數（避免 xUnit 平行執行測試類別時互相干擾）。新增 12 條測試（`CryptoHelperKeyResolutionTests`）涵蓋：未設定/空白回內嵌金鑰、非法 base64 與長度不對 fail-fast、合法金鑰採用、指定金鑰往返、雙金鑰 fallback 成功、兩把都解不開時仍拋例外。另外補了原清單提到的 `SystemSettingsService` AI 金鑰加密路徑測試（4 條：加密存放不留明碼、DTO write-only 不外洩、留空沿用既有金鑰、ClearAiApiKey 清除）。
- **未做**：README 部署章節的 `LF_CRYPTO_KEY` 說明留給批次 5（P1-3）——那裡會一次寫完整的 Web 部署環境變數清單（`ASPNETCORE_ENVIRONMENT`／`Jwt__SecretKey`／`Auth__ServerAdmin__PasswordHash`／`LF_CRYPTO_KEY`），現在單獨補一小段之後還要重寫，不如一次到位；`CryptoHelper` 類別本身的 XML doc 已完整說明用途與設定方式。

#### 批次 4（2026-07-27，已完成）

範圍決策：使用者選擇「加欄位，做到底」——`lf_daily_records` 加 `has_correlation` 欄，讓問題查詢頁
清單排序（風險等級→有無關聯訊號→日期）三鍵全部可下推，而不是只做部分下推留下排序退位的妥協。

- **schema**：`DailyRecordRow` 加 `HasCorrelation`（bool，預設 false）；`EfAnalysisRecordStore.Append` 寫入時同步計算 `shaped.CorrelationAlerts.Count > 0`；`SchemaUpgrader` 補上既有 DB 的欄位升級步驟（`AddColumnIfMissing` 的簽章順手改為接受完整欄位定義字串，才能表達 `NOT NULL DEFAULT 0`，`created_at` 呼叫端同步補上明確的 `NULL` 後綴）。實測（見下）：真的在既有 dev DB 上啟動一次，log 確認補欄位成功、無錯誤。
- **查詢下推**：`EfAnalysisRecordStore` 抽出共用的 `ApplyPushableFilters`（`Query`／`QueryPage` 共用單點），新增 Category／MinSeverity／EventId／Source 以 `lf_top_issues` EXISTS 子查詢下推——這張維度表當初就是為 filter-only 設計，索引已建，此前查詢端從未用到。`Query()` 沿用既有記憶體排序＋分頁（批次與不分頁呼叫端用）；新增 `QueryPage(filter, page, pageSize)`：偵測資料庫是否存在 `HostId=0` 舊列（一次 `Any()` 查詢，有索引很便宜）——沒有就 SQL 端 `CASE WHEN`（風險等級）＋`HasCorrelation`＋`RecordDate` 三鍵排序＋`OFFSET/FETCH` 真分頁；有就退回「SQL 過濾＋整批撈回→記憶體排序＋分頁」，正確性優先。7 條契約測試（`RecordQueryTests.cs`）逐位驗證兩條路徑與 `Query()` 語意一致，含排序正確性、跨頁完整性、授權空集合語意、HostId=0 退回路徑。
- **稽核分頁**：`IJsonLogStore` 加 `ReadLines(from,to)`（不分頁窄化）與 `ReadPage(skip,take)`（全表真分頁）；`JsonAuditLogStore.Query` 完全無篩選條件時走 `ReadPage`（SQL 端分頁，不必先讀全表——稽核頁的預設檢視就是這個情境）；有任何篩選條件時以 `created_at` 範圍先在 SQL 端窄化候選集（沒有時間戳記的既存列一律視為候選，精確判斷交給記憶體端既有的 `Matches`），其餘欄位維持原本的記憶體過濾。`Count`（儀表板登入失敗卡）同樣改用範圍窄化。10 條測試（`JsonAuditLogStorePageTests.cs`）涵蓋兩條路徑與 null-CreatedAt 既存列的精確性。
- **Web 層串接**：`RecordRepository` 加 `QueryPage`（與 `Query` 共用 `ApplyVisibility` 授權過濾邏輯）；`RecordQueryService.Search` 依 `request.Statuses`／`request.Overdue` 是否有值分支——兩者皆無時走新的 `QueryPage` 快速路徑（只為**當頁**載入處理狀態，這是 2000 台規模下清單頁最常見瀏覽情境的效能關鍵路徑）；任一有值時退回既有「全撈→算處理狀態→篩選→排序→分頁」邏輯（該邏輯本身未改動，因為 Statuses/Overdue 依賴的 handling 資料不在 SQL 裡，天生無法只看某一頁）。`Search()` 先前**零測試覆蓋**，新增 7 條端到端測試（`RecordQueryServiceSearchTests.cs`，真串接 `EfAnalysisRecordStore`＋`RecordRepository`，不是重新實作簡化邏輯）涵蓋排序、分頁、處理狀態顯示、授權邊界、兩條路徑的篩選正確性。
- **手動驗證**：啟動真實 Web 服務對著既有 dev SQLite DB 跑——`/records` 頁確認明細排序/處理狀態/狀態篩選（慢速路徑）都正確；`/audit` 頁確認無篩選（快速路徑，56 筆分頁）與依動作篩選（慢速路徑，narrowing 到 12 筆）都正確；全程 0 個瀏覽器主控台錯誤、0 個伺服器錯誤 log；schema 升級 log 確認 `has_correlation` 在真實既有資料庫上補欄成功。

#### 批次 5（2026-07-27，已完成）

- **P1-3 Windows Service**：`LogForesight.Web.csproj` 加 `Microsoft.Extensions.Hosting.WindowsServices`，`Program.cs` 開頭加 `builder.Host.UseWindowsService()`——對一般 `dotnet run`／直接執行 `.exe` 完全無影響（實測瀏覽器對著本機啟動的站台走一輪，行為不變），只在被服務控制管理器啟動時才切換生命週期管理。
- **P1-3 README 部署文件**：新增「Web 部署」章節（`sc create` 服務範例、Kestrel HTTPS 憑證設定含環境變數覆寫密碼、正式環境必用環境變數清單彙整——含批次 3 延後至此的 `LF_CRYPTO_KEY` 說明、防火牆限縮、與批次同機的目錄配置範例）。
- **P1-4 export 清理**：新增 `LogForesight/Service/ExportReportPruner.cs`（獨立可測試類別，未寫成 Program.cs 的內嵌 local function——這是刪檔案的邏輯，值得有測試覆蓋）。依檔名固定的 `yyyy-MM-dd` 前綴（`RiskReportService.BuildFileName`）判斷是否超過 `RetentionDays`，比 LastWriteTime 更準；遞迴掃描涵蓋 NetIQ 多主機情境的 `export\{host}\` 子目錄。批次 `Program.cs` 在既有清理段落旁呼叫，同樣包 try/catch。10 條測試（`ExportReportPrunerTests.cs`）涵蓋邊界日、子目錄、格式不符略過、目錄不存在。
- **P1-4 版本號**：新增根目錄 `Directory.Build.props`（`<Version>1.0.0</Version>`，MSBuild 自動套用到所有專案，取代各自散落的預設 1.0.0.0）；`SelfTestRunner` 輸出標頭加版本號；Web `_Layout.cshtml` 側欄頁尾加版本顯示（實測 `--selftest` 印出「版本 1.0.0.0」、瀏覽器 DOM 確認頁尾顯示「v1.0.0.0」）。
- **P1-4 CI**：新增 `.github/workflows/ci.yml`，`windows-latest`（net8.0-windows 讀 Windows Event Log／AD，只能在 Windows 建置測試）跑 `dotnet build --configuration Release` ＋ `dotnet test --configuration Release --no-build`，push 與 PR 都觸發。單人開發先求「提交必跑測試」，不上 lint/覆蓋率/多環境矩陣。本機以 Release 組態實跑驗證過整個指令序列（804/804 通過）才寫進 workflow，不是憑空假設 CI 環境行為。

批次 1+2+3+4+5 合計新增 69 條測試，總數 735→804，全數通過；建置 0 警告 0 錯誤（Debug 與 Release 組態皆驗證過）。**本規劃案 P0＋P1 範圍已全部完成**，P2（NetIQ 接線／EVTX 離線匯入／伺服器端 CSV 匯出）維持 backlog，等對應觸發條件成立（真實 Sentinel probe 輸出／實際離線調查需求／P1-2 QueryPage 基礎已就位可隨時排）再開專案計畫（**2026-07-28 補記**：已轉入 docs/BACKLOG.md）。

---

## 2026-07-28 — WEB-FEEDBACK-2-PLAN：第二輪使用者回饋的規劃（原 docs/WEB-FEEDBACK-2-PLAN.md）

> 狀態：**全部實作完成（2026-07-28）**，885 個測試綠（含新增的 6 個回歸測試）、
> 關鍵頁面（風險日詳情、報表、規則維護、設定）瀏覽器實測通過。
> 決策 D1–D4 於 2026-07-28 定案，見「決策點（已拍板）」一節。
>
> 前一輪（本檔上方「2026-07-27 — WEB-FEEDBACK-PLAN」段）已全部完成；本輪回饋集中在
> **嚴重度層級、處理狀態一致性、報表圖表呈現**三個主題。
>
> 實作時與規劃的偏差（皆為澄清，非變更）：
> - **#1 RiskBasis／ElevatesDayRisk 不需要 SchemaUpgrader**：規劃時誤以為
>   `LogIssueSignature`／`DailyAnalysisRecord` 走 SQL 逐欄位持久化，實際上 SQL 後端
>   （`EfAnalysisRecordStore`）整筆序列化進 `ContentJson`，`lf_top_issues` 只是查詢用的
>   維度表（filter-only）。新增欄位隨 JSON round-trip 自動生效，無需資料庫遷移。
> - **#1 TrendAnalyzer 逃逸路徑**：升級判斷（High→原 Critical）不只發生在規則命中時，
>   也發生在「High 嚴重度問題頻率上升」這條路徑（`TrendAnalyzer.Escalate`）——
>   這條路徑原本會把嚴重度撞頂到 Critical、連帶讓當天判定高風險，B1 改用旗標時
>   一併在 Escalate 前記錄「原本是不是 High」以複製同一個效果，行為零改變。
> - **#1 CategoryAggregator 新增 ElevatesCount**：規劃未提及，實作中發現儀表板分類卡的
>   紅框顯著性（`criticalCount > 0`）三級化後恆為 0 會靜默失效，補上對應的旗標計數欄位。
> - **#6 RecordHandlingLog 免 SchemaUpgrader**：同 #1，處理歷程走 JSON blob，
>   新欄位（IssueKey/IssueLabel）直接生效。
> - **#6 批次時間戳改用共用值**：`SetIssueStatusBatch` 原本逐筆呼叫 `DateTime.Now`，
>   改成整批共用一個 `occurredAt`——前端 timeline 靠「同操作者＋同時間戳」分組收合，
>   逐次取值的微小時間差會讓分組失效。
> - **#1 Program.cs 遺漏點**：批次主控台的紅色警告橫幅（`criticalIssues` 篩選）原本
>   直接比對 `Severity==Critical`，B1 後改比對 `ElevatesDayRisk`，否則橫幅會失去逐項列出的內容。

### 批次分組（依相依與風險排序）

| 批次 | 項目 | 性質 |
|------|------|------|
| A | #2 設定頁未儲存提醒、#5 簽章查詢說明、#9 折行檢查、#10 報告全文整列可點、#14 原始訊息改名＋modal | 純前端小修，低風險，無相依（#14 先建 ui.js 通用 modal helper，#13 復用） |
| B | #3 圓餅圖改左圖右列、#4 移除全部 PNG 鈕 | 報表前端，低風險 |
| C | #12 對外三態、#6 歷程同步、#7 勾選與狀態拆欄、#8 已結案排序收合、#13 歷程限高＋modal 放大 | 處理狀態一致性，前後端，**#12 先做**（#6/#8 的顯示依賴三態定義）；#13 排在 #6 之後（放大檢視呈現的就是 D4 的逐筆歷程） |
| D | #1 嚴重度層級（B1＋「重大」標註）、#11 風險等級一致性 | 涉及批次分析核心與資料遷移，已拍板 B1 |

批次 C、D 各自內聚；A、B 可隨時穿插。建議順序 A → B → C → D。

### 決策點（已拍板，2026-07-28）

- **D1：採 B1 三級化＋日風險旗標，且旗標要在畫面上顯性標註**。
  全面停止產生 Critical，原 Critical 規則改為「高＋命中即列為高風險日」旗標，
  現行風險判定行為完全不變，畫面上嚴重度只剩高/中/低；**帶旗標的規則命中時，
  畫面另以「重大」標註呈現**（加強一致性的同時保留「這件事特別嚴重」的直覺），
  細節見 #1 方案第 6 點。
  （落選案備查：B2 僅顯示層合併——下鑽與統計會對不上；B3 維持四級只補文案。）
- **D2：#8 只做風險日詳情的重點問題**。問題查詢清單維持既有緊急程度排序
  （清單已有狀態篩選 chip 可用）。
- **D3：詳情頁計數器改為「已處理 N／處理中 O／未處理 M」**。
  已處理＝標成 resolved；處理中＝標成 in_progress；未處理＝從未標記
  （且非預設不處理/自動雜訊）或明確 open。不處理/誤報/已知雜訊仍三邊都不計
  （已有結論、不是待辦），實作見 #8 方案第 4 點。
- **D4：處理歷程逐筆詳實記錄，不做彙總列**。每個問題的每次狀態變更都要留下
  「誰、何時、對哪個問題、標成什麼狀態」的獨立紀錄；「攏統的彙總標記沒有意義」。
  實作見 #6 方案第 1 點（含 schema 變更與 timeline 顯示的分組呈現）。

### #1 嚴重度層級：設定與顯示多了一個「嚴重」

**現況與事實確認**：問題嚴重度是四級列舉 `IssueSeverity`（Low/Medium/High/Critical，
KnownIssueCatalog.cs:15），「嚴重」= Critical 的中文顯示名（format.js:45）。
**Critical 不是死碼，且是「高風險日」的直接判定依據**：

- 種子規則有多條 Critical：磁碟故障（Event 153/55）、WHEA 硬體錯誤、非預期關機 6008、
  安全日誌被清除 1102 等（KnownIssueSeed.cs）。
- 趨勢層頻率暴增會把 High 升級成 Critical（TrendAnalyzer.Escalate）。
- 批次的日風險判定：**有未抑制的 Critical 問題或 Critical 關聯訊號 → 當日=高風險**；
  只有 High 問題/趨勢異常/關聯訊號 → 中風險（LogAnalysisService.ComputeRuleBasedRisk，
  LogAnalysisService.cs:709）。

所以「實際上嚴重度只有高中低」是**觀察偏差**（Critical 事件本來就罕見），
不是程式多做了一級。但四級與日風險三級（高/中/低）字面撞在一起確實造成困惑
（#11 即其副作用），簡化有其價值。

**方案 B1（建議）：三級化＋「命中即列為高風險日」旗標**

核心想法：Critical 在系統裡真正的職責只有一個——「這條規則命中，當天就是高風險日」。
把這個職責顯性化成規則上的布林旗標，嚴重度就能安全地收斂成三級，
且**現行風險判定行為零改變**。

1. **Core／批次**：
   - `KnownIssueRule` 新增 `ElevatesDayRisk`（bool）；`CorrelationFinding` 同樣改為
     嚴重度 High＋旗標（CorrelationAnalyzer 中原 Critical 的組合）。
   - `ComputeRuleBasedRisk` 改看旗標：任一未抑制問題命中 `ElevatesDayRisk` 規則
     或關聯訊號帶旗標 → 高；其餘規則不變。
   - `TrendAnalyzer.Escalate` 升級封頂改為 High（原本可升到 Critical）。
   - `IssueSeverity` 列舉**保留 Critical 值不刪**——舊紀錄/舊規則反序列化不能爆；
     但所有新產出不再是 Critical。
   - SchemaUpgrader：規則表既有 Severity=Critical 的列 → Severity=High、
     ElevatesDayRisk=true（種子與使用者自訂規則一併遷移）。
2. **Web 讀取正規化（單一咽喉點）**：歷史紀錄裡已存的 Critical 在 DTO 映射時一律
   顯示為 High——落點放在 RecordQueryService/RecordStatsBuilder/ReportService 共用的
   嚴重度→DTO 轉換處（一個 helper，如 `SeverityDisplay.Normalize`），不在前端特判。
   報表類型分布的 `criticalCount` 於 DTO 端併入 `highCount`（DB 欄位保留不動，
   避免動 lf_record_categories schema）。
3. **設定**：`SystemSettings.UnhandledSeverities` 預設改 `{High, Medium}`；
   `ParseUnhandledSeverities` 把既有設定中的 "Critical" 正規化為 High（讀取時靜默轉換，
   既有部署不用手動改）。設定頁層級按鈕剩三顆。
4. **前端**：format.js `SEVERITY_ORDER`/`SEVERITY_NAMES`/`SEVERITY_VARIANT` 刪 Critical；
   charts.js `severityColors` 刪 Critical；reports.js 類型分布堆疊圖與表格欄「嚴重」移除；
   規則編輯器（Rules.cshtml:135）刪「嚴重」選項、新增「命中即列為高風險日」勾選
   （含說明文字：這正是原「嚴重」等級的實際作用）。
5. **不回改的部分**：舊報告 txt 全文中的 Critical 字樣是證據層，不回寫；
   AI prompt 中的嚴重度敘述同步改三級。
6. **「重大」標註（D1 拍板追加）**：旗標不只是後端判定依據，要在畫面上顯性呈現——
   使用者要一眼看得出「這條問題特別嚴重、是它讓今天變高風險日」：
   - **詳情頁重點問題列**：命中帶旗標規則的問題，在嚴重度徽章旁加「重大」徽章
     （danger 色系，tooltip：「命中重大規則——此類問題出現當日即列為高風險日
     （原「嚴重」等級）」）。資料流：TopIssueDto 增 `ElevatesDayRisk`（bool），
     RecordQueryService 組 DTO 時以 issue.RuleId 對照規則store 帶出。
   - **規則維護頁**：清單列與編輯表單都顯示這個旗標（編輯表單的勾選文字：
     「命中即列為高風險日（重大）」）；篩選 chips 增加「重大」快篩。
   - **跨主機同簽章查詢**（報表）：命中列同樣帶「重大」徽章——這個查詢正是
     「全環境共通重大問題」的主要排查入口。
   - format.js 新增 `elevatesBadge()` 之類的單點工廠，三處共用同一顆徽章
     （§8.2 顏色＋文字單一定義原則）。
   - 舊資料相容：歷史紀錄裡嚴重度仍是 Critical 的問題，讀取正規化成 High 時
     **一併視為帶「重大」標註**（Critical 本來就是這個語意），不會出現
     「舊的高風險日看不出誰是元凶」的斷層。

**影響檔案**：KnownIssueCatalog.cs、KnownIssueSeed.cs、TrendAnalyzer.cs、
CorrelationAnalyzer.cs、LogAnalysisService.cs、SchemaUpgrader、SystemSettings.cs、
SystemSettingsService.cs、RecordQueryService.cs、RecordStatsBuilder.cs、ReportService.cs、
RuleAdminService.cs、RuleDtos.cs、RecordDtos.cs（TopIssueDto/SignatureHitDto 加旗標）、
format.js、charts.js、reports.js、settings.js、rules.js、Rules.cshtml、Settings.cshtml 文案、
record-detail.js（重大徽章）。

**測試影響**（現有 879 綠）：TrendAnalyzerTests（升級到 Critical 的 case 改封頂 High）、
SelfTestRunner check「嚴重度升級為 Critical」、KnownIssueCatalogTests、
CategoryAggregatorTests、ReportServiceTests、RecordQueryServiceSearchTests（severity 篩選）、
SystemSettingsServiceTests、RuleAdminServiceTests；新增：旗標判定高風險日、
Critical 讀取正規化、設定遷移。

**風險**：中。動到批次判定核心，靠「旗標等價替換 Critical 判定」維持行為不變，
需要靠既有 RiskReportServiceTests/SelfTestRunner 驗證前後判定一致。

### #2 設定頁跳轉前提醒尚未儲存

**現況**：settings.js 無 dirty 追蹤；站台是 MPA（側欄連結都是整頁跳轉），
離開即丟失未儲存的修改。

**方案**：
1. settings.js 載入完成後對 `#settings-form` 監聽 `input`/`click`（層級與顯示按鈕是
   button toggle，不觸發 input 事件，需一併涵蓋）設 dirty 旗標；
   儲存成功後清除。
2. dirty 時掛 `beforeunload`（瀏覽器原生「確定要離開？」對話框）；MPA 下這一個 handler
   就涵蓋側欄跳轉、重新整理、關閉分頁，不需要攔截個別連結。
3. **排除**不屬於設定內容的欄位：AD 測試帳號/密碼、測試連線按鈕（測完就丟，不算未儲存）。

**影響檔案**：settings.js。可做成 ui.js 的通用 helper（`trackUnsaved(form, options)`）
供未來其他頁復用，本輪只掛設定頁。

**風險**：無。注意 severity/顯示模式按鈕與 AD checkbox 都要觸發 dirty。

### #3 圓餅圖改「左圖右文字條列」；#4 移除所有 PNG 鈕

**現況**：三顆占比圖（風險層級占比/受影響主機占比/處理進度）是 doughnut、
legend 隱藏、中心疊百分比；每張圖卡工具列有「表格」切換與「PNG」下載
（charts.js attachToolbar:144-180）。圓餅圖本來就沒有 XY 軸——使用者指的應是
「表格切換後變成一張表」的呈現不直覺，以及切換/下載鈕多餘。

**方案**：
1. charts.js 新增 `attachDoughnutLegend(container, { items })`：每列
   「色點＋名稱＋數值＋百分比」，列本身可點（沿用該分段的 drillTo URL，
   與點圖同一個下鑽目的地）。
2. Reports.cshtml 三顆占比卡的 body 改兩欄（左 `lf-chart--sm`、右條列；
   手機窄幅時上下堆疊——flex-wrap 即可）；reports.js 三個 render 函式改呼叫新 helper，
   **不再對這三張卡呼叫 attachToolbar**（表格切換與 PNG 一併消失；數字已在右側條列，
   表格模式失去存在意義）。中心百分比保留。
3. #4：attachToolbar 移除 PNG 下載鈕（charts.js:166-176 整段刪除），
   趨勢/類型分布/主機排行保留「表格」切換（無障礙與精確讀值仍需要）。
   需要圖檔的情境走既有「列印 / 存成 PDF」。

**影響檔案**：charts.js、reports.js、Reports.cshtml、site.css（條列樣式）。
**風險**：無後端變動。注意 WEB-SPEC.md §8.3 規則 4 寫明「表格切換＋PNG 下載」，
需同步修訂規格文件。

### #5 跨主機同簽章查詢加上說明

**現況**：Reports.cshtml:126 只有標題與兩個輸入框，第一次看不懂要輸入什麼、查出來代表什麼。

**方案**：卡片標題下加一段說明（已按實作確認語意）：
> 輸入 Event ID（可加來源縮小範圍），找出**同一個事件簽章曾出現在哪些主機、哪些日子**——
> 用來判斷問題是單機個案還是全環境共通（例如同一批次更新後多台同時出現）。
> 查詢範圍：您有權檢視的主機、資料保留期內的全部紀錄（不受上方報表期間限制）。

最後一句很重要：簽章查詢**不吃**報表的 from/to（ReportService.FindSignature 不帶日期），
與畫面上方期間列並存時使用者一定會誤會範圍。

**影響檔案**：Reports.cshtml。**風險**：無。

### #6 處理狀態儲存後與處理歷程/問題查詢不同步

**現況與成因**（三個獨立缺口疊加）：

1. **問題層級標記完全不寫處理歷程**：`HandlingService.ApplyIssueStatus` 只寫
   issue store＋稽核，不 `AppendLog`（HandlingService.cs:223-263）；歷程只記日層級的
   Update/Assign。使用者批次「已處理」後，歷程最後一筆仍停在較早的
   「指派處理人／處理中／處理人：Wayne」——正是回饋附的畫面。
2. **處理面板顯示的是「存的日層級狀態」不是推導值**：指派處理人會把日層級自動推進成
   in_progress（HandlingService.cs:295-296），之後問題全結案也不會改寫這個存值；
   面板 chips 預選 `handling.status`（handling-panel.js:217），於是顯示「處理中」。
3. **清單顯示推導狀態**：只要勾選的問題沒涵蓋全部計入的問題（例如嚴重度篩選鈕
   隱藏了部分列、或右側還有未勾的），推導就是 in_progress（DayHandlingDerivation:54）
   ——語意正確（「已開始處理」）但畫面沒解釋，使用者以為儲存沒生效。

**方案**（歷程粒度依 D4 拍板：逐筆詳實，不彙總）：
1. **問題層級標記逐筆寫入處理歷程**：
   - `RecordHandlingLog` 新增兩個欄位（SchemaUpgrader 對 lf_record_handling_log 加欄）：
     - `IssueKey`（nullable string）——日層級操作為 null，問題層級操作存簽章鍵；
     - `IssueLabel`（nullable string）——顯示用的「Source EventId」文字**反正規化存下來**，
       歷程是追責紀錄，不能因為日後紀錄被清理/規則改名就查不回「當時標的是哪個問題」。
   - 新增 `HandlingActions.IssueStatus = "issue_status"`（ActionText「標記問題」）與
     `HandlingActions.IssueStatusCleared = "issue_status_cleared"`（ActionText「清除標記」）。
   - `ApplyIssueStatus` 每處理**一個問題寫一列**：Status＝套用的問題狀態、
     Note＝該次填的說明、ActorAccount/ActorId/CreatedAt 照既有欄位。
     批次勾 10 項就是 10 列——「誰、何時、對哪個問題、標成什麼」每一筆都查得到，
     這正是 D4 要求的粒度；歷程本來就是 append-only，量不構成儲存問題。
   - timeline 顯示端做**視覺分組**避免灌版：同一操作者、同一秒（同一次批次）的
     issue_status 列在畫面上合成一個區塊——標題「Wayne 於 07-28 14:03 標記 10 個問題為
     「已處理」」，展開後逐問題列出 IssueLabel（資料是逐筆的，只有呈現在收合，
     與 D4 不衝突：點開就是完整明細）。
2. `HandlingDto` 增 `DerivedStatus`/`DerivedStatusText`/`TotalIssues`/`ClosedIssues`
   （`HandlingService.Get` 內用既有 `ComputeProgress` 算，需補撈 record）；
   處理面板頂部顯示「目前狀態（由問題標記推導）：處理中（3/5 已結案）」，
   與清單頁看到的完全同源；日層級表單的 chips 預選也改用推導值。
3. 批次套用成功的 toast 帶回結果：「已套用；本日狀態：處理中（3/5 已結案）」
   （後端 BatchIssueStatusResultDto 已回傳 DayStatus/Total/Closed，前端沒用而已）。
4. **既有缺口一併補**：指派/日層級變更已逐筆入歷程（含操作者與時間），不動；
   自動帶入處理人（AutoAssign）已有系統列，不動。

**影響檔案**：HandlingService.cs、RecordHandling.cs（RecordHandlingLog 欄位＋HandlingActions）、
SchemaUpgrader、HandlingDtos.cs（HandlingLogDto 加 IssueLabel）、
handling-panel.js（timeline 分組顯示）、record-detail.js（toast）。
**測試**：HandlingServiceTests 新增「單筆/批次標記逐問題寫入歷程（含 IssueLabel/Actor）」
「清除標記寫入歷程」「Get 回傳推導狀態」；GetLogs 映射補 IssueLabel。
**風險**：低中。lf_record_handling_log 加欄屬增量 schema 變更（SchemaUpgrader 既定模式）；
舊列兩欄為 null，timeline 顯示不受影響。

### #7 「處理」欄的 checkbox 與狀態拆成兩欄

**現況**：詳情頁重點問題表的「處理」欄同時塞 checkbox＋狀態文字＋預計完成日
（record-detail.js checkboxControl:323-362），且「不處理（預設）」「已知雜訊（自動）」
兩種列沒有 checkbox（不能參與批次套用）。

**方案**：
1. `issueColumns()` 拆成「選取」欄（僅 checkbox，`canHandle` 才出現）與「處理狀態」欄
   （狀態徽章/文字＋預計完成日＋預設不處理/自動雜訊的行內動作）。
2. 「選取」欄表頭放全選 checkbox（勾/取消當前分節可見列——批次套用的常見手勢）。
3. 預設不處理與自動雜訊列**也給 checkbox**：批次套用本來就允許覆蓋任何問題的狀態
   （後端 SetIssueStatusBatch 不區分），前端沒理由擋。原本的「確認不處理/調回未處理」
   行內快捷鈕保留在狀態欄。

**影響檔案**：record-detail.js。與 #8 同一個函式群改動，**與 #8 合併實作**。
**風險**：低，純前端。

### #8 已結案項目排到最下方並收合

**現況**：分節內問題列順序就是後端 TopIssues 順序，已處理與未處理混排；
處理完的日子進頁面還是滿版列表。

**方案**（範圍依 D2 拍板：**僅風險日詳情**，問題查詢清單維持緊急程度排序不動）：
1. 每個類別分節內排序：**未處理（含明確 open）→ 處理中 → 其餘（已處理/不處理/誤報/
   已知雜訊/預設不處理/自動雜訊）**；同組內維持原相對順序（後端已按重要度排）。
2. 「其餘」組收合：分節表格只渲染未處理＋處理中列，尾端加一列
   「已處理／已有結論 N 項　展開▾」toggle（renderTable 之外自組一個 tfoot 列或
   分節內第二張表），展開狀態不持久化（每次進頁預設收合）。
3. 「另有 N 項因嚴重度篩選未顯示」提示不變——收合的項目**有顯示計數**，
   不會與「沒看到＝不存在」的誠實原則衝突。
4. **計數器改三段（D3 拍板）**：`renderProgress` 從「已處理 N／未處理 M」改為
   「**已處理 N／處理中 O／未處理 M**」——
   - 已處理＝`handlingStatus === 'resolved'`；
   - 處理中＝`handlingStatus === 'in_progress'`（新增的一段）；
   - 未處理＝明確 open 或從未標記（且非預設不處理/自動雜訊），計法不變；
   - 不處理/誤報/已知雜訊/預設不處理/自動雜訊照舊三邊都不計（已有結論，不是待辦）。
   任一段為 0 時該段省略，避免「已處理 0／處理中 0／未處理 12」這種噪音。

**影響檔案**：record-detail.js、site.css（toggle 列樣式）。
**風險**：低。注意收合狀態要在 `renderIssues` 重繪（篩選切換/批次套用重載）間表現一致；
收合分組的依據（狀態）會被批次套用即時改變，重載後列會「搬家」到新分組屬預期行為。

### #9 檢查不必要的折行（詢問 AI 送出中折行等）

**現況與成因**：`withBusy` 會把按鈕內容換成「spinner＋文字」（ui.js:383-393），
Bootstrap 按鈕預設**不**禁止換行；聊天輸入列是 `d-flex gap-2`，輸入框 flex 撐滿、
送出鈕寬度僅容「送出」二字，變成「spinner＋送出中」後必然折行（RecordDetail.cshtml:48-52）。

**方案**：
1. site.css 加全站規則 `.btn { white-space: nowrap; }`——專案內沒有依賴按鈕文字換行的
   設計（需全頁掃一次確認），這條治本，所有 withBusy 按鈕（送出中/歸納中/儲存中/
   測試中/判讀中/套用中）一次解決。
2. `#chat-send` 補 `flex-shrink: 0`，避免 nowrap 後按鈕被 flex 壓縮出現省略破版。
3. 全站巡檢其他折行點（同回饋的「例如」語氣，代表要普查）：
   - 詳情頁表格「時段」欄 `HH:mm~HH:mm` 窄欄折行 → `white-space: nowrap`；
   - 清單頁處理狀態欄「徽章＋逾期＋N/M」的折行；
   - 報表 KPI 對比列「↑ 12%（前期 34）」。
   實作時以瀏覽器實測為準，逐一補 nowrap 或調欄寬。

**影響檔案**：site.css（主要）、必要時各頁微調。
**風險**：低；nowrap 全站套用後需巡檢一次窄視窗（tablet 寬）確認沒有按鈕撐破容器。

### #10 風險報告全文：點整列即可展開

**現況**：只有「▾ 風險報告全文」那顆 btn-link 可點（RecordDetail.cshtml:58-63），
header 右側與空白區點了沒反應；複製/列印鈕在同一列。

**方案**：click handler 從 `#report-toggle` 移到整個 `.lf-card__header`；
複製/列印按鈕 `stopPropagation`；header 加 `cursor: pointer` 與 hover 底色
（沿用既有 clickable 視覺）；toggle 元素補 `aria-expanded` 同步。

**影響檔案**：record-detail.js（setupReportToggle）、RecordDetail.cshtml、site.css。
**風險**：無。

### #11 詳情頁顯示高風險、但問題最高嚴重度只有中

**現況與成因確認**：日風險等級與問題嚴重度是**刻意分開的兩套層級**
（RiskLevels.cs:7-9），「高風險日但看不到高嚴重度問題」有四種真實路徑：

1. **AI 上調**：AI 的 risk_level 只能把程式判定往上拉（LogAnalysisService.cs:264
   `MoreSevere`）——中風險的規則判定被 AI 拉成高，最可能是使用者遇到的情境。
2. **Critical 關聯訊號**：關聯鏈本身 Critical → 日=高，但關聯訊號不在重點問題表裡
   （在右側「程式偵測訊號」卡）。
3. **顯示設定隱藏**：SiteHidden 模式會把未勾選層級的問題從詳情頁過濾掉，
   但「風險等級判定不受顯示設定影響」是明文設計（SystemSettings.cs:26）——
   造成高風險的那條 Critical/High 問題可能根本沒顯示。
4. **抑制**：問題事後被抑制，舊紀錄的風險等級不回改（證據層）。

這不是 bug，但畫面沒有解釋，使用者的困惑合理。**若 D1 採 B1**，路徑 2 的來源
會顯性化成規則旗標，可解釋性大增。

**方案**（與 D1 的選擇無關皆可做）：
1. `DailyAnalysisRecord` 新增 `RiskBasis`（string，批次寫入判定依據代碼：
   `rule`（含旗標規則 id）/`correlation`/`trend`/`ai_raise`＋程式判定等級），
   SchemaUpgrader 加欄位；舊紀錄為空。
2. 詳情頁 header 的風險徽章旁顯示判定依據小字/tooltip：
   「高風險：AI 判讀上調（程式判定：中）」「高風險：磁碟故障規則命中」等；
   舊紀錄無 RiskBasis 時顯示通用說明
   （「日風險由規則命中＋趨勢＋關聯訊號＋AI 判讀綜合判定，與單一問題嚴重度非同一套層級」）。
3. SiteHidden 模式且當日有問題被過濾時，詳情頁補一行
   「部分問題已依全站顯示設定隱藏；風險等級以完整資料判定」（後端已知 hidden 數量
   才能顯示——RecordRepository 過濾時帶出 `hiddenIssueCount` 到 detail DTO）。

**影響檔案**：DailyAnalysisRecord、LogAnalysisService.cs、SchemaUpgrader、
RecordRepository、RecordDtos.cs、RecordQueryService.cs、record-detail.js。
**測試**：RiskReportServiceTests/儲存契約測試補 RiskBasis 欄位。
**風險**：中低。批次寫入新欄位屬增量 schema 變更，SchemaUpgrader 已有既定模式可循。

### #12 處理狀態對外一律三態：未處理／處理中／已處理

**現況**：狀態值域六種（open/in_progress/resolved/wont_fix/false_positive/known_noise）。
問題查詢清單、CSV、儀表板會直接露出「不處理/誤報/已知雜訊」徽章（format.js:68-75）；
日層級 fallback 為 wont_fix 等狀態的日子，清單「已處理」chip 查不到
（RecordQueryService.cs:147 精確比對）、報表處理進度的「未完成」下鑽也對不上
（GetTodo 只數 resolved）。

**方案**（單一事實來源收斂，比照 SHARED-STANDARDS 手法）：
1. Core `HandlingStatuses` 新增 `ExternalOf(status)`：open→open、in_progress→in_progress、
   **其餘（resolved/wont_fix/false_positive/known_noise）→ resolved**；
   加註「對外檢視三態；詳細結論只在詳情頁呈現」。
2. 後端套用點：
   - RecordQueryService：清單 DTO 的 `HandlingStatus`/`HandlingStatusText` 走 ExternalOf
     （文字＝未處理/處理中/已處理）；`Statuses` 篩選改比對 ExternalOf——
     「已處理」chip 從此涵蓋全部結案類；
   - DayHandlingDerivation fallback 出來的日層級狀態在**對外出口**正規化（推導本身不動）；
   - HandlingService.GetTodo：ResolvedCount 改數 ExternalOf==resolved（報表處理進度、
     儀表板 KPI 自動一致）。
3. 前端：format.js `handlingBadge` 收斂為三態（wont_fix/false_positive/known_noise
   併入「已處理」success 徽章）；**詳情頁不受影響**——問題列的狀態文字走
   issue 層級的 `handlingStatusText`（已處理/不處理/誤報/已知雜訊照舊詳列），
   處理面板的狀態 chips 也照舊六選——「只有 detail 頁才詳細說明處理方式」。
4. CSV 匯出、儀表板下鑽 URL（statuses=open,in_progress）語意不變但結果變準
   （wont_fix 日不再漏在「未完成」之外）。

**影響檔案**：RecordHandling.cs（HandlingStatuses）、RecordQueryService.cs、
HandlingService.cs、format.js、（檢查 dashboard.js/audit 顯示點）。
**測試**：HandlingServiceTests、RecordQueryServiceSearchTests 加三態映射與篩選 case；
既有斷言「不處理」文字的測試改「已處理」。
**風險**：低中。語意變更點要跟使用者確認一件事：清單上「已處理」徽章從此
**包含**不處理/誤報/已知雜訊（hover title 可帶原始結論，滑鼠移上去仍看得出細節）。

### #13 處理歷程固定高度＋modal 放大檢視

**現況**：詳情頁右欄的處理歷程卡（RecordDetail.cshtml:83-88）把 timeline 全量渲染，
沒有高度上限——歷程一長就把下方的「程式偵測訊號」「類型分布」「資料涵蓋率」推到
很深的位置。**#6 依 D4 改為逐問題逐筆記錄後，歷程只會更長**，本項是它的配套。

**方案**：
1. **卡片內限高**：site.css 對 `#handling-log` 設 `max-height`（約 320px，
   接近 4～5 筆的高度）＋ `overflow-y: auto`；右欄版面高度從此可預期。
2. **ui.js 新增通用資訊 modal helper**：`showDetailModal({ title, body, size })`——
   動態組 DOM、關閉即銷毀，骨架抽自既有 `confirmAction`（ui.js:165-197 已是同一套
   動態 modal 寫法，抽共用避免第三份複本）；`body` 收 DOM 節點（不是 HTML 字串，
   維持 S7 純文字組裝原則）、`size` 支援 `modal-lg`。**#14 共用同一個 helper**。
3. **放大檢視**：歷程卡 header 加「放大檢視」按鈕（icon＋文字，lf-no-print），
   點擊開 `modal-lg` 顯示完整歷程。`loadLogs` 拆成「取資料」與
   `renderTimeline(container, logs, { expanded })` 兩段——卡片內與 modal 內
   渲染同一份已載入的 logs，不重打 API；
   D4 的批次視覺分組在卡片內**預設收合**、modal 內**預設展開**
   （會開 modal 的人就是要看逐筆細節）。
4. 空歷程（「尚無處理紀錄」）時不顯示放大按鈕。

**影響檔案**：handling-panel.js、RecordDetail.cshtml、site.css、ui.js。
**風險**：無後端變動。注意 modal 開著時若批次套用重載頁面（onBatchSaved → load()），
modal 資料是舊的——關閉即可，不做即時同步（modal 是快照檢視）。

### #14 「範例訊息」名稱不明＋內容擠在一起

**現況**：詳情頁重點問題「說明」欄的「範例訊息」btn-link（record-detail.js:647-664），
hover/focus 觸發 Bootstrap popover，內容是 `sampleMessages.join('\n---\n')` 純文字。
site.css 其實已設過寬度與捲動（`.lf-sample-popover` max-width min(640px, 90vw)、
pre-wrap、限高 240px，site.css:957-965），但實際仍窄擠——實作時需實測確認成因
（popover 被 Popper 定位空間壓縮，或 `--bs-popover-max-width` 變數未生效），
不過**方案直接繞開 popover，不依賴查明**。名稱問題屬實：「範例訊息」看不出
指的是「這個問題實際觸發的 Windows 事件原始訊息樣本」。

**方案**：
1. **改名**：觸發鈕文字改「原始訊息 N 則」（如「原始訊息 3 則」），
   title/aria-label：「這個問題實際觸發的事件訊息樣本，供比對確認」。DTO 不變
   （sampleMessages 既有欄位）。
2. **互動改點擊開 modal**（共用 #13 的 `showDetailModal`，`modal-lg`）：
   - 每則訊息獨立一個區塊（等寬字型、pre-wrap），區塊間用邊框分隔——
     取代現在把 `---` 當分隔字串塞進同一段文字的做法；
   - modal 標題「原始訊息（Source EventId，共 N 則）」；
   - 事件訊息是攻擊者可控字串，維持 `textContent` 純文字組裝，不解析 HTML（S7）；
   - modal 寬度不受 popover 定位/max-width 限制，寬擠問題徹底解決，
     內容長也有完整捲動空間。
3. **移除 hover popover**：click 與 hover 兩套並存會曖昧（hover 看一半點下去變 modal），
   且省掉表格重繪時 `bootstrap.Popover` 實例殘留的隱患；
   `.lf-sample-popover` 樣式無其他使用處，一併刪除。

**影響檔案**：record-detail.js、ui.js（同 #13 的 helper）、site.css（刪 popover 樣式、
加訊息區塊樣式）。
**風險**：無。互動從 hover 變 click 是行為變更，但原本 trigger 本來就含 focus/click
（點擊維持顯示），使用者手勢相容。

### 與既有文件的同步

- WEB-SPEC.md §8.3（圖卡工具列含 PNG）、§8.2（嚴重度色彩四級）需隨 #3/#4/#1 修訂。
- 本檔上方「2026-07-27 — SHARED-STANDARDS-PLAN」段 S11（SEVERITY_ORDER）若採 B1 需同步。
- 完工後本文件標頭改「全部實作完成」並記錄與規劃的偏差，沿用前輪慣例。

---

## 2026-07-21 起 — WEB-SPEC 實作進度與過程中的定案（原 docs/WEB-SPEC.md §14）

> 本段原為 WEB-SPEC.md 的 §14 節，記錄 Phase 0–4（2026-07-21）與 SCALE-2000 施工
> （2026-07-23）逐階段的驗收結果與實作期間確立的技術細節；文件收斂時（2026-07-28）
> 整段移入本檔，WEB-SPEC.md 僅保留現況條文。

### Phase 0（✅ 已完成 2026-07-21）

驗收：建置 0 警告 0 錯誤；單元測試 268 通過（Phase 0 前 227，新增 41）；
`--selftest` 76 項通過、exit code 0（批次行為零改變）；
未登入 302、API 401、能力不足 403、serverAdmin 登入並指派 admin 成員皆實測通過。

實作過程中確立的技術細節（與規劃不同或規劃未涵蓋者）：

| # | 項目 | 定案 |
|---|---|---|
| 1 | **Core 與 Web 的 TargetFramework 為 `net8.0-windows`**（非 net8.0） | `LogIssueSignature.EntryType` 使用 Windows 專屬的 `EventLogEntryType`，屬既有序列化契約；為平台中立改掉它會動到歷史資料格式。本系統本來就只在 Windows 上運作，綁定不是額外限制 |
| 2 | `EventLogEntryData` 移入 Core/Models | 它是純資料模型且被 Analysis 層的 `LogAggregator` 依賴；讀取事件的 `EventLogService` 仍留在批次 exe |
| 3 | Core 的 `InternalsVisibleTo` 加入 `LogForesight` | 抽組件前 `CorrelationAnalyzer` 的 internal 常數與 `SelfTestRunner` 同組件可見。以 InternalsVisibleTo 恢復原可見範圍，而不是把實作細節改成 public——重構要求行為零改變，不順手擴大公開介面 |
| 4 | **授權採 FallbackPolicy（預設全部要求登入）** | 實測發現 ASP.NET Core 端點預設匿名：`AuthController` 未標 `[Authorize]` 時 `/api/auth/me` 未登入回 200。改為全域預設要求驗證、以 `[AllowAnonymous]` 明確開放例外——安全的預設值要讓「漏標註」變成拒絕存取而不是公開 |
| 5 | **JWT 關閉 claim 名稱映射**（`MapInboundClaims = false`）＋自訂 claim 名稱 | JwtBearer 預設會把 `unique_name` 等改寫成 WS-Federation 長 URI，導致寫入與讀取的名稱不一致（實測：`account` 讀出空字串，連帶讓稽核記錄的帳號空白）。改用 `account`/`name`/`cap`/`srvadm` 自訂名稱，寫什麼讀什麼 |
| 6 | Web 組態類別命名為 `WebAppSettings` | Core 已有批次用的 `AppSettings`，只差大小寫的兩個型別是閱讀陷阱 |
| 7 | 新增 `JsonCollectionFile<T>` 共用基底 | §10.4 的「寫 temp → File.Replace 原子替換」規則實作一次、所有整檔型 store 繼承，避免各自實作時漏掉原子性（與 `RecordStorageShaper` 單點化同理） |
| 8 | 儲存介面採**同步** API | 與 Core 既有的 `IAnalysisRecordStore` 一致；EF Core 對 SQL 後端同樣提供同步 API，不影響後端可替換性 |
| 9 | `global.json` 的 `rollForward` 改 `latestMajor` | 原設定釘 SDK 8.0.100 + latestFeature，開發機只有 9.x/10.x SDK 導致整個方案無法建置。TargetFramework 不變，產出仍是 .NET 8 |
| 10 | `LdapAuthenticationProvider` 一併實作 | 正式驗證已定案 AD LDAP，實作成本低（`PrincipalContext.ValidateCredentials`），先寫好讓 Production 路徑真的可用，而不是留 TODO |

已建立的檔案結構對應原 §4.1，並額外新增 `Extensions/`（DI 註冊擴充方法，讓 Program.cs 保持薄）。

### Phase 1（✅ 已完成 2026-07-21）

驗收：建置 0 警告 0 錯誤；單元測試 **322 通過**（Phase 1 前 268，新增 54）；
`--selftest` 76 項通過。端對端實測（真實 HTTP 呼叫、UTF-8 BOM 的 CSV 檔）：

| 驗收項目 | 結果 |
|---|---|
| 三份 CSV 依序匯入（使用者→主機→授權） | 使用者 4 筆、主機 3 筆、授權 2 筆全數套用；自動建立群組 OO部門／XX部門／OO部門主機／XX部門主機／DB伺服器 |
| 負責人可見性警告 | 授權尚未建立時逐行提出警告且**不阻擋**（設計如此） |
| **可見範圍**（Phase 1 核心判準） | OO 部門使用者只見 2 台 OO 主機；XX 部門只見 1 台 XX 主機；跨部門成員見全部 3 台（聯集）；admin 見全部 |
| 能力解析 | 一般使用者 `Handle`+`ConfirmPermission`；admin 全部七項 |
| 稽核 | 三筆 `import_apply` 摘要皆為完整人話（含新建群組清單） |

實作過程中確立的細節：

| # | 項目 | 定案 |
|---|---|---|
| 11 | `IHostStore` 分出 **`Touch`** 與 `Upsert` 兩條寫入路徑 | 主機是唯一由批次與 Web 共同寫入的資料。批次若走一般 Upsert，會用它不知道的空值蓋掉 Web 維護的角色描述／群組／負責人——而且要等到「大家都看不到這台主機了」才會發現。`Touch` 只建立缺少的主機並更新回報時間，已用合約測試釘住此行為 |
| 12 | `EnsureVisible` 對未授權主機拋 **404 而非 403** | 403 等於確認「這台主機存在，只是你沒權限」，可被用來列舉機房主機清單。對無權限者而言「不存在」與「看不到」應是同一件事 |
| 13 | 授權矩陣只列 `role=User` 的群組 | admin/manager/dev 本來就有 ViewAll，放進矩陣會讓人誤以為那些勾選有意義 |
| 14 | CSV 自動建立的使用者群組一律 `role=User`、非 builtin | 不允許一份試算表無中生有造出管理權限；指派到**既有**的 builtin 群組則允許（管理者的刻意操作，全程稽核） |
| 15 | 新增 `GET /api/hosts`（可見主機清單），**不掛 `[Permission]`** | 任何登入者都可以問「我看得到哪些主機」，答案本身就是授權過濾的結果——這正是第 3 層防線的用法示範 |
| 16 | 非 API 的 403 導向 `/access-denied` 頁 | 瀏覽器直接開無權限網址時回 JSON 是很差的體驗，也看不出是沒權限還是壞掉 |
| 17 | 新增 `JsonCollectionFile` 的第二批 store 與 `ICsvImporter` 抽象 | 三種匯入各一個實作，流程（解析→驗證→預覽→套用）共用；新增第四種匯入只要多註冊一個實作，ImportService 與 Controller 不需修改 |

### Phase 2（✅ 已完成 2026-07-21）

驗收：建置 0 警告 0 錯誤；單元測試 **348 通過**（Phase 2 前 322，新增 26）。
端對端實測以 30 天 × 3 主機的分析資料（含高/中/低風險、關聯訊號、深入分析、
Security 權限不足與 Event Log 覆蓋兩種涵蓋率缺口、17 份報告全文）：

| 驗收項目 | 結果 |
|---|---|
| 讀 JSONL 真實資料 | 儀表板正確彙總（13 高風險日、4 中風險日、6 涵蓋缺口日）；類型分布、主機排行、關聯訊號計數皆正確 |
| **授權過濾** | OO 部門使用者查得 58 筆（2 台）、XX 部門 29 筆（1 台）；XX 使用者的儀表板只出現自己主機的 Security 類別，看不到 OO 的 Storage 問題 |
| **越權防護** | XX 使用者直接指定 OO 主機的 hostId：查詢回 0 筆、詳情回 **404**（不確認主機存在） |
| **下鑽（§8.4 驗收標準）** | 類型分布圖「Storage×Critical」→ 12 筆明細；趨勢折線某日高風險點 → 該日 1 筆 → 風險日詳情（2 項問題、2 條關聯訊號、1 類深入分析、報告全文）。**兩次點擊內到達** |
| 報告全文 | `<pre>` 原樣呈現，含框線符號與中文皆正確 |
| 頁面與資源 | 9 個頁面路由全部 200 且套用版面；13 個靜態資源（含 Chart.js）全部 200 |

實作過程中確立的細節：

| # | 項目 | 定案 |
|---|---|---|
| 18 | 新增 `IAnalysisRecordQuery` 與批次的 `IAnalysisRecordReader` 分開 | 批次要的是「近 N 天」「這天有沒有紀錄」，Web 要的是多條件篩選；合成一個介面會讓兩邊都依賴自己用不到的方法（ISP）。`JsonlAnalysisRecordStore` 同時實作兩者（同一份 history.txt） |
| 19 | `RecordQueryFilter.Hosts`：**null=不限、空集合=查無資料** | 授權範圍為空的使用者必須得到空結果。若把空集合當成「不限」，沒有任何授權的人反而看得到全部——失敗方向最糟的一種錯誤。已列為合約測試的必測案例。（2026-07-21 由 `HostNames` 改為 `HostKey` 集合，見第 22 項） |
| 20 | 新增 `Repositories/RecordRepository` 層 | 一台主機可能有**多個識別**（本身＋已併入它的墓碑列），查詢時要一起展開；展開若散落在各 Service 遲早有人漏做。集中於此並**強制**套用可見範圍：呼叫端可縮小範圍但不可能擴大（取交集） |
| 22 | 紀錄與主機以 **`HostId`（PK）** 關聯，`Host` 字串降為顯示名快照（2026-07-21） | 主機改名／換 IP／搬遷 Sentinel 都不影響歷史歸戶，且與 `lf_daily_records.host_id` FK 直接對齊，DB 匯入不需名稱推斷。比對規則單點定義於 `HostMatcher`：**PK 優先**，`HostId==0` 的舊紀錄才退回名稱比對（舊資料不遷移也查得到）。刻意不是「id 或名稱任一命中」——查詢範圍即授權範圍，寧可嚴格。詳見本檔「2026-07-21 — NetIQ 主機清單 Web 維護與主機配對規劃」段 |
| 21 | `IReportReader` 與 `IReportSink` 分開，且做路徑逃逸防護 | 批次只寫、Web 只讀（ISP）。報告參照來自歷史紀錄檔——那是**資料**不是程式常數，被竄改成 `..\..\` 路徑時沒有防護就是任意檔案讀取。已有測試涵蓋 |
| 22 | 趨勢折線對沒有紀錄的日子**補 0** 而非略過 | 略過會讓折線把空白日連成一條斜線，看起來像平滑變化。同理，主機時間軸對無紀錄日給獨立顏色——「這天沒分析」與「這天沒風險」意義完全不同 |
| 23 | 趨勢白話描述（`TrendText`）在後端組好 | 同一份規則若前後端各寫一次，遲早出現「清單說頻率上升、詳情說重複發生」 |
| 24 | KPI 對比：**上升＝紅色** | 告警數上升是變壞，與一般儀表板「上升＝綠色」的直覺相反，需明確標示 |

**已知限制**：本機瀏覽器無法載入 ASP.NET 開發憑證（未受信任），視覺呈現未經瀏覽器實測；
功能驗收全部在 API 與 HTTP 層完成。要做視覺確認需先執行 `dotnet dev-certs https --trust`
（會跳出系統確認對話框）。

### Phase 3（✅ 已完成 2026-07-21）

驗收：建置 0 警告 0 錯誤；單元測試 **365 通過**（Phase 3 前 348，新增 17）；`--selftest` 76 項通過。
端對端實測 2026-07-21 指定的核心情境：

| 驗收項目 | 結果 |
|---|---|
| **指派情境**（admin 改派、負責人不變） | SRV-OO-DB01 負責人李大華 → admin 改派給王小明後，處理人=王小明、**負責人仍為李大華**；主機資料未被改動 |
| 能力邊界 | user（王小明）可更新狀態/說明/完成日；嘗試改派得到 **403** 並留下 `denied` 稽核 |
| 處理歷程 | 四筆完整敘事（自動帶入→改派→查修中→結案），先前說明未被覆蓋 |
| 權限異動待辦 | 3 筆真實資料；確認授權成功；標記可疑未填說明被擋下；user 只看得到自己部門的 2 筆（授權過濾生效） |
| 稽核頁 | 22 筆可查；摘要為完整人話，含「（主機負責人不變：李大華）」；user 查稽核得到 403 |
| 儀表板整合 | 待辦數字與權限異動待確認數皆正確 |

實作過程中確立的細節：

| # | 項目 | 定案 |
|---|---|---|
| 25 | 預設處理人**只在「完全沒有處理紀錄」時套用** | 「從未指派」與「admin 明確取消指派」都是 `HandlerId == null` 但意義相反。一旦有處理紀錄，其值即為唯一權威——否則按下「取消指派」後畫面又冒出負責人，看起來像壞掉。此為測試 `取消指派_處理人清空` 抓出的語意衝突 |
| 26 | 顯示用預設值**只計算不寫入** | 讀取不該產生副作用，也不該每次瀏覽都留一筆稽核。持久化與稽核發生在第一次真正寫入時（`NewHandling`），兩條路徑共用 `DefaultHandlerId` |
| 27 | 自動帶入與人工改派**各記一筆稽核** | 自動帶入也是一次指派行為；事後查「處理人怎麼變成現在這樣」必須看得到完整的變化鏈 |
| 28 | 指派給人時自動由 open 推進為 in_progress | 「已指派但仍未處理」語意矛盾；已結案的狀態不動（改派結案問題可能是為了補資料） |
| 29 | 標記可疑**必填說明** | 那是要交給別人接手調查的訊號，沒有說明對後續處理的人毫無幫助 |
| 30 | 權限異動的「異動」與「確認」分兩個檔案 | JSONL 後端的單一寫入者規則：批次寫 `rundata\perm_changes.jsonl`、Web 寫 `webdata\perm_confirms.json`，不需要跨程序交易。SQL 後端會合併成同一張表的欄位 |
| 31 | 清單頁的處理狀態以記憶體 join 取得 | 逐筆查會變成 N 次讀取，而 JSONL 後端每次讀取都是一次完整檔案解析 |

### Phase 4（✅ 已完成 2026-07-21）

驗收：建置 0 警告 0 錯誤；單元測試 **383 通過**（Phase 4 前 365，新增 18）；
`--selftest` 76 項通過且**維持唯讀承諾**（種子同步位於 selftest 早退之後，不建立任何檔案）。

| 驗收項目 | 結果 |
|---|---|
| **儲存前驗證擋壞資料** | 兩層都生效：DTO 層（缺來源比對 → 「請輸入來源比對字串」）、規則層（EventIds 空但未宣告全比對 → 拒絕儲存）。實測不合格規則**未寫入** rules.json |
| 四層保護 | 38 條 builtin 全部 `canRestore=true`／`canDelete=false`；custom 規則 `canDelete=true`／`canRestore=false` |
| 種子同步 | 批次啟動後產生 `rule_seeds.json`（45KB，38 條原廠快照） |
| 執行紀錄 | 批次登記 RunId=1；NLog 的 Warn 自動流入（實測 AIService 重試警告已入庫） |
| **異常中斷偵測** | 強制結束批次後 `FinishedAt` 維持未回報，監控頁顯示為執行中／逾時後為異常中斷——不會被誤判為成功 |
| 執行總表 | 正確顯示「哪幾台沒跑」（本機今日有紀錄，其餘 3 台全為未執行） |

實作過程中確立的細節：

| # | 項目 | 定案 |
|---|---|---|
| 32 | **DTO 驗證失敗改回統一信封** | `[ApiController]` 預設把模型驗證轉成 RFC 9110 ProblemDetails，形狀與本專案信封不同——前端解析不到 `error.message`，使用者看到通用的「系統發生未預期的錯誤」而不是「請輸入來源比對字串」。此問題自 Phase 1 起潛伏於**所有**帶 DataAnnotations 的端點，以 `InvalidModelStateResponseFactory` 一次修正 |
| 33 | 執行紀錄以 **NLog custom target** 收集 | 既有程式碼已在該記 log 的地方記了，逐處改呼叫既繁瑣又一定會漏。target 的 `Write` 內全程吞例外——它在記 log 的路徑上，拋出會讓「記錄」本身變成故障點 |
| 34 | `runs.jsonl` 以**再附加一列**取代回填 | 寫入端維持純 append（不需重寫整檔），讀取時同 RunId 取最後一列。批次被強制中斷時「開始」那一列仍在，正好就是要偵測的狀態 |
| 35 | 失敗回填交給 `using` 的 Dispose | `using var` 在 try 內、catch 看不到它；但例外往外傳時 using 的 finally 先執行，以 exit code 1 回填。掛掉的執行因此顯示為「失敗」而非停在「執行中」——後者會把最需要注意的狀態藏起來 |
| 36 | 異常彙總前**正規化訊息** | 訊息中的日期/數字/GUID 會讓同一個錯誤看起來各不相同，不正規化的話彙總退化成一筆一組，失去「這是個案還是通案」的判斷價值 |
| 37 | 規則 `Id` 與 `Origin` 建立後不可變更 | Id 是 seed 同步與抑制設定的比對鍵；Origin 決定會不會被 `--import-rules` 覆寫。新規則強制 `custom-` 前綴，避免使用者造出與內建種子衝突的命名 |

### 總體檢（✅ 2026-07-21，Phase 0–4 完成後的全面審查）

驗收：建置 0 警告；單元測試 **385 通過**（新增 2 條回歸測試）；`--selftest` 76 項通過。
審查發現並修正 5 個問題：

| # | 發現 | 修正 |
|---|---|---|
| 38 | **AI 失敗計數把「低風險日刻意不呼叫」算成失敗**——安靜的日子會讓執行總表永遠顯示「有警告」，狼來了之後沒人再看那個顏色 | 批次端只在「有呼叫或非低風險日」時計入 `RecordAiCall` |
| 39 | **處理面板無條件呼叫 /api/admin/users**——每個 user 每次開詳情頁都產生一筆 403＋`access_denied` 稽核，把稽核上最有價值的「權限試探」訊號淹沒在正常瀏覽的噪音裡 | 先取得 handling，`canAssign=true` 才載入人選清單 |
| 40 | `FileReportReader` 的根目錄檢查用純字串前綴——`C:\data` 會誤放行 `C:\databad\x.txt` | 比對前補目錄分隔符號；新增「同名前綴兄弟目錄」回歸測試 |
| 41 | **audit 頁不讀 URL 參數**——儀表板登入失敗卡下鑽到 `/audit?result=Denied` 等於壞連結（違反 §8.4） | init 時套用 `result`/`actions`/`from`/`to` |
| 42 | `SetEnabled` 蓋 `ModifiedBy` 戳記——只停用一條 builtin 就掛「已修改」徽章，讓人誤以為內容動過、改版時要人工比對其實不存在的差異 | 啟停不再蓋戳記（「已修改」專指內容修改）；新增回歸測試 |

審查確認無誤的重點：授權三層（FallbackPolicy／PermissionFilter／VisibilityService 交集）閉合、
`RuleImporter` 內容比對明確列舉欄位（不受新增的 ModifiedBy/At 影響）、`--selftest` 在所有
store 建立前早退（唯讀承諾成立）、前端動態內容一律 `textContent`（Event Log 訊息無 XSS 路徑）、
下鑽網址與 API 參數對齊。

**與規格的已知偏差（開工前已知悉、列為後續迭代，非缺陷）**：

| 優先 | 偏差 | 說明 |
|---|---|---|
| ~~P2~~ ✅ | ~~`rundata\`／`webdata\audit.jsonl` 無保留清理~~ **已於 2026-07-27 完成**（本檔「2026-07-27 — 營運強化與主機停用隱藏規劃」段 P0-3） | §11-6 規劃的 RunLogRetentionDays(90)／AuditRetentionDays(730) 已落地：`lf_log_lines` 補 `created_at` 欄，批次啟動時依「系統管理 > 設定」頁的保留天數清理執行歷程/匯入/稽核紀錄（handling_log 與 perm_changes 刻意不清，理由見該計畫 §4.2） |
| P3 | §9.2 主篩選列缺「處理狀態」下拉 | 功能存在（URL 參數 `statuses` 可用、儀表板下鑽依賴它），僅表單未提供控制項；且下鑽帶入的隱藏條件（severity/statuses）畫面上無標示 |
| P3 | §9.4 主機詳情缺「權限異動紀錄／生效中抑制」區塊；§9.8 使用者詳情缺「操作紀錄／最近登入」頁籤；§9.7 規則頁缺「異動史」連結 | 資料與 API 皆已存在（audit/suppressions 端點），純前端增補 |
| P3 | §8.6-2 表格欄位排序未實作；§6.3 `session_expired` 稽核補記未實作 | 便利性條款，不影響正確性 |
| P3 | CSV 匯入的預覽 token 未綁定使用者；imports.js 的 FormData 路徑未處理 401 導頁 | 兩者都需 Maintain 能力才可觸及，風險低 |

**技術債（重構候選，行為正確、僅維護性）**：`DashboardService` 與 `ReportService` 的
HostRanking/Categories 組裝邏輯重複；`HandlingStatusText` 在兩個 Service 各有一份；
前端 `CATEGORY_NAMES` 對照表散在 4 個頁面模組（應收進 `core/format.js`）；
清單頁逐列 `_users.Get()` 造成 JSONL 後端 N 次檔案讀取（SQL 階段自然消失，屆時再評估）。

（**2026-07-28 補記**：上述技術債已於 refactor/simplify-2026-07 分支的簡化重構全數處理——
`RecordStatsBuilder`／`HandlingStatusText` 單點化、`CATEGORY_NAMES` 收進 `format.js`、
SQL 階段 N 次讀取問題已隨 Phase C 完成而消失。）

### SCALE-2000 施工（✅ 2026-07-23，詳見本檔「2026-07-23 — 兩千台量級擴展規劃 SCALE-2000-PLAN」段）

2026-07-21 Phase 0–4 完成後，依 SCALE-2000-PLAN 執行兩千台量級的擴充，分支 `bugfix-ui-adjustments`：

| 階段 | 內容 | 對應本規格 |
|---|---|---|
| **Phase A** | 負責人 CSV 匯入、網段綁定主機群組 | §9.9、§9.8 |
| **Phase B** | NetIQ 主動探索匯入、Sentinel 生命週期（孤兒主機停用/復活） | §9.8 |
| **Phase C** | SQL 後端完成——三 provider（Jsonl/Sqlite/SqlServer），全資料走 SQL；測試/開發預設 SQLite | §5、§10.5 |
| **Phase D-0** | 篩選 toolbar／chip 共用元件（視覺基盤） | §8.2 |
| **Phase D-1** | 風險日詳情改版（七項：報告收合、低風險預設、已知雜訊記憶、狀態面板、計數器…） | §9.3 |
| **Phase D-2** | 規則/主機/使用者頁掛 toolbar 快速篩選＋排序 | §9.7、§9.8 |
| **Phase D-3** | NetIQ 匯入排程化（Web 排入佇列、批次套用） | §9.8 |
| **Phase D-4** | 量級 UI 調整（主機 autocomplete、群組風險概況、執行監控每日彙總、主機頁伺服器分頁） | §9.1、§9.2、§9.8、§9.10 |
| **Phase E** | Web AI 加值層 W1+W2（今日焦點、查詢歸納、詳情判讀，靜默降級） | §9.1、§9.2、§9.3 |
| **設定 fail-fast** | 批次設定檔存在但解析失敗改為中止啟動，不再靜默用預設值（起因：Storage 段括號誤刪讓整份設定含 AI 位址被丟棄） | §5 |

驗收：建置 0 警告；單元測試 **707 通過**（含 SQLite 合約測試、NetIQ 佇列、設定載入回歸釘樁）；
`--selftest` 通過。全部改動未併回主線，留在 `bugfix-ui-adjustments`。

### 開放事項

| # | 事項 | 狀態 |
|---|---|---|
| 1 | manager 收為純唯讀 | ✅ 已依 2026-07-21 指示定案（僅 ViewAll） |
| 2 | user 自我認領處理人 | ⏸ 暫不開放（嚴格 admin 指派），實務卡流程再議 |
| 3 | 登入失敗儀表板卡片 | ✅ 納入（admin 可見，§9.1） |
| 4 | dev 對業務資料可見範圍 | ✅ 全部唯讀（查執行問題需對照分析結果）；環境敏感時可收斂為授權制 |
| 5 | 正式驗證方式 | ✅ 定案 AD LDAP（2026-07-21）；失敗鎖定交由 AD 帳戶鎖定原則，Web 端不重複建置（§6.2） |
| 6 | 測試期 Stub 免密碼 | ✅ 已接受（2026-07-21，測試環境不含核心重要主機）；**2026-07-23 起涵蓋 serverAdmin（Stub 下所有帳號一致免密碼，§6.2）**；Production+Stub fail fast 欄杆維持 |
| 7 | serverAdmin 本地救援帳號 | ✅ 定案（2026-07-21）：appsettings 定義、密碼封存定期輪替（PBKDF2 雜湊存放）、最小授權（Maintain+ViewAudit）、Web 端 5 次失敗鎖 15 分鐘（§6.2）；**Stub 模式免密碼、不套鎖定（僅 Ldap 驗密碼時適用）** |
</content>
