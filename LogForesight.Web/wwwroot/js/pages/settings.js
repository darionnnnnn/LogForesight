/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy } from '../core/ui.js';
import { formatDateTime, severityName } from '../core/format.js';

// 由重到輕，與問題查詢／詳情頁的嚴重度篩選同一個順序
const SEVERITIES = ['Critical', 'High', 'Medium', 'Low'];

// 三段式：預設隱藏可手動開啟／完全隱藏無法開啟／全站徹底過濾（後端查詢層排除）
const DISPLAY_MODES = [
    { value: 'DefaultHidden', label: '預設隱藏，仍可手動開啟', hint: '風險日詳情頁預設只顯示勾選層級，未勾選層級的篩選鈕仍在，使用者可自行點開查看。' },
    { value: 'Locked', label: '完全隱藏，無法開啟', hint: '風險日詳情頁的重點問題列表完全不出現未勾選層級的問題，對應篩選鈕也不會顯示。' },
    { value: 'GlobalFilter', label: '全站徹底過濾', hint: '風險日詳情、儀表板風險類型卡、報表統計全部只計入勾選層級；問題查詢頁的下鑽篩選不受此模式影響。' }
];

let current = null;

async function load() {
    current = await api.get('/api/admin/settings');
    renderSeverityChecks(current.unhandledSeverities);
    renderDisplayModeButtons(current.severityDisplayMode);
    renderAiFields(current);
    renderRetentionFields(current);
    renderUpdatedAt(current);
}

// 按鈕反白樣式沿用風險日詳情頁的嚴重度篩選鈕（record-detail.js renderSeverityFilter），
// 全站「選擇嚴重度層級」的操作語彙一致
function renderSeverityChecks(selected) {
    const container = document.getElementById('severity-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const severity of SEVERITIES) {
        const btn = document.createElement('button');
        btn.type = 'button'; // 按鈕在 <form> 內，不加 type=button 會被當成 submit 誤觸儲存
        btn.className = 'btn btn-outline-secondary' + (selectedSet.has(severity) ? ' active' : '');
        btn.setAttribute('aria-pressed', String(selectedSet.has(severity)));
        btn.dataset.severity = severity;
        btn.textContent = severityName(severity);
        btn.addEventListener('click', () => {
            const active = btn.classList.toggle('active');
            btn.setAttribute('aria-pressed', String(active));
        });
        container.appendChild(btn);
    }
}

// 單選（非多選）：點擊即切到該模式，同組其餘按鈕自動取消 active
function renderDisplayModeButtons(selectedMode) {
    const container = document.getElementById('severity-display-mode');
    const hintEl = document.getElementById('severity-display-mode-hint');
    container.replaceChildren();

    const updateHint = mode => {
        hintEl.textContent = DISPLAY_MODES.find(m => m.value === mode)?.hint ?? '';
    };

    for (const mode of DISPLAY_MODES) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (mode.value === selectedMode ? ' active' : '');
        btn.dataset.mode = mode.value;
        btn.textContent = mode.label;
        btn.addEventListener('click', () => {
            container.querySelectorAll('button').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            updateHint(mode.value);
        });
        container.appendChild(btn);
    }
    updateHint(selectedMode);
}

function collectDisplayMode() {
    return document.querySelector('#severity-display-mode button.active')?.dataset.mode ?? 'DefaultHidden';
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
    document.getElementById('run-log-retention-days').value = settings.runLogRetentionDays;
    document.getElementById('audit-retention-days').value = settings.auditRetentionDays;
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
    return [...document.querySelectorAll('#severity-checks button.active')]
        .map(btn => btn.dataset.severity);
}

function bindForm() {
    const form = document.getElementById('settings-form');
    const saveButton = document.getElementById('settings-save');

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const severities = collectSeverities();
        if (severities.length === 0) {
            toast('請至少選擇一個層級。', 'warning');
            return;
        }

        const initialHistoryDays = Number(document.getElementById('initial-history-days').value);
        const retentionDays = Number(document.getElementById('retention-days').value);
        if (retentionDays < initialHistoryDays) {
            toast('歷史資料保留天數不可小於首次執行回補天數。', 'warning');
            return;
        }

        const runLogRetentionDays = Number(document.getElementById('run-log-retention-days').value);
        const auditRetentionDays = Number(document.getElementById('audit-retention-days').value);

        const apiKey = document.getElementById('ai-api-key').value;
        const clearApiKey = document.getElementById('ai-api-key-clear').checked;

        const restore = withBusy(saveButton, '儲存中');
        try {
            current = await api.put('/api/admin/settings', {
                unhandledSeverities: severities,
                severityDisplayMode: collectDisplayMode(),
                aiBaseUrl: document.getElementById('ai-base-url').value.trim(),
                aiApiKey: apiKey || null,
                clearAiApiKey: clearApiKey,
                initialHistoryDays,
                retentionDays,
                runLogRetentionDays,
                auditRetentionDays
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
