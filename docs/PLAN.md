# LogForesight 擴充規劃（已確認，待實作）

> 本文件是需求討論的收斂結果。實作按階段進行，每階段有驗證閘門。
> 規劃日期：2026-07-20

## 背景與目標

- 現況：單機版，讀本機 System/Application/Security，四層偵測（規則/趨勢/關聯/AI），地端 KoboldCpp 判讀。
- AI 環境：**Gemma 4 26B、context 20480**——所有呼叫的 prompt＋輸出必須在此預算內（見「AI 呼叫 context 預算」章節）。
- 目標：接上 NetIQ Sentinel **8.5** 取得約 **2000 台** Windows 主機的 Event Log 做集中分析
  （2026-07-20 由「數百台」上修）；主機分散於**多台 Sentinel**（皆 8.5、共用同一組查詢帳密），本機維持直讀。
- 系統定位：**第二層縱深防禦**——多數緊急狀況由既有第一層監控承擔，本系統負責提早發現趨勢與
  第一層漏掉的訊號，故通知即時性要求不高（通知維持 Phase 4 不前移，2026-07-20 確認）。
- 未來：紀錄與結果寫入 DB＋查詢介面——Phase 0 先抽持久層介面（見「持久層抽象」章節）。
- 實測 AI 成本：每主機日約 1~20 秒。

## 核心設計決策

### A. 分級分析（規模對策）

- 規則/趨勢/關聯三層＋跨主機關聯層：**全部主機每天跑**（純計算，秒級）。
- AI 每日判讀：**只給被前四層標記的主機**（規則命中 Medium 以上、趨勢異常、關聯訊號）。
- 未標記主機日照寫 history（`AiAnalyzed=false` 統計模式，沿用現有語意）。
- 深入分析：**不設上限**，僅按嚴重度排序（最嚴重先做）。（原保留的 `MaxDeepDiveHostsPerRun` 安全閥設定已於 2026-07-20 依過度設計體檢移除——有設定無行為會誤導使用者；Phase 3 若真需要限流，屆時連同行為一起實作。）
- 機房總覽：每天 1 次 AI 呼叫，吃第五層產出＋各主機一行結論。

### B. 體檢（2026-07-20 重設計：每日確定性偵測＋7 天 due-date 輪巡敘事，取代原「週六全量」）

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
- 保留 120 天（2026-07-24 由 90 天調整，配合首次執行回補 120 天，回補的歷史不會下次啟動即被清除）。

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
3. 候選（後續）：4672 特權登入、4648 明示認證；4688 走 Sentinel 收錄面處理。
   - **更新（2026-07，已完成）**：Defender / RDP 的 Operational 頻道原規劃走 Sentinel，改為
     **在本機直接以 `EventLogReader` 讀取**（見 README「EventLogReader 遷移＋Operational 頻道擴充」）。
     已納入 Defender（惡意程式偵測/防護遭關閉，seed v2 規則）與 RDP TerminalServices（Low 收集規則），
     並新增【暴力破解→RDP 得手】【防護遭關閉→惡意程式】【惡意程式→持久化】關聯。PowerShell 頻道仍待評估。

## 驗證機制（Phase 0）

- **測試專案**：規則/趨勢/關聯（含新增模式）合成事件測試；Sentinel 欄位對應測試（probe 真實回應存 fixture）。
- **`--selftest`**：注入合成事件跑完整 pipeline（不寫 history、AI 用 stub），輸出「應命中/實際命中」清單。新主機部署先跑。
- **`--debug-dump`**：單次執行完整輸出 prompt 與 AI 原始回應到 `diag\`（平時關閉，驗證期用）。
- 遠端驗證流程：console 輸出＋`logs\logforesight.log`＋history 對應行＋export 報告＋appsettings 貼回對話分析（敏感資訊先遮罩）。

## Sentinel 8.5 查詢設計

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

### 多台 Sentinel（2026-07-20 新增：2000 台分散於多台、皆 8.5、共用帳密）

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

### 主機清單：txt 檔匯入（2026-07-20 定案，取代原「自動發現」設計）

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

### `--netiq-probe` 驗證項（Phase 1 閘門，輸出貼回對話定案）

1. 認證方式（API 帳號實測）
2. 欄位對應：Windows EventID / 來源 / 主機名 / 訊息全文在 Sentinel schema 的哪個欄位
3. GROUP BY 語法能否經 REST 直接用（決定 Q1 走聚合或退回方案）
4. `dt` 時區基準與日切界
5. 各主機頻道覆蓋與詳細度
6. 分頁上限與 search job 生命週期/DELETE
7. **主機 IP 欄位是否存在、記錄的是哪個 IP**（txt 清單以 IP 篩選的前提；多網卡主機是否以清單外的 IP 回報——會造成「查無資料」假象）；Security 頻道實際收錄範圍（DB-PLAN 決策點 #4 第二步的依據）
8. **以 IP 清單做 Lucene 篩選的實測**：單一查詢可容納幾個 IP 條件（決定 Q1 的分批大小）；IP 欄位可否用於 GROUP BY／篩選
9. **認證方式細節**：Basic auth 或 token 交換；session 逾時與重新認證行為（帳密欄位設計不受影響，只影響 SentinelClient 內部）

## 設定檔規劃

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
- `Password` 支援兩種形式：明文（初期測試用）或 `enc:` 前綴的 **DPAPI 加密值**——
  提供 `--protect-netiq-password` 指令在部署機上產生（DPAPI machine 綁定，
  設定檔被複製到別台也解不開）。一個監控入侵的工具自己放明文 SIEM 密碼說不過去，
  但也不引入憑證庫等重型依賴，DPAPI 是 Windows 內建的合理中點
- **密碼永不寫入任何 log**（診斷 log 記設定摘要時遮蔽此欄位）
- **版控紅線**：repo 裡的 appsettings.json 永遠只放空白佔位，真實帳密只存在部署目錄的副本

`Enabled:false` 保證單機部署不受影響。

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
| 1 | SentinelClient + `--netiq-probe` | probe 輸出貼回，定案欄位對應與 Q1 形式 ｜**程式碼已完成 2026-07-24**（細部設計與原廠 API 事實見 docs/NETIQ-API-PLAN.md），閘門本身（真實環境 probe 輸出貼回）待執行 |
| 2 | SentinelStatsSource，2~3 台試點端到端 | 試點輸出貼回比對 |
| 3 | 全量：txt 主機清單（多 Sentinel 路由）、分級 AI、每日慢速趨勢偵測、體檢 due-date 輪巡＋閘門、第五層、機房總覽（含來源狀態）、覆蓋率清單 | 首次全量耗時分布＋總覽貼回調參 |
| 4 | 通知管道（Email / Teams webhook 擇一） | 實際收到通知 |
| 5（DB 就緒後啟動，欄位級設計已定案於 **docs/DB-PLAN.md**） | DB 後端（SQL Server 或 Oracle，EF Core provider 切換）＋JSONL/報告匯入器＋Web 查詢（依負責主機授權）＋AI 問答 | 建表、匯入舊資料、Web 查得到自己主機並可問答 |

回補策略：NetIQ 主機首次接入只回補統計基準（不做 AI，幾分鐘完成）；AI 自次日起服務被標記主機；首個週末做第一輪全量體檢。

## Phase 0 實作狀態（2026-07-20 完成並通過審查）

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

## 時間預算估算（2000 台，2026-07-20 重算；01:00 起跑）

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

## 已確認的需求決策紀錄

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
    詳 docs/AI-ROLE-PLAN.md（2026-07-20）
