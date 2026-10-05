import { Sha256Stream } from './sha256-stream.js';

let hash = new Sha256Stream();
self.onmessage = ({ data }) => {
    try {
        if (data.type === 'reset') hash = new Sha256Stream();
        else if (data.type === 'append' && data.bytes instanceof ArrayBuffer) hash.update(new Uint8Array(data.bytes));
        else if (data.type !== 'finish') throw new Error('診斷檔雜湊工作要求無效。');
        self.postMessage({ id: data.id, ok: true, hash: data.type === 'finish' ? hash.digestHex() : undefined });
    } catch (error) {
        self.postMessage({ id: data.id, ok: false, message: error?.message || '診斷檔雜湊失敗。' });
    }
};
