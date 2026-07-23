/**
 * 主機維護（docs/WEB-SPEC.md §9.8、docs/NETIQ-HOSTLIST-WEB-PLAN.md 步驟 3）。
 *
 * 這一頁除了 CRUD 還負責一件事：把「哪些主機今晚不會被檢查」講出來。
 * 待歸屬、IP 衝突、未分組各自代表一種靜默的失效，藏起來就會變成沒人記得的盲區。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, withBusy, confirmAction, renderChips } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const listContainer = document.getElementById('host-list');
const queueContainer = document.getElementById('netiq-queues');
const searchInput = document.getElementById('host-search');
const sentinelFilter = document.getElementById('sentinel-filter');
const sortSelect = document.getElementById('host-sort');

// chip 篩選狀態（§5.1 D-2）：狀態沿用舊版下拉的六個值改單選 chip；群組為新增的多選 chip
let statusMode = '';
const groupFilter = new Set();
const form = document.getElementById('host-form');
const bulkForm = document.getElementById('bulk-form');
const modal = new bootstrap.Modal(document.getElementById('host-modal'));
const bulkModal = new bootstrap.Modal(document.getElementById('bulk-modal'));

let hosts = [];
let hostGroups = [];
let users = [];
let overview = { sentinelNames: [], ipConflicts: [] };
let editingHost = null;

/**
 * IP 衝突的主機 ID。兩個集合都直接取自後端的衝突明細，前端不自行重算——
 * 「哪些主機會被輪巡」的規則只有 Core 的 NetiqHostList 一份，
 * 前端重算一次就會出現「畫面說會查、批次其實沒查」的分歧。
 */
let conflictIds = new Set();   // 衝突組涵蓋的全部主機（處置時要看到整組）
let unpolledIds = new Set();   // 其中今晚不會被輪巡的那些

async function load() {
    renderLoading(listContainer, 5);

    [hosts, hostGroups, users, overview] = await Promise.all([
        api.get('/api/admin/hosts'),
        api.get('/api/admin/host-groups'),
        api.get('/api/admin/users'),
        api.get('/api/admin/netiq/overview')
    ]);

    conflictIds = new Set(overview.ipConflicts.flatMap(group => group.hosts.map(h => h.hostId)));
    unpolledIds = new Set(
        overview.ipConflicts.flatMap(group => group.hosts.filter(h => !h.isPolled).map(h => h.hostId)));

    fillSentinelOptions();
    setupGroupChips();
    renderQueues();
    render();
}

function fillSentinelOptions() {
    const targets = [
        { el: sentinelFilter, keepFirst: '所有 Sentinel' },
        { el: document.getElementById('host-netiq'), keepFirst: null },
        { el: document.getElementById('bulk-netiq'), keepFirst: null }
    ];

    for (const { el } of targets) {
        const current = el.value;
        // 保留第一個選項（全部／待歸屬），其餘重建
        while (el.options.length > 1) el.remove(1);

        for (const name of overview.sentinelNames) {
            const option = document.createElement('option');
            option.value = name;
            option.textContent = name;
            el.appendChild(option);
        }
        el.value = current;
    }

    const hint = document.getElementById('host-netiq-hint');
    if (overview.sentinelNames.length === 0) {
        hint.textContent = '批次 appsettings.json 的 NetIq.Servers 尚未設定，目前只能登錄為待歸屬。';
        hint.classList.add('text-warning');
    }
}

/**
 * 待辦佇列：每一格都是「有主機正處在不會被正確檢查的狀態」，
 * 所以是可點擊的篩選捷徑，不是純數字看板。
 */
function renderQueues() {
    const cards = [
        {
            key: 'pending',
            count: overview.pendingAssignmentCount,
            title: '待歸屬 Sentinel',
            hint: '尚未確定在哪一台 Sentinel 上，確認前不會被輪巡'
        },
        {
            key: 'conflict',
            count: overview.ipConflictCount,
            title: 'IP 衝突',
            hint: '同一 IP 有多台登錄，每組只有最早建立的那台會被輪巡'
        },
        {
            key: 'ungrouped',
            count: overview.ungroupedCount,
            title: '未分組',
            hint: '依授權模型只有 admin 看得到，請補上主機群組'
        }
    ];

    queueContainer.replaceChildren();

    for (const card of cards) {
        const col = document.createElement('div');
        col.className = 'col-md-4';

        const button = document.createElement('button');
        button.type = 'button';
        button.className = `lf-card w-100 text-start p-3 border-0 ${card.count > 0 ? 'border-start border-4 border-warning' : ''}`;
        button.disabled = card.count === 0;

        const value = document.createElement('div');
        value.className = card.count > 0 ? 'fs-4 fw-semibold text-warning-emphasis' : 'fs-4 fw-semibold text-muted';
        value.textContent = card.count;

        const title = document.createElement('div');
        title.className = 'fw-semibold';
        title.textContent = card.title;

        const hint = document.createElement('div');
        hint.className = 'small text-muted';
        hint.textContent = card.count > 0 ? card.hint : '目前沒有待處理項目';

        button.append(value, title, hint);
        button.addEventListener('click', () => {
            statusMode = card.key;
            setupStatusChips();
            render();
        });

        col.appendChild(button);
        queueContainer.appendChild(col);
    }
}

/**
 * 狀態單選 chip（沿用舊版下拉的六個值，改用 chip 視覺）＋主機群組多選 chip（§5.1 D-2 新增）。
 * 待辦佇列卡點擊仍走同一個 statusMode，只是改呼叫 setupStatusChips() 同步 active 樣式。
 */
function setupStatusChips() {
    renderChips(document.getElementById('host-status-chips'), {
        items: [
            { value: '', label: '全部主機' },
            { value: 'local', label: '本機直讀' },
            { value: 'netiq', label: 'NetIQ 來源' },
            { value: 'pending', label: '待歸屬 Sentinel' },
            { value: 'conflict', label: 'IP 衝突' },
            { value: 'ungrouped', label: '未分組' },
            { value: 'inactive', label: '已停用／已併入' }
        ],
        attr: 'status',
        activeValues: [statusMode],
        multi: false,
        onToggle: value => { statusMode = value; render(); }
    });
}

function setupGroupChips() {
    renderChips(document.getElementById('host-group-chips'), {
        items: hostGroups.map(g => ({ value: String(g.groupId), label: g.groupName })),
        attr: 'group',
        activeValues: [...groupFilter],
        multi: true,
        onToggle: (value, active) => {
            if (active) groupFilter.add(value); else groupFilter.delete(value);
            render();
        }
    });
}

function sortHosts(list) {
    const sorted = [...list];
    if (sortSelect.value === 'lastReport') {
        sorted.sort((a, b) => new Date(b.lastReportAt ?? 0) - new Date(a.lastReportAt ?? 0));
    } else {
        sorted.sort((a, b) => a.hostName.localeCompare(b.hostName));
    }
    return sorted;
}

function visibleRows() {
    const keyword = searchInput.value.trim().toLowerCase();
    const sentinel = sentinelFilter.value;

    const filtered = hosts.filter(host => {
        if (keyword && !matchesKeyword(host, keyword)) return false;
        if (sentinel && host.netiqServer !== sentinel) return false;
        if (groupFilter.size > 0 && !host.groupIds.some(id => groupFilter.has(String(id)))) return false;

        switch (statusMode) {
            case 'local': return host.source === 'local' && host.active;
            case 'netiq': return host.source === 'netiq' && host.active;
            case 'pending': return host.source === 'netiq' && host.active && !host.mergedInto && !host.netiqServer;
            case 'conflict': return conflictIds.has(host.hostId);
            case 'ungrouped': return host.active && !host.mergedInto && host.groupIds.length === 0;
            case 'inactive': return !host.active;
            default: return true;
        }
    });

    return sortHosts(filtered);
}

function matchesKeyword(host, keyword) {
    return host.hostName.toLowerCase().includes(keyword) ||
        (host.displayName ?? '').toLowerCase().includes(keyword) ||
        (host.ipAddress ?? '').toLowerCase().includes(keyword);
}

function render() {
    const rows = visibleRows();
    document.getElementById('host-count').textContent = `共 ${rows.length} 台`;

    renderTable(listContainer, {
        columns: [
            { title: '主機', render: hostNameCell },
            { title: '來源', render: sourceCell },
            { title: 'IP', render: h => h.ipAddress ?? '' },
            { title: '角色描述', render: h => h.roleDesc },
            { title: '主機群組', render: h => badges(h.groupNames, '未分組（只有 admin 看得到）') },
            { title: '負責人', render: h => badges(h.ownerNames, '未指定') },
            { title: '最近回報', render: lastReportCell },
            { title: '', className: 'text-end', render: actionsCell }
        ],
        rows,
        empty: hosts.length === 0
            ? { title: '尚無主機', hint: '批次分析執行時會自動登記本機；NetIQ 主機請用「新增主機」或「批次貼上」建立。' }
            : { title: '沒有符合條件的主機', hint: '請調整搜尋或篩選條件後再試。' }
    });
}

function hostNameCell(host) {
    const wrap = document.createElement('div');

    const name = document.createElement('div');
    name.textContent = host.hostName;
    wrap.appendChild(name);

    // Sentinel 回報的主機名：NetIQ 主機以 IP 登錄，沒有這行就認不出是哪台機器
    if (host.displayName) {
        const display = document.createElement('div');
        display.className = 'small text-muted';
        display.textContent = host.displayName;
        wrap.appendChild(display);
    }

    const badgeRow = document.createElement('div');
    badgeRow.className = 'mt-1';
    for (const badge of statusBadges(host)) badgeRow.appendChild(badge);
    if (badgeRow.childElementCount > 0) wrap.appendChild(badgeRow);

    return wrap;
}

/**
 * 狀態徽章：只標「需要人做點什麼」的狀態。
 * 一切正常的主機不掛徽章，異常才顯眼——反過來做的話滿畫面徽章等於沒有徽章。
 */
function statusBadges(host) {
    const badges = [];

    if (host.mergedInto) {
        badges.push(badge('已併入其他主機', 'secondary'));
    } else if (!host.active) {
        badges.push(badge('停用（不分析）', 'secondary'));
    } else {
        if (host.source === 'netiq' && !host.netiqServer) {
            badges.push(badge('待歸屬 Sentinel', 'warning'));
        }
        if (unpolledIds.has(host.hostId)) {
            badges.push(badge('IP 衝突：今晚不會輪巡', 'danger'));
        } else if (conflictIds.has(host.hostId)) {
            badges.push(badge('IP 衝突', 'warning'));
        }
        if (host.groupIds.length === 0) {
            badges.push(badge('未分組', 'warning'));
        }
    }

    return badges;
}

function badge(text, variant) {
    const el = document.createElement('span');
    el.className = `lf-badge lf-badge--${variant} me-1`;
    el.textContent = text;
    return el;
}

function sourceCell(host) {
    const wrap = document.createElement('span');
    wrap.textContent = host.source === 'netiq' ? 'NetIQ' : '本機直讀';

    if (host.netiqServer) {
        const server = document.createElement('div');
        server.className = 'small text-muted';
        server.textContent = host.netiqServer;
        wrap.appendChild(server);
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
        const el = document.createElement('span');
        el.className = 'lf-badge lf-badge--light border me-1';
        el.textContent = name;
        wrap.appendChild(el);
    }
    return wrap;
}

function actionsCell(host) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-1 justify-content-end';

    if (host.mergedInto) {
        wrap.appendChild(actionButton('解除合併', 'outline-secondary', () => unmerge(host)));
        return wrap;
    }

    wrap.appendChild(actionButton('編輯', 'outline-primary', () => openModal(host)));
    wrap.appendChild(host.active
        ? actionButton('停用', 'outline-secondary', () => setActive(host, false))
        : actionButton('啟用', 'outline-success', () => setActive(host, true)));

    return wrap;
}

function actionButton(text, variant, onClick) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `btn btn-sm btn-${variant}`;
    button.textContent = text;
    button.addEventListener('click', onClick);
    return button;
}

async function setActive(host, active) {
    if (!active) {
        const confirmed = await confirmAction({
            title: '停用主機',
            message: `停用 ${host.hostName} 後將不再分析這台主機，既有的歷史紀錄與報告會保留。要繼續嗎？`,
            confirmText: '停用'
        });
        if (!confirmed) return;
    }

    try {
        await api.put(`/api/admin/hosts/${host.hostId}/active`, { active });
        toast(active ? '已啟用主機' : '已停用主機', 'success');
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    }
}

async function unmerge(host) {
    const confirmed = await confirmAction({
        title: '解除合併',
        message: `${host.hostName} 將恢復為獨立主機並重新啟用。` +
            '合併時帶入對方的群組／負責人等設定不會自動收回，請解除後一併確認。',
        confirmText: '解除',
        confirmVariant: 'warning'
    });
    if (!confirmed) return;

    try {
        await api.post(`/api/admin/hosts/${host.hostId}/unmerge`, {});
        toast('已解除合併', 'success');
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    }
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

bulkForm.addEventListener('submit', async event => {
    event.preventDefault();

    const lines = document.getElementById('bulk-lines').value;
    if (!lines.trim()) {
        toast('請貼上主機清單', 'warning');
        return;
    }

    const saveButton = document.getElementById('bulk-save');
    const restore = withBusy(saveButton, '登錄中');

    try {
        const result = await api.post('/api/admin/netiq/hosts/bulk', {
            netiqServer: document.getElementById('bulk-netiq').value.trim() || null,
            lines
        });

        renderBulkResult(result);
        toast(`已新增 ${result.addedCount} 台、更新 ${result.updatedCount} 台`, 'success');
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

/**
 * 略過的行逐筆列出。只說「略過 N 行」等於要使用者自己比對哪幾台沒進來，
 * 那是把系統知道的事推回給人做。
 */
function renderBulkResult(result) {
    const container = document.getElementById('bulk-result');
    container.replaceChildren();

    const summary = document.createElement('div');
    summary.className = result.skipped.length > 0 ? 'alert alert-warning' : 'alert alert-success';
    summary.textContent =
        `新增 ${result.addedCount} 台、更新 ${result.updatedCount} 台、略過 ${result.skipped.length} 行。`;
    container.appendChild(summary);

    if (result.skipped.length === 0) return;

    const list = document.createElement('ul');
    list.className = 'list-group small';

    for (const line of result.skipped) {
        const item = document.createElement('li');
        item.className = 'list-group-item';

        const location = document.createElement('span');
        location.className = 'fw-semibold me-2';
        location.textContent = `第 ${line.lineNumber} 行`;

        const raw = document.createElement('code');
        raw.className = 'me-2';
        raw.textContent = line.rawLine;

        const reason = document.createElement('span');
        reason.className = 'text-muted';
        reason.textContent = line.reason;

        item.append(location, raw, reason);
        list.appendChild(item);
    }

    container.appendChild(list);
}

function selectedIds(containerId) {
    return Array.from(document.querySelectorAll(`#${containerId} input:checked`))
        .map(input => Number(input.value));
}

document.getElementById('btn-new-host').addEventListener('click', () => openModal(null));
document.getElementById('btn-bulk-hosts').addEventListener('click', () => {
    document.getElementById('bulk-lines').value = '';
    document.getElementById('bulk-result').replaceChildren();
    bulkModal.show();
});

searchInput.addEventListener('input', render);
sentinelFilter.addEventListener('change', render);
sortSelect.addEventListener('change', render);
setupStatusChips();

// ── 從 NetIQ 主動探索匯入精靈（docs/SCALE-2000-PLAN.md §1）─────────────────────

const scanModal = new bootstrap.Modal(document.getElementById('scan-modal'));
let scanToken = null;

document.getElementById('btn-scan-netiq').addEventListener('click', async () => {
    document.getElementById('scan-result').replaceChildren();
    document.getElementById('scan-selection').textContent = '';
    document.getElementById('scan-import').disabled = true;
    scanToken = null;

    const select = document.getElementById('scan-server');
    select.replaceChildren();
    try {
        const targets = await api.get('/api/admin/netiq/scan-targets');
        if (targets.length === 0) {
            document.getElementById('scan-hint').textContent =
                '批次 appsettings.json 尚未設定任何 Sentinel。';
        } else {
            document.getElementById('scan-hint').textContent = '';
            for (const t of targets) {
                const opt = document.createElement('option');
                opt.value = t.name;
                opt.textContent = t.canDiscover ? t.name : `${t.name}（${t.reason}）`;
                opt.disabled = !t.canDiscover;
                select.appendChild(opt);
            }
        }
    } catch {
        return;
    }
    scanModal.show();
});

document.getElementById('scan-run').addEventListener('click', async () => {
    const server = document.getElementById('scan-server').value;
    if (!server) return;

    const restore = withBusy(document.getElementById('scan-run'), '掃描中');
    try {
        const result = await api.post('/api/admin/netiq/scan', { server });
        scanToken = result.token;
        renderScanResult(result);
    } catch {
        // 錯誤已由 api.js 顯示（含連線/逾時）
    } finally {
        restore();
    }
});

function renderScanResult(result) {
    const container = document.getElementById('scan-result');
    container.replaceChildren();

    const total = document.createElement('div');
    total.className = 'small text-muted mb-2';
    total.textContent = `共掃描到 ${result.totalCount} 台，分佈於 ${result.subnets.length} 個網段`;
    container.appendChild(total);

    for (const subnet of result.subnets) {
        const details = document.createElement('details');
        details.className = 'mb-2 border rounded';

        const summary = document.createElement('summary');
        summary.className = 'px-2 py-1 small';
        summary.style.cursor = 'pointer';
        // 網段層級的勾選框：勾整段
        const segBox = document.createElement('input');
        segBox.type = 'checkbox';
        segBox.className = 'form-check-input me-2';
        segBox.addEventListener('click', e => e.stopPropagation());
        segBox.addEventListener('change', () => {
            for (const box of details.querySelectorAll('input.lf-scan-host:not(:disabled)')) box.checked = segBox.checked;
            updateScanSelection();
        });
        summary.appendChild(segBox);
        const label = document.createElement('span');
        label.textContent = `${subnet.cidr}（${subnet.totalCount} 台` +
            (subnet.existingCount > 0 ? `，${subnet.existingCount} 台已登錄` : '') +
            (subnet.orphanOverlapCount > 0 ? `，${subnet.orphanOverlapCount} 台可復活` : '') + '）';
        summary.appendChild(label);
        details.appendChild(summary);

        const body = document.createElement('div');
        body.className = 'px-2 pb-2';
        for (const host of subnet.hosts) {
            body.appendChild(scanHostRow(host));
        }
        details.appendChild(body);
        container.appendChild(details);
    }
    updateScanSelection();
}

function scanHostRow(host) {
    const row = document.createElement('div');
    row.className = 'd-flex align-items-center gap-2 py-1 small';

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'form-check-input lf-scan-host';
    box.dataset.ip = host.ipAddress;
    // 新主機與可復活的預設勾選；使用中的既有主機預設不勾（再勾＝更新歸屬）
    box.checked = host.orphanOverlap || (!host.exists);
    box.addEventListener('change', updateScanSelection);
    row.appendChild(box);

    const name = document.createElement('span');
    name.textContent = `${host.ipAddress}　${host.hostName}`;
    row.appendChild(name);

    if (host.exists) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '已登錄';
        row.appendChild(badge);
    }
    if (host.orphanOverlap) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--primary';
        badge.textContent = `原屬 ${host.orphanedFrom}，因移除而停用`;
        row.appendChild(badge);
    }
    return row;
}

function selectedScanIps() {
    return Array.from(document.querySelectorAll('#scan-result input.lf-scan-host:checked'))
        .map(box => box.dataset.ip);
}

function updateScanSelection() {
    const count = selectedScanIps().length;
    document.getElementById('scan-selection').textContent = count > 0 ? `已選 ${count} 台` : '';
    document.getElementById('scan-import').disabled = count === 0 || !scanToken;
}

document.getElementById('scan-import').addEventListener('click', async () => {
    const selectedIps = selectedScanIps();
    if (selectedIps.length === 0 || !scanToken) return;

    const restore = withBusy(document.getElementById('scan-import'), '匯入中');
    try {
        const result = await api.post('/api/admin/netiq/import', { token: scanToken, selectedIps });
        toast(`匯入完成：新增 ${result.added}、更新 ${result.updated}` +
            (result.revived > 0 ? `、重新啟用 ${result.revived}` : ''), 'success');
        scanModal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

load();
