/**
 * 使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）：管理視角的單一使用者全貌。
 *
 * 兩支 API 各司其職，不重複第二套投影：
 * - `api/admin/users/{id}/detail`：基本資料／群組／能力／**此人**的可見主機／被指派歷程
 * - `api/handlers/{id}/workload`：進行中案件與被指派風險日（與工作頁同一份推導規則）
 *
 * 可見主機以**被查看者**為準，與 handler-detail.js（以檢視者可見範圍過濾）刻意不同。
 */

import { api, getCurrentUser } from '../core/api.js';
import { renderTable, renderLoading, statCard, guardLoad } from '../core/ui.js';
import { formatNumber, formatUserName, formatDateTime, riskBadge } from '../core/format.js';

const root = document.getElementById('user-detail');
const userId = Number(root.dataset.userId);

/**
 * 目前登入者。**必須在 load() 裡先取好再渲染**——`getCurrentUser()` 是 async，
 * 在同步的渲染函式裡直接呼叫會拿到 Promise（`promise.isServerAdmin` 是 undefined，
 * 任何比較都恆為 false，而且不會報錯）。本頁曾因此讓整個 §H4 的修復完全沒有生效。
 * 它有快取，放進既有的 Promise.all 是零額外成本。
 */
let currentUser = null;

async function load() {
    renderLoading(document.getElementById('user-visible-hosts'), 3);
    renderLoading(document.getElementById('user-open-work'), 3);
    renderLoading(document.getElementById('user-closed-work'), 2);
    renderLoading(document.getElementById('user-assignment-history'), 3);

    // 兩支端點平行取：detail 查無此人會 404，workload 也會，錯誤由 api.js 統一顯示
    const [detail, workload, me] = await Promise.all([
        api.get(`/api/admin/users/${userId}/detail`),
        api.get(`/api/handlers/${userId}/workload?includeResolvedDays=true`),
        getCurrentUser()
    ]);
    currentUser = me;

    renderHeader(detail);
    renderKpi(detail, workload);
    renderVisibleHosts(detail.visibleHosts);
    renderOpenWork(workload);
    renderClosedWork(detail.assignmentHistory);
    renderAssignmentHistory(detail.assignmentHistory);
}

function renderHeader(detail) {
    const user = detail.user;

    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const titleRow = document.createElement('div');
    titleRow.className = 'd-flex align-items-center flex-wrap gap-2';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold';
    title.textContent = formatUserName(user.displayName, user.account);
    titleRow.appendChild(title);

    titleRow.appendChild(badge(user.active ? '啟用' : '停用', user.active ? 'success' : 'secondary'));

    for (const group of detail.groups) {
        const text = group.active ? `${group.groupName}（${group.role}）` : `${group.groupName}（${group.role}．已停用）`;
        titleRow.appendChild(badge(text, group.active ? 'light' : 'secondary', !group.active));
    }
    if (detail.groups.length === 0) {
        titleRow.appendChild(badge('未指派群組', 'warning'));
    }

    const workLink = document.createElement('a');
    workLink.className = 'btn btn-sm btn-outline-primary ms-auto';
    workLink.href = `/handlers/${user.userId}`;
    workLink.textContent = '開啟工作頁';
    titleRow.appendChild(workLink);

    body.appendChild(titleRow);

    const meta = document.createElement('div');
    meta.className = 'text-muted small mt-2';
    meta.textContent = [
        user.email ? `Email：${user.email}` : 'Email：未填',
        `上次登入：${user.lastLoginAt ? formatDateTime(user.lastLoginAt) : '從未登入'}`,
        `能力：${detail.capabilities.length > 0 ? detail.capabilities.join('、') : '無（未指派群組且非任何主機的負責人）'}`
    ].join('　｜　');
    body.appendChild(meta);

    if (!user.active) {
        const note = document.createElement('div');
        note.className = 'text-muted small mt-1';
        note.textContent = '此帳號已停用：無法登入，且不具任何可見範圍；交辦紀錄是歷史事實，仍保留於下方。';
        body.appendChild(note);
    }

    card.appendChild(body);
    document.getElementById('user-detail-header').replaceChildren(card);
}

function badge(text, variant, muted = false) {
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${variant}${variant === 'light' ? ' border' : ''}`;
    if (muted) span.classList.add('text-decoration-line-through');
    span.textContent = text;
    return span;
}

/**
 * 這一頁同時呈現兩種可見範圍規則的資料（體檢 H4）：
 *   - `detail`（可見主機／被指派歷程）以**被查看者**為準
 *   - `workload`（處理中案件／逾期）以**檢視者**為準（§9.4a 既有規則）
 *
 * ViewAll 帳號下兩者結果相同，矛盾看不出來；但 serverAdmin 依 §6.2 沒有任何業務資料範圍，
 * 於是上方 KPI 顯示「處理中案件 0」、下方歷程卻列出 2 筆處理中——畫面在說謊。
 * serverAdmin 正好是正式部署第一天唯一能登入的帳號，也是 AD 停擺時的救援入口。
 *
 * 採體檢建議的第 2 案：**維持兩支 API 各司其職，但畫面要誠實**——標示工作負載是以
 * 檢視者範圍呈現；檢視者完全沒有業務範圍時直接換成說明卡，不顯示會被誤讀的「0」。
 */
function viewerHasNoBusinessScope() {
    return currentUser?.isServerAdmin === true;
}

function renderKpi(detail, workload) {
    const container = document.getElementById('user-detail-kpi');
    container.replaceChildren();

    const scopeNote = '以您的可見範圍計算，可能不完整';
    const noScope = viewerHasNoBusinessScope();

    const cards = [
        { value: detail.visibleHosts.length, label: '可見主機' },
        // 這兩張來自 workload（檢視者範圍），與左右兩張的母體不同，必須講出來
        { value: workload.openCaseCount, label: '處理中案件', hint: scopeNote, viewerScoped: true },
        { value: detail.assignmentHistory.filter(h => h.closed).length, label: '已結案案件' },
        {
            value: workload.overdueCount,
            label: '逾期',
            variant: workload.overdueCount > 0 ? 'danger' : 'secondary',
            hint: scopeNote,
            viewerScoped: true
        }
    ];

    for (const c of cards) {
        const col = document.createElement('div');
        col.className = 'col-6 col-lg-3';
        // 檢視者沒有任何業務範圍時，「0」是規則的產物而不是事實——顯示「—」並說明
        const noValue = c.viewerScoped && noScope;
        col.appendChild(statCard({
            value: noValue ? '—' : formatNumber(c.value),
            label: c.label,
            variant: noValue ? undefined : c.variant,
            hint: noValue ? '您的帳號沒有業務資料範圍，無法計算' : c.hint
        }));
        container.appendChild(col);
    }
}

function renderVisibleHosts(hosts) {
    document.getElementById('visible-host-count').textContent = `共 ${hosts.length} 台`;

    renderTable(document.getElementById('user-visible-hosts'), {
        columns: [
            { title: '主機', render: h => h.hostName },
            { title: 'IP', render: h => h.ipAddress ?? '' },
            { title: '作業系統', render: h => (h.os === 'linux' ? 'Linux' : 'Windows') },
            { title: '群組', render: h => h.groupNames.join('、') },
            { title: '可見來源', render: h => visibilitySourceCell(h) }
        ],
        rows: hosts,
        rowHref: h => `/hosts/${h.hostId}`,
        empty: {
            title: '這位使用者看不到任何主機',
            hint: '請於「群組與授權」頁把他所屬的部門群組授權到主機群組，或在主機頁把他設為負責人。'
        }
    });
}

/** 兩條路徑可同時成立，所以是兩顆徽章而不是一個值 */
function visibilitySourceCell(host) {
    const wrap = document.createElement('span');
    wrap.className = 'd-flex gap-1 flex-wrap';
    if (host.viaGroup) wrap.appendChild(badge('群組授權', 'light'));
    if (host.viaOwner) wrap.appendChild(badge('負責人', 'primary'));
    return wrap;
}

function renderOpenWork(workload) {
    const container = document.getElementById('user-open-work');
    container.replaceChildren();

    // 檢視者沒有業務範圍時，這一區塊必然是空的——顯示空表格會被讀成「這個人沒有工作」，
    // 而下方「被指派歷程」（以被查看者為準）同時列著處理中的案件，畫面自相矛盾（體檢 H4）
    if (viewerHasNoBusinessScope()) {
        const card = document.createElement('div');
        card.className = 'alert alert-secondary mb-0';
        card.textContent = '「處理中項目」以您的可見範圍計算，而您的帳號沒有業務資料範圍，'
            + '因此這裡不顯示任何項目。此人實際的交辦狀況請看下方「被指派歷程」，'
            + '或改用具備資料範圍的管理者帳號檢視。';
        container.appendChild(card);
        return;
    }

    const scopeNote = document.createElement('div');
    scopeNote.className = 'small text-muted mb-2';
    scopeNote.textContent = '以下項目以您的可見範圍顯示，可能不完整（下方「被指派歷程」以此人為準）。';
    container.appendChild(scopeNote);

    const casesTitle = sectionLabel(`進行中案件（${workload.cases.length}）`);
    container.appendChild(casesTitle);

    const casesBox = document.createElement('div');
    container.appendChild(casesBox);
    renderTable(casesBox, {
        columns: [
            { title: '主機', render: c => c.hostName },
            { title: '問題', render: c => c.issueLabel },
            { title: '狀態', render: c => statusText(c) },
            { title: '涵蓋範圍', render: c => `${c.firstLinkedDate} ~ ${c.lastLinkedDate}` },
            { title: '預計完成', className: 'text-nowrap', render: c => dueCell(c) }
        ],
        rows: workload.cases,
        rowHref: c => `/records/${c.hostId}/${c.lastLinkedDate}`,
        empty: { title: '目前沒有進行中案件', hint: '' }
    });

    // 未結案風險日：workload 帶回近 30 天已結案的日子，這裡只留推導後未結案的。
    // 判定沿用後端 HandlingStatuses.Unresolved 的定義（open／in_progress，觀察中在日層級
    // 推導時已映成 in_progress），不自己另立一套「不是 resolved 就算未結案」的規則——
    // 結案有四種狀態，那樣寫會把「不處理／誤報／已知雜訊」全部誤列成未結案。
    const UNRESOLVED = new Set(['open', 'in_progress']);
    const openDays = workload.days.filter(d => UNRESOLVED.has(d.derivedStatus));
    container.appendChild(sectionLabel(`未結案風險日（${openDays.length}）`));

    const daysBox = document.createElement('div');
    container.appendChild(daysBox);
    renderTable(daysBox, {
        columns: [
            { title: '日期', render: d => d.date },
            { title: '主機', render: d => d.hostName },
            { title: '風險', render: d => riskBadge(d.riskLevel) },
            { title: '狀態', render: d => statusText(d) },
            { title: '預計完成', className: 'text-nowrap', render: d => dueCell(d) }
        ],
        rows: openDays,
        rowHref: d => `/records/${d.hostId}/${d.date}`,
        empty: { title: '目前沒有未結案的風險日', hint: '' }
    });
}

function renderClosedWork(history) {
    const closed = history.filter(h => h.closed);

    renderTable(document.getElementById('user-closed-work'), {
        columns: [
            { title: '主機', render: h => h.hostName },
            { title: '問題', render: h => h.issueLabel },
            { title: '結論', render: h => h.statusText },
            { title: '涵蓋範圍', render: h => `${h.firstLinkedDate} ~ ${h.lastLinkedDate}` },
            { title: '結案時間', className: 'text-nowrap', render: h => (h.closedAt ? formatDateTime(h.closedAt) : '') }
        ],
        rows: closed,
        rowHref: h => (h.hostId ? `/records/${h.hostId}/${h.lastLinkedDate}` : null),
        empty: { title: '尚無已結案的案件', hint: '結案後的案件會保留在這裡，查得到「上次怎麼解的」。' }
    });
}

function renderAssignmentHistory(history) {
    renderTable(document.getElementById('user-assignment-history'), {
        columns: [
            { title: '交辦時間', className: 'text-nowrap', render: h => formatDateTime(h.createdAt) },
            { title: '交辦者', render: h => h.createdByAccount || '（系統）' },
            { title: '主機', render: h => h.hostName },
            { title: '問題', render: h => h.issueLabel },
            { title: '目前狀態', render: h => historyStatusCell(h) },
            { title: '涵蓋範圍', render: h => `${h.firstLinkedDate} ~ ${h.lastLinkedDate}` }
        ],
        rows: history,
        rowHref: h => (h.hostId ? `/records/${h.hostId}/${h.lastLinkedDate}` : null),
        empty: { title: '尚未被指派過任何問題案件', hint: '於問題查詢「依問題」視角或風險日詳情指派後，這裡會留下紀錄。' }
    });
}

function historyStatusCell(item) {
    const wrap = document.createElement('span');
    wrap.className = 'd-flex gap-1 align-items-center flex-wrap';
    wrap.appendChild(badge(item.statusText, item.closed ? 'secondary' : 'primary'));
    if (item.closed && item.closedAt) {
        const when = document.createElement('span');
        when.className = 'text-muted small';
        when.textContent = `結案於 ${formatDateTime(item.closedAt)}`;
        wrap.appendChild(when);
    }
    return wrap;
}

function sectionLabel(text) {
    const div = document.createElement('div');
    div.className = 'px-3 pt-3 pb-1 small fw-semibold text-muted';
    div.textContent = text;
    return div;
}

function statusText(item) {
    const span = document.createElement('span');
    span.textContent = item.derivedStatusText ?? item.statusText;
    if (item.isOverdue) span.className = 'text-danger fw-semibold';
    return span;
}

function dueCell(item) {
    if (!item.dueDate) return '';
    const span = document.createElement('span');
    span.className = item.isOverdue ? 'text-danger fw-semibold' : '';
    span.textContent = item.isOverdue ? `逾期 ${item.dueDate}` : item.dueDate;
    return span;
}

// backLink 只在 404 時出現（體檢 M1）：使用者打錯 id 或該帳號已被刪除時，
// 「請重新整理再試」永遠不會成功，要給的是回得去的出口
guardLoad([
    document.getElementById('user-visible-hosts'),
    document.getElementById('user-open-work'),
    document.getElementById('user-assignment-history')
], load, { backLink: { href: '/admin/users', text: '返回使用者清單' } });
