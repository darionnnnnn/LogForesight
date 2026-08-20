# LogForesight 偵測與 AI 內部規格

> 除非必要否則不要讀取 docs/archive/ 內容，避免浪費 token。

本文件收錄「偵測邏輯與 AI 運用」的深度內容：規則細節、危險訊號清單、小模型策略與 AI 穩定性
設計。規則「機制」（語意邊界、seed／匯入、DB 映射）另見
[RULES-SPEC.md](RULES-SPEC.md)——一份講「偵測什麼」，一份講「規則怎麼運作」。

---

## 提早發現問題的邏輯

### 核心設計：五層偵測

「偵測」和「判讀」分開，各用擅長的工具（使用的是地端 Gemma 26B/27B 級小模型，
所有能確定性計算的判斷都由程式先做掉，模型只負責把結論翻譯成白話）：

| 層 | 負責 | 為什麼 |
|---|---|---|
| **規則層** (`KnownIssueCatalog`) | 比對已知危險事件（Source + Event ID + 次數門檻），確定性命中；規則命中的問題同時附帶靜態知識庫內容（白話說明／常見原因／處置步驟），不需要 AI 深入分析 | 已知模式用規則抓，100% 召回、零成本，不賭小模型會不會漏看；同一 Event ID 的原因/處置幾乎不變，寫死比每次重新生成更快、更一致、零幻覺 |
| **趨勢層** (`TrendAnalyzer`) | 當日各事件的次數 vs 前一日 vs 近 14 日基準（中位數——單日爆量不會墊高基準讓後續真正異常被蓋掉），程式直接算出「首次出現 / 頻率上升 / 重複發生 / 下降」 | 數字比較程式做得又快又準，不該指望模型在腦中做算術 |
| **慢速趨勢層** (`SlowTrendAnalyzer`) | 近 7 天 vs 前 7 天總量比較，每日、全主機、確定性執行，捕捉躲在趨勢層單日門檻下的緩慢惡化訊號，偵測延遲最短可達 1 天 | 純算術，單元測試完整涵蓋 |
| **關聯層** (`CorrelationAnalyzer`) | 比對「多個獨立事件的已知組合模式」：攻擊鏈、故障連鎖、跨日推進（見下方清單） | 單一事件各自不嚴重、組合起來卻是明確故事——這種跨 log 關聯判讀正是小模型最容易漏掉的，必須程式先比對好 |
| **AI 層** (Gemma) | 把前四層已確定的結論（風險等級、趨勢、關聯訊號）翻譯成白話標題與敘述，讓不懂 Event Log 的人也能看懂該怎麼處理；只有規則未涵蓋的 Other 類問題才由 AI 判讀根因與處置建議 | AI 不是判斷風險或找根因的引擎（那是前四層與靜態知識庫的職責），語意轉譯與白話敘事才是規則做不到、AI 真正擅長的部分 |

五層結論取較嚴重者：規則或關聯鏈命中**「重大」旗標**（見下方「問題嚴重度與『重大』標註」）
→ 風險強制「高」；
趨勢層（含慢速趨勢）有頻率異常或關聯層有任何訊號 → 風險至少「中」。AI 判斷只能把風險往上拉、
不能往下壓，即使 AI 判斷輕忽或 AI 服務不可用也不影響告警與處置建議（詳見
[docs/archive/HISTORY.md](docs/archive/HISTORY.md)）。

### 關聯層偵測的組合模式（`CorrelationAnalyzer`）

| 模式 | 組合條件 | 意義 |
|---|---|---|
| 【入侵鏈】 | 大量 4625（≥10）＋帳號建立/提權（4720/4732 等）同日 | 暴力破解得手後建立立足點；有時間先後可判斷時會標注時序是否符合攻擊推進 |
| 【破解得手】 | 大量 4625（≥10）＋條件式撈取的 4624（成功登入）與失敗記錄同一組帳號/IP | 比帳號建立/提權更早、更直接的得手證據——暴力破解攻擊者未必馬上建帳號提權，可能先潛伏 |
| 【持久化】 | 帳號異動或攻擊嘗試＋新服務/排程任務（7045/4697/4698）同日 | 入侵後植入後門 |
| 【滅跡】 | 稽核清除/變更（1102/4719/4907）＋同日其他安全事件 | 入侵者清除操作痕跡 |
| 【提權→植入】 | 權限/特權異動（4670/4703 等）＋新服務/排程任務同日 | 先取得權限再植入執行體 |
| 【暴力破解→RDP 得手】 | **昨日**大量 4625（≥10）的來源 IP，**今日**以 RDP 成功登入（21/25/1149）同一 IP | 暴力破解跨日以遠端桌面得手；純以 IP 交集判定，無交集不觸發 |
| 【防護遭關閉→惡意程式】 | Defender 防護被關閉/停用（5001/5010/5012）＋同日惡意程式偵測或攻擊訊號（也含跨日：昨日關防護、今日驗出惡意程式） | 入侵者常在植入前先解除防護；單獨關防護只走規則層、不觸發關聯 |
| 【惡意程式→持久化】 | Defender 惡意程式偵測（1006/1116 等）＋新服務/排程任務同日 | 惡意程式建立持久化立足點 |
| 【跨日入侵鏈】 | **昨日**大量登入失敗＋**今日**帳號/權限/服務異動 | 攻擊者跨日推進，比單日訊號更值得警戒 |
| 【儲存連鎖】 | disk/Ntfs/storahci 三類儲存訊號同日命中 ≥2 類 | 硬碟故障連鎖反應，故障迫在眉睫 |
| 【儲存→當機】 | 儲存錯誤＋非預期關機（41/6008）同日 | 儲存故障已導致系統崩潰 |
| 【儲存持續劣化】 | 儲存錯誤連續兩日出現 | 不是偶發抖動，硬碟剩餘壽命可能以天計 |
| 【硬體不穩】 | WHEA 硬體錯誤＋非預期重開同日 | 硬體劣化已實際影響穩定性 |
| 【崩潰→服務失敗】 | 應用程式崩潰（1000/1026）＋服務異常終止（7031/7034）同日 | 可能為同一應用的崩潰導致服務失敗 |
| 【崩潰循環→資源耗盡】 | 服務高頻異常終止（≥100 次）＋資源耗盡（2004）同日 | 崩潰重啟循環正在拖垮整機 |
| 【時間偏移→驗證失敗】 | 時間同步失敗＋登入失敗同日 | 時鐘偏移造成的假性攻擊訊號（仍需排除真攻擊） |

關聯訊號在 prompt 中以獨立區塊呈現並明確標注「由程式確定性比對，不是猜測」，
執行輸出以紅色🔗區塊顯示，風險報告的整體摘要一併列出，也存入歷史資料庫的 `CorrelationAlerts` 欄位。

**`PatternId`**（`Analysis/CorrelationPatternIds.cs`）：上表 17 個 Windows 模式
＋ Linux 面 2 個模式（SSH 破解得手／不確定，見 docs/LINUX-RULES.md）合計 19 個模式，各自有
穩定不隨文字說明變動的 Id 常數。用途是**關聯模式抑制**的比對鍵（`RuleSuppression.TargetType=
Correlation`，見 docs/RULES-SPEC.md「抑制目標四型」）——過去關聯訊號只能整層 log 分析器一起
開關，沒有針對單一模式的抑制路徑；`CorrelationFinding.PatternId` 現在是 `public required
string`，`CorrelationAnalyzer`／`LinuxCorrelationAnalyzer` 的每個 `findings.Add(...)` 都標好
對應常數，`CorrelationAnalyzerRuleAlignmentTests` 涵蓋 Id 與模式的對齊不漂移。

### 正常 RDP 使用不會誤報的設計

納入 RDP 連線紀錄擴大了入侵偵測面，但**日常遠端維運絕不能被誤判成入侵**。防誤報靠三道設計：
RDP 事件規則一律 Low（不參與風險判定、不觸發「首次出現」告警）、無任何 RDP 單獨告警規則、
入侵訊號一律經由「有錨點」的確定性關聯才成立。下列正常情境保證**不產生任何告警**：

| 情境 | 結果 |
|---|---|
| 管理員每天 RDP 維運（21/24/25/1149 數十筆） | Low 簽章、趨勢 Recurring，零告警、零風險拉升 |
| 新員工第一次 RDP 登入 | 簽章鍵不含帳號，非新簽章，無變化 |
| 同一天大量正常重連（會議室機、跳板機） | 需達「今日 ≥5 且 ≥ 頻道歷史基準 2 倍」才出頻率上升告警（既有 TrendAnalyzer 門檻，且新頻道暖身 3 天內不吵）——RDP 用量真暴增本來就值得看一眼，屬預期而非誤報 |
| 正常成功登入 4624 | 平日完全不收集；只在同日 4625 ≥ 10 的嫌疑日才條件式撈取，且需帳號/IP 交集才成立訊號 |
| 零星登入失敗（< 10 次/日） | 4625 規則門檻 10、未達降級，無關聯錨點 |
| 管理員 RDP 登入後建帳號/裝服務 | **刻意不建**「RDP 登入＋帳號建立」這類無錨點模式——管理員遠端建帳號是日常維運，必誤報 |
| 頻道上線第一天 | 全部趨勢 Unknown、前 3 天暖身期不產生 New/Rising 告警 |

只有兩種有錨點的組合才把 RDP 成功登入判成入侵：**【破解得手】**（同日 4625 ≥ 10 且相同帳號/IP
出現成功登入，成功面現含 RDP 工作階段）與**【暴力破解→RDP 得手】**（昨日暴力破解的來源 IP、
今日以 RDP 成功登入同一 IP）。兩者都需要「暴力破解達門檻」加「帳號/IP 交集」，正常使用不會命中。

### 讀取方式：傳統日誌走 classic API，Operational 頻道走 EventLogReader

Defender／RDP 這類新式 `Microsoft-Windows-*/Operational` 頻道要靠 `EventLogReader`
（`System.Diagnostics.Eventing.Reader`）才能讀到——classic `System.Diagnostics.EventLog` 只能讀
`System`／`Application`／`Security` 三大傳統日誌。**採混合式讀取**：傳統三個日誌仍走 classic
`EventLog` API，新式 Operational 頻道才走 `EventLogReader`。原因是 `EventLogReader.ProviderName`
回傳完整 manifest 名（如 `Microsoft-Windows-DistributedCOM`），與 classic `EventLogEntry.Source`
的註冊短來源名（如 `DCOM`）不同——若把三大日誌也改用 reader，聚合鍵 `(LogName, Source, EventId,
EntryType)` 會全面漂移、既有歷史的趨勢比對全數斷成「首次出現」。混合式讓既有日誌識別鍵零改變、
新頻道又能讀進來。

新頻道有 **3 天暖身期**（`ChannelCoverage.WarmupDays`）：上線首日所有簽章都是「首次出現」，暖身
期內不產生 New/Rising 告警、不升級嚴重度，避免切換日的告警風暴；規則層與關聯層不受影響（Defender
真驗出病毒照樣拉高風險）。掃描頻道可在 Web「系統管理 > 設定 > 分析參數」頁調整，以資料庫
為唯一事實來源（`appsettings.json` 的 `Analysis.Channels` 已退役）。

### 頻率趨勢比對的判定規則（`TrendAnalyzer`）

每個事件簽章 `(LogName, Source, EventId, EntryType)` 逐一與歷史比對：

| 趨勢 | 判定條件 | 後續動作 |
|---|---|---|
| **首次出現 (New)** | 近 14 日歷史中從未發生 | 嚴重度 High 以上者列入頻率異常告警 |
| **頻率上升 (Rising)** | 今日次數 ≥ 5 **且** ≥ 歷史基準 2 倍 | `Trend` 一律標記 `Rising`、嚴重度自動升一級（封頂「高」；原本就是「高」的改標記「重大」旗標 → 觸發紅色告警）；**是否列入頻率異常告警文字、參與風險等級判定，另有嚴重度閘門，見下** |
| **重複出現 (Recurring)** | 歷史中出現過、頻率相近 | 附註出現天數與基準次數供 AI 判讀 |
| **頻率下降 (Declining)** | 歷史基準 ≥ 5 且今日次數 ≤ 基準一半 | 附註（問題可能已緩解） |

**Rising 嚴重度閘門**：只有**升級前**嚴重度已達 Medium 以上的簽章，
Rising 才會產生告警文字、進 `TrendAlerts`／`SuppressedTrendAlerts`（視是否被抑制）並參與
`ComputeRuleBasedRisk` 的風險等級判定；Low 嚴重度簽章的 Rising **不吵、不拉風險**，但
`Trend`／`Severity` 欄位仍照常標記與升級——資訊沒有遺失，只是不再單靠「量的變化」把一個
本質上輕微的問題拉成需要人工介入的中風險日（Low 嚴重度雜訊型簽章天然量大、頻率波動本來就
劇烈，過去的無閘門設計等於任何一個雜訊簽章某天多發生幾次就能觸發告警）。判定點在
`TrendAnalyzer.Apply`：`preEscalationSeverity`（升級前的原始嚴重度）在寫入 `sig.Severity`
之前先捕捉，`preEscalationSeverity >= IssueSeverity.Medium` 才把告警文字送進
`alerts`／`alertRefs`，否則整段略過（不進 `suppressedAlerts`——那是給「本來會吵、但被使用者
主動抑制」的東西，被閘門擋下的不算被抑制，是本來就不該吵）。

**爆量例外**：Rising 閘門把 Low 簽章的頻率上升全部靜音，但這
連帶讓「未命中任何規則」的事件（一律 `ClearToOther` → `Severity=Low`）失去唯一的瞬時異常
出口——一台主機某天冒出遠超日常量級的未知簽章，閘門會讓它完全無聲。因此 Low 簽章的 Rising
多一條例外：升級前 `Severity < Medium` 時，若 `今日次數 ≥ 歷史基準 × 10` **或**
`今日次數 ≥ 100`（兩者滿足其一），仍產生告警文字，但用「**頻率暴增**」與一般「頻率上升」
區分（讓讀者知道這是打破閘門進來的）。門檻刻意遠高於一般 Rising 的 2 倍：Low 簽章天然雜訊多，
日常 2~3 倍波動很常見，只有真正的爆量才該打破閘門；絕對量門檻（100）兜底的是**基準較大**的
情境——基準 15 時 10 倍要 150 筆才觸發，今日 100 筆的真暴增反而會被倍率門檻漏掉（基準很小時
10 倍本來就容易達到，走倍率條件即可）。

### Low 簽章的趨勢出口

未命中任何規則的簽章（`Severity=Low`）在趨勢層有三條能被看見的路：

| 出口 | 判定 | 說明 |
|---|---|---|
| **首次出現且大量** | `TrendAnalyzer` New 分支例外 | 從未出現過、單日次數達絕對量 100 筆 |
| **瞬時爆量** | `TrendAnalyzer` Rising 分支例外（上方） | 單日次數達基準 10 倍或絕對量 100 筆 |
| **持續惡化** | `SlowTrendAnalyzer` 無嚴重度閘門 | 近 7 天總量 ≥ 前 7 天總量 × 1.5（且 ≥ `MinRecentCount`） |

**首次出現且大量**：`Severity < High` 時，若`今日次數 ≥ 100`（`SurgeMinCount`，與 Rising 分支
共用同一個常數）仍產生告警，文字用「**首次出現且大量**」與一般「首次出現」區分。首次出現
沒有歷史基準可乘，因此**只用絕對量門檻，不像 Rising 分支的爆量例外還有 `SurgeFactor`（10 倍）
條件**。同樣受 `channelWarmingUp` 閘門保護——新頻道上線第一天所有簽章都是首次出現，這正是
暖身期要防的切換日風暴。

**命中此出口時嚴重度也會升一級**：比照 Rising 分支呼叫 `Escalate()`（Low→Medium），
使其符合 `RiskReportService.SelectFocusIssues` 篩進報告深入分析區塊的條件
（`Severity>=High || Trend==Rising || (Trend==New && Severity>=Medium)`）——嚴重度若沒升級，
這個訊號雖能靠告警文字把當天拉到中風險（`ComputeRuleBasedRisk` 的 `trendAlerts.Count>0`），
卻進不了 focus，報告裡只剩總覽一行文字，拿不到分類區塊、原始 log 樣本或知識庫/AI 寫的
處置建議。

`SlowTrendAnalyzer` **刻意不套用 Rising 同款的嚴重度閘門**——這不是遺漏，是設計取捨：
Low 簽章天然雜訊多、單日波動劇烈的理由在 7 天窗口被視窗長度與 `MinRecentCount` 部分抵銷，
且它是「持續性未知訊號緩慢成長」唯一能被抓到的安全網（單日層的爆量例外只抓瞬時暴增，
抓不到每天小幅加量、始終不觸發單日門檻的節奏）。代價是：日常就會週期性觸發的 Low 簽章，
7 天窗口翻倍時仍會週期性產生「慢速惡化」告警——這是換來安全網的必要噪音，不是待修的不一致。
**未來若要為 `SlowTrendAnalyzer` 補上與 `TrendAnalyzer` 一致的嚴重度閘門，必須先確認
上方三條出口是否都還在**，否則 Low 簽章會回到趨勢出口不完整的狀態。

未命中規則事件的完整涵蓋總結（四種時間尺度）：

| 時間尺度 | 出口 |
|---|---|
| 首次出現 | `TrendAnalyzer` New 分支例外，限單日達絕對量門檻 |
| 單日爆量 | `TrendAnalyzer` Rising 分支爆量例外 |
| 持續成長（7 天） | `SlowTrendAnalyzer`（無閘門） |
| 總量突增（不分簽章） | 整體錯誤量／稽核量突增（見下方，今日 ≥10 且 ≥ 基準 2 倍） |

另外比對**整體錯誤總量**：今日錯誤 ≥ 10 筆且 ≥ 近 14 日基準 2 倍時，即使個別事件都不顯眼也會告警
（多個不同來源同時出錯常是連鎖故障的開端）。安全稽核事件總量（如 4625 登入失敗）另做同構比對。
兩者皆可個別抑制（`RuleSuppression.TargetType=Volume`，`VolumeKind=error`／`audit`，
見 docs/RULES-SPEC.md「抑制目標四型」）：某台主機的錯誤或稽核事件量本來就大、
波動屬於正常範圍時，可關閉對應的總量告警，抑制期間仍照常聚合計數，只是不吵、不拉風險。

「今日次數 ≥ 5」的最低門檻是為了避免 1 次變 2 次這種統計雜訊觸發告警；
所有比對結果（前一日次數、歷史基準、出現天數）都會附註在 prompt 的事件行上，
並存入歷史紀錄的 `TopIssues` 與 `TrendAlerts` 欄位。

**基準採中位數，不是平均值**：14 天內若有一天異常爆量（例如單日 100 次、
其餘 13 天都是 2 次），平均值會被墊高到約 9 次，讓後續真正異常的 15 次反而顯得「沒超過兩倍」
而被平均值蓋掉；中位數對這種離群值有抵抗力，14 天後舊的爆量值自然被換血掉，不需要額外的
排除邏輯。C# 屬性名稱 `HistoryDailyAverage` 維持不變（blob JSON 序列化相容，欄位語意已更新
但不改名以免既有資料 round-trip 出問題）。

**總量層的基準進一步只取「非零日」的中位數**：簽章層的中位數天然只吃非零值
（一個簽章只在真的出現過的日子才有歷史紀錄），但整體錯誤量／稽核量原本是對含 0 的完整可靠
歷史取中位數——錯誤只在部分日子出現的主機，中位數會落在 0，0×2 恆為 0、倍率條件恆真，
規則悄悄退化成「今日 ≥10 筆」的固定門檻，告警還會印出誤導的「基準 0 筆」。改成非零日中位數後
兩層基準語意一致；歷史中一筆非零日都沒有的主機，今日突然 ≥10 筆仍以固定門檻告警
（平常零錯誤的主機突然冒錯誤本來就值得一提），只是文案誠實說「近 N 日多數日無錯誤」。

### 為什麼歷史紀錄能「提早」發現問題

很多故障不是突然發生的，而是**訊號頻率逐漸上升**：

- 硬碟壞掉前幾週，`disk` Event 153 / `storahci` Event 129 會從偶發變成每天數十次
- 記憶體劣化時，WHEA corrected error 次數會持續攀升（系統還能自我修正，所以不會當機，但這是換料的最佳時機）
- 暴力破解通常先有低頻率的探測（每天幾次 4625），確認服務存在後才開始大量嘗試

單看一天的 log 看不出這些，所以每天的分析結果（錯誤數、警告數、重點問題簽章與次數、風險等級）
會壓縮成一行 JSON 存入歷史。**這是新問題、重複發生、還是正在惡化**，由 `TrendAnalyzer`
（單日 vs 歷史基準）與 `SlowTrendAnalyzer`（近 7 天 vs 前 7 天總量）兩層
確定性判定，AI 只負責把已經判定好的結論接續前幾天脈絡講成白話（`trend_story` 欄位）。

### 問題嚴重度與「重大」標註

**問題嚴重度只有三級：高／中／低**（`IssueSeverity`，掛在單一問題簽章上）。
在此之上，部分規則帶「**重大**」旗標（`KnownIssueRule.ElevatesDayRisk`）——
**這類問題只要出現（且未被抑制），當天就直接判定為高風險日**，例如磁碟故障、
安全稽核日誌被清除。畫面上以「高＋重大」兩顆徽章並列呈現，規則維護頁可逐條調整。

嚴重度刻意只維持三級、不疊加第四級：「命中即列為高風險日」這個職責完全交給獨立的
「重大」旗標承載，避免嚴重度（問題層級）與日風險等級（高/中/低風險日）這兩套不可互推的
層級字面相撞，造成「詳情頁顯示高風險、但最嚴重的問題只有中」這類困惑（歷史沿革見
docs/archive/HISTORY.md #1）。

**日風險等級**（高/中/低風險日）是「主機×日期」整天的批次判定結果，不是任何單一問題
嚴重度的別名，兩者不可互相推導（`RiskLevels` 與 `IssueSeverity` 是兩個獨立列舉）。

### 監控的危險訊號清單

下表「嚴重度」欄的「高（重大）」＝ 高嚴重度且帶「重大」旗標（命中即列為高風險日）。

#### 硬體故障前兆（System log）

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| disk | 7, 11, 51, 52, 153 | 磁碟 I/O 錯誤、壞軌前兆 — **硬碟即將故障最直接的訊號** | 高（重大） |
| Ntfs | 55, 98, 130, 140, 141 | 檔案系統損毀跡象 | 高（重大） |
| storahci / stornvme | 129 | 儲存控制器逾時重置，常見於硬碟劣化、線材或背板異常 | High |
| WHEA-Logger | （全部） | CPU / 記憶體 / PCIe 硬體錯誤；corrected error 上升＝硬體劣化中 | 高（重大） |
| Kernel-Power | 41 | 非預期斷電或當機重開（電源、過熱、硬體不穩） | 高（重大） |
| EventLog | 6008 | 非預期關機 | High |
| Resource-Exhaustion-Detector | 2004 | 虛擬記憶體即將耗盡（可能有程式記憶體洩漏） | High |
| srv | 2013 | 磁碟空間即將不足 | Medium |

#### 入侵跡象（Security log + System log）

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| Security-Auditing | 4625 | 登入失敗；**單日 ≥10 次**視為暴力破解攻擊 | High |
| Security-Auditing | 4740 | 帳戶被鎖定（通常是暴力破解的結果） | High |
| Security-Auditing | 1102 | **安全稽核日誌被清除 — 入侵者滅跡的典型行為，應立即調查** | 高（重大） |
| Security-Auditing | 4719 | 稽核原則被變更（關閉記錄以躲避偵測） | High |
| Security-Auditing | 4720, 4722, 4724 | 帳戶建立 / 啟用 / 密碼被重設 — 入侵者建立立足點 | High |
| Security-Auditing | 4728, 4732, 4756 | 帳戶被加入特權群組（如 Administrators）— 典型提權手法。**同批 EventId（含 4729/4733/4757、4670、4717/4718/4719/4907）在 NetIQ 主機另會逐則寫成「權限異動待辦」**（`HostDayPostProcessor.RecordPermissionChanges`，與規則命中互不影響） | High |
| Security-Auditing | 4729, 4733, 4757 | 帳戶被**移出**特權群組 — 也可能是提權得手後清除紀錄 | High |
| Security-Auditing | 4697, 4698 | 安裝服務 / 建立排程任務 — 常見持久化手法 | High |
| Security-Auditing | 4670 | 檔案/資料夾/登錄物件的**權限 (ACL) 被變更** | High |
| Security-Auditing | 4907 | 物件的**稽核設定 (SACL) 被變更** — 針對性關閉稽核以躲避偵測 | 高（重大） |
| Security-Auditing | 4717, 4718 | 系統存取權限被授予/移除（User Rights Assignment） | High |
| Security-Auditing | 4704, 4705 | 使用者權限指派被新增/移除 | High |
| Security-Auditing | 4703 | 權杖 (token) 特殊權限於執行期間被調整 — 常見提權攻擊手法 | High |
| Security-Auditing | 4735 | 安全群組的內容或權限被變更 | High |
| Security-Auditing | 4739 | 網域原則被變更（僅網域控制站） | High |
| Security-Auditing | 4731, 4734 | 本機安全群組被建立/刪除 | Medium |
| Service Control Manager | 7045 | 安裝新服務 — 非預期時可能是後門植入 | High |

> 上表的權限/角色事件無論是「授予」還是「移除」都收錄——移除同樣值得關注
> （可能是入侵者提權得手後清除操作紀錄）。這些事件都需要 Security log 讀取權限；
> 若無法以系統管理員權限執行，改用下方「權限/角色異動監控」章節的機制。

#### 權限異動類別（兩來源共用的分類層）

寫進「權限異動待辦」的每一筆都帶一個**類別 key**，由 `change_type` 與 EventId 以純函式推導
（`PermissionCategory.Resolve`，純函式是為了讓舊資料能在遷移時離線重算）。`change_type`
現行產生 9 個相異值、兩個來源，其中「成員新增」「成員移除」兩個值兩來源共用
（另有既有資料才會出現的舊值，見下表 `summary`）：

| 類別 key | 中文標籤 | 涵蓋的 change_type |
|---|---|---|
| `group_member` | 群組成員異動 | 成員新增、成員移除（NetIQ 4728/4732/4756、4729/4733/4757；本機 Administrators 群組） |
| `folder_acl` | 資料夾權限異動 | 權限新增（ACL 規則）、權限移除（ACL 規則）、權限變更（4670） |
| `owner_change` | 擁有者變更 | 擁有者變更 |
| `folder_access` | 資料夾存取狀態 | 無法存取、恢復可存取 |
| `audit_policy` | 稽核政策變更 | 稽核政策變更（4717／4718／4719／4907） |
| `summary` | 權限異動彙總 | 權限異動（彙總）——不再產生的舊值，僅既有歷史列會落在此類別 |
| `other` | 其他 | 推導不出對應類別時的退路，恆非空 |

另有 `is_privileged_target` 旗標：類別為 `group_member`、`change_type` 是「成員新增」、
且對象命中特權群組關鍵字（Administrators、Domain Admins、Enterprise Admins、Schema Admins、
Account Operators、Backup Operators、本機 Administrators 群組，不分大小寫）時為真。
只標「加入」不標「移除」——這個旗標的用途是提示提權。

**稽核政策類事件的異動前後值**：4717／4718 取被授予／移除的存取權，4719 取政策類別與子類別
（有變更內容時附上），4907 取物件 SACL 的前後值（取不到退回變更內容）。抽不到時一律填
「（訊息未提供）」而**不是空字串**——空字串在畫面上與「沒有異動」無法區分。

**操作者與目標帳號分開擷取**：操作者取 NetIQ 的 `sun` 欄位，沒有時退而取訊息 `Subject`／
「主體」區段的帳戶名稱；目標帳號只取 `Member`／「成員」區段。剖析是**分區段**的——
`Subject` 與 `Member` 各自獨立，操作者在結構上不可能流進成員欄位。

#### 惡意程式與防護狀態（Microsoft Defender Operational 頻道，經 EventLogReader 讀取）

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| Windows Defender | 1006, 1116 | 偵測到惡意程式（偵測本身即明確訊號） | High |
| Windows Defender | 1007, 1117 | 已對惡意程式採取處置（隔離/移除） | Medium |
| Windows Defender | 1008, 1118, 1119 | **處置失敗，惡意程式可能仍活躍** — 應立即隔離主機 | 高（重大） |
| Windows Defender | 5001 | 即時防護被關閉（管理員合法操作 vs 入侵者解除防護，需確認來源） | High |
| Windows Defender | 5010, 5012 | 防毒/掃描被停用（第三方防毒接管屬正常情境） | Medium |
| Windows Defender | 1005 | 排程掃描失敗，防護涵蓋率可能不完整 | Medium |
| Windows Defender | 2001, 2003, 2004 | 病毒碼/引擎更新失敗；**單日 ≥3 次**才升 Medium（偶發網路失敗屬雜訊） | Medium |

> Defender 事件天生低誤報——偵測到惡意程式本身就是訊號。分級的關鍵在「已處置（Medium）vs
> 處置失敗（高＋重大）」，刻意**不做**「1116 之後沒看到 1117」這類缺席推論（資料不完整時會誤報）。
> 主機未安裝 Defender（如第三方防毒取代）時該頻道不存在，程式申報「不適用」而非錯誤。

#### 遠端桌面連線（RDP TerminalServices Operational 頻道）

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| TerminalServices-LocalSessionManager | 21, 24, 25 | RDP 工作階段登入/中斷/重連 | **Low（收集用，非告警）** |
| TerminalServices-RemoteConnectionManager | 1149 | RDP 驗證成功 | **Low（收集用，非告警）** |

> **這兩條規則刻意設為 Low：正常遠端桌面使用即會產生，本身不是告警訊號。** 收集目的是提供
> 關聯分析（【破解得手】【暴力破解→RDP 得手】需要成功登入的帳號/IP）與趨勢基準。入侵訊號一律
> 經由「有錨點」的確定性關聯（暴力破解達門檻、帳號/IP 交集）才成立，見下方「正常 RDP 使用不會
> 誤報的設計」。RDP 訊息的帳號（`User: DOMAIN\user`）會同時抽出純帳號，才能與 4625 的純帳號對得上。

#### 服務穩定性（System / Application log）

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| Service Control Manager | 7031, 7034 | 服務異常終止；單日 ≥3 次才升為 Medium（偶發屬正常雜訊） | Medium |
| Service Control Manager | 7000, 7001 | 服務啟動失敗 | Medium |
| Application Error | 1000 | 應用程式反覆崩潰（≥3 次）——服務完全掛掉前的先兆 | Medium |
| .NET Runtime | 1026 | .NET 未處理例外反覆發生（≥3 次） | Medium |

#### 營運健康（備份 / 時間 / 憑證 / 網域）

不是硬體壞、也不是被入侵，但放著不管就會演變成停機或災難的訊號：

| 來源 | Event ID | 意義 | 嚴重度 |
|---|---|---|---|
| Microsoft-Windows-Backup | 517 | **備份失敗**——備份損壞往往到需要還原時才發現 | High |
| VSS | （全部錯誤） | 陰影複製錯誤，會導致備份失敗或不完整 | Medium |
| Time-Service | 29, 36, 47, 50 | 時間同步失敗；偏移 >5 分鐘 Kerberos 驗證全面失敗 | Medium |
| CertificateServicesClient-AutoEnrollment | 64 | **憑證即將到期**——最容易預防的停機原因 | Medium |
| Schannel | 36870 | TLS 憑證私鑰存取失敗（憑證過期或權限異常） | Medium |
| GroupPolicy | 1030, 1058 | 群組原則套用失敗（≥3 次），SYSVOL/DC 連線問題先兆 | Medium |
| NETLOGON | 5719 | 無法連上網域控制站（≥3 次） | Medium |
| DhcpServer | 1020 | DHCP 位址池即將耗盡（僅 DHCP 角色會出現） | Medium |
| WindowsUpdateClient | 20 | 更新安裝失敗，持續失敗累積未修補的安全風險 | Low |

> Security log 的 SuccessAudit 事件量極大（每次登入都記一筆），所以只挑
> `KnownIssueCatalog.SecurityAuditWatchlist` 內的高價值事件納入，其餘忽略。

#### Linux syslog（seed v4；Sentinel 取數＋SSH 攻擊鏈關聯已全面落地）

Linux 主機沒有 Event ID，規則改以 **program（syslog identifier）＋訊息子字串**比對，或
Sentinel 正規化後的事件名（兩條路 OR，完整規則模型與種子清單見
[docs/LINUX-RULES.md](docs/LINUX-RULES.md)）。主機的 `Os` 欄位（Web 主機頁維護）決定它套用
哪個平台的規則面。

| program | 訊息關鍵字（任一命中） | 意義 | 嚴重度 |
|---|---|---|---|
| sshd | Failed password / authentication failure / Invalid user | SSH 登入失敗；**單日 ≥10 次**視為暴力破解 | High |
| sshd | Accepted password / Accepted publickey | SSH 登入成功 | **Low（收集用，非告警）** |
| sudo | authentication failure / incorrect password attempt | sudo 提權驗證失敗（≥5 次） | Medium |
| su | authentication failure / incorrect password / FAILED su | su 提權驗證失敗（≥5 次） | Medium |
| useradd/usermod/userdel（`user`） | （不看訊息） | 帳號建立/修改/刪除 — 入侵者建立立足點 | High |
| groupadd/groupmod/groupdel（`group`） | （不看訊息） | 群組異動 | High |
| gpasswd | （不看訊息） | 帳號被加入/移出群組 — 加入 sudo/wheel 即提權 | High |
| auditd | audit daemon is exiting / stopping | **稽核服務被停止 — 滅跡的典型行為** | 高（重大） |
| kernel | I/O error / Buffer I/O error / EXT4-fs error / XFS internal error | 磁碟或檔案系統錯誤 | 高（重大） |
| smartd | Prefailure / FAILED SMART self-check / predicted TO FAIL | S.M.A.R.T. 預警硬碟即將故障 | 高（重大） |
| kernel | Hardware Error / Machine Check / mce: | CPU/記憶體/PCIe 硬體錯誤 | 高（重大） |
| kernel | Out of memory / oom-kill / Killed process | 記憶體耗盡，核心強制終止程序 | High |
| systemd | entered failed state / Failed to start / Main process exited | 服務啟動失敗或異常終止（≥3 次） | Medium |
| kernel | segfault | 應用程式反覆區段錯誤（≥3 次） | Medium |
| chronyd | Can't synchronise / no reachable sources | 時間同步失敗（≥3 次） | Medium |
| ntpd | time reset / synchronisation lost / no servers reachable | 時間同步失敗（≥3 次） | Medium |
| CRON | FAILED / (CRON) ERROR | 排程任務執行失敗（≥3 次） | Medium |

> **比對順序有意義**：`ProgramPattern` 是子字串比對，`"sudo"` 包含 `"su"`，所以 sudo 規則必須排在
> su 之前，否則 sudo 的事件會被 su 規則先攔走。單元測試的逐條命中驗證會抓到這類錯誤。
>
> **SSH 登入成功刻意設為 Low**：與 RDP 同一個防誤報設計——日常遠端維運即會產生，本身不是告警訊號，
> 收集目的是趨勢基準與未來 SSH 關聯鏈的成功面。
>
> **現況**：規則模型、種子、驗證、Web 維護介面（規則頁的「Linux規則」分頁）、
> **事件模型與簽章聚合**（`EventKey` 五元組分組鍵、`LogAggregator.ClassifyLinux`、
> `IssueSignatureKey` 相容擴充）、**Sentinel 實際取數分支**
> （`SentinelFieldMap`／`SentinelEventMapper.MapLinux`／`SentinelQueryBuilder.BuildLinuxFilter`，
> 見 [docs/NETIQ-API-REFERENCE.md](docs/NETIQ-API-REFERENCE.md) §4a）、以及
> **SSH 攻擊鏈關聯**（`LinuxCorrelationAnalyzer`，見下方關聯層說明）全部完成並有專屬測試覆蓋
> （見 [docs/LINUX-RULES.md](docs/LINUX-RULES.md)）。上表的訊息關鍵字已對照真實環境輸出
> （program 量級、`msg` 片語查詢行為、sshd 樣本全文）逐項核對，零矛盾證據，seed 版本
> 維持 v4。
> 也就是說 Linux 主機從掃描精靈納入、排程／立即執行、Sentinel 取數、五層偵測到 AI 判讀，
> 已與 Windows 主機同一條管線走完整趟，沒有殘留的止血擋板或短路（見
> [docs/BACKLOG.md](docs/BACKLOG.md)）。本環境的 **Windows 與 Linux 已拆分為不同的 Sentinel**
> （同一台 Sentinel 不混平台，故 OS 標記落在 Sentinel 層級而非逐事件判別；唯一例外是同一台
> Sentinel 上另有 CEF collector 路徑，欄位形狀細節見 NETIQ-API-REFERENCE.md §4a）。
>
> **五層偵測對 Linux 主機的適用性**：
>
> | 層 | 適用 Linux？ | 說明 |
> |---|---|---|
> | 規則層 | ✓ | `KnownIssueCatalog.ClassifyLinux`，program＋訊息子字串比對 |
> | 趨勢層 | ✓ | `TrendAnalyzer.SameIssue` 五元組比對，隔離「同 program 不同規則」 |
> | 慢速趨勢層 | ✓ | `SlowTrendAnalyzer` 同上；`ChannelCoverage.WasRead("Linux")` 恆真 |
> | 關聯層 | 部分（僅 SSH 破解得手一項） | `LinuxCorrelationAnalyzer` 獨立於 Windows 的 `CorrelationAnalyzer`（機制完全不同：regex 解析 `msg` 文字取 user/ip 再找重疊，而非 EventId 群組比對）；同日 `builtin-linux-ssh-bruteforce` 達門檻＋`builtin-linux-ssh-accept` 存在時比對兩者的 (user, ip)，重疊→精確命中（High），無重疊但有解析失敗樣本→降級提醒（Medium），全數解析成功且無重疊→誠實不告警（不是漏做）。其餘 Windows 面的組合模式（帳號異動鏈／新服務鏈／儲存連鎖等）目前不適用於 Linux 主機，`UncoveredChecks` 會明講 |
> | AI 判讀層 | ✓ | 與平台無關，餵給 AI 的是聚合後的統計摘要，Linux 主機的規則/趨勢/慢速趨勢/關聯結果一樣能被翻譯成白話；「詢問 AI 現場取數」的即時查詢分支也已改用 program 子句支援 Linux（原本沿用 Windows 的 EventId 子句會恆查 0 筆） |

### 給 AI 判讀的輔助資訊（除了事件本身）

| 資訊 | 來源 | 為什麼需要 |
|---|---|---|
| 發生時段 `FirstSeen`~`LastSeen` | 聚合時計算 | 「4625 x50 集中在凌晨 03:00~03:10」和「分散全天」意義完全不同 |
| 訊息多樣性（相異內容數 + 3 則範例） | 聚合時計算 | 區分「同一服務掛 10 次」（服務有問題）和「10 個服務各掛一次」（系統層問題） |
| Security 事件的帳號/IP 彙總 | 從完整訊息抽取（範例訊息 200 字常截不到這些欄位） | 判斷是「單一 IP 打單一帳號」還是「掃描多帳號」——入侵分析最關鍵的依據 |
| 星期幾 | 日期換算 | 讓模型認出「每週日固定維護重開機」這類正常規律 |
| 非低風險歷史日的當日結論 | 歷史紀錄的 `Summary` | 模型看得到先前判讀脈絡：這問題之前判定過什麼、是否已知原因 |
| 伺服器角色描述 | `Program.cs` 的 `ServerDescription`（自行填寫） | 同一事件在 AD 網域控制站和一般檔案伺服器上的嚴重性不同 |
| 稽核事件總量趨勢 | `TrendAnalyzer` 獨立比對（4625 等稽核事件不計入錯誤數） | 安全事件總量暴增時即使個別簽章不顯眼也會告警 |

> 注意：classic EventLog API 讀取新式 **Critical 等級**事件（如 Kernel-Power 41）時，
> `EntryType` 可能為 0（列舉中沒有 Critical 值）。程式已特別納入這類事件並計入錯誤數，
> 避免最嚴重的事件反而被過濾掉。

## 資料完整性與涵蓋率誠實申報

兩個容易被忽略、卻會讓「沒告警」被誤讀成「沒問題」的情況，程式已明確標注：

- **回補時 Event Log 已被覆蓋**：`EventLogService` 倒序掃描到日誌最舊一筆仍未低於請求區間起點時，
  代表該來源保留的歷史不足以涵蓋整個回補區間（較舊的事件已被系統覆蓋，不是真的沒事件）。
  這幾天的紀錄會標記 `DataIncomplete = true`，`TrendAnalyzer` 計算 14 日基準時排除這些日子，
  避免不完整的一天把基準值墊低/墊高，讓之後的正常量被誤判為異常（或反過來蓋掉真異常）。
- **Security log 本次無法讀取**（無系統管理員權限）：紀錄標記 `SecurityLogAvailable = false`，
  執行輸出與風險報告會逐條列出因此停用的偵測項目（入侵跡象規則表、涉及 Security 的關聯模式、
  4624 破解得手比對、安全稽核事件總量趨勢），而不是一句「讀取失敗」帶過——讓看報告的人知道
  「沒告警 ≠ 沒問題，是沒看」。趨勢基準計算也會排除這些日子的 Security 簽章，避免權限恢復後
  的正常量被誤判成「首次出現」或「頻率上升」。

## 體檢（WeeklyCheckupService：due-date 輪巡＋確定性閘門）

除了每日分析，另外做週期性的「期間回顧」。「找出單看每天都不明顯、但整週合起來是持續
累積或緩慢惡化的訊號」這件**發現**的工作，由每日全主機執行的確定性 `SlowTrendAnalyzer`
（近 7 天 vs 前 7 天總量比較，見上方「五層偵測」）負責，偵測延遲最短可達 1 天。
體檢因此只負責**講這段期間的故事**：把窗口內已經確定有訊號的日子，接續上次體檢的結論寫成
一段白話回顧。

- **觸發時機（due-date 輪巡）**：`appsettings.json` 的
  `Analysis.CheckupIntervalDays`（預設 7 天）；距上次體檢達此天數即到期執行，不綁定固定星期幾，
  單機情境下等同「每 N 天做一次」，漏跑（機器關機、排程失敗）時下次執行自動補上，
  體檢不會因此消失。
- **確定性閘門**：窗口內任一天有風險（非「低」）、趨勢異常或關聯訊號，才呼叫 AI 敘事；
  三層皆無訊號的窗口直接寫固定結論「本期無累積性異常，程式比對通過」，不消耗 AI 呼叫——
  安靜的期間本來就沒有故事可講，這是多主機規模下 AI 時間預算能否成立的關鍵之一
  （詳見 [docs/archive/HISTORY.md](docs/archive/HISTORY.md)）。
- **AI 失敗不消耗額度**：閘門判定有訊號、實際呼叫 AI 卻失敗時，該次**不寫入歷史**
  （`WeeklyCheckupResult.Completed = false`），讓下次執行的補跑機制重試，而不是把這一期的
  體檢額度用掉。
- **輸入塑形**：不是把窗口內歷史原樣塞給模型——程式先彙整成「每個問題簽章一行、含期內逐日
  次數」，依嚴重度取前 40 行，控制 prompt 在小模型可負擔的範圍內；同時帶入上次體檢結論，
  讓模型知道「上次說要觀察的那件事後來如何」。
- **輸出**：結論寫入當日歷史紀錄的 `WeeklyCheckup` 欄位；**有發現才**輸出
  `export\{日期}_週檢.txt`（檔名沿用既有慣例），無累積性異常的期間不產生檔案。


---

## 小模型（Gemma 27B/31B 級）最大化效能的策略

小模型的限制：context 有效長度短、大海撈針能力弱、格式遵循不穩定、長輸入時容易「迷失在中間」。
對策全部落實在程式裡：

1. **餵摘要不餵原文，且呈現量有硬上限** — `LogAggregator` 把上千筆原始 log 依
   `(LogName, Source, EventId, EntryType)` 分組成最多 50 組統計；主 prompt 呈現層再設上限：
   規則命中問題最多逐項列 12 個（各附 2 則 200 字範例）、其他事件最多 10 個（各 1 則）、
   頻率異常最多 15 行。超出上限的項目**不是折疊消失，而是走前置掃描**（見第 10 點），
   所以主 prompt 有確定的長度上限（約 10KB），又不會有 AI 沒看過的事件。
   平常日通常只有 1~3K token。歷史資料庫與風險報告仍保存完整資訊（每組 3 則範例）。
2. **規則先標記重點** — prompt 中把「規則已命中的問題」和「其他事件」分區呈現，並附上規則的中文說明。
   模型不需要自己知道 Event 153 代表什麼，只需要在已標記的基礎上判讀，大幅降低知識面要求。
3. **歷史壓縮成統計行** — 每天歷史只佔一行（日期、錯誤/警告數、風險、前三大問題簽章），
   14 天歷史約 500 token，趨勢資訊完整但不吃 context。
4. **趨勢數字程式先算好** — LLM 不擅長算術，「昨日幾次、基準幾次、是不是兩倍」由 `TrendAnalyzer`
   預先算好，以「（頻率上升：近14日基準 x2.1、昨日 x3）」的形式附註在事件行上，模型只解讀不計算。
5. **JSON 契約 + grammar 強制 + 低溫度** — prompt 指定回傳
   `{risk_level, headline, story, trend_story, action}` 的 JSON 結構（`risk_level` 欄位語意為
   「AI 的白話翻譯」而非判斷結果，只作為向上拉的安全網），
   並透過 llama.cpp 的 `response_format: json_object`（grammar 約束解碼）從 server 端**保證**
   輸出合法 JSON，temperature 0.2 降低發散。解析端仍有容錯（剝除圍欄、擷取大括號區段），雙保險。
6. **System prompt 限定角色與範圍** — 明確要求「只根據提供的資料判斷、不要臆測」，抑制小模型的幻覺傾向。
7. **單一職責** — 一次呼叫只做一件事（判讀當日 + 對照歷史），不要求模型同時做分類、去重、統計
   （那些程式做得又快又準）。
8. **重大問題永不被截斷** — 聚合結果依嚴重度排序後才取 top 30，最高嚴重度的問題一定進 prompt。
9. **不信任模型的下限** — 規則命中「重大」旗標時風險強制「高」、趨勢層有頻率異常時至少「中」，模型漏判也不影響告警。
10. **依任務性質拆分呼叫，而不是把 prompt 對半切** — 拆分的原則：
    「逐項判斷是否為雜訊」彼此獨立、可以拆；「全局風險判讀」需要跨訊號關聯、不能拆。
    - **前置掃描**（Other 類事件種類超過主 prompt 上限時才觸發）：規則已命中
      的尾巴不再掃描（靜態知識庫已涵蓋處置建議），只掃超出上限的 Other 類項目，分批（每批 20 項）
      給獨立呼叫逐項篩選，值得注意的帶著「掃描意見」回流主分析、其餘以「已檢視 N 項屬雜訊」一行
      帶過；掃描發生在主判斷**之前**，發現的異常能影響當日風險等級。
      低風險日（四層皆無訊號）原則上完全不呼叫 AI，但未分類事件種類達 20 種以上時仍執行掃描——
      那些事件規則層依定義沒看過，不掃就沒有任何一層檢視過它們；掃描若有發現則照常執行主分析，
      讓發現能拉高當日風險等級
    - **主呼叫**（每日一次，低風險日不觸發）：把前四層已確定的結論翻譯成白話標題與敘述
    - **深入分析呼叫**（風險日才觸發，**僅 Other 類別**）：只帶該類別已確認的問題＋
      原始 log 證據，聚焦根因與處置；規則已命中的類別改查靜態知識庫，不再呼叫 AI；
      主分析摘要作為全局脈絡帶入，跨類別資訊不遺失
    把 prompt 對半切的做法則不採用：跨訊號關聯（如新服務安裝＋服務崩潰＋帳號建立）
    會被切斷，還要合併兩份可能矛盾的結論。

### NetIQ 搜尋與 AI 判讀脫鉤（兩階段模型）

多台 Sentinel 併行搜尋時，若每個主機日都要等 AI 判讀完才進下一個，AI 呼叫的延遲會直接拖慢
整條搜尋主線。`NetiqPipelineService` 因此把每個主機日拆成兩段：

1. **統計段**（`LogAnalysisService.BuildStatisticalRecordAsync`）：聚合、規則分類、趨勢／慢速
   趨勢／關聯比對全部是確定性計算，算完立刻寫入紀錄，不等 AI。需要 AI 的日子先寫入暫代內容
   （Headline/Summary 顯示「統計已完成，AI 分析排隊中」），並標記 `AiPending = true`。
2. **AI 段**（`LogAnalysisService.CompleteAiAsync`）：前置掃描＋主分析＋深入分析報告，交給
   `AiFollowupQueue`（bounded channel）背景消費——搜尋主線把工作丟進佇列就繼續處理下一個
   主機日，不等待。單一背景消費者依序處理，`AttachAiResult` 完成後覆寫暫代欄位（含抽出欄
   `RiskLevel` 同步）並把 `AiPending` 改回 `false`。

**`AiPending` 三態**（`DailyAnalysisRecord`）：
- `AiAnalyzed=false` 且 `AiPending=false`：AI 判定不需要（低風險日）或已嘗試但失敗——既有的
  「統計模式紀錄」語意，行為不變。
- `AiPending=true`：統計段已寫入，AI 段還在排隊或執行中——新增的第三態，畫面顯示「AI 分析中」
  徽章，與「統計模式（AI 未分析）」區分。
- `AiAnalyzed=true`：AI 段已完成並覆寫定案內容。

**深析報告時機**：不需要 AI 的日子（低風險或 AI 全域關閉）統計段當下就直接產出報告；需要
AI 的日子要等 AI 段完成才產出（暫代紀錄的 `ReportFile` 為 `null`），深析報告的內容因此只會
在 `CompleteAiAsync` 完成後出現，不會有「報告先出但沒有 AI 內容」的中間態。

**取消與補跑語意**：執行中途取消時，`AiFollowupQueue` 裡尚未處理的工作記為
`AiAbandoned`（`NetiqPipelineResult` 的統計數字之一），已經寫入的統計紀錄維持
`AiPending=true`，成為下次執行前的「孤兒」。下次執行時，`NetiqPipelineService` 除了掃描
「缺漏日」，也會獨立掃描 lookback 窗口內既有的 `AiPending=true` 紀錄（與主機當天是否缺漏
無關），包成補跑型工作（`LogAnalysisService.RetryAiAsync`）排進同一個佇列的尾端，優先序
低於當日主線。補跑由既有紀錄（`TopIssues`/`TrendAlerts`/`CorrelationAlerts` 皆已持久化）
重建主分析輸入，但前置掃描與深入分析報告刻意不補——兩者需要原始 log，取消當下已經回不去了。

**適用範圍**：兩階段脫鉤只在 NetIQ pipeline 生效。本機分析路徑（`AnalyzeDayAsync`）評估後
決定暫不比照拆分——單機序列執行、多個主機日之間本來就沒有並行搜尋主線可保護，脫鉤的收益
低、佇列歸屬權（誰在程式結束前把佇列排空）的侵入性風險偏高，詳細評估見
[docs/archive/FEEDBACK-12-PLAN.md](docs/archive/FEEDBACK-12-PLAN.md) §3.9。

---

## 正式環境穩定性設計

| 機制 | 說明 |
|---|---|
| **Polly 網路重試** | 連線失敗、HTTP 錯誤、逾時、**空回應**皆自動重試（預設 3 次、指數退避），涵蓋模型剛重啟或瞬間過載等暫時性失敗；每次重試皆記錄於執行輸出 |
| **停用連線池** | `SocketsHttpHandler.PooledConnectionLifetime = TimeSpan.Zero`，每次呼叫都用全新連線。連線池已停用，因為「連線池裡的連線其實已被對方關閉，用戶端還不知道就拿去重用」會導致「The response ended prematurely.」——這類錯誤幾乎都發生在前一次呼叫剛結束後幾十毫秒內，不是生成到一半斷線，是典型的連線重用問題（與 HTTP 版本協商無關）；每次呼叫間隔數秒到數十秒、單次又動輒數十秒，重用連線省下的握手成本相對生成時間微乎其微，直接停用連線池換取穩定性更划算 |
| **抑制退化重複輸出** | `FrequencyPenalty`/`PresencePenalty`（預設 0.8）+ `ExtraRequestFields` 的原生 `repeat_penalty`（1.3）送給模型，抑制生成過程中卡進重複迴圈的退化輸出（實際觀察到摘要欄位塞滿 `-1-1-1-1...`、`process 45312 process 45312...` 這類重複垃圾）。從 0.3 一路調到 0.8 仍未完全根除，屬於持續觀察中的調校項目，不是保證解 |
| **依用途分開 token 上限** | 終端 JSON 較短的呼叫（每日總覽、前置掃描）用 `Ai.MaxTokens`（預設 2048，故意抓緊），篇幅天生較長的深入分析用 `Ai.DeepDiveMaxTokens`（預設 8192）。單一全域上限會逼你在「精簡呼叫退化時拖很久才觸頂」和「深入分析被截斷」之間二選一，拆開後兩邊都能設到剛好 |
| **context 預算共用防線** | `PromptBudget`（`Analysis/PromptBudget.cs`）依實測環境 Gemma 4 26B、context 20480 保守估算（CJK 約 1:1、其餘約 3.5 字元 1 token，留 10% 餘裕）。檢查點放在 `AIService.ChatAsync`——所有 AI 呼叫的單一咽喉點，同時知道 prompt 與該次輸出上限，任何一次呼叫送出前若估計會超出可用預算就記 WARN。小模型爆 context 時 server 端行為不可靠（可能靜默截頭、可能報錯），這道防線負責在各呼叫類型自己的截斷（深入分析 16KB 字元硬上限、週體檢 40 行輸入塑形、主分析結構性上限）萬一失效時把問題顯性化，而不是等 server 端悄悄吞掉一段輸入 |
| **回應信封也做容錯 + 記錄原始內容** | `ChatAsync` 先把 HTTP 回應讀成字串再自行解析，不直接 `ReadFromJsonAsync`——曾觀察到 HTTP 狀態碼是成功、但回應本體不是 JSON（`'H' is an invalid start of a value`），可能是中間 proxy/gateway 用 200 回傳純文字/HTML 錯誤頁。解析失敗時記錄回應預覽（此前完全是黑盒，看不到內容）並拋出 `AiEnvelopeParseException` 交給 Polly 重試——原本這類失敗完全沒有走 Polly 重試、直接判定整次呼叫失敗，白白浪費一次 `JsonRetryCount` 名額 |
| **AI JSON 容錯解析** | `AiJson`（`Models/AiAnalysisResult.cs`）用括號配對掃描（正確跳過字串內容中的括號）取出真正的 JSON 物件，比天真的「第一個 `{` 到最後一個 `}`」精準——前言文字混有大括號、或模型多回了一個陣列包裹都能正確抓出。若輸出被 `max_tokens` 攔腰截斷，另外用堆疊追蹤 `{}`/`[]` 的巢狀順序，依正確的後進先出順序補上缺少的收尾符號（只算深度不記順序的話，物件裡包陣列會補錯括號種類，产生語法仍不合法的「修復」）後再解析一次。全部候選都失敗時印出回覆預覽方便診斷，而非直接吞掉黑盒子 |
| **AI JSON 格式/內容重試** | `response_format=json_object` 只保證輸出是「合法 JSON」，不保證是預期的物件形狀——模型可能回傳陣列包多個物件、或欄位塞入異常冗長的重複文字，兩者語法都合法但不符期望。`ChatJsonAsync<T>` 解析後再檢查內容合理性（必填欄位非空、長度未超出正常摘要範圍），檢查未過就重新請求（預設 2 次），失敗原因與嘗試次數皆印出 |
| **System prompt 明確禁止前言** | 兩個系統提示都要求「直接以 `{` 開始輸出，不要有任何前言、推理過程或說明文字」，減少 MoE 模型在正式輸出前先寫一段推理文字、把 `max_tokens` 額度耗在 JSON 本體之外的情況 |
| **失敗降級** | 網路層與 JSON 層重試皆耗盡仍失敗時，當日降級為統計模式紀錄（`AiAnalyzed=false`），規則與趨勢告警照常運作，不會整天沒有紀錄；若有拿到內容只是格式不合格，會保留原文（截斷）供人工參考，不遺失資訊 |
| **結構化錯誤協定** | AI 呼叫回傳 `AiResponse { Success, Content, Error }` / `AiJsonResult<T> { Success, Value, RawContent, Error, Attempts }`，錯誤與正常內容分離，不靠字串前綴判斷 |
| **單一執行個體** | 行程內以 `SchedulerRunState` 做單一執行 gate（排程與立即執行共用，重疊時後者直接拒絕、不排隊），另保留具名 Mutex（`Global\LogForesight`）防未來任何第二行程誤配置指向同一 `DataRoot` |
| **執行結果可見** | 每次執行的成功/失敗與訊息寫入 `BatchRun` 紀錄（「排程作業」頁可查完整歷史），排程狀態卡另外顯示「上次執行」的即時結果，不需要翻 log 檔才知道有沒有跑成功 |
| **無主控台相容** | 排程背景執行時 `Console.OutputEncoding` 設定失敗自動忽略，不會擋下程式 |
| **時鐘回撥容錯** | Event Log 倒序掃描多掃 1 小時緩衝才停止，時間同步回撥造成的事件亂序不會漏抓 |
| **歷史紀錄併發保護** | webdata 的整份 JSON 內容存於 `lf_blobs`，`UpdatedAt` 為樂觀鎖權杖；排程與立即執行／多個管理者操作併發寫入時，帶著過期內容的一方會被資料庫拒絕並自動重試，不會靜默蓋掉對方剛寫入的內容 |
| **診斷檔案 Log（NLog）** | 執行輸出不夠判斷問題細節時（重試原因、AI 回覆內容、完整例外堆疊），到 `logs\web.log` 查——詳見下方獨立章節 |

