/**
 * PRTG 維護頁「同步結構與對應」狀態卡的警告列（prtg-admin.js 的 renderStructureSyncStatus）。
 * 以切函式＋替身執行 production 函式本體；由 PrtgSyncWarningsBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'prtg-admin.js'), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿+/, '');

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

function env() {
    const dom = installDom();
    const { statusNote } = loadElements(dom);
    const el = () => document.createElement('div');
    const nodes = {
        'prtg-structure-sync-status': el(), 'prtg-structure-sync-progress': el(), 'prtg-structure-sync-warnings': el(),
        'prtg-structure-sync-btn': el(), 'prtg-structure-sync-cancel-btn': el()
    };
    const doc = { getElementById: id => nodes[id] ?? null };
    const state = { running: false };
    const render = new Function('document', 'statusNote', 'setupState', 'renderSetupSummary', 'PROGRESS_PHASE_LABEL', 'prtgEnabled',
        'formatDateTime', 'prtgHostMapSummary', 'structureSyncRunning',
        `${extractFunction('function renderStructureSyncStatus(')}\nreturn renderStructureSyncStatus;`)(
        doc, statusNote, {}, () => { }, {}, true, v => `T:${v}`, () => '對應摘要', false);
    return { render, nodes };
}

const base = { isRunning: false, lastCompletedAt: '2026-11-16T02:00:00', lastSuccess: true, lastSensors: 5, lastSource: 'manual' };
const warnNotes = n => { const out = []; n.find(x => { if ((x.className || '').includes('lf-status-note--warning')) out.push(x); return false; }); return out; };

const cases = {
    // 有警告：逐條一個 warning statusNote，內容走 textContent（標籤字元不會變成節點）
    warningsListedOneByOne() {
        const { render, nodes } = env();
        render({ ...base, lastWarnings: ['感測器全部回空', '<b>SRV1</b> 的裝置被清除'] });
        const notes = warnNotes(nodes['prtg-structure-sync-warnings']);
        assert.equal(notes.length, 2);
        assert.ok(notes[0].textContent.includes('感測器全部回空'));
        assert.ok(notes[1].textContent.includes('<b>SRV1</b>'));
    },
    // 沒警告／下一次重畫：清掉舊警告
    noWarningsLeavesAreaEmptyAndRerenderClears() {
        const { render, nodes } = env();
        render({ ...base, lastWarnings: ['舊警告'] });
        assert.equal(warnNotes(nodes['prtg-structure-sync-warnings']).length, 1);
        render({ ...base, lastWarnings: [] });
        assert.equal(nodes['prtg-structure-sync-warnings'].children.length, 0);
        render({ ...base });
        assert.equal(nodes['prtg-structure-sync-warnings'].children.length, 0);
    },
    // 未成功的同步也列警告；執行中不顯示舊警告
    failedSyncStillShowsWarningsRunningHidesThem() {
        const { render, nodes } = env();
        render({ ...base, lastSuccess: false, lastErrorMessage: '逾時', lastWarnings: ['半套鏡像'] });
        assert.equal(warnNotes(nodes['prtg-structure-sync-warnings']).length, 1);
        render({ ...base, isRunning: true, lastWarnings: ['半套鏡像'] });
        assert.equal(nodes['prtg-structure-sync-warnings'].children.length, 0);
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
