// Execute the actual Runs page render/click functions against a small DOM/API boundary.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourcePath = process.argv[2] || path.join(root, 'LogForesight.Web/wwwroot/js/pages/runs.js');
const source = fs.readFileSync(sourcePath, 'utf8');
const stateStart = source.indexOf('let prtgBackfillPollTimer = null;');
const renderStart = source.indexOf('function renderPrtgBackfillStatus(status) {', stateStart);
const refreshStart = source.indexOf('async function refreshPrtgBackfillStatus()', renderStart);
const bindStart = source.indexOf('function bindPrtgBackfill()', refreshStart);
const bindEnd = source.indexOf('// 三頁籤', bindStart);
assert.ok(stateStart >= 0 && renderStart > stateStart && refreshStart > renderStart && bindStart > refreshStart && bindEnd > bindStart);

let passed = 0;
function fixture() {
    const nodes = new Map();
    const requests = [];
    let click;
    let release;
    let block = false;
    const button = {
        hidden: true, disabled: false,
        classList: { toggle(_name, hidden) { button.hidden = hidden; } },
        addEventListener(_name, handler) { click = handler; }
    };
    nodes.set('prtg-backfill-cancel', button);
    const context = vm.createContext({
        document: { getElementById(id) { if (!nodes.has(id)) nodes.set(id, {}); return nodes.get(id); } },
        canMaintainSchedule: true,
        updateProgressBar() {}, setSpinnerText() {},
        elapsedSinceText: () => '', formatNumber: String, formatDateTime: String,
        withBusy: () => () => {}, toast() {}, refreshPrtgBackfillStatus: async () => {},
        api: { post: async (url, body) => { requests.push({url,body}); if (block) await new Promise(resolve => { release = resolve; }); } }
    });
    vm.runInContext(source.slice(stateStart, refreshStart) + source.slice(bindStart, bindEnd), context);
    vm.runInContext('bindPrtgBackfill()', context);
    return {
        button, requests,
        render(status) { context.renderPrtgBackfillStatus({ daysDone: 0, daysTotal: 1, ...status }); },
        click: () => click(),
        hold() { block = true; }, release: () => release(),
        readonly() { context.canMaintainSchedule = false; }
    };
}
function check(name, fn) { return Promise.resolve().then(fn).then(() => { passed++; process.stdout.write(`PASS ${name}\n`); }); }

for (const kind of ['full', 'tail']) {
    await check(`${kind} uses empty-body contract`, async () => {
        const f = fixture(); f.render({isRunning: true, runKind: kind, runId: 'full-1'});
        assert.equal(f.button.hidden, false); await f.click();
        assert.equal(f.requests.length, 1); assert.equal(f.requests[0].body, null);
    });
}
await check('selected supplies displayed exact run ID', async () => {
    const f = fixture(); f.render({isRunning: true, runKind: 'selected', runId: 'selected-1'});
    assert.equal(f.button.hidden, false); await f.click();
    assert.equal(f.requests.length, 1); assert.equal(f.requests[0].body.runId, 'selected-1');
});
await check('selected poll handoff cannot replace in-flight cancel ID', async () => {
    const f = fixture(); f.render({isRunning: true, runKind: 'selected', runId: 'old-1'}); f.hold();
    const pending = f.click(); f.render({isRunning: true, runKind: 'selected', runId: 'new-2'});
    assert.equal(f.requests[0].body.runId, 'old-1'); f.release(); await pending;
});
for (const [name, status] of [
    ['unknown kind', {isRunning: true, runKind: 'unknown', runId: 'run-1'}],
    ['missing identity', {isRunning: true, runKind: 'selected'}],
    ['blank identity', {isRunning: true, runKind: 'full', runId: '  '}],
    ['completed selected', {isRunning: false, runKind: 'selected', runId: 'old-1'}]
]) {
    await check(`${name} offers no stop and emits no request`, async () => {
        const f = fixture(); f.render(status); assert.equal(f.button.hidden, true); await f.click();
        assert.equal(f.requests.length, 0);
    });
}
await check('readonly polling keeps stop hidden', () => {
    const f = fixture(); f.readonly(); f.render({isRunning: true, runKind: 'selected', runId: 'selected-1'});
    assert.equal(f.button.hidden, true);
});
process.stdout.write(`Passed ${passed} actual Runs interaction contracts; synthetic DOM/API only.\n`);
