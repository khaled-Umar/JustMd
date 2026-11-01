import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorState, TextSelection, basePlugins, commands, parse, schema, serialize, spelling, undo } from '../src/index.js';
import { blockPos } from './helpers.mjs';

const { blockText, checkableBlocks } = spelling;

// A state carrying the plugin, plus the fake host's list of requests.
function setup(markdown, options = {}) {
  const sent = [];
  const plugin = spelling.plugin({
    request(blocks, scan) {
      sent.push({ id: sent.length + 1, blocks, scan });
      return sent.length;
    },
    ...options,
  });
  const { doc, baseline } = parse(markdown);
  const state = EditorState.create({ schema, doc, plugins: [...basePlugins({ underWebView: false }), plugin] });
  return { state, plugin, sent, baseline };
}

// Stands in for the EditorView, so the plugin's timers and dispatches run without a DOM.
function mount({ state, plugin }, t) {
  t.mock.timers.reset();
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const view = {
    state,
    composing: false,
    dispatch(tr) {
      const previous = view.state;
      view.state = previous.apply(tr);
      pluginView.update(view, previous);
    },
  };
  const pluginView = plugin.spec.view(view);
  return { view, tick: (ms = 500) => t.mock.timers.tick(ms), destroy: () => pluginView.destroy() };
}

// The position of the first occurrence of a needle in a text node.
function find(doc, needle) {
  let at = null;
  doc.descendants((node, pos) => {
    if (at === null && node.isText && node.text.includes(needle)) at = pos + node.text.indexOf(needle);
    return at === null;
  });
  if (at === null) throw new Error(`no text contains ${needle}`);
  return at;
}

const decorationsOf = (state) => spelling.key.getState(state).decorations.find();
const words = (state) => decorationsOf(state).map((d) => state.doc.textBetween(d.from, d.to)).sort();

// One request for all checkable blocks, as the view would record it. Returns the state and id.
function flushed(context, t) {
  const mounted = mount({ ...context }, t);
  mounted.tick();
  t.mock.timers.reset();
  return { state: mounted.view.state, id: context.sent.at(-1)?.id, text: context.sent.at(-1)?.blocks };
}

function errorFor(text, word, extra = {}) {
  return { start: text.indexOf(word), length: word.length, language: 'en-US', action: 1, replacement: null, ...extra };
}

function answered(state, id, blocks) {
  const tr = spelling.results(state, id, blocks);
  assert.ok(tr, 'the request is pending');
  return state.apply(tr);
}

test('blockText masks code marks, atoms and hard breaks and lines up with every position', () => {
  const { doc } = parse('Say `co de` now 😀 ok<br>x ![a](b.png) end  \nnext\n');
  const block = doc.child(0);
  const text = blockText(block);
  assert.equal(text.length, block.content.size);
  assert.ok(text.startsWith('Say       now 😀 ok'));
  assert.ok(!text.includes('co de'));
  assert.ok(text.includes('\nnext'));
  for (let k = 0; k < text.length; k++) {
    const piece = doc.textBetween(1 + k, 2 + k, '', '#');
    if (piece === '#') assert.match(text[k], /[ \n]/, `atom at ${k}`);
    else if (text[k] !== ' ') assert.equal(text[k], piece, `offset ${k}`);
  }
  const emoji = text.indexOf('😀');
  assert.equal(doc.textBetween(1 + emoji, 1 + emoji + 2), '😀');
});

test('blockText turns a lone surrogate into one space', () => {
  const block = schema.nodes.paragraph.create(null, schema.text('a\ud83dz'));
  assert.equal(blockText(block), 'a z');
});

test('checkableBlocks skips code, HTML and blank blocks and keeps headings, list items and table cells', () => {
  const { doc } = parse('# Title\n\nbody\n\n```\ncode\n```\n\n<div>html</div>\n\n- item\n\n| head |\n|---|\n| cell |\n\n&#32;\n');
  const texts = checkableBlocks(doc).map((b) => b.text);
  assert.deepEqual(texts, ['Title', 'body', 'item', 'head', 'cell']);
  for (const { pos, text } of checkableBlocks(doc)) assert.equal(doc.textBetween(pos + 1, pos + 1 + text.length), text);
});

test('a first scan sends the whole document once, marked as a scan', (t) => {
  const context = setup('one\n\ntwo\n');
  const { view, tick } = mount(context, t);
  assert.equal(context.sent.length, 0);
  tick(499);
  assert.equal(context.sent.length, 0);
  tick(1);
  assert.deepEqual(context.sent.map((r) => [r.blocks, r.scan]), [[['one', 'two'], true]]);
  assert.deepEqual(spelling.key.getState(view.state).dirty, []);
  tick(5000);
  assert.equal(context.sent.length, 1);
});

test('requests stay inside every limit and an oversized block is skipped', (t) => {
  const long = 'x'.repeat(60);
  const context = setup(`${long}\n\n${long}\n\n${'y'.repeat(101)}\n\nab\n\ncd\n\nef\n`, { maxBlockLength: 100, maxRequestLength: 120, maxBlocks: 2 });
  mount(context, t).tick();
  assert.deepEqual(context.sent.map((r) => r.blocks), [[long, long], ['ab', 'cd'], ['ef']]);
});

test('a block exactly at the limits is sent', (t) => {
  const context = setup(`${'x'.repeat(100)}\n\n${'y'.repeat(20)}\n`, { maxBlockLength: 100, maxRequestLength: 120 });
  mount(context, t).tick();
  assert.equal(context.sent.length, 1);
  assert.equal(context.sent[0].blocks.length, 2);
});

test('typing dirties only its block and the request waits for the last change', (t) => {
  const context = setup('one\n\ntwo\n\nthree\n');
  const { view, tick } = mount(context, t);
  tick();
  const at = find(view.state.doc, 'two');
  view.dispatch(view.state.tr.insertText('x', at));
  tick(300);
  view.dispatch(view.state.tr.insertText('y', at + 1));
  tick(499);
  assert.equal(context.sent.length, 1);
  tick(1);
  assert.deepEqual(context.sent.map((r) => [r.blocks, r.scan]), [[['one', 'two', 'three'], true], [['xytwo'], false]]);
});

test('toggling the code mark sends the block again with the word masked', (t) => {
  const context = setup('one two\n');
  const { view, tick } = mount(context, t);
  tick();
  const at = find(view.state.doc, 'two');
  view.dispatch(view.state.tr.addMark(at, at + 3, schema.marks.code.create()));
  tick();
  assert.deepEqual(context.sent.at(-1).blocks, ['one    ']);
});

test('nothing is requested while an IME composition is running', (t) => {
  const context = setup('one\n');
  const { view, tick } = mount(context, t);
  view.composing = true;
  tick();
  assert.equal(context.sent.length, 0);
  view.composing = false;
  tick();
  assert.equal(context.sent.length, 1);
});

test('a state that was never shown scans on its first attach, and a later attach does not', (t) => {
  const context = setup('one\n');
  const { view, tick, destroy } = mount(context, t);
  tick();
  destroy();
  mount({ state: view.state, plugin: context.plugin }, t).tick(5000);
  assert.equal(context.sent.length, 1);
});

test('edits made while no view is attached are sent on the next attach', (t) => {
  const context = setup('one\n\ntwo\n');
  const { view, tick, destroy } = mount(context, t);
  tick();
  destroy();
  const background = view.state.apply(view.state.tr.insertText('x', find(view.state.doc, 'two')));
  mount({ state: background, plugin: context.plugin }, t).tick();
  assert.deepEqual(context.sent.at(-1).blocks, ['xtwo']);
  assert.equal(context.sent.at(-1).scan, false);
});

test('results create spell-error decorations at the right positions', (t) => {
  const context = setup('A wrod here\n\nسلام wrld\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod')], [errorFor(text[1], 'wrld', { language: 'ar-SA' })]]);
  const decorations = decorationsOf(next);
  assert.deepEqual(words(next), ['wrld', 'wrod']);
  assert.equal(decorations[0].from, find(next.doc, 'wrod'));
  assert.equal(decorations[0].type.attrs.class, 'spell-error');
  assert.deepEqual(spelling.errorAt(next, find(next.doc, 'wrld') + 1), {
    from: find(next.doc, 'wrld'), to: find(next.doc, 'wrld') + 4, word: 'wrld', language: 'ar-SA', action: 1, replacement: null,
  });
  assert.equal(spelling.errorAt(next, find(next.doc, 'here')), null);
});

test('results neither change the document nor enter the history', (t) => {
  const context = setup('A wrod here\n');
  const { state, id, text } = flushed(context, t);
  const tr = spelling.results(state, id, [[errorFor(text[0], 'wrod')]]);
  assert.equal(tr.docChanged, false);
  const next = state.apply(tr);
  assert.equal(next.doc, state.doc);
  assert.equal(undo(next), false);
  const cleared = next.apply(spelling.clearWord(next, 'wrod', 'en-US'));
  assert.equal(cleared.doc, state.doc);
  assert.equal(undo(cleared), false);
  assert.equal(serialize(cleared.doc, context.baseline), 'A wrod here\n');
});

test('an answer is taken once and an unknown id is ignored', (t) => {
  const context = setup('A wrod\n');
  const { state, id, text } = flushed(context, t);
  assert.equal(spelling.results(state, id + 1, [[]]), null);
  const next = answered(state, id, [[errorFor(text[0], 'wrod')]]);
  assert.equal(spelling.results(next, id, [[]]), null);
  assert.equal(spelling.key.getState(next).pending.size, 0);
});

test('without the plugin every entry point answers null', () => {
  const state = EditorState.create({ schema, doc: parse('x\n').doc });
  assert.equal(spelling.results(state, 1, [[]]), null);
  assert.equal(spelling.errorAt(state, 1), null);
  assert.equal(spelling.clearWord(state, 'x', 'en-US'), null);
});

test('malformed results are dropped without touching the state', (t) => {
  const context = setup('A wrod here\n');
  const { state, id, text } = flushed(context, t);
  const good = errorFor(text[0], 'wrod');
  for (const blocks of [null, 'x', {}, [], [[], []]]) {
    assert.deepEqual(words(answered(state, id, blocks)), [], JSON.stringify(blocks));
  }
  for (const bad of [
    null, 'x', {},
    { ...good, start: -1 }, { ...good, start: 1.5 }, { ...good, start: '2' },
    { ...good, length: 0 }, { ...good, length: 1000 }, { ...good, start: text[0].length, length: 1 },
    { ...good, language: 'not a tag' }, { ...good, language: 7 }, { ...good, language: '<b>' },
    { ...good, action: 0 }, { ...good, action: 4 }, { ...good, action: '1' },
    { ...good, length: 'wrod here'.length },
  ]) {
    assert.deepEqual(words(answered(state, id, [[bad]])), [], JSON.stringify(bad));
  }
  assert.deepEqual(words(answered(state, id, [[null, good]])), ['wrod']);
});

test('a range that would split a surrogate pair is dropped', (t) => {
  const context = setup('a😀b\n');
  const { state, id } = flushed(context, t);
  assert.deepEqual(words(answered(state, id, [[{ start: 1, length: 1, language: 'en-US', action: 1 }]])), []);
});

test('a word over 128 characters is not underlined: the host would refuse it', (t) => {
  const context = setup(`${'x'.repeat(129)}\n`);
  const { state, id } = flushed(context, t);
  assert.deepEqual(words(answered(state, id, [[{ start: 0, length: 129, language: 'en-US', action: 1 }]])), []);
});

test('a replace action without a usable replacement falls back to suggestions', (t) => {
  const context = setup('A wrod\n');
  const { state, id, text } = flushed(context, t);
  const pos = find(state.doc, 'wrod');
  const bad = answered(state, id, [[errorFor(text[0], 'wrod', { action: 2, replacement: 'a\nb' })]]);
  assert.equal(spelling.errorAt(bad, pos).action, 1);
  assert.equal(spelling.errorAt(bad, pos).replacement, null);
  const context2 = setup('A wrod\n');
  const second = flushed(context2, t);
  const good = answered(second.state, second.id, [[errorFor(text[0], 'wrod', { action: 2, replacement: 'word' })]]);
  assert.equal(spelling.errorAt(good, pos).replacement, 'word');
});

test('a result after an insertion earlier in the doc lands on the moved word', (t) => {
  const context = setup('first\n\nA wrod here\n');
  const { state, id, text } = flushed(context, t);
  const moved = state.apply(state.tr.insertText('MORE ', find(state.doc, 'first')));
  const next = answered(moved, id, [[], [errorFor(text[1], 'wrod')]]);
  assert.deepEqual(words(next), ['wrod']);
  assert.equal(decorationsOf(next)[0].from, find(next.doc, 'wrod'));
});

test('a result after an insertion earlier in the same block lands on the moved word', (t) => {
  const context = setup('A wrod here\n');
  const { state, id, text } = flushed(context, t);
  const moved = state.apply(state.tr.insertText('MORE ', find(state.doc, 'A wrod')));
  const next = answered(moved, id, [[errorFor(text[0], 'wrod')]]);
  assert.equal(decorationsOf(next)[0].from, find(next.doc, 'wrod'));
});

test('a result for a word edited meanwhile is dropped', (t) => {
  const edits = {
    inside: (s, at) => s.tr.insertText('x', at + 2),
    'at its start': (s, at) => s.tr.insertText('x', at),
    'at its end': (s, at) => s.tr.insertText('x', at + 4),
    deleted: (s, at) => s.tr.delete(at, at + 4),
    'split by Enter': (s, at) => s.tr.split(at + 2),
    'made code': (s, at) => s.tr.addMark(at, at + 4, schema.marks.code.create()),
    'turned into a code block': (s, at) => s.tr.setBlockType(at, at, schema.nodes.code_block),
  };
  for (const [name, edit] of Object.entries(edits)) {
    const context = setup('A wrod here\n');
    const { state, id, text } = flushed(context, t);
    const edited = state.apply(edit(state, find(state.doc, 'wrod')));
    assert.deepEqual(words(answered(edited, id, [[errorFor(text[0], 'wrod')]])), [], name);
  }
});

test('a result replaces the old underlines of an unchanged block and keeps other blocks', (t) => {
  const context = setup('A wrod teh\n\nOthr\n');
  const { view, tick } = mount(context, t);
  tick();
  const [text, other] = context.sent[0].blocks;
  view.dispatch(spelling.results(view.state, context.sent[0].id, [[errorFor(text, 'wrod'), errorFor(text, 'teh')], [errorFor(other, 'Othr')]]));
  assert.deepEqual(words(view.state), ['Othr', 'teh', 'wrod']);
  const at = find(view.state.doc, 'A wrod');
  view.dispatch(view.state.tr.insertText('Z', at));
  view.dispatch(view.state.tr.delete(at, at + 1));
  tick();
  assert.deepEqual(context.sent[1].blocks, [text]);
  view.dispatch(spelling.results(view.state, context.sent[1].id, [[errorFor(text, 'teh')]]));
  assert.deepEqual(words(view.state), ['Othr', 'teh']);
});

test('a late answer for an older text keeps the underlines of the newer one', (t) => {
  const context = setup('A wrod\n');
  const { view, tick } = mount(context, t);
  tick();
  view.dispatch(view.state.tr.insertText('teh ', find(view.state.doc, 'A wrod')));
  tick();
  const [scan, typed] = context.sent;
  assert.deepEqual(typed.blocks, ['teh A wrod']);
  view.dispatch(spelling.results(view.state, typed.id, [[errorFor(typed.blocks[0], 'teh')]]));
  view.dispatch(spelling.results(view.state, scan.id, [[]]));
  assert.deepEqual(words(view.state), ['teh'], 'the scan knows nothing about this word');
  assert.equal(decorationsOf(view.state)[0].from, find(view.state.doc, 'teh'));
});

test('an old answer and a new one for the same word make one underline', (t) => {
  const context = setup('A wrod\n');
  const { view, tick } = mount(context, t);
  tick();
  view.dispatch(view.state.tr.insertText('new ', find(view.state.doc, 'A wrod')));
  tick();
  const [scan, typed] = context.sent;
  view.dispatch(spelling.results(view.state, typed.id, [[errorFor(typed.blocks[0], 'wrod')]]));
  view.dispatch(spelling.results(view.state, scan.id, [[errorFor(scan.blocks[0], 'wrod')]]));
  assert.equal(decorationsOf(view.state).length, 1);
});

test('an edit removes the underline of the word it changed at once and keeps the others', (t) => {
  const context = setup('A wrod teh\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod'), errorFor(text[0], 'teh')]]);
  const edited = next.apply(next.tr.insertText('x', find(next.doc, 'wrod') + 2));
  assert.deepEqual(words(edited), ['teh']);
  const coded = next.apply(next.tr.addMark(find(next.doc, 'teh'), find(next.doc, 'teh') + 3, schema.marks.code.create()));
  assert.deepEqual(words(coded), ['wrod']);
});

test('clearWord removes all matching decorations, and an answer on its way cannot bring them back', (t) => {
  const context = setup('wrod wrod teh\n\nwrod\n');
  const { view, tick } = mount(context, t);
  tick();
  const [one, two] = context.sent[0].blocks;
  const answer = [[errorFor(one, 'wrod'), { ...errorFor(one, 'wrod'), start: 5 }, errorFor(one, 'teh')], [errorFor(two, 'wrod')]];
  const flagged = answered(view.state, context.sent[0].id, answer);
  assert.equal(decorationsOf(flagged).length, 4);
  assert.equal(decorationsOf(flagged.apply(spelling.clearWord(flagged, 'wrod', 'ar-SA'))).length, 4, 'another language');
  assert.deepEqual(words(flagged.apply(spelling.clearWord(flagged, 'wrod', 'en-US'))), ['teh']);

  const ignoredFirst = view.state.apply(spelling.clearWord(view.state, 'wrod', 'en-US'));
  assert.deepEqual(words(answered(ignoredFirst, context.sent[0].id, answer)), ['teh']);
});

test('replace after typing, then one undo restores only the word', (t) => {
  const context = setup('A wrod here\n');
  const { state, id, text } = flushed(context, t);
  const typed = state.apply(state.tr.insertText('typed ', find(state.doc, 'A wrod')));
  const next = answered(typed, id, [[errorFor(text[0], 'wrod')]]);
  const error = spelling.errorAt(next, find(next.doc, 'wrod'));
  const replaced = next.apply(spelling.replace(next, error, 'word'));
  assert.equal(replaced.doc.textContent, 'typed A word here');
  assert.equal(words(replaced).length, 0);

  let undone = replaced;
  assert.ok(undo(replaced, (tr) => { undone = replaced.apply(tr); }));
  assert.equal(undone.doc.textContent, 'typed A wrod here');
  assert.ok(undo(undone, (tr) => { undone = undone.apply(tr); }));
  assert.equal(undone.doc.textContent, 'A wrod here');
});

test('replace keeps the marks of the word, including a link that ends with it', (t) => {
  const context = setup('Go [to wrod](https://a.example) now and **teh** bold\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod'), errorFor(text[0], 'teh')]]);
  const linked = next.apply(spelling.replace(next, spelling.errorAt(next, find(next.doc, 'wrod')), 'word'));
  const bolded = linked.apply(spelling.replace(linked, spelling.errorAt(linked, find(linked.doc, 'teh')), 'the'));
  const out = serialize(bolded.doc, context.baseline);
  assert.equal(out, 'Go [to word](https://a.example) now and **the** bold\n');
  assert.equal(bolded.storedMarks, null);
});

test('deleting a repeated word also removes one space before it', (t) => {
  const context = setup('the  the end\n\nthe\n');
  const { state, id, text } = flushed(context, t);
  const start = text[0].lastIndexOf('the');
  const next = answered(state, id, [[{ start, length: 3, language: 'en-US', action: 3 }], [{ start: 0, length: 3, language: 'en-US', action: 3 }]]);
  const pos = blockPos(next.doc, 0) + 1 + start;
  const deleted = next.apply(spelling.replace(next, spelling.errorAt(next, pos), ''));
  assert.equal(deleted.doc.child(0).textContent, 'the  end');
  const alone = spelling.errorAt(deleted, find(deleted.doc, 'end') + 5);
  const emptied = deleted.apply(spelling.replace(deleted, alone, ''));
  assert.equal(emptied.doc.childCount, 2, 'the block stays');
  assert.equal(emptied.doc.child(1).textContent, '');
});

test('replace refuses a stale error and an unusable text', (t) => {
  const context = setup('A wrod\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod')]]);
  const error = spelling.errorAt(next, find(next.doc, 'wrod'));
  assert.equal(spelling.replace(next, error, 'a\nb'), null);
  assert.equal(spelling.replace(next, error, 'x'.repeat(129)), null);
  assert.equal(spelling.replace(next, error, 7), null);
  assert.equal(spelling.replace(next, null, 'word'), null);
  const moved = next.apply(next.tr.insertText('x', 1));
  assert.equal(spelling.replace(moved, error, 'word'), null);
});

test('the caret rule hides only the word the caret ends, and only with an empty selection', (t) => {
  const context = setup('A wrod teh\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod'), errorFor(text[0], 'teh')]]);
  const from = find(next.doc, 'wrod');
  const shown = (s) => context.plugin.props.decorations(s).find().map((d) => s.doc.textBetween(d.from, d.to)).sort();
  const at = (a, b = a) => next.apply(next.tr.setSelection(TextSelection.create(next.doc, a, b)));
  assert.deepEqual(shown(at(from + 4)), ['teh']);
  assert.deepEqual(shown(at(from + 2)), ['teh', 'wrod']);
  assert.deepEqual(shown(at(from)), ['teh', 'wrod']);
  assert.deepEqual(shown(at(from, from + 4)), ['teh', 'wrod']);
  assert.deepEqual(words(at(from + 4)), ['teh', 'wrod'], 'hidden, not forgotten');
  assert.equal(spelling.errorAt(at(from + 4), from).word, 'wrod');
});

test('suggestions keeps at most eight usable strings', () => {
  assert.deepEqual(spelling.suggestions(null), []);
  assert.deepEqual(spelling.suggestions('word'), []);
  assert.deepEqual(spelling.suggestions(['a lot', '', 7, null, 'x\ny', 'x'.repeat(129), '<b>ok</b>']), ['a lot', '<b>ok</b>']);
  assert.equal(spelling.suggestions(Array(20).fill('a')).length, 8);
});

test('a block turned into a list keeps its underline and its text', (t) => {
  const context = setup('A wrod\n');
  const { state, id, text } = flushed(context, t);
  const next = answered(state, id, [[errorFor(text[0], 'wrod')]]);
  let wrapped = next;
  commands.toggleBulletList(next, (tr) => { wrapped = next.apply(tr); });
  assert.equal(wrapped.doc.textContent, 'A wrod');
  assert.deepEqual(words(wrapped), ['wrod']);
});
