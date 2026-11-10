/**
 * 小型元素工廠（docs/DESIGN-SYSTEM.md §8 文字角色與揭露）。
 *
 * 只放「三個以上頁面都會用到、而且有行為要封裝」的元素：
 *   - disclosure：就地展開／收合（取代頁面各自組的連結樣式「展開」）
 *   - metaLine：一行次要資訊（次數、時段、來源…），空值自動略過
 *   - statusNote：操作結果與驗證訊息，帶圖示與正確的 role
 * 只是設定 className 的東西不包工廠，頁面直接用 lf-text-* 角色類別。
 * 文字一律走 textContent，外部字串不會被解析成 HTML。
 */
import { icon } from './ui.js';

let disclosureSeq = 0;

/**
 * 就地展開／收合按鈕。label 是所指對象的名稱（「佐證」「全文」），展開前後不變——
 * 狀態由箭頭方向與 aria-expanded 表達，不靠換字。
 *
 * - target：要切換顯示的元素；沒有 id 時自動補一個給 aria-controls。
 * - expanded：初始狀態，預設收合（target 加上 d-none）。
 * - onToggle(expanded)：切換後呼叫；由呼叫端自行切換內容時（例如純文字截斷）傳 manageTarget: false。
 * 放在可點擊展開的表格列裡時 click 會 stopPropagation，避免連帶觸發列展開。
 * 列印時按鈕不印（lf-no-print）；目標的列印顯示由呼叫端或 CSS 決定。
 */
export function disclosure(label, target, { expanded = false, onToggle, manageTarget = true, className = '' } = {}) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `lf-disclosure lf-no-print${className ? ` ${className}` : ''}`;
    button.appendChild(icon('chevron-down', 'lf-disclosure__icon'));
    const text = document.createElement('span');
    text.textContent = label;
    button.appendChild(text);

    if (target) {
        if (!target.id) target.id = `lf-disclosure-${++disclosureSeq}`;
        button.setAttribute('aria-controls', target.id);
    }

    let open = expanded;
    const apply = () => {
        button.setAttribute('aria-expanded', String(open));
        if (target && manageTarget) target.classList.toggle('d-none', !open);
    };
    apply();

    button.addEventListener('click', event => {
        event.stopPropagation();
        open = !open;
        apply();
        onToggle?.(open);
    });
    return button;
}

/**
 * 一行次要資訊。items 可為字串、數字或 Node；null／undefined／空字串略過。
 * 沒有任何有效項目時回傳 null，呼叫端不必另判斷「要不要放這一行」。
 * 項目之間的間隔由 CSS gap 提供，窄寬度整項換行，不會出現行首的孤立分隔符。
 */
export function metaLine(items, { className = '' } = {}) {
    const valid = (items || []).filter(item => item !== null && item !== undefined && item !== '' && item !== false);
    if (valid.length === 0) return null;
    const line = document.createElement('div');
    line.className = `lf-meta-line lf-text-meta${className ? ` ${className}` : ''}`;
    for (const item of valid) {
        if (item instanceof Node) {
            line.appendChild(item);
        } else {
            const span = document.createElement('span');
            span.textContent = String(item);
            line.appendChild(span);
        }
    }
    return line;
}

const STATUS_NOTE = {
    error: { icon: 'exclamation-triangle', role: 'alert' },
    success: { icon: 'clipboard-check', role: 'status' },
    info: { icon: 'info-circle', role: 'status' }
};

/**
 * 操作結果與驗證訊息（連線測試結果、儲存失敗…）。失敗用 role="alert" 讓螢幕閱讀器立即唸出，
 * 其餘 role="status"。text 走 textContent。
 */
export function statusNote(type, text) {
    const spec = STATUS_NOTE[type] || STATUS_NOTE.info;
    const note = document.createElement('div');
    note.className = `lf-status-note lf-status-note--${STATUS_NOTE[type] ? type : 'info'}`;
    note.setAttribute('role', spec.role);
    note.appendChild(icon(spec.icon));
    const span = document.createElement('span');
    span.textContent = text;
    note.appendChild(span);
    return note;
}
