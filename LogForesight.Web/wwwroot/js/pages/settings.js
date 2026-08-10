/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy, trackUnsaved, bindTabs, icon } from '../core/ui.js';
import { formatDateTime, formatUserName, severityName, SEVERITY_ORDER } from '../core/format.js';

// 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）：目前選定的圖示 data URI。
// 不放在表單欄位裡——<input type="file"> 的值無法用程式設定，載入既有圖示時填不回去
let brandIconDataUri = '';

/** 與後端 SystemSettingsService.BrandIconMaxKb 同一個門檻（前端先擋是體驗，後端才是防線） */
const BRAND_ICON_MAX_KB = 64;

/** 「還原預設外觀」填回的副標——與 Core 的 SystemSettings.BrandSubtitle 出廠值一致 */
const BRAND_DEFAULT_SUBTITLE = '事件日誌預警';

// 未儲存提醒（docs/archive/HISTORY.md #2）：MPA 站台離開頁面前用瀏覽器原生確認攔一次。
// AD 測試帳密／測試連線按鈕不算「設定內容」——測完即丟，不該觸發離開提醒
let unsaved = null;

// 兩段式（docs/archive/HISTORY.md #5，2026-07-27 簡化自三段式）：
// 過濾機制已收斂到後端 RecordRepository 單一咽喉點，SiteHidden 對全站一致生效，沒有例外頁。
// 回饋十三輪新增項3：DefaultHidden 模式下儀表板／報表的「風險類型」卡片改為例外中的例外——
// 那兩張卡片沒有像風險日詳情頁一樣的手動展開入口，繼續套用「不過濾」會讓使用者以為
// 取消勾選沒有生效（尤其「其他」類別本來就是低嚴重度雜訊大宗，最容易被注意到）。
const DISPLAY_MODES = [
    {
        value: 'DefaultHidden', label: '預設隱藏，仍可手動開啟',
        hint: '風險日詳情頁預設只顯示勾選層級，未勾選層級的篩選鈕仍在，使用者可自行點開檢視；' +
            '問題查詢頁的下鑽與詢問 AI 下拉同樣不受影響（仍計入全部層級）。' +
            '但儀表板／報表的「風險類型」卡片沒有手動展開的入口，一律只計入上方勾選的嚴重度。'
    },
    {
        value: 'SiteHidden', label: '全站隱藏',
        hint: '未勾選層級的問題在全站一律不計入、不顯示：風險日詳情、詢問 AI 下拉、儀表板風險類型卡、' +
            '報表統計、問題查詢頁的下鑽全部同一套過濾，沒有例外頁。'
    }
];

// 日風險等級（docs/archive/FEEDBACK-3-PLAN.md #8）：中文順序對齊後端 RiskLevels.All（高/中/低，重到輕）
const DAY_RISK_LEVELS = ['高', '中', '低'];

let current = null;

async function load() {
    current = await api.get('/api/admin/settings');
    renderSeverityChecks(current.unhandledSeverities);
    renderDisplayModeButtons(current.severityDisplayMode);
    renderDayRiskLevelChecks(current.visibleDayRiskLevels);
    renderAiFields(current);
    renderAdFields(current);
    renderAnalysisFields(current);
    renderRetentionFields(current);
    renderBrandFields(current);
    renderUpdatedAt(current);
    loadBackfillStatus();   // 獨立打，失敗靜默、不阻塞其餘欄位（見函式註解）
}

// 按鈕反白樣式沿用風險日詳情頁的嚴重度篩選鈕（record-detail.js renderSeverityFilter），
// 全站「選擇嚴重度層級」的操作語彙一致
function renderSeverityChecks(selected) {
    const container = document.getElementById('severity-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const severity of SEVERITY_ORDER) {
        const btn = document.createElement('button');
        btn.type = 'button'; // 按鈕在 <form> 內，不加 type=button 會被當成 submit 誤觸儲存
        btn.className = 'btn btn-outline-secondary' + (selectedSet.has(severity) ? ' active' : '');
        btn.setAttribute('aria-pressed', String(selectedSet.has(severity)));
        btn.dataset.severity = severity;
        btn.textContent = severityName(severity);
        btn.addEventListener('click', () => {
            const active = btn.classList.toggle('active');
            btn.setAttribute('aria-pressed', String(active));
        });
        container.appendChild(btn);
    }
}

// 單選（非多選）：點擊即切到該模式，同組其餘按鈕自動取消 active
function renderDisplayModeButtons(selectedMode) {
    const container = document.getElementById('severity-display-mode');
    const hintEl = document.getElementById('severity-display-mode-hint');
    container.replaceChildren();

    const updateHint = mode => {
        hintEl.textContent = DISPLAY_MODES.find(m => m.value === mode)?.hint ?? '';
    };

    for (const mode of DISPLAY_MODES) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (mode.value === selectedMode ? ' active' : '');
        btn.dataset.mode = mode.value;
        btn.textContent = mode.label;
        btn.addEventListener('click', () => {
            container.querySelectorAll('button').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            updateHint(mode.value);
        });
        container.appendChild(btn);
    }
    updateHint(selectedMode);
}

function collectDisplayMode() {
    return document.querySelector('#severity-display-mode button.active')?.dataset.mode ?? 'DefaultHidden';
}

/**
 * 日風險等級顯示（docs/archive/FEEDBACK-3-PLAN.md #8）：與上方的問題嚴重度是不同的兩套層級，
 * 按鈕視覺沿用同一套語彙（severity-checks 的樣式），但「高」鎖定為恆選——
 * 全部隱藏會讓儀表板永遠空白，這裡直接用 disabled 擋掉互動，而不是等送出時才報錯。
 */
function renderDayRiskLevelChecks(selected) {
    const container = document.getElementById('day-risk-level-checks');
    container.replaceChildren();

    const selectedSet = new Set(selected);
    for (const level of DAY_RISK_LEVELS) {
        const locked = level === '高';
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-outline-secondary' + (selectedSet.has(level) || locked ? ' active' : '');
        btn.setAttribute('aria-pressed', String(selectedSet.has(level) || locked));
        btn.dataset.riskLevel = level;
        btn.textContent = level;

        if (locked) {
            btn.disabled = true;
            btn.title = '「高風險日」為必要顯示項目，無法取消勾選';
        } else {
            btn.addEventListener('click', () => {
                const active = btn.classList.toggle('active');
                btn.setAttribute('aria-pressed', String(active));
            });
        }
        container.appendChild(btn);
    }
}

function collectDayRiskLevels() {
    return [...document.querySelectorAll('#day-risk-level-checks button.active')]
        .map(btn => btn.dataset.riskLevel);
}

function renderAiFields(settings) {
    document.getElementById('ai-base-url').value = settings.aiBaseUrl ?? '';

    const apiKeyInput = document.getElementById('ai-api-key');
    apiKeyInput.value = '';

    const hint = document.getElementById('ai-api-key-hint');
    hint.textContent = settings.aiHasApiKey
        ? '已設定金鑰；留空儲存＝沿用既有金鑰，輸入新值才會覆蓋。'
        : '尚未設定金鑰；地端無驗證的端點可留空。';

    document.getElementById('ai-api-key-clear').checked = false;

    // AI 進階參數（§12：自 appsettings 遷入）
    setNumber('ai-timeout-seconds', settings.aiTimeoutSeconds);
    setNumber('ai-retry-count', settings.aiRetryCount);
    setNumber('ai-retry-delay-seconds', settings.aiRetryDelaySeconds);
    setNumber('ai-json-retry-count', settings.aiJsonRetryCount);
    setNumber('ai-max-tokens', settings.aiMaxTokens);
    setNumber('ai-deep-dive-max-tokens', settings.aiDeepDiveMaxTokens);
    setNumber('ai-frequency-penalty', settings.aiFrequencyPenalty);
    setNumber('ai-presence-penalty', settings.aiPresencePenalty);
    document.getElementById('ai-extra-request-fields').value = settings.aiExtraRequestFieldsJson ?? '';
}

/** 分析參數（§12：伺服器角色／體檢間隔／監控資料夾／掃描頻道／匯入上限） */
function renderAnalysisFields(settings) {
    document.getElementById('server-description').value = settings.serverDescription ?? '';
    setNumber('checkup-interval-days', settings.checkupIntervalDays);
    document.getElementById('watched-folders').value = (settings.watchedFolders ?? []).join('\n');
    document.getElementById('analysis-channels').value = (settings.analysisChannels ?? []).join('\n');
    setNumber('import-max-file-size-kb', settings.importMaxFileSizeKb);
    setNumber('import-max-rows', settings.importMaxRows);
}

function setNumber(id, value) {
    document.getElementById(id).value = value ?? '';
}

/** 一行一項、去除空白行——與後端 SystemSettingsService.NormalizeLines 對齊的寬鬆解析 */
function collectLines(id) {
    return document.getElementById(id).value
        .split('\n')
        .map(s => s.trim())
        .filter(s => s.length > 0);
}

/** docs/archive/HISTORY.md #9：AD 驗證設定——伺服器一行一台，測試帳密欄位不預填（每次都要重新輸入） */
function renderAdFields(settings) {
    document.getElementById('ad-auth-enabled').checked = settings.adAuthEnabled;
    document.getElementById('ad-servers').value = (settings.adServers ?? []).join('\n');
    document.getElementById('ad-search-base').value = settings.adSearchBase ?? '';
    document.getElementById('ad-search-filter').value = settings.adSearchFilter ?? '';

    document.getElementById('ad-test-account').value = '';
    document.getElementById('ad-test-password').value = '';
    document.getElementById('ad-test-result').replaceChildren();
}

/** 一行一台，去除空白行——與後端 SystemSettingsService.NormalizeAdServers 對齊的寬鬆解析 */
function collectAdServers() {
    return collectLines('ad-servers');
}

function renderRetentionFields(settings) {
    document.getElementById('initial-history-days').value = settings.initialHistoryDays;
    document.getElementById('retention-days').value = settings.retentionDays;
    document.getElementById('run-log-retention-days').value = settings.runLogRetentionDays;
    document.getElementById('audit-retention-days').value = settings.auditRetentionDays;
    document.getElementById('risky-event-retention-days').value = settings.riskyEventRetentionDays;
}

/**
 * 問題聚合欄背景回填狀態（回饋十三輪 A6，體檢 G2 殘餘）：/api/health/detail 的
 * backfillInProgress/backfillDone/backfillTotal 過去沒有任何維運端讀取點——
 * 回填未完成時問題排行的次數與影響範圍會偏低但看起來正常，這是唯一能發現「數字還不準」的地方。
 * **失敗完全靜默**：這是加值資訊，載不到就不顯示，不該擋住設定頁其餘欄位（同 layout.js 的
 * 工作量徽章慣例）；平常（沒有在回填）也不顯示，避免長駐一個「無事可報」的區塊佔版面。
 */
async function loadBackfillStatus() {
    const el = document.getElementById('backfill-status');
    try {
        const detail = await api.get('/api/health/detail', { silent: true });
        if (!detail.backfillInProgress) return;

        el.textContent = `問題聚合欄背景回填進行中（${detail.backfillDone} / ${detail.backfillTotal}）——` +
            '完成前，問題排行的次數與影響範圍可能偏低。';
        el.classList.remove('d-none');
    } catch {
        // 靜默：見函式註解
    }
}

/**
 * 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）：名稱、副標與圖示。
 * 圖示以 data URI 存在 brandIconDataUri 這個模組狀態裡（不是表單欄位）——
 * <input type="file"> 的值不能用程式設定，載入既有圖示時無從「填回」檔案輸入框，
 * 只能把當前值另外記著，送出時一起帶上。
 */
function renderBrandFields(settings) {
    document.getElementById('brand-name').value = settings.brandName ?? '';
    document.getElementById('brand-subtitle').value = settings.brandSubtitle ?? '';
    setBrandIcon(settings.brandIconDataUri ?? '');
    document.getElementById('brand-icon-file').value = '';
}

function setBrandIcon(dataUri) {
    brandIconDataUri = dataUri ?? '';

    const preview = document.getElementById('brand-icon-preview');
    preview.replaceChildren();

    if (brandIconDataUri) {
        const img = document.createElement('img');
        img.src = brandIconDataUri;
        img.alt = '';   // 純預覽，旁邊的欄位標籤已說明這是什麼
        preview.appendChild(img);
    } else {
        // 沒有自訂圖示時顯示內建圖示，讓預覽格永遠有東西（空框看起來像壞掉）
        preview.appendChild(icon('speedometer2'));
    }

    document.getElementById('brand-icon-hint').textContent = brandIconDataUri
        ? '已設定自訂圖示；重新選擇檔案即可更換。'
        : '目前使用內建圖示。';
}

/**
 * 圖示上傳：在瀏覽器端讀成 data URI 後隨整份設定一起送（不另開上傳端點——
 * 這是一張 64KB 以內的小圖，為它建一條 multipart 路徑不划算）。
 * 前端先擋大小與格式，後端 SystemSettingsService.ValidateBrandIcon 仍會再驗一次
 * （前端驗證是體驗，不是防線）。
 */
function bindBrandIcon() {
    const fileInput = document.getElementById('brand-icon-file');

    fileInput.addEventListener('change', () => {
        const file = fileInput.files?.[0];
        if (!file) return;

        if (!['image/png', 'image/jpeg'].includes(file.type)) {
            toast('圖示只接受 PNG 或 JPG。', 'warning');
            fileInput.value = '';
            return;
        }
        if (file.size > BRAND_ICON_MAX_KB * 1024) {
            toast(`圖示不可超過 ${BRAND_ICON_MAX_KB} KB（目前 ${Math.ceil(file.size / 1024)} KB）。`, 'warning');
            fileInput.value = '';
            return;
        }

        const reader = new FileReader();
        reader.onload = () => {
            setBrandIcon(String(reader.result));
            toast('已選擇圖示，按「儲存」後才會套用。', 'info');
        };
        reader.onerror = () => toast('讀取圖片失敗，請換一張圖片再試。', 'error');
        reader.readAsDataURL(file);
    });

    document.getElementById('brand-icon-clear').addEventListener('click', () => {
        setBrandIcon('');
        fileInput.value = '';
    });

    document.getElementById('brand-reset').addEventListener('click', () => {
        // 還原＝清空三個欄位，讓後端各自回退到出廠值（名稱空白會被回填為 LogForesight）。
        // 同 AI 進階參數的還原：只填表單、不直接落盤
        document.getElementById('brand-name').value = '';
        document.getElementById('brand-subtitle').value = BRAND_DEFAULT_SUBTITLE;
        setBrandIcon('');
        fileInput.value = '';
        toast('已還原預設外觀，按「儲存」後才會生效。', 'info');
    });
}

function renderUpdatedAt(settings) {
    const el = document.getElementById('settings-updated');
    if (!settings.updatedAt) {
        el.textContent = '尚未有人更新過（目前為系統預設值）。';
        return;
    }
    el.textContent = `最後更新：${formatDateTime(settings.updatedAt)}` +
        (settings.updatedByAccount ? `　更新者：${formatUserName(settings.updatedByDisplayName, settings.updatedByAccount)}` : '');
}

function collectSeverities() {
    return [...document.querySelectorAll('#severity-checks button.active')]
        .map(btn => btn.dataset.severity);
}

/**
 * 頁籤化後（docs/archive/FEEDBACK-5-PLAN.md §9），驗證失敗的欄位可能在非作用中頁籤——
 * 先切過去再顯示 toast，否則使用者看不到出錯的欄位在哪。#settings-tabs 位於
 * <form> 外（避免點頁籤誤觸 trackUnsaved 的未儲存提醒），用 data-panel 反查對應頁籤。
 */
function activateTabForElement(el) {
    const panelName = el?.closest('[data-panel]')?.dataset.panel;
    if (!panelName) return;
    document.querySelector(`#settings-tabs [data-tab="${panelName}"]`)?.click();
}

function bindForm() {
    const form = document.getElementById('settings-form');
    const saveButton = document.getElementById('settings-save');

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const severities = collectSeverities();
        if (severities.length === 0) {
            activateTabForElement(document.getElementById('severity-checks'));
            toast('請至少選擇一個層級。', 'warning');
            return;
        }

        const initialHistoryDays = Number(document.getElementById('initial-history-days').value);
        const retentionDays = Number(document.getElementById('retention-days').value);
        if (retentionDays < initialHistoryDays) {
            activateTabForElement(document.getElementById('retention-days'));
            toast('歷史資料保留天數不可小於首次執行回補天數。', 'warning');
            return;
        }

        const runLogRetentionDays = Number(document.getElementById('run-log-retention-days').value);
        const auditRetentionDays = Number(document.getElementById('audit-retention-days').value);
        const riskyEventRetentionDays = Number(document.getElementById('risky-event-retention-days').value);
        if (riskyEventRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('risky-event-retention-days'));
            toast('風險 log 暫存保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        const apiKey = document.getElementById('ai-api-key').value;
        const clearApiKey = document.getElementById('ai-api-key-clear').checked;

        const adAuthEnabled = document.getElementById('ad-auth-enabled').checked;
        const adServers = collectAdServers();
        if (adAuthEnabled && adServers.length === 0) {
            activateTabForElement(document.getElementById('ad-servers'));
            toast('啟用 AD 驗證時，請至少輸入一台 AD 伺服器。', 'warning');
            return;
        }

        const restore = withBusy(saveButton, '儲存中');
        try {
            current = await api.put('/api/admin/settings', {
                unhandledSeverities: severities,
                severityDisplayMode: collectDisplayMode(),
                visibleDayRiskLevels: collectDayRiskLevels(),
                aiBaseUrl: document.getElementById('ai-base-url').value.trim(),
                aiApiKey: apiKey || null,
                clearAiApiKey: clearApiKey,
                initialHistoryDays,
                retentionDays,
                runLogRetentionDays,
                auditRetentionDays,
                riskyEventRetentionDays,
                adAuthEnabled,
                adServers,
                adSearchBase: document.getElementById('ad-search-base').value.trim(),
                adSearchFilter: document.getElementById('ad-search-filter').value.trim(),
                // AI 進階參數（§12）
                aiTimeoutSeconds: Number(document.getElementById('ai-timeout-seconds').value),
                aiRetryCount: Number(document.getElementById('ai-retry-count').value),
                aiRetryDelaySeconds: Number(document.getElementById('ai-retry-delay-seconds').value),
                aiJsonRetryCount: Number(document.getElementById('ai-json-retry-count').value),
                aiMaxTokens: Number(document.getElementById('ai-max-tokens').value),
                aiDeepDiveMaxTokens: Number(document.getElementById('ai-deep-dive-max-tokens').value),
                aiFrequencyPenalty: Number(document.getElementById('ai-frequency-penalty').value),
                aiPresencePenalty: Number(document.getElementById('ai-presence-penalty').value),
                aiExtraRequestFieldsJson: document.getElementById('ai-extra-request-fields').value.trim(),
                // 分析參數（§12）
                serverDescription: document.getElementById('server-description').value.trim(),
                checkupIntervalDays: Number(document.getElementById('checkup-interval-days').value),
                watchedFolders: collectLines('watched-folders'),
                analysisChannels: collectLines('analysis-channels'),
                importMaxFileSizeKb: Number(document.getElementById('import-max-file-size-kb').value),
                importMaxRows: Number(document.getElementById('import-max-rows').value),
                // 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）
                brandName: document.getElementById('brand-name').value.trim(),
                brandSubtitle: document.getElementById('brand-subtitle').value.trim(),
                brandIconDataUri: brandIconDataUri
            });
            toast('已儲存設定', 'success');
            renderAiFields(current);
            renderAdFields(current);
            renderAnalysisFields(current);
            renderBrandFields(current);
            renderUpdatedAt(current);
            unsaved?.clear();
        } catch {
            // 錯誤訊息已由 api.js 以 toast 顯示（與其餘頁面同一套）；這裡吞掉是為了不留下
            // uncaught promise rejection 的 console 雜訊——驗證失敗是預期路徑，不是程式錯誤
        } finally {
            restore();
        }
    });
}

/**
 * AD 測試連線（docs/archive/HISTORY.md #9）：用表單目前填的值（不需先儲存）＋
 * 管理者當場輸入的帳密試 bind。這裡是管理者對自己測試，失敗訊息可以顯示細節
 * （與一般登入一律「帳號或密碼錯誤」不同）。
 */
function bindAdTest() {
    const button = document.getElementById('ad-test-btn');

    button.addEventListener('click', async () => {
        const servers = collectAdServers();
        if (servers.length === 0) {
            toast('請至少輸入一台 AD 伺服器。', 'warning');
            return;
        }

        const account = document.getElementById('ad-test-account').value.trim();
        const password = document.getElementById('ad-test-password').value;
        if (!account || !password) {
            toast('請輸入測試帳號與密碼。', 'warning');
            return;
        }

        const resultEl = document.getElementById('ad-test-result');
        const restore = withBusy(button, '測試中');
        try {
            const result = await api.post('/api/admin/settings/ad-test', {
                servers,
                searchBase: document.getElementById('ad-search-base').value.trim() || null,
                searchFilter: document.getElementById('ad-search-filter').value.trim() || null,
                account,
                password
            }, { silent: true });

            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.message;
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = error?.message || '測試連線失敗。';
        } finally {
            // 密碼欄不保留：每次測試都要求重新輸入，降低殘留在畫面上的機會
            document.getElementById('ad-test-password').value = '';
            restore();
        }
    });
}

/**
 * AI 進階參數「還原預設值」（docs/archive/FEEDBACK-10-PLAN.md §3）：出廠值由後端隨設定一起送來
 * （`aiAdvancedDefaults`，源頭是 Core 的 SystemSettings 屬性初始器），前端不硬編第二份數字。
 * 只填回欄位、不直接存檔——存檔仍走整頁單一 form 的「儲存」，未儲存提醒（trackUnsaved）
 * 因此會照常亮起，使用者反悔可以直接離開不套用。
 */
function bindAiAdvancedReset() {
    const button = document.getElementById('btn-ai-advanced-reset');

    button.addEventListener('click', () => {
        const defaults = current?.aiAdvancedDefaults;
        if (!defaults) {
            toast('尚未載入預設值，請重新整理頁面後再試。', 'warning');
            return;
        }

        setNumber('ai-timeout-seconds', defaults.timeoutSeconds);
        setNumber('ai-retry-count', defaults.retryCount);
        setNumber('ai-retry-delay-seconds', defaults.retryDelaySeconds);
        setNumber('ai-json-retry-count', defaults.jsonRetryCount);
        setNumber('ai-max-tokens', defaults.maxTokens);
        setNumber('ai-deep-dive-max-tokens', defaults.deepDiveMaxTokens);
        setNumber('ai-frequency-penalty', defaults.frequencyPenalty);
        setNumber('ai-presence-penalty', defaults.presencePenalty);
        document.getElementById('ai-extra-request-fields').value = defaults.extraRequestFieldsJson ?? '';

        // 未儲存提醒不必手動觸發：這顆按鈕在 #settings-form 內，trackUnsaved 監聽的是表單的
        // click（不只 input），按下去本身就已標記為未儲存
        toast('已填入出廠預設值，按「儲存」後才會生效。', 'info');
    });
}

bindForm();
bindAdTest();
bindAiAdvancedReset();
bindBrandIcon();
// #settings-tabs 在 <form> 外面，切頁籤的點擊不會冒泡進表單的 trackUnsaved 監聽器，
// 不需要額外排除——見 activateTabForElement 的說明
bindTabs(document.getElementById('settings-tabs'));
unsaved = trackUnsaved(document.getElementById('settings-form'), {
    excludeSelector: '#ad-test-account, #ad-test-password, #ad-test-btn, #ad-test-result'
});
load();
