// Parses one or more .ico files and lists every frame. Exits non-zero if the
// app icon is missing a required size or any frame is malformed.
//
//   node verify-ico.mjs ../../assets/icon/JustMd.ico ../../docs/favicon.ico

import { readFile } from 'node:fs/promises';
import { basename } from 'node:path';

const REQUIRED = {
  'JustMd.ico': [16, 20, 24, 32, 40, 48, 64, 128, 256],
  'favicon.ico': [16, 32, 48],
};
const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

let failed = false;
const fail = (msg) => { console.error(`  FAIL ${msg}`); failed = true; };

for (const file of process.argv.slice(2)) {
  const buf = await readFile(file);
  console.log(`${file} (${buf.length} bytes)`);
  if (buf.readUInt16LE(0) !== 0 || buf.readUInt16LE(2) !== 1) { fail('not an ICO header'); continue; }
  const count = buf.readUInt16LE(4);
  const sizes = [];
  for (let i = 0; i < count; i++) {
    const e = 6 + i * 16;
    const w = buf.readUInt8(e) || 256;
    const h = buf.readUInt8(e + 1) || 256;
    const bpp = buf.readUInt16LE(e + 6);
    const len = buf.readUInt32LE(e + 8);
    const off = buf.readUInt32LE(e + 12);
    if (off + len > buf.length) { fail(`frame ${i} overruns file`); continue; }
    const img = buf.subarray(off, off + len);
    let kind;
    if (img.subarray(0, 8).equals(PNG_SIG)) {
      const pw = img.readUInt32BE(16), ph = img.readUInt32BE(20);
      kind = `PNG ${pw}x${ph}`;
      if (pw !== w || ph !== h) fail(`frame ${i}: PNG is ${pw}x${ph}, directory says ${w}x${h}`);
    } else {
      const bw = img.readInt32LE(4), bh = img.readInt32LE(8), bits = img.readUInt16LE(14);
      kind = `DIB ${bw}x${bh / 2} ${bits}bpp`;
      if (bw !== w || bh !== h * 2) fail(`frame ${i}: DIB is ${bw}x${bh / 2}, directory says ${w}x${h}`);
    }
    console.log(`  ${String(w).padStart(3)}x${String(h).padEnd(3)} ${bpp}bpp  ${String(len).padStart(6)} bytes  ${kind}`);
    sizes.push(w);
  }
  const required = REQUIRED[basename(file)];
  if (required) {
    const missing = required.filter((s) => !sizes.includes(s));
    if (missing.length) fail(`missing sizes: ${missing.join(', ')}`);
    else console.log(`  OK: all ${required.length} required sizes present`);
  }
}
process.exit(failed ? 1 : 0);
