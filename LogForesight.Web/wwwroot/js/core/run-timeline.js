/**
 * 執行紀錄的階段時間軸與總表執行時間（docs/WEB-SPEC.md §9.10）。
 * 三路並行，所以每個階段一條水平條，依整趟時間比例定位；顏色之外一律有文字結果。
 * 外部字串（階段備註）一律 textContent。
 */
import { TIMING_PHASE_LABEL } from './run-phases.js';

const OUTCOME_TEXT = {
    success: '成功',
    partial: '部分完成',
    failed: '失敗',
    stopped: '已停止',
    skipped: '跳過'
};

export function formatDuration(seconds) {
    if (seconds < 60) return `${seconds} 秒`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} 分 ${seconds % 60} 秒`;
    return `${Math.floor(minutes / 60)} 時 ${minutes % 60} 分`;
}

// 後端回本機時間字串（無時區），不經 Date 換算，直接取時分秒
const timeOf = iso => String(iso).slice(11, 19);
const minuteOf = iso => String(iso).slice(11, 16);
const secondsBetween = (from, to) => Math.max(0, Math.round((new Date(to) - new Date(from)) / 1000));

/** 某一路在這趟的耗時；舊紀錄、沒跑或還沒結束回 null */
export function phaseSeconds(phases, key) {
    const phase = phases?.find(p => p.key === key);
    if (!phase?.finishedAt || phase.outcome === 'skipped') return null;
    return secondsBetween(phase.startedAt, phase.finishedAt);
}

function phaseRow(phase, runStart, span, now, interrupted) {
    const open = !phase.finishedAt;
    const end = open ? now : phase.finishedAt;
    const state = open ? (interrupted ? 'interrupted' : 'running') : phase.outcome;

    const row = document.createElement('div');
    row.className = phase.key.startsWith('prtg-') ? 'lf-phase-row lf-phase-row--sub' : 'lf-phase-row';

    const name = document.createElement('div');
    name.className = 'lf-phase-name';
    name.textContent = TIMING_PHASE_LABEL[phase.key] ?? phase.key;

    const track = document.createElement('div');
    track.className = 'lf-phase-track';
    const bar = document.createElement('div');
    bar.className = `lf-phase-bar lf-phase-bar--${state}`;
    const left = span > 0 ? (secondsBetween(runStart, phase.startedAt) / span) * 100 : 0;
    const width = span > 0 ? (secondsBetween(phase.startedAt, end) / span) * 100 : 100;
    bar.style.setProperty('--lf-phase-start', `${Math.min(left, 100).toFixed(2)}%`);
    bar.style.setProperty('--lf-phase-width', `${Math.min(width, 100).toFixed(2)}%`);
    track.appendChild(bar);

    const stateText = open ? (interrupted ? '異常中斷' : '進行中') : (OUTCOME_TEXT[phase.outcome] ?? phase.outcome);
    const range = open
        ? `${timeOf(phase.startedAt)}～`
        : `${timeOf(phase.startedAt)}～${timeOf(phase.finishedAt)}（${formatDuration(secondsBetween(phase.startedAt, phase.finishedAt))}）`;
    const detail = document.createElement('div');
    detail.className = 'lf-phase-detail';
    detail.textContent = [range, stateText, phase.note].filter(Boolean).join('　');

    row.append(name, track, detail);
    return row;
}

/**
 * @param {{ phases?: Array, startedAt: string, finishedAt?: string, interrupted?: boolean }} run
 * @param {string} [now] 進行中的階段畫到這個時刻（測試可注入）
 */
export function renderPhaseTimeline(run, now = new Date().toISOString()) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-phase-timeline';

    if (!run.phases?.length) {
        wrap.textContent = '這筆紀錄沒有階段資料。';
        return wrap;
    }

    const ends = run.phases.map(p => p.finishedAt).filter(Boolean);
    const lastEnd = run.finishedAt && !run.interrupted
        ? run.finishedAt
        : (run.interrupted && ends.length > 0 ? ends.reduce((a, b) => (a > b ? a : b)) : now);
    const span = secondsBetween(run.startedAt, lastEnd);

    for (const phase of run.phases) {
        wrap.appendChild(phaseRow(phase, run.startedAt, span, lastEnd, run.interrupted === true));
    }
    return wrap;
}

/** 總表一格：起訖與總耗時；沒有任何趟「—」；只有 AI 趟或中斷趟時說清楚 */
export function dayTimingText(timing) {
    if (!timing) return '—';
    const parts = [];
    if (timing.totalSeconds != null) {
        parts.push(`${minuteOf(timing.firstStartedAt)}～${minuteOf(timing.lastFinishedAt)}（${formatDuration(timing.totalSeconds)}）`);
    }
    if (timing.interruptedCount > 0) {
        parts.push(`異常中斷 ${timing.interruptedCount} 趟（最後活動 ${minuteOf(timing.lastInterruptedActivityAt)}）`);
    }
    return parts.length > 0 ? parts.join('　') : '—';
}

/** 總表展開時的逐路明細：本機、NetIQ、PRTG、AI 各自的區間與耗時 */
export function renderDayTiming(timing) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-day-timing';
    for (const key of ['local', 'netiq', 'prtg', 'ai']) {
        const branch = timing?.branches?.find(b => b.key === key);
        const item = document.createElement('div');
        const name = document.createElement('span');
        name.className = 'lf-stat__label';
        name.textContent = `${TIMING_PHASE_LABEL[key]}　`;
        const value = document.createElement('span');
        value.textContent = branch
            ? `${minuteOf(branch.from)}～${minuteOf(branch.to)}（${formatDuration(branch.seconds)}）`
            : '—';
        item.append(name, value);
        wrap.appendChild(item);
    }
    return wrap;
}
