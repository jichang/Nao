# Project guidance

## Unreleased project: prioritize the right design

Nao has not been released. Backward compatibility is not a requirement.

- Prefer the most suitable design and implementation over preserving existing abstractions, interfaces, schemas, or protocols.
- Make breaking changes whenever they improve the design. Update all affected implementations, callers, tests, examples, and documentation together.
- Do not retain legacy APIs, compatibility constructors, aliases, adapters, optional fields, fallback behavior, or migration machinery solely for backward compatibility.
- Remove obsolete abstractions rather than layering new behavior around them for compatibility.
- Preserve correctness, security boundaries, and intended product behavior; permission to break interfaces is not permission to weaken these guarantees.
- Keep validation and protocol checks that serve correctness or security, independently of compatibility concerns.
- Keep Nao-owned protocol and schema markers at v1 while the project is unreleased; change their current shape in place rather than inventing earlier released versions. Use 1.0.0 for Nao package metadata. External standards, dependency versions, host-supplied version identifiers, and deliberate unsupported-version test fixtures are not governed by this rule.

Apply this guidance until the project owner explicitly changes the release or compatibility policy.
