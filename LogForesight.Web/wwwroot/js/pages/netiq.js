/**
 * NetIQ 維護（「系統管理 > NetIQ 維護」頁）：Sentinel 連線設定 CRUD ＋ 查詢節流參數
 * ＋ 掃描匯入（§1，回饋第十一輪自「資料匯入」頁搬來）＋ API 診斷，共三個分頁。
 *
 * 掃描精靈自成一個模組（`netiq-import-wizard.js`）：本檔已 400 行，把精靈整段搬進來
 * 會變成千行檔案，而精靈本身是完整獨立的一段流程（掃描→勾選→分組→匯入）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderSpinner, toast, confirmAction, withBusy, bindTabs, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';
import { initNetiqImportTab, refreshScanPicker } from './netiq-import-wizard.js';

bindTabs(document.getElementById('netiq-tabs'));
initNetiqImportTab({ reloadSentinels: () => loadSentinels() });

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

    // 設定與匯入現在同一頁（§1）：剛補完探索帳密的 Sentinel 要立刻出現在「匯入」分頁的
    // 掃描下拉裡，否則使用者切過去看不到、以為沒存成功。傳已取回的清單，不讓它再查一次
    refreshScanPicker(sentinels);
}

function renderSentinels() {
    document.getElementById('sentinel-count').textContent = `共 ${sentinels.length} 台`;
    renderProbeSentinelOptions();

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
        empty: { title: '尚無 Sentinel', hint: '用右上角的「新增 Sentinel」建立第一台，之後可在本頁的「匯入」分頁對它掃描匯入主機。' }
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
    const [options, aiStatus] = await Promise.all([
        api.get('/api/admin/netiq/options'),
        api.get('/api/ai/status', { silent: true }).catch(() => null)
    ]);
    document.getElementById('opt-query-delay').value = options.queryDelayMs;
    document.getElementById('opt-page-size').value = options.pageSize;
    document.getElementById('opt-max-results').value = options.maxResultsPerJob;
    document.getElementById('opt-timeout').value = options.timeoutSeconds;
    document.getElementById('opt-retry-count').value = options.retryCount;
    document.getElementById('opt-allow-invalid-certs').checked = options.allowInvalidCertificates;
    document.getElementById('opt-backfill-days').value = options.backfillDays;
    document.getElementById('opt-max-parallel-servers').value = options.maxParallelServers;
    document.getElementById('opt-chat-live-fetch').checked = options.chatLiveFetchEnabled;
    // 這個選項只服務「詢問 AI」對話的 fallback 路徑，AI 未設定時整條路徑無意義，隱藏但保留值
    // （docs/archive/FEEDBACK-7-PLAN.md）
    document.getElementById('opt-chat-live-fetch-wrap').classList.toggle('d-none', !aiStatus?.available);

    // 離線示範資料開關（§13）：僅非 Production 顯示（canUseOfflineDemo）；開啟時亮警示徽章
    document.getElementById('opt-offline-demo').checked = options.useOfflineDemoData;
    document.getElementById('opt-offline-demo-wrap').classList.toggle('d-none', !options.canUseOfflineDemo);
    document.getElementById('opt-offline-demo-badge').classList.toggle('d-none', !options.useOfflineDemoData);

    renderOptionsUpdated(options);
}

function renderOptionsUpdated(options) {
    const el = document.getElementById('netiq-options-updated');
    if (!options.updatedAt) {
        el.textContent = '尚未有人更新過（目前為系統預設值）。';
        return;
    }
    el.textContent = `最後更新：${formatDateTime(options.updatedAt)}` +
        (options.updatedByAccount ? `　更新者：${formatUserName(options.updatedByDisplayName, options.updatedByAccount)}` : '');
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
            allowInvalidCertificates: document.getElementById('opt-allow-invalid-certs').checked,
            backfillDays: Number(document.getElementById('opt-backfill-days').value),
            maxParallelServers: Number(document.getElementById('opt-max-parallel-servers').value),
            chatLiveFetchEnabled: document.getElementById('opt-chat-live-fetch').checked,
            useOfflineDemoData: document.getElementById('opt-offline-demo').checked
        });
        toast('已儲存連線與節流參數', 'success');
        // 重新套用（含離線示範徽章）——儲存後徽章要即時反映
        document.getElementById('opt-offline-demo').checked = options.useOfflineDemoData;
        document.getElementById('opt-offline-demo-badge').classList.toggle('d-none', !options.useOfflineDemoData);
        renderOptionsUpdated(options);
    } finally {
        restore();
    }
});

// ── NetIQ API 診斷（probe，「診斷」分頁）────────────────────────────────────

const probeSentinelSelect = document.getElementById('probe-sentinel');
const probeStartButton = document.getElementById('probe-start');
const probeStateEl = document.getElementById('probe-state');
const probeOutputEl = document.getElementById('probe-output');
const probeCopyButton = document.getElementById('probe-copy');

let probePollTimer = null;

function renderProbeSentinelOptions() {
    const previousValue = probeSentinelSelect.value;
    probeSentinelSelect.replaceChildren();

    if (sentinels.length === 0) {
        const option = document.createElement('option');
        option.value = '';
        option.textContent = '（尚無 Sentinel，請先在「設定」分頁新增）';
        probeSentinelSelect.appendChild(option);
        probeSentinelSelect.disabled = true;
        return;
    }

    probeSentinelSelect.disabled = false;
    for (const sentinel of sentinels) {
        const option = document.createElement('option');
        option.value = String(sentinel.sentinelId);
        option.textContent = sentinel.canDiscover ? sentinel.name : `${sentinel.name}（帳密未設定）`;
        probeSentinelSelect.appendChild(option);
    }
    if (previousValue && sentinels.some(s => String(s.sentinelId) === previousValue)) {
        probeSentinelSelect.value = previousValue;
    }
}

/** 輪詢更新時只換文字節點、不重建 spinner（避免每次輪詢動畫重置閃爍） */
function setProbeSpinnerText(text) {
    if (!probeStateEl.querySelector('.spinner-border')) {
        renderSpinner(probeStateEl, text);
        return;
    }
    probeStateEl.querySelector('span:last-child').textContent = text;
}

function renderProbeStatus(status) {
    probeOutputEl.value = status.output || '';
    if (status.output) {
        probeOutputEl.scrollTop = probeOutputEl.scrollHeight;
    }
    probeCopyButton.disabled = !status.output;

    if (status.isRunning) {
        probeStartButton.disabled = true;
        setProbeSpinnerText(`執行中（${status.sentinelName}）…${status.latestMessage ? '　' + status.latestMessage : ''}`);
        return;
    }

    probeStartButton.disabled = false;
    if (!status.completedAt) {
        probeStateEl.textContent = '';
        return;
    }
    probeStateEl.textContent = `上次執行：${formatDateTime(status.completedAt)}　` +
        (status.success ? '✓ 完成（詳見下方輸出，個別步驟仍可能失敗，請自行檢視）' : '✗ 執行中發生錯誤，詳見下方輸出');
}

async function refreshProbeStatus() {
    let status;
    try {
        status = await api.get('/api/admin/netiq/probe/status', { silent: true });
    } catch {
        return;
    }
    renderProbeStatus(status);

    if (status.isRunning && !probePollTimer) {
        probePollTimer = setInterval(async () => {
            const latest = await api.get('/api/admin/netiq/probe/status', { silent: true }).catch(() => null);
            if (!latest) return;
            renderProbeStatus(latest);
            if (!latest.isRunning) {
                clearInterval(probePollTimer);
                probePollTimer = null;
            }
        }, 2000);
    }
}

probeStartButton.addEventListener('click', async () => {
    const sentinelId = Number(probeSentinelSelect.value);
    if (!sentinelId) {
        toast('請先選擇要診斷的 Sentinel', 'warning');
        return;
    }

    // 不用 withBusy：啟動成功後按鈕的 disabled 狀態要交給輪詢邏輯接管（診斷本身還在跑），
    // withBusy 的 restore 會在這裡的 await 之後無條件把 disabled 改回 false，跟輪詢邏輯打架
    probeStartButton.disabled = true;
    try {
        await api.post('/api/admin/netiq/probe/start', {
            sentinelId,
            sampleIp: document.getElementById('probe-sample-ip').value.trim() || null,
            sampleLinuxIp: document.getElementById('probe-sample-linux-ip').value.trim() || null
        });
        toast('已開始診斷，完成前可切換分頁，稍後回來查看即可', 'success');
        await refreshProbeStatus();
    } catch {
        // 錯誤訊息已由 api.js 以 toast 顯示；啟動失敗，交還控制權
        probeStartButton.disabled = false;
    }
});

probeCopyButton.addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText(probeOutputEl.value);
        toast('已複製診斷輸出', 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
});

guardLoad(sentinelListContainer, loadSentinels);
loadOptions();
refreshProbeStatus();
