/**
 * 字級偏好的級距（DESIGN-SYSTEM §3a）：頂列按鈕的 data-scale、樣式表的 :root[data-font-scale] 規則與這份清單一致
 * （FontScaleGuardUiTests 守門）。medium 是預設，樣式表不另寫規則。
 */
export const FONT_SCALES = ['small', 'medium', 'large', 'xlarge', 'xxlarge'];

/** 儲存值或按鈕值不在清單內（舊版、手改、空值）一律退回標準字級 */
export function normalizeFontScale(value) {
    return FONT_SCALES.includes(value) ? value : 'medium';
}
