/**
 * 風險日詳情頁的 AI 對話（R7 精簡版，實驗性）。
 *
 * 不持久化：對話史只存在這個模組的記憶體，重新整理即清空，符合「可清除重來」的需求。
 * 每輪送出完整歷史給後端，伺服器端重新從 hostId/date/issueKey 取得 context 並強制 10 輪上限——
 * 這裡的 MAX_TURNS 只是提前擋掉明知會被拒的請求，真正的防線在後端（見 AiController.Chat）。
 */

import { api } from '../core/api.js';
import { toast, withBusy } from '../core/ui.js';
import { severityName } from '../core/format.js';
import { renderAiText } from '../core/markdown-lite.js';

const MAX_TURNS = 10;

let hostId = null;
let date = null;
let messages = [];   // { role: 'user'|'assistant', content }
let currentIssueKey = '';
let pending = false;   // 送出後、收到回覆前——顯示「思考中」泡泡

export function initChatPanel(hostIdParam, dateParam, topIssues, aiAvailable) {
    hostId = hostIdParam;
    date = dateParam;
    // 重新初始化（批次套用後的頁面重載）時歸零選擇：下拉選項已重建回到「請先選擇」，
    // 留著舊的 issueKey 會讓輸入框誤保持可用、送出時用到已不存在畫面上的選擇
    currentIssueKey = '';

    const card = document.getElementById('chat-card');
    if (!aiAvailable || topIssues.length === 0) {
        card.classList.add('d-none');
        return;
    }
    card.classList.remove('d-none');

    renderIssueOptions(topIssues);
    resetConversation();
    bindEvents();
}

/**
 * 嚴重度篩選鈕切換時呼叫（record-detail.js renderSeverityFilter）：下拉選單只列出
 * 目前篩選後仍可見的問題（docs/HISTORY.md #4）——不重新綁定事件，
 * 只重建選項；目前選中的問題被篩掉時才重置選擇與對話，否則保留使用者正在進行的對話。
 */
export function updateIssueOptions(topIssues) {
    renderIssueOptions(topIssues);

    const select = document.getElementById('chat-issue-select');
    const stillVisible = currentIssueKey && topIssues.some(i => i.issueKey === currentIssueKey);

    if (stillVisible) {
        select.value = currentIssueKey;
    } else {
        currentIssueKey = '';
        resetConversation();
    }
}

function renderIssueOptions(topIssues) {
    const select = document.getElementById('chat-issue-select');
    select.replaceChildren();

    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '請先選擇要詢問的異常項目…';
    select.appendChild(placeholder);

    for (const issue of topIssues) {
        const option = document.createElement('option');
        option.value = issue.issueKey;
        option.textContent = `${issue.source} / EventId ${issue.eventId}（${severityName(issue.severity)}）`;
        select.appendChild(option);
    }
}

function bindEvents() {
    const select = document.getElementById('chat-issue-select');
    const form = document.getElementById('chat-form');
    const clearBtn = document.getElementById('chat-clear');

    // replaceWith 拋棄舊節點連同其上的監聽器，避免每次 initChatPanel（例如批次套用後 load() 重呼）疊加重複綁定
    const freshSelect = select.cloneNode(true);
    select.replaceWith(freshSelect);
    const freshForm = form.cloneNode(true);
    form.replaceWith(freshForm);
    const freshClear = clearBtn.cloneNode(true);
    clearBtn.replaceWith(freshClear);

    freshSelect.addEventListener('change', () => {
        currentIssueKey = freshSelect.value;
        resetConversation();
    });

    freshForm.addEventListener('submit', onSubmit);
    freshClear.addEventListener('click', resetConversation);
}

function resetConversation() {
    messages = [];
    pending = false;
    renderMessages();
    updateTurnCount();

    const input = document.getElementById('chat-input');
    const send = document.getElementById('chat-send');
    const hasIssue = !!currentIssueKey;
    input.disabled = !hasIssue;
    send.disabled = !hasIssue;
    input.value = '';
}

function updateTurnCount() {
    const userTurns = messages.filter(m => m.role === 'user').length;
    document.getElementById('chat-turn-count').textContent = `${userTurns}/${MAX_TURNS}`;
}

async function onSubmit(event) {
    event.preventDefault();
    if (!currentIssueKey) return;

    const input = document.getElementById('chat-input');
    const send = document.getElementById('chat-send');
    const content = input.value.trim();
    if (!content) return;

    const userTurns = messages.filter(m => m.role === 'user').length;
    if (userTurns >= MAX_TURNS) {
        toast('對話已達 10 輪上限，請清除重來。', 'warning');
        return;
    }

    messages.push({ role: 'user', content });
    pending = true;
    renderMessages();
    updateTurnCount();
    input.value = '';

    const restore = withBusy(send, '送出中');
    input.disabled = true;
    try {
        const result = await api.post('/api/ai/chat', { hostId, date, issueKey: currentIssueKey, messages }, { silent: true });
        if (result?.text) {
            messages.push({ role: 'assistant', content: result.text });
        } else {
            messages.push({ role: 'assistant', content: 'AI 目前忙碌或無法回應，稍後再試。', failed: true });
        }
    } catch {
        messages.push({ role: 'assistant', content: 'AI 目前忙碌或無法回應，稍後再試。', failed: true });
    } finally {
        pending = false;
        restore();
        input.disabled = false;
        input.focus();
        renderMessages();
        updateTurnCount();
    }
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
    bubble.style.maxWidth = '85%';

    if (message.role === 'user') {
        // 使用者自己輸入的文字，原樣呈現即可，不必解析 Markdown
        bubble.className = 'p-2 rounded bg-primary text-white';
        bubble.style.whiteSpace = 'pre-wrap';
        bubble.textContent = message.content;
    } else if (message.failed) {
        bubble.className = 'lf-ai-block';
        const text = document.createElement('div');
        text.style.whiteSpace = 'pre-wrap';
        text.textContent = message.content;
        bubble.appendChild(text);
    } else {
        // AI 回覆：安全子集 Markdown 渲染（**粗體**／清單等），取代原本星號原樣顯示
        bubble.className = 'lf-ai-block';
        renderAiText(bubble, message.content, { badge: 'AI 回覆', badgeClassName: 'lf-badge lf-badge--secondary mb-1' });
    }

    row.appendChild(bubble);
    return row;
}

function renderThinkingBubble() {
    const row = document.createElement('div');
    row.className = 'd-flex mb-2 justify-content-start';

    const bubble = document.createElement('div');
    bubble.className = 'lf-ai-block';
    bubble.style.maxWidth = '85%';

    const typing = document.createElement('div');
    typing.className = 'lf-typing';
    for (let i = 0; i < 3; i++) {
        typing.appendChild(document.createElement('span'));
    }
    bubble.appendChild(typing);

    row.appendChild(bubble);
    return row;
}
