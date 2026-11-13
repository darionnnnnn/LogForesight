# LogForesight 設計系統 v3

> 除非必要否則不要讀取 docs/archive/ 內容，避免浪費 token。

視覺改版依據文件。本文件是樣式表（`wwwroot/css/` 四層）token 區、共用元件層（ui.js／format.js／charts.js）與版面骨架的**單一設計依據**；與 WEB-SPEC §8 互補——§8 管架構規範（分層、白名單、驗收條目），本文件管視覺語彙的具體取值與理由。

## 1. 產生方式與定位

- 依據 [nextlevelbuilder/ui-ux-pro-max-skill](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill)（MIT）的設計智能資料庫產生：以「security log analysis enterprise dashboard」「enterprise log analytics admin dashboard professional clean light data-dense」等情境檢索其 84 種 UI 風格、192 色盤、74 組字型配對與 UX 指南後收斂。skill 僅在開發期作為檢索工具，**不進出貨產物、不引入任何執行期依賴**，符合 WEB-SPEC §8.1 憲章。
- 風格定位：**Data-Dense Dashboard（BI/Analytics）× Swiss 極簡**——資料密度優先、低彩度中性底、明確層級、剋制動效。skill 對此風格的定義：「Multiple charts/widgets, data tables, KPI cards, minimal padding, grid layout, space-efficient, maximum data visibility」。
- 色彩策略：**深企業藍（deep enterprise blue）主色 + 琥珀（amber）強調色**。skill「Analytics Dashboard」色盤原型：Blue data + amber highlights，語意是「藍＝資料與信任、琥珀＝需要注意的亮點」，與資安日誌產品的「大多正常、少數要看」的資訊結構吻合。
- 深色模式：未提供（淺色打磨到位後另案評估）。

## 2. 色彩 token（v1 → v2 對照）

### 品牌色

| token | v1（靛藍） | v2（企業藍） | 說明 |
|---|---|---|---|
| `--lf-primary` | `#4f46e5` | `#1e40af` | blue-800，更沉穩、對比更高（白字 8.7:1） |
| `--lf-primary-dark` | `#4338ca` | `#1e3a8a` | hover/active 深化 |
| `--lf-primary-hover` | `#6366f1` | `#2563eb` | 亮態（深色底上的 hover） |
| `--lf-primary-soft` | `#eef2ff` | `#eff6ff` | 淡底（選中列、active chip） |
| `--lf-primary-soft-border` | `#dfe3fb` | `#dbeafe` | 淡底配框 |
| `--lf-primary-text` | `#3730a3` | `#1e3a8a` | 淡底上的深字 |
| `--lf-accent`（新增） | — | `#d97706` | 琥珀強調：KPI 亮點、需注意標記。剋制使用，一畫面至多一處群組 |
| `--lf-accent-soft`（新增） | — | `#fef3e2` | |
| `--lf-accent-text`（新增） | — | `#92400e` | |

### 中性色階與版面

- `--lf-gray-50` ~ `--lf-gray-900`：除 `--lf-gray-200` 由 `#e5e9f0` 對齊為 slate-200 `#e2e8f0`
  （與 `--lf-border` 同值，消除「幾乎一樣的兩個 200」）外**皆不變**（既有 cool slate 即
  Tailwind slate，與新藍同溫層）。
- 版面色：

| token | v1 | v2 | 說明 |
|---|---|---|---|
| `--lf-sidebar-bg` | `#161d2e` | `#0c1527` | 深海軍藍，與主色同族；比內容區深得多，側欄與工作區一眼分開 |
| `--lf-sidebar-fg` | `#a9b4c7` | `#9fb0cc` | 隨底色微調 |
| `--lf-sidebar-fg-strong` | `#f1f4fa` | 不變 | |
| `--lf-sidebar-section` | `#6f7c93` | `#9fb0cc` | 與 `--lf-sidebar-fg` 同值，在 `--lf-sidebar-bg` 上對比 7.6:1（WCAG AA 要求 4.5:1）；分組標題的層級靠大寫、字距、字級與 600 字重表現 |
| `--lf-sidebar-active-bg` | `rgba(99,102,241,.22)` | `rgba(255,255,255,.08)` | 選中項淡底；另有 `--lf-sidebar-active-bar`（`#6b9bff`）左側 3px 細條，灰階下也看得出選中 |
| `--lf-sidebar-hover-bg` | 不變 | 不變 | |
| `--lf-content-bg` | `#f6f7fb` | `#f4f5f7` | 頁底：底色階梯（頁底 → 面板 → 凹槽）的第一階，與白色面板對比 1.09 |
| `--lf-card-bg` | `#ffffff` | 不變 | 面板 |
| `--lf-sunken` | — | `#eef0f3` | 凹槽：分段控制的軌道、停用按鈕、進度條的軌道 |
| `--lf-panel-ring` | — | `0 0 0 1px var(--lf-border), 0 1px 2px …` | 面板外環：面板用透明 1px 邊框（強制色彩模式下仍有輪廓）加這圈細環，不用實線邊框 |
| `--lf-border` | `#e5e9f0` | `rgba(15, 23, 42, .10)` | 細線由文字色的透明度推出，放在任何淡底上都同一個濃度 |
| `--lf-border-strong` | `#d5dbe6` | `rgba(15, 23, 42, .18)` | 較強的細線：輸入框、髮絲邊按鈕 |
| `--lf-text` | `#1e293b` | `#0f172a` | slate-900，資料密集畫面拉高正文對比（16.9:1） |
| `--lf-text-muted` | `#64748b` | `#5b6678` | 次要文字：對白底 5.9:1、對頁底 5.4:1 |

### 語意色（名稱固定不可改，charts.js 執行期讀取；只調色值）

| token | v1 | v2 | 說明 |
|---|---|---|---|
| `--lf-risk-high` | `#dc2626` | 不變 | 紅的語意無需動 |
| `--lf-risk-mid` | `#f59e0b` | `#d97706` | 與 accent 統一為 WCAG 調整後琥珀（白底上 3:1↑） |
| `--lf-risk-mid-soft/-text` | `#fef4e2` / `#92400e` | 不變 | |
| `--lf-risk-low` | `#64748b` | 不變 | |
| `--lf-severity-high` | `#ea580c` | 不變 | 與 risk-high 紅有別 |
| `--lf-severity-medium` | `#0891b2` | `#0284c7` | cyan→sky，歸入藍族、降低「第三種藍綠」的雜色感 |
| `--lf-severity-low` | `#94a3b8` | 不變 | |
| `--lf-info` / `-soft` / `-text` | `#0891b2` / `#e4f6fa` / `#155e75` | `#0284c7` / `#e0f2fe` / `#075985` | 同上歸族 |
| `--lf-warning` 系 | `#d97706` 系 | 不變 | 本就是琥珀，與 accent 自然統一 |
| success／danger／neutral／dark 系 | | 全部不變 | |
| `--lf-success-muted` | — | `#65a30d` | 執行紀錄「已回補」狀態（淺綠，與「當天真的有跑」的 `--lf-success` 分色） |
| `--lf-warning-line`／`--lf-danger-line`／`--lf-success-line` | — | 語意色 30～35% 透明度 | 提示框與「重大」旗標的細環（比實線語意色安靜） |
| `--lf-timeline-mid`／`-low`／`-gap`／`-none` | — | `#fbd5a5`／`#eef0f3`／`#fff1bf`／`transparent` | 主機風險月曆：只有高風險用實色（`--lf-risk-high`）；中風險淡琥珀加底線、無紀錄空框（見 §8.8） |

### 圖表 8 類固定色盤（§8.3 規則 3）

| token | v1 | v2 |
|---|---|---|
| `--lf-cat-storage` | `#4f46e5` | `#1d4ed8`（靛→藍，跟隨主色換族） |
| `--lf-cat-hardware` | `#7c3aed` | 不變 |
| `--lf-cat-security` | `#dc2626` | 不變 |
| `--lf-cat-service` | `#ea580c` | 不變 |
| `--lf-cat-backup` | `#0d9488` | 不變 |
| `--lf-cat-config` | `#64748b` | 不變 |
| `--lf-cat-resource` | `#db2777` | 不變 |
| `--lf-cat-other` | `#94a3b8` | 不變 |

圖表共用 token（`core/charts.js` 以 `cssVar` 讀取，圖表內不寫死 rgba）：

| token | 值 | 用途 |
|---|---|---|
| `--lf-chart-tooltip-bg` | `rgba(27, 42, 65, .92)` | 圖表 tooltip 背景 |
| `--lf-chart-grid` | `rgba(0, 0, 0, .06)` | 長條／折線圖 Y 軸格線 |

### Bootstrap 5.3 對接

`--bs-primary(-rgb)`、`--bs-link-*`、按鈕／分頁／dropdown／popover 等元件級變數全部改指向 v2 token；`--bs-primary-rgb` 改 `30, 64, 175`。

## 3. 字型

skill 對 dashboard/analytics 情境的一致首選配對：**Fira Sans（UI/正文）+ Fira Code（資料/等寬）**——「Fira family cohesion. Code for data, Sans for labels.」

- **Fira Sans**（latin，400/500/600/700）：標題與 UI 文字。中文 fallback 維持系統字。
- **Fira Code**（latin，400/600）：新增 `--lf-font-mono`，用於日誌原文、事件 ID、主機名、路徑等技術值——資料密集產品的「精確感」主要來源。
- 交付方式：**self-host woff2**（latin subset，每檔約 20–40KB，共 5 檔 ≈ 200KB 以內）放 `wwwroot/fonts/` + `@font-face`（`font-display: swap`）。**不用 CDN**（§8.1 禁外部請求）、**不引入中文 webfont**（MB 級，違反零依賴精神）。
- token 化：

```css
--lf-font-family: "Fira Sans", "Segoe UI", "Microsoft JhengHei", system-ui, sans-serif;
--lf-font-mono: "Fira Code", "Cascadia Mono", Consolas, monospace;
```

- 數字對齊：統計數值與表格數字欄加 `font-variant-numeric: tabular-nums`（Fira Sans 支援）。
- 字級系統見 §3a。

## 3a. 字級系統

依據：Bootstrap、Tabler 等後台樣板保留瀏覽器預設的根字級；WCAG F94（視窗單位調整的文字無法被縮放與瀏覽器字級設定放大）；
資料密集產品的內文多在 14～16px，按鈕字級等於內文，大小差異用高度與內距表現。背景見 docs/archive/FEEDBACK-72-PLAN.md。

**分層與責任邊界**（下層不得改寫上層）：

| 層 | 內容 | 誰可以改 | 怎麼變化 |
|---|---|---|---|
| 0 使用者 | 瀏覽器預設字級、瀏覽器縮放、作業系統縮放 | 只有使用者 | 站台不覆寫 |
| 1 個人偏好 | 頂列「小／中／大」：`html` 字級＝瀏覽器預設 × `--lf-font-scale`（.875／1／1.125） | 使用者 | 不含 vw、vh、clamp、px |
| 2 字階 | `--lf-font-size-*`：xs `max(.75rem, 12px)`／sm .875／base 1／lg 1.125／xl 1.25／2xl 1.5／stat 1.875rem | 設計系統 | 固定，不隨視窗寬度變化；相鄰比值 ≥ 1.11 |
| 3 文字角色 | 角色類別與元件樣式只引用字階（見下表） | 設計系統 | 頁面不寫字級數值 |
| 4 元件尺寸 | 一般按鈕、輸入框、下拉選單＝base；小按鈕、分段控制（`btn-group-sm`）、膠囊＝sm；小按鈕與分段控制的最小高度＝`--lf-control-height-sm`（小尺寸輸入框的高度） | 元件層 | 大小差異用高度與內距表現 |
| 5 密度 | 表格列高與內距 | 元件層 | 不動字級 |
| 6 圖表 | `--lf-chart-text-size`（座標、圖例，指向 xs）、`--lf-chart-tooltip-size`（提示框，指向 sm）；`core/charts.js` 以離屏探針 `.lf-chart-probe__*` 讀實際 px，收到 `lf:font-scale` 時重畫現存圖表 | 圖表封裝層 | 跟著第 1～3 層變化 |

大螢幕：一般頁面不隨視窗寬度放大字，寬螢幕交給作業系統縮放、瀏覽器縮放與版面格線。報表的大螢幕模式另有規則（WEB-SPEC §9.6）：
根字級 `18px × 倍率`、KPI 數字 `64px × 倍率`，不受偏好影響；兩個圖表 token 在該模式改指 sm／base。

**文字角色對應**（中檔、瀏覽器預設 16px 時的 px）：

| 角色 | 字階 | px | 套用在 |
|---|---|---|---|
| 頁標題 | 2xl | 24 | `lf-topbar__title`、登入頁標題 |
| 實體標題、結論標題 | xl | 20 | `lf-entity-head__title`、`lf-briefing__headline`、`.fs-4`、`.lead` |
| 卡片標題 | lg | 18 | `lf-card__title`、`modal-title`、`lf-entity-head__title--context`、說明書章內小節（`lf-doc__heading`，不大於所在卡片的章名）、`.fs-5`、`h5` |
| 內文、表格內容、控制項 | base | 16 | `body`、`td`、`.btn`、`.form-control`／`.form-select`（含 `-sm`）、`h6` |
| 標籤、說明、表頭、膠囊、徽章、小按鈕 | sm | 14 | `lf-text-label`、`lf-text-meta`、`.form-label`、`.form-text`、`th`、`lf-chip`、`lf-badge`、`.btn-sm`、分段控制、側欄分組標題、`small` |
| 最小字 | xs | 12 | 圖表座標與圖例、側欄計數徽章、月曆、舊 `.badge`；其他地方不用 |
| KPI 數字 | stat | 30 | `lf-stat__value` |

- 輸入框維持 base，不縮成 sm：iOS Safari 對小於 16px 的輸入框會自動放大整頁。
- Bootstrap 的 `fs-4`／`fs-5`／`h5`／`h6`／`small`／`.lead`／`.badge` 在 `base.css` 一處對到字階；`fs-1`～`fs-3`、`display-*`、`h1`～`h4` 類別的字級含 vw，不使用。
- 守門：`FontScaleGuardUiTests`（根字級、偏好倍率、字階數值與級距、控制項來源、樣式表字級只引用 token、禁用類別）；
  渲染後的字級（角色、字階內、最小 12px、品牌名稱不截斷）由 ui-shots 的探針量測。

## 4. 形狀、陰影、間距、動效

- **圓角**（收緊一階，data-dense 專業感）：`--lf-radius-sm: .25rem`、`--lf-radius: .5rem`、`--lf-radius-lg: .75rem`、pill 不變。
- **陰影**：只給浮在內容上方的東西（下拉選單、popover、modal、toast）。面板靜止時靠 `--lf-panel-ring` 與頁底分開，不靠陰影；可點的卡滑過只變底色。下拉選單的陰影直接寫 `box-shadow`（Bootstrap 5.3.8 沒有規則引用 `--bs-dropdown-box-shadow`）。
- **間距**：`--lf-space-*` 不變（已是 4/8 節奏）。表格列高目標 36px、卡片內距 12–16px（skill Data-Dense 建議值，Phase 3 元件層落實）。
- **動效**：`--lf-transition: .15s ease` 維持；較大轉場（modal、燈箱）150–300ms `ease-out`；**hover/pressed 不得位移佈局**（用透明度/陰影/色彩）；新增全站 `prefers-reduced-motion: reduce` 支援（動畫關閉、transition 降為 0）。
- **下拉選項**：`select option／optgroup` 明確指定 `--lf-text` 與 `--lf-card-bg`、`select` 宣告 `color-scheme: light`——選項背景透明時 Windows 版 Chrome 部分寬下拉的選項文字畫不出來（`FrontendConsistencyUiTests` 守門）。
- **焦點指示**：`--lf-focus-ring` 是 2px 面板色間隔＋2px 實心 `--lf-focus-color`（品牌藍），合成後對白底、淡底、頁底都 ≥ 3:1；嵌在列或標頭內的用 `--lf-focus-ring-inset`。半透明光暈對白底只有約 1.5:1，不得使用（`AccessibilityUiTests` 守門）。深色側欄的元件另用淺色外框。
- **捲軸**：顏色由 `html` 的 `scrollbar-color` 繼承到全站（拇指 `--lf-scrollbar-thumb`，軌道透明，對白底 4.8:1）；寬度分兩級——頁面主內容區與 modal 內文維持預設寬度（細捲軸不好抓），側欄選單、`.lf-table-wrap`、`.lf-scroll-*` 這類小型捲動容器用 `thin`。側欄另用 `--lf-scrollbar-thumb-sidebar`（對側欄底 3.2:1），滑過側欄時提亮。只用標準屬性，不用 `::-webkit-scrollbar`。

## 5. Anti-patterns（skill 檢索所得，適用本產品）

- 裝飾性設計（ornate）：不加漸層、玻璃擬態、發光等與資料閱讀競爭的效果。
- 表格無篩選／無排序（No filtering）：既有 §8.6 已保障，不得回退。
- Emoji 當結構性圖示：一律 SVG sprite（既有原則，維持）。
- 寬表格撐破版面：溢出容器一律 `overflow-x: auto`。
- 逐列單獨操作：清單頁維持批次選取（§8.6 #11）。
- 同一種膠囊表示不同種類的資訊：狀態、層級、歸屬一律長得一樣時，有底色的標記就失去訊號（§8.8）。

## 6. 交付前檢查表

可機器判定的項目由 §8.7 的量測工具與守門測試負責，畫面改動交付前必須是 0：
- [ ] 375／768／1280／1920／2560 五個寬度（另 375、1280 的小、大字級）：水平捲軸、元素溢出、點擊區 < 24×24、console 與網路錯誤、卡在載入、情境斷言六項皆 0
- [ ] FHD／2K／4K 首屏（`--matrix screens`）六項皆 0；改到字級時另以 `--font-default 20` 確認尊重瀏覽器字級設定
- [ ] `FontScaleGuardUiTests` 綠：新增的字級只引用 `--lf-font-size-*`，不使用含 vw 的 Bootstrap 字級類別
- [ ] `DesignSystemGuardUiTests`、`AccessibilityUiTests`（文字角色對比 ≥ 4.5:1、焦點可見）綠
人工確認：
- [ ] 1280 與 1920 截圖：畫面上最醒目的是風險訊號，不是按鈕或裝飾；寬螢幕沒有大片無意義空白
- [ ] hover 只變色與陰影、不位移；prefers-reduced-motion 生效；列印樣式存活（佐證層與收合區印出）

## 6a. 視窗高度驅動的版面（詳見 docs/archive/FEEDBACK-10-PLAN.md §5）

報表頁是全站第一個「一屏內看完」的版面，作法可供日後同類需求沿用：

- **範圍限定用 `:has()`**：`.lf-layout:has(.lf-report-page)` 才綁 `100dvh`，其他頁維持
  「內容多長就多長」。舊瀏覽器不支援時整組規則失效、頁面回到可捲動——功能不壞，只是不再保證一屏。
- **高度由外而內分配**：頁面 → 圖表區 `flex:1` → 卡片 `height:100%` → 畫布容器填滿。
  每一層都要 `min-height: 0`，否則 flex 子項的預設 `min-height:auto` 會讓內容撐破容器。
- **canvas 絕對定位**：Chart.js 的 canvas 留在文件流裡會回頭撐大容器，形成量測震盪
  （同一視窗尺寸每次載入算出不同高度）。`position:absolute; inset:0` 讓尺寸單向由版面流向圖表。
- **可讀性下限用 px 不用 rem**：那是「圖看不看得懂」的物理尺寸，用 rem 會隨字級偏好與瀏覽器字級設定放大，
  選「大」字級反而在同一台螢幕逼出捲軸。
- **Bootstrap `.row` 不直接當 flex 子項**：它的負 `margin-top` 會讓高度算錯，多出幾像素的捲軸；
  外面包一層普通 div 隔開；多欄的卡片列用 CSS grid（`minmax(0, …)`）排列，不借用 `.row`。
- **`overflow: auto` 不是 `hidden`**：視窗矮到觸發下限時內容仍捲得到；hidden 會直接裁掉且救不回來。
- **列印與窄螢幕（≤991px）整組解除**：紙張是分頁的，窄螢幕單欄、自然捲動。解除規則要寫在畫面規則之後（`pages.css` 的報表區）：
  同權重的規則後者勝出，放在載入順序較前的 `layout.css` 會被畫面規則蓋掉。

## 6b. 頁面層級規則（一看就知道要點哪裡）

目標：使用者進任何一頁，三秒內知道「這頁要我做什麼、下一步點哪裡」。以下規則逐條可由 UI 守門測試檢查
（`FrontendConsistencyUiTests` 等），新頁面與改版一律適用。

**動作層級**
- **每頁至多一個主要按鈕**（`btn-primary`，實心）：它就是這頁最常做的那件事。其餘一律 `btn-outline-secondary`（髮絲邊）；
  危險動作 `btn-outline-danger`（無框危險色文字）並經 `confirmAction` 二次確認。modal 內另計（每個 modal 同樣至多一個主要按鈕）。
- **清單列內重複出現的動作**（每列一顆「回覆」）不算頁面主要按鈕，樣式用 `btn-sm btn-outline-primary`（無框品牌色文字）——
  一頁 20 顆實心按鈕等於沒有重點。外觀見 §8.3。
- 功能未啟用、前置條件未滿足時，相關動作按鈕**不可呈現為主要樣式**；改顯示指路文字（「到 X 頁啟用」）。

**三種狀態版型必須看得出差別**
- **載入中**：`renderLoading`／spinner，不佔用空狀態版型。
- **空狀態**（`renderEmpty`）：灰色圖示＋標題＋**下一步**（hint 或動作連結）；確實「這是正常的」也要明說
  （例：「本期沒有問題訊號——規則、趨勢與關聯層皆未命中」）。下一步不得指向檢視者沒有權限的頁，依能力分流文案。
- **錯誤態**（`renderError`）：警示色圖示＋可讀原因＋「重試」按鈕；**不得借用空狀態版型**
  ——使用者要分得出「壞了」與「沒資料」。

**資訊密度**
- 說明文字的取捨見 WEB-SPEC §8.6a（保留後果句、其餘收進 `?` 或刪除）；一個下拉有多重副作用時，把副作用做成選項旁的對照表，不寫整段散文。
- 日常動線用不到的內容（開發工具、校準、純調校參數）收進「進階」收合區，預設收合、記住使用者的展開選擇（`localStorage`，try/catch 包起來）。
- KPI 統計卡一律用 `core/ui.js` 的 `kpiStrip(groups)`（唯一出口，`FrontendConsistencyUiTests` 守門）：卡片分組（可有組標題，列高固定、有無說明圖示同一條基線），組內 grid `repeat(auto-fill, minmax(11rem, 1fr))`（不設上限，4 張卡在 1280 寬同一列），
  寬螢幕（≥1200px）放得下的組依卡數比例並排、放不下整組換列（兩個 4 卡組要約 1900px 才並排）；575px 以下每列兩張。每張卡固定「數字／標籤／副標」三列，副標沒有內容也保留兩行高，
  同列等高；標籤可換行、副標最多兩行後省略（完整文字在 `title`），可行動的句子放前半。副標只放含資料或口徑的句子。卡片與面板同一語言（透明邊框＋細環）；可點的卡右上角有「›」當靜止時的線索，滑過只變底色，不位移。
- 卡片超過一排且第一排已足夠判斷時，`kpiStrip(groups, { singleRow: true })` 搭配 `core/reveal.js` 的 `revealFirstRow` 只顯示第一排：576px 以上各組同排依卡數分寬、卡片撐滿；依實際格線欄數收合、一個區域一顆揭露鈕、按鈕寫出被藏的數量與告警數、列印時全部顯示。不用固定高度裁切。

**互動**
- 送出後**就地更新**：只重繪受影響的列或區塊，保留使用者的勾選、展開與捲動位置；不整頁重載。
- 表單驗證錯誤：欄位紅框＋欄位下方 `invalid-feedback` 文字＋`focus` 到第一個錯誤欄位；錯誤訊息容器帶
  `role="alert"` 或 `aria-live`。紅框單獨出現不算完成。
- 有未送出內容的 modal，Esc／點遮罩／關閉鈕都要先確認；長文字輸入自動保留草稿（見 `core/note-editor.js`）。
- 網址 hash 對應頁籤（`bindTabs(..., { hash: true })`），深連結必須落在正確頁籤。

**窄螢幕（處理人會在機房用手機看單）**
- 375px 寬必須能完成「看單 → 回覆 → 下一筆」；統計卡、表格工具列在 `< md` 時換列，不固定欄數。
- 處理人會用手機操作的頁面（我的交辦、交辦單詳情、風險日詳情的處理面板）：可點擊元素觸控區至少 36px 高；其餘頁面的下限是 §8.2 的 24×24。清單列的主要動作在窄螢幕不得被擠出畫面（放列尾、允許換行）；窄螢幕隱藏的欄位若帶操作（連結、編輯鈕），摘要行要保留同一個操作。

**處理人畫面的用詞**
- 處理人（只有 `Handle`）看到的概念只有：**交辦單、主機、狀態**。「案件」「日層級狀態」「出現」只出現在管理視角與進階檢視。
  不處理的三種機制在處理人畫面只露出「不處理（需說明）」，靜音與抑制屬管理動作。
- 側欄與頁面標題使用現行導覽定義的顯示字串：`我的交辦`、`排程作業`、`PRTG 維護`、
  `問題負責與靜音`。說明書 manifest 的 `title` 必須對照 `layout.js` 與 View 的實際顯示文字；
  守門測試不能只驗 manifest 內部彼此相等。
- PRTG 的同步／回填啟動入口在「PRTG 維護」；「排程作業」只呈現狀態、進度與停止鈕。
  清理提示要分開說明監看裝置、數值取數對象與歷史資料保留期，不能用一個「範圍外」標籤混在一起。
- 任何外部系統輪詢都要有請求逾時、取消訊號與分頁／批次上限；等待中的畫面必須提供可理解的停止或失敗狀態。

## 7. 落地對應

| 層 | 檔案 | 內容 |
|---|---|---|
| 樣式表 | `wwwroot/css/` 四層，載入順序 `base.css` → `components.css` → `layout.css` → `pages.css`，唯一引用點 `Views/Shared/_Styles.cshtml` | base＝token、字型、元素預設、Bootstrap 元件級變數；components＝跨頁元件；layout＝外殼、頁首、兩欄與側軌、列印與窄螢幕外殼；pages＝單頁專屬 |
| token | `base.css` `:root` 區 | 本文件 §2–4、§8 全部取值 |
| 字型 | `wwwroot/fonts/` + `base.css` `@font-face` | Fira Sans ×4、Fira Code ×1（可變字重 300–700；latin woff2） |
| 元件 | `wwwroot/js/core/ui.js`、`format.js`、`charts.js` + `components.css` | 列高/內距/動效/mono 套用 |
| 頁首 | `ui.js` `renderPageHeader` + `.lf-page-header` | topbar 為唯一標題；頁首只放一句副標與頁面層級動作，每頁至多一個 `btn-primary`（§6b） |
| 處理面板與說明欄 | `handling-panel.js`、`note-editor.js` + `.lf-handling*`／`.lf-note-editor__*`／`.lf-form-actions` | 四區上邊線分隔；chips 自動排欄（最小格寬 4.75rem；右欄 1024 寬約 231px 時兩欄）；說明欄尾區與 textarea 同一外框、`--lf-gray-50` 底；主鈕在獨立動作區 |
| 詳情頁右欄側軌 | `core/side-rail.js` + `.lf-detail-layout`／`.lf-side-rail` | ≥ lg：grid 主欄 `minmax(0,1fr)`＋右欄收合 3rem／展開 `min(20rem, 34%)`；直式按鈕（`writing-mode: vertical-rl`）含標籤與一行摘要；無寬度動畫；< lg 與列印上下堆疊 |
| 長文字收合 | `ui.js` `collapsibleText` + `.lf-collapsible--clamped` | 超過 300 字才收合；純文字截到分隔符＋「…」，區塊以 9rem 高度＋底部漸層（展開格內漸變到 `--lf-gray-50`）；切換鈕是 `disclosure`（標籤「全文」）、列印全文 |
| 風險日詳情頁首 AI 摘要 | `record-detail.js` `aiSummaryBlock`／`briefBlock` + `.lf-briefing__*`（`components.css`） | 以「AI 摘要」徽章標註，不用 `.lf-ai-block` 的左框淡底；層級由版面表達，不靠框線與徽章。結論列＝AI 標題（`--lf-font-size-xl`、600、`--lf-text`，頁首唯一的大字）＋一顆 `lf-badge--secondary`「AI 摘要」（降級／放棄／分析中／統計模式改放狀態徽章）；其下「重點速報」（`lf-text-label` 眉標列右側複製鈕、`--lf-font-size-lg`／500／行高 1.7／`--lf-measure`、無底無框）與「建議處置」並排：≥ 992px 約 1.6 : 1、右欄最小 16rem，窄螢幕直排，缺一則另一佔滿；建議處置是頁首唯一有底色的面（`--lf-primary-soft`、`--lf-radius`、`--lf-space-3`，眉標 `--lf-primary-text`），統計模式不上底色；再下一條 1px `--lf-border` 細線分隔「狀況」「趨勢」兩欄（`repeat(auto-fit, minmax(min(20rem, 100%), 1fr))`）；四段共用 `lf-text-label` 眉標（`h3`）；缺值的段連眉標與細線都不渲染；列印時結論以下單欄、複製鈕不印 |
| 鍵值小表 | `ui.js` `buildKeyValueTable` | 鍵值型小表的共用元件（兩欄 label／value，可選等寬），頁面不自建 `<table>` |
| 版面 | `Views/Shared/_Layout.cshtml` + `layout.css` | 側欄/頂欄新語彙 |
| 版面（排程作業頁） | `Views/Pages/Runs.cshtml` + `.lf-run-*`／`.lf-kv` | 三張等寬狀態卡＋頁籤置頂；進度軌預留高度避免輪詢跳動 |
| 前置 | `wwwroot/lib/bootstrap/dist/` | 5.1→5.3 升級（元件級變數 retheme 的前提） |

## 8. 設計系統 v3：層級、揭露、動作與寬度

色票與 token 見 §2～§5；本節定義「怎麼用」：每頁的層級由版面與共用元件表達，不由各頁拼工具類。
核心原則：**版面能說明的不再寫一遍說明**（同 WEB-SPEC §8.6a）。守門測試在 `DesignSystemGuardUiTests`、`AccessibilityUiTests`。

### 8.1 文字角色

頁面只用角色類別，不再現場組 `small`／`text-muted`／`fw-semibold`；沒有角色的文字就是正文。
一個文字節點只帶一個角色；角色一律用 rem token（Bootstrap 的 `.small` 是相對 em，巢狀會連乘縮小）。

| 角色 | 用途 | 取值 |
|---|---|---|
| `lf-text-title` | 列或區塊的主標 | base、600、`--lf-text` |
| `lf-text-meta` | 次要資訊（次數、時段、來源） | sm、400、`--lf-text-muted` |
| `lf-text-label` | 眉標、欄位標籤 | sm、600、`--lf-text-muted` |
| `lf-text-data` | 技術值與數字 | `--lf-font-mono`、tabular-nums；不在字中斷行（IP、日期、來源名不拆成逐字直排），表格內整段不斷行（欄寬讓給技術值，折行留給成段文字欄；展開列、`lf-break-anywhere` 容器內，以及 `<pre>`／`lf-pre-wrap`／`lf-pre-line` 這類保留換行的內容除外）；放不下的長字串由容器的 `lf-break-anywhere` 斷行 |
| `lf-text-alert` | 風險狀態文字（逾期、失聯、失敗） | 600、`--lf-danger-text` |

紅、黃、綠字只有三種合法用途：驗證與操作結果走 `statusNote`（`core/elements.js`，失敗 `role="alert"`、其餘 `role="status"`）；
風險狀態走 `lf-text-alert`；其餘一律一般文字色（例：相關帳號是佐證資訊，不是風險訊號，不上紅字）。表單必填星號用 `lf-required-mark`（同色、`aria-hidden`）；需要使用者處理才能繼續、或選了會有明顯後果的提示用 `statusNote` 的 warning 型，不降成次要文字。
一行次要資訊用 `metaLine(items)`：空值自動略過，沒有任何有效項目時回傳 null；間隔由 gap 提供，窄寬度整項換行。
表格裡的帳號、主機名、Sentinel 名稱、來源這類識別值加 `text-nowrap`，不在連字號處斷行；名稱欄給 `lf-minw-md` 讓超長名稱折行不超過四行。

### 8.2 揭露與動作：三種語意三種外觀

| 語意 | 元件 | 外觀 |
|---|---|---|
| 就地展開／收合 | `elements.js` `disclosure(標籤, 目標)` | 箭頭＋文字、無底線、次要文字色，滑過與焦點轉主色；收合朝右、展開朝下；標籤寫所指對象（「全文」「佐證」），展開前後不變，狀態看箭頭與 `aria-expanded` |
| 工具列的次要條件 | `elements.js` `filterDisclosure(目標, 已套用數)` | 同就地展開；標籤「更多條件」後接已套用的條件數（0 個時不寫數字），進頁已有套用中的條件就預設展開 |
| 開 modal 或面板 | `ui.js` `button(文字, { opens: 'dialog' })` | 小髮絲邊按鈕＋圖示（表格列內無框），數量寫進標籤（「原始訊息 3」）；加 `aria-haspopup="dialog"` |
| 換頁 | `a[href]`（走 `appUrl()`） | 主色文字，底線只在滑過與焦點出現 |

`btn-link` 不當按鈕樣式；就地展開的標籤不寫「展開…」。`disclosure` 放在可點擊展開的表格列裡時自行阻止冒泡；列印時按鈕不印、由按鈕管理的目標一律印出（`lf-disclosure-target`），`filterDisclosure` 的篩選欄位除外。
點擊區下限 24×24 CSS px（`max(24px, 1.5rem)`，小字級時 rem 會低於 24px），全站適用；處理人會用手機操作的頁面另依 §6b 至少 36px 高。

### 8.3 按鈕

- **變體白名單**：`primary`（每頁、每個 modal 至多一顆）、`outline-primary`（列內重複動作）、`outline-secondary`（其餘）、
  `outline-danger`（危險動作入口）、`danger`（危險動作的確認鈕）。`confirmAction` 的 `confirmVariant` 只用 `primary` 或 `danger`；
  同一類確認的輕重差別交給確認框內提示框的顏色，不靠按鈕換色。唯一例外：深色側欄上的登出鈕用 `outline-light`。
- **變體名稱是角色，不是外觀**（外觀只在 `base.css` 定義一次）：`primary`／`danger` 實心；`outline-secondary` 面板色底＋髮絲細環（`--lf-border-strong`），滑過出現凹槽底；
  `outline-primary` 無框品牌色文字、`outline-danger` 無框危險色文字，滑過出現對應淡底；表格列內帶圖示的 `outline-secondary` 小按鈕無框，靠圖示辨識。
  所有按鈕保留 1px 邊框（無框者為透明），強制色彩模式下仍有輪廓。停用：凹槽底、次要文字色、`not-allowed` 游標。
- **分段控制**（互斥或可複選的切換）：全站一種外觀——一條凹槽（`--lf-sunken`），選中的選項浮起成面板色並加粗。`.btn-group` 一律是分段控制；
  不是 `.btn-group` 但子按鈕帶 `aria-pressed` 的容器（設定頁層級）同一外觀。按下狀態以 `.active` 或 `aria-pressed="true"` 表達，新寫的切換兩者都設。
  凹槽裡的停用選項不用一般停用的凹槽底（會與凹槽同色），改為透明底＋淡化。
- **尺寸**：頁首動作、表單動作區、modal footer 用預設尺寸；卡片標頭、工具列、表格列內用 `sm`。
- **位置**：頁面動作在頁首右側；卡片動作在卡片標頭右側；表單動作在尾端靠右、主要動作最右（與 modal 一致）；
  列內動作在列尾；工具列篩選在左、動作在右。
- 頁面模組的按鈕一律經 `ui.js` `button()`，不手寫 `btn btn-…` 類別。

### 8.4 寬度策略與中斷點

- **中斷點**只用 576／768／992／1200／1600，寫法 `min-width: N`、`max-width: N−0.02`。唯一例外 `max-width: 1799.98px`
  是 KPI 收合時「全部卡片放得下」的內容寬度，不是版面級中斷點。`core/side-rail.js` 的 992（並排模式）與 CSS 同值；1600（預設展開）只在 `side-rail.js` 使用，樣式表沒有對應的 `@media`。卡片內頂層表格容器是尺寸容器（`container-type`），展開列內容依它限制在可視寬度內。
- `--lf-content-max: 120rem`：單欄表單頁（設定、初始設定）在 View 設 `ViewData["ContentWidth"] = "bounded"`，內容區不超過它、靠左；
  其餘頁面（清單、儀表板、報表、維護頁與詳情頁）隨視窗滿版。詳情頁是卡片與表格、成段文字另受 `--lf-measure` 限制，滿版不會拉長文字行（`ContentWidthGuardUiTests` 守門）。
- `--lf-measure: 40em`：成段文字的行寬上限，表格與技術值不套用。
- 寬螢幕（≥ 1600px）詳情頁側軌預設展開，把多出的寬度用在並排，而不是拉長左欄行寬；使用者在本頁按過收合就照使用者的。
- 長字串：`body` 全站 `overflow-wrap: break-word`（沒有空白的連續字串放不下時才斷，一般字詞不受影響）；
  格線欄寬用 `minmax(min(Xrem, 100%), 1fr)`，窄容器不被最小欄寬撐破；資料來的名稱（chip、群組名）比容器寬時換行，不截斷。

### 8.5 判斷層與佐證層

資料量大的列（問題、交辦、主機）分兩層：
- **判斷層**（預設可見）：回答「要不要處理」——嚴重度、標題、一句白話、次數與時段、趨勢、狀態，以及入侵判讀必要的帳號／來源摘要。
- **佐證層**（列展開）：回答「憑什麼」——處置參考、登入失敗明細、數值佐證、來源觀測。內容一字不減、列印全顯示，只是換位置。

### 8.6 共用元件一覽（頁面不另寫一份）

| 用途 | 出口 |
|---|---|
| 單一文字節點套角色 | `elements.js` `roleText(角色, 文字)` |
| 「標籤 值」一組（放進 metaLine） | `elements.js` `fact(標籤, 值)` |
| 一行次要資訊 | `elements.js` `metaLine(items)` |
| 容器內的長字串可在任意位置斷行 | `lf-break-anywhere` |
| 表單必填星號 | `lf-required-mark` |
| 就地展開／收合 | `elements.js` `disclosure(標籤, 目標, opts)` |
| 工具列的次要條件（「更多條件 2」） | `elements.js` `filterDisclosure(目標, 已套用數, opts)`，回傳 `{ button, refresh }` |
| 操作結果與驗證訊息 | `elements.js` `statusNote(error／warning／success／info, 文字)` |
| 按鈕 | `ui.js` `button(文字, { variant, size, icon, title, opens, className, disabled, onClick })` |
| 換頁但要按鈕外觀 | `ui.js` `linkButton(文字, href, opts)` |
| 頁內導航（捲到分節、切頁籤） | `lf-nav-item`（句中用 `lf-nav-item--inline`） |
| 只有圖示的連結 | `lf-icon-link` |
| 詳情頁頁首卡 | `lf-entity-head`（`__title`／`__link`／`__badges`／`__actions`／`__facts`，layout.css） |
| 可展開表格就地重繪保留展開 | `renderTable(..., { rowKey, expandedKeys })`；`toggleAllTableDetails` 走列自己的切換 |
| KPI 數字語意色 | `kpiStrip` 的 `variant` → `lf-stat__value--danger／warning／secondary`：只有 `danger` 上色；`warning` 數字維持文字色、標籤前一顆警示色圓點；`secondary`（零或無事）淡化 |
| 狀態、層級、旗標標記 | `format.js` `statusBadge`／`severityBadge`／`riskBadge`／`elevatesBadge`（形式見 §8.8） |

表格內的勾選框（含可展開列第一欄）、換頁連結（滑過才有底線）、分頁鈕、排序表頭、關閉鈕、說明圖示鈕、`summary` 的點擊區都已在共用樣式處理。
表格寬到要橫向捲動時，卡片內頂層表格的展開列內容固定在可視寬度內（尺寸容器＋sticky）。

### 8.8 標記與顏色訊號

有底色的標記是稀有資源：一張表格裡每列都有兩三顆彩色膠囊時，就沒有東西被看見。

- **一般標記＝圓點加文字**（`lf-badge lf-badge--<變體>`）：無底無框，前方一顆圓點，文字維持文字色。變體只決定圓點顏色
  （`danger`／`warning`／`success`／`info`／`primary` 為語意色，`neutral`／`secondary`／`light`／`dark` 為灰）；同一個變體可以表示狀態，也可以表示標註。
  圓點以邊框畫（列印不印背景色時仍在）；帶圖示的標記不畫圓點，由圖示表示。相鄰的標記間距大於圓點到文字的距離（沒有底色當邊界，靠間距分組）。
- **層級**（嚴重度、日風險等級）由層級出口加 `lf-badge--level`：`severityBadge`、`severityCountBadge`、`riskBadge`。只有最高一級（風險「高」）保留淡底。
- **「重大」旗標**由 `elevatesBadge` 加 `lf-badge--flag`，保留淡底——這兩個是全站僅有的有底色標記。
- **標籤外觀的例外**：內含移除鈕的條件標籤（`:has(> button)`，例：問題查詢已選主機）與明確加 `border` 的歸屬標籤（群組、負責人），維持細框標籤、無圓點。
- **KPI 數字**：見 §8.6；最嚴重的類別卡（`lf-card--critical`，`dashboard.js` 以這個類別判斷告警）不加色條，數字轉危險色。
- **主機風險月曆**：只有高風險用實色；中風險淡琥珀加 3px 底線（灰階下與低風險分得出）、低風險近中性、無紀錄空框。圖例的色塊用同一組 token 與線索（`data-legend`）。
- 舊的 Bootstrap 徽章寫法（`badge bg-*`／`text-bg-*`）不再新增。

### 8.7 驗收工具（LogForesight.Tests）

- **資料集** `UiFixture/`：經產品 `StorageBackend` 產生涵蓋版面邊界的 SQLite（長名稱、無空白長字串、HTML 字元、0／1／極多筆、各種狀態與角色）。
  一般測試每次斷言矩陣齊全；設 `LF_UI_FIXTURE=<目錄>` 時把 DB 與情境清單留在 `<目錄>/ai`、`<目錄>/noai`。
  資料日期相對於產生當天（「昨日」就是產生前一天），隔天截圖前要重產，否則預設期間是空的。
- **啟動站台** `tools/ui-serve.mjs --fixture <目錄>/ai [--port] [--path-base /LogForesight]`：從獨立發佈目錄、以資料集複本啟動（不鎖 bin、背景服務改寫不影響下一次）。
  Git Bash 會把 `/LogForesight` 改寫成 Windows 路徑，要加 `MSYS_NO_PATHCONV=1`。
  站台或量測執行中不要同時跑 `dotnet test`：機器忙時 Node 行為案例會逾時（輸出空白而失敗），單獨重跑就過。
  `ui-shots.mjs` 被逾時或中斷時會留下無頭 Edge 程序，累積後同樣讓行為案例在約 5 秒處逾時：跑測試前清掉命令列含 `--headless` 的 `msedge`（不要動使用者自己開的瀏覽器）。
  情境步驟 `evalAssert`（函式本體，回傳 null 通過、字串為失敗訊息）用來量六項指標以外的東西：圖示外框是否落在按鈕內、字高下限、欄數、是否需捲動。
- **截圖與量測** `tools/ui-shots.mjs --base <網址> --fixture <目錄>/ai [--matrix full|widths|quick] [--height] [--only]`：
  情境清單 `tools/ui-scenarios.json`（資料集網址寫 `fixture:<鍵>`），步驟見檔頭；輸出截圖與 `summary.json`，任一量測項非零時結束代碼非零。
- **前後比對** `tools/ui-diff.mjs <基準> <新輸出>`：位元組相同或列出像素差異範圍。
