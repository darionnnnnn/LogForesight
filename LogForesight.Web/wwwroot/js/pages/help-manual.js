/**
 * 操作說明書（docs/archive/FEEDBACK-15-PLAN.md 批次E）：左側章節目錄＋右側內容，
 * 頂部單輪 AI 問答（實驗性）。內容渲染沿用 markdown-lite.js 的安全子集——
 * 這是全站唯一不使用可解析 HTML/連結的 Markdown 庫的渲染方式，即使手冊內容是自家資源，
 * 仍照專案既有的 XSS 紀律走（不引入新的可解析 HTML 的路徑）。
 */

import { api } from '../core/api.js';
import { toast, withBusy, icon } from '../core/ui.js';
import { renderAiText } from '../core/markdown-lite.js';

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
        btn.className = 'list-group-item list-group-item-action';
        btn.dataset.chapterId = chapter.id;
        btn.textContent = chapter.title;
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
    renderAiText(document.getElementById('help-chapter-content'), chapter.content);
    renderRelated(chapter);

    document.getElementById('help-chapter-content').scrollIntoView({ block: 'start', behavior: 'smooth' });
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

/** AI 問答框：未設定時整區換成說明文案，比照統計模式的誠實申報寫法 */
async function loadAskAvailability() {
    try {
        const status = await api.get('/api/help/ask-available', { silent: true });
        document.getElementById('help-ask-unavailable').classList.toggle('d-none', status.available);
        document.getElementById('help-ask-form-wrap').classList.toggle('d-none', !status.available);
    } catch {
        // 靜默：問答框本來就是加值功能，查詢失敗維持預設隱藏狀態即可，不擋住手冊內容
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

            const citedChapters = (result.citedChapterIds ?? []).map(id => chapterById.get(id)).filter(Boolean);
            if (citedChapters.length > 0) {
                const citeLabel = document.createElement('div');
                citeLabel.className = 'small text-muted mb-1';
                citeLabel.textContent = '引用章節：';
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
