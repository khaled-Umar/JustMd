# Editor bundle

Builds the ProseMirror engine behind edit mode into two committed files. `dotnet build` never runs node.

| Output | Content |
|---|---|
| `src/JustMd/web/vendor/editor.bundle.js` | IIFE defining the global `JustMdEditor` |
| `src/JustMd/web/vendor/editor.css` | The ProseMirror, tables and gap cursor stylesheets |

```
cd tools/editor
npm ci
npm test
npm run build            # regenerates both files
npm run build -- --check # fails unless both files match a fresh build byte for byte
```

All versions are pinned exactly. The `markdown-it` override keeps prosemirror-markdown (which asks for an older major) on our single copy. The outputs and test fixtures are `-text` in the root `.gitattributes`, so `core.autocrlf` cannot change their bytes.

## Fidelity

`parse` gives every top-level block two attributes, never rendered to the DOM: `srcIdx` (its index in the baseline) and `srcPre` (the verbatim lines between the previous block and this one that produced no block: blank lines, link definitions, some footnote definitions).

`serialize` then writes, per top-level block, its `srcPre` and either its original lines (if the block equals its baseline node) or fresh GFM. The definitions of blocks that no longer exist go to the end. So:

- A document with no edits comes back byte-identical, including syntax only Markdig understands.
- Undo back to the original, or moving a block, restores or keeps its source. The unit is the top-level block: editing one list item rewrites the whole list.

What a rewritten block loses (it renders the same unless noted):

- Source style: `_em_` becomes `*em*`, setext headings become ATX, `[a][ref]` becomes `[a](url)`, indented code becomes fenced, table padding goes away, list numbering becomes sequential, lazy quote lines get markers, indentation of continuation lines goes away.
- Link definitions nested in the rewritten block (inside a quote or list item): usages in that block are inlined, usages elsewhere stop resolving.
- Markdig inline syntax with characters that get escaped: `H~2~O` becomes `H\~2\~O`, which renders literal tildes. Footnote references and `[!NOTE]` markers are protected from this.

Source line breaks inside a paragraph are kept as `\n` in the text, so the editor (`white-space: pre-wrap`) shows a soft break as a line break where view mode shows a space.

A rewritten block keeps its text and structure, so it can contain more than the plain syntax:

- Whitespace at the start of a line and at the end of a wrapped line is written as `&#32;` or `&#9;`, outside any delimiters (`&#32;   **x**`), because Markdig decodes an entity before applying the flanking rules; markers that would start a block are escaped on every line, including after a hard break. Whitespace at the very end of a block is written as it is and the parser drops it.
- A delimiter CommonMark would not accept next to a letter gets the separator `<!-- -->`: `**(a)**b` is written `**(a)**<!-- -->b`. `parse` drops exactly this comment, so it never becomes a node; other comments stay `html_inline`. The page's sanitiser strips it from view mode.
- Where bold and italic start or end next to each other, the block writes italic with `_`: `**a**_b_`.
- The same separator follows a backtick in front of a code span, which Markdig would not open otherwise.
- The noncharacters U+FDD0 to U+FDD4 are used internally while writing, so they cannot survive in rewritten text.

Text that reads as a footnote definition (`[^1]: x`) is written as it is, because `[^1]` is protected above. It comes back as a definition, not a paragraph. Markdig-only syntax is plain text to the editor and is written as it is, so that real uses survive an edit; text that only looks like it renders as that syntax in view mode: `==a==`, `:a:`, `a)`, `: def`, `+---`. An empty bullet directly after a paragraph renders as a literal `-` in Markdig.

## window.JustMdEditor

This contract is frozen; the page (`src/JustMd/web/editor.js`) codes against it.

```js
parse(markdown) -> { doc, baseline }          // baseline: { lines, doc, rtl? } plus internals; otherwise opaque
serialize(doc, baseline) -> string            // LF-only
isSafeHref(href) -> boolean                   // the link rule of the schema and of setLink
isSafeImageSrc(src) -> boolean                // the image rule of the schema, insertImage and updateImage
schema, EditorState, EditorView, Plugin, PluginKey, TextSelection, keymap, history, undo, redo
basePlugins({ onEscape, underWebView }) -> Plugin[]   // keymap (Ctrl bindings only when !underWebView), history, dropCursor, gapCursor, tableEditing, columnResizing
commands: { setParagraph, setHeading(level), toggleBold, toggleItalic, toggleStrike, toggleCode,
  toggleBulletList, toggleOrderedList, toggleTaskList, toggleBlockquote, setCodeBlock,
  insertTable(rows, cols), insertHorizontalRule, setLink({href, title}), removeLink,
  insertImage({src, alt, title}), updateImage(pos, attrs), indent, outdent, undo, redo,
  table: { addRowBefore, addRowAfter, addColumnBefore, addColumnAfter, deleteRow, deleteColumn, deleteTable } }
  // each command is a ProseMirror Command (state, dispatch) => boolean
queries: { activeMarks(state) -> Set<string>, blockType(state) -> 'paragraph'|'heading-1'..'heading-6'|'code_block'|...,
  linkAt(state) -> {href, title, from, to} | null, imageAt(state) -> {pos, attrs} | null, inTable(state) -> boolean }
```

Notes for callers:

- **After a save**, keep the editor state and move only the dirty reference: `entry.baseline = { ...entry.baseline, doc: state.doc }`. Cursor and undo history survive, `state.doc.eq(baseline.doc)` is true again, and the next save still writes untouched blocks from their original source. `serialize` compares against nodes kept inside the baseline, not `baseline.doc`.
- **Do not compare a document with a baseline from a later `parse`** for dirtiness: their source attributes differ even when the text is the same.
- `baseline.rtl` is the caller's to set; `parse` and `serialize` ignore it.
- `setLink`, `insertImage` and `updateImage` return false for targets the schema's link and image rules would reject.
- `setHeading`, `setParagraph` and `setCodeBlock` return false where they change nothing or cannot apply; `setCodeBlock` inside a code block turns it back into a paragraph.
- `table.addRowBefore` and `table.deleteRow` return false on the header row, since GFM's header is always the first row.
- `toggleTaskList` turns plain list items into open tasks, and lifts items out of the list when all of them are already tasks; elsewhere it wraps the selection in a bullet list of open tasks.
- Page-level keys in `basePlugins`: Enter, Shift+Enter (hard break; in a code block, leave it), Tab and Shift+Tab, Backspace, Delete, Escape. Enter on an empty last line of a code block also leaves it. Link, image and table shortcuts open page popovers, so they are not in the plain-browser bindings either.
- Node types the page renders with node views: `image` (`src` is the raw Markdown target), `html_block` and `html_inline` (`attrs.source`), `list_item` (`attrs.checked`: null, true or false), `code_block` (`attrs.params`).
