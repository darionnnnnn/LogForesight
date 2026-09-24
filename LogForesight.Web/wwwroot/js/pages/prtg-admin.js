/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、擷取參數、鏡像狀態與環境探測。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { PROGRESS_PHASE_LABEL } from '../core/run-phases.js';
import {
    bindTabs, toast, withBusy, setSpinnerText, confirmAction, guardLoad, renderSpinner,
    renderPagination, loadPageSize, savePageSize, PAGE_SIZE_OPTIONS,
    collectLines, numberOr, renderTable, renderError
} from '../core/ui.js';
import { formatDate, elapsedSinceText, formatDateTime, formatNumber, formatUserName, prtgFreshnessLabel } from '../core/format.js';
import { initCalibration } from './prtg-calibration.js';
import { PRTG_SCOPE_OFF, toScopeSelectValue, prtgScopeInapplicableText } from '../core/prtg-scope-labels.js';
import { parseProbeSensorTypes } from '../core/prtg-probe-types.js';

bindTabs(document.getElementById('prtg-tabs'), { hash: true, onChange: name => { if (name === 'probe') queueMicrotask(loadDiskReadiness); } });

function missingHourDate(windowStart, dayIndex, hour) {
    const [year, month, day] = String(windowStart).slice(0, 10).split('-').map(Number);
    const date = new Date(year, month - 1, day);
    date.setDate(date.getDate() + dayIndex);
    date.setHours(hour, 0, 0, 0);
    return date;
}

function localDateInputValue(date) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
}

function effectivenessRangeError(from, through) {
    if (!from || !through) return '請選擇起始與結束日期。';
    const days = Math.round((Date.parse(`${through}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86400000) + 1;
    if (days < 1) return '起始日期不得晚於結束日期。';
    if (days > 366) return '日期範圍最多 366 天（含起訖日）。';
    return null;
}

function renderEffectiveness(summary) {
    const metrics = document.getElementById('prtg-effectiveness-metrics');
    const status = document.getElementById('prtg-effectiveness-status');
    const semantics = document.getElementById('prtg-effectiveness-semantics');
    const limitations = document.getElementById('prtg-effectiveness-limitations');
    if (!metrics || !status || !semantics || !limitations) return;
    metrics.replaceChildren();
    const definitions = [
        ['低 coverage sampled 小時', summary.lowCoverageSampledHours, '已落盤 sampled 且 coverage 低於門檻的小時列數；不包含完全缺值。此指標不涵蓋所有磁碟不就緒原因。'],
        ['問題訊號', summary.prtgFindings, 'PRTG TopIssue 列數；依主機日／特徵計數。'],
        ['建立案件', summary.casesCreated, '所選期間建立、來源為 PRTG 的案件列數。'],
        ['建立交辦單', summary.workOrdersCreated, '所選期間建立、來源為 PRTG 的交辦單列數。'],
        ['曾有回覆的交辦單', summary.workOrdersReplied, '上述建立交辦單中 LastReplyAt 有值的列數，不限回覆日期。'],
        ['抑制的問題訊號', summary.suppressedFindings, '日期分析內容中標記為抑制的 PRTG 特徵數。'],
        ['有 PRTG 佐證的主機日', summary.corroboratedHostDays, '含 PRTG 佐證參照或抑制佐證文字的主機日數。']
    ];
    for (const [label, value, description] of definitions) {
        const column = document.createElement('div');
        column.className = 'col-12 col-sm-6 col-lg-4';
        const card = document.createElement('div');
        card.className = 'border rounded p-3 h-100';
        const heading = document.createElement('div');
        heading.className = 'small text-muted';
        heading.textContent = label;
        const number = document.createElement('div');
        number.className = 'fs-4 fw-semibold';
        number.textContent = value == null ? '—' : formatNumber(value);
        const note = document.createElement('div');
        note.className = 'small text-muted mt-1';
        note.textContent = value == null ? `${description}目前沒有可計數的資料；目前無法計算（非 0）。` : description;
        card.append(heading, number, note);
        column.appendChild(card);
        metrics.appendChild(column);
    }
    metrics.classList.remove('d-none');
    status.textContent = `${summary.from?.slice(0, 10) ?? ''} 至 ${summary.through?.slice(0, 10) ?? ''}，共 ${formatNumber(summary.windowDays)} 天。指標使用不同計算對象，請依各項說明解讀，彼此不共用分母。`;
    semantics.textContent = summary.metricSemantics || '指標依各自的資料列與日期定義計算，彼此沒有共同分母。';
    limitations.textContent = summary.limitations || '';
}

async function loadPrtgEffectiveness() {
    const fromInput = document.getElementById('prtg-effectiveness-from');
    const throughInput = document.getElementById('prtg-effectiveness-through');
    const button = document.getElementById('prtg-effectiveness-refresh');
    const status = document.getElementById('prtg-effectiveness-status');
    const error = document.getElementById('prtg-effectiveness-error');
    const metrics = document.getElementById('prtg-effectiveness-metrics');
    if (!fromInput || !throughInput || !button || !status || !error || !metrics) return;
    error.replaceChildren();
    const rangeError = effectivenessRangeError(fromInput.value, throughInput.value);
    if (rangeError) {
        status.textContent = rangeError;
        status.className = 'small mb-3 text-danger';
        return;
    }
    button.disabled = true;
    button.textContent = '載入中…';
    status.className = 'small mb-3';
    status.textContent = '正在讀取使用效果…';
    error.replaceChildren();
    try {
        const query = new URLSearchParams({ from: fromInput.value, through: throughInput.value });
        const summary = await api.get(`/api/prtg/effectiveness?${query.toString()}`, { silent: true });
        renderEffectiveness(summary);
        const hasCount = [summary.lowCoverageSampledHours, summary.prtgFindings, summary.casesCreated, summary.workOrdersCreated,
            summary.workOrdersReplied, summary.suppressedFindings, summary.corroboratedHostDays]
            .some(value => value != null && value > 0);
        if (!hasCount) {
            status.textContent += ' 此期間沒有可計數的資料；若預期應有資料，請先查看環境探測與資料準備度。';
        }
        status.className = 'small mb-3 text-muted';
    } catch (exception) {
        metrics.classList.add('d-none');
        status.textContent = '使用效果載入失敗。';
        status.className = 'small mb-3 text-danger';
        const alert = document.createElement('div');
        alert.className = 'alert alert-danger py-2';
        alert.setAttribute('role', 'alert');
        const message = document.createElement('span');
        message.textContent = exception?.message || '目前無法讀取資料。';
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'btn btn-sm btn-outline-danger ms-2';
        retry.textContent = '重試';
        retry.addEventListener('click', loadPrtgEffectiveness);
        alert.append(message, retry);
        error.appendChild(alert);
    } finally {
        button.disabled = false;
        button.textContent = '查詢';
    }
}

function bindPrtgEffectiveness() {
    const fromInput = document.getElementById('prtg-effectiveness-from');
    const throughInput = document.getElementById('prtg-effectiveness-through');
    const refresh = document.getElementById('prtg-effectiveness-refresh');
    if (!fromInput || !throughInput || !refresh) return;
    const today = new Date();
    const firstDay = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 29);
    fromInput.value = localDateInputValue(firstDay);
    throughInput.value = localDateInputValue(today);
    refresh.addEventListener('click', loadPrtgEffectiveness);
}

/** 目前已儲存的 PRTG 擷取開關。鏡像頁籤的「同步結構與對應」關閉時要擋住（後端也會擋，這是提前告知）。 */
let prtgEnabled = false;
/** 結構同步是否執行中：開關的閘與執行中的灰掉是同一顆按鈕的兩個理由，任一成立就不能按。 */
let structureSyncRunning = false;
let selectedBackfillTimer = null;
let selectedBackfillPreview = null;
const selectedBackfillHostIds = new Set();
let pendingBackfillHostId = null;

function selectedBackfillRequest() {
    return {
        hostIds: [...selectedBackfillHostIds].map(Number),
        fromDate: document.getElementById('prtg-selected-backfill-from')?.value,
        toDate: document.getElementById('prtg-selected-backfill-to')?.value
    };
}

function selectedBackfillFormError(request) {
    if (request.hostIds.length < 1 || request.hostIds.length > 5) return '請選擇 1 至 5 台主機。';
    if (!request.fromDate || !request.toDate) return '請選擇起始與結束日期。';
    const days = Math.round((Date.parse(`${request.toDate}T00:00:00Z`) - Date.parse(`${request.fromDate}T00:00:00Z`)) / 86400000) + 1;
    if (days < 1) return '起始日期不得晚於結束日期。';
    if (days > 7) return '日期範圍最多 7 天（含起訖日）。';
    return null;
}

function renderSelectedBackfillHosts(hosts) {
    const root = document.getElementById('prtg-selected-backfill-hosts');
    if (!root) return;
    root.replaceChildren();
    const query = document.getElementById('prtg-selected-backfill-search')?.value.trim().toLocaleLowerCase() ?? '';
    const filtered = (hosts || []).filter(host => `${host.hostName ?? ''} ${host.ipAddress ?? ''}`.toLocaleLowerCase().includes(query));
    if (!filtered.length) {
        const empty = document.createElement('span');
        empty.className = 'small text-muted';
        empty.textContent = hosts?.length ? '沒有符合的主機。' : '沒有可選的已儲存主機。';
        root.appendChild(empty);
        return;
    }
    for (const host of filtered) {
        const id = String(host.hostId);
        const label = document.createElement('label');
        label.className = 'form-check d-flex gap-2 align-items-start py-1';
        const checkbox = document.createElement('input');
        checkbox.className = 'form-check-input mt-1';
        checkbox.type = 'checkbox';
        checkbox.value = id;
        checkbox.checked = selectedBackfillHostIds.has(id);
        checkbox.disabled = !checkbox.checked && selectedBackfillHostIds.size >= 5;
        checkbox.addEventListener('change', () => {
            if (checkbox.checked) selectedBackfillHostIds.add(id);
            else selectedBackfillHostIds.delete(id);
            selectedBackfillPreview = null;
            document.getElementById('prtg-selected-backfill-start').disabled = true;
            document.getElementById('prtg-selected-backfill-selection').textContent = `已選 ${selectedBackfillHostIds.size} / 5 台`;
            renderSelectedBackfillHosts(cachedHosts || []);
        });
        const name = document.createElement('span');
        name.textContent = `${host.hostName || '未命名主機'}${host.ipAddress ? ` (${host.ipAddress})` : ''}`;
        label.append(checkbox, name);
        root.appendChild(label);
    }
    document.getElementById('prtg-selected-backfill-selection').textContent = `已選 ${selectedBackfillHostIds.size} / 5 台`;
}

function renderSelectedBackfillPreview(preview) {
    const root = document.getElementById('prtg-selected-backfill-preview-result');
    root.replaceChildren();
    const box = document.createElement('div');
    box.className = preview.hasTargets ? 'alert alert-info' : 'alert alert-warning';
    const estimate = document.createElement('p');
    estimate.className = 'mb-1';
    estimate.textContent = `主機 ${preview.hostCount} 台；日期 ${preview.dayCount} 天（${preview.fromDate?.slice(0, 10)}～${preview.toDate?.slice(0, 10)}）；有目標的主機日 ${preview.daysWithTargets}；預估 PRTG 請求 ${formatNumber(preview.estimatedRequests)} 次。`;
    box.appendChild(estimate);
    const replacementEstimate = document.createElement('p');
    replacementEstimate.className = 'mb-1';
    replacementEstimate.textContent = `可能被取代的 sampled 小時列（上限估計，非保證）：${formatNumber(preview.estimatedSampledRowsToReplace ?? 0)} 列。`;
    box.appendChild(replacementEstimate);
    const note = document.createElement('p');
    note.className = 'mb-0 small';
    note.textContent = preview.hasTargets ? (preview.message || '檢查範圍後，按「確認並開始補值」才會送出請求。') : (preview.message || '此範圍沒有可補值目標，不會啟動回填。');
    box.appendChild(note);
    root.appendChild(box);
    document.getElementById('prtg-selected-backfill-start').disabled = !preview.hasTargets;
}

function renderSelectedBackfillStatus(status) {
    const text = document.getElementById('prtg-selected-backfill-status-text');
    const progress = document.getElementById('prtg-selected-backfill-progress');
    const cancel = document.getElementById('prtg-selected-backfill-cancel');
    if (!text || !progress || !cancel) return;
    if (status.isRunning) {
        text.textContent = status.latestMessage || 'PRTG 歷史回填執行中…';
        progress.textContent = status.readingStateChanges
            ? `讀取狀態變更 ${status.stateChangesRead} / ${status.stateChangesTotal}`
            : `日期 ${status.daysDone} / ${status.daysTotal}${status.currentDate ? `；目前 ${String(status.currentDate).slice(0, 10)}` : ''}；感測器 ${status.sensorsDone} / ${status.sensorsTotal}`;
        cancel.classList.toggle('d-none', status.runKind !== 'selected' || !status.runId);
        if (!selectedBackfillTimer) selectedBackfillTimer = setInterval(refreshSelectedBackfillStatus, 2000);
        return;
    }
    if (selectedBackfillTimer) { clearInterval(selectedBackfillTimer); selectedBackfillTimer = null; }
    cancel.classList.add('d-none');
    text.textContent = status.completedAt
        ? `${status.cancelled ? '已停止' : status.success ? '回填完成' : '回填未完整成功'}：${status.latestMessage || ''}`
        : '目前沒有執行中的歷史回填。';
    progress.textContent = status.completedAt
        ? `完成日期 ${status.daysDone} / ${status.daysTotal}；感測器 ${status.sensorsDone} / ${status.sensorsTotal}`
        : '';
}

async function refreshSelectedBackfillStatus() {
    try {
        const status = await api.get('/api/admin/settings/prtg-backfill/status', { silent: true });
        renderSelectedBackfillStatus(status);
    } catch (error) {
        const root = document.getElementById('prtg-selected-backfill-status-text');
        if (root) root.textContent = `無法讀取回填狀態：${error?.message || '請重新整理狀態。'}`;
    }
}

function bindSelectedBackfill() {
    const hostSearch = document.getElementById('prtg-selected-backfill-search');
    if (!hostSearch) return;
    hostSearch.addEventListener('input', () => renderSelectedBackfillHosts(cachedHosts || []));
    hostSearch.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown') document.querySelector('#prtg-selected-backfill-hosts input:not(:disabled)')?.focus();
    });
    for (const id of ['prtg-selected-backfill-from', 'prtg-selected-backfill-to']) {
        document.getElementById(id).addEventListener('change', () => {
            selectedBackfillPreview = null;
            document.getElementById('prtg-selected-backfill-start').disabled = true;
        });
    }
    api.get('/api/admin/hosts/all', { silent: true }).then(hosts => {
        cachedHosts = hosts || [];
        if (pendingBackfillHostId != null) activateSelectedBackfillHost(pendingBackfillHostId);
        renderSelectedBackfillHosts(cachedHosts);
    }).catch(error => {
        const root = document.getElementById('prtg-selected-backfill-hosts');
        root.replaceChildren();
        const message = document.createElement('span');
        message.className = 'small text-danger';
        message.textContent = `無法載入主機清單：${error?.message || '請重新整理頁面重試。'}`;
        root.appendChild(message);
    });
    document.getElementById('prtg-selected-backfill-preview').addEventListener('click', async event => {
        const request = selectedBackfillRequest();
        const error = selectedBackfillFormError(request);
        if (error) { toast(error, 'warning'); return; }
        const restore = withBusy(event.currentTarget, '預覽中');
        try {
            selectedBackfillPreview = await api.post('/api/admin/settings/prtg-backfill/selected/preview', request, { silent: true });
            renderSelectedBackfillPreview(selectedBackfillPreview);
        } catch (e) { renderError(document.getElementById('prtg-selected-backfill-preview-result'), { message: e?.message || '預覽失敗。', onRetry: () => document.getElementById('prtg-selected-backfill-preview').click() }); }
        finally { restore(); }
    });
    document.getElementById('prtg-selected-backfill-start').addEventListener('click', async () => {
        const request = selectedBackfillRequest();
        if (!selectedBackfillPreview || selectedBackfillFormError(request)) { toast('請先完成有效的預覽。', 'warning'); return; }
        const confirmed = await confirmAction({ title: '確認指定範圍的歷史補值', message: `即將對 ${selectedBackfillPreview.hostCount} 台主機、${selectedBackfillPreview.dayCount} 天執行約 ${formatNumber(selectedBackfillPreview.estimatedRequests)} 次 PRTG 歷史數值請求。這只寫入數值，不會補跑 finding 或派送。`, confirmText: '開始補值', confirmVariant: 'primary' });
        if (!confirmed) return;
        const button = document.getElementById('prtg-selected-backfill-start');
        const restore = withBusy(button, '啟動中');
        try {
            await api.post('/api/admin/settings/prtg-backfill/selected/start', request, { silent: true });
            selectedBackfillPreview = null;
            toast('已開始指定範圍的歷史數值補值。', 'success');
            await refreshSelectedBackfillStatus();
        } catch (e) { toast(e?.message || '無法啟動指定範圍回填，請確認條件後重試。', 'danger'); }
        finally { restore(); }
    });
    document.getElementById('prtg-selected-backfill-cancel').addEventListener('click', async event => {
        const status = await api.get('/api/admin/settings/prtg-backfill/status', { silent: true });
        if (!status.isRunning || status.runKind !== 'selected' || !status.runId) return;
        const restore = withBusy(event.currentTarget, '停止中');
        try { await api.post('/api/admin/settings/prtg-backfill/cancel', { runId: status.runId }); toast('已送出停止要求。', 'success'); }
        catch (e) { toast(e?.message || '停止要求失敗，請重新整理狀態後重試。', 'danger'); }
        finally { restore(); await refreshSelectedBackfillStatus(); }
    });
    document.getElementById('prtg-selected-backfill-status-retry').addEventListener('click', refreshSelectedBackfillStatus);
    refreshSelectedBackfillStatus();
}

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
    document.getElementById('prtg-sensor-type-category-overrides').value =
        (settings.prtgSensorTypeCategoryOverrides ?? []).join('\n');


    prtgEnabled = Boolean(settings.prtgEnabled);
    const scopeSelect = document.getElementById('prtg-value-fetch-scope');
    if (scopeSelect) {
        // 「關閉」與三個範圍是同一個下拉：未啟用一律顯示關閉，啟用時顯示已存的範圍
        scopeSelect.value = toScopeSelectValue(prtgEnabled, settings.prtgValueFetchScope);
        syncScopeFields();
    }
    const strategySelect = document.getElementById('prtg-fetch-strategy');
    if (strategySelect) {
        strategySelect.value = settings.prtgFetchStrategy === 'aggressive' ? 'aggressive' : 'conservative';
        syncStrategyHint();
    }
    syncStructureSyncGate();
    const extraHosts = document.getElementById('prtg-value-fetch-extra-hosts');
    if (extraHosts) extraHosts.value = (settings.prtgValueFetchExtraHosts ?? []).join('\n');
    document.getElementById('prtg-scope-estimate-result')?.replaceChildren();
    document.getElementById('prtg-snapshot-estimate-result')?.replaceChildren();

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

/**
 * 載入指示掛在「最後更新」那行（回饋第 45 輪 B7）：與 settings.js 同一個理由——
 * 這區塊是表單，骨架列會把表單節點整片換掉，只能用行內 spinner。
 */
async function loadSettings() {
    const statusEl = document.getElementById('prtg-config-updated');
    if (statusEl) renderSpinner(statusEl, '載入設定中…');
    await guardLoad(statusEl, loadPrtgSettings);
}

async function loadPrtgSettings() {
    const settings = await api.get('/api/admin/settings');
    historyRetentionDays = settings.retentionDays;
    renderPrtgFields(settings);
}

/**
 * 數值取數對象切換：只有「觸發主機＋指定清單」需要主機名稱輸入框；
 * 選「關閉」時連「估算規模」都沒有意義（不會取數），一併藏起來。
 * 用 classList 切換而非 style.display（同本頁認證方式切換的既有作法）。
 */
function syncScopeFields() {
    const scope = document.getElementById('prtg-value-fetch-scope')?.value ?? PRTG_SCOPE_OFF;
    const off = scope === PRTG_SCOPE_OFF;
    document.getElementById('prtg-value-fetch-extra-hosts-group')
        ?.classList.toggle('d-none', off || scope !== 'triggered-plus-list');
    document.getElementById('prtg-scope-estimate-btn')?.classList.toggle('d-none', off);
    if (off) {
        document.getElementById('prtg-scope-estimate-result')?.replaceChildren();
        document.getElementById('prtg-snapshot-estimate-result')?.replaceChildren();
    }
}

/**
 * 取數策略切換：激進策略顯示警告區塊（負載較高提示）。
 */
function syncStrategyHint() {
    const select = document.getElementById('prtg-fetch-strategy');
    const isAggressive = (select ? select.value : '') === 'aggressive';
    document.getElementById('prtg-strategy-aggressive-hint')
        ?.classList.toggle('d-none', !isAggressive);

    const scopeSelect = document.getElementById('prtg-value-fetch-scope');
    const inapplicableHint = document.getElementById('prtg-scope-inapplicable-hint');
    if (scopeSelect) {
        scopeSelect.disabled = !isAggressive;
    }
    if (inapplicableHint) {
        inapplicableHint.textContent = prtgScopeInapplicableText(false);
        inapplicableHint.classList.toggle('d-none', isAggressive);
    }
}

/**
 * 鏡像頁籤「同步結構與對應」的閘：擷取未啟用時同步一定被後端拒絕（PrtgStructureSyncService），
 * 讓按鈕直接灰掉並說去哪開，比按下去看紅字有用。以「已儲存的值」為準——
 * 下拉改了還沒存不算啟用，否則會讓人以為存過了。
 */
function syncStructureSyncGate() {
    const btn = document.getElementById('prtg-structure-sync-btn');
    if (btn) btn.disabled = !prtgEnabled || structureSyncRunning;
    document.getElementById('prtg-structure-sync-disabled-hint')
        ?.classList.toggle('d-none', prtgEnabled);
}

function bindScopeControls() {
    const select = document.getElementById('prtg-value-fetch-scope');
    if (select) select.addEventListener('change', syncScopeFields);

    const strategySelect = document.getElementById('prtg-fetch-strategy');
    if (strategySelect) strategySelect.addEventListener('change', () => {
        syncStrategyHint();
        const aggressive = strategySelect.value === 'aggressive';
        document.getElementById('prtg-fetch-concurrency').value = aggressive ? '4' : '2';
        document.getElementById('prtg-timeout-seconds').value = aggressive ? '120' : '60';
        document.getElementById('prtg-strategy-suggested-hint')?.classList.remove('d-none');
    });

    const button = document.getElementById('prtg-scope-estimate-btn');
    const result = document.getElementById('prtg-scope-estimate-result');
    if (!button || !result) return;

    button.addEventListener('click', async () => {
        const restore = withBusy(button, '估算中');
        result.replaceChildren();
        result.className = 'small';
        const snapshotResult = document.getElementById('prtg-snapshot-estimate-result');
        snapshotResult?.replaceChildren();

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

            if (snapshotResult) {
                const snapBase = `快照（不受數值取數對象影響）：${formatNumber(res.snapshotTargets)} 顆感測器，每天約 ${formatNumber(res.snapshotRowsPerDay)} 列，保留 ${res.snapshotRetentionDays} 天約 ${formatNumber(res.snapshotRowsAtRetention)} 列`;
                if (res.snapshotWarning) {
                    snapshotResult.className = 'text-warning small d-block';
                    snapshotResult.textContent = `⚠ ${snapBase}——${res.snapshotWarning}`;
                } else {
                    snapshotResult.className = 'text-muted small d-block';
                    snapshotResult.textContent = snapBase;
                }
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
            const fetchStrategy = document.getElementById('prtg-fetch-strategy')?.value || 'conservative';

            const payload = {
                prtgEnabled: enabled,
                prtgFetchStrategy: fetchStrategy,
                prtgIgnoreSslErrors: ignoreSsl,
                prtgTimeoutSeconds: timeoutSeconds,
                prtgFetchConcurrency: fetchConcurrency,
                prtgBackfillDays: backfillDays,
                prtgRetentionDays: prtgRetentionDays,
                prtgSensorTypeWhitelist: collectLines('prtg-sensor-type-whitelist'),
                prtgSensorTypeCategoryOverrides: collectLines('prtg-sensor-type-category-overrides'),
                prtgValueFetchExtraHosts: collectLines('prtg-value-fetch-extra-hosts'),
                // 關閉時整個鍵不送：範圍留著原值，下次重新啟用不必再選一次
                ...(enabled ? { prtgValueFetchScope: scopeValue } : {})
            };

            await api.put('/api/admin/settings/prtg', payload);
            toast('已儲存', 'success');
            await loadSettings();
            if (enabled && await confirmAction({
                title: '擷取參數已儲存',
                message: '要現在同步 PRTG 結構與主機對應嗎？這會讓新主機較快進入監看範圍。',
                confirmText: '現在同步',
                confirmVariant: 'primary'
            })) {
                document.getElementById('prtg-structure-sync-btn')?.click();
            }
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

    const snapEl = document.getElementById('prtg-mirror-snapshot');
    let snapText;
    if (!data.snapshotLastAt) {
        snapText = '數值快照：尚未執行';
        snapEl?.classList.remove('text-warning');
    } else {
        snapText = `數值快照：最近 ${formatDateTime(data.snapshotLastAt)}，${formatNumber(data.snapshotSensors)} 顆，間隔 ${data.snapshotIntervalMinutes} 分鐘`;
        if (data.snapshotBackingOff) {
            snapText += `（PRTG 連續失敗 ${data.snapshotConsecutiveFailures} 次，已自動拉長間隔）`;
            snapEl?.classList.add('text-warning');
        } else {
            snapEl?.classList.remove('text-warning');
        }
    }
    if (data.snapshotSkipReason) {
        snapText += `（目前暫停：${data.snapshotSkipReason}）`;
    }
    setTxt('prtg-mirror-snapshot', snapText);

    setTxt('prtg-mirror-map-date', `對應基準日：${data.mapDate ? formatDate(data.mapDate) : '無'}`);
    setTxt('prtg-mirror-map-ok', formatNumber(data.mapOk));
    setTxt('prtg-mirror-map-conflict', formatNumber(data.mapConflict));
    setTxt('prtg-mirror-map-unmatched', formatNumber(data.mapUnmatched));
    setTxt('prtg-mirror-ip-exclude-count', formatNumber(data.ipExcludeCount || 0));
    setTxt('prtg-mirror-whitelist-count', formatNumber(data.whitelistSensorCount));
    setTxt('prtg-mirror-whitelist-mapped', formatNumber(data.onMappedDeviceCount));

    renderPrtgFreshness(data.freshness ?? []);
}

/** 鏡像頁「擷取紀錄」：各類資料最後一次成功擷取；連續取得 0 筆的列加警示 */
function renderPrtgFreshness(items) {
    const el = document.getElementById('prtg-mirror-freshness');
    if (!el) return;
    renderTable(el, {
        columns: [
            { title: '類別', render: f => prtgFreshnessLabel(f.category) },
            { title: '最後成功', render: f => formatDateTime(f.lastSuccessAt) },
            {
                title: '取得筆數',
                render: f => {
                    if (!f.suspicious) return formatNumber(f.lastCount);
                    const warn = document.createElement('span');
                    warn.className = 'text-warning fw-semibold';
                    warn.textContent = `${formatNumber(f.lastCount)}（連續 ${f.zeroStreak} 次取得 0 筆）`;
                    return warn;
                }
            }
        ],
        rows: items,
        empty: { title: '尚無擷取紀錄', hint: '結構同步、快照或取數成功完成後會記錄在這裡。' }
    });
}

const snapshotStateLabels = {
    'no-targets': '無目標',
    'coverage-evidence': '有覆蓋率達標列；快照健康度未確認',
    'ok-covered': '由歷史 ok 數值覆蓋；不代表快照成功',
    'reported-write-count-met-target': '回報寫入量達目標，逐顆覆蓋未證明',
    insufficient: '數值不足',
    unknown: '資料未知'
};
const snapshotNextSteps = {
    'no-targets': '確認 PRTG 啟用狀態與監看目標。',
    'coverage-evidence': '另看成功／嘗試次數判讀快照執行；此彙總未逐顆核對目標。',
    'ok-covered': '數值由歷史資料補足；請查看快照成功／嘗試與原因。',
    'reported-write-count-met-target': '回報寫入量達目標但未逐顆確認；重試或覆寫可能造成重複計數。',
    insufficient: '查看跳過或寫入失敗原因，並確認涵蓋率。',
    unknown: '目前無法判定；確認快照是否啟用及診斷資料是否已累積。'
};

function renderSnapshotDiagnostics(hours) {
    const host = document.getElementById('prtg-snapshot-diagnostics');
    if (!host) return;
    renderTable(host, {
        columns: [
            { title: '完整小時', render: row => formatDateTime(row.hour) },
            { title: '狀態', render: row => snapshotStateLabels[row.state] ?? '資料未知' },
            { title: '品質達標列／目標', render: row => `${formatNumber(row.availableValues)} / ${formatNumber(row.targets)}` },
            { title: '成功／嘗試', render: row => `${formatNumber(row.successes)} / ${formatNumber(row.attempts)}` },
            { title: '跳過／寫入失敗', render: row => `${formatNumber(row.skips)} / ${formatNumber(row.writeFailures)}` },
            { title: '原因', render: row => Object.entries(row.reasons ?? {}).map(([reason, count]) => `${reason} (${formatNumber(count)})`).join('、') || '—' },
            { title: '建議檢查', render: row => snapshotNextSteps[row.state] ?? snapshotNextSteps.unknown }
        ],
        rows: hours,
        empty: { title: '尚無完整小時資料', hint: '資料累積後會顯示最近 24 個完整小時。' }
    });
    const tableRegion = host.querySelector('.lf-table-wrap');
    if (tableRegion) {
        tableRegion.tabIndex = 0;
        tableRegion.setAttribute('role', 'region');
        tableRegion.setAttribute('aria-label', '快照診斷表格，可水平捲動');
    }
}

async function loadSnapshotDiagnostics() {
    const host = document.getElementById('prtg-snapshot-diagnostics');
    if (host) renderSpinner(host, '讀取快照診斷…');
    try {
        const response = await api.get('/api/prtg-snapshot-diagnostics', { silent: true });
        renderSnapshotDiagnostics(response?.hours ?? []);
    } catch {
        if (host) renderError(host, { message: '快照診斷讀取失敗。請重新載入；不會呼叫 PRTG。', onRetry: loadSnapshotDiagnostics });
    }
}

// ── PRTG 鏡像狀態與衝突處理 ──────────────────────────────────────────────

let conflictPage = 1;
let conflictPageSize = loadPageSize('prtg-conflicts');
const selectedConflictDeviceObjids = new Set();
let currentConflictItems = [];

function clearConflictSelection(silent = false) {
    const hadSelection = selectedConflictDeviceObjids.size > 0;
    selectedConflictDeviceObjids.clear();
    updateConflictBatchBar();
    const selectAll = document.getElementById('prtg-conflict-select-all');
    if (selectAll) {
        selectAll.checked = false;
        selectAll.indeterminate = false;
    }
    if (!silent && hadSelection) {
        toast('換頁或重新整理已清空選取項目，避免隱藏選取。', 'info');
    }
}

function updateConflictBatchBar() {
    const count = selectedConflictDeviceObjids.size;
    const countEl = document.getElementById('prtg-conflict-selected-count');
    if (countEl) {
        countEl.textContent = `已選 ${count} 台`;
    }
    const submitBtn = document.getElementById('prtg-conflict-batch-submit');
    if (submitBtn) {
        submitBtn.disabled = (count === 0);
    }

    const selectAll = document.getElementById('prtg-conflict-select-all');
    if (selectAll) {
        if (currentConflictItems.length === 0) {
            selectAll.checked = false;
            selectAll.indeterminate = false;
        } else {
            const pageObjids = currentConflictItems.map(i => i.deviceObjid);
            const selectedOnPage = pageObjids.filter(id => selectedConflictDeviceObjids.has(id));
            if (selectedOnPage.length === pageObjids.length) {
                selectAll.checked = true;
                selectAll.indeterminate = false;
            } else if (selectedOnPage.length > 0) {
                selectAll.checked = false;
                selectAll.indeterminate = true;
            } else {
                selectAll.checked = false;
                selectAll.indeterminate = false;
            }
        }
    }
}

function renderConflictsLoading() {
    const tbody = document.getElementById('prtg-mirror-conflicts-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 6;
    td.className = 'text-muted text-center py-2';
    td.textContent = '載入中…';
    tr.appendChild(td);
    tbody.appendChild(tr);
}

function renderConflictsError(errorMessage) {
    const tbody = document.getElementById('prtg-mirror-conflicts-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 6;
    td.className = 'text-danger text-center py-2';
    const textSpan = document.createElement('span');
    textSpan.textContent = `載入衝突清單失敗：${errorMessage || '網路或伺服器錯誤'} `;
    const retryBtn = document.createElement('button');
    retryBtn.type = 'button';
    retryBtn.className = 'btn btn-sm btn-outline-danger py-0 ms-2';
    retryBtn.textContent = '重試';
    retryBtn.addEventListener('click', () => refreshConflicts(conflictPage));
    td.append(textSpan, retryBtn);
    tr.appendChild(td);
    tbody.appendChild(tr);
    document.getElementById('prtg-conflicts-pagination')?.replaceChildren();
}

async function refreshConflicts(page = conflictPage, options = {}) {
    const pageChanged = (page !== conflictPage);
    conflictPage = page;
    if (!options.preserveSelection) {
        clearConflictSelection(!pageChanged && options.silentClear === true);
    }
    renderConflictsLoading();
    try {
        const res = await api.get(`/api/admin/settings/prtg-host-map?status=conflict&page=${conflictPage}&pageSize=${conflictPageSize}`, { silent: true });
        const total = (res && res.total) ? res.total : 0;
        const totalPages = Math.ceil(total / conflictPageSize);

        if (conflictPage > totalPages && totalPages > 0) {
            return refreshConflicts(totalPages, options);
        }

        currentConflictItems = (res && res.items) ? res.items : [];
        if (options.preserveSelection) {
            const visibleIds = new Set(currentConflictItems.map(i => i.deviceObjid));
            const hiddenCount = [...selectedConflictDeviceObjids].filter(id => !visibleIds.has(id)).length;
            for (const id of [...selectedConflictDeviceObjids]) {
                if (!visibleIds.has(id)) selectedConflictDeviceObjids.delete(id);
            }
            if (hiddenCount > 0) toast(`${hiddenCount} 台裝置已不在目前頁，已取消選取。`, 'info');
        }
        renderConflicts(currentConflictItems);
        renderConflictPagination(totalPages);
        updateConflictBatchBar();
    } catch (error) {
        renderConflictsError(error && error.message ? error.message : '載入衝突清單失敗');
    }
}

function renderConflicts(items) {
    const tbody = document.getElementById('prtg-mirror-conflicts-body');
    if (!tbody) return;
    tbody.replaceChildren();

    if (!items || items.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 6;
        td.className = 'text-muted text-center py-2';
        td.textContent = '無衝突項目';
        tr.appendChild(td);
        tbody.appendChild(tr);
        return;
    }

    for (const item of items) {
        const tr = document.createElement('tr');

        const tdCheck = document.createElement('td');
        tdCheck.className = 'text-center';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'form-check-input prtg-conflict-row-check';
        checkbox.value = String(item.deviceObjid);
        checkbox.dataset.objid = String(item.deviceObjid);
        checkbox.setAttribute('aria-label', `選取裝置 ${item.deviceObjid}`);
        checkbox.checked = selectedConflictDeviceObjids.has(item.deviceObjid);
        checkbox.addEventListener('change', () => {
            if (checkbox.checked) {
                selectedConflictDeviceObjids.add(item.deviceObjid);
            } else {
                selectedConflictDeviceObjids.delete(item.deviceObjid);
            }
            updateConflictBatchBar();
        });
        tdCheck.appendChild(checkbox);

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
                        refreshUnmatched(unmatchedPage),
                        refreshIpExcludes()
                    ]);
                } catch (error) {
                    toast(error && error.message ? error.message : '排除失敗', 'danger');
                }
            });
        }
        tdAction.appendChild(excludeBtn);

        tr.append(tdCheck, tdDevice, tdIp, tdKind, tdNote, tdAction);
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

// ── PRTG 未對應清單 ──────────────────────────────────────────────────

let unmatchedPage = 1;
let unmatchedPageSize = loadPageSize('prtg-unmatched');

function renderUnmatchedLoading() {
    const tbody = document.getElementById('prtg-unmatched-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'text-muted text-center py-2';
    td.textContent = '載入中…';
    tr.appendChild(td);
    tbody.appendChild(tr);
}

function renderUnmatchedError(errorMessage) {
    const tbody = document.getElementById('prtg-unmatched-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'text-danger text-center py-2';
    const textSpan = document.createElement('span');
    textSpan.textContent = `載入未對應清單失敗：${errorMessage || '網路或伺服器錯誤'} `;
    const retryBtn = document.createElement('button');
    retryBtn.type = 'button';
    retryBtn.className = 'btn btn-sm btn-outline-danger py-0 ms-2';
    retryBtn.textContent = '重試';
    retryBtn.addEventListener('click', () => refreshUnmatched(unmatchedPage));
    td.append(textSpan, retryBtn);
    tr.appendChild(td);
    tbody.appendChild(tr);
    document.getElementById('prtg-unmatched-pagination')?.replaceChildren();
}

async function refreshUnmatched(page = unmatchedPage) {
    unmatchedPage = page;
    renderUnmatchedLoading();
    try {
        const res = await api.get(`/api/admin/settings/prtg-host-map?status=unmatched&page=${unmatchedPage}&pageSize=${unmatchedPageSize}`, { silent: true });
        const total = (res && res.total) ? res.total : 0;
        const totalPages = Math.ceil(total / unmatchedPageSize);

        if (unmatchedPage > totalPages && totalPages > 0) {
            return refreshUnmatched(totalPages);
        }

        renderUnmatched((res && res.items) ? res.items : []);
        renderUnmatchedPagination(totalPages);
    } catch (error) {
        renderUnmatchedError(error && error.message ? error.message : '載入未對應清單失敗');
    }
}

function renderUnmatched(items) {
    const tbody = document.getElementById('prtg-unmatched-body');
    if (!tbody) return;
    tbody.replaceChildren();

    if (!items || items.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 5;
        td.className = 'text-muted text-center py-2';
        td.textContent = '無未對應項目';
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
        } else if (item.conflictKind === 'unmatched' || item.mapStatus === 'unmatched') {
            kindBadge.className = 'badge bg-secondary';
            kindBadge.textContent = '未對應';
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
                        refreshUnmatched(unmatchedPage),
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

function renderUnmatchedPagination(totalPages) {
    const container = document.getElementById('prtg-unmatched-pagination');
    if (!container) return;

    renderPagination(container, {
        page: unmatchedPage,
        totalPages,
        onPage: page => {
            refreshUnmatched(page);
        },
        pageSize: unmatchedPageSize,
        onPageSize: size => {
            unmatchedPageSize = size;
            savePageSize('prtg-unmatched', size);
            refreshUnmatched(1);
        },
        pageSizeOptions: PAGE_SIZE_OPTIONS
    });
}

function renderIpExcludesLoading() {
    const tbody = document.getElementById('prtg-ip-excludes-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'text-muted text-center py-2';
    td.textContent = '載入中…';
    tr.appendChild(td);
    tbody.appendChild(tr);
}

function renderIpExcludesError(errorMessage) {
    const tbody = document.getElementById('prtg-ip-excludes-body');
    if (!tbody) return;
    tbody.replaceChildren();
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'text-danger text-center py-2';
    const textSpan = document.createElement('span');
    textSpan.textContent = `載入 IP 排除清單失敗：${errorMessage || '網路或伺服器錯誤'} `;
    const retryBtn = document.createElement('button');
    retryBtn.type = 'button';
    retryBtn.className = 'btn btn-sm btn-outline-danger py-0 ms-2';
    retryBtn.textContent = '重試';
    retryBtn.addEventListener('click', () => refreshIpExcludes());
    td.append(textSpan, retryBtn);
    tr.appendChild(td);
    tbody.appendChild(tr);
}

async function refreshIpExcludes() {
    renderIpExcludesLoading();
    try {
        const items = await api.get('/api/admin/settings/prtg-ip-excludes', { silent: true });
        renderIpExcludes(items || []);
    } catch (error) {
        renderIpExcludesError(error && error.message ? error.message : '載入 IP 排除清單失敗');
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
                    refreshUnmatched(unmatchedPage),
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
                    refreshUnmatched(unmatchedPage),
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
                refreshUnmatched(unmatchedPage),
                refreshIpExcludes()
            ]);
        } catch (error) {
            toast(error && error.message ? error.message : '指派失敗', 'danger');
        } finally {
            restore();
        }
    });
}

function populateBatchHostSelect(selectEl, hosts) {
    if (!selectEl) return;
    const currentVal = selectEl.value;
    selectEl.innerHTML = '<option value="">請選擇目標主機…</option>';
    for (const host of hosts) {
        const option = document.createElement('option');
        option.value = String(host.hostId);
        option.textContent = `${host.hostName}${host.ipAddress ? ` (${host.ipAddress})` : ''}`;
        if (currentVal && option.value === currentVal) {
            option.selected = true;
        }
        selectEl.appendChild(option);
    }
}

async function ensureBatchHostsLoaded() {
    const hostSelect = document.getElementById('prtg-conflict-batch-host');
    if (!hostSelect) return;
    if (cachedHosts && cachedHosts.length > 0) {
        populateBatchHostSelect(hostSelect, cachedHosts);
        return;
    }
    try {
        cachedHosts = await api.get('/api/admin/hosts/all', { silent: true });
        populateBatchHostSelect(hostSelect, cachedHosts || []);
    } catch {
        hostSelect.innerHTML = '<option value="">載入主機清單失敗</option>';
    }
}

function bindConflictBatchControls() {
    const selectAll = document.getElementById('prtg-conflict-select-all');
    if (selectAll) {
        selectAll.addEventListener('change', () => {
            const shouldCheck = selectAll.checked;
            for (const item of currentConflictItems) {
                if (shouldCheck) {
                    selectedConflictDeviceObjids.add(item.deviceObjid);
                } else {
                    selectedConflictDeviceObjids.delete(item.deviceObjid);
                }
            }
            const rowCheckboxes = document.querySelectorAll('.prtg-conflict-row-check');
            for (const cb of rowCheckboxes) {
                cb.checked = shouldCheck;
            }
            updateConflictBatchBar();
        });
    }

    const submitBtn = document.getElementById('prtg-conflict-batch-submit');
    const hostSelect = document.getElementById('prtg-conflict-batch-host');
    const noteInput = document.getElementById('prtg-conflict-batch-note');

    if (submitBtn) {
        submitBtn.addEventListener('click', async () => {
            const selectedIds = Array.from(selectedConflictDeviceObjids);
            if (selectedIds.length === 0) {
                toast('請先勾選欲指派的裝置', 'warning');
                return;
            }

            const hostIdVal = hostSelect?.value;
            if (!hostIdVal) {
                toast('請選擇目標主機', 'warning');
                return;
            }
            const targetHostId = Number(hostIdVal);
            const selectedHostText = hostSelect.options[hostSelect.selectedIndex]?.textContent || `主機 #${targetHostId}`;

            const confirmed = await confirmAction({
                message: `確認將已選取的 ${selectedIds.length} 台 PRTG 裝置指派給主機「${selectedHostText}」？`
            });
            if (!confirmed) return;

            const note = noteInput?.value.trim() || null;
            const restore = withBusy(submitBtn, '處理中');

            try {
                const res = await api.put('/api/admin/settings/prtg-manual-map/batch', {
                    hostId: targetHostId,
                    deviceObjids: selectedIds,
                    note
                });

                const succeeded = res?.succeededIds || [];
                const failedDeviceObjid = res?.failedDeviceObjid;
                const failureMessage = res?.failureMessage;
                const remapWarning = res?.remapWarning;

                for (const id of succeeded) {
                    selectedConflictDeviceObjids.delete(id);
                }

                if (!failedDeviceObjid) {
                    toast(`已成功指派 ${succeeded.length} 台裝置`, 'success');
                    if (noteInput) noteInput.value = '';
                } else {
                    const failMsg = `指派裝置 ${failedDeviceObjid} 失敗：${failureMessage || '儲存失敗'}。已成功 ${succeeded.length} 筆，其餘未處理。`;
                    toast(failMsg, 'danger');
                }

                if (remapWarning) {
                    toast(remapWarning, 'warning');
                }
                if (res?.auditWarning) {
                    toast(res.auditWarning, 'warning');
                }

                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage, { preserveSelection: true }),
                    refreshUnmatched(unmatchedPage),
                    refreshIpExcludes()
                ]);
            } catch (error) {
                toast(error && error.message ? error.message : '批次指派失敗', 'danger');
                await refreshConflicts(conflictPage, { preserveSelection: true });
            } finally {
                restore();
                updateConflictBatchBar();
            }
        });
    }
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
    await refreshScopePurge();
}

// ── 監看範圍外資料（預覽＋確認清除）────────────────────────────────────

function renderScopePurge(preview) {
    const btn = document.getElementById('prtg-scope-purge-btn');
    const blockedEl = document.getElementById('prtg-scope-purge-blocked');
    const summaryEl = document.getElementById('prtg-scope-purge-summary');
    const devicesEl = document.getElementById('prtg-scope-purge-devices');
    if (!btn || !blockedEl || !summaryEl || !devicesEl) return;

    // 自動清除被擋下的原因（縮小保護／第一次）：外部來源無關的站內字串，仍一律 textContent
    blockedEl.textContent = preview.blockedReason
        ? `夜間自動清除未執行（${formatDateTime(preview.blockedAt)}）：${preview.blockedReason}`
        : '';
    blockedEl.classList.toggle('d-none', !preview.blockedReason);

    if (!preview.success) {
        summaryEl.textContent = preview.errorMessage || '目前無法預覽。';
        devicesEl.textContent = '';
        btn.disabled = true;
        return;
    }

    const baseline = preview.baselineAt
        ? `基準：${formatDateTime(preview.baselineAt)} 清除時 ${formatNumber(preview.baselineDeviceCount)} 台`
        : '尚無基準（還沒清除過）';
    summaryEl.textContent = `監看裝置 ${formatNumber(preview.monitoredDevices)} 台；範圍外數值 ${formatNumber(preview.values)} 筆、`
        + `狀態變更 ${formatNumber(preview.stateChanges)} 筆，涉及 ${formatNumber(preview.affectedDevices)} 台裝置`
        + (preview.unknownSensors > 0 ? `及 ${formatNumber(preview.unknownSensors)} 顆鏡像已無的感測器` : '')
        + `。${baseline}`;
    const names = Array.isArray(preview.topDeviceNames) ? preview.topDeviceNames : [];
    devicesEl.textContent = names.length > 0
        ? `裝置：${names.join('、')}${preview.affectedDevices > names.length ? ' 等' : ''}`
        : '';
    btn.disabled = preview.values + preview.stateChanges === 0;
}

async function refreshScopePurge() {
    try {
        renderScopePurge(await api.get('/api/admin/settings/prtg-scope-purge/preview', { silent: true }));
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function bindScopePurge() {
    const btn = document.getElementById('prtg-scope-purge-btn');
    btn?.addEventListener('click', async () => {
        const confirmed = await confirmAction({
            message: '確定要清除監看範圍外的 PRTG 數值與狀態變更嗎？刪除後無法復原（重新納入監看的裝置只能靠回填補回保留期內的資料）。'
        });
        if (!confirmed) return;
        const restore = withBusy(btn, '清除中');
        try {
            const res = await api.post('/api/admin/settings/prtg-scope-purge/confirm', {});
            toast(`已清除數值 ${formatNumber(res.values)} 筆、狀態變更 ${formatNumber(res.stateChanges)} 筆`, 'success');
        } catch {
            // 錯誤已由 api.js 顯示
        } finally {
            restore();
        }
        await refreshScopePurge();
    });
}

function bindPrtgMirror() {
    const refreshBtn = document.getElementById('prtg-mirror-refresh');
    refreshBtn?.addEventListener('click', async () => {
        const restore = withBusy(refreshBtn, '載入中');
        try {
            await Promise.all([
                refreshPrtgMirror(),
                refreshConflicts(conflictPage),
                refreshUnmatched(unmatchedPage),
                refreshIpExcludes()
            ]);
            toast('已重新整理 PRTG 鏡像狀態', 'success');
        } catch {
            // 錯誤已由 api.js 顯示
        } finally {
            restore();
        }
    });
}

// ── PRTG API 探測（批次B-4，比照 NetIQ 診斷實作）─────────────────────────

let prtgProbePollTimer = null;

let diskReadinessPage = 1;
let diskReadinessLoaded = false;
let diskReadinessLoading = false;
let diskVerificationSensor = null;
let diskVerificationTimer = null;
const selectedDiskVerificationIds = new Set();
let pendingDiskSingleRequest = null;
let pendingDiskBatchRequest = null;

function pendingDiskRequestId(kind, signature) {
    const slot = kind === 'batch' ? pendingDiskBatchRequest : pendingDiskSingleRequest;
    if (slot?.signature === signature) return slot.requestId;
    const next = { signature, requestId: crypto.randomUUID() };
    if (kind === 'batch') pendingDiskBatchRequest = next; else pendingDiskSingleRequest = next;
    return next.requestId;
}

function updateDiskBatchButton() {
    const button = document.getElementById('prtg-disk-verification-batch-start');
    if (!button) return;
    button.textContent = `依序驗證所選（${selectedDiskVerificationIds.size}/5）`;
    button.disabled = selectedDiskVerificationIds.size < 1 || selectedDiskVerificationIds.size > 5;
}

function activateSelectedBackfillHost(hostName) {
    const name = String(hostName || '');
    const normalized = name.trim().toLocaleLowerCase();
    const host = (cachedHosts || []).find(item => String(item.hostName || '').trim().toLocaleLowerCase() === normalized);
    if (!host) {
        pendingBackfillHostId = name;
        const search = document.getElementById('prtg-selected-backfill-search');
        if (search) search.value = name;
    }
    else {
        pendingBackfillHostId = null;
        const id = String(host.hostId);
        if (!selectedBackfillHostIds.has(id) && selectedBackfillHostIds.size >= 5) {
            toast('已選滿 5 台主機；請先取消一台再加入這台。', 'warning');
            const search = document.getElementById('prtg-selected-backfill-search');
            if (search) search.value = host.hostName || '';
            renderSelectedBackfillHosts(cachedHosts || []);
            document.querySelector('#prtg-tabs [data-tab="selected-backfill"]')?.click();
            return;
        }
        if (!selectedBackfillHostIds.has(id)) selectedBackfillHostIds.add(id);
        const search = document.getElementById('prtg-selected-backfill-search');
        if (search) search.value = '';
        renderSelectedBackfillHosts(cachedHosts || []);
    }
    document.querySelector('#prtg-tabs [data-tab="selected-backfill"]')?.click();
    if (!host) renderSelectedBackfillHosts(cachedHosts || []);
    else [...document.querySelectorAll('#prtg-selected-backfill-hosts input[type="checkbox"]')]
        .find(item => item.value === String(host.hostId))?.focus();
}

function readinessText(tag, value, className = '') {
    const node = document.createElement(tag);
    if (className) node.className = className;
    node.textContent = value == null || value === '' ? '—' : String(value);
    return node;
}

function renderDiskReadiness(page) {
    const summary = document.getElementById('prtg-readiness-summary');
    const rows = document.getElementById('prtg-readiness-rows');
    const reasons = document.getElementById('prtg-readiness-reasons');
    if (!summary || !rows || !reasons) return;
    summary.replaceChildren();
    rows.replaceChildren();
    reasons.replaceChildren();

    const progress = readinessText('strong', `全局鏡像磁碟候選 ${page.globalMirrorCandidates} 顆`);
    summary.append(progress,
        readinessText('div', `目前映射至啟用主機 ${page.globallyMappedActive} 顆；白名單內 ${page.globallyWhitelisted} 顆；sensor/device 暫停 ${page.globallyPaused} 顆；最新對應衝突 ${page.globallyConflicted} 顆、未對應 ${page.globallyUnmapped} 顆、指向停用或合併主機 ${page.globallyDisabledHost} 顆。`),
        readinessText('div', '分類可重疊，不能相加；資料/語意就緒為前100抽樣。'),
        readinessText('div', page.readinessSummaryComputed
            ? `${page.readinessSummaryCapped ? `資料就緒度（Sensor Objid 順序前 ${page.readinessSummaryCandidateCount} 顆候選抽樣）：` : `資料就緒度（全部 ${page.readinessSummaryCandidateCount} 顆已映射候選）：`}每日有效小時與 28 日均達標 ${page.dataReadyCount} 顆；語意已驗證 ${page.semanticVerifiedCount} 顆；可供試算 ${page.previewReadyCount} 顆。`
            : '資料就緒、語意驗證與可試算摘要只在第 1 頁計算；返回第 1 頁可重新整理摘要。'),
        readinessText('div', `目前逐列分頁 ${page.page} / ${page.pageCount}；本頁 ${page.dataReadyOnPage} 顆資料就緒、${page.semanticVerifiedOnPage} 顆語意已驗證。`));
    if (page.readinessSummaryComputed)
        summary.append(readinessText('div', `資料就緒度與可試算數依目前已映射候選計算；${page.readinessSummaryCapped ? `僅為前 ${page.readinessSummaryCandidateCount} 顆候選抽樣，不代表全局。` : '本次覆蓋全部已映射候選。'}`));
    if (page.globalMirrorCandidates === 0) summary.append(readinessText('p', '全站鏡像目前沒有磁碟分類候選；請檢查結構同步與磁碟分類。', 'alert alert-info mt-2 mb-0'));
    else if (page.candidateSensors === 0) summary.append(readinessText('p', page.emptyState || '目前沒有映射到啟用主機的磁碟候選；請檢查主機對應。', 'alert alert-info mt-2 mb-0'));

    const reasonEntries = Object.entries(page.reasonCountsOnPage ?? {});
    if (reasonEntries.length) {
        reasons.append(readinessText('strong', '本頁逐列原因統計：'));
        for (const [reason, count] of reasonEntries) reasons.append(readinessText('span', ` ${reason} ${count} 筆；`, 'me-2'));
    }

    for (const row of page.rows ?? []) {
        const article = document.createElement('article');
        article.className = 'border rounded p-3';
        const title = document.createElement('h3');
        title.className = 'h6 mb-2';
        title.textContent = `${row.sensorName} (Sensor ${row.sensorObjid})`;
        const details = document.createElement('div');
        details.className = 'small d-grid gap-1';
        const latest = row.latestUsableHour ? formatDateTime(row.latestUsableHour) : '尚無可用小時';
        const mapped = Array.isArray(row.mappedHosts) && row.mappedHosts.length ? row.mappedHosts.join('、') : '未取得有效主機對應';
        const values = [
            `資料進度：${row.usableDays} / ${row.requiredDays} 天（每日至少 ${row.requiredHoursPerDay} 個可用小時；目前 ${row.usableHours} 小時）`,
            `最新可用小時：${latest}`,
            `映射主機：${mapped}；感測器類型 ${row.sensorType}（白名單資格依資料狀態與原因判讀）`,
            `資料狀態：${row.status}；語意狀態：${row.semanticVerified ? '已驗證' : row.semanticLabel}`,
            `原因：${row.reason}`
        ];
        for (const value of values) details.append(readinessText('div', value));
        const missingDetails = document.createElement('details');
        missingDetails.className = 'mt-1';
        const missingSummary = document.createElement('summary');
        missingSummary.textContent = '展開缺少的小時';
        const missingList = readinessText('div', '展開後載入…', 'mt-1');
        missingDetails.append(missingSummary, missingList);
        missingDetails.addEventListener('toggle', () => {
            if (!missingDetails.open || missingDetails.dataset.decoded === 'true') return;
            const masks = Array.isArray(row.missingHourMasks) ? row.missingHourMasks : [];
            const missing = [];
            for (let dayIndex = 0; dayIndex < masks.length; dayIndex++) {
                const mask = Number(masks[dayIndex]) >>> 0;
                for (let hour = 0; hour < 24; hour++) {
                    if ((mask & (1 << hour)) === 0) continue;
                    const date = missingHourDate(row.missingHourWindowStart, dayIndex, hour);
                    missing.push(formatDateTime(date));
                }
            }
            missingList.textContent = missing.length ? `缺少 ${missing.length} 個小時：${missing.join('、')}` : '28 日窗口內沒有缺少的小時。';
            missingDetails.dataset.decoded = 'true';
        });
        details.append(missingDetails);
        const verify = document.createElement('button');
        verify.type = 'button';
        verify.className = 'btn btn-sm btn-outline-primary mt-2 align-self-start';
        verify.textContent = '驗證這顆';
        verify.setAttribute('aria-label', `選取 ${row.sensorName}（Sensor ${row.sensorObjid}）進行語意驗證`);
        verify.addEventListener('click', () => selectDiskVerificationSensor(row));
        const actions = document.createElement('div');
        actions.className = 'd-flex flex-wrap align-items-center gap-2 mt-2';
        actions.append(verify);
        if (row.status === 'Ready') {
            const label = document.createElement('label'); label.className = 'form-check d-flex align-items-center gap-2 mb-0';
            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox'; checkbox.className = 'form-check-input m-0';
            checkbox.value = String(row.sensorObjid); checkbox.checked = selectedDiskVerificationIds.has(checkbox.value);
            checkbox.disabled = !checkbox.checked && selectedDiskVerificationIds.size >= 5;
            checkbox.setAttribute('aria-label', `加入批次驗證：${row.sensorName}（Sensor ${row.sensorObjid}）`);
            checkbox.addEventListener('change', () => {
                if (checkbox.checked) selectedDiskVerificationIds.add(checkbox.value); else selectedDiskVerificationIds.delete(checkbox.value);
                updateDiskBatchButton();
                renderDiskReadiness(page);
            });
            label.append(checkbox, readinessText('span', '加入批次驗證（最多 5 顆）'));
            actions.append(label);
        }
        const backfill = document.createElement('button');
        backfill.type = 'button'; backfill.className = 'btn btn-sm btn-link p-0'; backfill.textContent = '到指定主機補值';
        backfill.setAttribute('aria-label', `前往指定主機補值：${row.mappedHosts?.[0] || '對應主機'}`);
        backfill.addEventListener('click', () => activateSelectedBackfillHost(row.mappedHosts?.[0]));
        actions.append(backfill);
        article.append(title, details, actions);
        rows.append(article);
    }

    const label = document.getElementById('prtg-readiness-page-label');
    if (label) label.textContent = `第 ${page.page} / ${Math.max(page.pageCount, 1)} 頁；候選感測器共 ${page.candidateSensors} 筆（總數），上方資料與原因計數僅統計本頁。`;
    const prev = document.getElementById('prtg-readiness-prev');
    const next = document.getElementById('prtg-readiness-next');
    if (prev) prev.disabled = page.page <= 1;
    if (next) next.disabled = page.page >= page.pageCount;
}

function selectedDiskSensorId() { return diskVerificationSensor?.sensorObjid ?? null; }

async function refreshDiskRuleTrial() {
    const id = selectedDiskSensorId();
    const panel = document.getElementById('prtg-disk-rule-trial');
    if (!panel || !id) return;
    panel.textContent = '正在以最新完成日的已落地資料進行唯讀試算…';
    try {
        const trial = await api.get(`/api/prtg/disk-verification/${encodeURIComponent(id)}/rule-trial`, { silent: true });
        const lines = [
            trial.message,
            `完成日：${trial.completedDay}；資料品質：${trial.dataQuality}（${trial.usableDays}/${trial.requiredDays} 天，${trial.usableHours} 個可用小時；每日需 ${trial.requiredHoursPerDay} 小時）`,
            `語意：${trial.semanticVerified ? '已驗證' : '未驗證'}；排除原因：${trial.exclusion || '無'}`,
            trial.ruleId ? `規則：${trial.ruleId}（${trial.ruleEnabled ? '啟用' : '停用；試算仍使用已儲存門檻'}）` : '規則：未設定',
            trial.lowWaterPercent != null ? `門檻：低水位 ${trial.lowWaterPercent}%；下降至少 ${trial.minimumDeclinePerDay} 百分點／日；預估耗盡 ${trial.maximumDaysToDepletion} 日內` : null,
            trial.currentAvailablePercent != null ? `趨勢：目前 ${trial.currentAvailablePercent}%；穩健下降 ${trial.declinePerDay ?? '—'} 百分點／日；預估 ${trial.estimatedDaysToDepletion ?? '—'} 日耗盡；預測命中：${trial.predictedHit ? '是' : '否'}` : '趨勢：目前沒有可用的完整趨勢估計',
            `真實效果：${trial.message?.includes('real effect pending') ? '待觀察（尚無已證實的真實正向案例）' : trial.realPositiveStatus}`
        ].filter(Boolean);
        panel.replaceChildren(...lines.map(line => readinessText('div', line)));
    } catch (error) {
        panel.textContent = `唯讀規則試算失敗：${error?.message || '請重試。'}`;
    }
}

function renderDiskVerificationResult(result) {
    const status = document.getElementById('prtg-disk-verification-status');
    const evidence = document.getElementById('prtg-disk-verification-evidence');
    const form = document.getElementById('prtg-disk-manual-form');
    if (!status || !evidence || !form) return;
    evidence.replaceChildren();
    if (!result) { status.textContent = '尚無語意驗證結果。'; form.classList.add('d-none'); return; }
    const labels = { Verified: '語意已自動確認', NeedsManualReview: '候選需人工覆核', Mismatch: '資料不匹配', Failed: '驗證失敗，可重新驗證', TimedOut: '驗證逾時，可重新驗證', Cancelled: '驗證已取消，可重新驗證' };
    status.textContent = `${labels[result.status] || result.status}：${result.summary || '無摘要'}${result.cancelled ? '（已取消）' : ''}`;
    const fields = [
        `驗證時間：${result.checkedAtUtc ? formatDateTime(result.checkedAtUtc) : '—'}`,
        `資料日期：${result.dataDate || '—'}`,
        `主頻道：${result.channelName || '—'}（ID ${result.channelIdentifier || '—'}）`,
        `單位／尺度／方向：${result.unit || '—'}／${result.scale ?? '—'}／${result.direction || '—'}`,
        `落地資料比對：${result.comparedPointCount ?? 0} 點；${result.valuesMatch === true ? '相符' : result.valuesMatch === false ? '不相符' : '未完成比對'}`,
        `解析語意版本：${result.parserSemanticVersion || '—'}`
    ];
    for (const value of fields) evidence.append(readinessText('div', value));
    const canReview = result.status === 'NeedsManualReview' && result.valuesMatch === true && (result.comparedPointCount ?? 0) > 0 && !result.cancelled;
    form.classList.toggle('d-none', !canReview);
    const id = document.getElementById('prtg-manual-channel-id');
    const name = document.getElementById('prtg-manual-channel-name');
    const unit = document.getElementById('prtg-manual-unit');
    const scale = document.getElementById('prtg-manual-scale');
    if (canReview) {
        if (id) id.value = result.channelIdentifier || '';
        if (name) name.value = result.channelName || '';
        if (unit) unit.value = result.unit || '%';
        if (scale) scale.value = result.scale ?? 1;
    }
    const confirm = document.getElementById('prtg-manual-confirm');
    if (confirm) confirm.disabled = !canReview;
}

async function refreshDiskVerification() {
    const id = selectedDiskSensorId();
    if (!id) return;
    const statusEl = document.getElementById('prtg-disk-verification-status');
    try {
        const [run, evidence] = await Promise.all([
            api.get('/api/prtg/disk-verification', { silent: true }),
            api.get(`/api/prtg/disk-verification/${encodeURIComponent(id)}/evidence`, { silent: true })
        ]);
        const selectedRun = { ...run, selectedResult: run.results?.find(x => x.sensorObjid === id) };
        const running = run.isRunning;
        const cancel = document.getElementById('prtg-disk-verification-cancel');
        if (cancel) cancel.classList.toggle('d-none', !running);
        const start = document.getElementById('prtg-disk-verification-start');
        if (start) start.disabled = running;
        if (running && statusEl) statusEl.textContent = run.isBatch
            ? `批次驗證進行中：${run.batchCompleted}/${run.batchTotal} 顆已完成；目前 Sensor ${run.sensorObjid ?? '—'}。`
            : `Sensor ${run.sensorObjid ?? id} 昨天單日語意比對進行中…`;
        const batchResults = document.getElementById('prtg-disk-verification-batch-results');
        if (batchResults) {
            batchResults.replaceChildren();
            if (run.isBatch || run.batchTotal > 1) {
                batchResults.append(readinessText('strong', `批次進度：${run.batchCompleted}/${run.batchTotal}；站台重啟後進度不保留。`));
                for (const item of run.results || []) batchResults.append(readinessText('div', `Sensor ${item.sensorObjid}：${item.status} — ${item.summary}`));
            }
        }
        const evidenceList = document.getElementById('prtg-disk-verification-evidence');
        if (running) evidenceList?.replaceChildren();
        const valid = evidence.semantic;
        const semantic = readinessText('div', `持久語意證據：${valid?.isValid ? '有效' : valid?.invalidReason || '尚未確認'}`);
        evidenceList?.append(semantic);
        if (running) {
            document.getElementById('prtg-disk-manual-form')?.classList.add('d-none');
            const manualConfirm = document.getElementById('prtg-manual-confirm');
            if (manualConfirm) manualConfirm.disabled = true;
            if (!diskVerificationTimer) diskVerificationTimer = setInterval(refreshDiskVerification, 2500);
        } else {
            if (diskVerificationTimer) { clearInterval(diskVerificationTimer); diskVerificationTimer = null; }
            renderDiskVerificationResult(selectedRun.selectedResult);
            if (run.requestId && pendingDiskSingleRequest?.requestId === run.requestId) pendingDiskSingleRequest = null;
            if (run.requestId && pendingDiskBatchRequest?.requestId === run.requestId) pendingDiskBatchRequest = null;
            refreshDiskRuleTrial();
        }
    } catch (error) {
        if (statusEl) statusEl.textContent = `讀取驗證狀態失敗：${error?.message || '請重試。'}`;
    }
}

function selectDiskVerificationSensor(row) {
    diskVerificationSensor = row;
    const selection = document.getElementById('prtg-disk-verification-selection');
    if (selection) selection.textContent = `目前選取：${row.sensorName}（Sensor ${row.sensorObjid}；資料狀態：${row.status}；${row.usableDays}/${row.requiredDays} 天）`;
    for (const id of ['prtg-disk-verification-start', 'prtg-disk-verification-refresh']) {
        const button = document.getElementById(id); if (button) button.disabled = false;
    }
    const status = document.getElementById('prtg-disk-verification-status');
    if (status) status.textContent = '正在讀取此感測器的持久驗證結果…';
    refreshDiskVerification();
    refreshDiskRuleTrial();
    document.getElementById('prtg-disk-verification-title')?.focus();
}

function bindDiskVerification() {
    document.getElementById('prtg-disk-verification-start')?.addEventListener('click', async event => {
        const id = selectedDiskSensorId(); if (!id) return;
        const button = event.currentTarget; button.disabled = true;
        const yesterday = new Date(); yesterday.setDate(yesterday.getDate() - 1);
        const dataDate = `${yesterday.getFullYear()}-${String(yesterday.getMonth() + 1).padStart(2, '0')}-${String(yesterday.getDate()).padStart(2, '0')}`;
        const requestId = pendingDiskRequestId('single', `${id}|${dataDate}`);
        try { await api.post('/api/prtg/disk-verification/start', { sensorObjid: id, dataDate, requestId }); await refreshDiskVerification(); }
        catch (error) { const status = document.getElementById('prtg-disk-verification-status'); if (status) status.textContent = `無法啟動驗證：${error?.message || '請重試。'}`; button.disabled = false; }
    });
    document.getElementById('prtg-disk-verification-batch-start')?.addEventListener('click', async event => {
        const ids = [...selectedDiskVerificationIds].map(Number);
        if (ids.length < 1 || ids.length > 5) return;
        const button = event.currentTarget; button.disabled = true;
        const yesterday = new Date(); yesterday.setDate(yesterday.getDate() - 1);
        const dataDate = localDateInputValue(yesterday);
        const requestId = pendingDiskRequestId('batch', `${ids.join(',')}|${dataDate}`);
        const status = document.getElementById('prtg-disk-verification-status');
        try {
            await api.post('/api/admin/settings/prtg-disk-verification/batch/start', {
                sensorObjids: ids, dataDate, requestId
            });
            if (status) status.textContent = `已依序啟動 ${ids.length} 顆感測器的昨天單日語意驗證；每顆結果會分別保存。`;
            await refreshDiskVerification();
        } catch (error) {
            if (status) status.textContent = `批次無法啟動：${error?.message || '請確認選取項目與資料準備度。'}`;
            updateDiskBatchButton();
        }
    });
    document.getElementById('prtg-disk-verification-refresh')?.addEventListener('click', () => { refreshDiskVerification(); refreshDiskRuleTrial(); });
    document.getElementById('prtg-disk-verification-cancel')?.addEventListener('click', async event => {
        const button = event.currentTarget; button.disabled = true;
        try { await api.post('/api/prtg/disk-verification/cancel', {}); const status = document.getElementById('prtg-disk-verification-status'); if (status) status.textContent = '已送出取消要求；正在等待 PRTG 請求中止並保存取消結果…'; await refreshDiskVerification(); }
        catch (error) { const status = document.getElementById('prtg-disk-verification-status'); if (status) status.textContent = `取消失敗：${error?.message || '請重試。'}`; }
        finally { button.disabled = false; }
    });
    document.getElementById('prtg-disk-manual-form')?.addEventListener('submit', async event => {
        event.preventDefault();
        const id = selectedDiskSensorId(); const submit = document.getElementById('prtg-manual-confirm');
        if (!id || !submit || submit.disabled) return;
        submit.disabled = true;
        const value = selector => document.querySelector(selector)?.value?.trim() || '';
        try {
            await api.post('/api/prtg/disk-verification/confirm', { sensorObjid: id, channelIdentifier: value('#prtg-manual-channel-id'), channelName: value('#prtg-manual-channel-name'), unit: value('#prtg-manual-unit'), scale: Number(value('#prtg-manual-scale')), direction: value('#prtg-manual-direction'), reason: value('#prtg-manual-reason') });
            const status = document.getElementById('prtg-disk-verification-status'); if (status) status.textContent = '人工語意確認已保存並稽核；值型規則仍須在規則頁另行管理。';
            await refreshDiskVerification();
            await refreshDiskRuleTrial();
        } catch (error) { const status = document.getElementById('prtg-disk-verification-status'); if (status) status.textContent = `人工確認未保存：${error?.message || '請修正欄位或重新驗證。'}`; submit.disabled = false; }
    });
}

async function loadDiskReadiness(force = false) {
    if (diskReadinessLoading || (diskReadinessLoaded && !force)) return;
    const root = document.getElementById('prtg-disk-readiness');
    if (!root) return;
    diskReadinessLoading = true;
    const retry = document.getElementById('prtg-readiness-retry');
    if (retry) retry.disabled = true;
    document.getElementById('prtg-readiness-summary').textContent = '正在讀取已儲存的磁碟資料…';
    try {
        const result = await api.get(`/api/prtg/disk-readiness?page=${diskReadinessPage}&pageSize=20`, { silent: true });
        renderDiskReadiness(result);
        diskReadinessLoaded = true;
    } catch (error) {
        const summary = document.getElementById('prtg-readiness-summary');
        summary.replaceChildren(readinessText('span', error?.message || '讀取磁碟資料準備度失敗。', 'text-danger'));
        diskReadinessLoaded = false;
    } finally {
        diskReadinessLoading = false;
        if (retry) retry.disabled = false;
    }
}

function bindDiskReadiness() {
    document.getElementById('prtg-readiness-retry')?.addEventListener('click', () => {
        diskReadinessPage = 1;
        loadDiskReadiness(true);
    });
    document.getElementById('prtg-readiness-prev')?.addEventListener('click', () => {
        if (diskReadinessPage > 1) { diskReadinessPage--; diskReadinessLoaded = false; loadDiskReadiness(); }
    });
    document.getElementById('prtg-readiness-next')?.addEventListener('click', () => {
        diskReadinessPage++; diskReadinessLoaded = false; loadDiskReadiness();
    });
}

function renderPrtgProbeStatus(status) {
    const outputEl = document.getElementById('prtg-probe-output');
    const copyButton = document.getElementById('prtg-probe-copy');
    const startButton = document.getElementById('prtg-probe-start');
    const flowButton = document.getElementById('prtg-probe-flow-start');
    const cancelBtn = document.getElementById('prtg-probe-cancel');
    const statusEl = document.getElementById('prtg-probe-status');

    if (!outputEl || !copyButton || !startButton || !statusEl) return;

    if (cancelBtn) {
        cancelBtn.classList.toggle('d-none', !status.isRunning);
        if (!status.isRunning) cancelBtn.disabled = false;
    }

    const outputText = Array.isArray(status.output) ? status.output.join('\n') : (status.output || '');
    outputEl.value = outputText;
    if (outputText) {
        outputEl.scrollTop = outputEl.scrollHeight;
    }
    copyButton.disabled = !outputText;

    if (status.isRunning) {
        startButton.disabled = true;
        if (flowButton) flowButton.disabled = true;
        setSpinnerText(statusEl, `探測中…${elapsedSinceText(status.startedAt)}${status.latestMessage ? ' ' + status.latestMessage : ''}`);
        return;
    }

    startButton.disabled = false;
    if (flowButton) flowButton.disabled = false;
    if (!status.completedAt) {
        statusEl.textContent = '';
        return;
    }
    const outcomeText = status.cancelled
        ? '已停止'
        : (status.success ? '✓ 完成' : '✗ 未通過，請查看輸出');
    statusEl.textContent = `上次執行：${formatDateTime(status.completedAt)} ${outcomeText}`;
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
    const flowButton = document.getElementById('prtg-probe-flow-start');
    const cancelBtn = document.getElementById('prtg-probe-cancel');
    const copyButton = document.getElementById('prtg-probe-copy');
    const outputEl = document.getElementById('prtg-probe-output');
    if (!startButton || !copyButton || !outputEl) return;

    async function startProbe(dataFlow) {
        // 探測用的是「已儲存」的連線設定；表單連填都沒填時直接前置提示，不必打 API
        if (!document.getElementById('prtg-url')?.value.trim()) {
            toast('請先設定並儲存 PRTG 位址與認證資訊，再執行探測。', 'warning');
            return;
        }

        // 不用 withBusy：啟動成功後按鈕的 disabled 狀態交給輪詢狀態接管
        startButton.disabled = true;
        if (flowButton) flowButton.disabled = true;
        try {
            const path = dataFlow ? '/api/admin/settings/prtg-probe/data-flow/start' : '/api/admin/settings/prtg-probe/start';
            await api.post(path, {}, { silent: true });
            toast(dataFlow ? '已開始小範圍資料流驗證' : '已開始探測 PRTG 環境', 'success');
            await refreshPrtgProbeStatus();
        } catch (error) {
            // 啟動失敗（如尚未設定連線位址、與回填互斥）：訊息要讓使用者看得到，不能靜默
            startButton.disabled = false;
            if (flowButton) flowButton.disabled = false;
            toast(error?.message || '無法啟動 PRTG 探測。', 'danger');
        }
    }
    startButton.addEventListener('click', () => startProbe(false));
    flowButton?.addEventListener('click', () => startProbe(true));

    cancelBtn?.addEventListener('click', async () => {
        const restore = withBusy(cancelBtn, '停止中');
        try {
            await api.post('/api/admin/settings/prtg-probe/cancel', {});
            toast('已送出停止探測要求', 'success');
            await refreshPrtgProbeStatus();
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
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

function bindProbeWhitelistFill() {
    const button = document.getElementById('prtg-sensor-whitelist-probe-fill-btn');
    const input = document.getElementById('prtg-sensor-type-whitelist');
    if (!button || !input) return;
    button.addEventListener('click', async () => {
        const restore = withBusy(button, '讀取中');
        try {
            const status = await api.get('/api/admin/settings/prtg-probe/status', { silent: true });
            if (status.isRunning || !status.completedAt) {
                toast('請先完成一次 PRTG 環境探測，再帶入觀察到的感測器類型。', 'warning');
                return;
            }
            const types = parseProbeSensorTypes(status.output);
            if (types.length === 0) {
                toast('這次探測沒有取得可用的 Type 分布，原白名單未變更。', 'warning');
                return;
            }
            if (input.value.trim() && !await confirmAction({
                title: '取代目前的白名單？',
                message: `探測共觀察到 ${types.length} 種類型。這會取代目前輸入的內容，但不會自動儲存。`,
                confirmText: '帶入類型',
                confirmVariant: 'primary'
            })) return;
            input.value = types.join('\n');
            input.dispatchEvent(new Event('input', { bubbles: true }));
            toast(`已帶入 ${types.length} 種抽樣類型，請確認內容再儲存。`, 'success');
        } catch (error) {
            toast(error?.message || '無法讀取環境探測結果。', 'danger');
        } finally {
            restore();
        }
    });
}

async function refreshScheduleWarning() {
    const banner = document.getElementById('prtg-schedule-banner');
    if (!banner) return;
    try {
        const status = await api.get('/api/admin/schedule/status', { silent: true });
        banner.replaceChildren();
        if (status.scheduleEnabled) return;
        const alert = document.createElement('div');
        alert.className = 'alert alert-warning';
        alert.setAttribute('role', 'status');
        alert.append('排程尚未啟用；快照可能持續取值，但每日規則評估不會自動執行。');
        const link = document.createElement('a');
        link.href = appUrl('/runs#settings');
        link.className = 'alert-link ms-2';
        link.textContent = '前往排程設定';
        alert.appendChild(link);
        banner.appendChild(alert);
    } catch {
        renderError(banner, { message: '無法確認排程是否啟用。', onRetry: refreshScheduleWarning });
    }
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
    const cancelBtn = document.getElementById('prtg-structure-sync-cancel-btn');
    if (!statusEl || !progressEl || !btn) return;

    if (status.isRunning) {
        structureSyncRunning = true;
        btn.disabled = true;
        btn.textContent = '同步中…';
        // 停止鈕只在真的有東西可停時出現：沒有執行中時後端一律回 409
        cancelBtn?.classList.remove('d-none');
        statusEl.textContent = status.latestMessage || '同步進行中…';
        const phase = status.progressPhase;
        // 走與排程作業頁同一份對照表，不把 prtg-sync-devices 這種裸 phase 印給使用者
        const label = PROGRESS_PHASE_LABEL[phase] ?? phase;
        progressEl.textContent = phase && status.progressTotal > 0
            ? `${label}：${status.progressDone} / ${status.progressTotal}`
            : (phase ? `${label}：進行中` : '');
        return;
    }

    structureSyncRunning = false;
    // 未啟用時的閘由 syncStructureSyncGate 設定；這裡是輪詢，不能把它打開
    btn.disabled = !prtgEnabled;
    btn.textContent = '同步結構與對應';
    cancelBtn?.classList.add('d-none');
    if (cancelBtn) cancelBtn.disabled = false;
    progressEl.textContent = '';

    if (!status.lastCompletedAt) {
        // 從未同步過與「同步到 0 筆」是兩件事，文案要分得出來
        statusEl.textContent = '尚未同步。PRTG 剛啟用時請先執行一次，否則主機對應與資源守門的自動偵測都沒有資料可用。';
        return;
    }

    const when = formatDateTime(status.lastCompletedAt);
    const sourceLabel = status.lastSource === 'nightly' ? '（夜間取數）' : '（手動）';
    if (status.lastSuccess) {
        statusEl.textContent =
            `上次同步：${when}${sourceLabel}　裝置 ${status.lastDevices ?? 0}、感測器 ${status.lastSensors ?? 0}；`
            + `對應成功 ${status.lastMapOk ?? 0}、人工 ${status.lastMapManual ?? 0}、`
            + `衝突 ${status.lastMapConflict ?? 0}、查無主機 ${status.lastMapUnmatched ?? 0}、`
            + `略過 ${status.lastMapSkipped ?? 0}`;
    } else {
        // 未成功的同步＝鏡像可能只有半套，要說出後續會怎樣，否則使用者不知道該不該重跑
        statusEl.textContent = `上次同步（${when}）未成功：${status.lastErrorMessage || '原因不明'}`
            + '。鏡像可能不完整，夜間取數會重新同步。';
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
            toast('PRTG 擷取未啟用，請先在「擷取參數」頁籤選擇數值取數對象。', 'warning');
            return;
        }
        const restore = withBusy(btn, '啟動中');
        try {
            await api.post('/api/admin/settings/prtg-structure-sync/start', {});
            toast('已開始同步結構與對應', 'success');
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
        }
        // 輪詢要在 restore 之後：restore 會把按鈕設回可按，若先輪詢再 restore，
        // 同步進行中的「灰掉」會被 restore 打開三秒。
        await refreshStructureSyncStatus();
    });

    const cancelBtn = document.getElementById('prtg-structure-sync-cancel-btn');
    cancelBtn?.addEventListener('click', async () => {
        const restore = withBusy(cancelBtn, '停止中');
        try {
            await api.post('/api/admin/settings/prtg-structure-sync/cancel', {});
            toast('已送出停止要求，進行中的查詢會被中斷', 'success');
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示
        } finally {
            restore();
        }
        await refreshStructureSyncStatus();
    });
}

// 規則未套用提示：內建規則有新版本未套用，或沒有任何啟用中的 PRTG 規則時，夜間 PRTG 評估會不完整
async function refreshPrtgRuleBanner() {
    const [status, rules] = await Promise.all([
        api.get('/api/rules/import-status', { silent: true }).catch(() => null),
        api.get('/api/rules', { silent: true }).catch(() => null)
    ]);
    const banner = document.getElementById('prtg-rule-banner');
    if (!banner) return;
    banner.replaceChildren();
    const hasUpdate = status != null && status.hasUpdate === true;
    const noPrtgRules = Array.isArray(rules) && !rules.some(r =>
        r.enabled && String(r.platform).toLowerCase() === 'prtg' && r.prtgRuleCode);
    if (!hasUpdate && !noPrtgRules) return;
    const alert = document.createElement('div');
    alert.className = 'alert alert-warning';
    // 兩種原因給不同的話：沒有任何啟用中的 PRTG 規則時整晚零評估，比「有新版未套用」嚴重
    alert.appendChild(document.createTextNode(noPrtgRules
        ? '規則庫沒有任何啟用中的 PRTG 規則，PRTG 狀態不會產生任何問題訊號。請套用內建規則更新或啟用 PRTG 規則。'
        : '內建規則有新版本尚未套用，PRTG 規則評估可能不完整。'));
    const link = document.createElement('a');
    link.href = appUrl('/admin/rules');
    link.className = 'ms-2';
    link.textContent = '前往規則維護';
    alert.appendChild(link);
    banner.appendChild(alert);
}

function bindUnmatchedControls() {
    const badge = document.getElementById('prtg-mirror-map-unmatched');
    const section = document.getElementById('prtg-unmatched-section');
    const collapseEl = document.getElementById('prtg-unmatched-collapse');
    const toggleBtn = document.getElementById('prtg-unmatched-toggle-btn');

    const ensureExpanded = () => {
        if (!collapseEl) return;
        if (window.bootstrap?.Collapse) {
            const inst = window.bootstrap.Collapse.getOrCreateInstance(collapseEl, { toggle: false });
            inst.show();
        } else {
            collapseEl.classList.add('show');
        }
        if (toggleBtn) {
            toggleBtn.textContent = '收合';
            toggleBtn.setAttribute('aria-expanded', 'true');
        }
    };

    if (toggleBtn && collapseEl) {
        toggleBtn.addEventListener('click', () => {
            if (window.bootstrap?.Collapse) {
                const inst = window.bootstrap.Collapse.getOrCreateInstance(collapseEl, { toggle: false });
                inst.toggle();
            } else {
                const isShown = collapseEl.classList.contains('show');
                if (isShown) {
                    collapseEl.classList.remove('show');
                    toggleBtn.textContent = '展開';
                    toggleBtn.setAttribute('aria-expanded', 'false');
                } else {
                    collapseEl.classList.add('show');
                    toggleBtn.textContent = '收合';
                    toggleBtn.setAttribute('aria-expanded', 'true');
                }
            }
        });

        collapseEl.addEventListener('shown.bs.collapse', () => {
            toggleBtn.textContent = '收合';
            toggleBtn.setAttribute('aria-expanded', 'true');
        });
        collapseEl.addEventListener('hidden.bs.collapse', () => {
            toggleBtn.textContent = '展開';
            toggleBtn.setAttribute('aria-expanded', 'false');
        });
    }

    if (badge && section) {
        const jumpToUnmatched = () => {
            ensureExpanded();
            section.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            section.focus();
        };

        badge.addEventListener('click', jumpToUnmatched);
        badge.addEventListener('keydown', event => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                jumpToUnmatched();
            }
        });
    }
}

function init() {
    document.getElementById('prtg-snapshot-diagnostics-retry')?.addEventListener('click', loadSnapshotDiagnostics);
    bindSelectedBackfill();
    bindPrtgEffectiveness();
    getCurrentUser().then(user => {
        if (hasCapability(user, 'DevMonitor')) {
            document.getElementById('prtg-data-transfer-advanced')?.classList.remove('d-none');
        }
    }).catch(() => {});
    bindPrtgTest();
    bindPrtgMirror();
    bindScopePurge();
    bindPrtgProbe();
    bindDiskReadiness();
    bindDiskVerification();
    bindProbeWhitelistFill();
    bindAssignForm();
    bindPrtgDataTransfer();
    bindConnectionForm();
    bindParamsForm();
    bindScopeControls();
    bindStructureSync();
    bindConflictBatchControls();
    bindUnmatchedControls();
    initCalibration();
    loadSettings();
    ensureBatchHostsLoaded();
    refreshPrtgMirror();
    loadSnapshotDiagnostics();
    refreshConflicts(1);
    refreshUnmatched(1);
    refreshIpExcludes();
    refreshPrtgProbeStatus();
    refreshStructureSyncStatus();
    refreshPrtgRuleBanner();
    refreshScheduleWarning();
}

init();
