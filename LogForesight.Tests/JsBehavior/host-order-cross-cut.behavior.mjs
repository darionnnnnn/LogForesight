/**
 * 第 81 輪批次 T：主機與問題橫切面的前端行為。
 * - F05-2 頁頂主機快速搜尋：名稱與 IP、最多 10 筆、慢回應不蓋新結果、範圍外的主機搜不到（範圍由伺服器決定，前端不加料）。
 * - F05-1 主機詳情「這台的交辦」：勾選兩個問題一次回覆（每張單各一次、caseIds 只帶這台的案件）、
 *   其中一張被拒另一張仍成功並列出原因、0 列的空狀態、同問題多登錄類型才標登錄類型。
 * - F06-1／F15-2／F19-1 交辦單頁：處置參考與訊息樣本、三個捷徑各自的目的地與權限、代為結案的範圍。
 * - H11 風險日側欄的處理人：有問題層級交辦／只有日層級指派／都沒有 三例。
 * 執行 production 程式（去掉 import／export 後以 new Function 執行），只替換 DOM 與 API 邊界。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';
import { loadReplyModule } from './load-reply-modal.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const readJs = rel => fs.readFileSync(path.join(jsRoot, rel), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');
const { createSeqGuard } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);
const catalog = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);

function extractFunction(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1).replace(/^export /, '');
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

/** 載入整個模組：去掉 import（含多行）與 export 關鍵字，匯入的名稱必須全部有替身，否則明確報錯 */
function loadModule(rel, injections) {
    const source = readJs(rel);
    const importBlocks = [...source.matchAll(/^import[\s\S]*?from\s+'[^']+';\n/gm)];
    const imported = importBlocks.flatMap(m => [...m[0].matchAll(/import\s*\{([^}]*)\}/g)]
        .flatMap(x => x[1].split(',').map(n => n.trim().split(/\s+as\s+/).pop()).filter(Boolean)));
    for (const name of imported) assert(name in injections, `${rel} 匯入了 ${name}，本測試沒有對應替身`);
    const body = source.replace(/^import[\s\S]*?from\s+'[^']+';\n/gm, '').replace(/^export /gm, '');
    const exported = [...source.matchAll(/^export (?:async )?(?:function|const) (\w+)/gm)].map(m => m[1]);
    const names = Object.keys(injections);
    return new Function(...names, `${body}\nreturn { ${exported.join(', ')} };`)(...names.map(n => injections[n]));
}

const all = node => { const out = []; const walk = n => { out.push(n); n.children.forEach(walk); }; walk(node); return out; };
const textOf = node => node.textContent;
const hasClass = (node, name) => String(node.className || '').split(' ').includes(name);
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
const BASE = '/lf';
const appUrl = p => (typeof p === 'string' && p.startsWith('/') ? BASE + p : p);

// ── 頁頂主機快速搜尋 ────────────────────────────────────────────────────────

function setupSearch(apiGet) {
    const dom = installDom();
    const calls = [];
    const api = { get: async (url, options) => { calls.push({ url, options }); return apiGet(url); } };
    const module = loadModule('core/host-quick-search.js', { api, appUrl, createSeqGuard });
    const input = dom.newElement('input');
    input.value = '';
    const results = dom.newElement('div');
    results.className = 'd-none';
    const search = module.createHostQuickSearch({ input, results, delay: 0 });
    return { module, search, input, results, calls };
}

const host = (id, name, extra = {}) => ({ hostId: id, hostName: name, displayName: null, ipAddress: null, ...extra });
const links = results => all(results).filter(n => n.tagName === 'A');

// ── 主機詳情「這台的交辦」 ──────────────────────────────────────────────────

function setupHostOrders({ rows, user = { userId: 7 }, caps = ['Handle'], post, replyModal } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const { roleText } = loadElements(dom);
    const toasts = [];
    const posts = [];
    const tables = [];
    const modals = [];
    const uiSource = readJs('core/ui.js');
    const createPageSelection = new Function(`${extractFunction(uiSource, 'export function createPageSelection(')}\nreturn createPageSelection;`)();
    const reply = loadReplyModule();
    const stubs = {
        api: {
            get: async () => ({ items: rows }),
            post: async (url, body, options) => { posts.push({ url, body, options }); return post ? post(url, body) : { cases: body.caseIds.length, workOrderClosed: false }; }
        },
        appUrl,
        renderTable: (container, options) => { tables.push(options); container.replaceChildren(); },
        button,
        toast: (message, type) => toasts.push({ message, type }),
        createPageSelection,
        roleText,
        statusBadge: (text, variant) => { const el = dom.newElement('span'); el.textContent = text; el.variant = variant; return el; },
        severityBadge: sev => { const el = dom.newElement('span'); el.textContent = `嚴重度${sev}`; return el; },
        statusBadgeMeta: catalog.statusBadgeMeta,
        openWorkOrderReplyModal: options => { modals.push(options); replyModal?.(options); },
        replyOutcomeText: reply.replyOutcomeText
    };
    const module = loadModule('pages/host-orders.js', stubs);
    const container = dom.newElement('div');
    const replyHost = dom.newElement('div');
    const selected = dom.newElement('span');
    selected.id = 'host-orders-selected';
    dom.byId.set('host-orders-selected', selected);
    const orders = module.createHostOrders({
        hostId: 3, hostName: 'SRV-3', container, currentUser: user,
        can: (_u, cap) => caps.includes(cap), replyButtonHost: replyHost, selectedCountEl: selected
    });
    return { module, orders, container, replyHost, toasts, posts, tables, modals, selected };
}

const orderRow = (caseId, workOrderId, extra = {}) => ({
    caseId, workOrderId, issueLabel: `問題 ${caseId}`, logName: 'System', showLogName: false, severity: 'High', critical: false,
    handlerId: 7, handlerName: '甲', status: 'in_progress', dueDate: null, isRecurrence: false, lastReply: null, dismissalReleases: false, canReply: true, ...extra
});

/** 勾選某幾列（caseId）：走表格第一欄的列勾選框，與使用者操作同一條路 */
function tickRows(tableOptions, rows, caseIds) {
    const column = tableOptions.columns[0];
    for (const row of rows.filter(r => caseIds.includes(r.caseId))) {
        const check = column.render(row);
        check.checked = true;
        (check.listeners.change || []).forEach(h => h());
    }
}

const cases = {
    // ── F05-2 主機快速搜尋 ──────────────────────────────────────────────────
    // 名稱與 IP 各一例：都走同一個端點的 query，限制 10 筆，結果連到主機詳情（補掛載前綴）
    async hostSearchByNameAndIp() {
        const t = setupSearch(url => url.includes('10.1.2.3')
            ? { items: [host(9, 'DB-01', { ipAddress: '10.1.2.3' })], total: 1, truncated: false }
            : { items: [host(5, 'WEB-01', { displayName: '網站一號' }), host(6, 'WEB-02')], total: 2, truncated: false });

        await t.search.run('web');
        assert.equal(t.calls[0].url, '/api/hosts?query=web&limit=10', '名稱搜尋帶 query 與 10 筆上限');
        assert.deepEqual(links(t.results).map(a => a.href), [`${BASE}/hosts/5`, `${BASE}/hosts/6`]);
        assert(textOf(links(t.results)[0]).includes('WEB-01（網站一號）'), 'Sentinel 顯示名附在名稱後');
        assert(!hasClass(t.results, 'd-none'), '有結果時顯示');

        await t.search.run('10.1.2.3');
        assert.equal(t.calls[1].url, '/api/hosts?query=10.1.2.3&limit=10', 'IP 搜尋走同一個端點');
        assert.deepEqual(links(t.results).map(a => a.href), [`${BASE}/hosts/9`]);
        assert(textOf(links(t.results)[0]).includes('10.1.2.3'), 'IP 顯示在結果列');
        assert.notEqual(t.calls[0].options?.background, true, '使用者輸入觸發的查詢，不是常駐輪詢');
    },

    // 範圍由伺服器決定：受限使用者範圍外的主機伺服器不回，前端不補、也不多列；伺服器多回也只取 10 筆
    async hostSearchShowsOnlyWhatServerReturnsAndCapsAtTen() {
        const empty = setupSearch(() => ({ items: [], total: 0, truncated: false }));
        await empty.search.run('secret-host');
        assert.equal(links(empty.results).length, 0, '範圍外的主機搜不到');
        assert(textOf(empty.results).includes('找不到符合的主機'));

        const many = setupSearch(() => ({ items: Array.from({ length: 12 }, (_, i) => host(i + 1, `H-${i + 1}`)), total: 40, truncated: true }));
        await many.search.run('h-');
        assert.equal(links(many.results).length, 10, '最多列 10 筆');
        assert(textOf(many.results).includes('共 40 筆符合'), '符合的比顯示的多時直說');
    },

    // 慢回應不蓋新結果；空白輸入收起結果且不發請求
    async hostSearchStaleResponseAndBlankInput() {
        const pending = [];
        const t = setupSearch(url => new Promise(resolve => pending.push({ url, resolve })));
        const first = t.search.run('a');
        const second = t.search.run('ab');
        pending[1].resolve({ items: [host(2, 'AB-HOST')], total: 1, truncated: false });
        await second;
        pending[0].resolve({ items: [host(1, 'A-HOST')], total: 1, truncated: false });
        await first;
        assert.deepEqual(links(t.results).map(a => textOf(a).trim()), ['AB-HOST'], '過期回應被丟棄');

        const before = t.calls.length;
        await t.search.run('   ');
        assert.equal(t.calls.length, before, '空白輸入不發請求');
        assert(hasClass(t.results, 'd-none'), '空白輸入收起結果');
    },

    // Enter 前往：查詢期間又輸入了新字，舊的查詢回來時不能拿更早一批結果的第一筆去導覽；仍是最新的才前往
    async hostSearchEnterOnlyNavigatesWhenQueryIsCurrent() {
        const pending = [];
        const t = setupSearch(url => url.includes('query=web&')
            ? { items: [host(1, 'WEB-01')], total: 1, truncated: false }
            : new Promise(resolve => pending.push({ url, resolve })));
        const nav = { href: '' };
        globalThis.location = nav;
        const enter = () => t.input.listeners.keydown[0]({ key: 'Enter', preventDefault() { } });

        await t.search.run('web');                       // 先有一批結果（第一筆 WEB-01）
        t.input.value = 'web1';
        enter();                                         // 還沒查就按 Enter：送出 web1
        const newer = t.search.run('web12');             // 等待期間又輸入新字：web1 變成過期
        pending[0].resolve({ items: [host(2, 'WEB-12')], total: 1, truncated: false });
        await tick();
        assert.equal(nav.href, '', '過期的 Enter 查詢不得導覽（更不能用更早一批的第一筆）');
        pending[1].resolve({ items: [host(3, 'WEB-123')], total: 1, truncated: false });
        await newer;

        // 邊界另一側：Enter 的查詢仍是最新的，就前往第一筆
        t.input.value = 'web9';
        enter();
        pending[2].resolve({ items: [host(9, 'WEB-09')], total: 1, truncated: false });
        await tick(); await tick();
        assert.equal(nav.href, `${BASE}/hosts/9`, '最新的查詢才前往第一筆');
    },

    // ── F05-1 這台的交辦 ────────────────────────────────────────────────────
    // 勾選兩個問題（分屬兩張單）一次回覆：每張單各一次回覆請求，caseIds 只帶這台的案件
    async hostOrdersReplyTwoIssuesTwoRequests() {
        const rows = [orderRow('c1', 12), orderRow('c2', 9), orderRow('c3', 12)];
        const t = setupHostOrders({ rows });
        await t.orders.load();
        const table = t.tables.at(-1);
        tickRows(table, rows, ['c1', 'c2']);
        t.orders.openReply();
        assert.equal(t.modals.length, 1, '開一個回覆彈窗（沿用既有彈窗，不另寫）');
        assert(t.modals[0].targetText.includes('2 個問題（2 張單）'), t.modals[0].targetText);
        assert.equal(t.modals[0].multiIssues, true, '後果提示用「這台主機的這些問題」');

        const payload = { status: 'resolved', note: '已處理', dueDate: null };
        await t.modals[0].submit(payload);
        assert.deepEqual(t.posts.map(p => [p.url, p.body.caseIds]), [
            ['/api/work-orders/9/reply', ['c2']],
            ['/api/work-orders/12/reply', ['c1']]
        ], '依單號各送一次、只帶這台勾選的案件（c3 沒勾，不送）');
        assert(t.posts.every(p => p.options?.silent === true && p.body.status === 'resolved' && p.body.note === '已處理'));
        assert.equal(t.toasts.at(-1).type, 'success');
    },

    // 其中一張被拒：另一張仍成功，訊息列出失敗的單與原因；全部失敗才讓彈窗保留草稿
    async hostOrdersOneRejectedOthersSucceed() {
        const rows = [orderRow('c1', 12), orderRow('c2', 9)];
        const t = setupHostOrders({
            rows,
            post: (url, body) => {
                if (url.endsWith('/12/reply')) throw Object.assign(new Error('這張單剛改派給乙，你已不是處理人'), { status: 403 });
                return { cases: body.caseIds.length, workOrderClosed: false };
            }
        });
        await t.orders.load();
        tickRows(t.tables.at(-1), rows, ['c1', 'c2']);
        t.orders.openReply();

        await t.modals[0].submit({ status: 'resolved', note: null, dueDate: null });
        assert.equal(t.posts.length, 2, '被拒的不影響另一張送出');
        const last = t.toasts.at(-1);
        assert.equal(last.type, 'warning');
        assert(last.message.includes('已完成 1 張單'), last.message);
        assert(last.message.includes('#12 失敗：這張單剛改派給乙，你已不是處理人'), '列出失敗的單與原因');

        // 全部失敗：整筆當失敗（擲例外），彈窗保留草稿與原因
        const allFail = setupHostOrders({ rows, post: () => { throw new Error('全拒'); } });
        await allFail.orders.load();
        tickRows(allFail.tables.at(-1), rows, ['c1', 'c2']);
        allFail.orders.openReply();
        await assert.rejects(() => allFail.modals[0].submit({ status: 'resolved', note: null, dueDate: null }), /全拒/);
    },

    // 0 列＝「這台主機目前沒有進行中的交辦」；沒有可回覆的列就沒有勾選欄與回覆鈕
    async hostOrdersEmptyAndNotReplyable() {
        const empty = setupHostOrders({ rows: [] });
        await empty.orders.load();
        assert.equal(empty.tables.at(-1).rows.length, 0);
        assert.equal(empty.tables.at(-1).empty.title, '這台主機目前沒有進行中的交辦');
        assert(empty.tables.at(-1).empty.hint, '空狀態要有下一步的說明');

        const readOnly = setupHostOrders({ rows: [orderRow('c1', 12, { canReply: false })], user: { userId: 8 }, caps: ['ViewAll'] });
        await readOnly.orders.load();
        const columns = readOnly.tables.at(-1).columns;
        assert.deepEqual(columns.map(c => c.title), ['問題', '嚴重度', '處理人', '狀態', '單號'], '沒有可回覆的列時沒有勾選欄');
        assert.equal(readOnly.replyHost.children.length, 0, '沒有 Handle 能力就沒有回覆鈕');
    },

    // 同主機同問題不同登錄類型各一列：showLogName 為真才標登錄類型；單號只對看得了那張單的人是連結
    async hostOrdersLogNameAndOrderLink() {
        const rows = [orderRow('c1', 12, { showLogName: true, logName: 'Application' }), orderRow('c2', 9, { issueLabel: '別的問題', handlerId: 99, canReply: false })];
        const t = setupHostOrders({ rows, user: { userId: 7 }, caps: ['Handle'] });
        await t.orders.load();
        const columns = t.tables.at(-1).columns;
        const issueColumn = columns.find(c => c.title === '問題');
        assert(textOf(issueColumn.render(rows[0])).includes('登錄類型：Application'));
        assert(!textOf(issueColumn.render(rows[1])).includes('登錄類型'), '只有一列的問題不標登錄類型');

        const orderColumn = columns.find(c => c.title === '單號');
        assert.equal(orderColumn.render(rows[0]).tagName, 'A', '自己的單是連結');
        assert.equal(orderColumn.render(rows[0]).href, `${BASE}/work-orders/12`);
        assert.equal(orderColumn.render(rows[1]).tagName === 'A', false, '看不了的單只顯示單號文字');
    },

    // ── F06-1／F15-2／F19-1 交辦單頁 ────────────────────────────────────────
    // 處置參考與訊息樣本：有規則、無規則（只有白話說明或沒有）、無樣本三例
    async guidanceAndSamplePanels() {
        const dom = installDom();
        const button = loadButton(dom);
        const ref = loadModule('pages/work-order-reference.js', {
            appUrl, ruleEditUrl: id => `/admin/rules?ruleId=${id}`, roleText: loadElements(dom).roleText, button, linkButton: () => null
        });

        assert.equal(ref.guidancePanel(null), null, '規則不存在：不顯示處置參考');
        assert.equal(ref.guidancePanel({ explanation: '只有白話說明', impact: '', likelyCauses: [], nextSteps: [] }), null, '只有白話說明（單頭已顯示）沒有可展開的內容');
        const panel = ref.guidancePanel({ explanation: 'x', impact: '資料可能遺失', likelyCauses: ['硬碟老化'], nextSteps: ['備份', '換碟'] });
        const text = textOf(panel);
        assert(text.includes('影響') && text.includes('資料可能遺失') && text.includes('可能原因') && text.includes('硬碟老化'));
        assert.deepEqual(all(panel).filter(n => n.tagName === 'OL').map(n => n.children.map(textOf)), [['備份', '換碟']], '處置步驟是有先後的編號清單');

        assert.equal(ref.sampleMessagesPanel([]), null, '沒有樣本：不放展開鈕');
        assert.equal(ref.sampleMessagesPanel(undefined), null);
        const samples = ref.sampleMessagesPanel(['<b>m1</b>', 'm2']);
        const blocks = all(samples).filter(n => n.tagName === 'PRE');
        assert.deepEqual(blocks.map(textOf), ['<b>m1</b>', 'm2'], '逐則一段、原文以文字呈現不解析 HTML');
        assert.equal(samples.innerHTML, '', '不得用 innerHTML');
    },

    // 三個捷徑各自的目的地與參數；沒有 Maintain 時只有「其他交辦單」
    async issueShortcutsDestinationsAndCapabilities() {
        const dom = installDom();
        const button = loadButton(dom);
        const linkButton = (text, href, options = {}) => { const a = dom.newElement('a'); a.textContent = text; a.href = href; a.title = options.title; return a; };
        const ref = loadModule('pages/work-order-reference.js', {
            appUrl, ruleEditUrl: id => `/admin/rules?ruleId=${encodeURIComponent(id)}`, roleText: loadElements(dom).roleText, button, linkButton
        });
        const detail = { source: 'Disk', eventId: 153, ruleId: 'builtin-disk', closedAt: null };
        let muted = 0;
        const labels = row => (row ? row.children.map(textOf) : null);

        const assignOnly = ref.issueShortcuts(detail, { canListOrders: true, canMaintain: false, onMute: () => muted++ });
        assert.deepEqual(labels(assignOnly), ['這個問題的其他交辦單'], '沒有 Maintain：只有其他交辦單');
        assert.equal(assignOnly.children[0].href, `${BASE}/work-orders?source=Disk&eventId=153`, '連到已篩選的交辦總覽（來源＋事件）');

        const maintain = ref.issueShortcuts(detail, { canListOrders: true, canMaintain: true, onMute: () => muted++ });
        assert.deepEqual(labels(maintain), ['這個問題的其他交辦單', '靜音這個問題', '調整這條規則']);
        assert.equal(maintain.children[2].href, `${BASE}/admin/rules?ruleId=builtin-disk`, '調整這條規則帶規則 Id');
        maintain.children[1].listeners.click[0]({});
        assert.equal(muted, 1, '靜音鈕開靜音視窗');

        assert.equal(ref.issueShortcuts(detail, { canListOrders: false, canMaintain: false, onMute: () => {} }), null, '沒有任何權限：沒有捷徑');
        const noRule = ref.issueShortcuts({ ...detail, ruleId: null }, { canListOrders: true, canMaintain: true, onMute: () => {} });
        assert.deepEqual(labels(noRule), ['這個問題的其他交辦單', '靜音這個問題'], '沒有命中規則：沒有「調整這條規則」');
        const multi = ref.issueShortcuts({ ...detail, source: null, eventId: null, ruleId: null }, { canListOrders: true, canMaintain: true, onMute: () => {} });
        assert.equal(multi, null, '多問題單沒有單一問題：沒有捷徑');
        const closed = ref.issueShortcuts({ ...detail, closedAt: '2026-01-01T00:00:00' }, { canListOrders: true, canMaintain: true, onMute: () => {} });
        assert.deepEqual(labels(closed), ['這個問題的其他交辦單', '調整這條規則'], '已結案的單不再提供靜音');
    },

    // ── F15-3 代為結案的範圍選項 ────────────────────────────────────────────
    async adminCloseScopeOptions() {
        const dom = installDom();
        const ref = loadModule('pages/work-order-reference.js', {
            appUrl, ruleEditUrl: id => id, roleText: loadElements(dom).roleText, button: loadButton(dom), linkButton: () => null
        });
        const plain = ref.adminCloseScopeOptions({ activeCount: 60 });
        assert.deepEqual(plain.map(o => o.value), ['all'], '沒有上報成員也沒有勾選：只剩全部');
        assert.equal(ref.defaultAdminCloseScope(plain), 'all');
        assert.equal(plain[0].caseIds, null, '全部＝不帶案件清單（整張單結案）');

        const escalated = ref.adminCloseScopeOptions({ activeCount: 60, escalatedCaseIds: ['e1', 'e2'] });
        assert.deepEqual(escalated.map(o => [o.value, o.count]), [['all', 60], ['escalated', 2]]);
        assert.equal(ref.defaultAdminCloseScope(escalated), 'escalated', '有上報成員時預設「只有上報中的」');
        assert.deepEqual(escalated[1].caseIds, ['e1', 'e2']);

        const picked = ref.adminCloseScopeOptions({ activeCount: 60, selectedCaseIds: ['s1'] });
        assert.deepEqual(picked.map(o => o.value), ['all', 'selected']);
        assert.equal(ref.defaultAdminCloseScope(picked), 'all', '只有勾選沒有上報成員：預設仍是全部（不替人縮小範圍）');
        const both = ref.adminCloseScopeOptions({ activeCount: 60, escalatedCaseIds: ['e1'], selectedCaseIds: ['s1', 's2'] });
        assert.deepEqual(both.map(o => o.value), ['all', 'escalated', 'selected']);
    },

    // ── H11 風險日側欄的處理人 ──────────────────────────────────────────────
    async handlingPanelHandlerRows() {
        const dom = installDom();
        const source = readJs('pages/handling-panel.js');
        const formatSource = readJs('core/format.js');
        const formatUserName = new Function(`${extractFunction(formatSource, 'export function formatUserName(')}\nreturn formatUserName;`)();
        const fns = new Function('document', 'appUrl', 'formatUserName', 'helpIcon', `${[
            extractFunction(source, 'export function issueOrdersOf('),
            extractFunction(source, 'function issueOrdersField('),
            extractFunction(source, 'export function handlerRows('),
            extractFunction(source, 'function readonlyField(')
        ].join('\n')}\nreturn { issueOrdersOf, handlerRows };`)(document, appUrl, formatUserName, () => dom.newElement('span'));
        const assignStub = label => { const row = dom.newElement('div'); row.textContent = `指派下拉:${label}`; return row; };

        const issues = [
            { workOrderId: 8, caseHandlerName: '乙', caseHandlerAccount: 'b' },
            { workOrderId: 3, caseHandlerName: '甲', caseHandlerAccount: 'a' },
            { workOrderId: 3, caseHandlerName: '甲', caseHandlerAccount: 'a' },
            { workOrderId: null, caseHandlerName: '丙', caseHandlerAccount: 'c' },   // 沒有單號（舊案件未整併）不列
            { workOrderId: 5, caseHandlerName: null }                                   // 沒有案件
        ];
        const orders = fns.issueOrdersOf(issues);
        assert.deepEqual(orders.map(o => [o.workOrderId, o.issueCount]), [[3, 2], [8, 1]], '依交辦單分組、依單號排序');

        // 1) 有問題層級交辦、沒有日層級處理人：列出各單與各自的處理人，不寫「未指派」
        const withOrders = fns.handlerRows({ canAssign: false, handlerName: null }, orders, assignStub);
        const withText = withOrders.map(textOf).join('|');
        assert(withText.includes('#3') && withText.includes('甲') && withText.includes('#8') && withText.includes('乙'), withText);
        assert(!withText.includes('未指派'), '有問題層級交辦時不顯示「未指派」');
        assert.equal(all(withOrders[0]).filter(n => n.tagName === 'A').map(a => a.href).join(), `${BASE}/work-orders/3,${BASE}/work-orders/8`);

        // 2) 只有日層級指派：行為不變，仍是「處理人：某人」
        const dayOnly = fns.handlerRows({ canAssign: false, handlerName: '丁', handlerAccount: 'd' }, [], assignStub);
        assert.equal(dayOnly.length, 1);
        assert(textOf(dayOnly[0]).includes('處理人') && textOf(dayOnly[0]).includes('丁'));

        // 3) 都沒有：未指派
        const none = fns.handlerRows({ canAssign: false, handlerName: null }, [], assignStub);
        assert(textOf(none[0]).includes('未指派'));

        // 有 Assign 能力：日層級指派下拉照舊在；有交辦單時標籤改成「日層級指派」，沒有時仍叫「處理人」
        const assignWith = fns.handlerRows({ canAssign: true, handlerName: null }, orders, assignStub);
        assert.equal(assignWith.length, 2);
        assert(textOf(assignWith[1]).includes('指派下拉:日層級指派'));
        assert(textOf(fns.handlerRows({ canAssign: true, handlerName: null }, [], assignStub)[0]).includes('指派下拉:處理人'));
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
