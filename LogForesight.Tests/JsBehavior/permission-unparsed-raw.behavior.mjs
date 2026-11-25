/**
 * 權限異動檢核（permission-changes.js）：無法解析的列可就地展開原始事件摘要，可解析的列沒有展開鈕。
 * 切出 summaryCell 與 rawEventText、注入替身執行；原文一律 textContent（不解析成 HTML）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const src = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'permission-changes.js'), 'utf8').replaceAll('\r\n', '\n');
const extract = decl => {
    const start = src.indexOf(decl);
    assert(start >= 0, `找不到 production 函式：${decl}`);
    const open = src.indexOf('{', src.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(start, i + 1);
    }
    throw new Error(`未閉合 ${decl}`);
};
const SOURCES = [extract('function summaryCell'), extract('function rawEventText')].join('\n');

function summary(row) {
    const dom = installDom();
    const button = loadButton(dom);
    const factory = new Function('document', 'button', `${SOURCES}\nreturn summaryCell;`);
    return factory(document, button)(row);
}
const toggleOf = cell => cell.find?.(n => n.tagName === 'BUTTON') ?? null;
const rawBoxOf = cell => cell.find?.(n => n.classList?.contains('lf-pre-wrap')) ?? null;

const cases = {
    unparsedRowHasToggleThatShowsRawText() {
        const cell = summary({ isUnparsed: true, summaryText: '偵測到權限異動，但無法解析細節。', rawText: 'Security-Auditing 4670 :: <b>x</b>' });
        const toggle = toggleOf(cell);
        assert(toggle, '無法解析的列要有展開鈕');
        assert.equal(toggle.textContent, '展開原文');
        const box = rawBoxOf(cell);
        assert(box.classList.contains('d-none'), '預設收合');
        assert.equal(box.textContent, 'Security-Auditing 4670 :: <b>x</b>', '原文照實顯示（textContent，不解析 HTML）');
        toggle.dispatch('click');
        assert(!box.classList.contains('d-none'));
        assert.equal(toggle.getAttribute('aria-expanded'), 'true');
        assert.equal(toggle.textContent, '收合原文');
        toggle.dispatch('click');
        assert(box.classList.contains('d-none'));
    },
    unparsedRowFallsBackToAlertTextThenNote() {
        const a = summary({ isUnparsed: true, summaryText: 's', rawText: null, alertText: '舊資料的截斷文字' });
        assert.equal(rawBoxOf(a).textContent, '舊資料的截斷文字');
        const b = summary({ isUnparsed: true, summaryText: 's', rawText: '  ', alertText: '' });
        assert.equal(rawBoxOf(b).textContent, '（沒有可顯示的原始事件內容）');
    },
    parsedRowHasNoToggle() {
        const cell = summary({ isUnparsed: false, summaryText: '某人將某人加入群組', rawText: 'raw' });
        assert.equal(toggleOf(cell), null, '可解析的列沒有展開鈕');
        assert.equal(rawBoxOf(cell), null);
    }
};

const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
