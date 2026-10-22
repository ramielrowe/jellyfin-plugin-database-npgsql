# Vanilla model compatibility

Date: 2026-10-09

Bead: `jellyfin-38k.6`

The plugin replaces Jellyfin 12.2's EF model customizer through public EF Core
services. Its customizer runs after the unchanged host model and verifies the
expected properties, keys, and indexes. A separate public EF finalized-model
convention hashes the complete normalized long metadata representation plus all
database functions, parameters, and sequences. The checked-in SHA-256 is enforced
in production while EF builds the model, before a connection or schema operation;
it is not merely a test assertion.

To update the golden contract, intentionally make the reviewed model change, run
`ModelCompatibilityTests`, and inspect the old/new metadata and the fingerprint
reported by the runtime guard. Update `NpgsqlFinalizedModelContract.ExpectedSha256`
and its fixed-value test only after reviewing the complete model change. A hash
change without that review is a release blocker.

## Exact unbounded-text identity

Jellyfin has three logical primary keys and two unique indexes containing text
which is not application-bounded. PostgreSQL cannot put an arbitrarily large text
value directly in a B-tree entry. A fixed digest alone is not exact, so the plugin
does not use one as identity.

EF continues to track the three primary-key entities by their original, complete
strings. This preserves normal `Add`, same-context identity, detached `Update`,
delete, and restore-like explicit insertion behavior. The PostgreSQL schema uses
an additional trigger-maintained surrogate for each unbounded member. The
surrogate is the terminal node in a canonical trie of UTF-8 chunks:

- every edge contains at most 128 bytes;
- `(ParentId, Chunk, IsTerminal)` has an ordinary bounded unique constraint;
- `INSERT ... ON CONFLICT DO NOTHING RETURNING`, followed by an immutable-row
  lookup on conflict, canonicalizes every edge without a no-op update lock;
- an explicit terminal edge distinguishes prefixes and the empty string;
- physical unique indexes combine Jellyfin's bounded key members with the
  canonical terminal ID; and
- the original text remains unchanged in the owning table.

Distinct strings therefore remain distinct even if every optional hash or
accelerator collides. Exact duplicates converge on one terminal ID and are
rejected by the physical unique index. The implementation requires no extension,
superuser operation, advisory-lock correctness, or finite collision assumption.
Database triggers calculate IDs for EF writes and direct restore-style inserts.
They run on every update, even when only `InternId` was targeted, so direct SQL
cannot persist a forged or stale trigger-owned identity.

The exactness boundary is every value PostgreSQL can represent as valid UTF-8
`text`. PostgreSQL/Npgsql reject NUL and invalid UTF-16 surrogate input safely
before an owning row is stored. Unicode is not normalized: canonically equivalent
but byte-distinct forms remain distinct, matching ordinal Jellyfin key identity.

PostgreSQL may deadlock transactions which discover multiple entirely-new values
in inverse order. The provider's bounded execution strategy retries whole EF
operations for SQLSTATE `40P01` and `40001` at most five times with a maximum
two-second delay. EF implicit `SaveChanges` transactions are replayed as a unit.
Jellyfin core also starts explicit transactions in several code migrations. The
plugin detects a caller-owned transaction and disables retries for its lifetime,
so ordinary queries and `SaveChanges` remain compatible instead of failing EF's
stock retry-strategy transaction guard. A transient error aborts that explicit
transaction and is returned to its owner; individual commands are never replayed
inside it because doing so could duplicate effects.

Item persistence needs one narrower conflict-resolution path. Jellyfin 12.2's
`ItemPersistenceService.UpdateOrInsertItems` reads the existing `ItemValues`, then
adds each missing value and its `ItemValuesMap` rows inside a caller-owned
transaction. Concurrent scans can both observe a new tag as missing. Retrying the
losing `SaveChanges` would be incorrect: its tracker still contains a different
proposed `ItemValueId`, so its maps would not automatically reference the winning
row. `NpgsqlItemValueConcurrencyInterceptor` instead runs only when that explicit
transaction is already active. Before EF emits the tracked batch, it performs an
atomic `INSERT ... ON CONFLICT ... DO UPDATE RETURNING ItemValueId` for each mapped
Added `ItemValue`, marks an inserted winner unchanged, or replaces a loser's pending map
foreign keys with the returned canonical ID. EF then saves the maps and all other
changes in the original transaction. The physical
`IX_ItemValues_Type_ValueInternId` constraint remains the uniqueness authority;
the interceptor neither retries `SaveChanges` nor suppresses an unknown constraint
failure. Added values are resolved in stable type/value/ID order to reduce
multi-value lock inversions. Added values without a new map stay on EF's ordinary
constraint path, so an unrelated explicit duplicate insert is still rejected.

`UserDataManager.SaveUserData` has the same read-before-insert window for
`(ItemId, UserId, CustomDataKey)`, including the playback-start path. Its conflict
semantics differ from item-value interning: a user-data write is a complete
last-writer-wins playback snapshot, not a delta, and the host updates its LRU cache
only after committing. `NpgsqlUserDataConcurrencyInterceptor` therefore atomically
inserts or updates every non-key `UserData` field for Added entries inside the
existing caller-owned transaction, returns the trigger-owned intern ID, and marks
the tracked entry unchanged. The host then follows its normal commit, cache
`AddOrUpdate`, item-row rehydration, and event sequence. It never observes a false
successful insert and never caches before the database commit. Modified entries
continue through EF's ordinary update path. The
`IX_UserData_ItemId_UserId_CustomDataKeyInternId` constraint remains the identity
authority, and no `SaveChanges` retry is introduced.

Item providers have a delete-then-insert window rather than a read-before-insert
one. For an existing item, `UpdateOrInsertItems` attaches the remapped item as
Modified, runs `ExecuteDelete` on its `BaseItemProviders`, and inserts the item's
current provider set through `SaveChanges`, all in the same caller-owned
transaction. When two metadata refreshes save the same item, the second delete
waits on the first transaction's deleted rows, resumes after that commit, and
cannot see the providers it inserted, so the second insert fails with 23505 and
the whole save rolls back. SQLite's single writer lock serializes the two
rewrites instead. `NpgsqlBaseItemProviderConcurrencyInterceptor` handles only
Added providers whose owning item is tracked as Modified in the same save. It
upserts each provider's `ProviderValue` in stable item/provider order, returns the
trigger-owned intern ID, and marks the entry unchanged. A new item's providers stay
on EF's ordinary path: inserting them before the item would violate their foreign
key, and concurrent creation of the same item already conflicts on `BaseItems`.
Other explicit provider inserts also keep ordinary duplicate rejection. If the
concurrent rewrites carry different provider *names*, the resolved row set is
their union for the names the later writer omitted, not the later writer's exact set as
under SQLite. The later writer's value always wins for every name it writes.
`IX_BaseItemProviders_ItemId_ProviderIdInternId` remains the identity authority.

The initial PostgreSQL migration must install `NpgsqlTextInterning.InstallSql` in
the same transaction which creates the owning tables, and must omit/drop the
three unsafe full-text database primary-key constraints. The EF logical keys are
deliberately retained in model metadata; migration review must not regenerate
those three constraints blindly. Trie nodes remain after the last referencing
value is deleted. The strict worst-case growth for a newly seen value is
`ceil(UTF8 byte length / 128) + 1` nodes (fewer when prefixes are shared), and
reinterning a value creates none. Operators should monitor the node count and
relation size as append-only database capacity. This release deliberately
provides no online garbage collection: deleting a shared prefix concurrently
cannot be made safe by a simple reference count. Reclaiming nodes requires a
future offline, migration-versioned rebuild which reconstructs every owning
`InternId` atomically; ad-hoc deletion is not a supported maintenance policy.

## Query contract

`NpgsqlQueryExpressionInterceptor` is a public EF Core 10
`IQueryExpressionInterceptor`. It rewrites only expression shapes which an
unchanged Jellyfin 12.2 host actually supplies:

- Three-argument `EF.Functions.Like(match, pattern)` becomes a mapped function
  which folds ASCII `A-Z` only and emits PostgreSQL `ESCAPE ''`, matching SQLite's
  lack of an implicit escape character. Four-argument `Like` remains distinct:
  the original pattern is parsed with the original case-sensitive one-character
  escape before literal content is ASCII-folded. The parsed tokens are emitted as
  a canonical backslash-escaped PostgreSQL pattern, so a folded literal can never
  become an escape or wildcard. Empty or multi-character explicit escapes fail as
  they do on SQLite. `%` and `_` remain wildcards only when they were unescaped in
  the original pattern.
- `People.Name.StartsWith(prefix)` uses an indexable, `C`-collated 512-character
  generated ASCII prefix with `text_pattern_ops`. Its accelerator is emitted as a
  no-escape `LIKE` predicate (rather than Npgsql's parameterized `left()` rewrite),
  then the complete folded name predicate is applied. Long names/prefixes retain
  exact semantics without risking PostgreSQL's B-tree entry limit.
- ordering by a nullable key first adds an explicit null marker: ascending puts
  null first and descending puts null last, matching SQLite before applying the
  requested value direction. The same rewrite applies to `ThenBy`.

The interceptor does not attempt to recover intent absent from the LINQ tree. If
host code has already combined user data and wildcard syntax without escaping,
the resulting pattern is the contract. It does not rewrite arbitrary raw SQL or
provider-specific expressions.

## Index and type compatibility

Unbounded equality columns use PostgreSQL hash indexes. `ItemValue.Value` has a
hash equality accelerator alongside the bounded `Type` B-tree. Each removed
compound B-tree has a named bounded-prefix replacement. Real PostgreSQL tests
isolate and `EXPLAIN` every replacement to prove it is planner-usable. Some former
covering/order behavior necessarily becomes a bounded-prefix scan plus filter or
sort; this is explicit rather than risking runtime B-tree overflow.

Plan proofs execute tagged LINQ first, capture the actual Npgsql command and typed
parameter values, and run `EXPLAIN` with clones of those parameters. They do not
depend on `enable_seqscan` or another global planner override. The fixture uses
production-shaped cardinalities and asserts the target index conditions, both
`ItemValue.Type` and `Value` participation, and all nullable ordering directions.

The discriminator is bounded at 511 characters, matching the existing provider
smoke contract. Other bounded identifiers, UTC timestamp converters, ordinal `C`
collations, UUID operations, and the generated ASCII-lower people name are tested
against PostgreSQL 16.6. Tests also cover long Unicode and backslashes, shared
trie prefixes, exact duplicates, simultaneous contexts, detached updates,
restore-style SQL, update/delete, nullable ordering, LIKE escaping, catalog
extension absence, model creation, and an untouched Jellyfin startup. Additional
tests force inverse-order whole-transaction races for both new and existing trie
nodes, compare parameterized LIKE expressions directly with SQLite, validate
127/128/129-byte and normalization-distinct keys for all five mappings, tamper
with trigger-owned IDs through raw SQL, verify exact append-only growth, and
EXPLAIN the actual intercepted LINQ for People prefix, ItemValue equality,
nullable ordering, and every replacement index.

A separate inverse-order test uses ordinary tracked `SaveChangesAsync` calls. A
test-only trigger deterministically creates one `40P01` deadlock after the real
interning trigger. Explicit per-session roles acquire opposing, database-namespaced
advisory locks, and a bounded `pg_locks` barrier proves both first locks are held
before either blocking request. Nontransactional global and per-role sequences
prove exactly one failed operation was replayed. A second trigger raises `40001`
once. Both saves complete through the provider's automatic execution strategy;
returned shadow IDs, tracker states, canonical node counts, and exact persisted
rows prove retries have no duplicate side effects.

Caller-owned transactions cannot use that retry path after PostgreSQL aborts a
deadlock. The current interning function therefore resolves committed trie hits
without a write lock, but takes a transaction-scoped `SHARE ROW EXCLUSIVE` lock
on `__JellyfinTextNodes` before creating a missing path. This serializes only
transactions introducing previously unseen strings, so inverse batches such as
provider IDs or person roles `A/B` and `B/A` cannot each retain one speculative
unique-index entry while waiting for the other. The owning Jellyfin operation is
never replayed, exact text uniqueness and trigger-owned intern IDs are unchanged,
and direct SQL uses the same protection as EF persistence.

This guarantee is scoped to Jellyfin's normal PostgreSQL transactions, which use
the server default `READ COMMITTED` isolation level. After a waiter acquires the
miss lock, its next statement can see the winner's committed trie nodes. A custom
deployment that raises these caller-owned operations to `REPEATABLE READ` or
`SERIALIZABLE` may retain an older snapshot and require a whole-operation retry;
the provider does not claim to make those unsupported host transactions retryable.

The regression uses Jellyfin's real `JellyfinDbContext`, entity model, generated
EF commands, explicit transaction boundary, and production database triggers. It
does not instantiate `PeopleRepository` or `ItemPersistenceService`: those live
in the host implementation assembly, outside the provider's packaged dependency
boundary, and add no database step between `SaveChanges` and these triggers. The
test first installs the predecessor function and requires one inverse-role writer
to fail with `40P01`, then installs the successor and requires both equivalent
writers to commit with shared canonical intern IDs. That old-versus-new proof is
at the exact layer changed by this provider migration.

`20261011031154_SerializeTextInterningMisses` replaces the function on existing
databases and reversibly restores its predecessor on downgrade. No Jellyfin host
patch or database-ABI change is required, but installing only the updated DLL is
insufficient: normal startup must apply this provider migration before concurrent
scanning resumes. Older plugin releases remain susceptible until upgraded.

The full-host model test now uses the production provider package against a
wholly disposable database. The provider's migration backup implementation lets
the untouched vanilla host apply the real baseline, and the test verifies the
resulting model and lifecycle without a bridge assembly.

The concurrent-item-value regression has two layers. A deterministic PostgreSQL
test holds two caller-owned transactions after both have read the same new tag as
missing, then proves both operations commit, exactly one `ItemValues` row exists,
and both `ItemValuesMap` rows reference it. The untouched-host parity test also
issues two concurrent metadata-edit persistence operations for different scanned
items. A test-only trigger widens the insert window, reproducing the released
0.1.1.0 failure deterministically while verifying the fixed provider through the
public Jellyfin API.

User-data coverage follows the same two-layer structure. The deterministic test
holds two explicit transactions after both existence checks report the same key
missing, then verifies one row containing one complete submitted playback snapshot
rather than a torn mixture. The untouched-host test authenticates two real sessions
for one user and issues concurrent `Sessions/Playing` requests for the same item.
A test-only trigger widens the insert window; both `OnPlaybackStart` calls must
succeed, one physical row must remain, and `UserItems/{id}/UserData` must return the
same play count as a direct no-tracking database read, proving cache consistency.

Provider coverage reproduces the production interleaving deterministically. The
first rewrite deletes the item's seeded providers and pauses. The second rewrite's
delete starts, and the test waits until `pg_stat_activity` reports that backend
blocked on a lock before it releases the first. Both saves must commit, and exactly the
second writer's providers must remain. A duplicate explicit insert outside the
rewrite path must still fail. With the interceptor unregistered, the same test fails
with the production 23505 on `IX_BaseItemProviders_ItemId_ProviderIdInternId`.

No Jellyfin host patch or schema migration is required: the resolvers are provider
runtime behavior and remain pinned to the existing Jellyfin database ABI
12.2.0.0. The already-published 0.1.1.0 binary contains none of them, and 0.1.3.0
lacks the provider resolver; both remain affected. The fixes ship as plugin
0.1.4.0; select its new versioned installation directory rather than overwriting
an installed older directory in place. The normal package/host compatibility and
rollback rules still apply.

The checked-in migration reproduces this finalized model exactly. It installs
both LIKE forms and the bounded-prefix function, the miss-serialized interning
functions and unconditional triggers, the People `text_pattern_ops` and
ItemValue accelerators, omits the three unsafe physical primary keys, and
verifies the runtime fingerprint before mutation. Migration snapshots are used
only by Jellyfin's automatic failing-migration rollback and are deleted after a
successful migration; they are not operator restore media.
