/**
 * 共用 UI 元件（docs/WEB-SPEC.md §8.1）：toast、確認對話框、表格渲染、載入/空狀態。
 *
 * 集中在這裡的理由與 api.js 相同——「破壞性操作要二次確認」「空狀態要有指引」
 * 「載入中要有回饋」這些規範（§8.6）如果靠每頁自己實作，遲早有頁面漏掉。
 */

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
 */
export function renderTable(container, { columns, rows, empty }) {
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
export function renderEmpty(container, { title = '尚無資料', hint = '' } = {}) {
    const el = document.createElement('div');
    el.className = 'lf-empty';

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
