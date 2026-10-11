# Grounded generation evaluation metrics

## Change

`Nao.Eval` now measures grounded-generation quality directly from versioned
`KnowledgeEvaluationDataset` judgments and the evidence already emitted by the grounded-answer
pipeline.

`KnowledgeGenerationObservation.ofGroundedOutput` projects a `KnowledgeGroundedOutput` into
evaluation evidence without retaining answer or source text. The observation preserves:

- the accepted, warned, abstained, repair-required, or rejected outcome;
- claim-support verdicts;
- cited chunk and citation-reference identities;
- references selected by the support evaluator as entailing each claim;
- support evaluator component, model, configuration hash, usage, cost, and duration.

## Metrics

`KnowledgeGroundedGenerationEvaluator.measure` requires exactly one result for every dataset case
and reports:

- **context relevance**: the fraction of assembled context chunks labeled relevant for the case;
- **answer faithfulness**: the fraction of published material claims judged supported;
- **citation correctness**: the fraction of published citation occurrences that both were selected
  as supporting the claim and point to a chunk labeled relevant for the case;
- **abstention quality**: exact response-decision accuracy, where cases with expected facts require
  an answer and cases without expected facts require abstention.

Repair-required and rejected outcomes are unresolved, not successful abstentions. Metrics that
have no measurable denominator are `None`, rather than a success-shaped zero or one.

The report retains per-case evidence, macro averages, abstention counts, unresolved outcomes, and
total support-evaluator calls, tokens, cost, and duration.

## Compatibility

This is a new unreleased evaluation contract. No compatibility adapter or legacy result shape is
provided.
