/**
 * 執行監控（docs/WEB-SPEC.md §9.10）——dev 每天早上的第一個畫面。
 *
 * 「昨晚哪幾台沒跑」是這頁存在的理由：批次沒跑就不會有任何風險紀錄，
 * 而「沒有紀錄」看起來跟「一切正常」一模一樣。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty } from '../core/ui.js';
import { formatDateTime, formatNumber } from '../core/format.js';

const STATUS_META = {
    success: { label: '成功', color: '#198754' },
    warning: { label: '有警告', color: '#ffc107' },
    failed: { label: '失敗', color: '#dc3545' },
    running: { label: '執行中', color: '#0d6efd' },
    stuck: { label: '異常中斷', color: '#6f42c1' },
    none: { label: '未執行', color: '#e9ecef' }
};

let currentDays = 14;
let currentLogs = [];

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
            hint: '批次程式執行後會自動登記；請確認 LogForesight.exe 的排程已設定。'
        });
        return;
    }

    renderTable(container, {
        columns: [
            { title: '日期', render: s => dateLink(s.date) },
            { title: '成功', className: 'text-end', render: s => countCell(s.successCount, 'success') },
            { title: '有警告', className: 'text-end', render: s => countCell(s.warningCount, 'warning') },
            { title: '失敗', className: 'text-end', render: s => countCell(s.failedCount, 'failed') },
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

/** 點日期下鑽：該天每台主機的狀態，取代舊版矩陣的「一格看一次執行」 */
async function showDayDetail(date) {
    const card = document.getElementById('run-day-detail-card');
    card.classList.remove('d-none');
    card.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.getElementById('run-day-detail-title').textContent = `${date} 各主機狀態`;

    renderLoading(document.getElementById('run-day-detail-list'), 6);
    const hosts = await api.get(`/api/runs/day/${date}`);

    renderTable(document.getElementById('run-day-detail-list'), {
        columns: [
            { title: '主機', render: h => h.hostName },
            { title: '狀態', render: h => statusBadgeCell(h.status) },
            { title: '分析天數', className: 'text-end', render: h => h.runId != null ? String(h.daysAnalyzed) : '' },
            { title: '警告 / 錯誤', className: 'text-end', render: h => h.runId != null ? `${h.warnCount} / ${h.errorCount}` : '' },
            { title: '', className: 'text-end', render: h => h.runId != null ? viewRunButton(h.runId) : '' }
        ],
        rows: hosts,
        empty: { title: '這天沒有任何主機資料' }
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
    button.textContent = '查看執行';
    button.addEventListener('click', () => showDetail(runId));
    return button;
}

document.getElementById('run-day-detail-close').addEventListener('click', () => {
    document.getElementById('run-day-detail-card').classList.add('d-none');
});

function renderErrors(errors) {
    renderTable(document.getElementById('run-errors'), {
        columns: [
            { title: '等級', render: e => levelBadge(e.level) },
            { title: '訊息', render: e => messageCell(e) },
            { title: '次數', className: 'text-end', render: e => formatNumber(e.count) },
            { title: '影響主機', render: e => e.affectedHosts.join('、') },
            { title: '最近發生', render: e => formatDateTime(e.lastSeen) },
            { title: '', className: 'text-end', render: e => detailButton(e.latestRunId) }
        ],
        rows: errors,
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
    button.textContent = '查看執行';
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
        { label: 'AI 呼叫', value: `${detail.aiCalls}（失敗 ${detail.aiFailures}）` },
        { label: '警告 / 錯誤', value: `${detail.warnCount} / ${detail.errorCount}` },
        { label: '版本', value: detail.appVersion }
    ];

    const container = document.getElementById('run-detail-stats');
    container.replaceChildren();

    for (const stat of stats) {
        const col = document.createElement('div');
        col.className = 'col-6 col-md-2';

        const label = document.createElement('div');
        label.className = 'lf-stat__label';
        label.textContent = stat.label;

        const value = document.createElement('div');
        value.className = 'fw-semibold';
        value.textContent = String(stat.value);

        col.append(label, value);
        container.appendChild(col);
    }
}

function statusText(detail) {
    if (detail.finishedAt == null) return '未回報結束';
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

load();
