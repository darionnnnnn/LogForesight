/**
 * 數字欄位的讀取出口：**空欄位不得被轉成 0**。
 *
 * `Number('')` 是 0——直接轉會把清空的欄位當成「使用者輸入 0」送出（清空逾時秒數就存成 0）。
 * 本出口把「沒填」和「填了 0」分開：沒填回 null，填 0 回 0。
 *
 * - `readNumberField`：讀單一欄位。必填且沒填時以 `setFieldError` 提示並回 null，
 *   呼叫端 `if (value === null) return;` 阻止送出；選填（空代表沿用或未設定）直接回 null。
 * - `readNumberFields`：一次讀一組欄位，錯誤全部標出、焦點移到第一個，回 `{ ok, values }`。
 * 欄位一開始輸入就解除錯誤標示。
 */

import { setFieldError, clearFieldError } from './field-errors.js';

function resolveField(source) {
    if (source && typeof source === 'object') return source;
    return document.getElementById(source);
}

/**
 * @param {HTMLInputElement|string} source 欄位或欄位 id
 * @param {{required?: boolean, integer?: boolean, message?: string}} [options]
 *        required：沒填就顯示欄位錯誤；integer：必須是整數；message：必填未填的提示
 * @returns {number|null} 沒填、非數字（或欄位不存在）回 null
 */
export function readNumberField(source, { required = false, integer = false, message = '請輸入數值' } = {}) {
    const el = resolveField(source);
    if (!el) return null;

    const raw = String(el.value ?? '').trim();
    const value = raw === '' ? NaN : Number(raw);
    let error = null;
    if (!Number.isFinite(value)) error = raw === '' ? message : '請輸入有效的數字';
    else if (integer && !Number.isInteger(value)) error = '請輸入整數';

    if (error === null) {
        clearFieldError(el);
        return value;
    }
    if (required) {
        setFieldError(el, error);
        el.addEventListener('input', () => clearFieldError(el), { once: true });
    }
    return null;
}

/**
 * @param {Record<string, HTMLInputElement|string|{source: HTMLInputElement|string, required?: boolean, integer?: boolean, message?: string}>} specs
 *        鍵為結果欄位名；值為欄位（預設必填）或帶選項的物件
 * @returns {{ok: boolean, values: Record<string, number|null>}} ok 為 false 代表至少一個必填欄位沒過
 */
export function readNumberFields(specs) {
    const values = {};
    let firstBad = null;
    for (const [key, spec] of Object.entries(specs)) {
        const isOptions = spec && typeof spec === 'object' && 'source' in spec;
        const source = isOptions ? spec.source : spec;
        const options = { required: true, ...(isOptions ? spec : {}) };
        const value = readNumberField(source, options);
        values[key] = value;
        if (value === null && options.required && !firstBad) firstBad = resolveField(source);
    }
    firstBad?.focus?.();
    return { ok: firstBad === null, values };
}
