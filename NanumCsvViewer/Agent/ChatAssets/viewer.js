(function (global) {
  'use strict';

  // Host messages: strings (same table as the chat), theme (same CSS variables), doc {text, base, version, name}.
  let stringDict = {};
  const has = key => Object.prototype.hasOwnProperty.call(stringDict, key);
  global.T = function (key, ...args) {
    let str = has(key) ? stringDict[key] : key;
    args.forEach((arg, i) => { str = str.split('{' + i + '}').join(arg); });
    return str;
  };
  global.chatPost = function (msg) {
    if (global.chrome && global.chrome.webview) global.chrome.webview.postMessage(msg);
  };

  function applyTheme(vars) {
    if (!vars) return;
    const root = document.documentElement;
    for (const [key, val] of Object.entries(vars)) {
      if (key === 'scheme') { root.style.colorScheme = val; continue; }
      root.style.setProperty('--' + key, key === 'fontSize' && typeof val === 'number' ? val + 'px' : val);
    }
  }

  // Re-rendering keeps the reading position (the file may be rewritten while it is open).
  function render(msg) {
    const doc = document.getElementById('doc');
    const scroller = document.scrollingElement || document.documentElement;
    const top = scroller.scrollTop;
    global.Markdown.imageBase = msg.base || '';
    global.Markdown.imageVersion = msg.version ? String(msg.version) : '';
    doc.innerHTML = global.Markdown.render(msg.text || '');
    if (msg.name) document.title = msg.name;
    scroller.scrollTop = top;
  }

  function handle(msg) {
    if (!msg || typeof msg !== 'object') return;
    switch (msg.t) {
      case 'strings': if (msg.items && typeof msg.items === 'object') stringDict = msg.items; break;
      case 'theme': applyTheme(msg.vars); break;
      case 'doc': render(msg); break;
    }
  }

  if (global.chrome && global.chrome.webview) {
    global.chrome.webview.addEventListener('message', e => handle(e.data));
  }
  global.__viewerHost = handle;
  window.addEventListener('DOMContentLoaded', () => global.chatPost({ t: 'ready' }));
})(typeof window !== 'undefined' ? window : globalThis);
