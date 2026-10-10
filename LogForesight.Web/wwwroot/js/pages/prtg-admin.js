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
import { initializePrtgQualificationJobs } from '../prtg-qualification-jobs.js';
import { trustedProbeErrorMessage, trustedQualificationOutcome } from '../prtg-maintenance-outcomes.js';
import { toScopeSelectValue, prtgScopeInapplicableText } from '../core/prtg-scope-labels.js';
import { parseProbeSensorTypes } from '../core/prtg-probe-types.js';
import { extractProbeEvidenceJson, isProbeEvidenceDownloadable } from '../core/prtg-probe-evidence.js';
import { uploadDiagnosticFile, getDiagnosticTransfer, abandonDiagnosticTransfer } from '../core/prtg-diagnostic-upload.js';

bindTabs(document.getElementById('prtg-tabs'), { hash: true, onChange: name => {
    if (name === 'probe') queueMicrotask(loadDiskReadiness);
    if (name === 'timeline-progress') queueMicrotask(loadTimelineProgress);
    if (name === 'profile-refresh') { queueMicrotask(loadTrustedProfileRefresh); queueMicrotask(loadTrustedProfileBindings); }
} });

let trustedProfileOffset = 0;
let trustedBindingOffset = 0;
async function loadTrustedProfileRefresh() {
    const status = document.getElementById('prtg-profile-refresh-progress');
    const rowsRoot = document.getElementById('prtg-profile-refresh-rows');
    if (!status || !rowsRoot) return;
    status.textContent = '正在讀取背景進度與 100 筆 profile 狀態…';
    try {
        const [progress, page] = await Promise.all([
            api.get('/api/prtg/monitoring/trusted-sampling/refresh-progress', { silent: true }),
            api.get(`/api/prtg/monitoring/trusted-sampling/refresh-sensors?offset=${trustedProfileOffset}&limit=100`, { silent: true })
        ]);
        const p = progress || {};
        status.textContent = `所選 ${p.selectedSensors || 0} 顆｜符合條件 ${p.eligibleSensors || 0}｜游標 ${p.cursor || 0}｜符合 ${p.qualified || 0}、等待 ${p.waiting || 0}、不可用 ${p.unavailable || 0}、失敗 ${p.failed || 0}｜表格請求 ${p.rawRequestCount || 0}｜實際請求耗時 ${formatNumber(p.observedRequestSeconds || 0)} 秒｜最近結果 ${p.lastOutcome || '尚未執行'}${p.lastWaitingReason ? `（${p.lastWaitingReason}）` : ''}${p.running ? '｜工作進行中' : ''}${p.nextSweepAtUtc ? `｜下次巡覽 ${p.nextSweepAtUtc}` : ''}`;
        const rows = Array.isArray(page?.rows) ? page.rows : [];
        rowsRoot.replaceChildren();
        for (const row of rows) {
            const line = document.createElement('div');
            line.className = 'small py-1 border-bottom';
            line.textContent = `Sensor #${row.sensorObjid}｜${row.eligible ? '符合巡覽條件' : '目前排除'}｜${row.status || 'waiting'}｜${row.reason || '等待更新'}${row.nextAttemptAtUtc ? `｜下次嘗試 ${row.nextAttemptAtUtc}` : ''}`;
            rowsRoot.append(line);
        }
        const next = page?.nextOffset;
        document.getElementById('prtg-profile-refresh-page').textContent = `第 ${Math.floor(trustedProfileOffset / 100) + 1} 頁｜目前進度列 ${page?.total || 0}`;
        document.getElementById('prtg-profile-refresh-prev').disabled = trustedProfileOffset === 0;
        document.getElementById('prtg-profile-refresh-next').disabled = next == null;
    } catch (error) {
        rowsRoot.replaceChildren();
        status.textContent = `Trusted profile 進度無法讀取：${error?.message || '請重新載入。'}`;
        document.getElementById('prtg-profile-refresh-page').textContent = '';
        document.getElementById('prtg-profile-refresh-prev').disabled = true;
        document.getElementById('prtg-profile-refresh-next').disabled = true;
    }
}
document.getElementById('prtg-profile-refresh-reload')?.addEventListener('click', loadTrustedProfileRefresh);
document.getElementById('prtg-profile-refresh-prev')?.addEventListener('click', () => { trustedProfileOffset = Math.max(0, trustedProfileOffset - 100); loadTrustedProfileRefresh(); });

document.getElementById('prtg-profile-refresh-next')?.addEventListener('click', () => { trustedProfileOffset += 100; loadTrustedProfileRefresh(); });

const trustedBindingBase = '/api/prtg/monitoring/trusted-sampling';
let trustedBindingSelectedSensor = null;
let trustedBindingVersions = null;
let trustedBindingProbe = null;
let trustedBindingSaved = null;
let trustedBindingDraftDirty = false;
let trustedBindingBusy = false;
const trustedBindingBatchDrafts = new Map();
const trustedQuantityValues = {
    CpuLoadPercent: '1', MemoryUsedPercent: '2', MemoryAvailablePercent: '3',
    DiskFreePercent: '4', DiskUsedPercent: '5'
};
const bindingFieldIds = {
    channelObjectId: 'prtg-profile-binding-channel', quantity: 'prtg-profile-binding-quantity', unit: 'prtg-profile-binding-unit',
    scale: 'prtg-profile-binding-scale', direction: 'prtg-profile-binding-direction', intervalRawUnit: 'prtg-profile-binding-interval-unit',
    rawTimestampTimeZoneId: 'prtg-profile-binding-raw-zone', analysisTimeZoneId: 'prtg-profile-binding-analysis-zone',
    timeBasisEvidenceReference: 'prtg-profile-binding-time-evidence'
};
const trustedBindingEditorDrafts = new Map();

function trustedChannelObjectId(value) {
    if (typeof value === 'number' && Number.isSafeInteger(value) && value >= 0) return String(value);
    if (typeof value === 'string' && /^[0-9]{1,20}$/.test(value)) return value.replace(/^0+(?=\d)/, '');
    return null;
}

function sameTrustedBindingFence(left, right) {
    const keys = ['expectedSettingsRevision', 'expectedPolicyRevision', 'expectedIdentityEpoch', 'expectedChannelGeneration', 'expectedBindingRevision'];
    return !!left && !!right && keys.every(key => left[key] != null && right[key] != null && left[key] === right[key]);
}

function updateTrustedBindingBatchStatus(message) {
    const count = trustedBindingBatchDrafts.size;
    document.getElementById('prtg-profile-binding-batch-status').textContent = message ||
        (count ? `目前有 ${count} 筆待送批次草稿；最多 100 筆。逐筆核對後才會送出。` : '尚無批次草稿。');
    document.getElementById('prtg-profile-binding-batch-submit').disabled = trustedBindingBusy || count < 1;
    document.getElementById('prtg-profile-binding-batch-clear').disabled = trustedBindingBusy || count < 1;
}

async function loadTrustedProfileBindings() {
    const status = document.getElementById('prtg-profile-binding-status');
    const root = document.getElementById('prtg-profile-binding-rows');
    if (!status || !root) return;
    status.textContent = '正在讀取目前正式範圍的 Trusted profiles…';
    try {
        const page = await api.get(`${trustedBindingBase}/profiles?offset=${trustedBindingOffset}&limit=100`, { silent: true });
        const rows = Array.isArray(page?.rows) ? page.rows : [];
        root.replaceChildren();
        for (const row of rows) {
            const line = document.createElement('div');
            line.className = 'd-flex flex-wrap justify-content-between align-items-start gap-2 small py-2 border-bottom';
            const summary = document.createElement('span');
            const sensorKey = String(row.sensorObjid);
            const queued = trustedBindingBatchDrafts.has(sensorKey) ? '｜待批次提交' : '';
            summary.textContent = `Sensor #${row.sensorObjid}｜profile ${row.status || 'unknown'}／binding ${row.bindingStatus || 'unbound'}｜綁定 ${row.bindingRevision ?? '—'}｜頻道 ${row.boundChannelObjectId ?? '未選'} ${row.boundChannelCaption || ''}｜語意 ${row.bindingQuantity || row.quantity || '未設定'} ${row.unit || ''}｜資格 ${row.qualificationProofReference ? '有證據參考' : '尚無核驗證據'}${queued}${Array.isArray(row.missingFacts) && row.missingFacts.length ? `｜缺項 ${row.missingFacts.join(', ')}` : ''}`;
            const inspect = document.createElement('button');
            inspect.type = 'button'; inspect.className = 'btn btn-sm btn-outline-primary'; inspect.textContent = '設定此 sensor';
            inspect.addEventListener('click', () => openTrustedBinding(row.sensorObjid));
            line.append(summary, inspect); root.append(line);
        }
        const next = page?.nextOffset;
        document.getElementById('prtg-profile-binding-page').textContent = `offset ${page?.offset ?? trustedBindingOffset}｜共 ${page?.total ?? 0}`;
        document.getElementById('prtg-profile-binding-prev').disabled = trustedBindingOffset === 0;
        document.getElementById('prtg-profile-binding-next').disabled = next == null;
        status.textContent = `已載入 ${rows.length} 筆 profile｜總數 ${page?.total ?? 0}`;
    } catch (error) {
        root.replaceChildren();
        document.getElementById('prtg-profile-binding-page').textContent = '';
        document.getElementById('prtg-profile-binding-prev').disabled = true;
        document.getElementById('prtg-profile-binding-next').disabled = true;
        status.textContent = `Trusted profile 綁定資料無法讀取：${error?.message || '請重新載入。'}`;
    }
}

function setTrustedBindingPending(pending) {
    trustedBindingBusy = pending;
    for (const id of ['prtg-profile-binding-probe', 'prtg-profile-binding-reload', 'prtg-profile-binding-channel', ...Object.values(bindingFieldIds), 'prtg-profile-binding-save', 'prtg-profile-binding-queue', 'prtg-profile-binding-qualify', 'prtg-profile-binding-batch-submit', 'prtg-profile-binding-batch-clear']) {
        const control = document.getElementById(id);
        if (control) control.disabled = pending;
    }
    if (!pending) updateTrustedBindingBatchStatus();
    renderTrustedBindingQualificationControl();
}

function renderTrustedBindingQualificationControl() {
    const control = document.getElementById('prtg-profile-binding-qualify');
    if (control) control.disabled = trustedBindingBusy || trustedBindingDraftDirty ||
        (trustedBindingSelectedSensor != null && trustedBindingBatchDrafts.has(trustedBindingSelectedSensor)) ||
        !trustedBindingSaved?.bindingFingerprint || !!trustedBindingSaved.qualificationProofReference;
}

function fillTrustedBindingDraft(binding) {
    const channel = document.getElementById(bindingFieldIds.channelObjectId);
    if (channel && binding?.channelObjectId != null && ![...channel.options].some(option => option.value === String(binding.channelObjectId))) {
        const option = document.createElement('option'); option.value = String(binding.channelObjectId); option.textContent = `已保存頻道 #${binding.channelObjectId}（需重新 probe 確認）`; channel.append(option);
    }
    for (const [key, id] of Object.entries(bindingFieldIds)) {
        const control = document.getElementById(id);
        if (!control) continue;
        const field = key === 'channelObjectId' ? 'channelObjectId' : key;
        let value = binding?.[field];
        if (key === 'quantity' && typeof value === 'string') value = trustedQuantityValues[value] || '';
        if (value !== undefined && value !== null) control.value = String(value);
    }
    trustedBindingDraftDirty = false;
}

function captureTrustedBindingEditorDraft(sensorId) {
    if (!sensorId || !trustedBindingDraftDirty) return;
    const channel = document.getElementById(bindingFieldIds.channelObjectId);
    trustedBindingEditorDrafts.set(sensorId, {
        fields: Object.fromEntries(Object.entries(bindingFieldIds).map(([key, id]) => [key,
            document.getElementById(id)?.value ?? ''])),
        channelOptions: channel ? [...channel.options].map(option => ({ value: option.value, text: option.textContent || '' })) : [],
        selectedChannel: channel?.value || '',
        versions: trustedBindingVersions,
        probe: trustedBindingProbe
    });
}

function restoreTrustedBindingEditorDraft(draft) {
    const channel = document.getElementById(bindingFieldIds.channelObjectId);
    if (channel) {
        channel.replaceChildren(...(draft.channelOptions || []).map(item => new Option(item.text, item.value)));
        channel.value = draft.selectedChannel || '';
    }
    for (const [key, id] of Object.entries(bindingFieldIds)) {
        const control = document.getElementById(id);
        if (control) control.value = draft.fields?.[key] ?? '';
    }
    trustedBindingVersions = draft.versions;
    trustedBindingProbe = draft.probe || null;
    trustedBindingDraftDirty = true;
    renderTrustedBindingQualificationControl();
}

function isTrustedBindingCommitReceipt(value) {
    return value?.committed === true && value?.reloadRequired === true &&
        Array.isArray(value?.committedSensorObjids);
}

function clearTrustedBindingOperationAuthority() {
    trustedBindingSaved = null;
    trustedBindingProbe = null;
    trustedBindingVersions = null;
    renderTrustedBindingQualificationControl();
}

function bindingEditorFieldsMatchBody(fields, body) {
    if (!fields || !body) return false;
    return trustedChannelObjectId(fields.channelObjectId) === trustedChannelObjectId(body.channelObjectId) &&
        Number(fields.quantity) === Number(body.quantity) && fields.unit === body.unit &&
        Number(fields.scale) === Number(body.scale) && fields.direction === body.direction &&
        fields.intervalRawUnit === body.intervalRawUnit &&
        fields.rawTimestampTimeZoneId === body.rawTimestampTimeZoneId &&
        fields.analysisTimeZoneId === body.analysisTimeZoneId &&
        fields.timeBasisEvidenceReference === body.timeBasisEvidenceReference;
}

function cachedTrustedBindingEditorMatchesBody(sensorId, body) {
    const draft = trustedBindingEditorDrafts.get(String(sensorId));
    return !!draft && bindingEditorFieldsMatchBody(draft.fields, body);
}

function removeCommittedTrustedBindingDraft(sensorId, body) {
    const key = String(sensorId);
    if (body && trustedBindingBatchDrafts.has(key) &&
        bindingEditorFieldsMatchBody(trustedBindingBatchDrafts.get(key), body))
        trustedBindingBatchDrafts.delete(key);
    if (cachedTrustedBindingEditorMatchesBody(key, body)) trustedBindingEditorDrafts.delete(key);
}

async function openTrustedBinding(sensorObjid) {
    if (trustedBindingBusy) return;
    captureTrustedBindingEditorDraft(trustedBindingSelectedSensor);
    trustedBindingSelectedSensor = String(sensorObjid);
    trustedBindingProbe = null;
    trustedBindingSaved = null;
    trustedBindingVersions = null;
    const form = document.getElementById('prtg-profile-binding-form');
    const action = document.getElementById('prtg-profile-binding-action-status');
    const current = document.getElementById('prtg-profile-binding-current');
    const channel = document.getElementById('prtg-profile-binding-channel');
    form?.classList.remove('d-none');
    form?.reset();
    document.getElementById('prtg-profile-binding-sensor-label').textContent = `#${trustedBindingSelectedSensor}`;
    action.textContent = '正在載入最新 CAS 版本…';
    current.textContent = '';
    channel.replaceChildren(new Option('請先 probe 並人工選取頻道', ''));
    document.getElementById('prtg-profile-binding-qualify').disabled = true;
    trustedBindingDraftDirty = false;
    await reloadTrustedBinding(false);
    const editorDraft = trustedBindingEditorDrafts.get(trustedBindingSelectedSensor);
    if (editorDraft) {
        const freshVersions = trustedBindingVersions;
        restoreTrustedBindingEditorDraft(editorDraft);
        action.textContent = sameTrustedBindingFence(freshVersions, editorDraft.versions)
            ? '已恢復此 sensor 尚未提交的本機草稿；草稿仍使用原始 fence，核驗已停用。'
            : '已恢復未提交草稿，但來源 fence 已過期；草稿不會自動升級版本，請重新載入並 probe 後再送出。';
        return;
    }
    const queuedDraft = trustedBindingBatchDrafts.get(trustedBindingSelectedSensor);
    if (queuedDraft) {
        fillTrustedBindingDraft(queuedDraft);
        action.textContent = '已載入此 sensor 的本機批次草稿；它保留原始 fence，重新 probe 並加入批次可更新 fence。';
    }
}

async function reloadTrustedBinding(preserveDraft = true, allowPending = false) {
    if (!trustedBindingSelectedSensor || (trustedBindingBusy && !allowPending)) return;
    const action = document.getElementById('prtg-profile-binding-action-status');
    const current = document.getElementById('prtg-profile-binding-current');
    setTrustedBindingPending(true);
    try {
        const response = await api.get(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}`, { silent: true });
        const originalVersions = trustedBindingDraftDirty ? trustedBindingVersions : null;
        trustedBindingVersions = originalVersions || response || null;
        trustedBindingSaved = response?.binding || null;
        const b = trustedBindingSaved;
        current.textContent = b
            ? `目前狀態：${b.status || 'unknown'}｜revision ${b.bindingRevision ?? '—'}｜頻道 #${b.channelObjectId ?? '未設定'} ${b.expectedCaption || ''}｜資格證據 ${b.qualificationProofReference ? '已保存參考' : '無'}｜${Array.isArray(b.missingFacts) ? b.missingFacts.join(', ') : '無已知缺項'}`
            : '尚無已保存綁定。保存後狀態仍為 waiting，需另行執行來源核驗。';
        if (!preserveDraft || !trustedBindingDraftDirty) fillTrustedBindingDraft(b);
        action.textContent = preserveDraft && trustedBindingDraftDirty
            ? (sameTrustedBindingFence(response, originalVersions)
                ? '已重新讀取目前版本；保留原始 fence 與尚未提交草稿。'
                : '目前版本已變更；保留尚未提交草稿與舊 fence，請重新載入並 probe 後再送出。')
            : '已載入目前版本。';
        document.getElementById('prtg-profile-binding-qualify').disabled = !b || !!b.qualificationProofReference || !b.bindingFingerprint;
    } catch (error) {
        action.textContent = `目前版本無法讀取，草稿保留：${error?.message || '請稍後重試。'}`;
    } finally { setTrustedBindingPending(false); }
}

for (const id of Object.values(bindingFieldIds)) document.getElementById(id)?.addEventListener('input', () => { trustedBindingDraftDirty = true; renderTrustedBindingQualificationControl(); });
document.getElementById('prtg-profile-binding-channel')?.addEventListener('change', () => { trustedBindingDraftDirty = true; renderTrustedBindingQualificationControl(); });
document.getElementById('prtg-profile-binding-reload')?.addEventListener('click', () => reloadTrustedBinding(true));
document.getElementById('prtg-profile-refresh-reload')?.addEventListener('click', loadTrustedProfileBindings);
document.getElementById('prtg-profile-binding-prev')?.addEventListener('click', () => { trustedBindingOffset = Math.max(0, trustedBindingOffset - 100); loadTrustedProfileBindings(); });
document.getElementById('prtg-profile-binding-next')?.addEventListener('click', () => { trustedBindingOffset += 100; loadTrustedProfileBindings(); });
document.getElementById('prtg-profile-binding-probe')?.addEventListener('click', async () => {
    if (!trustedBindingSelectedSensor || trustedBindingBusy) return;
    const action = document.getElementById('prtg-profile-binding-action-status');
    const channel = document.getElementById('prtg-profile-binding-channel');
    setTrustedBindingPending(true); action.textContent = '正在讀取單顆唯讀來源 probe…';
    try {
        const rows = await api.post(`${trustedBindingBase}/probe`, { sensorObjids: [Number(trustedBindingSelectedSensor)] }, { silent: true, timeoutMs: 30000 });
        if (isTrustedBindingCommitReceipt(rows)) {
            action.textContent = '探測期間已有 profile 更新提交，但目錄已變更；請重新載入核對，不要直接重送。';
            clearTrustedBindingOperationAuthority();
            await loadTrustedProfileBindings();
            return;
        }
        const result = Array.isArray(rows) ? rows.find(item => String(item.sensorObjid) === trustedBindingSelectedSensor) : null;
        if (!result || !Array.isArray(result.channels)) throw new Error('來源未回傳可選頻道；請確認 probe 狀態。');
        trustedBindingProbe = result;
        channel.replaceChildren(new Option('請人工選擇頻道（不自動猜測）', ''));
        for (const item of result.channels) {
            const channelId = trustedChannelObjectId(item.channelObjectId);
            if (channelId === null) continue;
            const option = document.createElement('option'); option.value = channelId;
            option.textContent = `#${item.channelObjectId}｜${item.caption || '無 caption'}｜${item.unit || '無單位'}｜值 ${item.rawValue ?? '未知'}${item.sourceMarkedPrimary ? '｜來源標記 primary' : ''}`;
            channel.append(option);
        }
        document.getElementById('prtg-profile-binding-channel-evidence').textContent = `Probe 狀態 ${result.status || 'unknown'}｜Identity epoch ${result.identityEpoch ?? '未知'}｜Channel generation ${result.channelGeneration ?? '未知'}｜資源 generation ${result.resourceGeneration ?? '未知'}｜${result.channelsTruncated ? '頻道清單已截斷，不能由此保存完整語意' : '已列來源回傳頻道'}；probe 本身只供選擇，不授予資格。`;
        action.textContent = `來源回傳 ${result.channels.length} 個頻道，其中可用 ID ${channel.options.length - 1} 個；請核對來源語意後明確選取。`;
    } catch (error) {
        action.textContent = trustedProbeErrorMessage(error);
    }
    finally { setTrustedBindingPending(false); }
});

document.getElementById('prtg-profile-binding-queue')?.addEventListener('click', async () => {
    if (!trustedBindingSelectedSensor || !trustedBindingProbe || trustedBindingBusy) return;
    if (!document.getElementById('prtg-profile-binding-form').reportValidity()) return;
    if (trustedBindingBatchDrafts.size >= 100 && !trustedBindingBatchDrafts.has(trustedBindingSelectedSensor)) {
        document.getElementById('prtg-profile-binding-action-status').textContent = '批次已達 100 筆上限；先提交或清除這批草稿。';
        return;
    }
    const action = document.getElementById('prtg-profile-binding-action-status');
    const selectedId = document.getElementById(bindingFieldIds.channelObjectId).value;
    const selected = trustedBindingProbe.channels?.find(item => trustedChannelObjectId(item.channelObjectId) === selectedId);
    if (!selected || trustedBindingProbe.channelsTruncated) { action.textContent = '請重新 probe，並從未截斷的來源頻道中明確選擇一個 Channel ID。'; return; }
    if (!String(selected.caption || '').trim()) { action.textContent = '所選頻道缺少來源 caption；無法建立要求 exact caption 的 binding。'; return; }
    const scale = Number(document.getElementById(bindingFieldIds.scale).value);
    if (!Number.isFinite(scale) || scale <= 0) { action.textContent = 'Scale 必須是有限正數。'; return; }
    setTrustedBindingPending(true); action.textContent = '正在讀取目前 fence，建立一筆本機批次草稿…';
    try {
        const latest = await api.get(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}`, { silent: true });
        if (!sameTrustedBindingFence(latest, trustedBindingVersions) || trustedBindingProbe.identityEpoch == null || trustedBindingProbe.channelGeneration == null ||
            latest.expectedIdentityEpoch !== trustedBindingProbe.identityEpoch || latest.expectedChannelGeneration !== trustedBindingProbe.channelGeneration) {
            action.textContent = '設定、政策、綁定或來源 fence 已改變；草稿未加入。請明確重新載入並 probe。'; return;
        }
        const body = {
            sensorObjid: Number(trustedBindingSelectedSensor), expectedSettingsRevision: latest.expectedSettingsRevision,
            expectedPolicyRevision: latest.expectedPolicyRevision, expectedIdentityEpoch: trustedBindingProbe.identityEpoch,
            expectedChannelGeneration: trustedBindingProbe.channelGeneration, expectedBindingRevision: latest.expectedBindingRevision,
            channelObjectId: selectedId, expectedCaption: selected.caption || '',
            quantity: Number(document.getElementById(bindingFieldIds.quantity).value),
            unit: document.getElementById(bindingFieldIds.unit).value, scale,
            direction: document.getElementById(bindingFieldIds.direction).value,
            intervalRawUnit: document.getElementById(bindingFieldIds.intervalRawUnit).value,
            rawTimestampTimeZoneId: document.getElementById(bindingFieldIds.rawTimestampTimeZoneId).value,
            analysisTimeZoneId: document.getElementById(bindingFieldIds.analysisTimeZoneId).value,
            timeBasisEvidenceReference: document.getElementById(bindingFieldIds.timeBasisEvidenceReference).value
        };
        trustedBindingBatchDrafts.set(trustedBindingSelectedSensor, body);
        trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
        trustedBindingDraftDirty = false;
        renderTrustedBindingQualificationControl();
        action.textContent = `已將 sensor #${trustedBindingSelectedSensor} 加入批次草稿；尚未送到伺服器。`;
        updateTrustedBindingBatchStatus();
        await loadTrustedProfileBindings();
    } catch (error) {
        action.textContent = `無法建立批次草稿：${error?.message || '請稍後重試。'}`;
    } finally { setTrustedBindingPending(false); updateTrustedBindingBatchStatus(); }
});

document.getElementById('prtg-profile-binding-batch-clear')?.addEventListener('click', () => {
    if (trustedBindingSelectedSensor && trustedBindingBatchDrafts.has(trustedBindingSelectedSensor) && !trustedBindingDraftDirty) {
        trustedBindingDraftDirty = true;
        captureTrustedBindingEditorDraft(trustedBindingSelectedSensor);
    }
    trustedBindingBatchDrafts.clear();
    document.getElementById('prtg-profile-binding-batch-results').replaceChildren();
    renderTrustedBindingQualificationControl();
    updateTrustedBindingBatchStatus('已清除明確排入的批次草稿；目前編輯器內容保留為未提交草稿。');
});

document.getElementById('prtg-profile-binding-batch-submit')?.addEventListener('click', async () => {
    if (trustedBindingBusy || trustedBindingBatchDrafts.size < 1 || trustedBindingBatchDrafts.size > 100) return;
    const action = document.getElementById('prtg-profile-binding-action-status');
    const resultsRoot = document.getElementById('prtg-profile-binding-batch-results');
    const rows = [...trustedBindingBatchDrafts.values()];
    const submittedBySensor = new Map(rows.map(row => [String(row.sensorObjid), row]));
    const acceptedSensorIds = new Set();
    let committedReceiptReceived = false;
    setTrustedBindingPending(true); updateTrustedBindingBatchStatus(`正在送出 ${rows.length} 筆逐 row fence 批次…`);
    action.textContent = '批次保存進行中；每列會各自接受或拒絕，語意變更後須重新核驗。';
    resultsRoot.replaceChildren();
    try {
        const results = await api.post(`${trustedBindingBase}/bindings/batch`, { rows }, { silent: true, timeoutMs: 60000 });
        if (isTrustedBindingCommitReceipt(results)) {
            committedReceiptReceived = true;
            for (const sensorId of results.committedSensorObjids) {
                const key = String(sensorId);
                removeCommittedTrustedBindingDraft(key, submittedBySensor.get(key));
                trustedBindingBatchDrafts.delete(key);
                acceptedSensorIds.add(key);
            }
            action.textContent = `已提交 sensor ${results.committedSensorObjids.map(id => `#${id}`).join(', ')}；目錄已變更，請重新載入核對，不要直接重送。`;
            for (const sensorId of results.committedSensorObjids) {
                const line = document.createElement('div');
                line.textContent = `Sensor #${sensorId}｜已提交｜目錄已變更，請重新載入核對。`;
                resultsRoot.append(line);
            }
            await loadTrustedProfileBindings();
            return;
        }
        if (!Array.isArray(results)) throw new Error('伺服器未回傳逐列結果。');
        for (const result of results) {
            const sensorKey = String(result.sensorObjid);
            const line = document.createElement('div');
            line.textContent = `Sensor #${sensorKey}｜${result.status || 'unknown'}${result.status === 'waiting' ? '｜已保存但仍需逐筆核驗' : result.status === 'qualified' ? '｜語意未變，保留既有核驗資格；目前採樣狀態請查看清單' : `｜${result.rejectionReason || (Array.isArray(result.missingFacts) ? result.missingFacts.join(', ') : '請重新載入與核對')}`}`;
            resultsRoot.append(line);
            if (result.status === 'waiting' || result.status === 'qualified') {
                removeCommittedTrustedBindingDraft(sensorKey, submittedBySensor.get(sensorKey));
                trustedBindingBatchDrafts.delete(sensorKey);
                acceptedSensorIds.add(sensorKey);
            }
        }
        action.textContent = `批次完成 ${results.length} 筆；新／變更列等待核驗，語意未變列保留資格；拒絕列保留草稿供核對。`;
        await loadTrustedProfileBindings();
    } catch (error) {
        action.textContent = error?.status === 409
            ? '版本或目錄已改變；批次結果未確認，草稿保留。請先重新載入核對已保存版本，再決定是否重送。'
            : '批次結果未確認；草稿保留。請先重新載入核對已保存版本，再決定是否重送。';
    } finally {
        setTrustedBindingPending(false);
        updateTrustedBindingBatchStatus();
        if (trustedBindingSelectedSensor && acceptedSensorIds.has(trustedBindingSelectedSensor)) {
            const submitted = submittedBySensor.get(trustedBindingSelectedSensor);
            const currentFields = Object.fromEntries(Object.entries(bindingFieldIds).map(([key, id]) => [key,
                document.getElementById(id)?.value ?? '']));
            const hasNewerEditorDraft = trustedBindingDraftDirty && submitted &&
                !bindingEditorFieldsMatchBody(currentFields, submitted);
            if (hasNewerEditorDraft) {
                captureTrustedBindingEditorDraft(trustedBindingSelectedSensor);
                document.getElementById('prtg-profile-binding-action-status').textContent =
                    '批次中先前排入的版本已提交；較新的編輯草稿已保留，仍使用原始 fence，請核對並重新 probe。';
            } else if (committedReceiptReceived) {
                trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
                trustedBindingDraftDirty = false;
                clearTrustedBindingOperationAuthority();
            } else {
                trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
                trustedBindingDraftDirty = false;
                await reloadTrustedBinding(false);
            }
        }
        renderTrustedBindingQualificationControl();
    }
});

document.getElementById('prtg-profile-binding-form')?.addEventListener('submit', async event => {
    event.preventDefault();
    if (!trustedBindingSelectedSensor || !trustedBindingProbe || trustedBindingBusy) return;
    const action = document.getElementById('prtg-profile-binding-action-status');
    const selectedId = document.getElementById(bindingFieldIds.channelObjectId).value;
    const selected = trustedBindingProbe.channels?.find(item => trustedChannelObjectId(item.channelObjectId) === selectedId);
    if (!selected || trustedBindingProbe.channelsTruncated) { action.textContent = '請重新 probe，並從未截斷的來源頻道中明確選擇一個 Channel ID。'; return; }
    if (!String(selected.caption || '').trim()) { action.textContent = '所選頻道缺少來源 caption；無法保存要求 exact caption 的 binding。'; return; }
    setTrustedBindingPending(true); action.textContent = '正在重新讀取 CAS 版本並保存…';
    try {
        const latest = await api.get(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}`, { silent: true });
        const expected = latest || {};
        if (!sameTrustedBindingFence(expected, trustedBindingVersions) || trustedBindingProbe.identityEpoch == null || trustedBindingProbe.channelGeneration == null || expected.expectedIdentityEpoch !== trustedBindingProbe.identityEpoch || expected.expectedChannelGeneration !== trustedBindingProbe.channelGeneration) {
            action.textContent = '設定、政策、綁定或來源 fence 已改變；草稿保留。請明確重新載入並執行 probe 後再保存。'; return;
        }
        const number = id => Number(document.getElementById(id).value);
        const scale = number(bindingFieldIds.scale);
        if (!Number.isFinite(scale) || scale <= 0) { action.textContent = 'Scale 必須是有限正數。'; return; }
        const body = {
            sensorObjid: Number(trustedBindingSelectedSensor), expectedSettingsRevision: expected.expectedSettingsRevision,
            expectedPolicyRevision: expected.expectedPolicyRevision, expectedIdentityEpoch: trustedBindingProbe.identityEpoch,
            expectedChannelGeneration: trustedBindingProbe.channelGeneration, expectedBindingRevision: expected.expectedBindingRevision,
            channelObjectId: selectedId, expectedCaption: selected.caption || '',
            quantity: Number(document.getElementById(bindingFieldIds.quantity).value), unit: document.getElementById(bindingFieldIds.unit).value,
            scale, direction: document.getElementById(bindingFieldIds.direction).value,
            intervalRawUnit: document.getElementById(bindingFieldIds.intervalRawUnit).value,
            rawTimestampTimeZoneId: document.getElementById(bindingFieldIds.rawTimestampTimeZoneId).value,
            analysisTimeZoneId: document.getElementById(bindingFieldIds.analysisTimeZoneId).value,
            timeBasisEvidenceReference: document.getElementById(bindingFieldIds.timeBasisEvidenceReference).value
        };
        const saved = await api.put(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}`, body, { silent: true });
        if (isTrustedBindingCommitReceipt(saved)) {
            const committedIds = new Set(saved.committedSensorObjids.map(String));
            if (committedIds.has(trustedBindingSelectedSensor)) {
                const currentFields = Object.fromEntries(Object.entries(bindingFieldIds).map(([key, id]) => [key,
                    document.getElementById(id)?.value ?? '']));
                const editorStillMatchesSubmitted = bindingEditorFieldsMatchBody(currentFields, body);
                removeCommittedTrustedBindingDraft(trustedBindingSelectedSensor, body);
                if (editorStillMatchesSubmitted) {
                    trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
                    trustedBindingDraftDirty = false;
                    clearTrustedBindingOperationAuthority();
                } else {
                    trustedBindingDraftDirty = true;
                    captureTrustedBindingEditorDraft(trustedBindingSelectedSensor);
                }
            }
            updateTrustedBindingBatchStatus();
            action.textContent = trustedBindingDraftDirty
                ? `已提交 sensor #${trustedBindingSelectedSensor}；較新的編輯草稿仍保留。目錄已變更，請重新載入核對後再 probe，不要直接重送。`
                : `已提交 sensor #${trustedBindingSelectedSensor}；目錄已變更，請重新載入核對，不要直接重送。`;
            await loadTrustedProfileBindings();
            return;
        }
        trustedBindingSaved = saved?.binding || saved;
        trustedBindingDraftDirty = false;
        removeCommittedTrustedBindingDraft(trustedBindingSelectedSensor, body);
        trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
        updateTrustedBindingBatchStatus();
        action.textContent = `已保存 binding revision ${trustedBindingSaved?.bindingRevision ?? '未知'}；狀態 ${trustedBindingSaved?.status || saved?.status || 'waiting'}；語意未變可保留既有資格，新／變更設定仍需來源核驗。`;
        document.getElementById('prtg-profile-binding-qualify').disabled = !trustedBindingSaved?.bindingFingerprint;
        await loadTrustedProfileBindings();
        await reloadTrustedBinding(true, true);
    } catch (error) {
        action.textContent = error?.status === 409
            ? '版本或目錄已改變；保存結果未確認，草稿保留。請先重新載入核對已保存版本，再決定是否重送。'
            : '保存結果未確認；草稿保留。請先重新載入核對已保存版本，再決定是否重送。';
    } finally { setTrustedBindingPending(false); }
});

document.getElementById('prtg-profile-binding-qualify')?.addEventListener('click', async () => {
    if (!trustedBindingSelectedSensor || trustedBindingBusy) return;
    if (trustedBindingDraftDirty) { document.getElementById('prtg-profile-binding-action-status').textContent = '目前草稿尚未保存；先保存並重新核對綁定版本，再執行核驗。'; return; }
    const action = document.getElementById('prtg-profile-binding-action-status');
    setTrustedBindingPending(true); action.textContent = '正在重新讀取 binding 並執行單次有界來源核驗…';
    try {
        const latest = await api.get(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}`, { silent: true });
        const binding = latest?.binding;
        if (!sameTrustedBindingFence(latest, trustedBindingVersions) || !binding?.bindingFingerprint ||
            binding.bindingRevision !== trustedBindingSaved?.bindingRevision ||
            binding.bindingFingerprint !== trustedBindingSaved?.bindingFingerprint) {
            action.textContent = '設定、政策、來源或綁定 fence 已改變；未執行核驗，草稿保留。請明確重新載入並核對。'; return;
        }
        const result = await api.post(`${trustedBindingBase}/bindings/${encodeURIComponent(trustedBindingSelectedSensor)}/qualify`, {
            expectedBindingRevision: binding.bindingRevision, expectedBindingFingerprint: binding.bindingFingerprint
        }, { silent: true, timeoutMs: 30000 });
        if (isTrustedBindingCommitReceipt(result)) {
            trustedBindingDraftDirty = false;
            trustedBindingEditorDrafts.delete(trustedBindingSelectedSensor);
            clearTrustedBindingOperationAuthority();
            action.textContent = `已提交核驗 sensor #${trustedBindingSelectedSensor}；目錄已變更，請重新載入核對，不要直接重送。`;
            await loadTrustedProfileBindings();
            return;
        }
        trustedBindingSaved = result?.binding || result;
        const durableOutcome = trustedQualificationOutcome(result, trustedBindingSelectedSensor);
        if (durableOutcome) {
            action.textContent = durableOutcome.message;
            document.getElementById(durableOutcome.destination)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
            return;
        }
        const qualificationMessage = `核驗結果：${result?.status || trustedBindingSaved?.status || 'unknown'}｜${Array.isArray(result?.missingFacts) ? result.missingFacts.join(', ') : '請查看缺項'}｜來源版本 ${result?.probe?.sourceVersion || trustedBindingSaved?.qualificationSourceVersion || 'unknown'}。`;
        await loadTrustedProfileBindings(); await reloadTrustedBinding(true, true);
        action.textContent = qualificationMessage;
    } catch (error) {
        action.textContent = error?.status === 409
            ? '版本或目錄已改變；核驗結果未確認。請先重新載入核對目前綁定與資格狀態，再決定是否重送。'
            : '核驗結果未確認。請先重新載入核對目前綁定與資格狀態，再決定是否重送。';
    } finally { setTrustedBindingPending(false); }
});

let timelineProgressPage = 1;
let timelineProgressController = null;
let timelineProgressRevision = null;
async function loadTimelineProgress() {
    const root = document.getElementById('prtg-timeline-progress-rows');
    if (!root) return;
    timelineProgressController?.abort();
    const controller = new AbortController();
    timelineProgressController = controller;
    const status = document.getElementById('prtg-timeline-progress-status');
    const cancel = document.getElementById('prtg-timeline-progress-cancel');
    const previous = document.getElementById('prtg-timeline-progress-prev');
    const next = document.getElementById('prtg-timeline-progress-next');
    const pageLabel = document.getElementById('prtg-timeline-progress-page');
    cancel.disabled = false;
    status.textContent = '正在讀取一頁，最多 50 顆 sensor…';
    try {
        const data = await api.get(`/api/prtg/timeline-progress?page=${timelineProgressPage}&pageSize=50`,
            { signal: controller.signal, silent: true });
        if (controller.signal.aborted) return;
        timelineProgressRevision = data.revision || null;
        const hours = document.getElementById('prtg-timeline-bootstrap-hours');
        const resume = document.getElementById('prtg-timeline-bootstrap-resume');
        if (hours) hours.value = String(data.bootstrapDeadlineHours ?? 72);
        if (resume) resume.disabled = data.bootstrapCycleOutcome !== 'deadline-exceeded';
        root.replaceChildren();
        const rows = Array.isArray(data?.rows) ? data.rows : [];
        const labels = {
            complete: '31天歷史狀態涵蓋已累積', 'capacity-unverified': '歷史狀態涵蓋尚未完整驗證', 'capacity-shortfall': '歷史頁數上限不足',
            'waiting-identity': '等待目前資源身分', 'source-scope-stale': '來源範圍待更新',
            malformed: '進度資料無效', 'not-started': '尚未開始'
        };
        for (const row of rows) {
            const line = document.createElement('div');
            line.className = 'small py-1 border-bottom';
            const label = labels[row.bootstrapStatus] || '等待更新';
            const watermark = row.lastCompleteThrough ? `｜完整水位 ${row.lastCompleteThrough}` : '';
            const retry = row.nextAttemptAt ? `｜下次嘗試 ${row.nextAttemptAt}` : '';
            const initial = row.initialCollectionCaptured ? '｜本週期初始查詢完成' : '｜本週期初始查詢待完成';
            line.textContent = `Sensor #${row.sensorId}｜${label}${initial}${watermark}${retry}｜${row.qualityReason || '狀態待核對'}`;
            root.append(line);
        }
        pageLabel.textContent = `第 ${data.page} / ${data.totalPages} 頁｜選取 ${data.selectedSensors} 顆｜版本 ${data.revision || '已核對'}`;
        previous.disabled = data.page <= 1;
        next.disabled = data.page >= data.totalPages;
        const remaining = Number.isFinite(Number(data.bootstrapCycleRemainingSeconds))
            ? Math.ceil(Number(data.bootstrapCycleRemainingSeconds) / 3600) : null;
        const cycle = data.bootstrapCycleOutcome === 'running' && remaining !== null
            ? `週期 ${data.bootstrapCycleOutcome}，期限 ${data.bootstrapCycleDeadlineAtUtc}（剩餘約${remaining}小時）`
            : `週期 ${data.bootstrapCycleOutcome || '尚未開始'}${data.bootstrapCycleReason ? `：${data.bootstrapCycleReason}` : ''}`;
        const prior = data.previousBootstrapCycle
            ? `；前一週期 ${data.previousBootstrapCycle.outcome}（${data.previousBootstrapCycle.deadlineAtUtc || '無期限資料'}）` : '';
        status.textContent = `${cycle}${prior}；目前頁已完成初始收集 ${data.bootstrapCycleCapturedOnPage ?? 0} 顆，選取總數 ${data.bootstrapCycleSelectedSensors || data.selectedSensors} 顆，公平游標 sensor #${data.lastServedSensorId || 0}；成本基準估算 ${Number(data.estimatedBootstrapBaselineHours || 0).toFixed(1)} 小時，預算遙測 ${data.budgetTelemetryStatus || 'unknown'}；最近一輪 ${data.lastRoundOutcome || '尚未執行'}，完成時間 ${data.lastRoundCompletedAt || '尚無'}。估算不是容量保證；各規則品質與暖機條件另行判定。`;
    } catch (error) {
        if (error?.name === 'AbortError') return;
        root.replaceChildren();
        status.textContent = `進度無法讀取：${error?.message || '請重新載入。'} 範圍變更會取消舊頁結果。`;
        pageLabel.textContent = '';
        previous.disabled = true; next.disabled = true;
    } finally {
        if (timelineProgressController === controller) {
            timelineProgressController = null;
            cancel.disabled = true;
        }
    }
}
document.getElementById('prtg-timeline-progress-refresh')?.addEventListener('click', () => loadTimelineProgress());
document.getElementById('prtg-timeline-progress-cancel')?.addEventListener('click', () => timelineProgressController?.abort());
document.getElementById('prtg-timeline-progress-prev')?.addEventListener('click', () => { timelineProgressPage = Math.max(1, timelineProgressPage - 1); loadTimelineProgress(); });
document.getElementById('prtg-timeline-progress-next')?.addEventListener('click', () => { timelineProgressPage++; loadTimelineProgress(); });
document.getElementById('prtg-timeline-bootstrap-save')?.addEventListener('click', async () => {
    const hours = Number(document.getElementById('prtg-timeline-bootstrap-hours')?.value);
    if (!Number.isInteger(hours) || hours < 1 || hours > 720 || !timelineProgressRevision) {
        toast('期限需為1至720小時，請重新載入目前週期。', 'warning'); return;
    }
    try {
        await api.put('/api/prtg/timeline-progress/configuration', {
            deadlineHours: hours, expectedRevision: timelineProgressRevision, page: timelineProgressPage, pageSize: 50
        }, { silent: true });
        toast('期限設定已保存，套用於下一個新週期或明確續行。', 'success');
        await loadTimelineProgress();
    } catch (error) { toast(error?.message || '期限設定未保存；請重新載入。', 'danger'); }
});
document.getElementById('prtg-timeline-bootstrap-resume')?.addEventListener('click', async () => {
    if (!timelineProgressRevision) { toast('請先重新載入目前週期。', 'warning'); return; }
    try {
        await api.post('/api/prtg/timeline-progress/resume', {
            expectedRevision: timelineProgressRevision, page: timelineProgressPage, pageSize: 50
        }, { silent: true });
        toast('已明確續行保存的週期、游標與逐 sensor 前導。', 'success');
        await loadTimelineProgress();
    } catch (error) { toast(error?.message || '週期未續行；請重新載入。', 'danger'); }
});

let scopeRevision = null;
const setupState = { settings: null, settingsLoading: true, connectionTest: null, probe: null, sync: null, rules: null, schedule: null };

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
        return;
    }
    const credential = settings.prtgAuthMode === 'password' ? settings.prtgHasPassword && settings.prtgUsername
        : settings.prtgAuthMode === 'passhash' ? settings.prtgHasPasshash && settings.prtgUsername : settings.prtgHasApiToken;
    const checks = [
        { ok: Boolean(settings.prtgUrl && credential), label: '已儲存的連線位址與認證', next: '設定連線並儲存', tab: 'connection' },
        { ok: setupState.connectionTest === true, label: '本次頁面連線測試', next: '執行連線測試；重新載入後需再確認', tab: 'connection' },
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
        : '設定與最近一次檢查均已確認；請再到資料準備度與使用效果核對實際涵蓋，不能據此宣稱預警已可用。';
}

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
        const hasCount = [summary.lowCoverageSampledHours, summary.prtgFindings, summary.prtgOnlyHostDays, summary.casesCreated, summary.workOrdersCreated,
            summary.workOrdersReplied, summary.suppressedFindings, summary.mutedPrtgProfiles, summary.corroboratedHostDays]
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

/** 目前已儲存的 PRTG 擷取開關。結構同步只更新鏡像，不會變更這個開關。 */
let prtgEnabled = false;
/** 已儲存的位址與認證是否足以啟動結構同步。 */
let prtgStructureConfigured = false;
/** 結構同步是否執行中：開關的閘與執行中的灰掉是同一顆按鈕的兩個理由，任一成立就不能按。 */
let structureSyncRunning = false;
let selectedBackfillTimer = null;
let currentProbeEvidenceJson = null;
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
        if (setupState.settings?.updatedAt !== settings.updatedAt) setupState.connectionTest = null;
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
                `/api/admin/settings/prtg-fetch-scope/estimate?scope=${encodeURIComponent(scope)}&strategy=${encodeURIComponent(strategy)}`,
                { silent: true });

            if (!res.success) {
                result.className = 'text-danger small';
                result.textContent = res.errorMessage || '估算失敗。';
                return;
            }

            if (snapshotResult) {
                const snapBase = `快照（不受數值取數對象影響）：${formatNumber(res.snapshotTargets)} 顆感測器，每天約 ${formatNumber(res.snapshotRowsPerDay)} 列，保留 ${res.snapshotRetentionDays} 天約 ${formatNumber(res.snapshotRowsAtRetention)} 列`;
                const model = res.snapshotCapacityEstimatedSeconds == null
                    ? '尚無足夠同形新樣本' : `p95 ${Number(res.snapshotCapacityP95BatchSeconds).toFixed(1)} 秒/批，估計 ${Number(res.snapshotCapacityEstimatedSeconds).toFixed(0)} 秒`;
                const age = res.snapshotCapacityOldestSampleAgeSeconds == null
                    ? '無樣本時間' : `最舊樣本 ${Number(res.snapshotCapacityOldestSampleAgeSeconds).toFixed(0)} 秒前`;
                const capacity = `快照分項量測 ${res.snapshotCapacityStatus}（${res.snapshotCapacityStrategy}，${formatNumber(res.snapshotTargets)} 顆/${formatNumber(res.snapshotCapacityBatchCount)} 批；${formatNumber(res.snapshotCapacitySamples)} 個新鮮 ${formatNumber(res.snapshotCapacitySampleBatchSize)}-ID 樣本（至少 5 個）；${model}；${age}；原因 ${res.snapshotCapacityReason}；模型 ${res.snapshotCapacityModel}；請求形式 ${res.snapshotCapacityRequestShape}）`;
                const profileCapacity = `Profile 分項量測 ${res.profileCapacityStatus}（目前有效的新鮮樣本 ${formatNumber(res.profileCapacitySamples)} 筆；首次核准及失敗後恢復需 5 筆；p95 ${res.profileCapacityP95SensorSeconds == null ? '—' : Number(res.profileCapacityP95SensorSeconds).toFixed(2) + ' 秒／sensor'}；${res.profileCapacityEstimatedSeconds == null ? '尚無僅傳輸估算' : '僅傳輸試測估算 ' + formatNumber(res.profileCapacityEstimatedSeconds) + ' 秒'}／${formatNumber(res.profileCapacityWindowSeconds)} 秒可用期限；原因 ${res.profileCapacityReason}；請求形式 ${res.profileCapacityRequestShape}）`;
                const jointStatusText = res.jointCapacityStatus === 'capacity-qualified' ? '符合容量條件'
                    : res.jointCapacityStatus === 'capacity-exceeded' ? '超出期限或共享額度' : '待驗證';
                const jointPlanText = res.jointAdmissionPlanMatched
                    ? '符合目前已核准方案' : '候選設定共同試算，儲存或啟用時會重新驗證';
                const jointSeconds = value => value == null || !Number.isFinite(Number(value))
                    ? '—' : formatNumber(Number(value).toFixed(0));
                const jointRate = value => value == null || !Number.isFinite(Number(value))
                    ? '—' : Number(value).toFixed(3);
                const jointCapacity = `共同容量 ${jointStatusText}（${jointPlanText}；原因 ${res.jointCapacityReason}；Table API 共享配額 ${jointRate(res.jointSharedTableRequestsPerSecond)}/秒；快照保留 ${jointRate(res.jointSnapshotTableRequestsPerSecond)}/秒、Profile 保留 ${jointRate(res.jointProfileTableRequestsPerSecond)}/秒、一般請求餘額 ${jointRate(res.jointGeneralResidualTableRequestsPerSecond)}/秒；快照完成估算 ${jointSeconds(res.jointSnapshotEstimatedSeconds)}/${jointSeconds(res.jointSnapshotWindowSeconds)} 秒；Profile 完成估算 ${jointSeconds(res.jointProfileEstimatedSeconds)}/${jointSeconds(res.jointProfileWindowSeconds)} 秒）`;
                if (res.snapshotWarning) {
                    snapshotResult.className = 'text-warning small d-block';
                    snapshotResult.textContent = `⚠ ${snapBase}——${res.snapshotWarning}；${capacity}；${profileCapacity}；${jointCapacity}`;
                } else {
                    snapshotResult.className = res.jointCapacityStatus === 'capacity-qualified'
                        ? 'text-success small d-block'
                        : res.jointCapacityStatus === 'capacity-exceeded' ? 'text-danger small d-block' : 'text-warning small d-block';
                    snapshotResult.textContent = `${snapBase}；${capacity}；${profileCapacity}；${jointCapacity}`;
                }
            }

            if (scope === 'triggered') {
                if (strategy !== 'aggressive') {
                    result.className = 'text-muted small';
                    result.textContent = '保守策略不執行夜間逐 sensor 取數；快照容量估算已列於上方。';
                    return;
                }
                result.className = 'text-muted small';
                result.textContent = '只抓觸發主機：數量逐日變動，事前無法估算。';
                return;
            }

            if (strategy !== 'aggressive') {
                result.className = 'text-muted small';
                result.textContent = '保守策略不執行夜間逐 sensor 取數；快照容量估算已列於上方。';
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

    for (const id of ['prtg-url', 'prtg-auth-mode', 'prtg-username', 'prtg-api-token', 'prtg-password',
        'prtg-passhash', 'prtg-clear-token', 'prtg-clear-password', 'prtg-clear-passhash',
        'prtg-ignore-ssl', 'prtg-timeout-seconds']) {
        document.getElementById(id)?.addEventListener('input', () => {
            setupState.connectionTest = null;
            renderSetupSummary();
        });
    }

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
            setupState.connectionTest = result.success === true && matchesSaved;
            renderSetupSummary();
            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.elapsedMs != null
                ? `${mark} ${result.message}（耗時 ${result.elapsedMs}ms）`
                : `${mark} ${result.message}`;
            if (result.success && !matchesSaved) resultEl.textContent += '；本次測試使用未儲存的表單值，請儲存後重測以確認實際執行設定。';
        } catch (error) {
            setupState.connectionTest = false;
            renderSetupSummary();
            resultEl.className = 'text-danger small';
            resultEl.textContent = `✗ ${error?.message || '測試連線失敗。'}`;
        } finally {
            restore();
        }
    });

    const pilotButton = document.getElementById('prtg-capacity-pilot-btn');
    const pilotResult = document.getElementById('prtg-capacity-pilot-result');
    pilotButton?.addEventListener('click', async () => {
        const restore = withBusy(pilotButton, '試測中');
        pilotResult?.replaceChildren();
        pilotResult?.classList.remove('text-danger', 'text-success', 'text-warning');
        try {
            const res = await api.post('/api/admin/settings/prtg-capacity-pilot', {}, { silent: true });
            if (pilotResult) {
                const model = res.estimatedSeconds == null ? '尚無可用估算' : `估計 ${Number(res.estimatedSeconds).toFixed(0)} 秒`;
                pilotResult.classList.add(res.status === 'capacity-qualified' ? 'text-success' : 'text-warning');
                pilotResult.textContent = `${res.status}：${formatNumber(res.targetCount)} 個目前目標、送出 ${res.requestsSent}/${res.requestsAttempted} 批、${formatNumber(res.matchingFullBatchSamples)} 個新鮮 ${formatNumber(res.capacitySampleBatchSize)}-ID 樣本（至少 5 個），p95 ${res.p95BatchSeconds == null ? '—' : Number(res.p95BatchSeconds).toFixed(1) + ' 秒'}；${model}；覆蓋 ${res.coverage}；形狀 ${res.requestShape}（${res.reason}）。`;
            }
        } catch (error) {
            if (pilotResult) {
                pilotResult.classList.add('text-danger');
                pilotResult.textContent = error?.message || '容量試測失敗。';
            }
        } finally { restore(); }
    });

    const profilePilotButton = document.getElementById('prtg-profile-capacity-pilot-btn');
    const profilePilotResult = document.getElementById('prtg-profile-capacity-pilot-result');
    profilePilotButton?.addEventListener('click', async () => {
        const restore = withBusy(profilePilotButton, '測量中');
        profilePilotResult?.replaceChildren();
        profilePilotResult?.classList.remove('text-danger', 'text-success', 'text-warning');
        try {
            const res = await api.post('/api/prtg/monitoring/trusted-sampling/profile-capacity-pilot', {}, { silent: true });
            if (profilePilotResult) {
                const p95 = res.p95SensorSeconds == null ? '—' : `${Number(res.p95SensorSeconds).toFixed(2)} 秒／sensor`;
                const estimate = res.estimatedSeconds == null ? '尚無可用估算' : `估計 ${formatNumber(res.estimatedSeconds)} 秒`;
                profilePilotResult.classList.add(res.status === 'capacity-qualified' ? 'text-success' : 'text-warning');
                profilePilotResult.textContent = `${res.status}：範圍 ${formatNumber(res.targetSensorCount)} 顆；已發送 ${res.requestsSent}/${res.requestsAttempted} 個 GET；成功樣本 ${res.matchingFreshSuccessfulSamples}/5；p95 ${p95}；${estimate}／${formatNumber(res.completionWindowSeconds)} 秒期限；耗時 ${formatNumber(res.elapsedMilliseconds / 1000)} 秒；${res.reason}。`;
            }
        } catch (error) {
            if (profilePilotResult) {
                profilePilotResult.classList.add('text-danger');
                profilePilotResult.textContent = error?.message || 'Profile 傳輸測量失敗。';
            }
        } finally { restore(); }
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
                    const res = await api.put('/api/admin/settings/prtg-ip-excludes', { ip: item.ip, note: null, expectedScopeRevision: scopeRevision });
                    toast('已排除此 IP', 'success');
                    notifyRemapWarning(res);
                    await Promise.all([
                        refreshPrtgMirror(),
                        refreshConflicts(conflictPage),
                        refreshUnmatched(unmatchedPage),

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
                    const res = await api.put('/api/admin/settings/prtg-ip-excludes', { ip: item.ip, note: null, expectedScopeRevision: scopeRevision });
                    toast('已排除此 IP', 'success');
                    notifyRemapWarning(res);
                    await Promise.all([
                        refreshPrtgMirror(),
                        refreshConflicts(conflictPage),
                        refreshUnmatched(unmatchedPage),

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
    await refreshPrtgMirror();
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
                const res = await api.delete(`/api/admin/settings/prtg-ip-excludes/${encodeURIComponent(item.ip)}?expectedScopeRevision=${scopeRevision}`);
                toast('已移除 IP 排除設定', 'success');
                notifyRemapWarning(res);
                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage),
                    refreshUnmatched(unmatchedPage),

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
                const res = await api.delete(`/api/admin/settings/prtg-manual-map/${item.deviceObjid}?expectedScopeRevision=${scopeRevision}`);
                toast('已移除人工對應', 'success');
                notifyRemapWarning(res);
                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage),
                    refreshUnmatched(unmatchedPage),

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

                await Promise.all([
                    refreshPrtgMirror(),
                    refreshConflicts(conflictPage, { preserveSelection: true }),
                    refreshUnmatched(unmatchedPage),

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
    const readinessEl = document.getElementById('prtg-observation-readiness');
    if (readinessEl) {
        try {
            const preview = await api.get('/api/prtg/observation-readiness', { silent: true });
            readinessEl.textContent = `PRTG 證據核對（近 30 日）：有效快照 ${formatNumber(preview.activeSnapshots)} 筆、尚無日誌附掛對照 ${formatNumber(preview.independentSnapshots)} 筆、與日誌附掛重疊 ${formatNumber(preview.legacyOverlaps)} 筆。來源待確認 ${formatNumber(preview.unknownSourceGenerations)} 筆、資源待確認 ${formatNumber(preview.unknownResourceGenerations)} 筆。快照本身不代表正式問題；只有可信證據與成功 NetIQ 主機日才可補追加，進度請看補追加與通知處理狀態。`;
        } catch {
            readinessEl.textContent = '獨立 PRTG 問題準備度目前無法讀取，請稍後重試。';
        }
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
            `試算時間：${formatDateTime(trial.assessedAtUtc)}；設定版本：${trial.settingsRevision}；規則摘要：${trial.rulesFingerprint}；語意版本：${trial.semanticVersion}。設定或資料變更後請重算。`,
            '這是已儲存門檻與資料的試算；正式判定另須通過 NetIQ 父紀錄、試點身分、來源／資源與語意暖機，試算命中不代表已派工或已通知。',
            `完成日：${trial.completedDay}；資料品質：${trial.dataQuality}（${trial.usableDays}/${trial.requiredDays} 天，${trial.usableHours} 個可用小時；每日需 ${trial.requiredHoursPerDay} 小時）`,
            `語意：${trial.semanticVerified ? '已驗證' : '未驗證'}；排除原因：${trial.exclusion || '無'}`,
            trial.ruleId ? `規則：${trial.ruleId}（${trial.ruleEnabled ? '啟用' : '停用；試算仍使用已儲存門檻'}）` : '規則：未設定',
            trial.lowWaterPercent != null ? `門檻：低水位 ${trial.lowWaterPercent}%；下降至少 ${trial.minimumDeclinePerDay} 百分點／日；預估耗盡 ${trial.maximumDaysToDepletion} 日內；到低水位 ${trial.maximumDaysToLowWater ?? 7} 日內` : null,
            trial.currentAvailablePercent != null ? `趨勢：目前 ${trial.currentAvailablePercent}%；穩健下降 ${trial.declinePerDay ?? '—'} 百分點／日；預估 ${trial.estimatedDaysToDepletion ?? '—'} 日耗盡、${trial.estimatedDaysToLowWater ?? '—'} 日到低水位；預測命中：${trial.predictedHit ? '是' : '否'}` : '趨勢：目前沒有可用的完整趨勢估計',
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
    currentProbeEvidenceJson = status.evidenceJson ?? null;
    setupState.probe = status.isRunning ? null : (status.completedAt ? status.success === true && !status.cancelled : null);
    renderSetupSummary();
    const outputEl = document.getElementById('prtg-probe-output');
    const copyButton = document.getElementById('prtg-probe-copy');
    const downloadButton = document.getElementById('prtg-probe-download');
    const downloadEvidenceButton = document.getElementById('prtg-probe-download-evidence');
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
    if (downloadEvidenceButton) downloadEvidenceButton.disabled = !isProbeEvidenceDownloadable(status.evidenceJson, status.isRunning);

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
        currentProbeEvidenceJson = null;
        const evidenceButton = document.getElementById('prtg-probe-download-evidence');
        if (evidenceButton) evidenceButton.disabled = true;
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
    const downloadEvidenceButton = document.getElementById('prtg-probe-download-evidence');
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
        currentProbeEvidenceJson = null;
        const evidenceButton = document.getElementById('prtg-probe-download-evidence');
        if (evidenceButton) evidenceButton.disabled = true;
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
    downloadEvidenceButton?.addEventListener('click', () => {
        if (downloadEvidenceButton.disabled) return;
        const jsonText = extractProbeEvidenceJson(currentProbeEvidenceJson);
        if (!jsonText) {
            toast('尚未取得合法去識別相容性證據 JSON。', 'warning');
            return;
        }
        const blob = new Blob([jsonText + '\n'], { type: 'application/json;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = `prtg-compatibility-evidence-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
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
    initializePrtgQualificationJobs();
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
    const sourceMode = document.getElementById('prtg-monitoring-source-mode');
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
    let sourcePreviewSignature = null;
    let draftVersion = 0, estimateSequence = 0, sourcePreviewSequence = 0;
    let hostPageSequence = 0, sensorPageSequence = 0, monitoringLoadSequence = 0, previewSequence = 0;
    let hostPageController = null, sensorPageController = null;
    let hostSearchTimer = null, sensorSearchTimer = null;
    let selectionBatchSequence = 0, activeSelectionBatch = null;
    const selectionBatchTimeoutMs = 5 * 60 * 1000;
    function sortedIds(values) { return [...values].map(Number).filter(Number.isSafeInteger).sort((a, b) => a - b); }
    function scopeFingerprint(request) {
        return JSON.stringify([request.revision, request.catalogueToken, request.coreSystemId, request.sourceTimeZoneId, request.sourceCultureName,
            request.sourceChangeMode, request.continuityConfirmed, request.continuityEvidenceReference,
            request.identityConfirmed, sortedIds(new Set(request.hostIds)), sortedIds(new Set(request.sensorIds))]);
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
        draftVersion++; estimateSequence++; sourcePreviewSequence++;
        if (activeSelectionBatch) cancelSelectionBatch(activeSelectionBatch.kind, '範圍草稿已變更，批次已拒絕套用');
        cancelMonitoringPageRequest('host', false); cancelMonitoringPageRequest('sensor', false);
        estimateToken = null; estimateFingerprint = null; sourcePreviewSignature = null;
        document.getElementById('prtg-monitoring-estimate-result').textContent = '範圍或來源資料已變更，請重新估算。';
        updateSaveButton();
    }
    function updateSaveButton() {
        saveButton.disabled = !monitoring?.canEdit || !estimateToken || estimateFingerprint !== scopeFingerprint(monitoringRequest());
    }
    function monitoringRequest() {
        return { revision: monitoring.revision,
            coreSystemId: document.getElementById('prtg-monitoring-core').value,
            sourceTimeZoneId: document.getElementById('prtg-monitoring-zone').value,
            sourceCultureName: document.getElementById('prtg-monitoring-culture').value,
            sourceChangeMode: sourceMode.value,
            continuityConfirmed: document.getElementById('prtg-monitoring-continuity-confirm').checked,
            continuityEvidenceReference: document.getElementById('prtg-monitoring-continuity-evidence').value,
            identityConfirmed: document.getElementById('prtg-monitoring-confirm').checked,
            hostIds: sortedIds(hostSelection), sensorIds: sortedIds(sensorSelection),
            catalogueToken: monitoring.catalogueToken, estimateToken };
    }
    function sourceSignature(request) {
        return JSON.stringify([request.revision, request.coreSystemId, request.sourceTimeZoneId,
            request.sourceCultureName, request.sourceChangeMode, request.continuityConfirmed, request.continuityEvidenceReference,
            sortedIds(new Set(request.hostIds)), sortedIds(new Set(request.sensorIds))]);
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
        sourcePreviewSignature = null; updateSaveButton();
        return estimate;
    }
    async function previewSource(request, sequence = ++sourcePreviewSequence) {
        const requestFingerprint = scopeFingerprint(request);
        const requestVersion = draftVersion;
        const isCurrentRequest = () => sequence === sourcePreviewSequence && requestVersion === draftVersion &&
            requestFingerprint === scopeFingerprint(monitoringRequest());
        try {
            if (!request.estimateToken || estimateFingerprint !== requestFingerprint) {
                const estimate = await estimateScope();
                if (!estimate) throw new Error('範圍在估算期間已變更，請重新預覽目前範圍。');
            }
            if (!isCurrentRequest()) return null;
            request = monitoringRequest();
            const impact = await api.post('/api/prtg/monitoring/source-preview', request);
            if (!isCurrentRequest()) return null;
            document.getElementById('prtg-monitoring-source-impact').textContent =
                `${impact.message} 影響 ${impact.affectedHosts} 台／${impact.affectedSensors} 顆；保存觀察 ${impact.existingObservations} 筆，` +
                `磁碟暖機 ${impact.warmingSensors} 顆，未結 PRTG 交辦 ${impact.openPrtgCases} 件。` +
                `延續資格：${impact.continuationAllowed ? '可提出身分核對證據' : '不符合相同 Core／時間語意，禁止延續'}。`;
            sourcePreviewSignature = sourceSignature(request);
            return impact;
        } catch (error) {
            // A superseded preview must not display its success or failure over the newer draft/request.
            if (!isCurrentRequest()) return null;
            throw error;
        }
    }
    sourceMode.addEventListener('change', () => {
        document.getElementById('prtg-monitoring-core').required = sourceMode.value !== 'unknown';
        invalidateEstimate();
    });
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
    document.getElementById('prtg-monitoring-source-preview').addEventListener('click', async () => {
        if (!monitoring) return;
        const requestVersion = draftVersion;
        const sequence = ++sourcePreviewSequence;
        const requestFingerprint = scopeFingerprint(monitoringRequest());
        document.getElementById('prtg-monitoring-source-impact').textContent = '正在估算目前選取範圍的來源影響…';
        try { await previewSource(monitoringRequest(), sequence); }
        catch (error) {
            if (sequence === sourcePreviewSequence && requestVersion === draftVersion &&
                requestFingerprint === scopeFingerprint(monitoringRequest())) {
                sourcePreviewSignature = null;
                document.getElementById('prtg-monitoring-source-impact').textContent = `影響預覽失敗：${error.message}`;
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
                `本頁操作會超過 ${maximum.toLocaleString()} 筆選取上限；未變更任何選取。`;
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
                        throw new Error(`目前搜尋結果有 ${expectedTotal.toLocaleString()} 筆，超過 ${maximum.toLocaleString()} 筆批次選取上限；請縮小搜尋，未變更選取。`);
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
                    throw new Error(`搜尋結果分頁超過 ${maximum.toLocaleString()} 筆批次上限；請縮小搜尋，未變更選取。`);
                statusBox.textContent = `已讀取 ${Math.min(collected.size, expectedTotal).toLocaleString()}／${expectedTotal.toLocaleString()} 筆；選取尚未變更。`;
            } while (cursor);

            if (!selectionBatchCurrent(batch)) return;
            if (collected.size !== expectedTotal)
                throw new Error(`目錄只完整讀到 ${collected.size}／${expectedTotal} 筆；已拒絕部分結果，未變更選取。`);
            const current = isHost ? hostSelection : sensorSelection;
            const proposed = new Set(current);
            collected.forEach(id => proposed.add(id));
            if (proposed.size > maximum)
                throw new Error(`加入搜尋結果後會超過 ${maximum.toLocaleString()} 筆選取上限；已拒絕整批，未變更選取。`);
            activeSelectionBatch = null;
            updateSelectionBatchUi();
            const added = isHost ? commitHostSelection(proposed) : commitSensorSelection(proposed);
            statusBox.textContent = `已完整加入搜尋結果 ${added.toLocaleString()} 筆；目前選取 ${proposed.size.toLocaleString()} 筆。`;
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
            monitoring = response; draftVersion++; estimateSequence++; sourcePreviewSequence++;
            hostPageSequence++; sensorPageSequence++;
            const initializedDraftVersion = draftVersion;
            sourceMode.value = ''; sourcePreviewSignature = null;
            estimateToken = null; estimateFingerprint = null;
            hostSelection.clear(); monitoring.hostIds.forEach(id => hostSelection.add(Number(id)));
            sensorSelection.clear(); monitoring.sensorIds.forEach(id => sensorSelection.add(Number(id)));
            if (Array.isArray(monitoring.savedHosts) && monitoring.savedHosts.length > 3000)
                throw new Error('已保存主機超過 3,000 筆修復清單上限；拒絕部分載入。');
            savedHostRows = Array.isArray(monitoring.savedHosts) ? monitoring.savedHosts.slice() : [];
            savedHostPageIndex = 0; selectedSensorPageIndex = 0;
            document.getElementById('prtg-monitoring-core').required = true;
            document.getElementById('prtg-monitoring-core').value = monitoring.coreSystemId;
            document.getElementById('prtg-monitoring-zone').value = monitoring.sourceTimeZoneId || monitoring.suggestedTimeZoneId;
            document.getElementById('prtg-monitoring-culture').value = monitoring.sourceCultureName || 'zh-TW';
            const acceptanceHosts = document.getElementById('prtg-acceptance-host');
            acceptanceHosts?.replaceChildren();
            if (acceptanceHosts) for (const hostId of monitoring.hostIds) {
                const entry = document.createElement('option'); entry.value = String(hostId);
                entry.textContent = String(hostId); acceptanceHosts.append(entry);
            }
            for (const input of monitoringForm.querySelectorAll('input,select,button')) input.disabled = !monitoring.canEdit;
            status.textContent = !monitoring.canEdit ? '範圍含不可見主機，請由可見全部試點的管理者修改。'
                : !monitoring.enabled ? 'PRTG 擷取已停用；保留既有來源確認與證據，不發布新判定。'
                : monitoring.ready ? '身分與範圍已確認；個別 sensor 的涵蓋仍需查證，首次確認會開始暖機。'
                : '正式判定未就緒：請核對 Core 身分與明列試點，未確認不能發布 PRTG 風險。';
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
    for (const id of ['prtg-monitoring-core', 'prtg-monitoring-zone', 'prtg-monitoring-culture',
        'prtg-monitoring-continuity-confirm', 'prtg-monitoring-continuity-evidence', 'prtg-monitoring-confirm']) {
        const input = document.getElementById(id);
        input.addEventListener('input', invalidateEstimate); input.addEventListener('change', invalidateEstimate);
    }
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
            if (!request.hostIds.length || !request.sensorIds.length) throw new Error('請至少選取一台主機與一顆 sensor。');
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
            if (sourcePreviewSignature !== sourceSignature(request)) {
                const impact = await previewSource(request);
                if (!impact || requestVersion !== draftVersion || requestFingerprint !== scopeFingerprint(monitoringRequest()))
                    throw new Error('範圍在影響預覽期間已變更，未送出保存。');
                if (impact.changed) {
                    status.textContent = '已列出來源變更影響；請核對處理方式及證據後再次儲存。'; return;
                }
            }
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
    async function showOperations(retry = false) {
        const button = document.getElementById(retry ? 'prtg-operations-retry' : 'prtg-operations-refresh');
        button.disabled = true;
        operationsStatus.textContent = retry ? '正在重試，仍會核對目前範圍與權限…' : '正在讀取處理狀態…';
        try {
            const data = retry ? await api.post('/api/prtg/operations/retry', {}) : await api.get('/api/prtg/operations');
            document.getElementById('prtg-operations-retry').disabled = !data.canRetry;
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
            for (const group of data.supplements) {
                const row = document.createElement('li'); row.textContent = `補追加：${labels[group.status] || group.status} ${group.count} 筆`; list.append(row);
            }
            for (const intent of data.notifications) {
                const row = document.createElement('li');
                row.textContent = `主機 #${intent.hostId}｜${intent.recordDate.slice(0, 10)}｜${labels[intent.status] || intent.status}｜` +
                    `通知設定採用 ${intent.adoptedSettingsRevision || '未知'} → 期望 ${intent.expectedSettingsRevision}｜` +
                    intent.recipients.map(r => `${labels[r.status] || r.status} ${r.count} 位`).join('、');
                list.append(row);
            }
            if (!list.childElementCount) operationsStatus.textContent = '可見範圍內尚無補追加或通知意圖。';
            else operationsStatus.append(list);
        } catch (error) { operationsStatus.textContent = `查詢／重試失敗：${error.message}`; }
        finally { if (!retry) button.disabled = false; }
    }
    document.getElementById('prtg-operations-refresh').addEventListener('click', () => showOperations());
    document.getElementById('prtg-operations-retry').addEventListener('click', () => showOperations(true));
    showOperations();
}

const acceptanceExport = document.getElementById('prtg-acceptance-export');
if (acceptanceExport) {
    const status = document.getElementById('prtg-acceptance-status');
    let acceptanceLabels = [];
    const existing = document.getElementById('prtg-acceptance-existing');
    let acceptanceLabelRequest = 0;
    async function refreshAcceptanceLabels() {
        const requestId = ++acceptanceLabelRequest;
        const hostId = document.getElementById('prtg-acceptance-host')?.value;
        if (!hostId) {
            acceptanceLabels = []; existing.replaceChildren();
            const empty = document.createElement('option'); empty.value = ''; empty.textContent = '新增查證'; existing.append(empty);
            status.textContent = '請先選擇主機，再載入該主機完整查證清單。'; return null;
        }
        try {
            const data = await api.get(`/api/prtg/acceptance/incidents?hostId=${encodeURIComponent(hostId)}`);
            if (requestId !== acceptanceLabelRequest || hostId !== document.getElementById('prtg-acceptance-host')?.value) return null;
            acceptanceLabels = Array.isArray(data.items) ? data.items : [];
            existing.replaceChildren();
            const empty = document.createElement('option'); empty.value = ''; empty.textContent = '新增查證'; existing.append(empty);
            acceptanceLabels.forEach((item, index) => {
                const option = document.createElement('option'); option.value = String(index);
                const outcomeLabel = { occurred: '已發生', prevented: '已介入預防', unknown: '結果未知' }[item.outcome || 'occurred'] || '結果未知';
                option.textContent = `${item.incidentId}｜主機 #${item.hostId}｜${item.confirmedPositive == null ? '待查證' : item.confirmedPositive ? '需要處理' : '誤報'}｜${outcomeLabel}`;
                existing.append(option);
            });
            status.textContent = data.scopeComplete === false
                ? `主機 #${hostId} 的查證清單不完整（${data.scopeStatus || '資料上限或格式錯誤'}）；統計已停用。`
                : `主機 #${hostId} 已載入 ${data.itemsTotal ?? acceptanceLabels.length} 筆完整查證。`;
            return data;
        } catch (error) {
            if (requestId !== acceptanceLabelRequest || hostId !== document.getElementById('prtg-acceptance-host')?.value) return null;
            acceptanceLabels = []; existing.replaceChildren();
            const empty = document.createElement('option'); empty.value = ''; empty.textContent = '新增查證'; existing.append(empty);
            status.textContent = `查證清單讀取失敗：${error.message}`; return null;
        }
    }
    document.getElementById('prtg-acceptance-host')?.addEventListener('change', () => {
        existing.value = ''; const form = document.getElementById('prtg-acceptance-label-form'); const hostId = document.getElementById('prtg-acceptance-host').value;
        form.reset(); document.getElementById('prtg-acceptance-host').value = hostId; refreshAcceptanceLabels();
    });
    existing.addEventListener('change', () => {
        const form = document.getElementById('prtg-acceptance-label-form');
        if (existing.value === '') { const hostId = document.getElementById('prtg-acceptance-host').value; form.reset(); document.getElementById('prtg-acceptance-host').value = hostId; return; }
        const item = acceptanceLabels[Number(existing.value)];
        const set = (id, value) => { document.getElementById(`prtg-acceptance-${id}`).value = value ?? ''; };
        for (const [id, key] of Object.entries({ 'incident-id': 'incidentId', host: 'hostId', evidence: 'evidenceReference', reason: 'reason',
            segment: 'segment', outcome: 'outcome', action: 'actionDetails', before: 'beforeMeasurement', after: 'afterMeasurement',
            'cost-netiq': 'netiqVerificationMinutes', 'cost-native': 'nativePrtgVerificationMinutes', 'cost-union': 'simpleUnionVerificationMinutes',
            'cost-combined': 'combinedVerificationMinutes' })) set(id, item[key]);
        set('outcome', item.outcome || 'occurred'); set('result', item.confirmedPositive == null ? '' : String(item.confirmedPositive));
        for (const [id, key] of Object.entries({ occurred: 'occurredAt', predicted: 'predictedImpactAt', netiq: 'netiqActionableAt',
            native: 'nativePrtgActionableAt', union: 'simpleUnionActionableAt', combined: 'combinedActionableAt',
            available: 'combinedEvidenceAvailableAt', disposition: 'dispositionAt' })) {
            const at = item[key] ? new Date(item[key]) : null;
            set(id, at ? `${localDateInputValue(at)}T${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}` : '');
        }
        status.textContent = '已載入既有查證；修改後按保存，會更新同主機／事故識別。';
    });
    document.getElementById('prtg-acceptance-labels-refresh').addEventListener('click', refreshAcceptanceLabels);
    if (document.getElementById('prtg-acceptance-host')?.value) refreshAcceptanceLabels();
    acceptanceExport.addEventListener('click', async () => {
        const from = document.getElementById('prtg-effectiveness-from').value;
        const through = document.getElementById('prtg-effectiveness-through').value;
        if (!from || !through || from > through) { status.textContent = '請先選擇有效的起訖日期。'; return; }
        acceptanceExport.disabled = true;
        status.textContent = '正在準備證據包，完成傳輸後才會提供下載。';
        try {
            const blob = await api.downloadJson(`/api/prtg/acceptance/export?from=${encodeURIComponent(from)}&through=${encodeURIComponent(through)}`,
                { timeoutMs: 120000 });
            const objectUrl = URL.createObjectURL(blob);
            try {
                const link = document.createElement('a');
                link.href = objectUrl; link.download = `prtg-acceptance-${from}-${through}.json`; link.click();
            } finally { setTimeout(() => URL.revokeObjectURL(objectUrl), 1000); }
            status.textContent = '證據包已備妥並交給瀏覽器下載；包內 ScopeComplete 為 false 時代表有資料超限、缺失或無法驗證，請依 ScopeStatus 核對後再使用。';
        } catch (error) { status.textContent = `下載失敗：${error.message}`; }
        finally { acceptanceExport.disabled = false; }
    });
    document.getElementById('prtg-acceptance-label-form').addEventListener('submit', async event => {
        event.preventDefault(); const button = event.target.querySelector('button'); button.disabled = true;
        const submittedHostId = document.getElementById('prtg-acceptance-host').value;
        const isSubmittedHostCurrent = () => submittedHostId === document.getElementById('prtg-acceptance-host').value;
        try {
            const value = id => document.getElementById(`prtg-acceptance-${id}`).value;
            const cost = id => value(`cost-${id}`) === '' ? null : Number(value(`cost-${id}`));
            const time = id => value(id) ? new Date(value(id)).toISOString() : null;
            const incident = { incidentId: value('incident-id'), hostId: Number(value('host')),
                outcome: value('outcome'), predictedImpactAt: time('predicted'), actionDetails: value('action'),
                beforeMeasurement: value('before'), afterMeasurement: value('after'), occurredAt: time('occurred'), confirmedPositive: value('result') === '' ? null : value('result') === 'true',
                evidenceReference: value('evidence'), reason: value('reason'), segment: value('segment'),
                netiqActionableAt: time('netiq'), nativePrtgActionableAt: time('native'),
                simpleUnionActionableAt: time('union'), combinedActionableAt: time('combined'),
                combinedEvidenceAvailableAt: time('available'), dispositionAt: time('disposition'),
                netiqVerificationMinutes: cost('netiq'), nativePrtgVerificationMinutes: cost('native'),
                simpleUnionVerificationMinutes: cost('union'), combinedVerificationMinutes: cost('combined') };
            const data = await api.put('/api/prtg/acceptance/incidents', incident);
            if (!isSubmittedHostCurrent()) return;
            await refreshAcceptanceLabels();
            if (!isSubmittedHostCurrent()) return;
            const c = data.comparison;
            if (data.scopeComplete !== true || !c) {
                status.textContent = `已保存；主機查證範圍不完整（${data.scopeStatus || '未知'}），不顯示部分統計。`;
            } else {
                status.textContent = `已保存。主機查證 ${c.incidents} 筆、待查證 ${c.unreviewed} 筆、預防案例 ${c.prevented} 筆、結果未知 ${c.outcomeUnknown} 筆、真陽性 ${c.confirmedPositive} 筆、誤報 ${c.confirmedFalsePositive} 筆；具當時證據 ${c.evidenceQualified} 筆、完整基準 ${c.fullyCompared} 筆、比全部基準提早 ${c.incrementalBeforeAllBaselines} 筆。${c.limitations}`;
            }
        } catch (error) { if (isSubmittedHostCurrent()) status.textContent = `保存失敗：${error.message}`; }
        finally { button.disabled = false; }
    });
}
