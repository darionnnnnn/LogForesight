# LogForesight 設計系統 v2

2026-08-05 視覺改版依據文件。本文件是 site.css token 區、共用元件層（ui.js／format.js／charts.js）與版面骨架的**單一設計依據**；與 WEB-SPEC §8 互補——§8 管架構規範（分層、白名單、驗收條目），本文件管視覺語彙的具體取值與理由。

## 1. 產生方式與定位

- 依據 [nextlevelbuilder/ui-ux-pro-max-skill](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill)（MIT）的設計智能資料庫產生：以「security log analysis enterprise dashboard」「enterprise log analytics admin dashboard professional clean light data-dense」等情境檢索其 84 種 UI 風格、192 色盤、74 組字型配對與 UX 指南後收斂。skill 僅在開發期作為檢索工具，**不進出貨產物、不引入任何執行期依賴**，符合 WEB-SPEC §8.1 憲章。
- 風格定位：**Data-Dense Dashboard（BI/Analytics）× Swiss 極簡**——資料密度優先、低彩度中性底、明確層級、剋制動效。skill 對此風格的定義：「Multiple charts/widgets, data tables, KPI cards, minimal padding, grid layout, space-efficient, maximum data visibility」。
- 色彩策略：**深企業藍（deep enterprise blue）主色 + 琥珀（amber）強調色**，取代 v1 靛藍。skill「Analytics Dashboard」色盤原型：Blue data + amber highlights，語意是「藍＝資料與信任、琥珀＝需要注意的亮點」，與資安日誌產品的「大多正常、少數要看」的資訊結構吻合。
- 深色模式：本輪不做（淺色打磨到位後另案）。

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
| `--lf-sidebar-bg` | `#161d2e` | `#0f1d3a` | 深海軍藍，從「近黑」轉向與主色同族的深藍，品牌感更聚焦 |
| `--lf-sidebar-fg` | `#a9b4c7` | `#9fb0cc` | 隨底色微調 |
| `--lf-sidebar-fg-strong` | `#f1f4fa` | 不變 | |
| `--lf-sidebar-section` | `#6f7c93` | `#68799a` | |
| `--lf-sidebar-active-bg` | `rgba(99,102,241,.22)` | `rgba(59,130,246,.24)` | 換藍族 |
| `--lf-sidebar-hover-bg` | 不變 | 不變 | |
| `--lf-content-bg` | `#f6f7fb` | `#f8fafc` | slate-50，與中性階完全對齊 |
| `--lf-card-bg` | `#ffffff` | 不變 | |
| `--lf-border` | `#e5e9f0` | `#e2e8f0` | slate-200 對齊 |
| `--lf-border-strong` | `#d5dbe6` | `#cbd5e1` | slate-300 對齊 |
| `--lf-text` | `#1e293b` | `#0f172a` | slate-900，資料密集畫面拉高正文對比（16.9:1） |
| `--lf-text-muted` | `#64748b` | 不變 | slate-500（4.76:1，過 AA） |

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

### Bootstrap 5.3 對接

`--bs-primary(-rgb)`、`--bs-link-*`、按鈕／分頁／dropdown／popover 等元件級變數全部改指向 v2 token；`--bs-primary-rgb` 改 `30, 64, 175`。

## 3. 字型

skill 對 dashboard/analytics 情境的一致首選配對：**Fira Sans（UI/正文）+ Fira Code（資料/等寬）**——「Fira family cohesion. Code for data, Sans for labels.」

- **Fira Sans**（latin，400/500/600/700）：標題與 UI 文字。中文 fallback 維持系統字。
- **Fira Code**（latin，400/600）：新增 `--lf-font-mono`，用於日誌原文、事件 ID、主機名、路徑等技術值——資料密集產品的「精確感」主要來源。
- 交付方式：**self-host woff2**（latin subset，每檔約 20–40KB，共 6 檔 ≈ 200KB 以內）放 `wwwroot/fonts/` + `@font-face`（`font-display: swap`）。**不用 CDN**（§8.1 禁外部請求）、**不引入中文 webfont**（MB 級，違反零依賴精神）。
- token 化：

```css
--lf-font-family: "Fira Sans", "Segoe UI", "Microsoft JhengHei", system-ui, sans-serif;
--lf-font-mono: "Fira Code", "Cascadia Mono", Consolas, monospace;
```

- 數字對齊：統計數值與表格數字欄加 `font-variant-numeric: tabular-nums`（Fira Sans 支援）。
- 字級階層／根字級 clamp／使用者三檔倍率：**維持 v1 不動**（已是成熟決策）。

## 4. 形狀、陰影、間距、動效

- **圓角**（收緊一階，data-dense 專業感）：`--lf-radius-sm: .25rem`、`--lf-radius: .5rem`、`--lf-radius-lg: .75rem`、pill 不變。
- **陰影**：階層不變；卡片 hover 提升維持 `xs→md`。
- **間距**：`--lf-space-*` 不變（已是 4/8 節奏）。表格列高目標 36px、卡片內距 12–16px（skill Data-Dense 建議值，Phase 3 元件層落實）。
- **動效**：`--lf-transition: .15s ease` 維持；較大轉場（modal、燈箱）150–300ms `ease-out`；**hover/pressed 不得位移佈局**（用透明度/陰影/色彩）；新增全站 `prefers-reduced-motion: reduce` 支援（動畫關閉、transition 降為 0）。
- **focus ring**：`0 0 0 .2rem rgba(30, 64, 175, .22)`，鍵盤導覽全元件可見。

## 5. Anti-patterns（skill 檢索所得，適用本產品）

- 裝飾性設計（ornate）：不加漸層、玻璃擬態、發光等與資料閱讀競爭的效果。
- 表格無篩選／無排序（No filtering）：既有 §8.6 已保障，不得回退。
- Emoji 當結構性圖示：一律 SVG sprite（既有原則，維持）。
- 寬表格撐破版面：溢出容器一律 `overflow-x: auto`。
- 逐列單獨操作：清單頁維持批次選取（§8.6 #11）。

## 6. 交付前檢查表（skill pre-delivery，Phase 4 逐頁走查用）

- [ ] 主文字對比 ≥ 4.5:1、次要文字 ≥ 3:1
- [ ] 所有可點元素 cursor: pointer、hover 有 150–300ms 平滑回饋
- [ ] focus 態鍵盤可見
- [ ] prefers-reduced-motion 生效
- [ ] 分隔線／邊框在內容密集區可辨
- [ ] 響應斷點 768px（既有側欄轉頂列）不破版；列印樣式存活
- [ ] 無 Bootstrap 預設藍 `#0d6efd` 殘留
- [ ] 字級三檔切換下版面不破

## 6a. 視窗高度驅動的版面（2026-08-05，docs/archive/FEEDBACK-10-PLAN.md §5）

報表頁是全站第一個「一屏內看完」的版面，作法可供日後同類需求沿用：

- **範圍限定用 `:has()`**：`.lf-layout:has(.lf-report-page)` 才綁 `100dvh`，其他頁維持
  「內容多長就多長」。舊瀏覽器不支援時整組規則失效、頁面回到可捲動——功能不壞，只是不再保證一屏。
- **高度由外而內分配**：頁面 → 圖表區 `flex:1` → 卡片 `height:100%` → 畫布容器填滿。
  每一層都要 `min-height: 0`，否則 flex 子項的預設 `min-height:auto` 會讓內容撐破容器。
- **canvas 絕對定位**：Chart.js 的 canvas 留在文件流裡會回頭撐大容器，形成量測震盪
  （同一視窗尺寸每次載入算出不同高度）。`position:absolute; inset:0` 讓尺寸單向由版面流向圖表。
- **可讀性下限用 px 不用 rem**：那是「圖看不看得懂」的物理尺寸，用 rem 會隨字級偏好放大 25%，
  選「大」字級反而在同一台螢幕逼出捲軸。
- **Bootstrap `.row` 不直接當 flex 子項**：它的負 `margin-top` 會讓高度算錯，多出幾像素的捲軸；
  外面包一層普通 div 隔開。
- **`overflow: auto` 不是 `hidden`**：視窗矮到觸發下限時內容仍捲得到；hidden 會直接裁掉且救不回來。
- **列印與窄螢幕（≤768px）整組解除**：紙張是分頁的，手機本來就是捲動閱讀。

## 7. 落地對應

| 層 | 檔案 | 內容 |
|---|---|---|
| token | `LogForesight.Web/wwwroot/css/site.css` `:root` 區 | 本文件 §2–4 全部取值 |
| 字型 | `wwwroot/fonts/` + site.css `@font-face` | Fira Sans ×4、Fira Code ×2（latin woff2） |
| 元件 | `wwwroot/js/core/ui.js`、`format.js`、`charts.js` + site.css 元件區 | 列高/內距/動效/mono 套用 |
| 版面 | `Views/Shared/_Layout.cshtml` + site.css 版面區 | 側欄/頂欄新語彙 |
| 前置 | `wwwroot/lib/bootstrap/dist/` | 5.1→5.3 升級（元件級變數 retheme 的前提） |
