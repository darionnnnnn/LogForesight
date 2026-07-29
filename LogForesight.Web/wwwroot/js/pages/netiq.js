/**
 * NetIQ 維護（「系統管理 > NetIQ 維護」頁）：Sentinel 連線設定 CRUD ＋ 查詢節流參數。
 *
 * 「掃描匯入」是匯入動作，留在「資料匯入」頁；這裡只管「有哪些 Sentinel、怎麼連、
 * 查詢行為多節制」，取代原本寫死在批次 appsettings.json 的 NetIq 區段。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

// ── Sentinel 清單與編輯 ──────────────────────────────────────────────────────

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
            { title: '作業系統', render: s => s.os === 'linux' ? 'Linux' : 'Windows' },
            { title: '探索帳密', render: s => renderDiscoverBadge(s) },
            { title: '主機數', render: s => String(s.hostCount) },
            { title: '狀態', render: s => renderSentinelActiveBadge(s.active) },
            { title: '', className: 'text-end', render: s => renderSentinelActions(s) }
        ],
        rows: sentinels,
        empty: { title: '尚無 Sentinel', hint: '用右上角的「新增 Sentinel」建立第一台，之後可在「資料匯入」頁對它掃描匯入主機。' }
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
    editingSentinel = sentinel ?? null;

    document.getElementById('sentinel-modal-title').textContent = sentinel ? `編輯 ${sentinel.name}` : '新增 Sentinel';
    document.getElementById('sentinel-name').value = sentinel?.name ?? '';
    document.getElementById('sentinel-base-url').value = sentinel?.baseUrl ?? '';
    document.getElementById('sentinel-username').value = sentinel?.username ?? '';
    document.getElementById('sentinel-os').value = sentinel?.os ?? 'windows';
    document.getElementById('sentinel-password').value = '';
    document.getElementById('sentinel-password-hint').textContent = sentinel?.hasPassword
        ? '已設定，留空＝不變更。'
        : '留空＝此 Sentinel 無法主動掃描。';
    document.getElementById('sentinel-test-result').replaceChildren();   // 換一台編輯時不留上一台的測試結果

    sentinelModal.show();
}

document.getElementById('btn-new-sentinel').addEventListener('click', () => openSentinelModal(null));

document.getElementById('sentinel-test-connection').addEventListener('click', async () => {
    const baseUrl = document.getElementById('sentinel-base-url').value.trim();
    const username = document.getElementById('sentinel-username').value.trim();
    const password = document.getElementById('sentinel-password').value;
    const resultEl = document.getElementById('sentinel-test-result');

    if (!baseUrl) { toast('請先輸入連線位址', 'warning'); return; }
    if (!username) { toast('請先輸入探索帳號', 'warning'); return; }
    if (!password && !editingSentinel?.hasPassword) { toast('請先輸入探索密碼', 'warning'); return; }

    const testButton = document.getElementById('sentinel-test-connection');
    const restore = withBusy(testButton, '測試中');
    resultEl.replaceChildren();

    try {
        // password 留空＝沿用既有密碼測試，與儲存時的 write-only 語意一致（僅編輯既有 Sentinel 時有效）
        const result = await api.post('/api/admin/sentinels/test-connection', {
            sentinelId: editingSentinel?.sentinelId ?? 0,
            baseUrl,
            username,
            password: password || null
        });

        resultEl.textContent = result.success
            ? `✓ ${result.message}（耗時 ${result.elapsedMs}ms）`
            : `✗ ${result.message}`;
        resultEl.className = `small mb-0 ${result.success ? 'text-success' : 'text-danger'}`;
    } catch {
        // 輸入不合法（如既有 Sentinel 找不到）由 api.js 以 toast 顯示；此處不用另外處理
    } finally {
        restore();
    }
});

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
            os: document.getElementById('sentinel-os').value,
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

// ── 連線與節流參數 ───────────────────────────────────────────────────────────

async function loadOptions() {
    const options = await api.get('/api/admin/netiq/options');
    document.getElementById('opt-query-delay').value = options.queryDelayMs;
    document.getElementById('opt-page-size').value = options.pageSize;
    document.getElementById('opt-max-results').value = options.maxResultsPerJob;
    document.getElementById('opt-timeout').value = options.timeoutSeconds;
    document.getElementById('opt-retry-count').value = options.retryCount;
    document.getElementById('opt-allow-invalid-certs').checked = options.allowInvalidCertificates;
    renderOptionsUpdated(options);
}

function renderOptionsUpdated(options) {
    const el = document.getElementById('netiq-options-updated');
    if (!options.updatedAt) {
        el.textContent = '尚未有人更新過（目前為系統預設值）。';
        return;
    }
    el.textContent = `最後更新：${formatDateTime(options.updatedAt)}` +
        (options.updatedByAccount ? `　更新者：${options.updatedByAccount}` : '');
}

document.getElementById('netiq-options-form').addEventListener('submit', async event => {
    event.preventDefault();

    const saveButton = document.getElementById('netiq-options-save');
    const restore = withBusy(saveButton, '儲存中');
    try {
        const options = await api.put('/api/admin/netiq/options', {
            queryDelayMs: Number(document.getElementById('opt-query-delay').value),
            pageSize: Number(document.getElementById('opt-page-size').value),
            maxResultsPerJob: Number(document.getElementById('opt-max-results').value),
            timeoutSeconds: Number(document.getElementById('opt-timeout').value),
            retryCount: Number(document.getElementById('opt-retry-count').value),
            allowInvalidCertificates: document.getElementById('opt-allow-invalid-certs').checked
        });
        toast('已儲存連線與節流參數', 'success');
        renderOptionsUpdated(options);
    } finally {
        restore();
    }
});

loadSentinels();
loadOptions();
