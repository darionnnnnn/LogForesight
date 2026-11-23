import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'prtg-admin.js'), 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

/** 以切出來的 production previewScope 執行；回應的 maxOffset 由「後端」給，前端不寫死 */
async function render({ total, offset, maxOffset = 30000 }) {
    const dom = installDom();
    const panel = dom.newElement('div');
    dom.byId.set('prtg-monitoring-preview-result', panel);
    const rows = Array.from({ length: Math.min(100, total - offset) }, (_, i) => ({
        hostName: 'H', sensorName: 'S', objid: i, configuredForEvaluation: true, ruleIds: [], disposition: 'd', notification: 'n', reasons: []
    }));
    const data = {
        day: '2026-11-22T00:00:00', settingsRevision: 1, policyRevision: 1, total, offset, rows, limitations: '',
        hasMore: offset + rows.length < total, maxOffset
    };
    const deps = {
        document,
        api: { get: async () => data },
        previewGuard: { next: () => 1, isCurrent: () => true },
        makeButton: (text, { onClick } = {}) => {
            const btn = dom.newElement('button');
            btn.textContent = text;
            if (onClick) btn.addEventListener('click', onClick);
            return btn;
        }
    };
    const factory = new Function(...Object.keys(deps),
        `let previewOffset = ${offset};\n${extractFunction('async function previewScope(')}\nreturn previewScope;`);
    await factory(...Object.values(deps))();
    const buttons = panel.children.filter(c => c.tagName === 'BUTTON');
    return { panel, next: buttons.find(b => b.textContent === '下一頁'), prev: buttons.find(b => b.textContent === '上一頁') };
}

const CAP_NOTE = '已到可瀏覽的上限，請用篩選縮小範圍';

async function atCapWithMoreDataDisablesNextAndExplains() {
    const r = await render({ total: 30150, offset: 30000 });
    assert.ok(r.next, '應有下一頁按鈕');
    assert.equal(r.next.disabled, true);
    assert.match(r.panel.textContent, new RegExp(CAP_NOTE));
    assert.equal(r.prev.disabled, false);
}

async function oneStepBeforeCapStillAllowsNext() {
    const r = await render({ total: 30050, offset: 29900 });
    assert.equal(r.next.disabled, false);
    assert.doesNotMatch(r.panel.textContent, new RegExp(CAP_NOTE));
}

async function smallTotalHasNoNote() {
    const r = await render({ total: 500, offset: 0 });
    assert.equal(r.next.disabled, false);
    assert.doesNotMatch(r.panel.textContent, new RegExp(CAP_NOTE));
}

async function lastPageWithinCapHasNoNote() {
    // 有更多資料才說明：offset 30,000 且剛好是最後一頁（沒有更多）不出說明、下一頁停用
    const r = await render({ total: 30050, offset: 30000 });
    assert.equal(r.next.disabled, true);
    assert.doesNotMatch(r.panel.textContent, new RegExp(CAP_NOTE));
}

async function limitComesFromResponseNotHardcoded() {
    const r = await render({ total: 1000, offset: 500, maxOffset: 500 });
    assert.equal(r.next.disabled, true);
    assert.match(r.panel.textContent, new RegExp(CAP_NOTE));
}

const cases = {
    atCapWithMoreDataDisablesNextAndExplains,
    oneStepBeforeCapStillAllowsNext,
    smallTotalHasNoNote,
    lastPageWithinCapHasNoNote,
    limitComesFromResponseNotHardcoded
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
try { await cases[name](); console.log(`PASS ${name}`); }
catch (error) { console.error(`FAIL ${name}: ${error.stack || error.message}`); process.exitCode = 1; }
