/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy, trackUnsaved, bindTabs, icon, confirmAction } from '../core/ui.js';
import { formatDateTime, formatUserName, severityName, SEVERITY_ORDER } from '../core/format.js';

// 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）：目前選定的圖示 data URI。
// 不放在表單欄位裡——<input type="file"> 的值無法用程式設定，載入既有圖示時填不回去
let brandIconDataUri = '';

/** 與後端 SystemSettingsService.BrandIconMaxKb 同一個門檻（前端先擋是體驗，後端才是防線） */
const BRAND_ICON_MAX_KB = 64;

/** 「還原預設外觀」填回的副標——與 Core 的 SystemSettings.BrandSubtitle 出廠值一致 */
const BRAND_DEFAULT_SUBTITLE = '事件日誌預警';

/** 雲端 AI 提供者的資料外送申報文字（§D3） */
const CLOUD_AI_DECLARATION = '分析時最多 500 則原始 log 訊息（可能含帳號名稱、來源 IP）會傳送至第三方服務。';

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
    renderMailFields(current);
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

function updateAiProviderFields(provider) {
    const baseUrlWrap = document.getElementById('ai-base-url-wrap');
    const baseUrlLabel = document.getElementById('ai-base-url-label');
    const baseUrlInput = document.getElementById('ai-base-url');
    const baseUrlHint = document.getElementById('ai-base-url-hint');
    const modelWrap = document.getElementById('ai-model-wrap');
    const modelLabel = document.getElementById('ai-model-label');
    const modelInput = document.getElementById('ai-model');
    const modelHint = document.getElementById('ai-model-hint');
    const azureDepWrap = document.getElementById('ai-azure-deployment-wrap');
    const azureVerWrap = document.getElementById('ai-azure-api-version-wrap');
    const apiKeyLabel = document.getElementById('ai-api-key-label');
    const providerHint = document.getElementById('ai-provider-hint');

    if (provider === 'OpenAi') {
        if (providerHint) providerHint.textContent = `使用 OpenAI 官方 API 服務。${CLOUD_AI_DECLARATION}`;
        baseUrlWrap.classList.remove('d-none');
        baseUrlLabel.textContent = 'API 位址（選填）';
        baseUrlInput.placeholder = '留空使用官方端點，要走 proxy 才填';
        if (baseUrlHint) baseUrlHint.textContent = '留空使用官方端點，要走 proxy 才填。';
        modelWrap.classList.remove('d-none');
        modelLabel.textContent = '模型名稱（必填）';
        modelInput.placeholder = '例如 gpt-4o、gpt-4o-mini';
        modelHint.textContent = '例如 gpt-4o、gpt-4o-mini。';
        azureDepWrap.classList.add('d-none');
        azureVerWrap.classList.add('d-none');
        if (apiKeyLabel) apiKeyLabel.textContent = 'API 金鑰（必填）';
    } else if (provider === 'AzureOpenAi') {
        if (providerHint) providerHint.textContent = `使用 Microsoft Azure OpenAI 服務。${CLOUD_AI_DECLARATION}`;
        baseUrlWrap.classList.remove('d-none');
        baseUrlLabel.textContent = '端點位址（Endpoint，必填）';
        baseUrlInput.placeholder = 'https://your-resource.openai.azure.com/';
        if (baseUrlHint) baseUrlHint.textContent = '';
        modelWrap.classList.add('d-none');
        azureDepWrap.classList.remove('d-none');
        azureVerWrap.classList.remove('d-none');
        if (apiKeyLabel) apiKeyLabel.textContent = 'API 金鑰（必填）';
    } else {
        if (providerHint) providerHint.textContent = '使用本機或內部部署之 OpenAI 相容端點（如 llama.cpp／KoboldCpp）。';
        baseUrlWrap.classList.remove('d-none');
        baseUrlLabel.textContent = 'API 位址';
        baseUrlInput.placeholder = 'http://localhost:8080';
        if (baseUrlHint) baseUrlHint.textContent = '';
        modelWrap.classList.remove('d-none');
        modelLabel.textContent = '模型名稱';
        modelInput.placeholder = 'local-model';
        modelHint.textContent = '選填，預設 local-model。';
        azureDepWrap.classList.add('d-none');
        azureVerWrap.classList.add('d-none');
        if (apiKeyLabel) apiKeyLabel.textContent = 'API 金鑰（選填）';
    }
}

function renderAiFields(settings) {
    const provider = settings.aiProvider || 'Local';
    document.getElementById('ai-provider').value = provider;
    document.getElementById('ai-base-url').value = settings.aiBaseUrl ?? '';
    document.getElementById('ai-model').value = settings.aiModel ?? '';
    document.getElementById('ai-azure-deployment').value = settings.aiAzureDeployment ?? '';
    document.getElementById('ai-azure-api-version').value = settings.aiAzureApiVersion || '2024-10-21';
    updateAiProviderFields(provider);

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
    document.getElementById('detail-retention-days').value = settings.detailRetentionDays;
    document.getElementById('run-log-retention-days').value = settings.runLogRetentionDays;
    document.getElementById('audit-retention-days').value = settings.auditRetentionDays;
    document.getElementById('risky-event-retention-days').value = settings.riskyEventRetentionDays;
}

/** 郵件通知（docs/archive/FEEDBACK-15-PLAN.md 批次D）：密碼欄比照 AI 金鑰，不預填、只顯示是否已設定 */
function renderMailFields(settings) {
    document.getElementById('mail-enabled').checked = settings.mailEnabled;
    document.getElementById('smtp-server').value = settings.smtpServer ?? '';
    document.getElementById('smtp-port').value = settings.smtpPort ?? 25;
    document.getElementById('smtp-use-tls').checked = settings.smtpUseTls;
    document.getElementById('smtp-account').value = settings.smtpAccount ?? '';

    document.getElementById('smtp-password').value = '';
    document.getElementById('smtp-password-hint').textContent = settings.smtpHasPassword
        ? '已設定密碼；留空儲存＝沿用既有密碼，輸入新值才會覆蓋。'
        : '尚未設定密碼；不需驗證的內網 relay 可留空。';
    document.getElementById('smtp-password-clear').checked = false;

    document.getElementById('mail-from').value = settings.mailFrom ?? '';
    document.getElementById('mail-recipients').value = (settings.mailRecipients ?? []).join('\n');
    document.getElementById('mail-notify-host-owners').checked = settings.mailNotifyHostOwners;

    document.getElementById('mail-min-risk-level').value = settings.mailMinRiskLevel || '高';
    document.getElementById('mail-on-run-completed').checked = settings.mailOnRunCompleted;
    document.getElementById('mail-urgent-enabled').checked = settings.mailUrgentEnabled;
    document.getElementById('mail-daily-enabled').checked = settings.mailDailyEnabled;
    document.getElementById('mail-daily-time').value = settings.mailDailyTime || '08:00';
    document.getElementById('mail-weekly-enabled').checked = settings.mailWeeklyEnabled;
    document.getElementById('mail-weekly-day').value = settings.mailWeeklyDayOfWeek || 'Monday';
    document.getElementById('mail-weekly-time').value = settings.mailWeeklyTime || '08:00';

    document.getElementById('mail-subject-template').value = settings.mailSubjectTemplate ?? '';
    document.getElementById('mail-body-intro').value = settings.mailBodyIntro ?? '';
    document.getElementById('mail-digest-skip-empty').checked = settings.mailDigestSkipEmpty;
    document.getElementById('mail-test-result').replaceChildren();

    // 已暫停寄送的收件人（回饋十七輪批次B-1）：連續失敗達門檻的地址，通常是打錯字
    const suspended = settings.suspendedMailRecipients ?? [];
    document.getElementById('mail-suspended-recipients-wrap').classList.toggle('d-none', suspended.length === 0);
    document.getElementById('mail-suspended-recipients-list').textContent = suspended.join('、');
}

/** 一行一位、去除空白行——與後端 SystemSettingsService.NormalizeLines 對齊的寬鬆解析 */
function collectMailRecipients() {
    return collectLines('mail-recipients');
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

/**
 * 儲存成功後即時更新側欄品牌區塊（回饋十六輪批次E-3）：原本要整頁重整才看得到新外觀。
 * 空值回退規則與後端 BrandProvider.Get 對齊——名稱空白回出廠預設「LogForesight」，
 * 副標／圖示空白代表刻意不設定，對應節點直接移除／換回內建圖示。
 */
function applyBrandToSidebar(settings) {
    const mark = document.getElementById('lf-brand-mark');
    if (mark) {
        mark.replaceChildren();
        if (settings.brandIconDataUri) {
            const img = document.createElement('img');
            img.src = settings.brandIconDataUri;
            img.alt = '';
            img.className = 'lf-sidebar__brand-img';
            mark.appendChild(img);
        } else {
            mark.appendChild(icon('speedometer2'));
        }
    }

    const brandName = settings.brandName || 'LogForesight';
    const nameEl = document.getElementById('lf-brand-name');
    if (nameEl) {
        nameEl.textContent = brandName;
        nameEl.title = brandName;
    }

    let subtitleEl = document.getElementById('lf-brand-subtitle');
    if (settings.brandSubtitle) {
        if (!subtitleEl) {
            subtitleEl = document.createElement('small');
            subtitleEl.id = 'lf-brand-subtitle';
            document.querySelector('.lf-sidebar__brand-text')?.appendChild(subtitleEl);
        }
        subtitleEl.textContent = settings.brandSubtitle;
        subtitleEl.title = settings.brandSubtitle;
    } else if (subtitleEl) {
        subtitleEl.remove();
    }
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

        const detailRetentionDays = Number(document.getElementById('detail-retention-days').value);
        const runLogRetentionDays = Number(document.getElementById('run-log-retention-days').value);
        const auditRetentionDays = Number(document.getElementById('audit-retention-days').value);
        const riskyEventRetentionDays = Number(document.getElementById('risky-event-retention-days').value);
        if (riskyEventRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('risky-event-retention-days'));
            toast('風險 log 暫存保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        // **只列真的會造成刪除的設定。** 首次執行回補天數刻意不列——它決定的是
        // 「首次執行要往回補幾天」，調小不會刪掉任何既有資料。把不會發生的事寫進
        // 刪除確認，只會讓使用者學會忽略這個對話框。
        const reducedItems = [];
        if (retentionDays < current.retentionDays) {
            reducedItems.push(`歷史資料保留天數：${current.retentionDays} → ${retentionDays} 天（${retentionDays} 天以前的分析紀錄將進入刪除範圍）`);
        }
        if (detailRetentionDays < current.detailRetentionDays) {
            reducedItems.push(`詳情保留天數：${current.detailRetentionDays} → ${detailRetentionDays} 天（${detailRetentionDays} 天以前的原始樣本訊息將被清除，統計與問題清單保留）`);
        }
        if (runLogRetentionDays < current.runLogRetentionDays) {
            reducedItems.push(`執行歷程保留天數：${current.runLogRetentionDays} → ${runLogRetentionDays} 天（${runLogRetentionDays} 天以前的紀錄將進入刪除範圍）`);
        }
        if (auditRetentionDays < current.auditRetentionDays) {
            reducedItems.push(`稽核紀錄保留天數：${current.auditRetentionDays} → ${auditRetentionDays} 天（${auditRetentionDays} 天以前的紀錄將進入刪除範圍）`);
        }
        if (riskyEventRetentionDays < current.riskyEventRetentionDays) {
            reducedItems.push(`風險 log 暫存保留天數：${current.riskyEventRetentionDays} → ${riskyEventRetentionDays} 天（${riskyEventRetentionDays} 天以前的紀錄將進入刪除範圍）`);
        }

        if (reducedItems.length > 0) {
            const confirmed = await confirmAction({
                title: '確認資料保留變更',
                message: `${reducedItems.map(x => '• ' + x).join('\n')}\n\n此變更無法復原。確定要儲存？`,
                confirmText: '確定',
                confirmVariant: 'danger'
            });
            if (!confirmed) return;
        }

        const aiProvider = document.getElementById('ai-provider').value;
        const aiBaseUrl = document.getElementById('ai-base-url').value.trim();
        const aiModel = document.getElementById('ai-model').value.trim();
        const aiAzureDeployment = document.getElementById('ai-azure-deployment').value.trim();
        const aiAzureApiVersion = document.getElementById('ai-azure-api-version').value.trim();
        const apiKey = document.getElementById('ai-api-key').value;
        const clearApiKey = document.getElementById('ai-api-key-clear').checked;
        const hasApiKey = apiKey || (current?.aiHasApiKey && !clearApiKey);

        if (aiProvider === 'OpenAi') {
            if (!hasApiKey) {
                activateTabForElement(document.getElementById('ai-api-key'));
                toast('使用 OpenAI 官方 API 時，請輸入 API 金鑰。', 'warning');
                return;
            }
            if (!aiModel) {
                activateTabForElement(document.getElementById('ai-model'));
                toast('使用 OpenAI 官方 API 時，請輸入模型名稱。', 'warning');
                return;
            }
        } else if (aiProvider === 'AzureOpenAi') {
            if (!aiBaseUrl) {
                activateTabForElement(document.getElementById('ai-base-url'));
                toast('使用 Azure OpenAI 時，請輸入端點位址。', 'warning');
                return;
            }
            if (!aiAzureDeployment) {
                activateTabForElement(document.getElementById('ai-azure-deployment'));
                toast('使用 Azure OpenAI 時，請輸入部署名稱。', 'warning');
                return;
            }
            if (!aiAzureApiVersion) {
                activateTabForElement(document.getElementById('ai-azure-api-version'));
                toast('使用 Azure OpenAI 時，請輸入 API 版本。', 'warning');
                return;
            }
            if (!hasApiKey) {
                activateTabForElement(document.getElementById('ai-api-key'));
                toast('使用 Azure OpenAI 時，請輸入 API 金鑰。', 'warning');
                return;
            }
        }

        const adAuthEnabled = document.getElementById('ad-auth-enabled').checked;
        const adServers = collectAdServers();
        if (adAuthEnabled && adServers.length === 0) {
            activateTabForElement(document.getElementById('ad-servers'));
            toast('啟用 AD 驗證時，請至少輸入一台 AD 伺服器。', 'warning');
            return;
        }

        // 郵件通知（回饋十五輪批次D）：與後端 SystemSettingsService.Update 的驗證規則對齊，
        // 前端先擋一次是體驗，後端仍會再驗一次（前端驗證不是防線）
        const mailEnabled = document.getElementById('mail-enabled').checked;
        const mailRecipients = collectMailRecipients();
        const mailOnRunCompleted = document.getElementById('mail-on-run-completed').checked;
        const mailDailyEnabled = document.getElementById('mail-daily-enabled').checked;
        const mailWeeklyEnabled = document.getElementById('mail-weekly-enabled').checked;
        const mailUrgentEnabled = document.getElementById('mail-urgent-enabled').checked;
        const smtpServer = document.getElementById('smtp-server').value.trim();
        const mailFrom = document.getElementById('mail-from').value.trim();
        if (mailEnabled && (mailOnRunCompleted || mailDailyEnabled || mailWeeklyEnabled || mailUrgentEnabled)) {
            if (!smtpServer || !mailFrom || mailRecipients.length === 0) {
                activateTabForElement(document.getElementById('mail-recipients'));
                toast('已啟用郵件通知的觸發項目，請填妥 SMTP 伺服器、寄件人與至少一位收件人。', 'warning');
                return;
            }
        }

        // 切換到雲端 provider 時二次確認（由 Local 切到 OpenAi 或 AzureOpenAi）
        const previousAiProvider = current?.aiProvider || 'Local';
        if (previousAiProvider === 'Local' && (aiProvider === 'OpenAi' || aiProvider === 'AzureOpenAi')) {
            const confirmed = await confirmAction({
                title: '確認切換至雲端 AI 服務',
                message: `切換至雲端提供者後，${CLOUD_AI_DECLARATION}\n\n確定要儲存？`,
                confirmText: '確定',
                confirmVariant: 'danger'
            });
            if (!confirmed) return;
        }

        const restore = withBusy(saveButton, '儲存中');
        try {
            current = await api.put('/api/admin/settings', {
                unhandledSeverities: severities,
                severityDisplayMode: collectDisplayMode(),
                visibleDayRiskLevels: collectDayRiskLevels(),
                aiProvider,
                aiBaseUrl,
                aiModel,
                aiAzureDeployment,
                aiAzureApiVersion,
                aiApiKey: apiKey || null,
                clearAiApiKey: clearApiKey,
                initialHistoryDays,
                retentionDays,
                detailRetentionDays,
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
                // 郵件通知（回饋十五輪批次D）
                mailEnabled,
                smtpServer,
                smtpPort: Number(document.getElementById('smtp-port').value),
                smtpUseTls: document.getElementById('smtp-use-tls').checked,
                smtpAccount: document.getElementById('smtp-account').value.trim(),
                smtpPassword: document.getElementById('smtp-password').value || null,
                clearSmtpPassword: document.getElementById('smtp-password-clear').checked,
                mailFrom,
                mailRecipients,
                mailNotifyHostOwners: document.getElementById('mail-notify-host-owners').checked,
                mailMinRiskLevel: document.getElementById('mail-min-risk-level').value,
                mailOnRunCompleted,
                mailDailyEnabled,
                mailDailyTime: document.getElementById('mail-daily-time').value,
                mailWeeklyEnabled,
                mailWeeklyDayOfWeek: document.getElementById('mail-weekly-day').value,
                mailWeeklyTime: document.getElementById('mail-weekly-time').value,
                mailUrgentEnabled,
                mailSubjectTemplate: document.getElementById('mail-subject-template').value.trim(),
                mailBodyIntro: document.getElementById('mail-body-intro').value.trim(),
                mailDigestSkipEmpty: document.getElementById('mail-digest-skip-empty').checked,
                // 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）
                brandName: document.getElementById('brand-name').value.trim(),
                brandSubtitle: document.getElementById('brand-subtitle').value.trim(),
                brandIconDataUri: brandIconDataUri
            });
            toast('已儲存設定', 'success');
            renderAiFields(current);
            renderAdFields(current);
            renderAnalysisFields(current);
            renderMailFields(current);
            renderBrandFields(current);
            applyBrandToSidebar(current);
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
 * 測試寄信（docs/archive/FEEDBACK-15-PLAN.md 批次D）：用表單目前填的值（不需先儲存）試寄一封信。
 * 密碼欄留空時後端自動 fallback 已儲存的密文（見 SystemSettingsService.TestMail），
 * 這裡不需要另外提示——與「儲存」欄位共用同一套「留空＝沿用既有值」語意。
 */
function bindMailTest() {
    const button = document.getElementById('mail-test-btn');

    button.addEventListener('click', async () => {
        const recipients = collectMailRecipients();
        if (recipients.length === 0) {
            toast('請至少輸入一位收件人。', 'warning');
            return;
        }

        const resultEl = document.getElementById('mail-test-result');
        const restore = withBusy(button, '寄送中');
        try {
            const result = await api.post('/api/admin/settings/mail-test', {
                smtpServer: document.getElementById('smtp-server').value.trim(),
                smtpPort: Number(document.getElementById('smtp-port').value),
                smtpUseTls: document.getElementById('smtp-use-tls').checked,
                smtpAccount: document.getElementById('smtp-account').value.trim(),
                smtpPassword: document.getElementById('smtp-password').value || null,
                mailFrom: document.getElementById('mail-from').value.trim(),
                recipients,
                subjectTemplate: document.getElementById('mail-subject-template').value.trim(),
                bodyIntro: document.getElementById('mail-body-intro').value.trim()
            }, { silent: true });

            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.message;
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = error?.message || '測試寄信失敗。';
        } finally {
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

function bindAiProvider() {
    document.getElementById('ai-provider')?.addEventListener('change', e => {
        updateAiProviderFields(e.target.value);
    });
}

bindForm();
bindAiProvider();
bindAdTest();
bindMailTest();
bindAiAdvancedReset();
bindBrandIcon();
// #settings-tabs 在 <form> 外面，切頁籤的點擊不會冒泡進表單的 trackUnsaved 監聽器，
// 不需要額外排除——見 activateTabForElement 的說明
bindTabs(document.getElementById('settings-tabs'));
unsaved = trackUnsaved(document.getElementById('settings-form'), {
    excludeSelector: '#ad-test-account, #ad-test-password, #ad-test-btn, #ad-test-result, ' +
        '#mail-test-btn, #mail-test-result'
});
load();
