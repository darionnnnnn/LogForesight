/**
 * 全站設定（「系統管理 > 設定」頁）：未處理計算等級、AI 服務、資料保留天數。
 * 單一表單、單一 PUT，三個卡片對應同一份 SystemSettingsDto。
 */

import { api } from '../core/api.js';
import { toast, withBusy, trackUnsaved, bindTabs, icon, confirmAction, renderTable, renderSpinner } from '../core/ui.js';
import { formatDate, formatDateTime, formatNumber, formatUserName, severityName, SEVERITY_ORDER } from '../core/format.js';
import { alignBrandSubtitles } from '../core/brand-align.js';

// 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）：目前選定的圖示 data URI。
// 不放在表單欄位裡——<input type="file"> 的值無法用程式設定，載入既有圖示時填不回去
let brandIconDataUri = '';

/** 與後端 SystemSettingsService.BrandIconMaxKb 同一個門檻（前端先擋是體驗，後端才是防線） */
const BRAND_ICON_MAX_KB = 64;

/** 「還原預設外觀」填回的副標——與 Core 的 SystemSettings.BrandSubtitle 出廠值一致 */
const BRAND_DEFAULT_SUBTITLE = '事件日誌預警';

/** 雲端 AI 提供者的資料外送申報文字：provider 提示與切換確認框共用同一句 */
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
    renderPrtgFields(current);
    renderUpdatedAt(current);
    loadBackfillStatus();   // 獨立打，失敗靜默、不阻塞其餘欄位（見函式註解）
    loadAiUsage();          // 獨立打，失敗靜默（見函式註解）
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

/** 最近一次 GET /api/admin/settings/ai-usage 的回傳值；用於估費計算 */
let aiUsageData = null;

/**
 * 估算金額的唯一計算點（規格限制：反方向禁止複製，只能有一份）。
 * 依累計 promptTokens × inputPrice + completionTokens × outputPrice 計算，
 * 並即時更新 #ai-usage-cost 顯示。
 * 兩個單價都是 0（或空）時顯示「未設定單價」。
 * 此函式同時作為 input 事件處理器（單價欄位改動時即時觸發）。
 */
function calcAndRenderCost() {
    const costEl = document.getElementById('ai-usage-cost');
    if (!costEl) return;

    const inputPrice = Number(document.getElementById('ai-input-price')?.value) || 0;
    const outputPrice = Number(document.getElementById('ai-output-price')?.value) || 0;

    if (inputPrice === 0 && outputPrice === 0) {
        costEl.textContent = '未設定單價';
        return;
    }

    if (!aiUsageData) {
        costEl.textContent = '—';
        return;
    }

    const promptTokens = aiUsageData.total?.promptTokens ?? 0;
    const completionTokens = aiUsageData.total?.completionTokens ?? 0;

    // 金額＝(promptTokens × inputPrice + completionTokens × outputPrice) ÷ 1,000,000
    const cost = (promptTokens * inputPrice + completionTokens * outputPrice) / 1_000_000;
    // 小數位視金額大小而定：小於 1 顯示 4 位，其餘顯示 2 位
    const formatted = cost < 1 ? cost.toFixed(4) : cost.toFixed(2);
    costEl.textContent = formatted;
}

/**
 * 將 AiUsageDto 資料渲染至 AI 頁籤的用量統計區。
 * 同時更新模組狀態 aiUsageData 以供 calcAndRenderCost 使用。
 */
function renderAiUsage(usage) {
    aiUsageData = usage;

    // 今日消耗
    const todayEl = document.getElementById('ai-usage-today-tokens');
    if (todayEl) {
        todayEl.textContent = usage?.today
            ? formatNumber(usage.today.totalTokens)
            : '—';
    }

    // 累計消耗＋起算日小字
    const totalEl = document.getElementById('ai-usage-total-tokens');
    if (totalEl) {
        totalEl.textContent = usage?.total
            ? formatNumber(usage.total.totalTokens)
            : '—';
    }
    const sinceEl = document.getElementById('ai-usage-since');
    if (sinceEl) {
        sinceEl.textContent = usage?.countingSince ? `（自 ${usage.countingSince}）` : '';
    }

    // 累計呼叫次數
    const callsEl = document.getElementById('ai-usage-total-calls');
    if (callsEl) {
        callsEl.textContent = usage?.total ? formatNumber(usage.total.calls) : '—';
    }

    // 未回報 token 提示
    const noTokenHint = document.getElementById('ai-usage-no-token-hint');
    if (noTokenHint) {
        const n = usage?.total?.callsWithoutUsage ?? 0;
        if (n > 0) {
            noTokenHint.textContent =
                `注意：有 ${formatNumber(n)} 次呼叫的模型未回報 token 數（那些呼叫的 token 記 0）。`;
            noTokenHint.classList.remove('d-none');
        } else {
            noTokenHint.classList.add('d-none');
        }
    }

    // 更新時間
    const updatedEl = document.getElementById('ai-usage-updated');
    if (updatedEl) {
        updatedEl.textContent = usage?.updatedAt ? `最後更新：${formatDateTime(usage.updatedAt)}` : '';
    }

    // 重繪 30 天表格
    renderAiUsageDaysTable(usage?.days ?? []);

    // 依新資料即時更新估算金額
    calcAndRenderCost();
}

/**
 * 用 renderTable 渲染近 30 天明細表格。
 * days 為空時 renderTable 呼叫 renderEmpty，顯示「尚無資料」。
 */
function renderAiUsageDaysTable(days) {
    const container = document.getElementById('ai-usage-days-table');
    if (!container) return;

    renderTable(container, {
        columns: [
            { key: 'date',             title: '日期' },
            { key: 'calls',            title: '呼叫次數',        className: 'text-end', render: r => formatNumber(r.calls) },
            { key: 'promptTokens',     title: 'prompt tokens',   className: 'text-end', render: r => formatNumber(r.promptTokens) },
            { key: 'completionTokens', title: 'completion tokens', className: 'text-end', render: r => formatNumber(r.completionTokens) },
            { key: 'totalTokens',      title: '總計 tokens',     className: 'text-end', render: r => formatNumber(r.totalTokens) }
        ],
        rows: days,
        empty: { title: '尚無資料', hint: '目前沒有任何 AI 呼叫紀錄。' }
    });
}

/**
 * 非同步載入 AI token 用量統計。
 * 失敗靜默——這是加值資訊，不應阻塞設定頁其餘欄位。
 */
async function loadAiUsage() {
    try {
        const usage = await api.get('/api/admin/settings/ai-usage', { silent: true });
        renderAiUsage(usage);
    } catch {
        // 靜默：載不到統計不影響其他欄位
    }
}

/**
 * 「清空重新計算」按鈕：
 * 先跳既有破壞性操作確認（confirmAction），確認後打
 * POST /api/admin/settings/ai-usage/reset，成功後用回傳值重繪並顯示 toast。
 */
function bindAiUsageReset() {
    const button = document.getElementById('btn-ai-usage-reset');
    if (!button) return;

    button.addEventListener('click', async () => {
        const confirmed = await confirmAction({
            title: '清空 token 用量統計',
            message: '此操作將清除目前所有累計的 token 用量紀錄，清空後無法復原。確定要繼續？',
            confirmText: '清空',
            confirmVariant: 'danger'
        });
        if (!confirmed) return;

        const restore = withBusy(button, '清空中');
        try {
            const usage = await api.post('/api/admin/settings/ai-usage/reset', {});
            renderAiUsage(usage);
            toast('已清空 token 用量統計', 'success');
        } catch {
            // 錯誤由 api.js toast 顯示
        } finally {
            restore();
        }
    });
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

    // 單價欄位（token 用量估費，跟著整頁 form 儲存）
    setNumber('ai-input-price', settings.aiInputPricePerMillion);
    setNumber('ai-output-price', settings.aiOutputPricePerMillion);
    // 重算估費顯示（資料或單價其中一方改動時都要更新）
    calcAndRenderCost();
}

/** 分析參數（§12：伺服器角色／體檢間隔／監控資料夾／掃描頻道／權限異動欄位／匯入上限） */
function renderAnalysisFields(settings) {
    document.getElementById('server-description').value = settings.serverDescription ?? '';
    setNumber('checkup-interval-days', settings.checkupIntervalDays);
    document.getElementById('watched-folders').value = (settings.watchedFolders ?? []).join('\n');
    document.getElementById('analysis-channels').value = (settings.analysisChannels ?? []).join('\n');
    document.getElementById('perm-operator-fields').value = (settings.permissionOperatorFields ?? []).join('\n');
    document.getElementById('perm-member-fields').value = (settings.permissionMemberFields ?? []).join('\n');
    document.getElementById('perm-group-fields').value = (settings.permissionGroupFields ?? []).join('\n');
    document.getElementById('perm-object-fields').value = (settings.permissionObjectFields ?? []).join('\n');
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
    document.getElementById('account-display-rules').value = settings.accountDisplayRules ?? '';

    document.getElementById('ad-test-account').value = '';
    document.getElementById('ad-test-password').value = '';
    document.getElementById('ad-test-result').replaceChildren();

    renderAdStatus();
}

/**
 * AD 生效狀態。未設定時一般帳號一律登入失敗，而登入頁的訊息與密碼打錯完全相同
 * （刻意不洩漏帳號是否存在），管理者只能從這裡看出差別。
 * 隨勾選與伺服器清單即時更新，不必先儲存。
 */
function renderAdStatus() {
    const box = document.getElementById('ad-status');
    const enabled = document.getElementById('ad-auth-enabled').checked;
    const serverCount = collectAdServers().length;

    box.classList.remove('d-none', 'alert-warning', 'alert-success');
    if (!enabled || serverCount === 0) {
        box.classList.add('alert-warning');
        box.textContent = '目前狀態：AD 驗證未生效——一般帳號一律無法登入（登入頁只會顯示「帳號或密碼錯誤」）。'
            + '請以本機救援帳號（serverAdmin）登入設定，或維持此狀態只用救援帳號管理。';
    } else {
        box.classList.add('alert-success');
        box.textContent = `目前狀態：AD 驗證生效中，共 ${serverCount} 台伺服器依序嘗試。`;
    }
}

/**
 * 使用者名稱顯示規則的前端格式檢查（回饋三十四輪 B2）：格式為「樣式 => 取代文字」，
 * `#` 開頭為註解、空白行略過。回傳錯誤訊息字串（含行號）或 null。
 *
 * **只驗與正則引擎無關的格式**：正則語法一律交給後端驗。前端是 JavaScript RegExp、
 * 後端是 .NET Regex，語法集不同（例如 `\p{IsCJKUnifiedIdeographs}`、變長後查在 .NET 合法、
 * 在 JS 直接丟例外）——在這裡驗語法會把後端接受的規則永遠擋在存檔之外（終檢抓到）。
 */
function validateAccountDisplayRules(text) {
    const lines = (text ?? '').split(/\r\n|\r|\n/);
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line.length === 0 || line.startsWith('#')) continue;

        const arrow = line.indexOf('=>');
        if (arrow < 0) return `第 ${i + 1} 行規則格式錯誤：缺少「=>」分隔符號。`;
        if (line.slice(0, arrow).trim().length === 0) return `第 ${i + 1} 行正則表達式樣式不可為空。`;
    }
    return null;
}

/** 一行一台，去除空白行——與後端 SystemSettingsService.NormalizeAdServers 對齊的寬鬆解析 */
function collectAdServers() {
    return collectLines('ad-servers');
}

function renderRetentionFields(settings) {
    document.getElementById('initial-history-days').value = settings.initialHistoryDays;
    document.getElementById('retention-days').value = settings.retentionDays;
    document.getElementById('raw-event-retention-days').value = settings.rawEventRetentionDays;
    document.getElementById('run-log-retention-days').value = settings.runLogRetentionDays;
    document.getElementById('audit-retention-days').value = settings.auditRetentionDays;
    document.getElementById('report-retention-days').value = settings.reportRetentionDays;

    // 空間告知用實測值：報告全文存在資料庫，管理者要能看到「現在到底佔了多少」
    // 才有辦法決定保留期，一條估算公式他無從驗證
    document.getElementById('report-usage-count').textContent = formatNumber(settings.reportCount ?? 0);
    // 小於 0.1 MB 但確實有資料時顯示「< 0.1」而不是「0.0」——後者看起來像「一份都沒有」，
    // 與旁邊的份數自相矛盾
    const sizeMb = settings.reportSizeMb ?? 0;
    document.getElementById('report-usage-size').textContent =
        sizeMb > 0 && sizeMb < 0.1 ? '< 0.1' : sizeMb.toFixed(1);
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
            img.className = 'lf-brand__img';
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
            subtitleEl = document.createElement('span');
            subtitleEl.id = 'lf-brand-subtitle';
            subtitleEl.className = 'lf-brand__subtitle';
            document.querySelector('.lf-brand__text')?.appendChild(subtitleEl);
        }
        subtitleEl.textContent = settings.brandSubtitle;
        subtitleEl.title = settings.brandSubtitle;
    } else if (subtitleEl) {
        subtitleEl.remove();
    }

    // 主標題／副標題文字換了，貼齊要重算（brand-align.js 只在載入時與 resize 時算）
    alignBrandSubtitles();
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

/** PRTG 認證方式切換：依選取模式切換 token / password 區塊顯示（只動 classList 不設 style.display） */
function syncPrtgAuthFields() {
    const authMode = document.getElementById('prtg-auth-mode')?.value ?? 'token';
    const tokenFields = document.getElementById('prtg-auth-token-fields');
    const passwordFields = document.getElementById('prtg-auth-password-fields');

    if (tokenFields) {
        tokenFields.classList.toggle('d-none', authMode !== 'token');
    }
    if (passwordFields) {
        passwordFields.classList.toggle('d-none', authMode !== 'password');
    }
}

/** PRTG 監控系統設定（批次B-4）：填入表單欄位，token 與 password 永遠留空（write-only） */
function renderPrtgFields(settings) {
    document.getElementById('prtg-enabled').checked = Boolean(settings.prtgEnabled);
    document.getElementById('prtg-url').value = settings.prtgUrl ?? '';

    const authModeSelect = document.getElementById('prtg-auth-mode');
    if (authModeSelect) {
        authModeSelect.value = settings.prtgAuthMode || 'token';
    }

    document.getElementById('prtg-api-token').value = '';
    document.getElementById('prtg-clear-token').checked = false;

    const tokenHint = document.getElementById('prtg-api-token-hint');
    tokenHint.textContent = settings.prtgHasApiToken
        ? '已設定 API token；留空儲存＝沿用既有 API token，輸入新值才會覆蓋。'
        : '尚未設定 API token。';

    const usernameInput = document.getElementById('prtg-username');
    if (usernameInput) {
        usernameInput.value = settings.prtgUsername ?? '';
    }

    const passwordInput = document.getElementById('prtg-password');
    if (passwordInput) {
        passwordInput.value = '';
    }

    const clearPasswordCheck = document.getElementById('prtg-clear-password');
    if (clearPasswordCheck) {
        clearPasswordCheck.checked = false;
    }

    const passwordHint = document.getElementById('prtg-password-hint');
    if (passwordHint) {
        passwordHint.textContent = settings.prtgHasPassword
            ? '已設定密碼；留空儲存＝沿用既有密碼，輸入新值才會覆蓋。'
            : '尚未設定密碼。';
    }

    syncPrtgAuthFields();

    document.getElementById('prtg-ignore-ssl').checked = Boolean(settings.prtgIgnoreSslErrors);
    setNumber('prtg-timeout-seconds', settings.prtgTimeoutSeconds);
    setNumber('prtg-fetch-concurrency', settings.prtgFetchConcurrency);
    setNumber('prtg-backfill-days', settings.prtgBackfillDays);
    setNumber('prtg-retention-days', settings.prtgRetentionDays);
    document.getElementById('prtg-test-result').replaceChildren();
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

        const rawEventRetentionDays = Number(document.getElementById('raw-event-retention-days').value);
        const runLogRetentionDays = Number(document.getElementById('run-log-retention-days').value);
        const auditRetentionDays = Number(document.getElementById('audit-retention-days').value);
        const reportRetentionDays = Number(document.getElementById('report-retention-days').value);
        if (rawEventRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('raw-event-retention-days'));
            toast('原始事件內容保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        // 報告全文存在資料庫，超過歷史資料保留天數之後站上已無入口可點，留著只佔空間
        if (reportRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('report-retention-days'));
            toast('報告保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        const prtgRetentionDays = Number(document.getElementById('prtg-retention-days').value);
        if (prtgRetentionDays > retentionDays) {
            activateTabForElement(document.getElementById('prtg-retention-days'));
            toast('PRTG 資料保留天數不可大於歷史資料保留天數。', 'warning');
            return;
        }

        const prtgEnabled = document.getElementById('prtg-enabled').checked;
        const prtgUrl = document.getElementById('prtg-url').value.trim();
        if (prtgEnabled) {
            if (!prtgUrl) {
                activateTabForElement(document.getElementById('prtg-url'));
                toast('啟用 PRTG 時必須填寫 PRTG 位址。', 'warning');
                return;
            }

            const authMode = document.getElementById('prtg-auth-mode')?.value ?? 'token';
            if (authMode === 'password') {
                const username = document.getElementById('prtg-username')?.value.trim();
                if (!username) {
                    activateTabForElement(document.getElementById('prtg-username'));
                    toast('啟用 PRTG 並使用帳號密碼時，必須設定帳號。', 'warning');
                    return;
                }

                const password = document.getElementById('prtg-password')?.value ?? '';
                const clearPassword = document.getElementById('prtg-clear-password')?.checked ?? false;
                // 密碼空白＝沿用既有；但若原本未設定密碼（或勾選了清除密碼），則必須輸入密碼
                const hasExistingPassword = Boolean(current?.prtgHasPassword) && !clearPassword;
                if (!password && !hasExistingPassword) {
                    activateTabForElement(document.getElementById('prtg-password'));
                    toast('啟用 PRTG 並使用帳號密碼時，必須設定密碼。', 'warning');
                    return;
                }
            }
        }

        // **只列真的會造成刪除的設定。** 首次執行回補天數刻意不列——它決定的是
        // 「首次執行要往回補幾天」，調小不會刪掉任何既有資料。把不會發生的事寫進
        // 刪除確認，只會讓使用者學會忽略這個對話框。
        const reducedItems = [];
        if (retentionDays < current.retentionDays) {
            reducedItems.push(`歷史資料保留天數：${current.retentionDays} → ${retentionDays} 天（${retentionDays} 天以前的分析紀錄將進入刪除範圍）`);
        }
        if (rawEventRetentionDays < current.rawEventRetentionDays) {
            reducedItems.push(`原始事件內容保留天數：${current.rawEventRetentionDays} → ${rawEventRetentionDays} 天（${rawEventRetentionDays} 天以前的原始事件文字將被清除，統計與問題清單保留）`);
        }
        if (runLogRetentionDays < current.runLogRetentionDays) {
            reducedItems.push(`執行歷程保留天數：${current.runLogRetentionDays} → ${runLogRetentionDays} 天（${runLogRetentionDays} 天以前的紀錄將進入刪除範圍）`);
        }
        if (auditRetentionDays < current.auditRetentionDays) {
            reducedItems.push(`稽核與追責紀錄保留天數：${current.auditRetentionDays} → ${auditRetentionDays} 天（${auditRetentionDays} 天以前的紀錄將進入刪除範圍）`);
        }
        if (reportRetentionDays < current.reportRetentionDays) {
            reducedItems.push(`報告保留天數：${current.reportRetentionDays} → ${reportRetentionDays} 天（${reportRetentionDays} 天以前的報告全文將進入刪除範圍）`);
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

        // 使用者名稱顯示規則：前端先擋一次格式與明顯的語法錯誤是體驗，
        // 後端仍會逐行驗一次（前端驗證不是防線）
        const ruleError = validateAccountDisplayRules(document.getElementById('account-display-rules').value);
        if (ruleError) {
            activateTabForElement(document.getElementById('account-display-rules'));
            toast(ruleError, 'warning');
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
                rawEventRetentionDays,
                runLogRetentionDays,
                auditRetentionDays,
                reportRetentionDays,
                adAuthEnabled,
                adServers,
                adSearchBase: document.getElementById('ad-search-base').value.trim(),
                adSearchFilter: document.getElementById('ad-search-filter').value.trim(),
                accountDisplayRules: document.getElementById('account-display-rules').value,
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
                // token 用量單價（跟著整頁 form 儲存）
                aiInputPricePerMillion: Number(document.getElementById('ai-input-price').value) || 0,
                aiOutputPricePerMillion: Number(document.getElementById('ai-output-price').value) || 0,
                // 分析參數（§12）
                serverDescription: document.getElementById('server-description').value.trim(),
                checkupIntervalDays: Number(document.getElementById('checkup-interval-days').value),
                watchedFolders: collectLines('watched-folders'),
                analysisChannels: collectLines('analysis-channels'),
                permissionOperatorFields: collectLines('perm-operator-fields'),
                permissionMemberFields: collectLines('perm-member-fields'),
                permissionGroupFields: collectLines('perm-group-fields'),
                permissionObjectFields: collectLines('perm-object-fields'),
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
                brandIconDataUri: brandIconDataUri,
                // PRTG 監控系統
                prtgEnabled,
                prtgUrl,
                prtgAuthMode: document.getElementById('prtg-auth-mode')?.value ?? 'token',
                prtgUsername: document.getElementById('prtg-username')?.value.trim() ?? '',
                prtgPassword: document.getElementById('prtg-password')?.value || null,
                clearPrtgPassword: document.getElementById('prtg-clear-password')?.checked ?? false,
                prtgIgnoreSslErrors: document.getElementById('prtg-ignore-ssl').checked,
                prtgApiToken: document.getElementById('prtg-api-token').value || null,
                clearPrtgApiToken: document.getElementById('prtg-clear-token').checked,
                prtgTimeoutSeconds: Number(document.getElementById('prtg-timeout-seconds').value),
                prtgFetchConcurrency: Number(document.getElementById('prtg-fetch-concurrency').value),
                prtgBackfillDays: Number(document.getElementById('prtg-backfill-days').value),
                prtgRetentionDays
            });
            toast('已儲存設定', 'success');
            renderAiFields(current);
            renderAdFields(current);
            renderAnalysisFields(current);
            renderMailFields(current);
            renderBrandFields(current);
            renderPrtgFields(current);
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

/** PRTG 連線測試（批次B-4）：使用表單當前填寫值測試連線（不需先儲存） */
function bindPrtgTest() {
    const button = document.getElementById('prtg-test-btn');

    button.addEventListener('click', async () => {
        const url = document.getElementById('prtg-url').value.trim();
        if (!url) {
            toast('請先輸入 PRTG 位址。', 'warning');
            return;
        }

        const resultEl = document.getElementById('prtg-test-result');
        const restore = withBusy(button, '測試中');
        try {
            const result = await api.post('/api/admin/settings/prtg-test', {
                url,
                authMode: document.getElementById('prtg-auth-mode')?.value ?? 'token',
                username: document.getElementById('prtg-username')?.value.trim() ?? '',
                password: document.getElementById('prtg-password')?.value || null,
                apiToken: document.getElementById('prtg-api-token').value || null,
                ignoreSslErrors: document.getElementById('prtg-ignore-ssl').checked,
                timeoutSeconds: Number(document.getElementById('prtg-timeout-seconds').value)
            }, { silent: true });

            // 連線失敗是 HTTP 200 + success=false（走不到 catch），符號必須跟著 success 走，
            // 否則畫面會顯示「✓ 測試連線失敗：…」
            const mark = result.success ? '✓' : '✗';
            resultEl.className = result.success ? 'text-success small' : 'text-danger small';
            resultEl.textContent = result.elapsedMs != null
                ? `${mark} ${result.message}（耗時 ${result.elapsedMs}ms）`
                : `${mark} ${result.message}`;
        } catch (error) {
            resultEl.className = 'text-danger small';
            resultEl.textContent = `✗ ${error?.message || '測試連線失敗。'}`;
        } finally {
            restore();
        }
    });
}

// ── PRTG API 探測（批次B-4，比照 NetIQ 診斷實作）─────────────────────────

let prtgProbePollTimer = null;

/** 輪詢更新時只換文字節點、不重建 spinner（避免每次輪詢動畫重置閃爍） */
function setPrtgProbeSpinnerText(container, text) {
    if (!container.querySelector('.spinner-border')) {
        renderSpinner(container, text);
        return;
    }
    const label = container.querySelector('span:last-child');
    if (label) label.textContent = text;
}

function renderPrtgProbeStatus(status) {
    const outputEl = document.getElementById('prtg-probe-output');
    const copyButton = document.getElementById('prtg-probe-copy');
    const startButton = document.getElementById('prtg-probe-start');
    const statusEl = document.getElementById('prtg-probe-status');

    // 探測區預設收合。執行中時自動展開——否則按下探測後重新整理頁面，
    // 進度與輸出會被收在摺疊區裡，看起來像什麼都沒發生
    if (status.isRunning) {
        const details = document.getElementById('prtg-probe');
        if (details) details.open = true;
    }

    const outputText = Array.isArray(status.output) ? status.output.join('\n') : (status.output || '');
    outputEl.value = outputText;
    if (outputText) {
        outputEl.scrollTop = outputEl.scrollHeight;
    }
    copyButton.disabled = !outputText;

    if (status.isRunning) {
        startButton.disabled = true;
        setPrtgProbeSpinnerText(statusEl, `探測中…${status.latestMessage ? ' ' + status.latestMessage : ''}`);
        return;
    }

    startButton.disabled = false;
    if (!status.completedAt) {
        statusEl.textContent = '';
        return;
    }
    statusEl.textContent = `上次執行：${formatDateTime(status.completedAt)} ` +
        (status.success ? '✓ 完成' : '✗ 執行中發生錯誤');
}

async function refreshPrtgProbeStatus() {
    let status;
    try {
        status = await api.get('/api/admin/settings/prtg-probe/status', { silent: true });
    } catch {
        return;
    }
    renderPrtgProbeStatus(status);

    if (status.isRunning && !prtgProbePollTimer) {
        prtgProbePollTimer = setInterval(async () => {
            const latest = await api.get('/api/admin/settings/prtg-probe/status', { silent: true }).catch(() => null);
            if (!latest) return;
            renderPrtgProbeStatus(latest);
            if (!latest.isRunning) {
                clearInterval(prtgProbePollTimer);
                prtgProbePollTimer = null;
            }
        }, 2000);
    }
}

// ── PRTG 鏡像狀態（批次F）──────────────────────────────────────────────

function renderPrtgMirror(data) {
    if (!data) return;

    const setTxt = (id, text) => {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    };

    setTxt('prtg-mirror-device-count', formatNumber(data.deviceCount));
    setTxt('prtg-mirror-sensor-count', formatNumber(data.sensorCount));
    setTxt('prtg-mirror-last-device-sync', `最後同步：${data.lastDeviceSync ? formatDateTime(data.lastDeviceSync) : '-'}`);
    setTxt('prtg-mirror-last-sensor-sync', `最後同步：${data.lastSensorSync ? formatDateTime(data.lastSensorSync) : '-'}`);
    setTxt('prtg-mirror-last-value-at', `數值：${data.lastValueAt ? formatDateTime(data.lastValueAt) : '-'}`);
    setTxt('prtg-mirror-last-state-change-at', `狀態變更：${data.lastStateChangeAt ? formatDateTime(data.lastStateChangeAt) : '-'}`);

    setTxt('prtg-mirror-map-date', `對應基準日：${data.mapDate ? formatDate(data.mapDate) : '無'}`);
    setTxt('prtg-mirror-map-ok', formatNumber(data.mapOk));
    setTxt('prtg-mirror-map-conflict', formatNumber(data.mapConflict));
    setTxt('prtg-mirror-map-unmatched', formatNumber(data.mapUnmatched));

    const renderList = (bodyId, items, emptyText) => {
        const tbody = document.getElementById(bodyId);
        if (!tbody) return;
        tbody.replaceChildren();

        if (!items || items.length === 0) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = 4;
            td.className = 'text-muted text-center py-2';
            td.textContent = emptyText;
            tr.appendChild(td);
            tbody.appendChild(tr);
            return;
        }

        for (const item of items) {
            const tr = document.createElement('tr');

            const tdObjid = document.createElement('td');
            tdObjid.className = 'font-monospace';
            tdObjid.textContent = String(item.deviceObjid);

            const tdIp = document.createElement('td');
            tdIp.className = 'font-monospace';
            tdIp.textContent = item.ip || '-';

            const tdHost = document.createElement('td');
            tdHost.textContent = item.hostName || '-';

            const tdNote = document.createElement('td');
            tdNote.className = 'text-muted';
            tdNote.textContent = item.note || '-';

            tr.append(tdObjid, tdIp, tdHost, tdNote);
            tbody.appendChild(tr);
        }
    };

    renderList('prtg-mirror-conflicts-body', data.conflicts, '無衝突項目');
    renderList('prtg-mirror-unmatched-body', data.unmatched, '無未對應項目');
}

async function refreshPrtgMirror() {
    try {
        const data = await api.get('/api/admin/settings/prtg-mirror', { silent: true });
        renderPrtgMirror(data);
    } catch {
        // 失敗時不干擾整體頁面
    }
}

function bindPrtgMirror() {
    const refreshBtn = document.getElementById('prtg-mirror-refresh');
    refreshBtn?.addEventListener('click', async () => {
        const restore = withBusy(refreshBtn, '載入中');
        try {
            await refreshPrtgMirror();
            toast('已重新整理 PRTG 鏡像狀態', 'success');
        } finally {
            restore();
        }
    });
}

// ── PRTG 歷史回填（批次E）──────────────────────────────────────────────

let prtgBackfillPollTimer = null;

function setPrtgBackfillSpinnerText(container, text) {
    if (!container.querySelector('.spinner-border')) {
        renderSpinner(container, text);
        return;
    }
    const label = container.querySelector('span:last-child');
    if (label) label.textContent = text;
}

function renderPrtgBackfillStatus(status) {
    const outputEl = document.getElementById('prtg-backfill-output');
    const copyButton = document.getElementById('prtg-backfill-copy');
    const startButton = document.getElementById('prtg-backfill-start');
    const statusEl = document.getElementById('prtg-backfill-status');

    if (!outputEl || !copyButton || !startButton || !statusEl) return;

    const outputText = Array.isArray(status.output) ? status.output.join('\n') : (status.output || '');
    outputEl.value = outputText;
    if (outputText) {
        outputEl.scrollTop = outputEl.scrollHeight;
    }
    copyButton.disabled = !outputText;

    if (status.isRunning) {
        startButton.disabled = true;
        setPrtgBackfillSpinnerText(statusEl, `回填中…${status.latestMessage ? ' ' + status.latestMessage : ''}`);
        return;
    }

    startButton.disabled = false;
    if (!status.completedAt) {
        statusEl.textContent = '';
        return;
    }
    statusEl.textContent = `上次執行：${formatDateTime(status.completedAt)} ` +
        (status.success ? '✓ 完成' : '✗ 執行中發生錯誤');
}

async function refreshPrtgBackfillStatus() {
    let status;
    try {
        status = await api.get('/api/admin/settings/prtg-backfill/status', { silent: true });
    } catch {
        return;
    }
    renderPrtgBackfillStatus(status);

    if (status.isRunning && !prtgBackfillPollTimer) {
        prtgBackfillPollTimer = setInterval(async () => {
            const latest = await api.get('/api/admin/settings/prtg-backfill/status', { silent: true }).catch(() => null);
            if (!latest) return;
            renderPrtgBackfillStatus(latest);
            if (!latest.isRunning) {
                clearInterval(prtgBackfillPollTimer);
                prtgBackfillPollTimer = null;
                // 回填結束後連帶重新整理鏡像摘要數據
                refreshPrtgMirror();
            }
        }, 2000);
    }
}

function bindPrtgBackfill() {
    const startButton = document.getElementById('prtg-backfill-start');
    const copyButton = document.getElementById('prtg-backfill-copy');
    const outputEl = document.getElementById('prtg-backfill-output');

    startButton?.addEventListener('click', async () => {
        const ok = await confirmAction({
            title: '確認執行 PRTG 歷史資料回填',
            message: '回填會逐日擷取歷史監控數據與狀態變更，請確認目前為離峰時間。是否確定開始？',
            confirmText: '開始回填',
            confirmVariant: 'primary'
        });
        if (!ok) return;

        startButton.disabled = true;
        try {
            await api.post('/api/admin/settings/prtg-backfill/start', {}, { silent: true });
            toast('已開始執行 PRTG 歷史回填', 'success');
            await refreshPrtgBackfillStatus();
        } catch {
            startButton.disabled = false;
        }
    });

    copyButton?.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(outputEl.value);
            toast('已複製回填輸出', 'success');
        } catch {
            toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
        }
    });
}

function bindPrtgProbe() {
    const startButton = document.getElementById('prtg-probe-start');
    const copyButton = document.getElementById('prtg-probe-copy');
    const outputEl = document.getElementById('prtg-probe-output');

    startButton.addEventListener('click', async () => {
        // 不用 withBusy：啟動成功後按鈕的 disabled 狀態交給輪詢狀態接管
        startButton.disabled = true;
        try {
            await api.post('/api/admin/settings/prtg-probe/start', {}, { silent: true });
            toast('已開始探測 PRTG 環境', 'success');
            await refreshPrtgProbeStatus();
        } catch {
            startButton.disabled = false;
        }
    });

    copyButton.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(outputEl.value);
            toast('已複製探測輸出', 'success');
        } catch {
            toast('複製失敗，瀏覽器可能不允許存取剪貼簿', 'danger');
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
        // 位址欄語意隨 provider 改變（本機端點／OpenAI proxy／Azure endpoint），切換時清空，
        // 否則從 Local 切到雲端會把 localhost 位址一起存出去——請求根本沒出內網，申報卻說會
        const baseUrl = document.getElementById('ai-base-url');
        if (baseUrl) baseUrl.value = '';
        updateAiProviderFields(e.target.value);
    });
}

bindForm();
bindAiProvider();
bindAdTest();
bindMailTest();
bindPrtgTest();
bindPrtgMirror();
bindPrtgBackfill();
bindPrtgProbe();
bindAiAdvancedReset();
bindAiUsageReset();
// 單價欄位改動時即時更新估算金額（不必按儲存、不必打 API）
document.getElementById('ai-input-price')?.addEventListener('input', calcAndRenderCost);
document.getElementById('ai-output-price')?.addEventListener('input', calcAndRenderCost);
// AD 生效狀態同樣即時反映目前欄位內容，不必先儲存
document.getElementById('ad-auth-enabled')?.addEventListener('change', renderAdStatus);
document.getElementById('ad-servers')?.addEventListener('input', renderAdStatus);
// PRTG 認證方式切換即時連動欄位顯示（PRTG 第 2 輪批次A）
document.getElementById('prtg-auth-mode')?.addEventListener('change', syncPrtgAuthFields);
bindBrandIcon();
// #settings-tabs 在 <form> 外面，切頁籤的點擊不會冒泡進表單的 trackUnsaved 監聽器，
// 不需要額外排除——見 activateTabForElement 的說明
bindTabs(document.getElementById('settings-tabs'));
unsaved = trackUnsaved(document.getElementById('settings-form'), {
    excludeSelector: '#ad-test-account, #ad-test-password, #ad-test-btn, #ad-test-result, ' +
        '#mail-test-btn, #mail-test-result, ' +
        '#prtg-test-btn, #prtg-test-result, ' +
        '#prtg-mirror-refresh, ' +
        '#prtg-backfill-start, #prtg-backfill-status, #prtg-backfill-output, #prtg-backfill-copy, ' +
        '#prtg-probe-start, #prtg-probe-status, #prtg-probe-output, #prtg-probe-copy'
});
load();
refreshPrtgMirror();
refreshPrtgBackfillStatus();
refreshPrtgProbeStatus();

