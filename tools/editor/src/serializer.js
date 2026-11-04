// ProseMirror document -> Markdown. A top-level block equal to its baseline node is emitted as
// its original source lines; anything else goes through the Markdown serializer below.

import { defaultMarkdownSerializer, MarkdownSerializer } from 'prosemirror-markdown';
import { TableMap } from 'prosemirror-tables';
import { imageTag } from './html-image.js';
import { INLINE_SEPARATOR, schema } from './schema.js';

// Markdig syntax markdown-it sees as plain text. Escaping the brackets would destroy it when the
// surrounding block is edited: footnote references/definitions and alert markers.
const MARKDIG_TEXT = /\[\^[^\]\s]+\]|\[![A-Za-z]+\]/g;

// Characters prosemirror-markdown does not escape, but which would parse as HTML or an entity, and
// an underscore next to another one: esc() counts `_` as a word character and leaves it bare in `___`.
const escapeExtraCharacters = /<(?=[a-zA-Z/!?])|&(?=#?[a-zA-Z0-9]+;)|(?<=_)_|(?<!\\)_(?=\\?_)/g;

function destination(url) {
  const clean = url.replace(/[\r\n]/g, '');
  if (/[\s<>]/.test(clean)) return `<${clean.replace(/[<>\\]/g, '\\$&')}>`;
  return clean.replace(/[()\\]/g, '\\$&');
}

const titleSuffix = (title) => (title ? ` "${title.replace(/["\\]/g, '\\$&')}"` : '');

// Stand-ins for whitespace that has to reach the file as an entity. They are noncharacters, so they
// pass through upstream's whitespace handling and esc() untouched; renderRun swaps them at the end.
const ENTITIES = { '\uFDD0': '&#32;', '\uFDD1': '&#9;', '\uFDD2': '&#10;' };
const STAND_IN = { ' ': '\uFDD0', '\t': '\uFDD1', '\n': '\uFDD2' };
const STAND_INS = /[\uFDD0-\uFDD2]/g;

const isCode = (mark) => mark.type === schema.marks.code;
const withoutCode = (marks) => marks.filter((mark) => !isCode(mark));

// The parser strips whitespace at the start of a line (four spaces would even make a code block)
// and at its end (two spaces would make a hard break), so the outermost character becomes an entity.
function pushEdgeEncoded(nodes, text, marks, atStart, atEnd) {
  let encoded = text;
  if (atStart && /^[ \t]/.test(encoded)) encoded = STAND_IN[encoded[0]] + encoded.slice(1);
  if (atEnd && /[ \t]$/.test(encoded)) encoded = encoded.slice(0, -1) + STAND_IN[encoded[encoded.length - 1]];
  nodes.push(schema.text(encoded, marks));
}

// An entity would be literal inside a code span, so edge whitespace leaves the span instead.
function pushLinePart(nodes, text, marks, atStart, atEnd) {
  if (!text) return;
  if (!marks.some(isCode)) {
    pushEdgeEncoded(nodes, text, marks, atStart, atEnd);
    return;
  }
  const lead = atStart ? /^[ \t]*/.exec(text)[0] : '';
  const rest = text.slice(lead.length);
  const trail = atEnd ? /[ \t]*$/.exec(rest)[0] : '';
  const core = rest.slice(0, rest.length - trail.length);
  if (lead) pushEdgeEncoded(nodes, lead, withoutCode(marks), true, atEnd && !rest);
  if (core) nodes.push(schema.text(core, marks));
  if (trail) pushEdgeEncoded(nodes, trail, withoutCode(marks), false, true);
}

const EMPHASIS_MARKS = new Set(['em', 'strong', 'strike']);
const isEmphasis = (mark) => EMPHASIS_MARKS.has(mark.type.name);
const lacking = (marks, other) => marks.filter((mark) => !mark.isInSet(other));

// A delimiter next to a whitespace entity does not work in Markdig, which decodes the entity first, so
// entities at the edge of emphasized text become nodes of their own and normally leave the emphasis.
const LEADING_ENTITY = new RegExp(`^[\\uFDD0-\\uFDD2][\\s\\uFDD0-\\uFDD2]*`);
const TRAILING_ENTITY = new RegExp(`[\\s\\uFDD0-\\uFDD2]*[\\uFDD0-\\uFDD2]$`);
const ONLY_WHITESPACE = new RegExp(`^[\\s\\uFDD0-\\uFDD2]+$`);

// Upstream writes a whitespace-only node in front of the delimiters it should follow, and a delimiter
// after whitespace does not close. Whitespace looks the same either way, so such a node keeps its
// emphasis only where both neighbours have that same emphasis. Whitespace in front of a hard break becomes
// such a node first: upstream's look-ahead skips the node after the break and would keep the mark open.
function releaseWhitespace(nodes) {
  for (let index = nodes.length - 1; index >= 0; index--) {
    const node = nodes[index];
    if (!node.isText || !node.marks.some(isEmphasis)) continue;
    const lead = LEADING_ENTITY.exec(node.text)?.[0] ?? '';
    const trail = TRAILING_ENTITY.exec(node.text.slice(lead.length))?.[0] ?? '';
    if (!lead && !trail) continue;
    const parts = [lead, node.text.slice(lead.length, node.text.length - trail.length), trail].filter(Boolean);
    nodes.splice(index, 1, ...parts.map((part) => schema.text(part, node.marks)));
  }
  for (let index = nodes.length - 2; index >= 0; index--) {
    const node = nodes[index];
    if (!node.isText || !node.marks.some(isEmphasis) || nodes[index + 1].type !== schema.nodes.hard_break) continue;
    const [space] = /\s*$/.exec(node.text);
    if (!space || space === node.text) continue;
    nodes.splice(index, 1, schema.text(node.text.slice(0, -space.length), node.marks), schema.text(space, withoutCode(node.marks)));
  }
  // Until nothing changes: releasing a node can leave the whitespace next to it at the edge of the mark.
  for (let changed = true; changed;) {
    changed = false;
    nodes.forEach((node, index) => {
      if (!node.isText || !ONLY_WHITESPACE.test(node.text)) return;
      const before = nodes[index - 1]?.marks ?? [];
      const after = nodes[index + 1]?.marks ?? [];
      const same = (mark) => mark.isInSet(before) && mark.isInSet(after);
      const sameEmphasis = node.marks.filter(isEmphasis).every(same)
        && lacking(before.filter(isEmphasis), after).length === 0 && lacking(after.filter(isEmphasis), before).length === 0;
      // Markdig trims a line break from the end of a link's text, which would join two words.
      const kept = node.marks.filter((mark) => {
        if (isEmphasis(mark)) return sameEmphasis;
        return mark.type !== schema.marks.link || node.text !== '\n' || same(mark);
      });
      if (kept.length === node.marks.length) return;
      nodes[index] = schema.text(node.text, kept);
      changed = true;
    });
  }
}

// Written as INLINE_SEPARATOR, between a delimiter and a letter.
const SEPARATOR = String.fromCharCode(0xfdd3);
const ERASED = String.fromCharCode(0xfdd4);

// In the file these begin or end with punctuation. Whitespace entities only matter on the far side of
// a delimiter, where Markdig reads them as the space they decode to and markdown-it as `&` or `;`: both pass.
const isStandIn = (char) => char.codePointAt(0) >= 0xfdd0 && char.codePointAt(0) <= 0xfdd3;
const isPunctuation = (char) => isStandIn(char) || /[\p{P}\p{S}]/u.test(char);
const isPlain = (char) => !isPunctuation(char) && !/\s/.test(char);

// CommonMark's flanking rules: `**(a)**b` does not close and `b**(a)**` does not open, so the
// delimiters would stay visible as text. A separator between the delimiter and the letter makes that
// neighbour punctuation. It has to be real punctuation: Markdig decodes an entity before it looks, so
// `**(a)**&#98;` stays literal there. Delimiters of another mark next to the run are punctuation
// already. An `_` (see usesUnderscoreEm) needs this for any letter next to it, or it would count as
// inside a word.
function fixFlanking(nodes, underscoreEm) {
  const isEm = (mark) => mark.type === schema.marks.em;
  for (let index = 0; index + 1 < nodes.length; index++) {
    const left = nodes[index];
    const right = nodes[index + 1];
    if (!left.isText || !right.isText) continue;
    const closes = lacking(left.marks, right.marks);
    const opens = lacking(right.marks, left.marks);
    const leftChars = Array.from(left.text);
    const last = leftChars[leftChars.length - 1];
    const first = Array.from(right.text)[0];
    const closesOnly = opens.length === 0 && closes.some(isEmphasis) && isPunctuation(last);
    const touching = !/\s/.test(last) && !/\s/.test(first);
    if (isPlain(first) && !right.marks.some(isCode) && (closesOnly || (underscoreEm && touching && opens.length === 0 && closes.some(isEm)))) {
      nodes[index + 1] = schema.text(SEPARATOR + right.text, right.marks);
    }
    const opensOnly = closes.length === 0 && opens.some(isEmphasis) && isPunctuation(first);
    // Markdig does not open a code span directly after a backtick, even an escaped one.
    const hidesCode = last === '`' && opens.some(isCode);
    const hidesOpener = hidesCode || (isPlain(last) && (opensOnly || (underscoreEm && touching && closes.length === 0 && opens.some(isEm))));
    // Upstream escapes a `!` in front of a link unless a backslash precedes it, even a literal one.
    // With something erased in between it does escape it, and `\\\!` is what the text needs.
    if (/\\!$/.test(left.text) && opens.some((mark) => mark.type === schema.marks.link) && !left.marks.some(isCode)) {
      leftChars.splice(leftChars.length - 1, 0, ERASED);
      nodes[index] = schema.text(leftChars.join(''), left.marks);
    } else if (!left.marks.some(isCode) && hidesOpener) {
      nodes[index] = schema.text(left.text + SEPARATOR, left.marks);
    }
  }
}

// `**a***b*` runs the delimiters of two marks together and parses differently. Where emphasis or
// strong starts or ends next to the other, this block writes emphasis with `_` to keep them apart.
function usesUnderscoreEm(nodes) {
  const isEm = (mark) => mark.type === schema.marks.em;
  const isStrong = (mark) => mark.type === schema.marks.strong;
  return nodes.some((left, index) => {
    const right = nodes[index + 1];
    if (!right || !left.isText || !right.isText) return false;
    const changed = [...lacking(left.marks, right.marks), ...lacking(right.marks, left.marks)];
    const present = [...left.marks, ...right.marks];
    return (changed.some(isEm) && present.some(isStrong)) || (changed.some(isStrong) && present.some(isEm));
  });
}

// The inline content as upstream's renderInline should see it. Its whitespace regexes use the /m flag
// and keep only the first line of a text node, which would delete the rest, so no text node may
// contain a line terminator next to other text. The result has what renderInline asks of a parent.
function inlineRun(parent, singleLine) {
  const nodes = [];
  let lineStart = true;
  parent.forEach((node, _offset, index) => {
    if (!node.isText) {
      nodes.push(node);
      lineStart = !singleLine && node.type === schema.nodes.hard_break && !node.attrs.source;
      return;
    }
    const next = index + 1 < parent.childCount ? parent.child(index + 1) : null;
    const newLineFollows = !singleLine && next?.isText && next.text.startsWith('\n');
    const lines = (singleLine ? node.text.replace(/\n/g, ' ') : node.text).split('\n');
    lines.forEach((line, lineIndex) => {
      const lastLine = lineIndex === lines.length - 1;
      const parts = line.split(/([\r\u2028\u2029])/);
      parts.forEach((part, partIndex) => {
        const atEnd = partIndex === parts.length - 1 && (!lastLine || newLineFollows);
        pushLinePart(nodes, part, [...node.marks], lineStart && partIndex === 0, atEnd);
      });
      if (line) lineStart = false;
      if (lastLine) return;
      // A second line break in a row would be a blank line, which ends the paragraph.
      nodes.push(schema.text(lineStart ? STAND_IN['\n'] : '\n', withoutCode(node.marks)));
      lineStart = !lineStart;
    });
  });
  // The parser ignores a line break at the very end, and writing one would leave an empty quote line.
  while (nodes.length && nodes[nodes.length - 1].text === '\n') nodes.pop();
  releaseWhitespace(nodes);
  const underscoreEm = usesUnderscoreEm(nodes);
  fixFlanking(nodes, underscoreEm);
  // esc() leaves `_` bare between word characters, where it could now close the emphasis. With
  // something erased in front of it, it no longer looks like it is inside a word.
  if (underscoreEm) {
    nodes.forEach((node, index) => {
      if (node.isText && !node.marks.some(isCode)) nodes[index] = schema.text(node.text.replaceAll('_', `${ERASED}_`), node.marks);
    });
  }
  return {
    underscoreEm,
    childCount: nodes.length,
    child: (index) => nodes[index],
    forEach: (callback) => nodes.forEach((node, index) => callback(node, 0, index)),
  };
}

function renderRun(state, node, fromBlockStart) {
  const start = state.out.length;
  const run = inlineRun(node, state.singleLine);
  state.emDelimiter = run.underscoreEm ? '_' : '*';
  state.renderInline(run, fromBlockStart);
  state.out = state.out.slice(0, start) + state.out.slice(start)
    .replace(STAND_INS, (char) => ENTITIES[char])
    .replaceAll(SEPARATOR, INLINE_SEPARATOR)
    .replaceAll(ERASED, '');
}

// After a source line break or a hard break, even when the text is in a later node than the break.
function atLineStart(state) {
  if (state.singleLine) return false;
  const line = state.out.slice(state.out.lastIndexOf('\n') + 1);
  return line === '' || line === state.delim;
}

// What esc() misses where a block could begin: `1)` or a bare `1.` (a list), `+` with no space
// after it (a list), `===` (turns the line above into a heading) and a table delimiter row that does
// not start with a dash. Returns the part of the line still to write.
function renderLineStart(state, line) {
  const marker = /^\d{1,9}(?=[.)](?:\s|$))|^(?=\+(?:\t|$))|^(?==+[ \t]*$)|^(?=[|:][\s|:-]*-[\s|:-]*$)/.exec(line);
  if (!marker) return line;
  const at = marker[0].length;
  state.write(`${marker[0]}\\${line[at]}`);
  state.atBlockStart = false;
  return line.slice(at + 1);
}

function renderLine(state, source) {
  if (atLineStart(state)) state.atBlockStart = true;
  const line = state.atBlockStart ? renderLineStart(state, source) : source;
  let last = 0;
  for (const match of line.matchAll(MARKDIG_TEXT)) {
    if (match.index > last) state.text(line.slice(last, match.index));
    state.atBlockStart = false;
    state.text(match[0], false);
    last = match.index + match[0].length;
  }
  if (last < line.length) state.text(line.slice(last));
}

// Line by line: every source line break in the text starts a line a block could begin on.
function renderText(state, text) {
  text.split('\n').forEach((line, index) => {
    if (index > 0) {
      state.text('\n', false);
      state.atBlockStart = true;
    }
    renderLine(state, line);
  });
}

// Inline content that must stay on one line: headings and table cells.
function renderSingleLine(state, node) {
  state.singleLine = true;
  renderRun(state, node, false);
  state.singleLine = false;
}

function renderCell(state, cell) {
  const start = state.out.length;
  renderSingleLine(state, cell);
  state.out = state.out.slice(0, start) + state.out.slice(start).replace(/\|/g, '\\|');
}

// A code span with a space at both ends loses one on each side when parsed. Upstream pads a span
// with backticks in it itself, and a span of only spaces is kept as it is.
const codePadding = (node) => (/^ [^]*[^ ][^]* $/.test(node.text) && !node.text.includes('`') ? ' ' : '');

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

const isSized = (image) => image.attrs.width !== null || image.attrs.height !== null;

// A tag alone on the first line of a block starts an HTML block, which runs to the next blank line and
// would swallow what follows as raw HTML. The separator after the tag makes that line a paragraph.
const isLoneSizedImage = (paragraph) => {
  const image = paragraph.childCount === 1 ? paragraph.firstChild : null;
  return image?.type === schema.nodes.image && !image.marks.length && isSized(image);
};
const isList = (node) => node?.type === schema.nodes.bullet_list || node?.type === schema.nodes.ordered_list;

const blockSerializer = new MarkdownSerializer(
  {
    ...defaultMarkdownSerializer.nodes,
    paragraph(state, node, parent, index) {
      renderRun(state, node, true);
      // In a tight list a nested list comes on the next line.
      if (state.inTightList && isLoneSizedImage(node) && isList(parent.maybeChild(index + 1))) state.out += INLINE_SEPARATOR;
      state.closeBlock(node);
    },
    heading(state, node) {
      state.write(`${state.repeat('#', node.attrs.level)} `);
      const start = state.out.length;
      renderSingleLine(state, node);
      // A trailing run of # would read as the closing sequence of the heading.
      state.out = state.out.slice(0, start) + state.out.slice(start).replace(/(^|\s)(#+)(\s*)$/, '$1\\$2$3');
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
    image(state, node, parent, index) {
      if (isSized(node)) {
        state.write(imageTag(node.attrs));
        const next = index + 1 < parent.childCount ? parent.child(index + 1) : null;
        if (index === 0 && !node.marks.length && next?.isText && next.text.startsWith('\n')) state.write(INLINE_SEPARATOR);
        return;
      }
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
    em: {
      ...defaultMarkdownSerializer.marks.em,
      open: (state) => state.emDelimiter,
      close: (state) => state.emDelimiter,
    },
    code: {
      ...defaultMarkdownSerializer.marks.code,
      open: (state, mark, parent, index) => defaultMarkdownSerializer.marks.code.open(state, mark, parent, index) + codePadding(parent.child(index)),
      close: (state, mark, parent, index) => codePadding(parent.child(index - 1)) + defaultMarkdownSerializer.marks.code.close(state, mark, parent, index),
    },
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
  const present = new Set(out);
  blocks.forEach((block, index) => {
    if (emitted.has(index)) return;
    const content = trimBlank(preLines(block.pre));
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
