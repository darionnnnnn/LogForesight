/**
 * 風險日詳情的處理面板（docs/WEB-SPEC.md §9.3）。
 *
 * 核心情境：主機 A 的負責人是 OOO，但主管認為問題緊急、先交給 XXX 處理——
 * 此時**負責人不變**（唯讀顯示），變的是這個風險日的處理人（只有 admin 能改）。
 * 畫面上兩者分成兩行並存，就是為了讓這個區別一眼可見。
 */

import { api } from '../core/api.js';
import { renderLoading, renderEmpty, toast, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const STATUS_OPTIONS = [
    { value: 'open', text: '未處理' },
    { value: 'in_progress', text: '處理中' },
    { value: 'resolved', text: '已處理' },
    { value: 'wont_fix', text: '不處理（評估後決定）' },
    { value: 'false_positive', text: '誤報' },
    { value: 'known_noise', text: '已知雜訊' }
];

const STATUS_VARIANTS = {
    open: 'danger', in_progress: 'primary', resolved: 'success',
    wont_fix: 'secondary', false_positive: 'secondary', known_noise: 'secondary'
};

export async function initHandlingPanel(hostId, date) {
    const panel = document.getElementById('handling-panel');
    renderLoading(panel, 3);

    const handling = await api.get(`/api/records/${hostId}/${date}/handling`);

    // 指派下拉的人選**只在有 Assign 能力時**才載入：
    // 無條件呼叫 /api/admin/users 會讓每個 user 每次開詳情頁都產生一筆
    // 403＋access_denied 稽核——「權限不足被拒」是稽核上最有價值的訊號，
    // 被正常瀏覽的噪音淹沒後，真正的權限試探就再也看不出來了。
    const users = handling.canAssign ? await loadAssignableUsers() : [];

    render(panel, handling, users, hostId, date);
    await loadLogs(hostId, date);
}

async function loadAssignableUsers() {
    try {
        return await api.get('/api/admin/users', { silent: true });
    } catch {
        return [];
    }
}

function render(panel, handling, users, hostId, date) {
    panel.replaceChildren();

    // ── 負責人（唯讀）：主機的長期屬性，改派處理人不會動到它 ──
    panel.appendChild(readonlyField(
        '主機負責人',
        handling.ownerNames.length ? handling.ownerNames.join('、') : '未指定',
        '主機的長期屬性，於「主機」維護頁調整'
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
        panel.appendChild(readonlyField('處理狀態', handling.statusText));
        if (handling.note) panel.appendChild(readonlyField('處理說明', handling.note));
        return;
    }

    panel.appendChild(handlingForm(handling, hostId, date));
}

function readonlyField(label, value, hint) {
    const wrap = document.createElement('div');
    wrap.className = 'mb-3';

    const labelEl = document.createElement('div');
    labelEl.className = 'form-label small mb-1 text-muted';
    labelEl.textContent = label;

    const valueEl = document.createElement('div');
    valueEl.className = 'fw-semibold';
    valueEl.textContent = value;

    wrap.append(labelEl, valueEl);

    if (hint) {
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
            ? `${user.displayName}（負責人）`
            : user.displayName;
        option.selected = user.userId === handling.handlerId;
        select.appendChild(option);
    }

    select.addEventListener('change', async () => {
        select.disabled = true;
        try {
            const updated = await api.put(`/api/records/${hostId}/${date}/handling/assign`, {
                handlerId: select.value ? Number(select.value) : null
            });
            toast(updated.handlerName ? `已指派給 ${updated.handlerName}` : '已取消指派', 'success');
            await initHandlingPanel(hostId, date);
        } catch {
            select.disabled = false;
        }
    });

    const hint = document.createElement('div');
    hint.className = 'form-text';
    hint.textContent = '可指派給負責人以外的人；主機負責人不會因此改變。';

    wrap.append(label, select, hint);
    return wrap;
}

function handlingForm(handling, hostId, date) {
    const form = document.createElement('form');

    // 狀態
    const statusWrap = document.createElement('div');
    statusWrap.className = 'mb-3';

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label small mb-1 text-muted';
    statusLabel.textContent = '處理狀態';
    statusLabel.htmlFor = 'status-select';

    const statusSelect = document.createElement('select');
    statusSelect.className = 'form-select form-select-sm';
    statusSelect.id = 'status-select';
    for (const option of STATUS_OPTIONS) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.text;
        el.selected = option.value === handling.status;
        statusSelect.appendChild(el);
    }
    statusWrap.append(statusLabel, statusSelect);

    // 預計完成日
    const dueWrap = document.createElement('div');
    dueWrap.className = 'mb-3';

    const dueLabel = document.createElement('label');
    dueLabel.className = 'form-label small mb-1 text-muted';
    dueLabel.textContent = '預計完成日';
    dueLabel.htmlFor = 'due-date';

    const dueInput = document.createElement('input');
    dueInput.type = 'date';
    dueInput.className = 'form-control form-control-sm';
    dueInput.id = 'due-date';
    dueInput.value = handling.dueDate ?? '';
    dueWrap.append(dueLabel, dueInput);

    if (handling.isOverdue) {
        const overdue = document.createElement('div');
        overdue.className = 'form-text text-danger fw-semibold';
        overdue.textContent = '⚠ 已逾期';
        dueWrap.appendChild(overdue);
    }

    // 處理說明
    const noteWrap = document.createElement('div');
    noteWrap.className = 'mb-3';

    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label small mb-1 text-muted';
    noteLabel.textContent = '處理說明';
    noteLabel.htmlFor = 'handling-note';

    const noteInput = document.createElement('textarea');
    noteInput.className = 'form-control form-control-sm';
    noteInput.id = 'handling-note';
    noteInput.rows = 3;
    noteInput.value = handling.note ?? '';
    noteInput.placeholder = '例如：已確認為每週維護重開機，屬正常現象';

    const noteHint = document.createElement('div');
    noteHint.className = 'form-text';
    noteHint.textContent = '每次更新都會留在處理歷程中，不會覆蓋先前的說明。';

    noteWrap.append(noteLabel, noteInput, noteHint);

    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn btn-sm btn-primary w-100';
    submit.textContent = '儲存';

    form.append(statusWrap, dueWrap, noteWrap, submit);

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const restore = withBusy(submit, '儲存中');
        try {
            await api.put(`/api/records/${hostId}/${date}/handling`, {
                status: statusSelect.value,
                note: noteInput.value.trim() || null,
                dueDate: dueInput.value || null
            });
            toast('已更新處理狀態', 'success');
            await initHandlingPanel(hostId, date);
        } catch {
            restore();
        }
    });

    if (handling.updatedAt) {
        const updated = document.createElement('div');
        updated.className = 'form-text mt-2';
        updated.textContent = `最後更新：${formatDateTime(handling.updatedAt)}`;
        form.appendChild(updated);
    }

    return form;
}

/**
 * 處理歷程 timeline：完整敘事（指派 → 查修中 → 換了硬碟 → 結案）。
 * 這正是快照與歷程分兩份儲存的目的——單一說明欄位會把前面的過程蓋掉。
 */
async function loadLogs(hostId, date) {
    const container = document.getElementById('handling-log');
    const logs = await api.get(`/api/records/${hostId}/${date}/handling/logs`);

    if (logs.length === 0) {
        renderEmpty(container, { title: '尚無處理紀錄', hint: '更新處理狀態後，這裡會留下完整的處理過程。' });
        return;
    }

    const list = document.createElement('div');

    for (const log of logs) {
        const item = document.createElement('div');
        item.className = 'border-start border-3 ps-3 pb-3 mb-1';

        const head = document.createElement('div');
        head.className = 'd-flex align-items-center gap-2 flex-wrap';

        const action = document.createElement('span');
        action.className = 'fw-semibold small';
        action.textContent = log.actionText;

        const status = document.createElement('span');
        status.className = `badge text-bg-${STATUS_VARIANTS[log.status] ?? 'secondary'}`;
        status.textContent = log.statusText;

        head.append(action, status);

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
        meta.textContent = `${formatDateTime(log.createdAt)}　操作者：${log.actorAccount || '（系統）'}`;
        item.appendChild(meta);

        list.appendChild(item);
    }

    container.replaceChildren(list);
}
