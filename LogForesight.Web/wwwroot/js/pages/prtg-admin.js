/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、擷取參數、鏡像狀態與環境探測。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { PROGRESS_PHASE_LABEL } from '../core/run-phases.js';
import {
    bindTabs, toast, withBusy, renderSpinner, confirmAction,
    renderPagination, loadPageSize, savePageSize, PAGE_SIZE_OPTIONS,
    collectLines, numberOr
} from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, formatUserName } from '../core/format.js';
import { initCalibration } from './prtg-calibration.js';
import { PRTG_SCOPE_OFF, toScopeSelectValue } from '../core/prtg-scope-labels.js';

bindTabs(document.getElementById('prtg-tabs'), { hash: true });

/** 目前已儲存的 PRTG 擷取開關。鏡像頁籤的「同步結構與對應」關閉時要擋住（後端也會擋，這是提前告知）。 */
let prtgEnabled = false;

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

let historyRetentionDays = null;

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
    document.getElementById('prtg-timeout-seconds').value = settings.prtgTimeoutSeconds ?? 60;
    document.getElementById('prtg-fetch-concurrency').value = settings.prtgFetchConcurrency ?? 2;
    document.getElementById('prtg-backfill-days').value = settings.prtgBackfillDays ?? 30;
    document.getElementById('prtg-retention-days').value = settings.prtgRetentionDays ?? 180;
    document.getElementById('prtg-sensor-type-whitelist').value =
        (settings.prtgSensorTypeWhitelist ?? []).join('\n');


    prtgEnabled = Boolean(settings.prtgEnabled);
    const scopeSelect = document.getElementById('prtg-value-fetch-scope');
    if (scopeSelect) {
        // 「關閉」與三個範圍是同一個下拉：未啟用一律顯示關閉，啟用時顯示已存的範圍
        scopeSelect.value = toScopeSelectValue(prtgEnabled, settings.prtgValueFetchScope);
        syncScopeFields();
    }
    syncStructureSyncGate();
    const extraHosts = document.getElementById('prtg-value-fetch-extra-hosts');
    if (extraHosts) extraHosts.value = (settings.prtgValueFetchExtraHosts ?? []).join('\n');
    document.getElementById('prtg-scope-estimate-result')?.replaceChildren();

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
    historyRetentionDays = settings.retentionDays;
    renderPrtgFields(settings);
}

/**
 * 取數範圍切換：只有「觸發主機＋指定清單」需要主機名稱輸入框；
 * 選「關閉」時連「估算規模」都沒有意義（不會取數），一併藏起來。
 * 用 classList 切換而非 style.display（同本頁認證方式切換的既有作法）。
 */
function syncScopeFields() {
    const scope = document.getElementById('prtg-value-fetch-scope')?.value ?? PRTG_SCOPE_OFF;
    const off = scope === PRTG_SCOPE_OFF;
    document.getElementById('prtg-value-fetch-extra-hosts-group')
        ?.classList.toggle('d-none', off || scope !== 'triggered-plus-list');
    document.getElementById('prtg-scope-estimate-btn')?.classList.toggle('d-none', off);
    if (off) document.getElementById('prtg-scope-estimate-result')?.replaceChildren();
}

/**
 * 鏡像頁籤「同步結構與對應」的閘：擷取未啟用時同步一定被後端拒絕（PrtgStructureSyncService），
 * 讓按鈕直接灰掉並說去哪開，比按下去看紅字有用。以「已儲存的值」為準——
 * 下拉改了還沒存不算啟用，否則會讓人以為存過了。
 */
function syncStructureSyncGate() {
    const btn = document.getElementById('prtg-structure-sync-btn');
    if (btn) btn.disabled = !prtgEnabled;
    document.getElementById('prtg-structure-sync-disabled-hint')
        ?.classList.toggle('d-none', prtgEnabled);
}

function bindScopeControls() {
    const select = document.getElementById('prtg-value-fetch-scope');
    if (select) select.addEventListener('change', syncScopeFields);

    const button = document.getElementById('prtg-scope-estimate-btn');
    const result = document.getElementById('prtg-scope-estimate-result');
    if (!button || !result) return;

    button.addEventListener('click', async () => {
        const restore = withBusy(button, '估算中');
        result.replaceChildren();
        result.className = 'small';

        try {
            // 估算的是「目前選的模式」而非已儲存的模式——管理者是在決定要不要改設定。
            const scope = document.getElementById('prtg-value-fetch-scope')?.value ?? 'triggered';
            const res = await api.get(
                `/api/admin/settings/prtg-fetch-scope/estimate?scope=${encodeURIComponent(scope)}`,
                { silent: true });

            if (!res.success) {
                result.className = 'text-danger small';
                result.textContent = res.errorMessage || '估算失敗。';
                return;
            }

            if (scope === 'triggered') {
                result.className = 'text-muted small';
                result.textContent = '只抓觸發主機：數量逐日變動，事前無法估算。';
                return;
            }

            // 觸發主機那一半事前算不出來，估的只有指定清單——要說清楚，否則會被讀成整個模式的規模
            const prefix = scope === 'triggered-plus-list' ? '指定清單部分：' : '';
            const base = `${prefix}主機 ${formatNumber(res.hosts)} 台、device ${formatNumber(res.devices)} 個、` +
                         `sensor ${formatNumber(res.sensors)} 個/晚`;

            if (res.warning) {
                result.className = 'text-warning small';
                result.textContent = `⚠ ${base}——${res.warning}`;
            } else {
                result.className = 'text-success small';
                result.textContent = base;
            }
        } catch (error) {
            result.className = 'text-danger small';
            result.textContent = error?.message || '估算失敗。';
        } finally {
            restore();
        }
    });
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
                timeoutSeconds: Number(document.getElementById('prtg-timeout-seconds').value) || 60
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

function bindConnectionForm() {
    const form = document.getElementById('prtg-connection-form');
    const saveButton = document.getElementById('prtg-connection-save');
    if (!form || !saveButton) return;

    document.getElementById('prtg-auth-mode')?.addEventListener('change', syncPrtgAuthFields);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const restore = withBusy(saveButton, '儲存中');
        try {
            const url = document.getElementById('prtg-url').value.trim();
            const authMode = document.getElementById('prtg-auth-mode')?.value ?? 'token';
            const username = document.getElementById('prtg-username')?.value.trim() ?? '';
            const password = document.getElementById('prtg-password')?.value || null;
            const clearPassword = document.getElementById('prtg-clear-password')?.checked ?? false;
            const passhash = document.getElementById('prtg-passhash')?.value || null;
            const clearPasshash = document.getElementById('prtg-clear-passhash')?.checked ?? false;
            const apiToken = document.getElementById('prtg-api-token')?.value || null;
            const clearApiToken = document.getElementById('prtg-clear-token')?.checked ?? false;

            const payload = {
                prtgUrl: url,
                prtgAuthMode: authMode,
                prtgUsername: username,
                prtgPassword: password,
                clearPrtgPassword: clearPassword,
                prtgPasshash: passhash,
                clearPrtgPasshash: clearPasshash,
                prtgApiToken: apiToken,
                clearPrtgApiToken: clearApiToken
            };

            await api.put('/api/admin/settings/prtg', payload);
            toast('已儲存', 'success');
            await loadSettings();
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
        }
    });
}

/** 讀數字欄位：空白或非數字才回 fallback，0 是合法值（例如可用記憶體門檻）不可被 `||` 吞掉。 */

function bindParamsForm() {
    const form = document.getElementById('prtg-params-form');
    const saveButton = document.getElementById('prtg-params-save');
    if (!form || !saveButton) return;

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const prtgRetentionDays = Number(document.getElementById('prtg-retention-days')?.value) || 180;
        if (historyRetentionDays != null && prtgRetentionDays > historyRetentionDays) {
            toast('PRTG 資料保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        const restore = withBusy(saveButton, '儲存中');
        try {
            const ignoreSsl = document.getElementById('prtg-ignore-ssl').checked;
            const timeoutSeconds = Number(document.getElementById('prtg-timeout-seconds').value) || 60;
            const fetchConcurrency = Number(document.getElementById('prtg-fetch-concurrency').value) || 2;
            const backfillDays = Number(document.getElementById('prtg-backfill-days').value) || 30;

            // 下拉的「關閉」對應 prtgEnabled=false，此時不送 prtgValueFetchScope——
            // 範圍留著原值，下次重新啟用不必再選一次
            const scopeValue = document.getElementById('prtg-value-fetch-scope')?.value ?? PRTG_SCOPE_OFF;
            const enabled = scopeValue !== PRTG_SCOPE_OFF;

            const payload = {
                prtgEnabled: enabled,
                prtgIgnoreSslErrors: ignoreSsl,
                prtgTimeoutSeconds: timeoutSeconds,
                prtgFetchConcurrency: fetchConcurrency,
                prtgBackfillDays: backfillDays,
                prtgRetentionDays: prtgRetentionDays,
                prtgSensorTypeWhitelist: collectLines('prtg-sensor-type-whitelist'),
                prtgValueFetchExtraHosts: collectLines('prtg-value-fetch-extra-hosts'),
                // 關閉時整個鍵不送：範圍留著原值，下次重新啟用不必再選一次
                ...(enabled ? { prtgValueFetchScope: scopeValue } : {})
            };

            await api.put('/api/admin/settings/prtg', payload);
            toast('已儲存', 'success');
            await loadSettings();
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
        }
    });
}

function notifyRemapWarning(res) {
    if (res && res.remapWarning) {
        toast(res.remapWarning, 'warning');
    }
}

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
    setTxt('prtg-mirror-ip-exclude-count', formatNumber(data.ipExcludeCount || 0));
    setTxt('prtg-mirror-whitelist-count', formatNumber(data.whitelistSensorCount));
    setTxt('prtg-mirror-whitelist-mapped', formatNumber(data.onMappedDeviceCount));
}

// ── PRTG 鏡像狀態與衝突處理 ──────────────────────────────────────────────

let conflictPage = 1;
let conflictPageSize = loadPageSize('prtg-conflicts');

async function refreshConflicts(page = conflictPage) {
    conflictPage = page;
    try {
        const res = await api.get(`/api/admin/settings/prtg-host-map?status=conflict&page=${conflictPage}&pageSize=${conflictPageSize}`, { silent: true });
        const total = (res && res.total) ? res.total : 0;
        const totalPages = Math.ceil(total / conflictPageSize);

        if (conflictPage > totalPages && totalPages > 0) {
            return refreshConflicts(totalPages);
        }

        renderConflicts((res && res.items) ? res.items : []);
        renderConflictPagination(totalPages);
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function renderConflicts(items) {
    const tbody = document.getElementById('prtg-mirror-conflicts-body');
    if (!tbody) return;
    tbody.replaceChildren();

    if (!items || items.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 5;
        td.className = 'text-muted text-center py-2';
        td.textContent = '無衝突項目';
        tr.appendChild(td);
        tbody.appendChild(tr);
        return;
    }

    for (const item of items) {
        const tr = document.createElement('tr');

        const tdDevice = document.createElement('td');
        tdDevice.textContent = item.deviceName ? `${item.deviceObjid} ${item.deviceName}` : String(item.deviceObjid);

        const tdIp = document.createElement('td');
        tdIp.className = 'font-monospace';
        tdIp.textContent = item.ip || '-';

        const tdKind = document.createElement('td');
        const kindBadge = document.createElement('span');
        if (item.conflictKind === 'multi-device') {
            kindBadge.className = 'badge bg-warning text-dark';
            kindBadge.textContent = '同 IP 多裝置';
        } else if (item.conflictKind === 'multi-host') {
            kindBadge.className = 'badge bg-info text-dark';
            kindBadge.textContent = 'IP 對多主機';
        } else {
            kindBadge.className = 'badge bg-secondary';
            kindBadge.textContent = item.conflictKind || '-';
        }
        tdKind.appendChild(kindBadge);

        const tdNote = document.createElement('td');
        tdNote.className = 'text-muted';
        tdNote.textContent = item.note || '-';

        const tdAction = document.createElement('td');
        const assignBtn = document.createElement('button');
        assignBtn.type = 'button';
        assignBtn.className = 'btn btn-sm btn-outline-primary py-0 text-nowrap';
        assignBtn.textContent = '指派';
        assignBtn.addEventListener('click', () => openAssignModal(item));
        tdAction.appendChild(assignBtn);

        const excludeBtn = document.createElement('button');
        excludeBtn.type = 'button';
        excludeBtn.className = 'btn btn-sm btn-outline-danger py-0 text-nowrap ms-1';
        excludeBtn.textContent = '排除此 IP';
        if (!item.ip) {
            excludeBtn.disabled = true;
            excludeBtn.title = '此 device 沒有 IP';
        } else {
            excludeBtn.addEventListener('click', async () => {
                const deviceCount = (item.sameIpDevices && item.sameIpDevices.length > 0) ? item.sameIpDevices.length : 1;
                const confirmed = await confirmAction({
                    message: `將排除 IP ${item.ip}：此 IP 底下的 ${deviceCount} 台 PRTG device 都不會再進行主機對應與取數。`
                });
                if (!confirmed) return;
                try {
                    const res = await api.put('/api/admin/settings/prtg-ip-excludes', { ip: item.ip, note: null });
                    toast('已排除此 IP', 'success');
                    notifyRemapWarning(res);
                    await Promise.all([
                        refreshPrtgMirror(),
                        refreshConflicts(conflictPage),
                        refreshIpExcludes()
                    ]);
                } catch (error) {
                    toast(error && error.message ? error.message : '排除失敗', 'danger');
                }
            });
        }
        tdAction.appendChild(excludeBtn);

        tr.append(tdDevice, tdIp, tdKind, tdNote, tdAction);
        tbody.appendChild(tr);
    }
}

function renderConflictPagination(totalPages) {
    const container = document.getElementById('prtg-conflicts-pagination');
    if (!container) return;

    renderPagination(container, {
        page: conflictPage,
        totalPages,
        onPage: page => {
            refreshConflicts(page);
        },
        pageSize: conflictPageSize,
        onPageSize: size => {
            conflictPageSize = size;
            savePageSize('prtg-conflicts', size);
            refreshConflicts(1);
        },
        pageSizeOptions: PAGE_SIZE_OPTIONS
    });
}

async function refreshIpExcludes() {
    try {
        const items = await api.get('/api/admin/settings/prtg-ip-excludes', { silent: true });
        renderIpExcludes(items || []);
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function renderIpExcludes(items) {
    const tbody = document.getElementById('prtg-ip-excludes-body');
    if (!tbody) return;
    tbody.replaceChildren();

    if (!items || items.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 5;
        td.className = 'text-muted text-center py-2';
        td.textContent = '無排除 IP';
        tr.appendChild(td);
        tbody.appendChild(tr);
        return;
    }

    for (const item of items) {
        const tr = document.createElement('tr');

        const tdIp = document.createElement('td');
        tdIp.className = 'font-monospace';
        tdIp.textContent = item.ip || '-';

        const tdNote = document.createElement('td');
        tdNote.className = 'text-muted';
        tdNote.textContent = item.note || '-';

        const tdCreatedBy = document.createElement('td');
        tdCreatedBy.textContent = item.createdBy || '-';

        const tdCreatedAt = document.createElement('td');
        tdCreatedAt.textContent = item.createdAt ? formatDateTime(item.createdAt) : '-';

        const tdAction = document.createElement('td');
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'btn btn-sm btn-outline-danger py-0 text-nowrap';
        removeBtn.textContent = '移除';
        removeBtn.addEventListener('click', async () => {
            const confirmed = await confirmAction({
                message: `確定要移除 IP ${item.ip} 的排除設定嗎？移除後此 IP 會恢復自動對應。`
            });
            if (!confirmed) return;
            try {
                const res = await api.delete(`/api/admin/settings/prtg-ip-excludes/${encodeURIComponent(item.ip)}`);
                toast('已移除 IP 排除設定', 'success');
                notifyRemapWarning(res);
                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage),
                    refreshIpExcludes()
                ]);
            } catch (error) {
                toast(error && error.message ? error.message : '移除失敗', 'danger');
            }
        });
        tdAction.appendChild(removeBtn);

        tr.append(tdIp, tdNote, tdCreatedBy, tdCreatedAt, tdAction);
        tbody.appendChild(tr);
    }
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
        if (item.sameIpSkippedCount && item.sameIpSkippedCount > 0) {
            const span = document.createElement('span');
            span.className = 'text-muted ms-1';
            span.textContent = `（同 IP 另有 ${item.sameIpSkippedCount} 台 device 已略過）`;
            tdNote.appendChild(span);
        }

        const tdCreatedBy = document.createElement('td');
        tdCreatedBy.textContent = item.createdBy || '-';

        const tdAction = document.createElement('td');
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'btn btn-sm btn-outline-danger py-0 text-nowrap';
        removeBtn.textContent = '移除';
        removeBtn.addEventListener('click', async () => {
            const confirmed = await confirmAction({
                message: `確定要移除 PRTG device ${item.deviceObjid} 的人工主機對應嗎？移除後此 device 會回到自動 IP 對應判定。`
            });
            if (!confirmed) return;
            try {
                const res = await api.delete(`/api/admin/settings/prtg-manual-map/${item.deviceObjid}`);
                toast('已移除人工對應', 'success');
                notifyRemapWarning(res);
                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage),
                    refreshIpExcludes()
                ]);
            } catch (error) {
                toast(error && error.message ? error.message : '移除失敗', 'danger');
            }
        });
        tdAction.appendChild(removeBtn);

        tr.append(tdObjid, tdHost, tdNote, tdCreatedBy, tdAction);
        tbody.appendChild(tr);
    }
}

let assignModal = null;
let cachedHosts = null;
let currentAssignItem = null;
let currentFixedHostId = null;

async function openAssignModal(item) {
    const modalEl = document.getElementById('prtg-assign-modal');
    if (!modalEl) return;
    if (!assignModal) {
        assignModal = new bootstrap.Modal(modalEl);
    }

    currentAssignItem = item;
    currentFixedHostId = null;

    const titleEl = document.getElementById('prtg-assign-modal-title');
    const ipText = item.ip || '無 IP';
    if (titleEl) {
        titleEl.textContent = `指派 device ${item.deviceObjid}（${ipText}）`;
    }

    document.getElementById('prtg-assign-note').value = '';

    const deviceChoiceEl = document.getElementById('prtg-assign-device-choice');
    const hostFixedEl = document.getElementById('prtg-assign-host-fixed');
    const hostSelect = document.getElementById('prtg-assign-host');
    const hostSelectGroup = document.getElementById('prtg-assign-host-select-group');

    // 依 conflictKind 設定裝置選擇
    if (item.conflictKind === 'multi-device') {
        deviceChoiceEl.replaceChildren();
        deviceChoiceEl.classList.remove('d-none');

        const choiceLabel = document.createElement('label');
        choiceLabel.className = 'form-label fw-semibold small mb-2';
        choiceLabel.textContent = '選擇 PRTG 裝置';
        deviceChoiceEl.appendChild(choiceLabel);

        const devices = (item.sameIpDevices && item.sameIpDevices.length > 0)
            ? item.sameIpDevices
            : [{ objid: item.deviceObjid, name: item.deviceName, groupPath: item.groupPath }];

        for (const dev of devices) {
            const formCheck = document.createElement('div');
            formCheck.className = 'form-check mb-1';

            const radio = document.createElement('input');
            radio.type = 'radio';
            radio.className = 'form-check-input';
            radio.name = 'prtg-assign-device-radio';
            radio.id = `prtg-assign-dev-${dev.objid}`;
            radio.value = String(dev.objid);
            if (Number(dev.objid) === Number(item.deviceObjid)) {
                radio.checked = true;
            }
            radio.addEventListener('change', () => {
                if (radio.checked && titleEl) {
                    titleEl.textContent = `指派 device ${dev.objid}（${ipText}）`;
                }
            });

            const label = document.createElement('label');
            label.className = 'form-check-label font-monospace small';
            label.htmlFor = `prtg-assign-dev-${dev.objid}`;
            let text = `${dev.objid}　${dev.name || ''}`;
            if (dev.groupPath) {
                text += ` (${dev.groupPath})`;
            }
            label.textContent = text;

            formCheck.append(radio, label);
            deviceChoiceEl.appendChild(formCheck);
        }

        if (!deviceChoiceEl.querySelector('input[name="prtg-assign-device-radio"]:checked') && devices.length > 0) {
            const firstRadio = deviceChoiceEl.querySelector('input[name="prtg-assign-device-radio"]');
            if (firstRadio) firstRadio.checked = true;
        }
    } else {
        deviceChoiceEl.replaceChildren();
        deviceChoiceEl.classList.add('d-none');
    }

    const candidates = item.candidateHosts || [];

    // 依候選主機數量決定目標主機顯示
    if (item.conflictKind === 'multi-device' && candidates.length === 1) {
        currentFixedHostId = candidates[0].hostId;
        hostFixedEl.replaceChildren();
        hostFixedEl.classList.remove('d-none');

        const fixedTitle = document.createElement('label');
        fixedTitle.className = 'form-label text-muted small mb-1';
        fixedTitle.textContent = '目標主機';
        const fixedVal = document.createElement('div');
        fixedVal.className = 'fw-semibold';
        const ch = candidates[0];
        fixedVal.textContent = `${ch.hostName}${ch.ipAddress ? ` (${ch.ipAddress})` : ''}`;
        hostFixedEl.append(fixedTitle, fixedVal);

        if (hostSelectGroup) hostSelectGroup.classList.add('d-none');
        assignModal.show();
    } else if (candidates.length === 0) {
        hostFixedEl.replaceChildren();
        hostFixedEl.classList.add('d-none');
        if (hostSelectGroup) hostSelectGroup.classList.remove('d-none');

        hostSelect.innerHTML = '<option value="">載入中…</option>';
        assignModal.show();

        try {
            cachedHosts = await api.get('/api/admin/hosts/all', { silent: true });
            hostSelect.innerHTML = '<option value="">請選擇主機…</option>';
            for (const host of (cachedHosts || [])) {
                const option = document.createElement('option');
                option.value = String(host.hostId);
                option.textContent = `${host.hostName}${host.ipAddress ? ` (${host.ipAddress})` : ''}`;
                hostSelect.appendChild(option);
            }
        } catch {
            hostSelect.innerHTML = '<option value="">無法載入主機清單</option>';
            toast('載入主機清單失敗', 'danger');
        }
    } else {
        hostFixedEl.replaceChildren();
        hostFixedEl.classList.add('d-none');
        if (hostSelectGroup) hostSelectGroup.classList.remove('d-none');

        hostSelect.innerHTML = '<option value="">請選擇主機…</option>';
        for (const host of candidates) {
            const option = document.createElement('option');
            option.value = String(host.hostId);
            option.textContent = `${host.hostName}${host.ipAddress ? ` (${host.ipAddress})` : ''}`;
            if (item.hostName && host.hostName === item.hostName) {
                option.selected = true;
            }
            hostSelect.appendChild(option);
        }
        assignModal.show();
    }
}

function bindAssignForm() {
    const form = document.getElementById('prtg-assign-form');
    const submitBtn = document.getElementById('prtg-assign-submit');
    if (!form || !submitBtn) return;

    form.addEventListener('submit', async event => {
        event.preventDefault();

        if (!currentAssignItem) return;

        let targetDeviceObjid = currentAssignItem.deviceObjid;
        if (currentAssignItem.conflictKind === 'multi-device') {
            const checkedRadio = document.querySelector('input[name="prtg-assign-device-radio"]:checked');
            if (checkedRadio) {
                targetDeviceObjid = Number(checkedRadio.value);
            }
        }

        let targetHostId = currentFixedHostId;
        if (!targetHostId) {
            const hostSelectVal = document.getElementById('prtg-assign-host').value;
            if (!hostSelectVal) {
                toast('請選擇目標主機', 'warning');
                return;
            }
            targetHostId = Number(hostSelectVal);
        }

        const note = document.getElementById('prtg-assign-note').value.trim() || null;

        const restore = withBusy(submitBtn, '指派中');
        try {
            const res = await api.put('/api/admin/settings/prtg-manual-map', {
                deviceObjid: targetDeviceObjid,
                hostId: targetHostId,
                note
            });
            toast('已指派', 'success');
            notifyRemapWarning(res);
            if (assignModal) assignModal.hide();
            await Promise.all([
                refreshPrtgMirror(),
                refreshConflicts(conflictPage),
                refreshIpExcludes()
            ]);
        } catch (error) {
            toast(error && error.message ? error.message : '指派失敗', 'danger');
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
            await Promise.all([
                refreshPrtgMirror(),
                refreshConflicts(conflictPage),
                refreshIpExcludes()
            ]);
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

            const data = await api.post('/api/admin/settings/prtg-import', formData);

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
        } finally {
            restore();
        }
    });
}

// ── 初始化 ───────────────────────────────────────────────────────────────────

// ── 同步結構與對應（docs/PRTG-SPEC.md §5a）────────────────────────────

let structureSyncTimer = null;

/**
 * 把同步狀態畫到鏡像頁籤的卡片上。
 * 執行中每 3 秒輪詢一次；結束後停掉輪詢並重新載入鏡像統計，
 * 讓「裝置數／感測器數／對應結果」立刻反映這次同步的成果。
 */
function renderStructureSyncStatus(status) {
    const statusEl = document.getElementById('prtg-structure-sync-status');
    const progressEl = document.getElementById('prtg-structure-sync-progress');
    const btn = document.getElementById('prtg-structure-sync-btn');
    if (!statusEl || !progressEl || !btn) return;

    if (status.isRunning) {
        btn.disabled = true;
        btn.textContent = '同步中…';
        statusEl.textContent = status.latestMessage || '同步進行中…';
        const phase = status.progressPhase;
        // 走與排程作業頁同一份對照表，不把 prtg-sync-devices 這種裸 phase 印給使用者
        const label = PROGRESS_PHASE_LABEL[phase] ?? phase;
        progressEl.textContent = phase && status.progressTotal > 0
            ? `${label}：${status.progressDone} / ${status.progressTotal}`
            : (phase ? `${label}：進行中` : '');
        return;
    }

    // 未啟用時的閘由 syncStructureSyncGate 設定；這裡是輪詢，不能把它打開
    btn.disabled = !prtgEnabled;
    btn.textContent = '同步結構與對應';
    progressEl.textContent = '';

    if (!status.lastCompletedAt) {
        // 從未同步過與「同步到 0 筆」是兩件事，文案要分得出來
        statusEl.textContent = '尚未同步。PRTG 剛啟用時請先執行一次，否則主機對應與資源守門的自動偵測都沒有資料可用。';
        return;
    }

    const when = formatDateTime(status.lastCompletedAt);
    if (status.lastSuccess) {
        statusEl.textContent =
            `上次同步：${when}　裝置 ${status.lastDevices ?? 0}、感測器 ${status.lastSensors ?? 0}；`
            + `對應成功 ${status.lastMapOk ?? 0}、人工 ${status.lastMapManual ?? 0}、`
            + `衝突 ${status.lastMapConflict ?? 0}、查無主機 ${status.lastMapUnmatched ?? 0}、`
            + `略過 ${status.lastMapSkipped ?? 0}`;
    } else {
        statusEl.textContent = `上次同步（${when}）未成功：${status.lastErrorMessage || '原因不明'}`;
    }
}

async function refreshStructureSyncStatus() {
    try {
        const status = await api.get('/api/admin/settings/prtg-structure-sync/status', { silent: true });
        renderStructureSyncStatus(status);

        if (status.isRunning) {
            if (!structureSyncTimer) {
                structureSyncTimer = setInterval(refreshStructureSyncStatus, 3000);
            }
        } else if (structureSyncTimer) {
            clearInterval(structureSyncTimer);
            structureSyncTimer = null;
            await refreshPrtgMirror();
        }
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function bindStructureSync() {
    const btn = document.getElementById('prtg-structure-sync-btn');
    btn?.addEventListener('click', async () => {
        // 按鈕已依模組狀態灰掉，這裡是輪詢競態時的第二道（後端還有第三道）
        if (!prtgEnabled) {
            toast('PRTG 擷取未啟用，請先在「擷取參數」頁籤選擇取數範圍。', 'warning');
            return;
        }
        const restore = withBusy(btn, '啟動中');
        try {
            await api.post('/api/admin/settings/prtg-structure-sync/start', {});
            toast('已開始同步結構與對應', 'success');
            await refreshStructureSyncStatus();
        } finally {
            restore();
        }
    });
}

function init() {
    bindPrtgTest();
    bindPrtgMirror();
    bindPrtgProbe();
    bindAssignForm();
    bindPrtgDataTransfer();
    bindConnectionForm();
    bindParamsForm();
    bindScopeControls();
    bindStructureSync();
    initCalibration();
    loadSettings();
    refreshPrtgMirror();
    refreshPrtgProbeStatus();
    refreshStructureSyncStatus();
}

init();
