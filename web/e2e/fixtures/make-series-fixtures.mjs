// Synthetic ComicInfo fixture library for the series-info e2e spec (1.24.0).
// Dependency-free (node:zlib has crc32 + deflate). Every name and value is invented.
//
//   node web/e2e/fixtures/make-series-fixtures.mjs <outDir>
//
// Layout written under <outDir>:
//   Synthetic Series/Synthetic Saga v01..v03.cbz   ComicInfo "Synthetic Saga" #1-3 (one series)
//   Synthetic Anthology/Issue A.cbz, Issue B.cbz   two different series (a "mixed" folder)
//   Plain Folder/No Info 01.cbz                    no ComicInfo.xml
// Register <outDir> as a library, scan, and let analysis run; the spec does that itself
// when E2E_SERIES_FIXTURE_ROOT names the path as the SERVER sees it.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { crc32, deflateSync } from 'node:zlib';

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: node make-series-fixtures.mjs <outDir>');
  process.exit(2);
}

function u32(n) {
  const b = Buffer.alloc(4);
  b.writeUInt32BE(n >>> 0);
  return b;
}

/** A small solid-colour RGB PNG (decodable, so thumbnails and pages render). */
function png(width, height, [r, g, b]) {
  const row = Buffer.alloc(1 + width * 3);
  for (let x = 0; x < width; x++) row.set([r, g, b], 1 + x * 3);
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  const chunk = (type, data) => {
    const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    return Buffer.concat([u32(data.length), td, u32(crc32(td))]);
  };
  const ihdr = Buffer.concat([u32(width), u32(height), Buffer.from([8, 2, 0, 0, 0])]);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** A stored (uncompressed) ZIP with the given entries. */
function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const { name, data } of entries) {
    const nameBuf = Buffer.from(name, 'utf8');
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt32LE(crc >>> 0, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    locals.push(local, nameBuf, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt32LE(crc >>> 0, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);
    offset += 30 + nameBuf.length + data.length;
  }
  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBuf, end]);
}

function comicInfo(fields) {
  const esc = (v) => String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const body = Object.entries(fields).map(([k, v]) => `  <${k}>${esc(v)}</${k}>`).join('\n');
  return Buffer.from(`<?xml version="1.0" encoding="utf-8"?>\n<ComicInfo>\n${body}\n</ComicInfo>\n`, 'utf8');
}

function archive(dir, file, colour, info) {
  mkdirSync(join(outDir, dir), { recursive: true });
  const entries = [1, 2, 3].map((i) => ({ name: `page${String(i).padStart(3, '0')}.png`, data: png(60, 90, colour.map((c) => (c + i * 20) % 256)) }));
  if (info) entries.push({ name: 'ComicInfo.xml', data: comicInfo(info) });
  writeFileSync(join(outDir, dir, file), zip(entries));
}

for (const n of [1, 2, 3]) {
  archive('Synthetic Series', `Synthetic Saga v0${n}.cbz`, [120, 60, 200], {
    Series: 'Synthetic Saga',
    Number: n,
    Volume: n,
    Count: 3,
    Title: `Part ${n}`,
    Summary: `Synthetic summary of part ${n}.`,
    Year: 2019 + n,
    Writer: 'Test Writer',
    Penciller: 'Test Artist',
    Publisher: 'Synthetic Press',
    Genre: 'Action, Fantasy',
    Web: 'https://www.mangaupdates.com/series/abc123/synthetic-saga',
    LanguageISO: 'en',
    Manga: 'YesAndRightToLeft',
  });
}
archive('Synthetic Anthology', 'Issue A.cbz', [40, 160, 90], { Series: 'Alpha Tale', Number: 1 });
archive('Synthetic Anthology', 'Issue B.cbz', [200, 140, 40], { Series: 'Beta Tale', Number: 1 });
archive('Plain Folder', 'No Info 01.cbz', [90, 90, 90], null);
console.log(`Series fixtures written to ${outDir}`);
