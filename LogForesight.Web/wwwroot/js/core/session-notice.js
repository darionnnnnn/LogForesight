/**
 * 被踢回登入頁的原因（已登出、帳號已停用、登入已逾期）。
 *
 * 後端的 401 信封帶了原因，但整頁轉址會把訊息丟掉；api.js 轉址前存進 sessionStorage，
 * 登入頁載入時讀出顯示一次、讀後即刪。sessionStorage 可能被擋（隱私模式），一律包 try/catch——
 * 存不進去只是少一句說明，不影響轉址。
 */

/** 固定鍵：api.js 寫、login.js 讀 */
export const SESSION_NOTICE_KEY = 'lf.sessionNotice';

/** 記下原因；空白或非字串不記 */
export function rememberSessionNotice(message) {
    if (typeof message !== 'string' || !message.trim()) return;
    try { globalThis.sessionStorage?.setItem(SESSION_NOTICE_KEY, message.trim()); } catch { /* 存不進去就算了 */ }
}

/** 讀出並刪除（只顯示一次）；沒有回 null */
export function takeSessionNotice() {
    try {
        const storage = globalThis.sessionStorage;
        const message = storage?.getItem(SESSION_NOTICE_KEY) ?? null;
        if (message !== null) storage.removeItem(SESSION_NOTICE_KEY);
        return message && message.trim() ? message : null;
    } catch {
        return null;
    }
}
