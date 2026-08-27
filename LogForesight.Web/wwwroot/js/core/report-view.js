/**
 * 報告全文的共用呈現（風險／體檢／權限異動三種共用）。
 *
 * 三種報告的資料形狀相同（ReportViewDto），呈現需求也相同：等寬字型原樣顯示、
 * 可複製、可下載成 txt、可列印。共用一個模組是為了讓三個入口的視覺語彙與鍵盤行為
 * 完全一致，而不是各頁各刻一份會慢慢長歪的卡片。
 *
 * 兩種容器，同一個內容元件：
 *   - reportCard()：頁面內的可收合卡片（分析紀錄詳情——報告與結構化內容並列閱讀）
 *   - openReportModal()：對話框（清單頁——報告是某一列的附屬脈絡，沒有常駐版位）
 *
 * 報告內容是批次產生的純文字（含 AI 產出），一律走 textContent，
 * 不進 markdown-lite——這裡要的是一字不改照實顯示。
 */

import { icon, toast, showDetailModal } from './ui.js';

/**
 * 副標：日期＋風險等級＋類別。這幾個欄位在全文的標頭裡也有，但那要展開才看得到；
 * 收合狀態下使用者需要一眼判斷「這份值不值得展開」。
 */
function subtitleOf(report) {
    const parts = [report.reportDate];
    if (report.riskLevel) parts.push(`風險${report.riskLevel}`);
    if (report.categories) parts.push(report.categories.split('+').join('、'));
    return parts.join('｜');
}

/**
 * 下載成 txt。檔名沿用報告產生當時的命名慣例（後端存在 fileName），
 * 讓升級前後拿到的檔案看起來是同一批東西。
 *
 * 用 Blob + a[download] 而不是後端另開下載端點：內容已經在畫面上了，
 * 為了同一份文字再打一次伺服器沒有意義，也不必為此把檔案落地到站台主機——
 * 那正是本輪要移除的東西。
 */
function downloadReport(report) {
    // BOM：Windows 記事本／Excel 讀無 BOM 的 UTF-8 中文會是亂碼，而報告是拿去給人看、
    // 常被直接雙擊開啟的交付物
    const blob = new Blob(['﻿' + report.content], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = report.fileName || 'report.txt';
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}

async function copyReport(report) {
    try {
        await navigator.clipboard.writeText(report.content);
        toast('已複製報告全文', 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
}

/**
 * 工具列（複製／下載／列印）。刻意都是文字鈕而非純圖示鈕——
 * 純圖示鈕在沒有可見標籤時對螢幕閱讀器與不熟悉圖示的使用者都是猜謎。
 */
function toolbar(report) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-2 lf-no-print';

    const make = (text, handler) => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary';
        btn.textContent = text;
        btn.addEventListener('click', event => {
            // 卡片模式下整個 header 都是開合熱區，工具列的點擊不該連帶把卡片合起來
            event.stopPropagation();
            handler();
        });
        return btn;
    };

    wrap.append(
        make('複製', () => copyReport(report)),
        make('下載', () => downloadReport(report)),
        make('列印', () => window.print()));
    return wrap;
}

function pre(report) {
    const el = document.createElement('pre');
    el.className = 'report-text';
    el.textContent = report.content;
    return el;
}

/**
 * 頁面內的可收合報告卡片。
 *
 * @param {object} options
 * @param {string} options.storageKey 展開狀態的 localStorage 鍵——常看全文的人不必每次進來重新展開
 * @param {string} [options.title] 標題；省略時用報告種類的中文名稱
 * @returns {{ el: HTMLElement, setReport: (report: object|null) => void }}
 */
export function reportCard({ storageKey, title } = {}) {
    const section = document.createElement('section');
    section.className = 'lf-card d-none';

    const header = document.createElement('div');
    header.className = 'lf-card__header lf-card__header--clickable';
    header.setAttribute('role', 'button');
    header.tabIndex = 0;
    header.setAttribute('aria-expanded', 'false');

    const left = document.createElement('div');
    left.className = 'd-flex align-items-center gap-2';

    const caret = document.createElement('span');
    caret.className = 'lf-collapse-caret';
    caret.appendChild(icon('chevron-down'));

    const heading = document.createElement('h2');
    heading.className = 'lf-card__title mb-0';

    const subtitle = document.createElement('span');
    subtitle.className = 'small text-muted';

    left.append(caret, heading, subtitle);
    header.appendChild(left);

    const body = document.createElement('div');
    body.className = 'lf-card__body d-none';

    section.append(header, body);

    const bodyId = `lf-report-body-${Math.random().toString(36).slice(2, 10)}`;
    body.id = bodyId;
    header.setAttribute('aria-controls', bodyId);

    function applyExpanded(nowOpen) {
        body.classList.toggle('d-none', !nowOpen);
        caret.classList.toggle('lf-collapse-caret--open', nowOpen);
        header.setAttribute('aria-expanded', String(nowOpen));
        if (storageKey) localStorage.setItem(storageKey, String(nowOpen));
    }

    header.addEventListener('click', () => applyExpanded(body.classList.contains('d-none')));
    header.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            applyExpanded(body.classList.contains('d-none'));
        }
    });

    return {
        el: section,
        setReport(report) {
            if (!report) {
                section.classList.add('d-none');
                return;
            }

            heading.textContent = title ?? `${report.kindName}全文`;
            subtitle.textContent = subtitleOf(report);

            // 工具列每次重建：它綁著這一份報告的內容，留著舊的會下載到上一份
            header.querySelector('.lf-no-print')?.remove();
            header.appendChild(toolbar(report));

            body.replaceChildren(pre(report));
            section.classList.remove('d-none');

            applyExpanded(storageKey ? localStorage.getItem(storageKey) === 'true' : false);
        }
    };
}

/**
 * 對話框呈現（清單頁用）。報告是某一列的附屬脈絡，頁面上沒有常駐版位。
 */
export function openReportModal(report) {
    const wrap = document.createElement('div');

    const head = document.createElement('div');
    head.className = 'd-flex justify-content-between align-items-center gap-2 mb-2';

    const meta = document.createElement('span');
    meta.className = 'small text-muted';
    meta.textContent = subtitleOf(report);

    head.append(meta, toolbar(report));
    wrap.append(head, pre(report));

    showDetailModal({ title: `${report.kindName}全文`, body: wrap, size: 'modal-xl' });
}
