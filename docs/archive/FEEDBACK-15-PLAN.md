# 回饋第十五輪規劃（2026-08-11）

> **狀態：全案完成（2026-08-11，見文末「實作結果」）。** 來源是使用者的 UX 審查回饋
> （規則管理 R1~R4、告警 A1~A4）＋「其他」三項（操作說明書＋AI 提問、登入錯誤訊息、
> MAIL 通知）。全部項目本輪做完，實作方式依規劃時的查證結論與建議定案（見「定案決策」）。
>
> 延續第十四輪的教訓：**審查主張先逐項對過程式碼再動工**。本輪查證發現一處主張與
> 程式碼實況不符（A1 的 trendAlerts 敘述，見下），已修正後納入規劃；另有一項表面現象
> 與實際根因不同（登入錯誤訊息，根因在 api.js 的全域 401 轉址）。
>
> 基線：dev@491ab78，1726 測試綠。分支慣例：開 `feature/feedback-15` → 併 dev 實測 → 併 master。

## 查證結論（主張 vs 程式碼實況）

| 項目 | 查證 | 關鍵位置 |
|---|---|---|
| R1 比對順序不可見不可調、警告叫人做做不到的事 | ✅ 屬實 | `rules.js:186`（RULE_COLUMNS 無順序欄）、`RuleAdminService.cs:192`（一律 append）、`RuleValidator.cs:274/320`（兩處警告文字） |
| R2 抑制主機下拉硬上限 200 | ✅ 屬實 | `rules.js:679`；後端 `/api/admin/hosts` **已有 `query` 參數**（AdminController.GetHosts），前端改 combobox 即可 |
| R3 抑制徽章只取第一筆、Site 可能顯示成單台 | ✅ 屬實 | `RuleAdminService.cs:635`（FirstOrDefault＝建立順序第一筆） |
| R4 範本／停用拆成兩步、順序錯了撞牆 | ✅ 屬實 | `rules.js:432`；另查證：遮蔽是 **Warning 不是 Error**（`RuleAdminService` 的 IsValid 只看 errors），先建立再停用的順序可行 |
| A1 抑制覆蓋缺口 | ⚠️ 部分修正 | 見下節 |
| A2 趨勢/關聯告警純文字零出口 | ✅ 屬實 | `record-detail.js:1332/1352`；但 `DailyAnalysisRecord.TrendAlerts` 是持久化的 `List<string>`，需加結構化平行欄位（成本比回饋估的高） |
| A3 offerBatchSuppression 靜默不提議 | ✅ 屬實 | `record-detail.js:816` |
| A4 「可靠歷史」黑話、兩層基準不同義 | ✅ 屬實 | `lf-help` popover 元件現成 |
| 其他 2：登入錯誤馬上消失 | ✅ 現象屬實，**根因不同** | `AuthController.cs:62` 登入失敗回 401 → `api.js:52` 全域 401 處理 `location.href` 轉址整頁重載，錯誤閃掉、輸入清空、訊息被蓋成「登入已逾期」 |
| 其他 3：MAIL 通知 | — | 即第十四輪暫緩的批次 D，全案目前零 SMTP 基礎設施 |
| 其他 1：操作說明書＋AI 提問 | — | AI 是本機小模型（`PromptBudget.cs:13`：context 20480、扣餘裕約 18K token 可用），手冊全文塞 prompt 不可行；現有 docs 全是開發文件，**使用者手冊需從零撰寫** |

### A1 的主張修正（本輪最重要的查證）

回饋原文說「trendAlerts 與 correlations 兩項不看任何抑制設定」——**對 trendAlerts 不正確**：
`TrendAnalyzer.cs:111`（首次出現）與 `:129`（頻率上升）都有 `!sig.Suppressed` 守衛，
**有命中規則且被抑制的簽章，趨勢告警本來就不吵、也不拉高風險**。真正的缺口是三個：

1. **`RuleId == null`（Other 類）簽章永遠標不上 `Suppressed`**——`LogAnalysisService.cs:161`
   只以 RuleId 比對，Other 類的 Rising 告警確實關不掉；且 Rising 分支（`:117`）無嚴重度
   閘門、還會把 Low `Escalate` 成 Medium。
2. **總量突增告警**（整體錯誤量 `:156`／安全稽核量 `:176`）不掛任何簽章，結構上無抑制掛載點。
3. **關聯告警**無抑制路徑，且 `CorrelationFinding` 沒有識別碼（只有 Severity＋Description）。

另一個回饋沒提的既有事實：專案已有簽章級機制 **NoiseMark**（`NoiseMark.cs`，「已知雜訊」
記憶），但它只作用於**顯示層**（詳情頁自動標雜訊），不影響風險判定與告警——註解明文寫了
「治本／治標」兩條路徑的設計決策。本輪把 Other 類從治標升級到治本，NoiseMark 的定位見定案 1。

## 定案決策

1. **簽章級抑制落在 `RuleSuppression` 的擴充**（新增選配目標型別），不另起爐灶。
   **NoiseMark 保留原樣不退場**：它仍是「顯示層記憶」（跨日自動標雜訊），抑制是「分析層
   治本」；「標已知雜訊」的流程對無規則問題**新增提議建立簽章抑制**，兩者在手冊裡講清楚
   差異。既有 NoiseMark 資料零遷移、零行為變更。
2. **A1 四個建議本輪全做**：簽章級抑制（建議 1）＋總量抑制（建議 2）＋關聯模式抑制
   （建議 3）＋ Rising 嚴重度閘門（建議 4）。統一設計成**抑制目標四型**（規則／簽章／
   關聯模式／總量），共用同一份抑制清單、同一套 Scope（Host/Group/Site）與到期語意，
   避免三種新抑制概念各自長出一套 UI。
3. **R1 做「中等」級**：唯讀順序欄＋預設排序＝比對順序＋非順序排序時提示＋警告文字改寫。
   **不做上移／下移**——64 條內建規則下官方路徑（停用＋範本複製＋R4 勾選框）已能解遮蔽，
   可調順序反而引入「調錯順序改變全站分類結果」的新風險面。
4. **操作說明書＋AI 提問一輪做完**，但 AI 問答明確標示「實驗性」；檢索用**分節＋關鍵字
   選節**（輕量、無向量 RAG 基礎設施），單輪問答（不做多輪對話，尊重 20K context）。
5. **MAIL 通知需求預設值**（實作依此，使用者實測後可調）：
   - 緊急定義＝當日風險為**高**（ElevatesDayRisk 簽章或關聯命中）；
   - 收件人＝全域收件人清單（必填）＋選項「同時通知主機負責人」（`WebIdentity.Email`）；
   - 週報內容＝過去 7 日各主機高／中風險日彙總＋未處理數；
   - SMTP 密碼沿用 `CryptoHelper.Encrypt` 的 write-only 模式（同 AI 金鑰）。

## 批次劃分與實作順序

| 批次 | 內容 | 依賴 |
|---|---|---|
| F | 登入 401 轉址修復（其他 2） | 無，最小、先做 |
| A | 抑制系統擴充：目標四型＋Rising 閘門（A1 全案） | 無 |
| B | 規則管理 UX：R1～R4 | R3 依賴 A 的目標四型欄位（徽章要顯示新目標） |
| C | 詳情頁告警改造：A2 結構化＋導航、A4 popover、A3 提議流程升級、新抑制出口 | A |
| D | 郵件通知（其他 3） | 無（與 A~C 平行可做，建議照序） |
| E | 操作說明書＋AI 提問（其他 1） | A~D 全部（手冊要寫到新功能） |
| G | 全案體檢＋文件收尾 | A~F |

---

## 批次 F：登入 401 轉址修復

**根因**：`api.js:52` 對所有 401 一律 `location.href = '/login?returnUrl=…'`。在登入頁上，
登入失敗（`AuthController.Login` 回 `Unauthorized`）也觸發整頁轉址→重載，錯誤訊息閃掉、
輸入被清空，且拋出的訊息是寫死的「登入已逾期」而非後端的「帳號或密碼錯誤。」。

**修法**（只動 `api.js`）：

- 401 處理加前置條件：`location.pathname === '/login'` 或請求路徑為 `/api/auth/login` 時
  **不轉址**，改為解析回應 payload、以其中的錯誤訊息拋 `ApiError`（fallback「登入失敗。」）。
- 其餘頁面的 401 行為完全不變（登入逾期／停用仍轉址）。
- `login.js` 不用改：既有 `showError(error.message)` 會顯示正確訊息、輸入保留。

**測試**：無後端變更；前端行為以瀏覽器端到端驗證（錯誤帳密→訊息顯示且輸入保留→
改對帳密可直接重送）。

---

## 批次 A：抑制系統擴充（A1 全案）

### A-1 資料模型：抑制目標四型

`RuleSuppression`（`LogForesight.Core/Models/RuleSuppression.cs`）新增欄位，全部選配、
舊資料反序列化取預設值，**零遷移**：

```csharp
/// Rule（預設，舊資料反序列化到此值，語意與改版前逐位相同）｜Signature｜Correlation｜Volume
public string TargetType { get; set; } = SuppressionTargetTypes.Rule;

/// TargetType=Signature：IssueSignatureKey.For(...) 的簽章鍵
public string? SignatureKey { get; set; }

/// TargetType=Correlation：CorrelationFinding.PatternId
public string? CorrelationPatternId { get; set; }

/// TargetType=Volume："error"（整體錯誤量）｜"audit"（安全稽核量）
public string? VolumeKind { get; set; }

/// 非規則目標的人話標籤（建立時擷取，如「Application / MyApp EventId 1000」），管理頁直接顯示
public string? TargetLabel { get; set; }
```

- 新增 `SuppressionTargetTypes` 常數類（同 `SuppressionScopes` 風格）。
- `RuleId` 僅 TargetType=Rule 時必填，其餘型別為空字串。
- Scope（Host/Group/Site）、Reason、ExpiresAt、SuppressedBy 對四型通用，語意不變。
- 平台欄位推導：Signature 從簽章鍵推不出平台，抑制清單的平台篩選對非規則目標顯示「—」
  （或以建立來源主機的 os 記錄一份 `Platform`，建立時帶入——採後者，清單篩選才完整）。

### A-2 SuppressionFilter 擴充

`SuppressionFilter.cs` 維持純函數分工：

- `ToRuleIdSet` 加 `TargetType == Rule` 過濾（舊資料預設 Rule，行為不變——**這行是既有
  行為保證的關鍵，要有測試釘住**）。
- 新增 `ToSignatureKeySet`、`ToCorrelationPatternIdSet`、`HasVolumeSuppression(list, kind)`，
  皆以 `ActiveForHost` 的結果為輸入（Scope／到期判定完全沿用）。

### A-3 LogAnalysisService 接線

1. **簽章抑制**：`:159` 的迴圈擴充——`issue.Suppressed = true` 的條件加上
   `signatureKeySet.Contains(IssueSignatureKey.For(issue.LogName, issue.Source, issue.EventId, issue.EntryType))`。
   規則命中與否皆可被簽章抑制（主要用途是 Other 類，但不排除規則命中者）。
   下游全部自動生效：TrendAnalyzer 的 `!sig.Suppressed` 守衛、`ComputeRuleBasedRisk` 的
   High 判定、告警文字組裝——**Other 類的 Rising 告警自此關得掉**。
2. **關聯抑制**：`CorrelationAnalyzer.Detect` 維持純函數不動；Service 層拿到 findings 後，
   以 `ToCorrelationPatternIdSet` 分流——被抑制的**不進** `correlations`（不拉風險、不進
   告警文字），改記到 `record.SuppressedCorrelationAlerts`（新欄位，見 A-5）。
3. **總量抑制**：`TrendAnalyzer.Analyze` 加兩個參數（或小型 options 物件）
   `suppressErrorVolume` / `suppressAuditVolume`；為 true 時對應的突增告警不加入 alerts，
   改回傳到被抑制清單（`record.SuppressedTrendAlerts`）。呼叫端由
   `HasVolumeSuppression` 決定。

### A-4 TrendAnalyzer：Rising 嚴重度閘門

`:117` Rising 分支：告警文字只在**升級前** `sig.Severity >= IssueSeverity.Medium` 時加入
alerts。`Trend = Rising`、`Escalate`、`ElevatesDayRisk` 判定全部保持不變——Low 簽章的
Rising 仍會在問題清單顯示 Rising 徽章與升級後的 Medium 嚴重度，只是**不再產生告警文字、
不再把當天拉成中風險、不再觸發 AI 呼叫**。

> **行為變更申報**：過去因「Low 簽章頻率上升」而判中風險的日子，改判低風險。這正是
> 回饋要的效果（一個本來就不重要的簽章不該有能力拉高整天），但要在 DETECTION-SPEC 與
> README 明確記錄，且既有測試中斷言此行為者需改寫。

### A-5 CorrelationFinding.PatternId 與紀錄欄位

- `CorrelationFinding` 加 `public string PatternId { get; init; } = ""`。17 個模式各給穩定
  id（kebab-case，如 `intrusion-chain`、`brute-success`、`persistence`、`audit-tamper`、
  `priv-implant`、`av-off-malware`、`malware-persistence`、`storage-chain`、`storage-crash`、
  `hw-unstable`、`crash-service-fail`、`crash-loop-resource`、`time-skew-auth`、
  `xday-intrusion`、`xday-storage`、`xday-av-off-malware`、`xday-brute-rdp`；實作時以檔內
  【標籤】逐一對應）。`CorrelationAnalyzerRuleAlignmentTests` 擴充：斷言全部 finding 的
  PatternId 非空且不重複。
- `DailyAnalysisRecord` 新增（全部零遷移、舊資料為空清單）：
  - `List<TrendAlertRef> TrendAlertRefs`——`{ Text, IssueKey?, Kind }`，Kind ∈
    `signature`（首次出現/頻率上升，帶 IssueKey）｜`volume-error`｜`volume-audit`；
  - `List<CorrelationAlertRef> CorrelationAlertRefs`——`{ Text, PatternId }`；
  - `List<string> SuppressedTrendAlerts`、`List<string> SuppressedCorrelationAlerts`。
  - **既有 `TrendAlerts`／`CorrelationAlerts`（字串）維持照寫**：AI prompt 組裝、週體檢、
    既有渲染端全部不動；Refs 是顯示層增強的平行資料，兩邊由 TrendAnalyzer／Service 同時產生。

### A-6 API 與管理 UI

- 新端點 `POST /api/suppressions`（`[Permission(Capability.Maintain)]`）：request 帶
  `TargetType` 與對應目標欄位＋Scope＋Reason＋Days。既有
  `POST /api/rules/{ruleId}/suppressions` 保留，內部委派到同一 service 路徑（相容不破壞）。
- 驗證：TargetType 與目標欄位成對（Signature 必帶 SignatureKey…）；Volume 同主機同 kind
  重複建立擋下；Correlation 的 PatternId 必須在已知模式清單內。
- 規則頁「告警抑制」分頁：清單加「目標」欄（規則 Id／簽章標籤／關聯模式名／總量類別），
  非規則目標以 `TargetLabel` 顯示；篩選器加目標型別。解除／到期語意沿用。
- Site/Group 的影響面預覽（十四輪 C1）對新目標型別沿用既有「受影響主機數」計算，不另做
  per-target 特化。
- 稽核：建立／解除抑制的 audit 記錄補上 targetType 與 label。

### A-7 測試（批次 A）

- SuppressionFilter：`ToRuleIdSet` 不含非 Rule 目標（回歸保證）；三個新投影各自的
  Scope／到期組合。
- LogAnalysisService：簽章抑制標記（含 RuleId==null 者）；關聯分流（被抑制不進風險判定、
  進 Suppressed 清單）；總量抑制兩種 kind。
- TrendAnalyzer：Low Rising 不產生告警但 Trend/Escalate 照舊；Medium/High Rising 行為不變；
  被抑制簽章行為不變（既有測試）。
- 既有測試修正：凡斷言「Low 簽章 Rising → 告警／中風險」者依新行為改寫。
- CorrelationAnalyzer：PatternId 非空唯一（alignment test 擴充）。
- API：四型建立／驗證失敗案例／舊端點相容。

---

## 批次 B：規則管理 UX（R1～R4）

### B-1（R1）比對順序可見化——「中等」級

- `RuleDto` 加 `MatchOrder`（int）：GetRules 時計算＝該規則在**同平台**規則中於儲存清單
  的序位（1-based，含停用規則——順序是清單事實，停用只是不參與比對，列上仍照實顯示）。
- `rules.js` RULE_COLUMNS 加第一欄「順序」：`sortKey: 'matchOrder'`，**預設排序＝順序 asc**；
  使用者切到其他欄排序時，於筆數列旁顯示小字提示「目前排序非比對順序，比對以『順序』欄為準」。
- 頁首提示補一句：「規則由上而下依『順序』比對，第一條命中者生效；新規則一律加在最後。」
- 警告文字改寫（`RuleValidator.cs`）：
  - Windows（:274）：「規則 X 被排在前面的規則 Y 遮蔽，永遠不會命中（…）。解法：停用
    其中一條（建議停用 Y 並以它為範本建立更精確的自訂規則），或縮小 Y 的比對範圍使兩者
    不重疊。本頁不支援調整規則順序，順序由建立先後決定。」
  - Linux（:320）：移除「把 X 移到 Y 之前」選項，保留「幫 Y 加 MessagePatterns 收斂」＋
    補「或停用其中一條」。
- 測試：RuleValidatorTests 中斷言警告文案的測試同步更新；RuleAdminServiceTests 加
  MatchOrder 計算（跨平台交錯清單）。

### B-2（R2）抑制主機選取改搜尋型 combobox

- 後端：`/api/admin/hosts` 的 `query` 參數已存在；補選配 `os` 參數（HostAdminService
  過濾一行）讓平台過濾在伺服器端完成。
- 前端：`ui.js` 新增 `searchableHostSelect`（比照 `searchableUserSelect` 的 input-group
  單行模式）：輸入去抖 300ms → `GET /api/admin/hosts?query=…&os=…&pageSize=50`，下拉顯示
  `hostName（displayName）`；空字串顯示前 50 台＋「共 N 台，輸入關鍵字縮小範圍」提示。
- `rules.js` 抑制 modal 的 `populateHostOptions`／`ensureHostOptions` 換用新元件，移除
  `pageSize=200` 與一次拉全量的註解。
- 測試：HostAdminService 的 os 過濾；前端以瀏覽器驗證（2000 台情境以測試資料模擬可另計）。

### B-3（R3）抑制徽章誠實化

`RuleAdminService.ToDto`（:633）：

- 代表筆改「最寬範圍優先」：Site > Group > Host，同範圍取最早建立；
- `RuleDto` 加 `SuppressionCount`；徽章文字多筆時顯示「已抑制 ×N」；
- tooltip 列前 3 筆（範圍＋目標＋到期），其餘「見『告警抑制』分頁」；
- 徽章的抑制來源涵蓋 TargetType=Rule 者；簽章／總量抑制不掛在規則列（不屬於規則）。
- 測試：多筆混合範圍的代表筆選取、計數。

### B-4（R4）範本 modal 的「同時停用原規則」

- 「以此為範本建立自訂規則」modal 加核取方塊，**預設勾選**：「同時停用原規則 `{id}`」，
  說明文字：「停用後原規則保留可隨時恢復；不停用的話，新規則會被原規則遮蔽而不會生效。」
- 儲存流程（前端循序兩步）：先建立自訂規則 → 成功後若勾選 → 呼叫既有停用 API。
  - 建立成功但停用失敗：warning toast「自訂規則已建立，但停用原規則失敗——原規則仍啟用，
    新規則會被遮蔽，請到清單手動停用」。
  - 順序刻意「先建後停」：避免「停用成功、建立失敗」留下原規則被停、又沒有替代規則的空窗。
  - 勾選狀態下，建立回應中**針對原規則**的遮蔽警告前端不顯示（下一步就要停用它，顯示只會
    誤導）；其他規則造成的遮蔽警告照常顯示。
- 測試：前端流程以瀏覽器端到端驗證；後端無變更。

---

## 批次 C：詳情頁告警改造（A2＋A3＋A4＋新抑制出口）

### C-1（A2）告警結構化與頁內導航

- `RecordDetailDto` 增列 `TrendAlertRefs`／`CorrelationAlertRefs`／`SuppressedTrendAlerts`／
  `SuppressedCorrelationAlerts`（由 A-5 的紀錄欄位帶出）。
- `record-detail.js` `renderAlerts` 改造：
  - 趨勢告警：Kind=signature 且 IssueKey 對得上本日 `topIssues` 者，整列可點→捲動並高亮
    對應問題分節（**沿用「類型分布」卡既有的頁內導航模式**）；對不上（如簽章不在 top 清單）
    或舊紀錄無 Refs → 純文字降級。
  - 關聯告警：列尾掛模式說明 popover（模式名＋觸發條件摘要，靜態字典存前端）。
  - 舊資料（Refs 為空）：完全等同現行畫面，零破壞。
- 已抑制區塊：詳情頁「已抑制的告警」區（既有）追加顯示 `Suppressed*Alerts` 兩清單，
  收合呈現——**抑制是「不吵」不是「不記」**，誠實申報語意與現行一致。

### C-2（A4）基準說明 popover

- 趨勢告警標題旁掛 `lf-help`：「『可靠歷史』＝排除資料不完整日與該頻道未讀取日的歷史。
  簽章層基準＝該問題**出現日**的次數中位數；總量層基準＝**非零日**中位數。」
- 文案集中一處（前端字典），避免散落。

### C-3（A3＋簽章抑制出口）批次提議流程升級

`offerBatchSuppression`（`record-detail.js:809`）改造：

- 勾選的問題分兩群：有 ruleId 者→規則抑制（現行）；無 ruleId 者→**簽章抑制**
  （`POST /api/suppressions`，TargetType=Signature，Host scope，TargetLabel 用
  `source EventId n` 人話組裝）。
- 確認對話框同時列兩群數量：「抑制命中的 X 條規則＋ Y 個未命中規則的問題簽章」。
- 兩群都空（全部已抑制）→ toast：「已標記為已知雜訊；勾選的問題皆已在抑制範圍內」。
- 單列「標為已知雜訊」流程同步：無 ruleId 時提議簽章抑制（取代原本的靜默）。

### C-4 總量／關聯告警的抑制出口

- 總量告警列（Kind=volume-*）：Maintain 使用者可見「抑制此類告警（本主機）」按鈕→
  確認後建立 Volume 抑制（預設 Host scope＋理由必填）。
- 關聯告警列：「抑制此關聯模式」按鈕，確認對話框**強警告**：「此模式命中即為高風險日；
  抑制後本主機（／群組／全站）此模式將不再拉高風險、不再通知。僅在確認為既知誤報時使用。」
  預設 Host scope，Site 需經影響面預覽（沿用 C1 十四輪機制）。
- 測試：DTO 帶欄位的查詢服務測試；前端以瀏覽器端到端驗證三種建立路徑。

---

## 批次 D：郵件通知（SMTP）

### D-1 設定模型與頁面

`SystemSettings` 新增（全部零遷移預設）：

```
MailEnabled (bool, 預設 false)
SmtpServer / SmtpPort (預設 25) / SmtpUseTls (bool) / SmtpAccount / SmtpPasswordEnc（write-only，CryptoHelper）
MailFrom / MailRecipients (List<string>，全域收件人)
MailNotifyHostOwners (bool，同時通知主機負責人＝WebIdentity.Email)
MailMinRiskLevel（"高" | "中"，摘要納入門檻，預設 高）
MailOnRunCompleted (bool，排程結束後寄執行摘要)
MailDailyEnabled (bool) + MailDailyTime ("HH:mm")
MailWeeklyEnabled (bool) + MailWeeklyDayOfWeek + MailWeeklyTime
MailUrgentEnabled (bool，高風險日即時通知，不受每日/每週時間限制)
MailSubjectTemplate（預設 "[{site}] {type}：{date} {summary}"）
MailBodyIntro（信件開頭可自訂段落，純文字）
```

- 設定頁新增「郵件通知」區塊（比照 AD 驗證區塊的展開式版面）：連線四欄＋寄件人＋收件人
  （逗號分隔輸入，儲存前驗格式）＋通知邏輯開關群＋模板編輯（標題模板旁列可用變數：
  `{site}`＝品牌名稱、`{host}`、`{date}`、`{risk}`、`{type}`＝通知種類、`{summary}`）。
- 密碼欄 write-only：回傳只給 `MailHasPassword`，清除走 `ClearSmtpPassword` 旗標（完全
  比照 AI 金鑰的三態處理）。
- 「測試寄信」按鈕：`POST /api/settings/mail/test`（Maintain）——用**表單當前值**寄測試信
  （密碼欄空白時 fallback 已儲存密文），回報成功或含 SMTP 錯誤細節（管理者對自己測試，
  細節可顯示，比照 AD 測試連線的語意）。

### D-2 寄送服務與可測試性

- `LogForesight.Web/Services/Mail/` 新增：
  - `ISmtpMailSender`（介面：`Send(MailMessageSpec)`）＋ `SystemNetSmtpMailSender`
    （`System.Net.Mail.SmtpClient` 實作——不新增套件依賴；SmtpClient 雖標示 legacy，
    對內網 relay 場景足夠，介面隔離讓日後換 MailKit 只動一個類別）。
  - `MailNotificationService`：組信（標題模板展開、摘要表格純文字＋簡單 HTML 雙版本）、
    收件人解析（全域＋負責人去重）、寄送與錯誤記錄（NLog＋audit）。
- 單元測試全部打在 `MailNotificationService` ＋ fake `ISmtpMailSender`（斷言收件人、標題
  展開、門檻過濾、去重），不碰真實 SMTP。

### D-3 觸發點三路

1. **排程結束後**（`MailOnRunCompleted`）：排程執行收尾處（AnalysisOrchestrator 完成、
   SchedulerRunState 寫入 LastRun 結果的同一點）呼叫——彙整本次執行產生的高／中風險日
   （依 `MailMinRiskLevel`）與失敗主機數，一封摘要信。
2. **每日／每週定時**：`SchedulerHostedService` 的 60 秒輪詢加時刻檢查（比照排程窗口的
   判定方式，過門檻即觸發、以「上次寄送日」防重複——寄送狀態存新 blob
   `mail_notify_state`）。每日信＝當日（或最近分析日）摘要；每週信＝過去 7 日各主機
   高／中風險日彙總＋未處理數。
3. **緊急即時**（`MailUrgentEnabled`）：分析管線判定某主機日為**高風險**時立即寄送，
   每 `host+date` 只寄一次（記在 `mail_notify_state`，隨保留政策清理）。此路**不受**
   每日／每週時刻設定限制——即「特定緊急的項目忽略指定時間當天發送」的落地。

- 寄送失敗：NLog WARN＋執行歷程備註，不中斷分析主流程（**通知永遠不能弄掛分析**——
  try/catch 包住整個通知呼叫）。
- DB-SPEC 補 `mail_notify_state` blob key 說明。

### D-4 測試（批次 D）

- 設定 round-trip（含密碼三態、收件人格式驗證、模板保存）。
- MailNotificationService：門檻過濾、模板變數展開、負責人合併去重、緊急去重（同日重跑
  不重寄）、週報彙總範圍。
- 觸發判定：每日／每週時刻跨過與否、上次寄送日防重複（比照 ScheduleCalculator 測試手法，
  時間全部注入不用 DateTime.Now）。

---

## 批次 E：操作說明書＋AI 提問

### E-1 手冊內容與存放

- 新目錄 `LogForesight.Web/HelpContent/`：每節一個 Markdown 檔＋`manifest.json`
  （`id`、`title`、`keywords[]`、`related[]`——related 供跨功能問題把關聯節一併選入）。
  以**內嵌資源**編進組件（部署零額外檔案）。
- 章節規劃（14 節，規則維護相關依回饋要求加厚）：
  1. 系統總覽與登入　2. 儀表板　3. 問題事件（主視角）　4. 風險日詳情（含告警四類解讀）
  5. 處理狀態與案件　6. **規則維護**（比對順序語意、遮蔽警告的處理路徑、範本＋停用、
  內建規則改版徽章）　7. **告警抑制**（四種目標的差異與適用時機、與「已知雜訊」的差別、
  影響面預覽）　8. 主機與群組　9. 授權與角色　10. 排程作業　11. 匯入　12. 系統設定
  （AI／郵件通知／保留政策）　13. 名詞解釋（可靠歷史、基準、簽章、風險等級判定）
  14. 常見問題。
- 各節結尾固定「相關功能」連結（即 manifest 的 related，人與 AI 共用同一份關聯資訊）。

### E-2 手冊頁

- 左側選單最下方新增「操作說明書」，**僅 Maintain（admin）顯示**（選單顯示與頁面
  `[Permission(Capability.Maintain)]` 雙閘，比照既有 admin 頁）。
- 頁面 `/help/manual`：左側章節目錄＋右側內容。Markdown 渲染用 vendored `marked.min.js`
  放 `wwwroot/lib`（自載自用，無 CDN，比照既有前端資產慣例）；內容經 sanitize 後插入
  （內容雖是自家資源，仍照專案的 XSS 紀律走）。
- API：`GET /api/help/manual`（manifest＋全部章節內容一次取回——總量預估 <200KB，
  不值得做分節載入）。

### E-3 AI 提問（實驗性）

- 手冊頁頂部問答框，標示「實驗性」徽章；`AiBaseUrl` 未設定時整個問答區換成說明文案
  「未設定 AI 服務，僅提供文件瀏覽」（比照統計模式的誠實申報寫法）。
- `POST /api/help/ask`（Maintain）流程：
  1. **選節**：對 question 做關鍵字比對計分（title 命中 ×3、keywords ×2、內文 ×1；
     中文以雙字元 bigram 切詞、英文以空白切詞），取最高分節＋其 related 節；
  2. **預算控制**：以 `PromptBudget.EstimateTokens` 累計，內容上限約 12K token（超出即
     停止加節），保留輸出空間 `AiMaxTokens`；
  3. **呼叫**：`AIService.ChatAsync`，system prompt 固定——台灣繁中回答、僅依提供章節
     內容作答、章節沒寫的要明說「說明書未涵蓋」、結尾列出引用章節標題；
  4. 回應顯示答案＋「引用章節」連結（點擊跳至該節）。單輪問答，不保留對話歷史。
- 失敗處理沿用 AIService 的重試／逾時設定；錯誤顯示「AI 服務暫時無法回應，可先查閱下方
  章節」。
- 測試：選節計分（跨功能問題選入 related）、預算截斷、AI 未設定短路；ChatAsync 以既有
  fake 打樁驗證 prompt 組裝（不打真模型）。

> **明確不做**（本輪範圍界定）：向量 RAG／embedding、多輪對話、非 admin 開放、
> 手冊全文進 prompt。文件量若日後成長到選節命中率明顯不足，再評估 RAG——manifest 的
> keywords/related 結構已為它預留了素材。

---

## 批次 G：全案體檢＋文件收尾

- 體檢重點（依本案風險排序）：
  1. `ToRuleIdSet` 過濾 TargetType 後，所有既有呼叫端行為不變（全 callsite 普查——
     十四輪「改共用欄位漏改讀取端」教訓的直接應用）；
  2. Rising 閘門的行為變更影響面：全部斷言 trendAlerts 的測試逐一核對；
  3. 關聯抑制不影響 `ElevatesDayRisk` 以外的高風險路徑（High 簽章仍照常）；
  4. 郵件觸發不阻塞分析主流程（例外路徑實測）；
  5. 新增 API 的權限標註普查（Maintain 全掛）。
- 文件更新：
  - `README.md`：功能清單補郵件通知、操作說明書；Rising 閘門行為變更申報。
  - `docs/RULES-SPEC.md`：抑制目標四型、與 NoiseMark 的分工定案。
  - `docs/DETECTION-SPEC.md`：Rising 嚴重度閘門、關聯 PatternId、總量抑制。
  - `docs/WEB-SPEC.md`：新頁面／API／設定欄位。
  - `docs/DB-SPEC.md`：`mail_notify_state` blob、RuleSuppression 新欄位。
- 本檔補「實作結果」節後收尾。

## 風險與緩解

| 風險 | 緩解 |
|---|---|
| Rising 閘門改變既有風險判定（中→低） | 行為變更明文申報；Trend 徽章與嚴重度升級保留，資訊不遺失，只是不吵 |
| 關聯抑制可噤聲高風險日（誤用＝盲區） | 建立時強警告＋理由必填＋Site 需影響面預覽＋已抑制區塊照常申報＋audit |
| `RuleSuppression` 多型欄位讓舊端點誤建歪資料 | 服務層單點驗證（TargetType 與目標欄位成對），舊端點委派同一路徑 |
| SmtpClient 為 legacy API | 介面隔離（ISmtpMailSender），日後換實作不動業務碼；內網 relay 場景實測 |
| AI 問答品質不穩（20K 小模型） | 標示實驗性＋僅依章節作答的 system prompt＋未涵蓋要明說；手冊本體不依賴 AI |
| marked.js 新前端依賴 | **實作時改變決策，見「實作結果」**：沿用既有 markdown-lite.js 安全子集，未引入 marked.js |

## 實作結果（2026-08-11，全案完成）

全部 7 個批次依序（F→A→A-6→B→C→D→E→G）在 `feature/feedback-15` 分支完成，本機未推送
origin。最終狀態 1799 測試綠（不含跳過的 Scale 系列）。

| 批次 | commit | 內容 |
|---|---|---|
| F | `64096b5` | 登入頁 401 不再整頁轉址蓋掉錯誤訊息 |
| A（核心層） | `ce741e7` | 抑制目標四型（Rule/Signature/Correlation/Volume）、`CorrelationFinding.PatternId`、TrendAnalyzer Rising 嚴重度閘門＋總量抑制、SlowTrendAnalyzer 補抑制檢查、`DailyAnalysisRecord` 四個平行欄位 |
| （修復） | `9828dde` | `IssueSignatureKey.For` 4 處誤用四參數版本、漏帶 Linux 專用 EventKey 第五段，Linux 同 program 不同規則的簽章抑制會誤連坐；獨立提交（非批次A功能，是查證中發現的既有正確性問題） |
| A-6（Web層） | `b75e74e` | 統一入口 `POST/DELETE /api/suppressions`，`rules.js` 告警抑制分頁加目標型別欄＋篩選 |
| B | `9b5ed1a` | R1 MatchOrder 唯讀欄＋警告文案改寫；R2 `searchableHostSelect` 取代 `pageSize=200`；R3 代表筆改「範圍最寬優先」＋「已抑制×N」徽章；R4 範本 modal 先建立新規則成功才停用原規則 |
| C | `875799e` | C-1 告警結構化頁內導航（`RecordDetailDto` 新增 4 個 Ref 欄位）；C-2 `lf-help` 關聯模式觸發條件字典（19 項）；C-3 批次提議升級（無 `RuleId` 問題改提議簽章抑制）；C-4 總量／關聯告警抑制出口＋新增 `ui.js` `confirmActionWithReason` |
| D | `e32282c` | SMTP 郵件通知全案：三路觸發（執行後摘要／每日每週定時／高風險即時去重）、設定頁新分頁、測試寄信。**刻意簡化**：只寄純文字，規劃文件寫的 HTML 雙版本未實作（內部通知信不需要） |
| E | `41565a1` | 操作說明書＋AI 問答（實驗性）：14 節內嵌 Markdown、`/help/manual` 頁、關鍵字選節計分＋預算截斷。**刻意偏離規劃**：未引入 `marked.min.js`，沿用既有 `markdown-lite.js` 安全子集（全站至今未引入任何可解析 HTML 的 Markdown 庫，見該檔頭註解）；批次開工前依 CLAUDE.md 規則詢問使用者是否套用 `ui-ux-pro-max`，回覆「沿用現有樣式」 |
| G（體檢＋文件） | 本次提交 | 見下「體檢發現」與「文件更新」 |

### 體檢發現（批次G）

全案體檢揪出 3 個真實問題，皆已修復並補回歸測試：

1. **`SuppressionFilter.StillSuppressedElsewhere` 泛型化不完整**：四型抑制上線後，這個
   「到期抑制是否仍受其他範圍覆蓋」的比對函式仍只比 `RuleId`——Signature／Correlation／Volume
   三型的 `RuleId` 恆為空字串，比對恆假，「此設定仍受其他範圍抑制」的提示對這三型永遠不會
   出現。連帶發現 `AnalysisOrchestrator` 的到期抑制通知會對這三型印出空白識別（`{expired.RuleId}`
   是空字串）。修正：新增 `SuppressionFilter.TargetIdentity`（TargetType＋對應欄位的複合鍵），
   `StillSuppressedElsewhere` 改用複合鍵比對；`AnalysisOrchestrator` 改用 `TargetLabel` 做非
   Rule 型的顯示身分（與 `WeeklyCheckupService` 既有的正確寫法對齊）。
2. **`MailNotificationService` 兩個對外方法的 settings 讀取在 try 區塊外**：`ISystemSettingsStore.Get()`
   若拋例外（blob 讀取失敗／並發衝突，見 docs/DB-SPEC.md 的 `ConcurrencyToken` 說明）會直接
   穿透方法本身。`NotifyAfterRunAsync` 的呼叫端（`SchedulerHostedService.TriggerRunAsync`）會讓
   外層 catch 把**已成功的分析執行**誤判為失敗；`CheckAndSendDailyWeeklyAsync` 的呼叫端
   （`TickAsync`）沒有自己的 try/catch，例外會讓同一輪詢的排程窗口判斷整段被跳過。兩者都
   違反程式注解自己宣稱的「內部自行 try/catch 到底」保證。修正：把 `Get()` 移進 try 區塊。
3. **文字報告（`RiskReportService`）未涵蓋已抑制的趨勢／關聯告警**（記錄但不在本輪修——
   已用 `spawn_task` 交給後續處理）：網頁詳情頁與週體檢報告都已正確涵蓋抑制四型，只有
   `export/*.txt` 的「已抑制的告警」區塊仍只顯示 Rule／Signature 兩型（`AppendSuppressedIssues`
   只查 `TopIssues`），沒有渲染 `SuppressedTrendAlerts`／`SuppressedCorrelationAlerts`。不是
   bug（沒有任何地方會算錯或當掉），是文字報告的資訊完整度落後於網頁版，屬於錦上添花的
   一致性補強，故未列入本輪範圍。

其餘 4 項體檢項目（`ToRuleIdSet` 其餘 callsite、Rising 閘門測試覆蓋、關聯抑制與
`ElevatesDayRisk` 的隔離、新增 API 權限標註）核對後確認既有實作已經正確，無需修改。

### 終檢輪（併 dev 前的逐批次規劃對照）

全批次逐項對照規劃文件後，補上 3 個批次 D 的規劃缺漏與 2 個小瑕疵：

1. **收件人／寄件人格式驗證**（D-1「儲存前驗格式」原漏做）：`SystemSettingsService.Update`
   以 `MailAddress.TryCreate` 驗證——格式錯誤的位址若放行落盤，排程觸發時建 `MailAddress`
   才炸、又被 `SendSafeAsync` 靜默吞掉只記 log，使用者會以為通知設好了卻永遠收不到信。
2. **週報附未處理數**（D-3「週報＝…彙總＋未處理數」原漏做）：`MailNotificationService` 注入
   `IRecordHandlingStore`，週報結尾附未處理（含處理中）風險日總數。刻意**不限窗口**——
   「還沒處理完」是當下狀態，上上週掛著沒人動的風險日正是週報最該提醒的（同 DB-SPEC
   「進行中案件不論多舊都留著」的取向）；每日摘要刻意不附，避免變成每天寄的固定噪音。
3. **設定 round-trip 測試**（D-4 原漏做）：`SystemSettingsServiceTests` 補 10 條——完整
   Update→Get round-trip、SMTP 密碼三態（設定／留空沿用／清除）、觸發啟用但缺收件人、
   收件人／寄件人格式、時刻格式、模板留空回退、未啟用時可全空。
4. 「測試寄信」的空白模板改回退出廠模板（與儲存路徑同一 fallback，測出來的主旨才與存檔後
   的實際行為一致）；`help-manual.js` 移除一行多餘的 `innerHTML` 清空（上方已 `replaceChildren`，
   且全站慣例不用 innerHTML）。

**申報的刻意簡化**（規劃文字 vs 實作差異，除批次 D/E 段落已列者外）：D-3 的「寄送失敗：
NLog WARN＋執行歷程備註」只做了 NLog WARN，未寫執行歷程備註——通知寄送發生在執行收尾
之後（`RunOutcome` 已寫入），要回頭改執行歷程需要往 `SchedulerRunState` 加一條反向通道，
為一行備註不值得；寄送失敗的完整診斷（對象、主旨、例外）都在 `logs\web.log`。同理
D-2 的「錯誤記錄（NLog＋audit）」：自動觸發的寄送失敗只記 NLog（背景排程寫 audit 會把
稽核表灌成流水帳），管理者主動的「測試寄信」則有 audit 記錄。

### 文件更新

README.md（功能清單＋ Rising 閘門行為變更申報）、docs/RULES-SPEC.md（抑制目標四型完整規格＋
與 `NoiseMark` 的分工定案）、docs/DETECTION-SPEC.md（Rising 嚴重度閘門＋`PatternId`＋總量抑制）、
docs/WEB-SPEC.md（§9.7 抑制四型/UI、§9.9b 郵件通知分頁、新增 §9.9c 操作說明書、§10.2 blob key
表新增 `mail_notify_state`）皆已更新。**docs/DB-SPEC.md 未新增內容**——查證後確認該文件的既有
範圍僅涵蓋真正的 SQL 關聯表，`RuleSuppression`／`MailNotifyState` 皆是 `lf_blobs` 整份型儲存，
依既有慣例（如 `system_settings` blob）欄位級規格記在 WEB-SPEC.md，不重複記一份在 DB-SPEC.md。
