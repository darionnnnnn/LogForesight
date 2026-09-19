/**
 * 處理人員工作頁 `/handlers/{userId}`：處理人的工作單位是**交辦單**（一張單＝一個問題 ×
 * 一批主機），所以主體是交辦單清單；「依主機」的逐案件平鋪與「被指派的風險日」各自留一個頁籤。
 *
 * 授權：全登入角色可查看任何人，資料以**檢視者**的可見範圍過濾（後端 WorkOrderQueryService／
 * HandlingService.GetHandlerWorkload），與全站查詢頁同一套模型，不新增能力。
 * 回覆只有「該單處理人本人且具 Handle」能做（後端 403 把關），前端據此顯示或隱藏按鈕——
 * 見 canReply，回覆按鈕與勾選框都吃這個旗標。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    renderLoading,
    renderTable,
    renderPagination,
    bindTabs,
    statCard,
    guardLoad,
    loadPageSize,
    savePageSize
} from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, formatUserName, riskBadge, statusBadge } from '../core/format.js';
import { openWorkOrderReplyModal, toastReplyResult, toastReplyManyResult, workOrdersDraftKey } from './issue-status-reply.js';

const root = document.getElementById('handler-detail');
const userId = Number(root.dataset.userId);

const kpiEl = document.getElementById('handler-kpi');
const casesEl = document.getElementById('handler-cases');
const daysEl = document.getElementById('handler-days');
const ordersEl = document.getElementById('handler-wo-list');
const ordersPagerEl = document.getElementById('handler-wo-pager');
const ordersNoteEl = document.getElementById('handler-wo-note');
const statusSelect = document.getElementById('handler-wo-status');
const sortSelect = document.getElementById('handler-wo-sort');
const pausedCheckbox = document.getElementById('handler-wo-paused');
const replyOrdersBtn = document.getElementById('handler-wo-reply');

// 來源名稱對照（同 work-orders.js）
const ORIGIN_LABELS = {
    manual: '人工交辦',
    owner_rule: '負責人規則',
    auto_dispatch: '自動派工',
    backfill: '系統整併',
    day_assign: '詳情頁指派'
};

// 成員狀態對照（同 work-order-detail.js）
const MEMBER_STATUS_META = {
    open: { label: '未處理', variant: 'danger' },
    in_progress: { label: '處理中', variant: 'primary' },
    observing: { label: '觀察中', variant: 'primary' },
    escalated: { label: '無法處理（上報）', variant: 'warning' },
    resolved: { label: '已處理', variant: 'success' },
    wont_fix: { label: '不處理', variant: 'success' },
    false_positive: { label: '誤報', variant: 'success' },
    known_noise: { label: '已知雜訊', variant: 'success' }
};

const MEMBER_STATUS_OPTIONS = [
    { value: 'active', label: '進行中' },
    { value: 'escalated', label: '上報中' },
    { value: 'overdue', label: '逾期' },
    { value: 'closed', label: '已結案' },
    { value: 'all', label: '全部' }
];

const MEMBER_PAGE_SIZE = 50;

let includeResolvedDays = false;
let currentUser = null;
let canReply = false;              // 本人檢視自己的頁且具 Handle
let summary = null;                // work-orders/summary
let orderPage = 1;
const selectedOrderIds = new Set();
const orderRowsById = new Map();   // 目前這一頁的單，供「回覆選取的單」算台數

async function load() {
    renderLoading(kpiEl, 1);
    renderLoading(ordersEl, 5);
    renderLoading(casesEl, 3);
    renderLoading(daysEl, 3);
    selectedOrderIds.clear();

    const user = currentUser ?? await getCurrentUser();
    currentUser = user;
    canReply = user?.userId === userId && hasCapability(user, 'Handle');
    replyOrdersBtn.classList.toggle('d-none', !canReply);

    const [workload, summaryData] = await Promise.all([
        api.get(`/api/handlers/${userId}/workload?includeResolvedDays=${includeResolvedDays}`),
        api.get(`/api/handlers/${userId}/work-orders/summary`)
    ]);
    summary = summaryData;

    renderHeader(workload);
    renderKpi(summaryData);
    renderCases(workload.cases);
    renderDays(workload.days);

    await loadOrders();
}

function renderHeader(data) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold';
    const name = formatUserName(data.displayName, data.account);
    title.textContent = data.active ? name : `${name}（已停用）`;
    body.appendChild(title);

    if (!data.active) {
        const note = document.createElement('div');
        note.className = 'text-muted small mt-1';
        note.textContent = '此帳號已停用；交辦紀錄是歷史事實，不因停用消失。';
        body.appendChild(note);
    }

    card.appendChild(body);
    document.getElementById('handler-header').replaceChildren(card);
}

function renderKpi(data) {
    kpiEl.replaceChildren();

    const overdue = data.overdueMembers ?? 0;
    const cards = [
        { value: data.activeWorkOrders, label: '進行中單' },
        { value: data.activeMembers, label: '進行中台數' },
        { value: overdue, label: '逾期台數', variant: overdue > 0 ? 'danger' : 'secondary' },
        { value: data.unrepliedWorkOrders, label: '未回覆單' }
    ];

    for (const c of cards) {
        const col = document.createElement('div');
        col.className = 'col-3';
        col.appendChild(statCard({ value: formatNumber(c.value), label: c.label, variant: c.variant }));
        kpiEl.appendChild(col);
    }
}

// ── 交辦單頁籤 ─────────────────────────────────────────────────────────────

/** 交辦單清單的請求序號（同 work-orders.js）：慢回來的舊請求不可蓋掉新篩選的結果 */
let ordersLoadSeq = 0;

async function loadOrders() {
    const seq = ++ordersLoadSeq;
    renderLoading(ordersEl, 5);

    const pageSize = loadPageSize('handler-work-orders');
    const params = new URLSearchParams({
        status: statusSelect.value,
        sort: sortSelect.value,
        page: String(orderPage),
        pageSize: String(pageSize)
    });
    // 未勾選時不帶 paused：處理人視角的預設本來就是排除暫停單（後端決定）
    if (pausedCheckbox.checked) params.set('paused', 'only');

    const data = await api.get(`/api/handlers/${userId}/work-orders?${params}`);
    if (seq !== ordersLoadSeq) return;
    renderOrders(data);
}

/** 問題欄（同 work-orders.js） */
function renderIssueCell(issueLabelText, plainExplanationText) {
    const wrap = document.createElement('div');
    const main = document.createElement('div');
    main.className = 'fw-bold';
    main.textContent = issueLabelText || '';
    wrap.appendChild(main);

    if (plainExplanationText) {
        const sub = document.createElement('div');
        sub.className = 'text-muted small';
        sub.textContent = plainExplanationText;
        wrap.appendChild(sub);
    }
    return wrap;
}

function renderOrders(data) {
    const rows = data.items || [];
    orderRowsById.clear();
    for (const row of rows) orderRowsById.set(row.workOrderId, row);
    // 換頁／換篩選後，勾選中但已不在畫面上的單要跟著消失，否則按鈕數字對不上看得到的列
    for (const id of [...selectedOrderIds]) {
        if (!orderRowsById.has(id)) selectedOrderIds.delete(id);
    }

    const columns = [];

    if (canReply) {
        columns.push({
            title: '',
            className: 'text-center text-nowrap',
            renderHeader: () => {
                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.className = 'form-check-input';
                chk.title = '全選本頁交辦單';
                chk.checked = rows.length > 0 && rows.every(r => selectedOrderIds.has(r.workOrderId));
                chk.addEventListener('change', () => {
                    for (const r of rows) {
                        if (chk.checked) selectedOrderIds.add(r.workOrderId);
                        else selectedOrderIds.delete(r.workOrderId);
                    }
                    for (const rc of ordersEl.querySelectorAll('.handler-wo-select')) {
                        rc.checked = chk.checked;
                    }
                    updateReplyOrdersBtn();
                });
                return chk;
            },
            render: row => {
                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.className = 'form-check-input handler-wo-select';
                chk.checked = selectedOrderIds.has(row.workOrderId);
                chk.addEventListener('click', event => event.stopPropagation());
                chk.addEventListener('change', () => {
                    if (chk.checked) selectedOrderIds.add(row.workOrderId);
                    else selectedOrderIds.delete(row.workOrderId);
                    const selectAll = ordersEl.querySelector('thead input[type="checkbox"]');
                    if (selectAll) {
                        selectAll.checked = rows.length > 0 && rows.every(r => selectedOrderIds.has(r.workOrderId));
                    }
                    updateReplyOrdersBtn();
                });
                return chk;
            }
        });
    }

    columns.push(
        {
            title: '單號',
            className: 'text-nowrap',
            render: row => {
                const a = document.createElement('a');
                a.href = appUrl('/work-orders/' + row.workOrderId);
                a.textContent = '#' + row.workOrderId;
                return a;
            }
        },
        {
            title: '問題',
            render: row => renderIssueCell(row.issueLabel, row.plainExplanation)
        },
        {
            title: '成員',
            render: row => {
                const wrap = document.createElement('div');
                const counts = row.counts || {};
                const main = document.createElement('div');
                main.className = 'text-nowrap';
                main.textContent = `${counts.active ?? 0}／${counts.total ?? 0} 台`;
                wrap.appendChild(main);

                const parts = [];
                if (counts.inProgress > 0) parts.push(`處理中 ${counts.inProgress}`);
                if (counts.observing > 0) parts.push(`觀察 ${counts.observing}`);
                if (counts.open > 0) parts.push(`未處理 ${counts.open}`);
                if (counts.escalated > 0) parts.push(`上報 ${counts.escalated}`);
                if (counts.daySyncPending > 0) parts.push(`逐日同步中 ${counts.daySyncPending}`);

                if (parts.length > 0) {
                    const sub = document.createElement('div');
                    sub.className = 'text-muted small';
                    sub.textContent = parts.join('、');
                    wrap.appendChild(sub);
                }
                return wrap;
            }
        },
        {
            title: '逾期',
            className: 'text-end text-nowrap',
            render: row => {
                const overdue = row.counts?.overdue ?? 0;
                if (overdue > 0) return statusBadge(String(overdue), 'danger');
                const span = document.createElement('span');
                span.textContent = '0';
                return span;
            }
        },
        {
            title: '未回覆',
            className: 'text-end text-nowrap',
            render: row => (row.unrepliedDays == null ? '—' : `${row.unrepliedDays} 天`)
        },
        {
            title: '期限',
            className: 'text-nowrap',
            render: row => (row.dueDate ? formatDate(row.dueDate) : '—')
        },
        {
            title: '來源',
            render: row => statusBadge(ORIGIN_LABELS[row.origin] || row.origin || '未知', 'neutral')
        },
        {
            title: '最近新增',
            className: 'text-nowrap',
            render: row => (row.lastAppendedAt ? formatDateTime(row.lastAppendedAt) : '—')
        }
    );

    renderTable(ordersEl, {
        columns,
        rows,
        // 成員清單要另打一支 API，用 lazy 的 onRowExpand（首次展開才取），不是 rowDetail
        onRowExpand: (row, cell) => buildMemberPanel(row, cell),
        empty: { title: '目前沒有符合條件的交辦單' }
    });

    renderPagination(ordersPagerEl, {
        page: data.page,
        totalPages: Math.ceil(data.total / data.pageSize),
        pageSize: data.pageSize,
        onPage: newPage => {
            orderPage = newPage;
            guardLoad(ordersEl, loadOrders);
        },
        onPageSize: newSize => {
            savePageSize('handler-work-orders', newSize);
            orderPage = 1;
            guardLoad(ordersEl, loadOrders);
        }
    });

    renderPausedNote();
    updateReplyOrdersBtn();
}

function renderPausedNote() {
    // 勾「只看暫停的單」時表格列的就是那些單，再寫「另有 N 張」會自相矛盾
    const paused = pausedCheckbox.checked ? 0 : (summary?.pausedWorkOrders ?? 0);
    ordersNoteEl.textContent = paused > 0 ? `另有 ${paused} 張暫停（問題靜音中，到期自動恢復）` : '';
    ordersNoteEl.classList.toggle('d-none', paused === 0);
}

function updateReplyOrdersBtn() {
    replyOrdersBtn.classList.toggle('d-none', !canReply);
    replyOrdersBtn.disabled = selectedOrderIds.size === 0;
}

replyOrdersBtn.addEventListener('click', () => {
    if (selectedOrderIds.size === 0) return;

    const workOrderIds = [...selectedOrderIds];
    const hosts = workOrderIds.reduce((sum, id) => sum + (orderRowsById.get(id)?.counts?.active ?? 0), 0);
    // 選取的單都是同一個問題才帶問題名稱，不同問題混在一起時只帶主機數
    const labels = new Set(workOrderIds.map(id => orderRowsById.get(id)?.issueLabel));
    const [onlyLabel] = labels;
    const aiContext = labels.size === 1 && onlyLabel ? { issueLabel: onlyLabel, hostCount: hosts } : { hostCount: hosts };

    openWorkOrderReplyModal({
        title: '回覆選取的交辦單',
        targetText: `${workOrderIds.length} 張單共 ${hosts} 台`,
        draftKey: workOrdersDraftKey(workOrderIds),
        aiContext,
        submit: async payload => {
            const result = await api.post('/api/work-orders/reply-many', { workOrderIds, ...payload });
            toastReplyManyResult(result);
        },
        onApplied: () => guardLoad([kpiEl, ordersEl, casesEl, daysEl], load)
    });
});

/**
 * 展開列：該單的成員（主機）清單，可勾選後只回覆選取的幾台。
 * 勾選狀態是這一次展開的區域狀態——重新載入整頁時整張表重建，自然歸零。
 */
function buildMemberPanel(row, cell) {
    const box = document.createElement('div');
    box.className = 'p-2';

    const bar = document.createElement('div');
    bar.className = 'd-flex flex-wrap align-items-center gap-2 mb-2';

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label mb-0 text-muted small text-nowrap';
    statusLabel.textContent = '狀態';
    const memberStatus = document.createElement('select');
    memberStatus.className = 'form-select form-select-sm w-auto';
    for (const option of MEMBER_STATUS_OPTIONS) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        memberStatus.appendChild(el);
    }
    statusLabel.htmlFor = `handler-wo-member-status-${row.workOrderId}`;
    memberStatus.id = statusLabel.htmlFor;
    bar.append(statusLabel, memberStatus);

    const replyHostsBtn = document.createElement('button');
    replyHostsBtn.type = 'button';
    replyHostsBtn.className = 'btn btn-sm btn-outline-primary ms-auto';
    replyHostsBtn.textContent = '回覆選取的主機';
    replyHostsBtn.disabled = true;
    if (!canReply) replyHostsBtn.classList.add('d-none');
    bar.appendChild(replyHostsBtn);

    const tableBox = document.createElement('div');
    const pagerBox = document.createElement('nav');
    pagerBox.className = 'mt-2';
    const noteEl = document.createElement('div');
    noteEl.className = 'text-muted small mt-2 d-none';

    box.append(bar, tableBox, pagerBox, noteEl);
    cell.replaceChildren(box);

    const selectedCaseIds = new Set();
    let memberPage = 1;

    const updateReplyHostsBtn = () => {
        replyHostsBtn.disabled = selectedCaseIds.size === 0;
    };

    async function loadMembers() {
        renderLoading(tableBox, 3);
        const params = new URLSearchParams({
            status: memberStatus.value,
            page: String(memberPage),
            pageSize: String(MEMBER_PAGE_SIZE)
        });
        const data = await api.get(`/api/work-orders/${row.workOrderId}/members?${params}`);
        renderMembers(data);
    }

    function renderMembers(data) {
        const items = data.items || [];
        for (const id of [...selectedCaseIds]) {
            if (!items.some(i => i.caseId === id)) selectedCaseIds.delete(id);
        }

        noteEl.textContent = data.hiddenMemberCount > 0
            ? `另有 ${data.hiddenMemberCount} 台主機不在您的檢視範圍，未列出`
            : '';
        noteEl.classList.toggle('d-none', !(data.hiddenMemberCount > 0));

        const columns = [];
        if (canReply) {
            columns.push({
                title: '',
                className: 'text-center text-nowrap',
                renderHeader: () => {
                    const chk = document.createElement('input');
                    chk.type = 'checkbox';
                    chk.className = 'form-check-input';
                    chk.title = '全選本頁主機';
                    const selectable = items.filter(i => !i.closedAt);
                    chk.checked = selectable.length > 0 && selectable.every(i => selectedCaseIds.has(i.caseId));
                    chk.disabled = selectable.length === 0;
                    chk.addEventListener('change', () => {
                        for (const item of selectable) {
                            if (chk.checked) selectedCaseIds.add(item.caseId);
                            else selectedCaseIds.delete(item.caseId);
                        }
                        for (const rc of tableBox.querySelectorAll('.handler-wo-member-select')) {
                            rc.checked = chk.checked;
                        }
                        updateReplyHostsBtn();
                    });
                    return chk;
                },
                render: item => {
                    if (item.closedAt) return document.createTextNode('');
                    const chk = document.createElement('input');
                    chk.type = 'checkbox';
                    chk.className = 'form-check-input handler-wo-member-select';
                    chk.checked = selectedCaseIds.has(item.caseId);
                    chk.addEventListener('change', () => {
                        if (chk.checked) selectedCaseIds.add(item.caseId);
                        else selectedCaseIds.delete(item.caseId);
                        const selectAll = tableBox.querySelector('thead input[type="checkbox"]');
                        if (selectAll) {
                            const selectable = items.filter(i => !i.closedAt);
                            selectAll.checked = selectable.length > 0 && selectable.every(i => selectedCaseIds.has(i.caseId));
                        }
                        updateReplyHostsBtn();
                    });
                    return chk;
                }
            });
        }

        columns.push(
            {
                title: '主機',
                render: item => {
                    if (!item.hostId) {
                        const span = document.createElement('span');
                        span.textContent = item.hostName || '';
                        return span;
                    }
                    const a = document.createElement('a');
                    a.href = appUrl('/hosts/' + item.hostId);
                    a.textContent = item.hostName || '';
                    return a;
                }
            },
            {
                title: '狀態',
                className: 'text-nowrap',
                render: item => {
                    const meta = MEMBER_STATUS_META[item.status] || { label: item.status || '未知', variant: 'neutral' };
                    return statusBadge(meta.label, meta.variant);
                }
            },
            {
                title: '期限',
                className: 'text-nowrap',
                render: item => {
                    const wrap = document.createElement('div');
                    wrap.className = 'd-flex align-items-center gap-1 text-nowrap';
                    const span = document.createElement('span');
                    span.textContent = item.dueDate ? formatDate(item.dueDate) : '—';
                    wrap.appendChild(span);
                    if (item.overdue) wrap.appendChild(statusBadge('逾期', 'danger'));
                    return wrap;
                }
            },
            {
                title: '期間',
                className: 'text-nowrap',
                render: item => `${formatDate(item.firstLinkedDate)} ~ ${formatDate(item.lastLinkedDate)}`
            }
        );

        renderTable(tableBox, {
            columns,
            rows: items,
            empty: { title: '沒有符合條件的成員' }
        });

        renderPagination(pagerBox, {
            page: data.page,
            totalPages: Math.ceil(data.total / data.pageSize),
            onPage: newPage => {
                memberPage = newPage;
                guardLoad(tableBox, loadMembers);
            }
        });

        updateReplyHostsBtn();
    }

    memberStatus.addEventListener('change', () => {
        memberPage = 1;
        selectedCaseIds.clear();
        updateReplyHostsBtn();
        guardLoad(tableBox, loadMembers);
    });

    replyHostsBtn.addEventListener('click', () => {
        if (selectedCaseIds.size === 0) return;
        const caseIds = [...selectedCaseIds];
        const total = row.counts?.total ?? 0;

        openWorkOrderReplyModal({
            title: `回覆交辦單 #${row.workOrderId}`,
            targetText: `本單 ${total} 台中的 ${caseIds.length} 台`,
            draftKey: `order:${row.workOrderId}:cases:${[...caseIds].sort((a, b) => a - b).join(',')}`,
            aiContext: { issueLabel: row.issueLabel, hostCount: caseIds.length },
            submit: async payload => {
                const result = await api.post(`/api/work-orders/${row.workOrderId}/reply`, { caseIds, ...payload });
                toastReplyResult(result);
            },
            onApplied: () => guardLoad([kpiEl, ordersEl, casesEl, daysEl], load)
        });
    });

    guardLoad(tableBox, loadMembers);
}

// ── 依主機頁籤（逐案件平鋪）───────────────────────────────────────────────

function renderCases(cases) {
    renderTable(casesEl, {
        columns: [
            { title: '主機', render: c => hostLink(c) },
            { title: '問題', render: c => c.issueLabel },
            { title: '狀態', render: c => statusText(c) },
            { title: '涵蓋範圍', render: c => `${c.firstLinkedDate} ~ ${c.lastLinkedDate}` },
            { title: '預計完成', className: 'text-nowrap', render: c => dueCell(c) }
        ],
        rows: cases,
        // 點列 → 最近掛接日的風險日詳情（該頁有完整處理動線）；主機名稱另有連結可到主機頁
        rowHref: c => `/records/${c.hostId}/${c.lastLinkedDate}`,
        empty: { title: '目前沒有進行中案件', hint: '被指派問題並建立案件後，會顯示在這裡。' }
    });
}

// ── 被指派的風險日頁籤 ─────────────────────────────────────────────────────

function renderDays(days) {
    renderTable(daysEl, {
        columns: [
            { title: '日期', render: d => d.date },
            { title: '主機', render: d => hostLink(d) },
            { title: '風險', render: d => riskBadge(d.riskLevel) },
            { title: '狀態', render: d => statusText(d) },
            { title: '預計完成', className: 'text-nowrap', render: d => dueCell(d) }
        ],
        rows: days,
        rowHref: d => `/records/${d.hostId}/${d.date}`,
        empty: {
            title: includeResolvedDays ? '沒有被指派的風險日' : '目前沒有未結案的風險日',
            hint: includeResolvedDays ? '' : '勾選上方「顯示近 30 天已結案」檢視回顧紀錄。'
        }
    });
}

function statusText(item) {
    const span = document.createElement('span');
    span.textContent = item.derivedStatusText ?? item.statusText;
    if (item.isOverdue) span.className = 'text-danger fw-semibold';
    return span;
}

function dueCell(item) {
    if (!item.dueDate) return '';
    const span = document.createElement('span');
    span.className = item.isOverdue ? 'text-danger fw-semibold' : '';
    span.textContent = item.isOverdue ? `逾期 ${item.dueDate}` : item.dueDate;
    return span;
}

function hostLink(item) {
    const link = document.createElement('a');
    link.href = appUrl(`/hosts/${item.hostId}`);
    link.textContent = item.hostName;
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

// ── 事件綁定與初始化 ───────────────────────────────────────────────────────

const onOrderFilterChange = () => {
    orderPage = 1;
    selectedOrderIds.clear();
    guardLoad(ordersEl, loadOrders);
};
statusSelect.addEventListener('change', onOrderFilterChange);
sortSelect.addEventListener('change', onOrderFilterChange);
pausedCheckbox.addEventListener('change', onOrderFilterChange);

document.getElementById('toggle-resolved-days').addEventListener('change', event => {
    includeResolvedDays = event.target.checked;
    guardLoad([kpiEl, ordersEl, casesEl, daysEl], load);
});

bindTabs(document.getElementById('handler-tabs'));

guardLoad([kpiEl, ordersEl, casesEl, daysEl], load);
