// 比對兩次 ui-shots.mjs 的截圖輸出（docs/archive/FEEDBACK-69-PLAN.md A-1／G-2 的「逐像素相同」驗收）。
// 先比檔案雜湊；不同的再解碼比像素，印出差異範圍（x、y、寬、高與差異像素數），
// 方便直接打開兩張圖對照那一塊。全部相同時結束代碼 0。
//
// 用法：node ui-diff.mjs <基準輸出目錄> <新輸出目錄>

import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { inflateSync } from 'node:zlib';

const [beforeDir, afterDir] = process.argv.slice(2);
if (!beforeDir || !afterDir) {
    console.error('用法：node ui-diff.mjs <基準輸出目錄> <新輸出目錄>');
    process.exit(2);
}

function pngFiles(dir) {
    const map = new Map();
    const walk = d => {
        for (const name of readdirSync(d)) {
            const full = join(d, name);
            if (statSync(full).isDirectory()) walk(full);
            else if (name.endsWith('.png')) map.set(relative(dir, full), full);
        }
    };
    walk(dir);
    return map;
}

const sha = file => createHash('sha256').update(readFileSync(file)).digest('hex');

/** 解碼 8 位元 RGB／RGBA、非交錯的 PNG（Edge 截圖就是這種），回傳 { width, height, channels, pixels } */
function decodePng(file) {
    const data = readFileSync(file);
    let pos = 8, width = 0, height = 0, colorType = 0;
    const idat = [];
    while (pos < data.length) {
        const len = data.readUInt32BE(pos);
        const type = data.toString('latin1', pos + 4, pos + 8);
        const chunk = data.subarray(pos + 8, pos + 8 + len);
        if (type === 'IHDR') { width = chunk.readUInt32BE(0); height = chunk.readUInt32BE(4); colorType = chunk[9]; }
        else if (type === 'IDAT') idat.push(chunk);
        pos += 12 + len;
    }
    const channels = colorType === 6 ? 4 : 3;
    const raw = inflateSync(Buffer.concat(idat));
    const stride = width * channels;
    const pixels = Buffer.alloc(stride * height);
    for (let y = 0; y < height; y++) {
        const filter = raw[y * (stride + 1)];
        const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
        const out = pixels.subarray(y * stride, (y + 1) * stride);
        const prev = y > 0 ? pixels.subarray((y - 1) * stride, y * stride) : null;
        for (let x = 0; x < stride; x++) {
            const a = x >= channels ? out[x - channels] : 0;
            const b = prev ? prev[x] : 0;
            const c = prev && x >= channels ? prev[x - channels] : 0;
            let v = line[x];
            if (filter === 1) v += a;
            else if (filter === 2) v += b;
            else if (filter === 3) v += (a + b) >> 1;
            else if (filter === 4) { const p = a + b - c; const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
            out[x] = v & 0xff;
        }
    }
    return { width, height, channels, pixels };
}

function pixelDiff(fileA, fileB) {
    const a = decodePng(fileA), b = decodePng(fileB);
    if (a.width !== b.width || a.height !== b.height) return `尺寸不同 ${a.width}×${a.height} → ${b.width}×${b.height}`;
    let minX = Infinity, minY = Infinity, maxX = -1, maxY = -1, count = 0;
    for (let y = 0; y < a.height; y++) {
        for (let x = 0; x < a.width; x++) {
            const i = (y * a.width + x) * a.channels;
            if (a.pixels[i] !== b.pixels[i] || a.pixels[i + 1] !== b.pixels[i + 1] || a.pixels[i + 2] !== b.pixels[i + 2]) {
                count++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
    }
    return count === 0 ? null : `差異範圍 x=${minX} y=${minY} ${maxX - minX + 1}×${maxY - minY + 1}，${count} 個像素`;
}

const before = pngFiles(beforeDir);
const after = pngFiles(afterDir);
let changed = 0;
for (const [key, file] of before) {
    if (!after.has(key) || sha(file) === sha(after.get(key))) continue;
    const diff = pixelDiff(file, after.get(key));
    if (diff) { changed++; console.log(`不同：${key}　${diff}`); }
}
const missing = [...before.keys()].filter(k => !after.has(k));
const added = [...after.keys()].filter(k => !before.has(k));
for (const k of missing) console.log(`新輸出缺少：${k}`);
for (const k of added) console.log(`基準沒有：${k}`);
console.log(`比對 ${before.size} 張：相同 ${before.size - changed - missing.length}、不同 ${changed}、缺少 ${missing.length}、新增 ${added.length}`);
process.exit(changed + missing.length + added.length === 0 ? 0 : 1);
