/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

// 由重到輕，與問題查詢／詳情頁的嚴重度篩選同一個順序
const SEVERITIES = ['Critical', 'High', 'Medium', 'Low'];

let current = null;

async function load() {
    current = await api.get('/api/admin/settings');
    renderSeverityChecks(current.unhandledSeverities);
    renderAiFields(current);
    renderRetentionFields(current);
    renderUpdatedAt(current);
}

function renderSeverityChecks(selected) {
    const container = document.getElementById('severity-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const severity of SEVERITIES) {
        const wrap = document.createElement('div');
        wrap.className = 'form-check';

        const input = document.createElement('input');
        input.type = 'checkbox';
        input.className = 'form-check-input';
        input.id = `severity-${severity}`;
        input.dataset.severity = severity;
        input.checked = selectedSet.has(severity);

        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = severity;

        wrap.append(input, label);
        container.appendChild(wrap);
    }
}

function renderAiFields(settings) {
    document.getElementById('ai-base-url').value = settings.aiBaseUrl ?? '';

    const apiKeyInput = document.getElementById('ai-api-key');
    apiKeyInput.value = '';

    const hint = document.getElementById('ai-api-key-hint');
    hint.textContent = settings.aiHasApiKey
        ? '已設定金鑰；留空儲存＝沿用既有金鑰，輸入新值才會覆蓋。'
        : '尚未設定金鑰；地端無驗證的端點可留空。';

    document.getElementById('ai-api-key-clear').checked = false;
}

function renderRetentionFields(settings) {
    document.getElementById('initial-history-days').value = settings.initialHistoryDays;
    document.getElementById('retention-days').value = settings.retentionDays;
}

function renderUpdatedAt(settings) {
    const el = document.getElementById('settings-updated');
    if (!settings.updatedAt) {
        el.textContent = '尚未有人更新過（目前為系統預設值）。';
        return;
    }
    el.textContent = `最後更新：${formatDateTime(settings.updatedAt)}` +
        (settings.updatedByAccount ? `　更新者：${settings.updatedByAccount}` : '');
}

function collectSeverities() {
    return [...document.querySelectorAll('#severity-checks input[type="checkbox"]')]
        .filter(input => input.checked)
        .map(input => input.dataset.severity);
}

function bindForm() {
    const form = document.getElementById('settings-form');
    const saveButton = document.getElementById('settings-save');

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const severities = collectSeverities();
        if (severities.length === 0) {
            toast('請至少勾選一個未處理等級。', 'warning');
            return;
        }

        const initialHistoryDays = Number(document.getElementById('initial-history-days').value);
        const retentionDays = Number(document.getElementById('retention-days').value);
        if (retentionDays < initialHistoryDays) {
            toast('歷史資料保留天數不可小於首次執行回補天數。', 'warning');
            return;
        }

        const apiKey = document.getElementById('ai-api-key').value;
        const clearApiKey = document.getElementById('ai-api-key-clear').checked;

        const restore = withBusy(saveButton, '儲存中');
        try {
            current = await api.put('/api/admin/settings', {
                unhandledSeverities: severities,
                aiBaseUrl: document.getElementById('ai-base-url').value.trim(),
                aiApiKey: apiKey || null,
                clearAiApiKey: clearApiKey,
                initialHistoryDays,
                retentionDays
            });
            toast('已儲存設定', 'success');
            renderAiFields(current);
            renderUpdatedAt(current);
        } finally {
            restore();
        }
    });
}

bindForm();
load();
