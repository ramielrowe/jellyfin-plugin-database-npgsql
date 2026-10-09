# Vanilla model compatibility

Date: 2026-10-09

Bead: `jellyfin-38k.6`

The plugin replaces Jellyfin 12.2's EF model customizer through public EF Core
services. Its customizer runs after the unchanged host model, verifies the exact
audited shape, and refuses to continue when a property, key, or index has drifted.
A checked-in SHA-256 fingerprint of EF's deterministic long debug model provides
an independent golden drift guard.

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
- `INSERT ... ON CONFLICT ... RETURNING` canonicalizes every edge safely under
  concurrent transactions;
- an explicit terminal edge distinguishes prefixes and the empty string;
- physical unique indexes combine Jellyfin's bounded key members with the
  canonical terminal ID; and
- the original text remains unchanged in the owning table.

Distinct strings therefore remain distinct even if every optional hash or
accelerator collides. Exact duplicates converge on one terminal ID and are
rejected by the physical unique index. The implementation requires no extension,
superuser operation, advisory-lock correctness, or finite collision assumption.
Database triggers calculate IDs for EF writes and direct restore-style inserts.

The initial PostgreSQL migration must install `NpgsqlTextInterning.InstallSql` in
the same transaction which creates the owning tables, and must omit/drop the
three unsafe full-text database primary-key constraints. The EF logical keys are
deliberately retained in model metadata; migration review must not regenerate
those three constraints blindly. Trie nodes currently remain after the last
referencing value is deleted. This trades bounded storage growth for simple,
concurrency-safe identity and predictable restores; future garbage collection
must prove that it cannot remove a prefix used by another value.

## Query contract

`NpgsqlQueryExpressionInterceptor` is a public EF Core 10
`IQueryExpressionInterceptor`. It rewrites only expression shapes which an
unchanged Jellyfin 12.2 host actually supplies:

- `EF.Functions.Like(match, pattern)` becomes a mapped SQL function which folds
  ASCII `A-Z` only and evaluates the already-constructed pattern with an explicit
  backslash escape. `%` and `_` remain wildcards; `\%`, `\_`, and `\\` retain the
  literal behavior encoded in the expression.
- `People.Name.StartsWith(prefix)` reads the stored generated `NameLower` shadow
  column and ASCII-folds the prefix through a mapped public database function.
- ordering by a nullable key first adds an explicit null marker: ascending puts
  null first and descending puts null last, matching SQLite before applying the
  requested value direction. The same rewrite applies to `ThenBy`.

The interceptor does not attempt to recover intent absent from the LINQ tree. If
host code has already combined user data and wildcard syntax without escaping,
the resulting pattern is the contract. It does not rewrite arbitrary raw SQL or
provider-specific expressions.

## Index and type compatibility

Unbounded equality columns use PostgreSQL hash indexes. Each removed compound
B-tree has a named bounded-prefix replacement, and real PostgreSQL tests isolate
and `EXPLAIN` every replacement to prove it is planner-usable. Some former
covering/order behavior necessarily becomes a bounded-prefix scan plus filter or
sort; this is explicit rather than risking runtime B-tree overflow.

The discriminator is bounded at 511 characters, matching the existing provider
smoke contract. Other bounded identifiers, UTC timestamp converters, ordinal `C`
collations, UUID operations, and the generated ASCII-lower people name are tested
against PostgreSQL 16.6. Tests also cover long Unicode and backslashes, shared
trie prefixes, exact duplicates, simultaneous contexts, detached updates,
restore-style SQL, update/delete, nullable ordering, LIKE escaping, catalog
extension absence, model creation, and an untouched Jellyfin startup.

The full-host model test uses a separate test-only provider assembly and a wholly
disposable database to bridge the unfinished backup bead. It first lets an
untouched vanilla SQLite startup record Jellyfin's current code-migration history,
copies that history into the precreated PostgreSQL model, and then starts the same
untouched publish with the plugin. The release ZIP never contains the bridge;
the production provider continues to reject migration backup until the dedicated
backup implementation lands.
