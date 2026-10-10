#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
umask 0077

fail() {
    printf 'install-jellyfin-npgsql: %s\n' "$1" >&2
    exit 1
}

[[ $# -eq 2 || $# -eq 3 ]] || fail 'usage: install-plugin.sh SEED PLUGIN_ROOT [OWNER[:GROUP]]'
seed=${1%/}
plugin_root=${2%/}
owner=${3-}
[[ -n "$seed" && -n "$plugin_root" ]] || fail 'seed and plugin root must be non-empty'
[[ "$seed" == /* && "$plugin_root" == /* ]] || fail 'seed and plugin root must be absolute paths'
[[ "$seed" != *//* && "$plugin_root" != *//* ]] || fail 'paths must not contain empty components'

for command in bash chmod chown cmp find grep id install mkdir mktemp mv rm sed sha256sum sort stat wc; do
    command -v "$command" >/dev/null 2>&1 || fail "required GNU userspace command is unavailable: $command"
done

expected=$(mktemp)
actual=$(mktemp)
manifest_names=$(mktemp)
stage=
stage_owner_reference=

cleanup() {
    rm -f -- "$expected" "$actual" "$manifest_names"
    if [[ -n "$stage_owner_reference" ]]; then
        rm -f -- "$stage_owner_reference"
    fi
    if [[ -n "$stage" && -d "$stage" && ! -L "$stage" ]]; then
        rm -rf -- "$stage"
    fi
}
trap cleanup EXIT

# Check every existing component instead of trusting a final-path lstat. This
# prevents either the immutable seed or the persistent-volume path from
# traversing a pre-existing symlink. The checks are repeated after creation.
reject_symlink_components() {
    local path=$1 current= component
    [[ "$path" == /* ]] || fail 'internal path validation requires an absolute path'
    IFS='/' read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        [[ -n "$component" && "$component" != . && "$component" != .. ]] \
            || fail "path contains an unsafe component: $path"
        current="$current/$component"
        [[ ! -L "$current" ]] || fail "path contains a symbolic-link component: $path"
    done
}

ensure_directory_path() {
    local path=$1 current= component
    reject_symlink_components "$path"
    IFS='/' read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        current="$current/$component"
        if [[ -e "$current" ]]; then
            [[ -d "$current" && ! -L "$current" ]] || fail "path component is not a directory: $path"
        else
            mkdir -- "$current" || fail "cannot create plugin root component: $current"
            chmod 0750 -- "$current" || fail "cannot set plugin root component mode: $current"
        fi
    done
    reject_symlink_components "$path"
}

safe_regular_file() {
    local path=$1 mode
    [[ -f "$path" && ! -L "$path" ]] || fail "file is missing, symbolic, or not regular: $path"
    mode=$(stat -c '%a' -- "$path") || fail "cannot inspect file metadata: $path"
    (( (8#$mode & 8#022) == 0 )) || fail "file is group- or other-writable: $path"
}

safe_directory() {
    local path=$1 mode
    [[ -d "$path" && ! -L "$path" ]] || fail "directory is missing, symbolic, or not a directory: $path"
    mode=$(stat -c '%a' -- "$path") || fail "cannot inspect directory metadata: $path"
    (( (8#$mode & 8#022) == 0 )) || fail "directory is group- or other-writable: $path"
}

read_single_line() {
    local path=$1 value
    safe_regular_file "$path"
    IFS= read -r value < "$path" || true
    [[ -n "$value" ]] || fail "metadata is empty: $path"
    [[ $(wc -l < "$path") -eq 1 ]] || fail "metadata must contain exactly one line: $path"
    printf '%s' "$value"
}

safe_leaf_name() {
    [[ $1 =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ && $1 != . && $1 != .. ]]
}

reject_symlink_components "$seed"
safe_directory "$seed"
printf '%s\n' SHA256SUMS installation-directory package package-files.txt plugin-version | sort > "$actual"
find "$seed" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort > "$manifest_names"
cmp -s "$actual" "$manifest_names" || fail 'seed root does not contain the exact metadata layout'
version=$(read_single_line "$seed/plugin-version")
installation_directory=$(read_single_line "$seed/installation-directory")
safe_leaf_name "$version" || fail 'plugin version contains an unsafe character'
[[ "$installation_directory" != . && "$installation_directory" != .. \
    && "$installation_directory" != */* && "$installation_directory" != *\\* ]] \
    || fail 'installation directory contains an unsafe character'
! printf '%s' "$installation_directory" | grep -q '[[:cntrl:]]' \
    || fail 'installation directory contains a control character'

safe_regular_file "$seed/package-files.txt"
safe_regular_file "$seed/SHA256SUMS"
sort "$seed/package-files.txt" > "$expected"
cmp -s "$seed/package-files.txt" "$expected" \
    || fail 'package allowlist is not sorted or contains duplicates'
[[ -s "$expected" ]] || fail 'package allowlist is empty'
while IFS= read -r name; do
    safe_leaf_name "$name" || fail 'package allowlist contains an unsafe name'
done < "$expected"

safe_directory "$seed/package"
find "$seed/package" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | sort > "$actual"
cmp -s "$expected" "$actual" || fail 'seed package does not match the exact allowlist'
[[ -z $(find "$seed/package" -mindepth 1 -maxdepth 1 ! -type f -print -quit) ]] \
    || fail 'seed package contains a non-regular entry'
while IFS= read -r name; do
    safe_regular_file "$seed/package/$name"
done < "$expected"

# Require canonical sha256sum lines. Names are constrained above, so escaping
# and ambiguous whitespace are forbidden and manifest/package equality is exact.
while IFS= read -r line; do
    [[ "$line" =~ ^[0-9a-f]{64}\ \ [A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
        || fail 'checksum manifest contains a malformed entry'
    printf '%s\n' "${line:66}"
done < "$seed/SHA256SUMS" | sort > "$manifest_names"
cmp -s "$expected" "$manifest_names" || fail 'checksum manifest does not match the package allowlist'
(cd "$seed/package" && sha256sum -c -- "$seed/SHA256SUMS") >/dev/null \
    || fail 'seed package checksum verification failed'

ensure_directory_path "$plugin_root"
destination="$plugin_root/$installation_directory"

validate_destination() {
    local expected_owner
    reject_symlink_components "$destination"
    [[ -d "$destination" && ! -L "$destination" ]] \
        || fail 'existing plugin destination is not a directory'
    find "$destination" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | sort > "$actual"
    cmp -s "$expected" "$actual" || fail 'existing plugin destination conflicts with this image'
    [[ -z $(find "$destination" -mindepth 1 -maxdepth 1 ! -type f -print -quit) ]] \
        || fail 'existing plugin destination contains a non-regular entry'
    [[ $(stat -c '%a' -- "$destination") == 750 ]] \
        || fail 'existing plugin destination has an unsafe mode'
    if [[ -n "$owner" ]]; then
        expected_owner=$(stat -c '%u:%g' -- "$stage_owner_reference")
    else
        expected_owner="$(id -u):$(id -g)"
    fi
    [[ $(stat -c '%u:%g' -- "$destination") == "$expected_owner" ]] \
        || fail 'existing plugin destination has unexpected ownership'
    while IFS= read -r name; do
        cmp -s -- "$seed/package/$name" "$destination/$name" \
            || fail 'existing plugin destination has different content'
        [[ $(stat -c '%a' -- "$destination/$name") == 640 ]] \
            || fail 'existing plugin destination has an unsafe file mode'
        [[ $(stat -c '%u:%g' -- "$destination/$name") == "$expected_owner" ]] \
            || fail 'existing plugin destination has unexpected file ownership'
    done < "$expected"
}

# Resolve a symbolic or numeric owner once without relying on /etc/passwd for
# numeric Kubernetes UIDs. A private reference inode makes validation exact.
stage_owner_reference=$(mktemp)
if [[ -n "$owner" ]]; then
    [[ "$owner" != *[[:cntrl:]]* && "$owner" != */* ]] || fail 'owner contains an unsafe character'
    chown -- "$owner" "$stage_owner_reference" || fail 'requested owner cannot be resolved or assigned'
fi
chmod 0600 -- "$stage_owner_reference"

if [[ -e "$destination" || -L "$destination" ]]; then
    validate_destination
else
    stage=$(mktemp -d -p "$plugin_root" .init-npgsql-plugin.XXXXXXXXXX)
    chmod 0750 -- "$stage"
    while IFS= read -r name; do
        install -m 0640 -- "$seed/package/$name" "$stage/$name"
    done < "$expected"
    if [[ -n "$owner" ]]; then
        chown -- "$owner" "$stage"
        while IFS= read -r name; do
            chown -- "$owner" "$stage/$name"
        done < "$expected"
    fi
    # Ensure metadata and bytes are final before atomic publication. Point the
    # validator at the staging path temporarily, then restore the destination.
    published_destination=$destination
    destination=$stage
    validate_destination
    destination=$published_destination
    # GNU mv --no-clobber never replaces a concurrent winner. It may report
    # success without moving, so always validate the final path afterward.
    mv -T --no-clobber -- "$stage" "$destination"
    validate_destination
    if [[ -d "$stage" && ! -L "$stage" ]]; then
        rm -rf -- "$stage"
    fi
    stage=
fi

rm -f -- "$stage_owner_reference"
stage_owner_reference=
printf 'install-jellyfin-npgsql: installed %s in %s\n' "$version" "$destination"
