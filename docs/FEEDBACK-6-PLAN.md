# 回饋第六輪規劃（FEEDBACK-6-PLAN）——排程 Web 化落地與 console 退役

> 規劃日期：2026-07-31。狀態：**Q1~Q6 已全數定案（見 §6 定案紀錄），可開工；
> 尚未實作**。五項回饋：(1) NetIQ 維護頁「詢問 AI 即時查詢」設定文字已與
> Phase 1 後的實際行為不符；(2) 沒有排程時間設定頁面、也沒有手動觸發執行的
> 位置（追加：左側選單也要有入口，見 §2 側欄小節）；(3) 已經不使用的專案與
> 檔案自解決方案移除；(4) 說明文字非必要常駐顯示的改為 icon 滑過顯示
> （第五輪 §6 的二次收斂）；(5) 全站用詞檢視（隨 Q3 答覆擴充：以官方用詞或
> 一般台灣 IT 用詞為主，檢視整個網站）。
>
> **(2) 與 (3) 不是新設計——正是 docs/WEB-SCHEDULER-PLAN.md 已定案的
> Phase 2~5**（九項問題 2026-07-31 全數拍板，設計細節齊備）。本文件的職責是：
> 確認該規劃對照目前程式碼仍然成立、界定本輪範圍與順序、補上 (1) 這個
> Phase 1 收尾遺漏與 (4)(5) 的範圍與基準。

| # | 項目 | 對應 | 規模 |
|---|------|------|------|
| 1 | NetIQ 頁 ChatLiveFetch 設定文字過時 | Phase 1 收尾遺漏，本文件 §1 | 極小 |
| 2 | 排程設定頁面＋手動觸發＋側欄入口「排程作業」 | WEB-SCHEDULER-PLAN **Phase 2＋3**，本文件 §2 | 大 |
| 3 | 移除不使用的專案與檔案 | WEB-SCHEDULER-PLAN **Phase 4＋5**，本文件 §3 | 大 |
| 4 | 說明文字二次收斂（非必要常駐 → icon） | 第五輪 §6 續作，本文件 §4 | 小中 |
| 5 | 全站用詞檢視（官方用詞／台灣 IT 慣用詞） | 本文件 §5 | 小中 |

---

## 1. NetIQ 頁「詢問 AI 即時查詢」設定文字過時

### 現況與成因

Phase 1（風險 log 暫存 DB）上線後，「詢問 AI」的取數順序已是**先查
`lf_risky_events` 暫存（毫秒級、本機與 NetIQ 主機皆有、不受開關影響），查無才
fallback Sentinel 即時查詢**（`RiskyEventLookupService`，見 WEB-SCHEDULER-PLAN
§2.2.4）。後端註解、WEB-SPEC §9.3／§9.9a 當時已同步改寫，但 **NetIQ 維護頁的
UI 文字漏了**（`Netiq.cshtml` 86~92 行）：

- checkbox 標籤仍寫「詢問 AI 詢問當下向 Sentinel 即時查詢現場事件（僅 NetIQ 主機）」
- form-text 仍寫「對話的第一輪會對該主機所屬 Sentinel 發一次即時查詢」

讀起來像即時查詢是唯一/主要路徑，與實際行為矛盾——這正是使用者回報的困惑點。

### 全站掃描結果（2026-07-31）

過時的**使用者可見文字只有 Netiq.cshtml 這一處**。其餘皆已正確：

- `chat-panel.js` 的「已取回現場事件 N 則納入分析」刻意不標注來源（Phase 1
  體檢時已定案：事件多數來自暫存，標注來源會說謊）——不改。
- `AiController`／`AiInsightService`／`RiskyEventLookupService` 註解均已寫明
  DB-first＋fallback——不改。
- WEB-SPEC §9.3／§9.9a 已於 Phase 1 更新（§9.9a 明寫「2026-07-31 起此即時查詢
  降為 fallback」）——不改。

### 做法（保留開關、改寫文字）

**開關本身仍有真實作用**（它把關的是「查無暫存時要不要打 Sentinel」這條
fallback 路徑——白天對 Sentinel 的額外查詢負載這個顧慮沒有消失），不拿掉；
只把標籤與說明改寫成反映兩層語意：

- 標籤改為：「詢問 AI 查無暫存資料時，向 Sentinel 即時查詢現場事件（僅 NetIQ 主機）」
- form-text 改為（要點）：對話第一輪**先查風險 log 暫存資料庫**（毫秒級、
  不打 Sentinel、本開關管不到）；只有暫存查無（超過保留天數、功能上線前分析的
  日子、不屬風險簽章）才用到本開關——開啟時 fallback 向該主機所屬 Sentinel
  發一次即時查詢。既有的節流說明（併發 1、快取 10 分鐘、僅 NetIQ 主機）保留。

### 影響確認

- 純文字改動，零行為變化、零 API 變化；`NetiqOptions.ChatLiveFetchEnabled`
  欄位與序列化不動。
- 不需要新測試；文件面 WEB-SPEC 已是對的，不用再動。
- 措辭基準（2026-07-31 定案 Q3）：以**官方用詞或一般台灣 IT 用詞**為主；
  並隨此答覆擴充出 §5 全站用詞檢視。

---

## 2. 排程設定＋手動觸發（＝WEB-SCHEDULER-PLAN Phase 2＋3）

### 需求對應

使用者要的兩件事在 WEB-SCHEDULER-PLAN 全部已設計定案：

| 使用者回報 | 既有定案 |
|---|---|
| 沒有排程時間設定的頁面 | §1.4.3 `ScheduleOptions`（多窗口，上限 4，跨午夜支援）＋§1.4.5 UI：**併入執行監控（Runs）頁**頂部「排程設定」卡（回饋第五輪 Q6 也再次確認過位置） |
| 沒有手動觸發執行的位置 | §1.4.4 手動觸發 API（run-preview／run／cancel／status；範圍 all/segment/host；≥50 台加強警示；不受時間窗限制）＋Runs 頁「立即執行」鈕＋主機詳情頁「指定主機更新」鈕 |

### 對照目前程式碼的有效性確認（2026-07-31 重新核過）

規劃距今雖只有一天，但中間過了回饋第五輪 12 個 commit，逐點確認無失效：

1. **搬遷清單仍準確**：`LogForesight/Service/` 現存 16 檔，其中
   `RuleBootstrapper` 已提前搬 Core（FEEDBACK-5 §10，規劃 §1.4.1 已註記），
   剩餘待搬 11 檔＋不搬的 CLI 類 4 檔（`SelfTestRunner`／`HostListCli`／
   `SuppressionCli`／`NetiqProbeCli`）＋`RuleImporter`（Phase 4 拆純函數）。
2. **Runs 頁結構未變**（61 行 cshtml＋403 行 runs.js）：頂部插「排程設定」卡
   無衝突；第五輪的表格排序等改動不影響此頁。
3. **測試基準線更新為 1214**（規劃寫 1163——第五輪淨增 51 個）；Phase 2
   「只搬不改」的閘門改以 1214 全綠為準。
4. **`Program.cs` 已 924 行**（規劃寫約 890——Phase 1 掛接風險事件寫入所致），
   抽 `AnalysisOrchestrator` 時多帶這一段，掛接語意不變。
5. **Web `Program.cs` 啟動區**第五輪加了規則庫 bootstrap——
   `SchedulerHostedService` 註冊與它同區共存，無衝突。
6. 第五輪的 modal 寬版（§7）與 help icon（§6）規範適用於本輪新 UI：
   「立即執行」確認對話框、排程卡的欄位說明照 WEB-SPEC §8.6 第 9~11 條做。

### 本輪範圍界定

- **Phase 2（服務搬遷，只搬不改）**：§1.4.1 清單 11 檔搬 Core＋§1.4.2 抽
  `AnalysisOrchestrator`／`IRunConsole`／ct 貫通本機迴圈／具名 Mutex 保留／
  `OrchestratorResult`。驗收：console 行為（含彩色輸出）逐字不變、1214 測試綠。
  獨立 commit 群、單獨可回退。
  **相依補充（2026-07-31 核對 csproj）**：三專案同為 `net8.0-windows`，Core 已有
  `System.Diagnostics.EventLog`／NLog／Polly——搬遷唯一要補的套件是
  `PermissionMonitorService` 用的 `System.IO.FileSystem.AccessControl`（自
  console csproj 移入 Core）；console 的 `System.DirectoryServices.AccountManagement`
  是 AD 驗證用、Web 已自有，不隨搬遷動。
- **Phase 3（排程引擎＋UI）**：§1.4.3 `ScheduleOptions` blob＋`ScheduleCalculator`
  純函數（多窗口/跨午夜/重疊驗證/漏跑補償全部可單測）、`SchedulerHostedService`、
  §1.4.4 四支 API、§1.4.5 Runs 頁排程卡＋主機詳情頁觸發鈕、`BatchRun.Trigger`
  欄位、§1.4.6 Web appsettings 新區段、§1.4.7 權限監控基準目錄說明、
  §1.4.8 部署文件（Event Log Readers）。
- **`Enabled` 預設 false**：部署本身零行為變化，schtasks 續用——使用者何時
  切換由 §3 的試點流程決定。

### 側欄入口與權限（2026-07-31 追加：回應「左側選單沒有排程入口」）

使用者指出左側選單沒有排程設定相關入口。盤點後發現這不只是命名問題，
還牽出一個**權限缺口**：

**現況**：側欄「系統」區的「執行監控」（`/runs`）掛 `DevMonitor` 能力
（dev＋admin 持有）；排程設定與手動觸發規劃為 `Maintain`（admin＋serverAdmin
持有）。兩個集合交集只有 admin：

- dev：進得了執行監控頁，但**不該**能改排程／觸發執行（無 Maintain）✓ 語意正確
- admin：兩者皆有 ✓ 無問題
- **serverAdmin：有 Maintain 卻進不了 `/runs`**——排程設定放在執行監控頁的話，
  救援帳號在全新環境完成初始設定時搆不到排程，且側欄完全看不到入口 ✗

**方案比較**：

| 方案 | 作法 | 問題 |
|---|---|---|
| **A（採用）** | 側欄項目改名「執行監控」→**「排程作業」**（名稱為 2026-07-31 定案 Q5）；`/runs` 頁面權限放寬為 **DevMonitor 或 Maintain（任一）**；頁內排程卡依能力分層顯示 | 無——見下方細節 |
| B | 系統管理區另加「排程設定」項連到 `/runs` | 兩個側欄入口指同一頁，active 高亮同時亮兩條，混淆 |
| C | 獨立 `/admin/schedule` 頁 | 推翻第五輪 Q6 定案（排程 UI 維持執行監控頁）；且把「設定排程」與「看它有沒有跑」拆兩頁，違反同一視野的原設計 |

**方案 A 細節**：

1. **側欄**：「執行監控」改名**「排程作業」**（icon `activity` 不變，
   位置仍在「系統」區；頁面標題與麵包屑同步改）——名稱直接回答
   「排程設定在哪」，「作業」同時涵蓋排程設定、手動觸發與執行紀錄三件事。
   `layout.js` 的 nav `requires` 由單一能力字串擴為**支援陣列（任一命中即顯示）**，
   此項掛 `['DevMonitor', 'Maintain']`。
2. **頁面權限**：`PagesController.Runs()` 的 `[Permission]` 同步放寬——
   `PermissionAttribute` 擴為接受 params 多能力（任一持有即過，attribute 建構子
   簽名相容既有單能力用法，其餘頁面零改動）。
3. **頁內能力分層**（前端依 `hasCapability` 顯示＋後端各 API 自行把關，雙層）：
   - 排程卡的**唯讀狀態**（下次觸發時刻、目前執行中/閒置、觸發來源）：
     這本來就是「監控」資訊，dev 看得到；
   - 排程卡的**編輯欄位**（Enabled 開關、窗口編輯、DebugDump）與
     **立即執行／停止**鈕：僅 Maintain 顯示；
   - 執行總表／異常彙總等既有監控區塊：維持現狀（頁面能進就看得到——
     serverAdmin 因此多看到執行紀錄，屬監控資訊非業務資料，可接受且對
     救援診斷有益）。
4. **API 權限微調**（相對 WEB-SCHEDULER-PLAN §1.4.4 的兩處刻意偏離）：
   - `GET /api/admin/schedule/status` 由 Maintain 放寬為 **DevMonitor 或
     Maintain**——dev 的排程狀態列要有資料可渲染；`run-preview`／`run`／
     `cancel` 與設定讀寫維持 **Maintain**。
   - 既有 Runs 資料 API（`RunsController`，目前類別層級 `DevMonitor`）同步
     放寬為**任一**——否則 serverAdmin 進得了頁面卻拿不到執行總表資料，
     等於只放寬半套。
5. **文件**：WEB-SPEC §7.1 能力表、§9.9（Runs 頁）與側欄清單同步更新；
   WEB-SCHEDULER-PLAN §1.4.5 補註記指向本節。

**取捨說明**：放寬 `/runs` 給 Maintain 等於讓 serverAdmin 多看到執行監控資料。
serverAdmin 的設計原則是「依用途給權」（救援＋初始設定）——排程設定屬初始
設定的一部分，執行紀錄是確認排程活著的必要回饋，兩者都在用途內；業務資料
（儀表板/問題查詢/報表）仍然一項都看不到，最小授權的實質未被稀釋。

---

## 3. 移除不使用的專案與檔案（＝WEB-SCHEDULER-PLAN Phase 4＋5）

### 「不使用」的盤點結果（2026-07-31 全案掃描）

先講結論：**現在就真正無用的檔案，只有功能已被 Web 完全涵蓋的兩個 CLI 類**；
console 專案整體「看起來不使用」但**還不能刪**——它目前仍是每晚分析的唯一
執行載具，移除的前置條件正是 §2 做完。逐類盤點：

| 候選 | 判定 | 依據 |
|---|---|---|
| `SuppressionCli.cs`／`HostListCli.cs` | **可立即刪**（見下方建議） | 功能已被 Web 規則頁「告警抑制」分頁與主機頁完全涵蓋，WEB-SCHEDULER-PLAN 定案 #9 本就判「直接刪」；留著是第二套會漂移的入口 |
| console 專案（`LogForesight`） | **Phase 5 才能刪** | 排程/分析還在它身上；移除閘門＝Web 排程試點 ≥5 晚驗證通過（定案 #5） |
| `SelfTestRunner.cs` | 隨 console 刪（Phase 5） | 定案 #9：selftest 接受退役；退役前仍是部署驗證工具 |
| `NetiqProbeCli.cs` | Phase 4 搬 Web 後隨 console 刪 | probe 是 Linux Sentinel P3 閘門的載具，**必須先有 Web 替代**（診斷分頁）才能刪 |
| `RuleImporter.cs` | Phase 4 拆 Core 純函數後，CLI 薄殼隨 console 刪 | 規則升級 SOP 的現行入口，先建 Web 入口再退 |
| 19 個頁面 JS／wwwroot 靜態資源 | 無孤兒 | 逐檔核對：每個 pages/*.js 都被 cshtml 或其他模組引用；css/img/lib 皆在用 |
| docs/ 11 份文件 | 全數保留 | 逐份核對引用數（RULES-PLAN 51 處、DB-PLAN 22 處、NETIQ-API-PLAN 31 處、LINUX-RULES-PLAN 34 處、FEEDBACK-3/4 各 34/41 處——程式碼註解大量指回這些文件，是活的參照不是遺物） |
| 兩份 `appsettings.json`（批次/Web） | 過渡期並存，Phase 5 移除批次那份 | §1.4.6 定案：隨部署的基礎設施參數，維持檔案配置 |

### `SuppressionCli`／`HostListCli` 提前到本輪首段刪除（Q1 已定案：提前刪）

定案 #9 已判這兩個 CLI「直接刪」，原排程是隨 Phase 5 一起；提前刪的理由：

**引用覆核（2026-07-31）**：兩類別的程式引用只有 `Program.cs` 的 CLI 分派段，
**零測試相依**；`StoreHostListProvider`（1.4.4 run-preview 要複用的清單語意）
在 `HostListProviders.cs`，是另一個檔案，不受影響。文件引用僅
HISTORY.md／RULES-PLAN.md 的歷史紀錄段（紀錄不回溯改寫，維持原文）；
README 的 `--suppress`／`--host-list` 兩節屬現行操作指引，同 commit 刪除
並改指 Web 對應頁面。

- 使用者本輪明確要求移除不使用的東西，這兩個是**現在就能安全刪**的全部。
- Phase 2 的「console 行為逐字不變」驗收指的是**分析管線輸出**；棄用的 CLI
  參數移除是已定案的功能退場，不在該不變式範圍（README 同步刪
  `--suppress`／`--host-list` 兩節即可，SOP 指向 Web 頁面）。
- 風險：若有人在伺服器上仍用這兩個指令操作——但 Web 抑制分頁/主機頁
  2026-07 起就是主要入口，README 也早標注 CLI 為「沒有 Web 時」的備援。

### Phase 4／5 範圍（依 WEB-SCHEDULER-PLAN 原設計，無變更）

- **Phase 4（CLI 職責搬 Web）**：§1.4.9 規則升級（`RuleImportPlanner` 拆
  Core＋Web 規則頁橫幅/預覽/套用＋CLI 對等測試）、§1.4.10 AI 診斷傾印開關
  （`ScheduleOptions.DebugDump`＋Runs 頁警示徽章）、§1.4.11 probe 診斷分頁
  （`NetiqProbeRunner` 拆 Core＋NetIQ 維護頁「診斷」分頁，背景執行＋輪詢，
  獨立 probe gate 併發 1）。
- **Phase 5（退場）**：§1.5 五步驟——部署（Enabled=false 零變化）→ 開
  Enabled＋停用 schtasks（熱回退窗口）→ **連續 ≥5 晚驗證（使用者實際環境，
  時程由使用者控制）** → 刪 schtasks＋自方案移除 console 專案＋清 Core 內
  只被 console 用到的殘留＋部署面移除 exe 與批次 appsettings → 文件收尾
  （README 架構圖/使用方式/selftest/部署/規則升級 SOP、HISTORY 決策 20 修訂）。
  含冷回退演練（revert→build→跑一晚）確認回退路徑真實可走。

### 影響確認

- **§2 與 §3 有硬依賴**：console 移除（3）必須在排程 Web 化（2）試點通過之後；
  本輪能交付到「Phase 4 完成＋Phase 5 就緒」，最後的移除 commit 卡在使用者的
  ≥5 晚驗證之後——這不是拖延，是定案 #5 的閘門。
- 移除後回退只剩冷回退（git revert＋重建部署），已是定案 #9 的知情選擇；
  緩解措施（試點驗證、冷回退演練、分析冪等自癒）照 §1.5 原設計。

---

## 4. 說明文字二次收斂（非必要常駐 → icon）

### 需求

第五輪 §6 已做過一輪收斂（約 50 處逐一分類，31 處收進 icon），當時的保留標準
偏寬（「影響資料正確性或不可逆的警告」以外，連「陳述驗證限制」「營運調校
指引」也保留常駐）。使用者本輪要求**更嚴**：非必要常駐顯示的，一律改為 icon
滑過才顯示。

### 現存量與二次分類基準（2026-07-31 盤點）

目前殘留常駐 `form-text` **23 處**（Settings 7、Hosts 5、Netiq 4、Rules 4、
Groups／PermissionChanges／Users 各 1）＋頁首 `.lf-hint` 5 處（Imports 2、
Rules 2、PermissionChanges 1）。二次收斂的分類基準（比第五輪嚴）：

1. **僅保留**「不看見就可能立刻造成損失」的警告——不可逆操作
   （「建立後不可修改」）、資料可見性後果（「未分組只有 admin 看得到」）、
   送出會被擋的硬性限制中**與當前輸入直接相關**者。
2. **收進 icon**：驗證限制的完整說明（送出被擋時 toast 會再講一次，欄位旁
   不必常駐）、營運調校指引（Netiq 頁的回補天數/平行度長段建議）、格式範例、
   資料來源說明——第五輪因「陳述硬性限制」「調校警告」而保留的，這輪多數
   降為 icon。
3. `.lf-hint` 頁首說明維持既有「一行式＋popover 雙層」不動（第五輪原則 3，
   本身已是收斂形態）；`Hosts.cshtml` 批次貼上的格式說明含 `<code>` 排版、
   popover `html:false` 保不住，維持常駐（技術限制，第五輪已註記）。

逐處對照表沿用第五輪模式：**實作時整理、附於本節供驗收**（第五輪 Q5 同款）。

### 本輪新 UI 一體適用

§2／§3 新增的介面（排程卡欄位、立即執行對話框、probe 診斷分頁、規則升級
預覽）自始依 WEB-SPEC §8.6 第 10 條的 icon 慣例設計，不產生新的常駐說明債；
唯排程卡的「AI 診斷傾印開啟中」警示徽章與 ≥50 台紅字警示屬狀態警告非欄位
說明，常駐顯示（那正是要打擾使用者的東西）。

### 影響確認

- 純前端 markup 調整＋既有 `helpIcon`／popover 機制，零行為、零 API 變化。
- 與 §1 的 NetIQ 頁文字改寫同檔（`Netiq.cshtml`），實作時合併處理避免兩次
  相鄰 commit 碰同一段。

---

## 5. 全站用詞檢視（官方用詞／台灣 IT 慣用詞）

### 需求（隨 Q3 答覆擴充）

文字措辭以**官方用詞（微軟正體中文詞彙）或一般台灣 IT 用詞**為主，
並檢視整個網站的用詞一致性。

### 檢視範圍（四個使用者可見的字串面）

1. **Razor views**（`Views/Pages/*.cshtml`＋`_Layout.cshtml`）——靜態標籤、
   說明、modal 文字。
2. **前端 JS**（`wwwroot/js/`）——動態產生的 `textContent`、toast、確認框、
   空狀態、表頭。
3. **後端使用者可見字串**——`DomainException` 訊息（API 錯誤直接顯示於前端，
   WEB-SPEC §8.6-4 明定不轉譯）、稽核 summary、`RiskReportService` 報告 txt
   （每日產出的正式文件）、批次 console 輸出（過渡期仍在用）。
4. **README 與部署文件**的操作指引段（程式碼註解不在本輪範圍——註解是
   開發者溝通，量大且不影響使用者）。

### 初掃結果（2026-07-31）

站台底子乾淨：常見陸詞家族（刷新/保存/設置/服務器/網絡/數據/信息/軟件/
硬件/加載/默認/運行/界面/連接/字段/郵件）**前端全部零命中**；
「用戶端」（client 的微軟官方譯名）與「通過驗證」（動詞，非介詞誤用）
屬正確用法。真正要處理的是**一致性**問題：

| 詞組 | 現況 | 統一方向 |
|---|---|---|
| 點擊（22 處）vs 點選（3 處） | 混用 | 統一**「點選」**（一般台灣 IT 慣用；微軟官方「按一下」過於拘謹，與站台語氣不合） |
| 查看（13 處）vs 檢視（既有多處） | 混用 | 原則統一**「檢視」**（微軟官方 view 譯名，站台權限文案已用「檢視權限」）；口語句中自然的「看」不強改（例：「點此查看」→「點此檢視」，但「看得到這台主機」不動） |
| 其他 | 實作時逐頁掃描補列 | 以微軟語言入口網（Microsoft Language Portal）詞彙為優先參照，無官方詞才用台灣業界慣用詞 |

### 做法與驗收

- 實作時以詞表掃描（上表＋逐頁人工通讀）處理四個字串面；報告 txt 與稽核
  summary 的既有歷史資料**不回溯改寫**（證據層原則），只改產生端。
- 逐處變更整理成對照表附於本節驗收（同 §4 模式）；測試中若有 assert 比對到
  被改的字串，同 commit 內同步修正。
- 本輪新 UI（§2/§3）自始依統一後詞彙撰寫。

---

## 6. 定案紀錄（2026-07-31 使用者全數拍板）

| # | 問題 | 定案 |
|---|------|------|
| Q1 | `SuppressionCli`／`HostListCli` 提前刪或隨 Phase 5？ | **依建議：提前到本輪首段刪除**，README 同步改 |
| Q2 | 本輪範圍到 Phase 2+3 或一口氣到 Phase 4＋Phase 5 就緒？ | **全部處理**（做到 Phase 4 完成＋Phase 5 就緒；console 實際移除仍卡 ≥5 晚試點閘門） |
| Q3 | §1 文字措辭 | **依官方用詞或一般台灣 IT 用詞為主，同時檢視整個網站用詞**——擴充為 §5 全站用詞檢視 |
| Q4 | 分支基底 | **從 dev 開新分支**（`feature/web-scheduler`） |
| Q5 | 側欄項目名稱 | **「排程作業」** |
| Q6 | §4 二次收斂基準 | **評估後非必要常駐項目都改**——照 §4 擬定基準執行，逐處對照表實作後附於 §4 驗收 |

## 7. 實作順序（已可開工）

1. §1 NetIQ 頁文字修正＋§4 說明文字二次收斂＋§5 全站用詞檢視
   （同屬前端文字面，合併為一批 1~3 個 commit；§5 涉及後端字串與測試同步修正）
2. 刪 `SuppressionCli`／`HostListCli`＋README 對應兩節（Q1 定案）
3. Phase 2 服務搬遷（只搬不改；1214 測試綠＋console 輸出逐字不變為閘門；
   含 `System.IO.FileSystem.AccessControl` 套件移入 Core）
4. Phase 3 排程引擎＋UI（`ScheduleCalculator` 純函數先行＋完整單測；含側欄
   改名「排程作業」與 `PermissionAttribute` 多能力擴充、Runs API 權限放寬）
5. Phase 4 CLI 職責搬 Web（規則升級 → 傾印開關 → probe 診斷分頁，逐塊 commit）
6. 全案體檢 → 併 dev → 使用者驗證（含排程實跑）
7. Phase 5 退場步驟 1~2 由部署執行；**≥5 晚試點後**回頭做步驟 4~5
   （console 移除＋文件收尾，屆時另一個小輪收尾）

每步維持既有流程：feature 分支、逐步 commit、測試綠才前進。
