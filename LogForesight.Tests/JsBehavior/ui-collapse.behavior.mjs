import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

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

function element() {
    const classes = new Set();
    const listeners = {};
    return {
        textContent: '',
        attributes: {},
        classList: {
            toggle: (name, on) => { if (on) classes.add(name); else classes.delete(name); },
            add: name => classes.add(name),
            contains: name => classes.has(name)
        },
        setAttribute(name, value) { this.attributes[name] = value; },
        addEventListener: (type, handler) => { listeners[type] = handler; },
        fire: (type, event) => listeners[type]?.(event)
    };
}

// production 已改走 core/storage.js 的 safeStorage：這裡載入真正的 safeStorage，
// 並把測試替身掛到 globalThis.localStorage，讓「儲存區擲例外」的情境走真實的 try/catch 包裝
const { safeStorage } = await import(pathToFileURL(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'storage.js')).href);

function load(localStorage) {
    globalThis.localStorage = localStorage;
    const body = extractFunction('export function bindRememberedCollapse').replace(/^export /, '');
    return new Function('safeStorage', `${body}\nreturn bindRememberedCollapse;`)(safeStorage);
}

function memoryStorage(initial = {}) {
    const data = { ...initial };
    return { data, getItem: key => data[key] ?? null, setItem: (key, value) => { data[key] = value; } };
}

const cases = {
    defaultCollapsedAndRemembersChoice() {
        const storage = memoryStorage();
        const body = element();
        const toggle = element();
        load(storage)(body, toggle, 'prtg-monitoring');

        assert.equal(body.classList.contains('show'), false);
        assert.equal(toggle.textContent, '展開');
        assert.equal(toggle.attributes['aria-expanded'], 'false');

        body.fire('shown.bs.collapse');
        assert.equal(toggle.textContent, '收合');
        assert.equal(toggle.attributes['aria-expanded'], 'true');
        assert.equal(storage.data['lf.collapse.prtg-monitoring'], 'open');

        // 下次進頁面：直接展開
        const again = element();
        const againToggle = element();
        load(storage)(again, againToggle, 'prtg-monitoring');
        assert.equal(again.classList.contains('show'), true);
        assert.equal(againToggle.textContent, '收合');

        again.fire('hidden.bs.collapse');
        assert.equal(storage.data['lf.collapse.prtg-monitoring'], 'closed');
    },
    headerClickForwardsToToggle() {
        // 觸發鈕在卡片標題列內時整列可點：點標題列空白處等同按觸發鈕
        const header = element();
        const toggle = element();
        let clicks = 0;
        toggle.click = () => { clicks++; };
        load(memoryStorage())(element(), toggle, 'record-detail-chat', { header });
        assert(header.classList.contains('lf-card__header--clickable'), '標題列有可點線索');

        const blank = { closest: () => null };
        header.fire('click', { target: blank });
        assert.equal(clicks, 1, '點標題列空白處或標題文字等同按觸發鈕');

        // 標題列上的其他按鈕（放大檢視、清除重來）與觸發鈕本身：交給它們自己，不再轉發一次
        const onButton = { closest: selector => (selector.split(',').map(s => s.trim()).includes('button') ? {} : null) };
        header.fire('click', { target: onButton });
        assert.equal(clicks, 1, '點標題列上的按鈕不開合');

        // 拖曳選字放開：有選取文字時不開合
        globalThis.getSelection = () => '詢問 AI';
        try {
            header.fire('click', { target: blank });
            assert.equal(clicks, 1, '拖曳選字不開合');
        } finally {
            delete globalThis.getSelection;
        }
        header.fire('click', { target: blank });
        assert.equal(clicks, 2, '沒有選取時恢復可點');
    },
    withoutHeaderKeepsButtonOnly() {
        // 沒給標題列的呼叫端行為不變：不加可點類別
        const toggle = element();
        load(memoryStorage())(element(), toggle, 'prtg-name-mismatch');
        assert.equal(toggle.attributes['aria-expanded'], 'false');
    },
    storageUnavailableStillWorks() {
        const throwing = {
            getItem() { throw new Error('SecurityError'); },
            setItem() { throw new Error('QuotaExceededError'); }
        };
        const body = element();
        const toggle = element();
        load(throwing)(body, toggle, 'prtg-monitoring');
        assert.equal(body.classList.contains('show'), false);
        body.fire('shown.bs.collapse');
        assert.equal(toggle.textContent, '收合');
        assert.equal(toggle.attributes['aria-expanded'], 'true');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
