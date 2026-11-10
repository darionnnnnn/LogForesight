import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'prtg-admin.js'), 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

const MIRROR = { mapped: 1 };
const MANUAL = [{ id: 'm1' }];
const EXCLUDES = [{ id: 'e1' }];

// 以可控的 api 替身執行正式的 loadScopeSnapshot／renderScopeSnapshot／refreshRiskReviewPanel
// rounds（選用）：以「每趟讀快照」為單位控制時序——gate[n] 為第 n 趟開頭要等的 promise，fail[n] 為第 n 趟開頭擲例外；
// 啟用時每趟的 revision 為 r<n>、鏡像資料為 { round: n }，方便斷言畫面是哪一趟的結果。
function makeHarness({ failures = {}, revisions = ['r1', 'r1'], riskReview, riskPending, rounds } = {}) {
    const dom = installDom();
    const riskEl = dom.newElement('div');
    riskEl.id = 'prtg-risk-review';
    dom.byId.set('prtg-risk-review', riskEl);
    const log = { rendered: [], errors: [], getCalls: [] };
    let revisionIndex = 0;
    let revisionCalls = 0;
    const currentRound = () => Math.floor((revisionCalls - 1) / 2) + 1;
    const api = {
        get: async (url, options) => {
            log.getCalls.push({ url, options });
            if (url.endsWith('/prtg-scope-revision')) {
                if (rounds) {
                    const isBefore = revisionCalls % 2 === 0;
                    revisionCalls += 1;
                    const round = currentRound();
                    if (isBefore) {
                        log.roundsStarted = round;
                        if (rounds.gate?.[round]) await rounds.gate[round];
                        if (rounds.fail?.[round]) { revisionCalls += 1; throw new Error(`第 ${round} 趟讀快照失敗`); }
                    }
                    return `r${round}`;
                }
                return revisions[Math.min(revisionIndex++, revisions.length - 1)];
            }
            for (const key of Object.keys(failures)) if (url.endsWith(key)) throw new Error(failures[key]);
            if (url.endsWith('/prtg-mirror')) return rounds ? { round: currentRound() } : MIRROR;
            if (url.endsWith('/prtg-manual-map')) return MANUAL;
            if (url.endsWith('/prtg-ip-excludes')) return EXCLUDES;
            if (url.startsWith('/api/prtg/risk-review/pending')) {
                if (riskPending) return riskPending(url, options);
                return { items: [], total: 0 };
            }
            if (url.startsWith('/api/prtg/risk-review')) {
                if (riskReview) return riskReview(url, options);
                return { uncheckedRecords: 0, pendingReviewRecords: 1, revisedRecords: 0 };
            }
            throw new Error(`未預期的請求：${url}`);
        },
        post: async () => ({})
    };
    const deps = {
        api,
        document,
        formatNumber: v => String(v),
        appUrl: v => v,
        // ui.js button() 的替身：只保留測試需要的 click 與 disabled
        makeButton: (text, { onClick } = {}) => {
            const btn = dom.newElement('button');
            btn.textContent = text;
            if (onClick) btn.addEventListener('click', onClick);
            return btn;
        },
        renderPrtgMirror: data => log.rendered.push(['mirror', data]),
        renderManualMaps: data => log.rendered.push(['manual', data]),
        renderIpExcludes: data => log.rendered.push(['excludes', data]),
        renderListError: (containerId, label, message) => log.errors.push({ containerId, label, message }),
        // 與 core/async-guards.js 的 createSeqGuard 同形：頁面模組層以它守住重疊的待重評面板重讀
        createSeqGuard: () => { let current = 0; return { next: () => ++current, isCurrent: seq => seq === current }; }
    };
    const names = ['loadScopeSnapshot', 'renderScopeSnapshot', 'refreshRiskReviewPanel', 'refreshPrtgMirror', 'runScopeRefresh', 'refreshPrtgMirrorCore'];
    const bodies = names.map(n => {
        const text = extractFunction(`function ${n}(`);
        return source.includes(`async function ${n}(`) ? `async ${text}` : text;
    }).join('\n');
    const factory = new Function(...Object.keys(deps), 'log',
        `let riskReviewPage = 1;\nconst riskReviewGuard = createSeqGuard();\nlet scopeRevision = null;\nlet scopeRefresh = null;\nlet scopeRefreshDirty = false;\nconst bindRiskReviewRetry = () => { };\nconst refreshScopePurge = async () => { log.purgeCalls += 1; };\n${bodies}\nreturn { loadScopeSnapshot, renderScopeSnapshot, refreshRiskReviewPanel, refreshPrtgMirror, getScopeRevision: () => scopeRevision };`);
    log.purgeCalls = 0;
    log.roundsStarted = 0;
    const fns = factory(...Object.values(deps), log);
    return { fns, log, riskEl, dom };
}

async function manualMapRejectOnlyManualMapAreaShowsError() {
    const h = makeHarness({ failures: { '/prtg-manual-map': '人工對應壞了' } });
    const snapshot = await h.fns.loadScopeSnapshot();
    h.fns.renderScopeSnapshot(snapshot);
    assert.equal(h.log.errors.length, 1, JSON.stringify(h.log.errors));
    assert.equal(h.log.errors[0].containerId, 'prtg-mirror-manual-maps-table');
    assert.equal(h.log.errors[0].label, '人工對應');
    assert.match(h.log.errors[0].message, /人工對應壞了/);
    const kinds = h.log.rendered.map(r => r[0]).sort();
    assert.deepEqual(kinds, ['excludes', 'mirror']);
}

async function mirrorRejectOnlyMirrorAreaShowsError() {
    const h = makeHarness({ failures: { '/prtg-mirror': '鏡像壞了' } });
    h.fns.renderScopeSnapshot(await h.fns.loadScopeSnapshot());
    assert.equal(h.log.errors.length, 1, JSON.stringify(h.log.errors));
    assert.equal(h.log.errors[0].containerId, 'prtg-mirror-stats-error');
    assert.equal(h.log.errors[0].label, ' PRTG 鏡像');
    assert.match(h.log.errors[0].message, /鏡像壞了/);
    assert.deepEqual(h.log.rendered.map(r => r[0]).sort(), ['excludes', 'manual']);
}

// 部分失敗仍帶 revision：成功的人工對應／IP 排除區要能以正確版本送出異動
async function partialFailureStillStoresRevision() {
    const h = makeHarness({ failures: { '/prtg-mirror': '鏡像壞了' } });
    const snapshot = await h.fns.loadScopeSnapshot();
    assert.equal(snapshot.revision, 'r1');
}

// 鏡像重試成功時清掉錯誤：錯誤不得寫進統計卡本身（卡內節點被蓋掉就填不回數字）
async function mirrorSuccessClearsErrorSlotAndKeepsStatsNodes() {
    const h = makeHarness();
    const errorSlot = document.createElement('div');
    errorSlot.id = 'prtg-mirror-stats-error';
    errorSlot.append(document.createElement('span'));
    h.dom.byId.set(errorSlot.id, errorSlot);
    h.fns.renderScopeSnapshot(await h.fns.loadScopeSnapshot());
    assert.equal(errorSlot.children.length, 0, '成功後應清掉鏡像錯誤');
    assert(!source.includes("renderListError('prtg-mirror-stats',"), '錯誤不得寫進統計卡容器');
}

async function ipExcludesRejectOnlyExcludesAreaShowsError() {
    const h = makeHarness({ failures: { '/prtg-ip-excludes': '排除壞了' } });
    h.fns.renderScopeSnapshot(await h.fns.loadScopeSnapshot());
    assert.equal(h.log.errors.length, 1);
    assert.equal(h.log.errors[0].containerId, 'prtg-ip-excludes-table');
    assert.deepEqual(h.log.rendered.map(r => r[0]).sort(), ['manual', 'mirror']);
}

async function revisionMismatchShowsSameMessageInAllThreeAreas() {
    const h = makeHarness({ revisions: ['r1', 'r2'] });
    const snapshot = await h.fns.loadScopeSnapshot();
    h.fns.renderScopeSnapshot(snapshot);
    assert.equal(h.log.rendered.length, 0);
    assert.deepEqual(h.log.errors.map(e => e.containerId).sort(),
        ['prtg-ip-excludes-table', 'prtg-mirror-manual-maps-table', 'prtg-mirror-stats-error']);
    for (const e of h.log.errors) assert.equal(e.message, '載入期間 PRTG 範圍已被修改，請重新整理');
    assert.equal(snapshot.revision, undefined, '不一致時不得回傳 revision');
}

async function allSucceedRendersThreeAreasAndStoresRevision() {
    const h = makeHarness();
    const snapshot = await h.fns.loadScopeSnapshot();
    h.fns.renderScopeSnapshot(snapshot);
    assert.equal(snapshot.revision, 'r1');
    assert.equal(h.log.errors.length, 0);
    assert.deepEqual(h.log.rendered.map(r => r[0]), ['mirror', 'manual', 'excludes']);
}

async function riskReviewPendingRequestIsSilent() {
    const h = makeHarness();
    await h.fns.refreshRiskReviewPanel();
    const pending = h.log.getCalls.find(c => c.url.startsWith('/api/prtg/risk-review/pending'));
    assert(pending, '應該呼叫 risk-review/pending');
    assert.equal(pending.options?.silent, true);
}

async function overlappingRiskReviewCallsRenderPendingListOnce() {
    // 第一趟的 pending 請求被卡住，第二趟（翻頁）先回來；第一趟回來後不得再把清單接到第二趟的結果後面
    let releaseFirst;
    let pendingCalls = 0;
    const h = makeHarness({
        riskPending: (url) => {
            pendingCalls += 1;
            if (pendingCalls === 1) return new Promise(resolve => { releaseFirst = () => resolve({ items: [{ hostId: 1, hostName: 'A', recordDate: '2026-01-01', riskLevel: 'high', detailPruned: false }], total: 60 }); });
            return { items: [{ hostId: 2, hostName: 'B', recordDate: '2026-01-02', riskLevel: 'high', detailPruned: false }], total: 60 };
        }
    });
    const first = h.fns.refreshRiskReviewPanel(1);
    await new Promise(r => setTimeout(r, 0));
    await h.fns.refreshRiskReviewPanel(2);
    releaseFirst();
    await first;
    const lists = h.riskEl.children.filter(c => c.tagName === 'UL');
    assert.equal(lists.length, 1, `清單應只有一份，實際 ${lists.length}`);
    assert.equal(lists[0].children[0].children[0].textContent.startsWith('B'), true, '留下的應是最後一趟（第 2 頁）的結果');
}

// 寫入端剛完成時，前一趟（寫入前發出）還在飛：補跑恰一趟，且前一趟的舊快照不得渲染、不得寫 scopeRevision
async function rewriteDuringInFlightTriggersOneRerun() {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const h = makeHarness({ rounds: { gate: { 1: gate } } });
    const first = h.fns.refreshPrtgMirror();
    await new Promise(r => setTimeout(r, 0));
    const second = h.fns.refreshPrtgMirror();
    release();
    await Promise.all([first, second]);
    assert.equal(h.log.roundsStarted, 2, '應恰兩輪讀快照');
    assert.equal(h.log.getCalls.filter(c => c.url.endsWith('/prtg-mirror')).length, 2);
    const mirrors = h.log.rendered.filter(r => r[0] === 'mirror');
    assert.equal(mirrors.length, 1, `只應渲染補跑那趟：${JSON.stringify(h.log.rendered)}`);
    assert.equal(mirrors[0][1].round, 2, '畫面應為第二輪資料');
    assert.equal(h.fns.getScopeRevision(), 'r2');
    assert.equal(h.log.purgeCalls, 1, '被標髒那趟不得清除預覽');
}

async function threeCallsDuringInFlightStillTwoRounds() {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const h = makeHarness({ rounds: { gate: { 1: gate } } });
    const first = h.fns.refreshPrtgMirror();
    await new Promise(r => setTimeout(r, 0));
    const second = h.fns.refreshPrtgMirror();
    const third = h.fns.refreshPrtgMirror();
    release();
    await Promise.all([first, second, third]);
    assert.equal(h.log.roundsStarted, 2, '連叫三次仍只兩輪');
    assert.equal(h.log.rendered.filter(r => r[0] === 'mirror').length, 1);
    assert.equal(h.fns.getScopeRevision(), 'r2');
}

// 補跑那趟進行中又有寫入完成：補跑讀到的也可能是寫入前的快照，必須再跑第三趟，只渲染第三趟
async function callDuringRerunTriggersThirdRound() {
    let release1, release2;
    const gate1 = new Promise(resolve => { release1 = resolve; });
    const gate2 = new Promise(resolve => { release2 = resolve; });
    const h = makeHarness({ rounds: { gate: { 1: gate1, 2: gate2 } } });
    const first = h.fns.refreshPrtgMirror();
    await new Promise(r => setTimeout(r, 0));
    const second = h.fns.refreshPrtgMirror();
    release1();
    for (let i = 0; i < 50 && h.log.roundsStarted < 2; i++) await new Promise(r => setTimeout(r, 0));
    assert.equal(h.log.roundsStarted, 2, '第二趟應已開始');
    const third = h.fns.refreshPrtgMirror();
    release2();
    await Promise.all([first, second, third]);
    assert.equal(h.log.roundsStarted, 3, '補跑期間的呼叫應再跑一趟');
    const mirrors = h.log.rendered.filter(r => r[0] === 'mirror');
    assert.equal(mirrors.length, 1, `只應渲染最後一趟：${JSON.stringify(h.log.rendered)}`);
    assert.equal(mirrors[0][1].round, 3);
    assert.equal(h.fns.getScopeRevision(), 'r3');
}

async function inFlightFailureStillReruns() {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const h = makeHarness({ rounds: { gate: { 1: gate }, fail: { 1: true } } });
    const first = h.fns.refreshPrtgMirror();
    await new Promise(r => setTimeout(r, 0));
    const second = h.fns.refreshPrtgMirror();
    release();
    await Promise.all([first, second]);
    assert.equal(h.log.roundsStarted, 2, '第一趟失敗仍要補跑');
    const mirrors = h.log.rendered.filter(r => r[0] === 'mirror');
    assert.equal(mirrors.length, 1);
    assert.equal(mirrors[0][1].round, 2);
    assert.equal(h.log.errors.length, 0, '第一趟的失敗訊息不得渲染');
    assert.equal(h.fns.getScopeRevision(), 'r2');
}

// 沒有並發時只跑一趟
async function noConcurrencyRunsOnce() {
    const h = makeHarness({ rounds: {} });
    await h.fns.refreshPrtgMirror();
    assert.equal(h.log.roundsStarted, 1);
    assert.equal(h.log.rendered.filter(r => r[0] === 'mirror')[0][1].round, 1);
    assert.equal(h.fns.getScopeRevision(), 'r1');
}

const cases = {
    rewriteDuringInFlightTriggersOneRerun,
    threeCallsDuringInFlightStillTwoRounds,
    callDuringRerunTriggersThirdRound,
    inFlightFailureStillReruns,
    noConcurrencyRunsOnce,
    overlappingRiskReviewCallsRenderPendingListOnce,
    manualMapRejectOnlyManualMapAreaShowsError,
    mirrorRejectOnlyMirrorAreaShowsError,
    ipExcludesRejectOnlyExcludesAreaShowsError,
    revisionMismatchShowsSameMessageInAllThreeAreas,
    partialFailureStillStoresRevision,
    mirrorSuccessClearsErrorSlotAndKeepsStatsNodes,
    allSucceedRendersThreeAreasAndStoresRevision,
    riskReviewPendingRequestIsSilent
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
try { await cases[name](); console.log(`PASS ${name}`); }
catch (error) { console.error(`FAIL ${name}: ${error.stack || error.message}`); process.exitCode = 1; }
