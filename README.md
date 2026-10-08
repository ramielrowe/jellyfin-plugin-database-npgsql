# Jellyfin Npgsql database-provider feasibility probe

This directory contains the standalone feasibility work for loading an Npgsql-backed
`IJellyfinDatabaseProvider` into an unmodified Jellyfin server. It deliberately does
not modify or join the parent Jellyfin solution.

The probe targets the public Jellyfin 12.2 database ABI and carries Npgsql beside the
provider assembly. See `docs/feasibility-report.md` for measured results and
`tests/black-box-smoke.sh` for reproduction. The smoke test defaults to an exact
Jellyfin commit and digest-pinned PostgreSQL image; set `JELLYFIN_HOST_COMMIT` or
`POSTGRES_IMAGE` to run an explicit compatibility candidate.
