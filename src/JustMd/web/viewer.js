// JustMd page script.
// The host (C#) sends rendered Markdown as HTML via web messages. That HTML comes from an
// untrusted document, so it is always sanitised with DOMPurify before it touches the DOM.
// Links never navigate: every click is reported to the host, which decides what to do.
(function () {
  'use strict';

  var webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;

  // Must match MarkdownRenderer.HeadingIdPrefix in JustMd.Core.
  var HEADING_ID_PREFIX = 'user-content-';
  var OUTLINE_MIN = 160;
  var OUTLINE_MAX = 640;
  var OUTLINE_DEFAULT = 264;

  var el = {
    root: document.documentElement,
    body: document.body,
    content: document.getElementById('content'),
    editor: document.getElementById('editor'),
    scroller: document.getElementById('scroller'),
    welcome: document.getElementById('welcome'),
    error: document.getElementById('error'),
    errorMessage: document.getElementById('error-message'),
    errorPath: document.getElementById('error-path'),
    tabs: document.getElementById('tabs'),
    appTitle: document.getElementById('app-title'),
    outline: document.getElementById('outline'),
    outlineTree: document.getElementById('outline-tree'),
    outlineEmpty: document.getElementById('outline-empty'),
    outlineResizer: document.getElementById('outline-resizer'),
    btnOutline: document.getElementById('btn-outline'),
    btnTheme: document.getElementById('btn-theme'),
    btnEdit: document.getElementById('btn-edit'),
    dropOverlay: document.getElementById('drop-overlay')
  };

  var imageBase = null;
  var copyCounter = 0;
  var pendingCopies = Object.create(null);
  var dragDepth = 0;

  // The host owns the tabs; the page shows one tab at a time and reports its view state
  // (scroll, outline) so the host can restore it exactly when the tab is shown again.
  var currentTabId = 0;
  var contentTabId = 0; // tab whose document is in #content (and whose headings fill the outline)
  var editExit = null;  // { tabId, scrollTop } from leaving the editor, applied by the next render
  var tooLargeToEdit = false;
  var editorHooks = null; // set by editor.js: { message(msg), tabs(tabs), hidden() }
  var devSink = null;     // dev-only stand-in for the host in a plain browser (see editor.js)
  var reportTimer = 0;
  var pendingRestore = null; // { top, until } re-applied while images load, until the user scrolls

  // Outline state. It lives here (not in the DOM) so hiding the panel or re-rendering the
  // same document never loses expanded/collapsed nodes, scroll position or the active item.
  var outline = {
    visible: false,
    width: OUTLINE_DEFAULT,
    collapsed: new Set(),// node keys (heading id or text path)
    nodes: [],           // flat list in document order: { key, heading, li, row, parent, children }
    byHeading: new Map(),
    activeNode: null,
    focusNode: null,
    observer: null,
    savedScrollTop: 0
  };

  var THEME_LABELS = { system: 'System', light: 'Light', dark: 'Dark' };
  var COPY_ICON = '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h8"/></svg>';
  var CHECK_ICON = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>';
  var CHEVRON_ICON = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M6 4l4 4-4 4"/></svg>';

  // Tags that never make sense in a viewed document. DOMPurify's html profile already drops
  // script, svg and math; this list removes the rest of the interactive / embedding surface.
  var PURIFY_CONFIG = {
    USE_PROFILES: { html: true },
    FORBID_TAGS: ['style', 'script', 'noscript', 'template', 'form', 'button', 'select', 'option',
      'optgroup', 'textarea', 'datalist', 'output', 'fieldset', 'iframe', 'frame', 'frameset',
      'object', 'embed', 'applet', 'base', 'meta', 'link', 'dialog', 'video', 'audio', 'source',
      'track', 'canvas', 'portal', 'slot', 'marquee'],
    FORBID_ATTR: ['style', 'target', 'formaction', 'form', 'srcset', 'ping', 'action', 'autofocus',
      'contenteditable', 'popover', 'accesskey', 'tabindex', 'background', 'xmlns'],
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: true,
    RETURN_DOM_FRAGMENT: true
  };

  // ---------- host messaging ----------

  function post(message) {
    if (webview) {
      webview.postMessage(message);
    } else if (devSink) {
      devSink(message);
    }
  }

  function onHostMessage(event) {
    var msg = event.data;
    if (!msg || typeof msg.type !== 'string') {
      return;
    }

    switch (msg.type) {
      case 'render': render(msg); break;
      case 'welcome': showWelcome(); break;
      case 'error': showError(msg); break;
      case 'tabs':
        renderTabs(msg.tabs || [], msg.activeTabId || 0);
        if (editorHooks) {
          editorHooks.tabs(msg.tabs || []);
        }
        break;
      case 'theme': applyTheme(msg.theme, msg.mode); break;
      case 'outline':
        setOutlineWidth(msg.width, false);
        setOutlineVisible(!!msg.visible, false);
        break;
      case 'toggleOutline': setOutlineVisible(!outline.visible, true); break;
      case 'copied': onCopied(msg.id, !!msg.ok); break;
      case 'scrollTo': scrollToFragment(msg.fragment); break;
      case 'selectAll': selectAllContent(); break;
      default:
        // edit, exitEdit, editorCommand, ... belong to editor.js.
        if (editorHooks) {
          editorHooks.message(msg);
        }
    }
  }

  // ---------- states ----------

  function showOnly(section) {
    var wasEditing = !el.editor.hidden;
    var editing = section === el.editor;
    if (wasEditing && !editing && editorHooks) {
      editorHooks.hidden(); // still laid out here, so the editor can read its scroll position
    }

    el.content.hidden = section !== el.content;
    el.editor.hidden = !editing;
    el.welcome.hidden = section !== el.welcome;
    el.error.hidden = section !== el.error;
    // No document, no outline (the user's show/hide preference is left untouched).
    el.body.classList.toggle('no-document', section !== el.content && !editing);
    el.body.classList.toggle('editing', editing);
    el.btnOutline.disabled = editing;
    if (wasEditing && !editing && outline.visible) {
      el.outline.scrollTop = outline.savedScrollTop; // display:none dropped it
    }
    updateEditButton();
  }

  function updateEditButton() {
    var editing = !el.editor.hidden;
    el.btnEdit.hidden = el.content.hidden && !editing;
    el.btnEdit.disabled = tooLargeToEdit && !editing;
    el.btnEdit.setAttribute('aria-pressed', editing ? 'true' : 'false');
    el.btnEdit.title = editing ? 'Finish editing (F2)' : (tooLargeToEdit ? 'Too large to edit' : 'Edit (F2)');
  }

  // Shows the editor root in place of #content. Only editor.js calls this, and only in response
  // to an "edit" message from the host.
  function showEditor(tabId, base) {
    if (!el.content.hidden) {
      flushTabState();
      outline.savedScrollTop = el.outline.scrollTop;
    }
    switchToTab(tabId);
    imageBase = base;
    editExit = null;
    tooLargeToEdit = false;
    showOnly(el.editor);
  }

  // The host ended editing for the shown tab; its "render" (or "welcome") follows straight away.
  function leaveEditor() {
    if (el.editor.hidden) {
      return;
    }
    editExit = { tabId: currentTabId, scrollTop: el.scroller.scrollTop };
    if (contentTabId !== currentTabId) {
      el.content.replaceChildren(); // a different tab's document: never show it, even briefly
    }
    showOnly(el.content);
  }

  // Called before the page switches to another tab: send the old tab's view state now.
  function switchToTab(tabId) {
    if (tabId === currentTabId) {
      return false;
    }
    if (currentTabId) {
      flushTabState();
    }
    currentTabId = tabId;
    return true;
  }

  function clearDocument() {
    imageBase = null;
    pendingRestore = null;
    editExit = null;
    contentTabId = 0;
    tooLargeToEdit = false;
    el.content.replaceChildren();
    outline.collapsed.clear();
    outline.savedScrollTop = 0;
    buildOutline(false);
    el.scroller.scrollTop = 0;
  }

  function showWelcome() {
    clearTimeout(reportTimer);
    currentTabId = 0; // the tab was closed; nothing to report
    clearDocument();
    showOnly(el.welcome);
  }

  function showError(msg) {
    switchToTab(msg.tabId || 0);
    clearDocument();
    el.errorMessage.textContent = msg.message || 'The file could not be read.';
    el.errorPath.textContent = msg.filePath || '';
    showOnly(el.error);
  }

  // ---------- tab view state ----------

  function currentTabState() {
    return {
      type: 'tabState',
      tabId: currentTabId,
      scrollTop: Math.round(el.scroller.scrollTop),
      outlineScrollTop: Math.round(outline.visible ? el.outline.scrollTop : outline.savedScrollTop),
      collapsed: Array.from(outline.collapsed)
    };
  }

  function flushTabState() {
    clearTimeout(reportTimer);
    reportTimer = 0;
    if (currentTabId && !el.content.hidden) {
      post(currentTabState());
    }
  }

  function scheduleTabState() {
    if (!currentTabId) {
      return;
    }
    clearTimeout(reportTimer);
    reportTimer = setTimeout(flushTabState, 500);
  }

  function restoreScroll(top) {
    el.scroller.scrollTop = top;
    // Images finishing loading can still shift the layout; re-apply briefly unless the user scrolls.
    pendingRestore = top > 0 ? { top: top, until: Date.now() + 2000 } : null;
  }

  function onContentLoad(e) {
    if (pendingRestore && e.target && e.target.tagName === 'IMG') {
      if (Date.now() > pendingRestore.until) {
        pendingRestore = null;
      } else {
        el.scroller.scrollTop = pendingRestore.top;
      }
    }
  }

  function cancelPendingRestore() {
    pendingRestore = null;
  }

  // ---------- tab strip ----------

  var CLOSE_ICON = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4.5 4.5l7 7M11.5 4.5l-7 7"/></svg>';

  function renderTabs(tabs, activeId) {
    el.tabs.replaceChildren();
    el.appTitle.hidden = tabs.length > 0;
    var activeElement = null;

    tabs.forEach(function (tab) {
      var item = document.createElement('div');
      item.className = 'tab';
      item.setAttribute('role', 'tab');
      item.dataset.id = String(tab.id);
      item.title = tab.untitled ? '' : (tab.path || tab.name);
      item.classList.toggle('editing', !!tab.editing);
      var active = tab.id === activeId;
      item.setAttribute('aria-selected', active ? 'true' : 'false');
      item.tabIndex = active ? 0 : -1;
      if (active) {
        item.classList.add('active');
        activeElement = item;
      }

      var dot = document.createElement('span');
      dot.className = tab.dirty ? 'tab-dot dirty' : 'tab-dot';
      dot.hidden = !tab.updated && !tab.dirty;
      dot.title = tab.dirty ? 'Unsaved changes' : 'Updated on disk';

      var name = document.createElement('span');
      name.className = 'tab-name';
      name.textContent = tab.name;

      var close = document.createElement('button');
      close.type = 'button';
      close.className = 'tab-close';
      close.tabIndex = -1;
      close.title = 'Close (Ctrl+W)';
      close.setAttribute('aria-label', 'Close ' + tab.name);
      close.innerHTML = CLOSE_ICON;
      close.addEventListener('click', function (e) {
        e.stopPropagation();
        post({ type: 'closeTab', tabId: tab.id });
      });

      item.appendChild(dot);
      item.appendChild(name);
      item.appendChild(close);

      item.addEventListener('click', function () {
        if (tab.id !== activeId) {
          post({ type: 'activateTab', tabId: tab.id });
        }
      });
      item.addEventListener('mousedown', function (e) {
        if (e.button === 1) {
          e.preventDefault(); // no autoscroll cursor
        }
      });
      item.addEventListener('auxclick', function (e) {
        if (e.button === 1) {
          e.preventDefault();
          post({ type: 'closeTab', tabId: tab.id });
        }
      });

      el.tabs.appendChild(item);
    });

    if (activeElement) {
      scrollTabIntoView(activeElement);
    }
  }

  function scrollTabIntoView(item) {
    var strip = el.tabs;
    var left = item.offsetLeft - strip.offsetLeft;
    var right = left + item.offsetWidth;
    if (left < strip.scrollLeft) {
      strip.scrollLeft = left - 8;
    } else if (right > strip.scrollLeft + strip.clientWidth) {
      strip.scrollLeft = right - strip.clientWidth + 8;
    }
  }

  function onTabsKeyDown(e) {
    var items = Array.prototype.slice.call(el.tabs.querySelectorAll('.tab'));
    var index = items.indexOf(document.activeElement);
    if (index < 0) {
      return;
    }
    var target = null;
    if (e.key === 'ArrowRight') {
      target = items[(index + 1) % items.length];
    } else if (e.key === 'ArrowLeft') {
      target = items[(index - 1 + items.length) % items.length];
    } else if (e.key === 'Home') {
      target = items[0];
    } else if (e.key === 'End') {
      target = items[items.length - 1];
    } else if (e.key === 'Delete') {
      post({ type: 'closeTab', tabId: Number(items[index].dataset.id) });
      e.preventDefault();
      return;
    } else {
      return;
    }
    e.preventDefault();
    items.forEach(function (t) { t.tabIndex = t === target ? 0 : -1; });
    target.focus();
    post({ type: 'activateTab', tabId: Number(target.dataset.id) });
  }

  function selectAllContent() {
    var target = !el.content.hidden ? el.content : (!el.editor.hidden ? el.editor : (!el.welcome.hidden ? el.welcome : el.error));
    var range = document.createRange();
    range.selectNodeContents(target);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  // ---------- images in raw HTML ----------

  // Returns the URL to use for an <img src>: a string to replace it, null to drop it, or
  // undefined to leave it alone (web URLs, data: URIs). Mirrors LocalImageUrls in Core.
  function localImageUrl(src) {
    if (!src || src.charAt(0) === '#') {
      return undefined;
    }

    var driveAbsolute = /^[a-zA-Z]:[\\/]/.test(src);
    if (!driveAbsolute && (/^[a-z][a-z0-9+.\-]*:/i.test(src) || /^[\\/]{2}/.test(src))) {
      return undefined;
    }

    if (!imageBase) {
      return null;
    }

    try {
      var base = new URL(imageBase);
      var path = src.replace(/[?#].*$/, '');
      if (driveAbsolute) {
        var segments = path.split(/[\\/]+/).filter(Boolean).map(encodeURIComponent);
        return new URL(base.origin + '/f/' + segments.join('/')).href;
      }

      if (src.charAt(0) === '/' || src.charAt(0) === '\\') {
        // Root-relative: resolve on the document's drive (or UNC share).
        var parts = base.pathname.split('/').filter(Boolean);
        var keep = parts[1] === 'UNC' ? 4 : 2;
        var root = base.origin + '/' + parts.slice(0, keep).join('/') + '/';
        return new URL(path.replace(/^[\\/]+/, ''), root).href;
      }

      return new URL(path, imageBase).href;
    } catch (e) {
      return null;
    }
  }

  function installSanitizerHooks() {
    window.DOMPurify.addHook('afterSanitizeAttributes', function (node) {
      var tag = node.nodeName;
      if (tag === 'IMG') {
        // Rewritten inside DOMPurify's inert document, so nothing is fetched early.
        var rewritten = localImageUrl(node.getAttribute('src'));
        if (rewritten === null) {
          node.removeAttribute('src');
        } else if (rewritten !== undefined) {
          node.setAttribute('src', rewritten);
        }
        // Not lazy: restoring a tab's scroll position needs the layout above it to be final.
        node.setAttribute('decoding', 'async');
        node.setAttribute('referrerpolicy', 'no-referrer');
      } else if (tag === 'INPUT') {
        node.setAttribute('disabled', '');
        node.removeAttribute('name');
        node.removeAttribute('value');
      } else if (tag === 'A') {
        node.setAttribute('rel', 'noopener noreferrer');
      }
    });
  }

  // ---------- rendering ----------

  function decorate(fragment) {
    // Only read-only task-list checkboxes survive.
    fragment.querySelectorAll('input').forEach(function (input) {
      if ((input.getAttribute('type') || '').toLowerCase() !== 'checkbox') {
        input.remove();
      }
    });

    fragment.querySelectorAll('table').forEach(function (table) {
      var wrap = document.createElement('div');
      wrap.className = 'table-wrap';
      table.parentNode.insertBefore(wrap, table);
      wrap.appendChild(table);
    });

    fragment.querySelectorAll('pre').forEach(function (pre) {
      var code = pre.querySelector('code');
      var lang = '';
      if (code) {
        var match = /(?:^|\s)language-([\w#+.\-]+)/.exec(code.className || '');
        if (match) {
          lang = match[1];
          highlight(code, lang);
        }
      }
      wrapCodeBlock(pre, code || pre, lang);
    });
  }

  function highlight(code, lang) {
    var hljs = window.hljs;
    if (!hljs || !hljs.getLanguage(lang)) {
      return;
    }
    try {
      hljs.highlightElement(code);
    } catch (e) {
      // Leave the block as plain text.
    }
  }

  function wrapCodeBlock(pre, source, lang) {
    var wrap = document.createElement('div');
    wrap.className = 'code-block';
    pre.parentNode.insertBefore(wrap, pre);
    wrap.appendChild(pre);

    var tools = document.createElement('div');
    tools.className = 'code-tools';

    if (lang) {
      var label = document.createElement('span');
      label.className = 'code-lang';
      label.textContent = lang;
      tools.appendChild(label);
    }

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'code-copy';
    button.title = 'Copy code';
    setCopyButton(button, 'Copy', COPY_ICON);
    button.addEventListener('click', function (event) {
      event.preventDefault();
      event.stopPropagation();
      // Fenced code ends with the newline Markdig keeps before the closing fence.
      copyCode(button, (source.textContent || '').replace(/\r?\n$/, ''));
    });
    tools.appendChild(button);
    wrap.appendChild(tools);
  }

  function setCopyButton(button, text, icon) {
    // Static, app-owned markup only (no document content).
    button.innerHTML = icon;
    var span = document.createElement('span');
    span.textContent = text;
    button.appendChild(span);
  }

  function render(msg) {
    var purify = window.DOMPurify;
    if (!purify || !purify.isSupported) {
      // Never insert unsanitised HTML.
      showError({ tabId: msg.tabId, message: 'The viewer could not start its HTML sanitiser.', fileName: msg.fileName, filePath: msg.filePath });
      return;
    }

    var previousScroll = el.scroller.scrollTop;
    var showingContent = !el.content.hidden;
    var exit = editExit && editExit.tabId === (msg.tabId || 0) ? editExit : null;
    editExit = null;
    var switching = switchToTab(msg.tabId || 0);
    var sameTab = !switching && showingContent && contentTabId === (msg.tabId || 0);
    if (!sameTab) {
      // Another tab: take its remembered outline state, or start fully expanded.
      outline.collapsed = new Set(msg.restoreView && Array.isArray(msg.collapsed) ? msg.collapsed : []);
      outline.savedScrollTop = msg.restoreView ? (msg.outlineScrollTop || 0) : 0;
    }

    imageBase = typeof msg.imageBase === 'string' ? msg.imageBase : null;
    var fragment = purify.sanitize(String(msg.html || ''), PURIFY_CONFIG);
    decorate(fragment);
    if (!fragment.hasChildNodes()) {
      var hint = document.createElement('p');
      hint.className = 'empty-hint';
      hint.textContent = 'This document is empty.';
      fragment.appendChild(hint);
    }
    el.content.replaceChildren(fragment);
    contentTabId = msg.tabId || 0;
    tooLargeToEdit = !!msg.tooLarge;

    showOnly(el.content);
    buildOutline(sameTab);

    pendingRestore = null;
    if (exit && !msg.fragment) {
      restoreScroll(exit.scrollTop); // back from the editor: stay where the user was
    } else if (msg.preserveScroll && sameTab) {
      restoreScroll(previousScroll);
    } else if (msg.fragment) {
      el.scroller.scrollTop = 0;
      scrollToFragment(msg.fragment);
    } else if (msg.restoreView) {
      restoreScroll(msg.scrollTop || 0);
    } else {
      el.scroller.scrollTop = 0;
    }
  }

  // ---------- anchors ----------

  function byIdInContent(id) {
    if (!id) {
      return null;
    }
    try {
      return el.content.querySelector('#' + CSS.escape(id));
    } catch (e) {
      return null;
    }
  }

  // Looks only inside the document, never at the viewer's own elements. Heading ids carry
  // HEADING_ID_PREFIX; footnotes and raw-HTML anchors keep their original ids.
  function findTarget(fragment) {
    if (!fragment) {
      return null;
    }
    var lower = fragment.toLowerCase();
    var candidates = [HEADING_ID_PREFIX + fragment, fragment, HEADING_ID_PREFIX + lower, lower];
    for (var i = 0; i < candidates.length; i++) {
      var target = byIdInContent(candidates[i]);
      if (target) {
        return target;
      }
    }
    var named = el.content.querySelectorAll('a[name]');
    for (var j = 0; j < named.length; j++) {
      var name = named[j].getAttribute('name');
      if (name === fragment || name === HEADING_ID_PREFIX + fragment) {
        return named[j];
      }
    }
    return null;
  }

  function scrollToFragment(fragment) {
    if (fragment === '' || fragment === 'top') {
      el.scroller.scrollTo({ top: 0 });
      return;
    }
    var target = findTarget(fragment);
    if (target) {
      scrollToElement(target);
    }
  }

  function scrollToElement(target) {
    target.scrollIntoView({ block: 'start' });
    target.classList.remove('flash');
    void target.offsetWidth;
    target.classList.add('flash');
  }

  // ---------- outline tree ----------

  function headingLevel(h) {
    return Number(h.tagName.charAt(1));
  }

  function buildOutline(keepState) {
    if (outline.observer) {
      outline.observer.disconnect();
      outline.observer = null;
    }

    var savedScroll = keepState && outline.visible ? el.outline.scrollTop : outline.savedScrollTop;
    var previousActiveKey = keepState && outline.activeNode ? outline.activeNode.key : null;
    var previousFocusKey = keepState && outline.focusNode ? outline.focusNode.key : null;

    el.outlineTree.replaceChildren();
    outline.nodes = [];
    outline.byHeading = new Map();
    outline.activeNode = null;
    outline.focusNode = null;

    var headings = Array.prototype.filter.call(
      el.content.querySelectorAll('h1, h2, h3, h4, h5, h6'),
      function (h) { return h.textContent.trim().length > 0; });

    el.outlineEmpty.hidden = headings.length > 0;
    if (!headings.length) {
      outline.savedScrollTop = 0;
      return;
    }

    // Build the hierarchy: each heading nests under the nearest previous heading of a higher level.
    var stack = [];
    var seen = Object.create(null);
    headings.forEach(function (h) {
      var level = headingLevel(h);
      while (stack.length && stack[stack.length - 1].level >= level) {
        stack.pop();
      }
      var parent = stack.length ? stack[stack.length - 1] : null;
      var text = h.textContent.trim().replace(/\s+/g, ' ');
      var key = (parent ? parent.key + '\u001f' : '') + (h.id || 'text:' + text);
      seen[key] = (seen[key] || 0) + 1;
      if (seen[key] > 1) {
        key += '\u001e' + seen[key];
      }

      var node = { key: key, heading: h, level: level, text: text, parent: parent, children: [], li: null, row: null, group: null };
      if (parent) {
        parent.children.push(node);
      }
      outline.nodes.push(node);
      outline.byHeading.set(h, node);
      stack.push(node);
    });

    // Forget collapsed keys that no longer exist.
    var keys = new Set(outline.nodes.map(function (n) { return n.key; }));
    outline.collapsed.forEach(function (k) {
      if (!keys.has(k)) {
        outline.collapsed.delete(k);
      }
    });

    var depthOf = function (node) {
      var d = 1;
      for (var p = node.parent; p; p = p.parent) {
        d++;
      }
      return d;
    };

    outline.nodes.forEach(function (node) {
      var li = document.createElement('li');
      li.setAttribute('role', 'treeitem');
      li.setAttribute('aria-level', String(depthOf(node)));
      li.setAttribute('aria-selected', 'false');
      li.tabIndex = -1;
      li.className = 'tree-item';

      var row = document.createElement('div');
      row.className = 'tree-row';
      row.style.setProperty('--depth', String(depthOf(node) - 1));

      var toggle = document.createElement('span');
      toggle.className = 'tree-toggle';
      toggle.setAttribute('aria-hidden', 'true');
      if (node.children.length) {
        toggle.innerHTML = CHEVRON_ICON;
        toggle.addEventListener('click', function (e) {
          e.stopPropagation();
          setCollapsed(node, !outline.collapsed.has(node.key));
          focusNode(node);
        });
      }

      var label = document.createElement('span');
      label.className = 'tree-label';
      label.textContent = node.text;
      label.title = node.text;

      row.appendChild(toggle);
      row.appendChild(label);
      row.addEventListener('click', function () {
        focusNode(node);
        activateNode(node);
      });
      li.appendChild(row);

      if (node.children.length) {
        var group = document.createElement('ul');
        group.setAttribute('role', 'group');
        group.className = 'tree-group';
        li.appendChild(group);
        node.group = group;
      }

      node.li = li;
      node.row = row;
      (node.parent ? node.parent.group : el.outlineTree).appendChild(li);
      applyCollapsed(node);
    });

    var restoredFocus = previousFocusKey && findNodeByKey(previousFocusKey);
    setFocusable(restoredFocus || outline.nodes[0]);

    observeHeadings();
    if (previousActiveKey) {
      var active = findNodeByKey(previousActiveKey);
      if (active) {
        setActive(active, false);
      }
    }

    outline.savedScrollTop = savedScroll;
    if (outline.visible) {
      el.outline.scrollTop = savedScroll;
    }
  }

  function findNodeByKey(key) {
    for (var i = 0; i < outline.nodes.length; i++) {
      if (outline.nodes[i].key === key) {
        return outline.nodes[i];
      }
    }
    return null;
  }

  function applyCollapsed(node) {
    if (!node.children.length) {
      node.li.removeAttribute('aria-expanded');
      return;
    }
    var collapsed = outline.collapsed.has(node.key);
    node.li.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
    node.group.hidden = collapsed;
  }

  function setCollapsed(node, collapsed) {
    if (!node.children.length) {
      return;
    }
    if (collapsed) {
      outline.collapsed.add(node.key);
    } else {
      outline.collapsed.delete(node.key);
    }
    applyCollapsed(node);
    refreshActiveDisplay();
    scheduleTabState();
  }

  function isNodeVisible(node) {
    for (var p = node.parent; p; p = p.parent) {
      if (outline.collapsed.has(p.key)) {
        return false;
      }
    }
    return true;
  }

  function visibleNodes() {
    return outline.nodes.filter(isNodeVisible);
  }

  function setFocusable(node) {
    if (outline.focusNode && outline.focusNode.li) {
      outline.focusNode.li.tabIndex = -1;
    }
    outline.focusNode = node || null;
    if (node) {
      node.li.tabIndex = 0;
    }
  }

  function focusNode(node) {
    if (!node) {
      return;
    }
    setFocusable(node);
    node.li.focus({ preventScroll: true });
    keepRowInView(node.row);
  }

  function activateNode(node) {
    scrollToElement(node.heading);
    if (window.matchMedia('(max-width: 860px)').matches) {
      setOutlineVisible(false, true);
    }
  }

  function onTreeKeyDown(e) {
    var node = outline.focusNode;
    if (!node) {
      return;
    }
    var list = visibleNodes();
    var index = list.indexOf(node);
    var handled = true;

    switch (e.key) {
      case 'ArrowDown':
        focusNode(list[Math.min(list.length - 1, index + 1)]);
        break;
      case 'ArrowUp':
        focusNode(list[Math.max(0, index - 1)]);
        break;
      case 'Home':
        focusNode(list[0]);
        break;
      case 'End':
        focusNode(list[list.length - 1]);
        break;
      case 'ArrowRight':
        if (node.children.length && outline.collapsed.has(node.key)) {
          setCollapsed(node, false);
        } else if (node.children.length) {
          focusNode(node.children[0]);
        }
        break;
      case 'ArrowLeft':
        if (node.children.length && !outline.collapsed.has(node.key)) {
          setCollapsed(node, true);
        } else if (node.parent) {
          focusNode(node.parent);
        }
        break;
      case 'Enter':
      case ' ':
        activateNode(node);
        break;
      case '*':
        // Expand all siblings, as in the WAI-ARIA tree pattern.
        (node.parent ? node.parent.children : outline.nodes.filter(function (n) { return !n.parent; }))
          .forEach(function (n) { setCollapsed(n, false); });
        break;
      default:
        handled = false;
    }

    if (handled) {
      e.preventDefault();
      e.stopPropagation();
    }
  }

  function observeHeadings() {
    if (!outline.nodes.length) {
      return;
    }
    var headings = outline.nodes.map(function (n) { return n.heading; });
    var visible = new Set();
    var initial = true; // the first callback after a (re)build must not move the outline scroll

    outline.observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          visible.add(entry.target);
        } else {
          visible.delete(entry.target);
        }
      });

      var active = null;
      for (var i = 0; i < headings.length; i++) {
        if (visible.has(headings[i])) {
          active = headings[i];
          break;
        }
      }
      if (!active) {
        // Between headings: the last one above the top of the viewport.
        var top = el.scroller.getBoundingClientRect().top;
        for (var j = headings.length - 1; j >= 0; j--) {
          if (headings[j].getBoundingClientRect().top < top) {
            active = headings[j];
            break;
          }
        }
      }
      setActive(active ? outline.byHeading.get(active) : outline.nodes[0], !initial);
      initial = false;
    }, { root: el.scroller, rootMargin: '0px 0px -70% 0px' });

    headings.forEach(function (h) { outline.observer.observe(h); });
  }

  function setActive(node, follow) {
    outline.activeNode = node || null;
    refreshActiveDisplay(follow);
  }

  // Highlights the active heading, or its nearest visible ancestor if it sits in a collapsed node.
  function refreshActiveDisplay(follow) {
    var shown = outline.activeNode;
    while (shown && !isNodeVisible(shown)) {
      shown = shown.parent;
    }
    outline.nodes.forEach(function (n) {
      var on = n === shown;
      n.row.classList.toggle('active', on);
      n.li.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    if (follow && shown && outline.visible) {
      keepRowInView(shown.row);
    }
  }

  function keepRowInView(row) {
    if (!row || !outline.visible) {
      return;
    }
    // Adjust only the outline's own scroll position (scrollIntoView would also move the document).
    var box = el.outline.getBoundingClientRect();
    var r = row.getBoundingClientRect();
    var margin = 24;
    if (r.top < box.top + margin) {
      el.outline.scrollTop -= (box.top + margin) - r.top;
    } else if (r.bottom > box.bottom - margin) {
      el.outline.scrollTop += r.bottom - (box.bottom - margin);
    }
  }

  // ---------- outline visibility & width ----------

  // First top-level block at or below the top of the viewport, with its offset, so the reading
  // position can be restored after the content column changes width.
  function captureReadingAnchor() {
    if (el.content.hidden) {
      return null;
    }
    var top = el.scroller.getBoundingClientRect().top;
    var children = el.content.children;
    for (var i = 0; i < children.length; i++) {
      var r = children[i].getBoundingClientRect();
      if (r.bottom > top) {
        return { element: children[i], offset: r.top - top };
      }
    }
    return null;
  }

  function restoreReadingAnchor(anchor) {
    if (!anchor || !anchor.element.isConnected) {
      return;
    }
    var top = el.scroller.getBoundingClientRect().top;
    el.scroller.scrollTop += (anchor.element.getBoundingClientRect().top - top) - anchor.offset;
  }

  function setOutlineVisible(visible, notifyHost) {
    if (visible === outline.visible && el.body.classList.contains('outline-open') === visible) {
      return;
    }

    var anchor = captureReadingAnchor();
    if (!visible) {
      // display:none drops the scroll offset; remember it for when the panel comes back.
      outline.savedScrollTop = el.outline.scrollTop;
    }

    outline.visible = visible;
    el.body.classList.toggle('outline-open', visible);
    el.btnOutline.setAttribute('aria-pressed', visible ? 'true' : 'false');
    el.btnOutline.setAttribute('aria-expanded', visible ? 'true' : 'false');

    if (visible) {
      el.outline.scrollTop = outline.savedScrollTop;
    } else if (el.outline.contains(document.activeElement)) {
      el.btnOutline.focus();
    }
    restoreReadingAnchor(anchor);

    if (notifyHost) {
      post({ type: 'outline', visible: visible });
    }
  }

  function clampWidth(width) {
    var w = Number(width);
    if (!isFinite(w) || w <= 0) {
      w = OUTLINE_DEFAULT;
    }
    var max = Math.max(OUTLINE_MIN, Math.min(OUTLINE_MAX, Math.round(window.innerWidth * 0.6)));
    return Math.round(Math.max(OUTLINE_MIN, Math.min(max, w)));
  }

  function setOutlineWidth(width, notifyHost) {
    var w = clampWidth(width);
    var anchor = outline.visible ? captureReadingAnchor() : null;
    outline.width = w;
    el.body.style.setProperty('--outline-width', w + 'px');
    el.outlineResizer.setAttribute('aria-valuenow', String(w));
    restoreReadingAnchor(anchor);
    if (notifyHost) {
      post({ type: 'outlineWidth', width: w });
    }
  }

  function initResizer() {
    var startX = 0;
    var startWidth = 0;
    var dragging = false;

    el.outlineResizer.setAttribute('aria-valuemin', String(OUTLINE_MIN));
    el.outlineResizer.setAttribute('aria-valuemax', String(OUTLINE_MAX));

    el.outlineResizer.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) {
        return;
      }
      dragging = true;
      startX = e.clientX;
      startWidth = outline.width;
      el.outlineResizer.setPointerCapture(e.pointerId);
      el.body.classList.add('resizing');
      e.preventDefault();
    });

    el.outlineResizer.addEventListener('pointermove', function (e) {
      if (dragging) {
        setOutlineWidth(startWidth + (e.clientX - startX), false);
      }
    });

    function endDrag(e) {
      if (!dragging) {
        return;
      }
      dragging = false;
      el.body.classList.remove('resizing');
      if (el.outlineResizer.hasPointerCapture(e.pointerId)) {
        el.outlineResizer.releasePointerCapture(e.pointerId);
      }
      post({ type: 'outlineWidth', width: outline.width });
    }

    el.outlineResizer.addEventListener('pointerup', endDrag);
    el.outlineResizer.addEventListener('pointercancel', endDrag);
    el.outlineResizer.addEventListener('dblclick', function () {
      setOutlineWidth(OUTLINE_DEFAULT, true);
    });
    el.outlineResizer.addEventListener('keydown', function (e) {
      var step = e.shiftKey ? 64 : 16;
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        setOutlineWidth(outline.width + (e.key === 'ArrowRight' ? step : -step), true);
        e.preventDefault();
      }
    });
  }

  // ---------- theme ----------

  function applyTheme(theme, mode) {
    el.root.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light');
    var m = THEME_LABELS[mode] ? mode : 'system';
    el.root.setAttribute('data-mode', m);
    el.btnTheme.title = 'Theme: ' + THEME_LABELS[m] + ' (Ctrl+T)';
    el.btnTheme.setAttribute('aria-label', 'Theme: ' + THEME_LABELS[m] + '. Click to change.');
  }

  // ---------- copy ----------

  function copyCode(button, text) {
    var id = ++copyCounter;
    pendingCopies[id] = button;
    if (webview) {
      post({ type: 'copy', id: id, text: text });
    } else if (navigator.clipboard) {
      navigator.clipboard.writeText(text).then(
        function () { onCopied(id, true); },
        function () { onCopied(id, false); });
    } else {
      onCopied(id, false);
    }
  }

  function onCopied(id, ok) {
    var button = pendingCopies[id];
    delete pendingCopies[id];
    if (!button) {
      return;
    }

    var tools = button.parentElement;
    clearTimeout(button._resetTimer);
    button.classList.toggle('copied', ok);
    button.classList.toggle('failed', !ok);
    setCopyButton(button, ok ? 'Copied' : 'Copy failed', ok ? CHECK_ICON : COPY_ICON);
    if (tools) {
      tools.classList.add('show');
    }

    button._resetTimer = setTimeout(function () {
      button.classList.remove('copied', 'failed');
      setCopyButton(button, 'Copy', COPY_ICON);
      if (tools) {
        tools.classList.remove('show');
      }
    }, 1600);
  }

  // ---------- links: never navigate ----------

  function onLinkActivate(event) {
    var target = event.target;
    var anchor = target && target.closest ? target.closest('a[href], area[href]') : null;
    if (!anchor) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    // While editing a click only places the cursor; the editor's link popover offers "Open".
    if (event.type !== 'click' || event.button !== 0 || !el.editor.hidden || !el.content.contains(anchor)) {
      return;
    }

    var href = anchor.getAttribute('href') || '';
    if (href.charAt(0) === '#') {
      var fragment = href.slice(1);
      try {
        fragment = decodeURIComponent(fragment);
      } catch (e) {
        // Keep the raw fragment.
      }
      scrollToFragment(fragment);
      return;
    }

    post({ type: 'link', href: href });
  }

  // ---------- drag & drop ----------

  function hasFiles(event) {
    var types = event.dataTransfer && event.dataTransfer.types;
    return !!types && Array.prototype.indexOf.call(types, 'Files') >= 0;
  }

  function onDragEnter(event) {
    if (!hasFiles(event)) {
      return;
    }
    event.preventDefault();
    dragDepth++;
    el.dropOverlay.hidden = false;
  }

  function onDragOver(event) {
    if (!hasFiles(event)) {
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  }

  function onDragLeave(event) {
    if (!hasFiles(event)) {
      return;
    }
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) {
      el.dropOverlay.hidden = true;
    }
  }

  function onDrop(event) {
    event.preventDefault();
    dragDepth = 0;
    el.dropOverlay.hidden = true;
    var files = event.dataTransfer && event.dataTransfer.files;
    if (files && files.length && webview && webview.postMessageWithAdditionalObjects) {
      webview.postMessageWithAdditionalObjects({ type: 'drop' }, files);
    }
  }

  // ---------- API for editor.js ----------

  window.JustMdViewer = {
    purifyConfig: PURIFY_CONFIG,
    localImageUrl: localImageUrl,
    // Same sanitising as a rendered document; returns a fragment, or null without DOMPurify.
    sanitize: function (html) {
      var purify = window.DOMPurify;
      if (!purify || !purify.isSupported) {
        return null;
      }
      var fragment = purify.sanitize(String(html || ''), PURIFY_CONFIG);
      fragment.querySelectorAll('input').forEach(function (input) {
        if ((input.getAttribute('type') || '').toLowerCase() !== 'checkbox') {
          input.remove();
        }
      });
      return fragment;
    },
    post: post,
    receive: function (msg) { onHostMessage({ data: msg }); },
    showEditor: showEditor,
    leaveEditor: leaveEditor,
    currentTabId: function () { return currentTabId; },
    setHooks: function (hooks) { editorHooks = hooks; },
    setDevSink: function (sink) { devSink = webview ? null : sink; }
  };

  // ---------- startup ----------

  function init() {
    if (window.DOMPurify && window.DOMPurify.isSupported) {
      installSanitizerHooks();
    }
    if (window.hljs) {
      window.hljs.configure({ ignoreUnescapedHTML: true, throwUnescapedHTML: false });
    }

    document.addEventListener('click', onLinkActivate, true);
    document.addEventListener('auxclick', onLinkActivate, true);
    document.addEventListener('dragenter', onDragEnter);
    document.addEventListener('dragover', onDragOver);
    document.addEventListener('dragleave', onDragLeave);
    document.addEventListener('drop', onDrop);

    // Dropping something that is not a file (text, links) must not navigate either.
    window.addEventListener('dragover', function (e) { e.preventDefault(); });
    window.addEventListener('drop', function (e) { e.preventDefault(); });

    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && el.editor.hidden && outline.visible && window.matchMedia('(max-width: 860px)').matches) {
        setOutlineVisible(false, true);
      }
    });

    // View state for the host (per tab): reported shortly after scrolling stops.
    el.scroller.addEventListener('scroll', scheduleTabState, { passive: true });
    el.outline.addEventListener('scroll', function () {
      if (outline.visible) {
        scheduleTabState();
      }
    }, { passive: true });
    ['wheel', 'pointerdown', 'keydown', 'touchstart'].forEach(function (type) {
      el.scroller.addEventListener(type, cancelPendingRestore, { passive: true });
    });
    el.content.addEventListener('load', onContentLoad, true);

    el.tabs.addEventListener('keydown', onTabsKeyDown);
    el.tabs.addEventListener('wheel', function (e) {
      if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
        el.tabs.scrollLeft += e.deltaY;
        e.preventDefault();
      }
    }, { passive: false });

    el.outlineTree.addEventListener('keydown', onTreeKeyDown);
    el.outlineTree.addEventListener('focusin', function (e) {
      var node = outline.nodes.find(function (n) { return n.li === e.target; });
      if (node) {
        setFocusable(node);
      }
    });
    initResizer();
    window.addEventListener('resize', function () {
      if (outline.width !== clampWidth(outline.width)) {
        setOutlineWidth(outline.width, false);
      }
    });

    function openFile() { post({ type: 'open' }); }
    document.getElementById('btn-open').addEventListener('click', openFile);
    document.getElementById('btn-welcome-open').addEventListener('click', openFile);
    document.getElementById('btn-error-open').addEventListener('click', openFile);
    function newFile() { post({ type: 'newFile' }); }
    document.getElementById('btn-new').addEventListener('click', newFile);
    document.getElementById('btn-welcome-new').addEventListener('click', newFile);
    el.btnTheme.addEventListener('click', function () { post({ type: 'cycleTheme' }); });
    el.btnOutline.addEventListener('click', function () {
      setOutlineVisible(!outline.visible, true);
    });

    setOutlineWidth(OUTLINE_DEFAULT, false);
    if (webview) {
      webview.addEventListener('message', onHostMessage);
      // After every deferred script, so editor.js sees an "edit" sent in answer to "ready".
      document.addEventListener('DOMContentLoaded', function () { post({ type: 'ready' }); });
    } else {
      showWelcome();
    }
  }

  init();
})();
