/**
 * 第 81 輪批次 F（N1）：「不再顯示這個問題：選哪一種」比較表（core/hide-guide.js 的內容與 records.js 的 renderHideGuide），
 * 以及它與操作說明書（19-alert-tools.md）同一份說法。由 HideGuideBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';
import { read, extract, all, jsRoot } from './load-reply-modal.mjs';

const dom = installDom();
const { roleText, disclosure } = loadElements(dom);
const { HIDE_GUIDE_COLUMNS, HIDE_GUIDE_ROWS, HIDE_GUIDE_LEAD } = await import(pathToFileURL(path.join(jsRoot, 'core', 'hide-guide.js')).href);
const records = read('pages', 'records.js');

function env({ caps = ['Assign'] } = {}) {
    const container = document.createElement('div');
    container.classList.add('d-none');
    dom.byId.set('issue-hide-guide', container);
    const tables = [];
    const renderTable = (host, { columns, rows, caption }) => {
        const table = document.createElement('table');
        const trs = rows.map(row => {
            const tr = document.createElement('tr');
            for (const c of columns) { const td = document.createElement('td'); td.appendChild(c.render(row)); tr.appendChild(td); }
            table.appendChild(tr);
            return tr;
        });
        host.appendChild(table);
        tables.push({ columns, rows, caption, trs });
    };
    const currentUser = { caps };
    const hasCapability = (user, cap) => user.caps.includes(cap);
    const body = extract(records, 'function renderHideGuide(');
    const fn = new Function('document', 'currentUser', 'hasCapability', 'disclosure', 'roleText', 'renderTable', 'HIDE_GUIDE_COLUMNS', 'HIDE_GUIDE_ROWS', 'HIDE_GUIDE_LEAD',
        `${body}\nreturn renderHideGuide;`)(document, currentUser, hasCapability, disclosure, roleText, renderTable, HIDE_GUIDE_COLUMNS, HIDE_GUIDE_ROWS, HIDE_GUIDE_LEAD);
    return { render: fn, container, tables };
}

/** 說明書使用者版比較表的資料列：{ 做法: [範圍與期限, 進行中的交辦單, 之後會不會再派工] } */
function helpTable() {
    const md = fs.readFileSync(path.join(jsRoot, '..', '..', 'HelpContent', '19-alert-tools.md'), 'utf8').replaceAll('\r\n', '\n');
    const start = md.indexOf('## 不再顯示這個問題：選哪一種');
    assert(start >= 0, '說明書缺少「不再顯示這個問題：選哪一種」段落');
    const next = md.indexOf('\n## ', start + 5);
    const section = md.slice(start, next < 0 ? md.length : next);
    const lines = section.split('\n').filter(l => l.startsWith('|'));
    const rows = lines.slice(2).map(l => l.split('|').slice(1, -1).map(c => c.trim()));
    return { header: lines[0].split('|').slice(1, -1).map(c => c.trim()), rows };
}

const cases = {
    // 六種機制、四欄：名稱與順序固定
    sixMechanismsFourColumns() {
        assert.deepEqual(HIDE_GUIDE_ROWS.map(r => r.name), ['靜音', '抑制', '已知雜訊', '不處理', '誤報', '統一標記']);
        assert.equal(HIDE_GUIDE_COLUMNS.length, 4);
        for (const row of HIDE_GUIDE_ROWS) {
            for (const key of ['scope', 'orders', 'dispatch']) assert.ok(row[key].length > 0, `${row.name}.${key} 不可空白`);
        }
    },

    // 說明書句長規範（每句 45 字內）也適用畫面上的同一份文字
    everySentenceWithinGuideLimit() {
        for (const row of HIDE_GUIDE_ROWS) {
            for (const key of ['scope', 'orders', 'dispatch']) {
                for (const line of row[key]) {
                    for (const sentence of line.split(/[。；]/).filter(Boolean)) {
                        assert.ok([...sentence].length <= 45, `${row.name}.${key} 句子過長（${[...sentence].length} 字）：${sentence}`);
                    }
                }
            }
        }
    },

    // 畫面與說明書同一份說法：每一格逐字相同（說明書表格把多句接成一格）
    helpManualMatchesScreenData() {
        const { header, rows } = helpTable();
        assert.deepEqual(header, HIDE_GUIDE_COLUMNS);
        assert.equal(rows.length, HIDE_GUIDE_ROWS.length);
        HIDE_GUIDE_ROWS.forEach((row, i) => {
            assert.deepEqual(rows[i], [row.name, row.scope.join(''), row.orders.join(''), row.dispatch.join('')], `第 ${i + 1} 列（${row.name}）與說明書不同`);
        });
    },

    // 具能力＋依問題視角有資料：顯示收合的揭露鈕與六列表格
    rendersCollapsedTableForCapableUser() {
        const { render, container, tables } = env({ caps: ['Maintain'] });
        render(true);
        assert.equal(container.classList.contains('d-none'), false);
        const toggle = all(container).find(n => n.tagName === 'BUTTON');
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.ok(toggle.textContent.includes('不再顯示這個問題：選哪一種'));
        const target = all(container).find(n => n.id === toggle.getAttribute('aria-controls'));
        assert.ok(target.classList.contains('d-none'), '預設收合');
        assert.equal(tables[0].trs.length, 6);
        assert.deepEqual(tables[0].columns.map(c => c.title), HIDE_GUIDE_COLUMNS);
        assert.ok(all(container).some(n => (n.textContent || '') === HIDE_GUIDE_LEAD));
    },

    // 邊界：沒有 Assign、Maintain、Handle 任何一項＝不顯示；不是依問題視角或沒有資料＝不顯示、清空
    hiddenWithoutCapabilityOrData() {
        const none = env({ caps: ['ViewAll'] });
        none.render(true);
        assert.ok(none.container.classList.contains('d-none'));
        assert.equal(none.container.children.length, 0);

        const noData = env({ caps: ['Assign'] });
        noData.render(true);
        noData.render(false);
        assert.ok(noData.container.classList.contains('d-none'));
        assert.equal(noData.container.children.length, 0);
    },

    // 重新渲染不疊加
    rerenderDoesNotStack() {
        const { render, container } = env({ caps: ['Handle'] });
        render(true);
        render(true);
        assert.equal(all(container).filter(n => n.tagName === 'BUTTON').length, 1);
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
