/**
 * 處理人員工作頁（docs/FEEDBACK-4-PLAN.md §6）：點某個處理人的名字，看這個人目前
 * 被交辦哪些項目——進行中案件（跨日追蹤的問題）＋被指派的風險日（推導後未結案為主）。
 *
 * 授權：全登入角色可查看任何人，資料以**檢視者**的可見範圍過濾（後端 HandlingService.
 * GetHandlerWorkload），與全站查詢頁同一套模型，不新增能力——處理人名字本來就全站可見，
 * 此頁沒有洩漏新資訊。
 */

import { api } from '../core/api.js';
import { renderLoading, renderTable, statCard } from '../core/ui.js';
import { formatNumber, riskBadge } from '../core/format.js';

const root = document.getElementById('handler-detail');
const userId = Number(root.dataset.userId);

let includeResolvedDays = false;

async function load() {
    renderLoading(document.getElementById('handler-kpi'), 1);
    renderLoading(document.getElementById('handler-cases'), 3);
    renderLoading(document.getElementById('handler-days'), 3);

    const data = await api.get(`/api/handlers/${userId}/workload?includeResolvedDays=${includeResolvedDays}`);

    renderHeader(data);
    renderKpi(data);
    renderCases(data.cases);
    renderDays(data.days);
}

function renderHeader(data) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold';
    title.textContent = data.active ? data.displayName : `${data.displayName}（已停用）`;
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

function renderDays(days) {
    const container = document.getElementById('handler-days');
    renderTable(container, {
        columns: [
            { title: '日期', render: d => dateLink(d) },
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

function dateLink(item) {
    const link = document.createElement('a');
    link.href = `/records/${item.hostId}/${item.date}`;
    link.textContent = item.date;
    link.addEventListener('click', event => event.stopPropagation());
    return link;
}

document.getElementById('toggle-resolved-days').addEventListener('change', event => {
    includeResolvedDays = event.target.checked;
    load();
});

load();
