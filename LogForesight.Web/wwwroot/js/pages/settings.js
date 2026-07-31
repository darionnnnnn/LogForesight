/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy, trackUnsaved, bindTabs } from '../core/ui.js';
import { formatDateTime, severityName, SEVERITY_ORDER } from '../core/format.js';

// 未儲存提醒（docs/HISTORY.md #2）：MPA 站台離開頁面前用瀏覽器原生確認攔一次。
// AD 測試帳密／測試連線按鈕不算「設定內容」——測完即丟，不該觸發離開提醒
let unsaved = null;

// 兩段式（docs/HISTORY.md #5，2026-07-27 簡化自三段式）：
// 過濾機制已收斂到後端 RecordRepository 單一咽喉點，SiteHidden 對全站一致生效，沒有例外頁
const DISPLAY_MODES = [
    {
        value: 'DefaultHidden', label: '預設隱藏，仍可手動開啟',
        hint: '風險日詳情頁預設只顯示勾選層級，未勾選層級的篩選鈕仍在，使用者可自行點開查看；' +
            '儀表板、報表與問題查詢頁的統計不受影響（仍計入全部層級）。'
    },
    {
        value: 'SiteHidden', label: '全站隱藏',
        hint: '未勾選層級的問題在全站一律不計入、不顯示：風險日詳情、詢問 AI 下拉、儀表板風險類型卡、' +
            '報表統計、問題查詢頁的下鑽全部同一套過濾，沒有例外頁。'
    }
];

// 日風險等級（docs/FEEDBACK-3-PLAN.md #8）：中文順序對齊後端 RiskLevels.All（高/中/低，重到輕）
const DAY_RISK_LEVELS = ['高', '中', '低'];

let current = null;

async function load() {
    current = await api.get('/api/admin/settings');
    renderSeverityChecks(current.unhandledSeverities);
    renderDisplayModeButtons(current.severityDisplayMode);
    renderDayRiskLevelChecks(current.visibleDayRiskLevels);
    renderAiFields(current);
    renderAdFields(current);
    renderRetentionFields(current);
    renderUpdatedAt(current);
}

// 按鈕反白樣式沿用風險日詳情頁的嚴重度篩選鈕（record-detail.js renderSeverityFilter），
// 全站「選擇嚴重度層級」的操作語彙一致
function renderSeverityChecks(selected) {
    const container = document.getElementById('severity-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const severity of SEVERITY_ORDER) {
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

/**
 * 日風險等級顯示（docs/FEEDBACK-3-PLAN.md #8）：與上方的問題嚴重度是不同的兩套層級，
 * 按鈕視覺沿用同一套語彙（severity-checks 的樣式），但「高」鎖定為恆選——
 * 全部隱藏會讓儀表板永遠空白，這裡直接用 disabled 擋掉互動，而不是等送出時才報錯。
 */
function renderDayRiskLevelChecks(selected) {
    const container = document.getElementById('day-risk-level-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const level of DAY_RISK_LEVELS) {
        const locked = level === '高';
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (selectedSet.has(level) || locked ? ' active' : '');
        btn.setAttribute('aria-pressed', String(selectedSet.has(level) || locked));
        btn.dataset.riskLevel = level;
        btn.textContent = level;

        if (locked) {
            btn.disabled = true;
            btn.title = '「高風險日」為必要顯示項目，無法取消勾選';
        } else {
            btn.addEventListener('click', () => {
                const active = btn.classList.toggle('active');
                btn.setAttribute('aria-pressed', String(active));
            });
        }
        container.appendChild(btn);
    }
}

function collectDayRiskLevels() {
    return [...document.querySelectorAll('#day-risk-level-checks button.active')]
        .map(btn => btn.dataset.riskLevel);
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

/** docs/HISTORY.md #9：AD 驗證設定——伺服器一行一台，測試帳密欄位不預填（每次都要重新輸入） */
function renderAdFields(settings) {
    document.getElementById('ad-auth-enabled').checked = settings.adAuthEnabled;
    document.getElementById('ad-servers').value = (settings.adServers ?? []).join('\n');
    document.getElementById('ad-search-base').value = settings.adSearchBase ?? '';
    document.getElementById('ad-search-filter').value = settings.adSearchFilter ?? '';

    document.getElementById('ad-test-account').value = '';
    document.getElementById('ad-test-password').value = '';
    document.getElementById('ad-test-result').replaceChildren();
}

/** 一行一台，去除空白行——與後端 SystemSettingsService.NormalizeAdServers 對齊的寬鬆解析 */
function collectAdServers() {
    return document.getElementById('ad-servers').value
        .split('\n')
        .map(s => s.trim())
        .filter(s => s.length > 0);
}

function renderRetentionFields(settings) {
    document.getElementById('initial-history-days').value = settings.initialHistoryDays;
    document.getElementById('retention-days').value = settings.retentionDays;
    document.getElementById('run-log-retention-days').value = settings.runLogRetentionDays;
    document.getElementById('audit-retention-days').value = settings.auditRetentionDays;
    document.getElementById('risky-event-retention-days').value = settings.riskyEventRetentionDays;
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

/**
 * 頁籤化後（docs/FEEDBACK-5-PLAN.md §9），驗證失敗的欄位可能在非作用中頁籤——
 * 先切過去再顯示 toast，否則使用者看不到出錯的欄位在哪。#settings-tabs 位於
 * <form> 外（避免點頁籤誤觸 trackUnsaved 的未儲存提醒），用 data-panel 反查對應頁籤。
 */
function activateTabForElement(el) {
    const panelName = el?.closest('[data-panel]')?.dataset.panel;
    if (!panelName) return;
    document.querySelector(`#settings-tabs [data-tab="${panelName}"]`)?.click();
}

function bindForm() {
    const form = document.getElementById('settings-form');
    const saveButton = document.getElementById('settings-save');

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const severities = collectSeverities();
        if (severities.length === 0) {
            activateTabForElement(document.getElementById('severity-checks'));
            toast('請至少選擇一個層級。', 'warning');
            return;
        }

        const initialHistoryDays = Number(document.getElementById('initial-history-days').value);
        const retentionDays = Number(document.getElementById('retention-days').value);
        if (retentionDays < initialHistoryDays) {
            activateTabForElement(document.getElementById('retention-days'));
            toast('歷史資料保留天數不可小於首次執行回補天數。', 'warning');
            return;
        }

        const runLogRetentionDays = Number(document.getElementById('run-log-retention-days').value);
        const auditRetentionDays = Number(document.getElementById('audit-retention-days').value);
        const riskyEventRetentionDays = Number(document.getElementById('risky-event-retention-days').value);
        if (riskyEventRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('risky-event-retention-days'));
            toast('風險 log 暫存保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        const apiKey = document.getElementById('ai-api-key').value;
        const clearApiKey = document.getElementById('ai-api-key-clear').checked;

        const adAuthEnabled = document.getElementById('ad-auth-enabled').checked;
        const adServers = collectAdServers();
        if (adAuthEnabled && adServers.length === 0) {
            activateTabForElement(document.getElementById('ad-servers'));
            toast('啟用 AD 驗證時，請至少輸入一台 AD 伺服器。', 'warning');
            return;
        }

        const restore = withBusy(saveButton, '儲存中');
        try {
            current = await api.put('/api/admin/settings', {
                unhandledSeverities: severities,
                severityDisplayMode: collectDisplayMode(),
                visibleDayRiskLevels: collectDayRiskLevels(),
                aiBaseUrl: document.getElementById('ai-base-url').value.trim(),
                aiApiKey: apiKey || null,
                clearAiApiKey: clearApiKey,
                initialHistoryDays,
                retentionDays,
                runLogRetentionDays,
                auditRetentionDays,
                riskyEventRetentionDays,
                adAuthEnabled,
                adServers,
                adSearchBase: document.getElementById('ad-search-base').value.trim(),
                adSearchFilter: document.getElementById('ad-search-filter').value.trim()
            });
            toast('已儲存設定', 'success');
            renderAiFields(current);
            renderAdFields(current);
            renderUpdatedAt(current);
            unsaved?.clear();
        } finally {
            restore();
        }
    });
}

/**
 * AD 測試連線（docs/HISTORY.md #9）：用表單目前填的值（不需先儲存）＋
 * 管理者當場輸入的帳密試 bind。這裡是管理者對自己測試，失敗訊息可以顯示細節
 * （與一般登入一律「帳號或密碼錯誤」不同）。
 */
function bindAdTest() {
    const button = document.getElementById('ad-test-btn');

    button.addEventListener('click', async () => {
        const servers = collectAdServers();
        if (servers.length === 0) {
            toast('請至少輸入一台 AD 伺服器。', 'warning');
            return;
        }

        const account = document.getElementById('ad-test-account').value.trim();
        const password = document.getElementById('ad-test-password').value;
        if (!account || !password) {
            toast('請輸入測試帳號與密碼。', 'warning');
            return;
        }

        const resultEl = document.getElementById('ad-test-result');
        const restore = withBusy(button, '測試中');
        try {
            const result = await api.post('/api/admin/settings/ad-test', {
                servers,
                searchBase: document.getElementById('ad-search-base').value.trim() || null,
                searchFilter: document.getElementById('ad-search-filter').value.trim() || null,
                account,
                password
            }, { silent: true });

            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.message;
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = error?.message || '測試連線失敗。';
        } finally {
            // 密碼欄不保留：每次測試都要求重新輸入，降低殘留在畫面上的機會
            document.getElementById('ad-test-password').value = '';
            restore();
        }
    });
}

bindForm();
bindAdTest();
// #settings-tabs 在 <form> 外面，切頁籤的點擊不會冒泡進表單的 trackUnsaved 監聽器，
// 不需要額外排除——見 activateTabForElement 的說明
bindTabs(document.getElementById('settings-tabs'));
unsaved = trackUnsaved(document.getElementById('settings-form'), {
    excludeSelector: '#ad-test-account, #ad-test-password, #ad-test-btn, #ad-test-result'
});
load();
