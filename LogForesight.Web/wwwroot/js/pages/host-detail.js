/**
 * 主機詳情／風險時間軸（docs/WEB-SPEC.md §9.4）。
 *
 * 時間軸的每一格都可點擊進入該日詳情——這是 §8.4 下鑽規則的另一個入口。
 * 沒有紀錄的日子刻意用不同顏色：「這天沒分析」與「這天沒風險」是完全不同的意思。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { renderLoading, renderSpinner, renderTable, labelValue, toast, withBusy, guardLoad, sortRows, applyBackfillDaysLimit } from '../core/ui.js';
import { formatDateTime, formatNumber, severityBadge, riskBadge, CATEGORY_NAMES, SEVERITY_ORDER } from '../core/format.js';
import { renderAiInline } from '../core/markdown-lite.js';

const root = document.getElementById('host-detail');
const hostId = Number(root.dataset.hostId);
let currentDays = 30;
let canMaintainHost = false;
const resourcePressureRequests = new Map();
const pressureReplayJobsBySensor = new Map();
let pressureReplayRefreshTimer = null;
let pressureReplayRefreshInFlight = false;
let lastHostDetail = null;
const hostUpdateModal = new bootstrap.Modal(document.getElementById('host-update-modal'));

const LEGEND = [
    { key: 'high', label: '高風險', color: 'var(--lf-risk-high)' },
    { key: 'mid', label: '中風險', color: 'var(--lf-risk-mid)' },
    { key: 'low', label: '低風險', color: 'var(--lf-timeline-low)' },
    { key: 'gap', label: '涵蓋不完整', color: 'var(--lf-timeline-gap)' },
    { key: 'none', label: '無分析紀錄', color: 'var(--lf-timeline-none)' }
];

// 分析本機主機開關（回饋十八輪批次D）：本機主機（Source==='local'）在停用時，「指定主機更新」
// 按鈕點下去只會打到後端的 400（ScheduleController 的 host 分支已擋），不如直接不顯示。
let localAnalysisEnabled = true;

// 取數排程執行中就不能再觸發這台主機的更新（後端會擋）。狀態來自 layout.js 的全站告示輪詢，
// 這裡只訂閱、不自己打 /api/run-activity（事件名與 core/layout.js 的 RUN_ACTIVITY_EVENT 相同）。
//
// **只看取數、不看聯集**：告示的 isRunning 是「取數或 AI 任一在跑」，但後端只在取數執行中
// 擋主機更新，AI 單獨在跑時這個動作是允許的——用聯集會在 AI 分析期間停用按鈕並說
// 「排程執行中」，兩件事都不成立。
const RUN_ACTIVITY_EVENT = 'lf:run-activity';

/** 初值取 layout.js 保存的最後狀態（本模組與 layout 同為 deferred module，正常載入時這裡還是 undefined、
 *  狀態靠下方的事件監聽補上；只有本模組比第一次輪詢回應更晚才載入時，這個初值才派得上用場） */
let schedulerRunning = window.lfRunActivity?.isFetchRun === true;

async function load() {
    for (const request of resourcePressureRequests.values()) request.controller?.abort();
    resourcePressureRequests.clear();
    renderLoading(document.getElementById('host-timeline'), 2);
    renderLoading(document.getElementById('host-issues'), 3);

    const [detail, user] = await Promise.all([
        api.get(`/api/host-detail/${hostId}?days=${currentDays}`),
        getCurrentUser()
    ]);
    lastHostDetail = detail;
    canMaintainHost = hasCapability(user, 'Maintain');

    if (canMaintainHost && detail.source === 'local') {
        const options = await api.get('/api/admin/schedule/options', { silent: true }).catch(() => null);
        localAnalysisEnabled = options?.localAnalysisEnabled ?? true;
    }

    renderHeader(detail);
    renderTimeline(detail);
    renderIssues(detail);
    renderCheckup(detail);
    renderResourcePressure(detail);
    if (detail.caseGrantOnly === true) {
        renderCaseGrantScopeNotice(document.getElementById('host-prtg'),
            '案件授與只包含被交辦的問題；不顯示主機整體的 PRTG 對應資訊。');
    } else {
        loadPrtgMapping();
    }
}

function renderCaseGrantScopeNotice(container, message) {
    if (!container) return;
    const notice = document.createElement('div');
    notice.className = 'alert alert-info small mb-0';
    notice.setAttribute('role', 'note');
    notice.textContent = message;
    container.replaceChildren(notice);
}

function renderHeader(detail) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const titleRow = document.createElement('div');
    titleRow.className = 'd-flex justify-content-between align-items-start mb-1';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold';
    // NetIQ 主機以 IP 登錄，光看 hostName 認不出是哪台機器——有 Sentinel 回報的顯示名就一併帶出
    title.textContent = detail.displayName ? `${detail.hostName}（${detail.displayName}）` : detail.hostName;
    titleRow.appendChild(title);

    // 指定主機更新（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.5）：就近原則，看著這台主機覺得資料舊了當場按，
    // 只有 Maintain 能觸發（與排程作業頁的立即執行同一組能力）。本機主機在「分析本機主機」
    // 停用時不顯示這顆按鈕（回饋十八輪批次D）：按下去只會打到後端 400，不如不顯示。
    if (canMaintainHost && (detail.source !== 'local' || localAnalysisEnabled)) {
        const updateButton = document.createElement('button');
        updateButton.type = 'button';
        updateButton.className = 'btn btn-sm btn-outline-primary lf-no-print';
        updateButton.textContent = '指定主機更新';
        updateButton.id = 'host-update-open';
        updateButton.addEventListener('click', () => openHostUpdateModal(detail));
        titleRow.appendChild(updateButton);
        applyRunActivityState();   // 重新渲染後補上目前狀態（按鈕是每次 load() 重建的）
    }

    body.appendChild(titleRow);

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
        ['分級', detail.tierText ?? '一般'],
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

const WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日'];

function renderTimeline(detail) {
    const container = document.getElementById('host-timeline');
    if (detail.caseGrantOnly === true) {
        renderCaseGrantScopeNotice(container,
            '案件授與只包含被交辦的問題；此頁不顯示整台主機的時間軸。');
        return;
    }

    // 依月份（yyyy-MM）分組
    const monthsMap = new Map();
    for (const day of detail.timeline) {
        const monthKey = day.date.substring(0, 7);
        if (!monthsMap.has(monthKey)) {
            monthsMap.set(monthKey, []);
        }
        monthsMap.get(monthKey).push(day);
    }

    const calendarWrap = document.createElement('div');
    calendarWrap.className = 'lf-calendar';

    for (const [monthKey, days] of monthsMap) {
        const monthCard = document.createElement('div');
        monthCard.className = 'lf-calendar__month';

        const [yStr, mStr] = monthKey.split('-');
        const year = parseInt(yStr, 10);
        const month = parseInt(mStr, 10);

        const title = document.createElement('div');
        title.className = 'lf-calendar__title';
        title.textContent = `${year} 年 ${month} 月`;
        monthCard.appendChild(title);

        const weekdaysHeader = document.createElement('div');
        weekdaysHeader.className = 'lf-calendar__weekdays';
        for (const wd of WEEKDAYS) {
            const wdCol = document.createElement('div');
            wdCol.className = 'lf-calendar__weekday';
            wdCol.textContent = wd;
            weekdaysHeader.appendChild(wdCol);
        }
        monthCard.appendChild(weekdaysHeader);

        const grid = document.createElement('div');
        grid.className = 'lf-calendar__grid';

        // 月初以空白補齊對齊星期（一～日：週一為 0，週日為 6）。
        // 前提：detail.timeline 是後端產生的**連續每日**陣列（缺分析的日子也會以 hasRecord=false 佔位），
        // 所以只補月首、之後逐格順排就對得上星期。若日後改成稀疏陣列，這裡要改成依日數字算 grid 位置
        const firstDay = days[0];
        const [firstY, firstM, firstD] = firstDay.date.split('-').map(Number);
        const firstDate = new Date(firstY, firstM - 1, firstD);
        const startWeekday = (firstDate.getDay() + 6) % 7;

        for (let i = 0; i < startWeekday; i++) {
            const emptyCell = document.createElement('span');
            emptyCell.className = 'lf-calendar__cell lf-calendar__cell--empty';
            emptyCell.setAttribute('aria-hidden', 'true');
            grid.appendChild(emptyCell);
        }

        for (const day of days) {
            const cell = document.createElement(day.hasRecord ? 'a' : 'span');
            cell.className = 'lf-calendar__cell lf-timeline-cell';
            cell.dataset.date = day.date;   // 問題發生明細展開時的高亮連動用（docs/archive/FEEDBACK-4-PLAN.md §3）
            cell.style.backgroundColor = cellColor(day);

            const dayNum = parseInt(day.date.split('-')[2], 10);
            cell.textContent = String(dayNum);

            if (day.riskReportPending === true) {
                const marker = document.createElement('span');
                marker.className = 'position-absolute top-0 end-0 small text-warning fw-bold';
                marker.textContent = '!';
                marker.setAttribute('aria-hidden', 'true');
                cell.appendChild(marker);
                cell.setAttribute('aria-label', `${day.date}｜${day.riskLevel || '風險'}｜風險報告待補，背景自動重試`);
            }

            if (day.riskLevel) {
                cell.dataset.risk = day.riskLevel;
            }

            if (day.hasRecord) {
                cell.href = appUrl(`/records/${hostId}/${day.date}`);
                cell.title = `${day.date}｜${day.riskLevel}風險${day.headline ? '｜' + day.headline : ''}${day.riskReportPending === true ? '｜風險報告待補，背景自動重試' : ''}`;
            } else {
                // 這天沒有分析紀錄——可能是排程沒跑、機器關機，不是「沒問題」
                cell.title = `${day.date}｜無分析紀錄`;
            }

            grid.appendChild(cell);
        }

        monthCard.appendChild(grid);
        calendarWrap.appendChild(monthCard);
    }

    container.replaceChildren(calendarWrap);
    renderLegend();
}

function cellColor(day) {
    if (!day.hasRecord) return 'var(--lf-timeline-none)';
    if (day.riskLevel === '高') return 'var(--lf-risk-high)';
    if (day.riskLevel === '中') return 'var(--lf-risk-mid)';
    if (day.hasCoverageGap) return 'var(--lf-timeline-gap)';
    return 'var(--lf-timeline-low)';
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

const ISSUE_COLUMNS = [
    {
        title: '來源 / Event',
        sortKey: 'source',
        sortValue: s => `${s.source} (${s.eventId})`,
        render: s => `${s.source} (${s.eventId})`
    },
    {
        title: '分類',
        sortKey: 'category',
        sortValue: s => CATEGORY_NAMES[s.category] ?? s.category,
        render: s => CATEGORY_NAMES[s.category] ?? s.category
    },
    {
        title: '嚴重度',
        sortKey: 'severity',
        sortDefaultDir: 'asc',
        sortValue: s => SEVERITY_ORDER.indexOf(s.maxSeverity),
        render: s => severityBadge(s.maxSeverity)
    },
    {
        title: '總次數',
        className: 'text-end',
        sortKey: 'totalCount',
        sortDefaultDir: 'desc',
        sortValue: s => s.totalCount,
        render: s => formatNumber(s.totalCount)
    },
    {
        title: '出現天數',
        className: 'text-end',
        sortKey: 'daysSeen',
        sortDefaultDir: 'desc',
        sortValue: s => s.daysSeen,
        render: s => formatNumber(s.daysSeen)
    },
    {
        title: '最近出現',
        className: 'text-nowrap',
        sortKey: 'lastSeenDate',
        sortDefaultDir: 'desc',
        sortValue: s => s.lastSeenDate,
        render: s => lastSeenLink(s)
    },
    {
        title: '說明',
        sortKey: 'knownIssue',
        sortValue: s => s.knownIssue || '',
        render: s => s.knownIssue || ''
    }
];

let issuesSort = null;
let currentDetail = null;

/**
 * 重點問題（期間彙總，docs/archive/FEEDBACK-3-PLAN.md #4；docs/archive/FEEDBACK-4-PLAN.md §3 再改版）：
 * 問題查詢「依主機」下鑽進來原本只看得到時間軸色格，逐格點日期才看得到問題——這裡直接
 * 列出期間內出現過的問題。點列展開發生明細（rowDetail，不離頁）：頻率統計／案件資訊／
 * 逐日狀態，並把時間軸上這個問題出現過的日子高亮連動。「最近出現」欄的日期連結
 * 取代原本的整列連結（rowHref 與 rowDetail 互斥），跨日需求改走這個連結。
 */
function renderIssues(detail) {
    currentDetail = detail;
    if (detail.caseGrantOnly === true) {
        renderCaseGrantScopeNotice(document.getElementById('host-issues'),
            '案件授與只包含被交辦的問題；此頁不顯示整台主機的問題摘要。');
        return;
    }
    const rows = sortRows(detail.topSignatures, ISSUE_COLUMNS, issuesSort);
    renderTable(document.getElementById('host-issues'), {
        columns: ISSUE_COLUMNS,
        rows,
        sort: issuesSort,
        onSort: (key, dir) => {
            issuesSort = { key, dir };
            renderIssues(currentDetail);
        },
        rowDetail: s => occurrenceDetailPanel(s),
        // 時間軸的灰格＝「這天沒分析」，不是「沒問題」；空狀態的 hint 呼應同一個原則，
        // 避免使用者把「期間內沒有重點問題」誤讀成「這台主機根本沒被監控」
        empty: { title: '期間內未偵測到問題', hint: '時間軸灰格代表該日無分析紀錄，與「這天沒風險」是不同的意思。' }
    });
}

function lastSeenLink(signature) {
    const link = document.createElement('a');
    link.href = appUrl(`/records/${hostId}/${signature.lastSeenDate}`);
    link.textContent = signature.lastSeenDate;
    link.title = '前往這天的風險日詳情';
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

/**
 * 問題發生明細展開列（docs/archive/FEEDBACK-4-PLAN.md §3）：lazy fetch——renderTable 的展開/收合
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

        renderSpinner(container, '載入中…');

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
            link.href = appUrl(`/handlers/${data.case.handlerId}`);
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
        link.href = appUrl(`/records/${hostId}/${occurrence.date}`);
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

/** 時間軸連動高亮：這個問題出現過的日子加外框，其餘日子淡化（docs/archive/FEEDBACK-4-PLAN.md §3） */
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
    renderAiInline(conclusion, detail.latestCheckup.conclusion);

    container.replaceChildren(date, conclusion);
}

function renderResourcePressure(detail) {
    const container = document.getElementById('host-resource-pressure');
    if (detail.caseGrantOnly === true) {
        renderCaseGrantScopeNotice(container,
            '案件授與只包含被交辦的問題；此頁不顯示主機層級的資源觀察或維護控制。');
        return;
    }
    if (detail.canManageResourcePressure === true) refreshPressureReplayStatus(detail.hostId);
    const hints = detail.resourcePressureHints ?? [];
    const intro = document.createElement('div');
    intro.className = 'text-muted small mb-2';
    intro.textContent = '以可信來源與最近兩個完整小時評估；等待中的資料不代表主機故障。';
    const availability = resourcePressureAvailability(detail.resourcePressureAvailability);
    const modes = detail.resourcePressureModes ?? [];
    const canManage = detail.canManageResourcePressure === true;
    if (!hints.length && !modes.length) {
        const empty = Object.assign(document.createElement('div'), {
            className: 'text-muted small', textContent: '尚無目前資源期間觀察。'
        });
        const replayRows = [];
        if (canManage) {
            for (const [key, jobs] of pressureReplayJobsBySensor) {
                if (!key.startsWith(`${detail.hostId}:`)) continue;
                for (const job of jobs) {
                    const line = document.createElement('div');
                    line.className = 'small text-muted';
                    const state = job.status === 'Completed' ? '已完成' : job.status === 'Overdue' ? '已逾期' :
                        job.status === 'Superseded' ? '已被較新設定取代' : '等待撤回／重評';
                    line.textContent = `sensor #${job.sensorObjid} ${job.enabled ? '啟用重評' : '停用撤回'}：${state}` +
                        (job.lastReason ? `（${modeReplayReasonText(job.lastReason)}）` : '');
                    replayRows.push(line);
                }
            }
        }
        container.replaceChildren(intro, ...(availability ? [availability] : []), empty, ...replayRows);
        return;
    }
    const labels = {
        'source-policy-unready': '目前來源或監控政策尚未就緒',
        'resource-paused': '此資源已暫停監控',
        'current-strategy-unverified': '目前取樣策略尚未核實',
        'source-profile-missing': '缺少來源資源設定',
        'source-authority-incomplete': '來源尚未提供足以核實的資源語意',
        'source-profile-invalid': '來源資源設定無效',
        'source-profile-stale': '來源資源設定已過期或與目前資源不符',
        'measurement-semantics-unverified': '數值單位或方向尚未核實',
        'latest-two-completed-hours-missing': '缺少最近兩個完整小時的資料',
        'hour-coverage-below-75-percent': '每小時可信資料涵蓋率未達 75%',
        'summary-does-not-match-proved-slots': '小時摘要與可信取樣不一致',
        'invalid-hour-proof': '可信取樣證明無效',
        'trusted_hour_evidence': '缺少可信小時資料',
        'rule-changed': '目前啟用規則已變更',
        'profile-identity-or-current-rule-unavailable': '來源 profile、資源身分或目前規則已變更'
    };
    const familyNames = { Cpu: 'CPU', Memory: '記憶體', Disk: '磁碟' };
    const stateNames = { Hit: '門檻命中', Recovery: '已恢復', NoHit: '未達門檻', Insufficient: '等待可信資料' };
    const modeByKey = new Map(modes.map(mode => [`${mode.sensorObjid}:${mode.family}`, mode]));
    const rows = hints.map(hint => {
        const key = `${hint.sensorObjid}:${hint.family}`;
        const mode = modeByKey.get(key);
        const row = document.createElement('div');
        row.className = 'border-bottom py-2';
        const title = document.createElement('div');
        title.className = 'fw-semibold';
        title.textContent = `${familyNames[hint.family] ?? '資源'}：${stateNames[hint.state] ?? '等待核實'}`;
        row.appendChild(title);
        const detailLine = document.createElement('div');
        detailLine.className = 'small text-muted';
        const values = [hint.earlierHourAveragePercent, hint.latestHourAveragePercent];
        if (values.every(Number.isFinite)) {
            detailLine.textContent = `最近兩個完整小時平均 ${values[0].toFixed(1)}%／${values[1].toFixed(1)}%`;
            if (Number.isFinite(hint.coveragePercent))
                detailLine.textContent += `，可信涵蓋率 ${hint.coveragePercent.toFixed(1)}%`;
        } else {
            detailLine.textContent = labels[hint.reasonCode] ?? '目前資料不足以完成判定';
        }
        row.appendChild(detailLine);
        if (hint.firstHourStartUtc && hint.latestHourStartUtc) {
            const window = document.createElement('div');
            window.className = 'small text-muted';
            const through = new Date(new Date(hint.latestHourStartUtc).getTime() + 60 * 60 * 1000);
            window.textContent = `證據窗口（UTC）：${utcStamp(hint.firstHourStartUtc)} 至 ${utcStamp(through.toISOString())}`;
            row.appendChild(window);
        }
        if (hint.formalReasons?.length) {
            const formal = document.createElement('div');
            formal.className = 'small';
            formal.textContent = `正式有效理由：${hint.formalReasons.join('；')}`;
            row.appendChild(formal);
            if (hint.episodeObservedSinceUtc) {
                const observed = document.createElement('div');
                observed.className = 'small text-muted';
                observed.textContent = `此 episode 首次正式判定時間（UTC，非物理故障起始）：${utcStamp(hint.episodeObservedSinceUtc)}`;
                row.appendChild(observed);
            }
        }
        if (hint.missingFacts?.length) {
            const missing = document.createElement('div');
            missing.className = 'small text-muted';
            missing.textContent = `待補：${hint.missingFacts.map(code => labels[code] ?? '核實來源資料').join('、')}`;
            row.appendChild(missing);
        }
        const asOf = document.createElement('div');
        asOf.className = 'small text-muted';
        asOf.textContent = hint.evidenceAsOfUtc
            ? `證據截止 ${formatDateTime(hint.evidenceAsOfUtc)}；來源資格核對 ${formatDateTime(hint.asOfUtc)}`
            : `判定時間 ${formatDateTime(hint.asOfUtc)}`;
        row.appendChild(asOf);
        if (canManage && (hint.family === 'Cpu' || hint.family === 'Memory')) {
            row.appendChild(renderPressureControl(detail.hostId, hint, mode));
        }
        return row;
    });
    if (canManage) {
        for (const mode of modes) {
            if (mode.family !== 'Cpu' && mode.family !== 'Memory') continue;
            if (hints.some(hint => hint.sensorObjid === mode.sensorObjid && hint.family === mode.family)) continue;
            const row = document.createElement('div');
            row.className = 'border-bottom py-2';
            row.textContent = `${familyNames[mode.family] ?? '資源'} sensor #${mode.sensorObjid}：` +
                (mode.status === 'active' ? '正式模式有效，目前沒有期間觀察。' : `正式模式資格失效：${labels[mode.staleReason] ?? mode.staleReason ?? '需重新核實'}。`);
            row.appendChild(renderPressureControl(detail.hostId,
                { sensorObjid: mode.sensorObjid, family: mode.family }, mode));
            rows.push(row);
        }
    }
    if (canManage) {
        const note = document.createElement('div');
        note.className = 'small text-muted mt-2';
        note.textContent = '正式模式只影響後續正式資源風險判定；試算與期間觀察不會自動建立案件或寄送郵件。';
        rows.push(note);
    }
    container.replaceChildren(intro, ...(availability ? [availability] : []), ...rows);
}

function resourcePressureAvailability(state) {
    if (!state) return null;
    const copy = {
        disabled: 'PRTG 資源擷取目前關閉。若需要資源觀察，請由管理者核對系統設定。',
        'policy-not-ready': 'PRTG 監控政策尚未就緒或未涵蓋此主機。請維護者核對正式判定試點與目前政策。',
        'current-proof-unavailable': '目前沒有通過當前政策與資源身分核對的期間觀察；不以舊資料填補。請維護者核對來源政策、資源對應及新取樣是否完成。',
        warming: '目前資源身分可核對，但完整小時或可信涵蓋資料仍在累積。等待後續資料；這不表示主機故障。',
        'ready-no-hit': '最近的完整期間有足夠可信資料，尚未達目前判定門檻。',
        'current-evidence': '目前有符合來源與資源身分的期間證據，請查看下方各資源結果。'
    };
    const line = document.createElement('div');
    line.className = 'small mb-2';
    line.textContent = copy[state] ?? '目前資源證據狀態未知；請先核對來源政策與證據完整性。';
    return line;
}

function renderPressureControl(hostId, hint, mode) {
    const wrapper = document.createElement('div');
    wrapper.className = 'd-flex flex-wrap align-items-center gap-2 mt-2 lf-no-print';
    const key = `${hint.sensorObjid}:${hint.family}`;
    const state = resourcePressureRequests.get(key);
    const replayJobs = pressureReplayJobsBySensor.get(`${hostId}:${hint.sensorObjid}`) ?? [];
    if (replayJobs.length) {
        const replay = document.createElement('span');
        replay.className = 'small text-muted';
        replay.textContent = replayJobs.map(job => {
            const state = job.status === 'Completed' ? '已完成' : job.status === 'Overdue' ? '已逾期' :
                job.status === 'Superseded' ? '已被較新設定取代' : '等待撤回／重評';
            const deadline = job.deadlineArmed ? `，截止 ${formatDateTime(job.dueAtUtc)}` : '，完整證據就緒後開始 15 分鐘期限';
            return `${job.enabled ? '啟用重評' : '停用撤回'}：${state}${deadline}${job.lastReason ? `（${modeReplayReasonText(job.lastReason)}）` : ''}`;
        }).join('；');
        wrapper.appendChild(replay);
    }
    if (mode?.formalEnabled) {
        const status = document.createElement('span');
        status.className = `small ${mode.status === 'active' ? 'text-success' : 'text-warning'}`;
        status.textContent = mode.status === 'active' ? '正式模式有效' :
            `正式模式資格失效：${mode.staleReason ? ({
                'source-policy-unready': '來源或監控政策尚未就緒',
                'rule-changed': '目前啟用規則已變更',
                'profile-identity-or-current-rule-unavailable': '來源 profile、資源身分或目前規則已變更'
            }[mode.staleReason] ?? mode.staleReason) : '目前來源或規則已變更'}`;
        wrapper.appendChild(status);
        const disable = document.createElement('button');
        disable.type = 'button'; disable.className = 'btn btn-sm btn-outline-secondary';
        disable.textContent = '關閉正式模式';
        disable.disabled = state?.busy === true;
        disable.addEventListener('click', () => setPressureMode(hostId, hint, false, null, key));
        wrapper.appendChild(disable);
        if (state?.message) {
            const message = document.createElement('span'); message.className = 'small text-danger';
            message.textContent = state.message; wrapper.appendChild(message);
        }
        return wrapper;
    }
    if (!state?.trial) {
        const trialButton = document.createElement('button');
        trialButton.type = 'button'; trialButton.className = 'btn btn-sm btn-outline-primary';
        trialButton.textContent = state?.busy ? (state.stage === 'activating' ? '正在啟用…' : '正在試算…') : '試算正式模式';
        trialButton.disabled = state?.busy === true;
        trialButton.addEventListener('click', () => requestPressureTrial(hostId, hint, key));
        wrapper.appendChild(trialButton);
        if (state?.busy && state.stage !== 'activating') {
            const cancel = document.createElement('button');
            cancel.type = 'button'; cancel.className = 'btn btn-sm btn-link'; cancel.textContent = '取消';
            cancel.addEventListener('click', () => {
                state.controller?.abort();
                resourcePressureRequests.set(key, { message: '試算已取消；正式模式未啟用。' });
                renderResourcePressure(lastHostDetail);
            });
            wrapper.appendChild(cancel);
        }
        if (state?.message) {
            const message = document.createElement('span'); message.className = 'small text-danger';
            message.textContent = state.message; wrapper.appendChild(message);
        }
        return wrapper;
    }

    const trial = state.trial;
    const summary = document.createElement('div');
    summary.className = 'w-100 small';
    const outcome = { Hit: '兩小時均達壓力門檻', NoHit: '兩小時未同時達門檻', Recovery: '兩小時均低於恢復門檻' };
    const title = document.createElement('div'); title.className = 'fw-semibold';
    title.textContent = `試算結果：${outcome[trial.outcome] ?? '已完成'}（固定門檻 ${trial.thresholdPercent}%／恢復 ${trial.recoveryPercent}%）`;
    summary.appendChild(title);
    for (const hour of trial.hours ?? []) {
        const line = document.createElement('div'); line.className = 'text-muted';
        line.textContent = `${utcStamp(hour.startUtc)}：平均 ${Number(hour.averagePercent).toFixed(1)}%，可信涵蓋率 ${Number(hour.coveragePercent).toFixed(1)}%（${hour.goodSlots}/${hour.requiredSlots} 點，最低 ${trial.minimumHourlyCoveragePercent}%）`;
        summary.appendChild(line);
    }
    const expiry = document.createElement('div'); expiry.className = 'text-muted';
    expiry.textContent = `來源版本 ${trial.grant.profileFingerprint.slice(0, 12)}…／規則版本 ${trial.grant.rulesVersion}；證據截止 ${formatDateTime(trial.evidenceAsOfUtc)}；同版本試算結果有效至 ${formatDateTime(trial.grant.expiresAtUtc)}。`;
    summary.appendChild(expiry);
    const enable = document.createElement('button');
    enable.type = 'button'; enable.className = 'btn btn-sm btn-primary';
    enable.textContent = state.busy ? '正在啟用…' : '啟用正式模式'; enable.disabled = state.busy === true;
    enable.addEventListener('click', () => setPressureMode(hostId, hint, true, trial.grant.trialResultId, key));
    const cancel = document.createElement('button');
    cancel.type = 'button'; cancel.className = 'btn btn-sm btn-outline-secondary'; cancel.textContent = '取消';
    cancel.disabled = state.busy === true;
    cancel.addEventListener('click', () => { resourcePressureRequests.delete(key); renderResourcePressure(lastHostDetail); });
    wrapper.append(summary, enable, cancel);
    return wrapper;
}

function modeReplayReasonText(code) {
    if (code === 'unknown-generation-scope') return '缺少可安全限定的來源世代';
    if (code === 'no-persisted-host-days') return '尚無可核對的每日紀錄';
    const count = /^([0-9]+)-host-days-waiting$/.exec(code);
    if (count) return `有 ${count[1]} 個主機日仍待處理`;
    if (code.includes('qualified-parent-or-closed-day-pending')) return '等待合格父紀錄或完整關閉日證據';
    if (code.includes('parent-row-not-reconcilable')) return '父紀錄尚不能安全更新';
    return '等待背景重評完成';
}

async function refreshPressureReplayStatus(targetHostId) {
    if (pressureReplayRefreshInFlight || !Number.isFinite(targetHostId) || targetHostId <= 0) return;
    if (pressureReplayRefreshTimer) clearTimeout(pressureReplayRefreshTimer);
    pressureReplayRefreshTimer = null;
    pressureReplayRefreshInFlight = true;
    try {
        const result = await api.get(`/api/prtg/resource-pressure/${targetHostId}/mode/replay`, { silent: true });
        const grouped = new Map();
        for (const job of result?.replayJobs ?? []) {
            const key = `${targetHostId}:${job.sensorObjid}`;
            if (!grouped.has(key)) grouped.set(key, []);
            grouped.get(key).push(job);
        }
        for (const key of [...pressureReplayJobsBySensor.keys()]) {
            if (key.startsWith(`${targetHostId}:`)) pressureReplayJobsBySensor.delete(key);
        }
        for (const [key, jobs] of grouped) pressureReplayJobsBySensor.set(key, jobs);
        if (lastHostDetail?.hostId === targetHostId)
            renderResourcePressure(lastHostDetail);
        if (result?.replayPending === true)
            pressureReplayRefreshTimer = setTimeout(() => refreshPressureReplayStatus(targetHostId), 15000);
    } catch { /* Status display is advisory; saved mode and durable worker remain authoritative. */ }
    finally { pressureReplayRefreshInFlight = false; }
}

async function requestPressureTrial(hostId, hint, key) {
    const controller = new AbortController();
    const state = { busy: true, controller };
    resourcePressureRequests.set(key, state);
    renderResourcePressure(lastHostDetail);
    try {
        state.trial = await api.post(`/api/prtg/resource-pressure/${hostId}/trial`,
            { sensorObjid: hint.sensorObjid }, { signal: controller.signal });
        state.busy = false; state.controller = null;
    } catch (error) {
        if (error?.name === 'AbortError') return;
        state.busy = false; state.controller = null;
        state.message = error?.message ?? '試算失敗，請重新載入後再試。';
    }
    renderResourcePressure(lastHostDetail);
}

async function setPressureMode(hostId, hint, enabled, trialResultId, key) {
    const state = { busy: true, stage: enabled ? 'activating' : 'disabling',
        trial: resourcePressureRequests.get(key)?.trial ?? null };
    resourcePressureRequests.set(key, state);
    renderResourcePressure(lastHostDetail);
    try {
        const result = await api.put(`/api/prtg/resource-pressure/${hostId}/mode`,
            { sensorObjid: hint.sensorObjid, trialResultId, enabled });
        const replay = result?.replayJobs?.filter(job => job.sensorObjid === hint.sensorObjid) ?? [];
        const hasOverdue = replay.some(job => job.status === 'Overdue');
        const pending = result?.replayPending === true;
        const action = enabled ? '正式模式已保存' : '正式模式已保存';
        const deadlineArmed = replay.some(job => job.deadlineArmed);
        toast(hasOverdue
            ? `${action}；歷史撤回／重評已逾期，請查看主機模式狀態。`
            : pending ? `${action}；${deadlineArmed ? '歷史撤回／重評期限為 15 分鐘。' : '完整證據就緒後開始 15 分鐘期限。'}`
                : `${action}；歷史撤回／重評已完成。`, hasOverdue ? 'danger' : pending ? 'info' : 'success');
        resourcePressureRequests.delete(key);
        await load();
    } catch (error) {
        state.busy = false; state.trial = null;
        state.message = error?.message ?? '設定失敗，請重新試算後再試。';
        renderResourcePressure(lastHostDetail);
    }
}

function utcStamp(value) {
    return new Date(value).toISOString().slice(0, 19).replace('T', ' ') + ' UTC';
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

// ── 指定主機更新（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.5）─────────────────────────

function openHostUpdateModal(detail) {
    document.getElementById('host-update-message').textContent =
        `重新分析「${detail.hostName}」的缺漏日；已分析過的日子仍會冪等跳過，不會重複產生紀錄。`;
    const backfillInput = document.getElementById('host-update-backfill');
    if (backfillInput) {
        backfillInput.value = '';
    }
    applyBackfillDaysLimit(
        'host-update-backfill',
        'host-update-backfill-help',
        detail.maxBackfillDays,
        `往回檢查幾天內有沒有缺漏或需要補跑的日子（上限 ${detail.maxBackfillDays} 天），已完成的日子不會重跑。只影響這次執行，不會落地變更設定值；留空則沿用 NetIQ 維護頁設定的回望天數（本機主機不受此影響）。`
    );
    hostUpdateModal.show();
}

/** 取數執行中的說明文字（兩處共用同一句） */
const RUN_BUSY_NOTE_TEXT = '取數執行中，結束後可用。';

/**
 * 排程執行中時停用「指定主機更新」，並在**按鈕旁**與**表單內**各說明一次原因。
 *
 * 用 disabled 加說明而不是隱藏按鈕：按鈕消失會讓使用者以為自己的權限被拿掉了，
 * 「暫時不能按」與「你不能按」是兩件事，畫面上必須分得出來。
 *
 * **兩處都要有**：頁面上那顆按鈕被停用後 modal 就打不開了，說明只放在 modal 裡等於看不到，
 * 使用者面對的仍是一顆沒有理由的灰按鈕；而 modal 已經開著時排程才開始的話，
 * 灰掉的是 modal 內的送出鈕，那時要解釋的是它。
 */
function applyRunActivityState() {
    for (const id of ['host-update-open', 'host-update-submit']) {
        const button = document.getElementById(id);
        if (button) button.disabled = schedulerRunning;
    }

    for (const note of ensureRunActivityNotes()) {
        note.classList.toggle('d-none', !schedulerRunning);
    }
}

/** 說明文字元素（HostDetail.cshtml 沒有這兩行，第一次需要時就地補上） */
function ensureRunActivityNotes() {
    const notes = [];

    // 按鈕旁：解釋頁面上那顆「指定主機更新」為什麼是灰的。按鈕每次 load() 重建，
    // 說明也跟著掛回同一個容器，所以這裡以按鈕的父容器為準而不是快取節點。
    const openButton = document.getElementById('host-update-open');
    if (openButton?.parentElement) {
        let inlineNote = document.getElementById('host-update-busy-note-inline');
        if (!inlineNote || inlineNote.parentElement !== openButton.parentElement) {
            inlineNote?.remove();
            inlineNote = document.createElement('span');
            inlineNote.id = 'host-update-busy-note-inline';
            inlineNote.className = 'small text-muted d-none';
            inlineNote.textContent = RUN_BUSY_NOTE_TEXT;
            openButton.parentElement.appendChild(inlineNote);
        }
        notes.push(inlineNote);
    }

    // 表單內：解釋 modal 開著時送出鈕為什麼是灰的
    const form = document.getElementById('host-update-form');
    const modalBody = form?.querySelector('.modal-body');
    if (modalBody) {
        let note = document.getElementById('host-update-busy-note');
        if (!note) {
            note = document.createElement('div');
            note.id = 'host-update-busy-note';
            note.className = 'small text-muted mt-2 d-none';
            note.textContent = RUN_BUSY_NOTE_TEXT;
            modalBody.appendChild(note);
        }
        notes.push(note);
    }

    return notes;
}

window.addEventListener(RUN_ACTIVITY_EVENT, event => {
    schedulerRunning = event.detail?.isFetchRun === true;
    applyRunActivityState();
});

document.getElementById('host-update-form').addEventListener('submit', async event => {
    event.preventDefault();

    const backfillDays = document.getElementById('host-update-backfill').value;
    const submitButton = document.getElementById('host-update-submit');
    const restore = withBusy(submitButton, '送出中');
    try {
        const result = await api.post('/api/admin/schedule/run', {
            scope: 'host',
            hostId,
            backfillDays: backfillDays ? Number(backfillDays) : null
        });
        toast(result.message, result.started ? 'success' : 'warning');
        if (result.started) hostUpdateModal.hide();
    } catch {
        // 錯誤已由 api.js 顯示（含「此主機目前不會被查詢」等驗證訊息）
    } finally {
        restore();
    }
});

// ── PRTG 監控對應 ─────────────────────────────────────────────────────────

async function loadPrtgMapping() {
    const container = document.getElementById('host-prtg');
    if (!container) return;
    renderLoading(container, 2);

    try {
        const data = await api.get(`/api/host-detail/${hostId}/prtg`, { silent: true });
        if (!data || !data.devices || data.devices.length === 0) {
            container.replaceChildren();
            const empty = document.createElement('div');
            if (data?.ipExcluded) {
                empty.className = 'text-warning small';
                empty.textContent = `此主機 IP（${data.excludedIp || ''}）已排除 PRTG 對應，因此不會建立 device 對應，也不會取數。`;
            } else {
                empty.className = 'text-muted small';
                empty.textContent = '這台主機目前沒有對應到 PRTG device';
            }
            container.appendChild(empty);
            return;
        }

        renderPrtgDevices(container, data);
    } catch {
        container.replaceChildren();
        const empty = document.createElement('div');
        empty.className = 'text-muted small';
        empty.textContent = '這台主機目前沒有對應到 PRTG device';
        container.appendChild(empty);
    }
}

// PRTG sensor 狀態徽章：暫停優先；其餘依狀態字串前綴（不分大小寫）判定色系，
// 文字顯示原字串（保留「Down (Acknowledged)」這類括號變體），空值顯示「未知」。
function prtgSensorStatusBadge(sensor) {
    if (sensor.paused) return { text: '已暫停', variant: 'secondary' };
    const raw = (sensor.status ?? '').trim();
    if (!raw) return { text: '未知', variant: 'secondary' };
    const lower = raw.toLowerCase();
    if (lower.startsWith('down')) return { text: raw, variant: 'danger' };
    if (lower.startsWith('warning')) return { text: raw, variant: 'warning' };
    if (lower.startsWith('up')) return { text: raw, variant: 'success' };
    return { text: raw, variant: 'secondary' };
}

function badge(text, variant) {
    const el = document.createElement('span');
    el.className = `lf-badge lf-badge--${variant}`;
    el.textContent = text;
    return el;
}

function renderPrtgDevices(container, data) {
    container.replaceChildren();

    if (data.ipExcluded) {
        const warn = document.createElement('div');
        warn.className = 'text-warning small mb-3';
        warn.textContent = '此主機 IP 在排除清單中，以下對應來自人工指定。';
        container.appendChild(warn);
    }

    for (const device of data.devices) {
        const devCard = document.createElement('div');
        devCard.className = 'mb-3 pb-3 border-bottom';

        const devHeader = document.createElement('div');
        devHeader.className = 'd-flex justify-content-between align-items-center mb-2 flex-wrap gap-2';

        const devTitle = document.createElement('div');
        devTitle.className = 'fw-semibold';
        devTitle.textContent = device.name
            ? `${device.name}（objid ${device.deviceObjid}${device.ip ? `，${device.ip}` : ''}）`
            : `PRTG Device: ${device.deviceObjid}${device.ip ? ` (${device.ip})` : ''}`;

        const devMeta = document.createElement('div');
        devMeta.className = 'small text-muted';
        const parts = [];
        if (device.mapStatus) parts.push(`對應狀態：${device.mapStatus}`);
        if (data.mapDate) parts.push(`基準日：${data.mapDate.split('T')[0]}`);
        if (device.note) parts.push(`備註：${device.note}`);
        devMeta.textContent = parts.join(' ｜ ');

        devHeader.append(devTitle, devMeta);
        devCard.appendChild(devHeader);

        if (!device.sensors || device.sensors.length === 0 || device.sensorCount === 0) {
            const noSensors = document.createElement('div');
            noSensors.className = 'text-muted small';
            noSensors.textContent = 'PRTG 資料準備中：新對應的裝置正在補抓感測器，通常幾分鐘內完成。';
            devCard.appendChild(noSensors);
        } else {
            const table = document.createElement('table');
            table.className = 'table table-sm table-hover mb-0 small';
            table.innerHTML = '<thead><tr><th>Sensor ID</th><th>名稱</th><th>類型</th><th>分類</th><th>狀態</th></tr></thead>';
            const tbody = document.createElement('tbody');
            for (const s of device.sensors) {
                const tr = document.createElement('tr');
                const status = prtgSensorStatusBadge(s);
                const cells = [
                    [String(s.objid), 'font-monospace'],
                    [s.name ?? '', ''],
                    [s.sensorType || '-', ''],
                    [s.category || '—', '']
                ];
                for (const [text, className] of cells) {
                    const td = document.createElement('td');
                    if (className) td.className = className;
                    td.textContent = text;
                    tr.appendChild(td);
                }
                const statusTd = document.createElement('td');
                statusTd.appendChild(badge(status.text, status.variant));
                tr.appendChild(statusTd);
                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            devCard.appendChild(table);
        }

        container.appendChild(devCard);
    }
}

guardLoad([
    document.getElementById('host-timeline'),
    document.getElementById('host-issues'),
    document.getElementById('host-prtg')
], load);
