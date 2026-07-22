/**
 * 風險日詳情（docs/WEB-SPEC.md §9.3）。
 *
 * 兩層呈現（DB-PLAN 定案）：
 *   - 結構化層：重點問題（含趨勢註記）、關聯訊號、深入分析、資料完整性申報
 *   - 全文層：報告 txt 原樣以等寬字型呈現
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast } from '../core/ui.js';
import { riskBadge, severityBadge, formatNumber } from '../core/format.js';
import { initHandlingPanel } from './handling-panel.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

const root = document.getElementById('record-detail');
const hostId = Number(root.dataset.hostId);
const date = root.dataset.date;

async function load() {
    renderLoading(document.getElementById('detail-issues'), 5);

    const detail = await api.get(`/api/records/${hostId}/${date}`);

    renderHeader(detail);
    renderIssues(detail);
    renderAlerts(detail);
    renderCategories(detail);
    renderCoverage(detail);
    renderDeepDives(detail);

    await initHandlingPanel(hostId, date);

    if (detail.hasReport) await loadReport();
}

function renderHeader(detail) {
    const container = document.getElementById('detail-header');

    const card = document.createElement('div');
    card.className = 'lf-card';
    if (detail.riskLevel === '高') card.classList.add('lf-card--critical');
    else if (detail.riskLevel === '中') card.classList.add('lf-card--warning');

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const top = document.createElement('div');
    top.className = 'd-flex align-items-center gap-3 mb-2 flex-wrap';

    const hostLink = document.createElement('a');
    hostLink.href = `/hosts/${detail.hostId}`;
    hostLink.className = 'fs-5 fw-semibold text-decoration-none';
    hostLink.textContent = detail.hostName;

    const dateSpan = document.createElement('span');
    dateSpan.className = 'text-muted';
    dateSpan.textContent = detail.date;

    top.append(hostLink, dateSpan, riskBadge(detail.riskLevel));

    if (!detail.aiAnalyzed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '統計模式（AI 未分析）';
        badge.title = 'AI 未呼叫或呼叫失敗，規則與趨勢告警照常運作';
        top.appendChild(badge);
    }

    body.appendChild(top);

    if (detail.headline) {
        const headline = document.createElement('div');
        headline.className = 'fs-5 mb-2';
        headline.textContent = detail.headline;
        body.appendChild(headline);
    }

    for (const [label, text] of [['狀況', detail.summary], ['趨勢', detail.trendAssessment], ['建議處置', detail.action]]) {
        if (!text) continue;
        const p = document.createElement('p');
        p.className = 'mb-2';
        const strong = document.createElement('strong');
        strong.textContent = `${label}：`;
        p.append(strong, document.createTextNode(text));
        body.appendChild(p);
    }

    const stats = document.createElement('div');
    stats.className = 'd-flex gap-4 mt-3 pt-3 border-top small text-muted';
    stats.innerHTML =
        `<span>錯誤 <strong>${formatNumber(detail.errorCount)}</strong></span>` +
        `<span>警告 <strong>${formatNumber(detail.warningCount)}</strong></span>` +
        `<span>稽核事件 <strong>${formatNumber(detail.auditEventCount)}</strong></span>`;
    body.appendChild(stats);

    if (detail.hostRoleDesc) {
        const role = document.createElement('div');
        role.className = 'small text-muted mt-2';
        role.textContent = `主機角色：${detail.hostRoleDesc}`;
        body.appendChild(role);
    }

    card.appendChild(body);
    container.replaceChildren(card);
}

function renderIssues(detail) {
    renderTable(document.getElementById('detail-issues'), {
        columns: [
            { title: '來源 / Event', render: i => sourceCell(i) },
            { title: '次數', className: 'text-end', render: i => formatNumber(i.count) },
            { title: '嚴重度', render: i => severityBadge(i.severity) },
            { title: '時段', render: i => `${i.firstSeen}~${i.lastSeen}` },
            { title: '趨勢', render: i => i.trendText },
            { title: '說明', render: i => knownIssueCell(i) }
        ],
        rows: detail.topIssues,
        empty: { title: '當日沒有重點問題', hint: '沒有任何事件簽章達到列入重點的門檻。' }
    });
}

function sourceCell(issue) {
    const wrap = document.createElement('span');

    const main = document.createElement('span');
    main.textContent = `${issue.source} (${issue.eventId})`;
    wrap.appendChild(main);

    const log = document.createElement('div');
    log.className = 'small text-muted';
    log.textContent = issue.logName;
    wrap.appendChild(log);

    if (issue.suppressed) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary';
        badge.textContent = '已抑制';
        badge.title = '此規則已被本機抑制：只關掉通知與風險升級，事件仍照常紀錄';
        wrap.appendChild(badge);
    }

    return wrap;
}

function knownIssueCell(issue) {
    const wrap = document.createElement('div');

    if (issue.knownIssue) {
        const text = document.createElement('div');
        text.textContent = issue.knownIssue;
        wrap.appendChild(text);
    }

    // Security 事件的帳號/IP 彙總是入侵分析最關鍵的依據，不能折疊起來
    if (issue.keyDetails) {
        const details = document.createElement('div');
        details.className = 'small text-danger mt-1';
        details.textContent = issue.keyDetails;
        wrap.appendChild(details);
    }

    if (issue.distinctMessageCount > 1) {
        const distinct = document.createElement('div');
        distinct.className = 'small text-muted';
        distinct.textContent = `${issue.distinctMessageCount} 種相異訊息`;
        wrap.appendChild(distinct);
    }

    if (issue.sampleMessages?.length) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-link btn-sm p-0 lf-no-print';
        toggle.textContent = '範例訊息';

        const box = document.createElement('pre');
        box.className = 'report-text small mt-1 d-none';
        box.textContent = issue.sampleMessages.join('\n---\n');

        toggle.addEventListener('click', () => box.classList.toggle('d-none'));
        wrap.append(toggle, box);
    }

    return wrap;
}

/**
 * 關聯訊號與趨勢告警：這是**程式確定性比對**的結果，不是 AI 猜測。
 * console 用紅色🔗區塊呈現，Web 沿用同一套視覺語言。
 */
function renderAlerts(detail) {
    const container = document.getElementById('detail-alerts');
    container.replaceChildren();

    if (detail.correlationAlerts.length === 0 && detail.trendAlerts.length === 0) {
        renderEmpty(container, { title: '無關聯或趨勢訊號' });
        return;
    }

    if (detail.correlationAlerts.length > 0) {
        const box = document.createElement('div');
        box.className = 'alert alert-danger';

        const title = document.createElement('div');
        title.className = 'fw-semibold mb-2';
        title.textContent = '🔗 關聯訊號（程式確定性比對）';
        box.appendChild(title);

        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3 small';
        for (const alert of detail.correlationAlerts) {
            const item = document.createElement('li');
            item.textContent = alert;
            list.appendChild(item);
        }
        box.appendChild(list);
        container.appendChild(box);
    }

    if (detail.trendAlerts.length > 0) {
        const box = document.createElement('div');
        box.className = 'alert alert-warning mb-0';

        const title = document.createElement('div');
        title.className = 'fw-semibold mb-2';
        title.textContent = '頻率異常';
        box.appendChild(title);

        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3 small';
        for (const alert of detail.trendAlerts) {
            const item = document.createElement('li');
            item.textContent = alert;
            list.appendChild(item);
        }
        box.appendChild(list);
        container.appendChild(box);
    }
}

function renderCategories(detail) {
    const container = document.getElementById('detail-categories');

    if (detail.categories.length === 0) {
        renderEmpty(container, { title: '無分類資料' });
        return;
    }

    const list = document.createElement('div');
    for (const category of detail.categories) {
        const row = document.createElement('a');
        row.className = 'd-flex justify-content-between align-items-center py-2 border-bottom text-decoration-none text-body';
        // 下鑽：點類別 → 帶條件回問題查詢（§8.4）
        row.href = `/records?categories=${category.category}&from=${detail.date}&to=${detail.date}`;

        const name = document.createElement('span');
        name.textContent = CATEGORY_NAMES[category.category] ?? category.category;

        const right = document.createElement('span');
        right.className = 'd-flex align-items-center gap-2';
        right.append(severityBadge(category.maxSeverity));

        const count = document.createElement('span');
        count.className = 'text-muted small';
        count.textContent = `${category.issueCount} 項 / ${formatNumber(category.totalEvents)} 筆`;
        right.appendChild(count);

        row.append(name, right);
        list.appendChild(row);
    }

    container.replaceChildren(list);
}

/**
 * 資料涵蓋率申報。README 的核心誠實原則：
 * 「沒告警 ≠ 沒問題，是沒看」——這在 Web 上必須同樣顯眼。
 */
function renderCoverage(detail) {
    const container = document.getElementById('detail-coverage');
    container.replaceChildren();

    const hasGap = detail.dataIncomplete || detail.securityLogAvailable === false;

    if (!hasGap) {
        const ok = document.createElement('div');
        ok.className = 'text-success';
        ok.textContent = '✓ 本日資料完整，所有偵測項目皆已執行。';
        container.appendChild(ok);
        return;
    }

    const box = document.createElement('div');
    box.className = 'alert alert-warning mb-0';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = '⚠ 本日部分偵測未執行';
    box.appendChild(title);

    const notes = [];
    if (detail.dataIncomplete) notes.push('Event Log 已被系統覆蓋，本日事件資料不完整。');
    if (detail.securityLogAvailable === false) notes.push('未能讀取 Security log（權限不足）。');

    for (const note of notes.concat(detail.uncoveredChecks)) {
        const p = document.createElement('div');
        p.className = 'small';
        p.textContent = `• ${note}`;
        box.appendChild(p);
    }

    const warning = document.createElement('div');
    warning.className = 'small fw-semibold mt-2';
    warning.textContent = '本日「沒有告警」不代表沒有問題，可能只是沒有檢查到。';
    box.appendChild(warning);

    container.appendChild(box);
}

function renderDeepDives(detail) {
    if (detail.deepDives.length === 0) return;

    document.getElementById('deep-dive-card').classList.remove('d-none');
    const container = document.getElementById('detail-deepdives');
    container.replaceChildren();

    for (const dive of detail.deepDives) {
        const section = document.createElement('div');
        section.className = 'mb-3';

        const title = document.createElement('h3');
        title.className = 'h6 fw-semibold';
        title.textContent = `【${CATEGORY_NAMES[dive.category] ?? dive.category}】`;
        section.appendChild(title);

        for (const finding of dive.findings) {
            const item = document.createElement('div');
            item.className = 'border-start ps-3 mb-3';

            const problem = document.createElement('div');
            problem.className = 'fw-semibold';
            problem.textContent = finding.problem;
            item.appendChild(problem);

            if (finding.impact) {
                const impact = document.createElement('div');
                impact.className = 'small text-muted mb-1';
                impact.textContent = `影響：${finding.impact}`;
                item.appendChild(impact);
            }

            appendList(item, '可能原因', finding.likelyCauses);
            appendList(item, '處置步驟', finding.nextSteps);

            section.appendChild(item);
        }

        container.appendChild(section);
    }
}

function appendList(parent, label, items) {
    if (!items || items.length === 0) return;

    const title = document.createElement('div');
    title.className = 'small fw-semibold mt-1';
    title.textContent = label;
    parent.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'small mb-1 ps-3';
    for (const item of items) {
        const li = document.createElement('li');
        li.textContent = item;
        list.appendChild(li);
    }
    parent.appendChild(list);
}

async function loadReport() {
    const content = await api.get(`/api/records/${hostId}/${date}/report`);
    if (!content) return;

    document.getElementById('report-card').classList.remove('d-none');
    document.getElementById('detail-report').textContent = content;
}

document.getElementById('btn-copy-report').addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText(document.getElementById('detail-report').textContent);
        toast('已複製報告全文', 'success');
    } catch {
        toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
    }
});

document.getElementById('btn-print').addEventListener('click', () => window.print());

load();
