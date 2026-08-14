# 回饋第十三輪規劃（2026-08-10）

> **狀態（2026-08-10）：全案（批次 A～G＋D4 文件收尾）已全部實作完成＋全案體檢，於
> `feature/feedback-13` 分支提交；1697 測試綠（基線 1647），0 警告 0 錯誤。
> 待使用者實測後併 master（依 docs 既有 git 分支慣例）。**
>
> 來源是使用者的 23 項自我審查清單（批 0～4＋補充）＋三項補充回報（問題查詢類型過濾、
> 日風險等級預設、儀表板風險卡低嚴重度）。實作前先以三個探索代理逐項驗證主張與 dev 實況的
> 吻合度：其中約 8 項為已完成的過時主張（剔除不動），其餘依影響面分批。程式碼註解中的
> 「回饋十三輪 X」即指本文件的批次代號。

## 批次總表

| 批次 | 主題 | 主要落點 |
|---|---|---|
| A（A1~A11） | 正確性小修與誠實申報 | RecordListQueryService／SystemSettings／RecordStatsBuilder／DayHandlingCommandService／layout.js／records.js／Settings 頁／AiFollowupQueue／LogAnalysisService／rules.js／IP 範例統一 |
| B | 記憶體與資料完整性 | RiskyEventSelector.SelectSourceEvents＋NetiqPipelineService 截斷二分重查 |
| C | 孤兒補跑產報告（含深析） | IRiskyEventStore.QueryDay＋LogAnalysisService.RetryAiAsync 報告重建 |
| D | 單台 Sentinel 查詢併發 | NetiqOptions.MaxParallelQueriesPerServer＋NetiqPipelineService client pool |
| E | 趨勢基準改中位數 | TrendAnalyzer＋全站「平均」文案改「基準」 |
| F | 抑制範圍 Host／Group／Site | RuleSuppression.Scope＋SuppressionFilter＋RuleAdminService＋Rules 頁 UI |
| G | 外觀：品牌區與登入頁 | _Layout.cshtml／Login.cshtml／site.css（截斷保護＋登入頁品牌區重構） |
| D4 | 文件收尾 | README（試點措辭／效能數字／上線 SOP／權限申報量化）＋RULES/WEB/DETECTION-SPEC＋BACKLOG |

## 各批次關鍵決策

### 批次 A：正確性小修與誠實申報

- **A1** 問題查詢「依問題」視角補類別後過濾（比照既有 RiskLevels 疊加先例）；依主機／依日期
  視角的類別 chips 是記錄層語意，不動。
- **A2** `VisibleDayRiskLevels` 預設 `{高,中}`（使用者要求）；只影響從未儲存過設定的部署。
- **A3** 儀表板／報表「風險類型」卡改在服務端組 DTO 前過濾未勾選嚴重度（DefaultHidden 模式
  也生效——這兩張卡沒有「手動展開」入口，純顯示語意等同不顯示）；settings.js 提示同步。
  **下鑽連結不改**：`TryApplyDayRiskVisibility` 的交集只縮不放語意已保證一致，改了反而多餘。
- **A4** 日層級指派補「被指派人無 Handle 能力」提示（不擋、只提示，照抄問題層級先例）；
  側欄「我的交辦」徽章加 Handle 判斷（無 Handle 的角色不載入數字）。
- **A5** CSV 匯出移除畫面上已不存在的「風險日數」欄。
- **A6** 設定頁「資料保留」分頁加回填進度唯讀顯示（讀既有 /api/health/detail，後端零改動）。
- **A7** AI 佇列滿時的背壓顯示：`TryEnqueue` 失敗 → 回報 `netiq-backpressure` phase →
  阻塞等待 → 恢復。體檢揪出 `TryWrite` 不檢查取消權杖，補 `ThrowIfCancellationRequested`。
- **A8** 新主機趨勢基準空窗走 `UncoveredChecks` 申報「趨勢基準建立中（第 n/13 天）」。
  體檢揪出 off-by-one：`ReadRecent` 窗口含當天但當天紀錄尚不存在，比較閾值是
  `historyDays - 1` 不是 `historyDays`。
- **A9** Linux 檢索面窄一階（規則 program ∪ sev 2-5）同構申報，比照「關聯層不適用」先例。
- **A10** builtin 規則列加「以此為範本建立自訂規則」；停用開關補語意說明；Linux 遮蔽警告
  訊息補具體排序建議（`"sudo".Contains("su")` 是正確偵測，只改訊息不動邏輯）。
- **A11** UI 網段範例統一 192.168.0.x；docs 真實內網 IP 遮罩（10.xx/10.yy/10.zz 保留
  「不同網段」的敘事意義，不偽造成範例網段）；git 歷史不回溯。

### 批次 B：記憶體與資料完整性

- **B1** `AiWorkItem.Logs` 入列前縮成 `RiskyEventSelector.SelectSourceEvents` 選中的事件
  （≤500 筆/主機日，與 `ReplaceRiskyEvents` 共用同一次計算，選取語意必然一致）。
  佇列容量維持 200：最壞 500×2000 字 ≈ 2MB/件、200 件 ≈ 400MB 可界住；1000 件最壞 2GB
  不可接受（帳記在 AiFollowupQueue 註解）。已知行為變更：報告原始 log 池從全量縮成 risky
  池，fallback 走既有「（無對應的原始 log）」降級。
- **B2** 查詢截斷二分重查：批次還能切就切半各自重查，收斂到單台仍截斷才標 `DataIncomplete`
  ——一台吵的主機不再拖垮整批的趨勢基準。成本只在真的截斷時付（log₂ 深度）。

### 批次 C：孤兒補跑產報告

- `IRiskyEventStore.QueryDay(hostId, date)` 新介面；`RetryAiAsync` 補跑成功後從風險事件
  暫存重建 `EventLogEntryData` 產報告（含深析，使用者定案）。暫存超保留期／無合格事件時
  維持 `ReportFile=null` 並申報從缺。

### 批次 D：單台 Sentinel 查詢併發

- 新設定 `MaxParallelQueriesPerServer`（預設 1＝逐位相容既有行為，硬上限 4）：同一天內的
  IP 批次以 client pool 平行查詢（`SentinelClient` 單實例單併發，平行就得各建實例、各自
  SAML token）。**日期序仍嚴格序列**（趨勢比對需要前一天已寫入），同日批次主機不重疊
  （Chunk 保證），與跨 Sentinel 的 `MaxParallelServers` 正交。
- `AnalysisMaxPoolSize` 改為兩個上限常數的乘積＋1（編譯期常數），連線池配額不成為新瓶頸。
- 測試：51 台主機逼出兩批次，斷言併發峰值＝設定值、預設 1 時峰值恆 1、跨日嚴格序列。

### 批次 E：趨勢基準改中位數

- `TrendAnalyzer` 三處計算（簽章基準／整體錯誤量／稽核量）平均值→中位數；單日爆量不再
  墊高基準蓋掉後續真異常（有專屬證明測試：13 天 x2＋1 天 x100，中位數 2.0 正確觸發
  Rising、平均值 9.0 會漏）。屬性名 `HistoryDailyAverage` 不改（ContentJson 序列化相容）。
- 中性文案：「近 N 日平均」→「近 N 日基準」（告警文字／AI prompt／報告／詳情頁），
  舊紀錄的值不說謊、14 天後自然換血。`SlowTrendAnalyzer` 是另一套長窗口，不動（BACKLOG）。

### 批次 F：抑制範圍 Host／Group／Site

- `RuleSuppression` 加 `Scope`（預設 Host，舊資料零遷移）＋`HostGroupId`；
  `SuppressionFilter` 維持純函數，群組成員集合由四個呼叫端（LogAnalysisService／
  NetiqPipelineService／AnalysisOrchestrator／WeeklyCheckupService）各自解析注入——
  AnalysisOrchestrator 的到期抑制通知因此移到 `hostStore.Touch` 之後（群組成員資格要等
  主機登記完成才拿得到）。
- upsert 鍵擴為 (RuleId, Scope, Host|HostGroupId)；Host 與 Site 範圍可並存不互相覆寫。
- `RemoveSuppression` 路由從 `{host}` path segment 改 query string（Group/Site 沒有 host
  可放路徑）。UI：範圍下拉三選一切換目標欄位；列表「主機」欄改「範圍」欄。
- 詳情頁「已知雜訊→建立抑制」既有呼叫點不帶 scope，DTO 預設 Host，語意不變（已驗證）。

### 批次 G：外觀（品牌區與登入頁）

- 依全域規則先詢問並套用 ui-ux-pro-max；專案已有 docs/DESIGN-SYSTEM.md v2，本批是既有
  版面的局部修正，只檢索 truncation／版面準則，不重生設計方向。
- **preview 實測定位出兩個規劃之外的實質 bug**：
  1. 側欄副標題 `<small>` 沒有自己的截斷規則，超長時字形被硬裁一半（無省略號）；
  2. 登入頁品牌名稱／副標完全無截斷保護會撐爆卡片，且未設自訂圖示時整個品牌區只剩純文字
     （側欄有內建圖示 fallback，登入頁沒有——`.lf-login__brand` CSS 是死碼，HTML 從未用過）。
- 修法：側欄品牌區改 `brand-text`（flex-column＋min-width:0）名稱／副標各自 ellipsis＋
  title tooltip；登入頁啟用品牌區橫排（方形漸層底圖示＋名稱副標疊放，與側欄同一套視覺
  語言），fallback 內建圖示補齊。實測中再揪出 `<span>` 未 blockify 導致 ellipsis 無效的
  第二層 bug（flex-column 容器順帶解決）。長品牌名／RWD 斷點／預設外觀皆驗證。

## 裁決紀錄

- **已完成剔除（過時主張，不動）**：7-D3（409 已攔）、7-G4（handling_log 已入清理）、7-S4、
  7-D6、18-W1、18-D5、18-G3、22（ActiveUserMiddleware 已實作）。
- **本輪不做（已記入 BACKLOG「回饋第十三輪遞延項」）**：#11 通知管道（下輪候選首位）、
  #15 Windows SMART（NetIQ 取不到）、#16 簽章白話快取、SlowTrendAnalyzer 中位數化、
  7-S2 blob header 誤判陷阱、批 4 專案外觀。

## 全案體檢紀錄

- 實作期揪出並修正：A7 取消語意破洞（`TryWrite` 不檢查 ct）、A8 off-by-one、C 的
  非確定性測試（刪除重寫為 4 個直接單元測試）、`AiOutcome` 參數列內非法 XML doc。
- 終檢揪出並修正：A4 側欄徽章的 Handle 判斷漏做（layout.js 補上）；RULES-SPEC 五處
  批次代號誤標 D（實為 F）。
- 終檢確認無虞：批次 D client pool 的取消／例外路徑無 client 洩漏（body finally 歸還＋
  外層 finally 全數 dispose；工廠建構子驗證為確定性，無「建到一半」情境）；record-detail
  的抑制建立呼叫點與新 DTO 相容；`SuppressionFilter` 全部生產呼叫端皆傳真實群組集合。
