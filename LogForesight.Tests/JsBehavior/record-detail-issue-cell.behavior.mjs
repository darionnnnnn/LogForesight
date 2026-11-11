/**
 * 風險日詳情「問題」欄的判斷層（issueCell）與列展開的佐證層（evidencePanel）——docs/archive/FEEDBACK-69-PLAN.md 批次B。
 * 以 extractFunction 切出 production 函式本體一起執行；只有 renderTable、severityCell、AI 與 modal 這類
 * 和本契約無關的外部相依用替身，collapsibleText、metaLine、button 都是 production 版本。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const readJs = (...parts) => fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', ...parts), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿/, '');
const recordDetail = readJs('pages', 'record-detail.js');
const uiSource = readJs('core', 'ui.js');

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
    return source.slice(start, source.indexOf('\n', start));
}

const dom = installDom();
globalThis.window = { addEventListener: () => {} };
const { disclosure, metaLine } = loadElements(dom);

// production 的 collapsibleText 與 button
const printSetup = uiSource.slice(uiSource.indexOf('const printableCollapsibles'), uiSource.indexOf('function hookCollapsiblePrint'));
const collapsibleText = new Function('COLLAPSE_LIMIT', 'disclosure', `${printSetup}
${extractFunction(uiSource, 'function prunePrintableCollapsibles')}
${extractFunction(uiSource, 'function hookCollapsiblePrint')}
${extractFunction(uiSource, 'export function collapsibleText').replace(/^export /, '')}
return collapsibleText;`)(300, disclosure);
const button = loadButton(dom);

const mark = (cls, text = '') => { const el = document.createElement('div'); el.className = cls; el.textContent = text; return el; };
// renderTable 替身：逐列逐欄呼叫 production 的 render，把回傳元素（或字串）放進 td
const renderTableStub = (wrap, { columns, rows }) => {
    for (const row of rows) {
        const tr = document.createElement('tr');
        for (const col of columns) {
            const td = document.createElement('td');
            const v = col.render(row);
            if (typeof v === 'string') td.textContent = v; else td.appendChild(v);
            tr.appendChild(td);
        }
        wrap.appendChild(tr);
    }
};

const ctx = { currentDetail: { detailPruned: false }, aiAvailable: false, modals: [] };
const names = ['issueCell', 'issueStateBadges', 'evidencePanel', 'guidancePanel', 'appendList', 'sourceEvidenceBlock', 'formatSourceUtc',
    'residualCredentialBlock', 'loginFailureDetailsTable', 'keyDetailsBlock', 'sampleMessagesTrigger', 'trendCell'];
const body = [
    extractLine(recordDetail, 'const TREND_WORDS'),
    ...names.map(n => extractFunction(recordDetail, `function ${n}(`))
].join('\n');
const fns = new Function('document', 'collapsibleText', 'metaLine', 'button', 'severityCell', 'formatNumber', 'renderTable',
    'renderAiInline', 'aiInterpretPanel', 'prtgTrendEvidenceBlock', 'showDetailModal', 'sampleMessagesBody', 'ctx',
    `const currentDetail = new Proxy({}, { get: (_, k) => ctx.currentDetail[k] });
     const aiAvailableRef = () => ctx.aiAvailable;
     ${body.replaceAll('aiAvailable ?', 'aiAvailableRef() ?')}
     return { ${names.join(', ')} };`)(
    document, collapsibleText, metaLine, button, () => mark('severity', '高'), n => String(n), renderTableStub,
    (el, t) => { el.textContent = t; }, () => mark('ai-interpret', 'AI'), () => mark('prtg-evidence', 'PRTG'),
    opts => ctx.modals.push(opts), () => mark('samples'), ctx);

const baseIssue = (extra = {}) => ({
    sourceEventLabel: 'Security 4625', logName: 'Security', severity: 'High', count: 3,
    firstSeen: '01:00', lastSeen: '02:00', ...extra
});
const all = n => { const out = []; const walk = x => { out.push(x); (x.children || []).forEach(walk); }; walk(n); return out; };
const hasClass = (n, cls) => all(n).some(x => (x.className || '').split(' ').includes(cls));
const allText = n => all(n).map(x => x.textContent || '').join('|');
const labelsOf = panel => panel.children.map(s => s.children[0].textContent);
const reset = () => { ctx.currentDetail = { detailPruned: false }; ctx.aiAvailable = false; ctx.modals.length = 0; };
const obs = n => Array.from({ length: n }, (_, i) => ({
    sourceKind: 'Prtg', resourceScope: 'Host', exactHostKey: `h${i}`, exactResourceKey: `r${i}`,
    eventTimeUtc: i === 0 ? null : '2026-01-01T00:00:00Z', nativeReference: i === 2 ? 'ref' : null
}));
const failure = (account, extra = {}) => ({ account, source: 'WS01', logonTypeText: '網路', reasonText: '密碼錯誤', count: 3, ...extra });
const clickOf = b => (b.listeners.click || []).forEach(h => h({ stopPropagation() { b._stopped = true; } }));
const LEGACY = 'TP-BKSVR02$ Account Domain: BRK Logon ID: 0x3E7 Group: Security ID: S-1-5-32-544 Group Name: Administrators Group Domain: Builtin Changed Attributes: SAM Account Name: - SID History: - Additional Information: Privileges: -';

const cases = {
    // ── 判斷層 ──
    judgmentLayerOrder() {
        reset();
        const { issueCell } = fns;
        const wrap = issueCell(baseIssue({ guidance: { explanation: '有人一直猜密碼' }, knownIssue: '已知問題 A' }));
        const [head, explain, known, meta] = wrap.children;
        assert(head.className.includes('lf-issue-cell__head'));
        assert.equal(head.children[0].textContent, '高', '嚴重度與標題同一行、在標題前');
        assert.equal(head.children[1].textContent, 'Security 4625');
        assert(explain.className.includes('lf-issue-cell__explain'));
        assert.equal(explain.textContent, '有人一直猜密碼');
        assert.equal(known.textContent, '已知問題 A');
        assert(meta.className.includes('lf-meta-line'));
        assert.deepEqual(meta.children.map(c => c.textContent), ['3 次', '01:00–02:00', 'Security']);
        assert(meta.children[1].title.includes('不是來源時間窗'), '時段的口徑放在 title');
        assert(!allText(wrap).includes('群組顯示時段 '), '判斷層不再出現「群組顯示時段」字樣');
    },
    identicalTextShownOnce() {
        reset();
        const wrap = fns.issueCell(baseIssue({ guidance: { explanation: '同一句話' }, knownIssue: ' 同一句話 ' }));
        assert.equal(wrap.children.map(c => c.textContent).filter(t => t.trim() === '同一句話').length, 1);
    },
    noGuidanceLeavesNoEmptyNode() {
        reset();
        const plain = fns.issueCell(baseIssue({ guidance: null }));
        assert.equal(plain.children.length, 2, '只有標題列與次要資訊行');
        const impactOnly = fns.issueCell(baseIssue({ guidance: { impact: '有影響' } }));
        assert.equal(impactOnly.children.length, plain.children.length, '沒有白話說明時不得多出節點');
        assert(fns.issueCell(baseIssue({ distinctMessageCount: 4 })).children[1].textContent.includes('4 種訊息'));
    },
    judgmentExcludesEvidence() {
        reset();
        const issue = baseIssue({
            keyDetails: '相關帳號(1個): alice', sampleMessages: ['m1', 'm2'],
            loginFailureDetails: [failure('alice')], sourceObservations: obs(3), prtgTrendEvidence: { ruleCode: 'x' },
            residualCredentialBasis: '依據文字', residualCredentialRetry: true
        });
        const wrap = fns.issueCell(issue);
        const text = allText(wrap);
        assert(!text.includes('密碼錯誤'), '登入失敗明細不在判斷層');
        assert(!text.includes('原生參照'), '來源觀測不在判斷層');
        assert(!text.includes('依據文字'), '殘留憑證依據不在判斷層（徽章在）');
        assert(text.includes('疑似殘留憑證重試'));
        assert(hasClass(wrap, 'lf-key-details'), '帳號摘要留在判斷層');
        const sample = all(wrap).find(n => n.tagName === 'BUTTON' && (n.textContent || '').includes('原始訊息'));
        assert(sample, '原始訊息按鈕留在判斷層（一次點擊）');
        assert.equal(sample.getAttribute('aria-haspopup'), 'dialog');
        assert(!sample.className.includes('btn-link'));
        clickOf(sample);
        assert(sample._stopped, '列內按鈕不得連帶觸發列展開');
        assert.equal(ctx.modals.length, 1);
    },
    prunedDayShowsHintNotDetails() {
        reset();
        ctx.currentDetail = { detailPruned: true };
        const wrap = fns.issueCell(baseIssue({ keyDetails: '相關帳號(1個): alice', sampleMessages: ['m'], residualCredentialBasis: 'x' }));
        const text = allText(wrap);
        assert(text.includes('已超過保留期並清除'));
        assert(!hasClass(wrap, 'lf-key-details'));
        assert(!text.includes('原始訊息'));
        assert(!text.includes('殘留憑證'));
    },
    // ── 帳號／來源摘要：邊界兩側 ──
    keyDetailsNone() {
        reset();
        assert(!hasClass(fns.issueCell(baseIssue()), 'lf-key-details'));
    },
    keyDetailsOneAccount() {
        reset();
        const block = fns.keyDetailsBlock('相關帳號(1個): alice');
        assert.equal(block.children.length, 1);
        const row = block.children[0];
        assert.equal(row.children[0].textContent, '相關帳號(1個)');
        assert(row.children[0].className.includes('lf-text-label'));
        assert.equal(row.children[1].textContent, 'alice');
        assert(!all(block).some(n => (n.className || '').includes('text-danger')), '帳號是佐證不是風險訊號，不上紅字');
    },
    keyDetailsAccountsAndIps() {
        reset();
        const block = fns.keyDetailsBlock('相關帳號(2個): a, b；來源IP(1個): 10.0.0.1');
        assert.deepEqual(block.children.map(r => r.children[0].textContent), ['相關帳號(2個)', '來源IP(1個)']);
        const ipOnly = fns.keyDetailsBlock('來源IP(1個): 10.0.0.1');
        assert.equal(ipOnly.children.length, 1);
        assert.equal(ipOnly.children[0].children[1].textContent, '10.0.0.1');
    },
    keyDetailsManyAccountsCollapsible() {
        reset();
        const accounts = Array.from({ length: 50 }, (_, i) => `account-${String(i).padStart(2, '0')}`).join(', ');
        const block = fns.keyDetailsBlock(`相關帳號(50個): ${accounts}`);
        const toggle = all(block).find(n => (n.className || '').includes('lf-collapsible__toggle'));
        assert(toggle, '超過門檻的帳號清單收合，有切換鈕');
        clickOf(toggle);
        assert(allText(block).includes('account-49'), '展開後看得到全部帳號');
    },
    keyDetailsLegacyWholeMessage() {
        reset();
        const labeled = fns.keyDetailsBlock(`相關帳號(1個): ${LEGACY}`);
        assert.equal(labeled.children[0].children[0].textContent, '相關帳號(1個)', '在第一個冒號切開，值內的冒號保留');
        assert(allText(labeled).includes('Privileges: -'));
        const raw = fns.keyDetailsBlock(LEGACY);
        assert(raw.children[0].className.includes('lf-text-data'), '切不出「標籤：值」形狀時整段原樣顯示');
        assert.equal(raw.children[0].textContent, LEGACY);
    },
    // ── 佐證層 ──
    guidancePanelOmitsExplanationLabel() {
        reset();
        assert.equal(fns.guidancePanel({ guidance: { explanation: '只有說明' } }), null);
        const panel = fns.guidancePanel({ guidance: { explanation: '說明文字', impact: '會被入侵', nextSteps: ['改密碼'] } });
        assert(panel);
        assert(!allText(panel).includes('說明文字'), '白話說明已在判斷層，佐證不重複');
    },
    evidencePanelAllSections() {
        reset();
        const panel = fns.evidencePanel(baseIssue({
            guidance: { impact: '會被入侵' }, loginFailureDetails: [failure('alice')],
            prtgTrendEvidence: { ruleCode: 'x' }, residualCredentialBasis: '依據', sourceObservations: obs(2)
        }));
        assert.deepEqual(labelsOf(panel), ['處置參考', '登入失敗明細', '數值趨勢', '殘留憑證判定', '來源觀測']);
        assert(panel.className.includes('lf-evidence'));
    },
    evidencePanelSingleSection() {
        reset();
        assert.deepEqual(labelsOf(fns.evidencePanel(baseIssue({ sourceObservations: obs(1) }))), ['來源觀測']);
        ctx.aiAvailable = true;
        assert.deepEqual(labelsOf(fns.evidencePanel(baseIssue())), ['AI 判讀', '來源觀測'], '沒有知識庫但 AI 可用時提供 AI 判讀');
    },
    evidencePanelStatesMissingSourceObservations() {
        // 面板有其他佐證但沒有來源觀測：要明講「未保存」（分得出「沒有」與「漏顯示」），截斷也要講
        reset();
        const panel = fns.evidencePanel(baseIssue({ loginFailureDetails: [failure('alice')], sourceObservationsTruncated: true }));
        assert.deepEqual(labelsOf(panel), ['登入失敗明細', '來源觀測']);
        assert(allText(panel).includes('未保存可核對的來源觀測'));
        assert(allText(panel).includes('清單已截斷'));
        // 有觀測時不出現這一句
        assert(!allText(fns.evidencePanel(baseIssue({ sourceObservations: obs(1) }))).includes('未保存'));
    },
    evidencePanelNoneReturnsNull() {
        reset();
        assert.equal(fns.evidencePanel(baseIssue()), null, '四區皆無時不可展開、不出現空面板');
        assert.equal(fns.evidencePanel(baseIssue({ guidance: { explanation: '只有說明' }, sourceObservations: [] })), null);
    },
    evidencePanelPrunedKeepsGuidanceOnly() {
        reset();
        ctx.currentDetail = { detailPruned: true };
        const issue = baseIssue({ guidance: { impact: '會被入侵' }, loginFailureDetails: [failure('a')], sourceObservations: obs(2) });
        assert.deepEqual(labelsOf(fns.evidencePanel(issue)), ['處置參考'], '詳情已清除時只剩知識庫的處置參考');
        assert.equal(fns.evidencePanel(baseIssue({ loginFailureDetails: [failure('a')] })), null);
    },
    sourceEvidenceList() {
        reset();
        const block = fns.sourceEvidenceBlock({ sourceObservations: obs(3) });
        const summary = block.children[0].children.map(c => c.textContent);
        assert.deepEqual(summary, ['3 筆', '2 筆有 UTC 時間', '1 筆有原生參照', '群組顯示時段不是來源時間窗']);
        assert.equal(block.children[1].children.length, 3);
        assert(!all(block).some(n => n.tagName === 'BUTTON' || n.tagName === 'A'), '佐證層內不再有切換鈕，也不產生連結');
    },
    sourceEvidenceTruncatedMarker() {
        reset();
        assert(allText(fns.sourceEvidenceBlock({ sourceObservations: obs(1), sourceObservationsTruncated: true })).includes('清單已截斷'));
    },
    sourceEvidenceZeroReturnsNull() {
        reset();
        assert.equal(fns.sourceEvidenceBlock({ sourceObservations: [], sourceObservationsTruncated: true }), null);
        assert.equal(fns.sourceEvidenceBlock({}), null);
    },
    loginFailureLongAccountCollapses() {
        reset();
        const long = fns.loginFailureDetailsTable({ loginFailureDetails: [failure('a'.repeat(200))] });
        assert(hasClass(long, 'lf-collapsible__toggle'), '200 字帳號應被收合');
        const short = fns.loginFailureDetailsTable({ loginFailureDetails: [failure('a'.repeat(10), { isComputerAccount: true })] });
        assert(!hasClass(short, 'lf-collapsible__toggle'), '10 字帳號不得收合');
        assert(allText(short).includes('（電腦帳號）'));
    },
    loginFailureLongSourceAndReasonCollapse() {
        reset();
        const wrap = fns.loginFailureDetailsTable({ loginFailureDetails: [failure('u', { source: 's'.repeat(100), reasonText: 'r'.repeat(100) })] });
        assert.equal(all(wrap).filter(n => (n.className || '').includes('lf-collapsible__toggle')).length, 2);
    },
    // ── 趨勢欄 ──
    trendCellSplitsWordAndNumbers() {
        reset();
        const cell = fns.trendCell('頻率上升\n前一日 3 次\n歷史基準 1 次');
        assert.equal(cell.children[0].textContent, '頻率上升');
        assert(cell.children[0].className.includes('lf-trend__word--up'));
        assert.deepEqual(cell.children[1].children.map(c => c.textContent), ['前一日 3 次', '歷史基準 1 次']);
        const steady = fns.trendCell('重複發生\n前一日 3 次');
        assert(!steady.children[0].className.includes('--up'));
        const numbersOnly = fns.trendCell('前一日 3 次');
        assert.equal(numbersOnly.children.length, 1, '沒有狀態詞時整段都是數字');
        assert(numbersOnly.children[0].className.includes('lf-meta-line'));
        assert.equal(fns.trendCell('').textContent, '');
        assert.equal(fns.trendCell('首次出現').children.length, 1, '只有狀態詞時不留空的數字行');
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
