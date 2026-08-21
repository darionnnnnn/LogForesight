/**
 * 期間快捷（docs/WEB-SPEC.md §8.6-8）：問題查詢／報表／儀表板／主機詳情／排程作業共用。
 *
 * 期間終點一律錨在**昨天**（`analysisAnchorLocal`）而非今天：分析只產到昨天，
 * 錨在今天會讓區間的最後一天必然沒有資料。`data-range="1"` ＝「昨日」（起訖同一天）。
 *
 * 這裡是唯一一份計算：三頁原本各有一份逐行等價的複本，加一個選項要改三個地方。
 */
import { toLocalDateString, analysisAnchorLocal } from './format.js';

/**
 * 由天數算出 { from, to } 兩個本地日期字串（yyyy-MM-dd）。
 * days=1 → from 與 to 同為昨天；days=7 → 昨天往前算七天（含昨天）。
 */
export function rangeFromDays(days) {
    const to = new Date();
    to.setDate(to.getDate() - 1);
    const from = new Date(to);
    from.setDate(from.getDate() - days + 1);
    return { from: toLocalDateString(from), to: toLocalDateString(to) };
}

/** 期間終點（＝分析涵蓋的最後一天，昨天）；等同 format.js 的 analysisAnchorLocal */
export const rangeAnchor = analysisAnchorLocal;

/**
 * 綁定一組快捷鈕：每顆帶 `data-range="{天數}"`，按下時把 from/to 填進兩個日期欄位，
 * 把自己標成 active，再呼叫 onApply。
 *
 * fromInput／toInput 傳 null 時不填欄位（儀表板只用天數、沒有日期欄）。
 */
export function bindRangeChips({ container = document, fromInput, toInput, onApply, markActive = false }) {
    const buttons = [...container.querySelectorAll('[data-range]')];

    for (const button of buttons) {
        button.addEventListener('click', () => {
            const days = Number(button.dataset.range);
            const { from, to } = rangeFromDays(days);

            if (fromInput) fromInput.value = from;
            if (toInput) toInput.value = to;
            if (markActive) setActiveChip(buttons, days);

            onApply?.({ days, from, to });
        });
    }

    return buttons;
}

/** 把天數相符的那顆標成 active（篩選記憶還原時用） */
export function setActiveChip(buttons, days) {
    for (const button of buttons) {
        button.classList.toggle('active', Number(button.dataset.range) === days);
    }
}
