/**
 * 主機維護（docs/WEB-SPEC.md §9.8）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const listContainer = document.getElementById('host-list');
const searchInput = document.getElementById('host-search');
const form = document.getElementById('host-form');
const modal = new bootstrap.Modal(document.getElementById('host-modal'));

let hosts = [];
let hostGroups = [];
let users = [];
let editingHost = null;

async function load() {
    renderLoading(listContainer, 5);

    [hosts, hostGroups, users] = await Promise.all([
        api.get('/api/admin/hosts'),
        api.get('/api/admin/host-groups'),
        api.get('/api/admin/users')
    ]);

    render();
}

function render() {
    const keyword = searchInput.value.trim().toLowerCase();
    const rows = keyword
        ? hosts.filter(h =>
            h.hostName.toLowerCase().includes(keyword) ||
            (h.ipAddress ?? '').toLowerCase().includes(keyword))
        : hosts;

    renderTable(listContainer, {
        columns: [
            { title: '主機名稱', render: h => hostNameCell(h) },
            { title: 'IP', render: h => h.ipAddress ?? '' },
            { title: '角色描述', render: h => h.roleDesc },
            { title: '主機群組', render: h => badges(h.groupNames, '未分群（沒有人看得到）') },
            { title: '負責人', render: h => badges(h.ownerNames, '未指定') },
            { title: '最近回報', render: h => lastReportCell(h) },
            { title: '', className: 'text-end', render: h => editButton(h) }
        ],
        rows,
        empty: hosts.length === 0
            ? { title: '尚無主機', hint: '批次分析執行時會自動登記本機；其他主機可於「CSV 匯入」批次建立。' }
            : { title: '沒有符合搜尋條件的主機', hint: '請調整關鍵字後再試。' }
    });
}

function hostNameCell(host) {
    const wrap = document.createElement('span');
    wrap.textContent = host.hostName;

    if (!host.active) {
        const badge = document.createElement('span');
        badge.className = 'badge text-bg-secondary ms-2';
        badge.textContent = host.mergedInto ? '已併入其他主機' : '停用';
        wrap.appendChild(badge);
    }
    return wrap;
}

/**
 * 最近回報時間：超過 2 天沒回報就標紅。
 * 這正是「沒告警 ≠ 沒問題」的一種——批次沒跑就不會有任何風險紀錄，
 * 畫面上必須看得出是「真的沒事」還是「根本沒在看」。
 */
function lastReportCell(host) {
    const span = document.createElement('span');

    if (!host.lastReportAt) {
        span.className = 'text-danger';
        span.textContent = '尚未回報';
        return span;
    }

    const days = (Date.now() - new Date(host.lastReportAt).getTime()) / 86400000;
    span.textContent = formatDateTime(host.lastReportAt);
    if (days > 2) {
        span.className = 'text-danger fw-semibold';
        span.title = `已 ${Math.floor(days)} 天沒有回報`;
    }
    return span;
}

function badges(names, emptyText) {
    if (!names || names.length === 0) {
        const span = document.createElement('span');
        span.className = 'text-muted';
        span.textContent = emptyText;
        return span;
    }

    const wrap = document.createElement('span');
    for (const name of names) {
        const badge = document.createElement('span');
        badge.className = 'badge text-bg-light border me-1';
        badge.textContent = name;
        wrap.appendChild(badge);
    }
    return wrap;
}

function editButton(host) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-outline-primary';
    button.textContent = '編輯';
    button.addEventListener('click', () => openModal(host));
    return button;
}

function openModal(host) {
    editingHost = host;

    document.getElementById('host-modal-title').textContent = host ? `編輯 ${host.hostName}` : '新增主機';
    document.getElementById('host-name').value = host?.hostName ?? '';
    document.getElementById('host-name').disabled = !!host;
    document.getElementById('host-ip').value = host?.ipAddress ?? '';
    document.getElementById('host-netiq').value = host?.netiqServer ?? '';
    document.getElementById('host-role-desc').value = host?.roleDesc ?? '';
    document.getElementById('host-active').checked = host?.active ?? true;

    renderCheckboxes('host-groups', hostGroups.map(g => ({
        id: g.groupId,
        label: g.groupName,
        checked: host?.groupIds?.includes(g.groupId) ?? false
    })), '尚無主機群組，請先於「群組與授權」建立。');

    renderCheckboxes('host-owners', users.filter(u => u.active).map(u => ({
        id: u.userId,
        label: `${u.displayName}（${u.account}）`,
        checked: host?.ownerUserIds?.includes(u.userId) ?? false
    })), '尚無使用者，請先建立或匯入使用者。');

    modal.show();
}

function renderCheckboxes(containerId, items, emptyHint) {
    const container = document.getElementById(containerId);
    container.replaceChildren();

    if (items.length === 0) {
        const hint = document.createElement('div');
        hint.className = 'text-muted small';
        hint.textContent = emptyHint;
        container.appendChild(hint);
        return;
    }

    for (const item of items) {
        const wrapper = document.createElement('div');
        wrapper.className = 'form-check';

        const input = document.createElement('input');
        input.className = 'form-check-input';
        input.type = 'checkbox';
        input.value = item.id;
        input.id = `${containerId}-${item.id}`;
        input.checked = item.checked;

        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = item.label;

        wrapper.append(input, label);
        container.appendChild(wrapper);
    }
}

form.addEventListener('submit', async event => {
    event.preventDefault();

    const hostName = document.getElementById('host-name').value.trim();
    if (!hostName) {
        toast('請輸入主機名稱', 'warning');
        return;
    }

    const groupIds = selectedIds('host-groups');
    const ownerIds = selectedIds('host-owners');

    const saveButton = document.getElementById('host-save');
    const restore = withBusy(saveButton, '儲存中');

    try {
        const saved = await api.post('/api/admin/hosts', {
            hostName,
            ipAddress: document.getElementById('host-ip').value.trim() || null,
            netiqServer: document.getElementById('host-netiq').value.trim() || null,
            roleDesc: document.getElementById('host-role-desc').value.trim(),
            active: document.getElementById('host-active').checked
        });

        // 群組與負責人走獨立端點：兩者各自留下自己的稽核紀錄
        // （群組異動會改變可見範圍，是需要事後查得到的事）
        await api.put(`/api/admin/hosts/${saved.hostId}/groups`, { ids: groupIds });
        await api.put(`/api/admin/hosts/${saved.hostId}/owners`, { ids: ownerIds });

        toast(editingHost ? '已更新主機' : '已新增主機', 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

function selectedIds(containerId) {
    return Array.from(document.querySelectorAll(`#${containerId} input:checked`))
        .map(input => Number(input.value));
}

document.getElementById('btn-new-host').addEventListener('click', () => openModal(null));
searchInput.addEventListener('input', render);

load();
