/**
 * 風險日詳情（docs/WEB-SPEC.md §9.3）。
 *
 * 兩層呈現（DB-PLAN 定案）：
 *   - 結構化層：重點問題（含趨勢註記）、關聯訊號、深入分析、資料完整性申報
 *   - 全文層：報告 txt 原樣以等寬字型呈現
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast, icon, confirmAction, withBusy, renderChips } from '../core/ui.js';
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

// 問題層級處理狀態選項（方案 B）：勾選面板內的具體狀態選擇，不含「未處理」——
// 未處理由取消勾選表示，不是面板裡的一個選項
const ISSUE_STATUS_OPTIONS = [
    { value: 'resolved', text: '已處理' },
    { value: 'wont_fix', text: '不處理' },
    { value: 'false_positive', text: '誤報' },
    { value: 'known_noise', text: '已知雜訊' }
];

// 依狀態決定備註欄的標籤與是否必填（§5.1 D-1 #6：依狀態動態調整欄位）
const NOTE_FIELD_BY_STATUS = {
    resolved: { label: '處理說明（選填）', required: false },
    wont_fix: { label: '不處理原因（必填）', required: true },
    false_positive: { label: '備註（選填）', required: false },
    known_noise: { label: '備註（選填，供日後回頭確認判斷依據）', required: false }
};

// 標「已知雜訊」時要不要提議建立抑制規則，取決於能否維護規則（Maintain）
let canMaintainRules = false;
// AI 判讀（W2）只在 AI 可用時提供
let aiAvailable = false;

async function load() {
    renderLoading(document.getElementById('detail-issues'), 5);

    const [detail, user, aiStatus] = await Promise.all([
        api.get(`/api/records/${hostId}/${date}`),
        getCurrentUser(),
        api.get('/api/ai/status', { silent: true }).catch(() => null)
    ]);
    currentDetail = detail;
    canMaintainRules = hasCapability(user, 'Maintain');
    aiAvailable = !!aiStatus?.available;

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
 * 報告全文預設收合（§5.1 D-1 #1）：一天的報告全文很長，多數時候只需要看結構化的
 * 重點問題，全文留給少數需要逐字核對的場合。展開狀態記 localStorage——
 * 常看全文的人不必每次進來都重新展開。
 */
function setupReportToggle() {
    const expanded = localStorage.getItem('lf.recordDetail.reportExpanded') === 'true';
    document.getElementById('report-body').classList.toggle('d-none', !expanded);
    document.getElementById('report-caret').classList.toggle('lf-collapse-caret--open', expanded);

    document.getElementById('report-toggle').addEventListener('click', () => {
        const body = document.getElementById('report-body');
        const nowOpen = body.classList.toggle('d-none') === false;
        document.getElementById('report-caret').classList.toggle('lf-collapse-caret--open', nowOpen);
        localStorage.setItem('lf.recordDetail.reportExpanded', String(nowOpen));
    });
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
        { title: '趨勢', className: 'lf-trend-cell', render: i => i.trendText },
        { title: '說明', render: i => knownIssueCell(i) },
        { title: '處理', render: i => statusControl(i) }
    ];
}

function severityNeutralBadge(text) {
    const span = document.createElement('span');
    span.className = 'lf-badge lf-badge--neutral';
    span.textContent = text;
    return span;
}

/**
 * 問題層級處理狀態控制（方案 B，§5.1 D-1 #2/#3/#6）。四條路徑：
 *   1. 無 Handle 能力 → 唯讀徽章
 *   2. 低風險且從未標記過 → 「不處理（預設）」＋確認不處理／調回未處理
 *   3. 從未標記過但同主機同簽章有已知雜訊記憶 → 「已知雜訊（自動）」＋調回未處理
 *   4. 其餘（含明確 open 與已結案）→ 勾選＋浮出面板
 */
function statusControl(issue) {
    if (!currentDetail.canHandle) {
        if (issue.handlingStatus === 'open' || (!issue.handlingStatus && !issue.isDefaultUnhandled && !issue.isAutoNoise))
            return document.createTextNode('未處理');
        if (issue.isDefaultUnhandled) return severityNeutralBadge('不處理（預設）');
        if (issue.isAutoNoise) return severityNeutralBadge('已知雜訊（自動）');
        return severityNeutralBadge(issue.handlingStatusText);
    }

    if (issue.isDefaultUnhandled) return defaultUnhandledControl(issue);
    if (issue.isAutoNoise) return autoNoiseControl(issue);
    return checkboxControl(issue);
}

/** 低風險預設不處理（§5.1 D-1 #2）：推導不落盤，使用者可確認或調回未處理 */
function defaultUnhandledControl(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-issue-status__actions';

    const badge = severityNeutralBadge('不處理（預設）');
    badge.title = '低風險問題預設不處理；沒有實際落盤，可在此確認或調回未處理';
    wrap.appendChild(badge);

    const confirmBtn = smallActionButton('確認不處理', () => setIssueStatus(issue, 'wont_fix', wrap, { note: null }));
    const reopenBtn = smallActionButton('調回未處理', () => setIssueStatus(issue, 'open', wrap, { forgetNoise: false }));
    wrap.append(confirmBtn, reopenBtn);
    return wrap;
}

/** 已知雜訊記憶自動判讀（§5.1 D-1 #3）：同主機同簽章之前標過已知雜訊，這次自動顯示 */
function autoNoiseControl(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-issue-status__actions';

    const badge = severityNeutralBadge('已知雜訊（自動）');
    badge.title = issue.noiseNote
        ? `依記憶自動判讀：${issue.noiseNote}`
        : '同主機同簽章先前標記過已知雜訊，本次自動套用同樣判讀';
    wrap.appendChild(badge);

    const reopenBtn = smallActionButton('調回未處理', async () => {
        // 兩個對話框各自誠實：第一個的「取消」是真的取消整個動作；
        // 第二個是獨立的是非題，「取消」＝合理的「不刪除」答案，不會被誤讀成中止操作
        const proceed = await confirmAction({
            title: '調回未處理',
            message: `將「${issue.source} ${issue.eventId}」標為未處理。`,
            confirmText: '調回未處理',
            confirmVariant: 'primary'
        });
        if (!proceed) return;

        const forget = await confirmAction({
            title: '是否同時刪除已知雜訊記憶？',
            message: '刪除後，同主機同簽章之後不會再自動判讀成雜訊，需要重新標記；' +
                '不刪除的話，下次出現這個問題仍會自動判讀成已知雜訊。' +
                (issue.noiseNote ? `（記憶備註：${issue.noiseNote}）` : ''),
            confirmText: '刪除記憶',
            confirmVariant: 'danger'
        });
        await setIssueStatus(issue, 'open', wrap, { forgetNoise: forget });
    });
    wrap.appendChild(reopenBtn);
    return wrap;
}

function smallActionButton(text, onClick) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-link p-0';
    btn.textContent = text;
    btn.addEventListener('click', event => { event.stopPropagation(); onClick(); });
    return btn;
}

/**
 * 一般勾選＋浮出面板控制（§5.1 D-1 #6）。勾選框反映「是否有結案類狀態」：
 *   - 未勾選 → 勾選時開面板（預設選「已處理」，需按確定才送出，不是勾了就存）
 *   - 已勾選（有結案狀態）→ 點擊可重新打開面板修改；取消勾選＝立即清除（可逆，不用二次確認）
 * 明確 open 狀態視覺上等同未勾選（都是「未處理」），但不會被自動推導蓋掉。
 */
function checkboxControl(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-issue-status';

    const isClosed = issue.handlingStatus && issue.handlingStatus !== 'open';

    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input lf-issue-status__check';
    check.checked = !!isClosed;
    check.title = isClosed ? '取消勾選＝清除處理標記' : '勾選＝標記已處理（可在面板改選其他狀態）';

    const label = document.createElement('span');
    label.textContent = isClosed ? issue.handlingStatusText : '未處理';

    const col = document.createElement('div');
    col.append(check, document.createElement('br'), label);
    wrap.appendChild(col);

    check.addEventListener('click', event => event.stopPropagation());
    label.addEventListener('click', event => event.stopPropagation());

    check.addEventListener('change', () => {
        if (check.checked) {
            wrap.appendChild(statusPanel(issue, wrap, label, check));
        } else {
            // 立即清除：可逆操作，不需要二次確認彈窗
            setIssueStatus(issue, '', wrap, {});
        }
    });

    // 已是結案狀態時，點文字/勾選本體之外的地方（例如整列）不應該打開面板——
    // 只有明確想「改」的人會去點勾選框；已勾選時再點一次勾選框只會觸發 change 清除，
    // 若想修改成別的狀態，提供一個「修改」小連結
    if (isClosed) {
        const editBtn = smallActionButton('修改', () => wrap.appendChild(statusPanel(issue, wrap, label, check)));
        col.appendChild(editBtn);
    }

    return wrap;
}

/** 浮出的狀態選擇面板：chip 選狀態＋依狀態動態調整的備註欄 */
function statusPanel(issue, wrap, label, check) {
    const existing = wrap.querySelector('.lf-issue-status__panel');
    if (existing) return existing;

    const panel = document.createElement('div');
    panel.className = 'lf-issue-status__panel';
    panel.addEventListener('click', event => event.stopPropagation());

    let selected = issue.handlingStatus && issue.handlingStatus !== 'open' ? issue.handlingStatus : 'resolved';

    const chips = document.createElement('div');
    chips.className = 'lf-toolbar__chips';
    panel.appendChild(chips);

    const fieldLabel = document.createElement('div');
    fieldLabel.className = 'lf-issue-status__field-label';
    panel.appendChild(fieldLabel);

    const note = document.createElement('textarea');
    note.className = 'form-control form-control-sm';
    note.rows = 2;
    note.value = issue.handlingStatus === selected ? (issue._localNote ?? '') : '';
    panel.appendChild(note);

    const ruleHint = document.createElement('div');
    ruleHint.className = 'lf-hint mt-1';
    panel.appendChild(ruleHint);

    function renderChipsAndField() {
        renderChips(chips, {
            items: ISSUE_STATUS_OPTIONS.map(o => ({ value: o.value, label: o.text })),
            attr: 'issueStatus',
            activeValues: [selected],
            multi: false,
            onToggle: value => { selected = value; renderChipsAndField(); }
        });

        const field = NOTE_FIELD_BY_STATUS[selected];
        fieldLabel.textContent = field.label;
        note.required = field.required;

        // 誤報且能維護規則時，提議調整規則（治本：規則本身可能過嚴）
        ruleHint.classList.toggle('d-none', !(selected === 'false_positive' && issue.ruleId && canMaintainRules));
        ruleHint.textContent = '';
        if (selected === 'false_positive' && issue.ruleId && canMaintainRules) {
            const link = document.createElement('a');
            link.href = `/admin/rules?search=${encodeURIComponent(issue.ruleId)}`;
            link.target = '_blank';
            link.textContent = '如果這條規則常常誤判，可以到規則維護調整判定條件';
            ruleHint.appendChild(link);
        }
    }
    renderChipsAndField();

    const actions = document.createElement('div');
    actions.className = 'd-flex gap-2 mt-2';

    const save = document.createElement('button');
    save.type = 'button';
    save.className = 'btn btn-sm btn-primary';
    save.textContent = '確定';
    save.addEventListener('click', () => {
        const field = NOTE_FIELD_BY_STATUS[selected];
        if (field.required && !note.value.trim()) {
            note.classList.add('is-invalid');
            return;
        }
        setIssueStatus(issue, selected, wrap, { note: note.value.trim() || null });
    });

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'btn btn-sm btn-outline-secondary';
    cancel.textContent = '取消';
    cancel.addEventListener('click', () => {
        panel.remove();
        // 取消時勾選框要回到操作前的狀態（若是「勾選開面板但取消」，勾選要退回未勾選）
        check.checked = !!(issue.handlingStatus && issue.handlingStatus !== 'open');
    });

    actions.append(save, cancel);
    panel.appendChild(actions);
    return panel;
}

/**
 * 送出問題狀態變更。wrap 是目前顯示在表格「處理」欄的控制項節點——
 * 成功後重新取回這個問題目前的狀態（含後端算好的低風險預設／已知雜訊自動判讀旗標），
 * 就地替換 wrap，不整頁重載；也不能只拿 PUT 的回應自己猜這兩個旗標，
 * 那套推導邏輯只在後端算一次（單一事實來源），前端用哪個值必須問後端要。
 */
async function setIssueStatus(issue, status, wrap, extra = {}) {
    try {
        await api.put(`/api/records/${hostId}/${date}/handling/issues`, {
            issueKey: issue.issueKey,
            status,
            note: extra.note ?? null,
            forgetNoise: !!extra.forgetNoise
        });

        const fresh = await api.get(`/api/records/${hostId}/${date}`, { silent: true });
        const updated = fresh.topIssues.find(i => i.issueKey === issue.issueKey);
        if (updated) Object.assign(issue, updated);
        if (extra.note !== undefined) issue._localNote = extra.note;

        wrap.replaceWith(statusControl(issue));
        renderProgress();

        toast(status ? `已標為「${issue.handlingStatusText || '未處理'}」` : '已清除處理標記', 'success');

        // 已知雜訊 → 提議建立抑制規則（治本）
        if (status === 'known_noise' && issue.ruleId && canMaintainRules) {
            await offerSuppression(issue);
        }
    } catch (error) {
        toast(error?.message || '更新失敗', 'danger');
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

/**
 * 重點問題旁的計數器：只顯示「已處理／未處理」（§5.1 D-1 #7），忽略其他標籤——
 * 這顆計數器要回答的是「還剩幾件要動手」，不是「標了幾件」：
 *   已處理＝真的標成 resolved 的問題數
 *   未處理＝從沒標記過、且不是低風險預設不處理／已知雜訊自動判讀的問題（含明確 open）
 * 不處理／誤報／已知雜訊／低風險預設不處理，兩邊都不計——那些是「已經有結論」，
 * 不是「還沒處理」，混進未處理只會讓使用者以為還有事要做。
 * 從 currentDetail.topIssues 現算，每次任何一項狀態變動後呼叫，不依賴後端往返。
 */
function renderProgress() {
    const el = document.getElementById('detail-progress');
    if (!el || !currentDetail) return;

    const issues = currentDetail.topIssues;
    if (issues.length === 0) { el.textContent = ''; return; }

    const resolved = issues.filter(i => i.handlingStatus === 'resolved').length;
    const unhandled = issues.filter(i =>
        i.handlingStatus === 'open' ||
        (i.handlingStatus === '' && !i.isDefaultUnhandled && !i.isAutoNoise)
    ).length;

    el.textContent = `已處理 ${resolved}／未處理 ${unhandled}`;
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

    renderProgress();

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
        header.className = `lf-issue-group__header lf-issue-group__header--${category.maxSeverity.toLowerCase()} d-flex align-items-center gap-2`;

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

    if (issue.sampleMessages?.length) wrap.appendChild(sampleMessagesTrigger(issue));

    return wrap;
}

/**
 * 範例訊息改 hover 泡泡（§5.1 D-1 #5）：原本的展開式 <pre> 一展開就佔掉整列高度，
 * 多筆問題同時攤開會讓畫面很亂。改成滑過才顯示的 popover——trigger 含 focus，
 * 點擊（取得焦點）會維持顯示，方便選取複製；點頁面其他地方失焦即關閉。
 * content 走 html:false（純文字），事件訊息是攻擊者可控字串，不能當 HTML 解析。
 */
function sampleMessagesTrigger(issue) {
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'btn btn-link btn-sm p-0 lf-no-print';
    trigger.textContent = '範例訊息';

    // eslint-disable-next-line no-undef
    new bootstrap.Popover(trigger, {
        trigger: 'hover focus',
        placement: 'top',
        html: false,
        customClass: 'lf-sample-popover',
        title: `範例訊息（${issue.sampleMessages.length}）`,
        content: issue.sampleMessages.join('\n---\n')
    });

    return trigger;
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
    // 未命中規則（無知識庫）但 AI 可用 → 提供「AI 判讀」（W2 主要服務「其他」類別）
    if (!g) return aiAvailable ? aiInterpretPanel(issue) : null;

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
 * 未命中規則問題的「AI 判讀」面板（W2）：一顆按鈕，點了才呼叫 AI（不自動呼叫，
 * 避免展開就打 AI）。AI 產出以 textContent 呈現；失敗靜默提示。
 */
function aiInterpretPanel(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-guidance';

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-outline-secondary';
    button.textContent = 'AI 判讀';

    const output = document.createElement('div');
    output.className = 'small mt-2';

    button.addEventListener('click', async event => {
        event.stopPropagation();
        const restore = withBusy(button, '判讀中');
        try {
            const params = new URLSearchParams({ hostId: String(hostId), date, issueKey: issue.issueKey });
            const result = await api.get(`/api/ai/interpret-issue?${params.toString()}`, { silent: true });
            if (!result || !result.text) {
                output.textContent = 'AI 目前無法判讀這個問題。';
                output.classList.add('text-muted');
            } else {
                output.replaceChildren();
                const label = document.createElement('span');
                label.className = 'lf-badge lf-badge--secondary me-2';
                label.textContent = 'AI 判讀';
                const text = document.createElement('span');
                text.textContent = result.text;
                output.append(label, text);
            }
        } catch {
            output.textContent = 'AI 目前無法判讀這個問題。';
            output.classList.add('text-muted');
        } finally {
            restore();
            button.disabled = true;   // 判讀過就不重複呼叫
        }
    });

    wrap.append(button, output);
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
    setupReportToggle();
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
