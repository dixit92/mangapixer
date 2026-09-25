// THIRD-PARTY-NOTICES.md drift check. Compares, by package name and version, the shipped
// inventory in the notices file against what the build actually produces:
//   - section 1 (1a + 1b, shipped .NET) vs the union of "type": "package" entries in the
//     *.deps.json files of the Release `dotnet publish` output directories given with --publish;
//   - section 3 (web runtime npm) vs `npm ls --omit=dev --all --json` (run in web/, needs npm ci).
// Sections 2 and 4 (build/test sets) are deliberately not checked: section 4 is a Windows x64
// install (platform binaries differ on Linux) and section 2 needs a restore of the test projects.
// Usage: node scripts/check-notices-drift.mjs --publish <dir> [--publish <dir> ...] [--skip-dotnet] [--skip-npm]
// Exit code 1 on drift or on unreadable input.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const webDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const noticesPath = join(webDir, '..', 'THIRD-PARTY-NOTICES.md');

const publishDirs = [];
let skipDotnet = false;
let skipNpm = false;
const args = process.argv.slice(2);
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--publish') publishDirs.push(args[++i]);
  else if (args[i] === '--skip-dotnet') skipDotnet = true;
  else if (args[i] === '--skip-npm') skipNpm = true;
  else fail(`Unknown argument: ${args[i]}`);
}

function fail(message) {
  console.error(message);
  process.exit(1);
}

// Notices entries look like: - `name` - version - license - url
const entry = /^- `([^`]+)` - (\S+) - /;
const npmName = /^(@[a-z0-9._-]+\/)?[a-z0-9._-]+$/i;

function section(lines, startPrefix, endPrefix) {
  const start = lines.findIndex((l) => l.startsWith(startPrefix));
  if (start < 0) fail(`Notices heading not found: ${startPrefix}`);
  let end = lines.findIndex((l, i) => i > start && l.startsWith(endPrefix));
  if (end < 0) end = lines.length;
  return lines.slice(start + 1, end);
}

function parseEntries(lines) {
  const map = new Map();
  for (const line of lines) {
    const m = entry.exec(line);
    if (!m || !npmName.test(m[1])) continue; // skips prose such as the "WiX Toolset" line
    if (!map.has(m[1])) map.set(m[1], new Set());
    map.get(m[1]).add(m[2]);
  }
  return map;
}

function add(map, name, version) {
  if (!map.has(name)) map.set(name, new Set());
  map.get(name).add(version);
}

function diff(label, documented, actual) {
  const problems = [];
  for (const [name, versions] of [...actual].sort()) {
    const doc = documented.get(name);
    if (!doc) problems.push(`  missing from notices: ${name} ${[...versions].sort().join(', ')}`);
    else {
      const a = [...versions].sort().join(', ');
      const d = [...doc].sort().join(', ');
      if (a !== d) problems.push(`  version mismatch: ${name} notices ${d}, actual ${a}`);
    }
  }
  for (const [name, versions] of [...documented].sort()) {
    if (!actual.has(name)) problems.push(`  extra in notices (not in build output): ${name} ${[...versions].sort().join(', ')}`);
  }
  if (problems.length === 0) console.log(`${label}: OK (${actual.size} packages)`);
  else console.error(`${label}: DRIFT (${problems.length})\n${problems.join('\n')}`);
  return problems.length;
}

if (!existsSync(noticesPath)) fail(`Notices file not found: ${noticesPath}`);
const lines = readFileSync(noticesPath, 'utf-8').split(/\r?\n/);
let drift = 0;

if (!skipDotnet) {
  if (publishDirs.length === 0) fail('--publish <dir> is required (or pass --skip-dotnet).');
  const shipped = section(lines, '## 1. ', '### 1c.');
  const documented = parseEntries(shipped);
  const actual = new Map();
  for (const dir of publishDirs) {
    const files = existsSync(dir) ? readdirSync(dir).filter((f) => f.endsWith('.deps.json')) : [];
    if (files.length === 0) fail(`No *.deps.json in publish directory: ${dir}`);
    for (const file of files) {
      const deps = JSON.parse(readFileSync(join(dir, file), 'utf-8'));
      for (const [key, lib] of Object.entries(deps.libraries ?? {})) {
        if (lib.type !== 'package') continue;
        const at = key.lastIndexOf('/');
        add(actual, key.slice(0, at), key.slice(at + 1));
      }
    }
  }
  drift += diff('Section 1 (.NET shipped)', documented, actual);
}

if (!skipNpm) {
  const documented = parseEntries(section(lines, '## 3. ', '## 4. '));
  const run = spawnSync('npm', ['ls', '--omit=dev', '--all', '--json'], {
    cwd: webDir,
    encoding: 'utf-8',
    maxBuffer: 64 * 1024 * 1024,
    shell: process.platform === 'win32',
  });
  let tree;
  try {
    tree = JSON.parse(run.stdout);
  } catch {
    fail(`npm ls produced no JSON (run npm ci first): ${run.stderr}`);
  }
  if (tree.problems?.length) fail(`npm ls reported problems (run npm ci first):\n${tree.problems.join('\n')}`);
  const actual = new Map();
  const walk = (deps) => {
    for (const [name, node] of Object.entries(deps ?? {})) {
      if (node.version) add(actual, name, node.version);
      walk(node.dependencies);
    }
  };
  walk(tree.dependencies);
  drift += diff('Section 3 (web runtime npm)', documented, actual);
}

if (drift > 0) {
  console.error(`\nTHIRD-PARTY-NOTICES.md is out of date (${drift} difference(s)). Refresh it as described in its section 7.`);
  process.exit(1);
}
