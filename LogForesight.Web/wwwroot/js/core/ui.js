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
 * 通用資訊 modal（骨架抽自 confirmAction，避免第三份動態 modal 複本）：純檢視用途，
 * 沒有確認/取消語意，只有一個關閉鈕。body 收 DOM 節點而非 HTML 字串——內容常來自
 * 事件原始訊息等攻擊者可控字串，維持 textContent 純文字組裝原則（S7）。
 * size 可傳 'modal-lg' 等 Bootstrap modal-dialog 尺寸 class；fullscreen 為 true 時改用
 * modal-fullscreen（兩者擇一，同時傳入以 fullscreen 優先）。
 *
 * body 若是呼叫端既有的 DOM 節點（而非新建立的），appendChild 會把它從原位置「搬移」
 * 過來（節點的事件監聽器與內部狀態隨之保留，不是複製）——關閉時傳 onClose 把節點搬回原位，
 * 在 el.remove() **之前**呼叫，讓節點回到 DOM 上有效位置後才銷毀 modal 殼
 * （docs/FEEDBACK-3-PLAN.md #6：chat-panel.js 的放大檢視即用此手法）。
 */
export function showDetailModal({ title = '', body, size, fullscreen = false, onClose } = {}) {
    const el = document.createElement('div');
    el.className = 'modal fade';
    el.innerHTML = `
        <div class="modal-dialog modal-dialog-scrollable${fullscreen ? ' modal-fullscreen' : (size ? ` ${size}` : '')}">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title"></h5>
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
    el.addEventListener('hidden.bs.modal', () => {
        onClose?.();
        el.remove();
    });
    modal.show();
}

/**
 * 表單未儲存變更追蹤（docs/HISTORY.md #2）：離開頁面前跳出瀏覽器原生確認。
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
 * 目前唯一用途是風險日詳情重點問題表的「選取」欄全選 checkbox（docs/HISTORY.md #7）。
 * rowHref(row)（選填）：回傳非空字串時整列可點導向該網址——列內既有的 <a>/<button>
 * 仍照自己的行為，不被整列連結攔截。
 * rowDetail(row)（選填）：回傳 Node 時，該列下方多一條可展開的細節列（跨欄），
 * 點該列（避開列內連結/按鈕）即展開/收合，首欄前置一個展開箭頭。與 rowHref 互斥使用。
 * sort/onSort（選填，表格排序）：有 sortKey 的欄位表頭變成可點按鈕；點擊時以目前
 * sort（{key, dir}）算出下一個排序狀態（同欄切換 asc/desc，換欄回到 sortDefaultDir
 * ／預設 asc）並呼叫 onSort(key, dir)——切換邏輯集中在這裡，呼叫端只需套用收到的狀態，
 * 不論是重打 API（伺服器分頁頁）還是本地重排（見 sortRows）。
 */
export function renderTable(container, { columns, rows, empty, rowHref, rowDetail, sort, onSort }) {
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
            tr.addEventListener('click', event => {
                // 讓列內的連結／按鈕保有自己的行為，只有點到空白處才走整列導向
                if (event.target.closest('a, button')) return;
                location.href = href;
            });
        }

        const detail = rowDetail ? rowDetail(row) : null;

        for (const [index, col] of columns.entries()) {
            const td = document.createElement('td');
            if (col.className) td.className = col.className;

            // 展開箭頭放在首欄最前面，作為「這列可展開」的視覺提示
            if (detail && index === 0) {
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

        if (detail) {
            const detailRow = document.createElement('tr');
            detailRow.className = 'lf-row-detail d-none';
            const cell = document.createElement('td');
            cell.colSpan = columns.length;
            cell.appendChild(detail);
            detailRow.appendChild(cell);
            tbody.appendChild(detailRow);

            tr.classList.add('lf-row-expandable');
            tr.addEventListener('click', event => {
                if (event.target.closest('a, button')) return;
                const open = detailRow.classList.toggle('d-none');
                tr.classList.toggle('lf-row-open', !open);
            });
        }
    }
    table.appendChild(tbody);
    wrap.appendChild(table);

    container.replaceChildren(wrap);
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
