/**
 * 問題對話面板（pages/chat-panel.js）的焦點列行為測試：免選問題直接發問、焦點 chip、指定問題清單、
 * 回覆提示與等待期間鎖定。
 *
 * 讀入 production 原始碼，只替換 api／畫面邊界後執行；由 ChatPanelFocusBehaviorTests 以
 * `node chat-panel-focus.behavior.mjs <案例名>` 逐案執行。
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
        this.attributes = {};
        this.disabled = false;
        this.checked = false;
        this.value = '';
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
    setAttribute(name, value) { this.attributes[name] = value; }
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
let modals;

function setup(issues) {
    elements = {};
    modals = [];
    for (const id of ['chat-card', 'chat-form', 'chat-clear', 'chat-expand', 'chat-focus',
        'chat-messages', 'chat-input', 'chat-send', 'chat-turn-count', 'chat-run-hint', 'chat-body', 'chat-focus-picker']) {
        elements[id] = new El(id);
    }
    elements['chat-focus-picker'].classList.add('d-none');
    const document = {
        getElementById: id => elements[id] ?? null,
        createElement: () => new El('x'),
        createTextNode: text => ({ text })
    };

    const calls = [];
    const api = {
        get: async () => ({ isRunning: false }),
        post: (url, body) => new Promise((resolve, reject) => calls.push({ url, body, resolve, reject }))
    };
    const body = source
        .replace(/^import[\s\S]*?;\n/gm, '')
        .replace(/^export function/gm, 'function')
        + '\nreturn { initChatPanel, updateIssueOptions };';
    const factory = new Function('document', 'api', 'toast', 'withBusy', 'showDetailModal', 'severityName', 'renderAiText', 'loadRunHint', 'button', body);
    const panel = factory(
        document, api, () => {},
        button => { button.disabled = true; return () => { button.disabled = false; }; },
        options => { modals.push(options); }, value => `嚴重度${value}`,
        (container, text) => { container.appendChild({ aiText: text }); },
        () => {},
        // ui.js button() 的替身：只保留這個測試看得到的部分（className、文字、disabled、點擊）
        (text, { className, disabled, onClick } = {}) => {
            const btn = new El('button');
            btn.className = ('btn ' + (className ?? '')).trim();
            btn.textContent = text;
            btn.disabled = !!disabled;
            if (onClick) btn.addEventListener('click', onClick);
            return btn;
        });
    const list = issues ?? [
        { issueKey: 'A', sourceEventLabel: '問題甲', severity: 3 },
        { issueKey: 'B', sourceEventLabel: '問題乙', severity: 2 }
    ];
    panel.initChatPanel(1, '2026-10-30', list, true);
    return { panel, calls, issues: list };
}

const tick = () => new Promise(r => setTimeout(r, 0));

async function send(text = '請說明') {
    elements['chat-input'].value = text;
    elements['chat-form'].fire('submit');
    await tick();
}

function textOf(node) {
    if (!node) return '';
    return (node.textContent ?? '') + (node.children ?? []).map(textOf).join('');
}

function bar() { return elements['chat-focus']; }
function chips() { return bar().children.filter(c => c.className.includes('lf-focus-chip')); }
function removeButton(chip) { return chip.children[1]; }
function pickButton() { return bar().children.find(c => c.className.includes('lf-focus-pick')); }
function picker() { return elements['chat-focus-picker']; }
function pickerHidden() { return picker().classList.contains('d-none'); }
function pickerBoxes() { return picker().children[0].children.map(row => row.children[0]); }
function pickerButton(text) { return picker().children[1].children.find(b => b.textContent === text); }
function bubbleTexts() { return elements['chat-messages'].children.map(textOf); }

const FOCUS2 = [{ issueKey: 'A', label: '問題甲' }, { issueKey: 'B', label: '問題乙' }];

const cases = {
    async initialState() {
        setup();
        assert.equal(elements['chat-input'].disabled, false, '初始化後輸入框應可用');
        assert.equal(elements['chat-send'].disabled, false, '初始化後送出鈕應可用');
        assert.equal(elements['chat-issue-select'], undefined);
        assert.ok(textOf(bar()).includes('未指定，系統會依提問判斷'), '應顯示未指定提示');
        assert.equal(chips().length, 0);
    },

    async keywordFocusThenFollowUp() {
        const ctx = setup();
        await send('4625 和 4735');
        assert.deepEqual(ctx.calls[0].body.focusKeys, [], '第一則的 focusKeys 應為空陣列');
        assert.equal('issueKey' in ctx.calls[0].body, false, '不再送舊欄位 issueKey');
        ctx.calls[0].resolve({ text: '回覆一', focus: FOCUS2, focusSource: 'keyword' });
        await tick();
        assert.equal(chips().length, 2, '回覆後應有兩個焦點 chip');
        assert.ok(bubbleTexts().some(t => t.includes('已判定針對：問題甲、問題乙')), 'AI 泡泡上方應有判定提示');
        assert.equal(textOf(bar()).includes('未指定'), false, '有焦點時不顯示未指定提示');
        await send('再問');
        assert.deepEqual(ctx.calls[1].body.focusKeys, ['A', 'B']);
        assert.equal(ctx.calls[1].body.messages.length, 3, '歷史應含前一輪兩則加新問題');
    },

    async removeChipKeepsConversation() {
        const ctx = setup();
        await send();
        ctx.calls[0].resolve({ text: '回覆', focus: FOCUS2, focusSource: 'keyword' });
        await tick();
        const before = elements['chat-messages'].children.length;
        const turns = elements['chat-turn-count'].textContent;
        assert.equal(removeButton(chips()[0]).attributes['aria-label'], '移除焦點：問題甲');
        await removeButton(chips()[0]).fire('click');
        assert.equal(chips().length, 1, '剩一個 chip');
        assert.equal(textOf(chips()[0]), '問題乙×');
        assert.equal(elements['chat-messages'].children.length, before, '訊息數量不變');
        assert.equal(elements['chat-turn-count'].textContent, turns, '輪數不變');
    },

    async pickerLimitsToFour() {
        const many = ['A', 'B', 'C', 'D', 'E'].map((k, i) => ({ issueKey: k, sourceEventLabel: `問題${k}`, severity: i }));
        setup(many);
        await pickButton().fire('click');
        assert.equal(pickerHidden(), false, '應展開指定問題面板');
        const boxes = pickerBoxes();
        assert.equal(boxes.length, 5);
        assert.ok(textOf(picker().children[0].children[0]).includes('問題A（嚴重度0）'), '清單項顯示來源事件與嚴重度');
        for (let i = 0; i < 4; i++) {
            boxes[i].checked = true;
            await boxes[i].fire('change');
        }
        assert.equal(boxes[4].disabled, true, '勾滿 4 個後其餘須 disabled');
        assert.equal(boxes[0].disabled, false, '已勾選者仍可取消');
        boxes[0].checked = false;
        await boxes[0].fire('change');
        assert.equal(boxes[4].disabled, false, '取消一個後其餘恢復可勾');
        boxes[4].checked = true;
        await boxes[4].fire('change');
        await pickerButton('套用').fire('click');
        assert.deepEqual(chips().map(c => c.children[0].textContent), ['問題B', '問題C', '問題D', '問題E']);
    },

    async pickerOpensInline() {
        setup();
        assert.equal(pickButton().attributes['aria-controls'], 'chat-focus-picker');
        assert.equal(pickButton().attributes['aria-expanded'], 'false');
        await pickButton().fire('click');
        assert.equal(pickerHidden(), false, '面板顯示');
        assert.equal(pickButton().attributes['aria-expanded'], 'true');
        assert.equal(modals.length, 0, '不得呼叫 showDetailModal');
        assert.equal(pickerBoxes()[0].focused, true, '焦點移到第一個 checkbox');
    },

    async pickerApply() {
        setup();
        await pickButton().fire('click');
        for (const box of pickerBoxes()) {
            box.checked = true;
            await box.fire('change');
        }
        await pickerButton('套用').fire('click');
        assert.equal(chips().length, 2, '焦點列兩個 chip');
        assert.equal(pickerHidden(), true, '面板隱藏');
        assert.equal(pickButton().attributes['aria-expanded'], 'false');
        assert.equal(pickButton().focused, true, '焦點回到指定問題鈕');
        assert.equal(modals.length, 0);
    },

    async pickerCancel() {
        setup();
        await pickButton().fire('click');
        const boxes = pickerBoxes();
        boxes[0].checked = true;
        await boxes[0].fire('change');
        await pickerButton('取消').fire('click');
        assert.equal(chips().length, 0, '焦點不變');
        assert.equal(pickerHidden(), true);
        assert.equal(pickButton().attributes['aria-expanded'], 'false');
        assert.equal(pickButton().focused, true);
    },

    async pickerClosesWhenSending() {
        const ctx = setup();
        await pickButton().fire('click');
        await send();
        assert.equal(pickerHidden(), true, '送出後面板關閉');
        assert.equal(pickButton().disabled, true, '等待中指定問題鈕 disabled');
        ctx.calls[0].resolve({ text: '回覆', focus: FOCUS2, focusSource: 'manual' });
        await tick();
        assert.equal(pickButton().disabled, false, '回覆後恢復可用');
    },

    async pickerClosesOnClear() {
        setup();
        await pickButton().fire('click');
        const boxes = pickerBoxes();
        boxes[0].checked = true;
        await boxes[0].fire('change');
        await pickerButton('套用').fire('click');
        assert.equal(chips().length, 1);
        await pickButton().fire('click');
        assert.equal(pickerHidden(), false);
        await elements['chat-clear'].fire('click');
        assert.equal(pickerHidden(), true, '清除重來關閉面板');
        assert.equal(chips().length, 0, '焦點清空');
    },

    async pickerEscCancels() {
        setup();
        await pickButton().fire('click');
        const boxes = pickerBoxes();
        boxes[0].checked = true;
        await boxes[0].fire('change');
        let stopped = false;
        await picker().fire('keydown', { key: 'Escape', preventDefault() {}, stopPropagation() { stopped = true; } });
        assert.equal(pickerHidden(), true, 'Esc 關閉面板');
        assert.equal(chips().length, 0, 'Esc 不改焦點');
        assert.equal(stopped, true, 'Esc 不外傳給外層 modal');
    },

    async updateIssueOptionsKeepsState() {
        const ctx = setup();
        await send();
        ctx.calls[0].resolve({ text: '回覆', focus: FOCUS2, focusSource: 'keyword' });
        await tick();
        const before = elements['chat-messages'].children.length;
        ctx.panel.updateIssueOptions([ctx.issues[1]]);
        assert.equal(chips().length, 2, '焦點不變');
        assert.equal(elements['chat-messages'].children.length, before, '訊息不變');
        assert.equal(elements['chat-turn-count'].textContent, '1/10');
        await pickButton().fire('click');
        assert.equal(pickerBoxes().length, 1, '指定清單改為新清單');
    },

    async pendingLocksFocusControls() {
        const ctx = setup();
        await send();
        ctx.calls[0].resolve({ text: '回覆', focus: FOCUS2, focusSource: 'manual' });
        await tick();
        await send('再問');
        assert.equal(removeButton(chips()[0]).disabled, true, '等待中 × 須停用');
        assert.equal(pickButton().disabled, true, '等待中指定問題鈕須停用');
        assert.equal(elements['chat-clear'].disabled, true, '等待中清除鈕須停用');
        ctx.calls[1].resolve({ text: '回覆二', focus: FOCUS2, focusSource: 'manual' });
        await tick();
        assert.equal(removeButton(chips()[0]).disabled, false, '回覆後 × 恢復');
        assert.equal(pickButton().disabled, false, '回覆後指定問題鈕恢復');
        assert.equal(elements['chat-clear'].disabled, false, '回覆後清除鈕恢復');
    },

    async clearWhilePendingDropsStaleReply() {
        const ctx = setup();
        await send();
        ctx.calls[0].resolve({ text: '回覆', focus: FOCUS2, focusSource: 'manual' });
        await tick();
        await send('再問');
        await elements['chat-clear'].fire('click');
        assert.equal(elements['chat-messages'].children.length, 0, '清除後訊息清空');
        assert.equal(chips().length, 0, '清除後焦點清空');
        ctx.calls[1].resolve({ text: '舊回覆', focus: FOCUS2, focusSource: 'keyword' });
        await tick();
        assert.equal(elements['chat-messages'].children.length, 0, '舊回覆被丟棄');
        assert.equal(chips().length, 0, '舊回覆不得改焦點');
    },

    async defaultAndManualHints() {
        const ctx = setup();
        await send();
        ctx.calls[0].resolve({ text: '回覆一', focus: FOCUS2, focusSource: 'default' });
        await tick();
        assert.ok(bubbleTexts().some(t => t.includes('未偵測到指定問題')), 'default 應顯示提示');
        await send('再問');
        assert.deepEqual(ctx.calls[1].body.focusKeys, [], '預設焦點只用於該輪，下一輪不得當成指定送出');
        ctx.calls[1].resolve({ text: '回覆二', focus: FOCUS2, focusSource: 'manual' });
        await tick();
        const last = bubbleTexts().at(-1);
        assert.equal(last.includes('未偵測到指定問題'), false, 'manual 不顯示提示');
        assert.equal(last.includes('已判定針對'), false, 'manual 不顯示提示');
        // 重畫後第一則回覆的提示仍在
        assert.ok(bubbleTexts().some(t => t.includes('未偵測到指定問題')), '重畫後提示維持');
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
