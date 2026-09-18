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

import { api, getAiAvailable, getDisplaySettings, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    renderTable, renderLoading, renderSpinner, renderEmpty, toast, renderPagination, withBusy, renderChips,
    loadPageSize, savePageSize, PAGE_SIZE_OPTIONS, showDetailModal, button, searchableUserSelect, guardLoad,
    headerWithHelp, icon
} from '../core/ui.js';
import {
    riskBadge, handlingBadge, statusBadge, severityBadge, CATEGORY_NAMES, CATEGORY_ORDER, severityName, formatNumber,
    formatUserName, toLocalDateString, todayLocal, analysisAnchorLocal, isAiRetryPending, issueBaselineCell
} from '../core/format.js';
import { renderAiText } from '../core/markdown-lite.js';
import { openIssueStatusReplyModal } from './issue-status-reply.js';
import { openIssueMuteModal } from './issue-mute-modal.js';
import { bindRangeChips } from '../core/date-range.js';

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
// 全站「日風險等級顯示」設定允許的等級（見 syncRiskChipSemantics）
let dayRiskVisible = new Set(['高', '中', '低']);

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
    dayRiskVisible = new Set((await getDisplaySettings())?.visibleDayRiskLevels ?? ['高', '中', '低']);
    syncRiskChipSemantics();
}

/**
 * 這排 chip 在不同視角是**不同層級**：依問題視角（問題主視角）篩的是「問題嚴重度」，
 * 其餘視角篩的是「日風險等級」。因此：
 *   - 標籤文字隨視角改，使用者看得出自己在篩什麼；
 *   - 全站「日風險等級顯示」設定只能藏／取消日層級的 chip，**不能**動到依問題視角的嚴重度
 *     篩選（拿日層級的可見性去取消嚴重度條件，會讓低嚴重度問題整批消失、與儀表板風險類型卡
 *     的數字對不上，正是這個設定想避免的那種「看不見卻仍生效」）。
 */
function syncRiskChipSemantics() {
    const isIssueView = currentView === 'issue';
    const label = document.getElementById('filter-risk-label');
    if (label) label.textContent = isIssueView ? '問題嚴重度' : '風險層級';

    for (const btn of document.querySelectorAll('#filter-risk-chips [data-risk]')) {
        const hidden = !isIssueView && !dayRiskVisible.has(btn.dataset.risk);
        btn.classList.toggle('d-none', hidden);
        if (hidden) btn.classList.remove('active');
        btn.title = isIssueView ? '問題自身的嚴重度（與該問題出現在哪種風險日無關）' : '';
    }
}

/**
 * AI 歸納（docs/archive/HISTORY.md §6 W1-2）：使用者主動點才呼叫（查詢頁高頻，
 * 自動呼叫會塞爆 AI 佇列）。只在明細視角、且 AI 可用時顯示按鈕。
 */
async function initAiSummary() {
    aiAvailable = await getAiAvailable();
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

    syncRiskChipSemantics();

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
    // 帶處理狀態篩選時是整個保留期的記憶體推導，大站台會超過預設 60 秒
    lastResult = await api.get(`${ENDPOINT[currentView]}?${query}`, { timeoutMs: 120000 });
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
    // 依問題視角多帶「共 N 台主機（去重）」（回饋二十輪 B2／終檢補接）：列表各列的主機數
    // 是各問題自己的台數、加總會大於儀表板風險類型卡的去重數，這個才是與卡片同一口徑的數字
    let countText = lastResult.total > 0 ? `共 ${lastResult.total} ${VIEW_UNIT[currentView] ?? '筆'}` : '';
    if (currentView === 'issue' && lastResult.total > 0 && Number.isInteger(lastResult.distinctHostCount)) {
        countText += `，共 ${lastResult.distinctHostCount} 台主機（去重）`;
    }
    const countNodes = [document.createTextNode(countText)];
    // 靜音中的問題不列在依問題視角（規劃 15.3 (2)）：計數列尾端誠實說出少了幾個。
    // 查出零筆時更要說——期間內的問題若全被靜音，畫面只剩空清單，看起來像「沒有問題」
    const mutedIssueCount = lastResult.mutedIssueCount;
    if (currentView === 'issue' && Number.isInteger(mutedIssueCount) && mutedIssueCount > 0) {
        const lead = countText ? '；另有 ' : '另有 ';
        if (hasCapability(currentUser, 'Maintain')) {
            const link = document.createElement('a');
            link.href = appUrl('/work-orders') + '#muted';
            link.textContent = `${mutedIssueCount} 個問題靜音中`;
            countNodes.push(document.createTextNode(lead), link, document.createTextNode('（未列出）'));
        } else {
            countNodes.push(document.createTextNode(`${lead}${mutedIssueCount} 個問題靜音中（未列出）`));
        }
    }
    document.getElementById('result-count').replaceChildren(...countNodes);

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
            { title: '日期', sortKey: 'date', sortDefaultDir: 'desc', render: r => r.date },
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

function headlineCell(record) {
    const wrap = document.createElement('span');

    if (record.aiAnalyzed && record.headline) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary me-1';
        badge.textContent = 'AI';
        badge.title = 'AI 產出摘要';
        wrap.appendChild(badge);
    } else if (record.aiPending && isAiRetryPending(record.headline)) {
        // AI 曾嘗試但完全失敗、已標為待補（回饋二十輪 N）：與「分析中」區分開
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--warning me-1';
        badge.textContent = 'AI 待補';
        badge.title = 'AI 服務當時未回應，可用排程頁「只補跑失敗或未執行」補回';
        wrap.appendChild(badge);
    } else if (record.aiPending) {
        // 統計段已寫入、AI 段還在排隊或執行中（docs/archive/FEEDBACK-12-PLAN.md §3.5）——
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
    link.href = appUrl(`/handlers/${record.handlerId}`);
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
            { title: '日期', sortKey: 'date', sortDefaultDir: 'desc', render: d => d.date },
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

// ── 依問題視角（主機與日期都合併，docs/archive/FEEDBACK-4-PLAN.md §4）──────────────────

function renderIssueView() {
    // 欄位順序＝「這個問題有多嚴重／影響多廣／什麼形狀／誰在處理」（規劃 §10.2 的四個維度）。
    // 「涵蓋範圍」與「出現密度」是需求「期間跨度」的落地：只有「最近出現」看不出
    // 這是天天都有的背景值、還是近三天才冒出來的新問題。
    const columns = [
        { title: '問題', className: 'lf-issue-col', render: i => issueGroupCell(i) },
        { title: '分類', render: i => CATEGORY_NAMES[i.category] ?? i.category },
        { title: '嚴重度', sortKey: 'severity', render: i => issueSeverityCell(i) },
        {
            title: '主機數 / 主機日',
            // 兩個數字上下兩行（回饋二十六輪 E1）：單行 nowrap 時這一欄要 12 個字寬，
            // 依問題視角有 12 欄，寬度全被少數幾欄吃掉
            className: 'text-end',
            sortKey: 'hostCount', sortDefaultDir: 'desc',
            renderHeader: () => headerWithHelp('主機數 / 主機日', '在本次查詢日期區間內，曾出現此問題的相異主機總台數（台），以及累計出現的主機日總數（主機日，即展開明細的總筆數）。', '主機數與主機日'),
            render: i => {
                const wrap = document.createElement('div');
                const hosts = document.createElement('div');
                hosts.className = 'text-nowrap';
                hosts.textContent = `${formatNumber(i.hostCount)} 台`;
                const days = document.createElement('div');
                days.className = 'small text-muted text-nowrap';
                days.textContent = `${formatNumber(i.dayCount)} 主機日`;
                wrap.append(hosts, days);
                return wrap;
            }
        },
        {
            title: 'vs 基準',
            // 徽章與基準說明各自一行（E1）：不 nowrap 整欄，由 issueBaselineCell 內部
            // 對「基準 N 台/日 → M 台」那一行自己保 nowrap
            className: '',
            renderHeader: () => headerWithHelp('vs 基準', '最近一次出現時的受影響主機數，與過去 30 天歷史中位數（基準台數/日）的比較。倍數大於等於 2（紅色）代表異常擴散，1 到 2 之間（灰色）為正常波動，小於 1（綠色「收斂」）代表影響範圍已低於平時基準。', 'vs 基準線'),
            render: i => issueBaselineCell(i)
        },
        {
            title: '首見',
            className: 'text-nowrap',
            renderHeader: () => headerWithHelp('首見', '此問題在全機房歷史記錄中首次出現的日期。若本次查詢區間內的首次出現日與機房首見不同，第二行會額外標示本次查詢區間的「本期首見」日期。', '首見日期'),
            render: i => issueFirstSeenCell(i)
        },
        {
            title: '出現密度',
            className: 'text-center text-nowrap',
            renderHeader: () => headerWithHelp('出現密度', '查詢期間內有發生此問題的天數比例（出現天數 / 查詢天數）。比例高（如 30/30）代表天天發生的背景雜訊；比例低（如 2/30）代表近期或零星爆發的突發狀況。', '出現密度'),
            render: i => issueDensityCell(i)
        },
        {
            title: '總次數',
            className: 'text-end',
            sortKey: 'totalCount', sortDefaultDir: 'desc',
            renderHeader: () => headerWithHelp('總次數', '在本次查詢日期區間內，所有受影響主機累計觸發此事件記錄的總次數。', '總次數'),
            render: i => formatNumber(i.totalCount)
        },
        { title: '最近出現', className: 'text-nowrap', sortKey: 'lastSeen', sortDefaultDir: 'desc', render: i => issueLastSeenCell(i) },
        {
            title: '處理概況',
            className: 'text-nowrap',
            renderHeader: () => headerWithHelp('處理概況', '受此問題影響的主機目前處理狀態分佈。分為未指派或待確認的「未處理」、已有案件或跟進中的「處理中」，以及已結案或確認為雜訊/誤報的「已處理」台數。', '處理概況'),
            render: i => issueHandlingSummaryCell(i)
        },
        { title: '處理人', render: i => issueHandlersCell(i) },
        { title: '已交辦', render: i => issueAssignedCell(i) }
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
    foot.className = 'small mt-2 d-flex align-items-center gap-2';
    if (result.total > result.items.length) {
        const truncNote = document.createElement('span');
        truncNote.className = 'text-muted';
        truncNote.textContent = `共 ${formatNumber(result.total)} 筆（不套用風險層級篩選），僅顯示前 ${result.items.length} 筆。`;
        foot.appendChild(truncNote);
    }
    const allLink = document.createElement('a');
    allLink.href = detailForIssue(group);
    allLink.textContent = '在明細視角檢視這個問題的全部風險日 →';
    foot.appendChild(allLink);

    wrap.replaceChildren(inner, foot);
}

/** 展開列內每列的「去處理」連結，連到該主機該日風險日詳情 */
function goHandleLink(record) {
    const link = document.createElement('a');
    link.href = appUrl(`/records/${record.hostId}/${record.date}`);
    link.className = 'btn btn-sm btn-outline-primary';
    link.textContent = '去處理';
    return link;
}

/**
 * 依問題視角「處理人」欄：每個名字連到「這個問題、這個人」的交辦單（定案 49）。
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
        const name = formatUserName(h.displayName, h.account);
        link.href = appUrl('/work-orders') + '?' + new URLSearchParams({
            source: group.source, eventId: String(group.eventId), handlerId: String(h.handlerId)
        });
        link.textContent = name;
        link.title = `檢視 ${name} 在這個問題的交辦單`;
        link.addEventListener('click', event => event.stopPropagation());
        wrap.appendChild(link);
    });
    if (handlers.length > 3) {
        wrap.appendChild(document.createTextNode(` 等 ${handlers.length} 人`));
    }
    return wrap;
}

/**
 * 已交辦欄：顯示該問題已在進行中交辦單內的主機數與總主機數。
 * 零台時整格顯示灰字「未交辦」；有交辦時為連往總覽頁該問題篩選的連結。
 */
function issueAssignedCell(group) {
    const assigned = group.assignedHostCount || 0;
    const total = group.hostCount || 0;

    if (assigned === 0) {
        const span = document.createElement('span');
        span.className = 'text-muted';
        span.textContent = '未交辦';
        return span;
    }

    const wrap = document.createElement('div');
    const line1 = document.createElement('div');
    line1.className = 'text-nowrap';

    const params = new URLSearchParams({
        source: group.source,
        eventId: String(group.eventId)
    });
    const link = document.createElement('a');
    link.href = `${appUrl('/work-orders')}?${params.toString()}`;
    link.title = '檢視這個問題的交辦單';
    link.textContent = `${assigned}／${total} 台`;
    link.addEventListener('click', event => event.stopPropagation());
    line1.appendChild(link);
    wrap.appendChild(line1);

    if (assigned < total) {
        const line2 = document.createElement('div');
        line2.className = 'small text-muted text-nowrap';
        line2.textContent = `還有 ${total - assigned} 台未交辦`;
        wrap.appendChild(line2);
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

/**
 * 處理概況：三種狀態各一行（未處理／處理中／已處理），數量為 0 時淡色呈現
 */
function issueHandlingSummaryCell(group) {
    const wrap = document.createElement('div');
    wrap.className = 'small lf-mono';

    const unhandled = document.createElement('div');
    unhandled.className = group.unhandledCount > 0 ? '' : 'text-muted';
    unhandled.textContent = `${formatNumber(group.unhandledCount)} 台未處理`;

    const inProgress = document.createElement('div');
    inProgress.className = group.inProgressCount > 0 ? '' : 'text-muted';
    inProgress.textContent = `${formatNumber(group.inProgressCount)} 台處理中`;

    const resolved = document.createElement('div');
    resolved.className = group.resolvedCount > 0 ? '' : 'text-muted';
    resolved.textContent = `${formatNumber(group.resolvedCount)} 台已處理`;

    wrap.append(unhandled, inProgress, resolved);
    return wrap;
}

/**
 * 首見（兩欄合併）：預設顯示機房首見；當本期首見與機房首見不同時，
 * 換行顯示第二行標示本期首見，並附 SVG 圖示標記差異；相同時只顯示一行。
 * 兩行都以日期起頭，標記放在日期之後——圖示放行首會把第二行的日期推開，
 * 兩個日期的左緣就對不齊了。
 */
function issueFirstSeenCell(group) {
    const wrap = document.createElement('div');
    const fleetFirst = group.fleetFirstSeen || group.firstSeen;
    const periodFirst = group.firstSeen;

    wrap.title = '機房首見：此問題在全機房歷史記錄中首次出現的日期。\n本期首見：此問題在本次查詢區間內首次出現的日期。';

    const fleetLine = document.createElement('div');
    fleetLine.className = 'lf-mono small';
    fleetLine.textContent = fleetFirst;
    wrap.appendChild(fleetLine);

    if (periodFirst && periodFirst !== fleetFirst) {
        const periodLine = document.createElement('div');
        periodLine.className = 'lf-mono small text-muted d-flex align-items-center gap-1 mt-1';
        const date = document.createElement('span');
        date.textContent = periodFirst;
        periodLine.appendChild(date);
        const marker = document.createElement('span');
        marker.className = 'd-inline-flex align-items-center gap-1';
        marker.appendChild(icon('info-circle'));
        const text = document.createElement('span');
        text.textContent = '本期';
        marker.appendChild(text);
        periodLine.appendChild(marker);
        wrap.appendChild(periodLine);
    }

    return wrap;
}


/**
 * 出現密度：出現天數 ÷ 期間天數（§10.3）。
 * 數字在上、進度條換行在下，欄寬不變。既有的數字文字與 tooltip 內容保留。
 */
function issueDensityCell(group) {
    const wrap = document.createElement('div');
    wrap.className = 'd-inline-flex flex-column align-items-center';

    const text = document.createElement('span');
    text.className = 'lf-mono small';
    text.textContent = `${group.activeDays}/${group.periodDays}`;
    wrap.appendChild(text);

    const ratio = group.periodDays > 0 ? group.activeDays / group.periodDays : 0;
    const bar = document.createElement('span');
    bar.className = 'lf-density mt-1';
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
        hint.textContent = '昨日仍在發生';
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

    // 白話說明：在來源(EventId)之下多一行；單行截斷、設最大寬度、hover/focus 可看完整內容。
    // 沒有說明且分類是「其他」時改顯示固定句（回饋二十六輪 E3）——「其他」在定義上就是
    // 沒命中任何規則的收容分類，整欄留白會讓人以為是資料漏了，而不是「本來就沒有規則」。
    // 兩種都沒有才補固定句：knownIssue（規則描述快照）本身就是說明，兩行都補會變成廢話
    const fallbackExplanation = group.knownIssue
        ? null
        : (group.category === 'Other'
            ? '未命中任何規則的事件，分類為其他'
            : '這個問題的規則沒有填寫白話說明');
    const explanationText = group.plainExplanation || fallbackExplanation;
    if (explanationText) {
        const explanation = document.createElement('div');
        explanation.className = 'lf-issue-explanation';
        explanation.textContent = explanationText;
        explanation.title = group.plainExplanation
            ? explanationText
            : '沒有可顯示的白話說明。可在「規則維護」為這個事件建立規則或補上說明。';
        explanation.tabIndex = 0;
        wrap.appendChild(explanation);
    }

    // 問題負責人 badge（回饋十八輪批次F）：「這個問題歸誰」在主視角一眼可見——
    // 與下方 issueHandlersCell（現在誰在處理）是不同概念，見後端 IssueGroupDto.IssueOwnerNames 註解。
    if (group.issueOwnerNames?.length > 0) {
        const owner = document.createElement('div');
        owner.className = 'small mt-1';
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--info';
        badge.textContent = `負責人：${group.issueOwnerNames.join('、')}`;
        owner.appendChild(badge);
        wrap.appendChild(owner);
    }

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
    // 直排（E1）：三顆按鈕橫排時這一欄約 15 個字寬，是全表最寬的欄之一
    wrap.className = 'd-flex flex-column gap-1 align-items-end';

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
    if (hasCapability(currentUser, 'Maintain')) wrap.appendChild(issueMuteButton(group));

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
    btn.textContent = '交辦';
    btn.title = '把這個問題交辦給處理人（建立交辦單）';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openWorkOrderModal(group);
    });
    return btn;
}

/**
 * 靜音（B-3）：把這個問題在一段期間內完全噤聲，到期自動恢復。modal 是共用元件
 * （問題檔案頁的「靜音／延長」走同一顆）。這裡沒有現成的 currentMute 可帶——
 * 依問題視角的列不含靜音區間，傳 null 就是「新設定一段靜音」的版面；
 * 後端在今天已靜音時本來就會延長，不會因此多出一段區間。
 */
function issueMuteButton(group) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-outline-secondary';
    btn.textContent = '靜音';
    btn.title = '暫時不看這個問題（到期自動恢復）';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openIssueMuteModal({
            source: group.source,
            eventId: group.eventId,
            issueLabel: `${group.source} (${group.eventId})`,
            currentMute: null,
            onApplied: search
        });
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

    // 機房結論自動套用（回饋十九輪批次F，§2 決策一）：這次操作只處理上方列出的既有日子，
    // 勾選後才會另外把這個問題設成機房結論，讓之後新出現的主機日也自動套用同一個結論
    const autoApplyWrap = document.createElement('div');
    autoApplyWrap.className = 'form-check mb-3';
    const autoApplyInput = document.createElement('input');
    autoApplyInput.type = 'checkbox';
    autoApplyInput.className = 'form-check-input';
    autoApplyInput.id = 'bulk-close-auto-apply';
    const autoApplyLabel = document.createElement('label');
    autoApplyLabel.className = 'form-check-label small';
    autoApplyLabel.htmlFor = 'bulk-close-auto-apply';
    autoApplyLabel.textContent = '之後新出現的主機日也自動套用這個結論（設為機房結論）';
    autoApplyWrap.append(autoApplyInput, autoApplyLabel);
    form.appendChild(autoApplyWrap);

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
                note: noteInput.value.trim(),
                autoApply: autoApplyInput.checked
            });

            toast(`已標記 ${formatNumber(result.updatedHostCount)} 台主機、共 ${formatNumber(result.updatedDayCount)} 天` +
                  (result.skippedHostCount > 0 ? `；${formatNumber(result.skippedHostCount)} 台略過` : '') +
                  (autoApplyInput.checked ? '；已設為機房結論，之後新出現的主機日將自動套用' : ''), 'success', 6000);

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
    link.href = appUrl(`/records?${params.toString()}`);
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
 * 交辦 modal（依問題視角建立交辦單）。
 */
function openWorkOrderModal(group) {
    const body = document.createElement('div');
    const loadingWrap = document.createElement('div');
    loadingWrap.className = 'd-flex justify-content-center py-3';
    body.appendChild(loadingWrap);
    renderSpinner(loadingWrap, '載入中…');

    showDetailModal({ title: `交辦：${group.source} (${group.eventId})`, body, size: 'modal-lg' });
    loadWorkOrderForm(group, body);
}

async function loadWorkOrderForm(group, body) {
    let users, groups;
    try {
        [users, groups] = await Promise.all([
            api.get('/api/admin/users', { silent: true }),
            api.get('/api/admin/groups', { silent: true })
        ]);
    } catch (error) {
        body.replaceChildren();
        const msg = document.createElement('div');
        msg.className = 'text-danger';
        msg.textContent = error?.message || '載入資料失敗';
        body.appendChild(msg);
        return;
    }

    renderWorkOrderForm(body, group, users, groups);
}

/** 交辦範圍由頁面篩選推導：有選主機→Hosts；否則有選主機群組→Groups；都沒有→All（payload 與畫面共用） */
function workOrderScopeKind(filters) {
    if (filters.hostIds.length > 0) return 'Hosts';
    if (filters.groupIds.length > 0) return 'Groups';
    return 'All';
}

function workOrderScopeText(scopeKind, filters) {
    if (scopeKind === 'Hosts') return `指定主機（篩選列選的 ${formatNumber(filters.hostIds.length)} 台）`;
    if (scopeKind === 'Groups') {
        const names = filters.groupIds.map(id => {
            const found = hostGroups.find(g => String(g.groupId) === String(id));
            return found ? found.groupName : `群組 #${id}`;
        });
        return `主機群組：${names.join('、')}`;
    }
    return '全站（依目前篩選的期間）';
}

function renderWorkOrderForm(body, group, users, groups) {
    body.replaceChildren();

    const filters = collectFilters();
    let assignMode = 'single';
    const excludedHostIds = new Set();
    let currentPage = 1;
    let previewRequestId = 0;
    let isFetchingPreview = false;
    let pendingFetch = null;

    const form = document.createElement('form');

    // 1. 常駐說明（lf-hint）
    const hint = document.createElement('div');
    hint.className = 'lf-hint mb-3';
    hint.textContent = '交辦會建立交辦單：這個問題在選取主機上的案件都掛在同一張單，處理人回覆一次就套用到整張單。之後同一問題的新風險日會自動掛進單裡，直到結案。';
    form.appendChild(hint);

    // 2. 交辦給：單一使用者／使用者群組（平均分攤）
    const modeWrap = document.createElement('div');
    modeWrap.className = 'mb-3';
    const modeLabel = document.createElement('div');
    modeLabel.className = 'form-label small text-muted';
    modeLabel.textContent = '交辦給';
    const modeGroup = document.createElement('div');
    modeGroup.className = 'btn-group btn-group-sm mb-2';
    const modeUserBtn = button('單一使用者', { variant: 'outline-secondary', onClick: () => setMode('single') });
    const modeGroupBtn = button('使用者群組（平均分攤）', { variant: 'outline-secondary', onClick: () => setMode('group') });
    modeUserBtn.classList.add('active');
    modeGroup.append(modeUserBtn, modeGroupBtn);
    modeWrap.append(modeLabel, modeGroup);
    form.appendChild(modeWrap);

    // 單一使用者：searchableUserSelect
    const defaultUser = users.find(u => u.active);
    const { element: handlerSelectWrap, select: handlerSelect } = searchableUserSelect(users, {
        selectedId: defaultUser ? defaultUser.userId : null,
        onChange: () => {
            currentPage = 1;
            requestPreview(1);
        }
    });
    handlerSelectWrap.classList.add('mb-3');
    form.appendChild(handlerSelectWrap);

    // 使用者群組：群組下拉＋分攤方式
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
    groupSelect.addEventListener('change', () => {
        currentPage = 1;
        requestPreview(1);
    });

    const splitWrap = document.createElement('div');
    splitWrap.className = 'd-flex align-items-center gap-2 flex-wrap';
    const splitLabel = document.createElement('span');
    splitLabel.className = 'small text-muted';
    splitLabel.textContent = '分攤方式';
    const splitSelect = document.createElement('select');
    splitSelect.className = 'form-select form-select-sm w-auto';
    for (const option of [
        { value: 'byLoad', label: '依現有負載（手上案件少的人多分）' },
        { value: 'roundRobin', label: '平均輪流（每人台數盡量相同）' }
    ]) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        splitSelect.appendChild(el);
    }
    splitSelect.addEventListener('change', () => {
        currentPage = 1;
        requestPreview(1);
    });
    splitWrap.append(splitLabel, splitSelect);
    groupWrap.append(groupSelect, splitWrap);
    form.appendChild(groupWrap);

    function setMode(next) {
        if (assignMode === next) return;
        assignMode = next;
        modeUserBtn.classList.toggle('active', assignMode === 'single');
        modeGroupBtn.classList.toggle('active', assignMode === 'group');
        handlerSelectWrap.classList.toggle('d-none', assignMode !== 'single');
        groupWrap.classList.toggle('d-none', assignMode !== 'group');
        currentPage = 1;
        requestPreview(1);
    }

    // 範圍：由頁面篩選推導（有選主機→Hosts；否則有選主機群組→Groups；都沒有→All），不另設選單
    const scopeKind = workOrderScopeKind(filters);
    const scopeWrap = document.createElement('div');
    scopeWrap.className = 'mb-3';
    const scopeLine = document.createElement('div');
    scopeLine.className = 'small text-muted mb-1';
    scopeLine.textContent = `範圍：${workOrderScopeText(scopeKind, filters)}`;
    scopeWrap.appendChild(scopeLine);

    const autoAttachLabel = document.createElement('label');
    autoAttachLabel.className = 'form-check-label small d-flex align-items-center gap-1';
    const autoAttachCheck = document.createElement('input');
    autoAttachCheck.type = 'checkbox';
    autoAttachCheck.className = 'form-check-input mt-0';
    autoAttachCheck.checked = true;
    autoAttachLabel.append(autoAttachCheck, document.createTextNode('續掛新主機'));
    const autoAttachHelp = document.createElement('div');
    autoAttachHelp.className = 'form-text';
    if (scopeKind !== 'Hosts') {
        scopeWrap.append(autoAttachLabel, autoAttachHelp);
    }
    form.appendChild(scopeWrap);

    // 手動排除任一台主機時停用續掛並取消勾選；排除全部取消後恢復可用並回到預設勾選
    function updateAutoAttachState() {
        const hasExcluded = excludedHostIds.size > 0;
        if (hasExcluded) {
            autoAttachCheck.checked = false;
            autoAttachHelp.textContent = '已手動排除主機時不提供續掛（被排除的主機之後再出現會被自動加回）。';
        } else {
            if (autoAttachCheck.disabled) autoAttachCheck.checked = true;
            autoAttachHelp.textContent = '之後這個範圍內新出現此問題、還沒有人處理的主機，夜間自動加入這張單。';
        }
        autoAttachCheck.disabled = hasExcluded;
    }
    updateAutoAttachState();

    // 3. 說明（note，選填，textarea）與期限（dueDate，選填，input type="date"）
    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small text-muted';
    noteLabel.textContent = '說明（選填）';
    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm mb-3';
    noteInput.rows = 2;
    form.append(noteLabel, noteInput);

    const dueLabel = document.createElement('label');
    dueLabel.className = 'form-label small text-muted';
    dueLabel.textContent = '期限（選填）';
    const dueInput = document.createElement('input');
    dueInput.type = 'date';
    dueInput.className = 'form-control form-control-sm mb-3';
    form.append(dueLabel, dueInput);

    // 4. 衝突處理
    const conflictWrap = document.createElement('div');
    conflictWrap.className = 'mb-3';

    const conflictList = document.createElement('div');
    conflictList.className = 'small text-muted mb-1 d-none';

    const reassignCheckLabel = document.createElement('label');
    reassignCheckLabel.className = 'form-check-label small d-flex align-items-center gap-1';
    const reassignCheck = document.createElement('input');
    reassignCheck.type = 'checkbox';
    reassignCheck.className = 'form-check-input mt-0';
    reassignCheck.addEventListener('change', () => {
        currentPage = 1;
        requestPreview(1);
    });
    reassignCheckLabel.append(reassignCheck, document.createTextNode('把已由他人處理中的主機一併改派給新處理人'));
    conflictWrap.append(conflictList, reassignCheckLabel);
    form.appendChild(conflictWrap);

    // 5. 預覽摘要（每次預覽回來就重畫）
    const summaryWrap = document.createElement('div');
    summaryWrap.className = 'mb-3';
    form.appendChild(summaryWrap);

    // 6. 主機清單（可展開／收合，預設收合，標題「檢視受影響主機（{totalHosts} 台）」）
    const hostDetails = document.createElement('details');
    hostDetails.className = 'mb-3';
    const hostSummary = document.createElement('summary');
    hostSummary.className = 'form-label small text-muted';
    hostSummary.style.cursor = 'pointer';
    hostSummary.textContent = '檢視受影響主機（0 台）';
    hostDetails.appendChild(hostSummary);

    const hostDetailsBody = document.createElement('div');
    hostDetailsBody.className = 'mt-2';

    const hostList = document.createElement('div');
    hostList.className = 'lf-bulk-assign-hosts mb-2';

    const hostPager = document.createElement('div');
    hostDetailsBody.append(hostList, hostPager);
    hostDetails.appendChild(hostDetailsBody);
    form.appendChild(hostDetails);

    // 7. 送出按鈕
    const submitBtn = document.createElement('button');
    submitBtn.type = 'submit';
    submitBtn.className = 'btn btn-sm btn-primary';
    submitBtn.textContent = '建立交辦單';
    form.appendChild(submitBtn);

    body.appendChild(form);

    // ── 預覽與送出共用的組請求函式 ───────────────────────────────────────
    function buildWorkOrderPayload(page = 1) {
        return {
            source: group.source,
            eventId: group.eventId != null ? Number(group.eventId) : null,
            from: filters.from || null,
            to: filters.to || null,
            hostIds: filters.hostIds.length > 0 ? filters.hostIds.map(Number) : null,
            groupIds: filters.groupIds.length > 0 ? filters.groupIds.map(Number) : null,
            excludeHostIds: [...excludedHostIds],
            assignMode,
            handlerId: assignMode === 'single' ? (Number(handlerSelect.value) || null) : null,
            groupId: assignMode === 'group' ? (Number(groupSelect.value) || null) : null,
            splitMode: assignMode === 'group' ? splitSelect.value : null,
            reassignConflicts: Boolean(reassignCheck.checked),
            scopeKind,
            autoAttach: scopeKind !== 'Hosts' && autoAttachCheck.checked && excludedHostIds.size === 0,
            note: noteInput.value.trim() || null,
            dueDate: dueInput.value || null,
            page
        };
    }

    // ── 預覽時機與排程 ───────────────────────────────────────────────────
    async function requestPreview(page = currentPage) {
        if (isFetchingPreview) {
            pendingFetch = page;
            return;
        }
        isFetchingPreview = true;
        try {
            do {
                const fetchPage = pendingFetch !== null ? pendingFetch : page;
                pendingFetch = null;
                const currentRequestId = ++previewRequestId;
                const payload = buildWorkOrderPayload(fetchPage);
                if (payload.assignMode === 'single' && !payload.handlerId) break;
                if (payload.assignMode === 'group' && !payload.groupId) break;

                try {
                    const preview = await api.post('/api/work-orders/preview', payload, { silent: true });
                    if (currentRequestId === previewRequestId) {
                        renderPreview(preview);
                    }
                } catch (error) {
                    if (currentRequestId === previewRequestId) {
                        summaryWrap.replaceChildren();
                        const errDiv = document.createElement('div');
                        errDiv.className = 'text-danger small';
                        errDiv.textContent = error?.message || '預覽失敗';
                        summaryWrap.appendChild(errDiv);
                    }
                }
            } while (pendingFetch !== null);
        } finally {
            isFetchingPreview = false;
        }
    }

    function renderPreview(preview) {
        // 衝突處理清單
        conflictList.replaceChildren();
        if (preview.conflicts && preview.conflicts.length > 0) {
            for (const c of preview.conflicts) {
                const item = document.createElement('div');
                item.textContent = `${c.handlerName}：${formatNumber(c.hostCount)} 台`;
                conflictList.appendChild(item);
            }
            conflictList.classList.remove('d-none');
        } else {
            conflictList.classList.add('d-none');
        }

        // 預覽摘要
        summaryWrap.replaceChildren();

        const primaryLine = document.createElement('div');
        primaryLine.className = 'fw-semibold mb-1';
        primaryLine.textContent = `將交辦 ${formatNumber(preview.affectedHosts ?? 0)} 台（${formatNumber(preview.affectedMembers ?? 0)} 個問題成員）`;
        summaryWrap.appendChild(primaryLine);

        const secondaryLine = document.createElement('div');
        secondaryLine.className = 'small text-muted mb-1';
        const daysLabel = preview.estimatedHostDaysLabel || '期間內';
        secondaryLine.textContent = `${daysLabel} 預估 ${formatNumber(preview.estimatedHostDays ?? 0)} 主機日`;
        summaryWrap.appendChild(secondaryLine);

        const exclusions = [];
        if (preview.noiseExcludedHosts > 0) {
            exclusions.push(`已知雜訊排除 ${formatNumber(preview.noiseExcludedHosts)} 台`);
        }
        if (preview.manuallyExcludedHosts > 0) {
            exclusions.push(`手動排除 ${formatNumber(preview.manuallyExcludedHosts)} 台`);
        }
        if (preview.pausedMembersExcluded > 0) {
            exclusions.push(`暫停接單略過 ${formatNumber(preview.pausedMembersExcluded)} 位成員`);
        }
        for (const ex of exclusions) {
            const exDiv = document.createElement('div');
            exDiv.className = 'small text-muted mb-1';
            exDiv.textContent = ex;
            summaryWrap.appendChild(exDiv);
        }

        if (preview.allocation && preview.allocation.length > 0) {
            const allocWrap = document.createElement('div');
            allocWrap.className = 'small mt-2 pt-2 border-top';
            for (const a of preview.allocation) {
                const row = document.createElement('div');
                const mergeNote = a.mergeIntoWorkOrderId != null
                    ? `併入單號 #${a.mergeIntoWorkOrderId}`
                    : '新建單';
                row.textContent = `${a.handlerName} ${formatNumber(a.hostCount)} 台（${mergeNote}）`;
                allocWrap.appendChild(row);
            }
            summaryWrap.appendChild(allocWrap);
        }

        // 主機清單
        hostSummary.textContent = `檢視受影響主機（${formatNumber(preview.totalHosts ?? 0)} 台）`;
        hostList.replaceChildren();

        if (preview.hosts && preview.hosts.length > 0) {
            for (const host of preview.hosts) {
                const row = document.createElement('div');
                row.className = 'd-flex align-items-center gap-2 py-1';

                const check = document.createElement('input');
                check.type = 'checkbox';
                check.className = 'form-check-input mt-0';
                check.checked = !excludedHostIds.has(host.hostId);
                check.addEventListener('change', () => {
                    if (check.checked) {
                        excludedHostIds.delete(host.hostId);
                    } else {
                        excludedHostIds.add(host.hostId);
                    }
                    updateAutoAttachState();
                    requestPreview(currentPage);
                });

                const name = document.createElement('span');
                name.className = 'small flex-grow-1';
                name.textContent = host.hostName;

                row.append(check, name);

                if (host.existingHandlerName) {
                    const existing = document.createElement('span');
                    existing.className = 'small text-muted';
                    existing.textContent = `目前：${host.existingHandlerName}`;
                    row.appendChild(existing);
                }

                if (host.noiseExcluded) {
                    const noise = document.createElement('span');
                    noise.className = 'small text-muted';
                    noise.textContent = '已知雜訊';
                    row.appendChild(noise);
                }

                if (host.manuallyExcluded) {
                    const manual = document.createElement('span');
                    manual.className = 'small text-muted';
                    manual.textContent = '已排除';
                    row.appendChild(manual);
                }

                hostList.appendChild(row);
            }
        } else {
            const empty = document.createElement('div');
            empty.className = 'small text-muted py-2';
            empty.textContent = '沒有受影響的主機';
            hostList.appendChild(empty);
        }

        const pageSize = preview.pageSize || 100;
        const totalPages = Math.ceil((preview.totalHosts || 0) / pageSize);
        renderPagination(hostPager, {
            page: preview.page || currentPage,
            totalPages,
            onPage: page => {
                currentPage = page;
                requestPreview(page);
            }
        });
    }

    // ── 送出建立交辦單 ───────────────────────────────────────────────────
    form.addEventListener('submit', async event => {
        event.preventDefault();

        if (assignMode === 'single' && !handlerSelect.value) {
            toast('請選擇處理人', 'warning');
            return;
        }
        if (assignMode === 'group' && !groupSelect.value) {
            toast('請選擇使用者群組', 'warning');
            return;
        }

        const payload = buildWorkOrderPayload(1);
        const restore = withBusy(submitBtn, '建立中');

        try {
            const result = await api.post('/api/work-orders', payload);

            const orders = result.orders || [];
            const createdCount = orders.filter(o => o.createdOrder).length;
            const mergedCount = orders.filter(o => !o.createdOrder).length;
            const totalCases = orders.reduce((sum, o) => sum + (o.newCases || 0) + (o.linkedExisting || 0) + (o.reassigned || 0), 0);

            toast(`已建立 ${createdCount} 張、併入 ${mergedCount} 張，共 ${totalCases} 台`, 'success');

            if (result.daySyncPendingCases > 0) {
                toast('逐日同步在背景進行，儀表板數字會在數分鐘內更新', 'info');
            }

            if (result.skipped && result.skipped.length > 0) {
                toast(`${result.skipped.length} 台略過（已由他人處理中）`, 'warning');
            }

            if (result.assigneeCannotHandle && result.assigneeCannotHandle.length > 0) {
                for (const a of result.assigneeCannotHandle) {
                    toast(`${a.handlerName} 沒有處理權限（${a.hostCount} 台）`, 'warning');
                }
            }

            if (result.assigneeNoAccessTotal > 0) {
                toast(`${result.assigneeNoAccessTotal} 台不在對方的檢視範圍（對方仍可經由案件看到被交辦的問題）`, 'warning');
            }

            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            currentPage = 1;
            search();
        } catch (error) {
            restore();
            toast(error?.message || '建立交辦單失敗', 'danger');
        }
    });

    // 初次載入預覽
    requestPreview(1);
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

const categoryOrderMap = new Map(CATEGORY_ORDER.map((c, i) => [c, i]));

function categoryBadges(categories) {
    // 依 CATEGORY_ORDER 的固定順序呈現，使各列類別順序一致；命中篩選者仍用主色
    const active = new Set(activeChips('filter-category-chips', 'category'));
    const wrap = document.createElement('span');
    const sorted = [...(categories ?? [])].sort((a, b) =>
        (categoryOrderMap.get(a) ?? 999) - (categoryOrderMap.get(b) ?? 999) || a.localeCompare(b)
    );
    for (const category of sorted) {
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

bindRangeChips({
    fromInput: document.getElementById('filter-from'),
    toInput: document.getElementById('filter-to'),
    onApply: () => { currentPage = 1; search(); }
});

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
    // `2026-05-06 ~ 2026-07-28` 與 `3/98` 這種合併字串在 Excel 裡是死的。
    // 「風險日數」（回饋十三輪 A5）已移除：畫面欄位（renderIssueView）本來就沒有這一欄，
    // CSV 與畫面欄位不一致會讓人以為匯出漏東西——出現天數／期間天數已能回答同樣的問題。
    if (currentView === 'issue') {
        return ['來源', 'Event ID', '分類', '嚴重度', '重大', '主機數',
            '本期首見', '最近出現', '距今天數', '出現天數', '期間天數',
            '首見（機房）', '基準台數／日', '偏離倍數',
            '總次數', '處理概況', '處理人'];
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
            item.fleetFirstSeen || item.firstSeen,
            item.baselineMedianHostCount ?? '', item.baselineDeviationMultiplier ?? '',
            item.totalCount,
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

// 期間篩選的預設終點錨在昨天（回饋十九輪批次C），不是真實今天——見 analysisAnchorLocal 的說明
function today() {
    return analysisAnchorLocal();
}

function defaultFrom() {
    const date = new Date();
    date.setDate(date.getDate() - 7);   // 錨點已右移一天，往前 7 天湊回原本 7 天的預設窗
    return toLocalDateString(date);
}

guardLoad(listContainer, init);
