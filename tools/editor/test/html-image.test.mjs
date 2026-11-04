import assert from 'node:assert/strict';
import { test } from 'node:test';
import { MAX_SIZE, imageTag, isValidSize, parseImageTag, toSize } from '../src/html-image.js';

test('parseImageTag accepts a lone tag in any quoting and case', () => {
  assert.deepEqual(parseImageTag(`<IMG SRC='a.png' Width=300 />`), { src: 'a.png', alt: null, title: null, width: 300, height: null });
  assert.deepEqual(parseImageTag('  <img src="a.png">\n'), { src: 'a.png', alt: null, title: null, width: null, height: null });
  assert.deepEqual(
    parseImageTag(`<img\n  height = "20" title='T "q"' alt="a > b" src=img/a.png width="300"/>`),
    { src: 'img/a.png', alt: 'a > b', title: 'T "q"', width: 300, height: 20 },
  );
  assert.equal(parseImageTag(`<img src="C:\\pics\\a.png">`).src, 'C:\\pics\\a.png');
});

test('parseImageTag decodes entities in values', () => {
  assert.equal(parseImageTag('<img src="a&amp;b.png">').src, 'a&b.png');
  assert.equal(parseImageTag('<img src="a&b.png">').src, 'a&b.png');
  assert.equal(parseImageTag(`<img src="a.png" alt="&lt;&gt;&quot;&#39;&apos;&#65;&#x42;&#X43;">`).alt, `<>"''ABC`);
  assert.equal(parseImageTag('<img src="a.png" alt="&amp;lt;">').alt, '&lt;');
  assert.equal(parseImageTag('<img src="a.png" alt="&#0;&#xD800;&#x110000;">').alt, '\uFFFD\uFFFD\uFFFD');
  assert.equal(parseImageTag('<img src="a.png" width="&#51;00">').width, 300);
});

test('parseImageTag rejects everything else', () => {
  const rejected = [
    '',
    'text',
    '<img>',
    '<img alt="a">',
    '<imgx src="a.png">',
    '<img src="">',
    '<img src="javascript:alert(1)">',
    '<img src="JaVaScRiPt:alert(1)">',
    '<img src="java&#115;cript:alert(1)">',
    '<img src="java&#115cript:alert(1)">',
    '<img src="javascript&colon;alert(1)">',
    '<img src="data:image/png;base64,AAAA">',
    '<img src="a.png" alt="caf&eacute;">',
    '<img src="a.png" class="c">',
    '<img src="a.png" style="width:1px">',
    '<img src="a.png" onerror="x()">',
    '<img src="a.png" data-x="y">',
    '<img src="a.png" width="50%">',
    '<img src="a.png" width="0">',
    '<img src="a.png" width="-1">',
    '<img src="a.png" width="1.5">',
    '<img src="a.png" width="">',
    '<img src="a.png" width="99999">',
    '<img src="a.png" height="abc">',
    '<img src="a.png" width="1" width="2">',
    '<img src="a.png" SRC="b.png">',
    '<img src="a.png" alt>',
    '<img src="a.png" junk',
    '<img src="a.png"width="1">',
    '<img src="a.png"> trailing',
    'leading <img src="a.png">',
    '<img src="a.png"><img src="b.png">',
    '<img src="a.png"></img>',
  ];
  for (const html of rejected) assert.equal(parseImageTag(html), null, html);
});

test('sizes are integers from 1 to the maximum', () => {
  assert.equal(MAX_SIZE, 10000);
  for (const good of [null, 1, 300, 10000]) assert.equal(isValidSize(good), true, String(good));
  for (const bad of [0, -1, 1.5, 10001, '300', NaN, undefined, Infinity]) assert.equal(isValidSize(bad), false, String(bad));
  assert.equal(toSize('300'), 300);
  assert.equal(toSize('0300'), 300);
  for (const bad of ['0', '-1', '1.5', '10001', 'abc', '', ' 3', null, undefined]) assert.equal(toSize(bad), null, String(bad));
});

test('imageTag escapes values and leaves out what is not set', () => {
  assert.equal(imageTag({ src: 'x.png', alt: null, title: null, width: null, height: null }), '<img src="x.png">');
  assert.equal(imageTag({ src: 'x.png', alt: '', title: '', width: 3, height: null }), '<img src="x.png" width="3">');
  assert.equal(
    imageTag({ src: 'a&b "c" <d>.png', alt: 'one\ntwo', title: 'T"', width: 3, height: 4 }),
    '<img src="a&amp;b &quot;c&quot; &lt;d&gt;.png" alt="one two" title="T&quot;" width="3" height="4">',
  );
  assert.equal(imageTag({ src: 'a|b.png', width: 3 }), '<img src="a&#124;b.png" width="3">');
});

test('imageTag and parseImageTag are inverses', () => {
  const attrs = { src: 'a&b (1)|.png', alt: `say "hi" <now> & 'later'`, title: '&amp; |', width: 300, height: 20 };
  assert.deepEqual(parseImageTag(imageTag(attrs)), attrs);
});
