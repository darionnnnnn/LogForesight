import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const read = file => fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', file), 'utf8').replaceAll('\r\n', '\n');
const source = read('prtg-admin.js');

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

function extractConst(name) {
    const start = source.indexOf(`const ${name} = {`);
    assert(start >= 0, `找不到 production 常數：${name}`);
    const end = source.indexOf('\n};', start);
    return source.slice(start, end + 3);
}

const hasCapability = (user, capability) => Array.isArray(user?.capabilities) && user.capabilities.includes(capability);

function makeArchiveHarness({ confirm = true, postFails = false } = {}) {
    const dom = installDom();
    const host = dom.newElement('div');
    dom.byId.set('prtg-mirror-snapshot-archive', host);
    const log = { posts: [], toasts: [], confirms: [], refreshes: 0, diagnostics: 0 };
    const deps = {
        document,
        hasCapability,
        formatDateTime: v => String(v),
        formatNumber: v => String(v),
        makeButton: (text, { onClick } = {}) => {
            const btn = dom.newElement('button');
            btn.textContent = text;
            if (onClick) btn.addEventListener('click', onClick);
            return btn;
        },
        confirmAction: async options => { log.confirms.push(options); return confirm; },
        api: { post: async (url, body) => { log.posts.push([url, body]); if (postFails) throw new Error('409'); return { rows: 3 }; } },
        toast: (message, kind) => log.toasts.push([message, kind]),
        refreshPrtgMirror: () => { log.refreshes += 1; },
        loadSnapshotDiagnostics: () => { log.diagnostics += 1; }
    };
    const body = ['canShowSnapshotArchive', 'snapshotArchiveConfirmMessage', 'renderSnapshotArchive'].map(n => extractFunction(`function ${n}(`)).join('\n');
    const factory = new Function(...Object.keys(deps),
        `${extractConst('SNAPSHOT_STOP_LABELS')}\n${body}\nreturn { renderSnapshotArchive, snapshotArchiveConfirmMessage, canShowSnapshotArchive };`);
    return { fns: factory(...Object.values(deps)), host, log };
}

const JOURNAL = {
    stopped: true, stopCode: 'database-mismatch', canArchive: true, rows: 3, rowsInReplayWindow: 2,
    earliest: '2026-11-09T08:00:00', latest: '2026-11-10T09:00:00', archiveLocation: 'D:/data/pending/prtg-snapshot-archive/20261110-120000-database-mismatch'
};
const FULL_USER = { capabilities: ['Maintain', 'ViewAll'] };

function buttons(host) {
    return host.children.filter(c => c.tagName === 'BUTTON');
}

async function archiveButtonShownForMaintainAndViewAll() {
    const h = makeArchiveHarness();
    h.fns.renderSnapshotArchive(JOURNAL, FULL_USER);
    const found = buttons(h.host);
    assert.equal(found.length, 1);
    assert.equal(found[0].textContent, '封存舊待寫資料並重新開始');
}

async function archiveButtonHiddenWithoutEnoughCapability() {
    for (const user of [{ capabilities: ['Maintain'] }, { capabilities: ['ViewAll'] }, { capabilities: [] }, null]) {
        const h = makeArchiveHarness();
        h.fns.renderSnapshotArchive(JOURNAL, user);
        assert.equal(buttons(h.host).length, 0, JSON.stringify(user));
        // 停止原因的說明仍然顯示
        assert.match(h.host.textContent, /快照已停止/);
    }
}

async function cannotArchiveShowsReasonOnly() {
    const h = makeArchiveHarness();
    h.fns.renderSnapshotArchive({ stopped: true, stopCode: 'unavailable', canArchive: false, cannotArchiveReason: '不能由畫面封存' }, FULL_USER);
    assert.equal(buttons(h.host).length, 0);
    assert.match(h.host.textContent, /不能由畫面封存/);
}

async function notStoppedRendersNothing() {
    const h = makeArchiveHarness();
    h.fns.renderSnapshotArchive({ stopped: false }, FULL_USER);
    assert.equal(h.host.children.length, 0);
    h.fns.renderSnapshotArchive(null, FULL_USER);
    assert.equal(h.host.children.length, 0);
}

async function confirmDialogStatesRowsRangeAndLocation() {
    const h = makeArchiveHarness();
    const message = h.fns.snapshotArchiveConfirmMessage(JOURNAL);
    assert.match(message, /3 筆/);
    assert.match(message, /2026-11-09T08:00:00 至 2026-11-10T09:00:00/);
    assert.match(message, /2 筆仍在重播期限內/);
    assert.match(message, /20261110-120000-database-mismatch/);
    assert.match(message, /不會刪除/);
}

async function clickConfirmedPostsArchiveAndRefreshes() {
    const h = makeArchiveHarness();
    h.fns.renderSnapshotArchive(JOURNAL, FULL_USER);
    await buttons(h.host)[0].listeners.click[0]();
    assert.equal(h.log.confirms.length, 1);
    assert.deepEqual(h.log.posts.map(p => p[0]), ['/api/prtg-snapshot-diagnostics/archive-journal']);
    assert.equal(h.log.refreshes, 1);
    assert.equal(h.log.diagnostics, 1);
    assert.match(h.log.toasts[0][0], /已封存 3 筆/);
}

async function clickDeclinedDoesNothing() {
    const h = makeArchiveHarness({ confirm: false });
    h.fns.renderSnapshotArchive(JOURNAL, FULL_USER);
    await buttons(h.host)[0].listeners.click[0]();
    assert.equal(h.log.posts.length, 0);
    assert.equal(h.log.refreshes, 0);
}

async function stoppedStateLabelAndNextStep() {
    const labels = new Function(`${extractConst('snapshotStateLabels')}\n${extractConst('snapshotNextSteps')}\nreturn { snapshotStateLabels, snapshotNextSteps };`)();
    assert.equal(labels.snapshotStateLabels.stopped, '已停止：待寫檔無法使用');
    assert.match(labels.snapshotNextSteps.stopped, /PRTG 維護 > 鏡像狀態封存舊待寫資料/);
    // 系統管理設定頁的同一份對照
    const settings = read('settings.js');
    assert.match(settings, /stopped: '已停止：待寫檔無法使用'/);
    // 目標數未知（-1）顯示「未知」而不是 0 或 -1
    assert.match(source, /row\.targets < 0 \? '未知'/);
    assert.match(settings, /hour\.targets < 0 \? '未知'/);
}

function makeChecklistHarness(alert) {
    const dom = installDom();
    const summary = dom.newElement('div');
    const steps = dom.newElement('ul');
    dom.byId.set('prtg-setup-summary', summary);
    dom.byId.set('prtg-setup-steps', steps);
    const setupState = {
        settings: { prtgUrl: 'https://p', prtgCredentialState: 'ok', prtgEnabled: true },
        settingsLoading: false, probe: true, sync: { isRunning: false, lastSuccess: true, lastCompletedAt: 'x' },
        rules: true, schedule: true, snapshotAlert: alert
    };
    const deps = { document, setupState, appUrl: v => v, renderAcceptance: () => { } };
    const factory = new Function(...Object.keys(deps), `${extractFunction('function renderSetupSummary(')}\nreturn renderSetupSummary;`);
    factory(...Object.values(deps))();
    return { summary, steps };
}

async function setupChecklistHasSnapshotItem() {
    const stopped = makeChecklistHarness('stopped');
    const item = stopped.steps.children.find(li => li.textContent.includes('數值快照'));
    assert.ok(item, '啟用準備缺少「數值快照」項目');
    assert.match(item.textContent, /^待確認：數值快照。/);
    assert.match(item.textContent, /到鏡像狀態查看/);
    const slow = makeChecklistHarness('no-recent-success');
    assert.match(slow.steps.children.find(li => li.textContent.includes('數值快照')).textContent, /^待確認/);
    const none = makeChecklistHarness('none');
    assert.match(none.steps.children.find(li => li.textContent.includes('數值快照')).textContent, /^已確認：數值快照。/);
    assert.doesNotMatch(none.steps.children.find(li => li.textContent.includes('數值快照')).textContent, /到鏡像狀態查看/);
}

const cases = {
    archiveButtonShownForMaintainAndViewAll,
    archiveButtonHiddenWithoutEnoughCapability,
    cannotArchiveShowsReasonOnly,
    notStoppedRendersNothing,
    confirmDialogStatesRowsRangeAndLocation,
    clickConfirmedPostsArchiveAndRefreshes,
    clickDeclinedDoesNothing,
    stoppedStateLabelAndNextStep,
    setupChecklistHasSnapshotItem
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
try { await cases[name](); console.log(`PASS ${name}`); }
catch (error) { console.error(`FAIL ${name}: ${error.stack || error.message}`); process.exitCode = 1; }
