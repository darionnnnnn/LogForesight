/**
 * 權限異動待辦（docs/WEB-SPEC.md §9.5）。
 *
 * 每一筆逐項顯示「時間／主機 (IP)／帳號／類別／異動說明／狀態」，
 * 支援伺服器端排序、分頁與展開檢視完整異動明細。
 * 支援關鍵字、網段、類別（多選）、來源、時間範圍篩選，
 * 篩選條件記憶於 localStorage 並同步至網址 query string。
 */

import { api } from '../core/api.js';
import {
    renderTable, renderLoading, renderPagination, loadPageSize, savePageSize,
    guardLoad, toast, withBusy, headerWithHelp, toggleAllTableDetails, renderChips
} from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

const STORAGE_KEY = 'lf.permissionChanges.filters';

const modal = new bootstrap.Modal(document.getElementById('confirm-modal'));
const confirmForm = document.getElementById('confirm-form');
const batchConfirmModal = new bootstrap.Modal(document.getElementById('batch-confirm-modal'));
const batchSkippedModal = new bootstrap.Modal(document.getElementById('batch-skipped-modal'));
const batchConfirmForm = document.getElementById('batch-confirm-form');

const filterForm = document.getElementById('filter-form');
const keywordInput = document.getElementById('filter-keyword');
const subnetInput = document.getElementById('filter-subnet');
const subnetFeedback = document.getElementById('filter-subnet-feedback');
const sourceSelect = document.getElementById('filter-source');
const fromInput = document.getElementById('filter-from');
const toInput = document.getElementById('filter-to');
const categoryRow = document.getElementById('filter-category-row');
const categoryChips = document.getElementById('filter-category-chips');
const resetBtn = document.getElementById('btn-reset-filters');

let currentStatus = 'pending';
let currentPage = 1;
let pageSize = loadPageSize('permissionChanges');
let sort = { key: 'detectedAt', dir: 'desc' };
let lastResult = null;
let isAllExpanded = false;
let pendingConfirm = null;   // { change, targetStatus }
let searchDebounce = null;

const selectedCategories = new Set();
const knownCategories = new Map(); // categoryKey -> categoryLabel

const selectedChanges = new Map(); // changeId -> change object
let headerCheckboxEl = null;
let batchTargetStatus = 'authorized';

/**
 * 載入可用的類別清單。刻意向後端要，而不是從當頁資料收集——
 * 只從當頁收集的話，沒出現在當頁的類別就不會出現在篩選裡，等於篩不到那一類。
 */
async function loadCategories() {
    try {
        const options = await api.get('/api/permission-changes/categories', { silent: true });
        if (Array.isArray(options)) {
            for (const opt of options) {
                if (opt.key) knownCategories.set(opt.key, opt.label || opt.key);
            }
        }
    } catch {
        // 類別清單載入失敗不影響其他篩選，維持空清單即可
    }
}

document.getElementById('perm-tabs').addEventListener('click', event => {
    const button = event.target.closest('[data-status]');
    if (!button) return;

    currentStatus = button.dataset.status;
    for (const tab of document.querySelectorAll('#perm-tabs .nav-link')) {
        tab.classList.toggle('active', tab === button);
    }
    // 切換頁籤要清空選取：勾選框只在「待確認」的列出現，切走之後那些選取項目
    // 使用者已經看不見，送出時後端會以「已被其他使用者處理過」回絕，
    // 那個理由與實情不符，會把排查帶往錯誤方向。
    selectedChanges.clear();
    currentPage = 1;
    load();
});

/** 讀取 localStorage 篩選偏好 */
function loadStoredFilters() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

/** 儲存篩選偏好至 localStorage */
function saveStoredFilters() {
    const filters = {
        q: keywordInput ? keywordInput.value.trim() : '',
        subnet: subnetInput ? subnetInput.value.trim() : '',
        category: [...selectedCategories].join(','),
        source: sourceSelect ? sourceSelect.value : '',
        from: fromInput ? fromInput.value : '',
        to: toInput ? toInput.value : ''
    };
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(filters));
    } catch {
        // 略過 localStorage 寫入錯誤
    }
}

/** 初始化篩選條件（優先讀取 URL query string，次之讀取 localStorage） */
function initFiltersFromUrlOrStorage() {
    const params = new URLSearchParams(location.search);
    const hasUrlParams = params.has('q') || params.has('subnet') || params.has('category') ||
        params.has('source') || params.has('from') || params.has('to') ||
        params.has('status') || params.has('sort') || params.has('dir') ||
        params.has('page') || params.has('pageSize');

    if (hasUrlParams) {
        if (params.has('q') && keywordInput) keywordInput.value = params.get('q');
        if (params.has('subnet') && subnetInput) subnetInput.value = params.get('subnet');
        if (params.has('source') && sourceSelect) sourceSelect.value = params.get('source');
        if (params.has('from') && fromInput) fromInput.value = params.get('from');
        if (params.has('to') && toInput) toInput.value = params.get('to');
        if (params.has('category')) {
            for (const cat of params.get('category').split(',').filter(Boolean)) {
                selectedCategories.add(cat);
                if (!knownCategories.has(cat)) {
                    knownCategories.set(cat, cat);
                }
            }
        }
        if (params.has('status')) {
            currentStatus = params.get('status');
            for (const tab of document.querySelectorAll('#perm-tabs .nav-link')) {
                tab.classList.toggle('active', tab.dataset.status === currentStatus);
            }
        }
        if (params.has('sort')) {
            sort.key = params.get('sort');
            sort.dir = params.get('dir') === 'asc' ? 'asc' : 'desc';
        }
        if (params.has('page')) {
            currentPage = Math.max(1, parseInt(params.get('page'), 10) || 1);
        }
        if (params.has('pageSize')) {
            const ps = parseInt(params.get('pageSize'), 10);
            if (ps > 0) pageSize = ps;
        }
    } else {
        const stored = loadStoredFilters();
        if (stored) {
            if (stored.q && keywordInput) keywordInput.value = stored.q;
            if (stored.subnet && subnetInput) subnetInput.value = stored.subnet;
            if (stored.source && sourceSelect) sourceSelect.value = stored.source;
            if (stored.from && fromInput) fromInput.value = stored.from;
            if (stored.to && toInput) toInput.value = stored.to;
            if (stored.category) {
                for (const cat of stored.category.split(',').filter(Boolean)) {
                    selectedCategories.add(cat);
                    if (!knownCategories.has(cat)) {
                        knownCategories.set(cat, cat);
                    }
                }
            }
        }
    }
}

/** 渲染類別多選 chip（標籤與 key 完全來自後端 DTO） */
function renderCategoryChips() {
    if (!categoryChips) return;
    if (knownCategories.size === 0 && selectedCategories.size === 0) {
        if (categoryRow) categoryRow.classList.add('d-none');
        return;
    }
    if (categoryRow) categoryRow.classList.remove('d-none');

    const items = Array.from(knownCategories.entries()).map(([value, label]) => ({
        value,
        label
    }));

    renderChips(categoryChips, {
        items,
        attr: 'category',
        activeValues: [...selectedCategories],
        multi: true,
        onToggle: (value, active) => {
            if (active) selectedCategories.add(value);
            else selectedCategories.delete(value);
            currentPage = 1;
            load();
        }
    });
}

function clearSubnetError() {
    if (subnetInput) subnetInput.classList.remove('is-invalid');
    if (subnetFeedback) subnetFeedback.textContent = '';
}

function showSubnetError(message) {
    if (subnetInput) subnetInput.classList.add('is-invalid');
    if (subnetFeedback) subnetFeedback.textContent = message || '網段格式錯誤';
}

/**
 * 目前的篩選條件（供清單查詢與「全選符合條件」共用同一份組裝）。
 * 兩邊共用同一份組裝，避免清單與全選條件不一致。
 */
function buildQueryParams(includePaging = true) {
    const params = new URLSearchParams();

    const q = keywordInput ? keywordInput.value.trim() : '';
    if (q) params.set('q', q);

    const subnet = subnetInput ? subnetInput.value.trim() : '';
    if (subnet) params.set('subnet', subnet);

    if (selectedCategories.size > 0) {
        params.set('category', [...selectedCategories].join(','));
    }

    if (currentStatus) {
        params.set('status', currentStatus);
    }

    const source = sourceSelect ? sourceSelect.value : '';
    if (source) params.set('source', source);

    const from = fromInput ? fromInput.value : '';
    if (from) params.set('from', from);

    const to = toInput ? toInput.value : '';
    if (to) params.set('to', to);

    params.set('sort', sort.key || 'detectedAt');
    params.set('dir', sort.dir || 'desc');
    if (includePaging) {
        params.set('page', String(currentPage));
        params.set('pageSize', String(pageSize));
    }
    return params;
}

async function load() {
    clearSubnetError();
    saveStoredFilters();

    const container = document.getElementById('perm-list');
    renderLoading(container, 5);

    const params = buildQueryParams(true);
    history.replaceState(null, '', `?${params.toString()}`);

    try {
        lastResult = await api.get(`/api/permission-changes?${params.toString()}`, { silent: true });

        if (lastResult && Array.isArray(lastResult.items)) {
            for (const item of lastResult.items) {
                if (selectedChanges.has(item.changeId)) {
                    selectedChanges.set(item.changeId, item);
                }
            }
        }

        renderCategoryChips();
        render();
    } catch (err) {
        const isSubnetErr = subnetInput?.value.trim() && (err.code === 'validation_failed' || (err.message && err.message.includes('網段')));
        if (isSubnetErr) {
            showSubnetError(err.message);
        } else {
            toast(err.message || '查詢失敗', 'danger');
        }
        lastResult = { items: [], total: 0, page: currentPage, pageSize };
        render();
    }
}

/**
 * 全選符合目前篩選的待確認權限異動。
 * 伺服器端以同一組篩選條件解析出 changeIds 清單，跨頁選取不需使用者逐頁翻過去。
 */
async function selectAllMatching(button) {
    const restore = withBusy(button, '選取中');
    try {
        const params = buildQueryParams(false);
        const result = await api.get(`/api/permission-changes/ids?${params.toString()}`);

        for (const changeId of result.changeIds) {
            if (selectedChanges.has(changeId)) continue;
            const known = (lastResult?.items || []).find(c => c.changeId === changeId);
            selectedChanges.set(changeId, known ?? {
                changeId,
                hostName: '（其他）',
                categoryLabel: '其他',
                target: ''
            });
        }

        if (result.truncated) {
            toast(`符合 ${result.total} 筆，本次只能選取 ${result.changeIds.length} 筆，請分批處理。`, 'warning', 8000);
        } else {
            toast(`已選取符合目前篩選的 ${result.changeIds.length} 筆權限異動。`, 'success');
        }
        render();
    } finally {
        restore();
    }
}

function render() {
    const total = lastResult.total;
    const countEl = document.getElementById('perm-count');
    if (countEl) {
        countEl.textContent = total > 0 ? `共 ${total} 筆` : '';
    }

    isAllExpanded = false;
    updateExpandButtonState();

    renderTable(document.getElementById('perm-list'), {
        columns: [
            {
                key: 'detectedAt',
                title: '時間',
                sortKey: 'detectedAt',
                sortDefaultDir: 'desc',
                renderHeader: () => headerWithHelp('時間', '本機監控來源的時間語意為系統偵測比對時間，非事件實際發生時間。', '時間說明'),
                render: row => formatDateTime(row.detectedAt)
            },
            // 選取欄刻意不排第一欄（WEB-SPEC §8.6 第 6 條）：renderTable 的展開箭頭
            // 固定插在第一欄，排第一欄會與 checkbox 擠在同一格
            {
                title: '',
                className: 'lf-checkbox-cell',
                renderHeader: selectAllHeaderCheckbox,
                render: rowCheckboxCell
            },
            {
                key: 'hostName',
                title: '主機 (IP)',
                sortKey: 'hostName',
                sortDefaultDir: 'asc',
                render: row => hostCell(row)
            },
            {
                title: '帳號',
                render: row => accountCell(row)
            },
            {
                key: 'category',
                title: '類別',
                sortKey: 'category',
                sortDefaultDir: 'asc',
                render: row => categoryCell(row)
            },
            {
                title: '異動說明',
                render: row => summaryCell(row)
            },
            {
                key: 'status',
                title: '狀態',
                sortKey: 'status',
                sortDefaultDir: 'asc',
                render: row => statusBadge(row.status)
            }
        ],
        rows: lastResult.items,
        rowDetail: row => detailView(row),
        sort,
        onSort: (key, dir) => {
            sort = { key, dir };
            currentPage = 1;
            load();
        },
        empty: {
            title: currentStatus === 'pending' ? '沒有待確認的權限異動' : '沒有符合條件的紀錄',
            hint: currentStatus === 'pending'
                ? '批次執行時若偵測到 ACL 或群組成員異動，會出現在這裡等待逐筆確認。'
                : '請切換狀態頁籤以檢視其他紀錄。'
        }
    });

    renderPager();
    renderSelectionBar();
}

/** 勾選框只出現在 status 為 pending 的列（已確認的不能改回） */
function isSelectable(change) {
    return change.status === 'pending';
}

function rowCheckboxCell(change) {
    if (!isSelectable(change)) return document.createTextNode('');

    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input';
    check.checked = selectedChanges.has(change.changeId);
    check.title = '選取此項目';

    check.addEventListener('click', event => event.stopPropagation());
    check.addEventListener('change', () => {
        if (check.checked) selectedChanges.set(change.changeId, change);
        else selectedChanges.delete(change.changeId);
        renderSelectionBar();
        syncSelectAllHeaderCheckbox();
    });

    return check;
}

/** 表頭全選：只作用在目前這一頁可勾選的列 */
function selectAllHeaderCheckbox() {
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input';
    check.title = '全選本頁待確認項目';

    check.addEventListener('click', event => event.stopPropagation());
    check.addEventListener('change', () => {
        const selectable = (lastResult?.items || []).filter(isSelectable);
        if (check.checked) {
            for (const change of selectable) selectedChanges.set(change.changeId, change);
        } else {
            for (const change of selectable) selectedChanges.delete(change.changeId);
        }
        render();
    });

    headerCheckboxEl = check;
    syncSelectAllHeaderCheckbox();
    return check;
}

/** 本頁可勾選列是否已全部勾選，維持三態同步 */
function syncSelectAllHeaderCheckbox() {
    if (!headerCheckboxEl) return;
    const selectable = (lastResult?.items || []).filter(isSelectable);
    headerCheckboxEl.checked = selectable.length > 0 && selectable.every(c => selectedChanges.has(c.changeId));
    headerCheckboxEl.indeterminate = !headerCheckboxEl.checked && selectable.some(c => selectedChanges.has(c.changeId));
}

function renderSelectionBar() {
    const bar = document.getElementById('perm-selection-bar');
    if (!bar) return;
    bar.classList.toggle('d-none', selectedChanges.size === 0);
    const countEl = document.getElementById('perm-selection-count');
    if (countEl) {
        countEl.textContent = `已選 ${selectedChanges.size} 筆`;
    }
}

function hostCell(row) {
    const wrap = document.createElement('div');
    const hostName = document.createElement('div');
    hostName.className = 'fw-semibold';
    hostName.textContent = row.hostName;
    wrap.appendChild(hostName);

    if (row.hostIp) {
        const ip = document.createElement('div');
        ip.className = 'text-muted small';
        ip.textContent = row.hostIp;
        wrap.appendChild(ip);
    }

    return wrap;
}

function accountCell(row) {
    const wrap = document.createElement('div');
    wrap.className = 'small';

    // 顯示短名（完整 DN 留在 title 與展開明細）：AD 的帳號值動輒是
    // CN=…,OU=…,DC=… 整串，直接印會把這一欄撐開、也蓋不住重點
    const line = (labelText, display, full) => {
        const rowEl = document.createElement('div');
        rowEl.className = 'text-truncate';
        const label = document.createElement('span');
        label.className = 'text-muted me-1';
        label.textContent = labelText;
        const val = document.createElement('span');
        val.textContent = display || full || '—';
        if (full) rowEl.title = full;
        rowEl.append(label, val);
        return rowEl;
    };

    wrap.append(
        line('操作者：', row.initiatorAccountDisplay, row.initiatorAccount),
        line('目標：', row.targetAccountDisplay, row.targetAccount));
    return wrap;
}

function categoryCell(row) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex align-items-center gap-1 flex-wrap';

    const catBadge = document.createElement('span');
    catBadge.className = 'lf-badge lf-badge--secondary';
    catBadge.textContent = row.categoryLabel || row.category || '其他';
    wrap.appendChild(catBadge);

    if (row.isPrivilegedTarget) {
        const privBadge = document.createElement('span');
        privBadge.className = 'lf-badge lf-badge--danger';
        privBadge.textContent = '高風險';
        wrap.appendChild(privBadge);
    }

    return wrap;
}

function summaryCell(row) {
    // 沿用問題查詢頁的白話說明範式（.lf-issue-explanation）：同一套寬度與省略號規則，
    // tabIndex 讓鍵盤使用者也叫得出完整句子的 tooltip
    const div = document.createElement('div');
    div.className = 'lf-issue-explanation lf-issue-explanation--primary';
    div.textContent = row.summaryText || '—';
    if (row.summaryText) {
        div.title = row.summaryText;
        div.tabIndex = 0;
    }
    return div;
}

function statusBadge(status) {
    const meta = {
        pending: { text: '待確認', variant: 'warning' },
        authorized: { text: '已確認授權', variant: 'success' },
        suspicious: { text: '可疑', variant: 'danger' }
    }[status] ?? { text: status || '—', variant: 'secondary' };

    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

/**
 * 解析單行文字為結構化項目（區段標題、key/value 對、或原樣單欄）。
 * key 判定規則：出現在行首或空白之後、長度 2～20 字元、至少包含一個字母或中日韓文字、不含冒號與換行、緊接半形或全形冒號。
 * 冒號後內容為空（緊接下一個 key 或行尾）者視為區段標題。
 *
 * @param {string} line
 * @returns {Array<{type: 'section'|'kv'|'asis', title?: string, key?: string, value?: string, text?: string, colonChar?: string}>}
 */
function parseStructuredLine(line) {
    if (!line) {
        return [{ type: 'asis', text: '' }];
    }

    // 尋找行內所有的冒號位置（半形 : 或全形 ：）
    const colonIndices = [];
    for (let i = 0; i < line.length; i++) {
        if (line[i] === ':' || line[i] === '：') {
            colonIndices.push(i);
        }
    }

    if (colonIndices.length === 0) {
        return [{ type: 'asis', text: line }];
    }

    const matches = [];
    let lastMatchEnd = 0;

    for (let i = 0; i < colonIndices.length; i++) {
        const colonIdx = colonIndices[i];
        if (colonIdx < lastMatchEnd) continue;

        // key 契約：長度 2～20 字元、不含冒號、且必須至少包含一個字母（A-Z／a-z）或中日韓文字
        const isValidKey = (key) =>
            key.length >= 2 && key.length <= 20 &&
            /[\p{L}]/u.test(key) &&
            !key.includes(':') && !key.includes('：');

        // 多字 key（Security ID／Object Name）只在「該行第一個配對」時嘗試整段當候選：
        // Windows 多行原文的 key 都在行首（縮排後），整段除了 key 沒有別的；行內後續配對
        // 的冒號前必然帶著上一對的值（…識別碼: S-1-5-21-1 帳戶名稱:…），整段當 key 會把
        // 值吞進 key 裡，只能取「最後一個空白之後」的單詞。
        let candidateStart = -1;
        const segment = line.slice(lastMatchEnd, colonIdx);
        const firstNonSpace = segment.search(/\S/);
        if (lastMatchEnd === 0 && firstNonSpace !== -1 && isValidKey(segment.slice(firstNonSpace).trimEnd())) {
            candidateStart = firstNonSpace;
        } else {
            for (let k = colonIdx - 1; k >= lastMatchEnd; k--) {
                if (/\s/.test(line[k])) {
                    candidateStart = k + 1;
                    break;
                }
            }
            // 回掃不到空白時只有行首算數：否則像 AAA:BBB:CCC 這種值本身含冒號的字串，
            // BBB 會被當成 key（它既不在行首也不在空白之後），把 AAA 的值整個吃掉
            if (candidateStart === -1) {
                if (lastMatchEnd !== 0) continue;
                candidateStart = 0;
            }
        }

        const candidateKey = line.slice(candidateStart, colonIdx).trimEnd();
        if (isValidKey(candidateKey)) {
            matches.push({
                keyStart: candidateStart,
                colonIdx: colonIdx,
                key: candidateKey,
                colonChar: line[colonIdx]
            });
            lastMatchEnd = colonIdx + 1;
        }
    }

    if (matches.length === 0) {
        return [{ type: 'asis', text: line }];
    }

    const items = [];
    const leadingText = line.slice(0, matches[0].keyStart).trim();
    if (leadingText) {
        items.push({ type: 'asis', text: leadingText });
    }

    for (let i = 0; i < matches.length; i++) {
        const m = matches[i];
        const nextStart = (i + 1 < matches.length) ? matches[i + 1].keyStart : line.length;
        const valRaw = line.slice(m.colonIdx + 1, nextStart);
        const valTrimmed = valRaw.trim();

        if (valTrimmed === '') {
            items.push({
                type: 'section',
                title: m.key,
                colonChar: m.colonChar
            });
        } else {
            items.push({
                type: 'kv',
                key: m.key,
                value: valTrimmed
            });
        }
    }

    return items;
}

/**
 * 通用權限異動明細文字渲染函式（行為說明／異動前／異動後共用）。
 * 具備保留換行、通用 key/value 拆解、區段標題與少於 2 對時退回純文字機制。
 *
 * @param {string|null|undefined} rawText
 * @param {string} emptyFallback 空值時的預設文字（例如「—」或「（無）」）
 * @param {boolean} monoPlain 退回純文字時是否用等寬字（異動前／異動後的單串值需要）
 * @returns {HTMLElement}
 */
function renderStructuredDetail(rawText, emptyFallback = '—', monoPlain = false) {
    const text = (rawText != null) ? String(rawText).trim() : '';
    if (!text) {
        const emptyEl = document.createElement('span');
        emptyEl.className = 'text-muted';
        emptyEl.textContent = emptyFallback;
        return emptyEl;
    }

    const lines = String(rawText).split(/\r\n|\r|\n/);
    const parsedLines = lines.map(parseStructuredLine);

    // 只數 key/value 對：區段標題不算，否則整段只有「主體:」這種標題時
    // 會渲染出一張沒有任何欄位的空表格
    let totalKvCount = 0;
    for (const lineItems of parsedLines) {
        for (const item of lineItems) {
            if (item.type === 'kv') {
                totalKvCount++;
            }
        }
    }

    // 少於 2 對時，整段退回純文字（僅套用保留換行與自動折行）
    if (totalKvCount < 2) {
        const plainWrap = document.createElement('div');
        plainWrap.className = monoPlain ? 'lf-detail-plain font-monospace' : 'lf-detail-plain';
        plainWrap.textContent = String(rawText);
        return plainWrap;
    }

    // 解析成功：以雙欄表格逐列呈現
    const table = document.createElement('table');
    table.className = 'lf-kv-table table table-sm mb-0';

    const tbody = document.createElement('tbody');
    for (const lineItems of parsedLines) {
        for (const item of lineItems) {
            // 原文區段之間的空行不渲染：每列有底線，空列會變成一條條無內容的分隔線
            if (item.type === 'asis' && !(item.text || '').trim()) continue;
            const tr = document.createElement('tr');
            if (item.type === 'section') {
                tr.className = 'lf-kv-row--section';
                const th = document.createElement('th');
                th.colSpan = 2;
                th.className = 'lf-kv-section-header';
                th.textContent = item.title ? `${item.title}${item.colonChar || '：'}` : '';
                tr.appendChild(th);
            } else if (item.type === 'asis') {
                tr.className = 'lf-kv-row--asis';
                const td = document.createElement('td');
                td.colSpan = 2;
                td.className = 'lf-kv-asis-cell';
                td.textContent = item.text || '';
                tr.appendChild(td);
            } else if (item.type === 'kv') {
                tr.className = 'lf-kv-row--kv';
                const tdKey = document.createElement('td');
                tdKey.className = 'lf-kv-key small';
                tdKey.textContent = item.key;

                const tdVal = document.createElement('td');
                tdVal.className = 'lf-kv-val small';
                tdVal.textContent = item.value;

                tr.append(tdKey, tdVal);
            }
            tbody.appendChild(tr);
        }
    }

    table.appendChild(tbody);
    return table;
}

function detailView(change) {
    const wrap = document.createElement('div');
    wrap.className = 'p-3 bg-light rounded border';

    const metaGrid = document.createElement('div');
    metaGrid.className = 'row g-2 mb-3 small';

    const colAlert = document.createElement('div');
    colAlert.className = 'col-12';
    const alertLabel = document.createElement('div');
    alertLabel.className = 'text-muted mb-1';
    alertLabel.textContent = '行為說明：';
    const alertVal = document.createElement('div');
    alertVal.appendChild(renderStructuredDetail(change.alertText, '—'));
    colAlert.append(alertLabel, alertVal);

    const colTarget = document.createElement('div');
    colTarget.className = 'col-md-6';
    const targetLabel = document.createElement('span');
    targetLabel.className = 'text-muted me-2';
    targetLabel.textContent = '對象：';
    const targetVal = document.createElement('span');
    targetVal.className = 'font-monospace';
    targetVal.textContent = change.target || '—';
    colTarget.append(targetLabel, targetVal);

    const colSource = document.createElement('div');
    colSource.className = 'col-md-3';
    const sourceLabel = document.createElement('span');
    sourceLabel.className = 'text-muted me-2';
    sourceLabel.textContent = '來源：';
    const sourceVal = document.createElement('span');
    sourceVal.textContent = change.source || '本機監控';
    colSource.append(sourceLabel, sourceVal);

    const colEventId = document.createElement('div');
    colEventId.className = 'col-md-3';
    const eventIdLabel = document.createElement('span');
    eventIdLabel.className = 'text-muted me-2';
    eventIdLabel.textContent = 'EventId：';
    const eventIdVal = document.createElement('span');
    eventIdVal.className = 'font-monospace';
    eventIdVal.textContent = change.eventId != null ? String(change.eventId) : '—';
    colEventId.append(eventIdLabel, eventIdVal);

    metaGrid.append(colAlert, colTarget, colSource, colEventId);
    wrap.appendChild(metaGrid);

    const diffWrap = document.createElement('div');
    diffWrap.className = 'lf-table-wrap mb-3';
    diffWrap.innerHTML = `
        <table class="table table-sm bg-white mb-0 border">
            <tbody>
                <tr><th style="width:6rem" class="bg-light text-muted align-top">異動前</th><td class="small"></td></tr>
                <tr><th class="bg-light text-muted align-top">異動後</th><td class="small"></td></tr>
            </tbody>
        </table>`;
    const cells = diffWrap.querySelectorAll('td');
    cells[0].appendChild(renderStructuredDetail(change.before, '（無）', true));
    cells[1].appendChild(renderStructuredDetail(change.after, '（無）', true));
    wrap.appendChild(diffWrap);

    if (change.status === 'pending') {
        const actionArea = document.createElement('div');
        actionArea.className = 'd-flex align-items-center justify-content-between flex-wrap gap-2 pt-2 border-top';

        const prompt = document.createElement('div');
        prompt.className = 'small text-muted';
        prompt.textContent = '此異動是否為您或授權人員的操作？若否，可能為入侵或誤設定，建議立即調查。';

        const actions = document.createElement('div');
        actions.className = 'd-flex gap-2';

        const authorized = document.createElement('button');
        authorized.type = 'button';
        authorized.className = 'btn btn-sm btn-outline-success';
        authorized.textContent = '確認為授權操作';
        authorized.addEventListener('click', () => openConfirm(change, 'authorized'));

        const suspicious = document.createElement('button');
        suspicious.type = 'button';
        suspicious.className = 'btn btn-sm btn-outline-danger';
        suspicious.textContent = '標記可疑';
        suspicious.addEventListener('click', () => openConfirm(change, 'suspicious'));

        actions.append(authorized, suspicious);
        actionArea.append(prompt, actions);
        wrap.appendChild(actionArea);
    } else {
        const confirmedArea = document.createElement('div');
        confirmedArea.className = 'small text-muted pt-2 border-top row g-2';

        const resultCol = document.createElement('div');
        resultCol.className = 'col-md-3';
        const resLabel = document.createElement('span');
        resLabel.className = 'text-muted me-1';
        resLabel.textContent = '確認結果：';
        const resVal = document.createElement('span');
        resVal.className = 'fw-medium';
        resVal.textContent = change.status === 'authorized' ? '已確認授權' : '標記為可疑';
        resultCol.append(resLabel, resVal);

        const userCol = document.createElement('div');
        userCol.className = 'col-md-3';
        const uLabel = document.createElement('span');
        uLabel.className = 'text-muted me-1';
        uLabel.textContent = '確認人：';
        const uVal = document.createElement('span');
        uVal.textContent = formatUserName(change.confirmedByDisplayName, change.confirmedByAccount) || '—';
        userCol.append(uLabel, uVal);

        const timeCol = document.createElement('div');
        timeCol.className = 'col-md-3';
        const tLabel = document.createElement('span');
        tLabel.className = 'text-muted me-1';
        tLabel.textContent = '確認時間：';
        const tVal = document.createElement('span');
        tVal.textContent = change.confirmedAt ? formatDateTime(change.confirmedAt) : '—';
        timeCol.append(tLabel, tVal);

        const noteCol = document.createElement('div');
        noteCol.className = 'col-md-3';
        const nLabel = document.createElement('span');
        nLabel.className = 'text-muted me-1';
        nLabel.textContent = '確認說明：';
        const nVal = document.createElement('span');
        nVal.textContent = change.confirmNote || '—';
        noteCol.append(nLabel, nVal);

        confirmedArea.append(resultCol, userCol, timeCol, noteCol);
        wrap.appendChild(confirmedArea);
    }

    return wrap;
}

function renderPager() {
    const totalPages = Math.ceil(lastResult.total / lastResult.pageSize);
    renderPagination(document.getElementById('perm-pager'), {
        page: lastResult.page,
        totalPages,
        onPage: page => {
            currentPage = page;
            load();
            window.scrollTo({ top: 0, behavior: 'smooth' });
        },
        pageSize,
        onPageSize: size => {
            pageSize = size;
            savePageSize('permissionChanges', size);
            currentPage = 1;
            load();
        }
    });
}

function updateExpandButtonState() {
    const btn = document.getElementById('btn-toggle-expand');
    if (!btn) return;
    const hasRows = lastResult && lastResult.items && lastResult.items.length > 0;
    btn.disabled = !hasRows;
    btn.textContent = isAllExpanded ? '全部收合' : '全部展開';
}

document.getElementById('btn-toggle-expand')?.addEventListener('click', () => {
    const container = document.getElementById('perm-list');
    const rows = container.querySelectorAll('tbody tr.lf-row-expandable');
    if (rows.length === 0) return;
    const hasCollapsed = Array.from(rows).some(r => r.getAttribute('aria-expanded') !== 'true');
    isAllExpanded = toggleAllTableDetails(container, hasCollapsed);
    updateExpandButtonState();
});

document.getElementById('perm-list')?.addEventListener('click', e => {
    if (e.target.closest('tr.lf-row-expandable')) {
        setTimeout(() => {
            const rows = document.querySelectorAll('#perm-list tbody tr.lf-row-expandable');
            if (rows.length > 0) {
                isAllExpanded = Array.from(rows).every(r => r.getAttribute('aria-expanded') === 'true');
                updateExpandButtonState();
            }
        }, 0);
    }
});

function openConfirm(change, targetStatus) {
    pendingConfirm = { change, targetStatus };

    document.getElementById('confirm-modal-title').textContent =
        targetStatus === 'authorized' ? '確認為授權操作' : '標記為可疑';

    const detail = document.getElementById('confirm-detail');
    detail.replaceChildren();
    const summary = document.createElement('div');
    summary.className = 'alert alert-light border mb-0 small';
    // 對象剖不出時是空字串，濾掉才不會留一個沒有下文的分隔線
    summary.textContent = [change.hostName, change.changeType, change.target]
        .filter(Boolean).join('｜');
    detail.appendChild(summary);

    const note = document.getElementById('confirm-note');
    note.value = '';
    note.required = targetStatus === 'suspicious';

    // 標記可疑必須說明——那是要交給別人接手調查的訊號
    document.getElementById('confirm-note-hint').textContent = targetStatus === 'suspicious'
        ? '必填：請說明可疑之處，供後續調查參考。'
        : '選填：可記錄這是誰在什麼情況下做的變更。';

    const submit = document.getElementById('confirm-submit');
    submit.className = `btn btn-${targetStatus === 'authorized' ? 'success' : 'danger'}`;
    submit.textContent = targetStatus === 'authorized' ? '確認為授權操作' : '標記可疑';

    modal.show();
}

confirmForm.addEventListener('submit', async event => {
    event.preventDefault();

    const note = document.getElementById('confirm-note').value.trim();
    if (pendingConfirm.targetStatus === 'suspicious' && !note) {
        toast('標記為可疑時請填寫說明', 'warning');
        return;
    }

    const submit = document.getElementById('confirm-submit');
    const restore = withBusy(submit, '處理中');

    try {
        await api.put(`/api/permission-changes/${pendingConfirm.change.changeId}/confirm`, {
            status: pendingConfirm.targetStatus,
            note: note || null
        });

        selectedChanges.delete(pendingConfirm.change.changeId);

        toast(pendingConfirm.targetStatus === 'authorized' ? '已確認為授權操作' : '已標記為可疑', 'success');
        modal.hide();
        await load();
    } catch {
        restore();
    }
});

/**
 * 批次確認 modal 預覽：依類別與主機分組摘要，資料來自 selectedChanges 勾選當下已有的列物件，不重打 API。
 */
function openBatchConfirmModal(targetStatus) {
    if (selectedChanges.size === 0) {
        toast('請先勾選要處理的權限異動', 'warning');
        return;
    }

    batchTargetStatus = targetStatus;
    const isSuspicious = targetStatus === 'suspicious';
    const actionText = isSuspicious ? '標記為可疑' : '標記為授權操作';
    const statusText = isSuspicious ? '可疑' : '授權操作';

    document.getElementById('batch-confirm-modal-title').textContent = `批次${actionText}`;
    document.getElementById('batch-confirm-summary-text').textContent =
        `將把 ${selectedChanges.size} 筆標記為${statusText}`;

    // 依類別與主機分組摘要
    const categoryCounts = new Map();
    const hostCounts = new Map();
    for (const change of selectedChanges.values()) {
        const cat = change.categoryLabel || change.category || '其他';
        categoryCounts.set(cat, (categoryCounts.get(cat) || 0) + 1);

        const host = change.hostName || '（未指定主機）';
        hostCounts.set(host, (hostCounts.get(host) || 0) + 1);
    }

    const catContainer = document.getElementById('batch-confirm-categories');
    catContainer.replaceChildren();
    for (const [cat, count] of categoryCounts.entries()) {
        const item = document.createElement('div');
        item.className = 'd-flex justify-content-between align-items-center small py-1 border-bottom';
        const name = document.createElement('span');
        name.textContent = cat;
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = `${count} 筆`;
        item.append(name, badge);
        catContainer.appendChild(item);
    }

    const hostContainer = document.getElementById('batch-confirm-hosts');
    hostContainer.replaceChildren();
    for (const [host, count] of hostCounts.entries()) {
        const item = document.createElement('div');
        item.className = 'd-flex justify-content-between align-items-center small py-1 border-bottom';
        const name = document.createElement('span');
        name.className = 'font-monospace';
        name.textContent = host;
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = `${count} 筆`;
        item.append(name, badge);
        hostContainer.appendChild(item);
    }

    const noteEl = document.getElementById('batch-confirm-note');
    noteEl.value = '';
    noteEl.required = isSuspicious;

    const reqMark = document.getElementById('batch-confirm-note-required');
    if (reqMark) {
        reqMark.classList.toggle('d-none', !isSuspicious);
    }

    const hintEl = document.getElementById('batch-confirm-note-hint');
    hintEl.textContent = isSuspicious
        ? '必填：請說明可疑之處，供後續調查參考。'
        : '選填：可記錄這是誰在什麼情況下做的變更。';

    const submitBtn = document.getElementById('batch-confirm-submit');
    submitBtn.className = `btn btn-${isSuspicious ? 'danger' : 'success'}`;
    submitBtn.textContent = isSuspicious ? '標記可疑' : '確認為授權操作';

    batchConfirmModal.show();
}

/**
 * 顯示批次處理中被略過的項目與原因。
 */
function showSkippedModal(result) {
    const summary = document.getElementById('batch-skipped-summary');
    summary.textContent = `已處理 ${result.updatedCount} 筆，略過 ${result.skipped.length} 筆。略過明細如下：`;

    const list = document.getElementById('batch-skipped-list');
    list.replaceChildren();

    for (const item of result.skipped) {
        const row = document.createElement('div');
        row.className = 'd-flex align-items-center justify-content-between py-1 border-bottom small';

        const host = document.createElement('span');
        host.className = 'fw-medium text-truncate me-2';
        host.textContent = item.hostName || '（未指定主機）';

        const reason = document.createElement('span');
        reason.className = 'text-danger';
        reason.textContent = item.reason;

        row.append(host, reason);
        list.appendChild(row);
    }

    batchSkippedModal.show();
}

if (batchConfirmForm) {
    batchConfirmForm.addEventListener('submit', async event => {
        event.preventDefault();

        if (selectedChanges.size === 0) {
            toast('請至少勾選一筆權限異動', 'warning');
            return;
        }

        const note = document.getElementById('batch-confirm-note').value.trim();
        if (batchTargetStatus === 'suspicious' && !note) {
            toast('標記為可疑時請填寫說明', 'warning');
            return;
        }

        const submitBtn = document.getElementById('batch-confirm-submit');
        const restore = withBusy(submitBtn, '處理中');

        try {
            const result = await api.post('/api/permission-changes/confirm/batch', {
                changeIds: [...selectedChanges.keys()],
                status: batchTargetStatus,
                note: note || null
            });

            if (result.skipped && result.skipped.length > 0) {
                toast(`已處理 ${result.updatedCount} 筆，略過 ${result.skipped.length} 筆`, 'warning');
                showSkippedModal(result);
            } else {
                toast(`已處理 ${result.updatedCount} 筆`, 'success');
            }

            selectedChanges.clear();
            batchConfirmModal.hide();
            await load();
        } catch {
            // 錯誤已由 api.js 顯示
        } finally {
            restore();
        }
    });
}

function resetFilters() {
    if (keywordInput) keywordInput.value = '';
    if (subnetInput) subnetInput.value = '';
    if (sourceSelect) sourceSelect.value = '';
    if (fromInput) fromInput.value = '';
    if (toInput) toInput.value = '';
    selectedCategories.clear();
    selectedChanges.clear();   // 篩選換了，先前選取的項目多半已不在結果內
    clearSubnetError();
    renderCategoryChips();
    try {
        localStorage.removeItem(STORAGE_KEY);
    } catch {}
    currentPage = 1;
    load();
}

function onSearchInput() {
    clearSubnetError();
    clearTimeout(searchDebounce);
    searchDebounce = setTimeout(() => {
        currentPage = 1;
        load();
    }, 300);
}

if (filterForm) {
    filterForm.addEventListener('submit', event => {
        event.preventDefault();
        clearTimeout(searchDebounce);
        currentPage = 1;
        load();
    });
}

if (keywordInput) {
    keywordInput.addEventListener('input', onSearchInput);
}

if (subnetInput) {
    subnetInput.addEventListener('input', onSearchInput);
}

if (sourceSelect) {
    sourceSelect.addEventListener('change', () => {
        currentPage = 1;
        load();
    });
}

if (fromInput) {
    fromInput.addEventListener('change', () => {
        currentPage = 1;
        load();
    });
}

if (toInput) {
    toInput.addEventListener('change', () => {
        currentPage = 1;
        load();
    });
}

if (resetBtn) {
    resetBtn.addEventListener('click', resetFilters);
}

document.getElementById('btn-select-all-matching')
    ?.addEventListener('click', event => selectAllMatching(event.currentTarget));
document.getElementById('btn-batch-authorize')
    ?.addEventListener('click', () => openBatchConfirmModal('authorized'));
document.getElementById('btn-batch-suspicious')
    ?.addEventListener('click', () => openBatchConfirmModal('suspicious'));
document.getElementById('btn-clear-selection')
    ?.addEventListener('click', () => {
        selectedChanges.clear();
        render();
    });

initFiltersFromUrlOrStorage();
// 先取類別清單再渲染篩選晶片，避免第一次畫面上的類別篩選是空的
loadCategories().then(renderCategoryChips);
guardLoad(document.getElementById('perm-list'), load);
