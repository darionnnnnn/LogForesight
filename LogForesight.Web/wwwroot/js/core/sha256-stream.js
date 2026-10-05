const ROUND_CONSTANTS = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
]);
const rotate = (value, bits) => (value >>> bits) | (value << (32 - bits));

/** 固定 64-byte 尾段與工作陣列；瀏覽器不需把整份診斷檔放入記憶體。 */
export class Sha256Stream {
    #hash = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
    #block = new Uint8Array(64);
    #words = new Uint32Array(64);
    #tail = 0;
    #bytes = 0n;
    #digest;

    update(bytes) {
        if (this.#digest !== undefined) throw new Error('SHA-256 已完成，不能追加資料。');
        if (!(bytes instanceof Uint8Array)) throw new TypeError('SHA-256 需要原始位元組。');
        this.#bytes += BigInt(bytes.byteLength);
        if (this.#bytes >= (1n << 61n)) throw new RangeError('診斷檔超過 SHA-256 長度範圍。');
        let offset = 0;
        if (this.#tail !== 0) {
            const count = Math.min(64 - this.#tail, bytes.length);
            this.#block.set(bytes.subarray(0, count), this.#tail);
            this.#tail += count;
            offset = count;
            if (this.#tail === 64) {
                this.#compress(this.#block);
                this.#tail = 0;
            }
        }
        while (offset + 64 <= bytes.length) {
            this.#compress(bytes.subarray(offset, offset + 64));
            offset += 64;
        }
        if (offset < bytes.length) {
            this.#block.set(bytes.subarray(offset), 0);
            this.#tail = bytes.length - offset;
        }
        return this;
    }

    digestHex() {
        if (this.#digest !== undefined) return this.#digest;
        this.#block[this.#tail++] = 0x80;
        this.#block.fill(0, this.#tail);
        if (this.#tail > 56) {
            this.#compress(this.#block);
            this.#block.fill(0);
        }
        let bits = this.#bytes * 8n;
        for (let index = 63; index >= 56; index--) {
            this.#block[index] = Number(bits & 255n);
            bits >>= 8n;
        }
        this.#compress(this.#block);
        this.#digest = Array.from(this.#hash, value => value.toString(16).padStart(8, '0')).join('').toUpperCase();
        return this.#digest;
    }

    #compress(block) {
        const words = this.#words;
        for (let index = 0; index < 16; index++) {
            const offset = index * 4;
            words[index] = (block[offset] << 24) | (block[offset + 1] << 16) |
                (block[offset + 2] << 8) | block[offset + 3];
        }
        for (let index = 16; index < 64; index++) {
            const first = words[index - 15];
            const second = words[index - 2];
            const sigma0 = rotate(first, 7) ^ rotate(first, 18) ^ (first >>> 3);
            const sigma1 = rotate(second, 17) ^ rotate(second, 19) ^ (second >>> 10);
            words[index] = (words[index - 16] + sigma0 + words[index - 7] + sigma1) >>> 0;
        }
        let [a, b, c, d, e, f, g, h] = this.#hash;
        for (let index = 0; index < 64; index++) {
            const sum1 = rotate(e, 6) ^ rotate(e, 11) ^ rotate(e, 25);
            const choice = (e & f) ^ (~e & g);
            const temporary1 = (h + sum1 + choice + ROUND_CONSTANTS[index] + words[index]) >>> 0;
            const sum0 = rotate(a, 2) ^ rotate(a, 13) ^ rotate(a, 22);
            const majority = (a & b) ^ (a & c) ^ (b & c);
            h = g; g = f; f = e;
            e = (d + temporary1) >>> 0;
            d = c; c = b; b = a;
            a = (temporary1 + sum0 + majority) >>> 0;
        }
        const result = [a, b, c, d, e, f, g, h];
        for (let index = 0; index < 8; index++) this.#hash[index] = (this.#hash[index] + result[index]) >>> 0;
    }
}
