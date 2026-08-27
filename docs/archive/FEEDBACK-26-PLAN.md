# 回饋第二十六輪規劃（品牌副標／權限異動說明／報表圓餅／保留期 90／問題查詢）

## 0. 背景與範圍

輸入：使用者回饋 P1～P5.3（2026-08-21）＋核對階段順手發現的 bug。

| # | 項目 | 處理 |
|---|---|---|
| P1 | 側欄與登入頁副標題對齊 | 作業 A |
| P2.1 | 權限異動彙總列說明重複類別前綴、看不到完整數字 | 作業 B |
| P2.2 | 彙總明細看不出時間區間；異動前後皆空仍顯示 | 作業 B |
| P2.3 | 4670（Token/svchost）說明句沒重點、殘留髒值 | 作業 B |
| P3 | 報表甜甜圈高度跑版、百分比歪斜 | 作業 C |
| P4 | 保留期最短 90／預設低於 90 改 90 | 作業 D |
| P5.1 | 依問題視角「其他」類多無說明 | 作業 E |
| P5.2 | 依問題視角欄位換行省寬 | 作業 E |
| P5.3 | 期間快捷加「昨日」 | 作業 E |

**已定案決策**（含根因）：

- **A**：副標題比主標題窄→撐 `letter-spacing` 讓左右貼齊主標題；比主標題寬→靠左對齊主標題。純 CSS 做不到單行 justify，需前端量寬度計算；兩處品牌區塊改為**共用一份 partial／CSS**（目前是平行複製，且登入頁 logo 頂貼、側欄置中已不一致）。設定頁與文件用詞「產品名稱」改為「主標題」（`BrandName` 鍵名不動）。
- **B1**：根因＝`summary` 類別句首保留「類別標籤：對象」是 §9.5 的例外，但類別欄已顯示標籤，重複無資訊量。改為句首只留對象。「本日另有 N 則…」那批列是舊版產物（現行已不產生，commit 0a4f4b9），**不改其文字、不重寫**；另加一次性清理：刪除 `change_type='權限異動（彙總）'` 的舊列（這批列的產生邏輯已不存在、也無法補資料，留著只會誤導）。
- **B2**：根因＝彙總列只有日期、沒有首末時間、沒有計數欄。新增彙總列的「涵蓋起訖時間」欄位（暫定兩欄），只對新產生的彙總列有值；舊列顯示「—」。異動前／異動後**兩者皆空時整塊不渲染**。
- **B3**：根因有三：(i) 解析器沒擷取「物件類型／處理程序名稱」；(ii) 重剖回填只在剖得出值時覆蓋，舊解析器留下的「訊息尾巴」髒值永遠洗不掉；(iii) 4670 是「物件權限變更」（含 Token／登錄機碼），一律歸「資料夾權限異動」語意錯。決策：新增類別 `object_acl`「物件權限變更」；4670 依物件類型分流——File/Directory 類歸 `folder_acl`，其餘歸 `object_acl`；句型「{操作者} 變更 {物件類型} 物件（{處理程序名稱}）的權限」；重剖對「髒值形狀」允許洗成空並重算類別；存量重剖再跑一次（重剖狀態旗標需版本化，見閘門反例）。
- **C**：根因＝`charts.js` 的 `baseOptions` 無條件帶 `scales:{x,y}`，doughnut 沒覆寫→Chart.js 建出 0～1 線性軸佔掉繪圖區，圓被壓小偏移；中心字定位在 wrapper 幾何中心故歪斜。一併修：風險占比補中心字、`renderNoData` 不得刪 canvas、三圖高度一致、doughnut 不套 `interaction.mode='index'`。
- **D**：六鍵最小值全改 90；預設只改低於 90 者（`RiskyEventRetentionDays` 14→90），其餘預設不動（120/120/120/90/730）。消滅第二份硬編預設（`RetentionSettings` record 與 NetIQ pipeline 的 14）——改為引用 `SystemSettings` 的單一常數來源。**讀取端不 clamp**：已儲存的低值照舊生效（「已儲存不動」字面意義），只在設定頁儲存時攔。順手：WEB-SPEC:2259 過時的 `DbRetentionDays(730)`；前端驗證補 `Detail<=Retention`。
- **E1**：依問題視角的「主機數／主機日」「vs 基準」「動作欄」改為允許換行（主機數與主機日上下兩行；vs 基準徽章與說明上下；動作鈕直排）。
- **E2**：快捷鈕三份複本（records／reports／dashboard）抽進 `core/`，統一屬性與錨點函式；問題查詢加「昨日」；報表「本週／本月」標籤改「近 7 天／近 30 天」（值本來就是滾動天數）；WEB-SPEC §8.6-8 校正。
- **E3**：三層根因一次收：(a) 「其他」且無說明時，問題欄補固定說明「未命中任何規則的事件，分類為其他」；(b) `EfIssueAggregateQuery` 的 `Category = MIN(字串)` 是字典序不是語意，改為取**最近一筆**（LastSeen 那天）的分類；(c) `FindRule` 只認 `Platform=="windows"`，Linux 問題白話說明恆 null——Linux 事件改走 `FindLinuxRule` 取說明。
- **順手項全做**：`renderNoData` 刪 canvas、WEB-SPEC:2259、前端 retention 驗證缺一條、`PermissionCategory.Resolve` 死參數（B3 會用到 eventId/物件類型，改為真的使用而非刪除）。

**明確不做**：權限異動頁新建快捷鈕（該頁無此 UI，非本輪訴求）；彙總列補舊資料的時間區間（無來源）；讀取端 clamp 保留期。

## 1. 事實核對摘要

| 項 | 結果 | 關鍵證據 |
|---|---|---|
| P1 | ⚠️ | 兩處平行複製不共用（`_Layout.cshtml:30-52`／`Login.cshtml:26-47`；`site.css:522-575`／`1958-1994`）；文字來自 DB `BrandName/BrandSubtitle`；現行無字距邏輯 |
| P2.1 | ⚠️ | 前綴在 `PermissionChangeService.cs:593-598`（§9.5 例外）；「本日另有」已不在程式碼；截斷是 CSS `max-width:28rem` |
| P2.2 | ✅ | `HostDayPostProcessor.cs:289-305`：`DetectedAt=date.Date`、`Before/After=""`、無計數欄 |
| P2.3 | ✅ | 4670→`權限變更`→`FolderAcl`（`HostDayPostProcessor.cs:77`、`PermissionCategory.cs:50`）；解析器無物件類型欄；重剖守衛 `PermissionChangeReparser.cs:79` |
| P3 | ✅ | `charts.js:79-88,113-121`；`reports.js:605-639` 無 setCenterText；`charts.js:245-249` replaceChildren |
| P4 | ⚠️ | 預設／min 見 §3 作業 D；第二份硬編 `AnalysisOrchestrator.cs:1038-1043`、`NetiqPipelineService.cs:101`；設定為 blob 整包覆寫，缺 blob 用型別預設 |
| P5.1 | ✅ | `records.js:975-1011` 四層；`KnownIssueCatalog.cs:351` windows-only；`EfIssueAggregateQuery.cs:88` MIN |
| P5.2 | ✅ | `records.js:683-721` text-nowrap；動作欄 `:1029-1045` |
| P5.3 | ✅ | `Records.cshtml:40-42`；三套複本，儀表板已有「昨日」 |

## 2. 作業總覽

本輪委派模型：**未委派**。開工前查額度：Gemini 池週限 0%（2026-08-21 20:18 重置）、
Claude 池週限 0%（2026-08-22 21:39 重置），兩池皆用罄，全案由 Claude 自行實作。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A 品牌區塊 | 共用 partial＋副標對齊規則＋用詞「主標題」 | — | Claude |
| B 權限異動說明 | B1 前綴／舊列清理；B2 區間欄位＋明細；B3 4670 物件權限類別＋解析＋重剖 | — | Claude |
| C 報表甜甜圈 | 修 scales／中心字／noData／等高 | — | Claude |
| D 保留期 | 常數單一化＋min/預設＋測試＋文件 | — | Claude |
| E 問題查詢 | E1 換行；E2 快捷共用＋昨日；E3 說明缺漏三層 | — | Claude |

作業彼此獨立，可任意順序；建議 C→D→A→E→B（由小到大）。

## 3. 作業明細

### 作業 A-階段 1：品牌區塊共用化＋副標對齊
- **背景**：側欄與登入頁各有一份品牌區塊 HTML／CSS；副標題需依寬度決定字距貼齊或靠左。
- **契約**：
  - 兩處品牌區塊改為同一份 partial（logo＋主標題＋副標題），尺寸差異只允許以 CSS 變數／修飾類別表達；兩處 logo 一律垂直置中。
  - 副標題寬度（自然字距）< 主標題寬度 → 用 `letter-spacing` 撐到與主標題左右邊緣貼齊（末字不留尾距）；≥ 主標題寬度 → 靠左對齊主標題左緣、字距正常、可 ellipsis。
  - 量測在字型載入完成後與視窗 resize 時重算；副標為空時不渲染節點（既有行為不變）。
  - 設定頁標籤與說明、HelpContent、WEB-SPEC §9.9b 2d：「產品名稱」改「主標題」、「副標文字」改「副標題」；`BrandName`/`BrandSubtitle` 鍵名與 API 不動。
- **範圍**：`Views/Shared/`、`Views/Pages/Login.cshtml`、`Settings.cshtml`、`wwwroot/css/site.css`、`wwwroot/js/core/`（新增一個小模組或放 layout.js）、`HelpContent/12-settings.md`。不動 BrandProvider／SystemSettings。
- **驗收**：`dotnet build` 零警告、`dotnet test` 全綠；grep `lf-login__brand-text`／`lf-sidebar__brand-text` 兩套平行類別只剩一套；grep `產品名稱` 在 Views／HelpContent／docs（archive 除外）為 0。人工：副標「事件日誌預警」（4 字）在主標「LogForesight」下左右貼齊；把副標改成長句時靠左並截斷。
- **回報格式**：改了哪些檔、測試數字、偏離契約處。

### 作業 B-階段 1：彙總列句首＋舊彙總列一次性清理
- **背景**：`summary` 類別的說明句首重複類別標籤；另有舊版「權限異動（彙總）」列已無產生邏輯。
- **契約**：
  - `summary` 類別說明句＝「{對象} {AlertText}」，不再以類別標籤開頭（對象空→「（未指定對象）」）。
  - 一次性清理：啟動時由既有權限異動 migration hosted service 以版本化狀態旗標執行「刪除 `change_type='權限異動（彙總）'` 的列」，只跑一次；寫操作紀錄（刪除筆數）。**閘門反例**：旗標已設但日後再匯入同形舊列（不可能，產生邏輯已刪）→ 接受；旗標需與現有重剖狀態分開。
  - 更新 §9.5 的例外說明、`PermissionCategory.Summary` 註解。
- **範圍**：`PermissionChangeService`、`PermissionChangeMigrationHostedService`、`PermissionChangeStore`（加刪除方法）、對應測試。
- **驗收**：build/test 全綠；測試 `SummaryText_summary類別句首不含類別標籤`；`舊彙總列清理_只執行一次且不動例行同步列`；既有 `權限異動彙總summary類別_維持現行類別前綴…` 改名改斷言。

### 作業 B-階段 2：彙總列涵蓋起訖時間＋明細顯示
- **背景**：彙總列 `DetectedAt` 恆為日期 00:00、沒有起訖時間；明細「異動前／後」皆空仍顯示。
- **契約**：
  - `lf_permission_changes` 新增兩個可空欄位（暫定 `covered_from`／`covered_to`，datetime），SQLite 與 SqlServer 各一 migration；DB-SPEC 補欄位說明。
  - 產生例行同步彙總列時寫入被合併事件的最早／最晚 `DetectedAt`；逐則列不填。
  - DTO 帶出兩欄；前端明細：彙總列在「行為說明」下方顯示「涵蓋時間：HH:mm～HH:mm（N 對）」——N 由 AlertText 既有數字不可靠，**改由新增 `pair_count` 欄（暫定）供給**；舊列兩欄皆空→顯示「—」。
  - 「異動前／異動後」兩者皆空（trim 後）→整塊不渲染；任一有值→維持現行兩列。
- **範圍**：Core Models／Persistence（含 migrations）、`HostDayPostProcessor`、`PermissionChangeService` DTO、`permission-changes.js`、DB-SPEC／WEB-SPEC §9.5 對應行。
- **驗收**：build/test 全綠；測試 `例行同步彙總列_寫入涵蓋起訖與對數`；`MapToDto_舊彙總列起訖為null`；migration 測試兩 provider 能套用；前端人工：展開彙總列看到區間、逐則列（前後皆空）無「異動前／後」區塊。

### 作業 B-階段 3：4670 物件權限變更——解析欄位、類別分流、句型、重剖
- **背景**：4670 訊息含「物件類型／物件名稱／處理程序名稱」，現行只抓物件名稱（Token 類為 `-`→空）；4670 一律歸「資料夾權限異動」；舊解析器留下的髒 Target 重剖洗不掉。
- **契約**：
  - 解析器新增擷取 `ObjectType`、`ProcessName`（中英文鍵，沿用既有區段感知與欄位對應機制）；存入 DB 新欄（暫定 `object_type`、`process_name`，可空；兩 provider migration）。
  - 類別：新增 `object_acl`「物件權限變更」；`PermissionCategory.Resolve` 真的使用 eventId＋物件類型——4670 且物件類型為 File/Directory（含中文）→`folder_acl`；4670 其他→`object_acl`；非 4670 行為不變。篩選 chip、標籤字典、離線重算、測試全部跟上。
  - 句型：4670 → 「{操作者} 變更 {物件類型} 物件{（處理程序檔名）}的權限」；物件名稱有值時用「{操作者} 變更 {物件名稱}（{物件類型}）的權限」；缺操作者用被動句。降級字不再寫「路徑」。
  - 重剖：對 Target 呈「訊息尾巴」形狀（含「控制代碼識別碼」「處理程序」等鍵字的長字串，**什麼算一個**＝含至少一組「鍵: 值」且長度 > 40 的 Target）允許覆蓋為空／新值；重剖狀態旗標版本化（舊旗標 → 視為未跑），啟動再跑一次全量。彙總列不重剖（既有行為）。
  - DETECTION-SPEC 類別表、WEB-SPEC §9.5、HelpContent 09 同步。
- **範圍**：`PermissionChangeExtractor`、`PermissionCategory`、`HostDayPostProcessor`（4670 類型字串可不動）、`PermissionChangeReparser`、`PermissionChangeMigration*`、Models／migrations、`PermissionChangeService`、前端 chip、對應測試。
- **驗收**：build/test 全綠；測試：`Extract_4670_Token_擷取物件類型與處理程序名稱`、`Resolve_4670_File歸folder_acl_Token歸object_acl`、`SummaryText_4670_Token物件句型`、`Reparse_髒Target訊息尾巴_被洗成空並重算類別`、`Reparse_狀態旗標版本升級_重新執行`；grep `未能解析路徑` 只剩檔案類別使用。

### 作業 C-階段 1：報表甜甜圈修正
- **契約**：
  - `charts.js` 的 doughnut 建立時不得帶 `scales`、不得套 `interaction.mode='index'`（其餘圖型不變）。
  - 三個甜甜圈皆有中心百分比（風險占比顯示高風險占比）；中心字以圓心為準、字級隨容器縮放不溢出圓環。
  - `renderNoData` 不得移除 canvas：以覆蓋層顯示「沒有資料」，之後有資料時圖能回來。
  - 三張圖高度一致：圖例區不因換行影響甜甜圈高度（固定圖例高度或圖例置於固定區塊）。
- **範圍**：`core/charts.js`、`pages/reports.js`、`site.css` 報表區段、`Reports.cshtml`。不動折線／長條。
- **驗收**：前端無自動測試；人工：截圖三圓等大、百分比在圓心、無 0～1 刻度；選一個沒有風險日的期間再切回有資料的期間圖仍顯示；console 無錯。

### 作業 D-階段 1：保留期常數單一化、最小值 90、預設調整
- **契約**：
  - 六鍵最小值 90（DTO `[Range]`、設定頁 `min`、前端驗證）；預設：`RiskyEventRetentionDays` 14→90，其餘不變；`InitialHistoryDays`、`Detail`、`Retention` 三者預設 120 維持。
  - `RetentionSettings` record 與 NetIQ pipeline 的硬編 14 改為引用 `SystemSettings` 的預設常數（每鍵一個 `Default*` 常數）；「改預設只改一處」要成立。
  - 讀取端不 clamp；Resolver 容錯回退值亦取自同一常數。
  - 前端驗證補 `DetailRetentionDays <= RetentionDays`。
  - 文件：WEB-SPEC §9.9b 項次 4、DB-SPEC 保留期表、WEB-SPEC:2259 過時句改為指向 §9.9b。
- **範圍**：`SystemSettings`、`SettingsDtos`、`SystemSettingsService`、`AnalysisOrchestrator`（RetentionSettings）、`NetiqPipelineService`、`RuntimeSettingsResolver`、`Settings.cshtml`、`settings.js`、測試、docs。
- **驗收**：build/test 全綠；grep `= 14` 在 Core/Web 保留期相關為 0、grep `Days = 120`（非引用常數）為 0；測試 `出廠預設值` 斷言更新為 90；`Update_低於90被拒`（每鍵各一 InlineData）；既有 Resolver 測試回退值改為引用常數。

### 作業 E-階段 1：依問題視角欄位換行
- **契約**：「主機數／主機日」兩行（上：N 台、下：M 主機日）；「vs 基準」徽章一行、倍數說明第二行；動作欄按鈕直排（各一行）；移除這三欄 `text-nowrap`。其它欄不動。
- **範圍**：`records.js` 欄定義、`site.css`（如需）。
- **驗收**：人工截圖；寬度較改前縮減；sticky 動作欄仍固定。

### 作業 E-階段 2：期間快捷共用模組＋昨日
- **契約**：
  - 新增 `core/date-range.js`（暫定名）：輸出「由天數算 from/to（錨在昨天、本地日期）」與綁定 chip 群組的函式；records／reports／dashboard 三頁改用它，統一屬性 `data-range`；既有行為（dashboard 預設 active、reports 的 compare 連動）不變。
  - 問題查詢加「昨日」（=1）於最前；報表標籤改「近 7 天／近 30 天／近 90 天」。
  - WEB-SPEC §8.6-8 改為「昨日／近 7／近 30／近 90（錨在昨天）」。
- **驗收**：grep `setDate(.*- 1)` 在 pages/ 為 0（只剩 core）；人工：三頁快捷結果正確。

### 作業 E-階段 3：依問題說明缺漏三層修正
- **契約**：
  - (a) 分類為其他且無白話說明 → 問題欄第二行顯示固定句「未命中任何規則的事件，分類為其他」，樣式同白話說明。
  - (b) 依問題聚合的分類改為取該簽章**最近一筆**（LastSeen 日）的分類；儀表板風險類型卡口徑若依同一查詢須一致（既有跨檔對照測試須仍綠）。
  - (c) `PlainExplanationFor`：Windows 事件走現行；`EventId==0` 或來源平台為 Linux 時走 Linux 規則比對取說明；`MatchAllEventIds` 的 Windows 規則不得命中 Linux 事件。
  - WEB-SPEC §9.2 `PlainExplanation` 條目更新。
- **驗收**：build/test 全綠；測試 `SearchByIssue_分類取最近一筆_規則新增後不黏在其他`、`PlainExplanationFor_Linux事件取得Linux規則說明`、`PlainExplanationFor_MatchAllEventIds不命中Linux事件`；既有 `DashboardCategoryIssueTypeCountTests` 仍綠。

## 4. 測試計畫
見各階段驗收條列；前端依 WEB-SPEC §12 不建自動化測試，以人工截圖驗收。

## 5. 文件更新（全部驗收後由 Claude 寫）
WEB-SPEC §8.6-8、§9.2、§9.5、§9.9b（2d、4）、:2259；DB-SPEC `lf_permission_changes` 新欄＋保留期表；DETECTION-SPEC 權限異動類別表（object_acl）；HelpContent 09／12；BACKLOG 視情況。

## 6. 風險與回滾
- B3 新類別改變既有資料分類（離線重算）＋重剖再跑一次：資料量 9 萬則/日等級，需分批；回滾＝還原 migration 與旗標。
- D `RiskyEventRetentionDays` 預設 90：只影響未儲存過設定的部署；已儲存部署不變。
- E3(b) 聚合分類改最近一筆：儀表板與依問題口徑同步變動，靠既有對照測試守住。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| C-1 甜甜圈 | Claude | 完成 | 實機量測：三圖 scales 為空、interaction=nearest、中心字偏移 0/0、三圖高度 72/72/72 | 契約外另修：卡片標題字數不同造成的高度差（標題 min-height 兩行） |
| D-1 保留期 | Claude | 完成 | 六鍵 min=90／預設只改 RiskyEvent 14→90；grep 無第二份硬編 | 驗證測試改走 `Validator.TryValidateObject`（原以反射斷言 attribute，屬假通過） |
| A-1 品牌 | Claude | 完成 | 實機量測：副標寬＝主標寬（209.275/185.3）、左緣齊、長副標時字距歸零＋省略號 | 兩處合成 `_Brand.cshtml`＋`_BrandInner.cshtml`；Error.cshtml 借用的 `.lf-login__title` 改名 `.lf-login__heading` |
| E-1 欄位換行 | Claude | 完成 | 主機數／主機日兩行、vs 基準去 nowrap、動作欄直排 | — |
| E-2 期間快捷 | Claude | 完成 | 實機：昨日＝起訖同為昨天；`grep setDate(.*- 1)` 在 pages/ 為 0 | 報表頁一併加「昨日」（契約只要求問題查詢） |
| E-3 說明缺漏 | Claude | 完成 | 新增三測試；既有跨檔對照測試仍綠 | 契約的「MatchAllEventIds 不命中 Linux」由「EventId 0 走 Linux 規則」這條路徑涵蓋，未另加守衛 |
| B-1 彙總句首＋舊列清理 | Claude | 完成 | 實機：說明句不再有「權限異動彙總：」前綴；清理狀態 blob 寫入 | 清理走啟動時的 hosted service（專案無存量維護 UI） |
| B-2 涵蓋區間 | Claude | 完成 | 實機：展開顯示「涵蓋時間 hh:mm～hh:mm（N 對）」；前後皆空不渲染 | 另加 `pair_count` 欄（AlertText 的數字不可作資料來源） |
| B-3 4670 物件權限 | Claude | 完成 | 實機：類別「物件權限變更」、句子「… 變更 Token 物件（svchost.exe）的權限」 | 重剖版本旗標升到 2；既有 DB 實測升級成功（五欄補齊、狀態版本 2） |

### 終檢（兩份獨立審查）

程式碼側修正：`setCenterText` 的 ResizeObserver 洩漏（改掛 wrapper、每個容器一個）；
`IsDirtyTarget` 由「一組鍵: 值」收緊為「兩組」（一組冒號是合法命名，誤判會不可逆清空對象）；
`PermissionChangeMigrator` 補傳物件類型並寫入兩個新欄位；清理在取消時不執行；
`LatestCategories` 的 null 防護與回退值對齊 `AggregateByCategory`。

**同型遺漏（審查未抓到、自查發現）**：`AggregateByCategory`（儀表板風險類型卡）也用
`MIN(category)`，只改依問題視角會讓卡片與下鑽分岔——已改為共用同一份 `LatestCategories`。

文件側修正：`PlainExplanation` 條目補 Linux 路徑與前端固定句；DTO 註解與實作對齊；
說明書與規格去掉「舊版／升級後／已由清理刪除」等敘事；彙總欄位語意收斂到 DB-SPEC 一處。

測試修正：Range 反射斷言改為真正的模型驗證；補「含冒號的合法對象不被誤殺」「舊彙總列
清理」「彙總列更新既有列」三條；修掉長路徑測試資料裡的控制字元（原本測不到它宣稱的形狀）。

最終：`dotnet build` 零錯誤零警告、`dotnet test` **2499 綠**（基線 2473，略過 6 不變）。
