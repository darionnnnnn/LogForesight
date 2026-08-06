/**
 * 共用 UI 元件（docs/WEB-SPEC.md §8.1）：toast、確認對話框、表格渲染、載入/空狀態。
 *
 * 集中在這裡的理由與 api.js 相同——「破壞性操作要二次確認」「空狀態要有指引」
 * 「載入中要有回饋」這些規範（§8.6）如果靠每頁自己實作，遲早有頁面漏掉。
 */

/**
 * 內嵌 SVG sprite 圖示（§8.2）。name 一律是開發者提供的常數（sprite 內的 symbol id），
 * 不接受使用者資料——因此以 setAttribute 組 href 無 XSS 疑慮。
 * SVG 元素必須用 createElementNS 建立，一般 createElement 會靜默失效。
 */
// formatUserName 是使用者顯示的單一格式出口（§9）。format.js 也 import 本檔的 icon()，
// 形成循環——但雙方都只在「函式體內」使用彼此、非模組初始化期求值，ESM 的 live binding
// 在此安全（searchableUserSelect 於執行期才呼叫 formatUserName）。
import { formatUserName } from './format.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const XLINK_NS = 'http://www.w3.org/1999/xlink';

export function icon(name, className) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', className ? `lf-icon ${className}` : 'lf-icon');
    svg.setAttribute('aria-hidden', 'true');

    const use = document.createElementNS(SVG_NS, 'use');
    const href = `/img/icons.svg#${name}`;
    use.setAttribute('href', href);
    use.setAttributeNS(XLINK_NS, 'xlink:href', href);   // 舊瀏覽器相容
    svg.appendChild(use);
    return svg;
}

/**
 * 說明 icon 鈕（滑過或 focus 顯示 popover），供 JS 動態產生欄位時使用——
 * layout.js 的 initHelpPopovers 只在頁面載入當下掃描一次 DOM，涵蓋不到之後才建立的節點，
 * 這裡自行初始化 popover 實例（docs/archive/FEEDBACK-5-PLAN.md §6）。
 * content/title 皆為開發者常數，不是使用者輸入，直接寫入 data 屬性無 XSS 疑慮。
 */
export function helpIcon(content, title) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'lf-help';
    btn.tabIndex = 0;
    btn.setAttribute('data-bs-toggle', 'popover');
    btn.setAttribute('data-bs-trigger', 'hover focus');
    if (title) btn.setAttribute('data-bs-title', title);
    btn.setAttribute('data-bs-content', content);
    btn.appendChild(icon('question-circle'));
    new bootstrap.Popover(btn, { trigger: 'hover focus', html: false });
    return btn;
}

/**
 * 統一的按鈕工廠，取代各頁自己寫的 button()/actionButton()（都在組 `btn btn-sm btn-*`）。
 * text 走 textContent；variant/size/iconName 皆為開發者常數。
 */
export function button(text, { variant = 'outline-secondary', size = 'sm', icon: iconName, onClick, type = 'button', title } = {}) {
    const btn = document.createElement('button');
    btn.type = type;
    btn.className = `btn btn-${size} btn-${variant}`;
    if (title) btn.title = title;
    if (iconName) btn.appendChild(icon(iconName));
    if (text) {
        const span = document.createElement('span');
        span.textContent = text;
        btn.appendChild(span);
    }
    if (onClick) btn.addEventListener('click', onClick);
    return btn;
}

/**
 * 頁籤切換（§8.5）：抽出 rules/groups/permission-changes 重複的 [data-tab]/[data-panel] 邏輯。
 * tabsEl 內的 [data-tab] 按鈕與同層 [data-panel] 區塊以 data 值配對；點擊切換 active 與 d-none。
 */
export function bindTabs(tabsEl, { onChange } = {}) {
    if (!tabsEl) return;
    const panels = tabsEl.parentElement
        ? tabsEl.parentElement.querySelectorAll('[data-panel]')
        : document.querySelectorAll('[data-panel]');

    tabsEl.addEventListener('click', event => {
        const btn = event.target.closest('[data-tab]');
        if (!btn) return;
        const name = btn.dataset.tab;

        for (const link of tabsEl.querySelectorAll('[data-tab]')) {
            link.classList.toggle('active', link === btn);
        }
        for (const panel of panels) {
            panel.classList.toggle('d-none', panel.dataset.panel !== name);
        }
        if (onChange) onChange(name);
    });
}

/** 每頁筆數下拉的固定選項（需求：10/20/30/50/100，預設 20）*/
export const PAGE_SIZE_OPTIONS = [10, 20, 30, 50, 100];
export const DEFAULT_PAGE_SIZE = 20;

/**
 * 每頁筆數的偏好記憶（localStorage，per 呼叫端一把 key）：使用者選過一次後，
 * 同一頁下次進來沿用，不必每次都重選。key 為呼叫端自訂字串（如 'records'／'hosts'），
 * 内部統一加前綴避免與其他 localStorage 使用者衝突。
 */
export function loadPageSize(key, fallback = DEFAULT_PAGE_SIZE) {
    const raw = localStorage.getItem(`lf.pageSize.${key}`);
    const n = raw ? Number(raw) : NaN;
    return PAGE_SIZE_OPTIONS.includes(n) ? n : fallback;
}

export function savePageSize(key, size) {
    localStorage.setItem(`lf.pageSize.${key}`, String(size));
}

/**
 * 分頁列（§8.6-7）：抽出 records.js/audit.js 幾乎相同的手搓分頁。
 * page 為 1-based；onPage(n) 由呼叫端載入該頁。
 * pageSize/onPageSize（選填）：加一個每頁筆數下拉（10/20/30/50/100）。
 * totalPages === 0（無資料）時整個清空；否則至少畫出下拉（如有提供），
 * 頁碼列僅在 totalPages > 1 時出現。
 */
export function renderPagination(container, { page, totalPages, onPage, pageSize, onPageSize, pageSizeOptions = PAGE_SIZE_OPTIONS }) {
    const showPager = !!totalPages && totalPages > 1;
    const showPageSize = !!onPageSize && !!totalPages;

    if (!showPager && !showPageSize) {
        container.replaceChildren();
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'lf-pagination-bar';

    if (showPageSize) {
        bar.appendChild(pageSizeSelect(pageSize, pageSizeOptions, onPageSize));
    }
    if (showPager) {
        bar.appendChild(buildPageNav(page, totalPages, onPage));
    }

    container.replaceChildren(bar);
}

function pageSizeSelect(current, options, onChange) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-page-size';

    const label = document.createElement('span');
    label.className = 'text-muted small';
    label.textContent = '每頁顯示';

    const select = document.createElement('select');
    select.className = 'form-select form-select-sm d-inline-block w-auto';
    select.setAttribute('aria-label', '每頁顯示筆數');
    for (const size of options) {
        const opt = document.createElement('option');
        opt.value = String(size);
        opt.textContent = `${size} 筆`;
        if (size === current) opt.selected = true;
        select.appendChild(opt);
    }
    select.addEventListener('change', () => onChange(Number(select.value)));

    wrap.append(label, select);
    return wrap;
}

function buildPageNav(page, totalPages, onPage) {
    const nav = document.createElement('nav');
    const ul = document.createElement('ul');
    ul.className = 'pagination pagination-sm mb-0';

    const addItem = (label, targetPage, { disabled = false, active = false } = {}) => {
        const li = document.createElement('li');
        li.className = `page-item${disabled ? ' disabled' : ''}${active ? ' active' : ''}`;
        const a = document.createElement('a');
        a.className = 'page-link';
        a.href = '#';
        a.textContent = label;
        if (!disabled && !active) {
            a.addEventListener('click', e => { e.preventDefault(); onPage(targetPage); });
        } else {
            a.addEventListener('click', e => e.preventDefault());
        }
        li.appendChild(a);
        ul.appendChild(li);
    };

    addItem('‹', page - 1, { disabled: page <= 1 });

    // 視窗化頁碼：目前頁前後各 2 頁，頭尾恆顯示
    const windowSize = 2;
    const pages = new Set([1, totalPages]);
    for (let p = page - windowSize; p <= page + windowSize; p++) {
        if (p >= 1 && p <= totalPages) pages.add(p);
    }
    const sorted = [...pages].sort((a, b) => a - b);
    let prev = 0;
    for (const p of sorted) {
        if (p - prev > 1) {
            const li = document.createElement('li');
            li.className = 'page-item disabled';
            li.innerHTML = '<span class="page-link">…</span>';
            ul.appendChild(li);
        }
        addItem(String(p), p, { active: p === page });
        prev = p;
    }

    addItem('›', page + 1, { disabled: page >= totalPages });

    nav.appendChild(ul);
    return nav;
}

/** 右下角提示。type: success | danger | warning | info */
export function toast(message, type = 'info', delay = 4000) {
    let container = document.getElementById('lf-toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'lf-toast-container';
        container.className = 'toast-container position-fixed bottom-0 end-0 p-3';
        container.style.zIndex = '1090';
        document.body.appendChild(container);
    }

    const el = document.createElement('div');
    el.className = `toast align-items-center text-bg-${type} border-0`;
    el.setAttribute('role', 'alert');
    el.innerHTML = `
        <div class="d-flex">
            <div class="toast-body"></div>
            <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="關閉"></button>
        </div>`;
    // message 可以是字串或 Node——後者供「操作完成後給一個追溯／下一步連結」的情境使用
    // （例如統一標記完成後的「檢視這次影響的清單」，體檢 X6）。
    // 一律用 textContent／appendChild，**絕不 innerHTML**：toast 內容可能含主機名或
    // 問題描述，那是攻擊者可控的字串
    const bodyEl = el.querySelector('.toast-body');
    if (message instanceof Node) bodyEl.appendChild(message);
    else bodyEl.textContent = message;
    container.appendChild(el);

    const toastInstance = new bootstrap.Toast(el, { delay });
    el.addEventListener('hidden.bs.toast', () => el.remove());
    toastInstance.show();
}

/**
 * 破壞性操作的二次確認（§8.6-3）。
 * message 必須**具體描述影響**（「將刪除規則 custom-xxx 及其 3 筆抑制設定」），
 * 不是「確定嗎？」——使用者要有足夠資訊才做得了決定。
 */
export function confirmAction({ title = '請確認', message, confirmText = '確定', confirmVariant = 'danger' }) {
    return new Promise(resolve => {
        const el = document.createElement('div');
        el.className = 'modal fade';
        el.innerHTML = `
            <div class="modal-dialog modal-dialog-centered">
                <div class="modal-content">
                    <div class="modal-header">
                        <h5 class="modal-title"></h5>
                        <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="關閉"></button>
                    </div>
                    <div class="modal-body"><p class="mb-0"></p></div>
                    <div class="modal-footer">
                        <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
                        <button type="button" class="btn btn-${confirmVariant}" data-lf-confirm></button>
                    </div>
                </div>
            </div>`;
        el.querySelector('.modal-title').textContent = title;
        el.querySelector('.modal-body p').textContent = message;
        el.querySelector('[data-lf-confirm]').textContent = confirmText;

        document.body.appendChild(el);
        const modal = new bootstrap.Modal(el);

        let confirmed = false;
        el.querySelector('[data-lf-confirm]').addEventListener('click', () => {
            confirmed = true;
            modal.hide();
        });
        el.addEventListener('hidden.bs.modal', () => {
            el.remove();
            resolve(confirmed);
        });

        modal.show();
    });
}

/**
 * 通用資訊 modal（骨架抽自 confirmAction，避免第三份動態 modal 複本）：純檢視用途，
 * 沒有確認/取消語意，只有一個關閉鈕。body 收 DOM 節點而非 HTML 字串——內容常來自
 * 事件原始訊息等攻擊者可控字串，維持 textContent 純文字組裝原則（S7）。
 * size 可傳 'modal-lg' 等 Bootstrap modal-dialog 尺寸 class；fullscreen 為 true 時改用
 * modal-fullscreen（兩者擇一，同時傳入以 fullscreen 優先）。
 *
 * body 若是呼叫端既有的 DOM 節點（而非新建立的），appendChild 會把它從原位置「搬移」
 * 過來（節點的事件監聽器與內部狀態隨之保留，不是複製）——關閉時傳 onClose 把節點搬回原位，
 * 在 el.remove() **之前**呼叫，讓節點回到 DOM 上有效位置後才銷毀 modal 殼
 * （docs/archive/FEEDBACK-3-PLAN.md #6：chat-panel.js 的放大檢視即用此手法）。
 */
export function showDetailModal({ title = '', body, size, fullscreen = false, onClose } = {}) {
    const el = document.createElement('div');
    el.className = 'modal fade';
    // 標題與對話框的關聯（體檢 M6）：沒有 aria-labelledby 時螢幕閱讀器只會唸「對話方塊」，
    // 不會唸出它是什麼對話方塊。id 必須唯一——全站 modal 共用這個工廠，同時開兩個並非不可能
    const titleId = `lf-modal-title-${Math.random().toString(36).slice(2, 10)}`;
    el.setAttribute('aria-labelledby', titleId);
    el.innerHTML = `
        <div class="modal-dialog modal-dialog-scrollable${fullscreen ? ' modal-fullscreen' : (size ? ` ${size}` : '')}">
            <div class="modal-content" tabindex="-1">
                <div class="modal-header">
                    <h5 class="modal-title" id="${titleId}"></h5>
                    <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="關閉"></button>
                </div>
                <div class="modal-body"></div>
                <div class="modal-footer">
                    <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">關閉</button>
                </div>
            </div>
        </div>`;
    el.querySelector('.modal-title').textContent = title;
    if (body) el.querySelector('.modal-body').appendChild(body);

    document.body.appendChild(el);
    const modal = new bootstrap.Modal(el);

    // 開啟後把焦點移進對話框（體檢 M6）：不做的話 document.activeElement 仍是 <body>，
    // 鍵盤使用者按 Tab 是從整個頁面最上方開始，而不是從 modal 內開始。
    // 選第一個可聚焦元素而非固定聚焦關閉鈕——使用者要做的事在 body 裡，不是關掉它。
    el.addEventListener('shown.bs.modal', () => {
        const focusable = el.querySelector(
            '.modal-body button:not([disabled]), .modal-body a[href], .modal-body input:not([disabled]), ' +
            '.modal-body select:not([disabled]), .modal-body textarea:not([disabled])');
        (focusable ?? el.querySelector('.modal-content')).focus?.();
    });

    // 關閉時把焦點還給觸發它的元素——不還的話焦點落回 <body>，等於又回到頁面最上方。
    // Bootstrap 只在自己管理 focus 時處理這件事，程式化建立的 modal 要自己記
    const opener = document.activeElement;
    el.addEventListener('hidden.bs.modal', () => {
        onClose?.();
        el.remove();
        if (opener instanceof HTMLElement && document.contains(opener)) opener.focus();
    });
    modal.show();
}

/**
 * 表單未儲存變更追蹤（docs/archive/HISTORY.md #2）：離開頁面前跳出瀏覽器原生確認。
 * MPA 站台下 beforeunload 這一個 handler 就涵蓋側欄跳轉、重新整理、關閉分頁，
 * 不需要攔截個別連結。excludeSelector 可排除表單內不算「設定內容」的欄位
 * （例如測完即丟的測試帳密）——這些欄位的變更不該觸發離開提醒。
 */
export function trackUnsaved(form, { excludeSelector } = {}) {
    let dirty = false;

    const markDirty = event => {
        if (excludeSelector && event.target.closest(excludeSelector)) return;
        dirty = true;
    };
    form.addEventListener('input', markDirty);
    form.addEventListener('click', markDirty);

    window.addEventListener('beforeunload', event => {
        if (!dirty) return;
        event.preventDefault();
        event.returnValue = '';
    });

    return { clear: () => { dirty = false; } };
}

/**
 * 表格渲染：欄位定義 → <table>，含空狀態與載入中列。
 * columns: [{ key, title, className, render(row), renderHeader(), sortKey, sortDefaultDir }]
 * renderHeader()（選填）：回傳 Node 時取代 title 文字作為表頭儲存格內容——
 * 目前唯一用途是風險日詳情重點問題表的「選取」欄全選 checkbox（docs/archive/HISTORY.md #7）。
 * rowHref(row)（選填）：回傳非空字串時整列可點導向該網址——列內既有的 <a>/<button>
 * 仍照自己的行為，不被整列連結攔截。
 * rowDetail(row)（選填）：回傳 Node 時，該列下方多一條可展開的細節列（跨欄），
 * 點該列（避開列內連結/按鈕）即展開/收合，首欄前置一個展開箭頭。與 rowHref 互斥使用。
 * sort/onSort（選填，表格排序）：有 sortKey 的欄位表頭變成可點按鈕；點擊時以目前
 * sort（{key, dir}）算出下一個排序狀態（同欄切換 asc/desc，換欄回到 sortDefaultDir
 * ／預設 asc）並呼叫 onSort(key, dir)——切換邏輯集中在這裡，呼叫端只需套用收到的狀態，
 * 不論是重打 API（伺服器分頁頁）還是本地重排（見 sortRows）。
 */
export function renderTable(container, { columns, rows, empty, rowHref, rowDetail, onRowExpand, sort, onSort, stickyLastColumn }) {
    if (!rows || rows.length === 0) {
        renderEmpty(container, empty);
        return;
    }

    const wrap = document.createElement('div');
    // stickyLastColumn：末欄（動作欄）在橫向捲動時固定在右緣（體檢 W1）。
    // 只有真的有動作欄的表格才開——對一般表格套用會讓最後一欄無謂地浮起來
    wrap.className = 'lf-table-wrap' + (stickyLastColumn ? ' lf-table-wrap--sticky-action' : '');

    const table = document.createElement('table');
    table.className = 'table table-hover align-middle mb-0';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const col of columns) {
        const th = document.createElement('th');
        if (col.sortKey && onSort) {
            th.appendChild(sortHeader(col, sort, onSort));
            const active = sort && sort.key === col.sortKey;
            th.setAttribute('aria-sort', active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none');
        } else if (col.renderHeader) {
            th.appendChild(col.renderHeader());
        } else {
            th.textContent = col.title;
        }
        if (col.className) th.className = col.className;
        headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const row of rows) {
        const tr = document.createElement('tr');

        const href = rowHref ? rowHref(row) : null;
        if (href) {
            tr.classList.add('lf-row-link');
            // 鍵盤可達（體檢 H5）：整列可點是全站主要的下鑽動線（儀表板重點問題卡、
            // 依問題視角展開列都**沒有列內連結**），只有 click 監聽等於這些功能對
            // 鍵盤／輔助技術使用者不存在。範本取自風險日詳情已經做對的
            // lf-card__header--clickable（role + tabindex + Enter/Space + :focus-visible）。
            makeRowActivatable(tr, () => { location.href = href; });
        }

        // 兩種展開：rowDetail 進頁即建好 DOM（eager）；onRowExpand 首次展開才建（lazy，
        // docs/archive/FEEDBACK-9-PLAN.md §2）——大資料量（14 天×2000 台的執行明細）不能進頁全抓。
        const detail = rowDetail ? rowDetail(row) : null;
        const expandable = !!detail || !!onRowExpand;

        for (const [index, col] of columns.entries()) {
            const td = document.createElement('td');
            if (col.className) td.className = col.className;

            // 展開箭頭放在首欄最前面，作為「這列可展開」的視覺提示
            if (expandable && index === 0) {
                const caret = document.createElement('span');
                caret.className = 'lf-row-caret';
                caret.appendChild(icon('chevron-down'));
                td.appendChild(caret);
            }

            const content = col.render ? col.render(row) : row[col.key];
            if (content instanceof Node) {
                td.appendChild(content);
            } else if (content === null || content === undefined) {
                td.appendChild(document.createTextNode(''));
            } else {
                // 一律用 textContent 而非 innerHTML：資料裡混有 Event Log 的原始訊息，
                // 那是攻擊者可控的字串，絕不能當成 HTML 解析
                td.appendChild(document.createTextNode(String(content)));
            }
            tr.appendChild(td);
        }
        tbody.appendChild(tr);

        if (expandable) {
            const detailRow = document.createElement('tr');
            detailRow.className = 'lf-row-detail d-none';
            const cell = document.createElement('td');
            cell.colSpan = columns.length;
            if (detail) cell.appendChild(detail);
            detailRow.appendChild(cell);
            tbody.appendChild(detailRow);

            let populated = !!detail;   // eager 版本一開始就已填好；lazy 版本首次展開才填
            tr.classList.add('lf-row-expandable');
            // 展開列同樣要鍵盤可達（體檢 H5），另外補 aria-expanded 讓螢幕閱讀器
            // 知道現在是展開還是收合——只有視覺上的箭頭對輔助技術等於沒有狀態
            tr.setAttribute('aria-expanded', 'false');
            makeRowActivatable(tr, () => {
                const open = detailRow.classList.toggle('d-none');
                tr.classList.toggle('lf-row-open', !open);
                tr.setAttribute('aria-expanded', String(!open));
                // 首次展開才呼叫 onRowExpand 填內容（之後展開/收合重用同一份 DOM）
                if (!open && !populated && onRowExpand) {
                    populated = true;
                    onRowExpand(row, cell);
                }
            });
        }
    }
    table.appendChild(tbody);
    wrap.appendChild(table);

    container.replaceChildren(wrap);
    bindScrollAffordance(wrap);
}

/**
 * 橫向捲動的可見提示（體檢 M2／W1）。
 *
 * `.lf-table-wrap` 早就有 `overflow-x: auto`（頁面本身不溢出 ✅），但**沒有任何視覺提示**——
 * 1024×768（遠端桌面、投影、舊筆電的常見解析度）下依問題視角的內容寬 1512px、
 * 可視寬僅 709px，右側的「處理概況／處理人／動作」整段在畫面外，
 * 第一次使用的人會直接認定「這個視角沒有指派功能」。
 *
 * 作法是右緣漸層陰影＋一行文字提示，捲到底自動消失。**一處改、全站表格受惠**。
 * 用 class 切換而不是直接改 style：視覺細節留在 CSS，這裡只負責狀態。
 */
function bindScrollAffordance(wrap) {
    const update = () => {
        // 1px 容差：捲到底時 scrollLeft 可能因為小數寬度差一點點
        const more = wrap.scrollWidth - wrap.clientWidth - wrap.scrollLeft > 1;
        wrap.classList.toggle('lf-table-wrap--scrollable', more);
    };

    wrap.addEventListener('scroll', update, { passive: true });

    // 容器寬度會隨側欄收合、字級切換、視窗縮放改變——只綁 window resize 抓不到前兩者
    if (window.ResizeObserver) {
        const observer = new ResizeObserver(update);
        observer.observe(wrap);
        // **表格本身也要觀察**：wrap 的寬度由版面決定，內容再寬它都不會變，
        // 只觀察 wrap 的話「內容變寬」這個真正的觸發條件永遠偵測不到
        // （欄位展開、字級切換、延遲載入的內容都屬於這一類）。
        const table = wrap.querySelector('table');
        if (table) observer.observe(table);
    } else {
        window.addEventListener('resize', update);
    }

    // 立即量一次，不等 requestAnimationFrame：呼叫端已經把 wrap 掛進 DOM，
    // 讀 scrollWidth 會強制重排，量到的就是最終值。
    // 原本用 rAF 是想「等佈局穩定」，但 rAF 在**非前景分頁**會被延後到分頁可見為止——
    // 使用者在背景分頁開啟頁面、切回來時提示不會出現（wrap 寬度沒變，
    // ResizeObserver 也不會再補一次），提示就這樣永久缺席。
    update();
}

/**
 * 讓一整列（<tr>）成為可用鍵盤操作的觸發器（體檢 H5）。
 *
 * 為什麼是 `role="button"` 而不是把整列包成 <a>：表格列裡本來就可能有自己的連結與按鈕，
 * 巢狀互動元素是無效的 HTML，螢幕閱讀器的行為也不可預期。給列一個明確的按鈕語意，
 * 列內元素保有自己的行為（下面的 closest 判斷），是既有 lf-card__header--clickable
 * 已經驗證過的作法。
 *
 * Space 要 preventDefault：不擋的話瀏覽器會把它當成捲動頁面，使用者按下去畫面往下跳一屏。
 */
function makeRowActivatable(tr, activate) {
    tr.setAttribute('role', 'button');
    tr.setAttribute('tabindex', '0');

    tr.addEventListener('click', event => {
        // 讓列內的連結／按鈕／表單控制項保有自己的行為，只有點到空白處才觸發整列動作
        if (event.target.closest('a, button, input, select, textarea, label')) return;
        activate();
    });

    tr.addEventListener('keydown', event => {
        if (event.key !== 'Enter' && event.key !== ' ') return;
        if (event.target !== tr) return;   // 焦點在列內元素上時交給那個元素處理
        event.preventDefault();
        activate();
    });
}

function sortHeader(col, sort, onSort) {
    const active = sort && sort.key === col.sortKey;

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'lf-th-sort' + (active ? ' lf-th-sort--active' : '');

    const text = document.createElement('span');
    text.textContent = col.title;
    btn.appendChild(text);

    const caret = document.createElement('span');
    caret.className = 'lf-th-sort__caret';
    caret.classList.toggle('lf-th-sort__caret--desc', active && sort.dir === 'desc');
    caret.appendChild(icon('chevron-down'));
    btn.appendChild(caret);

    btn.addEventListener('click', () => {
        const nextDir = active ? (sort.dir === 'asc' ? 'desc' : 'asc') : (col.sortDefaultDir || 'asc');
        onSort(col.sortKey, nextDir);
    });

    return btn;
}

/**
 * 本地排序（表格清單頁：使用者、規則、告警抑制、執行監控單日明細／異常彙總）。
 * columns 需在要排序的欄位上提供 sortKey；比較值優先取 col.sortValue(row)，
 * 沒有則退回 row[col.key]（多數欄位 render() 回傳 DOM Node 無法直接比較，
 * 因此不拿 render 的結果當排序依據，需要排序的欄位務必補 sortValue 或有對應的 key）。
 * sort 為 null／key 對不到任何欄位時原樣回傳（不排序）。
 */
export function sortRows(rows, columns, sort) {
    if (!sort || !sort.key) return rows;
    const col = columns.find(c => c.sortKey === sort.key);
    if (!col) return rows;

    const valueOf = col.sortValue ?? (r => r[col.key]);
    const sorted = [...rows].sort((a, b) => {
        const va = valueOf(a);
        const vb = valueOf(b);
        if (va == null && vb == null) return 0;
        if (va == null) return -1;
        if (vb == null) return 1;
        if (va < vb) return -1;
        if (va > vb) return 1;
        return 0;
    });

    if (sort.dir === 'desc') sorted.reverse();
    return sorted;
}

/**
 * 篩選 chip 群組渲染（§5.0 D-0）：問題查詢／規則維護／主機／使用者頁共用同一套視覺與互動，
 * 取代各頁各自手排的裸 btn-group。multi=true 可複選（點擊只切換自己）；multi=false 為單選
 * （點擊會清掉群組內其他 active，適合「狀態」「排序方向」這類互斥篩選）。
 * onToggle(value, nowActive) 由呼叫端更新篩選狀態並重新查詢/重繪；本函式只管畫面與 class。
 */
export function renderChips(container, { items, attr, activeValues, multi = true, onToggle }) {
    container.replaceChildren();
    const active = new Set(activeValues ?? []);

    for (const item of items) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'lf-chip';
        btn.dataset[attr] = item.value;
        if (active.has(item.value)) btn.classList.add('active');
        if (item.disabled) btn.disabled = true;
        if (item.title) btn.title = item.title;

        const text = document.createElement('span');
        text.textContent = item.label;
        btn.appendChild(text);

        if (item.count !== undefined && item.count !== null) {
            const count = document.createElement('span');
            count.className = 'lf-chip__count';
            count.textContent = String(item.count);
            btn.appendChild(count);
        }

        btn.addEventListener('click', () => {
            if (!multi) {
                for (const other of container.querySelectorAll('.lf-chip')) other.classList.remove('active');
                btn.classList.add('active');
                onToggle(item.value, true);
                return;
            }
            const nowActive = !btn.classList.contains('active');
            btn.classList.toggle('active', nowActive);
            onToggle(item.value, nowActive);
        });

        container.appendChild(btn);
    }
}

/** 空狀態（§8.6-5）：不留白畫面，要說明「接下來該做什麼」 */
export function renderEmpty(container, { title = '尚無資料', hint = '', icon: iconName = 'inbox' } = {}) {
    const el = document.createElement('div');
    el.className = 'lf-empty';

    if (iconName) {
        const iconWrap = document.createElement('div');
        iconWrap.className = 'lf-empty__icon';
        iconWrap.appendChild(icon(iconName));
        el.appendChild(iconWrap);
    }

    const titleEl = document.createElement('div');
    titleEl.className = 'lf-empty__title';
    titleEl.textContent = title;
    el.appendChild(titleEl);

    if (hint) {
        const hintEl = document.createElement('div');
        hintEl.textContent = hint;
        el.appendChild(hintEl);
    }

    container.replaceChildren(el);
}

/**
 * 頁面載入流程的失敗收斂（docs/archive/FEEDBACK-10-PLAN.md §4）。
 *
 * 骨架列（renderLoading）是「等一下就會有東西」的承諾，但各頁的 load()／init() 過去沒有
 * 頂層 catch：中間任何一支 API 失敗或前端例外拋出，流程就中斷在那裡，骨架列**永遠**留在
 * 畫面上——api.js 的錯誤 toast 幾秒後消失，使用者看到的就是「一直在載入」。
 * （實例：主機頁在全新環境沒有任何 Sentinel 時，fillSentinelOptions 寫一個不存在節點的
 * textContent 而丟 TypeError，整頁清單就此卡住。）
 *
 * 用法：把整段載入流程包起來，失敗時把骨架列換成可理解的失敗狀態。
 * containers 可給單一容器或容器陣列（一頁多塊骨架時全部一起收掉）。
 */
export async function guardLoad(containers, fn, { backLink } = {}) {
    try {
        return await fn();
    } catch (error) {
        // 404 與其他失敗要分開講（體檢 M1）：「請重新整理再試」對「這筆不存在」永遠不會成功，
        // 是誤導。後端一律回可直接顯示的中文（如「找不到這個使用者，可能已被刪除。」），
        // 過去被一視同仁的文案吞掉、只剩幾秒就消失的 toast。
        const notFound = error?.status === 404;
        const state = notFound
            ? {
                title: '找不到資料',
                hint: error.message || '這筆資料可能已被刪除。',
                icon: 'exclamation-triangle'
            }
            : {
                title: '載入失敗',
                hint: '請重新整理頁面後再試；若持續失敗請聯絡系統管理員。',
                icon: 'exclamation-triangle'
            };

        // 一次載入失敗只呈現一塊訊息（體檢 M1 後半）：三個區塊各喊一次「載入失敗」
        // 會讓人以為壞了三個地方，其餘容器清空即可
        const targets = [containers].flat().filter(Boolean);
        targets.forEach((container, index) => {
            if (index === 0) {
                renderEmpty(container, state);
                if (notFound && backLink) container.appendChild(backLinkNode(backLink));
            } else {
                container.replaceChildren();
            }
        });

        // 不吞掉：錯誤細節仍要進 console 供排查（api.js 已負責顯示給使用者看的 toast）
        console.error('[load]', error);
        return undefined;
    }
}

/** 404 空狀態底下的「返回清單」出口——錯誤狀態一定要給得出下一步（優先序 8 Error Recovery） */
function backLinkNode({ href, text = '返回清單' }) {
    const wrap = document.createElement('div');
    wrap.className = 'mt-3';
    const link = document.createElement('a');
    link.className = 'btn btn-sm btn-outline-secondary';
    link.href = href;
    link.textContent = text;
    wrap.appendChild(link);
    return wrap;
}

/** 載入中的骨架列（§8.6-6） */
export function renderLoading(container, rows = 4) {
    const el = document.createElement('div');
    el.className = 'p-3';
    for (let i = 0; i < rows; i++) {
        const bar = document.createElement('div');
        bar.className = 'lf-skeleton mb-2';
        bar.style.width = `${70 + Math.random() * 30}%`;
        el.appendChild(bar);
    }
    container.replaceChildren(el);
}

/**
 * 區塊內等待指示（§8.6-6 的行內版，docs/archive/FEEDBACK-8-PLAN.md #1）：spinner＋文字，取代各頁
 * 過去各自手刻的純文字「載入中…」「掃描中…」——那些等待動作可能長達數十秒（NetIQ 掃描、
 * probe 執行中），純文字讓人分不清是卡住了還是真的在跑。
 *
 * 全站等待動畫的三種出口到此收斂完畢，不要再長出第四種寫法：
 *   - 整塊清單載入 → renderLoading（骨架列）
 *   - 按鈕忙碌 → withBusy（按鈕內文字換成 spinner＋「⋯中」）
 *   - 其餘區塊內等待（精靈步驟、輪詢中的狀態文字等）→ 這裡
 */
export function renderSpinner(container, text = '載入中…') {
    const el = document.createElement('div');
    el.className = 'text-muted small d-flex align-items-center gap-2';

    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm';
    spinner.setAttribute('role', 'status');
    spinner.setAttribute('aria-hidden', 'true');

    const label = document.createElement('span');
    label.textContent = text;

    el.append(spinner, label);
    container.replaceChildren(el);
}

/**
 * 勾選清單（users.js/groups.js/hosts.js 原本各自手刻一份幾乎相同的 form-check 清單）：
 * items: [{ id, label, checked }]。id 屬性組成 `${container.id}-${item.id}`，
 * 供同一清單內 label 的 htmlFor 配對；清單為空時顯示 emptyHint 取代整份清單。
 */
export function checkboxList(container, items, emptyHint) {
    container.replaceChildren();

    if (items.length === 0) {
        const hint = document.createElement('div');
        hint.className = 'text-muted small';
        hint.textContent = emptyHint;
        container.appendChild(hint);
        return;
    }

    for (const item of items) {
        const wrapper = document.createElement('div');
        wrapper.className = 'form-check';

        const input = document.createElement('input');
        input.className = 'form-check-input';
        input.type = 'checkbox';
        input.value = item.id;
        input.id = `${container.id}-${item.id}`;
        input.checked = item.checked;

        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = item.label;

        wrapper.append(input, label);
        container.appendChild(wrapper);
    }
}

/**
 * 可搜尋的使用者選單（docs/archive/FEEDBACK-9-PLAN.md §8）：文字框即時篩選＋原生 select。
 * 處理面板指派、跨主機批次指派共用同一份，不各寫一份。
 *
 * 篩選比對顯示名稱＋帳號（不分大小寫）；選項文字一律走 formatUserName()（§9 單一格式出口）。
 * 純前端過濾，使用者清單由呼叫端一次載入（不新增 API）。
 *
 * @param {Array} users `/api/admin/users` 的清單（含 userId/account/displayName/active）
 * @param {object} opts
 *   selectedId  預選的 userId（null＝不預選）
 *   includeNone 是否加「（未指派）」空選項（值＝''）
 *   noneLabel   空選項文字
 *   pinnedNames 置頂並標「（負責人）」的 displayName 集合（改派最常選負責人）
 *   onChange    select 值變更時回呼（帶新的 value 字串；篩選本身不觸發）
 * @returns {{ element: HTMLElement, getValue: () => string, select: HTMLSelectElement }}
 */
export function searchableUserSelect(users, { selectedId = null, includeNone = false, noneLabel = '（未指派）', pinnedNames = [], onChange } = {}) {
    const wrap = document.createElement('div');

    const search = document.createElement('input');
    search.type = 'text';
    search.className = 'form-control form-control-sm mb-1';
    search.placeholder = '輸入帳號或顯示名稱篩選…';
    search.autocomplete = 'off';

    const select = document.createElement('select');
    select.className = 'form-select form-select-sm';

    const pinned = new Set(pinnedNames);
    const sorted = [...users].filter(u => u.active).sort((a, b) => {
        const ap = pinned.has(a.displayName) ? 0 : 1;
        const bp = pinned.has(b.displayName) ? 0 : 1;
        return ap - bp || a.displayName.localeCompare(b.displayName, 'zh-TW');
    });

    let currentValue = selectedId != null ? String(selectedId) : '';

    function optionLabel(user) {
        const base = formatUserName(user.displayName, user.account);
        return pinned.has(user.displayName) ? `${base}（負責人）` : base;
    }

    function renderOptions() {
        const f = search.value.trim().toLowerCase();
        select.replaceChildren();

        if (includeNone) {
            const none = document.createElement('option');
            none.value = '';
            none.textContent = noneLabel;
            select.appendChild(none);
        }

        for (const user of sorted) {
            const matches = !f || `${user.displayName} ${user.account}`.toLowerCase().includes(f);
            // 目前選中的人即使被篩掉也保留其選項——否則 select.value 會被靜默改成第一項，
            // 使用者一打字就意外「改派」給名單第一人
            if (!matches && String(user.userId) !== currentValue) continue;
            const opt = document.createElement('option');
            opt.value = String(user.userId);
            opt.textContent = optionLabel(user);
            select.appendChild(opt);
        }

        select.value = currentValue;
    }

    search.addEventListener('input', renderOptions);
    select.addEventListener('change', () => {
        currentValue = select.value;
        onChange?.(currentValue);
    });

    renderOptions();
    wrap.append(search, select);
    return { element: wrap, getValue: () => select.value, select };
}

/**
 * 標籤／數值對（host-detail.js/runs.js 的資訊格、handling-panel.js 的唯讀欄共用的最小單位）：
 * 回傳 [labelEl, valueEl]，由呼叫端自行決定外層容器（grid col、mb-3 區塊等各頁不同，不強求一致）。
 * labelClass 各頁沿用原本的樣式，value 一律 fw-semibold（三處原本就相同）。
 */
export function labelValue(label, value, { labelClass = 'text-muted' } = {}) {
    const labelEl = document.createElement('div');
    labelEl.className = labelClass;
    labelEl.textContent = label;

    const valueEl = document.createElement('div');
    valueEl.className = 'fw-semibold';
    valueEl.textContent = value;

    return [labelEl, valueEl];
}

/**
 * KPI 統計卡（dashboard.js/reports.js 原本各自手刻一份）：col 內的可選連結卡片，
 * 大數字＋說明文字，variant 控制數字顏色、hint 當 title 提示、extra 可附加額外內容
 * （例如 reports.js 的與前期對比徽章）。centered=false 供 reports.js 的靠左版型使用。
 */
export function statCard({ value, label, variant, url, hint, centered = true, extra }) {
    const inner = document.createElement(url ? 'a' : 'div');
    inner.className = 'lf-stat';
    if (url) inner.href = url;

    const box = document.createElement('div');
    box.className = 'lf-card h-100' + (url ? ' lf-card--clickable' : '');
    if (hint) box.title = hint;

    const body = document.createElement('div');
    body.className = centered ? 'lf-card__body text-center' : 'lf-card__body';

    const valueEl = document.createElement('div');
    valueEl.className = variant ? `lf-stat__value text-${variant}` : 'lf-stat__value';
    valueEl.textContent = value;

    const labelEl = document.createElement('div');
    labelEl.className = 'lf-stat__label';
    labelEl.textContent = label;

    body.append(valueEl, labelEl);
    if (extra) body.appendChild(extra);

    box.appendChild(body);
    inner.appendChild(box);
    return inner;
}

/** 送出中的按鈕狀態：disable 防連點（§8.6-6） */
export function withBusy(button, busyText) {
    const original = button.innerHTML;
    button.disabled = true;
    if (busyText) {
        button.innerHTML = `<span class="spinner-border spinner-border-sm me-2"></span>${busyText}`;
    }
    return () => {
        button.disabled = false;
        button.innerHTML = original;
    };
}
