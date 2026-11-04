// Document schema: prosemirror-markdown's CommonMark schema plus what edit mode needs.
//
// Additions over the base schema: strike mark, GFM tables, task list items, raw HTML nodes,
// list marker attributes, the optional `source` of a hard break, the size of an image, and the fidelity attributes
// srcIdx/srcPre on every block that can sit at the top level (see parser.js). The fidelity
// attributes are never rendered to the DOM, so copied or pasted content does not carry them.

import { Schema } from 'prosemirror-model';
import { schema as base } from 'prosemirror-markdown';
import { tableNodes } from 'prosemirror-tables';
import { toSize } from './html-image.js';

// Separates a delimiter from a letter without any output: serialize writes it where the flanking
// rules need one and parse drops it, so it never becomes a node. The page's sanitiser strips comments.
export const INLINE_SEPARATOR = '<!-- -->';

// Browsers ignore control characters and whitespace inside a URL scheme, so test without them.
const stripIgnored = (url) => String(url).replace(/[\u0000- \u007f-\u009f]/g, '');
const schemeOf = (url) => {
  const match = /^([a-z][a-z0-9+.-]*):/i.exec(stripIgnored(url));
  return match ? match[1].toLowerCase() : null;
};

// Link targets: http, https, mailto, or a relative/anchor reference.
export function isSafeHref(href) {
  const scheme = schemeOf(href);
  return scheme === null || scheme === 'http' || scheme === 'https' || scheme === 'mailto';
}

// Image sources: http, https, relative, or an absolute Windows path (`c:foo` is neither).
export function isSafeImageSrc(src) {
  const stripped = stripIgnored(src);
  if (!stripped) return false;
  if (/^[a-z]:[\\/]/i.test(stripped)) return true;
  const scheme = schemeOf(src);
  return scheme === null || scheme === 'http' || scheme === 'https';
}

const sourceAttrs = { srcIdx: { default: null }, srcPre: { default: null } };

const withSourceAttrs = (spec) => ({ ...spec, attrs: { ...spec.attrs, ...sourceAttrs } });

const tables = tableNodes({
  tableGroup: 'block',
  cellContent: 'inline*',
  cellAttributes: {
    align: {
      default: null,
      getFromDOM: (dom) => dom.getAttribute('align') || dom.style?.textAlign || null,
      // A presentational attribute rather than a style attribute: the page CSP forbids inline styles.
      setDOMAttr: (value, attrs) => {
        if (value) attrs.align = value;
      },
    },
  },
});

const blockNames = ['paragraph', 'blockquote', 'horizontal_rule', 'heading', 'code_block', 'ordered_list', 'bullet_list'];

let nodes = base.spec.nodes;
for (const name of blockNames) nodes = nodes.update(name, withSourceAttrs(nodes.get(name)));

nodes = nodes
  .update('heading', { ...nodes.get('heading'), content: 'inline*' })
  .update('bullet_list', {
    ...nodes.get('bullet_list'),
    attrs: { ...nodes.get('bullet_list').attrs, bullet: { default: '-' } },
  })
  .update('ordered_list', {
    ...nodes.get('ordered_list'),
    attrs: { ...nodes.get('ordered_list').attrs, delim: { default: '.' } },
  })
  .update('horizontal_rule', {
    ...nodes.get('horizontal_rule'),
    attrs: { ...nodes.get('horizontal_rule').attrs, markup: { default: '---' } },
  })
  .update('list_item', {
    ...nodes.get('list_item'),
    attrs: { checked: { default: null } },
    parseDOM: [
      {
        tag: 'li',
        getAttrs(dom) {
          const marked = dom.getAttribute('data-checked');
          if (marked === 'true' || marked === 'false') return { checked: marked === 'true' };
          const box = dom.querySelector?.(':scope > input[type=checkbox], :scope > p > input[type=checkbox]');
          return { checked: box ? box.hasAttribute('checked') : null };
        },
      },
    ],
    toDOM(node) {
      return node.attrs.checked === null ? ['li', 0] : ['li', { 'data-checked': String(node.attrs.checked) }, 0];
    },
  })
  .update('image', {
    ...nodes.get('image'),
    attrs: { ...nodes.get('image').attrs, width: { default: null }, height: { default: null } },
    parseDOM: [
      {
        tag: 'img[src]',
        getAttrs(dom) {
          const src = dom.getAttribute('src');
          if (!isSafeImageSrc(src)) return false;
          return {
            src,
            title: dom.getAttribute('title'),
            alt: dom.getAttribute('alt'),
            width: toSize(dom.getAttribute('width')),
            height: toSize(dom.getAttribute('height')),
          };
        },
      },
    ],
    toDOM(node) {
      const { src, alt, title, width, height } = node.attrs;
      const attrs = { src, alt, title };
      if (width !== null) attrs.width = width;
      if (height !== null) attrs.height = height;
      return ['img', attrs];
    },
  })
  .update('hard_break', {
    ...nodes.get('hard_break'),
    attrs: { source: { default: null } },
  })
  .addToEnd('table', withSourceAttrs(tables.table))
  .addToEnd('table_row', tables.table_row)
  .addToEnd('table_cell', tables.table_cell)
  .addToEnd('table_header', tables.table_header)
  .addToEnd(
    'html_block',
    withSourceAttrs({
      group: 'block',
      atom: true,
      selectable: true,
      draggable: true,
      attrs: { source: { default: '' } },
      parseDOM: [
        {
          tag: 'pre.md-html-block',
          priority: 60,
          preserveWhitespace: 'full',
          getAttrs: (dom) => ({ source: dom.textContent }),
        },
      ],
      toDOM: (node) => ['pre', { class: 'md-html-block' }, node.attrs.source],
    }),
  )
  .addToEnd('html_inline', {
    group: 'inline',
    inline: true,
    atom: true,
    attrs: { source: { default: '' } },
    parseDOM: [
      {
        tag: 'code.md-html-inline',
        priority: 60,
        getAttrs: (dom) => ({ source: dom.textContent }),
      },
    ],
    toDOM: (node) => ['code', { class: 'md-html-inline' }, node.attrs.source],
  });

const marks = base.spec.marks
  .update('link', {
    ...base.spec.marks.get('link'),
    parseDOM: [
      {
        tag: 'a[href]',
        getAttrs(dom) {
          const href = dom.getAttribute('href');
          if (!isSafeHref(href)) return false;
          return { href, title: dom.getAttribute('title') };
        },
      },
    ],
  })
  .addBefore('code', 'strike', {
    parseDOM: [
      { tag: 's' },
      { tag: 'del' },
      { tag: 'strike' },
      { style: 'text-decoration=line-through' },
    ],
    toDOM: () => ['s', 0],
  });

export const schema = new Schema({ nodes, marks });
