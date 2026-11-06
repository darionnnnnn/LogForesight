/**
 * 第 66 輪 E：說明欄編輯器（core/note-editor.js）尾區結構與處理面板（pages/handling-panel.js）
 * 四區重排的 production 行為測試。直接 import production 模組，只替換 DOM 與 fetch；
 * 由 NoteEditorBehaviorTests 以 `node note-editor.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
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
const { initHandlingPanel } = panelModule;
const { renderChips } = await import(pathToFileURL(path.join(jsRoot, 'core', 'ui.js')).href);
const { statusesWhere } = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);
// 期望的 chip 清單直接由狀態目錄推導（處理面板不再 export 自己的清單）
const toChip = s => ({ value: s.value, text: s.label, short: s.shortLabel ?? s.label });
const DAY_CHIPS = statusesWhere(s => s.value !== 'observing').map(toChip);
const BATCH_CHIPS = statusesWhere(() => true).map(toChip);

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
    // 單選：再點第三顆後，只有第三顆是選取狀態
    before[2].dispatch('click');
    assert.deepEqual(before.map(b => has(b, 'active')), before.map((_, i) => i === 2));
    assert.deepEqual(before.map(b => b.getAttribute('aria-pressed')), before.map((_, i) => i === 2 ? 'true' : 'false'));
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
        const items = DAY_CHIPS.map(c => ({ value: c.value, label: c.short, title: c.text, ariaLabel: c.text }));
        renderChips(container, { items, attr: 'status', activeValues: [DAY_CHIPS[0].value], multi: false, onToggle: (value, on) => picked.push([value, on]) });
        const nodes = [...container.children];
        // 初始：第一顆選取，aria-pressed 與 .active 一致；有 ariaLabel 的才設 aria-label
        assert(has(nodes[0], 'active'));
        assert.deepEqual(nodes.map(n => n.getAttribute('aria-pressed')), nodes.map((_, i) => i === 0 ? 'true' : 'false'));
        assert.deepEqual(nodes.map(n => n.getAttribute('aria-label')), DAY_CHIPS.map(c => c.text));
        nodes[1].dispatch('click');
        nodes[2].dispatch('click');
        assert.deepEqual(picked, [[DAY_CHIPS[1].value, true], [DAY_CHIPS[2].value, true]]);
        // 點第二顆之後第一顆節點仍在容器內（沒被重建），且只剩被點的那顆選取
        container.children.forEach((child, i) => assert.equal(child, nodes[i], `第 ${i} 顆被重建了`));
        assert(!has(nodes[0], 'active'));
        assert.equal(nodes[0].getAttribute('aria-pressed'), 'false');
        assert.equal(nodes[1].getAttribute('aria-pressed'), 'false');
        assert(has(nodes[2], 'active'));
        assert.equal(nodes[2].getAttribute('aria-pressed'), 'true');

        // 沒給 ariaLabel 的項目不設 aria-label；複選模式點兩下會切回未選取並同步 aria-pressed
        const multiBox = document.createElement('div');
        renderChips(multiBox, { items: [{ value: 'a', label: 'A' }], attr: 'k', activeValues: [], onToggle: () => { } });
        const multi = multiBox.children[0];
        assert.equal(multi.getAttribute('aria-label'), null);
        assert.equal(multi.getAttribute('aria-pressed'), 'false');
        multi.dispatch('click');
        assert.equal(multi.getAttribute('aria-pressed'), 'true');
        multi.dispatch('click');
        assert.equal(multi.getAttribute('aria-pressed'), 'false');
    },
    async 處理面板以renderChips單選帶短標籤與完整名稱() {
        const panel = await buildPanel(new Set());
        const group = chipButtons(panel);
        const labelled = group.children.filter(b => b.getAttribute('aria-label') !== b.textContent);
        assert(labelled.length > 0, '至少一顆 chip 的短標籤應與完整名稱不同');
        // 資料屬性由共用 renderChips 以 attr=status 寫入
        assert.deepEqual(group.children.map(b => b.dataset.status), DAY_CHIPS.map(c => c.value));
        const src = fs.readFileSync(path.join(jsRoot, 'pages', 'handling-panel.js'), 'utf8').replaceAll('\r\n', '\n');
        assert(/renderChips\(chipGroup, \{[\s\S]*?multi: false/.test(src), '處理面板應呼叫 renderChips 並帶 multi: false');
        assert(!src.includes('buildStatusChips'), '不應再有自己的 buildStatusChips');
        assert(!/export const (DAY|BATCH)_CHIPS/.test(src), '不應為了測試 export chip 清單');
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
