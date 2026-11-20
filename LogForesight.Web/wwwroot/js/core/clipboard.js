/**
 * 剪貼簿共用出口：全站「複製」一律走 copyText，各頁不得直接呼叫 navigator.clipboard。
 *
 * 為什麼要有退路：`navigator.clipboard` 只在安全來源（HTTPS、localhost）存在，
 * 站台支援以 http 內網位址開啟，那種情況下它是 undefined——直接呼叫會擲 TypeError，
 * 使用者只看到「複製失敗」卻不知道換個方式就行。
 * 順序：優先 `navigator.clipboard.writeText`；不可用或被拒絕時退回隱藏 textarea 加
 * `document.execCommand('copy')`；兩者都失敗才回報原因，讓畫面能告訴使用者下一步。
 *
 * 回傳 `{ ok: true, method }`（method 為 'clipboard' 或 'execCommand'）或
 * `{ ok: false, reason }`（reason 是可直接顯示給使用者的繁體中文說明）。永不擲例外。
 */

const REASON_INSECURE = '目前連線不是 HTTPS，瀏覽器不開放剪貼簿，請手動選取文字複製';
const REASON_DENIED = '瀏覽器不允許存取剪貼簿，請手動選取文字複製';

/** 退路：隱藏 textarea 選取後執行 copy 指令；成功回 true */
function copyByExecCommand(text) {
    if (typeof document === 'undefined' || typeof document.execCommand !== 'function') return false;
    const previouslyFocused = document.activeElement;
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.setAttribute('aria-hidden', 'true');
    area.setAttribute('tabindex', '-1');
    // 視覺上藏起來但仍可選取（display:none 的元素 select() 不會生效）
    area.classList.add('visually-hidden');
    // 開著 modal 時 Bootstrap 會把焦點鎖在 modal 內：textarea 掛在 body 會被搶走焦點，
    // 選取落空而 copy 指令照樣回成功。掛進目前焦點所在的 modal 就不受影響
    const host = previouslyFocused?.closest?.('.modal') ?? document.body;
    host.appendChild(area);
    try {
        area.select();
        area.setSelectionRange?.(0, text.length);
        return document.execCommand('copy') === true;
    } catch {
        return false;
    } finally {
        area.remove();
        if (previouslyFocused && typeof previouslyFocused.focus === 'function') previouslyFocused.focus();
    }
}

/**
 * @param {string} text 要複製的文字
 * @returns {Promise<{ok: true, method: 'clipboard'|'execCommand'} | {ok: false, reason: string}>}
 */
export async function copyText(text) {
    const value = String(text ?? '');
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
    const secure = typeof window === 'undefined' || window.isSecureContext !== false;

    if (clipboard && typeof clipboard.writeText === 'function') {
        try {
            await clipboard.writeText(value);
            return { ok: true, method: 'clipboard' };
        } catch {
            // 權限被拒或頁面未取得焦點：往下試退路
        }
    }

    if (copyByExecCommand(value)) return { ok: true, method: 'execCommand' };

    return { ok: false, reason: clipboard || secure ? REASON_DENIED : REASON_INSECURE };
}
