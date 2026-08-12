/**
 * AI 產出文字的單一渲染入口（docs/archive/HISTORY.md S7）。
 *
 * AI 回覆常帶 `**粗體**`／`- 清單` 之類的 Markdown 語法，但畫面上一直是 textContent 原樣呈現
 * （星號原樣顯示）——這是刻意的安全設計：AI 產出不可信任為 HTML，過去沒有引入任何 Markdown
 * 轉換庫，因為那類庫多半支援連結/圖片/原始 HTML，等於重新打開 XSS 的門。
 *
 * 這裡只解析一個刻意收窄的安全子集：**粗體**、`行內代碼`、- / 1. 清單、# 開頭行（轉粗體行）、
 * 換行、GFM 風格表格（回饋十七輪批次G-4：AI 常用表格列數量/等級比較，原本整段被當純文字
 * 逐行印出，`|---|---|` 分隔列與 `|` 全部照原樣顯示，可讀性很差）。全程用
 * document.createElement／createTextNode 組 DOM，絕不使用 innerHTML——
 * 其餘語法（連結、圖片、原始 HTML 標籤）一律當純文字顯示，不會被解析成可執行內容；
 * 不合法的偽表格（無分隔列、或分隔列格式不對）退回段落渲染，不誤判。
 */

const HEADING_LINE = /^\s*#+\s+(.*)$/;
const UNORDERED_ITEM = /^\s*[-*]\s+(.*)$/;
const ORDERED_ITEM = /^\s*\d+[.)]\s+(.*)$/;
const INLINE_TOKEN = /\*\*([^*]+)\*\*|`([^`]+)`/g;
const TABLE_SEPARATOR_CELL = /^:?-{1,}:?$/;

/** 一列表格文字拆成儲存格：容忍有無外側 `|` 的兩種寫法，兩端各自的空白格移除 */
function splitTableRow(line) {
    let cells = line.split('|');
    if (cells.length > 1 && cells[0].trim() === '') cells = cells.slice(1);
    if (cells.length > 1 && cells[cells.length - 1].trim() === '') cells = cells.slice(0, -1);
    return cells.map(c => c.trim());
}

/** 分隔列（第二行）判定：每格都是 `---`／`:---`／`---:`／`:---:` 之類的純橫線 */
function isTableSeparatorLine(line) {
    if (!line.includes('|')) return false;
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(c => TABLE_SEPARATOR_CELL.test(c));
}

/** 「新表格開頭」邊界用的嚴格版分隔列判定（終檢修正）：每格至少兩條橫線。
 * 表格「續行」迴圈要中斷於下一個表格的分隔列（見呼叫端），但 GFM 的分隔格語法容許單一
 * 橫線，`| - | - |` 這種常見的「佔位資料列」會被寬鬆版誤判成分隔列——中斷發生在錯的地方，
 * 一個表格被拆成兩半、前一列還被誤升成第二個表格的表頭。實務上的 markdown 產生器
 * （含 AI 輸出）分隔列幾乎都寫 `---`，佔位格幾乎都是單一 `-`，用「至少兩條橫線」區分兩者；
 * 表格「開頭」判定維持寬鬆版不變（單橫線分隔列開新表格仍是合法 GFM，兩處語意不同）。 */
function isStrictTableSeparatorLine(line) {
    if (!line.includes('|')) return false;
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(c => /^:?-{2,}:?$/.test(c));
}

/** 判斷這行「去除行內代碼」後是否還算含 `|`（體檢輪修正）：行內代碼裡的管線符號
 * （如 `netstat -an | grep ESTABLISH` 這種指令範例）不該被當成表格儲存格分隔——
 * 否則表格後面緊接著（無空白行分隔）這種散文行會被誤吞成表格的資料列，行內代碼也被
 * 從中間切斷。只用於「這行算不算表格行」的判定，不影響 splitTableRow 實際切格的行為
 * （已確認是表格行之後，儲存格裡的行內代碼仍走 appendInline 正常解析）。 */
function hasPipeOutsideCode(line) {
    return line.replace(/`[^`]*`/g, '').includes('|');
}

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

        // 表格：目前行含 `|` 且下一行是分隔列（--- 之類）才判定為表格開頭——單純一行含 `|`
        // 的散文（如路徑、管線指令）不會有這種分隔列跟著，不會被誤判
        if (hasPipeOutsideCode(line) && i + 1 < lines.length && isTableSeparatorLine(lines[i + 1])) {
            const headerCells = splitTableRow(line);
            i += 2;
            const bodyRows = [];
            // 續行也要中斷於「下一行其實是另一個表格的開頭」（體檢輪修正）：兩個 GFM 表格
            // 中間沒有空白行分隔時，起點判定的防呆邏輯要在這裡對稱套用一次，否則第二個表格
            // 的表頭會被吞成第一個表格的普通資料列、分隔列會被當字面文字顯示成 ---。
            // 這裡用「嚴格版」分隔列判定（見 isStrictTableSeparatorLine）：`| - | - |` 這種
            // 單橫線佔位資料列不該觸發中斷，否則表格會被拆成兩半（終檢修正）。
            while (
                i < lines.length && lines[i].trim() !== '' && hasPipeOutsideCode(lines[i]) &&
                !(i + 1 < lines.length && isStrictTableSeparatorLine(lines[i + 1]))
            ) {
                bodyRows.push(splitTableRow(lines[i]));
                i++;
            }

            const wrap = document.createElement('div');
            wrap.className = 'table-responsive mb-2';
            const table = document.createElement('table');
            table.className = 'table table-sm table-bordered align-middle mb-0';

            const thead = document.createElement('thead');
            const headTr = document.createElement('tr');
            headerCells.forEach(cellText => {
                const th = document.createElement('th');
                appendInline(th, cellText);
                headTr.appendChild(th);
            });
            thead.appendChild(headTr);
            table.appendChild(thead);

            const tbody = document.createElement('tbody');
            bodyRows.forEach(cells => {
                const tr = document.createElement('tr');
                cells.forEach(cellText => {
                    const td = document.createElement('td');
                    appendInline(td, cellText);
                    tr.appendChild(td);
                });
                tbody.appendChild(tr);
            });
            table.appendChild(tbody);

            wrap.appendChild(table);
            container.appendChild(wrap);
            continue;
        }

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
            // 表格開頭也要中斷段落（同上方表格判定的條件）——不然「前情提要」這種散文開頭
            // 後面接的表格會被吞進同一個段落當純文字印出，永遠走不到表格解析
            if (current.trim() === '' || HEADING_LINE.test(current) ||
                UNORDERED_ITEM.test(current) || ORDERED_ITEM.test(current) ||
                (hasPipeOutsideCode(current) && i + 1 < lines.length && isTableSeparatorLine(lines[i + 1]))) break;
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
