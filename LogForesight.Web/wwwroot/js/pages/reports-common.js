/**
 * 報表頁三個視角共用的小工具（docs/WEB-SPEC.md §9.6）：視角名稱與網址參數、分桶標籤、
 * 各視角自己的載入序號守門、列印前補載入、大螢幕進出時的視角保存。
 *
 * 全部是純函式或只依賴注入的回呼——不碰頁面元素，行為測試直接匯入這支檔案即可。
 * reports.js（總覽）、reports-effectiveness.js、reports-category.js 都從這裡取共用邏輯，不各寫一份。
 */

import { createSeqGuard } from '../core/async-guards.js';

export const VIEWS = ['overview', 'effectiveness', 'category'];
export const DEFAULT_VIEW = 'overview';
export const VIEW_PARAM = 'view';

/** 無法辨識的值一律當總覽（網址被手改、舊連結） */
export function normalizeView(value) {
    return VIEWS.includes(value) ? value : DEFAULT_VIEW;
}

export function viewFromSearch(search) {
    return normalizeView(new URLSearchParams(search).get(VIEW_PARAM));
}

/** 回傳寫入 view 後的查詢字串（含開頭 ?；沒有任何參數時回空字串）。總覽不留參數，網址保持乾淨 */
export function searchWithView(search, view) {
    const params = new URLSearchParams(search);
    if (normalizeView(view) === DEFAULT_VIEW) params.delete(VIEW_PARAM); else params.set(VIEW_PARAM, view);
    const query = params.toString();
    return query ? `?${query}` : '';
}

/** 起訖日期檢查（起日晚於迄日、含首尾超過 366 天）：沒問題回 null，有問題回要給使用者看的訊息；與後端同口徑 */
export function rangeProblem(from, to) {
    if (!from || !to) return null;
    if (from > to) return '起日不可晚於迄日，請調整日期範圍';
    const days = Math.round((new Date(to) - new Date(from)) / 86400000) + 1;
    return days > 366 ? '查詢區間不可超過 366 天，請縮小範圍' : null;
}

// ── 分桶標籤（後端依期間長度分日／週／月桶，見 ReportBucketing）────────────────

const mmdd = date => date.slice(5).replace('-', '/');

/** 桶的種類：每桶都是 1 天＝日桶；最長不超過 7 天＝週桶；其餘＝月桶（頭尾不足整週整月的桶不影響判斷，因為期間夠長一定有完整的桶） */
export function bucketMode(buckets) {
    const longest = Math.max(0, ...buckets.map(b => b.days ?? 1));
    if (longest <= 1) return 'day';
    return longest <= 7 ? 'week' : 'month';
}

/** 日桶 MM/DD、週桶 MM/DD–MM/DD、月桶 YYYY/MM；週桶只剩一天時寫成單日 */
export function bucketLabel(bucket, mode) {
    if (mode === 'month') return bucket.from.slice(0, 7).replace('-', '/');
    if (mode === 'day' || bucket.from === bucket.to) return mmdd(bucket.from);
    return `${mmdd(bucket.from)}–${mmdd(bucket.to)}`;
}

// ── 熱度色階 ──────────────────────────────────────────────────────────────────

export const HEAT_LEVELS = 5;

/** 0 不上色（回 0）；有值依占全表最大值的比例分 1～HEAT_LEVELS 級，最大值一定落在最高級 */
export function heatLevel(value, max) {
    if (!(value > 0) || !(max > 0)) return 0;
    return Math.min(HEAT_LEVELS, Math.max(1, Math.ceil((value / max) * HEAT_LEVELS)));
}

// ── 各視角自己的載入狀態與序號守門 ────────────────────────────────────────────────

/** 每個延後載入的視角一份：序號守門（快速連切只畫最後一次）、上次成功載入的資料、有沒有載入失敗 */
export function createViewState() {
    return { guard: createSeqGuard(), data: null, loaded: false, failed: false, dirty: true };
}

/**
 * 取一次視角資料。回應回來時已有更新的請求，就回 { stale: true }、不得寫入任何狀態；
 * 失敗時舊請求的失敗也不外擲（失敗態只屬於最新一次請求）。成功回 { data }。
 * 待重載標記在發出請求時就清掉：請求在途時期間又變了（其他視角按了套用），重新標上的待重載不會被這次的舊期間回應抹掉。
 */
export async function loadViewData(state, fetcher) {
    const seq = state.guard.next();
    state.dirty = false;
    let data;
    try {
        data = await fetcher();
    } catch (error) {
        if (!state.guard.isCurrent(seq)) return { stale: true };
        state.failed = true;
        throw error;
    }
    if (!state.guard.isCurrent(seq)) return { stale: true };
    state.data = data;
    state.loaded = true;
    state.failed = false;
    return { data };
}

// ── 列印 ─────────────────────────────────────────────────────────────────────

/**
 * 列印／存成 PDF：三個視角依序全部印出。尚未載入（或條件已變動待重載）的視角先補載入，
 * 等回應或失敗之後才呼叫 print()；載入失敗的視角由 onResult(view, false) 通知頁面標成「此部分未能載入」。
 * @param {object} deps
 * @param {string[]} deps.views
 * @param {(view: string) => boolean} deps.needsLoad
 * @param {(view: string) => Promise<unknown>} deps.load        載入並渲染該視角；失敗擲例外
 * @param {(view: string, ok: boolean) => void} deps.onResult
 * @param {() => (void | Promise<void>)} [deps.beforePrint]     補載入完成、print() 之前（頁面在這裡讓三個視角都顯示，可等圖表重排）
 * @param {() => void} deps.print
 */
export async function printAllViews({ views, needsLoad, load, onResult, beforePrint, print }) {
    const pending = views.filter(needsLoad);
    await Promise.all(pending.map(async view => {
        try {
            await load(view);
            onResult(view, true);
        } catch {
            onResult(view, false);
        }
    }));
    await beforePrint?.();
    print();
}

// ── 大螢幕 ───────────────────────────────────────────────────────────────────

/**
 * 大螢幕只顯示總覽：進入時若在其他視角就先切回總覽（記下原視角），離開時回到原視角。
 * 切換本身（顯示哪個面板、是否改網址、是否載入）由頁面注入：show 只換面板不改網址，restore 是一般的切換視角。
 */
export function createWallViewKeeper({ getView, show, restore }) {
    let saved = null;
    return {
        enter() {
            const view = getView();
            if (view === DEFAULT_VIEW) return;
            saved = view;
            show(DEFAULT_VIEW);
        },
        leave() {
            if (saved === null) return;
            const view = saved;
            saved = null;
            restore(view);
        },
        saved: () => saved
    };
}
