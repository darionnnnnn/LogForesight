/**
 * 登入後導回網址的安全檢查（純函式，無 DOM 相依）。
 * 只接受站內相對路徑：瀏覽器會把反斜線當 '/'（'/\evil.com' 等同 '//evil.com'），所以任何反斜線都拒絕；
 * 瀏覽器 URL parser 會剝除 tab／LF／CR（'/<TAB>/evil.com' 會變成 '//evil.com'），含這些字元一律拒絕、不剝除後放行；
 * 含 '://' 的值也一律拒絕，不賭解析器怎麼讀。
 */
export function isSafeReturnUrl(url) {
    return typeof url === 'string'
        && !/[\t\n\r]/.test(url)
        && url.startsWith('/')
        && !url.startsWith('//')
        && !url.includes('\\')
        && !url.includes('://');
}
