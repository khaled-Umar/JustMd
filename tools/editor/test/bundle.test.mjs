import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { fixture } from './helpers.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const tool = resolve(here, '..');
const vendor = resolve(tool, '..', '..', 'src', 'JustMd', 'web', 'vendor');
const bundle = readFileSync(join(vendor, 'editor.bundle.js'), 'utf8');

test('the bundle uses neither eval nor new Function (the page CSP forbids both)', () => {
  assert.ok(!/\beval\s*\(/.test(bundle), 'eval(');
  assert.ok(!/\bnew\s+Function\b/.test(bundle), 'new Function');
  assert.ok(!/[^.\w]Function\s*\(/.test(bundle), 'Function(');
  assert.ok(!/\bsetTimeout\s*\(\s*["'`]/.test(bundle), 'setTimeout with a string');
});

test('the banner lists every bundled package with its licence', () => {
  const banner = bundle.slice(0, bundle.indexOf('*/'));
  for (const name of [
    'markdown-it', 'prosemirror-model', 'prosemirror-state', 'prosemirror-view', 'prosemirror-transform', 'prosemirror-markdown',
    'prosemirror-tables', 'prosemirror-commands', 'prosemirror-history', 'prosemirror-keymap', 'prosemirror-schema-list',
    'prosemirror-gapcursor', 'prosemirror-dropcursor', 'orderedmap', 'rope-sequence', 'w3c-keyname', 'linkify-it', 'mdurl', 'uc.micro', 'punycode.js',
  ]) {
    assert.match(banner, new RegExp(`\\b${name.replace('.', '\\.')} [\\d.]+ \\(`), name);
  }
  assert.match(banner, /\bentities [\d.]+ \(BSD-2-Clause\)/);
});

test('the bundle contains a single copy of markdown-it and prosemirror-model', () => {
  const lock = JSON.parse(readFileSync(join(tool, 'package-lock.json'), 'utf8'));
  const copies = (name) => Object.keys(lock.packages).filter((path) => path.endsWith(`node_modules/${name}`));
  assert.equal(copies('markdown-it').length, 1);
  assert.equal(copies('prosemirror-model').length, 1);
});

test('the bundle exposes the frozen contract as a global and round-trips', () => {
  const sandbox = {};
  vm.runInNewContext(bundle, sandbox);
  const api = sandbox.JustMdEditor;
  assert.ok(api, 'var JustMdEditor is defined at script scope');
  for (const name of ['parse', 'serialize', 'schema', 'EditorState', 'EditorView', 'Plugin', 'PluginKey', 'TextSelection', 'keymap', 'history', 'undo', 'redo', 'basePlugins', 'commands', 'queries']) {
    assert.ok(api[name], name);
  }
  for (const name of [
    'setParagraph', 'setHeading', 'toggleBold', 'toggleItalic', 'toggleStrike', 'toggleCode', 'toggleBulletList', 'toggleOrderedList', 'toggleTaskList',
    'toggleBlockquote', 'setCodeBlock', 'insertTable', 'insertHorizontalRule', 'setLink', 'removeLink', 'insertImage', 'updateImage', 'indent', 'outdent', 'undo', 'redo',
  ]) {
    assert.equal(typeof api.commands[name], 'function', name);
  }
  for (const name of ['addRowBefore', 'addRowAfter', 'addColumnBefore', 'addColumnAfter', 'deleteRow', 'deleteColumn', 'deleteTable']) {
    assert.equal(typeof api.commands.table[name], 'function', name);
  }
  for (const name of ['activeMarks', 'blockType', 'linkAt', 'imageAt', 'inTable']) {
    assert.equal(typeof api.queries[name], 'function', name);
  }
  const source = fixture('full.md');
  const { doc, baseline } = api.parse(source);
  assert.equal(api.serialize(doc, baseline), source);
});

test('the stylesheet carries the three upstream stylesheets', () => {
  const css = readFileSync(join(vendor, 'editor.css'), 'utf8');
  for (const selector of ['.ProseMirror-hideselection', '.tableWrapper', '.ProseMirror-gapcursor']) assert.ok(css.includes(selector), selector);
});

test('npm run build -- --check passes', () => {
  const result = spawnSync(process.execPath, ['build.mjs', '--check'], { cwd: tool, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr + result.stdout);
});
