// 多人同時操作情境（docs/FEEDBACK-81-CONCURRENCY-PLAN.md 第 4 節）。資料：D2 多日模擬（UsabilitySimulationTests）。
// 每個情境自己開使用者、自己查最終狀態；彼此不共用瀏覽器。會改資料，重跑前重新部署資料集。

const HANDLERS = ['h-chen', 'h-lin', 'h-wang', 'h-huang', 'h-wu', 'h-tsai'];
const J = v => JSON.stringify(v);
const today = (offset = 0) => { const d = new Date(Date.now() + offset * 86400000); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };

// ── 共用的頁面操作 ───────────────────────────────────────────────────────────

const api = (u, method, url, body) => u.inPage(`return __mu.api(${J(method)}, ${J(url)}, ${body === undefined ? 'undefined' : J(body)});`);
const get = async (u, url) => (await api(u, 'GET', url)).body?.data;

/** 某處理人的進行中單（id、問題、進行中台數），台數多的在前 */
async function activeOrders(u, handlerId) {
    const d = await get(u, `/api/handlers/${handlerId}/work-orders?status=active&page=1&pageSize=50`);
    return (d?.items || []).map(o => ({ id: o.workOrderId, label: o.issueLabel, active: o.counts.active, total: o.counts.total }))
        .sort((a, b) => b.active - a.active);
}

const orderState = async (u, id) => {
    const d = await get(u, `/api/work-orders/${id}`);
    return d ? { 處理人: d.handlerName || d.handlerId, 結案: d.closedReason || (d.closedAt ? '已結案' : '進行中'), 進行中: d.counts?.active, 總數: d.counts?.total, 處理中: d.counts?.inProgress, 觀察: d.counts?.observing, 已結: d.counts?.closed } : '（讀不到）';
};

const KPI = `return __mu.text('main', 140);`;
const ROW = text => `const r = __mu.row(${J(text)}); return r ? r.innerText.replace(/\\s+/g, ' ').slice(0, 160) : '（清單上沒有這一列）';`;

/** 交辦單頁：勾前 n 台進行中成員並開回覆彈窗、填好，不送出 */
async function prepareMemberReply(u, orderId, n, status, note, name = '準備回覆') {
    await u.step(`開交辦單 #${orderId}`, `開啟 /work-orders/${orderId}`, () => u.goto(`/work-orders/${orderId}`), `return __mu.text('main', 160);`);
    return u.step(name, `勾選前 ${n} 台 → 回覆選取的主機 → 選「${status}」→ 填說明`, async () => {
        await u.inPage(`await __mu.waitFor(() => document.querySelectorAll('main tbody input[type=checkbox]').length > 0);
            const boxes = [...document.querySelectorAll('main tbody input[type=checkbox]')].filter(c => c.offsetParent && !c.disabled).slice(0, ${n});
            boxes.forEach(c => { if (!c.checked) c.click(); });
            __mu.click('回覆選取的主機');
            await __mu.waitFor(() => __mu.modal());
            __mu.pickStatus('.modal.show', ${J(status)});
            const d = document.querySelector('.modal.show input[type=date]'); if (d && d.offsetParent) __mu.setValue(d, ${J(today(7))});
            __mu.typeIn('.modal.show', ${J(note)});`);
    }, `return __mu.modalText();`);
}

const submitModal = (u, text = '送出', name = '送出回覆') =>
    u.step(name, `按「${text}」`, () => u.inPage(text === '送出' ? `__mu.submitReply('.modal.show');` : `__mu.click(${J(text)}, '.modal.show');`), `return __mu.text('main', 200);`);

/** 我的交辦清單：勾一張單 → 回覆選取的單 → 填好，不送出 */
async function prepareOrderReplyFromList(u, orderId, status, note) {
    return u.step('從清單準備整單回覆', `勾選 #${orderId} → 回覆選取的單 → 選「${status}」`, async () => {
        await u.inPage(`__mu.checkRow(${J('#' + orderId)}); __mu.click('回覆選取的單');
            await __mu.waitFor(() => __mu.modal()); __mu.pickStatus('.modal.show', ${J(status)}); __mu.typeIn('.modal.show', ${J(note)});`);
    }, `return __mu.modalText();`);
}

/** 管理員：交辦單頁開改派彈窗並選人，不送出 */
async function prepareReassign(u, orderId, toAccount) {
    await u.step(`開交辦單 #${orderId}`, `開啟 /work-orders/${orderId}`, () => u.goto(`/work-orders/${orderId}`));
    return u.step('準備改派', `改派 → 選 ${toAccount}`, () => u.inPage(`__mu.click('改派'); await __mu.waitFor(() => __mu.modal());
        await __mu.waitFor(() => document.querySelector('.modal.show select')?.options.length > 1); __mu.selectIn('.modal.show', ${J('(' + toAccount + ')')});`), `return __mu.modalText();`);
}

/** 管理員：交辦單頁開代為結案彈窗並填好，不送出 */
async function prepareAdminClose(u, orderId, statusText, reason) {
    await u.step(`開交辦單 #${orderId}`, `開啟 /work-orders/${orderId}`, () => u.goto(`/work-orders/${orderId}`));
    return u.step('準備代為結案', `代為結案 → 選「${statusText}」→ 填原因`, () => u.inPage(`__mu.click('代為結案'); await __mu.waitFor(() => __mu.modal());
        __mu.pickStatus('.modal.show', ${J(statusText)}); __mu.typeIn('.modal.show', ${J(reason)});`), `return __mu.modalText();`);
}

/** 問題查詢依問題視角：找到問題列並開交辦彈窗、選人，不送出 */
async function prepareAssign(u, issueRowText, toAccount, riskLevels = '高,中,低') {
    await u.step('開問題查詢', '依問題視角、近 7 天', () => u.goto(`/records?riskLevels=${encodeURIComponent(riskLevels)}&from=${today(-7)}&to=${today(-1)}&view=issue&pageSize=50`),
        ROW(issueRowText));
    return u.step('準備交辦', `「${issueRowText}」→ 交辦 → 選 ${toAccount}`, () => u.inPage(`__mu.clickInRow(${J(issueRowText)}, '交辦');
        await __mu.waitFor(() => __mu.modal()); await __mu.waitFor(() => document.querySelector('.modal.show select')?.options.length > 1);
        __mu.selectIn('.modal.show', ${J('(' + toAccount + ')')}); await new Promise(r => setTimeout(r, 1200));`),
        `const t = __mu.modalText() || ''; const i = t.indexOf('將交辦'); return i >= 0 ? t.slice(i, i + 160) : t.slice(0, 200);`);
}

/**
 * MU13 用：在 admin 看得到的「問題→主機」清單中，找一個 handler 也能處理、該日這個問題尚無案件也沒標過狀態的（主機, 日期）。
 * 回傳 { hostId, hostName, date, dates, issueKey }；找不到回 null。
 */
async function findHostDayForIssue(admin, handler, source, eventId) {
    const q = `source=${encodeURIComponent(source)}&eventId=${eventId}&riskLevels=${encodeURIComponent('高,中,低')}&from=${today(-7)}&to=${today(-1)}&page=1&pageSize=50`;
    const d = await get(admin, `/api/records/issue-hosts?${q}`);
    for (const row of d?.items || []) {
        for (const date of (row.dates || []).slice(0, 3)) {
            const det = await get(handler, `/api/records/${row.hostId}/${date}`);
            const it = det?.topIssues?.find(i => i.source === source && i.eventId === eventId);
            // 只挑「真的待處理」的：沒有標記時，嚴重度預設已處理的問題（isDefaultUnhandled 為 false）面板上已是已結案，標不了
            if (det?.canHandle && it && !it.caseStatus && it.isDefaultUnhandled && (!it.handlingStatus || it.handlingStatus === 'open'))
                return { hostId: row.hostId, hostName: row.hostName, date, dates: row.dates || [date], issueKey: it.issueKey };
        }
    }
    return null;
}

/** MU13 查核：（主機, 問題）的案件分布，以及逐日列與案件狀態是否對得上 */
async function hostIssueState(u, ctx, source, eventId) {
    const list = await get(u, `/api/work-orders?status=all&source=${encodeURIComponent(source)}&eventId=${eventId}&page=1&pageSize=100`);
    let active = 0, closed = 0, cancelled = 0; const parts = [];
    for (const o of list?.items || []) {
        const m = await get(u, `/api/work-orders/${o.workOrderId}/members?status=all&page=1&pageSize=50&hostName=${encodeURIComponent(ctx.hostName)}`);
        for (const x of (m?.items || []).filter(i => i.hostId === ctx.hostId && i.issueKey === ctx.issueKey)) {
            const kind = x.cancelled ? '已取消' : x.closedAt ? '已結案' : '進行中';
            if (x.cancelled) cancelled++; else if (x.closedAt) closed++; else active++;
            parts.push(`單 #${o.workOrderId}（${o.handlerName}）案件 ${x.caseId}：${kind}／${x.status}${x.daySyncPending ? '／逐日同步待補' : ''}`);
        }
    }
    return { active, closed, cancelled, parts };
}

async function hostIssueDays(u, ctx, source, eventId) {
    const rows = [];
    for (const date of [...new Set([ctx.date, ...ctx.dates])]) {
        const det = await get(u, `/api/records/${ctx.hostId}/${date}`);
        const it = det?.topIssues?.find(i => i.source === source && i.eventId === eventId);
        rows.push({ date, handling: it?.handlingStatus || '（無標記）', caseStatus: it?.caseStatus || '（無案件）', handler: it?.caseHandlerAccount || '' });
    }
    return rows;
}

async function serverCheck(run, u, label, fn) { try { run.note(label, await fn()); } catch (e) { run.note(label, '（查核失敗：' + e.message + '）'); } }

// ── 情境 ────────────────────────────────────────────────────────────────────

export const scenarios = [
{
    id: 'MU01', title: '上班尖峰：11 人同時登入、開首頁、處理人同時回覆',
    what: '所有角色同時開各自的首頁；有單的處理人同時對自己的單回覆 2 台為觀察中；主管與管理員同時重新整理',
    async run(run) {
        const accounts = [...HANDLERS, 'a-admin', 'demo-admin', 'm-manager', 'd-dev', 'o-owner'];
        const users = await Promise.all(accounts.map(a => run.user(a)));
        const by = Object.fromEntries(users.map(u => [u.account, u]));
        await run.together(...users.map(u => () => u.step('開首頁', `開啟登入落點 ${u.landing}`, () => u.goto(u.landing), KPI)));
        const handlers = HANDLERS.map(a => by[a]);
        const plans = await Promise.all(handlers.map(async u => ({ u, orders: await activeOrders(u, u.userId) })));
        const working = plans.filter(p => p.orders.length > 0);
        run.note('各處理人的進行中單', plans.map(p => `${p.u.account}：${p.orders.map(o => `#${o.id}(${o.active})`).join('、') || '無'}`).join('；'));
        await run.together(...working.map(p => () => prepareMemberReply(p.u, p.orders[0].id, 2, '觀察中', '尖峰測試：先觀察')));
        await run.together(
            ...working.map(p => () => submitModal(p.u)),
            () => by['m-manager'].step('重新整理儀表板', '開啟 /', () => by['m-manager'].goto('/'), KPI),
            () => by['a-admin'].step('開交辦總覽', '開啟 /work-orders', () => by['a-admin'].goto('/work-orders'), `return __mu.text('main', 200);`),
            () => by['demo-admin'].step('開負載看板', '開啟 /work-orders#load', () => by['demo-admin'].goto('/work-orders#load'), `return __mu.text('main', 200);`),
            () => by['d-dev'].step('開排程作業', '開啟 /runs', () => by['d-dev'].goto('/runs'), `return __mu.text('main', 200);`),
            () => by['o-owner'].step('開儀表板', '開啟 /', () => by['o-owner'].goto('/'), KPI));
        await by['m-manager'].step('開報表', '開啟 /reports', () => by['m-manager'].goto('/reports'), `return __mu.text('main', 200);`);
        await run.together(...handlers.map(u => () => u.step('回到我的交辦', `開啟 ${u.landing}`, () => u.goto(u.landing), KPI)));
        for (const p of working) await serverCheck(run, by['a-admin'], `最終狀態 #${p.orders[0].id}（${p.u.account}）`, () => orderState(by['a-admin'], p.orders[0].id));
    }
},
{
    id: 'MU03', title: '兩位管理員同時把同一問題交辦給不同人',
    what: 'a-admin 交辦 Application Error 給 h-chen、demo-admin 交辦同一問題給 h-lin，同一瞬間按建立',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        await run.together(() => prepareAssign(a, 'Application Error EventId 1000', 'h-chen'), () => prepareAssign(b, 'Application Error EventId 1000', 'h-lin'));
        await run.together(() => submitModal(a, '建立交辦單', '建立（a-admin→h-chen）'), () => submitModal(b, '建立交辦單', '建立（demo-admin→h-lin）'));
        await serverCheck(run, a, '這個問題的進行中單', async () => {
            const d = await get(a, '/api/work-orders?status=active&source=Application%20Error&eventId=1000&page=1&pageSize=50');
            return (d?.items || []).map(o => `#${o.workOrderId} ${o.handlerName} 進行中 ${o.counts.active}／總數 ${o.counts.total}`).join('；') || '沒有單';
        });
    }
},
{
    id: 'MU02', title: '兩位管理員同時「立即派工」',
    what: '兩人都在待派頁按立即派工、停在確認框，同一瞬間按確定',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        const before = await get(a, '/api/work-orders?status=active&page=1&pageSize=100');
        run.note('派工前進行中單數', before?.total);
        // 立即派工先呼叫預覽：有可派的項目才開確認框；沒有時只顯示「沒有需要派的項目」，不開確認框（正確行為，記錄後不送出）
        const opened = {};
        const prep = u => u.step('待派頁按立即派工', '開啟 /work-orders#gaps → 立即派工（有可派項目時停在確認框）', async () => {
            await u.goto('/work-orders#gaps');
            opened[u.account] = await u.inPage(`await __mu.waitFor(() => __mu.find('立即派工')); __mu.click('立即派工');
                const end = Date.now() + 15000;
                while (Date.now() < end) { if (__mu.modal()) return true; if (/沒有需要派的項目/.test(document.body.innerText)) return false; await new Promise(r => setTimeout(r, 100)); }
                throw new Error('等不到確認框或「沒有需要派的項目」');`);
        }, `return __mu.modal() ? __mu.modalText() : __mu.text('main', 200);`);
        await run.together(() => prep(a), () => prep(b));
        run.note('預覽結果', `a-admin ${opened[a.account] ? '開確認框' : '沒有可派項目'}；demo-admin ${opened[b.account] ? '開確認框' : '沒有可派項目'}`);
        const confirmIfOpened = (u, name) => opened[u.account] ? submitModal(u, '確定派工', name) : Promise.resolve();
        await run.together(() => confirmIfOpened(a, '確定派工（a-admin）'), () => confirmIfOpened(b, '確定派工（demo-admin）'));
        await serverCheck(run, a, '派工後：同處理人同問題的進行中單', async () => {
            const d = await get(a, '/api/work-orders?status=active&page=1&pageSize=100');
            const key = o => `${o.handlerName}｜${o.issueLabel}`;
            const groups = {}; for (const o of d.items) (groups[key(o)] ||= []).push(`#${o.workOrderId}(${o.counts.active})`);
            const dup = Object.entries(groups).filter(([, v]) => v.length > 1).map(([k, v]) => `${k}：${v.join('、')}`);
            return `進行中單 ${d.total} 張；同人同問題多張：${dup.join('；') || '無'}；全部：${Object.entries(groups).map(([k, v]) => `${k} ${v.join('')}`).join('；')}`;
        });
    }
},
{
    id: 'MU04', title: '處理人回覆時單被改派',
    what: 'h-wu 填好整單回覆未送；a-admin 把同一張單改派給 h-chen；h-wu 再按送出',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-wu'), run.user('a-admin')]);
        const orders = await activeOrders(h, h.userId);
        const o = orders.find(x => x.active <= 25) || orders[0];
        await prepareMemberReply(h, o.id, 3, '已處理', '已重啟服務');
        await prepareReassign(a, o.id, 'h-chen');
        await submitModal(a, '確定改派', '管理員確定改派');
        await submitModal(h, '送出', '處理人送出（單已被改派）');
        await h.step('處理人看草稿是否還在', '檢查瀏覽器草稿', async () => { }, `return Object.keys(localStorage).filter(k => k.includes('draft')).join(',') || '（沒有草稿）';`);
        await h.step('處理人回到我的交辦', `開啟 ${h.landing}`, () => h.goto(h.landing), ROW('#' + o.id));
        await serverCheck(run, a, `最終狀態 #${o.id}`, () => orderState(a, o.id));
    }
},
{
    id: 'MU05', title: '處理人部分回覆與管理員代為結案同時',
    what: 'h-wu 對 5 台回「已處理」、a-admin 對同一張單代為結案「不處理」，同一瞬間送出',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-wu'), run.user('a-admin')]);
        const orders = await activeOrders(h, h.userId);
        const o = orders.find(x => x.active >= 10 && x.active <= 25) || orders[0];
        await run.together(() => prepareMemberReply(h, o.id, 5, '已處理', '這 5 台已修好'), () => prepareAdminClose(a, o.id, '不處理', '評估後不處理'));
        await run.together(() => submitModal(h, '送出', '處理人送出'), () => submitModal(a, '代為結案', '管理員代為結案'));
        await serverCheck(run, a, `最終狀態 #${o.id}`, () => orderState(a, o.id));
        await serverCheck(run, a, `歷程 #${o.id}`, async () => (await get(a, `/api/work-orders/${o.id}/timeline`) || []).map(e => `${e.action}${e.memberDelta ? `(${e.memberDelta})` : ''}`).join(' → '));
        await h.step('處理人重新整理交辦單', `開啟 /work-orders/${o.id}`, () => h.goto(`/work-orders/${o.id}`), `return __mu.text('main', 220);`);
    }
},
{
    id: 'MU07', title: '兩位管理員改授權矩陣同一列的不同格',
    what: '同一瞬間各勾一格；另測「一人的頁面開著沒重新整理，另一人先改，再換他改」',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        const open = u => u.step('開授權矩陣', '開啟 /admin/groups#access', async () => { await u.goto('/admin/groups#access'); await u.inPage(`await __mu.waitFor(() => __mu.row('ug-h-chen'));`); });
        const toggle = (u, col) => u.inPage(`const t = __mu.row('ug-h-chen').closest('table'); const i = [...t.querySelectorAll('thead th')].findIndex(h => { const t = h.innerText.trim(); return t === ${J(col)} || (t.startsWith(${J(col)}) && !/^\\d/.test(t.slice(${J(col)}.length))); });
            if (i < 0) throw new Error('找不到欄 ${col}'); __mu.row('ug-h-chen').children[i].querySelector('input').click();`);
        const granted = async () => { const m = await get(a, '/api/admin/access'); const names = Object.fromEntries(m.hostGroups.map(g => [g.groupId, g.groupName]));
            return m.userGroups.find(g => g.userGroupName === 'ug-h-chen').grantedHostGroupIds.map(id => names[id]).sort().join('、'); };
        await run.together(() => open(a), () => open(b));
        run.note('開始時 ug-h-chen 的授權', await granted());
        await run.together(
            () => a.step('a-admin 勾 機房18', '勾選 ug-h-chen × 機房18', () => toggle(a, '機房18')),
            () => b.step('demo-admin 勾 機房19', '勾選 ug-h-chen × 機房19', () => toggle(b, '機房19')));
        run.note('同時各勾一格後（預期多出 機房18、機房19）', await granted());
        await b.step('demo-admin 再勾 機房20（a-admin 的頁面沒有重新整理）', '勾選 ug-h-chen × 機房20', () => toggle(b, '機房20'));
        run.note('demo-admin 勾 機房20 後', await granted());
        await a.step('a-admin 在舊畫面勾 機房17', '勾選 ug-h-chen × 機房17（畫面是 demo-admin 改動前載入的）', () => toggle(a, '機房17'),
            `const r = __mu.row('ug-h-chen'); const t = r.closest('table'); const hs = [...t.querySelectorAll('thead th')].map(h => h.innerText.trim());
             return '畫面上打勾的：' + [...r.children].map((c, i) => c.querySelector('input:checked') ? hs[i] : null).filter(Boolean).join('、');`);
        run.note('a-admin 在舊畫面再勾一格後（預期保有 機房20）', await granted());
    }
},
{
    id: 'MU08', title: '兩位管理員同時儲存設定',
    what: 'a-admin 改「伺服器描述」、demo-admin 改「體檢間隔」，同一瞬間儲存；再測一人在舊畫面儲存',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        const open = u => u.step('開設定頁', '開啟 /admin/settings', async () => { await u.goto('/admin/settings'); await u.inPage(`await __mu.waitFor(() => document.getElementById('server-description'));`); });
        const read = async () => { const s = await get(a, '/api/admin/settings'); return `伺服器描述=${s.serverDescription ?? s.analysis?.serverDescription}；體檢間隔=${s.checkupIntervalDays ?? s.analysis?.checkupIntervalDays}；revision=${s.revision}`; };
        await run.together(() => open(a), () => open(b));
        run.note('開始時', await read());
        await a.step('a-admin 改伺服器描述', '分析參數 → 伺服器描述', () => a.inPage(`__mu.setValue(document.getElementById('ai-retry-count'), '1'); __mu.click('分析參數'); __mu.setValue(document.getElementById('server-description'), '同時測試-A');`));
        await b.step('demo-admin 改體檢間隔', '分析參數 → 體檢間隔 9', () => b.inPage(`__mu.setValue(document.getElementById('ai-retry-count'), '1'); __mu.click('分析參數'); __mu.setValue(document.getElementById('checkup-interval-days'), '9');`));
        const save = u => u.step(`${u.account} 儲存`, '按「儲存」', () => u.inPage(`__mu.click('儲存'); await new Promise(r => setTimeout(r, 800)); const m = __mu.modal(); if (m) { const ok = __mu.find('確定', m) || __mu.find('確認', m); }`), `return __mu.modalText() || __mu.text('main', 100);`);
        await run.together(() => save(a), () => save(b));
        run.note('同時儲存後（預期兩個欄位都是新值，或輸的一方被明確告知）', await read());
        const loser = [a, b];
        for (const u of loser) await u.step(`${u.account} 儲存後的畫面`, '不操作', async () => { }, `return (__mu.modalText() || '') + '｜' + [...document.querySelectorAll('.invalid-feedback, .alert, [role=alert], [role=status]')].filter(e => e.offsetParent).map(e => e.innerText.trim()).filter(Boolean).join(' / ').slice(0, 300);`);
    }
},
{
    id: 'MU09', title: '處理人操作中被停用',
    what: 'h-lin 開著自己的交辦單並填好回覆；a-admin 停用 h-lin；h-lin 按送出，再點別頁',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-lin'), run.user('a-admin')]);
        const orders = await activeOrders(h, h.userId);
        if (orders.length === 0) { run.note('前置不足', 'h-lin 沒有進行中單，略過'); return; }
        await prepareMemberReply(h, orders[0].id, 2, '已處理', '已修復');
        await a.step('管理員停用 h-lin', '使用者頁 → 編輯 h-lin → 取消啟用 → 儲存', async () => {
            await a.goto('/admin/users');
            await a.inPage(`await __mu.waitFor(() => __mu.row('h-lin')); __mu.clickInRow('h-lin', '編輯'); await __mu.waitFor(() => __mu.modal());
                const c = [...document.querySelectorAll('.modal.show input[type=checkbox]')].find(x => (x.labels?.[0]?.innerText || '').includes('啟用')); if (c.checked) c.click();
                __mu.click('儲存', '.modal.show');`);
        });
        await submitModal(h, '送出', '被停用的處理人送出回覆');
        await h.step('被停用的處理人開我的交辦', `開啟 ${h.landing}`, () => h.goto(h.landing), `return location.pathname + '｜' + __mu.text('body', 160);`);
        await serverCheck(run, a, `最終狀態 #${orders[0].id}`, () => orderState(a, orders[0].id));
        await a.step('管理員重新啟用 h-lin（還原）', '使用者頁 → 編輯 h-lin → 勾啟用 → 儲存', async () => {
            await a.goto('/admin/users');
            // 使用者清單預設只列啟用中的帳號：剛停用的 h-lin 要先把狀態篩選切到「全部」才看得到
            await a.inPage(`await __mu.waitFor(() => __mu.find('全部')); __mu.click('全部');
                await __mu.waitFor(() => __mu.row('h-lin')); __mu.clickInRow('h-lin', '編輯'); await __mu.waitFor(() => __mu.modal());
                const c = [...document.querySelectorAll('.modal.show input[type=checkbox]')].find(x => (x.labels?.[0]?.innerText || '').includes('啟用')); if (!c.checked) c.click();
                __mu.click('儲存', '.modal.show');`);
        });
    }
},
{
    id: 'MU11', title: '過期畫面：清單開著時單被管理員結案',
    what: 'h-wu 的我的交辦清單開著不動；a-admin 把其中一張單代為結案；h-wu 在舊清單上回覆那張單',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-wu'), run.user('a-admin')]);
        await h.step('處理人開我的交辦', `開啟 ${h.landing}`, () => h.goto(h.landing), KPI);
        const orders = await activeOrders(h, h.userId);
        const o = orders[orders.length - 1];
        await prepareAdminClose(a, o.id, '已處理', '管理員已自行處理');
        await submitModal(a, '代為結案', '管理員代為結案');
        await h.step('處理人看舊清單', '不重新整理', async () => { }, ROW('#' + o.id));
        await prepareOrderReplyFromList(h, o.id, '已處理', '我也處理好了');
        await submitModal(h, '送出', '處理人在舊清單送出');
        await h.step('處理人之後的清單', '送出後畫面', async () => { }, ROW('#' + o.id));
        await serverCheck(run, a, `最終狀態 #${o.id}`, () => orderState(a, o.id));
    }
},
{
    id: 'MU12', title: '同一帳號兩個工作階段同時回覆',
    what: 'h-wu 開兩個瀏覽器；兩邊對同一張單的前 3 台各回不同狀態，同一瞬間送出',
    async run(run) {
        const [h1, h2, a] = await Promise.all([run.user('h-wu', 'h-wu（視窗 1）'), run.user('h-wu', 'h-wu（視窗 2）'), run.user('a-admin')]);
        const orders = await activeOrders(h1, h1.userId);
        const o = orders[0];
        await run.together(() => prepareMemberReply(h1, o.id, 3, '已處理', '視窗 1：已處理'), () => prepareMemberReply(h2, o.id, 3, '觀察中', '視窗 2：觀察'));
        await run.together(() => submitModal(h1, '送出', '視窗 1 送出'), () => submitModal(h2, '送出', '視窗 2 送出'));
        await serverCheck(run, a, `最終狀態 #${o.id}`, () => orderState(a, o.id));
        await serverCheck(run, a, `歷程 #${o.id}（最後 4 筆）`, async () => (await get(a, `/api/work-orders/${o.id}/timeline`) || []).slice(0, 4).map(e => `${e.action}：${e.note || ''}`).join(' ｜ '));
        await h2.step('視窗 2 重新整理', `開啟 /work-orders/${o.id}`, () => h2.goto(`/work-orders/${o.id}`), `return __mu.text('main', 200);`);
    }
},
{
    id: 'MU10', title: '處理人操作中失去主機授權',
    what: 'h-wang 開著問題查詢；a-admin 取消他群組的全部授權；h-wang 繼續點',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-wang'), run.user('a-admin')]);
        await h.step('處理人開問題查詢', '依主機視角', () => h.goto(`/records?riskLevels=${encodeURIComponent('高,中')}&from=${today(-7)}&to=${today(-1)}&view=host`), `return __mu.text('main table', 160);`);
        const m = await get(a, '/api/admin/access');
        const g = m.userGroups.find(x => x.userGroupName === 'ug-h-wang');
        run.note('h-wang 原本的授權群組數', g.grantedHostGroupIds.length);
        await a.step('管理員取消 h-wang 的全部授權', 'PUT /api/admin/access（等同授權矩陣全部取消）', async () => { const r = await api(a, 'PUT', `/api/admin/access/${g.userGroupId}`, { grant: [], revoke: g.grantedHostGroupIds }); if (r.status !== 200) throw new Error('HTTP ' + r.status); });
        await h.step('處理人點第一台主機', '在舊清單上點主機', async () => {
            // 讀出舊清單上的連結再以 goto 前往：在頁內 click 會換頁，正在執行的頁內腳本會被中斷（不是產品錯誤）
            const href = await h.inPage(`const l = document.querySelector('main tbody a'); if (!l) throw new Error('清單沒有連結'); return l.getAttribute('href');`);
            await h.goto(href);
            await h.inPage(`await new Promise(r => setTimeout(r, 1500));`);
        }, `return location.pathname + '｜' + __mu.text('main', 220);`);
        await h.step('處理人重新整理問題查詢', '重新開啟', () => h.goto(`/records?view=host`), `return __mu.text('main', 220);`);
        await h.step('處理人開我的交辦', `開啟 ${h.landing}`, () => h.goto(h.landing), KPI);
        await api(a, 'PUT', `/api/admin/access/${g.userGroupId}`, { grant: g.grantedHostGroupIds, revoke: [] });
        run.note('已還原 h-wang 的授權', g.grantedHostGroupIds.length);
    }
},
{
    id: 'MU14', title: '看報表時大量寫入',
    what: '有單的處理人連續三輪各回覆 2 台；主管同時連續重新整理儀表板與報表',
    async run(run) {
        const users = await Promise.all([...HANDLERS, 'm-manager', 'a-admin'].map(x => run.user(x)));
        const mgr = users.find(u => u.account === 'm-manager'), adm = users.find(u => u.account === 'a-admin');
        const plans = (await Promise.all(users.filter(u => HANDLERS.includes(u.account)).map(async u => ({ u, orders: await activeOrders(u, u.userId) })))).filter(p => p.orders.length);
        for (let round = 1; round <= 3; round++) {
            await run.together(...plans.map(p => () => prepareMemberReply(p.u, p.orders[0].id, 2, '處理中', `第 ${round} 輪更新`, `第 ${round} 輪準備`)));
            await run.together(
                ...plans.map(p => () => submitModal(p.u, '送出', `第 ${round} 輪送出`)),
                () => mgr.step(`主管儀表板（第 ${round} 輪）`, '開啟 /', () => mgr.goto('/'), KPI),
                () => adm.step(`管理員交辦總覽（第 ${round} 輪）`, '開啟 /work-orders', () => adm.goto('/work-orders')));
            await mgr.step(`主管報表（第 ${round} 輪）`, '開啟 /reports', () => mgr.goto('/reports'), `return __mu.text('main', 160);`);
        }
    }
}
,{
    id: 'MU06', title: '處理人回覆與管理員靜音（代為結案）同時',
    what: 'h-tsai 對 disk 153 的單回覆 2 台「已處理」；a-admin 同時把 disk 153 靜音並選擇代為結案',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-tsai'), run.user('a-admin')]);
        const orders = await activeOrders(h, h.userId);
        const o = orders.find(x => x.label.startsWith('disk')) || orders[0];
        await prepareMemberReply(h, o.id, 2, '已處理', '已更換磁碟');
        await a.step('開問題查詢', '依問題視角', () => a.goto(`/records?riskLevels=${encodeURIComponent('高,中')}&from=${today(-7)}&to=${today(-1)}&view=issue&pageSize=50`), ROW('disk EventId 153'));
        await a.step('準備靜音', 'disk EventId 153 → 更多 → 靜音 → 1 週、代為結案', () => a.inPage(`__mu.clickInRow('disk EventId 153', '更多'); await new Promise(r => setTimeout(r, 300));
            __mu.click('靜音', '.dropdown-menu.show'); await __mu.waitFor(() => __mu.modal()); __mu.click('1 週', '.modal.show');
            __mu.typeIn('.modal.show', '廠商更換中，暫不追蹤', 'textarea, input[type=text]'); __mu.click('代為結案', '.modal.show');`), `return __mu.modalText();`);
        await run.together(() => submitModal(h, '送出', '處理人送出'), () => submitModal(a, '靜音問題', '管理員靜音並代為結案'));
        await serverCheck(run, a, `最終狀態 #${o.id}`, () => orderState(a, o.id));
        await serverCheck(run, a, `歷程 #${o.id}`, async () => (await get(a, `/api/work-orders/${o.id}/timeline`) || []).slice(0, 5).map(e => `${e.action}${e.memberDelta ? `(${e.memberDelta})` : ''}`).join(' → '));
        await h.step('處理人回到我的交辦', `開啟 ${h.landing}`, () => h.goto(h.landing), KPI);
    }
},
{
    id: 'MU15', title: '全部角色同時操作（13 人、三輪）',
    what: '兩位管理員、主管、開發維運、六位處理人、受限使用者、主機負責人、救援帳號，各做自己角色的查詢與寫入，三輪都在同一瞬間開始',
    async run(run) {
        const accounts = [...HANDLERS, 'a-admin', 'demo-admin', 'm-manager', 'd-dev', 'o-owner', 'v-viewer', 'svc-lfadmin'];
        const users = await Promise.all(accounts.map(x => run.user(x)));
        const by = Object.fromEntries(users.map(u => [u.account, u]));
        const A = by['a-admin'], B = by['demo-admin'], M = by['m-manager'], D = by['d-dev'], O = by['o-owner'], V = by['v-viewer'], S = by['svc-lfadmin'];
        const handlers = HANDLERS.map(x => by[x]);
        const view = (u, name, path, observe = `return __mu.text('main', 150);`) => () => u.step(name, `開啟 ${path}`, () => u.goto(path), observe);
        const recordsIssue = `/records?riskLevels=${encodeURIComponent('高,中,低')}&from=${today(-7)}&to=${today(-1)}&view=issue&pageSize=50`;
        const handlerRound = async (u, round, status) => {
            const orders = await activeOrders(u, u.userId);
            if (orders.length === 0) {
                await u.step(`第 ${round} 輪：沒有單，查問題`, '開啟問題查詢（依主機）', () => u.goto(`/records?riskLevels=${encodeURIComponent('高,中')}&from=${today(-7)}&to=${today(-1)}&view=host`), `return __mu.text('main table', 120) || __mu.text('main', 120);`);
                return u.step(`第 ${round} 輪：開第一台主機`, '點清單第一台', () => u.inPage(`const l = document.querySelector('main tbody a'); if (l) l.click(); await new Promise(r => setTimeout(r, 1200));`).catch(() => { }), `return location.pathname;`);
            }
            const o = orders[orders.length - 1];
            await prepareMemberReply(u, o.id, 2, status, `全角色測試第 ${round} 輪`, `第 ${round} 輪：準備回覆 #${o.id}`);
            return submitModal(u, '送出', `第 ${round} 輪：送出 #${o.id}`);
        };

        // ── 第 1 輪 ──
        const ownerDay = await (async () => { const d = await get(O, `/api/records?riskLevels=${encodeURIComponent('高,中')}&from=${today(-7)}&to=${today(-1)}&page=1&pageSize=5`); const it = d?.items?.[0]; return it ? { hostId: it.hostId, date: String(it.date || it.recordDate).slice(0, 10) } : null; })();
        run.note('負責人要標記的風險日', ownerDay);
        await run.together(
            ...handlers.map(u => () => handlerRound(u, 1, '已處理')),
            async () => {
                let hasModal = false;
                await A.step('第 1 輪：待派頁按立即派工', '開啟 /work-orders#gaps → 立即派工（有可派項目才開確認框）', async () => {
                    await A.goto('/work-orders#gaps');
                    hasModal = await A.inPage(`await __mu.waitFor(() => __mu.find('立即派工')); __mu.click('立即派工');
                        const end = Date.now() + 15000;
                        while (Date.now() < end) { if (__mu.modal()) return true; if (/沒有需要派的項目/.test(document.body.innerText)) return false; await new Promise(r => setTimeout(r, 100)); }
                        throw new Error('等不到確認框或「沒有需要派的項目」');`);
                }, `return __mu.modal() ? __mu.modalText() : __mu.text('main', 200);`);
                return hasModal ? submitModal(A, '確定派工', '第 1 輪：確定派工') : undefined;
            },
            async () => { await prepareAssign(B, 'Application Error EventId 1000', 'h-wang'); return submitModal(B, '建立交辦單', '第 1 輪：交辦 Application Error → h-wang'); },
            view(M, '第 1 輪：主管儀表板', '/'),
            view(D, '第 1 輪：開發維運排程作業', '/runs'),
            async () => {
                if (!ownerDay) return O.step('第 1 輪：負責人沒有可標記的風險日', '', async () => { });
                await O.step('第 1 輪：負責人開風險日詳情', `開啟 /records/${ownerDay.hostId}/${ownerDay.date}`, () => O.goto(`/records/${ownerDay.hostId}/${ownerDay.date}`), `return __mu.text('main', 160);`);
                return O.step('第 1 輪：負責人標記第一個問題為已處理', '勾選問題 → 已處理 → 套用', () => O.inPage(`const c = [...document.querySelectorAll('main tbody input[type=checkbox]')].find(x => x.offsetParent && !x.disabled); if (!c) throw new Error('沒有可勾選的問題'); c.click();
                    await __mu.waitFor(() => __mu.find('已處理', '.lf-detail-side__body')); __mu.click('已處理', '.lf-detail-side__body'); await new Promise(r => setTimeout(r, 300)); __mu.click('套用到', '.lf-detail-side__body');`), `return __mu.text('main', 220);`);
            },
            view(V, '第 1 輪：受限使用者問題查詢', recordsIssue),
            view(S, '第 1 輪：救援帳號使用者頁', '/admin/users'));

        // ── 第 2 輪 ──
        const tsai = await activeOrders(A, by['h-tsai'].userId);
        await run.together(
            ...handlers.map(u => () => handlerRound(u, 2, '觀察中')),
            async () => { if (!tsai.length) return; await prepareReassign(A, tsai[tsai.length - 1].id, 'h-huang'); return submitModal(A, '確定改派', `第 2 輪：改派 #${tsai[tsai.length - 1].id} → h-huang`); },
            async () => {
                await B.step('第 2 輪：開授權矩陣', '開啟 /admin/groups#access', async () => { await B.goto('/admin/groups#access'); await B.inPage(`await __mu.waitFor(() => __mu.row('ug-v-viewer'));`); });
                return B.step('第 2 輪：授權 ug-v-viewer × 機房02', '勾選', () => B.inPage(`const t = __mu.row('ug-v-viewer').closest('table'); const i = [...t.querySelectorAll('thead th')].findIndex(h => { const x = h.innerText.trim(); return x === '機房02' || (x.startsWith('機房02') && !/^\\d/.test(x.slice(4))); }); __mu.row('ug-v-viewer').children[i].querySelector('input').click();`));
            },
            view(M, '第 2 輪：主管報表', '/reports'),
            view(D, '第 2 輪：開發維運儀表板', '/'),
            view(O, '第 2 輪：負責人儀表板', '/'),
            view(V, '第 2 輪：受限使用者權限異動', '/permission-changes'),
            view(S, '第 2 輪：救援帳號稽核紀錄', '/audit'));

        // ── 第 3 輪 ──
        const wu = await activeOrders(A, by['h-wu'].userId);
        await run.together(
            ...handlers.map(u => async () => {
                // 我的交辦預設篩選是「待我處理」（只剩觀察期內成員的單不列）；這一輪要回覆的是任何進行中的單，所以切到「進行中」
                await u.step('第 3 輪：開我的交辦', `開啟 ${u.landing}?status=active`, () => u.goto(`${u.landing}?status=active`), KPI);
                const orders = await activeOrders(u, u.userId);
                if (!orders.length) return;
                const o = orders[orders.length - 1];
                await prepareOrderReplyFromList(u, o.id, '已處理', '全角色測試第 3 輪：整單');
                return submitModal(u, '送出', `第 3 輪：整單回覆 #${o.id}`);
            }),
            async () => { if (!wu.length) return; await prepareAdminClose(A, wu[0].id, '已處理', '第 3 輪：管理員代為結案'); return submitModal(A, '代為結案', `第 3 輪：代為結案 #${wu[0].id}`); },
            view(B, '第 3 輪：管理員負載看板', '/work-orders#load'),
            view(M, '第 3 輪：主管交辦總覽', '/work-orders'),
            view(D, '第 3 輪：開發維運排程作業', '/runs'),
            view(O, '第 3 輪：負責人我的交辦', O.landing, KPI),
            view(V, '第 3 輪：受限使用者儀表板', '/', KPI),
            view(S, '第 3 輪：救援帳號設定頁', '/admin/settings'));

        // ── 收尾：每個人回到自己的首頁，查最終狀態 ──
        await run.together(...users.map(u => () => u.step('收尾：回到首頁', `開啟 ${u.landing}`, () => u.goto(u.landing), KPI)));
        await serverCheck(run, A, '最終：進行中單', async () => {
            const d = await get(A, '/api/work-orders?status=active&page=1&pageSize=100');
            const groups = {}; for (const o of d.items) (groups[`${o.handlerName}｜${o.issueLabel}`] ||= []).push(`#${o.workOrderId}(${o.counts.active})`);
            const dup = Object.entries(groups).filter(([, v]) => v.length > 1).map(([k, v]) => `${k}：${v.join('、')}`);
            return `共 ${d.total} 張；同人同問題多張：${dup.join('；') || '無'}`;
        });
        await serverCheck(run, A, '最終：負載看板', async () => { const lb = await get(A, '/api/work-orders/load-board'); return (lb.rows || lb).map(r => `${r.name} ${r.activeWorkOrders} 張 ${r.activeMembers} 台`).join('；'); });
    }
}
,{
    id: 'MU13', title: '同一問題：處理人在風險日標記與管理員交辦同時',
    what: 'h-chen 在某台主機的風險日詳情勾選 Application Error EventId 1000 並選好「已處理」與說明（不送出）；a-admin 在問題查詢依問題視角對同一問題開交辦彈窗、選 h-lin（不送出）；兩人同一瞬間送出',
    async run(run) {
        const [h, a] = await Promise.all([run.user('h-chen'), run.user('a-admin')]);
        // 依序試幾個問題，用第一個找得到「真的待處理、尚無案件也沒標過」主機日的（低嚴重度的問題預設已處理，標不了）
        const CANDIDATES = [['Application Error', 1000], ['Service Control Manager', 7034], ['NETLOGON', 5719], ['Microsoft-Windows-WHEA-Logger', 17]];
        let SRC = null, EID = null, LABEL = null, ctx = null;
        for (const [src, eid] of CANDIDATES) {
            ctx = await findHostDayForIssue(a, h, src, eid);
            if (ctx) { SRC = src; EID = eid; LABEL = `${src} EventId ${eid}`; break; }
        }
        if (!ctx) { run.note('前置不足', `候選問題中找不到 h-chen 能處理、尚無案件也沒標過狀態的（主機, 日期），略過`); return; }
        run.note('選用的主機日', `${ctx.hostName}（#${ctx.hostId}）${ctx.date}；問題 ${LABEL}；issueKey=${ctx.issueKey}；此主機出現該問題的日期：${ctx.dates.join('、')}`);
        const before = await hostIssueState(a, ctx, SRC, EID);
        run.note('送出前（主機, 問題）的案件', before.parts.join('；') || '沒有案件');

        const prepareMark = u => u.step('開風險日、勾問題、選結論', `開啟 /records/${ctx.hostId}/${ctx.date} → 勾選 ${LABEL} → 處理狀態選「已處理」→ 填說明（不送出）`, async () => {
            await u.goto(`/records/${ctx.hostId}/${ctx.date}`);
            await u.inPage(`const key = ${J(ctx.issueKey)};
                const box = await __mu.waitFor(() => [...document.querySelectorAll('main tbody input[type=checkbox]')].find(c => c.dataset.issueKey === key && c.offsetParent && !c.disabled));
                box.click();
                await __mu.waitFor(() => document.querySelector('.lf-detail-side__body .lf-handling__chips button'));
                __mu.pickStatus('.lf-detail-side__body', '已處理');
                __mu.typeIn('.lf-detail-side__body', 'MU13：h-chen 已處理', '#handling-note');`);
        }, `return __mu.text('.lf-detail-side__body', 260);`);

        await run.together(() => prepareMark(h), () => prepareAssign(a, LABEL, 'h-lin'));
        await run.together(
            () => h.step('h-chen 送出標記', '按「套用到 1 個問題」', () => h.inPage(`__mu.click('套用到', '.lf-detail-side__body');`),
                `return __mu.text('.lf-detail-side__body', 260);`),
            () => submitModal(a, '建立交辦單', 'a-admin 建立交辦單（→h-lin）'));

        await serverCheck(run, a, '（主機, 問題）送出後的案件', async () => {
            const r = await hostIssueState(a, ctx, SRC, EID);
            const flags = [];
            if (r.active > 1) flags.push('⚠ 同一（主機, 問題）有多件進行中案件');
            return `進行中 ${r.active}、已結案 ${r.closed}、已取消 ${r.cancelled}；${r.parts.join('；') || '沒有案件'}${flags.length ? '；' + flags.join('；') : ''}`;
        });
        await serverCheck(run, a, '逐日列與案件是否對得上', async () => {
            const days = await hostIssueDays(a, ctx, SRC, EID);
            const flags = days.filter(d => d.caseStatus !== '（無案件）' && /^(（無標記）|open)$/.test(d.handling)).map(d => `⚠ ${d.date} 案件 ${d.caseStatus} 卻沒有對應的逐日標記`);
            return days.map(d => `${d.date}：標記 ${d.handling}／案件 ${d.caseStatus}${d.handler ? `（${d.handler}）` : ''}`).join('；') + (flags.length ? '；' + flags.join('；') : '；沒有「處理中卻沒有逐日列」的日子');
        });
        await h.step('h-chen 重新整理風險日', `開啟 /records/${ctx.hostId}/${ctx.date}`, () => h.goto(`/records/${ctx.hostId}/${ctx.date}`),
            `const r = document.querySelector('main tbody input[data-issue-key=${J(ctx.issueKey)}]')?.closest('tr'); return r ? r.innerText.replace(/\\s+/g, ' ').slice(0, 200) : '（找不到該問題列）';`);
    }
},
{
    id: 'MU16', title: '兩位管理員同時編輯同一條規則',
    what: 'a-admin 改規則「說明」、demo-admin 改同一條規則的「白話說明」，各自開編輯視窗改好後同一瞬間按儲存；兩人都具 Maintain',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        // 選一條 Windows、啟用中、Id 不是別條 Id 的子字串（清單搜尋才點得到唯一一列），且目前內容通得過驗證的規則
        const rules = await get(a, '/api/rules') || [];
        const cands = rules.filter(r => r.platform === 'windows' && r.enabled && !r.retiredNote && !rules.some(o => o !== r && o.id.toLowerCase().includes(r.id.toLowerCase())));
        let pick = null;
        for (const r of cands.slice(0, 15)) { const v = await api(a, 'POST', '/api/rules/validate', r); if (v.body?.data?.isValid) { pick = r; break; } }
        if (!pick) { run.note('前置不足', '找不到可編輯且通過驗證的 Windows 規則，略過'); return; }
        const original = { description: pick.description, plain: pick.plainExplanation || '' };
        const MARK_A = ' [MU16-A]', MARK_B = 'MU16-B 同時測試的白話說明';
        run.note('選用的規則', `${pick.id}；說明＝「${original.description}」；白話說明＝「${original.plain || '（空）'}」`);
        const read = async () => { const r = (await get(a, '/api/rules')).find(x => x.id === pick.id); return r; };

        const open = u => u.step('開規則維護、開編輯視窗', `開啟 /admin/rules → 搜尋 ${pick.id} → 按該列「編輯」`, async () => {
            await u.goto('/admin/rules');
            await u.inPage(`await __mu.waitFor(() => document.getElementById('rule-search'));
                __mu.setValue(document.getElementById('rule-search'), ${J(pick.id)});
                const row = await __mu.waitFor(() => [...document.querySelectorAll('#rule-list tbody tr')].filter(r => r.offsetParent).find(r => [...r.querySelectorAll('.lf-text-data')].some(e => e.innerText.trim() === ${J(pick.id)})));
                const btn = row.querySelector('button[title="編輯"]'); if (!btn) throw new Error('找不到編輯鈕'); btn.click();
                await __mu.waitFor(() => __mu.modal() && document.getElementById('rule-id').value === ${J(pick.id)});`);
        }, `return __mu.modalText();`);
        await run.together(() => open(a), () => open(b));

        await run.together(
            () => a.step('a-admin 改說明', `說明欄尾端加「${MARK_A}」`, () => a.inPage(`const el = document.getElementById('rule-description'); __mu.setValue(el, el.value + ${J(MARK_A)});`)),
            () => b.step('demo-admin 改白話說明', `白話說明改為「${MARK_B}」`, () => b.inPage(`__mu.setValue(document.getElementById('rule-plain'), ${J(MARK_B)});`)));
        const save = u => u.step(`${u.account} 儲存`, '按「儲存」', () => u.inPage(`__mu.click('儲存', '.modal.show'); await new Promise(r => setTimeout(r, 1500));`),
            `return (__mu.modalText() || '（視窗已關）').slice(0, 200);`);
        await run.together(() => save(a), () => save(b));
        const after = await read();
        run.note('同時儲存後的規則內容（只記錄，不斷言）',
            `說明＝「${after.description}」（${after.description.includes(MARK_A.trim()) ? '含 A 的修改' : '沒有 A 的修改'}）；白話說明＝「${after.plainExplanation || '（空）'}」（${(after.plainExplanation || '').includes(MARK_B) ? '含 B 的修改' : '沒有 B 的修改'}）`);
        for (const u of [a, b]) await u.step(`${u.account} 儲存後的畫面`, '不操作', async () => { }, `return (__mu.modalText() || '（視窗已關）') + '｜' + [...document.querySelectorAll('.alert, [role=alert], [role=status]')].filter(e => e.offsetParent).map(e => e.innerText.trim()).filter(Boolean).join(' / ').slice(0, 200);`);
        await serverCheck(run, a, '稽核紀錄（該規則最近的異動）', async () => {
            const d = await get(a, `/api/audit?page=1&pageSize=10&targetKind=rule`);
            return (d?.items || []).filter(x => x.targetId === pick.id).slice(0, 4).map(x => `${x.action}：${x.account} ${x.summary}`).join(' ｜ ') || '（讀不到或沒有紀錄）';
        });
        try { const latest = await read(); await api(a, 'POST', '/api/rules', { ...latest, description: original.description, plainExplanation: original.plain }); run.note('已還原規則', pick.id); } catch (e) { run.note('還原規則失敗', e.message); }
    }
},
{
    id: 'MU17', title: '兩位管理員同時編輯同一位使用者',
    what: 'a-admin 改 v-viewer 的顯示名稱、demo-admin 在同一位使用者勾選一個多出來的群組，各自開編輯視窗改好後同一瞬間按儲存（畫面儲存時會依序送出「基本資料」與「群組」兩個請求）',
    async run(run) {
        const [a, b] = await Promise.all([run.user('a-admin'), run.user('demo-admin')]);
        const TARGET = 'v-viewer', NAME_MARK = '（MU17-A）';
        const users = await get(a, '/api/admin/users') || [];
        const me = users.find(u => u.account === TARGET);
        const groups = await get(a, '/api/admin/groups') || [];
        const extra = groups.find(g => !me?.groupIds.includes(g.groupId) && g.active !== false && /^manager$/i.test(g.role)) || groups.find(g => !me?.groupIds.includes(g.groupId) && !/^admin$/i.test(g.role));
        if (!me || !extra) { run.note('前置不足', `找不到 ${TARGET} 或可多勾的非管理員群組，略過`); return; }
        const gname = id => groups.find(g => g.groupId === id)?.groupName || id;
        const original = { displayName: me.displayName, email: me.email, active: me.active, groupIds: me.groupIds.slice() };
        run.note('選用的使用者與群組', `${TARGET}：顯示名稱＝「${me.displayName}」；原群組＝${me.groupIds.map(gname).join('、') || '（無）'}；B 會多勾「${extra.groupName}」（${extra.role}）`);
        const read = async () => { const u = (await get(a, '/api/admin/users')).find(x => x.account === TARGET); return `顯示名稱＝「${u.displayName}」（${u.displayName.includes(NAME_MARK) ? '含 A 的修改' : '沒有 A 的修改'}）；群組＝${u.groupIds.map(gname).join('、') || '（無）'}（${u.groupIds.includes(extra.groupId) ? '含 B 勾的群組' : '沒有 B 勾的群組'}）`; };

        const open = u => u.step('開使用者清單、開編輯視窗', `開啟 /admin/users → 按 ${TARGET} 該列「編輯」`, async () => {
            await u.goto('/admin/users');
            await u.inPage(`// 其他使用者的群組欄也可能含 'v-viewer'（如 ug-v-viewer）：以帳號欄（第一格第一行）完全相符找列
                const row = await __mu.waitFor(() => [...document.querySelectorAll('#user-list tbody tr')].filter(r => r.offsetParent).find(r => (r.querySelector('td')?.innerText || '').trim().split(/\\s+/)[0] === ${J(TARGET)}));
                __mu.click('編輯', row); await __mu.waitFor(() => __mu.modal() && document.getElementById('user-account').value === ${J(TARGET)});`);
        }, `return __mu.modalText();`);
        await run.together(() => open(a), () => open(b));
        await run.together(
            () => a.step('a-admin 改顯示名稱', `顯示名稱尾端加「${NAME_MARK}」`, () => a.inPage(`const el = document.getElementById('user-display-name'); __mu.setValue(el, el.value + ${J(NAME_MARK)});`)),
            () => b.step(`demo-admin 勾「${extra.groupName}」`, `在所屬群組勾選「${extra.groupName}」`, () => b.inPage(`const c = [...document.querySelectorAll('#user-groups input[type=checkbox]')].find(x => x.value === ${J(String(extra.groupId))}); if (!c) throw new Error('找不到群組勾選框'); if (!c.checked) c.click();`)));
        const save = u => u.step(`${u.account} 儲存`, '按「儲存」', () => u.inPage(`__mu.click('儲存', '.modal.show'); await new Promise(r => setTimeout(r, 1500));`),
            `return (__mu.modalText() || '（視窗已關）').slice(0, 200);`);
        await run.together(() => save(a), () => save(b));
        run.note('同時儲存後的使用者內容（只記錄，不斷言）', await read());
        for (const u of [a, b]) await u.step(`${u.account} 儲存後的畫面`, '不操作', async () => { }, `return (__mu.modalText() || '（視窗已關）') + '｜' + (__mu.row(${J(TARGET)})?.innerText.replace(/\\s+/g, ' ').slice(0, 160) || '（清單上沒有這一列）');`);
        await a.step('a-admin 重新整理後看到的列', '重新開啟 /admin/users', () => a.goto('/admin/users'), ROW(TARGET));
        await serverCheck(run, a, '稽核紀錄（該使用者最近的異動）', async () => {
            const d = await get(a, `/api/audit?page=1&pageSize=10&targetKind=user`);
            return (d?.items || []).filter(x => String(x.targetId) === String(me.userId)).slice(0, 4).map(x => `${x.action}：${x.account} ${x.summary}`).join(' ｜ ') || '（讀不到或沒有紀錄）';
        });
        try {
            // 批次 M 起編輯既有使用者必須帶目前版本（沒帶或過期＝409）：還原前先讀回最新的 version
            const latest = (await get(a, '/api/admin/users') || []).find(x => x.account === TARGET);
            await api(a, 'POST', '/api/admin/users', { account: TARGET, displayName: original.displayName, email: original.email, active: original.active, version: latest?.version });
            await api(a, 'PUT', `/api/admin/users/${me.userId}/groups`, { groupIds: original.groupIds });
            run.note('已還原使用者', `${TARGET} 的顯示名稱與群組`);
        } catch (e) { run.note('還原使用者失敗', e.message); }
    }
}
];
