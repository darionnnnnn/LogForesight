/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、擷取參數、鏡像狀態與環境探測。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { PROGRESS_PHASE_LABEL } from '../core/run-phases.js';
import {
    bindTabs, toast, withBusy, setSpinnerText, confirmAction, guardLoad, renderSpinner,
    renderPagination, loadPageSize, savePageSize, PAGE_SIZE_OPTIONS, bindRememberedCollapse,
    collectLines, renderTable, renderError
} from '../core/ui.js';
import { formatDate, elapsedSinceText, formatDateTime, formatNumber, formatUserName, prtgFreshnessLabel, toLocalDateString } from '../core/format.js';
import { initCalibration } from './prtg-calibration.js';
import { initValueReadiness } from './prtg-value-readiness.js';
import { toScopeSelectValue, prtgScopeInapplicableText, prtgHostMapSummary } from '../core/prtg-scope-labels.js';
import { parseProbeSensorTypes } from '../core/prtg-probe-types.js';
import { uploadDiagnosticFile, getDiagnosticTransfer, abandonDiagnosticTransfer } from '../core/prtg-diagnostic-upload.js';
import { evaluateScheduledAcceptance } from '../core/prtg-acceptance.js';

bindTabs(document.getElementById('prtg-tabs'), { hash: true });

let scopeRevision = null;
// 最近一次排程驗收的三個來源：undefined＝讀取中、null＝讀取失敗（見 core/prtg-acceptance.js）
const setupState = {
    settings: null, settingsLoading: true, probe: null, sync: null, rules: null, schedule: null,
    scheduleStatus: undefined, aiStatus: undefined, snapshotHours: undefined
};

const ACCEPTANCE_STATE_TEXT = {
    ok: '已確認', pending: '待確認', na: '不適用', nodata: '無資料', loading: '讀取中', error: '無法讀取'
};

/** 啟用準備全部確認後才出現：用最近一趟排程的實際結果驗收，而不是只看設定齊不齊。 */
function renderAcceptance(allConfirmed) {
    const host = document.getElementById('prtg-acceptance');
    if (!host) return;
    host.hidden = !allConfirmed;
    host.replaceChildren();
    if (!allConfirmed) return;
    const title = document.createElement('h3');
    title.className = 'h6 mb-1';
    title.textContent = '最近一次排程驗收';
    const list = document.createElement('ul');
    list.className = 'small mb-0';
    for (const item of evaluateScheduledAcceptance(setupState)) {
        const li = document.createElement('li');
        li.dataset.acceptance = item.key;
        li.dataset.state = item.state;
        li.append(document.createTextNode(`${ACCEPTANCE_STATE_TEXT[item.state]}：${item.label}。${item.detail}`));
        const link = document.createElement(item.path ? 'a' : 'button');
        if (item.path) link.href = appUrl(item.path);
        else { link.type = 'button'; link.className = 'btn btn-link btn-sm p-0 align-baseline'; link.addEventListener('click', () => document.querySelector(`#prtg-tabs [data-tab="${item.tab}"]`)?.click()); }
        link.classList.add('ms-1');
        link.textContent = item.linkText;
        li.appendChild(link);
        list.appendChild(li);
    }
    const refresh = document.createElement('button');
    refresh.type = 'button';
    refresh.className = 'btn btn-outline-secondary btn-sm mt-1';
    refresh.textContent = '重新確認';
    refresh.addEventListener('click', refreshAcceptance);
    host.append(title, list, refresh);
}

/** 三個來源都只在載入時讀一次；取數或 AI 跑完後按這顆重抓，不必重新整理整頁。 */
function refreshAcceptance() {
    setupState.scheduleStatus = undefined;
    setupState.aiStatus = undefined;
    setupState.snapshotHours = undefined;
    renderSetupSummary();
    refreshScheduleWarning();
    loadSnapshotDiagnostics();
    refreshAcceptanceAi();
}

async function refreshAcceptanceAi() {
    try {
        setupState.aiStatus = await api.get('/api/admin/schedule/ai-status', { silent: true });
    } catch {
        setupState.aiStatus = null;
    }
    renderSetupSummary();
}

function renderSetupSummary() {
    const summary = document.getElementById('prtg-setup-summary');
    const steps = document.getElementById('prtg-setup-steps');
    if (!summary || !steps) return;
    const settings = setupState.settings;
    if (!settings) {
        summary.textContent = setupState.settingsLoading
            ? '正在確認已儲存設定…'
            : '無法確認已儲存設定；請重新載入頁面。';
        steps.replaceChildren();
        renderAcceptance(false);
        return;
    }
    const credential = settings.prtgAuthMode === 'password' ? settings.prtgHasPassword && settings.prtgUsername
        : settings.prtgAuthMode === 'passhash' ? settings.prtgHasPasshash && settings.prtgUsername : settings.prtgHasApiToken;
    const checks = [
        { ok: Boolean(settings.prtgUrl && credential), label: '已儲存的連線位址與認證', next: '設定連線並儲存', tab: 'connection' },
        { ok: Boolean(settings.prtgEnabled), label: 'PRTG 擷取', next: '到擷取參數啟用並儲存', tab: 'params' },
        { ok: !setupState.sync?.isRunning && setupState.sync?.lastSuccess === true && Boolean(setupState.sync?.lastCompletedAt), label: '上次結構與主機對應同步', next: setupState.sync?.isRunning ? '同步進行中，稍後確認結果' : '同步結構並檢查主機對應', tab: 'mirror' },
        { ok: setupState.rules === true, label: 'PRTG 規則', next: '檢查並啟用 PRTG 規則', href: appUrl('/admin/rules') },
        { ok: setupState.schedule === true, label: '每日排程', next: '前往排程設定檢查', href: appUrl('/runs#settings') }
    ];
    checks.splice(1, 0, {
        ok: setupState.probe === true, label: '上次環境探測', next: setupState.probe === false ? '查看上次探測失敗原因並重試' : '執行環境探測，確認站台資料能力', tab: 'probe'
    });
    steps.replaceChildren();
    for (const check of checks) {
        const item = document.createElement('li');
        item.append(document.createTextNode(`${check.ok ? '已確認' : '待確認'}：${check.label}。`));
        if (!check.ok) {
            const link = document.createElement(check.href ? 'a' : 'button');
            if (check.href) link.href = check.href;
            else { link.type = 'button'; link.className = 'btn btn-link btn-sm p-0 align-baseline'; link.addEventListener('click', () => document.querySelector(`#prtg-tabs [data-tab="${check.tab}"]`)?.click()); }
            link.textContent = check.next;
            item.appendChild(link);
        }
        steps.appendChild(item);
    }
    const pending = checks.filter(check => !check.ok).length;
    summary.textContent = pending ? `尚有 ${pending} 項需確認；下列步驟依已儲存設定與最近一次狀態顯示。`
        : '設定與最近一次檢查均已確認；下方以最近一趟排程的實際結果驗收，資料涵蓋另見數值資料就緒度與使用效果。';
    renderAcceptance(pending === 0);
}

function missingHourDate(windowStart, dayIndex, hour) {
    const [year, month, day] = String(windowStart).slice(0, 10).split('-').map(Number);
    const date = new Date(year, month - 1, day);
    date.setDate(date.getDate() + dayIndex);
    date.setHours(hour, 0, 0, 0);
    return date;
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
    const ruleMarks = document.getElementById('prtg-effectiveness-rule-marks');
    if (!metrics || !status || !semantics || !limitations) return;
    metrics.replaceChildren();
    const definitions = [
        ['低 coverage sampled 小時', summary.lowCoverageSampledHours, '已落盤 sampled 且 coverage 低於門檻的小時列數；不包含完全缺值。此指標不涵蓋所有磁碟不就緒原因。'],
        ['問題訊號', summary.prtgFindings, 'PRTG TopIssue 列數；依主機日／特徵計數。'],
        ['僅有 PRTG 主要問題的主機日', summary.prtgOnlyHostDays, '該主機日的 TopIssue 全來自 PRTG；不表示沒有其他較低優先級日誌。'],
        ['建立案件', summary.casesCreated, '所選期間建立、來源為 PRTG 的案件列數。'],
        ['建立交辦單', summary.workOrdersCreated, '所選期間建立、來源為 PRTG 的交辦單列數。'],
        ['曾有回覆的交辦單', summary.workOrdersReplied, '上述建立交辦單中 LastReplyAt 有值的列數，不限回覆日期。'],
        ['抑制的問題訊號', summary.suppressedFindings, '日期分析內容中標記為抑制的 PRTG 特徵數。'],
        ['期間有靜音設定的問題', summary.mutedPrtgProfiles, '來源為 PRTG、靜音區間與所選日期重疊的問題檔案數；不代表該期間曾出現 finding。'],
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

    if (ruleMarks) {
        ruleMarks.replaceChildren();
        const marks = summary.ruleMarks || [];
        const totalFp = summary.totalFalsePositiveMarks ?? 0;
        const totalHandled = summary.totalHandledMarks ?? 0;
        const totalMarks = totalFp + totalHandled;
        if (marks.length === 0 || totalMarks === 0) {
            const emptyEl = document.createElement('div');
            emptyEl.className = 'small text-muted mt-3';
            emptyEl.textContent = '尚無標記';
            ruleMarks.appendChild(emptyEl);
        } else {
            const wrapper = document.createElement('div');
            wrapper.className = 'mt-3';
            const note = document.createElement('p');
            note.className = 'small text-muted mb-2';
            note.textContent = '分母是已標記的 PRTG 問題';
            wrapper.appendChild(note);

            const marksHost = document.createElement('div');
            renderTable(marksHost, {
                caption: '分母是已標記的 PRTG 問題',
                columns: [
                    { title: '規則代碼', render: item => item.ruleCode || '(未分類)' },
                    { title: '標記誤報', render: item => formatNumber(item.falsePositive ?? 0) },
                    { title: '已處理', render: item => formatNumber(item.handled ?? 0) }
                ],
                rows: marks,
                footer: ['總計', formatNumber(totalFp), formatNumber(totalHandled)]
            });
            wrapper.appendChild(marksHost);
            ruleMarks.appendChild(wrapper);
        }
    }

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
    const restoreButton = withBusy(button, '查詢中');
    status.className = 'small mb-3';
    status.textContent = '正在讀取使用效果…';
    error.replaceChildren();
    try {
        const query = new URLSearchParams({ from: fromInput.value, through: throughInput.value });
        const summary = await api.get(`/api/prtg/effectiveness?${query.toString()}`, { silent: true });
        renderEffectiveness(summary);
        const hasCount = [summary.lowCoverageSampledHours, summary.prtgFindings, summary.prtgOnlyHostDays, summary.casesCreated, summary.workOrdersCreated,
            summary.workOrdersReplied, summary.suppressedFindings, summary.mutedPrtgProfiles, summary.corroboratedHostDays]
            .some(value => value != null && value > 0);
        if (!hasCount) {
            status.textContent += ' 此期間沒有可計數的資料；若預期應有資料，請先查看環境探測與資料準備度。';
        }
        status.className = 'small mb-3 text-muted';
    } catch (exception) {
        metrics.classList.add('d-none');
        const ruleMarks = document.getElementById('prtg-effectiveness-rule-marks');
        if (ruleMarks) ruleMarks.replaceChildren();
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
        restoreButton();
    }
}

function bindPrtgEffectiveness() {
    const fromInput = document.getElementById('prtg-effectiveness-from');
    const throughInput = document.getElementById('prtg-effectiveness-through');
    const refresh = document.getElementById('prtg-effectiveness-refresh');
    if (!fromInput || !throughInput || !refresh) return;
    const today = new Date();
    const firstDay = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 29);
    fromInput.value = toLocalDateString(firstDay);
    throughInput.value = toLocalDateString(today);
    refresh.addEventListener('click', loadPrtgEffectiveness);
}

/** 目前已儲存的 PRTG 擷取開關。結構同步只更新鏡像，不會變更這個開關。 */
let prtgEnabled = false;
/** 已儲存的位址與認證是否足以啟動結構同步。 */
let prtgStructureConfigured = false;
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

let fullBackfillPreview = null;

/** 全部重做（忽略完成水位）是否勾選；沒有已完成日子時不會出現這個勾選。 */
function fullBackfillRedoChecked() {
    return document.getElementById('prtg-full-backfill-redo')?.checked === true;
}

/** 全部日子都已完成且沒勾全部重做：按下去後端一定拒絕，直接停用並說明。 */
function syncFullBackfillStart(preview) {
    const start = document.getElementById('prtg-full-backfill-start');
    const hint = document.getElementById('prtg-full-backfill-redo-hint');
    const allDone = preview.completedDays > 0 && preview.completedDays >= preview.dayCount && !fullBackfillRedoChecked();
    if (start) start.disabled = !preview.previewId || allDone;
    if (hint) hint.hidden = !allDone;
}

function renderFullBackfillPreview(preview) {
    const root = document.getElementById('prtg-full-backfill-preview-result');
    const start = document.getElementById('prtg-full-backfill-start');
    if (!root || !start) return;
    // 重新預覽會整段重畫：先記住「全部重做」有沒有勾，不然勾了再按預覽就得再勾一次
    const redoWas = fullBackfillRedoChecked();
    root.replaceChildren();
    const summary = document.createElement('p');
    summary.className = 'small mb-2';
    summary.textContent = `${preview.fromDate.slice(0, 10)}～${preview.toDate.slice(0, 10)}，${formatNumber(preview.dayCount)} 天、` +
        `${formatNumber(preview.estimatedHistoricRequests)} 次 historicdata；配額時間下界 ${formatNumber(Math.ceil(preview.historicQuotaLowerBoundSeconds / 60))} 分鐘（不含回應延遲）。`;
    root.appendChild(summary);
    const limits = document.createElement('p');
    limits.className = 'small text-muted mb-2';
    limits.textContent = `${preview.daysWithTargets} 天有數值目標；另有 ${formatNumber(preview.stateChangeObjects)} 個狀態變更對象，table 分頁成本未知，故不估總 ETA。${preview.message}`;
    root.appendChild(limits);
    const metadata = document.createElement('p');
    metadata.className = 'small text-muted text-break mb-2';
    metadata.textContent = `預覽版本 ${preview.contractVersion} / ${preview.buildVersion}，有效至 ${formatDateTime(preview.expiresAtUtc)}；起算日 ${preview.planAnchorLocalDate.slice(0, 10)}。設定與範圍版本已綁定。`;
    root.appendChild(metadata);

    // 完成水位（docs/PRTG-SPEC.md §5）：先前已完整回填、目標沒變的日子預設略過
    if (preview.completedDays > 0) {
        const resume = document.createElement('div');
        resume.className = 'alert alert-info py-2 px-3 small mb-2';
        resume.setAttribute('role', 'status');
        const text = document.createElement('p');
        text.className = 'mb-1';
        text.textContent = `先前已完整回填 ${formatNumber(preview.completedDays)} 天，本次預設略過；剩餘約 ${formatNumber(preview.remainingHistoricRequests)} 次 historicdata。`;
        const check = document.createElement('div');
        check.className = 'form-check mb-0';
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.className = 'form-check-input';
        input.id = 'prtg-full-backfill-redo';
        input.checked = redoWas;
        input.addEventListener('change', () => syncFullBackfillStart(preview));
        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = '全部重做（忽略先前完成的日子，全部重新取數）';
        check.append(input, label);
        const hint = document.createElement('p');
        hint.className = 'mb-0 mt-1';
        hint.id = 'prtg-full-backfill-redo-hint';
        hint.hidden = true;
        hint.textContent = '全部日子都已完整回填；要重新取數請勾選「全部重做」。';
        resume.append(text, check, hint);
        root.appendChild(resume);
    }

    const details = document.createElement('details');
    const heading = document.createElement('summary');
    heading.textContent = '查看逐日工作量';
    details.appendChild(heading);
    const dayTableHost = document.createElement('div');
    dayTableHost.className = 'mt-2';
    renderTable(dayTableHost, {
        caption: '全量歷史回填逐日估算的觸發主機、歷史對應、選取主機與目標感測器數',
        columns: [
            { title: '日期', render: day => day.day.slice(0, 10) },
            { title: '觸發主機', render: day => formatNumber(day.triggeredHosts) },
            { title: '歷史對應主機', render: day => formatNumber(day.mappedHosts) },
            { title: '選取主機', render: day => formatNumber(day.selectedHosts) },
            { title: '目標 sensor', render: day => formatNumber(day.targetSensors) },
            { title: '本次', render: day => day.alreadyCompleted ? '已完成，略過' : '回填' }
        ],
        rows: preview.days || [],
        empty: { title: '沒有逐日資料', hint: '預覽範圍內沒有可回填的日期，請調整範圍後重新預估。', icon: null }
    });
    details.appendChild(dayTableHost); root.appendChild(details);
    syncFullBackfillStart(preview);
}

function bindFullBackfill() {
    const previewButton = document.getElementById('prtg-full-backfill-preview');
    const startButton = document.getElementById('prtg-full-backfill-start');
    const result = document.getElementById('prtg-full-backfill-preview-result');
    if (!previewButton || !startButton || !result) return;
    previewButton.addEventListener('click', async event => {
        const restore = withBusy(event.currentTarget, '預估中');
        fullBackfillPreview = null;
        startButton.disabled = true;
        try {
            fullBackfillPreview = await api.get('/api/admin/settings/prtg-backfill/preview', { silent: true });
            renderFullBackfillPreview(fullBackfillPreview);
        } catch (e) {
            result.textContent = `無法建立全量回填預覽：${e?.message || '請重新整理後重試。'}`;
        } finally { restore(); }
    });
    startButton.addEventListener('click', async event => {
        if (startButton.dataset.pending === 'true') return;
        const button = event.currentTarget;
        const preview = fullBackfillPreview;
        if (!preview?.previewId) { toast('請先取得有效的全量成本預覽。', 'warning'); return; }
        startButton.dataset.pending = 'true';
        const restore = withBusy(button, '等待確認');
        const redo = fullBackfillRedoChecked();
        const skipText = preview.completedDays > 0
            ? (redo ? `已勾選全部重做：先前完成的 ${formatNumber(preview.completedDays)} 天也會重新取數。`
                : `略過先前已完成的 ${formatNumber(preview.completedDays)} 天，剩餘約 ${formatNumber(preview.remainingHistoricRequests)} 次。`)
            : '';
        try {
            const confirmed = await confirmAction({
                title: '確認全量歷史回填',
                message: `全量依目前保存的觸發主機、歷史對應、sensor 白名單與監看數值範圍執行，不代表全站所有 sensor。${preview.fromDate.slice(0, 10)}～${preview.toDate.slice(0, 10)} 預估 ${formatNumber(preview.estimatedHistoricRequests)} 次 historicdata。${skipText}` +
                    `共享配額時間下界 ${Math.ceil(preview.historicQuotaLowerBoundSeconds / 60)} 分鐘，不含來源延遲；狀態變更另有 ${formatNumber(preview.stateChangeObjects)} 個對象，分頁成本未知。` +
                    '預覽五分鐘有效；設定、範圍、歷史紀錄或對應變更時必須重新預覽。',
                confirmText: '確認並開始', confirmVariant: 'danger'
            });
            if (!confirmed) return;
            button.querySelector('span')?.remove();
            button.replaceChildren(document.createTextNode('啟動中'));
            await api.post('/api/admin/settings/prtg-backfill/start', { previewId: preview.previewId, confirmed: true, resume: !redo }, { silent: true });
            fullBackfillPreview = null;
            result.replaceChildren(document.createTextNode('已啟動全量歷史回填。執行進度與停止控制位於 Runs 頁的 PRTG 狀態卡：'));
            const runsLink = document.createElement('a');
            runsLink.href = result.dataset.runsUrl || '/runs';
            runsLink.textContent = '查看進度／停止作業';
            result.appendChild(runsLink);
            toast('已開始全量歷史回填。', 'success');
        } catch (e) {
            fullBackfillPreview = null;
            result.textContent = `未啟動：${e?.message || '預覽失效，請重新預估並確認。'}`;
            toast(e?.message || '全量預覽已失效，請重新預估並確認。', 'danger');
        } finally {
            restore();
            delete startButton.dataset.pending;
            if (fullBackfillPreview) syncFullBackfillStart(fullBackfillPreview);
            else startButton.disabled = true;
        }
    });
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
    const authMode = settings.prtgAuthMode || 'token';
    const hasCredential = authMode === 'password'
        ? Boolean(settings.prtgHasPassword && settings.prtgUsername)
        : authMode === 'passhash'
            ? Boolean(settings.prtgHasPasshash && settings.prtgUsername)
            : Boolean(settings.prtgHasApiToken);
    prtgStructureConfigured = Boolean(settings.prtgUrl?.trim() && hasCredential);
    document.getElementById('prtg-enabled').checked = prtgEnabled;
    const scopeSelect = document.getElementById('prtg-value-fetch-scope');
    if (scopeSelect) {
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
    setupState.settingsLoading = true;
    renderSetupSummary();
    try {
        const settings = await api.get('/api/admin/settings');
        historyRetentionDays = settings.retentionDays;
        renderPrtgFields(settings);
        setupState.settings = settings;
    } catch (error) {
        setupState.settings = null;
        throw error;
    } finally {
        setupState.settingsLoading = false;
        renderSetupSummary();
    }
}

/**
 * 數值取數對象切換：只有「觸發主機＋指定清單」需要主機名稱輸入框；
 * 關閉擷取或保守策略時不用夜間數值範圍，一併藏起估算按鈕。
 * 用 classList 切換而非 style.display（同本頁認證方式切換的既有作法）。
 */
function syncScopeFields() {
    const scope = document.getElementById('prtg-value-fetch-scope')?.value ?? 'triggered';
    const off = !document.getElementById('prtg-enabled')?.checked;
    const conservative = document.getElementById('prtg-fetch-strategy')?.value !== 'aggressive';
    document.getElementById('prtg-value-fetch-extra-hosts-group')
        ?.classList.toggle('d-none', off || conservative || scope !== 'triggered-plus-list');
    document.getElementById('prtg-scope-estimate-btn')?.classList.toggle('d-none', off);
    if (off || conservative) {
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
    syncScopeFields();
}

/** 鏡像同步以已儲存的 PRTG 位址與認證為準；取樣停用不會阻擋手動更新結構。 */
function syncStructureSyncGate() {
    const btn = document.getElementById('prtg-structure-sync-btn');
    if (btn) btn.disabled = !prtgStructureConfigured || structureSyncRunning;
    document.getElementById('prtg-structure-sync-disabled-hint')
        ?.classList.toggle('d-none', prtgStructureConfigured);
}

function bindScopeControls() {
    document.getElementById('prtg-enabled')?.addEventListener('change', syncScopeFields);
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
            const strategy = document.getElementById('prtg-fetch-strategy')?.value ?? 'conservative';
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
                const batches = `每輪 ${formatNumber(res.snapshotBatchCount)} 批（每批最多 100 顆）`;
                if (res.snapshotWarning) {
                    snapshotResult.className = 'text-warning small d-block';
                    snapshotResult.textContent = `⚠ ${snapBase}，${batches}——${res.snapshotWarning}`;
                } else {
                    snapshotResult.className = 'text-muted small d-block';
                    snapshotResult.textContent = `${snapBase}，${batches}`;
                }
            }

            if (scope === 'triggered') {
                if (strategy !== 'aggressive') {
                    result.className = 'text-muted small';
                    result.textContent = '保守策略不執行夜間逐 sensor 取數；快照規模已列於上方。';
                    return;
                }
                result.className = 'text-muted small';
                result.textContent = '只抓觸發主機：數量逐日變動，事前無法估算。';
                return;
            }

            if (strategy !== 'aggressive') {
                result.className = 'text-muted small';
                result.textContent = '保守策略不執行夜間逐 sensor 取數；快照規模已列於上方。';
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
            const saved = setupState.settings;
            const matchesSaved = saved && url === saved.prtgUrl
                && (document.getElementById('prtg-auth-mode')?.value ?? 'token') === (saved.prtgAuthMode || 'token')
                && (document.getElementById('prtg-username')?.value.trim() ?? '') === (saved.prtgUsername ?? '')
                && !document.getElementById('prtg-api-token')?.value
                && !document.getElementById('prtg-password')?.value
                && !document.getElementById('prtg-passhash')?.value
                && !document.getElementById('prtg-clear-token')?.checked
                && !document.getElementById('prtg-clear-password')?.checked
                && !document.getElementById('prtg-clear-passhash')?.checked
                && Boolean(document.getElementById('prtg-ignore-ssl')?.checked) === Boolean(saved.prtgIgnoreSslErrors)
                && (Number(document.getElementById('prtg-timeout-seconds')?.value) || 60) === (saved.prtgTimeoutSeconds ?? 60);
            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.elapsedMs != null
                ? `${mark} ${result.message}（耗時 ${result.elapsedMs}ms）`
                : `${mark} ${result.message}`;
            if (result.success && !matchesSaved) resultEl.textContent += '；本次測試使用未儲存的表單值，請儲存後重測以確認實際執行設定。';
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = `✗ ${error?.message || '測試連線失敗。'}`;
        } finally {
            restore();
        }
    });
}

async function savePrtgSettings(payload) {
    try {
        return await api.put('/api/admin/settings/prtg', {
            ...payload, expectedRevision: setupState.settings?.revision
        });
    } catch (error) {
        if (error.status === 409 && await confirmAction({
            title: '設定已由其他作業更新',
            message: '本次沒有儲存，您的輸入仍保留。重新載入會以最新設定取代目前輸入，是否重新載入？',
            confirmText: '重新載入', cancelText: '保留目前輸入'
        })) await loadSettings();
        throw error;
    }
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

            await savePrtgSettings(payload);
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

            const scopeValue = document.getElementById('prtg-value-fetch-scope')?.value ?? 'triggered';
            const enabled = document.getElementById('prtg-enabled').checked;
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

            await savePrtgSettings(payload);
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
        snapText = '數值快照：本次啟動後尚無成功取樣';
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
        snapEl?.classList.add('text-warning');
    }
    if (data.snapshotPendingSamples > 0)
        snapText += `；等待寫入 ${formatNumber(data.snapshotPendingSamples)} 筆（含本小時樣本）`;
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
            // 與「最近一次排程驗收」同一個數（core/prtg-acceptance.js 看 databaseUsableValues）：照表自己數要與驗收段一致
            { title: '品質達標列／目標', render: row => `${formatNumber(row.databaseUsableValues)} / ${formatNumber(row.targets)}` },
            { title: '快照寫入', render: row => formatNumber(row.availableValues) },
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
        setupState.snapshotHours = response?.hours ?? [];
        renderSnapshotDiagnostics(setupState.snapshotHours);
    } catch {
        setupState.snapshotHours = null;
        if (host) renderError(host, { message: '快照診斷讀取失敗。請重新載入；不會呼叫 PRTG。', onRetry: loadSnapshotDiagnostics });
    }
    renderSetupSummary();
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
    const container = document.getElementById('prtg-mirror-conflicts-table');
    if (!container) return;
    renderMirrorTableLoading(container);
}

// 鏡像、衝突、未對應三塊清單一起重整（人工對應、IP 排除、批次指派後共用）
function refreshMirrorViews(conflictOptions) {
    return Promise.all([
        refreshPrtgMirror(),
        refreshConflicts(conflictPage, conflictOptions),
        refreshUnmatched(unmatchedPage)
    ]);
}


// 三張清單（衝突／未對應／IP 排除）載入失敗的共用呈現：只差標籤、重試動作與是否有分頁區
function renderListError(containerId, label, errorMessage, retry, paginationId) {
    const container = document.getElementById(containerId);
    if (!container) return;
    renderError(container, {
        message: `載入${label}失敗：${errorMessage || '網路或伺服器錯誤'}`,
        onRetry: retry
    });
    if (paginationId) document.getElementById(paginationId)?.replaceChildren();
}

function renderConflictsError(errorMessage) {
    renderListError('prtg-mirror-conflicts-table', '衝突清單', errorMessage, () => refreshConflicts(conflictPage), 'prtg-conflicts-pagination');
}

function renderUnmatchedError(errorMessage) {
    renderListError('prtg-unmatched-table', '未對應清單', errorMessage, () => refreshUnmatched(unmatchedPage), 'prtg-unmatched-pagination');
}

function renderIpExcludesError(errorMessage) {
    renderListError('prtg-ip-excludes-table', ' IP 排除清單', errorMessage, () => refreshIpExcludes());
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

/** 衝突清單表頭的全選 checkbox：每次渲染重建，id 維持不變供 updateConflictBatchBar 同步勾選狀態 */
function buildConflictSelectAll() {
    const selectAll = document.createElement('input');
    selectAll.type = 'checkbox';
    selectAll.className = 'form-check-input';
    selectAll.id = 'prtg-conflict-select-all';
    selectAll.title = '全選目前頁';
    selectAll.setAttribute('aria-label', '全選目前頁');
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
    return selectAll;
}

function renderConflicts(items) {
    const container = document.getElementById('prtg-mirror-conflicts-table');
    if (!container) return;

    renderTable(container, {
        columns: [
            {
                title: '選取',
                className: 'text-center lf-w-xxs',
                renderHeader: buildConflictSelectAll,
                render: item => {
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
                    return checkbox;
                }
            },
            { title: 'Device', className: 'lf-w-lg', truncate: true, render: mirrorDeviceText },
            { title: 'IP', className: 'lf-w-md font-monospace', render: item => item.ip || '-' },
            { title: '衝突型別', className: 'lf-w-md', render: item => mirrorKindBadge(item, false) },
            { title: '說明', className: 'text-muted', render: item => item.note || '-' },
            { title: '操作', className: 'lf-w-mdl', render: mirrorAssignExcludeActions }
        ],
        rows: items || [],
        empty: { title: '無衝突項目', hint: '目前沒有需要人工處理的衝突；同步後若出現同 IP 多裝置等情況會列在這裡。', icon: null }
    });
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

/** 鏡像清單表格的載入中佔位：spinner 置中，等待樣式與全站其他區塊一致（ui.js 的 renderSpinner） */
function renderMirrorTableLoading(container) {
    renderSpinner(container);
    container.firstElementChild.classList.add('justify-content-center', 'py-2');
}

/** 衝突／未對應清單共用的 Device 欄文字 */
function mirrorDeviceText(item) {
    return item.deviceName ? `${item.deviceObjid} ${item.deviceName}` : String(item.deviceObjid);
}

/** 衝突／未對應清單共用的型別徽章；includeUnmatched：未對應清單多一個「未對應」分支 */
function mirrorKindBadge(item, includeUnmatched) {
    const kindBadge = document.createElement('span');
    if (item.conflictKind === 'multi-device') {
        kindBadge.className = 'badge bg-warning text-dark';
        kindBadge.textContent = '同 IP 多裝置';
    } else if (item.conflictKind === 'multi-host') {
        kindBadge.className = 'badge bg-info text-dark';
        kindBadge.textContent = 'IP 對多主機';
    } else if (item.conflictKind === 'hostname-multi') {
        kindBadge.className = 'badge bg-warning text-dark';
        kindBadge.textContent = '名稱對多主機';
    } else if (item.conflictKind === 'hostname-host-taken') {
        kindBadge.className = 'badge bg-warning text-dark';
        kindBadge.textContent = '名稱命中已對應主機';
    } else if (includeUnmatched && (item.conflictKind === 'unmatched' || item.mapStatus === 'unmatched')) {
        kindBadge.className = 'badge bg-secondary';
        kindBadge.textContent = '未對應';
    } else {
        kindBadge.className = 'badge bg-secondary';
        kindBadge.textContent = item.conflictKind || '-';
    }
    return kindBadge;
}

/** 衝突／未對應清單共用的操作欄：指派＋排除此 IP */
function mirrorAssignExcludeActions(item) {
    const actions = document.createElement('span');

    const assignBtn = document.createElement('button');
    assignBtn.type = 'button';
    assignBtn.className = 'btn btn-sm btn-outline-primary py-0 text-nowrap';
    assignBtn.textContent = '指派';
    assignBtn.addEventListener('click', () => openAssignModal(item));
    actions.appendChild(assignBtn);

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
                const res = await api.put('/api/admin/settings/prtg-ip-excludes', { ip: item.ip, note: null, expectedScopeRevision: scopeRevision });
                toast('已排除此 IP', 'success');
                notifyRemapWarning(res);
                await refreshMirrorViews();
            } catch (error) {
                toast(error && error.message ? error.message : '排除失敗', 'danger');
            }
        });
    }
    actions.appendChild(excludeBtn);
    return actions;
}

function renderUnmatchedLoading() {
    const container = document.getElementById('prtg-unmatched-table');
    if (!container) return;
    renderMirrorTableLoading(container);
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
    const container = document.getElementById('prtg-unmatched-table');
    if (!container) return;

    renderTable(container, {
        columns: [
            { title: 'Device', className: 'lf-w-lg', truncate: true, render: mirrorDeviceText },
            { title: 'IP', className: 'lf-w-md font-monospace', render: item => item.ip || '-' },
            { title: '型別', className: 'lf-w-md', render: item => mirrorKindBadge(item, true) },
            { title: '說明', className: 'text-muted', render: item => item.note || '-' },
            { title: '操作', className: 'lf-w-mdl', render: mirrorAssignExcludeActions }
        ],
        rows: items || [],
        empty: { title: '無未對應項目', hint: '所有 PRTG device 都已對應主機；之後新增且未對應的 device 會列在這裡。', icon: null }
    });
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
    const container = document.getElementById('prtg-ip-excludes-table');
    if (!container) return;
    renderMirrorTableLoading(container);
}


async function refreshIpExcludes() {
    await refreshPrtgMirror();
}

function renderIpExcludes(items) {
    const container = document.getElementById('prtg-ip-excludes-table');
    if (!container) return;

    renderTable(container, {
        columns: [
            { title: 'IP', className: 'lf-w-md font-monospace', render: item => item.ip || '-' },
            { title: '備註', className: 'text-muted', render: item => item.note || '-' },
            { title: '建立者', className: 'lf-w-md', render: item => item.createdBy || '-' },
            { title: '建立時間', className: 'lf-w-mdl', render: item => (item.createdAt ? formatDateTime(item.createdAt) : '-') },
            {
                title: '操作',
                className: 'lf-w-sm',
                render: item => {
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
                            const res = await api.delete(`/api/admin/settings/prtg-ip-excludes/${encodeURIComponent(item.ip)}?expectedScopeRevision=${scopeRevision}`);
                            toast('已移除 IP 排除設定', 'success');
                            notifyRemapWarning(res);
                            await refreshMirrorViews();
                        } catch (error) {
                            toast(error && error.message ? error.message : '移除失敗', 'danger');
                        }
                    });
                    return removeBtn;
                }
            }
        ],
        rows: items || [],
        empty: { title: '無排除 IP', hint: '需要略過某個 IP 時，可從衝突或未對應清單點「排除此 IP」。', icon: null }
    });
}

function renderManualMaps(items) {
    const container = document.getElementById('prtg-mirror-manual-maps-table');
    if (!container) return;

    renderTable(container, {
        columns: [
            { title: 'Device Objid', className: 'lf-w-md font-monospace', render: item => String(item.deviceObjid) },
            { title: '主機', className: 'lf-w-lg', truncate: true, render: item => item.hostName || `Host ID: ${item.hostId}` },
            {
                title: '備註',
                className: 'text-muted',
                render: item => {
                    const note = document.createElement('span');
                    note.appendChild(document.createTextNode(item.note || '-'));
                    if (item.sameIpSkippedCount && item.sameIpSkippedCount > 0) {
                        const span = document.createElement('span');
                        span.className = 'text-muted ms-1';
                        span.textContent = `（同 IP 另有 ${item.sameIpSkippedCount} 台 device 已略過）`;
                        note.appendChild(span);
                    }
                    return note;
                }
            },
            { title: '建立者', className: 'lf-w-md', render: item => item.createdBy || '-' },
            {
                title: '操作',
                className: 'lf-w-sm',
                render: item => {
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
                            const res = await api.delete(`/api/admin/settings/prtg-manual-map/${item.deviceObjid}?expectedScopeRevision=${scopeRevision}`);
                            toast('已移除人工對應', 'success');
                            notifyRemapWarning(res);
                            await refreshMirrorViews();
                        } catch (error) {
                            toast(error && error.message ? error.message : '移除失敗', 'danger');
                        }
                    });
                    return removeBtn;
                }
            }
        ],
        rows: items || [],
        empty: { title: '無人工對應項目', hint: '需要手動指定主機時，可從衝突或未對應清單點「指派」。', icon: null }
    });
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

        hostSelect.replaceChildren(new Option('載入中…', ''));
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
                expectedScopeRevision: scopeRevision,
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
                refreshScopePurge()
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
    // 全選 checkbox 在表頭，隨每次 renderConflicts 重建並自帶 change 處理（buildConflictSelectAll）

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
                    expectedScopeRevision: scopeRevision,
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

                await refreshMirrorViews({ preserveSelection: true });
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

let scopeRefresh = null;
function refreshPrtgMirror() {
    if (scopeRefresh) return scopeRefresh;
    scopeRefresh = refreshPrtgMirrorCore().finally(() => { scopeRefresh = null; });
    return scopeRefresh;
}
async function refreshPrtgMirrorCore() {
    try {
        const before = await api.get('/api/admin/settings/prtg-scope-revision', { silent: true });
        const [mirrorData, manualMaps, excludes] = await Promise.all([
            api.get('/api/admin/settings/prtg-mirror', { silent: true }),
            api.get('/api/admin/settings/prtg-manual-map', { silent: true }),
            api.get('/api/admin/settings/prtg-ip-excludes', { silent: true })
        ]);
        const after = await api.get('/api/admin/settings/prtg-scope-revision', { silent: true });
        if (before !== after) throw new Error('載入期間 PRTG 範圍已被修改，請重新整理並核對；目前表單輸入保留。');
        scopeRevision = before;
        renderPrtgMirror(mirrorData);
        renderManualMaps(manualMaps);
        renderIpExcludes(excludes || []);
    } catch (error) {
        renderIpExcludesError(error?.message || '載入 PRTG 範圍失敗，請重新整理；目前表單輸入保留。');
    }
    const riskReviewEl = document.getElementById('prtg-risk-review');
    if (riskReviewEl) {
        const refreshRiskReview = async (page = 1) => {
            try {
                const result = await api.get('/api/prtg/risk-review', { silent: true });
                riskReviewEl.textContent = `歷史風險重評：尚未檢查 ${formatNumber(result.uncheckedRecords)} 筆、證據不足待重評 ${formatNumber(result.pendingReviewRecords)} 筆、已修訂 ${formatNumber(result.revisedRecords)} 筆。證據不足不會自動降風險；請從主機日詳情核對並明確重跑 NetIQ。`;
                if (result.pendingReviewRecords > 0) {
                    const pending = await api.get(`/api/prtg/risk-review/pending?page=${page}`);
                    const list = document.createElement('ul');
                    for (const record of pending.items) {
                        const row = document.createElement('li');
                        const link = document.createElement('a');
                        link.href = appUrl(`/records/${record.hostId}/${record.recordDate.slice(0, 10)}`);
                        link.textContent = `${record.hostName}｜${record.recordDate.slice(0, 10)}｜原等級 ${record.riskLevel}｜${record.detailPruned ? '詳情已精簡' : '待重評'}`;
                        row.append(link); list.append(row);
                    }
                    riskReviewEl.append(list);
                    const navigation = document.createElement('div');
                    navigation.append(document.createTextNode(`第 ${page} 頁，共 ${pending.total} 筆。`));
                    for (const [label, target, disabled] of [['上一頁', page - 1, page <= 1], ['下一頁', page + 1, page * 50 >= pending.total]]) {
                        const button = document.createElement('button'); button.type = 'button';
                        button.className = 'btn btn-sm btn-outline-secondary ms-2'; button.textContent = label; button.disabled = disabled;
                        button.addEventListener('click', () => refreshRiskReview(target)); navigation.append(button);
                    }
                    riskReviewEl.append(navigation);
                }
            } catch { riskReviewEl.textContent = '歷史風險重評狀態無法讀取，請重試。'; }
        };
        await refreshRiskReview();
        const retry = document.getElementById('prtg-risk-review-retry');
        if (retry) retry.onclick = async () => {
            const restore = withBusy(retry, '重評中');
            try { await api.post('/api/prtg/risk-review/retry', {}); await refreshRiskReview(); }
            catch (error) { toast(`重評失敗：${error.message}`, 'danger'); }
            finally { restore(); }
        };
    }
    await refreshScopePurge();
}

// ── 監看範圍外資料（預覽＋確認清除）────────────────────────────────────

let scopePurgeStorageKey = null;

function scopePurgeJobStorageKey(user) {
    const actor = user?.isServerAdmin
        ? `server-admin:${String(user.account || '').toLocaleLowerCase()}`
        : `user:${user?.userId ?? 'unknown'}`;
    return `lf-prtg-scope-purge:${actor}`;
}

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
    if (preview.previousJobId) {
        summaryEl.textContent += `；前次部分作業 ${preview.previousJobId.slice(0, 8)} 已刪除數值 ${formatNumber(preview.previousDeletedValues)} 筆、狀態變更 ${formatNumber(preview.previousDeletedStateChanges)} 筆，尚餘數值 ${formatNumber(preview.previousRemainingValues)} 筆、狀態變更 ${formatNumber(preview.previousRemainingStateChanges)} 筆（已由伺服器連結）`;
    }
    const names = Array.isArray(preview.topDeviceNames) ? preview.topDeviceNames : [];
    const affectedDevices = preview.detailsRestricted
        ? preview.visibleAffectedDevices
        : preview.affectedDevices;
    devicesEl.textContent = names.length > 0
        ? `裝置：${names.join('、')}${affectedDevices > names.length ? ' 等' : ''}`
        : '';
    btn.dataset.previewId = preview.previewId || '';
    btn.dataset.expiresAtUtc = preview.expiresAtUtc || '';
    const pendingJob = scopePurgeStorageKey && sessionStorage.getItem(`${scopePurgeStorageKey}:job-id`);
    const pendingPreview = scopePurgeStorageKey && sessionStorage.getItem(`${scopePurgeStorageKey}:preview-id`);
    btn.textContent = pendingJob || pendingPreview ? '檢查／繼續清除' : '確認清除';
    btn.disabled = pendingJob || pendingPreview ? false : (preview.values + preview.stateChanges === 0 || !btn.dataset.previewId);
}

async function refreshScopePurge() {
    try {
        scopePurgeStorageKey = scopePurgeJobStorageKey(await getCurrentUser());
        renderScopePurge(await api.get('/api/admin/settings/prtg-scope-purge/preview', { silent: true }));
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function bindScopePurge() {
    const btn = document.getElementById('prtg-scope-purge-btn');
    btn?.addEventListener('click', async () => {
        if (!scopePurgeStorageKey) scopePurgeStorageKey = scopePurgeJobStorageKey(await getCurrentUser());
        const jobStorageKey = `${scopePurgeStorageKey}:job-id`;
        const previewStorageKey = `${scopePurgeStorageKey}:preview-id`;
        const expiryStorageKey = `${scopePurgeStorageKey}:preview-expires`;
        const savedJobId = sessionStorage.getItem(jobStorageKey);
        const savedPreviewId = sessionStorage.getItem(previewStorageKey);
        const confirmed = await confirmAction({
            message: savedJobId || savedPreviewId
                ? '要繼續這個已接受的範圍外清除作業嗎？只會處理原預覽時的資料集合。'
                : '確定要清除監看範圍外的 PRTG 數值與狀態變更嗎？刪除後無法復原（重新納入監看的裝置只能靠回填補回保留期內的資料）。'
        });
        if (!confirmed) return;
        const restore = withBusy(btn, '清除中');
        try {
            let job;
            if (savedJobId) {
                job = await api.get(`/api/admin/settings/prtg-scope-purge/jobs/${encodeURIComponent(savedJobId)}`);
                if (job.status === 'paused') {
                    // A paused job advances only after a fresh, explicit user action.
                    job = await api.post(`/api/admin/settings/prtg-scope-purge/jobs/${encodeURIComponent(job.jobId)}/resume`,
                        { expectedRevision: job.revision });
                }
            } else {
                const previewId = savedPreviewId || btn.dataset.previewId;
                const expiresAtUtc = savedPreviewId
                    ? sessionStorage.getItem(expiryStorageKey)
                    : btn.dataset.expiresAtUtc;
                // Once POST may have been attempted, the same preview ID is an idempotency key.
                // Retry it even after local expiry so an accepted response lost in transit can be recovered.
                if (!previewId || (!savedPreviewId && (!expiresAtUtc || Date.parse(expiresAtUtc) <= Date.now()))) {
                    sessionStorage.removeItem(previewStorageKey);
                    sessionStorage.removeItem(expiryStorageKey);
                    throw new Error('預覽已逾期，請重新預覽後再確認。');
                }
                if (!savedPreviewId) {
                    sessionStorage.setItem(previewStorageKey, previewId);
                    sessionStorage.setItem(expiryStorageKey, expiresAtUtc);
                }
                job = await api.post('/api/admin/settings/prtg-scope-purge/confirm', { previewId });
                sessionStorage.removeItem(previewStorageKey);
                sessionStorage.removeItem(expiryStorageKey);
                sessionStorage.setItem(jobStorageKey, job.jobId);
            }
            while (job.status === 'accepted' || job.status === 'running') {
                job = await api.post(`/api/admin/settings/prtg-scope-purge/jobs/${encodeURIComponent(job.jobId)}/resume`,
                    { expectedRevision: job.revision });
                if (!job.countsReconciled) {
                    const summaryEl = document.getElementById('prtg-scope-purge-summary');
                    if (summaryEl) summaryEl.textContent = `清除進行中：本作業已刪數值 ${formatNumber(job.values)} 筆、狀態變更 ${formatNumber(job.stateChanges)} 筆；預估尚餘數值 ${formatNumber(job.remainingValues)} 筆、狀態變更 ${formatNumber(job.remainingStateChanges)} 筆。其他清理的影響會在安全暫停或完成時核對。`;
                }
            }
            if (job.status === 'complete') {
                sessionStorage.removeItem(jobStorageKey);
                toast(`清除完成：本作業刪除數值 ${formatNumber(job.values)} 筆、狀態變更 ${formatNumber(job.stateChanges)} 筆`, 'success');
            } else if (job.status === 'stopped') {
                sessionStorage.setItem(`${scopePurgeStorageKey}:stopped-job-id`, job.jobId);
                sessionStorage.removeItem(jobStorageKey);
                toast(`清除已停止（${job.stopReason || '範圍已變更'}）；可重新預覽，前次部分進度仍保留。`, 'warning');
            } else {
                toast(`清除已暫停（${job.stopReason || job.status}）；確認目前權限與範圍後，再明確選擇繼續。`, 'warning');
            }
        } catch (error) {
            if (error?.name === 'ApiError' && ['validation_failed', 'conflict'].includes(error.code)) {
                // These business responses mean the server rejected this operation. Auth/read
                // failures and transport errors keep the durable preview key for safe recovery.
                sessionStorage.removeItem(previewStorageKey);
                sessionStorage.removeItem(expiryStorageKey);
            } else if (error instanceof Error && error.name !== 'ApiError') {
                toast(`範圍外清除未完成：${error.message}`, 'danger');
            }
            // A request with unknown outcome keeps its preview/job key for idempotent recovery.
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
                refreshScopePurge()
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

function renderPrtgProbeStatus(status) {
    setupState.probe = status.isRunning ? null : (status.completedAt ? status.success === true && !status.cancelled : null);
    renderSetupSummary();
    const outputEl = document.getElementById('prtg-probe-output');
    const copyButton = document.getElementById('prtg-probe-copy');
    const downloadButton = document.getElementById('prtg-probe-download');
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
    if (downloadButton) downloadButton.disabled = !outputText || status.isRunning;

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
        setupState.probe = null;
        renderSetupSummary();
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
    const downloadButton = document.getElementById('prtg-probe-download');
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
    downloadButton?.addEventListener('click', () => {
        if (!outputEl.value || downloadButton.disabled) return;
        const blob = new Blob([outputEl.value + '\n'], { type: 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = `prtg-probe-${new Date().toISOString().replace(/[:.]/g, '-')}.txt`;
        anchor.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
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
        setupState.schedule = status.scheduleEnabled === true;
        setupState.scheduleStatus = status;
        renderSetupSummary();
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
        setupState.schedule = null;
        setupState.scheduleStatus = null;
        renderSetupSummary();
        renderError(banner, { message: '無法確認排程是否啟用。', onRetry: refreshScheduleWarning });
    }
}

// ── PRTG 資料搬運（任務G）──────────────────────────────────────────────

function bindPrtgDataTransfer() {
    const exportBtn = document.getElementById('prtg-export-btn');
    const importBtn = document.getElementById('prtg-import-btn');
    const importFile = document.getElementById('prtg-import-file');
    const importResult = document.getElementById('prtg-import-result');
    const cancelBtn = document.getElementById('prtg-import-cancel-btn');
    const statusBtn = document.getElementById('prtg-import-status-btn');
    const abandonBtn = document.getElementById('prtg-import-abandon-btn');
    const forgetBtn = document.getElementById('prtg-import-forget-btn');
    let activeController = null;
    let operationBusy = false;
    let pending = null;
    let storageKey = null;
    const show = (message, style = 'muted') => {
        if (!importResult) return;
        importResult.className = `small mb-2 text-${style}`;
        importResult.textContent = message;
    };
    const savePending = value => {
        pending = value;
        try {
            if (storageKey) value ? localStorage.setItem(storageKey, JSON.stringify(value)) : localStorage.removeItem(storageKey);
        } catch { /* 儲存空間不可用時本頁仍可續傳，識別碼持續顯示。 */ }
        if (statusBtn) statusBtn.disabled = operationBusy || !pending;
        if (abandonBtn) abandonBtn.disabled = operationBusy || !pending;
        if (forgetBtn) forgetBtn.disabled = operationBusy || !pending;
        if (importBtn) importBtn.disabled = operationBusy;
    };
    const loadPending = async () => {
        const user = await getCurrentUser();
        storageKey = `lf-prtg-diagnostic:${appUrl('/')}:${user.userId}:${user.isServerAdmin}:${user.account}`;
        if (!pending) {
            try {
                const stored = JSON.parse(localStorage.getItem(storageKey) || 'null');
                if (stored && /^[0-9a-f-]{36}$/i.test(stored.transferId) &&
                    /^[0-9a-f]{64}$/i.test(stored.packageSha256) && Number.isSafeInteger(stored.declaredBytes) && stored.declaredBytes > 0)
                    pending = stored;
            } catch { /* 無效的本機紀錄不授予傳輸資格。 */ }
        }
        savePending(pending);
    };
    const pendingLoaded = loadPending().then(() => {
        if (pending) show(`保留中的傳輸 ${pending.transferId}；選取原檔可續傳，或查詢狀態及明確放棄。`);
    }).catch(error => show(error.message, 'danger'));
    cancelBtn?.addEventListener('click', () => activeController?.abort());
    statusBtn?.addEventListener('click', async () => {
        if (!pending || operationBusy) return;
        operationBusy = true;
        const transferId = pending.transferId;
        savePending(pending);
        try {
            const status = await getDiagnosticTransfer(transferId);
            const names = { receiving: '接收中，可續傳', validating: '驗證中', 'validation-failed': '驗證失敗', complete: '已完成', abandoned: '已放棄' };
            show(`${names[status.state] || '未知狀態'}；已接受 ${formatNumber(status.receivedBytes)} / ${formatNumber(status.declaredBytes)} 位元組。${status.failureCode ? `原因 ${status.failureCode}。` : ''}識別碼 ${status.transferId}`,
                status.state === 'complete' ? 'success' : status.failureCode ? 'danger' : 'muted');
            if (status.state === 'complete' || status.state === 'abandoned') savePending(null);
        } catch (error) { show(`${error.message}。若來源或帳號已變更，可清除此頁續傳紀錄後重新匯入。`, 'danger'); }
        finally { operationBusy = false; savePending(pending); }
    });
    abandonBtn?.addEventListener('click', async () => {
        if (!pending || operationBusy) return;
        operationBusy = true;
        const transferId = pending.transferId;
        savePending(pending);
        try {
            if (!await confirmAction({ title: '放棄診斷傳輸', message: `將停止保留中的傳輸 ${transferId}，之後需重新匯入原檔。`, confirmText: '放棄傳輸' })) return;
            const status = await abandonDiagnosticTransfer(transferId);
            show(`傳輸 ${status.transferId} 已放棄，隔離片段將依保留期限清理。`);
            savePending(null);
        } catch (error) { show(error.message, 'danger'); }
        finally { operationBusy = false; savePending(pending); }
    });
    forgetBtn?.addEventListener('click', async () => {
        if (!pending || operationBusy) return;
        operationBusy = true;
        const transferId = pending.transferId;
        savePending(pending);
        try {
            if (!await confirmAction({ title: '清除此頁續傳紀錄', message: `只清除此瀏覽器的傳輸 ${transferId} 續傳紀錄。伺服器上的診斷片段仍保留；若要停止保留，請使用「放棄保留傳輸」。`, confirmText: '清除續傳紀錄' })) return;
            savePending(null);
            show(`已清除此頁的傳輸 ${transferId} 續傳紀錄，可重新選檔匯入。伺服器片段仍保留。`);
        } catch (error) { show(error.message, 'danger'); }
        finally { operationBusy = false; savePending(pending); }
    });

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

        if (operationBusy) return;
        operationBusy = true;
        activeController = new AbortController();
        savePending(pending);
        const restore = withBusy(importBtn, '匯入中');
        try {
            await pendingLoaded;
            if (cancelBtn) cancelBtn.disabled = false;
            savePending(pending);
            const data = await uploadDiagnosticFile(file, {
                signal: activeController.signal, resume: pending,
                onSession: savePending,
                progress: (phase, done, total) => {
                    const labels = { hashing: '計算檔案指紋', uploading: '傳送診斷片段', validating: '驗證完整性' };
                    show(`${labels[phase]}：${formatNumber(done)} / ${formatNumber(total)} 位元組${pending ? `；識別碼 ${pending.transferId}` : ''}`);
                }
            });
            show(`診斷包已隔離保存，正式監控資料與判定未變更。識別碼 ${data.transferId}；${formatNumber(data.receivedBytes)} 位元組已通過完整性驗證。`, 'success');
            toast('診斷包已隔離保存', 'success');
            savePending(null);
            if (importFile) importFile.value = '';
        } catch (error) {
            show(error.name === 'AbortError' ? '本次操作已取消；已接受的片段保留，選取原檔可續傳。'
                : `匯入未完成：${error?.message || '未知錯誤'}${pending ? `。傳輸 ${pending.transferId} 保留，可查詢狀態後續傳。` : ''}`, error.name === 'AbortError' ? 'muted' : 'danger');
        } finally {
            activeController = null;
            operationBusy = false;
            if (cancelBtn) cancelBtn.disabled = true;
            restore();
            savePending(pending);
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
    setupState.sync = status;
    renderSetupSummary();
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
            `上次同步：${when}${sourceLabel}　監看感測器 ${status.lastSensors ?? 0}；${prtgHostMapSummary(status)}`;
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
        setupState.sync = null;
        renderSetupSummary();
    }
}

function bindStructureSync() {
    const btn = document.getElementById('prtg-structure-sync-btn');
    btn?.addEventListener('click', async () => {
        // 按鈕依已儲存的連線設定灰掉；這裡再防一次載入與操作之間的競態。
        if (!prtgStructureConfigured) {
            toast('請先儲存 PRTG 位址與認證，再同步結構與對應。', 'warning');
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
    setupState.rules = status != null && Array.isArray(rules) ? !noPrtgRules && !hasUpdate : null;
    renderSetupSummary();
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
    bindFullBackfill();
    bindSelectedBackfill();
    bindPrtgEffectiveness();
    getCurrentUser().then(user => {
        if (hasCapability(user, 'Maintain')) {
            document.getElementById('prtg-data-transfer-advanced')?.classList.remove('d-none');
        }
    }).catch(() => {});
    bindPrtgTest();
    bindPrtgMirror();
    bindScopePurge();
    bindPrtgProbe();
    bindProbeWhitelistFill();
    bindAssignForm();
    bindPrtgDataTransfer();
    bindConnectionForm();
    bindParamsForm();
    bindScopeControls();
    // 監看範圍是可選縮圈，日常用不到：放在擷取參數頁籤、預設收合並記住使用者的選擇
    bindRememberedCollapse(document.getElementById('prtg-monitoring-body'),
        document.getElementById('prtg-monitoring-toggle'), 'prtg-monitoring');
    bindStructureSync();
    bindConflictBatchControls();
    bindUnmatchedControls();
    initCalibration();
    initValueReadiness();
    loadSettings();
    ensureBatchHostsLoaded();
    refreshPrtgMirror();
    loadSnapshotDiagnostics();
    refreshAcceptanceAi();
    refreshConflicts(1);
    refreshUnmatched(1);
    refreshIpExcludes();
    refreshPrtgProbeStatus();
    refreshStructureSyncStatus();
    refreshPrtgRuleBanner();
    refreshScheduleWarning();
}

init();

// 管理者確認來源及有限試點；後端另檢查資格、範圍與版本。
const monitoringForm = document.getElementById('prtg-monitoring-form');
if (monitoringForm) {
    let monitoring = null;
    const status = document.getElementById('prtg-monitoring-status');
    const hostSelect = document.getElementById('prtg-monitoring-hosts');
    const sensorBox = document.getElementById('prtg-monitoring-sensors');
    const selectedSensorBox = document.getElementById('prtg-monitoring-selected-sensors');
    const savedHostBox = document.getElementById('prtg-monitoring-saved-hosts');
    const savedHostSearch = document.getElementById('prtg-monitoring-saved-host-search');
    const selectedSensorSearch = document.getElementById('prtg-monitoring-selected-sensor-search');
    const hostSearch = document.getElementById('prtg-monitoring-host-search');
    const sensorSearch = document.getElementById('prtg-monitoring-sensor-search');
    const hostBatchStatus = document.getElementById('prtg-monitoring-host-batch-status');
    const sensorBatchStatus = document.getElementById('prtg-monitoring-sensor-batch-status');
    const saveButton = monitoringForm.querySelector('button[type="submit"]');
    const hostSelection = new Set();
    const sensorSelection = new Set();
    let hostCursors = [null], hostPageIndex = 0, hostPage = null;
    let sensorCursors = [null], sensorPageIndex = 0, sensorPage = null;
    let savedHostRows = [], savedHostPageIndex = 0, selectedSensorPageIndex = 0;
    let estimateToken = null, estimateFingerprint = null, baselineFingerprint = null;
    let draftVersion = 0, estimateSequence = 0;
    let hostPageSequence = 0, sensorPageSequence = 0, monitoringLoadSequence = 0, previewSequence = 0;
    let hostPageController = null, sensorPageController = null;
    let hostSearchTimer = null, sensorSearchTimer = null;
    let selectionBatchSequence = 0, activeSelectionBatch = null;
    const selectionBatchTimeoutMs = 5 * 60 * 1000;
    function sortedIds(values) { return [...values].map(Number).filter(Number.isSafeInteger).sort((a, b) => a - b); }
    function scopeFingerprint(request) {
        return JSON.stringify([request.revision, request.catalogueToken,
            sortedIds(new Set(request.hostIds)), sortedIds(new Set(request.sensorIds))]);
    }
    function cancelMonitoringPageRequest(kind, clearSearchTimer = true) {
        if (kind === 'host') {
            hostPageSequence++;
            hostPageController?.abort();
            hostPageController = null;
            if (clearSearchTimer) { clearTimeout(hostSearchTimer); hostSearchTimer = null; }
        } else {
            sensorPageSequence++;
            sensorPageController?.abort();
            sensorPageController = null;
            if (clearSearchTimer) { clearTimeout(sensorSearchTimer); sensorSearchTimer = null; }
        }
    }
    function invalidateEstimate() {
        draftVersion++; estimateSequence++;
        if (activeSelectionBatch) cancelSelectionBatch(activeSelectionBatch.kind, '範圍草稿已變更，批次已拒絕套用');
        cancelMonitoringPageRequest('host', false); cancelMonitoringPageRequest('sensor', false);
        estimateToken = null; estimateFingerprint = null;
        document.getElementById('prtg-monitoring-estimate-result').textContent = '範圍已變更，請重新估算。';
        updateSaveButton();
    }
    function updateSaveButton() {
        saveButton.disabled = !monitoring?.canEdit || !estimateToken || estimateFingerprint !== scopeFingerprint(monitoringRequest());
    }
    function monitoringRequest() {
        return { revision: monitoring.revision,
            hostIds: sortedIds(hostSelection), sensorIds: sortedIds(sensorSelection),
            catalogueToken: monitoring.catalogueToken, estimateToken };
    }
    async function estimateScope() {
        const request = monitoringRequest();
        const requestFingerprint = scopeFingerprint(request);
        const requestVersion = draftVersion;
        const sequence = ++estimateSequence;
        const estimate = await api.post('/api/prtg/monitoring/estimate', request);
        if (sequence !== estimateSequence || requestVersion !== draftVersion ||
            requestFingerprint !== scopeFingerprint(monitoringRequest())) return null;
        estimateToken = estimate.estimateToken;
        estimateFingerprint = requestFingerprint;
        document.getElementById('prtg-monitoring-estimate-result').textContent =
            `已估算 ${estimate.hostCount} 台主機、${estimate.selectedSensorCount} 顆已選 sensor；候選 ${estimate.availableSensorCount} 顆。` +
            `最低工作量 ${estimate.minimumQueryWork} 次查詢工作。${estimate.queryCost}`;
        updateSaveButton();
        return estimate;
    }
    document.getElementById('prtg-monitoring-estimate').addEventListener('click', async () => {
        if (!monitoring) return;
        const requestVersion = draftVersion;
        const sequence = estimateSequence + 1;
        try { await estimateScope(); }
        catch (error) {
            if (requestVersion === draftVersion && sequence === estimateSequence) {
                estimateToken = null; updateSaveButton(); document.getElementById('prtg-monitoring-estimate-result').textContent = `估算遭拒：${error.message}`;
            }
        }
    });
    let previewOffset = 0;
    async function previewScope(reset = false) {
        if (reset) previewOffset = 0;
        const requestOffset = previewOffset, sequence = ++previewSequence;
        const panel = document.getElementById('prtg-monitoring-preview-result');
        try {
            const data = await api.get(`/api/prtg/monitoring/preview?offset=${requestOffset}&limit=100`);
            if (sequence !== previewSequence || requestOffset !== previewOffset) return null;
            panel.replaceChildren();
            const summary = document.createElement('p');
            summary.textContent = `${data.day.slice(0, 10)}｜設定 ${data.settingsRevision}｜試點 ${data.policyRevision}｜` +
                `顯示 ${data.offset + 1}–${data.offset + data.rows.length}／${data.total}。${data.limitations}`;
            panel.append(summary);
            const list = document.createElement('ul');
            for (const row of data.rows) {
                const item = document.createElement('li');
                item.textContent = `${row.hostName || '未歸戶'}｜${row.sensorName} (#${row.objid})｜` +
                    `評估設定：${row.configuredForEvaluation ? '允許' : '排除'}；規則：${row.ruleIds.join('、') || '無'}；` +
                    `${row.disposition}；${row.notification}；${row.reasons.join('；') || '仍以正式當輪可信資料為準'}`;
                list.append(item);
            }
            panel.append(list);
            if (previewOffset > 0 || data.hasMore) {
                for (const [text, next, allowed] of [['上一頁', previewOffset - 100, previewOffset > 0], ['下一頁', previewOffset + 100, data.hasMore]]) {
                    const button = document.createElement('button'); button.type = 'button'; button.className = 'btn btn-sm btn-outline-secondary me-2';
                    button.textContent = text; button.disabled = !allowed;
                    button.addEventListener('click', () => { previewOffset = next; previewScope(); }); panel.append(button);
                }
            }
        } catch (error) {
            if (sequence === previewSequence && requestOffset === previewOffset)
                panel.textContent = `範圍預覽失敗：${error.message}`;
        }
    }
    document.getElementById('prtg-monitoring-preview').addEventListener('click', () => previewScope(true));
    function sameIdSet(left, right) { return left.size === right.size && [...left].every(id => right.has(id)); }
    function commitHostSelection(next) {
        if (next.size > 3000) {
            hostBatchStatus.textContent = '主機選取超過 3,000 台上限；未變更選取。';
            renderMonitoringHosts();
            return null;
        }
        if (sameIdSet(hostSelection, next)) return 0;
        const added = [...next].filter(id => !hostSelection.has(id)).length;
        hostSelection.clear(); next.forEach(id => hostSelection.add(id));
        invalidateEstimate(); renderMonitoringHosts();
        sensorPageSequence++; resetMonitoringSensorPages(); loadMonitoringSensorPage().catch(showSensorError);
        return added;
    }
    function commitSensorSelection(next) {
        if (next.size > 15000) {
            sensorBatchStatus.textContent = 'sensor 選取超過 15,000 顆上限；未變更選取。';
            renderMonitoringSensors();
            return null;
        }
        if (sameIdSet(sensorSelection, next)) return 0;
        const added = [...next].filter(id => !sensorSelection.has(id)).length;
        sensorSelection.clear(); next.forEach(id => sensorSelection.add(id));
        invalidateEstimate(); renderMonitoringSensors();
        return added;
    }
    function applyCurrentPageSelection(kind, select) {
        if (!monitoring?.canEdit) return;
        const isHost = kind === 'host';
        const rows = isHost ? hostPage?.rows || [] : sensorPage?.rows || [];
        const current = isHost ? hostSelection : sensorSelection;
        const maximum = isHost ? 3000 : 15000;
        const proposed = new Set(current);
        for (const row of rows) {
            const id = Number(isHost ? row.hostId : row.sensorId);
            if (!Number.isSafeInteger(id) || id <= 0) {
                (isHost ? hostBatchStatus : sensorBatchStatus).textContent = '本頁含無效 ID；未變更任何選取。';
                return;
            }
            if (select) proposed.add(id); else proposed.delete(id);
        }
        if (proposed.size > maximum) {
            (isHost ? hostBatchStatus : sensorBatchStatus).textContent =
                `本頁操作會超過 ${formatNumber(maximum)} 筆選取上限；未變更任何選取。`;
            return;
        }
        const added = isHost ? commitHostSelection(proposed) : commitSensorSelection(proposed);
        (isHost ? hostBatchStatus : sensorBatchStatus).textContent = select
            ? `已加入本頁 ${added} 筆；目前選取 ${proposed.size} 筆。`
            : `已取消本頁選取；目前選取 ${proposed.size} 筆。`;
    }
    function updateSelectionBatchUi() {
        const busyKind = activeSelectionBatch?.kind ?? null;
        for (const kind of ['host', 'sensor']) {
            const prefix = `prtg-monitoring-${kind}`;
            for (const suffix of ['select-page', 'clear-page', 'select-search'])
                document.getElementById(`${prefix}-${suffix}`).disabled = busyKind !== null;
            const cancel = document.getElementById(`${prefix}-select-cancel`);
            const isCurrent = busyKind === kind;
            cancel.classList.toggle('d-none', !isCurrent);
            cancel.disabled = !isCurrent;
        }
    }
    function selectionBatchCurrent(batch) {
        const search = batch.kind === 'host' ? hostSearch.value : sensorSearch.value;
        const selectedHosts = batch.kind === 'sensor' ? sortedIds(hostSelection) : null;
        return activeSelectionBatch === batch && batch.sequence === selectionBatchSequence &&
            batch.draftVersion === draftVersion && monitoring?.revision === batch.revision &&
            monitoring?.catalogueToken === batch.catalogueToken && search === batch.search &&
            JSON.stringify(selectedHosts) === JSON.stringify(batch.selectedHosts);
    }
    function cancelSelectionBatch(kind, reason = '已由使用者停止批次') {
        const batch = activeSelectionBatch;
        if (!batch || batch.kind !== kind) return;
        selectionBatchSequence++;
        activeSelectionBatch = null;
        if (batch.timeoutTimer) clearTimeout(batch.timeoutTimer);
        batch.controller.abort();
        updateSelectionBatchUi();
        (kind === 'host' ? hostBatchStatus : sensorBatchStatus).textContent = `${reason}；選取保持不變。`;
    }
    async function addCurrentSearchResults(kind) {
        if (!monitoring?.canEdit || activeSelectionBatch) return;
        const isHost = kind === 'host';
        const searchInput = isHost ? hostSearch : sensorSearch;
        const statusBox = isHost ? hostBatchStatus : sensorBatchStatus;
        const maximum = isHost ? 3000 : 15000;
        const batch = {
            kind, sequence: ++selectionBatchSequence, draftVersion,
            revision: monitoring.revision, catalogueToken: monitoring.catalogueToken,
            search: searchInput.value, selectedHosts: isHost ? null : sortedIds(hostSelection),
            controller: new AbortController(), timeoutTimer: null, timedOut: false
        };
        activeSelectionBatch = batch;
        updateSelectionBatchUi();
        statusBox.textContent = '正在逐頁讀取目前搜尋結果；最多 5 分鐘，可隨時停止。選取尚未變更…';
        batch.timeoutTimer = setTimeout(() => {
            if (activeSelectionBatch !== batch) return;
            batch.timedOut = true;
            batch.controller.abort();
        }, selectionBatchTimeoutMs);
        try {
            const collected = new Set();
            let cursor = null, expectedTotal = null, pageNumber = 0;
            do {
                if (!selectionBatchCurrent(batch)) return;
                let page;
                if (isHost) {
                    const query = new URLSearchParams({ search: batch.search, catalogueToken: batch.catalogueToken });
                    if (cursor) query.set('cursor', cursor);
                    page = await api.get(`/api/prtg/monitoring/hosts?${query.toString()}`, {
                        signal: batch.controller.signal, timeoutMs: 45000, silent: true
                    });
                } else {
                    page = await api.readOnlyPost('/api/prtg/monitoring/sensors', {
                        catalogueToken: batch.catalogueToken, cursor, search: batch.search,
                        hostIds: batch.selectedHosts, offset: pageNumber * 100
                    }, { signal: batch.controller.signal, timeoutMs: 45000, silent: true });
                }
                if (!selectionBatchCurrent(batch)) return;
                if (!page || !Array.isArray(page.rows) || page.rows.length > 100 || !Number.isSafeInteger(page.total) || page.total < 0)
                    throw new Error('目錄回傳格式無效，未變更選取。');
                if (expectedTotal === null) {
                    expectedTotal = page.total;
                    if (expectedTotal > maximum)
                        throw new Error(`目前搜尋結果有 ${formatNumber(expectedTotal)} 筆，超過 ${formatNumber(maximum)} 筆批次選取上限；請縮小搜尋，未變更選取。`);
                } else if (page.total !== expectedTotal) {
                    throw new Error('目錄總數在批次讀取期間變更；已拒絕整批，未變更選取。');
                }
                if (pageNumber > Math.ceil(maximum / 100) || (page.rows.length === 0 && page.nextCursor))
                    throw new Error('目錄分頁沒有前進；已停止批次，未變更選取。');
                for (const row of page.rows) {
                    const id = Number(isHost ? row.hostId : row.sensorId);
                    if (!Number.isSafeInteger(id) || id <= 0)
                        throw new Error('目錄含無效 ID；已拒絕整批，未變更選取。');
                    collected.add(id);
                }
                pageNumber++;
                cursor = page.nextCursor || null;
                if (cursor && pageNumber >= Math.ceil(maximum / 100))
                    throw new Error(`搜尋結果分頁超過 ${formatNumber(maximum)} 筆批次上限；請縮小搜尋，未變更選取。`);
                statusBox.textContent = `已讀取 ${formatNumber(Math.min(collected.size, expectedTotal))}／${formatNumber(expectedTotal)} 筆；選取尚未變更。`;
            } while (cursor);

            if (!selectionBatchCurrent(batch)) return;
            if (collected.size !== expectedTotal)
                throw new Error(`目錄只完整讀到 ${collected.size}／${expectedTotal} 筆；已拒絕部分結果，未變更選取。`);
            const current = isHost ? hostSelection : sensorSelection;
            const proposed = new Set(current);
            collected.forEach(id => proposed.add(id));
            if (proposed.size > maximum)
                throw new Error(`加入搜尋結果後會超過 ${formatNumber(maximum)} 筆選取上限；已拒絕整批，未變更選取。`);
            activeSelectionBatch = null;
            updateSelectionBatchUi();
            const added = isHost ? commitHostSelection(proposed) : commitSensorSelection(proposed);
            statusBox.textContent = `已完整加入搜尋結果 ${formatNumber(added)} 筆；目前選取 ${formatNumber(proposed.size)} 筆。`;
        } catch (error) {
            if (selectionBatchCurrent(batch)) statusBox.textContent = batch.timedOut
                ? '批次超過 5 分鐘整體時間上限，已拒絕套用；選取保持不變。'
                : `批次未套用：${error.message} 選取保持不變。`;
        } finally {
            if (activeSelectionBatch === batch) {
                activeSelectionBatch = null;
                if (batch.timeoutTimer) clearTimeout(batch.timeoutTimer);
                updateSelectionBatchUi();
            }
        }
    }
    function renderMonitoringHosts() {
        hostSelect.replaceChildren();
        for (const host of hostPage?.rows || []) {
            const label = document.createElement('label'); label.className = 'form-check d-block';
            const check = document.createElement('input'); check.type = 'checkbox'; check.className = 'form-check-input';
            check.value = String(host.hostId); check.checked = hostSelection.has(Number(host.hostId));
            check.addEventListener('change', () => {
                const id = Number(check.value);
                const proposed = new Set(hostSelection);
                if (check.checked) proposed.add(id); else proposed.delete(id);
                commitHostSelection(proposed);
            });
            label.append(check, document.createTextNode(`${host.hostName} (#${host.hostId})`)); hostSelect.append(label);
        }
        const offset = hostPageIndex * 100;
        document.getElementById('prtg-monitoring-host-page-info').textContent =
            `主機 ${hostPage?.total ? offset + 1 : 0}–${offset + (hostPage?.rows.length || 0)}／${hostPage?.total || 0}；已選 ${hostSelection.size}`;
        document.getElementById('prtg-monitoring-host-prev').disabled = hostPageIndex === 0;
        document.getElementById('prtg-monitoring-host-next').disabled = !hostPage?.nextCursor;
    }
    function renderSavedHosts() {
        savedHostBox.replaceChildren();
        const search = savedHostSearch.value.trim().toLocaleLowerCase();
        const filtered = savedHostRows.filter(host => `${host.hostId} ${host.hostName} ${host.status}`.toLocaleLowerCase().includes(search));
        const pageCount = Math.max(1, Math.ceil(filtered.length / 100));
        savedHostPageIndex = Math.min(savedHostPageIndex, pageCount - 1);
        const rows = filtered.slice(savedHostPageIndex * 100, savedHostPageIndex * 100 + 100);
        const labels = { eligible: '可選', inactive: '已停用', merged: '已合併', 'not-netiq': '非 NetIQ', 'not-visible': '目前不可見', missing: '目錄已移除' };
        for (const host of rows) {
            const label = document.createElement('label'); label.className = 'd-flex align-items-center gap-2 py-1';
            const remove = document.createElement('button'); remove.type = 'button'; remove.className = 'btn btn-sm btn-outline-danger';
            remove.textContent = '移除'; remove.setAttribute('aria-label', `移除已保存主機 ${host.hostId}`);
            remove.disabled = !monitoring?.canEdit;
            remove.addEventListener('click', () => {
                hostSelection.delete(Number(host.hostId));
                savedHostRows = savedHostRows.filter(item => Number(item.hostId) !== Number(host.hostId));
                invalidateEstimate(); renderSavedHosts(); renderMonitoringHosts();
                sensorPageSequence++; resetMonitoringSensorPages(); loadMonitoringSensorPage().catch(showSensorError);
            });
            const text = document.createElement('span');
            text.textContent = `${host.hostName} (#${host.hostId}) — ${labels[host.status] || '狀態未知'}`;
            label.append(remove, text); savedHostBox.append(label);
        }
        const start = filtered.length ? savedHostPageIndex * 100 + 1 : 0;
        const end = rows.length ? Math.min(start + rows.length - 1, filtered.length) : 0;
        document.getElementById('prtg-monitoring-saved-host-page-info').textContent =
            `已保存主機 ${start}–${end}／${filtered.length}；共 ${savedHostRows.length} 筆`;
        document.getElementById('prtg-monitoring-saved-host-prev').disabled = savedHostPageIndex === 0;
        document.getElementById('prtg-monitoring-saved-host-next').disabled = savedHostPageIndex + 1 >= pageCount;
        if (!rows.length) savedHostBox.textContent = '沒有符合搜尋條件的已保存主機。';
    }
    async function loadMonitoringHostPage(reset = false) {
        cancelMonitoringPageRequest('host');
        if (reset) { hostCursors = [null]; hostPageIndex = 0; }
        const pageIndex = hostPageIndex, search = hostSearch.value, catalogueToken = monitoring.catalogueToken;
        const cursor = hostCursors[pageIndex], sequence = ++hostPageSequence;
        const controller = new AbortController();
        hostPageController = controller;
        const binding = JSON.stringify([monitoring.revision, catalogueToken, pageIndex, cursor, search]);
        const query = new URLSearchParams({ search, catalogueToken });
        if (cursor) query.set('cursor', cursor);
        let response;
        try { response = await api.get(`/api/prtg/monitoring/hosts?${query.toString()}`, { signal: controller.signal, timeoutMs: 45000 }); }
        catch (error) {
            const currentBinding = monitoring && JSON.stringify([monitoring.revision, monitoring.catalogueToken,
                hostPageIndex, hostCursors[hostPageIndex], hostSearch.value]);
            if (sequence !== hostPageSequence || binding !== currentBinding) return null;
            throw error;
        } finally {
            if (hostPageController === controller) hostPageController = null;
        }
        const currentBinding = monitoring && JSON.stringify([monitoring.revision, monitoring.catalogueToken,
            hostPageIndex, hostCursors[hostPageIndex], hostSearch.value]);
        if (sequence !== hostPageSequence || binding !== currentBinding) return null;
        hostPage = response;
        if (hostPage.nextCursor) hostCursors[pageIndex + 1] = hostPage.nextCursor;
        renderMonitoringHosts();
    }
    function resetMonitoringSensorPages() { sensorCursors = [null]; sensorPageIndex = 0; sensorPage = null; }
    function showSensorError(error) {
        sensorBox.textContent = error.status === 403
            ? '所選主機含目前無法選取的項目；請核對已保存主機及已選 sensor 清單，明確移除失效項目後重新估算。'
            : `sensor 目錄載入失敗：${error.message}`;
    }
    function renderMonitoringSensors() {
        sensorBox.replaceChildren();
        for (const sensor of sensorPage?.rows || []) {
            const label = document.createElement('label'); label.className = 'form-check d-block';
            const check = document.createElement('input'); check.type = 'checkbox'; check.className = 'form-check-input';
            check.value = String(sensor.sensorId); check.checked = sensorSelection.has(Number(sensor.sensorId));
            check.addEventListener('change', () => {
                const id = Number(check.value);
                const proposed = new Set(sensorSelection);
                if (check.checked) proposed.add(id); else proposed.delete(id);
                commitSensorSelection(proposed);
            });
            label.append(check, document.createTextNode(`${sensor.name} (${sensor.sensorId}, ${sensor.sensorType}) — 主機 ${sensor.hostId}${sensor.category ? `；${sensor.category}` : ''}`));
            sensorBox.append(label);
        }
        const offset = sensorPageIndex * 100;
        document.getElementById('prtg-monitoring-sensor-page-info').textContent =
            `sensor ${sensorPage?.total ? offset + 1 : 0}–${offset + (sensorPage?.rows.length || 0)}／${sensorPage?.total || 0}；已選 ${sensorSelection.size}`;
        document.getElementById('prtg-monitoring-sensor-prev').disabled = sensorPageIndex === 0;
        document.getElementById('prtg-monitoring-sensor-next').disabled = !sensorPage?.nextCursor;
        if (!sensorPage?.rows.length) sensorBox.textContent = '此所選主機與搜尋範圍沒有有效的 sensor 對應。';
        renderSelectedSensors();
    }
    function renderSelectedSensors() {
        selectedSensorBox.replaceChildren();
        const search = selectedSensorSearch.value.trim();
        const filtered = sortedIds(sensorSelection).filter(sensorId => String(sensorId).includes(search));
        const pageCount = Math.max(1, Math.ceil(filtered.length / 100));
        selectedSensorPageIndex = Math.min(selectedSensorPageIndex, pageCount - 1);
        const rows = filtered.slice(selectedSensorPageIndex * 100, selectedSensorPageIndex * 100 + 100);
        for (const sensorId of rows) {
            const chip = document.createElement('span'); chip.className = 'badge text-bg-secondary d-inline-flex align-items-center gap-1';
            const value = document.createElement('span'); value.textContent = String(sensorId);
            const remove = document.createElement('button'); remove.type = 'button'; remove.className = 'btn-close btn-close-white';
            remove.setAttribute('aria-label', `移除 sensor ${sensorId}`); remove.title = `移除 sensor ${sensorId}`;
            remove.disabled = !monitoring?.canEdit;
            remove.addEventListener('click', () => {
                sensorSelection.delete(sensorId); invalidateEstimate(); renderMonitoringSensors();
            });
            chip.append(value, remove); selectedSensorBox.append(chip);
        }
        const start = filtered.length ? selectedSensorPageIndex * 100 + 1 : 0;
        const end = rows.length ? Math.min(start + rows.length - 1, filtered.length) : 0;
        document.getElementById('prtg-monitoring-selected-sensor-page-info').textContent =
            `已選 sensor ${start}–${end}／${filtered.length}；選取共 ${sensorSelection.size} 顆`;
        document.getElementById('prtg-monitoring-selected-sensor-prev').disabled = selectedSensorPageIndex === 0;
        document.getElementById('prtg-monitoring-selected-sensor-next').disabled = selectedSensorPageIndex + 1 >= pageCount;
        if (!rows.length) selectedSensorBox.textContent = '沒有符合搜尋條件的已選 sensor。';
    }
    async function loadMonitoringSensorPage() {
        cancelMonitoringPageRequest('sensor');
        const sequence = ++sensorPageSequence;
        const pageIndex = sensorPageIndex, hostIds = sortedIds(hostSelection), search = sensorSearch.value;
        const catalogueToken = monitoring.catalogueToken, revision = monitoring.revision, cursor = sensorCursors[pageIndex];
        const binding = JSON.stringify([revision, catalogueToken, pageIndex, cursor, search, hostIds]);
        if (!hostIds.length) {
            if (sequence === sensorPageSequence && binding === JSON.stringify([monitoring.revision, monitoring.catalogueToken,
                sensorPageIndex, sensorCursors[sensorPageIndex], sensorSearch.value, sortedIds(hostSelection)])) {
                sensorPage = { rows: [], total: 0, nextCursor: null }; renderMonitoringSensors();
            }
            return null;
        }
        const controller = new AbortController();
        sensorPageController = controller;
        let response;
        try {
            response = await api.readOnlyPost('/api/prtg/monitoring/sensors', {
                catalogueToken, cursor, search, hostIds, offset: pageIndex * 100
            }, { signal: controller.signal, timeoutMs: 45000, silent: true });
        } catch (error) {
            const currentBinding = monitoring && JSON.stringify([monitoring.revision, monitoring.catalogueToken,
                sensorPageIndex, sensorCursors[sensorPageIndex], sensorSearch.value, sortedIds(hostSelection)]);
            if (sequence !== sensorPageSequence || binding !== currentBinding) return null;
            throw error;
        } finally {
            if (sensorPageController === controller) sensorPageController = null;
        }
        const currentBinding = monitoring && JSON.stringify([monitoring.revision, monitoring.catalogueToken,
            sensorPageIndex, sensorCursors[sensorPageIndex], sensorSearch.value, sortedIds(hostSelection)]);
        if (sequence !== sensorPageSequence || binding !== currentBinding) return null;
        sensorPage = response;
        if (sensorPage.nextCursor) sensorCursors[pageIndex + 1] = sensorPage.nextCursor;
        renderMonitoringSensors();
    }
    async function loadMonitoring() {
        if (activeSelectionBatch) cancelSelectionBatch(activeSelectionBatch.kind, '正在重新載入目錄，批次已拒絕套用');
        cancelMonitoringPageRequest('host'); cancelMonitoringPageRequest('sensor');
        const sequence = ++monitoringLoadSequence;
        hostPageSequence++; sensorPageSequence++; previewSequence++;
        const draftVersionAtStart = draftVersion;
        try {
            const response = await api.get('/api/prtg/monitoring');
            if (sequence !== monitoringLoadSequence || draftVersionAtStart !== draftVersion) return null;
            monitoring = response; draftVersion++; estimateSequence++;
            hostPageSequence++; sensorPageSequence++;
            const initializedDraftVersion = draftVersion;
            estimateToken = null; estimateFingerprint = null;
            hostSelection.clear(); monitoring.hostIds.forEach(id => hostSelection.add(Number(id)));
            sensorSelection.clear(); monitoring.sensorIds.forEach(id => sensorSelection.add(Number(id)));
            if (Array.isArray(monitoring.savedHosts) && monitoring.savedHosts.length > 3000)
                throw new Error('已保存主機超過 3,000 筆修復清單上限；拒絕部分載入。');
            savedHostRows = Array.isArray(monitoring.savedHosts) ? monitoring.savedHosts.slice() : [];
            savedHostPageIndex = 0; selectedSensorPageIndex = 0;
            for (const input of monitoringForm.querySelectorAll('input,select,button')) input.disabled = !monitoring.canEdit;
            status.textContent = !monitoring.canEdit ? '範圍含不可見主機，請由可見全部範圍的管理者修改。'
                : !monitoring.enabled ? 'PRTG 擷取已停用；儲存的範圍保留，啟用後才評估。'
                : `目前範圍：主機 ${monitoring.hostIds.length ? monitoring.hostIds.length + ' 台' : '全部已對應'}、sensor ${monitoring.sensorIds.length ? monitoring.sensorIds.length + ' 顆' : '不限'}。`;
            // 已保存選取的修復不依賴新候選目錄成功；停用／移除主機仍保留完整已選 ID。
            renderSavedHosts(); renderSelectedSensors();
            baselineFingerprint = scopeFingerprint(monitoringRequest());
            updateSaveButton();
            try { await loadMonitoringHostPage(true); }
            catch (error) {
                if (sequence === monitoringLoadSequence && initializedDraftVersion === draftVersion)
                    hostSelect.textContent = `主機目錄載入失敗：${error.message}；已保存選取仍可核對。`;
            }
            if (sequence !== monitoringLoadSequence || initializedDraftVersion !== draftVersion) return null;
            renderSavedHosts();
            resetMonitoringSensorPages();
            try { await loadMonitoringSensorPage(); }
            catch (error) {
                if (sequence === monitoringLoadSequence && initializedDraftVersion === draftVersion)
                    showSensorError(error);
            }
            if (sequence !== monitoringLoadSequence || initializedDraftVersion !== draftVersion) return null;
            baselineFingerprint = scopeFingerprint(monitoringRequest());
            updateSaveButton();
        } catch (error) {
            if (sequence !== monitoringLoadSequence || draftVersionAtStart !== draftVersion) return null;
            for (const input of monitoringForm.querySelectorAll('input,select,button')) input.disabled = true;
            status.textContent = `無法載入或檢視試點範圍：${error.message}。請由至少能檢視完整既有試點的 Maintain 管理者處理。`;
        }
    }
    document.getElementById('prtg-monitoring-host-prev').addEventListener('click', async () => {
        if (hostPageIndex > 0) { hostPageIndex--; await loadMonitoringHostPage(); }
    });
    document.getElementById('prtg-monitoring-host-next').addEventListener('click', async () => {
        if (hostPage?.nextCursor) { hostCursors[hostPageIndex + 1] = hostPage.nextCursor; hostPageIndex++; await loadMonitoringHostPage(); }
    });
    document.getElementById('prtg-monitoring-sensor-prev').addEventListener('click', async () => {
        if (sensorPageIndex > 0) { sensorPageIndex--; await loadMonitoringSensorPage(); }
    });
    document.getElementById('prtg-monitoring-sensor-next').addEventListener('click', async () => {
        if (sensorPage?.nextCursor) { sensorCursors[sensorPageIndex + 1] = sensorPage.nextCursor; sensorPageIndex++; await loadMonitoringSensorPage(); }
    });
    document.getElementById('prtg-monitoring-host-select-page').addEventListener('click', () => applyCurrentPageSelection('host', true));
    document.getElementById('prtg-monitoring-host-clear-page').addEventListener('click', () => applyCurrentPageSelection('host', false));
    document.getElementById('prtg-monitoring-host-select-search').addEventListener('click', () => addCurrentSearchResults('host'));
    document.getElementById('prtg-monitoring-host-select-cancel').addEventListener('click', () => cancelSelectionBatch('host'));
    document.getElementById('prtg-monitoring-sensor-select-page').addEventListener('click', () => applyCurrentPageSelection('sensor', true));
    document.getElementById('prtg-monitoring-sensor-clear-page').addEventListener('click', () => applyCurrentPageSelection('sensor', false));
    document.getElementById('prtg-monitoring-sensor-select-search').addEventListener('click', () => addCurrentSearchResults('sensor'));
    document.getElementById('prtg-monitoring-sensor-select-cancel').addEventListener('click', () => cancelSelectionBatch('sensor'));
    document.getElementById('prtg-monitoring-saved-host-prev').addEventListener('click', () => {
        if (savedHostPageIndex > 0) { savedHostPageIndex--; renderSavedHosts(); }
    });
    document.getElementById('prtg-monitoring-saved-host-next').addEventListener('click', () => {
        const search = savedHostSearch.value.trim().toLocaleLowerCase();
        const count = savedHostRows.filter(host => `${host.hostId} ${host.hostName} ${host.status}`.toLocaleLowerCase().includes(search)).length;
        if ((savedHostPageIndex + 1) * 100 < count) { savedHostPageIndex++; renderSavedHosts(); }
    });
    savedHostSearch.addEventListener('input', () => { savedHostPageIndex = 0; renderSavedHosts(); });
    document.getElementById('prtg-monitoring-selected-sensor-prev').addEventListener('click', () => {
        if (selectedSensorPageIndex > 0) { selectedSensorPageIndex--; renderSelectedSensors(); }
    });
    document.getElementById('prtg-monitoring-selected-sensor-next').addEventListener('click', () => {
        const count = sortedIds(sensorSelection).filter(id => String(id).includes(selectedSensorSearch.value.trim())).length;
        if ((selectedSensorPageIndex + 1) * 100 < count) { selectedSensorPageIndex++; renderSelectedSensors(); }
    });
    selectedSensorSearch.addEventListener('input', () => { selectedSensorPageIndex = 0; renderSelectedSensors(); });
    hostSearch.addEventListener('input', () => {
        if (activeSelectionBatch?.kind === 'host') cancelSelectionBatch('host', '主機搜尋文字已變更，批次已拒絕套用');
        cancelMonitoringPageRequest('host');
        clearTimeout(hostSearchTimer); hostSearchTimer = setTimeout(() => loadMonitoringHostPage(true).catch(e => { status.textContent = `主機目錄載入失敗：${e.message}`; }), 250);
    });
    sensorSearch.addEventListener('input', () => {
        if (activeSelectionBatch?.kind === 'sensor') cancelSelectionBatch('sensor', 'sensor 搜尋文字已變更，批次已拒絕套用');
        cancelMonitoringPageRequest('sensor');
        clearTimeout(sensorSearchTimer); sensorSearchTimer = setTimeout(() => {
            resetMonitoringSensorPages(); loadMonitoringSensorPage().catch(showSensorError);
        }, 250);
    });
    window.addEventListener('beforeunload', event => {
        if (monitoring && baselineFingerprint !== scopeFingerprint(monitoringRequest())) {
            event.preventDefault(); event.returnValue = '';
        }
    });
    monitoringForm.addEventListener('submit', async event => {
        event.preventDefault(); if (!monitoring) return;
        saveButton.disabled = true;
        let submitDraftVersion = null;
        try {
            let request = monitoringRequest();
            if (request.sensorIds.length && !request.hostIds.length) throw new Error('指定 sensor 時須同時勾選所屬主機。');
            const requestVersion = draftVersion;
            submitDraftVersion = requestVersion;
            const requestFingerprint = scopeFingerprint(request);
            if (!request.estimateToken || estimateFingerprint !== requestFingerprint) {
                const estimate = await estimateScope();
                if (!estimate) throw new Error('範圍在估算期間已變更，未送出保存。');
            }
            request = monitoringRequest();
            if (requestVersion !== draftVersion || requestFingerprint !== scopeFingerprint(request))
                throw new Error('範圍在估算期間已變更，未送出保存。');
            const putRequest = monitoringRequest();
            if (requestVersion !== draftVersion || requestFingerprint !== scopeFingerprint(putRequest))
                throw new Error('範圍在保存前已變更，未送出保存。');
            const savedRevision = await api.put('/api/prtg/monitoring', putRequest);
            if (requestVersion === draftVersion && requestFingerprint === scopeFingerprint(monitoringRequest())) {
                await loadMonitoring();
            } else {
                if (typeof savedRevision === 'string' && savedRevision.length) {
                    monitoring.revision = savedRevision;
                    baselineFingerprint = scopeFingerprint({ ...putRequest, revision: savedRevision });
                }
                invalidateEstimate();
                status.textContent = '已保存送出時的範圍；送出期間表單又有變更，草稿已保留。請重新估算目前草稿，再保存。';
            }
        } catch (error) {
            if (submitDraftVersion === null || submitDraftVersion === draftVersion)
                status.textContent = `儲存失敗：${error.message}。設定衝突時請重新載入頁面。`;
        }
        finally { updateSaveButton(); }
    });
    loadMonitoring();
}

const operationsStatus = document.getElementById('prtg-operations-status');
if (operationsStatus) {
    const labels = { pending: '等待重試', applied: '已追加／案件已涵蓋', unassigned: '已追加但尚未交辦',
        'waiting-netiq': '等待 NetIQ 成功紀錄', 'scope-paused': '範圍或資源對應已變更', 'rules-changed': '規則已變更，等待重新評估', shadow: '僅診斷證據',
        retry: '寫入或派工待重試', invalid: '證據無效', 'smtp-accepted': 'SMTP 已接受',
        'no-qualified-recipient': '無合格收件人', 'sending-result-unknown': '寄送結果未知',
        'failed-or-unknown': '未收到 SMTP 確認，可能已送達；重試可能重複',
        'failed-or-not-sent': '寄送未完成（舊狀態）', 'not-sent': '本輪未送出' };
    async function showOperations() {
        const button = document.getElementById('prtg-operations-refresh');
        button.disabled = true;
        operationsStatus.textContent = '正在讀取處理狀態…';
        try {
            const data = await api.get('/api/prtg/operations');
            operationsStatus.replaceChildren();
            const list = document.createElement('ul');
            for (const operation of data.operationVersions || []) {
                const row = document.createElement('li');
                row.textContent = `${operation.kind}｜工作 ${operation.operationId}｜${operation.state}｜` +
                    `設定採用 ${operation.adoptedSettingsRevision} → 期望 ${operation.expectedSettingsRevision}｜` +
                    `範圍採用 ${operation.adoptedScopeRevision} → 期望 ${operation.expectedScopeRevision}｜` +
                    `最後完成：${operation.lastCompletedStage}。`;
                list.append(row);
            }
            for (const intent of data.notifications) {
                const row = document.createElement('li');
                row.textContent = `主機 #${intent.hostId}｜${intent.recordDate.slice(0, 10)}｜${labels[intent.status] || intent.status}｜` +
                    `通知設定採用 ${intent.adoptedSettingsRevision || '未知'} → 期望 ${intent.expectedSettingsRevision}｜` +
                    intent.recipients.map(r => `${labels[r.status] || r.status} ${r.count} 位`).join('、');
                list.append(row);
            }
            if (!list.childElementCount) operationsStatus.textContent = '可見範圍內尚無通知意圖。';
            else operationsStatus.append(list);
        } catch (error) { operationsStatus.textContent = `查詢失敗：${error.message}`; }
        finally { button.disabled = false; }
    }
    document.getElementById('prtg-operations-refresh').addEventListener('click', () => showOperations());
    showOperations();
}
