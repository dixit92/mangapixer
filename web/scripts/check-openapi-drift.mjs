// OpenAPI drift check: intended to regenerate the schema into a temp output and compare it
// against the checked-in TypeScript types. Currently only validates that the generated
// contracts/openapi.json exists and is well-formed; the actual comparison isn't implemented yet.
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
console.log('Placeholder check only: full drift comparison against checked-in types is not yet implemented.');
