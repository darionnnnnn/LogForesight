/**
 * 第 70 輪 E：風險日詳情頁首 AI 摘要版面（結論列／速報＋建議處置／狀況＋趨勢）（aiSummaryBlock）與頁首資料來源副標（sourceLine）、
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
const { aiSummaryBlock, identityFacts } = new Function('renderAiInline', 'toast', 'button', `
    ${extractFunction(detailSource, 'function aiSummaryBlock(')}
    ${extractFunction(detailSource, 'function briefBlock(')}
    ${extractFunction(detailSource, 'function identityFacts(')}
    return { aiSummaryBlock, identityFacts };
`)((el, text) => { el.appendChild(document.createTextNode(text)); }, () => {}, button);

const { sourceLine } = new Function('logSourceName', 'netiqAttemptStatusName', `
    ${extractFunction(detailSource, 'function sourceLine(')}
    return { sourceLine };
`)(logSourceName, netiqAttemptStatusName);

const statusBadgeOf = (label) => {
    const badge = document.createElement('span');
    badge.className = 'lf-badge lf-badge--info';
    badge.textContent = label;
    return badge;
};
/** 頁首所有眉標（lf-text-label）的文字，依 DOM 順序 */
const labelsOf = (block) => all(block, 'lf-text-label').map((n) => n.textContent);
/** 徽章節點（class 含 lf-badge）的文字，依 DOM 順序 */
const badgeTexts = (block) => all(block, 'lf-badge').map((n) => n.textContent);
const hasClass = (n, c) => String(n.className || '').split(' ').includes(c);
const all = (node, c) => node.querySelectorAll(`.${c}`);
const detailOf = (over) => ({ aiAnalyzed: true, headline: 'H', brief: 'B', summary: 'S', trendAssessment: 'T', action: 'A', ...over });

const cases = {
    五欄齊全版面區塊依序且各段只一份() {
        const block = aiSummaryBlock(detailOf());
        assert(hasClass(block, 'lf-briefing'));
        // 區塊順序：結論列 → 重點速報＋建議處置 → 狀況＋趨勢
        assert.deepEqual(block.children.map((n) => n.className.split(' ')[0]),
            ['lf-briefing__conclusion', 'lf-briefing__lead', 'lf-briefing__detail']);
        assert(hasClass(block.children[1], 'lf-briefing__lead--split'));
        assert.deepEqual(labelsOf(block), ['重點速報', '建議處置', '狀況', '趨勢']);
        assert(all(block, 'lf-text-label').every((n) => n.tagName === 'H3'), '四段共用同一種眉標 h3');
        assert.equal(all(block, 'lf-briefing__headline')[0].textContent, 'H');
        assert.equal(all(block, 'lf-briefing__brief-text')[0].textContent, 'B');
        assert.deepEqual(all(block, 'lf-briefing__text').map((n) => n.textContent), ['A', 'S', 'T']);
        // 頁首不再用舊容器與「重點速報」徽章
        assert.equal(all(block, 'lf-ai-block').length, 0);
        assert(!badgeTexts(block).includes('重點速報'), '重點速報是眉標不是徽章');
    },
    AI完成時AI摘要徽章恰好一顆且在標題之後() {
        const block = aiSummaryBlock(detailOf());
        assert.deepEqual(badgeTexts(block), ['AI 摘要']);
        const row = block.children[0];
        assert.deepEqual(row.children.map((n) => n.className.split(' ')[0]), ['lf-briefing__headline', 'lf-badge']);
        assert(hasClass(row.children[1], 'lf-badge--secondary'));
    },
    建議處置底色只在AI完成時出現() {
        assert.equal(all(aiSummaryBlock(detailOf()), 'lf-briefing__action--tinted').length, 1);
        assert.equal(all(aiSummaryBlock(detailOf({ aiAnalyzed: false })), 'lf-briefing__action--tinted').length, 0);
        assert.equal(all(aiSummaryBlock(detailOf({ aiAnalyzed: false })), 'lf-briefing__action').length, 1, '統計模式建議處置仍在，只是沒有底色');
    },
    複製鈕在重點速報區塊內且複製渲染後文字() {
        const block = aiSummaryBlock(detailOf({ brief: '一台主機磁碟異常' }));
        const brief = all(block, 'lf-briefing__brief')[0];
        const head = all(brief, 'lf-briefing__brief-head')[0];
        assert.deepEqual(head.children.map((n) => n.tagName), ['H3', 'BUTTON'], '複製鈕在眉標列右側');
        const copy = head.children[1];
        assert.equal(copy.getAttribute('aria-label'), '複製重點速報');
        assert(hasClass(copy, 'lf-no-print'));
        assert.equal(all(block, 'lf-briefing__lead')[0].querySelectorAll('.lf-briefing__brief-text').length, 1);
        assert.equal(block.find((n) => n.tagName === 'BUTTON' && !brief.find((m) => m === n)), null, '整個頁首只有速報這一顆複製鈕');
        const wait = () => new Promise((r) => setTimeout(r, 0));
        return (async () => {
            const written = [];
            Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: async (t) => written.push(t) } }, configurable: true });
            copy.dispatch('click'); await wait();
            assert.deepEqual(written, [all(brief, 'lf-briefing__brief-text')[0].textContent]);
            assert.deepEqual(written, ['一台主機磁碟異常']);
        })();
    },
    只有標題沒有其他區塊也沒有眉標與細線() {
        const block = aiSummaryBlock(detailOf({ brief: '', summary: '', trendAssessment: '', action: '' }));
        assert.deepEqual(block.children.map((n) => n.className.split(' ')[0]), ['lf-briefing__conclusion']);
        assert.deepEqual(labelsOf(block), []);
        assert.equal(all(block, 'lf-briefing__lead').length + all(block, 'lf-briefing__detail').length, 0);
        assert.equal(block.find((n) => n.tagName === 'SECTION'), null);
    },
    缺速報時建議處置佔滿這一列() {
        const block = aiSummaryBlock(detailOf({ brief: '' }));
        const lead = all(block, 'lf-briefing__lead')[0];
        assert(!hasClass(lead, 'lf-briefing__lead--split'));
        assert.equal(lead.children.length, 1);
        assert.deepEqual(labelsOf(block), ['建議處置', '狀況', '趨勢']);
        assert.equal(all(block, 'lf-briefing__brief').length, 0);
        assert.equal(block.find((n) => n.tagName === 'BUTTON'), null, '沒有速報就沒有複製鈕');
    },
    缺建議處置時重點速報佔滿() {
        const block = aiSummaryBlock(detailOf({ action: '' }));
        const lead = all(block, 'lf-briefing__lead')[0];
        assert(!hasClass(lead, 'lf-briefing__lead--split'));
        assert.equal(lead.children.length, 1);
        assert.deepEqual(labelsOf(block), ['重點速報', '狀況', '趨勢']);
        assert.equal(all(block, 'lf-briefing__action').length, 0);
    },
    缺狀況或趨勢時只剩一段且全缺時沒有細線容器() {
        const noTrend = aiSummaryBlock(detailOf({ trendAssessment: '' }));
        assert.deepEqual(labelsOf(noTrend), ['重點速報', '建議處置', '狀況']);
        assert.equal(all(noTrend, 'lf-briefing__item').length, 1);
        const noSummary = aiSummaryBlock(detailOf({ summary: '' }));
        assert.deepEqual(labelsOf(noSummary), ['重點速報', '建議處置', '趨勢']);
        const none = aiSummaryBlock(detailOf({ summary: '', trendAssessment: '' }));
        assert.equal(all(none, 'lf-briefing__detail').length, 0, '兩段都沒有時不留細線容器');
        assert.deepEqual(labelsOf(none), ['重點速報', '建議處置']);
        assert.deepEqual(none.children.map((n) => n.className.split(' ')[0]), ['lf-briefing__conclusion', 'lf-briefing__lead']);
    },
    分析中狀態徽章進結論列且沒有AI摘要徽章() {
        const block = aiSummaryBlock(detailOf({ aiAnalyzed: false, brief: '' }), statusBadgeOf('AI 分析中'));
        assert.deepEqual(badgeTexts(block), ['AI 分析中']);
        assert.equal(block.children[0].children.at(-1).textContent, 'AI 分析中');
    },
    降級待補與已放棄只放狀態徽章不放AI摘要徽章() {
        for (const text of ['AI 摘要待補（第 2 次格式異常）', 'AI 摘要已放棄']) {
            const block = aiSummaryBlock(detailOf(), statusBadgeOf(text));
            assert.deepEqual(badgeTexts(block), [text]);
            assert(all(block, 'lf-briefing__conclusion')[0].textContent.includes(text));
        }
    },
    統計模式同一套版面但沒有AI摘要徽章與速報與底色() {
        const block = aiSummaryBlock(detailOf({ aiAnalyzed: false }), statusBadgeOf('統計模式（AI 未分析）'));
        assert.deepEqual(badgeTexts(block), ['統計模式（AI 未分析）']);
        assert.equal(all(block, 'lf-briefing__brief').length, 0);
        assert.equal(all(block, 'lf-briefing__action--tinted').length, 0);
        assert.deepEqual(labelsOf(block), ['建議處置', '狀況', '趨勢']);
        assert.deepEqual(block.children.map((n) => n.className.split(' ')[0]),
            ['lf-briefing__conclusion', 'lf-briefing__lead', 'lf-briefing__detail']);
        assert.equal(block.find((n) => n.tagName === 'BUTTON'), null);
    },
    沒有結論列時狀態徽章不被收下() {
        const badge = statusBadgeOf('AI 分析中');
        const block = aiSummaryBlock(detailOf({ aiAnalyzed: false, headline: '' }), badge);
        assert.equal(block.querySelector('.lf-briefing__conclusion'), null);
        assert.equal(badgeTexts(block).length, 0, '徽章留給呼叫端放在第一行');
    },
    案件授與檢視沒有白話總覽() {
        assert.equal(aiSummaryBlock({ caseGrantOnly: true, aiAnalyzed: false }), null);
        assert.equal(aiSummaryBlock({ caseGrantOnly: true, aiAnalyzed: false }, null), null);
    },
    五欄全空回null() {
        assert.equal(aiSummaryBlock({ aiAnalyzed: true }), null);
        assert.equal(aiSummaryBlock({ aiAnalyzed: true, headline: '', brief: '', summary: '', trendAssessment: '', action: '' }), null);
        // 未分析時速報不算內容
        assert.equal(aiSummaryBlock({ aiAnalyzed: false, brief: 'B' }), null);
    },
    五欄文字一字不減() {
        const block = aiSummaryBlock(detailOf({ headline: '標題一', brief: '速報二', summary: '狀況三', trendAssessment: '趨勢四', action: '處置五' }));
        const text = block.textContent;
        for (const s of ['標題一', '速報二', '狀況三', '趨勢四', '處置五']) assert(text.includes(s), `缺 ${s}`);
    },
    第一行身分項目NetIQ主機不重複IP() {
        // NetIQ 主機的主機名就是 IP：次要資訊行不再放一次
        assert.deepEqual(identityFacts({ hostName: '10.1.2.3', hostIpAddress: '10.1.2.3', hostOs: 'windows' }), ['Windows', null]);
        // 本機主機：主機名與 IP 不同，兩者都有
        assert.deepEqual(identityFacts({ hostName: 'SRV01', hostIpAddress: '10.1.2.4', hostOs: 'windows' }), ['Windows', '10.1.2.4']);
        assert.deepEqual(identityFacts({ hostName: 'web', hostIpAddress: '10.1.2.5', hostOs: 'linux' }), ['Linux', '10.1.2.5']);
        assert.deepEqual(identityFacts({ hostName: 'SRV02', hostIpAddress: null, hostOs: 'linux' }), ['Linux', null]);
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
