/**
 * 交辦總覽頁（回饋第 47 輪）：以交辦單為單位管理——
 * 一張單＝一個問題 × 一批主機 × 一位處理人。
 * 包含三個頁籤：
 * 1. 進行中：追蹤每張交辦單的進度、成員數與逾期/回覆狀態。
 * 2. 負載看板：檢視各處理人的交辦單數、成員數與負載狀況。
 * 3. 待派：列出期間內尚未派工的問題與建議處理人，支援依規則一鍵立即派工。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    renderTable,
    renderPagination,
    renderLoading,
    renderEmpty,
    bindTabs,
    toast,
    confirmAction,
    withBusy,
    guardLoad,
    loadPageSize,
    savePageSize
} from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, statusBadge } from '../core/format.js';

// DOM 元素
const tabsEl = document.getElementById('wo-tabs');

// 進行中頁籤
const activeStatusSelect = document.getElementById('wo-status');
const activeSortSelect = document.getElementById('wo-sort');
const activeResumedCheckbox = document.getElementById('wo-resumed');
const activeListContainer = document.getElementById('wo-list');
const activePagerContainer = document.getElementById('wo-pager');

// 負載看板頁籤
const loadContainer = document.getElementById('wo-load');

// 待派頁籤
const gapsFromInput = document.getElementById('wo-gaps-from');
const gapsToInput = document.getElementById('wo-gaps-to');
const gapsQueryBtn = document.getElementById('wo-gaps-query');
const gapsAutoDispatchBtn = document.getElementById('wo-auto-dispatch');
const gapsNoteEl = document.getElementById('wo-gaps-note');
const gapsContainer = document.getElementById('wo-gaps');
const gapsPagerContainer = document.getElementById('wo-gaps-pager');

// 來源名稱對照
const ORIGIN_LABELS = {
    manual: '人工交辦',
    owner_rule: '負責人規則',
    auto_dispatch: '自動派工',
    backfill: '系統整併',
    day_assign: '詳情頁指派'
};

// 無法指派原因對照
const UNASSIGNABLE_LABELS = {
    no_pool: '沒有派工池成員',
    noPool: '沒有派工池成員',
    no_visibility: '派工池成員都看不到這些主機',
    noVisibility: '派工池成員都看不到這些主機',
    all_paused: '候選人全部暫停接單',
    allPaused: '候選人全部暫停接單',
    disabled: '自動派工未開啟'
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

// 狀態追蹤
let currentUser = null;
const loadedTabs = new Set();
let activePage = 1;
let gapsPage = 1;

/**
 * 兩個頁籤共用的「問題欄」渲染函式
 */
function renderIssueCell(issueLabelText, plainExplanationText) {
    const wrap = document.createElement('div');
    const main = document.createElement('div');
    main.className = 'fw-bold';
    main.textContent = issueLabelText || '';
    wrap.appendChild(main);

    if (plainExplanationText) {
        const sub = document.createElement('div');
        sub.className = 'text-muted small';
        sub.textContent = plainExplanationText;
        wrap.appendChild(sub);
    }
    return wrap;
}

// ── 進行中頁籤 ─────────────────────────────────────────────────────────────

async function loadActive() {
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
        resumedFromMute: String(resumed)
    });

    const data = await api.get(`/api/work-orders?${params}`);
    renderActiveTable(data);
    loadedTabs.add('active');
}

function renderActiveTable(data) {
    const columns = [
        {
            title: '單號',
            render: r => {
                const a = document.createElement('a');
                a.href = appUrl('/work-orders/' + r.workOrderId);
                a.textContent = '#' + r.workOrderId;
                return a;
            }
        },
        {
            title: '問題',
            render: r => renderIssueCell(r.issueLabel, r.plainExplanation)
        },
        {
            title: '處理人',
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
            render: r => {
                const wrap = document.createElement('div');
                const counts = r.counts || {};
                const main = document.createElement('div');
                main.textContent = `${counts.active ?? 0}／${counts.total ?? 0} 台`;
                wrap.appendChild(main);

                const parts = [];
                if (counts.inProgress > 0) parts.push(`處理中 ${counts.inProgress}`);
                if (counts.observing > 0) parts.push(`觀察 ${counts.observing}`);
                if (counts.open > 0) parts.push(`未處理 ${counts.open}`);
                if (counts.escalated > 0) parts.push(`上報 ${counts.escalated}`);
                if (counts.daySyncPending > 0) parts.push(`逐日同步中 ${counts.daySyncPending}`);

                if (parts.length > 0) {
                    const sub = document.createElement('div');
                    sub.className = 'text-muted small';
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
            className: 'text-end',
            render: r => (r.unrepliedDays == null ? '—' : `${r.unrepliedDays} 天`)
        },
        {
            title: '期限',
            render: r => (r.dueDate ? formatDate(r.dueDate) : '—')
        },
        {
            title: '來源',
            render: r => statusBadge(ORIGIN_LABELS[r.origin] || r.origin || '未知', 'neutral')
        },
        {
            title: '建立',
            render: r => formatDateTime(r.createdAt)
        },
        {
            title: '狀態',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 flex-wrap';
                if (r.closedAt) {
                    wrap.appendChild(statusBadge('已結案', 'neutral', { title: r.closedReason || '' }));
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
                }
                return wrap;
            }
        }
    ];

    renderTable(activeListContainer, {
        columns,
        rows: data.items,
        empty: { title: '沒有符合條件的交辦單' }
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

function renderLoadBoardTable(data) {
    const rows = (data.rows || []).slice().sort((a, b) => b.activeMembers - a.activeMembers);

    const columns = [
        {
            title: '處理人',
            render: r => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 flex-wrap';
                const link = document.createElement('a');
                link.href = appUrl('/handlers/' + r.userId);
                link.textContent = r.name || '';
                wrap.appendChild(link);
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
            render: r => formatNumber(r.activeWorkOrders)
        },
        {
            title: '進行中台數',
            className: 'text-end',
            render: r => formatNumber(r.activeMembers)
        },
        {
            title: '未回覆單',
            className: 'text-end',
            render: r => formatNumber(r.unrepliedWorkOrders)
        },
        {
            title: '逾期台數',
            className: 'text-end',
            render: r => formatNumber(r.overdueMembers)
        },
        {
            title: '近 7 天結案',
            className: 'text-end',
            render: r => formatNumber(r.closedLast7Days)
        },
        {
            title: '最舊進行中',
            render: r => (r.oldestActiveCreatedAt ? formatDate(r.oldestActiveCreatedAt) : '—')
        }
    ];

    renderTable(loadContainer, {
        columns,
        rows,
        empty: { title: '尚無資料' }
    });
}

// ── 待派頁籤 ───────────────────────────────────────────────────────────────

async function loadGaps() {
    renderLoading(gapsContainer, 5);

    const from = gapsFromInput.value;
    const to = gapsToInput.value;
    const params = new URLSearchParams();
    if (from) params.set('from', from);
    if (to) params.set('to', to);
    params.set('page', String(gapsPage));

    const data = await api.get(`/api/work-orders/gaps?${params}`);

    if (data.from) gapsFromInput.value = formatDate(data.from);
    if (data.to) gapsToInput.value = formatDate(data.to);

    if (data.tooLarge) {
        gapsNoteEl.textContent = '期間內尚未派出的出現點過多，請縮小期間';
        gapsContainer.replaceChildren();
        gapsPagerContainer.replaceChildren();
        loadedTabs.add('gaps');
        return;
    }

    gapsNoteEl.textContent = `期間 ${formatDate(data.from)} ~ ${formatDate(data.to)}，共 ${data.total} 個問題待派`;
    renderGapsTable(data);
    loadedTabs.add('gaps');
}

function renderGapsTable(data) {
    const columns = [
        {
            title: '問題',
            render: r => renderIssueCell(r.issueLabel, r.plainExplanation)
        },
        {
            title: '待派台數',
            className: 'text-end',
            render: r => formatNumber(r.gapHosts)
        },
        {
            title: '建議處理人',
            render: r => {
                if (!r.suggested || r.suggested.length === 0) {
                    return document.createTextNode('—');
                }
                const wrap = document.createElement('div');
                for (const s of r.suggested) {
                    const line = document.createElement('div');
                    line.textContent = `${s.name} ${s.hosts} 台 `;
                    const tag = document.createElement('span');
                    tag.className = 'text-muted small';
                    tag.textContent = s.willCreate ? '新建單' : '併入既有單';
                    line.appendChild(tag);
                    wrap.appendChild(line);
                }
                return wrap;
            }
        },
        {
            title: '無法派出',
            render: r => {
                const unassignable = r.unassignable || [];
                const unseen = r.unseenHostGroups || [];
                if (unassignable.length === 0 && unseen.length === 0) {
                    return document.createTextNode('—');
                }
                const wrap = document.createElement('div');
                for (const u of unassignable) {
                    const line = document.createElement('div');
                    const reasonText = UNASSIGNABLE_LABELS[u.reason] || u.reason;
                    line.textContent = `${reasonText} ${u.hosts} 台`;
                    wrap.appendChild(line);
                }
                if (unseen.length > 0) {
                    const sub = document.createElement('div');
                    sub.className = 'text-muted small';
                    sub.textContent = `無人看得到的主機群組：${unseen.join('、')}`;
                    wrap.appendChild(sub);
                }
                return wrap;
            }
        },
        {
            title: '已排除',
            render: r => {
                const lines = [];
                if (r.excluded) {
                    for (const [code, count] of Object.entries(r.excluded)) {
                        if (count > 0) {
                            const label = EXCLUDED_LABELS[code] || code;
                            lines.push(`${label} ${count} 台`);
                        }
                    }
                }
                if (lines.length === 0) {
                    return document.createTextNode('—');
                }
                const wrap = document.createElement('div');
                for (const text of lines) {
                    const line = document.createElement('div');
                    line.textContent = text;
                    wrap.appendChild(line);
                }
                return wrap;
            }
        }
    ];

    renderTable(gapsContainer, {
        columns,
        rows: data.rows,
        empty: { title: '沒有符合條件的問題' }
    });

    renderPagination(gapsPagerContainer, {
        page: data.page,
        totalPages: Math.ceil(data.total / 20),
        onPage: newPage => {
            gapsPage = newPage;
            guardLoad(gapsContainer, loadGaps);
        }
    });
}

// ── 頁籤切換與事件綁定 ─────────────────────────────────────────────────────

function loadTab(tabName) {
    if (tabName === 'active') {
        guardLoad(activeListContainer, loadActive);
    } else if (tabName === 'load') {
        guardLoad(loadContainer, loadLoadBoard);
    } else if (tabName === 'gaps') {
        guardLoad(gapsContainer, loadGaps);
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
    guardLoad(activeListContainer, loadActive);
};
activeStatusSelect.addEventListener('change', onActiveFilterChange);
activeSortSelect.addEventListener('change', onActiveFilterChange);
activeResumedCheckbox.addEventListener('change', onActiveFilterChange);

// 待派查詢
gapsQueryBtn.addEventListener('click', () => {
    gapsPage = 1;
    guardLoad(gapsContainer, loadGaps);
});

// 立即派工
gapsAutoDispatchBtn.addEventListener('click', async () => {
    const from = gapsFromInput.value;
    const to = gapsToInput.value;
    const confirmed = await confirmAction({
        title: '立即派工',
        message: `依自動派工規則，把期間 ${from} ~ ${to} 的待派問題建成交辦單。確定執行？`,
        confirmText: '確定派工',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;

    const restore = withBusy(gapsAutoDispatchBtn, '派工中…');
    try {
        const res = await api.post('/api/work-orders/auto-dispatch', {
            from: from || null,
            to: to || null
        });

        let msg = `已建立 ${res.createdOrders} 張、併入 ${res.mergedOrders} 張，共 ${res.assignedMembers} 台`;
        if (res.daySyncPendingCases > 0) {
            msg += '，逐日同步在背景完成';
        }

        const hasFailed = res.failedGroups && res.failedGroups.length > 0;
        if (hasFailed) {
            msg += `，${res.failedGroups.length} 組失敗`;
            toast(msg, 'warning');
        } else {
            toast(msg, 'success');
        }

        loadedTabs.delete('active');
        loadedTabs.delete('load');
        await loadGaps();
    } catch {
        // 錯誤訊息已由 api.js 顯示 toast
    } finally {
        restore();
    }
});

// ── 初始化 ─────────────────────────────────────────────────────────────────

async function init() {
    currentUser = await getCurrentUser();

    if (hasCapability(currentUser, 'Maintain')) {
        gapsAutoDispatchBtn.classList.remove('d-none');
    }

    bindTabs(tabsEl, { hash: true, onChange: handleTabChange });

    const activeBtn = tabsEl.querySelector('[data-tab].active');
    const initialTab = activeBtn ? activeBtn.dataset.tab : 'active';
    if (!loadedTabs.has(initialTab)) {
        loadTab(initialTab);
    }
}

init();
