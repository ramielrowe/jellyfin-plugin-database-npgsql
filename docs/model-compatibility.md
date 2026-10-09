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
Code which starts an explicit transaction must put the complete transaction body
inside `Database.CreateExecutionStrategy().Execute[Async]`; retrying individual
commands is unsupported and could duplicate effects.

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
  match, pattern, and its one-character explicit escape are folded consistently,
  including uppercase ASCII escape markers. Empty or multi-character explicit
  escapes fail as they do on SQLite. `%` and `_` remain wildcards.
- `People.Name.StartsWith(prefix)` uses an indexable, `C`-collated 512-character
  generated ASCII prefix with `text_pattern_ops`, then applies the complete folded
  name predicate. Long names/prefixes retain exact semantics without risking
  PostgreSQL's B-tree entry limit.
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

The full-host model test uses a separate test-only provider assembly and a wholly
disposable database to bridge the unfinished backup bead. It first lets an
untouched vanilla SQLite startup record Jellyfin's current code-migration history,
copies that history into the precreated PostgreSQL model, and then starts the same
untouched publish with the plugin. The release ZIP never contains the bridge;
the production provider continues to reject migration backup until the dedicated
backup implementation lands.

Bead `.7` owns migration generation. Its migration must reproduce this finalized
model exactly, install both LIKE forms and the bounded-prefix function, install
the non-locking interning functions and unconditional triggers, create the People
`text_pattern_ops` and ItemValue accelerators, omit the three unsafe physical
primary keys, and verify the runtime fingerprint before applying any mutation.
