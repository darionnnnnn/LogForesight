/**
 * 站台掛載路徑（docs/WEB-SPEC.md §8.1a）。
 *
 * 站台可能掛在網站根目錄，也可能是 IIS 的子 Application（`/LogForesight/...`）。
 * **前端所有的連結組裝、轉址與路由比對都必須走這裡**——寫死 `/` 開頭的路徑在子
 * Application 下會打到網站根目錄，症狀是整站 404 或選單靜默不高亮。
 *
 * 獨立成一個模組而不是放在 api.js：ui.js 也要用它，而 api.js 反過來要用 ui.js 的 toast，
 * 放在一起會形成循環相依。
 */

/** 由 `_Layout.cshtml`／`Login.cshtml` 注入 `Request.PathBase`，不含尾斜線 */
const BASE = (window.LF_BASE || '').replace(/\/$/, '');

/**
 * 把 app 內的絕對路徑（`/records?x=1`）補上掛載前綴。
 * 非 `/` 開頭的值（相對路徑、完整網址、`#`、`//host/…`）原樣返回，重複呼叫不安全——
 * 同一個值只在「即將寫進 href／location」的那一處套一次。
 */
export function appUrl(path) {
    if (typeof path !== 'string' || !path.startsWith('/') || path.startsWith('//')) return path;
    return BASE + path;
}

/**
 * 目前頁面在 app 內的路徑（已去掉掛載前綴），供路由比對用。
 * 直接比對 `location.pathname` 在子 Application 下會失準，而且是**靜默失效**不是 404：
 * 選單不會高亮、登入頁判斷會失敗，畫面卻看起來正常。
 */
export function appPath() {
    const path = location.pathname;
    if (!BASE) return path;

    // 大小寫不敏感比對：IIS 的路由不分大小寫，使用者打 /logforesight/records 一樣進得來，
    // 但 LF_BASE 是伺服器設定的 /LogForesight——大小寫敏感的比對會在這裡失手，
    // 而失手的症狀是選單不高亮這種靜默失效，不會有人回報。切片仍用原字串。
    const lower = path.toLowerCase();
    const baseLower = BASE.toLowerCase();
    if (lower === baseLower || lower.startsWith(baseLower + '/')) {
        return path.slice(BASE.length) || '/';
    }
    return path;
}
