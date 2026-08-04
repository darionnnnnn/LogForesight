/**
 * 執行監控（docs/WEB-SPEC.md §9.10）——dev 每天早上的第一個畫面。
 *
 * 「昨晚哪幾台沒跑」是這頁存在的理由：批次沒跑就不會有任何風險紀錄，
 * 而「沒有紀錄」看起來跟「一切正常」一模一樣。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import {
    renderTable, renderLoading, renderEmpty, labelValue, renderPagination, sortRows, loadPageSize, savePageSize,
    toast, withBusy, confirmAction
} from '../core/ui.js';
import { formatDateTime, formatNumber } from '../core/format.js';

const STATUS_META = {
    success: { label: '成功', color: '#198754' },
    warning: { label: '有警告', color: '#ffc107' },
    failed: { label: '失敗', color: '#dc3545' },
    stopped: { label: '已停止', color: '#fd7e14' },   // 優雅停止（手動或窗口結束）——不是失敗
    running: { label: '執行中', color: '#0d6efd' },
    stuck: { label: '異常中斷', color: '#6f42c1' },
    none: { label: '未執行', color: '#e9ecef' }
};

let currentDays = 14;
let currentLogs = [];

// 單日主機明細（本地排序＋分頁）：2000 台規模下曾整表一次 render，改成與其他清單頁一致的體驗
let dayDetailHosts = [];
let dayDetailSort = { key: 'hostName', dir: 'asc' };
let dayDetailPage = 1;
let dayDetailPageSize = loadPageSize('runs-day-detail');

// 異常彙總（本地排序，筆數通常不多，不加分頁）
let currentErrors = [];
let errorsSort = { key: 'count', dir: 'desc' };

async function load() {
    renderLoading(document.getElementById('run-summary'), 4);
    renderLoading(document.getElementById('run-errors'), 3);

    const [summaries, errors] = await Promise.all([
        api.get(`/api/runs/summary?days=${currentDays}`),
        api.get(`/api/runs/errors?days=${currentDays}`)
    ]);

    renderLegend();
    renderSummary(summaries);
    renderErrors(errors);
}

function renderLegend() {
    const legend = document.getElementById('run-legend');
    legend.replaceChildren();

    for (const [, meta] of Object.entries(STATUS_META)) {
        const item = document.createElement('span');
        item.className = 'd-flex align-items-center gap-1';

        const swatch = document.createElement('span');
        swatch.className = 'rounded d-inline-block';
        swatch.style.width = '12px';
        swatch.style.height = '12px';
        swatch.style.background = meta.color;

        const label = document.createElement('span');
        label.textContent = meta.label;

        item.append(swatch, label);
        legend.appendChild(item);
    }
}

/**
 * 執行總表改每日一列（§5.4 D-4）：取代舊版「主機×日期」矩陣——
 * 兩千台規模下矩陣會炸出過大的 DOM（2000×90 格），彙總後點日期才下鑽看逐主機明細。
 */
function renderSummary(summaries) {
    const container = document.getElementById('run-summary');

    if (summaries.every(s => s.totalHosts === 0)) {
        renderEmpty(container, {
            title: '尚無執行紀錄',
            hint: '分析執行後會自動登記；請至上方「排程設定」啟用排程，或按「立即執行」手動觸發。'
        });
        return;
    }

    renderTable(container, {
        columns: [
            { title: '日期', render: s => dateLink(s.date) },
            { title: '成功', className: 'text-end', render: s => countCell(s.successCount, 'success') },
            { title: '有警告', className: 'text-end', render: s => countCell(s.warningCount, 'warning') },
            { title: '失敗', className: 'text-end', render: s => countCell(s.failedCount, 'failed') },
            { title: '已停止', className: 'text-end', render: s => countCell(s.stoppedCount, 'stopped') },
            { title: '異常中斷', className: 'text-end', render: s => countCell(s.stuckCount, 'stuck') },
            { title: '執行中', className: 'text-end', render: s => countCell(s.runningCount, 'running') },
            { title: '未執行', className: 'text-end', render: s => countCell(s.notRunCount, 'none') },
            { title: '失敗主機', render: s => failedHostsCell(s) }
        ],
        rows: [...summaries].reverse(),   // 最新日期在最上面，跟其他頁的時間排序習慣一致
        empty: { title: '尚無執行紀錄' }
    });
}

function dateLink(date) {
    const link = document.createElement('a');
    link.href = '#';
    link.textContent = date;
    link.addEventListener('click', event => {
        event.preventDefault();
        showDayDetail(date);
    });
    return link;
}

function countCell(count, status) {
    if (count === 0) return document.createTextNode('');
    const span = document.createElement('span');
    span.style.color = STATUS_META[status].color;
    span.style.fontWeight = '600';
    span.textContent = String(count);
    return span;
}

function failedHostsCell(summary) {
    const total = summary.failedCount + summary.stuckCount;
    if (total === 0) return document.createTextNode('');

    const wrap = document.createElement('div');
    wrap.className = 'small';
    wrap.textContent = summary.failedHostNames.join('、');

    if (summary.otherFailedCount > 0) {
        const more = document.createElement('span');
        more.className = 'text-muted';
        more.textContent = `　其他 ${summary.otherFailedCount} 台`;
        wrap.appendChild(more);
    }
    return wrap;
}

const DAY_DETAIL_COLUMNS = [
    { title: '主機', sortKey: 'hostName', sortValue: h => h.hostName, render: h => h.hostName },
    { title: '狀態', sortKey: 'status', sortValue: h => h.status, render: h => statusBadgeCell(h.status) },
    {
        title: '分析天數', className: 'text-end', sortKey: 'daysAnalyzed', sortDefaultDir: 'desc',
        sortValue: h => h.runId != null ? h.daysAnalyzed : -1,
        render: h => h.runId != null ? String(h.daysAnalyzed) : ''
    },
    {
        title: '警告 / 錯誤', className: 'text-end', sortKey: 'errorCount', sortDefaultDir: 'desc',
        sortValue: h => h.runId != null ? h.errorCount : -1,
        render: h => h.runId != null ? `${h.warnCount} / ${h.errorCount}` : ''
    },
    { title: '', className: 'text-end', render: h => h.runId != null ? viewRunButton(h.runId) : '' }
];

/** 點日期下鑽：該天每台主機的狀態，取代舊版矩陣的「一格看一次執行」 */
async function showDayDetail(date) {
    const card = document.getElementById('run-day-detail-card');
    card.classList.remove('d-none');
    card.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.getElementById('run-day-detail-title').textContent = `${date} 各主機狀態`;

    renderLoading(document.getElementById('run-day-detail-list'), 6);
    dayDetailHosts = await api.get(`/api/runs/day/${date}`);
    dayDetailPage = 1;
    renderDayDetail();
}

/** 2000 台規模下曾整表一次 render——改成本地排序＋分頁（資料已一次取回，不必重打 API） */
function renderDayDetail() {
    const sorted = sortRows(dayDetailHosts, DAY_DETAIL_COLUMNS, dayDetailSort);
    const totalPages = Math.max(1, Math.ceil(sorted.length / dayDetailPageSize));
    if (dayDetailPage > totalPages) dayDetailPage = totalPages;
    const pageRows = sorted.slice((dayDetailPage - 1) * dayDetailPageSize, dayDetailPage * dayDetailPageSize);

    renderTable(document.getElementById('run-day-detail-list'), {
        columns: DAY_DETAIL_COLUMNS,
        rows: pageRows,
        sort: dayDetailSort,
        onSort: (key, dir) => {
            dayDetailSort = { key, dir };
            dayDetailPage = 1;
            renderDayDetail();
        },
        empty: { title: '這天沒有任何主機資料' }
    });

    renderPagination(document.getElementById('run-day-detail-pager'), {
        page: dayDetailPage,
        totalPages: dayDetailHosts.length ? totalPages : 0,
        onPage: page => { dayDetailPage = page; renderDayDetail(); },
        pageSize: dayDetailPageSize,
        onPageSize: size => {
            dayDetailPageSize = size;
            savePageSize('runs-day-detail', size);
            dayDetailPage = 1;
            renderDayDetail();
        }
    });
}

function statusBadgeCell(status) {
    const meta = STATUS_META[status] ?? STATUS_META.none;
    const span = document.createElement('span');
    span.className = 'd-inline-flex align-items-center gap-2';

    const swatch = document.createElement('span');
    swatch.className = 'rounded d-inline-block';
    swatch.style.width = '10px';
    swatch.style.height = '10px';
    swatch.style.background = meta.color;

    const text = document.createElement('span');
    text.textContent = meta.label;

    span.append(swatch, text);
    return span;
}

function viewRunButton(runId) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-outline-primary';
    button.textContent = '檢視執行';
    button.addEventListener('click', () => showDetail(runId));
    return button;
}

document.getElementById('run-day-detail-close').addEventListener('click', () => {
    document.getElementById('run-day-detail-card').classList.add('d-none');
});

const ERROR_COLUMNS = [
    { title: '等級', sortKey: 'level', sortValue: e => e.level, render: e => levelBadge(e.level) },
    { title: '訊息', render: e => messageCell(e) },
    { title: '次數', className: 'text-end', sortKey: 'count', sortDefaultDir: 'desc', sortValue: e => e.count, render: e => formatNumber(e.count) },
    { title: '影響主機', sortKey: 'affectedHosts', sortDefaultDir: 'desc', sortValue: e => e.affectedHosts.length, render: e => e.affectedHosts.join('、') },
    { title: '最近發生', sortKey: 'lastSeen', sortDefaultDir: 'desc', sortValue: e => e.lastSeen, render: e => formatDateTime(e.lastSeen) },
    { title: '', className: 'text-end', render: e => detailButton(e.latestRunId) }
];

function renderErrors(errors) {
    currentErrors = errors;
    renderErrorsTable();
}

function renderErrorsTable() {
    renderTable(document.getElementById('run-errors'), {
        columns: ERROR_COLUMNS,
        rows: sortRows(currentErrors, ERROR_COLUMNS, errorsSort),
        sort: errorsSort,
        onSort: (key, dir) => {
            errorsSort = { key, dir };
            renderErrorsTable();
        },
        empty: { title: '此期間沒有錯誤紀錄', hint: '所有批次執行都沒有產生 Error 或 Fatal 等級的訊息。' }
    });
}

function messageCell(group) {
    const div = document.createElement('div');
    div.className = 'small';
    div.style.maxWidth = '40rem';
    div.textContent = group.message;
    return div;
}

function levelBadge(level) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${level === 'Fatal' ? 'dark' : 'danger'}`;
    span.textContent = level;
    return span;
}

function detailButton(runId) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-outline-primary';
    button.textContent = '檢視執行';
    button.addEventListener('click', () => showDetail(runId));
    return button;
}

// ── 執行詳情 ─────────────────────────────────────────────────────────────────

async function showDetail(runId) {
    const card = document.getElementById('run-detail-card');
    card.classList.remove('d-none');
    card.scrollIntoView({ behavior: 'smooth', block: 'start' });

    renderLoading(document.getElementById('run-detail-logs'), 5);

    const detail = await api.get(`/api/runs/${runId}`);
    currentLogs = detail.logs;

    document.getElementById('run-detail-title').textContent =
        `執行詳情　${detail.hostName}　${formatDateTime(detail.startedAt)}`;

    renderStats(detail);
    renderLogs();
}

function renderStats(detail) {
    const stats = [
        { label: '狀態', value: statusText(detail) },
        { label: '耗時', value: detail.durationSeconds != null ? formatDuration(detail.durationSeconds) : '—' },
        { label: '分析天數', value: detail.daysAnalyzed },
        // AI 未設定時這欄多半是「0（失敗 0）」的雜訊；歷史上真的呼叫過 AI 的執行紀錄仍如實顯示
        // （docs/FEEDBACK-7-PLAN.md）
        ...(aiAvailable || detail.aiCalls > 0
            ? [{ label: 'AI 呼叫', value: `${detail.aiCalls}（失敗 ${detail.aiFailures}）` }]
            : []),
        { label: '警告 / 錯誤', value: `${detail.warnCount} / ${detail.errorCount}` },
        { label: '版本', value: detail.appVersion }
    ];

    const container = document.getElementById('run-detail-stats');
    container.replaceChildren();

    for (const stat of stats) {
        const col = document.createElement('div');
        col.className = 'col-6 col-md-2';
        col.append(...labelValue(stat.label, stat.value, { labelClass: 'lf-stat__label' }));
        container.appendChild(col);
    }
}

function statusText(detail) {
    if (detail.finishedAt == null) return '未回報結束';
    if (detail.stopped) return '已停止（手動停止或執行窗口結束）';
    return detail.exitCode === 0 ? '成功' : `失敗（exit ${detail.exitCode}）`;
}

function formatDuration(seconds) {
    if (seconds < 60) return `${seconds} 秒`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} 分 ${seconds % 60} 秒`;
    return `${Math.floor(minutes / 60)} 時 ${minutes % 60} 分`;
}

function renderLogs() {
    const level = document.getElementById('log-level-filter').value;
    const order = { Info: 0, Warn: 1, Error: 2, Fatal: 3 };

    const filtered = level
        ? currentLogs.filter(l => (order[l.level] ?? 0) >= (order[level] ?? 0))
        : currentLogs;

    renderTable(document.getElementById('run-detail-logs'), {
        columns: [
            { title: '時間', render: l => formatDateTime(l.loggedAt) },
            { title: '等級', render: l => logLevelBadge(l.level) },
            { title: '來源', render: l => l.logger },
            { title: '訊息', render: l => logMessageCell(l) }
        ],
        rows: filtered,
        empty: { title: '沒有符合等級的紀錄', hint: '此次執行未產生該等級以上的訊息。' }
    });
}

function logLevelBadge(level) {
    const variants = { Info: 'light', Warn: 'warning', Error: 'danger', Fatal: 'dark' };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${variants[level] ?? 'secondary'}`;
    span.textContent = level;
    return span;
}

function logMessageCell(log) {
    const wrap = document.createElement('div');

    const message = document.createElement('div');
    message.className = 'small';
    message.textContent = log.message;
    wrap.appendChild(message);

    // 完整堆疊只有 Error/Fatal 有，展開才顯示——清單要保持可掃視
    if (log.exceptionText) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-link btn-sm p-0';
        toggle.textContent = '完整堆疊';

        const box = document.createElement('pre');
        box.className = 'report-text small mt-1 d-none';
        box.textContent = log.exceptionText;

        toggle.addEventListener('click', () => box.classList.toggle('d-none'));
        wrap.append(toggle, box);
    }

    return wrap;
}

document.getElementById('log-level-filter').addEventListener('change', renderLogs);
document.getElementById('run-detail-close').addEventListener('click', () => {
    document.getElementById('run-detail-card').classList.add('d-none');
});

for (const button of document.querySelectorAll('[data-days]')) {
    button.addEventListener('click', () => {
        currentDays = Number(button.dataset.days);
        for (const other of document.querySelectorAll('[data-days]')) {
            other.classList.toggle('active', other === button);
        }
        load();
    });
}

// ── 排程設定（docs/WEB-SCHEDULER-PLAN.md §1.4.5）──────────────────────────────
// 這頁的權限是 DevMonitor 或 Maintain 任一（見 PagesController.Runs）：dev 進得來但只能看，
// 排程設定/立即執行/停止這些會動到系統的操作只給 Maintain——data-maintain-only 標記的元素
// 在沒有 Maintain 時整批隱藏，而不是逐一判斷。

let scheduleWindows = [];
let canMaintainSchedule = false;
// AI 未設定時「AI 診斷傾印」開關與徽章整組隱藏（docs/FEEDBACK-7-PLAN.md）：這是除錯用的手動
// 開關，本身不預設開啟，但 AI 沒設定時這個功能沒有意義，不該佔畫面。開關值照常載入/回傳，
// 只是不顯示——避免隱藏期間存檔把設定意外歸零。
let aiAvailable = false;
const runNowModal = new bootstrap.Modal(document.getElementById('run-now-modal'));

async function loadSchedule() {
    const user = await getCurrentUser();
    canMaintainSchedule = hasCapability(user, 'Maintain');
    if (!canMaintainSchedule) {
        for (const el of document.querySelectorAll('[data-maintain-only]')) el.classList.add('d-none');
    }

    const [options, aiStatus] = await Promise.all([
        api.get('/api/admin/schedule/options'),
        api.get('/api/ai/status', { silent: true }).catch(() => null)
    ]);
    aiAvailable = !!aiStatus?.available;
    document.getElementById('schedule-debug-dump-wrap').classList.toggle('d-none', !aiAvailable);

    applyScheduleOptions(options);
    await refreshScheduleStatus();
}

function applyScheduleOptions(options) {
    scheduleWindows = options.windows.map(w => ({ ...w }));
    document.getElementById('schedule-enabled').checked = options.enabled;
    document.getElementById('schedule-debug-dump').checked = options.debugDump;
    document.getElementById('schedule-debug-dump-badge').classList.toggle('d-none', !options.debugDump || !aiAvailable);
    renderScheduleWindows();

    document.getElementById('schedule-updated').textContent = options.updatedAt
        ? `最後更新：${formatDateTime(options.updatedAt)}${options.updatedByAccount ? `（${options.updatedByAccount}）` : ''}`
        : '尚未設定過（沿用預設：關閉）';

    if (!options.enabled) {
        document.getElementById('schedule-next-trigger').textContent = '排程未啟用';
    } else if (options.nextTriggerTime) {
        document.getElementById('schedule-next-trigger').textContent = formatDateTime(options.nextTriggerTime);
    }
}

function renderScheduleWindows() {
    const container = document.getElementById('schedule-windows');
    container.replaceChildren();

    scheduleWindows.forEach((window_, index) => {
        const row = document.createElement('div');
        row.className = 'd-flex align-items-center gap-2 mb-2';

        const start = document.createElement('input');
        start.type = 'time';
        start.className = 'form-control form-control-sm';
        // 固定寬度而非上限：瀏覽器原生 time picker（HH:MM + 時鐘圖示）實測約需 140~150px，
        // 130px 的上限會把數字擠掉（使用者回報時間被遮擋）。
        start.style.width = '150px';
        start.value = window_.start;
        start.disabled = !canMaintainSchedule;
        start.addEventListener('change', () => { window_.start = start.value; });

        const arrow = document.createElement('span');
        arrow.className = 'text-muted';
        arrow.textContent = '→';

        const end = document.createElement('input');
        end.type = 'time';
        end.className = 'form-control form-control-sm';
        end.style.width = '150px';
        end.value = window_.end;
        end.disabled = !canMaintainSchedule;
        end.addEventListener('change', () => { window_.end = end.value; });

        row.append(start, arrow, end);

        if (canMaintainSchedule) {
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn btn-sm btn-outline-danger';
            remove.textContent = '移除';
            remove.disabled = scheduleWindows.length <= 1;
            remove.title = scheduleWindows.length <= 1 ? '至少要保留一個窗口' : '';
            remove.addEventListener('click', () => {
                scheduleWindows.splice(index, 1);
                renderScheduleWindows();
            });
            row.appendChild(remove);
        }

        container.appendChild(row);
    });
}

document.getElementById('schedule-add-window')?.addEventListener('click', () => {
    if (scheduleWindows.length >= 4) {
        toast('執行窗口最多 4 組', 'warning');
        return;
    }
    scheduleWindows.push({ start: '01:00', end: '07:00' });
    renderScheduleWindows();
});

document.getElementById('schedule-form').addEventListener('submit', async event => {
    event.preventDefault();

    const saveButton = document.getElementById('schedule-save');
    const restore = withBusy(saveButton, '儲存中');
    try {
        const saved = await api.put('/api/admin/schedule/options', {
            enabled: document.getElementById('schedule-enabled').checked,
            windows: scheduleWindows,
            debugDump: document.getElementById('schedule-debug-dump').checked
        });
        applyScheduleOptions(saved);
        toast('已儲存排程設定', 'success');
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

// 執行中／閒置輪詢間隔不同（docs/FEEDBACK-8-PLAN.md #2）：執行中要讓進度條「看得出在動」，
// 閒置時沒有進度可看，維持原本的低頻率即可。自我重新排程（而非 setInterval）方便依上一次
// 拿到的狀態決定下一輪間隔。
let wasScheduleRunning = false;
let scheduleStatusTimer = null;

async function refreshScheduleStatus() {
    const status = await api.get('/api/admin/schedule/status', { silent: true }).catch(() => null);
    if (status) applyScheduleStatus(status);

    clearTimeout(scheduleStatusTimer);
    const interval = status?.isRunning ? 3000 : 10000;
    scheduleStatusTimer = setTimeout(refreshScheduleStatus, interval);
}

function applyScheduleStatus(status) {
    document.getElementById('schedule-status-text').textContent = status.isRunning ? '執行中' : '閒置';
    document.getElementById('schedule-run-state').textContent = status.isRunning
        ? `執行中（${status.triggerText}）`
        : '閒置';

    renderScheduleProgress(status);

    const messageEl = document.getElementById('schedule-latest-message');
    if (status.isRunning) {
        messageEl.textContent = status.latestMessage ?? '';
        messageEl.classList.remove('text-danger');
        messageEl.classList.add('text-muted');
    } else {
        // 閒置時顯示「上次執行到底成不成功」（docs/FEEDBACK-7-PLAN.md）：原本失敗只寫 log，
        // 使用者看到「已開始執行」的 toast 之後就再也沒有回饋，只能翻 log 檔才知道其實炸了。
        renderLastRunOutcome(messageEl, status);
    }

    const stopButton = document.getElementById('schedule-stop');
    if (canMaintainSchedule) stopButton.classList.toggle('d-none', !status.canStop);

    if (status.scheduleEnabled && status.nextTriggerTime) {
        document.getElementById('schedule-next-trigger').textContent = formatDateTime(status.nextTriggerTime);
    } else if (!status.scheduleEnabled) {
        document.getElementById('schedule-next-trigger').textContent = '排程未啟用';
    }

    // 執行完自動刷新總表（docs/FEEDBACK-8-PLAN.md #2）：isRunning 由 true → false 時，
    // 使用者不必手動重新整理才看得到剛跑完的這趟（手動與排程觸發都經過這條輪詢，天然涵蓋兩者）
    if (wasScheduleRunning && !status.isRunning) {
        toast('執行已結束，執行總表已刷新', 'info');
        load();
    }
    wasScheduleRunning = status.isRunning;
}

const PROGRESS_PHASE_LABEL = { local: '本機分析', netiq: 'NetIQ 機房分析' };

/** 執行中且有量化進度（total>0）畫百分比進度條；剛啟動／清理階段（total=0）畫不定進度；
 * 閒置時整組隱藏。（docs/FEEDBACK-8-PLAN.md #2） */
function renderScheduleProgress(status) {
    const wrap = document.getElementById('schedule-progress-wrap');
    const bar = document.getElementById('schedule-progress-bar');

    if (!status.isRunning) {
        wrap.classList.add('d-none');
        return;
    }
    wrap.classList.remove('d-none');

    if (status.progressTotal > 0) {
        const pct = Math.min(100, Math.round((status.progressDone / status.progressTotal) * 100));
        bar.classList.remove('progress-bar-striped', 'progress-bar-animated');
        bar.style.width = `${pct}%`;
        bar.title = `${PROGRESS_PHASE_LABEL[status.progressPhase] ?? status.progressPhase ?? ''}　${status.progressDone} / ${status.progressTotal}`;
    } else {
        bar.classList.add('progress-bar-striped', 'progress-bar-animated');
        bar.style.width = '100%';
        bar.title = '準備中…';
    }
}

/**
 * 閒置時的「上次執行結果」（docs/FEEDBACK-7-PLAN.md）：站台重啟後 lastRunEndedAt 為 null，
 * 顯示「—」即可——完整歷史查執行總表，這裡只回答「剛剛那次到底成不成功」。
 * 使用者手動取消不算失敗（AnalysisOrchestrator 把取消也回報成 Success=false，
 * FailureMessage="使用者取消"），這裡特判成中性語氣，不用紅字嚇人。
 */
function renderLastRunOutcome(messageEl, status) {
    if (!status.lastRunEndedAt) {
        messageEl.textContent = '';
        messageEl.classList.remove('text-danger');
        messageEl.classList.add('text-muted');
        return;
    }

    const when = formatDateTime(status.lastRunEndedAt);
    if (status.lastRunSuccess) {
        messageEl.textContent = `上次執行：成功（${status.lastRunTriggerText}，${when}）`;
        messageEl.classList.remove('text-danger');
        messageEl.classList.add('text-muted');
    } else if (status.lastRunMessage === '使用者取消') {
        messageEl.textContent = `上次執行：已停止（${status.lastRunTriggerText}，${when}）`;
        messageEl.classList.remove('text-danger');
        messageEl.classList.add('text-muted');
    } else {
        messageEl.textContent = `上次執行：失敗（${status.lastRunTriggerText}，${when}）— ${status.lastRunMessage ?? ''}`;
        messageEl.classList.remove('text-muted');
        messageEl.classList.add('text-danger');
    }
}

document.getElementById('schedule-stop')?.addEventListener('click', async () => {
    const confirmed = await confirmAction({
        title: '停止執行',
        message: '將優雅停止目前進行中的分析（停在主機日邊界，已完成的部分保留，不會產生殘缺紀錄）。要繼續嗎？',
        confirmText: '停止',
        confirmVariant: 'danger'
    });
    if (!confirmed) return;

    try {
        await api.post('/api/admin/schedule/cancel', {});
        toast('已送出停止要求', 'success');
        await refreshScheduleStatus();
    } catch {
        // 錯誤已由 api.js 顯示
    }
});

// ── 立即執行（docs/WEB-SCHEDULER-PLAN.md §1.4.4，「host」範圍另放在主機詳情頁）──────

let runNowPreviewDebounce = null;

document.getElementById('schedule-run-now')?.addEventListener('click', () => {
    document.getElementById('run-now-form').reset();
    document.getElementById('run-now-scope-all').checked = true;
    document.getElementById('run-now-segment-field').classList.add('d-none');
    updateRunNowPreview();
    runNowModal.show();
});

for (const radio of document.querySelectorAll('input[name="run-now-scope"]')) {
    radio.addEventListener('change', () => {
        document.getElementById('run-now-segment-field').classList.toggle('d-none', radio.value !== 'segment');
        updateRunNowPreview();
    });
}

document.getElementById('run-now-segment').addEventListener('input', () => {
    clearTimeout(runNowPreviewDebounce);
    runNowPreviewDebounce = setTimeout(updateRunNowPreview, 400);
});

async function updateRunNowPreview() {
    const scope = document.querySelector('input[name="run-now-scope"]:checked').value;
    const segment = document.getElementById('run-now-segment').value.trim();
    const previewEl = document.getElementById('run-now-preview');

    if (scope === 'segment' && !segment) {
        previewEl.textContent = '請輸入要執行的網段（如 192.168.0 或 192.168.0.0/24）。';
        previewEl.className = 'alert alert-secondary py-2 px-3 mb-0';
        return;
    }

    const params = new URLSearchParams({ scope });
    if (scope === 'segment') params.set('segment', segment);

    try {
        const result = await api.get(`/api/admin/schedule/run-preview?${params}`, { silent: true });
        const heavy = result.hostCount >= 50;
        previewEl.textContent = heavy
            ? `目前有 ${result.hostCount} 台主機符合條件，數量較多——請留意白天對 Sentinel 的查詢負載，` +
              '必要時改用網段範圍縮小。'
            : `目前有 ${result.hostCount} 台主機符合條件。`;
        previewEl.className = heavy ? 'alert alert-warning py-2 px-3 mb-0' : 'alert alert-secondary py-2 px-3 mb-0';
    } catch (err) {
        previewEl.textContent = err?.message || '無法預覽，請確認網段格式。';
        previewEl.className = 'alert alert-danger py-2 px-3 mb-0';
    }
}

document.getElementById('run-now-form').addEventListener('submit', async event => {
    event.preventDefault();

    const scope = document.querySelector('input[name="run-now-scope"]:checked').value;
    const segment = document.getElementById('run-now-segment').value.trim();
    const backfillDays = document.getElementById('run-now-backfill').value;

    const submitButton = document.getElementById('run-now-submit');
    const restore = withBusy(submitButton, '送出中');
    try {
        const result = await api.post('/api/admin/schedule/run', {
            scope,
            segment: scope === 'segment' ? segment : null,
            backfillDays: backfillDays ? Number(backfillDays) : null
        });
        toast(result.message, result.started ? 'success' : 'warning');
        if (result.started) {
            runNowModal.hide();
            await refreshScheduleStatus();
        }
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

loadSchedule();
load();
