/**
 * 資料匯入（docs/WEB-SPEC.md §9.9、docs/NETIQ-WEB-CONFIG-PLAN.md）。
 *
 * CSV 匯入（上傳→預覽→套用）與 NetIQ 匯入（Sentinel 管理＋新增/掃描精靈）合併在同一頁，
 * 因為兩者最終都寫進同一份「匯入紀錄」，拆成兩頁只會讓使用者要來回找歷史紀錄。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, withBusy, bindTabs } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

bindTabs(document.getElementById('import-tabs'));

// ── CSV 匯入：上傳與預覽 ───────────────────────────────────────────────────────

const previewCard = document.getElementById('preview-card');
const previewFile = document.getElementById('preview-file');
const previewSummary = document.getElementById('preview-summary');
const previewWarnings = document.getElementById('preview-warnings');
const previewRows = document.getElementById('preview-rows');
const applyButton = document.getElementById('preview-apply');

const KIND_NAMES = { Users: '使用者', Hosts: '主機', GroupAccess: '群組授權', Owners: '負責人', Netiq: 'NetIQ 掃描匯入' };
const ACTION_META = {
    Add: { text: '新增', variant: 'success' },
    Update: { text: '更新', variant: 'primary' },
    Unchanged: { text: '不變', variant: 'light' },
    Remove: { text: '移除', variant: 'warning' },
    Error: { text: '錯誤', variant: 'danger' }
};

let currentPlan = null;
let currentKind = null;

for (const input of document.querySelectorAll('[data-upload]')) {
    input.addEventListener('change', async () => {
        const file = input.files?.[0];
        if (!file) return;

        const kind = input.dataset.upload;
        const formData = new FormData();
        formData.append('file', file);

        renderLoading(previewRows, 4);
        previewCard.classList.remove('d-none');
        previewCard.scrollIntoView({ behavior: 'smooth', block: 'start' });

        try {
            // FormData 上傳不經 core/api.js 的 JSON 包裝，但 CSRF 標頭與信封解析規則相同
            const response = await fetch(`/api/imports/${kind}/preview`, {
                method: 'POST',
                headers: { 'X-Requested-By': 'LogForesight' },
                credentials: 'same-origin',
                body: formData
            });

            const payload = await response.json();
            if (!response.ok || !payload.success) {
                throw new Error(payload?.error?.message ?? '預覽失敗');
            }

            currentPlan = payload.data;
            currentKind = kind;
            renderPreview();
        } catch (error) {
            previewCard.classList.add('d-none');
            toast(error.message, 'danger', 8000);
        } finally {
            input.value = '';   // 清空以便重新選同一個檔案
        }
    });
}

function renderPreview() {
    previewFile.textContent = `${KIND_NAMES[currentKind]}／${currentPlan.fileName}`;

    renderSummary();
    renderWarnings();
    renderRows();

    applyButton.disabled = !currentPlan.canApply;
    applyButton.textContent = currentPlan.canApply ? '套用' : '有錯誤，無法套用';
}

function renderSummary() {
    const items = [
        { label: '新增', value: currentPlan.addCount, variant: 'success' },
        { label: '更新', value: currentPlan.updateCount, variant: 'primary' },
        { label: '不變', value: currentPlan.unchangedCount, variant: 'secondary' },
        { label: '移除', value: currentPlan.removeCount, variant: 'warning' },
        { label: '錯誤', value: currentPlan.errorCount, variant: 'danger' }
    ];

    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap gap-3';

    for (const item of items) {
        if (item.value === 0 && (item.label === '移除' || item.label === '錯誤')) continue;

        const box = document.createElement('div');
        box.className = 'text-center px-3';

        const value = document.createElement('div');
        value.className = `lf-stat__value text-${item.variant}`;
        value.textContent = String(item.value);

        const label = document.createElement('div');
        label.className = 'lf-stat__label';
        label.textContent = item.label;

        box.append(value, label);
        wrap.appendChild(box);
    }

    previewSummary.replaceChildren(wrap);

    if (currentPlan.newGroups.length > 0) {
        const note = document.createElement('div');
        note.className = 'alert alert-info mt-3 mb-0';
        note.textContent = `套用時將自動建立群組：${currentPlan.newGroups.join('、')}`;
        previewSummary.appendChild(note);
    }

    if (currentPlan.newUsers && currentPlan.newUsers.length > 0) {
        const note = document.createElement('div');
        note.className = 'alert alert-info mt-3 mb-0';
        note.textContent = `套用時將自動建立 ${currentPlan.newUsers.length} 個使用者帳號：${currentPlan.newUsers.join('、')}`;
        previewSummary.appendChild(note);
    }
}

function renderWarnings() {
    previewWarnings.replaceChildren();
    if (!currentPlan.warnings || currentPlan.warnings.length === 0) return;

    const box = document.createElement('div');
    box.className = 'alert alert-warning';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = '請確認以下事項';
    box.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'mb-0 ps-3';
    for (const warning of currentPlan.warnings) {
        const item = document.createElement('li');
        item.textContent = warning;
        list.appendChild(item);
    }
    box.appendChild(list);

    previewWarnings.appendChild(box);
}

function renderRows() {
    renderTable(previewRows, {
        columns: [
            { title: '行號', render: r => (r.lineNumber > 0 ? String(r.lineNumber) : '—') },
            { title: '動作', render: r => actionBadge(r.action) },
            { title: '對象', render: r => r.key },
            { title: '說明', render: r => r.error ?? r.description }
        ],
        rows: currentPlan.rows,
        empty: { title: '檔案中沒有資料列', hint: '請確認檔案是否只有標題列。' }
    });
}

function actionBadge(action) {
    const meta = ACTION_META[action] ?? { text: action, variant: 'secondary' };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

// ── CSV 匯入：套用 ─────────────────────────────────────────────────────────────

applyButton.addEventListener('click', async () => {
    // 移除是全量取代造成的隱形後果，必須在確認框裡具體講清楚影響幾筆
    const message = currentPlan.removeCount > 0
        ? `將新增 ${currentPlan.addCount} 筆、更新 ${currentPlan.updateCount} 筆，` +
          `並移除 ${currentPlan.removeCount} 筆未列於檔案中的既有授權。移除後相關人員將立即失去對應主機的檢視權限。`
        : `將新增 ${currentPlan.addCount} 筆、更新 ${currentPlan.updateCount} 筆資料。`;

    const confirmed = await confirmAction({
        title: `套用${KIND_NAMES[currentKind]}匯入`,
        message,
        confirmText: '套用',
        confirmVariant: currentPlan.removeCount > 0 ? 'danger' : 'primary'
    });
    if (!confirmed) return;

    const restore = withBusy(applyButton, '套用中');
    try {
        const result = await api.post(`/api/imports/${currentKind}/apply`, { token: currentPlan.token });

        toast(`匯入完成：新增 ${result.added}、更新 ${result.updated}` +
              (result.removed > 0 ? `、移除 ${result.removed}` : ''), 'success', 6000);

        previewCard.classList.add('d-none');
        currentPlan = null;
        await loadLogs();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

document.getElementById('preview-cancel').addEventListener('click', () => {
    previewCard.classList.add('d-none');
    currentPlan = null;
});

// ── 匯入紀錄（CSV 與 NetIQ 共用） ────────────────────────────────────────────

async function loadLogs() {
    const container = document.getElementById('import-logs');
    renderLoading(container, 3);

    const logs = await api.get('/api/imports/logs');

    renderTable(container, {
        columns: [
            { title: '時間', render: l => formatDateTime(l.createdAt) },
            { title: '類型', render: l => KIND_NAMES[l.kind] ?? l.kind },
            { title: '檔案／來源', render: l => l.fileName },
            { title: '操作者', render: l => l.account },
            {
                title: '結果',
                render: l => `新增 ${l.addedCount}、更新 ${l.updatedCount}` +
                             (l.removedCount > 0 ? `、移除 ${l.removedCount}` : '') +
                             (l.revivedCount > 0 ? `、復活 ${l.revivedCount}` : '') +
                             (l.createdGroups?.length ? `（新建群組：${l.createdGroups.join('、')}）` : '')
            }
        ],
        rows: logs,
        empty: { title: '尚無匯入紀錄', hint: '上傳並套用 CSV，或於「NetIQ 匯入」頁掃描匯入後，這裡會留下每次匯入的結果。' }
    });
}

// ── NetIQ 匯入：Sentinel 清單與編輯 ──────────────────────────────────────────

const sentinelListContainer = document.getElementById('sentinel-list');
const sentinelForm = document.getElementById('sentinel-form');
const sentinelModal = new bootstrap.Modal(document.getElementById('sentinel-modal'));

let sentinels = [];
let editingSentinel = null;

async function loadSentinels() {
    renderLoading(sentinelListContainer, 3);
    sentinels = await api.get('/api/admin/sentinels');
    renderSentinels();
}

function renderSentinels() {
    document.getElementById('sentinel-count').textContent = `共 ${sentinels.length} 台`;

    renderTable(sentinelListContainer, {
        columns: [
            { title: '名稱', render: s => s.name },
            { title: '連線位址', render: s => s.baseUrl || '' },
            { title: '探索帳密', render: s => renderDiscoverBadge(s) },
            { title: '主機數', render: s => String(s.hostCount) },
            { title: '狀態', render: s => renderSentinelActiveBadge(s.active) },
            { title: '', className: 'text-end', render: s => renderSentinelActions(s) }
        ],
        rows: sentinels,
        empty: { title: '尚無 Sentinel', hint: '用右上角的「新增 Sentinel」建立第一台。' }
    });
}

function renderDiscoverBadge(sentinel) {
    const span = document.createElement('span');
    if (sentinel.canDiscover) {
        span.className = 'lf-badge lf-badge--success';
        span.textContent = '已設定';
    } else {
        span.className = 'text-muted small';
        span.textContent = sentinel.hasPassword ? '缺帳號' : '未設定';
    }
    return span;
}

function renderSentinelActiveBadge(active) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${active ? 'success' : 'secondary'}`;
    span.textContent = active ? '啟用' : '停用（暫停輪巡）';
    return span;
}

function renderSentinelActions(sentinel) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-1 justify-content-end';

    const scan = document.createElement('button');
    scan.type = 'button';
    scan.className = 'btn btn-sm btn-primary';
    scan.textContent = '掃描匯入';
    scan.disabled = !sentinel.canDiscover;
    scan.title = sentinel.canDiscover ? '' : '請先於「編輯」補上探索帳密';
    scan.addEventListener('click', () => openWizardExisting(sentinel));
    wrap.appendChild(scan);

    const edit = document.createElement('button');
    edit.type = 'button';
    edit.className = 'btn btn-sm btn-outline-primary';
    edit.textContent = '編輯';
    edit.addEventListener('click', () => openSentinelModal(sentinel));
    wrap.appendChild(edit);

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-sm btn-outline-secondary';
    toggle.textContent = sentinel.active ? '停用' : '啟用';
    toggle.addEventListener('click', () => onToggleSentinelActive(sentinel));
    wrap.appendChild(toggle);

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'btn btn-sm btn-outline-danger';
    remove.textContent = '刪除';
    remove.addEventListener('click', () => onDeleteSentinel(sentinel));
    wrap.appendChild(remove);

    return wrap;
}

async function onToggleSentinelActive(sentinel) {
    try {
        await api.put(`/api/admin/sentinels/${sentinel.sentinelId}/active`, { active: !sentinel.active });
        toast(sentinel.active ? `已停用「${sentinel.name}」（暫停輪巡）` : `已啟用「${sentinel.name}」`, 'success');
        await loadSentinels();
    } catch {
        // 錯誤已由 api.js 顯示
    }
}

async function onDeleteSentinel(sentinel) {
    const confirmed = await confirmAction({
        title: '刪除 Sentinel',
        message: sentinel.hostCount > 0
            ? `「${sentinel.name}」轄下有 ${sentinel.hostCount} 台使用中的主機，刪除後這些主機會停用並標記為孤兒（可於主機頁重新綁定到其他 Sentinel，歷史紀錄不受影響）。確定要刪除嗎？`
            : `將刪除 Sentinel「${sentinel.name}」。此操作無法復原。`,
        confirmText: '刪除'
    });
    if (!confirmed) return;

    try {
        await api.delete(`/api/admin/sentinels/${sentinel.sentinelId}`);
        toast(`已刪除 Sentinel「${sentinel.name}」`, 'success');
        await loadSentinels();
    } catch {
        // 錯誤已由 api.js 顯示
    }
}

function openSentinelModal(sentinel) {
    editingSentinel = sentinel;

    document.getElementById('sentinel-modal-title').textContent = sentinel ? `編輯 ${sentinel.name}` : '新增 Sentinel';
    document.getElementById('sentinel-name').value = sentinel?.name ?? '';
    document.getElementById('sentinel-base-url').value = sentinel?.baseUrl ?? '';
    document.getElementById('sentinel-username').value = sentinel?.username ?? '';
    document.getElementById('sentinel-password').value = '';
    document.getElementById('sentinel-password-hint').textContent = sentinel?.hasPassword
        ? '已設定，留空＝不變更。'
        : '留空＝此 Sentinel 無法主動掃描。';

    sentinelModal.show();
}

sentinelForm.addEventListener('submit', async event => {
    event.preventDefault();

    const name = document.getElementById('sentinel-name').value.trim();
    if (!name) {
        toast('請輸入 Sentinel 名稱', 'warning');
        return;
    }

    const saveButton = document.getElementById('sentinel-save');
    const restore = withBusy(saveButton, '儲存中');

    try {
        await api.post('/api/admin/sentinels', {
            sentinelId: editingSentinel?.sentinelId ?? 0,
            name,
            baseUrl: document.getElementById('sentinel-base-url').value.trim(),
            username: document.getElementById('sentinel-username').value.trim(),
            // 留空字串＝不變更（後端 write-only 語意）；沒有勾選清除密碼的介面，
            // 需要清空密碼的情境（例如帳密停用）改用「停用」而非清空密碼
            password: document.getElementById('sentinel-password').value || null
        });

        toast(editingSentinel ? '已更新 Sentinel' : '已新增 Sentinel', 'success');
        sentinelModal.hide();
        await loadSentinels();
    } catch {
        // 錯誤訊息已由 api.js 以 toast 顯示
    } finally {
        restore();
    }
});

// ── NetIQ 匯入：新增／掃描精靈（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 6-8） ────────
//
// 「新增 Sentinel」與「對既有 Sentinel 掃描匯入」共用同一個精靈：前者多一個連線設定
// 步驟（掃描成功才建立 Sentinel，定案 6＝掃描即帳密驗證），後兩步（選主機／指派群組）
// 完全相同，拆成兩套 UI 只會製造重複與不同步的風險。

const wizardModal = new bootstrap.Modal(document.getElementById('netiq-wizard-modal'));
const wizardTitle = document.getElementById('wizard-title');
const wizardHint = document.getElementById('wizard-hint');
const wizardBackButton = document.getElementById('wizard-back');
const wizardPrimaryButton = document.getElementById('wizard-primary');
const autoScanCheckbox = document.getElementById('wizard-auto-scan');

let wizardMode = 'create';        // 'create' | 'existing'
let wizardPane = 'connect';       // 'connect' | 'subnets' | 'groups'
let wizardScan = null;            // 最近一次掃描結果（NetiqScanResultDto）
let wizardExistingServer = null;  // mode === 'existing' 時的 Sentinel 名稱

function openWizardCreate() {
    wizardMode = 'create';
    wizardPane = 'connect';
    wizardScan = null;
    wizardExistingServer = null;

    document.getElementById('wizard-name').value = '';
    document.getElementById('wizard-base-url').value = '';
    document.getElementById('wizard-username').value = '';
    document.getElementById('wizard-password').value = '';
    autoScanCheckbox.checked = true;

    renderWizardPane();
    wizardModal.show();
}

async function openWizardExisting(sentinel) {
    wizardMode = 'existing';
    wizardPane = 'subnets';
    wizardScan = null;
    wizardExistingServer = sentinel.name;

    renderWizardPane();
    wizardModal.show();

    document.getElementById('wizard-scan-result').replaceChildren(wizardNote('掃描中…'));
    wizardPrimaryButton.disabled = true;
    try {
        wizardScan = await api.post('/api/admin/netiq/scan', { server: sentinel.name });
        renderSubnetSelection();
    } catch {
        wizardModal.hide();
    } finally {
        wizardPrimaryButton.disabled = false;
    }
}

function wizardNote(text) {
    const p = document.createElement('p');
    p.className = 'text-muted small';
    p.textContent = text;
    return p;
}

function renderWizardPane() {
    document.getElementById('wizard-pane-connect').classList.toggle('d-none', wizardPane !== 'connect');
    document.getElementById('wizard-pane-subnets').classList.toggle('d-none', wizardPane !== 'subnets');
    document.getElementById('wizard-pane-groups').classList.toggle('d-none', wizardPane !== 'groups');

    wizardBackButton.classList.toggle('d-none', wizardPane !== 'groups');
    wizardHint.textContent = '';

    if (wizardPane === 'connect') {
        wizardTitle.textContent = '新增 Sentinel';
        wizardPrimaryButton.textContent = autoScanCheckbox.checked ? '掃描並建立' : '建立';
    } else if (wizardPane === 'subnets') {
        wizardTitle.textContent = wizardMode === 'create' ? '選擇要匯入的主機' : `從「${wizardExistingServer}」掃描匯入`;
        wizardPrimaryButton.textContent = '下一步';
        updateSubnetSelectionHint();
    } else {
        wizardTitle.textContent = '指派網段所屬主機群組';
        wizardPrimaryButton.textContent = '完成匯入';
    }
}

autoScanCheckbox.addEventListener('change', () => {
    if (wizardPane === 'connect') renderWizardPane();
});

document.getElementById('btn-new-sentinel').addEventListener('click', openWizardCreate);

wizardBackButton.addEventListener('click', () => {
    if (wizardPane !== 'groups') return;
    wizardPane = 'subnets';
    renderWizardPane();
});

wizardPrimaryButton.addEventListener('click', () => {
    if (wizardPane === 'connect') {
        wizardSubmitConnect();
    } else if (wizardPane === 'subnets') {
        wizardAdvanceToGroups();
    } else {
        wizardSubmitImport();
    }
});

async function wizardSubmitConnect() {
    const name = document.getElementById('wizard-name').value.trim();
    const username = document.getElementById('wizard-username').value.trim();
    const password = document.getElementById('wizard-password').value;
    const baseUrl = document.getElementById('wizard-base-url').value.trim();
    const autoScan = autoScanCheckbox.checked;

    if (!name) {
        toast('請輸入 Sentinel 名稱', 'warning');
        return;
    }
    if (autoScan && (!username || !password)) {
        toast('自動掃描需要探索帳號與密碼', 'warning');
        return;
    }

    const restore = withBusy(wizardPrimaryButton, autoScan ? '掃描中' : '建立中');
    try {
        if (autoScan) {
            wizardScan = await api.post('/api/admin/netiq/create-and-scan', { name, baseUrl, username, password });
            restore();
            await loadSentinels();
            wizardPane = 'subnets';
            renderWizardPane();
            renderSubnetSelection();
        } else {
            await api.post('/api/admin/sentinels', { sentinelId: 0, name, baseUrl, username, password: password || null });
            restore();
            toast('已新增 Sentinel', 'success');
            wizardModal.hide();
            await loadSentinels();
        }
    } catch {
        restore();
        // 錯誤已由 api.js 顯示
    }
}

function wizardAdvanceToGroups() {
    if (selectedWizardIps().length === 0) {
        toast('請至少勾選一台主機', 'warning');
        return;
    }
    wizardPane = 'groups';
    renderWizardPane();
    renderGroupAssignment();
}

async function wizardSubmitImport() {
    const selectedIps = selectedWizardIps();
    const groupAssignments = collectGroupAssignments();

    const restore = withBusy(wizardPrimaryButton, '匯入中');
    try {
        const result = await api.post('/api/admin/netiq/import', {
            token: wizardScan.token,
            selectedIps,
            groupAssignments
        });
        toast(`已匯入：新增 ${result.added}、更新 ${result.updated}` +
              (result.revived > 0 ? `、復活 ${result.revived}` : ''), 'success', 6000);
        wizardModal.hide();
        await loadSentinels();
        await loadLogs();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
}

// ── 精靈步驟 2：網段主機勾選（掃描結果） ─────────────────────────────────────

function renderSubnetSelection() {
    const container = document.getElementById('wizard-scan-result');
    container.replaceChildren();

    const total = document.createElement('div');
    total.className = 'small text-muted mb-2';
    total.textContent = `共掃描到 ${wizardScan.totalCount} 台，分佈於 ${wizardScan.subnets.length} 個網段`;
    container.appendChild(total);

    for (const subnet of wizardScan.subnets) {
        const details = document.createElement('details');
        details.className = 'mb-2 border rounded';
        details.open = true;

        const summary = document.createElement('summary');
        summary.className = 'px-2 py-1 small';
        summary.style.cursor = 'pointer';

        const segBox = document.createElement('input');
        segBox.type = 'checkbox';
        segBox.className = 'form-check-input me-2';
        segBox.addEventListener('click', e => e.stopPropagation());
        segBox.addEventListener('change', () => {
            for (const box of details.querySelectorAll('input.lf-wizard-host:not(:disabled)')) box.checked = segBox.checked;
            updateSubnetSelectionHint();
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
            body.appendChild(wizardHostRow(host));
        }
        details.appendChild(body);
        container.appendChild(details);
    }
    updateSubnetSelectionHint();
}

function wizardHostRow(host) {
    const row = document.createElement('div');
    row.className = 'd-flex align-items-center gap-2 py-1 small';

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'form-check-input lf-wizard-host';
    box.dataset.ip = host.ipAddress;
    // 新主機與可復活的預設勾選；使用中的既有主機預設不勾（再勾＝更新歸屬）
    box.checked = host.orphanOverlap || (!host.exists);
    box.addEventListener('change', updateSubnetSelectionHint);
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

function selectedWizardIps() {
    return Array.from(document.querySelectorAll('#wizard-scan-result input.lf-wizard-host:checked'))
        .map(box => box.dataset.ip);
}

function updateSubnetSelectionHint() {
    if (wizardPane !== 'subnets') return;
    const count = selectedWizardIps().length;
    wizardHint.textContent = count > 0 ? `已選 ${count} 台` : '';
}

// ── 精靈步驟 3：網段群組指派（只影響本次新增的主機，定案 8） ─────────────────

async function renderGroupAssignment() {
    const container = document.getElementById('wizard-group-assign');
    container.replaceChildren(wizardNote('載入群組清單中…'));

    const hostGroups = await api.get('/api/admin/host-groups');
    const selected = new Set(selectedWizardIps());
    container.replaceChildren();

    for (const subnet of wizardScan.subnets) {
        const selectedInSubnet = subnet.hosts.filter(h => selected.has(h.ipAddress)).length;
        if (selectedInSubnet === 0) continue;

        const row = document.createElement('div');
        row.className = 'row g-2 align-items-center mb-2';
        row.dataset.cidr = subnet.cidr;

        const labelCol = document.createElement('div');
        labelCol.className = 'col-4 small';
        labelCol.textContent = `${subnet.cidr}（${selectedInSubnet} 台）`;
        row.appendChild(labelCol);

        const selectCol = document.createElement('div');
        selectCol.className = 'col-4';
        const select = document.createElement('select');
        select.className = 'form-select form-select-sm lf-wizard-group-mode';
        select.appendChild(new Option('未分組（僅 admin 可見）', 'skip', true, true));
        for (const group of hostGroups) {
            select.appendChild(new Option(group.groupName, `existing:${group.groupId}`));
        }
        select.appendChild(new Option('＋ 建立新群組…', 'new'));
        selectCol.appendChild(select);
        row.appendChild(selectCol);

        const inputCol = document.createElement('div');
        inputCol.className = 'col-4';
        const newNameInput = document.createElement('input');
        newNameInput.type = 'text';
        newNameInput.className = 'form-control form-control-sm lf-wizard-group-new d-none';
        newNameInput.placeholder = '新群組名稱';
        inputCol.appendChild(newNameInput);
        row.appendChild(inputCol);

        select.addEventListener('change', () => {
            newNameInput.classList.toggle('d-none', select.value !== 'new');
        });

        container.appendChild(row);
    }

    if (container.childElementCount === 0) {
        container.appendChild(wizardNote('沒有需要指派的網段。'));
    }
}

function collectGroupAssignments() {
    const assignments = [];
    for (const row of document.querySelectorAll('#wizard-group-assign > .row[data-cidr]')) {
        const mode = row.querySelector('.lf-wizard-group-mode').value;
        const assignment = { cidr: row.dataset.cidr, mode: 'skip' };

        if (mode.startsWith('existing:')) {
            assignment.mode = 'existing';
            assignment.hostGroupId = Number(mode.split(':')[1]);
        } else if (mode === 'new') {
            const name = row.querySelector('.lf-wizard-group-new').value.trim();
            if (name) {
                assignment.mode = 'new';
                assignment.newGroupName = name;
            }
        }
        assignments.push(assignment);
    }
    return assignments;
}

loadLogs();
loadSentinels();
