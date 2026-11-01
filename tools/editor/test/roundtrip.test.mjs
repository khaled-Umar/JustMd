import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorState, parse, schema, serialize } from '../src/index.js';
import { fixture, roundTrip, typeBefore } from './helpers.mjs';

test('the full fixture round-trips byte for byte', () => {
  const source = fixture('full.md');
  assert.ok(!source.includes('\r'), 'fixture must be LF-only (see .gitattributes)');
  assert.equal(roundTrip(source), source);
});

test('the fixture parses into the blocks the plan relies on', () => {
  const { doc, baseline } = parse(fixture('full.md'));
  const types = new Set();
  doc.forEach((node) => types.add(node.type.name));
  for (const type of ['heading', 'paragraph', 'code_block', 'table', 'bullet_list', 'ordered_list', 'blockquote', 'html_block', 'horizontal_rule']) {
    assert.ok(types.has(type), `no ${type} in the fixture`);
  }
  doc.forEach((node, _, i) => {
    assert.equal(node.attrs.srcIdx, i);
    assert.equal(typeof node.attrs.srcPre, 'string');
  });
  assert.ok(baseline.lines.length > 50);
  assert.equal(baseline.doc, doc);
});

// Each case isolates something that has moved lines around or lost bytes in similar tools.
const CASES = {
  empty: '',
  'one newline': '\n',
  'only blanks': '\n  \n\t\n\n',
  'no trailing newline': 'text',
  'several trailing newlines': 'text\n\n\n',
  'trailing spaces at the end': 'text   ',
  'trailing whitespace lines': 'text\n   \n\t\n',
  'leading blank lines': '\n\n# h\n',
  'leading indentation': '   text\n',
  'no gap between blocks': '# a\ntext\n# b\n```\ncode\n```\ntext\n---\n',
  'wide gaps': 'a\n\n\n\n\nb\n',
  'whitespace-only gap lines': 'a\n  \n\t\nb\n',
  'definition only': '[a]: b\n',
  'definition only, no newline': '[a]: b',
  'definition at the start': '[a]: b\n\ntext [a]\n',
  'definition touching blocks': '# h\n[a]: b\ntext [a]\n',
  'many definitions at the end': 'text\n\n[a]: b\n[c]: d\n\n[e]: f\n\n\n',
  'single-word footnote': 'see [^1]\n\n[^1]: word\n',
  'loose list then text': '- a\n\n- b\n\n\ntext\n',
  'list ending the file with blanks': '- a\n- b\n\n\n',
  'nested lists': '- a\n    - b\n\n      c\n- d\n  1. e\n',
  'mixed list markers': '- a\n* b\n+ c\n1. d\n2) e\n',
  'empty list items': '-\n- \n-   \n',
  'empty quote': '>\n',
  'unclosed fence': '```\ncode\n\n',
  'unclosed fence with blanks then eof': '~~~\ncode\n\n\n',
  'fence in a quote': '> ```\n> code\n> ```\n',
  'fence with blank lines': '```\n\n\ncode\n\n\n```\n',
  'html block to eof': '<div>\ntext\n',
  'html block then blanks': '<div>\ntext\n\n\n</div>\n',
  'html comment across blanks': '<!-- a\n\nb -->\ntext\n',
  'script block': '<script>alert(1)</script>\n',
  'table to eof without newline': '| a |\n|---|\n| 1 |',
  'table followed by text': '| a |\n|---|\n| 1 |\ntext\n\nmore\n',
  'table without pipes at edges': 'a | b\n--|--\n1 | 2\n',
  'table with short and long rows': '| a | b |\n|---|---|\n| 1 |\n| 1 | 2 | 3 |\n',
  'not a table': '| a |\n| b |\n',
  'setext across lines': 'a\nb\n===\n',
  'hr variants': '***\n\n---\n\n___\n\n* * *\n',
  'atx variants': '# a #\n##   b\n###\n',
  'tab-indented code': '\tcode\n\n\tmore\n',
  'nul character': 'a\u0000b\n',
  'unicode and rtl': '# مرحبا\n\nنص **غامق** 🙂\n',
  'non-breaking and odd spaces': 'a b c\n',
  'task markers everywhere': '- [ ]\n- [x]\n- [ ] \n- [x] a\n1. [ ] b\n> - [x] c\n',
  'break tags': 'a<br>b<BR/>c<br />d\n',
  'unmatched inline html': 'a <b> c\n\nd </i> e\n',
  'javascript links stay as text': '[x](javascript:alert(1)) ![y](javascript:alert(1)) [z](java&#9;script:alert(1))\n',
  'data image': '![d](data:image/png;base64,AAAA)\n',
  'link with parens and title': "[a](b(c)d 'e') [f](<g h> (i))\n",
  'grid table': '+---+\n| a |\n+===+\n',
  'definition list': 'Term\n: Def\n\n    Indented continuation\n',
  'alert variants': '> [!WARNING]\n> x\n\n> [!TIP] y\n',
  'deep nesting beyond the parser limit': `${'> '.repeat(40)}deep\n\nafter\n`,
};

for (const [name, source] of Object.entries(CASES)) {
  test(`byte-identical: ${name}`, () => {
    assert.equal(roundTrip(source), source);
  });
}

// Deterministic so a failure reproduces: mulberry32.
function random(seed) {
  let a = seed;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const FRAGMENTS = [
  '# heading', 'setext\n===', 'para *em* _em_ **b**', 'two\nlines', '- item', '* item', '1. one', '2) two',
  '- [ ] task', '- [x] done', '  indented', '    code', '\tcode', '```', '~~~', '```js', '> quote', '>', '---', '***',
  '| a | b |', '|---|---|', '| 1 | 2 |', '<div>', '</div>', '<!--', '-->', '<br>', '[a]: b', '[^1]: note', '[^1]: two words',
  'see [a] [^1]', ': def', '> [!NOTE]', '![i](a b.png)', '![i](<a b.png>)', '[l](u "t")', '<kbd>k</kbd>', '+---+', '\\', '&amp;',
  '   ', '', '', '', '',
];

test('byte-identical: 3000 random documents', () => {
  const next = random(20261101);
  for (let run = 0; run < 3000; run++) {
    const count = Math.floor(next() * 14);
    const lines = [];
    for (let i = 0; i < count; i++) lines.push(FRAGMENTS[Math.floor(next() * FRAGMENTS.length)]);
    const source = lines.join('\n') + (next() < 0.5 ? '\n' : '');
    assert.equal(roundTrip(source), source, `seed run ${run}: ${JSON.stringify(source)}`);
  }
});

test('input with CRLF is normalised to LF, the only exception to identity', () => {
  assert.equal(roundTrip('a\r\n\r\nb\r\n'), 'a\n\nb\n');
});

// Serializes a document the baseline knows nothing about, so nothing can be written verbatim.
const rewrite = (...blocks) => serialize(schema.topNodeType.create(null, blocks), parse('').baseline).replace(/\n$/, '');
const paragraphOf = (...inline) => schema.nodes.paragraph.create(null, inline);
const headingOf = (text) => schema.nodes.heading.create({ level: 2 }, schema.text(text));

function assertSingle(markdown, type, inline) {
  const { doc } = parse(markdown);
  assert.equal(doc.childCount, 1, JSON.stringify(markdown));
  assert.equal(doc.firstChild.type.name, type, JSON.stringify(markdown));
  assert.ok(doc.firstChild.content.eq(schema.nodes.paragraph.create(null, inline).content), JSON.stringify(markdown));
}

for (const [text, expected] of [
  ['foo #', '## foo \\#'],
  ['#', '## \\#'],
  ['a ##', '## a \\##'],
  ['a #', '## a \\#'],
]) {
  test(`a rewritten heading keeps its trailing hashes: ${text}`, () => {
    const markdown = rewrite(headingOf(text));
    assert.equal(markdown, expected);
    assertSingle(markdown, 'heading', [schema.text(text)]);
  });
}

test('a heading with hashes elsewhere is left alone', () => {
  for (const text of ['a#', '#a', 'a # b', 'c#']) {
    assert.equal(rewrite(headingOf(text)), `## ${text}`);
    assertSingle(rewrite(headingOf(text)), 'heading', [schema.text(text)]);
  }
});

test('an edit in a heading ending in an escaped hash keeps the hash', () => {
  const { doc, baseline } = parse('## foo \\#\n');
  assert.equal(doc.firstChild.textContent, 'foo #');
  const markdown = serialize(typeBefore(doc, 'foo', 'new '), baseline);
  assert.equal(markdown, '## new foo \\#\n');
});

for (const text of ['1) x', '12. y', '1.', '1)', '    code?', '\tfoo', '   three', ' x', '  1) x', '123456789) y']) {
  test(`a rewritten paragraph stays one paragraph: ${JSON.stringify(text)}`, () => {
    assertSingle(rewrite(paragraphOf(schema.text(text))), 'paragraph', [schema.text(text)]);
  });
}

test('the block start rewrite writes one escape and nothing twice', () => {
  assert.equal(rewrite(paragraphOf(schema.text('1) x'))), '1\\) x');
  assert.equal(rewrite(paragraphOf(schema.text('12. y'))), '12\\. y');
  assert.equal(rewrite(paragraphOf(schema.text('1.'))), '1\\.');
  assert.equal(rewrite(paragraphOf(schema.text('    code?'))), '&#32;   code?');
  assert.equal(rewrite(paragraphOf(schema.text('\tfoo'))), '&#9;foo');
  assert.equal(rewrite(paragraphOf(schema.text('   # no'))), '&#32;  # no');
  assert.equal(rewrite(paragraphOf(schema.text('1) *a*'))), '1\\) \\*a\\*');
});

test('text that only looks like the start of a list is not escaped', () => {
  for (const text of ['1.x', '1)x', '1234567890) too long', 'a 1) b']) {
    assert.equal(rewrite(paragraphOf(schema.text(text))), text);
  }
});

test('a first node with marks needs no rewrite, the mark opener protects it', () => {
  const strong = schema.marks.strong.create();
  const link = schema.marks.link.create({ href: 'u' });
  assert.equal(rewrite(paragraphOf(schema.text('1) x', [strong]))), '**1) x**');
  assertSingle('**1) x**', 'paragraph', [schema.text('1) x', [strong])]);
  assert.equal(rewrite(paragraphOf(schema.text('12. y', [link]))), '[12. y](u)');
  assert.equal(rewrite(paragraphOf(schema.text('1) x', [strong]), schema.text(' rest'))), '**1) x** rest');
});

test('the block start rewrite applies inside quotes and lists', () => {
  const quote = schema.nodes.blockquote.create(null, paragraphOf(schema.text('1) x')));
  assert.equal(rewrite(quote), '> 1\\) x');
  const item = schema.nodes.list_item.create(null, paragraphOf(schema.text('    x')));
  assert.equal(rewrite(schema.nodes.bullet_list.create({ tight: true }, item)), '- &#32;   x');
});

test('later lines of a paragraph keep their escapes after a first line that was rewritten', () => {
  for (const text of ['\tfoo\n- bar', '  foo\n# bar', '1) foo\n> bar', '12. foo\n+ bar', 'see [^1] x\n- bar']) {
    assertSingle(rewrite(paragraphOf(schema.text(text))), 'paragraph', [schema.text(text)]);
  }
});

test('a later line that could start a list is escaped, also after a marked first node', () => {
  const strong = schema.marks.strong.create();
  assert.equal(rewrite(paragraphOf(schema.text('a\n1) b'))), 'a\n1\\) b');
  assert.equal(rewrite(paragraphOf(schema.text('a', [strong]), schema.text('\n- b'))), '**a**\n\\- b');
});

test('editing a paragraph that came from escaped source keeps it a paragraph', () => {
  for (const [source, text, expected] of [
    ['1\\) x\n', '1) x', '1\\) x tail\n'],
    ['12\\. y\n', '12. y', '12\\. y tail\n'],
    ['&#32;   code?\n', '    code?', '&#32;   code? tail\n'],
    ['&#9;foo\n', '\tfoo', '&#9;foo tail\n'],
  ]) {
    const { doc, baseline } = parse(source);
    assert.equal(doc.firstChild.textContent, text);
    const paragraph = doc.firstChild;
    const edited = doc.copy(doc.content.replaceChild(0, paragraph.copy(paragraph.content.append(schema.nodes.paragraph.create(null, schema.text(' tail')).content))));
    assert.equal(serialize(edited, baseline), expected);
  }
});

test('definitions of removed blocks are not repeated when several blocks are lost', () => {
  const { baseline } = parse('[a]: b\n\none\n\n[c]: d\n\ntwo\n\n[a]: b\n\nthree\n');
  const kept = schema.topNodeType.create(null, paragraphOf(schema.text('fresh')));
  assert.equal(serialize(kept, baseline), 'fresh\n\n[a]: b\n\n[c]: d\n\n[a]: b\n');
});

// Edits as a user makes them: real source, a transaction, save, reopen.
function textAfterEdit(source, edit) {
  const { doc, baseline } = parse(source);
  const edited = edit(doc);
  const saved = serialize(edited, baseline);
  const reopened = parse(saved).doc;
  return { edited, saved, reopened };
}

for (const [name, source, offset] of [
  ['bold', '**first\nsecond** tail\n', 5],
  ['italic', '*first\nsecond* tail\n', 5],
  ['strike', '~~first\nsecond~~ tail\n', 5],
  ['bold, typing at the start', '**first\nsecond** tail\n', 0],
]) {
  test(`a space typed into wrapped text keeps every line: ${name}`, () => {
    const { edited, reopened } = textAfterEdit(source, (doc) => {
      let at = null;
      doc.descendants((node, pos) => {
        if (at === null && node.isText) at = pos;
        return at === null;
      });
      return EditorState.create({ doc }).tr.insertText(' ', at + offset).doc;
    });
    assert.ok(edited.textContent.includes('second'));
    assert.equal(reopened.textContent, edited.textContent);
    assert.equal(reopened.childCount, 1);
  });
}

test('wrapped bold stays one bold run in the file', () => {
  const strong = schema.marks.strong.create();
  assert.equal(rewrite(paragraphOf(schema.text('first\nsecond', [strong]))), '**first\nsecond**');
  assert.ok(parse('**first\nsecond**\n').doc.firstChild.firstChild.marks.some((mark) => mark.eq(strong)));
});

test('no line terminator in marked text loses what follows it', () => {
  const strong = schema.marks.strong.create();
  for (const text of ['a \nb', ' a\nb', 'a\n b', 'a \u2028b', ' a\u2029b', 'a\n\nb']) {
    const written = rewrite(paragraphOf(schema.text(text, [strong])));
    assert.ok(written.includes('b'), JSON.stringify(written));
  }
});

for (const [typed, written] of [
  ['- item', '\\- item'],
  ['-', '\\-'],
  ['---', '\\---'],
  ['# title', '\\# title'],
  ['1) x', '1\\) x'],
  ['1.', '1\\.'],
  ['> q', '\\> q'],
  ['+ a', '\\+ a'],
  ['===', '\\==='],
  ['=', '\\='],
  ['    code', '&#32;   code'],
]) {
  test(`text after a hard break stays in the paragraph: ${typed}`, () => {
    const inline = [schema.text('first'), schema.nodes.hard_break.create(), schema.text(typed)];
    const markdown = rewrite(paragraphOf(...inline));
    assert.equal(markdown, `first\\\n${written}`);
    assertSingle(markdown, 'paragraph', inline);
  });
}

test('a hard break inside a quote or a list keeps the marker escaped', () => {
  const inline = [schema.text('first'), schema.nodes.hard_break.create(), schema.text('- item')];
  const quote = schema.nodes.blockquote.create(null, paragraphOf(...inline));
  assert.equal(rewrite(quote), '> first\\\n> \\- item');
  const item = schema.nodes.list_item.create(null, paragraphOf(...inline));
  assert.equal(rewrite(schema.nodes.bullet_list.create({ tight: true }, item)), '- first\\\n  \\- item');
});

for (const text of ['+', '+\tx', 'a\n+', 'a\n+\tx']) {
  test(`a plus sign stays text: ${JSON.stringify(text)}`, () => {
    assertSingle(rewrite(paragraphOf(schema.text(text))), 'paragraph', [schema.text(text)]);
  });
}

for (const mark of ['strong', 'em', 'strike']) {
  for (const spaces of [' ', '  ', '   ', '    ', '\t']) {
    test(`${mark} text keeps its leading whitespace at the start of a paragraph: ${JSON.stringify(spaces)}`, () => {
      const node = schema.text(`${spaces}x`, [schema.marks[mark].create()]);
      const { doc } = parse(rewrite(paragraphOf(node)));
      assert.equal(doc.childCount, 1);
      assert.equal(doc.firstChild.type.name, 'paragraph');
      assert.equal(doc.firstChild.textContent, `${spaces}x`);
      assert.ok(doc.firstChild.lastChild.marks.length === 1, 'the mark survives on the text');
      assert.equal(doc.firstChild.firstChild.marks.length, 0, 'the whitespace is outside the delimiters');
    });
  }
}

test('leading whitespace of a code span at the start of a paragraph does not make a code block', () => {
  const { doc } = parse(rewrite(paragraphOf(schema.text('    x', [schema.marks.code.create()]))));
  assert.equal(doc.firstChild.type.name, 'paragraph');
  assert.equal(doc.firstChild.textContent, '    x');
});

test('spaces typed at the end of a wrapped line are text, not a hard break', () => {
  for (const spaces of [' ', '  ', '   ', '\t']) {
    const text = `first${spaces}\nsecond`;
    assertSingle(rewrite(paragraphOf(schema.text(text))), 'paragraph', [schema.text(text)]);
  }
  assert.equal(rewrite(paragraphOf(schema.text('first  \nsecond'))), 'first &#32;\nsecond');
});

test('spaces at the end of a block are left alone, they mean nothing there', () => {
  assert.equal(rewrite(paragraphOf(schema.text('a  '))), 'a  ');
});

test('delimiters next to punctuation still parse as marks', () => {
  const strong = schema.marks.strong.create();
  const em = schema.marks.em.create();
  const closing = rewrite(paragraphOf(schema.text('(a)', [strong]), schema.text('b')));
  assert.equal(closing, '**(a)**<!-- -->b');
  const opening = rewrite(paragraphOf(schema.text('b'), schema.text('(a)', [strong])));
  assert.equal(opening, 'b<!-- -->**(a)**');
  assertSingle(closing, 'paragraph', [schema.text('(a)', [strong]), schema.text('b')]);
  assertSingle(opening, 'paragraph', [schema.text('b'), schema.text('(a)', [strong])]);
  assert.equal(rewrite(paragraphOf(schema.text('a', [strong]), schema.text('b', [em]))), '**a**_b_');
  assert.equal(rewrite(paragraphOf(schema.text('plain '), schema.text('a', [strong]), schema.text(' and '), schema.text('b', [em]))), 'plain **a** and *b*');
  assert.equal(rewrite(paragraphOf(schema.text('a', [em, strong]))), '***a***');
});

for (const [name, mark, delimiter] of [['strong', 'strong', '**'], ['em', 'em', '*'], ['strike', 'strike', '~~']]) {
  test(`a separator makes ${name} flank in Latin and Arabic text`, () => {
    for (const [word, letter] of [['(a)', 'b'], ['(كلمة)', 'و']]) {
      const marks = [schema.marks[mark].create()];
      const inline = [schema.text(word, marks), schema.text(letter)];
      const saved = rewrite(paragraphOf(...inline));
      assert.equal(saved, `${delimiter}${word}${delimiter}<!-- -->${letter}`);
      assertSingle(saved, 'paragraph', inline);
    }
  });
}

test('parse drops only the exact separator, and an edited block does not keep a stale one', () => {
  const { doc, baseline } = parse('**(a)**<!-- -->b\n\nc<!-- x -->d\n');
  assert.equal(doc.firstChild.childCount, 2);
  assert.equal(doc.firstChild.textContent, '(a)b');
  assert.equal(doc.lastChild.child(1).type.name, 'html_inline');
  assert.equal(serialize(doc, baseline), '**(a)**<!-- -->b\n\nc<!-- x -->d\n');
  const edited = typeBefore(doc, '(a)', 'x ');
  assert.match(serialize(edited, baseline), /^\*\*x \(a\)\*\*<!-- -->b\n/);
});

test('whitespace entities stay outside delimiters, which Markdig reads as the decoded space', () => {
  const strong = schema.marks.strong.create();
  assert.equal(rewrite(paragraphOf(schema.text('    x', [strong]))), '&#32;   **x**');
  assert.equal(rewrite(paragraphOf(schema.text('a \n', [strong]), schema.text('b'))), '**a**&#32;\nb');
  assert.equal(rewrite(paragraphOf(schema.text('a \nb', [strong]))), '**a&#32;\nb**');
});

test('Markdig forms: a backtick before a code span and a line break at the end of a link', () => {
  const code = schema.text('b', [schema.marks.code.create()]);
  assert.equal(rewrite(paragraphOf(schema.text('a`'), code)), 'a\\`<!-- -->`b`');
  const link = schema.marks.link.create({ href: 'u' });
  assert.equal(rewrite(paragraphOf(schema.text('x\n', [link]), schema.text('y'))), '[x](u)\ny');
});

test('runs of underscores, spaces at both ends of code and a literal backslash before a link keep their text', () => {
  assert.equal(rewrite(paragraphOf(schema.text('___a___ __b__ c_d'))), '\\_\\_\\_a\\_\\_\\_ \\_\\_b\\_\\_ c_d');
  const code = schema.text(' x ', [schema.marks.code.create()]);
  assert.equal(rewrite(paragraphOf(schema.text('a '), code)), 'a `  x  `');
  assertSingle(rewrite(paragraphOf(schema.text('a '), code)), 'paragraph', [schema.text('a '), code]);
  const link = schema.marks.link.create({ href: 'u' });
  const inline = [schema.text('\\!'), schema.text('x', [link])];
  assertSingle(rewrite(paragraphOf(...inline)), 'paragraph', inline);
});

const FUZZ_TEXT = [
  'a', 'b', 'word', ' ', '  ', '    ', '\t', '\n', '\n', '- ', '-', '+', '+ ', '* ', '> ', '>', '# ', '#', '##', '1) ', '1)', '2. ', '1.',
  '=', '===', '---', '***', '___', '~~~', '```', '`', '|', ':', ':---', '|---|', '_', '*', '~', '\\', '[', ']', '(', ')', '!', '<', '&',
  '&amp;', '<div>', '.', '[x] ', '[a]: b',
];
const { strong: fuzzStrong, em: fuzzEm, strike: fuzzStrike, code: fuzzCode, link: fuzzLink } = schema.marks;
const FUZZ_MARKS = [
  [], [], [],
  [fuzzStrong.create()], [fuzzEm.create()], [fuzzStrike.create()], [fuzzCode.create()],
  [fuzzLink.create({ href: 'u' })], [fuzzEm.create(), fuzzStrong.create()],
];

function fuzzBlock(next) {
  const pick = (list) => list[Math.floor(next() * list.length)];
  const inline = [];
  const count = 1 + Math.floor(next() * 5);
  for (let i = 0; i < count; i++) {
    if (next() < 0.15) {
      inline.push(schema.nodes.hard_break.create());
      continue;
    }
    let text = '';
    const length = 1 + Math.floor(next() * 4);
    for (let j = 0; j < length; j++) text += pick(FUZZ_TEXT);
    inline.push(schema.text(text, pick(FUZZ_MARKS)));
  }
  if (next() < 0.25) return schema.nodes.heading.create({ level: 1 + Math.floor(next() * 6) }, inline);
  const paragraph = schema.nodes.paragraph.create(null, inline);
  const wrap = next();
  if (wrap < 0.1) return schema.nodes.blockquote.create(null, paragraph);
  if (wrap < 0.2) return schema.nodes.bullet_list.create(null, schema.nodes.list_item.create(null, paragraph));
  return paragraph;
}

// What a block means: its type and its text. Allowed differences, each harmless and deliberate:
// whitespace and hard breaks at the very end of a block, which Markdown cannot hold; a line break in
// a heading, which is written as a space; a paragraph with nothing visible in it, which is not written.
function fuzzShape(node, out = []) {
  if (!node.isTextblock) {
    node.forEach((child) => fuzzShape(child, out));
    return out;
  }
  let text = '';
  node.forEach((child) => {
    text += child.isText ? child.text : '\u23CE';
  });
  if (node.type.name === 'heading') text = text.replace(/\n/g, ' ');
  text = text.replace(/[\s\u23CE]+$/, '');
  if (node.type.name === 'paragraph' && !text) return out;
  out.push(`${node.type.name}${node.attrs.level ?? ''}: ${text}`);
  return out;
}

test('fuzz: 4000 documents of hostile paragraphs and headings keep their text and structure', () => {
  const next = random(20261102);
  for (let run = 0; run < 4000; run++) {
    const blocks = [fuzzBlock(next)];
    if (next() < 0.5) blocks.push(fuzzBlock(next));
    const doc = schema.topNodeType.create(null, blocks);
    const saved = serialize(doc, parse('').baseline);
    assert.deepEqual(fuzzShape(parse(saved).doc), fuzzShape(doc), `run ${run}: ${JSON.stringify(saved)}`);
  }
});

test('serialize tolerates a document with blocks the baseline does not know', () => {
  const { baseline } = parse('a\n\nb\n');
  const other = parse('c\n\nd\n\ne\n\nf\n').doc;
  assert.doesNotThrow(() => serialize(other, baseline));
});
