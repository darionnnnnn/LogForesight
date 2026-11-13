/**
 * 執行紀錄的階段時間軸與總表執行時間（docs/WEB-SPEC.md §9.10）。
 * 三路並行，所以每個階段一條水平條，依整趟時間比例定位；顏色之外一律有文字結果。
 * 外部字串（階段備註）一律 textContent。
 */
import { TIMING_PHASE_LABEL, TIMING_SUB_PHASES } from './run-phases.js';

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
const minuteOf = iso => (iso ? String(iso).slice(11, 16) : '—');
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
    row.className = TIMING_SUB_PHASES.has(phase.key) ? 'lf-phase-row lf-phase-row--sub' : 'lf-phase-row';

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

const SUB_PHASE_LABEL = {
    'prtg-sync': '結構同步',
    'prtg-triggered': '觸發式取數',
    'prtg-value-fill': '補 PRTG 數值'
};

/**
 * PRTG 進度軌的暫停與停損文字；沒有就回 null。暫停以絕對時間算剩餘秒數：
 * 狀態每 3 秒輪詢，後端送秒數的話畫面會凍結。過了放行時刻就不再顯示。
 */
export function prtgPauseLabel(status, now = new Date()) {
    if (status.prtgHaltedPending > 0) return `PRTG 已停止本趟取數，${status.prtgHaltedPending} 台待補`;
    if (!status.prtgResumeAt) return null;
    const seconds = Math.ceil((new Date(status.prtgResumeAt) - now) / 1000);
    if (seconds <= 0) return null;
    return `PRTG 回應異常，暫停中（第 ${status.prtgPausedCount} 次，約 ${seconds} 秒後重試）`;
}

const otherAiText = timing => `AI 補跑 ${timing.otherAiRuns} 趟（${formatDuration(timing.otherAiSeconds ?? 0)}）`;

/**
 * 總表一格第一行：取數與跟隨取數 AI 的起訖與總耗時；沒有任何趟「—」；
 * 只有中斷趟時寫中斷；當天只有非跟隨 AI 趟時寫「AI 補跑 N 趟（M）」，不留空白。
 */
export function dayTimingText(timing) {
    if (!timing) return '—';
    const parts = [];
    if (timing.totalSeconds != null) {
        // 同日多趟時起訖差會大於總耗時（中間有空檔），寫出趟數才對得起來
        const runs = timing.fetchRuns > 1 ? `，共 ${timing.fetchRuns} 趟` : '';
        parts.push(`${minuteOf(timing.firstStartedAt)}～${minuteOf(timing.lastFinishedAt)}（${formatDuration(timing.totalSeconds)}${runs}）`);
    }
    if (timing.interruptedCount > 0) {
        parts.push(`異常中斷 ${timing.interruptedCount} 趟（最後活動 ${minuteOf(timing.lastInterruptedActivityAt)}）`);
    }
    if (parts.length === 0 && timing.otherAiRuns > 0) parts.push(otherAiText(timing));
    return parts.length > 0 ? parts.join('　') : '—';
}

/** 總表一格第二行小字：「本機 2 秒 · NetIQ 44 秒 · PRTG 30 分 · AI 1 分 27 秒」，另有非跟隨 AI 趟時加「另有 AI 補跑 N 趟（M）」；沒有內容回空字串 */
export function dayTimingDetailText(timing) {
    if (!timing) return '';
    const parts = [];
    if (timing.totalSeconds != null) {
        for (const [key, label] of [['local', '本機'], ['netiq', 'NetIQ'], ['prtg', 'PRTG'], ['ai', 'AI']]) {
            const branch = timing.branches?.find(b => b.key === key);
            if (branch) parts.push(`${label} ${formatDuration(branch.seconds)}`);
        }
    }
    // 與展開列同一個判定：當天取數趟全是舊紀錄就講明，不管第一行算不算得出總耗時
    const allLegacy = (timing.fetchRuns ?? 0) > 0 && (timing.fetchRunsWithoutPhases ?? 0) >= timing.fetchRuns;
    if (allLegacy) parts.unshift('無階段資料（升級前紀錄）');
    // 第一行已經是「AI 補跑」時不重複
    const firstLineIsOtherAi = timing.totalSeconds == null && !(timing.interruptedCount > 0);
    if (timing.otherAiRuns > 0 && !firstLineIsOtherAi) parts.push(`另有 ${otherAiText(timing)}`);
    return parts.join(' · ');
}

const monthDayOf = iso => String(iso).slice(5, 10);

/**
 * 總表「另於 MM-DD HH:mm 補跑」：這個資料日由排程處理日以外開跑的趟（後端給最新兩趟＋其餘趟數）；沒有回空字串。
 * 補跑趟同時處理多個資料日，耗時歸不到單一列，這裡只寫日期與時刻（第 73 輪 F）。
 */
export function catchUpText(startedAt, more = 0) {
    if (!startedAt?.length) return '';
    const list = startedAt.map(iso => `${monthDayOf(iso)} ${minuteOf(iso)}`).join('、');
    return `另於 ${list}${more > 0 ? ` 等 ${startedAt.length + more} 趟補跑` : ' 補跑'}`;
}

/**
 * 總表一格：第一行文字＋第二行小字（比照 runs.js 的 withPhaseDuration，textContent 組字）。
 * options.runDate（yyyy-MM-dd）：第一行以處理日 MM-DD 開頭——總表的列是資料日，這一格是處理日當天開跑的趟；
 * options.catchUp／catchUpMore：補跑行（catchUpText）。
 */
export function renderDayTimingCell(timing, options = {}) {
    const detail = dayTimingDetailText(timing);
    // 今天還在跑且算不出總耗時：第一行寫「進行中」，不要顯示「—」
    // 只在沒有任何可顯示內容（'—'）時才換成「進行中」；已有異常中斷或 AI 補跑文字就保留，不蓋掉
    const timingText = dayTimingText(timing);
    const firstText = options.inProgress === true && timingText === '—' ? '進行中' : timingText;
    const first = options.runDate ? `${monthDayOf(options.runDate)}　${firstText}` : firstText;
    const pending = options.pendingText;
    const catchUp = catchUpText(options.catchUp, options.catchUpMore);
    if (!detail && !pending && !catchUp) return first;
    const wrap = document.createElement('div');
    const line = document.createElement('div');
    line.textContent = first;
    wrap.append(line);
    if (detail) {
        const small = document.createElement('div');
        small.className = 'lf-text-meta';
        small.textContent = detail;
        wrap.append(small);
    }
    if (pending) {
        const pendingLine = document.createElement('div');
        pendingLine.className = 'lf-text-meta';
        pendingLine.textContent = pending;
        wrap.append(pendingLine);
    }
    if (catchUp) {
        const catchUpLine = document.createElement('div');
        catchUpLine.className = 'lf-text-meta';
        catchUpLine.textContent = catchUp;
        wrap.append(catchUpLine);
    }
    return wrap;
}

/** 總表展開時的逐路明細：本機、NetIQ、PRTG、AI 各自的區間與耗時 */
export function renderDayTiming(timing) {
    const wrap = document.createElement('div');
    wrap.className = 'lf-day-timing';
    // 取數趟全部是升級前紀錄（沒有 Phases）：三條取數路說清楚，不是「沒跑」
    const allLegacy = (timing?.fetchRuns ?? 0) > 0 && (timing?.fetchRunsWithoutPhases ?? 0) >= timing.fetchRuns;
    for (const key of ['local', 'netiq', 'prtg', 'ai']) {
        const branch = timing?.branches?.find(b => b.key === key);
        const item = document.createElement('div');
        const name = document.createElement('span');
        name.className = 'lf-stat__label';
        name.textContent = `${TIMING_PHASE_LABEL[key]}　`;
        const value = document.createElement('span');
        value.textContent = branch
            ? `${minuteOf(branch.from)}～${minuteOf(branch.to)}（${formatDuration(branch.seconds)}）`
            : (allLegacy && key !== 'ai' ? '無階段資料（升級前紀錄）' : '—');
        item.append(name, value);
        wrap.appendChild(item);
        for (const sub of branch?.subPhases ?? []) {
            const subItem = document.createElement('div');
            subItem.className = 'lf-text-meta';
            subItem.textContent = `　${SUB_PHASE_LABEL[sub.key] ?? sub.key}　${minuteOf(sub.from)}～${minuteOf(sub.to)}（${formatDuration(sub.seconds)}）`;
            wrap.appendChild(subItem);
        }
    }
    if (timing?.totalSeconds != null) {
        const item = document.createElement('div');
        const name = document.createElement('span');
        name.className = 'lf-stat__label';
        name.textContent = '總計　';
        const value = document.createElement('span');
        value.textContent = `${minuteOf(timing.firstStartedAt)}～${minuteOf(timing.lastFinishedAt)}（${formatDuration(timing.totalSeconds)}）`;
        item.append(name, value);
        wrap.appendChild(item);
    }
    return wrap;
}
