(function (global) {
  'use strict';

  // Files and folders added to the workspace from the chat: the + menu in the message box, drag and
  // drop onto the page, the chips above the text (what the next message carries) and the chips in
  // a sent message's bubble. The host opens the file/folder dialog, registers the files as
  // workspace sources (no tabs) and answers with "attached". A drop is handed to the host as File
  // objects (postMessageWithAdditionalObjects), from which only the host reads the paths; the page
  // never navigates to a dropped file. Removing a chip only drops it from the next message; the
  // source stays in the workspace. The message sent to omp names the files and tables, never their
  // contents.
  let chips = [];   // { name, path, label, tables }
  function T(key, ...args) {
    return global.T ? global.T(key, ...args) : key;
  }

  function $(id) { return document.getElementById(id); }
  function post(msg) { global.chatPost(msg); }

  function tip(item) {
    const tables = (item.tables || []).join(', ');
    return tables ? T('page.composer.attachTables', tables) : (item.path || item.name || '');
  }

  // One chip: removable above the box (onRemove), plain in a sent bubble.
  function chipEl(item, onRemove) {
    const chip = document.createElement('span');
    chip.className = 'attach-chip';
    chip.title = tip(item);
    const label = document.createElement('span');
    label.className = 'attach-name';
    label.textContent = item.label || ('\u{1F4CE} ' + item.name);
    chip.appendChild(label);
    if (onRemove) {
      const x = document.createElement('button');
      x.type = 'button';
      x.className = 'attach-x';
      x.textContent = '×';
      x.title = T('page.composer.attachRemove');
      x.setAttribute('aria-label', T('page.composer.attachRemove'));
      x.addEventListener('click', onRemove);
      chip.appendChild(x);
    }
    return chip;
  }

  function render() {
    const box = $('attach-chips');
    if (!box) return;
    box.innerHTML = '';
    for (const item of chips) {
      box.appendChild(chipEl(item, () => { chips = chips.filter(c => c !== item); render(); }));
    }
  }

  // Registered sources from the host. A file that is already a chip is replaced, not repeated.
  function add(items) {
    for (const it of Array.isArray(items) ? items : []) {
      if (!it || !it.name || !Array.isArray(it.tables) || !it.tables.length) continue;
      const key = (it.path || it.name).toLowerCase();
      chips = chips.filter(c => (c.path || c.name).toLowerCase() !== key);
      chips.push({ name: it.name, path: it.path || '', label: it.label || '', tables: it.tables });
    }
    render();
  }

  function snapshot() { return chips.slice(); }

  // The message left: its chips go, chips added since stay.
  function sent(items) {
    chips = chips.filter(c => !items.includes(c));
    render();
  }

  // The chips of a sent message, above its text.
  function bubbleChips(items) {
    if (!Array.isArray(items) || !items.length) return null;
    const row = document.createElement('div');
    row.className = 'user-attach';
    for (const item of items) row.appendChild(chipEl(item, null));
    return row;
  }

  // The drop hint covers the page while files are dragged over it.
  function dropHint(on) {
    const overlay = $('drop-overlay');
    if (overlay) overlay.hidden = !on;
  }

  function wireDrop() {
    const carriesFiles = e => !!e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
    let depth = 0;
    window.addEventListener('dragenter', e => {
      if (!carriesFiles(e)) return;
      e.preventDefault();
      depth++;
      dropHint(true);
    });
    window.addEventListener('dragover', e => {
      if (!carriesFiles(e)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
    });
    window.addEventListener('dragleave', e => {
      if (!carriesFiles(e)) return;
      if (--depth <= 0) { depth = 0; dropHint(false); }
    });
    window.addEventListener('drop', e => {
      if (!carriesFiles(e)) return;
      e.preventDefault();
      depth = 0;
      dropHint(false);
      const dropped = Array.from(e.dataTransfer.files);
      const host = global.chrome && global.chrome.webview;
      if (dropped.length && host && host.postMessageWithAdditionalObjects) {
        host.postMessageWithAdditionalObjects({ t: 'attachDrop' }, dropped);
      }
    });
  }

  function wire() {
    const btn = $('attach-btn');
    const menu = $('attach-menu');
    btn.addEventListener('click', e => { e.stopPropagation(); menu.hidden = !menu.hidden; });
    menu.querySelectorAll('[data-kind]').forEach(row => {
      row.addEventListener('click', () => {
        menu.hidden = true;
        post({ t: 'attach', kind: row.dataset.kind });
        global.ChatComposer.focus();
      });
    });
    document.addEventListener('click', e => { if (!menu.hidden && !menu.contains(e.target)) menu.hidden = true; });
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && !menu.hidden) { menu.hidden = true; e.stopPropagation(); } }, true);
    wireDrop();
    render();
  }

  global.ChatAttach = { wire, add, snapshot, sent, bubbleChips };
})(typeof window !== 'undefined' ? window : globalThis);
