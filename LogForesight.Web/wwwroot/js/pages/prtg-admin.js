/**
 * PRTG 維護（「系統管理 > PRTG 維護」頁）：連線設定、鏡像狀態與環境探測。
 */

import { api } from '../core/api.js';
import { bindTabs, toast, withBusy } from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

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
            const timeoutSeconds = Number(document.getElementById('prtg-timeout-seconds').value) || 30;

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

// ── 初始化 ───────────────────────────────────────────────────────────────────

function init() {
    bindPrtgTest();
    bindForm();
    loadSettings();
}

init();
