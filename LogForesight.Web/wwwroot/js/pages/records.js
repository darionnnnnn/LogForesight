/**
 * 問題查詢（docs/WEB-SPEC.md §9.2）。
 *
 * 篩選條件與 URL 查詢字串同步（§8.6-2）——查詢結果可以複製網址給同事，
 * 所有下鑽（§8.4）只需要「組出網址再導頁」，明細頁不必為下鑽寫額外程式碼。
 *
 * 三個檢視角度共用同一條篩選列與同一組 URL 參數（view=detail|host|date）：
 *   - 明細：一列一筆風險日（主機×日期）
 *   - 依主機：日期合併，一列一台主機
 *   - 依日期：主機合併，一列一天
 * 風險層級與風險類型是即點即篩的 chip；主機／日期／Event ID 走表單套用。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, renderPagination } from '../core/ui.js';
import { riskBadge, handlingBadge, statusBadge, CATEGORY_NAMES } from '../core/format.js';

// 預設不顯示低風險：清單常被低風險的雜訊淹沒，真正要處理的高／中反而被推到後面
const DEFAULT_RISKS = ['高', '中'];

const form = document.getElementById('filter-form');
const listContainer = document.getElementById('record-list');

let currentView = 'detail';
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

    if (hosts.length === 0) select.disabled = true;
}

/** URL → 表單／chip。下鑽進來的連結帶著條件，畫面必須反映它們 */
function applyUrlToForm() {
    const params = new URLSearchParams(location.search);

    setMultiSelect('filter-hosts', params.get('hostIds'));
    // riskLevels 參數不存在＝首次進頁，套預設高＋中；存在（含空字串）＝尊重使用者的選擇
    setChips('filter-risk-chips', 'risk',
        params.has('riskLevels') ? splitCsv(params.get('riskLevels')) : DEFAULT_RISKS);
    setChips('filter-category-chips', 'category', splitCsv(params.get('categories')));
    setChips('filter-status-chips', 'status', splitCsv(params.get('statuses')));
    document.getElementById('filter-from').value = params.get('from') ?? defaultFrom();
    document.getElementById('filter-to').value = params.get('to') ?? today();
    document.getElementById('filter-event-id').value = params.get('eventId') ?? '';

    currentView = ['detail', 'host', 'date'].includes(params.get('view')) ? params.get('view') : 'detail';
    setActiveView(currentView);
    currentPage = Number(params.get('page')) || 1;
}

function splitCsv(csv) {
    return csv ? csv.split(',').filter(Boolean) : [];
}

function setMultiSelect(id, csv) {
    if (!csv) return;
    const values = csv.split(',');
    for (const option of document.getElementById(id).options) {
        option.selected = values.includes(option.value);
    }
}

function setChips(containerId, attr, values) {
    const wanted = new Set(values);
    for (const btn of document.querySelectorAll(`#${containerId} [data-${attr}]`)) {
        btn.classList.toggle('active', wanted.has(btn.dataset[attr]));
    }
}

function activeChips(containerId, attr) {
    return Array.from(document.querySelectorAll(`#${containerId} [data-${attr}].active`))
        .map(btn => btn.dataset[attr]);
}

function setActiveView(view) {
    for (const btn of document.querySelectorAll('#view-toggle [data-view]')) {
        btn.classList.toggle('active', btn.dataset.view === view);
    }

    // 彙總視角的 API 不支援處理狀態篩選——chip 停用而不是默默忽略，
    // 否則使用者選了「未處理」卻看到全部，會以為篩選壞掉
    const supportsStatus = view === 'detail';
    for (const btn of document.querySelectorAll('#filter-status-chips button')) {
        btn.disabled = !supportsStatus;
        btn.title = supportsStatus ? '' : '依主機／依日期視角不支援處理狀態篩選';
    }
}

function collectFilters() {
    return {
        hostIds: selectedValues('filter-hosts'),
        riskLevels: activeChips('filter-risk-chips', 'risk'),
        categories: activeChips('filter-category-chips', 'category'),
        from: document.getElementById('filter-from').value,
        to: document.getElementById('filter-to').value,
        eventId: document.getElementById('filter-event-id').value,
        // 處理狀態現在是可見的 chip（不再只由下鑽網址帶入）
        statuses: activeChips('filter-status-chips', 'status').join(','),
        // severity/overdue 仍只由下鑽帶入，畫面以可移除的條件標籤顯示（見 renderActiveConditions）
        severity: new URLSearchParams(location.search).get('severity') ?? '',
        overdue: new URLSearchParams(location.search).get('overdue') ?? ''
    };
}

function selectedValues(id) {
    return Array.from(document.getElementById(id).selectedOptions).map(o => o.value);
}

function buildQueryString(filters, page) {
    const params = new URLSearchParams();
    if (filters.hostIds.length) params.set('hostIds', filters.hostIds.join(','));
    // riskLevels 一律寫入（即使為空）——空字串代表「使用者選了不限」，與「首次進頁」要分得出來
    params.set('riskLevels', filters.riskLevels.join(','));
    if (filters.categories.length) params.set('categories', filters.categories.join(','));
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);
    if (filters.eventId) params.set('eventId', filters.eventId);
    if (filters.severity) params.set('severity', filters.severity);
    if (filters.statuses) params.set('statuses', filters.statuses);
    if (filters.overdue) params.set('overdue', filters.overdue);
    if (currentView !== 'detail') params.set('view', currentView);
    if (page > 1) params.set('page', String(page));
    return params.toString();
}

/** 明細視角才支援處理狀態／逾期篩選；彙總視角不帶這兩個參數 */
const ENDPOINT = { detail: '/api/records', host: '/api/records/by-host', date: '/api/records/by-date' };

async function search() {
    const filters = collectFilters();
    const query = buildQueryString(filters, currentPage);

    // 同步網址：可直接複製分享，重新整理回到同一個結果與同一個視角
    history.replaceState(null, '', query ? `?${query}` : location.pathname);

    renderActiveConditions(filters);
    renderLoading(listContainer, 6);
    lastResult = await api.get(`${ENDPOINT[currentView]}?${query}`);
    render();
}

/**
 * 下鑽帶入的隱藏條件（severity/overdue）顯性化為可移除標籤——否則使用者只看到
 * 「為什麼只有這幾筆」卻在篩選列找不到原因。點 ✕ 移除該條件並重查。
 */
function renderActiveConditions(filters) {
    const container = document.getElementById('active-conditions');
    if (!container) return;
    container.replaceChildren();

    const tags = [];
    if (filters.severity) tags.push({ label: `嚴重度：${filters.severity}`, param: 'severity' });
    if (filters.overdue === 'true') tags.push({ label: '只看逾期', param: 'overdue' });

    for (const tag of tags) {
        const chip = document.createElement('span');
        chip.className = 'lf-badge lf-badge--primary d-inline-flex align-items-center gap-1';

        const text = document.createElement('span');
        text.textContent = tag.label;
        chip.appendChild(text);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn-close btn-close-sm';
        remove.setAttribute('aria-label', `移除條件：${tag.label}`);
        remove.style.fontSize = '.6rem';
        remove.addEventListener('click', () => {
            // severity/overdue 只存在 URL，移除＝從 URL 拿掉再重查
            const params = new URLSearchParams(location.search);
            params.delete(tag.param);
            history.replaceState(null, '', `?${params.toString()}`);
            currentPage = 1;
            search();
        });
        chip.appendChild(remove);
        container.appendChild(chip);
    }
}

function render() {
    document.getElementById('result-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} ${currentView === 'detail' ? '筆' : (currentView === 'host' ? '台主機' : '天')}` : '';

    if (currentView === 'host') renderHostView();
    else if (currentView === 'date') renderDateView();
    else renderDetailView();

    renderPager();
}

// ── 明細視角 ─────────────────────────────────────────────────────────────────

function renderDetailView() {
    renderTable(listContainer, {
        columns: [
            { title: '日期', render: r => dateLink(r) },
            { title: '主機', render: r => r.hostName },
            { title: '風險', render: r => riskBadge(r.riskLevel) },
            { title: '狀況', render: r => headlineCell(r) },
            { title: '類型', render: r => categoryBadges(r.categories) },
            { title: '處理狀態', render: r => handlingCell(r) },
            { title: '處理人', render: r => r.handlerName ?? '' }
        ],
        rows: lastResult.items,
        rowHref: r => `/records/${r.hostId}/${r.date}${detailQuery()}`,
        empty: {
            title: '沒有符合條件的資料',
            hint: '請調整日期區間或篩選條件；若剛部署，請先確認批次分析已執行過。'
        }
    });
}

/** 類別條件跟著連結進明細（§8.4 下鑽上下文不中斷）：明細頁會高亮並捲到對應的問題分節 */
function detailQuery() {
    const categories = activeChips('filter-category-chips', 'category');
    return categories.length ? `?categories=${encodeURIComponent(categories.join(','))}` : '';
}

function dateLink(record) {
    const link = document.createElement('a');
    link.href = `/records/${record.hostId}/${record.date}${detailQuery()}`;
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

    // 問題結案進度（方案 B）：未全部結案時顯示 N/M，讓「處理中」看得出還剩幾項
    if (record.totalIssues > 0 && record.closedIssues < record.totalIssues) {
        const progress = document.createElement('span');
        progress.className = 'text-muted small ms-2';
        progress.textContent = `${record.closedIssues}/${record.totalIssues}`;
        progress.title = '已結案問題數 / 當日問題總數';
        wrap.appendChild(progress);
    }

    return wrap;
}

// ── 依主機視角（日期合併）────────────────────────────────────────────────────

function renderHostView() {
    renderTable(listContainer, {
        columns: [
            { title: '主機', render: h => textCell(h.hostName) },
            { title: '高風險', className: 'text-end', render: h => String(h.highRiskDays) },
            { title: '中風險', className: 'text-end', render: h => String(h.mediumRiskDays) },
            { title: '低風險', className: 'text-end', render: h => String(h.lowRiskDays) },
            { title: '關聯訊號', className: 'text-end', render: h => correlationCell(h.correlationDays) },
            { title: '類型', render: h => categoryBadges(h.categories) },
            { title: '最新狀況', render: h => `${h.latestDate}　${h.latestHeadline}` }
        ],
        rows: lastResult.items,
        rowHref: h => h.hostId > 0 ? `/hosts/${h.hostId}` : null,
        empty: { title: '沒有符合條件的主機', hint: '請調整篩選條件或日期區間。' }
    });
}

// ── 依日期視角（主機合併）────────────────────────────────────────────────────

function renderDateView() {
    renderTable(listContainer, {
        columns: [
            { title: '日期', render: d => dateViewLink(d) },
            { title: '主機數', className: 'text-end', render: d => String(d.hostCount) },
            { title: '高風險', className: 'text-end', render: d => String(d.highRiskHosts) },
            { title: '中風險', className: 'text-end', render: d => String(d.mediumRiskHosts) },
            { title: '低風險', className: 'text-end', render: d => String(d.lowRiskHosts) },
            { title: '關聯訊號', className: 'text-end', render: d => correlationCell(d.correlationHosts) },
            { title: '類型', render: d => categoryBadges(d.categories) }
        ],
        rows: lastResult.items,
        // 點某天 → 切到明細視角並鎖定這天（單日區間）
        rowHref: d => detailForDate(d.date),
        empty: { title: '沒有符合條件的日期', hint: '請調整篩選條件或日期區間。' }
    });
}

/** 依日期視角下鑽到明細：沿用目前篩選，換成明細視角並鎖定單日區間 */
function detailForDate(date) {
    const params = new URLSearchParams(buildQueryString(collectFilters(), 1));
    params.delete('view');   // 明細是預設視角，不需要參數
    params.delete('page');
    params.set('from', date);
    params.set('to', date);
    return `?${params.toString()}`;
}

function dateViewLink(row) {
    const link = document.createElement('a');
    link.href = detailForDate(row.date);
    link.textContent = row.date;
    return link;
}

// ── 共用元件 ─────────────────────────────────────────────────────────────────

function textCell(value) {
    const span = document.createElement('span');
    span.textContent = value;
    return span;
}

function correlationCell(count) {
    if (!count) return '';
    const span = document.createElement('span');
    span.className = 'text-danger fw-semibold';
    span.textContent = String(count);
    span.title = '有攻擊鏈／故障鏈的關聯訊號';
    return span;
}

function categoryBadges(categories) {
    // 有類別篩選時，命中的類別 badge 用主色標示，看得出哪個是你篩的
    const active = new Set(activeChips('filter-category-chips', 'category'));
    const wrap = document.createElement('span');
    for (const category of categories) {
        const badge = document.createElement('span');
        badge.className = active.has(category)
            ? 'lf-badge lf-badge--primary me-1'
            : 'lf-badge lf-badge--light border me-1';
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

// chip：即點即篩（免按套用）
for (const container of ['filter-risk-chips', 'filter-category-chips', 'filter-status-chips']) {
    document.getElementById(container).addEventListener('click', event => {
        const btn = event.target.closest('button[data-risk], button[data-category], button[data-status]');
        if (!btn) return;
        btn.classList.toggle('active');
        currentPage = 1;
        search();
    });
}

// 視角切換：換 endpoint 重查，篩選條件不變
document.getElementById('view-toggle').addEventListener('click', event => {
    const btn = event.target.closest('[data-view]');
    if (!btn || btn.dataset.view === currentView) return;
    currentView = btn.dataset.view;
    setActiveView(currentView);
    currentPage = 1;
    search();
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

/** 複製為 CSV：前端序列化當前頁，零後端成本（§8.6-7）。欄位隨視角而異 */
document.getElementById('btn-copy-csv').addEventListener('click', async () => {
    if (!lastResult || lastResult.items.length === 0) {
        toast('目前沒有可複製的資料', 'warning');
        return;
    }

    const lines = [csvHeader().join(',')];
    for (const item of lastResult.items) lines.push(csvRow(item).join(','));

    try {
        await navigator.clipboard.writeText(lines.join('\r\n'));
        toast(`已複製 ${lastResult.items.length} 筆資料`, 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
});

function csvHeader() {
    if (currentView === 'host') return ['主機', '高風險', '中風險', '低風險', '關聯訊號', '類型', '最新日期', '最新狀況'];
    if (currentView === 'date') return ['日期', '主機數', '高風險', '中風險', '低風險', '關聯訊號', '類型'];
    return ['日期', '主機', '風險', '狀況', '類型', '處理狀態', '處理人'];
}

function csvRow(item) {
    const cats = c => quote((c ?? []).map(x => CATEGORY_NAMES[x] ?? x).join(';'));
    if (currentView === 'host') {
        return [quote(item.hostName), item.highRiskDays, item.mediumRiskDays, item.lowRiskDays,
            item.correlationDays, cats(item.categories), item.latestDate, quote(item.latestHeadline)];
    }
    if (currentView === 'date') {
        return [item.date, item.hostCount, item.highRiskHosts, item.mediumRiskHosts, item.lowRiskHosts,
            item.correlationHosts, cats(item.categories)];
    }
    return [item.date, quote(item.hostName), item.riskLevel, quote(item.headline),
        cats(item.categories), quote(item.handlingStatusText), quote(item.handlerName ?? '')];
}

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
