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

/**
 * 分頁列（§8.6-7）：抽出 records.js/audit.js 幾乎相同的手搓分頁。
 * page 為 1-based；onPage(n) 由呼叫端載入該頁。totalPages <= 1 時清空容器。
 */
export function renderPagination(container, { page, totalPages, onPage }) {
    if (!totalPages || totalPages <= 1) {
        container.replaceChildren();
        return;
    }

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
    container.replaceChildren(nav);
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
    el.querySelector('.toast-body').textContent = message;
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
 * 表格渲染：欄位定義 → <table>，含空狀態與載入中列。
 * columns: [{ key, title, className, render(row) }]
 * rowHref(row)（選填）：回傳非空字串時整列可點導向該網址——列內既有的 <a>/<button>
 * 仍照自己的行為，不被整列連結攔截。
 */
export function renderTable(container, { columns, rows, empty, rowHref }) {
    if (!rows || rows.length === 0) {
        renderEmpty(container, empty);
        return;
    }

    const wrap = document.createElement('div');
    wrap.className = 'lf-table-wrap';

    const table = document.createElement('table');
    table.className = 'table table-hover align-middle mb-0';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const col of columns) {
        const th = document.createElement('th');
        th.textContent = col.title;
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
            tr.addEventListener('click', event => {
                // 讓列內的連結／按鈕保有自己的行為，只有點到空白處才走整列導向
                if (event.target.closest('a, button')) return;
                location.href = href;
            });
        }

        for (const col of columns) {
            const td = document.createElement('td');
            if (col.className) td.className = col.className;

            const content = col.render ? col.render(row) : row[col.key];
            if (content instanceof Node) {
                td.appendChild(content);
            } else if (content === null || content === undefined) {
                td.textContent = '';
            } else {
                // 一律用 textContent 而非 innerHTML：資料裡混有 Event Log 的原始訊息，
                // 那是攻擊者可控的字串，絕不能當成 HTML 解析
                td.textContent = String(content);
            }
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    wrap.appendChild(table);

    container.replaceChildren(wrap);
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
