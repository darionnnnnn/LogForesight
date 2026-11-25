/**
 * 停用或暫停接單前的三選一確認（L12）：名下有進行中的交辦單時，先說明張數與台數，
 * 讓管理者選「一併改派」（儲存後直接進交辦總覽的批次改派）或「先不處理」（照常停用／暫停，單仍在他名下）；
 * 關閉對話框或按「取消」＝不做這次變更。
 */

import { button } from '../core/ui.js';

/**
 * @param {{ title: string, message: string }} opts
 * @returns {Promise<'reassign'|'skip'|'cancel'>}
 */
export function askWorkOrderChoice({ title, message }) {
    return new Promise(resolve => {
        const el = document.createElement('div');
        el.className = 'modal fade';
        const titleId = `lf-modal-title-${Math.random().toString(36).slice(2, 10)}`;
        el.setAttribute('aria-labelledby', titleId);
        el.setAttribute('tabindex', '-1');

        const dialog = document.createElement('div');
        dialog.className = 'modal-dialog modal-dialog-centered';
        const content = document.createElement('div');
        content.className = 'modal-content';
        const header = document.createElement('div');
        header.className = 'modal-header';
        const heading = document.createElement('h5');
        heading.className = 'modal-title';
        heading.id = titleId;
        heading.textContent = title;
        const closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'btn-close';
        closeBtn.setAttribute('data-bs-dismiss', 'modal');
        closeBtn.setAttribute('aria-label', '關閉');
        header.append(heading, closeBtn);

        const body = document.createElement('div');
        body.className = 'modal-body';
        const text = document.createElement('p');
        text.className = 'mb-0 lf-pre-line';
        text.textContent = message;
        body.appendChild(text);

        let choice = 'cancel';
        const modal = { instance: null };
        const choose = value => () => { choice = value; modal.instance.hide(); };
        const footer = document.createElement('div');
        footer.className = 'modal-footer';
        footer.append(
            button('取消', { size: null, onClick: choose('cancel') }),
            button('先不處理', { size: null, onClick: choose('skip') }),
            button('一併改派', { variant: 'primary', size: null, onClick: choose('reassign') })
        );

        content.append(header, body, footer);
        dialog.appendChild(content);
        el.appendChild(dialog);
        document.body.appendChild(el);

        modal.instance = new bootstrap.Modal(el);
        el.addEventListener('hidden.bs.modal', () => {
            el.remove();
            resolve(choice);
        });
        modal.instance.show();
    });
}
