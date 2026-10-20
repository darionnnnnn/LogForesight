/** 問題處理狀態目錄：值域對齊後端 IssueHandlingStatuses.All，畫面名稱、顏色、是否結案、說明欄規則只在這裡定義一次。 */
export const HANDLING_STATUSES = [
    { value: 'open',           label: '未處理',               shortLabel: '未處理',   variant: 'danger',    closed: false, replyable: false, noteRequired: false, noteLabel: '說明（選填）' },
    { value: 'in_progress',    label: '處理中',               shortLabel: '處理中',   variant: 'primary',   closed: false, replyable: true,  noteRequired: false, noteLabel: '處理說明（選填）' },
    { value: 'observing',      label: '觀察中',               shortLabel: '觀察中',   variant: 'info',      closed: false, replyable: true,  noteRequired: false, noteLabel: '觀察備註（選填）' },
    { value: 'escalated',      label: '無法處理（上報管理者）', shortLabel: '無法處理', variant: 'warning',   closed: false, replyable: true,  noteRequired: true,  noteLabel: '無法處理原因（必填，管理者將據此決定結案或改派）' },
    { value: 'resolved',       label: '已處理',               shortLabel: '已處理',   variant: 'success',   closed: true,  replyable: true,  noteRequired: false, noteLabel: '處理說明（選填）' },
    { value: 'wont_fix',       label: '不處理（評估後決定）',   shortLabel: '不處理',   variant: 'secondary', closed: true,  replyable: true,  noteRequired: true,  noteLabel: '不處理原因（必填）' },
    { value: 'false_positive', label: '誤報',                 shortLabel: '誤報',     variant: 'secondary', closed: true,  replyable: true,  noteRequired: false, noteLabel: '備註（選填）' },
    { value: 'known_noise',    label: '已知雜訊',             shortLabel: '已知雜訊', variant: 'secondary', closed: true,  replyable: true,  noteRequired: false, noteLabel: '備註（選填，供日後回頭確認判斷依據）' }
];

export function statusMeta(value) { return HANDLING_STATUSES.find(s => s.value === value) ?? null; }

/** 依條件取出目錄的子集（各頁的「只列結案類」「只列可回覆的」等篩選一律走這裡，不再寫死值與名稱） */
export function statusesWhere(predicate) { return HANDLING_STATUSES.filter(predicate); }

/** 狀態徽章顏色變體（未知值退回 secondary）；各頁的狀態徽章一律走這裡，不再各自從目錄衍生 */
export function statusVariant(value) { return statusMeta(value)?.variant ?? 'secondary'; }

/** 狀態徽章資料：{ label: 簡稱, variant }；未知值回傳 null，由呼叫端決定如何呈現 */
export function statusBadgeMeta(value) {
    const meta = statusMeta(value);
    return meta ? { label: meta.shortLabel, variant: meta.variant } : null;
}
