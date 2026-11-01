import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorState, TextSelection, basePlugins, commands, history, parse, queries, schema, serialize, undo } from '../src/index.js';
import { run, stateAt } from './helpers.mjs';

const fakeElement = (attrs) => ({ getAttribute: (name) => attrs[name] ?? null, hasAttribute: (name) => name in attrs });

function selectRange(state, needle) {
  let from = null;
  state.doc.descendants((node, pos) => {
    if (from === null && node.isText && node.text.includes(needle)) from = pos + node.text.indexOf(needle);
    return from === null;
  });
  return state.apply(state.tr.setSelection(TextSelection.create(state.doc, from, from + needle.length)));
}

test('link parseDOM rejects unsafe schemes and keeps safe ones', () => {
  const rule = schema.marks.link.spec.parseDOM[0];
  for (const href of ['javascript:alert(1)', 'JaVaScRiPt:alert(1)', ' javascript:alert(1)', 'java\tscript:alert(1)', 'java\nscript:x', 'data:text/html,x', 'vbscript:x', 'file:///c:/x']) {
    assert.equal(rule.getAttrs(fakeElement({ href })), false, href);
  }
  for (const href of ['https://a.example', 'http://a.example', 'mailto:a@b.example', 'rel/path.md', '#anchor', '../up', '?q=a:b']) {
    assert.equal(rule.getAttrs(fakeElement({ href })).href, href);
  }
});

test('image parseDOM rejects data, javascript and other non-image sources', () => {
  const rule = schema.nodes.image.spec.parseDOM[0];
  for (const src of ['data:image/png;base64,AAAA', 'DATA:image/svg+xml,x', ' data:image/png,x', 'javascript:alert(1)', 'blob:https://a/1', 'file:///c:/x.png', '']) {
    assert.equal(rule.getAttrs(fakeElement({ src })), false, src);
  }
  for (const src of ['img/a.png', 'img/a%20b.png', 'https://a.example/x.png', 'C:/pics/x.png']) {
    assert.equal(rule.getAttrs(fakeElement({ src })).src, src);
  }
});

test('Markdown cannot produce a javascript link, even through an entity', () => {
  for (const source of ['[x](javascript:alert(1))', '[x](java&#9;script:alert(1))', '[x](&#106;avascript:alert(1))', '<javascript:alert(1)>']) {
    const { doc } = parse(source);
    doc.descendants((node) => {
      assert.ok(!node.marks.some((mark) => mark.type.name === 'link'), source);
    });
  }
});

test('source attributes are not rendered to the DOM', () => {
  const { doc } = parse('[a]: b\n\ntext\n');
  const spec = doc.firstChild.type.spec.toDOM(doc.firstChild);
  assert.deepEqual(spec, ['p', 0]);
});

test('setLink and insertImage refuse unsafe targets', () => {
  const { state } = stateAt('word\n', 'word');
  assert.equal(commands.setLink({ href: 'javascript:alert(1)' })(state), false);
  assert.equal(commands.insertImage({ src: 'data:image/png;base64,AAAA' })(state), false);
  assert.equal(commands.setLink({ href: 'https://a.example' })(state), true);
  assert.equal(commands.insertImage({ src: 'img/a%20b.png' })(state), true);
});

test('marks toggle and are reported', () => {
  const { state } = stateAt('word here\n', 'word');
  const selected = selectRange(state, 'word');
  for (const [command, mark, output] of [
    [commands.toggleBold, 'strong', '**word** here\n'],
    [commands.toggleItalic, 'em', '*word* here\n'],
    [commands.toggleStrike, 'strike', '~~word~~ here\n'],
    [commands.toggleCode, 'code', '`word` here\n'],
  ]) {
    const on = run(selected, command).state;
    assert.ok(queries.activeMarks(on).has(mark));
    assert.equal(serialize(on.doc, parse('word here\n').baseline), output);
    assert.ok(!queries.activeMarks(run(on, command).state).has(mark));
  }
});

test('block commands and blockType', () => {
  const source = 'word\n';
  const { state, baseline } = stateAt(source, 'word');
  assert.equal(queries.blockType(state), 'paragraph');
  for (let level = 1; level <= 6; level++) {
    const heading = run(state, commands.setHeading(level)).state;
    assert.equal(queries.blockType(heading), `heading-${level}`);
    assert.equal(serialize(heading.doc, baseline), `${'#'.repeat(level)} word\n`);
    assert.equal(queries.blockType(run(heading, commands.setParagraph).state), 'paragraph');
  }
  const code = run(state, commands.setCodeBlock).state;
  assert.equal(queries.blockType(code), 'code_block');
  assert.equal(serialize(code.doc, baseline), '```\nword\n```\n');
  assert.equal(queries.blockType(run(code, commands.setCodeBlock).state), 'paragraph');
  const quote = run(state, commands.toggleBlockquote).state;
  assert.equal(serialize(quote.doc, baseline), '> word\n');
  assert.equal(serialize(run(quote, commands.toggleBlockquote).state.doc, baseline), source);
});

test('changing a block type keeps the definition above it in place', () => {
  const source = 'one\n\n[a]: b\n\ntwo\n\nthree\n';
  const { state, baseline } = stateAt(source, 'two');
  const heading = run(state, commands.setHeading(2)).state;
  assert.equal(serialize(heading.doc, baseline), 'one\n\n[a]: b\n\n## two\n\nthree\n');
  const list = run(state, commands.toggleBulletList).state;
  assert.equal(serialize(list.doc, baseline), 'one\n\n[a]: b\n\n- two\n\nthree\n');
  assert.equal(serialize(run(state, commands.setParagraph).state.doc, baseline), source);
});

test('list commands wrap, convert and lift', () => {
  const { state, baseline } = stateAt('word\n', 'word');
  const bullet = run(state, commands.toggleBulletList).state;
  assert.equal(serialize(bullet.doc, baseline), '- word\n');
  const ordered = run(bullet, commands.toggleOrderedList).state;
  assert.equal(serialize(ordered.doc, baseline), '1. word\n');
  assert.equal(serialize(run(ordered, commands.toggleOrderedList).state.doc, baseline), 'word\n');
});

test('task list command wraps and toggles back off to a paragraph', () => {
  const { state, baseline } = stateAt('word\n', 'word');
  const task = run(state, commands.toggleTaskList).state;
  assert.equal(serialize(task.doc, baseline), '- [ ] word\n');
  assert.equal(serialize(run(task, commands.toggleTaskList).state.doc, baseline), 'word\n');
});

test('task list command marks plain items', () => {
  const { state, baseline } = stateAt('- word\n', 'word');
  assert.equal(serialize(run(state, commands.toggleTaskList).state.doc, baseline), '- [ ] word\n');
});

test('task list off on a nested item leaves a plain item in the outer list', () => {
  const { state, baseline } = stateAt('- outer\n  - [x] inner\n', 'inner');
  assert.equal(serialize(run(state, commands.toggleTaskList).state.doc, baseline), '- outer\n- inner\n');
});

test('toggling a nested item changes only that item', () => {
  const source = '- outer\n  - inner\n';
  const { state, baseline } = stateAt(source, 'inner');
  assert.equal(serialize(run(state, commands.toggleTaskList).state.doc, baseline), '- outer\n  - [ ] inner\n');
});

test('indent and outdent move list items', () => {
  const { state, baseline } = stateAt('- a\n- b\n', 'b');
  const nested = run(state, commands.indent).state;
  assert.equal(serialize(nested.doc, baseline), '- a\n  - b\n');
  assert.equal(serialize(run(nested, commands.outdent).state.doc, baseline), '- a\n- b\n');
});

test('insertTable builds a header row, places the cursor inside and leaves a way out', () => {
  const { state, baseline } = stateAt('word\n', 'word');
  const table = run(state, commands.insertTable(3, 2)).state;
  assert.ok(queries.inTable(table));
  assert.equal(table.doc.lastChild.type, schema.nodes.paragraph);
  const text = serialize(table.doc, baseline);
  const reparsed = parse(text).doc;
  const node = [...Array(reparsed.childCount).keys()].map((i) => reparsed.child(i)).find((n) => n.type === schema.nodes.table);
  assert.equal(node.childCount, 3);
  assert.equal(node.child(0).childCount, 2);
  assert.equal(node.child(0).child(0).type, schema.nodes.table_header);
  assert.equal(node.child(1).child(0).type, schema.nodes.table_cell);
});

test('insertTable and the rule are refused inside a table cell', () => {
  const { state } = stateAt('| a | b |\n|---|---|\n| c | d |\n', 'c');
  assert.equal(commands.insertTable(2, 2)(state), false);
  assert.equal(commands.insertHorizontalRule(state), false);
});

test('insertHorizontalRule writes a rule and leaves a trailing paragraph', () => {
  const { state, baseline } = stateAt('word\n', 'word');
  const next = run(state, commands.insertHorizontalRule).state;
  assert.equal(next.doc.lastChild.type, schema.nodes.paragraph);
  assert.ok(serialize(next.doc, baseline).includes('---'));
});

test('table row and column commands; the header row is protected', () => {
  const source = '| a | b |\n|---|---|\n| c | d |\n';
  const header = stateAt(source, 'a').state;
  assert.ok(queries.inTable(header));
  assert.equal(commands.table.deleteRow(header), false);
  assert.equal(commands.table.addRowBefore(header), false);
  assert.equal(commands.table.addRowAfter(header), true);

  const body = stateAt(source, 'c');
  const { baseline } = body;
  assert.equal(serialize(run(body.state, commands.table.addRowAfter).state.doc, baseline), '| a | b |\n| --- | --- |\n| c | d |\n|  |  |\n');
  assert.equal(serialize(run(body.state, commands.table.addRowBefore).state.doc, baseline), '| a | b |\n| --- | --- |\n|  |  |\n| c | d |\n');
  assert.equal(serialize(run(body.state, commands.table.deleteRow).state.doc, baseline), '| a | b |\n| --- | --- |\n');
  assert.equal(serialize(run(body.state, commands.table.addColumnAfter).state.doc, baseline), '| a |  | b |\n| --- | --- | --- |\n| c |  | d |\n');
  assert.equal(serialize(run(body.state, commands.table.addColumnBefore).state.doc, baseline), '|  | a | b |\n| --- | --- | --- |\n|  | c | d |\n');
  assert.equal(serialize(run(body.state, commands.table.deleteColumn).state.doc, baseline), '| b |\n| --- |\n| d |\n');
  assert.equal(serialize(run(body.state, commands.table.deleteTable).state.doc, baseline), '');
});

test('Tab moves between table cells', () => {
  const { state } = stateAt('| a | b |\n|---|---|\n| c | d |\n', 'c');
  const next = run(state, commands.indent).state;
  assert.equal(next.selection.$from.parent.textContent, 'd');
  assert.equal(run(next, commands.outdent).state.selection.$from.parent.textContent, 'c');
});

test('linkAt, setLink and removeLink', () => {
  const source = 'see [the docs](https://a.example "T") now\n';
  const { state, baseline } = stateAt(source, 'docs');
  const link = queries.linkAt(state);
  assert.equal(link.href, 'https://a.example');
  assert.equal(link.title, 'T');
  assert.equal(state.doc.textBetween(link.from, link.to), 'the docs');

  const updated = run(state, commands.setLink({ href: 'https://b.example', title: '' })).state;
  assert.equal(serialize(updated.doc, baseline), 'see [the docs](https://b.example) now\n');
  assert.equal(serialize(run(state, commands.removeLink).state.doc, baseline), 'see the docs now\n');
  assert.equal(queries.linkAt(stateAt(source, 'now').state), null);
});

test('setLink on a selection, and with an empty selection inserts the address', () => {
  const { state, baseline } = stateAt('word here\n', 'word');
  assert.equal(serialize(run(selectRange(state, 'word'), commands.setLink({ href: 'u' })).state.doc, baseline), '[word](u) here\n');
  assert.equal(serialize(run(state, commands.setLink({ href: 'https://a.example' })).state.doc, baseline), '<https://a.example>word here\n');
});

test('insertImage, imageAt and updateImage', () => {
  const { state, baseline } = stateAt('word\n', 'word');
  const inserted = run(state, commands.insertImage({ src: 'img/a%20b.png', alt: 'A', title: 'T' })).state;
  assert.equal(serialize(inserted.doc, baseline), '![A](img/a%20b.png "T")word\n');
  const image = queries.imageAt(inserted);
  assert.equal(image.attrs.src, 'img/a%20b.png');
  assert.equal(commands.updateImage(image.pos, { src: 'javascript:x' })(inserted), false);
  const updated = run(inserted, commands.updateImage(image.pos, { alt: 'B' })).state;
  assert.equal(serialize(updated.doc, baseline), '![B](img/a%20b.png "T")word\n');
});

test('commands that do not apply report false instead of throwing', () => {
  const { state } = stateAt('```\ncode\n```\n', 'code');
  assert.equal(commands.toggleBold(state), false);
  assert.equal(commands.insertImage({ src: 'a.png' })(state), false);
  assert.equal(commands.setLink({ href: 'u' })(state), false);
  for (const name of ['addRowBefore', 'addRowAfter', 'addColumnBefore', 'addColumnAfter', 'deleteRow', 'deleteColumn', 'deleteTable']) {
    assert.equal(commands.table[name](state), false, name);
  }
});

function pressKey(state, plugins, key) {
  const keyCodes = { Enter: 13, Tab: 9, Escape: 27, Backspace: 8, Delete: 46 };
  let next = state;
  const view = { state, dispatch: (tr) => { next = next.apply(tr); view.state = next; } };
  const event = new Proxy({ key, code: key, keyCode: keyCodes[key] ?? 0, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }, {});
  const handled = plugins.some((plugin) => plugin.props.handleKeyDown?.(view, event));
  return { handled, state: next };
}

test('basePlugins binds Ctrl keys only outside WebView2', () => {
  const press = { key: 'b', code: 'KeyB', keyCode: 66, ctrlKey: true, shiftKey: false, altKey: false, metaKey: false };
  const attempt = (underWebView) => {
    const plugins = basePlugins({ underWebView });
    const { state } = stateAt('word\n', 'word');
    const selected = selectRange(EditorState.create({ doc: state.doc, plugins }), 'word');
    let next = selected;
    const view = { state: selected, dispatch: (tr) => { next = next.apply(tr); } };
    const handled = plugins[0].props.handleKeyDown(view, press);
    return { handled, bold: queries.activeMarks(next).has('strong') };
  };
  assert.deepEqual(attempt(true), { handled: false, bold: false });
  assert.deepEqual(attempt(false), { handled: true, bold: true });
});

test('basePlugins installs the plugins of the contract and history works', () => {
  const plugins = basePlugins({ underWebView: true });
  assert.ok(plugins.length >= 6);
  const { doc } = parse('word\n');
  let state = EditorState.create({ doc, plugins });
  state = state.apply(state.tr.insertText('x', 1));
  assert.notEqual(state.doc.textContent, 'word');
  state = run(state, undo).state;
  assert.equal(state.doc.textContent, 'word');
  assert.ok(state.doc.eq(doc));
  assert.equal(typeof history, 'function');
});

test('Escape calls onEscape; Enter makes an unticked item after a ticked one', () => {
  let escaped = 0;
  const plugins = basePlugins({ underWebView: true, onEscape: () => { escaped++; } });
  const { state } = stateAt('- [x] done\n', 'done');
  const withPlugins = EditorState.create({ doc: state.doc, selection: TextSelection.create(state.doc, state.selection.from + 4), plugins });
  assert.equal(pressKey(withPlugins, plugins, 'Escape').handled, true);
  assert.equal(escaped, 1);
  const pressed = pressKey(withPlugins, plugins, 'Enter');
  assert.equal(pressed.handled, true);
  const list = pressed.state.doc.firstChild;
  assert.deepEqual([list.child(0).attrs.checked, list.child(1).attrs.checked], [true, false]);
});

test('Enter twice at the end of a code block leaves it', () => {
  const plugins = basePlugins({ underWebView: true });
  const { doc } = parse('```\ncode\n```\n');
  const end = doc.firstChild.nodeSize - 1;
  let state = EditorState.create({ doc, selection: TextSelection.create(doc, end), plugins });
  state = pressKey(state, plugins, 'Enter').state;
  assert.equal(state.doc.firstChild.textContent, 'code\n');
  state = pressKey(state, plugins, 'Enter').state;
  assert.equal(state.doc.firstChild.textContent, 'code');
  assert.equal(state.selection.$from.parent.type, schema.nodes.paragraph);
});
