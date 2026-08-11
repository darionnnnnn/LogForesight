/**
 * 操作紀錄（docs/WEB-SPEC.md §9.11）。
 *
 * summary 由後端在寫入當下組成人話，這裡直接顯示——
 * 前端不從 detailJson 反推敘述（那份規則只該存在一處）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderPagination, loadPageSize, savePageSize, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName, toLocalDateString } from '../core/format.js';

const RESULT_META = {
    ok: { text: '成功', variant: 'light' },
    denied: { text: '被拒', variant: 'danger' },
    failed: { text: '失敗', variant: 'warning' }
};

let currentPage = 1;
let pageSize = loadPageSize('audit');
let sort = { key: 'occurredAt', dir: 'desc' };   // 稽核頁原本恆為新到舊，維持同一預設
let lastResult = null;

async function init() {
    await loadActionOptions();

    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - 6);
    // 本地日期（S12）：toISOString() 取的是 UTC 日期，台灣（UTC+8）凌晨 0~8 點呼叫會少算一天
    document.getElementById('audit-from').value = toLocalDateString(from);
    document.getElementById('audit-to').value = toLocalDateString(to);

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

    updateDeniedButtonState();
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

    params.set('dir', sort.dir);
    params.set('page', String(currentPage));
    params.set('pageSize', String(pageSize));

    lastResult = await api.get(`/api/audit?${params}`);
    render();
}

function render() {
    document.getElementById('audit-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} 筆` : '';

    renderTable(document.getElementById('audit-list'), {
        columns: [
            { title: '時間', sortKey: 'occurredAt', sortDefaultDir: 'desc', render: e => formatDateTime(e.occurredAt) },
            { title: '帳號', render: e => formatUserName(e.accountDisplayName, e.account) },
            { title: '動作', render: e => e.actionText },
            { title: '結果', render: e => resultBadge(e.result) },
            { title: '內容', render: e => summaryCell(e) },
            { title: '來源 IP', render: e => e.ipAddress ?? '' }
        ],
        rows: lastResult.items,
        sort,
        onSort: (key, dir) => {
            sort = { key, dir };
            currentPage = 1;
            search();
        },
        empty: { title: '沒有符合條件的操作紀錄', hint: '請調整日期區間或動作條件。' }
    });

    renderPager();
}

function resultBadge(result) {
    const meta = RESULT_META[result] ?? { text: result, variant: 'secondary' };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${meta.variant}`;
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
    const totalPages = Math.ceil(lastResult.total / lastResult.pageSize);
    renderPagination(document.getElementById('audit-pager'), {
        page: lastResult.page,
        totalPages,
        onPage: page => {
            currentPage = page;
            search();
            window.scrollTo({ top: 0, behavior: 'smooth' });
        },
        pageSize,
        onPageSize: size => {
            pageSize = size;
            savePageSize('audit', size);
            currentPage = 1;
            search();
        }
    });
}

document.getElementById('audit-filter').addEventListener('submit', event => {
    event.preventDefault();
    currentPage = 1;
    updateDeniedButtonState();
    search();
});

/** 被拒的存取是稽核上最有價值的一類——給它一個一鍵入口。再按一次可以取消篩選
 * （回饋十六輪批次E-1：原本只能設定、無法從按鈕取消，只能改用下拉選單清回「全部」）。 */
document.getElementById('btn-denied').addEventListener('click', () => {
    const select = document.getElementById('audit-result');
    select.value = select.value === 'Denied' ? '' : 'Denied';
    updateDeniedButtonState();
    currentPage = 1;
    search();
});

/** 手動改下拉選單、URL 下鑽（result=Denied）與按鈕點擊三個途徑最終都收斂到同一個
 * select 值，按鈕的啟用外觀（Bootstrap active class）跟著同步，不會出現「篩選其實是被拒、
 * 但按鈕看起來沒按下」的不一致。 */
document.getElementById('audit-result').addEventListener('change', updateDeniedButtonState);

function updateDeniedButtonState() {
    document.getElementById('btn-denied').classList.toggle('active', document.getElementById('audit-result').value === 'Denied');
}

// 失敗收斂（docs/archive/FEEDBACK-10-PLAN.md §4）：載入中途出錯時把骨架列換成失敗狀態，不留無限載入
guardLoad(document.getElementById('audit-list'), init);
