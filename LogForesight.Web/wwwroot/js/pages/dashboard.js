/**
 * 總覽儀表板（docs/WEB-SPEC.md §9.1）。
 *
 * 排版遵循 §8.2 視覺層級：有「重大」問題時該類別卡加紅邊；
 * 全數無風險時首屏顯示大字「無風險訊號」——沒事也要一眼確認是真的沒事。
 * 所有數字皆可下鑽（§8.4）。
 */

import { api, getCurrentUser, getDisplaySettings, hasCapability } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, icon, statCard } from '../core/ui.js';
import { formatNumber, CATEGORY_NAMES, SEVERITY_ORDER, severityCountBadge } from '../core/format.js';
import { categoryColors } from '../core/charts.js';
import { renderAiInline } from '../core/markdown-lite.js';

let currentDays = Number(localStorage.getItem('lf.dashboard.days')) || 7;

async function load() {
    renderLoading(document.getElementById('dashboard-categories'), 3);
    renderLoading(document.getElementById('dashboard-hosts'), 4);
    renderLoading(document.getElementById('dashboard-silent'), 2);
    renderLoading(document.getElementById('dashboard-group-risk'), 3);

    const [data, user, displaySettings] = await Promise.all([
        api.get(`/api/dashboard/summary?days=${currentDays}`),
        getCurrentUser(),
        getDisplaySettings()
    ]);

    document.getElementById('dashboard-range').textContent = `${data.from} ～ ${data.to}`;

    renderBanner(data);
    renderKpi(data, user, displaySettings);
    renderCategories(data);
    renderHosts(data);
    renderSilentHosts(data);
    renderGroupRisk(data);

    loadAiFocus();   // AI 今日焦點：非同步、失敗靜默，不擋主畫面
}

/**
 * AI 今日焦點（docs/archive/HISTORY.md §6 W1-1）：純加值。
 * AI 不可用或回空時整卡不顯示——不留「載入失敗」的殘影。
 */
async function loadAiFocus() {
    const container = document.getElementById('dashboard-ai-focus');
    container.replaceChildren();

    // AI 未設定時直接不打 today-focus（避免每次進儀表板都白發一次請求，docs/archive/FEEDBACK-7-PLAN.md）
    let status;
    try {
        status = await api.get('/api/ai/status', { silent: true });
    } catch {
        return;
    }
    if (!status?.available) return;

    let focus;
    try {
        focus = await api.get(`/api/ai/today-focus?days=${currentDays}`, { silent: true });
    } catch {
        return;
    }
    if (!focus || !focus.items || focus.items.length === 0) return;

    const card = document.createElement('div');
    card.className = 'lf-card mb-3';
    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2 d-flex align-items-center gap-2';
    const titleText = document.createElement('span');
    titleText.textContent = 'AI 今日焦點';
    title.appendChild(titleText);
    const hint = document.createElement('span');
    hint.className = 'lf-badge lf-badge--secondary';
    hint.textContent = 'AI 輔助，僅供排序參考';
    title.appendChild(hint);
    body.appendChild(title);

    const list = document.createElement('ol');
    list.className = 'mb-0 ps-3';
    for (const item of focus.items) {
        const li = document.createElement('li');
        li.className = 'mb-1';
        // AI 文字走 markdown-lite 行內渲染（S7 唯一出口）：DOM 組裝、不解析 HTML，
        // 「AI 產出不可信任為 HTML」的防線不變，只是 **粗體** 這類語法不再原樣顯示星號
        renderAiInline(li, item.text);
        if (item.link) {
            const link = document.createElement('a');
            link.href = item.link;   // 後端已過白名單驗證
            link.className = 'ms-2 small';
            link.textContent = '檢視 →';
            li.appendChild(link);
        }
        list.appendChild(li);
    }
    body.appendChild(list);
    card.appendChild(body);
    container.appendChild(card);
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

function renderKpi(data, user, displaySettings) {
    // docs/archive/FEEDBACK-3-PLAN.md #8：日風險等級顯示設定。後端已在 RecordRepository 這一咽喉
    // 過濾掉被隱藏等級的紀錄，data.mediumRiskDays 本來就會是 0——但「0」與「被藏起來」是
    // 兩件事，這裡整卡不顯示，不讓「0」被誤讀成「這期間真的沒有中風險日」
    const visibleDayRisk = new Set(displaySettings?.visibleDayRiskLevels ?? ['高', '中', '低']);

    const cards = [
        {
            label: '高風險日',
            value: data.highRiskDays,
            variant: data.highRiskDays > 0 ? 'danger' : 'secondary',
            // 日風險等級由批次分析算定，不受「設定 > 層級與顯示」的問題嚴重度設定影響（docs/archive/HISTORY.md #5）；
            // 顯示範圍另受「日風險等級顯示」設定影響（docs/archive/FEEDBACK-3-PLAN.md #8）
            hint: '日風險等級由批次分析（規則／趨勢／關聯訊號）算定，不受「層級與顯示」設定影響；顯示範圍受「日風險等級顯示」設定影響。',
            url: `/records?riskLevels=${encodeURIComponent('高')}&from=${data.from}&to=${data.to}`
        }
    ];

    if (visibleDayRisk.has('中')) {
        cards.push({
            label: '中風險日',
            value: data.mediumRiskDays,
            variant: data.mediumRiskDays > 0 ? 'warning' : 'secondary',
            hint: '日風險等級由批次分析（規則／趨勢／關聯訊號）算定，不受「層級與顯示」設定影響；顯示範圍受「日風險等級顯示」設定影響。',
            url: `/records?riskLevels=${encodeURIComponent('中')}&from=${data.from}&to=${data.to}`
        });
    }

    cards.push(
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
    );

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
        col.appendChild(statCard({
            value: formatNumber(card.value),
            label: card.label,
            variant: card.variant,
            url: card.url,
            hint: card.hint
        }));
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
    const grid = document.createElement('div');
    grid.className = 'lf-category-grid';

    for (const category of data.categories) {
        const link = document.createElement('a');
        link.className = 'lf-stat';
        // 分類卡的計數含低風險日的問題，下鑽顯式帶全部風險層級，卡片數字與點進去的筆數才對得上
        link.href = `/records?categories=${category.category}&riskLevels=${encodeURIComponent('高,中,低')}&from=${data.from}&to=${data.to}`;

        // 嚴重度驅動顯著性：命中「重大」旗標加紅邊、High 加黃邊（§8.2 原則 1；
        // docs/archive/HISTORY.md #1 B1 三級化後 criticalCount 恆為 0，改看 elevatesCount）
        const severityClass = category.elevatesCount > 0 ? ' lf-card--critical'
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
        grid.appendChild(link);
    }

    container.replaceChildren(grid);
}

/**
 * 嚴重度分解：顏色＋文字，不做只靠顏色區分的 UI。
 * 徽章顏色改走 format.js 的 severityCountBadge（單一標準）——這裡原本自己拼一份，
 * 把 Low 的底色寫成 secondary，與其餘頁面（format.js 的 SEVERITY_VARIANT＝neutral）不同色，
 * 是 docs/archive/HISTORY.md S11 記錄的實際分歧案例。
 */
function severityBreakdown(category) {
    // gap（非 me-1 margin）：卡片變窄需要換行時，gap 在換行處也維持間距，
    // margin 只顧橫向會在行尾留下不對稱空隙（docs/archive/FEEDBACK-3-PLAN.md #3）
    const wrap = document.createElement('span');
    wrap.className = 'd-flex flex-wrap gap-1';
    const counts = {
        High: category.highCount,
        Medium: category.mediumCount,
        Low: category.lowCount
    };

    for (const severity of SEVERITY_ORDER) {
        if (counts[severity] === 0) continue;
        const badge = severityCountBadge(severity, counts[severity]);
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

/**
 * 未回報主機改計數卡＋下鑽（§5.4 D-4）：兩千台規模下逐台列出可能是數百筆，
 * 改成一個大數字＋連結到主機頁的「未回報」篩選，該頁本來就有分頁與搜尋。
 */
function renderSilentHosts(data) {
    const container = document.getElementById('dashboard-silent');
    container.replaceChildren();

    if (data.silentHostsCount === 0) {
        renderEmpty(container, { title: '所有主機都正常回報', hint: `每台主機近兩天內都有執行紀錄。` });
        return;
    }

    const link = document.createElement('a');
    link.href = '/admin/hosts?status=silent';
    link.className = 'lf-stat d-block text-center py-3';

    const value = document.createElement('div');
    value.className = 'lf-stat__value text-danger';
    value.textContent = formatNumber(data.silentHostsCount);

    const label = document.createElement('div');
    label.className = 'lf-stat__label';
    label.textContent = '台主機超過 2 天未回報，點此檢視';

    link.append(value, label);
    container.appendChild(link);
}

/** 依群組風險概況（§5.4 D-4）：點列導向問題查詢並帶群組篩選，兩千台規模的主要動線是「先群組後下鑽」 */
function renderGroupRisk(data) {
    renderTable(document.getElementById('dashboard-group-risk'), {
        columns: [
            { title: '群組', render: g => g.groupName },
            { title: '主機數', className: 'text-end', render: g => formatNumber(g.hostCount) },
            { title: '高風險日', className: 'text-end', render: g => formatNumber(g.highRiskDays) },
            { title: '中風險日', className: 'text-end', render: g => formatNumber(g.mediumRiskDays) },
            { title: '未處理', className: 'text-end', render: g => formatNumber(g.unhandledCount) }
        ],
        rows: data.groupRisk,
        rowHref: g => `/records?groupIds=${g.groupId}&riskLevels=${encodeURIComponent('高,中')}&from=${data.from}&to=${data.to}`,
        empty: { title: '尚未設定任何主機群組', hint: '可於「群組與授權」頁建立主機群組並指派主機。' }
    });
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
