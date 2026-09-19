/**
 * 交辦單詳情頁（回饋第 47 輪）：
 * 包含單號標頭、成員清單、處理歷程時間軸與管理動作（改派、取消交辦、代為結案、拆出主機、複製 CSV）。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    renderTable,
    renderPagination,
    renderLoading,
    renderEmpty,
    guardLoad,
    toast,
    confirmActionWithReason,
    showDetailModal,
    withBusy,
    searchableUserSelect,
    labelValue
} from '../core/ui.js';
import { formatDate, formatDateTime, formatUserName, statusBadge, workOrderClosedReasonText } from '../core/format.js';

// DOM 元素
const rootEl = document.getElementById('wo-detail');
const titleEl = document.getElementById('wo-title');
const actionsContainer = document.getElementById('wo-actions');
const headerContainer = document.getElementById('wo-header');

const memberStatusSelect = document.getElementById('wo-member-status');
const memberCsvBtn = document.getElementById('wo-member-csv');
const memberSplitBtn = document.getElementById('wo-member-split');
const memberNoteEl = document.getElementById('wo-member-note');
const membersContainer = document.getElementById('wo-members');
const memberPagerContainer = document.getElementById('wo-member-pager');

const timelineContainer = document.getElementById('wo-timeline');

const workOrderId = Number(rootEl?.dataset.workOrderId);
const PAGE_SIZE = 50;

// 同 work-orders.js
const ORIGIN_LABELS = {
    manual: '人工交辦',
    owner_rule: '負責人規則',
    auto_dispatch: '自動派工',
    backfill: '系統整併',
    day_assign: '詳情頁指派'
};

// 問題層級狀態對照（同 issue-status-reply.js）
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

let currentUser = null;
let currentDetail = null;
let memberPage = 1;
const selectedCaseIds = new Set();
let assignableUsersCache = null;

// ── 成員狀態文字與徽章顏色（同一件事只寫一份）─────────────────────────────

function renderMemberStatusBadge(status) {
    const meta = MEMBER_STATUS_META[status] || { label: status || '未知', variant: 'neutral' };
    return statusBadge(meta.label, meta.variant);
}

// ── 處理人選單 modal（改派與拆出共用，同一件事只寫一份）───────────────────

async function getAssignableUsers() {
    if (!assignableUsersCache) {
        assignableUsersCache = await api.get('/api/admin/users', { silent: true });
    }
    return assignableUsersCache || [];
}

async function openSelectHandlerModal({ title, submitText, currentHandlerId, onSubmit }) {
    const users = await getAssignableUsers();
    // 停用帳號後端一定拒絕，不擺一個必定失敗的選項
    const filteredUsers = (users || []).filter(u => u.userId !== currentHandlerId && u.active);

    const body = document.createElement('div');
    const form = document.createElement('form');

    const label = document.createElement('label');
    label.className = 'form-label small text-muted';
    label.textContent = '選擇新處理人';

    const { element: selectWrap, getValue } = searchableUserSelect(filteredUsers, {
        selectedId: null,
        includeNone: true,
        noneLabel: '請選擇處理人…'
    });

    const submitBtn = document.createElement('button');
    submitBtn.type = 'submit';
    submitBtn.className = 'btn btn-sm btn-primary mt-3';
    submitBtn.textContent = submitText || '確定';

    form.append(label, selectWrap, submitBtn);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const selectedVal = getValue();
        if (!selectedVal) {
            toast('請選擇處理人', 'warning');
            return;
        }
        const handlerId = Number(selectedVal);
        const restore = withBusy(submitBtn, '處理中…');
        try {
            const closeModal = () => body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            await onSubmit(handlerId, closeModal);
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    showDetailModal({ title, body });
}

// ── 標頭渲染 ───────────────────────────────────────────────────────────────

function addHeaderField(grid, label, content) {
    const col = document.createElement('div');
    col.className = 'col-12 col-sm-6 col-lg-3';
    const [labelEl, valueEl] = labelValue(label, typeof content === 'string' ? content : '');
    if (content instanceof Node) {
        valueEl.textContent = '';
        valueEl.appendChild(content);
    }
    col.append(labelEl, valueEl);
    grid.appendChild(col);
}

function renderHeader(detail) {
    titleEl.textContent = `#${detail.workOrderId} ${detail.issueLabel || ''}`;

    const grid = document.createElement('div');
    grid.className = 'row g-3';

    // 處理人
    const handlerWrap = document.createElement('div');
    handlerWrap.className = 'd-flex align-items-center gap-1 flex-wrap';
    const handlerLink = document.createElement('a');
    handlerLink.href = appUrl('/handlers/' + detail.handlerId);
    handlerLink.textContent = detail.handlerName || '';
    handlerWrap.appendChild(handlerLink);
    if (!detail.handlerActive) {
        handlerWrap.appendChild(statusBadge('已停用', 'secondary'));
    }
    if (detail.handlerPaused) {
        handlerWrap.appendChild(statusBadge('暫停接單', 'warning'));
    }
    addHeaderField(grid, '處理人', handlerWrap);

    // 說明
    addHeaderField(grid, '說明', detail.plainExplanation || '—');

    // 來源
    const originWrap = document.createElement('div');
    originWrap.className = 'd-flex align-items-center gap-1 flex-wrap';
    originWrap.appendChild(statusBadge(ORIGIN_LABELS[detail.origin] || detail.origin || '未知', 'neutral'));
    if (detail.autoAttach) {
        originWrap.appendChild(statusBadge('自動續掛', 'info'));
    }
    addHeaderField(grid, '來源', originWrap);

    // 範圍
    const scopeKindLower = String(detail.scopeKind).toLowerCase();
    let scopeText;
    switch (scopeKindLower) {
        case 'all':
            scopeText = '全站';
            break;
        case 'groups':
            scopeText = detail.scopeGroups && detail.scopeGroups.length > 0
                ? detail.scopeGroups.map(g => g.groupName).join('、')
                : '主機群組';
            break;
        default:
            scopeText = '指定主機';
    }
    if (scopeKindLower !== 'hosts') {
        scopeText += detail.autoAttach ? '（續掛：開）' : '（續掛：關）';
    }
    addHeaderField(grid, '範圍', scopeText);

    // 成員
    const counts = detail.counts || {};
    const membersWrap = document.createElement('div');
    const membersMain = document.createElement('div');
    membersMain.className = 'text-nowrap';
    membersMain.textContent = `${counts.active ?? 0}／${counts.total ?? 0} 台`;
    membersWrap.appendChild(membersMain);

    const memberParts = [];
    if (counts.inProgress > 0) memberParts.push(`處理中 ${counts.inProgress}`);
    if (counts.observing > 0) memberParts.push(`觀察 ${counts.observing}`);
    if (counts.open > 0) memberParts.push(`未處理 ${counts.open}`);
    if (counts.escalated > 0) memberParts.push(`上報 ${counts.escalated}`);
    if (counts.daySyncPending > 0) memberParts.push(`逐日同步中 ${counts.daySyncPending} 台`);

    if (memberParts.length > 0) {
        const sub = document.createElement('div');
        sub.className = 'text-muted small';
        sub.textContent = memberParts.join('、');
        membersWrap.appendChild(sub);
    }
    addHeaderField(grid, '成員', membersWrap);

    // 期限
    addHeaderField(grid, '期限', detail.dueDate ? formatDate(detail.dueDate) : '—');

    // 建立
    const createdBy = detail.createdByAccount ? `（${detail.createdByAccount}）` : '（系統）';
    addHeaderField(grid, '建立', `${formatDateTime(detail.createdAt)} ${createdBy}`);

    // 最近新增
    addHeaderField(grid, '最近新增', detail.lastAppendedAt ? formatDateTime(detail.lastAppendedAt) : '—');

    // 最近回覆
    const lastReplyText = detail.lastReplyAt
        ? formatDateTime(detail.lastReplyAt)
        : (detail.unrepliedDays != null ? `尚未回覆（${detail.unrepliedDays} 天）` : '尚未回覆');
    addHeaderField(grid, '最近回覆', lastReplyText);

    // 交辦說明（有值才列）
    if (detail.note) {
        addHeaderField(grid, '交辦說明', detail.note);
    }

    // 狀態徽章
    const statusWrap = document.createElement('div');
    statusWrap.className = 'd-flex align-items-center gap-1 flex-wrap';
    if (detail.closedAt) {
        statusWrap.appendChild(statusBadge('已結案', 'neutral', { title: workOrderClosedReasonText(detail.closedReason) }));
    } else {
        if (detail.paused) {
            statusWrap.appendChild(statusBadge(`暫停（靜音至 ${detail.mutedUntil || ''}）`, 'warning'));
        }
        if (detail.resumedFromMuteAt) {
            statusWrap.appendChild(statusBadge(`${detail.resumedFromMuteAt} 自靜音恢復`, 'info'));
        }
        if (!detail.paused && !detail.resumedFromMuteAt) {
            statusWrap.appendChild(statusBadge('進行中', 'primary'));
        }
    }
    addHeaderField(grid, '狀態', statusWrap);

    headerContainer.replaceChildren(grid);
}

// ── 動作按鈕 ───────────────────────────────────────────────────────────────

// 處理人回覆的入口在處理人工作頁（/handlers/{id}），詳情頁只放管理動作

function openAdminCloseModal() {
    const body = document.createElement('div');
    const form = document.createElement('form');

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label small text-muted';
    statusLabel.textContent = '結案狀態';

    const statusSelect = document.createElement('select');
    statusSelect.className = 'form-select form-select-sm mb-3';
    const closeStatuses = [
        { value: 'resolved', label: '已處理' },
        { value: 'wont_fix', label: '不處理' },
        { value: 'false_positive', label: '誤報' },
        { value: 'known_noise', label: '已知雜訊' }
    ];
    for (const opt of closeStatuses) {
        const option = document.createElement('option');
        option.value = opt.value;
        option.textContent = opt.label;
        statusSelect.appendChild(option);
    }

    const reasonLabel = document.createElement('label');
    reasonLabel.className = 'form-label small text-muted';
    reasonLabel.textContent = '結案原因（必填）';

    const reasonInput = document.createElement('textarea');
    reasonInput.className = 'form-control form-control-sm mb-3';
    reasonInput.rows = 3;
    reasonInput.required = true;

    const submitBtn = document.createElement('button');
    submitBtn.type = 'submit';
    submitBtn.className = 'btn btn-sm btn-primary';
    submitBtn.textContent = '確定結案';

    form.append(statusLabel, statusSelect, reasonLabel, reasonInput, submitBtn);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const reason = reasonInput.value.trim();
        if (!reason) {
            toast('請填寫結案原因', 'warning');
            reasonInput.focus();
            return;
        }

        const restore = withBusy(submitBtn, '處理中…');
        try {
            const res = await api.post(`/api/work-orders/${workOrderId}/admin-close`, {
                status: statusSelect.value,
                reason
            });
            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            toast(`已代為結案 ${res.closedCases} 台`, 'success');
            await reloadAll();
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    showDetailModal({ title: '代為結案', body });
}

function renderActions(detail, user) {
    actionsContainer.replaceChildren();

    if (!detail.viewerCanAssign || detail.closedAt) {
        return;
    }

    // 改派按鈕
    const reassignBtn = document.createElement('button');
    reassignBtn.type = 'button';
    reassignBtn.className = 'btn btn-sm btn-outline-primary';
    reassignBtn.textContent = '改派';
    reassignBtn.addEventListener('click', () => {
        openSelectHandlerModal({
            title: '改派交辦單',
            submitText: '確定改派',
            currentHandlerId: detail.handlerId,
            onSubmit: async (handlerId, closeModal) => {
                const res = await api.post(`/api/work-orders/${workOrderId}/reassign`, { handlerId });
                closeModal();
                if (res.mergedIntoExisting) {
                    toast(`已改派 ${res.movedCases} 台，併入單號 #${res.targetWorkOrderId}`, 'success');
                } else {
                    toast(`已改派 ${res.movedCases} 台`, 'success');
                }
                if (res.assigneeCannotHandle && res.assigneeCannotHandle.length > 0) {
                    for (const item of res.assigneeCannotHandle) {
                        toast(`${item.handlerName} 沒有處理權限（${item.hostCount} 台）`, 'warning');
                    }
                }
                if (res.targetWorkOrderId !== workOrderId) {
                    location.href = appUrl('/work-orders/' + res.targetWorkOrderId);
                } else {
                    await reloadAll();
                }
            }
        });
    });
    actionsContainer.appendChild(reassignBtn);

    // 取消交辦按鈕
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-sm btn-outline-danger';
    cancelBtn.textContent = '取消交辦';
    cancelBtn.addEventListener('click', async () => {
        const reason = await confirmActionWithReason({
            title: '取消交辦',
            message: '取消後這張單的進行中主機會調回未處理，處理人會收到通知。',
            reasonLabel: '取消原因',
            confirmText: '確定取消',
            confirmVariant: 'danger'
        });
        if (!reason) return;

        const restore = withBusy(cancelBtn, '處理中…');
        try {
            const res = await api.post(`/api/work-orders/${workOrderId}/cancel`, { reason });
            toast(`已取消，${res.closedCases} 台調回未處理`, 'success');
            await reloadAll();
        } catch {
            // 錯誤訊息已由 api.js 顯示
            restore();
        }
    });
    actionsContainer.appendChild(cancelBtn);

    // 代為結案按鈕
    if (hasCapability(user, 'Assign') && hasCapability(user, 'Handle')) {
        const closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'btn btn-sm btn-outline-secondary';
        closeBtn.textContent = '代為結案';
        closeBtn.addEventListener('click', () => {
            openAdminCloseModal();
        });
        actionsContainer.appendChild(closeBtn);
    }
}

// ── 成員表 ─────────────────────────────────────────────────────────────────

function updateSplitBtn() {
    const canSplit = currentDetail && currentDetail.viewerCanAssign && !currentDetail.closedAt;
    if (!canSplit) {
        memberSplitBtn.classList.add('d-none');
        memberSplitBtn.disabled = true;
        return;
    }
    memberSplitBtn.classList.remove('d-none');
    memberSplitBtn.disabled = selectedCaseIds.size === 0;
}

function renderMembers(data) {
    if (data.hiddenMemberCount > 0) {
        memberNoteEl.textContent = `另有 ${data.hiddenMemberCount} 台主機不在您的檢視範圍，未列出`;
        memberNoteEl.classList.remove('d-none');
    } else {
        memberNoteEl.textContent = '';
        memberNoteEl.classList.add('d-none');
    }

    const canSplit = currentDetail && currentDetail.viewerCanAssign && !currentDetail.closedAt;
    const columns = [];

    if (canSplit) {
        columns.push({
            title: '',
            className: 'text-center text-nowrap',
            renderHeader: () => {
                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.className = 'form-check-input';
                chk.title = '全選可拆出主機';
                const splitableItems = (data.items || []).filter(item => !item.closedAt);
                chk.checked = splitableItems.length > 0 && splitableItems.every(item => selectedCaseIds.has(item.caseId));
                chk.disabled = splitableItems.length === 0;
                chk.addEventListener('change', () => {
                    for (const item of splitableItems) {
                        if (chk.checked) {
                            selectedCaseIds.add(item.caseId);
                        } else {
                            selectedCaseIds.delete(item.caseId);
                        }
                    }
                    const rowCheckboxes = membersContainer.querySelectorAll('.wo-member-select');
                    for (const rc of rowCheckboxes) {
                        rc.checked = chk.checked;
                    }
                    updateSplitBtn();
                });
                return chk;
            },
            render: item => {
                if (item.closedAt) {
                    return document.createTextNode('');
                }
                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.className = 'form-check-input wo-member-select';
                chk.checked = selectedCaseIds.has(item.caseId);
                chk.addEventListener('change', () => {
                    if (chk.checked) {
                        selectedCaseIds.add(item.caseId);
                    } else {
                        selectedCaseIds.delete(item.caseId);
                    }
                    updateSplitBtn();
                    const selectAll = membersContainer.querySelector('thead input[type="checkbox"]');
                    if (selectAll) {
                        const splitableItems = (data.items || []).filter(i => !i.closedAt);
                        selectAll.checked = splitableItems.length > 0 && splitableItems.every(i => selectedCaseIds.has(i.caseId));
                    }
                });
                return chk;
            }
        });
    }

    columns.push(
        {
            title: '主機',
            render: item => {
                if (item.hostId) {
                    const a = document.createElement('a');
                    a.href = appUrl('/hosts/' + item.hostId);
                    a.textContent = item.hostName || '';
                    return a;
                }
                const span = document.createElement('span');
                span.textContent = item.hostName || '';
                return span;
            }
        },
        {
            title: '狀態',
            className: 'text-nowrap',
            render: item => renderMemberStatusBadge(item.status)
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
                if (item.overdue) {
                    wrap.appendChild(statusBadge('逾期', 'danger'));
                }
                return wrap;
            }
        },
        {
            title: '期間',
            className: 'text-nowrap',
            render: item => {
                const span = document.createElement('span');
                span.textContent = `${formatDate(item.firstLinkedDate)} ~ ${formatDate(item.lastLinkedDate)}`;
                return span;
            }
        },
        {
            title: '附註',
            className: 'text-nowrap',
            render: item => {
                const notes = [];
                if (item.daySyncPending) notes.push('逐日同步中');
                if (item.cancelled) notes.push('已取消');
                if (item.closedAt) notes.push(`結案 ${formatDateTime(item.closedAt)}`);
                const span = document.createElement('span');
                span.className = 'text-muted small';
                span.textContent = notes.join('、') || '—';
                return span;
            }
        }
    );

    renderTable(membersContainer, {
        columns,
        rows: data.items,
        empty: { title: '沒有符合條件的成員' }
    });

    renderPagination(memberPagerContainer, {
        page: data.page,
        totalPages: Math.ceil(data.total / data.pageSize),
        pageSize: data.pageSize,
        onPage: newPage => {
            memberPage = newPage;
            selectedCaseIds.clear();
            updateSplitBtn();
            guardLoad(membersContainer, loadMembersOnly);
        }
    });

    updateSplitBtn();
}

// ── 時間軸 ─────────────────────────────────────────────────────────────────

/**
 * 事件註記轉畫面用語：Core 不持有顯示文字，註記裡留的是原始值——
 * 回覆與代為結案是「狀態碼：說明」，改派是「舊處理人 id→新處理人 id」。
 * 兩者都換成看得懂的文字，其餘原樣顯示。
 */
function eventNoteText(note) {
    const move = note.match(/^(\d+)→(\d+)$/);
    if (move) {
        return `${handlerNameOf(move[1])} → ${handlerNameOf(move[2])}`;
    }
    // 「狀態碼：說明（N 台）」與不填說明的「狀態碼（N 台）」：開頭的狀態碼緊接全形冒號或左括號
    const code = note.match(/^([a-z_]+)(?=[：（])/);
    const meta = code ? MEMBER_STATUS_META[code[1]] : null;
    return meta ? meta.label + note.slice(code[1].length) : note;
}

/** 改派註記用：使用者清單載入前（或查不到）就顯示原始 id，不擋畫面 */
function handlerNameOf(userId) {
    const user = (assignableUsersCache || []).find(u => String(u.userId) === String(userId));
    return user ? formatUserName(user.displayName, user.account) : `#${userId}`;
}

function renderTimeline(timeline) {
    if (!timeline || timeline.length === 0) {
        renderEmpty(timelineContainer, { title: '尚無歷程' });
        return;
    }

    const items = timeline.slice().sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
    const list = document.createElement('div');
    list.className = 'list-group list-group-flush';

    for (const ev of items) {
        const item = document.createElement('div');
        item.className = 'list-group-item px-0 py-2';

        const topRow = document.createElement('div');
        topRow.className = 'd-flex flex-wrap align-items-center gap-2';

        const timeSpan = document.createElement('span');
        timeSpan.className = 'text-muted small text-nowrap';
        timeSpan.textContent = formatDateTime(ev.createdAt);
        topRow.appendChild(timeSpan);

        const actorSpan = document.createElement('span');
        actorSpan.className = 'fw-semibold text-nowrap';
        actorSpan.textContent = ev.actorName || '系統';
        topRow.appendChild(actorSpan);

        const actionSpan = document.createElement('span');
        actionSpan.textContent = ev.actionText || '';
        topRow.appendChild(actionSpan);

        if (ev.memberDelta !== 0) {
            const deltaBadge = document.createElement('span');
            deltaBadge.className = 'badge bg-light text-dark border';
            deltaBadge.textContent = ev.memberDelta > 0 ? `+${ev.memberDelta} 台` : `−${Math.abs(ev.memberDelta)} 台`;
            topRow.appendChild(deltaBadge);
        }

        item.appendChild(topRow);

        if (ev.note) {
            const noteRow = document.createElement('div');
            noteRow.className = 'text-muted small mt-1 ps-2 border-start';
            noteRow.textContent = eventNoteText(ev.note);
            item.appendChild(noteRow);
        }

        list.appendChild(item);
    }

    timelineContainer.replaceChildren(list);
}

// ── 事件綁定 ───────────────────────────────────────────────────────────────

memberStatusSelect.addEventListener('change', () => {
    memberPage = 1;
    selectedCaseIds.clear();
    updateSplitBtn();
    guardLoad(membersContainer, loadMembersOnly);
});

memberSplitBtn.addEventListener('click', () => {
    if (selectedCaseIds.size === 0) return;
    openSelectHandlerModal({
        title: '拆出選取的主機',
        submitText: '確定拆出',
        currentHandlerId: currentDetail?.handlerId,
        onSubmit: async (handlerId, closeModal) => {
            const caseIds = Array.from(selectedCaseIds);
            const res = await api.post(`/api/work-orders/${workOrderId}/split`, { caseIds, handlerId });
            closeModal();
            toast(`已拆出 ${res.movedCases} 台到單號 #${res.targetWorkOrderId}`, 'success');
            if (res.assigneeCannotHandle && res.assigneeCannotHandle.length > 0) {
                for (const item of res.assigneeCannotHandle) {
                    toast(`${item.handlerName} 沒有處理權限（${item.hostCount} 台）`, 'warning');
                }
            }
            selectedCaseIds.clear();
            updateSplitBtn();
            await reloadAll();
        }
    });
});

memberCsvBtn.addEventListener('click', async () => {
    const restore = withBusy(memberCsvBtn, '複製中…');
    try {
        const status = memberStatusSelect.value;
        let page = 1;
        const allItems = [];
        while (true) {
            const res = await api.get(`/api/work-orders/${workOrderId}/members?status=${status}&page=${page}&pageSize=200`);
            if (res.items && res.items.length > 0) {
                allItems.push(...res.items);
            }
            // 以後端實際採用的頁大小判斷（後端會夾上限），不假設等於請求值
            if (!res.items || res.items.length < res.pageSize || allItems.length >= res.total) {
                break;
            }
            page++;
        }

        const header = ['主機', '狀態', '期限', '逾期', '首次關聯', '最近關聯', '附註'];
        const escapeCsv = val => {
            const str = val == null ? '' : String(val);
            if (str.includes(',') || str.includes('"') || str.includes('\n') || str.includes('\r')) {
                return `"${str.replace(/"/g, '""')}"`;
            }
            return str;
        };

        const rows = [header.join(',')];
        for (const item of allItems) {
            const statusText = MEMBER_STATUS_META[item.status]?.label || item.status || '';
            const dueText = item.dueDate ? formatDate(item.dueDate) : '';
            const overdueText = item.overdue ? '是' : '否';
            const firstLinked = item.firstLinkedDate ? formatDate(item.firstLinkedDate) : '';
            const lastLinked = item.lastLinkedDate ? formatDate(item.lastLinkedDate) : '';
            const notes = [];
            if (item.daySyncPending) notes.push('逐日同步中');
            if (item.cancelled) notes.push('已取消');
            if (item.closedAt) notes.push(`結案 ${formatDateTime(item.closedAt)}`);
            const noteText = notes.join('、');

            rows.push([
                escapeCsv(item.hostName),
                escapeCsv(statusText),
                escapeCsv(dueText),
                escapeCsv(overdueText),
                escapeCsv(firstLinked),
                escapeCsv(lastLinked),
                escapeCsv(noteText)
            ].join(','));
        }

        const csvContent = rows.join('\r\n');
        try {
            await navigator.clipboard.writeText(csvContent);
            toast(`已複製 ${allItems.length} 台`, 'success');
        } catch {
            toast('瀏覽器不允許寫入剪貼簿', 'danger');
        }
    } catch {
        // api.js 已跳出錯誤提示
    } finally {
        restore();
    }
});

// ── 載入流程 ───────────────────────────────────────────────────────────────

/** 成員清單的請求序號：連點分頁或連續換篩選時，慢回來的舊請求不可蓋掉新結果 */
let membersLoadSeq = 0;

async function loadMembersOnly() {
    const seq = ++membersLoadSeq;
    renderLoading(membersContainer, 5);
    const members = await api.get(`/api/work-orders/${workOrderId}/members?status=${memberStatusSelect.value}&page=${memberPage}&pageSize=${PAGE_SIZE}`);
    if (seq !== membersLoadSeq) return;
    renderMembers(members);
}

async function loadAll() {
    try {
        const [user, detail, members, timeline] = await Promise.all([
            getCurrentUser(),
            api.get(`/api/work-orders/${workOrderId}`),
            api.get(`/api/work-orders/${workOrderId}/members?status=${memberStatusSelect.value}&page=${memberPage}&pageSize=${PAGE_SIZE}`),
            api.get(`/api/work-orders/${workOrderId}/timeline`)
        ]);

        currentUser = user;
        currentDetail = detail;

        renderHeader(detail);
        renderActions(detail, user);
        renderMembers(members);
        // 改派註記要把處理人 id 換成名字，先確保使用者清單在手（取不到就顯示 id，不擋畫面）
        if (timeline.some(ev => /^\d+→\d+$/.test(ev.note || ''))) {
            try {
                await getAssignableUsers();
            } catch {
                // 沒有使用者清單權限時維持原始 id
            }
        }
        renderTimeline(timeline);
    } catch (error) {
        if (error?.status === 403) {
            renderEmpty(headerContainer, {
                title: '沒有權限',
                hint: error.message || '您沒有權限檢視這張交辦單。',
                icon: 'exclamation-triangle'
            });
            const wrap = document.createElement('div');
            wrap.className = 'mt-3';
            const a = document.createElement('a');
            a.className = 'btn btn-sm btn-outline-secondary';
            a.href = appUrl('/work-orders');
            a.textContent = '返回交辦清單';
            wrap.appendChild(a);
            headerContainer.appendChild(wrap);
            membersContainer.replaceChildren();
            timelineContainer.replaceChildren();
            actionsContainer.replaceChildren();
            return;
        }
        throw error;
    }
}

async function reloadAll() {
    selectedCaseIds.clear();
    await guardLoad([headerContainer, membersContainer, timelineContainer], loadAll, { backLink: { href: appUrl('/work-orders'), text: '返回交辦清單' } });
}

function init() {
    renderLoading(headerContainer, 3);
    renderLoading(membersContainer, 5);
    renderLoading(timelineContainer, 3);
    guardLoad([headerContainer, membersContainer, timelineContainer], loadAll, { backLink: { href: appUrl('/work-orders'), text: '返回交辦清單' } });
}

init();
