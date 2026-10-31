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
function makeHarness({ failures = {}, revisions = ['r1', 'r1'], riskReview, riskPending } = {}) {
    const dom = installDom();
    const riskEl = dom.newElement('div');
    riskEl.id = 'prtg-risk-review';
    dom.byId.set('prtg-risk-review', riskEl);
    const log = { rendered: [], errors: [], getCalls: [] };
    let revisionIndex = 0;
    const api = {
        get: async (url, options) => {
            log.getCalls.push({ url, options });
            if (url.endsWith('/prtg-scope-revision')) return revisions[Math.min(revisionIndex++, revisions.length - 1)];
            for (const key of Object.keys(failures)) if (url.endsWith(key)) throw new Error(failures[key]);
            if (url.endsWith('/prtg-mirror')) return MIRROR;
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
        renderPrtgMirror: data => log.rendered.push(['mirror', data]),
        renderManualMaps: data => log.rendered.push(['manual', data]),
        renderIpExcludes: data => log.rendered.push(['excludes', data]),
        renderListError: (containerId, label, message) => log.errors.push({ containerId, label, message }),
        refreshPrtgMirror: () => { }
    };
    const names = ['loadScopeSnapshot', 'renderScopeSnapshot', 'refreshRiskReviewPanel'];
    const bodies = names.map(n => {
        const text = extractFunction(`function ${n}(`);
        return source.includes(`async function ${n}(`) ? `async ${text}` : text;
    }).join('\n');
    const factory = new Function(...Object.keys(deps),
        `let riskReviewPage = 1;\n${bodies}\nreturn { loadScopeSnapshot, renderScopeSnapshot, refreshRiskReviewPanel };`);
    const fns = factory(...Object.values(deps));
    return { fns, log, riskEl };
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
    assert.equal(h.log.errors[0].containerId, 'prtg-mirror-stats');
    assert.equal(h.log.errors[0].label, ' PRTG 鏡像');
    assert.match(h.log.errors[0].message, /鏡像壞了/);
    assert.deepEqual(h.log.rendered.map(r => r[0]).sort(), ['excludes', 'manual']);
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
        ['prtg-ip-excludes-table', 'prtg-mirror-manual-maps-table', 'prtg-mirror-stats']);
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

const cases = {
    manualMapRejectOnlyManualMapAreaShowsError,
    mirrorRejectOnlyMirrorAreaShowsError,
    ipExcludesRejectOnlyExcludesAreaShowsError,
    revisionMismatchShowsSameMessageInAllThreeAreas,
    allSucceedRendersThreeAreasAndStoresRevision,
    riskReviewPendingRequestIsSilent
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
try { await cases[name](); console.log(`PASS ${name}`); }
catch (error) { console.error(`FAIL ${name}: ${error.stack || error.message}`); process.exitCode = 1; }
