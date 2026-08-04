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

import { api, getDisplaySettings, getCurrentUser, hasCapability } from '../core/api.js';
import {
    renderTable, renderLoading, renderSpinner, renderEmpty, toast, renderPagination, withBusy, renderChips,
    loadPageSize, savePageSize, PAGE_SIZE_OPTIONS, showDetailModal
} from '../core/ui.js';
import { riskBadge, handlingBadge, statusBadge, severityBadge, CATEGORY_NAMES, severityName, formatNumber, formatUserName, toLocalDateString, todayLocal } from '../core/format.js';
import { renderAiText } from '../core/markdown-lite.js';

// 預設不顯示低風險：清單常被低風險的雜訊淹沒，真正要處理的高／中反而被推到後面
const DEFAULT_RISKS = ['高', '中'];

const form = document.getElementById('filter-form');
const listContainer = document.getElementById('record-list');

let currentView = 'detail';
let currentPage = 1;
let pageSize = loadPageSize('records');
// 表頭排序：key 隨視角而異（明細 date/host/risk；依主機 host/highRisk/...；依日期 date/hostCount/...），
// 因此視角切換時重設（見 view-toggle 事件），不像篩選條件那樣沿用
let sort = { key: '', dir: 'desc' };
let lastResult = null;

let aiAvailable = false;

// 依問題視角批次指派（docs/FEEDBACK-4-PLAN.md §4）：只有 Assign 能力才顯示「指派」鈕
let currentUser = null;

// 主機篩選（§5.4 D-4）：兩千台規模下不能把全部主機灌進一個 <select>，
// 改成搜尋式 autocomplete＋已選主機顯示為可移除 chip。key 用字串，與 URL/DOM dataset 一致
const selectedHosts = new Map();   // hostId(string) → hostName
let hostGroups = [];
const activeGroupIds = new Set();  // groupId(string)

async function init() {
    applyUrlToForm();
    currentUser = await getCurrentUser();
    await Promise.all([resolveSelectedHostsFromUrl(), loadGroupOptions(), applyDayRiskVisibility()]);
    await search();
    initAiSummary();
    setupHostAutocomplete();
}

/**
 * 日風險等級顯示（docs/FEEDBACK-3-PLAN.md #8）：隱藏被全站「日風險等級顯示」設定藏起來的
 * 篩選 chip——留著會讓使用者點選一個「選了也查不到東西」的條件（後端已在 RecordRepository
 * 過濾掉該等級，勾選它只會得到空結果，卻看不出是「真的沒有」還是「被藏起來」）。
 *
 * 隱藏的同時**取消 active**：applyUrlToForm 可能已依 URL／預設值把被藏等級點亮
 * （例如下鑽連結帶 riskLevels=中），只藏不取消會留下一個看不見卻仍生效的篩選——
 * 使用者面對空清單、又沒有任何可點的東西能解除它，正是隱藏 chip 想避免的死路。
 * 在 init 的首次 search() 之前執行，取消後的條件才是實際送出的查詢。
 */
async function applyDayRiskVisibility() {
    const settings = await getDisplaySettings();
    const visible = new Set(settings?.visibleDayRiskLevels ?? ['高', '中', '低']);
    for (const btn of document.querySelectorAll('#filter-risk-chips [data-risk]')) {
        const hidden = !visible.has(btn.dataset.risk);
        btn.classList.toggle('d-none', hidden);
        if (hidden) btn.classList.remove('active');
    }
}

/**
 * AI 歸納（docs/HISTORY.md §6 W1-2）：使用者主動點才呼叫（查詢頁高頻，
 * 自動呼叫會塞爆 AI 佇列）。只在明細視角、且 AI 可用時顯示按鈕。
 */
async function initAiSummary() {
    try {
        const status = await api.get('/api/ai/status', { silent: true });
        aiAvailable = !!status?.available;
    } catch {
        aiAvailable = false;
    }
    updateAiSummaryButton();

    document.getElementById('btn-ai-summary').addEventListener('click', async () => {
        const area = document.getElementById('ai-summary');
        const button = document.getElementById('btn-ai-summary');
        const restore = withBusy(button, '歸納中');
        try {
            const params = new URLSearchParams(location.search);
            const result = await api.get(`/api/ai/query-summary?${params.toString()}`, { silent: true });
            area.replaceChildren();
            if (!result || !result.text) {
                toast('目前沒有跨主機的共通訊號可歸納', 'info');
                return;
            }
            const box = document.createElement('div');
            box.className = 'alert alert-light border mb-0';
            // AI 產出走 markdown-lite 唯一渲染出口（S7）：DOM 組裝、不解析 HTML
            renderAiText(box, result.text, { badge: 'AI 歸納', badgeClassName: 'lf-badge lf-badge--secondary me-2' });
            area.appendChild(box);
        } catch {
            // 靜默
        } finally {
            restore();
        }
    });
}

function updateAiSummaryButton() {
    // 只有明細視角支援歸納（彙總視角沒有逐筆問題可聚類）
    document.getElementById('btn-ai-summary').classList.toggle('d-none', !aiAvailable || currentView !== 'detail');
    if (currentView !== 'detail') document.getElementById('ai-summary').replaceChildren();
}

/**
 * 網址帶的 hostIds 只有數字，畫面上的 chip 需要顯示名稱——用 ids= 參數精確取回
 * （不受 query= 搜尋的 20 筆上限），下鑽連結（例如報表的主機排行）才能正確還原成 chip。
 */
async function resolveSelectedHostsFromUrl() {
    const params = new URLSearchParams(location.search);
    const csv = params.get('hostIds');
    if (!csv) return;

    try {
        const hosts = await api.get(`/api/hosts?ids=${encodeURIComponent(csv)}`, { silent: true });
        for (const host of hosts) selectedHosts.set(String(host.hostId), host.hostName);
    } catch {
        // 解析失敗就不顯示 chip；篩選條件本身仍在 URL 上，重新查詢不受影響
    }
    renderHostChips();
}

function renderHostChips() {
    const container = document.getElementById('filter-host-chips');
    container.replaceChildren();

    for (const [id, name] of selectedHosts) {
        const chip = document.createElement('span');
        chip.className = 'lf-badge lf-badge--primary d-inline-flex align-items-center gap-1';

        const text = document.createElement('span');
        text.textContent = name;
        chip.appendChild(text);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn-close btn-close-sm';
        remove.style.fontSize = '.6rem';
        remove.setAttribute('aria-label', `移除主機：${name}`);
        remove.addEventListener('click', () => {
            selectedHosts.delete(id);
            renderHostChips();
            currentPage = 1;
            search();
        });
        chip.appendChild(remove);

        container.appendChild(chip);
    }
}

/** 搜尋式主機 autocomplete：輸入 2 字元後查伺服器（防抖 250ms），點建議項加入 chip */
function setupHostAutocomplete() {
    const input = document.getElementById('filter-host-search');
    const suggestions = document.getElementById('filter-host-suggestions');
    let debounceTimer = null;

    input.addEventListener('input', () => {
        clearTimeout(debounceTimer);
        const value = input.value.trim();
        if (value.length < 2) {
            hideHostSuggestions();
            return;
        }
        debounceTimer = setTimeout(() => loadHostSuggestions(value), 250);
    });

    document.addEventListener('click', event => {
        if (!event.target.closest('#filter-host-search, #filter-host-suggestions')) hideHostSuggestions();
    });

    async function loadHostSuggestions(query) {
        let hosts;
        try {
            hosts = await api.get(`/api/hosts?query=${encodeURIComponent(query)}`, { silent: true });
        } catch {
            return;
        }

        const candidates = hosts.filter(h => !selectedHosts.has(String(h.hostId)));
        suggestions.replaceChildren();

        if (candidates.length === 0) {
            suggestions.classList.remove('show');
            return;
        }

        for (const host of candidates) {
            const item = document.createElement('button');
            item.type = 'button';
            item.className = 'dropdown-item small';
            item.textContent = host.hostName;
            item.addEventListener('click', () => {
                selectedHosts.set(String(host.hostId), host.hostName);
                renderHostChips();
                hideHostSuggestions();
                input.value = '';
                currentPage = 1;
                search();
            });
            suggestions.appendChild(item);
        }
        suggestions.classList.add('show');
    }

    function hideHostSuggestions() {
        suggestions.replaceChildren();
        suggestions.classList.remove('show');
    }
}

/** 主機群組 chip（§5.4 D-4）：只列出目前使用者看得到主機所屬的群組，選了不限的角色不會看到空群組 */
async function loadGroupOptions() {
    const params = new URLSearchParams(location.search);
    for (const id of splitCsv(params.get('groupIds'))) activeGroupIds.add(id);

    try {
        hostGroups = await api.get('/api/hosts/groups', { silent: true });
    } catch {
        hostGroups = [];
    }

    const row = document.getElementById('filter-group-row');
    row.classList.toggle('d-none', hostGroups.length === 0);
    if (hostGroups.length === 0) return;

    renderChips(document.getElementById('filter-group-chips'), {
        items: hostGroups.map(g => ({ value: String(g.groupId), label: g.groupName })),
        attr: 'group',
        activeValues: [...activeGroupIds],
        multi: true,
        onToggle: (value, active) => {
            if (active) activeGroupIds.add(value); else activeGroupIds.delete(value);
            currentPage = 1;
            search();
        }
    });
}

/** URL → 表單／chip。下鑽進來的連結帶著條件，畫面必須反映它們 */
function applyUrlToForm() {
    const params = new URLSearchParams(location.search);

    // riskLevels 參數不存在＝首次進頁，套預設高＋中；存在（含空字串）＝尊重使用者的選擇
    setChips('filter-risk-chips', 'risk',
        params.has('riskLevels') ? splitCsv(params.get('riskLevels')) : DEFAULT_RISKS);
    setChips('filter-category-chips', 'category', splitCsv(params.get('categories')));
    setChips('filter-status-chips', 'status', splitCsv(params.get('statuses')));
    document.getElementById('filter-from').value = params.get('from') ?? defaultFrom();
    document.getElementById('filter-to').value = params.get('to') ?? today();
    document.getElementById('filter-event-id').value = params.get('eventId') ?? '';

    currentView = ['detail', 'host', 'date', 'issue'].includes(params.get('view')) ? params.get('view') : 'detail';
    setActiveView(currentView);
    currentPage = Number(params.get('page')) || 1;
    sort = { key: params.get('sort') ?? '', dir: params.get('dir') === 'asc' ? 'asc' : 'desc' };
    const urlPageSize = Number(params.get('pageSize'));
    pageSize = PAGE_SIZE_OPTIONS.includes(urlPageSize) ? urlPageSize : loadPageSize('records');
}

function splitCsv(csv) {
    return csv ? csv.split(',').filter(Boolean) : [];
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
        btn.title = supportsStatus ? '' : '彙總視角（依主機／依日期／依問題）不支援處理狀態篩選';
    }
}

function collectFilters() {
    return {
        hostIds: [...selectedHosts.keys()],
        groupIds: [...activeGroupIds],
        riskLevels: activeChips('filter-risk-chips', 'risk'),
        categories: activeChips('filter-category-chips', 'category'),
        from: document.getElementById('filter-from').value,
        to: document.getElementById('filter-to').value,
        eventId: document.getElementById('filter-event-id').value,
        // 處理狀態現在是可見的 chip（不再只由下鑽網址帶入）
        statuses: activeChips('filter-status-chips', 'status').join(','),
        // severity/overdue/source 只由下鑽帶入，畫面以可移除的條件標籤顯示（見 renderActiveConditions）——
        // source 沒有對應的表單欄位（依問題視角點列下鑽用，docs/FEEDBACK-4-PLAN.md §4）
        severity: new URLSearchParams(location.search).get('severity') ?? '',
        overdue: new URLSearchParams(location.search).get('overdue') ?? '',
        source: new URLSearchParams(location.search).get('source') ?? ''
    };
}

function buildQueryString(filters, page) {
    const params = new URLSearchParams();
    if (filters.hostIds.length) params.set('hostIds', filters.hostIds.join(','));
    if (filters.groupIds.length) params.set('groupIds', filters.groupIds.join(','));
    // riskLevels 一律寫入（即使為空）——空字串代表「使用者選了不限」，與「首次進頁」要分得出來
    params.set('riskLevels', filters.riskLevels.join(','));
    if (filters.categories.length) params.set('categories', filters.categories.join(','));
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);
    if (filters.eventId) params.set('eventId', filters.eventId);
    if (filters.severity) params.set('severity', filters.severity);
    if (filters.statuses) params.set('statuses', filters.statuses);
    if (filters.overdue) params.set('overdue', filters.overdue);
    if (filters.source) params.set('source', filters.source);
    if (currentView !== 'detail') params.set('view', currentView);
    if (sort.key) {
        params.set('sort', sort.key);
        params.set('dir', sort.dir);
    }
    // 一律明傳：後端 pageSize 預設 50，與這裡的預設 20（PAGE_SIZE_OPTIONS）不同，
    // 省略此參數會讓 API 實際回 50 筆卻誤以為是 20
    params.set('pageSize', String(pageSize));
    if (page > 1) params.set('page', String(page));
    return params.toString();
}

/** 明細視角才支援處理狀態／逾期篩選；彙總視角不帶這兩個參數 */
const ENDPOINT = {
    detail: '/api/records', host: '/api/records/by-host', date: '/api/records/by-date',
    issue: '/api/records/by-issue'
};

async function search() {
    const filters = collectFilters();
    const query = buildQueryString(filters, currentPage);

    // 同步網址：可直接複製分享，重新整理回到同一個結果與同一個視角
    history.replaceState(null, '', query ? `?${query}` : location.pathname);

    renderActiveConditions(filters);
    document.getElementById('ai-summary').replaceChildren();   // 篩選變了，舊的 AI 歸納作廢
    renderLoading(listContainer, 6);
    lastResult = await api.get(`${ENDPOINT[currentView]}?${query}`);
    render();
}

/**
 * 下鑽帶入的隱藏條件（severity/overdue/source）顯性化為可移除標籤——否則使用者只看到
 * 「為什麼只有這幾筆」卻在篩選列找不到原因。點 ✕ 移除該條件並重查。
 */
function renderActiveConditions(filters) {
    const container = document.getElementById('active-conditions');
    if (!container) return;
    container.replaceChildren();

    const tags = [];
    if (filters.severity) tags.push({ label: `嚴重度：${severityName(filters.severity)}`, param: 'severity' });
    if (filters.overdue === 'true') tags.push({ label: '只看逾期', param: 'overdue' });
    // 依問題視角下鑽帶入（docs/FEEDBACK-4-PLAN.md §4）：source 沒有對應表單欄位，只能靠這裡顯性化
    if (filters.source) tags.push({ label: `來源：${filters.source}`, param: 'source' });

    // 空白時整列連同上邊界一起隱藏，不留一條沒東西的空行
    container.classList.toggle('d-none', tags.length === 0);

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

const VIEW_UNIT = { detail: '筆', host: '台主機', date: '天', issue: '個問題' };

function render() {
    document.getElementById('result-count').textContent =
        lastResult.total > 0 ? `共 ${lastResult.total} ${VIEW_UNIT[currentView] ?? '筆'}` : '';

    if (currentView === 'host') renderHostView();
    else if (currentView === 'date') renderDateView();
    else if (currentView === 'issue') renderIssueView();
    else renderDetailView();

    renderPager();
}

// ── 明細視角 ─────────────────────────────────────────────────────────────────

function renderDetailView() {
    renderTable(listContainer, {
        columns: [
            { title: '日期', sortKey: 'date', sortDefaultDir: 'desc', render: r => dateLink(r) },
            { title: '主機', sortKey: 'host', render: r => r.hostName },
            { title: '風險', sortKey: 'risk', render: r => riskBadge(r.riskLevel) },
            { title: '狀況', render: r => headlineCell(r) },
            { title: '類型', render: r => categoryBadges(r.categories) },
            { title: '處理狀態', render: r => handlingCell(r) },
            { title: '處理人', render: r => handlerCell(r) }
        ],
        rows: lastResult.items,
        sort,
        onSort: applySort,
        rowHref: r => `/records/${r.hostId}/${r.date}${detailQuery()}`,
        empty: {
            title: '沒有符合條件的資料',
            hint: '請調整日期區間或篩選條件；若剛部署，請先確認批次分析已執行過。'
        }
    });
}

/** 表頭排序共用（三個視角的 sortKey 命名空間各自獨立，見 sort 變數註解） */
function applySort(key, dir) {
    sort = { key, dir };
    currentPage = 1;
    search();
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

    if (record.aiAnalyzed && record.headline) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary me-1';
        badge.textContent = 'AI';
        badge.title = 'AI 產出摘要';
        wrap.appendChild(badge);
    }

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

/** 處理人姓名連到其工作頁（docs/FEEDBACK-4-PLAN.md §6）；無 HandlerId（從未指派）時純文字 */
function handlerCell(record) {
    if (!record.handlerId || !record.handlerName) return record.handlerName ?? '';

    const link = document.createElement('a');
    link.href = `/handlers/${record.handlerId}`;
    link.textContent = record.handlerName;
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

// ── 依主機視角（日期合併）────────────────────────────────────────────────────

function renderHostView() {
    renderTable(listContainer, {
        columns: [
            { title: '主機', sortKey: 'host', render: h => textCell(h.hostName) },
            { title: '高風險', className: 'text-end', sortKey: 'highRisk', sortDefaultDir: 'desc', render: h => String(h.highRiskDays) },
            { title: '中風險', className: 'text-end', sortKey: 'mediumRisk', sortDefaultDir: 'desc', render: h => String(h.mediumRiskDays) },
            { title: '低風險', className: 'text-end', sortKey: 'lowRisk', sortDefaultDir: 'desc', render: h => String(h.lowRiskDays) },
            { title: '關聯訊號', className: 'text-end', sortKey: 'correlation', sortDefaultDir: 'desc', render: h => correlationCell(h.correlationDays) },
            { title: '類型', render: h => categoryBadges(h.categories) },
            { title: '最新狀況', render: h => `${h.latestDate}　${h.latestHeadline}` }
        ],
        rows: lastResult.items,
        sort,
        onSort: applySort,
        rowHref: h => h.hostId > 0 ? `/hosts/${h.hostId}` : null,
        empty: { title: '沒有符合條件的主機', hint: '請調整篩選條件或日期區間。' }
    });
}

// ── 依日期視角（主機合併）────────────────────────────────────────────────────

function renderDateView() {
    renderTable(listContainer, {
        columns: [
            { title: '日期', sortKey: 'date', sortDefaultDir: 'desc', render: d => dateViewLink(d) },
            { title: '主機數', className: 'text-end', sortKey: 'hostCount', sortDefaultDir: 'desc', render: d => String(d.hostCount) },
            { title: '高風險', className: 'text-end', sortKey: 'highRisk', sortDefaultDir: 'desc', render: d => String(d.highRiskHosts) },
            { title: '中風險', className: 'text-end', sortKey: 'mediumRisk', sortDefaultDir: 'desc', render: d => String(d.mediumRiskHosts) },
            { title: '低風險', className: 'text-end', sortKey: 'lowRisk', sortDefaultDir: 'desc', render: d => String(d.lowRiskHosts) },
            { title: '關聯訊號', className: 'text-end', sortKey: 'correlation', sortDefaultDir: 'desc', render: d => correlationCell(d.correlationHosts) },
            { title: '類型', render: d => categoryBadges(d.categories) }
        ],
        rows: lastResult.items,
        sort,
        onSort: applySort,
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
    // 依日期視角的排序欄位（如 hostCount）在明細視角不存在，不該帶過去
    params.delete('sort');
    params.delete('dir');
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

// ── 依問題視角（主機與日期都合併，docs/FEEDBACK-4-PLAN.md §4）──────────────────

function renderIssueView() {
    const canAssign = hasCapability(currentUser, 'Assign');

    const columns = [
        { title: '問題', render: i => issueGroupCell(i) },
        { title: '分類', render: i => CATEGORY_NAMES[i.category] ?? i.category },
        { title: '嚴重度', sortKey: 'severity', render: i => severityBadge(i.maxSeverity) },
        { title: '主機數', className: 'text-end', sortKey: 'hostCount', sortDefaultDir: 'desc', render: i => String(i.hostCount) },
        { title: '風險日數', className: 'text-end', sortKey: 'dayCount', sortDefaultDir: 'desc', render: i => String(i.dayCount) },
        { title: '總次數', className: 'text-end', sortKey: 'totalCount', sortDefaultDir: 'desc', render: i => formatNumber(i.totalCount) },
        { title: '最近出現', sortKey: 'lastSeen', sortDefaultDir: 'desc', render: i => i.lastSeen },
        { title: '處理概況', render: i => i.handlingSummary },
        { title: '處理人', render: i => issueHandlersCell(i) }
    ];

    if (canAssign) {
        columns.push({ title: '', className: 'text-end lf-no-print', render: i => issueAssignButton(i) });
    }

    renderTable(listContainer, {
        columns,
        rows: lastResult.items,
        sort,
        onSort: applySort,
        // 點列 → 切到明細視角並鎖定這個問題（source+eventId）
        rowHref: i => detailForIssue(i),
        empty: { title: '沒有符合條件的問題', hint: '請調整篩選條件或日期區間。' }
    });
}

/**
 * 依問題視角「處理人」欄：每個名字連到其工作頁（docs/FEEDBACK-4-PLAN.md §4/§6）。
 * 超過 3 人時收斂成「第一人 等 N 人」——第一個名字仍是連結，收斂在前端做
 * 就是為了這個（伺服器端收斂成純文字，連結就斷了）。
 */
function issueHandlersCell(group) {
    const handlers = group.handlers ?? [];
    if (handlers.length === 0) return '';

    const wrap = document.createElement('span');
    const shown = handlers.length > 3 ? handlers.slice(0, 1) : handlers;
    shown.forEach((h, index) => {
        if (index > 0) wrap.appendChild(document.createTextNode('、'));
        const link = document.createElement('a');
        link.href = `/handlers/${h.handlerId}`;
        link.textContent = formatUserName(h.displayName, h.account);
        link.addEventListener('click', event => event.stopPropagation());
        wrap.appendChild(link);
    });
    if (handlers.length > 3) {
        wrap.appendChild(document.createTextNode(` 等 ${handlers.length} 人`));
    }
    return wrap;
}

function issueGroupCell(group) {
    const wrap = document.createElement('div');
    const title = document.createElement('div');
    title.className = 'fw-semibold';
    title.textContent = `${group.source} (${group.eventId})`;
    wrap.appendChild(title);

    if (group.knownIssue) {
        const known = document.createElement('div');
        known.className = 'small text-muted';
        known.textContent = group.knownIssue;
        wrap.appendChild(known);
    }
    return wrap;
}

/** 依問題視角下鑽到明細：沿用目前篩選，鎖定這個問題（source+eventId），排序欄位不帶過去 */
function detailForIssue(group) {
    const params = new URLSearchParams(buildQueryString(collectFilters(), 1));
    params.delete('view');
    params.delete('page');
    params.delete('sort');
    params.delete('dir');
    params.set('eventId', String(group.eventId));
    params.set('source', group.source);
    return `?${params.toString()}`;
}

function issueAssignButton(group) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-outline-primary';
    btn.textContent = '指派';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openBulkAssignModal(group);
    });
    return btn;
}

/**
 * 跨主機批次指派 modal（docs/FEEDBACK-4-PLAN.md §4）：開啟時先載入受影響主機預覽
 * （已由他人案件涵蓋的主機標出既有處理人、預設不勾），選處理人／說明／預計完成日後送出。
 */
function openBulkAssignModal(group) {
    const body = document.createElement('div');
    const loadingWrap = document.createElement('div');
    loadingWrap.className = 'd-flex justify-content-center py-3';
    body.appendChild(loadingWrap);
    renderSpinner(loadingWrap, '載入受影響主機…');

    showDetailModal({ title: `批次指派：${group.source} (${group.eventId})`, body, size: 'modal-lg' });
    loadBulkAssignForm(group, body);
}

async function loadBulkAssignForm(group, body) {
    const filters = collectFilters();
    const params = new URLSearchParams({ source: group.source, eventId: String(group.eventId) });
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);

    let hosts, users;
    try {
        [hosts, users] = await Promise.all([
            api.get(`/api/handling/issue-cases/preview?${params.toString()}`, { silent: true }),
            api.get('/api/admin/users', { silent: true })
        ]);
    } catch (error) {
        body.replaceChildren();
        const msg = document.createElement('div');
        msg.className = 'text-danger';
        msg.textContent = error?.message || '載入受影響主機失敗';
        body.appendChild(msg);
        return;
    }

    renderBulkAssignForm(body, group, hosts, users);
}

function renderBulkAssignForm(body, group, hosts, users) {
    body.replaceChildren();

    if (hosts.length === 0) {
        renderEmpty(body, { title: '目前查詢範圍內沒有受影響的主機' });
        return;
    }

    const form = document.createElement('form');

    const handlerLabel = document.createElement('label');
    handlerLabel.className = 'form-label small text-muted';
    handlerLabel.textContent = '處理人';
    const handlerSelect = document.createElement('select');
    handlerSelect.className = 'form-select form-select-sm mb-3';
    for (const user of [...users].filter(u => u.active).sort((a, b) => a.displayName.localeCompare(b.displayName, 'zh-TW'))) {
        const option = document.createElement('option');
        option.value = user.userId;
        option.textContent = formatUserName(user.displayName, user.account);
        handlerSelect.appendChild(option);
    }
    form.append(handlerLabel, handlerSelect);

    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small text-muted';
    noteLabel.textContent = '說明（選填）';
    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm mb-3';
    noteInput.rows = 2;
    form.append(noteLabel, noteInput);

    const dueLabel = document.createElement('label');
    dueLabel.className = 'form-label small text-muted';
    dueLabel.textContent = '預計完成日（選填）';
    const dueInput = document.createElement('input');
    dueInput.type = 'date';
    dueInput.className = 'form-control form-control-sm mb-3';
    form.append(dueLabel, dueInput);

    const hostListLabel = document.createElement('div');
    hostListLabel.className = 'form-label small text-muted';
    hostListLabel.textContent = `受影響主機（${hosts.length} 台，已由他人案件涵蓋的預設不勾）`;
    form.appendChild(hostListLabel);

    const hostList = document.createElement('div');
    hostList.className = 'lf-bulk-assign-hosts mb-3';
    const checks = [];
    for (const host of hosts) {
        const wrap = document.createElement('div');
        wrap.className = 'form-check';

        const check = document.createElement('input');
        check.type = 'checkbox';
        check.className = 'form-check-input';
        // 已有他人案件的主機預設不勾——送出時仍會被後端保留原處理人跳過，但先不勾更誠實：
        // 使用者一眼看得出「這台不會變」，不必等送出後才從略過清單得知
        check.checked = !host.existingHandlerName;
        check.dataset.hostId = host.hostId;
        checks.push(check);

        const label = document.createElement('label');
        label.className = 'form-check-label small';
        label.textContent = host.existingHandlerName
            ? `${host.hostName}（已由 ${host.existingHandlerName} 處理中，未變更）`
            : host.hostName;

        wrap.append(check, label);
        hostList.appendChild(wrap);
    }
    form.appendChild(hostList);

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary';
    submit.textContent = '送出指派';
    form.appendChild(submit);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const hostIds = checks.filter(c => c.checked).map(c => Number(c.dataset.hostId));
        if (hostIds.length === 0) {
            toast('請至少勾選一台主機', 'warning');
            return;
        }

        const restore = withBusy(submit, '送出中');
        try {
            const result = await api.post('/api/handling/issue-cases/bulk-assign', {
                source: group.source,
                eventId: group.eventId,
                hostIds,
                handlerId: Number(handlerSelect.value),
                note: noteInput.value.trim() || null,
                dueDate: dueInput.value || null
            });

            const skippedNote = result.skipped.length > 0 ? `；${result.skipped.length} 台已由他人案件涵蓋，未變更` : '';
            toast(`已建立 ${result.created} 個案件${skippedNote}`, 'success');

            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            currentPage = 1;
            search();
        } catch (error) {
            restore();
        }
    });

    body.appendChild(form);
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
        },
        pageSize,
        onPageSize: size => {
            pageSize = size;
            savePageSize('records', size);
            currentPage = 1;
            search();
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
    updateAiSummaryButton();
    currentPage = 1;
    sort = { key: '', dir: 'desc' };   // 排序欄位命名空間隨視角而異，換視角不沿用
    search();
});

for (const button of document.querySelectorAll('[data-range]')) {
    button.addEventListener('click', () => {
        const days = Number(button.dataset.range);
        const from = new Date();
        from.setDate(from.getDate() - days + 1);

        document.getElementById('filter-from').value = toLocalDateString(from);
        document.getElementById('filter-to').value = todayLocal();
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
    if (currentView === 'issue') return ['來源', 'Event ID', '分類', '嚴重度', '主機數', '風險日數', '總次數', '最近出現', '處理概況', '處理人'];
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
    if (currentView === 'issue') {
        return [quote(item.source), item.eventId, CATEGORY_NAMES[item.category] ?? item.category, severityName(item.maxSeverity),
            item.hostCount, item.dayCount, item.totalCount, item.lastSeen,
            quote(item.handlingSummary), quote((item.handlers ?? []).map(h => h.displayName).join('、'))];
    }
    return [item.date, quote(item.hostName), item.riskLevel, quote(item.headline),
        cats(item.categories), quote(item.handlingStatusText), quote(item.handlerName ?? '')];
}

function quote(value) {
    const text = String(value ?? '');
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

// 本地日期（S12）：toISOString() 取的是 UTC 日期，台灣（UTC+8）凌晨 0~8 點呼叫會少算一天
function today() {
    return todayLocal();
}

function defaultFrom() {
    const date = new Date();
    date.setDate(date.getDate() - 6);
    return toLocalDateString(date);
}

init();
