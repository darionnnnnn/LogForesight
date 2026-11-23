# FEEDBACK 第 80 輪規劃：前五輪回顧修正與說明書 AI 版

> 狀態：規劃中（已做四視角複檢；待使用者確認「待確認事項」後開工）
> 基準：`feature/backlog-help-content-fixes`@a231599e（dev@d9d8ac15 加兩個提交：BACKLOG 記錄與情境驅動器時鐘修正；1.0.79.2；測試基線 Windows 11,998 通過／12 略過、macOS 11,983 通過／27 略過）
> 來源：使用者要求先把第 75～79 輪的修改對照程式碼與規劃文件詳細檢查一遍，有錯誤或問題就併入本輪；本輪原定項目是 `docs/BACKLOG.md`「下一輪預定」的說明書 AI 版 5 處。
> 分支：`feature/feedback-80`（自 dev@d9d8ac15 開出，帶入規劃期間的四個提交）。

## 怎麼讀這份文件

- **項目編號**：每個改動項目有編號（A1、B1…），每個編號在該批次的「驗收」表裡各有一列。沒有驗收列的項目不算在本輪範圍。
- **核對狀態**：**〔已核〕**＝主代理親自讀碼或實跑探針確認；**〔待核〕**＝只有 subagent 回報，或程式事實已核但觸發情境還沒重現。〔待核〕的項目，該批次開工第一步由主代理確認；不成立就移除，並在執行紀錄記一行。
- **實測事實的寫法**：凡寫「已實測」的，一律附實測環境與實測資料的性質；條件與正式使用不同的標「未實測」。
- **驗收**：全部是主代理在開發環境能執行並判定的。可用手段只有這些：
  - macOS 的 `dotnet build`、`dotnet test`（定向與全量）；Windows 11 VM 的全量測試。
  - 以 `WebApplicationFactory` 起站台發請求；可替換時鐘（`FakeTimeProvider`）。
  - UI 資料集＋截圖量測工具（`LogForesight.Tests/tools/ui-shots.mjs`，五個寬度、六項量測與全站斷言）。
  - Node 前端行為測試（`*.behavior.mjs`，經 `NodeRuntime.RunOrFail`）。
  - `git grep`、獨立小程式探針、推送後讀遠端分支指向。
- 需要正式站台才能確認的另列文末，不算驗收；每條都對應一個已通過的模擬驗收。

## 回顧方式與核對程度

- 六個唯讀 subagent（Sonnet 5.5）各負責一段：第 75、76（第二階段）、77、78 輪各一，第 79 輪依批次拆成兩半。每個都讀該輪規劃文件的定案、驗收對照、體檢輪修正，再以**目前 HEAD 的檔案內容**核對，不重報已修正與 BACKLOG 已記載的項目。全程未建置、未跑測試。
- 回報共 22 條發現，沒有高嚴重度；中 2 條、低到中 2 條，其餘低。主代理逐條讀碼並做兩個探針（批次 C、H）。22 條中 18 條的程式事實〔已核〕，4 條〔待核〕。
- 複核時推翻或修正了 subagent 的兩個說法，已反映在批次內：
  - 「帳本回寫入口是 `NetiqSourceAttempt.Mark`」不成立：`Mark` 在正式碼沒有呼叫端（`git grep` 只命中測試）。實際入口是 `RecordBatch` → `PatchParentAttempt`（批次 B）。
  - 「結構同步的錯誤一律改成固定說明」會讓管理者失去可處置的原因：`PrtgClientException` 的訊息依其契約不含機敏資訊，且全站已有 `PrtgFailureText` 這份對照（批次 D）。
- 各輪核對後**沒有問題**的重點（subagent 讀碼結論，主代理未逐項重驗）：
  - 第 75 輪：報表分桶邊界、帳齡、再發生率分母為零、可見範圍、含授權維度的快取鍵、授權矩陣對新端點的登錄、快照封存。
  - 第 76 輪：「不再打擾」三處定義一致、取消收回案件授與、派工資格只有一份判定、說明章節權限無繞過路徑；E-TABLE 抽 16 列、G-TABLE 抽 11 列皆與現況相符。
  - 第 77 輪：父指紋在記憶體完整紀錄與資料庫讀回紀錄兩邊同值、快取與 `SingleFlight`、硬上限、依主機合併、空資料待複查欄。
  - 第 78 輪：批次 0～5 沒有找到「等價不成立」的缺陷，也沒有被第 79 輪回退。
  - 第 79 輪：Cookie 撤銷與 24 小時絕對上限、背景標頭不可偽造延長、CSRF 涵蓋、路由快照、對外連線出口、`ExecuteUpdate`／原生 SQL 寫入點的截斷、NLog 常駐 target 的並行。
- **沒看完的範圍**（本輪不補查，除非批次 X 的探針指到）：第 75 輪批次 A／B／C 的 CSS、校準匯出 v4；第 79 輪 J1 相容算法的逐行驗算、J3／J5／J7／J9～J13 的實作、`ChatFocusResolver`；SQL Server 實機行為。

## 待確認事項（附建議；未答覆者以建議為「暫定」開工，開工前可推翻）

| # | 事項 | 建議 | 理由 |
|---|---|---|---|
| Q1 | BACKLOG「下一輪預定」的 G4～G12、批次改派入口是否納入本輪 | **不納入，順延** | 本輪是回顧修正，項目多而零散；G4／G5／G9 需要產品取捨與畫面設計，混在一起會讓驗收失焦 |
| Q2 | 是否有站台部署過 1.0.77.0～1.0.77.2（77.2 與 77.3 同一天併入 dev） | 沒有就**不做** H3 | 只有停過那三個版本的站台，升級後才會把部分主機日的 PRTG 腿重設一次 |
| Q3 | 報表兩個沒有前端消費端的欄位（`MaxSeverityPerCell`、`HandlingScopeApplied`）要移除還是用上 | **移除** | 熱度表定案是單色淡階，不呈現嚴重度；`HandlingScopeApplied` 恆為 false，畫面另有判斷 |
| Q4 | Data Protection 金鑰環明文存放：改文件，還是 Windows 加 DPAPI | **只改文件**（備份章節註明排除 `dataprotection\`） | DPAPI 綁機器與帳號，換服務帳號或搬機就全員登出外加舊游標失效；現況的權限限縮已是主要防線 |
| Q5 | Core 寫進執行紀錄／維護主控台的 44 行 `ex.Message` 是否算「技術原文外洩」 | **不納入**，只修存進狀態 DTO 的那兩處（D1） | 第 76 輪定案範圍是 Web；主控台是診斷輸出。「serverAdmin 在執行詳情看得到輸出內容」已在 BACKLOG |
| Q6 | PRTG 評估預覽的翻頁上限 30,000：放寬，還是到上限時停用「下一頁」並提示縮小篩選 | **到上限時停用並提示** | 上限是用來擋深分頁成本；放寬只是把邊界往後推 |
| Q7 | 超長帳號標籤「截斷後保留」是否收緊成「值內出現下一個標籤樣貌就不保留」 | **收緊**（暫定，開工時以真實樣本驗） | 第 79 輪 F6 的本意是保住超長的真帳號，不是把缺邊界的整段訊息當帳號 |
| Q8 | 作業進度輪詢（PRTG 回填、探測、結構同步、NetIQ 探測與匯入）是否標成背景請求 | **標**（F3） | 同一種回填進度在排程頁已經標了，兩頁不一致；閒置時限預設 8 小時，盯著進度 8 小時不操作而被登出的情境幾乎不存在，作業本身不受登出影響 |

> **開工時的決定（2026-11-23）**：使用者未回覆 Q1～Q8，依本表建議為暫定開工。Q2 不知道答案，以「沒有站台停在 77.0～77.2」處理，H3 只補註解。

## 批次總覽

實作方式：主代理寫階段規格並獨立驗收，一次只派一個會改檔的 subagent。跨多檔或涉及判定邏輯用 Sonnet 5.5，機械性替換用 Haiku 5.5。批次 B（郵件去重鍵）由主代理自己做。畫面相關的批次（C、E4、G1）套 `ui-ux-pro-max`，以 `docs/DESIGN-SYSTEM.md` 為基準。

| 批次 | 內容 | 項目 | 來源 | 規模 | 相依 |
|---|---|---|---|---|---|
| A | 說明書 AI 版與使用者版同句 | A1～A5 | BACKLOG | 小 | 無 |
| C | 整列連結的點擊區被截斷欄破壞 | C1～C2 | 第 79 輪 | 小 | 無 |
| B | 郵件去重鍵含 NetIQ 嘗試欄位、重試入口空轉 | B1～B3 | 第 78 輪 | 中 | 無 |
| D | 技術原文出口：狀態欄位與守門範圍 | D1～D3 | 第 76、79 輪 | 中 | 無 |
| E | 輸入驗證：中文訊息、涵蓋守門、設定上限、翻頁上限 | E1～E4 | 第 77、79 輪 | 中 | 無 |
| F | 排程與時鐘：保留清除重試、逾期口徑、背景輪詢 | F1～F3 | 第 76、79 輪 | 中 | Q8 |
| G | 報表與 PRTG 判定 | G1～G3 | 第 75 輪 | 小 | Q3 |
| H | 防禦缺口：正規式逾時、帳號截斷、舊指紋、NLog 設定守門 | H1～H4 | 第 77、79 輪 | 小 | Q2、Q7 |
| X | 回顧留下的四個探針（成立才修） | X1～X4 | 各輪 | 小 | 無 |
| I | 文件與說明書同步 | I1～I7 | 第 76、78、79 輪 | 小 | B、D、E、F、G |
| Z | 終檢、全量、交接 | Z1～Z6 | — | 中 | 全部 |

順序：A → C → B → D → E → F → G → H → X → I → Z。A 是原定項目，C 是使用者每天會碰到的操作，兩者先做。

版號：開輪 1.0.80.0。

### 全輪共同契約（每個階段規格與驗收都要逐條抄進去）

- **先寫會紅的測試。** 每條缺陷修正先寫出在未修正的建置上會紅的測試並確認它紅，再修。版面缺陷先寫會紅的量測斷言。寫不紅時不硬修：回報「重現不到」，依該項目寫明的降級處置。
- **邊界兩側各一例。** 有門檻、上限、分隔規則的契約，驗收要有剛好通過與剛好不通過的案例；主代理每階段至少做一個突變，突變用檔案備份還原、`cmp` 確認並更新時間戳，不用 `git checkout`。
- **守門先拿反例試。** 新的守門或「搜尋零命中」類驗收，先放一個該命中的寫法確認它會紅。
- **收緊類改動寫出「原本可用、收緊後被擋」的情境**，並驗被擋時使用者看到什麼。
- **既有資料與既有設定不能因為規則變嚴而讀不出來或起不來。**
- **不新增設定。** 本輪用到的數值（重試間隔、次數）一律是程式常數。
- **沿用既有出口，不另造平行的一份。** 開工前先 `grep` 該判定或該對照表現有的唯一出口，改它或擴它；新類別只在沒有既有出口時才加。
- **移除時交代依賴方。** 刪掉的欄位、匯出、測試列成清單，以 `git grep` 零命中驗收。
- **定向測試篩選詞開工時實跑**，確認命中數大於 0 且含該階段新增的測試。改前端至少含 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`；改現行文件加跑 `DocNarrativeGuardTests`；改說明書加跑 `HelpContentServiceTests|HelpWritingGuide`；新增或改 `*.behavior.mjs` 加跑 `NodeRuntimeTests`；搬動 DTO 或端點加跑 `Authorization|ControllerRouteSnapshot|IssueExclusionTests`。
- **commit 寫成「測試通過才執行」的條件式指令**；每批驗收通過後在本輪分支 commit 並 push，確認遠端指向該提交。一段驗收提交後才開下一段。

## 批次 A：說明書 AI 版與使用者版同句

### 現況與核對結果

說明書問答的 AI 讀 `LogForesight.Web/HelpContent/*.ai.md`。以下原文都還在〔已核，2026-11-23 讀檔〕：

- **A1** `10-scheduler.ai.md:57` 流程圖寫本機主機「同步呼叫 AI」；`:101` 寫「本機分析的 AI 段會排隊等待，此時本機進度條短暫停留屬正常現象」。同檔 `:100` 的說法正確。程式事實：`AnalysisOrchestrator` 呼叫 `AnalyzeDayStatisticalAsync` 並標 `AiPending`，AI 只由 `AiAnalysisHostedService` 的 `RetryAiAsync` 補寫。
- **A2** `10-scheduler.ai.md:67` 寫 AI 例外或逾時時主機日標成「統計模式（AI 未分析）」。程式事實：主機日維持待補，標題加 `AiDegradation.ServiceFailedSuffix`，徽章顯示「AI 分析中」；「統計模式（AI 未分析）」只在 AI 沒有被呼叫時出現（`record-detail.js` 的 `aiStatusBadge`）。
  **同句的使用者版**：`10-scheduler.md:15`「AI 出錯時，那天改用統計模式記錄」。BACKLOG 記錄時沒列到這一句。
- **A3** `19-alert-tools.ai.md:12`「具 `Maintain`（統一標記為 `Assign` 且 `Handle`）」：統一標記只需要 `Assign`＋`Handle`。
- **A4** `03-issues.ai.md:31`「依主機、依日期這三個都不支援」：「三個」指處理狀態、未指派、逾期三種篩選。
- **A5** `19-alert-tools.ai.md:39`「靜音只影響之後的判定與畫面列出」：分析端只把區間內的日子標 `Suppressed`；畫面與派工端在問題目前靜音中時，連靜音前的日子也一併隱藏（`IssueExclusion.IsMuted`）。

已查證不必改（BACKLOG 記載）：「尚未有人接手」與「沒有進行中案件」同義；10 分鐘續跑與 60 分鐘空資料複查是兩套機制。`14-faq.ai.md` Q4、`04-record-detail*.md`、`13-glossary.md` 的「統計模式」描述的是「沒設定 AI」的情境，說法正確，不動。

### 定案

- 五處改成與程式事實一致、只有一種讀法的句子；A2 的使用者版同句一併修。
- 開工第一步重讀對應程式碼路徑確認事實仍成立。
- 使用者版照 `docs/HELP-WRITING-GUIDE.md`；AI 版每句只講一件事。
- A2 的使用者版要回答使用者真正會問的事：AI 出錯的那天畫面長什麼樣、要不要自己做什麼（不用，下次 AI 排程會再試）。
- 完成後從 BACKLOG「下一輪預定」移除這一項。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| A1 | `git grep -n "同步呼叫 AI" -- LogForesight.Web/HelpContent` 零命中；`:101` 那句不再說本機分析會等 AI。主代理對照 `AnalysisOrchestrator` 與 `AiAnalysisHostedService` 逐句核對改後文字 |
| A2 | AI 版與使用者版都不再把「AI 出錯」對應到「統計模式」；`git grep -n "統計模式" -- LogForesight.Web/HelpContent` 的每一處命中都只描述「沒有呼叫 AI」的情境（逐處列在執行紀錄）。對照 `record-detail.js` 的 `aiStatusBadge` 三種徽章文字 |
| A3 | 改後句子把「靜音與抑制需要 `Maintain`」和「統一標記需要 `Assign`＋`Handle`」分成兩句；對照 `IssueBulkCloseController` 的授權屬性 |
| A4 | `git grep -n "這三個都不支援"` 零命中；改後句子列出三種篩選的名稱；對照 `records.js` 的 `VIEW_FILTER_SUPPORT` |
| A5 | 改後文字分開說明「分析端」與「畫面與派工端」兩種效果；對照 `IssueExclusion.IsMuted` |
| 全批 | `dotnet test --filter "FullyQualifiedName~HelpContentServiceTests\|FullyQualifiedName~HelpWritingGuide"` 通過（開工時先確認命中數大於 0）；五處的「改前句／改後句／對應程式位置」對照表寫進執行紀錄；BACKLOG 該項已移除 |

## 批次 C：整列連結的點擊區

### 現況與核對結果

〔已核，實測〕`wwwroot/css/components.css:1147`：

```css
.lf-row-link :where(a:not(.lf-row-link__anchor), button, input, select, textarea, label, summary, [data-bs-toggle], [title]) { position: relative; z-index: 2; }
```

`[title]` 會選到帶 `title` 的 `<td>`；`core/ui.js:857` 的截斷欄（`truncate: true`）正是把 `title` 設在 `<td>` 上。

- **實測**：事實＝連結放在截斷欄那一格時，同列其他格的中心點 `elementFromPoint` 回傳 `TD` 而不是連結；連結放在別格時，截斷欄那一格回傳 `TD`；沒有 `title` 的對照列每格都回傳連結。環境＝macOS、Claude 桌面版內建瀏覽器（Chromium），視窗 1000×600。資料性質＝只含三列的最小靜態頁，載入專案**實際的** `components.css`，沒有載入其他三層樣式與 Bootstrap。完整站台與其他瀏覽器**未實測**。
- 使用整列連結的表格〔已核，`git grep rowHref`〕共 16 張，分在六個頁面模組：`dashboard.js`（2）、`handler-detail.js`（2）、`records.js`（4）、`user-detail.js`（5）、`users.js`（1），另 `host-detail.js` 已改用列詳情。哪幾張同時有截斷欄，開工時逐張列出。
- 沒有其他模組或測試以 `td[title]` 找元素〔已核，`git grep "\[title\]"` 在 `wwwroot/js` 與測試專案零命中〕。
- 既有測試沒抓到：`SharedExitGuardUiTests` 只做字串斷言（而且把 `[title]` 這個選取器本身釘住）；六項量測不量點擊命中。

### 根因

「讓列內帶提示的小元素浮在連結之上」的規則用屬性選取，沒有排除儲存格本身。浮起來的元素會吃掉點擊，所以它只該用在「本來就不該觸發整列導向」的元素。

### 定案（行為契約；內部作法由實作決定）

- **C1 整列每一格都能導向。** 有整列連結的列，點任何一格的任何位置（含被截斷的文字本身）都導向該列的網址，並保有原生連結行為（Ctrl／Cmd 點擊與中鍵開新分頁）。例外只有格內的互動元素（按鈕、輸入、其他連結、揭露鈕）：點它們執行它們自己的動作。
- **C2 截斷文字的完整內容仍看得到。** 滑鼠停在被截斷的文字上會顯示完整文字。不得為了保住提示而讓那段文字點不到（那正是現在的缺陷之一）。
- 帶提示的非互動小元素（徽章、縮寫）維持現況：浮在連結之上、顯示提示、點它不導向。這是既有行為，本輪不改。
- 抬升規則改成排除儲存格（寫成 `:not()` 排除，不事後覆寫）。
- 補一項全站量測斷言進 `LogForesight.Tests/tools/ui-global-asserts.json`，每個量測情境都檢查（判準見驗收 C1）。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| C1 | 新增全站量測斷言：每個 `tr.lf-row-link` 的每一格，取格的中心點與格內文字的中心點各一，`elementFromPoint` 命中的元素必須是「`closest('a')` 的 `href` 等於該列連結網址」的元素，或是格內的互動元素。**先在未修正的建置跑**：問題查詢「依主機」視角與使用者詳情主機表必須為紅（紅不了表示斷言是空的，先修斷言）。修正後 16 張表所在的頁面、五個寬度下為 0 |
| C1 | 行為測試：`renderTable` 帶 `rowHref` 與截斷欄，連結所在格與截斷欄不同格、同格兩種排列各一例，斷言 DOM 結構符合契約。列內有按鈕的格，按鈕不被連結包住 |
| C2 | 量測斷言：被截斷的儲存格（`scrollWidth > clientWidth`）文字中心點命中的元素或其祖先帶有等於完整文字的 `title`。未被截斷的格不要求 |
| C2 | 行為測試：沒有整列連結的表格，截斷欄的 `title` 位置與內容和修改前相同（`renderTable` 的其他使用頁不受影響） |
| 全批 | 16 張表所在頁面的全部既有量測情境重跑，六項量測維持 0；`SharedExitGuardUiTests` 的字串斷言同步；定向 `UiTests\|JsModuleImport\|BehaviorTests\|ApiTimeout\|NodeRuntimeTests` 通過。突變：把 `:not()` 排除拿掉，C1 的量測斷言轉紅 |

## 批次 B：郵件去重鍵與重試入口

### 現況與核對結果

- **B1 郵件內容指紋仍含 NetIQ 嘗試狀態與時間。**
  - 〔已核〕`HostDayWorkflowState.cs:1324` `ForMailContent` 只把 AI 已分析、AI 待補、詳情已清除三個旗標固定為 false；它呼叫的 `ForRecordWithIdentity`（同檔 `:1341` 起）仍序列化 `LatestNetiqAttemptStatus` 與 `LatestNetiqAttemptAtUtc`。
  - 〔已核〕回寫入口：`NetiqPipelineService.TryRecordAttempt`（`:343`，呼叫點 `:370`、`:627`、`:641`、`:668`、`:677`、`:687`、`:793`～`:801`）→ `NetiqSourceAttempt.RecordBatch` → `PatchParentAttempt`，以 `json_set`／`JSON_MODIFY` 就地改寫既有父紀錄的這兩欄。`:641` 在每批開始時寫 `Running`，結束時再寫 `Failed`／`Stopped` 等。
  - 〔已核〕同檔 `:1263` `ForParentRecord` 的註解寫明這個回寫，所以父指紋已把兩欄排除。郵件指紋沒有跟著改。
  - 〔待核〕完整序列：高風險、父紀錄需重試的 NetIQ 主機日已寄信 → 下一趟續跑重查同一個主機日並再次失敗（問題清單不變）→ `NotifyAfterRunAsync` 重算出不同的鍵 → 重寄。要以測試重現。
- **B2 重試入口對一類終態項目空轉。**〔已核條件存在；空轉行為待核〕`MailNotificationService.cs:370` 的觸發條件含 `IsRetryableLegacyNoQualifiedIntent`（`:440`；狀態 `no-qualified-recipient` 且收件人仍是 `failed-or-unknown`／`sending-result-unknown`）。這類項目屬終態，不會有進度，存在期間每 30 分鐘（退避上限）整套通知重跑一次。`docs/WEB-SPEC.md:2680` 只寫「非終態且尚未記為已寄」。
- **B3 同一份欄位的其他指紋（同型普查）。**〔待核〕`ForRecord` 同樣含嘗試欄位，被用作 AI 寫回的樂觀檢查（`EfAnalysisRecordStore.cs:516`、`:563`、`:663`，`AiAnalysisHostedService.cs:604`、`:623`，`LogAnalysisService.cs:498`～`:537`）與報表輸入指紋（`ForReportInput`）。疑點：AI 正在判讀某主機日時，續跑對同一主機日寫了 `Running`，AI 結果會不會被當成「紀錄已變」而丟棄、下一輪重做。

### 根因

第 78 輪批次 0 的定案是「指紋不含與信件內容無關的狀態」，列舉時只列了三個 AI 旗標。帳本回寫是第 79 輪才確認的寫入入口，修父指紋時沒有回頭看同一份欄位的其他指紋。

### 定案

- **B1**：`ForMailContent` 排除兩個嘗試欄位，與 `ForParentRecord` 同口徑。
  - 升級相容：已寄鍵是用舊算法（含嘗試欄位）算的。舊鍵對照（`LegacyKeySent` 一類）加上「以目前紀錄的嘗試欄位值、舊算法」算出的鍵。嘗試欄位自寄出後沒被改寫的主機日，舊鍵對得上，不重寄；已被改寫的主機日，舊值已不在紀錄裡，舊鍵無法重建，升級後會重寄一次——這與升級前的行為相同，不是新增的重寄。註解標明可移除的時點（升級滿 14 天回望期）。
  - 不新增函式簽章以外的抽象：`ForRecordWithIdentity` 加一個「是否含嘗試欄位」的參數即可，與 `ParentFingerprint` 的 `includeAttempt` 同形。
- **B2**：這類舊項目視為終態，不再觸發重試；判定仍只在 `MailNotifyStateStore.IsTerminalUrgentStatus`，不在重試入口另寫一份清單。開工第一步先讀 `IsRetryableLegacyNoQualifiedIntent` 的來歷（`git log -S`）：若它是為了某個「該補寄」的升級情境而留，改成有期限的出口（超過 14 天回望期就不再觸發），並回寫本文件。
- **B3**：只做探針，本輪不改 `ForRecord` 的內容（它同時是失敗路徑的復原判定，`HostDayWorkflowState.cs:1205` 刻意使用）。探針成立（AI 結果確實被丟棄）就回報使用者，附影響與修法選項，由使用者決定是否在本輪加做。

### 影響面

- 去重鍵改變同時影響已寄鍵、待送項目的鍵與 `SupersedeUrgentOutbox`；三處用同一個函式，改一處即可，但相容路徑三處都要驗。
- 直接填信箱的收件人與以帳號解析的收件人走同一個鍵，兩類都要有案例。
- CLAUDE.md「不要讓郵件去重鍵吃到與信件內容無關的狀態」條目補上嘗試欄位（I3）。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| B1 | **紅測試**：高風險的 NetIQ 主機日寄信成功 → 經 `NetiqSourceAttempt.RecordBatch`（正式碼的入口，不用只有測試呼叫的 `Mark`）依序寫 `Running`、`Failed`，問題清單不變 → 再跑 `NotifyAfterRunAsync`。斷言沒有新增寄信、待送項目沒有換鍵、沒有項目被標 `superseded`。未修正時必須紅。寫不紅時改列「防禦性對齊」，仍做排除，並在執行紀錄寫明走不到的原因 |
| B1 | 邊界另一側：嘗試欄位不變、問題清單多一件 → 必須重寄 |
| B1 | 升級相容：以舊算法預先寫入已寄鍵、嘗試欄位之後沒被改寫 → 升級後不重寄。另一條不經相容路徑：新算法寫入已寄鍵、嘗試欄位被改寫 → 不重寄 |
| B1 | 突變：把嘗試欄位加回 `ForMailContent`，紅測試轉紅；把相容鍵拿掉，升級相容測試轉紅 |
| B2 | 造一筆 `no-qualified-recipient`＋`failed-or-unknown` 的舊項目，可替換時鐘推進 2 小時並每 60 秒呼叫一次 `RetryPendingUrgentAsync`：整套通知重跑次數為 0（若定案改成有期限的出口：期限內照退避重跑、期限後為 0，兩側各一例） |
| B2 | 邊界另一側：非終態且未寄的項目仍照原退避重跑（既有測試維持綠） |
| B3 | 探針測試：AI 取得主機日輸入後、寫回前，經 `RecordBatch` 寫 `Running`；記錄 AI 寫回是否被拒絕。結果與判定寫進執行紀錄；不論結果，本批不改 `ForRecord` |
| 全批 | `dotnet test --filter "FullyQualifiedName~MailFingerprintTests\|FullyQualifiedName~MailNotificationServiceTests\|FullyQualifiedName~MailNotify\|FullyQualifiedName~NetiqPipeline"` 通過（開工時實跑確認各詞命中）；兩個後端的 EF 翻譯測試不受影響 |

## 批次 D：技術原文出口

### 現況與核對結果

- **D1 結構同步把例外原文存進狀態欄位。**〔已核〕`LogForesight.Core/Service/PrtgStructureSyncRunner.cs:119` `status.ErrorMessage = ex.Message;`；`:74`、`:142` `mapError = ex.Message` → `status.ErrorMessage = $"主機對應失敗：{mapError}"`。經 `PrtgStructureSyncService.cs:243` `LastErrorMessage` 回給維護頁顯示。
  - 兩類例外要分開看：`PrtgClientException`（`PrtgClient.cs:48`）的訊息依其契約「不含 token、密碼或 passhash，可直接顯示給操作者」，而且內容正是管理者需要的（逾時、認證失敗、轉址）；EF／SQL／一般例外的原文含伺服器名、資料表名、路徑，不該回給畫面。
  - 既有出口：`LogForesight.Core/PrtgFailureText.cs`（全站「PRTG 失敗種類 → 說明」唯一對照）、`LogForesight.Core/Persistence/MigrationFailureText.cs`（固定說明＋例外型別名的形狀）。`UserFacingMessage` 在 Web，Core 不能參考它。
- **D2 守門只認固定變數名、只掃 Web。**〔已核〕`UserFacingMessageGuardTests.cs:17` 的 `MessageUse` 只比對 `ex|exception|*Ex|workflowError|error|…` 這些識別字加 `.Message`；`e.Message`、`inner.Message`、`ex.InnerException.Message`、`ex.GetBaseException().Message` 都不命中。掃描範圍只有 `LogForesight.Web`。目前 Web 端零洩漏〔subagent 以較寬的 grep 核對〕。
- **D3 遷移失敗訊息的驗收只測純函式。**〔待核〕第 79 輪 B4 的驗收原定「讓遷移擲帶標記的例外：閘門回應、健康頁、告示都不含標記」；實作的 `ErrorOutputBoundaryTests` 只測 `MigrationFailureText.For(...)` 與 middleware 一層。遷移器若把 `LastError` 改回 `ex.Message`，`MigrationGateMiddleware` 的「（原因：…）」又會帶原文，測試仍綠。

### 定案

- **D1**：`PrtgClientException` 維持顯示它的訊息（管理者要靠它判斷是帳密、網路還是逾時）；其他例外寫「固定說明＋例外型別名，詳細原因見記錄檔」，形狀與 `MigrationFailureText` 相同。判定寫成 Core 裡的一個小函式，放在 `PrtgFailureText`（它已是這類文字的唯一出口），不另開類別。原文照舊寫進主控台輸出與記錄檔。已存在資料庫的舊 `ErrorMessage` 不回頭清，下次同步覆寫。
- **D2**：守門改成「任何成員存取鏈結尾是 `.Message`」都命中，再以型別白名單排除非例外的 `.Message`（事件列等），每條白名單附理由。掃描範圍加 Core 中「把 `.Message` 賦值給屬性」的寫法；主控台 `WriteLine` 與 `Log.` 不算（Q5）。
  - 為什麼不改成編譯期禁用 `Exception.Message`：正式碼把例外訊息寫進記錄檔與主控台是合法用法，數量上百處，逐處放行的成本高於這條守門的價值。維持來源掃描，但把偵測面放寬到不依賴變數名。
- **D3**：補整合斷言，三個遷移器各一例。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| D1 | **紅測試**：結構同步注入擲 `InvalidOperationException("MARKER-Server=sql01;path=/x")` 的來源，斷言狀態物件與 `GET prtg-structure-sync/status` 的 `lastErrorMessage` 不含 `MARKER`、含例外型別名與固定說明；主控台輸出含 `MARKER`。未修正時必須紅 |
| D1 | 另一側：來源擲 `PrtgClientException`（逾時類）→ `lastErrorMessage` 含該訊息的說明文字（管理者看得到可處置的原因） |
| D1 | 主機對應失敗那條路徑（`:74`、`:142`）兩類例外各一例，前綴「主機對應失敗：」保留 |
| D2 | 在目前 HEAD 暫時加入反例 `e.Message`、`ex.InnerException.Message`、`ex.GetBaseException().Message` 各一行，守門必須紅；移除後綠。把 D1 的修正還原（突變），守門必須紅 |
| D2 | 白名單逐條附理由；白名單裡每一條都以 `git grep` 確認對應的寫法確實存在（不留無用條目） |
| D3 | 三個遷移器各注入會擲帶標記例外的 store：`State.LastError`、健康頁原因、`MigrationGateMiddleware` 回應本文都不含標記；NLog 記憶體 target 收到含標記的訊息。突變：把任一遷移器改回 `ex.Message`，對應斷言轉紅 |
| 全批 | `dotnet test --filter "FullyQualifiedName~UserFacingMessage\|FullyQualifiedName~ErrorOutputBoundaryTests\|FullyQualifiedName~PrtgStructureSync\|FullyQualifiedName~Migration"` 通過 |

## 批次 E：輸入驗證

### 現況與核對結果

- **E1 驗證屬性沒有中文訊息。**〔已核數量；訊息字樣待核〕`LogForesight.Web/Models` 下 135 個 `[StringLength]`／`[MaxLength]`／`[MinLength]`／`[Range]`／`[EmailAddress]`／`[RegularExpression]`／`[Length]`，其中 67 個沒有 `ErrorMessage`。`ChineseModelBindingMessages`（`Filters/ChineseModelBindingMessages.cs:27` 掛了 `ImplicitRequiredMessageProvider`）只處理繫結訊息與沒寫訊息的 `[Required]`。違規時信封訊息預期是框架英文原文加 C# 屬性名。
- **E2 預設輸入上限的涵蓋有缺口。**〔已核〕`DefaultInputLimitsFilter.cs:56` `IsRequestDto` 只認命名空間以 `LogForesight.Web.Models` 開頭的型別。`PrtgTransferCreateRequestDto` 定義在 `Controllers/Api/PrtgTransferController.cs`，在範圍外〔它是否為唯一一個，待核〕。沒有守門保證每個 `[FromBody]` 型別都在範圍內。
- **E3 `Jwt:ExpireHours` 沒有上限。**〔已核〕`AppSettings.cs:67` 只檢查 `<= 0`。這個值是登入的閒置時限（預設 8 小時），絕對上限固定 24 小時，大於 24 的值沒有意義；極大值在換算時溢位〔溢位的例外型別未實測〕。
- **E4 PRTG 評估預覽翻頁上限。**〔已核〕`ScaleLimits.MaxPreviewOffset = 30_000`，`PrtgMonitoringController.cs:325` 超過回 400。預覽的列是感測器；3,000 台、每台超過約 10 顆時總數就超過 30,100，最後幾頁翻不到，使用者只會看到一則錯誤訊息。

### 定案

- **E1**：擴充既有的 `ImplicitRequiredMessageProvider`（或在同一個檔案加同形的 provider），為沒寫 `ErrorMessage` 的這幾類屬性補中文預設訊息。訊息不得露出 C# 屬性名：有 `[Display]` 名稱用它，沒有就用不帶欄位名的通用句（例如「輸入值超出允許範圍（1～5000）」）。不逐一在 67 處手寫訊息，也不為沒有顯示名稱的欄位補 `[Display]`（那是另一件事）。
- **E2**：把該 DTO 移到 `Models`；加反射守門：所有 `[FromBody]` 參數型別必須是 `LogForesight.Web.Models*` 下的類別，直接以集合或字串當 body 的要在守門列白名單並附理由。
- **E3**：大於 24 的值視為 24，並在啟動記錄檔寫一行警告；不擋啟動（既有站台若填了大值，升級後不能起不來）。只有會讓換算溢位的值才列入啟動錯誤，訊息為中文。0 與負數維持擋下。
- **E4**（依 Q6）：到上限時前端停用「下一頁」並在分頁列顯示「已到可瀏覽的上限，請用篩選縮小範圍」；上限剛好那一格（offset 30,000）仍可讀。說明文字用設計系統既有的次要說明角色類別，不新增樣式。後端 400 的訊息維持，作為非前端途徑的保護。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| E1 | 以 `WebApplicationFactory` 對一個 `[Range]`、一個 `[StringLength]` 端點各送超界值：回應訊息不含 `The field`、不含 C# 屬性名、含中文與允許範圍。**先在未修正的建置跑確認紅**（同時取得〔待核〕的英文字樣） |
| E1 | 邊界：值等於上限 → 通過；上限加一 → 被擋 |
| E1 | 反射測試：`Models` 下所有沒寫 `ErrorMessage` 的驗證屬性，其型別都在 provider 的涵蓋表內。先拿一個不在表內的屬性型別當反例確認會紅 |
| E1 | 已手寫 `ErrorMessage` 的 68 處不受影響：抽 3 處，訊息與修改前相同 |
| E2 | 守門：把 DTO 移回控制器檔（突變）必須紅。`PackageSha256` 送超過預設上限的字串 → 400 且訊息為中文。搬移後 `ControllerRouteSnapshot` 與該端點既有測試維持綠（JSON 形狀不變） |
| E3 | `ExpireHours` = 24 → 啟動成功、無警告；25 → 啟動成功、有一行警告、實際閒置時限為 24 小時；0 與 −1 → 啟動錯誤；極大值 → 啟動錯誤且訊息為中文 |
| E4 | `*.behavior.mjs` 案例：總數 30,150、位於 offset 30,000 的頁 →「下一頁」停用、說明文字出現；總數 30,050、offset 29,900 的頁 →「下一頁」可按、沒有說明；總數 500 → 沒有說明。新增案例登錄 C# `InlineData` |
| E4 | UI 量測：PRTG 維護頁在五個寬度下六項量測為 0（說明文字不造成溢出） |
| 全批 | `dotnet test --filter "FullyQualifiedName~InputValidation\|FullyQualifiedName~DefaultInputLimits\|FullyQualifiedName~ScaleLimits\|FullyQualifiedName~Authorization\|FullyQualifiedName~ControllerRouteSnapshot\|FullyQualifiedName~AppSettings"` 與前端四類、`NodeRuntimeTests` 通過（`AppSettings` 的實際測試類別名開工時以 `git grep -l "ExpireHours" -- LogForesight.Tests` 確認後寫進篩選） |

## 批次 F：排程與時鐘

### 現況與核對結果

- **F1 單獨保留清除失敗仍記成今天已完成。**〔已核〕`SchedulerHostedService.cs:678-679`：`await _orchestrator.RunRetentionOnlyAsync(...)` 之後無條件 `_retentionConfirmedDate = now.Date`。`AnalysisOrchestrator.RunRetentionOnlyAsync`（`:921`）回 `Task`，`RetentionPruner.Run` 的結果被丟掉。清除器自己的註解寫「失敗時不記錄今日已執行，留待下次重試」。失敗要等隔天或站台重啟才重試；健康告示仍會出聲。
- **F2 「處理中逾期」直接讀系統時鐘。**〔已核〕`IssueTodoQuery.cs:121` `var today = DateTime.Today;`。呼叫端只有兩個：`IssueTodoQuery.cs:105`（儀表板）與 `MailIssueDigest.cs:212`（每日摘要）。交辦單到期判定用注入的 `TimeProvider`，兩邊對同一張單可能用到不同的「今天」。`HandoffClockGuardTests` 的範圍不含這兩個檔。
- **F3 作業進度輪詢沒有標背景。**〔已核輪詢點；各回呼的請求待核〕`pollWhile` 的呼叫點：`prtg-admin.js:407`（回填，2 秒）、`:2645`（探測）、`:3020`（結構同步）、`netiq.js:433`、`netiq-import-wizard.js:331`；這三個檔案沒有任何 `background: true`。同類的 `runs.js:1326`、`:1880` 有標。守門 `BackgroundPollingHeaderGuardTests` 只列三個固定端點。影響：這些頁面開著時，閒置時限（預設 8 小時）不會到期，要到 24 小時絕對上限才登出。

### 定案

- **F1**：`RunRetentionOnlyAsync` 回傳清除結果。失敗時不設 `_retentionConfirmedDate`，改記下次可重試時間（30 分鐘後）；當天最多重試 3 次，之後等隔天。兩個數值是程式常數。判定抽成一個不依賴真時鐘的純函式（輸入：現在、上次結果、當天已試次數；輸出：要不要跑），排程服務只負責呼叫它。
- **F2**：`IsOverdueInProgress` 加一個「今天」參數，兩個呼叫端從注入的 `TimeProvider` 取值；兩個檔案加進 `HandoffClockGuardTests` 的範圍。口徑仍只有這一個函式。
- **F3**（依 Q8）：規則定為「頁面開著就會持續送、不需要使用者再操作的輪詢，一律標背景」。五個呼叫點補標。守門改成掃 `wwwroot/js/pages` 與 `wwwroot/js/core` 的每個 `pollWhile(`：回呼直接或經由同檔具名函式發出的 `api.get` 必須帶 `background: true`；不發請求的純本地計時（`runs.js:1416`）與其他例外在守門列白名單並附理由。
  - 收緊情境：管理者開著進度頁、超過閒置時限沒有任何操作，會被登出；作業在伺服器端繼續。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| F1 | 純函式測試（可替換時鐘）：第一次失敗後不算完成；29 分鐘時不跑、30 分鐘時跑；第 3 次重試失敗後當天不再跑；跨日重新計；成功後當天不再跑。**紅測試**：以替身讓清除某一段擲例外，斷言排程服務沒有把今天記成完成（未修正時紅） |
| F1 | 整輪分析路徑的保留清除行為不變（既有 `RetentionPrunerTests` 與整輪路徑測試維持綠） |
| F2 | 情境測試：可替換時鐘撥到預計完成日的隔天 → 儀表板逾期數與每日摘要的逾期清單都含該單；撥回預計完成日當天 → 兩邊都不含。守門：在範圍內的檔案暫時寫入 `DateTime.Today` 必須紅 |
| F3 | 守門在目前 HEAD（未補標）必須紅，且列出的點包含上述五個；補標後綠。突變：拿掉其中一個標記，守門轉紅 |
| F3 | 伺服器端行為（既有 `CookieAuthentication` 類測試同型）：帶背景標頭的請求不延長閒置時限；不帶的會延長 |
| F3 | 收緊情境：以 `WebApplicationFactory` 啟動一個回填替身 → 可替換時鐘推過閒置時限 → 進度端點回 401 → 重新登入後進度端點仍回同一個作業的進度 |
| 全批 | `dotnet test --filter "FullyQualifiedName~RetentionPruner\|FullyQualifiedName~RetentionRetry\|FullyQualifiedName~HandoffClockGuardTests\|FullyQualifiedName~IssueTodoQuery\|FullyQualifiedName~MailIssueDigest\|FullyQualifiedName~BackgroundPollingHeaderGuardTests\|FullyQualifiedName~PollingUiTests\|FullyQualifiedName~CookieAuthentication"` 與前端四類通過。`RetentionRetry` 是 F1 純函式的新測試類別（目前沒有以 `SchedulerHostedService` 命名的測試類別，該詞篩不到東西，不使用）；寫完後回頭確認命中 |

## 批次 G：報表與 PRTG 判定

### 現況與核對結果

- **G1 前期趨勢跨保留線時補 0。**〔已核〕`ReportService.cs:126-130`：只有整段超出保留期才回空陣列；部分超出時仍呼叫 `BuildTrendFromAggregate`，同檔 `:283-290` 對沒有資料的日子補 `HighRisk = 0`。前端把 0 畫成線，看起來像前期完全沒有高風險日。畫面上方有 `comparisonPartiallyOutOfRetention` 的警示（`reports.js:283`）。第一排的前期 KPI 數字在這種情況下同樣只統計到保留線以後，靠同一則警示說明；本輪不改 KPI 的算法。
- **G2 兩個欄位沒有前端消費端。**〔已核〕`git grep -i` 在 `LogForesight.Web`、`LogForesight.Core`：`MaxSeverityPerCell` 只在 DTO 與 `ReportCategoryTrendService.cs:112`；`HandlingScopeApplied` 只在 DTO 與 `ReportEffectivenessService.cs:75`（恆為 false）。前端零命中。
- **G3 持平降級對 Low 仍標註並計數。**〔已核程式；情境待核〕`PrtgCrossDay.cs:60-69`：`switch` 對 Low 回原值，之後無條件 `AppendDetail(…數值持平…)`、`steadyDowngraded++`、`continue`（跳過跨日升級）。規則嚴重度可在畫面編輯，所以 Low 的 warning 規則可以存在。

### 定案

- **G1**：早於保留線的日子輸出「沒有資料」而不是 0：前期趨勢點的風險數改可空，早於保留線的日子為 null；保留線當天算有資料；真正沒有高風險的日子仍是 0。前端 null 不畫點，線從保留線那天開始。控制器宣告的回傳型別就是實際型別。
- **G2**（依 Q3）：移除兩個欄位。依賴方清單：DTO 屬性、兩個服務的賦值、測試斷言、`reports-views.behavior.mjs` 的夾具、WEB-SPEC 的端點欄位說明。
- **G3**：只有嚴重度實際改變時才標註、計數並跳過跨日升級；Low 走原本的跨日流程。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| G1 | 後端測試：比較期起點早於保留線 3 天 → 前 3 天為 null、保留線當天起為數字（含一個真正為 0 的日子）；整段超出 → 空陣列（既有）；完全在保留期內 → 沒有 null。**紅測試**：未修正時前 3 天是 0 |
| G1 | 序列化斷言：端點回應的 JSON 中那 3 天的值是 `null`（不是缺鍵、不是 0） |
| G1 | 前端行為測試：null 的點不進圖表資料序列；警示文字照常顯示。UI 量測：報表頁五個寬度六項量測為 0 |
| G2 | `git grep -i "maxSeverityPerCell\|handlingScopeApplied" -- . ':!docs/archive'` 零命中（本文件除外）；報表三個視角的既有測試與行為測試維持綠 |
| G3 | Low 的 warning 簽章、連續達門檻天數、證據持平 → 嚴重度仍為 Low、說明不含「數值持平」、降級計數為 0、跨日升級照常套用。**先在未修正時跑確認紅**。Medium 與 High 那一側維持降一級（既有 `PrtgCrossDaySteadyTests`） |
| 全批 | `dotnet test --filter "FullyQualifiedName~ReportService\|FullyQualifiedName~ReportCategoryTrend\|FullyQualifiedName~ReportEffectiveness\|FullyQualifiedName~PrtgCrossDay"` 與前端四類、`NodeRuntimeTests` 通過 |

## 批次 H：防禦缺口

### 現況與核對結果

- **H1 網址遮罩的正規式逾時沒有人接。**〔已核，實測〕`UrlSecrets` 的 `UserInfo` 與 `QueryParam` 都帶 1 秒逾時，`Mask`／`ContainsSecret` 沒有處理 `RegexMatchTimeoutException`；正式碼有 11 處呼叫，其中執行紀錄去識別化會拿它掃整段自由文字。
  - **實測**：事實＝`"http://" + "a:" 重複 50,000 次` 在 1,005 毫秒擲 `RegexMatchTimeoutException`；重複 5,000 次需 634 毫秒但不擲；20 萬字只含一個冒號的字串 13 毫秒；500 個各 2,000 字的網址 81 毫秒；一般網址 0 毫秒。環境＝macOS、.NET SDK 10.0.404、Release、獨立主控台程式（把兩個正規式原樣複製出來，不是呼叫專案的 `UrlSecrets`）。資料性質＝人工構造的最壞輸入（scheme 之後連續不含空白與 `@`、帶大量冒號）。真實日誌出現這種形狀的機率**未實測**；Windows 上的耗時**未實測**。
- **H2 超長帳號標籤截斷後保留。**〔待核〕`LogAggregator.cs` 的標籤值擷取：欄位邊界只認換行、tab、連續兩個以上空白；`keepOverlong`（`:313`、`:399`，帳號標籤）時超過 128 字改成截到 128 後保留。訊息以單一空白分隔時，整段後續欄位會成為一個 128 字的「帳號」，進帳號集合與登入失敗計數。
- **H3 舊父指紋缺 1.0.77.0～77.2 的算法。**〔待核〕`LegacyParentFingerprints` 的兩個舊算法都以目前的精簡投影（含已抑制告警）計算；77.0～77.2 的精簡投影不含已抑制告警。
- **H4 NLog 設定在執行中途被換掉時，該趟的警告計數靜默歸零。**〔待核〕`RunLogCapture.EnsureInstalled` 只在啟動與 `BatchRunRecorder` 建構時檢查。正式環境唯一會整份更換設定的途徑是 `nlog.config` 開 `autoReload`，目前沒有開。

### 定案

- **H1**：兩件都做，各一行等級的改動。
  - 根因：`UserInfo` 的式子改成不會平方級回溯的寫法，語意不變（使用者資訊取到主機段最後一個 `@`、有冒號才算帳密）。
  - 防線：`Mask` 與 `ContainsSecret` 接住逾時——`ContainsSecret` 回 true，`Mask` 回固定的整段遮蔽字串，並記一行不含原文的警告。
- **H2**（依 Q7，暫定）：截斷保留只在值內沒有「下一個標籤樣貌」時套用，否則維持回空值（第 79 輪以前的行為）。開工時用測試專案既有的真實樣本訊息確認不會誤判合法的超長帳號；會誤判就不改判定，改回報使用者。
- **H3**（依 Q2）：有站台停過那三個版本才加第三個舊算法；沒有就只在 `LegacyParentFingerprints` 的註解補一句涵蓋範圍。
- **H4**：不改執行期程式（CLAUDE.md：不得在執行期增減 target 或規則；為一個目前到不了的情境加偵測邏輯屬過度設計）。改成一條守門測試：`nlog.config` 不得開 `autoReload`，並在該檔註解寫明原因。有人打開時測試會紅，屆時再處理擷取。

### 驗收

| 項目 | 驗收（主代理執行） |
|---|---|
| H1 | 測試呼叫專案的 `UrlSecrets.Mask`／`ContainsSecret`：上述最壞輸入不擲例外，且耗時小於 200 毫秒（式子修好的證據）。**先在未修正時跑確認擲例外** |
| H1 | 等價性：既有 `UrlSecrets` 相關測試全綠；另補「密碼含 `@`」「只有帳號沒有密碼不遮」「一段文字含三個網址」「查詢參數含機敏名稱」四例，修改前後輸出相同（修改前先錄預期值） |
| H1 | 防線：以極短逾時建構的測試用實例（或注入逾時）讓比對必定逾時 → `Mask` 回固定遮蔽字串、`ContainsSecret` 回 true、警告不含輸入原文。突變：拿掉例外處理，此測試轉紅 |
| H2 | 單一空白分隔、帳號後接其他標籤的訊息 → 帳號為空（**未修正時為 128 字的假帳號，先確認紅**）；128 字純帳號保留；129 字純帳號截到 128 保留；含冒號的合法帳號樣本結果與修改前相同 |
| H3 | 做的情況：以 77.2 的算法預先寫入指紋 → 升級後 PRTG 腿不重設；另一條不經相容路徑的斷言。不做的情況：註解已補，`LegacyParentFingerprints` 的行為測試不變 |
| H4 | 守門測試：讀 `nlog.config`，`autoReload` 不存在或為 false。暫時改成 `autoReload="true"` 必須紅 |
| 全批 | 定向篩選詞開工時以 `git grep -l "UrlSecrets\|LegacyParentFingerprints\|keepOverlong\|RunLogCapture" -- LogForesight.Tests` 找到實際測試類別名後寫入（目前沒有名為 `UrlSecretsTests` 的類別），確認命中數大於 0 |

## 批次 X：回顧留下的四個探針

subagent 標為「沒把握」、值得花一個探針的項目。每個探針都是一條自動化測試或一次量測，**判定標準寫在表裡**。成立且修法在一個檔案內、不改行為契約的，在本批修並補上轉綠的測試；其餘成立的回報使用者，附影響與修法選項。不成立的在執行紀錄記一行，探針測試留下當守門。

| # | 疑點 | 探針與判定標準（主代理執行） |
|---|---|---|
| X1 | 帶有效 cookie 時資料庫讀不到，錯誤頁重新執行管線又擲一次，使用者看到空白 500 | `WebApplicationFactory` 注入會擲例外的使用者來源，帶有效 cookie 請求一個頁面與 `/api/health`。**成立**＝頁面回應本文為空或不是站台的錯誤頁；API 回應不是標準錯誤信封 |
| X2 | `CsvParser` 行號在有空行或跨行引號欄位時指錯列 | 兩份輸入：標題列＋空行＋一筆資料；含跨行引號欄位的一筆後接一筆。**成立**＝回報的行號不等於該筆資料在檔案中的起始行 |
| X3 | PRTG 快照手動封存時，記憶體中尚未落盤的樣本被丟掉，確認對話少報筆數 | 造一個因來源變更而停止、仍有累積樣本的狀態，比對封存前的預覽筆數、封存後的實際筆數與累積筆數。**成立**＝預覽筆數少於實際被丟棄的筆數 |
| X4 | 報表熱度格、分桶堆疊條下鑽後的筆數與格內數字不一致 | UI 資料集開報表「問題類型」與「處理成效」視角，各點一格，讀下鑽清單的總筆數。**成立**＝筆數不等於格內數字，且畫面沒有說明兩者單位不同 |

## 批次 I：文件與說明書同步

| 項目 | 內容 | 驗收（主代理執行） |
|---|---|---|
| I1 | `docs/WEB-SPEC.md:249`、`:292`、`:295`〔已核〕仍描述已移除的 serverAdmin 連續失敗鎖定與失敗計數。改成現況（登入節流，429「登入嘗試次數過多」）並指向 `LoginThrottle` | `git grep -n "已鎖定，請於\|清空失敗計數" -- docs/WEB-SPEC.md` 零命中；改後敘述逐句對照 `AuthController` 與 `LoginThrottle` |
| I2 | `docs/WEB-SPEC.md:2680` 重試退避的觸發條件依 B2 定案改寫；§9.4b 管理者提醒段的「收件人是目前操作者時不入列」〔待核〕改成不會被讀成適用於管理者提醒 | 改後敘述逐句對照 `MailNotificationService` 的重試入口與 `EnqueueAdminReminders`／`EnqueueWorkOrderNotices` |
| I3 | CLAUDE.md：郵件去重鍵條目補嘗試欄位；背景輪詢條目補「作業進度輪詢也算」；例外訊息條目補 Core 的狀態欄位與 `PrtgClientException` 的例外 | 三條各能指到本輪對應的守門測試名稱；`DocNarrativeGuardTests` 通過 |
| I4 | README 備份章節（`:111`、`:667`、`:766` 附近）：依 Q4 註明備份資料目錄時排除 `dataprotection\` 與原因 | 三處說法一致；`git grep -n "dataprotection" -- README.md` 的每一處都不與「排除」矛盾 |
| I5 | WEB-SPEC：報表端點欄位說明移除兩個欄位（G2）、前期趨勢點可為 null（G1）；PRTG 評估預覽的翻頁上限行為（E4）；`Jwt:ExpireHours` 大於 24 的處理（E3）；保留清除失敗的重試（F1） | 每一句對照程式；`DocNarrativeGuardTests\|DocCrossReferenceTests` 通過 |
| I6 | 說明書：使用者看得到變化的項目同步——報表章節若描述前期虛線，補「早於保留期的日子不畫」；PRTG 維護章節補預覽翻頁上限的提示；`01-overview.ai.md:37` 登入時限補上限 24 小時。先 `git grep` 各章節有沒有相關描述，沒有就不加 | `HelpContentServiceTests\|HelpWritingGuide` 通過；每處改動列「章節／改前／改後／對應程式位置」 |
| I7 | 定稿前掃敘事字眼（原本／後來／改為／取代／第 N 輪／回饋／體檢輪） | 現行文件的本輪改動行中零命中（`git diff` 加 grep） |

## 批次 Z：終檢、全量、交接

| 項目 | 內容 | 驗收（主代理執行） |
|---|---|---|
| Z1 | 逐條對照本文件定案與實作 | 每個項目編號在對照表各一列，填程式位置、驗收測試名稱、結果；沒有空白列 |
| Z2 | 兩次彼此獨立的全 diff 審查（程式、文件各一） | 兩份審查結果寫進執行紀錄；高嚴重度與推翻定案的發現由主代理讀碼確認後才處理 |
| Z3 | UI 量測 | 改到的頁面（問題查詢、儀表板、使用者詳情、處理人詳情、使用者清單、報表、PRTG 維護、NetIQ 維護）五個寬度六項量測為 0，加 C1／C2 的新斷言 |
| Z4 | 全量測試 | macOS 與 Windows 11 VM 各一次全綠；通過數、略過數與基線的差寫進「體檢交接」，差額能以本輪新增的測試數解釋 |
| Z5 | 版號與 BACKLOG | `VersionRoundGuardTests` 通過；BACKLOG 已移除說明書項目，並補記本輪「不做」中需要長期追蹤的項目 |
| Z6 | 遠端狀態 | `git rev-parse HEAD` 等於 `git rev-parse origin/feature/feedback-80` |

換模型體檢與收尾（歸檔、索引、現行文件只留結論）依專案慣例，在 Z 之後進行；併 dev 需使用者同意。

## 四視角檢視（2026-11-23）

每個視角先列「會被怎麼質疑」，再寫規劃的回應。標 **→** 的是因此修改了規劃的地方。

### 使用者視角（處理人、管理者、唯讀檢視者；以評論尖銳但講理的使用者為準）

| 項目 | 會被怎麼質疑 | 規劃的回應 |
|---|---|---|
| C | 「整列看起來都能點，結果只有第一格有反應，游標還是手指。點主機名稱也沒反應，要點旁邊空白才行。」 | **→** 第一版的修法會讓被截斷的文字浮在連結之上，等於把缺陷從「整格」縮成「文字」，沒有解決。契約改成 C1「點文字本身也導向」與 C2「提示仍在」同時成立，並以量測斷言驗兩個落點 |
| C | 「我習慣中鍵開新分頁，別又被做成 JavaScript 跳轉。」 | C1 明寫保有原生連結行為；驗收以 `closest('a')` 的 `href` 判定，JavaScript 跳轉過不了 |
| A2 | 「說明書說 AI 出錯會變統計模式，我畫面上明明寫 AI 分析中，到底哪個對？」 | 這正是 A2 要修的；**→** 補上使用者版同句，並要求使用者版回答「畫面長什麼樣、要不要自己處理」 |
| B1 | 「同一台主機同樣的問題，為什麼每晚再寄一封？」 | B1 的紅測試直接重現這個情境。升級後已被改寫過嘗試欄位的主機日還會再寄一次，規劃如實寫明，不宣稱零重寄 |
| D1 | 「同步失敗只寫『詳細原因見記錄檔』？我是管理者，至少告訴我是帳密錯還是連不上。」 | **→** 第一版一律改固定說明，會讓管理者失去可處置的資訊。改成 PRTG 連線類例外照舊顯示原因，只有其他例外換成固定說明 |
| E1 | 「欄位填錯跳一句英文加程式變數名，這是給誰看的？」 | E1；訊息不得露出屬性名，並含允許範圍 |
| E4 | 「翻到某一頁直接跳錯誤，連為什麼都不說。」 | E4 在到達上限的那一頁就停用按鈕並說明，不等使用者按了才知道 |
| F3 | 「我開著回填進度頁去開會，回來被登出？」 | 閒置時限預設 8 小時；作業不受登出影響，重新登入後進度仍在（F3 第三條驗收）。排程頁的同一種進度早已如此。仍列為 Q8 由使用者定 |
| G1 | 「虛線前半段貼著 0，我還以為去年都沒事。」 | G1；線從保留線那天開始，警示照舊 |
| G1 | 「那上面的前期數字呢？不也只算到一半？」 | 是，前期 KPI 在跨保留線時同樣只統計保留線以後，靠同一則警示說明。本輪不改算法（改了會變成「不比較」，是產品取捨），在現況段落寫明 |
| F2 | 「儀表板說逾期 3 件，摘要信說 2 件。」 | 正式環境兩邊用同一個系統時鐘，數字不會不同；F2 修的是口徑只有一份但時鐘來源兩種，測試撥時鐘時才會分岔。規劃不誇大成使用者可見的缺陷 |

### 管理者視角（系統管理員、部署與維運）

| 項目 | 會被怎麼質疑 | 規劃的回應 |
|---|---|---|
| E3 | 「升級後站台起不來，只因為我三年前把時限填 48？」 | **→** 第一版直接擋。改成視為 24 並警告，不擋啟動 |
| F1 | 「清除失敗，系統說今天做完了；那到底有沒有清？」 | F1；失敗不算完成，30 分鐘後重試，最多 3 次。健康告示照舊出聲 |
| F1 | 「重試會不會在白天把資料庫拖慢？」 | 單獨清除本來就只在當天分析沒跑、中午之後才補做；重試次數有上限，不新增設定 |
| B2 | 「每 30 分鐘整套通知空跑一次，記錄檔全是它。」 | B2；先查那類舊項目原本要補救什麼，再決定終止或加期限 |
| I4 | 「說金鑰環不用備份，那我整個資料夾備份走，金鑰不就跟著出去？」 | I4 改文件；不改成 DPAPI 的理由在 Q4 |
| H3 | 「升級後一堆主機日又顯示 PRTG 待補？」 | 只發生在停過 1.0.77.0～77.2 的站台，且只重設一次、會自己補回；Q2 請使用者確認 |
| 整體 | 「這輪有沒有要我做的事？改設定、跑遷移？」 | 沒有：不新增設定、不改資料表、不新增遷移。唯一的升級可見差異是 B1 的相容行為與 E3 的警告，I5 寫進 WEB-SPEC |

### 程式視角（單一職責、不重複、不過度設計）

| 項目 | 檢視 | 結論 |
|---|---|---|
| B1 | 指紋的「含不含嘗試欄位」已有 `ParentFingerprint(includeAttempt)` 的形狀 | 沿用同形的參數，不新增型別或策略類別 |
| B2 | 終態判定的唯一出口是 `IsTerminalUrgentStatus` | 在它裡面處理，不在重試入口另列清單 |
| D1 | 第一版考慮「Runner 接一個把例外轉成文字的委派」 | **→** 過度設計。Core 已有 `PrtgFailureText` 這個唯一出口，加一個小函式即可，Core 不必認識 Web |
| D2 | 來源掃描 vs 編譯期禁用 | 維持來源掃描並放寬偵測面；編譯期禁用需要上百處放行，成本高於價值（理由寫在 D2 定案） |
| E1 | 67 處逐一補訊息 vs 一個 provider | 一個 provider（擴既有的那一個）；不為了訊息去補 67 個 `[Display]` |
| F1 | 重試判定寫在排程服務的私有方法內就測不到 | 抽成純函式，排程服務只呼叫；不引入排程框架或重試程式庫 |
| F2 | 「今天」的來源 | 改成參數傳入，函式維持靜態、無相依；口徑仍只有一份 |
| F3 | 守門是固定三個端點的清單 | 改成掃輪詢出口 `pollWhile`，新加的輪詢自動納入 |
| H1 | 只接例外 vs 只改式子 | 兩者都做：式子是根因，接例外是防線；各自都只有幾行 |
| H4 | 第一版要在 `Finish` 時偵測設定被換掉 | **→** 過度設計：正式環境到不了這個狀態。改成一條設定守門測試 |
| G2 | 留著沒人用的欄位 | 移除（與「有設定無行為」同一條紅線的精神） |
| 另記 | `NetiqSourceAttempt.Mark` 在正式碼沒有呼叫端，只有測試使用 | 本輪不刪（三個測試類別依賴它布置資料）；B1 的新測試不得使用它。記進 BACKLOG |

### 整體視角（這些改動碰到的其他地方）

- **資料與升級**：不改資料表、不加遷移、不新增設定。升級當下的行為差異只有三處：B1（已寄鍵相容，見定案）、E3（大於 24 的時限被視為 24）、H3（依 Q2）。
- **兩個後端**：本輪沒有新增原生 SQL 或 `ExecuteUpdate`。B1 的測試走 `RecordBatch` 既有的兩套 SQL，SQLite 實跑；SQL Server 僅限既有的翻譯測試，實機未驗證。
- **授權與快取**：不新增端點、不新增快取。E2 搬移 DTO 的命名空間不改路由與 JSON 形狀，以路由快照與既有端點測試確認。G1／G2 改報表 DTO：報表快取以資料版本戳與可見主機為鍵，DTO 形狀改變不影響鍵；部署後舊行程的快取隨重啟消失。
- **前端共用模組**：C 改 `core/ui.js` 的 `renderTable`／`attachRowLink` 與 `components.css`，影響所有用整列連結的 16 張表——全部進量測。`renderTable` 的其他使用頁（沒有整列連結的表）也會經過截斷欄的程式碼：驗收含「沒有整列連結的截斷欄，`title` 行為與修改前相同」（加進 C2 的行為測試）。
- **閒置登出**：F3 讓五個輪詢不再延長工作階段。這些頁面上使用者按鈕觸發的請求不帶背景標記，照常延長。
- **偵測結果**：H2 會讓部分主機日的帳號集合與登入失敗計數少掉假帳號；只影響之後的分析，已寫入的紀錄不變。G3 只影響嚴重度為 Low 的 PRTG warning 簽章。
- **郵件**：B1、B2 都只會讓信變少，不會漏寄——B1 的邊界另一側驗「內容變了必須重寄」，B2 的邊界另一側驗「非終態項目照常重試」。
- **說明書與現行文件**：A 直接改說明書；E4、G1、E3 的使用者可見變化由 I6 同步；CLAUDE.md 三條由 I3 同步。
- **測試基線**：本輪只增不減測試（G2 會刪掉斷言兩個欄位的幾行）。Z4 要求通過數的差額能以新增測試數解釋。
- **批次之間的檔案重疊**：B 與 H3 都改 `HostDayWorkflowState.cs` 的指紋區段，順序 B → H。E4 與 F3 都改 `prtg-admin.js`，順序 E → F。C 與 E4、G1 都改前端但不同檔。每段驗收提交後才開下一段。
- **檢視後仍未涵蓋的風險**：C 只在 Chromium 驗證（量測工具用 Chrome）；Firefox／Safari 對表格儲存格的疊放行為未實測，列入正式站台確認。

## 明確不做（本輪定案，Q1／Q5 以建議為暫定）

- BACKLOG「下一輪預定」的 G4～G12 與批次改派入口：順延。
- Core 寫進主控台與執行紀錄的 `ex.Message`：不視為外洩，不改。
- Data Protection 金鑰環加密存放：只改文件。
- 前期 KPI 在跨保留線時的算法：不改，靠既有警示。
- `ForRecord`（AI 寫回的樂觀檢查）的內容：本輪只做探針（B3）。
- NLog 設定執行中更換的偵測：不做，改設定守門（H4）。
- 刪除只有測試使用的 `NetiqSourceAttempt.Mark`：不做，記 BACKLOG。
- 回顧中列為觀察、不值得一個探針的項目：救援帳號密碼雜湊改回舊值時舊 cookie 恢復有效（需 24 小時內改兩次）；`ChannelPolicy.IsInformationalAllowed` 以子字串比對提供者名（沒有證據顯示真實環境有撞名的提供者）；`ColumnClamp.ClampFor` 對沒有長度上限的屬性原樣回傳（目前所有呼叫點都有上限）；`WorkOrderOutboxItem` 行內切字串（BACKLOG 既有條目涵蓋）；閒置時限的實際語意是時限的一半到全長之間（文件已寫續發規則）。
- 第 75 輪批次 A／B／C 的 CSS 與第 79 輪 J 批部分實作沒有逐行重讀：本輪不補查。

## 正式站台才能確認的事項（不算驗收）

| 事項 | 對應的模擬驗收 |
|---|---|
| 升級後，回望期內已寄過的 NetIQ 重試主機日沒有異常重寄 | B1 升級相容測試 |
| 站台實際使用的瀏覽器（含 Firefox／Safari，若有人用）上，問題查詢「依主機」視角整列可點 | C1 量測斷言（Chromium） |
| 開著回填進度頁超過閒置時限會被登出，重新登入後進度仍在 | F3 收緊情境測試 |
| 真實 PRTG 逾時或認證失敗時，維護頁顯示可處置的原因 | D1 另一側測試 |
| SQL Server 上 `RecordBatch` 改寫後的郵件行為 | B1（SQLite） |

## 規劃完成後複檢

- **第一次（2026-11-23，初版後）**：E3 改成不擋啟動；F3 與 CLAUDE.md 現行措辭的空間列入同步；H 的 NLog 項不在執行期重裝規則；G1 的宣告型別列入驗收。
- **第二次（2026-11-23，四視角檢視後）**，修改如下，其餘無新增事項：
  - C：契約從「排除儲存格＋提示搬到內層」改成 C1／C2 兩條行為契約，補「點文字本身」的落點與原生連結行為。
  - B：回寫入口更正為 `RecordBatch` → `PatchParentAttempt`；紅測試不得使用 `Mark`；升級相容的可達範圍寫明；新增 B3 探針。
  - D1：`PrtgClientException` 維持顯示原因；出口放在既有的 `PrtgFailureText`。
  - F1：判定抽成純函式；F3 補閒置時限預設值與收緊情境驗收，新增 Q8。
  - H：NLog 項改成設定守門（H4）；舊指紋改編號 H3。
  - I：新增說明書同步（I6）與各條驗收。
  - 每個項目編號都有對應的驗收列；全部驗收都落在「怎麼讀這份文件」列出的可用手段內。
- **四個坑**：
  - 什麼算一個／分母為零：F1 的重試次數以當天失敗次數計、跨日歸零；G1 的 null 與 0 分開定義；E4 的上限剛好那一格可讀、總數很小時不顯示說明；C2 只對實際被截斷的格要求提示。
  - 破壞性判準的反例：H2 會讓部分原本保留的值變成空值，已要求以真實樣本驗合法的含冒號帳號，會誤判就不改；H1 的整段遮蔽只在逾時時發生。
  - 單向閘門：B2 把舊項目視為終態之前先查它原本要補救的情境；F1 的當天上限跨日解除。
  - 移除類的依賴方：G2 已列清單與 grep 驗收；E2 搬移 DTO 以路由快照確認。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 回顧盤點 | 6 個唯讀 subagent（Sonnet 5.5）＋主代理（Fable 5.2）複核 | 22 條發現，18 條已核、4 條待核 | 兩個探針（整列連結命中、正規式逾時） | 見各批次「現況與核對結果」 |
| 四視角複檢 | 主代理（Fable 5.2） | 修正 subagent 兩個說法；規劃修改 6 處 | 讀碼：`NetiqSourceAttempt`、`NetiqPipelineService`、`PrtgClient`、`PrtgFailureText`、`UserFacingMessage`、`ReportService`、`rowHref` 全部使用點 | 見「規劃完成後複檢」第二次 |
| A | 主代理（Opus 5.5） | 五處改寫；A2 同一說法另見於 `10-scheduler.md:15`、`01-overview.ai.md:100`、`13-glossary.ai.md:39-40`（後兩處規劃時沒列到，同根因一併改）；`10-scheduler.ai.md` 的「進度條停住」Q&A 隨 A1 改寫 | `HelpContentServiceTests`、`HelpWritingGuide`、`DocNarrativeGuardTests`、`DocCrossReferenceTests` 20 筆通過；兩個字串 grep 零命中；`統計模式` 剩餘命中逐處確認都描述「沒有呼叫 AI」 | 對照：A1→`AnalysisOrchestrator`／`AiAnalysisHostedService`；A2→`record-detail.js aiStatusBadge`、`AiDegradation.ServiceFailedSuffix`；A3→`IssueBulkCloseController` 的 `[Permission(Assign)]`＋`[Permission(Handle)]`；A4→`records.js VIEW_FILTER_SUPPORT`；A5→`IssueExclusion.IsMuted` |

## 體檢交接

（實作完成後填寫）
