/**
 * 品牌副標題的對齊（docs/WEB-SPEC.md §9.9b 2d）。
 *
 * 規則：副標題自然寬度**小於**主標題 → 撐開字距（letter-spacing）讓它與主標題左右邊緣貼齊；
 * **大於等於** → 字距歸零、靠左對齊主標題左緣（超長時由 CSS 的省略號處理）。
 *
 * 為什麼要用 JS：CSS 的 `text-align: justify` 對單行文字不生效（最後一行不參與兩端對齊），
 * 而品牌名稱與副標題都是管理者可改的設定值，字數不固定，沒辦法預先寫死字距。
 *
 * letter-spacing 會加在**最後一個字之後**，直接用差值除以字數會多出一格尾距、
 * 整行看起來右邊少貼一點——所以除以「字數 - 1」，並用同寬的負 margin 把尾距收掉。
 */

/** 每組品牌區塊算一次；找不到主標題或副標題就跳過 */
export function alignBrandSubtitles(root = document) {
    for (const block of root.querySelectorAll('.lf-brand')) {
        alignOne(block);
    }
}

function alignOne(block) {
    const name = block.querySelector('.lf-brand__name');
    const subtitle = block.querySelector('.lf-brand__subtitle');
    if (!name || !subtitle) return;

    // 先歸零再量：上一次撐開的字距會讓這次量到的「自然寬度」偏大，
    // 視窗連續縮放時會一路把字距越撐越開
    subtitle.style.letterSpacing = '';
    subtitle.style.marginRight = '';

    const nameWidth = name.getBoundingClientRect().width;
    const naturalWidth = measureNaturalWidth(subtitle);
    if (nameWidth <= 0 || naturalWidth <= 0) return;

    // 以字元數（非 code unit）為準：中文副標題常見 4～6 字，一個 emoji 或代理對
    // 用 length 會多算一格，字距就會算窄
    const charCount = [...subtitle.textContent.trim()].length;
    if (naturalWidth >= nameWidth || charCount < 2) {
        // 比主標題寬（或只有一個字，撐不出字距）：靠左，維持 CSS 的省略號行為
        return;
    }

    const spacing = (nameWidth - naturalWidth) / (charCount - 1);
    subtitle.style.letterSpacing = `${spacing.toFixed(3)}px`;
    subtitle.style.marginRight = `${(-spacing).toFixed(3)}px`;
}

/**
 * 量測副標題不受省略號影響的自然寬度。
 *
 * 元素本身是 `overflow:hidden` 的塊狀元素，`getBoundingClientRect()` 量到的是被容器
 * 限制後的寬度，不是文字真正需要的寬度——用同字型的離屏節點量原始文字。
 */
function measureNaturalWidth(subtitle) {
    const probe = document.createElement('span');
    const style = getComputedStyle(subtitle);
    probe.textContent = subtitle.textContent;
    probe.style.position = 'absolute';
    probe.style.visibility = 'hidden';
    probe.style.whiteSpace = 'pre';
    probe.style.font = style.font;
    probe.style.letterSpacing = '0';
    subtitle.parentElement.appendChild(probe);
    const width = probe.getBoundingClientRect().width;
    probe.remove();
    return width;
}

/**
 * 綁定重算時機：字型載入完成（載入前是 fallback 字型，寬度不同）與視窗縮放。
 * 側欄寬度是 rem，使用者調整字級偏好時也會走 resize。
 */
export function initBrandAlign() {
    alignBrandSubtitles();

    document.fonts?.ready.then(() => alignBrandSubtitles());

    let pending = 0;
    window.addEventListener('resize', () => {
        cancelAnimationFrame(pending);
        pending = requestAnimationFrame(() => alignBrandSubtitles());
    });
}
