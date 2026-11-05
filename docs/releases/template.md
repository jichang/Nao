# Nao development release notes

- Release date: Unreleased
- Milestone: R4
- Owners: @jichang

## Summary

Reviewed development changes for the next Git-tagged Nao release. Untagged builds
use `1.0.0-dev` and are never published.

## Added

None

## Changed

Package versions come from validated release tags, not source-code version bumps.
Publication generates versioned release records from this template in its checkout.
Extension scaffolds reference the package version of the installed developer tools.

## Fixed

None

## Breaking changes

None

## Security

Existing fail-closed boundaries, protected publishing environments, and release
qualification gates remain required. Development versions cannot be published.

## Upgrade and rollback

Prereleases have no compatibility or support guarantee. Back up authoritative data
before evaluation and follow applicable schema migration guides. Package version
changes alone do not change schemas or require data migration.

## Validation

Publication requires the clean solution build, full supported test surface,
compatibility, migration, evaluation, security, performance, documentation,
package-content, and clean-consumer gates. This draft does not claim release qualification.

## Supply-chain evidence

The publishing workflow retains the generated versioned manifest, notes, changelog,
policy, packages, checksums, SPDX SBOMs, provenance, and attestations. No NuGet package
signatures are claimed.

## Known limitations

The [capability inventory](../capabilities.md) remains authoritative.
No stable compatibility, support, or multi-region guarantee is claimed.
