// Precompressed web assets (1.46.0): writes a Brotli (`<file>.br`) and a gzip (`<file>.gz`) copy next to every compressible file
// of the production build, so the server sends them as they are instead of compressing per request (or sending the bundle
// uncompressed). The server (StaticAssetDelivery) picks the copy the browser accepts and ignores a copy older than its file.
//
// Usage: node scripts/precompress.mjs [folder]   (default: dist/mangapixer-web/browser; run by `npm run build`)
// Only text formats are compressed (images and woff2 fonts are compressed already); a copy is kept only when it saves at least 5 %.
import { readdir, readFile, stat, unlink, writeFile } from 'node:fs/promises';
import { extname, join, relative } from 'node:path';
import { brotliCompressSync, constants, gzipSync } from 'node:zlib';

const root = process.argv[2] ?? join('dist', 'mangapixer-web', 'browser');
const COMPRESSIBLE = new Set(['.js', '.mjs', '.css', '.html', '.json', '.webmanifest', '.svg', '.txt', '.ico', '.xml', '.map']);
const MIN_BYTES = 1024;
const MAX_RATIO = 0.95;

async function* files(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* files(path);
    else if (entry.isFile()) yield path;
  }
}

async function main() {
  try {
    if (!(await stat(root)).isDirectory()) throw new Error('not a folder');
  } catch {
    console.error(`precompress: build output not found at ${root}`);
    process.exit(1);
  }

  // Copies from an earlier run in the same folder go first (they are rewritten below when still wanted).
  for await (const path of files(root)) {
    if (path.endsWith('.br') || path.endsWith('.gz')) await unlink(path);
  }

  const totals = { files: 0, bytes: 0, br: 0, gz: 0 };
  for await (const path of files(root)) {
    if (!COMPRESSIBLE.has(extname(path).toLowerCase())) continue;
    const data = await readFile(path);
    if (data.length < MIN_BYTES) continue;

    const br = brotliCompressSync(data, {
      params: {
        [constants.BROTLI_PARAM_MODE]: constants.BROTLI_MODE_TEXT,
        [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
        [constants.BROTLI_PARAM_SIZE_HINT]: data.length,
      },
    });
    const gz = gzipSync(data, { level: 9 });
    const keepBr = br.length <= data.length * MAX_RATIO;
    const keepGz = gz.length <= data.length * MAX_RATIO;
    if (!keepBr && !keepGz) continue;
    if (keepBr) await writeFile(path + '.br', br);
    if (keepGz) await writeFile(path + '.gz', gz);
    totals.files += 1;
    totals.bytes += data.length;
    totals.br += keepBr ? br.length : data.length;
    totals.gz += keepGz ? gz.length : data.length;
  }

  const kb = (n) => `${(n / 1024).toFixed(1)} KB`;
  console.log(`precompress: ${totals.files} files in ${relative(process.cwd(), root) || '.'}: ${kb(totals.bytes)} -> br ${kb(totals.br)}, gzip ${kb(totals.gz)}`);
}

await main();
