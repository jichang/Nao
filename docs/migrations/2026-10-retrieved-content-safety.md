# 2026-10 retrieved-content safety

## Inspection boundary

Context assembly now requires an explicit `KnowledgeContextSafetyPolicy`. The policy owns a
versioned `KnowledgeRetrievedContentInspector` that runs on exact source text before token or
source-budget allocation.

The built-in strict inspector reports typed, range-bound, content-hashed findings for:

- reserved `[nao-...]` control syntax, including forged citation markers and block delimiters;
- common prompt-injection directives that attempt to replace prior instructions or reveal the
  system prompt;
- common model role/control delimiters.

Candidates with any finding are suppressed as `UnsafeRetrievedContent`. Finding evidence records
kind, offset, length, and matched-content hash without copying hostile text into decision logs.

Hosts may supply another synchronous inspector through the same contract. Custom inspectors must
fail closed and should preserve at least the reserved-control-marker rule. The built-in patterns
are deterministic defenses, not a claim that arbitrary natural-language prompt injection can be
perfectly classified.

## Context isolation

Included chunks are rendered inside framework-owned source blocks containing:

- a chunk-bound begin delimiter;
- an explicit untrusted-data policy line;
- the framework-generated citation marker immediately before the exact source statement;
- a chunk-bound end delimiter.

Source text containing the reserved delimiter namespace is suppressed before rendering, so it
cannot close a block or manufacture framework citation syntax. Security framing counts against the
declared token budget.

Source authority, document identity, versions, trust assessments, and citation provenance continue
to come from validated index records rather than claims inside source text.

## Breaking change

`KnowledgeContextAssemblyConfig.create` now requires a safety policy. This is a deliberate change
to the unreleased API. No overload with uninspected retrieval exists.
