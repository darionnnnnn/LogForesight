/**
 * localStorage／sessionStorage 安全包裝（無 DOM 相依）。
 *
 * 隱私模式、停用站台資料或沙箱 iframe 下，存取 localStorage／sessionStorage 會擲 SecurityError；
 * 這裡一律吞掉例外：get 失敗回 null，set／remove 靜默失敗，讓呼叫端照常運作。
 */
/**
 * @param {() => Storage | undefined} getStore 取得底層儲存區；連「取得」本身都可能擲例外，所以在 try 內呼叫
 */
function createSafeStore(getStore) {
    return {
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
}

export const safeStorage = createSafeStore(() => globalThis.localStorage);

/** 同型包裝，對象是 sessionStorage（分頁內暫存，例如進行中的作業識別碼） */
export const safeSession = createSafeStore(() => globalThis.sessionStorage);
