/**
 * 非同步守門工具（純函式，無 DOM 相依）。
 *
 * createSeqGuard：請求序號守門。每次發出查詢前 next() 取得新序號，回應回來時
 * 以 isCurrent(seq) 判斷它是否仍是最新一次；舊回應就丟掉，不得蓋掉新結果。
 *
 * pollWhile：狀態輪詢。fn 跑完才排下一輪（不會疊加在途請求）；fn 擲例外時吞掉、
 * 只 console.warn 一次並照常續排；fn 回傳 false 或呼叫 stop() 即停止。
 * 選項：間隔可為函式（依上一輪狀態重算）、連續失敗退避（backoffMaxMs）、
 * 背景分頁暫停並在回前景時立即補一次（pauseWhenHidden）；皆預設關閉。
 */

/**
 * @returns {{ next(): number, isCurrent(seq: number): boolean }}
 */
export function createSeqGuard() {
    let latest = 0;
    return {
        next() {
            latest += 1;
            return latest;
        },
        isCurrent(seq) {
            return seq === latest;
        },
    };
}

/**
 * @typedef {object} PollContext
 * @property {number} failures 連續失敗次數（fn 擲例外加一，成功歸零）
 * @property {unknown} lastResult 上一輪 fn 的回傳值（首輪為 undefined）
 *
 * @typedef {object} PollOptions
 * @property {boolean} [pauseWhenHidden] 背景分頁（document.hidden）時不執行也不排程；
 *           回到前景立即執行一次，之後照常續排。預設 false（維持原行為）
 * @property {number} [backoffMaxMs] 設了就在連續失敗時退避：間隔 ×2 的 N 次方，上限為此值；
 *           成功一次即回到基本間隔。未設則失敗不改間隔
 */

/**
 * @param {() => (unknown | Promise<unknown>)} fn 每輪執行的工作；回傳 false 代表停止輪詢
 * @param {number | ((ctx: PollContext) => number)} intervalMs 上一輪結束後到下一輪開始的間隔（毫秒）；
 *        給函式時每次排程前依上一輪狀態重算（例如執行中 30 秒、閒置 60 秒）
 * @param {PollOptions} [options]
 * @returns {{ stop(): void, readonly failures: number }}
 */
export function pollWhile(fn, intervalMs, { pauseWhenHidden = false, backoffMaxMs } = {}) {
    let stopped = false;
    let timer = null;
    let warned = false;
    let running = false;
    let paused = false;      // 因背景分頁而停排，等回到前景
    let wasHidden = false;   // 排程期間曾進過背景：回前景要立刻補一次
    let failures = 0;
    let lastResult;

    const hasDocument = typeof document !== 'undefined';
    const isHidden = () => pauseWhenHidden && hasDocument && document.hidden === true;

    const delay = () => {
        const base = typeof intervalMs === 'function' ? intervalMs({ failures, lastResult }) : intervalMs;
        if (backoffMaxMs == null || failures === 0) return base;
        return Math.min(base * 2 ** Math.min(failures, 16), Math.max(backoffMaxMs, base));
    };

    const schedule = () => {
        if (stopped) return;
        if (isHidden()) { paused = true; wasHidden = true; return; }
        timer = setTimeout(tick, delay());
    };

    const tick = async () => {
        timer = null;
        if (stopped || running) return;
        if (isHidden()) { paused = true; wasHidden = true; return; }
        running = true;
        let keepGoing = true;
        try {
            lastResult = await fn();
            keepGoing = lastResult !== false;
            failures = 0;
        } catch (err) {
            failures += 1;
            lastResult = undefined;
            if (!warned) {
                warned = true;
                console.warn('[pollWhile] 輪詢工作擲出例外，之後仍會續排', err);
            }
        } finally {
            running = false;
        }
        if (keepGoing) schedule();
    };

    const onVisibility = () => {
        if (stopped || !hasDocument) return;
        if (document.hidden) { wasHidden = true; return; }
        // 回到前景：只在先前真的進過背景時補一次；fn 在途就等它自己排下一輪
        if (!wasHidden && !paused) return;
        wasHidden = false;
        paused = false;
        if (running) return;
        if (timer !== null) clearTimeout(timer);
        tick();
    };
    if (pauseWhenHidden && hasDocument) document.addEventListener('visibilitychange', onVisibility);

    // 首輪同樣等 intervalMs 後才執行（與 setInterval 的既有節奏一致），
    // 讓呼叫端在 pollWhile 回傳前就能拿到 handle 給 fn 使用。
    schedule();

    return {
        stop() {
            stopped = true;
            if (timer !== null) clearTimeout(timer);
            timer = null;
            if (pauseWhenHidden && hasDocument) document.removeEventListener('visibilitychange', onVisibility);
        },
        get failures() { return failures; },
    };
}

/**
 * 進行中保護：包住一個會 await 之後才開對話框的入口。上一次呼叫還沒結束時，
 * 重複觸發直接忽略（回傳 undefined），不會再開第二個對話框（Bootstrap 5.3 不支援疊開）。
 * 不論成功或擲例外，結束後都解除。
 *
 * @template {(...args: any[]) => Promise<any>} F
 * @param {F} fn
 * @returns {F}
 */
export function exclusive(fn) {
    let running = false;
    return /** @type {F} */ (async function (...args) {
        if (running) return undefined;
        running = true;
        try {
            return await fn.apply(this, args);
        } finally {
            running = false;
        }
    });
}
