/**
 * 第 81 輪批次 N：雜訊分流入口的前端行為。
 * - F16-1 對照表（core/hide-triage.js）：2×3×3＝18 個答案組合逐格固定「呼叫哪個端點、帶哪些參數」；不可用的組合不出現並附原因；
 *   後果摘要寫的機制與實際送出的請求是同一個機制；套用時依序送出 plan 的請求、失敗即停。
 * - F16-4 總覽儲存格文字（core/hide-summary.js）與入口接線（交辦單頁捷徑、records 的更多選單）。
 * 由 HideTriageBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { read, extract, all, jsRoot } from './load-reply-modal.mjs';

const triage = await import(pathToFileURL(path.join(jsRoot, 'core', 'hide-triage.js')).href);
const summaryText = await import(pathToFileURL(path.join(jsRoot, 'core', 'hide-summary.js')).href);
const {
    TRIAGE_TABLE, cellFor, defaultOrders, triageAvailability, scopeAvailability, ordersAvailability, coerceOrders,
    validateTriage, buildTriagePlan, mechanismOfRequests, triageSummary, applyTriage, TRIAGE_MECHANISM_NAMES
} = triage;

const ctx = { source: 'disk', eventId: 153, issueLabel: '磁碟錯誤', ruleId: 'builtin-disk', activeOrders: 2, orderIds: [11, 12], canClose: true };
const params = { days: '7', reason: '例行維護', hostGroupId: '5', host: 'HOST-1', groupName: '機房甲', conclusion: 'known_noise' };
const MUTE = '/api/admin/issue-owners/disk/153/mute';
const CONCLUSION = '/api/admin/issue-owners/disk/153/conclusion';
const SUPPRESS = '/api/rules/builtin-disk/suppressions';
const ALL_KEYS = ['temporary', 'permanent'].flatMap(d => ['site', 'group', 'host'].flatMap(s => ['pause', 'close', 'keep'].map(o => `${d}|${s}|${o}`)));

/** 逐格預期（獨立於實作，手寫）：key → 該組合送出的請求；null＝不提供 */
const EXPECTED = {
    'temporary|site|pause': [{ method: 'PUT', path: MUTE, body: { days: 7, until: null, reason: '例行維護', existingOrders: 'pause' } }],
    'temporary|site|close': [{ method: 'PUT', path: MUTE, body: { days: 7, until: null, reason: '例行維護', existingOrders: 'close' } }],
    'temporary|site|keep': null,
    'temporary|group|pause': null,
    'temporary|group|close': null,
    'temporary|group|keep': [{ method: 'POST', path: SUPPRESS, body: { scope: 'Group', host: null, hostGroupId: 5, reason: '例行維護', days: 7 } }],
    'temporary|host|pause': null,
    'temporary|host|close': null,
    'temporary|host|keep': [{ method: 'POST', path: SUPPRESS, body: { scope: 'Host', host: 'HOST-1', hostGroupId: null, reason: '例行維護', days: 7 } }],
    'permanent|site|pause': null,
    'permanent|site|close': [
        { method: 'PUT', path: CONCLUSION, body: { status: 'known_noise', note: '例行維護', autoApply: true } },
        { method: 'POST', path: '/api/work-orders/11/admin-close', body: { status: 'known_noise', reason: '例行維護', blockRedispatch: true, caseIds: null } },
        { method: 'POST', path: '/api/work-orders/12/admin-close', body: { status: 'known_noise', reason: '例行維護', blockRedispatch: true, caseIds: null } }
    ],
    'permanent|site|keep': [{ method: 'PUT', path: CONCLUSION, body: { status: 'known_noise', note: '例行維護', autoApply: true } }],
    'permanent|group|pause': null,
    'permanent|group|close': null,
    'permanent|group|keep': [{ method: 'POST', path: SUPPRESS, body: { scope: 'Group', host: null, hostGroupId: 5, reason: '例行維護', days: null } }],
    'permanent|host|pause': null,
    'permanent|host|close': null,
    'permanent|host|keep': [{ method: 'POST', path: SUPPRESS, body: { scope: 'Host', host: 'HOST-1', hostGroupId: null, reason: '例行維護', days: null } }]
};

const answersOf = key => { const [duration, scope, orders] = key.split('|'); return { duration, scope, orders }; };

const cases = {
    // 18 格一格不少、不多
    tableHasExactlyEighteenCells() {
        assert.deepEqual(Object.keys(TRIAGE_TABLE).sort(), [...ALL_KEYS].sort());
        assert.deepEqual(Object.keys(EXPECTED).sort(), [...ALL_KEYS].sort());
        // 每格不是有步驟就是有原因，沒有第三種
        for (const key of ALL_KEYS) {
            const cell = TRIAGE_TABLE[key];
            assert.ok(cell.unavailable ? cell.steps.length === 0 : cell.steps.length > 0, `${key} 必須恰為「可用」或「不可用並附原因」`);
            assert.ok(['mute', 'conclusion', 'suppression'].includes(cell.mechanism), `${key} 標出機制`);
        }
    },

    // 每個答案組合 → 端點與參數（逐格）；不可用的組合沒有 plan、附一句原因
    everyAnswerCombinationMapsToEndpointAndParams() {
        for (const key of ALL_KEYS) {
            const answers = answersOf(key);
            const plan = buildTriagePlan(answers, params, ctx);
            const expected = EXPECTED[key];
            if (expected === null) {
                assert.equal(plan, null, `${key} 不應該有對應的請求`);
                const availability = triageAvailability(answers, ctx);
                assert.equal(availability.available, false, `${key} 應標為不可用`);
                assert.ok(availability.reason && availability.reason.length > 0, `${key} 要說明原因`);
                continue;
            }
            assert.ok(plan, `${key} 應該有對應的請求`);
            assert.deepEqual(plan.requests, expected, `${key} 的端點與參數`);
        }
    },

    // 暫時＋全站的第三題「不動」被擋的原因講清楚；長期＋全站的「暫停」同
    unavailableReasonsAreSpecific() {
        assert.match(triageAvailability({ duration: 'temporary', scope: 'site', orders: 'keep' }, ctx).reason, /靜音期間單會自動顯示為暫停/);
        assert.match(triageAvailability({ duration: 'permanent', scope: 'site', orders: 'pause' }, ctx).reason, /沒有到期日/);
        assert.match(triageAvailability({ duration: 'temporary', scope: 'host', orders: 'close' }, ctx).reason, /單與成員維持原狀/);
    },

    // 規則不存在卻選了指定群組／單一主機：不提供該選項並說明原因；全站不受影響
    noRuleHidesPartialScopes() {
        const noRule = { ...ctx, ruleId: null };
        for (const scope of ['group', 'host']) {
            const state = scopeAvailability(scope, noRule);
            assert.equal(state.available, false);
            assert.match(state.reason, /沒有對應的規則/);
            const answers = { duration: 'temporary', scope, orders: 'keep' };
            assert.equal(triageAvailability(answers, noRule).available, false);
            assert.equal(buildTriagePlan(answers, params, noRule), null);
        }
        assert.equal(scopeAvailability('site', noRule).available, true);
        assert.ok(buildTriagePlan({ duration: 'temporary', scope: 'site', orders: 'pause' }, params, noRule));
        // 有規則的另一側
        assert.equal(scopeAvailability('group', ctx).available, true);
    },

    // 沒有進行中交辦單：不問第三題，套用該組合的預設（暫時＋全站＝靜音，其餘不動）；結果與明確選預設相同
    noOrdersUsesDefaultAnswer() {
        const none = { ...ctx, activeOrders: 0, orderIds: [] };
        for (const duration of ['temporary', 'permanent']) {
            for (const scope of ['site', 'group', 'host']) {
                const orders = defaultOrders(duration, scope);
                assert.equal(orders, duration === 'temporary' && scope === 'site' ? 'pause' : 'keep');
                // 不論畫面殘留什麼第三題答案，都用預設
                const plan = buildTriagePlan({ duration, scope, orders: 'close' }, params, none);
                const explicit = buildTriagePlan({ duration, scope, orders }, params, { ...none, activeOrders: 1 });
                assert.deepEqual(plan.requests, explicit.requests, `${duration}|${scope}`);
                assert.equal(plan.orders, orders);
            }
        }
        // 長期＋全站沒有單：只有結論那一個請求（沒有 admin-close）
        assert.equal(buildTriagePlan({ duration: 'permanent', scope: 'site', orders: 'close' }, params, none).requests.length, 1);
    },

    // 代為結案要同時有指派與處理權限；沒有時兩個會用到它的組合都不可用，其他組合不受影響
    closeNeedsAssignAndHandle() {
        const noClose = { ...ctx, canClose: false };
        for (const key of ['temporary|site|close', 'permanent|site|close']) {
            const state = triageAvailability(answersOf(key), noClose);
            assert.equal(state.available, false, key);
            assert.match(state.reason, /指派與處理權限/);
            assert.equal(ordersAvailability('close', answersOf(key), noClose).available, false);
        }
        assert.equal(triageAvailability(answersOf('temporary|site|pause'), noClose).available, true);
        assert.equal(triageAvailability(answersOf('permanent|site|keep'), noClose).available, true);
        assert.equal(ordersAvailability('close', answersOf('temporary|site|close'), ctx).available, true);
    },

    // 答案改變後，已不可用的第三題換成預設；可用的維持
    coerceOrdersFallsBackToDefault() {
        assert.equal(coerceOrders({ duration: 'temporary', scope: 'site', orders: 'keep' }, ctx), 'pause');
        assert.equal(coerceOrders({ duration: 'permanent', scope: 'site', orders: 'pause' }, ctx), 'keep');
        assert.equal(coerceOrders({ duration: 'temporary', scope: 'group', orders: 'close' }, ctx), 'keep');
        assert.equal(coerceOrders({ duration: 'permanent', scope: 'site', orders: 'close' }, ctx), 'close');
        assert.equal(coerceOrders({ duration: 'temporary', scope: 'site', orders: 'close' }, ctx), 'close');
        assert.equal(coerceOrders({ duration: 'temporary', scope: 'site', orders: 'close' }, { ...ctx, canClose: false }), 'pause');
    },

    // 送出前的驗證：邊界兩側各一例
    validationBoundaries() {
        const ok = { duration: 'temporary', scope: 'site', orders: 'pause' };
        assert.equal(validateTriage(ok, params, ctx), null);
        assert.match(validateTriage(ok, { ...params, reason: '   ' }, ctx), /原因/);
        assert.match(validateTriage(ok, { ...params, reason: '' }, ctx), /原因/);
        assert.equal(validateTriage(ok, { ...params, days: '1' }, ctx), null, '1 天是下限');
        assert.equal(validateTriage(ok, { ...params, days: '365' }, ctx), null, '365 天是上限');
        assert.match(validateTriage(ok, { ...params, days: '0' }, ctx), /1～365/);
        assert.match(validateTriage(ok, { ...params, days: '366' }, ctx), /1～365/);
        assert.match(validateTriage(ok, { ...params, days: '7.5' }, ctx), /1～365/);
        const group = { duration: 'temporary', scope: 'group', orders: 'keep' };
        assert.match(validateTriage(group, { ...params, hostGroupId: '' }, ctx), /主機群組/);
        assert.equal(validateTriage(group, params, ctx), null);
        const host = { duration: 'permanent', scope: 'host', orders: 'keep' };
        assert.match(validateTriage(host, { ...params, host: ' ' }, ctx), /選擇主機/);
        assert.equal(validateTriage(host, { ...params, days: '' }, ctx), null, '長期不檢查天數');
        const permanent = { duration: 'permanent', scope: 'site', orders: 'keep' };
        assert.match(validateTriage(permanent, { ...params, conclusion: 'resolved' }, ctx), /結論/);
        assert.equal(validateTriage(permanent, { ...params, conclusion: 'wont_fix' }, ctx), null);
        // 不可用的組合直接回原因
        assert.match(validateTriage({ duration: 'temporary', scope: 'site', orders: 'keep' }, params, ctx), /靜音期間/);
    },

    // 後果摘要：第一行寫的做法＝實際送出的機制；文字依答案帶出天數、結論、群組、主機、單的處置
    summaryMatchesAppliedMechanism() {
        let checked = 0;
        for (const key of ALL_KEYS) {
            if (EXPECTED[key] === null) continue;
            const answers = answersOf(key);
            const plan = buildTriagePlan(answers, params, ctx);
            const lines = triageSummary(answers, params, ctx, plan);
            const applied = mechanismOfRequests(plan.requests);
            assert.equal(applied, plan.mechanism, `${key} 請求的機制與 plan 一致`);
            assert.equal(lines[0], `做法：${TRIAGE_MECHANISM_NAMES[applied]}`, `${key} 摘要寫的做法＝實際送出的機制`);
            checked++;
        }
        assert.equal(checked, 8);

        const mutePause = triageSummary(answersOf('temporary|site|pause'), params, ctx, buildTriagePlan(answersOf('temporary|site|pause'), params, ctx));
        assert.ok(mutePause.some(l => l.includes('7 天')), '靜音寫出天數');
        assert.ok(mutePause.some(l => l.includes('2 張交辦單') && l.includes('暫停')));
        const muteClose = triageSummary(answersOf('temporary|site|close'), params, ctx, buildTriagePlan(answersOf('temporary|site|close'), params, ctx));
        assert.ok(muteClose.some(l => l.includes('代為結案為「不處理」')));

        const noise = triageSummary(answersOf('permanent|site|close'), params, ctx, buildTriagePlan(answersOf('permanent|site|close'), params, ctx));
        assert.ok(noise.some(l => l.includes('「已知雜訊」') && l.includes('已經出現的日子維持原狀')));
        assert.ok(noise.some(l => l.includes('代為結案為「已知雜訊」')));
        const wontFixParams = { ...params, conclusion: 'wont_fix' };
        const wontFix = triageSummary(answersOf('permanent|site|keep'), wontFixParams, ctx, buildTriagePlan(answersOf('permanent|site|keep'), wontFixParams, ctx));
        assert.ok(wontFix.some(l => l.includes('「不處理」')), '結論改選不處理，摘要與請求同步');
        assert.ok(wontFix.some(l => l.includes('2 張交辦單') && l.includes('不動')));
        assert.equal(buildTriagePlan(answersOf('permanent|site|keep'), wontFixParams, ctx).requests[0].body.status, 'wont_fix');

        const group = triageSummary(answersOf('temporary|group|keep'), params, ctx, buildTriagePlan(answersOf('temporary|group|keep'), params, ctx));
        assert.ok(group.some(l => l.includes('主機群組「機房甲」') && l.includes('7 天') && l.includes('builtin-disk')));
        assert.ok(group.some(l => l.includes('其他問題')), '抑制設在規則上，要說會連帶其他問題');
        const host = triageSummary(answersOf('permanent|host|keep'), params, ctx, buildTriagePlan(answersOf('permanent|host|keep'), params, ctx));
        assert.ok(host.some(l => l.includes('主機「HOST-1」') && l.includes('不設期限')));

        // 沒有進行中交辦單：不寫單的處置句
        const none = { ...ctx, activeOrders: 0, orderIds: [] };
        const noOrders = triageSummary(answersOf('temporary|site|pause'), params, none, buildTriagePlan(answersOf('temporary|site|pause'), params, none));
        assert.ok(!noOrders.some(l => l.includes('交辦單')));
        assert.deepEqual(triageSummary(answersOf('temporary|site|keep'), params, ctx, null), []);
    },

    // 套用：依序送出 plan 的請求（PUT 與 POST 各走各的方法）；第一個失敗即停，已完成的數量如實回報
    async applyTriageSendsPlanInOrderAndStopsOnFailure() {
        const plan = buildTriagePlan(answersOf('permanent|site|close'), params, ctx);
        const sent = [];
        const api = {
            put: async (url, body) => { sent.push(['PUT', url, body]); return { ok: 'put' }; },
            post: async (url, body) => { sent.push(['POST', url, body]); return { ok: 'post' }; }
        };
        const done = await applyTriage(plan, api);
        assert.deepEqual(sent.map(([m, u, b]) => ({ method: m, path: u, body: b })), plan.requests);
        assert.equal(done.done, 3);
        assert.equal(done.failed, null);
        assert.deepEqual(done.results.map(r => r.ok), ['put', 'post', 'post']);

        const failing = [];
        const bad = {
            put: async (url, body) => { failing.push(url); return {}; },
            post: async url => { failing.push(url); throw new Error('boom'); }
        };
        const failed = await applyTriage(plan, bad);
        assert.equal(failed.done, 1, '結論已寫入、第一張單失敗');
        assert.equal(failed.failed.path, '/api/work-orders/11/admin-close');
        assert.deepEqual(failing, [CONCLUSION, '/api/work-orders/11/admin-close'], '失敗後不再送第二張');
    },

    // 路徑裡的來源與規則編號要編碼（來源可含空白或斜線以外的特殊字元）
    pathsEncodeSourceAndRule() {
        const odd = { ...ctx, source: 'Service Control Manager', ruleId: 'rule/x y' };
        const mute = buildTriagePlan(answersOf('temporary|site|pause'), params, odd);
        assert.equal(mute.requests[0].path, '/api/admin/issue-owners/Service%20Control%20Manager/153/mute');
        const suppress = buildTriagePlan(answersOf('temporary|host|keep'), params, odd);
        assert.equal(suppress.requests[0].path, '/api/rules/rule%2Fx%20y/suppressions');
    },

    // F16-4 總覽儲存格文字：有／沒有、單位與邊界
    hideSummaryCellTexts() {
        const { muteText, conclusionText, suppressionText, dismissedText } = summaryText;
        assert.equal(muteText({ mute: null }), null);
        assert.equal(muteText({ mute: { to: '2026-12-10T00:00:00' } }), '靜音至 2026-12-10');
        assert.equal(conclusionText({ conclusion: null }), null);
        assert.equal(conclusionText({ conclusion: { statusText: '已知雜訊', autoApply: false } }), '已知雜訊');
        assert.equal(conclusionText({ conclusion: { statusText: '已知雜訊', autoApply: true } }), '已知雜訊（自動套用中）');
        assert.equal(suppressionText({ suppression: null }), null);
        assert.equal(suppressionText({ suppression: { total: 3, siteCount: 1, groupCount: 1, hostCount: 1 } }), '3 條：全站 1、主機群組 1、單一主機 1');
        assert.equal(suppressionText({ suppression: { total: 2, siteCount: 0, groupCount: 0, hostCount: 2 } }), '2 條：單一主機 2');
        assert.equal(dismissedText({ dismissed: null }), null);
        assert.equal(dismissedText({ dismissed: { hosts: 3, releasingHosts: 0, nextReleaseInDays: null } }), '3 台，不會自動恢復');
        assert.equal(dismissedText({ dismissed: { hosts: 3, releasingHosts: 2, nextReleaseInDays: 21 } }), '3 台，其中 2 台會自動恢復，最早 21 天後');
    },

    // 交辦單頁捷徑：傳了 onHideTriage 且具 Maintain、單未結案才有「不再顯示這個問題…」；其餘三種情形都沒有
    shortcutAppearsOnlyForMaintainOnOpenOrder() {
        const dom = installDom();
        const button = loadButton(dom);
        const linkButton = (text, href) => { const a = dom.newElement('a'); a.textContent = text; a.href = href; return a; };
        const source = read('pages', 'work-order-reference.js');
        const body = ['function otherOrdersHref(', 'function ruleEditHref(', 'function issueShortcuts('].map(d => extract(source, d)).join('\n');
        const fn = new Function('document', 'appUrl', 'ruleEditUrl', 'button', 'linkButton',
            `${body}\nreturn issueShortcuts;`)(document, p => p, id => `/admin/rules?ruleId=${id}`, button, linkButton);
        const detail = { source: 'Disk', eventId: 153, ruleId: 'builtin-disk', closedAt: null };
        let opened = 0;
        const labels = row => (row ? row.children.map(n => n.textContent) : null);

        const maintain = fn(detail, { canListOrders: true, canMaintain: true, onMute: () => {}, onHideTriage: () => opened++ });
        assert.ok(labels(maintain).includes('不再顯示這個問題…'));
        maintain.children[labels(maintain).indexOf('不再顯示這個問題…')].listeners.click[0]({});
        assert.equal(opened, 1);

        assert.ok(!labels(fn(detail, { canListOrders: true, canMaintain: false, onMute: () => {}, onHideTriage: () => {} })).includes('不再顯示這個問題…'), '沒有 Maintain：沒有');
        assert.ok(!labels(fn({ ...detail, closedAt: '2026-01-01T00:00:00' }, { canListOrders: true, canMaintain: true, onMute: () => {}, onHideTriage: () => {} })).includes('不再顯示這個問題…'), '已結案：沒有');
        assert.ok(!labels(fn(detail, { canListOrders: true, canMaintain: true, onMute: () => {} })).includes('不再顯示這個問題…'), '沒傳 onHideTriage：沒有');
    },

    // 問題查詢依問題視角的「更多」選單：具 Maintain 才有這個動作，且帶規則編號
    recordsIssueActionRequiresMaintain() {
        const source = read('pages', 'records.js');
        const body = extract(source, 'function issueActions(');
        const calls = [];
        const make = caps => new Function('hasCapability', 'currentUser', 'openWorkOrderModal', 'openBulkCloseModal', 'openIssueStatusReplyModal', 'openIssueMuteModal',
            'openHideTriage', 'HIDE_TRIAGE_ACTION', 'search', 'appUrl', 'ruleEditUrl', 'location',
            `${body}\nreturn issueActions;`)(
            (user, cap) => caps.includes(cap), { userId: 1 }, () => {}, () => {}, () => {}, () => {},
            args => calls.push(args), { text: '不再顯示這個問題…', title: 't' }, () => {}, p => p, id => `/r/${id}`, {});
        const group = { source: 'disk', eventId: 153, issueLabel: '磁碟錯誤', ruleId: 'builtin-disk', handlers: [] };
        const withMaintain = make(['Maintain'])(group);
        const item = withMaintain.find(a => a.text === '不再顯示這個問題…');
        assert.ok(item);
        item.run();
        assert.equal(calls[0].ruleId, 'builtin-disk');
        assert.equal(calls[0].source, 'disk');
        assert.equal(calls[0].eventId, 153);
        assert.ok(!make(['Assign', 'Handle'])(group).some(a => a.text === '不再顯示這個問題…'), '沒有 Maintain：沒有');
        make(['Maintain'])({ ...group, ruleId: undefined }).find(a => a.text === '不再顯示這個問題…').run();
        assert.equal(calls[1].ruleId, null, '沒有對應規則時帶 null，面板會停用群組與單一主機');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
