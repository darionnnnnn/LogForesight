/**
 * 主機詳情／風險時間軸（docs/WEB-SPEC.md §9.4）。
 *
 * 時間軸的每一格都可點擊進入該日詳情——這是 §8.4 下鑽規則的另一個入口。
 * 沒有紀錄的日子刻意用不同顏色：「這天沒分析」與「這天沒風險」是完全不同的意思。
 */

import { api } from '../core/api.js';
import { renderLoading } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const root = document.getElementById('host-detail');
const hostId = Number(root.dataset.hostId);
let currentDays = 30;

const LEGEND = [
    { key: 'high', label: '高風險', color: 'var(--lf-risk-high)' },
    { key: 'mid', label: '中風險', color: 'var(--lf-risk-mid)' },
    { key: 'low', label: '低風險', color: '#c8e6c9' },
    { key: 'gap', label: '涵蓋不完整', color: '#ffe0b2' },
    { key: 'none', label: '無分析紀錄', color: '#e9ecef' }
];

async function load() {
    renderLoading(document.getElementById('host-timeline'), 2);

    const detail = await api.get(`/api/host-detail/${hostId}?days=${currentDays}`);

    renderHeader(detail);
    renderTimeline(detail);
    renderCheckup(detail);
}

function renderHeader(detail) {
    const card = document.createElement('div');
    card.className = 'lf-card';

    const body = document.createElement('div');
    body.className = 'lf-card__body';

    const title = document.createElement('div');
    title.className = 'fs-5 fw-semibold mb-1';
    title.textContent = detail.hostName;
    body.appendChild(title);

    if (detail.roleDesc) {
        const role = document.createElement('div');
        role.className = 'text-muted mb-3';
        role.textContent = detail.roleDesc;
        body.appendChild(role);
    }

    const grid = document.createElement('div');
    grid.className = 'row g-3 small';

    const fields = [
        ['IP 位址', detail.ipAddress || '未設定'],
        ['所屬 Sentinel', detail.netiqServer || '本機直讀'],
        ['主機群組', detail.groupNames.length ? detail.groupNames.join('、') : '未分群'],
        ['負責人', detail.ownerNames.length ? detail.ownerNames.join('、') : '未指定'],
        ['最近回報', detail.lastReportAt ? formatDateTime(detail.lastReportAt) : '尚未回報']
    ];

    for (const [label, value] of fields) {
        const col = document.createElement('div');
        col.className = 'col-6 col-md-4 col-lg-2';

        const labelEl = document.createElement('div');
        labelEl.className = 'text-muted';
        labelEl.textContent = label;

        const valueEl = document.createElement('div');
        valueEl.className = 'fw-semibold';
        valueEl.textContent = value;

        col.append(labelEl, valueEl);
        grid.appendChild(col);
    }

    body.appendChild(grid);
    card.appendChild(body);
    document.getElementById('host-header').replaceChildren(card);
}

function renderTimeline(detail) {
    const container = document.getElementById('host-timeline');

    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap gap-1';

    for (const day of detail.timeline) {
        const cell = document.createElement(day.hasRecord ? 'a' : 'div');
        cell.className = 'rounded';
        cell.style.width = '22px';
        cell.style.height = '22px';
        cell.style.background = cellColor(day);
        cell.style.display = 'inline-block';

        if (day.hasRecord) {
            cell.href = `/records/${hostId}/${day.date}`;
            cell.title = `${day.date}｜${day.riskLevel}風險${day.headline ? '｜' + day.headline : ''}`;
        } else {
            // 這天沒有分析紀錄——可能是排程沒跑、機器關機，不是「沒問題」
            cell.title = `${day.date}｜無分析紀錄`;
        }

        wrap.appendChild(cell);
    }

    container.replaceChildren(wrap);
    renderLegend();
}

function cellColor(day) {
    if (!day.hasRecord) return '#e9ecef';
    if (day.riskLevel === '高') return 'var(--lf-risk-high)';
    if (day.riskLevel === '中') return 'var(--lf-risk-mid)';
    if (day.hasCoverageGap) return '#ffe0b2';
    return '#c8e6c9';
}

function renderLegend() {
    const legend = document.getElementById('timeline-legend');
    legend.replaceChildren();

    for (const item of LEGEND) {
        const wrap = document.createElement('span');
        wrap.className = 'd-flex align-items-center gap-1';

        const swatch = document.createElement('span');
        swatch.className = 'rounded d-inline-block';
        swatch.style.width = '12px';
        swatch.style.height = '12px';
        swatch.style.background = item.color;

        const label = document.createElement('span');
        label.textContent = item.label;

        wrap.append(swatch, label);
        legend.appendChild(wrap);
    }
}

function renderCheckup(detail) {
    if (!detail.latestCheckup) return;

    document.getElementById('checkup-card').classList.remove('d-none');
    const container = document.getElementById('host-checkup');

    const date = document.createElement('div');
    date.className = 'text-muted small mb-2';
    date.textContent = `${detail.latestCheckup.checkupDate}` +
        (detail.latestCheckup.hasFindings ? '' : '（本期無累積性異常）');

    const conclusion = document.createElement('div');
    conclusion.textContent = detail.latestCheckup.conclusion;

    container.replaceChildren(date, conclusion);
}

for (const button of document.querySelectorAll('[data-days]')) {
    button.addEventListener('click', () => {
        currentDays = Number(button.dataset.days);
        for (const other of document.querySelectorAll('[data-days]')) {
            other.classList.toggle('active', other === button);
        }
        load();
    });
}

load();
