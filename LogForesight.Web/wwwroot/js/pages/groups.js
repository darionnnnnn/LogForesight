/**
 * 群組與授權（docs/WEB-SPEC.md §9.8）。
 * 授權矩陣是這頁的核心：一眼看穿「哪個部門看得到哪些主機」。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast, confirmAction, withBusy } from '../core/ui.js';

const modal = new bootstrap.Modal(document.getElementById('group-modal'));
const form = document.getElementById('group-form');
const membersModal = new bootstrap.Modal(document.getElementById('members-modal'));

let userGroups = [];
let hostGroups = [];
let matrix = null;
let editing = null;      // { kind: 'user' | 'host', group }
let membersState = null; // { groupId, mode, candidates }

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
            hint.className = 'lf-badge lf-badge--secondary ms-2';
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

    // 主機群組多一個「加入成員」——兩千台情境下逐台編輯不現實，改用網段/關鍵字批次加入
    if (kind === 'host') {
        const members = document.createElement('button');
        members.type = 'button';
        members.className = 'btn btn-sm btn-outline-secondary';
        members.textContent = '加入成員';
        members.addEventListener('click', () => openMembersModal(group));
        wrap.appendChild(members);
    }

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

// ── 批次加入成員（網段／關鍵字）─────────────────────────────────────────────

const membersInput = document.getElementById('members-input');

function openMembersModal(group) {
    membersState = { groupId: group.groupId, mode: 'cidr', candidates: [] };

    document.getElementById('members-group-name').textContent = group.groupName;
    membersInput.value = '';
    setMembersMode('cidr');
    document.getElementById('members-summary').textContent = '';
    document.getElementById('members-preview').replaceChildren();
    document.getElementById('members-remove-field').classList.add('d-none');
    document.getElementById('members-remove-others').checked = false;
    document.getElementById('members-apply').disabled = true;

    membersModal.show();
}

function setMembersMode(mode) {
    membersState.mode = mode;
    for (const btn of document.querySelectorAll('#members-mode [data-mode]')) {
        btn.classList.toggle('active', btn.dataset.mode === mode);
    }
    membersInput.placeholder = mode === 'cidr'
        ? '10.1.2.0/24、10.1.2.* 或單一 IP'
        : '主機名或 IP 關鍵字';
}

document.getElementById('members-mode').addEventListener('click', event => {
    const btn = event.target.closest('[data-mode]');
    if (btn) setMembersMode(btn.dataset.mode);
});

document.getElementById('members-search').addEventListener('click', searchMembers);
membersInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); searchMembers(); }
});

async function searchMembers() {
    const value = membersInput.value.trim();
    if (!value) { toast('請輸入網段或關鍵字', 'warning'); return; }

    const body = membersState.mode === 'cidr' ? { pattern: value } : { query: value };
    try {
        const preview = await api.post(`/api/admin/host-groups/${membersState.groupId}/members/preview`, body);
        membersState.candidates = preview.candidates;
        renderMembersPreview(preview);
    } catch {
        // 錯誤已由 api.js 顯示（含網段格式錯誤）
    }
}

function renderMembersPreview(preview) {
    const summary = document.getElementById('members-summary');
    const applyBtn = document.getElementById('members-apply');
    const removeField = document.getElementById('members-remove-field');

    if (preview.candidates.length === 0) {
        summary.textContent = '沒有命中任何主機。';
        document.getElementById('members-preview').replaceChildren();
        applyBtn.disabled = true;
        removeField.classList.add('d-none');
        return;
    }

    summary.textContent = `命中 ${preview.matchCount} 台` +
        (preview.inOtherGroupsCount > 0 ? `，其中 ${preview.inOtherGroupsCount} 台已屬其他群組` : '');

    // 已在本群組的預設不勾且不可選；其餘預設勾選
    renderTable(document.getElementById('members-preview'), {
        columns: [
            { title: '', className: 'text-center', render: c => memberCheckbox(c) },
            { title: '主機', render: c => c.hostName },
            { title: 'IP', render: c => c.ipAddress ?? '' },
            { title: '現有群組', render: c => currentGroupsCell(c) }
        ],
        rows: preview.candidates
    });

    removeField.classList.toggle('d-none', preview.inOtherGroupsCount === 0);
    updateMembersApplyState();
}

function memberCheckbox(candidate) {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'form-check-input';
    box.dataset.hostId = String(candidate.hostId);
    box.checked = !candidate.alreadyInTarget;
    box.disabled = candidate.alreadyInTarget;
    box.title = candidate.alreadyInTarget ? '已在本群組' : '';
    box.addEventListener('change', updateMembersApplyState);
    return box;
}

function currentGroupsCell(candidate) {
    const wrap = document.createElement('span');
    if (candidate.alreadyInTarget) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--success';
        badge.textContent = '已在本群組';
        wrap.appendChild(badge);
        return wrap;
    }
    // 已屬其他群組顯性通知（主色徽章），使用者才知道這台已經歸別的群組管
    for (const name of candidate.currentGroups) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--primary me-1';
        badge.textContent = name;
        wrap.appendChild(badge);
    }
    return wrap;
}

function selectedHostIds() {
    return Array.from(document.querySelectorAll('#members-preview input[type="checkbox"]:checked:not(:disabled)'))
        .map(box => Number(box.dataset.hostId));
}

function updateMembersApplyState() {
    document.getElementById('members-apply').disabled = selectedHostIds().length === 0;
}

document.getElementById('members-apply').addEventListener('click', async () => {
    const hostIds = selectedHostIds();
    if (hostIds.length === 0) return;

    const removeFromOthers = document.getElementById('members-remove-others').checked;
    const button = document.getElementById('members-apply');
    const restore = withBusy(button, '加入中');
    try {
        await api.post(`/api/admin/host-groups/${membersState.groupId}/members`, { hostIds, removeFromOthers });
        toast(`已將 ${hostIds.length} 台主機加入群組`, 'success');
        membersModal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

function roleBadge(role) {
    const variants = { Admin: 'danger', Manager: 'primary', Dev: 'info', User: 'secondary' };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${variants[role] ?? 'secondary'}`;
    span.textContent = role;
    return span;
}

function activeBadge(active) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${active ? 'success' : 'secondary'}`;
    span.textContent = active ? '啟用' : '停用';
    return span;
}

load();
