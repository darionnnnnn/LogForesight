# FEEDBACK 第 74 輪規劃：版面格線與元件一致性（草案）

> 狀態：**草案，尚未開輪**。第 73 輪併 dev 後才開輪；開輪時 `git mv` 到 `docs/FEEDBACK-74-PLAN.md`，第一個提交把版號改成 `1.0.74.0`。草案放在 `docs/drafts/`，是因為 `VersionRoundGuardTests` 以 docs 根目錄的規劃檔判定目前輪次。
> 來源：
> - 使用者 2026-11-12 以 FHD／2K／4K 截圖檢視第 71 輪成果，要求檢查全站「建議處置」那種排版歪掉的同類問題。原是第 72 輪留給第 73 輪的版面清單，第 73 輪開輪時漏讀，只做了風險日詳情部分，其餘順延到本輪。
> - 使用者 2026-11-13 定案：卡片滑過變色只限可點的卡片。
> - BACKLOG 中標「下一輪預定」或「下一輪動到某處時處理」、且與本清單同一區域的項目。
> 整理：2026-11-13 逐條對照第 73 輪的改動（`docs/archive/FEEDBACK-73-PLAN.md` 批次 A～F）與 BACKLOG，合併重疊、標出衝突，依區域分組；各條的「現況」是同日在 `feature/feedback-73` 上讀碼核對的結果（行號以該分支為準，開輪時以 dev 重新核對）。條目後的〔原 N〕是原清單的類別編號。

## 核心原則（依第 73 輪定案修正）

- **同一頁只用一條主分欄線**：元素要嘛對齊這條線，要嘛占滿整列。
- **寬螢幕的處理分兩種頁面**：
  - 詳情頁（風險日、主機、交辦單、處理人、使用者）已在第 73 輪定案**滿版**，多出的寬度用來並排與加寬側欄，**不再加內容最大寬度**（`ContentWidthGuardUiTests` 守住只有設定、初始設定限寬）。
  - 清單、儀表板與報表類頁面的寬螢幕處理是本輪待決（待決 2）。
- 不放大字（第 72 輪定案）。
- 修法優先改共用元件或共用類別，一處生效全站；同一結構三個以上頁面才抽共用（CLAUDE.md）。

## 開輪前必做

1. **重新盤點**：下面的「現象」多數是 2026-11-12 依第 71 輪畫面整理的；第 72 輪改了字級，第 73 輪改了詳情頁寬度、風險日詳情版面與排程作業總表。每一條都要在 dev 最新畫面上確認仍然存在才列入定案，不存在的移到「已自然解決」。
2. **量測工具**：`LogForesight.Tests/tools/ui-shots.mjs`（第 73 輪補了「文字溢出自身方框」）。可直接用的既有情境：儀表板、問題查詢、報表、排程作業、規則維護、使用者、主機、群組與授權、問題負責與靜音、資料匯入、NetIQ維護、權限異動檢核、處理人-首頁／我的交辦／交辦單、大螢幕-報表、版面-詳情頁滿版-*、版面-風險日詳情-頁首摘要對齊／缺段、互動-風險日詳情-類型分布在右欄、互動-儀表板-期間切換與告警分流。量測新的版面關係用 `evalAssert`（欄起點對齊、卡片等高、滑過底色）。
3. **先讀**：本文件、下表被併入本輪的 BACKLOG 條目、`LogForesight-design-mock/DIRECTION.md`（新視覺方向第二階段的方向基準，repo 外）。
4. 依 `plan-before-dev` 走事實核對與待決討論，再改寫成正式規劃；畫面工作套 `ui-ux-pro-max` 與 DESIGN-SYSTEM。

## 與第 73 輪的衝突與重疊

| 第 73 輪 | 影響本輪的條目 | 關係 | 處理方式 |
|---|---|---|---|
| A 詳情頁滿版 | B-5 使用者詳情 KPI 只占一半、主機月曆只在左邊、可見主機表欄位分散 | **加劇**：寬度變大後空白更多 | 以滿版為前提重量；解法是改欄數或並排，不加回上限 |
| A 詳情頁滿版 | 原第六類「內容沒有最大寬度」 | **方向相反** | 詳情頁不再討論上限；只剩清單與儀表板類頁面（B-8） |
| C 頁首 2×2（左右各 50%） | A-1 頁首右欄起點與下方主欄／右欄分界不同 | **互相牽動** | 要統一只能改頁首比例或右欄寬度；2×2 是使用者選的，改比例前要再問 |
| B 類型分布回右欄、側軌第三顆按鈕 | A-2 右欄太窄、A-3 側軌直排標籤截斷 | **加劇**：右欄多一張卡、側軌多一顆按鈕 | 右欄寬度、側軌與分界一起設計（區域 A） |
| C 處置左側指示條 | D-5 卡片滑過變色只限可點卡片 | **前提改變** | 指示條原因之一是「滑過時卡片與處置同色」；改完後仍保留（它也區分處置與速報） |
| F 改寫 `renderPrtgDaySummaryCell` | D-4 PRTG 欄重複表達 | **同一個函式** | 第 73 輪只改了提示與「—」，重複仍在；以第 73 輪版本為底 |
| F 執行總表欄位變動 | D-3「未執行」數字太淡、C 類表格捲動 | 欄寬已變 | 重量 |
| D 帳號值折行 | B-3「← 可左右捲動」提示、C-4 窄螢幕表格 | 風險日詳情表格寬度已變 | 重量 |

## 第 73 輪已處理（不再列入）

- 建議處置的位置〔原一〕：已改為頁首四段 2×2，處置維持底色並加左側指示條；原方向 A、B 都不採用。
- 重點問題的帳號值蓋住隔壁欄：已修。
- 詳情頁右側空白〔原六的詳情頁部分〕：已滿版。

---

## 區域 A：風險日詳情（右欄與分界、處理面板缺陷）

一次設計右欄的寬度、內容與分界，避免分三次各改一點。

### A-1 頁首右欄與下方右欄的分界不同〔原一〕

- **現況**：頁首兩排是 `repeat(2, minmax(0, 1fr))`（`components.css:628-631`、`669-681`），右欄從 50% 開始；下方 `.lf-detail-layout` 是 `minmax(0, 1fr) min(20rem, 34%)`（`layout.css:757`），右欄從約 66%～80% 開始，2K 滿版後差距更大。
- **根因**：兩組格線各自定義，沒有共用的分欄 token。
- **方向**：依待決 1。若統一，抽一個「詳情頁右欄寬度」token 給兩處共用；頁首比例跟著變時，2×2 的「左右對齊」關係要保留。
- **驗收**：`evalAssert` 在 1280／1920／2560 量頁首右欄左緣與 `#record-side` 左緣的差（依定案為相等或明列允許差）；`版面-風險日詳情-頁首摘要對齊` 情境照綠。
- **受影響測試**：`AiSummaryBehaviorTests`、`ai-summary.behavior.mjs`、`SideRailBehaviorTests`。

### A-2 右欄在 2K 仍太窄，處理狀態膠囊折兩行〔原七〕

- **現況**：`.lf-handling__chips` 是 `repeat(auto-fill, minmax(4.75rem, 1fr))`、膠囊 `white-space: normal`（`components.css:1519-1531`）；右欄上限 20rem 不隨螢幕加寬。第 73 輪起右欄多了類型分布卡。
- **根因**：右欄寬度固定上限，膠囊允許換行。
- **方向**：右欄在 ≥ 1600 加寬（數值依待決 1），膠囊不在字中斷行。
- **驗收**：1920／2560 下 `evalAssert` 斷言每顆膠囊文字只有一行；1280 側軌展開時不撐出水平捲動。
- **受影響測試**：`NoteEditorUiTests`、`note-editor.behavior.mjs`（含 `lf-handling__chips`）。

### A-3 側軌直排標籤截斷〔原十，待複核〕

- **現況**：收合態右欄 3rem，`.lf-side-rail__btn` 直排、`max-height: 14rem`（`layout.css:762-840`）；第 73 輪起三顆按鈕（處理狀態、處理歷程、類型分布）。
- **方向**：先量 1280 下三顆按鈕的標籤與摘要是否截斷；截斷的話縮短摘要或改變側軌呈現，與 A-2 一起定。
- **驗收**：1280 下 `evalAssert` 斷言每顆按鈕的 `.lf-side-rail__label` 沒有被裁（`scrollHeight <= clientHeight`）；捲動後三顆都在視窗內（第 73 輪既有斷言）。
- **受影響測試**：`SideRailBehaviorTests`、`RecordDetailRailBehaviorTests`。

### A-4 切換篩選後已勾選的問題沒被清掉（bug）

- **現況**：`selectedIssueKeys`（`record-detail.js:83`）只在 `load()` 清空（`:151`）；嚴重度鈕（`:1296-1307`）與 `changeScope`（`:1235-1241`）只重繪清單。處理面板的 `getSelection()` 與 `refreshRailSelectionSummary`（`:233`）讀整個集合。
- **情境**：勾 3 個問題 → 篩選切到只剩 1 個可見 → 面板與側軌仍寫「已勾選 3 個」，「套用到 3 個問題」送出畫面上看不到的兩個。
- **方向**：篩選或範圍變更後，移除不在 `visibleTopIssues()` 內的 key，再呼叫 `refreshRailSelectionSummary()` 與面板的選取刷新。
- **驗收**：行為測試「勾 3 → 篩到 1 → 已勾選數為 1、送出只含可見的 1 個」；邊界另一側「篩選後原勾選都仍可見 → 數量不變」。突變：拿掉修剪，第一條轉紅。
- **受影響測試**：`record-detail-rail.behavior.mjs`。

### A-5 處理面板草稿與「AI 整理」回寫（既有缺陷）

- **現況**：`formDraft` 與草稿鍵 `record:${hostId}:${date}`（`handling-panel.js:62-63`、`:562`）日層級與批次模式共用；`note-editor.js:557-558` 把 AI 回應寫進建立時捕捉的 textarea，勾選變動重建表單後寫進已卸下的舊節點。
- **方向**（BACKLOG 原文）：草稿加 `mode`，模式不同不套用 status／dueDate／note，只在使用者實際操作後寫入，鍵加模式後綴；回應到達時檢查 `textarea.isConnected`，已卸下就丟棄。
- **驗收**：行為測試三條：日層級草稿不帶進批次、批次不帶回日層級、AI 回應在表單重建後不寫入舊 textarea 也不寫草稿。
- **受影響測試**：`NoteEditorUiTests`、`note-editor.behavior.mjs`。

## 區域 B：全站版面格線

### B-1 群組與授權兩張表欄位不對齊〔原一〕

- **現況**：`groups.js:125-138` 的 `appendUserGroupSection` 每區各自 `renderTable`、無欄寬；欄位為群組名稱／角色／成員數／派工池／狀態／動作。
- **方向**：兩張表共用同一組欄寬（固定版面＋各欄寬度），或合成一張依類型分組的表。
- **驗收**：`群組與授權` 情境 `evalAssert` 斷言兩張表同名欄的左緣相等（±1px），五寬度六項 0。

### B-2 儀表板 KPI 分組線與風險類型欄對不起來〔原一〕

- **現況**：`kpiStrip`（`ui.js:1678`、`components.css:296-362`）依卡數 flex 分寬；`.lf-category-grid` 是獨立的 `auto-fit minmax(13rem, 1fr)`（`components.css:429`）；`dashboard.js:409` `singleRow: true`。
- **方向**：依待決 2 決定兩者是否共用同一組欄軌；不共用時至少讓兩者的左右緣對齊整頁主分欄線。
- **驗收**：`儀表板`、`處理人-首頁` 情境量 KPI 分組線與風險類型欄界的關係（依定案）。
- **受影響測試**：`KpiStripBehaviorTests`、`DashboardRevealBehaviorTests`、`FrontendConsistencyUiTests`。

### B-3 孤立在右下的元素〔原二〕

- **現況**：
  - 「顯示其餘 N 項／N 類」：`.lf-reveal-bar` `justify-content: flex-end`（`components.css:464`，`core/reveal.js`）。
  - 「← 可左右捲動」：`.lf-table-wrap--scrollable::after` `float: right; position: sticky`（`components.css:1229`），無測試斷言。
  - 大螢幕報表「最後更新」：`.lf-wall-header` 右側 `9rem` 留白是為離開鈕預留（`layout.css:554-560`），屬刻意設計，開輪時確認是否仍要改。
- **方向**：揭露鈕併到所屬區塊的標頭或貼齊內容左緣；捲動提示改成不占版面的方式（例如表格上緣一行提示或淡出陰影），列印照舊不顯示。
- **驗收**：`儀表板` 情境斷言揭露鈕位於所屬卡片內、不單獨成列；有捲動的表格提示不再浮在表格下緣外（`evalAssert` 量提示與表格下緣的關係）。

### B-4 排程作業三張狀態卡中段空白〔原二〕

- **現況**：`.lf-run-status-grid` `align-items: stretch`、`.lf-run-actions` `margin-top: auto`（`pages.css:919-944`），刻意讓按鈕對齊卡底，2K 下中段約 250px 空白。
- **方向**：按鈕跟在內容後面（不貼卡底），或卡片不等高；兩者擇一，依 ui-ux-pro-max 判斷。
- **驗收**：`排程作業` 情境在 2560 斷言每張卡內容最後一列與按鈕列的距離不超過一個間距級距。
- **受影響測試**：`RunsPageUiTests`。

### B-5 詳情頁寬度沒填滿〔原三，第 73 輪滿版後加劇〕

- **現況**：
  - 使用者詳情、處理人：`kpiStrip` 單組（`user-detail.js:151`、`handler-detail.js:212`），`.lf-kpi-grid` 是 `auto-fill minmax(11rem, 1fr)`（`components.css:329`），4 張卡只占前 4 軌。
  - 主機詳情月曆：`.lf-calendar__month { flex: 0 0 auto; width: 210px }`（`pages.css:408-420`），固定寬度，只在左邊。無測試。
  - 使用者詳情「可見主機」表（`user-detail.js` 約 170 行）：5 欄無欄寬，前四欄擠在左、「可見來源」在最右。
- **方向**：KPI 改 `auto-fit` 讓卡片填滿；月曆依寬度增加每列月份數或放大月份格；可見主機表給欄寬。
- **驗收**：`版面-詳情頁滿版-使用者／處理人／主機` 情境在 1920／2560 斷言 KPI 列右緣與下方卡片右緣相等、月曆右側空白不超過一個月份寬；五寬度六項 0。

### B-6 卡片外的說明文字、只裝一個東西的卡片〔原四〕

- **現況**：問題負責與靜音頁首說明（`IssueOwners.cshtml`）、權限異動檢核說明在卡外（`PermissionChanges.cshtml:62`）、資料匯入 `#encoding-note` 是只含一句話的獨立卡（`Imports.cshtml:60-65`）；規則維護 `lf-help` 圖示獨占一行（`Rules.cshtml:60`、`144-169`）；NetIQ 維護 `#btn-new-sentinel` 單按鈕一列（`Netiq.cshtml:15`）；主機「全選符合目前篩選的主機」單按鈕加說明（`Hosts.cshtml:47-53`）。這些位置都沒有測試斷言。
- **方向**：說明文字進頁首副標或所屬卡片；單按鈕併進相鄰工具列或卡片標頭。
- **驗收**：各頁情境 `evalAssert` 斷言 `main` 直接子層沒有卡片以外的段落文字、沒有只含一個控制項的 `.lf-card`；改 markup 前 grep 測試專案被斷言的字串。

### B-7 排程作業 PRTG 卡數值靠右又折兩行〔原五〕

- **現況**：`core/prtg-scope-labels.js:17-22` 的 `prtgHostMapSummary` 產生「已對應主機 N 台、未對應 M 台」，`runs.js:1794-1814` 與時間串接放進 `.lf-kv__value`；`.lf-kv` 是 `space-between`、值 `text-align: right; overflow-wrap: anywhere`（`pages.css:958-975`）。
- **方向**：長值改成值在標籤下方另起一行（靠左），或把時間與對應數拆成兩列。
- **驗收**：`排程作業` 情境 1920 斷言該值為單行或靠左起排，不出現「台」單獨落到第二行。

### B-8 清單與儀表板類頁面的寬螢幕〔原六扣掉詳情頁〕

- **現況**：儀表板「重點問題」表在 2K 欄與欄之間空約 250px；KPI 卡高約 140px 只放一個數字；「權限與安全」兩張卡各占半寬；圖表固定 `height: 230px`（`components.css:493`、`pages.css:358-362`），大螢幕報表 4K 只用上方約三分之一。
- **方向**：依待決 2。
- **驗收**：依定案寫；至少 2560／3840 下儀表板表格同列資料的跨度、KPI 卡空白比例、大螢幕報表圖表占卡片高度比例各有一條量測。
- **受影響測試**：`ReportsWallLayoutUiTests`、`FontScaleGuardUiTests`（圖表字級）。

## 區域 C：表格與列內動作

### C-1 問題查詢固定動作欄蓋住欄位〔原七＋BACKLOG「重大標記被蓋」，同一根因〕

- **現況**：`records.js` 約 868 行 `renderTable` 開 `stickyLastColumn`（`ui.js:731`），末欄 sticky（`components.css:1244-1270`）；「出現密度」（`records.js:834`）與嚴重度標記（`core/format.js:114`）在 1280／FHD 被蓋。確切重疊位置待開輪實測。
- **方向**：與 C-2 一起縮窄動作欄（動作收進「更多」選單），表格在 FHD 不需捲動。
- **驗收**：`問題查詢` 情境 1280／1920 斷言末欄左緣不壓到任何其他欄的文字（`evalAssert` 比對文字範圍）。
- **受影響測試**：`TableConsistencyUiTests:122-130`、`AccessibilityUiTests:322`（鎖 `stickyLastColumn: true`）。

### C-2 問題查詢每列三顆動作疊成直排〔原八＋BACKLOG 第二階段「更多」選單，同一件事〕

- **現況**：`records.js:1171-1250` `issueActionsCell` 用 `d-flex flex-column`，「統一標記」（`:1196`）、「交辦」（`:1221`）、「靜音」（`:1240`）直排，列高約 150px。
- **方向**：一顆主要動作＋「更多」選單；放在本輪或與區域 E 一起，依待決 6。
- **驗收**：`問題查詢` 情境列高不超過兩行文字高；選單鍵盤可操作（Esc 關閉、焦點回到觸發鈕）。

### C-3 規則維護「類別」被擠成兩行〔原七〕

- **現況**：`rules.js:222` 類別欄 `roleText('lf-text-meta', …)` 無 nowrap、無寬度。
- **方向**：類別欄不在字中斷行並給最小寬度。
- **驗收**：`規則維護` 情境 1280 斷言類別欄每格單行。

### C-4、C-5 窄螢幕〔原十，待複核〕

- **現況**：儀表板與風險日詳情表格在 375 欄位截斷、捲動提示重複；儀表板頂部（`Dashboard.cshtml:11-19`）期間切換、更新時間、日期範圍三個子元素沒有 `flex-wrap`。
- **方向**：頂部列允許換行；表格是否改卡片清單依待決 5。
- **驗收**：375 下 `儀表板` 情境六項 0，頂部三個元素不互相擠壓（各自寬度不小於內容最小寬度）。

## 區域 D：按鈕與顏色訊號

### D-1 同一區動作按鈕三種外觀〔原八＋BACKLOG「無框按鈕用在表格以外」，同一根因〕

- **現況**：DESIGN-SYSTEM §8.3 定義 `outline-primary`／`outline-danger` 的外觀是無框文字（原意是列內重複動作）。交辦單頁首：回覆 `:331`、改派 `:363` 用 `outline-primary`，取消交辦 `:424` 用 `outline-danger`，退回 `:396`、代為結案 `:453` 是框線（`work-order-detail.js`）。排程作業：立即執行 `btn-primary`（`Runs.cshtml:54`），立即補跑 AI `outline-primary`（`:97`），強制重新分析 `outline-danger`（`:99`）。BACKLOG 另列 NetIQ 維護「儲存」、PRTG 維護「測試連線」、稽核紀錄「只看被拒的存取」、主機「從 NetIQ 匯入」「批次貼上」、規則維護 modal「先驗證」、交辦單「回覆這張單」。
- **方向**（待決 3）：無框限定在 `.table` 內（樣式層一條規則），或表格外改用 `outline-secondary`（改標記）。
- **驗收**：`evalAssert` 斷言表格外的 `.btn` 都有可見邊框（border 寬度 > 0 且顏色非透明）；同一工具列內除了一顆主要按鈕外其餘外觀一致。`DesignSystemGuardUiTests` 依定案補守門。

### D-2 紅色用太多〔原九〕

- **現況**：紅色來自 `.lf-text-alert`（`components.css:78`，`--lf-danger-text`），用在儀表板計數（`dashboard.js:675`）、「昨日仍在發生」（`:714`）、「本期新增」（`:743`、`:760`、`:791`），報表「↑ 前期為 0」（`reports.js:382`），問題查詢（`records.js:1097`）；另有 `.lf-stat__value--danger`（`components.css:261`）、`.lf-card--critical .lf-stat__value`（`:489`）。
- **方向**（待決 4）：紅色只給高風險與失敗；「本期新增」「昨日仍在發生」這類變化改用中性強調。圖表色（E-4）跟同一決定。
- **驗收**：DESIGN-SYSTEM §8.8 寫明紅色用途；`DesignSystemGuardUiTests` 掃 `lf-text-alert` 只出現在白名單用途。
- **受影響測試**：`AccessibilityUiTests`、`DesignSystemGuardUiTests`、`reports-kpi.behavior.mjs`、`PrtgRunsCardAndProbeTests`、`ReportsWallLayoutUiTests`。

### D-3 排程作業「未執行」數字太淡〔原九〕

- **現況**：`runs.js:40` `none: statusMeta('未執行', '--lf-gray-200')`，`countCell`（`:373-380`）以 `--lf-status-color` 套色，對白底對比太低。
- **方向**：圖例色塊可維持淺灰，數字本身用次要文字色。
- **驗收**：`evalAssert` 量「未執行」欄數字對背景的對比 ≥ 4.5:1。
- **受影響測試**：`runs-timeline.behavior.mjs`（斷言 gray-200）。

### D-4 排程作業 PRTG 欄重複表達〔原九；第 73 輪改過同一函式〕

- **現況**：`runs.js:67-103` `renderPrtgDaySummaryCell` 顯示徽章「未啟用」，下方再印 `s.prtgNote`；note「PRTG 未啟用」來自 `BatchRunStore.cs:168` `PrtgNoteNotEnabled`（`PrtgDailyPipeline.cs:1099` 寫入）。
- **方向**：note 與徽章同義時不重印（前端判斷），其他原因（尚未設定認證、憑證無法解密）照印。不改後端寫入的 note（其他畫面與匯出也用）。
- **驗收**：行為測試：note 為「PRTG 未啟用」時只有徽章；note 為「憑證無法解密…」時徽章＋說明都在（邊界另一側）。
- **受影響測試**：`runs-timeline.behavior.mjs`、`RunsPageUiTests`。

### D-5 卡片滑過變色只限可點的卡片（使用者 2026-11-13 定案）

- **現況**：`components.css:177` `.lf-card:hover { background-color: var(--lf-primary-soft) }`（第 L 輪 2026-08-18 加的，原意是「與表格列相同的 hover」），所有卡片都會變色。可點的卡片：
  - `dashboard.js:459` `lf-card lf-card--clickable`（外層 `<a class="lf-stat">`，`:444`），另有 `components.css:282` `a.lf-stat:hover .lf-card, .lf-card--clickable:hover` 的陰影規則。
  - `hosts.js:169` `button.lf-card`（整張卡是按鈕）。
  - 風險類型格線 `.lf-category-grid` 的卡片 hover（`components.css:437-461`）。
  - KPI 卡 `a.lf-kpi-card--link` 是獨立類別，不在 `.lf-card` 之下，不受影響。
- **方向**：刪掉全卡的 `.lf-card:hover`，底色變化只掛在上述可點選擇器（`a .lf-card`、`button.lf-card`、`.lf-card--clickable`、格線卡）。第 73 輪處置的指示條保留。
- **驗收**：
  - 樣式表 `\.lf-card:hover\s*\{` 零命中（全卡規則移除）。
  - 量測情境：風險日詳情頁首卡、處理狀態卡滑過後背景不變；儀表板可點卡、主機卡片按鈕滑過後背景改變（`evalAssert` 以 CDP 滑鼠移入後讀 `getComputedStyle`）。
  - 鍵盤焦點的可見性不受影響（可點卡片 `:focus-visible` 仍有焦點環）。
- **受影響測試**：`FrontendConsistencyUiTests:739`（regex 鎖 `a\.lf-stat:hover \.lf-card,\s*\.lf-card--clickable:hover\s*\{`，規則改寫時要同步）。

## 區域 E：新視覺方向第二階段（建議另開一輪）

方向在 `DIRECTION.md`；BACKLOG「新視覺方向第二階段（下一輪預定）」與相關條目：

| # | 項目 | 與本輪其他項目的關係 |
|---|---|---|
| E-1 | 篩選工具列（條件收進「名稱：值」下拉鈕） | 會解掉權限異動檢核「清除篩選」被擠到第二行〔原二〕；BACKLOG「快捷期間按鈕組像分段控制」「輸入框旁按鈕接縫」的觸發時機都是這一項 |
| E-2 | 列內「更多」選單 | 與 C-2 同一件事，放在哪一輪要一起決定 |
| E-3 | 啟用停用改開關、詳情頁共用頁首、人名與帳號上下兩行 | 人名帳號兩行會解 BACKLOG「使用者表 1280 仍需捲動」「稽核紀錄帳號折三行」 |
| E-4 | 圖表樣式（`charts.js` 18 處寫死色碼） | 與 D-2 紅色使用範圍的決定要一致 |

開輪前要先決定（BACKLOG 原文）：手機上的選單操作、篩選下拉展開後的多選互動、名稱顯示規則是否調整。

## 待決（開輪時決定）

1. 風險日詳情：頁首右欄要對齊下方右欄（改頁首比例或改右欄寬度），還是接受頁首自成一組兩等欄？右欄在 ≥ 1600 要加寬到多少？
2. 清單與儀表板類頁面在 2K／4K：加內容最大寬度、改多欄（例如儀表板兩張表並排），還是維持滿版只處理欄距？詳情頁已定案滿版，不在此列。
3. 動作按鈕分級：一個區塊至多一個主要按鈕，其餘外觀怎麼統一；危險動作用紅色文字還是框線；無框變體是否限定在表格內。
4. 紅色的使用範圍：只給高風險與失敗，還是也包含「本期新增」這類變化（E-4 圖表色跟同一決定）。
5. 窄螢幕表格要不要改卡片清單（範圍大，可能再拆一輪）。
6. 區域 E 是否併入本輪；建議另開一輪，但 C-2 與 E-2 是同一件事，要決定放在哪一輪。

## 建議的批次方向（開輪時依待決調整）

| 批次 | 內容 | 備註 |
|---|---|---|
| A | 處理面板缺陷 A-4、A-5 | A-4 是 bug，不論其他待決都先做 |
| B | 卡片滑過變色 D-5 | 已定案、範圍小，可先做 |
| C | 風險日詳情右欄與分界 A-1～A-3 | 依待決 1 |
| D | 版面格線規則（主欄＋側欄共用類別、分欄 token，寫進 DESIGN-SYSTEM）與 B-1～B-7 | |
| E | 寬螢幕清單與儀表板 B-8 | 依待決 2 |
| F | 表格與列內動作 C-1～C-5 | C-2 依待決 6 |
| G | 按鈕與顏色 D-1～D-4 | 依待決 3、4 |
| H | 文件、守門（版面探針：分欄起點一致、沒有孤立元素、不可點卡片滑過不變色）、前後對照、全量測試 | |
| （下一輪） | 新視覺方向第二階段（區域 E） | 建議 |

## 測試缺口（開輪時補）

`lf-calendar`、`btn-select-all-matching`、`encoding-note`、`btn-new-sentinel` 沒有任何測試斷言；`lf-category-grid` 只有 `FrontendConsistencyUiTests` 的 regex。改到這些位置時一併補守門或量測情境。

## 已自然解決

（開輪時依第 73 輪完成後的畫面填寫。）
