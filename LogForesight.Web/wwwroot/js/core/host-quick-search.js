/**
 * 頁頂主機快速搜尋：輸入名稱或 IP，列出可見範圍內符合的主機（最多 10 筆），選取後到主機詳情。
 *
 * 範圍由伺服器決定：沿用 GET /api/hosts?query=（名稱、顯示名、IP 包含比對，依登入者的可見範圍過濾），
 * 前端不自己過濾也不快取，受限使用者搜不到範圍外的主機。請求序號守門：慢回應不蓋掉新輸入的結果。
 * 這是使用者輸入觸發的查詢，不是常駐輪詢，所以不標 background。
 */

import { api } from './api.js';
import { appUrl } from './paths.js';
import { createSeqGuard } from './async-guards.js';

/** 一次最多列幾筆 */
export const HOST_SEARCH_LIMIT = 10;

/** 輸入停頓多久才送出查詢（毫秒） */
export const HOST_SEARCH_DELAY_MS = 250;

/** 主機詳情的連結：連結組裝一律走 appUrl，站台掛在子 Application 時才不會 404 */
export function hostSearchHref(hostId) {
    return appUrl(`/hosts/${hostId}`);
}

/** 結果列的主文字：名稱，有 Sentinel 顯示名時附在括號；次要文字是 IP */
function itemMain(host) {
    return host.displayName ? `${host.hostName}（${host.displayName}）` : host.hostName;
}

/**
 * @param {{input: HTMLInputElement, results: HTMLElement, delay?: number}} elements
 *        input＝搜尋欄；results＝結果容器（空白時隱藏，由本函式切換 d-none）
 * @returns {{ run(keyword: string): Promise<void>, search(keyword: string): void, clear(): void }}
 */
export function createHostQuickSearch({ input, results, delay = HOST_SEARCH_DELAY_MS }) {
    const seq = createSeqGuard();
    let timer = null;
    let firstHref = null;

    function show(nodes) {
        results.replaceChildren(...nodes);
        results.classList.toggle('d-none', nodes.length === 0);
    }

    function note(text) {
        const item = document.createElement('div');
        item.className = 'lf-host-search__note lf-text-meta';
        item.textContent = text;
        return item;
    }

    function clear() {
        clearTimeout(timer);
        timer = null;
        seq.next();   // 讓在途請求變成過期
        firstHref = null;
        show([]);
    }

    async function run(keyword) {
        clearTimeout(timer);
        timer = null;
        const text = String(keyword ?? '').trim();
        if (text === '') {
            clear();
            return;
        }

        const id = seq.next();
        let data;
        try {
            const params = new URLSearchParams({ query: text, limit: String(HOST_SEARCH_LIMIT) });
            data = await api.get(`/api/hosts?${params}`, { silent: true });
        } catch {
            if (!seq.isCurrent(id)) return;
            firstHref = null;
            show([note('搜尋失敗，請再試一次')]);
            return;
        }
        if (!seq.isCurrent(id)) return;   // 更新的輸入已送出，這筆是過期回應

        const items = (data?.items ?? []).slice(0, HOST_SEARCH_LIMIT);
        if (items.length === 0) {
            firstHref = null;
            show([note('找不到符合的主機')]);
            return;
        }

        const nodes = items.map(host => {
            const link = document.createElement('a');
            link.className = 'lf-host-search__item';
            link.href = hostSearchHref(host.hostId);
            const main = document.createElement('span');
            main.className = 'lf-text-title';
            main.textContent = itemMain(host);
            link.appendChild(main);
            if (host.ipAddress) {
                const ip = document.createElement('span');
                ip.className = 'lf-text-meta lf-text-data';
                ip.textContent = host.ipAddress;
                link.append(' ', ip);
            }
            return link;
        });
        firstHref = nodes[0].href;
        // 實際符合的比顯示的多時直說，不讓人以為就只有這些
        if (data?.truncated) nodes.push(note(`只列前 ${HOST_SEARCH_LIMIT} 筆，共 ${data.total} 筆符合，請輸入更完整的名稱或 IP`));
        show(nodes);
    }

    input.addEventListener('input', () => {
        clearTimeout(timer);
        if (input.value.trim() === '') {
            clear();
            return;
        }
        timer = setTimeout(() => run(input.value), delay);
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            input.value = '';
            clear();
        } else if (event.key === 'Enter') {
            event.preventDefault();
            // 還沒查就按 Enter：先查再依第一筆前往
            run(input.value).then(() => { if (firstHref) location.href = firstHref; });
        }
    });

    return { run, search: keyword => run(keyword), clear };
}

/** 掛到版面的搜尋欄（_Layout 的 #lf-host-search）；沒有業務檢視資格的帳號不顯示 */
export function initHostQuickSearch(user) {
    const root = document.getElementById('lf-host-search');
    if (!root || user?.isServerAdmin) return null;   // 維護帳號沒有任何主機（VisibilityService），搜尋只會永遠空白
    root.classList.remove('d-none');
    const search = createHostQuickSearch({
        input: document.getElementById('lf-host-search-input'),
        results: document.getElementById('lf-host-search-results')
    });
    // 點到搜尋區以外就收起結果（結果是連結，點結果本身會直接換頁）
    document.addEventListener('click', event => {
        if (!root.contains(event.target)) search.clear();
    });
    return search;
}
