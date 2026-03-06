#!/usr/bin/env node
// Downloads the GraphQL schema from the API via introspection and writes schema.graphql
import { writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const apiUrl = process.env.RELAY_SCHEMA_URL;
if (!apiUrl) {
  console.error('RELAY_SCHEMA_URL is not set');
  process.exit(1);
}

const introspectionQuery = `
  query IntrospectionQuery {
    __schema {
      queryType { name }
      mutationType { name }
      subscriptionType { name }
      types { ...FullType }
      directives {
        name description locations
        args { ...InputValue }
      }
    }
  }
  fragment FullType on __Type {
    kind name description
    fields(includeDeprecated: true) {
      name description
      args { ...InputValue }
      type { ...TypeRef }
      isDeprecated deprecationReason
    }
    inputFields { ...InputValue }
    interfaces { ...TypeRef }
    enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
    possibleTypes { ...TypeRef }
  }
  fragment InputValue on __InputValue {
    name description type { ...TypeRef } defaultValue
  }
  fragment TypeRef on __Type {
    kind name
    ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } }
  }
`;

console.log(`Downloading schema from ${apiUrl}/graphql ...`);

const res = await fetch(`${apiUrl}/graphql`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ query: introspectionQuery }),
});

if (!res.ok) throw new Error(`HTTP ${res.status}`);
const { data } = await res.json();

// Convert introspection JSON to SDL using graphql-js
const { buildClientSchema, printSchema } = await import('graphql');
const schema = buildClientSchema(data);
const sdl = printSchema(schema);

const outPath = join(dirname(fileURLToPath(import.meta.url)), '../schema.graphql');
writeFileSync(outPath, sdl, 'utf8');
console.log(`schema.graphql updated (${sdl.length} bytes)`);
