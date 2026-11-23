# FEEDBACK 第 80 輪規劃：前五輪回顧修正與說明書 AI 版

> 狀態：規劃中（待使用者確認「待確認事項」後開工）
> 基準：`feature/backlog-help-content-fixes`@a231599e（dev@d9d8ac15 加兩個提交：BACKLOG 記錄與情境驅動器時鐘修正；1.0.79.2；測試基線 Windows 11,998 通過／12 略過、macOS 11,983 通過／27 略過）
> 來源：使用者要求先把第 75～79 輪的修改對照程式碼與規劃文件詳細檢查一遍，有錯誤或問題就併入本輪；本輪原定項目是 `docs/BACKLOG.md`「下一輪預定」的說明書 AI 版 5 處。
> 分支：沿用 `feature/backlog-help-content-fixes`，不另開。

## 怎麼讀這份文件

- **核對狀態**：每條發現標 **〔已核〕**（主代理親自讀碼或實跑探針確認）或 **〔待核〕**（只有 subagent 回報，主代理還沒走完那條路徑）。〔待核〕的項目，該批次開工第一步由主代理確認；不成立就移除，並在執行紀錄記一行。
- **實測事實的寫法**：凡寫「已實測」的，一律附實測環境與實測資料的性質；條件與正式使用不同的標「未實測」。
- **驗收**：全部是主代理在開發環境能執行並判定的（macOS `dotnet test` 定向與全量、Windows 11 VM 全量、`WebApplicationFactory`、UI 量測工具、Node 前端行為測試、`git grep`）。需要正式站台的另列文末。

## 回顧方式與核對程度

- 六個唯讀 subagent（Sonnet 5.5）各負責一段：第 75、76（第二階段）、77、78 輪各一，第 79 輪依批次拆成兩半（P／A／B／C／G／L 與 E／J／F／K／D／H／M／N／I）。每個都讀該輪規劃文件的定案、驗收對照、體檢輪修正，再以**目前 HEAD 的檔案內容**核對，不重報已修正與 BACKLOG 已記載的項目。全程未建置、未跑測試。
- 回報共 22 條發現，沒有高嚴重度；中 2 條、低到中 2 條，其餘低。主代理逐條讀碼，並做了兩個探針（見批次 C、H）。22 條中 18 條的程式事實〔已核〕（其中數條的觸發情境仍標待核）、4 條〔待核〕。
- 各輪核對後**沒有問題**的重點（subagent 讀碼結論，主代理未逐項重驗）：
  - 第 75 輪：報表分桶邊界、帳齡、再發生率分母為零、可見範圍、含授權維度的快取鍵、授權矩陣對新端點的登錄、快照封存。
  - 第 76 輪：「不再打擾」三處定義一致、取消收回案件授與、派工資格只有一份判定、說明章節權限無繞過路徑；E-TABLE 抽 16 列、G-TABLE 抽 11 列皆與現況相符。
  - 第 77 輪：父指紋在記憶體完整紀錄與資料庫讀回紀錄兩邊同值（逐欄核對精簡投影）、快取與 `SingleFlight`、硬上限、依主機合併、空資料待複查欄。
  - 第 78 輪：批次 0～5 沒有找到「等價不成立」的缺陷，也沒有被第 79 輪回退。
  - 第 79 輪：Cookie 撤銷與 24 小時絕對上限、背景標頭不可偽造延長、CSRF 涵蓋、路由快照、對外連線出口（全 repo 的 `HttpClient` 建構點都經 `OutboundHttp.CreateHandler`）、`ExecuteUpdate`／原生 SQL 寫入點的截斷、NLog 常駐 target 的並行。
- **沒看完的範圍**（如實列出，本輪不補查，除非下方批次 X 的探針指到）：第 75 輪批次 A／B／C 的 CSS、校準匯出 v4；第 79 輪 J1 相容算法的逐行驗算、J3／J5／J7／J9～J13 的實作、`ChatFocusResolver`；SQL Server 實機行為。

## 待確認事項（附建議；未答覆者以建議為「暫定」開工，開工前可推翻）

| # | 事項 | 建議 | 理由 |
|---|---|---|---|
| Q1 | BACKLOG「下一輪預定」的 G4～G12、批次改派入口是否納入本輪 | **不納入，順延** | 本輪是回顧修正，項目多而零散；G4／G5／G9 需要產品取捨與畫面設計，混在一起會讓驗收失焦 |
| Q2 | 是否有站台部署過 1.0.77.0～1.0.77.2（77.2 與 77.3 同一天併入 dev） | 沒有就**不做** H4 | 只有停過那三個版本的站台，升級後才會把部分主機日的 PRTG 腿重設一次 |
| Q3 | 報表兩個沒有前端消費端的欄位（`MaxSeverityPerCell`、`HandlingScopeApplied`）要移除還是用上 | **移除** | 熱度表定案是單色淡階，不呈現嚴重度；`HandlingScopeApplied` 恆為 false，畫面另有判斷 |
| Q4 | Data Protection 金鑰環明文存放：改文件，還是 Windows 加 DPAPI | **只改文件**（備份章節註明排除 `dataprotection\`） | DPAPI 綁機器與帳號，換服務帳號或搬機就全員登出外加舊游標失效；現況的權限限縮已是主要防線 |
| Q5 | Core 寫進執行紀錄／維護主控台的 44 行 `ex.Message` 是否算「技術原文外洩」 | **不納入**，只修存進狀態 DTO 的那兩處（批次 D） | 第 76 輪定案範圍是 Web；主控台是診斷輸出。「serverAdmin 在執行詳情看得到輸出內容」已在 BACKLOG |
| Q6 | PRTG 評估預覽的翻頁上限 30,000：放寬，還是到上限時停用「下一頁」並提示縮小篩選 | **到上限時停用並提示** | 上限是用來擋深分頁成本；放寬只是把邊界往後推 |
| Q7 | 超長帳號標籤「截斷後保留」是否收緊成「值內出現下一個標籤樣貌就不保留」 | **收緊**（暫定，開工時以真實樣本驗） | 第 79 輪 F6 的本意是保住超長的真帳號，不是把缺邊界的整段訊息當帳號 |

## 批次總覽

實作方式：主代理寫階段規格並獨立驗收，一次只派一個會改檔的 subagent。跨多檔或涉及判定邏輯用 Sonnet 5.5，機械性替換用 Haiku 5.5。批次 B（郵件去重鍵）由主代理自己做。

| 批次 | 內容 | 來源 | 規模 | 相依 |
|---|---|---|---|---|
| A | 說明書 AI 版 5 處與使用者版同句 | BACKLOG | 小 | 無 |
| B | 郵件去重鍵含 NetIQ 嘗試欄位、重試入口空轉 | 第 78 輪 | 中 | 無 |
| C | 整列連結的點擊區被截斷欄破壞 | 第 79 輪 | 小 | 無 |
| D | 技術原文出口：狀態欄位與守門範圍 | 第 76、79 輪 | 中 | 無 |
| E | 輸入驗證：中文訊息、涵蓋守門、設定上限、翻頁上限 | 第 77、79 輪 | 中 | 無 |
| F | 排程與時鐘：保留清除重試、逾期口徑、背景輪詢 | 第 76、79 輪 | 中 | 無 |
| G | 報表與 PRTG 判定 | 第 75 輪 | 小 | Q3 |
| H | 防禦缺口：正規式逾時、帳號截斷、NLog 設定更換、舊指紋 | 第 77、79 輪 | 小 | Q2、Q7 |
| X | 回顧留下的四個探針（成立才修） | 各輪 | 小 | 無 |
| I | 文件同步 | 第 76、78、79 輪 | 小 | B、D |
| Z | 終檢、全量、交接 | — | 中 | 全部 |

順序：A → C → B → D → E → F → G → H → X → I → Z。A 與 C 先做：前者是原定項目，後者是使用者每天會碰到的操作。

版號：開輪 1.0.80.0（本文件的提交）。

### 全輪共同契約（每個階段規格與驗收都要逐條抄進去）

- **先寫會紅的測試。** 每條缺陷修正先寫出在未修正的建置上會紅的測試並確認它紅，再修。版面缺陷先寫會紅的量測斷言。
- **邊界兩側各一例。** 有門檻、上限、分隔規則的契約，驗收要有剛好通過與剛好不通過的案例；主代理每階段至少做一個突變，突變用檔案備份還原、`cmp` 確認並更新時間戳，不用 `git checkout`。
- **守門先拿反例試。** 新的守門或「搜尋零命中」類驗收，先放一個該命中的寫法確認它會紅。
- **收緊類改動寫出「原本可用、收緊後被擋」的情境**，並驗被擋時使用者看到什麼。
- **既有資料不能因為規則變嚴而讀不出來。**
- **不新增沒有消費端的設定。** 本輪不新增設定。
- **移除時交代依賴方。** 刪掉的欄位、匯出、測試列成清單，以 `git grep` 零命中驗收。
- **定向測試篩選詞開工時實跑**，確認命中數大於 0 且含該階段新增的測試。改前端至少含 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`；改現行文件加跑 `DocNarrativeGuardTests`；改說明書加跑 `HelpContentServiceTests|HelpWritingGuide`；新增或改 `*.behavior.mjs` 加跑 `NodeRuntimeTests`。
- **commit 寫成「測試通過才執行」的條件式指令**；每批驗收通過後在本輪分支 commit 並 push，確認遠端指向該提交。

## 批次 A：說明書 AI 版 5 處與使用者版同句

### 現況與核對結果

說明書問答的 AI 讀 `LogForesight.Web/HelpContent/*.ai.md`。以下原文都還在〔已核，2026-11-23 讀檔〕：

1. `10-scheduler.ai.md:57` 流程圖寫本機主機「同步呼叫 AI」；`:101` 寫「本機分析的 AI 段會排隊等待，此時本機進度條短暫停留屬正常現象」。同檔 `:100` 的說法正確（先寫統計、AI 事後補寫）。程式事實：`AnalysisOrchestrator` 呼叫 `AnalyzeDayStatisticalAsync` 並標 `AiPending`，AI 只由 `AiAnalysisHostedService` 的 `RetryAiAsync` 補寫。
2. `10-scheduler.ai.md:67` 寫 AI 例外或逾時時主機日標成「統計模式（AI 未分析）」。程式事實：主機日維持待補，標題加 `AiDegradation.ServiceFailedSuffix`，徽章顯示「AI 分析中」；「統計模式（AI 未分析）」只在 AI 沒有被呼叫時出現（`record-detail.js` 的 `aiStatusBadge`）。
   **同句的使用者版**：`10-scheduler.md:15`「AI 出錯時，那天改用統計模式記錄」。BACKLOG 記錄時沒列到這一句。
3. `19-alert-tools.ai.md:12`「具 `Maintain`（統一標記為 `Assign` 且 `Handle`）」：統一標記只需要 `Assign`＋`Handle`。
4. `03-issues.ai.md:31`「依主機、依日期這三個都不支援」：「三個」指處理狀態、未指派、逾期三種篩選。
5. `19-alert-tools.ai.md:39`「靜音只影響之後的判定與畫面列出」：分析端只把區間內的日子標 `Suppressed`；畫面與派工端在問題目前靜音中時，連靜音前的日子也一併隱藏（`IssueExclusion.IsMuted`）。

已查證不必改（BACKLOG 記載）：「尚未有人接手」與「沒有進行中案件」同義；10 分鐘續跑與 60 分鐘空資料複查是兩套機制。

### 定案

- 五處改成與程式事實一致、只有一種讀法的句子。第 2 處的使用者版同句一併修，並 `grep` 其餘章節有沒有同一個說法（`14-faq.ai.md` Q4、`04-record-detail*.md`、`13-glossary.md` 的「統計模式」描述目前是「沒設定 AI」的情境，說法正確，不動）。
- 開工第一步重讀對應程式碼路徑確認事實仍成立（基準已從 d9d8ac15 往前兩個提交，皆未動這幾條路徑）。
- 使用者版照 `docs/HELP-WRITING-GUIDE.md`（句長、用詞、給非 IT 人員）；AI 版每句只講一件事。
- 完成後從 BACKLOG「下一輪預定」移除這一項。

### 驗收

- `dotnet test --filter "FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~HelpWritingGuide"` 通過（開工時先確認命中數大於 0）。
- `git grep -n "同步呼叫 AI" -- LogForesight.Web/HelpContent` 零命中；`git grep -n "這三個都不支援"` 零命中。
- 五處各列「改前句／改後句／對應程式位置」對照表進執行紀錄，主代理逐句對程式碼。

## 批次 B：郵件去重鍵與重試入口

### 現況與核對結果

- **B1 郵件內容指紋仍含 NetIQ 嘗試狀態與時間。**
  - 〔已核〕`HostDayWorkflowState.cs:1324` `ForMailContent` 只把 AI 已分析、AI 待補、詳情已清除三個旗標固定為 false；它呼叫的 `ForRecordWithIdentity`（同檔 `:1341` 起）仍序列化 `LatestNetiqAttemptStatus` 與 `LatestNetiqAttemptAtUtc`。
  - 〔已核〕同檔 `:1263` `ForParentRecord` 的註解寫明「批次結束時帳本會對非成功的主機日回寫這兩欄」，所以父指紋已把它們排除（第 79 輪 J1）。郵件指紋沒有跟著改。
  - 〔待核〕回寫入口 `NetiqSourceAttempt.Mark` 與寄信的先後：需確認「已寄出 → 之後 `Mark` 改寫 → 下一趟 `NotifyAfterRunAsync` 重算」這條序列確實走得到，以及 `LegacyKeySent` 認不得新鍵。
- **B2 重試入口對一類終態項目空轉。**〔已核條件存在；空轉行為待核〕`MailNotificationService.cs:370` 的觸發條件含 `IsRetryableLegacyNoQualifiedIntent`（狀態 `no-qualified-recipient` 且收件人仍是 `failed-or-unknown`／`sending-result-unknown`）。這類項目屬終態，永遠不會有進度，存在期間每 30 分鐘（退避上限）整套通知重跑一次。`docs/WEB-SPEC.md:2680` 只寫「非終態且尚未記為已寄」。

### 根因

第 78 輪批次 0 的定案是「指紋不含與信件內容無關的狀態」，列舉時只列了三個 AI 旗標。帳本回寫是第 79 輪才確認的寫入入口，修父指紋時沒有回頭看同一份欄位的另一個指紋。

### 定案

- **B1**：`ForMailContent` 排除這兩個嘗試欄位，與 `ForParentRecord` 同口徑。升級相容：`LegacyKeySent`／舊鍵對照要認得「含嘗試欄位」的舊鍵（與現有三旗標組合相乘），否則升級當下回望期內已寄過的主機日會重寄一次。註解標明可移除的時點（升級滿 14 天回望期）。
- **B1 的同型普查**：`grep` `ForRecordWithIdentity`、`ForRecord`、`ForReportInput` 的全部呼叫端，逐一判斷嘗試欄位進指紋是否合理（報表輸入指紋若會因帳本回寫而讓報告重產，同屬本條）。結果列表進執行紀錄。
- **B2**：這類舊項目視為終態，不再觸發重試；`MailNotifyStateStore.IsTerminalUrgentStatus` 仍是唯一判定點，不在重試入口另寫一份清單。開工時先讀 `IsRetryableLegacyNoQualifiedIntent` 的來歷（它是為了哪個升級情境留的），若仍有「該補寄」的情境就改成有次數或期限的出口，並回寫本文件。

### 影響面

- 去重鍵改變會同時影響已寄鍵、待送項目的鍵與 `SupersedeUrgentOutbox`；三處用同一個函式，改一處即可，但相容路徑三處都要驗。
- CLAUDE.md「不要讓郵件去重鍵吃到與信件內容無關的狀態」條目要補上嘗試欄位（批次 I）。

### 驗收

- 先寫紅測試：已寄主機日 → 以 `NetiqSourceAttempt.Mark` 改寫嘗試狀態與時間（問題清單不變）→ 再跑 `NotifyAfterRunAsync`，斷言沒有新增寄信、待送項目沒有換鍵。未修正時必須紅；若寫不紅，表示 B1 的序列走不到，改列為「防禦性對齊」並在執行紀錄說明。
- 邊界另一側：嘗試欄位不變、問題清單改變 → 必須重寄（鍵有變）。
- 升級相容：以舊算法（含嘗試欄位）預先寫入已寄鍵，升級後同內容不重寄。另加一條不經相容路徑的斷言（新算法寫入、嘗試欄位改變、不重寄），避免被相容路徑遮蔽。
- B2：造一筆 `no-qualified-recipient`＋`failed-or-unknown` 的舊項目，以固定時鐘推進 2 小時，斷言整套通知重跑次數為 0（或為定案的有限次數）。
- 突變：把嘗試欄位加回 `ForMailContent`，第一條測試轉紅。
- 定向：`dotnet test --filter "FullyQualifiedName~MailFingerprintTests|FullyQualifiedName~MailNotificationServiceTests|FullyQualifiedName~MailNotify"`（開工時實跑確認命中）。

## 批次 C：整列連結的點擊區

### 現況與核對結果

〔已核，實測〕`wwwroot/css/components.css:1147`：

```css
.lf-row-link :where(a:not(.lf-row-link__anchor), button, input, select, textarea, label, summary, [data-bs-toggle], [title]) { position: relative; z-index: 2; }
```

`[title]` 會選到帶 `title` 的 `<td>`；`core/ui.js:857` 的截斷欄（`truncate: true`）正是把 `title` 設在 `<td>` 上。

- **實測**：事實＝連結放在截斷欄那一格時，同列其他格的中心點 `elementFromPoint` 回傳 `TD` 而不是連結；連結放在別格時，截斷欄那一格回傳 `TD`；沒有 `title` 的對照列每格都回傳連結。環境＝macOS、Claude 桌面版內建瀏覽器（Chromium），視窗 1000×600。資料性質＝只含三列的最小靜態頁，載入專案**實際的** `components.css`，沒有載入其他三層樣式與 Bootstrap。完整站台與其他瀏覽器**未實測**。
- 影響的頁面〔待核，subagent 列舉〕：問題查詢「依主機」視角（`records.js` 約 738～754，首欄主機 `truncate`）、使用者詳情的主機表（`user-detail.js` 約 222～236）整列只剩第一格可點；問題查詢「明細」視角（約 605～621）的主機格點不到。游標仍是 pointer。
- 既有測試沒抓到：`SharedExitGuardUiTests` 只做字串斷言（而且把 `[title]` 這個選取器本身釘住）；六項量測不量點擊命中。

### 根因

「讓列內帶提示的小元素浮在連結之上」的規則用屬性選取，沒有排除儲存格本身。

### 定案

- 抬升規則排除儲存格：`[title]:not(td, th)`（寫成排除，不事後覆寫）。截斷欄的完整文字提示仍由 `<td title>` 提供——連結的延伸區蓋在上面時，滑鼠停在該格看到的是連結而不是 `td` 的提示，所以 `ui.js` 同時把提示改掛在格內的 `span`（該 `span` 會被規則抬起，提示可見，點它不導向；這與其他帶提示的小元素行為一致）。若量測顯示 `span` 佔滿整格讓該格點不到，改成只在文字實際被截斷時才加 `title`。兩種作法擇一，以量測結果定，寫回本文件。
- `attachRowLink` 的全部使用頁面列清單（`grep rowHref`），每頁都進量測。
- 補一項全站量測斷言進 `LogForesight.Tests/tools/ui-global-asserts.json`：每個 `tr.lf-row-link` 的每一格，中心點命中的元素必須是該列的連結，或是該格內的互動元素／帶提示的小元素。

### 驗收

- 新量測斷言在**未修正**的建置先跑，確認「依主機」視角與使用者詳情主機表為紅；修正後五個寬度下為 0。
- 該頁全部既有量測情境重跑，六項量測維持 0。
- 截斷欄的完整文字提示仍取得到（斷言被截斷的主機格存在帶完整文字的 `title`，位置依定案）。
- `git grep -n "\[title\]" -- LogForesight.Web/wwwroot/js LogForesight.Tests` 逐一看有沒有其他模組或測試依賴 `td[title]`（`querySelector('td[title]')` 之類）。
- `SharedExitGuardUiTests` 的字串斷言同步；定向 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`。

## 批次 D：技術原文出口

### 現況與核對結果

- **D1 結構同步把例外原文存進狀態欄位。**〔已核〕`LogForesight.Core/Service/PrtgStructureSyncRunner.cs:119` `status.ErrorMessage = ex.Message;`；`:74`、`:142` `mapError = ex.Message` → `status.ErrorMessage = $"主機對應失敗：{mapError}"`。經 `PrtgStructureSyncService.cs:243` `LastErrorMessage` 回給維護頁顯示。EF／SQL／HttpClient 的例外原文含伺服器名、路徑、位址。這不是主控台診斷輸出，是持久化後回給畫面的 DTO 欄位，屬 CLAUDE.md 的紅線。
- **D2 守門只認固定變數名、只掃 Web。**〔已核〕`UserFacingMessageGuardTests.cs:17` 的 `MessageUse` 只比對 `ex|exception|*Ex|workflowError|error|…` 這些識別字加 `.Message`；`e.Message`、`inner.Message`、`ex.InnerException.Message`、`ex.GetBaseException().Message` 都不命中。掃描範圍只有 `LogForesight.Web`，所以 D1 沒被看到。目前 Web 端零洩漏〔subagent 以較寬的 grep 核對，僅兩處事件列的 `.Message` 屬性〕。
- **D3 遷移失敗訊息的驗收只測純函式。**〔待核〕第 79 輪 B4 的驗收原定「讓遷移擲帶標記的例外：閘門回應、健康頁、告示都不含標記」；實作的 `ErrorOutputBoundaryTests` 只測 `MigrationFailureText.For(...)` 與 middleware 一層。遷移器若把 `s.LastError = MigrationFailureText.For(ex)` 改回 `ex.Message`，`MigrationGateMiddleware` 的「（原因：…）」又會帶原文，測試仍綠。

### 定案

- **D1**：狀態欄位寫固定說明加例外型別名；原文只進主控台輸出與記錄檔。Core 沒有 `UserFacingMessage`（在 Web），所以做法是 Runner 接一個把例外轉成說明的委派，或在 Core 放一個只回「型別名＋固定句」的小出口——擇一由實作時依相依方向決定，不得讓 Core 參考 Web。已存在資料庫的舊 `ErrorMessage` 不回頭清（下次同步就會覆寫）。
- **D2**：守門改成「任何成員存取鏈結尾是 `.Message`」都算命中，再以型別白名單排除非例外的 `.Message`（事件列等），並補反例：`e.Message`、`ex.InnerException.Message`、`ex.GetBaseException().Message`。掃描範圍加 Core 中「寫進會回給畫面的狀態／DTO」的賦值：以 `= …\.Message` 賦值給屬性為判準，主控台 `WriteLine` 與 `Log.` 不算（依 Q5）。
- **D3**：補整合斷言——三個遷移器各注入會擲帶標記例外的 store，斷言 `State.LastError`、健康頁原因、`MigrationGateMiddleware` 回應都不含標記，記錄檔含標記。

### 驗收

- D1 先寫紅測試：結構同步注入擲 `InvalidOperationException("MARKER-Server=sql01;path=/x")` 的來源，斷言 `status` 與 `GET prtg-structure-sync/status` 的 `lastErrorMessage` 不含 `MARKER`，主控台輸出含。對應失敗那條路徑另一例。
- D2：守門在目前 HEAD 加入反例時必須紅；把 D1 修正還原時守門必須紅（突變）。白名單逐條附理由。
- D3：把任一遷移器改回 `ex.Message`，新斷言轉紅（突變）。
- 定向：`UserFacingMessageGuardTests|ErrorOutputBoundaryTests|PrtgStructureSync|MigrationTests`。

## 批次 E：輸入驗證

### 現況與核對結果

- **E1 驗證屬性沒有中文訊息。**〔已核數量；訊息字樣待核〕`LogForesight.Web/Models` 下 135 個 `[StringLength]`／`[MaxLength]`／`[MinLength]`／`[Range]`／`[EmailAddress]`／`[RegularExpression]`／`[Length]`，其中 67 個沒有 `ErrorMessage`。`ChineseModelBindingMessages` 只處理繫結訊息與沒寫訊息的 `[Required]`。違規時信封訊息預期是框架英文原文加 C# 屬性名。
- **E2 預設輸入上限的涵蓋有缺口。**〔已核〕`DefaultInputLimitsFilter.cs:56` `IsRequestDto` 只認命名空間以 `LogForesight.Web.Models` 開頭的型別。`PrtgTransferCreateRequestDto` 定義在 `Controllers/Api/PrtgTransferController.cs`，在範圍外〔subagent 回報它是唯一一個，待核〕。沒有守門保證每個 `[FromBody]` 型別都在範圍內。
- **E3 `Jwt:ExpireHours` 沒有上限。**〔已核〕`AppSettings.cs:67` 只檢查 `<= 0`。絕對上限固定 24 小時，大於 24 的值沒有意義；極大值在簽發票證或 `TimeSpan` 換算時溢位〔溢位的例外型別未實測〕。
- **E4 PRTG 評估預覽翻頁上限。**〔已核〕`ScaleLimits.MaxPreviewOffset = 30_000`，`PrtgMonitoringController.cs:325` 超過回 400。預覽的列是感測器；3,000 台、每台超過約 10 顆時總數就超過 30,100，最後幾頁翻不到，畫面沒有說明。

### 定案

- **E1**：以一個 metadata provider 為沒寫 `ErrorMessage` 的這幾類屬性補中文預設訊息（作法比照既有的 `ImplicitRequiredMessageProvider`），訊息用欄位的顯示名稱而不是 C# 屬性名；沒有顯示名稱的欄位先補 `[Display]` 或在 provider 內退回通用句（「輸入值超出允許範圍」），不得露出屬性名。不逐一在 67 處手寫訊息。
- **E2**：把該 DTO 移到 `Models`；加反射守門：所有 `[FromBody]` 參數型別必須是 `LogForesight.Web.Models*` 下的類別，直接以集合或字串當 body 的要在守門列白名單並附理由。
- **E3**：啟動檢查加上限 24，訊息為中文並說明「絕對上限固定 24 小時」。收緊情境：既有站台若填了大於 24 的值，升級後**啟動失敗**。這違反「既有設定不能因變嚴而起不來」的精神，所以改成：大於 24 視為 24 並在啟動記錄檔與健康頁出聲，不擋啟動；只有會溢位的極端值才擋。
- **E4**：依 Q6，到上限時前端停用「下一頁」並顯示「已到可瀏覽的上限，請用篩選縮小範圍」；後端 400 的訊息維持。上限剛好那一格（offset 30,000）仍可讀。

### 驗收

- E1：以 `WebApplicationFactory` 對一個 `[Range]`、一個 `[StringLength]` 端點各送超界值，斷言訊息不含 ASCII 的 `The field` 與屬性名，含中文說明。反射測試：`Models` 下所有驗證屬性，沒寫 `ErrorMessage` 的型別都在 provider 的涵蓋表內（先拿一個不在表內的屬性型別當反例確認會紅）。邊界：值剛好等於上限通過、上限加一被擋。
- E2：守門在把 DTO 移回控制器檔時轉紅（突變）；`PackageSha256` 超長被預設上限擋下且訊息為中文。
- E3：`ExpireHours` = 24 通過、25 啟動成功且出聲、0 與負數仍擋。
- E4：`*.behavior.mjs` 案例：總數 30,150、位於 offset 30,000 的頁，「下一頁」停用並有說明；總數 30,050、offset 29,900 的頁「下一頁」可按。新增案例同步登錄 C# `InlineData`，加跑 `NodeRuntimeTests`。
- 定向：`InputValidationTests|DefaultInputLimits|ScaleLimits|Authorization|ControllerRouteSnapshot` 加前端四類。

## 批次 F：排程與時鐘

### 現況與核對結果

- **F1 單獨保留清除失敗仍記成今天已完成。**〔已核〕`SchedulerHostedService.cs:678-679`：`await _orchestrator.RunRetentionOnlyAsync(...)` 之後無條件 `_retentionConfirmedDate = now.Date`。`AnalysisOrchestrator.RunRetentionOnlyAsync`（`:921`）回 `Task`，`RetentionPruner.Run` 的結果被丟掉。清除器自己的註解寫「失敗時不記錄今日已執行，留待下次重試」。失敗要等隔天或站台重啟才重試；健康告示仍會出聲。
- **F2 「處理中逾期」直接讀系統時鐘。**〔已核〕`IssueTodoQuery.cs:121` `var today = DateTime.Today;`，是儀表板「逾期」與每日摘要（`MailIssueDigest`）的共用口徑。交辦單到期判定用注入的 `TimeProvider`，兩邊對同一張單可能用到不同的「今天」。`HandoffClockGuardTests` 的範圍不含這兩個檔。
- **F3 作業進度輪詢沒有標背景。**〔已核輪詢點；各回呼內容待核〕`pollWhile` 的呼叫點：`prtg-admin.js:407`（回填，2 秒）、`:2645`（探測）、`:3020`（結構同步）、`netiq.js:433`、`netiq-import-wizard.js:331`，這三個檔案沒有任何 `background: true`；同類的 `runs.js:1326`、`:1880` 有標。守門 `BackgroundPollingHeaderGuardTests` 只列三個固定端點。結果：開著跑數小時的回填頁離開座位，閒置登出不會發生，直到 24 小時絕對上限。

### 定案

- **F1**：`RunRetentionOnlyAsync` 回傳清除結果；失敗時不設 `_retentionConfirmedDate`，改記「下次可重試時間」（暫定 30 分鐘後），避免每分鐘輪詢都重跑整輪。重試上限暫定當天 3 次，之後等隔天。不新增設定。
- **F2**：`IsOverdueInProgress` 的「今天」改由呼叫端傳入，`IssueTodoQuery` 與 `MailIssueDigest` 取自注入的 `TimeProvider`；兩個檔案加進 `HandoffClockGuardTests` 的範圍。先 `grep` `IsOverdueInProgress` 的全部呼叫端。
- **F3**：規則定為「頁面開著就會持續送、不需要使用者再操作的輪詢，一律標背景」，作業進度輪詢屬此類。五個呼叫點補標；守門改成掃 `pages/*.js` 與 `core/*.js` 的每個 `pollWhile(`，回呼直接或經由具名函式發出的 `api.get` 必須帶 `background: true`，例外在守門列白名單並附理由（純本地計時如 `runs.js:1416` 不發請求，不適用）。
  收緊情境：管理者盯著回填進度不操作，超過閒置時限會被登出、進度頁轉到登入頁；作業本身在伺服器端繼續。驗收要確認登出後重新登入能看到同一個作業的進度。

### 驗收

- F1：以替身讓清除某一段擲例外，固定時鐘：第一次輪詢失敗後 `_retentionConfirmedDate` 未設；29 分鐘內的輪詢不重跑、30 分鐘後重跑；第 3 次失敗後當天不再跑；隔天重新計。成功那一側：成功後當天不再跑。`RunRetentionIfDueAsync` 是私有且依中午門檻，需抽出可測的判定函式或以 `TimeProvider` 驅動，作法寫進階段規格。
- F2：情境測試把時鐘撥到預計完成日的隔天，儀表板逾期數與交辦單逾期判定一致；撥回前一天兩邊都不逾期。守門拿 `DateTime.Today` 反例確認會紅。
- F3：守門在目前 HEAD（未補標）必須紅且列出五個點；補標後綠。行為測試：背景請求不延長閒置時限（既有 `CookieAuthenticationSiteTests` 類別的同型案例）。
- 定向：`RetentionPrunerTests|HandoffClockGuardTests|IssueTodoQuery|MailIssueDigest|BackgroundPollingHeaderGuardTests|PollingUiTests|CookieAuthentication` 加前端四類。

## 批次 G：報表與 PRTG 判定

### 現況與核對結果

- **G1 前期趨勢跨保留線時補 0。**〔已核〕`ReportService.cs:126-130`：只有整段超出保留期才回空陣列；部分超出時仍呼叫 `BuildTrendFromAggregate`，同檔 `:283-290` 對沒有資料的日子補 `HighRisk = 0`。前端把 0 畫成線，看起來像前期完全沒有高風險日。畫面上方有 `comparisonPartiallyOutOfRetention` 的警示，所以是誤導不是靜默錯。
- **G2 兩個欄位沒有前端消費端。**〔已核〕`git grep -i` 在 `LogForesight.Web`、`LogForesight.Core`：`MaxSeverityPerCell` 只在 DTO 與 `ReportCategoryTrendService.cs:112`；`HandlingScopeApplied` 只在 DTO 與 `ReportEffectivenessService.cs:75`（恆為 false）。前端零命中。
- **G3 持平降級對 Low 仍標註並計數。**〔已核程式；情境待核〕`PrtgCrossDay.cs` 降級分支：`switch` 對 Low 回原值，之後無條件 `AppendDetail(…數值持平…)`、`steadyDowngraded++`、`continue`（跳過跨日升級）。規則嚴重度可在畫面編輯，所以 Low 的 warning 規則可以存在；是否有預設為 Low 的要看 `PrtgRuleCatalog`。

### 定案

- **G1**：早於保留線的日子輸出「沒有資料」而不是 0。作法：前期趨勢點的高／中風險數改可空，早於保留線的日子為 null，前端 null 不畫點（`reports.js` 已有 `?? null` 的處理）。宣告型別要與實際回傳型別一致，補序列化斷言。保留線當天算有資料。
- **G2**：依 Q3 移除兩個欄位。依賴方清單：DTO 屬性、兩個服務的賦值、測試斷言、`reports-views.behavior.mjs` 的夾具、WEB-SPEC 的端點欄位說明。`git grep -i "maxSeverityPerCell\|handlingScopeApplied"` 全 repo（含 docs 主目錄，不含 archive）零命中。
- **G3**：只有嚴重度實際改變時才標註、計數並跳過跨日升級；Low 走原本的跨日流程。

### 驗收

- G1：比較期起點早於保留線 3 天的案例：前 3 天為 null、保留線當天起為數字（含真正的 0）；整段超出仍回空陣列；完全在保留期內沒有 null。前端行為測試：null 的點不出現在資料序列。
- G2：上述 grep 零命中；`ReportServiceTests` 與報表行為測試通過。
- G3：Low 的 warning 簽章、連續 7 日、證據持平：嚴重度仍為 Low、說明不含「數值持平」、降級計數為 0、跨日升級照常套用。Medium 那一側維持降一級（既有測試）。
- 定向：`ReportService|ReportCategoryTrend|ReportEffectiveness|PrtgCrossDay` 加前端四類與 `NodeRuntimeTests`。

## 批次 H：防禦缺口

### 現況與核對結果

- **H1 網址遮罩的正規式逾時沒有人接。**〔已核，實測〕`UrlSecrets` 的 `UserInfo` 與 `QueryParam` 都帶 1 秒逾時，`Mask`／`ContainsSecret` 沒有處理 `RegexMatchTimeoutException`；正式碼有 11 處呼叫，其中執行紀錄去識別化會拿它掃整段自由文字。
  - **實測**：事實＝`"http://" + "a:" 重複 50,000 次` 在 1,005 毫秒擲 `RegexMatchTimeoutException`；重複 5,000 次需 634 毫秒但不擲；20 萬字只含一個冒號的 base64 樣字串 13 毫秒；500 個各 2,000 字的網址 81 毫秒；一般網址 0 毫秒。環境＝macOS、.NET SDK 10.0.404、Release、獨立主控台程式（把兩個正規式原樣複製出來，不是呼叫專案的 `UrlSecrets`）。資料性質＝人工構造的最壞輸入（scheme 之後連續不含空白與 `@`、帶大量冒號）。真實日誌出現這種形狀的機率**未實測**；Windows 上的耗時**未實測**。
- **H2 超長帳號標籤截斷後保留。**〔待核〕`LogAggregator.cs` 的 `ReadLabelValue`：欄位邊界只認換行、tab、連續兩個以上空白；`keepOverlong`（`:313`、`:399`，帳號標籤）時超過 128 字改成截到 128 後保留。訊息以單一空白分隔時，整段後續欄位會成為一個 128 字的「帳號」，進帳號集合與登入失敗計數。
- **H3 NLog 設定在執行中途被換掉。**〔待核〕`RunLogCapture.EnsureInstalled` 只在啟動與 `BatchRunRecorder` 建構時檢查；執行中 `LogManager.Configuration` 被整份換掉時，該趟的警告計數靜默歸零。目前 `nlog.config` 沒有 `autoReload`，實際觸發機率低。
- **H4 舊父指紋缺 1.0.77.0～77.2 的算法。**〔待核〕`LegacyParentFingerprints` 的兩個舊算法都以目前的精簡投影（含已抑制告警）計算；77.0～77.2 的精簡投影不含已抑制告警。停過那三個版本的站台，帶已抑制告警的中風險以上主機日升級後會被當成父紀錄改變，PRTG 腿重設一次。

### 定案

- **H1**：逾時時走保守路徑——`ContainsSecret` 回 true（當成有機敏內容），`Mask` 回整段遮蔽的固定字串並記一行警告（不含原文）。不放寬逾時。另把 `UserInfo` 的式子改成不會平方級回溯的寫法（使用者資訊段以「到最後一個 `@`」的方式比對），兩者都做：式子修好是根因，接例外是防線。
- **H2**：依 Q7（暫定）：截斷保留只在值內沒有「下一個標籤樣貌」（`\w[\w ]{0,30}:\s`）時套用，否則維持回空值。開工時用 `LogForesight.Tests` 既有的真實樣本訊息確認不會把合法的超長帳號（含冒號的 UPN 之類）誤判；會誤判就改成「截斷保留並另記計數」，回寫本文件。
- **H3**：`BatchRunRecorder.Finish` 時再確認一次常駐規則仍在；不在就把該趟標記「記錄擷取中途中斷」並寫一行警告，不嘗試在執行期重裝（CLAUDE.md：不得在執行期增減 target 或規則——`EnsureInstalled` 既有的重裝行為維持在建構時點）。
- **H4**：依 Q2。有站台停過那三個版本才做：加第三個舊算法（精簡投影去掉已抑制告警＋嘗試欄位）。沒有就不做，在 `LegacyParentFingerprints` 註解補一句涵蓋範圍。

### 驗收

- H1：上述五組輸入寫成測試；最壞輸入不擲例外、回遮蔽字串，且（式子修好後）耗時小於 200 毫秒。式子改寫的等價性：既有 `UrlSecrets` 測試全綠，另補「密碼含 `@`」「只有帳號沒有密碼不遮」「多個網址」三例。突變：拿掉例外處理且還原式子，最壞輸入測試轉紅。
- H2：單一空白分隔的訊息 → 帳號為空；128 字純帳號保留、129 字純帳號截到 128 保留（既有）；含冒號的合法帳號樣本維持原結果。
- H3：執行中把 `LogManager.Configuration` 換成新物件後 `Finish`：該趟有「中途中斷」標記與一行警告；未更換時沒有。測試結束還原全域設定。
- H4（若做）：以 77.2 的算法預先寫入指紋，升級後不重設 PRTG 腿；另一條不經相容路徑的斷言。
- 定向：`UrlSecret|LogAggregator|RunLog|BatchRunRecorder|WorkflowFingerprint|HostDayWorkflow`（開工時實跑確認各詞命中；目前沒有名為 `UrlSecretsTests` 的類別，先 `git grep -l "UrlSecrets" -- LogForesight.Tests` 找到實際類別名再寫進篩選）。

## 批次 X：回顧留下的四個探針

subagent 標為「沒把握」、值得花一個探針的項目。**成立才修，不成立在執行紀錄記一行**；修法超出小改就回報使用者，不在本批硬做。

| # | 疑點 | 探針 |
|---|---|---|
| X1 | 帶有效 cookie 時資料庫讀不到：`ValidatePrincipal` 擲例外，`/error` 重新執行管線又擲一次，可能變成空白 500 | 測試站台注入會擲例外的使用者來源，帶有效 cookie 請求頁面與 `/api/health`，看回應內容 |
| X2 | `CsvParser` 行號在有空行或跨行引號欄位時是否指到正確的列 | 標題列＋空行＋一筆資料；跨行引號欄位後的下一列；比對 `LineNumber` |
| X3 | PRTG 快照手動封存時，記憶體中尚未落盤的樣本是否被丟掉且確認對話少報筆數 | 造一個因來源變更而停止、仍有累積樣本的狀態，比對封存前後筆數 |
| X4 | 報表熱度格、分桶堆疊條下鑽後的筆數是否與格內數字一致 | UI 資料集點一格，比對下鑽清單筆數與格內數字的定義（單位是主機日還是問題） |

## 批次 I：文件同步

- `docs/WEB-SPEC.md:249`、`:292`、`:295`〔已核〕仍描述已移除的 serverAdmin 連續失敗鎖定與失敗計數；程式只剩登入節流（429「登入嘗試次數過多」）。改成現況並指向 `LoginThrottle`。
- `docs/WEB-SPEC.md:2680` 重試退避的觸發條件，依批次 B2 的定案改寫。
- `docs/WEB-SPEC.md` §9.4b 管理者提醒段的「收件人是目前操作者時不入列」〔待核〕：這條過濾只在交辦通知，管理者提醒由夜間排程呼叫、沒有操作者。改寫成不會被讀成適用於管理者提醒。
- README 備份章節（`:111`、`:667`、`:766` 附近）：依 Q4 註明備份資料目錄時排除 `dataprotection\`，並說明原因（備份副本帶走金鑰）。
- CLAUDE.md：郵件去重鍵條目補上嘗試欄位；背景輪詢條目補上「作業進度輪詢也算」；`UserFacingMessage` 條目補上 Core 的狀態欄位。
- WEB-SPEC 報表端點欄位說明隨 G2 移除兩個欄位；PRTG 評估預覽補翻頁上限的行為（E4）。
- 現行文件只寫現況，定稿前掃敘事字眼；加跑 `DocNarrativeGuardTests|DocCrossReferenceTests`。

## 批次 Z：終檢、全量、交接

- 逐條對照本文件定案與實作（每條定案都要能指到程式位置與驗收測試）。
- 兩次彼此獨立的全 diff 審查（程式、文件各一）。
- 改到的頁面（問題查詢、使用者詳情、報表、PRTG 維護、NetIQ 維護）跑 UI 量測：五個寬度六項量測為 0，加批次 C 的新斷言。
- 全量：macOS 與 Windows 11 VM 各一次，結果與基線差異寫進「體檢交接」。
- BACKLOG：移除已完成的說明書項目；本輪「不做」與探針不成立的項目視需要補記。
- 換模型體檢與收尾依專案慣例（`docs/archive/` 歸檔、索引、現行文件只留結論）。

## 明確不做（本輪定案，Q1／Q5 以建議為暫定）

- BACKLOG「下一輪預定」的 G4～G12 與批次改派入口：順延。
- Core 寫進主控台與執行紀錄的 `ex.Message`：不視為外洩，不改。
- Data Protection 金鑰環加密存放：只改文件。
- 回顧中列為觀察、不值得一個探針的項目：救援帳號密碼雜湊改回舊值時舊 cookie 恢復有效（需 24 小時內改兩次）；`ChannelPolicy.IsInformationalAllowed` 以子字串比對提供者名（沒有證據顯示真實環境有撞名的提供者）；`ColumnClamp.ClampFor` 對沒有長度上限的屬性原樣回傳（目前所有呼叫點都有上限）；`WorkOrderOutboxItem` 行內切字串（BACKLOG 既有條目涵蓋）；閒置時限的實際語意是時限的一半到全長之間（文件已寫續發規則）。
- 第 75 輪批次 A／B／C 的 CSS 與第 79 輪 J 批部分實作沒有逐行重讀：本輪不補查。

## 正式站台才能確認的事項（不算驗收）

- 批次 B：正式站台升級後，回望期內已寄過的 NetIQ 重試主機日沒有重寄。
- 批次 C：站台實際使用的瀏覽器上，問題查詢「依主機」視角整列可點。
- 批次 F3：開著回填進度頁超過閒置時限會被登出，重新登入後進度仍在。
- SQL Server 實機行為與第 79 輪文末清單維持原樣，不在本輪。

## 規劃完成後複檢（2026-11-23）

- **與既有設計的衝突**：
  - 批次 F3 把作業進度輪詢標成背景，推翻 CLAUDE.md 現行措辭中可被讀成「使用者啟動的作業進度不算」的空間；已在批次 I 列入同步，並把收緊情境與驗收寫進 F3。
  - 批次 E3 原想直接擋大於 24 的值，與「既有資料／設定不能因變嚴而壞掉」衝突，已改成夾到 24 並出聲。
  - 批次 H3 不在執行期重裝規則，與 CLAUDE.md「不要在執行期增減 NLog target 或規則」一致。
  - 批次 G1 改 DTO 欄位為可空：控制器宣告型別必須是實際型別，已列入驗收。
- **批次之間**：B 與 H4 都改 `HostDayWorkflowState.cs` 的指紋區段，順序 B → H，H 開工時以 B 提交後的檔案為準。C 與 E4、F3、G 都改前端，各批改不同檔（`components.css`／`ui.js`、`prtg-admin.js`、`reports*.js`）；`prtg-admin.js` 同時被 E4 與 F3 改，順序 E → F，一段驗收提交後才開下一段。
- **四個坑**：
  - 什麼算一個／分母為零：F1 的重試次數以「當天失敗次數」計、跨日歸零；G1 的 null 與 0 已分開定義；E4 的上限剛好那一格可讀。
  - 破壞性判準的反例：H2 會讓某些原本保留的帳號變成空值，已要求以真實樣本驗合法的含冒號帳號；H1 的保守遮蔽只在逾時時發生。
  - 單向閘門：B2 把舊項目視為終態之前，要先查它原本要補救的情境，已寫進定案。
  - 移除類的依賴方：G2 已列清單與 grep 驗收；E2 搬移 DTO 要同步路由快照與序列化。
- **未驗證的技術事實**：B1 的寄信與回寫先後、E1 的英文字樣、E3 的溢位型別、G3 是否存在 Low 的 warning 規則，都標為〔待核〕並寫成「先寫紅測試，寫不紅就降級或移除」。
- 複檢完成；除上述四點修正外無新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 回顧盤點 | 6 個唯讀 subagent（Sonnet 5.5）＋主代理（Fable 5.2）複核 | 22 條發現，18 條已核、4 條待核 | 兩個探針（整列連結命中、正規式逾時） | 見各批次「現況與核對結果」 |

## 體檢交接

（實作完成後填寫）
