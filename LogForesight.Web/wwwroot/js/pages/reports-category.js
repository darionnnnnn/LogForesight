/**
 * 報表「問題類型」視角（docs/WEB-SPEC.md §9.6）：是哪一類問題在變多。
 *
 * 一張「類別 × 時間」熱度表（HTML 表格，不是 canvas），資料來自 GET api/reports/category-trend（延後載入）：
 *   - 列＝類別（中文名）、欄＝時間桶、格內印「問題日」數字（問題日＝一台主機一天出現的一個問題）；
 *   - 格子底色是單一色相的淡階（--lf-heat-1～5，只有最高一級接近實色），數字才是主要資訊；0 的格不上色、不可點；
 *   - 非 0 的格是連結，下鑽問題查詢（帶類別與該桶起訖），每格有可讀名稱（類別、期間、數量）給鍵盤與讀屏使用者；
 *   - 最右一欄「合計」。表格由共用的 renderTable 產生，橫向捲動的提示沿用設計系統既有作法（§8.4a）。
 * 表格模式與列印是同一張表，不另做。
 */

import { appUrl, recordsUrl } from '../core/paths.js';
import { renderTable, helpIcon } from '../core/ui.js';
import { categoryName, formatNumber } from '../core/format.js';
import { bucketMode, bucketLabel, heatLevel } from './reports-common.js';

export const HEAT_HELP = '問題日：一台主機在一天裡出現的一個問題算一個問題日。格子越深，表示那一類在那段時間的問題日越多；點格子可看組成它的問題清單。';

/** 某一格的下鑽網址：類別與該桶起訖。嚴重度是問題層級、與當日風險等級無關，所以風險層級帶全部（否則低風險日的問題會被預設隱藏而對不上數字） */
export function cellDrillUrl(category, bucket) {
    return recordsUrl({ categories: category, riskLevels: '高,中,低', from: bucket.from, to: bucket.to });
}

/** 每格的可讀名稱，例如「儲存裝置，11/03–11/09，12 個問題日」 */
export function cellAriaLabel(categoryText, periodText, count) {
    return `${categoryText}，${periodText}，${count} 個問題日`;
}

/**
 * @param {object} [deps]
 * @param {Document} [deps.doc]
 */
export function createCategoryView(deps = {}) {
    const doc = deps.doc ?? document;

    function cellNode(row, index, bucket, periodText, max) {
        const count = row.cells[index] ?? 0;
        if (count <= 0) {
            // 0 的格：不上色、不可點，只留數字（次要色）
            const zero = doc.createElement('span');
            zero.className = 'lf-heat__zero';
            zero.textContent = '0';
            return zero;
        }
        const link = doc.createElement('a');
        link.className = `lf-heat__cell lf-heat__cell--${heatLevel(count, max)}`;
        link.href = appUrl(cellDrillUrl(row.category, bucket));
        link.textContent = formatNumber(count);
        link.setAttribute('aria-label', cellAriaLabel(categoryName(row.category), periodText, count));
        return link;
    }

    return {
        render(data) {
            const help = doc.getElementById('cat-help');
            help.replaceChildren(helpIcon(HEAT_HELP, '問題日怎麼算'));

            const buckets = data.buckets ?? [];
            const mode = bucketMode(buckets);
            const periods = buckets.map(b => bucketLabel(b, mode));
            const max = data.cellMax ?? 0;

            const columns = [
                { key: 'category', title: '類型', className: 'lf-col-nowrap lf-heat__label', render: row => categoryName(row.category) },
                ...buckets.map((bucket, i) => ({
                    key: `bucket${i}`,
                    title: periods[i],
                    className: 'lf-col-nowrap lf-heat__col',
                    render: row => cellNode(row, i, bucket, periods[i], max)
                })),
                { key: 'total', title: '合計', className: 'lf-col-nowrap', render: row => formatNumber(row.total) }
            ];

            renderTable(doc.getElementById('cat-heat-body'), {
                columns,
                rows: data.rows ?? [],
                caption: '類別與時間的問題日熱度表',
                empty: { title: '此期間沒有問題', hint: '換一段期間再看看。', icon: 'inbox' }
            });
        }
    };
}
