/**
 * 登入頁（docs/WEB-SPEC.md §9.0）。
 * 密碼欄是否顯示由後端的 Provider 決定（Stub 不需要密碼），前端不寫死。
 */

import { api } from '../core/api.js';
import { withBusy } from '../core/ui.js';

const form = document.getElementById('lf-login-form');
const accountInput = document.getElementById('account');
const passwordField = document.getElementById('password-field');
const passwordInput = document.getElementById('password');
const errorBox = document.getElementById('login-error');
const submitButton = document.getElementById('login-submit');
const providerHint = document.getElementById('provider-hint');

async function init() {
    try {
        // silent：登入頁還沒有 toast 容器，錯誤直接顯示在表單裡就好
        const options = await api.get('/api/auth/options', { silent: true });

        if (options.requiresPassword) {
            passwordField.classList.remove('d-none');
            passwordInput.setAttribute('required', 'required');
        } else {
            // 測試（Stub）模式：前端一切照舊——密碼欄照常顯示、照常輸入，前端驗證不變。
            // 免密碼發生在**後端**：密碼送到後端後，Stub 模式一律通過密碼驗證（不論輸入什麼，
            // 一般帳號由 StubAuthenticationProvider 放行、救援管理員 svc-lfadmin 由
            // ServerAdminAuthenticator 在 requiresPassword=false 下放行）。密碼欄設選填，留白也可。
            passwordField.classList.remove('d-none');
            providerHint.textContent = '測試模式：密碼不驗證，任意輸入或留空皆可登入';
        }
    } catch {
        showError('無法取得登入設定，請確認伺服器狀態。');
    }
}

form.addEventListener('submit', async event => {
    event.preventDefault();
    hideError();

    const account = accountInput.value.trim();
    if (!account) {
        accountInput.classList.add('is-invalid');
        document.getElementById('account-error').textContent = '請輸入帳號';
        return;
    }
    accountInput.classList.remove('is-invalid');

    const restore = withBusy(submitButton, '登入中');
    try {
        await api.post('/api/auth/login', {
            account,
            password: passwordInput.value || null
        }, { silent: true });

        // returnUrl：被踢回登入頁前想去的位置。只接受站內相對路徑——
        // 直接採用外部網址會變成開放重新導向（釣魚可用它把使用者帶去偽站）
        const params = new URLSearchParams(location.search);
        const returnUrl = params.get('returnUrl');
        location.href = isSafeReturnUrl(returnUrl) ? returnUrl : '/';
    } catch (error) {
        showError(error.message);
        restore();
    }
});

function isSafeReturnUrl(url) {
    return typeof url === 'string' && url.startsWith('/') && !url.startsWith('//');
}

function showError(message) {
    errorBox.textContent = message;
    errorBox.classList.remove('d-none');
}

function hideError() {
    errorBox.classList.add('d-none');
}

init();
