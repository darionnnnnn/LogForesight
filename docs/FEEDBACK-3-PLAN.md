# 回饋第三輪規劃（FEEDBACK-3-PLAN）

2026-07-30 起草。來源：使用者實測回饋 8 項（批次/NetIQ 2 項＋Web 6 項）。
本文件只規劃、不實作；每項含現況根因、方案設計、影響範圍、測試計畫。
未決事項集中在文末，需拍板後才動工。

| # | 項目 | 層面 | 性質 | 規模 |
|---|------|------|------|------|
| 1 | NetIQ 只處理前一天（回補窗口可設定） | 批次 | 行為調整＋新設定 | 中 |
| 2 | 多台 Sentinel 平行處理 | 批次 | 效能＋新設定 | 中 |
| 3 | 儀表板「風險類型」卡排版擠壓 | Web 前端 | CSS bug | 小 |
| 4 | 主機詳情看不到該主機的問題 | Web 前後端 | 功能缺口 | 中 |
| 5 | 風險日詳情「重點問題」表格擠壓 | Web 前端 | 排版重構 | 中 |
| 6 | 詢問 AI 可放大成全螢幕 modal | Web 前端 | 功能加值 | 小 |
| 7 | AI 輸出含 `<|channel>thought` 與簡體字 | Core | 輸出清洗＋繁化 | 中 |
| 8 | 「層級與顯示」設定與報表中風險不一致 | Web 前後端 | 語意擴充（需拍板） | 中大 |

---

## 1. NetIQ 正式環境只處理前一天資料

### 現況與根因
`NetiqPipelineService.RunServerAsync()`（LogForesight/Service/NetiqPipelineService.cs:138-143）：

- 首次執行（該主機 record store 全空）：回補 `NetiqInitialLookbackDays = 14` 天。
- 已有紀錄：檢查 `trendWindowDays = 14` 天內全部缺漏日並逐日回補。

2000 台規模下最壞情況＝14 × N 台主機日的 Sentinel 查詢。排程一旦漏跑幾天、或大量新主機
剛登錄，隔天的批次會對 Sentinel 發出大量歷史日查詢，正是「癱瘓或執行不完」的來源。

### 方案
`NetiqOptions` 新增欄位 **`BackfillDays`**（int，預設 `1`，合法範圍 1～14）：

- 取代兩處寫死的 lookback：`lookback = store.HasAnyRecord() ? min(BackfillDays, trendWindowDays) : BackfillDays`
  ——實際上首次與非首次統一用 `BackfillDays`，`NetiqInitialLookbackDays` 常數退役。
- 預設 1＝只看「昨天有沒有紀錄」，沒有就查昨天這一天；正式環境的預期行為。
- 「系統管理 > NetIQ 維護」節流參數區新增輸入欄（Netiq.cshtml / netiq.js /
  NetiqOptionsService 驗證範圍），與 QueryDelayMs 等既有欄位並列。
- 舊 blob 無此欄位 → System.Text.Json 反序列化用預設值 1，零遷移。

### 明講的取捨（要寫進設定欄位的說明文字）
- **缺漏日自癒能力下降**：BackfillDays=1 時，排程漏跑的中間日子永遠不會補。
  主機時間軸的灰格（無分析紀錄）與執行監控頁仍會誠實顯示缺口；需要補歷史時，
  管理者可暫時把 BackfillDays 調大、跑一次、再調回來——這是顯式操作，不是靜默行為。
- **趨勢基準逐日累積**：新主機第一天沒有歷史基準，TrendAnalyzer 對無歷史的情況
  已有既定行為（本機模式首次執行同款），不需要改。

### 影響範圍
- `LogForesight.Core/Models/NetiqOptions.cs`（新欄位＋註解）
- `LogForesight/Service/NetiqPipelineService.cs`（lookback 計算、常數退役、類別註解更新）
- `LogForesight.Web`：NetIQ 維護頁 UI＋`NetiqOptionsService` 驗證
- `docs/NETIQ-API-PLAN.md` §4 註記行為變更

### 測試
- `SentinelPipelineContractTests`：BackfillDays=1 只補昨天；=3 補三天內缺漏；
  首次執行不再深度回補 14 天（既有案例改寫）。
- `NetiqOptions` 舊 blob 缺欄位 → 預設 1 生效（EfWebdataStoreTests 或 options 相關測試）。
- Web 端驗證範圍測試（NetiqOptionsService / SentinelAdminServiceTests 慣例）。

---

## 2. 多台 Sentinel 平行處理

### 現況與根因
`NetiqPipelineService.RunAsync()`（NetiqPipelineService.cs:77-121）逐台 Sentinel 依序處理。
每台 Sentinel 各自獨立（自己的 SentinelClient、轄下主機不重疊），跨台其實沒有順序依賴——
單台之內「不同日期依序」的限制（趨勢需要前面日期的歷史）只跟同一台主機有關，
而一台主機只屬於一台 Sentinel，跨台平行不破壞這個前提。

### 方案
- `RunAsync` 的 per-server 迴圈改 `Parallel.ForEachAsync`，
  `MaxDegreeOfParallelism = NetiqOptions.MaxParallelServers`（新欄位，預設 2，範圍 1～8）。
  設 1 即完全等同現行為（保守逃生門）。
- 每台 Sentinel 的處理本體（RunServerAsync 內容）不變：自己的 client、日期依序、批次化。
- 失敗隔離不變：既有 per-server try/catch 移進 parallel body。

### 併發安全盤點（本項的主要工作量）
| 共用物件 | 現況 | 處置 |
|---|---|---|
| `NetiqPipelineResult` 計數與 Warnings | 純 POCO，非安全 | 加 lock（或改 Interlocked＋ConcurrentBag） |
| `BatchRunRecorder.RecordDayAnalyzed/RecordAiCall` | `_run.DaysAnalyzed++` 非原子（BatchRunRecorder.cs:58-64） | 改 Interlocked（欄位化）或方法內 lock |
| `IHostStore.TouchNetiq` | `JsonBlobCollection.Mutate` → `EfJsonBlobStore.Mutate` 已整段互斥 | 免改（已驗證 EfJsonBlobStore.cs:46 有 lock） |
| 各主機 record store | 每操作各開新 DbContext（EfAnalysisRecordStore.cs:63） | 免改 |
| `AIService` | 內部 `SemaphoreSlim(1,1)` 序列化全部請求 | 免改；平行收益主要在 Sentinel 查詢 I/O 與映射，AI 段仍排隊（地端模型本來就只能一個一個來） |
| `RiskReportService` 報告檔輸出 | 每主機日各寫各的檔 | 實作時確認無共用可變狀態（目前掃過無 static 可變欄位） |
| Console 輸出 | 平行後會交錯 | 逐行都帶 `[SentinelName]` 前綴（RunBatchDayAsync 已帶，AnalyzeHostDayAsync 的 `[ip]` 行補上 server 名） |

### 影響範圍
- `NetiqOptions.cs`（新欄位）、`NetiqPipelineService.cs`（平行化＋計數安全）、
  `BatchRunRecorder.cs`（原子計數）、NetIQ 維護頁 UI＋驗證。

### 測試
- `SentinelPipelineContractTests`：兩台 fake Sentinel 平行跑完，計數（HostDaysAnalyzed／
  HostsSkippedUpToDate／HostsFailed）與逐台依序跑一致；一台整台失敗不影響另一台。
- `BatchRunRecorder` 並發計數不掉數（新測試）。

---

## 3. 儀表板「風險類型」卡排版擠壓

### 現況與根因
`site.css:633`：`.lf-category-grid { grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); }`

`auto-fill` 在卡片數少於可容納軌道數時**保留空軌道**，實卡被鎖在 11rem 最小寬——
全寬容器（Dashboard.cshtml 的 col-12 卡）只有 2 個類別時，卡片仍然只有 11rem 寬，
「個問題．5 台主機」與嚴重度徽章列被迫換行擠壓（回饋截圖的狀況）。
當初避免 `auto-fit` 的理由是「卡少時不要被拉爆成半版寬」（css 註解），但 1fr 上限
配 auto-fill 的組合讓卡片永遠停在最小寬，是反效果。

### 方案
- 改為 `repeat(auto-fit, minmax(13rem, 20rem))`：auto-fit 收合空軌道讓實卡取得寬度，
  max 20rem 上限保住「不被拉爆成半版寬」的原意；css 註解同步改寫。
- `dashboard.js:248 severityBreakdown()`：徽章容器加 `d-flex flex-wrap gap-1`
  （取代 me-1 margin），換行時徽章間距不塌陷。
- 驗證：1、2、4、8 張卡＋窄螢幕（<576px）皆不擠壓、不爆寬。

### 影響範圍
`site.css`、`dashboard.js`。純顯示層，無後端、無測試專案影響（手動驗證）。

---

## 4. 主機詳情看不到該主機的問題

### 現況與根因
問題查詢「依主機」視角點列 → `/hosts/{hostId}` 主機詳情（records.js:515），
但 `GetHostDetail()`（RecordQueryService.cs:417-481）只回：基本資料＋風險時間軸色格＋
最近體檢結論。**沒有任何問題清單**——使用者要再逐格點日期才看得到問題，
「依主機」動線點進來等於斷頭。

### 方案
`HostDetailDto` 新增 `TopSignatures`（期間內問題彙總清單）：

- 資料來源：GetHostDetail 既有的 `records`（已含別名展開＋repository 的嚴重度可見性過濾），
  把期間內全部 `TopIssues` 依 `Source + EventId` 分組——分組邏輯與
  `ClusterSignatures()`（RecordQueryService.cs:483-）同款，抽私有共用或平行實作皆可，
  實作時以「不複製第二份分組規則」為準。
- 每列欄位：來源/EventId、分類、最高嚴重度、總次數、出現天數、最近出現日、說明（knownIssue）。
- 每列連結 → `/records/{hostId}/{最近出現日}`（該日詳情有完整處理動線），
  沿用 §8.4 下鑽規則。
- 排序：最高嚴重度 → 總次數 desc。
- 前端：HostDetail.cshtml 新增「重點問題（期間彙總）」卡，host-detail.js 用
  `renderTable` 渲染；空狀態「期間內未偵測到問題」。天數切換（currentDays）連動重載。

### 影響範圍
- `RecordQueryService.GetHostDetail`＋`HostDetailDto`（Models/Dto）
- `HostDetail.cshtml`、`host-detail.js`
- `docs/WEB-SPEC.md` §9.4 補述

### 測試
- 新增 GetHostDetail 彙總測試（比照 RecordQueryServiceSearchTests 慣例）：
  分組計數正確、含別名展開的歷史、SiteHidden 模式下被隱藏嚴重度不出現。

---

## 5. 風險日詳情「重點問題」表格擠壓

### 現況與根因
`issueColumns()`（record-detail.js:276-302）在 col-lg-8 容器裡塞 8 欄
（來源/Event、選取、次數、嚴重度、時段、趨勢、說明、處理狀態）。
「說明」欄的 `keyDetails`（帳號/IP 彙總，4703 這類事件動輒數百字）把其他欄壓到
最小寬，「趨勢」欄（max-width 180px＋pre-line）被擠成逐字直排——回饋截圖的災難畫面。

### 方案（依回饋建議：前段欄位合併，趨勢與處理狀態獨立保留）
新欄位配置（含展開箭頭仍在第一欄，guidancePanel 機制不動）：

| 欄 | 內容 |
|---|---|
| 問題（合併欄，佔滿剩餘寬度） | 第一行：`來源 (EventId)`＋log 名＋已抑制徽章；meta 列（小字）：嚴重度徽章・次數・時段；說明（knownIssue）；keyDetails（紅字，**預設 line-clamp 3 行＋「顯示全部」展開**）；相異訊息數＋原始訊息連結 |
| 選取 | 照舊（canHandle 才有，checkbox 邏輯零改動） |
| 趨勢 | 照舊內容，補 `min-width: 9rem` 防逐字直排 |
| 處理狀態 | 照舊（statusControl），補 `min-width` |

- `sourceCell`／`knownIssueCell`／次數／嚴重度／時段合併成新的 `issueCell()`；
  嚴重度徽章沿用 `severityCell` 的 `severityCountBadge` 標準（S11 單一標準不破壞）。
- keyDetails 展開：優先 CSS line-clamp＋toggle（比 modal 少一步）；「原始訊息」maintain
  既有 `showDetailModal` 不動。
- 列印樣式（lf-no-print／report 列印）驗證合併欄在列印下可讀。
- 嚴重度篩選鈕、批次選取、chat-panel 的 `updateIssueOptions` 皆吃資料層，不受欄位重排影響。

### 影響範圍
`record-detail.js`（issueColumns 與相關 cell 函式）、`site.css`（合併欄/趨勢欄樣式）。
純前端；手動驗證含：窄視窗、列印預覽、展開處置參考列、批次勾選。

---

## 6. 詢問 AI 放大成全螢幕 modal

### 現況
chat 卡固定 `max-height: 340px` 內部捲動（site.css:799），長回覆閱讀吃力。
「處理歷程」卡已有「放大檢視」按鈕前例（RecordDetail.cshtml:86 `handling-log-expand`）。

### 方案
- chat-card header 加「放大檢視」鈕（比照 handling-log-expand 的視覺與位置）。
- 點擊 → 開 **fullscreen modal**（`modal-fullscreen`；或 `modal-xl` ＋ 90vh，實作時擇一），
  用**節點搬移**（appendChild 移動既有 `#chat-issue-select`／`#chat-messages`／`#chat-form`
  整組容器進 modal body，關閉時搬回卡片原位）——不是複製：
  - 事件監聽器與對話狀態隨節點保留，chat-panel.js 的邏輯零改動；
  - 不會產生重複 id。
- modal 內 `lf-chat-messages` 改 flex 撐滿高度（覆寫 max-height）；關閉後恢復 340px。
- 先讀 handling-log-expand 的實作（handling-panel.js），能共用同一套「搬移進 modal」
  工具函式就抽到 ui.js 共用，不寫第二份。

### 影響範圍
`RecordDetail.cshtml`、`chat-panel.js`（或 ui.js 共用工具）、`site.css`。純前端。

---

## 7. AI 輸出清洗（channel 標記）＋ OpenCC 簡轉繁

### 現況與根因
`AIService.ChatAsync`（Core/Analysis/AIService.cs:210-223）把 `message.content` 原樣回傳，
無任何輸出清洗。目前地端模型是會輸出 channel 標記的推理型模型，實測漏出：

1. `<|channel>thought` 整段思考過程直接顯示給使用者；
2. 思考與回覆夾雜簡體中文——PromptGuidelines 的提示詞約束擋不住模型內在行為。

### 方案：Core 單點清洗＋轉換（批次與 Web 全部受惠）
新增 `LogForesight.Core/Analysis/AiOutputSanitizer.cs`，在 `ChatAsync` 取得 content 後、
回傳前套用（與 EmptyAiResponse 檢查同層，是唯一咽喉點）：

**步驟一：channel 標記解析**（regex 同時容忍 `<|channel|>`／`<|channel>`、有無結尾標記的變體）
1. 內容含 final 段標記（如 `<|channel|>final<|message|>`）→ 只取 final 段之後的文字。
2. 無 final 但含 thought/analysis 段標記 → 剝除該段（標記起點到下一個標記或字尾）。
3. 剝除所有殘留的 `<|...|>`／`<|...>` token。
4. 清洗後為空白 → 丟 `EmptyAiResponseException`：讓 Polly 依既有規則重試，
   重試耗盡走既有降級（誠實失敗，不把半截思考當成回覆）。

**步驟二：OpenCC 簡→繁（台灣用詞）**
- NuGet 套件 `OpenCC`（doggy8088 純 C# 實作、零相依、MIT）；
  用法 `var convert = OpenCC.Converter("cn", "tw2"); convert(text);`。
- **實作前驗證**（本項的先決檢查）：套件支援 net8.0；`"tw2"` 確為
  「簡體→台灣正體＋台灣慣用詞」設定（相當於 OpenCC 標準的 s2twp）；
  converter 建構成本高則做成 static Lazy 單例、確認其執行緒安全（與 #2 平行化相容）。
- 套用在清洗後的最終字串：JSON 模式輸出的鍵名是 ASCII 不受影響，只有值內中文被轉換；
  快取（AiCacheStore）存的即是轉換後內容，key 不變、舊快取相容。

**附帶（文件註記，不寫程式）**：`Ai.ExtraRequestFields` 本來就是為「限制思考長度」而留的
透傳欄位——部署端同步設定，可減少 thought 吃光 max_tokens 導致 final 段被截斷、
清洗後變空觸發重試的機率。

### 影響範圍
- 新檔 `AiOutputSanitizer.cs`＋`AIService.ChatAsync` 一行接入
- `LogForesight.Core.csproj` 新增 OpenCC 套件參考
- 不動 PromptGuidelines（提示詞約束仍保留，雙保險）

### 測試
`AiOutputSanitizerTests`（新檔）：
- 使用者回報的實際樣本：`<|channel>thought` 開頭、整段皆思考無 final → 視為空回應；
- thought＋final 混合 → 只留 final；無標記 → 原樣；
- 簡體樣本（内存/网络/数据/该/导致 等 LocalizationLintTests 字集）→ 轉出台灣繁體；
- JSON 模式輸出：鍵名不變、值被轉換、仍可反序列化。

---

## 8. 「層級與顯示」與報表中風險不一致

### 現況判讀（這項不是漏套過濾，是語意缺口）
- 「層級與顯示」設定**只作用於問題嚴重度**（單一問題上的高/中/低），
  過濾咽喉在 `RecordRepository`（S1，`GetVisibleSeverities()`，
  RecordRepository.cs:84/99）——SiteHidden 模式下報表的類型分布卡等
  問題層統計**已經**吃到過濾。
- 回饋裡「報表還是出現中風險」的「中風險」是**日風險等級**（報表趨勢圖的中風險線、
  主機排行的中風險日欄、KPI）——設定頁（Settings.cshtml:18-22）與儀表板 hint 都明講
  日風險等級「不受此設定影響」，`ReportService.BuildTrend/BuildKpi` 直接數 `RiskLevel`。
- 也就是說：現況是**刻意設計**，但從兩輪回饋看，使用者要的是「顯示面也能把
  中/低風險日藏起來」。證據層不可改寫的原則不變，這裡動的只是顯示過濾。

### 方案（建議，需拍板）：新增獨立的「日風險等級顯示」設定
- `SystemSettings` 新增 `VisibleDayRiskLevels`（List&lt;string&gt;，預設 高/中/低 全勾；
  驗證：**「高」強制勾選**——全部隱藏會讓儀表板永遠空白，也違背產品目的）。
- 設定頁「層級與顯示」卡新增區塊：三個勾選＋說明文字改寫
  （原「不受此設定影響」段落改為指向新設定；問題嚴重度與日風險等級兩套層級
  的區分說明保留，這是 HISTORY #11 特意建立的認知）。
- **套用點與 S1 同一咽喉**：`RecordRepository.Query/QueryPage` 增加日風險等級過濾——
  未勾選等級的風險日**整筆**從查詢結果消失，自動涵蓋：儀表板 KPI/主機排行/群組概況、
  報表 KPI/趨勢/排行、問題查詢三視角。
- **兩個顯式豁免**（誠實原則）：
  - `GetOne`（風險日詳情）不過濾——直接連結仍可看；
  - `GetHostDetail` 時間軸不過濾——被藏的日子若顯示成灰格「無分析紀錄」是說謊；
    實作上 `RecordQueryFilter` 加 opt-out 旗標（如 `IgnoreDayRiskVisibility`），
    僅這兩處使用，並加註解說明為什麼豁免。
- 報表/儀表板既有的「風險日由批次算定」hint 文案同步更新。

### 替代案（若不想增加設定）
只把設定頁與報表的說明文字寫得更醒目（「此設定不影響日風險統計」）。
成本低但沒有解決使用者的實際需求，不建議；列出供拍板。

### 影響範圍
- `SystemSettings.cs`／`SystemSettingsService.cs`（欄位＋驗證＋DTO）
- `RecordRepository.cs`（第二個可見性過濾＋opt-out 旗標）
- `RecordQueryService.GetOne/GetHostDetail`（豁免旗標）
- `Settings.cshtml`／`settings.js`（UI）＋ dashboard.js/reports.js hint 文案
- `docs/WEB-SPEC.md` §7/§9 補述

### 測試
- `RecordRepositorySeverityVisibilityTests` 擴充：日風險過濾契約（Query 過濾、
  GetOne/時間軸豁免）；
- `SystemSettingsServiceTests`：驗證規則（高強制勾選、舊 blob 缺欄位＝全顯示）。

---

## 實作順序建議

1. **#7 AI 輸出清洗**（獨立、影響最大——所有 AI 輸出品質）
2. **#3 儀表板 CSS**＋**#6 chat modal**（小而獨立，先清掉）
3. **#5 重點問題表格重構**（純前端，與 #6 同頁，接著做）
4. **#4 主機詳情問題彙總**（前後端，中等）
5. **#1 BackfillDays**＋**#2 平行處理**（同檔案、同設定頁，綁一起做；#1 先——
   窗口縮小後平行化的壓力測試面也小）
6. **#8 日風險等級顯示**（等拍板；牽動面最廣，最後做）

每項完成後跑全量測試（現況基準：master 5c1a56d，1073 綠）；全部完成後做慣例的
全案體檢（對照本文件逐項核對實作與規劃的落差）。

---

## 未決事項（動工前請拍板）

1. **#1** `BackfillDays` 預設 1（正式環境語意優先）？還是預設 14（保留現行為，正式環境手動調 1）？
   ——規劃採**預設 1**。
2. **#2** `MaxParallelServers` 預設 2、上限 8，可接受？
3. **#8** 採「新增日風險等級顯示設定」方案？時間軸與詳情頁豁免的處理方式如上？
4. **#5** keyDetails 收合採 line-clamp＋展開（規劃案）還是點擊開 modal？
5. **#7** OpenCC 套件若驗證後不支援 net8 或轉換品質不佳，備案為只做 channel 標記清洗、
   簡繁轉換另尋方案（如 OpenCCSharp）再議。
