/**
 * 執行監控（docs/WEB-SPEC.md §9.10）——dev 每天早上的第一個畫面。
 *
 * 「昨晚哪幾台沒跑」是這頁存在的理由：批次沒跑就不會有任何風險紀錄，
 * 而「沒有紀錄」看起來跟「一切正常」一模一樣。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import {
    renderTable, renderLoading, renderEmpty, labelValue, renderPagination, sortRows, loadPageSize, savePageSize,
    toast, withBusy, confirmAction, showDetailModal, guardLoad, bindTabs
} from '../core/ui.js';
import { formatDateTime, formatNumber, formatUserName } from '../core/format.js';

/* 色值對齊 site.css 語意 token（§8.2 原則 3：同一語意全站同色）——圖例色塊/狀態字
   是 style 直塗、吃不到 CSS class，故以字面值對齊：success=--lf-success、
   warning=--lf-warning、failed=--lf-danger、stopped=--lf-severity-high、
   running=--lf-primary、stuck=--lf-cat-hardware、none=--lf-gray-200 */
const STATUS_META = {
    success: { label: '成功', color: '#16a34a' },
    // 已回補（§3）：當天沒有 BatchRun，但事後回補的分析紀錄補上了這天——與「當天真的有跑」
    // 分色（淺綠），符合本頁「沒跑不能冒充有跑」的誠實原則
    backfilled: { label: '已回補', color: '#65a30d' },
    warning: { label: '有警告', color: '#d97706' },
    failed: { label: '失敗', color: '#dc2626' },
    stopped: { label: '已停止', color: '#ea580c' },   // 優雅停止（手動或窗口結束）——不是失敗
    running: { label: '執行中', color: '#1e40af' },
    stuck: { label: '異常中斷', color: '#7c3aed' },
    none: { label: '未執行', color: '#e2e8f0' },
    // 本機分析已停用（批次D）：設定行為不是漏跑，與「未執行」分開顯示（--lf-gray-400）
    local_disabled: { label: '本機分析已停用', color: '#94a3b8' }
};

let currentDays = 14;

// 異常彙總（本地排序，筆數通常不多，不加分頁）
let currentErrors = [];
let errorsSort = { key: 'count', dir: 'desc' };

async function load() {
    renderLoading(document.getElementById('run-summary'), 4);
    renderLoading(document.getElementById('run-errors'), 3);
    renderLoading(document.getElementById('run-list'), 4);

    const [summaries, errors, runList] = await Promise.all([
        api.get(`/api/runs/summary?days=${currentDays}`),
        api.get(`/api/runs/errors?days=${currentDays}`),
        api.get(`/api/runs/list?days=${currentDays}`)
    ]);

    renderLegend();
    renderSummary(summaries);
    renderErrors(errors);
    renderRunList(runList);
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
            { title: '日期', render: s => s.date },
            { title: '成功', className: 'text-end', render: s => countCell(s.successCount, 'success') },
            { title: '已回補', className: 'text-end', render: s => countCell(s.backfilledCount, 'backfilled') },
            { title: '有警告', className: 'text-end', render: s => countCell(s.warningCount, 'warning') },
            { title: '失敗', className: 'text-end', render: s => countCell(s.failedCount, 'failed') },
            { title: '已停止', className: 'text-end', render: s => countCell(s.stoppedCount, 'stopped') },
            { title: '異常中斷', className: 'text-end', render: s => countCell(s.stuckCount, 'stuck') },
            { title: '執行中', className: 'text-end', render: s => countCell(s.runningCount, 'running') },
            { title: '未執行', className: 'text-end', render: s => countCell(s.notRunCount, 'none') },
            { title: '本機停用', className: 'text-end', render: s => countCell(s.localDisabledCount, 'local_disabled') },
            { title: '失敗主機', render: s => failedHostsCell(s) }
        ],
        rows: [...summaries].reverse(),   // 最新日期在最上面，跟其他頁的時間排序習慣一致
        // 點日期就地展開該天每台主機的狀態（§2）：懶載入，展開才 fetch，各列狀態獨立
        onRowExpand: (summary, cell) => renderDayDetailInto(cell, summary.date),
        empty: { title: '尚無執行紀錄' }
    });
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

/**
 * 日期列就地展開（§2）：懶載入該天每台主機的狀態，render 進展開列。
 * 每個日期各持有獨立的排序/分頁狀態（允許同時展開多天，互不干擾）——
 * 2000 台規模下曾整表一次 render，這裡沿用本地排序＋分頁（資料一次取回，不重打 API）。
 */
async function renderDayDetailInto(cell, date) {
    cell.replaceChildren();
    const listEl = document.createElement('div');
    const pagerEl = document.createElement('nav');
    pagerEl.className = 'mt-2';
    cell.append(listEl, pagerEl);

    renderLoading(listEl, 6);

    let hosts;
    try {
        hosts = await api.get(`/api/runs/day/${date}`);
    } catch {
        renderEmpty(listEl, { title: '載入當日明細失敗' });
        return;
    }

    const state = { sort: { key: 'hostName', dir: 'asc' }, page: 1, pageSize: loadPageSize('runs-day-detail') };

    function render() {
        const sorted = sortRows(hosts, DAY_DETAIL_COLUMNS, state.sort);
        const totalPages = Math.max(1, Math.ceil(sorted.length / state.pageSize));
        if (state.page > totalPages) state.page = totalPages;
        const pageRows = sorted.slice((state.page - 1) * state.pageSize, state.page * state.pageSize);

        renderTable(listEl, {
            columns: DAY_DETAIL_COLUMNS,
            rows: pageRows,
            sort: state.sort,
            onSort: (key, dir) => { state.sort = { key, dir }; state.page = 1; render(); },
            empty: { title: '這天沒有任何主機資料' }
        });

        renderPagination(pagerEl, {
            page: state.page,
            totalPages: hosts.length ? totalPages : 0,
            onPage: page => { state.page = page; render(); },
            pageSize: state.pageSize,
            onPageSize: size => {
                state.pageSize = size;
                savePageSize('runs-day-detail', size);
                state.page = 1;
                render();
            }
        });
    }

    render();
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

// ── 執行紀錄（回饋十七輪批次F-3）：每一筆 BatchRun 的扁平清單，不像執行總表按日期彙總 ──

let currentRunList = [];
let runListSort = { key: 'startedAt', dir: 'desc' };

const RUN_LIST_COLUMNS = [
    { title: '主機', sortKey: 'hostName', sortValue: r => r.hostName, render: r => r.hostName },
    { title: '狀態', sortKey: 'status', sortValue: r => r.status, render: r => statusBadgeCell(r.status) },
    { title: '開始時間', sortKey: 'startedAt', sortDefaultDir: 'desc', sortValue: r => r.startedAt, render: r => formatDateTime(r.startedAt) },
    {
        title: '耗時', className: 'text-end', sortKey: 'durationSeconds', sortDefaultDir: 'desc',
        sortValue: r => r.durationSeconds ?? -1,
        render: r => r.durationSeconds != null ? formatDuration(r.durationSeconds) : '進行中'
    },
    { title: '觸發來源', sortKey: 'triggerText', sortValue: r => r.triggerText, render: r => r.triggerText },
    { title: '分析天數', className: 'text-end', sortKey: 'daysAnalyzed', sortDefaultDir: 'desc', sortValue: r => r.daysAnalyzed, render: r => String(r.daysAnalyzed) },
    {
        title: '警告 / 錯誤', className: 'text-end', sortKey: 'errorCount', sortDefaultDir: 'desc',
        sortValue: r => r.errorCount, render: r => `${r.warnCount} / ${r.errorCount}`
    },
    { title: '', className: 'text-end', render: r => viewRunButton(r.runId) }
];

function renderRunList(runs) {
    currentRunList = runs;
    renderRunListTable();
}

function renderRunListTable() {
    renderTable(document.getElementById('run-list'), {
        columns: RUN_LIST_COLUMNS,
        rows: sortRows(currentRunList, RUN_LIST_COLUMNS, runListSort),
        sort: runListSort,
        onSort: (key, dir) => {
            runListSort = { key, dir };
            renderRunListTable();
        },
        empty: { title: '此期間沒有執行紀錄' }
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

// ── 執行詳情（改 modal，§2：取代舊版跳到頁面最下方的 run-detail-card）──────────────

async function showDetail(runId) {
    const body = document.createElement('div');
    renderLoading(body, 5);
    showDetailModal({ title: '執行詳情', body, size: 'modal-xl' });

    let detail;
    try {
        detail = await api.get(`/api/runs/${runId}`);
    } catch {
        body.replaceChildren();
        renderEmpty(body, { title: '載入執行詳情失敗' });
        return;
    }

    body.replaceChildren();

    // 主機＋起始時間放在 body 標頭（modal 標題已固定「執行詳情」）
    const heading = document.createElement('div');
    heading.className = 'fw-semibold mb-3';
    heading.textContent = `${detail.hostName}　${formatDateTime(detail.startedAt)}`;
    body.appendChild(heading);

    const statsRow = document.createElement('div');
    statsRow.className = 'row g-3 mb-3';
    renderStats(statsRow, detail);
    body.appendChild(statsRow);

    // 等級過濾（原本在 cshtml 的 log-level-filter，改建在 modal 內）
    const filterWrap = document.createElement('div');
    filterWrap.className = 'd-flex justify-content-end mb-2';
    const levelSelect = document.createElement('select');
    levelSelect.className = 'form-select form-select-sm';
    levelSelect.style.width = '130px';
    for (const [value, text] of [['', '全部等級'], ['Warn', 'Warn 以上'], ['Error', 'Error 以上']]) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = text;
        levelSelect.appendChild(option);
    }
    filterWrap.appendChild(levelSelect);
    body.appendChild(filterWrap);

    const logsEl = document.createElement('div');
    body.appendChild(logsEl);

    function renderLogs() {
        const level = levelSelect.value;
        const order = { Info: 0, Warn: 1, Error: 2, Fatal: 3 };
        const filtered = level
            ? detail.logs.filter(l => (order[l.level] ?? 0) >= (order[level] ?? 0))
            : detail.logs;

        renderTable(logsEl, {
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

    levelSelect.addEventListener('change', renderLogs);
    renderLogs();
}

function renderStats(container, detail) {
    const stats = [
        { label: '狀態', value: statusText(detail) },
        { label: '耗時', value: detail.durationSeconds != null ? formatDuration(detail.durationSeconds) : '—' },
        { label: '分析天數', value: detail.daysAnalyzed },
        // AI 未設定時這欄多半是「0（失敗 0）」的雜訊；歷史上真的呼叫過 AI 的執行紀錄仍如實顯示
        // （docs/archive/FEEDBACK-7-PLAN.md）
        ...(aiAvailable || detail.aiCalls > 0
            ? [{ label: 'AI 呼叫', value: `${detail.aiCalls}（失敗 ${detail.aiFailures}）` }]
            : []),
        { label: '警告 / 錯誤', value: `${detail.warnCount} / ${detail.errorCount}` },
        { label: '版本', value: detail.appVersion }
    ];

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

for (const button of document.querySelectorAll('[data-days]')) {
    button.addEventListener('click', () => {
        currentDays = Number(button.dataset.days);
        for (const other of document.querySelectorAll('[data-days]')) {
            other.classList.toggle('active', other === button);
        }
        load();
    });
}

// ── 排程設定（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.5）──────────────────────────────
// 這頁的權限是 DevMonitor 或 Maintain 任一（見 PagesController.Runs）：dev 進得來但只能看，
// 排程設定/立即執行/停止這些會動到系統的操作只給 Maintain——data-maintain-only 標記的元素
// 在沒有 Maintain 時整批隱藏，而不是逐一判斷。

let scheduleWindows = [];
let canMaintainSchedule = false;
// AI 未設定時「AI 診斷傾印」開關與徽章整組隱藏（docs/archive/FEEDBACK-7-PLAN.md）：這是除錯用的手動
// 開關，本身不預設開啟，但 AI 沒設定時這個功能沒有意義，不該佔畫面。開關值照常載入/回傳，
// 只是不顯示——避免隱藏期間存檔把設定意外歸零。
let aiAvailable = false;
// 分析本機主機開關（回饋十八輪批次D）：影響「立即執行」modal 的「全部主機」描述文字。
let localAnalysisEnabled = true;
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
    document.getElementById('schedule-local-analysis').checked = options.localAnalysisEnabled;
    localAnalysisEnabled = options.localAnalysisEnabled;
    const scopeAllLabel = document.getElementById('run-now-scope-all-label');
    if (scopeAllLabel) {
        scopeAllLabel.textContent = localAnalysisEnabled
            ? '全部主機（等同排程觸發的完整執行）'
            : '全部主機（不含本機——本機分析已停用，等同排程觸發的完整執行）';
    }
    renderScheduleWindows();

    document.getElementById('schedule-updated').textContent = options.updatedAt
        ? `最後更新：${formatDateTime(options.updatedAt)}${options.updatedByAccount ? `（${formatUserName(options.updatedByDisplayName, options.updatedByAccount)}）` : ''}`
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
            debugDump: document.getElementById('schedule-debug-dump').checked,
            localAnalysisEnabled: document.getElementById('schedule-local-analysis').checked
        });
        applyScheduleOptions(saved);
        toast('已儲存排程設定', 'success');
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

// 執行中／閒置輪詢間隔不同（docs/archive/FEEDBACK-8-PLAN.md #2）：執行中要讓進度條「看得出在動」，
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

/**
 * 開始時間／已耗時（回饋十七輪批次F-2）：後端 status.startedAt 早就有了，只是畫面上原本
 * 只顯示「執行中」，看不出何時開始、跑了多久。每秒本地計時（不是等輪詢間隔才更新，執行中
 * 輪詢是 3 秒一次，數字跳動會不夠即時），輪詢回來時用 status.startedAt 重設一次校正飄移
 * （分頁背景分頁、系統睡眠都可能讓 setInterval 累積誤差）。
 */
let elapsedTimer = null;

function startElapsedTicker(startedAt) {
    const start = new Date(startedAt).getTime();
    const el = document.getElementById('schedule-elapsed');
    el.classList.remove('d-none');

    clearInterval(elapsedTimer);
    function tick() {
        const seconds = Math.max(0, Math.floor((Date.now() - start) / 1000));
        el.textContent = `開始於 ${formatDateTime(startedAt)}・已耗時 ${formatDuration(seconds)}`;
    }
    tick();
    elapsedTimer = setInterval(tick, 1000);
}

function stopElapsedTicker() {
    clearInterval(elapsedTimer);
    elapsedTimer = null;
    document.getElementById('schedule-elapsed').classList.add('d-none');
}

function applyScheduleStatus(status) {
    document.getElementById('schedule-status-text').textContent = status.isRunning ? '執行中' : '閒置';
    document.getElementById('schedule-run-state').textContent = status.isRunning
        ? `執行中（${status.triggerText}）`
        : '閒置';

    if (status.isRunning && status.startedAt) {
        startElapsedTicker(status.startedAt);
    } else {
        stopElapsedTicker();
    }

    renderScheduleProgress(status);

    const messageEl = document.getElementById('schedule-latest-message');
    if (status.isRunning) {
        messageEl.textContent = status.latestMessage ?? '';
        messageEl.classList.remove('text-danger');
        messageEl.classList.add('text-muted');
    } else {
        // 閒置時顯示「上次執行到底成不成功」（docs/archive/FEEDBACK-7-PLAN.md）：原本失敗只寫 log，
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

    // 執行完自動刷新總表（docs/archive/FEEDBACK-8-PLAN.md #2）：isRunning 由 true → false 時，
    // 使用者不必手動重新整理才看得到剛跑完的這趟（手動與排程觸發都經過這條輪詢，天然涵蓋兩者）
    if (wasScheduleRunning && !status.isRunning) {
        toast('執行已結束，執行總表已刷新', 'info');
        load();
    }
    wasScheduleRunning = status.isRunning;
}

// netiq-ai（docs/archive/FEEDBACK-12-PLAN.md §3.7）：搜尋全部完成、AI 佇列還在背景消化白話摘要的
// 第二階段——統計與紀錄早就寫入了，這裡純粹是白話摘要/風險再判定的補寫進度，用詞刻意
// 跟「NetIQ 機房分析」（還在查詢中）區分開，避免使用者以為又要重新查一次
// netiq-backpressure（回饋十三輪 A7）：AI 待處理佇列滿載（AiFollowupQueue.Capacity=200）時，
// 搜尋主線在 NetiqPipelineService 背壓等待——外觀上與「卡住」無法區分，必須獨立標示原因，
// 否則使用者回報的症狀就是「進度條不動了」。done/total 沿用 netiq 階段同一組主機日數字
// （暫停當下已完成的量不會消失），只是暫時不再往前走。
const PROGRESS_PHASE_LABEL = {
    local: '本機分析', netiq: 'NetIQ 機房分析', 'netiq-ai': 'AI 白話分析補寫中',
    'netiq-backpressure': '搜尋暫停：AI 佇列已滿，等待消化中'
};
const PROGRESS_PHASE_UNIT = { 'netiq-ai': '件' };

/** 執行中且有量化進度（total>0）畫百分比進度條＋「階段　x / y 主機日」文字（數字自己會說話，
 * 不只給百分比）；剛啟動／清理階段（total=0）畫不定進度；閒置時整組隱藏。
 * （docs/archive/FEEDBACK-8-PLAN.md #2）
 *
 * 子進度（回饋十四輪 UI-6）：netiq-ai／netiq-backpressure 這條 AI 背景消化軌與主進度
 * （netiq，搜尋仍在往下一台主機推進）可能同時在跑——兩者原本共用一組欄位，後回報的會
 * 直接蓋掉先回報的，畫面症狀是「進度卡住不動」。SchedulerRunState 已把兩者分成獨立欄位，
 * 這裡只需各自畫一條：主進度在上（沿用既有邏輯不變），子進度在下、只在有值時才顯示。
 *
 * 本機進度（回饋十七輪批次E）：本機與 NetIQ 改並行執行後，本機也是獨立一條軌，畫在主進度
 * 之上。與子進度同樣採「有值才顯示」——不像主進度（NetIQ）執行中就無條件顯示「準備中」，
 * 因為 NetiqHosts 範圍（僅指定 NetIQ 主機）時本機根本不會執行，不該一直顯示一條空的
 * 「本機分析 準備中」軌。 */
function renderScheduleProgress(status) {
    const localWrap = document.getElementById('schedule-local-progress-wrap');
    const localBar = document.getElementById('schedule-local-progress-bar');
    const localText = document.getElementById('schedule-local-progress-text');
    const wrap = document.getElementById('schedule-progress-wrap');
    const bar = document.getElementById('schedule-progress-bar');
    const text = document.getElementById('schedule-progress-text');
    const subWrap = document.getElementById('schedule-subprogress-wrap');
    const subBar = document.getElementById('schedule-subprogress-bar');
    const subText = document.getElementById('schedule-subprogress-text');

    if (!status.isRunning) {
        localWrap.classList.add('d-none');
        localText.classList.add('d-none');
        wrap.classList.add('d-none');
        text.classList.add('d-none');
        subWrap.classList.add('d-none');
        subText.classList.add('d-none');
        return;
    }

    if (!status.localProgressPhase) {
        localWrap.classList.add('d-none');
        localText.classList.add('d-none');
    } else {
        localWrap.classList.remove('d-none');
        localText.classList.remove('d-none');

        if (status.localProgressTotal > 0) {
            const localPct = Math.min(100, Math.round((status.localProgressDone / status.localProgressTotal) * 100));
            const localUnit = PROGRESS_PHASE_UNIT[status.localProgressPhase] ?? '主機日';
            const localLabel = `${PROGRESS_PHASE_LABEL[status.localProgressPhase] ?? status.localProgressPhase}　` +
                               `${status.localProgressDone} / ${status.localProgressTotal} ${localUnit}`;
            localBar.classList.remove('progress-bar-striped', 'progress-bar-animated');
            localBar.style.width = `${localPct}%`;
            localBar.title = localLabel;
            localText.textContent = localLabel;
        } else {
            localBar.classList.add('progress-bar-striped', 'progress-bar-animated');
            localBar.style.width = '100%';
            localBar.title = '準備中…';
            localText.textContent = '準備中…';
        }
    }

    // 主進度（NetIQ）：回饋十七輪批次E 前是「執行中就無條件顯示，total=0 畫不定進度」——那時
    // 本機與 NetIQ 依序執行，這條「準備中」占位最多只會出現在本機剛跑完、NetIQ 還沒回報第一次
    // 進度的短暫空檔。並行後兩者同時開跑，若這次執行根本沒有 NetIQ 主機（NetiqPipelineService
    // 一開始就直接 return，永遠不會呼叫 Report("netiq",...)），這個「準備中」會卡在畫面上長達
    // 整段本機執行時間，變成誤導（瀏覽器實測跑一次純本機執行時抓到）。改成與本機／子進度同一套
    // 「有回報過才顯示」：沒有 NetIQ 工作時這條軌乾脆不出現，比一直閃著假的「準備中」誠實。
    if (!status.progressPhase) {
        wrap.classList.add('d-none');
        text.classList.add('d-none');
    } else {
        wrap.classList.remove('d-none');
        text.classList.remove('d-none');

        if (status.progressTotal > 0) {
            const pct = Math.min(100, Math.round((status.progressDone / status.progressTotal) * 100));
            const unit = PROGRESS_PHASE_UNIT[status.progressPhase] ?? '主機日';
            const label = `${PROGRESS_PHASE_LABEL[status.progressPhase] ?? status.progressPhase}　${status.progressDone} / ${status.progressTotal} ${unit}`;
            bar.classList.remove('progress-bar-striped', 'progress-bar-animated');
            bar.style.width = `${pct}%`;
            bar.title = label;
            text.textContent = label;
        } else {
            bar.classList.add('progress-bar-striped', 'progress-bar-animated');
            bar.style.width = '100%';
            bar.title = '準備中…';
            text.textContent = '準備中…';
        }
    }

    // 子進度：沒有回報過（subProgressPhase 為 null，如純本機執行、或 NetIQ 還沒進到 AI 消化階段）
    // 就整組隱藏，不畫一條空的第二軌出來
    if (!status.subProgressPhase) {
        subWrap.classList.add('d-none');
        subText.classList.add('d-none');
        return;
    }
    subWrap.classList.remove('d-none');
    subText.classList.remove('d-none');

    if (status.subProgressTotal > 0) {
        const subPct = Math.min(100, Math.round((status.subProgressDone / status.subProgressTotal) * 100));
        const subUnit = PROGRESS_PHASE_UNIT[status.subProgressPhase] ?? '主機日';
        const subLabel = `${PROGRESS_PHASE_LABEL[status.subProgressPhase] ?? status.subProgressPhase}　` +
                         `${status.subProgressDone} / ${status.subProgressTotal} ${subUnit}`;
        subBar.classList.remove('progress-bar-striped', 'progress-bar-animated');
        subBar.style.width = `${subPct}%`;
        subBar.title = subLabel;
        subText.textContent = subLabel;
    } else {
        subBar.classList.add('progress-bar-striped', 'progress-bar-animated');
        subBar.style.width = '100%';
        subBar.title = '準備中…';
        subText.textContent = '準備中…';
    }
}

/**
 * 閒置時的「上次執行結果」（docs/archive/FEEDBACK-7-PLAN.md）：站台重啟後 lastRunEndedAt 為 null，
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

// ── 立即執行（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4，「host」範圍另放在主機詳情頁）──────

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

document.getElementById('run-now-backfill')?.addEventListener('input', () => {
    clearTimeout(runNowPreviewDebounce);
    runNowPreviewDebounce = setTimeout(updateRunNowPreview, 400);
});

document.getElementById('run-now-only-missing')?.addEventListener('change', updateRunNowPreview);

async function updateRunNowPreview() {
    const scope = document.querySelector('input[name="run-now-scope"]:checked').value;
    const segment = document.getElementById('run-now-segment').value.trim();
    const onlyMissingOrFailed = document.getElementById('run-now-only-missing')?.checked || false;
    const backfillDays = document.getElementById('run-now-backfill')?.value;
    const previewEl = document.getElementById('run-now-preview');

    if (scope === 'segment' && !segment) {
        previewEl.textContent = '請輸入要執行的網段（如 192.168.0 或 192.168.0.0/24）。';
        previewEl.className = 'alert alert-secondary py-2 px-3 mb-0';
        return;
    }

    const params = new URLSearchParams({ scope });
    if (scope === 'segment') params.set('segment', segment);
    if (onlyMissingOrFailed) params.set('onlyMissingOrFailed', 'true');
    if (backfillDays) params.set('backfillDays', backfillDays);

    try {
        const result = await api.get(`/api/admin/schedule/run-preview?${params}`, { silent: true });
        const heavy = result.hostCount >= 50;
        const suffix = onlyMissingOrFailed ? '待補跑或未執行。' : '符合條件。';
        const text = heavy
            ? `目前有 ${result.hostCount} 台主機${suffix.replace('。', '，')}數量較多——請留意白天對 Sentinel 的查詢負載，` +
              '必要時改用網段範圍縮小。'
            : `目前有 ${result.hostCount} 台主機${suffix}`;
        previewEl.textContent = text;
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
    const onlyMissingOrFailed = document.getElementById('run-now-only-missing')?.checked || false;

    const submitButton = document.getElementById('run-now-submit');
    const restore = withBusy(submitButton, '送出中');
    try {
        const result = await api.post('/api/admin/schedule/run', {
            scope,
            segment: scope === 'segment' ? segment : null,
            backfillDays: backfillDays ? Number(backfillDays) : null,
            onlyMissingOrFailed
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

// 三頁籤（回饋十七輪批次F-1）共用同一份天數篩選、同一次 load() 取回的資料——頁籤切換
// 只是顯示/隱藏面板，不重新打 API（三個面板的資料本來就一起抓，見 load()）
bindTabs(document.getElementById('runs-tabs'));

loadSchedule();
guardLoad([
    document.getElementById('run-summary'),
    document.getElementById('run-errors'),
    document.getElementById('run-list')
], load);
