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

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = (...p) => fs.readFileSync(path.join(jsRoot, ...p), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');
const catalog = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);

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
    const showDetailModal = ({ title, body }) => { body.closest = () => null; shown.push({ title, body }); };
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
async function openAdminClose(activeCount = 3, onDisabled = 0, { mail = true, issueSource = 'Security' } = {}) {
    const env = makeEnv();
    const source = read('pages', 'work-order-detail.js');
    const fn = extract(source, 'async function openAdminCloseModal(');
    const api = { post: async (url, payload) => { env.posts.push({ url, payload }); return { closedCases: activeCount }; } };
    const run = new Function('button', 'statusNote', 'statusesWhere', 'isNoFixClosure', 'showDetailModal', 'api', 'toast', 'withBusy',
        'currentDetail', 'workOrderId', 'reloadAll', 'getDisplaySettings', `${fn}\nreturn openAdminCloseModal;`)(
        env.button, env.statusNote, catalog.statusesWhere, catalog.isNoFixClosure, env.showDetailModal, api,
        (m, t) => env.toasts.push({ m, t }), () => () => { }, { source: issueSource, counts: { active: activeCount, activeOnDisabledHosts: onDisabled } }, 7, async () => { }, async () => ({ mailWorkOrdersEnabled: mail }));
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
        const one = await openAdminClose(1, 0, { issueSource: null });
        assert.ok(textOf(one.blockWrap).includes('這台主機上的問題之後不再自動派工'));
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

    // B5-b：回覆表單的不處理類說明行
    async replyFormShowsNoFixNoteOnlyForNoFix() {
        const env = makeEnv();
        const source = read('pages', 'issue-status-reply.js');
        const fn = extract(source, 'export function openWorkOrderReplyModal(').replace(/^export /, '');
        const statusOptions = catalog.statusesWhere(s => s.replyable).map(s => ({ value: s.value, label: s.label }));
        const editor = { setValue() { }, clearDraft() { } };
        const noFixConsequenceText = new Function(`${extract(source, 'export function noFixConsequenceText(').replace(/^export /, '')}\nreturn noFixConsequenceText;`)();
        const run = new Function('STATUS_OPTIONS', 'noFixConsequenceText', 'statusNote', 'isNoFixClosure', 'statusMeta', 'makeButton', 'attachNoteEditor',
            'showDetailModal', 'bindPastDateCheck', 'confirmAction', 'withBusy', 'toast', 'todayLocal', 'toLocalDateString', 'MAX_DUE_DATE_DAYS',
            `${fn}\nreturn openWorkOrderReplyModal;`)(
            statusOptions, noFixConsequenceText, env.statusNote, catalog.isNoFixClosure, catalog.statusMeta, env.button, () => editor,
            env.showDetailModal, () => ({ validate: () => true }), async () => true, () => () => { }, () => { },
            () => '2026-01-01', () => '2026-04-01', 90);
        const open = (extra = {}) => {
            env.shown.length = 0;
            run({ title: 't', targetText: '本單 3 台', draftKey: 'k', submit: async () => { }, ...extra });
            const body = env.shown[0].body;
            return { body, select: byId(body, 'wo-reply-status'), note: byId(body, 'wo-reply-no-fix-note') };
        };

        const m = open();
        assert.ok(m.note, '回覆表單有說明行節點');
        assert.ok(textOf(m.note).includes('這台主機的這個問題之後不會再派給任何人；需要恢復請找管理者。'));
        assert.ok(m.note.classList.contains('lf-status-note--warning'), '用 warning 型提示');
        const options = all(m.select).filter(n => n.tagName === 'OPTION').map(n => n.value);
        assert.deepEqual(options.filter(v => ['wont_fix', 'false_positive', 'known_noise'].includes(v)).sort(), ['false_positive', 'known_noise', 'wont_fix']);
        for (const value of options) {
            m.select.value = value;
            m.select.dispatch('change');
            const expected = ['wont_fix', 'false_positive', 'known_noise'].includes(value);
            assert.equal(hidden(m.note), !expected, `${value}：說明行${expected ? '要' : '不可'}出現`);
        }
        for (const value of ['resolved', 'in_progress', 'observing', 'escalated']) {
            assert.ok(options.includes(value), `${value} 在回覆選項內`);
            m.select.value = value;
            m.select.dispatch('change');
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
