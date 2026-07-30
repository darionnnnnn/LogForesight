# 回饋第三輪規劃（FEEDBACK-3-PLAN）

2026-07-30 起草；同日第二輪細化，**全部未決事項已拍板**，本文件為可直接動工的定案版。
來源：使用者實測回饋 8 項（批次/NetIQ 2 項＋Web 6 項）。只規劃、不實作。

## 已定案事項（原文件末段的未決點）

1. #1 `BackfillDays` **預設 1**、範圍 1～14。
2. #2 `MaxParallelServers` **預設 2**、範圍 1～8。
3. #8 **採「日風險等級顯示」新設定**；過濾下推到 SQL 條件層（見 #8 修訂），
   風險日詳情直連與主機時間軸豁免。
4. #5 keyDetails 收合採 **line-clamp＋「顯示全部」展開**。
5. #7 採 NuGet `OpenCC`；實作首步驗證 net8 相容與轉換品質，**已通過並修正 locale**：
   `"cn"→"tw2"` 只做字元級簡繁轉換（网络→網絡，非台灣慣用詞），實測
   `"cn"→"twp"` 才是片語級台灣慣用詞替換（网络→網路、默认→預設、数据→資料、
   用户→使用者），且 20000 次併發呼叫零不一致（純函式、無可變狀態，單例安全）。
   **本文件其餘各處的 `tw2` 一律改讀為 `twp`**。

| # | 項目 | 層面 | 規模 |
|---|------|------|------|
| 1 | NetIQ 回補窗口設定化（預設只補前一天） | 批次 | 中 |
| 2 | 多台 Sentinel 平行處理 | 批次 | 中 |
| 3 | 儀表板「風險類型」卡排版擠壓 | Web 前端 | 小 |
| 4 | 主機詳情期間問題彙總 | Web 前後端 | 中 |
| 5 | 風險日詳情「重點問題」欄位合併 | Web 前端 | 中 |
| 6 | 詢問 AI 放大 modal | Web 前端 | 小 |
| 7 | AI 輸出 channel 標記清洗＋OpenCC 繁化 | Core | 中 |
| 8 | 日風險等級顯示設定 | Web 前後端 | 中大 |

---

## 1. NetIQ 回補窗口設定化（BackfillDays）

### 現況與根因
`NetiqPipelineService.RunServerAsync()`（NetiqPipelineService.cs:138-143）：
首次執行回補 `NetiqInitialLookbackDays = 14` 天、已有紀錄檢查 `trendWindowDays = 14` 天缺漏。
2000 台規模最壞 14 × N 主機日查詢，正式環境會癱瘓 Sentinel 或執行不完。

### 改動明細
1. **`LogForesight.Core/Models/NetiqOptions.cs`**：新增
   `public int BackfillDays { get; set; } = 1;`——XML 註解寫明兩個取捨：
   缺漏日不自癒（需要補歷史時暫時調大、跑一次、調回）；新主機趨勢基準逐日累積
   （TrendAnalyzer 對無歷史情況已有既定行為，同本機模式首次執行）。
   舊 blob 缺欄位 → 反序列化用預設 1，零遷移。
2. **`NetiqPipelineService.cs`**：
   - `RunAsync` 簽章不動（`trendWindowDays` 參數保留給分析用的 `historyDays`）；
   - lookback 計算改為 `var lookback = Math.Min(_netiqOptions.BackfillDays, trendWindowDays);`
     ——首次與非首次統一，`HasAnyRecord()` 判斷與 `NetiqInitialLookbackDays` 常數退役；
   - 類別註解更新（「當日續跑免費取得」段落仍成立，缺漏窗口的敘述改寫）。
3. **Web 維護頁**：
   - `NetiqDtos.cs` `UpdateNetiqOptionsRequest` 加 `[Range(1, 14)] public int BackfillDays { get; set; }`；
   - `NetiqOptionsService.Update` 抄寫欄位（稽核 Before/After 自動涵蓋）；
   - `Netiq.cshtml` 節流參數區新增輸入欄 `#opt-backfill-days`（與 `#opt-query-delay` 並列，
     form-text 寫明取捨）；`netiq.js` `loadOptions()`/submit 各加一行。
4. **`docs/NETIQ-API-PLAN.md`** §4 註記行為變更（14 天首次回補的描述作廢）。

### 測試
- `SentinelPipelineContractTests`：BackfillDays=1 只補昨天；=3 補三天內缺漏日；
  首次執行（store 全空）不再深度回補 14 天（既有首次回補案例改寫）。
- NetiqOptions 舊 blob 缺欄位 → 預設 1（比照既有 options 反序列化測試慣例）。
- Web 端 `[Range]` 驗證由框架處理，補 service 層測試比照 SentinelAdminServiceTests 慣例。

---

## 2. 多台 Sentinel 平行處理（MaxParallelServers）

### 前提確認（已驗證）
- 跨台 Sentinel 主機不重疊、各自有獨立 `SentinelClient`；「不同日期依序」的限制只在
  單一主機內（趨勢依賴），主機只屬一台 Sentinel → 跨台平行不破壞前提。
- `IHostStore.TouchNetiq` → `EfJsonBlobStore.Mutate` 已整段互斥（EfJsonBlobStore.cs:46）✅
- record store 每操作各開新 DbContext（EfAnalysisRecordStore.cs:63）✅
- `AIService` 內部 `SemaphoreSlim(1,1)` 序列化 ✅（平行收益在 Sentinel 查詢 I/O 與映射）
- `RiskReportService` 每主機日各寫各的報告檔；實作時再掃一次確認無 static 可變狀態。

### 改動明細
1. **`NetiqOptions.cs`**：`public int MaxParallelServers { get; set; } = 2;`
   （註解：1＝完全等同舊行為的逃生門；上限 8）。
2. **`NetiqPipelineService.RunAsync`**：per-server `foreach` 改
   `Parallel.ForEachAsync(hostList.ByServer, new ParallelOptions { MaxDegreeOfParallelism = ..., CancellationToken = ct }, ...)`；
   既有 per-server try/catch（失敗隔離）整段移入 body。
3. **`NetiqPipelineResult`**：計數三欄與 `Warnings` 的更新集中成內部方法
   （`AddAnalyzed()`/`AddFailed(n)`/`AddSkipped()`/`AddWarning(s)`），方法內 lock 私有物件——
   呼叫端不再直接 `++`，避免散落的非原子更新。
4. **`BatchRunRecorder`**：`RecordDayAnalyzed`/`RecordAiCall` 方法體加 lock
   （`_run.DaysAnalyzed` 等是屬性，不能用 Interlocked；lock 成本相對 AI 呼叫可忽略）。
   `OnLogRecorded` 的 Warn/Error 計數同鎖。`BatchRunStore.AppendLog` 已有自己的 lock ✅
5. **Console 輸出**：平行後會交錯——`AnalyzeHostDayAsync` 的
   `[{ip}] {date} 風險【…】` 行改為 `[{sentinelName}] [{ip}] …`（RunBatchDayAsync 的行已帶）。

### 測試
- `SentinelPipelineContractTests`：兩台 fake Sentinel、MaxParallelServers=2 跑完，
  三個計數與依序跑（=1）完全一致；一台整台丟例外不影響另一台的完成數。
- `BatchRunRecorder` 並發呼叫 RecordDayAnalyzed×N 不掉數（新測試，Parallel.For 打進去）。

---

## 3. 儀表板「風險類型」卡排版

### 根因（已確認）
`site.css:633` `repeat(auto-fill, minmax(11rem, 1fr))`：`auto-fill` 在卡片數少時**保留空軌道**，
實卡被鎖在 11rem 最小寬（全寬容器也一樣），徽章列與「個問題．N 台主機」被迫換行擠壓。
當初避免 auto-fit 是怕「卡少時拉爆成半版寬」，但 auto-fill＋1fr 的組合是反效果。

### 改動明細
1. `site.css`：改 `repeat(auto-fit, minmax(13rem, 20rem))`——auto-fit 收合空軌道讓實卡
   取得寬度，max 20rem 保住原意；css 註解改寫成上述根因說明。
2. `dashboard.js:248 severityBreakdown()`：容器 span 加 class `d-flex flex-wrap gap-1`，
   徽章移除 `me-1`（gap 取代 margin，換行時間距不塌）。
3. 手動驗證：1／2／4／8 張卡、<576px 窄幅、SiteHidden 模式（徽章數變少）皆不擠壓不爆寬。

---

## 4. 主機詳情期間問題彙總

### 現況
`GetHostDetail()`（RecordQueryService.cs:417-481）只回基本資料＋時間軸＋最近體檢；
問題查詢「依主機」點進來（records.js:515 → `/hosts/{id}`）看不到任何問題，動線斷頭。

### 改動明細
1. **`RecordQueryService.GetHostDetail`**：既有 `records`（已含別名展開＋repository 嚴重度
   過濾）再做一次彙總——依 `Source + EventId` 分組，與 `ClusterSignatures()`
   （RecordQueryService.cs:483-507）同款分組邏輯；差異：單主機不需要 HostCount>1 門檻、
   不取 Top5。抽私有 helper 或平行實作皆可，準則是**分組鍵規則不出現第二份定義**
   （建議把「SelectMany TopIssues → GroupBy Source+EventId」抽成共用私有方法，兩處呼叫）。
2. **`HostDetailDto`** 新增 `List<HostIssueSummaryDto> TopSignatures`，每列：
   `Source`、`EventId`、`Category`、`MaxSeverity`（期間內最高，字串沿用 IssueSeverity 名）、
   `TotalCount`、`DaysSeen`（出現天數）、`LastSeenDate`（yyyy-MM-dd）、`KnownIssue`（取最近一日的）。
   排序：MaxSeverity（重→輕）→ TotalCount desc。
3. **`HostDetail.cshtml`**：時間軸卡下方新增「重點問題（期間彙總）」卡（container id
   `host-issues`）。
4. **`host-detail.js`**：`load()` 增 `renderIssues(detail)`，用 `renderTable` 渲染：
   欄位＝來源/Event（沿用 `source (eventId)` 格式）、分類、嚴重度徽章（`severityCountBadge`
   或 format.js 既有標準）、總次數、出現天數、最近出現（`rowHref` →
   `/records/${hostId}/${lastSeenDate}`，該日詳情有完整處理動線）、說明。
   空狀態：「期間內未偵測到問題」＋hint「時間軸灰格代表該日無分析紀錄，不是沒問題」。
   天數切換（currentDays）既有 `load()` 動線自動連動。
5. **`docs/WEB-SPEC.md`** §9.4 補述。

### 測試
新增 GetHostDetail 彙總測試（比照 RecordQueryServiceSearchTests 慣例）：
分組計數／DaysSeen／LastSeen 正確；含墓碑別名的歷史併入；SiteHidden 下被隱藏嚴重度
不出現在彙總（吃 repository 過濾的自然結果，測試固定住這個契約）。

---

## 5. 風險日詳情「重點問題」欄位合併

### 現況
`issueColumns()`（record-detail.js:276-302）8 欄塞在 col-lg-8；keyDetails 長字串（4703 類
事件數百字）壓縮其他欄，「趨勢」欄（max-width 180px＋pre-line）被擠成逐字直排。

### 新欄位配置（4 欄，展開箭頭仍在第一欄、guidancePanel 機制不動）

| 欄 | 內容 |
|---|---|
| 問題（佔滿剩餘寬） | 新 `issueCell(issue)`，由上而下：①標題行＝`source (eventId)`＋logName 小字＋「已抑制」徽章（原 sourceCell 內容）；②meta 行（小字、`d-flex flex-wrap gap-2`）＝嚴重度徽章・次數・時段 `firstSeen~lastSeen`；③說明（knownIssue）；④keyDetails 紅字，**CSS line-clamp 3 行**＋「顯示全部／收合」toggle（超過 3 行才出現按鈕，用 scrollHeight 判斷）；⑤`N 種相異訊息`＋既有「原始訊息 N 則」modal 連結 |
| 選取 | 照舊（canHandle 才有；selectCheckbox/selectAllCheckbox 零改動） |
| 趨勢 | 內容照舊；`.lf-trend-cell` 加 `min-width: 9rem` 防直排 |
| 處理狀態 | statusControl 照舊；加 `min-width` |

### 改動明細
1. `record-detail.js`：`issueColumns()` 改 4 欄；`sourceCell`＋`knownIssueCell`＋次數/嚴重度/
   時段合併為 `issueCell()`（severityCell 的徽章標準沿用，S11 單一標準不破壞）；
   keyDetails toggle 新增小函式。
2. `site.css`：`.lf-issue-cell`（區塊間距）、`.lf-issue-cell__meta`、
   `.lf-issue-details--clamped`（`-webkit-line-clamp: 3`）＋趨勢/處理狀態欄 min-width。
3. 不動的部分（驗證清單）：嚴重度篩選鈕、批次勾選與表頭全選、guidancePanel 展開列、
   chat-panel 的 `updateIssueOptions`、收合的「已處理／已有結論」區塊——全吃資料層，
   欄位重排不影響。
4. 手動驗證：窄視窗、**列印預覽**（合併欄可讀、toggle 屬 lf-no-print、keyDetails 列印時
   展開全文——加 `@media print` 解除 clamp）、展開處置參考、批次勾選流程。

---

## 6. 詢問 AI 放大 modal

### 做法（節點搬移，chat-panel.js 對話邏輯零改動）
處理歷程的「放大檢視」（handling-panel.js:405-418）是**資料重繪**進 modal——chat 有
live form／模組內狀態，不適用同款；改用**節點搬移**，需要 modal 關閉回呼：

1. **`ui.js` `showDetailModal`** 增加選項 `{ onClose, fullscreen }`：
   - `fullscreen: true` → dialog 加 `modal-fullscreen`；
   - `onClose` 在 `hidden.bs.modal` handler 內、`el.remove()` **之前**呼叫
     （搬入的節點必須先搬回原位再銷毀 modal 殼）。既有呼叫端不受影響。
2. **`RecordDetail.cshtml`**：chat-card header 加「放大檢視」鈕（比照 `handling-log-expand`
   的視覺；id `chat-expand`）；卡片 body 內容（select＋messages＋form）包一層
   `<div id="chat-body">` 方便整組搬移。
3. **`chat-panel.js`**：`bindEvents()` 綁 `#chat-expand`——點擊把 `#chat-body` appendChild
   進 modal body（監聽器與對話狀態隨節點保留、無重複 id），`onClose` 搬回卡片原位；
   開啟/關閉後各捲動 `#chat-messages` 到底。
4. **`site.css`**：modal 內 `#chat-body` 改 flex 直欄撐滿高度、`.lf-chat-messages`
   在 modal 情境覆寫 `max-height`（如 `.modal .lf-chat-messages { max-height: none; flex: 1; }`）；
   關閉搬回後自動恢復 340px。

驗證：放大中送出訊息／清除重來／切換問題；關閉後對話保留、卡片版恢復；
批次套用觸發 `initChatPanel` 重入時 modal 已關（重入前置條件不變）。

---

## 7. AI 輸出清洗（channel 標記）＋OpenCC 繁化

### 接入點（單一咽喉）
`AIService.ChatAsync`（Core/Analysis/AIService.cs:210-223）取得 content 後、回傳前——
批次五層分析、Web 互動卡、詳情頁對話全部受惠；快取（AiCacheStore）存清洗後內容，
key 不變、舊快取相容。

### 新檔 `LogForesight.Core/Analysis/AiOutputSanitizer.cs`
`public static string? Sanitize(string content)`（回 null＝清洗後無有效內容）：

**步驟一：channel 標記解析**（regex 容忍 `<|channel|>`／`<|channel>`、
`<|message|>` 有無、大小寫）：
1. 含 final 段標記（`<|channel|>final` 系列）→ 只取最後一個 final 標記（及其後的
   `<|message|>`）之後的文字。
2. 無 final 但含 thought／analysis 段標記 → 剝除各段（標記起點到下一個標記或字尾）。
3. 剝除殘留的 `<|...|>`／`<|...>` token 與多餘空白。
4. 結果空白 → 回 null。

**步驟二：OpenCC 簡→繁（台灣用詞）**：
- 套件 NuGet `OpenCC`（doggy8088，純 C#、零相依、MIT，已加入 LogForesight.Core.csproj）；
  `OpenCC.OpenCC.Converter("cn", "twp")` 建 converter——**locale 是 `twp` 不是 `tw2`**
  （spike 實測：`tw2` 只做字元級簡繁轉換如网络→網絡；`twp` 才是片語級台灣慣用詞替換
  网络→網路／默认→預設／数据→資料／用户→使用者）。
- converter 以 `static Lazy<>` 單例持有（建構含字典載入，不逐次建）；
  **執行緒安全已驗證**（spike：20000 次 Parallel.For 併發呼叫零不一致，純函式無可變狀態，
  不需額外加鎖，與 #2 平行化並存安全）。
- 轉換套用於步驟一的輸出；JSON 模式輸出鍵名為 ASCII 不受影響、僅值內中文被轉換，
  轉換不產生 `"`／`\` 等 JSON 結構字元（OpenCC 只映射中文字詞）。

**`ChatAsync` 接入**：在 EmptyAiResponse 檢查之後
`text = AiOutputSanitizer.Sanitize(text); if (text == null) throw new EmptyAiResponseException();`
——整段皆思考（final 被 max_tokens 截掉）視為空回應，交給 Polly 既有重試、
耗盡走既有降級。誠實失敗，不把半截思考當回覆。

**實作首步（閘門，已完成）**：net8 可載入 ✅；`"cn"→"twp"` 確為「簡體→台灣正體＋
台灣慣用詞」（相當 OpenCC 標準 s2twp）✅；併發安全 ✅。

**附帶文件註記**（不寫程式）：部署端以 `Ai.ExtraRequestFields` 限制思考長度，
降低 final 段被截、清洗後變空觸發重試的機率。

### 測試（新檔 `AiOutputSanitizerTests`）
- 使用者回報實例：`<|channel>thought` 開頭且無 final → null；
- thought＋final 混合 → 只留 final；多個標記變體（單雙豎線）；無標記 → 原樣；
- 簡體樣本（LocalizationLintTests 的高信心字集：内存/网络/数据/该/导致…）→ 台灣繁體；
- JSON 內容：清洗＋轉換後仍可反序列化、鍵名不變；
- 空白/純 token 輸入 → null。
- `AIService` 層：Sanitize 回 null 觸發重試的行為（fake handler 比照既有 AIService 測試慣例，
  若無既有 HTTP fake 基礎則此條移到 sanitizer 單元測試涵蓋、ChatAsync 接線靠 code review）。

---

## 8. 日風險等級顯示設定（VisibleDayRiskLevels）

### 語意（定案）
- 「層級與顯示」既有部分**只管問題嚴重度**，不變。
- 新增獨立區塊「日風險等級顯示」：高/中/低勾選、**「高」強制勾選**（全隱藏會讓
  儀表板永遠空白）。未勾選等級的風險日**整筆**從查詢/統計消失。
- 日風險等級仍由批次算定、證據層不可改寫——這裡只動顯示過濾。

### 過濾位置（修訂：下推到 SQL 條件，不做記憶體後過濾）
第一版規劃「Query 後在記憶體剔除」有**分頁陷阱**：`QueryPage` 的排序/分頁在 SQL 端完成，
事後剔除會讓總筆數與每頁筆數對不上。修訂為：

- **`RecordRepository`** 新增 `ApplyDayRiskVisibility(filter)`，與 `ApplyVisibility` 並列、
  Query/QueryPage 共同呼叫：`filter.RiskLevels = filter.RiskLevels == null ? visibleLevels
  : filter.RiskLevels.Intersect(visibleLevels)`——與可見範圍同樣的「只能縮小不能放大」
  語意；交集為空時沿用「空集合＝零結果」慣例（使用者顯式篩「中」而中被全站隱藏 → 空清單，
  誠實）。SQL 端分頁/總數自然正確。
- **豁免（兩處、顯式）**：
  - `GetOne`（風險日詳情直連）：本來就不走 filter 路徑，不受影響——維持可看，
    詳情頁既有的「風險等級由完整資料判定」hint 已足夠；
  - `GetHostDetail` 時間軸：被藏的日子顯示成灰格「無分析紀錄」是說謊。
    `IRecordRepository.Query` 增加可選參數 `bool applyDayRiskVisibility = true`，
    僅 `GetHostDetail` 傳 false（含 #4 的問題彙總——主機詳情整頁都看全量），
    參數上加註解說明為什麼豁免。**不在 Core 的 RecordQueryFilter 加旗標**（那是
    儲存層契約，顯示策略不該滲進去）。

### 改動明細
1. **`SystemSettings.cs`**：`List<string> VisibleDayRiskLevels`，預設 `["高","中","低"]`
   （舊 blob 缺欄位＝全顯示，行為不變直到有人改設定）。
2. **`SystemSettingsService`**：
   - DTO＋`Update` 驗證：值 ∈ RiskLevels.All、必含 `RiskLevels.High`、去重；
   - 新方法 `IReadOnlySet<string> GetVisibleDayRiskLevels()`（全勾時可回 null 表不過濾，
     與 GetVisibleSeverities 同慣例）；
   - 稽核 detail Before/After 補新欄位。
3. **`RecordRepository`**：`ApplyDayRiskVisibility` ＋ Query/QueryPage 接線＋豁免參數。
   類別頂部註解補「第二個全站咽喉」的說明。
4. **設定頁**：`Settings.cshtml` 層級與顯示卡新增「日風險等級顯示」區塊
   （高的 checkbox disabled＋checked；說明文字改寫——原「日風險等級不受此設定影響」
   段落改為指向新設定，兩套層級的區分說明保留）；`settings.js` 載入/送出。
5. **顯示層連動**（前端要知道哪些等級被藏，設定 API 是 Maintain-only 一般使用者拿不到）：
   - 新端點 `GET /api/settings/display`（任何已登入者；比照 HostsController 無 [Permission]
     的先例＋註解說明理由）回 `{ visibleDayRiskLevels }`；
   - `dashboard.js`：中風險日 KPI 卡在「中」被隱藏時整卡不顯示（高風險日卡同理不會發生，
     高強制顯示）；hint 文案改為「…由批次分析算定；顯示範圍受『日風險等級顯示』設定影響」；
   - `reports.js`：趨勢圖隱藏被藏等級的 series；KPI/排行表格欄位保留
     （資料母體已被過濾，數值一致，不另做欄位增減）；
   - `records.js`：風險等級篩選 chips 隱藏被藏等級（避免點了得到空結果的死路）。
6. **文案同步**：dashboard.js:119-127 兩處 hint、`docs/WEB-SPEC.md` §7.1/§9 補述。

### 測試
- `RecordRepositorySeverityVisibilityTests` 擴充：日風險過濾契約——Query 過濾生效、
  QueryPage 總數正確、顯式 RiskLevels 交集（含交集為空）、豁免參數不過濾、
  GetOne 不受影響、全勾回 null 不過濾；
- `SystemSettingsServiceTests`：驗證規則（缺「高」擋下、非法值剔除、舊 blob 預設全顯示）；
- `ReportServiceTests`／`HandlingServiceTests`：中風險日隱藏時 KPI/趨勢/待辦母體
  跟著縮小（吃 repository 的自然結果，測試固定契約）。

---

## 實作順序（定案）

1. **#7** AI 輸出清洗（首步先過 OpenCC 閘門）
2. **#3** 儀表板 CSS＋**#6** chat modal（小而獨立）
3. **#5** 重點問題欄位合併（同頁接著做）
4. **#4** 主機詳情問題彙總
5. **#1** BackfillDays → **#2** 平行處理（同檔案、同設定頁，#1 先——窗口縮小後
   平行化的壓測面也小）
6. **#8** 日風險等級顯示（牽動面最廣，最後做）

每項完成即跑全量測試（基準：master 5c1a56d，1073 綠）；全部完成後做慣例全案體檢
（對照本文件逐項核對實作與規劃落差，含：#8 分頁總數、#5 列印、#6 搬移回位、
#2 計數一致性四個重點複查）。
