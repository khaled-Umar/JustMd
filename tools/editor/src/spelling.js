// Spell-check underlines. The host checks the text (Windows Spell Checking API, one checker per
// script), this plugin decides what to send and turns the answers into inline decorations.
//
// Nothing here changes the document except `replace`: requests, results and clearWord are
// step-less transactions, so they neither mark a tab dirty nor enter the undo history.
//
// Every offset the host sees is a position: blockText gives one UTF-16 unit per ProseMirror
// position, so offset k of the block at pos is position pos + 1 + k.

import { closeHistory } from 'prosemirror-history';
import { Plugin, PluginKey } from 'prosemirror-state';
import { Mapping } from 'prosemirror-transform';
import { Decoration, DecorationSet } from 'prosemirror-view';

const key = new PluginKey('spelling');

// Mirrors the host's rule for the language and word of spellSuggest, spellIgnore and spellAdd.
const LANGUAGE = /^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$/;
const WORD = /^[^\s\p{Cc}\p{Cs}]{1,128}$/u;
const SUGGESTION = /^[^\p{Cc}\p{Cs}]{1,128}$/u;
const MAX_SUGGESTIONS = 8;

// CORRECTIVE_ACTION. NONE (0) means "no error", so it never becomes an underline.
const ACTION_GET_SUGGESTIONS = 1;
const ACTION_REPLACE = 2;
const ACTION_DELETE = 3;

const isExcluded = (node) => node.type.name === 'code_block' || node.type.name === 'html_block';
const wordKey = (word, language) => `${language}\n${word}`;

// A lone surrogate also becomes a space: the host rejects a message that contains one.
function blockText(node) {
  let text = '';
  node.forEach((child) => {
    if (child.isText) {
      text += child.marks.some((mark) => mark.type.name === 'code') ? ' '.repeat(child.text.length) : child.text;
    } else {
      text += child.type.name === 'hard_break' ? '\n' : ' '.repeat(child.nodeSize);
    }
  });
  return text.replace(/\p{Cs}/gu, ' ');
}

// Calls visit(node, pos) once for each textblock the ranges touch. Each range is widened by one so
// a deletion that left an empty range between two blocks still reaches both.
function forEachTextblock(doc, ranges, visit) {
  const seen = new Set();
  const size = doc.content.size;
  for (const range of ranges) {
    doc.nodesBetween(Math.max(0, range.from - 1), Math.min(size, range.to + 1), (node, pos) => {
      if (!node.isTextblock) return true;
      if (!seen.has(pos)) {
        seen.add(pos);
        visit(node, pos);
      }
      return false;
    });
  }
}

// The blocks worth sending, as { pos, text }: all of them, or those the given ranges touch.
function checkableBlocks(doc, ranges) {
  const blocks = [];
  forEachTextblock(doc, ranges ?? [{ from: 0, to: doc.content.size }], (node, pos) => {
    if (isExcluded(node)) return;
    const text = blockText(node);
    if (/\S/.test(text)) blocks.push({ pos, text });
  });
  return blocks.sort((a, b) => a.pos - b.pos);
}

function chunkBlocks(blocks, { maxBlocks, maxBlockLength, maxRequestLength }) {
  const chunks = [];
  let current = [];
  let length = 0;
  for (const block of blocks) {
    if (block.text.length > maxBlockLength) continue;
    if (current.length && (current.length >= maxBlocks || length + block.text.length > maxRequestLength)) {
      chunks.push(current);
      current = [];
      length = 0;
    }
    current.push(block);
    length += block.text.length;
  }
  if (current.length) chunks.push(current);
  return chunks;
}

// Whether from..to is still exactly this word, whole, in one checkable block, outside code. The
// separators make a split block or an inserted atom compare unequal.
function isWordAt(doc, from, to, word) {
  if (!(from >= 0 && from < to && to <= doc.content.size)) return false;
  const $from = doc.resolve(from);
  if (!$from.parent.isTextblock || isExcluded($from.parent) || !$from.sameParent(doc.resolve(to))) return false;
  if (doc.rangeHasMark(from, to, doc.type.schema.marks.code)) return false;
  return doc.textBetween(from, to, '\n', '\ufffc') === word;
}

function isSuggestion(text) {
  return typeof text === 'string' && SUGGESTION.test(text);
}

// What the host sent for one block, checked against the text that was sent; null if unusable.
function validError(error, text) {
  if (!error || !Number.isInteger(error.start) || !Number.isInteger(error.length)) return null;
  if (error.start < 0 || error.length < 1 || error.start + error.length > text.length) return null;
  if (typeof error.language !== 'string' || !LANGUAGE.test(error.language)) return null;
  let { action } = error;
  if (action !== ACTION_GET_SUGGESTIONS && action !== ACTION_REPLACE && action !== ACTION_DELETE) return null;
  const word = text.slice(error.start, error.start + error.length);
  if (!WORD.test(word)) return null;
  let replacement = null;
  if (action === ACTION_REPLACE) {
    if (isSuggestion(error.replacement)) replacement = error.replacement;
    else action = ACTION_GET_SUGGESTIONS;
  }
  return { start: error.start, length: error.length, word, language: error.language, action, replacement };
}

// A word's underline leaves as soon as an edit makes it wrong, not when the next result arrives.
function dropStale(decorations, doc, ranges) {
  const stale = [];
  forEachTextblock(doc, ranges, (node, pos) => {
    for (const decoration of decorations.find(pos + 1, pos + 1 + node.content.size)) {
      if (!isWordAt(doc, decoration.from, decoration.to, decoration.spec.word)) stale.push(decoration);
    }
  });
  return stale.length ? decorations.remove(stale) : decorations;
}

// What the transaction changed, in the coordinates of its final document. A mark step moves
// nothing but can still change what is checked (the code mark), so it counts too.
function changedRanges(tr) {
  const ranges = [];
  tr.steps.forEach((step, index) => {
    const rest = tr.mapping.slice(index + 1);
    const add = (from, to) => ranges.push({ from: rest.map(from, -1), to: rest.map(to, 1) });
    let moved = false;
    tr.mapping.maps[index].forEach((_oldStart, _oldEnd, from, to) => {
      moved = true;
      add(from, to);
    });
    if (!moved && typeof step.from === 'number') add(step.from, step.to);
  });
  return ranges;
}

function mapPending(pending, tr) {
  const mapped = new Map();
  for (const [id, request] of pending) {
    // slice first: the previous state must stay valid, an apply can be thrown away.
    const mapping = request.mapping.slice();
    mapping.appendMapping(tr.mapping);
    mapped.set(id, { blocks: request.blocks, mapping });
  }
  return mapped;
}

function applyMeta(value, meta, tr) {
  if (meta.sent) {
    const pending = new Map(value.pending);
    for (const request of meta.sent) pending.set(request.id, { blocks: request.blocks, mapping: new Mapping() });
    return { ...value, pending, dirty: [] };
  }
  if (meta.answered !== undefined) {
    const pending = new Map(value.pending);
    pending.delete(meta.answered);
    return { ...value, pending, decorations: value.decorations.remove(meta.remove).add(tr.doc, meta.add) };
  }
  const { word, language } = meta.clear;
  const found = value.decorations.find(undefined, undefined, (spec) => spec.word === word && spec.language === language);
  return { ...value, cleared: new Set(value.cleared).add(wordKey(word, language)), decorations: value.decorations.remove(found) };
}

function plugin({ request, delay = 500, maxBlockLength = 65536, maxRequestLength = 131072, maxBlocks = 1000 }) {
  const limits = { maxBlocks, maxBlockLength, maxRequestLength };
  return new Plugin({
    key,
    state: {
      init: () => ({ decorations: DecorationSet.empty, pending: new Map(), dirty: 'all', cleared: new Set() }),
      apply(tr, previous) {
        let value = previous;
        if (tr.docChanged) {
          const changed = changedRanges(tr);
          const { dirty } = previous;
          value = {
            ...previous,
            decorations: dropStale(previous.decorations.map(tr.mapping, tr.doc), tr.doc, changed),
            pending: mapPending(previous.pending, tr),
            dirty: dirty === 'all' ? dirty : [...dirty.map(({ from, to }) => ({ from: tr.mapping.map(from, -1), to: tr.mapping.map(to, 1) })), ...changed],
          };
        }
        const meta = tr.getMeta(key);
        return meta ? applyMeta(value, meta, tr) : value;
      },
    },
    props: {
      // A word still being typed: no underline until the caret leaves its end.
      decorations(state) {
        const { decorations } = key.getState(state);
        const { empty, head } = state.selection;
        if (!empty) return decorations;
        const atCaret = decorations.find(head, head).filter((decoration) => decoration.to === head);
        return atCaret.length ? decorations.remove(atCaret) : decorations;
      },
    },
    view(editorView) {
      let timer = 0;
      const hasWork = (dirty) => dirty === 'all' || dirty.length > 0;
      const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(flush, delay);
      };
      function flush() {
        timer = 0;
        if (editorView.composing) {
          schedule();
          return;
        }
        const { state } = editorView;
        const { dirty } = key.getState(state);
        if (!hasWork(dirty)) return;
        const scan = dirty === 'all';
        const sent = [];
        for (const chunk of chunkBlocks(checkableBlocks(state.doc, scan ? undefined : dirty), limits)) {
          const id = request(chunk.map((block) => block.text), scan);
          if (id) sent.push({ id, blocks: chunk });
        }
        editorView.dispatch(state.tr.setMeta(key, { sent }).setMeta('addToHistory', false));
      }
      if (hasWork(key.getState(editorView.state).dirty)) schedule();
      return {
        update(_view, previous) {
          const { dirty } = key.getState(editorView.state);
          if (dirty !== key.getState(previous).dirty && hasWork(dirty)) schedule();
        },
        destroy() {
          clearTimeout(timer);
        },
      };
    },
  });
}

// A transaction applying the host's answer, or null if this state is not waiting for it (an answer
// is taken once). A flagged word counts only if it is still there after the edits made since the
// request. The block's old underlines go only if the block still reads as it was sent: otherwise
// it is dirty again and a request of its own settles them, so a late scan answer cannot wipe newer ones.
function results(state, requestId, blocks) {
  const value = key.getState(state);
  const request = value?.pending.get(requestId);
  if (!request) return null;
  const { doc } = state;
  const { mapping } = request;
  const remove = [];
  const add = [];
  const usable = Array.isArray(blocks) && blocks.length === request.blocks.length;
  request.blocks.forEach(({ pos, text }, index) => {
    if (!usable || !Array.isArray(blocks[index])) return;
    const taken = new Set();
    const moved = mapping.map(pos, 1);
    const node = doc.nodeAt(moved);
    if (node && node.isTextblock && blockText(node) === text) {
      remove.push(...value.decorations.find(moved + 1, moved + 1 + node.content.size));
    } else {
      for (const decoration of value.decorations.find(mapping.map(pos + 1, -1), mapping.map(pos + 1 + text.length, 1))) {
        taken.add(`${decoration.from}:${decoration.to}`);
      }
    }
    for (const raw of blocks[index]) {
      const error = validError(raw, text);
      if (!error || value.cleared.has(wordKey(error.word, error.language))) continue;
      const from = mapping.map(pos + 1 + error.start, -1);
      const to = mapping.map(pos + 1 + error.start + error.length, 1);
      const place = `${from}:${to}`;
      if (taken.has(place) || !isWordAt(doc, from, to, error.word)) continue;
      taken.add(place);
      const { word, language, action, replacement } = error;
      add.push(Decoration.inline(from, to, { class: 'spell-error' }, { word, language, action, replacement }));
    }
  });
  return state.tr.setMeta(key, { answered: requestId, remove, add }).setMeta('addToHistory', false);
}

// The error at a position (touching counts), as { from, to, word, language, action, replacement }.
function errorAt(state, pos) {
  const value = key.getState(state);
  if (!value) return null;
  for (const decoration of value.decorations.find(pos, pos)) {
    if (isWordAt(state.doc, decoration.from, decoration.to, decoration.spec.word)) {
      const { from, to, spec: { word, language, action, replacement } } = decoration;
      return { from, to, word, language, action, replacement };
    }
  }
  return null;
}

// A transaction removing every underline of that word and language, including any still on its way.
function clearWord(state, word, language) {
  if (!key.getState(state)) return null;
  return state.tr.setMeta(key, { clear: { word, language } }).setMeta('addToHistory', false);
}

// The marks all of from..to has. What insertText works out itself would drop a link ending with the word.
function sharedMarks(doc, from, to) {
  let shared = null;
  doc.nodesBetween(from, to, (node) => {
    if (node.isInline) shared = shared === null ? node.marks : shared.filter((mark) => mark.isInSet(node.marks));
  });
  return shared ?? [];
}

// One transaction in its own undo step. An empty text deletes the word and one space before it.
// Null if the error no longer matches the document or the text is not a usable suggestion.
function replace(state, error, text) {
  const { doc } = state;
  if (!error || !isWordAt(doc, error.from, error.to, error.word)) return null;
  const tr = state.tr;
  if (text === '') {
    const spaceBefore = doc.resolve(error.from).parentOffset > 0 && doc.textBetween(error.from - 1, error.from) === ' ';
    tr.delete(spaceBefore ? error.from - 1 : error.from, error.to);
  } else if (isSuggestion(text)) {
    tr.setStoredMarks(sharedMarks(doc, error.from, error.to)).insertText(text, error.from, error.to);
  } else {
    return null;
  }
  return closeHistory(tr);
}

// The usable part of a spellSuggestions list: text the page may offer and hand to replace.
function suggestions(list) {
  return Array.isArray(list) ? list.filter(isSuggestion).slice(0, MAX_SUGGESTIONS) : [];
}

export const spelling = { key, plugin, blockText, checkableBlocks, results, errorAt, clearWord, replace, suggestions };
