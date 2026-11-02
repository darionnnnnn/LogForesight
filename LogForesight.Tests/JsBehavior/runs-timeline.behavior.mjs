/**
 * core/run-timeline.js：階段時間軸與總表執行時間的文字與定位。
 */
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
installDom();
const { renderPhaseTimeline, dayTimingText, renderDayTiming, phaseSeconds, formatDuration } =
    await import(pathToFileURL(path.join(jsRoot, 'core', 'run-timeline.js')).href);

const at = (h, m = 0) => `2026-11-02T${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:00`;
const barOf = row => row.children[1].children[0];

const cases = {
    barsArePositionedByRunSpan() {
        const timeline = renderPhaseTimeline({
            startedAt: at(1), finishedAt: at(5),
            phases: [
                { key: 'local', startedAt: at(1), finishedAt: at(2), outcome: 'success' },
                { key: 'netiq', startedAt: at(1), finishedAt: at(5), outcome: 'partial', note: '分析 3 主機日' },
                { key: 'prtg-triggered', startedAt: at(3), finishedAt: at(4), outcome: 'failed' }
            ]
        });
        assert.equal(timeline.children.length, 3);
        const [local, netiq, sub] = timeline.children;
        assert.equal(barOf(local).style['--lf-phase-start'], '0.00%');
        assert.equal(barOf(local).style['--lf-phase-width'], '25.00%');
        assert.equal(barOf(netiq).style['--lf-phase-width'], '100.00%');
        assert.equal(barOf(sub).style['--lf-phase-start'], '50.00%');
        assert.ok(barOf(netiq).classList.contains('lf-phase-bar--partial'));
        assert.equal(netiq.children[0].textContent, 'NetIQ 機房分析');
        assert.equal(netiq.children[2].textContent, '01:00:00～05:00:00（4 時 0 分）　部分完成　分析 3 主機日');
        assert.ok(sub.classList.contains('lf-phase-row--sub'));
    },

    valueFillIsTopLevelNotSubPhase() {
        const timeline = renderPhaseTimeline({
            startedAt: at(1), finishedAt: at(2),
            phases: [
                { key: 'prtg-value-fill', startedAt: at(1), finishedAt: at(2), outcome: 'success' },
                { key: 'prtg-sync', startedAt: at(1), finishedAt: at(1, 30), outcome: 'success' }
            ]
        });
        assert.ok(!timeline.children[0].classList.contains('lf-phase-row--sub'));
        assert.ok(timeline.children[1].classList.contains('lf-phase-row--sub'));
    },

    missingDayTimingFieldsShowDash() {
        assert.equal(dayTimingText({ firstStartedAt: null, totalSeconds: 5, lastFinishedAt: at(1), interruptedCount: 0 }), '—～01:00（5 秒）');
        assert.equal(dayTimingText({ totalSeconds: null, interruptedCount: 1, lastInterruptedActivityAt: null }), '異常中斷 1 趟（最後活動 —）');
    },

    skippedPhaseSaysSo() {
        const timeline = renderPhaseTimeline({
            startedAt: at(1), finishedAt: at(2),
            phases: [{ key: 'prtg', startedAt: at(1), finishedAt: at(1), outcome: 'skipped', note: 'PRTG 未啟用' }]
        });
        const row = timeline.children[0];
        assert.ok(barOf(row).classList.contains('lf-phase-bar--skipped'));
        assert.ok(row.children[2].textContent.includes('跳過'));
    },

    runningPhaseDrawsToNow() {
        const timeline = renderPhaseTimeline({
            startedAt: at(1),
            phases: [{ key: 'local', startedAt: at(1) }]
        }, at(3));
        const row = timeline.children[0];
        assert.ok(barOf(row).classList.contains('lf-phase-bar--running'));
        assert.equal(barOf(row).style['--lf-phase-width'], '100.00%');
        assert.ok(row.children[2].textContent.includes('進行中'));
    },

    interruptedRunStopsAtLastActivity() {
        const timeline = renderPhaseTimeline({
            startedAt: at(1), finishedAt: at(9), interrupted: true,
            phases: [
                { key: 'local', startedAt: at(1), finishedAt: at(2), outcome: 'success' },
                { key: 'netiq', startedAt: at(1) }
            ]
        }, at(23));
        const [local, netiq] = timeline.children;
        // 比例以最後活動 02:00 為終點，不含重啟前的停機時間
        assert.equal(barOf(local).style['--lf-phase-width'], '100.00%');
        assert.ok(barOf(netiq).classList.contains('lf-phase-bar--interrupted'));
        assert.ok(netiq.children[2].textContent.includes('異常中斷'));
    },

    legacyRunHasNoPhases() {
        const timeline = renderPhaseTimeline({ startedAt: at(1), finishedAt: at(2), phases: null });
        assert.equal(timeline.textContent, '這筆紀錄沒有階段資料。');
    },

    phaseSecondsIgnoresSkippedAndOpen() {
        const phases = [
            { key: 'local', startedAt: at(1), finishedAt: at(2), outcome: 'success' },
            { key: 'netiq', startedAt: at(1), finishedAt: at(1), outcome: 'skipped' },
            { key: 'prtg', startedAt: at(1) }
        ];
        assert.equal(phaseSeconds(phases, 'local'), 3600);
        assert.equal(phaseSeconds(phases, 'netiq'), null);
        assert.equal(phaseSeconds(phases, 'prtg'), null);
        assert.equal(phaseSeconds(null, 'local'), null);
        assert.equal(formatDuration(3660), '1 時 1 分');
    },

    dayTimingCellTexts() {
        assert.equal(dayTimingText(null), '—');
        assert.equal(dayTimingText({ firstStartedAt: at(1), lastFinishedAt: at(5, 47), totalSeconds: 7200, interruptedCount: 0 }),
            '01:00～05:47（2 時 0 分）');
        assert.equal(dayTimingText({ firstStartedAt: at(1), totalSeconds: null, interruptedCount: 1, lastInterruptedActivityAt: at(3, 10) }),
            '異常中斷 1 趟（最後活動 03:10）');
        assert.equal(dayTimingText({ firstStartedAt: null, totalSeconds: null, interruptedCount: 0 }), '—');
    },

    dayTimingBranchesShowEveryPath() {
        const wrap = renderDayTiming({ branches: [{ key: 'netiq', from: at(1), to: at(3), seconds: 7200 }] });
        assert.equal(wrap.children.length, 4);
        assert.equal(wrap.children[0].textContent, '本機分析　—');
        assert.equal(wrap.children[1].textContent, 'NetIQ 機房分析　01:00～03:00（2 時 0 分）');
    }
};

const name = process.argv[2];
if (!name) {
    Object.keys(cases).forEach(key => console.log(key));
    process.exit(0);
}
if (!cases[name]) {
    console.error(`未知案例：${name}`);
    process.exit(2);
}
await cases[name]();
console.log(`PASS ${name}`);
