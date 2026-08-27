/**
 * 操作說明書（docs/archive/FEEDBACK-15-PLAN.md 批次E）：左側章節目錄＋右側內容，
 * 頂部單輪 AI 問答（實驗性）。內容渲染沿用 markdown-lite.js 的安全子集——
 * 這是全站唯一不使用可解析 HTML/連結的 Markdown 庫的渲染方式，即使手冊內容是自家資源，
 * 仍照專案既有的 XSS 紀律走（不引入新的可解析 HTML 的路徑）。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { toast, withBusy, icon } from '../core/ui.js';
import { renderAiText } from '../core/markdown-lite.js';

/** 沒有指定圖示的章節（理論上不會發生，manifest 十四章都已配置）退回這個通用圖示 */
const DEFAULT_CHAPTER_ICON = 'file-earmark-text';

let chapters = [];
let chapterById = new Map();
let currentId = null;

async function load() {
    const manual = await api.get('/api/help/manual');
    chapters = manual.chapters;
    chapterById = new Map(chapters.map(c => [c.id, c]));

    renderNav();

    const hashId = location.hash.replace(/^#/, '');
    selectChapter(chapterById.has(hashId) ? hashId : chapters[0]?.id);

    loadAskAvailability();   // 獨立打，失敗不擋住章節內容（同 settings.js 的 backfill 狀態慣例）
}

function renderNav() {
    const nav = document.getElementById('help-chapter-nav');
    nav.replaceChildren();

    for (const chapter of chapters) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'list-group-item list-group-item-action d-flex align-items-center gap-2';
        btn.dataset.chapterId = chapter.id;
        btn.append(icon(chapter.icon || DEFAULT_CHAPTER_ICON), document.createTextNode(chapter.title));
        btn.addEventListener('click', () => selectChapter(chapter.id));
        nav.appendChild(btn);
    }
}

function selectChapter(id) {
    const chapter = chapterById.get(id);
    if (!chapter) return;

    currentId = id;
    history.replaceState(null, '', `#${id}`);

    for (const btn of document.querySelectorAll('#help-chapter-nav button')) {
        btn.classList.toggle('active', btn.dataset.chapterId === id);
    }

    document.getElementById('help-chapter-title').textContent = chapter.title;
    renderChapterContent(chapter);
    renderRelated(chapter);

    document.getElementById('help-chapter-content').scrollIntoView({ block: 'start', behavior: 'smooth' });
}

/**
 * type=link 章節（回饋十八輪批次H，目前只有啟動精靈）沒有 Markdown 內容可渲染——
 * markdown-lite 刻意不支援連結，這裡另外組一張導引卡。type=markdown（既有章節）
 * 走原本的 renderAiText。
 */
function renderChapterContent(chapter) {
    const container = document.getElementById('help-chapter-content');
    container.replaceChildren();

    if (chapter.type !== 'link') {
        renderAiText(container, chapter.content);
        return;
    }

    const card = document.createElement('div');
    card.className = 'lf-card p-3';

    const desc = document.createElement('p');
    desc.className = 'mb-3';
    desc.textContent = '透過就緒度檢查一步步完成初始設定：儲存體、管理員帳號、郵件通知、AI 服務、NetIQ、群組授權、排程，每一步都可以跳過稍後再回來。';
    card.appendChild(desc);

    const link = document.createElement('a');
    link.className = 'btn btn-primary';
    link.href = appUrl(chapter.href);
    link.textContent = '開啟啟動精靈';
    card.appendChild(link);

    container.appendChild(card);
}

function renderRelated(chapter) {
    const wrap = document.getElementById('help-chapter-related');
    wrap.replaceChildren();

    const relatedChapters = (chapter.related ?? []).map(id => chapterById.get(id)).filter(Boolean);
    if (relatedChapters.length === 0) {
        wrap.classList.add('d-none');
        return;
    }

    const label = document.createElement('div');
    label.className = 'small text-muted mb-2';
    label.textContent = '相關功能';
    wrap.appendChild(label);

    const list = document.createElement('div');
    list.className = 'd-flex flex-wrap gap-2';
    for (const related of relatedChapters) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary';
        btn.textContent = related.title;
        btn.addEventListener('click', () => selectChapter(related.id));
        list.appendChild(btn);
    }
    wrap.appendChild(list);
    wrap.classList.remove('d-none');
}

/**
 * AI 問答卡：未設定時整卡隱藏（回饋十六輪批次D-3，行為變更）——與全站其他 AI 入口
 * （儀表板焦點卡、清單頁歸納鈕、詳情頁對話）一致，不再顯示「未設定 AI 服務」的說明文案。
 */
async function loadAskAvailability() {
    try {
        const status = await api.get('/api/help/ask-available', { silent: true });
        document.getElementById('help-ask-card').classList.toggle('d-none', !status.available);
        if (status.available) loadRunHint();
    } catch {
        // 靜默：問答框本來就是加值功能，查詢失敗維持預設隱藏狀態即可，不擋住手冊內容
    }
}

/**
 * 分析執行中提示（回饋十六輪批次D-1）：AI 問答與批次分析共用同一個地端模型的序列請求佇列，
 * 執行中時回應會明顯變慢——卡片顯示當下查一次，不輪詢。
 */
async function loadRunHint() {
    const hint = document.getElementById('help-ask-run-hint');
    try {
        const activity = await api.get('/api/run-activity', { silent: true });
        hint.classList.toggle('d-none', !activity?.isRunning);
    } catch {
        hint.classList.add('d-none');
    }
}

function bindAskForm() {
    const form = document.getElementById('help-ask-form');
    const submitButton = document.getElementById('help-ask-submit');
    const resultEl = document.getElementById('help-ask-result');

    form.addEventListener('submit', async event => {
        event.preventDefault();

        const input = document.getElementById('help-ask-input');
        const question = input.value.trim();
        if (!question) return;

        const restore = withBusy(submitButton, '思考中');
        resultEl.replaceChildren();
        try {
            const result = await api.post('/api/help/ask', { question }, { silent: true });
            if (!result) {
                const warn = document.createElement('div');
                warn.className = 'text-muted small';
                warn.textContent = 'AI 服務暫時無法回應，可先查閱下方章節。';
                resultEl.appendChild(warn);
                return;
            }

            const answerBox = document.createElement('div');
            answerBox.className = 'border rounded p-2 mb-2';
            renderAiText(answerBox, result.answer, { badge: 'AI 回答' });
            resultEl.appendChild(answerBox);

            // 回饋十六輪批次D-2：這是 HelpChapterScorer 選進 prompt 的候選章節，不是模型自述
            // 實際引用了哪些——SystemPrompt 另外要求模型在回答結尾自列引用，兩份清單可能不同
            // （模型可能只用到候選裡的一部分）。標籤誠實反映「這是什麼」，避免與模型自述矛盾。
            const citedChapters = (result.citedChapterIds ?? []).map(id => chapterById.get(id)).filter(Boolean);
            if (citedChapters.length > 0) {
                const citeLabel = document.createElement('div');
                citeLabel.className = 'small text-muted mb-1';
                citeLabel.textContent = '參考章節（提供給 AI 的內容）：';
                resultEl.appendChild(citeLabel);

                const citeList = document.createElement('div');
                citeList.className = 'd-flex flex-wrap gap-2';
                for (const chapter of citedChapters) {
                    const btn = document.createElement('button');
                    btn.type = 'button';
                    btn.className = 'btn btn-sm btn-outline-primary';
                    btn.append(icon('file-earmark-text'), document.createTextNode(' ' + chapter.title));
                    btn.addEventListener('click', () => selectChapter(chapter.id));
                    citeList.appendChild(btn);
                }
                resultEl.appendChild(citeList);
            }
        } catch (error) {
            toast(error?.message || '提問失敗，請稍後再試。', 'error');
        } finally {
            restore();
        }
    });
}

window.addEventListener('hashchange', () => {
    const hashId = location.hash.replace(/^#/, '');
    if (chapterById.has(hashId) && hashId !== currentId) selectChapter(hashId);
});

bindAskForm();
load();
