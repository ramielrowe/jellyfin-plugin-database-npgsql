# Kubernetes plugin seed image

This directory publishes a small, **init-container-only** image containing the
exact seven files for plugin `0.1.0.0` and an atomic installer. It is intended
to run before the unmodified, qualified LinuxServer Jellyfin image. It is not a
Jellyfin server image and is not supported by LinuxServer.io or Jellyfin.

The `linux/amd64` image is published at:

```text
ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-seed
```

Release tags are the plugin version, `sha-<12-character-commit>`, and `latest`.
Resolve a release tag once and deploy its immutable digest. The first package
published to GHCR must be made public in the repository package settings; the
release workflow deliberately fails its anonymous-access check until it is.

## Deployment contract

[`deployment.yaml`](deployment.yaml) is an intentionally explicit starting
point. Replace both PVC names and the seed-image digest. It pins the only
qualified stock image: LinuxServer Jellyfin `12.2ubu2604-ls53`, Jellyfin ABI
`12.2.0.0`, on `amd64`. It uses one replica and `Recreate`; do not permit two
Jellyfin Pods or two seed writers to share this config volume.

Before the **first** Pod starts, the external config PVC must already contain a
valid `/config/database.xml` selecting `PLUGIN_PROVIDER`, plugin name
`Npgsql Database_0.1.0.0`, and assembly
`Jellyfin.Plugin.Database.Npgsql.dll`. Follow the
[operator guide](../../docs/operator-guide.md). The seed image does not create
that file, provision PostgreSQL, create a database or role, migrate SQLite, or
perform PostgreSQL backup/restore. PostgreSQL `16.6` must be provisioned
separately. Do not let Jellyfin take a first boot against an empty config PVC:
that can create SQLite state before the provider is selected.

The init process runs as numeric UID/GID `1000:1000`, with no passwd entry,
read-only root filesystem, no Linux capabilities, and only its memory-backed
`/tmp` plus the config PVC writable. Its UID/GID must equal LinuxServer's
`PUID`/`PGID`. It never changes ownership. The PVC must therefore already
be pre-provisioned and pre-owned by `1000:1000`, allow that identity to create
`/config/data/plugins`, and retain Unix modes and ownership. The reference Pod
deliberately does **not** set `fsGroup` or `fsGroupChangePolicy`: kubelet and
some CSI drivers may recursively rewrite ownership or permission bits before
the init container starts, which would violate the installer's fail-closed,
no-mutation contract. Arrange ownership out of band using the storage system's
provisioning controls. An ownership or mode mismatch in an existing plugin
directory is a hard error and is left untouched.

The installer validates immutable seed metadata, exact allowlist, file types,
and checksums. It rejects symbolic path components and conflicting existing
content, stages final `0750`/`0640` metadata inside the plugin root, and uses an
atomic no-clobber rename. Repeated starts with the exact directory are
idempotent. No code or packages are downloaded at runtime.

Upgrade and rollback must coordinate all three resources: the seed digest, the
stock Jellyfin digest, and `database.xml`. Keep old plugin directories outside
the active plugin root and read the
[installation lifecycle](../../docs/installation-lifecycle.md). A package-only
rollback after a successful schema migration is unsupported without separately
qualified PostgreSQL recovery.

## Local build and qualification

Run from the repository root:

```sh
docker buildx build --load --platform linux/amd64 \
  --file docker/kubernetes/Dockerfile \
  --tag jellyfin-npgsql-seed:test .
docker/kubernetes/smoke-test.sh jellyfin-npgsql-seed:test
```

The smoke test exercises empty, repeated, and reused volumes; numeric UIDs;
read-only-root operation; conflicting bytes, names, types, modes, owners, and
symlinks without mutation; then starts the qualified stock LinuxServer image
twice against PostgreSQL `16.6` and rejects SQLite fallback.
