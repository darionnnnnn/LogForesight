/**
 * 「交辦單通知」設定的三態說明句（純函式，無 DOM 相依）。
 *
 * getDisplaySettings 讀不到設定時回傳的物件沒有 mailWorkOrdersEnabled：值不是 true 也不是 false 就是「不知道」，
 * 不能當成關閉去說「未啟用交辦單通知」。代為結案、退回處理人、取消交辦、靜音代為結案附註與解除靜音都從這裡取句子，
 * 開啟與關閉的句子由呼叫端給（各處措辭不同），讀不到時一律用同一句中性句：不承諾、也不否認。
 */

/** 設定讀不到時的中性句（不含句尾標點，標點由呼叫端依上下文補） */
export const MAIL_NOTIFY_UNKNOWN_TEXT = '處理人是否收到通知，依「交辦單通知」設定而定';

/**
 * @param {{ mailWorkOrdersEnabled?: boolean } | null | undefined} settings getDisplaySettings 的回傳
 * @param {string} onText 設定開啟時的句子
 * @param {string} offText 設定關閉時的句子
 * @returns {string}
 */
export function mailNotifyText(settings, onText, offText) {
    const enabled = settings?.mailWorkOrdersEnabled;
    if (enabled === true) return onText;
    if (enabled === false) return offText;
    return MAIL_NOTIFY_UNKNOWN_TEXT;
}
