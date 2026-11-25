/**
 * 風險日詳情「顯示範圍」（H10）：三個互斥且字面相符的選項——待處理／全部（含已完成）／只看已完成。
 * 切出 production 的 SCOPE_OPTIONS、issueBucket、issueRank 等一起執行；計數與實際列數用同一個判斷，不是各算一份。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'record-detail.js'), 'utf8')
    .replaceAll('\r\n', '\n');

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
function extractBlock(startMarker, endMarker) {
    const start = source.indexOf(startMarker);
    assert(start >= 0, `找不到：${startMarker}`);
    return source.slice(start, source.indexOf(endMarker, start) + endMarker.length);
}

const closed = ['resolved', 'wont_fix', 'false_positive', 'known_noise'];
const build = new Function('statusesWhere', 'currentUserId', `
${extractBlock('const SCOPE_OPTIONS = [', '];')}
const CLOSED_STATUSES = new Set(statusesWhere(() => true));
${extractFunction('function issueBucket')}
${extractFunction('function isUnresolvedIssue')}
${extractFunction('function isInProgressIssue')}
${extractFunction('function issueRank')}
return { SCOPE_OPTIONS, issueBucket, issueRank };`);
const env = build(() => closed, 7);

const issue = (key, extra) => ({ issueKey: key, handlingStatus: '', ...extra });
const issues = [
    issue('p1', {}),                                                    // pending（沒標記、需處理）
    issue('p2', { handlingStatus: 'open' }),                            // pending
    issue('m1', { handlingStatus: 'in_progress' }),                     // mine
    issue('m2', { caseHandlerId: 7 }),                                  // mine（我的案件）
    issue('o1', { caseHandlerId: 9 }),                                  // others
    issue('d1', { handlingStatus: 'resolved' }),                        // done
    issue('d2', { isDefaultUnhandled: true }),                          // done（預設不處理）
    issue('d3', { isAutoNoise: true })                                  // done（自動雜訊）
];

const inScope = (option, list) => list.filter(i => option.buckets.includes(env.issueBucket(i)));
const byValue = value => env.SCOPE_OPTIONS.find(o => o.value === value);

const cases = {
    // 三個選項、字面與內容一致
    optionsAreThree() {
        assert.deepEqual(env.SCOPE_OPTIONS.map(o => o.label), ['待處理', '全部（含已完成）', '只看已完成']);
    },
    // 計數＝實際列數；待處理＋只看已完成＋他人處理中＝全部（互斥且不漏）
    countsEqualRows() {
        const pending = inScope(byValue('pending'), issues).map(i => i.issueKey);
        const all = inScope(byValue('all'), issues).map(i => i.issueKey);
        const done = inScope(byValue('done-only'), issues).map(i => i.issueKey);
        assert.deepEqual(pending, ['p1', 'p2', 'm1', 'm2']);
        assert.deepEqual(done, ['d1', 'd2', 'd3']);
        assert.equal(all.length, issues.length, '全部＝所有問題，包含已完成與他人處理中');
        assert.deepEqual([...pending, 'o1', ...done].sort(), [...all].sort());
        // 沒有任何一列同時屬於「待處理」與「只看已完成」
        assert.equal(pending.filter(k => done.includes(k)).length, 0);
    },
    // 邊界：沒有已完成時「全部」與「待處理」只差他人處理中；沒有他人處理中時兩者相同
    allEqualsPendingWhenNothingElse() {
        const only = issues.filter(i => ['p1', 'p2', 'm1', 'm2'].includes(i.issueKey));
        assert.equal(inScope(byValue('all'), only).length, inScope(byValue('pending'), only).length);
        assert.equal(inScope(byValue('done-only'), only).length, 0);
    },
    // 「全部」的排序層級：未處理 0、處理中 1、已有結論 2，已完成排在後面
    allSortsUnresolvedFirst() {
        const all = inScope(byValue('all'), issues).slice().sort((a, b) => env.issueRank(a) - env.issueRank(b));
        const ranks = all.map(i => env.issueRank(i));
        assert.deepEqual(ranks, [...ranks].sort((a, b) => a - b));
        assert.equal(env.issueRank(issues.find(i => i.issueKey === 'p1')), 0);
        assert.equal(env.issueRank(issues.find(i => i.issueKey === 'm1')), 1);
        assert.equal(env.issueRank(issues.find(i => i.issueKey === 'd1')), 2);
    },
    // 不再有「已處理／已有結論」收合區（選了卻看不到的地方）
    noCollapsedRestSection() {
        assert(!source.includes('collapsedRestSection'), '收合區已移除');
        assert(!source.includes('collapseDone'), 'collapseDone 已移除');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
try {
    await cases[name]();
    console.log(`PASS ${name}`);
} catch (error) {
    console.error(`FAIL ${name}: ${error.stack || error.message}`);
    process.exitCode = 1;
}
