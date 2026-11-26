/**
 * 「不再顯示這個問題…」分流面板（第 81 輪批次 N，F16-1）：三個問句 → 套用既有機制，先說後果再套用。
 * 對照表與判定都在 core/hide-triage.js（純資料、有測試逐格固定）；這裡只負責畫面與送出。
 *
 * 一個 modal、問答全部就地展開（Bootstrap 5.3 不能疊開第二層）；問題查詢「依問題」視角與交辦單頁兩個入口共用這一份，
 * 與靜音 modal 同一個分工理由（issue-mute-modal.js）。入口需要 Maintain：後端三個端點本來就都要它。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { toast, withBusy, showDetailModal, button, searchableHostSelect } from '../core/ui.js';
import { statusNote, roleText } from '../core/elements.js';
import { exclusive, createSeqGuard } from '../core/async-guards.js';
import {
    TRIAGE_DURATIONS, TRIAGE_SCOPES, TRIAGE_ORDERS, TRIAGE_CONCLUSIONS, TRIAGE_MECHANISM_NAMES,
    scopeAvailability, ordersAvailability, coerceOrders, validateTriage, buildTriagePlan, triageSummary, applyTriage
} from '../core/hide-triage.js';
import { loadActiveOrders } from './issue-mute-modal.js';

/** 暫時的天數選項（預設 7 天）；後端上限 365 */
const DAY_CHOICES = [1, 7, 30, 90, 180, 365];
const DEFAULT_DAYS = 7;

/** 分段控制：互斥單選，選項可個別停用（停用的選項不能點，理由由呼叫端另行顯示） */
function segmented(label, options, { value, onChange }) {
    const wrap = document.createElement('div');
    wrap.className = 'mb-3';
    const title = document.createElement('div');
    title.className = 'form-label lf-text-label';
    title.textContent = label;
    const group = document.createElement('div');
    group.className = 'btn-group btn-group-sm';
    group.setAttribute('role', 'group');
    group.setAttribute('aria-label', label);
    const buttons = new Map();
    for (const option of options) {
        const btn = button(option.label, { variant: 'outline-secondary', title: option.hint, onClick: () => onChange(option.value) });
        buttons.set(option.value, btn);
        group.appendChild(btn);
    }
    const note = document.createElement('div');
    note.className = 'lf-hint mt-1';
    wrap.append(title, group, note);

    function sync(current, availability = () => ({ available: true })) {
        const reasons = [];
        for (const option of options) {
            const btn = buttons.get(option.value);
            const state = availability(option.value);
            const selected = option.value === current;
            btn.disabled = !state.available;
            btn.classList.toggle('active', selected);
            btn.setAttribute('aria-pressed', selected ? 'true' : 'false');
            if (!state.available && state.reason) reasons.push(`「${option.label}」不能選：${state.reason}`);
        }
        const hint = options.find(o => o.value === current)?.hint ?? '';
        note.replaceChildren();
        note.textContent = reasons.length > 0 ? reasons.join(' ') : hint;
    }
    return { element: wrap, sync };
}

/**
 * @param {object} options
 * @param {string} options.source 問題來源
 * @param {number} options.eventId 事件 ID
 * @param {string} options.issueLabel 顯示用的問題標籤
 * @param {string|null} [options.ruleId] 這個問題對得到的規則（沒有規則就不能只對群組或單一主機設定）
 * @param {() => void} [options.onApplied] 套用成功後的重新載入
 */
async function buildHideTriagePanel({ source, eventId, issueLabel, ruleId = null, onApplied }) {
    let user;
    try {
        user = await getCurrentUser();
    } catch {
        return;
    }
    if (!hasCapability(user, 'Maintain')) return;

    const active = await loadActiveOrders(source, eventId);
    const canClose = hasCapability(user, 'Assign') && hasCapability(user, 'Handle');
    // 進行中的單超過一頁（100 張）時逐張代為結案會漏掉；寧可不提供也不做一半
    const ordersTruncated = active.orders > active.ids.length;
    const ctx = {
        source, eventId, issueLabel, ruleId,
        activeOrders: active.orders, orderIds: active.ids,
        canClose: canClose && !ordersTruncated
    };

    const answers = { duration: 'temporary', scope: 'site', orders: 'pause' };
    const params = { days: DEFAULT_DAYS, reason: '', hostGroupId: '', host: '', groupName: '', conclusion: TRIAGE_CONCLUSIONS[0].value };
    let groups = null;   // null＝尚未載入；[] 載入失敗或沒有

    let modal = null;
    const body = document.createElement('div');
    const form = document.createElement('form');
    body.appendChild(form);

    const lead = document.createElement('div');
    lead.className = 'lf-hint mb-3';
    lead.textContent = '回答三個問題，系統會選對應的做法並先說明後果，確認後才套用。六種做法的差別見問題查詢頁的比較表。';
    form.appendChild(lead);

    // ── 1. 多久 ───────────────────────────────────────────────────────────
    const duration = segmented('多久', TRIAGE_DURATIONS, {
        value: answers.duration,
        onChange: value => { answers.duration = value; refresh(); }
    });
    const daysWrap = document.createElement('div');
    daysWrap.className = 'mb-3';
    const daysLabel = document.createElement('label');
    daysLabel.className = 'form-label lf-text-label';
    daysLabel.textContent = '天數';
    const daysSelect = document.createElement('select');
    daysSelect.className = 'form-select form-select-sm w-auto';
    daysLabel.htmlFor = daysSelect.id = `lf-triage-days-${Math.random().toString(36).slice(2, 8)}`;
    for (const days of DAY_CHOICES) {
        const option = document.createElement('option');
        option.value = String(days);
        option.textContent = days === 1 ? '1 天' : `${days} 天`;
        if (days === DEFAULT_DAYS) option.selected = true;
        daysSelect.appendChild(option);
    }
    daysSelect.addEventListener('change', () => { params.days = Number(daysSelect.value) || DEFAULT_DAYS; refresh(); });
    daysWrap.append(daysLabel, daysSelect);
    form.append(duration.element, daysWrap);

    // ── 2. 範圍 ───────────────────────────────────────────────────────────
    const scope = segmented('範圍', TRIAGE_SCOPES, {
        value: answers.scope,
        onChange: value => { answers.scope = value; refresh(); }
    });
    const groupWrap = document.createElement('div');
    groupWrap.className = 'mb-3 d-none';
    const groupLabel = document.createElement('label');
    groupLabel.className = 'form-label lf-text-label';
    groupLabel.textContent = '主機群組';
    const groupSelect = document.createElement('select');
    groupSelect.className = 'form-select form-select-sm';
    groupLabel.htmlFor = groupSelect.id = `lf-triage-group-${Math.random().toString(36).slice(2, 8)}`;
    groupSelect.addEventListener('change', () => {
        params.hostGroupId = groupSelect.value;
        params.groupName = groupSelect.selectedOptions[0]?.textContent ?? '';
        refresh();
        loadGroupPreview();
    });
    groupWrap.append(groupLabel, groupSelect);

    const hostWrap = document.createElement('div');
    hostWrap.className = 'mb-3 d-none';
    const hostLabel = document.createElement('div');
    hostLabel.className = 'form-label lf-text-label';
    hostLabel.textContent = '主機';
    let hostWidget = null;
    hostWrap.appendChild(hostLabel);
    form.append(scope.element, groupWrap, hostWrap);

    // ── 3. 已交辦的單（沒有進行中的單就不問）──────────────────────────────
    const orders = segmented('已交辦的單', TRIAGE_ORDERS, {
        value: answers.orders,
        onChange: value => { answers.orders = value; refresh(); }
    });
    const ordersSummary = document.createElement('div');
    ordersSummary.className = 'lf-text-meta mb-1';
    ordersSummary.textContent = `這個問題目前有 ${active.orders} 張進行中交辦單、共 ${active.hosts} 台。`;
    const ordersBlock = document.createElement('div');
    ordersBlock.append(ordersSummary, orders.element);
    if (active.orders > 0) form.appendChild(ordersBlock);
    if (ordersTruncated) {
        const note = document.createElement('div');
        note.className = 'lf-hint mb-3';
        note.textContent = `進行中的單超過 ${active.ids.length} 張，逐張代為結案可能漏掉，所以不提供「代為結案」。`;
        ordersBlock.appendChild(note);
    }

    // ── 長期＋全站：結論 ─────────────────────────────────────────────────
    const conclusion = segmented('標成什麼結論', TRIAGE_CONCLUSIONS.map(c => ({ ...c, hint: '' })), {
        value: params.conclusion,
        onChange: value => { params.conclusion = value; refresh(); }
    });
    form.appendChild(conclusion.element);

    // ── 原因 ─────────────────────────────────────────────────────────────
    const reasonLabel = document.createElement('label');
    reasonLabel.className = 'form-label lf-text-label';
    reasonLabel.textContent = '原因（必填）';
    const reasonInput = document.createElement('textarea');
    reasonInput.className = 'form-control form-control-sm';
    reasonInput.rows = 2;
    reasonInput.maxLength = 500;
    reasonLabel.htmlFor = reasonInput.id = `lf-triage-reason-${Math.random().toString(36).slice(2, 8)}`;
    reasonInput.addEventListener('input', () => { params.reason = reasonInput.value; refresh(); });
    const reasonWrap = document.createElement('div');
    reasonWrap.className = 'mb-3';
    reasonWrap.append(reasonLabel, reasonInput);
    form.appendChild(reasonWrap);

    // ── 後果摘要與送出 ────────────────────────────────────────────────────
    const summaryBox = document.createElement('div');
    summaryBox.className = 'border rounded p-2 mb-3';
    summaryBox.setAttribute('role', 'status');
    summaryBox.setAttribute('aria-live', 'polite');
    const errorSlot = document.createElement('div');
    form.append(summaryBox, errorSlot);

    const submitBtn = button('套用', { variant: 'primary', type: 'submit' });
    form.appendChild(submitBtn);

    const groupPreviewGuard = createSeqGuard();
    let groupPreviewText = '';

    async function ensureGroups() {
        if (groups !== null) return;
        groups = [];
        try {
            const result = await api.get('/api/admin/host-groups', { silent: true });
            groups = (result ?? []).filter(g => g.active);
        } catch {
            groups = [];
        }
        groupSelect.replaceChildren();
        const placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.textContent = '請選擇主機群組…';
        groupSelect.appendChild(placeholder);
        for (const group of groups) {
            const option = document.createElement('option');
            option.value = String(group.groupId);
            option.textContent = group.groupName;
            groupSelect.appendChild(option);
        }
    }

    /** 指定群組時的影響面：與規則維護的抑制預覽同一個端點。取不到就不顯示，不擋套用 */
    async function loadGroupPreview() {
        groupPreviewText = '';
        if (answers.scope !== 'group' || !params.hostGroupId || !ruleId) { renderSummary(); return; }
        const seq = groupPreviewGuard.next();
        try {
            const query = new URLSearchParams({ scope: 'Group', hostGroupId: String(params.hostGroupId) });
            const preview = await api.get(`/api/rules/${encodeURIComponent(ruleId)}/suppression-preview?${query}`, { silent: true });
            if (!groupPreviewGuard.isCurrent(seq)) return;
            groupPreviewText = `這個群組有 ${preview.affectedHostCount} 台主機；過去 ${preview.windowDays} 天這條規則在這些主機共命中 ${preview.recentHitCount} 次。`;
        } catch {
            if (!groupPreviewGuard.isCurrent(seq)) return;
            groupPreviewText = '';
        }
        renderSummary();
    }

    function currentPlan() {
        answers.orders = coerceOrders(answers, ctx);
        return buildTriagePlan(answers, params, ctx);
    }

    function renderSummary() {
        const plan = currentPlan();
        summaryBox.replaceChildren();
        const heading = document.createElement('div');
        heading.className = 'lf-text-label mb-1';
        heading.textContent = '套用後會發生什麼';
        summaryBox.appendChild(heading);
        if (!plan) {
            summaryBox.appendChild(roleText('lf-text-meta', '這個答案組合沒有對應的做法，請改選。'));
            return;
        }
        const lines = triageSummary(answers, params, ctx, plan);
        lines.forEach((line, index) => {
            const row = document.createElement('div');
            if (index === 0) row.className = 'lf-text-title';
            row.textContent = line;
            summaryBox.appendChild(row);
        });
        if (groupPreviewText && answers.scope === 'group') {
            const preview = document.createElement('div');
            preview.className = 'lf-text-meta';
            preview.textContent = groupPreviewText;
            summaryBox.appendChild(preview);
        }
    }

    function refresh() {
        // 範圍沒有可用規則時，群組與單一主機不能選；已選的要退回全站
        if (!scopeAvailability(answers.scope, ctx).available) answers.scope = 'site';
        answers.orders = coerceOrders(answers, ctx);
        duration.sync(answers.duration);
        daysWrap.classList.toggle('d-none', answers.duration !== 'temporary');
        scope.sync(answers.scope, value => scopeAvailability(value, ctx));
        groupWrap.classList.toggle('d-none', answers.scope !== 'group');
        hostWrap.classList.toggle('d-none', answers.scope !== 'host');
        if (answers.scope === 'group') ensureGroups();
        if (answers.scope === 'host' && !hostWidget) {
            hostWidget = searchableHostSelect({
                platform: null,
                onChange: value => { params.host = value; refresh(); }
            });
            hostWrap.appendChild(hostWidget.element);
        }
        if (active.orders > 0) {
            orders.sync(answers.orders, value => ordersAvailability(value, answers, ctx));
        }
        conclusion.element.classList.toggle('d-none', !(answers.scope === 'site' && answers.duration === 'permanent'));
        conclusion.sync(params.conclusion);
        renderSummary();
    }
    refresh();

    form.addEventListener('submit', async event => {
        event.preventDefault();
        errorSlot.replaceChildren();
        const problem = validateTriage(answers, params, ctx);
        if (problem) {
            errorSlot.appendChild(statusNote('error', problem));
            return;
        }
        const plan = currentPlan();
        const restore = withBusy(submitBtn, '套用中');
        const outcome = await applyTriage(plan, api);
        if (outcome.failed) {
            // 錯誤訊息已由 api.js 出 toast；做到一半的要誠實說，已完成的步驟不回滾
            if (outcome.done > 0) {
                toast(`已完成 ${outcome.done} 步，第 ${outcome.done + 1} 步失敗：已完成的設定仍然有效，請核對後再處理剩下的。`, 'warning');
                onApplied?.();
            }
            restore();
            return;
        }
        const closeOutcome = outcome.results[0]?.muteCloseOutcome;
        if (closeOutcome?.failedWorkOrderId != null) {
            toast(`已靜音，${closeOutcome.succeeded?.length ?? 0} 張交辦單已結案；#${closeOutcome.failedWorkOrderId} 失敗，另有 ${closeOutcome.notProcessed?.length ?? 0} 張未處理。請先核對再重試。`, 'warning');
        } else {
            toast(`已用「${TRIAGE_MECHANISM_NAMES[plan.mechanism]}」不再顯示『${issueLabel}』`, 'success');
        }
        modal?.close();
        onApplied?.();
    });

    modal = showDetailModal({ title: `不再顯示這個問題：${issueLabel}`, body, size: 'modal-lg' });
}

/** 分流面板的唯一入口：前置請求回來前再觸發會被忽略，不會疊開第二個對話框 */
export const openHideTriage = exclusive(buildHideTriagePanel);

/** 動作按鈕（問題查詢「更多」選單與交辦單頁捷徑共用的文字與提示） */
export const HIDE_TRIAGE_ACTION = {
    text: '不再顯示這個問題…',
    title: '回答三個問題，系統選對應的做法並先說明後果'
};
