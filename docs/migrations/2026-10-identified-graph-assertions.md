# Identified graph assertions

## Affected contracts and data

This change replaces tuple-identified `GraphRelation` records with independently identified graph
assertions. It affects `GraphNode`, `GraphRelation`, `GraphMemory`, `GraphEvent`, relation
extractors, and every file or ADO.NET `graph/<owner-key>` event stream.

Nodes now carry aliases, update time, and source provenance. Relations carry a stable ID, explicit
direction, properties, temporal validity, bounded confidence, assertion time, source-version
identity, extraction-operation identity, and evidence addresses. Extraction now returns its
endpoint nodes and relations as one `GraphExtraction`. Relation deletion accepts an assertion ID
rather than a subject/predicate/object tuple. Entity identities and aliases are unique within an
owner partition. Merge preserves assertion identity and provenance while rewiring endpoints; split
requires an explicit replacement for every endpoint that referenced the source entity.

The first incompatible source version is the October 2026 GRA-01 graph foundation.

## Backup

Stop all writers and copy the complete graph event backend before deploying:

- copy every `graph/*.jsonl` file while preserving names and ordering; or
- take a transactionally consistent backup of every ADO.NET event row whose stream starts with
  `graph/`.

Retain the application build and configuration that wrote the backup. Do not merge streams or
derive owner identities from file contents.

## Required transformation or reset

Nao has no in-process legacy reader. Choose one external operation:

1. Reset each graph stream and rebuild it from authoritative source versions and extraction
   operations. This is the preferred operation because it produces complete evidence addresses.
2. Export with the previous build and externally create current node and relation records. The
   transformation must assign a unique relation ID per independent assertion, explicit direction
   and validity, and real source-version, extraction-operation, and evidence identities.

Do not generate placeholder provenance, merge assertions that share a triple, infer direction, or
copy authentication claims into graph payloads. If authoritative provenance is unavailable, reset
the graph rather than presenting legacy assertions as traceable facts.

Current stores expose an owner-bound `GraphSnapshot` as the backup, restore, and rebuild boundary.
Restore validates ownership, unique identities and aliases, relation IDs, and every endpoint before
replacing current state. A foreign or structurally invalid snapshot cannot partially replace a
graph.

## Validation

After rebuild or import:

1. Run `dotnet test tests/Nao.Persistence.Tests/Nao.Persistence.Tests.fsproj --configuration Release --filter "FullyQualifiedName~Graph"`.
2. Verify every relation endpoint resolves to a node in the same owner partition.
3. Verify every relation ID is unique and every relation has at least one evidence address.
4. Delete a test node and confirm all incident assertions disappear after backend reload.
5. Query the same entity from a foreign authorization scope and confirm no nodes, assertions, or
   paths are returned.
6. Merge two test entities and split the result after reload; confirm aliases remain unambiguous
   and assertion IDs and provenance remain unchanged.

## Rollback limits

The previous build cannot decode current events. Rollback requires stopping writers and restoring
the complete pre-upgrade backup. Current events cannot be appended to a restored legacy stream,
and graph mutations performed after cutover are lost.
