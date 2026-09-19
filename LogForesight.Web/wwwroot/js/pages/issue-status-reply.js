/**
 * 回覆處理狀態 modal（共用元件）：欄位、必填規則與狀態值域只有這一份——
 * 值域與風險日詳情的問題層級狀態一致（core 的 IssueHandlingStatuses）。
 *
 * 分工：`openWorkOrderReplyModal` 只負責「填什麼、怎麼驗」；「回覆哪些對象、打哪支端點」
 * 由呼叫端以 `submit` 帶進來。處理人工作頁的兩條動線（整張單／單內選取主機）與
 * 問題查詢的「依問題」視角都走這顆 modal，兩邊各寫一份遲早會漂移成兩套必填規則。
 * 入口的顯示條件（「這個問題／這張單的處理人是自己」）由呼叫端判斷——後端另有同一條限制。
 */

import { api, getCurrentUser } from '../core/api.js';
import { toast, withBusy, showDetailModal, confirmAction } from '../core/ui.js';
import { attachNoteEditor } from '../core/note-editor.js';

/** escalated：「我處理不了，需要上報」——非結案，後端會即時通知 admin 群組決定結案或重新指派 */
const STATUS_OPTIONS = [
    { value: 'in_progress', label: '處理中' },
    { value: 'observing', label: '觀察中' },
    { value: 'resolved', label: '已處理' },
    { value: 'wont_fix', label: '不處理' },
    { value: 'false_positive', label: '誤報' },
    { value: 'known_noise', label: '已知雜訊' },
    { value: 'escalated', label: '無法處理（上報管理員）' }
];

/**
 * @param {object} options
 * @param {string} options.title modal 標題
 * @param {string} options.targetText 最上方的對象說明（呼叫端決定，例如「本單 96 台中的 5 台」）
 * @param {(payload: {status: string, note: string|null, dueDate: string|null}) => Promise<any>} options.submit
 *        呼叫端提供的送出函式；成功與否的訊息由呼叫端自己出
 * @param {() => void} [options.onApplied] 送出成功後的重新載入
 * @param {string} options.draftKey 草稿識別（必填）：可區分回覆對象的字串，下次打開同一批對象會還原草稿
 */
export function openWorkOrderReplyModal({ title, targetText, draftKey, submit, onApplied }) {
    const body = document.createElement('div');
    const form = document.createElement('form');
    form.noValidate = true;   // 超過字數的 customValidity 走下方手動驗證，不跳原生泡泡

    const hint = document.createElement('div');
    hint.className = 'lf-hint mb-3';
    hint.textContent = `套用對象：${targetText}；每台主機的案件會連同它涵蓋的日期一起更新。`;
    form.appendChild(hint);

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label small text-muted';
    statusLabel.textContent = '處理狀態';
    const statusSelect = document.createElement('select');
    statusSelect.className = 'form-select form-select-sm mb-3';
    for (const option of STATUS_OPTIONS) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        statusSelect.appendChild(el);
    }
    form.append(statusLabel, statusSelect);

    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small text-muted';
    noteLabel.textContent = '處理說明';
    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm mb-3';
    noteInput.rows = 3;
    form.append(noteLabel, noteInput);
    const noteEditor = attachNoteEditor(noteInput, { draftKey: `wo-reply:${draftKey}` });

    // 處理中的預計完成日／觀察中的觀察至日期共用同一個欄位（後端同一個 DueDate）
    const dueLabel = document.createElement('label');
    dueLabel.className = 'form-label small text-muted';
    dueLabel.textContent = '預計完成日／觀察至';
    const dueInput = document.createElement('input');
    dueInput.type = 'date';
    dueInput.className = 'form-control form-control-sm mb-3';
    form.append(dueLabel, dueInput);

    function syncDueVisibility() {
        const needsDue = statusSelect.value === 'in_progress' || statusSelect.value === 'observing';
        dueLabel.classList.toggle('d-none', !needsDue);
        dueInput.classList.toggle('d-none', !needsDue);
    }
    statusSelect.addEventListener('change', syncDueVisibility);
    syncDueVisibility();

    const submitBtn = document.createElement('button');
    submitBtn.type = 'submit';
    submitBtn.className = 'btn btn-sm btn-primary';
    submitBtn.textContent = '送出';
    form.appendChild(submitBtn);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        // 「不處理」必須說明理由——與風險日詳情的規則一致（那裡也是不處理→說明必填）
        if (statusSelect.value === 'wont_fix' && !noteInput.value.trim()) {
            toast('標記為「不處理」時請填寫說明', 'warning');
            noteInput.focus();
            return;
        }
        // 「無法處理」必填原因：管理員收到上報通知要據此決定結案或改派
        if (statusSelect.value === 'escalated' && !noteInput.value.trim()) {
            toast('標記為「無法處理」時請填寫原因，管理員將據此決定結案或重新指派', 'warning');
            noteInput.focus();
            return;
        }
        if (!noteInput.checkValidity()) {
            toast(noteInput.validationMessage, 'warning');   // 超過字數上限（attachNoteEditor 設的）
            noteInput.focus();
            return;
        }
        if (statusSelect.value === 'observing' && !dueInput.value) {
            toast('標記為「觀察中」時請指定觀察至日期', 'warning');
            return;
        }

        const restore = withBusy(submitBtn, '送出中');
        try {
            await submit({
                status: statusSelect.value,
                note: noteInput.value.trim() || null,
                dueDate: dueInput.value || null
            });

            noteEditor.clearDraft();
            closeConfirmed = true;
            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            onApplied?.();
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    showDetailModal({ title, body });

    // 關閉保護（DESIGN-SYSTEM §6b）：Esc／點遮罩／關閉鈕都走 hide.bs.modal，說明欄有字時先問
    let closeConfirmed = false;
    const modalEl = body.closest('.modal');
    modalEl?.addEventListener('hide.bs.modal', async event => {
        if (closeConfirmed || !noteInput.value.trim()) return;
        event.preventDefault();
        const confirmed = await confirmAction({
            title: '放棄未送出的處理說明？',
            message: '草稿已自動保留，下次打開同一張單會還原。',
            confirmText: '關閉'
        });
        if (!confirmed) return;
        closeConfirmed = true;
        bootstrap.Modal.getInstance(modalEl)?.hide();
    });
}

/** 回覆多張交辦單的草稿識別：單號排序後以逗號連接，同一批單不論勾選順序都對到同一份草稿 */
export function workOrdersDraftKey(workOrderIds) {
    return `orders:${[...workOrderIds].sort((a, b) => a - b).join(',')}`;
}

/** 單張交辦單回覆的成功訊息（POST /api/work-orders/{id}/reply 的回應） */
export function toastReplyResult(result) {
    const closed = result.workOrderClosed ? '，整張單已結案' : '';
    toast(`已回覆 ${result.cases} 台${closed}`, 'success');
    if (result.daySyncPendingCases > 0) {
        toast('逐日同步在背景進行', 'info');
    }
}

/** 多張交辦單回覆的成功訊息（POST /api/work-orders/reply-many 的回應） */
export function toastReplyManyResult(result) {
    const closed = result.closedWorkOrders > 0 ? `，其中 ${result.closedWorkOrders} 張結案` : '';
    toast(`已回覆 ${result.workOrders} 張單共 ${result.cases} 台${closed}`, 'success');
    if (result.daySyncPendingCases > 0) {
        toast('逐日同步在背景進行', 'info');
    }
}

/**
 * 依問題視角的入口：把「這個問題指派給我的進行中交辦單」全部帶進 reply-many。
 * @param {{source: string, eventId: number}} group 問題（Source＋EventId）
 * @param {() => void} onApplied 套用成功後的重新載入
 */
export async function openIssueStatusReplyModal(group, onApplied) {
    const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '100' });
    if (group.source) params.set('source', group.source);
    if (group.eventId !== null && group.eventId !== undefined) params.set('eventId', String(group.eventId));

    // 呼叫端是 click 監聽（沒有人接這個 Promise），取單失敗要在這裡收掉——
    // api.js 已經出過錯誤 toast，往外擲只會變成 unhandled rejection
    let list;
    try {
        const user = await getCurrentUser();
        list = await api.get(`/api/handlers/${user.userId}/work-orders?${params}`);
    } catch {
        return;
    }

    const orders = list?.items ?? [];
    if (orders.length === 0) {
        toast('這個問題目前沒有指派給您的交辦單', 'info');
        return;
    }

    const hosts = orders.reduce((sum, order) => sum + (order.counts?.active ?? 0), 0);
    const workOrderIds = orders.map(order => order.workOrderId);

    openWorkOrderReplyModal({
        title: `回覆處理狀態：${group.source} (${group.eventId})`,
        targetText: `${orders.length} 張單共 ${hosts} 台`,
        draftKey: workOrdersDraftKey(workOrderIds),
        submit: async payload => {
            const result = await api.post('/api/work-orders/reply-many', { workOrderIds, ...payload });
            toastReplyManyResult(result);
        },
        onApplied
    });
}
