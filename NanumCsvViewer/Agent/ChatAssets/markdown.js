(function (global) {
  'use strict';

  function escapeHtml(str) {
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function escapeAttr(str) {
    return escapeHtml(str);
  }

  // Files the app opens when clicked: data files (csv/tsv/xlsx/sas7bdat/sav/json ...), reports (md) and pictures/pdf the agent
  // writes into the analysis folder. Text is HTML-escaped before it gets here, so '&' and ';' are excluded from names.
  // Groups: 1 absolute path, 2 its :row, 3 relative path, 4 its :row.
  const FILE_EXT = 'csv|tsv|tab|txt|xlsx|xlsm|xls|sas7bdat|sav|zsav|json|jsonl|ndjson|md|markdown|png|jpg|jpeg|gif|bmp|webp|svg|pdf';
  const NAME_CH = '[^\\s\\\\/:*?"<>|()&;]';
  const FILE_END = '(?![\\p{L}\\p{N}_]|\\.[\\p{L}\\p{N}])';
  const FILE_REF_RE = new RegExp(
    '([A-Za-z]:[\\\\/](?:' + NAME_CH + '+[\\\\/])*' + NAME_CH + '+\\.(?:' + FILE_EXT + ')' + FILE_END + ')(?::(\\d+))?' +
    '|(?<![\\p{L}\\p{N}_.\\\\/:-])((?:' + NAME_CH + '+[\\\\/])*' + NAME_CH + '+\\.(?:' + FILE_EXT + ')' + FILE_END + ')(?::(\\d+))?',
    'giu');

  function linkFileRefs(text, wrap) {
    if (!text) return '';
    FILE_REF_RE.lastIndex = 0;
    return text.replace(FILE_REF_RE, (match, p1, l1, p2, l2) => {
      const path = p1 || p2;
      const line = parseInt(l1 || l2 || '0', 10);
      const html = `<span class="file-ref" data-path="${path}" data-line="${line}">${match}</span>`;
      return wrap ? wrap(html) : html;
    });
  }

  // Pictures in answers/reports: ![title](figure.png). Only a relative path to a picture file under Markdown.imageBase
  // (the analysis folder in the chat, the report's folder in the viewer) is shown. Web URLs, absolute paths and ".." are
  // never loaded: a model-written answer must not make the page fetch from anywhere else. Anything else renders as [title].
  const IMG_EXT_RE = /\.(png|jpe?g|gif|bmp|webp|svg)$/i;
  function resolveImage(src) {
    const base = Markdown.imageBase;
    if (!base) return null;
    let p = String(src).replace(/&amp;/g, '&');
    try { p = decodeURIComponent(p); } catch (e) { return null; }
    p = p.replace(/\\/g, '/');
    if (/^[a-z][a-z0-9+.-]*:/i.test(p) || p.startsWith('/')) return null;
    const parts = p.split('/').filter(s => s !== '' && s !== '.');
    if (!parts.length || parts.some(s => s === '..') || !IMG_EXT_RE.test(parts[parts.length - 1])) return null;
    const rel = parts.join('/');
    const url = base + parts.map(encodeURIComponent).join('/') + (Markdown.imageVersion ? '?v=' + Markdown.imageVersion : '');
    return { url, rel };
  }

  function renderImage(alt, src) {
    const r = resolveImage(src);
    if (!r) return `<span class="img-missing">[${alt}]</span>`;
    return `<img class="chat-img" src="${r.url}" alt="${alt}" data-path="${escapeAttr(r.rel)}" loading="lazy">`;
  }

  // Light highlighting for the languages an analysis answer shows: comments, strings, numbers, keywords.
  const WORDS = s => new Set(s.split(' '));
  const LANGS = {
    sql: {
      ci: true,
      comment: '--[^\\r\\n]*|\\/\\*[\\s\\S]*?(?:\\*\\/|$)',
      kw: WORDS('select from where group by order having join left right inner outer full cross on as and or not in is null like between case when then else end insert into values update set delete create table drop alter distinct limit offset union all with count sum avg min max over partition asc desc exists true false')
    },
    python: {
      comment: '#[^\\r\\n]*',
      kw: WORDS('and as assert async await break class continue def del elif else except finally for from global if import in is lambda None nonlocal not or pass raise return True False try while with yield')
    },
    r: {
      comment: '#[^\\r\\n]*',
      kw: WORDS('if else repeat while function for next break in TRUE FALSE NULL NA NA_integer_ NA_real_ NA_character_ Inf NaN library require')
    },
    json: { comment: '(?!)', kw: WORDS('true false null') }
  };
  const LANG_ALIAS = { sql: 'sql', py: 'python', python: 'python', r: 'r', json: 'json', jsonc: 'json', jsonl: 'json' };
  const tokenRes = {};

  function tokenRe(spec, name) {
    return tokenRes[name] || (tokenRes[name] = new RegExp(
      '(' + spec.comment + ')|("(?:[^"\\\\\\r\\n]|\\\\.)*(?:"|$)|\'(?:[^\'\\\\\\r\\n]|\\\\.)*(?:\'|$))' +
      '|(\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b)|(\\b[A-Za-z_][A-Za-z0-9_]*\\b)|(\\s+|.)', 'g'));
  }

  function highlightCode(lang, code) {
    const name = LANG_ALIAS[lang];
    if (!name) return linkFileRefs(escapeHtml(code));
    const spec = LANGS[name];
    const re = tokenRe(spec, name);
    re.lastIndex = 0;
    let out = '';
    let m;
    while ((m = re.exec(code)) !== null) {
      const [full, comm, str, num, ident] = m;
      if (comm) out += `<span class="tok-comment">${linkFileRefs(escapeHtml(comm))}</span>`;
      else if (str) out += `<span class="tok-string">${linkFileRefs(escapeHtml(str))}</span>`;
      else if (num) out += `<span class="tok-number">${escapeHtml(num)}</span>`;
      else if (ident) {
        const word = spec.ci ? ident.toLowerCase() : ident;
        out += spec.kw.has(word) ? `<span class="tok-keyword">${escapeHtml(ident)}</span>` : escapeHtml(ident);
      } else out += escapeHtml(full);
    }
    return out;
  }

  function renderInline(raw) {
    let text = escapeHtml(raw);
    const ph = [];
    const pushPh = (h) => {
      ph.push(h);
      return `\x00PH${ph.length - 1}\x00`;
    };

    // 1. Code spans `code`
    text = text.replace(/`([^`]+)`/g, (_, code) => pushPh(`<code>${linkFileRefs(code)}</code>`));

    // 1b. Pictures ![title](figure.png) (before links, which would otherwise swallow the [title](...) part)
    text = text.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_, alt, src) => pushPh(renderImage(alt, src)));

    // 2. Markdown links [text](url)
    text = text.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, url) => pushPh(`<a href="#" class="chat-link" data-url="${url}">${label}</a>`));

    // 3. Bare URLs http:// or https://
    text = text.replace(/\b(https?:\/\/[^\s<>()"']+)/g, (_, url) => pushPh(`<a href="#" class="chat-link" data-url="${url}">${url}</a>`));

    // 4. File references
    text = linkFileRefs(text, pushPh);

    // 5. Bold, italic, strikethrough
    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/__([^_]+)__/g, '<strong>$1</strong>');
    text = text.replace(/\*([^*]+)\*/g, '<em>$1</em>');
    text = text.replace(/_([^_]+)_/g, '<em>$1</em>');
    text = text.replace(/~~([^~]+)~~/g, '<del>$1</del>');

    // 6. Restore placeholders
    text = text.replace(/\x00PH(\d+)\x00/g, (_, i) => ph[parseInt(i, 10)]);
    return text;
  }

  function parseTable(lines, startIdx) {
    const headerLine = lines[startIdx];
    const delimLine = lines[startIdx + 1];
    if (!delimLine) return null;
    const delimCols = delimLine.trim().replace(/^\||\|$/g, '').split('|').map(s => s.trim());
    if (delimCols.length === 0 || !delimCols.every(c => /^:?-+:?$/.test(c))) return null;

    const aligns = delimCols.map(c => {
      const l = c.startsWith(':');
      const r = c.endsWith(':');
      return l && r ? 'center' : r ? 'right' : l ? 'left' : '';
    });

    const headerCols = headerLine.trim().replace(/^\||\|$/g, '').split('|').map(s => s.trim());
    let html = '<table><thead><tr>';
    for (let i = 0; i < delimCols.length; i++) {
      const align = aligns[i] ? ` style="text-align:${aligns[i]}"` : '';
      html += `<th${align}>${renderInline(headerCols[i] || '')}</th>`;
    }
    html += '</tr></thead><tbody>';

    let nextIdx = startIdx + 2;
    while (nextIdx < lines.length) {
      const line = lines[nextIdx].trim();
      if (!line || !line.includes('|')) break;
      const rowCols = line.replace(/^\||\|$/g, '').split('|').map(s => s.trim());
      html += '<tr>';
      for (let i = 0; i < delimCols.length; i++) {
        const align = aligns[i] ? ` style="text-align:${aligns[i]}"` : '';
        html += `<td${align}>${renderInline(rowCols[i] || '')}</td>`;
      }
      html += '</tr>';
      nextIdx++;
    }
    html += '</tbody></table>';
    return { html, nextIdx };
  }

  function parseList(lines, startIdx) {
    let idx = startIdx;
    const items = [];
    while (idx < lines.length) {
      const line = lines[idx];
      const m = line.match(/^(\s*)([-*+]|\d+\.)\s+(.*)$/);
      if (!m) break;
      items.push({ indent: m[1].length, isOrdered: /^\d+\./.test(m[2]), text: m[3] });
      idx++;
    }
    if (items.length === 0) return null;

    const rootTag = items[0].isOrdered ? 'ol' : 'ul';
    let html = `<${rootTag}>`;
    let inSub = false;
    let subTag = 'ul';

    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      const isSub = it.indent >= 2;
      if (isSub && !inSub) {
        inSub = true;
        subTag = it.isOrdered ? 'ol' : 'ul';
        html += `<${subTag}>`;
      } else if (!isSub && inSub) {
        html += `</${subTag}></li>`;
        inSub = false;
      } else if (i > 0 && !isSub) {
        html += '</li>';
      }
      html += `<li>${renderInline(it.text)}`;
    }
    if (inSub) html += `</${subTag}></li>`;
    else html += '</li>';
    html += `</${rootTag}>`;
    return { html, nextIdx: idx };
  }

  function render(markdown) {
    if (!markdown) return '';
    const lines = markdown.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
    let html = '';
    let idx = 0;

    while (idx < lines.length) {
      const line = lines[idx];

      // Empty line
      if (!line.trim()) {
        idx++;
        continue;
      }

      // Fenced code block; list items indent theirs, so up to 4 leading spaces count.
      const codeMatch = line.match(/^( {0,4})```\s*([a-zA-Z0-9_\-+]*)\s*$/);
      if (codeMatch) {
        const indent = codeMatch[1].length;
        const lang = codeMatch[2].toLowerCase();
        const codeLines = [];
        idx++;
        while (idx < lines.length) {
          if (/^ {0,4}```\s*$/.test(lines[idx])) {
            idx++;
            break;
          }
          const raw = lines[idx];
          codeLines.push(raw.slice(Math.min(indent, raw.length - raw.trimStart().length)));
          idx++;
        }
        const rawCode = codeLines.join('\n');
        const highlighted = highlightCode(lang, rawCode);

        const copyLabel = typeof global.T === 'function' ? global.T('page.markdown.copy') : 'Copy';
        html += `<div class="code-block">` +
          `<div class="code-header">` +
            `<span class="code-lang">${escapeHtml(lang || 'text')}</span>` +
            `<button class="code-copy-btn" data-code="${escapeAttr(rawCode)}" aria-label="${escapeAttr(copyLabel)}">${escapeHtml(copyLabel)}</button>` +
          `</div>` +
          `<pre><code>${highlighted}</code></pre>` +
        `</div>`;
        continue;
      }

      // Heading
      const headMatch = line.match(/^(#{1,6})\s+(.+)$/);
      if (headMatch) {
        const level = headMatch[1].length;
        html += `<h${level}>${renderInline(headMatch[2])}</h${level}>`;
        idx++;
        continue;
      }

      // Horizontal rule
      if (/^(?:---|\*\*\*|___)\s*$/.test(line)) {
        html += '<hr>';
        idx++;
        continue;
      }

      // Blockquote
      if (/^>\s?/.test(line)) {
        const qLines = [];
        while (idx < lines.length && /^>\s?/.test(lines[idx])) {
          qLines.push(lines[idx].replace(/^>\s?/, ''));
          idx++;
        }
        html += `<blockquote>${qLines.map(l => renderInline(l)).join('<br>')}</blockquote>`;
        continue;
      }

      // Table
      if (line.includes('|') && idx + 1 < lines.length && /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$/.test(lines[idx + 1])) {
        const tableRes = parseTable(lines, idx);
        if (tableRes) {
          html += tableRes.html;
          idx = tableRes.nextIdx;
          continue;
        }
      }

      // List
      if (/^\s*([-*+]|\d+\.)\s+/.test(line)) {
        const listRes = parseList(lines, idx);
        if (listRes) {
          html += listRes.html;
          idx = listRes.nextIdx;
          continue;
        }
      }

      // Paragraph
      const paraLines = [];
      while (idx < lines.length) {
        const pLine = lines[idx];
        if (!pLine.trim()) break;
        // The first line is always taken: a line no block accepted ("# " while streaming) must
        // still move idx forward, or render() never returns.
        if (paraLines.length && (pLine.trimStart().startsWith('```') || /^#{1,6}\s+/.test(pLine) ||
            /^>\s?/.test(pLine) || /^\s*([-*+]|\d+\.)\s+/.test(pLine) || /^(?:---|\*\*\*|___)\s*$/.test(pLine))) {
          break;
        }
        if (paraLines.length && pLine.includes('|') && idx + 1 < lines.length &&
            /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$/.test(lines[idx + 1])) {
          break;
        }
        paraLines.push(pLine);
        idx++;
      }
      if (paraLines.length > 0) {
        html += `<p>${paraLines.map(l => renderInline(l)).join('<br>')}</p>`;
      }
    }

    return html;
  }

  const Markdown = {
    imageBase: '',
    imageVersion: '',
    resolveImage,
    escapeHtml,
    escapeAttr,
    linkFileRefs,
    highlightCode,
    renderInline,
    render
  };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = Markdown;
  }
  global.Markdown = Markdown;
})(typeof window !== 'undefined' ? window : globalThis);
