/**
 * 交辦總覽頁（回饋第 47 輪）：以交辦單為單位管理——
 * 一張單＝一個問題 × 一批主機 × 一位處理人。
 * 包含三個頁籤：
 * 1. 進行中：追蹤每張交辦單的進度、成員數與逾期/回覆狀態。
 * 2. 負載看板：檢視各處理人的交辦單數、成員數與負載狀況。
 * 3. 待派：分三段——可以派（可整頁立即派工或單題派工，先預覽再確認）、派不出去（依原因分組，無人看得到的主機群組可就地授權）、
 *    不會派（預設收合）；下方「不再自動派工」清單預設收合、展開才載入。
 * 4. 靜音中（只有具 Maintain 時存在）：列出靜音中的問題，可延長或解除。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl, urlParamChoice } from '../core/paths.js';
import {
    renderTable,
    renderPagination,
    renderLoading,
    renderEmpty,
    bindTabs,
    button,
    linkButton,
    toast,
    confirmAction,
    confirmAndClearMute,
    withBusy,
    guardLoad,
    loadPageSize,
    savePageSize,
    showTab,
    createPageSelection
} from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, statusBadge, todayLocal, workOrderClosedReasonText, renderIssueCell, originBadge, hostCountText, workOrderSeverityCell, workOrderFlagBadges } from '../core/format.js';
import { openIssueMuteModal } from './issue-mute-modal.js';
import { roleText, filterDisclosure, statusNote, disclosure } from '../core/elements.js';
import { statusBadgeMeta } from '../core/handling-statuses.js';
import { compareText } from '../core/collation.js';
import { createSeqGuard } from '../core/async-guards.js';
import { createBulkReassignPanel, BULK_REASSIGN_MAX } from './work-orders-bulk.js';

// DOM 元素
const tabsEl = document.getElementById('wo-tabs');

// 管理者提醒區（頁籤列上方；三類都是 0 時不顯示）
const remindersContainer = document.getElementById('wo-reminders');

// 進行中頁籤
const activeStatusSelect = document.getElementById('wo-status');
const activeSortSelect = document.getElementById('wo-sort');
const activeGroupSelect = document.getElementById('wo-group');
// 處理人篩選：清單端點的 handlerId 參數；負載看板點人名、網址帶 handlerId 都會設定它
const activeHandlerSelect = document.getElementById('wo-handler');
const activeResumedCheckbox = document.getElementById('wo-resumed');
// 處理人離職或停用後，要逐單改派或代為結案——這個篩選把那些單一次找出來
const activeHandlerInactiveCheckbox = document.getElementById('wo-handler-inactive');
// 只看未讀回覆：處理人回覆後管理者尚未開過詳情的單（判定見 WorkOrder.HasUnreadReply）
const activeUnreadOnlyCheckbox = createUnreadOnlyCheckbox();
setupMoreFilters();
const activeListContainer = document.getElementById('wo-list');
const activePagerContainer = document.getElementById('wo-pager');
// 批次改派：勾選列（選取數與動作）、就地面板（不開 modal）、提示
const bulkBarActions = document.getElementById('wo-bulk-actions');
const bulkPanelContainer = document.getElementById('wo-bulk-panel');
const bulkHintEl = document.getElementById('wo-bulk-hint');

// 負載看板頁籤
const loadContainer = document.getElementById('wo-load');

// 待派頁籤
const gapsFromInput = document.getElementById('wo-gaps-from');
const gapsToInput = document.getElementById('wo-gaps-to');
const gapsQueryBtn = document.getElementById('wo-gaps-query');
const gapsAutoDispatchBtn = document.getElementById('wo-auto-dispatch');
const gapsNoteEl = document.getElementById('wo-gaps-note');
const gapsContainer = document.getElementById('wo-gaps');

// 待派頁籤下方的「不再自動派工」區（被「不再打擾」擋住的項目與恢復）
const dismissedCard = document.getElementById('wo-dismissed-card');
const dismissedCountEl = document.getElementById('wo-dismissed-count');
const dismissedContainer = document.getElementById('wo-dismissed');
const dismissedPagerContainer = document.getElementById('wo-dismissed-pager');
const dismissedBody = document.getElementById('wo-dismissed-body');
const dismissedToggleSlot = document.getElementById('wo-dismissed-toggle-slot');

// 靜音中頁籤
const mutedTabItem = document.getElementById('wo-muted-tab-item');
const mutedContainer = document.getElementById('wo-muted-list');

// 無法指派原因對照
const UNASSIGNABLE_LABELS = {
    no_pool: '沒有派工池成員',
    no_visibility: '派工池成員都看不到這些主機',
    all_paused: '候選人全部暫停接單'
};

// 已排除原因對照
const EXCLUDED_LABELS = {
    muted: '靜音中',
    gate_suppressed: '已抑制',
    gateSuppressed: '已抑制',
    gate_noise: '已知雜訊',
    gateNoise: '已知雜訊',
    gate_severity: '嚴重度未達',
    gateSeverity: '嚴重度未達',
    gate_dismissed: '不再打擾',
    gateDismissed: '不再打擾'
};

// URL 篩選參數（依問題／處理人篩選；群組只預選下拉）
const urlParams = new URLSearchParams(location.search);
const filterSource = urlParams.get('source');
const filterEventId = urlParams.get('eventId');
const filterHandlerId = urlParams.get('handlerId');
const initialGroupId = urlParams.get('groupId');
const hasUrlFilter = Boolean(filterSource || filterEventId || filterHandlerId);
/** 篩選橫幅的文字節點：處理人名稱要等清單回來才知道，所以留參照回填 */
let filterLabelSpan = null;

if (hasUrlFilter) {
    if (activeStatusSelect) {
        activeStatusSelect.value = 'all';
        // 連結可帶 status 指定狀態（例如停用處理人後的「查看進行中的單」帶 status=active）
        const urlStatus = urlParams.get('status');
        if (urlStatus && Array.from(activeStatusSelect.options ?? []).some(o => o.value === urlStatus)) {
            activeStatusSelect.value = urlStatus;
        }
    }
    const toolbar = activeStatusSelect?.closest('.d-flex');
    if (toolbar && toolbar.parentElement) {
        const hintRow = document.createElement('div');
        hintRow.className = 'd-flex align-items-center gap-2 mb-2 lf-text-meta';
        const labelSpan = document.createElement('span');
        filterLabelSpan = labelSpan;
        updateFilterLabel(null);
        const clearLink = document.createElement('a');
        clearLink.href = appUrl('/work-orders');
        clearLink.textContent = '清除篩選';
        hintRow.appendChild(labelSpan);
        hintRow.appendChild(clearLink);
        toolbar.parentElement.insertBefore(hintRow, toolbar);
    }
}

// 網址帶 sort 時優先於預設（最新建立）；不認得的值沿用預設
{
    const urlSort = activeSortSelect && urlParamChoice(location.search, 'sort', Array.from(activeSortSelect.options ?? []).map(o => o.value));
    if (urlSort) activeSortSelect.value = urlSort;
}

// 只帶 status（例如提醒區的「上報中」連結）：預選狀態下拉，其餘篩選維持預設
if (!hasUrlFilter && activeStatusSelect) {
    const urlStatus = urlParams.get('status');
    if (urlStatus && Array.from(activeStatusSelect.options ?? []).some(o => o.value === urlStatus)) {
        activeStatusSelect.value = urlStatus;
    }
}

// 連結帶 handlerInactive=1 就預先勾選「處理人離職或停用」篩選
if (activeHandlerInactiveCheckbox && urlParams.get('handlerInactive') === '1') {
    activeHandlerInactiveCheckbox.checked = true;
}

/** 在「只看處理人已停用或失去處理能力」的核取方塊之後補上「只看未讀回覆」；找不到錨點時退回附在工具列尾端 */
function createUnreadOnlyCheckbox() {
    const wrap = document.createElement('div');
    wrap.className = 'form-check mb-0';
    const input = document.createElement('input');
    input.className = 'form-check-input';
    input.type = 'checkbox';
    input.id = 'wo-unread-only';
    const label = document.createElement('label');
    label.className = 'form-check-label lf-text-meta';
    label.htmlFor = 'wo-unread-only';
    label.textContent = '只看未讀回覆';
    wrap.append(input, label);

    const anchor = activeHandlerInactiveCheckbox?.closest('.form-check');
    if (anchor?.parentElement) anchor.after(wrap);
    else activeStatusSelect?.closest('.d-flex')?.appendChild(wrap);
    return input;
}

/** 三個「只看…」是次要條件：收進「更多條件」，標籤帶已勾選數 */
function setupMoreFilters() {
    const more = document.getElementById('wo-more');
    if (!more) return;
    const boxes = [activeResumedCheckbox, activeHandlerInactiveCheckbox, activeUnreadOnlyCheckbox].filter(Boolean);
    const { button, refresh } = filterDisclosure(more, () => boxes.filter(box => box.checked).length);
    more.before(button);
    boxes.forEach(box => box.addEventListener('change', refresh));
}

/** 篩選橫幅：「篩選：{source} {eventId}｜處理人 {名稱}」；名稱未知時用「處理人 #{id}」 */
function updateFilterLabel(handlerName) {
    if (!filterLabelSpan) return;
    const parts = [filterSource, filterEventId].filter(x => x !== null && x !== '');
    let text = `篩選：${parts.join(' ')}`;
    const handlerValue = currentHandlerValue();
    if (handlerValue) {
        const handlerText = handlerName ? `處理人 ${handlerName}` : `處理人 #${handlerValue}`;
        text += parts.length > 0 ? `｜${handlerText}` : handlerText;
    }
    filterLabelSpan.textContent = text;
}

/** 群組下拉選項（回饋第 47 輪定案 48）：失敗靜默，下拉只剩「全部」 */
async function loadHandlerGroups() {
    let groups = [];
    try {
        groups = await api.get('/api/work-orders/handler-groups', { silent: true });
    } catch {
        return;
    }
    for (const g of groups ?? []) {
        const opt = document.createElement('option');
        opt.value = String(g.groupId);
        opt.textContent = g.dispatchPool ? `${g.groupName}（派工池）` : g.groupName;
        activeGroupSelect.appendChild(opt);
    }
    if (initialGroupId && [...activeGroupSelect.options].some(o => o.value === initialGroupId)) {
        activeGroupSelect.value = initialGroupId;
    }
}

// 狀態追蹤
let currentUser = null;
const loadedTabs = new Set();
let activePage = 1;
let dismissedPage = 1;
/** 具 Assign 才有勾選與批次改派（ViewAll 只能看） */
let canAssign = false;
/** 目前這一頁的進行中清單（勾選全選只看本頁可勾選的列） */
let activeItems = [];
/** 從負載看板或停用提示進來的批次改派：清單載入後若全部放得進一頁，就先全部勾好，使用者只要按「改派給…」 */
let pendingAutoSelect = false;

/** 進行中清單的勾選（workOrderId → 列）：跨頁保留，改篩選或套用後清除 */
const selection = createPageSelection({
    keyOf: item => item.workOrderId,
    isSelectable: item => !item.closedAt,
    getPageItems: () => activeItems,
    labels: { row: '選取這張交辦單', all: '全選本頁進行中的交辦單' },
    bar: { barId: 'wo-bulk-bar', countId: 'wo-bulk-count', unit: '張' },
    onSelectAll: () => renderActiveRows(),
    onChange: () => updateBulkBar()
});

const bulkPanel = createBulkReassignPanel({
    container: bulkPanelContainer,
    getSelectedIds: () => [...selection.selected.keys()],
    onApplied: () => {
        selection.clear();
        loadedTabs.delete('load');
        guardLoad(activeListContainer, loadActive);
    }
});

/** 勾選列上的動作：「改派給…」（超過上限停用並說明）與「清除選取」 */
function updateBulkBar() {
    if (!bulkBarActions) return;
    const count = selection.selected.size;
    bulkBarActions.replaceChildren();
    if (count === 0) return;
    const over = count > BULK_REASSIGN_MAX;
    bulkBarActions.append(
        button('改派給…', { variant: 'primary', disabled: over, onClick: () => bulkPanel.open() }),
        button('清除選取', { onClick: () => selection.clear() })
    );
    if (over) bulkBarActions.appendChild(roleText('lf-text-meta', `一次最多改派 ${BULK_REASSIGN_MAX} 張，請取消部分勾選`));
}

/** 目前處理人篩選的值（空字串＝不限） */
function currentHandlerValue() {
    return activeHandlerSelect ? activeHandlerSelect.value : '';
}

/** 設定處理人篩選；下拉裡沒有這個人（例如網址帶了已不在負載看板的人）就補一個選項，不靜默退回「全部」 */
function setHandlerFilterValue(userId, name) {
    if (!activeHandlerSelect) return;
    const value = String(userId ?? '');
    if (value && !Array.from(activeHandlerSelect.options ?? []).some(o => o.value === value)) {
        const opt = document.createElement('option');
        opt.value = value;
        opt.textContent = name || `處理人 #${value}`;
        activeHandlerSelect.appendChild(opt);
    }
    activeHandlerSelect.value = value;
}

/**
 * 以某位處理人篩選進行中清單（負載看板點人名、「改派他名下的單…」共用同一個入口）：
 * 回到第 1 頁、清掉勾選、切到「進行中」頁籤並重查。autoSelect＝清單載入後先全部勾好（放得進一頁時）。
 */
function applyHandlerFilter(userId, { name, autoSelect = false } = {}) {
    setHandlerFilterValue(userId, name);
    if (activeStatusSelect) activeStatusSelect.value = 'active';
    activePage = 1;
    selection.clear();
    bulkPanel.close();
    if (bulkHintEl) bulkHintEl.textContent = '';
    pendingAutoSelect = autoSelect;
    loadedTabs.add('active'); // 避免頁籤切換回呼再重查一次；下面明確載入
    showTab('wo-tabs', 'active');
    guardLoad(activeListContainer, loadActive);
}

/** 清單載入後的「預先全選」：只在全部放得進本頁、且不超過批次上限時做，否則交給使用者用表頭全選或調整每頁筆數 */
function consumeAutoSelect(data) {
    if (!pendingAutoSelect) return;
    pendingAutoSelect = false;
    if (!canAssign) return;
    const selectable = (data.items || []).filter(item => !item.closedAt);
    if (data.total > data.items.length || selectable.length === 0 || selectable.length > BULK_REASSIGN_MAX) {
        if (bulkHintEl) bulkHintEl.textContent = '名下的單超過一頁：請用表頭的勾選框全選本頁，或調高每頁筆數後再勾選，然後按「改派給…」。';
        return;
    }
    for (const item of selectable) selection.selected.set(item.workOrderId, item);
    if (bulkHintEl) bulkHintEl.textContent = '已勾選這位處理人名下進行中的單；按「改派給…」選新處理人並預覽。';
}

/** 負載看板與處理人下拉共用的選項來源（靜默失敗：下拉只剩「全部」） */
async function loadHandlerOptions() {
    if (!activeHandlerSelect) return;
    let data = null;
    try {
        data = await api.get('/api/work-orders/load-board', { silent: true });
    } catch {
        return;
    }
    const rows = (data?.rows || []).slice().sort((a, b) => compareText(a.name || '', b.name || ''));
    for (const row of rows) {
        const opt = document.createElement('option');
        opt.value = String(row.userId);
        opt.textContent = row.active === false ? `${row.name}（已停用）` : row.name;
        activeHandlerSelect.appendChild(opt);
    }
}

// ── 進行中頁籤 ─────────────────────────────────────────────────────────────

/** 進行中清單的請求序號：篩選連續改動時，慢回來的舊請求不可蓋掉新篩選的結果 */
const activeGuard = createSeqGuard();

async function loadActive() {
    const seq = activeGuard.next();
    renderLoading(activeListContainer, 5);

    const status = activeStatusSelect.value;
    const sort = activeSortSelect.value;
    const resumed = activeResumedCheckbox.checked;
    const pageSize = loadPageSize('work-orders');

    const params = new URLSearchParams({
        status,
        sort,
        page: String(activePage),
        pageSize: String(pageSize),
        resumedFromMute: String(resumed),
        handlerInactive: String(activeHandlerInactiveCheckbox.checked),
        unreadOnly: String(activeUnreadOnlyCheckbox.checked)
    });

    if (hasUrlFilter) {
        if (filterSource) params.set('source', filterSource);
        if (filterEventId) params.set('eventId', filterEventId);
    }
    const handlerValue = currentHandlerValue();
    if (handlerValue) params.set('handlerId', handlerValue);
    if (activeGroupSelect.value) params.set('groupId', activeGroupSelect.value);

    const data = await api.get(`/api/work-orders?${params}`);
    if (!activeGuard.isCurrent(seq)) return;
    if (hasUrlFilter && currentHandlerValue()) {
        updateFilterLabel(data?.items?.length > 0 ? data.items[0].handlerName : null);
    }
    renderActiveTable(data);
    loadedTabs.add('active');
}

/** 續掛已停止（夜間派工擋下了續掛）：只有進行中且後端標了 continuationStopped 的單才有；原因用後端給的顯示名稱 */
function continuationStoppedBadge(r) {
    if (!r.continuationStopped) return null;
    const label = r.continuationStoppedLabel || r.continuationStoppedReason || '';
    return statusBadge(`續掛已停止：${label}`, 'warning', { title: '夜間派工不再把新主機續掛進這張單；處理人恢復資格後，之後新出現的主機照常續掛。' });
}

function renderActiveTable(data) {
    activeItems = data.items || [];
    consumeAutoSelect(data);
    selection.renderBar();
    renderActiveRows(data);
    loadedTabs.add('active');
}

/** 目前頁的清單資料（全選重繪時沿用，不重打 API） */
let lastActiveData = null;

function renderActiveRows(data = lastActiveData) {
    if (!data) return;
    lastActiveData = data;
    const columns = [];
    if (canAssign) {
        columns.push({
            title: '',
            className: 'text-center text-nowrap',
            renderHeader: () => selection.selectAllHeaderCheckbox(),
            render: r => selection.rowCheckboxCell(r)
        });
    }
    columns.push(
        {
            title: '單號',
            className: 'text-nowrap',
            render: r => {
                const a = document.createElement('a');
                a.href = appUrl('/work-orders/' + r.workOrderId);
                a.className = 'lf-text-data';
                a.textContent = '#' + r.workOrderId;
                return a;
            }
        },
        {
            title: '問題',
            className: 'lf-minw-xl',
            render: r => {
                const cell = renderIssueCell(r.issueLabel, r.plainExplanation);
                const flags = workOrderFlagBadges(r, { recurrence: true });
                if (flags) cell.appendChild(flags);
                return cell;
            }
        },
        {
            title: '嚴重度',
            className: 'text-nowrap',
            render: r => workOrderSeverityCell(r)
        },
        {
            title: '處理人',
            className: 'lf-minw-sm',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 flex-wrap';
                const span = document.createElement('span');
                span.textContent = r.handlerName || '';
                wrap.appendChild(span);
                if (!r.handlerActive) {
                    wrap.appendChild(statusBadge('已停用', 'secondary'));
                }
                if (r.handlerPaused) {
                    wrap.appendChild(statusBadge('暫停接單', 'warning'));
                }
                return wrap;
            }
        },
        {
            title: '成員',
            className: 'lf-minw-xs',
            render: r => {
                const wrap = document.createElement('div');
                const counts = r.counts || {};
                const main = document.createElement('div');
                main.className = 'text-nowrap lf-text-data';
                main.textContent = hostCountText(r);
                wrap.appendChild(main);

                const parts = [];
                if (counts.inProgress > 0) parts.push(`處理中 ${counts.inProgress}`);
                if (counts.observing > 0) parts.push(`觀察 ${counts.observing}`);
                if (counts.open > 0) parts.push(`未處理 ${counts.open}`);
                if (counts.escalated > 0) parts.push(`上報 ${counts.escalated}`);
                if (counts.daySyncPending > 0) parts.push(`逐日同步中 ${counts.daySyncPending}`);
                // 停用主機上的進行中成員不計入左邊的台數（與處理人徽章、負載看板同一口徑）
                if (counts.activeOnDisabledHosts > 0) parts.push(`主機已停用 ${counts.activeOnDisabledHosts}`);

                if (parts.length > 0) {
                    const sub = document.createElement('div');
                    sub.className = 'lf-text-meta';
                    sub.textContent = parts.join('、');
                    wrap.appendChild(sub);
                }
                return wrap;
            }
        },
        {
            title: '逾期',
            className: 'text-end',
            render: r => {
                const overdue = r.counts?.overdue ?? 0;
                if (overdue > 0) {
                    return statusBadge(String(overdue), 'danger');
                }
                const span = document.createElement('span');
                span.textContent = '0';
                return span;
            }
        },
        {
            title: '未回覆',
            className: 'text-end text-nowrap',
            render: r => (r.unrepliedDays == null ? '—' : roleText('lf-text-data', `${r.unrepliedDays} 天`))
        },
        {
            title: '未讀回覆',
            className: 'text-nowrap',
            render: r => (r.unreadReply
                ? statusBadge('有新回覆', 'info', { title: '處理人回覆後尚未開啟過這張單的詳情' })
                : '—')
        },
        {
            title: '期限',
            className: 'text-nowrap',
            render: r => (r.dueDate ? roleText('lf-text-data', formatDate(r.dueDate)) : '—')
        },
        {
            title: '來源',
            render: r => originBadge(r.origin)
        },
        {
            title: '建立',
            className: 'text-nowrap',
            render: r => roleText('lf-text-data', formatDateTime(r.createdAt))
        },
        {
            title: '最近新增',
            className: 'text-nowrap',
            render: r => (r.lastAppendedAt ? roleText('lf-text-data', formatDateTime(r.lastAppendedAt)) : '—')
        },
        {
            title: '狀態',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 flex-wrap';
                if (r.closedAt) {
                    wrap.appendChild(statusBadge('已結案', 'neutral', { title: workOrderClosedReasonText(r.closedReason) }));
                } else {
                    if (r.paused) {
                        wrap.appendChild(statusBadge(`暫停（靜音至 ${r.mutedUntil || ''}）`, 'warning'));
                    }
                    if (r.resumedFromMuteAt) {
                        wrap.appendChild(statusBadge(`${r.resumedFromMuteAt} 自靜音恢復`, 'info'));
                    }
                    if (!r.paused && !r.resumedFromMuteAt) {
                        wrap.appendChild(statusBadge('進行中', 'primary'));
                    }
                    const stopped = continuationStoppedBadge(r);
                    if (stopped) wrap.appendChild(stopped);
                }
                return wrap;
            }
        }
    );

    renderTable(activeListContainer, {
        columns,
        rows: data.items,
        empty: { title: '沒有符合條件的交辦單', hint: '請調整狀態、群組或勾選條件，或至「待派」頁籤查看可建立交辦的問題。' }
    });

    renderPagination(activePagerContainer, {
        page: data.page,
        totalPages: Math.ceil(data.total / data.pageSize),
        pageSize: data.pageSize,
        onPage: newPage => {
            activePage = newPage;
            guardLoad(activeListContainer, loadActive);
        },
        onPageSize: newSize => {
            savePageSize('work-orders', newSize);
            activePage = 1;
            guardLoad(activeListContainer, loadActive);
        }
    });
}

// ── 負載看板頁籤 ───────────────────────────────────────────────────────────

async function loadLoadBoard() {
    renderLoading(loadContainer, 5);

    const data = await api.get('/api/work-orders/load-board');
    renderLoadBoardTable(data);
    loadedTabs.add('load');
}

/** 負載看板的處理人名稱：點下去帶入進行中清單的處理人篩選（原本連到處理人工作頁的連結移到操作欄） */
function handlerFilterLink(row) {
    const link = document.createElement('a');
    link.href = appUrl('/work-orders') + '?handlerId=' + row.userId;
    link.textContent = row.name || '';
    link.title = '看他名下進行中的交辦單';
    link.addEventListener('click', event => {
        event.preventDefault();
        applyHandlerFilter(row.userId, { name: row.name });
    });
    return link;
}

/** 負載看板每列的操作：「改派他名下的單…」（具 Assign 且他名下有進行中單）與處理人工作頁連結 */
function loadBoardActions(row) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex align-items-center gap-2';
    if (canAssign && row.activeWorkOrders > 0) {
        wrap.appendChild(button('改派他名下的單…', {
            title: `篩選 ${row.name} 名下進行中的單並勾選，再選新處理人`,
            onClick: () => applyHandlerFilter(row.userId, { name: row.name, autoSelect: true })
        }));
    }
    const page = document.createElement('a');
    page.href = appUrl('/handlers/' + row.userId);
    page.textContent = '工作頁';
    wrap.appendChild(page);
    return wrap;
}

function renderLoadBoardTable(data) {
    const rows = (data.rows || []).slice().sort((a, b) => b.activeMembers - a.activeMembers);

    const columns = [
        {
            title: '處理人',
            className: 'lf-minw-sm',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 flex-wrap';
                wrap.appendChild(handlerFilterLink(r));
                if (!r.active) {
                    wrap.appendChild(statusBadge('已停用', 'secondary'));
                }
                if (r.paused) {
                    wrap.appendChild(statusBadge('暫停接單', 'warning'));
                }
                if (r.inPool) {
                    wrap.appendChild(statusBadge('派工池', 'info'));
                }
                return wrap;
            }
        },
        {
            title: '進行中單',
            className: 'text-end',
            render: r => roleText('lf-text-data', formatNumber(r.activeWorkOrders))
        },
        {
            title: '進行中台數',
            className: 'text-end',
            render: r => roleText('lf-text-data', formatNumber(r.activeMembers))
        },
        {
            title: '未回覆單',
            className: 'text-end',
            render: r => roleText('lf-text-data', formatNumber(r.unrepliedWorkOrders))
        },
        {
            title: '逾期台數',
            className: 'text-end',
            render: r => roleText('lf-text-data', formatNumber(r.overdueMembers))
        },
        {
            title: '近 7 天結案',
            className: 'text-end',
            render: r => roleText('lf-text-data', formatNumber(r.closedLast7Days))
        },
        {
            title: '最舊進行中',
            render: r => (r.oldestActiveCreatedAt ? roleText('lf-text-data', formatDate(r.oldestActiveCreatedAt)) : '—')
        },
        {
            title: '操作',
            className: 'text-nowrap',
            render: r => loadBoardActions(r)
        }
    ];

    renderTable(loadContainer, {
        columns,
        rows,
        empty: { title: '尚無處理人負載資料', hint: '目前系統中沒有設定任何可指派的處理人或尚無交辦負載資料。' }
    });
}

// ── 待派頁籤 ───────────────────────────────────────────────────────────────
// 三段：可以派（預設展開）、派不出去（依原因分組，每組一次說明）、不會派（預設收合）。
// 各段各自分頁；第一次載入只送「可以派」那一段的請求（回應同時帶三段的統計、原因分組與可授權群組），
// 其餘兩段的問題清單在使用者展開時才載入。

/** 待派清單每頁筆數（與 WorkOrderBoardService.GapPageSize 同值） */
const GAP_PAGE_SIZE = 50;

/** 派不出去的原因：一組一次說明（要做什麼、去哪裡） */
const UNASSIGNABLE_HINTS = {
    no_pool: '派工池裡沒有任何成員。到「系統管理 > 群組與授權」，把使用者群組設為派工池並加入成員。',
    all_paused: '派工池成員全部暫停接單。請處理人到自己的頁面恢復接單。',
    no_visibility: '派工池成員都看不到這些主機。把下列主機群組授權給派工池群組，就能派出去。'
};

const SECTION_META = {
    dispatchable: { title: '可以派', open: true, countOf: d => d.dispatchable },
    unassignable: { title: '派不出去', open: true, countOf: d => d.unassignableSection },
    notDispatched: { title: '不會派', open: false, countOf: d => d.notDispatched }
};

/** 各段的畫面狀態：頁碼、容器、是否已載入問題清單 */
const gapSections = {};
const gapsGuard = createSeqGuard();
let gapsMeta = null;
let gapsCanAssign = false;

function gapSectionText(name, counts) {
    const issues = counts?.issues ?? 0;
    const hosts = counts?.hosts ?? 0;
    return issues === 0 ? '0 個問題' : `${formatNumber(issues)} 個問題、${formatNumber(hosts)} 台`;
}

/** 把一段的標頭（展開鈕＋標題＋統計）與內容容器建好；回傳狀態物件 */
function buildGapSection(name, data) {
    const meta = SECTION_META[name];
    const previous = gapSections[name];
    const section = document.createElement('section');
    section.className = 'mb-4';
    section.dataset.gapSection = name;

    const head = document.createElement('div');
    head.className = 'd-flex flex-wrap align-items-center gap-2 mb-2';
    const title = document.createElement('h3');
    title.className = 'lf-text-label mb-0';
    title.textContent = meta.title;
    const count = roleText('lf-text-meta', gapSectionText(name, meta.countOf(data)));
    const body = document.createElement('div');
    body.id = `wo-gaps-section-${name}`;
    const state = { name, body, page: previous?.page ?? 1, rowsHost: null, pagerHost: null, loaded: false, guard: previous?.guard ?? createSeqGuard() };
    const startOpen = previous ? previous.open : meta.open;
    state.open = startOpen;
    const toggle = disclosure(meta.title === '不會派' ? '不會派的問題' : meta.title === '派不出去' ? '派不出去的原因與問題' : '可派的問題', body, {
        expanded: startOpen,
        onToggle: open => {
            state.open = open;
            if (open) ensureGapSectionLoaded(state);
        }
    });
    head.append(title, count, toggle);
    section.append(head, body);
    gapSections[name] = state;
    return { section, state };
}

async function loadGaps() {
    renderLoading(gapsContainer, 5);
    const seq = gapsGuard.next();

    const data = await fetchSectionPage('dispatchable');
    if (!gapsGuard.isCurrent(seq)) return;

    if (data.from) gapsFromInput.value = formatDate(data.from);
    if (data.to) gapsToInput.value = formatDate(data.to);

    if (data.tooLarge) {
        gapsNoteEl.textContent = `期間內尚未派出的出現點至少 ${formatNumber(data.pendingAtLeast)} 個，超過上限 ${formatNumber(data.maxOccurrences)} 個，請縮小期間`;
        gapsContainer.replaceChildren();
        gapsMeta = null;
        loadedTabs.add('gaps');
        return;
    }

    gapsMeta = data;
    gapsCanAssign = hasCapability(currentUser, 'Assign');
    gapsNoteEl.textContent = `期間 ${formatDate(data.from)} ~ ${formatDate(data.to)}，共 ${formatNumber(data.total)} 個問題待派`;
    gapsContainer.replaceChildren();
    for (const name of Object.keys(SECTION_META)) {
        const { section, state } = buildGapSection(name, data);
        gapsContainer.appendChild(section);
        if (name === 'dispatchable') {
            // 第一段的清單已隨第一個請求回來；0 個可派時顯示空狀態文字
            renderGapSectionBody(state, data);
        } else if (name === 'unassignable') {
            renderUnassignableSummary(state, data);
            if (state.open) ensureGapSectionLoaded(state);
        } else if (state.open) {
            ensureGapSectionLoaded(state);
        }
    }
    loadedTabs.add('gaps');
}

async function fetchGaps(section, page) {
    const params = new URLSearchParams();
    if (gapsFromInput.value) params.set('from', gapsFromInput.value);
    if (gapsToInput.value) params.set('to', gapsToInput.value);
    params.set('page', String(page));
    params.set('section', section);
    return api.get(`/api/work-orders/gaps?${params}`);
}

/** 取某段目前頁；刪除或派工後頁數變少、目前頁已沒有資料時退回第 1 頁 */
async function fetchSectionPage(name) {
    const state = gapSections[name];
    let data = await fetchGaps(name, state?.page ?? 1);
    if (state && state.page > 1 && (data.rows ?? []).length === 0 && (data.sectionTotal ?? 0) > 0) {
        state.page = 1;
        data = await fetchGaps(name, 1);
    }
    return data;
}

/** 展開時才載入該段的問題清單（可以派的清單已隨第一個請求帶回） */
function ensureGapSectionLoaded(state) {
    // 可以派的清單已隨第一個請求帶回；派不出去的清單由段內的「這些問題」展開時載入
    if (state.loaded || state.name !== 'notDispatched') return;
    loadGapSectionRows(state);
}

async function loadGapSectionRows(state) {
    const seq = state.guard.next();
    state.rowsHost ??= document.createElement('div');
    renderLoading(state.rowsHost, 3);
    if (!state.rowsHost.isConnected) state.body.appendChild(state.rowsHost);
    let data;
    try {
        data = await fetchSectionPage(state.name);
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
        state.rowsHost.replaceChildren();
        return;
    }
    if (!state.guard.isCurrent(seq)) return;
    state.loaded = true;
    renderGapSectionRows(state, data);
}

function renderGapSectionBody(state, data) {
    state.loaded = true;
    state.body.replaceChildren();
    state.rowsHost = document.createElement('div');
    state.body.appendChild(state.rowsHost);
    renderGapSectionRows(state, data);
}

/** 派不出去段：先是依原因分組的說明與授權入口（不用另外載入），問題清單在下方 */
function renderUnassignableSummary(state, data) {
    state.body.replaceChildren();
    const reasons = data.unassignableReasons ?? [];
    if (reasons.length === 0) {
        state.body.appendChild(roleText('lf-text-meta', '沒有派不出去的問題。'));
    }
    for (const reason of reasons) {
        const block = document.createElement('div');
        block.className = 'mb-3';
        const line = document.createElement('div');
        line.append(
            roleText('lf-text-label', UNASSIGNABLE_LABELS[reason.reason] || reason.reason), ' ',
            roleText('lf-text-meta', `${formatNumber(reason.issues)} 個問題、${formatNumber(reason.hosts)} 台`));
        block.appendChild(line);
        const hint = UNASSIGNABLE_HINTS[reason.reason];
        if (hint) {
            const hintLine = roleText('lf-text-meta', hint);
            hintLine.classList.add('d-block');
            block.appendChild(hintLine);
        }
        if (reason.reason === 'no_visibility') block.appendChild(renderUnseenGroups(data));
        state.body.appendChild(block);
    }
    // 問題清單收在這一段裡的「這些問題」下面，點開才載入
    state.rowsHost = document.createElement('div');
    state.loaded = false;
    if (reasons.length > 0) {
        const toggle = disclosure('這些問題', state.rowsHost, {
            expanded: false,
            onToggle: open => { if (open && !state.loaded) loadGapSectionRows(state); }
        });
        state.body.append(toggle, state.rowsHost);
    }
}

/** 「沒有人看得到」的主機群組：每個群組只出現一次，旁邊「授權給…」；沒有 Maintain 時是連到群組與授權的連結 */
function renderUnseenGroups(data) {
    const wrap = document.createElement('ul');
    wrap.className = 'list-unstyled mt-2 mb-0';
    wrap.id = 'wo-gaps-unseen-groups';
    const groups = data.unseenHostGroups ?? [];
    if (groups.length === 0) {
        const none = document.createElement('li');
        none.appendChild(roleText('lf-text-meta', '這些主機都沒有主機群組，請先到「系統管理 > 主機」補上群組。'));
        wrap.appendChild(none);
        return wrap;
    }
    for (const group of groups) {
        const item = document.createElement('li');
        item.className = 'd-flex flex-wrap align-items-center gap-2 mb-2 lf-list-row';
        item.dataset.hostGroupId = String(group.groupId);
        item.appendChild(roleText('lf-text-label', group.groupName));
        item.appendChild(roleText('lf-text-meta', `${formatNumber(group.hosts)} 台、${formatNumber(group.issues)} 個問題`));
        item.appendChild(data.canGrantAccess
            ? renderGrantControl(group, data.poolGroups ?? [])
            : linkButton('到「群組與授權」設定', appUrl('/admin/groups'), { variant: 'outline-secondary' }));
        wrap.appendChild(item);
    }
    return wrap;
}

/** 「授權給…」：選一個派工池群組後按「授權」；確認框寫出誰會看到哪些主機，成功後待派重算 */
function renderGrantControl(group, poolGroups) {
    const box = document.createElement('span');
    box.className = 'd-inline-flex flex-wrap align-items-center gap-2';
    if (poolGroups.length === 0) {
        box.appendChild(roleText('lf-text-meta', '沒有可授權的派工池群組'));
        box.appendChild(linkButton('到「群組與授權」設定', appUrl('/admin/groups'), { variant: 'outline-secondary' }));
        return box;
    }
    const select = document.createElement('select');
    select.className = 'form-select form-select-sm w-auto';
    select.setAttribute('aria-label', `把「${group.groupName}」授權給哪個派工池群組`);
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '授權給…';
    select.appendChild(placeholder);
    for (const pool of poolGroups) {
        const option = document.createElement('option');
        option.value = String(pool.userGroupId);
        option.textContent = pool.groupName;
        select.appendChild(option);
    }
    const grant = button('授權', {
        variant: 'outline-primary',
        disabled: true,
        onClick: () => grantHostGroup(group, poolGroups.find(p => String(p.userGroupId) === select.value), grant)
    });
    select.addEventListener('change', () => { grant.disabled = !select.value; });
    box.append(select, grant);
    return box;
}

async function grantHostGroup(group, pool, grantButton) {
    if (!pool) return;
    const confirmed = await confirmAction({
        title: '授權主機群組',
        message: `把主機群組「${group.groupName}」（${formatNumber(group.hosts)} 台）授權給「${pool.groupName}」後，該群組的成員看得到這些主機，之後可以派工給他們。確定授權？`,
        confirmText: '確定授權',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;
    const restore = withBusy(grantButton, '授權中…');
    try {
        await api.put(`/api/admin/access/${pool.userGroupId}`, { grant: [group.groupId], revoke: [] });
        toast(`已把「${group.groupName}」授權給「${pool.groupName}」`, 'success');
        loadedTabs.delete('load');
        // 授權後三段統計與原因分組都變了：整頁重算，維持各段展開狀態
        await loadGaps();
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
    } finally {
        restore();
    }
}

/** 某段的問題清單（表格＋分頁）。三段的欄位不同，但都是「一列＝一個問題」 */
function renderGapSectionRows(state, data) {
    const host = state.rowsHost;
    host.replaceChildren();
    const pager = document.createElement('nav');
    pager.className = 'mt-3';

    const rows = data.rows ?? [];
    if (rows.length === 0) {
        const text = state.name === 'dispatchable'
            ? { title: '目前沒有可以派的問題', hint: '期間內沒有可派給處理人的問題；派不出去或不會派的問題在下面兩段，或調整上方的起訖日期。' }
            : state.name === 'unassignable'
                ? { title: '沒有派不出去的問題', hint: '期間內每個要派的問題都找得到處理人。' }
                : { title: '沒有不會派的問題', hint: '期間內沒有被閘門排除的問題。' };
        renderEmpty(host, { title: text.title, hint: text.hint });
        return;
    }

    renderTable(host, {
        columns: gapColumns(state.name),
        rows,
        caption: `待派清單：${SECTION_META[state.name].title}`
    });
    host.appendChild(pager);
    renderPagination(pager, {
        page: data.page,
        totalPages: Math.max(1, Math.ceil((data.sectionTotal ?? data.total) / GAP_PAGE_SIZE)),
        onPage: newPage => {
            state.page = newPage;
            if (state.name === 'dispatchable') reloadDispatchable(state);
            else loadGapSectionRows(state);
        }
    });
}

/** 可派段換頁：只重載這一段（不動另兩段與統計） */
async function reloadDispatchable(state) {
    const seq = state.guard.next();
    renderLoading(state.rowsHost, 3);
    let data;
    try {
        data = await fetchSectionPage('dispatchable');
    } catch {
        return;
    }
    if (!state.guard.isCurrent(seq)) return;
    renderGapSectionRows(state, data);
}

function gapColumns(sectionName) {
    const issueColumn = {
        title: '問題',
        className: 'lf-minw-xl',
        render: r => renderIssueCell(r.issueLabel, r.plainExplanation)
    };
    if (sectionName === 'dispatchable') {
        const columns = [
            issueColumn,
            {
                title: '可派台數',
                className: 'text-end lf-col-nowrap',
                render: r => roleText('lf-text-data', formatNumber((r.suggested || []).reduce((sum, s) => sum + s.hosts, 0)))
            },
            {
                title: '建議處理人',
                render: r => {
                    const wrap = document.createElement('div');
                    for (const s of r.suggested || []) {
                        const line = document.createElement('div');
                        line.textContent = `${s.name} ${s.hosts} 台 `;
                        line.appendChild(roleText('lf-text-meta', s.willCreate ? '新建單' : '併入既有單'));
                        wrap.appendChild(line);
                    }
                    return wrap;
                }
            }
        ];
        // 單題派工需要 Assign；沒有時整欄不出現
        if (gapsCanAssign) {
            columns.push({
                title: '',
                className: 'lf-col-nowrap text-end',
                render: r => button('派這個問題', {
                    variant: 'outline-primary',
                    opens: 'dialog',
                    onClick: () => runDispatch({ source: r.source, eventId: r.eventId, label: r.issueLabel })
                })
            });
        }
        return columns;
    }
    if (sectionName === 'unassignable') {
        return [
            issueColumn,
            {
                title: '原因',
                render: r => {
                    const wrap = document.createElement('div');
                    for (const u of r.unassignable || []) {
                        const line = document.createElement('div');
                        line.textContent = `${UNASSIGNABLE_LABELS[u.reason] || u.reason} ${formatNumber(u.hosts)} 台`;
                        wrap.appendChild(line);
                    }
                    return wrap;
                }
            }
        ];
    }
    return [
        issueColumn,
        {
            title: '不會派的原因',
            render: r => {
                const wrap = document.createElement('div');
                for (const [code, count] of Object.entries(r.excluded || {})) {
                    if (!(count > 0)) continue;
                    const text = `${EXCLUDED_LABELS[code] || code} ${formatNumber(count)} 台`;
                    const line = document.createElement('div');
                    if (code === 'gate_dismissed' || code === 'gateDismissed') {
                        // 不再打擾的台數可點：展開下方的「不再自動派工」清單
                        const link = document.createElement('button');
                        link.type = 'button';
                        link.className = 'lf-nav-item lf-nav-item--inline';
                        link.textContent = text;
                        link.title = '到「不再自動派工」清單查看與恢復';
                        link.addEventListener('click', () => openDismissed());
                        line.appendChild(link);
                    } else {
                        line.textContent = text;
                    }
                    wrap.appendChild(line);
                }
                return wrap;
            }
        }
    ];
}

// ── 立即派工（整頁）與單題派工：先預覽、確認後才派 ────────────────────────────

/** 確認框內文：將建立／併入幾張單、每人幾台、派不出去的 */
function dispatchPreviewText(preview, scope) {
    const lines = [];
    lines.push(scope
        ? `「${scope.label}」將建立 ${formatNumber(preview.createOrders)} 張單、併入 ${formatNumber(preview.mergeOrders)} 張單，共 ${formatNumber(preview.hosts)} 台。`
        : `將建立 ${formatNumber(preview.createOrders)} 張單、併入 ${formatNumber(preview.mergeOrders)} 張單，共 ${formatNumber(preview.hosts)} 台。`);
    for (const p of preview.perHandler ?? []) lines.push(`${p.name}　${formatNumber(p.hosts)} 台`);
    const unassigned = (preview.unassigned ?? []).filter(u => u.hosts > 0);
    if (unassigned.length > 0) {
        lines.push(`另有派不出去的：${unassigned.map(u => `${UNASSIGNABLE_LABELS[u.reason] || u.reason} ${formatNumber(u.hosts)} 台`).join('、')}`);
    }
    return lines.join('\n');
}

/** 派工結果的提示文字（成功、部分失敗與零成員三種）；回傳 { message, kind } */
function dispatchResultNote(res) {
    if (res.nothingToDispatch) return { message: res.message || '沒有需要派的項目（可能剛由其他人派出）', kind: 'info' };
    let msg = `已建立 ${res.createdOrders} 張、併入 ${res.mergedOrders} 張，共 ${res.assignedMembers} 台`;
    if (res.daySyncPendingCases > 0) msg += '，逐日同步在背景完成';
    const failed = res.failedGroups ?? [];
    if (failed.length > 0) return { message: `${msg}，${failed.length} 組失敗`, kind: 'warning' };
    return { message: msg, kind: 'success' };
}

/** scope 為 null＝整頁立即派工；{ source, eventId, label }＝只派這個問題 */
async function runDispatch(scope) {
    const from = gapsFromInput.value;
    const to = gapsToInput.value;
    const body = { from: from || null, to: to || null };
    if (scope) { body.source = scope.source; body.eventId = scope.eventId; }

    const busyTarget = scope ? null : gapsAutoDispatchBtn;
    const restore = busyTarget ? withBusy(busyTarget, '試算中…') : () => {};
    let preview;
    try {
        preview = await api.post('/api/work-orders/auto-dispatch/preview', body);
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
        return;
    } finally {
        restore();
    }

    if (preview.nothingToDispatch) {
        toast(preview.message || '沒有需要派的項目（可能剛由其他人派出）', 'info');
        await loadGaps();
        return;
    }

    const confirmed = await confirmAction({
        title: scope ? '派這個問題' : '立即派工',
        message: `${dispatchPreviewText(preview, scope)}\n期間 ${from} ~ ${to}。確定派工？`,
        confirmText: '確定派工',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;

    const restoreRun = busyTarget ? withBusy(busyTarget, '派工中…') : () => {};
    try {
        const res = await api.post('/api/work-orders/auto-dispatch', body);
        const note = dispatchResultNote(res);
        toast(note.message, note.kind);
        loadedTabs.delete('active');
        loadedTabs.delete('load');
        await loadGaps();
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
    } finally {
        restoreRun();
    }
}

// ── 不再自動派工（待派頁籤下方）──────────────────────────────────────────────

/** 「不再自動派工」清單預設收合：展開才載入；收合時標頭只顯示輕量計數 */
let dismissedOpen = false;
let dismissedLoaded = false;
let dismissedToggleBtn = null;

async function loadDismissedCount() {
    try {
        const count = await api.get('/api/work-orders/dismissed/count', { silent: true });
        // 清單展開後以清單回應的數字為準（renderDismissed 會寫同一個欄位）；這裡只在收合時補計數
        if (!dismissedOpen || !dismissedLoaded) {
            dismissedCountEl.textContent = count.issues > 0 ? `共 ${count.issues} 個問題、${count.hosts} 台` : '目前沒有';
        }
    } catch {
        dismissedCountEl.textContent = '';
    }
}

/** 展開「不再自動派工」並捲到那一區（待派的「不再打擾」台數連結用） */
function openDismissed() {
    if (!dismissedOpen) dismissedToggleBtn?.click();
    dismissedCard?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function setupDismissedToggle() {
    if (!dismissedBody || !dismissedToggleSlot) return;
    dismissedToggleBtn = disclosure('被擋住的問題', dismissedBody, {
        expanded: false,
        onToggle: open => {
            dismissedOpen = open;
            if (open && !dismissedLoaded) {
                dismissedPage = 1;
                guardLoad(dismissedContainer, loadDismissed);
            }
        }
    });
    dismissedToggleSlot.replaceChildren(dismissedToggleBtn);
}

async function loadDismissed() {
    dismissedLoaded = true;
    renderLoading(dismissedContainer, 3);
    let data = await api.get(`/api/work-orders/dismissed?page=${dismissedPage}`);
    // 恢復後最後一頁清空：退回新的最後一頁
    if ((data.groups || []).length === 0 && data.total > 0 && dismissedPage > 1) {
        dismissedPage = Math.ceil(data.total / (data.pageSize || 1));
        data = await api.get(`/api/work-orders/dismissed?page=${dismissedPage}`);
    }
    renderDismissed(data, hasCapability(currentUser, 'Assign'));
}

/** 就地重載這一區 */
async function reloadDismissed() {
    await guardLoad(dismissedContainer, loadDismissed);
}

function renderDismissed(data, canRestore) {
    dismissedContainer.replaceChildren();
    dismissedPagerContainer.replaceChildren();
    const groups = data.groups || [];
    dismissedCountEl.textContent = data.total > 0 ? `共 ${data.total} 個問題、${data.totalHosts} 台` : '';

    if (groups.length === 0) {
        renderEmpty(dismissedContainer, {
            title: '沒有不再自動派工的問題',
            hint: '目前沒有被判定為不處理、誤報或已知雜訊而停止自動派工的問題。'
        });
        return;
    }

    if (!canRestore) {
        const readOnly = roleText('lf-text-meta', '只有系統管理員可以處理或恢復。');
        readOnly.classList.add('d-block', 'mb-3');
        dismissedContainer.appendChild(readOnly);
    }

    for (const group of groups) {
        const section = document.createElement('div');
        section.className = 'mb-4';

        const head = document.createElement('div');
        head.className = 'd-flex flex-wrap align-items-start justify-content-between gap-2 mb-2';
        const title = document.createElement('div');
        title.appendChild(renderIssueCell(group.issueLabel, group.plainExplanation));
        title.appendChild(roleText('lf-text-meta', `${group.hosts} 台`));
        head.appendChild(title);
        if (canRestore) {
            head.appendChild(button('整個問題恢復', {
                variant: 'outline-secondary',
                onClick: () => restoreDismissedIssue(group)
            }));
        }
        section.appendChild(head);

        const tableHost = document.createElement('div');
        tableHost.className = 'lf-table-aligned';
        section.appendChild(tableHost);
        const columns = [
            { title: '主機', className: 'text-nowrap', truncate: true, render: row => row.hostName },
            {
                title: '先前結案狀態',
                className: 'lf-col-nowrap lf-col-w-md',
                render: row => {
                    const meta = statusBadgeMeta(row.status);
                    return statusBadge(meta ? meta.label : row.status, meta ? meta.variant : 'secondary');
                }
            },
            { title: '結案的人', render: row => row.closedBy || '—' },
            {
                title: '日期',
                className: 'lf-col-nowrap lf-col-w-md',
                render: row => row.closedAt ? roleText('lf-text-data', formatDate(row.closedAt)) : document.createTextNode('—')
            },
            {
                // 高嚴重度（或重大）且處理人回覆產生的，滿 30 天自動恢復派工；上線前判定的不會自動恢復；其餘永久
                title: '恢復派工',
                className: 'lf-col-nowrap lf-col-w-md',
                render: row => {
                    if (row.releaseInDays != null) return roleText('lf-text-data', `${row.releaseInDays} 天後恢復派工`);
                    if (row.preLaunch) {
                        const text = roleText('lf-text-data', '上線前判定');
                        text.title = '結案早於「30 天自動恢復」上線，不會自動恢復；請逐筆或整個問題恢復';
                        return text;
                    }
                    return roleText('lf-text-data', '永久');
                }
            }
        ];
        if (canRestore) {
            columns.push({
                title: '',
                className: 'lf-col-nowrap lf-col-w-md text-end',
                render: row => button('恢復', {
                    variant: 'outline-primary',
                    onClick: () => restoreDismissedRow(group, row)
                })
            });
        }
        renderTable(tableHost, {
            columns,
            rows: group.rows,
            caption: `${group.issueLabel} 不再自動派工的主機`
        });
        dismissedContainer.appendChild(section);
    }

    renderPagination(dismissedPagerContainer, {
        page: data.page,
        totalPages: Math.ceil(data.total / (data.pageSize || 1)),
        onPage: newPage => {
            dismissedPage = newPage;
            guardLoad(dismissedContainer, loadDismissed);
        }
    });
}

async function restoreDismissedRow(group, row) {
    const confirmed = await confirmAction({
        title: '恢復自動派工',
        message: `主機 ${row.hostName} 的「${group.issueLabel}」恢復後，下一次夜間派工會重新判斷要不要派給處理人。確定恢復？`,
        confirmText: '確定恢復',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;
    try {
        await api.post('/api/work-orders/dismissed/restore', { hostName: row.hostName, issueKey: row.issueKey });
        toast(`已恢復 ${row.hostName} 的「${group.issueLabel}」`, 'success');
        await reloadDismissed();
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
    }
}

async function restoreDismissedIssue(group) {
    const confirmed = await confirmAction({
        title: '恢復整個問題的自動派工',
        message: `「${group.issueLabel}」目前有 ${group.hosts} 台不再自動派工，全部恢復後，下一次夜間派工會重新判斷。確定恢復？`,
        confirmText: '確定全部恢復',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;
    try {
        const res = await api.post('/api/work-orders/dismissed/restore-issue', { source: group.source, eventId: group.eventId });
        toast(`已恢復「${group.issueLabel}」${res.restored} 台`, 'success');
        await reloadDismissed();
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
    }
}

// ── 靜音中頁籤 ─────────────────────────────────────────────────────────────

/** 暫停中交辦單張數只逐一查前幾個問題（docs/archive/FEEDBACK-47-PLAN.md 15.3 (1)：問題數通常個位數） */
const MUTED_PAUSED_LOOKUP_LIMIT = 20;

/** 取靜音中的問題（currentMute 不為 null），並同步頁籤標題的數字 */
async function fetchMutedIssues() {
    const owners = await api.get('/api/admin/issue-owners');
    const muted = (owners ?? []).filter(o => o.currentMute != null);
    updateMutedTabTitle(muted.length);
    return muted;
}

/** 頁籤標題的數字只用輕量計數；清單等使用者點到「靜音中」才載入 */
async function loadMutedCount() {
    try {
        const result = await api.get('/api/admin/issue-owners/muted-count', { silent: true });
        updateMutedTabTitle(result.count ?? 0);
    } catch {
        // 取不到就維持「靜音中」標題；進頁籤時清單會再算一次
    }
}

function updateMutedTabTitle(count) {
    const btn = mutedTabItem?.querySelector('[data-tab="muted"]');
    if (btn) btn.textContent = `靜音中（${count}）`;
}

/** 剩餘天數＝靜音至 − 今天 + 1；今天到期顯示「今天到期」 */
function remainingDaysText(toDate) {
    if (!toDate) return '—';
    const diff = Math.round((Date.parse(toDate + 'T00:00:00Z') - Date.parse(todayLocal() + 'T00:00:00Z')) / 86400000);
    if (Number.isNaN(diff)) return '—';
    if (diff < 0) return '已到期';
    if (diff === 0) return '今天到期';
    return `${diff + 1} 天`;
}

/** 單一問題目前暫停中的交辦單張數；失敗回 null（顯示「—」，不中斷整張表） */
async function fetchPausedCount(source, eventId) {
    const params = new URLSearchParams({
        source,
        eventId: String(eventId),
        status: 'active',
        paused: 'only',
        pageSize: '1'
    });
    try {
        const data = await api.get(`/api/work-orders?${params}`, { silent: true });
        return Number.isInteger(data?.total) ? data.total : null;
    } catch {
        return null;
    }
}

async function loadMuted() {
    renderLoading(mutedContainer, 5);

    const muted = await fetchMutedIssues();

    const rows = muted.slice().sort((a, b) =>
        compareText(a.currentMute.to, b.currentMute.to));

    const counts = await Promise.all(rows.slice(0, MUTED_PAUSED_LOOKUP_LIMIT)
        .map(r => fetchPausedCount(r.sourceName, r.eventId)));
    const pausedCounts = new Map();
    rows.slice(0, MUTED_PAUSED_LOOKUP_LIMIT).forEach((r, i) => pausedCounts.set(r, counts[i]));

    renderMutedTable(rows, pausedCounts);
    loadedTabs.add('muted');
}

function reloadMuted() {
    loadedTabs.delete('muted');
    guardLoad(mutedContainer, loadMuted);
}

function renderMutedTable(rows, pausedCounts) {
    if (rows.length === 0) {
        renderEmpty(mutedContainer, { title: '目前沒有靜音中的問題', hint: '目前沒有啟用中的靜音規則；需要暫停特定問題通知時，可到左側「系統管理 > 問題負責與靜音」頁，或在問題查詢的「依問題」視角，對問題設定靜音。' });
        return;
    }

    const columns = [
        {
            title: '問題',
            className: 'lf-minw-xl',
            render: r => renderIssueCell(r.displayLabel, `${r.sourceName ?? ''} ${r.eventId ?? ''}`.trim())
        },
        {
            title: '靜音至',
            className: 'text-nowrap',
            render: r => roleText('lf-text-data', String(r.currentMute.to ?? '').slice(0, 10) || '—')
        },
        {
            title: '剩餘天數',
            className: 'text-nowrap',
            render: r => remainingDaysText(String(r.currentMute.to ?? '').slice(0, 10))
        },
        {
            title: '起始日',
            className: 'text-nowrap',
            render: r => roleText('lf-text-data', String(r.currentMute.from ?? '').slice(0, 10) || '—')
        },
        {
            title: '原因',
            render: r => roleText('lf-text-meta', r.currentMute.reason || '—')
        },
        {
            title: '設定者',
            render: r => roleText('lf-text-data', r.currentMute.byAccount || '—')
        },
        {
            title: '暫停中交辦單',
            className: 'text-end',
            render: r => {
                const n = pausedCounts.get(r);
                if (!Number.isInteger(n)) return '—';
                if (n === 0) return '0';
                const a = document.createElement('a');
                a.href = appUrl('/work-orders') + '?' + new URLSearchParams({ source: r.sourceName, eventId: String(r.eventId) });
                a.textContent = `${n} 張`;
                return a;
            }
        },
        {
            title: '動作',
            className: 'text-nowrap',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1';
                wrap.append(
                    button('延長', { opens: 'dialog', onClick: () => extendMute(r) }),
                    button('解除', { variant: 'outline-danger', opens: 'dialog', onClick: () => removeMute(r) })
                );
                return wrap;
            }
        }
    ];

    renderTable(mutedContainer, { columns, rows });

    if (rows.length > MUTED_PAUSED_LOOKUP_LIMIT) {
        const note = document.createElement('div');
        note.className = 'lf-text-meta mt-2';
        note.textContent = `只統計前 ${MUTED_PAUSED_LOOKUP_LIMIT} 個問題的暫停交辦單張數`;
        mutedContainer.appendChild(note);
    }
}

function extendMute(r) {
    openIssueMuteModal({
        source: r.sourceName,
        eventId: r.eventId,
        issueLabel: r.displayLabel,
        currentMute: r.currentMute,
        onApplied: reloadMuted
    });
}

async function removeMute(r) {
    if (!await confirmAndClearMute(r)) return;
    // 解除後暫停中的交辦單恢復，進行中頁籤下次切過去要重查
    loadedTabs.delete('active');
    reloadMuted();
}

// ── 頁籤切換與事件綁定 ─────────────────────────────────────────────────────

// ── 管理者提醒區 ───────────────────────────────────────────────────────────

/**
 * 提醒區（第 76-2 輪批次 C-2）：今天新上報、上報逾期未處理、本趟派不出去、上報時沒有收件人、反覆結案又復發五類（今天新上報只在這裡顯示、不寄信），數字與夜間的管理者摘要信
 * 同一個彙整函式（GET api/work-orders/admin-reminders）。三類都是 0 時整區不顯示；有數字時每類一行「N 筆」並連到對應位置。
 */
function renderReminders(data) {
    remindersContainer.replaceChildren();
    if (!data) return;
    const escalated = data.escalatedCount ?? 0;
    const unassignable = data.unassignableCount ?? 0;
    const noRecipient = data.noRecipientCount ?? 0;
    const newEscalated = data.newEscalatedCount ?? 0;
    const recurrence = data.recurrenceCount ?? 0;
    // 管理員待辦的新增類別（只有具 Assign 的人才會有數字，見 AdminReminderDto.TodoIncluded）
    const fetchFailed = data.fetchFailedCount ?? 0;
    const dispatchable = data.dispatchableCount ?? 0;
    const inactiveHandlers = data.inactiveHandlerCount ?? 0;
    const ungrouped = data.ungroupedHostCount ?? 0;
    if (escalated + unassignable + noRecipient + newEscalated + recurrence + fetchFailed + dispatchable + inactiveHandlers + ungrouped === 0) return;

    remindersContainer.appendChild(statusNote('warning', '有項目需要管理者處理'));
    // 沒有 Assign 的檢視者（主管、開發人員）只能看：不用行動語氣，直說誰能處理
    if (!hasCapability(currentUser, 'Assign')) {
        remindersContainer.appendChild(roleText('lf-text-meta', '只有系統管理員可以處理或恢復。'));
    }
    // 設定為 0＝不寄提醒信，但這裡照常顯示：特殊值語意，常駐說明
    if ((data.reminderDays ?? 1) === 0) {
        remindersContainer.appendChild(statusNote('info', '提醒信已關閉（設定為 0），此處仍會顯示。'));
    }
    const list = document.createElement('ul');
    list.className = 'list-unstyled mt-2 mb-0';
    // 每類一行（說明＋連結）；有明細就多一顆預設收合的揭露鈕，明細只列前幾筆（筆數是全部）
    const addRow = (text, linkText, href, detailLabel, detailItems, total) => {
        const item = document.createElement('li');
        item.className = 'd-flex flex-wrap align-items-center gap-2 mb-1 lf-list-row';
        const label = document.createElement('span');
        label.textContent = text;
        item.appendChild(label);
        item.appendChild(linkButton(linkText, href));
        if (detailItems && detailItems.length > 0) {
            const detail = document.createElement('ul');
            detail.className = 'list-unstyled w-100 mb-1 ms-3';
            for (const entry of detailItems) {
                const line = document.createElement('li');
                line.className = 'd-flex flex-wrap align-items-center gap-2';
                for (const node of entry) line.appendChild(node);
                detail.appendChild(line);
            }
            if (total > detailItems.length) {
                const more = document.createElement('li');
                more.textContent = `只列前 ${formatNumber(detailItems.length)} 筆，共 ${formatNumber(total)} 筆`;
                detail.appendChild(more);
            }
            item.appendChild(disclosure(detailLabel, detail));
            item.appendChild(detail);
        }
        list.appendChild(item);
    };
    const span = t => { const e = document.createElement('span'); e.textContent = t; return e; };
    const orderLink = id => linkButton(`單號 ${id}`, appUrl('/work-orders/' + id));
    if (fetchFailed > 0) {
        const at = data.fetchFailed?.endedAt ? `（${formatDateTime(data.fetchFailed.endedAt)}）` : '';
        addRow(`最近一趟取數失敗${at}`, '看排程作業', appUrl('/runs'));
    }
    // 今天新上報：與「超過 N 天」分開，管理員當天就看得到剛上報的，不必等到逾期
    if (newEscalated > 0) {
        const rows = (data.newEscalated ?? []).map(e => [orderLink(e.workOrderId), span(`${e.issueLabel}　${e.hostName}`)]);
        addRow(`今天新上報：${formatNumber(newEscalated)} 筆`, '看上報中的單', appUrl('/work-orders') + '?status=escalated',
            '看是哪些', rows, newEscalated);
    }
    if (escalated > 0) {
        const rows = (data.escalated ?? []).map(e => [orderLink(e.workOrderId), span(`${e.issueLabel}　${e.hostName}　上報 ${e.days} 天`)]);
        addRow(`上報超過 ${data.thresholdDays} 天沒人處理：${formatNumber(escalated)} 筆`, '看上報中的單', appUrl('/work-orders') + '?status=escalated',
            '看是哪些', rows, escalated);
    }
    if (dispatchable > 0) {
        addRow(`可派未派的問題：${formatNumber(dispatchable)} 個`, '看待派', appUrl('/work-orders') + '#gaps');
    }
    if (unassignable > 0) {
        const at = data.unassignableAt ? `（彙整於 ${formatDateTime(data.unassignableAt)}）` : '';
        const rows = (data.unassignable ?? []).map(u => [span(`${u.issueLabel}　${UNASSIGNABLE_LABELS[u.reason] ?? u.reason}　${formatNumber(u.hosts)} 台`)]);
        addRow(`最近一次夜間派工派不出去的問題：${formatNumber(unassignable)} 筆${at}`, '看待派', appUrl('/work-orders') + '#gaps',
            '看是哪些', rows, unassignable);
    }
    if (noRecipient > 0) {
        const first = (data.noRecipient ?? [])[0];
        const rows = (data.noRecipient ?? []).map(n => [orderLink(n.workOrderId), span(n.issueLabel)]);
        addRow(`上報信沒有任何收件人的單：${formatNumber(noRecipient)} 筆`, first ? `看單號 ${first.workOrderId}` : '看上報中的單',
            first ? appUrl('/work-orders/' + first.workOrderId) : appUrl('/work-orders') + '?status=escalated',
            '看是哪些', rows, noRecipient);
    }
    if (inactiveHandlers > 0) {
        const rows = (data.inactiveHandlers ?? []).map(h => [span(`${h.name}　${h.disabled ? '帳號已停用' : '暫停接單'}　${formatNumber(h.activeWorkOrders)} 張、${formatNumber(h.activeMembers)} 台`)]);
        addRow(`處理人已停用或暫停接單、名下仍有單：${formatNumber(inactiveHandlers)} 人`, '看這些單',
            appUrl('/work-orders') + '?status=active&handlerInactive=1', '看是哪些人', rows, inactiveHandlers);
    }
    // 反覆結案又復發：沒有專屬的清單篩選，連到交辦總覽「進行中」（復發的成員都在進行中的單裡）；
    // 明細的單號連結直接到那張單，成員表會標出復發次數
    if (recurrence > 0) {
        const rows = (data.recurrence ?? []).map(r => [orderLink(r.workOrderId), span(`${r.issueLabel}　${r.hostName}　復發 ${r.recurrenceCount} 次`)]);
        addRow(`反覆結案又復發：${formatNumber(recurrence)} 筆`, '看進行中的單', appUrl('/work-orders') + '?status=active',
            '看是哪些', rows, recurrence);
    }
    if (ungrouped > 0) {
        addRow(`未分組主機：${formatNumber(ungrouped)} 台（一般使用者看不到）`, '看未分組主機', appUrl('/admin/hosts') + '?status=ungrouped');
    }
    remindersContainer.appendChild(list);
}

async function loadReminders() {
    try {
        renderReminders(await api.get('/api/work-orders/admin-reminders'));
    } catch {
        // 提醒區是補充資訊：取不到就不顯示，不擋頁面其餘功能（錯誤訊息已由 api.js 顯示）
        remindersContainer.replaceChildren();
    }
}

function loadTab(tabName) {
    if (tabName === 'active') {
        guardLoad(activeListContainer, loadActive);
    } else if (tabName === 'load') {
        guardLoad(loadContainer, loadLoadBoard);
    } else if (tabName === 'gaps') {
        guardLoad(gapsContainer, loadGaps);
        dismissedPage = 1;
        dismissedLoaded = false;
        dismissedContainer.replaceChildren();
        dismissedPagerContainer.replaceChildren();
        loadDismissedCount();
        // 清單先前已展開：維持展開並重新載入；收合時只更新計數
        if (dismissedOpen) guardLoad(dismissedContainer, loadDismissed);
    } else if (tabName === 'muted') {
        guardLoad(mutedContainer, loadMuted);
    }
}

function handleTabChange(tabName) {
    if (!loadedTabs.has(tabName)) {
        loadTab(tabName);
    }
}

// 篩選變更時回到第 1 頁重查
const onActiveFilterChange = () => {
    activePage = 1;
    selection.clear();   // 篩選變了，先前勾的單不一定還在這份清單裡
    bulkPanel.close();
    if (bulkHintEl) bulkHintEl.textContent = '';
    guardLoad(activeListContainer, loadActive);
};
activeStatusSelect.addEventListener('change', onActiveFilterChange);
activeSortSelect.addEventListener('change', onActiveFilterChange);
activeGroupSelect.addEventListener('change', onActiveFilterChange);
activeHandlerSelect?.addEventListener('change', onActiveFilterChange);
activeResumedCheckbox.addEventListener('change', onActiveFilterChange);
activeHandlerInactiveCheckbox.addEventListener('change', onActiveFilterChange);
activeUnreadOnlyCheckbox.addEventListener('change', onActiveFilterChange);

// 側欄徽章每 60 秒刷新（core/badges.js 是唯一計時器，本頁不另開）：
// 未讀數與上次不同且目前在「進行中」頁籤，就依目前篩選與頁碼重新載入清單。
// 第一次事件只記基準（頁面剛載入，清單本來就是最新的）。
let lastUnreadCount;
window.addEventListener('lf:badges', event => {
    const unread = event.detail?.unread;
    if (unread == null) return;
    const previous = lastUnreadCount;
    lastUnreadCount = unread;
    if (previous === undefined || previous === unread) return;
    const activeTab = tabsEl?.querySelector('[data-tab].active')?.dataset.tab;
    if (activeTab === 'active') guardLoad(activeListContainer, loadActive);
    else loadedTabs.delete('active'); // 切回進行中時重查
});

// 待派查詢
gapsQueryBtn.addEventListener('click', () => {
    for (const state of Object.values(gapSections)) state.page = 1;
    guardLoad(gapsContainer, loadGaps);
});

// 立即派工（整頁）：先預覽、確認後才派
gapsAutoDispatchBtn.addEventListener('click', () => runDispatch(null));

// ── 初始化 ─────────────────────────────────────────────────────────────────

async function init() {
    currentUser = await getCurrentUser();
    canAssign = hasCapability(currentUser, 'Assign');

    // 立即派工是業務動作（建單），能力是 Assign；Maintain 含不能看業務資料的救援帳號，不該看到這顆鈕
    if (hasCapability(currentUser, 'Assign')) gapsAutoDispatchBtn.classList.remove('d-none');

    if (hasCapability(currentUser, 'Maintain')) {
        mutedTabItem?.classList.remove('d-none');
        // 頁籤數字走輕量計數、不阻擋頁面；清單等使用者點到「靜音中」才載入（停在其他頁籤時不送問題負責清單的請求）
        loadMutedCount();
    } else {
        // 沒有 Maintain 看得到也做不了事：頁籤與 panel 一起拿掉，網址帶 #muted 時 bindTabs 找不到就停在預設頁籤
        mutedTabItem?.remove();
        document.querySelector('[data-panel="muted"]')?.remove();
    }

    // 群組選項先填好，網址帶 groupId 時第一次查詢就用預選值
    await loadHandlerGroups();
    // 處理人下拉：先填好選項，網址帶 handlerId 時第一次查詢就用預選值；停用處理人提示帶 reassign=1 時預先勾好他名下的單
    await loadHandlerOptions();
    if (filterHandlerId) setHandlerFilterValue(filterHandlerId);
    if (urlParams.get('reassign') === '1') pendingAutoSelect = true;

    setupDismissedToggle();
    bindTabs(tabsEl, { hash: true, onChange: handleTabChange });
    loadReminders();

    const activeBtn = tabsEl.querySelector('[data-tab].active');
    const initialTab = activeBtn ? activeBtn.dataset.tab : 'active';
    if (!loadedTabs.has(initialTab)) {
        loadTab(initialTab);
    }
}

init();
