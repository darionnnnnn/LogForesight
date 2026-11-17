/**
 * 規則頁「套用內建規則升級」後的驗證警告（pages/rules.js）：
 * 警告在套用成功後出現，並在規則清單下一次重新載入（儲存、刪除、再次載入）時清掉，不整頁常駐。
 * 實際執行 production 的 applyRuleImport、load 與 renderRuleImportWarnings，只替換 api／toast／modal 等邊界。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'rules.js'), 'utf8').replaceAll('\r\n', '\n');

function extract(declaration) {
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

function setup() {
    const dom = installDom();
    const { statusNote } = loadElements(dom);
    const box = dom.newElement('div');
    box.id = 'rule-import-warnings';
    dom.byId.set('rule-import-warnings', box);
    for (const id of ['rule-import-overwrite', 'rule-import-apply-btn']) {
        const el = dom.newElement(id === 'rule-import-overwrite' ? 'input' : 'button');
        el.id = id;
        dom.byId.set(id, el);
    }
    const toasts = [];
    const fns = ['async function applyRuleImport(', 'async function load(', 'function renderRuleImportWarnings(', 'function clearRuleImportWarnings(']
        .map(d => source.includes(d) ? extract(d) : '').join('\n');
    const api = {
        get: async () => [],
        post: async () => ({ added: 1, updated: 0, warnings: ['規則 A 遮蔽規則 B', '規則 C 不合格被跳過'] })
    };
    const run = new Function('api', 'toast', 'withBusy', 'statusNote', 'renderLoading', 'renderRules', 'renderSuppressions', 'ruleImportModal', 'checkRuleImportStatus', 'document',
        `let rules = [], suppressions = [];\n${fns}\nreturn { applyRuleImport, load };`)(
        api, (m) => toasts.push(m), () => () => { }, statusNote, () => { }, () => { }, () => { }, { hide() { } }, async () => { }, document);
    return { box, run, toasts };
}

const textOf = n => n.textContent;
const items = box => box.children.length === 0 ? [] : box.children.flatMap(c => c.tagName === 'UL' ? c.children.map(textOf) : []);

const cases = {
    // 套用後（含套用後那次重新載入）警告要留在頁面上
    async warningsVisibleAfterApply() {
        const { box, run } = setup();
        await run.applyRuleImport();
        assert.deepEqual(items(box), ['規則 A 遮蔽規則 B', '規則 C 不合格被跳過']);
        assert.ok(box.children.some(c => textOf(c).includes('另有 2 項驗證警告')), '要有說明句');
    },
    // 規則清單重新載入（儲存、刪除等）時警告清掉，不整頁常駐
    async warningsClearedWhenRuleListReloads() {
        const { box, run } = setup();
        await run.applyRuleImport();
        assert.equal(items(box).length, 2);
        await run.load();
        assert.equal(box.children.length, 0, '重新載入後警告區要清空');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
