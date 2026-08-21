/**
 * API 呼叫的唯一出口（docs/WEB-SPEC.md §8.1）。
 *
 * 頁面模組**不得直接呼叫 fetch**——信封解析、錯誤提示、401 導頁、CSRF 標頭
 * 這四件事只要有一處漏掉就是一個 bug，集中在這裡就只需要寫對一次。
 */

import { toast } from './ui.js';
import { appUrl, appPath } from './paths.js';

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
        if (body instanceof FormData) {
            // 檔案上傳：Content-Type 必須讓瀏覽器自己帶（它要在裡面附 multipart boundary），
            // 手動指定會讓後端解不出欄位
            init.body = body;
        } else {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
    }

    let response;
    try {
        response = await fetch(appUrl(url), init);
    } catch (networkError) {
        // 網路層失敗（站台重啟、連線中斷）與業務錯誤是不同的情況，訊息要說得出差別，
        // 否則使用者只會看到「失敗」而不知道該重試還是該找人
        const message = '無法連線到伺服器，請確認網路狀態後重試。';
        if (options.silent !== true) toast(message, 'danger');
        throw new ApiError('network_error', message, 0);
    }

    // 401：登入逾期或帳號已停用 → 導回登入頁，並記住原本要去的位置
    // 例外：登入頁本身送出的登入請求 401 是「帳號或密碼錯誤」，不是「登入逾期」——
    // 整頁轉址會讓 login.js 的錯誤訊息顯示與輸入內容保留全部失效（訊息閃一下就被
    // 蓋成「登入已逾期」）。此時放行到下面的一般錯誤處理，讓後端訊息正常顯示。
    const isLoginAttempt = appPath() === '/login' || url === '/api/auth/login';
    if (response.status === 401 && !isLoginAttempt) {
        // returnUrl 記的是 app 內路徑（不含掛載前綴），登入後由 login.js 用 appUrl 還原
        const returnUrl = encodeURIComponent(appPath() + location.search);
        location.href = appUrl(`/login?returnUrl=${returnUrl}`);
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
 *
 * **這是 async，呼叫端一定要 await。** 忘了 await 不會報錯——拿到的是 Promise，
 * 而 `promise.isServerAdmin`／`promise.capabilities` 都是 `undefined`，
 * 任何比較都恆為 false，判斷因此**靜默失效**（user-detail.js 曾因此讓整個
 * serverAdmin 的可見範圍修正完全沒有生效）。
 *
 * 頁面模組的既有慣例是把它放進 `load()` 的 `Promise.all` 一起取、存成模組變數，
 * 同步的渲染函式再讀那個變數——它有快取，這樣做是零額外成本。
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

/**
 * 顯示層設定的公開子集（docs/archive/FEEDBACK-3-PLAN.md #8）：目前哪些日風險等級顯示中。
 * 與 getCurrentUser 同樣的快取理由——多個頁面模組（儀表板、報表、問題查詢）都需要，
 * 避免每頁重複請求；此設定在單次頁面瀏覽期間不會變（管理者另開分頁改設定不影響本頁）。
 *
 * 失敗降級為「全顯示」而不是拋出：呼叫端都把它放進頁面初始化的 Promise.all，這是
 * 純加值資訊，不能讓它的一次網路失敗拖垮整頁載入。失敗不寫入快取——下一頁還有機會取到。
 */
let displaySettingsCache = null;

export async function getDisplaySettings() {
    if (displaySettingsCache === null) {
        try {
            displaySettingsCache = await api.get('/api/settings/display', { silent: true });
        } catch {
            return { visibleDayRiskLevels: ['高', '中', '低'] };
        }
    }
    return displaySettingsCache;
}
