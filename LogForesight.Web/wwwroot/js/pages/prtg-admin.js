/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、擷取參數、鏡像狀態與環境探測。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    bindTabs, toast, withBusy, renderSpinner, confirmAction,
    renderPagination, loadPageSize, savePageSize, PAGE_SIZE_OPTIONS
} from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, formatUserName } from '../core/format.js';
import { initCalibration } from './prtg-calibration.js';

bindTabs(document.getElementById('prtg-tabs'), { hash: true });

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

    const guardEnabled = document.getElementById('prtg-guard-enabled');
    if (guardEnabled) guardEnabled.checked = Boolean(settings.prtgResourceGuardEnabled);
    const guardCpu = document.getElementById('prtg-guard-cpu-percent');
    if (guardCpu) guardCpu.value = settings.prtgResourceGuardCpuPercent ?? 85;
    const guardMemory = document.getElementById('prtg-guard-memory-free-percent');
    if (guardMemory) guardMemory.value = settings.prtgResourceGuardMemoryFreePercent ?? 10;
    const guardCheck = document.getElementById('prtg-guard-check-seconds');
    if (guardCheck) guardCheck.value = settings.prtgResourceGuardCheckSeconds ?? 60;
    const guardPause = document.getElementById('prtg-guard-pause-minutes');
    if (guardPause) guardPause.value = settings.prtgResourceGuardPauseMinutes ?? 5;
    const guardStrikes = document.getElementById('prtg-guard-strikes');
    if (guardStrikes) guardStrikes.value = settings.prtgResourceGuardStrikes ?? 2;
    const guardMaxPause = document.getElementById('prtg-guard-max-pause-minutes');
    if (guardMaxPause) guardMaxPause.value = settings.prtgResourceGuardMaxPauseMinutes ?? 120;
    const guardSensors = document.getElementById('prtg-guard-sensor-objids');
    if (guardSensors) guardSensors.value = (settings.prtgResourceGuardSensorObjids ?? []).join('\n');
    document.getElementById('prtg-guard-preview-result')?.replaceChildren();

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

function collectLines(id) {
    const value = document.getElementById(id)?.value ?? '';
    return value.split('\n')
        .map(line => line.trim())
        .filter(line => line.length > 0);
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

function bindGuardForm() {
    const form = document.getElementById('prtg-guard-form');
    const saveButton = document.getElementById('prtg-guard-save');
    if (!form || !saveButton) return;

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const restore = withBusy(saveButton, '儲存中');
        try {
            const enabled = document.getElementById('prtg-guard-enabled')?.checked ?? false;
            const cpuPercent = Number(document.getElementById('prtg-guard-cpu-percent')?.value) || 85;
            const memoryFreePercent = Number(document.getElementById('prtg-guard-memory-free-percent')?.value) ?? 10;
            const checkSeconds = Number(document.getElementById('prtg-guard-check-seconds')?.value) || 60;
            const pauseMinutes = Number(document.getElementById('prtg-guard-pause-minutes')?.value) || 5;
            const strikes = Number(document.getElementById('prtg-guard-strikes')?.value) || 2;
            const maxPauseMinutes = Number(document.getElementById('prtg-guard-max-pause-minutes')?.value) || 120;

            const payload = {
                prtgResourceGuardEnabled: enabled,
                prtgResourceGuardCpuPercent: cpuPercent,
                prtgResourceGuardMemoryFreePercent: memoryFreePercent,
                prtgResourceGuardCheckSeconds: checkSeconds,
                prtgResourceGuardPauseMinutes: pauseMinutes,
                prtgResourceGuardStrikes: strikes,
                prtgResourceGuardMaxPauseMinutes: maxPauseMinutes,
                prtgResourceGuardSensorObjids: collectLines('prtg-guard-sensor-objids')
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

function bindGuardPreview() {
    const button = document.getElementById('prtg-guard-preview-btn');
    const container = document.getElementById('prtg-guard-preview-result');
    if (!button || !container) return;

    button.addEventListener('click', async () => {
        const restore = withBusy(button, '查詢中');
        container.replaceChildren();

        try {
            const res = await api.get('/api/admin/settings/prtg-resource-guard/preview', { silent: true });

            if (!res.success) {
                const errEl = document.createElement('div');
                errEl.className = 'text-danger small mt-2';
                errEl.textContent = res.errorMessage || '預覽失敗。';
                container.appendChild(errEl);
                return;
            }

            // 清單來源
            const sourceEl = document.createElement('div');
            sourceEl.className = 'small text-muted mb-2';
            sourceEl.textContent = res.source === 'override' ? '來源：覆寫清單' : '來源：自動偵測';
            container.appendChild(sourceEl);

            // 偵測警告逐行顯示
            if (res.warnings && res.warnings.length > 0) {
                const warnContainer = document.createElement('div');
                warnContainer.className = 'mb-2';
                for (const w of res.warnings) {
                    const wEl = document.createElement('div');
                    wEl.className = 'text-warning small';
                    wEl.textContent = `⚠ ${w}`;
                    warnContainer.appendChild(wEl);
                }
                container.appendChild(warnContainer);
            }

            // 表格：Device／Sensor／分類／狀態／目前值／說明
            const tableResp = document.createElement('div');
            tableResp.className = 'table-responsive mt-2';

            const table = document.createElement('table');
            table.className = 'table table-sm table-bordered mb-0 small';

            const thead = document.createElement('thead');
            thead.className = 'table-light';
            const headRow = document.createElement('tr');
            const headers = ['Device', 'Sensor', '分類', '狀態', '目前值', '說明'];
            for (const h of headers) {
                const th = document.createElement('th');
                th.textContent = h;
                headRow.appendChild(th);
            }
            thead.appendChild(headRow);
            table.appendChild(thead);

            const tbody = document.createElement('tbody');
            const sensors = res.sensors || [];
            if (sensors.length === 0) {
                const emptyRow = document.createElement('tr');
                const emptyTd = document.createElement('td');
                emptyTd.colSpan = 6;
                emptyTd.className = 'text-muted text-center py-2';
                emptyTd.textContent = '無受監看感測器。';
                emptyRow.appendChild(emptyTd);
                tbody.appendChild(emptyRow);
            } else {
                for (const s of sensors) {
                    const tr = document.createElement('tr');

                    // Device
                    const tdDevice = document.createElement('td');
                    tdDevice.textContent = s.device || '-';
                    tr.appendChild(tdDevice);

                    // Sensor
                    const tdSensor = document.createElement('td');
                    tdSensor.textContent = s.sensor ? `${s.sensor} (#${s.objid})` : `#${s.objid}`;
                    tr.appendChild(tdSensor);

                    // 分類
                    const tdCat = document.createElement('td');
                    tdCat.textContent = s.category || '-';
                    tr.appendChild(tdCat);

                    // 狀態
                    const tdStatus = document.createElement('td');
                    tdStatus.textContent = s.status || '-';
                    tr.appendChild(tdStatus);

                    // 目前值：有百分比就顯示 xx.x %，沒有就顯示無法判定的原因（text-muted）
                    const tdValue = document.createElement('td');
                    if (s.percentage != null) {
                        tdValue.textContent = `${Number(s.percentage).toFixed(1)} %`;
                    } else if (s.unmeasurableReason) {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = s.unmeasurableReason;
                    } else {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = '-';
                    }
                    tr.appendChild(tdValue);

                    // 說明
                    const tdNote = document.createElement('td');
                    if (s.unmeasurableReason && s.percentage != null) {
                        tdNote.textContent = s.unmeasurableReason;
                    } else if (s.percentage != null) {
                        tdNote.textContent = '正常量測';
                    } else {
                        tdNote.className = 'text-muted';
                        tdNote.textContent = s.unmeasurableReason || '-';
                    }
                    tr.appendChild(tdNote);

                    tbody.appendChild(tr);
                }
            }
            table.appendChild(tbody);
            tableResp.appendChild(table);
            container.appendChild(tableResp);
        } catch (error) {
            const errEl = document.createElement('div');
            errEl.className = 'text-danger small mt-2';
            errEl.textContent = error?.message || '預覽失敗。';
            container.appendChild(errEl);
        } finally {
            restore();
        }
    });
}


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

            const payload = {
                prtgIgnoreSslErrors: ignoreSsl,
                prtgTimeoutSeconds: timeoutSeconds,
                prtgFetchConcurrency: fetchConcurrency,
                prtgBackfillDays: backfillDays,
                prtgRetentionDays: prtgRetentionDays,
                prtgSensorTypeWhitelist: collectLines('prtg-sensor-type-whitelist')
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

function bindGuardForm() {
    const form = document.getElementById('prtg-guard-form');
    const saveButton = document.getElementById('prtg-guard-save');
    if (!form || !saveButton) return;

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const restore = withBusy(saveButton, '儲存中');
        try {
            const enabled = document.getElementById('prtg-guard-enabled')?.checked ?? false;
            const cpuPercent = Number(document.getElementById('prtg-guard-cpu-percent')?.value) || 85;
            const memoryFreePercent = Number(document.getElementById('prtg-guard-memory-free-percent')?.value) ?? 10;
            const checkSeconds = Number(document.getElementById('prtg-guard-check-seconds')?.value) || 60;
            const pauseMinutes = Number(document.getElementById('prtg-guard-pause-minutes')?.value) || 5;
            const strikes = Number(document.getElementById('prtg-guard-strikes')?.value) || 2;
            const maxPauseMinutes = Number(document.getElementById('prtg-guard-max-pause-minutes')?.value) || 120;

            const payload = {
                prtgResourceGuardEnabled: enabled,
                prtgResourceGuardCpuPercent: cpuPercent,
                prtgResourceGuardMemoryFreePercent: memoryFreePercent,
                prtgResourceGuardCheckSeconds: checkSeconds,
                prtgResourceGuardPauseMinutes: pauseMinutes,
                prtgResourceGuardStrikes: strikes,
                prtgResourceGuardMaxPauseMinutes: maxPauseMinutes,
                prtgResourceGuardSensorObjids: collectLines('prtg-guard-sensor-objids')
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

function bindGuardPreview() {
    const button = document.getElementById('prtg-guard-preview-btn');
    const container = document.getElementById('prtg-guard-preview-result');
    if (!button || !container) return;

    button.addEventListener('click', async () => {
        const restore = withBusy(button, '查詢中');
        container.replaceChildren();

        try {
            const res = await api.get('/api/admin/settings/prtg-resource-guard/preview', { silent: true });

            if (!res.success) {
                const errEl = document.createElement('div');
                errEl.className = 'text-danger small mt-2';
                errEl.textContent = res.errorMessage || '預覽失敗。';
                container.appendChild(errEl);
                return;
            }

            // 清單來源
            const sourceEl = document.createElement('div');
            sourceEl.className = 'small text-muted mb-2';
            sourceEl.textContent = res.source === 'override' ? '來源：覆寫清單' : '來源：自動偵測';
            container.appendChild(sourceEl);

            // 偵測警告逐行顯示
            if (res.warnings && res.warnings.length > 0) {
                const warnContainer = document.createElement('div');
                warnContainer.className = 'mb-2';
                for (const w of res.warnings) {
                    const wEl = document.createElement('div');
                    wEl.className = 'text-warning small';
                    wEl.textContent = `⚠ ${w}`;
                    warnContainer.appendChild(wEl);
                }
                container.appendChild(warnContainer);
            }

            // 表格：Device／Sensor／分類／狀態／目前值／說明
            const tableResp = document.createElement('div');
            tableResp.className = 'table-responsive mt-2';

            const table = document.createElement('table');
            table.className = 'table table-sm table-bordered mb-0 small';

            const thead = document.createElement('thead');
            thead.className = 'table-light';
            const headRow = document.createElement('tr');
            const headers = ['Device', 'Sensor', '分類', '狀態', '目前值', '說明'];
            for (const h of headers) {
                const th = document.createElement('th');
                th.textContent = h;
                headRow.appendChild(th);
            }
            thead.appendChild(headRow);
            table.appendChild(thead);

            const tbody = document.createElement('tbody');
            const sensors = res.sensors || [];
            if (sensors.length === 0) {
                const emptyRow = document.createElement('tr');
                const emptyTd = document.createElement('td');
                emptyTd.colSpan = 6;
                emptyTd.className = 'text-muted text-center py-2';
                emptyTd.textContent = '無受監看感測器。';
                emptyRow.appendChild(emptyTd);
                tbody.appendChild(emptyRow);
            } else {
                for (const s of sensors) {
                    const tr = document.createElement('tr');

                    // Device
                    const tdDevice = document.createElement('td');
                    tdDevice.textContent = s.device || '-';
                    tr.appendChild(tdDevice);

                    // Sensor
                    const tdSensor = document.createElement('td');
                    tdSensor.textContent = s.sensor ? `${s.sensor} (#${s.objid})` : `#${s.objid}`;
                    tr.appendChild(tdSensor);

                    // 分類
                    const tdCat = document.createElement('td');
                    tdCat.textContent = s.category || '-';
                    tr.appendChild(tdCat);

                    // 狀態
                    const tdStatus = document.createElement('td');
                    tdStatus.textContent = s.status || '-';
                    tr.appendChild(tdStatus);

                    // 目前值：有百分比就顯示 xx.x %，沒有就顯示無法判定的原因（text-muted）
                    const tdValue = document.createElement('td');
                    if (s.percentage != null) {
                        tdValue.textContent = `${Number(s.percentage).toFixed(1)} %`;
                    } else if (s.unmeasurableReason) {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = s.unmeasurableReason;
                    } else {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = '-';
                    }
                    tr.appendChild(tdValue);

                    // 說明
                    const tdNote = document.createElement('td');
                    if (s.unmeasurableReason && s.percentage != null) {
                        tdNote.textContent = s.unmeasurableReason;
                    } else if (s.percentage != null) {
                        tdNote.textContent = '正常量測';
                    } else {
                        tdNote.className = 'text-muted';
                        tdNote.textContent = s.unmeasurableReason || '-';
                    }
                    tr.appendChild(tdNote);

                    tbody.appendChild(tr);
                }
            }
            table.appendChild(tbody);
            tableResp.appendChild(table);
            container.appendChild(tableResp);
        } catch (error) {
            const errEl = document.createElement('div');
            errEl.className = 'text-danger small mt-2';
            errEl.textContent = error?.message || '預覽失敗。';
            container.appendChild(errEl);
        } finally {
            restore();
        }
    });
}


// ── PRTG 鏡像狀態與衝突處理 ──────────────────────────────────────────────

let conflictPage = 1;
let conflictPageSize = loadPageSize('prtg-conflicts');

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

function init() {
    bindPrtgTest();
    bindPrtgMirror();
    bindPrtgProbe();
    bindAssignForm();
    bindPrtgDataTransfer();
    bindConnectionForm();
    bindParamsForm();
    bindGuardForm();
    bindGuardPreview();
    initCalibration();
    loadSettings();
    refreshPrtgMirror();
    refreshPrtgProbeStatus();
}

init();
