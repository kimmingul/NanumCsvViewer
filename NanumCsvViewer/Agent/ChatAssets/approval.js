(function (global) {
  'use strict';

  // Cards that need the user's answer inside the chat: change approvals (diff + approve/deny).
  // chat.js owns turns and passes messages in.
  let ctx = null;
  const cards = new Map();
  function T(key, ...args) {
    return global.T ? global.T(key, ...args) : key;
  }


  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function button(text, cls, onClick) {
    const b = el('button', 'card-btn ' + cls, text);
    b.addEventListener('click', onClick);
    return b;
  }

  function approval(msg) {
    const wasNear = ctx.isNearBottom();
    ctx.closeAssistant();
    const card = el('div', 'action-card approval-card');
    const head = el('div', 'card-head');
    head.appendChild(el('span', 'card-title', T('page.approval.request')));
    head.appendChild(el('span', 'card-target', msg.target || ''));
    card.appendChild(head);
    if (msg.summary) card.appendChild(el('div', 'card-summary', msg.summary));
    const pre = el('pre', 'card-diff');
    for (const line of msg.lines || []) {
      const row = el('div', 'diff-' + line.k);
      row.textContent = (line.k === 'add' ? '+ ' : line.k === 'del' ? '- ' : '  ') + line.t;
      pre.appendChild(row);
    }
    card.appendChild(pre);
    const actions = el('div', 'card-actions');
    const status = el('span', 'card-status');
    const answer = ok => {
      actions.querySelectorAll('button').forEach(b => { b.disabled = true; });
      status.textContent = ok ? T('page.approval.sendingApprove') : T('page.approval.sendingDeny');
      ctx.post({ t: 'approval', id: msg.id, ok });
    };
    actions.appendChild(button(T('page.approval.approve'), 'primary', () => answer(true)));
    actions.appendChild(button(T('page.approval.deny'), '', () => answer(false)));
    actions.appendChild(status);
    card.appendChild(actions);
    ctx.ensureTurn().appendChild(card);
    cards.set(msg.id, { card, actions, status });
    ctx.newContent(wasNear);
    card.scrollIntoView({ block: 'nearest' });
  }

  function approvalResult(msg) {
    const item = cards.get(msg.id);
    if (!item) return;
    item.actions.querySelectorAll('button').forEach(b => b.remove());
    item.status.textContent = msg.ok ? T('page.approval.approved') : T('page.approval.refused');
    item.card.classList.add(msg.ok ? 'approved' : 'refused');
    cards.delete(msg.id);
  }

  global.ChatCards = {
    init: c => { ctx = c; },
    approval, approvalResult,
    clear: () => cards.clear()
  };
})(typeof window !== 'undefined' ? window : globalThis);
