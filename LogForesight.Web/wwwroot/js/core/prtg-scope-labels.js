/**
 * PRTG 擷取的「關閉＋三種取數範圍」四值與顯示字串（docs/PRTG-SPEC.md §3a、§7）。
 *
 * 啟用與範圍在畫面上是同一個下拉：選「關閉」＝ PrtgEnabled false，選任一範圍＝ true 加該範圍。
 * 後端仍是兩個欄位（PrtgEnabled 有七個消費端、PrtgValueFetchScope 有六個），這裡只負責 UI 的對應。
 *
 * 維護頁（載入／存檔的值對應）與排程作業頁（狀態顯示）共用這一份，兩頁各寫一份就會在改字時只改到一邊。
 */

/** 「關閉」這個選項的值。不是後端的合法 scope，只存在於畫面上。 */
export const PRTG_SCOPE_OFF = 'off';

/**
 * 值 → 顯示字串。狀態文字用它，不重寫一份。
 * 下拉的 <option> 由 Prtg.cshtml 靜態產生（那邊的 off 寫「關閉（預設）」），
 * 這份是「狀態顯示」用的短標籤；`PrtgAdminPageUiTests` 鎖住兩邊的 value 集合一致。
 */
export const PRTG_SCOPE_LABEL = {
    [PRTG_SCOPE_OFF]: '關閉',
    'triggered': '只抓觸發主機',
    'all-mapped': '全部已對應主機',
    'triggered-plus-list': '觸發主機＋指定清單'
};

/**
 * 後端的兩個欄位 → 下拉的單一值。
 * 未啟用時一律回 off（不管 scope 存的是什麼），啟用時回 scope（不合法時退回 triggered，同後端 Normalize）。
 */
export function toScopeSelectValue(prtgEnabled, prtgValueFetchScope) {
    if (!prtgEnabled) return PRTG_SCOPE_OFF;
    return PRTG_SCOPE_LABEL[prtgValueFetchScope] && prtgValueFetchScope !== PRTG_SCOPE_OFF
        ? prtgValueFetchScope
        : 'triggered';
}

/** 模組狀態的顯示文字：關閉時只說未啟用，啟用時把生效範圍一起說出來。 */
export function prtgModuleStateText(prtgEnabled, prtgValueFetchScope) {
    if (!prtgEnabled) return '未啟用';
    return `已啟用：${PRTG_SCOPE_LABEL[toScopeSelectValue(true, prtgValueFetchScope)]}`;
}
