/**
 * 回覆流程（批次 E-1）的行為測試：回覆彈窗（結論按鈕、不預選、觀察中快捷、成功訊息、鍵盤、被拒後的常駐訊息與草稿）、
 * 交辦單頁的入口與被拒重載、「清單有更新」提示與徽章事件。
 *
 * 實際執行 production 程式：issue-status-reply.js 整份載入（見 load-reply-modal.mjs），
 * work-order-detail.js／badges.js／list-update-notice.js 切出函式執行，只替換 DOM 與 API 邊界。
 * 我的交辦列上「回覆」的入口案例在 handler-reply-carry.behavior.mjs（那裡已有 handler-detail.js 的切函式環境）。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { loadReplyModule, openReply, read, extract, fire, all, byId, hidden, textOf, deferred, catalog } from './load-reply-modal.mjs';

const REPLYABLE = catalog.statusesWhere(s => s.replyable).map(s => s.value);

/** 獨立於 production dateAfterDays 的預期值：今天起第 N 天的本機日期 */
function expectedDate(days) {
    const d = new Date();
    d.setDate(d.getDate() + days);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

const rejection = (status, message) => Object.assign(new Error(message), { status });

const cases = {
    // 不預選：沒選結論時送出鈕停用、顯示原因；選處理中時鈕文字直說不結案
    // X3：送出鈕在頁尾、與「關閉」同列；表單內不留送出鈕，頁尾按鈕以 form 屬性關聯表單
    async submitButtonsLiveInFooterBesideClose() {
        const view = openReply(loadReplyModule(), { onNext: () => { } });
        const footerButtons = view.entry.footer.children.filter(n => n.tagName === 'BUTTON');
        assert.deepEqual(footerButtons.map(n => n.textContent.replace(/\s+/g, '')), ['關閉', '送出並回覆下一張', '送出'], '頁尾依序是關閉、送出並回覆下一張、送出');
        assert.equal(view.submitBtn.type, 'submit');
        assert.ok(view.entry.footer.children.includes(view.submitBtn), '送出鈕在頁尾');
        assert.equal(view.submitBtn.getAttribute('form'), view.form.id, '以 form 屬性關聯表單');
        assert.ok(view.form.id, '表單要有 id');
        const inForm = all(view.form).filter(n => n.tagName === 'BUTTON' && n.type === 'submit');
        assert.equal(inForm.length, 0, '表單內不留送出鈕');
        // 搬到頁尾後送出路徑不變：選結論、送出表單 → 呼叫 submit
        view.pick('resolved');
        await view.send();
        assert.equal(view.submits.length, 1);
        // 沒有「送出並回覆下一張」時，頁尾只有關閉與送出
        const plain = openReply(loadReplyModule());
        assert.deepEqual(plain.entry.footer.children.filter(n => n.tagName === 'BUTTON').map(n => n.textContent.replace(/\s+/g, '')), ['關閉', '送出']);
    },

    async noConclusionDisablesSubmit() {
        const loaded = loadReplyModule();
        const m = openReply(loaded);
        assert.deepEqual(m.active(), [], '預設不選任何結論');
        assert.equal(m.chips.length, REPLYABLE.length, '每個可回覆結論一顆按鈕');
        assert.equal(m.submitBtn.disabled, true, '沒選結論送出鈕停用');
        assert.equal(hidden(m.required), false, '停用時顯示原因');
        assert.equal(textOf(m.required), '請選擇這次的結論');
        await m.send();
        assert.equal(m.submits.length, 0, '沒選結論硬送（Enter）也不送出');

        m.pick('in_progress');
        assert.equal(m.submitBtn.disabled, false);
        assert.equal(textOf(m.submitBtn), '更新說明（不結案）');
        assert.equal(hidden(m.required), true, '選了結論就不再顯示原因');
        m.pick('resolved');
        assert.equal(textOf(m.submitBtn), '送出', '其他結論的鈕文字是「送出」');
        assert.deepEqual(m.active(), ['resolved'], '單選：只有一顆是選取狀態');
        assert.equal(m.chip('resolved').getAttribute('aria-pressed'), 'true');
        assert.equal(m.chip('in_progress').getAttribute('aria-pressed'), 'false');

        // 「送出並回覆下一張」沿用上一張結論：initialStatus 仍有效，打開就已選
        const carried = openReply(loaded, { initialStatus: 'wont_fix', onNext: () => { } });
        assert.deepEqual(carried.active(), ['wont_fix']);
        assert.equal(carried.submitBtn.disabled, false);
        assert.equal(textOf(carried.buttonByText('送出並回覆下一張')), '送出並回覆下一張');
        const nextBtn = carried.buttonByText('送出並回覆下一張');
        assert.equal(nextBtn.disabled, false);
        const emptyNext = openReply(loaded, { onNext: () => { } });
        assert.equal(emptyNext.buttonByText('送出並回覆下一張').disabled, true, '沒選結論時「下一張」也停用');
        // 值域外的初始結論不預選
        assert.deepEqual(openReply(loaded, { initialStatus: 'open' }).active(), []);
    },

    // 觀察中：預設 7 日自動填入；3／14 日快捷；換走時收回自動填入的日期
    async observingDefaultsToSevenDaysWithQuickButtons() {
        const loaded = loadReplyModule();
        const m = openReply(loaded);
        assert.equal(hidden(m.quick), true, '沒選觀察中不顯示快捷');
        m.pick('observing');
        assert.equal(hidden(m.quick), false);
        assert.equal(m.due.value, expectedDate(7), '預設觀察 7 日');
        assert.deepEqual(all(m.quick).filter(n => n.tagName === 'BUTTON').map(textOf), ['3 日', '7 日', '14 日']);

        m.buttonByText('3 日').click();
        assert.equal(m.due.value, expectedDate(3), '3 日快捷＝今天＋3');
        m.buttonByText('14 日').click();
        assert.equal(m.due.value, expectedDate(14));
        m.buttonByText('7 日').click();
        assert.equal(m.due.value, expectedDate(7));

        // 自動填入的預設沒被動過：換成別的結論時收回，不把觀察日期帶進「處理中」
        const other = openReply(loaded);
        other.pick('observing');
        other.pick('in_progress');
        assert.equal(other.due.value, '', '自動填入的觀察日期換走時收回');
        assert.equal(hidden(other.quick), true, '處理中不顯示觀察快捷');
        // 使用者手動選過日期就保留
        const manual = openReply(loaded);
        manual.pick('observing');
        manual.due.value = expectedDate(30);
        manual.due.dispatch('input');
        manual.pick('in_progress');
        assert.equal(manual.due.value, expectedDate(30), '手動選的日期不被收回');

        // 初始就是觀察中（下一張沿用）也一樣預設 7 日
        assert.equal(openReply(loaded, { initialStatus: 'observing' }).due.value, expectedDate(7));

        // 送出帶 7 日
        const sent = openReply(loaded);
        sent.pick('observing');
        await sent.send();
        assert.deepEqual(sent.submits, [{ status: 'observing', note: null, dueDate: expectedDate(7) }]);
    },

    // 上報後寫明通知了幾位管理員；沒有可通知的人時寫明已列入交辦總覽提醒；沒帶數字（不是新上報）不多說
    async escalationMessageNamesNotifiedAdmins() {
        const loaded = loadReplyModule();
        const say = (result, many = false) => {
            loaded.env.toasts.length = 0;
            (many ? loaded.toastReplyManyResult : loaded.toastReplyResult)(result, { status: 'escalated', note: 'x' });
            return loaded.env.toasts[0].message;
        };
        assert.equal(say({ cases: 3, escalationNotifiedAdmins: 2 }), '3 台已上報管理者，已通知 2 位管理員');
        assert.equal(say({ cases: 3, escalationNotifiedAdmins: 0 }), '3 台已上報管理者，目前沒有可通知的管理員，已列入交辦總覽提醒');
        assert.equal(say({ cases: 3, escalationNotifiedAdmins: null }), '3 台已上報管理者');
        assert.equal(say({ workOrders: 2, cases: 5, escalationNotifiedAdmins: 1 }, true), '2 張單共 5 台已上報管理者，已通知 1 位管理員');
        // 非上報狀態即使帶了數字也不提
        loaded.env.toasts.length = 0;
        loaded.toastReplyResult({ cases: 1, escalationNotifiedAdmins: 2 }, { status: 'resolved' });
        assert.equal(loaded.env.toasts[0].message, '已回覆 1 台為已處理');
    },

    // 成功訊息寫出結論（三個呼叫端共用 replyOutcomeText／toastReply*Result）
    async successMessagesNameTheConclusion() {
        const loaded = loadReplyModule();
        const say = (result, payload, many = false) => {
            loaded.env.toasts.length = 0;
            (many ? loaded.toastReplyManyResult : loaded.toastReplyResult)(result, payload);
            return loaded.env.toasts[0].message;
        };
        assert.equal(say({ cases: 20 }, { status: 'resolved' }), '已回覆 20 台為已處理');
        assert.equal(say({ cases: 2 }, { status: 'observing', dueDate: '2026-12-01' }), '2 台改為觀察中，至 12-01');
        assert.equal(say({ cases: 2 }, { status: 'in_progress' }), '已更新 2 台的說明（仍為處理中）');
        assert.equal(say({ cases: 3 }, { status: 'escalated', note: 'x' }), '3 台已上報管理者');
        assert.equal(say({ cases: 1 }, { status: 'wont_fix' }), '已回覆 1 台為不處理');
        assert.equal(say({ cases: 1 }, { status: 'false_positive' }), '已回覆 1 台為誤報');
        assert.equal(say({ cases: 1 }, { status: 'known_noise' }), '已回覆 1 台為已知雜訊');
        // 整單結案：保留原有的後綴
        assert.equal(say({ cases: 20, workOrderClosed: true }, { status: 'resolved' }), '已回覆 20 台為已處理，整張單已結案');
        // 多張單：沿用「其中 N 張結案」
        assert.equal(say({ workOrders: 3, cases: 20, closedWorkOrders: 1 }, { status: 'resolved' }, true), '已回覆 3 張單共 20 台為已處理，其中 1 張結案');
        assert.equal(say({ workOrders: 2, cases: 5, closedWorkOrders: 0 }, { status: 'observing', dueDate: '2026-12-01' }, true), '2 張單共 5 台改為觀察中，至 12-01');
        // 背景同步提示照舊
        loaded.env.toasts.length = 0;
        loaded.toastReplyResult({ cases: 1, daySyncPendingCases: 2 }, { status: 'resolved' });
        assert.deepEqual(loaded.env.toasts.map(t => t.message), ['已回覆 1 台為已處理', '逐日同步在背景進行']);
        // 部分失敗的警告不變
        loaded.env.toasts.length = 0;
        loaded.toastReplyManyResult({ failedWorkOrderId: 9, failureMessage: '壞了', succeeded: [1], notProcessed: [2, 3] }, { status: 'resolved' });
        assert.equal(loaded.env.toasts[0].type, 'warning');
    },

    // 鍵盤：方向鍵切換結論、Ctrl／⌘＋Enter 送出、Esc 後草稿仍在
    async keyboardArrowKeysCtrlEnterAndEscKeepDraft() {
        const loaded = loadReplyModule();
        const m = openReply(loaded);
        const key = async (name, extra = {}) => { const ev = await fire(m.group, 'keydown', { key: name, ...extra }); return ev; };

        // 沒選時：右／下＝第一個、左／上＝最後一個
        await key('ArrowRight');
        assert.deepEqual(m.active(), [REPLYABLE[0]]);
        const fresh = openReply(loaded);
        await fire(fresh.group, 'keydown', { key: 'ArrowLeft' });
        assert.deepEqual(fresh.active(), [REPLYABLE.at(-1)], '沒選時左鍵取最後一個');

        m.pick('resolved');
        const index = REPLYABLE.indexOf('resolved');
        const ev = await key('ArrowRight');
        assert.equal(ev.defaultPrevented, true, '方向鍵不捲動頁面');
        assert.deepEqual(m.active(), [REPLYABLE[index + 1]]);
        await key('ArrowDown');
        assert.deepEqual(m.active(), [REPLYABLE[index + 2]]);
        await key('ArrowLeft');
        await key('ArrowUp');
        assert.deepEqual(m.active(), [REPLYABLE[index]], '往回切換');
        assert.equal(m.chip(REPLYABLE[index]).focused, true, '焦點跟著選取走');
        // 頭尾相接
        m.pick(REPLYABLE.at(-1));
        await key('ArrowRight');
        assert.deepEqual(m.active(), [REPLYABLE[0]]);
        await key('ArrowLeft');
        assert.deepEqual(m.active(), [REPLYABLE.at(-1)]);
        // 其他鍵不動作
        const before = m.active();
        const other = await key('a');
        assert.equal(other.defaultPrevented, undefined);
        assert.deepEqual(m.active(), before);

        // Ctrl＋Enter／⌘＋Enter：說明欄內送出
        const s = openReply(loaded);
        s.pick('resolved');
        s.write('已重開機');
        const enter = await fire(s.note, 'keydown', { key: 'Enter', ctrlKey: true });
        assert.equal(enter.defaultPrevented, true);
        assert.deepEqual(s.submits, [{ status: 'resolved', note: '已重開機', dueDate: null }]);
        const mac = openReply(loaded);
        mac.pick('resolved');
        await fire(mac.note, 'keydown', { key: 'Enter', metaKey: true });
        assert.equal(mac.submits.length, 1, '⌘＋Enter 也送出');
        // 單按 Enter（換行）、輸入法組字中、沒選結論都不送出
        const guard = openReply(loaded);
        guard.write('x');
        await fire(guard.note, 'keydown', { key: 'Enter', ctrlKey: true });
        assert.equal(guard.submits.length, 0, '沒選結論不送出');
        guard.pick('resolved');
        await fire(guard.note, 'keydown', { key: 'Enter' });
        await fire(guard.note, 'keydown', { key: 'Enter', ctrlKey: true, isComposing: true });
        assert.equal(guard.submits.length, 0, '單按 Enter 與組字中的 Ctrl＋Enter 不送出');

        // Esc（hide.bs.modal）：說明欄有字先問；放棄關閉後草稿仍在
        const closedBefore = loaded.env.closed;
        const esc = openReply(loaded, { draftKey: 'order:9:all' });
        esc.write('寫到一半的說明');
        const hideEvent = await fire(esc.entry.element, 'hide.bs.modal');
        assert.equal(hideEvent.defaultPrevented, true, '有字時先攔下並詢問');
        assert.equal(loaded.env.confirms, 1);
        assert.equal(loaded.env.closed, closedBefore + 1, '確認後關閉');
        assert.equal(loaded.env.drafts['wo-reply:order:9:all'], '寫到一半的說明', '關閉後草稿仍在');
        // 說明欄空的 Esc 直接關閉、不詢問
        const empty = openReply(loaded);
        const emptyEvent = await fire(empty.entry.element, 'hide.bs.modal');
        assert.equal(emptyEvent.defaultPrevented, undefined);
    },

    // 被拒（403 單已改派）：訊息常駐、含新處理人、草稿保留、可複製說明、通知呼叫端重載
    async rejected403KeepsMessageDraftAndOffersCopy() {
        const loaded = loadReplyModule();
        const calls = [];
        const m = openReply(loaded, {
            draftKey: 'order:7:all',
            submit: async () => { throw rejection(403, '交辦單 #7 不是指派給您的，無法回覆。'); },
            onRejected: async error => { calls.push(error.status); return '這張單剛改派給 h-chen，你的說明仍保留在草稿'; },
            onApplied: () => calls.push('applied')
        });
        m.pick('resolved');
        m.write('已處理好但單被改派');
        await m.send();

        assert.deepEqual(calls, [403], '通知呼叫端重載，且不算成功');
        assert.equal(hidden(m.rejected), false, '被拒訊息顯示在彈窗');
        const text = textOf(m.rejected);
        assert.ok(text.includes('h-chen') && text.includes('你的說明仍保留在草稿'), text);
        assert.ok(!text.includes('不是指派給您'), '改寫後取代伺服器原文');
        assert.equal(m.rejected.find(n => n.getAttribute?.('role') === 'alert') !== null, true, '用 role=alert');
        assert.equal(loaded.env.toasts.length, 0, '常駐訊息不是 toast');
        assert.equal(loaded.env.drafts['wo-reply:order:7:all'], '已處理好但單被改派', '草稿保留');
        assert.equal(m.note.value, '已處理好但單被改派', '說明欄內容保留');
        assert.equal(loaded.env.closed, 0, '彈窗不關');
        assert.equal(m.submitBtn.disabled, false, '送出鈕還原');
        assert.equal(hidden(m.copyBtn), false, '403 才提供複製說明');

        // 複製說明：成功
        await fire(m.copyBtn, 'click');
        assert.deepEqual(loaded.env.copied, ['已處理好但單被改派']);
        assert.equal(loaded.env.toasts.at(-1).message, '已複製說明');
        // 複製失敗：選取文字讓使用者自己複製
        const failing = loadReplyModule({ copyResult: { ok: false, reason: '瀏覽器不允許存取剪貼簿，請手動選取文字複製' } });
        const f = openReply(failing, { submit: async () => { throw rejection(403, 'x'); } });
        f.pick('resolved');
        f.write('留底');
        await f.send();
        await fire(f.copyBtn, 'click');
        assert.equal(f.note.selected, true, '失敗時選取說明欄文字');
        assert.equal(f.note.focused, true);
        assert.equal(failing.env.toasts.at(-1).type, 'warning');

        // 訊息要使用者按「關閉訊息」才消失
        const dismiss = m.buttonByText('關閉訊息');
        assert.ok(dismiss, '常駐訊息附關閉鈕');
        await fire(dismiss, 'click');
        assert.equal(hidden(m.rejected), true);
    },

    // 被拒（400 單已結案／成員已非進行中）：照現行訊息常駐顯示、不給複製、通知重載、草稿保留；其他錯誤不當成被拒
    async rejected400AndOtherErrors() {
        const loaded = loadReplyModule();
        const seen = [];
        const m = openReply(loaded, {
            draftKey: 'k400',
            submit: async () => { throw rejection(400, '這張單已結案，無法回覆。'); },
            onRejected: error => { seen.push(error.message); }
        });
        m.pick('resolved');
        m.write('草稿');
        await m.send();
        assert.deepEqual(seen, ['這張單已結案，無法回覆。']);
        assert.equal(hidden(m.rejected), false);
        assert.ok(textOf(m.rejected).includes('這張單已結案，無法回覆。'), '沒有改寫時顯示伺服器訊息');
        assert.equal(hidden(m.copyBtn), true, '400 不顯示複製說明');
        assert.equal(loaded.env.drafts['wo-reply:k400'], '草稿');
        assert.equal(loaded.env.toasts.length, 0);

        // 伺服器錯誤：回覆請求是靜音的，由彈窗補 toast；不顯示常駐被拒訊息、不通知重載
        const other = loadReplyModule();
        let rejected = 0;
        const o = openReply(other, { submit: async () => { throw rejection(500, '系統發生未預期的錯誤'); }, onRejected: () => { rejected++; } });
        o.pick('resolved');
        await o.send();
        assert.equal(rejected, 0);
        assert.equal(hidden(o.rejected), true);
        assert.deepEqual(other.env.toasts.map(t => [t.message, t.type]), [['系統發生未預期的錯誤', 'danger']]);

        // 沒有狀態碼的錯誤（例如 reply-many 部分失敗的 partial）：呼叫端已出過訊息，這裡不重複
        const partial = loadReplyModule();
        const p = openReply(partial, { submit: async () => { throw new Error('partial'); } });
        p.pick('resolved');
        await p.send();
        assert.equal(partial.env.toasts.length, 0);
        assert.equal(hidden(p.rejected), true);
        assert.equal(p.submitBtn.disabled, false, '失敗後可再送出');
    },

    // 單張單被拒的說明：403 重新讀單取新處理人；讀不到就不指名；400 沿用伺服器訊息
    async describeRejectionNamesNewHandler() {
        const named = loadReplyModule({ apiGet: async () => ({ handlerName: 'h-chen' }) });
        const text = await named.describeReplyRejection(rejection(403, 'x'), 7);
        assert.equal(text, '這張單剛改派給 h-chen，你的說明仍保留在草稿');
        assert.deepEqual(named.env.gets, ['/api/work-orders/7']);
        const unknown = loadReplyModule({ apiGet: async () => { throw rejection(403, '無權'); } });
        assert.equal(await unknown.describeReplyRejection(rejection(403, 'x'), 7), '這張單剛改派給 其他處理人，你的說明仍保留在草稿');
        assert.equal(await named.describeReplyRejection(rejection(400, 'x'), 7), undefined, '400 不改寫');
        assert.equal(named.env.gets.length, 1, '400 不讀單');
    },

    // 送出成功：清草稿、關彈窗、呼叫 onApplied；觀察至之外的結論不送無關日期
    async successClearsDraftClosesAndApplies() {
        const loaded = loadReplyModule();
        let applied = 0;
        const m = openReply(loaded, { draftKey: 'ok', onApplied: () => { applied++; } });
        m.pick('resolved');
        m.write('好了');
        assert.equal(loaded.env.drafts['wo-reply:ok'], '好了');
        await m.send();
        assert.deepEqual(m.submits, [{ status: 'resolved', note: '好了', dueDate: null }]);
        assert.equal(loaded.env.drafts['wo-reply:ok'], undefined, '成功後清草稿');
        assert.equal(loaded.env.closed, 1);
        assert.equal(applied, 1);
        // 不處理類必填說明照舊
        const nf = openReply(loaded);
        nf.pick('wont_fix');
        await nf.send();
        assert.equal(nf.submits.length, 0, '不處理必填說明');
        assert.ok(loaded.env.fieldErrors.some(t => t.includes('不處理')));
    },

    // 交辦單頁：整單回覆 caseIds 為空且靜音、被拒時重載整頁並回傳含新處理人的說明
    async workOrderPageReplyEntryAndRejection() {
        const dom = installDom();
        void dom;
        const loaded = loadReplyModule({ apiGet: async () => ({ handlerName: 'h-chen' }) });
        const source = read('pages', 'work-order-detail.js');
        const fn = extract(source, 'function openMemberReply(');
        const opened = [];
        const posts = [];
        let reloads = 0;
        const api = { post: async (url, body, options) => { posts.push({ url, body, options }); return { cases: 20 }; } };
        const run = new Function('openWorkOrderReplyModal', 'workOrderId', 'currentDetail', 'api', 'toastReplyResult', 'describeReplyRejection', 'reloadAll',
            `${fn}\nreturn openMemberReply;`)(
            options => opened.push(options), 7, { issueLabel: '問題', issueKey: 'k' }, api, loaded.toastReplyResult,
            (error, id) => loaded.describeReplyRejection(error, id), async () => { reloads++; });

        run([], '全部進行中主機（20 台）', 20);
        const modal = opened[0];
        assert.equal(modal.targetText, '全部進行中主機（20 台）');
        assert.equal(modal.multiTarget, true);
        assert.equal(modal.draftKey, 'order:7:all');
        assert.equal(modal.aiContext.hostCount, 20);
        await modal.submit({ status: 'resolved', note: null, dueDate: null });
        assert.deepEqual(posts[0].body.caseIds, [], '整單回覆 caseIds 為空');
        assert.equal(posts[0].url, '/api/work-orders/7/reply');
        assert.equal(posts[0].options.silent, true, '被拒訊息由彈窗常駐顯示，不再出 toast');
        assert.equal(loaded.env.toasts[0].message, '已回覆 20 台為已處理');

        const message = await modal.onRejected(rejection(403, 'x'));
        assert.equal(reloads, 1, '被拒後重載整頁（單頭、成員、時間軸）');
        assert.equal(message, '這張單剛改派給 h-chen，你的說明仍保留在草稿');

        // 指定主機的呼叫（成員表勾選）仍帶 caseIds
        run([5, 3], '本單 2 台');
        assert.equal(opened[1].draftKey, 'order:7:cases:3,5');
        await opened[1].submit({ status: 'resolved', note: null, dueDate: null });
        assert.deepEqual(posts[1].body.caseIds, [5, 3]);
    },

    // 交辦單頁入口：0 台提示、1 台單台、多台開整單彈窗（不再只捲到成員表）
    async workOrderPageActionOpensModalForAllCounts() {
        const env = loadReplyModule();
        const source = read('pages', 'work-order-detail.js');
        const fn = extract(source, 'function renderActions(');
        const opened = [];
        const toasts = [];
        const actionsContainer = document.createElement('div');
        const button = loadButton(env.env.dom);
        const api = { get: async url => (url.includes('/members?') ? { items: [{ caseId: 55, hostName: 'HOST-D' }] } : {}), post: async () => ({}) };
        const make = counts => {
            actionsContainer.replaceChildren();
            const run = new Function('actionsContainer', 'button', 'toast', 'api', 'workOrderId', 'openMemberReply', 'hasCapability', 'replyableCount', 'appUrl', 'location',
                `${fn}\nreturn renderActions;`)(
                actionsContainer, button, (m, t) => toasts.push({ m, t }), api, 7, (...args) => opened.push(args), () => false, catalog.replyableCount, p => p, {});
            run({ viewerCanReply: true, viewerCanAssign: false, closedAt: null, handlerId: 1, counts: { total: counts.active, ...counts } }, {});
            return all(actionsContainer).find(n => n.tagName === 'BUTTON' && textOf(n).includes('回覆這張單'));
        };
        await fire(make({ active: 20 }), 'click');
        assert.deepEqual(opened.at(-1), [[], '全部進行中主機（20 台）', 20], '20 台開整單彈窗、caseIds 為空');
        await fire(make({ active: 1 }), 'click');
        assert.deepEqual(opened.at(-1).slice(0, 2), [[55], '主機 HOST-D']);
        const before = opened.length;
        await fire(make({ active: 0 }), 'click');
        assert.equal(opened.length, before, '0 台不開彈窗');
        assert.equal(toasts.at(-1).m, '這張單已沒有進行中的主機');
    },

    // 徽章事件帶出我的進行中單數（供「清單有更新」比對台數或單數）
    async badgeEventCarriesMemberAndOrderCounts() {
        installDom();
        const source = read('core', 'badges.js');
        const fns = ['async function refreshBadges(', 'async function loadMyWorkBadge(', 'async function loadUnreadBadge(', 'function setBadge(']
            .map(d => extract(source, d)).join('\n');
        const events = [];
        class FakeCustomEvent { constructor(type, init) { this.type = type; this.detail = init.detail; } }
        const links = {
            'my-work': { querySelector: () => null, appendChild() { }, dataset: {} },
            'unread-replies': { querySelector: () => null, appendChild() { }, dataset: {} }
        };
        const make = summary => new Function('api', 'hasCapability', 'document', 'window', 'CustomEvent',
            `const lastKnown = { myWork: null, myWorkOrders: null, unread: null };\nlet refreshing = false;\n${fns}\nreturn refreshBadges;`)(
            { get: async url => (url.includes('badge') ? summary : { count: 2 }) }, () => true,
            { querySelector: sel => links[/"([^"]+)"/.exec(sel)[1]], createElement: () => ({ classList: { toggle() { } } }) },
            { dispatchEvent: e => events.push(e) }, FakeCustomEvent);
        await make({ activeMembers: 20, activeWorkOrders: 3, overdueMembers: 0, unrepliedWorkOrders: 0 })({ userId: 1 });
        assert.equal(events[0].type, 'lf:badges');
        assert.deepEqual(events[0].detail, { myWork: 20, myWorkOrders: 3, unread: 2 });
    },

    // 未讀徽章的提示文字：有上報時帶「其中上報 M 張」，沒有時維持原文
    async unreadBadgeTitleMentionsEscalated() {
        installDom();
        const source = read('core', 'badges.js');
        const fns = ['async function loadUnreadBadge(', 'function setBadge('].map(d => extract(source, d)).join('\n');
        const titles = [];
        const link = {
            querySelector: () => null, dataset: {},
            appendChild(node) { node.title = ''; this.badge = node; }
        };
        const run = async payload => {
            titles.length = 0;
            const build = new Function('api', 'hasCapability', 'document', `${fns}\nreturn loadUnreadBadge;`);
            const fn = build({ get: async () => payload }, () => true, {
                querySelector: () => link,
                createElement: () => ({ classList: { toggle() { } }, setAttribute(k, v) { if (k === 'title') titles.push(v); }, set title(v) { titles.push(v); }, className: '', textContent: '' })
            });
            const count = await fn({ userId: 1 });
            return { count, title: titles.at(-1) };
        };
        const withEsc = await run({ count: 4, escalated: 2 });
        assert.equal(withEsc.count, 4);
        assert.equal(withEsc.title, '4 張交辦單有處理人的新回覆（其中上報 2 張）');
        const without = await run({ count: 3, escalated: 0 });
        assert.equal(without.title, '3 張交辦單有處理人的新回覆');
        const legacy = await run({ count: 3 });
        assert.equal(legacy.title, '3 張交辦單有處理人的新回覆', '舊伺服器沒有 escalated 欄位：維持原文');
    },

    // M1：未讀回覆徽章只對具 Assign 的人顯示（已讀只有 Assign 會寫，ViewAll 的人看到的數字永遠不會歸零）；
    // 沒有 Assign 的人不送請求（不產生被拒或無意義的請求）。能力組合照 RoleCapabilityMap 的 Manager／Dev／Admin。
    async unreadBadgeOnlyForAssign() {
        installDom();
        const source = read('core', 'badges.js');
        const fns = ['async function loadUnreadBadge(', 'function setBadge('].map(d => extract(source, d)).join('\n');
        const roles = {
            Manager: ['ViewAll'],
            Dev: ['ViewAll', 'DevMonitor'],
            Admin: ['ViewAll', 'Handle', 'Assign', 'ConfirmPermission', 'Maintain', 'DevMonitor', 'ViewAudit']
        };
        const run = async role => {
            let calls = 0;
            const link = { querySelector: () => null, dataset: {}, appendChild() { } };
            const build = new Function('api', 'hasCapability', 'document', `${fns}\nreturn loadUnreadBadge;`);
            const fn = build({ get: async () => { calls++; return { count: 3, escalated: 0 }; } },
                (user, cap) => user.caps.includes(cap),
                { querySelector: () => link, createElement: () => ({ classList: { toggle() { } }, setAttribute() { }, set title(v) { }, className: '', textContent: '' }) });
            const count = await fn({ userId: 1, caps: roles[role] });
            return { count, calls };
        };
        assert.deepEqual(await run('Manager'), { count: null, calls: 0 }, 'Manager 不顯示也不送請求');
        assert.deepEqual(await run('Dev'), { count: null, calls: 0 }, 'Dev 不顯示也不送請求');
        assert.deepEqual(await run('Admin'), { count: 3, calls: 1 }, 'Admin 顯示');
    },

    // 「清單有更新」：數字與載入時不同才出現、相同不出現；自己重載後以新數字為基準
    async listUpdateNoticeAppearsOnlyWhenCountsDiffer() {
        const dom = installDom();
        const { statusNote } = loadElements(dom);
        const button = loadButton(dom);
        let listeners = [];
        globalThis.window = { addEventListener: (type, handler) => listeners.push({ type, handler }) };
        const source = read('core', 'list-update-notice.js')
            .replace(/^import .*;$/gm, '').replace(/^export /gm, '');
        const make = initial => {
            const mine = listeners = [];   // 每個實例各自的訂閱清單（window 替身讀的是最新一份）
            const parent = dom.newElement('div');
            const anchor = dom.newElement('div');
            parent.appendChild(anchor);
            let reloads = 0;
            const notice = new Function('statusNote', 'button', 'currentBadgeCounts', `${source}\nreturn createListUpdateNotice;`)(
                statusNote, button, () => initial)({ anchor, onReload: () => { reloads++; } });
            const box = parent.children[0];
            const emit = detail => mine.filter(l => l.type === 'lf:badges').forEach(l => l.handler({ detail }));
            return { notice, box, emit, reloads: () => reloads, shown: () => !hidden(box), parent };
        };

        // 載入時徽章已取過：直接以它為基準
        const a = make({ myWork: 20, myWorkOrders: 3, unread: 0 });
        assert.equal(a.shown(), false, '一開始不顯示');
        assert.equal(a.parent.children.indexOf(a.box) < a.parent.children.indexOf(a.parent.children[1]), true, '放在錨點前面（頁面頂端）');
        a.emit({ myWork: 20, myWorkOrders: 3, unread: 5 });
        assert.equal(a.shown(), false, '台數與單數都相同：不出現（未讀數變動與這個提示無關）');
        a.emit({ myWork: 21, myWorkOrders: 3 });
        assert.equal(a.shown(), true, '台數不同：出現');
        assert.ok(textOf(a.box).includes('清單有更新') && textOf(a.box).includes('重新載入'));
        a.emit({ myWork: 20, myWorkOrders: 3 });
        assert.equal(a.shown(), false, '又回到原數字：隱藏');
        a.emit({ myWork: 20, myWorkOrders: 4 });
        assert.equal(a.shown(), true, '單數不同也出現');

        // 數字取不到（沒有 Handle、徽章失敗）：不提示也不改狀態
        a.emit({ myWork: null, myWorkOrders: null });
        assert.equal(a.shown(), true);
        const none = make({ myWork: null, myWorkOrders: null });
        none.emit({ myWork: 5, myWorkOrders: 1 });
        assert.equal(none.shown(), false, '載入時還沒有基準：第一次事件只記基準');
        none.emit({ myWork: 6, myWorkOrders: 1 });
        assert.equal(none.shown(), true);

        // 按「重新載入」：呼叫重載、提示收起、下一次事件重設基準
        const reloadBtn = all(a.box).find(n => n.tagName === 'BUTTON');
        await fire(reloadBtn, 'click');
        assert.equal(a.reloads(), 1);
        assert.equal(a.shown(), false);
        a.emit({ myWork: 20, myWorkOrders: 4 });
        assert.equal(a.shown(), false, '重載後第一個事件是新基準');
        a.emit({ myWork: 19, myWorkOrders: 4 });
        assert.equal(a.shown(), true);
        // 頁面自己重載（回覆成功）呼叫 rebase：自己造成的變動不算別人的更新
        a.notice.rebase();
        assert.equal(a.shown(), false);
        a.emit({ myWork: 10, myWorkOrders: 2 });
        assert.equal(a.shown(), false);
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
