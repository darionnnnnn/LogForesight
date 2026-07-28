/**
 * AI 產出文字的單一渲染入口（docs/SHARED-STANDARDS-PLAN.md S7）。
 *
 * AI 回覆常帶 `**粗體**`／`- 清單` 之類的 Markdown 語法，但畫面上一直是 textContent 原樣呈現
 * （星號原樣顯示）——這是刻意的安全設計：AI 產出不可信任為 HTML，過去沒有引入任何 Markdown
 * 轉換庫，因為那類庫多半支援連結/圖片/原始 HTML，等於重新打開 XSS 的門。
 *
 * 這裡只解析一個刻意收窄的安全子集：**粗體**、`行內代碼`、- / 1. 清單、# 開頭行（轉粗體行）、
 * 換行。全程用 document.createElement／createTextNode 組 DOM，絕不使用 innerHTML——
 * 其餘語法（連結、圖片、原始 HTML 標籤）一律當純文字顯示，不會被解析成可執行內容。
 */

const HEADING_LINE = /^\s*#+\s+(.*)$/;
const UNORDERED_ITEM = /^\s*[-*]\s+(.*)$/;
const ORDERED_ITEM = /^\s*\d+[.)]\s+(.*)$/;
const INLINE_TOKEN = /\*\*([^*]+)\*\*|`([^`]+)`/g;

/** 一行文字中的 **粗體** 與 `行內代碼` 拆成節點附加到 target；其餘文字原樣 createTextNode */
function appendInline(target, line) {
    INLINE_TOKEN.lastIndex = 0;
    let lastIndex = 0;
    let match;

    while ((match = INLINE_TOKEN.exec(line)) !== null) {
        if (match.index > lastIndex) {
            target.appendChild(document.createTextNode(line.slice(lastIndex, match.index)));
        }

        if (match[1] !== undefined) {
            const strong = document.createElement('strong');
            strong.textContent = match[1];
            target.appendChild(strong);
        } else {
            const code = document.createElement('code');
            code.textContent = match[2];
            target.appendChild(code);
        }

        lastIndex = INLINE_TOKEN.lastIndex;
    }

    if (lastIndex < line.length) {
        target.appendChild(document.createTextNode(line.slice(lastIndex)));
    }
}

/** 逐行分块：# 標題行／- 或 1. 清單／段落（連續一般行以 <br> 相接，貼近原本 pre-wrap 的視覺效果） */
function renderBlocks(container, text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    let i = 0;

    while (i < lines.length) {
        const line = lines[i];

        if (line.trim() === '') { i++; continue; }

        const heading = line.match(HEADING_LINE);
        if (heading) {
            const div = document.createElement('div');
            div.className = 'fw-semibold mb-1';
            appendInline(div, heading[1]);
            container.appendChild(div);
            i++;
            continue;
        }

        if (UNORDERED_ITEM.test(line)) {
            const ul = document.createElement('ul');
            ul.className = 'mb-2 ps-3';
            while (i < lines.length) {
                const m = lines[i].match(UNORDERED_ITEM);
                if (!m) break;
                const li = document.createElement('li');
                appendInline(li, m[1]);
                ul.appendChild(li);
                i++;
            }
            container.appendChild(ul);
            continue;
        }

        if (ORDERED_ITEM.test(line)) {
            const ol = document.createElement('ol');
            ol.className = 'mb-2 ps-3';
            while (i < lines.length) {
                const m = lines[i].match(ORDERED_ITEM);
                if (!m) break;
                const li = document.createElement('li');
                appendInline(li, m[1]);
                ol.appendChild(li);
                i++;
            }
            container.appendChild(ol);
            continue;
        }

        const para = document.createElement('div');
        para.className = 'mb-1';
        let first = true;
        while (i < lines.length) {
            const current = lines[i];
            if (current.trim() === '' || HEADING_LINE.test(current) ||
                UNORDERED_ITEM.test(current) || ORDERED_ITEM.test(current)) break;
            if (!first) para.appendChild(document.createElement('br'));
            appendInline(para, current);
            first = false;
            i++;
        }
        container.appendChild(para);
    }
}

/**
 * 渲染 AI 產出文字：清空 container，選填徽章（沿用各頁既有的 lf-badge 視覺語言），
 * 內文以上述安全子集解析後組 DOM 附加。取代各頁各自的 textContent／innerHTML 拼法。
 *
 * @param {HTMLElement} container
 * @param {string} text
 * @param {{ badge?: string, badgeClassName?: string }} [options]
 */
export function renderAiText(container, text, { badge, badgeClassName = 'lf-badge lf-badge--secondary' } = {}) {
    container.replaceChildren();

    if (badge) {
        const badgeEl = document.createElement('span');
        badgeEl.className = badgeClassName;
        badgeEl.textContent = badge;
        container.appendChild(badgeEl);
    }

    renderBlocks(container, text ?? '');
}

/**
 * 行內版：只解析 **粗體** 與 `行內代碼`，不產生區塊元素——給「AI 文字是一行、旁邊還要接
 * 其他行內元素（如下鑽連結）」的場合用（儀表板今日焦點的清單項）。換行當空白處理。
 *
 * @param {HTMLElement} target 附加目標（不清空既有內容，由呼叫端決定結構）
 * @param {string} text
 */
export function renderAiInline(target, text) {
    appendInline(target, (text ?? '').replace(/\s*\n\s*/g, ' '));
}
