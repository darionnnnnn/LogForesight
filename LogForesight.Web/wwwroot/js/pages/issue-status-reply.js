/**
 * 跨主機回覆處理狀態（docs/archive/FEEDBACK-10-PLAN.md §11）：同一個問題被指派到多台主機時，
 * 處理人在這裡填一次就套用到**自己名下**的全部進行中案件，不必逐台進詳情頁標一樣的狀態。
 *
 * 共用元件（docs/archive/FEEDBACK-11-PLAN.md §7 自 records.js 抽出）：問題查詢「依問題」視角與
 * 處理人工作頁的「依問題」視角都要這顆按鈕，兩邊各寫一份遲早會漂移成兩套必填規則。
 * 入口的顯示條件（「這個問題的處理人包含自己」）由呼叫端判斷——後端另有同一條限制。
 */

import { api } from '../core/api.js';
import { toast, withBusy, showDetailModal } from '../core/ui.js';

/** 值域與風險日詳情的問題層級狀態一致（core 的 IssueHandlingStatuses）。
 * escalated（回饋十八輪批次G）：「我處理不了，需要上報」——非結案，後端會即時通知
 * admin 群組決定結案或重新指派 */
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
 * @param {{source: string, eventId: number}} group 問題（Source＋EventId）
 * @param {() => void} onApplied 套用成功後的重新載入
 */
export function openIssueStatusReplyModal(group, onApplied) {
    const body = document.createElement('div');
    const form = document.createElement('form');

    const hint = document.createElement('div');
    hint.className = 'lf-hint mb-3';
    hint.textContent = '套用對象是這個問題目前指派給您、且尚未結案的全部主機；' +
        '每台主機的案件會連同它涵蓋的日期一起更新。';
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

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary';
    submit.textContent = '送出';
    form.appendChild(submit);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        // 「不處理」必須說明理由——與風險日詳情的規則一致（那裡也是不處理→說明必填）
        if (statusSelect.value === 'wont_fix' && !noteInput.value.trim()) {
            toast('標記為「不處理」時請填寫說明', 'warning');
            return;
        }
        // 「無法處理」必填原因（回饋十八輪批次G）：管理員收到上報通知要據此決定結案或改派
        if (statusSelect.value === 'escalated' && !noteInput.value.trim()) {
            toast('標記為「無法處理」時請填寫原因，管理員將據此決定結案或重新指派', 'warning');
            return;
        }
        if (statusSelect.value === 'observing' && !dueInput.value) {
            toast('標記為「觀察中」時請指定觀察至日期', 'warning');
            return;
        }

        const restore = withBusy(submit, '送出中');
        try {
            const result = await api.post('/api/handling/issue-cases/bulk-status', {
                source: group.source,
                eventId: group.eventId,
                status: statusSelect.value,
                note: noteInput.value.trim() || null,
                dueDate: dueInput.value || null
            });

            toast(`已更新 ${result.updatedCaseCount} 台主機、共 ${result.updatedDayCount} 天`, 'success');
            body.closest('.modal')?.querySelector('[data-bs-dismiss="modal"]')?.click();
            onApplied?.();
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    showDetailModal({ title: `回覆處理狀態：${group.source} (${group.eventId})`, body });
}
