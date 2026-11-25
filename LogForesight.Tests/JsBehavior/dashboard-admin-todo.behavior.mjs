/**
 * 第 81 輪批次 F（F11-1）：儀表板頂端「需要你處理」一列與待派工問題卡的副標（dashboard.js 的 adminTodoItems、renderAdminTodo、pendingDispatchHint）。
 * 切出 production 函式執行，不載入整頁。由 DashboardAdminTodoBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';
import { read, extract, all } from './load-reply-modal.mjs';

const dom = installDom();
const { roleText } = loadElements(dom);
const source = read('pages', 'dashboard.js');

function env() {
    const container = document.createElement('div');
    dom.byId.set('dashboard-todo', container);
    const body = ['function adminTodoItems(', 'function renderAdminTodo(', 'function pendingDispatchHint('].map(d => extract(source, d)).join('\n');
    const fns = new Function('document', 'roleText', 'formatNumber', 'appUrl', `${body}\nreturn { adminTodoItems, renderAdminTodo, pendingDispatchHint };`)(
        document, roleText, n => String(n), p => '/lf' + p);
    return { ...fns, container };
}

const todo = (o = {}) => ({ todoIncluded: true, escalatedCount: 0, newEscalatedCount: 0, unassignableCount: 0, noRecipientCount: 0, recurrenceCount: 0,
    fetchFailedCount: 0, dispatchableCount: 0, inactiveHandlerCount: 0, ungroupedHostCount: 0, ...o });
const links = node => all(node).filter(n => n.tagName === 'A').map(a => ({ text: a.textContent, href: a.href }));

const cases = {
    // 具 Assign：每一項連到處理點，順序固定（取數失敗、今天新上報、逾期、可派未派、派不出去…）
    itemsLinkToTheirHandlingPlaces() {
        const { renderAdminTodo, container } = env();
        renderAdminTodo({ adminTodo: todo({
            fetchFailedCount: 1, newEscalatedCount: 2, escalatedCount: 3, dispatchableCount: 4, unassignableCount: 5, noRecipientCount: 6,
            inactiveHandlerCount: 7, recurrenceCount: 8, ungroupedHostCount: 9 }) });
        assert.deepEqual(links(container), [
            { text: '最近一趟取數失敗', href: '/lf/runs' },
            { text: '今天新上報 2 筆', href: '/lf/work-orders?status=escalated' },
            { text: '上報逾期 3 筆', href: '/lf/work-orders?status=escalated' },
            { text: '可派未派 4 個問題', href: '/lf/work-orders#gaps' },
            { text: '派不出去 5 個問題', href: '/lf/work-orders#gaps' },
            { text: '上報沒有收件人 6 筆', href: '/lf/work-orders?status=escalated' },
            { text: '處理人已停用或暫停、仍有單 7 人', href: '/lf/work-orders?status=active&handlerInactive=1' },
            { text: '反覆復發 8 筆', href: '/lf/work-orders?status=active' },
            { text: '未分組主機 9 台', href: '/lf/admin/hosts?status=ungrouped' }
        ]);
        assert.ok(container.textContent.includes('需要你處理'));
    },

    // 邊界：只列數字不是 0 的類別
    zeroCategoriesAreOmitted() {
        const { renderAdminTodo, container } = env();
        renderAdminTodo({ adminTodo: todo({ dispatchableCount: 1 }) });
        assert.deepEqual(links(container).map(l => l.text), ['可派未派 1 個問題']);
    },

    // 全部為 0：收成一行文字，沒有連結
    allZeroCollapsesToOneLine() {
        const { renderAdminTodo, container } = env();
        renderAdminTodo({ adminTodo: todo() });
        assert.equal(container.textContent, '目前沒有需要你處理的項目');
        assert.equal(links(container).length, 0);
    },

    // Manager、Dev：後端不回待辦（null）或標示未納入＝整列不顯示
    notShownWithoutAssign() {
        const nullTodo = env();
        nullTodo.renderAdminTodo({ adminTodo: null });
        assert.equal(nullTodo.container.children.length, 0);
        const notIncluded = env();
        notIncluded.renderAdminTodo({ adminTodo: todo({ todoIncluded: false, escalatedCount: 3 }) });
        assert.equal(notIncluded.container.children.length, 0);
        const missing = env();
        missing.renderAdminTodo({});
        assert.equal(missing.container.children.length, 0);
    },

    // 待派問題超過上限、算不出可派數字：仍提示去縮小期間
    tooLargePendingStillPointsToGaps() {
        const { renderAdminTodo, container } = env();
        renderAdminTodo({ adminTodo: todo(), pendingDispatch: { tooLarge: true } });
        assert.deepEqual(links(container), [{ text: '待派問題太多，請縮小期間', href: '/lf/work-orders#gaps' }]);
    },

    // 重新渲染不疊加上一次的內容
    rerenderReplacesPreviousContent() {
        const { renderAdminTodo, container } = env();
        renderAdminTodo({ adminTodo: todo({ escalatedCount: 1 }) });
        renderAdminTodo({ adminTodo: todo() });
        assert.equal(container.textContent, '目前沒有需要你處理的項目');
    },

    // A3：待派工問題卡副標——兩段分開寫；0 個可派仍寫出 0；全 0 與超過上限各有自己的句子
    pendingCardHintSplitsTwoSections() {
        const { pendingDispatchHint } = env();
        assert.equal(pendingDispatchHint({ dispatchableIssueCount: 3, unassignableIssueCount: 2 }), '可以派 3 個、派不出去 2 個');
        assert.equal(pendingDispatchHint({ dispatchableIssueCount: 0, unassignableIssueCount: 2 }), '可以派 0 個、派不出去 2 個');
        assert.equal(pendingDispatchHint({ dispatchableIssueCount: 0, unassignableIssueCount: 0 }), '近 7 天沒人接的問題');
        assert.equal(pendingDispatchHint({ tooLarge: true }), '超過上限，請到待派頁縮小期間');
        assert.equal(pendingDispatchHint({}), '近 7 天沒人接的問題');
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
