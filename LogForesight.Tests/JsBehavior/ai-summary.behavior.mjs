/**
 * 第 68 輪 B：風險日詳情 AI 摘要三格版面（aiSummaryBlock）與頁首資料來源副標（sourceLine）、
 * format.js 的 logSourceName／netiqAttemptStatusName 行為測試。
 * 從 production 原始碼抽出函式原文執行，由 AiSummaryBehaviorTests 以 `node ai-summary.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton } from './load-elements.mjs';

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

function extractBlock(source, prefix) {
    const start = source.indexOf(prefix);
    assert(start >= 0, `找不到 production 宣告：${prefix}`);
    return source.slice(start, source.indexOf('\n};\n', start) + 3);
}

const dom = installDom();
const button = loadButton(dom);

const formatSource = sourceOf('core', 'format.js');
const { logSourceName, netiqAttemptStatusName } = new Function(`
    ${extractFunction(formatSource, 'export function logSourceName(').replace(/^export /, '')}
    ${extractBlock(formatSource, 'const NETIQ_ATTEMPT_STATUS_NAMES')}
    ${extractFunction(formatSource, 'export function netiqAttemptStatusName(').replace(/^export /, '')}
    return { logSourceName, netiqAttemptStatusName };
`)();

const detailSource = sourceOf('pages', 'record-detail.js');
const { aiSummaryBlock } = new Function('renderAiInline', 'toast', 'button', `
    ${extractFunction(detailSource, 'function aiSummaryBlock(')}
    ${extractFunction(detailSource, 'function briefBlock(')}
    return { aiSummaryBlock };
`)((el, text) => { el.appendChild(document.createTextNode(text)); }, () => {}, button);

const { sourceLine } = new Function('logSourceName', 'netiqAttemptStatusName', `
    ${extractFunction(detailSource, 'function sourceLine(')}
    return { sourceLine };
`)(logSourceName, netiqAttemptStatusName);

const hasClass = (n, c) => String(n.className || '').split(' ').includes(c);
const all = (node, c) => node.querySelectorAll(`.${c}`);
const detailOf = (over) => ({ aiAnalyzed: true, headline: 'H', brief: 'B', summary: 'S', trendAssessment: 'T', action: 'A', ...over });

const cases = {
    五欄齊全三格依序且只有建議處置帶色條() {
        const block = aiSummaryBlock(detailOf());
        assert(hasClass(block, 'lf-ai-block'));
        assert.equal(all(block, 'lf-ai-summary').length, 1);
        const items = all(block, 'lf-ai-summary__item');
        assert.equal(items.length, 3);
        const labels = all(block, 'lf-ai-summary__label');
        assert.deepEqual(labels.map((n) => n.textContent), ['狀況', '趨勢', '建議處置']);
        assert(labels.every((n) => n.tagName === 'H3'));
        assert.deepEqual(items.map((n) => hasClass(n, 'lf-ai-summary__item--action')), [false, false, true]);
        assert.deepEqual(all(block, 'lf-ai-summary__text').map((n) => n.textContent), ['S', 'T', 'A']);
        assert.equal(all(block, 'lf-ai-brief').length, 1);
    },
    缺趨勢只剩兩格() {
        const block = aiSummaryBlock(detailOf({ trendAssessment: '' }));
        assert.deepEqual(all(block, 'lf-ai-summary__label').map((n) => n.textContent), ['狀況', '建議處置']);
        assert.equal(all(block, 'lf-ai-summary__item').length, 2);
    },
    三欄全空沒有三格容器但標題與速報仍在() {
        const block = aiSummaryBlock(detailOf({ summary: '', trendAssessment: '', action: '' }));
        assert.equal(all(block, 'lf-ai-summary').length, 0);
        assert(block.textContent.includes('H'));
        assert.equal(all(block, 'lf-ai-brief').length, 1);
    },
    統計模式維持舊呈現() {
        const block = aiSummaryBlock(detailOf({ aiAnalyzed: false }));
        assert(!hasClass(block, 'lf-ai-block'));
        assert.equal(all(block, 'lf-ai-block').length, 0);
        assert.equal(all(block, 'lf-ai-summary').length, 0);
        assert(block.textContent.includes('狀況：'), block.textContent);
        assert.equal(all(block, 'lf-ai-brief').length, 0);
    },
    來源與狀態中文名稱() {
        assert.deepEqual(['Netiq', 'Local', 'Prtg', '', null, 'x'].map(logSourceName), ['NetIQ', '本機', 'PRTG', '未知', '未知', '未知']);
        const expected = {
            'running': '執行中', 'success': '成功', 'partial': '部分完成', 'retained-incomplete': '保留不完整結果',
            'failed': '失敗', 'stopped': '已停止', 'empty-once': '查無資料待複查'
        };
        for (const [k, v] of Object.entries(expected)) assert.equal(netiqAttemptStatusName(k), v);
        assert.equal(netiqAttemptStatusName('weird'), '未知');
        assert.equal(netiqAttemptStatusName(null), '未知');
    },
    副標組字() {
        assert.equal(
            sourceLine({ logSource: 'Netiq', latestNetiqAttemptStatus: 'success', prtgSupplementEligible: true }),
            '資料來源：NetIQ（最近一次取數：成功；可再補 PRTG 資料）');
        const saved = sourceLine({ logSource: 'Netiq', latestNetiqAttemptStatus: null, prtgSupplementEligible: false });
        assert(saved.includes('已保存的分析結果'));
        assert(saved.includes('不能再補 PRTG 資料，原分析保留；請核對來源或補跑 NetIQ'));
        assert(sourceLine({ logSource: 'Prtg' }).startsWith('資料來源：PRTG（當日沒有日誌分析'), 'PRTG 當日的說明併進同一句');
        const unknown = sourceLine({ logSource: 'Netiq', latestNetiqAttemptStatus: 'weird', prtgSupplementEligible: true });
        assert(unknown.includes('未知'));
        assert(!unknown.includes('weird'));
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
