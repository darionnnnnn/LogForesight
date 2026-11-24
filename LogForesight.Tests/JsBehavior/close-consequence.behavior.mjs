/**
 * 代為結案對話框與處理人回覆表單的「後果說明」行為測試（第 76-2 輪 B5-a／B5-b）。
 *
 * 實際執行 production 的 openAdminCloseModal（pages/work-order-detail.js）與
 * openWorkOrderReplyModal（pages/issue-status-reply.js），狀態目錄、statusNote、button 也用 production 版；
 * 只替換 api／toast／showDetailModal／attachNoteEditor 等邊界。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { loadReplyModule, openReply } from './load-reply-modal.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = (...p) => fs.readFileSync(path.join(jsRoot, ...p), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');
const catalog = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);
const { mailNotifyText } = await import(pathToFileURL(path.join(jsRoot, 'core', 'mail-notify.js')).href);
const { exclusive } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);
const NEUTRAL = '依「交辦單通知」設定而定';

function extract(source, declaration) {
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

function extractLine(source, startsWith) {
    const line = source.split('\n').find(l => l.startsWith(startsWith));
    assert(line, `找不到 production 敘述：${startsWith}`);
    return line;
}

function deferred() {
    let resolve;
    const promise = new Promise(r => { resolve = r; });
    return { promise, resolve };
}

function makeEnv() {
    const dom = installDom();
    const originalCreate = document.createElement;
    document.createElement = tag => {
        const el = originalCreate(tag);
        el.focus = () => { };
        if (tag === 'textarea' || tag === 'input') el.value = '';
        return el;
    };
    const { statusNote } = loadElements(dom);
    const button = loadButton(dom);
    const shown = [];
    const showDetailModal = ({ title, body }) => { body.closest = () => null; shown.push({ title, body }); return { close() { }, element: null }; };
    const posts = [];
    const toasts = [];
    return { dom, statusNote, button, shown, showDetailModal, posts, toasts };
}

const all = node => { const out = []; const walk = n => { out.push(n); n.children.forEach(walk); }; walk(node); return out; };
const byId = (root, id) => all(root).find(n => n.id === id) ?? null;
const hidden = n => n.classList.contains('d-none');
const textOf = node => node.textContent;
const notes = root => all(root).filter(n => n.classList?.contains('lf-status-note'));

// ── 代為結案對話框 ───────────────────────────────────────────────
function loadAdminClose(env, { settings, currentDetail, getDisplaySettings }) {
    const source = read('pages', 'work-order-detail.js');
    const fn = extract(source, 'async function buildAdminCloseModal(');
    const wiring = extractLine(source, 'const openAdminCloseModal = ');
    const blockTargetText = extract(source, 'function blockTargetText(');
    const api = { post: async (url, payload) => { env.posts.push({ url, payload }); return { closedCases: 1 }; } };
    return new Function('button', 'statusNote', 'statusesWhere', 'isNoFixClosure', 'showDetailModal', 'api', 'toast', 'withBusy',
        'currentDetail', 'workOrderId', 'reloadAll', 'getDisplaySettings', 'mailNotifyText', 'exclusive',
        `${blockTargetText}\n${fn}\n${wiring}\nreturn openAdminCloseModal;`)(
        env.button, env.statusNote, catalog.statusesWhere, catalog.isNoFixClosure, env.showDetailModal, api,
        (m, t) => env.toasts.push({ m, t }), () => () => { }, currentDetail, 7, async () => { },
        getDisplaySettings ?? (async () => settings), mailNotifyText, exclusive);
}

async function openAdminClose(activeCount = 3, onDisabled = 0, { mail = true, issueSource = 'Security', settings } = {}) {
    const env = makeEnv();
    const detail = { source: issueSource, counts: { active: activeCount, activeOnDisabledHosts: onDisabled } };
    const run = loadAdminClose(env, { settings: settings ?? { mailWorkOrdersEnabled: mail }, currentDetail: detail });
    await run();
    const body = env.shown[0].body;
    const form = body.children[0];
    const block = byId(body, 'wo-close-block');
    return {
        env, body, form, block,
        select: byId(body, 'wo-close-status'),
        blockWrap: block._parent,
        reason: byId(body, 'wo-close-reason'),
        consequence: byId(body, 'wo-close-consequence'),
        submit: all(body).find(n => n.tagName === 'BUTTON' && n.type === 'submit'),
        pick(value) { this.select.value = value; this.select.dispatch('change'); },
        write(text) { this.reason.value = text; this.reason.dispatch('input'); },
        tick() { this.block.dispatch('change'); },
        async send() { await form.listeners.submit[0]({ preventDefault() { } }); }
    };
}

// ── 交辦單詳情動作列（renderActions）─────────────────────────────
function loadActions({ counts, settings = { mailWorkOrdersEnabled: true } }) {
    const env = makeEnv();
    const source = read('pages', 'work-order-detail.js');
    const fn = extract(source, 'function renderActions(');
    const optional = extract(source, 'function blockTargetText(');
    const actionsContainer = document.createElement('div');
    const memberStatusSelect = { value: '' };
    const opened = [];
    const confirms = [];
    const api = {
        get: async url => (url.includes('/members?') ? { items: [{ caseId: 55, hostName: 'HOST-D' }] } : {}),
        post: async () => ({})
    };
    const detail = {
        viewerCanReply: true, viewerCanAssign: true, closedAt: null, handlerId: 1, source: 'Security',
        counts: { total: 1, ...counts }
    };
    const run = new Function('actionsContainer', 'button', 'toast', 'memberStatusSelect', 'selectedCaseIds', 'guardLoad', 'loadMembersOnly', 'membersContainer',
        'api', 'workOrderId', 'openMemberReply', 'hasCapability', 'openSelectHandlerModal', 'loadedMemberHosts', 'reloadAll', 'confirmActionWithReason', 'withBusy',
        'getDisplaySettings', 'mailNotifyText', 'replyableCount', 'appUrl', 'location', 'openAdminCloseModal',
        `let memberPage = 1;\n${optional}\n${fn}\nreturn renderActions;`)(
        actionsContainer, env.button, (m, t) => env.toasts.push({ m, t }), memberStatusSelect, new Set(), async (_c, f) => f(), async () => { },
        { scrollIntoView() { } }, api, 7, (caseIds, targetText) => opened.push({ caseIds, targetText }), () => true, () => { }, new Map(), async () => { },
        async options => { confirms.push(options); return null; }, () => () => { }, async () => settings, mailNotifyText, catalog.replyableCount, p => p, {}, async () => { });
    run(detail, {});
    return {
        env, opened, confirms, memberStatusSelect, toasts: env.toasts,
        async click(label) {
            const btn = all(actionsContainer).find(n => n.tagName === 'BUTTON' && n.textContent.includes(label));
            assert.ok(btn, `找不到按鈕：${label}`);
            await btn.listeners.click[0]();
        }
    };
}

const cases = {
    // B5-a：打開時沒有預選、確認鈕停用
    async adminCloseOpensWithoutPreselection() {
        const m = await openAdminClose();
        assert.equal(m.select.value, '', '結案狀態不預選');
        assert.equal(m.submit.disabled, true, '未選狀態、未填原因時確認鈕停用');
        m.write('原因');
        assert.equal(m.submit.disabled, true, '只填原因不選狀態仍停用');
        m.pick('resolved');
        assert.equal(m.submit.disabled, false, '選了狀態又填了原因才可按');
        m.write('   ');
        assert.equal(m.submit.disabled, true, '原因只有空白視為未填');
    },
    // B5-a：勾選框只在不處理類出現、預設不勾、改選已處理時清掉
    async adminCloseBlockCheckboxOnlyForNoFix() {
        const m = await openAdminClose();
        assert.equal(hidden(m.blockWrap), true, '未選狀態時不出現');
        m.pick('resolved');
        assert.equal(hidden(m.blockWrap), true, '已處理不出現');
        for (const status of ['wont_fix', 'false_positive', 'known_noise']) {
            m.pick(status);
            assert.equal(hidden(m.blockWrap), false, `${status} 要出現勾選框`);
            assert.equal(m.block.checked, false, '預設不勾');
            m.block.checked = true;
            m.tick();
            m.pick('resolved');
            assert.equal(m.block.checked, false, '改選已處理後值清掉');
        }
        m.pick('wont_fix');
        assert.equal(m.block.checked, false, '再選回不處理也不會殘留舊值');
        assert.ok(textOf(m.blockWrap).includes('之後不再自動派工'));
    },
    // B5-a：後果說明三件事都看得到、確認鈕寫出台數
    async adminCloseExplainsConsequences() {
        const m = await openAdminClose(5);
        m.pick('wont_fix');
        const text = notes(m.consequence).map(textOf).join('｜');
        assert.ok(text.includes('結案通知'), '通知開啟時說處理人會收到結案通知');
        assert.ok(text.includes('重新派工'), '不勾時問題再出現會重新派工');
        assert.ok(text.includes('勾選') && text.includes('靜音'), '可以勾選或改用靜音');
        assert.ok(notes(m.consequence).some(n => n.classList.contains('lf-status-note--warning')), '後果用 warning 型提示');
        m.block.checked = true;
        m.tick();
        assert.ok(!notes(m.consequence).map(textOf).join('').includes('會重新派工'), '勾選後不再說會重新派工');
        m.pick('resolved');
        assert.ok(!notes(m.consequence).some(n => n.classList.contains('lf-status-note--warning')), '已處理沒有警示');
        assert.ok(notes(m.consequence).map(textOf).join('').includes('結案通知'), '已處理也要說處理人會收到通知');
        assert.ok(textOf(m.submit).includes('代為結案 5 台'), `確認鈕要含台數：${textOf(m.submit)}`);
    },
    // 單上只剩停用主機的進行中成員：後端仍會結案它們，按鈕台數不能是 0，後果說明帶出停用台數
    async adminCloseCountsMembersOnDisabledHosts() {
        const m = await openAdminClose(0, 2);
        assert.ok(textOf(m.submit).includes('代為結案 2 台'), `只剩停用主機時台數不是 0：${textOf(m.submit)}`);
        m.pick('resolved');
        assert.ok(notes(m.consequence).map(textOf).join('').includes('含已停用主機 2 台'));
        const mixed = await openAdminClose(3, 1);
        assert.ok(textOf(mixed.submit).includes('代為結案 4 台'));
        const none = await openAdminClose(3, 0);
        none.pick('resolved');
        assert.ok(!notes(none.consequence).map(textOf).join('').includes('已停用主機'));
    },
    // 續掛已停止的提示帶停止日期；沒有日期就不寫括號
    async continuationStopNoteShowsDate() {
        const env = makeEnv();
        const fn = extract(read('pages', 'work-order-detail.js'), 'function continuationStopNote(');
        const run = new Function('statusNote', 'formatDate', 'viewerIsAdminLike', `${fn}\nreturn continuationStopNote;`)(env.statusNote, v => `D:${v}`, () => true);
        const withDate = run({ continuationStopped: true, continuationStoppedLabel: '處理人停用', continuationStoppedAt: '2026-11-03T02:00:00' });
        assert.ok(textOf(withDate).includes('續掛已停止（D:2026-11-03T02:00:00）：處理人停用'), textOf(withDate));
        const without = run({ continuationStopped: true, continuationStoppedLabel: '處理人停用', continuationStoppedAt: null });
        assert.ok(textOf(without).includes('續掛已停止：處理人停用'));
        assert.equal(run({ continuationStopped: false }), null);
    },
    // 結案通知依「交辦單通知」切換：關閉時不承諾信會寄出，改請自行告知（與退回、取消同句型）
    async adminCloseNotifyTextFollowsMailSetting() {
        const on = await openAdminClose(3, 0, { mail: true });
        on.pick('resolved');
        const onText = notes(on.consequence).map(textOf).join('');
        assert.ok(onText.includes('下一封交辦摘要收到結案通知'), onText);
        assert.ok(!onText.includes('未啟用'));
        const off = await openAdminClose(3, 0, { mail: false });
        off.pick('resolved');
        const offText = notes(off.consequence).map(textOf).join('');
        assert.ok(offText.includes('目前未啟用交辦單通知，請自行告知處理人'), offText);
        assert.ok(!offText.includes('會在下一封'), '關閉時不可承諾會寄信');
    },
    // 多問題單（沒有單一來源）的勾選框不指稱「這個問題」
    async adminCloseCheckboxWordingForMultiIssueOrder() {
        const single = await openAdminClose(3, 0, { issueSource: 'Security' });
        assert.ok(textOf(single.blockWrap).includes('這 3 台主機的這個問題之後不再自動派工'), textOf(single.blockWrap));
        const multi = await openAdminClose(3, 0, { issueSource: null });
        const text = textOf(multi.blockWrap);
        assert.ok(text.includes('這 3 台主機上的問題之後不再自動派工'), text);
        assert.ok(!text.includes('這個問題'));
        multi.pick('wont_fix');
        const consequence = notes(multi.consequence).map(textOf).join('｜');
        assert.ok(consequence.includes('這 3 台主機上的問題之後再出現時會重新派工') && !consequence.includes('這個問題'), consequence);
        const one = await openAdminClose(1, 0, { issueSource: null });
        assert.ok(textOf(one.blockWrap).includes('這台主機上的問題之後不再自動派工'));
    },
    // 後果說明與勾選框指稱同一個對象（單台／多台、單一問題／多問題）：不寫死「這台主機的這個問題」
    async adminCloseConsequenceNamesSameTargetAsCheckbox() {
        const consequenceOf = m => notes(m.consequence).map(textOf).join('｜');
        const single = await openAdminClose(3, 0, { issueSource: 'Security' });
        single.pick('wont_fix');
        assert.ok(consequenceOf(single).includes('未勾選：這 3 台主機的這個問題之後再出現時會重新派工'), consequenceOf(single));
        single.block.checked = true;
        single.tick();
        assert.ok(consequenceOf(single).includes('已勾選：這 3 台主機的這個問題之後不會再自動派工'), consequenceOf(single));

        const multi = await openAdminClose(3, 0, { issueSource: null });
        multi.pick('wont_fix');
        assert.ok(consequenceOf(multi).includes('未勾選：這 3 台主機上的問題之後再出現時會重新派工'), consequenceOf(multi));
        multi.block.checked = true;
        multi.tick();
        const checked = consequenceOf(multi);
        assert.ok(checked.includes('已勾選：這 3 台主機上的問題之後不會再自動派工'), checked);
        assert.ok(!checked.includes('這個問題'), '多問題單不指稱「這個問題」');

        const one = await openAdminClose(1, 0, { issueSource: null });
        one.pick('wont_fix');
        assert.ok(consequenceOf(one).includes('未勾選：這台主機上的問題之後再出現時會重新派工'), consequenceOf(one));
        one.block.checked = true;
        one.tick();
        assert.ok(consequenceOf(one).includes('已勾選：這台主機上的問題之後不會再自動派工'), consequenceOf(one));
        // 台數含停用主機：後果說明與勾選框同一個台數
        const disabled = await openAdminClose(1, 2, { issueSource: 'Security' });
        disabled.pick('wont_fix');
        assert.ok(consequenceOf(disabled).includes('未勾選：這 3 台主機的這個問題'), consequenceOf(disabled));
        assert.ok(textOf(disabled.blockWrap).includes('這 3 台主機的這個問題'));
    },
    // 設定讀不到（沒有 mailWorkOrdersEnabled）：中性句，不說「未啟用」、也不承諾會收到
    async adminCloseUnknownMailSettingUsesNeutralText() {
        const m = await openAdminClose(3, 0, { settings: { visibleDayRiskLevels: ['高'] } });
        m.pick('resolved');
        const text = notes(m.consequence).map(textOf).join('');
        assert.ok(text.includes(NEUTRAL), text);
        assert.ok(!text.includes('未啟用'), '讀不到設定不可說未啟用');
        assert.ok(!text.includes('會在下一封'), '讀不到設定不可承諾會寄信');
    },
    // 設定請求回來前再按一次：只開一個對話框；開完之後可以再開
    async adminCloseIgnoresRepeatTriggerWhileLoading() {
        const env = makeEnv();
        const gate = deferred();
        let settingsCalls = 0;
        const run = loadAdminClose(env, {
            currentDetail: { source: 'Security', counts: { active: 2, activeOnDisabledHosts: 0 } },
            getDisplaySettings: async () => { settingsCalls++; await gate.promise; return { mailWorkOrdersEnabled: true }; }
        });
        const first = run();
        const second = run();
        gate.resolve();
        await Promise.all([first, second]);
        assert.equal(env.shown.length, 1, '等待期間的重複觸發要被忽略');
        assert.equal(settingsCalls, 1);
        await run();
        assert.equal(env.shown.length, 2, '第一個開完後可以再開');
    },
    // B5-a：送出請求帶 blockRedispatch
    async adminCloseSendsBlockRedispatch() {
        const checked = await openAdminClose();
        checked.pick('false_positive');
        checked.block.checked = true;
        checked.tick();
        checked.write('誤報');
        await checked.send();
        assert.equal(checked.env.posts.length, 1);
        assert.equal(checked.env.posts[0].url, '/api/work-orders/7/admin-close');
        assert.deepEqual(checked.env.posts[0].payload, { status: 'false_positive', reason: '誤報', blockRedispatch: true });

        const unchecked = await openAdminClose();
        unchecked.pick('wont_fix');
        unchecked.write('不處理');
        await unchecked.send();
        assert.equal(unchecked.env.posts[0].payload.blockRedispatch, false);

        const resolved = await openAdminClose();
        resolved.pick('wont_fix');
        resolved.block.checked = true;
        resolved.tick();
        resolved.pick('resolved');
        resolved.write('已修好');
        await resolved.send();
        assert.equal(resolved.env.posts[0].payload.blockRedispatch, false, '改選已處理後不帶停派');
    },
    // 未選狀態硬送（例如 Enter 鍵）也不會打出請求
    async adminCloseRefusesWithoutStatus() {
        const m = await openAdminClose();
        m.write('原因');
        await m.send();
        assert.equal(m.env.posts.length, 0);
    },

    // 「回覆這張單」台數含停用主機：只剩停用主機上的一台時直接開回覆；一啟用加一停用要走多台的勾選流程
    async actionsReplyCountsMembersOnDisabledHosts() {
        const run = async counts => {
            const h = loadActions({ counts });
            await h.click('回覆這張單');
            return h;
        };
        const onlyDisabled = await run({ active: 0, activeOnDisabledHosts: 1 });
        assert.deepEqual(onlyDisabled.opened, [{ caseIds: [55], targetText: '主機 HOST-D' }], '只剩停用主機的一台要開單台回覆');
        assert.ok(!onlyDisabled.toasts.some(t => t.m.includes('已沒有進行中')), '不可被擋成沒有進行中的主機');

        // 一啟用加一停用：合計 2 台，一律開整單彈窗（caseIds 為空），不再只捲到成員表
        const mixed = await run({ active: 1, activeOnDisabledHosts: 1 });
        assert.deepEqual(mixed.opened, [{ caseIds: [], targetText: '全部進行中主機（2 台）' }], '兩台合計要開整單彈窗');
        assert.equal(mixed.toasts.length, 0);

        const single = await run({ active: 1, activeOnDisabledHosts: 0 });
        assert.equal(single.opened.length, 1);

        const none = await run({ active: 0, activeOnDisabledHosts: 0 });
        assert.equal(none.opened.length, 0);
        assert.deepEqual(none.toasts.map(t => t.m), ['這張單已沒有進行中的主機']);
    },
    // 退回處理人、取消交辦：設定三態各說各的話；讀不到不說「未啟用」
    async actionsNotifyTextFollowsMailSettingThreeStates() {
        for (const [label, expectOn] of [['退回處理人', '下一封交辦摘要收到退回通知'], ['取消交辦', '下一封交辦摘要收到取消通知']]) {
            const on = loadActions({ counts: { active: 1, escalated: 1 }, settings: { mailWorkOrdersEnabled: true } });
            await on.click(label);
            assert.ok(on.confirms[0].message.includes(expectOn), on.confirms[0].message);
            const off = loadActions({ counts: { active: 1, escalated: 1 }, settings: { mailWorkOrdersEnabled: false } });
            await off.click(label);
            assert.ok(off.confirms[0].message.includes('目前未啟用交辦通知，請自行告知處理人'), off.confirms[0].message);
            const unknown = loadActions({ counts: { active: 1, escalated: 1 }, settings: { visibleDayRiskLevels: ['高'] } });
            await unknown.click(label);
            const message = unknown.confirms[0].message;
            assert.ok(message.includes(NEUTRAL), message);
            assert.ok(!message.includes('未啟用') && !message.includes('下一封'), `${label}：讀不到設定不可承諾也不可否認：${message}`);
        }
    },
    // 解除靜音：三態
    async clearMuteNotifyTextFollowsMailSettingThreeStates() {
        const source = read('core', 'ui.js');
        const fn = extract(source, 'export async function confirmAndClearMute(').replace(/^export /, '');
        const run = async settings => {
            const messages = [];
            const f = new Function('getDisplaySettings', 'mailNotifyText', 'confirmAction', 'api', 'toast', `${fn}\nreturn confirmAndClearMute;`)(
                async () => settings, mailNotifyText, async ({ message }) => { messages.push(message); return false; }, {}, () => { });
            await f({ sourceName: 'S', eventId: 1 });
            return messages[0];
        };
        const on = await run({ mailWorkOrdersEnabled: true });
        assert.ok(on.includes('下一封交辦摘要收到恢復通知'), on);
        const off = await run({ mailWorkOrdersEnabled: false });
        assert.ok(off.includes('目前未啟用交辦單通知'), off);
        const unknown = await run({ visibleDayRiskLevels: ['高'] });
        assert.ok(unknown.includes(NEUTRAL), unknown);
        assert.ok(!unknown.includes('未啟用') && !unknown.includes('下一封'), unknown);
    },
    // 靜音對話框：代為結案附註的三態，以及設定請求回來前重複觸發只開一個
    async muteModalNotifyTextThreeStatesAndRepeatTrigger() {
        const build = (settings, gate) => {
            const env = makeEnv();
            const source = read('pages', 'issue-mute-modal.js');
            const fn = [
                extract(source, 'function muteEndDate('), extract(source, 'function dateOnly('), extract(source, 'async function loadActiveOrders('),
                extract(source, 'async function buildIssueMuteModal('),
                extractLine(source, 'export const openIssueMuteModal = ').replace(/^export /, '')
            ].join('\n');
            const api = { get: async () => ({ total: 1, items: [{ counts: { active: 2 } }] }), put: async () => ({}) };
            const user = { capabilities: ['Assign', 'Handle'] };
            const run = new Function('api', 'getCurrentUser', 'hasCapability', 'getDisplaySettings', 'button', 'statusNote', 'showDetailModal', 'toast', 'withBusy',
                'formatDateTime', 'toLocalDateString', 'mailNotifyText', 'exclusive', 'DURATION_OPTIONS', 'MIN_DAYS', 'MAX_DAYS', 'DEFAULT_CUSTOM_DAYS',
                `${fn}\nreturn openIssueMuteModal;`)(
                api, async () => user, () => true,
                async () => { if (gate) await gate.promise; return settings; },
                env.button, env.statusNote, env.showDetailModal, () => { }, () => () => { },
                v => String(v), () => '2026-01-01', mailNotifyText, exclusive,
                [{ label: '1 天', days: 1 }, { label: '1 週', days: 7 }, { label: '自訂', days: null }], 1, 365, 30);
            return { env, run };
        };
        const noteOf = async settings => {
            const { env, run } = build(settings);
            await run({ source: 'Security', eventId: 1, issueLabel: 'L' });
            return all(env.shown[0].body).filter(n => n.classList?.contains('form-text')).map(textOf).join('｜');
        };
        assert.ok((await noteOf({ mailWorkOrdersEnabled: true })).includes('處理人會在下一封交辦摘要收到通知'));
        assert.ok((await noteOf({ mailWorkOrdersEnabled: false })).includes('目前未啟用交辦單通知，請自行告知處理人'));
        const unknown = await noteOf({});
        assert.ok(unknown.includes(NEUTRAL), unknown);
        assert.ok(!unknown.includes('未啟用') && !unknown.includes('下一封'), unknown);

        const gate = deferred();
        const { env, run } = build({ mailWorkOrdersEnabled: true }, gate);
        const a = run({ source: 'Security', eventId: 1, issueLabel: 'L' });
        const b = run({ source: 'Security', eventId: 1, issueLabel: 'L' });
        gate.resolve();
        await Promise.all([a, b]);
        assert.equal(env.shown.length, 1, '等待期間的重複觸發要被忽略');
    },

    // B5-b：回覆表單的不處理類說明行（結論改成一排按鈕後，以 production 整份模組執行）
    async replyFormShowsNoFixNoteOnlyForNoFix() {
        const loaded = loadReplyModule();
        const open = (extra = {}) => {
            const m = openReply(loaded, { targetText: '本單 3 台', draftKey: 'k', ...extra });
            return { ...m, note: byId(m.body, 'wo-reply-no-fix-note') };
        };

        const m = open();
        assert.ok(m.note, '回覆表單有說明行節點');
        assert.ok(textOf(m.note).includes('這台主機的這個問題之後不會再派給任何人；需要恢復請找管理者。'));
        assert.ok(m.note.classList.contains('lf-status-note--warning'), '用 warning 型提示');
        assert.equal(hidden(m.note), true, '沒選結論時不顯示');
        const options = m.chips.map(n => n.dataset.status);
        assert.deepEqual(options.filter(v => ['wont_fix', 'false_positive', 'known_noise'].includes(v)).sort(), ['false_positive', 'known_noise', 'wont_fix']);
        for (const value of options) {
            m.pick(value);
            const expected = ['wont_fix', 'false_positive', 'known_noise'].includes(value);
            assert.equal(hidden(m.note), !expected, `${value}：說明行${expected ? '要' : '不可'}出現`);
        }
        for (const value of ['resolved', 'in_progress', 'observing', 'escalated']) {
            assert.ok(options.includes(value), `${value} 在回覆選項內`);
            m.pick(value);
            assert.equal(hidden(m.note), true, `${value} 不出現說明行`);
        }
        // 一次涵蓋多台主機或多張單：改用複數說法，不說「這台主機」
        const many = open({ multiTarget: true });
        assert.ok(textOf(many.note).includes('這些主機的這個問題之後不會再派給任何人'), textOf(many.note));
        assert.ok(!textOf(many.note).includes('這台主機'));
        // 「下一張」沿用上一張的初始狀態為不處理類時，一打開就要看得到
        const carried = open({ initialStatus: 'wont_fix' });
        assert.equal(hidden(carried.note), false, '初始就是不處理時一打開就顯示');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
