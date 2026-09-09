/**
 * PRTG 資源守門的設定欄位與「預覽／自動偵測」（docs/PRTG-SPEC.md §12）。
 *
 * 守門同時節制 NetIQ 取數與 PRTG 擷取兩路，因此設定放在「系統管理 > 設定」的
 * 資源守門頁籤，而不是 PRTG 維護頁。這個模組是那組欄位的唯一實作——
 * 抽出來是為了讓載入、送出與預覽三件事只有一份，不在兩個頁面各寫一遍。
 */

import { api } from '../core/api.js';
import { withBusy } from '../core/ui.js';

/** 讀取數字欄位，空白或非數字時回預設值。 */
function numberOr(id, fallback) {
    const el = document.getElementById(id);
    if (!el) return fallback;
    const n = Number(el.value);
    return Number.isFinite(n) ? n : fallback;
}

/** 讀取多行文字欄位，去掉空白行。 */
function collectLines(id) {
    const el = document.getElementById(id);
    if (!el) return [];
    return el.value.split('\n').map(l => l.trim()).filter(l => l.length > 0);
}

/** 把已儲存的守門設定填進畫面欄位。 */
export function loadGuardFields(settings) {
    const guardEnabled = document.getElementById('prtg-guard-enabled');
    if (guardEnabled) guardEnabled.checked = Boolean(settings.prtgResourceGuardEnabled);
    const guardCpu = document.getElementById('prtg-guard-cpu-percent');
    if (guardCpu) guardCpu.value = settings.prtgResourceGuardCpuPercent ?? 85;
    const guardMemory = document.getElementById('prtg-guard-memory-free-percent');
    if (guardMemory) guardMemory.value = settings.prtgResourceGuardMemoryFreePercent ?? 10;
    const guardCheck = document.getElementById('prtg-guard-check-seconds');
    if (guardCheck) guardCheck.value = settings.prtgResourceGuardCheckSeconds ?? 60;
    const guardPause = document.getElementById('prtg-guard-pause-minutes');
    if (guardPause) guardPause.value = settings.prtgResourceGuardPauseMinutes ?? 5;
    const guardStrikes = document.getElementById('prtg-guard-strikes');
    if (guardStrikes) guardStrikes.value = settings.prtgResourceGuardStrikes ?? 2;
    const guardMaxPause = document.getElementById('prtg-guard-max-pause-minutes');
    if (guardMaxPause) guardMaxPause.value = settings.prtgResourceGuardMaxPauseMinutes ?? 120;
    const guardSensors = document.getElementById('prtg-guard-sensor-objids');
    if (guardSensors) guardSensors.value = (settings.prtgResourceGuardSensorObjids ?? []).join('\n');
    document.getElementById('prtg-guard-preview-result')?.replaceChildren();
}

/** 收集守門欄位，併入整包設定的 payload。 */
export function collectGuardPayload() {
    return {
        prtgResourceGuardEnabled: document.getElementById('prtg-guard-enabled')?.checked ?? false,
        prtgResourceGuardCpuPercent: numberOr('prtg-guard-cpu-percent', 85),
        prtgResourceGuardMemoryFreePercent: numberOr('prtg-guard-memory-free-percent', 10),
        prtgResourceGuardCheckSeconds: numberOr('prtg-guard-check-seconds', 60),
        prtgResourceGuardPauseMinutes: numberOr('prtg-guard-pause-minutes', 5),
        prtgResourceGuardStrikes: numberOr('prtg-guard-strikes', 2),
        prtgResourceGuardMaxPauseMinutes: numberOr('prtg-guard-max-pause-minutes', 120),
        prtgResourceGuardSensorObjids: collectLines('prtg-guard-sensor-objids')
    };
}

export function bindGuardPreview() {
    const previewBtn = document.getElementById('prtg-guard-preview-btn');
    const autofillBtn = document.getElementById('prtg-guard-autofill-btn');
    const container = document.getElementById('prtg-guard-preview-result');
    if (!container) return;

    // 兩顆按鈕共用同一段渲染：差別只在「要不要忽略覆寫清單」與「要不要把結果填回輸入框」。
    // 覆寫清單非空時偵測會被短路，所以「已手填、想重抓」必須帶 forceAuto，
    // 否則只會把手填值原樣吐回來。
    const run = async (button, { forceAuto, fillTextarea }) => {
        const restore = withBusy(button, forceAuto ? '偵測中' : '查詢中');
        container.replaceChildren();

        try {
            const url = forceAuto
                ? '/api/admin/settings/prtg-resource-guard/preview?forceAuto=true'
                : '/api/admin/settings/prtg-resource-guard/preview';
            const res = await api.get(url, { silent: true });

            if (!res.success) {
                const errEl = document.createElement('div');
                errEl.className = 'text-danger small mt-2';
                errEl.textContent = res.errorMessage || '預覽失敗。';
                container.appendChild(errEl);
                return;
            }

            // 清單來源
            const sourceEl = document.createElement('div');
            sourceEl.className = 'small text-muted mb-2';
            sourceEl.textContent = res.source === 'override' ? '來源：覆寫清單' : '來源：自動偵測';
            container.appendChild(sourceEl);

            // 偵測警告逐行顯示
            if (res.warnings && res.warnings.length > 0) {
                const warnContainer = document.createElement('div');
                warnContainer.className = 'mb-2';
                for (const w of res.warnings) {
                    const wEl = document.createElement('div');
                    wEl.className = 'text-warning small';
                    wEl.textContent = `⚠ ${w}`;
                    warnContainer.appendChild(wEl);
                }
                container.appendChild(warnContainer);
            }

            // 表格：Device／Sensor／分類／狀態／目前值／說明
            const tableResp = document.createElement('div');
            tableResp.className = 'table-responsive mt-2';

            const table = document.createElement('table');
            table.className = 'table table-sm table-bordered mb-0 small';

            const thead = document.createElement('thead');
            thead.className = 'table-light';
            const headRow = document.createElement('tr');
            const headers = ['Device', 'Sensor', '分類', '狀態', '目前值', '說明'];
            for (const h of headers) {
                const th = document.createElement('th');
                th.textContent = h;
                headRow.appendChild(th);
            }
            thead.appendChild(headRow);
            table.appendChild(thead);

            const tbody = document.createElement('tbody');
            const sensors = res.sensors || [];
            if (sensors.length === 0) {
                const emptyRow = document.createElement('tr');
                const emptyTd = document.createElement('td');
                emptyTd.colSpan = 6;
                emptyTd.className = 'text-muted text-center py-2';
                emptyTd.textContent = '無受監看感測器。';
                emptyRow.appendChild(emptyTd);
                tbody.appendChild(emptyRow);
            } else {
                for (const s of sensors) {
                    const tr = document.createElement('tr');

                    // Device
                    const tdDevice = document.createElement('td');
                    tdDevice.textContent = s.device || '-';
                    tr.appendChild(tdDevice);

                    // Sensor
                    const tdSensor = document.createElement('td');
                    tdSensor.textContent = s.sensor ? `${s.sensor} (#${s.objid})` : `#${s.objid}`;
                    tr.appendChild(tdSensor);

                    // 分類
                    const tdCat = document.createElement('td');
                    tdCat.textContent = s.category || '-';
                    tr.appendChild(tdCat);

                    // 狀態
                    const tdStatus = document.createElement('td');
                    tdStatus.textContent = s.status || '-';
                    tr.appendChild(tdStatus);

                    // 目前值：有百分比就顯示 xx.x %，沒有就顯示無法判定的原因（text-muted）
                    const tdValue = document.createElement('td');
                    if (s.percentage != null) {
                        tdValue.textContent = `${Number(s.percentage).toFixed(1)} %`;
                    } else if (s.unmeasurableReason) {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = s.unmeasurableReason;
                    } else {
                        tdValue.className = 'text-muted';
                        tdValue.textContent = '-';
                    }
                    tr.appendChild(tdValue);

                    // 說明
                    const tdNote = document.createElement('td');
                    if (s.unmeasurableReason && s.percentage != null) {
                        tdNote.textContent = s.unmeasurableReason;
                    } else if (s.percentage != null) {
                        tdNote.textContent = '正常量測';
                    } else {
                        tdNote.className = 'text-muted';
                        tdNote.textContent = s.unmeasurableReason || '-';
                    }
                    tr.appendChild(tdNote);

                    tbody.appendChild(tr);
                }
            }
            table.appendChild(tbody);
            tableResp.appendChild(table);
            container.appendChild(tableResp);

            if (fillTextarea) {
                const textarea = document.getElementById('prtg-guard-sensor-objids');
                const objids = sensors.map(x => x.objid).filter(x => x != null);

                const noteEl = document.createElement('div');
                noteEl.className = 'small mt-2';

                if (objids.length === 0) {
                    // 偵測不到時**不清空**輸入框：管理者手填的清單比一次失敗的偵測可信。
                    noteEl.className = 'text-warning small mt-2';
                    noteEl.textContent = '⚠ 自動偵測沒有找到任何感測器，已保留原本的覆寫清單。';
                } else if (textarea) {
                    textarea.value = objids.join('\n');
                    noteEl.className = 'text-success small mt-2';
                    noteEl.textContent = `已填入 ${objids.length} 個 objid，尚未儲存——請按「儲存」才會生效。`;
                }

                container.appendChild(noteEl);
            }
        } catch (error) {
            const errEl = document.createElement('div');
            errEl.className = 'text-danger small mt-2';
            errEl.textContent = error?.message || '預覽失敗。';
            container.appendChild(errEl);
        } finally {
            restore();
        }
    };

    if (previewBtn) {
        previewBtn.addEventListener('click', () => run(previewBtn, { forceAuto: false, fillTextarea: false }));
    }
    if (autofillBtn) {
        autofillBtn.addEventListener('click', () => run(autofillBtn, { forceAuto: true, fillTextarea: true }));
    }
}


// ── PRTG 鏡像狀態與衝突處理 ──────────────────────────────────────────────

let conflictPage = 1;
let conflictPageSize = loadPageSize('prtg-conflicts');
