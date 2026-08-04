/**
 * 風險日詳情的處理面板（docs/WEB-SPEC.md §9.3；2026-07-27 批次套用改版）。
 *
 * 核心情境一：主機 A 的負責人是 OOO，但主管認為問題緊急、先交給 XXX 處理——
 * 此時**負責人不變**（唯讀顯示），變的是這個風險日的處理人（只有 admin 能改）。
 *
 * 核心情境二（本次改版）：使用者在左側問題列表勾選多個問題後，不再逐列跳出面板填寫，
 * 而是在這裡的「處理狀態」區塊填一次，套用到所有勾選的問題；沒有勾選任何問題時，
 * 這個區塊改回原本的日層級狀態編輯（相容既有行為，例如沒有具體問題可標的整天說明）。
 */

import { api } from '../core/api.js';
import { renderLoading, renderEmpty, toast, withBusy, showDetailModal, labelValue, button, helpIcon } from '../core/ui.js';
import { formatDateTime, formatUserName, toLocalDateString } from '../core/format.js';

/** 操作者顯示：帳號空＝系統動作（docs/archive/FEEDBACK-8-PLAN.md #6） */
function operatorLabel(actorDisplayName, actorAccount) {
    return actorAccount ? formatUserName(actorDisplayName, actorAccount) : '（系統）';
}

// 狀態直選（取代下拉）：日層級與問題層級批次套用共用同一組值域
const STATUS_CHIPS = [
    { value: 'open', text: '未處理' },
    { value: 'in_progress', text: '處理中' },
    { value: 'resolved', text: '已處理' },
    { value: 'wont_fix', text: '不處理（評估後決定）' },
    { value: 'false_positive', text: '誤報' },
    { value: 'known_noise', text: '已知雜訊' }
];

// 「觀察中」只在問題層級（批次模式）提供（docs/archive/FEEDBACK-8-PLAN.md #4）：日層級 HandlingStatuses
// 沒有這個值域，觀察的對象是「這個問題」而不是「這一天」，硬加進日層級只會讓 Update API
// 回一個看不懂的驗證錯誤
const OBSERVING_CHIP = { value: 'observing', text: '觀察中' };

const NOTE_FIELD_BY_STATUS = {
    open: { label: '說明（選填）', required: false },
    in_progress: { label: '處理說明（選填）', required: false },
    observing: { label: '觀察備註（選填）', required: false },
    resolved: { label: '處理說明（選填）', required: false },
    wont_fix: { label: '不處理原因（必填）', required: true },
    false_positive: { label: '備註（選填）', required: false },
    known_noise: { label: '備註（選填，供日後回頭確認判斷依據）', required: false }
};

const STATUS_VARIANTS = {
    open: 'danger', in_progress: 'primary', observing: 'info', resolved: 'success',
    wont_fix: 'secondary', false_positive: 'secondary', known_noise: 'secondary'
};

/** 目前面板的狀態：initHandlingPanel 之後才有值，refreshSelection 靠這份重繪 */
let state = null;

/**
 * @param {() => Set<string>} getSelection 目前勾選的問題鍵集合（record-detail 持有）
 * @param {(result?: {status: string, issueKeys: string[]}) => Promise} onBatchSaved 批次套用成功後回呼（重載頁面＋後續治本提議）
 * @param {{canMaintainRules?: boolean}} options 能力旗標：誤報時是否顯示規則維護提示
 */
export async function initHandlingPanel(hostId, date, getSelection, onBatchSaved, options = {}) {
    const panel = document.getElementById('handling-panel');
    renderLoading(panel, 3);

    const handling = await api.get(`/api/records/${hostId}/${date}/handling`);

    // 指派下拉的人選**只在有 Assign 能力時**才載入：
    // 無條件呼叫 /api/admin/users 會讓每個 user 每次開詳情頁都產生一筆
    // 403＋access_denied 稽核——「權限不足被拒」是稽核上最有價值的訊號，
    // 被正常瀏覽的噪音淹沒後，真正的權限試探就再也看不出來了。
    const users = handling.canAssign ? await loadAssignableUsers() : [];

    state = { hostId, date, handling, users, getSelection, onBatchSaved, options };
    render();
    await loadLogs(hostId, date);
}

/** 勾選狀態改變時呼叫：只重繪「處理狀態」表單，不重新打 API（負責人／處理人／歷程不受影響） */
export function refreshSelection() {
    if (state) render();
}

async function loadAssignableUsers() {
    try {
        return await api.get('/api/admin/users', { silent: true });
    } catch {
        return [];
    }
}

/**
 * 由問題標記推導出的狀態文字（docs/archive/HISTORY.md #6）：與存的日層級快照
 * （handling.statusText）不同——後者指派後會卡在「處理中」不再更新，這裡才是
 * 「現在真正的狀態」，與清單頁看到的完全同源。derivedStatus 為 null（Update/Assign
 * 呼叫端未補算）時 fallback 用 statusText，不帶結案進度。
 */
function derivedStatusLabel(handling) {
    const text = handling.derivedStatusText ?? handling.statusText;
    return handling.totalIssues > 0 ? `${text}（${handling.closedIssues}/${handling.totalIssues} 已結案）` : text;
}

function render() {
    const panel = document.getElementById('handling-panel');
    const { handling, users, hostId, date } = state;
    panel.replaceChildren();

    panel.appendChild(readonlyField(
        '目前狀態',
        derivedStatusLabel(handling),
        '由問題標記推導；與下方表單要儲存的日層級狀態是兩件事——沒有勾選問題逐項標記時兩者相同'
    ));

    // 案件連動提示（docs/archive/FEEDBACK-4-PLAN.md §2）：本日有幾個問題屬進行中案件，
    // 讓使用者知道為什麼那幾列的狀態會「自己動」（案件同步的結果，不是自己標的）
    if (handling.openCaseCount > 0) {
        const hint = document.createElement('div');
        hint.className = 'lf-hint mb-3';
        hint.textContent = `本日 ${handling.openCaseCount} 項問題屬進行中案件，狀態會跟著案件涵蓋的其他日子連動。`;
        panel.appendChild(hint);
    }

    // ── 負責人（唯讀）：主機的長期屬性，改派處理人不會動到它 ──
    panel.appendChild(readonlyField(
        '主機負責人',
        handling.ownerNames.length ? handling.ownerNames.join('、') : '未指定',
        '主機的長期屬性，於「主機」維護頁調整',
        true
    ));

    // ── 處理人：事件層級，只有 admin 能改 ──
    if (handling.canAssign) {
        panel.appendChild(assignField(handling, users, hostId, date));
    } else {
        panel.appendChild(readonlyField(
            '處理人',
            handling.handlerName ?? '未指派',
            '僅系統管理員可指派或改派'
        ));
    }

    panel.appendChild(document.createElement('hr'));

    if (!handling.canHandle) {
        if (handling.note) panel.appendChild(readonlyField('處理說明', handling.note));
        return;
    }

    panel.appendChild(handlingForm());
}

/**
 * hintAsIcon（docs/archive/FEEDBACK-5-PLAN.md §6）：預設 false 沿用常駐 form-text——三個呼叫端裡
 * 只有「主機負責人」的說明屬純描述性質（看過一次就記得），改收進 icon；其餘兩個
 * （目前狀態的雙軌語意、處理人唯讀原因）填錯/看漏代價較高，維持常駐。
 */
function readonlyField(label, value, hint, hintAsIcon = false) {
    const wrap = document.createElement('div');
    wrap.className = 'mb-3';
    const [labelEl, valueEl] = labelValue(label, value, { labelClass: 'form-label small mb-1 text-muted' });

    if (hint && hintAsIcon) {
        labelEl.classList.add('d-inline-flex', 'align-items-center', 'gap-1');
        labelEl.appendChild(helpIcon(hint));
    }
    wrap.append(labelEl, valueEl);

    if (hint && !hintAsIcon) {
        const hintEl = document.createElement('div');
        hintEl.className = 'form-text';
        hintEl.textContent = hint;
        wrap.appendChild(hintEl);
    }

    return wrap;
}

function assignField(handling, users, hostId, date) {
    const wrap = document.createElement('div');
    wrap.className = 'mb-3';

    const label = document.createElement('label');
    label.className = 'form-label small mb-1 text-muted';
    label.textContent = '處理人';
    label.htmlFor = 'handler-select';

    const select = document.createElement('select');
    select.className = 'form-select form-select-sm';
    select.id = 'handler-select';

    const none = document.createElement('option');
    none.value = '';
    none.textContent = '（未指派）';
    select.appendChild(none);

    // 負責人置頂：改派時最常選的還是負責人，放在最上面省一次捲動
    const ownerNames = new Set(handling.ownerNames);
    const sorted = [...users].filter(u => u.active).sort((a, b) => {
        const aOwner = ownerNames.has(a.displayName) ? 0 : 1;
        const bOwner = ownerNames.has(b.displayName) ? 0 : 1;
        return aOwner - bOwner || a.displayName.localeCompare(b.displayName, 'zh-TW');
    });

    for (const user of sorted) {
        const option = document.createElement('option');
        option.value = user.userId;
        option.textContent = ownerNames.has(user.displayName)
            ? `${formatUserName(user.displayName, user.account)}（負責人）`
            : formatUserName(user.displayName, user.account);
        option.selected = user.userId === handling.handlerId;
        select.appendChild(option);
    }

    select.addEventListener('change', async () => {
        select.disabled = true;
        try {
            const updated = await api.put(`/api/records/${hostId}/${date}/handling/assign`, {
                handlerId: select.value ? Number(select.value) : null
            });

            // 建案提示（docs/archive/FEEDBACK-4-PLAN.md §2/Q1/Q2）：指派會對本日未結案問題建立案件並
            // 回溯歷史；已有他人進行中案件的問題不搶走，讓使用者知道發生了什麼
            const parts = [updated.handlerName ? `已指派給 ${updated.handlerName}` : '已取消指派'];
            if (updated.casesCreated > 0) parts.push(`已為 ${updated.casesCreated} 個問題建立案件`);
            if (updated.casesSkippedHandlerNames?.length) {
                parts.push(`${updated.casesSkippedHandlerNames.length} 個問題已由 ${updated.casesSkippedHandlerNames.join('、')} 的案件涵蓋，未變更`);
            }
            toast(parts.join('；'), 'success');

            await initHandlingPanel(hostId, date, state.getSelection, state.onBatchSaved, state.options);
        } catch {
            select.disabled = false;
        }
    });

    label.classList.add('d-inline-flex', 'align-items-center', 'gap-1');
    label.appendChild(helpIcon('可指派給負責人以外的人；主機負責人不會因此改變。'));

    wrap.append(label, select);
    return wrap;
}

/**
 * 處理狀態表單：批次模式（有勾選問題）套用到問題層級，否則沿用日層級 fallback。
 * 兩種模式共用同一套狀態直選＋預計完成日＋說明欄，只有送出目標與預設值不同。
 */
function handlingForm() {
    const { handling, hostId, date, getSelection } = state;
    const selection = getSelection();
    const batchMode = selection.size > 0;

    const form = document.createElement('form');

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label small mb-1 text-muted';
    statusLabel.textContent = '處理狀態';
    form.appendChild(statusLabel);

    if (batchMode) {
        const info = document.createElement('div');
        info.className = 'lf-hint mb-2';
        info.textContent = `已勾選 ${selection.size} 個問題，套用後會覆蓋這些問題目前的處理狀態。`;
        form.appendChild(info);
    }

    // 狀態直選（取代下拉）：批次模式不預選，逼使用者明確選一個；日層級模式預選推導出的
    // 目前狀態（#6）——不用存的日層級快照，避免使用者一進面板就看到跟「目前狀態」欄位對不上的預選
    let selectedStatus = batchMode ? null : (handling.derivedStatus ?? handling.status);

    const chipGroup = document.createElement('div');
    chipGroup.className = 'lf-toolbar__chips mb-3';
    form.appendChild(chipGroup);

    // 預計完成日（只有「處理中」才顯示，§6）
    const dueWrap = document.createElement('div');
    dueWrap.className = 'mb-3';
    form.appendChild(dueWrap);

    const quickRow = document.createElement('div');
    quickRow.className = 'd-flex gap-2 mb-2';
    for (const days of [3, 7, 14]) {
        quickRow.appendChild(button(`${days} 日`, {
            onClick: () => {
                const target = new Date();
                target.setDate(target.getDate() + days);
                dueInput.value = toLocalDateString(target);   // 本地日期（S12），不用 toISOString 的 UTC 日期
            }
        }));
    }

    const dueInput = document.createElement('input');
    dueInput.type = 'date';
    dueInput.className = 'form-control form-control-sm';
    dueInput.id = 'due-date';
    dueInput.value = batchMode ? '' : (handling.dueDate ?? '');

    dueWrap.append(quickRow, dueInput);

    if (!batchMode && handling.isOverdue) {
        const overdue = document.createElement('div');
        overdue.className = 'form-text text-danger fw-semibold';
        overdue.textContent = '⚠ 已逾期';
        dueWrap.appendChild(overdue);
    }

    // 觀察天數（只有「觀察中」才顯示，批次模式限定，docs/archive/FEEDBACK-8-PLAN.md #4）：輸入天數而非
    // 直接選日期——「觀察 N 天」是使用者的心智模型，換算成日期送給後端（沿用 DueDate 欄位）
    const observeWrap = document.createElement('div');
    observeWrap.className = 'mb-3 d-none';
    const observeLabel = document.createElement('label');
    observeLabel.className = 'form-label small mb-1 text-muted';
    observeLabel.htmlFor = 'observe-days';
    observeLabel.textContent = '觀察天數';
    const observeInput = document.createElement('input');
    observeInput.type = 'number';
    observeInput.className = 'form-control form-control-sm';
    observeInput.id = 'observe-days';
    observeInput.min = '1';
    observeInput.max = '90';
    observeInput.value = '7';
    const observeHint = document.createElement('div');
    observeHint.className = 'form-text';
    observeHint.textContent = '觀察期間這個問題不再進入待辦，觀察到期後若仍在發生，會回到「處理中逾期」提醒。';
    observeWrap.append(observeLabel, observeInput, observeHint);
    form.appendChild(observeWrap);

    function observeUntilDate() {
        const days = Math.min(90, Math.max(1, Number(observeInput.value) || 7));
        const target = new Date();
        target.setDate(target.getDate() + days);
        return toLocalDateString(target);
    }

    /** 送出用的 dueDate：in_progress 用日期選擇器的值，observing 用天數換算的日期，其餘不存 */
    function resolveDueDate() {
        if (selectedStatus === 'in_progress') return dueInput.value || null;
        if (selectedStatus === 'observing') return observeUntilDate();
        return null;
    }

    // 調回未處理時，批次模式下可選是否一併清除已知雜訊記憶（同主機同簽章之後不再自動判讀成雜訊）
    const forgetNoiseWrap = document.createElement('div');
    forgetNoiseWrap.className = 'form-check mb-3';
    const forgetNoiseCheck = document.createElement('input');
    forgetNoiseCheck.type = 'checkbox';
    forgetNoiseCheck.className = 'form-check-input';
    forgetNoiseCheck.id = 'forget-noise';
    const forgetNoiseLabel = document.createElement('label');
    forgetNoiseLabel.className = 'form-check-label small';
    forgetNoiseLabel.htmlFor = 'forget-noise';
    forgetNoiseLabel.textContent = '同時清除已知雜訊記憶（若先前標記過，之後不再自動判讀）';
    forgetNoiseWrap.append(forgetNoiseCheck, forgetNoiseLabel);
    if (batchMode) form.appendChild(forgetNoiseWrap);

    // 說明
    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small mb-1 text-muted';
    noteLabel.htmlFor = 'handling-note';
    form.appendChild(noteLabel);

    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm mb-3';
    noteInput.id = 'handling-note';
    noteInput.rows = 3;
    noteInput.value = batchMode ? '' : (handling.note ?? '');
    noteInput.placeholder = '例如：已確認為每週維護重開機，屬正常現象';
    form.appendChild(noteInput);

    // 誤報且能維護規則時，提議調整規則（治本：規則本身可能過嚴）——
    // 沿用舊逐列面板的提示，批次模式下無法指向單一規則，改連到規則維護頁
    const ruleHint = document.createElement('div');
    ruleHint.className = 'lf-hint mb-3 d-none';
    const ruleLink = document.createElement('a');
    ruleLink.href = '/admin/rules';
    ruleLink.target = '_blank';
    ruleLink.textContent = '如果規則常常誤判，可以到規則維護調整判定條件';
    ruleHint.appendChild(ruleLink);
    form.appendChild(ruleHint);

    // 觀察中只在批次（問題層級）模式提供，見 OBSERVING_CHIP 的宣告理由
    const availableChips = batchMode ? [...STATUS_CHIPS, OBSERVING_CHIP] : STATUS_CHIPS;

    function renderChips() {
        chipGroup.replaceChildren();
        for (const chip of availableChips) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'lf-chip' + (selectedStatus === chip.value ? ' active' : '');
            btn.textContent = chip.text;
            btn.addEventListener('click', () => {
                selectedStatus = chip.value;
                renderChips();
                updateFieldsForStatus();
            });
            chipGroup.appendChild(btn);
        }
    }

    function updateFieldsForStatus() {
        dueWrap.classList.toggle('d-none', selectedStatus !== 'in_progress');
        observeWrap.classList.toggle('d-none', selectedStatus !== 'observing');
        forgetNoiseWrap.classList.toggle('d-none', selectedStatus !== 'open');
        ruleHint.classList.toggle('d-none', !(selectedStatus === 'false_positive' && state.options.canMaintainRules));

        const field = NOTE_FIELD_BY_STATUS[selectedStatus] ?? { label: '說明（選填）', required: false };
        noteLabel.textContent = field.label;
        noteInput.required = field.required;
    }

    renderChips();
    updateFieldsForStatus();

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary w-100';
    submit.textContent = batchMode ? `套用到 ${selection.size} 個問題` : '儲存';
    form.appendChild(submit);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        if (!selectedStatus) {
            toast('請選擇處理狀態', 'warning');
            return;
        }
        const field = NOTE_FIELD_BY_STATUS[selectedStatus] ?? { required: false };
        if (field.required && !noteInput.value.trim()) {
            noteInput.classList.add('is-invalid');
            return;
        }

        const restore = withBusy(submit, batchMode ? '套用中' : '儲存中');
        try {
            if (batchMode) {
                const issueKeys = [...selection];
                const result = await api.put(`/api/records/${hostId}/${date}/handling/issues/batch`, {
                    issueKeys,
                    status: selectedStatus,
                    note: noteInput.value.trim() || null,
                    dueDate: resolveDueDate(),
                    forgetNoise: forgetNoiseCheck.checked
                });
                selection.clear();
                // 帶回套用後的日狀態（#6）：後端已算好 DayStatus/Total/Closed，直接顯示，
                // 不必等頁面重載才看到「這次套用完，這天現在算什麼狀態」
                const progress = result.totalIssues > 0 ? `（${result.closedIssues}/${result.totalIssues} 已結案）` : '';
                // 案件同步提示（docs/archive/FEEDBACK-4-PLAN.md §2）：這批問題中有屬進行中案件的，
                // 這次套用也會連動到案件涵蓋的其他日子
                const caseNote = result.caseSyncedDayCount > 0 ? `；已同步案件涵蓋的 ${result.caseSyncedDayCount} 天` : '';
                toast(`已套用處理狀態；本日狀態：${result.dayStatusText}${progress}${caseNote}`, 'success');
                // 把套用結果回報給頁面：已知雜訊等狀態的後續治本提議（建立抑制規則）由呼叫端接手
                await state.onBatchSaved?.({ status: selectedStatus, issueKeys });
            } else {
                await api.put(`/api/records/${hostId}/${date}/handling`, {
                    status: selectedStatus,
                    note: noteInput.value.trim() || null,
                    dueDate: resolveDueDate()
                });
                toast('已更新處理狀態', 'success');
                await initHandlingPanel(hostId, date, state.getSelection, state.onBatchSaved, state.options);
            }
        } catch {
            restore();
        }
    });

    if (!batchMode && handling.updatedAt) {
        const updated = document.createElement('div');
        updated.className = 'form-text mt-2';
        updated.textContent = `最後更新：${formatDateTime(handling.updatedAt)}`;
        form.appendChild(updated);
    }

    return form;
}

const ISSUE_LOG_ACTIONS = new Set(['issue_status', 'issue_status_cleared']);

/**
 * 處理歷程 timeline：完整敘事（指派 → 查修中 → 換了硬碟 → 結案）。
 * 這正是快照與歷程分兩份儲存的目的——單一說明欄位會把前面的過程蓋掉。
 *
 * D4（docs/archive/HISTORY.md #6）改為問題層級逐筆記錄後，卡片內固定高度＋
 * 「放大檢視」modal（#13）：卡片內把同一次批次（同操作者＋同動作＋同時間戳，
 * 見 HandlingService.SetIssueStatusBatch 的 occurredAt）收合成一條摘要，
 * modal 內展開逐筆——資料本來就是逐筆的，只有呈現方式不同。
 */
async function loadLogs(hostId, date) {
    const container = document.getElementById('handling-log');
    const expandButton = document.getElementById('handling-log-expand');

    const logs = await api.get(`/api/records/${hostId}/${date}/handling/logs`);

    renderTimeline(container, logs, { expanded: false });

    if (expandButton) {
        expandButton.classList.toggle('d-none', logs.length === 0);
        expandButton.onclick = () => {
            const body = document.createElement('div');
            renderTimeline(body, logs, { expanded: true });
            showDetailModal({ title: '處理歷程（完整明細）', body, size: 'modal-lg' });
        };
    }
}

function renderTimeline(container, logs, { expanded }) {
    if (logs.length === 0) {
        renderEmpty(container, { title: '尚無處理紀錄', hint: '更新處理狀態後，這裡會留下完整的處理過程。' });
        return;
    }

    const list = document.createElement('div');
    for (const entry of groupLogs(logs)) {
        if (entry.kind === 'single') {
            list.appendChild(renderLogItem(entry.log));
        } else if (expanded) {
            for (const log of entry.logs) list.appendChild(renderLogItem(log));
        } else {
            list.appendChild(renderGroupSummary(entry));
        }
    }
    container.replaceChildren(list);
}

/**
 * 把連續的問題層級標記（同操作者＋同動作＋同時間戳）視覺分組——資料仍是逐筆的
 * （見 groupLogs 回傳結構），只在卡片內的收合呈現才把一批合成一條。單筆日層級操作
 * （指派/日層級狀態變更）維持個別顯示，不參與分組。
 */
function groupLogs(logs) {
    const entries = [];
    for (const log of logs) {
        const last = entries[entries.length - 1];
        const groupable = ISSUE_LOG_ACTIONS.has(log.action);

        if (groupable && last?.kind === 'group' &&
            last.action === log.action && last.actorAccount === log.actorAccount && last.createdAt === log.createdAt) {
            last.logs.push(log);
            continue;
        }

        entries.push(groupable
            ? {
                kind: 'group', action: log.action, actorAccount: log.actorAccount,
                actorDisplayName: log.actorDisplayName, createdAt: log.createdAt, logs: [log]
            }
            : { kind: 'single', log });
    }
    return entries;
}

function renderLogItem(log) {
    const item = document.createElement('div');
    item.className = 'border-start border-3 ps-3 pb-3 mb-1';

    const head = document.createElement('div');
    head.className = 'd-flex align-items-center gap-2 flex-wrap';

    const action = document.createElement('span');
    action.className = 'fw-semibold small';
    action.textContent = log.actionText;
    head.appendChild(action);

    if (log.issueLabel) {
        const issue = document.createElement('span');
        issue.className = 'small text-muted';
        issue.textContent = `【${log.issueLabel}】`;
        head.appendChild(issue);
    }

    const status = document.createElement('span');
    status.className = `lf-badge lf-badge--${STATUS_VARIANTS[log.status] ?? 'secondary'}`;
    status.textContent = log.statusText;
    head.appendChild(status);

    if (log.handlerName) {
        const handler = document.createElement('span');
        handler.className = 'small text-muted';
        handler.textContent = `處理人：${log.handlerName}`;
        head.appendChild(handler);
    }

    item.appendChild(head);

    if (log.note) {
        const note = document.createElement('div');
        note.className = 'small mt-1';
        note.textContent = log.note;
        item.appendChild(note);
    }

    const meta = document.createElement('div');
    meta.className = 'small text-muted mt-1';
    meta.textContent = `${formatDateTime(log.createdAt)}　操作者：${operatorLabel(log.actorDisplayName, log.actorAccount)}`;
    item.appendChild(meta);

    return item;
}

/** 卡片內的批次摘要列（收合狀態）：一次批次一行，逐筆明細留給「放大檢視」modal */
function renderGroupSummary(group) {
    const item = document.createElement('div');
    item.className = 'border-start border-3 ps-3 pb-3 mb-1';

    const head = document.createElement('div');
    head.className = 'd-flex align-items-center gap-2 flex-wrap';

    const verb = group.action === 'issue_status_cleared' ? '清除標記' : '標記';
    const action = document.createElement('span');
    action.className = 'fw-semibold small';
    action.textContent = `${verb} ${group.logs.length} 個問題`;
    head.appendChild(action);

    if (group.action === 'issue_status') {
        const status = document.createElement('span');
        status.className = `lf-badge lf-badge--${STATUS_VARIANTS[group.logs[0].status] ?? 'secondary'}`;
        status.textContent = group.logs[0].statusText;
        head.appendChild(status);
    }

    item.appendChild(head);

    const meta = document.createElement('div');
    meta.className = 'small text-muted mt-1';
    meta.textContent =
        `${formatDateTime(group.createdAt)}　操作者：${operatorLabel(group.actorDisplayName, group.actorAccount)}　點「放大檢視」看逐筆明細`;
    item.appendChild(meta);

    return item;
}
