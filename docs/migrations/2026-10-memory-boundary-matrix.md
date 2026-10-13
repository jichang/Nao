# Memory authorization boundary matrix

## Tested dimensions

MEM-01 now has an executable identity matrix that varies each authorization dimension
independently:

- tenant;
- effective user;
- optional group;
- workspace;
- session.

The matrix uses valid host-authenticated scopes for every variant rather than malformed identity
fixtures.

## Boundary expectations

| Boundary | Required identity |
|---|---|
| Session-event eligibility | Exact tenant, user, group, workspace, and session that captured the event |
| Cross-session approval | Exact source session; destination is derived by removing only session |
| Background extraction | Exact approved tenant, user, group, and workspace with no session |
| Durable lifecycle mutation | Exact durable tenant, user, group, and workspace with no session |
| User-requested forgetting | Request and application use the same authenticated session; removing session must equal the durable owner |
| Federated recall | The complete invocation scope is forwarded independently to memory and knowledge capabilities |

Forgetting is deliberately available from a later session belonging to the same tenant, user,
group, and workspace because the durable memory is cross-session. A request captured in one session
cannot be applied under another session, and no non-session ownership dimension may change.

## Coverage

The matrix verifies:

- eligibility and consent approval reject each changed session identity dimension;
- extraction callbacks are not invoked for an incorrect durable owner;
- lifecycle operations reject changed tenant, user, group, workspace, or accidental session scope;
- same-owner later-session forgetting succeeds while cross-boundary forgetting fails;
- federated knowledge recall receives exactly the current invocation identity for every variant.

These tests complement store-level isolation tests and exact owner/version/value binding in the
memory-to-knowledge authority boundary.
