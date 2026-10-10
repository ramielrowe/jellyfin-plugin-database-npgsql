#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

[[ $# -eq 2 ]] || {
    printf 'usage: build-seed.sh PLUGIN_VERSION INSTALLATION_DIRECTORY\n' >&2
    exit 2
}

version=$1
installation_directory=$2
allowlist=docker/linuxserver/package-files.txt
package=artifacts/package
seed=/out/seed
expected=$(mktemp)
actual=$(mktemp)

cleanup() {
    rm -f -- "$expected" "$actual"
}
trap cleanup EXIT

[[ -n "$version" && "$version" != */* ]]
! printf '%s' "$version" | grep -q '[[:cntrl:]]'
[[ -n "$installation_directory" && "$installation_directory" != . && "$installation_directory" != .. ]]
[[ "$installation_directory" != */* ]]
! printf '%s' "$installation_directory" | grep -q '[[:cntrl:]]'

policy_version=$(sed -n 's/^  "version":[[:space:]]*"\([^"[:cntrl:]]*\)",[[:space:]]*$/\1/p' build/release-policy.json)
policy_installation_directory=$(sed -n 's/^  "installationDirectory":[[:space:]]*"\([^"[:cntrl:]]*\)",[[:space:]]*$/\1/p' build/release-policy.json)
checksum_file=$(sed -n 's/^  "checksumFile":[[:space:]]*"\([^"[:cntrl:]]*\)",[[:space:]]*$/\1/p' build/release-policy.json)
[[ "$version" == "$policy_version" ]]
[[ "$installation_directory" == "$policy_installation_directory" ]]
[[ -n "$checksum_file" && "$checksum_file" != */* ]]

sort "$allowlist" > "$expected"
cmp -s "$allowlist" "$expected"
[[ -s "$expected" ]]
while IFS= read -r name; do
    [[ -n "$name" && "$name" != . && "$name" != .. ]]
    [[ "$name" != */* ]]
    ! printf '%s' "$name" | grep -q '[[:cntrl:]]'
done < "$expected"

find "$package" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | sort > "$actual"
cmp -s "$expected" "$actual"
[[ -z $(find "$package" -mindepth 1 -maxdepth 1 ! -type f -print -quit) ]]
(cd artifacts && sha256sum -c -- "$checksum_file")

mkdir -p "$seed/package"
while IFS= read -r name; do
    cp -- "$package/$name" "$seed/package/$name"
done < "$expected"
cp -- "$allowlist" "$seed/package-files.txt"
(cd "$seed/package" && sha256sum -- *) > "$seed/SHA256SUMS"
printf '%s\n' "$version" > "$seed/plugin-version"
printf '%s\n' "$installation_directory" > "$seed/installation-directory"
chmod 0444 "$seed"/package/* "$seed/package-files.txt" "$seed/SHA256SUMS" \
    "$seed/plugin-version" "$seed/installation-directory"
