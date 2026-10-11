# Knowledge quality regression gates and baseline promotion

## Quality snapshots

`KnowledgeQualitySnapshot` converts hybrid-retrieval or grounded-generation reports into
release-gate evidence. A snapshot retains:

- dataset name, revision, and exact content hash;
- evaluator component and version;
- evaluated case count;
- recording time;
- overall and segmented quality, latency, and cost metrics.

Metric keys distinguish global values from source, format, language, tenant-policy, and query-type
slices. A missing required metric remains missing and fails the gate.

## Thresholds

`KnowledgeQualityGateConfig` declares:

- a minimum case count;
- one absolute threshold per required metric key;
- the maximum permitted regression from the promoted baseline.

Quality ratios are higher-is-better. Latency and cost are lower-is-better. Threshold
configuration is canonically hashed, and duplicate metric keys are rejected.

Gate evaluation requires an exact dataset content-hash match and the same evaluator component
family as the baseline. It records current value, baseline value, direction, absolute threshold,
regression tolerance, and typed failures for every configured check.

## Promotion

`KnowledgeQualityBaselinePromotion.promote` accepts only passing gate results. The version-1
baseline artifact records:

- the complete promoted snapshot;
- gate-configuration hash;
- authenticated promotion owner;
- mandatory review justification;
- promotion time;
- previous baseline identity.

Initial promotion runs without a baseline and enforces absolute thresholds. Later promotions must
also remain within regression tolerances. Dataset changes require a separately reviewed initial
baseline because scores from different corpus identities are not treated as directly comparable.

This is a new unreleased contract; no compatibility gate or unreviewed promotion path is retained.
