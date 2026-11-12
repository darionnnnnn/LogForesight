/**
 * 報表頁大螢幕模式（pages/reports-wall.js 的狀態機與排程、pages/reports.js 的轉接、core/charts.js 的字級倍率）的行為測試。
 *
 * reports-wall.js 是純邏輯模組，直接以 production 原檔載入，視窗、文件、網址與計時器換成可控替身（假時鐘）；
 * reports.js 與 charts.js 沿用切出 production 函式原文的作法，只替換 DOM、api 與 Chart 邊界。
 * 由 ReportsWallBehaviorTests 以 `node reports-wall.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = (...parts) => fs.readFileSync(path.join(jsRoot, ...parts), 'utf8').replaceAll('\r\n', '\n');

const dom = installDom();
const wallModule = await import(pathToFileURL(path.join(jsRoot, 'pages', 'reports-wall.js')).href);
const { createWall, WALL_REFRESH_MS, wallScale, formatClock, WALL_CLASS } = wallModule;

const MINUTE = 60 * 1000;
const flush = () => new Promise(resolve => setImmediate(resolve));

function extractFunction(source, declaration) {
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

/** 以 production 函式原文建立可執行實例；prelude 提供外部變數與替身 */
function build(source, declarations, prelude, exportsExpr, args = {}) {
    const names = Object.keys(args);
    const body = declarations.map(d => extractFunction(source, d).replace(/^export /, '')).join('\n');
    return new Function(...names, `${prelude}\n${body}\nreturn ${exportsExpr};`)(...names.map(n => args[n]));
}

// ── 可控的瀏覽器環境 ────────────────────────────────────────────────────────

/**
 * 假時鐘＋視窗／文件／網址替身。refresh 由測試以 env.finishRefresh(ok) 手動完成，
 * 才能表達「上一次還沒完成」與「完成後才排下一次」。
 */
function makeEnv({ search = '', innerHeight = 1080, wide = true, visible = true } = {}) {
    let now = new Date(2026, 10, 11, 9, 0).getTime();
    let nextId = 1;
    const timers = new Map();
    const docListeners = {};
    const winListeners = {};
    const mqListeners = [];
    const classes = new Set();
    const calls = { refresh: 0, rerender: 0, scales: [], exitVisible: [], replace: [], states: 0, exitFullscreen: 0 };
    let pending = null;

    const mq = { matches: wide, addEventListener: (_type, handler) => mqListeners.push(handler) };
    const win = {
        innerHeight,
        matchMedia: () => mq,
        addEventListener: (type, handler) => { (winListeners[type] ||= []).push(handler); }
    };
    const documentElement = {
        classList: { add: n => classes.add(n), remove: n => classes.delete(n), contains: n => classes.has(n) },
        requestFullscreen: async () => { doc.fullscreenElement = documentElement; }
    };
    const doc = {
        documentElement,
        visibilityState: visible ? 'visible' : 'hidden',
        fullscreenElement: null,
        exitFullscreen: async () => { calls.exitFullscreen++; doc.fullscreenElement = null; },
        addEventListener: (type, handler) => { (docListeners[type] ||= []).push(handler); }
    };
    const loc = { search, pathname: '/reports' };
    const hist = {
        replaceState: (_s, _t, url) => {
            calls.replace.push(url);
            loc.search = url.startsWith('?') ? url : '';
        }
    };

    const env = {
        calls, classes, doc, loc, mq, timers,
        get now() { return now; },
        wall: createWall({
            win, doc, loc, hist,
            now: () => now,
            setTimer: (fn, ms) => { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; },
            clearTimer: id => { timers.delete(id); },
            refresh: () => { calls.refresh++; return new Promise(resolve => { pending = resolve; }); },
            rerender: () => { calls.rerender++; },
            applyScale: scale => { calls.scales.push(scale); },
            setExitVisible: v => { calls.exitVisible.push(v); },
            onState: () => { calls.states++; }
        }),
        /** 前進假時鐘並依序觸發到期的計時器，每次觸發後讓非同步工作跑完 */
        async advance(ms) {
            const target = now + ms;
            for (; ;) {
                const due = [...timers.entries()].filter(([, t]) => t.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
                if (!due) break;
                timers.delete(due[0]);
                now = Math.max(now, due[1].at);
                due[1].fn();
                await flush();
            }
            now = target;
            await flush();
        },
        /** 完成目前在途的 refresh（ok 為 false 時以失敗結束） */
        async finishRefresh(ok = true) {
            assert(pending, '沒有在途的 refresh');
            const resolve = pending;
            pending = null;
            resolve(ok);
            await flush();
        },
        get inflight() { return pending !== null; },
        emit(type, event = {}) {
            for (const handler of docListeners[type] ?? []) handler(event);
        },
        emitMedia() { mqListeners.forEach(h => h()); },
        setVisibility(state) { doc.visibilityState = state; }
    };
    return env;
}

/** 模擬頁面首次載入：開始、成功渲染（renderAll 通知）、結束 */
function firstLoad(env, ok = true) {
    env.wall.loadStarted();
    if (ok) env.wall.dataLoaded();
    env.wall.loadSettled(ok);
}

const WALL_URL = '?display=wall';

const cases = {
    // ── 自動更新排程 ──────────────────────────────────────────────────────
    async 完成後15分才發下一次且未完成時不發第二次() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        firstLoad(env);
        assert.equal(env.calls.refresh, 0, '首次載入完成不會立刻再發');

        await env.advance(WALL_REFRESH_MS - 1000);
        assert.equal(env.calls.refresh, 0, '未滿 15 分不發');
        await env.advance(1000);
        assert.equal(env.calls.refresh, 1, '滿 15 分發第一次更新');

        // 上一次未完成：時間再久、分頁可見性變化都不會發第二個請求
        await env.advance(20 * MINUTE);
        env.emit('visibilitychange');
        await flush();
        assert.equal(env.calls.refresh, 1, '在途時不發第二個請求');

        // 完成後才排下一次：從「完成那一刻」起算 15 分，不是從上次發出起算
        await env.finishRefresh(true);
        await env.advance(WALL_REFRESH_MS - 1000);
        assert.equal(env.calls.refresh, 1, '完成後未滿 15 分不發');
        await env.advance(1000);
        assert.equal(env.calls.refresh, 2, '完成後滿 15 分發下一次');
        assert.equal(env.timers.size, 0, '在途期間不留計時器');
    },
    async 分頁隱藏時不發_恢復可見且已逾間隔立刻發_未逾間隔不發() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        firstLoad(env);

        // 到期時分頁是隱藏的：不發請求、也不續排
        env.setVisibility('hidden');
        await env.advance(WALL_REFRESH_MS);
        await env.advance(2 * 60 * MINUTE);
        assert.equal(env.calls.refresh, 0, '隱藏時不發請求');
        assert.equal(env.timers.size, 0, '隱藏時不空轉計時器');

        // 回到可見且已逾間隔：立刻載入一次
        env.setVisibility('visible');
        env.emit('visibilitychange');
        await flush();
        assert.equal(env.calls.refresh, 1, '逾間隔回到可見要立刻補一次');
        await env.finishRefresh(true);

        // 隱藏一段時間但未逾間隔：回到可見時不發，照原排程到點才發
        env.setVisibility('hidden');
        await env.advance(5 * MINUTE);
        env.setVisibility('visible');
        env.emit('visibilitychange');
        await flush();
        assert.equal(env.calls.refresh, 1, '未逾間隔回到可見不發');
        await env.advance(10 * MINUTE - 1000);
        assert.equal(env.calls.refresh, 1);
        await env.advance(1000);
        assert.equal(env.calls.refresh, 2, '照原排程到點才發');
    },

    // ── 快捷期間滾動 ──────────────────────────────────────────────────────
    async 快捷期間跨午夜後起訖各前進一天_自訂日期不變() {
        const reports = read('pages', 'reports.js');
        const rangeSource = read('core', 'date-range.js');
        const formatSource = read('core', 'format.js');
        const toLocalDateString = build(formatSource, ['export function toLocalDateString('], '', 'toLocalDateString');
        const rangeFromDays = build(rangeSource, ['export function rangeFromDays('], '', 'rangeFromDays', { toLocalDateString });

        const RealDate = Date;
        let clock = new RealDate(2026, 10, 11, 10, 0).getTime();   // 2026-11-11 10:00
        class FakeDate extends RealDate {
            constructor(...args) { if (args.length === 0) super(clock); else super(...args); }
        }

        const inputs = { 'report-from': { value: '' }, 'report-to': { value: '' }, 'report-compare': { value: 'previous' } };
        const loaded = [];
        const state = { failing: false };
        const make = presetDays => build(reports, ['async function wallRefresh(', 'function setRange('], `
            let activePresetDays = presetDays;
            let userSelectedCompare = false;
            const document = { getElementById: id => inputs[id] };
            const defaultCompareFor = () => 'previous';
            const updateYoyOptionLabel = () => {};
            const clearDisplaySettingsCache = () => { state.settingsCleared = (state.settingsCleared ?? 0) + 1; };
            const loadReport = async () => {
                loaded.push([inputs['report-from'].value, inputs['report-to'].value]);
                if (state.failing) throw new Error('boom');
                return true;
            };
        `, '{ wallRefresh, setRange, preset: () => activePresetDays }', { presetDays, inputs, loaded, rangeFromDays, state });

        globalThis.Date = FakeDate;
        try {
            // 快捷「近 7 天」：進頁時算一次，跨午夜後每次更新起訖各前進一天
            const preset = make(7);
            preset.setRange(7);
            assert.deepEqual([inputs['report-from'].value, inputs['report-to'].value], ['2026-11-04', '2026-11-10']);
            assert.equal(await preset.wallRefresh(), true);
            assert.deepEqual(loaded.at(-1), ['2026-11-04', '2026-11-10'], '同一天內更新，起訖不變');
            assert.equal(state.settingsCleared, 1, '每次自動更新都重取顯示設定（大螢幕整天不重新整理）');

            clock = new RealDate(2026, 10, 12, 0, 5).getTime();   // 跨過午夜
            assert.equal(await preset.wallRefresh(), true);
            assert.deepEqual(loaded.at(-1), ['2026-11-05', '2026-11-11'], '跨午夜後起訖各前進一天');

            // 自訂日期：固定不動（activePresetDays 為 null）
            const custom = make(null);
            inputs['report-from'].value = '2026-10-01';
            inputs['report-to'].value = '2026-10-05';
            assert.equal(custom.preset(), null);
            clock = new RealDate(2026, 10, 14, 0, 5).getTime();
            assert.equal(await custom.wallRefresh(), true);
            assert.deepEqual(loaded.at(-1), ['2026-10-01', '2026-10-05'], '自訂日期不得被滾動');

            // 取數失敗：回 false（由 wall 記成更新失敗），不外擲
            state.failing = true;
            assert.equal(await custom.wallRefresh(), false);
        } finally {
            globalThis.Date = RealDate;
        }
    },

    // ── 更新失敗 ──────────────────────────────────────────────────────────
    async 更新失敗保留舊資料且標頭寫更新失敗_下一次成功後消失() {
        // 資料側：production 的 loadReport 在取數失敗時不得動 currentData
        const reports = read('pages', 'reports.js');
        const old = { from: 'a', to: 'b', marker: 'old' };
        const loadInst = build(reports, ['async function loadReport('], `
            let currentData = old;
            let retentionDays = 90;
            let currentScope = 'all';
            let requestedScope = null;
            let visibleDayRisk = null;
            const reportGuard = { next: () => 1, isCurrent: () => true };
            const document = { getElementById: () => ({ value: 'x' }) };
        `, '{ loadReport, data: () => currentData }', {
            old,
            api: { get: () => Promise.reject(new Error('逾時')) },
            getDisplaySettings: async () => ({})
        });
        await assert.rejects(loadInst.loadReport(), /逾時/);
        assert.equal(loadInst.data().marker, 'old', '失敗時保留畫面上的舊資料');

        // 狀態側：標頭文字與恢復
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        firstLoad(env);
        assert.deepEqual(env.wall.statusText(), { updated: '最後更新 09:00', stale: '' });

        await env.advance(WALL_REFRESH_MS);
        await env.finishRefresh(false);
        assert.deepEqual(env.wall.statusText(), { updated: '最後更新 09:00', stale: '更新失敗，目前是 09:00 的資料' });
        assert.equal(formatClock(new Date(2026, 10, 11, 9, 5).getTime()), '09:05', 'HH:mm 補零');

        // 下一輪照常再試；成功後訊息消失、時間更新
        await env.advance(WALL_REFRESH_MS);
        assert.equal(env.calls.refresh, 2, '失敗後下一輪照常再試');
        await env.finishRefresh(true);
        assert.deepEqual(env.wall.statusText(), { updated: '最後更新 09:30', stale: '' });
    },
    async 首次載入失敗不寫舊資料提示並排下一次重試() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        firstLoad(env, false);
        assert.deepEqual(env.wall.statusText(), { updated: '', stale: '' }, '沒有舊資料可說，由頁面的錯誤狀態負責');
        assert.equal(env.timers.size, 1, '首次失敗也要排下一次');

        await env.advance(WALL_REFRESH_MS - 1000);
        assert.equal(env.calls.refresh, 0);
        await env.advance(1000);
        assert.equal(env.calls.refresh, 1, '15 分後重試');
        await env.finishRefresh(true);
        assert.equal(env.wall.statusText().stale, '');
        assert.equal(env.wall.statusText().updated, '最後更新 09:15');
    },

    // ── 離開 ──────────────────────────────────────────────────────────────
    async Esc離開時網址與類別與計時器全部還原() {
        const env = makeEnv({ search: '?display=wall&handlingScope=open' });
        env.wall.init();
        firstLoad(env);
        assert.equal(env.wall.isActive(), true);
        assert(env.classes.has(WALL_CLASS), '進入時 <html> 有大螢幕類別');
        assert.equal(env.timers.size, 1);
        const rerenders = env.calls.rerender;

        env.emit('keydown', { key: 'Enter' });
        assert.equal(env.wall.isActive(), true, '其他按鍵不離開');

        env.emit('keydown', { key: 'Escape' });
        assert.equal(env.wall.isActive(), false);
        assert.equal(env.calls.replace.at(-1), '?handlingScope=open', '只移除 display，保留其他參數');
        assert(!env.classes.has(WALL_CLASS), '<html> 類別已移除');
        assert.equal(env.timers.size, 0, '計時器已清掉');
        assert.equal(env.calls.scales.at(-1), null, '字級倍率還原');
        assert(env.calls.rerender > rerenders, '以一般模式重繪');

        await env.advance(2 * WALL_REFRESH_MS);
        assert.equal(env.calls.refresh, 0, '離開後不再自動更新');
    },
    async Esc離開只有display參數時網址回到無查詢字串() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        env.emit('keydown', { key: 'Escape' });
        assert.equal(env.calls.replace.at(-1), '/reports');
    },
    async 按鈕進入後全螢幕退出事件會離開模式() {
        const env = makeEnv();
        env.wall.init();
        assert.equal(env.wall.isActive(), false);
        assert.equal(await env.wall.enter(), true);
        assert.equal(env.calls.replace.at(-1), '?display=wall', '進入時網址加上 display=wall');
        assert(env.classes.has(WALL_CLASS));
        assert.equal(env.wall.isActive(), true);
        assert(env.doc.fullscreenElement, '按鈕進入會要求瀏覽器全螢幕');

        // 全螢幕還在時的 fullscreenchange（例如進入當下）不離開
        env.emit('fullscreenchange');
        assert.equal(env.wall.isActive(), true);

        // 使用者以瀏覽器方式退出全螢幕
        env.doc.fullscreenElement = null;
        env.emit('fullscreenchange');
        assert.equal(env.wall.isActive(), false);
        assert(!env.classes.has(WALL_CLASS));
        assert.equal(env.calls.replace.at(-1), '/reports');
    },
    async 按鈕離開時仍在全螢幕就退出全螢幕() {
        const env = makeEnv();
        env.wall.init();
        await env.wall.enter();
        env.wall.exit();
        assert.equal(env.calls.exitFullscreen, 1);
        assert.equal(env.doc.fullscreenElement, null);
    },
    async 全螢幕被拒絕時模式照樣生效且不受fullscreenchange影響() {
        const env = makeEnv();
        env.wall.init();
        env.doc.documentElement.requestFullscreen = async () => { throw new Error('denied'); };
        await env.wall.enter();
        assert.equal(env.wall.isActive(), true, '全螢幕被拒絕只略過，模式照樣生效');
        env.emit('fullscreenchange');
        assert.equal(env.wall.isActive(), true, '沒開全螢幕就不跟著全螢幕退出而離開');
    },
    async 網址進入時全螢幕退出事件不離開() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        assert.equal(env.wall.isActive(), true);
        assert.equal(env.doc.fullscreenElement, null, '網址進入不要求全螢幕');
        env.emit('fullscreenchange');
        assert.equal(env.wall.isActive(), true, '由網址進入的不受 fullscreenchange 影響');
        assert(env.classes.has(WALL_CLASS));
    },

    // ── 寬度不足 ──────────────────────────────────────────────────────────
    async 寬度不足時不啟動自動更新且按鈕隱藏() {
        const env = makeEnv({ search: WALL_URL, wide: false });
        env.wall.init();
        assert.equal(env.wall.canEnter(), false, '按鈕不顯示');
        assert.equal(env.wall.isActive(), false, '不執行大螢幕行為');
        firstLoad(env);
        assert.equal(env.timers.size, 0, '不排自動更新');
        await env.advance(2 * WALL_REFRESH_MS);
        assert.equal(env.calls.refresh, 0);
        assert.equal(await env.wall.enter(), false, '按鈕入口也不生效');
        assert.equal(env.calls.replace.length, 0, '不改網址');

        // 視窗放寬到 992 以上：網址要求的模式開始生效
        env.mq.matches = true;
        env.emitMedia();
        assert.equal(env.wall.canEnter(), true);
        assert.equal(env.wall.isActive(), true);
        assert.equal(env.timers.size, 1, '放寬後才排自動更新');

        // 再窄回去：停止
        env.mq.matches = false;
        env.emitMedia();
        assert.equal(env.wall.isActive(), false);
        assert.equal(env.timers.size, 0);
    },

    // ── 字級 ──────────────────────────────────────────────────────────────
    字級倍率隨視窗高度等比放大且下限為1() {
        assert.equal(wallScale(1080), 1);
        assert.equal(wallScale(2160), 2);
        assert.equal(wallScale(1620), 1.5);
        assert.equal(wallScale(720), 1, '矮視窗不縮小');
        assert.equal(wallScale(undefined), 1);
    },
    async 進入時套用倍率_視窗高度改變時重算_離開還原() {
        const env = makeEnv({ search: WALL_URL, innerHeight: 2160 });
        env.wall.init();
        assert.equal(env.calls.scales.at(-1), 2, '3840×2160 的倍率是 2');
        const rerenders = env.calls.rerender;
        env.emit('resize');
        assert.equal(env.calls.rerender, rerenders, '倍率沒變不重繪');
        env.emit('keydown', { key: 'Escape' });
        assert.equal(env.calls.scales.at(-1), null);
    },
    async 離開鈕滑鼠移動出現且3秒後淡出() {
        const env = makeEnv({ search: WALL_URL });
        env.wall.init();
        env.emit('mousemove');
        assert.equal(env.calls.exitVisible.at(-1), true);
        await env.advance(2900);
        assert.equal(env.calls.exitVisible.at(-1), true, '3 秒內持續顯示');
        env.emit('mousemove');   // 重新計時
        await env.advance(2900);
        assert.equal(env.calls.exitVisible.at(-1), true);
        await env.advance(200);
        assert.equal(env.calls.exitVisible.at(-1), false, '停止移動 3 秒後淡出');
    },

    // ── reports.js 轉接 ───────────────────────────────────────────────────
    大螢幕生效時第一排卡片不帶連結() {
        const reports = read('pages', 'reports.js');
        const container = dom.newElement('div');
        dom.byId.set('report-kpi', container);
        const captured = [];
        const formatNumber = n => String(n);
        const make = active => build(reports, [
            'function laggingText(', 'function coverageCard(', 'function openRiskCard(', 'function comparisonBadge(',
            'function compareSub(', 'function scopeDrillParams(', 'function buildKpiCards(', 'function renderKpi('
        ], `
            const currentData = data;
            let currentScope = 'all';
            const wall = { isActive: () => active };
        `, '{ renderKpi }', {
            data: {
                from: '2026-10-12', to: '2026-11-10', totalHosts: 12,
                kpi: { highRiskDays: 5, highRiskDaysPrevious: 2, affectedHosts: 3, affectedHostsPrevious: 3 },
                handling: { openCount: 4, inProgressCount: 1, resolvedCount: 0, overdueCount: 0 },
                coverage: { missingHostDays: 0, lagging: false, noData: false }
            },
            active,
            formatNumber,
            recordsUrl: (o) => `/records?riskLevels=${o.riskLevels ?? ''}`,
            roleText: (_r, t) => t,
            kpiStrip: groups => { captured.push(groups[0].cards); return dom.newElement('div'); }
        });
        make(false).renderKpi();
        assert(captured.at(-1).filter(c => c.url).length >= 3, '一般模式的下鑽卡片照舊帶連結');
        make(true).renderKpi();
        assert.equal(captured.at(-1).length, 4);
        assert(captured.at(-1).every(c => !c.url), '大螢幕模式所有卡片都不帶連結（kpiStrip 不會輸出 <a>）');
        assert.deepEqual(captured.at(-1).map(c => c.label), ['高風險日', '受影響主機', '未結案風險日', '資料完整度']);
    },
    圖表轉接_大螢幕不掛下鑽_重繪不做進場動畫() {
        const reports = read('pages', 'reports.js');
        const make = (active, animate) => build(reports, ['function adaptChartSpec('], `
            let chartAnimate = animate;
            const wall = { isActive: () => active };
        `, '{ adaptChartSpec }', { active, animate });
        const spec = { data: {}, options: { scales: { x: {} } }, drillTo: () => '/x' };

        const normal = make(false, true).adaptChartSpec(spec);
        assert.equal(normal.drillTo, spec.drillTo, '一般模式保留下鑽');
        assert.equal(normal.options, spec.options, '一般模式不動 options');

        const wallFirst = make(true, true).adaptChartSpec(spec);
        assert.equal('drillTo' in wallFirst, false, '大螢幕模式不掛下鑽');
        assert.equal(wallFirst.options.animation, undefined, '首次載入維持現行動畫');

        const quiet = make(true, false).adaptChartSpec(spec);
        assert.equal(quiet.options.animation, false, '更新時關閉動畫');
        assert.deepEqual(quiet.options.scales, spec.options.scales, '其他選項保留');
        assert.equal(make(true, false).adaptChartSpec({ data: {} }).options.animation, false, '沒有 options 也能關動畫');
        assert(spec.drillTo && spec.options.animation === undefined, '不得改動原本的 spec');
    },

    // ── charts.js 字級倍率 ────────────────────────────────────────────────
    charts倍率預設1時座標字級仍是11且不加根字級() {
        const { api, created } = loadCharts();
        api.bar({ parentElement: null }, { data: { labels: [], datasets: [] } });
        const options = created.at(-1).options;
        assert.equal(options.scales.x.ticks.font.size, 11);
        assert.equal(options.scales.y.ticks.font.size, 11);
        assert.equal(options.plugins.legend.labels.font.size, 12);
        assert.equal(options.plugins.tooltip.bodyFont.size, 12);
        assert.equal('font' in options, false, '倍率 1 不加任何新設定');

        // 頁面自訂座標軸（整組取代共用設定）時同樣不補刻度字級
        api.bar({ parentElement: null }, { data: {}, options: { scales: { x: { stacked: true } } } });
        assert.equal(created.at(-1).options.scales.x.ticks, undefined, '倍率 1 不動頁面自訂的座標軸');
    },
    charts倍率大於1時座標與圖例與提示字級放大_設回1即還原() {
        const { api, created } = loadCharts();
        api.setFontScale(2);
        api.bar({ parentElement: null }, { data: {} });
        let options = created.at(-1).options;
        assert.equal(options.scales.x.ticks.font.size, 22);
        assert.equal(options.scales.y.ticks.font.size, 22);
        assert.equal(options.plugins.legend.labels.font.size, 24);
        assert.equal(options.plugins.tooltip.titleFont.size, 26);
        assert.equal('font' in options, false, '不靠根層 font（Chart.js 的座標軸不吃它）');

        // 頁面自訂座標軸整組取代共用設定：補上放大後的刻度字級；頁面自己指定字級的軸不動
        api.bar({ parentElement: null }, { data: {}, options: { scales: { x: { stacked: true }, y: { ticks: { font: { size: 30 } } } } } });
        assert.equal(created.at(-1).options.scales.x.ticks.font.size, 22, '被頁面取代的座標軸也放大');
        assert.equal(created.at(-1).options.scales.x.stacked, true, '頁面原有設定保留');
        assert.equal(created.at(-1).options.scales.y.ticks.font.size, 30, '頁面自己指定的刻度字級優先');

        api.setFontScale(1);
        api.bar({ parentElement: null }, { data: {} });
        options = created.at(-1).options;
        assert.equal(options.scales.x.ticks.font.size, 11);
        assert.equal('font' in options, false);

        api.setFontScale(NaN);
        api.setFontScale(-3);
        api.bar({ parentElement: null }, { data: {} });
        assert.equal(created.at(-1).options.scales.x.ticks.font.size, 11, '不合理的倍率一律視為 1');
    }
};

/** 載入 production 的 core/charts.js：匯入換成替身，Chart 換成記錄設定的假類別 */
function loadCharts() {
    const source = read('core', 'charts.js');
    const body = source.replace(/^import .*$/gm, '').replace(/^export /gm, '');
    const created = [];
    class FakeChart { constructor(_canvas, config) { created.push(config); } }
    const api = new Function('Chart', 'appUrl', 'formatNumber', 'cssVar', 'document',
        `${body}\nreturn { bar, setFontScale };`)(FakeChart, x => x, n => n, (_name, fallback) => fallback, document);
    return { api, created };
}

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
