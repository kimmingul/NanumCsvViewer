(function (global) {
  'use strict';

  // The message box and the line under it: send/stop, / command menu, @column menu, input history,
  // context chips, approval mode, model, thinking level, context ring. The host does the work.
  const MaxHistory = 100;
  const MaxMenuRows = 30;
  let input, sendBtn, slash;
  let commands = [];
  let columns = [];
  let menuKind = '';
  let atStart = 0;
  let atOpen = false;
  let matches = [];
  let active = 0;
  const history = [];
  let historyIndex = -1;
  let draft = '';
  let state = { connected: false, busy: false };
  // Sent drafts by submission id until the host answers ok (accepted) or not (rejected).
  const pending = new Map();
  let nextSubmission = 0;
  function T(key, ...args) {
    return global.T ? global.T(key, ...args) : key;
  }


  function $(id) { return document.getElementById(id); }
  function post(msg) { global.chatPost(msg); }

  function autosize() {
    input.style.height = 'auto';
    input.style.height = input.scrollHeight + 'px';
  }

  // While the agent works, typed text goes into the running turn; the button stops it when empty.
  function canQueue() {
    return state.busy && !state.shell && state.connected && !!input.value.trim();
  }

  function stopMode() { return state.busy && !canQueue(); }

  function refreshSend() {
    const stop = stopMode();
    sendBtn.classList.toggle('stop', stop);
    sendBtn.textContent = stop ? '■' : '↑';
    sendBtn.title = stop ? T('page.composer.stopTitle') :
      state.busy ? T('page.composer.steerTitle') : T('page.composer.sendTitle');
    sendBtn.disabled = stop ? false : !((state.connected || state.ready) && input.value.trim());
  }

  // followUp: while the agent works, send after the turn instead of at its next step.
  function submit(followUp) {
    const text = input.value.trim();
    if (!text || state.shell || !(state.connected || state.ready)) return;
    const id = 's' + (++nextSubmission);
    pending.set(id, { raw: input.value, text });
    post({ t: 'submit', id, text, followUp: !!followUp });
  }

  // The host's answer for submission id. Accepted: remember it and clear what was sent (a draft
  // typed since stays). Rejected: keep everything for another try.
  function submitted(id, ok) {
    const snap = pending.get(id);
    pending.delete(id);
    if (!snap || !ok) return;
    if (history[history.length - 1] !== snap.text) history.push(snap.text);
    if (history.length > MaxHistory) history.shift();
    historyIndex = -1;
    if (input.value === snap.raw) {
      input.value = '';
      autosize();
    }
    refreshSend();
  }

  function recall(delta) {
    if (!history.length) return false;
    if (historyIndex === -1) {
      if (delta > 0) return false;
      draft = input.value;
      historyIndex = history.length;
    }
    historyIndex += delta;
    if (historyIndex < 0) historyIndex = 0;
    if (historyIndex >= history.length) {
      historyIndex = -1;
      input.value = draft;
    } else {
      input.value = history[historyIndex];
    }
    autosize();
    refreshSend();
    return true;
  }

  // "@part" or '@"part with spaces' right before the caret: a column of the open table.
  function atToken() {
    const before = input.value.slice(0, input.selectionStart);
    const m = /(^|\s)@(?:"([^"@\r\n]*)|([^\s@"]*))$/.exec(before);
    if (!m) return null;
    const quoted = m[2] !== undefined;
    const part = quoted ? m[2] : m[3];
    return { start: before.length - part.length - (quoted ? 2 : 1), part: part.toLowerCase() };
  }

  function updateSlash() {
    const text = input.value;
    const at = atToken();
    if (at) {
      // The columns change with the open file: ask again every time an @ starts.
      if (!atOpen) { atOpen = true; post({ t: 'listColumns' }); }
      menuKind = 'at';
      atStart = at.start;
      const starts = [], inside = [];
      for (const c of columns) {
        const lower = c.name.toLowerCase();
        if (lower.startsWith(at.part)) starts.push(c);
        else if (lower.includes(at.part)) inside.push(c);
      }
      matches = starts.concat(inside).slice(0, MaxMenuRows);
      renderMenu();
      return;
    }
    atOpen = false;
    const m = /^\/(\S*)$/.exec(text);
    if (!m) { slash.hidden = true; return; }
    menuKind = 'slash';
    const prefix = m[1].toLowerCase();
    matches = commands.filter(c => c.name.toLowerCase().startsWith(prefix)).slice(0, 12);
    renderMenu();
  }

  function renderMenu() {
    if (!matches.length) { slash.hidden = true; return; }
    active = Math.min(active, matches.length - 1);
    slash.innerHTML = '';
    matches.forEach((c, i) => {
      const row = document.createElement('div');
      row.className = 'popup-item' + (i === active ? ' active' : '');
      row.innerHTML = '<span class="popup-name"></span><span class="popup-desc"></span>';
      row.firstChild.textContent = menuKind === 'at' ? '@' + c.name : '/' + c.name + (c.hint ? ' ' + c.hint : '');
      row.lastChild.textContent = c.description || '';
      row.addEventListener('mousedown', e => { e.preventDefault(); pick(i); });
      slash.appendChild(row);
      if (i === active) row.scrollIntoView({ block: 'nearest' });
    });
    slash.hidden = false;
  }

  // A column name with spaces (or quotes) goes in as @"name".
  function columnToken(name) {
    return /[\s"@]/.test(name) ? '@"' + name.replace(/"/g, '') + '"' : '@' + name;
  }

  function pick(i) {
    if (menuKind === 'at') {
      const after = input.value.slice(input.selectionStart);
      const token = columnToken(matches[i].name);
      input.value = input.value.slice(0, atStart) + token + ' ' + after;
      const caret = atStart + token.length + 1;
      input.setSelectionRange(caret, caret);
      slash.hidden = true;
      atOpen = false;
      input.focus();
      autosize();
      refreshSend();
      return;
    }
    input.value = '/' + matches[i].name + ' ';
    slash.hidden = true;
    input.focus();
    autosize();
    refreshSend();
  }

  function onKeyDown(e) {
    if (e.isComposing || e.keyCode === 229) return;
    if (!slash.hidden) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        active = (active + (e.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length;
        updateSlash();
        e.preventDefault();
        return;
      }
      if (e.key === 'Enter' || e.key === 'Tab') { pick(active); e.preventDefault(); return; }
      if (e.key === 'Escape') { slash.hidden = true; e.preventDefault(); return; }
    }
    if (e.key === 'Escape') {
      if (global.ChatPanels.isOpen()) return;
      if (state.busy && !input.value.trim()) post({ t: 'abort' });
      e.preventDefault();
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(e.ctrlKey); return; }
    const single = !input.value.includes('\n');
    if (e.key === 'ArrowUp' && single && input.selectionStart === 0 && recall(-1)) e.preventDefault();
    else if (e.key === 'ArrowDown' && single && historyIndex !== -1 && recall(1)) e.preventDefault();
  }

  // Chips above the box: the open file and the number of pending cell edits.
  function renderChips(ctx) {
    const chips = $('chips');
    chips.innerHTML = '';
    if (!ctx) return;
    if (ctx.file) {
      const file = document.createElement('span');
      file.className = 'ctx-chip';
      file.textContent = ctx.file;
      file.title = ctx.path || '';
      chips.appendChild(file);
    }
    if (ctx.edits > 0) {
      const ed = document.createElement('span');
      ed.className = 'ctx-chip';
      ed.textContent = T('page.composer.editsCount', ctx.edits);
      ed.title = T('page.composer.editsTitle');
      chips.appendChild(ed);
    }
  }

  function fillSelect(sel, values, current, labelFn) {
    const known = values.slice();
    if (current && !known.includes(current)) known.unshift(current);
    // Labels are translated, so a language change must rebuild the options too.
    const key = document.documentElement.lang + '\n' + known.join('\n');
    if (sel.dataset.key !== key) {
      sel.innerHTML = '';
      for (const v of known) {
        const opt = document.createElement('option');
        opt.value = v;
        opt.textContent = labelFn ? labelFn(v) : v;
        sel.appendChild(opt);
      }
      sel.dataset.key = key;
    }
    if (current) sel.value = current;
  }

  let catalog = { models: [], levels: [] };
  function status(msg) {
    state = msg;
    const idle = msg.connected && !msg.busy;
    global.ChatModelPicker.status(msg.model, idle);
    fillSelect($('thinking-select'), catalog.levels, msg.thinking, v => T('page.composer.thinkingLevel', v));
    $('thinking-select').disabled = !idle;
    // The approval mode select exists only when the host reports a mode.
    const approval = $('approval-select');
    approval.hidden = !msg.approval;
    if (msg.approval) approval.value = msg.approval;
    approval.classList.toggle('yolo', approval.value === 'yolo');
    // Locked by an extra omp argument (--approval-mode / --yolo): disabled, and the reason shows on hover.
    // A disabled <select> gets no hover events, so the reason tooltip lives on the wrapper.
    approval.disabled = !msg.connected || !!msg.approvalLocked;
    approval.classList.toggle('locked', !!msg.approvalLocked);
    $('approval-wrap').title = msg.approvalLocked || '';
    // The workspace file (.ncvws) tightened the agent settings: show a lock with the details on hover.
    const wsLimit = $('ws-limit');
    wsLimit.hidden = !msg.workspaceLimit;
    wsLimit.textContent = msg.workspaceLimit ? (msg.workspaceLimitLabel || '\u{1F512}') : '';
    wsLimit.title = msg.workspaceLimit || '';
    input.placeholder = msg.busy && !msg.shell ? T('page.composer.busyPlaceholder') : T('page.composer.placeholder');
    const pct = msg.context >= 0 ? Math.min(100, msg.context) : 0;
    $('ctx-arc').setAttribute('stroke-dasharray', (pct / 100 * 50.3).toFixed(1) + ' 50.3');
    $('ctx-ring').setAttribute('title', msg.context >= 0 ? T('page.composer.contextPct', pct.toFixed(0)) : T('page.composer.noContext'));
    const dot = $('conn-dot');
    dot.className = msg.error ? 'error' : msg.connected ? '' : 'off';
    dot.title = msg.state || '';
    refreshSend();
  }

  function setCatalog(msg) {
    catalog = { models: msg.models || [], levels: msg.levels || [] };
    global.ChatModelPicker.catalog(catalog.models, msg.providers);
    status(state);
  }

  function wire() {
    input = $('input');
    sendBtn = $('send-btn');
    slash = $('slash-menu');
    input.addEventListener('input', () => { active = 0; autosize(); updateSlash(); refreshSend(); });
    input.addEventListener('click', () => updateSlash());
    input.addEventListener('keydown', onKeyDown);
    input.addEventListener('blur', () => { setTimeout(() => { slash.hidden = true; atOpen = false; }, 150); });
    sendBtn.addEventListener('click', () => { if (stopMode()) post({ t: 'abort' }); else submit(false); });
    $('thinking-select').addEventListener('change', e => post({ t: 'setThinking', value: e.target.value }));
    $('approval-select').addEventListener('change', e => post({ t: 'setApproval', value: e.target.value }));
    input.focus();
  }

  function setInput(text) { input.value = text || ''; autosize(); refreshSend(); input.focus(); }

  function insertText(text) {
    const at = input.selectionStart;
    const before = input.value.slice(0, at);
    const sep = before && !/\s$/.test(before) ? ' ' : '';
    input.value = before + sep + text + input.value.slice(input.selectionEnd);
    autosize();
    refreshSend();
    input.focus();
  }

  // Column names: plain strings or {name, description}.
  function setColumns(items) {
    columns = (Array.isArray(items) ? items : []).map(it => typeof it === 'string'
      ? { name: it, description: '' }
      : { name: String((it && it.name) ?? ''), description: (it && it.description) || '' })
      .filter(c => c.name);
    if (menuKind === 'at' && atToken()) updateSlash();
  }

  global.ChatComposer = {
    wire, status, setCatalog, submitted, setInput, insertText,
    isBusy: () => !!state.busy || !state.connected,
    context: renderChips,
    commands: items => { commands = Array.isArray(items) ? items : []; },
    files: setColumns,
    focus: () => input && input.focus()
  };
})(typeof window !== 'undefined' ? window : globalThis);
