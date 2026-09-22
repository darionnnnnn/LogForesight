/**
 * PRTG 擷取的「關閉＋三種數值取數對象」四值與顯示字串（docs/PRTG-SPEC.md §3a、§7）。
 *
 * 啟用與數值取數對象在畫面上是同一個下拉：選「關閉」＝ PrtgEnabled false，選任一對象＝ true 加該對象。
 * 畫面用詞：這個設定叫「數值取數對象」；由主機對應算出、決定感測器與狀態變更同步範圍的裝置集合叫「監看裝置」，兩者不可混稱。
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
/** 數值取數對象在保守策略下不適用的提示文字：排程頁卡片與維護頁共用。 */
export function prtgScopeInapplicableText(isRunsCard = false) {
    return isRunsCard
        ? '不適用（保守策略，數值由快照供應）'
        : '保守策略下不適用';
}

/** 模組狀態的顯示文字：關閉時只說未啟用，啟用時把生效的數值取數對象一起說出來（保守策略時顯示不適用）。 */
export function prtgModuleStateText(prtgEnabled, prtgValueFetchScope, prtgFetchStrategy = null) {
    if (!prtgEnabled) return '未啟用';
    const scopeText = prtgFetchStrategy && prtgFetchStrategy !== 'aggressive'
        ? prtgScopeInapplicableText(true)
        : PRTG_SCOPE_LABEL[toScopeSelectValue(true, prtgValueFetchScope)];
    return `已啟用（數值取數對象：${scopeText}）`;
}
