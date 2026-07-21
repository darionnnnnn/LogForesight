/**
 * API 呼叫的唯一出口（docs/WEB-SPEC.md §8.1）。
 *
 * 頁面模組**不得直接呼叫 fetch**——信封解析、錯誤提示、401 導頁、CSRF 標頭
 * 這四件事只要有一處漏掉就是一個 bug，集中在這裡就只需要寫對一次。
 */

import { toast } from './ui.js';

const CSRF_HEADER = 'X-Requested-By';
const CSRF_VALUE = 'LogForesight';

/** API 回傳的業務錯誤。message 是後端組好的中文，可直接顯示 */
export class ApiError extends Error {
    constructor(code, message, status) {
        super(message);
        this.name = 'ApiError';
        this.code = code;
        this.status = status;
    }
}

async function request(method, url, body, options = {}) {
    const init = {
        method,
        headers: { 'Accept': 'application/json' },
        // Cookie 是 HttpOnly，JS 讀不到 token，但同源請求會自動帶上
        credentials: 'same-origin'
    };

    if (method !== 'GET') {
        init.headers[CSRF_HEADER] = CSRF_VALUE;
    }

    if (body !== undefined && body !== null) {
        init.headers['Content-Type'] = 'application/json';
        init.body = JSON.stringify(body);
    }

    let response;
    try {
        response = await fetch(url, init);
    } catch (networkError) {
        // 網路層失敗（站台重啟、連線中斷）與業務錯誤是不同的情況，訊息要說得出差別，
        // 否則使用者只會看到「失敗」而不知道該重試還是該找人
        const message = '無法連線到伺服器，請確認網路狀態後重試。';
        if (options.silent !== true) toast(message, 'danger');
        throw new ApiError('network_error', message, 0);
    }

    // 401：登入逾期或帳號已停用 → 導回登入頁，並記住原本要去的位置
    if (response.status === 401) {
        const returnUrl = encodeURIComponent(location.pathname + location.search);
        location.href = `/login?returnUrl=${returnUrl}`;
        throw new ApiError('auth_expired', '登入已逾期', 401);
    }

    let payload = null;
    try {
        payload = await response.json();
    } catch {
        payload = null;
    }

    if (!response.ok || !payload || payload.success !== true) {
        const code = payload?.error?.code ?? 'server_error';
        const message = payload?.error?.message ?? '系統發生未預期的錯誤，請稍後再試。';
        if (options.silent !== true) toast(message, 'danger');
        throw new ApiError(code, message, response.status);
    }

    return payload.data;
}

export const api = {
    get: (url, options) => request('GET', url, null, options),
    post: (url, body, options) => request('POST', url, body, options),
    put: (url, body, options) => request('PUT', url, body, options),
    delete: (url, options) => request('DELETE', url, null, options)
};

/**
 * 目前登入者。多個頁面模組都需要（側欄、功能鈕顯示），快取避免每頁重複請求。
 */
let currentUserCache = null;

export async function getCurrentUser() {
    if (currentUserCache === null) {
        currentUserCache = await api.get('/api/auth/me');
    }
    return currentUserCache;
}

export function hasCapability(user, capability) {
    return Array.isArray(user?.capabilities) && user.capabilities.includes(capability);
}
