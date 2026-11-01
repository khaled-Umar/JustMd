import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { EditorState, TextSelection, parse, schema, serialize } from '../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));

export const fixture = (name) => readFileSync(join(here, 'fixtures', name), 'utf8');

export const roundTrip = (markdown) => {
  const { doc, baseline } = parse(markdown);
  return serialize(doc, baseline);
};

// The index of the first top-level block whose text contains the needle.
export function blockIndex(doc, needle) {
  for (let i = 0; i < doc.childCount; i++) if (doc.child(i).textContent.includes(needle)) return i;
  throw new Error(`no block contains ${needle}`);
}

export function blockPos(doc, index) {
  let pos = 0;
  for (let i = 0; i < index; i++) pos += doc.child(i).nodeSize;
  return pos;
}

// Types text at the position of the needle, as the view would.
export function typeBefore(doc, needle, text) {
  let at = null;
  doc.descendants((node, pos) => {
    if (at === null && node.isText && node.text.includes(needle)) at = pos + node.text.indexOf(needle);
    return at === null;
  });
  if (at === null) throw new Error(`no text contains ${needle}`);
  return EditorState.create({ doc }).tr.insertText(text, at).doc;
}

export function deleteBlock(doc, index) {
  const from = blockPos(doc, index);
  return EditorState.create({ doc }).tr.delete(from, from + doc.child(index).nodeSize).doc;
}

export function moveBlock(doc, index, toIndex) {
  const node = doc.child(index);
  const without = deleteBlock(doc, index);
  return EditorState.create({ doc: without }).tr.insert(blockPos(without, toIndex), node).doc;
}

// Lines of `after` that are not in `before` and the other way round, as multisets.
export function changedLines(before, after) {
  const count = new Map();
  for (const line of before.split('\n')) count.set(line, (count.get(line) ?? 0) + 1);
  const added = [];
  for (const line of after.split('\n')) {
    const left = count.get(line) ?? 0;
    if (left > 0) count.set(line, left - 1);
    else added.push(line);
  }
  const removed = [...count].flatMap(([line, n]) => Array(n).fill(line));
  return { added, removed };
}

// An editor state with the cursor at the start of the text containing the needle.
export function stateAt(markdown, needle, extra = {}) {
  const { doc, baseline } = parse(markdown);
  let at = null;
  doc.descendants((node, pos) => {
    if (at === null && node.isText && node.text.includes(needle)) at = pos + node.text.indexOf(needle);
    return at === null;
  });
  if (at === null) throw new Error(`no text contains ${needle}`);
  const state = EditorState.create({ doc, ...extra });
  return { state: state.apply(state.tr.setSelection(TextSelection.create(doc, at))), baseline };
}

export function run(state, command) {
  let next = state;
  const applied = command(state, (tr) => {
    next = state.apply(tr);
  });
  return { applied, state: next };
}

// The same document as if it had no source: forces every block through the Markdown serializer.
export function withoutSource(doc) {
  const blocks = [];
  doc.forEach((node) => blocks.push(node.type.create({ ...node.attrs, srcIdx: null, srcPre: null }, node.content, node.marks)));
  return schema.topNodeType.create(null, blocks);
}
