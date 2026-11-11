import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

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
const prune = extractFunction('function prunePrintableCollapsibles');
// 切換鈕是 core/elements.js 的 disclosure：載入 production 版本，不另寫假的
const { disclosure } = loadElements(dom);
const { collapsibleText, printableCollapsibles } = new Function('COLLAPSE_LIMIT', 'disclosure', `${printSetup}\n${prune}\n${hookPrint}\n${body}\nreturn { collapsibleText, printableCollapsibles };`)(300, disclosure);

function textContainer(text) {
    const el = dom.newElement('div');
    el.textContent = text;
    return el;
}
function toggleOf(wrap) { return wrap.find(n => n.tagName === 'BUTTON'); }
const labelOf = toggle => toggle.children.find(c => c.tagName === 'SPAN').textContent;
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
        assert.equal(labelOf(toggle), '全文');
        assert.equal(toggle.title, '全文共 301 字');
        assert(toggle.classList.contains('lf-disclosure'), '切換鈕是共用的 disclosure');
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.equal(toggle.getAttribute('aria-controls'), container.id);
        assert.equal(toggle.type, 'button');
        assert(container.textContent.endsWith('…'));
        assert.equal(container.textContent, 'a'.repeat(289) + '…');
        assert(container.textContent.length <= 291);
        click(toggle);
        assert.equal(container.textContent, original);
        assert.equal(labelOf(toggle), '全文', '標籤展開前後不變，狀態看 aria-expanded');
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
    // 切點落在代理對中間時往前退一格，不留孤立的 high surrogate
    surrogatePairNotSplit() {
        const container = textContainer('x'.repeat(299) + '😀' + 'y'.repeat(20)); // 第 300 個碼元是 😀 的前半
        collapsibleText(container);
        const shown = container.textContent;
        assert.equal(shown, 'x'.repeat(299) + '…');
        assert(!/[\uD800-\uDBFF]…$/.test(shown), '不得以孤立的 high surrogate 結尾');
    },
    // 最後一個分隔符在 limit 一半之前：不採用，直接在 limit 截斷
    earlySeparatorNotUsed() {
        const container = textContainer('Error:' + 'z'.repeat(400)); // 唯一的分隔符在第 6 個字
        collapsibleText(container);
        assert.equal(container.textContent, ('Error:' + 'z'.repeat(400)).slice(0, 300) + '…');
        // 分隔符剛好在一半（150）以後仍採用
        const late = textContainer('a'.repeat(150) + '、' + 'b'.repeat(300));
        collapsibleText(late);
        assert.equal(late.textContent, 'a'.repeat(150) + '…');
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
        assert.equal(toggle.getAttribute('aria-expanded'), 'true');
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
    },

    // 登記集合不得累積已卸下的節點：曾接上文件、後來卸下的在下一次登記時清掉
    detachedNodesPrunedOnNextRegistration() {
        printableCollapsibles.clear();
        const a = textContainer('a'.repeat(400)); a.isConnected = true;
        collapsibleText(a);
        const b = textContainer('b'.repeat(400)); b.isConnected = true;
        collapsibleText(b); // 此次登記前 a 已被標記為曾接上
        assert.equal(printableCollapsibles.size, 2);
        a.isConnected = false;
        const c = textContainer('c'.repeat(400)); c.isConnected = true;
        collapsibleText(c);
        const containers = [...printableCollapsibles].map(i => i.container);
        assert(!containers.includes(a));
        assert(containers.includes(b) && containers.includes(c));
    },
    // 從未接上文件的新元件不能被刪（呼叫端是先建立再接上）
    neverConnectedNodeIsKept() {
        printableCollapsibles.clear();
        const a = textContainer('a'.repeat(400)); a.isConnected = false;
        collapsibleText(a);
        const b = textContainer('b'.repeat(400)); b.isConnected = false;
        collapsibleText(b);
        assert.equal(printableCollapsibles.size, 2);
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
