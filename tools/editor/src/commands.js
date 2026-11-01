// Editing commands and selection queries. Every command is a ProseMirror Command, so calling it
// without a dispatch function reports whether it applies (the toolbar can use that to enable buttons).

import { lift, toggleMark, wrapIn } from 'prosemirror-commands';
import { undo, redo } from 'prosemirror-history';
import { liftListItem, sinkListItem, wrapInList } from 'prosemirror-schema-list';
import { TextSelection, NodeSelection } from 'prosemirror-state';
import {
  addColumnAfter,
  addColumnBefore,
  addRowAfter,
  addRowBefore,
  deleteColumn,
  deleteRow,
  deleteTable,
  goToNextCell,
  isInTable,
  selectedRect,
} from 'prosemirror-tables';
import { isSafeHref, isSafeImageSrc, schema } from './schema.js';

const { nodes, marks } = schema;

// Like prosemirror-commands' setBlockType, but the block keeps its source attributes: changing a
// heading level must not detach the link definitions written above it.
function setBlockType(type, attrs = {}) {
  const fullAttrs = (node) => ({ ...attrs, srcIdx: node.attrs.srcIdx ?? null, srcPre: node.attrs.srcPre ?? null });
  return (state, dispatch) => {
    let applicable = false;
    for (const { $from, $to } of state.selection.ranges) {
      state.doc.nodesBetween($from.pos, $to.pos, (node, pos) => {
        if (applicable) return false;
        if (!node.isTextblock || node.hasMarkup(type, fullAttrs(node))) return true;
        if (node.type === type) {
          applicable = true;
        } else {
          const $pos = state.doc.resolve(pos);
          const index = $pos.index();
          applicable = $pos.parent.canReplaceWith(index, index + 1, type);
        }
        return !applicable;
      });
    }
    if (!applicable) return false;
    if (dispatch) {
      const { tr } = state;
      for (const { $from, $to } of state.selection.ranges) tr.setBlockType($from.pos, $to.pos, type, fullAttrs);
      dispatch(tr.scrollIntoView());
    }
    return true;
  };
}

const setParagraph = setBlockType(nodes.paragraph);
const setHeading = (level) => setBlockType(nodes.heading, { level });

function setCodeBlock(state, dispatch) {
  if (state.selection.$from.parent.type === nodes.code_block) return setParagraph(state, dispatch);
  return setBlockType(nodes.code_block, { params: '' })(state, dispatch);
}

function enclosingNode(state, predicate) {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth--) {
    const node = $from.node(depth);
    if (predicate(node)) return { node, depth, pos: $from.before(depth) };
  }
  return null;
}

const isList = (node) => node.type === nodes.bullet_list || node.type === nodes.ordered_list;

export const insideList = (state) => enclosingNode(state, isList) !== null;

function toggleList(type) {
  return (state, dispatch) => {
    const list = enclosingNode(state, isList);
    if (list?.node.type === type) return liftListItem(nodes.list_item)(state, dispatch);
    if (list) {
      if (dispatch) {
        const { tight, srcIdx, srcPre } = list.node.attrs;
        dispatch(state.tr.setNodeMarkup(list.pos, type, { tight, srcIdx, srcPre }).scrollIntoView());
      }
      return true;
    }
    return wrapInList(type, { tight: true })(state, dispatch);
  };
}

// The list items whose first paragraph lies in the given selection.
function selectedItems(doc, selection) {
  const items = [];
  for (const { $from, $to } of selection.ranges) {
    doc.nodesBetween($from.pos, $to.pos, (node, pos, parent) => {
      if (parent?.type === nodes.list_item && parent.firstChild === node && node.type === nodes.paragraph) {
        items.push({ node: parent, pos: pos - 1 });
      }
      return true;
    });
  }
  return items;
}

function setChecked(tr, items, checked) {
  for (const { node, pos } of items) tr.setNodeMarkup(pos, null, { ...node.attrs, checked });
  return tr;
}

function toggleTaskList(state, dispatch) {
  const items = selectedItems(state.doc, state.selection);
  if (items.length && items.every(({ node }) => node.attrs.checked !== null)) {
    // Off, like the other list toggles. A nested item lands in the outer list as a plain item.
    let lifted = null;
    if (!liftListItem(nodes.list_item)(state, (tr) => { lifted = tr; })) return false;
    if (dispatch) dispatch(setChecked(lifted, selectedItems(lifted.doc, lifted.selection), null).scrollIntoView());
    return true;
  }
  if (items.length) {
    if (dispatch) dispatch(setChecked(state.tr, items, false).scrollIntoView());
    return true;
  }
  let wrapped = null;
  if (!wrapInList(nodes.bullet_list, { tight: true })(state, (tr) => { wrapped = tr; })) return false;
  if (dispatch) dispatch(setChecked(wrapped, selectedItems(wrapped.doc, wrapped.selection), false).scrollIntoView());
  return true;
}

function toggleBlockquote(state, dispatch) {
  if (enclosingNode(state, (node) => node.type === nodes.blockquote)) return lift(state, dispatch);
  return wrapIn(nodes.blockquote)(state, dispatch);
}

function canInsert(state, type) {
  if (isInTable(state)) return false;
  const { $from } = state.selection;
  for (let depth = $from.depth; depth >= 0; depth--) {
    const index = $from.index(depth);
    if ($from.node(depth).canReplaceWith(index, index, type)) return true;
  }
  return false;
}

// Without a final paragraph there is no place to put the cursor after a table or rule.
function ensureTrailingParagraph(tr) {
  if (tr.doc.lastChild.type !== nodes.paragraph) tr.insert(tr.doc.content.size, nodes.paragraph.create());
  return tr;
}

function insertTable(rows, cols) {
  return (state, dispatch) => {
    if (!canInsert(state, nodes.table)) return false;
    if (dispatch) {
      const makeRow = (cell) => nodes.table_row.create(null, Array.from({ length: Math.max(1, cols) }, () => cell.create()));
      const rowNodes = [makeRow(nodes.table_header)];
      for (let i = 1; i < rows; i++) rowNodes.push(makeRow(nodes.table_cell));
      const tr = state.tr.replaceSelectionWith(nodes.table.create(null, rowNodes));
      // The table is somewhere in the range the replacement changed.
      let tablePos = null;
      tr.mapping.maps[0].forEach((_oldStart, _oldEnd, newStart, newEnd) => {
        tr.doc.nodesBetween(newStart, newEnd, (node, pos) => {
          if (tablePos === null && node.type === nodes.table) tablePos = pos;
          return tablePos === null;
        });
      });
      ensureTrailingParagraph(tr);
      // Table start + row + cell: the inside of the first header cell.
      if (tablePos !== null) tr.setSelection(TextSelection.create(tr.doc, tablePos + 3));
      dispatch(tr.scrollIntoView());
    }
    return true;
  };
}

function insertHorizontalRule(state, dispatch) {
  if (!canInsert(state, nodes.horizontal_rule)) return false;
  if (dispatch) dispatch(ensureTrailingParagraph(state.tr.replaceSelectionWith(nodes.horizontal_rule.create())).scrollIntoView());
  return true;
}

function linkAt(state) {
  const { $from, empty, from, to } = state.selection;
  const parent = $from.parent;
  if (!parent.inlineContent) return null;

  const type = marks.link;
  const mark = (empty ? type.isInSet($from.marks()) : undefined) ?? type.isInSet($from.nodeAfter?.marks ?? []);
  if (!mark) return null;

  // The run of adjacent children carrying this link, if it contains the selection.
  const base = $from.start();
  let start = null;
  let offset = 0;
  for (let i = 0; i <= parent.childCount; i++) {
    const child = i < parent.childCount ? parent.child(i) : null;
    if (child && mark.isInSet(child.marks)) {
      if (start === null) start = offset;
    } else if (start !== null) {
      if (base + start <= from && to <= base + offset) {
        return { href: mark.attrs.href, title: mark.attrs.title, from: base + start, to: base + offset };
      }
      start = null;
    }
    if (child) offset += child.nodeSize;
  }
  return null;
}

function setLink({ href, title = null }) {
  return (state, dispatch) => {
    if (!href || !isSafeHref(href)) return false;
    const { from, to, empty, $from } = state.selection;
    if (!$from.parent.inlineContent || !$from.parent.type.allowsMarkType(marks.link)) return false;
    if (dispatch) {
      const mark = marks.link.create({ href, title: title || null });
      const existing = empty ? linkAt(state) : null;
      if (existing) dispatch(state.tr.removeMark(existing.from, existing.to, marks.link).addMark(existing.from, existing.to, mark));
      else if (empty) dispatch(state.tr.insert(from, schema.text(href, [mark])));
      else dispatch(state.tr.removeMark(from, to, marks.link).addMark(from, to, mark));
    }
    return true;
  };
}

function removeLink(state, dispatch) {
  const { from, to, empty } = state.selection;
  const range = empty ? linkAt(state) : { from, to };
  if (!range || !state.doc.rangeHasMark(range.from, range.to, marks.link)) return false;
  if (dispatch) dispatch(state.tr.removeMark(range.from, range.to, marks.link));
  return true;
}

function insertImage({ src, alt = null, title = null }) {
  return (state, dispatch) => {
    if (!src || !isSafeImageSrc(src)) return false;
    const { $from } = state.selection;
    const index = $from.index();
    if (!$from.parent.canReplaceWith(index, index, nodes.image)) return false;
    if (dispatch) dispatch(state.tr.replaceSelectionWith(nodes.image.create({ src, alt, title }), false).scrollIntoView());
    return true;
  };
}

function updateImage(pos, attrs) {
  return (state, dispatch) => {
    const node = state.doc.nodeAt(pos);
    if (node?.type !== nodes.image) return false;
    if (attrs.src !== undefined && !isSafeImageSrc(attrs.src)) return false;
    if (dispatch) dispatch(state.tr.setNodeMarkup(pos, null, { ...node.attrs, ...attrs }));
    return true;
  };
}

function indent(state, dispatch) {
  if (isInTable(state)) return goToNextCell(1)(state, dispatch);
  return sinkListItem(nodes.list_item)(state, dispatch);
}

function outdent(state, dispatch) {
  if (isInTable(state)) return goToNextCell(-1)(state, dispatch);
  return liftListItem(nodes.list_item)(state, dispatch);
}

// GFM has exactly one header row, always first: nothing can go above it and it cannot be removed.
const outsideHeader = (command) => (state, dispatch) => {
  if (!isInTable(state) || selectedRect(state).top === 0) return false;
  return command(state, dispatch);
};

export const commands = {
  setParagraph,
  setHeading,
  toggleBold: toggleMark(marks.strong),
  toggleItalic: toggleMark(marks.em),
  toggleStrike: toggleMark(marks.strike),
  toggleCode: toggleMark(marks.code),
  toggleBulletList: toggleList(nodes.bullet_list),
  toggleOrderedList: toggleList(nodes.ordered_list),
  toggleTaskList,
  toggleBlockquote,
  setCodeBlock,
  insertTable,
  insertHorizontalRule,
  setLink,
  removeLink,
  insertImage,
  updateImage,
  indent,
  outdent,
  undo,
  redo,
  table: {
    addRowBefore: outsideHeader(addRowBefore),
    addRowAfter,
    addColumnBefore,
    addColumnAfter,
    deleteRow: outsideHeader(deleteRow),
    deleteColumn,
    deleteTable,
  },
};

export const queries = {
  activeMarks(state) {
    const { empty, $from, ranges } = state.selection;
    const active = new Set();
    for (const [name, type] of Object.entries(marks)) {
      const on = empty
        ? type.isInSet(state.storedMarks || $from.marks())
        : ranges.some((range) => state.doc.rangeHasMark(range.$from.pos, range.$to.pos, type));
      if (on) active.add(name);
    }
    return active;
  },
  blockType(state) {
    const { selection } = state;
    const node = selection instanceof NodeSelection ? selection.node : selection.$from.parent;
    return node.type === nodes.heading ? `heading-${node.attrs.level}` : node.type.name;
  },
  linkAt,
  imageAt(state) {
    const { selection } = state;
    if (selection instanceof NodeSelection) {
      return selection.node.type === nodes.image ? { pos: selection.from, attrs: selection.node.attrs } : null;
    }
    if (!selection.empty) return null;
    const { $from } = selection;
    if ($from.nodeAfter?.type === nodes.image) return { pos: $from.pos, attrs: $from.nodeAfter.attrs };
    if ($from.nodeBefore?.type === nodes.image) return { pos: $from.pos - $from.nodeBefore.nodeSize, attrs: $from.nodeBefore.attrs };
    return null;
  },
  inTable: isInTable,
};
