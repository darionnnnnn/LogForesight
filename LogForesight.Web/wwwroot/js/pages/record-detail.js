/**
 * 風險日詳情（docs/WEB-SPEC.md §9.3）。
 *
 * 兩層呈現（DB-PLAN 定案）：
 *   - 結構化層：重點問題（含趨勢註記）、關聯訊號、深入分析、資料完整性申報
 *   - 全文層：報告 txt 原樣以等寬字型呈現
 */

import { api, getCurrentUser, hasCapability, getDisplaySettings } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { renderTable, renderLoading, renderEmpty, toast, icon, confirmAction, confirmActionWithReason, withBusy, showDetailModal, guardLoad, helpIcon, button } from '../core/ui.js';
import { riskBadge, severityBadge, elevatesBadge, formatNumber, formatUserName, CATEGORY_NAMES, severityName, SEVERITY_ORDER, todayLocal, isAiRetryPending } from '../core/format.js';
import { initHandlingPanel, refreshSelection } from './handling-panel.js';
import { initChatPanel, updateIssueOptions } from './chat-panel.js';
import { renderAiText, renderAiInline } from '../core/markdown-lite.js';

const root = document.getElementById('record-detail');
const hostId = Number(root.dataset.hostId);
const date = root.dataset.date;

// 預設只顯示系統設定「未處理計算」勾選的層級——重點問題頁常被
// 未勾選層級的雜訊淹沒，真正要看的反而被推到下面（與清單頁預設排除低風險同一個取捨）。
// 只在頁面首次載入時初始化一次：批次套用觸發的重載（onBatchSaved → load()）不能
// 把使用者手動調整過的篩選狀態蓋回預設值。
let activeSeverities = null;
let currentDetail = null;
let currentDisplaySettings = null;

// 批次套用改版（2026-07-27）：勾選純粹代表「這列要包含在下一次批次套用」，
// 與這列目前的處理狀態脫鉤——狀態改在右側「處理狀態」區塊填一次套用給全部勾選的問題
// （見 handling-panel.js），不再逐列各自跳出面板。每次 load() 重新整理時清空。
const selectedIssueKeys = new Set();

// 標「已知雜訊」時要不要提議建立抑制規則，取決於能否維護規則（Maintain）
let canMaintainRules = false;
// AI 判讀（W2）只在 AI 可用時提供
let aiAvailable = false;

/**
 * 顯示範圍（docs/archive/FEEDBACK-10-PLAN.md §8）。每個問題先歸入四個互斥的桶：
 *
 *   pending    未處理：沒有結案標記、也沒有人在處理
 *   mine       我處理中：進行中案件的處理人是我，或我把它標成處理中／觀察中
 *   others     他人處理中：進行中案件的處理人是別人
 *   done       已完成：結案四態＋「不處理（預設）」＋「已知雜訊（自動）」
 *
 * 四個選項是這四個桶的組合。**預設不顯示他人處理中**——同一個問題同時有兩個人在動，
 * 後標的會蓋掉先標的，那是這個功能要防的事（後端另有一道拒絕，見 IssueHandlingCommandService）。
 * 「已完成」平常維持既有的「分節底部收合列」呈現，只有選「僅已完成」時才平鋪出來。
 */
const SCOPE_OPTIONS = [
    { value: 'pending', label: '待處理', buckets: ['pending', 'mine'], collapseDone: true },
    { value: 'all', label: '顯示所有問題', buckets: ['pending', 'mine', 'others'], collapseDone: true },
    { value: 'hide-done', label: '隱藏已完成', buckets: ['pending', 'mine', 'others'], collapseDone: false },
    { value: 'done-only', label: '僅已完成', buckets: ['done'], collapseDone: false }
];

let currentScope = 'pending';
let currentUserId = null;

/** 結案類：與後端 IssueHandlingStatuses.IsClosed 同一組值 */
const CLOSED_STATUSES = new Set(['resolved', 'wont_fix', 'false_positive', 'known_noise']);

/** 單一問題屬於哪個桶（四桶互斥，判定順序即優先序：已完成 → 誰在處理 → 未處理） */
function issueBucket(issue) {
    if (CLOSED_STATUSES.has(issue.handlingStatus) || issue.isDefaultUnhandled || issue.isAutoNoise) return 'done';

    if (issue.caseHandlerId) {
        return issue.caseHandlerId === currentUserId ? 'mine' : 'others';
    }
    // escalated（回饋十八輪批次G）：上報中比照 in_progress——有人在管、還沒有結論
    if (issue.handlingStatus === 'in_progress' || issue.handlingStatus === 'observing' ||
        issue.handlingStatus === 'escalated') return 'mine';

    return 'pending';
}

/** 這個問題是不是「別人正在處理」——決定能不能被勾選／改狀態 */
function isHandledByOthers(issue) {
    return issueBucket(issue) === 'others';
}

function currentScopeOption() {
    return SCOPE_OPTIONS.find(o => o.value === currentScope) ?? SCOPE_OPTIONS[0];
}

/** 通過顯示範圍篩選（與嚴重度篩選是 AND 關係，兩者都在 visibleTopIssues 套用） */
function inCurrentScope(issue) {
    return currentScopeOption().buckets.includes(issueBucket(issue));
}

async function load() {
    renderLoading(document.getElementById('detail-issues'), 5);
    selectedIssueKeys.clear();

    const [detail, user, aiStatus, displaySettings] = await Promise.all([
        api.get(`/api/records/${hostId}/${date}`),
        getCurrentUser(),
        api.get('/api/ai/status', { silent: true }).catch(() => null),
        getDisplaySettings()
    ]);
    // SiteHidden 模式的過濾已由後端 RecordRepository 統一套用（docs/archive/HISTORY.md S1）：
    // detail.topIssues 拿到的就是可見子集，不需要（也不該）在前端再做一次特判過濾
    currentDetail = detail;
    currentDisplaySettings = displaySettings;
    canMaintainRules = hasCapability(user, 'Maintain');
    // 「這個案件是不是我的」要靠 userId 比對（§8）；ServerAdmin 沒有對應的 WebUser，
    // userId 為 0，比對永遠不成立——它本來就看不到業務資料，行為正確
    currentUserId = user.userId;
    aiAvailable = !!aiStatus?.available;

    const allowed = allowedSeverities();
    if (activeSeverities === null) {
        const initial = detail.unhandledSeverities?.length ? detail.unhandledSeverities : ['Critical', 'High', 'Medium'];
        activeSeverities = new Set(initial.filter(s => allowed.has(s)));
    } else {
        for (const s of activeSeverities) {
            if (!allowed.has(s)) activeSeverities.delete(s);
        }
    }

    renderHeader(currentDetail);
    renderSeverityFilter(currentDetail);
    renderScopeFilter(currentDetail);
    renderIssues(currentDetail);
    renderAlerts(currentDetail);
    renderCategories(currentDetail);
    renderCoverage(currentDetail);
    // 詢問 AI 的下拉只列出目前嚴重度篩選後仍可見的問題（docs/archive/HISTORY.md #4）
    initChatPanel(hostId, date, visibleTopIssues(), aiAvailable);

    await initHandlingPanel(hostId, date, () => selectedIssueKeys, onBatchSaved, {
        canMaintainRules,
        // §7：案件授與檢視時，處理面板收斂成「只標記自己被交辦的問題」
        caseGrantOnly: currentDetail.caseGrantOnly
    });

    if (currentDetail.hasReport) await loadReport();

    setupNextUnhandled();
}

/** 批次套用成功後：重載頁面，已知雜訊另接治本提議（建立抑制規則） */
async function onBatchSaved(result) {
    await load();
    if (result?.status === 'known_noise') await offerBatchSuppression(result.issueKeys);
}

/**
 * 報告全文預設收合（§5.1 D-1 #1）：一天的報告全文很長，多數時候只需要看結構化的
 * 重點問題，全文留給少數需要逐字核對的場合。展開狀態記 localStorage——
 * 常看全文的人不必每次進來都重新展開。
 *
 * 整個 header 都可點開合（docs/archive/HISTORY.md #10）：原本只有標題那顆
 * btn-link 可點，右側複製/列印鈕之外的空白區點了沒反應。複製/列印鈕各自
 * stopPropagation，不被 header 的點擊攔截。
 */
function setupReportToggle() {
    const header = document.getElementById('report-header');
    const body = document.getElementById('report-body');
    const caret = document.getElementById('report-caret');

    const expanded = localStorage.getItem('lf.recordDetail.reportExpanded') === 'true';
    applyReportExpanded(expanded);

    header.addEventListener('click', () => toggleReport());
    header.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggleReport();
        }
    });

    for (const id of ['btn-copy-report', 'btn-print']) {
        document.getElementById(id).addEventListener('click', event => event.stopPropagation());
    }

    function toggleReport() {
        applyReportExpanded(body.classList.contains('d-none'));
    }

    function applyReportExpanded(nowOpen) {
        body.classList.toggle('d-none', !nowOpen);
        caret.classList.toggle('lf-collapse-caret--open', nowOpen);
        header.setAttribute('aria-expanded', String(nowOpen));
        localStorage.setItem('lf.recordDetail.reportExpanded', String(nowOpen));
    }
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

    button.href = appUrl(`/records/${next.hostId}/${next.date}`);
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

    // 案件授與檢視（docs/archive/FEEDBACK-10-PLAN.md §7）：這一頁被裁剪成只剩被交辦的問題，
    // 一定要講出來——否則使用者會以為這台主機這天真的只發生了這一件事
    if (detail.caseGrantOnly) {
        const notice = document.createElement('div');
        notice.className = 'alert alert-info py-2 px-3 mb-3';
        notice.textContent = '您以案件處理人的身分檢視這一頁：僅顯示指派給您的問題，' +
            '這台主機當日的其他問題、白話總覽與報告全文不在您的檢視範圍內。';
        body.appendChild(notice);
    }

    const top = document.createElement('div');
    top.className = 'd-flex align-items-center gap-3 mb-2 flex-wrap';

    const hostLink = document.createElement('a');
    hostLink.href = appUrl(`/hosts/${detail.hostId}`);
    hostLink.className = 'fs-5 fw-semibold text-decoration-none';
    // NetIQ 主機以 IP 登錄，光看 hostName 認不出是哪台機器——有 Sentinel 回報的顯示名就一併帶出
    hostLink.textContent = detail.hostDisplayName ? `${detail.hostName}（${detail.hostDisplayName}）` : detail.hostName;

    const dateSpan = document.createElement('span');
    dateSpan.className = 'text-muted';
    dateSpan.textContent = detail.date;

    // 判定依據（docs/archive/HISTORY.md #11）：日風險等級與問題嚴重度是刻意分開的兩套層級，
    // 高風險日不保證看得到高嚴重度問題（可能是 AI 判讀上調、關聯訊號、或問題被顯示設定隱藏）——
    // 沒有明確依據（舊紀錄／低風險日）時給通用說明，不留使用者自己猜
    const riskBasisTitle = detail.riskBasisText ??
        '日風險等級由規則命中／趨勢異常／關聯訊號／AI 判讀綜合判定，與單一問題嚴重度非同一套層級。';
    top.append(hostLink, dateSpan, riskBadge(detail.riskLevel, { title: riskBasisTitle }));

    // docs/LINUX-RULES.md：詳情頁除 hostname 外，加 IP 與作業系統類型
    const osBadge = document.createElement('span');
    osBadge.className = 'lf-badge lf-badge--light border';
    osBadge.textContent = detail.hostOs === 'linux' ? 'Linux' : 'Windows';
    top.appendChild(osBadge);

    if (detail.hostIpAddress) {
        const ipSpan = document.createElement('span');
        ipSpan.className = 'text-muted small font-monospace';
        ipSpan.textContent = detail.hostIpAddress;
        top.appendChild(ipSpan);
    }

    if (detail.aiPending && isAiRetryPending(detail.headline)) {
        // AI 曾嘗試但完全失敗、已標為待補（回饋二十輪 N）：不能顯示成「分析中」——
        // 那會讓人以為稍後重整就有，實際要靠排程「只補跑失敗或未執行」才補得回來
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--warning';
        badge.textContent = 'AI 待補';
        badge.title = 'AI 服務當時未回應，白話摘要從缺；可用排程頁「只補跑失敗或未執行」補回';
        top.appendChild(badge);
    } else if (detail.aiPending) {
        // 統計段已寫入、AI 段還在排隊或執行中（docs/archive/FEEDBACK-12-PLAN.md §3.5）——
        // 中性色，不能顯示成跟「統計模式（AI 未分析）」一樣，那看起來像失敗
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--info';
        badge.textContent = 'AI 分析中';
        badge.title = '統計結果已完成，AI 白話摘要正在背景處理，稍後重新整理即可看到';
        top.appendChild(badge);
    } else if (!detail.aiAnalyzed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '統計模式（AI 未分析）';
        badge.title = 'AI 未呼叫或呼叫失敗，規則與趨勢告警照常運作';
        top.appendChild(badge);
    }

    body.appendChild(top);

    // headline/summary/trendAssessment/action 皆為 AI 產出（見 DailyAnalysisRecord）。
    // 刻意用 renderAiInline 而非區塊版：prompt 要求這幾欄是散文短句（一句話標題、白話說明），
    // 清單／表格不在預期輸出內；inline 附加而不清空，「狀況：」這類標籤才能維持純文字。
    // aiAnalyzed 為 false 時這些欄位其實是統計模式的替代文字，不是 AI 產出，不包框。
    const textParts = [];
    if (detail.headline) textParts.push({ headline: true, text: detail.headline });
    for (const [label, text] of [['狀況', detail.summary], ['趨勢', detail.trendAssessment], ['建議處置', detail.action]]) {
        if (text) textParts.push({ label, text });
    }

    if (textParts.length > 0) {
        const target = document.createElement('div');
        if (detail.aiAnalyzed) {
            target.className = 'lf-ai-block mb-2';
            const badge = document.createElement('span');
            badge.className = 'lf-badge lf-badge--secondary mb-2';
            badge.textContent = 'AI 摘要';
            target.appendChild(badge);
        }

        for (const part of textParts) {
            if (part.headline) {
                const headline = document.createElement('div');
                headline.className = 'fs-5 mb-2';
                renderAiInline(headline, part.text);
                target.appendChild(headline);
                continue;
            }
            const p = document.createElement('p');
            p.className = 'mb-2';
            const strong = document.createElement('strong');
            strong.textContent = `${part.label}：`;
            p.appendChild(strong);
            renderAiInline(p, part.text);
            target.appendChild(p);
        }

        body.appendChild(target);
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

    // docs/archive/HISTORY.md #11：SiteHidden 模式下部分問題被全站顯示設定隱藏時要明講，
    // 否則使用者會誤以為「風險等級判定的依據」就是眼前看到的這些問題
    if (detail.hiddenIssueCount > 0) {
        const hiddenNote = document.createElement('div');
        hiddenNote.className = 'small text-muted mt-2';
        hiddenNote.textContent =
            `另有 ${detail.hiddenIssueCount} 項問題已依全站顯示設定隱藏；風險等級以完整資料判定，不受此設定影響。`;
        body.appendChild(hiddenNote);
    }

    card.appendChild(body);
    container.replaceChildren(card);
}

/**
 * 表格欄位定義（docs/archive/HISTORY.md #7；docs/archive/FEEDBACK-3-PLAN.md #5 欄位合併；
 * docs/archive/FEEDBACK-4-PLAN.md §1 再改版：勾選併入「處理狀態」欄右上角）：
 * 原本「來源/Event」「次數」「嚴重度」「時段」「說明」五欄各自為政，keyDetails
 * （4703 這類事件動輒數百字的帳號/IP 彙總）把其餘欄壓成逐字直排。合併為單一
 * 「問題」欄（issueCell），趨勢與處理狀態維持獨立欄——使用者要看得到「這個問題
 * 正在惡化」與「誰在處理」，這兩者不適合塞進合併欄。
 * 「選取」不再獨立佔欄（原本欄寬窄、checkbox 不好點）：全選移到「處理狀態」表頭
 * 右側，逐列 checkbox 移到「處理狀態」欄內容右上角、加大點擊範圍（見 statusCell／
 * site.css .lf-status-cell__checkbox）。sectionIssues 是這張表要渲染的那批問題
 * （全選 checkbox 的作用範圍）。
 */
function issueColumns(sectionIssues) {
    const statusColumn = {
        title: '處理狀態',
        className: 'lf-status-cell',
        render: i => statusCell(i, sectionIssues)
    };
    if (currentDetail.canHandle) {
        statusColumn.renderHeader = () => statusHeader(sectionIssues);
    }

    return [
        // 「問題」合併欄留在第一欄：renderTable 的展開箭頭（guidancePanel）固定插在第一欄最前面
        { title: '問題', render: i => issueCell(i) },
        { title: '趨勢', className: 'lf-trend-cell', render: i => i.trendText },
        statusColumn
    ];
}

/** 「處理狀態」表頭：欄名文字＋右側全選 checkbox（取代原獨立「選取」欄的表頭） */
function statusHeader(sectionIssues) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex align-items-center justify-content-between gap-2';

    const label = document.createElement('span');
    label.textContent = '處理狀態';
    wrap.appendChild(label);
    wrap.appendChild(selectAllCheckbox(sectionIssues));

    return wrap;
}

/**
 * 「處理狀態」欄內容：canHandle 時右上角疊一顆勾選 checkbox（絕對定位，見
 * site.css .lf-status-cell__wrap），下方是既有的 statusControl 內容（狀態文字／
 * 快速動作）。批次套用允許覆蓋任何問題的狀態（後端 SetIssueStatusBatch 不區分），
 * 前端沒理由把「不處理（預設）」「已知雜訊（自動）」擋在批次選取之外。
 */
function statusCell(issue, sectionIssues) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-status-cell__wrap';

    if (currentDetail.canHandle) {
        wrap.appendChild(selectCheckbox(issue, sectionIssues));
    }
    wrap.appendChild(statusControl(issue));
    // 案件徽章（docs/archive/FEEDBACK-10-PLAN.md §6）：「誰在處理」是處理狀態資訊，放這一欄
    // 才和狀態文字、預計完成日在一起——原本掛在「問題」欄，跟問題本身的識別資訊混雜
    if (issue.caseHandlerName) wrap.appendChild(caseBadge(issue));
    // 先前處理過（docs/archive/FEEDBACK-5-PLAN.md §4）：canHandle 與否都顯示——唯讀角色
    // 同樣需要參考上次怎麼解的，不是只有能操作的人才看得到
    if (issue.hasPriorHandling) wrap.appendChild(priorHandlingTrigger(issue));

    return wrap;
}

/**
 * 「先前處理」按鈕（docs/archive/FEEDBACK-5-PLAN.md §4）：這個問題簽章之前結案過，
 * 點開 modal 看上次案件摘要＋逐日結案標記（只含結案類，處理中／未處理的歷史不列入）。
 */
function priorHandlingTrigger(issue) {
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'btn btn-link btn-sm p-0 lf-no-print mt-1';
    trigger.textContent = '先前處理';
    trigger.title = '這個問題之前結案過，點開看上次怎麼處理的';

    trigger.addEventListener('click', async event => {
        event.stopPropagation();
        const restore = withBusy(trigger, '載入中');
        try {
            const history = await api.get(
                `/api/records/${hostId}/${date}/handling/issue-history?issueKey=${encodeURIComponent(issue.issueKey)}`);
            showDetailModal({
                title: `先前處理（${issue.sourceEventLabel}）`,
                body: issueHistoryBody(history),
                size: 'modal-lg'
            });
        } finally {
            restore();
        }
    });

    return trigger;
}

const ISSUE_HISTORY_STATUS_VARIANTS = {
    resolved: 'success', wont_fix: 'secondary', false_positive: 'secondary', known_noise: 'secondary'
};

/** modal 內容：已結案案件摘要（較接近「上次怎麼解的」）＋逐日結案標記，兩者可能重疊，分開呈現不強行去重 */
function issueHistoryBody(history) {
    const wrap = document.createElement('div');

    if (history.cases.length > 0) {
        const heading = document.createElement('h6');
        heading.textContent = '上次案件';
        wrap.appendChild(heading);
        for (const c of history.cases) wrap.appendChild(issueHistoryCaseItem(c));
    }

    if (history.entries.length > 0) {
        const heading = document.createElement('h6');
        heading.className = history.cases.length > 0 ? 'mt-3' : '';
        heading.textContent = '逐日結案標記';
        wrap.appendChild(heading);
        for (const entry of history.entries) wrap.appendChild(issueHistoryEntryItem(entry));
    }

    if (history.cases.length === 0 && history.entries.length === 0) {
        renderEmpty(wrap, { title: '查無先前處理紀錄' });
    }

    return wrap;
}

function issueHistoryCaseItem(c) {
    const item = document.createElement('div');
    item.className = 'border-start border-3 ps-3 pb-3 mb-1';

    const head = document.createElement('div');
    head.className = 'd-flex align-items-center gap-2 flex-wrap';

    const status = document.createElement('span');
    status.className = `lf-badge lf-badge--${ISSUE_HISTORY_STATUS_VARIANTS[c.status] ?? 'secondary'}`;
    status.textContent = c.statusText;
    head.appendChild(status);

    const summary = document.createElement('span');
    summary.className = 'small';
    summary.textContent = c.handlerName
        ? `由 ${c.handlerName} 處理，${c.firstLinkedDate}～${c.lastLinkedDate}，${c.closedAt} 結案`
        : `${c.firstLinkedDate}～${c.lastLinkedDate}，${c.closedAt} 結案`;
    head.appendChild(summary);

    item.appendChild(head);

    if (c.note) {
        const note = document.createElement('div');
        note.className = 'small text-muted mt-1';
        note.textContent = c.note;
        item.appendChild(note);
    }

    return item;
}

function issueHistoryEntryItem(entry) {
    const item = document.createElement('div');
    item.className = 'border-start border-3 ps-3 pb-3 mb-1';

    const head = document.createElement('div');
    head.className = 'd-flex align-items-center gap-2 flex-wrap';

    const status = document.createElement('span');
    status.className = `lf-badge lf-badge--${ISSUE_HISTORY_STATUS_VARIANTS[entry.status] ?? 'secondary'}`;
    status.textContent = entry.statusText;
    head.appendChild(status);

    const date = document.createElement('span');
    date.className = 'small text-muted';
    date.textContent = `${entry.date}　${formatUserName(entry.actorDisplayName, entry.actorAccount)}${entry.fromCase ? '（案件同步）' : ''}`;
    head.appendChild(date);

    item.appendChild(head);

    if (entry.note) {
        const note = document.createElement('div');
        note.className = 'small text-muted mt-1';
        note.textContent = entry.note;
        item.appendChild(note);
    }

    return item;
}

function selectCheckbox(issue, sectionIssues) {
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input lf-status-cell__checkbox lf-no-print';
    check.dataset.issueKey = issue.issueKey;
    check.checked = selectedIssueKeys.has(issue.issueKey);

    // 別人的案件正在處理的問題不給動（docs/archive/FEEDBACK-10-PLAN.md §8）：兩個人同時標同一個
    // 問題，後標的會蓋掉先標的。要換人處理走「指派」的改派流程，不從狀態標記側繞過去
    // （後端 IssueHandlingCommandService 另有一道拒絕，前端只是不讓人白按）
    if (isHandledByOthers(issue)) {
        check.disabled = true;

        // tooltip 必須掛在**非 disabled** 的外層（體檢 M4）：瀏覽器不對 disabled 元素派送
        // 滑鼠事件，title 寫在 checkbox 上永遠不會出現——使用者看到的就是一個勾不動、
        // 也沒有解釋的 checkbox。文案本身沒問題，問題只在掛載點。
        const reason = `此問題由 ${formatUserName(issue.caseHandlerName, issue.caseHandlerAccount)} 的案件處理中，如需接手請由管理者改派`;
        const lock = document.createElement('span');
        lock.className = 'lf-status-cell__checkbox lf-no-print';
        lock.title = reason;
        // 螢幕閱讀器讀不到 title 以外的線索——disabled checkbox 只會被唸成「已停用」，
        // 不會說明為什麼
        lock.setAttribute('aria-label', reason);
        check.classList.remove('lf-status-cell__checkbox');
        lock.appendChild(check);
        return lock;
    }

    check.title = '勾選後於右側「處理狀態」區塊填寫，可一次套用到所有勾選的問題';

    check.addEventListener('click', event => event.stopPropagation());
    check.addEventListener('change', () => {
        if (check.checked) selectedIssueKeys.add(issue.issueKey);
        else selectedIssueKeys.delete(issue.issueKey);
        refreshSelection();

        const headerCheck = check.closest('table')?.querySelector('thead input[type="checkbox"]');
        if (headerCheck) syncSelectAllCheckbox(headerCheck, sectionIssues);
    });

    return check;
}

/** 表頭全選 checkbox：勾/取消當前這張表（分節或收合區塊）目前顯示的列——批次套用的常見手勢 */
function selectAllCheckbox(sectionIssues) {
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input lf-no-print';
    check.title = '勾選／取消勾選這批問題';
    syncSelectAllCheckbox(check, sectionIssues);

    check.addEventListener('click', event => event.stopPropagation());
    check.addEventListener('change', () => {
        // 全選跳過他人案件處理中的問題（§8）——那些列的 checkbox 是 disabled，
        // 全選若把它們也加進來，送出時會被後端整批拒絕
        for (const issue of selectableIssues(sectionIssues)) {
            if (check.checked) selectedIssueKeys.add(issue.issueKey);
            else selectedIssueKeys.delete(issue.issueKey);
        }

        const table = check.closest('table');
        if (table) {
            for (const rowCheck of table.querySelectorAll('tbody input[type="checkbox"][data-issue-key]:not(:disabled)')) {
                rowCheck.checked = check.checked;
            }
        }
        refreshSelection();
    });

    return check;
}

/** 可被批次套用的問題（§8）：他人案件處理中的排除在外 */
function selectableIssues(issues) {
    return issues.filter(i => !isHandledByOthers(i));
}

function syncSelectAllCheckbox(check, sectionIssues) {
    // 母體是「可勾選的列」而非整批（§8）：否則有他人案件的分節永遠停在 indeterminate，
    // 全選看起來像壞掉
    const selectable = selectableIssues(sectionIssues);
    const selectedCount = selectable.filter(i => selectedIssueKeys.has(i.issueKey)).length;
    check.disabled = selectable.length === 0;
    check.checked = selectedCount > 0 && selectedCount === selectable.length;
    check.indeterminate = selectedCount > 0 && selectedCount < selectable.length;
}

function severityNeutralBadge(text) {
    const span = document.createElement('span');
    span.className = 'lf-badge lf-badge--neutral';
    span.textContent = text;
    return span;
}

/**
 * 嚴重度徽章＋「重大」旗標（docs/archive/HISTORY.md #1，B1 三級化）：命中帶
 * ElevatesDayRisk 旗標規則的問題，一眼看得出「這條問題特別嚴重、是它讓今天變高風險日」。
 */
function severityCell(issue) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1';
    wrap.appendChild(severityBadge(issue.severity));
    if (issue.elevatesDayRisk) wrap.appendChild(elevatesBadge());
    return wrap;
}

/**
 * 問題層級處理狀態顯示（方案 B，§5.1 D-1 #2/#3；#7 拆欄後這裡只管「處理狀態」欄，
 * 勾選移到獨立的「選取」欄，見 selectCheckbox）。四條路徑：
 *   1. 無 Handle 能力 → 唯讀徽章
 *   2. 未列入未處理計算的等級且從未標記過 → 「不處理（預設）」＋確認不處理／調回未處理
 *   3. 從未標記過但同主機同簽章有已知雜訊記憶 → 「已知雜訊（自動）」＋調回未處理
 *   4. 其餘（含明確 open 與已結案）→ 狀態文字＋預計完成日
 */
function statusControl(issue) {
    // 他人案件處理中：一律唯讀呈現（§8），不給「確認不處理」「調回未處理」這些動作按鈕——
    // 那會繞過批次套用直接改到別人正在處理的問題
    if (isHandledByOthers(issue)) {
        return severityNeutralBadge(issue.handlingStatusText || '處理中');
    }

    if (!currentDetail.canHandle) {
        if (issue.handlingStatus === 'open' || (!issue.handlingStatus && !issue.isDefaultUnhandled && !issue.isAutoNoise))
            return document.createTextNode('未處理');
        if (issue.isDefaultUnhandled) return severityNeutralBadge('不處理（預設）');
        if (issue.isAutoNoise) return severityNeutralBadge('已知雜訊（自動）');
        return severityNeutralBadge(issue.handlingStatusText);
    }

    if (issue.isDefaultUnhandled) return defaultUnhandledControl(issue);
    if (issue.isAutoNoise) return autoNoiseControl(issue);
    return statusLabel(issue);
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
            message: `將「${issue.sourceEventLabel}」標為未處理。`,
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
 * 狀態文字＋預計完成日（#7 拆欄後取代原本的 checkboxControl）：勾選已移到獨立的
 * 「選取」欄（見 selectCheckbox），這裡純顯示——狀態改到右側「處理狀態」區塊填一次，
 * 套用到全部勾選的問題（見 handling-panel.js 的 refreshSelection/batch 提交）。
 */
function statusLabel(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-issue-status';

    const hasStatus = issue.handlingStatus && issue.handlingStatus !== 'open';

    const label = document.createElement('div');
    label.className = 'small';
    label.textContent = hasStatus ? issue.handlingStatusText : '未處理';
    wrap.appendChild(label);

    if (issue.handlingStatus === 'in_progress' && issue.dueDate) {
        // yyyy-MM-dd 字串比大小即日期先後（本地日期字串，見 handling-panel 快速鈕的組法）
        const isOverdue = issue.dueDate < todayLocal();

        const due = document.createElement('div');
        due.className = `small lf-issue-status__due ${isOverdue ? 'text-danger fw-semibold' : 'text-muted'}`;
        due.textContent = `${isOverdue ? '逾期' : '預計'} ${issue.dueDate.slice(5)}`;
        wrap.appendChild(due);
    }

    // 觀察中／觀察到期（docs/archive/FEEDBACK-8-PLAN.md #4）：DueDate 在此狀態下代表「觀察至」，
    // 到期後問題仍在發生，比照逾期用紅字提醒（不是新告警機制，是既有逾期通道的延伸）
    if (issue.handlingStatus === 'observing' && issue.dueDate) {
        const isExpired = issue.dueDate < todayLocal();

        const observe = document.createElement('div');
        observe.className = `small lf-issue-status__due ${isExpired ? 'text-danger fw-semibold' : 'text-muted'}`;
        if (isExpired) {
            observe.textContent = `觀察到期 ${issue.dueDate.slice(5)}，問題仍在發生`;
        } else {
            const remainingDays = Math.round((new Date(issue.dueDate) - new Date(todayLocal())) / 86400000);
            observe.textContent = `觀察至 ${issue.dueDate.slice(5)}（剩 ${remainingDays} 天）`;
        }
        wrap.appendChild(observe);
    }

    return wrap;
}

/**
 * 送出問題狀態變更。wrap 是目前顯示在表格「處理」欄的控制項節點——
 * 成功後重新取回這個問題目前的狀態（含後端算好的低風險預設／已知雜訊自動判讀旗標），
 * 就地替換 wrap，不整頁重載；也不能只拿 PUT 的回應自己猜這兩個旗標，
 * 那套推導邏輯只在後端算一次（單一事實來源），前端用哪個值必須問後端要。
 */
async function setIssueStatus(issue, status, wrap, extra = {}) {
    try {
        const result = await api.put(`/api/records/${hostId}/${date}/handling/issues`, {
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
        renderSeverityFilter(currentDetail);
        renderScopeFilter(currentDetail);

        // 案件同步提示（docs/archive/FEEDBACK-4-PLAN.md §2）：這個問題有進行中案件時，這次標記
        // 也會連動到案件涵蓋的其他日子，提示使用者「不是只改了眼前這一列」
        const caseNote = result?.caseSyncedDayCount > 0 ? `（已同步案件涵蓋的 ${result.caseSyncedDayCount} 天）` : '';
        toast((status ? `已標為「${issue.handlingStatusText || '未處理'}」` : '已清除處理標記') + caseNote, 'success');
    } catch (error) {
        toast(error?.message || '更新失敗', 'danger');
    }
}

/**
 * 批次標「已知雜訊」後的治本提議：勾選的問題中有命中規則的，提議把那些規則在本主機抑制；
 * 未命中規則的（Other 類）改提議簽章抑制（回饋十五輪 C-3，取代過去的靜默不提議——A1 指出
 * 的核心缺口是這類問題過去完全沒有抑制掛載點，現在補上了，批次提議也該跟著涵蓋）。
 */
async function offerBatchSuppression(issueKeys) {
    if (!canMaintainRules || !currentDetail) return;

    const keys = new Set(issueKeys);
    const flagged = currentDetail.topIssues.filter(i => keys.has(i.issueKey) && !i.suppressed);
    const ruleIds = [...new Set(flagged.filter(i => i.ruleId).map(i => i.ruleId))];
    const signatureIssues = flagged.filter(i => !i.ruleId);

    if (ruleIds.length === 0 && signatureIssues.length === 0) return;

    // 純 Other 類（無規則可抑制）：改提議簽章抑制，不再靜默不提議
    if (ruleIds.length === 0) {
        const ok = await confirmAction({
            title: '一併建立簽章抑制？',
            message: `已標為已知雜訊。這 ${signatureIssues.length} 個問題未命中任何規則，` +
                `要不要改用簽章抑制在本主機（${currentDetail.hostName}）關閉這些訊號的通知？` +
                '抑制後不再拉高風險、不再進報告（事件仍照常紀錄）。',
            confirmText: '建立抑制',
            confirmVariant: 'primary'
        });
        if (!ok) return;

        const failed = await createSignatureSuppressions(signatureIssues);
        if (failed === 0) toast(`已建立 ${signatureIssues.length} 條簽章抑制`, 'success');
        else toast(`部分簽章抑制建立失敗（${failed}/${signatureIssues.length}），可到「規則維護」手動設定`, 'warning');
        return;
    }

    const message = signatureIssues.length > 0
        ? `已標為已知雜訊。要不要在本主機（${currentDetail.hostName}）抑制命中的 ${ruleIds.length} 條規則` +
          `（${ruleIds.join('、')}），並對另外 ${signatureIssues.length} 個未命中規則的問題建立簽章抑制？` +
          '抑制後這些訊號不再拉高風險、不再進報告（事件仍照常紀錄）。'
        : `已標為已知雜訊。要不要在本主機（${currentDetail.hostName}）抑制命中的 ${ruleIds.length} 條規則` +
          `（${ruleIds.join('、')}）？抑制後這些訊號不再拉高風險、不再進報告（事件仍照常紀錄）。`;

    const ok = await confirmAction({
        title: '一併建立抑制規則？',
        message,
        confirmText: '建立抑制',
        confirmVariant: 'primary'
    });
    if (!ok) return;

    let failed = 0;
    for (const ruleId of ruleIds) {
        try {
            await api.post(`/api/rules/${encodeURIComponent(ruleId)}/suppressions`, {
                host: currentDetail.hostName,
                reason: '詳情頁批次標記已知雜訊',
                days: null
            }, { silent: true });
        } catch {
            failed++;
        }
    }
    if (signatureIssues.length > 0) {
        failed += await createSignatureSuppressions(signatureIssues);
    }

    const total = ruleIds.length + signatureIssues.length;
    if (failed === 0) toast(`已建立 ${total} 條抑制設定`, 'success');
    else toast(`部分抑制設定建立失敗（${failed}/${total}），可到「規則維護」手動設定`, 'warning');
}

/** 對未命中規則的問題建立簽章抑制：issue.issueKey 與後端 IssueSignatureKey.For 同一套算法，
 * 直接送出即可，不需要前端自己組鍵。回傳失敗筆數，供呼叫端彙整 toast。 */
async function createSignatureSuppressions(issues) {
    let failed = 0;
    for (const issue of issues) {
        try {
            await api.post('/api/suppressions', {
                targetType: 'Signature',
                signatureKey: issue.issueKey,
                targetLabel: issue.sourceEventLabel,
                platform: currentDetail.hostOs,
                host: currentDetail.hostName,
                reason: '詳情頁批次標記已知雜訊',
                days: null
            }, { silent: true });
        } catch {
            failed++;
        }
    }
    return failed;
}

/**
 * 未處理判定（含明確 open 或從沒標記過、且不是低風險預設不處理／已知雜訊自動判讀的問題）：
 * renderProgress 的三段計數器與 renderIssues 的排序／收合共用同一份判斷（D2/D3），
 * 避免計數器說的「未處理」跟畫面上排在最前面的列對不起來。
 */
function isUnresolvedIssue(issue) {
    return issue.handlingStatus === 'open' ||
        (!issue.handlingStatus && !issue.isDefaultUnhandled && !issue.isAutoNoise);
}

function isInProgressIssue(issue) {
    // 觀察中一併算「還在進行」（docs/archive/FEEDBACK-10-PLAN.md §8 體檢）：它是非結案類，
    // 日層級推導本來就把它視同處理中（docs/archive/FEEDBACK-8-PLAN.md #4）。
    // 少了它，觀察中的問題會被收進標示「已處理／已有結論」的收合區——標籤與內容不符，
    // 而且顯示範圍下拉把它算進「待處理」，數字對得上、卻要展開「已有結論」才找得到。
    // escalated（回饋十八輪批次G）同理：上報中＝有人在管、還沒有結論。
    return issue.handlingStatus === 'in_progress' || issue.handlingStatus === 'observing' ||
        issue.handlingStatus === 'escalated';
}

/**
 * 重點問題旁的計數器（docs/archive/HISTORY.md #8/D3）：三段「已處理／處理中／未處理」，
 * 忽略其他標籤——這顆計數器要回答的是「還剩幾件要動手、進度到哪」，不是「標了幾件」：
 *   已處理＝真的標成 resolved 的問題數
 *   處理中＝標成 in_progress 的問題數
 *   未處理＝見 isUnresolvedIssue
 * 不處理／誤報／已知雜訊／低風險預設不處理，三邊都不計——那些是「已經有結論」，
 * 不是「還沒處理」，混進未處理只會讓使用者以為還有事要做。任一段為 0 時省略，
 * 避免「已處理 0／處理中 0／未處理 12」這種噪音。
 * 從 currentDetail.topIssues 現算，每次任何一項狀態變動後呼叫，不依賴後端往返。
 */
function renderProgress() {
    const el = document.getElementById('detail-progress');
    if (!el || !currentDetail) return;

    const issues = currentDetail.topIssues;
    if (issues.length === 0) { el.textContent = ''; return; }

    const resolved = issues.filter(i => i.handlingStatus === 'resolved').length;
    const inProgress = issues.filter(isInProgressIssue).length;
    const unhandled = issues.filter(isUnresolvedIssue).length;

    const parts = [];
    if (resolved > 0) parts.push(`已處理 ${resolved}`);
    if (inProgress > 0) parts.push(`處理中 ${inProgress}`);
    if (unhandled > 0) parts.push(`未處理 ${unhandled}`);
    el.textContent = parts.join('／');
}

/** 下鑽帶入的類別（§8.4）：從儀表板分類卡或查詢頁篩著類別點進來時，網址會帶 categories */
function highlightedCategories() {
    const csv = new URLSearchParams(location.search).get('categories');
    return new Set(csv ? csv.split(',') : []);
}

/**
 * 目前篩選後仍可見的問題（docs/archive/HISTORY.md #4）：詢問 AI 下拉與嚴重度／顯示範圍
 * 切換時共用同一份判斷，避免多處篩選邏輯各自維護後兜不起來。
 * 嚴重度與顯示範圍（§8）是 AND 關係——兩個條件都通過才看得到。
 */
/**
 * 管理者顯示設定允許的嚴重度（回饋二十輪 L）。與使用者自己的篩選（activeSeverities）
 * 是兩件事：被管理者隱藏的層級不該長出篩選鈕、也不該算進「另有 N 項未顯示」——
 * 那句提示講的是「你自己篩掉的」，管理者隱藏的部分由 hiddenIssueCount 那句負責交代。
 * 取不到設定時退回全部允許（getDisplaySettings 本身已對失敗降級）。
 */
function allowedSeverities() {
    return new Set(currentDisplaySettings?.visibleSeverities ?? SEVERITY_ORDER);
}

function visibleTopIssues() {
    const allowed = allowedSeverities();
    return currentDetail.topIssues.filter(i => allowed.has(i.severity) && activeSeverities.has(i.severity) && inCurrentScope(i));
}

/**
 * 計算符合條件的問題筆數（契約 §1、§2、§3 共用）。
 * @param {Array} issues 問題清單（通常為 detail.topIssues）
 * @param {object} opts
 *   severity: 指定單一嚴重度字串；若為 null 則取 activeSeverities
 *   scoped: 是否套用目前顯示範圍（inCurrentScope）
 */
function countIssues(issues, { severity = null, scoped = true, scopeValue = null } = {}) {
    const allowed = allowedSeverities();
    const buckets = scopeValue
        ? (SCOPE_OPTIONS.find(o => o.value === scopeValue) ?? SCOPE_OPTIONS[0]).buckets
        : null;
    return issues.filter(i => {
        if (!allowed.has(i.severity)) return false;
        if (severity !== null ? i.severity !== severity : !activeSeverities.has(i.severity)) return false;
        if (buckets) return buckets.includes(issueBucket(i));
        return !scoped || inCurrentScope(i);
    }).length;
}

/**
 * 找出「切過去真的看得到」的顯示範圍。預設不處理的低嚴重度問題落在 done 桶，
 * 而「顯示所有問題」的桶不含 done——直接寫死切到 all 會切了還是空的。
 * 回傳 null 代表沒有任何範圍看得到（不該出現捷徑按鈕）。
 */
function scopeThatReveals(issues) {
    return SCOPE_OPTIONS.find(o => o.value !== currentScope &&
        countIssues(issues, { scopeValue: o.value }) > 0) ?? null;
}

/**
 * 切換顯示範圍並連動重新渲染問題清單、嚴重度篩選鈕與顯示範圍下拉。
 */
function changeScope(newScope) {
    currentScope = newScope;
    renderIssues(currentDetail);
    renderSeverityFilter(currentDetail);
    renderScopeFilter(currentDetail);
    updateIssueOptions(visibleTopIssues());
}

/**
 * 顯示範圍下拉（docs/archive/FEEDBACK-10-PLAN.md §8）：選項附上該範圍實際會顯示的問題數，
 * 切之前就知道會多／少幾列。**狀態不持久化**——每次進頁回到「待處理」，
 * 與「已結案收合」同一個誠實預設的原則（上次的篩選不該悄悄決定這次看到什麼）。
 */
function renderScopeFilter(detail) {
    const select = document.getElementById('detail-scope');
    if (!select) return;

    // 只算嚴重度篩選後的問題：下拉顯示的數字要與切過去之後真正看得到的列數一致
    const allowed = allowedSeverities();
    const bySeverity = detail.topIssues.filter(i => allowed.has(i.severity) && activeSeverities.has(i.severity));
    const counts = { pending: 0, mine: 0, others: 0, done: 0 };
    for (const issue of bySeverity) counts[issueBucket(issue)]++;

    select.replaceChildren();
    for (const option of SCOPE_OPTIONS) {
        const count = option.buckets.reduce((sum, bucket) => sum + counts[bucket], 0);
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = `${option.label}（${count}）`;
        el.selected = option.value === currentScope;
        select.appendChild(el);
    }

    // 沒有任何他人處理中的問題時，「待處理」與「顯示所有問題」看到的東西完全一樣——
    // 下拉仍保留（選項數固定比較好預期），但預設值不需要使用者操心
    select.onchange = () => changeScope(select.value);
}

/**
 * 嚴重度篩選鈕：點選即重繪（免按查詢，比照儀表板期間鈕）。
 * 用 btn-group + active 沿用既有視覺語言，不另造樣式。
 */
function renderSeverityFilter(detail) {
    const container = document.getElementById('detail-severity-filter');
    if (!container) return;

    // 只列出管理者顯示設定允許且當日實際存在的嚴重度，避免出現點了也沒東西的空鈕。
    // 某嚴重度在目前顯示範圍下筆數為 0，但在未套範圍時筆數大於 0 時，該按鈕仍要顯示（計數顯示 0）。
    // 完全不存在該嚴重度的問題時，維持現行行為（按鈕不出現）。
    const allowed = allowedSeverities();
    const present = SEVERITY_ORDER.filter(s => allowed.has(s) && detail.topIssues.some(i => i.severity === s));
    if (present.length <= 1) {
        container.replaceChildren();
        return;
    }

    container.replaceChildren();
    for (const severity of present) {
        const count = countIssues(detail.topIssues, { severity, scoped: true });
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (activeSeverities.has(severity) ? ' active' : '');
        btn.textContent = `${severityName(severity)} ${count}`;
        btn.addEventListener('click', () => {
            if (activeSeverities.has(severity)) activeSeverities.delete(severity);
            else activeSeverities.add(severity);
            btn.classList.toggle('active');
            renderIssues(currentDetail);
            // 顯示範圍下拉的數量是「嚴重度篩選後」的數量（§8），改嚴重度就要重算，
            // 否則下拉會停在舊數字、與實際看到的列數對不起來
            renderScopeFilter(currentDetail);
            updateIssueOptions(visibleTopIssues());
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
    const allowed = allowedSeverities();

    // 顯示範圍（§8）：collapseDone 決定「已完成」是維持既有的分節底部收合列（待處理／
    // 顯示所有問題），還是整組不出現（隱藏已完成）／整組平鋪（僅已完成）
    const { collapseDone } = currentScopeOption();

    for (const category of detail.categories) {
        const all = detail.topIssues.filter(i => i.category === category.category);
        if (all.length === 0) continue;

        // 管理者顯示設定允許的問題（排除管理者設定隱藏的嚴重度，不計入使用者篩選造成的 hidden）
        const allowedIssues = all.filter(i => allowed.has(i.severity));

        // 嚴重度與顯示範圍兩道使用者篩選（§8）：兩者都要通過才顯示，被使用者篩掉的併入底部的
        // 「另有 N 項未顯示」提示——「沒看到」與「不存在」必須分得清楚
        const issues = allowedIssues.filter(i => activeSeverities.has(i.severity) && inCurrentScope(i));
        hidden += allowedIssues.length - issues.length;
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

        // 已結案排序收合（docs/archive/HISTORY.md #8/D2，僅風險日詳情——問題查詢清單
        // 維持既有緊急程度排序不動）：未處理→處理中排在最前面直接可見，其餘（已處理/
        // 不處理/誤報/已知雜訊/預設不處理/自動雜訊——已經有結論的）收合到分節底部。
        // 顯示範圍為「隱藏已完成」「僅已完成」時不再分主表／收合區（§8）：前者已經沒有
        // 已完成的列可收，後者整張表就是已完成的列——再收合一次等於什麼都看不到
        const primary = collapseDone
            ? issues.filter(i => isUnresolvedIssue(i) || isInProgressIssue(i))
            : issues;
        const rest = collapseDone
            ? issues.filter(i => !isUnresolvedIssue(i) && !isInProgressIssue(i))
            : [];

        const body = document.createElement('div');
        if (primary.length > 0) {
            // 規則命中問題掛「處置參考」可展開列，讓「這問題怎麼辦」與問題本身直接對齊
            renderTable(body, { columns: issueColumns(primary), rows: primary, rowDetail: guidancePanel });
        } else {
            renderEmpty(body, { title: '本類別問題皆已有結論', hint: '展開下方「已處理／已有結論」檢視。' });
        }
        section.appendChild(body);

        if (rest.length > 0) {
            section.appendChild(collapsedRestSection(rest));
        }

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
        const unscopedCount = countIssues(detail.topIssues, { scoped: false });
        const target = unscopedCount > 0 ? scopeThatReveals(detail.topIssues) : null;
        if (target) {
            renderEmpty(container, {
                title: `已隱藏全部 ${hidden} 項`,
                hint: `有 ${unscopedCount} 項符合目前嚴重度篩選的問題不在「${currentScopeOption().label}」這個顯示範圍內。`
            });
            const btn = button(`切換為「${target.label}」`, {
                variant: 'outline-primary',
                size: 'sm',
                onClick: () => changeScope(target.value)
            });
            btn.classList.add('mt-3');
            container.querySelector('.lf-empty')?.appendChild(btn);
        } else if (hidden > 0) {
            renderEmpty(container, {
                title: `已隱藏全部 ${hidden} 項`,
                hint: '目前的嚴重度篩選或顯示範圍未包含任何一項，請調整上方的篩選條件。'
            });
        } else {
            renderEmpty(container, {
                title: '當日沒有符合顯示設定的重點問題',
                hint: '當日問題已依全站顯示設定隱藏。'
            });
        }
        return;
    }

    // 有被使用者篩掉的項數在底部提示，「沒看到」與「不存在」要分得清楚（README 的核心誠實原則）
    if (hidden > 0) {
        const note = document.createElement('div');
        note.className = 'text-muted small px-3 py-2 border-top';
        note.textContent = `另有 ${hidden} 項因嚴重度篩選或顯示範圍未顯示。`;
        container.appendChild(note);
    }

    // 下鑽進來時直接捲到命中的第一個類別分節
    container.querySelector('.lf-issue-group--hit')?.scrollIntoView({ block: 'start', behavior: 'smooth' });
}

/**
 * 已結案／已有結論問題的收合區塊（#8）：分節底部一條「展開▾」列，點開才渲染完整表格。
 * 每次 renderIssues 重繪都重新收合（不記憶展開狀態）——批次套用後常有列從上方主表
 * 「搬」進這裡，維持收合預設值最不會讓人意外。
 */
function collapsedRestSection(rest) {
    const wrap = document.createElement('div');
    wrap.className = 'border-top';

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-link btn-sm text-decoration-none text-body d-flex align-items-center gap-2 px-3 py-2 lf-no-print';

    const caret = document.createElement('span');
    caret.className = 'lf-collapse-caret';
    caret.appendChild(icon('chevron-down'));

    const label = document.createElement('span');
    label.textContent = `已處理／已有結論 ${rest.length} 項`;
    toggle.append(caret, label);

    const body = document.createElement('div');
    body.className = 'd-none';
    renderTable(body, { columns: issueColumns(rest), rows: rest, rowDetail: guidancePanel });

    toggle.addEventListener('click', () => {
        const nowOpen = body.classList.toggle('d-none') === false;
        caret.classList.toggle('lf-collapse-caret--open', nowOpen);

        // keyDetails 的「顯示全部」按鈕靠量測裁切狀態決定要不要出現，但這批列是在 d-none
        // 容器裡建構的、當時量不到——展開（列第一次現身）時補量（見 keyDetailsBlock）
        if (nowOpen) {
            for (const details of body.querySelectorAll('.lf-issue-details--clamped')) {
                details.dispatchEvent(new Event('lf-remeasure'));
            }
        }
    });

    wrap.append(toggle, body);
    return wrap;
}

/**
 * 「問題」合併欄（docs/archive/FEEDBACK-3-PLAN.md #5）：取代原本各自獨立的來源/Event、
 * 次數、嚴重度、時段、說明五欄。由上而下：標題行（來源/Event＋log 名＋已抑制徽章）、
 * meta 行（嚴重度／重大徽章・次數・時段）、說明、keyDetails（見 keyDetailsBlock）、
 * 相異訊息數／原始訊息連結。
 */
function issueCell(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-issue-cell';

    const title = document.createElement('div');
    title.className = 'fw-semibold';
    title.textContent = issue.sourceEventLabel;
    wrap.appendChild(title);

    const logName = document.createElement('div');
    logName.className = 'small text-muted';
    logName.textContent = issue.logName;
    wrap.appendChild(logName);

    if (issue.suppressed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '已抑制';
        badge.title = '此規則已被本機抑制：只關掉通知與風險升級，事件仍照常紀錄';
        wrap.appendChild(badge);
    }

    const meta = document.createElement('div');
    meta.className = 'lf-issue-cell__meta d-flex flex-wrap align-items-center gap-2 small text-muted mt-1';
    meta.appendChild(severityCell(issue));
    const count = document.createElement('span');
    count.textContent = `次數 ${formatNumber(issue.count)}`;
    meta.appendChild(count);
    const period = document.createElement('span');
    period.className = 'text-nowrap';
    period.textContent = `${issue.firstSeen}~${issue.lastSeen}`;
    meta.appendChild(period);
    wrap.appendChild(meta);

    if (issue.knownIssue) {
        const text = document.createElement('div');
        text.className = 'mt-1';
        text.textContent = issue.knownIssue;
        wrap.appendChild(text);
    }

    // Security 事件的帳號/IP 彙總是入侵分析最關鍵的依據，不能真的藏起來——
    // 超長時只是視覺上先收合（keyDetailsBlock），有明確的「顯示全部」可以展開，
    // 不是把內容拿掉
    if (currentDetail.detailPruned) {
        const prunedHint = document.createElement('div');
        prunedHint.className = 'small mt-1 px-2 py-1 rounded';
        prunedHint.style.backgroundColor = 'var(--lf-info-soft)';
        prunedHint.style.color = 'var(--lf-info-text)';
        prunedHint.textContent = '這一天的詳情已超過保留期並清除，統計、風險等級與問題清單仍然保留。';
        wrap.appendChild(prunedHint);
    } else {
        if (issue.residualCredentialBasis) wrap.appendChild(residualCredentialBlock(issue));

        if (issue.loginFailureDetails?.length) wrap.appendChild(loginFailureDetailsTable(issue.loginFailureDetails));

        if (issue.keyDetails) wrap.appendChild(keyDetailsBlock(issue.keyDetails));

        if (issue.distinctMessageCount > 1) {
            const distinct = document.createElement('div');
            distinct.className = 'small text-muted mt-1';
            distinct.textContent = `${issue.distinctMessageCount} 種相異訊息`;
            wrap.appendChild(distinct);
        }

        if (issue.sampleMessages?.length) wrap.appendChild(sampleMessagesTrigger(issue));
    }

    return wrap;
}

/**
 * 殘留徽章與判定依據（A6）：
 * 當 issue.residualCredentialBasis 有值時呈現疑似殘留或由殘留觸發的徽章與說明。
 */
function residualCredentialBlock(issue) {
    const wrap = document.createElement('div');
    wrap.className = 'small mt-1 px-2 py-1 rounded';
    wrap.style.backgroundColor = 'var(--lf-info-soft)';
    wrap.style.color = 'var(--lf-info-text)';

    const badge = document.createElement('span');
    badge.className = 'lf-badge lf-badge--secondary';
    badge.textContent = issue.residualCredentialRetry ? '疑似殘留憑證重試' : '可能由殘留憑證觸發';
    wrap.appendChild(badge);

    const basis = document.createElement('div');
    basis.className = 'mt-1';
    basis.textContent = issue.residualCredentialBasis;
    wrap.appendChild(basis);

    return wrap;
}

/**
 * 登入失敗明細表（A6）：
 * 顯示帳號／來源／類型／原因／次數結構化明細，上限 10 列。
 */
function loginFailureDetailsTable(details) {
    const wrap = document.createElement('div');
    wrap.className = 'mt-1';

    const table = document.createElement('table');
    table.className = 'table table-sm mb-0';

    const thead = document.createElement('thead');
    const headerRow = document.createElement('tr');
    const headers = ['帳號', '來源', '類型', '原因', '次數'];
    for (const h of headers) {
        const th = document.createElement('th');
        th.textContent = h;
        if (h === '次數') th.className = 'text-end';
        headerRow.appendChild(th);
    }
    thead.appendChild(headerRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    const maxRows = 10;
    const displayed = details.slice(0, maxRows);
    for (const d of displayed) {
        const tr = document.createElement('tr');

        const accountTd = document.createElement('td');
        const accountSpan = document.createElement('span');
        accountSpan.textContent = d.account ? d.account : '（不明）';
        accountTd.appendChild(accountSpan);
        if (d.isComputerAccount) {
            const note = document.createElement('span');
            note.className = 'text-muted small ms-1';
            note.textContent = '（電腦帳號）';
            accountTd.appendChild(note);
        }
        tr.appendChild(accountTd);

        const sourceTd = document.createElement('td');
        sourceTd.textContent = d.source ? d.source : '（不明）';
        tr.appendChild(sourceTd);

        const typeTd = document.createElement('td');
        typeTd.textContent = d.logonTypeText ? d.logonTypeText : '—';
        tr.appendChild(typeTd);

        const reasonTd = document.createElement('td');
        reasonTd.textContent = d.reasonText ? d.reasonText : '（不明）';
        tr.appendChild(reasonTd);

        const countTd = document.createElement('td');
        countTd.className = 'text-end';
        countTd.textContent = formatNumber(d.count);
        tr.appendChild(countTd);

        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    wrap.appendChild(table);

    if (details.length > maxRows) {
        const remaining = details.length - maxRows;
        const note = document.createElement('div');
        note.className = 'text-muted small mt-1';
        note.textContent = `另有 ${remaining} 筆明細未顯示`;
        wrap.appendChild(note);
    }

    return wrap;
}

/**
 * 案件徽章（docs/archive/FEEDBACK-4-PLAN.md §2；docs/archive/FEEDBACK-10-PLAN.md §6 改格式與位置）：
 * 這個問題目前有進行中案件，狀態會跨日連動——徽章解釋「為什麼這一列的狀態可能是別天標的、
 * 不是我剛動的」。案件狀態值只會是 open／in_progress／observing（後端只回傳進行中案件，
 * 結案類代表案件已結束、不會再出現在這裡），不需要六態全表。
 * 人名走全站統一的「顯示名稱(帳號)」（§6）——同名同姓在企業環境不罕見，只有顯示名稱認不出是誰。
 */
function caseBadge(issue) {
    const statusText = issue.caseStatus === 'open' ? '未處理'
        : issue.caseStatus === 'observing' ? '觀察中' : '處理中';
    const handlerText = formatUserName(issue.caseHandlerName, issue.caseHandlerAccount);
    // 有處理人 Id 時做成連結，點了直接看這個人的工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）
    const badge = document.createElement(issue.caseHandlerId ? 'a' : 'span');
    // d-inline-block + mt-1：徽章現在接在狀態文字／預計完成日之下，需要自己撐開行距
    badge.className = 'lf-badge lf-badge--primary d-inline-block mt-1';
    if (issue.caseHandlerId) {
        badge.href = appUrl(`/handlers/${issue.caseHandlerId}`);
        badge.addEventListener('click', event => event.stopPropagation());
    }
    badge.textContent = `${handlerText} ${statusText}`;
    badge.title = `案件處理人：${handlerText}（自 ${issue.caseFirstLinkedDate} 起追蹤，跨日同步狀態）`;
    return badge;
}

/**
 * keyDetails 收合（docs/archive/FEEDBACK-3-PLAN.md #5）：常見數百字的帳號/IP 彙總
 * （4703 這類事件動輒 11 個帳號欄位）會把合併欄撐得極長，先用 CSS line-clamp
 * 收 3 行，超過才出現「顯示全部」——沒被裁切的短內容不多一次點擊。
 * scrollHeight 是否大於 clientHeight 是判斷有沒有被裁切的標準手法，line-clamp
 * 要等這一輪繪製完成才量得準，故延到 requestAnimationFrame。
 * 列印時 @media print 解除裁切（site.css）：紙本一律看得到完整內容。
 */
function keyDetailsBlock(keyDetails) {
    const wrap = document.createElement('div');
    wrap.className = 'mt-1';

    const clampedClass = 'lf-issue-details--clamped';
    const details = document.createElement('div');
    details.className = `small text-danger ${clampedClass}`;
    details.textContent = keyDetails;
    wrap.appendChild(details);

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-link btn-sm p-0 small lf-no-print d-none';
    toggle.textContent = '顯示全部';
    wrap.appendChild(toggle);

    // 呼叫當下這個節點還沒接上文件（renderTable 會在整列組好後才一次性 replaceChildren），
    // scrollHeight/clientHeight 量到的都是 0——延到下一輪事件迴圈（setTimeout 0）才量得準。
    // 用 setTimeout 而非 requestAnimationFrame：後者綁在合成/繪製管線上，分頁不在前景時
    // 可能整批延後或不觸發（ResizeObserver 的回呼派發同樣綁在繪製步驟，一樣不可靠）。
    //
    // 「已處理／已有結論」收合區的列在 d-none 容器裡建構，setTimeout 量測時兩個高度都是 0
    // （看起來像「沒被裁切」），按鈕會永遠不出現——收合區展開是這些列唯一的現身路徑，
    // 由 collapsedRestSection 在展開時對區塊內的 keyDetails 派發 lf-remeasure 事件補量，
    // 確定性觸發、不依賴任何繪製時機。evaluate 冪等：量得到就定案，重複呼叫無副作用。
    const evaluate = () => {
        if (details.clientHeight === 0) return;   // 仍隱藏中，等下一次 lf-remeasure
        if (details.scrollHeight > details.clientHeight + 1) toggle.classList.remove('d-none');
    };

    details.addEventListener('lf-remeasure', evaluate);
    setTimeout(evaluate, 0);

    toggle.addEventListener('click', event => {
        event.stopPropagation();
        const nowClamped = details.classList.toggle(clampedClass);
        toggle.textContent = nowClamped ? '顯示全部' : '收合';
    });

    return wrap;
}

/**
 * 原始訊息（docs/archive/HISTORY.md #14，取代舊「範例訊息」名稱與 hover 泡泡）：
 * 這個問題實際觸發的事件訊息樣本，供比對確認——舊名稱「範例訊息」看不出指的是什麼。
 * hover popover 在窄欄位下常被 Popper 定位空間壓縮、內容擠成一團，且與點擊維持顯示
 * 兩套手勢並存會曖昧；改為點擊開 modal，寬度不受定位限制，逐則訊息各自成段落，
 * 不再把 `---` 當分隔字串塞進同一段文字裡。
 */
function sampleMessagesTrigger(issue) {
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'btn btn-link btn-sm p-0 lf-no-print';
    trigger.textContent = `原始訊息 ${issue.sampleMessages.length} 則`;
    trigger.title = '這個問題實際觸發的事件訊息樣本，供比對確認';

    trigger.addEventListener('click', event => {
        event.stopPropagation();
        showDetailModal({
            title: `原始訊息（${issue.sourceEventLabel}，共 ${issue.sampleMessages.length} 則）`,
            body: sampleMessagesBody(issue.sampleMessages),
            size: 'modal-lg'
        });
    });

    return trigger;
}

/** 逐則訊息各自成段落（等寬字型、保留原始換行）；textContent 純文字組裝，訊息是攻擊者可控字串，不解析 HTML */
function sampleMessagesBody(messages) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-sample-messages';

    for (const message of messages) {
        const block = document.createElement('pre');
        block.className = 'lf-sample-messages__item';
        block.textContent = message;
        wrap.appendChild(block);
    }

    return wrap;
}

/** 關聯模式的觸發條件說明（回饋十五輪 C-2），供 popover 用——與 Description 本身
 * （已經顯示在畫面上）不重複，這裡講的是「什麼組合會觸發這個模式」，供事後核對。 */
const CORRELATION_PATTERN_HINTS = {
    'intrusion-chain': '觸發條件：同日大量登入失敗，加上帳號建立／提權操作。',
    'brute-success': '觸發條件：同日大量登入失敗後，相同帳號或來源 IP 出現成功登入。',
    'persistence': '觸發條件：帳號異動或攻擊嘗試，加上新服務／排程任務同日出現。',
    'audit-tamper': '觸發條件：稽核記錄被清除或變更，且同日有其他安全事件。',
    'priv-implant': '觸發條件：權限／特權異動，加上新服務／排程任務同日出現。',
    'av-off-malware': '觸發條件：防毒防護被關閉，且同日出現惡意程式或攻擊訊號。',
    'malware-persistence': '觸發條件：偵測到惡意程式，加上新服務／排程任務同日出現。',
    'storage-chain': '觸發條件：兩種以上儲存層訊號（磁碟／NTFS／控制器）同日出現。',
    'storage-crash': '觸發條件：儲存層錯誤，加上非預期關機同日出現。',
    'hw-unstable': '觸發條件：WHEA 硬體錯誤，加上非預期重開同日出現。',
    'crash-service-fail': '觸發條件：應用程式崩潰，加上服務異常終止同日出現。',
    'crash-loop-resource': '觸發條件：服務高頻異常終止（≥100 次），加上系統資源耗盡同日出現。',
    'time-skew-auth': '觸發條件：時間同步失敗，加上登入失敗同日出現。',
    'xday-intrusion': '觸發條件：昨日大量登入失敗，今日出現帳號／權限／服務異動。',
    'xday-storage': '觸發條件：儲存層錯誤連續兩日出現。',
    'xday-av-off-malware': '觸發條件：昨日防護被關閉，今日偵測到惡意程式。',
    'xday-brute-rdp': '觸發條件：昨日大量登入失敗的來源 IP，今日以遠端桌面成功登入同一 IP。',
    'linux-ssh-brute-success': '觸發條件：同日大量 SSH 登入失敗後，相同帳號或來源 IP 出現成功登入。',
    'linux-ssh-brute-uncertain': '觸發條件：同日大量 SSH 登入失敗與成功登入同時存在，但部分事件無法解析帳號／IP。'
};

const TREND_BASIS_HINT = '「可靠歷史」＝排除資料不完整日與該頻道未讀取日的歷史。' +
    '簽章層基準＝該問題出現日的次數中位數；總量層（整體錯誤量／安全稽核事件量）基準＝非零日中位數。';

/**
 * 關聯訊號與趨勢告警：這是**程式確定性比對**的結果，不是 AI 猜測。
 * console 用紅色🔗區塊呈現，Web 沿用同一套視覺語言。
 *
 * 回饋十五輪 C-1／C-2／C-4：有結構化 Ref 資料時做頁內導航＋模式說明 popover＋抑制出口；
 * 舊紀錄（Refs 為空清單）降級回純文字條列，零破壞。
 */
function renderAlerts(detail) {
    const container = document.getElementById('detail-alerts');
    container.replaceChildren();

    const hasSuppressed = detail.suppressedTrendAlerts?.length > 0 || detail.suppressedCorrelationAlerts?.length > 0;
    if (detail.correlationAlerts.length === 0 && detail.trendAlerts.length === 0 && !hasSuppressed) {
        renderEmpty(container, { title: '無關聯或趨勢訊號' });
        return;
    }

    if (detail.correlationAlerts.length > 0) {
        container.appendChild(renderCorrelationBox(detail));
    }
    if (detail.trendAlerts.length > 0) {
        container.appendChild(renderTrendBox(detail));
    }
    if (hasSuppressed) {
        container.appendChild(renderSuppressedAlertsBox(detail));
    }
}

function renderCorrelationBox(detail) {
    const box = document.createElement('div');
    box.className = 'alert alert-danger';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = '🔗 關聯訊號（程式確定性比對）';
    box.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'mb-0 ps-3 small';
    const refs = detail.correlationAlertRefs ?? [];
    for (const alert of detail.correlationAlerts) {
        const ref = refs.find(r => r.text === alert);
        list.appendChild(ref ? correlationAlertItem(ref) : plainAlertItem(alert));
    }
    box.appendChild(list);
    return box;
}

function correlationAlertItem(ref) {
    const item = document.createElement('li');
    item.className = 'd-flex align-items-start justify-content-between gap-2 mb-1';

    const text = document.createElement('span');
    text.className = 'lf-alert-item__text';
    text.textContent = ref.text;
    item.appendChild(text);

    const right = document.createElement('span');
    right.className = 'd-flex align-items-center gap-1 flex-shrink-0';
    const hint = CORRELATION_PATTERN_HINTS[ref.patternId];
    if (hint) right.appendChild(helpIcon(hint, '關聯模式說明'));
    if (canMaintainRules) {
        right.appendChild(button('', {
            variant: 'outline-danger', size: 'sm', icon: 'bell-slash', title: '抑制此關聯模式',
            onClick: () => suppressCorrelationPattern(ref)
        }));
    }
    item.appendChild(right);
    return item;
}

function renderTrendBox(detail) {
    const box = document.createElement('div');
    box.className = 'alert alert-warning mb-0';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2 d-flex align-items-center gap-1';
    title.appendChild(document.createTextNode('頻率異常'));
    title.appendChild(helpIcon(TREND_BASIS_HINT, '基準怎麼算'));
    box.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'mb-0 ps-3 small';
    const refs = detail.trendAlertRefs ?? [];
    for (const alert of detail.trendAlerts) {
        const ref = refs.find(r => r.text === alert);
        list.appendChild(ref ? trendAlertItem(ref) : plainAlertItem(alert));
    }
    box.appendChild(list);
    return box;
}

function trendAlertItem(ref) {
    const item = document.createElement('li');
    item.className = 'd-flex align-items-start justify-content-between gap-2 mb-1';

    // 文字側必須能收縮（lf-alert-item__text）：flex 子項預設 min-width:auto，
    // 而這裡的內容是「Microsoft-Windows-Security-Auditing EventId 4719」這種不含空白的
    // 長 token，不加的話它拒絕收縮、把整列推出卡片（純中文的那幾行會自動斷行所以看不出來）
    if (ref.kind === 'signature' && ref.issueKey) {
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'btn btn-link p-0 text-body text-start lf-alert-item__text';
        link.textContent = ref.text;
        link.addEventListener('click', () => scrollToIssue(ref.issueKey));
        item.appendChild(link);
    } else {
        const text = document.createElement('span');
        text.className = 'lf-alert-item__text';
        text.textContent = ref.text;
        item.appendChild(text);
    }

    if (canMaintainRules && ref.kind !== 'signature') {
        const volumeKind = ref.kind === 'volume-audit' ? 'audit' : 'error';
        const right = document.createElement('span');
        right.className = 'd-flex align-items-center gap-1 flex-shrink-0';
        right.appendChild(button('', {
            variant: 'outline-danger', size: 'sm', icon: 'bell-slash', title: '抑制此類告警（本主機）',
            onClick: () => suppressVolumeAlert(volumeKind)
        }));
        item.appendChild(right);
    }
    return item;
}

function plainAlertItem(text) {
    const item = document.createElement('li');
    item.textContent = text;
    return item;
}

/** 已抑制的告警（回饋十五輪 C-1）：抑制關的是「要不要吵」不是「要不要記」——收合呈現，
 * 讓看得仔細的人知道「暫時關掉的東西其實還在發生」，不是被無聲吃掉。 */
function renderSuppressedAlertsBox(detail) {
    const box = document.createElement('div');
    box.className = 'alert alert-secondary mb-0 mt-2';

    const summary = document.createElement('button');
    summary.type = 'button';
    summary.className = 'btn btn-link p-0 text-body text-decoration-none fw-semibold';
    const count = (detail.suppressedTrendAlerts?.length ?? 0) + (detail.suppressedCorrelationAlerts?.length ?? 0);
    summary.textContent = `已抑制的告警 ${count} 項（通知已關閉，偵測與紀錄照常）`;

    const list = document.createElement('ul');
    list.className = 'mb-0 mt-2 ps-3 small d-none';
    for (const text of [...(detail.suppressedCorrelationAlerts ?? []), ...(detail.suppressedTrendAlerts ?? [])]) {
        list.appendChild(plainAlertItem(text));
    }

    summary.addEventListener('click', () => list.classList.toggle('d-none'));

    box.append(summary, list);
    return box;
}

/** 「類型分布」卡的頁內導航沿用（scrollToCategory）——趨勢告警的簽章鍵先反查所屬類別，
 * 再捲到那個分節；找不到代表被嚴重度篩選隱藏了，同一套提示訊息。 */
function scrollToIssue(issueKey) {
    const issue = currentDetail?.topIssues.find(i => i.issueKey === issueKey);
    if (!issue) {
        toast('這個問題目前被嚴重度篩選隱藏了，請在上方放寬篩選。', 'info');
        return;
    }
    scrollToCategory(issue.category);
}

async function suppressCorrelationPattern(ref) {
    // 強警告＋必填原因（回饋十五輪 C-4）：此模式命中即判高風險日，抑制是影響面最大的一種，
    // 誤用等於幫入侵/故障訊號噤聲——只在確認為已知誤報時使用。
    const reason = await confirmActionWithReason({
        title: '抑制此關聯模式？',
        message: `此模式命中即為高風險日。抑制後本主機（${currentDetail.hostName}）此模式將不再拉高風險、` +
            '不再通知——僅在確認為已知誤報（如既知的內部弱點掃描演練）時使用。',
        confirmText: '確定抑制',
        confirmVariant: 'danger'
    });
    if (!reason) return;

    try {
        await api.post('/api/suppressions', {
            targetType: 'Correlation',
            correlationPatternId: ref.patternId,
            targetLabel: ref.text,
            scope: 'Host',
            host: currentDetail.hostName,
            reason
        });
        toast('已抑制此關聯模式', 'success');
        await load();
    } catch {
        // api.js 已以 toast 顯示錯誤
    }
}

async function suppressVolumeAlert(volumeKind) {
    const label = volumeKind === 'audit' ? '安全稽核事件量突增' : '整體錯誤量突增';
    const reason = await confirmActionWithReason({
        title: `抑制「${label}」？`,
        message: `抑制後本主機（${currentDetail.hostName}）此類告警將不再拉高風險、不再通知，事件仍照常紀錄。`,
        confirmText: '確定抑制',
        confirmVariant: 'danger'
    });
    if (!reason) return;

    try {
        await api.post('/api/suppressions', {
            targetType: 'Volume',
            volumeKind,
            targetLabel: label,
            scope: 'Host',
            host: currentDetail.hostName,
            reason
        });
        toast(`已抑制${label}`, 'success');
        await load();
    } catch {
        // api.js 已以 toast 顯示錯誤
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
        cross.href = appUrl(`/records?categories=${category.category}&riskLevels=${encodeURIComponent('高,中,低')}&from=${detail.date}&to=${detail.date}`);
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
 * 避免展開就打 AI）。AI 產出走 renderAiText 唯一渲染出口（S7）；失敗靜默提示。
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
                renderAiText(output, result.text, { badge: 'AI 判讀', badgeClassName: 'lf-badge lf-badge--secondary me-2' });
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
        renderAiInline(problem, finding.problem);
        item.appendChild(problem);

        if (finding.impact) {
            const impact = document.createElement('div');
            impact.className = 'small text-muted mb-1';
            impact.appendChild(document.createTextNode('影響：'));
            renderAiInline(impact, finding.impact);
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
        renderAiInline(li, item);
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

guardLoad(document.getElementById('detail-issues'), load);
