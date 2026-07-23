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

/** 風險等級 → 徽章 CSS class（後端回傳中文「高/中/低」） */
const RISK_CLASS = {
    '高': 'lf-badge--high',
    '中': 'lf-badge--mid',
    '低': 'lf-badge--low'
};

/** 嚴重度 → 淡色徽章 variant（對應 site.css 的 lf-badge--*）*/
const SEVERITY_VARIANT = {
    Critical: 'danger',
    High: 'warning',
    Medium: 'info',
    Low: 'neutral'
};

/** 處理狀態 → { 顯示文字, 淡色徽章 variant } */
const HANDLING_STATUS = {
    open: { text: '未處理', variant: 'danger' },
    in_progress: { text: '處理中', variant: 'primary' },
    resolved: { text: '已處理', variant: 'success' },
    wont_fix: { text: '不處理', variant: 'neutral' },
    false_positive: { text: '誤報', variant: 'neutral' },
    known_noise: { text: '已知雜訊', variant: 'neutral' }
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

/** 風險等級徽章元素 */
export function riskBadge(riskLevel) {
    const span = document.createElement('span');
    span.className = `lf-badge ${RISK_CLASS[riskLevel] ?? 'lf-badge--low'}`;
    span.textContent = `${riskLevel}風險`;
    return span;
}

/** 嚴重度徽章元素 */
export function severityBadge(severity) {
    return statusBadge(severity, SEVERITY_VARIANT[severity] ?? 'neutral');
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

/** 千分位數字 */
export function formatNumber(value) {
    if (value === null || value === undefined) return '';
    return Number(value).toLocaleString('zh-TW');
}
