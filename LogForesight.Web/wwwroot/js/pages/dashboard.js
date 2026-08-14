/**
 * 總覽儀表板（docs/WEB-SPEC.md §9.1）。
 *
 * 排版遵循 §8.2 視覺層級：有「重大」問題時該類別卡加紅邊；
 * 全數無風險時首屏顯示大字「無風險訊號」——沒事也要一眼確認是真的沒事。
 * 所有數字皆可下鑽（§8.4）。
 */

import { api, getCurrentUser, getDisplaySettings, hasCapability } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, icon, statCard, guardLoad } from '../core/ui.js';
import { formatNumber, CATEGORY_NAMES, SEVERITY_ORDER, severityCountBadge, severityBadge } from '../core/format.js';
import { categoryColors } from '../core/charts.js';
import { renderAiInline } from '../core/markdown-lite.js';

let currentDays = Number(localStorage.getItem('lf.dashboard.days')) || 7;

async function load() {
    // serverAdmin 沒有業務資料檢視能力（§6.2 最小授權），儀表板會是一片空白——
    // 改顯示引導卡說明用途，而不是讓人以為壞掉（§1）
    const currentUser = await getCurrentUser();
    if (currentUser.isServerAdmin) {
        renderServerAdminGuide();
        return;
    }

    renderLoading(document.getElementById('dashboard-categories'), 3);
    renderLoading(document.getElementById('dashboard-issues'), 3);
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
    renderTopIssues(data);
    renderHosts(data);
    renderSilentHosts(data);
    renderGroupRisk(data);

    loadAiFocus();   // AI 今日焦點：非同步、失敗靜默，不擋主畫面
    startRunActivityWatch();  // 執行中告示（S-3）：同上，失敗靜默
}

/**
 * 執行中告示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）。
 *
 * 分析與網站跑在同一個行程（本輪定案不拆獨立 worker），6000 台環境下一跑就是數小時，
 * 期間整站回應變慢是**設計上接受的代價**。代價本身沒問題，「使用者不知道為什麼」才有問題——
 * 沒有這行告示，慢就等於故障，變成客服電話。
 *
 * 只在真的有在跑時才顯示；跑完自動消失並停止輪詢，不留殘影也不長期打 API。
 */
let runActivityTimer = null;

function startRunActivityWatch() {
    if (runActivityTimer) return;   // 切換期間會重新 load()，不要疊出第二個計時器
    refreshRunActivity();
    // 30 秒：這行字只需要「大致上是對的」，分析動輒數小時，更密集只是白發請求
    runActivityTimer = setInterval(refreshRunActivity, 30000);
}

async function refreshRunActivity() {
    const container = document.getElementById('dashboard-run-activity');
    if (!container) return;

    let activity;
    try {
        activity = await api.get('/api/run-activity', { silent: true });
    } catch {
        // 純加值資訊，失敗就當作沒在跑——不要為了一行告示在畫面上留錯誤訊息
        container.replaceChildren();
        return;
    }

    if (!activity?.isRunning) {
        container.replaceChildren();
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'alert alert-info d-flex align-items-center gap-2 py-2 mb-3';
    bar.setAttribute('role', 'status');       // 進行中狀態用 status（polite），不是 alert——
    bar.setAttribute('aria-live', 'polite');  // 這不是需要打斷讀屏的緊急訊息

    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm flex-shrink-0';
    spinner.setAttribute('aria-hidden', 'true');
    bar.appendChild(spinner);

    // 有分母才講「第 N/M」——Total=0 代表還在掃描/清理階段，這時報進度是假的
    const progressText = activity.total > 0
        ? `分析進行中（第 ${formatNumber(activity.done)}／${formatNumber(activity.total)} ${activity.unitText || ''}）`
        : '分析進行中';

    const text = document.createElement('span');
    text.textContent = `${progressText}，畫面回應可能較慢。資料仍是完整的，分析完成後會自動恢復。`;
    bar.appendChild(text);

    container.replaceChildren(bar);
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

/**
 * serverAdmin 引導卡（§1）：這個帳號是本地救援/引導帳號，依 §6.2 刻意只有維護與稽核能力、
 * 不看業務資料。說明用途、如何測全站（測試模式下用 demo-admin 免密碼登入）、正式環境建帳號步驟。
 */
function renderServerAdminGuide() {
    // 隱藏其他區塊（連同靜態卡片標題），只留引導卡——否則會殘留「風險類型」等空標頭
    for (const id of ['dashboard-kpi', 'dashboard-ai-focus']) {
        document.getElementById(id)?.replaceChildren();
    }
    for (const id of ['dashboard-categories', 'dashboard-hosts', 'dashboard-group-risk']) {
        document.getElementById(id)?.closest('.row')?.classList.add('d-none');
    }
    for (const el of document.querySelectorAll('[data-days]')) el.closest('.btn-group')?.classList.add('d-none');

    const container = document.getElementById('dashboard-banner');
    const card = document.createElement('div');
    card.className = 'lf-card';
    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold mb-2';
    title.textContent = '您以本機救援帳號（serverAdmin）登入';
    body.appendChild(title);

    const intro = document.createElement('p');
    intro.className = 'text-muted mb-3';
    intro.textContent = '此帳號的用途是「指派 admin 成員」與「AD／資料庫停擺時的救援入口」，'
        + '依最小授權原則刻意只有維護與稽核能力，不檢視業務資料——所以儀表板、問題查詢、報表對它是空白，這是正常的。';
    body.appendChild(intro);

    const list = document.createElement('ul');
    list.className = 'mb-0';
    for (const [strongText, rest] of [
        ['要檢視業務資料（儀表板／問題查詢／報表）：', '請以具 admin 權限的帳號登入。測試模式下可用帳號 demo-admin（顯示名稱「測試管理員」）直接登入，免密碼。'],
        ['要指派正式管理者：', '到左側「系統管理 › 使用者」新增或編輯帳號，將對象加入 admin 群組後，即可用該帳號登入操作全站。'],
        ['正式環境：', '請將 Auth:Provider 改為 Ldap 並依 docs/WEB-SPEC.md §5／§6.2 設定，本測試管理員不會在正式模式下建立。']
    ]) {
        const li = document.createElement('li');
        li.className = 'mb-2';
        const strong = document.createElement('strong');
        strong.textContent = strongText;
        li.append(strong, document.createTextNode(rest));
        list.appendChild(li);
    }
    body.appendChild(list);

    card.appendChild(body);
    container.replaceChildren(card);
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
    // 大數字改問題口徑（回饋十九輪批次D2，外部審查§一-2）：「未處理 1,340」（風險日數）
    // 在兩千台環境對使用者等於沒有資訊，改成「未處理問題 X 個」——有幾個不同的問題要處理，
    // 副標才是影響台數與風險日數（data.todo 的既有日數，退居輔助角色）
    const todoExtra = document.createElement('div');
    todoExtra.className = 'small text-muted';
    todoExtra.textContent = `影響 ${formatNumber(data.issueTodo.affectedHostCount)} 台．未處理風險日 ${formatNumber(data.todo.openCount + data.todo.inProgressCount)}`;
    cards.push({
        label: data.issueTodo.overdueIssueCount > 0 ? `未處理問題（逾期 ${data.issueTodo.overdueIssueCount}）` : '未處理問題',
        value: data.issueTodo.openIssueCount,
        variant: data.issueTodo.overdueIssueCount > 0 ? 'danger' : (data.issueTodo.openIssueCount > 0 ? 'warning' : 'secondary'),
        url: `/records?view=issue&statuses=open&riskLevels=${encodeURIComponent('高,中')}&from=${data.from}&to=${data.to}`,
        extra: todoExtra
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
            hint: card.hint,
            extra: card.extra
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
        // 分類卡的計數含低風險日的問題，下鑽顯式帶全部風險層級，卡片數字與點進去的筆數才對得上。
        // view=issue（回饋十四輪 UI-2）：這張卡片本身就是「依風險類型看問題」的入口，理應直接
        // 落在依問題視角——與 renderTopIssues 的下鑽連結（見下方，同一個 view=issue 慣例）
        // 保持一致，否則帶著 categories 參數進頁會被 §10 的「帶參數預設回明細」規則接住，
        // 使用者點一個問題類別的卡片，看到的卻是逐筆明細而非依問題分組。
        link.href = `/records?view=issue&categories=${category.category}&riskLevels=${encodeURIComponent('高,中,低')}&from=${data.from}&to=${data.to}`;

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
                <div class="small text-muted mb-1"></div>
                <div class="small"></div>
            </div>`;

        card.querySelector('span.rounded-circle').style.background = colors[category.category] ?? 'var(--lf-cat-other)';
        card.querySelector('span.fw-semibold').textContent = CATEGORY_NAMES[category.category] ?? category.category;
        // 大數字＝去重風險資訊筆數（同一台主機同一個問題連續多天只算一筆）；
        // 小字＝期間累計出現次數（主機×日），兩者常常差很多，同時看才不會誤以為問題變多了
        // （回饋十九輪批次D，外部審查點名的「看不出真正嚴重程度」）
        card.querySelector('.lf-stat__value').textContent = formatNumber(category.riskItemCount);
        card.querySelector('.lf-stat__value').title = '去重後的風險資訊筆數：同一台主機同一個問題連續多天只算一筆';
        card.querySelector('.lf-stat__label').textContent = `個問題．${category.affectedHosts} 台主機`;
        const cumulative = card.querySelector('.small.text-muted');
        cumulative.textContent = `期間累計 ${formatNumber(category.cumulativeCount)} 筆（主機×日）`;
        cumulative.title = '主機×日的原始出現次數加總——數字大不代表問題多，只代表拖得久';
        card.querySelector('.small:not(.text-muted)').replaceChildren(severityBreakdown(category));

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

/**
 * 重點問題 Top 5（docs/archive/FEEDBACK-11-PLAN.md §8-1）：儀表板原本只有「類別」與「主機」兩個維度，
 * 看不出「現在最該處理哪幾個問題」。點列下鑽問題查詢的**依問題**視角（帶 source＋eventId），
 * 那裡有處理概況、指派與統一標記——這張卡只負責把注意力導過去。
 */
function renderTopIssues(data) {
    // 背景整理中的提示放在**表格容器之外**——renderTable 會 replaceChildren，
    // 塞在同一個容器裡會被下一次渲染吃掉
    renderStatsPendingNote('dashboard-issues-pending', data);
    renderConcludedNote('dashboard-issues-concluded', data.concludedTopIssueCount);

    renderTable(document.getElementById('dashboard-issues'), {
        columns: [
            { title: '問題', render: i => issueNameCell(i) },
            { title: '嚴重度', render: i => issueSeverityCell(i) },
            { title: '主機數', className: 'text-end', render: i => issueHostCell(i) },
            { title: '未處理', className: 'text-end', render: i => issueOpenCell(i) },
            { title: '涵蓋範圍', className: 'text-nowrap', render: i => issueSpanCell(i) },
            { title: '出現密度', className: 'text-end text-nowrap', render: i => issueDensityCell(i) },
            { title: '變化', className: 'text-end text-nowrap', render: i => issueChangeCell(i) },
            { title: '總次數', className: 'text-end', render: i => formatNumber(i.totalCount) }
        ],
        rows: data.topIssues,
        // 帶 view=issue 明確指定視角（帶參數時預設會回到明細視角），期間沿用本頁的區間
        rowHref: i => `/records?view=issue&source=${encodeURIComponent(i.source)}&eventId=${i.eventId}` +
                      `&from=${data.from}&to=${data.to}`,
        empty: { title: '本期沒有重點問題', hint: '期間內沒有偵測到任何問題事件。' }
    });
}

/**
 * 已有結論的問題被排除提示（§10.6）：全部主機都已有結論的問題不佔用重點清單版面，
 * 但悄悄少幾筆會讓人以為問題變少了——卡底把數字誠實說出來。
 * 同 renderStatsPendingNote，容器在表格外，不會被 renderTable 的 replaceChildren 清掉。
 */
function renderConcludedNote(containerId, concludedCount) {
    const el = document.getElementById(containerId);
    if (!el) return;

    if (!concludedCount) {
        el.classList.add('d-none');
        el.textContent = '';
        return;
    }

    el.className = 'small text-muted';
    el.textContent = `另有 ${concludedCount} 個問題已有結論（未列入）`;
}

/**
 * 「統計中」提示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md G2）。
 *
 * 遷移或回填未完成時，問題排行的數字**偏低但看起來完全正常**——那是本專案最忌諱的
 * 「靜默給錯數字」。旗標與說明由後端合成（使用者不必分辨是哪一種背景工作），
 * 這裡只負責顯示；沒有 pending 時不留任何殘留節點。
 */
function renderStatsPendingNote(containerId, data) {
    const el = document.getElementById(containerId);
    if (!el) return;

    el.replaceChildren();
    if (!data.issueStatsPending) {
        el.classList.add('d-none');
        return;
    }

    el.className = 'alert alert-warning py-2 mb-2 small';
    el.textContent = data.issueStatsPendingHint ?? '問題統計整理中，數字可能不完整。';
}

/** 問題名稱＋「新」徽章：本期新出現是「今天有什麼不一樣」最直接的訊號（§10.3） */
function issueNameCell(issue) {
    const wrap = document.createElement('div');

    const title = document.createElement('div');
    title.className = 'fw-semibold d-flex align-items-center gap-1';
    const name = document.createElement('span');
    name.textContent = `${issue.source} (${issue.eventId})`;
    title.appendChild(name);

    if (issue.isNew) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--warning';
        badge.textContent = '新';
        badge.title = '前一個等長期間完全沒有出現過';
        title.appendChild(badge);
    }
    wrap.appendChild(title);

    const category = document.createElement('div');
    category.className = 'small text-muted';
    category.textContent = CATEGORY_NAMES[issue.category] ?? issue.category;
    wrap.appendChild(category);

    return wrap;
}

/** 嚴重度＋「重大」旗標（§10.2 維度 1：這個旗標過去只在詳情頁看得到） */
function issueSeverityCell(issue) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1';
    wrap.appendChild(severityBadge(issue.maxSeverity));

    if (issue.elevatesDayRisk) {
        const flag = document.createElement('span');
        flag.className = 'lf-badge lf-badge--danger';
        flag.textContent = '重大';
        flag.title = '此問題曾命中「命中即列為高風險日」的規則旗標';
        wrap.appendChild(flag);
    }
    return wrap;
}

/**
 * 主機數＋影響率（§10.2 維度 2）。
 * 「600 台」在 2000 台環境是 30%、在 50 台環境是全滅——**絕對值無法跨環境解讀**，
 * 也無法在同一張榜上比較不同規模的部門。
 */
function issueHostCell(issue) {
    const wrap = document.createElement('div');

    const count = document.createElement('div');
    count.textContent = formatNumber(issue.hostCount);
    wrap.appendChild(count);

    if (issue.hostRatio > 0) {
        const ratio = document.createElement('div');
        ratio.className = 'small text-muted';
        ratio.textContent = `${Math.round(issue.hostRatio * 100)}%`;
        ratio.title = '影響率＝主機數 ÷ 可見主機總數';
        wrap.appendChild(ratio);
    }
    return wrap;
}

/**
 * 未處理主機數（§10.6）：主機數答的是「影響多大」，這一欄答「現在還有幾台真的要處理」——
 * 兩者常常不一樣（很多台早就有結論，只是還沒退出這張榜的排序）。0 台時淡化顯示，
 * 不用紅字製造「這裡也要處理」的錯誤急迫感。
 */
function issueOpenCell(issue) {
    const wrap = document.createElement('div');
    const count = document.createElement('div');
    count.textContent = formatNumber(issue.openHostCount);
    count.className = issue.openHostCount > 0 ? 'text-danger fw-semibold' : 'text-muted';
    wrap.appendChild(count);
    return wrap;
}

/** 涵蓋範圍：首見 ~ 最近出現（需求的「期間跨度」）＋是否仍在發生 */
function issueSpanCell(issue) {
    const wrap = document.createElement('div');

    const span = document.createElement('div');
    span.className = 'lf-mono small';
    span.textContent = issue.firstSeen === issue.lastSeen
        ? issue.firstSeen
        : `${issue.firstSeen} ~ ${issue.lastSeen}`;
    wrap.appendChild(span);

    const hint = document.createElement('div');
    hint.className = 'small';
    if (issue.daysSinceLastSeen === 0) {
        hint.className += ' text-danger fw-semibold';
        hint.textContent = '昨日仍在發生';
    } else {
        hint.className += ' text-muted';
        hint.textContent = `${issue.daysSinceLastSeen} 天前`;
    }
    wrap.appendChild(hint);

    return wrap;
}

/** 出現密度：天天都有（背景值）還是零星爆發（§10.3）。文字為主、密度條為輔 */
function issueDensityCell(issue) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1 justify-content-end';

    const text = document.createElement('span');
    text.className = 'lf-mono small';
    text.textContent = `${issue.activeDays}/${issue.periodDays}`;
    wrap.appendChild(text);

    const ratio = issue.periodDays > 0 ? issue.activeDays / issue.periodDays : 0;
    const bar = document.createElement('span');
    bar.className = 'lf-density';
    bar.title = `期間 ${issue.periodDays} 天內出現 ${issue.activeDays} 天（${Math.round(ratio * 100)}%）`;
    const fill = document.createElement('span');
    fill.className = 'lf-density__fill';
    fill.style.width = `${Math.max(4, Math.round(ratio * 100))}%`;
    bar.appendChild(fill);
    wrap.appendChild(bar);

    return wrap;
}

/**
 * 變化幅度（§10.3）：與前一個等長期間相比的主機數增減。
 * 這一欄是把「哪些問題最普遍」變成「哪些問題正在惡化」的關鍵——
 * DCOM 這種每天都一樣的雜訊在這裡會顯示「—」，而真正在擴散的問題會跳出來。
 */
function issueChangeCell(issue) {
    const span = document.createElement('span');
    span.className = 'small';

    if (issue.isNew) {
        span.className += ' text-danger fw-semibold';
        span.textContent = '本期新增';
        return span;
    }

    if (issue.previousHostCount === 0) {
        span.className += ' text-muted';
        span.textContent = '—';
        return span;
    }

    const delta = (issue.hostCount - issue.previousHostCount) / issue.previousHostCount;
    const percent = Math.round(delta * 100);
    if (percent === 0) {
        span.className += ' text-muted';
        span.textContent = '持平';
    } else if (percent > 0) {
        span.className += ' text-danger';
        span.textContent = `↑${percent}%`;
        span.title = `前期 ${issue.previousHostCount} 台 → 本期 ${issue.hostCount} 台`;
    } else {
        span.className += ' text-success';
        span.textContent = `↓${Math.abs(percent)}%`;
        span.title = `前期 ${issue.previousHostCount} 台 → 本期 ${issue.hostCount} 台`;
    }
    return span;
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

guardLoad([
    document.getElementById('dashboard-categories'),
    document.getElementById('dashboard-hosts'),
    document.getElementById('dashboard-silent'),
    document.getElementById('dashboard-group-risk')
], load);
