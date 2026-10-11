# 2026-10 grounded generation response protocol

## Protocol boundary

`KnowledgeGroundedResponseProtocol` is a provider-neutral
`Nao.Protocols.ResponseProtocol<KnowledgeGroundedGenerationResult>`. The same descriptor owns
prompt instructions, DTO deserialization, structured errors, and bounded repair guidance.

Models return one JSON object with either:

- `status: "answer"` and answer text, confidence, claims, and citation assertions;
- `status: "insufficient"` and a typed reason plus explanation.

No prose, markdown, code fences, or additional actions are accepted.

## Cited claims

Every material claim must include:

- a unique claim ID;
- an exact zero-based answer offset and length;
- `kind` set to `source-quotation` or `generated-interpretation`;
- finite confidence between zero and one;
- one or more citation-reference IDs.

Every asserted citation-reference ID must be defined in the response. Citation definitions contain
an ID, stable context marker, and chunk ID. Markers must exactly encode the declared chunk ID.

These assertions remain unverified proposals. The deterministic verifier is responsible for
proving that each marker, chunk, quoted range, and source location exists in the assembled context.

## Deserialization and bounds

The parser rejects:

- JSON that cannot be deserialized into the selected response DTO;
- missing fields needed by the grounded domain contracts;
- unsupported statuses and insufficiency reasons;
- uncited claims or undefined citation IDs;
- spoofed marker/chunk pairs;
- invalid answer ranges or confidence values;
- missing or unsupported claim kinds;
- configured response, claim, citation, and per-claim citation limit violations.

Ordinary `System.Text.Json` behavior applies. Additional properties are ignored and duplicate
properties use the serializer's normal last-value behavior.

Parse failures retain structured summaries, expected forms, locations when available, and targeted
repair guidance. Repair does not relax the protocol or convert invalid output into a successful
answer.
