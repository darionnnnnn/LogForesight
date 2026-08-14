/**
 * 問題負責人維護（回饋十八輪批次F）。
 *
 * 以 (Source, EventId) 為鍵指派跨主機負責人——與主機負責人（hosts.js 的 #host-owners）
 * 是相同概念、相同管理模式，唯一差別是鍵從「主機」換成「問題」。優先於主機負責人：
 * 自動帶入處理人、郵件通知路由都會先看這裡的規則。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, withBusy, confirmAction, checkboxList, button, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

const listContainer = document.getElementById('issue-owner-list');
const form = document.getElementById('issue-owner-form');
const modal = new bootstrap.Modal(document.getElementById('issue-owner-modal'));
const picker = document.getElementById('issue-owner-picker');
const manualToggle = document.getElementById('issue-owner-manual-toggle');
const manualField = document.getElementById('issue-owner-manual-field');
const pickerField = document.getElementById('issue-owner-picker-field');
const selectedSummary = document.getElementById('issue-owner-selected-summary');

let rules = [];
let recentIssues = [];
let users = [];
let editingRule = null;   // null＝新增；否則正在編輯的既有規則（鍵不可改，只能改負責人／備註）

async function load() {
    renderLoading(listContainer, 4);

    const [ruleList, recent, userList] = await Promise.all([
        api.get('/api/admin/issue-owners'),
        api.get('/api/admin/issue-owners/recent-issues'),
        api.get('/api/admin/users')
    ]);
    rules = ruleList;
    recentIssues = recent;
    users = userList;

    renderList();
}

function renderList() {
    renderTable(listContainer, {
        columns: [
            {
                title: '問題',
                render: r => {
                    const main = document.createElement('div');
                    main.className = 'fw-semibold';
                    main.textContent = `${r.sourceName} (${r.eventId})`;
                    return main;
                }
            },
            {
                title: '負責人',
                render: r => r.ownerNames.length > 0 ? r.ownerNames.join('、') : '（無）'
            },
            {
                title: '近期出現（30 天）',
                className: 'text-end',
                render: r => r.recentHostCount == null
                    ? renderMuted('尚未出現')
                    : `${r.recentHostCount} 台主機，最近 ${formatDateTime(r.recentLastSeen).slice(0, 10)}`
            },
            { title: '備註', render: r => r.note || '' },
            {
                title: '更新',
                className: 'text-nowrap',
                render: r => r.updatedAt
                    ? renderMuted(`${formatDateTime(r.updatedAt)}${r.updatedByAccount ? `（${formatUserName(r.updatedByDisplayName, r.updatedByAccount)}）` : ''}`)
                    : ''
            },
            {
                title: '',
                className: 'text-end',
                render: r => {
                    const wrap = document.createElement('div');
                    wrap.className = 'd-flex gap-1 justify-content-end';
                    wrap.append(
                        button('編輯', { icon: 'pencil', onClick: () => openModal(r) }),
                        button('刪除', { icon: 'trash', variant: 'outline-danger', onClick: () => removeRule(r) })
                    );
                    return wrap;
                }
            }
        ],
        rows: rules,
        empty: {
            title: '尚未指派任何問題負責人',
            hint: '按右上角「新增規則」開始指派——問題負責人優先於主機負責人，自動帶入處理人與郵件通知都會先看這裡。'
        },
        stickyLastColumn: true
    });
}

function renderMuted(text) {
    const span = document.createElement('span');
    span.className = 'text-muted small';
    span.textContent = text;
    return span;
}

// ── 新增／編輯 modal ─────────────────────────────────────────────────────

document.getElementById('issue-owner-new').addEventListener('click', () => openModal(null));

function openModal(rule) {
    editingRule = rule;
    document.getElementById('issue-owner-modal-title').textContent = rule ? '編輯問題負責人' : '新增問題負責人規則';

    renderPicker(rule);
    setManualMode(!!rule && !recentIssues.some(o => matchesIssue(o, rule)));
    if (rule) {
        document.getElementById('issue-owner-source').value = rule.sourceName;
        document.getElementById('issue-owner-event-id').value = String(rule.eventId);
        document.getElementById('issue-owner-source').disabled = true;
        document.getElementById('issue-owner-event-id').disabled = true;
        picker.disabled = true;
    } else {
        document.getElementById('issue-owner-source').value = '';
        document.getElementById('issue-owner-event-id').value = '';
        document.getElementById('issue-owner-source').disabled = false;
        document.getElementById('issue-owner-event-id').disabled = false;
        picker.disabled = false;
    }
    // 編輯時鍵（Source/EventId）不可改——要換問題請刪除後重新指派，避免「編輯」跟「搬移規則」混淆
    manualToggle.classList.toggle('d-none', !!rule);

    document.getElementById('issue-owner-note').value = rule?.note ?? '';

    checkboxList(document.getElementById('issue-owner-users'), users.filter(u => u.active).map(u => ({
        id: u.userId,
        label: formatUserName(u.displayName, u.account),
        checked: rule?.ownerUserIds?.includes(u.userId) ?? false
    })), '尚無使用者，請先於「使用者」頁建立。');

    renderSelectedSummary();
    modal.show();
}

/** 目前選到／輸入的問題摘要——編輯時鍵已鎖定，新增時 picker／手動輸入互斥，
 * 使用者存檔前應該看得到「我現在到底要指派哪個問題」，不必回頭比對上方欄位。 */
function renderSelectedSummary() {
    let sourceName, eventId;
    if (editingRule) {
        sourceName = editingRule.sourceName;
        eventId = editingRule.eventId;
    } else if (!manualField.classList.contains('d-none')) {
        sourceName = document.getElementById('issue-owner-source').value.trim();
        eventId = document.getElementById('issue-owner-event-id').value;
    } else {
        const [source, id] = (picker.value || '').split('|');
        sourceName = source;
        eventId = id;
    }

    selectedSummary.textContent = sourceName && eventId ? `已選擇：${sourceName} (${eventId})` : '';
}

picker.addEventListener('change', renderSelectedSummary);
document.getElementById('issue-owner-source').addEventListener('input', renderSelectedSummary);
document.getElementById('issue-owner-event-id').addEventListener('input', renderSelectedSummary);

function matchesIssue(option, rule) {
    return option.eventId === rule.eventId && option.sourceName.toUpperCase() === rule.sourceName.toUpperCase();
}

function renderPicker(rule) {
    picker.replaceChildren();
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = recentIssues.length > 0 ? '請選擇問題…' : '近 30 天沒有可選的問題，請改為手動輸入';
    picker.appendChild(placeholder);

    for (const option of recentIssues) {
        const el = document.createElement('option');
        el.value = `${option.sourceName}|${option.eventId}`;
        el.textContent = `${option.sourceName} (${option.eventId}) — ${option.hostCount} 台主機` +
            (option.hasOwner && !(rule && matchesIssue(option, rule)) ? '（已指派）' : '');
        if (rule && matchesIssue(option, rule)) el.selected = true;
        picker.appendChild(el);
    }
}

manualToggle.addEventListener('click', () => setManualMode(true));

function setManualMode(manual) {
    pickerField.classList.toggle('d-none', manual);
    manualField.classList.remove('d-none');   // 手動欄位固定顯示，避免使用者以為輸入不見了
    manualField.classList.toggle('d-none', !manual);
    if (manual) {
        document.getElementById('issue-owner-source').focus();
    }
    renderSelectedSummary();
}

form.addEventListener('submit', async event => {
    event.preventDefault();

    let sourceName, eventId;
    if (editingRule) {
        sourceName = editingRule.sourceName;
        eventId = editingRule.eventId;
    } else if (!manualField.classList.contains('d-none')) {
        sourceName = document.getElementById('issue-owner-source').value.trim();
        eventId = Number(document.getElementById('issue-owner-event-id').value);
    } else {
        const [source, id] = (picker.value || '').split('|');
        sourceName = source;
        eventId = Number(id);
    }

    if (!sourceName || !eventId) {
        toast('請選擇或輸入要指派的問題', 'warning');
        return;
    }

    const ownerUserIds = Array.from(document.querySelectorAll('#issue-owner-users input:checked')).map(i => Number(i.value));

    const saveButton = document.getElementById('issue-owner-save');
    const restore = withBusy(saveButton, '儲存中');
    try {
        await api.put('/api/admin/issue-owners', {
            sourceName,
            eventId,
            ownerUserIds,
            note: document.getElementById('issue-owner-note').value.trim() || null
        });
        toast('已儲存問題負責人規則', 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

async function removeRule(rule) {
    const confirmed = await confirmAction({
        title: '刪除問題負責人規則',
        message: `確定要刪除「${rule.sourceName} (${rule.eventId})」的負責人規則嗎？刪除後這個問題會落回主機負責人（若有設定）。`,
        confirmText: '刪除',
        confirmVariant: 'danger'
    });
    if (!confirmed) return;

    try {
        await api.delete(`/api/admin/issue-owners/${encodeURIComponent(rule.sourceName)}/${rule.eventId}`);
        toast('已刪除', 'success');
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    }
}

guardLoad(listContainer, load);
