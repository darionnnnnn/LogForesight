/**
 * 顯示格式化的單點定義（docs/WEB-SPEC.md §8.1、§8.2 原則 3）。
 *
 * 風險等級、嚴重度、處理狀態、執行結果的顏色對應**只在這裡寫一次**：
 * 同一個顏色在圖表、徽章、卡片、時間軸中必須是同一個意義，
 * 各頁自己判斷的話遲早會出現「這頁的黃色是中風險、那頁的黃色是警告」。
 */

import { icon } from './ui.js';

/**
 * 風險類別的中文名（後端回傳英文列舉字串）。集中在這裡，取代先前散在 4 個頁面模組的
 * 各自副本——同一個對照表複製多份，遲早有頁面漏改（新增類別時尤其）。
 */
export const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

/** 類別英文列舉 → 中文名，查無回原字串 */
export function categoryName(category) {
    return CATEGORY_NAMES[category] ?? category;
}

/**
 * 使用者名稱的唯一顯示格式（docs/FEEDBACK-8-PLAN.md #6）：「顯示名稱(帳號)」（半形括號）。
 * 全站只要是使用者名稱欄位一律走這裡——分散各頁各自組字串遲早有一處漏改或用了全形括號。
 * 只有帳號（查無顯示名稱）→ 顯示帳號；只有顯示名稱（無帳號可比對）→ 顯示名稱本身。
 */
export function formatUserName(displayName, account) {
    if (displayName && account) return `${displayName}(${account})`;
    return displayName || account || '';
}

/** 風險等級 → 徽章 CSS class（後端回傳中文「高/中/低」） */
const RISK_CLASS = {
    '高': 'lf-badge--high',
    '中': 'lf-badge--mid',
    '低': 'lf-badge--low'
};

/**
 * 嚴重度 → 淡色徽章 variant（對應 site.css 的 lf-badge--*）。
 * docs/HISTORY.md #1（B1 三級化）：Critical 已收斂進 High，不再是獨立層級——
 * 原本「特別嚴重」的意義改由 elevatesBadge() 的「重大」徽章承接，不再靠嚴重度本身多分一級。
 */
const SEVERITY_VARIANT = {
    High: 'warning',
    Medium: 'info',
    Low: 'neutral'
};

/**
 * 嚴重度英文列舉 → 中文顯示名。內部值（Set、URL 參數、API 欄位、<option value>）
 * 一律維持英文；只有畫面文字改中文。不帶「風險」後綴，避免和日風險等級的
 * 「高風險/中風險/低風險」（riskBadge）字面撞在一起——兩者色系也不同。
 */
export const SEVERITY_NAMES = { High: '高', Medium: '中', Low: '低' };

/** 嚴重度英文列舉 → 中文名，查無回原字串 */
export function severityName(severity) {
    return SEVERITY_NAMES[severity] ?? severity;
}

/**
 * 嚴重度合法值，由重到輕（docs/HISTORY.md S11）。取代 record-detail.js／
 * settings.js 各自維護的同值陣列——兩份copy遲早有一份漏改（新增嚴重度時尤其）。
 */
export const SEVERITY_ORDER = ['High', 'Medium', 'Low'];

/**
 * 「重大」徽章（docs/HISTORY.md #1，B1 三級化）：命中規則帶
 * ElevatesDayRisk 旗標的問題——這類問題出現當天就直接判定為高風險日，是原「嚴重」等級
 * 唯一的實際作用。詳情頁重點問題列、規則維護頁、跨主機同簽章查詢三處共用同一顆徽章
 * （§8.2 顏色＋文字單一定義原則）。
 */
export function elevatesBadge() {
    return statusBadge('重大', 'danger', {
        title: '命中重大規則——此類問題出現當日即列為高風險日'
    });
}

/**
 * 嚴重度計數徽章（顏色＋文字，如「高 3」）：dashboard.js 的分類卡曾自己拼一份，
 * 把 Low 的底色寫成 secondary（其餘頁面都是 neutral）——同一個「低」在不同頁面
 * 因此曾經是兩種顏色。改走這裡，SEVERITY_VARIANT 單一標準不會再走樣。
 */
export function severityCountBadge(severity, count) {
    return statusBadge(`${severityName(severity)} ${count}`, SEVERITY_VARIANT[severity] ?? 'neutral');
}

/**
 * 處理狀態 → { 顯示文字, 淡色徽章 variant }。對外一律三態（docs/HISTORY.md #12）：
 * 後端 RecordListItemDto 的 handlingStatus 已先經 HandlingStatuses.ExternalOf 收斂
 * （不處理/誤報/已知雜訊…併入「已處理」），這裡只需覆蓋三態；未知值一律回退顯示原字串。
 * 詳細結論只在風險日詳情頁呈現（issue 層級走 IssueHandlingStatuses 的六態原樣顯示，
 * 不經過這個徽章工廠）。
 */
const HANDLING_STATUS = {
    open: { text: '未處理', variant: 'danger' },
    in_progress: { text: '處理中', variant: 'primary' },
    resolved: { text: '已處理', variant: 'success' }
};

/**
 * 泛用淡色徽章工廠（§8.2「顏色＋文字」）——各頁面自訂狀態徽章（啟用/停用/IP衝突…）
 * 統一走這裡，取代散落各頁的 `badge text-bg-*`。variant 對應 site.css 的 lf-badge--*：
 * success | danger | warning | info | primary | neutral | dark。
 * icon（選填）為 sprite symbol id，會以 SVG 前置於文字（取代原本用 emoji 當圖示的做法）。
 */
export function statusBadge(text, variant = 'neutral', { title, icon: iconName } = {}) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${variant}`;
    if (iconName) span.appendChild(icon(iconName));
    const label = document.createElement('span');
    label.textContent = text;
    span.appendChild(label);
    if (title) span.title = title;
    return span;
}

/**
 * 風險等級徽章元素。title（選填，docs/HISTORY.md #11）：日風險等級的判定依據，
 * 解釋「為什麼是這個風險等級」——日風險等級與問題嚴重度是刻意分開的兩套層級，
 * 高風險日不保證看得到高嚴重度問題（見 record-detail.js renderHeader）。
 */
export function riskBadge(riskLevel, { title } = {}) {
    const span = document.createElement('span');
    span.className = `lf-badge ${RISK_CLASS[riskLevel] ?? 'lf-badge--low'}`;
    span.textContent = `${riskLevel}風險`;
    if (title) span.title = title;
    return span;
}

/** 嚴重度徽章元素（title 附英文原值，方便與規則設定頁等處的英文值對照） */
export function severityBadge(severity) {
    return statusBadge(severityName(severity), SEVERITY_VARIANT[severity] ?? 'neutral', {
        title: `嚴重度：${severity}`
    });
}

/** 處理狀態徽章元素 */
export function handlingBadge(status) {
    const meta = HANDLING_STATUS[status] ?? { text: status ?? '未處理', variant: 'neutral' };
    return statusBadge(meta.text, meta.variant);
}

/** yyyy-MM-dd（不做隱式時區轉換：後端給的就是主機當地日期） */
export function formatDate(value) {
    if (!value) return '';
    return String(value).slice(0, 10);
}

/** yyyy-MM-dd HH:mm */
export function formatDateTime(value) {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return String(value);

    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
           `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * 本地日期字串 yyyy-MM-dd（docs/HISTORY.md S12）。
 * **不可用 `date.toISOString().slice(0, 10)`**——那取的是 UTC 日期，在台灣（UTC+8）
 * 凌晨 0~8 點呼叫會少算一天；reports.js 曾經這樣寫，是這個共用函式要修掉的原始 bug。
 */
export function toLocalDateString(date) {
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** 今天的本地日期字串，等同 toLocalDateString(new Date()) */
export function todayLocal() {
    return toLocalDateString(new Date());
}

/** 千分位數字 */
export function formatNumber(value) {
    if (value === null || value === undefined) return '';
    return Number(value).toLocaleString('zh-TW');
}
