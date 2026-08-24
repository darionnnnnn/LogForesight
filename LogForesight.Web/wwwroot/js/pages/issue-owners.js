/**
 * 問題檔案維護（回饋十八輪批次F 建立「問題負責人」、回饋十九輪批次F 擴充機房結論）。
 *
 * 以 (Source, EventId) 為鍵指派跨主機負責人——與主機負責人（hosts.js 的 #host-owners）
 * 是相同概念、相同管理模式，唯一差別是鍵從「主機」換成「問題」。優先於主機負責人：
 * 自動帶入處理人、郵件通知路由都會先看這裡的規則。
 *
 * 機房結論（§2 決策一）與負責人是兩支獨立的 API（PUT .../conclusion、DELETE .../conclusion），
 * 不合併進負責人的 Upsert——後端 IssueOwnerAdminService.SetConclusion 有自己的驗證規則
 * （結案四態、原因必填），混進同一次送出會讓「只改負責人」的常見情境也要通過結論驗證。
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
const conclusionStatus = document.getElementById('issue-owner-conclusion-status');
const conclusionFields = document.getElementById('issue-owner-conclusion-fields');
const conclusionNote = document.getElementById('issue-owner-conclusion-note');
const conclusionAutoApply = document.getElementById('issue-owner-conclusion-auto-apply');
const conclusionCurrent = document.getElementById('issue-owner-conclusion-current');
const conclusionClearButton = document.getElementById('issue-owner-conclusion-clear');

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

/** 問題顯示標籤的單一出口：後端已算好 displayLabel（Linux 的 EventId 恆 0，不顯示無意義的「(0)」），
 * 舊資料或尚未帶欄位時退回本地推算 */
function issueLabel(item) {
    if (!item) return '';
    return item.displayLabel || (item.eventId === 0 ? item.sourceName : `${item.sourceName} (${item.eventId})`);
}

function renderList() {
    renderTable(listContainer, {
        columns: [
            {
                title: '問題',
                render: r => {
                    const main = document.createElement('div');
                    main.className = 'fw-semibold';
                    main.textContent = issueLabel(r);
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
                title: '機房結論',
                render: r => {
                    if (!r.conclusionStatus) return renderMuted('（無）');
                    const wrap = document.createElement('div');
                    const status = document.createElement('div');
                    status.textContent = r.conclusionStatusText + (r.autoApply ? '（自動套用中）' : '');
                    const note = document.createElement('div');
                    note.className = 'text-muted small';
                    note.textContent = r.conclusionNote || '';
                    wrap.append(status, note);
                    // 代全體下結論的留痕（批次I 體檢補上，與「更新」欄同一慣例）：
                    // 這個結論會自動套用到未來的主機日，之後有人問「這是誰決定的」要答得出來
                    if (r.concludedAt) {
                        const by = document.createElement('div');
                        by.className = 'text-muted small';
                        by.textContent = `由 ${formatUserName(r.concludedByDisplayName, r.concludedByAccount)} 於 ${formatDateTime(r.concludedAt)} 設定`;
                        wrap.appendChild(by);
                    }
                    return wrap;
                }
            },
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
            hint: '按右上角「新增規則」開始指派——問題負責人是長期負責人，該問題之後每天出現時會自動建案指派給他，郵件通知也優先看這裡。'
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
    document.getElementById('issue-owner-modal-title').textContent = rule ? '編輯問題檔案' : '新增問題檔案';

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
    })), '尚無使用者，請先於「使用者」頁建立。', { filterable: true });

    renderSelectedSummary();
    renderConclusionSection(rule);
    modal.show();
}

/**
 * 機房結論區塊（回饋十九輪批次F）：新增規則時還沒有問題檔案可設定結論——負責人跟結論
 * 是兩支獨立 API，沒存過負責人就沒有 (Source,EventId) 這個鍵讓結論掛上去，所以新增模式下
 * 整段隱藏，存好負責人、重新打開編輯才看得到。下拉維持空白＝這次儲存不動結論欄
 * （既有結論原封不動），選了狀態才會在送出時另外呼叫 SetConclusion。
 */
function renderConclusionSection(rule) {
    document.getElementById('issue-owner-conclusion-section').classList.toggle('d-none', !rule);

    conclusionStatus.value = '';
    conclusionNote.value = '';
    conclusionAutoApply.checked = false;
    syncConclusionFieldsVisibility();

    if (rule?.conclusionStatus) {
        conclusionCurrent.textContent = `目前結論：${rule.conclusionStatusText}${rule.autoApply ? '（自動套用中）' : ''} — ${rule.conclusionNote ?? ''}`;
        conclusionClearButton.classList.remove('d-none');
    } else {
        conclusionCurrent.textContent = rule ? '目前沒有機房結論。' : '';
        conclusionClearButton.classList.add('d-none');
    }
}

function syncConclusionFieldsVisibility() {
    conclusionFields.classList.toggle('d-none', !conclusionStatus.value);
}

conclusionStatus.addEventListener('change', syncConclusionFieldsVisibility);

conclusionClearButton.addEventListener('click', async () => {
    if (!editingRule) return;
    const targetLabel = issueLabel(editingRule);
    const confirmed = await confirmAction({
        title: '解除機房結論',
        message: `確定要解除「${targetLabel}」的機房結論嗎？已經套用到既有主機日的結果不會回滾。`,
        confirmText: '解除',
        confirmVariant: 'danger'
    });
    if (!confirmed) return;

    try {
        await api.delete(`/api/admin/issue-owners/${encodeURIComponent(editingRule.sourceName)}/${editingRule.eventId}/conclusion`);
        toast('已解除機房結論', 'success');
        await load();
        modal.hide();
    } catch {
        // 錯誤已由 api.js 顯示
    }
});

/** 目前選到／輸入的問題摘要——編輯時鍵已鎖定，新增時 picker／手動輸入互斥，
 * 使用者存檔前應該看得到「我現在到底要指派哪個問題」，不必回頭比對上方欄位。 */
function renderSelectedSummary() {
    let sourceName, eventId, displayLabel;
    if (editingRule) {
        sourceName = editingRule.sourceName;
        eventId = editingRule.eventId;
        displayLabel = editingRule.displayLabel;
    } else if (!manualField.classList.contains('d-none')) {
        sourceName = document.getElementById('issue-owner-source').value.trim();
        const raw = document.getElementById('issue-owner-event-id').value.trim();
        eventId = raw === '' ? NaN : Number(raw);
    } else {
        const [source, id] = (picker.value || '').split('|');
        sourceName = source;
        eventId = id !== undefined && id !== '' ? Number(id) : NaN;
        const selectedOption = recentIssues.find(o => matchesIssue(o, { sourceName, eventId }));
        displayLabel = selectedOption?.displayLabel;
    }

    const isValid = !!sourceName && Number.isInteger(eventId) && eventId >= 0;
    if (!isValid) {
        selectedSummary.textContent = '';
        return;
    }

    const label = issueLabel({ displayLabel, sourceName, eventId });
    selectedSummary.textContent = `已選擇：${label}`;
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
        const label = issueLabel(option);
        el.textContent = `${label} — ${option.hostCount} 台主機` +
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
        const raw = document.getElementById('issue-owner-event-id').value.trim();
        eventId = raw === '' ? NaN : Number(raw);
    } else {
        const [source, id] = (picker.value || '').split('|');
        sourceName = source;
        eventId = id !== undefined && id !== '' ? Number(id) : NaN;
    }

    if (!sourceName || !Number.isInteger(eventId) || eventId < 0) {
        toast('請選擇或輸入要指派的問題', 'warning');
        return;
    }

    // 機房結論下拉留白＝這次儲存不動結論欄（既有結論原封不動）；選了狀態才驗證原因必填
    // ——只有編輯模式看得到這個區塊（新增規則時還沒有鍵可以掛結論，見 renderConclusionSection）
    const settingConclusion = editingRule && conclusionStatus.value;
    if (settingConclusion && !conclusionNote.value.trim()) {
        toast('請填寫機房結論的原因', 'warning');
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
        if (settingConclusion) {
            await api.put(`/api/admin/issue-owners/${encodeURIComponent(sourceName)}/${eventId}/conclusion`, {
                status: conclusionStatus.value,
                note: conclusionNote.value.trim(),
                autoApply: conclusionAutoApply.checked
            });
        }
        toast('已儲存問題檔案', 'success');
        modal.hide();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

async function removeRule(rule) {
    const targetLabel = issueLabel(rule);
    const confirmed = await confirmAction({
        title: '刪除問題檔案',
        message: `確定要刪除「${targetLabel}」的問題檔案嗎？刪除後這個問題會落回主機負責人（若有設定）` +
            (rule.conclusionStatus ? '，機房結論也會一併移除。' : '。'),
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
