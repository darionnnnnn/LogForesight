/**
 * 第 66 輪 E：說明欄編輯器（core/note-editor.js）尾區結構與處理面板（pages/handling-panel.js）
 * 四區重排的 production 行為測試。直接 import production 模組，只替換 DOM 與 fetch；
 * 由 NoteEditorBehaviorTests 以 `node note-editor.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');

const dom = installDom();
globalThis.window = { LF_BASE: '', matchMedia: () => ({ matches: false }) };
globalThis.bootstrap.Popover = class { };

// 替身節點沒有表單與焦點方法：建立時統一補上最小實作
const realCreate = document.createElement;
document.createElement = tag => {
    const el = realCreate(tag);
    el.focus = () => { };
    el.setCustomValidity = () => { };
    el.checkValidity = () => true;
    el.setSelectionRange = () => { };
    el.dispatchEvent = () => { };
    return el;
};

// 假 fetch：依網址尾端回應固定資料
let aiAvailable = true;
const HANDLING = {
    status: 'open', statusText: '未處理', derivedStatus: 'open', derivedStatusText: '未處理',
    totalIssues: 0, closedIssues: 0, canHandle: true, canAssign: false, ownerNames: ['王小明'],
    handlerName: null, handlerAccount: null, openCaseCount: 0, isOverdue: false,
    updatedAt: null, dueDate: null, note: ''
};
function respond(data) {
    return { ok: true, status: 200, json: async () => ({ success: true, data }) };
}
globalThis.fetch = async url => {
    const u = String(url);
    if (u.endsWith('/api/ai/status')) return respond({ available: aiAvailable, external: false, batchBusy: false });
    if (u.endsWith('/api/auth/me')) return respond({ userId: 1, capabilities: [] });
    if (u.endsWith('/handling/logs')) return respond([]);
    if (u.endsWith('/handling')) return respond(HANDLING);
    throw new Error(`未預期的請求：${u}`);
};

const { attachNoteEditor } = await import(pathToFileURL(path.join(jsRoot, 'core', 'note-editor.js')).href);
const panelModule = await import(pathToFileURL(path.join(jsRoot, 'pages', 'handling-panel.js')).href);
const { initHandlingPanel, DAY_CHIPS, BATCH_CHIPS, buildStatusChips } = panelModule;

const wait = (ms = 30) => new Promise(resolve => setTimeout(resolve, ms));
const has = (node, name) => node.classList.contains(name);

function buildEditor(options) {
    const form = document.createElement('div');
    const textarea = document.createElement('textarea');
    textarea.value = '';
    form.appendChild(textarea);
    const result = attachNoteEditor(textarea, options);
    return { form, textarea, result };
}

async function buildPanel(selection) {
    const panel = document.createElement('div');
    panel.id = 'handling-panel';
    const log = document.createElement('div');
    log.id = 'handling-log';
    dom.byId.set('handling-panel', panel);
    dom.byId.set('handling-log', log);
    await initHandlingPanel(1, '2026-11-05', () => selection, async () => { }, {});
    return panel;
}

function chipButtons(panel) {
    const group = panel.find(n => has(n, 'lf-handling__chips'));
    assert(group, '找不到 lf-handling__chips 容器');
    return group;
}

async function chipCase(selection, expected) {
    const panel = await buildPanel(selection);
    const group = chipButtons(panel);
    assert.equal(group.children.length, expected.length);

    const before = [...group.children];
    assert.deepEqual(before.map(b => b.textContent), expected.map(c => c.short));
    assert.deepEqual(before.map(b => b.title), expected.map(c => c.text));
    assert.deepEqual(before.map(b => b.getAttribute('aria-label')), expected.map(c => c.text));

    // 日層級預設選推導出的狀態（第一顆「未處理」）；批次不預選
    if (selection.size === 0) {
        assert(has(before[0], 'active'));
        assert.equal(before[0].getAttribute('aria-pressed'), 'true');
    } else {
        assert(before.every(b => !has(b, 'active')));
    }

    before[1].dispatch('click');

    // 節點沒有被重建：同一個物件仍在容器內（鍵盤焦點不會掉）
    assert.equal(group.children.length, expected.length);
    group.children.forEach((child, index) => assert.equal(child, before[index], `第 ${index} 顆 chip 被重建了`));
    assert(!has(before[0], 'active'));
    assert.equal(before[0].getAttribute('aria-pressed'), 'false');
    assert(has(before[1], 'active'));
    assert.equal(before[1].getAttribute('aria-pressed'), 'true');
}

const cases = {
    有errorEl時順序為說明欄_尾區_錯誤() {
        const errorEl = document.createElement('div');
        errorEl.className = 'invalid-feedback';
        const { textarea } = buildEditor({ errorEl, phrases: true });
        const tail = textarea.nextElementSibling;
        assert(tail && has(tail, 'lf-note-editor__tail'), 'textarea 後面應是尾區');
        assert.equal(tail.nextElementSibling, errorEl);
        assert(has(textarea, 'lf-note-editor__field'));
    },
    async 尾區含字數計數與AI整理按鈕() {
        aiAvailable = true;
        const { textarea } = buildEditor({ ai: { context: () => ({ hostCount: 1 }) }, phrases: true });
        await wait();
        const tail = textarea.nextElementSibling;
        const counter = tail.find(n => n.getAttribute('aria-live') === 'polite' && n.classList.contains('form-text'));
        assert(counter, '尾區應含 aria-live=polite 的字數計數');
        assert.equal(counter.textContent, '0 / 1000');
        const ai = tail.find(n => n.tagName === 'BUTTON' && n.textContent === 'AI 整理');
        assert(ai, '尾區應含「AI 整理」按鈕');
        assert.equal(ai.type, 'button');
        const phrase = tail.find(n => n.tagName === 'BUTTON' && n.textContent === '常用語');
        assert(phrase, '尾區應含常用語');
        // 常用語在左側工具組，AI 在右側
        assert(!tail.find(n => has(n, 'lf-note-editor__tools')).find(n => n === ai), 'AI 整理不應在左側工具組');
    },
    不傳errorEl時說明欄後面仍是尾區() {
        const { textarea } = buildEditor({});
        const tail = textarea.nextElementSibling;
        assert(tail && has(tail, 'lf-note-editor__tail'));
        assert.equal(tail.nextElementSibling, null);
    },
    async 日層級七顆chip點選不重建節點() {
        assert.equal(DAY_CHIPS.length, 7);
        await chipCase(new Set(), DAY_CHIPS);
    },
    async 批次八顆chip點選不重建節點() {
        assert.equal(BATCH_CHIPS.length, 8);
        await chipCase(new Set(['a', 'b']), BATCH_CHIPS);
    },
    async 動作區只含送出鈕且說明欄結構正確() {
        const panel = await buildPanel(new Set());
        const submit = panel.find(n => n.tagName === 'BUTTON' && n.type === 'submit');
        assert(submit, '找不到送出鈕');
        assert(has(submit._parent, 'lf-form-actions'), '送出鈕的父元素應有 lf-form-actions');
        assert(!submit.previousElementSibling || !has(submit.previousElementSibling, 'lf-note-editor__tail'),
            '送出鈕前面不應直接是尾區');
        assert.equal(submit._parent.children.length, 1);

        const note = panel.find(n => n.id === 'handling-note');
        const tail = note.nextElementSibling;
        assert(has(tail, 'lf-note-editor__tail'));
        assert(has(tail.nextElementSibling, 'invalid-feedback'));
        assert(!panel.find(n => n.tagName === 'HR'), '面板不應再有 hr');
        assert(has(panel, 'lf-handling'));
    },
    async chips公開函式只建立一次節點() {
        const container = document.createElement('div');
        const picked = [];
        const control = buildStatusChips(container, DAY_CHIPS, null, value => picked.push(value));
        const first = container.children[0];
        container.children[2].dispatch('click');
        assert.deepEqual(picked, [DAY_CHIPS[2].value]);
        assert.equal(container.children[0], first);
        control.select(DAY_CHIPS[0].value);
        assert(has(first, 'active'));
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
process.exit(0);
