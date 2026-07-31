/**
 * 主機詳情／風險時間軸（docs/WEB-SPEC.md §9.4）。
 *
 * 時間軸的每一格都可點擊進入該日詳情——這是 §8.4 下鑽規則的另一個入口。
 * 沒有紀錄的日子刻意用不同顏色：「這天沒分析」與「這天沒風險」是完全不同的意思。
 */

import { api } from '../core/api.js';
import { renderLoading, renderTable, labelValue } from '../core/ui.js';
import { formatDateTime, formatNumber, severityBadge, riskBadge, CATEGORY_NAMES } from '../core/format.js';

const root = document.getElementById('host-detail');
const hostId = Number(root.dataset.hostId);
let currentDays = 30;

const LEGEND = [
    { key: 'high', label: '高風險', color: 'var(--lf-risk-high)' },
    { key: 'mid', label: '中風險', color: 'var(--lf-risk-mid)' },
    { key: 'low', label: '低風險', color: '#c8e6c9' },
    { key: 'gap', label: '涵蓋不完整', color: '#ffe0b2' },
    { key: 'none', label: '無分析紀錄', color: '#e9ecef' }
];

async function load() {
    renderLoading(document.getElementById('host-timeline'), 2);
    renderLoading(document.getElementById('host-issues'), 3);

    const detail = await api.get(`/api/host-detail/${hostId}?days=${currentDays}`);

    renderHeader(detail);
    renderTimeline(detail);
    renderIssues(detail);
    renderCheckup(detail);
}

function renderHeader(detail) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold mb-1';
    // NetIQ 主機以 IP 登錄，光看 hostName 認不出是哪台機器——有 Sentinel 回報的顯示名就一併帶出
    title.textContent = detail.displayName ? `${detail.hostName}（${detail.displayName}）` : detail.hostName;
    body.appendChild(title);

    if (detail.roleDesc) {
        const role = document.createElement('div');
        role.className = 'text-muted mb-3';
        role.textContent = detail.roleDesc;
        body.appendChild(role);
    }

    const grid = document.createElement('div');
    grid.className = 'row g-3 small';

    const fields = [
        ['IP 位址', detail.ipAddress || '未設定'],
        ['作業系統', detail.os === 'linux' ? 'Linux' : 'Windows'],
        ['所屬 Sentinel', detail.netiqServer || '本機直讀'],
        ['主機群組', detail.groupNames.length ? detail.groupNames.join('、') : '未分群'],
        ['負責人', detail.ownerNames.length ? detail.ownerNames.join('、') : '未指定'],
        ['最近回報', detail.lastReportAt ? formatDateTime(detail.lastReportAt) : '尚未回報']
    ];

    for (const [label, value] of fields) {
        const col = document.createElement('div');
        col.className = 'col-6 col-md-4 col-lg-2';
        col.append(...labelValue(label, value));
        grid.appendChild(col);
    }

    body.appendChild(grid);
    card.appendChild(body);
    document.getElementById('host-header').replaceChildren(card);
}

function renderTimeline(detail) {
    const container = document.getElementById('host-timeline');

    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap gap-1';

    for (const day of detail.timeline) {
        const cell = document.createElement(day.hasRecord ? 'a' : 'div');
        cell.className = 'rounded lf-timeline-cell';
        cell.dataset.date = day.date;   // 問題發生明細展開時的高亮連動用（docs/FEEDBACK-4-PLAN.md §3）
        cell.style.width = '22px';
        cell.style.height = '22px';
        cell.style.background = cellColor(day);
        cell.style.display = 'inline-block';

        if (day.hasRecord) {
            cell.href = `/records/${hostId}/${day.date}`;
            cell.title = `${day.date}｜${day.riskLevel}風險${day.headline ? '｜' + day.headline : ''}`;
        } else {
            // 這天沒有分析紀錄——可能是排程沒跑、機器關機，不是「沒問題」
            cell.title = `${day.date}｜無分析紀錄`;
        }

        wrap.appendChild(cell);
    }

    container.replaceChildren(wrap);
    renderLegend();
}

function cellColor(day) {
    if (!day.hasRecord) return '#e9ecef';
    if (day.riskLevel === '高') return 'var(--lf-risk-high)';
    if (day.riskLevel === '中') return 'var(--lf-risk-mid)';
    if (day.hasCoverageGap) return '#ffe0b2';
    return '#c8e6c9';
}

function renderLegend() {
    const legend = document.getElementById('timeline-legend');
    legend.replaceChildren();

    for (const item of LEGEND) {
        const wrap = document.createElement('span');
        wrap.className = 'd-flex align-items-center gap-1';

        const swatch = document.createElement('span');
        swatch.className = 'rounded d-inline-block';
        swatch.style.width = '12px';
        swatch.style.height = '12px';
        swatch.style.background = item.color;

        const label = document.createElement('span');
        label.textContent = item.label;

        wrap.append(swatch, label);
        legend.appendChild(wrap);
    }
}

/**
 * 重點問題（期間彙總，docs/FEEDBACK-3-PLAN.md #4；docs/FEEDBACK-4-PLAN.md §3 再改版）：
 * 問題查詢「依主機」下鑽進來原本只看得到時間軸色格，逐格點日期才看得到問題——這裡直接
 * 列出期間內出現過的問題。點列展開發生明細（rowDetail，不離頁）：頻率統計／案件資訊／
 * 逐日狀態，並把時間軸上這個問題出現過的日子高亮連動。「最近出現」欄的日期連結
 * 取代原本的整列連結（rowHref 與 rowDetail 互斥），跨日需求改走這個連結。
 */
function renderIssues(detail) {
    renderTable(document.getElementById('host-issues'), {
        columns: [
            { title: '來源 / Event', render: s => `${s.source} (${s.eventId})` },
            { title: '分類', render: s => CATEGORY_NAMES[s.category] ?? s.category },
            { title: '嚴重度', render: s => severityBadge(s.maxSeverity) },
            { title: '總次數', className: 'text-end', render: s => formatNumber(s.totalCount) },
            { title: '出現天數', className: 'text-end', render: s => formatNumber(s.daysSeen) },
            { title: '最近出現', className: 'text-nowrap', render: s => lastSeenLink(s) },
            { title: '說明', render: s => s.knownIssue || '' }
        ],
        rows: detail.topSignatures,
        rowDetail: s => occurrenceDetailPanel(s),
        // 時間軸的灰格＝「這天沒分析」，不是「沒問題」；空狀態的 hint 呼應同一個原則，
        // 避免使用者把「期間內沒有重點問題」誤讀成「這台主機根本沒被監控」
        empty: { title: '期間內未偵測到問題', hint: '時間軸灰格代表該日無分析紀錄，與「這天沒風險」是不同的意思。' }
    });
}

function lastSeenLink(signature) {
    const link = document.createElement('a');
    link.href = `/records/${hostId}/${signature.lastSeenDate}`;
    link.textContent = signature.lastSeenDate;
    link.title = '前往這天的風險日詳情';
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

/**
 * 問題發生明細展開列（docs/FEEDBACK-4-PLAN.md §3）：lazy fetch——renderTable 的展開/收合
 * 由外層 <tr> 的 click 監聽器控制（見 ui.js renderTable），這個節點本身沒有「被展開」的
 * 事件可掛，改用 setTimeout(0) 延到節點插入 DOM 後才找得到外層 <tr>，掛一個額外的 click
 * 監聽器判斷當下是展開還是收合（同一輪 click 兩個監聽器都會跑，順序即註冊順序，
 * renderTable 的 toggle 先發生，這裡讀到的 class 已經是切換後的狀態）。
 * cachedOccurrences 只抓一次，收合再展開不重打 API、也不必重新渲染內容。
 */
function occurrenceDetailPanel(signature) {
    const container = document.createElement('div');
    container.className = 'p-3';

    const placeholder = document.createElement('div');
    placeholder.className = 'text-muted small';
    placeholder.textContent = '展開檢視發生明細…';
    container.appendChild(placeholder);

    let cachedDates = null;

    async function load() {
        if (cachedDates) {
            highlightTimeline(cachedDates);
            return;
        }

        container.replaceChildren();
        const loading = document.createElement('div');
        loading.className = 'text-muted small';
        loading.textContent = '載入中…';
        container.appendChild(loading);

        try {
            const params = new URLSearchParams({ source: signature.source, eventId: String(signature.eventId), days: String(currentDays) });
            const data = await api.get(`/api/host-detail/${hostId}/issues?${params.toString()}`, { silent: true });
            cachedDates = data.occurrences.map(o => o.date);
            renderOccurrencePanel(container, data);
            highlightTimeline(cachedDates);
        } catch {
            container.replaceChildren();
            const error = document.createElement('div');
            error.className = 'text-danger small';
            error.textContent = '載入發生明細失敗，請重新整理頁面後再試。';
            container.appendChild(error);
            cachedDates = null;   // 失敗不快取，下次展開可以重試
        }
    }

    setTimeout(() => {
        // container 位在收合列（lf-row-detail）裡，真正掛 click 監聽器（renderTable 的
        // 展開/收合 toggle）的是它的上一個手足節點——那才是使用者實際點的內容列
        const detailRow = container.closest('tr');
        const tr = detailRow?.previousElementSibling;
        if (!tr) return;
        tr.addEventListener('click', () => {
            if (tr.classList.contains('lf-row-open')) load();
            else clearTimelineHighlight();
        });
    }, 0);

    return container;
}

function renderOccurrencePanel(container, data) {
    container.replaceChildren();

    const stats = document.createElement('div');
    stats.className = 'd-flex flex-wrap gap-4 small text-muted mb-3';
    const statParts = [
        `出現 ${data.stats.daysSeen} 天`,
        `總次數 ${formatNumber(data.stats.totalCount)}`,
        data.stats.avgGapDays != null ? `平均間隔 ${data.stats.avgGapDays.toFixed(1)} 天` : null,
        `最長連續 ${data.stats.longestStreak} 天`,
        `首見 ${data.stats.firstSeen}～最近 ${data.stats.lastSeen}`
    ].filter(Boolean);
    for (const part of statParts) {
        const span = document.createElement('span');
        span.textContent = part;
        stats.appendChild(span);
    }
    container.appendChild(stats);

    if (data.case) {
        const caseRow = document.createElement('div');
        caseRow.className = 'lf-hint mb-3';
        const closedNote = data.case.closedAt ? '（已結案）' : '（進行中）';
        if (data.case.handlerId) {
            const link = document.createElement('a');
            link.href = `/handlers/${data.case.handlerId}`;
            link.textContent = data.case.handlerName;
            link.addEventListener('click', event => event.stopPropagation());
            caseRow.append(`案件處理人：`, link, ` ｜ 狀態：${data.case.statusText}${closedNote} ｜ 涵蓋自 ${data.case.firstLinkedDate} 起`);
        } else {
            caseRow.textContent = `案件狀態：${data.case.statusText}${closedNote} ｜ 涵蓋自 ${data.case.firstLinkedDate} 起`;
        }
        container.appendChild(caseRow);
    }

    if (data.occurrences.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'text-muted small';
        empty.textContent = '目前篩選期間內沒有發生紀錄。';
        container.appendChild(empty);
        return;
    }

    const table = document.createElement('table');
    table.className = 'table table-sm mb-0';
    const thead = document.createElement('thead');
    thead.innerHTML = '<tr><th>日期</th><th class="text-end">當日次數</th><th>日風險</th><th>處理狀態</th></tr>';
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const occurrence of [...data.occurrences].reverse()) {   // 最近的排最上面
        const tr = document.createElement('tr');

        const dateCell = document.createElement('td');
        const link = document.createElement('a');
        link.href = `/records/${hostId}/${occurrence.date}`;
        link.textContent = occurrence.date;
        dateCell.appendChild(link);
        tr.appendChild(dateCell);

        const countCell = document.createElement('td');
        countCell.className = 'text-end';
        countCell.textContent = formatNumber(occurrence.count);
        tr.appendChild(countCell);

        const riskCell = document.createElement('td');
        riskCell.appendChild(riskBadge(occurrence.riskLevel));
        tr.appendChild(riskCell);

        const statusCell = document.createElement('td');
        statusCell.textContent = occurrence.statusText;
        if (occurrence.fromCase) {
            const note = document.createElement('span');
            note.className = 'text-muted small ms-1';
            note.textContent = '（案件同步）';
            statusCell.appendChild(note);
        }
        tr.appendChild(statusCell);

        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    container.appendChild(table);
}

/** 時間軸連動高亮：這個問題出現過的日子加外框，其餘日子淡化（docs/FEEDBACK-4-PLAN.md §3） */
function highlightTimeline(dates) {
    const set = new Set(dates);
    for (const cell of document.querySelectorAll('#host-timeline [data-date]')) {
        const match = set.has(cell.dataset.date);
        cell.classList.toggle('lf-timeline-cell--highlight', match);
        cell.classList.toggle('lf-timeline-cell--dim', !match);
    }
}

function clearTimelineHighlight() {
    for (const cell of document.querySelectorAll('#host-timeline [data-date]')) {
        cell.classList.remove('lf-timeline-cell--highlight', 'lf-timeline-cell--dim');
    }
}

function renderCheckup(detail) {
    if (!detail.latestCheckup) return;

    document.getElementById('checkup-card').classList.remove('d-none');
    const container = document.getElementById('host-checkup');

    const date = document.createElement('div');
    date.className = 'text-muted small mb-2';
    date.textContent = `${detail.latestCheckup.checkupDate}` +
        (detail.latestCheckup.hasFindings ? '' : '（本期無累積性異常）');

    const conclusion = document.createElement('div');
    conclusion.textContent = detail.latestCheckup.conclusion;

    container.replaceChildren(date, conclusion);
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

load();
