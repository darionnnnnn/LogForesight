# 第 56 輪規劃：派工與回報流程重整、排版一致性收斂

> 狀態：**實作中**（feature/feedback-56；§0 四項 2026-10-20 照建議值定案）。
> 基準：feature/feedback-55@ee62b0ed（6,326 通過／10 略過）。本分支疊在第 55 輪分支之上——使用者規則不自行 merge，第 55 輪併 dev 後本分支再併；委派改用 Sonnet 5.5 subagent。
> 來源：使用者 2026-10-20 六角度評估的角度 5（派工回報流程與使用者體驗）與角度 6（畫面排版專業度）；評估結論與證據見 [FEEDBACK-55-PLAN.md](FEEDBACK-55-PLAN.md) §1、§2。
> 設計依據：使用者定案套用 `ui-ux-pro-max`。本輪不換風格、不重做 token（DESIGN-SYSTEM v2 維持），只做**一致性收斂與可用性**；skill 的判斷順序（可用性與語意 → 真實內容與響應式 → 一致性 → 效能動態）與 WCAG 2.2 AA 檢查目標作為驗收基準，檢索到的對應指引：資料表橫向溢出容器、每頁 focus 可見、表單 blur 驗證＋可聚焦錯誤摘要、空狀態要有下一步、載入回饋依等待長短、正文對比 4.5:1、目前頁要有視覺指示。
> 實作方式：agy 為主、`impl-low` 備援；每段 Claude 以瀏覽器實測（內建瀏覽器 `preview_start` 的 `web`，Stub 登入）＋ UI 守門測試驗收。
> 第二版改了什麼（審視後）：E-1 定案 `previous_handler_id` 欄；E-2 改 POST 並補效能邊界；E-5 改靜態目錄模組＋反射對照、不開端點；E-6 已讀改明確 `POST seen`、只有 Assign 算已讀；新增 E-10 首頁待派工卡；F-1 驗收改為盤點後逐頁核對、不要求 26 頁都有頁首；F-4 icon 按鈕缺 title 改警告加退路、不擲例外；§6 補驗證環境；明確不做補三條。

## 0. 待確認（2026-10-20 全數照建議值定案：E-7 做、E-6 只有 Assign 算已讀、F-1 topbar 唯一 h1、F-2 統一 table-sm）

- **E-7 退回處理人**：處理人標「無法處理」後，管理者目前只能改派或結案；建議新增「退回」（附說明，狀態回 `in_progress`，入第 55 輪的交辦摘要 outbox）。要做嗎？
- **E-6 未讀回覆的定義**：建議以「處理人最近回覆時間 > 最近一位 **Assign** 使用者開啟該單詳情的時間」判定；Manager／Dev 以 ViewAll 開啟不算已讀（他們不處理派工）。可接受？
- **F-1 頁首與 topbar 的分工**：建議 topbar 保留唯一的 `<h1>` 頁名，頁內頁首元件只放副標（一句話說這頁做什麼）與動作鈕，不再出現第二個標題；沒有動作鈕也沒有必要副標的頁不放頁首。報表頁自己的 `<h1>` 移除。麵包屑改規格（§8.5 刪除該句）不補做。可接受？
- **F-2 表格密度**：建議全站 `renderTable` 統一 `table-sm`（列高目標 36px，DESIGN-SYSTEM §4），自建表格歸一後不再有 `table` 與 `table-sm` 並存。可接受？

## 1. 使用情境流程（定案依據，角度 5）

| 時段 | 角色 | 畫面 | 本輪對應 |
|---|---|---|---|
| 08:00 | 管理者 | 首頁「待派工」卡：昨天往前 7 天沒有人接的問題數（與交辦總覽「待派」頁籤同一支試跑）→ 點進待派頁籤或依問題視角一鍵交辦 | E-10、E-2 |
| 交辦當下 | 管理者 | 人選只列能處理且看得到這些主機的人，預設帶最近處理過同問題的人；送出後 toast 附單號連結；急件可勾「立即通知」（第 55 輪 C-4） | E-2、E-8 |
| 08:00／12:00 | 處理人 | 收到交辦摘要信（第 55 輪）→ 點連結進「我的交辦」→ 依逾期、期限排序 → 詳情逐台或整批回覆；手機 375px 可完成「看單 → 回覆 → 下一筆」 | E-5、F-全 |
| 回覆後 | 管理者 | 側欄「交辦總覽」徽章顯示未讀回覆數，總覽清單「未讀回覆」欄，60 秒自動刷新；Assign 開詳情即標已讀 | E-6 |
| 無法處理 | 管理者 | 上報信即時到（第 55 輪維持）；詳情頁可改派／退回／代為結案 | E-7 |
| 取消／改派 | 處理人 | 下一封摘要列「移交 M 張／取消 K 張」；原處理人仍可唯讀開啟被移走的單 | E-1 |
| 任何時候 | 任何人 | 不會按到回 403 的按鈕；無權看的頁面有明確說明不卡載入 | E-1 |

## 2. 核對表（本輪契約依賴的事實）

| 項目 | 結果 | 證據 |
|---|---|---|
| 詳情頁回覆鈕只看是否本人 | ✅ 缺陷 | `WorkOrderQueryService.cs:210` `ViewerIsHandler = HandlerId == UserId`；`work-order-detail.js:359-389`；`handler-detail.js:108` 另有 Handle 判斷（兩頁不一致） |
| 無權者開別人的處理人頁卡載入 | ✅ 缺陷 | `AuthorizeHandlerView` 403（WorkOrderQueryService.cs:121-126）；`handler-detail.js:110` summary 失敗後 `loadOrders` 不執行；§9.4a 首段「全角色」與末段「本人或 Assign／ViewAll」矛盾 |
| 手動派工人選＝全部啟用使用者 | ✅ 缺陷 | `work-order-detail.js:81-91`、`records.js:1469`、`handling-panel.js:92` 皆打 `GET api/admin/users`；自動派工候選條件在 `DispatchCandidateSource.cs:46-58` |
| 日層級指派 change 即送出 | ✅ 缺陷 | `handling-panel.js:243-262`，衝突確認取消時前半段不回滾 |
| 取消日層級指派不收單 | ✅ 缺陷 | `DayHandlingCommandService.cs:192-196` handlerId=null 不碰 day_assign 單 |
| 狀態清單五份 | ✅ | `issue-status-reply.js:16`、`handling-panel.js:24-55`、`work-order-detail.js:54-61`／`:282`、`records.js:1322`；後端 `IssueHandlingStatuses`（IssueHandling.cs:242-276）；驗證單點 `IssueStatusValidation`（已含日期、必填、觀察期規則） |
| 回覆無狀態轉移限制，後端接受 open | ✅ | `WorkOrderReplyService.cs:203`、`WorkOrderCoordinator.cs:699-721`（檔案在 Core/Persistence） |
| 管理者端無回覆通知與刷新 | ✅ | 只有 escalated 寄 admin（WorkOrderReplyService.cs:72）；`work-orders.js`／`layout.js:229` 無輪詢 |
| 原處理人被改派後開單 403 | ✅ | `WorkOrderQueryService.cs:436-442` Authorize 只認目前處理人 |
| 待派試跑端點已存在 | ✅ 可複用 | `GET api/work-orders/gaps?from&to&page`（WorkOrderBoardController.cs:32-34），超過 20,000 候選回 `tooLarge` |
| 頁首六種寫法、無共用元件 | ✅ | 排版掃描 4-1（`lf-page-header` 0 筆；`Reports.cshtml` 與 topbar 各一個 h1） |
| 自建表格 14 處 | ✅ | groups.js:196、host-detail.js:474／746、permission-changes.js:707／848、prtg-admin.js:151／484、prtg-guard.js:129、record-detail.js:1558、rules.js:1154、settings.js:935、core/alert-tools-table.js:31、charts.js:250、markdown-lite.js:118；靜態 Imports.cshtml:33、Prtg.cshtml ×5、Runs.cshtml:376 |
| inline style | ✅ | cshtml 57 處（Prtg 24、Settings 11…）、JS `.style.` 約 85 處；`.lf-input-narrow` 只被用 2 次 |
| modal `aria-labelledby` | ✅ | 22 個靜態 modal 只有 1 個有；`showDetailModal` 會產生 titleId |
| 側欄分組標題對比 | ✅ | `#68799a` on `#0f1d3a`＝3.81:1（實算）；`--lf-sidebar-fg #9fb0cc` 同底＝7.6:1 |
| 日期／數字繞過 format.js | ✅ | handler-detail.js:711、work-orders.js:582、prtg-admin.js:77-78／3144-3256、rules.js:817／830／1027／1048、prtg-calibration.js:223 |
| 目前頁高亮漏詳情頁 | ✅ | `layout.js:176-179` 前綴比對，`/hosts/{id}` 無選單項可比 |
| KPI 欄格四頁三種 | ✅ | dashboard `col-6 col-lg`、handler-detail `col-6 col-md-3`、user-detail／reports `col-6 col-lg-3`；DESIGN-SYSTEM §6b 寫 `col-6 col-xl-3` |
| 深色模式 | — | 不存在，明確不做 |
| 既有 UI 守門測試 | ✅ 可擴充 | `FrontendConsistencyUiTests`、`PagePrimaryActionUiTests`、`EmptyStateGuidanceUiTests`、`ErrorStateUiTests`、`FrontendWaitingStateUiTests`、`RunsPageUiTests` |
| 本機可跑站台 | ✅ | `.claude/launch.json` `web`（port 5041）；Stub 登入 `demo-admin` |

## 3. 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| E 派工回報流程 | E-1 權限預檢與唯讀分流；E-2 派工人選端點；E-3 日層級指派改確認送出；E-4 取消指派收單；E-5 狀態目錄單一來源與轉移規則；E-6 未讀回覆與輪詢；E-7 退回處理人；E-8 回饋文案與即時檢查；E-9 規格同步；E-10 首頁待派工卡 | 大 | 第 55 輪 C（outbox） | 1 |
| F 排版收斂 | F-1 頁首元件；F-2 表格歸一；F-3 inline style 清除；F-4 無障礙；F-5 格式單一出口；F-6 狀態回饋收斂；F-7 KPI 欄格與規格同步；F-8 runs 狀態色走 token | 大 | E 先完成（避免同檔互踩） | 2 |

E 與 F 都會動 `work-orders.js`、`work-order-detail.js`、`handler-detail.js`、`handling-panel.js`、`dashboard.js`：F 排在 E 之後，F 的頁面套用以 E 的結果為基準。

## 4. 批次 E：派工與回報流程

### E-1 權限預檢與唯讀分流
**定案**
- 交辦單 DTO 新增 `ViewerCanReply`（本人 ∧ 具 Handle ∧ 單進行中）、`ViewerCanManage`（Assign）；兩頁回覆鈕一律看 `ViewerCanReply`，不再各自推導。
- `lf_work_orders` 新增可空欄 `previous_handler_id`（`SchemaUpgrader` 冪等加欄、SQL Server 同步），改派時寫入被換掉的處理人；拆出不寫（原單處理人沒變）。`Authorize` 放行「目前處理人或 previous_handler_id」，後者唯讀，畫面頂端提示「此單已移交給 ○○，你只能檢視」。只記最近一次（再被改派時覆寫）。
- 處理人頁：無權看別人時 API 仍 403，但前端**先**依 `me` 的能力判斷，非本人且無 Assign／ViewAll 時直接渲染「只能檢視自己的交辦」空狀態＋「前往我的交辦」連結，不打 summary；全站處理人姓名連結只在檢視者有權時才是連結，否則純文字（`format.js` 的 `handlerLink()` 單一出口，吃一個 `canViewOthers` 旗標）。§9.4a 首段改寫為「本人或 Assign／ViewAll 可開；其他角色看到的是姓名文字」。

**驗收**
- `WorkOrderQueryServiceTests`：三案（本人有 Handle→true；本人無 Handle→false；非本人→false）；原處理人（previous_handler_id）開啟不 403 且 `ViewerCanReply=false`；再改派一次後更早的處理人 403。
- UI 守門：`grep -c "viewerCanReply" work-order-detail.js handler-detail.js` 各 ≥1；`grep -c "hasCapability(user, 'Handle')" handler-detail.js` 為 0；處理人姓名連結組裝只在 `format.js` 出現（`grep -rn "/handlers/" wwwroot/js/pages` 為 0）。
- 瀏覽器實測：以 User 帳號開別人的 `/handlers/{id}` 顯示空狀態、無 403 toast；開自己的頁回覆鈕存在；被改派後的原處理人能唯讀開單。

### E-2 派工人選端點
**定案**
- 新端點 `POST api/work-orders/candidates { hostIds[], source?, eventId? }`（Assign；主機清單可能上千，不走 query string）：回傳啟用 ∧ 具 Handle 的使用者，每人附 `VisibleHostCount`／`HiddenHostCount`、`Paused`、`InPool`、`RecentlyResolvedSameIssue`（最近 30 天此問題 resolved 單的處理人）、`ActiveLoad`（進行中台數）。判定複用 `DispatchCandidateSource`（候選）與 `WorkOrderDispatcher`（延續性偏好、負載）既有規則，不另寫一份；可見性以每位候選人的可見主機 id 集合與請求主機集合做交集（候選人數 × 主機數，50 × 2,000 等級為記憶體集合運算，不查 DB）。
- 三個入口（交辦 modal、改派／拆出、日層級指派）改用此端點；清單排序：最近處理過同問題 → 看得到全部 → 負載輕；看不到全部主機者可選但列尾並標「看不到 N 台（將以案件授與補）」；暫停接單者標示但可選。`searchableUserSelect` 加 `badges` 參數呈現這些標記。
- `GET api/admin/users` 不再被這三頁呼叫。

**驗收**
- 控制器／服務測試：無 Handle 者不在清單；暫停者 `Paused=true`；最近 resolved 者 `RecentlyResolvedSameIssue=true` 且排第一；`HiddenHostCount` 正確；非 Assign 403。突變：把排序鍵的最近處理優先拿掉後排序案必紅。
- `grep -rn "api/admin/users" records.js handling-panel.js work-order-detail.js` 為 0。
- 瀏覽器實測：交辦 modal 人選列尾出現「看不到 N 台」標記的帳號。

### E-3 日層級指派改確認送出
**定案**
- 下拉選人後不送出；按「指派」→ `POST api/records/{hostId}/{date}/handling/assign/preview` 回「將建 N 件案件、其中 M 件已由他人處理中（列名）、處理人看不到此主機與否、處理人無 email」→ 確認框一次列出 → 送出一次（衝突時帶 `reassign=true`）。取消＝零寫入。
- 既有 `PUT …/handling/assign` 行為不變（仍供 API 相容）。

**驗收**
- 服務測試：preview 不寫入（store 呼叫計數為 0）；送出一次寫入一次稽核一次。
- UI 守門：`handling-panel.js` 的 select 不再掛 `change` 直接 `api.put`（grep `addEventListener('change'` 後 5 行內無 `api.put`）。
- 瀏覽器實測：有衝突時只跳一次確認；按取消後重新整理無任何變更。

### E-4 取消日層級指派收單
**定案**：`handlerId=null` 時，對該主機日 `origin=day_assign` 的單：成員只剩此主機日 → 取消該單（`cancelled`，原因「日層級指派已取消」，入摘要 outbox）；否則只移出該成員（事件 `split_out`，member_delta −1）。走 `WorkOrderCoordinator`，寫稽核 `handling_assign`（既有）。
**驗收**：服務測試兩案（單成員單被取消；多成員單只移出）；突變：把「只剩此主機日」改成恆真後第二案必紅。瀏覽器實測：取消指派後「我的交辦」不再列該單。

### E-5 狀態目錄單一來源與轉移規則
**定案**
- 新前端模組 `core/handling-statuses.js`：匯出 `STATUSES`（code、中文名、是否結案、必填欄、是否可由處理人回覆選用）；五處靜態清單刪除、改引用。**不開端點**：中文名本來就是前端的事，後端只有 code；以測試讀 JS 檔與 `IssueHandlingStatuses.All` 反射比對 code 集合（比照 `RunsPageUiTests` 讀檔核對的做法）。
- 後端 `IssueStatusValidation` 新增 `ValidateReply(...)`：處理人回覆不接受 `open`（400）；其餘維持自由轉移（結案後再回覆由既有「單已結案 400」擋）。詳情頁逐筆標記（管理視角）仍允許 `open`（「調回未處理」是既有功能）。
- 回覆 modal：狀態 change 與日期 blur 即時檢查必填與「預計完成日不得早於今天」，錯誤顯示在欄位下並 `focus` 到第一個錯誤欄（§6b 既有規範）。

**驗收**
- 反射測試：`core/handling-statuses.js` 的 code 集合 == `IssueHandlingStatuses.All`。
- `grep -c "wont_fix" wwwroot/js/pages/*.js` 總和比基準少 ≥4。
- `WorkOrderReplyServiceTests`：status=open 回 400；`IssueHandlingCommandServiceTests`：詳情頁標記 open 仍 200。
- 瀏覽器實測：選昨天為預計完成日，送出前即紅框且焦點落在日期欄。

### E-6 未讀回覆與輪詢
**定案**
- `lf_work_orders` 新欄 `admin_seen_at`（`SchemaUpgrader` 冪等加欄、SQL Server 同步）。詳情頁載入時前端呼叫 `POST api/work-orders/{id}/seen`（Assign；其他角色 204 不寫），後端寫入現在時間。**不在 GET 上做寫入**。未讀＝`last_reply_at > COALESCE(admin_seen_at, created_at)`。
- 總覽清單「未讀回覆」欄（徽章）＋篩選「只看未讀回覆」；側欄「交辦總覽」項目徽章顯示未讀單數（新端點 `GET api/work-orders/unread-count`，Assign／ViewAll）。
- 輪詢：新 `core/badges.js` 單一計時器 60 秒刷新側欄兩顆徽章（我的交辦、交辦總覽）與總覽清單（頁面有訂閱時）；`document.hidden` 時暫停、回前景立即刷新一次；各頁不再各自開計時器。

**驗收**
- 服務測試：回覆後未讀＝1；Assign 開詳情（seen）後＝0；再回覆又＝1；ViewAll 呼叫 seen 不改 `admin_seen_at`。
- `grep -c "setInterval" core/badges.js` =1 且 `pages/work-orders.js`、`layout.js` 無新的 `setInterval`。
- 瀏覽器實測：處理人回覆後 ≤60 秒管理者側欄徽章出現；切到背景分頁 3 分鐘，network 面板無新的 unread-count 請求。

### E-7 退回處理人（§0 待確認）
**定案（若採用）**：詳情頁對含 `escalated` 成員的單新增「退回處理人」（Assign）：說明必填；成員 `escalated`→`in_progress`，事件 `returned`（新 action），稽核 `work_order_return`，入第 55 輪 outbox（新 kind `Returned`，摘要信新增段「退回 R 張」）。
**驗收**：服務測試（狀態回 in_progress、事件與稽核各一筆、outbox 一筆）；UI 守門該鈕只對 Assign 顯示；瀏覽器實測退回後處理人側單狀態回「處理中」。

### E-8 回饋文案與即時檢查
- 交辦建單成功 toast 附單號連結（多張時列前 3 張＋「等 N 張」），並停留原頁（記錄視角不變）；連結組裝走 `appUrl`。
- 取消交辦確認文字依 `MailEnabled && MailNotifyWorkOrders` 切換（「處理人會在下一封摘要收到通知」／「目前未啟用交辦通知，請自行告知」）；開關值由既有 `GET api/settings/display` 多回兩個布林（實作時若該端點語意不合，回報 §8 改用 `GET api/me` 附帶）。
- 上報信加單號與「交辦單詳情」路徑文字。
- `records.js` 交辦 modal 的預計完成日同 E-5 即時檢查。
**驗收**：`FrontendConsistencyUiTests` 新增 grep 守門（toast 連結走 `appUrl('/work-orders/')`）；`MailNotificationServiceTests` 上報信內文含單號；瀏覽器實測交辦後 toast 可點進詳情。

### E-9 規格同步
- WEB-SPEC §9.4a（授權語意、無權空狀態）、§9.4b（候選端點、未讀回覆、退回、取消指派收單、seen 端點、稽核表補 `handling_status`／`work_order_due_date`／`work_order_return`）、§9.1（待派工卡）、DB-SPEC §A（`admin_seen_at`、`previous_handler_id`）、DESIGN-SYSTEM §6b 處理人用詞對照。
- 驗收：`grep -c "admin_seen_at" docs/DB-SPEC.md docs/WEB-SPEC.md` ≥2；`grep -c "previous_handler_id" docs/DB-SPEC.md` ≥1；§9.4a 首段不再含「全角色」。

### E-10 首頁待派工卡
**定案**：儀表板待辦區對具 Assign 的使用者多一張「待派工」卡：數字＝昨天往前 7 天「待派」問題數（複用 `WorkOrderBoardService.GetGaps` 同一支試跑，取第一頁的問題分組總數；結果 `tooLarge` 時顯示「超過上限，請到待派頁縮小期間」），副行「無法派出 N 個問題」，點卡進 `/work-orders#gaps`。結果併入 `GET api/dashboard/summary`（`DashboardDto.PendingDispatch`，無 Assign 為 null、前端不渲染），受既有 `SummaryCache` 與版本戳管理。
**驗收**：`DashboardServiceTests`：有 Assign 回數字、無 Assign 為 null、`tooLarge` 傳遞；UI 守門卡片容器只在 `pendingDispatch` 非 null 時建立；瀏覽器實測 demo-admin 首頁看得到卡且點擊落在待派頁籤。

## 5. 批次 F：排版一致性收斂

### F-1 頁首元件
**定案**：`ui.js` 新增 `renderPageHeader(container, { subtitle, actions, help })` 與 `.lf-page-header`（副標一行、動作鈕靠右、`< md` 換列）；topbar 的 `<h1>` 為每頁唯一標題（§0 待確認）。實作第一步先**盤點**：列出每頁現有的頂部區塊（§2「頁首六種寫法」）與其動作鈕，寫進 §8；凡有動作鈕或副標的頁改用元件，純清單頁（既有 `.lf-toolbar` 已是頁首角色）不另加空殼。`Reports.cshtml` 自己的 `<h1>` 移除；JS 動態頁首（`#handler-header`、`#host-header`、`#user-detail-header`）改走同元件（動態主體名放副標位置的強調文字）。
**驗收**：§8 盤點表的每一頁逐項核對（Claude 以瀏覽器對照）；`grep -c "<h1" Views/Pages/*.cshtml` 總和 0；`grep -l "lf-page-header" Views/Pages/*.cshtml | wc -l` ＝ 盤點表標「套用」的頁數；`PagePrimaryActionUiTests` 既有「每頁至多一個 btn-primary」仍綠；瀏覽器實測三頁（首頁、我的交辦、PRTG 維護）375px 與 1280px。

### F-2 表格歸一
**定案**
- `renderTable` 預設 `table table-sm table-hover align-middle`（§0 待確認）；新增 `renderKeyValueTable(container, rows)` 給鍵值型小表（取代 `lf-kv-table` 自建）。
- 12 處自建表格歸一：groups.js、host-detail.js ×2、permission-changes.js ×2、prtg-admin.js ×2、prtg-guard.js、record-detail.js、rules.js、settings.js、core/alert-tools-table.js；`charts.js:250`（圖表資料表）與 `markdown-lite.js:118`（渲染器）**豁免**，註明理由。靜態 `Imports.cshtml`、`Prtg.cshtml` ×5、`Runs.cshtml` 的表格改為容器由 JS `renderTable` 填。
- 長字串：`renderTable` 欄位定義新增 `truncate: true`（`text-truncate` + `title`），主機名、問題標籤、sensor 名稱欄啟用。
**驗收**：`grep -rc "createElement('table')\|<table" wwwroot/js/pages wwwroot/js/core` 總和＝基準 − 12（只剩 renderTable、charts、markdown-lite）；`grep -c "<table" Views/Pages/*.cshtml` 總和 0；瀏覽器 768px 寬各改過的頁 `document.documentElement.scrollWidth <= clientWidth`（javascript_tool 逐頁量）。

### F-3 inline style 清除
**定案**：cshtml 57 處全部改 class（新增工具類 `.lf-w-{4,6,8,12}rem`、`.lf-maxh-{...}` 於 site.css，`.lf-input-narrow` 沿用）；JS 的 `.style.` 只保留**動態量值**（進度條寬度、圖例色塊顏色、畫布尺寸）且一律改成設 CSS 變數（`el.style.setProperty('--lf-progress', …)`）由 class 消費；其餘改 class。`Settings.cshtml:454` 的手拼警告色改 `.alert-warning` 語意類。
**驗收**：`grep -c 'style="' Views/Pages/*.cshtml Views/Shared/*.cshtml` 總和 0；`grep -rc "\.style\.[a-zA-Z]* *=" wwwroot/js` 總和 ≤ 10 且每處在允許清單（§8 列出）；新增 `InlineStyleUiTests` 守門這兩個數字。

### F-4 無障礙
**定案**
- 22 個靜態 modal 補 `aria-labelledby` 指向標題 id；`showDetailModal` 既有行為不變。
- 無 `for` 的 label 逐一補（Hosts 6、IssueOwners 3、PermissionChanges 2、Groups 1、Runs 2、Users 1）與四個無 label 輸入（`#chat-issue-select`、`#chat-input`、`#help-ask-input`、Imports file input）補可見 label 或 `aria-label`。
- 只有圖示的按鈕：`ui.js button()` 無文字且無 `title` 時以圖示名稱查一張小對照表當 `aria-label` 退路並 `console.warn`（**不擲例外**，避免任何漏網呼叫點讓整頁掛掉）；既有呼叫點補齊 title；單元測試驗退路與 warn。
- 側欄：分組標題色改用 `--lf-sidebar-fg`（7.6:1）＋大寫字距表現層級，`--lf-sidebar-section` token 改為同值；目前頁加 `aria-current="page"`；`/hosts/{id}`、`/handlers/{id}`、`/records/{host}/{date}`、`/work-orders/{id}` 高亮對應的父選單（`layout.js` 加「詳情頁路徑前綴 → 父選單」對照表）。
- focus：確認全站 `:focus-visible` 樣式在 modal 內控制項可見（既有 token），缺的補。
**驗收**：新增 `AccessibilityUiTests`：每個 `.modal` 區塊含 `aria-labelledby`（兩個 grep 計數相等）；`<label` 數 == `for=` 數（排除 `form-check-label` 包覆式）；`button()` 退路與 warn 單元測試；對比以程式讀 site.css 的兩個 token 值計算 ≥4.5。瀏覽器實測：Tab 走一遍交辦 modal，Esc 關閉，焦點回到觸發鈕；開 `/hosts/{id}` 側欄「主機」高亮。

### F-5 格式單一出口
**定案**：`handler-detail.js:711`、`work-orders.js:582`、`prtg-admin.js:77-78`、`rules.js:817/830/1027/1048`、`prtg-calibration.js:223` 改走 `format.js` 的 `formatDate`／`formatDateTime`；`prtg-admin.js` 3144–3256 的 `.toLocaleString()` 改 `formatNumber`。
**驗收**：`grep -rc "toLocale\|getFullYear()" wwwroot/js/pages` 總和 0（`format.js` 內部允許）；寫進 `FrontendConsistencyUiTests`。

### F-6 狀態回饋收斂
**定案**：prtg-admin.js 五處、prtg-value-readiness.js 一處的「載入中…」文字改 `renderSpinner`；users.js、rules.js、prtg-guard.js、prtg-value-readiness.js 的載入入口包 `guardLoad`，失敗走 `renderError` 含重試；rules.js 兩處 `{silent:true}).catch(()=>null)` 改為明確的降級文案（顯示「無法取得 X，功能仍可用」）。
**驗收**：`grep -rc "'載入中…'" wwwroot/js/pages` 為 0；`FrontendWaitingStateUiTests`／`ErrorStateUiTests` 新增這四頁；瀏覽器以 javascript_tool 讓一支 API 失敗，看到錯誤態與重試鈕。

### F-7 KPI 欄格與規格同步
**定案**：KPI 卡欄格統一 `col-6 col-lg-3`（四頁改一致），DESIGN-SYSTEM §6b 該句同步改為 `col-lg-3`；WEB-SPEC §8.5 刪「麵包屑」；§8.2 補頁首元件與表格密度決定；DESIGN-SYSTEM §7 落地對應補 `renderPageHeader`／`renderKeyValueTable`；BACKLOG 補「深色模式明確不做」。
**驗收**：`grep -c "col-6 col-lg-3" dashboard.js handler-detail.js user-detail.js reports.js` 各 ≥1；`grep -c "麵包屑" docs/WEB-SPEC.md` 為 0。

### F-8 runs 狀態色走 token
**定案**：`runs.js` `STATUS_META` 的 9 個 hex 改為執行期 `getComputedStyle` 讀 token（比照 `charts.js:19-58`），缺的 token（`#65a30d` 已回補色）補進 site.css `:root` 並寫入 DESIGN-SYSTEM §2；`charts.js` tooltip／格線兩處硬編改 token。
**驗收**：`grep -c "#[0-9a-fA-F]\{6\}" wwwroot/js/pages/runs.js wwwroot/js/core/charts.js` 為 0；瀏覽器實測 `/runs` 圖例顏色與改前一致（截圖對照）。

## 6. 驗證環境與瀏覽器實測清單

- **環境**：`preview_start` 的 `web`（port 5041），Stub 登入 `demo-admin`；另建一個 `User` 角色帳號（只有 Handle）與一個 `Manager`（ViewAll）走處理人與唯讀視角。SMTP 無法實測，郵件以測試替身驗。
- **每段共同驗收**：同第 55 輪 §7（白名單、BOM／NUL、0 警告、定向測試、過度設計與 DRY 檢查）。
- 依 `ui-ux-pro-max` verification：
  - 尺寸：375px（處理人手機）、768px、1280px；字級偏好「大」一次。
  - 鍵盤：交辦 modal、回覆 modal、日層級指派確認框——Tab／Shift+Tab 順序、Enter 送出、Esc 取消、焦點回到觸發鈕；錯誤時焦點到第一個錯誤欄。
  - 狀態：每個改過的清單各跑一次「載入中／空／錯誤（以 javascript_tool 讓 API 失敗）／有資料」。
  - 內容：長主機名（40 字）與長問題標籤在表格與頁首不破版。
  - 效能：`reduced-motion` 開啟時無動畫；輪詢在分頁隱藏時停止（network 請求數不增加）。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 規格修正（開工前） | Claude | — | — | E-1：`handler-detail.js` 的回覆判斷（本人＋Handle）本來就正確，問題只在交辦單詳情頁用 `viewerIsHandler`；原驗收「handler-detail.js 不得有 hasCapability(Handle)」刪除。處理人連結改為 `api.js` 一個 `canViewHandlerPage` 判斷，不另做 `format.js` 的 `handlerLink()` |
| E-1 | Sonnet 5.5（規格 task-56-e1） | 完成 | 0 警告；定向 478 綠；突變拿掉原處理人放行 → 1 案紅；20 檔編碼全對 | 偏離：補了白名單外的測試替身 `WorkOrderFakes.Clone`（不補新欄位會被丟掉）；曾是原處理人但具 Assign 的管理者仍保有管理動作；`host-detail.js`、`record-detail.js` 新增模組層級 `currentUser`。瀏覽器實測併入批次 E 結尾一次做 |
| E-2 | Sonnet 5.5（規格 task-56-e2） | 完成 | 0 警告；定向 631 綠；突變拿掉「最近處理過」排序優先 → 1 案紅 | 偏離（接受）：依問題交辦 modal 的範圍由後端解析（全站／群組可上千台），前端沒有主機清單，人選不標「看不到 N 台」——既有交辦預覽的 `AssigneeNoAccess` 已在選人後提示；改派／拆出只用已載入分頁的成員主機，超過一頁時「看不到」偏少；日層級指派沒有單一問題脈絡，不標「最近處理過」；`ContinuityDays` 經 InternalsVisibleTo 直接讀，未改 `DispatchContext` |
| E-3、E-4 | Sonnet 5.5（規格 task-56-e34） | 完成 | 0 警告；定向 637 綠；突變「只剩這台」判斷改恆真 → 1 案紅 | 偏離（接受，比規格好）：收單的查詢與判斷整段放進 `WorkOrderCoordinator.RetractDayAssignments`（同一把鎖、不替服務加相依）；新增「移出單一成員」與 `PreviewMembers`（預覽共用協調器同一份分類判定）。已知限制：案件跨日，同主機同問題由別日日層級指派建的案件，在本日取消指派時會一併收回（資料模型沒有逐日來源）。確認文字改為「另有 M 個問題…」以免數字對不上 |

## 8. 明確不做（本輪定案）

- 深色模式（DESIGN-SYSTEM §1 既有決定，寫進 BACKLOG）。
- 麵包屑（改規格）。
- 管理者與處理人的對話串／已讀逐人記錄（只做單層未讀，Assign 任一人開過即已讀）。
- 處理人逐日進度視圖（多日單仍以案件為單位）。
- 改派歷史完整鏈（只記最近一位前處理人；完整軌跡在時間軸事件）。
- 狀態目錄 API 端點（靜態模組＋反射對照已足夠；中文名是前端的事）。
- `runs.js` 拆檔（BACKLOG 既有條目，維護性收益，不在本輪）。
- 首頁「昨夜派工摘要」區塊（執行紀錄的 `NightlyDispatch.DescribeSummary` 一行已有，不重複投影）。

## 9. 規劃完成後複檢

- **與既有設計衝突**：F-1 推翻 WEB-SPEC §8.5「麵包屑」與 `Reports.cshtml` 自有 h1；F-2 推翻各頁自建表格的密度差異；F-7 推翻 DESIGN-SYSTEM §6b `col-xl-3`。皆已列規格同步。E-1 推翻 §9.4a 首段「全角色」。E-5 對處理人回覆關掉 `open`，管理視角的「調回未處理」保留。
- **批次間衝突**：E 與 F 同檔（四支交辦 JS、dashboard.js）已定順序 E→F；F-3 的 `.style.` 允許清單要包含 E-6 徽章的動態（無）。
- **與第 55 輪的相依**：E-4、E-7 入列 outbox 需要 55 輪 C-1 的 `EnqueueWorkOrderNotice` 與新 kind；E-8 的「立即通知」文案引用 55 輪 C-4；E-6 的輪詢與 55 輪 A-3 無關。
- **四個坑**：E-6「什麼算一個未讀」＝一張單，不是一則回覆；「一個都沒有」＝徽章不顯示。E-4 破壞性判準（取消單）反例＝多成員單只移出。E-1 單向閘門（唯讀開啟）繞過路徑＝原處理人再被指派回來自然恢復可回覆；再改派一次後更早的人失去存取是刻意的。F-3 移除類（inline style）依賴方＝動態量值以 CSS 變數保留。F-4 `button()` 不擲例外，退路＋warn。
- **使用者視角自查（尖銳但有理）**：「為什麼我回了管理者不知道」→ E-6；「派給看不到主機的人然後才警告我」→ E-2；「下拉一動就送出，我只是看看」→ E-3；「取消指派後單還掛在我這」→ E-4；「手機上回覆 modal 填不完」→ F-全＋§6 375px 實測；「按鈕按了 403」→ E-1；「每頁長得不一樣」→ F-1／F-2。
- **管理者視角自查**：「有多少沒人接」→ E-10；「誰回了沒看」→ E-6；「無法處理之後我只能改派？」→ E-7；「寄信有沒有寄到」→ 55 輪 C-2／outbox 失敗保留。
- **SOLID／KISS／DRY 自查**：候選判定複用 `DispatchCandidateSource`／`WorkOrderDispatcher`；狀態目錄一份；處理人連結一個出口；頁首、鍵值表、徽章計時器各一個共用元件；未新增抽象介面；`previous_handler_id` 比解析事件簡單且單一欄位。
- 複檢完成，第二版新增 E-10 與三條明確不做。
