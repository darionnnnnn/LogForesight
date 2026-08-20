/**
 * 權限異動待辦（docs/WEB-SPEC.md §9.5）。
 *
 * 每一筆逐項顯示「時間／主機 (IP)／帳號／類別／異動說明／狀態」，
 * 支援伺服器端排序、分頁與展開檢視完整異動明細。
 */

import { api } from '../core/api.js';
import {
    renderTable, renderLoading, renderPagination, loadPageSize, savePageSize,
    guardLoad, toast, withBusy, headerWithHelp, toggleAllTableDetails
} from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

const modal = new bootstrap.Modal(document.getElementById('confirm-modal'));
const form = document.getElementById('confirm-form');

let currentStatus = 'pending';
let currentPage = 1;
let pageSize = loadPageSize('permissionChanges');
let sort = { key: 'detectedAt', dir: 'desc' };
let lastResult = null;
let isAllExpanded = false;
let pendingConfirm = null;   // { change, targetStatus }

document.getElementById('perm-tabs').addEventListener('click', event => {
    const button = event.target.closest('[data-status]');
    if (!button) return;

    currentStatus = button.dataset.status;
    for (const tab of document.querySelectorAll('#perm-tabs .nav-link')) {
        tab.classList.toggle('active', tab === button);
    }
    currentPage = 1;
    load();
});

/** 組裝查詢參數（供清單查詢共用） */
function buildQueryParams(overrides = {}) {
    const params = new URLSearchParams();
    if (currentStatus) {
        params.set('status', currentStatus);
    }
    params.set('sort', sort.key || 'detectedAt');
    params.set('dir', sort.dir || 'desc');
    params.set('page', String(overrides.page ?? currentPage));
    params.set('pageSize', String(overrides.pageSize ?? pageSize));
    return params;
}

async function load() {
    const container = document.getElementById('perm-list');
    renderLoading(container, 5);

    const params = buildQueryParams();
    lastResult = await api.get(`/api/permission-changes?${params}`);
    render();
}

function render() {
    const total = lastResult.total;
    const countEl = document.getElementById('perm-count');
    if (countEl) {
        countEl.textContent = total > 0 ? `共 ${total} 筆` : '';
    }

    isAllExpanded = false;
    updateExpandButtonState();

    renderTable(document.getElementById('perm-list'), {
        columns: [
            {
                key: 'detectedAt',
                title: '時間',
                sortKey: 'detectedAt',
                sortDefaultDir: 'desc',
                renderHeader: () => headerWithHelp('時間', '本機監控來源的時間語意為系統偵測比對時間，非事件實際發生時間。', '時間說明'),
                render: row => formatDateTime(row.detectedAt)
            },
            {
                key: 'hostName',
                title: '主機 (IP)',
                sortKey: 'hostName',
                sortDefaultDir: 'asc',
                render: row => hostCell(row)
            },
            {
                title: '帳號',
                render: row => accountCell(row)
            },
            {
                key: 'category',
                title: '類別',
                sortKey: 'category',
                sortDefaultDir: 'asc',
                render: row => categoryCell(row)
            },
            {
                title: '異動說明',
                render: row => summaryCell(row)
            },
            {
                key: 'status',
                title: '狀態',
                sortKey: 'status',
                sortDefaultDir: 'asc',
                render: row => statusBadge(row.status)
            }
        ],
        rows: lastResult.items,
        rowDetail: row => detailView(row),
        sort,
        onSort: (key, dir) => {
            sort = { key, dir };
            currentPage = 1;
            load();
        },
        empty: {
            title: currentStatus === 'pending' ? '沒有待確認的權限異動' : '沒有符合條件的紀錄',
            hint: currentStatus === 'pending'
                ? '批次執行時若偵測到 ACL 或群組成員異動，會出現在這裡等待逐筆確認。'
                : '請切換狀態頁籤以檢視其他紀錄。'
        }
    });

    renderPager();
}

function hostCell(row) {
    const wrap = document.createElement('div');
    const hostName = document.createElement('div');
    hostName.className = 'fw-semibold';
    hostName.textContent = row.hostName;
    wrap.appendChild(hostName);

    if (row.hostIp) {
        const ip = document.createElement('div');
        ip.className = 'text-muted small';
        ip.textContent = row.hostIp;
        wrap.appendChild(ip);
    }

    return wrap;
}

function accountCell(row) {
    const wrap = document.createElement('div');
    wrap.className = 'small';

    const opRow = document.createElement('div');
    const opLabel = document.createElement('span');
    opLabel.className = 'text-muted me-1';
    opLabel.textContent = '操作者：';
    const opVal = document.createElement('span');
    opVal.textContent = row.initiatorAccount || '—';
    opRow.append(opLabel, opVal);

    const targetRow = document.createElement('div');
    const targetLabel = document.createElement('span');
    targetLabel.className = 'text-muted me-1';
    targetLabel.textContent = '目標：';
    const targetVal = document.createElement('span');
    targetVal.textContent = row.targetAccount || '—';
    targetRow.append(targetLabel, targetVal);

    wrap.append(opRow, targetRow);
    return wrap;
}

function categoryCell(row) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex align-items-center gap-1 flex-wrap';

    const catBadge = document.createElement('span');
    catBadge.className = 'lf-badge lf-badge--secondary';
    catBadge.textContent = row.categoryLabel || row.category || '其他';
    wrap.appendChild(catBadge);

    if (row.isPrivilegedTarget) {
        const privBadge = document.createElement('span');
        privBadge.className = 'lf-badge lf-badge--danger';
        privBadge.textContent = '高風險';
        wrap.appendChild(privBadge);
    }

    return wrap;
}

function summaryCell(row) {
    const div = document.createElement('div');
    div.className = 'text-truncate';
    div.style.maxWidth = '360px';
    div.textContent = row.summaryText || '—';
    div.title = row.summaryText || '';
    return div;
}

function statusBadge(status) {
    const meta = {
        pending: { text: '待確認', variant: 'warning' },
        authorized: { text: '已確認授權', variant: 'success' },
        suspicious: { text: '可疑', variant: 'danger' }
    }[status] ?? { text: status || '—', variant: 'secondary' };

    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

function detailView(change) {
    const wrap = document.createElement('div');
    wrap.className = 'p-3 bg-light rounded border';

    const metaGrid = document.createElement('div');
    metaGrid.className = 'row g-2 mb-3 small';

    const colAlert = document.createElement('div');
    colAlert.className = 'col-12';
    const alertLabel = document.createElement('span');
    alertLabel.className = 'text-muted me-2';
    alertLabel.textContent = '行為說明：';
    const alertVal = document.createElement('span');
    alertVal.className = 'fw-medium';
    alertVal.textContent = change.alertText || '—';
    colAlert.append(alertLabel, alertVal);

    const colTarget = document.createElement('div');
    colTarget.className = 'col-md-6';
    const targetLabel = document.createElement('span');
    targetLabel.className = 'text-muted me-2';
    targetLabel.textContent = '對象：';
    const targetVal = document.createElement('span');
    targetVal.className = 'font-monospace';
    targetVal.textContent = change.target || '—';
    colTarget.append(targetLabel, targetVal);

    const colSource = document.createElement('div');
    colSource.className = 'col-md-3';
    const sourceLabel = document.createElement('span');
    sourceLabel.className = 'text-muted me-2';
    sourceLabel.textContent = '來源：';
    const sourceVal = document.createElement('span');
    sourceVal.textContent = change.source || '本機監控';
    colSource.append(sourceLabel, sourceVal);

    const colEventId = document.createElement('div');
    colEventId.className = 'col-md-3';
    const eventIdLabel = document.createElement('span');
    eventIdLabel.className = 'text-muted me-2';
    eventIdLabel.textContent = 'EventId：';
    const eventIdVal = document.createElement('span');
    eventIdVal.className = 'font-monospace';
    eventIdVal.textContent = change.eventId != null ? String(change.eventId) : '—';
    colEventId.append(eventIdLabel, eventIdVal);

    metaGrid.append(colAlert, colTarget, colSource, colEventId);
    wrap.appendChild(metaGrid);

    const diffWrap = document.createElement('div');
    diffWrap.className = 'lf-table-wrap mb-3';
    diffWrap.innerHTML = `
        <table class="table table-sm bg-white mb-0 border">
            <tbody>
                <tr><th style="width:6rem" class="bg-light text-muted">異動前</th><td class="font-monospace small"></td></tr>
                <tr><th class="bg-light text-muted">異動後</th><td class="font-monospace small"></td></tr>
            </tbody>
        </table>`;
    const cells = diffWrap.querySelectorAll('td');
    cells[0].textContent = change.before || '（無）';
    cells[1].textContent = change.after || '（無）';
    wrap.appendChild(diffWrap);

    if (change.status === 'pending') {
        const actionArea = document.createElement('div');
        actionArea.className = 'd-flex align-items-center justify-content-between flex-wrap gap-2 pt-2 border-top';

        const prompt = document.createElement('div');
        prompt.className = 'small text-muted';
        prompt.textContent = '此異動是否為您或授權人員的操作？若否，可能為入侵或誤設定，建議立即調查。';

        const actions = document.createElement('div');
        actions.className = 'd-flex gap-2';

        const authorized = document.createElement('button');
        authorized.type = 'button';
        authorized.className = 'btn btn-sm btn-outline-success';
        authorized.textContent = '確認為授權操作';
        authorized.addEventListener('click', () => openConfirm(change, 'authorized'));

        const suspicious = document.createElement('button');
        suspicious.type = 'button';
        suspicious.className = 'btn btn-sm btn-outline-danger';
        suspicious.textContent = '標記可疑';
        suspicious.addEventListener('click', () => openConfirm(change, 'suspicious'));

        actions.append(authorized, suspicious);
        actionArea.append(prompt, actions);
        wrap.appendChild(actionArea);
    } else {
        const confirmedArea = document.createElement('div');
        confirmedArea.className = 'small text-muted pt-2 border-top row g-2';

        const resultCol = document.createElement('div');
        resultCol.className = 'col-md-3';
        const resLabel = document.createElement('span');
        resLabel.className = 'text-muted me-1';
        resLabel.textContent = '確認結果：';
        const resVal = document.createElement('span');
        resVal.className = 'fw-medium';
        resVal.textContent = change.status === 'authorized' ? '已確認授權' : '標記為可疑';
        resultCol.append(resLabel, resVal);

        const userCol = document.createElement('div');
        userCol.className = 'col-md-3';
        const uLabel = document.createElement('span');
        uLabel.className = 'text-muted me-1';
        uLabel.textContent = '確認人：';
        const uVal = document.createElement('span');
        uVal.textContent = formatUserName(change.confirmedByDisplayName, change.confirmedByAccount) || '—';
        userCol.append(uLabel, uVal);

        const timeCol = document.createElement('div');
        timeCol.className = 'col-md-3';
        const tLabel = document.createElement('span');
        tLabel.className = 'text-muted me-1';
        tLabel.textContent = '確認時間：';
        const tVal = document.createElement('span');
        tVal.textContent = change.confirmedAt ? formatDateTime(change.confirmedAt) : '—';
        timeCol.append(tLabel, tVal);

        const noteCol = document.createElement('div');
        noteCol.className = 'col-md-3';
        const nLabel = document.createElement('span');
        nLabel.className = 'text-muted me-1';
        nLabel.textContent = '確認說明：';
        const nVal = document.createElement('span');
        nVal.textContent = change.confirmNote || '—';
        noteCol.append(nLabel, nVal);

        confirmedArea.append(resultCol, userCol, timeCol, noteCol);
        wrap.appendChild(confirmedArea);
    }

    return wrap;
}

function renderPager() {
    const totalPages = Math.ceil(lastResult.total / lastResult.pageSize);
    renderPagination(document.getElementById('perm-pager'), {
        page: lastResult.page,
        totalPages,
        onPage: page => {
            currentPage = page;
            load();
            window.scrollTo({ top: 0, behavior: 'smooth' });
        },
        pageSize,
        onPageSize: size => {
            pageSize = size;
            savePageSize('permissionChanges', size);
            currentPage = 1;
            load();
        }
    });
}

function updateExpandButtonState() {
    const btn = document.getElementById('btn-toggle-expand');
    if (!btn) return;
    const hasRows = lastResult && lastResult.items && lastResult.items.length > 0;
    btn.disabled = !hasRows;
    btn.textContent = isAllExpanded ? '全部收合' : '全部展開';
}

document.getElementById('btn-toggle-expand')?.addEventListener('click', () => {
    const container = document.getElementById('perm-list');
    const rows = container.querySelectorAll('tbody tr.lf-row-expandable');
    if (rows.length === 0) return;
    const hasCollapsed = Array.from(rows).some(r => r.getAttribute('aria-expanded') !== 'true');
    isAllExpanded = toggleAllTableDetails(container, hasCollapsed);
    updateExpandButtonState();
});

document.getElementById('perm-list')?.addEventListener('click', e => {
    if (e.target.closest('tr.lf-row-expandable')) {
        setTimeout(() => {
            const rows = document.querySelectorAll('#perm-list tbody tr.lf-row-expandable');
            if (rows.length > 0) {
                isAllExpanded = Array.from(rows).every(r => r.getAttribute('aria-expanded') === 'true');
                updateExpandButtonState();
            }
        }, 0);
    }
});

function openConfirm(change, targetStatus) {
    pendingConfirm = { change, targetStatus };

    document.getElementById('confirm-modal-title').textContent =
        targetStatus === 'authorized' ? '確認為授權操作' : '標記為可疑';

    const detail = document.getElementById('confirm-detail');
    detail.replaceChildren();
    const summary = document.createElement('div');
    summary.className = 'alert alert-light border mb-0 small';
    summary.textContent = `${change.hostName}｜${change.changeType}｜${change.target}`;
    detail.appendChild(summary);

    const note = document.getElementById('confirm-note');
    note.value = '';
    note.required = targetStatus === 'suspicious';

    // 標記可疑必須說明——那是要交給別人接手調查的訊號
    document.getElementById('confirm-note-hint').textContent = targetStatus === 'suspicious'
        ? '必填：請說明可疑之處，供後續調查參考。'
        : '選填：可記錄這是誰在什麼情況下做的變更。';

    const submit = document.getElementById('confirm-submit');
    submit.className = `btn btn-${targetStatus === 'authorized' ? 'success' : 'danger'}`;
    submit.textContent = targetStatus === 'authorized' ? '確認為授權操作' : '標記可疑';

    modal.show();
}

form.addEventListener('submit', async event => {
    event.preventDefault();

    const note = document.getElementById('confirm-note').value.trim();
    if (pendingConfirm.targetStatus === 'suspicious' && !note) {
        toast('標記為可疑時請填寫說明', 'warning');
        return;
    }

    const submit = document.getElementById('confirm-submit');
    const restore = withBusy(submit, '處理中');

    try {
        await api.put(`/api/permission-changes/${pendingConfirm.change.changeId}/confirm`, {
            status: pendingConfirm.targetStatus,
            note: note || null
        });

        toast(pendingConfirm.targetStatus === 'authorized' ? '已確認為授權操作' : '已標記為可疑', 'success');
        modal.hide();
        await load();
    } catch {
        restore();
    }
});

guardLoad(document.getElementById('perm-list'), load);
