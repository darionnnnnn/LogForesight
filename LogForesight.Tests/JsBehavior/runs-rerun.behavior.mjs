/**
 * 執行總表「補跑這些主機」（runs.js）：失敗或未執行的列按鈕只是把參數帶進既有的立即執行彈窗——
 * 模式（只補跑失敗或未執行）、資料日（換成回望天數）、主機（後端算好的名單）；預覽與確認流程不變。
 * 按鈕只給 Maintain（data-maintain-only）；沒有可補跑的主機不出現。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const src = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'runs.js'), 'utf8').replaceAll('\r\n', '\n');
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
const SOURCES = ['function runNowCarryFor', 'function openRunNowForDay', 'function rerunCell'].map(extract).join('\n');

function env({ maintain = true } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const els = new Map();
    const el = id => { if (!els.has(id)) els.set(id, Object.assign(dom.newElement('input'), { checked: false, value: '' })); return els.get(id); };
    const fakeDocument = { ...document, getElementById: id => el(id), createTextNode: t => document.createTextNode(t) };
    const calls = { preview: 0, modeUi: 0, status: 0, shown: 0, form: 0 };
    el('run-now-form').reset = () => { calls.form++; };
    const modal = { show: () => { calls.shown++; } };
    const state = { names: [] };
    const factory = new Function('document', 'button', 'updateRerunModeUI', 'updateRunNowPreview', 'refreshScheduleStatus', 'runNowModal', 'state', 'canMaintainSchedule',
        `let runNowHostNames = [];\n${SOURCES}\n` +
        `return { runNowCarryFor, openRunNowForDay, rerunCell, names: () => runNowHostNames };`);
    const api = factory(fakeDocument, button, () => { calls.modeUi++; }, () => { calls.preview++; }, () => { calls.status++; }, modal, state, maintain);
    return { api, el, calls };
}

const summary = (over = {}) => ({
    date: '2026-11-20', canRerun: true, rerunHostCount: 3, rerunHostNames: ['A', 'B', 'C'], ...over
});

const cases = {
    carriesModeDateAndHosts() {
        const { api, el, calls } = env();
        api.openRunNowForDay(summary());
        assert.equal(el('run-now-rerun-mode').value, 'None', '模式＝只補跑失敗或未執行');
        // 資料日 11/20 距今天 11/25 為 5 天（回望天數涵蓋到那個資料日）
        const carry = api.runNowCarryFor(summary(), new Date(2026, 10, 25, 9, 30));
        assert.equal(carry.backfillDays, 5);
        assert.deepEqual(carry.hostNames, ['A', 'B', 'C']);
        assert.equal(el('run-now-scope-hosts').checked, true);
        assert.equal(el('run-now-scope-all').checked, false);
        assert.deepEqual(api.names(), ['A', 'B', 'C']);
        assert(el('run-now-scope-hosts-label').textContent.includes('3 台'));
        assert.equal(calls.shown, 1, '開既有的立即執行彈窗');
        assert.equal(calls.preview, 1, '預覽照常');
        assert(Number(el('run-now-backfill').value) >= 1);
    },
    yesterdayIsOneDayBack() {
        const { api } = env();
        assert.equal(api.runNowCarryFor(summary({ date: '2026-11-24' }), new Date(2026, 10, 25, 23, 59)).backfillDays, 1);
    },
    overCapListFallsBackToAllHosts() {
        // 名單超過上限時後端給空清單：改用全部主機（仍只補跑失敗或未執行、同一資料日）
        const { api, el } = env();
        api.openRunNowForDay(summary({ rerunHostNames: [], rerunHostCount: 350 }));
        assert.equal(el('run-now-scope-all').checked, true);
        assert.equal(el('run-now-scope-hosts').checked, false);
        assert.deepEqual(api.names(), []);
        assert.equal(el('run-now-rerun-mode').value, 'None');
    },
    buttonOnlyForMaintainAndOnlyWhenRerunnable() {
        const withMaintain = env({ maintain: true }).api.rerunCell(summary());
        assert(withMaintain.tagName === 'BUTTON');
        assert.equal(withMaintain.classList.contains('d-none'), false);
        assert('maintainOnly' in withMaintain.dataset, '以 data-maintain-only 標記');
        const noMaintain = env({ maintain: false }).api.rerunCell(summary());
        assert.equal(noMaintain.classList.contains('d-none'), true, '沒有 Maintain 時隱藏');
        const none = env().api.rerunCell(summary({ canRerun: false, rerunHostCount: 0 }));
        assert.notEqual(none.tagName, 'BUTTON', '沒有可補跑的主機不出現按鈕');
    }
};

const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
