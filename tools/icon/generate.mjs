// Regenerates every JustMd brand asset from the geometry defined in this file.
//
//   cd tools/icon && npm install && npm run build
//
// Outputs (paths relative to the repo root):
//   assets/logo.svg                    full app mark
//   assets/logo-wordmark.svg           mark + "JustMd", for light backgrounds
//   assets/logo-wordmark-dark.svg      mark + "JustMd", for dark backgrounds
//   assets/icon/JustMd.ico       16, 20, 24, 32, 40, 48, 64, 128, 256
//   assets/icon/icon-256.png, icon-512.png
//   docs/favicon.svg, docs/favicon.ico (16, 32, 48)
//
// `npm run preview` (or `--preview <dir>`) also writes every icon size as PNG plus an 8x
// enlarged contact sheet to tools/icon/preview/ (git-ignored) for eyeballing the small sizes.
// `npm run verify` parses the generated .ico files and checks that every size is present.

import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import sharp from 'sharp';
import opentype from 'opentype.js';
import { encodeIco } from './ico.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..', '..');
const require = createRequire(import.meta.url);

// ---------------------------------------------------------------------------
// Palette
// ---------------------------------------------------------------------------
const C = {
  tileTop: '#3B82F6',
  tileBottom: '#1D4ED8',
  accent: '#2563EB',
  accentDark: '#60A5FA',
  sheet: '#FFFFFF',
  fold: '#BFDBFE',
  line: '#C3D3F0',
  inkLight: '#0E1726',
  inkDark: '#E6ECF5',
};

const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const FAVICON_SIZES = [16, 32, 48];

// ---------------------------------------------------------------------------
// Full mark (64 px and up): a blue tile holding a single page with a folded
// corner; the page shows a slanted Markdown heading "#" over three text lines.
// Designed on a 256 grid.
// ---------------------------------------------------------------------------
function fullMarkBody(id = 'jmv') {
  return `
  <defs>
    <linearGradient id="${id}-tile" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="${C.tileTop}"/>
      <stop offset="1" stop-color="${C.tileBottom}"/>
    </linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="56" fill="url(#${id}-tile)"/>
  <path d="M82 40H146L194 88V200Q194 216 178 216H82Q66 216 66 200V56Q66 40 82 40Z" fill="${C.sheet}"/>
  <path d="M146 40V76Q146 88 158 88H194Z" fill="${C.fold}"/>
  <g stroke="${C.accent}" stroke-width="13" stroke-linecap="round" fill="none">
    <path d="M106 66L98 126M131 66L123 126"/>
    <path d="M84 84H144M82 108H142"/>
  </g>
  <g stroke="${C.line}" stroke-width="11" stroke-linecap="round" fill="none">
    <path d="M86 152H174M86 174H166M86 196H136"/>
  </g>`;
}

function fullMarkSvg() {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="JustMd">${fullMarkBody()}
</svg>
`;
}

// ---------------------------------------------------------------------------
// Hand-hinted marks for 16-48 px. Scaling the full mark down turns the slanted
// "#" into mush, so each size gets its own whole-pixel geometry: an upright
// "#" (1 px strokes up to 20 px, then 2-3 px) and fewer or no text lines.
// All rects are [x0, y0, x1, y1] in device pixels, end-exclusive.
// ---------------------------------------------------------------------------
const HINTED = {
  16: { inset: 0, r: 3.5, sheet: [3, 2, 13, 14], fold: 3, sr: 1,
    hash: [[6, 5, 7, 11], [9, 5, 10, 11], [5, 6, 11, 7], [5, 9, 11, 10]], lines: [] },
  20: { inset: 0, r: 4, sheet: [4, 2, 16, 18], fold: 4, sr: 1,
    hash: [[8, 5, 9, 11], [11, 5, 12, 11], [7, 6, 13, 7], [7, 9, 13, 10]],
    lines: [[7, 13, 14, 14], [7, 15, 11, 16]] },
  24: { inset: 0, r: 5, sheet: [5, 2, 19, 22], fold: 5, sr: 1.5,
    hash: [[9, 7, 11, 17], [13, 7, 15, 17], [7, 9, 17, 11], [7, 13, 17, 15]],
    lines: [[7, 19, 17, 20]] },
  32: { inset: 1, r: 6.5, sheet: [8, 3, 24, 29], fold: 5, sr: 1.5,
    hash: [[12, 8, 14, 19], [17, 8, 19, 19], [10, 10, 21, 12], [10, 15, 21, 17]],
    lines: [[10, 21, 21, 23], [10, 25, 17, 27]] },
  40: { inset: 1, r: 8, sheet: [10, 4, 30, 36], fold: 7, sr: 2,
    hash: [[16, 10, 18, 21], [21, 10, 23, 21], [14, 12, 25, 14], [14, 17, 25, 19]],
    lines: [[14, 24, 26, 26], [14, 28, 25, 30], [14, 32, 21, 34]] },
  48: { inset: 1, r: 10, sheet: [12, 5, 36, 43], fold: 8, sr: 2.5,
    hash: [[19, 12, 22, 27], [25, 12, 28, 27], [16, 15, 31, 18], [16, 21, 31, 24]],
    lines: [[16, 30, 32, 32], [16, 34, 30, 36], [16, 38, 25, 40]] },
};

const rect = ([x0, y0, x1, y1], fill, rx = 0) =>
  `<rect x="${x0}" y="${y0}" width="${x1 - x0}" height="${y1 - y0}"${rx ? ` rx="${rx}"` : ''} fill="${fill}"/>`;

function sheetWithFold([x0, y0, x1, y1], f, r) {
  const sheet = `<path d="M${x0 + r} ${y0}H${x1 - f}L${x1} ${y0 + f}V${y1 - r}Q${x1} ${y1} ${x1 - r} ${y1}H${x0 + r}Q${x0} ${y1} ${x0} ${y1 - r}V${y0 + r}Q${x0} ${y0} ${x0 + r} ${y0}Z" fill="${C.sheet}"/>`;
  const flap = `<path d="M${x1 - f} ${y0}V${y0 + f}H${x1}Z" fill="${C.fold}"/>`;
  return sheet + flap;
}

function hintedMarkSvg(size, { standalone = true } = {}) {
  const h = HINTED[size];
  const s = size - h.inset * 2;
  const lineRadius = size >= 32 ? 1 : 0;
  const parts = [
    `<defs><linearGradient id="h${size}-tile" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${C.tileTop}"/><stop offset="1" stop-color="${C.tileBottom}"/></linearGradient></defs>`,
    `<rect x="${h.inset}" y="${h.inset}" width="${s}" height="${s}" rx="${h.r}" fill="url(#h${size}-tile)"/>`,
    sheetWithFold(h.sheet, h.fold, h.sr),
    ...h.hash.map((r) => rect(r, C.accent)),
    ...h.lines.map((r) => rect(r, C.line, lineRadius)),
  ];
  const attrs = standalone ? ` width="${size}" height="${size}"` : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}"${attrs}>${parts.join('')}</svg>`;
}

function svgForSize(size) {
  if (HINTED[size]) return hintedMarkSvg(size);
  return fullMarkSvg();
}

// ---------------------------------------------------------------------------
// Wordmark: mark + "JustMd" set in Plus Jakarta Sans Bold (SIL OFL 1.1),
// converted to outlines so the SVG renders identically everywhere.
// "Md" takes the accent colour.
// ---------------------------------------------------------------------------
async function wordmarkSvg(variant) {
  const fontFile = require.resolve('@fontsource/plus-jakarta-sans/files/plus-jakarta-sans-latin-700-normal.woff');
  const buf = await readFile(fontFile);
  const font = opentype.parse(buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength));

  const ink = variant === 'dark' ? C.inkDark : C.inkLight;
  const accent = variant === 'dark' ? C.accentDark : C.accent;
  const text = 'JustMd';
  const markSize = 64;
  const capHeight = 30; // px
  const fontSize = (capHeight / font.tables.os2.sCapHeight) * font.unitsPerEm;
  const baseline = (markSize + capHeight) / 2;
  const startX = markSize + 16;

  // Simple left-to-right layout with pair kerning; no shaping is needed for
  // plain Latin text (and opentype.js's shaper chokes on this font's GSUB).
  const runs = { ink: [], accent: [] };
  const scale = fontSize / font.unitsPerEm;
  const glyphs = [...text].map((ch) => font.charToGlyph(ch));
  let x = startX;
  glyphs.forEach((glyph, i) => {
    const d = glyph.getPath(x, baseline, fontSize).toPathData(2);
    (i === 4 || i === 5 ? runs.accent : runs.ink).push(d);
    x += glyph.advanceWidth * scale;
    if (i < glyphs.length - 1) x += font.getKerningValue(glyph, glyphs[i + 1]) * scale;
  });
  const width = Math.ceil(x + 2);
  const mark = `<svg x="0" y="0" width="${markSize}" height="${markSize}" viewBox="0 0 256 256">${fullMarkBody(`wm-${variant}`)}</svg>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${markSize}" width="${width * 2}" height="${markSize * 2}" role="img" aria-label="JustMd">
  ${mark}
  <path fill="${ink}" d="${runs.ink.join('')}"/>
  <path fill="${accent}" d="${runs.accent.join('')}"/>
</svg>
`;
}

// ---------------------------------------------------------------------------
async function renderPng(svg, size) {
  return sharp(Buffer.from(svg), { density: 72 * (size / svgIntrinsicSize(svg)) })
    .resize(size, size)
    .png({ compressionLevel: 9 })
    .toBuffer();
}

function svgIntrinsicSize(svg) {
  const m = /viewBox="0 0 (\d+(?:\.\d+)?) /.exec(svg);
  return m ? Number(m[1]) : 256;
}

async function rgba(png) {
  const { data, info } = await sharp(png).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  return { data, width: info.width, height: info.height };
}

async function out(rel, content) {
  const p = join(root, rel);
  await mkdir(dirname(p), { recursive: true });
  await writeFile(p, content);
  console.log(`  wrote ${rel}`);
}

async function main() {
  const previewIdx = process.argv.indexOf('--preview');
  const previewDir = previewIdx > -1 ? resolve(here, process.argv[previewIdx + 1] ?? 'preview') : null;

  console.log('JustMd brand assets');
  await out('assets/logo.svg', fullMarkSvg());
  await out('assets/logo-wordmark.svg', await wordmarkSvg('light'));
  await out('assets/logo-wordmark-dark.svg', await wordmarkSvg('dark'));
  await out('docs/favicon.svg', hintedMarkSvg(16, { standalone: false }) + '\n');

  const frames = new Map();
  for (const size of new Set([...ICO_SIZES, ...FAVICON_SIZES])) {
    frames.set(size, await renderPng(svgForSize(size), size));
  }

  const icoFrames = async (sizes) => Promise.all(sizes.map(async (s) => ({ size: s, png: frames.get(s), ...(await rgba(frames.get(s))) })));
  await out('assets/icon/JustMd.ico', encodeIco(await icoFrames(ICO_SIZES)));
  await out('docs/favicon.ico', encodeIco(await icoFrames(FAVICON_SIZES)));
  await out('assets/icon/icon-256.png', frames.get(256));
  await out('assets/icon/icon-512.png', await renderPng(fullMarkSvg(), 512));

  if (previewDir) await writePreview(previewDir, frames);
}

// Writes each frame plus an 8x nearest-neighbour contact sheet on light and dark backgrounds.
async function writePreview(dir, frames) {
  await mkdir(dir, { recursive: true });
  const scale = 8;
  const sizes = [...frames.keys()].filter((s) => s <= 64).sort((a, b) => a - b);
  const pad = 16;
  const width = sizes.reduce((w, s) => w + s * scale + pad, pad);
  const rowH = 64 * scale + pad * 2;
  const composites = [];
  let x = pad;
  for (const s of sizes) {
    await writeFile(join(dir, `icon-${s}.png`), frames.get(s));
    const big = await sharp(frames.get(s)).resize(s * scale, s * scale, { kernel: 'nearest' }).png().toBuffer();
    composites.push({ input: big, left: x, top: pad }, { input: big, left: x, top: rowH + pad });
    x += s * scale + pad;
  }
  const bg = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${rowH * 2}"><rect width="100%" height="${rowH}" fill="#F6F8FC"/><rect y="${rowH}" width="100%" height="${rowH}" fill="#202020"/></svg>`);
  await sharp(bg).composite(composites).png().toFile(join(dir, 'contact-sheet.png'));

  // Actual-size strip, as the icons appear in Explorer / the taskbar.
  const strip = [];
  let sx = 8;
  for (const s of [...frames.keys()].sort((a, b) => a - b)) {
    strip.push({ input: frames.get(s), left: sx, top: 8 }, { input: frames.get(s), left: sx, top: 8 + 256 + 16 });
    sx += s + 12;
  }
  const stripBg = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${sx}" height="${(256 + 16) * 2}"><rect width="100%" height="272" fill="#FFFFFF"/><rect y="272" width="100%" height="272" fill="#1C1C1C"/></svg>`);
  await sharp(stripBg).composite(strip).png().toFile(join(dir, 'actual-size.png'));
  console.log(`  preview -> ${dir}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
