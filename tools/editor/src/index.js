// Entry point of the vendored bundle: window.JustMdEditor. The contract is documented in README.md.

import { baseKeymap, chainCommands } from 'prosemirror-commands';
import { dropCursor } from 'prosemirror-dropcursor';
import { gapCursor } from 'prosemirror-gapcursor';
import { history, undo, redo } from 'prosemirror-history';
import { keymap } from 'prosemirror-keymap';
import { splitListItem } from 'prosemirror-schema-list';
import { EditorState, Plugin, PluginKey, TextSelection } from 'prosemirror-state';
import { columnResizing, deleteCellSelection, isInTable, tableEditing } from 'prosemirror-tables';
import { EditorView } from 'prosemirror-view';
import { commands, insideList, queries } from './commands.js';
import { parse } from './parser.js';
import { isSafeHref, isSafeImageSrc, schema } from './schema.js';
import { serialize } from './serializer.js';

const { nodes } = schema;

function insideCode(state) {
  return state.selection.$from.parent.type === nodes.code_block;
}

// A new paragraph after the code block at the cursor; returns the updated transaction.
function leaveCodeBlock(state, tr) {
  const after = tr.mapping.map(state.selection.$from.after());
  tr.insert(after, nodes.paragraph.create());
  return tr.setSelection(TextSelection.create(tr.doc, after + 1)).scrollIntoView();
}

function enterInCode(state, dispatch) {
  if (!insideCode(state)) return false;
  const { $from, empty } = state.selection;
  if (dispatch) {
    // Enter on an empty last line leaves the block: without Ctrl bindings there must be a way out.
    if (empty && $from.parentOffset === $from.parent.content.size && $from.parent.textContent.endsWith('\n')) {
      dispatch(leaveCodeBlock(state, state.tr.delete($from.pos - 1, $from.pos)));
    } else {
      dispatch(state.tr.insertText('\n').scrollIntoView());
    }
  }
  return true;
}

// splitListItem copies the attributes, so a new item under a ticked one would start ticked.
function enterInList(state, dispatch) {
  return splitListItem(nodes.list_item)(state, dispatch && ((tr) => {
    const { $from } = tr.selection;
    for (let depth = $from.depth; depth > 0; depth--) {
      const node = $from.node(depth);
      if (node.type === nodes.list_item) {
        if (node.attrs.checked === true) tr.setNodeMarkup($from.before(depth), null, { ...node.attrs, checked: false });
        break;
      }
    }
    dispatch(tr);
  }));
}

function shiftEnter(state, dispatch) {
  if (insideCode(state)) {
    if (dispatch) dispatch(leaveCodeBlock(state, state.tr));
    return true;
  }
  const { $from } = state.selection;
  const index = $from.index();
  if (!$from.parent.canReplaceWith(index, index, nodes.hard_break)) return false;
  if (dispatch) dispatch(state.tr.replaceSelectionWith(nodes.hard_break.create()).scrollIntoView());
  return true;
}

// Tab stays in the editor wherever it means something; elsewhere it moves focus for keyboard users.
function tabKey(command) {
  return (state, dispatch) => {
    if (insideCode(state)) {
      if (command === commands.indent && dispatch) dispatch(state.tr.insertText('  ').scrollIntoView());
      return command === commands.indent;
    }
    return command(state, dispatch) || isInTable(state) || insideList(state);
  };
}

function ctrlBindings() {
  const bindings = {
    'Mod-z': commands.undo,
    'Mod-y': commands.redo,
    'Mod-Shift-z': commands.redo,
    'Mod-b': commands.toggleBold,
    'Mod-i': commands.toggleItalic,
    'Mod-Shift-x': commands.toggleStrike,
    'Mod-e': commands.toggleCode,
    'Mod-Shift-0': commands.setParagraph,
    'Mod-Shift-7': commands.toggleOrderedList,
    'Mod-Shift-8': commands.toggleBulletList,
    'Mod-Shift-9': commands.toggleTaskList,
    'Mod-Shift-q': commands.toggleBlockquote,
    'Mod-Shift-c': commands.setCodeBlock,
    'Mod-Shift-h': commands.insertHorizontalRule,
  };
  for (let level = 1; level <= 6; level++) bindings[`Mod-Shift-${level}`] = commands.setHeading(level);
  return bindings;
}

// Under WebView2 the host owns every Ctrl shortcut and posts it as a message; binding them here too
// would run them twice. In a plain browser (dev only) nothing else would handle them.
function basePlugins({ onEscape, underWebView = true } = {}) {
  const bindings = {
    Enter: chainCommands(enterInCode, enterInList, baseKeymap.Enter),
    'Shift-Enter': shiftEnter,
    Tab: tabKey(commands.indent),
    'Shift-Tab': tabKey(commands.outdent),
    Backspace: chainCommands(deleteCellSelection, baseKeymap.Backspace),
    Delete: chainCommands(deleteCellSelection, baseKeymap.Delete),
    Escape: (state, dispatch, view) => {
      onEscape?.(view);
      return true;
    },
    ...(underWebView ? {} : ctrlBindings()),
  };
  return [keymap(bindings), history(), dropCursor(), gapCursor(), columnResizing(), tableEditing()];
}

export {
  parse,
  serialize,
  schema,
  EditorState,
  EditorView,
  Plugin,
  PluginKey,
  TextSelection,
  keymap,
  history,
  undo,
  redo,
  basePlugins,
  commands,
  queries,
  isSafeHref,
  isSafeImageSrc,
};
