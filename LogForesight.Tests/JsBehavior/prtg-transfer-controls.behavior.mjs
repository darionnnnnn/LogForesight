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

const pending = { transferId: '12345678-1234-1234-1234-123456789abc', packageSha256: 'a'.repeat(64), declaredBytes: 42 };
const storageKey = 'lf-prtg-diagnostic:/:73:false:maintainer';
function deferred() {
    let resolve, reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}
function tick() { return new Promise(resolve => setImmediate(resolve)); }

function makeHarness({ upload, status, abandon, confirmation } = {}) {
    const dom = installDom();
    const ids = ['prtg-export-btn', 'prtg-import-btn', 'prtg-import-file', 'prtg-import-result',
        'prtg-import-cancel-btn', 'prtg-import-status-btn', 'prtg-import-abandon-btn', 'prtg-import-forget-btn'];
    for (const id of ids) {
        const element = dom.newElement(id.includes('file') ? 'input' : 'button');
        element.id = id;
        dom.byId.set(id, element);
    }
    const calls = { upload: 0, status: 0, abandon: 0, confirm: 0 };
    const local = new Map([[storageKey, JSON.stringify(pending)]]);
    globalThis.localStorage = {
        getItem: key => local.get(key) ?? null,
        setItem: (key, value) => local.set(key, String(value)),
        removeItem: key => local.delete(key)
    };
    const deps = {
        getCurrentUser: async () => ({ userId: 73, isServerAdmin: false, account: 'maintainer' }),
        appUrl: value => value,
        formatNumber: value => String(value),
        toast: () => { },
        confirmAction: async () => { calls.confirm++; return confirmation ? confirmation() : true; },
        withBusy: button => {
            const wasDisabled = button.disabled;
            button.disabled = true;
            return () => { button.disabled = wasDisabled; };
        },
        uploadDiagnosticFile: async (...args) => { calls.upload++; return upload ? upload(...args) : ({ transferId: pending.transferId, receivedBytes: 42 }); },
        getDiagnosticTransfer: async id => { calls.status++; return status ? status(id) : ({ transferId: id, state: 'receiving', receivedBytes: 0, declaredBytes: 42 }); },
        abandonDiagnosticTransfer: async id => { calls.abandon++; return abandon ? abandon(id) : ({ transferId: id }); },
        document,
        localStorage: globalThis.localStorage
    };
    const declaration = extractFunction('function bindPrtgDataTransfer()');
    const bind = new Function(...Object.keys(deps), `${declaration}; return bindPrtgDataTransfer;`)(...Object.values(deps));
    bind();
    const el = id => dom.byId.get(id);
    el('prtg-import-file').files = [{ name: 'diagnostic.json', size: 42 }];
    const dispatch = async id => {
        const listeners = el(id).listeners.click || [];
        const pendingCalls = listeners.map(listener => listener({ preventDefault() { } }));
        await Promise.resolve();
        return pendingCalls;
    };
    return { calls, local, el, dispatch, tick };
}

async function doubleUploadClickStartsOneOperationAndRestoresButtons() {
    const gate = deferred();
    const h = makeHarness({ upload: () => gate.promise });
    await h.tick();
    const first = await h.dispatch('prtg-import-btn');
    const second = await h.dispatch('prtg-import-btn');
    await h.tick();
    assert.equal(h.calls.upload, 1, 'busy guard must prevent the second click from starting another upload');
    assert.equal(h.el('prtg-import-status-btn').disabled, true);
    gate.resolve({ transferId: pending.transferId, receivedBytes: 42 });
    await Promise.all([...first, ...second]);
    assert.equal(h.el('prtg-import-btn').disabled, false);
    assert.equal(h.el('prtg-import-status-btn').disabled, true, 'successful upload clears pending state');
    assert.equal(h.el('prtg-import-cancel-btn').disabled, true);
}

async function statusAndAbandonAreMutuallyExclusive() {
    const gate = deferred();
    const h = makeHarness({ status: () => gate.promise });
    await h.tick();
    const statusCalls = await h.dispatch('prtg-import-status-btn');
    const abandonCalls = await h.dispatch('prtg-import-abandon-btn');
    await h.tick();
    assert.equal(h.calls.status, 1);
    assert.equal(h.calls.confirm, 0, 'abandon must not open confirmation during status request');
    assert.equal(h.calls.abandon, 0);
    gate.resolve({ transferId: pending.transferId, state: 'receiving', receivedBytes: 1, declaredBytes: 42 });
    await Promise.all([...statusCalls, ...abandonCalls]);
    assert.equal(h.el('prtg-import-abandon-btn').disabled, false);

    const confirmation = deferred();
    const reverse = makeHarness({ confirmation: () => confirmation.promise });
    await reverse.tick();
    const abandonActive = await reverse.dispatch('prtg-import-abandon-btn');
    const statusBlocked = await reverse.dispatch('prtg-import-status-btn');
    await reverse.tick();
    assert.equal(reverse.calls.confirm, 1);
    assert.equal(reverse.calls.status, 0, 'status must not start while abandon confirmation is active');
    confirmation.resolve(false);
    await Promise.all([...abandonActive, ...statusBlocked]);
    assert.equal(JSON.parse(reverse.local.get(storageKey)).transferId, pending.transferId);
}

async function confirmationBlocksImportAndCancelledForgetPreservesRecord() {
    const confirmGate = deferred();
    const h = makeHarness({ confirmation: () => confirmGate.promise });
    await h.tick();
    const forgetting = await h.dispatch('prtg-import-forget-btn');
    const importing = await h.dispatch('prtg-import-btn');
    await h.tick();
    assert.equal(h.calls.confirm, 1);
    assert.equal(h.calls.upload, 0, 'import must not start while local-forget confirmation is pending');
    confirmGate.resolve(false);
    await Promise.all([...forgetting, ...importing]);
    assert.equal(JSON.parse(h.local.get(storageKey)).transferId, pending.transferId, 'cancel keeps local resume record');
    assert.equal(h.calls.abandon, 0, 'forget is local and must not abandon server state');
    assert.equal(h.el('prtg-import-btn').disabled, false);
}

async function statusForbiddenOrMissingStillAllowsLocalForget() {
    for (const failure of [{ status: 403 }, { status: 404 }]) {
        const h = makeHarness({ status: async () => { throw Object.assign(new Error('request rejected'), failure); } });
        await h.tick();
        const statusCalls = await h.dispatch('prtg-import-status-btn');
        await Promise.all(statusCalls);
        assert.equal(h.el('prtg-import-forget-btn').disabled, false, `${failure.status} must leave local cleanup available`);
        const forgetCalls = await h.dispatch('prtg-import-forget-btn');
        await Promise.all(forgetCalls);
        assert.equal(h.local.has(storageKey), false, `${failure.status} should not block local forget`);
        assert.equal(h.calls.abandon, 0);
    }
}

async function forgetSuccessIsLocalOnly() {
    const h = makeHarness({ confirmation: async () => true });
    await h.tick();
    const forgetCalls = await h.dispatch('prtg-import-forget-btn');
    await Promise.all(forgetCalls);
    assert.equal(h.local.has(storageKey), false);
    assert.equal(h.calls.abandon, 0, 'forget must never call server abandon');
    assert.equal(h.calls.status, 0);
    assert.equal(h.calls.upload, 0);
    assert.equal(h.el('prtg-import-status-btn').disabled, true);
    assert.equal(h.el('prtg-import-forget-btn').disabled, true);
}

async function uploadRestoresControlsAfterCompletion() {
    const h = makeHarness();
    await h.tick();
    const calls = await h.dispatch('prtg-import-btn');
    await Promise.all(calls);
    assert.equal(h.calls.upload, 1);
    assert.equal(h.el('prtg-import-btn').disabled, false);
    assert.equal(h.el('prtg-import-cancel-btn').disabled, true);
    assert.equal(h.el('prtg-import-status-btn').disabled, true);
    assert.equal(h.el('prtg-import-abandon-btn').disabled, true);
    assert.equal(h.el('prtg-import-forget-btn').disabled, true);
}

const cases = {
    doubleUploadClickStartsOneOperationAndRestoresButtons,
    statusAndAbandonAreMutuallyExclusive,
    confirmationBlocksImportAndCancelledForgetPreservesRecord,
    statusForbiddenOrMissingStillAllowsLocalForget,
    forgetSuccessIsLocalOnly,
    uploadRestoresControlsAfterCompletion
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
try { await cases[name](); console.log(`PASS ${name}`); }
catch (error) { console.error(`FAIL ${name}: ${error.stack || error.message}`); process.exitCode = 1; }
