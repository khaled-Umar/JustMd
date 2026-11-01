// ProseMirror document -> Markdown. A top-level block equal to its baseline node is emitted as
// its original source lines; anything else goes through the Markdown serializer below.

import { defaultMarkdownSerializer, MarkdownSerializer } from 'prosemirror-markdown';
import { TableMap } from 'prosemirror-tables';
import { schema } from './schema.js';

// Markdig syntax markdown-it sees as plain text. Escaping the brackets would destroy it when the
// surrounding block is edited: footnote references/definitions and alert markers.
const MARKDIG_TEXT = /\[\^[^\]\s]+\]|\[![A-Za-z]+\]/g;

// Characters prosemirror-markdown does not escape, but which would parse as HTML or an entity.
const escapeExtraCharacters = /<(?=[a-zA-Z/!?])|&(?=#?[a-zA-Z0-9]+;)/g;

function destination(url) {
  const clean = url.replace(/[\r\n]/g, '');
  if (/[\s<>]/.test(clean)) return `<${clean.replace(/[<>\\]/g, '\\$&')}>`;
  return clean.replace(/[()\\]/g, '\\$&');
}

const titleSuffix = (title) => (title ? ` "${title.replace(/["\\]/g, '\\$&')}"` : '');

function renderText(state, text) {
  let last = 0;
  for (const match of text.matchAll(MARKDIG_TEXT)) {
    if (match.index > last) state.text(text.slice(last, match.index));
    state.atBlockStart = false;
    state.text(match[0], false);
    last = match.index + match[0].length;
  }
  if (last < text.length) state.text(text.slice(last));
}

// Inline content that must stay on one line: headings and table cells.
function renderSingleLine(state, node) {
  state.singleLine = true;
  state.renderInline(node, false);
  state.singleLine = false;
}

function renderCell(state, cell) {
  const start = state.out.length;
  renderSingleLine(state, cell);
  state.out = state.out.slice(0, start) + state.out.slice(start).replace(/\|/g, '\\|');
}

const ALIGN_RULES = { left: ':---', center: ':---:', right: '---:' };

function renderTable(state, table) {
  const map = TableMap.get(table);
  const seen = new Set();
  const alignments = new Array(map.width).fill(null);

  for (let row = 0; row < map.height; row++) {
    state.write('|');
    for (let column = 0; column < map.width; column++) {
      const offset = map.map[row * map.width + column];
      state.out += ' ';
      // A merged cell (only pasted HTML can produce one) repeats in the map; GFM has no spans.
      if (!seen.has(offset)) {
        seen.add(offset);
        const cell = table.nodeAt(offset);
        if (row === 0) alignments[column] = cell.attrs.align;
        renderCell(state, cell);
      }
      state.out += ' |';
    }
    state.ensureNewLine();
    if (row === 0) {
      state.write(`|${alignments.map((align) => ` ${ALIGN_RULES[align] ?? '---'} `).join('|')}|`);
      state.ensureNewLine();
    }
  }
  state.closeBlock(table);
}

const blockSerializer = new MarkdownSerializer(
  {
    ...defaultMarkdownSerializer.nodes,
    heading(state, node) {
      state.write(`${state.repeat('#', node.attrs.level)} `);
      renderSingleLine(state, node);
      state.closeBlock(node);
    },
    bullet_list(state, node) {
      state.renderList(node, '  ', () => `${node.attrs.bullet || '-'} `);
    },
    ordered_list(state, node) {
      const start = node.attrs.order ?? 1;
      const width = String(start + node.childCount - 1).length;
      const delim = node.attrs.delim || '.';
      state.renderList(node, state.repeat(' ', width + 2), (i) => {
        const number = String(start + i);
        return `${state.repeat(' ', width - number.length)}${number}${delim} `;
      });
    },
    list_item(state, node) {
      if (node.attrs.checked !== null && node.firstChild?.type.name === 'paragraph') {
        state.write(node.attrs.checked ? '[x] ' : '[ ] ');
      }
      state.renderContent(node);
    },
    image(state, node) {
      const alt = (node.attrs.alt || '').replace(/\n/g, ' ');
      state.write(`![${state.esc(alt)}](${destination(node.attrs.src)}${titleSuffix(node.attrs.title)})`);
    },
    hard_break(state, node, parent, index) {
      if (node.attrs.source) state.write(node.attrs.source);
      else if (state.singleLine) state.write('<br>');
      else defaultMarkdownSerializer.nodes.hard_break(state, node, parent, index);
    },
    text(state, node) {
      if (state.inAutolink) state.text(node.text, false);
      else renderText(state, state.singleLine ? node.text.replace(/\n/g, ' ') : node.text);
    },
    html_block(state, node) {
      state.text(node.attrs.source, false);
      state.closeBlock(node);
    },
    html_inline(state, node) {
      state.text(state.singleLine ? node.attrs.source.replace(/\n/g, ' ') : node.attrs.source, false);
    },
    table: renderTable,
  },
  {
    ...defaultMarkdownSerializer.marks,
    strike: { open: '~~', close: '~~', mixable: true, expelEnclosingWhitespace: true },
    link: {
      ...defaultMarkdownSerializer.marks.link,
      close(state, mark) {
        const { inAutolink } = state;
        state.inAutolink = undefined;
        return inAutolink ? '>' : `](${destination(mark.attrs.href)}${titleSuffix(mark.attrs.title)})`;
      },
    },
  },
  { escapeExtraCharacters },
);

function toMarkdown(node) {
  const wrapper = schema.topNodeType.create(null, [node]);
  return blockSerializer.serialize(wrapper).replace(/\n+$/, '');
}

const isBlank = (line) => /^[ \t]*$/.test(line);

const preLines = (pre) => (pre ? pre.slice(0, -1).split('\n') : []);

function trimBlank(lines) {
  let start = 0;
  let end = lines.length;
  while (start < end && isBlank(lines[start])) start++;
  while (end > start && isBlank(lines[end - 1])) end--;
  return lines.slice(start, end);
}

function ensureBlankLine(out) {
  if (out.length && !isBlank(out[out.length - 1])) out.push('');
}

const hasSource = (node) => node.attrs.srcIdx !== null && node.attrs.srcIdx !== undefined;

// The source attributes that travel with a block. A block wrapped in a list or quote loses them
// at the top level, but its old node still sits inside and keeps its definitions in place.
function sourceOf(node) {
  if (hasSource(node)) return node.attrs;
  let found = null;
  node.descendants((child) => {
    if (!found && hasSource(child)) found = child.attrs;
    return !found;
  });
  return found;
}

export function serialize(doc, baseline) {
  const { lines, blocks, tail } = baseline;
  const out = [];
  const emitted = new Set();
  let previous = null;

  doc.forEach((node) => {
    const original = blocks[node.attrs.srcIdx];
    const verbatim = original !== undefined && node.eq(original.node);
    const text = verbatim ? lines.slice(original.start, original.end) : toMarkdown(node);

    const source = sourceOf(node);
    const index = source ? source.srcIdx : null;
    const fresh = index !== null && !emitted.has(index);
    const pre = fresh ? preLines(source.srcPre) : [];
    if (fresh) emitted.add(index);

    if (text === '') {
      // A block with nothing to write (an emptied paragraph) still keeps its definitions.
      if (trimBlank(pre).length) {
        ensureBlankLine(out);
        out.push(...trimBlank(pre));
      }
      return;
    }

    const inOrder = fresh && (previous ? previous.index !== null && previous.index + 1 === index : index === 0);
    if (inOrder) {
      // The original gap, widened only where a rewritten neighbour needs a blank line.
      if (pre.length === 0) {
        if (previous && !(previous.verbatim && verbatim)) out.push('');
      } else {
        if (previous && !previous.verbatim && !isBlank(pre[0])) out.push('');
        out.push(...pre);
        if (!verbatim && !isBlank(pre[pre.length - 1])) out.push('');
      }
    } else {
      ensureBlankLine(out);
      if (trimBlank(pre).length) out.push(...trimBlank(pre), '');
    }

    out.push(...(verbatim ? text : text.split('\n')));
    previous = { index, verbatim };
  });

  // Definitions of blocks that no longer appear, unless the same lines were written anyway.
  const lost = [];
  blocks.forEach((block, index) => {
    if (emitted.has(index)) return;
    const content = trimBlank(preLines(block.pre));
    const present = new Set(out);
    if (content.some((line) => !isBlank(line) && !present.has(line))) lost.push(content);
  });

  const atOriginalEnd = previous ? previous.index === blocks.length - 1 && previous.verbatim : blocks.length === 0;
  if (atOriginalEnd && lost.length === 0) {
    out.push(...tail);
  } else {
    const content = trimBlank(tail);
    let trailing = 0;
    while (trailing < tail.length && isBlank(tail[tail.length - 1 - trailing])) trailing++;
    if (content.length) {
      ensureBlankLine(out);
      out.push(...content);
    }
    for (const definitions of lost) {
      ensureBlankLine(out);
      out.push(...definitions);
    }
    for (let i = 0; i < trailing; i++) out.push('');
  }

  return out.join('\n');
}
