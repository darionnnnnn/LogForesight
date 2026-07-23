/**
 * 風險日詳情（docs/WEB-SPEC.md §9.3）。
 *
 * 兩層呈現（DB-PLAN 定案）：
 *   - 結構化層：重點問題（含趨勢註記）、關聯訊號、深入分析、資料完整性申報
 *   - 全文層：報告 txt 原樣以等寬字型呈現
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast, icon, confirmAction } from '../core/ui.js';
import { riskBadge, severityBadge, formatNumber, CATEGORY_NAMES } from '../core/format.js';
import { initHandlingPanel } from './handling-panel.js';

const root = document.getElementById('record-detail');
const hostId = Number(root.dataset.hostId);
const date = root.dataset.date;

// 嚴重度由重到輕；預設不顯示 Low——重點問題頁常被 Low 的雜訊淹沒，
// 真正要看的 Critical/High/Medium 反而被推到下面（與清單頁預設排除低風險同一個取捨）
const SEVERITY_ORDER = ['Critical', 'High', 'Medium', 'Low'];
const activeSeverities = new Set(['Critical', 'High', 'Medium']);
let currentDetail = null;

// 問題層級處理狀態選項（方案 B）：未處理＝清除標記，其餘為結案類
const ISSUE_STATUS_OPTIONS = [
    { value: '', text: '未處理' },
    { value: 'resolved', text: '已處理' },
    { value: 'wont_fix', text: '不處理' },
    { value: 'false_positive', text: '誤報' },
    { value: 'known_noise', text: '已知雜訊' }
];

// 標「已知雜訊」時要不要提議建立抑制規則，取決於能否維護規則（Maintain）
let canMaintainRules = false;

async function load() {
    renderLoading(document.getElementById('detail-issues'), 5);

    const [detail, user] = await Promise.all([
        api.get(`/api/records/${hostId}/${date}`),
        getCurrentUser()
    ]);
    currentDetail = detail;
    canMaintainRules = hasCapability(user, 'Maintain');

    renderHeader(currentDetail);
    renderSeverityFilter(currentDetail);
    renderIssues(currentDetail);
    renderAlerts(currentDetail);
    renderCategories(currentDetail);
    renderCoverage(currentDetail);

    await initHandlingPanel(hostId, date);

    if (currentDetail.hasReport) await loadReport();

    setupNextUnhandled();
}

/**
 * 「下一筆未處理」捷徑：處理完一天後不必手動返回清單再自己找下一筆。
 * 沿用問題查詢的緊急程度排序（未結案的高＋中風險日），跳到目前這筆之後的下一筆。
 * 目前這筆已不在未處理清單（剛結案）時，跳到清單第一筆；全部處理完則按鈕不顯示。
 */
async function setupNextUnhandled() {
    const button = document.getElementById('next-unhandled');
    if (!button) return;

    let items;
    try {
        const result = await api.get(
            `/api/records?statuses=open,in_progress&riskLevels=${encodeURIComponent('高,中')}&pageSize=200`,
            { silent: true });
        items = result.items;
    } catch {
        return;   // 取不到就不顯示捷徑，不打斷詳情頁
    }

    if (!items || items.length === 0) return;

    const currentIndex = items.findIndex(r => r.hostId === hostId && r.date === date);
    // 目前這筆還在未處理清單 → 取它之後的下一筆；已不在（剛結案）→ 取第一筆
    const next = currentIndex >= 0 ? items[currentIndex + 1] : items[0];
    if (!next) return;   // 這是最後一筆未處理

    button.href = `/records/${next.hostId}/${next.date}`;
    button.classList.remove('d-none');
}

function renderHeader(detail) {
    const container = document.getElementById('detail-header');

    const card = document.createElement('div');
    card.className = 'lf-card';
    if (detail.riskLevel === '高') card.classList.add('lf-card--critical');
    else if (detail.riskLevel === '中') card.classList.add('lf-card--warning');

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const top = document.createElement('div');
    top.className = 'd-flex align-items-center gap-3 mb-2 flex-wrap';

    const hostLink = document.createElement('a');
    hostLink.href = `/hosts/${detail.hostId}`;
    hostLink.className = 'fs-5 fw-semibold text-decoration-none';
    hostLink.textContent = detail.hostName;

    const dateSpan = document.createElement('span');
    dateSpan.className = 'text-muted';
    dateSpan.textContent = detail.date;

    top.append(hostLink, dateSpan, riskBadge(detail.riskLevel));

    if (!detail.aiAnalyzed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '統計模式（AI 未分析）';
        badge.title = 'AI 未呼叫或呼叫失敗，規則與趨勢告警照常運作';
        top.appendChild(badge);
    }

    body.appendChild(top);

    if (detail.headline) {
        const headline = document.createElement('div');
        headline.className = 'fs-5 mb-2';
        headline.textContent = detail.headline;
        body.appendChild(headline);
    }

    for (const [label, text] of [['狀況', detail.summary], ['趨勢', detail.trendAssessment], ['建議處置', detail.action]]) {
        if (!text) continue;
        const p = document.createElement('p');
        p.className = 'mb-2';
        const strong = document.createElement('strong');
        strong.textContent = `${label}：`;
        p.append(strong, document.createTextNode(text));
        body.appendChild(p);
    }

    const stats = document.createElement('div');
    stats.className = 'd-flex gap-4 mt-3 pt-3 border-top small text-muted';
    stats.innerHTML =
        `<span>錯誤 <strong>${formatNumber(detail.errorCount)}</strong></span>` +
        `<span>警告 <strong>${formatNumber(detail.warningCount)}</strong></span>` +
        `<span>稽核事件 <strong>${formatNumber(detail.auditEventCount)}</strong></span>`;
    body.appendChild(stats);

    if (detail.hostRoleDesc) {
        const role = document.createElement('div');
        role.className = 'small text-muted mt-2';
        role.textContent = `主機角色：${detail.hostRoleDesc}`;
        body.appendChild(role);
    }

    card.appendChild(body);
    container.replaceChildren(card);
}

function issueColumns() {
    return [
        { title: '來源 / Event', render: i => sourceCell(i) },
        { title: '次數', className: 'text-end', render: i => formatNumber(i.count) },
        { title: '嚴重度', render: i => severityBadge(i.severity) },
        { title: '時段', render: i => `${i.firstSeen}~${i.lastSeen}` },
        { title: '趨勢', render: i => i.trendText },
        { title: '說明', render: i => knownIssueCell(i) },
        { title: '處理', render: i => statusControl(i) }
    ];
}

/**
 * 問題層級處理狀態控制（方案 B）。可處理者顯示下拉即選即存；否則唯讀徽章。
 * 標「已知雜訊」且該問題命中規則、且使用者能維護規則時，接著提議一鍵建立抑制規則
 * ——這是「已知雜訊」的治本動作：同樣的雜訊之後不再進報告。
 */
function statusControl(issue) {
    if (!currentDetail.canHandle) {
        return issue.handlingStatus
            ? severityNeutralBadge(issue.handlingStatusText)
            : document.createTextNode('未處理');
    }

    const select = document.createElement('select');
    select.className = 'form-select form-select-sm lf-status-select';
    if (issue.handlingStatus) select.classList.add('lf-status-select--set');

    for (const option of ISSUE_STATUS_OPTIONS) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.text;
        el.selected = option.value === issue.handlingStatus;
        select.appendChild(el);
    }

    select.addEventListener('change', () => setIssueStatus(issue, select.value, select));
    // 避免點下拉時觸發整列的處置參考展開
    select.addEventListener('click', event => event.stopPropagation());
    return select;
}

function severityNeutralBadge(text) {
    const span = document.createElement('span');
    span.className = 'lf-badge lf-badge--neutral';
    span.textContent = text;
    return span;
}

async function setIssueStatus(issue, status, select) {
    const previous = issue.handlingStatus;
    select.disabled = true;
    try {
        const result = await api.put(`/api/records/${hostId}/${date}/handling/issues`, {
            issueKey: issue.issueKey,
            status
        });

        // 就地更新本地模型與畫面，不整頁重載
        issue.handlingStatus = result.status;
        issue.handlingStatusText = result.statusText;
        select.classList.toggle('lf-status-select--set', !!result.status);
        renderProgress(result.closedIssues, result.totalIssues);

        toast(status ? `已標為「${result.statusText}」` : '已清除處理標記', 'success');

        // 已知雜訊 → 提議建立抑制規則（治本）
        if (status === 'known_noise' && issue.ruleId && canMaintainRules) {
            await offerSuppression(issue);
        }
    } catch (error) {
        select.value = previous;   // 還原下拉，畫面與後端保持一致
        toast(error?.message || '更新失敗', 'danger');
    } finally {
        select.disabled = false;
    }
}

/** 標「已知雜訊」後的治本提議：把該規則在這台主機抑制，同樣雜訊之後不再進報告 */
async function offerSuppression(issue) {
    const ok = await confirmAction({
        title: '一併建立抑制規則？',
        message: `已標為已知雜訊。要不要在本主機（${currentDetail.hostName}）抑制規則「${issue.ruleId}」？` +
            '抑制後這個訊號不再拉高風險、不再進報告（事件仍照常紀錄）。',
        confirmText: '建立抑制',
        confirmVariant: 'primary'
    });
    if (!ok) return;

    try {
        await api.post(`/api/rules/${encodeURIComponent(issue.ruleId)}/suppressions`, {
            host: currentDetail.hostName,
            reason: `詳情頁標記已知雜訊：${issue.source} ${issue.eventId}`,
            days: null
        });
        toast('已建立抑制規則', 'success');
    } catch (error) {
        toast(error?.message || '建立抑制規則失敗，可到「規則維護」手動設定', 'warning');
    }
}

/** 當日處理進度「N/M 已處理」，顯示在重點問題卡標題旁 */
function renderProgress(closed, total) {
    const el = document.getElementById('detail-progress');
    if (!el) return;
    el.textContent = total > 0 ? `${closed}/${total} 已處理` : '';
}

/** 下鑽帶入的類別（§8.4）：從儀表板分類卡或查詢頁篩著類別點進來時，網址會帶 categories */
function highlightedCategories() {
    const csv = new URLSearchParams(location.search).get('categories');
    return new Set(csv ? csv.split(',') : []);
}

/**
 * 嚴重度篩選鈕：點選即重繪（免按查詢，比照儀表板期間鈕）。
 * 用 btn-group + active 沿用既有視覺語言，不另造樣式。
 */
function renderSeverityFilter(detail) {
    const container = document.getElementById('detail-severity-filter');
    if (!container) return;

    // 只列出當日實際存在的嚴重度，避免出現點了也沒東西的空鈕
    const present = SEVERITY_ORDER.filter(s => detail.topIssues.some(i => i.severity === s));
    if (present.length <= 1) {
        container.replaceChildren();
        return;
    }

    container.replaceChildren();
    for (const severity of present) {
        const count = detail.topIssues.filter(i => i.severity === severity).length;
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (activeSeverities.has(severity) ? ' active' : '');
        btn.textContent = `${severity} ${count}`;
        btn.addEventListener('click', () => {
            if (activeSeverities.has(severity)) activeSeverities.delete(severity);
            else activeSeverities.add(severity);
            btn.classList.toggle('active');
            renderIssues(currentDetail);
        });
        container.appendChild(btn);
    }
}

/**
 * 重點問題依類別分節，對齊報告 txt 的「■【類別】重點問題 N 項」——
 * 一天常同時有硬體＋資源＋服務的問題，合併成一張平面表會讓「這項屬於哪一類」
 * 從畫面上消失，儀表板分類卡下鑽進來就對不上自己點的類別。
 * 分節順序沿用 detail.categories（CategoryAggregator：最高嚴重度 → 問題數）。
 */
function renderIssues(detail) {
    const container = document.getElementById('detail-issues');

    if (detail.topIssues.length === 0) {
        renderEmpty(container, {
            title: '當日沒有重點問題',
            hint: '沒有任何事件簽章達到列入重點的門檻。'
        });
        return;
    }

    const highlighted = highlightedCategories();
    container.replaceChildren();

    // 當日處理進度：已結案問題數 / 總數（結案類狀態才算）
    const closed = detail.topIssues.filter(i => i.handlingStatus).length;
    renderProgress(closed, detail.topIssues.length);

    let shown = 0;
    let hidden = 0;

    for (const category of detail.categories) {
        const all = detail.topIssues.filter(i => i.category === category.category);
        if (all.length === 0) continue;

        const issues = all.filter(i => activeSeverities.has(i.severity));
        hidden += all.length - issues.length;
        if (issues.length === 0) continue;
        shown += issues.length;

        const section = document.createElement('section');
        section.className = 'lf-issue-group';
        section.dataset.category = category.category;   // 類型分布頁內導航的落點
        if (highlighted.has(category.category)) section.classList.add('lf-issue-group--hit');

        const header = document.createElement('div');
        header.className = 'lf-issue-group__header d-flex align-items-center gap-2';

        const title = document.createElement('span');
        title.className = 'fw-semibold';
        title.textContent = `【${CATEGORY_NAMES[category.category] ?? category.category}】重點問題 ${issues.length} 項`;
        header.append(title, severityBadge(category.maxSeverity));
        section.appendChild(header);

        const body = document.createElement('div');
        // 規則命中問題掛「處置參考」可展開列，讓「這問題怎麼辦」與問題本身直接對齊
        renderTable(body, { columns: issueColumns(), rows: issues, rowDetail: guidancePanel });
        section.appendChild(body);

        // 「其他」類別（未命中規則）沒有逐列處置參考，改在分節末尾附上 AI 深入分析——
        // 取代舊版獨立的深入分析卡，讓分析與所屬類別至少對齊在同一個區塊
        if (category.category === 'Other') {
            const analysis = otherAnalysis(detail);
            if (analysis) section.appendChild(analysis);
        }

        container.appendChild(section);
    }

    // 全被篩掉時給明確出口，不留白畫面讓人誤以為「這天沒問題」
    if (shown === 0) {
        renderEmpty(container, {
            title: `已隱藏全部 ${hidden} 項`,
            hint: '目前的嚴重度篩選未包含任何一項，點上方的嚴重度鈕加回。'
        });
        return;
    }

    // 有被篩掉的項數在底部提示，「沒看到」與「不存在」要分得清楚（README 的核心誠實原則）
    if (hidden > 0) {
        const note = document.createElement('div');
        note.className = 'text-muted small px-3 py-2 border-top';
        note.textContent = `另有 ${hidden} 項因嚴重度篩選未顯示。`;
        container.appendChild(note);
    }

    // 下鑽進來時直接捲到命中的第一個類別分節
    container.querySelector('.lf-issue-group--hit')?.scrollIntoView({ block: 'start', behavior: 'smooth' });
}

function sourceCell(issue) {
    const wrap = document.createElement('span');

    const main = document.createElement('span');
    main.textContent = `${issue.source} (${issue.eventId})`;
    wrap.appendChild(main);

    const log = document.createElement('div');
    log.className = 'small text-muted';
    log.textContent = issue.logName;
    wrap.appendChild(log);

    if (issue.suppressed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '已抑制';
        badge.title = '此規則已被本機抑制：只關掉通知與風險升級，事件仍照常紀錄';
        wrap.appendChild(badge);
    }

    return wrap;
}

function knownIssueCell(issue) {
    const wrap = document.createElement('div');

    if (issue.knownIssue) {
        const text = document.createElement('div');
        text.textContent = issue.knownIssue;
        wrap.appendChild(text);
    }

    // Security 事件的帳號/IP 彙總是入侵分析最關鍵的依據，不能折疊起來
    if (issue.keyDetails) {
        const details = document.createElement('div');
        details.className = 'small text-danger mt-1';
        details.textContent = issue.keyDetails;
        wrap.appendChild(details);
    }

    if (issue.distinctMessageCount > 1) {
        const distinct = document.createElement('div');
        distinct.className = 'small text-muted';
        distinct.textContent = `${issue.distinctMessageCount} 種相異訊息`;
        wrap.appendChild(distinct);
    }

    if (issue.sampleMessages?.length) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-link btn-sm p-0 lf-no-print';
        toggle.textContent = '範例訊息';

        const box = document.createElement('pre');
        box.className = 'report-text small mt-1 d-none';
        box.textContent = issue.sampleMessages.join('\n---\n');

        toggle.addEventListener('click', () => box.classList.toggle('d-none'));
        wrap.append(toggle, box);
    }

    return wrap;
}

/**
 * 關聯訊號與趨勢告警：這是**程式確定性比對**的結果，不是 AI 猜測。
 * console 用紅色🔗區塊呈現，Web 沿用同一套視覺語言。
 */
function renderAlerts(detail) {
    const container = document.getElementById('detail-alerts');
    container.replaceChildren();

    if (detail.correlationAlerts.length === 0 && detail.trendAlerts.length === 0) {
        renderEmpty(container, { title: '無關聯或趨勢訊號' });
        return;
    }

    if (detail.correlationAlerts.length > 0) {
        const box = document.createElement('div');
        box.className = 'alert alert-danger';

        const title = document.createElement('div');
        title.className = 'fw-semibold mb-2';
        title.textContent = '🔗 關聯訊號（程式確定性比對）';
        box.appendChild(title);

        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3 small';
        for (const alert of detail.correlationAlerts) {
            const item = document.createElement('li');
            item.textContent = alert;
            list.appendChild(item);
        }
        box.appendChild(list);
        container.appendChild(box);
    }

    if (detail.trendAlerts.length > 0) {
        const box = document.createElement('div');
        box.className = 'alert alert-warning mb-0';

        const title = document.createElement('div');
        title.className = 'fw-semibold mb-2';
        title.textContent = '頻率異常';
        box.appendChild(title);

        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3 small';
        for (const alert of detail.trendAlerts) {
            const item = document.createElement('li');
            item.textContent = alert;
            list.appendChild(item);
        }
        box.appendChild(list);
        container.appendChild(box);
    }
}

/**
 * 類型分布＝本日問題的目錄，不是離開本頁的出口。
 * 點某一類 → 捲到並高亮該類別的問題分節（頁內導航），使用者留在原地看到細節；
 * 想看「其他日期同類問題」的跨日需求，收進每列尾端的次要小連結（帶全部風險層級，
 * 免得問題查詢的預設隱藏低風險把該類的低風險日藏掉）。
 */
function renderCategories(detail) {
    const container = document.getElementById('detail-categories');

    if (detail.categories.length === 0) {
        renderEmpty(container, { title: '無分類資料' });
        return;
    }

    const list = document.createElement('div');
    for (const category of detail.categories) {
        const row = document.createElement('div');
        row.className = 'd-flex justify-content-between align-items-center py-2 border-bottom';

        const nav = document.createElement('button');
        nav.type = 'button';
        nav.className = 'btn btn-link p-0 text-body text-decoration-none text-start flex-grow-1 d-flex justify-content-between align-items-center';
        nav.addEventListener('click', () => scrollToCategory(category.category));

        const name = document.createElement('span');
        name.textContent = CATEGORY_NAMES[category.category] ?? category.category;

        const right = document.createElement('span');
        right.className = 'd-flex align-items-center gap-2';
        right.append(severityBadge(category.maxSeverity));

        const count = document.createElement('span');
        count.className = 'text-muted small';
        count.textContent = `${category.issueCount} 項 / ${formatNumber(category.totalEvents)} 筆`;
        right.appendChild(count);

        nav.append(name, right);

        // 跨日：帶條件回問題查詢（§8.4），次要動作、圖示連結不搶主視線
        const cross = document.createElement('a');
        cross.className = 'lf-no-print ms-2 text-muted';
        cross.href = `/records?categories=${category.category}&riskLevels=${encodeURIComponent('高,中,低')}&from=${detail.date}&to=${detail.date}`;
        cross.title = '在問題查詢中看這一類（可跨日）';
        cross.appendChild(icon('search'));

        row.append(nav, cross);
        list.appendChild(row);
    }

    container.replaceChildren(list);
}

/**
 * 捲到指定類別的問題分節並短暫高亮。若該類別的分節目前被嚴重度篩選整個隱藏
 * （例如只有 Low 問題、而 Low 被關掉），提示使用者放寬篩選，而不是靜默沒反應。
 */
function scrollToCategory(category) {
    const section = document.querySelector(`.lf-issue-group[data-category="${category}"]`);
    if (!section) {
        toast('這一類的問題目前被嚴重度篩選隱藏了，請在上方放寬篩選。', 'info');
        return;
    }

    section.scrollIntoView({ block: 'start', behavior: 'smooth' });
    section.classList.add('lf-issue-group--flash');
    setTimeout(() => section.classList.remove('lf-issue-group--flash'), 1200);
}

/**
 * 資料涵蓋率申報。README 的核心誠實原則：
 * 「沒告警 ≠ 沒問題，是沒看」——這在 Web 上必須同樣顯眼。
 */
function renderCoverage(detail) {
    const container = document.getElementById('detail-coverage');
    container.replaceChildren();

    const hasGap = detail.dataIncomplete || detail.securityLogAvailable === false;

    if (!hasGap) {
        const ok = document.createElement('div');
        ok.className = 'text-success';
        ok.textContent = '✓ 本日資料完整，所有偵測項目皆已執行。';
        container.appendChild(ok);
        return;
    }

    const box = document.createElement('div');
    box.className = 'alert alert-warning mb-0';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = '⚠ 本日部分偵測未執行';
    box.appendChild(title);

    const notes = [];
    if (detail.dataIncomplete) notes.push('Event Log 已被系統覆蓋，本日事件資料不完整。');
    if (detail.securityLogAvailable === false) notes.push('未能讀取 Security log（權限不足）。');

    for (const note of notes.concat(detail.uncoveredChecks)) {
        const p = document.createElement('div');
        p.className = 'small';
        p.textContent = `• ${note}`;
        box.appendChild(p);
    }

    const warning = document.createElement('div');
    warning.className = 'small fw-semibold mt-2';
    warning.textContent = '本日「沒有告警」不代表沒有問題，可能只是沒有檢查到。';
    box.appendChild(warning);

    container.appendChild(box);
}

/**
 * 問題列的處置參考面板（規則命中問題才有 guidance）。與 txt 報告「處置參考（知識庫）」
 * 同一份內容，掛在該問題列下方——不再讓使用者在獨立卡片與問題表之間玩多對多連連看。
 * 回傳 null 時 renderTable 不會替該列加展開列。
 */
function guidancePanel(issue) {
    const g = issue.guidance;
    if (!g) return null;

    const wrap = document.createElement('div');
    wrap.className = 'lf-guidance';

    if (g.explanation) {
        const label = document.createElement('div');
        label.className = 'lf-guidance__label';
        label.textContent = '說明';
        const text = document.createElement('div');
        text.className = 'small';
        text.textContent = g.explanation;
        wrap.append(label, text);
    }

    if (g.impact) {
        const label = document.createElement('div');
        label.className = 'lf-guidance__label';
        label.textContent = '影響';
        const text = document.createElement('div');
        text.className = 'small';
        text.textContent = g.impact;
        wrap.append(label, text);
    }

    appendList(wrap, '可能原因', g.likelyCauses, 'lf-guidance__label');
    appendList(wrap, '處置步驟', g.nextSteps, 'lf-guidance__label');

    return wrap;
}

/**
 * 「其他」類別（未命中規則）的 AI 深入分析。過去獨立成一張卡，與重點問題表多對多對不起來；
 * 現在渲染在【其他】分節末尾，至少與所屬類別對齊在同一區塊。規則命中的類別不走這裡——
 * 它們的處置參考已逐列掛在問題下（同一份知識庫來源，避免重複呈現）。
 */
function otherAnalysis(detail) {
    const dive = detail.deepDives.find(d => d.category === 'Other');
    if (!dive || dive.findings.length === 0) return null;

    const box = document.createElement('div');
    box.className = 'lf-issue-group__ai px-3 py-3 border-top';

    const heading = document.createElement('div');
    heading.className = 'small fw-semibold text-muted mb-2';
    heading.textContent = 'AI 深入分析（規則未涵蓋的問題）';
    box.appendChild(heading);

    for (const finding of dive.findings) {
        const item = document.createElement('div');
        item.className = 'border-start ps-3 mb-3';

        const problem = document.createElement('div');
        problem.className = 'fw-semibold';
        problem.textContent = finding.problem;
        item.appendChild(problem);

        if (finding.impact) {
            const impact = document.createElement('div');
            impact.className = 'small text-muted mb-1';
            impact.textContent = `影響：${finding.impact}`;
            item.appendChild(impact);
        }

        appendList(item, '可能原因', finding.likelyCauses);
        appendList(item, '處置步驟', finding.nextSteps);

        box.appendChild(item);
    }

    return box;
}

function appendList(parent, label, items, labelClass = 'small fw-semibold mt-1') {
    if (!items || items.length === 0) return;

    const title = document.createElement('div');
    title.className = labelClass;
    title.textContent = label;
    parent.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'small mb-1 ps-3';
    for (const item of items) {
        const li = document.createElement('li');
        li.textContent = item;
        list.appendChild(li);
    }
    parent.appendChild(list);
}

async function loadReport() {
    const content = await api.get(`/api/records/${hostId}/${date}/report`);
    if (!content) return;

    document.getElementById('report-card').classList.remove('d-none');
    document.getElementById('detail-report').textContent = content;
}

document.getElementById('btn-copy-report').addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText(document.getElementById('detail-report').textContent);
        toast('已複製報告全文', 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
});

document.getElementById('btn-print').addEventListener('click', () => window.print());

load();
