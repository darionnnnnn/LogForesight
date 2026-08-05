# 回饋第十一輪規劃（FEEDBACK-11-PLAN）

> 2026-08-05 起案並於同日**全案實作完成＋全案體檢**（`feature/feedback-11`）。
> 基線 dev@997ade0（1356 測試綠）→ 完成後 **1384 測試綠**。
> 決策點見文中【定案】標記；實作與規劃的差異記於文末「實作後記」。

八項需求對照：§1 NetIQ 匯入併頁、§2 匯入頁只留負責人＋負責人可見範圍、§3 使用者詳細頁、
§4 使用者頁預設啟用、§5 停用阻擋登入（**現況已成立**）、§6 依問題統一標記、
§7 處理人工作頁問題視角、§8 全站視角盤點。

---

## 現況盤點結論（先講重點）

1. **§5 已經是現況**：`IdentityService.Login` 對 `!user.Active` 直接拒絕（稽核 `login_failed`／
   「此帳號已停用」），且 `ActiveUserMiddleware` 逐請求檢查、停用即時 401（WEB-SPEC §6.3）。
   本輪只需補驗證測試（若缺）與在計畫內申報，不需要改動。
2. **§2 的一半已經是現況**：`owners.csv` 負責人匯入已存在（`OwnerCsvImporter`），
   含「帳號不存在自動建立（User 空群組）」與「AD 首次登入補顯示名稱／Email」
   （`IdentityService.SyncFromAdIfNeeded`）。本輪新做的是**負責人可見範圍**與**頁面收斂**。
3. **§2 有一個隱藏的能力缺口**：自動建立的負責人**沒有任何群組 → 沒有任何 Capability**
   （`RoleCapabilityMap` 依群組角色給能力）。就算給了可見範圍，他登入後也**不能標記處理狀態**
   （沒有 `Handle`）。這一點必須一併定案（見 §2 待決策）。
4. **§6 與第十輪既有定案的關係已釐清（定案）**：統一標記**只處理尚未指派的項目**——
   已有進行中案件（不論處理人是誰）的主機一律略過並回報。這與 FEEDBACK-10 §8
   「他人處理中 admin 也擋、正確動作是改派」完全相容：已指派的走既有改派／回覆動線，
   統一標記負責的是「還沒有人接手、admin 直接下結論」的那一塊。

---

## §1 NetIQ 匯入併入「NetIQ 維護」頁

**現況**：`/admin/imports` 有「CSV 匯入｜NetIQ 匯入」兩分頁；NetIQ 分頁只剩
「選 Sentinel → 掃描精靈」（連線設定 2026-07-27 已搬去 `/admin/netiq`）。精靈的 markup 在
`Imports.cshtml`（`#netiq-wizard-modal` 等），邏輯在 `imports.js` 後半（約 410 行）。
API 全部已在 `/api/admin/netiq/*`（scan／import），**API 零改動**。

**改法**：

- `/admin/netiq` 分頁由「設定｜診斷」改為「**設定｜匯入｜診斷**」。「匯入」分頁＝
  搬過來的 scan picker＋精靈 modal，行為零改動。
- 前端拆檔：精靈邏輯抽成 `wwwroot/js/pages/netiq-import-wizard.js`（ES module），由
  `netiq.js` 匯入掛載——`netiq.js` 已 400 行，直接塞進去會變千行怪物；`imports.js` 砍半。
- `Imports.cshtml` 移除 NetIQ 分頁與精靈 modal；頁內既有的「Sentinel 設定請至 NetIQ 維護頁」
  提示改為「NetIQ 掃描匯入已併入 NetIQ 維護頁」導引（過渡期提示，之後可拿掉）。
- **匯入紀錄**：`ImportKind.Netiq` 的紀錄目前顯示在匯入頁的歷次紀錄表。規劃：
  NetIQ「匯入」分頁內顯示自己的紀錄（同一支 `GET api/imports/logs`，前端篩 `kind=Netiq`）；
  匯入頁的紀錄表保留全部 kind（歷史紀錄不搬家、不消失）。
- 文件：WEB-SPEC §9.9（移除 NetIQ 分頁段落，留指路）、§9.9a（新增「匯入」分頁一節）。

**影響面**：純 UI 搬遷，無 API／儲存／授權改動；`Maintain` 能力不變。
測試影響僅限文案／整合測試若有引用頁面結構者。

---

## §2 資料匯入頁只留「負責人」＋負責人可見範圍

### 2a. 頁面收斂：退役 users.csv／hosts.csv／group_access.csv

**現況**：CSV 分頁四卡（使用者／主機／群組授權／負責人）。

**改法**：匯入頁只留「負責人」卡＋歷次匯入紀錄。三個退役 Importer
（`UserCsvImporter`／`HostCsvImporter`／`GroupAccessCsvImporter`）連同範本、測試整組移除
（比照 console 退場的「整組退役」慣例，不留死碼）；`ImportKind` 保留舊值**僅供歷史紀錄
顯示名稱解析**（`KIND_NAMES` 保留），API `{kind}` 路由對退役 kind 回 `validation_failed`。

**退役後各功能的替代途徑**（規劃時逐一確認過，無孤兒功能）：

| 被退役的 | 替代途徑 |
|---|---|
| users.csv | 使用者頁「一次新增多筆」（上限 100/批）＋ owners.csv 自動建帳號 |
| hosts.csv 建主機 | 本機主機：批次分析自動 Touch 登錄；NetIQ 主機：掃描匯入精靈 |
| hosts.csv 設群組 | 主機頁批次設定群組（FEEDBACK-5 §8） |
| hosts.csv 設 OS／role_desc | 主機頁逐台編輯（量大時：NetIQ 匯入本就依 Sentinel 預填 OS） |
| group_access.csv | 群組頁授權矩陣 |

**已知損失（要明講）**：本機主機失去「第一次分析前預先建檔＋分組」的批次途徑——
上線初期若要預先掛群組授權，得等第一晚批次 Touch 之後再用主機頁批次分組。
兩千台情境主力是 NetIQ 掃描匯入，本機主機量少，評估可接受。

**【定案 2-1】直接整組退役**（2026-08-05）：主機主要來源即 NetIQ 匯入，三個 Importer
連同範本、測試、前端卡片整組刪除，不留無入口的程式；真要回來從 git 撈得回。

### 2b. 負責人可見範圍（本輪核心）

**現況**：授權鏈只有「使用者→使用者群組→授權矩陣→主機群組→主機」與案件授與兩條；
`OwnerCsvImporter` 還特地警告「負責人不會自動取得檢視權限」。

**改法**：`VisibilityService` 增加第三條路徑——**負責人路徑**：

- `GetVisibleHostIds()`／`GetVisibleHostIdsFor(userId)` 在群組授權結果之上，聯集
  「`WebHost.OwnerUserIds` 含此人、且 `Active`」的主機。停用主機照舊排除；
  墓碑列照舊經別名展開，不需入集合。
- 單一咽喉點改動 → 儀表板／問題查詢／報表／主機詳情／指派前檢查**自動**涵蓋，無逐頁改動。
- 與案件授與的關係：負責人路徑給的是**整台**可見（進 `GetVisibleHostIds`），
  自然不觸發 `IsCaseGrantOnly` 裁剪，兩機制不相撞。
- `OwnerCsvImporter` 的警告改為說明「負責人自動取得該主機檢視權」；
  WEB-SPEC §7.1 授權鏈圖與註解同步改寫。
- 主機頁／匯入預覽不變（負責人欄本來就有）。

**【定案 2-2】方案 A：隱含能力**（2026-08-05）——`ResolveCapabilities` 時若此人是
任一**啟用**主機的負責人，聯集 User 角色能力（`Handle`＋`ConfirmPermission`）。
不動群組模型、授權矩陣零噪音，「負責人天生是處理者」語意直接寫進能力解析。

實作細節：

- `IdentityService.ResolveCapabilities` 注入 `IHostStore`，加一段
  「`hosts.GetAll().Any(h => h.Active && h.OwnerUserIds.Contains(user.UserId))` → 聯集
  `RoleCapabilityMap.For(UserRole.User)`」。單點改動，登入與 `/api/auth/me` 自動生效，
  側欄／功能鈕顯示照既有能力機制走、零前端改動。
- **生效延遲同既有規則**：能力進 JWT——匯入當下已在線上的人，最遲重新登入才拿到
  `Handle`（可見範圍是逐請求解析、即時生效；能力異動本就接受 token 效期內延遲，§6.2）。
- 隱含能力**不含 ViewAll**，`GetVisibleHostIdsFor` 的 ViewAll 判定（走群組角色）不受影響。
- WEB-SPEC §7.1 的 RoleCapabilityMap 段補「負責人隱含能力」一節。

### 2c. 既有負責人資料的回溯

owners.csv 已上線過，`OwnerUserIds` 可能已有資料。可見範圍是即時解析，**不需要遷移**；
方案 A 的隱含能力同樣即時解析，不需要遷移。

---

## §3 使用者詳細頁（點使用者看全貌）

**現況**：使用者頁只有清單＋編輯 modal；WEB-SPEC §9.8 寫的「個人操作紀錄與最近登入頁籤」
**實際上並未實作**（程式碼無對應物，規格此句需修正）。工作負載已有現成整包：
`GET api/handlers/{userId}/workload`（進行中案件＋被指派風險日＋KPI，§9.4a）。

**改法**：新增 admin 子頁 `/admin/users/{id}`（`Maintain`；使用者頁清單點列進入），區塊：

1. **基本資料**：帳號／顯示名稱／Email／狀態／所屬群組（含角色徽章）／**上次登入時間**。
2. **可見主機**：`GetVisibleHostIdsFor` 的結果列表（名稱／IP／群組／**來源徽章：群組授權
   或 負責人**——兩條路徑分開標，回答「他為什麼看得到這台」）；列點擊 → `/hosts/{id}`。
3. **處理中項目**：重用 workload 的進行中案件表＋未結案風險日表（列點擊 → 風險日詳情）。
4. **已處理項目**：已結案案件（`IIssueCaseStore` 該人歷史案件）＋「近 30 天已結案風險日」
   （workload 既有 `includeResolvedDays` 開關）。
5. **被指派歷程**：以 `IssueCase` 為事實來源——建案（何時、誰指派、哪台哪個問題）、
   改派（`case_reassign` 進出兩向）、結案，時間軸呈現。**刻意不用稽核表反查**
   （detail JSON 比對脆弱、且受稽核保留天數截斷；案件本身就是指派的第一手紀錄）。
6. 頁頂放「開啟工作頁」連結 → `/handlers/{userId}`（全角色視角的同一個人）。

**上次登入時間**：`lf_users` 新增 `last_login_at`（nullable timestamp），
`IdentityService.Login` 成功時更新。**不從稽核推導**——每列使用者都掃一次稽核太貴、
且受保留天數影響會「登入過卻查不到」。DB-SPEC 補欄位（JSON 後端缺欄容忍、零遷移；
SQL 後端依既有 Schema 升級規則加欄）。使用者清單頁順帶加「上次登入」欄（排序用得上）。

**【定案 3-1】獨立 admin 頁 `/admin/users/{id}`**（2026-08-05，依建議執行）：
`/handlers` 是全角色頁、資料以檢視者範圍過濾；可見主機與上次登入是管理視角資訊
（以被看者為準），混在同一頁會讓「這頁以誰的範圍過濾」變成兩套規則疊在一起。
權限限 `Maintain`（manager 看不到本頁；要放寬再議）。
API：`GET api/admin/users/{id}/detail`（組合基本資料＋可見主機＋案件歷程；
工作負載區塊前端另打既有 `api/handlers/{userId}/workload`，不重複第二套投影）。

---

## §4 使用者頁預設只看啟用

`users.js` 的 `statusFilter` 初始值 `''` → `'active'`。chip 仍可切「全部／停用」。
含 §3 的清單改動一併驗收。一行等級，無後端影響。

---

## §5 停用使用者阻擋登入 — 現況已成立，無改動

- 登入面：`IdentityService.Login` 擋 `!user.Active`（訊息「此帳號已停用」、稽核 denied）。
- 生效面：`ActiveUserMiddleware` 逐請求檢查，停用即時 401（不等 token 過期）。
- serverAdmin 不在 `lf_users`、不受影響（設計如此，是救援帳號）。
- 本輪動作：確認測試覆蓋此二路徑，缺則補；WEB-SPEC 不需改。

---

## §6 依問題視角的「統一標記」（admin，限尚未指派的項目）

**現況**：by-issue 視角已有「批次指派」（`Assign`）與「回覆處理狀態」（`Handle`，
限自己名下案件）。缺的是「不經指派，admin 直接把整個問題在**還沒有人接手**的主機上
標成結論」的動作。

**【定案 6-1】只處理尚未指派的項目**（2026-08-05）：已有進行中案件的主機——不論處理人
是誰、包含 admin 自己——一律**略過並列入回報清單**。與 FEEDBACK-10 §8「他人處理中
admin 也擋」不牴觸：已指派的走既有改派（§9.3-17）／回覆（bulk-status）動線，
統一標記只負責「無人接手」的那一塊。不做任何「連同他人案件一併結案」的旁路。

**【定案 6-2】範圍＝目前篩選期間，且 modal 必須顯性告知**（2026-08-05）：
只對 by-issue 目前篩選的 `from`～`to` 期間內的紀錄下結論；modal 以醒目提示列明
「本次僅處理 yyyy-MM-dd ～ yyyy-MM-dd 期間內的紀錄」＋受影響主機×日統計，
操作的人必須看得到範圍再按下去。不提供全歷史結案（爆炸半徑太大）。

**改法**：by-issue 列內新增「統一標記」（與「指派」並排，僅具備能力者顯示）：

- **能力**：`Assign`＋`Handle` 同掛（`[Permission]` AllowMultiple 疊加＝都要滿足；
  實務上只有 admin 兩者兼具，與需求「admin 使用者」一致，不必為此開新能力）。
- **「尚未指派」的精確定義**（【定案 6-3，2026-08-05：無案件時以 admin 操作為主】；
  單點寫在 service，前端預覽與後端落盤同一套）：
  - 該（主機, 問題）**存在進行中案件** → 整台略過（回報「已由 ○○○ 處理中」）。
    案件＝指派的事實來源，這是唯一的略過條件。
  - **無案件**時，期間內該問題的逐日標記即使是 `in_progress`／`observing` 也**一併覆蓋**
    ——admin 的統一標記為主。覆蓋照走 `ApplyIssueStatus` 逐筆寫歷程（原標記者事後
    在歷程查得到「誰、何時、把處理中改成了什麼結論、原因」），預覽的主機明細
    對這類主機標註「將覆蓋 N 天處理中／觀察中標記」，admin 按下去之前看得到。
  - **已是結案類的日子不動**（已有結論，不重寫歷程）；未標記／`open`／預設不處理／
    自動雜訊照常納入標記。
- **modal**：狀態單選（已處理／不處理／誤報／已知雜訊，僅結案四態）＋**原因必填**
  （四態一律必填——這是代全體下結論的操作，理由是紀錄的一部分）＋期間提示（定案 6-2）＋
  受影響主機×日預覽與略過名單預覽（擴充 `GET api/handling/issue-cases/preview` 回傳
  各主機的「將標記天數／略過原因」，前端開 modal 即看得到誰會被跳過，不是套用後才知道）。
- **API**：`POST api/handling/issue-cases/bulk-close`
  （`{source, eventId, from, to, status, note}`；能力標註同上）。
- **落盤語意——完全走既有咽喉，不開第二套狀態機**：逐主機逐未結案日走既有
  `ApplyIssueStatus` 同一條路徑（逐筆寫歷程 `issue_status`、`IssueLabel` 反正規化、
  同批共用同一個 `occurredAt` 時間戳供 timeline 分組）。
  - `known_noise`：逐主機寫 `NoiseMark` 記憶（與詳情頁行為一致）——四態中唯一
    「之後同問題自動有結論」的；modal 中明講。
  - `false_positive`：套用成功 toast 附「前往規則維護」導引（治本在規則，與詳情頁一致）。
  - **「規則沒變會再出現」**：modal 常駐說明「本操作只對上列期間內的既有紀錄下結論；
    規則未調整前，之後的新日子仍會產生同類問題（已知雜訊除外——有記憶會自動標示）」。
    誠實邊界，不做「未來自動套用」的隱形規則。
- 稽核：新 action `issue_bulk_close`（含問題、期間、狀態、原因、標記主機×日數、略過清單）。
- 套用結果 toast＋結果 modal：成功 N 台 M 日、略過清單（含原因），與批次指派的回報模式一致。

---

## §7 處理人工作頁：預設「依問題」視角

**現況**：`/handlers/{userId}`（§9.4a）＝進行中案件表（一列＝主機×問題）＋被指派風險日表。
被交辦「同一問題 × N 台主機」時要在 N 列之間來回。

**改法**：頁內加視角切換（chip：**依問題（預設）**｜依主機）：

- **依問題**：進行中案件依 `IssueKey` 分組，一列一問題——問題（`IssueLabel`）｜主機數｜
  狀態彙總｜最早指派～最近出現｜逾期標記。**點列就地展開**主機明細（主機名 → `/hosts/{id}`、
  最近出現日 → 風險日詳情、各主機狀態／預計完成日），同 §9.2 by-issue 的展開手勢。
- **本人檢視時**（我的交辦），每個問題列提供「回覆處理狀態」——**直接重用**既有
  `POST api/handling/issue-cases/bulk-status`（FEEDBACK-10 §11，本來就是「一次回覆
  自己名下該問題全部案件」的端點），前端從 records 頁抽共用 modal。看別人的頁不顯示
  （該端點語意即「自己名下」，不代人回覆）。
- **依主機**：現行兩表原樣保留。
- 被指派風險日表（日層級指派、無案件者）不屬於問題分組：兩視角**共用**、固定放頁面下半
  （原樣）。KPI 列不動。
- 後端：workload DTO 已含逐案件的 IssueKey／IssueLabel，分組可**純前端**完成；
  若展開需要「最近出現日」以外的細節再評估是否後端補欄位（傾向後端一次算好，避免 N+1）。

---

## §8 全站視角盤點：問題 → 主機 → 日期

逐頁盤點現況與提案（8-1／8-2 已定案納入本輪，見下）：

| 頁面 | 現況主視角 | 評估 |
|---|---|---|
| 問題查詢 `/records` | **問題**（第九輪已定為預設）→ 主機 → 日期 → 明細 | ✅ 已符合，不動 |
| 風險日詳情 | 日（主機×日終端頁） | ✅ 合理——它是所有下鑽的終點，內容已以問題分節 |
| 主機詳情 `/hosts/{id}` | 主機（含期間問題彙總） | ✅ 第二視角入口，已有問題彙總表，不動 |
| 處理人工作頁 | 主機×問題列 → 本輪 §7 改問題優先 | 🔧 §7 處理 |
| 儀表板 `/` | **類別**統計卡＋主機排行＋群組概況 | ⚠️ 見提案 8-1 |
| 報表 `/reports` | 趨勢（日）＋類別分布＋**主機排行** | ⚠️ 見提案 8-2 |
| 排程作業／稽核／權限異動 | 維運與紀錄頁 | ✅ 不適用視角原則 |

**【定案 8-3】8-1／8-2 納入本輪實作**（2026-08-05）。

- **8-1（儀表板）「重點問題 Top 5」卡**：期間內依問題聚合——問題（`IssueLabel`）／
  最高嚴重度／主機數／未處理數，點列下鑽 `/records` by-issue 帶 `eventId`＋`source` 篩選。
  位置放**高風險主機排行之前**（問題排在主機前，呼應視角順位）；類別統計卡保留——
  類別是 8 類固定的「儀表板量表」，與問題卡回答不同問題，不互斥。
  後端併入 `GET api/dashboard/summary` 一次回傳（首頁單請求原則），資料重用
  `SearchByIssue` 的聚合投影（可見範圍／嚴重度可見性／日風險顯示過濾自動繼承，
  取前 5 筆即可，不做分頁）。serverAdmin 引導卡邏輯不動（本就不打 summary）。
  「今日無風險訊號」全綠狀態的判定不因新增卡片改變。
- **8-2（報表）問題排行【定案 2026-08-05：採卡內切換，以畫面整體美觀為準】**：
  自第一輪的「自訂圖表新卡、預設關閉」修訂為「主機告警排行」卡**雙模式切換**——
  卡 header 加「主機｜問題」toggle（同報表其他工具鈕樣式），問題模式＝水平長條 Top 10
  「問題×告警次數」＋「其他 N 個問題」聚合列，點長條下鑽 by-issue；狀態存
  `localStorage`（預設主機模式，既有畫面零變化）。
  理由：報表一頁化的高度分配（FEEDBACK-10 §5 剛以 flex 由外而內算定）容不下
  常駐第五卡；掛在自訂圖表 modal 裡「預設關閉的新卡」開啟後仍要擠進第二列、
  高度重新分配一樣破功。同卡切換不動任何高度計算，也正好呼應「同一個排行、
  兩種視角」的本輪主題。後端：報表 API 增 `topIssues` 聚合（與 8-1 同一套投影邏輯，
  抽共用私有方法，不做第二套分組規則）。
- **不改的**：把日期視角再往後壓（如拿掉 by-date）沒有需求支撐，維持四視角。

---

## 實作順序

1. §4＋§5（半天內：預設篩選＋測試補強）
2. §2b 負責人可見範圍＋隱含能力（核心、其他項的前提）
3. §2a 匯入頁收斂 → §1 NetIQ 併頁（同一批 UI 搬遷）
4. §3 使用者詳細頁（吃 §2b 的 GetVisibleHostIdsFor 與 last_login_at）
5. §7 工作頁問題視角
6. §6 統一標記
7. §8-1 儀表板重點問題卡 → §8-2 報表排行切換（共用聚合投影，接在 §6 之後做，
   by-issue 相關程式碼都熱著）
8. 全案體檢＋文件同步（WEB-SPEC §6.2/§7.1/§9.1/§9.2/§9.4a/§9.6/§9.8/§9.9/§9.9a、
   DB-SPEC lf_users）

## 測試計畫

- VisibilityService 負責人路徑（含停用主機／停用使用者／ViewAll 不受影響／GetVisibleHostIdsFor）
- 能力解析（負責人隱含 Handle＋ConfirmPermission；非負責人不受影響；不含 ViewAll）
- 登入阻擋回歸（§5，若現無測試則補：停用帳號 Login 拒絕＋Middleware 401）
- last_login_at 寫入與兩後端讀寫
- bulk-close：期間範圍、進行中案件略過（自己／他人皆略過）、無案件的 in_progress／
  observing 標記被覆蓋且歷程可追（定案 6-3）、已結案日不重寫、NoiseMark 寫入、
  歷程逐筆＋共用時間戳、稽核
- preview 擴充：略過原因、將標記天數、「將覆蓋處理中標記」註記的正確性
- dashboard TopIssues／報表 topIssues 聚合（可見範圍過濾、Top N 截斷、其他聚合列）
- workload 依問題分組投影（若後端補欄位）
- 匯入退役後：退役 kind 的 API 拒絕、Owners 匯入回歸、歷史紀錄顯示

---

## 實作後記（2026-08-05 完成，1384 測試綠，+28）

### 與規劃不同之處（都在實作時才看得清楚）

1. **§3 使用者詳細頁：停用帳號一律顯示為「無能力」**。規劃沒寫這條；實作時測試先紅——
   停用使用者仍會列出群組角色帶來的 Handle／ConfirmPermission。但停用帳號連登入都進不來
   （`IdentityService` 擋、`ActiveUserMiddleware` 逐請求 401），列出「他可以標記處理狀態」
   是誤導，且與同頁「可見主機為空」互相矛盾。改為停用即視為無能力。
2. **§3 被指派歷程沒有「已改派走」的列**。`IssueCase` 只保存目前處理人，改派後
   `GetByHandler` 就查不到那筆——原本 DTO 設計了 `StillHandler` 欄位，實作時發現它恆為 true
   （永遠不會有 false 的資料來源），是個會說謊的欄位，直接拿掉並在 DTO 註解寫明這個誠實邊界。
3. **§8-1 儀表板重點問題卡不含「未處理數」**（規劃原列了這一欄）。那需要逐問題查 handling
   標記，是依問題視角才做的事；排行卡回答「哪幾個問題影響最大」，點進去就看得到處理概況。
   維持與 `BuildHostRanking` 同樣的純紀錄聚合，不為一張卡多拉一份跨期間的標記查詢。
4. **§7 的 bulk-status modal 抽成共用元件** `pages/issue-status-reply.js`（規劃只寫「前端從
   records 頁抽共用 modal」，實作確認兩頁必填規則必須同源，否則遲早漂移成兩套）。
5. **§6 預覽做成獨立端點 `close-preview`，不是擴充既有 `preview`**（規劃原寫「擴充
   `GET api/handling/issue-cases/preview`」）。既有 preview 服務批次指派（回「既有處理人」
   供改派勾選），統一標記要的是「將標幾天／覆蓋幾天／略過原因」——兩組回傳形狀完全不同，
   硬併成一個端點會變成帶模式參數的雙面 DTO；且兩者能力標註不同（Assign vs Assign＋Handle），
   分開才對得上各自的 controller。
6. **退役範圍比規劃再多一層**：`ImportChangeHelpers`、`ImportRowAction.Remove`、
   `ImportPlan.NewGroups/RemoveCount`、`ImportResult.Removed/CreatedGroups` 隨三個 Importer
   一併移除——它們的產生端全部消失，留著就是「永遠為 0 的欄位」寫進稽核明細。
   `ImportKind` 的三個列舉值刻意保留（歷史匯入紀錄要顯示名稱）。

### 全案體檢揪出並修掉的四處

1. **統一標記在兩千台規模會炸**：`PlanBulkClose` 原本逐主機呼叫 `IIssueHandlingStore.GetMany`，
   而整份 blob 型的 store 每次都是一次讀取＋反序列化——兩千台的問題＝兩千次全表掃描。
   改成一次撈完再依主機分組（同 `GetTodo` 的作法）。
2. **NetIQ 匯入分頁重複請求**：設定與匯入同頁後，`netiq.js` 與精靈模組各查一次
   `/api/admin/sentinels`。改為由 `netiq.js` 把已取回的清單傳給 `refreshScanPicker(sentinels)`，
   匯入成功則回呼 `reloadSentinels`（主機數會變）——單向資料流，一次往返。
3. **殘留死碼**：`BulkCloseIssue` 迴圈裡的 `_ = row;`、`ImportsController` 的退役 kind 檔名對應
   （`GetTemplate` 早已擋下，那幾個分支永遠走不到）。
4. **`issue_bulk_close` 漏了稽核動作中文對照**：`AuditQueryService.ActionNames` 沒補的話，
   稽核頁的動作欄與篩選下拉會顯示原始代碼——補「統一標記問題」（動作下拉吃後端
   `GetActionNames()`，補字典即自動出現）。

### 已知邊界（刻意不做）

- 統一標記的**期間過濾**由 `IRecordRepository.Query` 負責，而 `FakeRecordRepository` 刻意忽略
  filter（既有設計，見其類別註解），因此該組測試釘的是「哪些主機／哪些天會被標」的規則本身；
  期間過濾另由 RecordRepository 的查詢測試涵蓋。
- 負責人隱含能力經 JWT 生效，**已在線上的使用者需重新登入**才拿到 `Handle`（可見範圍即時生效）。
  owners.csv 預覽的提醒文字已明講這件事。
