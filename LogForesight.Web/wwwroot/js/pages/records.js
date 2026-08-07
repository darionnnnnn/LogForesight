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
    loadPageSize, savePageSize, PAGE_SIZE_OPTIONS, showDetailModal, button, searchableUserSelect, guardLoad
} from '../core/ui.js';
import { riskBadge, handlingBadge, statusBadge, severityBadge, CATEGORY_NAMES, severityName, formatNumber, formatUserName, toLocalDateString, todayLocal } from '../core/format.js';
import { renderAiText } from '../core/markdown-lite.js';
import { openIssueStatusReplyModal } from './issue-status-reply.js';

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

// 依問題視角批次指派（docs/archive/FEEDBACK-4-PLAN.md §4）：只有 Assign 能力才顯示「指派」鈕
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
 * 日風險等級顯示（docs/archive/FEEDBACK-3-PLAN.md #8）：隱藏被全站「日風險等級顯示」設定藏起來的
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
 * AI 歸納（docs/archive/HISTORY.md §6 W1-2）：使用者主動點才呼叫（查詢頁高頻，
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
        const result = await api.get(`/api/hosts?ids=${encodeURIComponent(csv)}`, { silent: true });
        for (const host of result.items) selectedHosts.set(String(host.hostId), host.hostName);
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
        let result;
        try {
            result = await api.get(`/api/hosts?query=${encodeURIComponent(query)}`, { silent: true });
        } catch {
            return;
        }

        const candidates = result.items.filter(h => !selectedHosts.has(String(h.hostId)));
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

        // 截斷要說出來（體檢 X4）：2000 台環境輸入常見前綴可能有數百台符合，
        // 只出現 20 筆而不說明，使用者會合理地認為「就只有這 20 台」
        if (result.truncated) {
            const note = document.createElement('div');
            note.className = 'dropdown-item-text small text-muted border-top';
            note.textContent = `顯示前 ${result.items.length} 筆，共 ${result.total} 筆符合，請再輸入以縮小範圍`;
            suggestions.appendChild(note);
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
    document.getElementById('filter-unassigned-chip').classList.toggle('active', params.get('unassigned') === 'true');
    document.getElementById('filter-from').value = params.get('from') ?? defaultFrom();
    document.getElementById('filter-to').value = params.get('to') ?? today();
    document.getElementById('filter-event-id').value = params.get('eventId') ?? '';
    document.getElementById('filter-source').value = params.get('source') ?? '';

    // 預設視角（§10）：URL 完全沒有查詢參數（從側欄直接進頁）→ 依問題；帶任何參數（下鑽連結
    // 帶 statuses/severity 等明細專屬條件）→ 維持明細，下鑽連結零改動、數字對得上
    const viewParam = params.get('view');
    const hasAnyParam = [...params.keys()].length > 0;
    currentView = ['detail', 'host', 'date', 'issue'].includes(viewParam)
        ? viewParam
        : (hasAnyParam ? 'detail' : 'issue');
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

    // 明細與依問題視角支援處理狀態篩選（§10：依問題篩的是「處理概況」三態）；依主機／依日期不支援
    const supportsStatus = view === 'detail' || view === 'issue';
    for (const btn of document.querySelectorAll('#filter-status-chips button')) {
        btn.disabled = !supportsStatus;
        btn.title = supportsStatus
            ? (view === 'issue' ? '依問題視角篩的是各問題的處理概況（未處理／處理中／已處理）' : '')
            : '依主機／依日期視角不支援處理狀態篩選';
    }

    // 未指派 chip 僅「依問題」視角顯示（§10）：問題角度的分派入口
    document.getElementById('filter-unassigned-group').classList.toggle('d-none', view !== 'issue');
    if (view !== 'issue') document.getElementById('filter-unassigned-chip').classList.remove('active');
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
        // 來源現在是可見的表單欄位（§4：跨主機同簽章查詢併入問題查詢），與 eventId 同款；
        // severity/overdue 仍只由下鑽帶入，畫面以可移除的條件標籤顯示（見 renderActiveConditions）
        source: document.getElementById('filter-source').value.trim(),
        // 未指派（§10）：依問題視角看 chip；其餘視角只由 URL 帶入（§5 報表未指派下鑽）——
        // chip 在非 issue 視角隱藏，若仍讀 chip 之外又讀 URL，會變成看不見卻仍生效的篩選，
        // 因此非 issue 視角一律以 URL 為準，並由 renderActiveConditions 顯性化成可移除標籤
        unassigned: currentView === 'issue'
            ? document.getElementById('filter-unassigned-chip').classList.contains('active')
            : new URLSearchParams(location.search).get('unassigned') === 'true',
        severity: new URLSearchParams(location.search).get('severity') ?? '',
        overdue: new URLSearchParams(location.search).get('overdue') ?? ''
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
    if (filters.unassigned) params.set('unassigned', 'true');
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
    // 未指派在非 issue 視角沒有 chip 可解除（§5 報表下鑽帶入）——顯性化成可移除標籤
    if (filters.unassigned && currentView !== 'issue') tags.push({ label: '僅未指派', param: 'unassigned' });
    // 來源（§4 起）已是可見表單欄位，不再作為可移除條件標籤——清空欄位即可

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
    } else if (record.aiPending) {
        // 統計段已寫入、AI 段還在排隊或執行中（docs/FEEDBACK-12-PLAN.md §3.5）——
        // 中性色，跟灰字的「統計模式」區分開，不能看起來像失敗
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--info me-1';
        badge.textContent = 'AI 分析中';
        badge.title = '統計結果已完成，AI 白話摘要正在背景處理';
        wrap.appendChild(badge);
    }

    const text = document.createElement('span');
    text.textContent = record.headline || '（無 AI 摘要）';
    if (!record.aiAnalyzed && !record.aiPending) text.className = 'text-muted';
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

/** 處理人姓名連到其工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）；無 HandlerId（從未指派）時純文字。
 *  §9：顯示「顯示名稱(帳號)」；來自案件 fallback 時後綴「（案件）」（原後端組字，改由前端組） */
function handlerCell(record) {
    if (!record.handlerId || !record.handlerName) return record.handlerName ?? '';

    const suffix = record.handlerFromCase ? '（案件）' : '';
    const link = document.createElement('a');
    link.href = `/handlers/${record.handlerId}`;
    link.textContent = formatUserName(record.handlerName, record.handlerAccount) + suffix;
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

// ── 依問題視角（主機與日期都合併，docs/archive/FEEDBACK-4-PLAN.md §4）──────────────────

function renderIssueView() {
    // 欄位順序＝「這個問題有多嚴重／影響多廣／什麼形狀／誰在處理」（規劃 §10.2 的四個維度）。
    // 「涵蓋範圍」與「出現密度」是需求「期間跨度」的落地：只有「最近出現」看不出
    // 這是天天都有的背景值、還是近三天才冒出來的新問題。
    const columns = [
        { title: '問題', render: i => issueGroupCell(i) },
        { title: '分類', render: i => CATEGORY_NAMES[i.category] ?? i.category },
        { title: '嚴重度', sortKey: 'severity', render: i => issueSeverityCell(i) },
        { title: '主機數', className: 'text-end', sortKey: 'hostCount', sortDefaultDir: 'desc', render: i => String(i.hostCount) },
        { title: '涵蓋範圍', className: 'text-nowrap', render: i => issueSpanCell(i) },
        { title: '出現密度', className: 'text-end text-nowrap', render: i => issueDensityCell(i) },
        { title: '總次數', className: 'text-end', sortKey: 'totalCount', sortDefaultDir: 'desc', render: i => formatNumber(i.totalCount) },
        { title: '最近出現', sortKey: 'lastSeen', sortDefaultDir: 'desc', render: i => issueLastSeenCell(i) },
        { title: '處理概況', render: i => i.handlingSummary },
        { title: '處理人', render: i => issueHandlersCell(i) }
    ];

    // 動作欄：admin 的「指派」與處理人自己的「回覆處理狀態」（§11）共用同一欄——
    // 兩者都是「對這個問題做點什麼」，分兩欄會讓表格在沒有權限的角色眼中出現空欄
    columns.push({
        title: '',
        className: 'text-end lf-no-print',
        render: i => issueActionsCell(i)
    });

    renderTable(listContainer, {
        columns,
        rows: lastResult.items,
        sort,
        onSort: applySort,
        // 動作欄固定在列末（體檢 W1）：1024px 下這一欄整段在畫面外，
        // 而它正是 admin 在這個視角要用的東西（指派／統一標記／回覆狀態）
        stickyLastColumn: true,
        // §10：點列就地展開該問題的受影響主機×日期，每列直連風險日詳情去處理——
        // 把「看到問題→去處理」從「下鑽明細→點日期→找問題」縮短（取代原本整列導向明細視角）
        onRowExpand: (group, cell) => renderIssueOccurrences(cell, group),
        empty: { title: '沒有符合條件的問題', hint: '請調整篩選條件或日期區間。' }
    });
}

/**
 * 依問題視角的列展開（§10）：列出該問題目前查詢範圍內的受影響主機×日期，每列直連該主機
 * 該日的風險日詳情。重用明細端點（/api/records，全角色、可見範圍已過濾），不另建 API。
 */
async function renderIssueOccurrences(cell, group) {
    cell.replaceChildren();
    const wrap = document.createElement('div');
    wrap.className = 'p-2';
    renderLoading(wrap, 3);
    cell.appendChild(wrap);

    const filters = collectFilters();
    const params = new URLSearchParams();
    params.set('source', group.source);
    params.set('eventId', String(group.eventId));
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);
    params.set('riskLevels', '高,中,低');   // 問題的出現橫跨各風險層級，不套當前風險 chip
    params.set('pageSize', '100');

    let result;
    try {
        result = await api.get(`/api/records?${params}`, { silent: true });
    } catch {
        renderEmpty(wrap, { title: '載入受影響主機失敗' });
        return;
    }

    if (!result.items.length) {
        renderEmpty(wrap, { title: '此範圍內沒有可展開的主機日' });
        return;
    }

    const inner = document.createElement('div');
    renderTable(inner, {
        columns: [
            { title: '主機', render: r => r.hostName },
            { title: '日期', render: r => r.date },
            { title: '風險', render: r => riskBadge(r.riskLevel) },
            { title: '處理狀態', render: r => handlingCell(r) },
            { title: '處理人', render: r => handlerCell(r) },
            { title: '', className: 'text-end', render: r => goHandleLink(r) }
        ],
        rows: result.items,
        rowHref: r => `/records/${r.hostId}/${r.date}`,
        empty: { title: '沒有資料' }
    });

    // 保留原本「切到明細視角看全部」的出口（不再是整列導向，改成明確連結）
    const foot = document.createElement('div');
    foot.className = 'small mt-2';
    const allLink = document.createElement('a');
    allLink.href = detailForIssue(group);
    allLink.textContent = '在明細視角檢視這個問題的全部風險日 →';
    foot.appendChild(allLink);

    wrap.replaceChildren(inner, foot);
}

/** 展開列內每列的「去處理」連結，連到該主機該日風險日詳情 */
function goHandleLink(record) {
    const link = document.createElement('a');
    link.href = `/records/${record.hostId}/${record.date}`;
    link.className = 'btn btn-sm btn-outline-primary';
    link.textContent = '去處理';
    return link;
}

/**
 * 依問題視角「處理人」欄：每個名字連到其工作頁（docs/archive/FEEDBACK-4-PLAN.md §4/§6）。
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

/**
 * 嚴重度欄：徽章＋「重大」旗標（規劃 §10.2 維度 1 的既有缺口——這個旗標過去只在
 * 風險日詳情看得到，而它正是「disk 153 該排第一」的理由之一）。
 */
function issueSeverityCell(group) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1';
    wrap.appendChild(severityBadge(group.maxSeverity));

    if (group.elevatesDayRisk) {
        const flag = document.createElement('span');
        flag.className = 'lf-badge lf-badge--danger';
        flag.textContent = '重大';
        flag.title = '此問題曾命中「命中即列為高風險日」的規則旗標';
        wrap.appendChild(flag);
    }
    return wrap;
}

/** 涵蓋範圍：首見 ~ 最近出現（需求的「期間跨度」）。同一天時只顯示一次，不寫成「X ~ X」 */
function issueSpanCell(group) {
    const span = document.createElement('span');
    span.className = 'lf-mono small';
    span.textContent = group.firstSeen === group.lastSeen
        ? group.firstSeen
        : `${group.firstSeen} ~ ${group.lastSeen}`;
    return span;
}

/**
 * 出現密度：出現天數 ÷ 期間天數（§10.3）。
 * 「2/90」與「90/90」是完全不同的問題——前者是零星爆發、後者是天天都有的背景值，
 * 而數量排序看不出這件事。附一條密度條讓比例一眼可辨（文字仍是主要資訊，圖只是輔助）。
 */
function issueDensityCell(group) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1 justify-content-end';

    const text = document.createElement('span');
    text.className = 'lf-mono small';
    text.textContent = `${group.activeDays}/${group.periodDays}`;
    wrap.appendChild(text);

    const ratio = group.periodDays > 0 ? group.activeDays / group.periodDays : 0;
    const bar = document.createElement('span');
    bar.className = 'lf-density';
    bar.title = `期間 ${group.periodDays} 天內出現 ${group.activeDays} 天（${Math.round(ratio * 100)}%）`;
    const fill = document.createElement('span');
    fill.className = 'lf-density__fill';
    fill.style.width = `${Math.max(4, Math.round(ratio * 100))}%`;
    bar.appendChild(fill);
    wrap.appendChild(bar);

    return wrap;
}

/**
 * 最近出現：日期＋「還在不在發生」（§10.3）。
 * 目前的排行把「90 天前爆發過、之後再也沒出現」與「今天正在發生」視為同等——
 * 前者其實該自動退場，而使用者只看日期得自己心算。
 */
function issueLastSeenCell(group) {
    const wrap = document.createElement('div');

    const date = document.createElement('div');
    date.className = 'lf-mono small';
    date.textContent = group.lastSeen;
    wrap.appendChild(date);

    const hint = document.createElement('div');
    hint.className = 'small';
    if (group.daysSinceLastSeen === 0) {
        hint.className += ' text-danger fw-semibold';
        hint.textContent = '今天仍在發生';
    } else if (group.daysSinceLastSeen <= 3) {
        hint.className += ' text-danger';
        hint.textContent = `${group.daysSinceLastSeen} 天前`;
    } else {
        hint.className += ' text-muted';
        hint.textContent = `已 ${group.daysSinceLastSeen} 天未再出現`;
    }
    wrap.appendChild(hint);

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

/**
 * 依問題視角的動作欄：admin 可指派（§4）與統一標記（§6，回饋第十一輪）；
 * 處理人清單含自己時可一次回覆處理狀態（§11）。都沒有時回空字串——不留一顆按不下去的按鈕。
 */
function issueActionsCell(group) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-2 justify-content-end';

    // 「是我的案件」還不夠，還要「動得了」（體檢 H2）：被指派但沒有 Handle 能力的人
    // （manager／dev／未分群組且非負責人）過去看得到這顆按鈕，按下去必定 403。
    // 後端才是真正的防線，但前端不該擺一顆一定失敗的按鈕。
    const isMyIssue = (group.handlers ?? []).some(h => h.handlerId === currentUser?.userId);
    if (isMyIssue && hasCapability(currentUser, 'Handle')) wrap.appendChild(issueReplyButton(group));
    // 統一標記要 Assign＋Handle 兩者（後端同一條規則）——實務上就是 admin
    if (hasCapability(currentUser, 'Assign') && hasCapability(currentUser, 'Handle')) {
        wrap.appendChild(issueBulkCloseButton(group));
    }
    if (hasCapability(currentUser, 'Assign')) wrap.appendChild(issueAssignButton(group));

    return wrap.children.length > 0 ? wrap : '';
}

/** 統一標記（§6）：把這個問題在還沒有人接手的主機上一次標成結論 */
function issueBulkCloseButton(group) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-outline-secondary';
    btn.textContent = '統一標記';
    btn.title = '把這個問題在尚未有人接手的主機上一次標成結論';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openBulkCloseModal(group);
    });
    return btn;
}

/** 回覆處理狀態（§11）：modal 本身是共用元件（處理人工作頁的依問題視角也用同一個） */
function issueReplyButton(group) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-outline-secondary';
    btn.textContent = '回覆處理狀態';
    btn.title = '一次回覆這個問題在所有指派給您的主機上的處理狀態';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openIssueStatusReplyModal(group, search);
    });
    return btn;
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
 * 統一標記 modal（docs/archive/FEEDBACK-11-PLAN.md §6）：admin 把這個問題在**尚未有人接手**的主機上
 * 一次標成結論（結案四態），原因必填。
 *
 * 三件事一定要在按下去之前看得到（定案 6-2／6-3）：套用的**期間**、哪些主機會被**略過**
 * 及原因、哪些天的「處理中／觀察中」標記會被**覆蓋**。預覽端點與落盤走同一份計畫規則。
 */
function openBulkCloseModal(group) {
    const body = document.createElement('div');
    const loadingWrap = document.createElement('div');
    loadingWrap.className = 'd-flex justify-content-center py-3';
    body.appendChild(loadingWrap);
    renderSpinner(loadingWrap, '載入受影響主機…');

    showDetailModal({ title: `統一標記：${group.source} (${group.eventId})`, body, size: 'modal-lg' });
    loadBulkCloseForm(group, body);
}

async function loadBulkCloseForm(group, body) {
    const filters = collectFilters();
    const params = new URLSearchParams({ source: group.source, eventId: String(group.eventId) });
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);

    let preview;
    try {
        preview = await api.get(`/api/handling/issue-cases/close-preview?${params.toString()}`, { silent: true });
    } catch (error) {
        body.replaceChildren();
        const msg = document.createElement('div');
        msg.className = 'text-danger';
        msg.textContent = error?.message || '載入受影響主機失敗';
        body.appendChild(msg);
        return;
    }

    renderBulkCloseForm(body, group, preview);
}

function renderBulkCloseForm(body, group, preview) {
    body.replaceChildren();

    const targets = preview.hosts.filter(h => !h.skipReason && h.dayCount > 0);
    const skipped = preview.hosts.filter(h => h.skipReason);
    const form = document.createElement('form');

    // 期間：定案 6-2 要求「正在處理的使用者」看得到本次的邊界，不能只寫在說明裡
    const rangeNote = document.createElement('div');
    rangeNote.className = 'alert alert-warning py-2 mb-3';
    rangeNote.textContent = preview.from || preview.to
        ? `本次僅處理 ${preview.from ?? '（不限）'} ～ ${preview.to ?? '（不限）'} 期間內的紀錄。`
        : '本次處理目前查詢範圍內的全部紀錄。';
    form.appendChild(rangeNote);

    const scopeNote = document.createElement('div');
    scopeNote.className = 'lf-hint mb-3';
    scopeNote.textContent = '只套用到「尚未有人接手」的主機——已建立案件的主機一律略過（要換結論請由處理人回覆或先改派）。' +
        '本操作只對上列期間內的既有紀錄下結論；規則未調整前，之後的新日子仍會產生同類問題' +
        '（標「已知雜訊」除外——會為這些主機寫入雜訊記憶，之後同問題自動標示）。';
    form.appendChild(scopeNote);

    // 摘要一律用**總數**而不是本頁列出的筆數（體檢 M10）：逐台清單有 200 筆上限，
    // 拿截斷後的長度當摘要會讓「我到底影響了多少」少報，而那正是這個操作最需要準確的數字
    const summary = document.createElement('div');
    summary.className = 'mb-3 small fw-semibold';
    const overwriteDays = targets.reduce((sum, h) => sum + h.overwriteDayCount, 0);
    const affectedHosts = preview.totalHostCount - preview.skippedHostCount;
    summary.textContent = `將標記 ${formatNumber(affectedHosts)} 台主機、共 ${formatNumber(preview.totalDayCount)} 天` +
        (overwriteDays > 0 ? `（列出的主機中有 ${overwriteDays} 天原本標著處理中／觀察中，會被覆蓋）` : '') +
        (preview.skippedHostCount > 0 ? `；${formatNumber(preview.skippedHostCount)} 台略過` : '');
    form.appendChild(summary);

    if (preview.truncated) {
        const truncNote = document.createElement('div');
        truncNote.className = 'lf-hint mb-3';
        truncNote.textContent = `下方只列出前 ${preview.hosts.length} 台（共 ${formatNumber(preview.totalHostCount)} 台）——`
            + '清單僅供抽查，實際套用範圍以上方數字為準。';
        form.appendChild(truncNote);
    }

    // 逐主機明細：略過的也列出來，「沒被處理到」與「不存在」要分得清楚
    const table = document.createElement('div');
    table.className = 'mb-3';
    renderTable(table, {
        columns: [
            { title: '主機', render: h => h.hostName },
            { title: '將標記天數', className: 'text-end', render: h => (h.skipReason ? '—' : String(h.dayCount)) },
            { title: '覆蓋處理中', className: 'text-end', render: h => (h.overwriteDayCount > 0 ? `${h.overwriteDayCount} 天` : '') },
            { title: '已有結論', className: 'text-end', render: h => (h.alreadyClosedDayCount > 0 ? `${h.alreadyClosedDayCount} 天` : '') },
            { title: '狀態', render: h => bulkCloseStatusCell(h) }
        ],
        rows: preview.hosts,
        empty: { title: '目前查詢範圍內沒有受影響的主機' }
    });
    form.appendChild(table);

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label small text-muted';
    statusLabel.textContent = '結論';
    const statusSelect = document.createElement('select');
    statusSelect.className = 'form-select form-select-sm mb-3';
    for (const option of [
        { value: 'resolved', label: '已處理' },
        { value: 'wont_fix', label: '不處理' },
        { value: 'false_positive', label: '誤報' },
        { value: 'known_noise', label: '已知雜訊' }
    ]) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        statusSelect.appendChild(el);
    }
    form.append(statusLabel, statusSelect);

    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small text-muted';
    noteLabel.textContent = '原因（必填）';
    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm mb-3';
    noteInput.rows = 3;
    noteInput.placeholder = '例：確認為週期性維護作業產生，非異常。';
    form.append(noteLabel, noteInput);

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary';
    submit.textContent = '套用';
    // 上限用總數判斷，不是本頁列出的筆數——列表被截斷不代表可以送出更多
    const overLimit = preview.totalDayCount > BULK_CLOSE_DAY_LIMIT;
    submit.disabled = targets.length === 0 || overLimit;
    form.appendChild(submit);

    if (overLimit) {
        const limitNote = document.createElement('div');
        limitNote.className = 'alert alert-danger py-2 mt-3 mb-0 small';
        limitNote.textContent = `本次將寫入 ${formatNumber(preview.totalDayCount)} 筆，超過單次上限 `
            + `${formatNumber(BULK_CLOSE_DAY_LIMIT)} 筆。請縮小日期區間或改用更精確的問題條件，分次執行。`;
        form.appendChild(limitNote);
    }

    form.addEventListener('submit', async event => {
        event.preventDefault();

        if (!noteInput.value.trim()) {
            toast('請填寫原因', 'warning');
            return;
        }

        const filters = collectFilters();
        const restore = withBusy(submit, '套用中');
        try {
            const result = await api.post('/api/handling/issue-cases/bulk-close', {
                source: group.source,
                eventId: group.eventId,
                from: filters.from || null,
                to: filters.to || null,
                status: statusSelect.value,
                note: noteInput.value.trim()
            });

            toast(`已標記 ${formatNumber(result.updatedHostCount)} 台主機、共 ${formatNumber(result.updatedDayCount)} 天` +
                  (result.skippedHostCount > 0 ? `；${formatNumber(result.skippedHostCount)} 台略過` : ''), 'success', 6000);

            if (statusSelect.value === 'false_positive') {
                toast('若要根治誤報，請至「規則維護」調整對應規則的門檻或條件。', 'info', 8000);
            }

            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            // 影響範圍的追溯出口（體檢 X6）：批次寫入沒有復原機制，至少要查得到影響了哪些。
            // 導向依問題視角並鎖定同一個問題與期間，剛寫入的結論就在那裡逐台可查
            showBulkCloseTraceLink(result);
            search();
        } catch {
            restore();
        }
    });

    body.appendChild(form);
}

/**
 * 統一標記單次可寫入的主機日上限——與後端 IssueHandlingCommandService.MaxBulkCloseDayWrites
 * 同一個數字。**前端擋是為了在按下去之前就講清楚**（後端仍會擋，那是防繞過的實際防線）：
 * 讓使用者填完原因、按下套用之後才收到「超過上限」，等於白填一次。
 */
const BULK_CLOSE_DAY_LIMIT = 5000;

/**
 * 影響範圍的追溯連結（體檢 X6）：批次寫入沒有「上一步／復原」，成本高不做；
 * 但「影響了哪些」必須查得回去。這裡給一個帶問題與期間的依問題視角連結，
 * 點進去展開就是剛才被寫入的那些主機日。
 */
function showBulkCloseTraceLink(result) {
    const params = new URLSearchParams({ view: 'issue', source: result.source, eventId: String(result.eventId) });
    if (result.from) params.set('from', result.from);
    if (result.to) params.set('to', result.to);

    const link = document.createElement('a');
    link.href = `/records?${params.toString()}`;
    link.textContent = '檢視這次影響的清單';
    toast(link, 'info', 10000);
}

function bulkCloseStatusCell(host) {
    const span = document.createElement('span');
    if (host.skipReason) {
        span.className = 'text-muted small';
        span.textContent = `略過：${host.skipReason}`;
    } else {
        span.className = 'text-success small';
        span.textContent = '將標記';
    }
    return span;
}

/**
 * 跨主機批次指派 modal（docs/archive/FEEDBACK-4-PLAN.md §4）：開啟時先載入受影響主機預覽
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

    let preview, users, groups;
    try {
        [preview, users, groups] = await Promise.all([
            api.get(`/api/handling/issue-cases/preview?${params.toString()}`, { silent: true }),
            api.get('/api/admin/users', { silent: true }),
            // 群組指派（docs/archive/FEEDBACK-10-PLAN.md §12）：一次把整個問題的主機分攤給一個群組的成員
            api.get('/api/admin/groups', { silent: true })
        ]);
    } catch (error) {
        body.replaceChildren();
        const msg = document.createElement('div');
        msg.className = 'text-danger';
        msg.textContent = error?.message || '載入受影響主機失敗';
        body.appendChild(msg);
        return;
    }

    renderBulkAssignForm(body, group, preview, users, groups);
}

/**
 * 批次指派 modal（docs/archive/FEEDBACK-4-PLAN.md §4；docs/archive/FEEDBACK-10-PLAN.md §9／§12 擴充）。
 *
 * 三件事在同一張預覽表上完成，因為它們回答的是同一個問題「每一台主機由誰處理」：
 *   - 指派：沒有既有處理人的主機 → 建案
 *   - 改派（§9）：已有他人進行中案件的主機，勾「改派」才換人，否則維持原處理人
 *   - 分攤（§12）：指派對象選「使用者群組」時，把主機分給群組成員（輪流或依負載）
 */
function renderBulkAssignForm(body, group, preview, users, groups) {
    body.replaceChildren();

    const hosts = preview.hosts;
    if (hosts.length === 0) {
        renderEmpty(body, { title: '目前查詢範圍內沒有受影響的主機' });
        return;
    }

    const form = document.createElement('form');

    // 逐台清單有 200 筆上限（體檢 M10）：常見問題在 6000 台環境會把 modal 塞爆。
    // 截斷必須說出來，而且要講清楚「本次只會指派列出的這些」——
    // 靜默截斷會讓使用者以為整批都指派了
    if (preview.truncated) {
        const truncNote = document.createElement('div');
        truncNote.className = 'alert alert-warning py-2 mb-3';
        truncNote.textContent = `這個問題共影響 ${formatNumber(preview.totalHostCount)} 台主機，`
            + `本次只列出並指派前 ${hosts.length} 台。其餘主機請調整篩選條件後分批指派。`;
        form.appendChild(truncNote);
    }

    // §6：講清楚「一次性 vs 持續」——指派會建立案件，這些主機之後同問題的新風險日會自動掛進
    // 案件並同步狀態，直到結案（不是只處理當下這些日子）
    const persistNote = document.createElement('div');
    persistNote.className = 'lf-hint mb-3';
    persistNote.textContent = '指派會為勾選的主機建立案件；這些主機之後同一問題的新風險日會自動掛進案件並同步處理狀態，直到結案。';
    form.appendChild(persistNote);

    // ── 指派對象：單一使用者／使用者群組（§12）──────────────────────────────
    const modeWrap = document.createElement('div');
    modeWrap.className = 'mb-3';
    const modeLabel = document.createElement('div');
    modeLabel.className = 'form-label small text-muted';
    modeLabel.textContent = '指派給';
    const modeGroup = document.createElement('div');
    modeGroup.className = 'btn-group btn-group-sm mb-2';
    const modeUserBtn = button('單一使用者', { variant: 'outline-secondary', onClick: () => setMode('user') });
    const modeGroupBtn = button('使用者群組（平均分攤）', { variant: 'outline-secondary', onClick: () => setMode('group') });
    modeGroup.append(modeUserBtn, modeGroupBtn);
    modeWrap.append(modeLabel, modeGroup);
    form.appendChild(modeWrap);

    // 單一使用者：可搜尋處理人選單（帳號/顯示名稱關鍵字過濾），與處理面板共用同一元件
    const { element: handlerSelectWrap, select: handlerSelect } = searchableUserSelect(users);
    handlerSelectWrap.classList.add('mb-3');
    form.appendChild(handlerSelectWrap);

    // 群組模式：選群組＋分攤方式
    const groupWrap = document.createElement('div');
    groupWrap.className = 'mb-3 d-none';

    const groupSelect = document.createElement('select');
    groupSelect.className = 'form-select form-select-sm mb-2';
    for (const g of groups) {
        const option = document.createElement('option');
        option.value = String(g.groupId);
        option.textContent = `${g.groupName}（${g.role}）`;
        groupSelect.appendChild(option);
    }

    const splitWrap = document.createElement('div');
    splitWrap.className = 'd-flex align-items-center gap-2 flex-wrap';
    const splitLabel = document.createElement('span');
    splitLabel.className = 'small text-muted';
    splitLabel.textContent = '分攤方式';
    const splitSelect = document.createElement('select');
    splitSelect.className = 'form-select form-select-sm w-auto';
    for (const option of [
        { value: 'round-robin', label: '平均輪流（每人台數盡量相同）' },
        { value: 'load', label: '依現有負載（手上案件少的人多分）' }
    ]) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        splitSelect.appendChild(el);
    }
    const memberHint = document.createElement('span');
    memberHint.className = 'small text-muted';
    splitWrap.append(splitLabel, splitSelect, memberHint);

    groupWrap.append(groupSelect, splitWrap);
    form.appendChild(groupWrap);

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

    // §6：台數多時的操作——主機名關鍵字過濾＋全選/全不選（只作用於目前過濾可見的項目）
    const hostTools = document.createElement('div');
    hostTools.className = 'd-flex gap-2 align-items-center mb-2';
    const hostFilter = document.createElement('input');
    hostFilter.type = 'text';
    hostFilter.className = 'form-control form-control-sm';
    hostFilter.placeholder = '過濾主機名稱…';
    hostFilter.autocomplete = 'off';
    const selectAllBtn = button('全選', { onClick: () => setAllVisible(true) });
    const selectNoneBtn = button('全不選', { onClick: () => setAllVisible(false) });
    hostTools.append(hostFilter, selectAllBtn, selectNoneBtn);
    form.appendChild(hostTools);

    const hostList = document.createElement('div');
    hostList.className = 'lf-bulk-assign-hosts mb-3';
    const rows = [];
    for (const host of hosts) {
        const wrap = document.createElement('div');
        wrap.className = 'd-flex align-items-center gap-2 py-1';

        const check = document.createElement('input');
        check.type = 'checkbox';
        check.className = 'form-check-input mt-0';
        // 已有他人案件的主機預設不勾——先不勾更誠實：使用者一眼看得出「這台不會變」，
        // 要換人就勾旁邊的「改派」（§9）
        check.checked = !host.existingHandlerName;
        check.dataset.hostId = host.hostId;

        const name = document.createElement('span');
        name.className = 'small flex-grow-1';
        name.textContent = host.hostName;

        wrap.append(check, name);

        // 已有處理人的主機多一個「改派」勾（§9）：不勾＝維持原處理人（既有語意）
        let reassignCheck = null;
        if (host.existingHandlerName) {
            const existing = document.createElement('span');
            existing.className = 'small text-muted';
            existing.textContent = `目前：${formatUserName(host.existingHandlerName, host.existingHandlerAccount)}`;

            const reassignLabel = document.createElement('label');
            reassignLabel.className = 'form-check-label small d-flex align-items-center gap-1 text-nowrap';
            reassignCheck = document.createElement('input');
            reassignCheck.type = 'checkbox';
            reassignCheck.className = 'form-check-input mt-0';
            reassignCheck.dataset.hostId = host.hostId;
            reassignLabel.append(reassignCheck, document.createTextNode('改派'));

            // 勾了改派就必須連帶勾選這台主機，否則送出時它根本不在名單裡、改派不會發生
            reassignCheck.addEventListener('change', () => {
                if (reassignCheck.checked) check.checked = true;
                renderPlanHint();
            });

            wrap.append(existing, reassignLabel);
        }

        // 群組分攤模式下顯示這台會分給誰（可個別改人）
        const assigneeSelect = document.createElement('select');
        assigneeSelect.className = 'form-select form-select-sm w-auto d-none';
        for (const user of users.filter(u => u.active)) {
            const option = document.createElement('option');
            option.value = String(user.userId);
            option.textContent = formatUserName(user.displayName, user.account);
            assigneeSelect.appendChild(option);
        }
        wrap.appendChild(assigneeSelect);

        hostList.appendChild(wrap);
        rows.push({ wrap, check, reassignCheck, assigneeSelect, host, name: host.hostName.toLowerCase() });
    }
    form.appendChild(hostList);

    const planHint = document.createElement('div');
    planHint.className = 'small text-muted mb-3';
    form.appendChild(planHint);

    function applyHostFilter() {
        const f = hostFilter.value.trim().toLowerCase();
        for (const row of rows) row.wrap.classList.toggle('d-none', !!f && !row.name.includes(f));
    }
    function setAllVisible(checked) {
        for (const row of rows) {
            if (!row.wrap.classList.contains('d-none')) row.check.checked = checked;
        }
        onSelectionChanged();
    }
    hostFilter.addEventListener('input', applyHostFilter);
    for (const row of rows) row.check.addEventListener('change', onSelectionChanged);

    /**
     * 勾選變動後：群組模式要**重算分攤**而不只是重畫提示——新勾進來的主機若沿用下拉的
     * 預設值（清單第一個人），會讓實際落盤與「平均分攤」的承諾不符，而且畫面上看不出來。
     */
    function onSelectionChanged() {
        if (mode === 'group') applySplit();
        else renderPlanHint();
    }

    // ── 指派對象模式切換與分攤計算（§12）────────────────────────────────────
    let mode = 'user';
    let candidates = [];

    function setMode(next) {
        mode = next;
        modeUserBtn.classList.toggle('active', mode === 'user');
        modeGroupBtn.classList.toggle('active', mode === 'group');
        handlerSelectWrap.classList.toggle('d-none', mode !== 'user');
        groupWrap.classList.toggle('d-none', mode !== 'group');
        for (const row of rows) row.assigneeSelect.classList.toggle('d-none', mode !== 'group');
        if (mode === 'group') loadCandidates();
        else renderPlanHint();
    }

    async function loadCandidates() {
        memberHint.textContent = '載入成員…';
        try {
            candidates = await api.get(`/api/handling/issue-cases/handler-candidates?groupId=${groupSelect.value}`, { silent: true });
        } catch {
            candidates = [];
        }
        memberHint.textContent = candidates.length > 0
            ? `${candidates.length} 位啟用中的成員`
            : '這個群組沒有啟用中的成員';
        applySplit();
    }

    /**
     * 分攤計算（§12），兩種模式都是**確定性**的——同一組輸入永遠得到同一個結果，
     * 預覽看到的就是實際會落盤的分配：
     *   round-robin：主機依名稱排序、成員依帳號排序，逐台輪流
     *   load：每次分給「既有案件數＋本次已分到的台數」最少的人，同分時帳號序決勝
     */
    function applySplit() {
        if (mode !== 'group' || candidates.length === 0) {
            renderPlanHint();
            return;
        }

        const selected = rows.filter(r => r.check.checked)
            .sort((a, b) => a.host.hostName.localeCompare(b.host.hostName, 'en'));
        const load = new Map(candidates.map(c => [c.userId, c.openCaseCount]));

        selected.forEach((row, index) => {
            let target;
            if (splitSelect.value === 'round-robin') {
                target = candidates[index % candidates.length];
            } else {
                target = candidates.reduce((best, c) => (load.get(c.userId) < load.get(best.userId) ? c : best), candidates[0]);
                load.set(target.userId, load.get(target.userId) + 1);
            }
            row.assigneeSelect.value = String(target.userId);
        });

        renderPlanHint();
    }

    groupSelect.addEventListener('change', loadCandidates);
    splitSelect.addEventListener('change', applySplit);

    /** 送出前把「誰分到幾台」講出來——分攤規則再確定，看不到結果的人也不會信任它 */
    function renderPlanHint() {
        const selected = rows.filter(r => r.check.checked);
        if (mode !== 'group' || candidates.length === 0) {
            planHint.textContent = selected.length > 0 ? `將指派 ${selected.length} 台主機。` : '';
            return;
        }

        const counts = new Map();
        for (const row of selected) {
            const id = Number(row.assigneeSelect.value);
            counts.set(id, (counts.get(id) ?? 0) + 1);
        }
        const parts = candidates
            .filter(c => counts.has(c.userId))
            .map(c => `${formatUserName(c.displayName, c.account)} ${counts.get(c.userId)} 台`);
        planHint.textContent = parts.length > 0 ? `分攤結果：${parts.join('、')}` : '';
    }

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary';
    submit.textContent = '送出指派';
    form.appendChild(submit);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const selected = rows.filter(r => r.check.checked);
        if (selected.length === 0) {
            toast('請至少勾選一台主機', 'warning');
            return;
        }
        if (mode === 'group' && candidates.length === 0) {
            toast('這個群組沒有啟用中的成員，無法分攤', 'warning');
            return;
        }

        const restore = withBusy(submit, '送出中');
        try {
            const result = await api.post('/api/handling/issue-cases/bulk-assign', {
                source: group.source,
                eventId: group.eventId,
                hostIds: selected.map(r => Number(r.check.dataset.hostId)),
                // 單人模式送 handlerId，群組模式逐台送 assignments（後端一支 API 兩用）
                handlerId: mode === 'user' ? Number(handlerSelect.value) : 0,
                assignments: mode === 'group'
                    ? selected.map(r => ({ hostId: r.host.hostId, handlerId: Number(r.assigneeSelect.value) }))
                    : [],
                reassignHostIds: selected.filter(r => r.reassignCheck?.checked).map(r => r.host.hostId),
                note: noteInput.value.trim() || null,
                dueDate: dueInput.value || null
            });

            const parts = [`已建立 ${result.created} 個案件`];
            if (result.reassigned?.length) parts.push(`改派 ${result.reassigned.length} 台`);
            if (result.skipped?.length) parts.push(`${result.skipped.length} 台已由他人案件涵蓋，未變更`);
            toast(parts.join('；'), 'success');

            // 被指派者看不到主機時提醒指派的人（§7）：指派成立，但對方只看得到這個問題
            if (result.assigneeNoAccess?.length) {
                const names = [...new Set(result.assigneeNoAccess.map(a => a.handlerName))];
                toast(`${names.join('、')} 沒有部分主機的檢視權限（${result.assigneeNoAccess.length} 台），` +
                    '只看得到被指派的這個問題。若需要完整權限，請至「群組與授權」調整。', 'warning', 10000);
            }

            // 被指派者**動不了**（體檢 H1）：與「看不到」是兩件事，分開講。
            // 看不到還能被授與範圍；動不了則是工作進了對方清單、對方做不了任何事，
            // 而指派的人過去完全不知情
            if (result.assigneeCannotHandle?.length) {
                const detail = result.assigneeCannotHandle
                    .map(a => `${a.handlerName}（${a.hostCount} 台）`)
                    .join('、');
                toast(`${detail} 沒有「處理」能力，收到交辦後無法回覆處理狀態。` +
                    '指派已完成；若要讓對方能處理，請至「群組與授權」把他加入具處理能力的群組，' +
                    '或在主機頁指定為負責人。', 'warning', 12000);
            }

            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            currentPage = 1;
            search();
        } catch (error) {
            restore();
        }
    });

    body.appendChild(form);
    setMode('user');
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
        if (!btn || btn.disabled) return;
        btn.classList.toggle('active');
        currentPage = 1;
        search();
    });
}

// 未指派 chip（§10，僅依問題視角）：即點即篩
document.getElementById('filter-unassigned-chip').addEventListener('click', event => {
    event.currentTarget.classList.toggle('active');
    currentPage = 1;
    search();
});

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
    // 依問題視角（§10.3）：「涵蓋範圍」與「出現密度」在畫面上是合併字串（好讀），
    // 匯出時**拆成獨立欄位**——CSV 是給人貼進試算表再排序／樞紐的，
    // `2026-05-06 ~ 2026-07-28` 與 `3/98` 這種合併字串在 Excel 裡是死的
    if (currentView === 'issue') {
        return ['來源', 'Event ID', '分類', '嚴重度', '重大', '主機數',
            '首見', '最近出現', '距今天數', '出現天數', '期間天數',
            '風險日數', '總次數', '處理概況', '處理人'];
    }
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
        return [quote(item.source), item.eventId, CATEGORY_NAMES[item.category] ?? item.category,
            severityName(item.maxSeverity), item.elevatesDayRisk ? '是' : '',
            item.hostCount,
            item.firstSeen, item.lastSeen, item.daysSinceLastSeen,
            item.activeDays, item.periodDays,
            item.dayCount, item.totalCount,
            quote(item.handlingSummary), quote((item.handlers ?? []).map(h => h.displayName).join('、'))];
    }
    const handler = item.handlerName
        ? formatUserName(item.handlerName, item.handlerAccount) + (item.handlerFromCase ? '（案件）' : '')
        : '';
    return [item.date, quote(item.hostName), item.riskLevel, quote(item.headline),
        cats(item.categories), quote(item.handlingStatusText), quote(handler)];
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

guardLoad(listContainer, init);
