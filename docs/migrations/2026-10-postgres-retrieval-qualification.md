# PostgreSQL production retrieval qualification

## Change

The PostgreSQL integration suite now executes one complete production retrieval path:

1. index source-backed documents in pgvector and PostgreSQL full-text search;
2. query both indexes through the authorized hybrid retrieval pipeline;
3. fuse, select, rerank, and assemble bounded context;
4. create and deterministically verify a citation against the retrieved source evidence;
5. repeat retrieval under a different authenticated user and require an empty result.

The test retains and asserts source and chunk identities through citation verification.

## PostgreSQL JSON repair

Executing the complete path exposed that private F# persistence DTOs could serialize source
locations and trust assessments but could not be instantiated by `System.Text.Json` during
retrieval. The adapter now writes explicit canonical JSON property names and parses the JSON
structure directly. Missing properties, malformed structures, unsupported trust values, and
invalid source locations fail closed.

Nao is unreleased, so the stored JSON shape was corrected in place without a legacy reader.
The Nao-owned index schema marker remains version 1.

## Scope

This qualifies production indexing, SQL-bound authorization, hybrid retrieval, provenance, context
assembly, and citation verification. It does not qualify a production generation adapter or close
the broader R2 production-adapter-set exit criterion.
