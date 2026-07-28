/**
 * 使用者維護（docs/WEB-SPEC.md §9.8）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, withBusy, renderChips, confirmAction, checkboxList, button } from '../core/ui.js';

const listContainer = document.getElementById('user-list');
const searchInput = document.getElementById('user-search');
const sortSelect = document.getElementById('user-sort');
const modalElement = document.getElementById('user-modal');
const form = document.getElementById('user-form');
const modal = new bootstrap.Modal(modalElement);

let users = [];
let groups = [];
let editingUser = null;

// 新增筆數（docs/HISTORY.md #7）：'single' | 'batch'，只在「新增使用者」時可切換，
// 編輯既有使用者一律走單筆（帳號本來就不可改，多筆模式沒有意義）
let createMode = 'single';

// chip 篩選狀態（§5.1 D-2）：狀態/角色單選，群組多選
let statusFilter = '';
let roleFilter = '';
const groupFilter = new Set();

async function load() {
    renderLoading(listContainer, 5);

    [users, groups] = await Promise.all([
        api.get('/api/admin/users'),
        api.get('/api/admin/groups')
    ]);

    setupToolbar();
    render();
}

/** 角色 chip 的選項來自現有群組的 role 去重——群組是實際存在的，不會出現選了也沒結果的角色 */
function setupToolbar() {
    renderChips(document.getElementById('user-status-chips'), {
        items: [{ value: '', label: '全部' }, { value: 'active', label: '啟用' }, { value: 'inactive', label: '停用' }],
        attr: 'status',
        activeValues: [statusFilter],
        multi: false,
        onToggle: value => { statusFilter = value; render(); }
    });

    const roles = [...new Set(groups.map(g => g.role))];
    renderChips(document.getElementById('user-role-chips'), {
        items: [{ value: '', label: '全部' }, ...roles.map(r => ({ value: r, label: r }))],
        attr: 'role',
        activeValues: [roleFilter],
        multi: false,
        onToggle: value => { roleFilter = value; render(); }
    });

    renderChips(document.getElementById('user-group-chips'), {
        items: groups.map(g => ({ value: String(g.groupId), label: g.groupName })),
        attr: 'group',
        activeValues: [...groupFilter],
        multi: true,
        onToggle: (value, active) => {
            if (active) groupFilter.add(value); else groupFilter.delete(value);
            render();
        }
    });
}

function sortUsers(list) {
    const sorted = [...list];
    if (sortSelect.value === 'displayName') {
        sorted.sort((a, b) => (a.displayName || a.account).localeCompare(b.displayName || b.account, 'zh-Hant'));
    } else {
        sorted.sort((a, b) => a.account.localeCompare(b.account));
    }
    return sorted;
}

function render() {
    const keyword = searchInput.value.trim().toLowerCase();
    const groupRoleOf = new Map(groups.map(g => [g.groupId, g.role]));

    let rows = keyword
        ? users.filter(u =>
            u.account.toLowerCase().includes(keyword) ||
            (u.displayName ?? '').toLowerCase().includes(keyword))
        : users;

    if (statusFilter === 'active') rows = rows.filter(u => u.active);
    if (statusFilter === 'inactive') rows = rows.filter(u => !u.active);
    if (roleFilter) rows = rows.filter(u => u.groupIds.some(id => groupRoleOf.get(id) === roleFilter));
    if (groupFilter.size > 0) rows = rows.filter(u => u.groupIds.some(id => groupFilter.has(String(id))));

    rows = sortUsers(rows);
    document.getElementById('user-count').textContent = `共 ${rows.length} 位`;

    renderTable(listContainer, {
        columns: [
            { title: '帳號', render: u => u.account },
            { title: '顯示名稱', render: u => u.displayName },
            { title: 'Email', render: u => u.email ?? '' },
            { title: '群組', render: u => renderGroupBadges(u) },
            { title: '狀態', render: u => renderActiveBadge(u.active) },
            { title: '', className: 'text-end', render: u => renderEditButton(u) }
        ],
        rows,
        empty: users.length === 0
            ? { title: '尚無使用者', hint: '可於「CSV 匯入」批次建立，或用右上角的「新增使用者」逐筆新增。' }
            : { title: '沒有符合搜尋條件的使用者', hint: '請調整關鍵字後再試。' }
    });
}

function renderGroupBadges(user) {
    if (!user.groupNames || user.groupNames.length === 0) {
        const span = document.createElement('span');
        span.className = 'text-muted';
        // 沒有群組 = 登入後什麼都看不到，這是需要被注意的狀態而不是普通的空值
        span.textContent = '未指派（無任何權限）';
        return span;
    }

    const wrap = document.createElement('span');
    for (const name of user.groupNames) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--light border me-1';
        badge.textContent = name;
        wrap.appendChild(badge);
    }
    return wrap;
}

function renderActiveBadge(active) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${active ? 'success' : 'secondary'}`;
    span.textContent = active ? '啟用' : '停用';
    return span;
}

function renderEditButton(user) {
    return button('編輯', { variant: 'outline-primary', onClick: () => openModal(user) });
}

function openModal(user) {
    editingUser = user;

    document.getElementById('user-modal-title').textContent = user ? `編輯 ${user.account}` : '新增使用者';
    document.getElementById('user-account').value = user?.account ?? '';
    document.getElementById('user-account').disabled = !!user;   // 帳號是自然鍵，建立後不可改
    document.getElementById('user-accounts-batch').value = '';
    document.getElementById('user-display-name').value = user?.displayName ?? '';
    document.getElementById('user-email').value = user?.email ?? '';
    document.getElementById('user-active').checked = user?.active ?? true;

    // 多筆模式只在「新增使用者」提供——編輯既有使用者的帳號本來就不可改，多筆沒有意義
    const modeToggle = document.getElementById('user-mode-toggle');
    modeToggle.classList.toggle('d-none', !!user);
    setCreateMode('single');

    renderGroupCheckboxes(user);
    modal.show();
}

/** 切換單筆／多筆：多筆模式隱藏顯示名稱與 Email——後端固定顯示名稱＝帳號、Email 留空 */
function setCreateMode(mode) {
    createMode = mode;

    for (const button of document.querySelectorAll('#user-mode-toggle button')) {
        button.classList.toggle('active', button.dataset.mode === mode);
    }

    const isBatch = mode === 'batch';
    document.getElementById('user-account-single-group').classList.toggle('d-none', isBatch);
    document.getElementById('user-account-batch-group').classList.toggle('d-none', !isBatch);
    document.getElementById('user-display-name-group').classList.toggle('d-none', isBatch);
    document.getElementById('user-email-group').classList.toggle('d-none', isBatch);
}

for (const button of document.querySelectorAll('#user-mode-toggle button')) {
    button.addEventListener('click', () => setCreateMode(button.dataset.mode));
}

function renderGroupCheckboxes(user) {
    checkboxList(document.getElementById('user-groups'), groups.map(group => ({
        id: group.groupId,
        label: group.builtin
            ? `${group.groupName}（${group.role}．系統內建）`
            : `${group.groupName}（${group.role}）`,
        checked: user?.groupIds?.includes(group.groupId) ?? false
    })), '尚無群組，請先於「群組與授權」建立。');
}

/** 多筆模式的帳號解析：一行一個，也接受同一行用逗號分隔多個——與後端 NormalizeBatchAccounts 對齊的寬鬆解析 */
function parseBatchAccounts(text) {
    return text
        .split(/[\n,]/)
        .map(a => a.trim())
        .filter(a => a.length > 0);
}

form.addEventListener('submit', async event => {
    event.preventDefault();

    const selectedGroupIds = Array.from(document.querySelectorAll('#user-groups input:checked'))
        .map(input => Number(input.value));
    const active = document.getElementById('user-active').checked;
    const saveButton = document.getElementById('user-save');

    if (createMode === 'batch' && !editingUser) {
        await submitBatch(selectedGroupIds, active, saveButton);
        return;
    }

    const account = document.getElementById('user-account').value.trim();
    if (!account) {
        toast('請輸入帳號', 'warning');
        return;
    }

    const restore = withBusy(saveButton, '儲存中');

    try {
        const saved = await api.post('/api/admin/users', {
            account,
            displayName: document.getElementById('user-display-name').value.trim(),
            email: document.getElementById('user-email').value.trim() || null,
            active
        });

        // 群組是獨立端點：儲存基本資料不會動到權限，權限異動一律留下自己的稽核紀錄
        await api.put(`/api/admin/users/${saved.userId}/groups`, { groupIds: selectedGroupIds });

        toast(editingUser ? '已更新使用者' : '已新增使用者', 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤訊息已由 api.js 以 toast 顯示
    } finally {
        restore();
    }
});

/**
 * 多筆新增（docs/HISTORY.md #7）：送出前先比對頁面已載入的使用者清單，
 * 發現已存在帳號時跳確認，讓使用者決定「跳過已存在」或「以這次勾選的群組覆蓋其權限」；
 * 前端這一步只是 UX 提示，後端仍以自己當下的查詢結果為準（避免兩人同時操作的競態）。
 */
async function submitBatch(groupIds, active, saveButton) {
    const accounts = parseBatchAccounts(document.getElementById('user-accounts-batch').value);
    if (accounts.length === 0) {
        toast('請輸入至少一個帳號', 'warning');
        return;
    }

    const existingAccounts = new Set(users.map(u => u.account.toLowerCase()));
    const conflicts = [...new Set(accounts.filter(a => existingAccounts.has(a.toLowerCase())))];

    let overwriteExisting = false;
    if (conflicts.length > 0) {
        overwriteExisting = await confirmAction({
            title: '部分帳號已存在',
            message: `以下帳號已存在：${conflicts.join('、')}。點「覆蓋權限」會用這次勾選的群組取代這些帳號原有的群組；` +
                '點「取消」則保留原樣，只新增其餘尚未存在的帳號。',
            confirmText: '覆蓋權限',
            confirmVariant: 'primary'
        });
    }

    const restore = withBusy(saveButton, '儲存中');
    try {
        const result = await api.post('/api/admin/users/batch', {
            accounts,
            groupIds,
            active,
            overwriteExisting
        });

        const parts = [`新增 ${result.created.length} 筆`];
        if (result.overwritten.length > 0) parts.push(`覆蓋 ${result.overwritten.length} 筆（${result.overwritten.join('、')}）`);
        if (result.skipped.length > 0) parts.push(`跳過 ${result.skipped.length} 筆（${result.skipped.join('、')}）`);
        if (result.invalidCount > 0) parts.push(`格式不合 ${result.invalidCount} 筆`);

        toast(parts.join('、'), 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤訊息已由 api.js 以 toast 顯示
    } finally {
        restore();
    }
}

document.getElementById('btn-new-user').addEventListener('click', () => openModal(null));
searchInput.addEventListener('input', render);
sortSelect.addEventListener('change', render);

load();
