/**
 * 稽核紀錄（docs/WEB-SPEC.md §9.11）。
 *
 * summary 由後端在寫入當下組成人話，這裡直接顯示——
 * 前端不從 detailJson 反推敘述（那份規則只該存在一處）。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderPager, loadPageSize, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName, toLocalDateString } from '../core/format.js';
import { createSeqGuard } from '../core/async-guards.js';
import { disclosure, roleText } from '../core/elements.js';

const RESULT_META = {
    ok: { text: '成功', variant: 'light' },
    denied: { text: '被拒', variant: 'danger' },
    failed: { text: '失敗', variant: 'warning' }
};

let currentPage = 1;
let pageSize = loadPageSize('audit');
let sort = { key: 'occurredAt', dir: 'desc' };   // 稽核頁原本恆為新到舊，維持同一預設
let lastResult = null;
const searchGuard = createSeqGuard();   // 連點篩選／換頁時，慢的舊回應不得蓋掉新結果

async function init() {
    await loadActionOptions();

    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - 6);
    // 本地日期（S12）：toISOString() 取的是 UTC 日期，台灣（UTC+8）凌晨 0~8 點呼叫會少算一天
    document.getElementById('audit-from').value = toLocalDateString(from);
    document.getElementById('audit-to').value = toLocalDateString(to);

    // 套用 URL 帶入的篩選條件（§8.4 下鑽）：儀表板的登入失敗卡會導向
    // /audit?actions=login_failed&from=…&to=…，不讀 URL 的話下鑽連結等於壞的
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

/**
 * 動作分組（純前端，依代碼前綴；前一組先比對）：沒命中任何前綴的新動作歸「其他」，不會因為對照表沒更新而消失。
 * 分組只影響下拉的呈現，動作代碼與名稱仍由後端對照表提供。
 */
const ACTION_GROUPS = [
    ['身分與存取', /^(login|logout|session_|access_|perm_)/],
    ['處理與交辦', /^(handling_|issue_bulk_close|work_order_|escalation_)/],
    ['問題、規則與靜音', /^(rule_|suppress_|issue_)/],
    ['使用者、主機與群組', /^(user_|host_|group_)/],
    ['匯入與資料來源', /^(import_|netiq_|sentinel_)/],
    ['PRTG', /^(prtg_|calibration_)/],
    ['排程與系統', /^(schedule_|settings_|health_|run_|ai_)/]
];
const OTHER_GROUP = '其他';

/** 把 { 代碼: 名稱 } 分成 [{ label, entries: [[代碼, 名稱]] }]；組內依名稱排序、沒有項目的組不出現，「其他」固定在最後 */
export function groupAuditActions(actions) {
    const buckets = new Map([...ACTION_GROUPS.map(([label]) => label), OTHER_GROUP].map(label => [label, []]));
    for (const [code, name] of Object.entries(actions || {})) {
        const group = ACTION_GROUPS.find(([, pattern]) => pattern.test(code));
        buckets.get(group ? group[0] : OTHER_GROUP).push([code, name]);
    }
    return [...buckets]
        .filter(([, entries]) => entries.length > 0)
        .map(([label, entries]) => ({ label, entries: entries.sort((a, b) => a[1].localeCompare(b[1], 'zh-Hant')) }));
}

/** 搜尋比對：名稱或代碼包含關鍵字（不分大小寫）；沒輸入關鍵字全部符合 */
export function auditActionMatches(code, name, query) {
    const q = String(query || '').trim().toLowerCase();
    return q === '' || String(name).toLowerCase().includes(q) || String(code).toLowerCase().includes(q);
}

/**
 * 依關鍵字顯示／隱藏下拉的選項與分組標題：已勾選的選項一律保留（搜尋時不能讓人看不到自己選了什麼），
 * 整組都隱藏時連分組標題一起隱藏。
 */
export function applyAuditActionSearch(select, query) {
    for (const group of Array.from(select.querySelectorAll('optgroup'))) {
        let anyShown = false;
        for (const option of Array.from(group.querySelectorAll('option'))) {
            const show = option.selected || auditActionMatches(option.value, option.textContent, query);
            option.hidden = !show;
            if (show) anyShown = true;
        }
        group.hidden = !anyShown;
    }
}

/** 動作對照表由後端提供：動作代碼由後端定義，對照表跟著定義走才不會漏 */
async function loadActionOptions() {
    const actions = await api.get('/api/audit/actions');
    const select = document.getElementById('audit-actions');

    for (const { label, entries } of groupAuditActions(actions)) {
        const group = document.createElement('optgroup');
        group.label = label;
        for (const [code, name] of entries) {
            const option = document.createElement('option');
            option.value = code;
            option.textContent = name;
            group.appendChild(option);
        }
        select.appendChild(group);
    }

    document.getElementById('audit-actions-search')?.addEventListener('input', event => {
        applyAuditActionSearch(select, event.target.value);
    });
}

async function search() {
    const container = document.getElementById('audit-list');
    renderLoading(container, 8);

    const params = new URLSearchParams();
    params.set('from', document.getElementById('audit-from').value);
    params.set('to', document.getElementById('audit-to').value);

    const actions = Array.from(document.getElementById('audit-actions').selectedOptions).map(o => o.value);
    if (actions.length) params.set('actions', actions.join(','));

    const resultFilter = document.getElementById('audit-result').value;
    if (resultFilter) params.set('result', resultFilter);

    params.set('dir', sort.dir);
    params.set('page', String(currentPage));
    params.set('pageSize', String(pageSize));

    const seq = searchGuard.next();
    let result;
    try {
        result = await api.get(`/api/audit?${params}`);
    } catch (err) {
        // 過期請求的失敗不處理：使用者此刻在等的是新請求
        if (!searchGuard.isCurrent(seq)) return;
        throw err;
    }
    if (!searchGuard.isCurrent(seq)) return;
    lastResult = result;
    render();
}

/** 篩選、換頁、排序後的重載：失敗時換成可重試的失敗態，不留永遠轉圈的骨架 */
function reload() {
    return guardLoad(document.getElementById('audit-list'), search);
}

function render() {
    document.getElementById('audit-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} 筆` : '';

    renderDefaultRangeNotice(lastResult.defaultRangeApplied === true);

    renderTable(document.getElementById('audit-list'), {
        columns: [
            { title: '時間', sortKey: 'occurredAt', sortDefaultDir: 'desc', render: e => roleText('lf-text-data', formatDateTime(e.occurredAt)) },
            { title: '帳號', render: e => roleText('lf-text-meta', formatUserName(e.accountDisplayName, e.account)) },
            { title: '動作', className: 'text-nowrap', render: e => e.actionText },
            { title: '結果', render: e => resultBadge(e.result) },
            { title: '內容', className: 'lf-minw-md', render: e => summaryCell(e) },
            { title: '來源 IP', render: e => roleText('lf-text-data', e.ipAddress ?? '') }
        ],
        rows: lastResult.items,
        sort,
        onSort: (key, dir) => {
            sort = { key, dir };
            currentPage = 1;
            reload();
        },
        empty: { title: '沒有符合條件的稽核紀錄', hint: '請調整日期區間或動作條件。' }
    });

    renderPager('audit-pager', lastResult, {
        pageSizeKey: 'audit',
        onPage: (page, size) => { currentPage = page; pageSize = size; reload(); }
    });
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
    summary.className = 'lf-text-title';
    summary.textContent = entry.summary;
    wrap.appendChild(summary);

    // 欄位級前後對照放在展開處：清單保持可掃視，需要細節時才點開
    if (entry.detailJson) {
        const box = document.createElement('pre');
        box.className = 'report-text lf-text-data mt-1 d-none';
        try {
            box.textContent = JSON.stringify(JSON.parse(entry.detailJson), null, 2);
        } catch {
            box.textContent = entry.detailJson;
        }

        wrap.append(disclosure('詳細', box), box);
    }

    return wrap;
}

/**
 * 有篩選條件卻沒指定起日時，後端只查近 90 天——在清單上方說明，免得使用者以為更舊的紀錄不存在。
 * 提示列放在清單容器之外（renderTable 會整個重畫清單容器），只建一次、之後切換顯示。
 */
function renderDefaultRangeNotice(applied) {
    let notice = document.getElementById('audit-default-range');
    if (!notice) {
        notice = document.createElement('div');
        notice.id = 'audit-default-range';
        notice.className = 'lf-text-meta px-3 pt-2';
        notice.textContent = '未指定起日，僅顯示近 90 天的紀錄。';
        const list = document.getElementById('audit-list');
        list.parentNode.insertBefore(notice, list);
    }
    notice.hidden = !applied;
}

document.getElementById('audit-filter').addEventListener('submit', event => {
    event.preventDefault();
    currentPage = 1;
    updateDeniedButtonState();
    reload();
});

/** 被拒的存取是稽核上最有價值的一類——給它一個一鍵入口。再按一次可以取消篩選
 * （回饋十六輪批次E-1：原本只能設定、無法從按鈕取消，只能改用下拉選單清回「全部」）。 */
document.getElementById('btn-denied').addEventListener('click', () => {
    const select = document.getElementById('audit-result');
    select.value = select.value === 'Denied' ? '' : 'Denied';
    updateDeniedButtonState();
    currentPage = 1;
    reload();
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
