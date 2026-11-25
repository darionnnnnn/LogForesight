/**
 * 第 76-2 輪批次 B-2：交辦總覽待派頁籤下方的「不再自動派工」區（work-orders.js 的 renderDismissed、
 * restoreDismissedRow、restoreDismissedIssue、reloadDismissed）。以 extractFunction 切出 production 函式本體，
 * 只替換外部相依（表格／空狀態／確認對話框／API／toast 等共用出口）。
 * 由 WorkOrdersDismissedBehaviorTests 以 `node work-orders-dismissed.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton, loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'work-orders.js'), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿/, '');

function extractFunction(declaration) {
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

const SOURCES = [
    'function renderDismissed(', 'async function restoreDismissedRow(', 'async function restoreDismissedIssue(', 'async function reloadDismissed('
].map(extractFunction).join('\n');

const STATUS = {
    wont_fix: { label: '不處理', variant: 'secondary' },
    false_positive: { label: '誤報', variant: 'secondary' },
    known_noise: { label: '已知雜訊', variant: 'secondary' }
};

/** 組一個頁面層環境；confirm＝確認對話框的回答 */
function env({ confirm = true } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const { roleText } = loadElements(dom);
    const el = tag => document.createElement(tag);
    const dismissedContainer = el('div');
    const dismissedPagerContainer = el('div');
    const dismissedCountEl = el('span');
    const log = { empty: [], tables: [], pagination: [], confirms: [], posts: [], toasts: [], loads: 0 };

    const renderEmpty = (container, opts) => { log.empty.push(opts); const n = el('div'); n.textContent = opts.title; container.appendChild(n); };
    // 表格替身：依欄位定義逐列渲染儲存格（欄位 render 回 Node 或字串）
    const renderTable = (container, { columns, rows, caption }) => {
        const table = el('table');
        const head = el('tr');
        for (const c of columns) { const th = el('th'); th.textContent = c.title; th.className = c.className || ''; head.appendChild(th); }
        table.appendChild(head);
        const trs = rows.map(row => {
            const tr = el('tr');
            for (const c of columns) {
                const td = el('td');
                td.className = c.className || '';
                const out = c.render(row);
                td.appendChild(out instanceof Node ? out : document.createTextNode(String(out ?? '')));
                tr.appendChild(td);
            }
            table.appendChild(tr);
            return tr;
        });
        container.appendChild(table);
        log.tables.push({ columns, rows, caption, table, trs });
    };
    const renderIssueCell = (label, explanation) => { const d = el('div'); d.textContent = label + (explanation ? ` ${explanation}` : ''); return d; };
    const statusBadge = (text, variant) => { const s = el('span'); s.textContent = text; s.className = `lf-badge lf-badge--${variant}`; return s; };
    const statusBadgeMeta = value => STATUS[value] ? { label: STATUS[value].label, variant: STATUS[value].variant } : null;
    const formatDate = value => (value ? String(value).slice(0, 10) : '');
    const renderPagination = (container, opts) => { log.pagination.push(opts); };
    const confirmAction = async opts => { log.confirms.push(opts); return confirm; };
    const api = { post: async (url, body) => { log.posts.push({ url, body }); return { restored: body.hostName ? 1 : 4 }; } };
    const toast = (message, kind) => log.toasts.push({ message, kind });
    const guardLoad = async (_container, fn) => fn();
    const loadDismissed = async () => { log.loads++; };

    const factory = new Function('document', 'Node', 'button', 'roleText', 'dismissedContainer', 'dismissedPagerContainer', 'dismissedCountEl',
        'renderEmpty', 'renderTable', 'renderIssueCell', 'statusBadge', 'statusBadgeMeta', 'formatDate', 'renderPagination',
        'confirmAction', 'api', 'toast', 'guardLoad', 'loadDismissed',
        `${SOURCES}\nreturn { renderDismissed, restoreDismissedRow, restoreDismissedIssue, reloadDismissed };`);
    const fns = factory(document, Node, button, roleText, dismissedContainer, dismissedPagerContainer, dismissedCountEl,
        renderEmpty, renderTable, renderIssueCell, statusBadge, statusBadgeMeta, formatDate, renderPagination,
        confirmAction, api, toast, guardLoad, loadDismissed);
    return { ...fns, dismissedContainer, dismissedPagerContainer, dismissedCountEl, log };
}

const row = (hostName, status = 'wont_fix', extra = {}) => ({
    hostName, issueKey: `System|disk|153|${hostName}`, status, closedBy: '王小明', closedAt: '2026-11-09T09:00:00', ...extra
});
const group = (label, hosts, rows) => ({ source: label.split(' ')[0], eventId: Number(label.split(' ')[1]), issueLabel: label, plainExplanation: null, hosts, rows });
const textsOf = (node, predicate) => { const out = []; node.find(n => { if (predicate(n)) out.push(n); return false; }); return out; };
const buttonsOf = node => textsOf(node, n => n.tagName === 'BUTTON');

const cases = {
    // 0 筆：顯示說明文字（標題寫明是什麼資料），沒有表格、沒有按鈕
    emptyShowsExplanation() {
        const { renderDismissed, dismissedContainer, dismissedCountEl, log } = env();
        renderDismissed({ page: 1, pageSize: 50, total: 0, totalHosts: 0, groups: [] }, true);
        assert.equal(log.empty.length, 1);
        assert.equal(log.empty[0].title, '沒有不再自動派工的問題');
        assert.notEqual(log.empty[0].title, '尚無資料');
        assert.ok(log.empty[0].hint.length > 0);
        assert.equal(log.tables.length, 0);
        assert.equal(buttonsOf(dismissedContainer).length, 0);
        assert.equal(dismissedCountEl.textContent, '');
    },

    // 多筆：依問題分組、每組一個標頭＋表格；欄位與欄寬類別符合設計系統
    groupsRenderOneTablePerIssue() {
        const { renderDismissed, dismissedCountEl, log } = env();
        const groups = [
            group('disk 153', 3, [row('HOST-A'), row('HOST-B', 'false_positive'), row('HOST-C', 'known_noise', { closedBy: null, closedAt: null })]),
            group('ntfs 55', 1, [row('HOST-D')])
        ];
        renderDismissed({ page: 1, pageSize: 50, total: 2, totalHosts: 4, groups }, true);
        assert.equal(log.tables.length, 2);
        assert.equal(dismissedCountEl.textContent, '共 2 個問題、4 台');
        const t = log.tables[0];
        assert.deepEqual(t.columns.map(c => c.title), ['主機', '先前結案狀態', '結案的人', '日期', '恢復派工', '']);
        const cls = title => t.columns.find(c => c.title === title).className || '';
        assert.ok(cls('主機').includes('text-nowrap'));
        assert.ok(cls('先前結案狀態').includes('lf-col-nowrap'));
        assert.ok(cls('日期').includes('lf-col-nowrap'));
        assert.ok(!cls('結案的人').includes('lf-col-nowrap'), '結案的人是吸收欄');
        // 狀態徽章、日期用 lf-text-data、缺值用破折號
        assert.equal(t.trs[1].children[1].textContent, '誤報');
        assert.ok(t.trs[0].children[3].find(n => n.className === 'lf-text-data'));
        assert.equal(t.trs[0].children[3].textContent, '2026-11-09');
        assert.equal(t.trs[2].children[2].textContent, '—');
        assert.equal(t.trs[2].children[3].textContent, '—');
    },

    // 批次 S：恢復派工欄三種標示——N 天後恢復派工（高嚴重度）、上線前判定、其餘永久
    releaseColumnShowsDaysPreLaunchOrPermanent() {
        const { renderDismissed, log } = env();
        const rows = [
            row('HOST-A', 'wont_fix', { releaseInDays: 23, preLaunch: false }),
            row('HOST-B', 'wont_fix', { releaseInDays: null, preLaunch: true }),
            row('HOST-C', 'wont_fix', { releaseInDays: null, preLaunch: false }),
            row('HOST-D', 'wont_fix')   // 舊回應沒有欄位：永久
        ];
        renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 4, groups: [group('disk 153', 4, rows)] }, true);
        const t = log.tables[0];
        const col = t.columns.findIndex(c => c.title === '恢復派工');
        assert.ok(col >= 0, '有恢復派工欄');
        assert.ok((t.columns[col].className || '').includes('lf-col-nowrap'), '短值欄不折行');
        assert.equal(t.trs[0].children[col].textContent, '23 天後恢復派工');
        assert.equal(t.trs[1].children[col].textContent, '上線前判定');
        assert.ok(t.trs[1].children[col].find(n => n.title && n.title.includes('不會自動恢復')), '上線前判定的說明放在 title');
        assert.equal(t.trs[2].children[col].textContent, '永久');
        assert.equal(t.trs[3].children[col].textContent, '永久');
    },

    // 按鈕：每組標頭一顆「整個問題恢復」（outline-secondary），每列一顆「恢復」（outline-primary）；沒有能力時都不出現
    restoreButtonsExistOnlyWithCapability() {
        const withCap = env();
        withCap.renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 2, groups: [group('disk 153', 2, [row('HOST-A'), row('HOST-B')])] }, true);
        const buttons = buttonsOf(withCap.dismissedContainer);
        const whole = buttons.filter(b => b.textContent === '整個問題恢復');
        const single = buttons.filter(b => b.textContent === '恢復');
        assert.equal(whole.length, 1);
        assert.ok(whole[0].className.includes('btn-outline-secondary') && whole[0].className.includes('btn-sm'));
        assert.equal(single.length, 2);
        assert.ok(single.every(b => b.className.includes('btn-outline-primary') && b.className.includes('btn-sm')));

        const noCap = env();
        noCap.renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 2, groups: [group('disk 153', 2, [row('HOST-A'), row('HOST-B')])] }, false);
        assert.equal(buttonsOf(noCap.dismissedContainer).length, 0);
        assert.deepEqual(noCap.log.tables[0].columns.map(c => c.title), ['主機', '先前結案狀態', '結案的人', '日期', '恢復派工']);
    },

    // 分頁：總頁數＝問題組數 ÷ 每頁組數
    paginationUsesServerPageSize() {
        const { renderDismissed, log } = env();
        renderDismissed({ page: 2, pageSize: 50, total: 120, totalHosts: 130, groups: [group('disk 153', 1, [row('HOST-A')])] }, true);
        assert.equal(log.pagination.length, 1);
        assert.equal(log.pagination[0].page, 2);
        assert.equal(log.pagination[0].totalPages, 3);
    },

    // 單筆恢復：確認內文寫出主機與問題、確認鈕是 primary；確認後打單筆端點、toast、就地重載
    async restoreRowConfirmsThenPostsAndReloads() {
        const { renderDismissed, dismissedContainer, log } = env();
        renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 1, groups: [group('disk 153', 1, [row('HOST-A')])] }, true);
        const btn = buttonsOf(dismissedContainer).find(b => b.textContent === '恢復');
        await Promise.all(btn.listeners.click.map(h => h()));
        assert.equal(log.confirms.length, 1);
        assert.equal(log.confirms[0].confirmVariant, 'primary');
        assert.ok(log.confirms[0].message.includes('HOST-A') && log.confirms[0].message.includes('disk 153'));
        assert.deepEqual(log.posts, [{ url: '/api/work-orders/dismissed/restore', body: { hostName: 'HOST-A', issueKey: 'System|disk|153|HOST-A' } }]);
        assert.equal(log.toasts.length, 1);
        assert.equal(log.toasts[0].kind, 'success');
        assert.equal(log.loads, 1);
    },

    // 整個問題恢復：確認內文寫出台數與問題；打整個問題端點
    async restoreIssueConfirmsThenPosts() {
        const { renderDismissed, dismissedContainer, log } = env();
        renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 4, groups: [group('disk 153', 4, [row('HOST-A')])] }, true);
        const btn = buttonsOf(dismissedContainer).find(b => b.textContent === '整個問題恢復');
        await Promise.all(btn.listeners.click.map(h => h()));
        assert.equal(log.confirms[0].confirmVariant, 'primary');
        assert.ok(log.confirms[0].message.includes('4 台') && log.confirms[0].message.includes('disk 153'));
        assert.deepEqual(log.posts, [{ url: '/api/work-orders/dismissed/restore-issue', body: { source: 'disk', eventId: 153 } }]);
        assert.equal(log.loads, 1);
    },

    // 按「取消」：什麼都不打、不重載
    async cancelDoesNothing() {
        const { renderDismissed, dismissedContainer, log } = env({ confirm: false });
        renderDismissed({ page: 1, pageSize: 50, total: 1, totalHosts: 1, groups: [group('disk 153', 1, [row('HOST-A')])] }, true);
        const btn = buttonsOf(dismissedContainer).find(b => b.textContent === '恢復');
        await Promise.all(btn.listeners.click.map(h => h()));
        assert.equal(log.posts.length, 0);
        assert.equal(log.toasts.length, 0);
        assert.equal(log.loads, 0);
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
