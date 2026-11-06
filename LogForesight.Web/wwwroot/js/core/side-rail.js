/**
 * 右側面板側軌（第 66 輪 D2）：風險日詳情的「處理狀態」「處理歷程」預設縮成右側直式側軌，
 * 點了才展開，讓左欄閱讀重點問題時有更大的寬度。
 *
 * 模組只管「展開／收合／焦點／摘要」，不知道 hash 與勾選——什麼時候該自動展開由頁面層決定
 * （見 record-detail.js）。狀態只以 layout 上的 class 表示、不記 localStorage：
 * 每次進頁面都從收合開始，避免「上次展開、這次忘了」造成左欄寬度忽大忽小。
 * 視窗 < lg 時 CSS 讓兩欄上下堆疊、側軌不顯示、右欄內容一律展開（見 site.css）。
 */

const OPEN_CLASS = 'lf-detail-layout--side-open';

/** 側軌模式的斷點（>= lg）。與 site.css 的 `@media (min-width: 992px)` 側軌規則是同一個值，兩處要一起改。 */
const RAIL_MEDIA_QUERY = '(min-width: 992px)';

/** 目前視窗是否在側軌模式（寬螢幕）；< lg 時右欄堆疊在下、內容一律展開，側軌不顯示。 */
function isRailMode() {
    try { return !!window.matchMedia?.(RAIL_MEDIA_QUERY).matches; } catch { return false; }
}

/**
 * @param {{ layout: HTMLElement, side: HTMLElement, items: {key: string, label: string, target: HTMLElement}[] }} options
 */
export function createSideRail({ layout, side, items }) {
    if (!side.id) side.id = 'lf-detail-side';
    const body = side.querySelector('.lf-detail-side__body') || side;

    let open = false;
    let userChoice = false;
    let lastTrigger = null;

    const nav = document.createElement('nav');
    nav.className = 'lf-side-rail lf-no-print';
    nav.setAttribute('aria-label', '右側面板');

    const buttons = new Map();
    for (const item of items) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'lf-side-rail__btn';
        button.setAttribute('aria-controls', side.id);
        button.setAttribute('aria-expanded', 'false');
        const label = document.createElement('span');
        label.className = 'lf-side-rail__label';
        label.textContent = item.label;
        const summary = document.createElement('span');
        summary.className = 'lf-side-rail__summary';
        button.append(label, summary);
        button.addEventListener('click', () => {
            userChoice = true;
            expand(item.key, { focus: true, scroll: true });
        });
        nav.appendChild(button);
        buttons.set(item.key, { button, summary, item });
    }

    const collapseButton = document.createElement('button');
    collapseButton.type = 'button';
    collapseButton.className = 'btn btn-sm btn-outline-secondary lf-side-rail__collapse lf-no-print';
    collapseButton.textContent = '收合 ›';
    collapseButton.setAttribute('aria-controls', side.id);
    collapseButton.setAttribute('aria-expanded', 'false');
    collapseButton.addEventListener('click', () => {
        userChoice = true;
        collapse();
    });

    side.insertAdjacentElement('afterbegin', nav);
    body.insertAdjacentElement('afterbegin', collapseButton);

    function setOpen(value) {
        open = value;
        if (value) layout.classList.add(OPEN_CLASS);
        else layout.classList.remove(OPEN_CLASS);
        for (const { button } of buttons.values()) button.setAttribute('aria-expanded', String(value));
        collapseButton.setAttribute('aria-expanded', String(value));
    }

    function prefersReducedMotion() {
        try { return !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches; } catch { return false; }
    }

    function expand(key, { focus = true, scroll = true } = {}) {
        const entry = buttons.get(key);
        if (!entry) return;
        lastTrigger = entry.button;
        setOpen(true);
        const target = entry.item.target;
        if (scroll) {
            target.scrollIntoView?.({ behavior: prefersReducedMotion() ? 'auto' : 'smooth', block: 'nearest' });
        }
        if (focus) {
            const heading = target.querySelector('.lf-card__title');
            if (heading) {
                heading.setAttribute('tabindex', '-1');
                heading.focus?.({ preventScroll: true });
            }
        }
    }

    function collapse() {
        setOpen(false);
        const back = lastTrigger || buttons.values().next().value?.button;
        back?.focus?.();
    }

    function setSummary(key, text) {
        const entry = buttons.get(key);
        if (!entry) return;
        entry.summary.textContent = text ?? '';
        // 直式側軌空間窄、摘要可能被截斷，完整文字放 title 供滑鼠停留與輔助科技讀取
        entry.button.setAttribute('title', entry.item.label + (text ? `：${text}` : ''));
    }

    return {
        expand,
        collapse,
        isExpanded: () => open,
        setSummary,
        hasUserChoice: () => userChoice,
        isRailMode
    };
}
