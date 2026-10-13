# Federated memory and knowledge recall

## Contract change

`MemoryTools.create` now requires an optional `MemoryKnowledgeRecall` capability. There is no
compatibility overload.

`memory_search` requires `source`:

- `memory` searches learned durable memory;
- `knowledge` searches the configured source-backed knowledge capability;
- `all` searches both independently.

Optional `memoryTags` filter only learned memory. `limit` is a per-source limit bounded by host
policy.

## Separate result semantics

Search output contains two arrays:

- `memories`: key, value, timestamp, and tags;
- `knowledge`: content, retrieval score, and source/document/version/chunk/URI/content-hash
  provenance.

The tool does not merge or normalize memory relevance and knowledge retrieval scores. Knowledge
passages are not converted into `MemoryEntry` values, and memories are not represented as
knowledge chunks or citations.

## Authorization and mutation

When knowledge recall is configured, `memory_search` declares both the durable-memory read
permission and the capability's concrete knowledge-retrieve permission. The same authenticated
authorization scope and cancellation token flow into both stores.

Knowledge-only search does not read memory storage. `memory_remember` and `memory_forget` continue
to operate exclusively on `MemoryStore`; they cannot write, replace, or delete knowledge.
Requesting knowledge or federated recall without a configured capability fails explicitly.

Hosts construct `MemoryKnowledgeRecall` around their authorized retrieval pipeline and map
retrieved passages into the provider-neutral provenance contract.
