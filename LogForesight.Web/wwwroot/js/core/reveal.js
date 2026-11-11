/**
 * 「只顯示第一排，其餘點一下展開」（docs/DESIGN-SYSTEM.md §6b）。
 *
 * 一排放幾張要看實際版面：KPI 各組的寬度由 flex 依卡數分配，CSS 斷點算不出欄數，
 * 所以量格線的軌道數。`auto-fit` 收合的空軌會以 0px 列出，同樣計入——不計的話藏卡後
 * 欄數跟著變少，會越藏越多。量到 0 欄代表還沒佈局（容器未插入或被藏起），一律不收合。
 *
 * 用 hidden 屬性而不是固定高度＋overflow：被藏的卡仍在 DOM、無障礙樹與畫面一致。
 * `.lf-kpi-card{display:block}` 會蓋過瀏覽器預設的 [hidden]，實際靠 Bootstrap 的
 * `[hidden]{display:none!important}` 生效；列印時由樣式表還原成全部顯示。
 *
 * 記憶鍵與 bindRememberedCollapse 同一個前綴與 open／closed 值，讀寫各一行不另抽共用函式。
 */
import { safeStorage } from './storage.js';

/** layout.js 切換字級時派發：rem 變了但格線寬度沒變，ResizeObserver 不會觸發 */
export const FONT_SCALE_EVENT = 'lf:font-scale';

/** 量格線目前的軌道數（含 auto-fit 收合的 0px 軌）；尚未佈局回 0 */
export function countGridColumns(grid) {
    const tracks = getComputedStyle(grid).gridTemplateColumns;
    if (!tracks || tracks === 'none') return 0;
    return tracks.trim().split(/\s+/).length;
}

/**
 * @param {object} options
 * @param {Array} options.grids 共用同一顆揭露鈕的格線（KPI 三組、風險類型一組）
 * @param {Element} options.host 揭露鈕插入的容器（放在格線之後）
 * @param {string} options.key 展開狀態的記憶鍵（lf.collapse.{key}）
 * @param {string} options.noun 按鈕文字的量詞與名詞，例如「項指標」「類」
 * @param {(child: Element) => boolean} [options.isAlert] 判斷被藏的卡是否帶告警
 * @param {(expanded: boolean) => void} [options.onCollapsedChange] 初始與每次切換展開／收合時呼叫；
 *        只傳展開狀態、不傳量測結果，呼叫端據此切換版面類別（本函式不認得 KPI 群組）
 * @returns {{ dispose: () => void }} 重新渲染前要呼叫，否則舊格線上的 observer 會累積
 */
export function revealFirstRow({
    grids,
    host,
    key,
    noun,
    isAlert = () => false,
    onCollapsedChange = null,
    measure = countGridColumns,
    // 延到下一格畫面：callback 內改 hidden 會讓被觀察的格線變高，同一輪內就會觸發瀏覽器的 ResizeObserver 迴圈錯誤
    createObserver = callback => new ResizeObserver(() => requestAnimationFrame(callback)),
    storage = safeStorage,
    eventTarget = window
}) {
    // 沒有格線（例如 KPI 全無卡片）就沒有可收合的東西，不建按鈕也不掛 observer
    if (!grids || grids.length === 0) return { dispose() {} };

    const storageKey = `lf.collapse.${key}`;
    let expanded = storage.get(storageKey) === 'open';
    let lastColumns = null;
    let disposed = false;

    const notifyState = () => { if (onCollapsedChange) onCollapsedChange(expanded); };
    notifyState();

    const bar = document.createElement('div');
    bar.className = 'lf-reveal-bar';
    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-sm btn-outline-secondary lf-reveal-toggle';
    grids.forEach((grid, index) => { if (!grid.id) grid.id = `lf-reveal-${key}-${index}`; });
    toggle.setAttribute('aria-controls', grids.map(grid => grid.id).join(' '));
    bar.appendChild(toggle);
    host.appendChild(bar);

    const render = () => {
        const overflow = new Set();
        for (const grid of grids) {
            const columns = measure(grid);
            if (columns > 0) [...grid.children].slice(columns).forEach(child => overflow.add(child));
        }
        for (const grid of grids) {
            for (const child of grid.children) child.hidden = !expanded && overflow.has(child);
        }

        bar.hidden = overflow.size === 0;
        toggle.setAttribute('aria-expanded', String(expanded));
        if (expanded) {
            toggle.textContent = '收合';
        } else {
            const alerts = [...overflow].filter(isAlert).length;
            toggle.textContent = `顯示其餘 ${overflow.size} ${noun}${alerts > 0 ? `（${alerts} 項告警）` : ''}`;
        }
    };

    const refresh = () => {
        if (disposed) return;
        const columns = grids.map(measure).join(',');
        if (columns === lastColumns) return;
        lastColumns = columns;
        render();
    };

    toggle.addEventListener('click', () => {
        expanded = !expanded;
        storage.set(storageKey, expanded ? 'open' : 'closed');
        notifyState();
        render();
    });

    const observer = createObserver(refresh);
    grids.forEach(grid => observer.observe(grid));
    eventTarget.addEventListener(FONT_SCALE_EVENT, refresh);
    refresh();

    return {
        dispose() {
            disposed = true;
            observer.disconnect();
            eventTarget.removeEventListener(FONT_SCALE_EVENT, refresh);
            bar.remove();
        }
    };
}
