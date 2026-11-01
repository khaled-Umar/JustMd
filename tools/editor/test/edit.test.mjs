import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorState, parse, schema, serialize } from '../src/index.js';
import { blockIndex, blockPos, changedLines, deleteBlock, fixture, moveBlock, typeBefore, withoutSource } from './helpers.mjs';

test('editing one paragraph changes only its lines', () => {
  const source = fixture('full.md');
  const { doc, baseline } = parse(source);
  const edited = typeBefore(doc, 'Paragraph directly after', 'NEW ');
  const result = serialize(edited, baseline);
  const { added, removed } = changedLines(source, result);
  assert.deepEqual(removed, ['Paragraph directly after a definition.']);
  // The widening blank line is the definition above it being separated from a rewritten block.
  assert.deepEqual(added.filter((line) => line !== ''), ['NEW Paragraph directly after a definition.']);
});

test('editing each block in turn leaves every other block untouched', () => {
  const source = fixture('full.md');
  const { doc, baseline } = parse(source);
  doc.forEach((node, _, index) => {
    if (!node.textContent) return;
    const needle = node.textContent.slice(0, 1);
    const pos = blockPos(doc, index);
    let at = null;
    node.descendants((child, childPos) => {
      if (at === null && child.isText) at = pos + 1 + childPos;
      return at === null;
    });
    const edited = EditorState.create({ doc }).tr.insertText('Z', at).doc;
    const result = serialize(edited, baseline);
    const others = baseline.blocks.filter((_b, i) => i !== index);
    for (const other of others) {
      const text = baseline.lines.slice(other.start, other.end).join('\n');
      assert.ok(result.includes(text), `editing block ${index} (${needle}) disturbed: ${text}`);
    }
  });
});

test('undoing an edit restores the source verbatim', () => {
  const source = fixture('full.md');
  const { doc, baseline } = parse(source);
  const edited = typeBefore(doc, 'Paragraph directly after', 'NEW ');
  assert.notEqual(serialize(edited, baseline), source);
  const at = blockIndex(doc, 'Paragraph directly after');
  const from = blockPos(edited, at) + 1;
  const undone = EditorState.create({ doc: edited }).tr.delete(from, from + 4).doc;
  assert.ok(undone.eq(doc));
  assert.equal(serialize(undone, baseline), source);
});

test('moving a block keeps its source', () => {
  const source = '_one_\n\n~~~\ntwo\n~~~\n\nThree   with spaces\n===\n';
  const { doc, baseline } = parse(source);
  const moved = moveBlock(doc, 2, 0);
  assert.equal(serialize(moved, baseline), 'Three   with spaces\n===\n\n_one_\n\n~~~\ntwo\n~~~\n');
  const back = moveBlock(moved, 0, 2);
  assert.equal(serialize(back, baseline), source);
});

test('deleting the block before a definition keeps the definition in place', () => {
  const { doc, baseline } = parse('first\n\n[a]: b\n\nsecond [a]\n');
  assert.equal(serialize(deleteBlock(doc, 0), baseline), '[a]: b\n\nsecond [a]\n');
});

test('deleting the block a definition is attached to appends the definition', () => {
  const { doc, baseline } = parse('first [a]\n\n[a]: b\n\nsecond\n\nthird\n');
  assert.equal(serialize(deleteBlock(doc, 1), baseline), 'first [a]\n\nthird\n\n[a]: b\n');
});

test('deleting every block keeps definitions and the final newline', () => {
  const { doc, baseline } = parse('[a]: b\n\none\n\n[^1]: note\n');
  const emptied = deleteBlock(doc, 0);
  // Only the document's last block was deleted, leaving the tail, then the appended definition.
  assert.equal(serialize(emptied, baseline), '[^1]: note\n\n[a]: b\n');
});

test('a wrapped paragraph keeps its line breaks when edited', () => {
  const source = 'before\n\nfirst line\nsecond line\nthird line\n\nafter\n';
  const { doc, baseline } = parse(source);
  assert.equal(serialize(typeBefore(doc, 'second', 'x'), baseline), 'before\n\nfirst line\nxsecond line\nthird line\n\nafter\n');
});

test('definition lists and grid tables keep their shape when edited', () => {
  const definition = parse('Term\n: Def one\n: Def two\n');
  assert.equal(serialize(typeBefore(definition.doc, 'Term', 'x'), definition.baseline), 'xTerm\n: Def one\n: Def two\n');
  const grid = parse('+---+---+\n| a | b |\n+===+===+\n| 1 | 2 |\n+---+---+\n');
  assert.equal(
    serialize(typeBefore(grid.doc, 'a |', 'x'), grid.baseline),
    '+---+---+\n| xa | b |\n+===+===+\n| 1 | 2 |\n+---+---+\n',
  );
});

test('definitions in the tail survive edits and stay put', () => {
  const { doc, baseline } = parse('text [a]\n\n[a]: b\n');
  assert.equal(serialize(typeBefore(doc, 'text', 'x'), baseline), 'xtext [a](b)\n\n[a]: b\n');
});

test('a rewritten block is separated from a neighbour it touched in the source', () => {
  const { doc, baseline } = parse('# h\ntext\n');
  const state = EditorState.create({ doc });
  const toParagraph = state.tr.setBlockType(1, 1, schema.nodes.paragraph, { srcIdx: 0, srcPre: '' }).doc;
  assert.equal(serialize(toParagraph, baseline), 'h\n\ntext\n');
});

test('new blocks get one blank line of separation', () => {
  const { doc, baseline } = parse('one\ntwo\n\nthree\n');
  const added = EditorState.create({ doc }).tr.insert(blockPos(doc, 1), schema.nodes.paragraph.create(null, schema.text('new'))).doc;
  assert.equal(serialize(added, baseline), 'one\ntwo\n\nnew\n\nthree\n');
});

test('a split paragraph does not duplicate its definitions', () => {
  const { doc, baseline } = parse('[a]: b\n\nleft right\n');
  const pos = 1 + 'left '.length;
  const split = EditorState.create({ doc }).tr.split(pos).doc;
  assert.equal(serialize(split, baseline), '[a]: b\n\nleft \n\nright\n');
});

test('new table serializes as valid GFM with pipes escaped', () => {
  const { nodes } = schema;
  const cell = (type, text, attrs) => type.create(attrs, text ? schema.text(text) : null);
  const table = nodes.table.create(null, [
    nodes.table_row.create(null, [cell(nodes.table_header, 'a|b', { align: 'left' }), cell(nodes.table_header, 'c', { align: 'center' }), cell(nodes.table_header, '', { align: 'right' })]),
    nodes.table_row.create(null, [
      nodes.table_cell.create(null, schema.text('x|y', [schema.marks.code.create()])),
      cell(nodes.table_cell, 'back\\|slash'),
      nodes.table_cell.create(null, [schema.text('l'), nodes.hard_break.create(), schema.text('m')]),
    ]),
  ]);
  const { baseline } = parse('');
  const doc = schema.topNodeType.create(null, [table]);
  const text = serialize(doc, baseline);
  assert.equal(
    text,
    '| a\\|b | c |  |\n| :--- | :---: | ---: |\n| `x\\|y` | back\\\\\\|slash | l<br>m |\n',
  );
  const reparsed = parse(text).doc.firstChild;
  assert.equal(reparsed.type, nodes.table);
  assert.equal(reparsed.child(0).child(0).textContent, 'a|b');
  assert.equal(reparsed.child(0).child(1).attrs.align, 'center');
  assert.equal(reparsed.child(1).child(0).textContent, 'x|y');
  assert.equal(reparsed.child(1).child(1).textContent, 'back\\|slash');
  assert.equal(reparsed.child(1).child(2).child(1).attrs.source, '<br>');
});

test('task items keep their markers when their list is rewritten', () => {
  const { doc, baseline } = parse('- [ ] open\n- [x] done\n- plain\n');
  const items = doc.firstChild;
  assert.deepEqual([0, 1, 2].map((i) => items.child(i).attrs.checked), [false, true, null]);
  assert.equal(items.child(0).textContent, 'open');
  assert.equal(serialize(typeBefore(doc, 'open', 'still '), baseline), '- [ ] still open\n- [x] done\n- plain\n');
});

test('an empty task item round-trips through the serializer', () => {
  const { doc, baseline } = parse('- [ ] a\n- [x]\n');
  assert.equal(doc.firstChild.child(1).attrs.checked, true);
  const result = serialize(typeBefore(doc, 'a', 'b'), baseline);
  const again = parse(result).doc.firstChild;
  assert.equal(again.child(1).attrs.checked, true);
  assert.equal(again.child(1).textContent, '');
});

test('<br> survives when its paragraph is edited', () => {
  const { doc, baseline } = parse('one<br>two<BR/>three\n');
  assert.equal(doc.firstChild.child(1).type, schema.nodes.hard_break);
  assert.equal(serialize(typeBefore(doc, 'one', 'x'), baseline), 'xone<br>two<BR/>three\n');
});

test('inline HTML survives when its paragraph is edited', () => {
  const { doc, baseline } = parse('press <kbd>Ctrl</kbd> now\n');
  assert.equal(doc.firstChild.child(1).type, schema.nodes.html_inline);
  assert.equal(serialize(typeBefore(doc, 'press', 'x'), baseline), 'xpress <kbd>Ctrl</kbd> now\n');
});

test('footnote references and alert markers survive when their block is edited', () => {
  const footnote = parse('text[^1] and [^note].\n\n[^1]: word\n');
  assert.equal(serialize(typeBefore(footnote.doc, 'text', 'x'), footnote.baseline), 'xtext[^1] and [^note].\n\n[^1]: word\n');
  const definition = parse('[^1]: A footnote with words.\n');
  assert.equal(serialize(typeBefore(definition.doc, 'A footnote', 'x'), definition.baseline), '[^1]: xA footnote with words.\n');
  const alert = parse('> [!NOTE]\n> Body\n');
  assert.equal(serialize(typeBefore(alert.doc, 'Body', 'x'), alert.baseline), '> [!NOTE]\n> xBody\n');
});

test('images keep their raw source through a rewrite', () => {
  const { doc, baseline } = parse('t ![a](images/my%20photo.png) ![b](<my photo (1).png> "T") ![c](C:/x.png)\n');
  const images = [];
  doc.descendants((node) => { if (node.type.name === 'image') images.push(node.attrs.src); });
  assert.deepEqual(images, ['images/my%20photo.png', 'my photo (1).png', 'C:/x.png']);
  assert.equal(
    serialize(typeBefore(doc, 't ', 'x'), baseline),
    'xt ![a](images/my%20photo.png) ![b](<my photo (1).png> "T") ![c](C:/x.png)\n',
  );
});

test('typed characters that would become markup are escaped', () => {
  const { baseline } = parse('');
  const para = (text) => schema.topNodeType.create(null, [schema.nodes.paragraph.create(null, schema.text(text))]);
  for (const text of ['<script>alert(1)</script>', '&amp;', '*a*', '# no', '- no', '[a](b)', 'a < b && c > d', '~~x~~', '`c`', '<!-- c -->']) {
    const reparsed = parse(serialize(para(text), baseline)).doc;
    assert.equal(reparsed.childCount, 1, text);
    assert.equal(reparsed.firstChild.type, schema.nodes.paragraph, text);
    assert.equal(reparsed.firstChild.childCount, 1, text);
    assert.equal(reparsed.firstChild.textContent, text);
    assert.deepEqual(reparsed.firstChild.firstChild.marks, [], text);
  }
});

test('a multi-line setext heading stays one heading when rewritten', () => {
  const { doc, baseline } = parse('a\nb\n===\n');
  const result = serialize(typeBefore(doc, 'a', 'x'), baseline);
  assert.equal(result, '# xa b\n');
});

test('rewriting every block reparses to the same document', () => {
  // Without the Markdig-only and whitespace-sensitive bits: this measures the serializer itself.
  const source = [
    '# H *e* `c`', '', 'p **b** *i* ~~s~~ [l](u "t") <https://a.example> `c` ![a](b.png)', 'second line<br>third\\', 'fourth', '',
    '```js', 'code', '```', '', '~~~', '```', '~~~', '', '    indented', '', '> q', '>', '> - a', '> - b', '',
    '- a', '- b', '  - c', '- [x] d', '', '1. a', '', '   b', '2. c', '', '| a | b |', '|:--|--:|', '| `x\\|y` | |', '',
    '<div>', 'html', '</div>', '', '---', '', '***', '', 'k <kbd>x</kbd>', '',
  ].join('\n');
  const { doc, baseline } = parse(source);
  const rewritten = serialize(withoutSource(doc), baseline);
  const again = parse(rewritten).doc;
  assert.ok(withoutSource(again).eq(withoutSource(doc)), rewritten);
});

test('a baseline reset by replacing baseline.doc keeps saving correctly', () => {
  // What the page does after a save: dirty is measured from the saved document, source stays.
  const source = 'one\n\n[a]: b\n\ntwo\n\nthree   x\n===\n';
  const { doc, baseline } = parse(source);
  const edited = typeBefore(doc, 'two', 'new ');
  const first = serialize(edited, baseline);
  const reset = { ...baseline, doc: edited };
  assert.equal(serialize(edited, reset), first);
  assert.ok(edited.eq(reset.doc));
  // A later edit of another block keeps the first edit and the untouched source.
  assert.equal(serialize(typeBefore(edited, 'one', 'z'), reset), 'zone\n\n[a]: b\n\nnew two\n\nthree   x\n===\n');
});

test('a fresh baseline with an old document never loses or duplicates definitions', () => {
  const source = 'one\n\n[a]: b\n\ntwo [a]\n';
  const { doc, baseline } = parse(source);
  const edited = EditorState.create({ doc }).tr.insert(0, schema.nodes.paragraph.create(null, schema.text('new'))).doc;
  const saved = serialize(edited, baseline);
  const fresh = parse(saved).baseline;
  const again = serialize(edited, fresh);
  assert.equal(again.split('[a]: b').length, 2, again);
  assert.ok(again.includes('new') && again.includes('one') && again.includes('two'));
});
