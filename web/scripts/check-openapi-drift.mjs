// OpenAPI drift check. Compares every schema in contracts/openapi.json against the matching
// exported interface/type in src/app/core/api/api-types.ts (parsed with the TypeScript
// compiler API): property names in both directions, required-vs-optional, and nullability.
// Intentional differences live in openapi-drift-allowlist.json (each with a reason).
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const __dirname = dirname(fileURLToPath(import.meta.url));
const openApiPath = join(__dirname, '..', '..', 'contracts', 'openapi.json');
const typesPath = join(__dirname, '..', 'src', 'app', 'core', 'api', 'api-types.ts');
const allowlistPath = join(__dirname, 'openapi-drift-allowlist.json');

for (const p of [openApiPath, typesPath, allowlistPath]) {
  if (!existsSync(p)) {
    console.error(`Drift check input not found: ${p}`);
    process.exit(1);
  }
}

const spec = JSON.parse(readFileSync(openApiPath, 'utf-8'));
if (!spec.openapi || !spec.info || !spec.components?.schemas) {
  console.error('Invalid OpenAPI document: missing openapi, info or components.schemas.');
  process.exit(1);
}
const allowlist = JSON.parse(readFileSync(allowlistPath, 'utf-8'));
const ignoredSchemas = allowlist.ignoredSchemas ?? {};
const aliases = allowlist.aliases ?? {};
const ignoredProperties = allowlist.ignoredProperties ?? {};

// TS side: name -> Map(property -> { optional, nullable }). Interface merging is honoured.
const source = ts.createSourceFile(typesPath, readFileSync(typesPath, 'utf-8'), ts.ScriptTarget.Latest, true);
const tsTypes = new Map();

function includesNull(typeNode) {
  if (!typeNode) return false;
  if (ts.isLiteralTypeNode(typeNode)) return typeNode.literal.kind === ts.SyntaxKind.NullKeyword;
  if (ts.isUnionTypeNode(typeNode)) return typeNode.types.some(includesNull);
  if (ts.isParenthesizedTypeNode(typeNode)) return includesNull(typeNode.type);
  return false;
}

function collect(name, members) {
  const props = tsTypes.get(name) ?? new Map();
  for (const m of members) {
    if (!ts.isPropertySignature(m) || !m.name) continue;
    props.set(m.name.getText(source).replace(/^['"]|['"]$/g, ''), {
      optional: !!m.questionToken,
      nullable: includesNull(m.type),
    });
  }
  tsTypes.set(name, props);
}

for (const stmt of source.statements) {
  if (!stmt.modifiers?.some((mod) => mod.kind === ts.SyntaxKind.ExportKeyword)) continue;
  if (ts.isInterfaceDeclaration(stmt)) collect(stmt.name.text, stmt.members);
  else if (ts.isTypeAliasDeclaration(stmt) && ts.isTypeLiteralNode(stmt.type)) collect(stmt.name.text, stmt.type.members);
  else if (ts.isTypeAliasDeclaration(stmt) || ts.isEnumDeclaration(stmt)) tsTypes.set(stmt.name.text, null);
}

const problems = [];
const used = { schemas: new Set(), props: new Set() };

for (const [schemaName, schema] of Object.entries(spec.components.schemas)) {
  if (schemaName in ignoredSchemas) {
    used.schemas.add(schemaName);
    continue;
  }
  const tsName = aliases[schemaName] ?? schemaName;
  if (!tsTypes.has(tsName)) {
    problems.push(`${schemaName}: no exported interface/type '${tsName}' in api-types.ts (add it, or allowlist the schema with a reason)`);
    continue;
  }
  const tsProps = tsTypes.get(tsName);
  if (tsProps === null) continue; // non-object type (union/enum alias): existence only
  const specProps = schema.properties ?? {};
  const required = new Set(schema.required ?? []);

  for (const [prop, def] of Object.entries(specProps)) {
    const key = `${schemaName}.${prop}`;
    if (key in ignoredProperties) {
      used.props.add(key);
      continue;
    }
    const ts_ = tsProps.get(prop);
    if (!ts_) {
      problems.push(`${key}: in the contract but missing from ${tsName} in api-types.ts`);
      continue;
    }
    if (required.has(prop) && ts_.optional) {
      problems.push(`${key}: required by the contract but optional (?) in ${tsName}`);
    }
    const specNullable = Array.isArray(def.type) ? def.type.includes('null') : (def.oneOf ?? []).some((o) => o.type === 'null');
    if (ts_.nullable && !specNullable) {
      problems.push(`${key}: ${tsName} allows null but the contract does not`);
    }
  }
  for (const prop of tsProps.keys()) {
    const key = `${schemaName}.${prop}`;
    if (key in ignoredProperties) {
      used.props.add(key);
      continue;
    }
    if (!(prop in specProps)) problems.push(`${key}: in ${tsName} in api-types.ts but not in the contract`);
  }
}

for (const key of [...Object.keys(ignoredSchemas)]) {
  if (!used.schemas.has(key)) problems.push(`allowlist: ignoredSchemas entry '${key}' matches no schema (stale; remove it)`);
}
for (const key of Object.keys(ignoredProperties)) {
  if (!used.props.has(key)) problems.push(`allowlist: ignoredProperties entry '${key}' matches no property (stale; remove it)`);
}

const count = Object.keys(spec.components.schemas).length;
if (problems.length > 0) {
  console.error(`OpenAPI drift check FAILED (${problems.length} problem(s) across ${count} schemas):`);
  for (const p of problems) console.error(`  - ${p}`);
  console.error('The server contract is the source of truth: update api-types.ts, or add a reasoned entry to web/scripts/openapi-drift-allowlist.json.');
  process.exit(1);
}
console.log(`OpenAPI drift check passed: ${spec.openapi} version ${spec.info.version}, ${count} schemas (${Object.keys(ignoredSchemas).length} allowlisted).`);
