/**
 * 小型元素工廠（docs/DESIGN-SYSTEM.md §8 文字角色與揭露）。
 *
 * 只放「三個以上頁面都會用到、而且有行為要封裝」的元素：
 *   - disclosure：就地展開／收合（取代頁面各自組的連結樣式「展開」）
 *   - filterDisclosure：工具列的次要條件，標籤帶已套用的條件數
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
    text.className = 'lf-disclosure__label';
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
 * 工具列的次要條件：不常用的篩選收進 target，按鈕標籤後接已套用的條件數（0 個時只寫標籤）。
 * countApplied() 回傳目前已套用幾個條件；條件變動後呼叫回傳的 refresh() 更新標籤。
 * 進頁時已有套用中的條件（下鑽連結帶進來）就預設展開，看得到是哪些條件在作用。
 */
export function filterDisclosure(target, countApplied, { label = '更多條件', className = '' } = {}) {
    const button = disclosure(label, target, { expanded: countApplied() > 0, className });
    const text = button.querySelector('.lf-disclosure__label');
    const refresh = () => {
        const count = countApplied();
        text.textContent = count > 0 ? `${label} ${count}` : label;
    };
    refresh();
    return { button, refresh };
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

/** 單一文字節點套一個文字角色（lf-text-title／meta／label／data／alert）；text 走 textContent */
export function roleText(role, text) {
    const span = document.createElement('span');
    span.className = role;
    span.textContent = text ?? '';
    return span;
}

/** 「標籤 值」一組（標籤用 lf-text-label），通常放進 metaLine；value 可為字串、數字或 Node */
export function fact(label, value) {
    const span = document.createElement('span');
    span.append(roleText('lf-text-label', label), ' ', value instanceof Node ? value : String(value ?? ''));
    return span;
}

const STATUS_NOTE = {
    error: { icon: 'exclamation-triangle', role: 'alert' },
    warning: { icon: 'exclamation-triangle', role: 'status' },
    success: { icon: 'clipboard-check', role: 'status' },
    info: { icon: 'info-circle', role: 'status' }
};

/**
 * 操作結果與驗證訊息（連線測試結果、儲存失敗…）。類型 error／warning／success／info；
 * 失敗用 role="alert" 讓螢幕閱讀器立即唸出，其餘 role="status"。text 走 textContent。
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
