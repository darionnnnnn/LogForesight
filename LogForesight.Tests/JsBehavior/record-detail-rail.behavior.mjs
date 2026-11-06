/**
 * 第 66 輪終檢：風險日詳情頁的側軌接線（record-detail.js 的 updateSideRail／expandSideRailForSelection）
 * 與處理面板的 onRefreshed 回呼（handling-panel.js 的 initHandlingPanel）。
 * 以 extractFunction 切出 production 函式本體，只替換外部相依（側軌、history、location、matchMedia、api）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const read = (...p) => fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', ...p), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿/, '');
const recordDetail = read('pages', 'record-detail.js');
const handlingPanel = read('pages', 'handling-panel.js');

function extractFunction(source, declaration) {
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

const updateSideRailSrc = extractFunction(recordDetail, 'function updateSideRail');
const expandForSelectionSrc = extractFunction(recordDetail, 'function expandSideRailForSelection');

/** 組一個頁面層環境：模組變數 sideRail／pendingHandlingHash 以 let 前置宣告，函式本體取自 production */
function pageEnv({ rail, hash, wide = true }) {
    const calls = { replaceState: 0 };
    const history = { replaceState: () => { calls.replaceState++; } };
    const location = { pathname: '/records/1/2026-11-01', search: '?a=1', hash };
    const window = { matchMedia: () => ({ matches: wide }) };
    const derivedStatusLabel = h => h.label;
    const factory = new Function('history', 'location', 'window', 'derivedStatusLabel', 'initialRail', 'initialHash',
        `let sideRail = initialRail; let pendingHandlingHash = initialHash;\n${updateSideRailSrc}\n${expandForSelectionSrc}\n` +
        'return { updateSideRail, expandSideRailForSelection, pending: () => pendingHandlingHash };');
    const env = factory(history, location, window, derivedStatusLabel, rail, hash === '#handling');
    return { ...env, calls };
}

function fakeRail({ userChoice = false, expanded = false } = {}) {
    return {
        expands: [], summaries: [],
        userChoice, expanded,
        expand(key, opts) { this.expands.push({ key, opts }); this.expanded = true; },
        setSummary(key, text) { this.summaries.push({ key, text }); },
        hasUserChoice() { return this.userChoice; },
        isExpanded() { return this.expanded; }
    };
}

const cases = {
    hashHandlingWideExpandsOnceAndClearsHash() {
        const rail = fakeRail();
        const env = pageEnv({ rail, hash: '#handling', wide: true });
        env.updateSideRail({ handling: { label: '處理中', isOverdue: false }, logCount: 2 });
        assert.equal(rail.expands.length, 1);
        assert.deepEqual(rail.expands[0], { key: 'handling', opts: { focus: false, scroll: true } });
        assert.equal(env.calls.replaceState, 1);
        // 面板之後每次自行重載都會再回呼：不得再展開、也不再清 hash
        env.updateSideRail({ handling: { label: '處理中', isOverdue: false }, logCount: 3 });
        assert.equal(rail.expands.length, 1);
        assert.equal(env.calls.replaceState, 1);
    },
    hashHandlingNarrowOnlyClearsHash() {
        const rail = fakeRail();
        const env = pageEnv({ rail, hash: '#handling', wide: false });
        env.updateSideRail({ handling: { label: '未處理', isOverdue: false }, logCount: 0 });
        assert.equal(rail.expands.length, 0);
        assert.equal(env.calls.replaceState, 1);
    },
    hashHandlingCaseGrantWithoutRailStillClearsHash() {
        const env = pageEnv({ rail: null, hash: '#handling', wide: true });
        env.updateSideRail({ handling: { label: '未處理', isOverdue: false }, logCount: 0 });
        assert.equal(env.calls.replaceState, 1);
        assert.equal(env.pending(), false);
    },
    noHashDoesNotTouchHistory() {
        const rail = fakeRail();
        const env = pageEnv({ rail, hash: '', wide: true });
        env.updateSideRail({ handling: { label: '未處理', isOverdue: false }, logCount: 1 });
        assert.equal(env.calls.replaceState, 0);
        assert.equal(rail.expands.length, 0);
    },
    summaryShowsOverdueSuffixAndLogCount() {
        const rail = fakeRail();
        const env = pageEnv({ rail, hash: '', wide: true });
        env.updateSideRail({ handling: { label: '處理中（1/4 已結案）', isOverdue: true }, logCount: 7 });
        const handling = rail.summaries.find(s => s.key === 'handling');
        const log = rail.summaries.find(s => s.key === 'log');
        assert(handling.text.endsWith('・逾期'), handling.text);
        assert.equal(log.text, '7 筆');
        env.updateSideRail({ handling: { label: '已結案', isOverdue: false }, logCount: undefined });
        const last = rail.summaries.filter(s => s.key === 'handling').at(-1);
        assert(!last.text.endsWith('・逾期'));
        assert.equal(rail.summaries.filter(s => s.key === 'log').at(-1).text, '0 筆');
    },
    expandForSelectionRules() {
        const fresh = fakeRail();
        pageEnv({ rail: fresh, hash: '' }).expandSideRailForSelection();
        assert.equal(fresh.expands.length, 1);
        assert.deepEqual(fresh.expands[0].opts, { focus: false, scroll: false });

        const chosen = fakeRail({ userChoice: true });
        pageEnv({ rail: chosen, hash: '' }).expandSideRailForSelection();
        assert.equal(chosen.expands.length, 0);

        const open = fakeRail({ expanded: true });
        pageEnv({ rail: open, hash: '' }).expandSideRailForSelection();
        assert.equal(open.expands.length, 0);

        // 案件授與沒有側軌：不得拋錯
        pageEnv({ rail: null, hash: '' }).expandSideRailForSelection();
    },

    // 處理面板每次載完都要通知頁面層（含面板自行重載），否則側軌摘要停在舊值
    async panelNotifiesOnRefreshed() {
        const src = extractFunction(handlingPanel, 'export async function initHandlingPanel').replace(/^export /, '');
        const handling = { canAssign: false, statusText: '處理中' };
        const api = { get: async () => handling };
        const document = { getElementById: () => ({}) };
        const factory = new Function('document', 'renderLoading', 'api', 'loadAssignableUsers', 'render', 'loadLogs',
            `let state = null;\n${src}\nreturn initHandlingPanel;`);
        const initHandlingPanel = factory(document, () => { }, api, async () => [], () => { }, async () => 5);
        const seen = [];
        const options = { onRefreshed: info => seen.push(info) };
        const result = await initHandlingPanel(1, '2026-11-01', () => new Set(), () => { }, options);
        assert.deepEqual(result, { handling, logCount: 5 });
        assert.equal(seen.length, 1);
        assert.deepEqual(seen[0], { handling, logCount: 5 });
        // 面板內部重載沿用同一份 options：再呼叫一次就再通知一次
        await initHandlingPanel(1, '2026-11-01', () => new Set(), () => { }, options);
        assert.equal(seen.length, 2);
        // 沒帶回呼（其他頁面）也不能壞
        await initHandlingPanel(1, '2026-11-01', () => new Set(), () => { });
    },
    panelInternalReloadsKeepOptions() {
        // 面板內部的四處重載都必須帶 state.options，onRefreshed 才會一路沿用
        const calls = handlingPanel.match(/await initHandlingPanel\([^)]*\)/g) || [];
        assert(calls.length >= 4, `內部重載呼叫數：${calls.length}`);
        for (const c of calls) assert(c.includes('state.options'), c);
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
