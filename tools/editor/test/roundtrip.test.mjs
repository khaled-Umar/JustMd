import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parse, serialize } from '../src/index.js';
import { fixture, roundTrip } from './helpers.mjs';

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

test('serialize tolerates a document with blocks the baseline does not know', () => {
  const { baseline } = parse('a\n\nb\n');
  const other = parse('c\n\nd\n\ne\n\nf\n').doc;
  assert.doesNotThrow(() => serialize(other, baseline));
});
