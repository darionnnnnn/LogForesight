/**
 * 靜音問題 modal（共用元件）：把一個問題（來源＋事件 ID）在一段期間內完全噤聲——
 * 期間內的新資料不進待辦、不告警、不列在清單，到期自動恢復。
 *
 * 只有這一份：問題查詢的「依問題」視角與問題檔案頁兩個入口都走它，兩邊各寫一份
 * 遲早會漂移成兩套時長換算與必填規則（同 issue-status-reply.js 的分工理由）。
 *
 * 後端（PUT /api/admin/issue-owners/{source}/{eventId}/mute）在今天已處於靜音區間時是
 * **延長**（重設迄日，可縮短）而非新增區間，所以 currentMute 有值時只換標題與按鈕文字，
 * 送出的請求本體完全相同。
 */

import { api, getCurrentUser, hasCapability } from '../core/api.js';
import { toast, withBusy, showDetailModal } from '../core/ui.js';
import { formatDateTime, toLocalDateString } from '../core/format.js';

/** 前五顆快捷時長；自訂以 days: null 表示，選中時才顯示天數輸入 */
const DURATION_OPTIONS = [
    { label: '1 天', days: 1 },
    { label: '1 週', days: 7 },
    { label: '1 個月', days: 30 },
    { label: '3 個月', days: 90 },
    { label: '6 個月', days: 180 },
    { label: '自訂', days: null }
];

const MIN_DAYS = 1;
const MAX_DAYS = 365;
const DEFAULT_CUSTOM_DAYS = 30;

/** 靜音區間含今天，所以迄日＝今天＋days−1（前端自己算，只用於成功訊息的顯示） */
function muteEndDate(days) {
    const d = new Date();
    d.setDate(d.getDate() + days - 1);
    return toLocalDateString(d);
}

function dateOnly(value) {
    return formatDateTime(value).slice(0, 10);
}

/**
 * 查這個問題目前進行中的交辦單（張數與台數）。
 * 這是加值資訊，取不到就當作沒有單：靜音本身不該被一次列表請求失敗卡住
 * （後端仍以預設的 pause 處置既有單）。
 */
async function loadActiveOrders(source, eventId) {
    const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '100' });
    if (source) params.set('source', source);
    if (eventId !== null && eventId !== undefined) params.set('eventId', String(eventId));

    try {
        const data = await api.get(`/api/work-orders?${params}`, { silent: true });
        const items = data?.items ?? [];
        // 張數取後端總數（同一問題一位處理人最多一張，實務上遠少於一頁）；台數加總本頁各單
        return {
            orders: data.total,
            hosts: items.reduce((sum, r) => sum + (r.counts?.active ?? 0), 0)
        };
    } catch {
        return { orders: 0, hosts: 0 };
    }
}

/**
 * @param {object} options
 * @param {string} options.source 問題來源
 * @param {number} options.eventId 事件 ID
 * @param {string} options.issueLabel 顯示用的問題標籤
 * @param {{to: string, reason: string, byAccount: string}|null} [options.currentMute] 目前靜音區間；有值＝這次是延長
 * @param {() => void} [options.onApplied] 套用成功後的重新載入
 */
export async function openIssueMuteModal({ source, eventId, issueLabel, currentMute, onApplied }) {
    // 呼叫端都是 click 監聽（沒有人接這個 Promise），失敗要在這裡收掉
    let user;
    try {
        user = await getCurrentUser();
    } catch {
        return;
    }
    const canClose = hasCapability(user, 'Assign') && hasCapability(user, 'Handle');
    const activeOrders = await loadActiveOrders(source, eventId);

    const isExtend = !!currentMute;
    const actionText = isExtend ? '延長靜音' : '靜音問題';

    const body = document.createElement('div');
    const form = document.createElement('form');

    // ── 1. 常駐提示：靜音做什麼、不做什麼、什麼情況該改用別的工具 ──────────
    for (const line of [
        '靜音期間的新資料不進待辦、不告警、不列在清單；到期自動恢復。',
        '靜音前就未處理的日子，到期後會回到待辦；要永久結案請改用「統一標記」。',
        '只想對某些主機或群組噤聲，請改用規則維護的「抑制」。'
    ]) {
        const hint = document.createElement('div');
        hint.className = 'lf-hint mb-2';
        hint.textContent = line;
        form.appendChild(hint);
    }

    // ── 2. 目前靜音資訊（延長情境）────────────────────────────────────────
    if (isExtend) {
        const current = document.createElement('div');
        current.className = 'alert alert-secondary py-2 small';
        current.textContent = `目前靜音至 ${dateOnly(currentMute.to)}｜原因：${currentMute.reason ?? ''}` +
            `｜設定者：${currentMute.byAccount ?? ''}`;
        form.appendChild(current);
    }

    // ── 3. 時長 ───────────────────────────────────────────────────────────
    const durationLabel = document.createElement('label');
    durationLabel.className = 'form-label small text-muted';
    durationLabel.textContent = '靜音時長';
    form.appendChild(durationLabel);

    const durationWrap = document.createElement('div');
    durationWrap.className = 'd-flex flex-wrap gap-1 mb-2';
    form.appendChild(durationWrap);

    const customWrap = document.createElement('div');
    customWrap.className = 'mb-3 d-none';
    const customInput = document.createElement('input');
    customInput.type = 'number';
    customInput.className = 'form-control form-control-sm';
    customInput.min = String(MIN_DAYS);
    customInput.max = String(MAX_DAYS);
    customInput.value = String(DEFAULT_CUSTOM_DAYS);
    const customHint = document.createElement('div');
    customHint.className = 'form-text';
    customHint.textContent = `天數（${MIN_DAYS}～${MAX_DAYS}）`;
    customWrap.append(customInput, customHint);
    form.appendChild(customWrap);

    let selected = DURATION_OPTIONS[1];   // 預設 1 週
    const durationButtons = [];
    for (const option of DURATION_OPTIONS) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary';
        btn.textContent = option.label;
        btn.addEventListener('click', () => {
            selected = option;
            syncDuration();
        });
        durationButtons.push({ option, btn });
        durationWrap.appendChild(btn);
    }

    function syncDuration() {
        for (const entry of durationButtons) {
            entry.btn.classList.toggle('active', entry.option === selected);
        }
        customWrap.classList.toggle('d-none', selected.days !== null);
    }
    syncDuration();

    // ── 4. 原因（必填）────────────────────────────────────────────────────
    const reasonLabel = document.createElement('label');
    reasonLabel.className = 'form-label small text-muted';
    reasonLabel.textContent = '原因';
    const reasonInput = document.createElement('textarea');
    reasonInput.className = 'form-control form-control-sm';
    reasonInput.rows = 2;
    reasonInput.maxLength = 500;
    const reasonError = document.createElement('div');
    reasonError.className = 'text-danger small d-none';
    reasonError.textContent = '請填寫靜音原因';
    form.append(reasonLabel, reasonInput, reasonError);

    // ── 5. 既有進行中交辦單處置（沒有單就整段不顯示）──────────────────────
    let existingOrdersValue = 'pause';
    if (activeOrders.orders > 0) {
        const section = document.createElement('div');
        section.className = 'mt-3';

        const summary = document.createElement('div');
        summary.className = 'small mb-1';
        summary.textContent = `這個問題目前有 ${activeOrders.orders} 張進行中交辦單、共 ${activeOrders.hosts} 台`;
        section.appendChild(summary);

        const groupName = `lf-mute-orders-${Math.random().toString(36).slice(2, 10)}`;
        const choices = [{ value: 'pause', label: '暫停，到期自動恢復' }];
        // 代為結案要同時具 Assign 與 Handle（後端同一條規則，沒有能力送出會整筆 403 零寫入）
        if (canClose) choices.push({ value: 'close', label: '代為結案為『不處理』' });

        for (const choice of choices) {
            const wrap = document.createElement('div');
            wrap.className = 'form-check';
            const input = document.createElement('input');
            input.type = 'radio';
            input.className = 'form-check-input';
            input.name = groupName;
            input.value = choice.value;
            input.id = `${groupName}-${choice.value}`;
            input.checked = choice.value === 'pause';
            input.addEventListener('change', () => {
                if (input.checked) existingOrdersValue = choice.value;
            });
            const label = document.createElement('label');
            label.className = 'form-check-label small';
            label.htmlFor = input.id;
            label.textContent = choice.label;
            wrap.append(input, label);
            section.appendChild(wrap);
        }

        if (!canClose) {
            const note = document.createElement('div');
            note.className = 'text-muted small';
            note.textContent = '代為結案需要指派與處理權限';
            section.appendChild(note);
        }

        form.appendChild(section);
    }

    // ── 6. 送出 ───────────────────────────────────────────────────────────
    const submitBtn = document.createElement('button');
    submitBtn.type = 'submit';
    submitBtn.className = 'btn btn-sm btn-primary mt-3';
    submitBtn.textContent = actionText;
    form.appendChild(submitBtn);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const reason = reasonInput.value.trim();
        reasonError.classList.toggle('d-none', !!reason);
        if (!reason) {
            reasonInput.focus();
            return;
        }

        let days = selected.days;
        if (days === null) {
            days = Number(customInput.value);
            if (!Number.isInteger(days) || days < MIN_DAYS || days > MAX_DAYS) {
                toast(`自訂天數請填 ${MIN_DAYS}～${MAX_DAYS} 之間的整數`, 'warning');
                return;
            }
        }

        const restore = withBusy(submitBtn, '送出中');
        try {
            await api.put(
                `/api/admin/issue-owners/${encodeURIComponent(source)}/${eventId}/mute`,
                { days, until: null, reason, existingOrders: existingOrdersValue });

            const closedSuffix = existingOrdersValue === 'close'
                ? `，${activeOrders.orders} 張交辦單已代為結案`
                : '';
            toast(`已靜音『${issueLabel}』至 ${muteEndDate(days)}${closedSuffix}`, 'success');

            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            onApplied?.();
        } catch {
            // 錯誤訊息已由 api.js 出 toast；modal 不關，讓使用者改完再送一次
            restore();
        }
    });

    body.appendChild(form);
    showDetailModal({ title: `${actionText}：${issueLabel}`, body });
}

/**
 * 提前解除靜音（DELETE 同一個端點）。成功訊息與重新載入由呼叫端的 onApplied 接手。
 */
export async function clearIssueMute(source, eventId) {
    await api.delete(`/api/admin/issue-owners/${encodeURIComponent(source)}/${eventId}/mute`);
}
