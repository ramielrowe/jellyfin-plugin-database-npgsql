# Kubernetes plugin seed image

This directory publishes a small, **init-container-only** image containing the
exact seven files for plugin `0.1.2.0` and an atomic installer. It is intended
to run before the unmodified, qualified LinuxServer Jellyfin image. It is not a
Jellyfin server image and is not supported by LinuxServer.io or Jellyfin.

The `linux/amd64` image is published at:

```text
ghcr.io/ramielrowe/jellyfin-plugin-database-npgsql-seed
```

Release tags are the plugin version, `sha-<12-character-commit>`, and `latest`.
Registry tags can be moved by a subsequent publish, so resolve a release tag
once and deploy its immutable digest. The first package published to GHCR must
be made public in the repository package settings; the release workflow
deliberately fails its anonymous-access check until it is.

## Deployment contract

[`deployment.yaml`](deployment.yaml) is an intentionally explicit starting
point. Replace both PVC names and the seed-image digest. It pins the only
qualified stock image: LinuxServer Jellyfin `12.2ubu2604-ls53`, Jellyfin ABI
`12.2.0.0`, on `amd64`. It uses one replica and `Recreate`; do not permit two
Jellyfin Pods or two seed writers to share this config volume.

Before the **first** Pod starts, the external config PVC must already contain a
valid `/config/database.xml` selecting `PLUGIN_PROVIDER`, plugin name
`Npgsql Database_0.1.2.0`, and assembly
`Jellyfin.Plugin.Database.Npgsql.dll`. Follow the
[operator guide](../../docs/operator-guide.md). The seed image does not create
that file, provision PostgreSQL, create a database or role, migrate SQLite, or
perform PostgreSQL backup/restore. PostgreSQL `16.6` must be provisioned
separately. Do not let Jellyfin take a first boot against an empty config PVC:
that can create SQLite state before the provider is selected.

The init process runs as numeric UID/GID `1000:1000`, with no passwd entry,
read-only root filesystem, no Linux capabilities, and only its memory-backed
`/tmp` plus the config PVC writable. Its UID/GID must equal LinuxServer's
`PUID`/`PGID`. It never changes ownership. The PVC must therefore already be
pre-provisioned and pre-owned by `1000:1000`, allow that identity to create
`/config/data/plugins`, and retain Unix modes and ownership. An ownership or
mode mismatch in an existing plugin directory is a hard error and is left
untouched.

The reference Pod sets `fsGroup: 1000` so a native Secret projection can be
read without copying it. That setting is Pod-wide and can also rewrite the
config PVC. Pre-provision the PVC root with GID `1000`, the setgid bit, and at
least owner/group read, write, and search permissions (for example mode
`2770`), so `fsGroupChangePolicy: OnRootMismatch` skips recursive changes.
Confirm this behavior with the actual storage driver before production: a CSI
driver that handles `VOLUME_MOUNT_GROUP` itself ignores
`fsGroupChangePolicy`. If kubelet or the driver rewrites the installed plugin's
required `0750` directory and `0640` files, the seed installer fails closed.
The manifest also uses `supplementalGroupsPolicy: Strict`, which requires
Kubernetes 1.33 or later, to prevent image-defined supplementary groups from
silently widening access.

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

## PostgreSQL passfile Secret

Yes, a native Secret can be mounted directly without a passfile-copy init
container. The reference manifest uses the broadly available group-based
model:

- the Secret volume requests `defaultMode: 0400`;
- Pod `fsGroup: 1000` makes the projected file root-owned, group `1000`, and
  effectively mode `0440`;
- Jellyfin runs as UID/GID `1000:1000`; and
- `database.xml` explicitly opts into the plugin's private-group policy.

Create the Secret from a local mode-`0600` passfile rather than placing its
value in a checked-in manifest:

```sh
kubectl create secret generic jellyfin-postgres-passfile \
  --from-file=pgpass=/secure/local/path/jellyfin.pgpass \
  --from-file=root.crt=/secure/local/path/postgresql-root.crt
```

Replace `REPLACE_WITH_POSTGRES_PASSFILE_SECRET` in `deployment.yaml`, then use
the mounted path and policy in the pre-provisioned `database.xml`:

```xml
<ConnectionString>Host=postgres.example;Port=5432;Database=jellyfin;Username=jellyfin_runtime;Passfile=/run/secrets/postgresql/pgpass;SSL Mode=VerifyFull;Root Certificate=/run/secrets/postgresql/root.crt</ConnectionString>
<Options>
  <CustomDatabaseOption>
    <Key>passfile-permission-policy</Key>
    <Value>owner-or-group-read</Value>
  </CustomDatabaseOption>
</Options>
```

The opt-in policy accepts owner-only files as before. On Linux it additionally
accepts group read only when the opened file's GID is one of Jellyfin's actual
effective or supplementary groups. It still rejects group write, group
execute, every `other` bit, and group-readable TLS private keys. The permission
and GID are rechecked on every passfile open. This deliberately differs from
libpq's owner-only passfile convention, so use a dedicated `fsGroup`, mount the
Secret only into the Jellyfin container, keep `supplementalGroupsPolicy:
Strict`, and do not add sidecars to that group unless they are trusted with the
database credential.

Do not mount the individual key with `subPath`: Kubernetes does not propagate
Secret updates through a `subPath` mount. A normal Secret-volume update becomes
visible to subsequent passfile opens, although existing pooled PostgreSQL
connections continue using their established authentication until replaced.
Kubernetes Secret objects are only base64-encoded by default; protect Secret
API access with narrow RBAC and enable control-plane encryption at rest. Node
administrators and kubelet-level access remain inside the credential trust
boundary.

Kubernetes 1.37 offers a closer owner-only model through the alpha, disabled by
default `AtomicWriteVolumeUserFields` feature. When every relevant component
has that gate enabled, set `secret.defaultUser: 1000` with `defaultMode: 0400`,
remove the passfile policy option, and omit `fsGroup` if no other volume needs
it. This avoids group exposure and Pod-wide PVC permission changes, but an
alpha API is not the portable production recommendation yet. On older clusters
where the PVC cannot safely coexist with Pod-wide `fsGroup`, a Secret Store CSI
driver with verified UID support or the copy-to-`emptyDir` init-container model
remains necessary.

These recommendations follow Kubernetes' [Secret-volume and file-owner
contract](https://kubernetes.io/docs/concepts/storage/volumes/#secret), its
[documented `fsGroup` and `OnRootMismatch`
behavior](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/#configure-volume-permission-and-ownership-change-policy-for-pods),
and the kubelet's [read-only `0440` group-permission
mask](https://github.com/kubernetes/kubernetes/blob/v1.37.1/pkg/volume/volume_linux.go).
The opt-in is necessary because PostgreSQL's [standard passfile
rule](https://www.postgresql.org/docs/current/libpq-pgpass.html) rejects all
group and world access.

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
