/**
 * 階段 P-2：期限預填、嚴重度欄與標記、排序、「今天需要你處理」KPI 與篩選橫幅（前端）行為測試。
 * 一律切出 production 函式本體執行，只替換 DOM 與 API／UI 邊界；由 WorkOrderP2BehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { read, extract, all, textOf, hidden } from './load-reply-modal.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web');
const view = name => fs.readFileSync(path.join(webRoot, 'Views', 'Pages', name), 'utf8');

const formatSource = read('core', 'format.js');
const recordsSource = read('pages', 'records.js');
const handlerSource = read('pages', 'handler-detail.js');
const pathsSource = read('core', 'paths.js');

function env() {
    const dom = installDom();
    const { roleText } = loadElements(dom);
    const button = loadButton(dom);
    const statusBadge = (text, variant, { title } = {}) => {
        const span = document.createElement('span');
        span.className = `lf-badge lf-badge--${variant}`;
        span.textContent = text;
        if (title) span.title = title;
        return span;
    };
    return { dom, roleText, button, statusBadge };
}

const findAll = (node, predicate) => all(node).filter(predicate);

/** format.js 兩個新出口（嚴重度徽章與重大旗標用 production 版） */
function formatFns() {
    const { statusBadge } = env();
    const SEVERITY_VARIANT = { High: 'warning', Medium: 'info', Low: 'neutral' };
    const severityName = s => ({ High: '高', Medium: '中', Low: '低' })[s] ?? s;
    const parts = [
        'export function elevatesBadge(', 'export function severityCell(', 'export function severityBadge(',
        'export function workOrderSeverityCell(', 'export function workOrderFlagBadges('
    ].map(d => extract(formatSource, d)).join('\n');
    return new Function('document', 'statusBadge', 'SEVERITY_VARIANT', 'severityName',
        `${parts}\nreturn { workOrderSeverityCell, workOrderFlagBadges };`)(document, statusBadge, SEVERITY_VARIANT, severityName);
}

/** 交辦彈窗的期限預填：只切 production 的 createSuggestedDuePrefill */
function prefill() {
    const { dom } = env();
    const dueInput = dom.newElement('input');
    const hint = dom.newElement('div');
    hint.classList.add('d-none');
    const make = new Function(`${extract(recordsSource, 'function createSuggestedDuePrefill(')}\nreturn createSuggestedDuePrefill;`)();
    return { dueInput, hint, ...{ prefill: make(dueInput, hint) } };
}

/** handler-detail.js 標記篩選、KPI 與請求網址的最小環境 */
function handlerEnv({ search = '', status = 'todo' } = {}) {
    const { dom, roleText, button } = env();
    const bannerEl = dom.newElement('div');
    bannerEl.classList.add('d-none');
    const statusSelect = { value: status };
    const sortSelect = { value: 'priority' };
    const pausedCheckbox = { checked: false };
    const historyCalls = [];
    const requests = [];
    const loc = { search, pathname: '/handlers/9' };
    const history = { replaceState: (_s, _t, url) => { historyCalls.push(url); loc.search = url.includes('?') ? url.slice(url.indexOf('?')) : ''; } };
    const source = [
        'const ORDER_FLAGS = ' + /const ORDER_FLAGS = ([\s\S]*?\n\});?\n/.exec(handlerSource)?.[1],
        extract(handlerSource, 'function urlOrderFlag('),
        extract(handlerSource, 'function flagBannerText('),
        extract(handlerSource, 'function renderFlagBanner('),
        extract(handlerSource, 'function syncFlagUrl('),
        extract(handlerSource, 'function ordersUrl('),
        extract(handlerSource, 'function applyFlag('),
        extract(handlerSource, 'function todayCards(')
    ].join('\n');
    const factory = new Function('document', 'userId', 'bannerEl', 'statusSelect', 'sortSelect', 'pausedCheckbox', 'loadPageSize',
        'location', 'history', 'roleText', 'button', 'formatNumber', 'onOrderFilterChange', 'initialSearch', 'appUrlFn',
        `let orderPage = 1;\nlet activeFlag = null;\nconst flagBannerEl = bannerEl;\nconst appUrl = appUrlFn;\nactiveFlag = urlOrderFlag(initialSearch);\n${source}\n` +
        'return { ordersUrl, applyFlag, todayCards, renderFlagBanner, flagBannerText, urlOrderFlag, getFlag: () => activeFlag };');
    const api = factory(document, 9, bannerEl, statusSelect, sortSelect, pausedCheckbox, () => 20, loc, history, roleText, button, n => String(n),
        () => { requests.push(api.ordersUrl()); }, search, p => p);
    api.renderFlagBanner();
    return { ...api, bannerEl, statusSelect, requests, historyCalls, loc };
}

const cases = {
    // ── 1. 期限預填 ──────────────────────────────────────────────────────────
    prefillFillsSuggestedDueDateOnFirstPreview() {
        const p = prefill();
        p.prefill.apply({ suggestedDueDate: '2026-11-28T00:00:00' });
        assert.equal(p.dueInput.value, '2026-11-28');
        assert.ok(!hidden(p.hint), '有建議期限時顯示說明');
        assert.ok(recordsSource.includes("dueHint.textContent = '依嚴重度預設，可清除'"), '說明文字');
        // 再預覽（換頁、改處理人）仍可更新建議
        p.prefill.apply({ suggestedDueDate: '2026-12-02T00:00:00' });
        assert.equal(p.dueInput.value, '2026-12-02');
        // 建議消失（低嚴重度）且使用者沒動過：清掉預填、藏說明
        p.prefill.apply({ suggestedDueDate: null });
        assert.equal(p.dueInput.value, '');
        assert.ok(hidden(p.hint));
    },

    prefillDoesNotOverrideAfterUserClears() {
        const p = prefill();
        p.prefill.apply({ suggestedDueDate: '2026-11-28T00:00:00' });
        p.dueInput.value = '';
        p.dueInput.dispatch('input');
        p.prefill.apply({ suggestedDueDate: '2026-11-28T00:00:00' });
        assert.equal(p.dueInput.value, '', '使用者清掉後再預覽不再填回');
    },

    prefillDoesNotOverrideAfterUserEdits() {
        const p = prefill();
        p.prefill.apply({ suggestedDueDate: '2026-11-28T00:00:00' });
        p.dueInput.value = '2026-12-31';
        p.dueInput.dispatch('change');
        p.prefill.apply({ suggestedDueDate: '2026-11-28T00:00:00' });
        assert.equal(p.dueInput.value, '2026-12-31', '使用者改過日期後不覆蓋');
        p.prefill.apply({ suggestedDueDate: null });
        assert.equal(p.dueInput.value, '2026-12-31', '建議消失也不清使用者填的值');
    },

    // ── 2. 嚴重度欄與標記 ────────────────────────────────────────────────────
    severityCellShowsFourValuesAndCritical() {
        const { workOrderSeverityCell } = formatFns();
        assert.equal(textOf(workOrderSeverityCell({ severity: 'High' })), '高');
        assert.equal(textOf(workOrderSeverityCell({ severity: 'Medium' })), '中');
        assert.equal(textOf(workOrderSeverityCell({ severity: 'Low' })), '低');
        assert.equal(textOf(workOrderSeverityCell({ severity: null })), '—');
        assert.equal(textOf(workOrderSeverityCell({})), '—', '欄位缺漏（舊回應）顯示—');
        assert.ok(textOf(workOrderSeverityCell({ severity: 'High', critical: true })).includes('重大'));
        assert.ok(!textOf(workOrderSeverityCell({ severity: 'High', critical: false })).includes('重大'));
        assert.ok(!textOf(workOrderSeverityCell({ severity: null, critical: true })).includes('重大'), '沒有嚴重度不單獨標重大');
    },

    flagBadgesShowOnlyPositiveCounts() {
        const { workOrderFlagBadges } = formatFns();
        const row = { newTodayHosts: 3, returnedMembers: 2, observationExpiredHosts: 4, recurrenceHosts: 5 };
        const text = workOrderFlagBadges(row).textContent;
        for (const t of ['新 3', '退回 2', '觀察到期 4']) assert.ok(text.includes(t), `${t}：${text}`);
        assert.ok(!text.includes('復發'), '預設不含復發（我的交辦已在主機欄顯示）');
        assert.ok(workOrderFlagBadges(row, { recurrence: true }).textContent.includes('復發 5'));
        // 逐一為 0／缺漏：各自不出現
        assert.ok(!workOrderFlagBadges({ ...row, newTodayHosts: 0 }).textContent.includes('新 '));
        assert.ok(!workOrderFlagBadges({ ...row, returnedMembers: 0 }).textContent.includes('退回'));
        assert.ok(!workOrderFlagBadges({ ...row, observationExpiredHosts: undefined }).textContent.includes('觀察到期'));
        assert.ok(!workOrderFlagBadges({ ...row, recurrenceHosts: 0 }, { recurrence: true }).textContent.includes('復發'));
        assert.equal(workOrderFlagBadges({ newTodayHosts: 0, returnedMembers: 0 }), null, '全 0 回 null，不留空容器');
    },

    flagBadgesAndSeverityAreWiredIntoBothLists() {
        const handler = handlerSource;
        const wo = read('pages', 'work-orders.js');
        for (const [name, src] of [['handler-detail.js', handler], ['work-orders.js', wo]]) {
            assert.ok(src.includes('workOrderSeverityCell('), `${name} 要有嚴重度欄`);
            assert.ok(src.includes('workOrderFlagBadges('), `${name} 要有標記`);
            assert.ok(/title: '嚴重度'/.test(src), `${name} 嚴重度欄標題`);
        }
        // 窄螢幕摘要行保留嚴重度
        const narrow = /const narrow = metaLine\(\[(.*?)\]/s.exec(handler)[1];
        assert.ok(narrow.includes('嚴重度'), '窄版摘要行要有嚴重度');
    },

    // ── 3. 排序 ──────────────────────────────────────────────────────────────
    handlerSortDefaultsToPriorityAndUrlWins() {
        const select = view('HandlerDetail.cshtml').match(/<select id="handler-wo-sort"[\s\S]*?<\/select>/)[0];
        const options = [...select.matchAll(/<option value="([^"]+)"([^>]*)>([^<]*)</g)].map(m => ({ value: m[1], selected: /\bselected\b/.test(m[2]), label: m[3] }));
        assert.deepEqual(options.filter(o => o.selected).map(o => o.value), ['priority']);
        assert.equal(options.find(o => o.value === 'priority').label, '處理優先序');
        assert.equal(options.find(o => o.value === 'dueSoon').label, '期限最近');
        assert.equal(options.find(o => o.value === 'severity').label, '嚴重度');
        for (const v of ['created_desc', 'members_desc', 'unreplied_oldest']) assert.ok(options.some(o => o.value === v), `保留 ${v}`);
        assert.ok(/urlParamChoice\(location\.search, 'sort'/.test(handlerSource), '網址 sort 優先於預設');
        const choice = new Function(`${extract(pathsSource, 'export function urlParamChoice(')}\nreturn urlParamChoice;`)();
        const values = options.map(o => o.value);
        assert.equal(choice('?sort=dueSoon', 'sort', values), 'dueSoon');
        assert.equal(choice('?sort=bogus', 'sort', values), null);
        assert.equal(choice('', 'sort', values), null);
    },

    overviewSortKeepsDefaultAndAddsThree() {
        const select = view('WorkOrders.cshtml').match(/<select id="wo-sort"[\s\S]*?<\/select>/)[0];
        const options = [...select.matchAll(/<option value="([^"]+)"([^>]*)>([^<]*)</g)].map(m => ({ value: m[1], selected: /\bselected\b/.test(m[2]) }));
        assert.deepEqual(options.filter(o => o.selected).map(o => o.value), ['created_desc'], '總覽預設維持現行');
        for (const v of ['priority', 'dueSoon', 'severity', 'members_desc', 'unreplied_oldest']) assert.ok(options.some(o => o.value === v), v);
        assert.ok(/urlParamChoice\(location\.search, 'sort'/.test(read('pages', 'work-orders.js')), '總覽也接網址 sort');
    },

    // ── 4. KPI「今天需要你處理」 ─────────────────────────────────────────────
    todayCardsClickAppliesFlagFilter() {
        const e = handlerEnv();
        const data = { newTodayHosts: 4, recurrenceHosts: 3, returnedMembers: 2, observationExpiredHosts: 1, dueSoonMembers: 6 };
        const cards = e.todayCards(data);
        assert.deepEqual(cards.map(c => c.label), ['今天新增', '復發', '被退回', '觀察到期', '3 天內到期']);
        assert.deepEqual(cards.map(c => c.value), ['4', '3', '2', '1', '6']);
        const expected = ['newToday', 'recurrence', 'returned', 'observationExpired', 'dueSoon'];
        cards.forEach((c, i) => {
            e.requests.length = 0;
            c.onClick({ preventDefault() { } });
            assert.equal(e.requests.length, 1, c.label);
            assert.ok(new URLSearchParams(e.requests[0].split('?')[1]).get('flag') === expected[i], `${c.label} → ${e.requests[0]}`);
            assert.equal(e.statusSelect.value, 'active', '標記數字以進行中成員計，狀態改為進行中以免被「待我處理」再篩一次');
        });
    },

    todayCardsZeroIsShownButNotClickable() {
        const e = handlerEnv();
        const cards = e.todayCards({ newTodayHosts: 0, returnedMembers: 2 });
        const zero = cards.find(c => c.label === '今天新增');
        assert.equal(zero.value, '0', '0 照顯示');
        assert.equal(zero.onClick, undefined, '0 不可點');
        assert.equal(zero.url, undefined);
        assert.equal(zero.variant, 'secondary', '0 淡化');
        assert.equal(cards.find(c => c.label === '復發').value, '0', '欄位缺漏當 0');
        const live = cards.find(c => c.label === '被退回');
        assert.equal(typeof live.onClick, 'function');
        assert.ok(live.url.includes('flag=returned'), live.url);
        assert.notEqual(live.variant, 'secondary');
    },

    // ── 5. 篩選橫幅 ──────────────────────────────────────────────────────────
    flagBannerShowsForKnownFlagAndClearDropsIt() {
        const e = handlerEnv({ search: '?flag=returned', status: 'todo' });
        assert.ok(!hidden(e.bannerEl));
        assert.ok(textOf(e.bannerEl).includes('只看：被退回的單'), textOf(e.bannerEl));
        assert.ok(new URLSearchParams(e.ordersUrl().split('?')[1]).get('flag') === 'returned', '網址帶的 flag 要送進清單請求');
        const clear = findAll(e.bannerEl, n => n.tagName === 'BUTTON')[0];
        assert.ok(clear, '要有清除鈕');
        e.requests.length = 0;
        clear.listeners.click[0]({ preventDefault() { } });
        assert.equal(e.requests.length, 1);
        assert.ok(!e.requests[0].includes('flag='), `清除後請求不帶 flag：${e.requests[0]}`);
        assert.ok(hidden(e.bannerEl), '清除後橫幅隱藏');
        assert.equal(e.getFlag(), null);
        assert.ok(!e.loc.search.includes('flag'), '網址的 flag 參數一併移除');
    },

    flagBannerHiddenWithoutFlagOrWithUnknownFlag() {
        const none = handlerEnv({ search: '' });
        assert.ok(hidden(none.bannerEl));
        assert.ok(!none.ordersUrl().includes('flag='));
        const unknown = handlerEnv({ search: '?flag=bogus' });
        assert.ok(hidden(unknown.bannerEl), '未知值不顯示橫幅');
        assert.ok(unknown.ordersUrl().includes('flag=bogus'), '仍送給後端，驗證錯誤照後端顯示');
        // 全部五個已知值都有橫幅文字
        for (const [flag, label] of [['returned', '被退回的單'], ['newToday', '今天新增的單'], ['recurrence', '復發的單'], ['observationExpired', '觀察到期的單'], ['dueSoon', '3 天內到期的單']]) {
            assert.equal(handlerEnv({ search: `?flag=${flag}` }).flagBannerText(flag), `只看：${label}`);
        }
    },

    // ── 6. 逾期標示（預設期限之後會出現）────────────────────────────────────
    overdueSingleShowsAlertTextInList() {
        // 清單的逾期欄沿用既有呈現：逾期 > 0 用警示文字；窄版摘要行也一樣
        const { roleText } = env();
        const m = /title: '逾期',[\s\S]*?render: row => \{([\s\S]*?)\n            \}/.exec(handlerSource);
        assert.ok(m, '找不到逾期欄 render');
        const render = new Function('roleText', `return row => {${m[1]}\n};`)(roleText);
        const cell = render({ counts: { overdue: 2 } });
        assert.equal(cell.className, 'lf-text-alert');
        assert.equal(cell.textContent, '2');
        assert.equal(render({ counts: { overdue: 0 } }).className, 'lf-text-data');
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
