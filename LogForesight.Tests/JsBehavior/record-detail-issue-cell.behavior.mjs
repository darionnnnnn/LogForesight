/**
 * 風險日詳情「問題」欄（issueCell）的白話說明位置與 guidancePanel 不重複輸出說明：
 * 以 extractFunction 切出 production 函式本體，只替換外部相依（collapsibleText、severityCell、各區塊產生器）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const recordDetail = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'record-detail.js'), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿/, '');

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

installDom();
const issueCellSrc = extractFunction(recordDetail, 'function issueCell');
const guidancePanelSrc = extractFunction(recordDetail, 'function guidancePanel');
const appendListSrc = extractFunction(recordDetail, 'function appendList');

const mark = (cls, text = '') => { const el = document.createElement('div'); el.className = cls; el.textContent = text; return el; };

const issueCell = new Function('document', 'collapsibleText', 'severityCell', 'formatNumber', 'sourceEvidenceBlock', 'currentDetail',
    'residualCredentialBlock', 'loginFailureDetailsTable', 'keyDetailsBlock', 'prtgTrendEvidenceBlock', 'sampleMessagesTrigger',
    `${issueCellSrc}\nreturn issueCell;`)(
    document, el => el, () => mark('severity'), n => String(n), () => mark('evidence'), { detailPruned: true },
    () => mark('x'), () => mark('x'), () => mark('x'), () => mark('x'), () => mark('x'));

const guidancePanel = new Function('document', 'collapsibleText', 'renderAiInline', 'aiAvailable', 'aiInterpretPanel',
    `${appendListSrc}\n${guidancePanelSrc}\nreturn guidancePanel;`)(
    document, el => el, (el, t) => { el.textContent = t; }, false, () => 'AI');

const baseIssue = (extra = {}) => ({
    sourceEventLabel: 'Security 4625', logName: 'Security', severity: 'high', count: 3,
    firstSeen: '01:00', lastSeen: '02:00', ...extra
});
const textsOf = wrap => wrap.children.map(c => c.textContent);

const cases = {
    explanationAndKnownIssueBeforeLogName() {
        const wrap = issueCell(baseIssue({ guidance: { explanation: '有人一直猜密碼' }, knownIssue: '已知問題 A' }));
        assert.equal(wrap.children[0].textContent, 'Security 4625');
        assert(wrap.children[1].className.includes('lf-issue-cell__explain'));
        assert.equal(wrap.children[1].textContent, '有人一直猜密碼');
        assert.equal(wrap.children[2].textContent, '已知問題 A');
        const logIndex = wrap.children.findIndex(c => c.textContent === 'Security');
        assert(logIndex > 2, `logName 應在兩者之後：${textsOf(wrap).join('|')}`);
    },
    identicalTextShownOnce() {
        const wrap = issueCell(baseIssue({ guidance: { explanation: '同一句話' }, knownIssue: ' 同一句話 ' }));
        assert.equal(textsOf(wrap).filter(t => t.trim() === '同一句話').length, 1);
        assert(wrap.children[1].className.includes('lf-issue-cell__explain'));
    },
    noGuidanceLeavesNoEmptyNode() {
        const withKnown = issueCell(baseIssue({ guidance: null, knownIssue: '只有已知問題' }));
        assert.equal(withKnown.children[1].textContent, '只有已知問題');
        const plain = issueCell(baseIssue({ guidance: null }));
        assert.equal(plain.children[1].textContent, 'Security');
        const noExplain = issueCell(baseIssue({ guidance: { impact: '有影響' } }));
        assert.equal(noExplain.children[1].textContent, 'Security');
        assert.equal(noExplain.children.length, plain.children.length, '不得多出節點');
    },
    guidancePanelOmitsExplanationLabel() {
        assert.equal(guidancePanel({ guidance: { explanation: '只有說明' } }), null);
        const panel = guidancePanel({ guidance: { explanation: '說明文字', impact: '會被入侵', nextSteps: ['改密碼'] } });
        assert(panel, '有影響時應有面板');
        const all = [];
        const walk = n => { all.push(n.textContent); (n.children || []).forEach(walk); };
        walk(panel);
        assert(!panel.children.some(c => c.textContent === '說明'), '不得有「說明」標籤');
        assert(!all.includes('說明文字'));
        assert(panel.children.some(c => c.textContent === '影響'));
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
