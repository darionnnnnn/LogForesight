/**
 * 第 63-2 輪 B-b-6：AI 摘要狀態（aiStateOf）與清單／詳情徽章的行為測試。
 *
 * 從 production 原始碼抽出 core/format.js 的 aiStateOf／aiDegradedBadge、records.js 的 headlineCell、
 * record-detail.js 的 aiStatusBadge 原文執行，只替換 DOM 與無關的徽章工廠；
 * 由 AiStateBehaviorTests 以 `node ai-state.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');

function sourceOf(...parts) {
    return fs.readFileSync(path.join(jsRoot, ...parts), 'utf8').replaceAll('\r\n', '\n');
}

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

function extractLine(source, prefix) {
    const start = source.indexOf(prefix);
    assert(start >= 0, `找不到 production 宣告：${prefix}`);
    return source.slice(start, source.indexOf('\n', start)).replace(/^export /, '');
}

installDom();

const formatSource = sourceOf('core', 'format.js');
const { aiStateOf, aiDegradedBadge, latestStatusCell, maxAttempts } = new Function(`
    ${extractLine(formatSource, 'export const AI_DEGRADED_MAX_ATTEMPTS')}
    ${extractFunction(formatSource, 'function aiStateOf(')}
    ${extractFunction(formatSource, 'function aiDegradedBadge(')}
    ${extractFunction(formatSource, 'function latestStatusCell(')}
    return { aiStateOf, aiDegradedBadge, latestStatusCell, maxAttempts: AI_DEGRADED_MAX_ATTEMPTS };
`)();

const { headlineCell } = new Function('aiStateOf', 'aiDegradedBadge', 'statusBadge', `
    ${extractFunction(sourceOf('pages', 'records.js'), 'function headlineCell(')}
    return { headlineCell };
`)(aiStateOf, aiDegradedBadge, (text) => {
    const node = document.createElement('span');
    node.className = 'lf-badge';
    node.textContent = text;
    node.classList.add = node.classList.add.bind(node.classList);
    return node;
});

const { aiStatusBadge } = new Function('aiStateOf', 'aiDegradedBadge', `
    ${extractFunction(sourceOf('pages', 'record-detail.js'), 'function aiStatusBadge(')}
    return { aiStatusBadge };
`)(aiStateOf, aiDegradedBadge);

/** 只收「徽章」節點（class 含 lf-badge）的文字 */
function badgeTexts(node) {
    const found = [];
    const walk = (n) => {
        if (String(n.className || '').split(' ').includes('lf-badge')) found.push(n);
        (n.children || []).forEach(walk);
    };
    walk(node);
    return found.map((n) => n.textContent);
}

const QUEUED = '（統計已完成，AI 分析排隊中）';
const FAILED = '今日分析摘要暫缺（AI 服務未回應，AI 待補）';
const LEGACY_ANOMALY = 'AI 回覆格式異常，以下為原始內容';
const RULE = '磁碟錯誤偏多';

/** 紀錄形狀：aiAnalyzed／aiPending／aiDegradedAttempts／headline 四欄是徽章判定的全部輸入 */
const rec = (aiAnalyzed, aiPending, aiDegradedAttempts, headline) => ({ aiAnalyzed, aiPending, aiDegradedAttempts, headline });

// 與改動前一致的現有狀態（徽章文字不得改變），以及新增的降級／放棄
const LIST_TABLE = [
    ['已完成', rec(true, false, 0, RULE), ['AI']],
    ['排隊中', rec(false, true, 0, QUEUED), ['AI 分析中']],
    ['強制重新分析中（舊結論保留）', rec(true, true, 0, RULE), ['AI']],
    ['統計模式', rec(false, false, 0, '（統計模式紀錄，未呼叫 AI 分析）'), []],
    ['歷史舊標題', rec(true, false, 0, LEGACY_ANOMALY), ['AI']],
    ['降級後已補成功（次數保留）', rec(true, false, 1, RULE), ['AI']],
    ['降級待補第 1 次', rec(true, true, 1, `${RULE}（AI 摘要待補，第 1 次）`), ['AI 摘要待補（第 1 次格式異常）']],
    ['降級待補第 2 次', rec(true, true, 2, `${RULE}（AI 摘要待補，第 2 次）`), ['AI 摘要待補（第 2 次格式異常）']],
    ['已放棄', rec(true, false, 3, `${RULE}（AI 摘要已放棄：連續 3 次格式異常）`), ['AI 摘要已放棄']]
];

const DETAIL_TABLE = [
    ['已完成', rec(true, false, 0, RULE), []],
    ['排隊中', rec(false, true, 0, QUEUED), ['AI 分析中']],
    ['強制重新分析中（舊結論保留）', rec(true, true, 0, RULE), ['AI 分析中']],
    ['統計模式', rec(false, false, 0, '（統計模式紀錄，未呼叫 AI 分析）'), ['統計模式（AI 未分析）']],
    ['歷史舊標題', rec(true, false, 0, LEGACY_ANOMALY), []],
    ['降級後已補成功（次數保留）', rec(true, false, 2, RULE), []],
    ['降級待補第 2 次', rec(true, true, 2, `${RULE}（AI 摘要待補，第 2 次）`), ['AI 摘要待補（第 2 次格式異常）']],
    ['已放棄', rec(true, false, 3, `${RULE}（AI 摘要已放棄：連續 3 次格式異常）`), ['AI 摘要已放棄']]
];

const cases = {
    aiStateOf四態() {
        assert.equal(aiStateOf(rec(true, true, 1, 'x')), 'degraded');
        assert.equal(aiStateOf(rec(true, true, 2, 'x')), 'degraded');
        assert.equal(aiStateOf(rec(true, false, 3, 'x')), 'abandoned');
        assert.equal(aiStateOf(rec(false, true, 0, 'x')), 'pending');
        assert.equal(aiStateOf(rec(true, true, 0, 'x')), 'pending');
        assert.equal(aiStateOf(rec(true, false, 0, 'x')), 'done');
        // 降級後已補成功：次數保留、不待補、未達上限＝完成
        assert.equal(aiStateOf(rec(true, false, 1, 'x')), 'done');
        assert.equal(aiStateOf(rec(true, false, 2, 'x')), 'done');
    },
    aiStateOf完全失敗標題只看欄位不看文字() {
        // AI 服務未回應：aiPending 且 aiAnalyzed=false、沒降級過 → 與排隊中同一態（pending），標題文字不參與判定
        assert.equal(aiStateOf(rec(false, true, 0, FAILED)), 'pending');
        assert.equal(aiStateOf(rec(false, true, 0, QUEUED)), 'pending');
        // 標題文字寫了「AI 待補」但欄位是完成：不誤判
        assert.equal(aiStateOf(rec(true, false, 0, FAILED)), 'done');
    },
    aiStateOf歷史舊標題與缺欄位維持完成() {
        assert.equal(aiStateOf(rec(true, false, 0, LEGACY_ANOMALY)), 'done');
        assert.equal(aiStateOf({}), 'done');
        assert.equal(aiStateOf(null), 'done');
        assert.equal(aiStateOf({ aiPending: true }), 'pending');
        assert.equal(aiStateOf({ aiPending: false, aiDegradedAttempts: '3' }), 'abandoned');
    },
    上限常數是3() {
        assert.equal(maxAttempts, 3);
        assert.equal(aiStateOf(rec(true, false, maxAttempts - 1, 'x')), 'done');
        assert.equal(aiStateOf(rec(true, false, maxAttempts, 'x')), 'abandoned');
    },
    降級徽章文字與用色() {
        const degraded = aiDegradedBadge(rec(true, true, 2, 'x'), 'me-1');
        assert.equal(degraded.textContent, 'AI 摘要待補（第 2 次格式異常）');
        assert(degraded.className.includes('lf-badge--warning'), degraded.className);
        assert(degraded.className.includes('me-1'), degraded.className);
        assert(degraded.title.includes('下一次 AI 排程自動再試'), degraded.title);
        assert(degraded.title.includes('連續 3 次'), degraded.title);

        const abandoned = aiDegradedBadge(rec(true, false, 3, 'x'));
        assert.equal(abandoned.textContent, 'AI 摘要已放棄');
        assert(abandoned.className.includes('lf-badge--danger'), abandoned.className);
        assert(abandoned.title.includes('不再自動補跑'), abandoned.title);

        for (const r of [rec(true, false, 0, 'x'), rec(false, true, 0, 'x'), rec(true, false, 1, 'x')])
            assert.equal(aiDegradedBadge(r), null);
    },
    清單徽章文字對照() {
        for (const [label, record, expected] of LIST_TABLE) {
            const wrap = headlineCell({ ...record, handlingStatus: 'open' });
            assert.deepEqual(badgeTexts(wrap), expected, `清單：${label}`);
        }
    },
    清單降級時標題文字照常顯示() {
        const wrap = headlineCell(rec(true, true, 1, `${RULE}（AI 摘要待補，第 1 次）`));
        assert(wrap.textContent.includes(`${RULE}（AI 摘要待補，第 1 次）`), wrap.textContent);
    },
    最新狀況一格只有降級或放棄才加徽章() {
        const text = `${RULE}（AI 摘要待補，第 2 次）`;
        const degraded = latestStatusCell({ latestAiPending: true, latestAiDegradedAttempts: 2 }, text);
        assert.deepEqual(badgeTexts(degraded), ['AI 摘要待補（第 2 次格式異常）']);
        assert(degraded.textContent.includes(text), degraded.textContent);
        const abandoned = latestStatusCell({ latestAiPending: false, latestAiDegradedAttempts: 3 }, RULE);
        assert.deepEqual(badgeTexts(abandoned), ['AI 摘要已放棄']);
        // 排隊中、完成、降級後已恢復：維持純文字，表格欄位不多一顆徽章
        assert.equal(latestStatusCell({ latestAiPending: true, latestAiDegradedAttempts: 0 }, RULE), RULE);
        assert.equal(latestStatusCell({ latestAiPending: false, latestAiDegradedAttempts: 0 }, RULE), RULE);
        assert.equal(latestStatusCell({ latestAiPending: false, latestAiDegradedAttempts: 1 }, RULE), RULE);
    },
    詳情徽章文字對照() {
        for (const [label, record, expected] of DETAIL_TABLE) {
            const badge = aiStatusBadge(record);
            assert.deepEqual(badge ? [badge.textContent] : [], expected, `詳情：${label}`);
        }
    },
    詳情案件授與檢視不顯示AI狀態() {
        assert.equal(aiStatusBadge({ ...rec(true, true, 2, 'x'), caseGrantOnly: true }), null);
        assert.equal(aiStatusBadge({ ...rec(false, false, 0, ''), caseGrantOnly: true }), null);
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
