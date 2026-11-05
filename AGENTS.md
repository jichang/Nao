# Project guidance

## Unreleased project: prioritize the right design

Nao has not been released. Backward compatibility is not a requirement.

- Prefer the most suitable design and implementation over preserving existing abstractions, interfaces, schemas, or protocols.
- Make breaking changes whenever they improve the design. Update all affected implementations, callers, tests, examples, and documentation together.
- Do not retain legacy APIs, compatibility constructors, aliases, adapters, optional fields, fallback behavior, or migration machinery solely for backward compatibility.
- Remove obsolete abstractions rather than layering new behavior around them for compatibility.
- Preserve correctness, security boundaries, and intended product behavior; permission to break interfaces is not permission to weaken these guarantees.
- Keep validation and protocol checks that serve correctness or security, independently of compatibility concerns.
- Version each schema or protocol independently when its contract changes; unchanged formats keep their versions regardless of package releases.
- Keep the default package version on `main` at `1.0.0-dev`, never published. Release versions come only from validated `v<SemVer>` Git tags in publishing workflows, not source-code version bumps. Generate release records from the reviewed development templates in the publishing checkout.

Apply this guidance until the project owner explicitly changes the release or compatibility policy.
