/**
 * 報表頁「大螢幕模式」（戰情中心）的狀態機與排程（docs/WEB-SPEC.md §9.6）。
 *
 * 這支模組只管「什麼時候進入、什麼時候離開、什麼時候重新取數」，不碰任何頁面元素：
 * 視窗、文件、網址、計時器與取數／重繪都由 createWall(deps) 注入，行為測試才能用可控時鐘驅動。
 * 頁面元素（標頭列、離開鈕、字級變數、圖表字級）由 reports.js 在注入的回呼裡處理。
 *
 * 大螢幕上整天開著、沒有人操作，所以：
 *   - 自動更新用 setTimeout 鏈（上一次**完成後**才排下一次），同一時間只會有一個請求；
 *   - 分頁不可見時不發請求，回到可見時視已過多久決定要不要立刻補一次；
 *   - 更新失敗保留舊資料，狀態只寫在標頭列，下一輪照常再試。
 */

/** 自動更新間隔（暫定）：上一次載入完成後這麼久才排下一次 */
export const WALL_REFRESH_MS = 15 * 60 * 1000;

/** 離開鈕在滑鼠停止移動後多久淡出（毫秒） */
export const WALL_EXIT_FADE_MS = 3000;

/** 寬度不足就不提供大螢幕模式（與樣式表 @media 的 992px 同值） */
export const WALL_MIN_WIDTH_QUERY = '(min-width: 992px)';

/** 套在 <html> 的類別；伺服器端（_Layout）在網址帶 display=wall 的報表頁直接輸出，避免進頁閃現側欄 */
export const WALL_CLASS = 'lf-wall';

const PARAM = 'display';
const VALUE = 'wall';
const BASE_HEIGHT = 1080;

/** 字級倍率：以 1080px 高為 1，隨視窗高度等比放大，下限 1（矮視窗不縮小） */
export function wallScale(innerHeight) {
    const ratio = Number(innerHeight) / BASE_HEIGHT;
    return Number.isFinite(ratio) ? Math.max(1, ratio) : 1;
}

/** HH:mm（本地時間、24 小時制） */
export function formatClock(ms) {
    const d = new Date(ms);
    const pad = n => String(n).padStart(2, '0');
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * 標頭列的兩段狀態文字（純函式）：
 *   updated：「最後更新 HH:mm」，還沒有任何成功載入時為空；
 *   stale：最近一次更新失敗且畫面上還有舊資料時為「更新失敗，目前是 HH:mm 的資料」，否則為空。
 * 首次載入就失敗時沒有舊資料可說，由頁面的錯誤狀態負責，標頭不重複。
 */
export function wallStatusText({ lastOkAt, failed }) {
    if (lastOkAt == null) return { updated: '', stale: '' };
    const clock = formatClock(lastOkAt);
    return {
        updated: `最後更新 ${clock}`,
        stale: failed ? `更新失敗，目前是 ${clock} 的資料` : ''
    };
}

/**
 * @param {object} deps
 * @param {Window}   deps.win        innerHeight、matchMedia、addEventListener
 * @param {Document} deps.doc        documentElement、visibilityState、fullscreenElement、事件
 * @param {Location} deps.loc        search、pathname
 * @param {History}  deps.hist       replaceState
 * @param {() => number} deps.now    目前時間（毫秒）
 * @param {(fn: Function, ms: number) => any} deps.setTimer
 * @param {(handle: any) => void} deps.clearTimer
 * @param {() => Promise<boolean>} deps.refresh  取數並重繪一次；成功回 true，失敗回 false 或擲例外
 * @param {() => void} deps.rerender             依目前模式重繪（下鑽、圖表字級）
 * @param {(scale: number | null) => void} deps.applyScale  套用字級倍率；null 表示還原
 * @param {(visible: boolean) => void} deps.setExitVisible  離開鈕顯示／淡出
 * @param {() => void} deps.onState              狀態變動（標頭列、按鈕顯示）
 */
export function createWall(deps) {
    const { win, doc, loc, hist } = deps;
    const root = doc.documentElement;
    const mq = win.matchMedia(WALL_MIN_WIDTH_QUERY);

    let requested = false;   // 使用者要的模式（網址或按鈕）；寬度不足時仍為 true，但不執行
    let running = false;     // requested 且寬度足夠：自動更新與大螢幕行為生效中
    let busy = false;        // 有一個載入在途（首次載入或自動更新）
    let timer = null;
    let exitTimer = null;
    let lastDoneAt = null;   // 上一次載入完成（成功或失敗）的時間
    let lastOkAt = null;     // 上一次成功載入的時間；標頭列顯示它
    let failed = false;
    let loads = 0;           // 成功載入累計次數，供頁面判斷一次載入有沒有成功
    let appliedScale = null;
    let fullscreenByButton = false;

    const changed = () => deps.onState?.();

    function setUrl(on) {
        const params = new URLSearchParams(loc.search);
        if (on) params.set(PARAM, VALUE); else params.delete(PARAM);
        const query = params.toString();
        hist.replaceState(null, '', query ? `?${query}` : loc.pathname);
    }

    function clearTimers() {
        if (timer !== null) deps.clearTimer(timer);
        timer = null;
        if (exitTimer !== null) deps.clearTimer(exitTimer);
        exitTimer = null;
    }

    /** 依上一次完成的時間排下一次；每次完成後重新計算，不是固定間隔 */
    function schedule() {
        if (timer !== null) deps.clearTimer(timer);
        timer = null;
        if (!running || busy || lastDoneAt === null) return;
        const wait = Math.max(0, WALL_REFRESH_MS - (deps.now() - lastDoneAt));
        timer = deps.setTimer(tick, wait);
    }

    function tick() {
        timer = null;
        if (!running) return;
        // 分頁不可見：不發請求也不續排，回到可見時由 onVisibility 接手
        if (doc.visibilityState === 'hidden') return;
        run();
    }

    async function run() {
        if (busy || !running) return;
        busy = true;
        changed();
        let ok = false;
        try {
            ok = (await deps.refresh()) === true;
        } catch {
            ok = false;
        }
        busy = false;
        if (ok) dataLoaded(); else { lastDoneAt = deps.now(); failed = true; }
        if (running) schedule();
        changed();
    }

    function onVisibility() {
        if (!running || busy || doc.visibilityState === 'hidden' || lastDoneAt === null) return;
        if (deps.now() - lastDoneAt >= WALL_REFRESH_MS) {
            if (timer !== null) deps.clearTimer(timer);
            timer = null;
            run();
        } else if (timer === null) {
            schedule();   // 隱藏期間計時器已觸發過：照原排程補排剩下的時間
        }
    }

    function applyScale(force) {
        const scale = wallScale(win.innerHeight);
        if (!force && scale === appliedScale) return false;
        appliedScale = scale;
        deps.applyScale(scale);
        return true;
    }

    function begin() {
        running = true;
        root.classList.add(WALL_CLASS);
        applyScale(true);
        schedule();
        deps.rerender();
    }

    function halt() {
        running = false;
        clearTimers();
        appliedScale = null;
        deps.applyScale(null);
        deps.setExitVisible(false);
        deps.rerender();
    }

    /** 對齊「要的模式」與「寬度是否足夠」 */
    function sync() {
        const should = requested && mq.matches;
        if (should && !running) begin();
        else if (!should && running) halt();
        changed();
    }

    /** 離開：還原網址、類別、字級，停止自動更新，以一般模式重繪 */
    function exit() {
        if (!requested && !running) return;
        requested = false;
        fullscreenByButton = false;
        setUrl(false);
        root.classList.remove(WALL_CLASS);
        if (doc.fullscreenElement && doc.exitFullscreen) {
            try { Promise.resolve(doc.exitFullscreen()).catch(() => { }); } catch { /* 不支援或已離開，略過 */ }
        }
        if (running) halt();
        changed();
    }

    /** 工具列「大螢幕」鈕：套用模式並嘗試瀏覽器全螢幕（被拒絕或不支援時只略過） */
    async function enter() {
        if (!mq.matches) return false;
        requested = true;
        setUrl(true);
        sync();
        try {
            if (typeof root.requestFullscreen === 'function') {
                await root.requestFullscreen();
                if (!requested) {
                    // 等待期間已離開模式：把剛開起來的全螢幕收回去
                    if (doc.fullscreenElement && doc.exitFullscreen) await doc.exitFullscreen();
                } else {
                    fullscreenByButton = Boolean(doc.fullscreenElement);
                }
            }
        } catch {
            fullscreenByButton = false;
        }
        return true;
    }

    function onFullscreenChange() {
        // 只有「由按鈕進入且開了全螢幕」才跟著全螢幕退出而離開；網址直接進入的不受影響
        if (fullscreenByButton && !doc.fullscreenElement) exit();
    }

    function onKeyDown(event) {
        if (running && event.key === 'Escape') exit();
    }

    function onPointerMove() {
        if (!running) return;
        deps.setExitVisible(true);
        if (exitTimer !== null) deps.clearTimer(exitTimer);
        exitTimer = deps.setTimer(() => { exitTimer = null; deps.setExitVisible(false); }, WALL_EXIT_FADE_MS);
    }

    function onResize() {
        // 視窗高度變了才重算倍率與重繪圖表字級；寬度穿越 992 由 matchMedia 的 change 處理
        if (running && applyScale(false)) deps.rerender();
    }

    /** 頁面載入時呼叫一次：讀網址決定是否進入，並掛上全域事件 */
    function init() {
        requested = new URLSearchParams(loc.search).get(PARAM) === VALUE;
        // 伺服器端已為這種網址輸出類別；這裡補一次是保險（冪等）
        if (requested) root.classList.add(WALL_CLASS);
        mq.addEventListener('change', sync);
        win.addEventListener('resize', onResize);
        doc.addEventListener('visibilitychange', onVisibility);
        doc.addEventListener('fullscreenchange', onFullscreenChange);
        doc.addEventListener('keydown', onKeyDown);
        doc.addEventListener('mousemove', onPointerMove);
        sync();
    }

    /** 一次載入成功（任何路徑）：記下時間、清掉失敗狀態 */
    function dataLoaded() {
        lastOkAt = lastDoneAt = deps.now();
        failed = false;
        loads += 1;
    }

    return {
        init,
        enter,
        exit,
        /** 首次載入開始：載入完成前不排自動更新，也不會同時再發一個 */
        loadStarted() { busy = true; },
        /** 首次載入結束（成功或失敗）：失敗也要排下一輪重試 */
        loadSettled(ok) {
            busy = false;
            if (!ok) { lastDoneAt = deps.now(); failed = true; }
            schedule();
            changed();
        },
        dataLoaded,
        /** 目前是否為大螢幕模式生效中（要求了模式且寬度足夠） */
        isActive: () => running,
        /** 工具列「大螢幕」鈕是否該顯示：只看寬度 */
        canEnter: () => mq.matches,
        status: () => ({ lastOkAt, failed, loads }),
        statusText: () => wallStatusText({ lastOkAt, failed })
    };
}
