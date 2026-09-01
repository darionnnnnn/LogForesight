# PRTG 第 3 輪規劃（新增 username＋passhash 直接登入）

> 狀態：全案完成已併 dev
> 基準：dev@d6300f8（3181 綠，略過 6）
> 來源：使用者需求（2026-08-31）——認證方式新增第三種：直接提供 username＋passhash
> 註：原「第 3 輪候選」的 UI 重構順延為下一輪，觸發條件不變（實機五關驗證通過）

## 0. 需求與定位

現況兩種認證：`token`（apitoken）、`password`（存密碼，系統自動打 `getpasshash.htm` 換
passhash）。新增第三種 **`passhash`**：使用者自己取得 passhash（PRTG 帳號設定頁的
Show passhash，或自行呼叫 `getpasshash.htm`），LogForesight **只存 username＋passhash、
完全不經手密碼、不打 `getpasshash.htm`**。

適用情境：安全政策不允許第三方系統保存人員密碼（即使加密）；或想避免系統端任何一次
帶密碼的請求。passhash 等價於密碼（拿到就能用），**儲存等級比照密碼**：加密、write-only、
DTO 只回布林。

## 1. 現況與核對結果

- `PrtgAuthModes`（`PrtgConstants.cs`）：`Token`／`Password` 兩值＋`IsValid`（唯一合法值判定點）。
- `PrtgClient`：`_isPasswordMode` 決定走 `EnsurePasshashAsync`（首次換 passhash、快取於
  `_cachedPasshash`、憑證錯誤黏住）；`BuildUri` 依模式附 `apitoken=` 或 `username=&passhash=`。
  **資料請求的 401 訊息已分模式**（`:252-253`）；`StripSecrets` 已遮 token／password／
  `_cachedPasshash` 三者（passhash 模式下直接填入快取即自動被遮蔽涵蓋）。
- `PrtgClientFactory`：`ResolveCredentials`（internal，體檢輪抽出、有解密行為測試）＋
  `HasUsableCredentials`。
- `SystemSettingsService`：驗證依模式檢查「新存或既有」（`HasEffectiveSecret`）；
  **寫入依模式隔離**（隱藏欄位殘值不寫入——本輪加第三組憑證時必須維持這個契約）；
  測試連線留空沿用已存密文。
- 前端：`syncPrtgAuthFields` 切換兩個容器並清空隱藏側；**`prtg-username` 欄目前埋在
  密碼容器內**（`Settings.cshtml:773-777`）——passhash 模式也需要 username，要抽出共用。

## 2. 定案

1. **`PrtgAuthModes.Passhash = "passhash"`**，`IsValid` 擴為三值（仍是唯一判定點）。
2. **`SystemSettings` 新增一欄 `PrtgPasshashEnc`**（密文 write-only，處理三段式與
   `PrtgPasswordEnc` 完全對稱：Clear 旗標／有值才加密／留空沿用；DTO 只回
   `PrtgHasPasshash`）。username 由 `password`／`passhash` 兩模式**共用既有的
   `PrtgUsername`**，不另開欄位。
3. **`PrtgClient`**：建構子加 `passhashOrEmpty` 參數（維持單一建構子）。passhash 模式＝
   建構時直接把提供的 passhash 填入 `_cachedPasshash`——`EnsurePasshashAsync` 天然短路、
   `BuildUri` 的 `username=&passhash=` 分支照走、遮蔽自動涵蓋，**不新增任何分支邏輯**。
   passhash 模式下 username 為空同樣建構期擲例外。
   **（終檢修正原定案）憑證錯誤黏住機制要延伸到資料請求層**：原本判斷「passhash 模式不需要
   黏住」是錯的——passhash 是帳號類認證，資料請求的 401 一樣會累加 PRTG 端的帳號鎖定計數，
   三千個 sensor 就是三千次認證失敗。改為：帳號類認證（`password`／`passhash`）的資料請求
   401 一律黏住；403 不黏（可能是單一物件授權不足）；`token` 模式不黏（token 失效不鎖帳號）。
   這使 `password` 模式的資料請求 401 行為也一併改變（原本每次都重試），屬於刻意的擴張。
4. **`PrtgClientFactory`**：`ResolveCredentials` 回傳擴為四元組（加 Passhash）；
   `HasUsableCredentials`：passhash 模式＝`PrtgUsername` 與 `PrtgPasshashEnc` 皆非空。
5. **`SystemSettingsService`**：
   - 驗證：passhash 模式＝username 必填＋passhash「新存或既有」；`IsValid` 三值自動生效。
   - 寫入隔離改三分支：token→只寫 token；password→username＋密碼；passhash→username＋passhash。
   - 測試連線：`TestPrtgConnectionRequest` 加 `Passhash` 欄，留空沿用
     `DecryptSavedPrtgPasshash()`（新增，形狀同既有兩個 DecryptSaved 方法）。
6. **前端**：下拉三選項（「API token」／「帳號密碼」／「帳號＋passhash」）。
   **`prtg-username` 欄抽出到兩個憑證容器之外**，由 `syncPrtgAuthFields` 控制
   「token 模式隱藏、其餘顯示」；密碼容器剩密碼欄，新增 passhash 容器（password 型輸入＋
   已設定提示＋清除勾選，比照密碼欄）。切換時清空規則跟上：隱藏的**憑證**欄清空，
   username 在 password↔passhash 之間切換時**不清**（兩模式共用）。
   前端驗證補 passhash 分支（與後端訊息一致）。
7. **文件**：PRTG-SPEC §6a 補第三模式（表格加一列＋「不打 getpasshash」說明）；
   教學文件事前準備與第 1 關補第三選項（含怎麼從 PRTG 取得 passhash）；WEB-SPEC 的
   fallback 句擴為三者。

## 3. 批次

| 批次 | 內容 | 執行者（暫定） | 規模 |
|---|---|---|---|
| A | 後端：常數＋settings 欄＋client＋factory＋驗證與寫入隔離＋測試連線＋測試 | agy | 中小 |
| B | 前端：下拉三選項＋username 抽出共用＋passhash 容器＋驗證 | agy | 小 |
| 文件 | SPEC／教學／WEB-SPEC | Claude | 小 |

### 批次A 測試（要點）

1. passhash 模式：請求帶 `username=&passhash=`、**完全不請求 `getpasshash.htm`**（請求清單斷言）、
   不帶 `password=`。
2. 例外訊息不含 passhash（原文與 URL 編碼形式；用含特殊字元的 passhash）。
3. `ResolveCredentials`：passhash 分支解密輸出正確、與另兩模式不互相污染。
4. 驗證：passhash 模式缺 username／從未設定 passhash 時拒絕；留空沿用；`ClearPrtgPasshash` 清空。
5. **寫入隔離三分支互不污染**（擴充既有的「切換模式不污染」測試為三模式矩陣）。
6. username 共用語意：password 模式存的 username，切到 passhash 模式仍在（不被清）。

## 4. 明確不做（本輪定案）

- 不做「輸入密碼→系統換出 passhash 並顯示給使用者」的輔助功能（教學文件教手動取得即可）。
- 不動 UI 重構三項（順延下一輪）。
- 不改 `password` 模式的 `getpasshash.htm` 換取流程與其黏住判定。
  （**例外**：資料請求層的 401 黏住是本輪新增的共通保護，`password` 模式一併適用，理由見定案 3。）

## 5. 複檢（規劃完成後）

- 與既有契約衝突：寫入隔離從二分支改三分支——「隱藏欄位殘值不寫入」契約維持；username
  的共用語意是新決定（password↔passhash 切換不清、token 模式不寫），已明寫進定案 6 與測試 6。
- 單向閘門／破壞性判準：無新增。`PrtgAuthMode` 既有值不受影響（三值向後相容）。
- 批次間介面：B 消費 A 的 DTO 欄位（`PrtgHasPasshash`）。
- 複檢完成，無其他新增事項。

## 6. 執行紀錄

委派模型：`gemini-3.7-flash-high`（整輪未切換）。基準 dev@d6300f8（3181 綠）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A：後端（常數／設定欄／client／工廠／驗證與寫入隔離／測試連線） | agy | 通過 | 3193 綠（+12） | 無落差。`_usesUsernameAuth`／`_needsPasshashExchange` 的語意拆分完全照規格；passhash 靠預填 `_cachedPasshash` 達成，`BuildUri` 與 `StripSecrets` 零新增分支。規格外的良性加固一項：`EnsurePasshashAsync` 加了「passhash 模式走到換取邏輯＝程式錯誤」的防禦性例外 |
| B：前端（三選項／username 抽出共用／passhash 容器／驗證） | agy | 通過 | 3193 綠（前端不增測試） | 無落差。BOM 維持、零 `innerHTML`、零寫死路徑；清空規則與後端寫入隔離一致 |
| 終檢（獨立 Explore 審全 diff） | Claude | 完成 | 3196 綠 | 見下方 |

### 終檢處置

| 嚴重度 | 問題 | 處置 |
|---|---|---|
| 高 | **passhash 模式的資料請求 401 沒有任何黏住保護**（Claude 自審 401 路徑時抓到，終檢獨立確認）：passhash 是帳號類認證，貼錯一碼或 PRTG 端改密碼使舊 passhash 失效時，三千個 sensor 就是三千次帶帳號的認證失敗——與上一輪修掉的 getpasshash 連打是同一種帳號鎖定風險，而規劃階段誤判為「不適用」 | 資料請求層加黏住：帳號類認證的 401 黏、403 不黏（可能是單一物件授權不足）、token 模式不黏。三條測試分別鎖住這三種情況 |
| 中 | 實作與規劃定案 3／§4 相反（黏住延伸到資料請求層，且 `password` 模式一併適用） | 回寫定案 3 與 §4，標明是終檢修正原定案並寫出理由 |
| 中 | `docs/PRTG-SPEC.md` 的黏住段只描述 getpasshash 路徑，且「401/403 都黏」對資料請求路徑是錯的——日後有人依此把 403 不黏的刻意設計改掉 | 改寫成兩條路徑各自的黏／不黏對照表 |
| 低 | `PRTG_username在password與passhash模式間切換時不被清空` 兩段帶同一個 username，分不出「有寫入」與「沒動到」——刪掉 passhash 分支的 username 寫入（真實後果：passhash 模式下改不了帳號）測試仍全綠 | 改帶不同值並補「切回 password 也要能改」一段；以突變測試確認原版漏得掉、強化後會紅 |

**查證後不成立**：終檢把「工作區未 commit 的 401 黏住改動」列為擋路級——那是它讀 HEAD 時
我正在修的東西；另指教學文件未更新，實際上該文件在 repo 外的歸檔區（`桌面/Claude/logforesight/`），
本輪已同步第三種認證的取得方式與測試步驟。
