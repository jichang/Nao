# Supply-chain Security

Nao release artifacts are built from reviewed, locked dependencies through protected GitHub
environments. The repository policy is machine-readable in `supply-chain-policy.json` and enforced
by `scripts/validate-supply-chain.py`.

## Dependency and build-tool policy

Paket resolves all NuGet dependencies into `paket.lock`. The policy records that file's SHA-256
digest, so any dependency graph change requires an explicit policy review. Direct dependency
directions remain enforced separately by `scripts/validate-project-dependencies.py`.

Repository-local .NET tools use exact versions with roll-forward disabled. Container SDK and
runtime bases use immutable manifest digests. Every third-party GitHub Action is pinned to its
full commit SHA; the adjacent version comment is informational and never controls execution.
Dependabot proposes reviewed updates for Actions and Docker inputs.

Pull requests run GitHub dependency review with high-severity vulnerability blocking and the
repository license denylist. CI also queries NuGet advisories for every production project,
including transitive dependencies. The machine-readable reports are retained as workflow
artifacts. The release workflow repeats this scan rather than trusting a previous workflow run.
Before scanning, both workflows run `dotnet restore Nao.slnx` to generate the NuGet assets files;
`dotnet paket restore` alone does not generate them. Scanner output is also written to the workflow
log, and scanner errors block the gate even when a report contains no vulnerability findings.

## Vulnerability response

| Severity | Release action | Triage | Remediation target |
|---|---|---:|---:|
| Critical | Block | 4 hours | 24 hours |
| High | Block | 24 hours | 72 hours |
| Medium | Reviewed exception or fix | 72 hours | 30 days |
| Low | Track | 7 days | 90 days |

Critical and high findings in dependencies or images block publication. A scanner false positive
must be resolved by updating the source advisory, dependency, or machine-readable policy through
review; do not suppress scanner failure in a workflow. A time-bounded medium exception must record
the advisory, affected artifact, exploitability analysis, owner, expiry, and compensating control
in the release notes and issue tracker.

Privately reported vulnerabilities should identify affected package/image versions, impact, and a
minimal reproduction without customer data or credentials. The owner triages against the current
main branch, the latest signed package set, and published image digests. Remediation includes a
regression test, renewed scan, new SBOM/provenance, and revocation or deprecation guidance for
affected artifacts.

## Supported versions

Nao has no stable release. Only the current main branch is supported. The policy names
`1.0.0-alpha1` as the current release target, but prerelease packages are evaluation artifacts
without compatibility or support guarantees. Security fixes are not backported to unpublished
interfaces or abstractions.

## Licenses

The policy contains explicit SPDX allow and deny lists. Dependency review blocks newly introduced
denied licenses. The release SBOM records declared license data for shipped artifacts and their
resolved dependency graph. Missing or conflicting license information requires review before
publication; repository ownership and a package being free of charge are not license evidence.

Strong-copyleft and source-available licenses in the denylist are not accepted into distributed
artifacts without an explicit policy redesign and legal review. External services and executables
that Nao does not redistribute remain subject to their operators' license obligations.

## Native and external dependency review

| Dependency | Boundary | Distribution | Review focus |
|---|---|---|---|
| libc | Worker host system library | Supplied by the digest-pinned Linux runtime image | Narrow interop surface, Linux identity and Landlock behavior, container isolation regression tests |
| Z3 | Optional external process | Host-supplied; not redistributed | Versioned protocol, engine identity, malformed result rejection, cancellation, process isolation |
| PostgreSQL | Optional external service | Operator-supplied; not redistributed | Supported server version, TLS, patching, owner isolation, backup/restore, extension provenance |

Document parsing and RDF dependencies currently execute as managed NuGet libraries. If a parser,
reasoner, database adapter, or provider begins shipping a native asset, update the native inventory,
threat model, image SBOM, platform matrix, and isolation tests in the same change.

## Package publication

The NuGet workflow uses the protected `nuget` environment and OIDC trusted publishing. It:

1. validates reviewed release metadata, policy, and advisories;
2. builds and runs compatibility, migration, evaluation, security, performance, and complete-suite gates;
3. derives the package version from the manifest and verifies the protected tag;
4. packs libraries and portable symbol packages with repository and source-link metadata;
5. verifies package contents and installation into a clean consumer;
6. signs every `.nupkg` with the protected code-signing certificate and trusted timestamp;
7. generates SHA-256 checksums and an SPDX JSON SBOM;
8. creates GitHub build-provenance and SBOM attestations;
9. publishes through NuGet trusted publishing;
10. retains signed packages and evidence as a workflow artifact.

The environment must restrict reviewers, tag patterns, and access to
`NUGET_SIGNING_CERTIFICATE_BASE64` and `NUGET_SIGNING_CERTIFICATE_PASSWORD`. Certificate rotation
must overlap trust long enough to verify existing packages; never replace a package at the same
version.

Verify a downloaded package before use:

```bash
dotnet nuget verify --all Nao.Agents.1.0.0-alpha1.nupkg
gh attestation verify Nao.Agents.1.0.0-alpha1.nupkg --repo jichang/Nao
```

Compare its SHA-256 digest with the release evidence and inspect the SPDX SBOM before promotion.

## Container publication

The container workflow uses the protected `container-registry` environment. Each worker image is
built first for local scanning; high and critical findings block the multi-architecture publish.
Published images use immutable content digests, include BuildKit SBOM and maximum provenance, and
receive a GitHub registry attestation.

Verify by digest rather than mutable tag:

```bash
gh attestation verify \
  oci://ghcr.io/jichang/nao-agents-worker@sha256:DIGEST \
  --repo jichang/Nao
```

Production deployment values accept only image digests. Signature or attestation verification is
mandatory before worker execution, as described in
[Container images and Kubernetes orchestration](container-orchestration.md).

## Release review

Release notes must identify:

- exact source commit and tag;
- signed package and image digests;
- SBOM and provenance locations;
- vulnerability and license scan results;
- dependency-lock digest;
- native dependency changes;
- signing identity and certificate rotation, when applicable;
- supported-version and vulnerability-policy changes.

If any evidence is missing, inconsistent, unsigned, or produced from another commit, do not
publish or promote the artifact.

Rollback, package deprecation, unlisting, partial-publication handling, release channels, and compatibility baselines are defined in the [release and compatibility process](release-process.md).
