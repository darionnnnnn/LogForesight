/**
 * 風險日詳情頁的 AI 對話（實驗性）。
 *
 * 不持久化：對話史只存在這個模組的記憶體，重新整理即清空，符合「可清除重來」的需求。
 * 每輪送出完整歷史與目前焦點（focusKeys，可空）給後端，伺服器端重新從 hostId/date 取得 context、
 * 判斷針對哪些問題並強制 10 輪上限——這裡的 MAX_TURNS 只是提前擋掉明知會被拒的請求，
 * 真正的防線在後端（見 AiController.Chat）。焦點由伺服器驗證，前端只負責顯示與回送。
 */

import { api } from '../core/api.js';
import { toast, withBusy, showDetailModal, loadRunHint } from '../core/ui.js';
import { severityName } from '../core/format.js';
import { renderAiText } from '../core/markdown-lite.js';

const MAX_TURNS = 10;
const MAX_FOCUS = 4;   // 與後端 ChatFocusResolver 的焦點上限一致

let hostId = null;
let date = null;
let messages = [];   // { role: 'user'|'assistant', content, hint? }
let focus = [];      // 目前焦點：{ issueKey, label }
let availableIssues = [];   // 「指定問題」可勾選的清單（目前可見的重點問題）
let controlsLocked = false;  // 等待回覆期間鎖住焦點列與清除鈕
let restoreSend = null;   // 進行中請求的送出鈕還原函式；重設對話時由 resetConversation 代為還原
let generation = 0;    // 對話世代：每次 resetConversation 加一，送出時記下，回覆抵達若世代不同就丟棄
let pending = false;   // 送出後、收到回覆前——顯示「思考中」泡泡

export function initChatPanel(hostIdParam, dateParam, topIssues, aiAvailable) {
    hostId = hostIdParam;
    date = dateParam;
    availableIssues = topIssues;

    const card = document.getElementById('chat-card');
    if (!aiAvailable || topIssues.length === 0) {
        card.classList.add('d-none');
        return;
    }
    card.classList.remove('d-none');

    resetConversation();
    bindEvents();
    loadRunHint(document.getElementById('chat-run-hint'));
}

/**
 * 嚴重度篩選鈕切換時呼叫（record-detail.js renderSeverityFilter）：只更新「指定問題」可勾選的清單。
 * 不重設對話、不動目前焦點——焦點由伺服器驗證，不在當日問題中的鍵會被靜默忽略。
 */
export function updateIssueOptions(topIssues) {
    availableIssues = topIssues;
}

function bindEvents() {
    const form = document.getElementById('chat-form');
    const clearBtn = document.getElementById('chat-clear');
    const expandBtn = document.getElementById('chat-expand');

    // replaceWith 拋棄舊節點連同其上的監聽器，避免每次 initChatPanel（例如批次套用後 load() 重呼）疊加重複綁定。
    // #chat-body（expandChat 搬移的對象）本身不在此列——它是穩定的容器節點，
    // 內容物各自被替換，容器身分從不改變，expandChat 每次都能取到正確的即時容器。
    const freshForm = form.cloneNode(true);
    form.replaceWith(freshForm);
    const freshClear = clearBtn.cloneNode(true);
    clearBtn.replaceWith(freshClear);
    const freshExpand = expandBtn.cloneNode(true);
    expandBtn.replaceWith(freshExpand);

    freshForm.addEventListener('submit', onSubmit);
    freshClear.addEventListener('click', resetConversation);
    freshExpand.addEventListener('click', expandChat);
}

/**
 * 放大檢視（docs/archive/FEEDBACK-3-PLAN.md #6）：把 #chat-body（焦點列／訊息／輸入表單整組）
 * 搬移進全螢幕 modal——是搬移不是複製，節點上的事件監聽器與對話狀態原樣保留，
 * chat-panel.js 其餘邏輯零改動。關閉時搬回原位置：insertBefore 用搬移前記下的
 * 下一個兄弟節點還原確切順序（目前 chat-body 是 lf-card__body 裡唯一子節點，
 * originalNextSibling 恆為 null，insertBefore(x, null) 等同 appendChild，寫法對未來
 * 若在旁邊加其他節點時仍然正確）。
 */
function expandChat() {
    const chatBody = document.getElementById('chat-body');
    const originalParent = chatBody.parentElement;
    const originalNextSibling = chatBody.nextSibling;

    showDetailModal({
        title: '詢問 AI（實驗性）',
        body: chatBody,
        fullscreen: true,
        onClose: () => {
            originalParent.insertBefore(chatBody, originalNextSibling);
            scrollMessagesToBottom();
        }
    });
    scrollMessagesToBottom();
}

function scrollMessagesToBottom() {
    const container = document.getElementById('chat-messages');
    container.scrollTop = container.scrollHeight;
}

function resetConversation() {
    generation++;
    // 進行中的舊請求不會再收尾（世代已變），送出鈕的「送出中」外觀由這裡還原
    if (restoreSend) {
        restoreSend();
        restoreSend = null;
    }
    messages = [];
    focus = [];
    pending = false;
    renderMessages();
    updateTurnCount();

    const input = document.getElementById('chat-input');
    const send = document.getElementById('chat-send');
    input.disabled = false;
    send.disabled = false;
    input.value = '';
    setConversationControlsDisabled(false);
}

/** 回覆等待期間鎖住焦點列（移除與指定）與清除鈕：回覆回來時以伺服器回的焦點為準，等待中不接受變更 */
function setConversationControlsDisabled(disabled) {
    controlsLocked = disabled;
    const clearBtn = document.getElementById('chat-clear');
    if (clearBtn) clearBtn.disabled = disabled;
    renderFocus();
}

/** 焦點列：每個焦點一顆 chip（附「×」）；沒有焦點且尚無回覆時顯示說明；列尾是「指定問題」鈕 */
function renderFocus() {
    const bar = document.getElementById('chat-focus');
    if (!bar) return;
    bar.replaceChildren();

    for (const item of focus) {
        const chip = document.createElement('span');
        chip.className = 'lf-badge lf-badge--primary lf-focus-chip';
        const label = document.createElement('span');
        label.textContent = item.label;
        chip.appendChild(label);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'lf-focus-chip__remove';
        remove.textContent = '×';
        remove.setAttribute('aria-label', `移除焦點：${item.label}`);
        remove.disabled = controlsLocked;
        remove.addEventListener('click', () => removeFocus(item.issueKey));
        chip.appendChild(remove);
        bar.appendChild(chip);
    }

    if (focus.length === 0) {
        const empty = document.createElement('span');
        empty.className = 'small text-muted';
        empty.textContent = '未指定，系統會依提問判斷';
        bar.appendChild(empty);
    }

    const pick = document.createElement('button');
    pick.type = 'button';
    pick.className = 'btn btn-sm btn-outline-secondary lf-focus-pick';
    pick.textContent = '指定問題';
    pick.disabled = controlsLocked;
    pick.addEventListener('click', openFocusPicker);
    bar.appendChild(pick);
}

function toFocusItem(issueKey, label) {
    return { issueKey, label };
}

/** 移除單一焦點：不重設對話、不改輪數 */
function removeFocus(issueKey) {
    if (controlsLocked) return;
    focus = focus.filter(f => f.issueKey !== issueKey);
    renderFocus();
}

/**
 * 「指定問題」：以 checkbox 清單列出目前可指定的問題，已是焦點者預設勾選，最多 4 個。
 * 關閉 modal 時才套用（modal 只有「關閉」鈕，沒有獨立的確定鈕）。
 * 不在清單中的既有焦點（例如被嚴重度篩選藏起來的）原樣保留並計入上限。
 */
function openFocusPicker() {
    if (controlsLocked) return;
    const current = new Set(focus.map(f => f.issueKey));
    const listed = new Set(availableIssues.map(i => i.issueKey));
    const kept = focus.filter(f => !listed.has(f.issueKey));
    const limit = MAX_FOCUS - kept.length;

    const body = document.createElement('div');
    const rows = [];
    availableIssues.forEach((issue, index) => {
        const row = document.createElement('div');
        row.className = 'form-check';
        const box = document.createElement('input');
        box.type = 'checkbox';
        box.className = 'form-check-input';
        box.id = `chat-pick-${index}`;
        box.checked = current.has(issue.issueKey);
        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.setAttribute('for', box.id);
        label.textContent = `${issue.sourceEventLabel}（${severityName(issue.severity)}）`;
        row.appendChild(box);
        row.appendChild(label);
        body.appendChild(row);
        rows.push({ box, issue });
    });
    if (rows.length === 0) {
        const none = document.createElement('div');
        none.className = 'text-muted';
        none.textContent = '目前沒有可指定的問題。';
        body.appendChild(none);
    }

    const refresh = () => {
        const checkedCount = rows.filter(r => r.box.checked).length;
        for (const r of rows) r.box.disabled = !r.box.checked && checkedCount >= limit;
    };
    for (const r of rows) r.box.addEventListener('change', refresh);
    refresh();

    showDetailModal({
        title: '指定問題',
        body,
        onClose: () => {
            if (controlsLocked) return;
            const chosen = rows.filter(r => r.box.checked)
                .map(r => toFocusItem(r.issue.issueKey, r.issue.sourceEventLabel));
            focus = [...kept, ...chosen];
            renderFocus();
        }
    });
}

function updateTurnCount() {
    const userTurns = messages.filter(m => m.role === 'user').length;
    document.getElementById('chat-turn-count').textContent = `${userTurns}/${MAX_TURNS}`;
}

async function onSubmit(event) {
    event.preventDefault();
    const input = document.getElementById('chat-input');
    const send = document.getElementById('chat-send');
    const content = input.value.trim();
    if (!content) return;

    const userTurns = messages.filter(m => m.role === 'user').length;
    if (userTurns >= MAX_TURNS) {
        toast('對話已達 10 輪上限，請清除重來。', 'warning');
        return;
    }

    const requestGeneration = generation;
    messages.push({ role: 'user', content });
    pending = true;
    setConversationControlsDisabled(true);
    renderMessages();
    updateTurnCount();
    input.value = '';

    const restore = withBusy(send, '送出中');
    restoreSend = restore;
    input.disabled = true;
    try {
        const result = await api.post('/api/ai/chat', {
            hostId, date,
            focusKeys: focus.map(f => f.issueKey),
            messages: messages.map(m => ({ role: m.role, content: m.content }))
        }, { silent: true });
        // 等待期間對話被重設（清除重來）：回覆屬於舊對話，丟棄
        if (requestGeneration !== generation) return;
        if (result?.text) {
            // 詢問 AI 現場取數（docs/archive/FEEDBACK-4-PLAN.md §5）：只有真的取到資料才顯示提示，
            // 不誤導（開關關閉／非 NetIQ 主機／失敗時 fetchedLogCount 為 null，不顯示任何取數跡象）
            messages.push({
                role: 'assistant', content: result.text, fetchedLogCount: result.fetchedLogCount,
                hint: buildFocusHint(result)
            });
            // 以伺服器實際採用的焦點取代目前焦點；沒帶 focus 欄位就維持原狀。
            // 預設焦點（沒指定也沒命中時系統取的最嚴重幾項）只用於這一輪：存起來下一輪會被當成使用者指定，
            // 之後問別的問題時一直夾帶這幾項
            if (Array.isArray(result.focus) && result.focusSource !== 'default') {
                focus = result.focus.map(f => toFocusItem(f.issueKey, f.label));
            }
        } else {
            messages.push({ role: 'assistant', content: 'AI 目前忙碌或無法回應，稍後再試。', failed: true });
        }
    } catch (err) {
        if (requestGeneration !== generation) return;
        const content = err?.status === 429 ? err.message : 'AI 目前忙碌或無法回應，稍後再試。';
        messages.push({ role: 'assistant', content, failed: true });
    } finally {
        // 只在對話仍是自己發起的那一個時收尾；已被重設的對話，重設時就把畫面狀態處理好了——
        // 此時新對話可能正在等待自己的回覆，舊請求不得把送出鈕或輸入框重新開啟
        if (requestGeneration === generation) {
            restore();
            restoreSend = null;
            pending = false;
            setConversationControlsDisabled(false);
            renderMessages();
            updateTurnCount();
            input.disabled = false;
            send.disabled = false;
            input.focus();
        }
    }
}

/** 該則回覆的焦點提示：keyword 列出判定的問題、default 說明退回最嚴重的項目、manual 不顯示 */
function buildFocusHint(result) {
    if (result.focusSource === 'keyword' && Array.isArray(result.focus) && result.focus.length > 0) {
        return `已判定針對：${result.focus.map(f => f.label).join('、')}`;
    }
    if (result.focusSource === 'default') return '未偵測到指定問題，以最嚴重的幾項為主';
    return '';
}

function renderMessages() {
    const container = document.getElementById('chat-messages');
    container.replaceChildren();

    for (const message of messages) {
        container.appendChild(renderBubble(message));
    }

    if (pending) {
        container.appendChild(renderThinkingBubble());
    }

    container.scrollTop = container.scrollHeight;
}

function renderBubble(message) {
    const row = document.createElement('div');
    row.className = `d-flex mb-2 ${message.role === 'user' ? 'justify-content-end' : 'justify-content-start'}`;

    const bubble = document.createElement('div');

    if (message.role === 'user') {
        // 使用者自己輸入的文字，原樣呈現即可，不必解析 Markdown
        bubble.className = 'p-2 rounded bg-primary text-white lf-bubble lf-pre-wrap';
        bubble.textContent = message.content;
    } else if (message.failed) {
        bubble.className = 'lf-ai-block lf-bubble';
        const text = document.createElement('div');
        text.className = 'lf-pre-wrap';
        text.textContent = message.content;
        bubble.appendChild(text);
    } else {
        // AI 回覆：安全子集 Markdown 渲染（**粗體**／清單等），取代原本星號原樣顯示
        bubble.className = 'lf-ai-block lf-bubble';
        if (message.hint) {
            const hint = document.createElement('div');
            hint.className = 'small text-muted mb-1';
            hint.textContent = message.hint;
            bubble.appendChild(hint);
        }
        if (message.fetchedLogCount > 0) {
            const note = document.createElement('div');
            note.className = 'small text-muted mb-1';
            note.textContent = `已取回現場事件 ${message.fetchedLogCount} 則納入分析`;
            bubble.appendChild(note);
        }
        renderAiText(bubble, message.content, { badge: 'AI 回覆', badgeClassName: 'lf-badge lf-badge--secondary mb-1' });
    }

    row.appendChild(bubble);
    return row;
}

function renderThinkingBubble() {
    const row = document.createElement('div');
    row.className = 'd-flex mb-2 justify-content-start';

    const bubble = document.createElement('div');
    bubble.className = 'lf-ai-block lf-bubble';

    const typing = document.createElement('div');
    typing.className = 'lf-typing';
    for (let i = 0; i < 3; i++) {
        typing.appendChild(document.createElement('span'));
    }
    bubble.appendChild(typing);

    row.appendChild(bubble);
    return row;
}
