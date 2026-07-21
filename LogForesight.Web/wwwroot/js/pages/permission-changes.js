/**
 * 權限異動待辦（docs/WEB-SPEC.md §9.5）。
 *
 * 每一筆逐項顯示「對象／異動類型／異動前／異動後」，
 * 對照 README「被異動項目明細（人工防護層）」的 console 輸出格式。
 */

import { api } from '../core/api.js';
import { renderLoading, renderEmpty, toast, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const modal = new bootstrap.Modal(document.getElementById('confirm-modal'));
const form = document.getElementById('confirm-form');

let currentStatus = 'pending';
let pendingConfirm = null;   // { change, targetStatus }

document.getElementById('perm-tabs').addEventListener('click', event => {
    const button = event.target.closest('[data-status]');
    if (!button) return;

    currentStatus = button.dataset.status;
    for (const tab of document.querySelectorAll('#perm-tabs .nav-link')) {
        tab.classList.toggle('active', tab === button);
    }
    load();
});

async function load() {
    const container = document.getElementById('perm-list');
    renderLoading(container, 4);

    const query = currentStatus ? `?status=${currentStatus}` : '';
    const changes = await api.get(`/api/permission-changes${query}`);

    if (changes.length === 0) {
        renderEmpty(container, {
            title: currentStatus === 'pending' ? '沒有待確認的權限異動' : '沒有符合條件的紀錄',
            hint: currentStatus === 'pending'
                ? '批次執行時若偵測到 ACL 或群組成員異動，會出現在這裡等待逐筆確認。'
                : ''
        });
        return;
    }

    container.replaceChildren();
    for (const change of changes) {
        container.appendChild(changeCard(change));
    }
}

function changeCard(change) {
    const card = document.createElement('section');
    card.className = 'lf-card mb-3';
    if (change.status === 'pending') card.classList.add('lf-card--warning');
    if (change.status === 'suspicious') card.classList.add('lf-card--critical');

    const header = document.createElement('div');
    header.className = 'lf-card__header';

    const title = document.createElement('div');
    title.className = 'd-flex align-items-center gap-2 flex-wrap';

    const type = document.createElement('span');
    type.className = 'fw-semibold';
    type.textContent = change.changeType;

    const host = document.createElement('span');
    host.className = 'text-muted small';
    host.textContent = `${change.hostName}　${formatDateTime(change.detectedAt)}`;

    title.append(type, host, statusBadge(change.status));
    header.appendChild(title);

    if (change.status === 'pending') {
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
        header.appendChild(actions);
    }

    const body = document.createElement('div');
    body.className = 'lf-card__body';
    body.appendChild(detailTable(change));

    if (change.status !== 'pending') {
        const confirmed = document.createElement('div');
        confirmed.className = 'small text-muted mt-3 pt-3 border-top';
        confirmed.textContent =
            `${change.status === 'authorized' ? '確認為授權操作' : '標記為可疑'}` +
            `　${change.confirmedByAccount}　${formatDateTime(change.confirmedAt)}` +
            (change.confirmNote ? `　說明：${change.confirmNote}` : '');
        body.appendChild(confirmed);
    }

    card.append(header, body);
    return card;
}

/** 異動前後對照——這是使用者判斷「這筆是否正常」的全部依據，不能折疊 */
function detailTable(change) {
    const wrap = document.createElement('div');

    const target = document.createElement('div');
    target.className = 'mb-2';
    const targetLabel = document.createElement('span');
    targetLabel.className = 'text-muted small me-2';
    targetLabel.textContent = '對象';
    const targetValue = document.createElement('span');
    targetValue.className = 'font-monospace';
    targetValue.textContent = change.target;
    target.append(targetLabel, targetValue);
    wrap.appendChild(target);

    const table = document.createElement('div');
    table.className = 'lf-table-wrap';
    table.innerHTML = `
        <table class="table table-sm mb-0">
            <tbody>
                <tr><th style="width:6rem">異動前</th><td class="font-monospace small"></td></tr>
                <tr><th>異動後</th><td class="font-monospace small"></td></tr>
            </tbody>
        </table>`;
    const cells = table.querySelectorAll('td');
    cells[0].textContent = change.before || '（無）';
    cells[1].textContent = change.after || '（無）';
    wrap.appendChild(table);

    const prompt = document.createElement('div');
    prompt.className = 'small text-muted mt-2';
    prompt.textContent = '此異動是否為您或授權人員的操作？若否，可能為入侵或誤設定，建議立即調查。';
    wrap.appendChild(prompt);

    return wrap;
}

function statusBadge(status) {
    const meta = {
        pending: { text: '待確認', variant: 'warning' },
        authorized: { text: '已確認授權', variant: 'success' },
        suspicious: { text: '可疑', variant: 'danger' }
    }[status] ?? { text: status, variant: 'secondary' };

    const span = document.createElement('span');
    span.className = `badge text-bg-${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

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

load();
