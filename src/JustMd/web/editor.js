// JustMd edit mode (page side).
// Nothing here enters edit mode by itself: the editor is only shown in response to an "edit"
// message from the host, and the app bar's pencil button merely asks the host for one.
//
// Host -> page: edit, exitEdit, editorCommand, requestContent, saved, documentRenamed,
// externalChange, imagePicked. The page keeps one entry per editing tab (state, undo history,
// baseline), so background tabs can still be saved; only the shown tab has an EditorView.
//
// editorCommand { tabId, command, arg } names: the keys of JustMdEditor.commands listed in
// PLAIN_COMMANDS, setHeading (arg is the level as a string, "1" to "6"), and four that need the
// page's UI: setLink, insertImage and insertTable (open their popovers), toggleRtl.
//
// requestContent is answered with editorContent { tabId, requestId, markdown, rtl }, or, when
// there is nothing to hand over, with { tabId, requestId, error: 'tooLarge' | 'failed' } alone.
(function () {
  'use strict';

  var viewer = window.JustMdViewer;
  var PM = window.JustMdEditor;
  var webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;

  if (!viewer) {
    return;
  }

  if (!PM) {
    // Bundle missing: stay a viewer. Hand any "edit" straight back so the host re-renders.
    document.body.classList.add('no-editor');
    viewer.setHooks({
      message: function (msg) {
        if (msg.type === 'edit' && msg.tabId > 0) {
          viewer.post({ type: 'exitEdit', tabId: msg.tabId });
        }
      },
      tabs: function () {},
      hidden: function () {}
    });
    return;
  }

  var DIRTY_DELAY = 250;
  // Must equal WebMessageParser.MaxMarkdownLength: the host rejects anything longer.
  var MAX_MARKDOWN_LENGTH = 8 * 1024 * 1024;
  var PLAIN_COMMANDS = ['setParagraph', 'toggleBold', 'toggleItalic', 'toggleStrike', 'toggleCode',
    'toggleBulletList', 'toggleOrderedList', 'toggleTaskList', 'toggleBlockquote', 'setCodeBlock',
    'insertHorizontalRule', 'indent', 'outdent', 'undo', 'redo'];
  // Dry runs that tell whether the popover-opening buttons can apply at the cursor.
  var PROBES = {
    setLink: PM.commands.setLink({ href: 'x' }),
    insertImage: PM.commands.insertImage({ src: 'x' }),
    insertTable: PM.commands.insertTable(2, 2)
  };
  var MARK_BUTTONS = { toggleBold: 'strong', toggleItalic: 'em', toggleStrike: 'strike', toggleCode: 'code', setLink: 'link' };

  var el = {
    editor: document.getElementById('editor'),
    scroller: document.getElementById('scroller'),
    btnEdit: document.getElementById('btn-edit'),
    toolbar: document.getElementById('toolbar'),
    block: document.getElementById('tb-block'),
    tableBar: document.getElementById('table-bar'),
    banner: document.getElementById('external-banner'),
    popLink: document.getElementById('pop-link'),
    linkTextRow: document.getElementById('pop-link-text-row'),
    linkText: document.getElementById('pop-link-text'),
    linkHref: document.getElementById('pop-link-href'),
    linkTitle: document.getElementById('pop-link-title'),
    linkError: document.getElementById('pop-link-error'),
    linkOpen: document.getElementById('pop-link-open'),
    linkRemove: document.getElementById('pop-link-remove'),
    popImage: document.getElementById('pop-image'),
    imageSrc: document.getElementById('pop-image-src'),
    imageAlt: document.getElementById('pop-image-alt'),
    imageTitle: document.getElementById('pop-image-title'),
    imageError: document.getElementById('pop-image-error'),
    imageApply: document.getElementById('pop-image-apply'),
    popTable: document.getElementById('pop-table'),
    tableRows: document.getElementById('pop-table-rows'),
    tableCols: document.getElementById('pop-table-cols')
  };
  var toolbarButtons = Array.prototype.slice.call(el.toolbar.querySelectorAll('button[data-cmd]'));
  var tableButtons = Array.prototype.slice.call(el.tableBar.querySelectorAll('button[data-table]'));

  var entries = new Map(); // tabId -> entry, see createEntry
  var activeId = 0;
  var view = null;
  var openPopover = null;

  var post = viewer.post;

  // ---------- entries ----------

  function createEntry(msg) {
    var parsed = PM.parse(typeof msg.markdown === 'string' ? msg.markdown : '');
    var state = PM.EditorState.create({
      schema: PM.schema,
      doc: parsed.doc,
      plugins: PM.basePlugins({ onEscape: onEscape, underWebView: !!webview })
    });
    return {
      tabId: msg.tabId,
      revision: msg.revision,
      state: state,
      baseline: parsed.baseline,
      rtl: !!msg.rtl,
      // What is on disk. Kept apart from the baseline: serialising against the original baseline
      // stays correct after a save, and the undo history survives it.
      savedDoc: parsed.doc,
      savedRtl: !!msg.rtl,
      pendingSave: null,
      postedDirty: false,
      dirtyTimer: 0,
      scrollTop: 0,
      imageBase: typeof msg.imageBase === 'string' ? msg.imageBase : null,
      externalChange: false,
      listed: false
    };
  }

  function dropEntry(tabId) {
    var entry = entries.get(tabId);
    if (entry) {
      clearTimeout(entry.dirtyTimer);
      entries.delete(tabId);
    }
  }

  function isDirty(entry) {
    return entry.rtl !== entry.savedRtl || !entry.state.doc.eq(entry.savedDoc);
  }

  function reportDirty(entry, always) {
    clearTimeout(entry.dirtyTimer);
    entry.dirtyTimer = 0;
    var dirty = isDirty(entry);
    if (always || dirty !== entry.postedDirty) {
      entry.postedDirty = dirty;
      post({ type: 'editorDirty', tabId: entry.tabId, dirty: dirty });
    }
  }

  // Becoming dirty is reported at once: the host decides from this flag whether closing the tab
  // needs a prompt, so a debounce here could lose the last keystrokes. Only the way back waits.
  function noteChange(entry) {
    if (!entry.postedDirty) {
      reportDirty(entry);
      return;
    }
    clearTimeout(entry.dirtyTimer);
    entry.dirtyTimer = setTimeout(function () { reportDirty(entry); }, DIRTY_DELAY);
  }

  // ---------- the view ----------

  function attach(entry, top, placeCursor) {
    detach();
    activeId = entry.tabId;
    viewer.showEditor(entry.tabId, entry.imageBase);
    view = new PM.EditorView({ mount: el.editor }, {
      state: entry.state,
      dispatchTransaction: dispatchTransaction,
      nodeViews: nodeViews,
      handleClick: onEditorClick,
      attributes: { 'aria-label': 'Document editor' }
    });
    applyDirection(entry);
    el.scroller.scrollTop = top;
    if (placeCursor && top > 0) {
      placeCursorInView();
    }
    syncBanner();
    syncToolbar();
    view.focus();
  }

  // Stores the tab's scroll position and removes the view. The entry itself stays.
  function detach() {
    closePopover(false);
    el.tableBar.hidden = true;
    el.banner.hidden = true;
    if (!view) {
      return;
    }
    var entry = entries.get(activeId);
    if (entry) {
      entry.scrollTop = el.scroller.scrollTop;
      if (entry.dirtyTimer) {
        reportDirty(entry);
      }
    }
    view.destroy();
    view = null;
    activeId = 0;
  }

  // Entered from a scrolled view: the first keystroke must not jump back to the top of the document.
  function placeCursorInView() {
    var box = el.editor.getBoundingClientRect();
    var top = el.scroller.getBoundingClientRect().top;
    var found = view.posAtCoords({ left: box.left + box.width / 2, top: top + 24 });
    if (found) {
      view.dispatch(view.state.tr.setSelection(PM.TextSelection.near(view.state.doc.resolve(found.pos))));
    }
  }

  function applyDirection(entry) {
    if (entry.rtl) {
      el.editor.setAttribute('dir', 'rtl');
    } else {
      el.editor.removeAttribute('dir');
    }
  }

  function dispatchTransaction(tr) {
    var entry = entries.get(activeId);
    var previous = view.state;
    var next = previous.apply(tr);
    view.updateState(next);
    if (!entry) {
      return;
    }
    entry.state = next;
    if (next.doc !== previous.doc) {
      noteChange(entry);
    }
    if (next.doc !== previous.doc || next.selection !== previous.selection) {
      closeStalePopover(next, next.doc !== previous.doc);
    }
    syncToolbar();
  }

  function onEditorClick(editorView, pos, event) {
    var target = event.target;
    // The selection settles after this handler returns.
    setTimeout(function () { showContextPopover(target); }, 0);
    return false;
  }

  function showContextPopover(target) {
    if (!view || openPopover || !target || !target.closest) {
      return;
    }
    if (target.closest('a') && view.state.selection.empty && PM.queries.linkAt(view.state)) {
      openLinkPopover(caretRect(), false);
    } else if (target.tagName === 'IMG' && selectedImage()) {
      openImagePopover(target.getBoundingClientRect(), false);
    }
  }

  // ---------- node views ----------

  // The URL to show for a raw Markdown image source, or null for a placeholder.
  function displayUrl(raw) {
    if (!raw || !PM.isSafeImageSrc(raw)) {
      return null;
    }
    var url = viewer.localImageUrl(raw);
    if (url === undefined) {
      return /^https?:/i.test(raw) ? raw : null;
    }
    return url;
  }

  function withAttrs(node, changes) {
    return Object.assign({}, node.attrs, changes);
  }

  function ImageView(node) {
    var img = document.createElement('img');
    img.setAttribute('decoding', 'async');
    img.setAttribute('referrerpolicy', 'no-referrer');
    img.addEventListener('error', function () { img.classList.add('image-missing'); });
    this.dom = img;
    this.show(node);
  }
  ImageView.prototype.show = function (node) {
    var img = this.dom;
    var raw = node.attrs.src || '';
    var url = displayUrl(raw);
    img.alt = node.attrs.alt || raw;
    img.title = node.attrs.title || raw;
    img.classList.toggle('image-missing', !url);
    if (url) {
      img.src = url;
    } else {
      img.removeAttribute('src');
    }
    this.node = node;
  };
  ImageView.prototype.update = function (node) {
    if (node.type !== this.node.type) {
      return false;
    }
    this.show(node);
    return true;
  };

  function htmlSource(node) {
    return typeof node.attrs.source === 'string' ? node.attrs.source : node.textContent;
  }

  // Read-only preview of a raw HTML block; the source stays verbatim in the node.
  function HtmlBlockView(node) {
    var dom = document.createElement('div');
    dom.className = 'editor-html-block';
    dom.contentEditable = 'false';
    var label = document.createElement('span');
    label.className = 'editor-html-label';
    label.textContent = 'HTML';
    var preview = document.createElement('div');
    dom.appendChild(label);
    dom.appendChild(preview);
    this.dom = dom;
    this.preview = preview;
    this.show(node);
  }
  HtmlBlockView.prototype.show = function (node) {
    var source = htmlSource(node);
    var fragment = viewer.sanitize(source);
    if (fragment && fragment.hasChildNodes()) {
      this.preview.className = '';
      this.preview.replaceChildren(fragment);
    } else {
      // Nothing visible survives (a comment, say), or no sanitiser: show the source as text.
      this.preview.className = 'editor-html-source';
      this.preview.textContent = source;
    }
    this.node = node;
  };
  HtmlBlockView.prototype.update = function (node) {
    if (node.type !== this.node.type) {
      return false;
    }
    if (htmlSource(node) !== htmlSource(this.node)) {
      this.show(node);
    }
    this.node = node;
    return true;
  };
  HtmlBlockView.prototype.ignoreMutation = function () { return true; };

  function HtmlInlineView(node) {
    var dom = document.createElement('span');
    dom.className = 'editor-html-chip';
    dom.contentEditable = 'false';
    dom.dir = 'ltr';
    dom.textContent = htmlSource(node);
    this.dom = dom;
  }
  HtmlInlineView.prototype.ignoreMutation = function () { return true; };

  function ListItemView(node, editorView, getPos) {
    var li = document.createElement('li');
    this.dom = li;
    this.node = node;
    if (node.attrs.checked === null || node.attrs.checked === undefined) {
      this.contentDOM = li;
      return;
    }

    var self = this;
    var check = document.createElement('input');
    check.type = 'checkbox';
    check.contentEditable = 'false';
    check.checked = !!node.attrs.checked;
    check.setAttribute('aria-label', 'Done');
    // Keeps the focus in the document. The click itself is not cancelled: that would make the
    // browser restore the old checked value after this handler has updated it.
    check.addEventListener('mousedown', function (e) { e.preventDefault(); });
    check.addEventListener('click', function () {
      var pos = getPos();
      if (typeof pos === 'number') {
        editorView.dispatch(editorView.state.tr.setNodeMarkup(pos, null, withAttrs(self.node, { checked: !self.node.attrs.checked })));
      }
    });
    var body = document.createElement('div');
    body.className = 'task-body';
    li.className = 'task-list-item';
    li.appendChild(check);
    li.appendChild(body);
    this.check = check;
    this.contentDOM = body;
  }
  ListItemView.prototype.update = function (node) {
    var wasTask = !!this.check;
    var isTask = node.attrs.checked !== null && node.attrs.checked !== undefined;
    if (node.type !== this.node.type || wasTask !== isTask) {
      return false;
    }
    if (this.check) {
      this.check.checked = !!node.attrs.checked;
    }
    this.node = node;
    return true;
  };
  ListItemView.prototype.stopEvent = function (e) {
    return !!this.check && e.target === this.check;
  };

  function CodeBlockView(node, editorView, getPos) {
    var self = this;
    var wrap = document.createElement('div');
    wrap.className = 'code-block';
    var pre = document.createElement('pre');
    var code = document.createElement('code');
    pre.appendChild(code);
    var field = document.createElement('input');
    field.type = 'text';
    field.className = 'code-lang-field';
    field.dir = 'ltr';
    field.placeholder = 'language';
    field.spellcheck = false;
    field.autocomplete = 'off';
    field.setAttribute('aria-label', 'Code language');
    field.value = node.attrs.params || '';
    field.addEventListener('change', function () {
      var params = field.value.replace(/[`\r\n]/g, '').trim();
      var pos = getPos();
      if (typeof pos === 'number' && params !== (self.node.attrs.params || '')) {
        editorView.dispatch(editorView.state.tr.setNodeMarkup(pos, null, withAttrs(self.node, { params: params })));
      }
    });
    field.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') {
        e.preventDefault();
        field.blur();
        editorView.focus();
      }
    });
    wrap.appendChild(pre);
    wrap.appendChild(field);
    this.dom = wrap;
    this.contentDOM = code;
    this.field = field;
    this.node = node;
  }
  CodeBlockView.prototype.update = function (node) {
    if (node.type !== this.node.type) {
      return false;
    }
    if (document.activeElement !== this.field) {
      this.field.value = node.attrs.params || '';
    }
    this.node = node;
    return true;
  };
  CodeBlockView.prototype.stopEvent = function (e) {
    return e.target === this.field;
  };

  var nodeViews = {
    image: function (node) { return new ImageView(node); },
    html_block: function (node) { return new HtmlBlockView(node); },
    html_inline: function (node) { return new HtmlInlineView(node); },
    list_item: function (node, editorView, getPos) { return new ListItemView(node, editorView, getPos); },
    code_block: function (node, editorView, getPos) { return new CodeBlockView(node, editorView, getPos); }
  };

  // ---------- commands ----------

  function plainCommand(name, arg) {
    if (name === 'setHeading') {
      var level = parseInt(arg, 10); // HostMessage.Arg is a string
      return level >= 1 && level <= 6 ? PM.commands.setHeading(level) : null;
    }
    return PLAIN_COMMANDS.indexOf(name) >= 0 ? PM.commands[name] : null;
  }

  function runCommand(name, arg, trigger) {
    if (!view || (PROBES[name] && !PROBES[name](view.state))) {
      return;
    }
    switch (name) {
      case 'setLink':
        openLinkPopover(trigger ? trigger.getBoundingClientRect() : caretRect(), true);
        return;
      case 'insertImage':
        openImagePopover(trigger ? trigger.getBoundingClientRect() : caretRect(), true);
        return;
      case 'insertTable':
        openTablePopover(trigger ? trigger.getBoundingClientRect() : caretRect());
        return;
      case 'toggleRtl':
        toggleRtl();
        return;
    }
    var command = plainCommand(name, arg);
    if (command) {
      closePopover(false);
      command(view.state, view.dispatch, view);
      view.focus();
    }
  }

  function onEditorCommand(msg) {
    if (!view || (msg.tabId && msg.tabId !== activeId)) {
      return;
    }
    // A text field has the focus: the shortcut is meant for the field, not the document.
    var focused = document.activeElement;
    if (focused && focused.tagName === 'INPUT' && focused.type !== 'checkbox') {
      if (msg.command === 'undo' || msg.command === 'redo') {
        document.execCommand(msg.command);
      }
      return;
    }
    runCommand(msg.command, msg.arg, null);
  }

  function toggleRtl() {
    var entry = entries.get(activeId);
    if (entry) {
      entry.rtl = !entry.rtl;
      applyDirection(entry);
      noteChange(entry);
      syncToolbar();
      view.focus();
    }
  }

  // Runs against the shown view, or directly against a background tab's state.
  function execOn(entry, command) {
    if (entry.tabId === activeId && view) {
      return command(view.state, view.dispatch, view);
    }
    return command(entry.state, function (tr) {
      entry.state = entry.state.apply(tr);
      noteChange(entry);
    });
  }

  // ---------- toolbar ----------

  function nearest(state, test) {
    var $from = state.selection.$from;
    for (var depth = $from.depth; depth > 0; depth--) {
      var node = $from.node(depth);
      if (test(node)) {
        return node;
      }
    }
    return null;
  }

  function syncToolbar() {
    if (!view) {
      return;
    }
    var state = view.state;
    var entry = entries.get(activeId);
    var marks = PM.queries.activeMarks(state);
    var blockType = PM.queries.blockType(state);
    var list = nearest(state, function (n) { return n.type.name === 'bullet_list' || n.type.name === 'ordered_list'; });
    var item = nearest(state, function (n) { return n.type.name === 'list_item'; });
    var task = !!item && item.attrs.checked !== null && item.attrs.checked !== undefined;
    var pressed = {
      toggleBulletList: !!list && list.type.name === 'bullet_list' && !task,
      toggleOrderedList: !!list && list.type.name === 'ordered_list',
      toggleTaskList: task,
      toggleBlockquote: !!nearest(state, function (n) { return n.type.name === 'blockquote'; }),
      setCodeBlock: blockType === 'code_block',
      toggleRtl: !!entry && entry.rtl
    };

    toolbarButtons.forEach(function (button) {
      var name = button.dataset.cmd;
      var command = plainCommand(name) || PROBES[name];
      if (command) {
        button.disabled = !command(state);
      }
      if (MARK_BUTTONS[name]) {
        button.setAttribute('aria-pressed', marks.has(MARK_BUTTONS[name]) ? 'true' : 'false');
      } else if (name in pressed) {
        button.setAttribute('aria-pressed', pressed[name] ? 'true' : 'false');
      }
    });

    el.block.value = /^(paragraph|heading-[1-6])$/.test(blockType) ? blockType : '';

    var inTable = PM.queries.inTable(state);
    el.tableBar.hidden = !inTable;
    if (inTable) {
      tableButtons.forEach(function (button) {
        button.disabled = !PM.commands.table[button.dataset.table](state);
      });
    }
  }

  function syncBanner() {
    var entry = entries.get(activeId);
    el.banner.hidden = !entry || !entry.externalChange;
  }

  // ---------- popovers ----------

  function caretRect() {
    try {
      return view.coordsAtPos(view.state.selection.from);
    } catch (e) {
      return el.toolbar.getBoundingClientRect();
    }
  }

  function showPopover(popover, rect, focusTarget) {
    closePopover(false);
    popover.hidden = false;
    openPopover = popover;
    var width = popover.offsetWidth;
    var height = popover.offsetHeight;
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
    var top = rect.bottom + 6;
    if (top + height > window.innerHeight - 8) {
      top = Math.max(8, rect.top - height - 6);
    }
    popover.style.left = Math.round(left) + 'px';
    popover.style.top = Math.round(top) + 'px';
    if (focusTarget) {
      focusTarget.focus();
      focusTarget.select();
    }
  }

  function closePopover(refocus) {
    if (openPopover) {
      openPopover.hidden = true;
      openPopover = null;
      if (refocus && view) {
        view.focus();
      }
    }
  }

  function popoverHasFocus() {
    return !!openPopover && openPopover.contains(document.activeElement);
  }

  // A popover opened by clicking a link or image goes away once the cursor has moved on.
  function closeStalePopover(state, docChanged) {
    if (!openPopover || popoverHasFocus()) {
      return;
    }
    var stale = docChanged
      || (openPopover === el.popLink && !PM.queries.linkAt(state))
      || (openPopover === el.popImage && !PM.queries.imageAt(state))
      || openPopover === el.popTable;
    if (stale) {
      closePopover(false);
    }
  }

  function openLinkPopover(rect, focus) {
    var link = PM.queries.linkAt(view.state);
    var askText = !link && view.state.selection.empty;
    el.linkHref.value = link ? link.href || '' : '';
    el.linkTitle.value = link ? link.title || '' : '';
    el.linkText.value = '';
    el.linkTextRow.hidden = !askText;
    el.linkError.hidden = true;
    el.linkOpen.hidden = !link;
    el.linkRemove.hidden = !link;
    showPopover(el.popLink, rect, focus ? (askText ? el.linkText : el.linkHref) : null);
  }

  function applyLink() {
    var href = el.linkHref.value.trim();
    if (!href || !PM.isSafeHref(href)) {
      el.linkError.hidden = false;
      el.linkHref.focus();
      return;
    }
    var attrs = { href: href, title: el.linkTitle.value.trim() || null };
    var link = PM.queries.linkAt(view.state);
    var selection = view.state.selection;

    if (!link && selection.empty) {
      // One transaction, so a single undo removes the text together with its link.
      var marks = PM.schema.marks.link.create(attrs).addToSet(selection.$from.marks());
      view.dispatch(view.state.tr.insert(selection.from, PM.schema.text(el.linkText.value || href, marks)));
    } else {
      PM.commands.setLink(attrs)(view.state, view.dispatch, view);
    }
    closePopover(true);
  }

  function removeLink() {
    PM.commands.removeLink(view.state, view.dispatch, view);
    closePopover(true);
  }

  // The existing "link" message: LinkClassifier and ExternalLinkDialog stay the only way out.
  function openLink() {
    var link = PM.queries.linkAt(view.state);
    closePopover(true);
    if (link && link.href) {
      post({ type: 'link', href: link.href });
    }
  }

  // queries.imageAt also answers for a caret next to an image, where the button must still insert.
  function selectedImage() {
    return view.state.selection.node ? PM.queries.imageAt(view.state) : null;
  }

  function openImagePopover(rect, focus) {
    var image = selectedImage();
    el.imageSrc.value = image ? image.attrs.src || '' : '';
    el.imageAlt.value = image ? image.attrs.alt || '' : '';
    el.imageTitle.value = image ? image.attrs.title || '' : '';
    el.imageError.hidden = true;
    el.imageApply.textContent = image ? 'Apply' : 'Insert';
    showPopover(el.popImage, rect, focus ? el.imageSrc : null);
  }

  function applyImage() {
    var src = el.imageSrc.value.trim();
    if (!src || !PM.isSafeImageSrc(src)) {
      el.imageError.hidden = false;
      el.imageSrc.focus();
      return;
    }
    var attrs = { src: src, alt: el.imageAlt.value.trim() || null, title: el.imageTitle.value.trim() || null };
    var image = selectedImage();
    var command = image ? PM.commands.updateImage(image.pos, attrs) : PM.commands.insertImage(attrs);
    command(view.state, view.dispatch, view);
    closePopover(true);
  }

  function onImagePicked(msg) {
    var entry = entries.get(msg.tabId || activeId);
    if (!entry || typeof msg.src !== 'string' || !msg.src) {
      return;
    }
    if (entry.tabId === activeId && openPopover === el.popImage) {
      el.imageSrc.value = msg.src;
      if (!el.imageAlt.value && msg.alt) {
        el.imageAlt.value = msg.alt;
      }
      el.imageSrc.focus();
      return;
    }
    if (PM.isSafeImageSrc(msg.src)) {
      execOn(entry, PM.commands.insertImage({ src: msg.src, alt: msg.alt || null, title: null }));
    }
  }

  function openTablePopover(rect) {
    showPopover(el.popTable, rect, el.tableRows);
  }

  function clampField(input, min, max, fallback) {
    var value = parseInt(input.value, 10);
    return isNaN(value) ? fallback : Math.max(min, Math.min(max, value));
  }

  function applyTable() {
    var rows = clampField(el.tableRows, 1, 100, 3);
    var cols = clampField(el.tableCols, 1, 30, 3);
    PM.commands.insertTable(rows, cols)(view.state, view.dispatch, view);
    closePopover(true);
  }

  // ---------- leaving ----------

  // Used by the editor's keymap (which marks the key press as handled) and, for focus outside the
  // document, by the listener in init.
  function onEscape() {
    if (openPopover) {
      closePopover(true);
    } else {
      requestExit();
    }
  }

  // The host decides (it may prompt); the editor stays up until it answers with "exitEdit".
  function requestExit() {
    var entry = entries.get(activeId);
    if (entry) {
      reportDirty(entry);
      post({ type: 'exitEdit', tabId: entry.tabId });
    }
  }

  // ---------- host messages ----------

  function onEdit(msg) {
    if (!(msg.tabId > 0)) {
      return;
    }

    var sameTabShown = viewer.currentTabId() === msg.tabId;
    var entry = entries.get(msg.tabId);
    var top = sameTabShown ? el.scroller.scrollTop : (entry ? entry.scrollTop : 0);

    if (entry && entry.revision === msg.revision) {
      if (msg.tabId !== activeId) {
        attach(entry, entry.scrollTop, false);
      }
      return;
    }

    var created;
    try {
      created = createEntry(msg);
    } catch (e) {
      // Never leave the user in a half-built editor; the host goes back to view mode.
      console.error(e);
      post({ type: 'exitEdit', tabId: msg.tabId });
      return;
    }

    if (msg.tabId === activeId) {
      detach();
    }
    dropEntry(msg.tabId);
    entries.set(msg.tabId, created);
    created.listed = !!entry && entry.listed;
    attach(created, top, !entry);
    // A rebuild replaces whatever the host believed about this tab.
    reportDirty(created, true);
  }

  function onExitEdit(msg) {
    var tabId = msg.tabId || activeId;
    dropEntry(tabId); // first: discarded changes must not be reported as dirty on the way out
    if (tabId === activeId) {
      viewer.leaveEditor();
    }
  }

  function onRequestContent(msg) {
    var entry = entries.get(msg.tabId);
    if (!entry) {
      return;
    }
    var markdown;
    try {
      markdown = PM.serialize(entry.state.doc, entry.baseline);
    } catch (e) {
      console.error(e);
      post({ type: 'editorContent', tabId: entry.tabId, requestId: msg.requestId, error: 'failed' });
      return;
    }
    if (markdown.length > MAX_MARKDOWN_LENGTH) {
      post({ type: 'editorContent', tabId: entry.tabId, requestId: msg.requestId, error: 'tooLarge' });
      return;
    }
    // Typing can continue while the host writes the file; "saved" refers to this snapshot.
    entry.pendingSave = { doc: entry.state.doc, rtl: entry.rtl };
    post({ type: 'editorContent', tabId: entry.tabId, requestId: msg.requestId, markdown: markdown, rtl: entry.rtl });
  }

  function onSaved(msg) {
    var entry = entries.get(msg.tabId);
    if (!entry) {
      return;
    }
    if (typeof msg.revision === 'number') {
      entry.revision = msg.revision;
    }
    entry.savedDoc = entry.pendingSave ? entry.pendingSave.doc : entry.state.doc;
    entry.savedRtl = entry.pendingSave ? entry.pendingSave.rtl : entry.rtl;
    entry.pendingSave = null;
    entry.externalChange = false;
    syncBanner();
    reportDirty(entry, true);
  }

  function onDocumentRenamed(msg) {
    var entry = entries.get(msg.tabId);
    if (!entry) {
      return;
    }
    entry.imageBase = typeof msg.imageBase === 'string' ? msg.imageBase : null;
    if (entry.tabId === activeId) {
      // Rebuild the node views so image previews resolve against the new folder.
      attach(entry, el.scroller.scrollTop, false);
    }
  }

  function onExternalChange(msg) {
    var entry = entries.get(msg.tabId);
    if (entry) {
      entry.externalChange = true;
      syncBanner();
    }
  }

  function onHostMessage(msg) {
    switch (msg.type) {
      case 'edit': onEdit(msg); break;
      case 'exitEdit': onExitEdit(msg); break;
      case 'editorCommand': onEditorCommand(msg); break;
      case 'requestContent': onRequestContent(msg); break;
      case 'saved': onSaved(msg); break;
      case 'documentRenamed': onDocumentRenamed(msg); break;
      case 'externalChange': onExternalChange(msg); break;
      case 'imagePicked': onImagePicked(msg); break;
    }
  }

  // Forgets tabs that have gone from the strip. Only tabs the strip has already listed: an "edit"
  // for a new file may overtake the "tabs" message that announces it.
  function onTabs(tabs) {
    var ids = new Set(tabs.map(function (t) { return t.id; }));
    entries.forEach(function (entry, tabId) {
      if (ids.has(tabId)) {
        entry.listed = true;
      } else if (entry.listed) {
        if (tabId === activeId) {
          detach();
        }
        dropEntry(tabId);
      }
    });
  }

  // ---------- wiring ----------

  function onEnterKey(input, action) {
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') {
        e.preventDefault();
        action();
      }
    });
  }

  function bind(id, action) {
    document.getElementById(id).addEventListener('click', action);
  }

  function init() {
    viewer.setHooks({ message: onHostMessage, tabs: onTabs, hidden: detach });

    el.btnEdit.addEventListener('click', function () {
      if (view) {
        requestExit();
      } else if (viewer.currentTabId()) {
        post({ type: 'enterEdit', tabId: viewer.currentTabId() });
      }
    });

    // Buttons must not take the focus (and with it the selection) away from the document.
    [el.toolbar, el.tableBar].forEach(function (bar) {
      bar.addEventListener('mousedown', function (e) {
        if (e.target.closest('button')) {
          e.preventDefault();
        }
      });
    });
    el.toolbar.addEventListener('click', function (e) {
      var button = e.target.closest('button[data-cmd]');
      if (button) {
        runCommand(button.dataset.cmd, null, button);
      }
    });
    el.tableBar.addEventListener('click', function (e) {
      var button = e.target.closest('button[data-table]');
      if (button && view) {
        PM.commands.table[button.dataset.table](view.state, view.dispatch, view);
        view.focus();
      }
    });
    el.block.addEventListener('change', function () {
      if (!view || !el.block.value) {
        return;
      }
      var heading = /^heading-([1-6])$/.exec(el.block.value);
      runCommand(heading ? 'setHeading' : 'setParagraph', heading ? heading[1] : null, null);
    });

    function currentTab(type) {
      return function () {
        if (activeId) {
          post({ type: type, tabId: activeId });
        }
      };
    }
    bind('tb-save', currentTab('save'));
    bind('tb-save-as', currentTab('saveAs'));
    bind('tb-done', requestExit);
    bind('banner-reload', currentTab('reloadFromDisk'));
    bind('banner-keep', function () {
      // The host keeps its own flag, so saving still asks before overwriting the newer file.
      var entry = entries.get(activeId);
      if (entry) {
        entry.externalChange = false;
      }
      syncBanner();
    });

    bind('pop-link-apply', applyLink);
    bind('pop-link-remove', removeLink);
    bind('pop-link-open', openLink);
    bind('pop-image-apply', applyImage);
    bind('pop-image-browse', currentTab('pickImage'));
    bind('pop-table-apply', applyTable);
    [el.linkText, el.linkHref, el.linkTitle].forEach(function (i) { onEnterKey(i, applyLink); });
    [el.imageSrc, el.imageAlt, el.imageTitle].forEach(function (i) { onEnterKey(i, applyImage); });
    [el.tableRows, el.tableCols].forEach(function (i) { onEnterKey(i, applyTable); });

    document.addEventListener('mousedown', function (e) {
      if (openPopover && !openPopover.contains(e.target)) {
        closePopover(false);
      }
    }, true);
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || e.isComposing || !view || e.defaultPrevented) {
        return;
      }
      e.preventDefault();
      onEscape();
    });
    el.scroller.addEventListener('scroll', function () {
      if (openPopover && !popoverHasFocus()) {
        closePopover(false);
      }
    }, { passive: true });
  }

  // ---------- dev only: plain browser, never under WebView2 ----------

  var DEV_FIXTURE = [
    '# Edit mode fixture',
    '',
    'A paragraph with **bold**, _emphasis_, ~~strike~~, `code` and a [link](https://example.com).',
    '',
    '- [ ] open task',
    '- [x] done task',
    '',
    '| Name | Value |',
    '|------|------:|',
    '| a    | 1     |',
    '',
    '```js',
    'var answer = 42;',
    '```',
    '',
    '<div class="note">Raw <em>HTML</em> block</div>',
    '',
    'Inline <kbd>Ctrl</kbd> and an image: ![alt](images/missing.png)',
    ''
  ].join('\n');

  // ?devEdit=1 gives the page a stand-in for the host: outgoing messages are collected in
  // JustMdDev.sent and host messages can be fed in through JustMdDev.receive.
  function startDevMode() {
    var sent = [];
    viewer.setDevSink(function (message) {
      sent.push(message);
      console.debug('page -> host', message);
    });
    window.JustMdDev = { sent: sent, receive: viewer.receive };
    viewer.receive({ type: 'tabs', tabs: [{ id: 1, name: 'fixture.md', path: 'C:\\fixture.md' }], activeTabId: 1 });
    viewer.receive({ type: 'edit', tabId: 1, revision: 1, markdown: DEV_FIXTURE, rtl: false, fileName: 'fixture.md', filePath: 'C:\\fixture.md', imageBase: null, untitled: false });
  }

  init();
  if (!webview && new URLSearchParams(window.location.search).has('devEdit')) {
    startDevMode();
  }
})();
