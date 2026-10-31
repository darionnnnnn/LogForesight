/**
 * PRTG sensor 分類的中文顯示（單一字典）。
 *
 * label 是完整名稱（規則頁分類後綴與下拉選項）；short 是表格欄位用的短稱，沒有短稱時沿用 label。
 * 規則頁 Rules.cshtml 的 rule-prtg-sensor-category 選項文字必須等於這裡的 label（UiTests 守門）；
 * 數值就緒度表的 availability 指 ping 延遲，所以短稱是「Ping」，不與規則頁的「連通性」強制同字。
 */

const PRTG_SENSOR_CATEGORIES = {
    traffic: { label: '流量' },
    disk: { label: '磁碟空間', short: '磁碟' },
    cpu: { label: 'CPU' },
    memory: { label: '記憶體' },
    availability: { label: '連通性', short: 'Ping' },
    hardware: { label: '硬體健康' }
};

function entryOf(key) {
    if (!key) return null;
    return PRTG_SENSOR_CATEGORIES[String(key).toLowerCase()] ?? null;
}

/** 完整名稱；空值回空字串，未知 key 回原字串 */
export function prtgCategoryLabel(key) {
    if (!key) return '';
    return entryOf(key)?.label ?? String(key);
}

/** 表格短稱；沒有短稱時回完整名稱，未知 key 回原字串 */
export function prtgCategoryShort(key) {
    if (!key) return '';
    const entry = entryOf(key);
    return entry ? (entry.short ?? entry.label) : String(key);
}
