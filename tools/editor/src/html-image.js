// The one raw HTML form the editor treats as an image: a lone <img> tag with nothing but
// src, alt, title, width and height. Markdig has no other syntax for a sized image.

import { isSafeImageSrc } from './schema.js';

export const MAX_SIZE = 10000;

export const isValidSize = (v) => v === null || (Number.isInteger(v) && v >= 1 && v <= MAX_SIZE);

export const toSize = (s) => (/^\d+$/.test(s ?? '') && isValidSize(Number(s)) ? Number(s) : null);

const ALLOWED = new Set(['src', 'alt', 'title', 'width', 'height']);
const NAMED_ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };
const ENTITY = /&(?:#(\d+|[xX][0-9a-fA-F]+);?|([a-zA-Z][a-zA-Z0-9]*);)/g;

function decodeNumeric(digits) {
  const code = /^x/i.test(digits) ? parseInt(digits.slice(1), 16) : parseInt(digits, 10);
  const valid = code > 0 && code <= 0x10ffff && (code < 0xd800 || code > 0xdfff);
  return valid ? String.fromCodePoint(code) : '�';
}

// Null for a named entity this decoder does not know: the browser would decode it and the editor
// would not, so `javascript&colon;x` must not pass the source check as a relative path.
function decodeValue(value) {
  let known = true;
  const decoded = value.replace(ENTITY, (_match, numeric, named) => {
    if (numeric) return decodeNumeric(numeric);
    if (Object.hasOwn(NAMED_ENTITIES, named)) return NAMED_ENTITIES[named];
    known = false;
    return '';
  });
  return known ? decoded : null;
}

// Null unless the whole string is exactly one such tag. Whatever is refused stays raw HTML.
export function parseImageTag(html) {
  const tag = String(html ?? '').trim();
  const open = /^<img/i.exec(tag);
  if (!open) return null;

  const attribute = /\s+([a-z]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+))/iy;
  const values = new Map();
  let end = open[0].length;
  attribute.lastIndex = end;
  for (let match = attribute.exec(tag); match; match = attribute.exec(tag)) {
    const name = match[1].toLowerCase();
    if (!ALLOWED.has(name) || values.has(name)) return null;
    const value = decodeValue(match[2] ?? match[3] ?? match[4]);
    if (value === null) return null;
    values.set(name, value);
    end = attribute.lastIndex;
  }
  if (!/^\s*\/?>$/.test(tag.slice(end))) return null;

  const src = values.get('src');
  if (src === undefined || !isSafeImageSrc(src)) return null;
  const size = (name) => (values.has(name) ? toSize(values.get(name)) : null);
  const width = size('width');
  const height = size('height');
  if ((values.has('width') && width === null) || (values.has('height') && height === null)) return null;
  return { src, alt: values.get('alt') ?? null, title: values.get('title') ?? null, width, height };
}

// `|` is an entity because a table cell would end there, or keep the backslash if it were escaped.
const ESCAPES = { '&': '&amp;', '"': '&quot;', '<': '&lt;', '>': '&gt;', '|': '&#124;' };
const escapeValue = (value) => String(value).replace(/[&"<>|]/g, (char) => ESCAPES[char]);
const oneLine = (value) => String(value).replace(/[\r\n]+/g, ' ');

export function imageTag({ src, alt, title, width, height }) {
  let tag = `<img src="${escapeValue(String(src).replace(/[\r\n]/g, ''))}"`;
  if (alt) tag += ` alt="${escapeValue(oneLine(alt))}"`;
  if (title) tag += ` title="${escapeValue(oneLine(title))}"`;
  if (width !== null && width !== undefined) tag += ` width="${width}"`;
  if (height !== null && height !== undefined) tag += ` height="${height}"`;
  return `${tag}>`;
}
