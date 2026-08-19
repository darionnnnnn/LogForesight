/**
 * 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）：點某個處理人的名字，看這個人目前
 * 被交辦哪些項目——進行中案件（跨日追蹤的問題）＋被指派的風險日（推導後未結案為主）。
 *
 * 授權：全登入角色可查看任何人，資料以**檢視者**的可見範圍過濾（後端 HandlingService.
 * GetHandlerWorkload），與全站查詢頁同一套模型，不新增能力——處理人名字本來就全站可見，
 * 此頁沒有洩漏新資訊。
 *
 * **視角（docs/archive/FEEDBACK-11-PLAN.md §7）**：進行中案件預設「依問題」——一列一個問題、
 * 展開看受影響主機；被交辦單一類型問題橫跨多台時可一次查閱、一次回覆。「依主機」
 * 是改版前的逐案件列表（一列＝一台主機×一個問題），chip 隨時切回。
 * 分組在前端做：workload 已帶回逐案件的 source／eventId，不必為了換個排版多打一支 API。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { renderLoading, renderTable, renderChips, statCard, guardLoad } from '../core/ui.js';
import { formatNumber, formatUserName, riskBadge } from '../core/format.js';
import { openIssueStatusReplyModal } from './issue-status-reply.js';

const root = document.getElementById('handler-detail');
const userId = Number(root.dataset.userId);

let includeResolvedDays = false;
let caseView = 'issue';       // 'issue' | 'host'
let currentUser = null;
let latest = null;            // 最近一次的 workload 回應（切換視角時不重打 API）

async function load() {
    renderLoading(document.getElementById('handler-kpi'), 1);
    renderLoading(document.getElementById('handler-cases'), 3);
    renderLoading(document.getElementById('handler-days'), 3);

    const [data, user] = await Promise.all([
        api.get(`/api/handlers/${userId}/workload?includeResolvedDays=${includeResolvedDays}`),
        currentUser ? Promise.resolve(currentUser) : getCurrentUser()
    ]);
    currentUser = user;
    latest = data;

    renderHeader(data);
    renderKpi(data);
    renderViewChips();
    renderCases(data.cases);
    renderDays(data.days);
}

function renderViewChips() {
    renderChips(document.getElementById('handler-view-chips'), {
        items: [{ value: 'issue', label: '依問題' }, { value: 'host', label: '依主機' }],
        attr: 'view',
        activeValues: [caseView],
        multi: false,
        onToggle: value => {
            // 單選 chip 再點一次會回傳空字串（取消選取）——視角沒有「都不選」這個狀態
            caseView = value || caseView;
            renderViewChips();
            renderCases(latest.cases);
        }
    });
}

function renderHeader(data) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold';
    const name = formatUserName(data.displayName, data.account);
    title.textContent = data.active ? name : `${name}（已停用）`;
    body.appendChild(title);

    if (!data.active) {
        const note = document.createElement('div');
        note.className = 'text-muted small mt-1';
        note.textContent = '此帳號已停用；交辦紀錄是歷史事實，不因停用消失。';
        body.appendChild(note);
    }

    card.appendChild(body);
    document.getElementById('handler-header').replaceChildren(card);
}

function renderKpi(data) {
    const container = document.getElementById('handler-kpi');
    container.replaceChildren();

    const cards = [
        { value: data.openCaseCount, label: '進行中案件' },
        { value: data.unresolvedDayCount, label: '未結案風險日' },
        { value: data.overdueCount, label: '逾期', variant: data.overdueCount > 0 ? 'danger' : 'secondary' }
    ];

    for (const c of cards) {
        const col = document.createElement('div');
        col.className = 'col-4';
        col.appendChild(statCard({ value: formatNumber(c.value), label: c.label, variant: c.variant }));
        container.appendChild(col);
    }
}

function renderCases(cases) {
    if (caseView === 'issue') {
        renderCasesByIssue(cases);
        return;
    }

    renderTable(document.getElementById('handler-cases'), {
        columns: [
            { title: '主機', render: c => hostLink(c) },
            { title: '問題', render: c => c.issueLabel },
            { title: '狀態', render: c => statusText(c) },
            { title: '涵蓋範圍', render: c => `${c.firstLinkedDate} ~ ${c.lastLinkedDate}` },
            { title: '預計完成', className: 'text-nowrap', render: c => dueCell(c) }
        ],
        rows: cases,
        // 點列 → 最近掛接日的風險日詳情（該頁有完整處理動線）；主機名稱另有連結可到主機頁
        rowHref: c => `/records/${c.hostId}/${c.lastLinkedDate}`,
        empty: { title: '目前沒有進行中案件', hint: '被指派問題並建立案件後，會顯示在這裡。' }
    });
}

/**
 * 依問題視角（§7）：一列一個問題（Source＋EventId，與問題查詢 by-issue 同一把分組鍵），
 * 展開看受影響主機。看自己的工作頁時每列多一顆「回覆處理狀態」——一次套用到自己名下
 * 該問題的全部案件（共用 issue-status-reply.js，與問題查詢頁同一個 modal 與同一組必填規則）。
 */
function renderCasesByIssue(cases) {
    const groups = groupCasesByIssue(cases);
    // 「是自己的頁面」還不夠，還要「動得了」（體檢 H2）：manager 被指派後進自己的工作頁，
    // 過去看得到「回覆處理狀態」，按下去必定 403 並留下一筆 denied 稽核。
    // 與 records.js 的依問題視角是同一條規則，兩處要一起改才不會又漏一個入口。
    const isSelf = currentUser?.userId === userId && hasCapability(currentUser, 'Handle');

    renderTable(document.getElementById('handler-cases'), {
        columns: [
            { title: '問題', render: g => g.issueLabel },
            { title: '主機數', className: 'text-end', render: g => String(g.items.length) },
            { title: '狀態', render: g => issueStatusSummary(g) },
            { title: '涵蓋範圍', render: g => `${g.firstLinkedDate} ~ ${g.lastLinkedDate}` },
            { title: '最近逾期', className: 'text-nowrap', render: g => issueOverdueCell(g) },
            { title: '', className: 'text-end', render: g => (isSelf ? issueReplyButton(g) : '') }
        ],
        rows: groups,
        // 展開看受影響主機（與問題查詢 by-issue 同一個手勢）；rowDetail 與 rowHref 互斥，
        // 逐台的連結放在展開列裡
        rowDetail: g => issueHostsTable(g),
        empty: { title: '目前沒有進行中案件', hint: '被指派問題並建立案件後，會顯示在這裡。' }
    });
}

function groupCasesByIssue(cases) {
    const map = new Map();

    for (const item of cases) {
        // source/eventId 是自 IssueKey 反解的；萬一解析不出來（舊資料格式壞掉）就退回用
        // issueLabel 當鍵，至少不會把不同問題混成一組
        const key = item.source ? `${item.source}|${item.eventId}` : `label:${item.issueLabel}`;
        let group = map.get(key);
        if (!group) {
            group = {
                source: item.source,
                eventId: item.eventId,
                issueLabel: item.issueLabel,
                items: [],
                firstLinkedDate: item.firstLinkedDate,
                lastLinkedDate: item.lastLinkedDate
            };
            map.set(key, group);
        }

        group.items.push(item);
        if (item.firstLinkedDate < group.firstLinkedDate) group.firstLinkedDate = item.firstLinkedDate;
        if (item.lastLinkedDate > group.lastLinkedDate) group.lastLinkedDate = item.lastLinkedDate;
    }

    // 逾期的問題排最前面，其次主機數多的（一次處理多台的效益最高），再來是最近出現
    return [...map.values()].sort((a, b) =>
        (b.items.some(i => i.isOverdue) - a.items.some(i => i.isOverdue)) ||
        (b.items.length - a.items.length) ||
        b.lastLinkedDate.localeCompare(a.lastLinkedDate));
}

/** 「N 台處理中／M 台觀察中」——同一個問題在不同主機上可能各自不同狀態 */
function issueStatusSummary(group) {
    const counts = new Map();
    for (const item of group.items) {
        counts.set(item.statusText, (counts.get(item.statusText) ?? 0) + 1);
    }
    return [...counts].map(([text, count]) => `${count} 台${text}`).join('／');
}

function issueOverdueCell(group) {
    const overdue = group.items.filter(i => i.isOverdue);
    if (overdue.length === 0) return '';

    const span = document.createElement('span');
    span.className = 'text-danger fw-semibold';
    span.textContent = `${overdue.length} 台逾期`;
    return span;
}

function issueHostsTable(group) {
    const box = document.createElement('div');
    renderTable(box, {
        columns: [
            { title: '主機', render: c => hostLink(c) },
            { title: '狀態', render: c => statusText(c) },
            { title: '涵蓋範圍', render: c => `${c.firstLinkedDate} ~ ${c.lastLinkedDate}` },
            { title: '預計完成', className: 'text-nowrap', render: c => dueCell(c) },
            { title: '', className: 'text-end', render: c => detailLink(c) }
        ],
        rows: [...group.items].sort((a, b) => (b.isOverdue - a.isOverdue) || a.hostName.localeCompare(b.hostName)),
        empty: { title: '', hint: '' }
    });
    return box;
}

function detailLink(item) {
    const link = document.createElement('a');
    link.href = `/records/${item.hostId}/${item.lastLinkedDate}`;
    link.className = 'btn btn-sm btn-outline-primary';
    link.textContent = '去處理';
    return link;
}

function issueReplyButton(group) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-outline-secondary';
    btn.textContent = '回覆處理狀態';
    btn.title = '一次回覆這個問題在所有指派給您的主機上的處理狀態';
    btn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openIssueStatusReplyModal(group, load);
    });
    return btn;
}

function renderDays(days) {
    const container = document.getElementById('handler-days');
    renderTable(container, {
        columns: [
            { title: '日期', render: d => d.date },
            { title: '主機', render: d => hostLink(d) },
            { title: '風險', render: d => riskBadge(d.riskLevel) },
            { title: '狀態', render: d => statusText(d) },
            { title: '預計完成', className: 'text-nowrap', render: d => dueCell(d) }
        ],
        rows: days,
        rowHref: d => `/records/${d.hostId}/${d.date}`,
        empty: {
            title: includeResolvedDays ? '沒有被指派的風險日' : '目前沒有未結案的風險日',
            hint: includeResolvedDays ? '' : '勾選上方「顯示近 30 天已結案」檢視回顧紀錄。'
        }
    });
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

function hostLink(item) {
    const link = document.createElement('a');
    link.href = `/hosts/${item.hostId}`;
    link.textContent = item.hostName;
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

document.getElementById('toggle-resolved-days').addEventListener('change', event => {
    includeResolvedDays = event.target.checked;
    load();
});

guardLoad([
    document.getElementById('handler-kpi'),
    document.getElementById('handler-cases'),
    document.getElementById('handler-days')
], load);
