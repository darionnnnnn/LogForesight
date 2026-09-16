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

/**
 * GET 的預設逾時（毫秒）。慢端點沒有逾時時，骨架列會無限停在畫面上，
 * 使用者分不出「還在算」與「已經壞了」——逾時是為了讓等待有盡頭。
 * 呼叫端可用 `{ timeoutMs: 120000 }` 覆寫（例如已知很慢的匯總查詢）。
 */
const GET_TIMEOUT_MS = 60000;


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

    // **只有 GET 加逾時，POST／PUT／DELETE 一律不加**：測試連線、估算規模、套用匯入這類
    // 長時間寫入操作若在客戶端中止，伺服器仍會繼續做完——使用者看到失敗後再按一次，
    // 就是重複執行（重複匯入、重複套用）。讀取沒有這個副作用，中止是安全的。
    let timedOut = false;
    let timeoutTimer = null;
    if (method === 'GET') {
        const timeoutMs = typeof options.timeoutMs === 'number' && options.timeoutMs > 0
            ? options.timeoutMs
            : GET_TIMEOUT_MS;
        const controller = new AbortController();
        init.signal = controller.signal;
        timeoutTimer = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
    }

    let response;
    try {
        response = await fetch(appUrl(url), init);
    } catch (networkError) {
        // 逾時與網路層失敗要分得開：前者該縮小查詢範圍，後者該看網路或站台是否還活著
        if (timedOut) {
            const message = '查詢逾時，請縮小查詢範圍（例如時間區間）或稍後再試。';
            if (options.silent !== true) toast(message, 'danger');
            throw new ApiError('timeout', message, 0);
        }
        // 網路層失敗（站台重啟、連線中斷）與業務錯誤是不同的情況，訊息要說得出差別，
        // 否則使用者只會看到「失敗」而不知道該重試還是該找人
        const message = '無法連線到伺服器，請確認網路狀態後重試。';
        if (options.silent !== true) toast(message, 'danger');
        throw new ApiError('network_error', message, 0);
    } finally {
        // 收到回應就停錶：計時器若在讀 body 的期間觸發，會把已經在傳的回應串流中止掉
        if (timeoutTimer !== null) clearTimeout(timeoutTimer);
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

/**
 * AI 是否可用（回饋第 45 輪 A4）：原本 runs／records／record-detail／dashboard／netiq 五個
 * 頁面各自打一次 /api/ai/status、各存一份旗標，同一次頁面載入重複請求且錯誤處理各寫各的。
 * 收斂到這裡，比照 getCurrentUser／getDisplaySettings 的模組快取模式。
 *
 * 取不到一律視為不可用（與收斂前各頁行為一致）：AI 是加值功能，寧可少顯示一顆按鈕，
 * 也不要讓使用者按下去才發現後端根本沒設定。失敗不寫入快取——同一頁之後還有機會取到。
 */
let aiAvailableCache = null;

export async function getAiAvailable() {
    if (aiAvailableCache === null) {
        try {
            const status = await api.get('/api/ai/status', { silent: true });
            aiAvailableCache = !!status?.available;
        } catch {
            // null＝還不知道：暫時性失敗不等於「未設定」。只做真值判斷的呼叫端把 null 當不可用
            // （與過去行為相同）；排程頁比對的是明確的 false，網路偶發失敗才不會被畫成
            // 「AI 服務未設定」還附上設定頁連結。失敗不快取，下一次呼叫會再問一次。
            return null;
        }
    }
    return aiAvailableCache;
}
