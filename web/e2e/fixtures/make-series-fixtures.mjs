// Synthetic ComicInfo fixture library for the series-info e2e spec (1.24.0).
// Dependency-free (node:zlib has crc32 + deflate). Every name and value is invented.
//
//   node web/e2e/fixtures/make-series-fixtures.mjs <outDir>
//
// Layout written under <outDir>:
//   Synthetic Series/Synthetic Saga v01..v03.cbz   ComicInfo "Synthetic Saga" #1-3 (one series)
//   Synthetic Anthology/Issue A.cbz, Issue B.cbz   two different series (a "mixed" folder)
//   Plain Folder/No Info 01.cbz                    no ComicInfo.xml
//
// And, next to it, <outDir>-volumes (1.29.0, the Volumes view e2e spec; offline, no ComicInfo):
//   Stacked Saga/Stacked Saga v01 c001..c003.cbz   chapters that state volume 1
//   Stacked Saga/Stacked Saga v02 c004..c005.cbz   chapters that state volume 2
//   Stacked Saga/Stacked Saga v02.5.cbz            a fractional (bonus) volume: the end of volume 2's stack (1.29.0 RC)
//   Stacked Saga/Stacked Saga v03.cbz              a real volume file (no chapters of its own volume)
//   Indexed Webtoon/0001 [0000].cbz .. 0010 [0008 - Some Title].cbz   a running index, the chapter in brackets (1.34.0)
//   Doubled Saga/Doubled Saga v01 c001..c003.cbz   chapters that state volume 1, with chapters 1 and 2 each in a second file
//                                                  (`... c001 [part 2].cbz`): duplicate numbers (1.31.0)
//   Story Artist/Story Artist - Alpha Tale.cbz, - Beta Tale.cbz, - Gamma Tale.cbz   an artist's folder of stories (1.37.0, tankoubon
//                                                  stacks: the links to one collected volume are served in the browser by the spec)
//
// And <outDir>-reading-modes (1.41.0, the reading-mode suggestions e2e spec; offline, no ComicInfo):
//   Tall Strip Tale/Tall Strip Tale c001..c003.cbz   8 pages of 40 x 160 each (webtoon-shaped tall strips: 24 measured, all tall)
//   Paged Tale/Paged Tale v01..v03.cbz               8 pages of 60 x 90 each (ordinary pages)
// And <outDir>-jump (1.41.0, the smart jump rail e2e spec; offline, no ComicInfo):
//   Jump Saga/Jump Saga - Ch. 001.cbz .. Ch. 064.cbz   numbered chapters behind a common prefix -> number ranges 0, 10 ... 60
//   Jump Saga/Jump Saga - Oneshot.cbz                  an extra without a number -> its own "Other" chip after the ranges
//   Jump Doujin/[<Circle> Circle] Synthetic Tale <Word>.cbz   24 tag-led names (1.49.0) -> bracket chips [A ... [F
// And <outDir>-volume-contents (1.44.0, the volume contents scan e2e spec; offline, no ComicInfo; patterned pages so each page has its
// own perceptual hash):
//   Contents Saga/Contents Saga v02.cbz            2 cover pages, the pages of chapters 9 and 10, 2 back pages
//   Contents Saga/Contents Saga - Chapter 009.cbz, - Chapter 010.cbz   the same pages from "another release" (lightly changed pixels)
//   Contents Saga/Contents Saga - Chapter 011.cbz  pages that are not in the volume
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

function archive(dir, file, colour, info, base = outDir) {
  mkdirSync(join(base, dir), { recursive: true });
  const entries = [1, 2, 3].map((i) => ({ name: `page${String(i).padStart(3, '0')}.png`, data: png(60, 90, colour.map((c) => (c + i * 20) % 256)) }));
  if (info) entries.push({ name: 'ComicInfo.xml', data: comicInfo(info) });
  writeFileSync(join(base, dir, file), zip(entries));
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

// Volumes view fixtures: names alone state the volume, so the spec needs no series record and no network.
const volumesDir = `${outDir}-volumes`;
for (const [volume, chapters] of [[1, [1, 2, 3]], [2, [4, 5]]]) {
  for (const c of chapters) {
    archive('Stacked Saga', `Stacked Saga v0${volume} c00${c}.cbz`, [40 + volume * 60, 90 + c * 20, 160], null, volumesDir);
  }
}
archive('Stacked Saga', 'Stacked Saga v02.5.cbz', [120, 200, 90], null, volumesDir);
archive('Stacked Saga', 'Stacked Saga v03.cbz', [200, 80, 80], null, volumesDir);
for (const name of ['c001', 'c001 [part 2]', 'c002', 'c002 [part 2]', 'c003']) {
  archive('Doubled Saga', `Doubled Saga v01 ${name}.cbz`, [90 + name.length * 5, 120, 60], null, volumesDir);
}
// 1.34.0: a webtoon named with a running index in front of the chapter ("0003 [0001 - Some Title]"; index = chapter + 2).
archive('Indexed Webtoon', '0001 [0000].cbz', [70, 70, 150], null, volumesDir);
archive('Indexed Webtoon', '0002 [0000.5].cbz', [80, 80, 160], null, volumesDir);
for (let c = 1; c <= 8; c++) {
  archive('Indexed Webtoon', `${String(c + 2).padStart(4, '0')} [${String(c).padStart(4, '0')} - Some Title].cbz`, [60 + c * 15, 140, 200 - c * 10], null, volumesDir);
}
// 1.37.0: an artist's folder of individual stories (no numbers, no ComicInfo).
for (const [i, story] of ['Alpha Tale', 'Beta Tale', 'Gamma Tale'].entries()) {
  archive('Story Artist', `Story Artist - ${story}.cbz`, [180 - i * 50, 60 + i * 60, 120], null, volumesDir);
}
// 1.41.0: reading-mode suggestions (offline, no ComicInfo): one folder of webtoon-shaped tall strips, one of ordinary pages.
const readingModesDir = `${outDir}-reading-modes`;
function stripArchive(dir, file, colour, width, height, pages) {
  mkdirSync(join(readingModesDir, dir), { recursive: true });
  const entries = Array.from({ length: pages }, (_, i) =>
    ({ name: `page${String(i + 1).padStart(3, '0')}.png`, data: png(width, height, colour.map((c) => (c + i * 9) % 256)) }));
  writeFileSync(join(readingModesDir, dir, file), zip(entries));
}
for (const c of [1, 2, 3]) {
  stripArchive('Tall Strip Tale', `Tall Strip Tale c00${c}.cbz`, [60 + c * 40, 120, 180], 40, 160, 8);
  stripArchive('Paged Tale', `Paged Tale v0${c}.cbz`, [180, 60 + c * 40, 90], 60, 90, 8);
}
// 1.41.0: a folder of numbered chapters for the smart jump rail (names synthetic).
const jumpDir = `${outDir}-jump`;
for (let c = 1; c <= 64; c++) {
  archive('Jump Saga', `Jump Saga - Ch. ${String(c).padStart(3, '0')}.cbz`, [40 + (c * 3) % 200, 120, 220 - (c * 2) % 200], null, jumpDir);
}
archive('Jump Saga', 'Jump Saga - Oneshot.cbz', [220, 180, 60], null, jumpDir);
// 1.49.0: a doujin-style level - every name opens with a "[Circle]" tag (six synthetic circles, four tales each) -> bracket chips [A ... [F.
const doujinCircles = ['Alder', 'Birch', 'Cedar', 'Dogwood', 'Elm', 'Fir'];
const doujinTales = ['Alpha', 'Bravo', 'Charlie', 'Delta'];
doujinCircles.forEach((circle, ci) => doujinTales.forEach((tale, ti) => {
  archive('Jump Doujin', `[${circle} Circle] Synthetic Tale ${tale}.cbz`, [60 + ci * 30, 90 + ti * 40, 150], null, jumpDir);
}));

// 1.44.0: the volume contents scan. A patterned page (seeded grey blocks on white) - unlike the solid-colour pages above, every page
// has its own perceptual hash; `jitter` changes the pixels slightly (another release of the same page: other bytes, the same picture).
function patternPng(width, height, seed, jitter) {
  let state = seed * 2654435761 >>> 0;
  const next = () => (state = (state * 1103515245 + 12345) >>> 0) / 4294967296;
  const pixels = new Uint8Array(width * height).fill(255);
  for (let k = 0; k < 10; k++) {
    const x0 = Math.floor(next() * width), y0 = Math.floor(next() * height);
    const x1 = Math.min(width, x0 + 10 + Math.floor(next() * width / 2)), y1 = Math.min(height, y0 + 10 + Math.floor(next() * height / 2));
    const grey = Math.floor(next() * 160);
    for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) pixels[y * width + x] = grey;
  }
  const rows = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3);
    for (let x = 0; x < width; x++) {
      const v = Math.max(0, Math.min(255, pixels[y * width + x] + (jitter && (x + y) % 7 === 0 ? 6 : 0)));
      row.set([v, v, v], 1 + x * 3);
    }
    rows.push(row);
  }
  const chunk = (type, data) => {
    const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    return Buffer.concat([u32(data.length), td, u32(crc32(td))]);
  };
  const ihdr = Buffer.concat([u32(width), u32(height), Buffer.from([8, 2, 0, 0, 0])]);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(Buffer.concat(rows))),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}
const contentsDir = join(`${outDir}-volume-contents`, 'Contents Saga');
mkdirSync(contentsDir, { recursive: true });
const pagesOf = (seeds, jitter) => seeds.map((seed, i) => ({ name: `page${String(i + 1).padStart(3, '0')}.png`, data: patternPng(120, 180, seed, jitter) }));
const chapter9 = [91, 92, 93, 94, 95, 96];
const chapter10 = [101, 102, 103, 104, 105, 106];
writeFileSync(join(contentsDir, 'Contents Saga v02.cbz'), zip(pagesOf([1, 2, ...chapter9, ...chapter10, 3, 4], false)));
writeFileSync(join(contentsDir, 'Contents Saga - Chapter 009.cbz'), zip(pagesOf(chapter9, true)));
writeFileSync(join(contentsDir, 'Contents Saga - Chapter 010.cbz'), zip(pagesOf(chapter10, true)));
writeFileSync(join(contentsDir, 'Contents Saga - Chapter 011.cbz'), zip(pagesOf([111, 112, 113, 114, 115, 116], true)));
console.log(`Series fixtures written to ${outDir} (and ${volumesDir}, ${readingModesDir}, ${jumpDir}, ${outDir}-volume-contents)`);
