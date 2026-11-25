/**
 * 部分主機回覆（批次 E-2）的行為測試：彈窗內的主機面板（貼上主機名自動勾選、勾選跨頁保留、單一名稱篩選）、
 * 部分回覆結案後的「其餘 N 台」續問、交辦單頁成員表（勾選跨頁、空狀態文字、整單結案後篩選切到全部）。
 * 執行 production 程式（見 load-reply-modal.mjs），只替換 DOM 與 API 邊界。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadReplyModule, openReply, read, extract, fire, all, byId, textOf, hidden, catalog } from './load-reply-modal.mjs';

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

/** 獨立於 production dateAfterDays 的預期值：今天起第 N 天的本機日期 */
function expectedDate(days) {
    const d = new Date();
    d.setDate(d.getDate() + days);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
const settle = async () => { for (let i = 0; i < 6; i++) await new Promise(r => setTimeout(r, 0)); };

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

    // 貼上兩個以上名稱：不必按「篩選／勾選」就自動查詢並勾選；只貼一個名稱維持現行（不自動篩選）
    async pasteAutoSelectsOnlyWhenTwoOrMoreNames() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load: src.load } });
        m.toggle.click();
        await settle();
        const callsBefore = src.calls.length;

        m.search.value = 'srv-001\nSRV-003';
        await fire(m.search, 'paste');
        await settle();
        assert.ok(src.calls.length > callsBefore, '貼上兩個名稱後自動查詢');
        assert.equal(textOf(m.panel.find(n => n.id === 'wo-reply-pick-count')), '已選 2 台', '自動勾選命中的兩台');
        assert.deepEqual(rowChecks(m.body).map(c => c.checked), [true, true]);

        const m2 = openReply(loaded, { picker: { load: src.load } });
        m2.toggle.click();
        await settle();
        const before = src.calls.length;
        m2.search.value = 'srv-002';
        await fire(m2.search, 'paste');
        await settle();
        assert.equal(src.calls.length, before, '只貼一個名稱不自動查詢');
        assert.equal(rowChecks(m2.body).filter(c => c.checked).length, 0, '也不勾選');
    },

    // 部分主機＋結案類結論：結論下方出現「其餘 N 台一起設為…（選填）」，N＝進行中總台數－已勾選；非結案類不出現
    async restSectionShowsForClosedPartialReply() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load: src.load }, followUp: { remaining: async () => 3, submitRest: async () => ({}) } });
        const box = byId(m.body, 'wo-reply-rest');
        assert.equal(hidden(box), true, '整單模式沒有這一段');
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        m.pick('resolved');
        assert.equal(hidden(box), false);
        assert.equal(textOf(byId(m.body, 'wo-reply-rest-label')), '其餘 3 台一起設為…（選填）');
        const restChips = () => all(byId(m.body, 'wo-reply-rest-status')).filter(n => n.classList?.contains('lf-chip'));
        assert.ok(restChips().length >= 4, '可選的結論與第二步相同');
        assert.deepEqual(restChips().filter(c => c.classList.contains('active')), [], '不預選');

        await tick(rowChecks(m.body)[2]);
        assert.equal(textOf(byId(m.body, 'wo-reply-rest-label')), '其餘 2 台一起設為…（選填）', '多勾一台，N 跟著變');

        m.pick('observing');
        assert.equal(hidden(box), true, '非結案類結論不出現');
        m.pick('wont_fix');
        assert.equal(hidden(box), false);
        // 全勾＝沒有其餘
        await tick(rowChecks(m.body)[3]);
        await tick(rowChecks(m.body)[4]);
        assert.equal(hidden(box), true, '其餘 0 台不出現');

        // 沒有 followUp 或面板還沒取得總台數：不出現
        const noFollow = openReply(loaded, { picker: { load: src.load } });
        noFollow.toggle.click();
        await settle();
        await tick(rowChecks(noFollow.body)[0]);
        noFollow.pick('resolved');
        assert.equal(hidden(byId(noFollow.body, 'wo-reply-rest')), true, '沒有 followUp 不出現');
    },

    // 選其餘＝觀察中後送出：先送勾選的、再以 submitRest 送其餘；兩次都成功才關閉、一則合併訊息
    async restChosenSendsBothRequestsThenCloses() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const calls = [];
        let applied = 0;
        const m = openReply(loaded, {
            picker: { load: src.load },
            submit: async (payload, picked, context) => { calls.push({ kind: 'submit', payload, picked, context }); return { cases: 2 }; },
            followUp: {
                remaining: async () => 3,
                submitRest: async (payload, context) => { calls.push({ kind: 'rest', payload, context }); return { cases: 3 }; }
            },
            onApplied: () => { applied++; }
        });
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        m.pick('resolved');
        m.write('已處理完');
        const restObserving = all(byId(m.body, 'wo-reply-rest-status')).find(n => n.dataset.restStatus === 'observing');
        restObserving.click();
        assert.equal(byId(m.body, 'wo-reply-rest-due').value, expectedDate(7), '其餘選觀察中預設 7 日');
        assert.equal(hidden(byId(m.body, 'wo-reply-rest-due-quick')), false, '有 3／7／14 日快捷');
        await m.send();

        assert.deepEqual(calls.map(c => c.kind), ['submit', 'rest']);
        assert.deepEqual(calls[0].picked, ['c1', 'c2'], '第一個請求只送勾選的');
        assert.deepEqual(calls[0].payload, { status: 'resolved', note: '已處理完', dueDate: null });
        assert.deepEqual(calls[1].payload, { status: 'observing', note: '已處理完', dueDate: expectedDate(7) }, '其餘沿用同一份說明');
        assert.deepEqual([calls[0].context, calls[1].context], [{ batch: true }, { batch: true }], '兩次都交代呼叫端不出訊息');
        assert.equal(loaded.env.closed, 1, '兩次都成功才關閉');
        assert.equal(applied, 1);
        const successes = loaded.env.toasts.filter(t => t.type === 'success');
        assert.equal(successes.length, 1, '只出一則成功訊息');
        assert.ok(successes[0].message.includes('2 台') && successes[0].message.includes('3 台'), successes[0].message);
        assert.ok(successes[0].message.includes('已處理') && successes[0].message.includes('觀察中'), successes[0].message);
    },

    // 可再點一次取消；取消後與沒選一樣進入「其餘 N 台」第二步（既有行為），不先送其餘
    async restChoiceCanBeToggledOffAndFallsBackToSecondStep() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const calls = [];
        const m = openReply(loaded, {
            picker: { load: src.load },
            submit: async (payload, picked, context) => { calls.push({ kind: 'submit', context }); return { cases: 2 }; },
            followUp: { remaining: async () => 3, submitRest: async () => { calls.push({ kind: 'rest' }); return {}; } }
        });
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        m.pick('resolved');
        const chipOf = status => all(byId(m.body, 'wo-reply-rest-status')).find(n => n.dataset.restStatus === status);
        chipOf('observing').click();
        chipOf('observing').click();
        assert.deepEqual(all(byId(m.body, 'wo-reply-rest-status')).filter(n => n.classList?.contains('active')), [], '再點一次取消');
        await m.send();
        assert.deepEqual(calls.map(c => c.kind), ['submit'], '沒選其餘就只送第一個請求');
        assert.equal(calls[0].context, undefined);
        assert.equal(loaded.env.closed, 0, '彈窗留著進入第二步');
        assert.equal(textOf(m.statusLabel), '其餘 3 台要一起設為…');
    },

    // 第二個請求失敗：彈窗不關、進入 rest 模式並保留其餘結論與日期，第一部分已更新畫面
    async restRequestFailureKeepsModalInRestModeWithChoice() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        let applied = 0;
        const m = openReply(loaded, {
            picker: { load: src.load },
            submit: async () => ({ cases: 2 }),
            followUp: { remaining: async () => 3, submitRest: async () => { throw Object.assign(new Error('伺服器忙碌'), { status: 500 }); } },
            onApplied: () => { applied++; }
        });
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);
        await tick(rowChecks(m.body)[1]);
        m.pick('resolved');
        m.write('說明');
        all(byId(m.body, 'wo-reply-rest-status')).find(n => n.dataset.restStatus === 'observing').click();
        const picked = expectedDate(14);
        m.buttonByText('14 日').click();
        await m.send();

        assert.equal(loaded.env.closed, 0, '彈窗不關');
        assert.equal(applied, 1, '第一部分已寫入，先更新畫面一次');
        assert.equal(textOf(m.statusLabel), '其餘 3 台要一起設為…', '切到 rest 模式');
        assert.deepEqual(m.active(), ['observing'], '保留使用者選的其餘結論');
        assert.equal(m.due.value, picked, '保留日期');
        assert.equal(m.note.value, '說明', '保留說明');
        assert.ok(loaded.env.toasts.some(t => t.type === 'danger'), '顯示錯誤');
        assert.equal(hidden(byId(m.body, 'wo-reply-rest')), true, '舊的其餘區收起');
        // 直接再送一次：走既有 rest 模式的送出
        assert.equal(m.submitBtn.disabled, false);
    },

    // 提示行（請選擇這次的結論）的槽在選結論前後、整單／部分／其餘三種模式都保留，底部按鈕不位移
    async statusHintSlotKeepsItsLine() {
        const src = memberSource(5);
        const loaded = loadReplyModule();
        const m = openReply(loaded, {
            picker: { load: src.load },
            submit: async () => ({ cases: 1 }),
            followUp: { remaining: async () => 3, submitRest: async () => ({}) }
        });
        const slot = () => byId(m.body, 'wo-reply-status-required-slot');
        const check = label => {
            assert.ok(slot(), `${label}：槽存在`);
            assert.ok(slot().classList.contains('lf-hint-slot'), `${label}：槽帶佔位類別`);
            assert.equal(hidden(slot()), false, `${label}：槽沒被收成不佔位`);
            assert.ok(slot().children.includes(m.required), `${label}：提示在槽內`);
        };
        check('整單未選結論');
        m.pick('resolved');
        check('整單已選結論');
        m.toggle.click();
        await settle();
        check('部分模式');
        await tick(rowChecks(m.body)[0]);
        m.pick('resolved');
        check('部分且已勾選');
        await m.send();
        assert.equal(textOf(m.statusLabel), '其餘 3 台要一起設為…');
        check('其餘模式');
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
const setDetailSectionsDenied = () => { };
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
    },
    // 其餘選到不處理類：同款後果說明（複數／單數、30 天解除），收起或取消選擇時一起隱藏
    async restNoFixNoteFollowsRestChoice() {
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load: memberSource(5).load }, followUp: { remaining: async () => 3, submitRest: async () => ({}) } });
        const note = () => byId(m.body, 'wo-reply-rest-no-fix-note');
        const restChip = status => nodes(byId(m.body, 'wo-reply-rest-status'), n => n.dataset?.restStatus === status)[0];
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);
        m.pick('resolved');
        assert.equal(hidden(note()), true, '其餘還沒選：不顯示');
        restChip('resolved').click();
        assert.equal(hidden(note()), true, '其餘選已處理：不顯示');
        restChip('wont_fix').click();
        assert.equal(hidden(note()), false, '其餘選不處理：顯示後果說明');
        assert.equal(textOf(note()), loaded.noFixConsequenceText({ multiHosts: true }), '其餘 4 台：複數說法');
        assert.equal(hidden(byId(m.body, 'wo-reply-no-fix-note')), true, '主結論不是不處理類：主結論的說明不受影響');
        restChip('wont_fix').click();
        assert.equal(hidden(note()), true, '再點一次取消選擇：隱藏');
        restChip('false_positive').click();
        assert.equal(hidden(note()), false);
        m.pick('in_progress');
        assert.equal(hidden(note()), true, '其餘區收起（主結論改成非結案類）：一起隱藏');

        // 其餘剩 1 台＝單數說法；整批 30 天解除
        const one = openReply(loadReplyModule(), { picker: { load: memberSource(2).load }, dismissalReleases: true, followUp: { remaining: async () => 1, submitRest: async () => ({}) } });
        one.toggle.click();
        await settle();
        await tick(rowChecks(one.body)[0]);
        one.pick('resolved');
        nodes(byId(one.body, 'wo-reply-rest-status'), n => n.dataset?.restStatus === 'known_noise')[0].click();
        const text = textOf(byId(one.body, 'wo-reply-rest-no-fix-note'));
        assert.equal(text, loaded.noFixConsequenceText({ multiHosts: false, releases: true }));
        assert.ok(text.includes('30 天'), text);
    },

    // 主機面板載入失敗：面板內常駐一行提示（呼叫端的 load 是靜音的，沒有 toast），下一次成功載入就清掉
    async pickerLoadFailureShowsInlineErrorUntilNextSuccess() {
        const src = memberSource(5);
        const state = { fail: true };
        const load = async params => { if (state.fail) throw Object.assign(new Error('boom'), { status: 500 }); return src.load(params); };
        const loaded = loadReplyModule();
        const m = openReply(loaded, { picker: { load } });
        const errorEl = () => byId(m.body, 'wo-reply-pick-error');
        m.toggle.click();
        await settle();
        assert.equal(hidden(errorEl()), false, '第一頁就載入失敗：面板顯示提示');
        assert.ok(textOf(errorEl()).includes('載入失敗'), textOf(errorEl()));
        assert.equal(loaded.env.toasts.length, 0, '不靠 toast');

        state.fail = false;
        await m.applyBtn.listeners.click[0]();
        assert.equal(hidden(errorEl()), true, '下一次成功載入清掉提示');
        assert.equal(rowChecks(m.body).length, 5);

        // 貼上清單時查詢失敗也要出聲，而不是讓人以為沒有相符的主機
        state.fail = true;
        m.search.value = 'srv-001\nSRV-003';
        await m.applyBtn.listeners.click[0]();
        assert.equal(hidden(errorEl()), false, '貼上清單的查詢失敗：顯示提示');
        assert.equal(hidden(m.missing), true, '不誤報「找不到」');
        state.fail = false;
        await m.applyBtn.listeners.click[0]();
        assert.equal(hidden(errorEl()), true);
        assert.deepEqual(rowChecks(m.body).map(c => c.checked), [true, true]);
    },

    // 被拒（成員已非進行中）後：面板清單重載、勾選只留仍進行中的（含不在目前頁的勾選）、已選台數跟著更新
    async rejectionRefreshesPickerAndPrunesSelection() {
        const all25 = memberSource(25).items;
        const gone = new Set();
        const calls = [];
        const load = async ({ page, pageSize, hostNames }) => {
            calls.push({ page, hostNames });
            const keys = hostNames ? new Set(hostNames.map(n => n.toUpperCase())) : null;
            const matched = all25.filter(item => !gone.has(item.caseId) && (!keys || keys.has(item.hostName.toUpperCase())));
            return { items: matched.slice((page - 1) * pageSize, page * pageSize), total: matched.length, page, pageSize };
        };
        const loaded = loadReplyModule();
        let rejected = 0;
        const m = openReply(loaded, {
            picker: { load },
            followUp: { remaining: async () => 0, submitRest: async () => ({}) },
            submit: async () => { throw Object.assign(new Error('主機 SRV-001 已不是交辦單 7 的進行中成員'), { status: 400 }); },
            onRejected: () => { rejected++; }
        });
        m.toggle.click();
        await settle();
        await tick(rowChecks(m.body)[0]);                      // c1（第 1 頁）
        pagerBtn(m.body, '下一頁').click();
        await settle();
        await tick(rowChecks(m.body)[0]);                      // c21（第 2 頁）
        pagerBtn(m.body, '上一頁').click();
        await settle();
        assert.ok(m.hintText().includes('選取的 2 台'), m.hintText());

        gone.add('c1');                                         // c1 在別處被結案
        const before = calls.length;
        m.pick('resolved');
        await m.send();
        await settle();
        assert.equal(rejected, 1);
        assert.ok(calls.length > before, '被拒後面板重新載入');
        assert.equal(hidden(m.rejected), false, '被拒訊息仍常駐');
        assert.ok(m.hintText().includes('選取的 1 台'), `已移走的勾選要被拿掉：${m.hintText()}`);
        assert.ok(textOf(m.panel.find(n => n.id === 'wo-reply-pick-count')).includes('1'), '已選台數跟著更新');
        // 清單補位：c1 移走後第 1 頁是 c2…c21，只有補進來的 c21 是勾選的
        assert.deepEqual(rowChecks(m.body).map(c => c.checked), [...Array(19).fill(false), true], 'c1 的勾選已拿掉，c21 的勾選仍在');
        assert.ok(!all(m.body).some(n => n.textContent === 'SRV-001'), '已移走的成員不在清單');
        pagerBtn(m.body, '下一頁').click();
        await settle();
        assert.equal(rowChecks(m.body).some(c => c.checked), false, '第 2 頁（c22…c25）沒有勾選');
    },

    // 「送出並回覆下一張」搭配部分主機結案：單還有進行中主機時交代其餘台數，並通知呼叫端不要標成已處理
    async nextAfterPartialClosingReportsRemainder() {
        const run = async ({ remaining, partial = true, status = 'resolved' }) => {
            const loaded = loadReplyModule();
            const nexts = [];
            const applied = [];
            const m = openReply(loaded, {
                picker: { load: memberSource(5).load }, onNext: p => nexts.push(p), onApplied: info => applied.push(info),
                submit: async () => ({ cases: 2, workOrderClosed: false }),
                followUp: { remaining: async () => remaining, submitRest: async () => ({}) }
            });
            if (partial) {
                m.toggle.click();
                await settle();
                await tick(rowChecks(m.body)[0]);
                await tick(rowChecks(m.body)[1]);
            }
            m.pick(status);
            await fire(m.buttonByText('送出並回覆下一張'), 'click');
            await settle();
            await fire(m.entry.element, 'hidden.bs.modal');   // 彈窗完全關閉後才開下一張
            return { loaded, nexts, applied };
        };
        const left = await run({ remaining: 3 });
        assert.equal(left.loaded.env.closed, 1, '不停下來問其餘，直接關閉去下一張');
        assert.equal(left.nexts.length, 1);
        assert.deepEqual(left.applied, [{ restRemaining: 3 }], '告訴呼叫端這張單還有 3 台進行中');
        assert.ok(left.loaded.env.toasts.some(t => t.message.includes('其餘 3 台尚未處理')), JSON.stringify(left.loaded.env.toasts));

        const none = await run({ remaining: 0 });
        assert.deepEqual(none.applied, [undefined], '沒有剩的：照常');
        assert.ok(!none.loaded.env.toasts.some(t => t.message.includes('尚未處理')));
        const open = await run({ remaining: 3, status: 'in_progress' });
        assert.deepEqual(open.applied, [undefined], '非結案類不提示（單本來就還在進行）');
        const whole = await run({ remaining: 3, partial: false });
        assert.deepEqual(whole.applied, [undefined], '回覆全部進行中主機：沒有「其餘」');
    },

    // 復發提示的計數單位：一般入口用「台」，主機詳情「這台的交辦」（多個問題）用「個問題」
    recurrenceUnitFollowsEntry() {
        const text = options => textOf(byId(openReply(loadReplyModule(), options).body, 'wo-reply-recurrence'));
        assert.ok(text({ recurrence: { hosts: 2 } }).includes('其中 2 台是反覆發生的'), '一般入口以台計');
        assert.ok(text({ recurrence: { hosts: 2 }, multiIssues: true }).includes('其中 2 個問題是反覆發生的'));
        assert.ok(text({ recurrence: { hosts: 1, unit: 'issue' } }).includes('其中 1 個問題是反覆發生的'), '只勾一個問題也以問題計');
    },

    // 草稿識別：GUID 字串以字串排序，同一批成員不論勾選順序都對到同一份草稿
    caseIdsDraftKeyIgnoresPickOrder() {
        const { caseIdsDraftKey } = loadReplyModule();
        const ids = ['b1f0-guid', 'a0c3-guid', 'c7d9-guid'];
        assert.equal(caseIdsDraftKey(7, ids), caseIdsDraftKey(7, [...ids].reverse()));
        assert.equal(caseIdsDraftKey(7, ids), 'order:7:cases:a0c3-guid,b1f0-guid,c7d9-guid');
        assert.equal(caseIdsDraftKey(7, []), 'order:7:all', '空陣列＝整單');
        assert.equal(caseIdsDraftKey(7, [5, 3]), 'order:7:cases:3,5');
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
