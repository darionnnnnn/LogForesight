/**
 * 報表「處理成效」視角（docs/WEB-SPEC.md §9.6）：處理有沒有跟上、處理完還會不會再來。
 *
 * 三塊內容，資料來自 GET api/reports/effectiveness（延後載入，切到這個視角才打）：
 *   1. 各時段的處理狀態：直向堆疊條（已結案／處理中／未處理），一根一桶；點「未處理」段下鑽問題查詢；
 *   2. 未結案帳齡：依比例分段的細條＋數字，五段由新到舊色階加深；
 *   3. 結案後再發生：一個大數字（N / M 組、x%）＋觀察中組數＋前 10 名表格。
 * 三個指標標題旁各有說明圖示，文字用後端的 definitions（主管會被問「這個數字怎麼算的」）。
 *
 * 不碰全域狀態：頁面（reports.js）以 createEffectivenessView(deps) 注入圖表包裝、進度條元件與讀樣式表 token 的函式，
 * 行為測試也用替身注入。顏色一律取自色盤函式與樣式表 token，這裡沒有色碼字面。
 */

import { appUrl, recordsUrl } from '../core/paths.js';
import { renderTable, helpIcon } from '../core/ui.js';
import { formatNumber } from '../core/format.js';
import { bucketMode, bucketLabel } from './reports-common.js';

/** 處理狀態的三個層級：資料欄位、畫面名稱、下鑽用的處理狀態值（與問題查詢的 statuses 同值域） */
export const STATUS_SERIES = [
    { key: 'resolved', label: '已結案', statuses: 'resolved' },
    { key: 'inProgress', label: '處理中', statuses: 'in_progress' },
    { key: 'open', label: '未處理', statuses: 'open' }
];

/** 某一桶某一層級的下鑽網址：該桶起訖、風險高＋中（處理狀態只對高＋中風險日有意義） */
export function bucketDrillUrl(bucket, statuses) {
    return recordsUrl({ statuses, riskLevels: '高,中', from: bucket.from, to: bucket.to });
}

/** 帳齡的分段標籤：後端給的標籤直接用，沒有時以天數區間補 */
function agingLabel(band) {
    if (band.label) return band.label;
    return band.maxDays == null ? `${band.minDays} 天以上` : `${band.minDays}–${band.maxDays} 天`;
}

/** 帳齡分段的色階：單一色相、由新到舊逐級加深（--lf-heat-1 最淡、--lf-heat-5 最深）；分段多於色階時共用最深一級 */
export function agingSwatches(count, readToken) {
    return Array.from({ length: count }, (_, i) => readToken(`--lf-heat-${Math.min(i + 1, 5)}`));
}

/**
 * 「未結案」三段的說明列（純函式）：每段寫明單位。三段的「主機日」相加＝未結案主機日；
 * 案件數與「問題×主機」是另一種單位，只列在旁邊對照。待派試跑超過上限時拆不開，直說，不猜。
 */
export function unresolvedBreakdownLines(unresolved) {
    if (!unresolved) return [];
    const inHand = unresolved.inHandlers ?? {};
    const pending = unresolved.pendingDispatch ?? {};
    const notNeeded = unresolved.notNeeded ?? {};
    const oldest = inHand.oldestDays == null ? '' : `，最舊 ${inHand.oldestDays} 天`;
    const lines = [`在處理人手上：${inHand.hostDays ?? 0} 個主機日（進行中交辦 ${inHand.cases ?? 0} 項${oldest}）`];
    if (pending.tooLarge) {
        lines.push(pending.unsplitReason === 'permission'
            ? '待派與不需處理：需要派工權限才能拆開'
            : '待派與不需處理：期間太長，無法拆開，請縮短期間');
    } else {
        lines.push(`待派：${pending.hostDays ?? 0} 個主機日（可派的問題×主機 ${pending.issueHosts ?? 0} 個）`);
        lines.push(`不需處理：${notNeeded.hostDays ?? 0} 個主機日（嚴重度未達、已知雜訊、不再打擾、靜音等，不會派工）`);
    }
    return lines;
}

/** 再發生率的文字（純函式）：rate 為 null＝分母 0，不是 0% */
export function recurrenceSummary(recurrence) {
    const evaluated = recurrence.recurred + recurrence.notRecurred;
    if (recurrence.rate === null || recurrence.rate === undefined || evaluated === 0) {
        return { empty: true, text: '期間內沒有可評估的已處理問題', percent: null };
    }
    const percent = Math.round(recurrence.rate * 100);
    return { empty: false, text: `${recurrence.recurred} / ${evaluated} 組`, percent: `（${percent}%）` };
}

/**
 * @param {object} deps
 * @param {Document} [deps.doc]
 * @param {object} deps.charts            頁面包好的圖表層：bar、attachToolbar、renderNoData、statusColors
 * @param {(title: string, aria: string, segments: object[]) => HTMLElement} deps.progressBlock
 * @param {(name: string, fallback?: string) => string} deps.readToken   讀樣式表 token 的函式（頁面傳入 ui.js 提供的那一支）
 */
export function createEffectivenessView(deps) {
    const { charts, progressBlock } = deps;
    const readVar = deps.readToken;
    const doc = deps.doc ?? document;
    const byId = id => doc.getElementById(id);
    let bucketChart = null;

    function setHelp(slotId, content, title) {
        const slot = byId(slotId);
        slot.replaceChildren();
        if (content) slot.appendChild(helpIcon(content, title));
    }

    function renderBuckets(data) {
        const wrapper = byId('eff-buckets-wrapper');
        const toolbar = byId('eff-buckets-toolbar');
        const buckets = data.buckets ?? [];
        // 上一次切成表格時圖表容器被藏起來；重畫一律回到圖表，否則新表格容器也是隱藏的，整張卡空白
        wrapper.classList.remove('d-none');
        bucketChart?.destroy();
        bucketChart = null;

        if (buckets.length === 0 || buckets.every(b => b.total === 0)) {
            // 沒資料也收掉上一次的表格切換：舊按鈕留著會讓人切出上一期的數字
            charts.attachToolbar(toolbar, { canvasWrapper: wrapper, title: '', tableColumns: [], tableRows: [] });
            toolbar.replaceChildren();
            charts.renderNoData(wrapper, '此期間沒有風險日');
            return;
        }

        const status = charts.statusColors();
        const colors = { resolved: status.success, inProgress: status.inProgress, open: status.unhandled };
        const mode = bucketMode(buckets);
        const labels = buckets.map(b => bucketLabel(b, mode));

        bucketChart = charts.bar(byId('eff-buckets-chart'), {
            data: {
                labels,
                datasets: STATUS_SERIES.map(s => ({
                    label: s.label,
                    data: buckets.map(b => b[s.key]),
                    backgroundColor: colors[s.key],
                    maxBarThickness: 48
                }))
            },
            options: {
                scales: {
                    x: { stacked: true, grid: { display: false }, ticks: { autoSkip: true, maxRotation: 0 } },
                    y: { stacked: true, beginAtZero: true, ticks: { precision: 0 } }
                }
            },
            // 每一段都可下鑽：帶該桶起訖與該層級的處理狀態，點進去的筆數與柱段對得上
            drillTo: point => bucketDrillUrl(buckets[point.index], STATUS_SERIES[point.datasetIndex].statuses)
        });

        charts.attachToolbar(toolbar, {
            canvasWrapper: wrapper,
            title: '各時段的處理狀態',
            tableColumns: ['時段', '已結案', '處理中', '未處理', '合計'],
            tableRows: buckets.map((b, i) => [labels[i], b.resolved, b.inProgress, b.open, b.total])
        });
    }

    function renderAging(data) {
        const body = byId('eff-aging-body');
        body.replaceChildren();
        const total = data.unresolvedTotal ?? 0;
        if (!(total > 0)) {
            const empty = doc.createElement('p');
            empty.className = 'mb-0';
            empty.textContent = '目前沒有未結案的風險日';
            body.appendChild(empty);
            return;
        }

        const bands = data.aging ?? [];
        const swatches = agingSwatches(bands.length, readVar);
        const segments = bands.map((band, i) => ({ label: agingLabel(band), value: band.count, color: swatches[i] }));
        body.appendChild(progressBlock(
            `未結案 ${formatNumber(total)}`,
            `未結案帳齡：${segments.map(s => `${s.label} ${s.value}`).join('、')}，共 ${total} 個風險日（算到 ${data.asOf}）`,
            segments));

        const lines = doc.createElement('ul');
        lines.className = 'lf-text-meta mt-2 mb-0';
        for (const text of unresolvedBreakdownLines(data.unresolved)) {
            const item = doc.createElement('li');
            item.textContent = text;
            lines.appendChild(item);
        }
        body.appendChild(lines);
    }

    /** 前 10 名表格：問題可點（下鑽問題查詢的依問題視角，帶來源、事件編號與期間），後兩欄是短值欄 */
    function renderRecurringTable(container, topRecurring, range) {
        renderTable(container, {
            caption: '結案後再發生的前 10 名問題',
            rows: topRecurring,
            columns: [
                {
                    key: 'label', title: '問題', className: 'lf-break-anywhere',
                    render: item => {
                        const link = doc.createElement('a');
                        link.textContent = item.label;
                        link.href = appUrl(recordsUrl({
                            view: 'issue', source: item.source, eventId: item.eventId,
                            riskLevels: '高,中,低', from: range.from, to: range.to
                        }));
                        return link;
                    }
                },
                { key: 'hostCount', title: '已處理主機', className: 'lf-col-nowrap', render: item => formatNumber(item.hostCount) },
                { key: 'recurredCount', title: '再發生', className: 'lf-col-nowrap', render: item => formatNumber(item.recurredCount) }
            ]
        });
    }

    function renderRecurrence(data) {
        const body = byId('eff-recur-body');
        body.replaceChildren();
        const recurrence = data.recurrence;
        const summary = recurrenceSummary(recurrence);

        const metric = doc.createElement('div');
        metric.className = 'lf-report-recur__metric';
        const value = doc.createElement('p');
        if (summary.empty) {
            value.className = 'mb-0';
            value.textContent = summary.text;
        } else {
            value.className = 'lf-report-recur__value mb-0';
            const percent = doc.createElement('span');
            percent.className = 'lf-text-meta';
            percent.textContent = summary.percent;
            value.append(summary.text, ' ', percent);
        }
        metric.appendChild(value);
        if (recurrence.observing > 0) {
            const note = doc.createElement('p');
            note.className = 'lf-text-meta mb-0';
            note.textContent = `另有 ${formatNumber(recurrence.observing)} 組仍在觀察期`;
            metric.appendChild(note);
        }
        body.appendChild(metric);

        const top = recurrence.topRecurring ?? [];
        const list = doc.createElement('div');
        list.className = 'lf-report-recur__list';
        if (top.length === 0) {
            const none = doc.createElement('p');
            none.className = 'lf-text-meta mb-0';
            none.textContent = '期間內沒有再發生的問題';
            list.appendChild(none);
        } else {
            renderRecurringTable(list, top, data);
        }
        body.appendChild(list);
    }

    return {
        /** 依後端回應畫三塊；「此視角不套用顯示範圍」的提示由頁面（reports.js）依目前範圍顯示 */
        render(data) {
            const defs = data.definitions ?? {};
            setHelp('eff-buckets-help', defs.resolution, '處理狀態怎麼算');
            setHelp('eff-aging-help', defs.aging, '帳齡怎麼算');
            setHelp('eff-recur-help', defs.recurrence, '再發生怎麼算');
            renderBuckets(data);
            renderAging(data);
            renderRecurrence(data);
        }
    };
}
