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
function openAdminClose(activeCount = 3) {
    const env = makeEnv();
    const source = read('pages', 'work-order-detail.js');
    const fn = extract(source, 'function openAdminCloseModal(');
    const api = { post: async (url, payload) => { env.posts.push({ url, payload }); return { closedCases: activeCount }; } };
    const run = new Function('button', 'statusNote', 'statusesWhere', 'isNoFixClosure', 'showDetailModal', 'api', 'toast', 'withBusy',
        'currentDetail', 'workOrderId', 'reloadAll', `${fn}\nreturn openAdminCloseModal;`)(
        env.button, env.statusNote, catalog.statusesWhere, catalog.isNoFixClosure, env.showDetailModal, api,
        (m, t) => env.toasts.push({ m, t }), () => () => { }, { counts: { active: activeCount } }, 7, async () => { });
    run();
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
        const m = openAdminClose();
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
        const m = openAdminClose();
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
        const m = openAdminClose(5);
        m.pick('wont_fix');
        const text = notes(m.consequence).map(textOf).join('｜');
        assert.ok(text.includes('處理人會收到'), '處理人會收到通知');
        assert.ok(text.includes('重新派工'), '不勾時問題再出現會重新派工');
        assert.ok(text.includes('勾選') && text.includes('靜音'), '可以勾選或改用靜音');
        assert.ok(notes(m.consequence).some(n => n.classList.contains('lf-status-note--warning')), '後果用 warning 型提示');
        m.block.checked = true;
        m.tick();
        assert.ok(!notes(m.consequence).map(textOf).join('').includes('會重新派工'), '勾選後不再說會重新派工');
        m.pick('resolved');
        assert.ok(!notes(m.consequence).some(n => n.classList.contains('lf-status-note--warning')), '已處理沒有警示');
        assert.ok(notes(m.consequence).map(textOf).join('').includes('處理人會收到'), '已處理也要說處理人會收到通知');
        assert.ok(textOf(m.submit).includes('代為結案 5 台'), `確認鈕要含台數：${textOf(m.submit)}`);
    },
    // B5-a：送出請求帶 blockRedispatch
    async adminCloseSendsBlockRedispatch() {
        const checked = openAdminClose();
        checked.pick('false_positive');
        checked.block.checked = true;
        checked.tick();
        checked.write('誤報');
        await checked.send();
        assert.equal(checked.env.posts.length, 1);
        assert.equal(checked.env.posts[0].url, '/api/work-orders/7/admin-close');
        assert.deepEqual(checked.env.posts[0].payload, { status: 'false_positive', reason: '誤報', blockRedispatch: true });

        const unchecked = openAdminClose();
        unchecked.pick('wont_fix');
        unchecked.write('不處理');
        await unchecked.send();
        assert.equal(unchecked.env.posts[0].payload.blockRedispatch, false);

        const resolved = openAdminClose();
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
        const m = openAdminClose();
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
        const run = new Function('STATUS_OPTIONS', 'statusNote', 'isNoFixClosure', 'statusMeta', 'makeButton', 'attachNoteEditor',
            'showDetailModal', 'bindPastDateCheck', 'confirmAction', 'withBusy', 'toast', 'todayLocal', 'toLocalDateString', 'MAX_DUE_DATE_DAYS',
            `${fn}\nreturn openWorkOrderReplyModal;`)(
            statusOptions, env.statusNote, catalog.isNoFixClosure, catalog.statusMeta, env.button, () => editor,
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
