/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、鏡像狀態與環境探測。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { bindTabs, toast, withBusy, renderSpinner, confirmAction } from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, formatUserName } from '../core/format.js';

bindTabs(document.getElementById('prtg-tabs'));

/** PRTG 認證方式切換：依選取模式切換 token / password / passhash 區塊顯示（只動 classList 不設 style.display） */
function syncPrtgAuthFields() {
    const authMode = document.getElementById('prtg-auth-mode')?.value ?? 'token';
    const usernameField = document.getElementById('prtg-auth-username-field');
    const tokenFields = document.getElementById('prtg-auth-token-fields');
    const passwordFields = document.getElementById('prtg-auth-password-fields');
    const passhashFields = document.getElementById('prtg-auth-passhash-fields');

    if (usernameField) {
        usernameField.classList.toggle('d-none', authMode === 'token');
    }
    if (tokenFields) {
        tokenFields.classList.toggle('d-none', authMode !== 'token');
    }
    if (passwordFields) {
        passwordFields.classList.toggle('d-none', authMode !== 'password');
    }
    if (passhashFields) {
        passhashFields.classList.toggle('d-none', authMode !== 'passhash');
    }

    const clearHidden = (inputId, checkboxId) => {
        const input = document.getElementById(inputId);
        if (input) input.value = '';
        const checkbox = document.getElementById(checkboxId);
        if (checkbox) checkbox.checked = false;
    };
    if (authMode !== 'token') clearHidden('prtg-api-token', 'prtg-clear-token');
    if (authMode !== 'password') clearHidden('prtg-password', 'prtg-clear-password');
    if (authMode !== 'passhash') clearHidden('prtg-passhash', 'prtg-clear-passhash');
}

function renderPrtgFields(settings) {
    document.getElementById('prtg-url').value = settings.prtgUrl ?? '';

    const authModeSelect = document.getElementById('prtg-auth-mode');
    if (authModeSelect) {
        authModeSelect.value = settings.prtgAuthMode || 'token';
    }

    document.getElementById('prtg-api-token').value = '';
    document.getElementById('prtg-clear-token').checked = false;

    const tokenHint = document.getElementById('prtg-api-token-hint');
    tokenHint.textContent = settings.prtgHasApiToken
        ? '已設定 API token；留空儲存＝沿用既有 API token，輸入新值才會覆蓋。'
        : '尚未設定 API token。';

    const usernameInput = document.getElementById('prtg-username');
    if (usernameInput) {
        usernameInput.value = settings.prtgUsername ?? '';
    }

    const passwordInput = document.getElementById('prtg-password');
    if (passwordInput) {
        passwordInput.value = '';
    }

    const clearPasswordCheck = document.getElementById('prtg-clear-password');
    if (clearPasswordCheck) {
        clearPasswordCheck.checked = false;
    }

    const passwordHint = document.getElementById('prtg-password-hint');
    if (passwordHint) {
        passwordHint.textContent = settings.prtgHasPassword
            ? '已設定密碼；留空儲存＝沿用既有密碼，輸入新值才會覆蓋。'
            : '尚未設定密碼。';
    }

    const passhashInput = document.getElementById('prtg-passhash');
    if (passhashInput) {
        passhashInput.value = '';
    }

    const clearPasshashCheck = document.getElementById('prtg-clear-passhash');
    if (clearPasshashCheck) {
        clearPasshashCheck.checked = false;
    }

    const passhashHint = document.getElementById('prtg-passhash-hint');
    if (passhashHint) {
        passhashHint.textContent = settings.prtgHasPasshash
            ? '已設定 passhash；留空儲存＝沿用既有 passhash，輸入新值才會覆蓋。'
            : '尚未設定 passhash。';
    }

    syncPrtgAuthFields();

    document.getElementById('prtg-ignore-ssl').checked = Boolean(settings.prtgIgnoreSslErrors);
    document.getElementById('prtg-timeout-seconds').value = settings.prtgTimeoutSeconds ?? 30;
    document.getElementById('prtg-test-result').replaceChildren();
    renderUpdatedAt(settings);
}

function renderUpdatedAt(settings) {
    const el = document.getElementById('prtg-config-updated');
    if (!el) return;
    if (!settings.updatedAt) {
        el.textContent = '尚未有人更新過（目前為系統預設值）。';
        return;
    }
    el.textContent = `最後更新：${formatDateTime(settings.updatedAt)}` +
        (settings.updatedByAccount ? `　更新者：${formatUserName(settings.updatedByDisplayName, settings.updatedByAccount)}` : '');
}

async function loadSettings() {
    const settings = await api.get('/api/admin/settings');
    renderPrtgFields(settings);
}

function bindPrtgTest() {
    const button = document.getElementById('prtg-test-btn');
    if (!button) return;

    button.addEventListener('click', async () => {
        const url = document.getElementById('prtg-url').value.trim();
        if (!url) {
            toast('請先輸入 PRTG 位址。', 'warning');
            return;
        }

        const resultEl = document.getElementById('prtg-test-result');
        const restore = withBusy(button, '測試中');
        try {
            const result = await api.post('/api/admin/settings/prtg-test', {
                url,
                authMode: document.getElementById('prtg-auth-mode')?.value ?? 'token',
                username: document.getElementById('prtg-username')?.value.trim() ?? '',
                password: document.getElementById('prtg-password')?.value || null,
                passhash: document.getElementById('prtg-passhash')?.value || null,
                apiToken: document.getElementById('prtg-api-token').value || null,
                ignoreSslErrors: document.getElementById('prtg-ignore-ssl').checked,
                timeoutSeconds: Number(document.getElementById('prtg-timeout-seconds').value) || 30
            }, { silent: true });

            const mark = result.success ? '✓' : '✗';
            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.elapsedMs != null
                ? `${mark} ${result.message}（耗時 ${result.elapsedMs}ms）`
                : `${mark} ${result.message}`;
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = `✗ ${error?.message || '測試連線失敗。'}`;
        } finally {
            restore();
        }
    });
}

function bindForm() {
    const form = document.getElementById('prtg-config-form');
    const saveButton = document.getElementById('prtg-config-save');
    if (!form || !saveButton) return;

    document.getElementById('prtg-auth-mode')?.addEventListener('change', syncPrtgAuthFields);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const restore = withBusy(saveButton, '儲存中');
        try {
            const current = await api.get('/api/admin/settings');
            const url = document.getElementById('prtg-url').value.trim();
            const authMode = document.getElementById('prtg-auth-mode')?.value ?? 'token';
            const username = document.getElementById('prtg-username')?.value.trim() ?? '';
            const password = document.getElementById('prtg-password')?.value || null;
            const clearPassword = document.getElementById('prtg-clear-password')?.checked ?? false;
            const passhash = document.getElementById('prtg-passhash')?.value || null;
            const clearPasshash = document.getElementById('prtg-clear-passhash')?.checked ?? false;
            const apiToken = document.getElementById('prtg-api-token')?.value || null;
            const clearApiToken = document.getElementById('prtg-clear-token')?.checked ?? false;
            const ignoreSsl = document.getElementById('prtg-ignore-ssl').checked;
            const timeoutSeconds = Number(document.getElementById('prtg-timeout-seconds').value) || 60;

            const payload = {
                ...current,
                prtgUrl: url,
                prtgAuthMode: authMode,
                prtgUsername: username,
                prtgPassword: password,
                clearPrtgPassword: clearPassword,
                prtgPasshash: passhash,
                clearPrtgPasshash: clearPasshash,
                prtgApiToken: apiToken,
                clearPrtgApiToken: clearApiToken,
                prtgIgnoreSslErrors: ignoreSsl,
                prtgTimeoutSeconds: timeoutSeconds
            };

            await api.put('/api/admin/settings', payload);
            toast('已儲存', 'success');
            await loadSettings();
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
        }
    });
}

// ── PRTG 鏡像狀態（批次F）──────────────────────────────────────────────

function renderPrtgMirror(data) {
    if (!data) return;

    const setTxt = (id, text) => {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    };

    setTxt('prtg-mirror-device-count', formatNumber(data.deviceCount));
    setTxt('prtg-mirror-sensor-count', formatNumber(data.sensorCount));
    setTxt('prtg-mirror-last-device-sync', `最後同步：${data.lastDeviceSync ? formatDateTime(data.lastDeviceSync) : '-'}`);
    setTxt('prtg-mirror-last-sensor-sync', `最後同步：${data.lastSensorSync ? formatDateTime(data.lastSensorSync) : '-'}`);
    setTxt('prtg-mirror-last-value-at', `數值：${data.lastValueAt ? formatDateTime(data.lastValueAt) : '-'}`);
    setTxt('prtg-mirror-last-state-change-at', `狀態變更：${data.lastStateChangeAt ? formatDateTime(data.lastStateChangeAt) : '-'}`);

    setTxt('prtg-mirror-map-date', `對應基準日：${data.mapDate ? formatDate(data.mapDate) : '無'}`);
    setTxt('prtg-mirror-map-ok', formatNumber(data.mapOk));
    setTxt('prtg-mirror-map-conflict', formatNumber(data.mapConflict));
    setTxt('prtg-mirror-map-unmatched', formatNumber(data.mapUnmatched));
    setTxt('prtg-mirror-whitelist-count', formatNumber(data.whitelistSensorCount));
    setTxt('prtg-mirror-whitelist-mapped', formatNumber(data.onMappedDeviceCount));

    const renderList = (bodyId, items, emptyText) => {
        const tbody = document.getElementById(bodyId);
        if (!tbody) return;
        tbody.replaceChildren();

        if (!items || items.length === 0) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = 5;
            td.className = 'text-muted text-center py-2';
            td.textContent = emptyText;
            tr.appendChild(td);
            tbody.appendChild(tr);
            return;
        }

        for (const item of items) {
            const tr = document.createElement('tr');

            const tdObjid = document.createElement('td');
            tdObjid.className = 'font-monospace';
            tdObjid.textContent = String(item.deviceObjid);

            const tdIp = document.createElement('td');
            tdIp.className = 'font-monospace';
            tdIp.textContent = item.ip || '-';

            const tdHost = document.createElement('td');
            tdHost.textContent = item.hostName || '-';

            const tdNote = document.createElement('td');
            tdNote.className = 'text-muted';
            tdNote.textContent = item.note || '-';

            const tdAction = document.createElement('td');
            const assignBtn = document.createElement('button');
            assignBtn.type = 'button';
            assignBtn.className = 'btn btn-sm btn-outline-primary py-0 text-nowrap';
            assignBtn.textContent = '指派給主機';
            assignBtn.addEventListener('click', () => openAssignModal(item.deviceObjid));
            tdAction.appendChild(assignBtn);

            tr.append(tdObjid, tdIp, tdHost, tdNote, tdAction);
            tbody.appendChild(tr);
        }
    };

    renderList('prtg-mirror-conflicts-body', data.conflicts, '無衝突項目');
    renderList('prtg-mirror-unmatched-body', data.unmatched, '無未對應項目');
}

function renderManualMaps(items) {
    const tbody = document.getElementById('prtg-mirror-manual-maps-body');
    if (!tbody) return;
    tbody.replaceChildren();

    if (!items || items.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 5;
        td.className = 'text-muted text-center py-2';
        td.textContent = '無人工對應項目';
        tr.appendChild(td);
        tbody.appendChild(tr);
        return;
    }

    for (const item of items) {
        const tr = document.createElement('tr');

        const tdObjid = document.createElement('td');
        tdObjid.className = 'font-monospace';
        tdObjid.textContent = String(item.deviceObjid);

        const tdHost = document.createElement('td');
        tdHost.textContent = item.hostName || `Host ID: ${item.hostId}`;

        const tdNote = document.createElement('td');
        tdNote.className = 'text-muted';
        tdNote.textContent = item.note || '-';

        const tdCreatedBy = document.createElement('td');
        tdCreatedBy.textContent = item.createdBy || '-';

        const tdAction = document.createElement('td');
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'btn btn-sm btn-outline-danger py-0 text-nowrap';
        removeBtn.textContent = '移除';
        removeBtn.addEventListener('click', () => {
            confirmAction(`確定要移除 PRTG device ${item.deviceObjid} 的人工主機對應嗎？`, async () => {
                try {
                    await api.delete(`/api/admin/settings/prtg-manual-map/${item.deviceObjid}`);
                    toast('已移除人工對應', 'success');
                    await refreshPrtgMirror();
                } catch (error) {
                    toast(error?.message || '移除失敗', 'danger');
                }
            });
        });
        tdAction.appendChild(removeBtn);

        tr.append(tdObjid, tdHost, tdNote, tdCreatedBy, tdAction);
        tbody.appendChild(tr);
    }
}

let assignModal = null;
let cachedHosts = null;

async function openAssignModal(deviceObjid) {
    const modalEl = document.getElementById('prtg-assign-modal');
    if (!modalEl) return;
    if (!assignModal) {
        assignModal = new bootstrap.Modal(modalEl);
    }

    document.getElementById('prtg-assign-device-objid').value = String(deviceObjid);
    document.getElementById('prtg-assign-note').value = '';

    const hostSelect = document.getElementById('prtg-assign-host');
    hostSelect.innerHTML = '<option value="">載入中…</option>';
    assignModal.show();

    try {
        if (!cachedHosts) {
            const res = await api.get('/api/admin/hosts?pageSize=2000', { silent: true });
            cachedHosts = (res?.items || res || []).filter(h => h.active);
        }
        hostSelect.innerHTML = '<option value="">請選擇主機…</option>';
        for (const host of cachedHosts) {
            const option = document.createElement('option');
            option.value = String(host.hostId);
            option.textContent = `${host.hostName}${host.ipAddress ? ` (${host.ipAddress})` : ''}`;
            hostSelect.appendChild(option);
        }
    } catch (error) {
        hostSelect.innerHTML = '<option value="">無法載入主機清單</option>';
        toast('載入主機清單失敗', 'danger');
    }
}

function bindAssignForm() {
    const form = document.getElementById('prtg-assign-form');
    const submitBtn = document.getElementById('prtg-assign-submit');
    if (!form || !submitBtn) return;

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const hostId = document.getElementById('prtg-assign-host').value;
        if (!hostId) {
            toast('請選擇目標主機', 'warning');
            return;
        }

        const deviceObjid = Number(document.getElementById('prtg-assign-device-objid').value);
        const note = document.getElementById('prtg-assign-note').value.trim() || null;

        const restore = withBusy(submitBtn, '指派中');
        try {
            await api.put('/api/admin/settings/prtg-manual-map', {
                deviceObjid,
                hostId: Number(hostId),
                note
            });
            toast('已指派', 'success');
            if (assignModal) assignModal.hide();
            await refreshPrtgMirror();
        } catch (error) {
            toast(error?.message || '指派失敗', 'danger');
        } finally {
            restore();
        }
    });
}

async function refreshPrtgMirror() {
    try {
        const [mirrorData, manualMaps] = await Promise.all([
            api.get('/api/admin/settings/prtg-mirror', { silent: true }),
            api.get('/api/admin/settings/prtg-manual-map', { silent: true })
        ]);
        renderPrtgMirror(mirrorData);
        renderManualMaps(manualMaps);
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function bindPrtgMirror() {
    const refreshBtn = document.getElementById('prtg-mirror-refresh');
    refreshBtn?.addEventListener('click', async () => {
        const restore = withBusy(refreshBtn, '載入中');
        try {
            await refreshPrtgMirror();
            toast('已重新整理 PRTG 鏡像狀態', 'success');
        } finally {
            restore();
        }
    });
}

// ── PRTG API 探測（批次B-4，比照 NetIQ 診斷實作）─────────────────────────

let prtgProbePollTimer = null;

/** 輪詢更新時只換文字節點、不重建 spinner（避免每次輪詢動畫重置閃爍） */
function setPrtgProbeSpinnerText(container, text) {
    if (!container.querySelector('.spinner-border')) {
        renderSpinner(container, text);
        return;
    }
    const label = container.querySelector('span:last-child');
    if (label) label.textContent = text;
}

function renderPrtgProbeStatus(status) {
    const outputEl = document.getElementById('prtg-probe-output');
    const copyButton = document.getElementById('prtg-probe-copy');
    const startButton = document.getElementById('prtg-probe-start');
    const statusEl = document.getElementById('prtg-probe-status');

    if (!outputEl || !copyButton || !startButton || !statusEl) return;

    // 探測區預設收合。執行中時自動展開——否則按下探測後重新整理頁面，
    // 進度與輸出會被收在摺疊區裡，看起來像什麼都沒發生
    if (status.isRunning) {
        const details = document.getElementById('prtg-probe');
        if (details) details.open = true;
    }

    const outputText = Array.isArray(status.output) ? status.output.join('\n') : (status.output || '');
    outputEl.value = outputText;
    if (outputText) {
        outputEl.scrollTop = outputEl.scrollHeight;
    }
    copyButton.disabled = !outputText;

    if (status.isRunning) {
        startButton.disabled = true;
        setPrtgProbeSpinnerText(statusEl, `探測中…${status.latestMessage ? ' ' + status.latestMessage : ''}`);
        return;
    }

    startButton.disabled = false;
    if (!status.completedAt) {
        statusEl.textContent = '';
        return;
    }
    statusEl.textContent = `上次執行：${formatDateTime(status.completedAt)} ` +
        (status.success ? '✓ 完成' : '✗ 執行中發生錯誤');
}

async function refreshPrtgProbeStatus() {
    let status;
    try {
        status = await api.get('/api/admin/settings/prtg-probe/status', { silent: true });
    } catch {
        return;
    }
    renderPrtgProbeStatus(status);

    if (status.isRunning && !prtgProbePollTimer) {
        prtgProbePollTimer = setInterval(async () => {
            const latest = await api.get('/api/admin/settings/prtg-probe/status', { silent: true }).catch(() => null);
            if (!latest) return;
            renderPrtgProbeStatus(latest);
            if (!latest.isRunning) {
                clearInterval(prtgProbePollTimer);
                prtgProbePollTimer = null;
            }
        }, 2000);
    }
}

function bindPrtgProbe() {
    const startButton = document.getElementById('prtg-probe-start');
    const copyButton = document.getElementById('prtg-probe-copy');
    const outputEl = document.getElementById('prtg-probe-output');
    if (!startButton || !copyButton || !outputEl) return;

    startButton.addEventListener('click', async () => {
        // 探測用的是「已儲存」的連線設定；表單連填都沒填時直接前置提示，不必打 API
        if (!document.getElementById('prtg-url')?.value.trim()) {
            toast('請先設定並儲存 PRTG 位址與認證資訊，再執行探測。', 'warning');
            return;
        }

        // 不用 withBusy：啟動成功後按鈕的 disabled 狀態交給輪詢狀態接管
        startButton.disabled = true;
        try {
            await api.post('/api/admin/settings/prtg-probe/start', {}, { silent: true });
            toast('已開始探測 PRTG 環境', 'success');
            await refreshPrtgProbeStatus();
        } catch (error) {
            // 啟動失敗（如尚未設定連線位址、與回填互斥）：訊息要讓使用者看得到，不能靜默
            startButton.disabled = false;
            toast(error?.message || '無法啟動 PRTG 探測。', 'danger');
        }
    });

    copyButton.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(outputEl.value);
            toast('已複製探測輸出', 'success');
        } catch {
            toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
        }
    });
}

// ── PRTG 資料搬運（任務G）──────────────────────────────────────────────

function bindPrtgDataTransfer() {
    const exportBtn = document.getElementById('prtg-export-btn');
    const importBtn = document.getElementById('prtg-import-btn');
    const importFile = document.getElementById('prtg-import-file');
    const importResult = document.getElementById('prtg-import-result');

    exportBtn?.addEventListener('click', () => {
        const from = document.getElementById('prtg-export-from')?.value?.trim();
        const to = document.getElementById('prtg-export-to')?.value?.trim();

        if (!from || !to) {
            toast('請選擇匯出起始與結束日期。', 'warning');
            return;
        }

        if (from > to) {
            toast('匯出起始日期不得大於結束日期。', 'warning');
            return;
        }

        const url = appUrl(`/api/admin/settings/prtg-export?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
        window.location.assign(url);
    });

    importBtn?.addEventListener('click', async () => {
        const file = importFile?.files?.[0];
        if (!file) {
            toast('請先選擇要匯入的 JSON 檔案。', 'warning');
            return;
        }

        const restore = withBusy(importBtn, '匯入中');
        if (importResult) {
            importResult.className = 'small mb-2 text-muted';
            importResult.textContent = '匯入處理中…';
        }

        try {
            const formData = new FormData();
            formData.append('file', file);

            const response = await fetch(appUrl('/api/admin/settings/prtg-import'), {
                method: 'POST',
                body: formData,
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-By': 'LogForesight'
                },
                credentials: 'same-origin'
            });

            let payload;
            try {
                payload = await response.json();
            } catch {
                throw new Error('伺服器回應格式不符。');
            }

            if (!response.ok || !payload.success) {
                const errMsg = payload?.error?.message || payload?.message || `匯入失敗（HTTP ${response.status}）`;
                throw new Error(errMsg);
            }

            const data = payload.data;
            const msg = `匯入成功：裝置 ${formatNumber(data.devices)} 筆、感測器 ${formatNumber(data.sensors)} 筆、狀態變更 ${formatNumber(data.stateChanges)} 筆、數值 ${formatNumber(data.values)} 筆、主機對應 ${formatNumber(data.hostMaps)} 筆、人工對應 ${formatNumber(data.manualMaps)} 筆。`;
            if (importResult) {
                importResult.className = 'small mb-2 text-success';
                importResult.textContent = msg;
            }
            toast('PRTG 鏡像資料匯入完成', 'success');
            if (importFile) importFile.value = '';
            await refreshPrtgMirror();
        } catch (error) {
            const errMsg = error?.message || '匯入時發生未知錯誤。';
            if (importResult) {
                importResult.className = 'small mb-2 text-danger';
                importResult.textContent = `匯入失敗：${errMsg}`;
            }
            toast(errMsg, 'danger');
        } finally {
            restore();
        }
    });
}

// ── 初始化 ───────────────────────────────────────────────────────────────────

function init() {
    bindPrtgTest();
    bindPrtgMirror();
    bindPrtgProbe();
    bindAssignForm();
    bindPrtgDataTransfer();
    bindForm();
    loadSettings();
    refreshPrtgMirror();
    refreshPrtgProbeStatus();
}

init();
