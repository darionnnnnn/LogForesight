/**
 * 欄位錯誤的共用呈現（含無障礙屬性）：紅框＋欄位下方的錯誤訊息。
 *
 * 螢幕閱讀器要能把錯誤和欄位連起來：欄位帶 `aria-invalid="true"`，
 * `aria-describedby` 指向錯誤訊息，錯誤訊息本身有 id。清除時三者一起還原，
 * 欄位原本就有的 aria-describedby（說明文字）保留不動。
 * 訊息一律以 textContent 填入。
 */

let sequence = 0;

function describedByTokens(input) {
    return String(input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
}

/** 錯誤訊息元素的 id：沿用欄位 id，沒有 id 的欄位配一個不重複的 */
function errorIdFor(input) {
    if (!input.id) {
        sequence += 1;
        input.setAttribute('id', `lf-field-${sequence}`);
    }
    return `${input.id}-error`;
}

/**
 * 在欄位上顯示錯誤。
 * @param {HTMLElement} input 欄位
 * @param {string} message 錯誤文字
 * @param {{feedback?: HTMLElement}} [options] feedback：呼叫端版面已備好的訊息容器（沿用，不另建）
 * @returns {HTMLElement} 錯誤訊息元素
 */
export function setFieldError(input, message, { feedback } = {}) {
    const errorId = errorIdFor(input);
    let el = feedback ?? input._lfFieldError ?? null;
    if (!el) {
        el = document.createElement('div');
        el.className = 'invalid-feedback lf-field-error';
        el.setAttribute('role', 'alert');
        input.after(el);
        input._lfFieldError = el;
        el._lfAutoCreated = true;
    }
    el.setAttribute('id', el.id || errorId);
    el.textContent = message;

    input.classList.add('is-invalid');
    input.setAttribute('aria-invalid', 'true');
    const tokens = describedByTokens(input);
    if (!tokens.includes(el.id)) tokens.push(el.id);
    input.setAttribute('aria-describedby', tokens.join(' '));
    input._lfErrorId = el.id;
    input._lfErrorEl = el;
    return el;
}

/** 清掉欄位錯誤：自動建立的訊息元素移除；呼叫端提供的容器只清空文字 */
export function clearFieldError(input) {
    input.classList.remove('is-invalid');
    input.removeAttribute?.('aria-invalid');

    const errorId = input._lfErrorId;
    if (errorId) {
        const rest = describedByTokens(input).filter(t => t !== errorId);
        if (rest.length > 0) input.setAttribute('aria-describedby', rest.join(' '));
        else input.removeAttribute?.('aria-describedby');
    }
    const el = input._lfErrorEl;
    if (el) {
        if (el._lfAutoCreated) {
            el.remove();
            input._lfFieldError = null;
        } else {
            el.textContent = '';
        }
    }
    input._lfErrorId = null;
    input._lfErrorEl = null;
}
