import assert from 'node:assert/strict';
import { evaluateScheduledAcceptance } from '../../LogForesight.Web/wwwroot/js/core/prtg-acceptance.js';

const day = (outcome, extra = {}) => ({ date: '2026-10-21', outcome, findings: 3, attributedHosts: 2, runId: 9, ...extra });
const okHour = { targets: 10, databaseUsableValues: 10, successes: 4 };
const shortHour = { targets: 10, databaseUsableValues: 6, successes: 4 };
const idleHour = { targets: 0, databaseUsableValues: 0, successes: 0 };

function only(key, input) {
    return evaluateScheduledAcceptance({ scheduleStatus: undefined, aiStatus: undefined, snapshotHours: undefined, ...input })
        .find(item => item.key === key);
}

const cases = {
    scheduledRun() {
        assert.equal(only('scheduled-run', {}).state, 'loading');
        assert.equal(only('scheduled-run', { scheduleStatus: null }).state, 'error');
        assert.equal(only('scheduled-run', { scheduleStatus: { isRunning: true, prtgLastScheduledDay: day('success') } }).state, 'pending');
        const none = only('scheduled-run', { scheduleStatus: { isRunning: false, prtgLastScheduledDay: null } });
        assert.equal(none.state, 'nodata');
        assert.match(none.detail, /立即執行不計入/);
        assert.equal(none.path, '/runs#list');
        const ok = only('scheduled-run', { scheduleStatus: { isRunning: false, prtgLastScheduledDay: day('success') } });
        assert.equal(ok.state, 'ok');
        assert.match(ok.detail, /finding 3 筆、歸戶主機 2 台/);
        // 「檢視該趟執行」直接開那趟執行詳情，不是只到總表
        assert.equal(ok.path, '/runs?run=9#list');
        const partial = only('scheduled-run', { scheduleStatus: { isRunning: false, prtgLastScheduledDay: day('partial') } });
        assert.equal(partial.state, 'pending');
        // 與執行總表徽章同一套用詞
        assert.match(partial.detail, /部分失敗/);
    },
    ai() {
        assert.equal(only('ai', { aiStatus: { idleReason: 'disabled', pendingTotal: 0 } }).state, 'na');
        // 站台啟動後存量校正中：待補 0 件不算跟上（與排程頁 AI 卡同一句）
        assert.equal(only('ai', { aiStatus: { idleReason: 'backfill-pending', isRunning: false, pendingTotal: 0 } }).state, 'pending');
        assert.equal(only('ai', { aiStatus: { isRunning: true, pendingTotal: 0 } }).state, 'pending');
        assert.equal(only('ai', { aiStatus: { isRunning: false, pendingTotal: 5 } }).state, 'pending');
        assert.equal(only('ai', { aiStatus: { isRunning: false, pendingTotal: 0, lastRunSuccess: false } }).state, 'pending');
        assert.equal(only('ai', { aiStatus: { isRunning: false, pendingTotal: 0, lastRunSuccess: true } }).state, 'ok');
        assert.equal(only('ai', { aiStatus: { isRunning: false, pendingTotal: 0, lastRunSuccess: null } }).state, 'ok');
    },
    snapshot() {
        // 分母為零（沒有任何有目標的小時）不得算通過
        assert.equal(only('snapshot', { snapshotHours: [] }).state, 'nodata');
        assert.equal(only('snapshot', { snapshotHours: Array(24).fill(idleHour) }).state, 'nodata');
        // 18／24 恰好 75%，最近一小時有成功 → 通過；17／24 → 待確認
        const pass = only('snapshot', { snapshotHours: [...Array(6).fill(shortHour), ...Array(18).fill(okHour)] });
        assert.equal(pass.state, 'ok');
        assert.match(pass.detail, /18／24 小時品質達標/);
        assert.equal(only('snapshot', { snapshotHours: [...Array(7).fill(shortHour), ...Array(17).fill(okHour)] }).state, 'pending');
        // 比例達標但最近一個完整小時沒有成功的快照 → 待確認
        const stale = only('snapshot', { snapshotHours: [...Array(23).fill(okHour), { ...okHour, successes: 0 }] });
        assert.equal(stale.state, 'pending');
        assert.match(stale.detail, /最近一個完整小時沒有成功的快照/);
        // 無目標的小時不進分母
        assert.equal(only('snapshot', { snapshotHours: [...Array(12).fill(idleHour), ...Array(12).fill(okHour)] }).state, 'ok');
    }
};

const name = process.argv[2];
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
