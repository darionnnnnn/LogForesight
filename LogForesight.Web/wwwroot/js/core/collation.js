/**
 * 排序比較的共用出口：全站文字排序一律用 compareText。
 *
 * 碼位比較（`a < b`）對中文是任意順序，對數字字串會把 "10" 排在 "9" 前面；
 * `Intl.Collator('zh-Hant', { numeric: true })` 依語系排中文、並把連續數字當數值比。
 */

const textCollator = new Intl.Collator('zh-Hant', { numeric: true });

/** 文字比較（可直接給 Array.prototype.sort）；null／undefined 視為空字串 */
export function compareText(a, b) {
    return textCollator.compare(String(a ?? ''), String(b ?? ''));
}
