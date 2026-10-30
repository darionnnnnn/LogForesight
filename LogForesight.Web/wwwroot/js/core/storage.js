/**
 * localStorage 安全包裝（無 DOM 相依）。
 *
 * 隱私模式、停用站台資料或沙箱 iframe 下，存取 localStorage 會擲 SecurityError；
 * 這裡一律吞掉例外：get 失敗回 null，set／remove 靜默失敗，讓呼叫端照常運作。
 */
function getStore() {
    return globalThis.localStorage;
}

export const safeStorage = {
    /**
     * @param {string} key
     * @returns {string | null} 讀取失敗或沒有此鍵時回 null
     */
    get(key) {
        try {
            const store = getStore();
            return store ? store.getItem(key) : null;
        } catch {
            return null;
        }
    },

    /** @param {string} key @param {string} value 寫入失敗時靜默略過 */
    set(key, value) {
        try {
            getStore().setItem(key, value);
        } catch {
            /* 無法寫入即略過，不影響頁面流程 */
        }
    },

    /** @param {string} key 移除失敗時靜默略過 */
    remove(key) {
        try {
            getStore().removeItem(key);
        } catch {
            /* 無法移除即略過，不影響頁面流程 */
        }
    },
};
