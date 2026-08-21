# 回饋第二十五輪規劃（FEEDBACK-25）：IIS 子應用路徑前綴與品牌版面

## 0. 背景與範圍

輸入：使用者三項——①IIS 以 Application 掛載時網址帶前綴（`/LogForesight/...`），全站寫死
根路徑造成 404（登入頁「無法取得登入設定」即此症狀）②登入頁品牌整體置中＋文字放大到
上下貼齊圖片 ③側欄品牌同樣處理。

**已定案決策**（2026-08-21 與使用者討論定案）：
- P1 採**集中前綴**：`_Layout` 與 `Login`（不套主版面，各自注入）server-render
  `window.LF_BASE = Request.PathBase`；`api.js` 單一出口自動補前綴（約 170 處 `/api/...`
  字串零改動）；新增 `appUrl()`（連結組裝）與 pathname 正規化 helper；cshtml 用
  `@Url.Content("~/...")`；後端 Redirect／returnUrl 帶 PathBase。**不採** `<base href>`＋
  相對路徑（多層路由下相對深度心算是災難）。
- 新增 `Server:PathBase` 設定鍵（預設空）供 Kestrel 反代情境與本機驗證；IIS in-process
  掛載時 ASP.NET Core 自動填 `Request.PathBase`，不依賴此鍵。
- 品牌「貼齊」的語意是**視覺貼齊**（字形頂/底對到圖片上下邊），不是行盒貼齊——側欄行盒
  合計已等於圖片高但視覺仍有行高留白。修法：文字容器改上下分佈（首行頂貼、末行底貼）、
  字級放大、行高收緊；整組置中、文字仍靠左貼齊圖片。
- 副標為空時固定字級無法撐滿：以「有副標」為設計基準，無副標維持垂直置中。
- 順手全修：favicon `<link>`（缺失，前綴下必 404）、`imports.js` 繞過 api.js 的破例收斂、
  README IIS 矛盾收斂＋新增 IIS 子應用部署章節。
- WEB-SPEC 立紅線：新程式碼一律走 api.js／`appUrl()`，不准寫死 `/` 開頭路徑。

**明確不做**：改 controller 屬性路由（相對 PathBase，本來就對）；改 css/js 資源引用
（已是 `~/` tag helper）；改 ES module import（已相對）。

## 1. 事實核對摘要

| 項 | 事實 |
|---|---|
| fetch 單一出口 | `core/api.js:42` 全站唯一 fetch（唯一破例 `imports.js:47` multipart） |
| 要掃的呼叫點 | js 導航/連結組裝約 60 處；`location.pathname` 比對 2 處（`api.js:56` 登入判斷、`layout.js` 選單高亮/BUSINESS_PAGES）**邏輯失效不只 404**；`ui.js:30` sprite 單點 |
| cshtml | sprite `<use href="/img/icons.svg#">` 約 87 處；頁面連結 9 處；下載連結 1 處；`asp-*`／`Url.*` 現為零使用 |
| CSS | `site.css:20-56` 字型 5 處絕對路徑 → 改相對（`../fonts/`） |
| 後端 | `ServiceCollectionExtensions.cs:164-165`（OnChallenge redirect＋returnUrl）、`ActiveUserMiddleware.cs:41` 皆不帶 PathBase；`Program.cs` 無 UsePathBase；cookie Path=`/` 可留（範圍較廣無害）；`AiInsightService`／`SetupReadinessService` 回傳 app 相對連結由前端補前綴 |
| favicon | 無 `<link rel="icon">` |
| 品牌量測 | 側欄 mark 2.75rem、name 1.45rem/1.15、sub .78rem/1.25（行盒合計 2.7425rem）；登入 mark 3rem、title 1.45/1.15、sub .86/1.25（合計 2.7425，短 0.26rem）；兩處整組皆靠左未置中 |
| settings.js 耦合 | 副標樣式靠 `small` 類型選擇器（JS 動態建立無 class）；`lf-brand-mark`/`lf-brand-name`/`.lf-sidebar__brand-text` 三個查找點不可改名/改層級 |

## 2. 作業總覽

委派模型：**無**——開工前查額度時兩池皆為 0%（Gemini 週限與 Claude 池同時用罄），
依使用者「額度用完再自己做」的既有指示，本輪全部由 Claude 自做。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | base path 基礎機制（注入、api.js 前綴、helper、後端、設定鍵、字型、favicon、imports.js 收斂） | 無 | agy |
| B | 全站呼叫點掃描（js 約 60 處＋cshtml 約 97 處改走 helper／Url.Content） | A | agy |
| C | 品牌版面（置中＋視覺貼齊＋字級放大）＋整站前綴實測 | A、B | Claude |

## 3. 作業明細

### 作業 A-階段 1：base path 基礎機制（agy）
- **背景**：站台將部署在 IIS 子 Application（`/LogForesight/`）。IIS in-process 掛載會自動填
  `Request.PathBase`，但全站前端寫死根路徑、後端兩處 Redirect 不帶前綴。
- **契約**：
  1. `_Layout.cshtml` 與 `Login.cshtml`（不套主版面）各注入
     `window.LF_BASE = '@Context.Request.PathBase'`（空字串或 `/LogForesight` 形式，
     **不含尾斜線**），置於任何 module script 之前。
  2. `core/api.js`：`request()` 對 `/` 開頭的 url 自動補 `LF_BASE` 前綴（單一出口，其他頁
     模組的 `/api/...` 字串不動）。新增並匯出兩個 helper：
     - `appUrl(path)`：`/` 開頭補前綴後回傳（給連結組裝與 `location.href` 用）
     - `appPath()`：回傳去掉前綴的 `location.pathname`（給路由比對用）
     實作位置放 api.js 或新的 core 模組皆可，但**只准一份**。
  3. `api.js` 內部自己的兩處也要改：401 攔截的 `location.href = '/login?...'` 走 appUrl；
     `isLoginAttempt` 的 `location.pathname === '/login'` 走 appPath。
  4. `imports.js:47` 的直接 fetch 收斂回 api.js（api.js 需支援 FormData body——**不得**對
     FormData 設 Content-Type，瀏覽器要自帶 boundary）。
  5. 後端：`ServiceCollectionExtensions` OnChallenge 的 redirect 與 returnUrl、
     `ActiveUserMiddleware` 的 redirect，皆改為帶 `Request.PathBase`。
  6. 新增設定鍵 `Server:PathBase`（appsettings，預設空）：非空時 `Program.cs` 早於其他
     middleware 呼叫 `UsePathBase`；appsettings 註解寫明「IIS 子應用不需設定（自動），
     僅 Kestrel 反代掛前綴時使用」。
  7. `site.css` 5 處字型 `url("/fonts/...")` 改相對 `url("../fonts/...")`。
  8. `_Layout.cshtml` 與 `Login.cshtml` 補 `<link rel="icon" href="~/favicon.ico">`。
- **範圍**：`LogForesight.Web`（Program.cs、Extensions、Middleware、_Layout、Login、
  core/api.js、pages/imports.js、site.css、appsettings 註解）＋測試。不准動其他頁面 js／
  其他 cshtml／docs/。
- **驗收**：build 警告 ≤1；test 全綠既有不少（基準 2469 總／略過 6），新增至少 4 支
  （OnChallenge redirect 帶 PathBase／returnUrl 含 PathBase／ActiveUserMiddleware redirect
  帶 PathBase／`Server:PathBase` 設定生效——用 WebApplicationFactory 或等效方式走真實管線）。
  grep：`imports.js` 不再出現 `fetch(`。
- **回報**：檔案清單、測試數字、helper 的匯出形狀、偏離與理由。

### 作業 B-階段 1：全站呼叫點掃描（agy）
- **背景**：作業 A 提供了 `appUrl()`／`appPath()`；本階段把所有寫死根路徑的**非 API** 呼叫點
  換過去。API 字串（傳給 api.get/post/put/delete 的 `/api/...`）**不要動**——出口已統一補前綴。
- **契約**：
  1. **js**（wwwroot/js/**）：所有 `location.href = '/...'`、`link.href = '/...'`／
     `` `/records?...` `` 這類連結組裝、`cell.href`、`rowHref`、圖表下鑽 url、側欄選單表
     （`layout.js` 的 href 與 `BUSINESS_PAGES`）、`ui.js:30` 的 sprite 路徑、
     `dashboard.js:164` 的 `item.link`（後端回傳 app 相對連結）——全部改走 `appUrl()`。
     `location.pathname` 的比對（選單高亮等）改走 `appPath()`。
  2. **cshtml**：`<use href="/img/icons.svg#x">` 改 `<use href="@Url.Content("~/img/icons.svg")#x">`
     （或等效的單一寫法，整批一致）；9 處頁面連結與 1 處下載連結改 `@Url.Content("~/...")`。
  3. 不改行為：改完後在**無前綴**環境所有 URL 與現狀完全相同（`LF_BASE` 為空字串時
     `appUrl('/x') === '/x'`）。
- **範圍**：`wwwroot/js/**`（除 core/api.js——A 已完成）、`Views/**`。不准動後端、docs/。
- **驗收**：build 警告 ≤1；test 全綠既有不少。**grep 驗收（核心）**：
  - `wwwroot/js` 內不應再有 `location.href = '/`、`href = '/`、`` href = `/ ``、
    `location.pathname ===`／`.startsWith('/`（路由比對類）殘留——允許的例外只有
    `appUrl(`／`appPath(` 的實作本身與 `'#'` 佔位。
  - `Views` 內不應再有 `href="/`（`~/` 與 `@Url.Content` 除外）。
  - 逐一列出你判定「不需要改」而留下的 `/` 開頭字面值及理由（例如 api 字串、regex）。
- **回報**：改動檔案清單（一行一檔＋處數）、grep 結果、留下未改清單與理由。

### 作業 C（Claude 自做）
- 契約：
  1. 品牌版面：兩處（側欄／登入）整組置中（文字仍靠左貼齊圖片：文字容器 `flex:0 1 auto`
     ＋外層 `justify-content:center`，長名稱 ellipsis 保留）；文字容器改上下分佈
     （首行頂貼、末行底貼圖片邊），字級放大＋行高收緊——暫定側欄 name 1.7rem／登入
     title 1.85rem、行高 1.05，副標按現行比例放大，實測視覺微調。無副標維持垂直置中。
     不動 `lf-brand-mark`／`lf-brand-name` id 與 `small` 類型選擇器（settings.js 契約）。
  2. 整站前綴實測：本機以 `Server:PathBase=/LogForesight` 啟動，逐頁走過（登入→儀表板→
     問題查詢→下鑽→權限異動→設定→匯入→說明書），確認無 404、選單高亮正確、圖示顯示、
     登入跳轉 returnUrl 正確；再以無前綴啟動確認行為不變。
- 驗收：build/test 全綠；兩種模式實測截圖級確認。

## 4. 測試計畫

A 的四支後端測試；B 靠 grep＋C 的整站實測兜底（前端無自動化測試機制）。

## 5. 文件更新（全部驗收後 Claude 寫）

- README：「Web 部署」新增 IIS 子應用章節（in-process、自動 PathBase、`dotnet publish` 產
  web.config）；收斂 :457 與 :580 的矛盾（IIS 現為支援的部署方式之一）；`Server:PathBase`
  設定表列。
- WEB-SPEC：路徑規約紅線（新程式碼一律 api.js／appUrl，不寫死 `/`）＋ `LF_BASE` 注入機制；
  §9.5 品牌版面規則更新（如有描述）。
- CLAUDE.md「不要做」加一條路徑紅線。
- 本檔完工後歸檔。

## 6. 風險與回滾

- B 是大面積機械改動（約 160 處）：靠「無前綴時 URL 與現狀完全相同」的不變式＋grep 驗收
  ＋C 的雙模式整站實測兜底。
- `Url.Content` 在 87 處 sprite 的 Razor 求值成本：每處一次字串組合，量級無虞。
- 各作業獨立 commit，單獨 revert。

## 7. 執行紀錄

結案基線：2479 總／2473 綠／略過 6（開工時 2469）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A | Claude | `e99fd1e` | 2472 總 | agy 兩池額度皆 0%，本輪無法委派，全部自做 |
| B | Claude | `ad0b96d` | grep 全清＋雙模式實測 | `appUrl/appPath` 抽成 `core/paths.js`：ui.js 也要用，而 api.js 用 ui.js 的 toast，放一起會循環相依 |
| C | Claude | `2af6545` | 瀏覽器實測 gapTop/gapBottom 皆 0 | 側欄字級由規劃的 1.7rem 下修為 1.6rem——實測「LogForesight」加 letter-spacing 約 150px、可用寬僅約 157px，1.7rem 會踩省略號 |

### 併回前終檢（兩個獨立審查）

**程式碼審查成立並修正**：
1. **4 處遺漏**（都是實際會壞的動線）：儀表板主機排行連結、AI 洞察的下鑽連結
   （值來自後端 `AiInsightService`）、**啟動精靈每一步的「前往設定」**、說明書的章節連結
   （值來自 `manifest.json`）。共通點是「值不是字面量、而是來自後端或資料檔」——
   機械 grep 只掃得到字面量，這類要靠追資料來源。
2. **子應用下的 cookie 覆蓋**（規劃時判定「Path=/ 無害」，該判斷在子應用情境不成立）：
   同一台主機掛正式／測試兩個 Application 時 cookie 同名同 Path，後登入的直接蓋掉前一個，
   症狀是「身分變成另一個環境的人」而非 401。改為以 PathBase 為範圍，並抽出
   `AuthCookieOptions` 讓寫入與刪除共用（Path 不一致會刪不掉）。
3. `appPath` 大小寫敏感：IIS 路由不分大小寫，使用者打 `/logforesight/...` 會讓比對失手、
   選單靜默不高亮。改為大小寫不敏感比對（切片仍用原字串）。
4. `imports.js` 收斂後同一錯誤跳兩個 toast（api.js 一次、catch 一次）→ 傳 `silent`。
5. 補 OnChallenge 的測試：從 DI 取出**實際註冊的 lambda** 來跑，而非複製邏輯——
   這是本輪最容易寫反的一處（returnUrl 必須不含前綴、轉址目標必須含前綴）。

**文件審查成立並修正**：WEB-SPEC §8.5 的 sprite 引用寫法**明文教人寫死絕對路徑**
（照著寫新頁面就會再犯），已改為 `@Url.Content` 並保留「Razor 不解析 `<use>` 內的 `~/`」
的理由；新增 §8.1a 路徑規約（含匯集點只套一次的規則）；README 收斂「不需要 IIS」的既有
矛盾＋新增 IIS 子 Application 部署章節＋設定表與範例補 `Server:PathBase`；CLAUDE.md 加紅線。

### 體檢輪（終檢修正 commit 的獨立審查）

**程式碼審查成立並修正**：
1. **升級情境的雙 cookie**（高，安全面）：既有部署的舊 `Path=/` cookie 與新的
   `Path={PathBase}` 同名並存；ASP.NET Core 解析 Cookie 標頭時後到的值覆寫先到的，
   舊 token 會蓋過新 token 且重登救不回來，登出也只刪新範圍、舊 token 永遠有效。
   修法：寫入與刪除都連同 `Path=/` 的舊 cookie 一併清（一次性清理，數版後可移除）。
2. cookie Path 用 `PathBase.Value`（解碼後）與轉址（編碼後）不一致：掛載名稱含空白或
   非 ASCII 時 path-match 永遠比不中，變登入迴圈。改 `ToUriComponent()`。
3. `LF_BASE` 注入走 Razor HTML 編碼器：非 ASCII 會編成 `&#x…;` 且 `<script>` 內不解碼，
   全站前綴靜默全錯。改 `Html.Raw(JsonSerializer.Serialize(...))`。
4. cookie 邏輯自 Controller 移到 `Auth/AuthCookie`（middleware 反向依賴 Controller 違反
   分層慣例）；測試改用既有 `FakeSystemSettingsStore`（原重造了一份）；補 cookie Path
   一致性與編碼形式的測試。實測驗證：登入回應同時送出「刪 `path=/`」與
   「寫 `path=/LogForesight`」兩個 Set-Cookie。

**文件審查成立並修正**：§8.1a 匯集點規則改為「誰寫入誰套一次」（原敘述會把頁面模組的
正確寫法讀成違規）；§8.5 sprite 段刪掉殘留的舊寫法示範（改一半反而自相矛盾）；
_Layout 註解指錯檔（appUrl 在 paths.js 不在 api.js）；site.css 副標註解與值矛盾；
README IIS 章節與設定表的重複收斂。

**規劃自己的落差**：§5 寫「WEB-SPEC §9.5 品牌版面」是錯的——§9.5 是權限異動待辦，
品牌在 §9.9b，且該段不描述版面字級，本輪不需要動它。
