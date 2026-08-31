# PRTG 第 2 輪規劃（認證方式與介面收斂）

> 狀態：規劃中
> 基準：dev@c17f626（3148 綠，略過 6）
> 來源：使用者回饋五項（2026-08-31）
> 範圍裁定（使用者定案）：**本輪只做到「拿得到測試資料」**——阻擋取數的是認證方式
> （使用者的 PRTG 可能沒有 API token 功能），其餘 UI 重構全部遞延進 BACKLOG 等下一輪。

## 0. 回饋五項的處置總覽

| # | 回饋 | 本輪 | 理由 |
|---|---|---|---|
| 1 | 認證方式可選（帳號密碼登入） | **做（批次A）** | 唯一會擋住取數的項目：舊版 PRTG 沒有 API token，現況實作會整個不能用 |
| 2 | 環境探測平時收合、需要才展開 | **做（批次B，極小）** | 幾行改動，設定頁有 `<details>` 前例（`ai-advanced`），順手做掉 |
| 3 | 歷史回填搬到排程作業頁，設定頁只留參數 | 遞延 | 與 #4 是同一次 UI 重構，拆開做會搬兩次 |
| 4 | PRTG 拉成獨立維護頁（與 NetIQ 同層級、呈現參考 NetIQ） | 遞延 | 結構性搬家，不影響取數；等測試資料驗證過取數正確再動 UI |
| 5 | 主機頁面篩選 PRTG 對應狀態＋明細看 sensor＋手動對應 | 遞延 | 依賴真實對應資料的形狀（衝突/未對應的實際分布）再設計才不會做錯 |

#3/#4/#5 已寫入 `docs/BACKLOG.md`「PRTG UI 重構（第 3 輪候選）」，附觸發條件與定案方向，
見本文件 §4。

## 1. 批次A：認證方式（token／帳號密碼 二選一）

### 現況與核對結果

- 認證組裝**只有一處**：`PrtgClient.BuildUri`（`PrtgClient.cs:153`，第 1 輪規格明訂
  「apitoken 一律由這個方法附加」）——擴充認證方式時 client 層只動這附近。
- `PrtgClient` 建構子：`(baseUrl, token, timeoutSeconds, ignoreSslErrors, handler?)`（`:33`）。
- **四個建構呼叫端**：`AnalysisOrchestrator.cs:1072`、`PrtgBackfillService.cs:117`、
  `PrtgProbeService.cs:158`、`SystemSettingsService.cs:622`（測試連線）。前三處**各自複製了
  同一段 token 解密三元式**（`IsEncrypted ? Decrypt : 原值`）——本輪第四種認證資料一進來就是
  第四份拷貝，正好收斂成單一工廠。
- 帳密加密儲存前例：`Sentinel.Username`（明文）＋`Sentinel.PasswordEnc`（`CryptoHelper` 密文，
  write-only、DTO 只回布林、空字串沿用），`SentinelAdminService` 是完整範本。
- **PRTG API 認證事實**（v1，全 query string）：
  - `apitoken=<token>`（較新版本才有）
  - `username=<u>&passhash=<h>`——passhash 由 `GET /api/getpasshash.htm?username=<u>&password=<p>`
    取得（回應是純文字的一串數字）。這是 PRTG 官方長年的標準做法。
  - `username=<u>&password=<p>` 直掛也可行，但密碼明文進 URL（會出現在 PRTG 的存取 log 與
    任何中間設備），**不採用**。

### 定案

1. **兩種認證模式**：`token`（預設，行為同現況）與 `password`。選 passhash 而非密碼直掛：
   密碼只在「換 passhash」那一次請求中出現，之後所有請求 URL 帶的是 passhash。
2. **`SystemSettings` 新增三欄**（皆有當輪消費端）：
   - `PrtgAuthMode`（string，`"token"`｜`"password"`，預設 `"token"`——既有部署零行為變化）
   - `PrtgUsername`（string，明文，比照 `Sentinel.Username`）
   - `PrtgPasswordEnc`（string，密文 write-only，完全比照 `PrtgApiTokenEnc` 的三段式：
     Clear 旗標／有值才加密／否則沿用；DTO 只回 `PrtgHasPassword`）
3. **`PrtgClient` 擴充**：
   - 建構子改吃認證參數（token 模式帶 token；password 模式帶 username＋password 明文，
     由呼叫端解密後傳入——與現況 token 的傳遞方式一致）。
   - password 模式下，**首次請求前先呼叫 `getpasshash.htm` 取得 passhash 並快取在實例內**
     （client 每趟執行新建，每趟多一次輕量請求，可接受）；取失敗擲 `PrtgClientException`
     （401 → 「帳號或密碼錯誤」）。
   - `BuildUri` 依模式附 `apitoken=` 或 `username=&passhash=`；認證參數的附加**仍然只能在
     這一個方法**。
   - **遮蔽擴充**：`StripToken` 改為把 token、password、passhash 三者的原文與 URL 編碼形式
     都遮成 `***`（例外訊息可直接顯示的承諾不變）。
4. **新增 `PrtgClientFactory`（Core）**：`Create(SystemSettings)` 統一「依 authMode 解密憑證
   → 建 client」，四個呼叫端全部改走它，**刪掉三份重複的解密三元式**。工廠同時提供
   `HasUsableCredentials(SystemSettings)` 供 TryStart 類前置檢查共用（現況「無 token 就擋」
   的判斷在三處，改為「依模式判斷憑證齊不齊」後同樣收斂）。
5. **驗證**（`SystemSettingsService.Update`）：`PrtgEnabled` 時依模式檢查——token 模式要有
   token（新存或既有）、password 模式要有 username 且有密碼（新存或既有）。`PrtgAuthMode`
   只接受兩個合法值。
6. **測試連線**：`TestPrtgConnectionRequest` 加 `AuthMode`／`Username`／`Password`（留空沿用
   已存密文，比照既有 token 語意）；`TestPrtgAsync` 依模式組 client。
7. **設定頁 UI**：認證方式下拉（「API token」／「帳號密碼」），依選擇切換顯示 token 欄或
   帳密欄（含「清除密碼」勾選與已設定狀態顯示，比照 token 欄現況）；前端驗證與 PUT payload
   跟上。教學文件（歸檔區 `prtg-取值測試步驟.md`）第 1 關補帳密模式的說明。

### 測試 / 驗收

1. `PrtgClient`：password 模式首次請求先打 `getpasshash.htm` 且只打一次（請求計數斷言）、
   後續請求帶 `username=&passhash=` 不帶 `password=`；passhash 取得失敗擲例外且訊息含
   「帳號或密碼」；**例外訊息不含 password 也不含 passhash**（含 URL 編碼形式，比照既有
   token 遮蔽測試用含特殊字元的密碼）。
2. token 模式行為與現況完全相同（既有測試不動全綠）。
3. 設定驗證：password 模式啟用但缺 username／密碼 → 拒絕；`PrtgAuthMode` 非法值 → 拒絕；
   密碼三段式（存入密文／留空沿用／清除）。
4. 工廠：四個呼叫端 grep 零殘留的 `new PrtgClient(`（測試與工廠本身除外）、零殘留的
   解密三元式拷貝。
5. 全套綠且**測試總數比 3148 多至少 8 筆**。

## 2. 批次B：環境探測預設收合（Claude 自做，不委派）

### 定案

設定頁 PRTG 頁籤的「環境探測」整塊改包進 `<details>`（**不帶 `open`，預設收合**），
`<summary>` 為區塊標題——比照同頁 `ai-advanced` 的既有寫法。探測執行中或有歷史輸出時
不需要自動展開（使用者要看自然會點開；`<details>` 的展開狀態不影響輪詢邏輯）。

歷史回填區**本輪不動**（下一輪整塊搬去排程頁，現在收合它是白做）。

### 測試 / 驗收

前端無新測試；驗收＝`data-panel="prtg"` 內探測區在 `<details>` 內且無 `open` 屬性、
探測輪詢與複製功能行為不變（JS 零改動即為證）。

## 3. 作業總覽

| 批次 | 內容 | 執行者 | 規模 |
|---|---|---|---|
| A-1 | 後端：settings 三欄＋驗證＋`PrtgClient` passhash＋遮蔽＋`PrtgClientFactory` 收斂四呼叫端＋測試 | agy（暫定） | 中 |
| A-2 | 前端：認證方式下拉與欄位切換＋測試連線帶新欄位 | agy（暫定） | 小 |
| B | 探測收合 | Claude | 極小 |
| 文件 | PRTG-SPEC（認證一節）＋教學文件第 1 關＋BACKLOG 三項 | Claude | 小 |

## 4. 遞延項（寫入 BACKLOG「PRTG UI 重構（第 3 輪候選）」）

三項是同一次重構，一起做才不會搬兩次。方向先定案、細節下輪規劃：

1. **PRTG 獨立維護頁 `/admin/prtg`**：與 NetIQ 維護同層級（側欄「系統管理」內、緊鄰
   NetIQ），呈現參考 NetIQ 頁的頁籤結構（連線設定／鏡像狀態／探測）。設定頁的 PRTG 頁籤
   瘦身為純參數（併發、回填天數、保留天數），連線與操作全部搬過去。
2. **歷史回填搬到排程作業頁**：回填是排程性操作不是參數設定。
3. **主機頁面 PRTG 對應整合**：主機清單加「PRTG 對應」篩選（有對應／衝突／無）；主機明細
   顯示對應的 device 與 sensor 清單；**手動對應 UI**（把 unmatched/conflict 的 device 指給
   某台主機，人工結果優先於每日自動對應且不被覆蓋——同 sensor 分類欄「人工優先」的既有
   契約精神；需要 `lf_prtg_host_map` 之上新增人工對應的持久層，細節下輪定）。

觸發條件：本輪認證落地、實機取數驗證通過（教學文件五關全過）之後。

## 5. 明確不做（本輪定案）

- 上述遞延三項。
- NTLM／SSO 等其他 PRTG 認證方式（PRTG API 只有這兩條路）。
- 密碼直掛（`password=` 參數）——安全性較差且無必要。
- 多 PRTG server（維持第 1 輪定案）。

## 6. 複檢（規劃完成後）

- 與既有設計衝突：`PrtgAuthMode` 預設 `token` 保證既有部署零行為變化；工廠收斂**移除**了
  三份既有拷貝——移除類已 grep 全部呼叫點（四處，含測試連線），無白名單外依賴。
- 破壞性判準／單向閘門／分母為零：本輪無刪除、無一次性旗標；「憑證齊不齊」的判斷寫明
  「新存或既有皆算有」（否則留空沿用的既有語意會被誤判為缺）。
- 批次間介面：A-2 消費 A-1 的 DTO 欄位（`PrtgAuthMode`／`PrtgHasPassword`）；B 與 A 不相交。
- 教學文件在歸檔區（repo 外），文件批次要記得同步，已列入 §3。
- 複檢完成，無其他新增事項。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （待實作開始填寫） | | | | |
