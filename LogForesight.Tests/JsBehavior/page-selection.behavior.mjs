/**
 * core/ui.js createPageSelection 的行為測試：抽出並執行 production 函式，只替換 DOM。
 * 守的是勾選的共用語意——全選本頁只選可選列、換頁保留已選、表頭三態（全選／部分／未選）、清除。
 */
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

function load() {
    const dom = installDom();
    globalThis.document.createTextNode = text => ({ nodeType: 3, textContent: text });
    const bar = dom.newElement('div');
    const count = dom.newElement('span');
    dom.byId.set('sel-bar', bar);
    dom.byId.set('sel-count', count);
    bar.className = 'd-none';
    // mini-dom 的 classList 沒有 toggle；補一個行為相同的替身（只用在這支測試）
    bar.classList.toggle = (name, force) => {
        if (force) bar.classList.add(name);
        else bar.className = bar.className.split(' ').filter(c => c && c !== name).join(' ');
    };

    const body = extractFunction('export function createPageSelection').replace(/^export /, '');
    const createPageSelection = new Function('document', `${body}\nreturn createPageSelection;`)(globalThis.document);

    let page = [];
    let selectAllCalls = 0;
    const selection = createPageSelection({
        keyOf: item => item.id,
        isSelectable: item => item.ok,
        getPageItems: () => page,
        labels: { row: '選此列', all: '全選本頁' },
        bar: { barId: 'sel-bar', countId: 'sel-count', unit: '台' },
        onSelectAll: () => { selectAllCalls++; }
    });
    const setPage = items => { page = items; };
    return { selection, bar, count, setPage, selectAllCalls: () => selectAllCalls };
}

/** 手動觸發事件；click 帶 stopPropagation 替身，勾選框不得讓點擊冒泡到整列 */
const fire = (node, type, checked) => {
    if (checked !== undefined) node.checked = checked;
    (node.listeners[type] || []).forEach(h => h({ stopPropagation() { } }));
};

const items = [
    { id: 1, ok: true }, { id: 2, ok: true }, { id: 3, ok: false }, { id: 4, ok: true }
];

const cases = {
    selectAllOnlyTakesSelectableRows() {
        const { selection, setPage, selectAllCalls, bar, count } = load();
        setPage(items);
        const header = selection.selectAllHeaderCheckbox();
        assert.equal(header.title, '全選本頁');
        fire(header, 'change', true);
        assert.deepEqual([...selection.selected.keys()].sort(), [1, 2, 4], '不可選的列不得被選');
        assert.equal(selectAllCalls(), 1, '全選後要通知頁面重繪');
        selection.renderBar();
        assert.equal(bar.classList.contains('d-none'), false);
        assert.equal(count.textContent, '已選 3 台');
        fire(header, 'change', false);
        assert.equal(selection.selected.size, 0);
    },
    rowCheckboxSkipsUnselectableAndTracksSelection() {
        const { selection, setPage, count, bar } = load();
        setPage(items);
        const blocked = selection.rowCheckboxCell(items[2]);
        assert.equal(blocked.nodeType, 3, '不可選的列只放空文字節點');
        const cell = selection.rowCheckboxCell(items[0]);
        assert.equal(cell.type, 'checkbox');
        assert.equal(cell.title, '選此列');
        fire(cell, 'click');
        fire(cell, 'change', true);
        assert.equal(selection.selected.get(1), items[0]);
        assert.equal(count.textContent, '已選 1 台');
        assert.equal(bar.classList.contains('d-none'), false);
        fire(cell, 'change', false);
        assert.equal(selection.selected.size, 0);
        assert.equal(bar.classList.contains('d-none'), true);
    },
    selectionKeptAcrossPages() {
        const { selection, setPage } = load();
        setPage(items);
        fire(selection.rowCheckboxCell(items[0]), 'change', true);
        const page2 = [{ id: 10, ok: true }, { id: 11, ok: true }];
        setPage(page2);
        assert.equal(selection.selected.has(1), true, '換頁不得清掉已選');
        fire(selection.rowCheckboxCell(page2[0]), 'change', true);
        assert.deepEqual([...selection.selected.keys()].sort((a, b) => a - b), [1, 10]);
        setPage(items);
        assert.equal(selection.rowCheckboxCell(items[0]).checked, true, '回到第一頁，已選的列仍顯示為已勾');
        assert.equal(selection.rowCheckboxCell(items[1]).checked, false);
    },
    headerTriState() {
        const { selection, setPage } = load();
        setPage(items);
        const header = selection.selectAllHeaderCheckbox();
        assert.equal(header.checked, false);
        assert.equal(header.indeterminate, false, '未選：兩者皆 false');

        fire(selection.rowCheckboxCell(items[0]), 'change', true);
        assert.equal(header.checked, false);
        assert.equal(header.indeterminate, true, '部分：indeterminate');

        fire(selection.rowCheckboxCell(items[1]), 'change', true);
        fire(selection.rowCheckboxCell(items[3]), 'change', true);
        assert.equal(header.checked, true, '本頁可選列全選：checked（不可選的列不計）');
        assert.equal(header.indeterminate, false);

        setPage([{ id: 3, ok: false }]);
        selection.sync();
        assert.equal(header.checked, false, '本頁沒有可選列不算全選');
        assert.equal(header.indeterminate, false);
    },
    clearEmptiesAndResyncs() {
        const { selection, setPage, bar, count } = load();
        setPage(items);
        const header = selection.selectAllHeaderCheckbox();
        fire(header, 'change', true);
        selection.renderBar();
        assert.equal(selection.selected.size, 3);
        selection.clear();
        assert.equal(selection.selected.size, 0);
        assert.equal(bar.classList.contains('d-none'), true);
        assert.equal(count.textContent, '已選 0 台');
        assert.equal(header.checked, false);
        assert.equal(header.indeterminate, false);
    }
};

const name = process.argv[2];
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
