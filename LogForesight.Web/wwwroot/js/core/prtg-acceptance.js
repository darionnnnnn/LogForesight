/**
 * PRTG 維護頁「最近一次排程驗收」的三項判定（docs/PRTG-SPEC.md §13）。
 *
 * 純函式：輸入三支既有 API 的回應，輸出逐項結論，畫面只負責排版。
 * 輸入為 undefined＝還在讀取、null＝讀取失敗。**分母為零或沒有資料一律不算通過**——
 * 「什麼都沒量到」不能被讀成「量到而且全對」。
 */

/** 最近 24 個完整小時中，品質達標小時占有目標小時的最低比例（暫定，與可用列門檻同值）。 */
export const SNAPSHOT_HOUR_RATIO = 0.75;

const OUTCOME_TEXT = {
    success: '成功',
    partial: '部分成功',
    failed: '失敗',
    no_output: '無產出',
    disabled: '未啟用'
};

/**
 * @returns {{ key: string, label: string, state: 'ok'|'pending'|'na'|'nodata'|'loading'|'error', detail: string,
 *             path?: string, tab?: string, linkText?: string }[]}
 */
export function evaluateScheduledAcceptance({ scheduleStatus, aiStatus, snapshotHours }) {
    return [scheduledRunItem(scheduleStatus), aiItem(aiStatus), snapshotItem(snapshotHours)];
}

function scheduledRunItem(status) {
    const base = { key: 'scheduled-run', label: '最近一趟排程取數的 PRTG 結果', path: '/runs#list', linkText: '檢視執行紀錄' };
    if (status === undefined) return { ...base, state: 'loading', detail: '讀取中…' };
    if (status === null) return { ...base, state: 'error', detail: '無法讀取排程狀態。' };
    if (status.isRunning) return { ...base, state: 'pending', detail: '取數執行中，結束後再確認。' };
    const day = status.prtgLastScheduledDay;
    if (!day) return { ...base, state: 'nodata', detail: '近 8 天沒有排程執行（立即執行不計入）。' };
    const summary = `${day.date}：finding ${day.findings} 筆、歸戶主機 ${day.attributedHosts} 台`;
    return day.outcome === 'success'
        ? { ...base, state: 'ok', detail: `${summary}。` }
        : { ...base, state: 'pending', detail: `${summary}，結局為「${OUTCOME_TEXT[day.outcome] ?? '未知'}」。` };
}

function aiItem(status) {
    const base = { key: 'ai', label: '取數結束後 AI 判讀跟上', path: '/runs', linkText: '查看 AI 分析卡' };
    if (status === undefined) return { ...base, state: 'loading', detail: '讀取中…' };
    if (status === null) return { ...base, state: 'error', detail: '無法讀取 AI 分析狀態。' };
    if (status.idleReason === 'disabled') return { ...base, state: 'na', detail: 'AI 未設定，不需判讀。' };
    if (status.isRunning) return { ...base, state: 'pending', detail: 'AI 判讀進行中。' };
    if (status.pendingTotal > 0) return { ...base, state: 'pending', detail: `還有 ${status.pendingTotal} 件待判讀。` };
    if (status.lastRunSuccess === false) return { ...base, state: 'pending', detail: '待判讀 0 件，但上一次 AI 執行沒有正常結束。' };
    return { ...base, state: 'ok', detail: '待判讀 0 件。' };
}

function snapshotItem(hours) {
    const base = { key: 'snapshot', label: '快照最近 24 個完整小時', tab: 'mirror', linkText: '查看快照診斷' };
    if (hours === undefined) return { ...base, state: 'loading', detail: '讀取中…' };
    if (hours === null) return { ...base, state: 'error', detail: '無法讀取快照診斷。' };
    const withTargets = hours.filter(hour => hour.targets > 0);
    if (withTargets.length === 0) return { ...base, state: 'nodata', detail: '沒有快照目標或尚未累積資料。' };
    const met = withTargets.filter(hour => hour.databaseUsableValues >= hour.targets).length;
    const latest = hours[hours.length - 1];
    const alive = latest != null && latest.successes > 0;
    const detail = `${met}／${withTargets.length} 小時品質達標` + (alive ? '。' : '；最近一個完整小時沒有成功的快照。');
    return { ...base, state: met / withTargets.length >= SNAPSHOT_HOUR_RATIO && alive ? 'ok' : 'pending', detail };
}
