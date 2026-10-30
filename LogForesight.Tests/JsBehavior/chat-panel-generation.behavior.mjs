/**
 * 問題對話面板（pages/chat-panel.js）的對話世代行為測試（第 59 輪 G4 契約 1）。
 *
 * 讀入 production 原始碼，只替換 api／畫面邊界後執行；由 ChatPanelGenerationBehaviorTests 以
 * `node chat-panel-generation.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(
    path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'chat-panel.js'), 'utf8')
    .replaceAll('\r\n', '\n');

/** 極簡元素：夠 chat-panel 用的屬性與事件 */
class El {
    constructor(id) {
        this.id = id;
        this.children = [];
        this.listeners = {};
        this.disabled = false;
        this.value = '';
        this.innerHTML = '';
        this.className = '';
        this.textContent = '';
        this.scrollTop = 0;
        this.scrollHeight = 0;
        const classes = new Set();
        this.classList = {
            add: c => classes.add(c), remove: c => classes.delete(c),
            toggle: (c, on) => (on ? classes.add(c) : classes.delete(c)), contains: c => classes.has(c)
        };
    }
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
    cloneNode() { const copy = new El(this.id); copy.children = []; return copy; }
    replaceWith(next) { elements[this.id] = next; }
    appendChild(child) { this.children.push(child); return child; }
    replaceChildren(...nodes) { this.children = [...nodes]; }
    focus() { this.focused = true; }
    async fire(type, event = { preventDefault() {} }) {
        for (const fn of this.listeners[type] ?? []) await fn(event);
    }
}

let elements;

function setup() {
    elements = {};
    for (const id of ['chat-card', 'chat-issue-select', 'chat-form', 'chat-clear', 'chat-expand',
        'chat-messages', 'chat-input', 'chat-send', 'chat-turn-count', 'chat-run-hint', 'chat-body']) {
        elements[id] = new El(id);
    }
    const document = {
        getElementById: id => elements[id] ?? null,
        createElement: () => new El('x'),
        createTextNode: text => ({ text })
    };

    // 可手動決定回覆時機的 api 替身
    const calls = [];
    const api = {
        get: async () => ({ isRunning: false }),
        post: (url, body) => new Promise((resolve, reject) => calls.push({ url, body, resolve, reject }))
    };
    const body = source
        .replace(/^import[\s\S]*?;\n/gm, '')
        .replace(/^export function/gm, 'function')
        + '\nreturn { initChatPanel, updateIssueOptions };';
    const factory = new Function('document', 'api', 'toast', 'withBusy', 'showDetailModal', 'severityName', 'renderAiText', body);
    const panel = factory(
        document, api, () => {},
        button => { button.disabled = true; return () => { button.disabled = false; }; },
        () => {}, value => String(value),
        (container, text) => { container.appendChild({ aiText: text }); });
    const issues = [
        { issueKey: 'A', sourceEventLabel: '問題甲', severity: 3 },
        { issueKey: 'B', sourceEventLabel: '問題乙', severity: 2 }
    ];
    panel.initChatPanel(1, '2026-10-30', issues, true);
    return { panel, calls, issues };
}

/** 選定問題並送出一則訊息，回覆懸置中 */
async function selectAndSend(ctx, key, text = '請說明') {
    elements['chat-issue-select'].value = key;
    await elements['chat-issue-select'].fire('change');
    elements['chat-input'].value = text;
    // 不 await：送出處理會一直等到回覆抵達
    elements['chat-form'].fire('submit');
    await new Promise(r => setTimeout(r, 0));
}

function bubbles() {
    return elements['chat-messages'].children;
}

const cases = {
    async pendingLocksControls() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        assert.equal(ctx.calls.length, 1, '應已送出一次請求');
        assert.equal(elements['chat-issue-select'].disabled, true, '等待回覆期間問題下拉須停用');
        assert.equal(elements['chat-clear'].disabled, true, '等待回覆期間清除鈕須停用');
        ctx.calls[0].resolve({ text: '回覆內容' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(elements['chat-issue-select'].disabled, false, '回覆後下拉須恢復');
        assert.equal(elements['chat-clear'].disabled, false, '回覆後清除鈕須恢復');
    },

    async sameConversationReplyKept() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        ctx.calls[0].resolve({ text: '回覆內容' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(bubbles().length, 2, '同一對話的回覆應顯示（使用者＋AI 兩則）');
        assert.equal(elements['chat-input'].disabled, false, '已選問題時輸入框應可用');
    },

    async staleReplyDropped() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        // 等待期間問題清單更新使目前問題消失，對話被重設（下拉停用只擋使用者操作，不擋這條程式路徑）
        ctx.panel.updateIssueOptions([ctx.issues[1]]);
        elements['chat-issue-select'].value = 'B';
        await elements['chat-issue-select'].fire('change');
        ctx.calls[0].resolve({ text: '舊問題的回覆' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(bubbles().length, 0, '舊對話的回覆不得出現在新問題的對話裡');
        assert.equal(elements['chat-turn-count'].textContent, '0/10', '新對話的回合數不得被舊回覆改動');
        assert.equal(elements['chat-input'].disabled, false, '新問題已選，輸入框應可用');
        assert.equal(elements['chat-issue-select'].disabled, false, '下拉不得停留在停用');
        // 新對話接著送出時，帶給後端的歷史只能有新對話自己的訊息（舊回覆不得混進 messages）
        elements['chat-input'].value = '新問題';
        elements['chat-form'].fire('submit');
        await new Promise(r => setTimeout(r, 0));
        assert.equal(ctx.calls[1].body.messages.length, 1, '舊回覆不得被推進新對話的歷史');
        assert.equal(ctx.calls[1].body.issueKey, 'B');
    },

    async staleFailureDropped() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        ctx.panel.updateIssueOptions([ctx.issues[1]]);
        elements['chat-issue-select'].value = 'B';
        await elements['chat-issue-select'].fire('change');
        ctx.calls[0].reject(new Error('boom'));
        await new Promise(r => setTimeout(r, 0));
        assert.equal(bubbles().length, 0, '舊對話的失敗訊息也不得出現在新對話裡');
        elements['chat-input'].value = '新問題';
        elements['chat-form'].fire('submit');
        await new Promise(r => setTimeout(r, 0));
        assert.equal(ctx.calls[1].body.messages.length, 1, '舊失敗訊息不得被推進新對話的歷史');
    },

    async 舊請求收尾不得重新開啟新請求等待中的控制項() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        // 等待舊回覆期間換到問題 B（對話重設），並在 B 送出新請求、等待回覆
        elements['chat-issue-select'].value = 'B';
        await elements['chat-issue-select'].fire('change');
        elements['chat-input'].value = '新問題';
        elements['chat-form'].fire('submit');
        await new Promise(r => setTimeout(r, 0));
        assert.equal(ctx.calls.length, 2, '新對話應已送出自己的請求');
        assert.equal(elements['chat-send'].disabled, true, '前提：新請求等待中送出鈕停用');
        assert.equal(elements['chat-input'].disabled, true, '前提：新請求等待中輸入框停用');

        ctx.calls[0].resolve({ text: '舊回覆' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(elements['chat-send'].disabled, true, '舊請求收尾不得重新開啟送出鈕');
        assert.equal(elements['chat-input'].disabled, true, '舊請求收尾不得重新開啟輸入框');
        assert.equal(elements['chat-issue-select'].disabled, true, '新請求等待中下拉仍須停用');

        ctx.calls[1].resolve({ text: '新回覆' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(elements['chat-send'].disabled, false, '新請求自己收尾後送出鈕才恢復');
        assert.equal(elements['chat-input'].disabled, false, '新請求自己收尾後輸入框才恢復');
    },

    async finallyKeepsInputDisabledWithoutIssue() {
        const ctx = setup();
        await selectAndSend(ctx, 'A');
        // 目前問題從清單消失 → currentIssueKey 清空、對話重設、輸入框停用
        ctx.panel.updateIssueOptions([ctx.issues[1]]);
        assert.equal(elements['chat-input'].disabled, true, '重設後沒有選定問題，輸入框應停用');
        ctx.calls[0].resolve({ text: '舊回覆' });
        await new Promise(r => setTimeout(r, 0));
        assert.equal(elements['chat-input'].disabled, true, '回覆抵達的收尾不得把沒有問題時的輸入框重新啟用');
        assert.equal(elements['chat-send'].disabled, true, '送出鈕同理維持停用');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
const target = cases[name];
if (!target) {
    console.error(`找不到案例：${name}`);
    process.exit(1);
}
try {
    await target();
    console.log(`PASS ${name}`);
    process.exit(0);
} catch (error) {
    console.error(`FAIL ${name}: ${error.message}`);
    process.exit(1);
}
