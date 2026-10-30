/**
 * 非同步守門工具（純函式，無 DOM 相依）。
 *
 * createSeqGuard：請求序號守門。每次發出查詢前 next() 取得新序號，回應回來時
 * 以 isCurrent(seq) 判斷它是否仍是最新一次；舊回應就丟掉，不得蓋掉新結果。
 *
 * pollWhile：狀態輪詢。fn 跑完才排下一輪（不會疊加在途請求）；fn 擲例外時吞掉、
 * 只 console.warn 一次並照常續排；fn 回傳 false 或呼叫 stop() 即停止。
 * 本工具不做背景分頁降頻與失敗退避。
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
 * @param {() => (unknown | Promise<unknown>)} fn 每輪執行的工作；回傳 false 代表停止輪詢
 * @param {number} intervalMs 上一輪結束後到下一輪開始的間隔（毫秒）
 * @returns {{ stop(): void }}
 */
export function pollWhile(fn, intervalMs) {
    let stopped = false;
    let timer = null;
    let warned = false;

    const schedule = () => {
        if (stopped) return;
        timer = setTimeout(tick, intervalMs);
    };

    const tick = async () => {
        timer = null;
        if (stopped) return;
        let keepGoing = true;
        try {
            keepGoing = (await fn()) !== false;
        } catch (err) {
            if (!warned) {
                warned = true;
                console.warn('[pollWhile] 輪詢工作擲出例外，之後仍會續排', err);
            }
        }
        if (keepGoing) schedule();
    };

    // 首輪同樣等 intervalMs 後才執行（與 setInterval 的既有節奏一致），
    // 讓呼叫端在 pollWhile 回傳前就能拿到 handle 給 fn 使用。
    schedule();

    return {
        stop() {
            stopped = true;
            if (timer !== null) clearTimeout(timer);
            timer = null;
        },
    };
}
