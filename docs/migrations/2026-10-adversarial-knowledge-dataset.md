# Adversarial knowledge evaluation dataset

## Change

`Nao.Eval` now provides `KnowledgeAdversarialDatasets.adversarialV1`, a synthetic executable
knowledge corpus covering:

- source poisoning;
- retrieved prompt injection;
- stale sources;
- conflicting ambiguous sources;
- authorization-boundary violations.

The corpus contains source, document, chunk, text, trust, freshness, access, and risk evidence.
Each scenario wraps a normal `KnowledgeEvaluationCase`, so the same retrieval and grounded
generation evaluators consume its relevant documents, relevant chunks, expected facts, and
prohibited results.

## Integrity

`KnowledgeAdversarialDataset` has schema version `1`, a positive revision, and a deterministic
content hash over:

- the derived `KnowledgeEvaluationDataset` hash;
- all corpus identities and exact synthetic text;
- trust, freshness, access, and risk classifications;
- every case's adversarial risk classifications.

Construction fails when chunks are duplicated, documents span multiple sources, judgments refer
to absent corpus entries, or a case declares a risk that is not represented by its relevant or
prohibited chunks.

The ACL scenario marks restricted source content as unauthorized relative to the fixture
principal. The ambiguity and ACL scenarios intentionally have no expected facts, making explicit
abstention the correct grounded-generation outcome.

## Compatibility

This is a new unreleased evaluation contract. It does not add an adapter for external or legacy
fixture shapes.
