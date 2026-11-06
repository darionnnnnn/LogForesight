/**
 * 第 66 輪終檢：風險日詳情頁的側軌接線（record-detail.js 的 updateSideRail／expandSideRailForSelection）
 * 與處理面板的 onRefreshed 回呼（handling-panel.js 的 initHandlingPanel）。
 * 以 extractFunction 切出 production 函式本體，只替換外部相依（側軌、history、location、matchMedia、api）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

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
const refreshRailSelectionSummarySrc = extractFunction(recordDetail, 'function refreshRailSelectionSummary');
const expandForSelectionSrc = extractFunction(recordDetail, 'function expandSideRailForSelection');

/** 組一個頁面層環境：模組變數 sideRail／pendingHandlingHash 以 let 前置宣告，函式本體取自 production */
function pageEnv({ rail, hash, wide = true }) {
    const calls = { replaceState: 0 };
    const history = { replaceState: () => { calls.replaceState++; } };
    const location = { pathname: '/records/1/2026-11-01', search: '?a=1', hash };
    // 斷點由側軌自己回答（isRailMode），頁面層不再直接問 matchMedia
    if (rail) rail.isRailMode = () => wide;
    const derivedStatusLabel = h => h.label;
    const selectedIssueKeys = new Set();
    const factory = new Function('history', 'location', 'derivedStatusLabel', 'initialRail', 'initialHash', 'selectedIssueKeys',
        `let sideRail = initialRail; let pendingHandlingHash = initialHash; let railHandlingSummary = '';\n${updateSideRailSrc}\n${expandForSelectionSrc}\n${refreshRailSelectionSummarySrc}\n` +
        'return { updateSideRail, expandSideRailForSelection, refreshRailSelectionSummary, pending: () => pendingHandlingHash };');
    const env = factory(history, location, derivedStatusLabel, rail, hash === '#handling', selectedIssueKeys);
    return { ...env, calls, selectedIssueKeys };
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
        // 逾期放最前面：側軌窄、摘要會被截斷，逾期不能被截掉
        assert.equal(handling.text, '逾期・處理中（1/4 已結案）');
        assert(!handling.text.endsWith('・逾期'), handling.text);
        assert.equal(log.text, '7 筆');
        env.updateSideRail({ handling: { label: '已結案', isOverdue: false }, logCount: undefined });
        const last = rail.summaries.filter(s => s.key === 'handling').at(-1);
        assert.equal(last.text, '已結案');
        assert.equal(rail.summaries.filter(s => s.key === 'log').at(-1).text, '0 筆');
    },
    // 手動收合後勾選也要有回饋：摘要前綴「已勾選 N 個・」，取消到 0 回到原摘要
    selectionCountShownInSummary() {
        const rail = fakeRail();
        const env = pageEnv({ rail, hash: '' });
        env.updateSideRail({ handling: { label: '處理中', isOverdue: true }, logCount: 1 });
        const lastHandling = () => rail.summaries.filter(s => s.key === 'handling').at(-1).text;
        assert.equal(lastHandling(), '逾期・處理中');
        env.selectedIssueKeys.add('a');
        env.refreshRailSelectionSummary();
        assert.equal(lastHandling(), '已勾選 1 個・逾期・處理中');
        env.selectedIssueKeys.add('b');
        env.selectedIssueKeys.add('c');
        env.refreshRailSelectionSummary();
        assert.equal(lastHandling(), '已勾選 3 個・逾期・處理中');
        env.selectedIssueKeys.clear();
        env.refreshRailSelectionSummary();
        assert.equal(lastHandling(), '逾期・處理中');
        // 面板重載（onRefreshed）時若仍有勾選，摘要也要帶勾選數
        env.selectedIssueKeys.add('a');
        env.updateSideRail({ handling: { label: '未處理', isOverdue: false }, logCount: 1 });
        assert.equal(lastHandling(), '已勾選 1 個・未處理');
        // 案件授與沒有側軌：不得拋錯
        pageEnv({ rail: null, hash: '' }).refreshRailSelectionSummary();
    },
    // 單列勾選與表頭全選兩條路徑都要更新摘要
    selectionPathsBothRefreshSummary() {
        const calls = recordDetail.match(/refreshRailSelectionSummary\(\);/g) || [];
        assert(calls.length >= 2, `勾選路徑應至少兩處呼叫 refreshRailSelectionSummary，實際 ${calls.length}`);
        const single = extractFunction(recordDetail, 'function selectCheckbox');
        const all = extractFunction(recordDetail, 'function selectAllCheckbox');
        assert(single.includes('refreshRailSelectionSummary()'), '單列勾選');
        assert(all.includes('refreshRailSelectionSummary()'), '全選');
        // 取消勾選同樣走 change 事件：呼叫不能被包在 if (check.checked) 之內
        assert(!/if \(check\.checked\) refreshRailSelectionSummary/.test(single + all));
    },
    // 複製鈕：有可讀名稱，複製的是畫面上渲染後的文字（不帶 markdown 符號）
    async briefCopyUsesRenderedText() {
        const dom = installDom();
        const copied = [];
        const toasts = [];
        const { renderAiInline } = await import(pathToFileURL(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'markdown-lite.js')).href);
        const navigator = { clipboard: { writeText: async t => { copied.push(t); } } };
        const src = extractFunction(recordDetail, 'function briefBlock');
        const briefBlock = new Function('document', 'renderAiInline', 'navigator', 'toast', `${src}\nreturn briefBlock;`)(
            document, renderAiInline, navigator, (m, k) => toasts.push([m, k]));
        const wrap = briefBlock('一台主機**磁碟異常**，請採購備品。');
        const copyBtn = wrap.find(n => n.tagName === 'BUTTON');
        assert.equal(copyBtn.getAttribute('aria-label'), '複製給主管的說明');
        const shown = wrap.find(n => n.tagName === 'P').textContent;
        assert(!shown.includes('*'), shown);
        await Promise.all((copyBtn.listeners.click || []).map(h => h()));
        assert.deepEqual(copied, [shown]);
        assert(!copied[0].includes('*'));
        assert.deepEqual(toasts, [['已複製', 'success']]);
        void dom;
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
