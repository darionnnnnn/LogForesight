/**
 * 授權矩陣（groups.js）：每格一個只含那一格的增或減請求、進行中停用、以回應整列重繪、
 * 失敗重新載入、成功提示合併；群組編輯彈窗儲存只送與開啟時的差異。
 *
 * 做法同 groups-members.behavior.mjs：整支讀入、去掉 import 行後以替身參數執行；
 * renderTable 換成記錄欄位定義的替身，由案例自己呼叫 col.render 取得真實的勾選框。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();

const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 8; i++) await tick(); };

const HOST_GROUPS = [
    { groupId: 17, groupName: '機房17', active: true, hostCount: 3 },
    { groupId: 18, groupName: '機房18', active: true, hostCount: 50 },
    { groupId: 19, groupName: '機房19', active: true, hostCount: 0 },
    { groupId: 20, groupName: '機房20', active: true, hostCount: 7 }
];
const USER_GROUPS = [{ groupId: 5, groupName: '維運部', role: 'User', builtin: false, active: true, memberCount: 2 }];

function load({ granted = [19], put, matrixAfter = null }) {
    const src = fs.readFileSync(path.join(jsRoot, 'pages', 'groups.js'), 'utf8').replaceAll('\r\n', '\n');
    const imports = src.match(/^import .*;$/gm) ?? [];
    assert.equal(imports.length, 6, 'groups.js 的匯入行數變了，替身要跟著檢查');
    const body = src.replace(/^import .*;$/gm, '');

    const els = new Map();
    const el = key => { if (!els.has(key)) els.set(key, dom.newElement('div')); return els.get(key); };
    const spy = { puts: [], gets: [], posts: [], toasts: [], matrixTables: [], timers: [], checked: [] };
    const fakeDocument = {
        ...document,
        getElementById: id => el(`#${id}`),
        querySelector: sel => el(sel),
        querySelectorAll: sel => sel.includes('group-host-access-checks') ? spy.checked.map(value => ({ value: String(value) })) : [],
        createElement: tag => document.createElement(tag)
    };
    let matrixCalls = 0;
    const stubs = {
        document: fakeDocument,
        bootstrap: { Modal: class { show() { } hide() { } } },
        api: {
            get: async url => {
                spy.gets.push(url);
                if (url === '/api/admin/groups') return USER_GROUPS;
                if (url === '/api/admin/host-groups') return HOST_GROUPS;
                if (url === '/api/admin/access') {
                    matrixCalls++;
                    return matrixCalls > 1 && matrixAfter ? matrixAfter : { userGroups: [{ userGroupId: 5, userGroupName: '維運部', active: true, grantedHostGroupIds: granted }], hostGroups: HOST_GROUPS };
                }
                return [];
            },
            put: async (url, bodyArg) => { spy.puts.push({ url, body: bodyArg }); return put(url, bodyArg, spy.puts.length); },
            post: async (url, bodyArg) => { spy.posts.push({ url, body: bodyArg }); return { groupId: 5, groupName: '維運部' }; }
        },
        renderTable: (container, opts) => { if (opts.columns?.[0]?.className === 'lf-matrix-sticky') spy.matrixTables.push(opts); },
        renderLoading: () => { }, renderEmpty: () => { }, renderError: () => { },
        toast: (message, type) => spy.toasts.push({ message, type }),
        createSeqGuard: () => { let latest = 0; return { next: () => ++latest, isCurrent: seq => seq === latest }; },
        confirmAction: async () => true,
        withBusy: () => () => { },
        bindTabs: () => ({ show() { } }), checkboxList: () => { }, button: () => dom.newElement('button'),
        guardLoad: async (_containers, fn) => fn(),
        renderPagination: () => { },
        formatNumber: n => String(n),
        compareText: (a, b) => String(a).localeCompare(String(b)),
        roleText: (cls, text) => { const n = dom.newElement('span'); n.textContent = text; return n; },
        statusNote: (type, text) => { const n = dom.newElement('div'); n.textContent = text; return n; },
        // 合併提示的計時器改成手動觸發
        setTimeout: fn => { spy.timers.push(fn); return spy.timers.length; },
        clearTimeout: id => { spy.timers[id - 1] = null; }
    };
    const names = Object.keys(stubs);
    const module = new Function(...names, `${body}\nreturn { load, openModal };`)(...names.map(n => stubs[n]));
    return { ...module, el, spy, flushTimers: () => { for (const t of spy.timers.splice(0)) t?.(); } };
}

/** 載入並取得矩陣的勾選框（key＝hostGroupId） */
async function boot(opts) {
    const ctx = load(opts);
    await ctx.load();
    const table = ctx.spy.matrixTables.at(-1);
    assert.ok(table, '矩陣表格沒畫出來');
    const row = table.rows[0];
    const boxes = new Map();
    table.columns.slice(1).forEach((col, i) => boxes.set(HOST_GROUPS[i].groupId, col.render(row)));
    return { ...ctx, table, boxes, row };
}

const flipTo = (box, checked) => { box.checked = checked; box.dispatch('change'); };

const cases = {
    // 連點四格：送出四個各帶一格的請求；進行中該格停用；最後畫面等於最後一個回應（含別人剛做的變更）
    async rapidTogglesSendOnePerCellAndRedrawFromResponse() {
        const pending = [];
        const { boxes, spy, flushTimers } = await boot({
            granted: [19],
            put: () => new Promise(resolve => pending.push(resolve))
        });

        flipTo(boxes.get(17), true);
        flipTo(boxes.get(18), true);
        flipTo(boxes.get(19), false);
        flipTo(boxes.get(20), true);
        await settle();

        assert.equal(spy.puts.length, 4, '每格各送一個請求');
        assert.deepEqual(spy.puts.map(p => p.body), [
            { grant: [17], revoke: [] }, { grant: [18], revoke: [] },
            { grant: [], revoke: [19] }, { grant: [20], revoke: [] }
        ]);
        for (const p of spy.puts) assert.equal(p.url, '/api/admin/access/5');
        for (const id of [17, 18, 19, 20]) assert.equal(boxes.get(id).disabled, true, `進行中 ${id} 要停用`);

        // 伺服器回的完整清單多了「別人剛授權的 17 之外」：這裡回 17、18、20 與（別人做的）19 已被收回
        const finalList = [17, 18, 20];
        pending.forEach(resolve => resolve({ grantedHostGroupIds: finalList }));
        await settle();

        for (const id of [17, 18, 20]) assert.equal(boxes.get(id).checked, true, `${id} 應勾選`);
        assert.equal(boxes.get(19).checked, false);
        for (const id of [17, 18, 19, 20]) assert.equal(boxes.get(id).disabled, false, `完成後 ${id} 要恢復`);

        assert.equal(spy.toasts.length, 0, '合併時間窗內還沒出提示');
        flushTimers();
        assert.equal(spy.toasts.length, 1, '連續變更合併成一則提示');
        assert.ok(spy.toasts[0].message.includes('已授權：機房17、機房18、機房20'), spy.toasts[0].message);
        assert.ok(spy.toasts[0].message.includes('已收回：機房19'), spy.toasts[0].message);
        assert.equal(spy.toasts[0].type, 'success');
    },

    // 別人剛做的變更：回應的清單蓋過本機畫面（回應含本機沒有的 20）
    async responseListRedrawsWholeRow() {
        const { boxes } = await boot({ granted: [19], put: () => ({ grantedHostGroupIds: [18, 20] }) });
        flipTo(boxes.get(18), true);
        await settle();
        assert.equal(boxes.get(20).checked, true, '別人授權的 20 要跟著出現');
        assert.equal(boxes.get(19).checked, false, '別人收回的 19 要跟著消失');
    },

    // 失敗：以 GET /api/admin/access 重新載入矩陣並提示，勾選回到資料庫的狀態
    async failureReloadsMatrixAndWarns() {
        const { boxes, spy, table } = await boot({
            granted: [19],
            put: () => { throw new Error('boom'); },
            matrixAfter: { userGroups: [{ userGroupId: 5, userGroupName: '維運部', active: true, grantedHostGroupIds: [19, 20] }], hostGroups: HOST_GROUPS }
        });
        const before = spy.gets.filter(u => u === '/api/admin/access').length;
        flipTo(boxes.get(18), true);
        await settle();

        assert.equal(spy.gets.filter(u => u === '/api/admin/access').length, before + 1, '失敗要重新載入矩陣');
        assert.ok(spy.matrixTables.length >= 2, '重新載入後要重繪矩陣');
        assert.equal(spy.matrixTables.at(-1).rows[0].grantedHostGroupIds.includes(20), true);
        assert.ok(spy.toasts.some(t => t.type === 'warning' && t.message.includes('重新載入')), JSON.stringify(spy.toasts));
        assert.equal(spy.toasts.some(t => t.type === 'success'), false);
        assert.equal(table.columns[1].title, '機房17（3 台）', '欄標題要帶台數');
    },

    // 欄標題：台數與完整名稱放 title
    async columnHeaderShowsHostCountAndTitle() {
        const { table } = await boot({ put: () => ({ grantedHostGroupIds: [] }) });
        const header = table.columns[2].renderHeader();
        assert.equal(header.textContent, '機房18（50 台）');
        assert.equal(header.title, '機房18（50 台）');
        assert.equal(table.columns[3].title, '機房19（0 台）', '0 台也要顯示');
        assert.equal(table.columns[0].className, 'lf-matrix-sticky', '首欄固定');
    },

    // 彈窗：開啟時授權 {18,19}，儲存時勾成 {19,20} → 只送 grant 20、revoke 18
    async modalSavesOnlyTheDifference() {
        const ctx = load({ granted: [18, 19], put: () => ({ grantedHostGroupIds: [] }) });
        await ctx.load();
        ctx.el('#group-role').value = 'User';
        ctx.openModal('user', USER_GROUPS[0]);
        ctx.el('#group-name').value = '維運部';
        ctx.el('#group-active').checked = true;
        ctx.spy.checked.push(19, 20);
        await ctx.el('#group-form').listeners.submit[0]({ preventDefault() { } });
        await settle();

        assert.equal(ctx.spy.puts.length, 1);
        assert.deepEqual(ctx.spy.puts[0].body, { grant: [20], revoke: [18] });
    },

    // 彈窗沒有差異：不送授權請求
    async modalWithoutDifferenceSendsNoAccessRequest() {
        const ctx = load({ granted: [18, 19], put: () => ({ grantedHostGroupIds: [] }) });
        await ctx.load();
        ctx.el('#group-role').value = 'User';
        ctx.openModal('user', USER_GROUPS[0]);
        ctx.el('#group-name').value = '維運部';
        ctx.spy.checked.push(18, 19);
        await ctx.el('#group-form').listeners.submit[0]({ preventDefault() { } });
        await settle();

        assert.equal(ctx.spy.posts.length, 1, '群組本身仍要儲存');
        assert.equal(ctx.spy.puts.length, 0, '沒有差異不送授權請求');
    },

    // 彈窗全部取消勾選＝收回開啟時有的全部（仍然是差異，要送）
    async modalUncheckAllRevokesWhatWasThere() {
        const ctx = load({ granted: [18, 19], put: () => ({ grantedHostGroupIds: [] }) });
        await ctx.load();
        ctx.el('#group-role').value = 'User';
        ctx.openModal('user', USER_GROUPS[0]);
        ctx.el('#group-name').value = '維運部';
        await ctx.el('#group-form').listeners.submit[0]({ preventDefault() { } });
        await settle();

        assert.deepEqual(ctx.spy.puts[0].body, { grant: [], revoke: [18, 19] });
    }
};

const name = process.argv[2];
if (!name) { Object.keys(cases).forEach(key => console.log(key)); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
