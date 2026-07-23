/**
 * 總覽儀表板（docs/WEB-SPEC.md §9.1）。
 *
 * 排版遵循 §8.2 視覺層級：有 Critical 時該類別卡置頂加紅邊；
 * 全數無風險時首屏顯示大字「無風險訊號」——沒事也要一眼確認是真的沒事。
 * 所有數字皆可下鑽（§8.4）。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, icon } from '../core/ui.js';
import { formatDateTime, formatNumber } from '../core/format.js';
import { categoryColors } from '../core/charts.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

let currentDays = Number(localStorage.getItem('lf.dashboard.days')) || 7;

async function load() {
    renderLoading(document.getElementById('dashboard-categories'), 3);
    renderLoading(document.getElementById('dashboard-hosts'), 4);
    renderLoading(document.getElementById('dashboard-silent'), 2);

    const [data, user] = await Promise.all([
        api.get(`/api/dashboard/summary?days=${currentDays}`),
        getCurrentUser()
    ]);

    document.getElementById('dashboard-range').textContent = `${data.from} ～ ${data.to}`;

    renderBanner(data);
    renderKpi(data, user);
    renderCategories(data);
    renderHosts(data);
    renderSilentHosts(data);
}

/** 全綠時明確說「沒事」——空白畫面無法讓人分辨「沒問題」與「沒載入」 */
function renderBanner(data) {
    const container = document.getElementById('dashboard-banner');

    if (data.highRiskDays > 0 || data.mediumRiskDays > 0) {
        container.replaceChildren();
        return;
    }

    const banner = document.createElement('div');
    banner.className = 'lf-card lf-card--ok mb-3';
    banner.innerHTML = `
        <div class="lf-card__body text-center py-4">
            <div class="fs-4 fw-semibold text-success mb-1">本期無風險訊號</div>
            <div class="text-muted">規則、趨勢與關聯層皆未偵測到異常。</div>
        </div>`;
    container.replaceChildren(banner);
}

function renderKpi(data, user) {
    const cards = [
        {
            label: '高風險日',
            value: data.highRiskDays,
            variant: data.highRiskDays > 0 ? 'danger' : 'secondary',
            url: `/records?riskLevels=${encodeURIComponent('高')}&from=${data.from}&to=${data.to}`
        },
        {
            label: '中風險日',
            value: data.mediumRiskDays,
            variant: data.mediumRiskDays > 0 ? 'warning' : 'secondary',
            url: `/records?riskLevels=${encodeURIComponent('中')}&from=${data.from}&to=${data.to}`
        },
        {
            label: '監控主機數',
            value: data.totalHosts,
            variant: 'secondary',
            url: null
        },
        {
            label: '涵蓋率缺口天數',
            value: data.coverageGapDays,
            variant: data.coverageGapDays > 0 ? 'warning' : 'secondary',
            hint: '資料不完整或 Security log 未讀取的日子',
            url: null
        }
    ];

    // 待辦：主管看到「有哪些風險」後的下一個問題是「有人在處理嗎」。
    // 後端只數本期的高＋中風險日，下鑽連結帶同一組條件，卡片數字與點進去的筆數才對得上
    const unresolved = data.todo.openCount + data.todo.inProgressCount;
    cards.push({
        label: data.todo.overdueCount > 0 ? `未處理（逾期 ${data.todo.overdueCount}）` : '未處理',
        value: unresolved,
        variant: data.todo.overdueCount > 0 ? 'danger' : (unresolved > 0 ? 'warning' : 'secondary'),
        url: `/records?statuses=open,in_progress&riskLevels=${encodeURIComponent('高,中')}&from=${data.from}&to=${data.to}`
    });

    if (data.pendingPermissionChanges > 0 && hasCapability(user, 'ConfirmPermission')) {
        cards.push({
            label: '權限異動待確認',
            value: data.pendingPermissionChanges,
            variant: 'warning',
            url: '/permission-changes'
        });
    }

    if (data.recentLoginFailures !== null && data.recentLoginFailures !== undefined) {
        cards.push({
            label: '24 小時登入失敗',
            value: data.recentLoginFailures,
            variant: data.recentLoginFailures > 0 ? 'warning' : 'secondary',
            url: '/audit?result=Denied'
        });
    }

    const container = document.getElementById('dashboard-kpi');
    container.replaceChildren();

    for (const card of cards) {
        const col = document.createElement('div');
        col.className = 'col-6 col-lg';

        const inner = document.createElement(card.url ? 'a' : 'div');
        inner.className = 'lf-stat';
        if (card.url) inner.href = card.url;

        const box = document.createElement('div');
        box.className = 'lf-card h-100' + (card.url ? ' lf-card--clickable' : '');
        box.innerHTML = `
            <div class="lf-card__body text-center">
                <div class="lf-stat__value text-${card.variant}"></div>
                <div class="lf-stat__label"></div>
            </div>`;
        box.querySelector('.lf-stat__value').textContent = formatNumber(card.value);
        box.querySelector('.lf-stat__label').textContent = card.label;
        if (card.hint) box.title = card.hint;

        inner.appendChild(box);
        col.appendChild(inner);
        container.appendChild(col);
    }
}

function renderCategories(data) {
    const container = document.getElementById('dashboard-categories');

    if (data.categories.length === 0) {
        renderEmpty(container, {
            title: '本期沒有問題訊號',
            hint: '規則層、趨勢層與關聯層皆未命中。'
        });
        return;
    }

    const colors = categoryColors();
    const row = document.createElement('div');
    row.className = 'row g-3';

    for (const category of data.categories) {
        const col = document.createElement('div');
        col.className = 'col-6 col-md-4 col-xl-3';

        const link = document.createElement('a');
        link.className = 'lf-stat';
        // 分類卡的計數含低風險日的問題，下鑽顯式帶全部風險層級，卡片數字與點進去的筆數才對得上
        link.href = `/records?categories=${category.category}&riskLevels=${encodeURIComponent('高,中,低')}&from=${data.from}&to=${data.to}`;

        // 嚴重度驅動顯著性：Critical 加紅邊、High 加黃邊（§8.2 原則 1）
        const severityClass = category.criticalCount > 0 ? ' lf-card--critical'
            : category.highCount > 0 ? ' lf-card--warning' : '';

        const card = document.createElement('div');
        card.className = `lf-card lf-card--clickable h-100${severityClass}`;
        card.innerHTML = `
            <div class="lf-card__body">
                <div class="d-flex align-items-center gap-2 mb-2">
                    <span class="d-inline-block rounded-circle" style="width:10px;height:10px"></span>
                    <span class="fw-semibold"></span>
                </div>
                <div class="lf-stat__value"></div>
                <div class="lf-stat__label mb-2"></div>
                <div class="small"></div>
            </div>`;

        card.querySelector('span.rounded-circle').style.background = colors[category.category] ?? '#adb5bd';
        card.querySelector('span.fw-semibold').textContent = CATEGORY_NAMES[category.category] ?? category.category;
        card.querySelector('.lf-stat__value').textContent = formatNumber(category.issueCount);
        card.querySelector('.lf-stat__label').textContent = `個問題．${category.affectedHosts} 台主機`;
        card.querySelector('.small').replaceChildren(severityBreakdown(category));

        link.appendChild(card);
        col.appendChild(link);
        row.appendChild(col);
    }

    container.replaceChildren(row);
}

/** 嚴重度分解：顏色＋文字，不做只靠顏色區分的 UI */
function severityBreakdown(category) {
    const wrap = document.createElement('span');
    const parts = [
        { count: category.criticalCount, label: 'Critical', variant: 'danger' },
        { count: category.highCount, label: 'High', variant: 'warning' },
        { count: category.mediumCount, label: 'Medium', variant: 'info' },
        { count: category.lowCount, label: 'Low', variant: 'secondary' }
    ];

    for (const part of parts) {
        if (part.count === 0) continue;
        const badge = document.createElement('span');
        badge.className = `lf-badge lf-badge--${part.variant} me-1`;
        badge.textContent = `${part.label} ${part.count}`;
        wrap.appendChild(badge);
    }
    return wrap;
}

function renderHosts(data) {
    renderTable(document.getElementById('dashboard-hosts'), {
        columns: [
            { title: '主機', render: h => hostLink(h) },
            { title: '高風險', className: 'text-end', render: h => String(h.highRiskDays) },
            { title: '中風險', className: 'text-end', render: h => String(h.mediumRiskDays) },
            { title: '關聯訊號', className: 'text-end', render: h => correlationCell(h) },
            { title: '最新狀況', render: h => h.latestHeadline }
        ],
        rows: data.hostRanking,
        empty: { title: '本期沒有風險主機', hint: '所有主機的分析結果皆為低風險。' }
    });
}

function correlationCell(host) {
    if (host.correlationDays === 0) return '';

    // 關聯訊號＝程式確定性比對出的攻擊鏈/故障鏈，比單一事件更值得警戒，用紅色鏈結圖示延續 console 的視覺語言
    const span = document.createElement('span');
    span.className = 'text-danger fw-semibold d-inline-flex align-items-center gap-1 justify-content-end';
    span.appendChild(icon('link-45deg'));
    const count = document.createElement('span');
    count.textContent = String(host.correlationDays);
    span.appendChild(count);
    span.title = '有攻擊鏈／故障鏈的關聯訊號';
    return span;
}

function hostLink(host) {
    const link = document.createElement('a');
    link.href = host.hostId > 0 ? `/hosts/${host.hostId}` : '#';
    link.textContent = host.hostName;
    return link;
}

function renderSilentHosts(data) {
    renderTable(document.getElementById('dashboard-silent'), {
        columns: [
            { title: '主機', render: h => hostLink(h) },
            { title: '最近回報', render: h => silentCell(h) }
        ],
        rows: data.silentHosts,
        empty: { title: '所有主機都正常回報', hint: '每台主機近兩天內都有執行紀錄。' }
    });
}

function silentCell(host) {
    const span = document.createElement('span');
    span.className = 'text-danger';
    span.textContent = host.lastReportAt
        ? `${formatDateTime(host.lastReportAt)}（${host.daysSilent} 天前）`
        : '尚未回報';
    return span;
}

for (const button of document.querySelectorAll('[data-days]')) {
    button.addEventListener('click', () => {
        currentDays = Number(button.dataset.days);
        localStorage.setItem('lf.dashboard.days', String(currentDays));

        for (const other of document.querySelectorAll('[data-days]')) {
            other.classList.toggle('active', other === button);
        }
        load();
    });
}

// 還原上次選的期間（§8.6-1 篩選記憶）
for (const button of document.querySelectorAll('[data-days]')) {
    button.classList.toggle('active', Number(button.dataset.days) === currentDays);
}

load();
