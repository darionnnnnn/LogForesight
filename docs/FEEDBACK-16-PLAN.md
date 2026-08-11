# 回饋第十六輪實作規劃（FEEDBACK-16-PLAN）

> 來源：外部程式碼審視報告（發現 1~9）＋使用者回饋六項（其他 1~6）。
> 九項發現已逐一對照 dev@75faf07 程式碼核實，全部屬實。
> 四個決策點已與使用者定案（見各批次「決策」註記）。
> 狀態：**全案（批次A~F）實作完成，含測試與體檢輪，已在瀏覽器中實測 UI 改動**。

## 總覽

| 批次 | 主題 | 對應項目 |
|------|------|----------|
| A | 郵件通知可信度重構 | 發現 1、2、8＋其他 6 |
| B | 趨勢層 Low 簽章出口 | 發現 3、4 |
| C | 抑制範圍護欄下沉 | 發現 5 |
| D | AI 入口整理 | 發現 6、7＋其他 2 |
| E | UI 小改 | 其他 1、3、4、5 |
| F | 文件收尾＋全量測試 | — |

發現 9（文字報告缺已抑制告警）已由前輪 spawn_task 交付獨立任務，本輪不含。

---

## 批次 A：郵件通知可信度重構

**決策**：高風險即時通知改為**按收件人分組聚合**——一位使用者一次執行只收一封信，
信中簡述其負責範圍內的高風險主機，詳細內容導向站台（使用者回饋「其他 6」定案）。

### A-1 高風險即時通知：逐主機日一封 → 每收件人一封

檔案：`LogForesight.Web/Services/Mail/MailNotificationService.cs`（`SendUrgentNotificationsAsync`）

改法：

1. `pending` 的計算不變（`UrgentSentKeys` 去重）。
2. 反轉映射：對每筆 pending 解析「全域收件人 ∪ 主機負責人（`MailNotifyHostOwners` 開啟時）」，
   建 `收件人 email → 該收件人應看到的 record 清單`。同一人既是全域收件人又是負責人 → 只進一次
   （沿用既有 `OrdinalIgnoreCase` 去重）。全域收件人自然涵蓋全部 pending。
3. 每收件人組一封信：
   - 主旨：單台時沿用現行格式；多台時 `{host}` 帶「N 台主機」。
   - 內文：每主機日一行（`[高風險] 主機　日期　Headline`），**上限 20 行**，
     超出補一行「其餘 N 筆請至站台的問題查詢頁檢視」。不放 RiskBasis 細節——簡述即可，
     細節到網站處理（使用者定案）。
4. **標記語意改為「涵蓋此 record 的信全部寄成功才標記」**：
   - `SendSafeAsync` 改回傳 `bool`（見 A-2）。
   - 逐收件人寄送時累積 per-record 成功狀態；任一涵蓋它的信失敗 → 該 record 不進
     `UrgentSentKeys`，下次執行整批補寄。重寄（已收到的人再收一次）是比漏寄輕的錯，
     且聚合後信量小、發生率低。
   - 收件人映射為空（全域收件人未設且無負責人 email）→ **不標記**，設定補齊後下次執行自動補寄。
     修掉「初次部署先啟用通知後填收件人 → 那幾天永久漏寄」的路徑（發現 2b）。

### A-2 `SendSafeAsync`：回傳成敗＋取消不再被吞

檔案：同上

- 簽章改 `Task<bool>`：成功 `true`，寄送失敗 `false`（照舊記 WARN）。
- **取消要與逾時區分**：`catch (OperationCanceledException) when (ct.IsCancellationRequested)`
  → 重新拋出，讓呼叫端迴圈立即中斷（服務停止／執行取消時不能把整批標成已寄——發現 2a 的根修）。
  30 秒逾時觸發的 OCE（`timeoutCts` 到期、外層 ct 未取消）視為單封寄送失敗，回 `false` 不拋。
- 呼叫端（`SendUrgentNotificationsAsync`）讓 OCE 穿透到 `NotifyAfterRunAsync` 既有外層
  catch 前，先確保「已成功的 record 標記」有落地（把 `_state.Update` 移到 try/finally，
  或在拋出前先寫入已成功部分）。

### A-3 連續失敗熔斷

檔案：同上

- 同一輪寄送**連續 3 封失敗**即停止本輪剩餘寄送，記一筆 WARN（含剩餘封數）。
  聚合後每輪信件數＝收件人數，熔斷主要防的是「SMTP 整台不通時把 30 秒 × N 全付掉」。
- 中斷後未寄出的 record 不標記，下次執行補寄（與 A-1 標記語意自然銜接）。

### A-4 通知移出執行鎖

檔案：`LogForesight.Web/Services/SchedulerHostedService.cs`（`TriggerRunAsync`）

- `NotifyAfterRunAsync` 呼叫點從 `RunExclusiveAsync` lambda 內移到 **`finally` 的
  `EndRun(outcome)` 之後**：分析結果已落地，通知不需要持鎖；順帶修掉「寄信期間 UI
  一直顯示執行中」的副作用（報告未提但實測會發生）。
- 觸發條件照舊：`outcome is { Success: true }` 才通知。
- **取消權杖檢查點**：移出後 `runCts` 可能已隨 `EndRun` 走完生命週期——改傳
  `_stoppingToken`（HostedService 的停止權杖）或 `CancellationToken.None`＋依賴 A-2 的
  30 秒逾時。實作時確認 `SchedulerRunState.TryBeginRun/EndRun` 對 runCts 的 dispose 時機再定。
- **並發檢查點**：移出鎖後 `NotifyAfterRunAsync` 與 60 秒輪詢的 `CheckAndSendDailyWeeklyAsync`
  可能同時碰 `MailNotifyStateStore`。`JsonBlobSingleton.Update` 走 `EfJsonBlobStore.Mutate`，
  實作時確認其並發語意（DB-SPEC 的 ConcurrencyToken）；必要時服務內加一把私有
  `SemaphoreSlim` 序列化兩路。

### A-5 執行後摘要與彙總信筆數上限

檔案：`MailNotificationService.cs`（`SendRunSummaryAsync`、`SendDigestAsync`）

- 明細行**上限 50 行**（依風險排序後截斷），補一行「其餘 N 台／N 筆請至站台檢視」。
  `MailMinRiskLevel=中` 在 2000 台規模下 600+ 行純文字信的防呆（發現 8）。

### A 批次測試

`LogForesight.Tests/MailNotificationServiceTests.cs`：

- 聚合：全域收件人收一封列全部；負責人只收自己主機；身兼兩者只收一封不重複。
- 標記：寄成功才進 `UrgentSentKeys`；部分失敗只標成功涵蓋的；收件人空不標記、補設定後補寄。
- 取消：外層 ct 取消 → 迴圈中斷、未寄批次不標記；單封逾時 → 記失敗續寄下一封。
- 熔斷：連續 3 失敗停止本輪；下輪補寄。
- 上限：即時信 20 行、摘要／彙總 50 行截斷＋「其餘 N 筆」行。
- 測試替身 `FakeSmtpMailSender` 需支援逐封成敗腳本與取消模擬（現況確認後擴充）。

---

## 批次 B：趨勢層 Low 簽章出口

**決策**：加爆量例外，門檻採「基準 ×10 或絕對量 ≥100 筆」。

### B-1 Rising 閘門加爆量例外

檔案：`LogForesight.Core/Analysis/TrendAnalyzer.cs`（Rising 分支，約 :157-192）

- 現行：`preEscalationSeverity >= Medium` 才產生告警文字。
- 新增例外：`preEscalationSeverity < Medium` 但
  `sig.Count >= sig.HistoryDailyAverage * 10 || sig.Count >= 100` → 仍產生告警。
- 告警文字用可區辨的前綴「頻率暴增」（非「頻率上升」），讓讀者知道這是走爆量例外進來的
  Low 簽章；`alertRefs` 照樣帶 `IssueKey` 供頁內導航。
- `channelWarmingUp` 守門照舊優先——暖身期一律不告警。
- Trend／Escalate／ElevatesDayRisk 的計算完全不動（本來就照算）。

### B-2 DETECTION-SPEC 申報「Low 簽章的趨勢出口」

檔案：`docs/DETECTION-SPEC.md`

新增小節，明確定位兩條出口、防止未來被「補一致性」拆掉（發現 4 的核心風險）：

1. **瞬時爆量**：`TrendAnalyzer` 爆量例外（B-1，×10 或 ≥100）。
2. **持續惡化**：`SlowTrendAnalyzer` 無嚴重度閘門（7 天窗口翻倍即告警）——這是**刻意保留的
   不對稱**，不是缺漏；單日層的閘門理由（Low 雜訊型簽章波動劇烈）在 7 天窗口被
   `MinRecentCount=10`＋兩側等長窗口部分抵銷，且它是未知簽章緩慢成長的唯一安全網。
3. 未命中規則事件的涵蓋總結：首次出現靜音（維持）、單日爆量走 B-1、持續成長走慢速層、
   總量突增走 Volume 網——四象限寫成表。

### B 批次測試

`TrendAnalyzerTests`：Low 簽章 ×10 觸發、99 筆且 <×10 不觸發、100 筆觸發、暖身期不觸發、
Medium 以上走原有路徑文字不變。`SlowTrendAnalyzerTests` 不動（行為零改變）。

---

## 批次 C：抑制範圍護欄下沉

**決策**：Signature／Correlation／Volume **三型一律限 Host**（比報告原建議再收緊一步——
UI 三個入口本來就硬編 Host，服務層收到一樣緊零行為損失；Site 的 Signature 抑制同樣是
無預覽的大範圍噤聲）。

### C-1 `AddSuppression` 服務層限制

檔案：`LogForesight.Web/Services/RuleAdminService.cs`（統一入口，約 :422）

- `TargetType != Rule` 且 `Scope != Host` → `DomainException.Validation`，訊息說明
  「簽章／關聯／總量抑制目前僅支援單台主機範圍；大範圍抑制需先有影響面預覽（規劃中）」。
- Rule 型完全不動（有 C1 預覽護欄）。
- 泛型化預覽（`PreviewSuppression(targetType, targetKey, scope, groupId)`）列為**未來輪**
  前置條件：若要在規則頁補新三型建立入口或開放 Group/Site，必須先做。本輪不做。

### C 批次測試

`RuleAdminServiceTests`：三型 × Group/Site 被拒（六案）；三型 Host 照常成功；Rule 型
Group/Site 不受影響。

### C 文件

`docs/RULES-SPEC.md`（或抑制章節所在文件）補「範圍支援矩陣」：Rule＝Host/Group/Site（Group/Site
有預覽），新三型＝僅 Host。

---

## 批次 D：AI 入口整理

**決策**：發現 6 只加執行中提示，不做全域 gate（雙實例是刻意設計且註解有申報，保留取捨）。

### D-1 「分析執行中」提示

檔案：`help-manual.js`＋`chat-panel.js`（兩個對話入口共用）、對應 cshtml

- 問答框／對話框上方顯示小字提示「分析執行中，AI 回應可能較慢」。
- 資料來源：前端既有的排程狀態 API（runs 頁在用的 `/api/scheduler/status` 類端點；
  實作時確認端點名稱與 `IsRunning` 欄位、以及非 Maintain 權限使用者是否可讀——
  詳情頁對話的使用者不一定有排程頁權限，若權限不符則只在說明書頁（Maintain）顯示提示，
  chat-panel 略過）。
- 提問當下查一次即可，不輪詢。

### D-2 「引用章節」標籤改名

檔案：`help-manual.js:135`

- `'引用章節：'` → `'參考章節（提供給 AI 的內容）：'`。
- `HelpQaService`／DTO 的 `CitedChapterIds` 欄位名不動（改名會動 API 介面，收益不成比例）；
  在 `HelpQaService.cs:63` 補一行註解說明「這是選節器候選，非模型自述引用」。
- SystemPrompt 的「列出實際引用章節」要求保留——兩份清單語意分開後不再矛盾。

### D-3 AI 未設定全站隱藏收尾（其他 2）

現況盤點（已核實）：儀表板焦點卡、records 頁 AI 歸納鈕、詳情頁對話、runs 頁傾印開關
均已隱藏；**唯一例外是說明書頁**顯示「未設定 AI 服務…」文案。

- `HelpManual.cshtml`＋`help-manual.js`：AI 不可用時整張「詢問說明書」卡 `d-none`，
  移除 `help-ask-unavailable` 文案路徑。
- 全站盤點 `/api/ai/status` 消費端（dashboard／records／record-detail／runs／netiq／
  help-manual）確認一致為「隱藏」而非「顯示停用文案」；netiq.js:225 的用法實作時看一眼。
- 設定頁 AI 分頁**保留**（不然沒地方設定）。

### D 批次測試

`HelpQaServiceTests` 不動（後端無行為變更）；前端為主，實測驗證。

---

## 批次 E：UI 小改

**決策**：不套 ui-ux-pro-max，沿用既有樣式系統直接微調。

### E-1 「只看被拒的存取」可取消（其他 1）

檔案：`audit.js`（:173-178）、`Audit.cshtml`（按鈕標記）

- 改 toggle：目前篩選已是 Denied → 再按清回「全部」並查詢；否則設 Denied 查詢。
- 按鈕加 active 視覺狀態（既有 `btn` active class），與下拉選單手動變更、
  URL `?result=Denied` 下鑽進頁三者同步（`search()` 後依當前值刷新按鈕狀態）。

### E-2 側欄 brand 區塊排版（其他 3）

檔案：`site.css`（`.lf-sidebar__brand*`，約 :483-549）

- `.lf-sidebar__brand-mark` 自 2.25rem 放大至 2.75rem，內部圖示同比放大到 1.5rem。
- `.lf-sidebar__brand-text` 用 `justify-content: space-between` 讓名稱貼上緣、副標貼下緣。
- 只動側欄；登入頁 `.lf-login__brand` 結構相同但回饋未提，不動。
- 實測項：長品牌名的省略號行為不能回歸（十三輪 G 的修正）。
- **實作中用瀏覽器實測抓到規劃沒預期到的問題**：一開始用固定 `height: 2.75rem` 給圖示、
  `align-self: stretch` 給文字（假設圖示固定高度必然 ≥ 文字自然高度），但實測發現有副標時
  兩行文字的自然高度（60px）超過圖示固定高度（53px），導致圖示沒撐滿、對齊被打破。改法：
  父層 `.lf-sidebar__brand` 的 `align-items` 從 `center` 改 `stretch`，圖示的 `height` 改
  `min-height`——兩者互相以「較高者」為準撐滿，沒有副標時圖示落在 min-height（維持正方形），
  有副標時圖示跟著撐高（微幅變成瘦長矩形，換取像素級對齊）。這個修正只能靠瀏覽器實測量測
  `getBoundingClientRect()` 才抓得到，靜態審查規劃時看不出來。

### E-3 外觀儲存後即時更新側欄（其他 4）

檔案：`settings.js`（儲存成功回呼，約 :507-517）、`_Layout.cshtml`（側欄元素需可定位）

- 儲存成功後直接更新側欄 DOM：品牌名稱、副標（含「無副標→有副標」的節點增減）、
  圖示（自訂圖 ↔ 預設 SVG 的切換）。
- `_Layout.cshtml` 的品牌節點補 id（`lf-brand-name` 等）供 JS 定位；Razor 端 `Brand.Get()`
  邏輯不動（下次整頁載入本來就會拿到新值，這裡只是免重整的即時回饋）。
- 瀏覽器分頁標題不在回饋範圍，不動。

### E-4 說明書問答卡移至下方＋內容高度對齊目錄（其他 5）

檔案：`HelpManual.cshtml`、`help-manual.js`、必要時 `site.css`

- 「詢問說明書」卡從頁面頂部移到章節內容 row **之後**（頁面下方）。
- 章節內容卡高度以左欄「章節目錄」卡為基準：載入完成與章節切換後，JS 量測目錄卡高度，
  設到內容卡 body 的 `max-height`，超出 `overflow-y: auto`。視窗 resize 時重算（節流）。
  純 CSS 難以做到「以較矮欄為基準」（flex stretch 只會等高於較高者），故用 JS 量測。
- 注意：D-3 決定 AI 不可用時整卡隱藏，移位後此邏輯一併帶過去。
- 版面理解已向使用者確認方向（問答卡移下方＋內容高度對齊目錄）；若實測後位置不符預期，
  以使用者實測回饋為準微調。

---

## 批次 F：文件收尾＋全量測試

1. `README.md`：郵件通知行為改述——「高風險即時通知按收件人聚合，一人一次執行一封，
   詳細內容至站台檢視」；寄送可靠性語意（成功才標記、失敗自動補寄、連續失敗熔斷）。
2. `docs/DETECTION-SPEC.md`：B-2 的「Low 簽章趨勢出口」小節＋爆量例外門檻申報。
3. `docs/WEB-SPEC.md`：說明書頁版面變更（E-4）、AI 未設定隱藏語意（D-3）、
   抑制範圍矩陣（C-1，或指向 RULES-SPEC）。
4. 全量 `dotnet test`：實作前基準 1810 綠，全案完成後 1827 綠（+17：郵件 6、趨勢層 5、
   抑制範圍 6）。
5. 依既有流程：feature branch（`feature/feedback-16`）→ 併 dev → 使用者實測 → 併 master。

## 實作時的檢查點清單（動手前的未定案項目，實作階段逐一確認完畢）

- [x] `EfJsonBlobStore.Mutate` 的並發語意（A-4：通知移出鎖後與輪詢並發）——已內建樂觀並發
      偵測＋短退避重試（見該檔頭註解「webdata 下更新遺失的風險小；真的撞上並發時記 log 並
      重試」），不需要在 `MailNotificationService` 額外加序列化鎖。
- [x] `runCts` 在 `EndRun` 後的生命週期（A-4）——`SchedulerRunState.EndRun` 會
      `_cts?.Dispose()`，確認不能沿用；改用 `SchedulerHostedService` 新增的
      `_lifetime.ApplicationStopping`（`IHostApplicationLifetime`，建構子已注入），站台正常
      關閉時通知會被取消，平時等同不取消。
- [x] `FakeSmtpMailSender` 現有能力，擴充逐封成敗腳本（A 測試）——加 `Attempts`（不論成敗都
      記錄，供驗證熔斷的嘗試次數）與 `ThrowOnSendForRecipient`（依收件人選擇性失敗）。
- [x] 排程狀態端點名稱與非 Maintain 權限可讀性（D-1）——沿用既有 `GET /api/run-activity`
      （`RunActivityController`，docs/SCALE-FIX-PLAN-2026-08-06.md S-3 新增），刻意不掛
      `[Permission]`、任何登入者可讀，chat-panel.js（非 Maintain 專屬頁）與 help-manual.js
      （Maintain 專屬頁）都能直接呼叫，不需要權限分流的退路。
- [x] `netiq.js:225` 的 ai/status 用法是否已是隱藏語意（D-3）——確認已是
      `classList.toggle('d-none', !aiStatus?.available)`，不需修改。

## 全案體檢（實作完成後的第二輪重掃）

逐批對照本規劃重新審查程式碼與文件，揪出並修正四個問題、申報一處刻意偏離：

1. **【修正】寄送迴圈未用 `recipientOrder`**（A-1）：首版建了保序清單、註解也宣稱熔斷的
   「連續」語意依明確順序判定，但迴圈實際仍迭代 `Dictionary`——順序碰巧正確（插入序），
   卻依賴實作細節而非保證。已改為迭代 `recipientOrder`。
2. **【修正】取消時標記整段被跳過**（A-2）：取消例外從 `SendSafeAsync` 重拋後直接穿透整個
   方法，`_state.Update` 不會執行——中斷前已「對所有收件人寄成功」的 record 也不標記，
   下次對已收到的人整批重寄。規劃 A-2 本來就要求已成功部分先落地，首版漏做。已把標記
   移進 `finally`，並補測試「取消中斷前已寄成的record仍標記_只補寄未完成的」（`FakeSmtpMailSender`
   加 `OnSend` 回呼＋`ThrowOnSendForRecipientError`，讓取消能模擬在「寄送中途」而非一開始）。
3. **【修正＋補強】兩個 `NotifyAfterRunAsync` 並發重複寄**（A-4 的衍生風險，體檢新增）：
   通知移出執行鎖後，前一輪寄信進行中、下一輪（統計模式秒級完成）就可能開始並完成——
   兩邊都在「第一輪還沒標記」的窗口讀 state，同一批 pending 整批重複寄。加
   `SemaphoreSlim(1,1)` 的 `_notifyGate` 序列化 `NotifyAfterRunAsync`（gate 包住 state 讀取，
   後進的一輪天然排除已寄過的）；每日/週彙總不 gate——各自有「上次寄送日」防重複，
   且操作不同狀態欄位。
4. **【修正】`SurgeMinCount` 註解與 DETECTION-SPEC 的論述邏輯寫反**（B-1）：絕對量門檻兜底的
   是「基準較大」的情境（基準 15 → 10 倍要 150 筆，今日 100 筆的真暴增反被倍率門檻漏掉），
   不是「基準很小」（基準小時 10 倍本來就容易達到）。程式碼行為正確、測試斷言正確
   （絕對量測試的註解一直是對的），錯的是常數註解與 spec 文字，兩處已修。
5. **【申報偏離】單台高風險信保留「判定依據」行**（A-1 規劃寫「不放 RiskBasis 細節」）：
   聚合信只有一台主機時，補一行 `判定依據：{RiskBasis}`——單台情境與改版前的逐台信
   內容對等、只多一行仍屬簡述；多台聚合信維持規劃，不放判定依據。
6. **【補做】README 收尾**（F-1 首版漏做）：架構區塊的郵件通知 bullet 補「按收件人聚合
   一人一次執行一封、寄成功才標記、失敗自動補寄、連續失敗熔斷」；同段「告警抑制」的
   「主機/群組/全站三種範圍」在 C-1 之後對新三型已不成立，改述為「規則型三種範圍、
   其餘三型僅單台主機」並指向 RULES-SPEC 範圍矩陣。
