/**
 * 群組與授權（docs/WEB-SPEC.md §9.8）。
 * 授權矩陣是這頁的核心：一眼看穿「哪個部門看得到哪些主機」。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast, confirmAction, withBusy } from '../core/ui.js';

const modal = new bootstrap.Modal(document.getElementById('group-modal'));
const form = document.getElementById('group-form');

let userGroups = [];
let hostGroups = [];
let matrix = null;
let editing = null;      // { kind: 'user' | 'host', group }

// ── 分頁切換 ─────────────────────────────────────────────────────────────────

document.getElementById('group-tabs').addEventListener('click', event => {
    const button = event.target.closest('[data-tab]');
    if (!button) return;

    for (const tab of document.querySelectorAll('#group-tabs .nav-link')) {
        tab.classList.toggle('active', tab === button);
    }
    for (const panel of document.querySelectorAll('[data-panel]')) {
        panel.classList.toggle('d-none', panel.dataset.panel !== button.dataset.tab);
    }
});

// ── 載入與渲染 ───────────────────────────────────────────────────────────────

async function load() {
    renderLoading(document.getElementById('user-group-list'), 3);
    renderLoading(document.getElementById('host-group-list'), 3);

    [userGroups, hostGroups, matrix] = await Promise.all([
        api.get('/api/admin/groups'),
        api.get('/api/admin/host-groups'),
        api.get('/api/admin/access')
    ]);

    renderUserGroups();
    renderHostGroups();
    renderMatrix();
}

function renderUserGroups() {
    renderTable(document.getElementById('user-group-list'), {
        columns: [
            { title: '群組名稱', render: g => g.groupName },
            { title: '角色', render: g => roleBadge(g.role) },
            { title: '成員數', className: 'text-end', render: g => String(g.memberCount) },
            { title: '狀態', render: g => activeBadge(g.active) },
            { title: '', className: 'text-end', render: g => groupActions('user', g) }
        ],
        rows: userGroups,
        empty: { title: '尚無使用者群組', hint: '系統內建的 admin / manager / dev 會在站台啟動時自動建立。' }
    });
}

function renderHostGroups() {
    renderTable(document.getElementById('host-group-list'), {
        columns: [
            { title: '群組名稱', render: g => g.groupName },
            { title: '主機數', className: 'text-end', render: g => String(g.hostCount) },
            { title: '狀態', render: g => activeBadge(g.active) },
            { title: '', className: 'text-end', render: g => groupActions('host', g) }
        ],
        rows: hostGroups,
        empty: { title: '尚無主機群組', hint: '可於「CSV 匯入」上傳主機時自動建立，或用右上角的「新增群組」建立。' }
    });
}

/**
 * 授權矩陣：列＝使用者群組、欄＝主機群組。
 * 勾選即時送出——這種「一次改一格」的操作若還要按儲存，很容易改完忘了按。
 */
function renderMatrix() {
    const container = document.getElementById('access-matrix');

    if (matrix.userGroups.length === 0 || matrix.hostGroups.length === 0) {
        renderEmpty(container, {
            title: '尚無可設定的授權',
            hint: '需要至少一個一般使用者群組與一個主機群組才能設定授權。'
        });
        return;
    }

    const wrap = document.createElement('div');
    wrap.className = 'lf-table-wrap';

    const table = document.createElement('table');
    table.className = 'table table-bordered align-middle mb-0';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    const corner = document.createElement('th');
    corner.textContent = '使用者群組 ＼ 主機群組';
    headRow.appendChild(corner);

    for (const hostGroup of matrix.hostGroups) {
        const th = document.createElement('th');
        th.className = 'text-center';
        th.textContent = hostGroup.groupName;
        headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const row of matrix.userGroups) {
        const tr = document.createElement('tr');

        const th = document.createElement('th');
        th.textContent = row.userGroupName;
        if (!row.active) {
            const hint = document.createElement('span');
            hint.className = 'badge text-bg-secondary ms-2';
            hint.textContent = '已停用';
            th.appendChild(hint);
        }
        tr.appendChild(th);

        for (const hostGroup of matrix.hostGroups) {
            const td = document.createElement('td');
            td.className = 'text-center';

            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.className = 'form-check-input';
            checkbox.checked = row.grantedHostGroupIds.includes(hostGroup.groupId);
            checkbox.setAttribute('aria-label', `${row.userGroupName} 可存取 ${hostGroup.groupName}`);
            checkbox.addEventListener('change', () => onToggleAccess(row, hostGroup, checkbox));

            td.appendChild(checkbox);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    wrap.appendChild(table);
    container.replaceChildren(wrap);
}

async function onToggleAccess(row, hostGroup, checkbox) {
    const granted = new Set(row.grantedHostGroupIds);
    if (checkbox.checked) granted.add(hostGroup.groupId);
    else granted.delete(hostGroup.groupId);

    checkbox.disabled = true;
    try {
        await api.put(`/api/admin/access/${row.userGroupId}`, { hostGroupIds: [...granted] });
        row.grantedHostGroupIds = [...granted];
        toast(
            checkbox.checked
                ? `已授權「${row.userGroupName}」檢視「${hostGroup.groupName}」`
                : `已取消「${row.userGroupName}」對「${hostGroup.groupName}」的授權`,
            'success', 2500);
    } catch {
        checkbox.checked = !checkbox.checked;   // 失敗時還原勾選狀態，避免畫面與實際不符
    } finally {
        checkbox.disabled = false;
    }
}

// ── 群組編輯 ─────────────────────────────────────────────────────────────────

function groupActions(kind, group) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-1 justify-content-end';

    const edit = document.createElement('button');
    edit.type = 'button';
    edit.className = 'btn btn-sm btn-outline-primary';
    edit.textContent = '編輯';
    edit.addEventListener('click', () => openModal(kind, group));
    wrap.appendChild(edit);

    // 系統內建群組不顯示刪除鈕：整套授權建立在這些群組上，刪掉就沒有依據了
    if (!(kind === 'user' && group.builtin)) {
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn btn-sm btn-outline-danger';
        remove.textContent = '刪除';
        remove.addEventListener('click', () => onDelete(kind, group));
        wrap.appendChild(remove);
    }

    return wrap;
}

function openModal(kind, group) {
    editing = { kind, group };

    document.getElementById('group-modal-title').textContent =
        group ? `編輯群組 ${group.groupName}` : (kind === 'user' ? '新增使用者群組' : '新增主機群組');
    document.getElementById('group-name').value = group?.groupName ?? '';
    document.getElementById('group-active').checked = group?.active ?? true;

    const roleField = document.getElementById('group-role-field');
    const roleSelect = document.getElementById('group-role');
    const roleHint = document.getElementById('group-role-hint');

    roleField.classList.toggle('d-none', kind !== 'user');
    if (kind === 'user') {
        roleSelect.value = group?.role ?? 'User';
        // builtin 群組可以改名（配合公司慣例），但角色不可改
        roleSelect.disabled = !!group?.builtin;
        roleHint.textContent = group?.builtin
            ? '系統內建群組的角色不可變更，但可以改名或停用。'
            : '';
    }

    modal.show();
}

form.addEventListener('submit', async event => {
    event.preventDefault();

    const name = document.getElementById('group-name').value.trim();
    if (!name) {
        toast('請輸入群組名稱', 'warning');
        return;
    }

    const saveButton = document.getElementById('group-save');
    const restore = withBusy(saveButton, '儲存中');

    try {
        const payload = {
            groupId: editing.group?.groupId ?? 0,
            groupName: name,
            active: document.getElementById('group-active').checked
        };

        if (editing.kind === 'user') {
            payload.role = document.getElementById('group-role').value;
            await api.post('/api/admin/groups', payload);
        } else {
            await api.post('/api/admin/host-groups', payload);
        }

        toast('已儲存群組', 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

async function onDelete(kind, group) {
    const count = kind === 'user' ? group.memberCount : group.hostCount;
    const unit = kind === 'user' ? '位成員' : '台主機';

    const confirmed = await confirmAction({
        title: '刪除群組',
        message: count > 0
            ? `「${group.groupName}」目前有 ${count} ${unit}，必須先移出才能刪除。`
            : `將刪除群組「${group.groupName}」，以及所有以它為對象的授權設定。此操作無法復原。`,
        confirmText: '刪除'
    });
    if (!confirmed) return;

    try {
        const url = kind === 'user'
            ? `/api/admin/groups/${group.groupId}`
            : `/api/admin/host-groups/${group.groupId}`;
        await api.delete(url);
        toast(`已刪除群組「${group.groupName}」`, 'success');
        await load();
    } catch {
        // 錯誤已由 api.js 顯示（含「還有成員」這類業務錯誤）
    }
}

document.querySelector('[data-new-user-group]').addEventListener('click', () => openModal('user', null));
document.querySelector('[data-new-host-group]').addEventListener('click', () => openModal('host', null));

function roleBadge(role) {
    const variants = { Admin: 'danger', Manager: 'primary', Dev: 'info', User: 'secondary' };
    const span = document.createElement('span');
    span.className = `badge text-bg-${variants[role] ?? 'secondary'}`;
    span.textContent = role;
    return span;
}

function activeBadge(active) {
    const span = document.createElement('span');
    span.className = `badge text-bg-${active ? 'success' : 'secondary'}`;
    span.textContent = active ? '啟用' : '停用';
    return span;
}

load();
