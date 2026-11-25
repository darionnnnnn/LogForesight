/**
 * 階段 R-2：復發與觀察分流的畫面（前端）行為測試。
 *
 * 台數口徑（core/format.js 的 hostCountText）、交辦單頁單頭／成員表／一鍵結案（work-order-detail.js）、
 * 我的交辦清單列／KPI／預設篩選（handler-detail.js）、回覆彈窗的復發建議（issue-status-reply.js）。
 * 一律切出 production 函式本體執行，只替換 DOM 與 API／UI 邊界；由 RecurrenceUiBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { loadReplyModule, openReply, read, extract, byId, all, textOf, hidden, catalog, fire } from './load-reply-modal.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const webRoot = path.join(repoRoot, 'LogForesight.Web');

const formatSource = read('core', 'format.js');
const detailSource = read('pages', 'work-order-detail.js');
const handlerSource = read('pages', 'handler-detail.js');
// 惰性載入：函式還不存在時只讓用到它的案例失敗，不拖垮整支檔案
const hostCountText = (...args) => new Function(`${extract(formatSource, 'export function hostCountText(')}\nreturn hostCountText;`)()(...args);

const RECURRENCE_NOTE = '重複發生，請評估調整規則或靜音';

function elements() {
    const dom = installDom();
    const { roleText, statusNote, metaLine } = loadElements(dom);
    const button = loadButton(dom);
    const statusBadge = (text, variant, { title } = {}) => {
        const span = document.createElement('span');
        span.className = `lf-badge lf-badge--${variant}`;
        span.textContent = text;
        if (title) span.title = title;
        return span;
    };
    return { dom, roleText, statusNote, metaLine, button, statusBadge };
}

/** 切出一行頂層常數宣告（`const NAME = …;`） */
function constLine(source, name) {
    const m = new RegExp(`^const ${name} = .*;$`, 'm').exec(source);
    assert(m, `找不到 production 常數：${name}`);
    return m[0];
}

/** work-order-detail.js 成員狀態格需要的函式與常數（production 原文） */
const memberStatusCellParts = () => [
    constLine(detailSource, 'LAST_REPLY_NOTE_MAX'),
    extract(detailSource, 'function priorDismissalText('),
    extract(detailSource, 'function lastReplyParts('),
    extract(detailSource, 'function observedRecurText('),
    extract(detailSource, 'function renderMemberStatusBadge('),
    extract(detailSource, 'function memberStatusCell(')
].join('\n');

const findAll = (node, predicate) => all(node).filter(predicate);

const cases = {
    // ── 1. 台數口徑 ──────────────────────────────────────────────────────────
    hostCountTextShowsHostsThenMembers() {
        assert.equal(hostCountText({ activeHosts: 60, totalHosts: 100, counts: { total: 247 } }), '進行中 60 台；歷來 100 台、247 件');
        // 歷來台數等於進行中：不重複報同一個數字，寫「歷來同」
        assert.equal(hostCountText({ activeHosts: 60, totalHosts: 60, counts: { total: 247 } }), '進行中 60 台；歷來同、247 件');
        // 沒有進行中
        assert.equal(hostCountText({ activeHosts: 0, totalHosts: 3, counts: { total: 5 } }), '進行中 0 台；歷來 3 台、5 件');
        // 欄位缺漏（舊回應）不出 undefined／NaN
        assert.equal(hostCountText({}), '進行中 0 台；歷來同、0 件');
    },

    // 交辦總覽與我的交辦、交辦單頁都不再自己拼「進行中／總數 台」
    hostCountTextIsTheOnlyFormatter() {
        for (const [name, source] of [['work-orders.js', read('pages', 'work-orders.js')], ['handler-detail.js', handlerSource], ['work-order-detail.js', detailSource]]) {
            assert.ok(source.includes('hostCountText('), `${name} 要用共用的 hostCountText`);
            assert.ok(!/counts\.active \?\? 0\}／\$\{counts\.total/.test(source), `${name} 還有自己拼的「進行中／總數 台」`);
        }
    },

    // ── 2. 復發上畫面 ────────────────────────────────────────────────────────
    headerShowsRecurrenceOnlyWhenPositive() {
        const make = () => new Function('hostCountText', `${extract(detailSource, 'function memberSummaryText(')}\nreturn memberSummaryText;`)(hostCountText);
        const text = make();
        const base = { activeHosts: 60, totalHosts: 100, counts: { total: 247, active: 60, inProgress: 2, observing: 1 } };
        const withRec = text({ ...base, recurrenceHosts: 12 });
        assert.ok(withRec.startsWith('進行中 60 台；歷來 100 台、247 件'), withRec);
        assert.ok(withRec.includes('其中 12 台是復發'), withRec);
        assert.ok(withRec.includes('處理中 2') && withRec.includes('觀察 1'), '原本的狀態分項仍在');
        assert.ok(!text({ ...base, recurrenceHosts: 0 }).includes('復發'));
        assert.ok(!text(base).includes('復發'), '舊回應沒有欄位也不出現');
    },

    memberStatusCellShowsRecurrenceAndLastReply() {
        const { roleText, statusBadge } = elements();
        const cell = new Function('document', 'statusBadge', 'statusBadgeMeta', 'roleText', `${memberStatusCellParts()}\nreturn memberStatusCell;`)(document, statusBadge, catalog.statusBadgeMeta, roleText);

        const rec = cell({ status: 'in_progress', isRecurrence: true, recurrenceCount: 3,
            lastReply: { date: '2026-11-20T09:30:00', status: 'resolved', note: '已重開服務' } });
        assert.ok(rec.textContent.includes('處理中'), '狀態徽章仍在');
        assert.ok(rec.textContent.includes('復發 3 次'), rec.textContent);
        assert.ok(rec.textContent.includes('11-20 已處理：已重開服務'), rec.textContent);

        // 說明過長：截斷並以 title 呈現全文；說明一律當純文字
        const longNote = '<b>很長</b>'.repeat(20);
        const long = cell({ status: 'open', isRecurrence: true, recurrenceCount: 1,
            lastReply: { date: '2026-11-20T09:30:00', status: 'resolved', note: longNote } });
        const noteEl = findAll(long, n => n.title && n.title.includes(longNote))[0];
        assert.ok(noteEl, '全文放在 title');
        assert.ok(noteEl.textContent.length < longNote.length + 10 && noteEl.textContent.endsWith('…'), noteEl.textContent);
        assert.ok(noteEl.textContent.includes('<b>'), '以 textContent 輸出，不被當 HTML');
        assert.equal(findAll(long, n => n.tagName === 'B').length, 0);

        // 沒有說明：只有日期與狀態
        const noNote = cell({ status: 'open', isRecurrence: true, recurrenceCount: 1, lastReply: { date: '2026-11-20T09:30:00', status: 'resolved', note: null } });
        assert.ok(noNote.textContent.includes('11-20 已處理') && !noNote.textContent.includes('：'));

        // 非復發：沒有任何復發附註
        const plain = cell({ status: 'in_progress', isRecurrence: false, recurrenceCount: 0, lastReply: { date: '2026-11-01T00:00:00', status: 'resolved', note: 'x' } });
        assert.ok(!plain.textContent.includes('復發') && !plain.textContent.includes('11-01'), plain.textContent);
    },

    // 批次 S：期滿放行後重新派工的成員，狀態格寫「先前判定不處理（MM-DD，某人），已滿 30 天仍在發生」；沒有就不出現
    memberStatusCellShowsPriorDismissal() {
        const { roleText, statusBadge } = elements();
        const cell = new Function('document', 'statusBadge', 'statusBadgeMeta', 'roleText', `${memberStatusCellParts()}\nreturn memberStatusCell;`)(document, statusBadge, catalog.statusBadgeMeta, roleText);

        const marked = cell({ status: 'in_progress', priorDismissal: { closedAt: '2026-12-18T09:00:00', account: 'h1', status: 'wont_fix' } });
        assert.ok(marked.textContent.includes('先前判定不處理（12-18，h1），已滿 30 天仍在發生'), marked.textContent);
        const noAccount = cell({ status: 'open', priorDismissal: { closedAt: '2026-12-18T09:00:00', account: null, status: 'known_noise' } });
        assert.ok(noAccount.textContent.includes('先前判定不處理（12-18），已滿 30 天仍在發生'), noAccount.textContent);
        const plain = cell({ status: 'in_progress', priorDismissal: null });
        assert.ok(!plain.textContent.includes('先前判定不處理'), plain.textContent);
        const old = cell({ status: 'in_progress' });
        assert.ok(!old.textContent.includes('先前判定不處理'), '舊回應沒有欄位也不出現');
    },

    // 批次 S：單頭只在有這類成員時才多一句
    headerShowsPriorDismissalOnlyWhenPositive() {
        const text = detail => new Function('hostCountText', `${extract(detailSource, 'function memberSummaryText(')}\nreturn memberSummaryText;`)(hostCountText)(detail);
        const base = { counts: { inProgress: 2, total: 2, active: 2 }, activeHosts: 2, totalHosts: 2 };
        assert.ok(text({ ...base, priorDismissalHosts: 2 }).includes('其中 2 台先前判定不處理、已滿 30 天仍在發生'), text({ ...base, priorDismissalHosts: 2 }));
        assert.ok(!text({ ...base, priorDismissalHosts: 0 }).includes('先前判定不處理'));
        assert.ok(!text(base).includes('先前判定不處理'), '舊回應沒有欄位也不出現');
    },

    memberHostCellMarksOccurrenceOnlyUnderAllFilter() {
        const { roleText, statusBadge } = elements();
        const make = () => new Function('document', 'appUrl', 'roleText', 'statusBadge', `${extract(detailSource, 'function renderMemberHostCell(')}\nreturn renderMemberHostCell;`)(document, p => '/lf' + p, roleText, statusBadge);
        const cell = make();
        const item = { hostId: 5, hostName: 'SRV-1', occurrence: 3, closedAt: null };
        assert.ok(textOf(cell(item, true)).includes('第 3 次'));
        assert.ok(!textOf(cell(item, false)).includes('第 3 次'), '非「全部」篩選不標');
        assert.ok(!textOf(cell({ ...item, occurrence: 1 }, true)).includes('第 1 次'), '第 1 次且同頁沒有別筆不標');
        assert.ok(textOf(cell({ ...item, occurrence: 1 }, true, true)).includes('第 1 次'), '同主機同頁有多筆時，第 1 次也標，新舊才對得起來');
        // 主機名仍是連結
        assert.equal(findAll(cell(item, true), n => n.tagName === 'A')[0].href, '/lf/hosts/5');
    },

    // H6：同主機在同頁有多列時標登錄類型；單列或沒有登錄類型不標
    memberHostCellShowsLogNameOnlyWhenHostRepeats() {
        const { roleText, statusBadge } = elements();
        const cell = new Function('document', 'appUrl', 'roleText', 'statusBadge', `${extract(detailSource, 'function renderMemberHostCell(')}\nreturn renderMemberHostCell;`)(document, p => '/lf' + p, roleText, statusBadge);
        const item = { hostId: 5, hostName: 'SRV-1', occurrence: 1, logName: 'Application', closedAt: null };
        assert.ok(textOf(cell(item, false, true)).includes('登錄類型：Application'), '同頁同主機多列要標');
        assert.ok(!textOf(cell(item, false, false)).includes('登錄類型'), '同頁只有一列不標');
        assert.ok(!textOf(cell({ ...item, logName: null }, false, true)).includes('登錄類型'), '沒有登錄類型不標');
        assert.equal(findAll(cell(item, false, true), n => n.tagName === 'A')[0].href, '/lf/hosts/5', '主機名仍是連結');
    },

    // E2：沒有權限時成員與歷程兩個區塊整個收起，載入成功再恢復
    deniedDetailHidesMemberAndTimelineSections() {
        installDom();
        const section = () => { const s = document.createElement('section'); return s; };
        const [memberSection, timelineSection] = [section(), section()];
        const membersContainer = document.createElement('div');
        const timelineContainer = document.createElement('div');
        memberSection.appendChild(membersContainer);
        timelineSection.appendChild(timelineContainer);
        // mini-dom 沒有 closest：替身只回所屬 section（production 的 closest('section') 在真實 DOM 同義）
        membersContainer.closest = () => memberSection;
        timelineContainer.closest = () => timelineSection;
        const fn = new Function('membersContainer', 'timelineContainer', `${extract(detailSource, 'function setDetailSectionsDenied(')}\nreturn setDetailSectionsDenied;`)(membersContainer, timelineContainer);
        fn(true);
        assert.ok(memberSection.classList.contains('d-none'), '成員區塊要收起');
        assert.ok(timelineSection.classList.contains('d-none'), '歷程區塊要收起');
        fn(false);
        assert.ok(!memberSection.classList.contains('d-none') && !timelineSection.classList.contains('d-none'), '載入成功要恢復');
        // 403 分支與成功分支都要呼叫它（切出的原始碼斷言：避免只寫了函式沒接上）
        const loadAll = extract(detailSource, 'async function loadAll(');
        assert.ok(/error\?\.status === 403\) \{[\s\S]*?setDetailSectionsDenied\(true\)/.test(loadAll), '403 分支要收起區塊');
        assert.ok(loadAll.includes('setDetailSectionsDenied(false)'), '成功分支要恢復');
    },

    handlerRowShowsRecurrenceBadgeOnlyWhenPositive() {
        const { roleText, statusBadge } = elements();
        const make = () => new Function('document', 'hostCountText', 'roleText', 'statusBadge', `${extract(handlerSource, 'function orderHostsHeadline(')}\n${extract(handlerSource, 'function orderHostsCell(')}\nreturn orderHostsCell;`)(document, hostCountText, roleText, statusBadge);
        const cell = make();
        const row = { activeHosts: 60, totalHosts: 100, todoHosts: 40, recurrenceHosts: 7, counts: { total: 247, inProgress: 3, escalated: 1 } };
        const rec = cell(row, 'active');
        assert.ok(rec.textContent.includes('進行中 60 台；歷來 100 台、247 件'), rec.textContent);
        assert.ok(rec.textContent.includes('復發 7'), rec.textContent);
        assert.ok(!cell({ ...row, recurrenceHosts: 0 }, 'active').textContent.includes('復發'));
        assert.ok(!cell({ ...row, recurrenceHosts: undefined }, 'active').textContent.includes('復發'));
        // 待我處理篩選：台數用待處理主機數
        const todo = cell(row, 'todo');
        assert.ok(todo.textContent.includes('待我處理 40 台'), todo.textContent);
    },

    // ── 3. 回覆彈窗的復發建議 ────────────────────────────────────────────────
    replyModalShowsRecurrenceHintAndThreeSuggestions() {
        const withRec = openReply(loadReplyModule(), { recurrence: { hosts: 4 } });
        const hint = byId(withRec.body, 'wo-reply-recurrence');
        assert.ok(hint && textOf(hint).includes('其中 4 台是反覆發生的問題'), hint && textOf(hint));
        for (const id of ['wo-reply-suggest-progress', 'wo-reply-suggest-observe', 'wo-reply-suggest-escalate']) assert.ok(byId(withRec.body, id), id);
        assert.equal(textOf(byId(withRec.body, 'wo-reply-suggest-progress')), '還在處理（設預計完成日）');
        assert.equal(textOf(byId(withRec.body, 'wo-reply-suggest-observe')), '觀察 7 日');
        assert.equal(textOf(byId(withRec.body, 'wo-reply-suggest-escalate')), '請管理員評估調整規則或靜音');
        // 建議在結論按鈕之前
        const order = all(withRec.body);
        assert.ok(order.indexOf(byId(withRec.body, 'wo-reply-suggest-escalate')) < order.indexOf(withRec.group), '建議列在結論按鈕前');
        // 選「已處理」不擋
        withRec.pick('resolved');
        assert.deepEqual(withRec.active(), ['resolved']);
        assert.equal(withRec.submitBtn.disabled, false);

        for (const options of [{}, { recurrence: { hosts: 0 } }, { recurrence: undefined }]) {
            const plain = openReply(loadReplyModule(), options);
            assert.equal(byId(plain.body, 'wo-reply-recurrence'), null, JSON.stringify(options));
            assert.equal(byId(plain.body, 'wo-reply-suggest-escalate'), null);
            assert.equal(byId(plain.body, 'wo-reply-suggest-observe'), null);
        }
    },

    async replyModalEscalateSuggestionSelectsEscalatedAndPrefillsNote() {
        const loaded = loadReplyModule();
        const m = openReply(loaded, { recurrence: { hosts: 2 } });
        byId(m.body, 'wo-reply-suggest-escalate').click();
        assert.deepEqual(m.active(), ['escalated'], '選對應結論（上報／無法處理）');
        assert.equal(m.note.value, RECURRENCE_NOTE);
        assert.equal(m.submitBtn.disabled, false);
        await m.send();
        assert.equal(m.submits.length, 1);
        assert.equal(m.submits[0].status, 'escalated');
        assert.equal(m.submits[0].note, RECURRENCE_NOTE);
        assert.ok(catalog.statusMeta('escalated'), '沿用既有結論代碼');
    },

    async replyModalEscalateSuggestionKeepsUserNote() {
        const m = openReply(loadReplyModule(), { recurrence: { hosts: 2 } });
        m.write('我已經試過重開機');
        byId(m.body, 'wo-reply-suggest-escalate').click();
        assert.deepEqual(m.active(), ['escalated']);
        assert.equal(m.note.value, '我已經試過重開機', '使用者已輸入的說明不覆蓋');
        await m.send();
        assert.equal(m.submits[0].note, '我已經試過重開機');
    },

    async replyModalObserveSuggestionSetsSevenDays() {
        const loaded = loadReplyModule();
        const m = openReply(loaded, { recurrence: { hosts: 2 } });
        m.due.value = '2020-01-01';   // 先前手動填過別的日期，建議要以「今天＋7」為準
        byId(m.body, 'wo-reply-suggest-observe').click();
        assert.deepEqual(m.active(), ['observing']);
        assert.equal(m.due.value, loaded.formatFns.dateAfterDays(7));
        await m.send();
        assert.equal(m.submits[0].status, 'observing');
        assert.equal(m.submits[0].dueDate, loaded.formatFns.dateAfterDays(7));
    },

    replyModalProgressSuggestionSelectsInProgressAndFocusesDue() {
        const m = openReply(loadReplyModule(), { recurrence: { hosts: 2 } });
        byId(m.body, 'wo-reply-suggest-progress').click();
        assert.deepEqual(m.active(), ['in_progress']);
        assert.equal(m.due.focused, true, '焦點移到期限欄');
        assert.equal(hidden(m.due), false);
    },

    async replyModalSuggestionsHiddenInRestMode() {
        // 部分回覆後續問「其餘 N 台」：復發主機數是整張單的，不適用於其餘，提示與建議收起
        const m = openReply(loadReplyModule(), {
            recurrence: { hosts: 2 }, partialTarget: true,
            submit: async () => ({ cases: 1, workOrderClosed: false }),
            followUp: { remaining: async () => 3, submitRest: async () => ({}) }
        });
        assert.equal(hidden(byId(m.body, 'wo-reply-suggest')), false);
        m.pick('resolved');
        await m.send();
        assert.equal(hidden(byId(m.body, 'wo-reply-recurrence')), true);
        assert.equal(hidden(byId(m.body, 'wo-reply-suggest')), true);
    },

    // 對象切到「只回覆部分主機」：復發提示的台數以勾選成員中 isRecurrence 的不重複主機數重算，0 時提示與建議都收起
    async replyModalRecurrenceCountFollowsPartialSelection() {
        const members = [
            { caseId: 'c1', hostName: 'SRV-001', status: 'in_progress', isRecurrence: true },
            { caseId: 'c2', hostName: 'SRV-002', status: 'in_progress', isRecurrence: true },
            { caseId: 'c3', hostName: 'SRV-003', status: 'in_progress', isRecurrence: false },
            { caseId: 'c4', hostName: 'SRV-001', status: 'in_progress', isRecurrence: true }   // 同一台的另一個簽章
        ];
        const load = async ({ page, pageSize }) => ({ items: members, total: members.length, page, pageSize });
        const m = openReply(loadReplyModule(), { recurrence: { hosts: 2 }, picker: { load } });
        const hint = () => byId(m.body, 'wo-reply-recurrence');
        const suggest = () => byId(m.body, 'wo-reply-suggest');
        const checks = () => all(m.body).filter(n => n.tagName === 'INPUT' && n.type === 'checkbox' && n.title === '選取這台主機');
        const tick = (i, on = true) => { checks()[i].checked = on; return fire(checks()[i], 'change'); };

        // 整單：沿用呼叫端給的台數
        assert.ok(textOf(hint()).includes('其中 2 台'), textOf(hint()));

        m.toggle.click();
        await new Promise(r => setTimeout(r, 0));
        // 切到部分、還沒勾：0 台復發，提示與三個建議都收起
        assert.equal(hidden(hint()), true, '沒勾任何主機不提示');
        assert.equal(hidden(suggest()), true);

        await tick(0);                       // SRV-001（復發）
        await tick(2);                       // SRV-003（非復發）
        assert.equal(hidden(hint()), false);
        assert.ok(textOf(hint()).includes('其中 1 台'), '勾 2 台其中 1 台復發：' + textOf(hint()));
        assert.equal(hidden(suggest()), false);

        await tick(3);                       // 同一台 SRV-001 的另一件：不重複計
        assert.ok(textOf(hint()).includes('其中 1 台'), '同主機不重複計：' + textOf(hint()));
        await tick(1);                       // SRV-002（復發）
        assert.ok(textOf(hint()).includes('其中 2 台'), textOf(hint()));

        // 只留非復發的
        await tick(0, false); await tick(1, false); await tick(3, false);
        assert.equal(hidden(hint()), true, '勾的都不是復發：無提示');
        assert.equal(hidden(suggest()), true, '勾的都不是復發：無建議');

        // 改回整單：回到呼叫端給的台數
        m.toggle.click();
        assert.equal(hidden(hint()), false);
        assert.ok(textOf(hint()).includes('其中 2 台'), textOf(hint()));
        assert.equal(hidden(suggest()), false);
    },

    // 單一成員回覆：用該成員自己的 isRecurrence，不拿單頭復發主機數近似
    singleMemberReplyUsesMembersOwnRecurrence() {
        const make = () => {
            const opened = [];
            const openReplyModal = options => opened.push(options);
            const fn = new Function('currentDetail', 'workOrderId', 'openWorkOrderReplyModal', 'caseIdsDraftKey',
                `${extract(detailSource, 'function openMemberReply(')}\nreturn openMemberReply;`)(
                { recurrenceHosts: 5, issueLabel: 'x', issueKey: 'k' }, 7, openReplyModal, loadReplyModule().caseIdsDraftKey);
            return { fn, opened };
        };
        const { fn, opened } = make();
        fn(['c1'], '主機 A', 1, 0);
        assert.equal(opened.at(-1).recurrence.hosts, 0, '該成員非復發、單頭有復發：不提示');
        fn(['c1'], '主機 A', 1, 1);
        assert.equal(opened.at(-1).recurrence.hosts, 1, '該成員是復發：1 台');
        fn(['c1'], '主機 A', 1);
        assert.equal(opened.at(-1).recurrence.hosts, 0, '沒給成員資訊的單一成員不以單頭近似');
        fn([], '全部', 20);
        assert.equal(opened.at(-1).recurrence.hosts, 5, '整張單仍取單頭的復發主機數');

        // 呼叫端（單一進行中成員的回覆入口）要把成員自己的旗標傳進來
        assert.match(detailSource, /openMemberReply\(\[member\.caseId\], `主機 \$\{member\.hostName\}`, 1, member\.isRecurrence \? 1 : 0\)/);
    },

    // ── 4. 我的交辦觀察分流 ──────────────────────────────────────────────────
    handlerKpiShowsTodoObservingEscalated() {
        const cards = new Function(`${extract(handlerSource, 'function kpiCards(')}\nreturn kpiCards;`)()(
            { activeWorkOrders: 5, activeMembers: 30, overdueMembers: 2, unrepliedWorkOrders: 1, todoHosts: 12, observingHosts: 15, escalatedHosts: 3 });
        const by = label => cards.find(c => c.label === label);
        assert.equal(by('待我處理').value, 12);
        assert.equal(by('觀察期內').value, 15);
        assert.equal(by('上報中').value, 3);
        assert.equal(by('逾期台數').variant, 'danger');
        assert.equal(by('進行中單').value, 5);
        assert.equal(by('進行中台數'), undefined, '三個互斥數字取代原本的進行中台數');
        // 舊回應沒有欄位：不出 undefined
        const old = new Function(`${extract(handlerSource, 'function kpiCards(')}\nreturn kpiCards;`)()({ activeWorkOrders: 1 });
        assert.equal(old.find(c => c.label === '待我處理').value, 0);
    },

    handlerDefaultStatusIsTodoAndUrlWins() {
        const view = fs.readFileSync(path.join(webRoot, 'Views', 'Pages', 'HandlerDetail.cshtml'), 'utf8');
        const select = view.match(/<select id="handler-wo-status"[\s\S]*?<\/select>/)[0];
        const options = [...select.matchAll(/<option value="([^"]+)"([^>]*)>([^<]*)</g)].map(m => ({ value: m[1], selected: /\bselected\b/.test(m[2]), label: m[3] }));
        assert.deepEqual(options.filter(o => o.selected).map(o => o.value), ['todo'], '預設選取待我處理');
        assert.equal(options.find(o => o.value === 'todo').label, '待我處理');
        for (const value of ['active', 'escalated', 'overdue', 'unreplied', 'closed', 'all']) assert.ok(options.some(o => o.value === value), `保留 ${value}`);

        const urlStatus = new Function(`${extract(handlerSource, 'function urlOrderStatus(')}\nreturn urlOrderStatus;`)();
        const values = options.map(o => o.value);
        assert.equal(urlStatus('?status=all', values), 'all', '網址帶 status 以網址為準');
        assert.equal(urlStatus('?status=active&order=3', values), 'active');
        assert.equal(urlStatus('', values), null, '沒帶就沿用頁面預設（todo）與使用者已選的值');
        assert.equal(urlStatus('?status=bogus', values), null, '不認得的值不採用');
    },

    // ── 5. 觀察到期與一鍵結案 ────────────────────────────────────────────────
    observedTextHasThreeForms() {
        const { roleText, statusBadge } = elements();
        const cell = new Function('document', 'statusBadge', 'statusBadgeMeta', 'roleText', `${memberStatusCellParts()}\nreturn memberStatusCell;`)(document, statusBadge, catalog.statusBadgeMeta, roleText);
        const observing = d => cell({ status: 'observing', closedAt: null, isRecurrence: false, ...d }).textContent;
        assert.ok(observing({ observedRecurDays: 3 }).includes('觀察期間再發生 3 天'));
        assert.ok(observing({ observedRecurDays: 0 }).includes('沒有再發生'));
        assert.ok(observing({ observedRecurDays: null }).includes('無資料'));
        assert.ok(observing({}).includes('無資料'), '欄位缺漏視同無資料');
        // 不是觀察中就不寫
        assert.ok(!cell({ status: 'in_progress', observedRecurDays: null }).textContent.includes('無資料'));
        assert.ok(!cell({ status: 'resolved', observedRecurDays: 0 }).textContent.includes('沒有再發生'));
    },

    quietCloseButtonHiddenUnlessEligible() {
        const make = () => quietCloseEnv().quietCloseButton;
        const make1 = make();
        const ok = { viewerCanReply: true, closedAt: null, observationExpiredQuietCaseIds: ['c1', 'c2'] };
        assert.ok(make1(ok));
        assert.equal(make1({ ...ok, observationExpiredQuietCaseIds: [] }), null, '清單為空不顯示');
        assert.equal(make1({ ...ok, observationExpiredQuietCaseIds: undefined }), null);
        assert.equal(make1({ ...ok, viewerCanReply: false }), null, '檢視者不能回覆不顯示');
        assert.equal(make1({ ...ok, closedAt: '2026-11-01' }), null, '單已結案不顯示');
    },

    async quietCloseConfirmsThenRepliesWithExpiredIdsAndReloads() {
        const env = quietCloseEnv();
        const detail = { viewerCanReply: true, closedAt: null, observationExpiredQuietCaseIds: ['c1', 'c2', 'c3'] };
        const btn = env.quietCloseButton(detail);
        assert.ok(textOf(btn).includes('把到期且沒有再發生的 3 台結案'), textOf(btn));
        env.state.confirm = false;
        await btn.listeners.click[0]();
        assert.equal(env.posts.length, 0, '先 confirmAction：取消就不送');
        assert.equal(env.confirms.length, 1);
        assert.ok(env.confirms[0].message.includes('3 台'), env.confirms[0].message);

        env.state.confirm = true;
        await btn.listeners.click[0]();
        assert.equal(env.posts.length, 1);
        assert.equal(env.posts[0].url, '/api/work-orders/7/reply');
        assert.deepEqual(env.posts[0].body.caseIds, ['c1', 'c2', 'c3'], 'caseIds 等於 observationExpiredQuietCaseIds');
        assert.equal(env.posts[0].body.status, 'resolved');
        assert.equal(env.posts[0].body.note, '觀察期滿未再發生');
        assert.equal(env.reloads.length, 1, '成功後重載單頭、成員、時間軸');
        assert.equal(env.replyToasts.length, 1, '用 E-1 的共用成功訊息');
        assert.equal(env.replyToasts[0].payload.status, 'resolved');
    },

    async quietCloseFailureDoesNotReloadSilently() {
        const env = quietCloseEnv();
        env.state.postError = Object.assign(new Error('這張單剛結案'), { status: 400 });
        const btn = env.quietCloseButton({ viewerCanReply: true, closedAt: null, observationExpiredQuietCaseIds: ['c1'] });
        await btn.listeners.click[0]();
        assert.ok(env.toasts.some(t => t.message.includes('這張單剛結案')), '失敗要出聲');
        assert.equal(env.replyToasts.length, 0);
        assert.equal(env.reloads.length, 1, '被拒（400）時畫面資料已過期，重載一次');
    }
};

/** quietCloseButton 的最小環境：button／withBusy 用 production 版，其餘為可觀察替身 */
function quietCloseEnv() {
    const { dom, roleText } = elements();
    const button = loadButton(dom);
    const uiSource = read('core', 'ui.js');
    const withBusy = new Function(`${extract(uiSource, 'export function withBusy(')}\nreturn withBusy;`)();
    const state = { confirm: true, postError: null };
    const posts = []; const confirms = []; const reloads = []; const toasts = []; const replyToasts = [];
    const api = { post: async (url, body, options) => { if (state.postError) throw state.postError; posts.push({ url, body, options }); return { cases: body.caseIds.length, workOrderClosed: false }; } };
    const factory = new Function('button', 'withBusy', 'api', 'workOrderId', 'confirmAction', 'toast', 'toastReplyResult', 'reloadAll',
        `${constLine(detailSource, 'QUIET_CLOSE_NOTE')}\n${extract(detailSource, 'function quietCloseButton(')}\nreturn quietCloseButton;`);
    const quietCloseButton = factory(button, withBusy, api, 7,
        async options => { confirms.push(options); return state.confirm; },
        (message, type) => toasts.push({ message, type }),
        (result, payload) => replyToasts.push({ result, payload }),
        async () => { reloads.push(1); });
    return { quietCloseButton, posts, confirms, reloads, toasts, replyToasts, state };
}

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
