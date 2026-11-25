/**
 * 儀表板「未處理問題」卡副標（K1）：影響台數與下鑽同口徑，風險日拆成「未處理 N、處理中 M」。
 * 切出 dashboard.js 的 todoCardSub 執行，不載入整頁。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { read, extract } from './load-reply-modal.mjs';

installDom();

const source = read('pages', 'dashboard.js');
const build = new Function('formatNumber', `${extract(source, 'function todoCardSub(')}\nreturn todoCardSub;`);
const todoCardSub = build(n => Number(n).toLocaleString('en-US'));

const cases = {
    // 風險日兩個數字分開，不是相加
    async splitsOpenAndInProgress() {
        assert.equal(todoCardSub({ affectedHostCount: 330 }, { openCount: 1200, inProgressCount: 34 }),
            '影響 330 台．風險日未處理 1,200、處理中 34');
    },
    // 邊界：處理中為 0 仍顯示 0，不省略
    async zeroInProgressStillShown() {
        assert.equal(todoCardSub({ affectedHostCount: 0 }, { openCount: 0, inProgressCount: 0 }),
            '影響 0 台．風險日未處理 0、處理中 0');
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
