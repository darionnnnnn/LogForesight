/**
 * PRTG 擷取的啟用狀態與三種數值取數對象顯示字串（docs/PRTG-SPEC.md §3a、§7）。
 *
 * 啟用由獨立開關控制，範圍下拉保留設定值；保守策略不使用夜間歷史值範圍。
 * 畫面用詞：這個設定叫「數值取數對象」；由主機對應算出、決定感測器與狀態變更同步範圍的裝置集合叫「監看裝置」，兩者不可混稱。
 * 後端仍是兩個欄位（PrtgEnabled 有七個消費端、PrtgValueFetchScope 有六個），這裡只負責 UI 的對應。
 *
 * 維護頁（載入／存檔的值對應）與排程作業頁（狀態顯示）共用這一份，兩頁各寫一份就會在改字時只改到一邊。
 */

/**
 * 值 → 顯示字串。狀態文字用它，不重寫一份。
 * 下拉的 <option> 由 Prtg.cshtml 靜態產生；`PrtgAdminPageUiTests` 鎖住兩邊的 value 集合一致。
 */
export const PRTG_SCOPE_LABEL = {
    'triggered': '只抓觸發主機',
    'all-mapped': '全部已對應主機',
    'triggered-plus-list': '觸發主機＋指定清單'
};

/**
 * 儲存的範圍 → 下拉值。關閉擷取時仍顯示上次範圍，重新啟用不需重選。
 */
export function toScopeSelectValue(_prtgEnabled, prtgValueFetchScope) {
    return PRTG_SCOPE_LABEL[prtgValueFetchScope]
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
