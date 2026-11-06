import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'ui.js'), 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

const dom = installDom();
const windowListeners = {};
globalThis.window = { addEventListener: (type, handler) => { (windowListeners[type] ||= []).push(handler); } };
// 列印監聽是模組層的單一實例（printableCollapsibles／hookCollapsiblePrint），要一起帶進來
const printSetup = source.slice(source.indexOf('const printableCollapsibles'), source.indexOf('function hookCollapsiblePrint'));
const hookPrint = extractFunction('function hookCollapsiblePrint');
const body = extractFunction('export function collapsibleText').replace(/^export /, '');
const collapsibleText = new Function('COLLAPSE_LIMIT', `${printSetup}\n${hookPrint}\n${body}\nreturn collapsibleText;`)(300);

function textContainer(text) {
    const el = dom.newElement('div');
    el.textContent = text;
    return el;
}
function toggleOf(wrap) { return wrap.find(n => n.tagName === 'BUTTON'); }
function click(button) {
    let stopped = 0;
    (button.listeners.click || []).forEach(h => h({ stopPropagation: () => { stopped++; } }));
    return stopped;
}

const cases = {
    plainTextCollapsesAtSeparator() {
        const original = 'a'.repeat(289) + '、' + 'b'.repeat(10) + 'c'; // 301 字，第 290 字是「、」
        assert.equal(original.length, 301);
        const container = textContainer(original);
        const wrap = collapsibleText(container);
        assert.notEqual(wrap, container);
        const toggle = toggleOf(wrap);
        assert.equal(toggle.textContent, '顯示全文（共 301 字）');
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.equal(toggle.getAttribute('aria-controls'), container.id);
        assert.equal(toggle.type, 'button');
        assert(container.textContent.endsWith('…'));
        assert.equal(container.textContent, 'a'.repeat(289) + '…');
        assert(container.textContent.length <= 291);
        click(toggle);
        assert.equal(container.textContent, original);
        assert.equal(toggle.textContent, '收合');
        assert.equal(toggle.getAttribute('aria-expanded'), 'true');
        click(toggle);
        assert.equal(container.textContent, 'a'.repeat(289) + '…');
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
    },
    exactLimitReturnsContainer() {
        const container = textContainer('x'.repeat(300));
        assert.equal(collapsibleText(container), container);
    },
    noSeparatorCutsAtLimit() {
        const container = textContainer('x'.repeat(301));
        collapsibleText(container);
        assert.equal(container.textContent, 'x'.repeat(300) + '…');
    },
    blockModeTogglesClass() {
        const container = dom.newElement('div');
        const p1 = dom.newElement('p'); p1.textContent = 'a'.repeat(200);
        const p2 = dom.newElement('p'); p2.textContent = 'b'.repeat(200);
        container.append(p1, p2);
        const wrap = collapsibleText(container);
        assert(container.classList.contains('lf-collapsible--clamped'));
        const toggle = toggleOf(wrap);
        assert(toggle);
        assert.equal(container.children.length, 2);
        click(toggle);
        assert(!container.classList.contains('lf-collapsible--clamped'));
        assert.equal(toggle.textContent, '收合');
        click(toggle);
        assert(container.classList.contains('lf-collapsible--clamped'));

        const small = dom.newElement('div');
        const q = dom.newElement('p'); q.textContent = 'a'.repeat(200);
        small.append(q);
        assert.equal(collapsibleText(small), small);
        assert(!small.classList.contains('lf-collapsible--clamped'));
        assert.equal(toggleOf(small), null);
    },
    clickStopsPropagation() {
        const wrap = collapsibleText(textContainer('x'.repeat(400)));
        assert.equal(click(toggleOf(wrap)), 1);
    },
    printRestoresFullText() {
        const container = textContainer('y'.repeat(400));
        collapsibleText(container);
        assert(container.textContent.endsWith('…'));
        windowListeners.beforeprint.forEach(h => h());
        assert.equal(container.textContent, 'y'.repeat(400));
        windowListeners.afterprint.forEach(h => h());
        assert(container.textContent.endsWith('…'));
    },

    // 清單每次重繪都會建立新元件：列印監聽只能掛一組，否則卸下的節點會讓監聽越積越多
    printListenerRegisteredOnce() {
        const a = textContainer('a'.repeat(400));
        const b = textContainer('b'.repeat(400));
        collapsibleText(a);
        collapsibleText(b);
        assert.equal(windowListeners.beforeprint.length, 1);
        assert.equal(windowListeners.afterprint.length, 1);
        windowListeners.beforeprint.forEach(h => h());
        assert.equal(a.textContent, 'a'.repeat(400));
        assert.equal(b.textContent, 'b'.repeat(400));
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
