/**
 * 第 66 輪終檢：頁面內報告卡（core/report-view.js reportCard）省略 storageKey 時不記展開狀態。
 * 以 extractFunction 切出 production 的 reportCard，只替換 icon／safeStorage／toolbar 等外部相依。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'report-view.js'), 'utf8').replaceAll('\r\n', '\n');

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
const stored = [];
const safeStorage = { set: (k, v) => { stored.push([k, v]); }, get: () => 'true' };
const icon = () => dom.newElement('i');
const toolbar = () => { const t = dom.newElement('div'); t.className = 'lf-no-print'; return t; };
const subtitleOf = () => '副標';
const evidenceVersionNotice = () => null;
const pre = () => dom.newElement('pre');
const body = extractFunction('export function reportCard').replace(/^export /, '');
const reportCard = new Function('document', 'icon', 'safeStorage', 'toolbar', 'subtitleOf', 'evidenceVersionNotice', 'pre',
    `${body}\nreturn reportCard;`)(document, icon, safeStorage, toolbar, subtitleOf, evidenceVersionNotice, pre);

const report = { kindName: '日報', text: 'x' };
const click = header => (header.listeners.click || []).forEach(h => h());

const cases = {
    withoutStorageKeyAlwaysCollapsedAndNeverStores() {
        // 第一次 setReport 收合；之後 setReport 保留展開狀態；任何時候都不寫 safeStorage
        stored.length = 0;
        const card = reportCard({});
        card.setReport(report);
        const header = card.el.children[0];
        assert.equal(header.getAttribute('aria-expanded'), 'false');
        assert.equal(stored.length, 0);
        click(header);
        assert.equal(header.getAttribute('aria-expanded'), 'true');
        assert.equal(stored.length, 0);
        // 頁內重載（再次 setReport）保留目前的展開狀態，也不寫儲存
        card.setReport(report);
        assert.equal(header.getAttribute('aria-expanded'), 'true');
        assert(!card.el.children[1].classList.contains('d-none'));
        assert.equal(stored.length, 0);
        // 使用者收合後再重載仍維持收合
        click(header);
        card.setReport(report);
        assert.equal(header.getAttribute('aria-expanded'), 'false');
        assert.equal(stored.length, 0);
    },
    withStorageKeyStillRemembers() {
        stored.length = 0;
        const card = reportCard({ storageKey: 'k' });
        card.setReport(report);
        const header = card.el.children[0];
        assert.equal(header.getAttribute('aria-expanded'), 'true');
        assert(stored.some(([k]) => k === 'k'));
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
