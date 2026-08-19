# 回饋第二十一輪規劃（外部審查 P1~P4 ＋ 使用者回饋 Q1~Q11）

## 0. 背景與範圍

輸入：外部審查四項（P1 補跑旗標、P2 雲端 provider 申報、P3 首見日浮水印、P4 兩小項）＋使用者回饋十一項（Q1~Q11）。全部在本輪處理。

**已定案決策**（討論結論，含根因）

| # | 決策 | 根因一句話 |
|---|---|---|
| D1 | 「需補跑」判定收斂成一個共用純函式，三處（`MissingDateFinder.Find` / NetIQ 孤兒補跑 / `RunPreview`）共用；`useAi=false` 時 requireAi 強制降級並在 UI 明講 | 判定式散落三處各自演化；「低風險＋未跑 AI」是合法終局，只有一處記得 |
| D2 | NetIQ 手動回補上限由 14 開放到 **30**（設定 DTO、UI、`RunPreview` 同步）；趨勢基線窗口 `TrendWindowDays=14` **不動**；UI 文案改為「回望窗口」語意 | 回補窗口與趨勢窗口是兩個概念，卻用同一個常數夾住 |
| D3 | 雲端 provider（OpenAi／AzureOpenAi）：提示明講「原始 log 內容會傳送至第三方服務」；從 Local 切走時儲存前 `confirmAction` 二次確認；說明書＋README 升級用詞；OpenAI 位址欄恢復顯示（選填 proxy）；`AiProviders.Normalize` fallback 記 WARN | 介面把合規決策呈現得像換端點；說明書承諾了 UI 藏起來的欄位 |
| D4 | 首見日合併改增量：INSERT 限 `record_id > 浮水印`，UPDATE 段只在「初次回補」執行（blob 旗標）；BACKLOG 補「完整重算」開關條目 | 浮水印比對的是 MAX(record_id)，任何一次分析都會讓它失效 |
| D5 | 儀表板「風險類型」主數字改為 **問題類型數**（相異 Source+EventId，跨主機跨日皆去重），DTO 新增欄位；原 `RiskItemCount` 從卡片移除；排版改三行「N 個問題／M 台主機／期間累計 K 筆（主機×日）」 | 現行數字是「主機×問題」組合數，兩邊都不是使用者要的口徑 |
| D6 | 詳情頁嚴重度按鈕計數改為「套用顯示範圍後」的數；點選後若被範圍吃光，顯示提示並提供一鍵放寬範圍 | 兩道 AND 篩選，按鈕計數只算了一道 |
| D7 | 問題檔案：候選清單與存檔支援 EventId 0（Linux）；識別改用 `SourceEventLabel`；前後端驗證同步放行 | `!eventId` falsy 把 Linux 恆為 0 的 EventId 擋掉，訊息還說「請選擇」 |
| D8 | 問題負責人改為**長期負責＋自動交辦**：`AttachNewDay` 在「無既有標記、無進行中案件、無 AutoApply 結論」時，若 profile 有負責人，自動建立案件並指派（系統名義 `ActorId=null`）；多負責人暫定取清單第一位 | `OwnerUserIds` 目前只給隱含權限，使用者預期它會產生交辦 |
| D9 | 問題查詢「依問題」列表：主機數欄加表頭與「N 台／M 主機日」雙數字；展開列加「共 M 筆，顯示前 100」截斷提示；明細視角日期欄改純文字；類型 badge 改固定類別順序 | 數字對但無標籤無對照；連結中的連結；每列順序不同 |
| D10 | 權限異動待辦新增 **NetIQ 來源**：pipeline 從 Security 事件（特權群組成員異動、ACL／稽核政策異動）產生 `PermissionChangeRecord`，`HostName` 為該 NetIQ 主機；本機監控維持 | 現行只監控跑批次那台本機，NetIQ 主機永遠空 |

**明確不做**：`lf_top_issues` 加 `source_key` 持久化欄位（維持 BACKLOG，回填風險不成比例）；P4a `GetDayHandlingRaw` 案件維度只補註解（BACKLOG 已記待實測）。

## 1. 事實核對摘要

| 項 | 判定 | 關鍵證據 |
|---|---|---|
| P1 | ✅ | `HostDayPostProcessor.cs:11-34`、`NetiqPipelineService.cs:316`、`ScheduleController.cs:128-153`；統計模式 `AiAnalyzed` 恆 false（`LogAnalysisService.cs:270/346`）；fixture 用不可能組合 |
| Q1 | ⚠️ | UI `max=14`、DTO `[Range(1,14)]`、`Math.Min(backfillDays, TrendWindowDays)`；「只補 10 天」是 `HasRecord` 判不缺 |
| P2 | ✅ | `settings.js:165-196` 一句提示；`12-settings.md` 說可填位址但 UI `d-none`；prompt 送 `Message` 欄（`RiskyEventLookupService.cs:32`） |
| P3 | ✅ | `SchemaUpgrader.cs:140-229`；BACKLOG:247-252 已有 source_key 條目 |
| P4 | ✅ | `EfIssueAggregateQuery.cs:566-570`；`AiProviders.cs:21-29` 無 log |
| Q2/3 | ⚠️ | `RiskItemCount`=相異(主機,Source,EventId)（`IIssueAggregateQuery.cs:294`）；`dashboard.js:373-393` 兩個 div |
| Q4 | ✅ | `record-detail.js:63-77/1035-1043`；`RecordDetailQueryService.cs:544` isDefaultUnhandled |
| Q6 | ✅ | `issue-owners.js:278-297` falsy；`IssueOwnerAdminService.cs:75` `<=0` 擋；Linux EventId 恆 0 |
| Q7 | ✅ 依設計 | `IssueCaseCoordinator.cs:199-270` 不用 OwnerUserIds；`UserCapabilityResolver.cs:57` 只給權限 |
| Q8 | ⚠️ | 截圖 4 台／14 主機日一致；展開硬上限 100（`records.js:759`）無提示 |
| Q9 | ✅ | `records.js:475/486` 同 URL |
| Q10 | ✅ 依設計 | `AnalysisOrchestrator.cs:373-398` `HostName=currentHost` |
| Q11 | ✅ | `CategoryAggregator` 依嚴重度→問題數排序 |

## 2. 作業總覽

本輪委派模型：claude-sonnet-4-6（Gemini 週限剩 17% < 20%，依 skill 規則改用 Claude 池，週限 32%）｜使用者未指派。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | 補跑判定收斂＋回補上限 30（D1、D2） | — | agy |
| B | 雲端 provider 申報＋Normalize WARN（D3） | — | agy |
| C | 首見日增量合併（D4） | — | agy |
| D | 儀表板風險類型口徑與排版（D5） | — | agy |
| E | 詳情頁嚴重度按鈕（D6） | — | agy |
| F | 問題檔案 Linux 指派＋自動交辦（D7、D8） | — | agy |
| G | 問題查詢頁三項（D9） | — | agy |
| H | NetIQ 權限異動來源（D10） | — | agy |
| I | 小修：P4a 註解、Q7/Q10 說明文字、文件同步 | A~H | Claude |

## 3. 作業明細

### 作業 A-階段 1：共用「需補跑」判定＋三處接線
- **背景**：「只補跑失敗或未執行」在三處判定不一致；低風險日不跑 AI 是合法終局；統計模式下 `AiAnalyzed` 恆 false。
- **契約**：
  - 新增純函式（Core 層、可單測）：輸入一筆 `AnalysisRecord`（可為 null＝缺日）與 `useAi`，輸出是否需補跑。規則：缺日→需要；`AiPending`→需要；`useAi && !AiAnalyzed && RiskLevel != Low`→需要；其餘不需要。`useAi=false` 時只有缺日與 `AiPending` 成立。
  - `MissingDateFinder.Find(requireAi:true)`、NetIQ 孤兒補跑、`ScheduleController.RunPreview` 三處改用該函式；`RunPreview` 保留「缺日算需要」語意。
  - `RunPreview` 回應新增欄位（暫定 `aiDisabled: bool`），前端在 AI 未設定時於旗標旁顯示「AI 未設定，此選項僅補跑缺漏日」。
- **範圍**：Core/Service、Web/Controllers/Api/ScheduleController、runs.js；不動 docs。
- **驗收**：`dotnet test` 全綠；新增測試：`需補跑判定_低風險未跑AI不算需補跑`、`需補跑判定_AI未設定時只有缺日與AiPending成立`、`RunPreview_低風險AiAnalyzedFalse不計入預覽台數`；既有 fixture 中「低＋AiAnalyzed=true」改為真實狀態。
- **回報格式**：改檔清單／測試數字／偏離契約說明。

### 作業 A-階段 2：NetIQ 回補上限 30
- **契約**：`BackfillDays` DTO 驗證、Runs 頁與 NetIQ 維護頁輸入框、`RunPreview` 夾值、`ResolveLookbackDays` 全部上限改 30（常數單點，暫定名 `MaxBackfillDays`）；`TrendWindowDays=14` 不變、趨勢計算不受影響；UI 文案改為「回望天數：檢查最近 N 天內缺漏或需補跑的日子（上限 30）」。
- **範圍**：Core/Service/NetiqPipelineService、Web DTO/Views/js；不動 AnalysisOrchestrator 本機路徑。
- **驗收**：測試 `ResolveLookbackDays_30天不被14夾住`、DTO 驗證 31 被拒；grep `max="14"` 在 Runs/Netiq 頁不再出現。

### 作業 B-階段 1：provider 申報與二次確認
- **契約**：settings.js provider 提示 OpenAi/AzureOpenAi 加一句「分析時最多 500 則原始 log 訊息（可能含帳號、IP）會傳送至第三方服務」；儲存時若 provider 由 Local 變為雲端，走既有 `confirmAction`（danger）確認；OpenAi 位址欄改為顯示且選填（placeholder 說明 proxy 用）。`AiProviders.Normalize` 無法辨識時記一次 WARN（含原值）。
- **範圍**：settings.js、AiProviders.cs（可注入 static logger 依專案慣例）；HelpContent/12-settings.md 與 README 由 Claude 於作業 I 改。
- **驗收**：測試 `Normalize_未知值退回Local且記警告`；手動 grep settings.js 含「第三方」。

### 作業 C-階段 1：首見日增量合併
- **契約**：INSERT 段加 `record_id > 浮水印` 條件（浮水印不存在視為 0）；UPDATE 段只在 blob 旗標（暫定 key `issue_first_seen_full_done`）不存在時執行，執行後寫旗標；閘門仍先比 MAX(record_id)；SQLite/SqlServer 兩 provider 皆可跑。
- **範圍**：SchemaUpgrader.cs、SchemaUpgraderTests；不動 IssueFirstSeenSeedHostedService。
- **驗收**：測試 `首見日合併_第二次只處理新record_id`、`首見日合併_初次後不再跑UPDATE段`；既有測試全綠。

### 作業 D-階段 1：風險類型卡片
- **契約**：`CategoryAggregate`／`DashboardCategoryDto` 新增 `IssueTypeCount`（相異 (Source 大小寫不敏感, EventId)），移除卡片對 `RiskItemCount` 的顯示（DTO 欄位可留）；前端排版三行如 D5，「個問題」與數字同行；tooltip 改為「同一問題不論幾台主機、幾天只算一項」；下鑽連結不變。
- **範圍**：Core/Persistence（聚合查詢與介面）、DashboardDtos、dashboard.js。
- **驗收**：測試 `風險類型_IssueTypeCount跨主機跨日去重`；`dotnet test` 全綠。

### 作業 E-階段 1：嚴重度按鈕計數與提示
- **契約**：按鈕文字計數改為「套用目前顯示範圍後」的筆數，為 0 的等級按鈕仍顯示但標示 0；點選某等級後若可見列數為 0 而原始筆數 > 0，於列表位置顯示「有 N 項低嚴重度問題屬預設不處理，已被顯示範圍隱藏」＋按鈕「顯示所有問題」切換範圍。切換範圍時按鈕計數即時重算。
- **範圍**：record-detail.js。
- **驗收**：無自動化前端測試；回報需附手動確認步驟結果（低等級點選後提示出現、切換後列出）；`dotnet build` 零警告。

### 作業 F-階段 1：Linux 問題可指派
- **契約**：候選清單顯示用 `SourceEventLabel` 語意（EventId 0 時顯示 EventKey 或不顯示括號）；前端驗證改判 source 非空且 eventId 為合法數字（含 0）；後端 `EventId < 0` 才拒絕；規則索引鍵與既有 `IssueSignatureKey` 一致，Linux 問題能被 `AttachNewDay` 命中。
- **範圍**：issue-owners.js、IssueOwnerAdminService、相關測試。
- **驗收**：測試 `建立規則_EventId為0可儲存`；手動：選「包含(0)」可儲存。

### 作業 F-階段 2：負責人自動交辦
- **契約**：`AttachNewDay` 優先序：已有標記→進行中案件→AutoApply 結論→**負責人自動交辦**（新增）。自動交辦＝建立 `IssueCase`（Status=open、HandlerId=負責人清單第一位【暫定】、`ActorId=null`、Note 暫定「系統依問題檔案自動派送」）並寫該日標記（新增 `HandlingActions` 值，暫定 `OwnerAutoAssign`）；冪等：同主機同問題已有進行中案件不重建。案件出現在該負責人「我的交辦」與 workload 徽章。負責人被移除時既有案件不動。
- **範圍**：IssueCaseCoordinator、HandlingActions、HostDayPostProcessor 接線、測試；不動 Web 指派路徑。
- **驗收**：測試 `AttachNewDay_有負責人無案件時自動建案並指派`、`AttachNewDay_已有進行中案件不重複建案`、`AttachNewDay_AutoApply結論優先於自動交辦`；`GetHandlerWorkload` 能查到該案件。

### 作業 G-階段 1：問題查詢頁三項
- **契約**：依問題列表主機數欄表頭固定顯示，格值「N 台 / M 主機日」（M 用既有主機日數）；展開列若總筆數 > 100 顯示「共 M 筆，僅顯示前 100 筆」於「在明細視角檢視…」連結旁；明細視角日期欄改純文字（整列連結保留）；`categoryBadges` 依 `CATEGORY_NAMES` 固定順序輸出，命中篩選者仍主色。
- **範圍**：records.js、format.js（若需要）。
- **驗收**：`dotnet build` 零警告；回報附手動確認。

### 作業 H-階段 1：NetIQ 權限異動來源
- **背景**：現行 `PermissionChangeRecord` 只由本機監控產生；NetIQ 主機需由 Security 事件推導。
- **契約**：pipeline 處理每個 NetIQ 主機日時，從該日事件中挑出 EventId 屬集合【暫定：4728/4732/4756（成員新增）、4729/4733/4757（成員移除）、4670（ACL 變更）、4717/4718/4907（稽核政策）】的事件，每則寫一筆 `PermissionChangeRecord`：`HostName`=該主機、`DetectedAt`=事件時間、`ChangeType` 對應中文類型、`Target`／`Before`／`After` 從訊息擷取（擷取不到就放原訊息摘要）、`AlertText`=訊息前 500 字。冪等：以 (HostName, EventTime, EventId, 訊息雜湊) 去重，重跑不重複。事件集合放常數單點並註明來源。走既有 `perm_changes` store，Web 頁不改即可看到；頁面加來源欄（本機監控／NetIQ 事件）。
- **範圍**：Core/Service（NetIQ pipeline 後處理）、Models（若需加來源欄位）、permission-changes.js／DTO；不動 PermissionMonitorService。
- **驗收**：測試 `NetIQ主機日_含4756事件時寫入權限異動紀錄`、`NetIQ權限異動_重跑同日不重複`；`dotnet test` 全綠。

### 作業 I（Claude）
- P4a `hostDaysNeedingDetail` 補上界註解；問題檔案頁與權限異動頁說明文字（負責人＝長期負責自動交辦；權限異動來源兩種）；HelpContent 12-settings、README `LF_CRYPTO_KEY` 用詞升級為「使用雲端 provider 時必須設定」；BACKLOG 補「首見日完整重算開關」；WEB-SPEC／DB-SPEC／DETECTION-SPEC 對應段落同步；CLAUDE.md 測試基線數字。

## 4. 測試計畫
見各階段驗收；總數需高於 2168 且全綠。

## 5. 文件更新
作業 I 統一處理：WEB-SPEC（儀表板卡片口徑、詳情頁篩選、問題檔案自動交辦、權限異動來源、回補上限 30）、DB-SPEC（首見日增量旗標 blob key、案件自動建立動作值）、DETECTION-SPEC（NetIQ 權限異動事件集合）、HelpContent、README、BACKLOG。

## 6. 風險與回滾
- A：判定收斂後補跑筆數會大幅下降——這是預期；若使用者要重跑全部，用不勾旗標的模式。
- A-2：回補 30 天時，前 16 天的趨勢基線資料可能不足，趨勢標記可能偏「新出現」，接受。
- F-2：自動建案會產生大量案件（每主機×問題一件），負責人可能被淹沒；先上線觀察，必要時加「自動交辦上限」設定（有消費端才加）。
- H：訊息欄位擷取依 Sentinel 訊息格式，擷取失敗只影響 Target/Before/After 可讀性。
- 每作業獨立 commit，可單獨 revert。

## 7. 執行紀錄
| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
