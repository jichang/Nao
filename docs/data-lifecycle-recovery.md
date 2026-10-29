# Data lifecycle and disaster recovery

Nao coordinates recovery but does not replace database, object-storage, or volume-snapshot tooling. Hosts must combine provider-native backups with the typed `BackupManifest`, `RestorePlan`, `RestoreDrill`, and migration contracts in `Nao.Platform`.

## Durable-store inventory

The field-level format inventory and current schema markers are maintained in [Durable formats](durable-formats.html). Operationally, stores fall into these recovery groups:

| Recovery group | Nao implementations or boundary | Authority | Backup and restore boundary |
|---|---|---|---|
| Orleans runtime state | Session, session-directory, workspace-route, runtime-administration, lifecycle, and turn-ledger grain state | Authoritative | Back up the configured Orleans ADO.NET provider at one database recovery point. In-memory providers are development-only and are never recoverable. |
| Conversation and execution streams | Conversations, turns, feedback, execution journals, permission grants, audit, metrics, traces, tracer spans, and working/tiered/episodic/graph memory | Authoritative | Back up the ADO.NET event tables or the complete JSONL volume. Preserve every stream sequence and owner partition; never merge lines from different recovery points. |
| PostgreSQL graph state | Owner-partitioned graph nodes, relations, aliases, properties, and provenance | Authoritative | Use PostgreSQL physical/PITR backup for coordinated recovery. `GraphSnapshot` is the portable owner-scoped export and rebuild boundary. |
| Knowledge sources and checkpoints | Host source configuration plus filesystem, Git, and HTTP synchronization checkpoints | Authoritative host boundary | Back up host source registrations and checkpoint files or tables. Source credentials remain secret-provider data and are not copied into backups. |
| Knowledge records | Parsed source records and grounded evidence | Host-owned authority | Nao does not yet ship a durable knowledge-record store. A production host must supply one and include it in the recovery manifest before claiming recoverable RAG. |
| Search and graph indexes | PostgreSQL pgvector and lexical indexes, embedding caches, and host graph-search projections | Derived | Do not treat as sole authority. Rebuild from restored knowledge or graph records. Managed PostgreSQL indexes also support typed export/restore for acceleration, followed by an authoritative rebuild verification. |
| Evaluation archive and baselines | Versioned datasets, reports, results, release baselines, and quality snapshots | Authoritative | Back up the evaluation JSONL stream and reviewed fixture repository at matching revisions and content hashes. Reports without their exact dataset revision fail restoration. |
| Artifacts | Conversation-embedded artifact records and host object payloads | Authoritative split boundary | Back up metadata with conversations and payload bytes in the host artifact store. Restore requires matching artifact identity and content digest. |
| Platform recovery state | Backup manifests, deletion ledger, migration checkpoints, and drill reports | Authoritative control state | Store independently from the protected workload with encryption, immutability, and access logging. Restore this state before customer data. |
| Provider configuration and secrets | Runtime-administration state plus external secret manager | Split authority | Back up non-secret provider configuration with administration state. Restore secret-manager versions through its own protected procedure; secret values never enter a Nao manifest. |

`RecoveryDataKind` names every coordinated category. Each `RecoveryRecord` is owner-bound and content-digested. `BackupManifest.create` rejects duplicate identities, dangling references, cross-owner references, and derived records without authoritative source references.

## Recovery objectives

| Deployment profile | Maximum data loss | Maximum recovery time | Qualification |
|---|---:|---:|---|
| Local development | Best effort | Best effort | Disposable local files; no durability claim |
| Single-node production | 15 minutes | 4 hours | Backup cadence, WAL retention, and restore drill must prove both bounds |
| Clustered production | 5 minutes | 1 hour | Highly available database plus PITR and a timed isolated restore drill |
| Isolated worker | Control-plane owned | Control-plane owned | Worker containers hold no authoritative local state |

These are maximum supported objectives, not automatic guarantees. A deployment must configure storage capable of meeting a profile and retain drill evidence. `RestoreDrill` fails the objective when backup age or elapsed restore time exceeds the selected bound.

## Backup procedure

1. Verify storage, membership, and deletion-ledger health. Reject the backup when any authoritative component is unavailable.
2. Obtain one provider recovery point:
   - for PostgreSQL, record the timeline and WAL location and use provider-native physical backup or an equivalent consistent snapshot;
   - for JSONL deployments, pause mutations and snapshot the complete volume atomically;
   - for object artifacts, obtain a versioned inventory at the same logical cutoff.
3. Capture Orleans state, every Nao event stream, PostgreSQL graph state, knowledge authority, artifact payload inventory, evaluation archives, and platform control state.
4. Emit one content-digested `RecoveryRecord` per restorable object. Session records must reference their permission grants, policies, memory, artifacts, traces, and execution journals. Evaluation reports must reference their exact datasets. Derived index records must reference authoritative knowledge or graph records.
5. Create schema-v1 `BackupManifest` with the provider recovery point, capture time, region, and records.
6. Copy the current deletion ledger into the independently protected recovery-control store. The ledger is not rolled back with customer data.
7. Verify encryption, manifest hashes, object availability, database backup completion, and cross-store references before marking the backup usable.

A database snapshot without its deletion ledger and manifest is not a valid Nao backup.

## Restore and point-in-time recovery

1. Create an isolated target environment in an allowed region. Keep public ingress and runtime admission disabled.
2. Restore the latest deletion ledger, legal holds, regional policy, and migration checkpoints first.
3. Restore PostgreSQL or the selected durable provider to the requested recovery point using provider-native PITR. Restore complete JSONL volumes only from an atomic snapshot; partial stream copying is unsupported.
4. Generate a manifest for the restored recovery point and create `RestorePlan`. The plan suppresses every object in the current deletion ledger, including data retained under legal hold.
5. Restore non-secret administration configuration, policies, permission grants, source registrations, artifacts, evaluation datasets, conversations, sessions, and remaining event streams.
6. Execute the plan through a host `RecoveryRestoreTarget`. `RecoveryCoordinator.restoreAsync` restores every authoritative record, requests every derived rebuild, then rereads and compares every expected content digest before returning success.
7. Verify every owner identity and digest. `RestorePlan.create` fails if deletion suppression or missing data would leave a reference dangling, and the coordinator fails if any restored or rebuilt digest differs.
8. Rebuild vector, lexical, graph-search, and embedding-cache projections from authoritative records listed by `DerivedRebuild`. Do not open retrieval while a required projection is partial.
9. Run authorization, referential-consistency, terminal-execution replay, citation, and evaluation-baseline checks.
10. Confirm startup and readiness health, fence the failed write environment, then enable admission.

Cross-region restore is allowed only to a region admitted by placement policy. Multi-region active/active writers and automatic conflict resolution remain unsupported.

## Retention, deletion, archival, and legal hold

| Lifecycle action | Required behavior |
|---|---|
| Retention expiry | Append or record the store-specific tombstone, remove active projections, and add a recovery deletion before the next backup. |
| Session archival | Preserve the session and referenced evidence read-only until its deletion deadline. Archived state is still backed up. |
| Deletion request | Coordinate active-store deletion, then durably append owner/kind/id to the deletion ledger. A restore always applies the latest ledger, even when restoring to a point before the request. |
| Immutable backup retention | Do not rewrite immutable backup media. Suppress deleted records during every restore, then physically expire the backup or destroy its scoped encryption key at the policy deadline. |
| Legal hold | Retain encrypted backup bytes and record the hold reason and retention deadline, but never reactivate held data into a serving environment. Clearing a hold resumes normal expiry; it does not remove the deletion suppression. |
| Regional placement | Backup copies, WAL archives, manifests, deletion ledgers, and drill targets must remain in an allowed region. Cross-region transfer requires an explicit placement-policy update and audit. |

Deletion ledger loss is a severity-one recovery incident because an old backup could resurrect deleted data. Keep it replicated independently of workload backups and test it in every drill.

## Migration procedure

`MigrationPreflight` requires:

- distinct source and target revisions;
- a verified backup identity;
- all incompatible writers fenced; and
- positive storage capacity greater than or equal to the migration requirement.

Create an immutable `MigrationPlan` of uniquely identified reversible steps. `MigrationRunner` writes a durable checkpoint before execution and after every completed step. A failed run resumes after the last durable step only when the plan digest is unchanged. Rollback executes completed steps in reverse and persists each rollback step.

The host must provide a durable, single-writer `MigrationCheckpointStore`; the in-memory implementation is for tests only. Do not deploy a target build until preflight passes. Do not change a plan under an existing migration identity. If rollback cannot be implemented for a transformation, use an external blue/green copy and retain the source until verification instead of supplying a false rollback.

Nao-owned durable schema markers remain v1 while the project is unreleased. Migration revisions identify concrete before/after layouts within v1; they do not create legacy public formats.

## Restore drills

The scheduled CI workflow runs the `RestoreDrill` category against a complete cross-store manifest. It verifies owner isolation, references, deletion suppression, rebuild scheduling, and profile objectives.

Production operators must additionally run:

- a monthly isolated clustered-production PITR drill;
- a quarterly allowed-region relocation drill when a secondary region is configured;
- a restore after storage, schema, encryption-key, or backup-provider changes; and
- a deletion-resurrection test in every drill.

Record backup identity, recovery point, target region, started/completed times, restored counts, rebuild counts, objective result, integrity failures, operator, and follow-up issue. Synthetic CI evidence does not qualify physical storage or replace production drills.
