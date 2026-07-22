/**
 * 問題查詢（docs/WEB-SPEC.md §9.2）。
 *
 * 篩選條件與 URL 查詢字串同步（§8.6-2）——這同時滿足兩件事：
 * 查詢結果可以複製網址給同事，以及讓所有下鑽（§8.4）只需要「組出網址再導頁」，
 * 明細頁不必為下鑽寫任何額外程式碼。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, renderPagination } from '../core/ui.js';
import { riskBadge, handlingBadge, statusBadge } from '../core/format.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

const form = document.getElementById('filter-form');
const listContainer = document.getElementById('record-list');
let currentPage = 1;
let lastResult = null;

async function init() {
    await loadHostOptions();
    applyUrlToForm();
    await search();
}

async function loadHostOptions() {
    const hosts = await api.get('/api/hosts');
    const select = document.getElementById('filter-hosts');

    for (const host of hosts) {
        const option = document.createElement('option');
        option.value = host.hostId;
        option.textContent = host.hostName;
        select.appendChild(option);
    }

    if (hosts.length === 0) {
        select.disabled = true;
    }
}

/** URL → 表單。下鑽進來的連結帶著條件，畫面必須反映它們 */
function applyUrlToForm() {
    const params = new URLSearchParams(location.search);

    setMultiSelect('filter-hosts', params.get('hostIds'));
    setMultiSelect('filter-risk', params.get('riskLevels'));
    setMultiSelect('filter-category', params.get('categories'));
    document.getElementById('filter-from').value = params.get('from') ?? defaultFrom();
    document.getElementById('filter-to').value = params.get('to') ?? today();
    document.getElementById('filter-event-id').value = params.get('eventId') ?? '';

    currentPage = Number(params.get('page')) || 1;
}

function setMultiSelect(id, csv) {
    if (!csv) return;
    const values = csv.split(',');
    for (const option of document.getElementById(id).options) {
        option.selected = values.includes(option.value);
    }
}

function collectFilters() {
    return {
        hostIds: selectedValues('filter-hosts'),
        riskLevels: selectedValues('filter-risk'),
        categories: selectedValues('filter-category'),
        from: document.getElementById('filter-from').value,
        to: document.getElementById('filter-to').value,
        eventId: document.getElementById('filter-event-id').value,
        // 下面兩項只由下鑽網址帶入，不放在表單裡（避免主篩選列過度複雜）
        severity: new URLSearchParams(location.search).get('severity') ?? '',
        statuses: new URLSearchParams(location.search).get('statuses') ?? '',
        overdue: new URLSearchParams(location.search).get('overdue') ?? ''
    };
}

function selectedValues(id) {
    return Array.from(document.getElementById(id).selectedOptions).map(o => o.value);
}

function buildQueryString(filters, page) {
    const params = new URLSearchParams();
    if (filters.hostIds.length) params.set('hostIds', filters.hostIds.join(','));
    if (filters.riskLevels.length) params.set('riskLevels', filters.riskLevels.join(','));
    if (filters.categories.length) params.set('categories', filters.categories.join(','));
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);
    if (filters.eventId) params.set('eventId', filters.eventId);
    if (filters.severity) params.set('severity', filters.severity);
    if (filters.statuses) params.set('statuses', filters.statuses);
    if (filters.overdue) params.set('overdue', filters.overdue);
    if (page > 1) params.set('page', String(page));
    return params.toString();
}

async function search() {
    const filters = collectFilters();
    const query = buildQueryString(filters, currentPage);

    // 同步網址：使用者可以直接複製這條網址分享，重新整理也回到同一個結果
    history.replaceState(null, '', query ? `?${query}` : location.pathname);

    renderLoading(listContainer, 6);
    lastResult = await api.get(`/api/records?${query}`);
    render();
}

function render() {
    document.getElementById('result-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} 筆` : '';

    renderTable(listContainer, {
        columns: [
            { title: '日期', render: r => dateLink(r) },
            { title: '主機', render: r => r.hostName },
            { title: '風險', render: r => riskBadge(r.riskLevel) },
            { title: '狀況', render: r => headlineCell(r) },
            { title: '類型', render: r => categoryBadges(r) },
            { title: '處理狀態', render: r => handlingCell(r) },
            { title: '處理人', render: r => r.handlerName ?? '' }
        ],
        rows: lastResult.items,
        empty: {
            title: '沒有符合條件的資料',
            hint: '請調整日期區間或篩選條件；若剛部署，請先確認批次分析已執行過。'
        }
    });

    renderPager();
}

function dateLink(record) {
    const link = document.createElement('a');
    link.href = `/records/${record.hostId}/${record.date}`;
    link.textContent = record.date;
    return link;
}

function headlineCell(record) {
    const wrap = document.createElement('span');

    const text = document.createElement('span');
    text.textContent = record.headline || '（無 AI 摘要）';
    if (!record.aiAnalyzed) text.className = 'text-muted';
    wrap.appendChild(text);

    if (record.hasCorrelation) {
        const badge = statusBadge('關聯訊號', 'danger', {
            icon: 'link-45deg',
            title: '程式確定性比對出的攻擊鏈／故障鏈組合'
        });
        badge.classList.add('ms-2');
        wrap.appendChild(badge);
    }

    // 涵蓋率缺口要顯眼：「沒告警」可能是「沒看」而不是「沒問題」
    if (record.hasCoverageGap) {
        const badge = statusBadge('涵蓋不完整', 'warning', {
            icon: 'exclamation-triangle',
            title: '資料不完整或 Security log 未讀取——沒告警不等於沒問題'
        });
        badge.classList.add('ms-2');
        wrap.appendChild(badge);
    }

    return wrap;
}

function handlingCell(record) {
    const wrap = document.createElement('span');
    wrap.appendChild(handlingBadge(record.handlingStatus));

    if (record.isOverdue) {
        const overdue = document.createElement('span');
        overdue.className = 'lf-badge lf-badge--danger ms-1';
        overdue.textContent = '逾期';
        wrap.appendChild(overdue);
    }

    return wrap;
}

function categoryBadges(record) {
    const wrap = document.createElement('span');
    for (const category of record.categories) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--light border me-1';
        badge.textContent = CATEGORY_NAMES[category] ?? category;
        wrap.appendChild(badge);
    }
    return wrap;
}

function renderPager() {
    const totalPages = Math.ceil(lastResult.total / lastResult.pageSize);
    renderPagination(document.getElementById('pager'), {
        page: lastResult.page,
        totalPages,
        onPage: page => {
            currentPage = page;
            search();
            window.scrollTo({ top: 0, behavior: 'smooth' });
        }
    });
}

// ── 事件 ─────────────────────────────────────────────────────────────────────

form.addEventListener('submit', event => {
    event.preventDefault();
    currentPage = 1;
    search();
});

document.getElementById('btn-reset').addEventListener('click', () => {
    location.href = location.pathname;
});

for (const button of document.querySelectorAll('[data-range]')) {
    button.addEventListener('click', () => {
        const days = Number(button.dataset.range);
        const from = new Date();
        from.setDate(from.getDate() - days + 1);

        document.getElementById('filter-from').value = from.toISOString().slice(0, 10);
        document.getElementById('filter-to').value = today();
        currentPage = 1;
        search();
    });
}

/** 複製為 CSV：前端序列化當前頁，零後端成本（§8.6-7） */
document.getElementById('btn-copy-csv').addEventListener('click', async () => {
    if (!lastResult || lastResult.items.length === 0) {
        toast('目前沒有可複製的資料', 'warning');
        return;
    }

    const header = ['日期', '主機', '風險', '狀況', '類型', '錯誤數', '警告數'];
    const lines = [header.join(',')];

    for (const item of lastResult.items) {
        lines.push([
            item.date,
            quote(item.hostName),
            item.riskLevel,
            quote(item.headline),
            quote(item.categories.map(c => CATEGORY_NAMES[c] ?? c).join(';')),
            item.errorCount,
            item.warningCount
        ].join(','));
    }

    try {
        await navigator.clipboard.writeText(lines.join('\r\n'));
        toast(`已複製 ${lastResult.items.length} 筆資料`, 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
});

function quote(value) {
    const text = String(value ?? '');
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function today() {
    return new Date().toISOString().slice(0, 10);
}

function defaultFrom() {
    const date = new Date();
    date.setDate(date.getDate() - 6);
    return date.toISOString().slice(0, 10);
}

init();
