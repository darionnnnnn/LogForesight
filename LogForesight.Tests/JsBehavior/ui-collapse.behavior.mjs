import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

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
            contains: name => classes.has(name)
        },
        setAttribute(name, value) { this.attributes[name] = value; },
        addEventListener: (type, handler) => { listeners[type] = handler; },
        fire: type => listeners[type]?.()
    };
}

function load(localStorage) {
    const body = extractFunction('export function bindRememberedCollapse').replace(/^export /, '');
    return new Function('localStorage', `${body}\nreturn bindRememberedCollapse;`)(localStorage);
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
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
