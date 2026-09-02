/**
 * 校準數值匯出頁面模組（任務 A4）
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { toast, withBusy } from '../core/ui.js';
import { formatNumber } from '../core/format.js';

let latestStatusData = null;

function getBadgeClass(status) {
    switch (status) {
        case 'Sufficient':
            return 'badge bg-success';
        case 'Available':
            return 'badge bg-info text-dark';
        case 'Insufficient':
            return 'badge bg-warning text-dark';
        case 'Unavailable':
            return 'badge bg-secondary';
        default:
            return 'badge bg-secondary';
    }
}

function createMetricRow(label, value) {
    const row = document.createElement('div');
    row.className = 'd-flex justify-content-between align-items-center py-1 border-bottom border-light small';

    const labelSpan = document.createElement('span');
    labelSpan.className = 'text-muted';
    labelSpan.textContent = label;

    const valueSpan = document.createElement('span');
    valueSpan.className = 'fw-semibold font-monospace';
    valueSpan.textContent = String(value);

    row.append(labelSpan, valueSpan);
    return row;
}

function renderExplanations(containerId, list) {
    const container = document.getElementById(containerId);
    if (!container) return;
    container.replaceChildren();

    if (!list || list.length === 0) {
        const li = document.createElement('li');
        li.className = 'text-muted';
        li.textContent = '無特殊說明';
        container.appendChild(li);
        return;
    }

    for (const text of list) {
        const li = document.createElement('li');
        li.className = 'mb-1';
        li.textContent = text;
        container.appendChild(li);
    }
}

function renderCard(cardKey, itemData, metricLabels) {
    if (!itemData) return;

    const badge = document.getElementById(`badge-${cardKey}`);
    if (badge) {
        badge.className = getBadgeClass(itemData.status);
        badge.textContent = itemData.statusText || itemData.status;
    }

    const metricsContainer = document.getElementById(`metrics-${cardKey}`);
    if (metricsContainer) {
        metricsContainer.replaceChildren();
        if (itemData.keyMetrics) {
            for (const [key, label] of metricLabels) {
                if (key in itemData.keyMetrics) {
                    const rawVal = itemData.keyMetrics[key];
                    const displayVal = typeof rawVal === 'number' ? formatNumber(rawVal) : String(rawVal);
                    metricsContainer.appendChild(createMetricRow(label, displayVal));
                }
            }
        }
    }

    renderExplanations(`explanations-${cardKey}`, itemData.explanations);
}

function updateExportButtonState() {
    const exportBtn = document.getElementById('calibration-export-btn');
    const overrideCheck = document.getElementById('calibration-override-check');
    if (!exportBtn || !overrideCheck) return;

    if (!latestStatusData) {
        exportBtn.disabled = true;
        return;
    }

    if (latestStatusData.canExport) {
        exportBtn.disabled = false;
    } else {
        exportBtn.disabled = !overrideCheck.checked;
    }
}

function renderAssessment(data) {
    latestStatusData = data;

    // 1. PRTG 值型基線
    renderCard('prtg-value-baseline', data.prtgValueBaseline, [
        ['WhitelistedSensors', '白名單感測器數'],
        ['MappedHosts', '已對應主機數'],
        ['MaxCoverageDays', '最長涵蓋天數'],
        ['HostsReachingAvailable', '達可用標準主機數'],
        ['HostsReachingSufficient', '達充足標準主機數']
    ]);

    // 2. PRTG 規則門檻
    renderCard('prtg-rule-thresholds', data.prtgRuleThresholds, [
        ['DistinctCoverageDays', '變更涵蓋天數'],
        ['TotalRuleHits', '規則命中總筆數']
    ]);

    // 3. 觸發式取數量級
    renderCard('triggered-fetch-magnitude', data.triggeredFetchMagnitude, [
        ['DaysWithValues', '有數值天數'],
        ['WindowDays', '評估視窗天數']
    ]);

    // 4. 殘留判定門檻
    renderCard('residual-credential-thresholds', data.residualCredentialThresholds, [
        ['CandidateHostDays', '候選主機日數'],
        ['DistinctCoverageDays', '相異涵蓋天數']
    ]);

    const hint = document.getElementById('calibration-status-hint');
    if (hint) {
        const now = new Date();
        const timeStr = now.toLocaleTimeString('zh-TW', { hour12: false });
        if (data.canExport) {
            hint.className = 'text-success small fw-semibold';
            hint.textContent = `計算完成（${timeStr}）：四項指標皆達標，可直接匯出`;
        } else {
            hint.className = 'text-warning small fw-semibold';
            hint.textContent = `計算完成（${timeStr}）：部分指標未達標，需勾選覆寫才可匯出`;
        }
    }

    updateExportButtonState();
}

async function runAssessment() {
    const calcBtn = document.getElementById('calibration-calc-btn');
    const restore = withBusy(calcBtn, '計算中…');

    try {
        const data = await api.get(`/api/admin/calibration/status`);
        renderAssessment(data);
        toast('校準指標評估計算完成', 'success');
    } catch (error) {
        toast(error?.message || '計算失敗，請稍後再試。', 'danger');
    } finally {
        restore();
    }
}

async function downloadPackage() {
    const exportBtn = document.getElementById('calibration-export-btn');
    const overrideCheck = document.getElementById('calibration-override-check');
    const isOverride = overrideCheck ? overrideCheck.checked : false;

    const restore = withBusy(exportBtn, '匯出中…');

    try {
        const downloadUrl = appUrl(`/api/admin/calibration/export?override=${isOverride ? 'true' : 'false'}`);
        const response = await fetch(downloadUrl, {
            method: 'GET',
            headers: {
                'Accept': 'application/json'
            },
            credentials: 'same-origin'
        });

        if (!response.ok) {
            let errorMsg = '匯出失敗，請稍後再試。';
            try {
                const payload = await response.json();
                if (payload?.error?.message) {
                    errorMsg = payload.error.message;
                }
            } catch {
                // 忽略非 JSON 錯誤回應
            }
            toast(errorMsg, 'danger');
            return;
        }

        const blob = await response.blob();
        const disposition = response.headers.get('Content-Disposition');
        let filename = `calibration-${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.json`;

        if (disposition && disposition.includes('filename=')) {
            const match = disposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
            if (match && match[1]) {
                filename = match[1].replace(/['"]/g, '');
            }
        }

        const objectUrl = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = objectUrl;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(objectUrl);

        toast('校準數值封裝檔已成功匯出', 'success');
    } catch (error) {
        toast(error?.message || '下載失敗，請稍後再試。', 'danger');
    } finally {
        restore();
    }
}

function bindEvents() {
    const calcBtn = document.getElementById('calibration-calc-btn');
    calcBtn?.addEventListener('click', runAssessment);

    const overrideCheck = document.getElementById('calibration-override-check');
    overrideCheck?.addEventListener('change', updateExportButtonState);

    const exportBtn = document.getElementById('calibration-export-btn');
    exportBtn?.addEventListener('click', downloadPackage);
}

function init() {
    bindEvents();
}

init();
