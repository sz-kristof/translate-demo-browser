// Injected into every page. Splits the page into translation units, hands them to the
// host one batch at a time (visible first), and swaps translations in / out.
(() => {
  if (window.__tk) return;

  const BLOCK = new Set(['P', 'LI', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'TD', 'TH', 'DT', 'DD',
    'BLOCKQUOTE', 'FIGCAPTION', 'CAPTION', 'LABEL', 'BUTTON', 'SUMMARY', 'LEGEND', 'DIV', 'SECTION',
    'ARTICLE', 'HEADER', 'FOOTER', 'NAV', 'ASIDE', 'MAIN', 'FORM', 'UL', 'OL', 'TABLE', 'TR', 'BODY']);
  const SKIP = 'script,style,noscript,code,pre,kbd,samp,textarea,input,select,svg,math,iframe,' +
    'object,template,[translate="no"],.notranslate,[contenteditable="true"]';
  const MAX_HTML = 4000;

  let units = new Map();      // id -> { el, node, kind: 'html'|'text'|'title', original }
  let seen = new WeakSet();   // text nodes already queued, so later passes only pick up new text
  let nextId = 1;

  // Set when the site changes the page (pop-ups, infinite scroll), so the host only
  // re-scans when there is something new. Our own apply()/restore() edits are dropped
  // with takeRecords() before the observer callback can see them.
  let dirty = false;
  const observer = new MutationObserver(() => { dirty = true; });
  observer.observe(document.documentElement, { childList: true, subtree: true, characterData: true });

  const hasText = s => /[\p{L}]/u.test(s);
  const skipped = el => el.closest(SKIP) !== null;

  // Nearest block-level ancestor of a text node: the natural unit of context.
  function blockOf(node) {
    let el = node.parentElement;
    while (el && !BLOCK.has(el.tagName)) el = el.parentElement;
    return el || document.body;
  }

  function collect() {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode: n => (!seen.has(n) && hasText(n.data) && n.parentElement && !skipped(n.parentElement) &&
        !n.parentElement.closest('[data-tk]')) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT,
    });
    const blocks = new Map();   // block element -> [text nodes]
    for (let n; (n = walker.nextNode());) {
      seen.add(n);
      const b = blockOf(n);
      if (!blocks.has(b)) blocks.set(b, []);
      blocks.get(b).push(n);
    }
    // Blocks that contain another text-bearing block (walk up once from each block).
    const containsBlock = new Set();
    for (const b of blocks.keys()) {
      for (let p = b.parentElement; p && !containsBlock.has(p); p = p.parentElement) containsBlock.add(p);
    }
    const out = [];
    for (const [block, nodes] of blocks) {
      // A block is translated as one HTML fragment (keeps inline tags + context) unless it
      // also contains other blocks or is huge; then its own text runs go one by one.
      if (!containsBlock.has(block) && block !== document.body && block.innerHTML.length <= MAX_HTML) {
        const id = String(nextId++);
        block.setAttribute('data-tk', id);
        units.set(id, { el: block, kind: 'html', original: block.innerHTML });
        out.push({ id, html: block.innerHTML, top: block.getBoundingClientRect().top });
      } else {
        for (const n of nodes) {
          const id = String(nextId++);
          units.set(id, { node: n, kind: 'text', original: n.data });
          out.push({ id, text: n.data, top: n.parentElement.getBoundingClientRect().top });
        }
      }
    }
    // Viewport first, then the rest of the page top to bottom.
    const vh = window.innerHeight;
    const rank = u => (u.top >= -50 && u.top < vh ? 0 : 1) * 1e7 + Math.max(u.top, -1e6);
    out.sort((a, b) => rank(a) - rank(b));
    if (hasText(document.title) && ![...units.values()].some(u => u.kind === 'title')) {
      const id = String(nextId++);
      units.set(id, { kind: 'title', original: document.title });
      out.push({ id, text: document.title, top: -1e6 });
    }
    return out.map(({ top, ...u }) => ({ ...u, vis: top >= -50 && top < vh }));
  }

  function apply(results) {
    for (const r of results) {
      const u = units.get(r.id);
      if (!u) continue;
      if (u.kind === 'html') { u.el.innerHTML = r.html; u.el.setAttribute('data-tk-done', ''); }
      else if (u.kind === 'title') document.title = r.text;
      else {
        // keep the original's leading/trailing whitespace so words don't glue together
        const m = u.original.match(/^(\s*)[\s\S]*?(\s*)$/);
        u.node.data = m[1] + r.text.trim() + m[2];
      }
      // innerHTML replaced the block's text nodes; don't pick the translations up again
      if (u.el) for (const w = document.createTreeWalker(u.el, NodeFilter.SHOW_TEXT); w.nextNode();) seen.add(w.currentNode);
    }
    observer.takeRecords();
  }

  function restore() {
    for (const u of units.values()) {
      if (u.kind === 'html') { u.el.innerHTML = u.original; u.el.removeAttribute('data-tk'); u.el.removeAttribute('data-tk-done'); }
      else if (u.kind === 'title') document.title = u.original;
      else u.node.data = u.original;
    }
    units = new Map();
    seen = new WeakSet();
    observer.takeRecords();
    dirty = false;
  }

  function sample() {
    const t = (document.body ? document.body.innerText : '') || '';
    return JSON.stringify({ lang: document.documentElement.lang || '', title: document.title, text: t.slice(0, 5000) });
  }

  window.__tk = {
    collect: () => JSON.stringify(collect()),
    apply: results => { apply(results); return results.length; },
    restore,
    sample,
    takeDirty: () => { const d = dirty; dirty = false; return d; },
  };
})();
