/**
 * 把網址或資料裡的字串放進 CSS 選擇器前的跳脫出口。
 *
 * 網址 hash 是使用者（或別人貼來的連結）可控的字串：`#a"]` 直接拼進 `[data-x="…"]`
 * 會讓 querySelector 擲 SyntaxError，頁面的載入流程跟著中斷、顯示「載入失敗」。
 * 一律經 `escapeSelectorValue` 或 `findByData`，找不到就回 null，不擲例外。
 */

/** CSS.escape；沒有 CSS 物件的環境（舊瀏覽器、測試）以同樣規則退回 */
export function escapeSelectorValue(value) {
    const text = String(value ?? '');
    if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') return CSS.escape(text);
    return text.replace(/[^a-zA-Z0-9_ -￿-]/g, ch => `\\${ch}`).replace(/^(-?)(\d)/, '$1\\3$2 ');
}

/** 目前網址 hash（不含 #），已解碼；解碼失敗時回原字串 */
export function currentHashId() {
    const raw = String(globalThis.location?.hash ?? '').replace(/^#/, '');
    try {
        return decodeURIComponent(raw);
    } catch {
        return raw;
    }
}

/**
 * 依 data 屬性值找元素：`findByData('step-row', hash)` 找 `[data-step-row="…"]`。
 * 值經跳脫；選擇器仍然非法時回 null，不擲例外。
 */
export function findByData(name, value, root = document) {
    try {
        return root.querySelector(`[data-${name}="${escapeSelectorValue(value)}"]`);
    } catch {
        return null;
    }
}
