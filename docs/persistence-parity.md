# Persistence and query parity

Date: 2026-10-09

Bead: `jellyfin-38k.9`

The parity suite has two independent layers. `PersistenceParityTests` uses only
the published `JellyfinDbContext`, entity types, and provider interfaces. It
runs a stable SQLite baseline and compares its complete observable result with
two separately created PostgreSQL databases. `StockServerPersistenceParityTests`
installs the release ZIP into an integrity-pinned, unmodified Jellyfin 12.2
publish and exercises the internal repositories and services through public
HTTP APIs and a real NFO/STRM media scan. Neither layer references a Jellyfin
server project, internal type, copied repository implementation, or
`InternalsVisibleTo` host hook.

The public-model matrix covers users, permissions, preferences, display
preferences, base items and linked children, people and roles, item values,
user data, media streams, chapters, activities, API keys, devices, search-like
expressions, stable adjacent pages, ascending and descending nullable ordering,
date ranges, transactions, optimistic concurrency, bulk deletion, and repeated
UUID partitions. Its boundary values include mixed ASCII case, non-ASCII text,
literal percent, underscore and backslash characters, null values, 129-row page
boundaries, 1,025-row deletion, exact UTC timestamps, duplicate constraints,
UUID grouping/minimum/maximum aggregates, and composite text values over 10,000
UTF-16 code units.

Replacement and destructive-write coverage is transactional rather than merely
checking final rows. A failed stream/chapter replacement rolls back its prior
deletes, a successful replacement preserves another item's media graph, and a
1,025-item deletion retains the winning detached user-data row while removing
dependent item-value, people, stream, and chapter rows without touching their
survivor counterparts. Eight independent PostgreSQL contexts concurrently
persist API keys; token uniqueness and an intentionally failed mixed batch
prove atomic rollback.

The stock-server proof creates and configures users, authenticates a device,
creates and revokes an API key, installs and scans a movie library, reads people
and genres parsed from local NFO files, searches and pages through the Items
API, updates user data, shuts down normally, and then verifies the persisted
PostgreSQL graph directly through the public context. Failure diagnostics are
redacted and bounded; copied hosts and raw data remain inside an ownership-
marked ignored repository-local scratch root and are deleted by the C# harness.
The API layer also creates six API keys concurrently, revokes one, favorites
and deletes a scanned item, and verifies Jellyfin's detached user-data retention
and orphan cleanup while another scanned item's graph remains intact. It also
runs two concurrent metadata edits through the untouched host, assigning the
same previously unseen tag to different items. A test-only PostgreSQL trigger
widens the vulnerable insert window; both HTTP operations must succeed, one
canonical `ItemValues` row must remain, and both `ItemValuesMap` rows must point
to that row. A second forced race sends playback-start reports from two authenticated
sessions for one user and item. Both real `SaveUserData` calls must succeed, the
database must contain one complete last-writer-wins snapshot, and the user-data API
must return the same play count from Jellyfin's LRU cache.

The stock-server proof forces the test database's JIT cost threshold to zero,
then times the first real `GET /UserItems/Resume` request. The request must return
the expected empty Continue Watching page in under ten seconds. A separate real-
PostgreSQL test executes `SHOW jit` through a plugin-configured context and pins
the session value to `off`; together these checks prevent the 1,401-function JIT
compilation path from returning to synchronous requests.

## Intentional provider differences

PostgreSQL's ordinary text comparison is case-sensitive while SQLite `LIKE`
folds ASCII letters only. The plugin rewrites Jellyfin's public `EF.Functions.Like`
and people-prefix expression shapes to preserve SQLite's ASCII-only behavior,
including explicit escape characters. Non-ASCII case folding is deliberately
not added: for example `CAFÉ` does not match `%café%` on either tested path.

PostgreSQL sorts nulls last when ascending and first when descending, opposite
to Jellyfin's SQLite expectations. The query interceptor adds an explicit null
marker before the requested key so both directions match SQLite.

PostgreSQL 16 does not provide `min(uuid)` or `max(uuid)`, while unchanged
Jellyfin emits `min(uuid)` when collapsing presentation versions. The plugin's
third migration adds those two exact aggregates using provider-prefixed,
immutable transition functions. This is a database capability shim, not a host
query change, and is reversibly removed on downgrade.
Both aggregates declare their schema-qualified built-in UUID ordering operator,
which enables PostgreSQL's min/max index optimization. An actual parameterized
10,000-row `EXPLAIN` must contain the limit/index-only plan and no aggregate
node. Catalog tests also pin the transition/combine/final-function shape,
parallel safety, null behavior, owner and ACLs, public-schema operator
collisions, atomic name-collision failure, idempotent upgrade, downgrade, and
reinstall. Default `PUBLIC` execute privileges are revoked; the database owner
used by Jellyfin retains the required execution right.

PostgreSQL B-tree keys have a finite tuple size. Long logical text identities
are therefore represented by the plugin's exact chunk-interned surrogate keys;
the original text remains lossless and publicly queryable. This physical
representation is intentionally provider-specific, while uniqueness and
round-trip behavior match SQLite.
