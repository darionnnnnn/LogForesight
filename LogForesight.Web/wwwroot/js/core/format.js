/**
 * 顯示格式化的單點定義（docs/WEB-SPEC.md §8.1、§8.2 原則 3）。
 *
 * 風險等級、嚴重度、處理狀態、執行結果的顏色對應**只在這裡寫一次**：
 * 同一個顏色在圖表、徽章、卡片、時間軸中必須是同一個意義，
 * 各頁自己判斷的話遲早會出現「這頁的黃色是中風險、那頁的黃色是警告」。
 */

/** 風險等級 → 徽章 CSS class（後端回傳中文「高/中/低」） */
const RISK_CLASS = {
    '高': 'lf-badge--high',
    '中': 'lf-badge--mid',
    '低': 'lf-badge--low'
};

/** 嚴重度 → Bootstrap 語意色 */
const SEVERITY_VARIANT = {
    Critical: 'danger',
    High: 'warning',
    Medium: 'info',
    Low: 'secondary'
};

/** 處理狀態 → { 顯示文字, Bootstrap 語意色 } */
const HANDLING_STATUS = {
    open: { text: '未處理', variant: 'danger' },
    in_progress: { text: '處理中', variant: 'primary' },
    resolved: { text: '已處理', variant: 'success' },
    wont_fix: { text: '不處理', variant: 'secondary' },
    false_positive: { text: '誤報', variant: 'secondary' },
    known_noise: { text: '已知雜訊', variant: 'secondary' }
};

/** 風險等級徽章元素 */
export function riskBadge(riskLevel) {
    const span = document.createElement('span');
    span.className = `lf-badge ${RISK_CLASS[riskLevel] ?? 'lf-badge--low'}`;
    span.textContent = `${riskLevel}風險`;
    return span;
}

/** 嚴重度徽章元素 */
export function severityBadge(severity) {
    const span = document.createElement('span');
    span.className = `badge text-bg-${SEVERITY_VARIANT[severity] ?? 'secondary'}`;
    span.textContent = severity;
    return span;
}

/** 處理狀態徽章元素 */
export function handlingBadge(status) {
    const meta = HANDLING_STATUS[status] ?? { text: status ?? '未處理', variant: 'secondary' };
    const span = document.createElement('span');
    span.className = `badge text-bg-${meta.variant}`;
    span.textContent = meta.text;
    return span;
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
