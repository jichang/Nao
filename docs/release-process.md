# Release and compatibility process

Nao has no stable release. The only supported development line is `main`, and
development snapshots and prerelease packages have no compatibility guarantee.
Main always defaults to `1.0.0-dev`, which is never published. A pushed Git tag
such as `v1.0.0-alpha2` supplies the release version without a source-code version bump.

## Branches and release channels

- `main` is the single integration branch and the source of release tags.
- Pull requests are the review boundary. Release metadata, migrations, package changes, and policy changes are reviewed with the implementation they describe.
- Untagged CI outputs are development evidence only. They are not published to NuGet and are not supported packages.
- Alpha, beta, release-candidate, and stable publication require a canonical `v<SemVer>` Git tag. Branch dispatches and development-version tags are rejected.
- `release/current.json` selects `release/template.json` on main. Publication generates a versioned manifest and selects it only in the workflow checkout. Historical manifests remain immutable.
- Prerelease channels are evaluation artifacts and carry no compatibility or support guarantee.
- `1.0.0` is not an LTS line. Nao will not claim an LTS line until a later reviewed policy defines its support duration, backport ownership, and successor.
- Before the first release, breaking APIs and abstractions are changed in place. After a stable release, a release manifest must name the compatibility baseline and package validation must compare against it.

## Reviewed release metadata

The reviewed `release/template.json` and `docs/releases/template.md` own development
release content, compatibility baseline, LTS status, changelog entries, completed
roadmap capabilities, mandatory gates, and artifact requirements. They contain no
future release number. The tag owns the published package version; its prerelease
identifier determines the channel. Schema versions change only with their contracts.

`scripts/release.py validate` rejects metadata, release-note, supply-chain policy,
roadmap, and generated-changelog drift. Build defaults remain `1.0.0-dev` even when
validating generated release records.

Update generated changelog content with:

```bash
python3 scripts/release.py render
python3 scripts/release.py validate
```

Do not edit `CHANGELOG.md` directly.

After validating the tag, the publishing workflow runs `prepare-publication --tag`
to generate `release/<version>.json` and `docs/releases/<version>.md`, update the
checkout's policy and current pointer, and regenerate the changelog. An `Unreleased`
date becomes the current UTC date; an explicit ISO date is preserved. The command
validates reviewed metadata before writing, refuses to overwrite historical records,
and is idempotent for the same prepared tag. It does not repair drift or bypass gates.

These changes exist only in the publishing checkout: the workflow does not commit, push, or
modify the tag. A fresh run of a tag still marked `Unreleased` uses that run's UTC date.
Restore, build, and pack receive the same tag-derived MSBuild `Version`, so package
dependencies, assembly metadata, and scaffold references agree. The workflow retains
the finalized manifest, notes, policy, and changelog alongside the package evidence.
Package release-note links reference the reviewed template at the immutable Git tag;
the finalized, dated notes are retained in the workflow artifacts.
To reproduce the publication metadata checks locally:

```bash
python3 scripts/release.py version --tag v1.0.0-alpha3
python3 scripts/release.py prepare-publication --tag v1.0.0-alpha3
python3 scripts/release.py validate
python3 scripts/validate-release-docs.py --version "$(python3 scripts/release.py version)" --publishing
```

Run publication preparation only in a disposable checkout, not on the development
worktree. To release after the reviewed main commit is pushed:

```bash
git tag v1.0.0-alpha3
git push origin v1.0.0-alpha3
```

The tag triggers protected package and container publication. No release-version
edit on main is required.

## Protected release gates

Publication reruns evidence from the tagged commit and does not trust an earlier workflow:

1. compatibility and package validation;
2. durable migration and rollback tests;
3. fast evaluation release gates;
4. fail-closed security regressions and vulnerability policy;
5. deterministic performance/benchmark contract tests;
6. the complete build and test surface;
7. strict release and migration documentation;
8. package-content, symbols, source metadata, and clean-consumer installation checks.

The initial release has no supported predecessor, so its reviewed compatibility baseline is `null`. This is an explicit first-release decision, not a skipped check.

## Artifacts and evidence

Every packable library produces a `.nupkg` and `.snupkg` with repository metadata, package README, portable symbols, and source-link build metadata. The protected workflow authenticates publication through NuGet Trusted Publishing, writes `SHA256SUMS`, generates an SPDX SBOM, creates build-provenance and SBOM attestations, and retains the complete evidence set. Nao does not currently maintain a NuGet repository-signing certificate and does not claim package signatures.

Consumers verify the checksum, GitHub provenance, SBOM attestation, package identity, and immutable version before promotion. A clean temporary F# project installs `Nao.Extensions` and `Nao.Platform` from the produced package directory and compiles against their public APIs before publication.

## Rollback and package yanking

Published package bytes are immutable and never overwritten with `--skip-duplicate` as a recovery mechanism. If publication fails before NuGet push, discard the run and correct the reviewed source or metadata. If only part of the package set reaches NuGet, stop promotion, mark every pushed package version deprecated, and publish incident guidance; do not complete the partial set from a different commit.

For a defective published release:

1. stop deployment and promotion;
2. restore application and authoritative data using the release-specific rollback procedure;
3. deprecate affected NuGet packages with a replacement or withdrawal message;
4. revoke compromised publishing identities or attestations when applicable;
5. publish a new version from a reviewed fix with new checksums, SBOMs, provenance, and attestations.

NuGet unlisting is reserved for legal, security, or severe safety incidents because it harms reproducibility. Ordinary defects use deprecation plus a fixed release. Container tags may be withdrawn, but immutable digests remain in incident and release evidence.

## Release record

The versioned release notes are immutable after publication and record validation results, exact source/tag identity, package and image digests, checksums, SBOM and attestation locations, dependency-lock identity, security review, rollback limits, known limitations, and the completed roadmap capability IDs generated into the changelog.
