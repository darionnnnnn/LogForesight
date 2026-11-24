/**
 * 部分主機回覆（批次 E-2）的行為測試：彈窗內的主機面板（貼上主機名自動勾選、勾選跨頁保留、單一名稱篩選）、
 * 部分回覆結案後的「其餘 N 台」續問、交辦單頁成員表（勾選跨頁、空狀態文字、整單結案後篩選切到全部）。
 * 執行 production 程式（見 load-reply-modal.mjs），只替換 DOM 與 API 邊界。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadReplyModule, openReply, read, extract, fire, all, textOf, hidden, catalog } from './load-reply-modal.mjs';

/** 假的成員端點：total 台進行中成員 SRV-001…，依 hostNames 完全相符（不分大小寫）篩選並分頁 */
function memberSource(total, { closed = new Set() } = {}) {
    const items = Array.from({ length: total }, (_, i) => ({
        caseId: `c${i + 1}`, hostName: `SRV-${String(i + 1).padStart(3, '0')}`, status: 'in_progress', closedAt: closed.has(i + 1) ? 'x' : null
    }));
    const calls = [];
    const load = async ({ page, pageSize, hostNames }) => {
        calls.push({ page, pageSize, hostNames });
        const keys = hostNames ? new Set(hostNames.map(n => n.toUpperCase())) : null;
        const matched = items.filter(m => !keys || keys.has(m.hostName.toUpperCase()));
        return { items: matched.slice((page - 1) * pageSize, page * pageSize), total: matched.length, page, pageSize };
    };
    return { items, calls, load };
}

const nodes = (root, pred) => all(root).filter(pred);
const rowChecks = body => nodes(body, n => n.tagName === 'INPUT' && n.type === 'checkbox' && n.title === '選取這台主機');
const tick = (check, on = true) => { check.checked = on; return fire(check, 'change'); };
const pagerBtn = (body, text) => nodes(body, n => n.tagName === 'BUTTON' && textOf(n) === text)[0];

const cases = {
    // 貼上多個主機名：完全相符（不分大小寫）的自動勾選、找不到的逐一列出
    async pastedNamesSelectMatchesAndListMissing() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load: src.load }, followUp: undefined });
        assert.equal(hidden(m.panel), true, '面板預設收合，對象是全部進行中主機');
        m.toggle.click();
        assert.equal(hidden(m.panel), false);

        m.search.value = 'srv-001\nSRV-003, ghost-1  GHOST-2';
        await m.applyBtn.listeners.click[0]();
        const picked = [...m.panel.find(n => n.id === 'wo-reply-pick-count').textContent.matchAll(/\d+/g)].map(x => x[0]);
        assert.deepEqual(picked, ['2'], '兩台完全相符的被勾選');
        assert.equal(hidden(m.missing), false);
        assert.ok(textOf(m.missing).includes('找不到 2 個') && textOf(m.missing).includes('ghost-1') && textOf(m.missing).includes('GHOST-2'), textOf(m.missing));
        assert.ok(!textOf(m.missing).includes('SRV-001'), '找得到的不列入找不到');
        assert.deepEqual(rowChecks(m.body).map(c => c.checked), [true, true], '清單只列相符的兩台且已勾選');
        assert.ok(m.hintText().includes('選取的 2 台主機'), m.hintText());

        // 送出：勾選的 caseId 以第二個參數傳回
        m.pick('resolved');
        await m.send();
        assert.deepEqual(m.submits, [{ status: 'resolved', note: null, dueDate: null }]);
    },

    // 勾選跨頁保留、已選台數正確；送出帶全部勾選
    async selectionKeptAcrossPagesAndSubmitsAll() {
        const src = memberSource(45);
        const loaded = loadReplyModule();
        const sent = [];
        const m = openReply(loaded, { picker: { load: src.load }, submit: async (payload, picked) => { sent.push(picked); return {}; } });
        m.toggle.click();
        await new Promise(r => setTimeout(r, 0));
        assert.equal(rowChecks(m.body).length, 20, '第一頁 20 筆');

        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        await fire(pagerBtn(m.body, '下一頁'), 'click');
        await new Promise(r => setTimeout(r, 0));
        assert.deepEqual(rowChecks(m.body).map(c => c.checked).filter(Boolean), [], '第二頁的列沒被勾');
        await tick(rowChecks(m.body)[0]);
        assert.equal(textOf(m.panel.find(n => n.id === 'wo-reply-pick-count')), '已選 3 台');

        await fire(pagerBtn(m.body, '上一頁'), 'click');
        await new Promise(r => setTimeout(r, 0));
        assert.deepEqual(rowChecks(m.body).slice(0, 3).map(c => c.checked), [true, true, false], '換頁回來勾選仍在');
        assert.equal(textOf(m.panel.find(n => n.id === 'wo-reply-pick-count')), '已選 3 台');

        m.pick('resolved');
        await m.send();
        assert.deepEqual(sent[0], ['c1', 'c2', 'c21'], '跨頁勾選全部送出');
    },

    // 單一名稱：伺服器端篩選清單（不勾選）；沒勾主機不能送出
    async singleNameFiltersListAndEmptySelectionBlocksSubmit() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load: src.load } });
        m.toggle.click();
        await new Promise(r => setTimeout(r, 0));
        m.pick('resolved');
        assert.equal(m.submitBtn.disabled, true, '切到部分主機但沒勾任何一台：不能送出');
        assert.equal(textOf(m.required), '請勾選要回覆的主機');

        m.search.value = 'srv-002';
        await m.applyBtn.listeners.click[0]();
        assert.deepEqual(src.calls.at(-1).hostNames, ['srv-002'], '單一名稱走伺服器端篩選');
        assert.equal(rowChecks(m.body).length, 1);
        assert.equal(rowChecks(m.body)[0].checked, false, '單一名稱只篩選、不自動勾選');
        await tick(rowChecks(m.body)[0]);
        assert.equal(m.submitBtn.disabled, false);

        // 切回全部進行中主機：不再需要勾選
        m.toggle.click();
        assert.equal(hidden(m.panel), true);
        assert.ok(m.hintText().includes('全部進行中主機'), m.hintText());
        // 清空輸入再套用＝不篩選
        m.toggle.click();
        m.search.value = '  ';
        await m.applyBtn.listeners.click[0]();
        assert.equal(src.calls.at(-1).hostNames, null);
    },

    // 部分回覆已處理後：彈窗不關、問「其餘 N 台」，選了以 caseIds 為空送出
    async restPromptAfterPartialClosingReply() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const calls = [];
        let applied = 0;
        const m = openReply(loaded, {
            picker: { load: src.load },
            submit: async (payload, picked) => { calls.push({ kind: 'submit', payload, picked }); return { cases: 2, workOrderClosed: false }; },
            followUp: {
                remaining: async () => 3,
                submitRest: async payload => { calls.push({ kind: 'rest', payload }); return { cases: 3, workOrderClosed: true }; }
            },
            onApplied: () => { applied++; }
        });
        m.toggle.click();
        await new Promise(r => setTimeout(r, 0));
        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        m.pick('resolved');
        m.write('第一批');
        await m.send();

        assert.equal(loaded.env.closed, 0, '部分回覆結案後彈窗不關閉');
        assert.equal(applied, 1, '剛回覆的那幾台先更新到畫面');
        assert.equal(textOf(m.statusLabel), '其餘 3 台要一起設為…');
        assert.ok(m.hintText().includes('其餘 3 台'), m.hintText());
        assert.deepEqual(m.active(), [], '結論要重新選');
        assert.equal(m.note.value, '', '說明清空');
        assert.equal(hidden(m.panel), true);
        assert.equal(hidden(m.toggle), true, '續問時不再提供切換部分主機');
        assert.equal(m.submitBtn.disabled, true);

        m.pick('resolved');
        await m.send();
        assert.deepEqual(calls.map(c => c.kind), ['submit', 'rest']);
        assert.deepEqual(calls[0].picked, ['c1', 'c2']);
        assert.equal(loaded.env.closed, 1, '續問送出後關閉');
        assert.equal(applied, 2);
    },

    // 續問的邊界：非結案類不問、沒有剩下不問、整單已結案不問、整張送出不問；固定對象的部分回覆（成員表勾選）要問
    async restPromptOnlyWhenPartialClosingAndStillActive() {
        const make = async ({ picking, partialTarget = false, status, remaining = 3, workOrderClosed = false }) => {
            const src = memberSource(3);
            const loaded = loadReplyModule();
            const counts = { remaining: 0 };
            const m = openReply(loaded, {
                picker: picking ? { load: src.load } : undefined,
                partialTarget,
                submit: async () => ({ cases: 1, workOrderClosed }),
                followUp: { remaining: async () => { counts.remaining++; return remaining; }, submitRest: async () => ({}) }
            });
            if (picking) {
                m.toggle.click();
                await new Promise(r => setTimeout(r, 0));
                await tick(rowChecks(m.body)[0]);
            }
            m.pick(status);
            if (status === 'escalated') m.write('我處理不了');
            await m.send();
            return { loaded, counts };
        };
        const closesNormally = r => assert.equal(r.loaded.env.closed, 1);
        const asks = r => assert.equal(r.loaded.env.closed, 0);

        asks(await make({ picking: true, status: 'resolved' }));
        asks(await make({ picking: false, partialTarget: true, status: 'wont_fix' }));
        for (const status of ['in_progress', 'observing', 'escalated']) {
            const r = await make({ picking: true, status });
            closesNormally(r);
            assert.equal(r.counts.remaining, 0, `${status} 是非結案類：連剩餘台數都不查`);
        }
        closesNormally(await make({ picking: true, status: 'resolved', remaining: 0 }));
        const whole = await make({ picking: true, status: 'resolved', workOrderClosed: true });
        closesNormally(whole);
        assert.equal(whole.counts.remaining, 0, '整單已結案不查剩餘');
        const noPartial = await make({ picking: false, partialTarget: false, status: 'resolved' });
        closesNormally(noPartial);
        assert.equal(noPartial.counts.remaining, 0, '整張單回覆（非部分）不問其餘');
    },

    // 成員端點網址：主機名以重複的 hostName 參數送出並編碼
    async orderMembersUrlRepeatsHostNameParam() {
        const loaded = loadReplyModule();
        assert.equal(loaded.orderMembersUrl(7, { status: 'active', page: 2, pageSize: 20 }), '/api/work-orders/7/members?status=active&page=2&pageSize=20');
        assert.equal(loaded.orderMembersUrl(7, { hostNames: ['a b', 'C&D'] }), '/api/work-orders/7/members?status=all&page=1&pageSize=50&hostName=a+b&hostName=C%26D');
        assert.equal(loaded.orderMembersUrl(7, { hostNames: [] }), '/api/work-orders/7/members?status=all&page=1&pageSize=50');
        assert.deepEqual(loaded.parseHostNames('a\nb,c  d，e、f;g；A'), ['a', 'b', 'c', 'd', 'e', 'f', 'g'], '分隔符與不分大小寫去重');
    },

    // 交辦單頁成員表空狀態依篩選組字
    async memberEmptyStateTexts() {
        const fn = extract(read('pages', 'work-order-detail.js'), 'function memberEmptyState(');
        const run = new Function(`${fn}\nreturn memberEmptyState;`)();
        assert.equal(run('active', '', { closed: 5 }).title, '沒有進行中的主機（已結案 5 台）');
        assert.equal(run('active', '', { closed: 0 }).title, '沒有進行中的主機');
        assert.equal(run('active', '', undefined).title, '沒有進行中的主機');
        assert.equal(run('active', 'SRV-X', { closed: 5 }).title, '沒有符合條件的主機', '有主機名篩選一律是沒有符合條件');
        assert.equal(run('overdue', '', { closed: 5 }).title, '沒有符合條件的主機');
        assert.equal(run('all', '', { closed: 0 }).title, '沒有符合條件的主機');
        assert.ok(!JSON.stringify(run('active', '', { closed: 5 })).includes('沒有關聯的成員主機'));
    },

    // 整單結案後成員篩選自動切到「全部」（只切一次，之後尊重使用者自己選的）
    async closedOrderSwitchesMemberFilterToAll() {
        const urls = [];
        const rendered = [];
        const memberStatusSelect = { value: 'active' };
        const source = read('pages', 'work-order-detail.js');
        const loadAll = extract(source, 'async function loadAll(');
        const memberListUrl = extract(source, 'function memberListUrl(');
        const loaded = loadReplyModule();
        const api = {
            get: async url => {
                urls.push(url);
                if (url.endsWith('/timeline')) return [];
                if (url.includes('/members?')) return { items: [], total: 0, page: 1, pageSize: 50 };
                return { closedAt: '2026-01-01', counts: { closed: 5, total: 5, active: 0 }, viewerCanAssign: false };
            }
        };
        const make = detailClosed => new Function('getCurrentUser', 'api', 'workOrderId', 'memberStatusSelect', 'orderMembersUrl', 'PAGE_SIZE',
            'markSeenOnce', 'renderHeader', 'renderReassignedHint', 'renderActions', 'renderMembers', 'renderTimeline', 'renderError',
            'headerContainer', 'membersContainer', 'timelineContainer', 'actionsContainer', 'badgesEl', 'canViewHandlerPage', 'hasCapability', 'linkButton', 'appUrl', 'backLinkFor',
            `let memberPage = 3; let memberHostName = ''; let currentUser = null; let currentDetail = null; let memberAutoSwitched = false;
${memberListUrl}\n${loadAll}\nreturn { loadAll, state: () => ({ memberPage, currentDetail }) };`)(
            async () => ({ userId: 1 }), { get: async (url, o) => { const r = await api.get(url, o); return detailClosed || !r.closedAt ? r : { ...r, closedAt: null }; } },
            7, memberStatusSelect, loaded.orderMembersUrl, 50, () => { }, () => { }, () => { }, () => { }, members => rendered.push(members), () => { }, () => { },
            { replaceChildren() { } }, { replaceChildren() { } }, { replaceChildren() { } }, { replaceChildren() { } }, { replaceChildren() { } },
            () => true, () => true, () => null, p => p, () => ({ href: '/', text: '' }));

        const closed = make(true);
        await closed.loadAll();
        assert.equal(memberStatusSelect.value, 'all', '整單結案後篩選切到全部');
        assert.ok(urls.some(u => u.includes('/members?status=all&page=1')), `要以全部重取成員：${urls}`);
        assert.equal(rendered.length, 1);
        assert.equal(closed.state().memberPage, 1);

        // 使用者自己選回「進行中」：再次載入不再強制切換
        memberStatusSelect.value = 'active';
        await closed.loadAll();
        assert.equal(memberStatusSelect.value, 'active', '只自動切一次');

        // 還在進行中的單不切
        memberStatusSelect.value = 'active';
        const open = make(false);
        await open.loadAll();
        assert.equal(memberStatusSelect.value, 'active');
    },

    // 交辦單頁成員表：換頁不清勾選（共用勾選元件）、已選台數隨之顯示；改狀態篩選才清除
    async memberTableKeepsSelectionAcrossPages() {
        const dom = installDom();
        const source = read('pages', 'work-order-detail.js');
        const renderMembers = extract(source, 'function renderMembers(');
        const uiSource = read('core', 'ui.js');
        const createPageSelection = new Function(`${extract(uiSource, 'export function createPageSelection(')}\nreturn createPageSelection;`)();
        const bar = dom.newElement('span');
        bar.className = 'd-none';
        bar.classList.toggle = (name, force) => { if (force) bar.classList.add(name); else bar.classList.remove(name); };
        dom.byId.set('wo-member-selected', bar);
        let tableOptions = null;
        let pagination = null;
        const loads = [];
        const memberStatusSelect = { value: 'active' };
        const factory = new Function('createPageSelection', 'renderTable', 'renderPagination', 'guardLoad', 'loadMembersOnly', 'memberStatusSelect', 'membersContainer', 'memberPagerContainer',
            'memberNoteEl', 'currentDetail', 'document', 'memberEmptyState', 'PAGE_SIZE',
            `let memberPage = 1; let memberHostName = ''; let currentMemberItems = []; let lastMemberData = null; const loadedMemberHosts = new Map();
const updateSplitBtn = () => {}; const updateReplyBtn = () => {};
const selection = createPageSelection({
    keyOf: item => item.caseId, isSelectable: item => !item.closedAt, getPageItems: () => currentMemberItems,
    labels: { row: '選取這台主機', all: '全選本頁可處理主機' }, bar: { barId: 'wo-member-selected', countId: 'wo-member-selected', unit: '台' },
    onSelectAll: () => { renderMemberRows(); }, onChange: () => { updateSplitBtn(); updateReplyBtn(); }
});
function renderMemberRows() { if (lastMemberData) renderMembers(lastMemberData); }
${renderMembers}
return { renderMembers, selection, page: () => memberPage };`);
        const api = factory(createPageSelection, (container, options) => { tableOptions = options; }, (container, options) => { pagination = options; },
            (container, fn) => { loads.push(fn); }, () => { }, memberStatusSelect, { querySelectorAll: () => [] }, {}, { textContent: '', classList: { add() { }, remove() { } } },
            { viewerCanReply: true, viewerCanAssign: false, closedAt: null, counts: { closed: 0 } }, globalThis.document, () => ({}), 50);

        const page1 = { items: [{ caseId: 'c1', hostName: 'A', closedAt: null }, { caseId: 'c2', hostName: 'B', closedAt: null }], total: 4, page: 1, pageSize: 2, hiddenMemberCount: 0 };
        api.renderMembers(page1);
        const cell = tableOptions.columns[0].render(page1.items[0]);
        await tick(cell);
        assert.equal(api.selection.selected.size, 1);
        assert.equal(textOf(bar), '已選 1 台', '工具列顯示已選台數');

        pagination.onPage(2);
        assert.equal(api.selection.selected.size, 1, '換頁不清勾選');
        assert.equal(loads.length, 1, '換頁仍會重新載入該頁');

        api.renderMembers({ ...page1, items: [{ caseId: 'c3', hostName: 'C', closedAt: null }], page: 2 });
        const cell2 = tableOptions.columns[0].render({ caseId: 'c3', hostName: 'C', closedAt: null });
        await tick(cell2);
        assert.equal(textOf(bar), '已選 2 台');
        const back = tableOptions.columns[0].render(page1.items[0]);
        assert.equal(back.checked, true, '回到第一頁時該列仍是勾選');
        // 已結案成員不能勾
        assert.equal(tableOptions.columns[0].render({ caseId: 'c9', closedAt: 'x' }).tagName === 'INPUT', false);
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
