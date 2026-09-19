/**
 * 處理人員工作頁 `/handlers/{userId}`：處理人的工作單位是**交辦單**（一張單＝一個問題 ×
 * 一批主機），主區塊只有交辦單清單，列上就能「回覆」；「依主機」與「被指派的風險日」
 * 收進下方進階檢視，第一次展開才載入（workload API 只在那時呼叫）。
 *
 * 授權：全登入角色可查看任何人，資料以**檢視者**的可見範圍過濾（後端 WorkOrderQueryService／
 * HandlingService.GetHandlerWorkload），與全站查詢頁同一套模型，不新增能力。
 * 回覆與改期限只有「該單處理人本人且具 Handle」能做（後端 403 把關），前端據此顯示或隱藏——
 * 見 canReply，回覆按鈕、勾選框與期限連結都吃這個旗標。
 *
 * 回覆後就地更新（DESIGN-SYSTEM §6b）：只重打摘要與目前這一頁（refreshAfterReply），
 * 保留勾選、展開與捲動位置；勾選跨頁累積，改變篩選條件才清除。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    renderLoading,
    renderTable,
    renderEmpty,
    renderPagination,
    statCard,
    guardLoad,
    loadPageSize,
    savePageSize,
    toast
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
const selectedCountEl = document.getElementById('handler-wo-selected');
const advancedEl = document.getElementById('handler-advanced');

const ADVANCED_OPEN_KEY = 'lf-handler-advanced-open';

// 剛回覆而結案的列先淡化多久再重繪（讓人看得到「這張結案了」，不是突然消失）
const DONE_ROW_DELAY_MS = 1200;

// 處理人可排定的期限最遠天數（與後端 WorkOrderReplyService.MaxDueDateDays 相同）
const MAX_DUE_DATE_DAYS = 90;

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
let isSelf = false;                // 檢視自己的頁
let canReply = false;              // 本人檢視自己的頁且具 Handle
let summary = null;                // work-orders/summary
let orderPage = 1;
let orderTotalPages = 1;
let currentRows = [];              // 目前這一頁的單（清單排序），「下一張」依此找
const selectedOrderIds = new Set();
const orderRowsById = new Map();   // 目前這一頁的單
const selectedOrderMeta = new Map(); // 勾選中的單在勾選當下的台數與問題名稱（換頁後仍算得出台數）
const handledOrderIds = new Set(); // 這次在頁上回覆過的單：「下一張」跳過
let lastRefresh = Promise.resolve();
let advancedLoaded = false;

async function load() {
    renderLoading(kpiEl, 1);
    renderLoading(ordersEl, 5);

    const user = currentUser ?? await getCurrentUser();
    currentUser = user;
    isSelf = user?.userId === userId;
    canReply = isSelf && hasCapability(user, 'Handle');

    summary = await api.get(`/api/handlers/${userId}/work-orders/summary`);
    renderHeader(summary);
    renderKpi(summary);

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
        col.className = 'col-6 col-md-3';
        col.appendChild(statCard({ value: formatNumber(c.value), label: c.label, variant: c.variant }));
        kpiEl.appendChild(col);
    }
}

// ── 交辦單清單 ─────────────────────────────────────────────────────────────

/** 交辦單清單的請求序號（同 work-orders.js）：慢回來的舊請求不可蓋掉新篩選的結果 */
let ordersLoadSeq = 0;

function ordersUrl() {
    const pageSize = loadPageSize('handler-work-orders');
    const params = new URLSearchParams({
        status: statusSelect.value,
        sort: sortSelect.value,
        page: String(orderPage),
        pageSize: String(pageSize)
    });
    // 未勾選時不帶 paused：處理人視角的預設本來就是排除暫停單（後端決定）
    if (pausedCheckbox.checked) params.set('paused', 'only');
    return `/api/handlers/${userId}/work-orders?${params}`;
}

async function loadOrders() {
    const seq = ++ordersLoadSeq;
    renderLoading(ordersEl, 5);

    const data = await api.get(ordersUrl());
    if (seq !== ordersLoadSeq) return;
    renderOrders(data);
}

/**
 * 回覆成功後的就地更新：只重打摘要（KPI）與目前這一頁，不呼叫 workload、不顯示骨架列；
 * 重繪後恢復勾選（扣掉已結案的單）、先前展開的列與視窗捲動位置。
 * 進階檢視若已展開順便重載；收合中則標記過期，下次展開才重載。
 */
async function refreshAfterReply({ keepExpandedId = null } = {}) {
    const expanded = expandedOrderIds();
    if (keepExpandedId != null) expanded.add(keepExpandedId);
    const scrollY = window.scrollY;

    const seq = ++ordersLoadSeq;
    const [summaryData, data] = await Promise.all([
        api.get(`/api/handlers/${userId}/work-orders/summary`),
        api.get(ordersUrl())
    ]);
    if (seq !== ordersLoadSeq) return;

    summary = summaryData;
    renderKpi(summaryData);
    for (const row of data.items || []) {
        if (row.closedAt) unselectOrder(row.workOrderId);   // 結案處理：已結案的單不能再回覆
    }
    renderOrders(data);
    for (const id of expanded) expandOrderRow(id);
    window.scrollTo(0, scrollY);

    if (advancedLoaded) {
        if (advancedEl.open) guardLoad([casesEl, daysEl], loadAdvanced);
        else advancedLoaded = false;
    }
}

/** 回覆成功後排程就地更新；單因此結案且目前看的是「進行中」時，先淡化該列再重繪（不阻塞） */
function scheduleRefresh(workOrderId, closed, keepExpandedId = null) {
    const refresh = () => guardLoad(ordersEl, () => refreshAfterReply({ keepExpandedId }));
    if (!(closed && statusSelect.value === 'active')) return refresh();

    const tr = orderRowEl(workOrderId);
    if (tr) tr.classList.add('lf-row--done');
    return new Promise(resolve => {
        setTimeout(() => resolve(refresh()), DONE_ROW_DELAY_MS);
    });
}

/** 列的 tr：單號連結帶 data-wo-id */
function orderRowEl(workOrderId) {
    const anchor = ordersEl.querySelector(`a[data-wo-id="${workOrderId}"]`);
    return anchor ? anchor.closest('tr') : null;
}

function expandedOrderIds() {
    const ids = new Set();
    for (const anchor of ordersEl.querySelectorAll('a[data-wo-id]')) {
        if (anchor.closest('tr').getAttribute('aria-expanded') === 'true') ids.add(Number(anchor.dataset.woId));
    }
    return ids;
}

/** 展開某列的成員面板（已展開就不動）；回傳面板，列不在本頁回 null */
function expandOrderRow(workOrderId) {
    const tr = orderRowEl(workOrderId);
    if (!tr) return null;
    if (tr.getAttribute('aria-expanded') !== 'true') tr.click();
    return tr.nextElementSibling.querySelector('.handler-wo-member-panel');
}

function selectOrder(row) {
    selectedOrderIds.add(row.workOrderId);
    selectedOrderMeta.set(row.workOrderId, { active: row.counts.active, issueLabel: row.issueLabel });
}

function unselectOrder(id) {
    selectedOrderIds.delete(id);
    selectedOrderMeta.delete(id);
}

/** 勾選中的單的台數與問題名稱：本頁的用最新列，不在本頁的用勾選當下記的那份 */
function selectedInfo(id) {
    const row = orderRowsById.get(id);
    return row ? { active: row.counts.active, issueLabel: row.issueLabel } : selectedOrderMeta.get(id);
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
    currentRows = rows;
    orderTotalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
    orderRowsById.clear();
    for (const row of rows) orderRowsById.set(row.workOrderId, row);
    // 勾選跨頁保留：不在本頁的勾選不刪（按鈕旁另外交代「含其他頁 M 張」）
    // 可勾選的列（未結案）：全選與全選框的勾選狀態都只看這些
    const selectable = rows.filter(r => !r.closedAt);

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
                chk.checked = selectable.length > 0 && selectable.every(r => selectedOrderIds.has(r.workOrderId));
                chk.disabled = selectable.length === 0;
                chk.addEventListener('change', () => {
                    for (const r of selectable) {
                        if (chk.checked) selectOrder(r);
                        else unselectOrder(r.workOrderId);
                    }
                    for (const rc of ordersEl.querySelectorAll('.handler-wo-select:not(:disabled)')) {
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
                if (row.closedAt) {
                    chk.disabled = true;
                    chk.title = '已結案的交辦單無法回覆';
                    return chk;
                }
                chk.addEventListener('change', () => {
                    if (chk.checked) selectOrder(row);
                    else unselectOrder(row.workOrderId);
                    const selectAll = ordersEl.querySelector('thead input[type="checkbox"]');
                    if (selectAll) {
                        selectAll.checked = selectable.length > 0 && selectable.every(r => selectedOrderIds.has(r.workOrderId));
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
                a.dataset.woId = String(row.workOrderId);
                return a;
            }
        },
        {
            title: '問題',
            render: row => renderIssueCell(row.issueLabel, row.plainExplanation)
        },
        {
            title: '主機',
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
            render: row => dueDateCell(row)
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

    if (canReply) {
        columns.push({
            title: '',
            className: 'text-end',
            render: row => {
                if (row.closedAt) return '';
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'btn btn-sm btn-outline-primary text-nowrap';
                btn.textContent = '回覆';
                btn.addEventListener('click', () => replyOrder(row, null));
                return btn;
            }
        });
    }

    if (rows.length === 0) {
        renderOrdersEmpty();
    } else {
        renderTable(ordersEl, {
            columns,
            rows,
            // 成員清單要另打一支 API，用 lazy 的 onRowExpand（首次展開才取），不是 rowDetail
            onRowExpand: (row, cell) => buildMemberPanel(row, cell)
        });
    }

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

/**
 * 空狀態分流：本人頁看「進行中」而且一張都沒有時，要分得出「沒被授權任何主機」與「真的沒工作」；
 * 其他篩選（含只看暫停）沿用一般文字。
 */
function renderOrdersEmpty() {
    if (!(isSelf && statusSelect.value === 'active' && !pausedCheckbox.checked)) {
        renderEmpty(ordersEl, { title: '目前沒有符合條件的交辦單' });
        return;
    }
    if (summary.visibleHostCount === 0) {
        renderEmpty(ordersEl, { title: '尚未被授權任何主機', hint: '請聯絡系統管理員為你設定主機群組授權。' });
        return;
    }
    renderEmpty(ordersEl, { title: '目前沒有被指派的工作' });
    const link = document.createElement('a');
    link.href = appUrl('/');
    link.textContent = '前往總覽儀表板';
    const hint = document.createElement('div');
    hint.appendChild(link);
    ordersEl.firstElementChild.appendChild(hint);
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

    const total = selectedOrderIds.size;
    const offPage = [...selectedOrderIds].filter(id => !orderRowsById.has(id)).length;
    selectedCountEl.textContent = offPage > 0 ? `已選 ${total} 張（含其他頁 ${offPage} 張）` : `已選 ${total} 張`;
    selectedCountEl.classList.toggle('d-none', !canReply || total === 0);
}

replyOrdersBtn.addEventListener('click', () => {
    if (selectedOrderIds.size === 0) return;

    let workOrderIds = [...selectedOrderIds];
    const infos = workOrderIds.map(selectedInfo).filter(Boolean);
    const hosts = infos.reduce((sum, info) => sum + info.active, 0);
    // 選取的單都是同一個問題才帶問題名稱，不同問題混在一起時只帶主機數
    const labels = new Set(infos.map(info => info.issueLabel));
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
            // 回覆成功的單已處理完：取消勾選並記為處理過（「下一張」跳過）
            for (const id of result.succeeded || []) {
                unselectOrder(id);
                handledOrderIds.add(id);
            }
            // 部分失敗：只留失敗與未處理的單；彈窗保持開著（說明不必重打），再按送出就只送剩下的單。
            // 擲出例外讓彈窗不關閉、不清草稿（issue-status-reply 的 submit 失敗路徑只還原按鈕）；清單在背景就地更新
            if (result.failedWorkOrderId != null) {
                workOrderIds = [result.failedWorkOrderId, ...(result.notProcessed || [])];
                lastRefresh = scheduleRefresh(null, false);
                throw new Error('partial');
            }
        },
        onApplied: () => {
            lastRefresh = scheduleRefresh(null, false);
        }
    });
});

// ── 列上的「回覆」與下一張 ─────────────────────────────────────────────────

/**
 * 列上的「回覆」：只剩一台進行中 → 直接開單張回覆彈窗；多台 → 展開成員面板讓人勾選這次處理好的主機。
 * carry＝由「送出並回覆下一張」帶過來的上一張內容（沿用狀態、可帶入說明）。
 */
async function replyOrder(row, carry) {
    if (row.counts.active === 1) {
        const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '1' });
        let member;
        try {
            const data = await api.get(`/api/work-orders/${row.workOrderId}/members?${params}`);
            [member] = data.items || [];
        } catch {
            return;   // api.js 已出過錯誤 toast
        }
        if (!member) {
            toast('這張單已沒有進行中的主機', 'info');
            return;
        }
        openSingleReply(row, member, carry);
        return;
    }

    const panel = expandOrderRow(row.workOrderId);
    if (!panel) return;
    if (!panel.querySelector('.handler-wo-member-hint')) {
        const hint = document.createElement('div');
        hint.className = 'lf-hint mb-2 handler-wo-member-hint';
        hint.textContent = '勾選這次處理好的主機後按「回覆選取的主機」；全部處理好可按「全選」';
        panel.prepend(hint);
    }
    panel.focus();
}

function openSingleReply(row, member, carry) {
    let closedNow = false;
    openWorkOrderReplyModal({
        title: `回覆交辦單 #${row.workOrderId}`,
        targetText: `主機 ${member.hostName}`,
        draftKey: `order:${row.workOrderId}:cases:${member.caseId}`,
        aiContext: { issueLabel: row.issueLabel, hostCount: 1 },
        reuseIssueKey: member.issueKey || null,
        initialStatus: carry ? carry.status : undefined,
        previousNote: carry ? carry.note : null,
        submit: async payload => {
            const result = await api.post(`/api/work-orders/${row.workOrderId}/reply`, { caseIds: [member.caseId], ...payload });
            toastReplyResult(result);
            closedNow = result.workOrderClosed;
        },
        onApplied: () => {
            handledOrderIds.add(row.workOrderId);
            lastRefresh = scheduleRefresh(row.workOrderId, closedNow);
        },
        onNext: payload => openNextOrder(row.workOrderId, payload)
    });
}

/** 可當「下一張」的單：進行中、這次沒回覆過、沒被勾選（勾選的要走批次） */
function isNextCandidate(row, fromId) {
    return row.workOrderId !== fromId && !row.closedAt
        && !handledOrderIds.has(row.workOrderId) && !selectedOrderIds.has(row.workOrderId);
}

/**
 * 下一張＝目前清單排序中本張之後第一張候選；本頁沒有 → 等就地更新完，先看補進本頁的單，
 * 再換下一頁取第一張；都沒有就告知處理完。
 */
async function openNextOrder(fromId, carry) {
    const index = currentRows.findIndex(r => r.workOrderId === fromId);
    const seen = new Set(currentRows.map(r => r.workOrderId));
    const next = currentRows.slice(index + 1).find(r => isNextCandidate(r, fromId));
    if (next) {
        replyOrder(next, carry);
        return;
    }

    await lastRefresh;
    const joined = currentRows.find(r => !seen.has(r.workOrderId) && isNextCandidate(r, fromId));
    if (joined) {
        replyOrder(joined, carry);
        return;
    }

    for (let page = orderPage + 1; page <= orderTotalPages; page++) {
        orderPage = page;
        await guardLoad(ordersEl, loadOrders);
        const first = currentRows.find(r => isNextCandidate(r, fromId));
        if (first) {
            replyOrder(first, carry);
            return;
        }
    }
    toast('清單中的交辦單都已處理完', 'info');
}

// ── 期限就地修改 ───────────────────────────────────────────────────────────

function isoDate(date) {
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** 期限欄：本人且未結案時可點（無期限顯示「設定期限」），點了就地換成日期欄＋儲存／取消 */
function dueDateCell(row) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap align-items-center gap-1';

    const showValue = () => {
        const text = row.dueDate ? formatDate(row.dueDate) : '—';
        if (!canReply || row.closedAt) {
            wrap.replaceChildren(document.createTextNode(text));
            return;
        }
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'btn btn-link btn-sm p-0 text-nowrap';
        link.textContent = row.dueDate ? text : '設定期限';
        link.title = '修改這張單的期限';
        link.addEventListener('click', showEditor);
        wrap.replaceChildren(link);
    };

    const showEditor = () => {
        const today = new Date();
        const max = new Date();
        max.setDate(max.getDate() + MAX_DUE_DATE_DAYS);

        const input = document.createElement('input');
        input.type = 'date';
        input.className = 'form-control form-control-sm w-auto';
        input.min = isoDate(today);
        input.max = isoDate(max);
        input.value = row.dueDate ? String(row.dueDate).slice(0, 10) : '';
        input.setAttribute('aria-label', '期限');

        const save = document.createElement('button');
        save.type = 'button';
        save.className = 'btn btn-sm btn-outline-primary';
        save.textContent = '儲存';

        const cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'btn btn-sm btn-outline-secondary';
        cancel.textContent = '取消';
        cancel.addEventListener('click', showValue);

        save.addEventListener('click', async () => {
            save.disabled = true;
            try {
                const result = await api.put(`/api/work-orders/${row.workOrderId}/due-date`, { dueDate: input.value || null });
                row.dueDate = result.dueDate;
                toast('已更新期限', 'success');
                showValue();
            } catch {
                save.disabled = false;   // api.js 已出過錯誤 toast；留在編輯狀態讓人改
            }
        });

        wrap.replaceChildren(input, save, cancel);
        input.focus();
    };

    showValue();
    return wrap;
}

/**
 * 展開列：該單的成員（主機）清單，可勾選後只回覆選取的幾台。
 * 勾選狀態是這一次展開的區域狀態——就地更新重繪列時自然歸零。
 */
function buildMemberPanel(row, cell) {
    const box = document.createElement('div');
    box.className = 'p-2 handler-wo-member-panel';
    box.tabIndex = -1;

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

    // 勾選的主機：caseId → 問題簽章（全部相同時回覆彈窗才能「沿用此問題上次的說明」）
    const selectedCases = new Map();
    let memberPage = 1;

    const updateReplyHostsBtn = () => {
        replyHostsBtn.disabled = selectedCases.size === 0;
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
        for (const id of [...selectedCases.keys()]) {
            if (!items.some(i => i.caseId === id)) selectedCases.delete(id);
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
                    chk.checked = selectable.length > 0 && selectable.every(i => selectedCases.has(i.caseId));
                    chk.disabled = selectable.length === 0;
                    chk.addEventListener('change', () => {
                        for (const item of selectable) {
                            if (chk.checked) selectedCases.set(item.caseId, item.issueKey);
                            else selectedCases.delete(item.caseId);
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
                    chk.checked = selectedCases.has(item.caseId);
                    chk.addEventListener('change', () => {
                        if (chk.checked) selectedCases.set(item.caseId, item.issueKey);
                        else selectedCases.delete(item.caseId);
                        const selectAll = tableBox.querySelector('thead input[type="checkbox"]');
                        if (selectAll) {
                            const selectable = items.filter(i => !i.closedAt);
                            selectAll.checked = selectable.length > 0 && selectable.every(i => selectedCases.has(i.caseId));
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
            empty: { title: '沒有符合條件的主機' }
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
        selectedCases.clear();
        updateReplyHostsBtn();
        guardLoad(tableBox, loadMembers);
    });

    replyHostsBtn.addEventListener('click', () => {
        if (selectedCases.size === 0) return;
        const caseIds = [...selectedCases.keys()];
        const issueKeys = new Set(selectedCases.values());
        const total = row.counts?.total ?? 0;
        let closedNow = false;

        openWorkOrderReplyModal({
            title: `回覆交辦單 #${row.workOrderId}`,
            targetText: `本單 ${total} 台中的 ${caseIds.length} 台`,
            draftKey: `order:${row.workOrderId}:cases:${[...caseIds].sort((a, b) => a - b).join(',')}`,
            aiContext: { issueLabel: row.issueLabel, hostCount: caseIds.length },
            // 勾選的主機都是同一個問題簽章才能沿用上次的說明
            reuseIssueKey: issueKeys.size === 1 ? [...issueKeys][0] : null,
            submit: async payload => {
                const result = await api.post(`/api/work-orders/${row.workOrderId}/reply`, { caseIds, ...payload });
                toastReplyResult(result);
                closedNow = result.workOrderClosed;
            },
            onApplied: () => {
                handledOrderIds.add(row.workOrderId);
                lastRefresh = scheduleRefresh(row.workOrderId, closedNow, row.workOrderId);
            }
        });
    });

    guardLoad(tableBox, loadMembers);
}

// ── 進階檢視：依主機、被指派的風險日（第一次展開才載入）──────────────────

async function loadAdvanced() {
    advancedLoaded = true;
    renderLoading(casesEl, 3);
    renderLoading(daysEl, 3);
    const workload = await api.get(`/api/handlers/${userId}/workload?includeResolvedDays=${includeResolvedDays}`);
    renderCases(workload.cases);
    renderDays(workload.days);
}

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

/** 篩選、排序、只看暫停變更：回第一頁並清除全部勾選（勾選是對「這個條件下的清單」做的） */
const onOrderFilterChange = () => {
    orderPage = 1;
    const hadSelection = selectedOrderIds.size > 0;
    selectedOrderIds.clear();
    selectedOrderMeta.clear();
    if (hadSelection) toast('篩選條件已變更，已清除勾選', 'info');
    guardLoad(ordersEl, loadOrders);
};
statusSelect.addEventListener('change', onOrderFilterChange);
sortSelect.addEventListener('change', onOrderFilterChange);
pausedCheckbox.addEventListener('change', onOrderFilterChange);

document.getElementById('toggle-resolved-days').addEventListener('change', event => {
    includeResolvedDays = event.target.checked;
    guardLoad([casesEl, daysEl], loadAdvanced);
});

advancedEl.addEventListener('toggle', () => {
    try {
        localStorage.setItem(ADVANCED_OPEN_KEY, advancedEl.open ? '1' : '0');
    } catch {
        // 本機儲存區不可用（隱私模式等）：不記住展開狀態，不影響功能
    }
    if (advancedEl.open && !advancedLoaded) guardLoad([casesEl, daysEl], loadAdvanced);
});

try {
    // 設定 open 會觸發 toggle 事件，由上面的監聽負責第一次載入
    if (localStorage.getItem(ADVANCED_OPEN_KEY) === '1') advancedEl.open = true;
} catch {
    // 本機儲存區不可用：維持預設收合
}

guardLoad([kpiEl, ordersEl], load);
