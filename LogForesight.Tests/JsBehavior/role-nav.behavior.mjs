/**
 * 第 76-2 輪批次 F：角色與導覽。實際執行 production 的 layout.js（navItemVisible、NAV_SECTIONS）與
 * handler-detail.js（renderNoHandleCapability），只替換 hasCapability、appPath、renderEmpty 這類邊界。
 * 由 RoleNavigationBehaviorTests 以 `node role-nav.behavior.mjs <案例名> [能力逗號清單] [isServerAdmin 0/1]` 執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = file => fs.readFileSync(path.join(jsRoot, file), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');

function extractBlock(source, startMarker, open, close) {
    const start = source.indexOf(startMarker);
    assert(start >= 0, `找不到 production 程式：${startMarker}`);
    const first = source.indexOf(open, start);
    let depth = 0;
    for (let i = first; i < source.length; i++) {
        if (source[i] === open) depth++;
        else if (source[i] === close && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 程式未閉合：${startMarker}`);
}

function loadLayout() {
    const source = read('core/layout.js');
    const navSections = extractBlock(source, 'const NAV_SECTIONS =', '[', ']') + ';';
    const businessPages = source.match(/const BUSINESS_PAGES = [^;]+;/)[0];
    const visibleFn = extractBlock(source, 'function navItemVisible(', '{', '}');
    return new Function('hasCapability',
        `${businessPages}\n${navSections}\n${visibleFn}\nreturn { NAV_SECTIONS, navItemVisible };`)(
        (user, cap) => Array.isArray(user?.capabilities) && user.capabilities.includes(cap));
}

/** 這位使用者的導覽有哪些項目（與 renderNav 同一個判斷函式） */
function visibleLabels(user) {
    const { NAV_SECTIONS, navItemVisible } = loadLayout();
    const labels = [];
    for (const section of NAV_SECTIONS) {
        for (const item of section.items) {
            const resolvedHref = typeof item.href === 'function' ? item.href(user) : item.href;
            if (navItemVisible({ ...item, resolvedHref }, user)) labels.push(item.label);
        }
    }
    return labels;
}

const cases = {
    // F1-a、F1-b：給能力清單，輸出「我的交辦」可不可見，由 C# 端依真實角色對應表／resolver 提供能力
    navMyWork() {
        const caps = (process.argv[3] ?? '').split(',').filter(Boolean);
        const user = { userId: 7, capabilities: caps, isServerAdmin: process.argv[4] === '1' };
        console.log(`my-work=${visibleLabels(user).includes('我的交辦') ? 'visible' : 'hidden'}`);
    },

    // F1-c：沒有處理能力的人開自己的頁 → 說明原因，不是「目前沒有被指派的工作」
    noHandleExplained() {
        const source = read('pages/handler-detail.js');
        const fn = extractBlock(source, 'function renderNoHandleCapability(', '{', '}');
        const rendered = [];
        const render = new Function('renderEmpty', `${fn}\nreturn renderNoHandleCapability;`)((_c, opts) => rendered.push(opts));
        render({});
        assert.equal(rendered.length, 1);
        assert.ok(rendered[0].title.includes('沒有處理能力'));
        assert.ok(!rendered[0].title.includes('沒有被指派'));
        assert.ok(rendered[0].hint.length > 0);
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
