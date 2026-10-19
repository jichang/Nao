# Versioned evaluation specification

Nao is unreleased, so the former payload-only evaluation contracts were replaced directly rather
than retained behind compatibility constructors.

## Removed contracts

- `EvalCase.Input`, `EvalCase.Expected`, and `EvalCase.Tags`
- `EvalCase.create`, `EvalCase.openEnded`, and `EvalCase.withTags`
- unversioned `EvalDataset.create owner name cases`
- name-only `Evaluator.create`
- `EvalExecutionConfig.create` without executable component identity
- `EvalRun.create` with only owner and dataset ID

## Current contracts

- Construct complete cases with `EvalCase.create`, or explicitly repository-authored text cases
  with `EvalCase.repository` and `EvalCase.repositoryOpenEnded`.
- Read executable input through `EvalCase.input`; inspect structured expectations with
  `EvalCase.expectedBehavior`.
- Construct datasets with a positive revision and fixture list. Use `EvalDataset.revise` to retain
  identity while advancing the revision and content hash.
- Create evaluators with name, implementation version, canonical configuration text, and the
  evaluation function. The configuration is stored as a SHA-256 identity.
- Supply `EvalExecutionSpecification` when creating `EvalExecutionConfig`.
- Construct `EvalRun` with dataset revision, dataset content hash, and the resolved
  `EvalExecutableConfiguration`.

Reports and archive records remain schema version 1 because no Nao evaluation schema has been
released. The current version-1 shape is authoritative; no legacy reader or migration adapter is
provided.
