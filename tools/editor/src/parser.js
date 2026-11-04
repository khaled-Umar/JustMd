// Markdown -> ProseMirror document, recording where each top-level block came from.
//
// markdown-it's token.map gives the source line range of every top-level block. Each block's
// tokens are parsed on their own so the correspondence block <-> lines cannot drift, and the
// node gets two attributes: srcIdx (its index in the baseline) and srcPre (the verbatim lines
// between the previous block and this one: blank lines, link and footnote definitions). Each
// line of srcPre ends in "\n", so "" means no lines at all and "\n" means one blank line.

import MarkdownIt from 'markdown-it';
import { MarkdownParser } from 'prosemirror-markdown';
import { parseImageTag } from './html-image.js';
import { INLINE_SEPARATOR, schema } from './schema.js';

const md = new MarkdownIt('commonmark', { html: true, linkify: false }).enable(['table', 'strikethrough']);

// Keep image and link targets exactly as written: no percent-encoding or punycode.
md.normalizeLink = (url) => url;
md.normalizeLinkText = (url) => url;

// The default check assumes normalizeLink ran first, which would have encoded control characters.
const defaultValidateLink = md.validateLink;
md.validateLink = (url) => defaultValidateLink(url.replace(/[\u0000- \u007f-\u009f]/g, ''));

// `[^1]: word` looks like a link definition to CommonMark, but Markdig reads it as a footnote.
// Without this `[^1]` in the text would become a link and be rewritten once its block is edited.
md.core.ruler.before('inline', 'justmd_footnote_labels', (state) => {
  const references = state.env.references;
  if (!references) return;
  for (const label of Object.keys(references)) if (label.startsWith('^')) delete references[label];
});

// An <img> tag alone in what CommonMark calls an HTML block is a paragraph holding that image. A block
// rule rather than a rewrite of the html_block token, so that the list rule still marks the paragraph
// of a tight list. A line after the tag would be part of the block, and then the block stays raw HTML.
md.block.ruler.before('html_block', 'justmd_image_block', (state, startLine, endLine, silent) => {
  if (state.sCount[startLine] - state.blkIndent >= 4) return false;
  const line = state.src.slice(state.bMarks[startLine] + state.tShift[startLine], state.eMarks[startLine]);
  if (!parseImageTag(line)) return false;
  const next = startLine + 1;
  if (next < endLine && !state.isEmpty(next) && state.sCount[next] >= state.blkIndent) return false;
  if (silent) return true;
  state.line = next;
  state.push('paragraph_open', 'p', 1).map = [startLine, next];
  const inline = state.push('inline', '', 0);
  inline.content = line.trim();
  inline.map = [startLine, next];
  inline.children = [];
  state.push('paragraph_close', 'p', -1);
  return true;
});

const TASK_MARKER = /^\[([ xX])\](?:[ \t]+|$)/;
const HTML_BREAK = /^<br\s*\/?>$/i;

function htmlToImage(token) {
  const image = parseImageTag(token.content);
  if (!image) return;
  token.type = 'image';
  token.tag = 'img';
  token.attrs = [['src', image.src]];
  if (image.title) token.attrs.push(['title', image.title]);
  token.children = [];
  token.meta = { alt: image.alt, width: image.width, height: image.height };
}

md.core.ruler.after('inline', 'justmd_inline_nodes', (state) => {
  const { tokens } = state;
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token.type === 'inline') {
      token.children = token.children.filter((child) => !(child.type === 'html_inline' && child.content === INLINE_SEPARATOR));
      for (const child of token.children) {
        if (child.type === 'html_inline' && HTML_BREAK.test(child.content)) child.type = 'html_break';
        if (child.type === 'html_inline') htmlToImage(child);
        // Keep source line breaks. prosemirror-markdown's default turns them into spaces, which
        // would reflow a wrapped paragraph and destroy alerts, definition lists and grid tables
        // as soon as their block is edited.
        if (child.type === 'softbreak') {
          child.type = 'text';
          child.content = '\n';
        }
      }
    }
    if (token.type !== 'list_item_open' || tokens[i + 1]?.type !== 'paragraph_open') continue;
    const children = tokens[i + 2].children;
    const first = children[0];
    if (first?.type !== 'text') continue;
    const marker = TASK_MARKER.exec(first.content);
    // A bare "[x]" only counts when nothing follows it: "[x]\nmore" is ordinary text.
    if (!marker || (!/\s$/.test(marker[0]) && children.length > 1)) continue;
    token.meta = { ...token.meta, checked: marker[1] !== ' ' };
    first.content = first.content.slice(marker[0].length);
  }
});

function listIsTight(tokens, i) {
  while (++i < tokens.length) if (tokens[i].type !== 'list_item_open') return tokens[i].hidden;
  return false;
}

function cellAttrs(token) {
  const align = /text-align:\s*(left|center|right)/.exec(token.attrGet('style') ?? '');
  return { align: align ? align[1] : null };
}

function imageAttrs(token) {
  return {
    src: token.attrGet('src'),
    title: token.attrGet('title') || null,
    alt: token.meta?.alt || md.renderer.renderInlineAsText(token.children, md.options, {}) || null,
    width: token.meta?.width ?? null,
    height: token.meta?.height ?? null,
  };
}

// The "tokenizer" is the identity: the tokens are produced once, in parse() below.
const tokenParser = new MarkdownParser(schema, { parse: (tokens) => tokens }, {
  paragraph: { block: 'paragraph' },
  blockquote: { block: 'blockquote' },
  heading: { block: 'heading', getAttrs: (token) => ({ level: Number(token.tag.slice(1)) }) },
  hr: { node: 'horizontal_rule', getAttrs: (token) => ({ markup: token.markup }) },
  code_block: { block: 'code_block', noCloseToken: true },
  fence: { block: 'code_block', noCloseToken: true, getAttrs: (token) => ({ params: token.info || '' }) },
  bullet_list: {
    block: 'bullet_list',
    getAttrs: (token, tokens, i) => ({ tight: listIsTight(tokens, i), bullet: token.markup }),
  },
  ordered_list: {
    block: 'ordered_list',
    getAttrs: (token, tokens, i) => ({
      tight: listIsTight(tokens, i),
      order: token.attrGet('start') === null ? 1 : Number(token.attrGet('start')),
      delim: token.markup,
    }),
  },
  list_item: { block: 'list_item', getAttrs: (token) => ({ checked: token.meta?.checked ?? null }) },
  table: { block: 'table' },
  thead: { ignore: true },
  tbody: { ignore: true },
  tr: { block: 'table_row' },
  th: { block: 'table_header', getAttrs: cellAttrs },
  td: { block: 'table_cell', getAttrs: cellAttrs },
  html_block: { node: 'html_block', getAttrs: (token) => ({ source: token.content.replace(/\n+$/, '') }) },
  html_inline: { node: 'html_inline', getAttrs: (token) => ({ source: token.content }) },
  html_break: { node: 'hard_break', getAttrs: (token) => ({ source: token.content }) },
  hardbreak: { node: 'hard_break' },
  image: { node: 'image', getAttrs: imageAttrs },
  em: { mark: 'em' },
  strong: { mark: 'strong' },
  s: { mark: 'strike' },
  link: {
    mark: 'link',
    getAttrs: (token) => ({ href: token.attrGet('href'), title: token.attrGet('title') || null }),
  },
  code_inline: { mark: 'code', noCloseToken: true },
});

function topLevelGroups(tokens) {
  const groups = [];
  for (let i = 0; i < tokens.length; i++) {
    const first = i;
    if (tokens[i].nesting === 1) {
      let depth = 1;
      while (depth > 0) depth += tokens[++i].nesting;
    }
    groups.push({ tokens: tokens.slice(first, i + 1), map: tokens[first].map });
  }
  return groups;
}

const isBlankLine = (line) => /^[ \t]*$/.test(line);

// Returns the document and the baseline serialize() compares it with. The baseline keeps its own
// reference to each original node, so it stays correct whatever a caller does with baseline.doc.
export function parse(markdown) {
  const source = String(markdown ?? '').replace(/\r\n?/g, '\n');
  const lines = source.split('\n');
  const blocks = [];
  const children = [];
  let cursor = 0;

  for (const group of topLevelGroups(md.parse(source, {}))) {
    const start = Math.max(group.map[0], cursor);
    let end = Math.min(group.map[1], lines.length);
    // A list's range can include the blank lines after it; those belong to the gap.
    while (end > start + 1 && isBlankLine(lines[end - 1])) end--;

    const pre = lines.slice(cursor, start).map((line) => `${line}\n`).join('');
    const parsed = tokenParser.parse(group.tokens).firstChild;
    const node = parsed.type.create({ ...parsed.attrs, srcIdx: blocks.length, srcPre: pre }, parsed.content, parsed.marks);
    blocks.push({ node, start, end, pre });
    children.push(node);
    cursor = end;
  }

  const doc = schema.topNodeType.create(null, children.length ? children : [schema.nodes.paragraph.create()]);
  return { doc, baseline: { lines, doc, blocks, tail: lines.slice(cursor) } };
}
