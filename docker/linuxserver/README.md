# LinuxServer.io Jellyfin image with the Npgsql provider

This directory builds an **unofficial derivative** of
[`linuxserver/jellyfin`](https://github.com/linuxserver/docker-jellyfin) with
this repository's Npgsql database-provider plugin pre-installed. It is not an
image published or supported by LinuxServer.io or Jellyfin.

For Kubernetes, prefer the dedicated
[non-root init-container seed image](../kubernetes/README.md). It keeps the
stock LinuxServer image and plugin lifecycles separate while sharing this
image's exact installer, checksums, and fail-closed package policy.

The image is deliberately narrow: plugin `0.1.4.0`, Jellyfin database ABI
`12.2.0.0`, LinuxServer.io base `12.2ubu2604-ls53`, and PostgreSQL `16.6` are
the only supported combination. The Dockerfile pins both build and runtime
images by digest. Published images support `linux/amd64` only.

## Pulling the image

Images are published to:

```text
ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-linuxserver
```

A published release has three tags: the plugin version (for example `0.1.4.0`),
`sha-<12-character-commit>`, and `latest`. Registry tags can be moved by a
subsequent publish, so production deployments should resolve a tag once and
then pull by its immutable digest:

```sh
docker pull ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-linuxserver:0.1.4.0
docker image inspect --format '{{index .RepoDigests 0}}' \
  ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-linuxserver:0.1.4.0
docker pull ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-linuxserver@sha256:REPLACE_WITH_DIGEST
```

The first package published to GHCR must be made public in the repository's
package settings. The release workflow intentionally verifies anonymous pulls
after publishing and attestation; that check fails while the package is private.

## Configuration

The image preserves LinuxServer.io's normal `PUID`, `PGID`, and `TZ` interface.
Do not set Compose `user:` or pass Docker `--user`: the s6 initialization phase
must begin as root so LinuxServer.io can prepare `/config` and this image can
atomically seed the plugin with `abc` ownership.

```yaml
services:
  jellyfin:
    image: ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-linuxserver@sha256:REPLACE_WITH_DIGEST
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: Etc/UTC
    ports:
      - "8096:8096"
    volumes:
      - jellyfin-config:/config
      - /srv/media:/media:ro
      - ./secrets/jellyfin-pgpass:/run/secrets/jellyfin-pgpass:ro
      - ./secrets/postgresql-root.crt:/run/secrets/postgresql-root.crt:ro

volumes:
  jellyfin-config:
```

The image does **not** bundle or provision PostgreSQL, create a database or
role, create `database.xml`, select the custom provider, or migrate an existing
SQLite installation. Provision PostgreSQL `16.6` separately and follow the
repository [operator guide](../../docs/operator-guide.md) to create
`/config/database.xml`. Use secrets or a mode-`0600` passfile; do not place a
password in Compose environment variables. The configured `PluginName` must be
exactly `Npgsql Database_0.1.4.0`.

At each startup, an s6 oneshot runs after LinuxServer.io's Jellyfin config
initialization and before `init-config-end`. It verifies the embedded seed's
exact seven-file allowlist and checksums, then atomically creates:

```text
/config/data/plugins/Npgsql Database_0.1.4.0
```

Restarting with identical content and exact metadata is idempotent. The
directory must be mode `0750`, its files mode `0640`, and all must be owned by
`abc`. An existing same-name directory with mismatched ownership or mode, or
with missing, extra, non-regular, or different content, is a hard startup
error; the image never mutates or overwrites a conflict. This also means an
upgrade must use the matching image, plugin directory, and `database.xml`
together. Read the
[installation lifecycle](../../docs/installation-lifecycle.md) before upgrading
or rolling back: package-only downgrade after a successful schema migration is
unsupported without separately qualified database recovery.

## Local build and smoke test

Run from the repository root:

```sh
docker buildx build --load --platform linux/amd64 \
  --file docker/linuxserver/Dockerfile \
  --tag jellyfin-npgsql-linuxserver:test .
docker/linuxserver/smoke-test.sh jellyfin-npgsql-linuxserver:test
```

The smoke test starts the image with the pinned PostgreSQL `16.6` image,
requires Jellyfin's `/health` response to be exactly `Healthy`, proves the
canonical persisted plugin directory and its ownership/checksums, verifies
PostgreSQL schema and provider-specific objects, rejects SQLite fallback, and
recreates Jellyfin with the same config volume to prove idempotent persistence.
