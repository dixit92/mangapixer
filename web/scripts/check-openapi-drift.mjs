// OpenAPI drift check: regenerate schema into temp output and compare with checked-in types.
// P02 implements the full check. P00 provides the placeholder script.
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const openApiPath = join(__dirname, '..', '..', 'contracts', 'openapi.json');

if (!existsSync(openApiPath)) {
  console.error('contracts/openapi.json not found. Run the server OpenAPI generation first.');
  process.exit(1);
}

const spec = JSON.parse(readFileSync(openApiPath, 'utf-8'));
if (!spec.openapi || !spec.info) {
  console.error('Invalid OpenAPI document: missing openapi or info field.');
  process.exit(1);
}

console.log(`OpenAPI check: ${spec.openapi} version ${spec.info.version}`);
console.log('P00 placeholder: full drift comparison implemented in P02.');
