/**
 * 操作紀錄（docs/WEB-SPEC.md §9.11）。
 *
 * summary 由後端在寫入當下組成人話，這裡直接顯示——
 * 前端不從 detailJson 反推敘述（那份規則只該存在一處）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const RESULT_META = {
    ok: { text: '成功', variant: 'light' },
    denied: { text: '被拒', variant: 'danger' },
    failed: { text: '失敗', variant: 'warning' }
};

let currentPage = 1;
let lastResult = null;

async function init() {
    await loadActionOptions();

    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - 6);
    document.getElementById('audit-from').value = from.toISOString().slice(0, 10);
    document.getElementById('audit-to').value = to.toISOString().slice(0, 10);

    // 套用 URL 帶入的篩選條件（§8.4 下鑽）：儀表板的登入失敗卡會導向
    // /audit?result=Denied，不讀 URL 的話下鑽連結等於壞的
    const params = new URLSearchParams(location.search);
    if (params.get('result')) document.getElementById('audit-result').value = params.get('result');
    if (params.get('from')) document.getElementById('audit-from').value = params.get('from');
    if (params.get('to')) document.getElementById('audit-to').value = params.get('to');
    if (params.get('actions')) {
        const wanted = params.get('actions').split(',');
        for (const option of document.getElementById('audit-actions').options) {
            option.selected = wanted.includes(option.value);
        }
    }

    await search();
}

/** 動作對照表由後端提供：動作代碼由後端定義，對照表跟著定義走才不會漏 */
async function loadActionOptions() {
    const actions = await api.get('/api/audit/actions');
    const select = document.getElementById('audit-actions');

    for (const [code, name] of Object.entries(actions)) {
        const option = document.createElement('option');
        option.value = code;
        option.textContent = name;
        select.appendChild(option);
    }
}

async function search() {
    const container = document.getElementById('audit-list');
    renderLoading(container, 8);

    const params = new URLSearchParams();
    params.set('from', document.getElementById('audit-from').value);
    params.set('to', document.getElementById('audit-to').value);

    const actions = Array.from(document.getElementById('audit-actions').selectedOptions).map(o => o.value);
    if (actions.length) params.set('actions', actions.join(','));

    const result = document.getElementById('audit-result').value;
    if (result) params.set('result', result);

    params.set('page', String(currentPage));

    lastResult = await api.get(`/api/audit?${params}`);
    render();
}

function render() {
    document.getElementById('audit-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} 筆` : '';

    renderTable(document.getElementById('audit-list'), {
        columns: [
            { title: '時間', render: e => formatDateTime(e.occurredAt) },
            { title: '帳號', render: e => e.account },
            { title: '動作', render: e => e.actionText },
            { title: '結果', render: e => resultBadge(e.result) },
            { title: '內容', render: e => summaryCell(e) },
            { title: '來源 IP', render: e => e.ipAddress ?? '' }
        ],
        rows: lastResult.items,
        empty: { title: '沒有符合條件的操作紀錄', hint: '請調整日期區間或動作條件。' }
    });

    renderPager();
}

function resultBadge(result) {
    const meta = RESULT_META[result] ?? { text: result, variant: 'secondary' };
    const span = document.createElement('span');
    span.className = `badge text-bg-${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

function summaryCell(entry) {
    const wrap = document.createElement('div');

    const summary = document.createElement('div');
    summary.textContent = entry.summary;
    wrap.appendChild(summary);

    // 欄位級前後對照放在展開處：清單保持可掃視，需要細節時才點開
    if (entry.detailJson) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-link btn-sm p-0';
        toggle.textContent = '詳細';

        const box = document.createElement('pre');
        box.className = 'report-text small mt-1 d-none';
        try {
            box.textContent = JSON.stringify(JSON.parse(entry.detailJson), null, 2);
        } catch {
            box.textContent = entry.detailJson;
        }

        toggle.addEventListener('click', () => box.classList.toggle('d-none'));
        wrap.append(toggle, box);
    }

    return wrap;
}

function renderPager() {
    const pager = document.getElementById('audit-pager');
    pager.replaceChildren();

    const totalPages = Math.ceil(lastResult.total / lastResult.pageSize);
    if (totalPages <= 1) return;

    const list = document.createElement('ul');
    list.className = 'pagination pagination-sm mb-0';

    const addItem = (label, page, disabled = false, active = false) => {
        const item = document.createElement('li');
        item.className = `page-item${disabled ? ' disabled' : ''}${active ? ' active' : ''}`;

        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'page-link';
        link.textContent = label;
        link.disabled = disabled;
        link.addEventListener('click', () => {
            currentPage = page;
            search();
            window.scrollTo({ top: 0, behavior: 'smooth' });
        });

        item.appendChild(link);
        list.appendChild(item);
    };

    addItem('上一頁', lastResult.page - 1, lastResult.page <= 1);
    for (let p = 1; p <= totalPages; p++) {
        if (totalPages > 9 && Math.abs(p - lastResult.page) > 3 && p !== 1 && p !== totalPages) continue;
        addItem(String(p), p, false, p === lastResult.page);
    }
    addItem('下一頁', lastResult.page + 1, lastResult.page >= totalPages);

    pager.appendChild(list);
}

document.getElementById('audit-filter').addEventListener('submit', event => {
    event.preventDefault();
    currentPage = 1;
    search();
});

/** 被拒的存取是稽核上最有價值的一類——給它一個一鍵入口 */
document.getElementById('btn-denied').addEventListener('click', () => {
    document.getElementById('audit-result').value = 'Denied';
    currentPage = 1;
    search();
});

init();
